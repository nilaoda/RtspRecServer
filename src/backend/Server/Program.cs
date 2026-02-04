using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using RtspRecServer.Server.Recording;
using RtspRecServer.Server.Services;
using RtspRecServer.Server.Services.Epg;
using RtspRecServer.Shared;
using Serilog;
using Serilog.Events;

// 配置Serilog日志
var logsPath = Path.Combine(AppContext.BaseDirectory, "logs");
Directory.CreateDirectory(logsPath);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .WriteTo.Console()
    .WriteTo.File(
        path: Path.Combine(logsPath, $"log-{DateTime.Now:yyyyMMdd}.txt"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

try
{
    Log.Information("应用程序启动中...");
    
    var builder = WebApplication.CreateBuilder(args);
    var bootConfig = LoadAppConfig();
    var serverUrls = ApplyServerUrls(builder, bootConfig);

    builder.Services.ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.TypeInfoResolverChain.Insert(0, RtspJsonContext.Default);
        options.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    });

    builder.Services.Configure<HostOptions>(options =>
    {
        options.ShutdownTimeout = TimeSpan.FromSeconds(2);
    });

    builder.Services.AddResponseCompression(options =>
    {
        options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/octet-stream"]);
    });

    builder.Services.AddSingleton<ConfigurationService>();
    builder.Services.AddSingleton<RecordingTaskStore>();
    builder.Services.AddSingleton<IRecordingService, RtspRecordingService>();
    builder.Services.AddSingleton<WebSocketConnectionManager>();
    builder.Services.AddSingleton<IRecordingNotifier, WebSocketRecordingNotifier>();
    builder.Services.AddSingleton<RecordingManager>();
    builder.Services.AddHostedService<RecordingBackgroundService>();

    // 添加EPG服务
    builder.Services.AddEpgServices(builder.Configuration);
    
    builder.Host.UseSerilog();

    Log.Information("正在构建应用程序...");
    var app = builder.Build();
    Log.Information("应用程序构建完成");
  
    Log.Information("配置中间件...");
    app.UseResponseCompression();
    app.UseWebSockets();

    app.Use(async (context, next) =>
    {
        var configService = context.RequestServices.GetRequiredService<ConfigurationService>();
        var config = configService.GetAppConfig();
        if (!config.UseAuth)
        {
            await next();
            return;
        }

        var authHeader = context.Request.Headers.Authorization.ToString();
        if (!authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers.WWWAuthenticate = "Basic";
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var encoded = authHeader["Basic ".Length..].Trim();
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch
        {
            context.Response.Headers.WWWAuthenticate = "Basic";
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var parts = decoded.Split(':', 2);
        if (parts.Length != 2 || parts[0] != config.Username || parts[1] != config.Password)
        {
            context.Response.Headers.WWWAuthenticate = "Basic";
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next();
    });

    Log.Information("配置静态文件...");
    var embeddedProvider = new EmbeddedFileProvider(typeof(Program).Assembly, "Server.wwwroot");
    app.Environment.WebRootFileProvider = embeddedProvider;
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = embeddedProvider
    });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = embeddedProvider
    });

    Log.Information("映射端点...");
    app.Map("/ws", async context =>
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var socket = await context.WebSockets.AcceptWebSocketAsync();
        var manager = context.RequestServices.GetRequiredService<WebSocketConnectionManager>();
        await manager.HandleConnectionAsync(socket, context.RequestAborted);
    });

    app.MapGet("/api/config", (ConfigurationService service) =>
    {
        return Results.Ok(service.GetAppConfig());
    });

    app.MapPut("/api/config", (ConfigurationService service, AppConfigUpdateRequest request) =>
    {
        var updated = service.UpdateAppConfig(request);
        return Results.Ok(updated);
    });

    app.MapGet("/api/channels", (ConfigurationService service) =>
    {
        return Results.Ok(service.GetChannels());
    });

    app.MapPost("/api/channels", (ConfigurationService service, ChannelConfig channel) =>
    {
        return Results.Ok(service.AddChannel(channel));
    });

    app.MapPut("/api/channels/{id:int}", (ConfigurationService service, int id, ChannelConfig channel) =>
    {
        var updated = service.UpdateChannel(id, channel);
        return updated is null ? Results.NotFound(ApiResponse.Create("频道不存在")) : Results.Ok(updated);
    });

    app.MapDelete("/api/channels/{id:int}", (ConfigurationService service, int id) =>
    {
        return service.DeleteChannel(id)
            ? Results.Ok(ApiResponse.Create("频道已删除"))
            : Results.NotFound(ApiResponse.Create("频道不存在"));
    });

    app.MapGet("/api/tasks", (RecordingManager manager) =>
    {
        var tasks = manager.GetAllTasks().Select(ToDto).ToList();
        return Results.Ok(tasks);
    });

    app.MapGet("/api/tasks/{id:long}", (RecordingManager manager, long id) =>
    {
        var task = manager.GetTask(id);
        return task is null ? Results.NotFound(ApiResponse.Create("任务不存在")) : Results.Ok(ToDto(task));
    });

    app.MapPost("/api/tasks", async (RecordingManager manager, RecordingTaskCreateRequest request, CancellationToken cancellationToken) =>
    {
        try
        {
            var task = await manager.CreateTaskAsync(request, cancellationToken);
            return Results.Ok(ToDto(task));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(ApiResponse.Create(ex.Message));
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    });

    app.MapPost("/api/tasks/{id:long}/stop", async (RecordingManager manager, long id, CancellationToken cancellationToken) =>
    {
        var task = manager.GetTask(id);
        if (task is null)
        {
            return Results.NotFound(ApiResponse.Create("任务不存在"));
        }
        var stopped = await manager.StopTaskAsync(id, cancellationToken);
        return stopped ? Results.Ok(ApiResponse.Create("停止请求已发送")) : Results.BadRequest(ApiResponse.Create("任务未在录制中"));
    });

    app.MapDelete("/api/tasks/{id:long}", (RecordingManager manager, long id) =>
    {
        var task = manager.GetTask(id);
        if (task is null)
        {
            return Results.NotFound(ApiResponse.Create("任务不存在"));
        }
        if (manager.IsActive(id))
        {
            return Results.BadRequest(ApiResponse.Create("任务正在录制，无法删除"));
        }
        return manager.DeleteTask(id)
            ? Results.Ok(ApiResponse.Create("任务已删除"))
            : Results.BadRequest(ApiResponse.Create("删除失败"));
    });

    app.MapGet("/api/tasks/{id:long}/download", (RecordingManager manager, long id) =>
    {
        var task = manager.GetTask(id);
        if (task?.FilePath is null || !File.Exists(task.FilePath))
        {
            return Results.NotFound(ApiResponse.Create("录制文件不存在"));
        }

        var fileName = Path.GetFileName(task.FilePath);
        return Results.File(task.FilePath, "application/octet-stream", fileName);
    });

    app.MapGet("/api/tasks/{id:long}/mediainfo", async (RecordingManager manager, long id) =>
    {
        var task = manager.GetTask(id);
        if (task?.FilePath is null || !File.Exists(task.FilePath))
        {
            return Results.NotFound(ApiResponse.Create("录制文件不存在"));
        }

        var info = await GetMediaInfoAsync(task.FilePath);
        return Results.Ok(new MediaInfoResponse { Content = info });
    });

    app.MapGet("/api/system/status", (ConfigurationService configurationService) =>
    {
        var config = configurationService.GetAppConfig();
        var disk = GetDiskStatus(config.RecordPath);
        var status = new SystemStatus
        {
            CurrentUser = Environment.UserName,
            SystemTime = DateTimeOffset.UtcNow,
            Disk = disk
        };
        return Results.Ok(status);
    });

    app.MapGet("/api/recordings", (ConfigurationService configurationService) =>
    {
        var config = configurationService.GetAppConfig();
        var recordPath = ResolveRecordPath(config.RecordPath);
        if (!Directory.Exists(recordPath))
        {
            return Results.Ok(new List<RecordingFileInfo>());
        }

        var files = Directory.EnumerateFiles(recordPath, "*.*", SearchOption.TopDirectoryOnly)
            .Select(path =>
            {
                var info = new FileInfo(path);
                return new RecordingFileInfo
                {
                    FileName = info.Name,
                    FilePath = info.FullName,
                    FileSizeBytes = info.Length,
                    RecordedAt = info.LastWriteTimeUtc
                };
            })
            .OrderByDescending(f => f.RecordedAt)
            .ToList();

        return Results.Ok(files);
    });

    app.MapGet("/api/recordings/mediainfo", async (ConfigurationService configurationService, string filePath) =>
    {
        var config = configurationService.GetAppConfig();
        var recordPath = ResolveRecordPath(config.RecordPath);
        if (!IsPathUnderRoot(recordPath, filePath))
        {
            return Results.BadRequest(ApiResponse.Create("非法路径"));
        }

        if (!File.Exists(filePath))
        {
            return Results.NotFound(ApiResponse.Create("录制文件不存在"));
        }

        var info = await GetMediaInfoAsync(filePath);
        return Results.Ok(new MediaInfoResponse { Content = info });
    });

    Log.Information("映射EPG端点...");
    // 映射EPG API端点
    app.MapEpgEndpoints();
    Log.Information("EPG端点映射完成");

    Log.Information("注册生命周期回调...");
    app.Lifetime.ApplicationStopping.Register(() =>
    {
        Log.Information("正在停止所有录制会话...");
        var manager = app.Services.GetRequiredService<RecordingManager>();
        manager.RequestStopAllActiveSessions();
    });
    Log.Information("生命周期回调注册完成");

    Log.Information("完成配置，准备启动...");
    app.MapFallbackToFile("index.html");
    Log.Information("Fallback路由配置完成");

    Log.Information("监听地址: {Urls}", string.Join(", ", serverUrls));
    Log.Information("正在启动 Web 主机...");
    app.Run();

}
catch (Exception ex)
{
    Log.Fatal(ex, "应用程序启动失败");
    throw;
}
finally
{
    Log.Information("应用程序关闭中...");
    Log.CloseAndFlush();
}

