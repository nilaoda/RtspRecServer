using System.Buffers;
using Serilog;
using RtspRecServer.Shared;

namespace RtspRecServer.Server.Recording;

public sealed class HttpLiveRecordingService : IRecordingService
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public async Task<RecordingResult> RecordAsync(
        RecordingTask task,
        string outputPath,
        TimeSpan? targetDuration,
        string recordingTransport,
        Action<RecordingProgress> onProgress,
        CancellationToken cancellationToken)
    {
        var bytesWritten = 0L;

        try
        {
            EnsureOutputPath(outputPath);
            using var response = await HttpClient.GetAsync(task.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Failure($"直播源返回 HTTP {(int)response.StatusCode} {response.ReasonPhrase}", bytesWritten, outputPath);
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase))
            {
                return Failure($"直播源返回非视频内容 ({mediaType})", bytesWritten, outputPath);
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var file = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read, 32 * 1024, FileOptions.Asynchronous);
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            var lastProgress = DateTime.UtcNow;
            var firstChunk = true;

            try
            {
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                while (!cancellationToken.IsCancellationRequested)
                {
                    idleCts.CancelAfter(IdleTimeout);

                    int read;
                    try
                    {
                        read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), idleCts.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        Log.Warning("HTTP 直播流超过 {Seconds} 秒无数据: {Url}", IdleTimeout.TotalSeconds, task.Url);
                        onProgress(new RecordingProgress { BytesWritten = bytesWritten });
                        return Failure("直播流无数据超时", bytesWritten, outputPath);
                    }

                    if (read == 0)
                    {
                        onProgress(new RecordingProgress { BytesWritten = bytesWritten });
                        return Failure("直播流已结束", bytesWritten, outputPath);
                    }

                    if (firstChunk)
                    {
                        firstChunk = false;
                        if (!LooksLikeMpegTs(buffer.AsSpan(0, read)))
                        {
                            return Failure("直播源返回内容不是 MPEG-TS", bytesWritten, outputPath);
                        }
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    bytesWritten += read;

                    var now = DateTime.UtcNow;
                    if ((now - lastProgress).TotalSeconds >= 1.0)
                    {
                        onProgress(new RecordingProgress { BytesWritten = bytesWritten });
                        lastProgress = now;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            await file.FlushAsync(CancellationToken.None);
            onProgress(new RecordingProgress { BytesWritten = bytesWritten });

            return new RecordingResult
            {
                Success = !cancellationToken.IsCancellationRequested,
                BytesWritten = bytesWritten,
                OutputPath = outputPath
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            onProgress(new RecordingProgress { BytesWritten = bytesWritten });
            return new RecordingResult
            {
                Success = false,
                BytesWritten = bytesWritten,
                OutputPath = outputPath
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "HTTP 直播录制失败: {Url}", task.Url);
            return new RecordingResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                BytesWritten = bytesWritten,
                OutputPath = outputPath
            };
        }
    }

    private static RecordingResult Failure(string message, long bytesWritten, string outputPath)
    {
        return new RecordingResult
        {
            Success = false,
            ErrorMessage = message,
            BytesWritten = bytesWritten,
            OutputPath = outputPath
        };
    }

    private static bool LooksLikeMpegTs(ReadOnlySpan<byte> data)
    {
        if (data.Length < 564)
        {
            return true;
        }

        for (var offset = 0; offset <= 188 && offset + 188 < data.Length; offset++)
        {
            if (data[offset] == 0x47 && data[offset + 188] == 0x47)
            {
                return true;
            }
        }

        return false;
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
