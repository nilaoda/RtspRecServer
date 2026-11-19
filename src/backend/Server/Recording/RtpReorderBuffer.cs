using System.Diagnostics;

namespace RtspRecServer.Server.Recording;

internal readonly record struct BufferedRtpPacket(
    long Sequence, UdpDatagram Datagram, ReadOnlyMemory<byte> Payload) : IDisposable
{
    public void Dispose() => Datagram.Dispose();
}

internal sealed class RtpReorderBuffer : IDisposable
{
    private static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(80);
    private const int DefaultMaxPackets = 512;

    private readonly TimeSpan _delay;
    private readonly int _maxPackets;
    private readonly SortedDictionary<long, BufferedRtpPacket> _packets = new();
    private long _referenceSequence;
    private long _nextSequence;
    private long _startupTimestamp;
    private bool _initialized;
    private bool _hasReleased;
    private bool _disposed;

    public long MissingSequences { get; private set; }
    public long OldPackets { get; private set; }

    public RtpReorderBuffer(TimeSpan? delay = null, int maxPackets = DefaultMaxPackets)
    {
        _delay = delay ?? DefaultDelay;
        _maxPackets = Math.Max(1, maxPackets);
    }

    public bool TryReleaseInOrder(ushort sequence)
    {
        if (_disposed || !_hasReleased || _packets.Count != 0 || sequence != unchecked((ushort)_nextSequence))
        {
            return false;
        }

        _referenceSequence = _nextSequence++;
        return true;
    }

    public IReadOnlyList<BufferedRtpPacket> Push(
        ushort sequence,
        UdpDatagram datagram,
        ReadOnlyMemory<byte> payload)
    {
        if (_disposed || payload.Length == 0)
        {
            return [];
        }

        var extendedSequence = ExtendSequence(sequence);
        if ((_hasReleased && extendedSequence < _nextSequence) || _packets.ContainsKey(extendedSequence))
        {
            OldPackets++;
            return ReleaseDue(datagram.ReceivedTimestamp);
        }

        datagram.Retain();
        _packets.Add(extendedSequence, new BufferedRtpPacket(extendedSequence, datagram, payload));

        if (!_initialized)
        {
            _referenceSequence = extendedSequence;
            _nextSequence = extendedSequence;
            _startupTimestamp = datagram.ReceivedTimestamp;
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

        return ReleaseDue(datagram.ReceivedTimestamp);
    }

    public TimeSpan? GetReleaseDelay(long timestamp)
    {
        return _packets.Count == 0 ? null : DelayRemaining(_packets.First().Value, timestamp);
    }

    public IReadOnlyList<BufferedRtpPacket> ReleaseDue(long timestamp) => DrainDue(timestamp, force: false);

    public IReadOnlyList<BufferedRtpPacket> Flush()
    {
        return DrainDue(0, force: true);
    }

    private IReadOnlyList<BufferedRtpPacket> DrainDue(long timestamp, bool force)
    {
        if (_disposed || _packets.Count == 0)
        {
            return [];
        }

        List<BufferedRtpPacket>? ready = null;
        while (_packets.Count > 0)
        {
            var first = _packets.First();
            var packet = first.Value;
            var due = force ||
                      (_hasReleased && packet.Sequence == _nextSequence) ||
                      _packets.Count > _maxPackets ||
                      DelayRemaining(packet, timestamp) <= TimeSpan.Zero;
            if (!due)
            {
                break;
            }

            _packets.Remove(first.Key);
            if (_hasReleased)
            {
                MissingSequences += packet.Sequence - _nextSequence;
            }

            _nextSequence = packet.Sequence + 1;
            _hasReleased = true;
            (ready ??= []).Add(packet);
        }

        return ready ?? [];
    }

    private TimeSpan DelayRemaining(BufferedRtpPacket packet, long timestamp) =>
        _delay - Stopwatch.GetElapsedTime(_hasReleased ? packet.Datagram.ReceivedTimestamp : _startupTimestamp, timestamp);

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

    public void Reset()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var packet in _packets.Values)
        {
            packet.Dispose();
        }

        _packets.Clear();
        MissingSequences = 0;
        OldPackets = 0;
        _referenceSequence = 0;
        _nextSequence = 0;
        _initialized = false;
        _hasReleased = false;
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
