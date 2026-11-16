using RtspRecServer.Server.Recording;

namespace RtspRecServer.Tests.Recording;

public class RecordingFileDeletionTests
{
    [Fact]
    public void Delete_RemovesTsAndSidecarLog()
    {
        var root = CreateRoot();
        try
        {
            var ts = Path.Combine(root, "节目_20260101120000.ts");
            var log = Path.ChangeExtension(ts, ".log");
            File.WriteAllBytes(ts, new byte[16]);
            File.WriteAllText(log, "log");

            var result = RecordingFileDeletion.Delete(root, ["节目_20260101120000.ts"], new HashSet<string>(), "127.0.0.1");

            Assert.Equal(1, result.Succeeded);
            Assert.Empty(result.FailedItems);
            Assert.False(File.Exists(ts));
            Assert.False(File.Exists(log));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("../outside.ts")]
    [InlineData("sub/inside.ts")]
    [InlineData("/etc/passwd.ts")]
    [InlineData("C:evil.ts")]
    [InlineData("movie.mp4")]
    [InlineData("..")]
    public void Delete_RejectsUnsafeNames(string fileName)
    {
        var root = CreateRoot();
        try
        {
            var result = RecordingFileDeletion.Delete(root, [fileName], new HashSet<string>(), null);

            Assert.Equal(0, result.Succeeded);
            Assert.Single(result.FailedItems);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Delete_RejectsActiveRecording()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "active.ts");
            File.WriteAllBytes(path, [1, 2, 3]);
            var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(path) };

            var result = RecordingFileDeletion.Delete(root, ["active.ts"], active, null);

            Assert.Equal(0, result.Succeeded);
            Assert.Single(result.FailedItems);
            Assert.Equal("文件正在录制中", result.FailedItems[0].Reason);
            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Delete_ReportsMissingFileAndKeepsMixedResults()
    {
        var root = CreateRoot();
        try
        {
            File.WriteAllBytes(Path.Combine(root, "ok.ts"), [1]);

            var result = RecordingFileDeletion.Delete(root, ["ok.ts", "missing.ts"], new HashSet<string>(), null);

            Assert.Equal(1, result.Succeeded);
            Assert.Equal(1, result.Failed);
            Assert.Equal(["ok.ts"], result.SucceededFiles);
            Assert.Equal("missing.ts", result.FailedItems[0].FileName);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Delete_IgnoresEmptyAndDuplicateNames()
    {
        var root = CreateRoot();
        try
        {
            File.WriteAllBytes(Path.Combine(root, "a.ts"), [1]);

            var result = RecordingFileDeletion.Delete(root, ["a.ts", " a.ts ", "", "   "], new HashSet<string>(), null);

            Assert.Equal(1, result.Succeeded);
            Assert.Equal(0, result.Failed);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Delete_RejectsSymlinkPointingOutsideRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows 创建符号链接通常需要额外权限
            return;
        }

        var root = CreateRoot();
        var outside = CreateRoot();
        try
        {
            var target = Path.Combine(outside, "secret.ts");
            File.WriteAllBytes(target, [1]);
            File.CreateSymbolicLink(Path.Combine(root, "link.ts"), target);

            var result = RecordingFileDeletion.Delete(root, ["link.ts"], new HashSet<string>(), null);

            Assert.Equal(0, result.Succeeded);
            Assert.Single(result.FailedItems);
            Assert.Equal("不允许删除符号链接", result.FailedItems[0].Reason);
            Assert.True(File.Exists(target));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void Delete_RejectsOverlongAndControlCharacterNames()
    {
        var root = CreateRoot();
        try
        {
            var overlong = RecordingFileDeletion.Delete(root, [new string('a', 256) + ".ts"], new HashSet<string>(), null);
            Assert.Equal("文件名不合法", overlong.FailedItems[0].Reason);

            var control = RecordingFileDeletion.Delete(root, ["evil\nname.ts"], new HashSet<string>(), null);
            Assert.Equal("文件名不合法", control.FailedItems[0].Reason);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string CreateRoot()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rtsp-delete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}