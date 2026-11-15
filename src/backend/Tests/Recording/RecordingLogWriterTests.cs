using RtspRecServer.Server.Recording;
using RtspRecServer.Shared;

namespace RtspRecServer.Tests.Recording;

public class RecordingLogWriterTests
{
    [Fact]
    public void Create_Disabled_ReturnsNull()
    {
        var directory = CreateTempDirectory();
        try
        {
            var outputPath = Path.Combine(directory, "task.ts");

            using var writer = RecordingLogWriter.Create(outputPath, enabled: false);

            Assert.Null(writer);
            Assert.False(File.Exists(Path.ChangeExtension(outputPath, ".log")));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void WritesStartProgressAndFinalSummary()
    {
        var directory = CreateTempDirectory();
        try
        {
            var outputPath = Path.Combine(directory, "Live_Test_20260101_20260102.ts");
            var startedAt = DateTimeOffset.Now;
            var task = new RecordingTask
            {
                Id = 7,
                ChannelId = 1,
                ChannelName = "CCTV-1",
                TaskName = "Live_Test_20260101_20260102",
                DisplayName = "Live",
                IsLive = true,
                StartTime = startedAt,
                EndTime = startedAt.AddMinutes(10),
                Url = "http://udpxy.example.com:5140/rtp/239.1.1.1:8000"
            };
            var tsCheck = new TsCheckSummary
            {
                Verdict = "warning",
                ErrorCount = 1,
                WarningCount = 2,
                PacketCount = 1000,
                BytesScanned = 1024 * 1024,
                BitrateKbps = 8000,
                Video = "H.264",
                Audio = "AAC",
                PidCount = 4,
                LastEvent = "连续计数缺口 (PID 0x0101)"
            };

            using (var writer = RecordingLogWriter.Create(outputPath, enabled: true))
            {
                Assert.NotNull(writer);
                writer!.WriteStart(task, outputPath, "MP2T/TCP", startedAt);
                writer.WriteProgress(new RecordingProgress
                {
                    BytesWritten = 1024 * 1024,
                    TsCheck = tsCheck
                });
                writer.WriteProgress(new RecordingProgress
                {
                    BytesWritten = 2 * 1024 * 1024,
                    TsCheck = tsCheck
                });
                writer.WriteFinal(RecordingStatus.Completed, 2 * 1024 * 1024, 1.5, null, tsCheck);
            }

            var logPath = Path.ChangeExtension(outputPath, ".log");
            Assert.True(File.Exists(logPath));
            var content = File.ReadAllText(logPath);
            Assert.Contains("录制开始", content);
            Assert.Contains("CCTV-1", content);
            Assert.Contains("TS=警告", content);
            Assert.Contains("H.264", content);
            Assert.Contains("AAC", content);
            Assert.Contains("连续计数缺口", content);
            Assert.Contains("录制结束", content);
            Assert.Contains("已完成", content);
            Assert.Equal(1, content.Split("[TS]").Length - 1);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void DoesNotWriteRoutineProgressWithoutErrorsOrWarnings()
    {
        var directory = CreateTempDirectory();
        try
        {
            var outputPath = Path.Combine(directory, "Live_Test_20260101_20260102.ts");
            var startedAt = DateTimeOffset.Now;
            var task = new RecordingTask
            {
                Id = 8,
                ChannelId = 1,
                ChannelName = "CCTV-1",
                TaskName = "Live_Test_20260101_20260102",
                DisplayName = "Live",
                IsLive = true,
                StartTime = startedAt,
                EndTime = startedAt.AddMinutes(10),
                Url = "http://udpxy.example.com:5140/rtp/239.1.1.1:8000"
            };
            var tsCheck = new TsCheckSummary
            {
                Verdict = "pass",
                PacketCount = 100_000,
                Video = "H.264",
                Audio = "AAC",
                PidCount = 4
            };

            using (var writer = RecordingLogWriter.Create(outputPath, enabled: true))
            {
                Assert.NotNull(writer);
                writer!.WriteStart(task, outputPath, "MP2T/TCP", startedAt);
                writer.WriteProgress(new RecordingProgress
                {
                    BytesWritten = 1024 * 1024,
                    TsCheck = tsCheck
                });
            }

            var content = File.ReadAllText(Path.ChangeExtension(outputPath, ".log"));
            Assert.DoesNotContain("[TS]", content);
            Assert.DoesNotContain("[进度]", content);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rtsp-record-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
