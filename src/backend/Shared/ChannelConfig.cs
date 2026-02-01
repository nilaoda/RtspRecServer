namespace RtspRecServer.Shared;

public sealed record ChannelConfig
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
}
