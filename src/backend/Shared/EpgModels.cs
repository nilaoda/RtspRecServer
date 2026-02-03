using System;
using System.Collections.Generic;

namespace RtspRecServer.Shared
{
    /// <summary>
    /// EPG频道信息
    /// </summary>
    public class EpgChannel
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? IconUrl { get; set; }
        public List<string> Categories { get; set; } = new();
        public string? Language { get; set; }
        public string? Country { get; set; }
    }

    /// <summary>
    /// EPG节目信息
    /// </summary>
    public class EpgProgram
    {
        public string Id { get; set; } = string.Empty;
        public string ChannelId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? SubTitle { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public string? Category { get; set; }
        public List<string> Categories { get; set; } = new();
        public string? Language { get; set; }
        public string? IconUrl { get; set; }
        public int? EpisodeNumber { get; set; }
        public int? SeasonNumber { get; set; }
        public string? Rating { get; set; }
        public string? StarRating { get; set; }
        public bool IsPremiere { get; set; }
        public bool IsRepeat { get; set; }
        public bool IsSubtitled { get; set; }
        public bool IsHd { get; set; }
        public bool IsNew { get; set; }
    }

    /// <summary>
    /// 当前节目信息
    /// </summary>
    public class CurrentProgramInfo
    {
        public EpgChannel Channel { get; set; } = new();
        public EpgProgram CurrentProgram { get; set; } = new();
        public double Progress { get; set; } // 播放进度百分比
        public int RemainingMinutes { get; set; } // 剩余分钟数
    }

    /// <summary>
    /// EPG数据缓存
    /// </summary>
    public class EpgDataCache
    {
        public DateTime LastUpdateTime { get; set; }
        public List<EpgChannel> Channels { get; set; } = new();
        public Dictionary<string, List<EpgProgram>> ChannelPrograms { get; set; } = new();
    }

    /// <summary>
    /// EPG状态信息
    /// </summary>
    public class EpgStatus
    {
        public DateTime? LastUpdate { get; set; }
    }
}
