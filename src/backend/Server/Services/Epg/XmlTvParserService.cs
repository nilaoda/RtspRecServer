using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RtspRecServer.Shared;

namespace RtspRecServer.Server.Services.Epg
{
    /// <summary>
    /// XMLTV格式解析服务
    /// </summary>
    public interface IXmlTvParserService
    {
        Task<EpgDataCache> ParseXmlTvAsync(Stream xmlStream);
    }

    public class XmlTvParserService : IXmlTvParserService
    {
        private readonly ILogger<XmlTvParserService> _logger;

        public XmlTvParserService(ILogger<XmlTvParserService> logger)
        {
            _logger = logger;
        }

        public async Task<EpgDataCache> ParseXmlTvAsync(Stream xmlStream)
        {
            var channels = new List<EpgChannel>();
            var programs = new Dictionary<string, List<EpgProgram>>();
            var lastUpdateTime = DateTime.Now;

            using var reader = XmlReader.Create(xmlStream, new XmlReaderSettings 
            { 
                Async = true,
                IgnoreWhitespace = true,
                IgnoreComments = true,
                DtdProcessing = DtdProcessing.Parse
            });

            _logger.LogInformation("开始解析XMLTV数据");

            while (await reader.ReadAsync())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    switch (reader.LocalName.ToLower())
                    {
                        case "channel":
                            var channel = await ParseChannelAsync(reader);
                            if (channel != null)
                            {
                                channels.Add(channel);
                                if (!programs.ContainsKey(channel.Id))
                                {
                                    programs[channel.Id] = new List<EpgProgram>();
                                }
                            }
                            break;
                        case "programme":
                            var program = await ParseProgrammeAsync(reader);
                            if (program != null && !string.IsNullOrEmpty(program.ChannelId))
                            {
                                if (!programs.ContainsKey(program.ChannelId))
                                {
                                    programs[program.ChannelId] = new List<EpgProgram>();
                                }
                                programs[program.ChannelId].Add(program);
                            }
                            break;
                    }
                }
            }

            // 对每个频道的节目按时间排序，并提取频道分类
            foreach (var channel in channels)
            {
                if (programs.TryGetValue(channel.Id, out var channelPrograms))
                {
                    channelPrograms.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
                    
                    // 提取分类
                    var channelCategories = channelPrograms
                        .SelectMany(p => p.Categories ?? new List<string>())
                        .Where(c => !string.IsNullOrEmpty(c))
                        .Distinct()
                        .ToList();
                    
                    if (channel.Categories == null) channel.Categories = new List<string>();
                    
                    foreach (var cat in channelCategories)
                    {
                        if (!channel.Categories.Contains(cat))
                        {
                            channel.Categories.Add(cat);
                        }
                    }

                    // 检查单个 Category 属性
                    var singleCategories = channelPrograms
                        .Select(p => p.Category)
                        .Where(c => !string.IsNullOrEmpty(c))
                        .Distinct();

                    foreach (var cat in singleCategories)
                    {
                        if (cat != null && !channel.Categories.Contains(cat))
                        {
                            channel.Categories.Add(cat);
                        }
                    }
                }
            }

            _logger.LogInformation($"XMLTV解析完成，共{channels.Count}个频道，{programs.Sum(p => p.Value.Count)}个节目");

