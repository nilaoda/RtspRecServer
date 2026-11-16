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
        // 只接受纯文件名：拒绝路径分隔符、盘符/数据流冒号、超长名与控制字符，防止目录穿越与日志注入
        if (fileName.Length > 255 ||
            fileName.Any(char.IsControl) ||
            fileName is "." or ".." ||
            fileName.IndexOf('/') >= 0 ||
            fileName.IndexOf('\\') >= 0 ||
            fileName.IndexOf(':') >= 0 ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return "文件名不合法";
        }

        if (!string.Equals(Path.GetExtension(fileName), ".ts", StringComparison.OrdinalIgnoreCase))
        {
            return "仅支持删除 .ts 录像文件";
        }

        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, fileName));
        if (!IsPathUnderRoot(fullRoot, fullPath))
        {
            return "非法路径";
        }

        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            return "文件不存在";
        }
        if (info.LinkTarget is not null)
        {
            return "不允许删除符号链接";
        }
        if (activePaths.Contains(fullPath))
        {
            return "文件正在录制中";
        }

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
        if (!IsPathUnderRoot(fullRoot, logPath))
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

    internal static bool IsPathUnderRoot(string rootPath, string targetPath)
    {
        var fullRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullTarget = Path.GetFullPath(targetPath);
        return fullTarget.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }
}