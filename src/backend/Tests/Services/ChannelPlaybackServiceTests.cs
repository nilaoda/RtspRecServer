using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RtspRecServer.Server.Recording;
using RtspRecServer.Server.Services;
using RtspRecServer.Shared;
using RtspRecServer.Tests.TestSupport;

namespace RtspRecServer.Tests.Services;

public class ChannelPlaybackServiceTests
{
    [Fact]
    public async Task HttpViewersShareAnUpstreamThatStopsWhenTheLastViewerLeaves()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var twoViewers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendMore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstreamClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewers = 0;
        var upstreamRequests = 0;
        var ts = CreateTsPayload();

        await using var server = await PlaybackServer.StartAsync(app =>
        {
            // 普通 HTTP TS 路径，验证播放无需依赖 udpxy 的 /rtp/ 或 /udp/ 路径。
            app.MapGet("/origin.ts", async (HttpContext context) =>
            {
                Interlocked.Increment(ref upstreamRequests);
                var token = context.RequestAborted;
                try
                {
                    await twoViewers.Task.WaitAsync(token);
                    context.Response.ContentType = "video/mp2t";
                    await context.Response.Body.WriteAsync(ts, token);
                    await sendMore.Task.WaitAsync(token);
                    // 一小块数据后上游继续保持连接，播放端应该能收到这块尾包。
                    await context.Response.Body.WriteAsync(ts, token);
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                }
                finally
                {
                    upstreamClosed.TrySetResult();
                }
            });
        }, () =>
        {
            if (Interlocked.Increment(ref viewers) == 2)
            {
                twoViewers.TrySetResult();
            }
        });

        var id = server.AddChannel(new Uri(server.Address, "/origin.ts").AbsoluteUri);
        var firstRequest = server.PlayAsync(id, deadline.Token);
        var secondRequest = server.PlayAsync(id, deadline.Token);
        using var first = await firstRequest;
        using var second = await secondRequest;
        await AssertTsAsync(first, ts, deadline.Token);
        await AssertTsAsync(second, ts, deadline.Token);
        Assert.Equal(1, Volatile.Read(ref upstreamRequests));

        first.Dispose();
        sendMore.TrySetResult();
        await AssertTsAsync(second, ts, deadline.Token);
        Assert.False(upstreamClosed.Task.IsCompleted);

