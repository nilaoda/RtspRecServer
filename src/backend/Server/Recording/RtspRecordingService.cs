using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using RtspRecServer.Shared;
using Serilog;

namespace RtspRecServer.Server.Recording;

public sealed record RecordingResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public long BytesWritten { get; init; }
    public string OutputPath { get; init; } = string.Empty;
    public double? PcrElapsedSeconds { get; init; }
}

public sealed class RtspRecordingService : IRecordingService
{
    public async Task<RecordingResult> RecordAsync(
        RecordingTask task,
        string outputPath,
        TimeSpan? targetDuration,
        string recordingTransport,
        Action<RecordingProgress> onProgress,
        CancellationToken cancellationToken)
    {
        var recorder = new RtspStreamRecorder(task.Url, outputPath, recordingTransport);
        try
        {
            var result = await recorder.RunAsync(targetDuration, onProgress, cancellationToken);
            return new RecordingResult
            {
                Success = !cancellationToken.IsCancellationRequested,
                BytesWritten = result.BytesWritten,
                OutputPath = outputPath,
                PcrElapsedSeconds = result.PcrElapsedSeconds
            };
        }
        catch (Exception ex)
        {
            return new RecordingResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                BytesWritten = recorder.BytesWritten,
                OutputPath = outputPath
            };
        }
        finally
        {
            recorder.Close();
        }
    }
}

internal sealed class RtspStreamRecorder
{
    private readonly string _url;
    private readonly string _outputPath;
    private readonly string _recordingTransport;
    private readonly bool _useRtp;
    private TcpClient? _client;
    private int _seq = 2;
    public long BytesWritten { get; private set; }

