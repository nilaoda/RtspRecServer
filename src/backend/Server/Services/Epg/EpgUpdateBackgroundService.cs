using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RtspRecServer.Server.Services;
using RtspRecServer.Shared;

namespace RtspRecServer.Server.Services.Epg
{
    /// <summary>
    /// EPG更新后台服务
    /// </summary>
    public class EpgUpdateBackgroundService : BackgroundService
    {
        private readonly ILogger<EpgUpdateBackgroundService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly ConfigurationService _configurationService;
        private readonly HttpClient _httpClient;

        public EpgUpdateBackgroundService(
            ILogger<EpgUpdateBackgroundService> logger,
            IServiceProvider serviceProvider,
            ConfigurationService configurationService,
            HttpClient httpClient)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _configurationService = configurationService;
            _httpClient = httpClient;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("EPG更新后台服务启动");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // 分段等待到下次更新时间：期间修改更新时间可在约 1 分钟内生效，无需重启
                    var nextUpdate = GetNextUpdateTime(DateTime.Now);
                    _logger.LogInformation("下次EPG数据自动更新时间: {NextUpdate:yyyy-MM-dd HH:mm:ss}", nextUpdate);

                    while (!stoppingToken.IsCancellationRequested)
                    {
                        var remaining = nextUpdate - DateTime.Now;
                        if (remaining <= TimeSpan.Zero)
                        {
                            break;
                        }

                        var chunk = remaining > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : remaining;
                        await Task.Delay(chunk, stoppingToken);

                        if (DateTime.Now >= nextUpdate)
                        {
                            break;
                        }

                        var refreshed = GetNextUpdateTime(DateTime.Now);
                        if (refreshed != nextUpdate)
                        {
                            nextUpdate = refreshed;
                            _logger.LogInformation("EPG更新时间已调整为: {NextUpdate:yyyy-MM-dd HH:mm:ss}", nextUpdate);
                        }
                    }

                    // 执行更新
                    if (!stoppingToken.IsCancellationRequested)
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var epgService = scope.ServiceProvider.GetRequiredService<IEpgService>();
                        await epgService.RefreshEpgDataAsync(stoppingToken);
                    }
                }
                catch (TaskCanceledException)
                {
                    _logger.LogInformation("EPG更新后台服务被取消");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "EPG数据更新失败");
                    
                    // 失败后等待1小时再重试
                    try
                    {
                        await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                }
            }

            _logger.LogInformation("EPG更新后台服务停止");
        }

        private DateTime GetNextUpdateTime(DateTime now)
        {
            var config = _configurationService.GetAppConfig();
            var updateTimeStr = config.Epg?.UpdateTime ?? "08:00";
            if (!TimeSpan.TryParse(updateTimeStr, out var updateTime))
            {
                updateTime = TimeSpan.FromHours(8); // 默认8点
            }

            var next = now.Date.Add(updateTime);
            return now >= next ? next.AddDays(1) : next;
        }

        private async Task UpdateEpgDataAsync(CancellationToken cancellationToken)
        {
            // 此方法已被 RefreshEpgDataAsync 取代，但保留空实现以防编译错误（如果其他地方调用）
            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// EPG服务扩展接口（用于保存数据）
    /// </summary>
    public interface IEpgDataStorage
    {
        Task SaveEpgDataAsync(EpgDataCache epgData);
    }
}