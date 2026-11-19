using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Features;
using RtspRecServer.Server.Recording;
using RtspRecServer.Shared;

namespace RtspRecServer.Server.Services;

internal sealed class ChannelPlaybackService(
    ConfigurationService configuration,
    UdpLiveRecordingService udp,
    HttpLiveRecordingService http,
    RtspRecordingService rtsp,
    IHostApplicationLifetime lifetime,
    ILogger<ChannelPlaybackService> logger)
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);
    private const int ViewerQueueCapacity = 64;

    private readonly object _sync = new();
    private readonly Dictionary<int, PlaybackSession> _sessions = [];

    public IResult Subscribe(int channelId)
    {
        var channel = configuration.GetChannels().FirstOrDefault(channel => channel.Id == channelId);
        if (channel is null)
        {
            return Results.NotFound(ApiResponse.Create("频道不存在"));
        }

        var url = channel.Url.Trim();
        var separator = url.IndexOf("://", StringComparison.Ordinal);
        var scheme = separator > 0 ? url[..separator].ToLowerInvariant() : string.Empty;
        if (scheme is not ("rtp" or "udp" or "http" or "https" or "rtsp"))
        {
            return Results.BadRequest(ApiResponse.Create("当前协议不支持频道播放"));
        }

        var transport = scheme == "rtsp" ? configuration.GetAppConfig().RecordingTransport : string.Empty;
        var viewer = new PlaybackViewer();

        lock (_sync)
        {
            if (_sessions.TryGetValue(channelId, out var session) && (session.Url != url || session.Transport != transport))
            {
                StopSession(session);
                session = null;
            }

            if (session is null)
            {
                session = new PlaybackSession(channel with { Url = url }, scheme, transport, lifetime.ApplicationStopping);
                session.Viewers.Add(viewer);
                _sessions.Add(channelId, session);
                _ = Task.Run(() => ReceiveAsync(session));
            }
            else
            {
                session.Viewers.Add(viewer);
            }

            return new PlaybackResponse(this, session, viewer);
        }
    }

    public void InvalidateChannels(IEnumerable<int> channelIds)
    {
        lock (_sync)
        {
            foreach (var id in channelIds)
            {
                if (_sessions.TryGetValue(id, out var session))
                {
                    StopSession(session);
                }
            }
        }
    }

    private async Task ReceiveAsync(PlaybackSession session)
    {
        var channel = session.Channel;
        var task = new RecordingTask { ChannelId = channel.Id, ChannelName = channel.Name, TaskName = channel.Name, Url = session.Url };
        using var output = new PlaybackOutput(data => Publish(session, data));
        try
        {
            var token = session.Cancellation.Token;
            var result = await (session.Scheme switch
            {
                "rtp" or "udp" => udp.ForwardAsync(task, output, token),
                "http" or "https" => http.ForwardAsync(task, output, token),
                _ => rtsp.ForwardAsync(task, session.Transport, output, token)
            });

            if (!token.IsCancellationRequested && result.ErrorMessage is not null)
            {
                logger.LogWarning("频道 {ChannelId} 播放源结束: {Error}", channel.Id, result.ErrorMessage);
            }
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "频道 {ChannelId} 播放收流失败", channel.Id);
        }
        finally
        {
            output.Flush();
            lock (_sync)
            {
                if (_sessions.GetValueOrDefault(channel.Id) == session)
                {
                    _sessions.Remove(channel.Id);
                }
                CompleteViewers(session);
            }

            try
            {
                await session.StopCompletion;
            }
            finally
            {
                session.Cancellation.Dispose();
            }
        }
    }

    private void Publish(PlaybackSession session, ReadOnlyMemory<byte> data)
    {
        lock (_sync)
        {
            for (var index = session.Viewers.Count - 1; index >= 0; index--)
            {
                var viewer = session.Viewers[index];
                if (!viewer.Queue.Writer.TryWrite(data))
                {
                    // 慢连接不会阻塞收流，也不会把缺了中间数据的 TS 继续送给播放器。
                    viewer.Stop();
                    session.Viewers.RemoveAt(index);
                }
            }

            if (session.Viewers.Count == 0 && _sessions.GetValueOrDefault(session.Channel.Id) == session)
            {
                StopSession(session);
            }
        }
    }

    private void Unsubscribe(PlaybackSession session, PlaybackViewer viewer)
    {
        lock (_sync)
        {
            if (session.Viewers.Remove(viewer))
            {
                viewer.Queue.Writer.TryComplete();
                if (session.Viewers.Count == 0)
                {
                    StopSession(session);
                }
            }
        }
    }

    // 调用方持有 _sync，确保收流退出、换址和最后一个播放器退出只关闭各自的会话。
    private void StopSession(PlaybackSession session)
    {
        _sessions.Remove(session.Channel.Id);
        session.StopCompletion = session.Cancellation.CancelAsync();
        CompleteViewers(session, stop: true);
    }

    private static void CompleteViewers(PlaybackSession session, bool stop = false)
    {
        foreach (var viewer in session.Viewers)
        {
            if (stop)
            {
                viewer.Stop();
            }
            else
            {
                viewer.Queue.Writer.TryComplete();
            }
        }
        session.Viewers.Clear();
    }

    private async Task ServeAsync(HttpContext context, PlaybackSession session, PlaybackViewer viewer)
    {
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, lifetime.ApplicationStopping, viewer.StopToken);
        var token = deadline.Token;
        var writing = false;
        try
        {
            deadline.CancelAfter(StartTimeout);
            if (!await viewer.Queue.Reader.WaitToReadAsync(token))
            {
                viewer.StopToken.ThrowIfCancellationRequested();
                await WriteErrorAsync(context, StatusCodes.Status502BadGateway, "频道没有返回可播放的 MPEG-TS 数据");
                return;
            }

            response.ContentType = "video/mp2t";
            response.Headers["X-Accel-Buffering"] = "no";
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            do
            {
                while (viewer.Queue.Reader.TryRead(out var data))
                {
                    viewer.StopToken.ThrowIfCancellationRequested();
                    deadline.CancelAfter(WriteTimeout);
                    writing = true;
                    await response.Body.WriteAsync(data, token);
                    writing = false;
                }
                deadline.CancelAfter(ReadTimeout);
            } while (await viewer.Queue.Reader.WaitToReadAsync(token));
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested || lifetime.ApplicationStopping.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException) when (viewer.StopToken.IsCancellationRequested)
        {
            if (writing)
            {
                context.Abort();
            }
            else if (!response.HasStarted)
            {
                await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "频道播放已停止");
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            if (response.HasStarted)
            {
                context.Abort();
            }
            else
            {
                await WriteErrorAsync(context, StatusCodes.Status504GatewayTimeout, "等待频道数据超时");
            }
        }
        catch (IOException)
        {
            context.Abort();
        }
        finally
        {
            Unsubscribe(session, viewer);
            await viewer.DisposeAsync();
        }
    }

    private static Task WriteErrorAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(ApiResponse.Create(message), RtspJsonContext.Default.ApiResponse,
            cancellationToken: context.RequestAborted);
    }

    private sealed class PlaybackSession(ChannelConfig channel, string scheme, string transport, CancellationToken stopping)
    {
        public ChannelConfig Channel { get; } = channel;
        public string Url => Channel.Url;
        public string Scheme { get; } = scheme;
        public string Transport { get; } = transport;
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        public Task StopCompletion { get; set; } = Task.CompletedTask;
        public List<PlaybackViewer> Viewers { get; } = [];
    }

    private sealed class PlaybackViewer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private Task _stopCompletion = Task.CompletedTask;

        public Channel<ReadOnlyMemory<byte>> Queue { get; } = Channel.CreateBounded<ReadOnlyMemory<byte>>(
            new BoundedChannelOptions(ViewerQueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        public CancellationToken StopToken => _stop.Token;

        public void Stop()
        {
            _stopCompletion = _stop.CancelAsync();
            Queue.Writer.TryComplete();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _stopCompletion;
            }
            finally
            {
                _stop.Dispose();
            }
        }
    }

    private sealed class PlaybackResponse(
        ChannelPlaybackService service, PlaybackSession session, PlaybackViewer viewer) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => service.ServeAsync(httpContext, session, viewer);
    }

    private sealed class PlaybackOutput : Stream
    {
        private const int BlockSize = 188 * 128;
        private static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(40);
        private readonly object _sync = new();
        private readonly Action<ReadOnlyMemory<byte>> _publish;
        private readonly Timer _flushTimer;
        private byte[]? _buffer;
        private int _length;
        private bool _started;
        private bool _flushScheduled;

        public PlaybackOutput(Action<ReadOnlyMemory<byte>> publish)
        {
            _publish = publish;
            _flushTimer = new Timer(static state => ((PlaybackOutput)state!).FlushOnTimeout(), this,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        public override void Write(ReadOnlySpan<byte> data)
        {
            lock (_sync)
            {
                WriteCore(data);
            }
        }

        private void WriteCore(ReadOnlySpan<byte> data)
        {
            if (!_started && !data.IsEmpty)
            {
                if (!MpegTsValidator.LooksLikeMpegTs(data))
                {
                    throw new InvalidDataException("播放源返回内容不是 MPEG-TS");
                }

                // 首个数据块立即发送，后续小 UDP 包合并成一块后再分发给各播放器。
                var count = Math.Min(data.Length, BlockSize);
                _publish(data[..count].ToArray());
                data = data[count..];
                _started = true;
            }

            while (!data.IsEmpty)
            {
                if (_buffer is null)
                {
                    if (data.Length >= BlockSize)
                    {
                        _publish(data[..BlockSize].ToArray());
                        data = data[BlockSize..];
                        continue;
                    }

                    _buffer = new byte[BlockSize];
                    if (!_flushScheduled)
                    {
                        _flushScheduled = true;
                        _flushTimer.Change(FlushDelay, Timeout.InfiniteTimeSpan);
                    }
                }
                var count = Math.Min(data.Length, BlockSize - _length);
                data[..count].CopyTo(_buffer.AsSpan(_length));
                _length += count;
                data = data[count..];
                if (_length == BlockSize)
                {
                    FlushCore();
                }
            }
        }

        public override void Flush()
        {
            lock (_sync)
            {
                FlushCore();
            }
        }

        private void FlushOnTimeout()
        {
            lock (_sync)
            {
                _flushScheduled = false;
                FlushCore();
            }
        }

        private void FlushCore()
        {
            if (_buffer is not null)
            {
                // 分发后的数据由播放器队列共享，收流端不再修改它。
                _publish(_buffer.AsMemory(0, _length));
                _buffer = null;
                _length = 0;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _flushTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Flush();
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
