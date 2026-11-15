import { createContext, useContext } from 'react'
import type {
  AppConfig,
  ChannelConfig,
  RecordingFileInfo,
  RecordingTaskDto,
  RecordingTimer,
  SystemStatus,
  TimerUpsertPayload,
} from '../types'

export type AppContextValue = {
  tasks: RecordingTaskDto[]
  channels: ChannelConfig[]
  recordings: RecordingFileInfo[]
  timers: RecordingTimer[]
  systemStatus: SystemStatus | null
  appConfig: AppConfig | null
  now: Date
  connectionStatus: 'connecting' | 'connected' | 'reconnecting' | 'disconnected'
  themeMode: 'dark' | 'light'
  setThemeMode: (mode: 'dark' | 'light') => void
  reloadTasks: () => Promise<void>
  reloadChannels: () => Promise<void>
  reloadRecordings: () => Promise<void>
  reloadTimers: () => Promise<void>
  reloadConfig: () => Promise<void>
  createTask: (payload: {
    channelId: number
    startTime: string
    endTime: string
    taskName: string | null
  }) => Promise<void>
  stopTask: (id: number) => Promise<void>
  stopTasks: (ids: number[]) => Promise<void>
  updateTaskEndTime: (id: number, endTime: string) => Promise<boolean>
  deleteTask: (id: number) => Promise<void>
  deleteTasks: (ids: number[]) => Promise<void>
  addChannel: (payload: { name: string; url: string }) => Promise<void>
  updateChannel: (payload: ChannelConfig) => Promise<void>
  deleteChannel: (id: number) => Promise<void>
  createTimer: (payload: TimerUpsertPayload) => Promise<void>
  updateTimer: (id: number, payload: TimerUpsertPayload) => Promise<void>
  deleteTimer: (id: number) => Promise<void>
  updateConfig: (payload: { maxRecordingTasks: number; recordingTransport: 'MP2T/TCP' | 'MP2T/RTP/TCP'; writeRecordingLog: boolean }) => Promise<void>
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
