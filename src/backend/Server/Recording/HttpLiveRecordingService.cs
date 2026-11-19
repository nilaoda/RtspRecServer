using System.IO.Pipelines;
using Serilog;
using RtspRecServer.Server.Recording.TsAnalysis;
using RtspRecServer.Shared;

namespace RtspRecServer.Server.Recording;

public sealed class HttpLiveRecordingService : IRecordingService
{
    private const int ReadBufferBytes = 64 * 1024;
    private const int PauseBufferBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);
    private static readonly HttpClient HttpClient = CreateHttpClient();

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

        try
        {
            if (output is null)
            {
                EnsureOutputPath(outputPath);
            }

            using var response = await HttpClient.GetAsync(task.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Finish(false, $"直播源返回 HTTP {(int)response.StatusCode} {response.ReasonPhrase}", bytesWritten);
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase))
            {
                return Finish(false, $"直播源返回非视频内容 ({mediaType})", bytesWritten);
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var ownedFile = output is null
                ? new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 32 * 1024, FileOptions.Asynchronous)
                : null;
            var sink = output ?? ownedFile!;
            var pipe = new Pipe(new PipeOptions(
                pauseWriterThreshold: PauseBufferBytes,
                resumeWriterThreshold: PauseBufferBytes / 2,
                minimumSegmentSize: ReadBufferBytes,
                useSynchronizationContext: false));
            using var stopReading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var lastProgress = DateTime.UtcNow;

            async Task WriteOutputAsync()
            {
                try
                {
                    while (true)
                    {
                        var read = await pipe.Reader.ReadAsync();
                        foreach (var segment in read.Buffer)
                        {
                            // 停录时让已收到的数据完成写入和检测。
                            await sink.WriteAsync(segment);
                            bytesWritten += segment.Length;
                            analysis.Append(segment.Span);
                        }

                        pipe.Reader.AdvanceTo(read.Buffer.End);
                        var now = DateTime.UtcNow;
                        if ((now - lastProgress).TotalSeconds >= 1.0)
                        {
                            onProgress(new RecordingProgress { BytesWritten = bytesWritten, TsCheck = analysis.GetSummary() });
                            lastProgress = now;
                        }

                        if (read.IsCompleted)
                        {
                            break;
                        }
                    }
                }
                catch
                {
                    stopReading.Cancel();
                    throw;
                }
                finally
                {
                    await pipe.Reader.CompleteAsync();
                }
            }

            var writing = WriteOutputAsync();
            string? streamError = null;
            try
            {
                streamError = await ReadSourceAsync(source, pipe.Writer, task.Url, stopReading.Token);
            }
            catch (OperationCanceledException) when (stopReading.IsCancellationRequested)
            {
                // 停录或写入端失败，先等待写入端退出，再汇报结果。
            }
            finally
            {
                await pipe.Writer.CompleteAsync();
                await writing;
            }

            await sink.FlushAsync(CancellationToken.None);
            onProgress(new RecordingProgress { BytesWritten = bytesWritten, TsCheck = analysis.GetSummary() });

            return Finish(!cancellationToken.IsCancellationRequested && streamError is null, streamError, bytesWritten);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            onProgress(new RecordingProgress { BytesWritten = bytesWritten, TsCheck = analysis.GetSummary() });
            return Finish(false, null, bytesWritten);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "HTTP 直播录制失败: {Url}", task.Url);
            return Finish(false, ex.Message, bytesWritten);
        }
    }

    private static async Task<string?> ReadSourceAsync(
        Stream source, PipeWriter writer, string url, CancellationToken cancellationToken)
    {
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var firstChunk = true;
        var reportedBackpressure = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            var buffer = writer.GetMemory(4096);
            int read;
            idleCts.CancelAfter(IdleTimeout);
            try
            {
                read = await source.ReadAsync(buffer[..Math.Min(buffer.Length, ReadBufferBytes)], idleCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Log.Warning("HTTP 直播流超过 {Seconds} 秒无数据: {Url}", IdleTimeout.TotalSeconds, url);
                return "直播流无数据超时";
            }
            finally
            {
                // 缓冲满时的等待属于本机背压，不计入上游无数据超时。
                idleCts.CancelAfter(Timeout.InfiniteTimeSpan);
            }

            if (read == 0)
            {
                return "直播流已结束";
            }

            if (firstChunk && !MpegTsValidator.LooksLikeMpegTs(buffer.Span[..read]))
            {
                return "直播源返回内容不是 MPEG-TS";
            }

            firstChunk = false;
            writer.Advance(read);
            var flushing = writer.FlushAsync(cancellationToken);
            if (!reportedBackpressure && !flushing.IsCompleted)
            {
                reportedBackpressure = true;
                Log.Warning("HTTP 直播收流缓冲已满，等待写盘或 TS 检测: {Url}", url);
            }

            var flushed = await flushing;
            if (flushed.IsCompleted || flushed.IsCanceled)
            {
                break;
            }
        }

        return null;
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AllowAutoRedirect = true
        };

        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
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
