using System.IO.Compression;

namespace RtspRecServer.Server.Services.Epg;

/// <summary>
/// EPG 下载源的解码结果。
/// </summary>
public sealed record DecodedEpgSource(Stream Stream, string Format);

/// <summary>
/// EPG 数据源格式嗅探与解码：按响应内容的魔数识别 gzip / zip / 纯 XML。
/// 部分源以 .gz 地址返回未压缩内容、或以任意地址返回 zip 压缩包，
/// 不能信任 URL 后缀，否则 GZipStream 会以 deflate 非法块报错
/// （"The archive entry was compressed using an unsupported compression method"）。
/// </summary>
public static class EpgSourceDecoder
{
    public static async Task<DecodedEpgSource> DecodeToXmlStreamAsync(Stream source, CancellationToken cancellationToken = default)
    {
        // 嗅探响应前两个字节：1f 8b = gzip，50 4b（PK）= zip，其余按纯 XML 透传
        var prefix = new byte[2];
        var prefixLength = 0;
        while (prefixLength < prefix.Length)
        {
            var read = await source.ReadAsync(prefix.AsMemory(prefixLength), cancellationToken);
            if (read == 0)
            {
                break;
            }
            prefixLength += read;
        }

        if (prefixLength >= 2 && prefix[0] == 0x1f && prefix[1] == 0x8b)
        {
            var replay = new PrefixReplayStream(source, prefix, prefixLength);
            return new DecodedEpgSource(new GZipStream(replay, CompressionMode.Decompress), "gzip");
        }

        if (prefixLength >= 2 && prefix[0] == 0x50 && prefix[1] == 0x4b)
        {
            return await DecodeZipAsync(source, prefix, prefixLength, cancellationToken);
        }

        return new DecodedEpgSource(new PrefixReplayStream(source, prefix, prefixLength), "plain");
    }

    /// <summary>
    /// 解压 zip 压缩包并定位节目单：优先取 .xml.gz 条目，其次 .xml 条目。
    /// </summary>
    private static async Task<DecodedEpgSource> DecodeZipAsync(
        Stream source, byte[] prefix, int prefixLength, CancellationToken cancellationToken)
    {
        // ZipArchive 需要可查找流，压缩包体积有限，整体缓冲
        var buffered = new MemoryStream();
        await using (var replay = new PrefixReplayStream(source, prefix, prefixLength))
        {
            await replay.CopyToAsync(buffered, cancellationToken);
        }

        buffered.Position = 0;
        var archive = new ZipArchive(buffered, ZipArchiveMode.Read, leaveOpen: false);
        var gzEntry = archive.Entries.FirstOrDefault(e =>
            e.Name.EndsWith(".xml.gz", StringComparison.OrdinalIgnoreCase) && e.Length > 0);
        var xmlEntry = archive.Entries.FirstOrDefault(e =>
            e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && e.Length > 0);

        var entry = gzEntry ?? xmlEntry;
        if (entry is null)
        {
            archive.Dispose();
            throw new InvalidDataException("EPG 压缩包中没有找到 .xml 或 .xml.gz 节目单文件");
        }

        try
        {
            var entryStream = await entry.OpenAsync(cancellationToken);
            if (gzEntry is not null)
            {
                entryStream = new GZipStream(entryStream, CompressionMode.Decompress);
            }

            var format = gzEntry is not null ? "zip(xml.gz)" : "zip(xml)";
            return new DecodedEpgSource(new ArchiveEntryStream(entryStream, archive, buffered), format);
        }
        catch (InvalidDataException)
        {
            archive.Dispose();
            throw new InvalidDataException(
                $"EPG 压缩包条目「{entry.FullName}」使用了不受支持的压缩算法（如 bzip2/LZMA/Zstd），请更换为 Deflate 压缩或 .gz/.xml 直链");
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    /// <summary>先回放嗅探阶段读取的前缀字节，再继续读取底层流。</summary>
    internal sealed class PrefixReplayStream : Stream
    {
        private readonly Stream _inner;
        private readonly byte[] _prefix;
        private readonly int _prefixLength;
        private int _prefixPosition;

        public PrefixReplayStream(Stream inner, byte[] prefix, int prefixLength)
        {
            _inner = inner;
            _prefix = prefix;
            _prefixLength = prefixLength;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_prefixPosition < _prefixLength)
            {
                var count = Math.Min(buffer.Length, _prefixLength - _prefixPosition);
                _prefix.AsMemory(_prefixPosition, count).CopyTo(buffer);
                _prefixPosition += count;
                return count;
            }

            return await _inner.ReadAsync(buffer, cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>包装 zip 条目流，释放时连同 ZipArchive 与缓冲一起释放。</summary>
    internal sealed class ArchiveEntryStream : Stream
    {
        private readonly Stream _entry;
        private readonly ZipArchive _archive;
        private readonly MemoryStream _buffer;

        public ArchiveEntryStream(Stream entry, ZipArchive archive, MemoryStream buffer)
        {
            _entry = entry;
            _archive = archive;
            _buffer = buffer;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _entry.ReadAsync(buffer, cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
            => _entry.Read(buffer, offset, count);

        public override bool CanRead => _entry.CanRead;
        public override bool CanSeek => _entry.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _entry.Length;
        public override long Position { get => _entry.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _entry.Flush();
        public override long Seek(long offset, SeekOrigin origin) => _entry.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _entry.Dispose();
                _archive.Dispose();
                _buffer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
