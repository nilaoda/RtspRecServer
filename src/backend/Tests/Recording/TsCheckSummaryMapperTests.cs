using RtspRecServer.Server.Recording.TsAnalysis;

namespace RtspRecServer.Tests.Recording;

public class TsCheckSummaryMapperTests
{
    [Fact]
    public void FromProgress_UsesDecimalKilobitForBitrate()
    {
        var progress = new TsCheckProgress(
            BytesScanned: 1_000_000,
            FileSize: 2_000_000,
            PacketCount: 100,
            ErrorCount: 0,
            WarningCount: 0,
            BytesPerSecond: 1_000_000,
            Elapsed: TimeSpan.FromSeconds(1),
            Pids: [],
            NewEvent: null,
            Timeline: []);

        var summary = TsCheckSummaryMapper.FromProgress(progress);

        Assert.Equal(8000, summary.BitrateKbps, 3);
    }

    [Fact]
    public void FromResult_UsesDecimalKilobitForBitrate()
    {
        var result = new TsCheckResult
        {
            FilePath = "test.ts",
            FileSize = 2_000_000,
            BytesScanned = 1_000_000,
            Elapsed = TimeSpan.FromSeconds(1)
        };

        var summary = TsCheckSummaryMapper.FromResult(result);

        Assert.Equal(8000, summary.BitrateKbps, 3);
    }
}
