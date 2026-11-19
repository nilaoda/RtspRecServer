namespace RtspRecServer.Server.Recording;

internal sealed class BatchedTsStream : Stream
{
    private const int PacketSize = 188;
    private const int BlockSize = PacketSize * 128;
    private static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(40);
    private readonly Action<ReadOnlyMemory<byte>> _publish;
    private readonly bool _alignPackets;
    private readonly Timer _flushTimer;
    private byte[]? _buffer;
    private int _length;
    private bool _started;
    private bool _flushScheduled;

    public BatchedTsStream(Action<ReadOnlyMemory<byte>> publish, bool alignPackets = false)
    {
        _publish = publish;
        _alignPackets = alignPackets;
        _flushTimer = new Timer(static state => ((BatchedTsStream)state!).FlushOnTimeout(), this,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    // 录制订阅的加入和退出与发送共用这把锁，切换前可先发送已经完整接收的数据包。
    public object SyncRoot { get; } = new();

    public override void Write(ReadOnlySpan<byte> data)
    {
        lock (SyncRoot)
        {
            if (!_started && !data.IsEmpty)
            {
                var syncOffset = MpegTsValidator.FindSyncOffset(data);
                if (syncOffset < 0)
                {
                    throw new InvalidDataException(_alignPackets
                        ? "直播源返回内容不是 MPEG-TS"
                        : "播放源返回内容不是 MPEG-TS");
                }

                if (_alignPackets)
                {
                    data = data[syncOffset..];
                }

                // 立即发送首块数据，后续合并小包以减少分发和写入次数。
                var count = GetPublishLength(Math.Min(data.Length, BlockSize));
                if (count != 0)
                {
                    _publish(data[..count].ToArray());
                    data = data[count..];
                }
                _started = true;
            }

            while (!data.IsEmpty)
            {
                if (_buffer is null && data.Length >= BlockSize)
                {
                    _publish(data[..BlockSize].ToArray());
                    data = data[BlockSize..];
                    continue;
                }

                _buffer ??= new byte[BlockSize];
                var count = Math.Min(data.Length, BlockSize - _length);
                data[..count].CopyTo(_buffer.AsSpan(_length));
                _length += count;
                data = data[count..];
                if (_length == BlockSize)
                {
                    FlushCore();
                }
            }

            if (!_flushScheduled && GetPublishLength(_length) > 0)
            {
                _flushScheduled = true;
                _flushTimer.Change(FlushDelay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    public override void Flush()
    {
        lock (SyncRoot)
        {
            FlushCore();
        }
    }

    private void FlushOnTimeout()
    {
        lock (SyncRoot)
        {
            _flushScheduled = false;
            FlushCore();
        }
    }

    private int GetPublishLength(int length) => _alignPackets ? length - length % PacketSize : length;

    private void FlushCore()
    {
        var count = GetPublishLength(_length);
        if (count == 0)
        {
            return;
        }

        var data = _buffer!;
        _length -= count;
        _buffer = null;
        if (_length != 0)
        {
            _buffer = new byte[BlockSize];
            data.AsSpan(count, _length).CopyTo(_buffer);
        }

        // 发出的字节块由各订阅共享，保留在缓冲中的尾部使用新的数组。
        _publish(data.AsMemory(0, count));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _flushTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        Flush();
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
