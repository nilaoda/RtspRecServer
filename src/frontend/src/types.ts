export type PageKey = 'tasks' | 'channels' | 'recordings' | 'settings'

export type EpgConfig = {
  url: string
  updateTime: string
  cacheDurationHours: number
}

export type AppConfig = {
  port: number
  recordPath: string
  host: string[]
  useAuth: boolean
  username: string
  hasPassword: boolean
  maxRecordingTasks: number
  maxRecordingWriters: number
  recordingTransport: 'MP2T/TCP' | 'MP2T/RTP/TCP'
  writeRecordingLog: boolean
  taskErrorLogMaxEntries: number
  epg: EpgConfig
  restartRequiredFields: string[]
}

export type RecordingStatus = 'Pending' | 'Recording' | 'Completed' | 'Failed' | 'Stopped' | 0 | 1 | 2 | 3 | 4

export type TsStreamTrack = {
  pid: number
  label: string
}

export type TsCheckSummary = {
  verdict: 'pass' | 'warning' | 'error' | 'incomplete' | 'unknown'
  errorCount: number
  warningCount: number
  continuityErrors?: number | null
  packetCount: number
  bytesScanned: number
  bitrateKbps: number
  video?: string
  audio?: string
  videoTracks?: TsStreamTrack[]
  audioTracks?: TsStreamTrack[]
  pidCount: number
  lastEvent?: string
  anomalyEvents?: string[]
}

export type RecordingTaskDto = {
  id: number
  channelId: number
  channelName: string
  taskName: string
  displayName: string
  isLive: boolean
  startTime: string
  endTime: string
  status: RecordingStatus
  bytesWritten: number
  filePath?: string
  errorMessage?: string
  startedAt?: string
  finishedAt?: string
  currentBitrateKbps?: number
  pcrElapsedSeconds?: number
  tsCheck?: TsCheckSummary
  timerId?: number
}

// 定时录制规则：按频道 + 每周几 + 每日时间窗口自动生成录制任务
export type RecordingTimer = {
  id: number
  name: string
  channelId: number
  channelName: string
  url: string
  // "HH:mm"；endTime <= startTime 表示跨午夜窗口
  startTime: string
  endTime: string
  // 7 位布尔，0=周一 … 6=周日
  weekdays: boolean[]
  preOffsetMinutes: number
  postOffsetMinutes: number
  // "yyyy-MM-dd"，null 表示不限
  startDate?: string
  endDate?: string
  enabled: boolean
  lastTriggeredAt?: string
  createdAt: string
}

export type TimerUpsertPayload = {
  name: string
  channelId: number
  startTime: string
  endTime: string
  weekdays: boolean[]
  preOffsetMinutes: number
  postOffsetMinutes: number
  startDate?: string | null
  endDate?: string | null
  enabled: boolean
}

export type ChannelConfig = {
  id: number
  name: string
  url: string
  group?: string | null
}

export type ChannelProbeResult = {
  channelId: number
  accessible: boolean
  videoTracks: TsStreamTrack[]
  audioTracks: TsStreamTrack[]
  resolution?: string
  bitrateKbps?: number
  tsVerdict?: 'pass' | 'warning' | 'error' | 'incomplete' | 'unknown'
  errorCount: number
  warningCount: number
  errorMessage?: string
  checkedAt: string
  elapsedSeconds: number
  attempts?: number
}

export type ChannelRecordingSimulationUpdate = {
  id: string
  channelId: number
  durationSeconds: number
  status: 'running' | 'stopping' | 'stopped' | 'completed' | 'failed'
  elapsedSeconds: number
  bytesReceived: number
  tsCheck?: TsCheckSummary | null
  errorMessage?: string | null
}

export type ChannelDiagnosticsSnapshot = {
  scan?: { id: string; total: number; done: number; stopping: boolean; activeChannelIds: number[] } | null
  probeRevision: string
  probes?: ChannelProbeResult[] | null
  simulations: ChannelRecordingSimulationUpdate[]
}

export type ChannelBulkResult = {
  succeeded: number
  failed: number
  succeededIds: number[]
  failedIds: number[]
}

export type ChannelBulkEditItem = {
  id: number
  name?: string
  url?: string
  group?: string
  clearGroup?: boolean
}

export type ChannelBulkEditFailure = {
  id: number
  message: string
}

export type ChannelBulkEditResult = {
  succeeded: number
  failed: number
  succeededIds: number[]
  failedItems: ChannelBulkEditFailure[]
}

export type BulkTaskResult = {
  succeeded: number
  failed: number
  succeededIds: number[]
  failedIds: number[]
}

export type LogFileInfo = {
  fileName: string
  fileSizeBytes: number
  modifiedAt: string
}

export type LogFileContent = {
  fileName: string
  content: string
  truncated: boolean
  modifiedAt: string
}

export type RecordingFileInfo = {
  fileName: string
  filePath: string
  fileSizeBytes: number
  recordedAt: string
}

export type RecordingFileDeleteFailure = {
  fileName: string
  reason: string
}

export type RecordingFileDeleteResult = {
  succeeded: number
  failed: number
  succeededFiles: string[]
  failedItems: RecordingFileDeleteFailure[]
}

export type DiskStatus = {
  totalBytes: number
  freeBytes: number
}

export type SystemStatus = {
  currentUser: string
  systemTime: string
  disk: DiskStatus
}

export type TaskUpdateMessage = {
  type: 'taskUpdated'
  task: RecordingTaskDto
}

export type SystemStatusMessage = {
  type: 'systemStatus'
  status: SystemStatus
}

export type TaskErrorLogEntry = {
  id: number
  revision: number
  taskId: number
  taskName: string
  channelName: string
  fileName?: string
  severity: 'error' | 'warning'
  message: string
  identity: string
  timestamp: string
}

export type TaskErrorLogSnapshot = {
  generation: number
  entries: TaskErrorLogEntry[]
}

export type TaskErrorLogMessage = {
  type: 'taskErrorLog'
  generation: number
  reset: boolean
  entries: TaskErrorLogEntry[]
}

export type WebSocketMessage = TaskUpdateMessage | SystemStatusMessage | TaskErrorLogMessage
