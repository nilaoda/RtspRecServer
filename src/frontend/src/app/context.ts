import { createContext, useContext } from 'react'
import type {
  AppConfig,
  ChannelConfig,
  ChannelProbeResult,
  RecordingFileDeleteResult,
  RecordingFileInfo,
  RecordingTaskDto,
  RecordingTimer,
  SystemStatus,
  TaskErrorLogEntry,
  TimerUpsertPayload,
} from '../types'
import type { UpdateConfigPayload } from '../services/api'

export type AppContextValue = {
  tasks: RecordingTaskDto[]
  channels: ChannelConfig[]
  channelProfiles: Record<number, ChannelProbeResult>
  recordings: RecordingFileInfo[]
  timers: RecordingTimer[]
  systemStatus: SystemStatus | null
  appConfig: AppConfig | null
  taskErrorLog: TaskErrorLogEntry[]
  now: Date
  connectionStatus: 'connecting' | 'connected' | 'reconnecting' | 'disconnected'
  themeMode: 'dark' | 'light'
  setThemeMode: (mode: 'dark' | 'light') => void
  reloadTasks: () => Promise<void>
  reloadChannels: () => Promise<void>
  reloadChannelProfiles: () => Promise<void>
  reloadRecordings: () => Promise<void>
  reloadTimers: () => Promise<void>
  reloadConfig: () => Promise<void>
  createTask: (payload: {
    channelId: number
    startTime: string
    endTime: string
    taskName: string | null
  }) => Promise<boolean>
  stopTask: (id: number) => Promise<void>
  stopTasks: (ids: number[]) => Promise<void>
  updateTaskEndTime: (id: number, endTime: string) => Promise<boolean>
  deleteTask: (id: number) => Promise<void>
  deleteTasks: (ids: number[]) => Promise<void>
  deleteRecordings: (fileNames: string[]) => Promise<RecordingFileDeleteResult | null>
  clearTaskErrorLog: () => Promise<void>
  addChannel: (payload: { name: string; url: string; group?: string | null }) => Promise<void>
  updateChannel: (payload: ChannelConfig) => Promise<void>
  deleteChannel: (id: number) => Promise<void>
  createTimer: (payload: TimerUpsertPayload) => Promise<void>
  updateTimer: (id: number, payload: TimerUpsertPayload) => Promise<void>
  deleteTimer: (id: number) => Promise<void>
  updateConfig: (payload: UpdateConfigPayload) => Promise<void>
  getTaskMediaInfo: (id: number) => Promise<string>
  getRecordingMediaInfo: (filePath: string) => Promise<string>
  totalBitrateKbps: number // 实时总码率
}

export const AppContext = createContext<AppContextValue | null>(null)

export const useAppContext = () => {
  const context = useContext(AppContext)
  if (!context) {
    throw new Error('AppContext not initialized')
  }
  return context
}
