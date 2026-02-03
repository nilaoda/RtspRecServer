export interface EpgChannel {
  id: string
  name: string
  iconUrl?: string
  categories: string[]
  language: string
  country: string
  description?: string
}

export interface EpgProgram {
  id: string
  title: string
  description?: string
  startTime: Date
  endTime: Date
  category: string
}

export interface BackendEpgProgram extends Omit<EpgProgram, 'startTime' | 'endTime'> {
  startTime: string
  endTime: string
}

export interface CurrentProgramInfo {
  channelId: string
  channelName: string
  program: EpgProgram
  startTime: Date
  endTime: Date
}

export interface BackendCurrentProgramInfo {
  channel: {
    id: string
    name: string
  }
  currentProgram: BackendEpgProgram
}