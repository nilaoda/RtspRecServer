using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RtspRecServer.Server.Services.Epg;
using RtspRecServer.Shared;

namespace RtspRecServer.Server.Services.Epg
{
    /// <summary>
    /// EPG API端点扩展
    /// </summary>
    public static class EpgApiEndpoints
    {
        public static void MapEpgEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapGet("/api/epg/channels", async (IEpgService epgService) =>
            {
                try
                {
                    var channels = await epgService.GetChannelsAsync();
                    return Results.Ok(channels);
                }
                catch (Exception ex)
                {
                    var logger = app.ServiceProvider.GetRequiredService<ILogger<IEpgService>>();
                    logger.LogError(ex, "获取频道列表失败");
                    return Results.Json(ApiResponse.Create("获取频道列表失败"), RtspJsonContext.Default.ApiResponse, statusCode: 500);
                }
            });

            app.MapGet("/api/epg/channels/{channelId}/programs", async (IEpgService epgService, string channelId) =>
            {
                try
                {
                    var programs = await epgService.GetChannelProgramsAsync(channelId);
                    return Results.Ok(programs);
                }
                catch (Exception ex)
                {
                    var logger = app.ServiceProvider.GetRequiredService<ILogger<IEpgService>>();
                    logger.LogError(ex, $"获取频道节目单失败: {channelId}");
                    return Results.Json(ApiResponse.Create("获取频道节目单失败"), RtspJsonContext.Default.ApiResponse, statusCode: 500);
                }
            });

            app.MapGet("/api/epg/current", async (IEpgService epgService) =>
            {
                try
                {
                    var currentPrograms = await epgService.GetCurrentProgramsAsync();
                    return Results.Ok(currentPrograms);
                }
                catch (Exception ex)
                {
                    var logger = app.ServiceProvider.GetRequiredService<ILogger<IEpgService>>();
                    logger.LogError(ex, "获取当前节目列表失败");
                    return Results.Json(ApiResponse.Create("获取当前节目列表失败"), RtspJsonContext.Default.ApiResponse, statusCode: 500);
                }
            });

            app.MapGet("/api/epg/categories", async (IEpgService epgService) =>
            {
                try
                {
                    var categories = await epgService.GetCategoriesAsync();
                    return Results.Ok(categories);
                }
                catch (Exception ex)
                {
                    var logger = app.ServiceProvider.GetRequiredService<ILogger<IEpgService>>();
                    logger.LogError(ex, "获取分类列表失败");
                    return Results.Json(ApiResponse.Create("获取分类列表失败"), RtspJsonContext.Default.ApiResponse, statusCode: 500);
                }
            });

            app.MapGet("/api/epg/categories/{category}/channels", async (IEpgService epgService, string category) =>
            {
                try
                {
                    var channels = await epgService.GetChannelsByCategoryAsync(category);
                    return Results.Ok(channels);
                }
                catch (Exception ex)
                {
                    var logger = app.ServiceProvider.GetRequiredService<ILogger<IEpgService>>();
                    logger.LogError(ex, $"获取分类频道失败: {category}");
                    return Results.Json(ApiResponse.Create("获取分类频道失败"), RtspJsonContext.Default.ApiResponse, statusCode: 500);
                }
            });

            app.MapPost("/api/epg/refresh", async (IEpgService epgService) =>
            {
                try
                {
                    await epgService.RefreshEpgDataAsync();
                    return Results.Ok(ApiResponse.Create("EPG数据刷新成功"));
                }
                catch (Exception ex)
                {
                    var logger = app.ServiceProvider.GetRequiredService<ILogger<IEpgService>>();
                    logger.LogError(ex, "EPG数据刷新失败");
                    return Results.Json(ApiResponse.Create("EPG数据刷新失败"), RtspJsonContext.Default.ApiResponse, statusCode: 500);
                }
            });

            app.MapGet("/api/epg/status", async (IEpgService epgService) =>
            {
                try
                {
                    var lastUpdateTime = await epgService.GetLastUpdateTimeAsync();
                    return Results.Ok(new EpgStatus { LastUpdate = lastUpdateTime });
                }
                catch (Exception ex)
                {
                    var logger = app.ServiceProvider.GetRequiredService<ILogger<IEpgService>>();
                    logger.LogError(ex, "获取EPG状态失败");
                    return Results.Json(ApiResponse.Create("获取EPG状态失败"), RtspJsonContext.Default.ApiResponse, statusCode: 500);
                }
            });
        }
    }
}