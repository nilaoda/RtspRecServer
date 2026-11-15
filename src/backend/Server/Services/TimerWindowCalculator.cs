using System.Globalization;
using RtspRecServer.Shared;

namespace RtspRecServer.Server.Services;

/// <summary>
/// 定时规则的一次执行窗口（按本地时区计算）。
/// </summary>
public sealed record TimerWindow
{
    /// <summary>规则设定的窗口起点（不含提前量）</summary>
    public DateTimeOffset WindowStart { get; init; }
    /// <summary>规则设定的窗口终点（不含延后量），跨午夜时在次日</summary>
    public DateTimeOffset WindowEnd { get; init; }
    /// <summary>实际录制起点 = WindowStart - PreOffsetMinutes</summary>
    public DateTimeOffset EffectiveStart { get; init; }
    /// <summary>实际录制终点 = WindowEnd + PostOffsetMinutes</summary>
    public DateTimeOffset EffectiveEnd { get; init; }
}

/// <summary>
/// 纯函数窗口计算，便于单元测试。时间字符串格式 "HH:mm"，日期范围 "yyyy-MM-dd"。
/// </summary>
public static class TimerWindowCalculator
{
    private const int MaxSearchDays = 10;

    /// <summary>
    /// 返回 now 所处的执行窗口；now 不在任何生效窗口内时返回 null。
    /// </summary>
    public static TimerWindow? TryGetCurrentWindow(RecordingTimer timer, DateTimeOffset now, TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? TimeZoneInfo.Local;
        var todayLocal = TimeZoneInfo.ConvertTime(now, tz).Date;

        return EnumerateWindows(timer, todayLocal.AddDays(-1), 3, tz)
            .FirstOrDefault(w => now >= w.EffectiveStart && now < w.EffectiveEnd);
    }

    /// <summary>
    /// 返回下一个尚未开始的执行窗口；没有（如日期范围已结束）返回 null。
    /// </summary>
    public static TimerWindow? GetNextWindow(RecordingTimer timer, DateTimeOffset now, TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? TimeZoneInfo.Local;
        var todayLocal = TimeZoneInfo.ConvertTime(now, tz).Date;

        return EnumerateWindows(timer, todayLocal.AddDays(-1), MaxSearchDays + 1, tz)
            .Where(w => w.EffectiveStart > now)
            .OrderBy(w => w.EffectiveStart)
            .FirstOrDefault();
    }

    /// <summary>
    /// 从 fromDateLocal 起连续 days 天内，所有匹配星期与日期范围的窗口。
    /// 跨午夜规则（EndTime &lt;= StartTime）窗口终点落在次日。
    /// </summary>
    public static IEnumerable<TimerWindow> EnumerateWindows(
        RecordingTimer timer, DateTime fromDateLocal, int days, TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? TimeZoneInfo.Local;

        if (!TimeOnly.TryParse(timer.StartTime, CultureInfo.InvariantCulture, out var startTime) ||
            !TimeOnly.TryParse(timer.EndTime, CultureInfo.InvariantCulture, out var endTime))
        {
            yield break;
        }

        var startDate = ParseDate(timer.StartDate, DateTime.MinValue.Date);
        var endDate = ParseDate(timer.EndDate, DateTime.MaxValue.Date);

        for (var i = 0; i < days; i++)
        {
            var date = fromDateLocal.AddDays(i);

            // 星期索引：0=周一 … 6=周日
            var weekdayIndex = ((int)date.DayOfWeek + 6) % 7;
            if (weekdayIndex >= timer.Weekdays.Length || !timer.Weekdays[weekdayIndex])
            {
                continue;
            }

            // 日期范围按窗口起点所在日期判定
            if (date.Date < startDate || date.Date > endDate)
            {
                continue;
            }

            var windowStart = AtDate(date, startTime, tz);
            var windowEnd = AtDate(date, endTime, tz);
            if (windowEnd <= windowStart)
            {
                windowEnd = windowEnd.AddDays(1);
            }

            yield return new TimerWindow
            {
                WindowStart = windowStart,
                WindowEnd = windowEnd,
                EffectiveStart = windowStart.AddMinutes(-timer.PreOffsetMinutes),
                EffectiveEnd = windowEnd.AddMinutes(timer.PostOffsetMinutes)
            };
        }
    }

    private static DateTimeOffset AtDate(DateTime date, TimeOnly time, TimeZoneInfo tz)
    {
        var local = new DateTime(date.Year, date.Month, date.Day, time.Hour, time.Minute, 0);
        var offset = tz.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    private static DateTime ParseDate(string? value, DateTime fallback)
    {
        if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date.Date;
        }

        return fallback;
    }
}
