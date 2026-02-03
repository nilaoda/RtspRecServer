using System.Text.Json.Serialization;

namespace RtspRecServer.Shared;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(EpgConfig))]
[JsonSerializable(typeof(AppConfigUpdateRequest))]
[JsonSerializable(typeof(ChannelConfig))]
[JsonSerializable(typeof(RecordingTask))]
[JsonSerializable(typeof(RecordingTaskDto))]
[JsonSerializable(typeof(RecordingTaskStatusUpdate))]
[JsonSerializable(typeof(RecordingTaskCreateRequest))]
[JsonSerializable(typeof(DiskStatus))]
[JsonSerializable(typeof(RecordingFileInfo))]
[JsonSerializable(typeof(MediaInfoResponse))]
[JsonSerializable(typeof(ApiResponse))]
[JsonSerializable(typeof(SystemStatus))]
[JsonSerializable(typeof(EpgStatus))]
[JsonSerializable(typeof(EpgDataCache))]
[JsonSerializable(typeof(EpgChannel))]
[JsonSerializable(typeof(EpgProgram))]
[JsonSerializable(typeof(CurrentProgramInfo))]
[JsonSerializable(typeof(List<ChannelConfig>))]
[JsonSerializable(typeof(List<RecordingTask>))]
[JsonSerializable(typeof(List<RecordingTaskDto>))]
[JsonSerializable(typeof(List<RecordingFileInfo>))]
[JsonSerializable(typeof(List<EpgChannel>))]
[JsonSerializable(typeof(List<EpgProgram>))]
[JsonSerializable(typeof(List<CurrentProgramInfo>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, List<EpgProgram>>))]
[JsonSerializable(typeof(TaskUpdateMessage))]
[JsonSerializable(typeof(SystemStatusMessage))]
[JsonSerializable(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails))]
public sealed partial class RtspJsonContext : JsonSerializerContext
{
}
