using System.Net;
using System.Net.Sockets;
using System.Text;
using RtspRecServer.Server.Recording;
using RtspRecServer.Shared;

namespace RtspRecServer.Tests.Recording;

public class HttpLiveRecordingServiceTests
{
    [Fact]
    public async Task RecordAsync_WritesBodyToFileAndReportsStreamEnd()
    {
        var payload = CreateTsPayload();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var outputPath = Path.Combine(Path.GetTempPath(), $"rtsp-live-{Guid.NewGuid():N}.ts");

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            await ReadRequestHeadersAsync(stream);

            var headers = $"HTTP/1.1 200 OK\r\nContent-Type: video/mp2t\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
            await stream.WriteAsync(payload);
        });

        try
        {
            var service = new HttpLiveRecordingService();
            var task = new RecordingTask
            {
                Url = $"http://127.0.0.1:{port}/rtp/239.1.1.1:8000",
                IsLive = true
            };
            long reportedBytes = 0;

            var result = await service.RecordAsync(
                task,
                outputPath,
                targetDuration: null,
                recordingTransport: "MP2T/TCP",
                onProgress: progress => reportedBytes = progress.BytesWritten,
                cancellationToken: CancellationToken.None);

            await serverTask;

            Assert.False(result.Success);
            Assert.Equal("直播流已结束", result.ErrorMessage);
            Assert.Equal(payload.Length, result.BytesWritten);
            Assert.Equal(payload.Length, reportedBytes);
            Assert.Equal(payload, await File.ReadAllBytesAsync(outputPath));
        }
        finally
        {
            listener.Stop();
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [Fact]
    public async Task RecordAsync_RejectsHtmlResponse()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var outputPath = Path.Combine(Path.GetTempPath(), $"rtsp-live-{Guid.NewGuid():N}.ts");

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            await ReadRequestHeadersAsync(stream);

            var body = Encoding.UTF8.GetBytes("<html>error</html>");
            var headers = $"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
            await stream.WriteAsync(body);
        });

        try
        {
            var service = new HttpLiveRecordingService();
            var task = new RecordingTask
            {
                Url = $"http://127.0.0.1:{port}/rtp/239.1.1.1:8000",
                IsLive = true
            };

            var result = await service.RecordAsync(
                task,
                outputPath,
                targetDuration: null,
                recordingTransport: "MP2T/TCP",
                onProgress: _ => { },
                cancellationToken: CancellationToken.None);

            await serverTask;

            Assert.False(result.Success);
            Assert.Contains("非视频内容", result.ErrorMessage ?? string.Empty);
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            listener.Stop();
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [Fact]
    public async Task RecordAsync_RejectsNonTsBody()
    {
        var body = Encoding.ASCII.GetBytes(new string('A', 1000));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var outputPath = Path.Combine(Path.GetTempPath(), $"rtsp-live-{Guid.NewGuid():N}.ts");

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            await ReadRequestHeadersAsync(stream);

            var headers = $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
            await stream.WriteAsync(body);
        });

        try
        {
            var service = new HttpLiveRecordingService();
            var task = new RecordingTask
            {
                Url = $"http://127.0.0.1:{port}/rtp/239.1.1.1:8000",
                IsLive = true
            };

            var result = await service.RecordAsync(
                task,
                outputPath,
                targetDuration: null,
                recordingTransport: "MP2T/TCP",
                onProgress: _ => { },
                cancellationToken: CancellationToken.None);

            await serverTask;

            Assert.False(result.Success);
            Assert.Contains("MPEG-TS", result.ErrorMessage ?? string.Empty);
        }
        finally
        {
            listener.Stop();
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    private static byte[] CreateTsPayload()
    {
        var payload = new byte[188 * 2];
        for (var offset = 0; offset < payload.Length; offset += 188)
        {
            payload[offset] = 0x47;
        }

        return payload;
    }

    private static async Task ReadRequestHeadersAsync(Stream stream)
    {
        var buffer = new byte[1024];
        var received = new StringBuilder();

        while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                break;
            }

            received.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }
    }
}
