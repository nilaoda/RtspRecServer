using RtspRecServer.Server.Services;
using RtspRecServer.Shared;

namespace RtspRecServer.Tests.Services;

public class TaskErrorLogServiceTests
{
    [Fact]
    public void RecordTaskChange_AppendsAndMergesSameIdentityWithinWindow()
    {
        var notifier = new NotifierFake();
        var now = DateTimeOffset.Parse("2026-11-16T19:00:00+08:00");
        var service = new TaskErrorLogService(notifier, () => now);

        var first = CreateTask(
            1,
            new TsCheckSummary
            {
                LastEvent = "19:00:00 / 00:00:01.000* 连续计数缺口 (PID 0x0101)",
                ErrorCount = 1
            });
        service.RecordTaskChange(null, first);

        now = now.AddSeconds(1);
        var second = CreateTask(
            1,
            new TsCheckSummary
            {
                LastEvent = "19:00:01 / 00:00:02.000* ×3 连续计数缺口 (PID 0x0101)",
                ErrorCount = 3
            });
        service.RecordTaskChange(first, second);

        var snapshot = service.GetSnapshot();
        Assert.Single(snapshot.Entries);
        Assert.Equal("连续计数缺口 (PID 0x0101)", snapshot.Entries[0].Identity);
        Assert.Contains("×3", snapshot.Entries[0].Message);
        Assert.Equal("error", snapshot.Entries[0].Severity);
        Assert.Equal(2, snapshot.Entries[0].Revision);
        Assert.Equal(2, notifier.Messages.Count);
    }

    [Fact]
    public void RecordTaskChange_AppendsNewIdentityOrOutsideMergeWindow()
    {
        var notifier = new NotifierFake();
        var now = DateTimeOffset.Parse("2026-11-16T19:00:00+08:00");
        var service = new TaskErrorLogService(notifier, () => now);

        var first = CreateTask(1, new TsCheckSummary
        {
            LastEvent = "19:00:00 / 00:00:01.000* 连续计数缺口 (PID 0x0101)",
            ErrorCount = 1
        });
        service.RecordTaskChange(null, first);

        now = now.AddSeconds(5);
        var later = CreateTask(1, new TsCheckSummary
        {
            LastEvent = "19:00:05 / 00:00:06.000* ×2 连续计数缺口 (PID 0x0101)",
            ErrorCount = 3
        });
        service.RecordTaskChange(first, later);

        now = now.AddSeconds(1);
        var different = CreateTask(1, new TsCheckSummary
        {
            LastEvent = "19:00:06 / 00:00:07.000* 节目表 CRC 错误 (PID 0x0000)",
            ErrorCount = 4
        });
        service.RecordTaskChange(later, different);

        var snapshot = service.GetSnapshot();
        Assert.Equal(3, snapshot.Entries.Length);
        Assert.Equal(3, snapshot.Entries[^1].Id);
    }

    [Fact]
    public void RecordTaskChange_RecordsFailureOnlyOncePerMessage()
    {
        var notifier = new NotifierFake();
        var service = new TaskErrorLogService(notifier, () => DateTimeOffset.Now);

        var recording = CreateTask(1, null);
        var failed = CreateTask(1, null, RecordingStatus.Failed, "UDP 接收已停止");
        service.RecordTaskChange(recording, failed);
        service.RecordTaskChange(failed, failed);

        var snapshot = service.GetSnapshot();
        Assert.Single(snapshot.Entries);
        Assert.Equal("error", snapshot.Entries[0].Severity);
        Assert.Equal("失败：UDP 接收已停止", snapshot.Entries[0].Message);
    }

    [Fact]
    public void Snapshot_CapsAtConfiguredMaxEntriesAndClearBumpsGeneration()
    {
        var notifier = new NotifierFake();
        const int maxEntries = TaskErrorLogService.MinMaxEntries;
        var service = new TaskErrorLogService(notifier, () => maxEntries, () => DateTimeOffset.Now);

        for (var index = 0; index < maxEntries + 5; index++)
        {
            service.RecordTaskChange(null, CreateTask(1, new TsCheckSummary
            {
                LastEvent = $"00:00:01.000* 事件 {index} (PID 0x0101)",
                ErrorCount = index + 1
            }));
        }

        var snapshot = service.GetSnapshot();
        Assert.Equal(maxEntries, snapshot.Entries.Length);
        Assert.Equal(6, snapshot.Entries[0].Id);

        var cleared = service.Clear();
        Assert.True(cleared.Reset);
        var afterClear = service.GetSnapshot();
        Assert.Empty(afterClear.Entries);
        Assert.Equal(1, afterClear.Generation);
    }

    [Theory]
    [InlineData("19:04:37 / 00:00:08.075* 节目表 CRC 错误 (PID 0x0000)", "节目表 CRC 错误 (PID 0x0000)")]
    [InlineData("00:00:05.547* ×12 连续计数缺口 (PID 0x0101)", "连续计数缺口 (PID 0x0101)")]
    [InlineData("失败：直播流无数据超时", "失败：直播流无数据超时")]
    public void ExtractIdentity_StripsTimeAndCountPrefixes(string message, string expected)
    {
        Assert.Equal(expected, TaskErrorLogService.ExtractIdentity(message));
    }

    private static RecordingTask CreateTask(
        long id,
        TsCheckSummary? tsCheck,
        RecordingStatus status = RecordingStatus.Recording,
        string? errorMessage = null)
    {
        return new RecordingTask
        {
            Id = id,
            ChannelId = 1,
            ChannelName = "测试频道 01",
            Url = "rtp://239.1.1.1:8000",
            TaskName = $"task-{id}",
            DisplayName = $"任务 {id}",
            Status = status,
            ErrorMessage = errorMessage,
            FilePath = "/tmp/测试频道 01_20261116190000.ts",
            TsCheck = tsCheck
        };
    }

    private sealed class NotifierFake : IRecordingNotifier
    {
        public List<TaskErrorLogMessage> Messages { get; } = [];

        public Task NotifyTaskUpdateAsync(RecordingTaskDto task, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task NotifyTaskErrorLogAsync(TaskErrorLogMessage message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}