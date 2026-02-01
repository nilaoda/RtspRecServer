export type PageKey = 'tasks' | 'channels' | 'recordings' | 'settings'

export type AppConfig = {
  maxRecordingTasks: number
}

export type RecordingStatus = 'Pending' | 'Recording' | 'Completed' | 'Failed' | 'Stopped' | 0 | 1 | 2 | 3 | 4

export type RecordingTaskDto = {
  id: number
  channelId: number
  channelName: string
  taskName: string
  startTime: string
  endTime: string
  status: RecordingStatus
  bytesWritten: number
  filePath?: string
  errorMessage?: string
  startedAt?: string
  finishedAt?: string
}

export type ChannelConfig = {
  id: number
  name: string
  url: string
}

export type RecordingFileInfo = {
  fileName: string
  filePath: string
  fileSizeBytes: number
  recordedAt: string
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

export type WebSocketMessage = TaskUpdateMessage | SystemStatusMessage