static RecordingTaskDto ToDto(RecordingTask task)
{
    return new RecordingTaskDto
    {
        Id = task.Id,
        ChannelId = task.ChannelId,
        ChannelName = task.ChannelName,
        TaskName = task.TaskName,
        StartTime = task.StartTime,
        EndTime = task.EndTime,
        Status = task.Status,
        BytesWritten = task.BytesWritten,
        FilePath = task.FilePath,
        ErrorMessage = task.ErrorMessage,
        StartedAt = task.StartedAt,
        FinishedAt = task.FinishedAt,
        DisplayName = task.DisplayName
    };
}

static DiskStatus GetDiskStatus(string recordPath)
{
    try
    {
        var fullPath = ResolveRecordPath(recordPath);
        var drives = DriveInfo.GetDrives().Where(d => d.IsReady).ToList();
        
        // 寻找最长匹配的根路径
        var match = drives
            .Select(d => new { Drive = d, Root = d.RootDirectory.FullName })
            .Where(d => fullPath.StartsWith(d.Root, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.Root.Length)
            .FirstOrDefault();

        if (match != null)
        {
            return new DiskStatus
            {
                TotalBytes = match.Drive.TotalSize,
                FreeBytes = match.Drive.AvailableFreeSpace
            };
        }
    }
    catch
    {
        // 忽略错误，返回默认的0
    }

    return new DiskStatus(); // 获取不到直接返回0
}

static async Task<string> GetMediaInfoAsync(string filePath)
{
    try
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "mediainfo",
            Arguments = $"\"{filePath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var outputTask = ReadStreamAsync(process.StandardOutput.BaseStream);
        var errorTask = ReadStreamAsync(process.StandardError.BaseStream);
        await Task.WhenAll(outputTask, errorTask);
        await process.WaitForExitAsync();

        var output = DecodeOutput(outputTask.Result);
        var error = DecodeOutput(errorTask.Result);
        var raw = string.IsNullOrWhiteSpace(output) ? error : output;
        return NormalizeLineEndings(raw);
    }
    catch (Exception ex)
    {
        return ex.Message;
    }
}

