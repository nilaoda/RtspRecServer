using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net;
using System.Threading.Channels;
using RtspRecServer.Server.Recording.TsAnalysis;
using RtspRecServer.Shared;
using Serilog;

namespace RtspRecServer.Server.Recording;

internal sealed record DirectUdpSource(
    bool IsRtp,
    IPAddress GroupAddress,
    int Port,
    IPAddress? LocalAddress);

internal static class DirectUdpSourceParser
{
    public static bool TryParse(string url, out DirectUdpSource? source, out string error)
    {
        source = null;
        error = string.Empty;

        var value = url?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            error = "直播地址为空";
            return false;
        }

        var separator = value.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0)
        {
            error = "直播地址必须使用 rtp:// 或 udp://";
            return false;
        }

        var scheme = value[..separator].ToLowerInvariant();
        var isRtp = scheme == "rtp";
        if (!isRtp && scheme != "udp")
        {
            error = "直播地址必须使用 rtp:// 或 udp://";
            return false;
        }

        var rest = value[(separator + 3)..];
        var queryIndex = rest.IndexOf('?');
        var query = queryIndex >= 0 ? rest[(queryIndex + 1)..] : string.Empty;
        if (queryIndex >= 0)
        {
            rest = rest[..queryIndex];
        }

        var fragmentIndex = query.IndexOf('#');
        if (fragmentIndex >= 0)
        {
            query = query[..fragmentIndex];
        }

        var atIndex = rest.LastIndexOf('@');
        if (atIndex >= 0)
        {
            rest = rest[(atIndex + 1)..];
        }

        rest = rest.TrimEnd('/');

        if (rest.StartsWith("[", StringComparison.Ordinal))
        {
            error = "暂不支持 IPv6 组播地址";
            return false;
        }

        var colonIndex = rest.LastIndexOf(':');
        if (colonIndex <= 0 || colonIndex == rest.Length - 1)
        {
            error = "直播地址缺少端口";
            return false;
        }

        var host = rest[..colonIndex].Trim();
        var portText = rest[(colonIndex + 1)..].Trim();
        if (!IPAddress.TryParse(host, out var groupAddress) ||
            groupAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            error = "直播地址必须是 IPv4 地址";
            return false;
        }

        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            error = "直播端口无效";
            return false;
        }

        IPAddress? localAddress = null;
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equalsIndex = part.IndexOf('=');
            if (equalsIndex <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(part[..equalsIndex]).Trim();
            if (!key.Equals("localaddr", StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("localaddress", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var localValue = Uri.UnescapeDataString(part[(equalsIndex + 1)..]).Trim();
            if (!IPAddress.TryParse(localValue, out var parsed) ||
                parsed.AddressFamily != AddressFamily.InterNetwork)
            {
                error = "localaddr 必须是 IPv4 地址";
                return false;
            }

            localAddress = parsed.Equals(IPAddress.Any) ? null : parsed;
        }

        source = new DirectUdpSource(isRtp, groupAddress, port, localAddress);
        return true;
    }
}

public sealed class UdpLiveRecordingService : IRecordingService
{
    private static readonly TimeSpan InvalidDataTimeout = TimeSpan.FromSeconds(5);
    private readonly UdpReceiveHub _receiverHub = new();
    private readonly TimeSpan _idleTimeout;
    private readonly int _maxIdleRecoveries;

    public UdpLiveRecordingService()
        : this(TimeSpan.FromSeconds(30), maxIdleRecoveries: 5)
    {
    }

    internal UdpLiveRecordingService(TimeSpan idleTimeout, int maxIdleRecoveries)
    {
        _idleTimeout = idleTimeout;
        _maxIdleRecoveries = maxIdleRecoveries;
    }

    public async Task<RecordingResult> RecordAsync(
        RecordingTask task,
        string outputPath,
        TimeSpan? targetDuration,
        string recordingTransport,
        Action<RecordingProgress> onProgress,
        CancellationToken cancellationToken,
        Stream? output = null)
    {
        var bytesWritten = 0L;
        using var analysis = new TsLiveAnalysisSession(task.TaskName);

        RecordingResult Finish(bool success, string? error, long bytes)
        {
            return new RecordingResult
            {
                Success = success,
                ErrorMessage = error,
                BytesWritten = bytes,
                OutputPath = outputPath,
                TsCheck = analysis.Complete()
            };
        }

        if (!DirectUdpSourceParser.TryParse(task.Url, out var source, out var parseError) || source is null)
        {
            return Finish(false, parseError, bytesWritten);
        }

        try
        {
            if (output is null)
            {
                EnsureOutputPath(outputPath);
            }

            var groupAddress = UdpEndpointReceiver.IsMulticast(source.GroupAddress) ? source.GroupAddress : null;
            await using var ownedFile = output is null
                ? new FileStream(
                    outputPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous)
                : null;
            await using var buffered = ownedFile is null ? null : new BufferedStream(ownedFile, 128 * 1024);
            var sink = output ?? buffered!;

            var startedAt = DateTime.UtcNow;
            var lastProgress = startedAt;
            var validTsSeen = false;
            var idleRecoveries = 0;

            async Task WritePayloadAsync(ReadOnlyMemory<byte> payload, CancellationToken token)
            {
                await sink.WriteAsync(payload, token);
                bytesWritten += payload.Length;
                analysis.Append(payload.Span);

                var now = DateTime.UtcNow;
                if ((now - lastProgress).TotalSeconds >= 1.0)
                {
                    onProgress(new RecordingProgress { BytesWritten = bytesWritten, TsCheck = analysis.GetSummary() });
                    lastProgress = now;
                }
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                await using var subscription = _receiverHub.Subscribe(
                    groupAddress,
                    source.LocalAddress,
                    source.Port);
                using var rtpReorder = source.IsRtp ? new RtpReorderBuffer() : null;
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                uint? lastSsrc = null;
                var idleTimedOut = false;

                async Task FlushReorderedAsync()
                {
                    if (rtpReorder is null)
                    {
                        return;
                    }

                    foreach (var packet in rtpReorder.Flush())
                    {
                        try
                        {
                            await WritePayloadAsync(packet.Payload, CancellationToken.None);
                        }
                        finally
                        {
                            packet.Dispose();
                        }
                    }
                }

                while (!cancellationToken.IsCancellationRequested)
                {
                    idleCts.CancelAfter(_idleTimeout);

                    UdpDatagram datagram;
                    try
                    {
                        datagram = await subscription.Reader.ReadAsync(idleCts.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        Log.Warning("UDP 直播流超过 {Seconds} 秒无数据: {Url}", _idleTimeout.TotalSeconds, task.Url);
                        idleTimedOut = true;
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (ChannelClosedException ex)
                    {
                        Log.Error(ex, "UDP 接收循环已停止: {Url}", task.Url);
                        await FlushReorderedAsync();
                        return Finish(false, ex.InnerException?.Message ?? "UDP 接收已停止", bytesWritten);
                    }

                    using (datagram)
                    {
                        var datagramSpan = datagram.Payload.Span;
                        if (!TryResolveTsPayload(
                                datagramSpan,
                                source.IsRtp,
                                out var dataOffset,
                                out var dataLength,
                                out var rtpSequence,
                                out var ssrc))
                        {
                            if (!validTsSeen && DateTime.UtcNow - startedAt > InvalidDataTimeout)
                            {
                                Log.Warning("UDP 直播源未返回有效的 MPEG-TS 数据: {Url}", task.Url);
                                await FlushReorderedAsync();
                                return Finish(false, "直播源返回内容不是 MPEG-TS", bytesWritten);
                            }

                            continue;
                        }

                        validTsSeen = true;
                        if (ssrc.HasValue)
                        {
                            if (lastSsrc.HasValue && ssrc.Value != lastSsrc.Value)
                            {
                                Log.Information(
                                    "RTP SSRC 变化（{Old} -> {New}），重置乱序缓冲: {Url}",
                                    lastSsrc.Value,
                                    ssrc.Value,
                                    task.Url);
                                await FlushReorderedAsync();
                                rtpReorder?.Reset();
                            }

                            lastSsrc = ssrc;
                        }

                        if (rtpSequence.HasValue && rtpReorder is not null)
                        {
                            var ready = rtpReorder.Push(
                                rtpSequence.Value,
                                datagram.Payload.Span.Slice(dataOffset, dataLength),
                                DateTime.UtcNow);
                            foreach (var packet in ready)
                            {
                                try
                                {
                                    await WritePayloadAsync(packet.Payload, cancellationToken);
                                }
                                finally
                                {
                                    packet.Dispose();
                                }
                            }
                        }
                        else
                        {
                            await WritePayloadAsync(datagram.Payload.Slice(dataOffset, dataLength), cancellationToken);
                        }
                    }
                }

                await FlushReorderedAsync();

                if (!idleTimedOut)
                {
                    break;
                }

                idleRecoveries++;
                if (idleRecoveries > _maxIdleRecoveries)
                {
                    onProgress(new RecordingProgress { BytesWritten = bytesWritten, TsCheck = analysis.GetSummary() });
                    return Finish(false, "直播流无数据超时", bytesWritten);
                }

                var retryDelay = TimeSpan.FromSeconds(Math.Min(2 * idleRecoveries, 10));
                Log.Warning(
                    "UDP 直播流中断，{Delay} 秒后重新加入（第 {Attempt}/{Max} 次）: {Url}",
                    retryDelay.TotalSeconds,
                    idleRecoveries,
                    _maxIdleRecoveries,
                    task.Url);
                onProgress(new RecordingProgress { BytesWritten = bytesWritten, TsCheck = analysis.GetSummary() });
                try
                {
                    await Task.Delay(retryDelay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            await sink.FlushAsync(CancellationToken.None);
            onProgress(new RecordingProgress { BytesWritten = bytesWritten, TsCheck = analysis.GetSummary() });
            return Finish(!cancellationToken.IsCancellationRequested, null, bytesWritten);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            onProgress(new RecordingProgress { BytesWritten = bytesWritten, TsCheck = analysis.GetSummary() });
            return Finish(false, null, bytesWritten);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UDP 直播录制失败: {Url}", task.Url);
            return Finish(false, ex.Message, bytesWritten);
        }
    }

    private static bool TryResolveTsPayload(
        ReadOnlySpan<byte> datagram,
        bool isRtp,
        out int offset,
        out int length,
        out ushort? rtpSequence,
        out uint? ssrc)
    {
        offset = 0;
        length = datagram.Length;
        rtpSequence = null;
        ssrc = null;
        if (length == 0)
        {
            return false;
        }

        if (isRtp && datagram[0] != 0x47)
        {
            if ((datagram[0] >> 6) != 2 ||
                !RtpPayloadParser.TryGetRtpHeaderLength(datagram, out var headerLength) ||
                headerLength >= length)
            {
                return false;
            }

            rtpSequence = (ushort)((datagram[2] << 8) | datagram[3]);
            ssrc = BinaryPrimitives.ReadUInt32BigEndian(datagram.Slice(8, 4));
            offset = headerLength;
            length -= headerLength;

            if ((datagram[0] & 0x20) != 0)
            {
                var padding = datagram[^1];
                if (padding == 0 || padding >= length)
                {
                    return false;
                }

                length -= padding;
            }
        }

        return length > 0 && MpegTsValidator.LooksLikeMpegTs(datagram.Slice(offset, length));
    }

    private static void EnsureOutputPath(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
