using System.Buffers;
using System.Net;
using System.Net.Sockets;
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
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InvalidDataTimeout = TimeSpan.FromSeconds(5);
    private const int ReceiveBufferBytes = 8 * 1024 * 1024;

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

            using var socket = CreateSocket(source);
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
            var buffer = ArrayPool<byte>.Shared.Rent(65536);

            try
            {
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                EndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
                var startedAt = DateTime.UtcNow;
                var lastProgress = startedAt;
                var validTsSeen = false;
                var foreignGroupPackets = 0;

                while (!cancellationToken.IsCancellationRequested)
                {
                    idleCts.CancelAfter(IdleTimeout);

                    SocketReceiveMessageFromResult received;
                    try
                    {
                        received = await socket.ReceiveMessageFromAsync(
                            buffer.AsMemory(0, buffer.Length),
                            SocketFlags.None,
                            remoteEndPoint,
                            idleCts.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        Log.Warning("UDP 直播流超过 {Seconds} 秒无数据: {Url}", IdleTimeout.TotalSeconds, task.Url);
                        onProgress(new RecordingProgress { BytesWritten = bytesWritten, TsCheck = analysis.GetSummary() });
                        return Finish(false, "直播流无数据超时", bytesWritten);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (received.ReceivedBytes <= 0)
                    {
                        continue;
                    }

                    var destination = received.PacketInformation.Address;
                    if (!IsExpectedMulticastDestination(source.GroupAddress, destination))
                    {
                        foreignGroupPackets++;
                        if (foreignGroupPackets == 1)
                        {
                            Log.Warning(
                                "UDP 共享端口收到其他组播组数据，已忽略：期望 {ExpectedGroup}，实际 {ActualGroup}。地址: {Url}",
                                source.GroupAddress,
                                destination,
                                task.Url);
                        }

                        continue;
                    }

                    var datagram = buffer.AsSpan(0, received.ReceivedBytes);
                    if (!TryResolveTsPayload(datagram, source.IsRtp, out var dataOffset, out var dataLength))
                    {
                        if (!validTsSeen && DateTime.UtcNow - startedAt > InvalidDataTimeout)
                        {
                            Log.Warning("UDP 直播源未返回有效的 MPEG-TS 数据: {Url}", task.Url);
                            return Finish(false, "直播源返回内容不是 MPEG-TS", bytesWritten);
                        }

                        continue;
                    }

                    validTsSeen = true;
                    await sink.WriteAsync(buffer.AsMemory(dataOffset, dataLength), cancellationToken);
                    bytesWritten += dataLength;
                    analysis.Append(buffer.AsSpan(dataOffset, dataLength));

                    var now = DateTime.UtcNow;
                    if ((now - lastProgress).TotalSeconds >= 1.0)
                    {
                        onProgress(new RecordingProgress { BytesWritten = bytesWritten, TsCheck = analysis.GetSummary() });
                        lastProgress = now;
                    }
                }

                await sink.FlushAsync(CancellationToken.None);
                onProgress(new RecordingProgress { BytesWritten = bytesWritten, TsCheck = analysis.GetSummary() });
                return Finish(!cancellationToken.IsCancellationRequested, null, bytesWritten);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
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

    internal static Socket CreateSocket(DirectUdpSource source)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            // IPTV 常见多个组播频道共用同一端口，多个录制任务需要同时绑定。
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            try
            {
                // 共享端口时用目标地址过滤其他组播组，避免多路流混入同一 TS。
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            }
            catch (SocketException ex)
            {
                Log.Warning(ex, "启用 UDP 目标地址信息失败，共享端口上的其他组播组可能无法过滤");
            }

            try
            {
                socket.ReceiveBufferSize = ReceiveBufferBytes;
            }
            catch (SocketException ex)
            {
                Log.Warning(ex, "设置 UDP 接收缓冲区失败，将使用系统默认值");
            }

            var bindAddress = source.LocalAddress ?? IPAddress.Any;
            try
            {
                socket.Bind(new IPEndPoint(bindAddress, source.Port));
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                throw new InvalidOperationException(
                    $"UDP 端口 {source.Port} 已被其他程序占用，请关闭占用该端口的播放器或进程后重试",
                    ex);
            }

            if (IsMulticast(source.GroupAddress))
            {
                var option = source.LocalAddress is null
                    ? new MulticastOption(source.GroupAddress)
                    : new MulticastOption(source.GroupAddress, source.LocalAddress);
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, option);
                Log.Information(
                    "UDP 直播已加入组播组 {Group}:{Port}，本机地址: {LocalAddress}",
                    source.GroupAddress,
                    source.Port,
                    source.LocalAddress?.ToString() ?? "默认");
            }

            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool IsMulticast(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] is >= 224 and <= 239;
    }

    internal static bool IsExpectedMulticastDestination(IPAddress group, IPAddress? destination)
    {
        if (!IsMulticast(group))
        {
            return true;
        }

        // 部分平台/配置拿不到目标地址，保留兼容行为；拿到时必须与当前组播组一致。
        return destination is null ||
               destination.Equals(IPAddress.Any) ||
               destination.Equals(IPAddress.IPv6Any) ||
               destination.Equals(group);
    }

    private static bool TryResolveTsPayload(
        ReadOnlySpan<byte> datagram,
        bool isRtp,
        out int offset,
        out int length)
    {
        offset = 0;
        length = datagram.Length;
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