            return new EpgDataCache
            {
                LastUpdateTime = lastUpdateTime,
                Channels = channels,
                ChannelPrograms = programs
            };
        }

        private async Task<EpgChannel?> ParseChannelAsync(XmlReader reader)
        {
            try
            {
                var channelId = reader.GetAttribute("id") ?? string.Empty;
                var channel = new EpgChannel { Id = channelId };

                if (reader.IsEmptyElement)
                {
                    return string.IsNullOrEmpty(channel.Name) ? (string.IsNullOrEmpty(channel.Id) ? null : new EpgChannel { Id = channelId, Name = channelId }) : channel;
                }

                using var subReader = reader.ReadSubtree();
                while (await subReader.ReadAsync())
                {
                    if (subReader.NodeType == XmlNodeType.Element)
                    {
                        switch (subReader.LocalName.ToLower())
                        {
                            case "display-name":
                                var lang = subReader.GetAttribute("lang") ?? "zh";
                                var name = await subReader.ReadElementContentAsStringAsync();
                                if (string.IsNullOrEmpty(channel.Name) || lang == "zh")
                                {
                                    channel.Name = name.Trim();
                                    channel.Language = lang;
                                }
                                break;
                            case "icon":
                                var iconSrc = subReader.GetAttribute("src");
                                if (!string.IsNullOrEmpty(iconSrc))
                                {
                                    channel.IconUrl = iconSrc;
                                }
                                break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(channel.Name))
                {
                    channel.Name = channelId;
                }

                return string.IsNullOrEmpty(channel.Name) ? null : channel;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"解析频道信息失败: {reader.GetAttribute("id")}");
                return null;
            }
        }

        private async Task<EpgProgram?> ParseProgrammeAsync(XmlReader reader)
        {
            try
            {
                var channelId = reader.GetAttribute("channel") ?? string.Empty;
                var startTimeStr = reader.GetAttribute("start") ?? string.Empty;
                var endTimeStr = reader.GetAttribute("stop") ?? string.Empty;

                if (string.IsNullOrEmpty(channelId) || string.IsNullOrEmpty(startTimeStr) || string.IsNullOrEmpty(endTimeStr))
                {
                    return null;
                }

                var startTime = ParseXmlTvTime(startTimeStr);
                var endTime = ParseXmlTvTime(endTimeStr);

                if (startTime == null || endTime == null)
                {
                    return null;
                }

                var program = new EpgProgram
                {
                    Id = $"{channelId}_{startTime:yyyyMMddHHmmss}",
                    ChannelId = channelId,
                    StartTime = startTime.Value,
                    EndTime = endTime.Value
                };

                // 在 ReadSubtree 之前先检查是否是空元素
                bool isEmpty = reader.IsEmptyElement;

                if (!isEmpty)
                {
                    using var subReader = reader.ReadSubtree();
                    while (await subReader.ReadAsync())
                    {
                        if (subReader.NodeType == XmlNodeType.Element)
                        {
                            switch (subReader.LocalName.ToLower())
                            {
                                case "title":
                                    var lang = subReader.GetAttribute("lang") ?? "zh";
                                    var title = await subReader.ReadElementContentAsStringAsync();
                                    if (string.IsNullOrEmpty(program.Title) || lang == "zh")
                                    {
                                        program.Title = title.Trim();
                                        program.Language = lang;
                                    }
                                    break;
                                case "sub-title":
                                    program.SubTitle = (await subReader.ReadElementContentAsStringAsync()).Trim();
                                    break;
                                case "desc":
                                    var desc = await subReader.ReadElementContentAsStringAsync();
                                    if (string.IsNullOrEmpty(program.Description))
                                    {
                                        program.Description = desc.Trim();
                                    }
                                    break;
                                case "category":
                                    var category = (await subReader.ReadElementContentAsStringAsync()).Trim();
                                    if (!string.IsNullOrEmpty(category))
                                    {
                                        program.Categories.Add(category);
                                        if (string.IsNullOrEmpty(program.Category))
                                        {
                                            program.Category = category;
                                        }
                                    }
                                    break;
                                case "icon":
                                    var iconSrc = subReader.GetAttribute("src");
                                    if (!string.IsNullOrEmpty(iconSrc))
                                    {
                                        program.IconUrl = iconSrc;
                                    }
                                    break;
                                case "episode-num":
                                    var episodeSystem = subReader.GetAttribute("system") ?? "";
                                    var episodeText = await subReader.ReadElementContentAsStringAsync();
                                    ParseEpisodeInfo(program, episodeText, episodeSystem);
                                    break;
                                case "star-rating":
                                    await ParseStarRatingAsync(program, subReader);
                                    break;
                                case "rating":
                                    await ParseRatingAsync(program, subReader);
                                    break;
                                case "premiere":
                                    program.IsPremiere = true;
                                    break;
                                case "new":
                                    program.IsNew = true;
                                    break;
                                case "previously-shown":
                                    program.IsRepeat = true;
                                    break;
                                case "subtitles":
                                    program.IsSubtitled = true;
                                    break;
                                case "video":
                                    await ParseVideoInfoAsync(program, subReader);
                                    break;
                            }
                        }
                    }
                }

                return string.IsNullOrEmpty(program.Title) ? null : program;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"解析节目信息失败: {reader.GetAttribute("channel")} {reader.GetAttribute("start")}");
                return null;
            }
        }

        private DateTime? ParseXmlTvTime(string timeStr)
        {
            try
            {
                // XMLTV时间格式: 20240115120000 +0800 或 202401151200 +0800
                if (string.IsNullOrEmpty(timeStr))
                    return null;

                var parts = timeStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var datePart = parts[0];
                var offsetPart = parts.Length > 1 ? parts[1] : string.Empty;

                string? format = datePart.Length switch
                {
                    14 => "yyyyMMddHHmmss",
                    12 => "yyyyMMddHHmm",
                    8 => "yyyyMMdd",
                    _ => null
                };

                if (format == null) return null;

                if (DateTime.TryParseExact(datePart, format, null,
                    System.Globalization.DateTimeStyles.None, out var dateTime))
                {
                    if (!string.IsNullOrEmpty(offsetPart) && (offsetPart.StartsWith("+") || offsetPart.StartsWith("-")))
                    {
                        if (int.TryParse(offsetPart.Substring(1), out var offsetVal))
                        {
                            var hours = offsetVal / 100;
                            var minutes = offsetVal % 100;
                            var timeZone = TimeSpan.FromMinutes(hours * 60 + minutes);
                            
                            if (offsetPart.StartsWith("-"))
                                timeZone = -timeZone;

                            // 转换为 UTC 再转换为本地时间
                            var utcTime = DateTime.SpecifyKind(dateTime.Add(-timeZone), DateTimeKind.Utc);
                            return utcTime.ToLocalTime();
                        }
                    }

                    return DateTime.SpecifyKind(dateTime, DateTimeKind.Local);
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"解析时间失败: {timeStr}");
                return null;
            }
        }

        private void ParseEpisodeInfo(EpgProgram program, string episodeText, string system)
        {
            try
            {
                // 简单的剧集信息解析
                if (!string.IsNullOrEmpty(episodeText))
                {
                    if (int.TryParse(episodeText, out var episodeNumber))
                    {
                        program.EpisodeNumber = episodeNumber;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"解析剧集信息失败: {episodeText}");
            }
        }

        private async Task ParseStarRatingAsync(EpgProgram program, XmlReader reader)
        {
            try
            {
                while (await reader.ReadAsync())
                {
                    if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName.ToLower() == "star-rating")
                        break;

                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName.ToLower() == "value")
                    {
                        var rating = await reader.ReadElementContentAsStringAsync();
                        program.StarRating = rating.Trim();
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "解析星级评分失败");
            }
        }

        private async Task ParseRatingAsync(EpgProgram program, XmlReader reader)
        {
            try
            {
                var system = reader.GetAttribute("system") ?? "";
                var value = reader.GetAttribute("value") ?? "";
                
                if (!string.IsNullOrEmpty(value))
                {
                    program.Rating = $"{system}:{value}".Trim(':');
                }

                // 跳过rating元素的内容
                while (await reader.ReadAsync())
                {
                    if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName.ToLower() == "rating")
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "解析评级信息失败");
            }
        }

        private async Task ParseVideoInfoAsync(EpgProgram program, XmlReader reader)
        {
            try
            {
                while (await reader.ReadAsync())
                {
                    if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName.ToLower() == "video")
                        break;

                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        switch (reader.LocalName.ToLower())
                        {
                            case "quality":
                                var quality = await reader.ReadElementContentAsStringAsync();
                                if (quality.ToLower().Contains("hd"))
                                {
                                    program.IsHd = true;
                                }
                                break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "解析视频信息失败");
            }
        }
    }
}