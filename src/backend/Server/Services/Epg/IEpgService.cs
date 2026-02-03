using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RtspRecServer.Shared;

namespace RtspRecServer.Server.Services.Epg
{
    /// <summary>
    /// EPG（电子节目单）服务接口
    /// </summary>
    public interface IEpgService
    {
        /// <summary>
        /// 获取所有频道
        /// </summary>
        Task<List<EpgChannel>> GetChannelsAsync();
        
        /// <summary>
        /// 获取频道节目单
        /// </summary>
        Task<List<EpgProgram>> GetChannelProgramsAsync(string channelId);
        
        /// <summary>
        /// 获取当前所有频道正在播放的节目
        /// </summary>
        Task<List<CurrentProgramInfo>> GetCurrentProgramsAsync();
        
        /// <summary>
        /// 手动刷新EPG数据
        /// </summary>
        Task RefreshEpgDataAsync(System.Threading.CancellationToken cancellationToken = default);
        
        /// <summary>
        /// 获取频道分类
        /// </summary>
        Task<List<string>> GetCategoriesAsync();
        
        /// <summary>
        /// 按分类获取频道
        /// </summary>
        Task<List<EpgChannel>> GetChannelsByCategoryAsync(string category);
        
        /// <summary>
        /// 获取最后更新时间
        /// </summary>
        Task<DateTime?> GetLastUpdateTimeAsync();
        
        /// <summary>
        /// 保存EPG数据
        /// </summary>
        Task SaveEpgDataAsync(EpgDataCache epgData);
    }
}