        second.Dispose();
        await upstreamClosed.Task.WaitAsync(deadline.Token);
    }

    [Fact]
    public async Task FullViewerQueueCancelsItsBlockedWriteWithoutStoppingAnotherViewer()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var fastSubscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendMore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ts = CreateTsPayload();
        var block = Enumerable.Range(0, 20).SelectMany(_ => ts).ToArray();
        const int blockCount = 80;

        await using var server = await PlaybackServer.StartAsync(app =>
        {
            app.MapGet("/origin.ts", async (HttpContext context) =>
            {
                var token = context.RequestAborted;
                await fastSubscribed.Task.WaitAsync(token);
                await context.Response.Body.WriteAsync(ts, token);
                await sendMore.Task.WaitAsync(token);
                for (var i = 0; i < blockCount; i++)
                {
                    await context.Response.Body.WriteAsync(block, token);
                    await Task.Delay(5, token);
                }
                await Task.Delay(Timeout.Infinite, token);
            });
        }, () => fastSubscribed.TrySetResult());

        var id = server.AddChannel(new Uri(server.Address, "/origin.ts").AbsoluteUri);
        using var slowOutput = new BlockedPlaybackOutput();
        var slow = server.PlayToAsync(id, slowOutput, deadline.Token);
        using var fast = await server.PlayAsync(id, deadline.Token);
        await AssertTsAsync(fast, ts, deadline.Token);
        await slowOutput.Writing.Task.WaitAsync(deadline.Token);

        sendMore.TrySetResult();
        var body = await fast.Content.ReadAsStreamAsync(deadline.Token);
        var received = new byte[block.Length * blockCount];
        var read = body.ReadExactlyAsync(received, deadline.Token).AsTask();
        await slow.WaitAsync(TimeSpan.FromSeconds(3), deadline.Token);
        await read;
        Assert.Equal(ts, received[^ts.Length..]);
    }

    [Theory]
    [InlineData("rtp")]
    [InlineData("udp")]
    public async Task ReceivesMpegTsFromDirectUdpSources(string scheme)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        var endpoint = new IPEndPoint(IPAddress.Loopback, GetFreeUdpPort());
        var ts = CreateTsPayload();
        await using var server = await PlaybackServer.StartAsync();
        var id = server.AddChannel($"{scheme}://127.0.0.1:{endpoint.Port}");
        var request = server.PlayAsync(id, deadline.Token);

        for (ushort sequence = 0; !request.IsCompleted; sequence++)
        {
            var packet = scheme == "rtp" ? WrapRtp(ts, sequence) : ts;
            await sender.SendAsync(packet, endpoint, deadline.Token);
            await Task.Delay(5, deadline.Token);
        }

        using var response = await request;
        await AssertTsAsync(response, ts, deadline.Token);
    }

    [Theory]
    [InlineData("MP2T/TCP", false)]
    [InlineData("MP2T/RTP/TCP", true)]
    public async Task ForwardsIptvRtspWithItsOriginalUrlAndSession(string transport, bool rtp)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var url = $"rtsp://127.0.0.1:{port}/channel?profile=1";
        var ts = CreateTsPayload();
        await using var server = await PlaybackServer.StartAsync();
        server.Configuration.UpdateAppConfig(new AppConfigUpdateRequest { RecordingTransport = "MP2T/TCP" });
        var request = server.PlayAsync(server.AddChannel(url), deadline.Token);

        using var upstream = await listener.AcceptTcpClientAsync(deadline.Token);
        await using var network = upstream.GetStream();
        using var requests = new StreamReader(network, leaveOpen: true);
        foreach (var method in new[] { "DESCRIBE", "SETUP", "PLAY" })
        {
            Assert.Equal($"{method} {url} RTSP/1.0", await requests.ReadLineAsync(deadline.Token));
            var headers = new List<string>();
            while (await requests.ReadLineAsync(deadline.Token) is { Length: > 0 } header)
            {
                headers.Add(header);
            }

            if (method == "PLAY")
            {
                Assert.Contains("Session: live-session", headers);
                Assert.DoesNotContain(headers, line => line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase));
            }

            var timeout = rtp ? 2 : 60;
            var reply = method == "SETUP"
                ? $"RTSP/1.0 200 Ready\r\nSession: live-session;timeout={timeout}\r\nTransport: {transport};unicast;interleaved=2-3\r\n\r\n"
                : "RTSP/1.0 200 OK\r\n\r\n";
            await network.WriteAsync(System.Text.Encoding.ASCII.GetBytes(reply), deadline.Token);
        }

        byte[] payload = rtp ? [.. WrapRtp(ts, 1), 0, 0, 0, 4] : ts;
        if (rtp)
        {
            payload[0] |= 0x20;
        }
        byte[] frame = [0x24, 2, (byte)(payload.Length >> 8), (byte)payload.Length, .. payload];
        await network.WriteAsync(frame, deadline.Token);

        using var response = await request;
        await AssertTsAsync(response, ts, deadline.Token);
        if (rtp)
        {
            Assert.Equal($"OPTIONS {url} RTSP/1.0", await requests.ReadLineAsync(deadline.Token));
            var sessionPresent = false;
            while (await requests.ReadLineAsync(deadline.Token) is { Length: > 0 } header)
            {
                sessionPresent |= header == "Session: live-session";
            }
            Assert.True(sessionPresent);
            await network.WriteAsync("RTSP/1.0 200 OK\r\n\r\n"u8.ToArray(), deadline.Token);
        }
        // 验证 RTP padding 未被夹在前后两批 TS 之间，且小批数据按时发出。
        await network.WriteAsync(frame, deadline.Token);
        await AssertTsAsync(response, ts, deadline.Token);
        response.Dispose();
        Assert.Equal($"TEARDOWN {url} RTSP/1.0", await requests.ReadLineAsync(deadline.Token));
    }

    private static async Task AssertTsAsync(HttpResponseMessage response, byte[] expected, CancellationToken cancellationToken)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("video/mp2t", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var actual = new byte[expected.Length];
        var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        await body.ReadExactlyAsync(actual, cancellationToken);
        Assert.Equal(expected, actual);
    }

    private static byte[] CreateTsPayload() => Enumerable.Range(0, 7).SelectMany(_ => TsPacketBuilder.WithoutPcr()).ToArray();

    private static byte[] WrapRtp(byte[] ts, ushort sequence) =>
        [0x80, 33, (byte)(sequence >> 8), (byte)sequence, 0, 0, 0, 0, 0, 0, 0, 1, .. ts];

    private static int GetFreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private sealed class BlockedPlaybackOutput : MemoryStream
    {
        public TaskCompletionSource Writing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writing.TrySetResult();
            return new ValueTask(Task.Delay(Timeout.Infinite, cancellationToken));
        }
    }

    private sealed class PlaybackServer(
        WebApplication app, DirectoryInfo directory, ConfigurationService configuration, ChannelPlaybackService service) : IAsyncDisposable
    {
        private readonly HttpClient _client = new(new SocketsHttpHandler { MaxResponseDrainSize = 0 });
        public Uri Address => new(app.Urls.Single());
        public ConfigurationService Configuration { get; } = configuration;

        public static async Task<PlaybackServer> StartAsync(Action<WebApplication>? mapUpstream = null, Action? onSubscribed = null)
        {
            var directory = Directory.CreateTempSubdirectory("rtsp-channel-playback-");
            var configuration = new ConfigurationService(directory.FullName);
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = directory.FullName });
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            var service = new ChannelPlaybackService(configuration, new UdpLiveRecordingService(), new HttpLiveRecordingService(),
                new RtspRecordingService(), app.Services.GetRequiredService<IHostApplicationLifetime>(), NullLogger<ChannelPlaybackService>.Instance);

            app.MapGet("/api/channels/{id:int}/play.ts", (int id) =>
            {
                var response = service.Subscribe(id);
                onSubscribed?.Invoke();
                return response;
            });
            mapUpstream?.Invoke(app);
            await app.StartAsync();
            return new PlaybackServer(app, directory, configuration, service);
        }

        public int AddChannel(string url) => Configuration.AddChannel(new ChannelConfig { Name = "Test", Url = url }).Id;

        public Task<HttpResponseMessage> PlayAsync(int id, CancellationToken cancellationToken) =>
            _client.GetAsync(new Uri(Address, $"/api/channels/{id}/play.ts"), HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        public Task PlayToAsync(int id, Stream output, CancellationToken cancellationToken)
        {
            var context = new DefaultHttpContext { RequestServices = app.Services, RequestAborted = cancellationToken };
            context.Response.Body = output;
            return service.Subscribe(id).ExecuteAsync(context);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            directory.Delete(recursive: true);
        }
    }
}
