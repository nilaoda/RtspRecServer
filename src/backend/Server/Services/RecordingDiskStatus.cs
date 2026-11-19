using RtspRecServer.Shared;

namespace RtspRecServer.Server.Services;

internal static class RecordingDiskStatus
{
    public static DiskStatus Read(string recordPath)
    {
        try
        {
            var path = string.IsNullOrWhiteSpace(recordPath) ? "./records" : recordPath;
            var fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path));
            var drive = DriveInfo.GetDrives()
                .Where(drive => drive.IsReady && ContainsPath(drive.RootDirectory.FullName, fullPath))
                .MaxBy(drive => drive.RootDirectory.FullName.Length);
            if (drive is not null)
            {
                return new DiskStatus { TotalBytes = drive.TotalSize, FreeBytes = drive.AvailableFreeSpace };
            }
        }
        catch
        {
            // 挂载点可能在读取过程中卸载，此时由界面显示空间数据不可用。
        }

        return new DiskStatus();
    }

    internal static bool ContainsPath(string mountPath, string path)
    {
        var relative = Path.GetRelativePath(mountPath, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }
}
