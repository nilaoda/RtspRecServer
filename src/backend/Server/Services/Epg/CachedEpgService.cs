using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using RtspRecServer.Shared;

namespace RtspRecServer.Server.Services.Epg
{
    /// <summary>
    /// EPG服务代理，用于缓存和性能优化
    /// </summary>
    public class CachedEpgService : IEpgService
    {
        private readonly IEpgService _innerService;
        private readonly IMemoryCache _cache;
        private readonly ILogger<CachedEpgService> _logger;
        private readonly MemoryCacheEntryOptions _cacheOptions;

        public CachedEpgService(IEpgService innerService, IMemoryCache cache, ILogger<CachedEpgService> logger)
        {
            _innerService = innerService;
            _cache = cache;
            _logger = logger;
            
            // 设置缓存选项 - 频道信息缓存15分钟，节目信息缓存5分钟
            _cacheOptions = new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromMinutes(15),
                Priority = CacheItemPriority.Normal
                // 不设置 Size，因为 MemoryCache 没有设置 SizeLimit
            };
        }

        public async Task<List<EpgChannel>> GetChannelsAsync()
        {
            const string cacheKey = "epg_all_channels";
            
            if (_cache.TryGetValue(cacheKey, out List<EpgChannel>? cachedChannels) && cachedChannels != null)
            {
                _logger.LogDebug("从缓存获取频道列表");
                return cachedChannels;
            }

            var channels = await _innerService.GetChannelsAsync();
            
            // 缓存频道列表
            _cache.Set(cacheKey, channels, _cacheOptions);
            
            _logger.LogDebug($"缓存频道列表，共{channels.Count}个频道");
            return channels;
        }

        public async Task<List<EpgProgram>> GetChannelProgramsAsync(string channelId)
        {
            var cacheKey = $"epg_channel_programs_{channelId}";
            
            if (_cache.TryGetValue(cacheKey, out List<EpgProgram>? cachedPrograms) && cachedPrograms != null)
            {
                _logger.LogDebug($"从缓存获取频道节目单: {channelId}");
                return cachedPrograms;
            }

            var programs = await _innerService.GetChannelProgramsAsync(channelId);
            
            // 节目信息缓存时间较短（5分钟），因为节目单会频繁变化
            var programCacheOptions = new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromMinutes(5),
                Priority = CacheItemPriority.Normal
            };
            
            _cache.Set(cacheKey, programs, programCacheOptions);
            
            _logger.LogDebug($"缓存频道节目单: {channelId}，共{programs.Count}个节目");
            return programs;
        }

        public async Task<List<CurrentProgramInfo>> GetCurrentProgramsAsync()
        {
            // 当前播放的节目列表不使用缓存，因为它依赖于实时时间计算
            // 且内部服务的实现只是对内存中的列表进行过滤，性能开销极小
            return await _innerService.GetCurrentProgramsAsync();
        }

        public async Task RefreshEpgDataAsync(System.Threading.CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("开始刷新EPG数据");
            
            // 刷新数据时清除所有缓存
            ClearAllCache();
            
            await _innerService.RefreshEpgDataAsync(cancellationToken);
            
            _logger.LogInformation("EPG数据刷新完成");
        }

        public async Task SaveEpgDataAsync(EpgDataCache epgData)
        {
            // 保存数据时也清除缓存，确保下次获取的是最新数据
            ClearAllCache();
            await _innerService.SaveEpgDataAsync(epgData);
        }

        public async Task<List<string>> GetCategoriesAsync()
        {
            const string cacheKey = "epg_categories";
            
            if (_cache.TryGetValue(cacheKey, out List<string>? cachedCategories) && cachedCategories != null)
            {
                _logger.LogDebug("从缓存获取分类列表");
                return cachedCategories;
            }

            var categories = await _innerService.GetCategoriesAsync();
            
            _cache.Set(cacheKey, categories, _cacheOptions);
            
            _logger.LogDebug($"缓存分类列表，共{categories.Count}个分类");
            return categories;
        }

        public async Task<List<EpgChannel>> GetChannelsByCategoryAsync(string category)
        {
            var cacheKey = $"epg_channels_by_category_{category}";
            
            if (_cache.TryGetValue(cacheKey, out List<EpgChannel>? cachedChannels) && cachedChannels != null)
            {
                _logger.LogDebug($"从缓存获取分类频道: {category}");
                return cachedChannels;
            }

            var channels = await _innerService.GetChannelsByCategoryAsync(category);
            
            _cache.Set(cacheKey, channels, _cacheOptions);
            
            _logger.LogDebug($"缓存分类频道: {category}，共{channels.Count}个频道");
            return channels;
        }

        public async Task<DateTime?> GetLastUpdateTimeAsync()
        {
            // 最后更新时间不需要缓存，直接调用内部服务
            return await _innerService.GetLastUpdateTimeAsync();
        }

        private void ClearAllCache()
        {
            // 清除所有EPG相关的缓存
            var cacheKeys = new[]
            {
                "epg_all_channels",
                "epg_current_programs",
                "epg_categories"
            };

            foreach (var key in cacheKeys)
            {
                _cache.Remove(key);
            }

            // 清除频道节目单缓存（需要清除所有频道的缓存）
            if (_cache.TryGetValue("epg_all_channels", out List<EpgChannel>? channels) && channels != null)
            {
                foreach (var channel in channels)
                {
                    _cache.Remove($"epg_channel_programs_{channel.Id}");
                    foreach (var category in channel.Categories)
                    {
                        _cache.Remove($"epg_channels_by_category_{category}");
                    }
                }
            }

            _logger.LogInformation("已清除所有EPG缓存");
        }
    }
}