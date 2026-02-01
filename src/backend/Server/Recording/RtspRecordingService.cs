using System.Buffers;
using System.Net;
using System.Net.Sockets;
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
}

public sealed class RtspRecordingService : IRecordingService
{
    public async Task<RecordingResult> RecordAsync(RecordingTask task, string outputPath, Action<long> onBytesWritten, CancellationToken cancellationToken)
    {
        var recorder = new RtspStreamRecorder(task.Url, outputPath);
        try
        {
            var bytes = await recorder.RunAsync(onBytesWritten, cancellationToken);
            return new RecordingResult
            {
                Success = !cancellationToken.IsCancellationRequested,
                BytesWritten = bytes,
                OutputPath = outputPath
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
    private TcpClient? _client;
    private int _seq = 2;
    public long BytesWritten { get; private set; }

    public RtspStreamRecorder(string url, string outputPath)
    {
        _url = url;
        _outputPath = outputPath;
    }

    public async Task<long> RunAsync(Action<long> onBytesWritten, CancellationToken cancellationToken)
    {
        Connect();
        var stream = _client!.GetStream();
        // 增加超时时间到30秒，避免网络波动导致录制中断
        stream.ReadTimeout = 30000;
        stream.WriteTimeout = 30000;
        
        var startTime = DateTime.UtcNow;
        var packetCount = 0;
        var errorCount = 0;

        var describe = RtspMessage.BuildDescribe(_url, _seq++);
        await SendAsync(stream, describe, cancellationToken);
        var resp = ReceiveHead(stream);

        if (Regex.IsMatch(resp, "Location: (.*)"))
        {
            var newUrl = Regex.Match(resp, "Location: (.*)").Groups[1].Value.Trim();
            Log.Information("RTSP重定向到新URL：{NewUrl}", newUrl);
            _client.Close();
            _client = null;
            _seq = 2;
            return await new RtspStreamRecorder(newUrl, _outputPath).RunAsync(onBytesWritten, cancellationToken);
        }

        if (!resp.Contains("200 OK"))
        {
            Log.Error("RTSP DESCRIBE失败：{Response}", resp);
            throw new InvalidOperationException($"RTSP DESCRIBE失败：{resp}");
        }

        var setup = RtspMessage.BuildSetup(_url, _seq++);
        await SendAsync(stream, setup, cancellationToken);
        var setupResp = ReceiveHead(stream);

        if (!setupResp.Contains("200 OK"))
        {
            Log.Error("RTSP SETUP失败：{Response}", setupResp);
            throw new InvalidOperationException($"RTSP SETUP失败：{setupResp}");
        }

        var play = RtspMessage.BuildPlay(_url, _seq++);
        await SendAsync(stream, play, cancellationToken);
        var playResp = ReceiveHead(stream);

        if (!playResp.Contains("200 OK"))
        {
            Log.Error("RTSP PLAY失败：{Response}", playResp);
            throw new InvalidOperationException($"RTSP PLAY失败：{playResp}");
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
                    await buffered.WriteAsync(payload.AsMemory(0, length), cancellationToken);
                    BytesWritten += length;
                    onBytesWritten(BytesWritten);
                    packetCount++;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }

        await buffered.FlushAsync(cancellationToken);

        var duration = DateTime.UtcNow - startTime;
        Log.Information("RTSP录制结束：总字节数 {BytesWritten}，总数据包数 {PacketCount}，总耗时 {Duration} 秒，平均速率 {Rate} KB/s", 
            BytesWritten, packetCount, duration.TotalSeconds, 
            duration.TotalSeconds > 0 ? BytesWritten / 1024.0 / duration.TotalSeconds : 0);

        return BytesWritten;
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
        if (_client != null && _client.Connected)
        {
            _client.Close();
        }

        var uri = new Uri(_url);
        var hostname = uri.Host;
        var port = uri.Port == -1 ? 554 : uri.Port;
        _client = new TcpClient(hostname, port);
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

    public static string BuildSetup(string url, int seq)
    {
        return $"SETUP {url} RTSP/1.0\r\n" +
               "Transport: MP2T/TCP;unicast;interleaved=0-1\r\n" +
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
