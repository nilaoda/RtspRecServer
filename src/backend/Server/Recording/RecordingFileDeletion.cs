using Serilog;
using RtspRecServer.Shared;

namespace RtspRecServer.Server.Recording;

internal static class RecordingFileDeletion
{
    public static RecordingFileDeleteResult Delete(
        string rootPath,
        IEnumerable<string>? fileNames,
        IReadOnlySet<string> activePaths,
        string? clientIp)
    {
        var requested = (fileNames ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var succeeded = new List<string>(requested.Length);
        var failures = new List<RecordingFileDeleteFailure>();
        var fullRoot = Path.GetFullPath(rootPath);

        foreach (var fileName in requested)
        {
            var error = TryDelete(fullRoot, fileName, activePaths, clientIp);
            if (error is null)
            {
                succeeded.Add(fileName);
            }
            else
            {
                failures.Add(new RecordingFileDeleteFailure { FileName = fileName, Reason = error });
            }
        }

        return new RecordingFileDeleteResult
        {
            Succeeded = succeeded.Count,
            Failed = failures.Count,
            SucceededFiles = succeeded.ToArray(),
            FailedItems = failures.ToArray()
        };
    }

    private static string? TryDelete(string fullRoot, string fileName, IReadOnlySet<string> activePaths, string? clientIp)
    {
        var error = RecordingFileResolver.Resolve(fullRoot, fileName, out var fullPath) switch
        {
            RecordingFileResolveResult.Ok => null,
            RecordingFileResolveResult.NotTs => "仅支持删除 .ts 录像文件",
            RecordingFileResolveResult.NotFound => "文件不存在",
            RecordingFileResolveResult.Symlink => "不允许删除符号链接",
            _ => "文件名不合法"
        };
        if (error is not null)
        {
            return error;
        }

        if (activePaths.Contains(fullPath))
        {
            return "文件正在录制中";
        }

        var info = new FileInfo(fullPath);
        var size = info.Length;
        try
        {
            File.Delete(fullPath);
        }
        catch (IOException)
        {
            return "文件正在被使用";
        }
        catch (UnauthorizedAccessException)
        {
            return "没有删除权限";
        }

        Log.Information(
            "删除录像文件: {FileName}（{Bytes} 字节），来源 {ClientIp}",
            fileName,
            size,
            clientIp ?? "-");

        DeleteSidecarLog(fullPath, fullRoot);
        return null;
    }

    private static void DeleteSidecarLog(string fullPath, string fullRoot)
    {
        var logPath = Path.ChangeExtension(fullPath, ".log");
        if (!RecordingFileResolver.IsPathUnderRoot(fullRoot, logPath))
        {
            return;
        }

        try
        {
            var info = new FileInfo(logPath);
            if (!info.Exists || info.LinkTarget is not null)
            {
                return;
            }

            File.Delete(logPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "删除录像日志失败: {LogPath}", logPath);
        }
    }
}