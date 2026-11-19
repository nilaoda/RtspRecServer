using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using RtspRecServer.Server.Recording.TsAnalysis;
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
    public TsCheckSummary? TsCheck { get; init; }
}

public sealed class RtspRecordingService : IRecordingService
{
    public async Task<RecordingResult> RecordAsync(
        RecordingTask task,
        string outputPath,
        TimeSpan? targetDuration,
        string recordingTransport,
        Action<RecordingProgress> onProgress,
        CancellationToken cancellationToken,
        Stream? output = null)
    {
        var recorder = new RtspStreamRecorder(task.Url, outputPath, recordingTransport, task.TaskName, output);
        try
        {
            var result = await recorder.RunAsync(targetDuration, onProgress, cancellationToken);
            return new RecordingResult
            {
                Success = !cancellationToken.IsCancellationRequested,
                BytesWritten = result.BytesWritten,
                OutputPath = outputPath,
                PcrElapsedSeconds = result.PcrElapsedSeconds,
                TsCheck = result.TsCheck
            };
        }
        catch (Exception ex)
        {
            return new RecordingResult
            {
                Success = false,
                ErrorMessage = ex is OperationCanceledException && cancellationToken.IsCancellationRequested ? null : ex.Message,
                BytesWritten = recorder.BytesWritten,
                OutputPath = outputPath,
                TsCheck = recorder.CompleteTsCheck()
            };
        }
        finally
        {
            await recorder.CloseAsync();
            recorder.DisposeTsCheck();
        }
    }
}

internal sealed class RtspStreamRecorder
{
    private readonly string _url;
    private readonly string _outputPath;
    private readonly string _recordingTransport;
    private readonly bool _useRtp;
    private readonly string _displayName;
    private readonly Stream? _output;
    private TcpClient? _client;
    private int _seq = 2;
    private TsLiveAnalysisSession? _analysis;
    public long BytesWritten { get; private set; }