    public RtspStreamRecorder(string url, string outputPath, string recordingTransport)
    {
        _url = url;
        _outputPath = outputPath;
        _recordingTransport = recordingTransport;
        _useRtp = string.Equals(_recordingTransport, "MP2T/RTP/TCP", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<RecordingResult> RunAsync(TimeSpan? targetDuration, Action<RecordingProgress> onProgress, CancellationToken cancellationToken)
    {
        Connect();
        var stream = _client!.GetStream();
        // 增加超时时间到30秒，避免网络波动导致录制中断
        stream.ReadTimeout = 30000;
        stream.WriteTimeout = 30000;
        
        var startTime = DateTime.UtcNow;
        var packetCount = 0;
        var errorCount = 0;
        var targetTicks = targetDuration.HasValue && targetDuration.Value > TimeSpan.Zero
            ? (long)Math.Round(targetDuration.Value.TotalSeconds * 27_000_000D)
            : 0L;
        long? firstPcrTicks = null;
        long? lastPcrTicks = null;
        var stoppedByPcr = false;
        
        // 性能优化：批量写入和每秒进度上报
        const int BatchSize = 16 * 1024; // 16KB批处理
        var batchBuffer = ArrayPool<byte>.Shared.Rent(BatchSize);
        var batchOffset = 0;
        var lastProgressTime = DateTime.UtcNow; // 记录上次进度上报时间

        var describe = RtspMessage.BuildDescribe(_url, _seq++);
        await SendAsync(stream, describe, cancellationToken);
        var resp = ReceiveHead(stream);

        if (Regex.IsMatch(resp, "Location: (.*)"))
        {
            var newUrl = Regex.Match(resp, "Location: (.*)").Groups[1].Value.Trim();
            Log.Information("RTSP重定向到新URL：{NewUrl} (原URL: {OldUrl})", newUrl, _url);
            _client.Close();
            _client = null;
            _seq = 2;
            return await new RtspStreamRecorder(newUrl, _outputPath, _recordingTransport).RunAsync(targetDuration, onProgress, cancellationToken);
        }

        if (!resp.Contains("200 OK"))
        {
            var statusLine = resp.Split("\r\n").FirstOrDefault() ?? "Unknown";
            Log.Error("RTSP DESCRIBE失败。状态行: {StatusLine}, URL: {Url}, 完整响应: {Response}", statusLine, _url, resp);
            throw new InvalidOperationException($"RTSP DESCRIBE失败 ({statusLine})，请检查URL或服务器状态");
        }

        var setup = RtspMessage.BuildSetup(_url, _seq++, _recordingTransport);
        await SendAsync(stream, setup, cancellationToken);
        var setupResp = ReceiveHead(stream);

        if (!setupResp.Contains("200 OK"))
        {
            var statusLine = setupResp.Split("\r\n").FirstOrDefault() ?? "Unknown";
            Log.Error("RTSP SETUP失败。状态行: {StatusLine}, URL: {Url}, 响应内容: {Response}", statusLine, _url, setupResp);
            throw new InvalidOperationException($"RTSP SETUP失败 ({statusLine})，可能不支持 {_recordingTransport} 传输模式");
        }

        var play = RtspMessage.BuildPlay(_url, _seq++);
        await SendAsync(stream, play, cancellationToken);
        var playResp = ReceiveHead(stream);

        if (!playResp.Contains("200 OK"))
        {
            var statusLine = playResp.Split("\r\n").FirstOrDefault() ?? "Unknown";
            Log.Error("RTSP PLAY失败。状态行: {StatusLine}, URL: {Url}, 响应内容: {Response}", statusLine, _url, playResp);
            throw new InvalidOperationException($"RTSP PLAY失败 ({statusLine})，服务器拒绝播放请求");
        }
        _ = ReceiveHead(stream);

        EnsureOutputPath(_outputPath);
        await using var fileStream = new FileStream(_outputPath, FileMode.Create, FileAccess.Write, FileShare.Read, 8192, FileOptions.Asynchronous);
        await using var buffered = new BufferedStream(fileStream, 8192);

        var header = new byte[4];
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!await TryReadExactlyAsync(stream, header, cancellationToken))
            {
                Log.Warning("RTSP录制：读取头数据失败，录制停止。已录制 {BytesWritten} 字节，耗时 {Duration} 秒", 
                    BytesWritten, (DateTime.UtcNow - startTime).TotalSeconds);
                break;
            }

            if (header[0] != 0x24)
            {
                errorCount++;
                continue;
            }

            var length = (header[2] << 8) | header[3];
            if (length <= 0)
            {
                Log.Warning("RTSP录制：无效的数据包长度 {Length}", length);
                continue;
            }

            var payload = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                if (!await TryReadExactlyAsync(stream, payload.AsMemory(0, length), cancellationToken))
                {
                    Log.Warning("RTSP录制：读取负载数据失败，录制停止。已录制 {BytesWritten} 字节，耗时 {Duration} 秒", 
                        BytesWritten, (DateTime.UtcNow - startTime).TotalSeconds);
                    break;
                }

                if (header[1] == 0)
                {
                    packetCount++;
                    var payloadMemory = payload.AsMemory(0, length);
                    if (!TryResolvePayloadSegment(payloadMemory.Span, _useRtp, ref firstPcrTicks, ref lastPcrTicks, targetTicks, packetCount, out var dataOffset, out var dataLength, out var shouldStop))
                    {
                        continue;
                    }

                    if (dataLength > BatchSize)
                    {
                        if (batchOffset > 0)
                        {
                            await buffered.WriteAsync(batchBuffer.AsMemory(0, batchOffset), cancellationToken);
                            batchOffset = 0;
                        }
                        await buffered.WriteAsync(payloadMemory.Slice(dataOffset, dataLength), cancellationToken);
                        BytesWritten += dataLength;
                    }
                    else
                    {
                        if (batchOffset + dataLength > BatchSize)
                        {
                            await buffered.WriteAsync(batchBuffer.AsMemory(0, batchOffset), cancellationToken);
                            batchOffset = 0;
                        }

                        payloadMemory.Span.Slice(dataOffset, dataLength).CopyTo(batchBuffer.AsSpan(batchOffset));
                        batchOffset += dataLength;
                        BytesWritten += dataLength;
                    }
                    
                    if (shouldStop)
                    {
                        stoppedByPcr = true;
                        break;
                    }
                    
                    // 每秒进度上报：检查是否需要上报进度
                    var now = DateTime.UtcNow;
                    if ((now - lastProgressTime).TotalSeconds >= 1.0)
                    {
                        double? currentPcrElapsedSeconds = null;
                        if (firstPcrTicks.HasValue && lastPcrTicks.HasValue)
                        {
                            currentPcrElapsedSeconds = CalculateElapsedSeconds(firstPcrTicks.Value, lastPcrTicks.Value);
                        }
                        onProgress(new RecordingProgress 
                        { 
                            BytesWritten = BytesWritten, 
                            PcrElapsedSeconds = currentPcrElapsedSeconds 
                        });
                        lastProgressTime = now;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }

        // 写入剩余数据
        if (batchOffset > 0)
        {
            await buffered.WriteAsync(batchBuffer.AsMemory(0, batchOffset), cancellationToken);
        }
        ArrayPool<byte>.Shared.Return(batchBuffer);
        
        await buffered.FlushAsync(cancellationToken);
        
        // 最终进度报告
        double? finalPcrElapsedSeconds = null;
        if (firstPcrTicks.HasValue && lastPcrTicks.HasValue)
        {
            finalPcrElapsedSeconds = CalculateElapsedSeconds(firstPcrTicks.Value, lastPcrTicks.Value);
        }
        onProgress(new RecordingProgress 
        { 
            BytesWritten = BytesWritten, 
            PcrElapsedSeconds = finalPcrElapsedSeconds 
        });

        var duration = DateTime.UtcNow - startTime;
        if (stoppedByPcr)
        {
            var elapsedSeconds = lastPcrTicks.HasValue && firstPcrTicks.HasValue
                ? CalculateElapsedSeconds(firstPcrTicks.Value, lastPcrTicks.Value)
                : 0;
            Log.Information("RTSP录制达到PCR时长限制，已录制 {Seconds} 秒，任务目标 {TargetSeconds} 秒", elapsedSeconds, targetDuration?.TotalSeconds ?? 0);
        }
        Log.Information("RTSP录制结束：总字节数 {BytesWritten}，总数据包数 {PacketCount}，总耗时 {Duration} 秒，平均速率 {Rate} KB/s", 
            BytesWritten, packetCount, duration.TotalSeconds, 
            duration.TotalSeconds > 0 ? BytesWritten / 1024.0 / duration.TotalSeconds : 0);

        return new RecordingResult
        {
            Success = true,
            BytesWritten = BytesWritten,
            PcrElapsedSeconds = finalPcrElapsedSeconds,
            OutputPath = _outputPath
        };
    }

    private static bool TryUpdatePcrTicks(ReadOnlySpan<byte> payload, ref long? first, ref long? last, out long elapsedTicks)
    {
        elapsedTicks = 0;
        if (payload.Length < 188)
        {
            return false;
        }

        // 性能优化：减少边界检查，使用unsafe代码提高性能
        ref byte payloadRef = ref MemoryMarshal.GetReference(payload);
        var offset = 0;
        
        while (offset + 188 <= payload.Length)
        {
            // 快速检查同步字节
            if (Unsafe.Add(ref payloadRef, offset) != 0x47)
            {
                offset++;
                continue;
            }

            if (TryGetPcrTicks(payload.Slice(offset, 188), out var pcrTicks))
            {
                first ??= pcrTicks;
                last = pcrTicks;
                if (first.HasValue && last.HasValue)
                {
                    elapsedTicks = CalculateElapsedTicks(first.Value, last.Value);
                    return true;
                }
            }

            offset += 188;
        }

        return false;
    }

    private static bool TryGetPcrTicks(ReadOnlySpan<byte> packet, out long ticks)
    {
        ticks = 0;
        if (packet.Length < 188 || packet[0] != 0x47)
        {
            return false;
        }

        // 性能优化：使用位操作快速检查
        var adaptationFieldControl = (packet[3] >> 4) & 0x3;
        if (adaptationFieldControl != 2 && adaptationFieldControl != 3)
        {
            return false;
        }

        var adaptationLength = packet[4];
        if (adaptationLength < 7 || 5 + adaptationLength > 188)
        {
            return false;
        }

        // 快速检查PCR标志
        if ((packet[5] & 0x10) == 0)
        {
            return false;
        }

        // 性能优化：使用BitConverter和位操作快速读取PCR
        var baseIndex = 6;
        var pcrBase = ((long)packet[baseIndex] << 25)
                      | ((long)packet[baseIndex + 1] << 17)
                      | ((long)packet[baseIndex + 2] << 9)
                      | ((long)packet[baseIndex + 3] << 1)
                      | ((long)(packet[baseIndex + 4] >> 7));
        var pcrExt = ((packet[baseIndex + 4] & 0x1) << 8) | packet[baseIndex + 5];

        ticks = pcrBase * 300L + pcrExt;
        return true;
    }

    private static long CalculateElapsedTicks(long firstTicks, long lastTicks)
    {
        var wrap = (1L << 33) * 300L;
        return lastTicks >= firstTicks ? lastTicks - firstTicks : lastTicks + wrap - firstTicks;
    }

    private static double CalculateElapsedSeconds(long firstTicks, long lastTicks)
    {
        return CalculateElapsedTicks(firstTicks, lastTicks) / 27_000_000D;
    }

    private static bool TryResolvePayloadSegment(
        ReadOnlySpan<byte> payload,
        bool useRtp,
        ref long? firstPcrTicks,
        ref long? lastPcrTicks,
        long targetTicks,
        int packetCount,
        out int dataOffset,
        out int dataLength,
        out bool shouldStop)
    {
        dataOffset = 0;
        dataLength = payload.Length;
        shouldStop = false;

        if (useRtp && payload.Length > 0 && payload[0] != 0x47)
        {
            if (!TryGetRtpHeaderLength(payload, out var rtpHeaderLength) || rtpHeaderLength >= payload.Length)
            {
                return false;
            }
            dataOffset = rtpHeaderLength;
            dataLength = payload.Length - rtpHeaderLength;
        }

        if (dataLength <= 0)
        {
            return false;
        }

        if (targetTicks > 0 && (packetCount % 10 == 0))
        {
            if (TryUpdatePcrTicks(payload.Slice(dataOffset, dataLength), ref firstPcrTicks, ref lastPcrTicks, out var elapsedTicks) &&
                elapsedTicks >= targetTicks)
            {
                shouldStop = true;
            }
        }

        return true;
    }

    private static bool TryGetRtpHeaderLength(ReadOnlySpan<byte> payload, out int headerLength)
    {
        headerLength = 0;
        if (payload.Length < 12)
        {
            return false;
        }

        var csrcCount = payload[0] & 0x0F;
        headerLength = 12 + csrcCount * 4;
        if (payload.Length < headerLength)
        {
            return false;
        }

        if ((payload[0] & 0x10) != 0)
        {
            if (payload.Length < headerLength + 4)
            {
                return false;
            }

            var extLength = (payload[headerLength + 2] << 8) | payload[headerLength + 3];
            headerLength += 4 + extLength * 4;
            if (payload.Length < headerLength)
            {
                return false;
            }
        }

        return true;
    }


    public void Close()
    {
        if (_client != null && _client.Connected)
        {
            try
            {
                var stream = _client.GetStream();
                var teardown = RtspMessage.BuildTeardown(_url, _seq);
                stream.Write(Encoding.UTF8.GetBytes(teardown));
            }
            catch
            {
            }
            finally
            {
                _client.Close();
            }
        }
    }

    private void Connect()
    {
        try
        {
            var uri = new Uri(_url);
            _client = new TcpClient();
            // 设置连接超时
            var connectTask = _client.ConnectAsync(uri.Host, uri.Port > 0 ? uri.Port : 554);
            if (!connectTask.Wait(10000)) // 10秒超时
            {
                throw new TimeoutException($"连接到 RTSP 服务器 {uri.Host} 超时 (10s)");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "连接 RTSP 服务器失败: {Url}", _url);
            throw new InvalidOperationException($"无法连接到 RTSP 服务器: {ex.Message}", ex);
        }
    }

    private static async Task SendAsync(Stream stream, string message, CancellationToken cancellationToken)
    {
        var data = Encoding.UTF8.GetBytes(message);
        await stream.WriteAsync(data, cancellationToken);
    }

    private static string ReceiveHead(Stream stream)
    {
        var builder = new StringBuilder();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        string? line;
        while ((line = reader.ReadLine()) != null && line.Length > 0)
        {
            builder.AppendLine(line);
        }

        var contentMatch = Regex.Match(builder.ToString(), "Content-Length: (.*)");
        if (contentMatch.Success)
        {
            var size = Convert.ToInt32(contentMatch.Groups[1].Value);
            var buffer = new char[size];
            reader.ReadBlock(buffer, 0, size);
            builder.AppendLine();
            builder.Append(buffer);
        }

        return builder.ToString();
    }

    private static void EnsureOutputPath(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static async Task<bool> TryReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private static async Task<bool> TryReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.Slice(offset, buffer.Length - offset), cancellationToken);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}

internal static class RtspMessage
{
    private const string UserAgent = "Lavf58.20.100";

    public static string BuildDescribe(string url, int seq)
    {
        return $"DESCRIBE {url} RTSP/1.0\r\n" +
               $"CSeq: {seq}\r\n" +
               $"User-Agent: {UserAgent}\r\n" +
               "Accept: application/sdp\r\n\r\n";
    }

    public static string BuildSetup(string url, int seq, string recordingTransport)
    {
        var transport = string.Equals(recordingTransport, "MP2T/RTP/TCP", StringComparison.OrdinalIgnoreCase)
            ? "MP2T/RTP/TCP;unicast;interleaved=0-1"
            : "MP2T/TCP;unicast;interleaved=0-1";
        return $"SETUP {url} RTSP/1.0\r\n" +
               $"Transport: {transport}\r\n" +
               $"CSeq: {seq}\r\n" +
               $"User-Agent: {UserAgent}\r\n\r\n";
    }

    public static string BuildPlay(string url, int seq)
    {
        return $"PLAY {url} RTSP/1.0\r\n" +
               "Range: npt=0.000-\r\n" +
               $"CSeq: {seq}\r\n" +
               $"User-Agent: {UserAgent}\r\n\r\n";
    }

    public static string BuildTeardown(string url, int seq)
    {
        return $"TEARDOWN {url} RTSP/1.0\r\n" +
               $"CSeq: {seq}\r\n" +
               $"User-Agent: {UserAgent}\r\n\r\n";
    }
}
