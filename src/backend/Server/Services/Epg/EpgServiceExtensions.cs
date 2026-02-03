using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RtspRecServer.Server.Services.Epg;

namespace RtspRecServer.Server.Services.Epg
{
    /// <summary>
    /// EPG服务依赖注入扩展
    /// </summary>
    public static class EpgServiceExtensions
    {
        public static IServiceCollection AddEpgServices(this IServiceCollection services, IConfiguration configuration)
        {
            // 配置HttpClient用于EPG数据下载
            services.AddHttpClient<EpgUpdateBackgroundService>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(300); // 默认300秒
                client.DefaultRequestHeaders.Add("User-Agent", "RtspRecServer/1.0");
            });

            // 注册EPG相关服务
            services.AddSingleton<IXmlTvParserService, XmlTvParserService>();
            services.AddSingleton<EpgService>();
            services.AddSingleton<IEpgService>(provider => 
            {
                var inner = provider.GetRequiredService<EpgService>();
                var cache = provider.GetRequiredService<IMemoryCache>();
                var logger = provider.GetRequiredService<ILogger<CachedEpgService>>();
                return new CachedEpgService(inner, cache, logger);
            });
            services.AddSingleton<IEpgDataStorage>(provider => provider.GetRequiredService<EpgService>());

            // 注册后台更新服务
            services.AddHostedService<EpgUpdateBackgroundService>();

            // 配置内存缓存
            services.AddMemoryCache(options => 
            {
                // 不设置 SizeLimit，避免 System.InvalidOperationException: Cache entry must specify a value for Size when SizeLimit is set
            });

            return services;
        }
    }
}