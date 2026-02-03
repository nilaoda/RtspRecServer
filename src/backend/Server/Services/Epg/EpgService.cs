using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using RtspRecServer.Server.Services;
using RtspRecServer.Shared;

namespace RtspRecServer.Server.Services.Epg
{
    public class EpgService : IEpgService, IEpgDataStorage
    {
        private readonly ILogger<EpgService> _logger;
        private readonly IMemoryCache _cache;
        private readonly ConfigurationService _configurationService;
        private readonly IXmlTvParserService _parserService;
        private EpgDataCache? _epgData;
        private readonly object _dataLock = new();
        private readonly System.Threading.SemaphoreSlim _updateLock = new(1, 1);
        private readonly string _cacheFilePath;

        public EpgService(
            ILogger<EpgService> logger,
            IMemoryCache cache,
            ConfigurationService configurationService,
            IXmlTvParserService parserService)
        {
            _logger = logger;
            _cache = cache;
            _configurationService = configurationService;
            _parserService = parserService;
            _cacheFilePath = Path.Combine(AppContext.BaseDirectory, "epg_cache.json");
            
            // 启动时立即尝试加载磁盘缓存
            LoadFromDisk();
        }

        private void LoadFromDisk()
        {
            if (_epgData != null) return;

            lock (_dataLock)
            {
                if (_epgData != null) return;

                if (File.Exists(_cacheFilePath))
                {
                    try
                    {
                        var json = File.ReadAllText(_cacheFilePath);
                        // 使用 Source Generator 提供的上下文进行反序列化，以支持 NativeAOT
                        _epgData = JsonSerializer.Deserialize(json, RtspJsonContext.Default.EpgDataCache);
                        
                        if (_epgData != null)
                        {
                            _logger.LogInformation("成功从磁盘加载 EPG 缓存数据: {ChannelCount} 个频道, 最后更新时间: {LastUpdate}", 
                                _epgData.Channels?.Count ?? 0, _epgData.LastUpdateTime);
                        }
                        else
                        {
                            _logger.LogWarning("从磁盘加载的 EPG 缓存数据为空 (文件路径: {Path})", _cacheFilePath);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "从磁盘加载 EPG 缓存失败 (文件路径: {Path})", _cacheFilePath);
                    }
                }
                else
                {
                    _logger.LogInformation("未发现 EPG 磁盘缓存文件 (路径: {Path})", _cacheFilePath);
                }
            }
        }

        private async Task EnsureDataLoadedAsync()
        {
            if (_epgData != null) return;
            
            LoadFromDisk();

            if (_epgData == null || _epgData.Channels.Count == 0)
            {
                _logger.LogInformation("首次加载或磁盘缓存为空，开始更新 EPG 数据");
                await RefreshEpgDataAsync();
            }
        }

        public async Task<List<EpgChannel>> GetChannelsAsync()
        {
            await EnsureDataLoadedAsync();
            return _epgData?.Channels ?? new List<EpgChannel>();
        }

        public async Task<List<EpgProgram>> GetChannelProgramsAsync(string channelId)
        {
            await EnsureDataLoadedAsync();
            if (_epgData?.ChannelPrograms?.TryGetValue(channelId, out var programs) == true)
            {
                return programs;
            }
            return new List<EpgProgram>();
        }

        public async Task<List<CurrentProgramInfo>> GetCurrentProgramsAsync()
        {
            await EnsureDataLoadedAsync();
            
            var currentPrograms = new List<CurrentProgramInfo>();
            var now = DateTime.Now;
            _logger.LogDebug("Getting current programs at {Now}", now);

            if (_epgData?.Channels == null || _epgData.ChannelPrograms == null)
            {
                return currentPrograms;
            }

            foreach (var channel in _epgData.Channels)
            {
                if (_epgData.ChannelPrograms.TryGetValue(channel.Id, out var programs))
                {
                    var currentProgram = programs.FirstOrDefault(p => 
                        p.StartTime <= now && p.EndTime > now);

                    if (currentProgram != null)
                    {
                        var totalMinutes = (currentProgram.EndTime - currentProgram.StartTime).TotalMinutes;
                        var elapsedMinutes = (now - currentProgram.StartTime).TotalMinutes;
                        var progress = totalMinutes > 0 ? Math.Min(100, Math.Max(0, (elapsedMinutes / totalMinutes) * 100)) : 0;
                        var remainingMinutes = Math.Max(0, (int)(currentProgram.EndTime - now).TotalMinutes);

                        currentPrograms.Add(new CurrentProgramInfo
                        {
                            Channel = channel,
                            CurrentProgram = currentProgram,
                            Progress = progress,
                            RemainingMinutes = remainingMinutes
                        });
                    }
                }
            }

            return currentPrograms;
        }