static string NormalizeLineEndings(string value)
{
    return value.Replace("\r\n", "\n").Replace("\r", "\n");
}

static async Task<byte[]> ReadStreamAsync(Stream stream)
{
    using var memory = new MemoryStream();
    await stream.CopyToAsync(memory);
    return memory.ToArray();
}

static string DecodeOutput(byte[] bytes)
{
    if (bytes.Length == 0)
    {
        return string.Empty;
    }

    try
    {
        var utf8 = new UTF8Encoding(false, true);
        return utf8.GetString(bytes);
    }
    catch
    {
        return Encoding.GetEncoding("GB18030").GetString(bytes);
    }
}

static string ResolveRecordPath(string recordPath)
{
    var basePath = AppContext.BaseDirectory;
    var safePath = string.IsNullOrWhiteSpace(recordPath) ? "./records" : recordPath;
    var combined = Path.IsPathRooted(safePath) ? safePath : Path.Combine(basePath, safePath);
    return Path.GetFullPath(combined);
}

static bool IsPathUnderRoot(string rootPath, string targetPath)
{
    var fullRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    var fullTarget = Path.GetFullPath(targetPath);
    return fullTarget.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
}

static AppConfig LoadAppConfig()
{
    try
    {
        var basePath = AppContext.BaseDirectory;
        var configPath = Path.Combine(basePath, "recsettings.json");
        if (!File.Exists(configPath))
        {
            return new AppConfig();
        }

        var json = File.ReadAllText(configPath);
        return JsonSerializer.Deserialize(json, RtspJsonContext.Default.AppConfig) ?? new AppConfig();
    }
    catch
    {
        return new AppConfig();
    }
}

static string[] ApplyServerUrls(WebApplicationBuilder builder, AppConfig config)
{
    var port = config.Port <= 0 ? 8080 : config.Port;
    var hosts = config.Host is { Length: > 0 } ? config.Host : ["0.0.0.0"];
    var urls = hosts.Select(host => $"http://{host}:{port}").ToArray();
    builder.WebHost.UseUrls(urls);
    return urls;
}