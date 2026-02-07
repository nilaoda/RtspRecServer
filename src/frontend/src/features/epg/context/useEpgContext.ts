import { createContext, useContext } from 'react'
import type { EpgChannel, EpgProgram, CurrentProgramInfo } from '../types/epg'

export interface EpgContextType {
  channels: EpgChannel[]
  currentPrograms: CurrentProgramInfo[]
  categories: string[]
  loading: boolean
  error: string | null
  refresh: () => Promise<void>
  loadAllData: () => Promise<void>
  getChannelPrograms: (channelId: string) => Promise<EpgProgram[]>
  getCategoryChannels: (category: string) => Promise<EpgChannel[]>
  now: number // 提供全局统一的系统时间戳，用于 UI 自动刷新
}

export const EpgContext = createContext<EpgContextType | undefined>(undefined)

export const useEpgContext = () => {
  const context = useContext(EpgContext)
  if (context === undefined) {
    throw new Error('useEpgContext must be used within an EpgProvider')
  }
  return context
}
