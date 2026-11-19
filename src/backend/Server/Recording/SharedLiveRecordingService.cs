using System.Threading.Channels;
using RtspRecServer.Server.Recording.TsAnalysis;
using RtspRecServer.Shared;

namespace RtspRecServer.Server.Recording;

internal sealed class SharedLiveRecordingService(
    HttpLiveRecordingService http,
    UdpLiveRecordingService udp,
    ILogger<SharedLiveRecordingService> logger)
{
    // 按块的最大尺寸控制每个任务的缓冲上限。队列只在实际积压时持有数据。
    private const int QueueCapacity = 50 * 1024 * 1024 / BatchedTsStream.BlockSize;
    private readonly object _sync = new();
    private readonly Dictionary<LiveRecordingSourceKey, SourceSession> _sources = [];

    public async Task<RecordingResult> RecordAsync(
        RecordingTask task,
        LiveRecordingSourceKey key,
        string outputPath,
        Action<RecordingProgress> onProgress,
        CancellationToken cancellationToken,
        Stream? output = null)
    {
        long bytesWritten = 0;
        using var analysis = new TsLiveAnalysisSession(task.TaskName);

        RecordingResult Finish(string? error) => new()
        {
            Success = error is null && !cancellationToken.IsCancellationRequested,
            ErrorMessage = error,
            BytesWritten = bytesWritten,
            OutputPath = outputPath,
            TsCheck = analysis.Complete()
        };

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (output is null && Path.GetDirectoryName(outputPath) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            await using var file = output is null
                ? new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous)
                : null;
            await using var buffer = file is null ? null : new BufferedStream(file, 128 * 1024);
            var sink = output ?? buffer!;
            await using var subscription = Subscribe(key, task);
            using var stop = cancellationToken.UnsafeRegister(static state => ((Subscription)state!).Stop(), subscription);
            var lastProgress = DateTime.UtcNow;

            // 停录时先关闭这个订阅，再写完它已经收到的块。其他订阅继续收流。
            await foreach (var data in subscription.Queue.Reader.ReadAllAsync())
            {
                await sink.WriteAsync(data, CancellationToken.None);
                bytesWritten += data.Length;
                analysis.Append(data.Span);
                var now = DateTime.UtcNow;
                if (now - lastProgress >= TimeSpan.FromSeconds(1))
                {
                    onProgress(new RecordingProgress { BytesWritten = bytesWritten, TsCheck = analysis.GetSummary() });
                    lastProgress = now;
                }
            }

            await sink.FlushAsync(CancellationToken.None);
            onProgress(new RecordingProgress { BytesWritten = bytesWritten, TsCheck = analysis.GetSummary() });
            return Finish(subscription.ErrorMessage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Finish(null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "直播任务 {TaskId} 写入录像失败", task.Id);
            return Finish(ex.Message);
        }
    }

    private Subscription Subscribe(LiveRecordingSourceKey key, RecordingTask task)
    {
        lock (_sync)
        {
            _sources.TryGetValue(key, out var previous);
            if (previous?.TrySubscribe(task.Id) is { } existing)
            {
                return existing;
            }

            var source = new SourceSession();
            var subscription = source.TrySubscribe(task.Id)!;
            _sources[key] = source;
            _ = Task.Run(() => ReceiveAsync(key, task, source, previous?.Completion.Task ?? Task.CompletedTask));
            return subscription;
        }
    }

    public Dictionary<long, int> GetSharedTaskCounts()
    {
        var counts = new Dictionary<long, int>();
        lock (_sync)
        {
            foreach (var source in _sources.Values)
            {
                source.AddSharedTaskCounts(counts);
            }
        }
        return counts;
    }

    private async Task ReceiveAsync(LiveRecordingSourceKey key, RecordingTask task, SourceSession source, Task previous)
    {
        string? error = null;
        try
        {
            // 上一批订阅刚退出时，等原连接关闭后再拉同一来源。
            await previous;
            var token = source.Cancellation.Token;
            token.ThrowIfCancellationRequested();
            var result = key.HttpUrl is { } url
                ? await http.ForwardAsync(task with { Url = url }, source.Output, token)
                : await udp.ForwardAsync(task, source.Output, token);
            error = result.ErrorMessage ?? (token.IsCancellationRequested ? null : "直播源已停止");
        }
        catch (OperationCanceledException) when (source.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "共享直播源收流失败，频道 {ChannelId}", task.ChannelId);
            error = ex.Message;
        }
        finally
        {
            source.Complete(error);
            lock (_sync)
            {
                if (_sources.GetValueOrDefault(key) == source)
                {
                    _sources.Remove(key);
                }
            }

            try
            {
                await source.StopCompletion;
            }
            finally
            {
                source.Output.Dispose();
                source.Cancellation.Dispose();
                source.Completion.TrySetResult();
            }
        }
    }

    private sealed class SourceSession
    {
        private readonly List<Subscription> _subscriptions = [];
        private bool _closed;

        public SourceSession() => Output = new BatchedTsStream(Publish, alignPackets: true);

        public BatchedTsStream Output { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StopCompletion { get; private set; } = Task.CompletedTask;

        public Subscription? TrySubscribe(long taskId)
        {
            lock (Output.SyncRoot)
            {
                if (_closed)
                {
                    return null;
                }

                // 先给已有任务发送待发的完整 TS 包，新任务从之后的数据开始写。
                Output.Flush();
                if (_closed)
                {
                    return null;
                }

                var subscription = new Subscription(this, taskId);
                _subscriptions.Add(subscription);
                return subscription;
            }
        }

        public void AddSharedTaskCounts(Dictionary<long, int> counts)
        {
            lock (Output.SyncRoot)
            {
                if (_closed || _subscriptions.Count < 2)
                {
                    return;
                }

                foreach (var subscription in _subscriptions)
                {
                    counts[subscription.TaskId] = _subscriptions.Count;
                }
            }
        }

        public Task Unsubscribe(Subscription subscription)
        {
            lock (Output.SyncRoot)
            {
                if (_subscriptions.Contains(subscription))
                {
                    Output.Flush();
                    if (_subscriptions.Count == 1 && _subscriptions[0] == subscription)
                    {
                        // 最后一个任务等上游收尾，接收 UDP 队列、RTP 重排窗口和 HTTP 读缓冲中已有的数据。
                        RequestStop();
                    }
                    else if (_subscriptions.Remove(subscription))
                    {
                        subscription.Complete(null);
                    }
                }

                // 写入失败或队列满也必须等最后一路收流关闭，以便调度器释放名额。
                return _closed ? Completion.Task : Task.CompletedTask;
            }
        }

        public void Complete(string? error)
        {
            lock (Output.SyncRoot)
            {
                _closed = true;
                Output.Flush();
                foreach (var subscription in _subscriptions)
                {
                    subscription.Complete(error);
                }
                _subscriptions.Clear();
            }
        }

        // BatchedTsStream 调用本方法时持有 Output.SyncRoot。
        private void Publish(ReadOnlyMemory<byte> data)
        {
            for (var i = _subscriptions.Count - 1; i >= 0; i--)
            {
                var subscription = _subscriptions[i];
                if (!subscription.Queue.Writer.TryWrite(data))
                {
                    _subscriptions.RemoveAt(i);
                    subscription.Complete("录像写入缓冲已满，保存已接收的数据后停止");
                }
            }

            StopIfUnused();
        }

        private void StopIfUnused()
        {
            if (_subscriptions.Count == 0)
            {
                RequestStop();
            }
        }

        private void RequestStop()
        {
            if (!_closed)
            {
                _closed = true;
                StopCompletion = Cancellation.CancelAsync();
            }
        }
    }

    private sealed class Subscription(SourceSession source, long taskId) : IAsyncDisposable
    {
        public long TaskId { get; } = taskId;
        public Channel<ReadOnlyMemory<byte>> Queue { get; } = Channel.CreateBounded<ReadOnlyMemory<byte>>(
            new BoundedChannelOptions(QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

        public string? ErrorMessage { get; private set; }

        public void Complete(string? error)
        {
            ErrorMessage = error;
            Queue.Writer.TryComplete();
        }

        public void Stop() => _ = source.Unsubscribe(this);

        public ValueTask DisposeAsync() => new(source.Unsubscribe(this));
    }
}
