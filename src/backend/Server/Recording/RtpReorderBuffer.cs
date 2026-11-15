using System.Buffers;

namespace RtspRecServer.Server.Recording;

internal sealed class BufferedRtpPacket : IDisposable
{
    public required long Sequence { get; init; }
    public required byte[] Buffer { get; init; }
    public required int Length { get; init; }
    public required DateTime ReceivedAt { get; init; }

    public ReadOnlyMemory<byte> Payload => Buffer.AsMemory(0, Length);

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(Buffer);
    }
}

internal sealed class RtpReorderBuffer : IDisposable
{
    private static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(80);
    private const int DefaultMaxPackets = 256;

    private readonly TimeSpan _delay;
    private readonly int _maxPackets;
    private readonly SortedDictionary<long, BufferedRtpPacket> _packets = new();
    private long _referenceSequence;
    private long _nextSequence;
    private bool _initialized;
    private bool _hasReleased;
    private bool _disposed;

    public RtpReorderBuffer(TimeSpan? delay = null, int maxPackets = DefaultMaxPackets)
    {
        _delay = delay ?? DefaultDelay;
        _maxPackets = Math.Max(1, maxPackets);
    }

    public IReadOnlyList<BufferedRtpPacket> Push(
        ushort sequence,
        ReadOnlySpan<byte> payload,
        DateTime now)
    {
        if (_disposed || payload.Length == 0)
        {
            return [];
        }

        var extendedSequence = ExtendSequence(sequence);
        if (_hasReleased && extendedSequence < _nextSequence)
        {
            return [];
        }

        if (_packets.ContainsKey(extendedSequence))
        {
            return [];
        }

        var buffer = ArrayPool<byte>.Shared.Rent(payload.Length);
        payload.CopyTo(buffer);
        _packets[extendedSequence] = new BufferedRtpPacket
        {
            Sequence = extendedSequence,
            Buffer = buffer,
            Length = payload.Length,
            ReceivedAt = now
        };

        if (!_initialized)
        {
            _referenceSequence = extendedSequence;
            _nextSequence = extendedSequence;
            _initialized = true;
        }
        else
        {
            _referenceSequence = Math.Max(_referenceSequence, extendedSequence);
            if (!_hasReleased && extendedSequence < _nextSequence)
            {
                _nextSequence = extendedSequence;
            }
        }

        return DrainDue(now, force: false);
    }

    public IReadOnlyList<BufferedRtpPacket> Flush()
    {
        return DrainDue(DateTime.MaxValue, force: true);
    }

    private IReadOnlyList<BufferedRtpPacket> DrainDue(DateTime now, bool force)
    {
        if (_disposed || _packets.Count == 0)
        {
            return [];
        }

        var ready = new List<BufferedRtpPacket>();
        while (_packets.Count > 0)
        {
            var first = _packets.First();
            var packet = first.Value;
            var due = force ||
                      _packets.Count > _maxPackets ||
                      now - packet.ReceivedAt >= _delay;
            if (!due)
            {
                break;
            }

            _packets.Remove(first.Key);
            if (packet.Sequence < _nextSequence && _hasReleased)
            {
                packet.Dispose();
                continue;
            }

            _nextSequence = Math.Max(_nextSequence, packet.Sequence + 1);
            _hasReleased = true;
            ready.Add(packet);
        }

        return ready;
    }

    private long ExtendSequence(ushort sequence)
    {
        if (!_initialized)
        {
            return sequence;
        }

        var referenceLow = (ushort)(_referenceSequence & 0xFFFF);
        var difference = (int)sequence - referenceLow;
        if (difference < -32768)
        {
            difference += 65536;
        }
        else if (difference > 32767)
        {
            difference -= 65536;
        }

        return _referenceSequence + difference;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var packet in _packets.Values)
        {
            packet.Dispose();
        }

        _packets.Clear();
    }
}
