using System.Text.Json.Serialization;

namespace RtspRecServer.Shared;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(AppConfigUpdateRequest))]
[JsonSerializable(typeof(ChannelConfig))]
[JsonSerializable(typeof(RecordingTask))]
[JsonSerializable(typeof(RecordingTaskDto))]
[JsonSerializable(typeof(RecordingTaskStatusUpdate))]
[JsonSerializable(typeof(RecordingTaskCreateRequest))]
[JsonSerializable(typeof(DiskStatus))]
[JsonSerializable(typeof(RecordingFileInfo))]
[JsonSerializable(typeof(MediaInfoResponse))]
[JsonSerializable(typeof(SystemStatus))]
[JsonSerializable(typeof(List<ChannelConfig>))]
[JsonSerializable(typeof(List<RecordingTask>))]
[JsonSerializable(typeof(List<RecordingTaskDto>))]
[JsonSerializable(typeof(List<RecordingFileInfo>))]
[JsonSerializable(typeof(TaskUpdateMessage))]
[JsonSerializable(typeof(SystemStatusMessage))]
public sealed partial class RtspJsonContext : JsonSerializerContext
{
}
