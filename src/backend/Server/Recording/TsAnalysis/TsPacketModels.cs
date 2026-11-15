namespace RtspRecServer.Server.Recording.TsAnalysis;

internal enum TsPacketParseError : byte
{
    None,
    InvalidSize,
    InvalidSyncByte,
    ReservedAdaptationControl,
    InvalidAdaptationLength
}

internal readonly struct TsPacketInfo
{
    private readonly uint _header;
    private readonly byte _payloadOffset;
    private readonly byte _adaptationLength;
    private readonly byte _adaptationFlags;
    private readonly byte _error;

    internal TsPacketInfo(
        TsPacketParseError error,
        uint header,
        int payloadOffset,
        int adaptationLength,
        byte adaptationFlags)
    {
        _header = header;
        _payloadOffset = (byte)payloadOffset;
        _adaptationLength = (byte)adaptationLength;
        _adaptationFlags = adaptationFlags;
        _error = (byte)error;
    }

    public TsPacketParseError Error => (TsPacketParseError)_error;
    public int Pid => (int)(_header >> 8) & 0x1FFF;
    public int ContinuityCounter => (int)_header & 0x0F;
    public int PayloadOffset => _payloadOffset;
    public bool PayloadStart => (_header & 0x0040_0000) != 0;
    public bool TransportError => (_header & 0x0080_0000) != 0;
    public bool TransportPriority => (_header & 0x0020_0000) != 0;
    public int ScramblingControl => (int)(_header >> 6) & 0x03;
    public int AdaptationControl => (int)(_header >> 4) & 0x03;
    public int AdaptationLength => _adaptationLength;
    public byte AdaptationFlags => _adaptationFlags;
    public bool IsValid => Error == TsPacketParseError.None;
    public bool HasPayload => (AdaptationControl & 0x01) != 0 && PayloadOffset < TsUtil.TsPacketSize;
    public bool HasAdaptation => (AdaptationControl & 0x02) != 0;
    public bool Discontinuity => HasAdaptation && AdaptationLength > 0 && (AdaptationFlags & 0x80) != 0;
    public bool RandomAccess => HasAdaptation && AdaptationLength > 0 && (AdaptationFlags & 0x40) != 0;
    public bool ElementaryStreamPriority => HasAdaptation && AdaptationLength > 0 && (AdaptationFlags & 0x20) != 0;
    public bool PcrFlag => HasAdaptation && AdaptationLength > 0 && (AdaptationFlags & 0x10) != 0;
    public bool OpcrFlag => HasAdaptation && AdaptationLength > 0 && (AdaptationFlags & 0x08) != 0;
    public bool HasPcr => PcrFlag && AdaptationLength >= 7;
    public bool HasOpcr => OpcrFlag && AdaptationLength >= (PcrFlag ? 13 : 7);
    public bool HasSplicingPoint => HasAdaptation && AdaptationLength > 0 && (AdaptationFlags & 0x04) != 0;
    public bool HasPrivateData => HasAdaptation && AdaptationLength > 0 && (AdaptationFlags & 0x02) != 0;
    public bool HasAdaptationExtension => HasAdaptation && AdaptationLength > 0 && (AdaptationFlags & 0x01) != 0;
}
