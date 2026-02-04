import { createContext, useContext } from 'react'
import type {
  AppConfig,
  ChannelConfig,
  RecordingFileInfo,
  RecordingTaskDto,
  SystemStatus,
} from '../types'

export type AppContextValue = {
  tasks: RecordingTaskDto[]
  channels: ChannelConfig[]
  recordings: RecordingFileInfo[]
  systemStatus: SystemStatus | null
  appConfig: AppConfig | null
  now: Date
  connectionStatus: 'connecting' | 'connected' | 'reconnecting' | 'disconnected'
  themeMode: 'dark' | 'light'
  setThemeMode: (mode: 'dark' | 'light') => void
  reloadTasks: () => Promise<void>
  reloadChannels: () => Promise<void>
  reloadRecordings: () => Promise<void>
  reloadConfig: () => Promise<void>
  createTask: (payload: {
    channelId: number
    startTime: string
    endTime: string
    taskName: string | null
  }) => Promise<void>
  stopTask: (id: number) => Promise<void>
  deleteTask: (id: number) => Promise<void>
  addChannel: (payload: { name: string; url: string }) => Promise<void>
  updateChannel: (payload: ChannelConfig) => Promise<void>
  deleteChannel: (id: number) => Promise<void>
  updateConfig: (payload: { maxRecordingTasks: number }) => Promise<void>
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