    public RtspStreamRecorder(string url, string outputPath, string recordingTransport, string displayName, Stream? output = null)
    {
        _url = url;
        _outputPath = outputPath;
        _recordingTransport = recordingTransport;
        _displayName = displayName;
        _output = output;
        _useRtp = string.Equals(_recordingTransport, "MP2T/RTP/TCP", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<RecordingResult> RunAsync(TimeSpan? targetDuration, Action<RecordingProgress> onProgress, CancellationToken cancellationToken)
    {
        _analysis = new TsLiveAnalysisSession(_displayName);
        await ConnectAsync(cancellationToken);
        var stream = _client!.GetStream();
        // 握手和媒体共用读取缓冲，避免丢掉与 PLAY 响应同批到达的数据。
        using var reader = new BufferedStream(new NetworkStream(_client.Client, ownsSocket: false));
        
        var startTime = DateTime.UtcNow;
        var packetCount = 0;
        var errorCount = 0;
        var targetTicks = targetDuration.HasValue && targetDuration.Value > TimeSpan.Zero
            ? (long)Math.Round(targetDuration.Value.TotalSeconds * 27_000_000D)
            : 0L;
        long? firstPcrTicks = null;
        long? lastPcrTicks = null;
        var stoppedByPcr = false;
        
        var lastProgressTime = DateTime.UtcNow; // 记录上次进度上报时间

        var describe = RtspMessage.BuildDescribe(_url, _seq++);
        await SendAsync(stream, describe, cancellationToken);
        var resp = await ReceiveHeadAsync(reader, cancellationToken);

        if (Regex.IsMatch(resp, "Location: (.*)"))
        {
            var newUrl = Regex.Match(resp, "Location: (.*)").Groups[1].Value.Trim();
            Log.Information("RTSP重定向到新URL：{NewUrl} (原URL: {OldUrl})", newUrl, _url);
            _client.Close();
            _client = null;
            _seq = 2;
            _analysis?.Dispose();
            _analysis = null;
            var redirected = new RtspStreamRecorder(newUrl, _outputPath, _recordingTransport, _displayName, _output);
            try
            {
                return await redirected.RunAsync(targetDuration, onProgress, cancellationToken);
            }
            finally
            {
                BytesWritten = redirected.BytesWritten;
                _analysis = redirected._analysis;
                redirected._analysis = null;
                await redirected.CloseAsync();
            }
        }

        if (!resp.Contains("200 OK"))
        {
            var statusLine = resp.Split("\r\n").FirstOrDefault() ?? "Unknown";
            Log.Error("RTSP DESCRIBE失败。状态行: {StatusLine}, URL: {Url}, 完整响应: {Response}", statusLine, _url, resp);
            throw new InvalidOperationException($"RTSP DESCRIBE失败 ({statusLine})，请检查URL或服务器状态");
        }

        var setup = RtspMessage.BuildSetup(_url, _seq++, _recordingTransport);
        await SendAsync(stream, setup, cancellationToken);
        var setupResp = await ReceiveHeadAsync(reader, cancellationToken);

        if (!setupResp.Contains("200 OK"))
        {
            var statusLine = setupResp.Split("\r\n").FirstOrDefault() ?? "Unknown";
            Log.Error("RTSP SETUP失败。状态行: {StatusLine}, URL: {Url}, 响应内容: {Response}", statusLine, _url, setupResp);
            throw new InvalidOperationException($"RTSP SETUP失败 ({statusLine})，可能不支持 {_recordingTransport} 传输模式");
        }

        var play = RtspMessage.BuildPlay(_url, _seq++);
        await SendAsync(stream, play, cancellationToken);
        var playResp = await ReceiveHeadAsync(reader, cancellationToken);

        if (!playResp.Contains("200 OK"))
        {
            var statusLine = playResp.Split("\r\n").FirstOrDefault() ?? "Unknown";
            Log.Error("RTSP PLAY失败。状态行: {StatusLine}, URL: {Url}, 响应内容: {Response}", statusLine, _url, playResp);
            throw new InvalidOperationException($"RTSP PLAY失败 ({statusLine})，服务器拒绝播放请求");
        }
        if (_output is null)
        {
            EnsureOutputPath(_outputPath);
        }
        await using var ownedFile = _output is null
            ? new FileStream(_outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous)
            : null;
        await using var buffered = ownedFile is null ? null : new BufferedStream(ownedFile, 64 * 1024);
        var sink = _output ?? buffered!;

        const int BatchSize = 64 * 1024;
        using var batch = MemoryPool<byte>.Shared.Rent(BatchSize);
        var batchBuffer = batch.Memory;
        var batchOffset = 0;
        var header = new byte[4];
        while (!cancellationToken.IsCancellationRequested)
        {
            if (await reader.ReadAsync(header.AsMemory(0, 1), cancellationToken) == 0)
            {
                Log.Warning("RTSP录制：读取头数据失败，录制停止。已录制 {BytesWritten} 字节，耗时 {Duration} 秒", 
                    BytesWritten, (DateTime.UtcNow - startTime).TotalSeconds);
                break;
            }

            if (header[0] == 'R')
            {
                // 部分 IPTV 源在 PLAY 后还会发送一段 RTSP 响应。
                await ReceiveHeadAsync(reader, cancellationToken, header[0]);
                continue;
            }

            if (header[0] != 0x24)
            {
                errorCount++;
                continue;
            }

            if (!await TryReadExactlyAsync(reader, header.AsMemory(1), cancellationToken))
            {
                break;
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
                if (!await TryReadExactlyAsync(reader, payload.AsMemory(0, length), cancellationToken))
                {
                    Log.Warning("RTSP录制：读取负载数据失败，录制停止。已录制 {BytesWritten} 字节，耗时 {Duration} 秒", 
                        BytesWritten, (DateTime.UtcNow - startTime).TotalSeconds);
                    break;
                }

                if (header[1] == 0)
                {
                    packetCount++;
                    var payloadMemory = payload.AsMemory(0, length);
                    if (!RtpPayloadParser.TryResolvePayloadSegment(payloadMemory.Span, _useRtp, ref firstPcrTicks, ref lastPcrTicks, targetTicks, packetCount, out var dataOffset, out var dataLength, out var shouldStop))
                    {
                        continue;
                    }

                    if (dataLength > BatchSize)
                    {
                        if (batchOffset > 0)
                        {
                            await sink.WriteAsync(batchBuffer[..batchOffset], cancellationToken);
                            batchOffset = 0;
                        }
                        await sink.WriteAsync(payloadMemory.Slice(dataOffset, dataLength), cancellationToken);
                        BytesWritten += dataLength;
                        _analysis?.Append(payloadMemory.Span.Slice(dataOffset, dataLength));
                    }
                    else
                    {
                        if (batchOffset + dataLength > BatchSize)
                        {
                            await sink.WriteAsync(batchBuffer[..batchOffset], cancellationToken);
                            batchOffset = 0;
                        }

                        payloadMemory.Span.Slice(dataOffset, dataLength).CopyTo(batchBuffer.Span[batchOffset..]);
                        batchOffset += dataLength;
                        BytesWritten += dataLength;
                        _analysis?.Append(payloadMemory.Span.Slice(dataOffset, dataLength));
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
                            currentPcrElapsedSeconds = PcrParser.CalculateElapsedSeconds(firstPcrTicks.Value, lastPcrTicks.Value);
                        }
                        onProgress(new RecordingProgress 
                        { 
                            BytesWritten = BytesWritten, 
                            PcrElapsedSeconds = currentPcrElapsedSeconds,
                            TsCheck = _analysis?.GetSummary()
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
            await sink.WriteAsync(batchBuffer[..batchOffset], cancellationToken);
        }
        
        await sink.FlushAsync(cancellationToken);
        
        // 最终进度报告
        double? finalPcrElapsedSeconds = null;
        if (firstPcrTicks.HasValue && lastPcrTicks.HasValue)
        {
            finalPcrElapsedSeconds = PcrParser.CalculateElapsedSeconds(firstPcrTicks.Value, lastPcrTicks.Value);
        }
        onProgress(new RecordingProgress 
        { 
            BytesWritten = BytesWritten, 
            PcrElapsedSeconds = finalPcrElapsedSeconds,
            TsCheck = _analysis?.GetSummary()
        });

        var duration = DateTime.UtcNow - startTime;
        if (stoppedByPcr)
        {
            var elapsedSeconds = lastPcrTicks.HasValue && firstPcrTicks.HasValue
                ? PcrParser.CalculateElapsedSeconds(firstPcrTicks.Value, lastPcrTicks.Value)
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
            OutputPath = _outputPath,
            TsCheck = _analysis?.Complete()
        };
    }

    public TsCheckSummary? CompleteTsCheck() => _analysis?.Complete();

    public void DisposeTsCheck()
    {
        _analysis?.Dispose();
        _analysis = null;
    }

    public async Task CloseAsync()
    {
        using var client = _client;
        _client = null;
        if (client?.Connected == true)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                var teardown = RtspMessage.BuildTeardown(_url, _seq);
                await SendAsync(client.GetStream(), teardown, timeout.Token);
            }
            catch
            {
            }
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var uri = new Uri(_url);
        _client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await _client.ConnectAsync(uri.Host, uri.Port > 0 ? uri.Port : 554, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"连接到 RTSP 服务器 {uri.Host} 超时 (10s)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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

    private static async Task<string> ReceiveHeadAsync(Stream stream, CancellationToken cancellationToken, byte? firstByte = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        const int MaxHeaderBytes = 64 * 1024;
        const int MaxBodyBytes = 1024 * 1024;
        using var header = new MemoryStream();
        if (firstByte.HasValue)
        {
            header.WriteByte(firstByte.Value);
        }

        var next = new byte[1];
        var lineHasContent = firstByte.HasValue;
        while (header.Length < MaxHeaderBytes)
        {
            await stream.ReadExactlyAsync(next.AsMemory(), timeout.Token);
            header.WriteByte(next[0]);
            if (next[0] == '\n')
            {
                if (!lineHasContent)
                {
                    var response = Encoding.UTF8.GetString(header.GetBuffer(), 0, (int)header.Length);
                    var match = Regex.Match(response, @"^Content-Length:[ \t]*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
                    if (!match.Success)
                    {
                        return response;
                    }

                    if (!int.TryParse(match.Groups[1].Value, out var bodyLength) || bodyLength > MaxBodyBytes)
                    {
                        throw new InvalidDataException("RTSP 响应体长度无效");
                    }

                    var body = new byte[bodyLength];
                    await stream.ReadExactlyAsync(body.AsMemory(), timeout.Token);
                    return response + Encoding.UTF8.GetString(body);
                }

                lineHasContent = false;
            }
            else if (next[0] != '\r')
            {
                lineHasContent = true;
            }
        }

        throw new InvalidDataException("RTSP 响应头超过最大长度");
    }

    private static void EnsureOutputPath(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
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
