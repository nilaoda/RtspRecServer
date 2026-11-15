using System.Globalization;
using RtspRecServer.Server.Services;
using RtspRecServer.Shared;

namespace RtspRecServer.Tests.Services;

public class TimerWindowCalculatorTests
{
    // 固定时区，避免测试依赖运行机器时区（IANA id 在 macOS/Linux 可用）
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");

    private static readonly bool[] MondayToFriday = [true, true, true, true, true, false, false];
    private static readonly bool[] EveryDay = [true, true, true, true, true, true, true];
    private static readonly bool[] OnlyMonday = [true, false, false, false, false, false, false];

    private static DateTimeOffset Local(string localDateTime)
    {
        var dto = DateTimeOffset.Parse(localDateTime, CultureInfo.InvariantCulture);
        return new DateTimeOffset(dto.DateTime, Tz.GetUtcOffset(dto.DateTime));
    }

    private static RecordingTimer MakeTimer(
        string startTime = "20:00",
        string endTime = "21:30",
        bool[]? weekdays = null,
        int pre = 0,
        int post = 0,
        string? startDate = null,
        string? endDate = null,
        bool enabled = true)
    {
        return new RecordingTimer
        {
            Name = "测试规则",
            ChannelId = 1,
            ChannelName = "频道",
            StartTime = startTime,
            EndTime = endTime,
            Weekdays = weekdays ?? MondayToFriday,
            PreOffsetMinutes = pre,
            PostOffsetMinutes = post,
            StartDate = startDate,
            EndDate = endDate,
            Enabled = enabled
        };
    }

    [Fact]
    public void CurrentWindow_MatchesWithinPlainWindow()
    {
        var timer = MakeTimer(weekdays: OnlyMonday);
        // 2026-11-16 是周一
        var now = Local("2026-11-16T20:30:00");

        var window = TimerWindowCalculator.TryGetCurrentWindow(timer, now, Tz);

        Assert.NotNull(window);
        Assert.Equal(Local("2026-11-16T20:00:00"), window.WindowStart);
        Assert.Equal(Local("2026-11-16T21:30:00"), window.WindowEnd);
    }

    [Fact]
    public void CurrentWindow_UsesPreAndPostOffsets()
    {
        var timer = MakeTimer(pre: 2, post: 5, weekdays: OnlyMonday);
        // 提前 2 分钟进入生效窗口
        var now = Local("2026-11-16T19:58:00");

        var window = TimerWindowCalculator.TryGetCurrentWindow(timer, now, Tz);

        Assert.NotNull(window);
        Assert.Equal(Local("2026-11-16T20:00:00"), window.WindowStart);
        Assert.Equal(Local("2026-11-16T19:58:00"), window.EffectiveStart);
        Assert.Equal(Local("2026-11-16T21:35:00"), window.EffectiveEnd);
    }

    [Fact]
    public void CurrentWindow_HandlesCrossMidnightWindow()
    {
        // 23:30 - 次日 01:00：周二凌晨 00:30 属于周一晚开始的窗口
        var timer = MakeTimer(startTime: "23:30", endTime: "01:00", weekdays: OnlyMonday);
        var now = Local("2026-11-17T00:30:00"); // 周二

        var window = TimerWindowCalculator.TryGetCurrentWindow(timer, now, Tz);

        Assert.NotNull(window);
        Assert.Equal(Local("2026-11-16T23:30:00"), window.WindowStart);
        Assert.Equal(Local("2026-11-17T01:00:00"), window.WindowEnd);
    }

    [Fact]
    public void CurrentWindow_ReturnsNullOutsideWeekday()
    {
        var timer = MakeTimer(weekdays: OnlyMonday);
        var now = Local("2026-11-17T20:30:00"); // 周二，规则只允许周一

        var window = TimerWindowCalculator.TryGetCurrentWindow(timer, now, Tz);

        Assert.Null(window);
    }

    [Fact]
    public void CurrentWindow_ReturnsNullOutsideDateRange()
    {
        var timer = MakeTimer(weekdays: EveryDay, startDate: "2026-11-01", endDate: "2026-11-10");
        var now = Local("2026-11-16T20:30:00"); // 超出生效日期

        var window = TimerWindowCalculator.TryGetCurrentWindow(timer, now, Tz);

        Assert.Null(window);
    }

    [Fact]
    public void CurrentWindow_MatchesWithinDateRange()
    {
        var timer = MakeTimer(weekdays: EveryDay, startDate: "2026-11-01");
        var now = Local("2026-11-16T20:30:00");

        var window = TimerWindowCalculator.TryGetCurrentWindow(timer, now, Tz);

        Assert.NotNull(window);
    }

    [Fact]
    public void NextWindow_FindsUpcomingWindowAcrossDays()
    {
        var timer = MakeTimer(weekdays: OnlyMonday);
        var now = Local("2026-11-17T22:00:00"); // 周二晚，下一个窗口是下周一

        var next = TimerWindowCalculator.GetNextWindow(timer, now, Tz);

        Assert.NotNull(next);
        Assert.Equal(Local("2026-11-23T20:00:00"), next.WindowStart);
    }

    [Fact]
    public void NextWindow_IncludesPreOffsetInEffectiveStart()
    {
        var timer = MakeTimer(pre: 2, weekdays: OnlyMonday);
        // 周一 19:59（已过 effectiveStart 19:58），下一个窗口是下周一
        var now = Local("2026-11-16T19:59:00");

        var next = TimerWindowCalculator.GetNextWindow(timer, now, Tz);

        Assert.NotNull(next);
        Assert.Equal(Local("2026-11-23T20:00:00"), next.WindowStart);
        Assert.Equal(Local("2026-11-23T19:58:00"), next.EffectiveStart);
    }
}
