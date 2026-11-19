using System.Net;
using System.Net.Sockets;
using System.Text;
using RtspRecServer.Server.Recording;
using RtspRecServer.Shared;
using RtspRecServer.Tests.TestSupport;

namespace RtspRecServer.Tests.Recording;

public class RtspRecordingServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordAsync_KeepsMediaCoalescedWithPlayResponse(bool extraResponse)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var output = new MemoryStream();
        var recording = new RtspRecordingService().RecordAsync(
            new RecordingTask { Url = $"rtsp://127.0.0.1:{port}/test" },
            string.Empty, null, "MP2T/TCP", _ => { }, deadline.Token, output);

        using var client = await listener.AcceptTcpClientAsync(deadline.Token);
        await using var stream = client.GetStream();
        using var requests = new StreamReader(stream, leaveOpen: true);
        var payload = TsPacketBuilder.Concatenate(TsPacketBuilder.WithoutPcr(), TsPacketBuilder.WithoutPcr());
        foreach (var method in new[] { "DESCRIBE", "SETUP", "PLAY" })
        {
            var line = await requests.ReadLineAsync(deadline.Token);
            Assert.StartsWith(method + " ", line);
            do
            {
                line = await requests.ReadLineAsync(deadline.Token);
                Assert.NotNull(line);
            } while (line.Length > 0);

            if (method != "PLAY")
            {
                await stream.WriteAsync("RTSP/1.0 200 OK\r\n\r\n"u8.ToArray(), deadline.Token);
                continue;
            }

            var response = "RTSP/1.0 200 OK\r\n\r\n";
            if (extraResponse)
            {
                response += "RTSP/1.0 200 OK\r\ncontent-length: 3\r\n\r\n流";
            }

            byte[] mediaHeader = [0x24, 0, (byte)(payload.Length >> 8), (byte)payload.Length];
            await stream.WriteAsync((byte[])[.. Encoding.UTF8.GetBytes(response), .. mediaHeader, .. payload], deadline.Token);
        }
        client.Client.Shutdown(SocketShutdown.Send);

        var result = await recording.WaitAsync(deadline.Token);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(payload, output.ToArray());
    }

    [Fact]
    public async Task RecordAsync_CancelsWhileWaitingForDescribeResponse()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);

        var recording = new RtspRecordingService().RecordAsync(
            new RecordingTask { Url = $"rtsp://127.0.0.1:{port}/test" },
            string.Empty, null, "MP2T/TCP", _ => { }, stop.Token, Stream.Null);

        using var client = await listener.AcceptTcpClientAsync(deadline.Token);
        var bytes = await client.GetStream().ReadAsync(new byte[256], deadline.Token);
        Assert.True(bytes > 0);
        stop.Cancel();

        var result = await recording.WaitAsync(deadline.Token);
        Assert.False(result.Success);
        Assert.Equal(0, result.BytesWritten);
    }
}
