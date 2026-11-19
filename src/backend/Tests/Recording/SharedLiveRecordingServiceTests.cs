using System.Net;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RtspRecServer.Server.Recording;
using RtspRecServer.Shared;
using RtspRecServer.Tests.TestSupport;

namespace RtspRecServer.Tests.Recording;

public class SharedLiveRecordingServiceTests
{
    [Fact]
    public async Task OverlappingHttpRecordingsShareOneRequestAndWriteTheirOwnWindows()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var firstStop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var secondStop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var firstOutput = new ObservedOutput();
        using var secondOutput = new ObservedOutput();
        var input = Channel.CreateUnbounded<byte[]>();
        var upstreamClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        await using var app = CreateHttpSource(input.Reader,
            onOpen: () => Interlocked.Increment(ref requests), onClose: () => upstreamClosed.TrySetResult());
        await app.StartAsync(deadline.Token);

        try
        {
            var service = CreateService();
            var task = new RecordingTask { Id = 1, ChannelId = 1, Url = $"{app.Urls.Single()}/rtp/source", IsLive = true };
            var key = LiveRecordingSourceKey.FromTask(task)!.Value;
            var beforeOverlap = CreatePayload(1);
            var overlap = CreatePayload(2);
            var afterOverlap = CreatePayload(3);

            var first = service.RecordAsync(task, key, string.Empty, _ => { }, firstStop.Token, firstOutput);
            Assert.True(input.Writer.TryWrite(beforeOverlap));
            await firstOutput.WaitForBytesAsync(beforeOverlap.Length, deadline.Token);

            // 用不同频道 ID 验证共享依据是源地址。HTTP 分块停在 TS 包中间时也能正确拼回整包。
            var second = service.RecordAsync(task with { Id = 2, ChannelId = 2 }, key, string.Empty, _ => { }, secondStop.Token, secondOutput);
            Assert.True(input.Writer.TryWrite(overlap[..211]));
            await secondOutput.WaitForBytesAsync(TsPacketBuilder.PacketSize, deadline.Token);
            Assert.True(input.Writer.TryWrite(overlap[211..]));
            await secondOutput.WaitForBytesAsync(overlap.Length, deadline.Token);
            var sharedCounts = service.GetSharedTaskCounts();
            Assert.Equal(2, sharedCounts[1]);
            Assert.Equal(2, sharedCounts[2]);

            firstStop.Cancel();
            var firstResult = await first.WaitAsync(deadline.Token);
            Assert.Equal(beforeOverlap.Length + overlap.Length, firstResult.BytesWritten);
            Assert.Equal((byte[])[.. beforeOverlap, .. overlap], firstOutput.ToArray());
            Assert.Null(firstResult.ErrorMessage);
            Assert.False(upstreamClosed.Task.IsCompleted);
            Assert.Empty(service.GetSharedTaskCounts());

            Assert.True(input.Writer.TryWrite(afterOverlap));
            await secondOutput.WaitForBytesAsync(overlap.Length + afterOverlap.Length, deadline.Token);
            secondStop.Cancel();
            var secondResult = await second.WaitAsync(deadline.Token);
            Assert.Equal(overlap.Length + afterOverlap.Length, secondResult.BytesWritten);
            Assert.Equal((byte[])[.. overlap, .. afterOverlap], secondOutput.ToArray());
            Assert.Null(secondResult.ErrorMessage);
            Assert.Equal(1, Volatile.Read(ref requests));
            await upstreamClosed.Task.WaitAsync(deadline.Token);
        }
        finally
        {
            input.Writer.TryComplete();
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task FullWriterQueueEndsOnlyThatRecording()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var resumeWriting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var slowOutput = new ObservedOutput(resumeWriting.Task);
        using var healthyOutput = new ObservedOutput();
        var input = Channel.CreateUnbounded<byte[]>();
        await using var app = CreateHttpSource(input.Reader);
        await app.StartAsync(deadline.Token);

        var service = CreateService();
        var task = new RecordingTask { ChannelId = 1, Url = $"{app.Urls.Single()}/rtp/source", IsLive = true };
        var key = LiveRecordingSourceKey.FromTask(task)!.Value;
        var slow = service.RecordAsync(task, key, string.Empty, _ => { }, stop.Token, slowOutput);
        var healthy = service.RecordAsync(task with { ChannelId = 2 }, key, string.Empty, _ => { }, stop.Token, healthyOutput);
        try
        {
            // 每批不足写入队列容量，累计则超过容量。先确认另一个写入端已经跟上再发下一批。
            var payload = TsPacketBuilder.Concatenate(Enumerable.Repeat(CreatePayload(1), 1024).ToArray());
            const int batches = 20;
            for (var index = 1; index <= batches; index++)
            {
                Assert.True(input.Writer.TryWrite(payload));
                await healthyOutput.WaitForBytesAsync((long)index * payload.Length, deadline.Token);
            }

            resumeWriting.TrySetResult();
            var slowResult = await slow.WaitAsync(deadline.Token);
            Assert.Contains("录像写入缓冲已满", slowResult.ErrorMessage);
            Assert.InRange(slowResult.BytesWritten, 1, (long)batches * payload.Length - 1);

            var finalPayload = CreatePayload(2);
            Assert.True(input.Writer.TryWrite(finalPayload));
            var expectedBytes = (long)batches * payload.Length + finalPayload.Length;
            await healthyOutput.WaitForBytesAsync(expectedBytes, deadline.Token);
            stop.Cancel();
            var healthyResult = await healthy.WaitAsync(deadline.Token);
            Assert.Null(healthyResult.ErrorMessage);
            Assert.Equal(expectedBytes, healthyResult.BytesWritten);
        }
        finally
        {
            resumeWriting.TrySetResult();
            stop.Cancel();
            input.Writer.TryComplete();
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task LastWriterFailureAllowsRecordingTheSameSourceAgain()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var failingOutput = new FailingOutput();
        using var nextOutput = new ObservedOutput();
        var input = Channel.CreateUnbounded<byte[]>();
        var upstreamClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        await using var app = CreateHttpSource(input.Reader,
            onOpen: () => Interlocked.Increment(ref requests), onClose: () => upstreamClosed.TrySetResult());
        await app.StartAsync(deadline.Token);

        var service = CreateService();
        var task = new RecordingTask { Url = $"{app.Urls.Single()}/rtp/source", IsLive = true };
        var key = LiveRecordingSourceKey.FromTask(task)!.Value;
        try
        {
            var failure = service.RecordAsync(task, key, string.Empty, _ => { }, deadline.Token, failingOutput);
            Assert.True(input.Writer.TryWrite(CreatePayload(1)));
            var failedResult = await failure.WaitAsync(deadline.Token);
            Assert.False(failedResult.Success);
            Assert.Equal("写盘失败", failedResult.ErrorMessage);
            await upstreamClosed.Task.WaitAsync(deadline.Token);

            var next = service.RecordAsync(task, key, string.Empty, _ => { }, stop.Token, nextOutput);
            var payload = CreatePayload(2);
            Assert.True(input.Writer.TryWrite(payload));
            await nextOutput.WaitForBytesAsync(payload.Length, deadline.Token);
            stop.Cancel();
            var nextResult = await next.WaitAsync(deadline.Token);
            Assert.Null(nextResult.ErrorMessage);
            Assert.Equal(payload, nextOutput.ToArray());
            Assert.Equal(2, Volatile.Read(ref requests));
        }
        finally
        {
            stop.Cancel();
            input.Writer.TryComplete();
            await app.StopAsync();
        }
    }

    private static SharedLiveRecordingService CreateService() => new(new HttpLiveRecordingService(),
        new UdpLiveRecordingService(), NullLogger<SharedLiveRecordingService>.Instance);

    private static WebApplication CreateHttpSource(
        ChannelReader<byte[]> input, Action? onOpen = null, Action? onClose = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.MapGet("/rtp/source", async (HttpContext context) =>
        {
            onOpen?.Invoke();
            try
            {
                await foreach (var chunk in input.ReadAllAsync(context.RequestAborted))
                {
                    await context.Response.Body.WriteAsync(chunk, context.RequestAborted);
                }
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
            }
            finally
            {
                onClose?.Invoke();
            }
        });
        return app;
    }

    private static byte[] CreatePayload(byte marker)
    {
        var packet = TsPacketBuilder.WithoutPcr();
        packet[^1] = marker;
        return TsPacketBuilder.Concatenate(packet, packet, packet, packet);
    }

    private sealed class FailingOutput : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("写盘失败"));
    }

    private sealed class ObservedOutput(Task? resumeWriting = null) : MemoryStream
    {
        private readonly Channel<long> _sizes = Channel.CreateUnbounded<long>();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (resumeWriting is not null)
            {
                await resumeWriting;
            }
            Write(buffer.Span);
            _sizes.Writer.TryWrite(Length);
        }

        public async Task WaitForBytesAsync(long length, CancellationToken cancellationToken)
        {
            while (await _sizes.Reader.ReadAsync(cancellationToken) < length)
            {
            }
        }
    }
}
