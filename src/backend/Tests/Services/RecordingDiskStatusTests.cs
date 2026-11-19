using RtspRecServer.Server.Services;

namespace RtspRecServer.Tests.Services;

public class RecordingDiskStatusTests
{
    [Theory]
    [InlineData("record", true)]
    [InlineData("record/live", true)]
    [InlineData("recordings/live", false)]
    public void ContainsPath_UsesMountDirectoryBoundary(string path, bool expected)
    {
        var root = Path.GetTempPath();
        Assert.Equal(expected, RecordingDiskStatus.ContainsPath(Path.Combine(root, "record"), Path.Combine(root, path)));
    }
}
