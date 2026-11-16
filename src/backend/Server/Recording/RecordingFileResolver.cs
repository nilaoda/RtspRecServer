namespace RtspRecServer.Server.Recording;

internal enum RecordingFileResolveResult
{
    Ok,
    InvalidName,
    NotTs,
    OutsideRoot,
    NotFound,
    Symlink
}

/// <summary>把前端传来的文件名安全解析为录像目录下的真实路径，供播放/删除等操作共用。</summary>
internal static class RecordingFileResolver
{
    public static RecordingFileResolveResult Resolve(string rootPath, string fileName, out string fullPath)
    {
        fullPath = string.Empty;

        // 只接受纯文件名：拒绝路径分隔符、盘符/数据流冒号、超长名与控制字符
        if (fileName.Length > 255 ||
            fileName.Any(char.IsControl) ||
            fileName is "." or ".." ||
            fileName.IndexOf('/') >= 0 ||
            fileName.IndexOf('\\') >= 0 ||
            fileName.IndexOf(':') >= 0 ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return RecordingFileResolveResult.InvalidName;
        }

        if (!string.Equals(Path.GetExtension(fileName), ".ts", StringComparison.OrdinalIgnoreCase))
        {
            return RecordingFileResolveResult.NotTs;
        }

        var fullRoot = Path.GetFullPath(rootPath);
        fullPath = Path.GetFullPath(Path.Combine(fullRoot, fileName));
        if (!IsPathUnderRoot(fullRoot, fullPath))
        {
            return RecordingFileResolveResult.OutsideRoot;
        }

        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            return RecordingFileResolveResult.NotFound;
        }
        if (info.LinkTarget is not null)
        {
            return RecordingFileResolveResult.Symlink;
        }

        return RecordingFileResolveResult.Ok;
    }

    public static bool IsPathUnderRoot(string rootPath, string targetPath)
    {
        var fullRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullTarget = Path.GetFullPath(targetPath);
        return fullTarget.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }
}