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
                    // 从配置中获取更新时间
                    var config = _configurationService.GetAppConfig();
                    var updateTimeStr = config.Epg?.UpdateTime ?? "08:00";
                    
                    if (!TimeSpan.TryParse(updateTimeStr, out var updateTime))
                    {
                        updateTime = TimeSpan.FromHours(8); // 默认8点
                    }

                    var now = DateTime.Now;
                    var nextUpdate = now.Date.Add(updateTime);
                    
                    if (now >= nextUpdate)
                    {
                        nextUpdate = nextUpdate.AddDays(1);
                    }

                    var delay = nextUpdate - now;
                    
                    _logger.LogInformation($"下次EPG数据自动更新时间: {nextUpdate:yyyy-MM-dd HH:mm:ss}，等待时间: {delay.TotalHours:F1}小时");

                    // 等待到下次更新时间
                    await Task.Delay(delay, stoppingToken);

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