        public async Task RefreshEpgDataAsync(System.Threading.CancellationToken cancellationToken = default)
        {
            if (!_updateLock.Wait(0))
            {
                _logger.LogWarning("EPG 数据刷新已在进行中，跳过本次请求");
                return;
            }

            _logger.LogInformation("开始刷新 EPG 数据");
            
            try
            {
                var config = _configurationService.GetAppConfig();
                var epgUrl = config.Epg?.Url ?? "http://epg.51zmt.top:8000/e.xml.gz";
                
                using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                using var response = await httpClient.GetAsync(epgUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                Stream decompressedStream = stream;
                if (epgUrl.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                {
                    decompressedStream = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress);
                }

                try
                {
                    var epgData = await _parserService.ParseXmlTvAsync(decompressedStream);
                    await SaveEpgDataAsync(epgData);
                }
                finally
                {
                    if (decompressedStream != stream) await decompressedStream.DisposeAsync();
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("EPG 数据刷新已取消");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "刷新 EPG 数据失败");
                throw;
            }
            finally
            {
                _updateLock.Release();
            }
        }

        public async Task<List<string>> GetCategoriesAsync()
        {
            await EnsureDataLoadedAsync();
            var categories = new HashSet<string>();
            if (_epgData?.Channels != null)
            {
                foreach (var channel in _epgData.Channels)
                {
                    if (channel.Categories != null && channel.Categories.Count > 0)
                    {
                        foreach (var category in channel.Categories) categories.Add(category);
                    }
                }
            }

            // 如果从频道中没有提取到分类，尝试从节目中提取
            if (categories.Count == 0 && _epgData?.ChannelPrograms != null)
            {
                foreach (var programList in _epgData.ChannelPrograms.Values)
                {
                    foreach (var program in programList)
                    {
                        if (program.Categories != null)
                        {
                            foreach (var category in program.Categories) categories.Add(category);
                        }
                        if (!string.IsNullOrEmpty(program.Category))
                        {
                            categories.Add(program.Category);
                        }
                    }
                }
            }

            // 如果还是没有分类，返回一个默认分类
            if (categories.Count == 0 && _epgData?.Channels != null && _epgData.Channels.Count > 0)
            {
                categories.Add("未分类");
            }

            return categories.OrderBy(c => c).ToList();
        }

        public async Task<List<EpgChannel>> GetChannelsByCategoryAsync(string category)
        {
            await EnsureDataLoadedAsync();
            if (_epgData?.Channels == null) return new List<EpgChannel>();

            if (category == "未分类")
            {
                return _epgData.Channels
                    .Where(c => c.Categories == null || c.Categories.Count == 0)
                    .ToList();
            }

            return _epgData.Channels
                .Where(channel => channel.Categories != null && channel.Categories.Contains(category, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        public async Task<DateTime?> GetLastUpdateTimeAsync()
        {
            await EnsureDataLoadedAsync();
            return _epgData?.LastUpdateTime;
        }

        public async Task SaveEpgDataAsync(EpgDataCache epgData)
        {
            lock (_dataLock)
            {
                _epgData = epgData;
            }

            try
            {
                // 使用 Source Generator 提供的上下文进行序列化，以支持 NativeAOT
                var json = JsonSerializer.Serialize(epgData, RtspJsonContext.Default.EpgDataCache);
                await File.WriteAllTextAsync(_cacheFilePath, json);
                _logger.LogInformation("EPG 数据已保存到磁盘缓存");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存 EPG 缓存到磁盘失败");
            }

            // 清除内存中的业务缓存
            _cache.Remove("epg_all_channels");
            _cache.Remove("epg_categories");
            _cache.Remove("epg_current_programs");
        }
    }
}
