import { toast } from 'sonner'
import type {
  AppConfig,
  ChannelConfig,
  RecordingFileInfo,
  RecordingTaskDto,
  SystemStatus,
} from '../types'
import type {
  EpgChannel,
  BackendEpgProgram,
  BackendCurrentProgramInfo
} from '../features/epg/types/epg'

type FetchOptions = RequestInit & { silent?: boolean }

let lastToastAt = 0

const notifyError = (text: string, options?: FetchOptions) => {
  if (options?.silent) {
    return
  }
  const now = Date.now()
  if (now - lastToastAt < 2000) {
    return
  }
  lastToastAt = now
  toast.error(text)
}

const readErrorMessage = async (response: Response) => {
  let errorText = `请求失败: ${response.status}`
  try {
    const contentType = response.headers.get('content-type') ?? ''
    if (contentType.includes('application/json')) {
      const data = (await response.json()) as {
        message?: string
        detail?: string
        title?: string
        error?: string
      }
      const candidate =
        (typeof data?.message === 'string' && data.message.trim()) ||
        (typeof data?.detail === 'string' && data.detail.trim()) ||
        (typeof data?.title === 'string' && data.title.trim()) ||
        (typeof data?.error === 'string' && data.error.trim())
      if (candidate) {
        errorText = candidate
      }
    } else {
      const text = await response.text()
      if (text.trim()) {
        errorText = text
      }
    }
  } catch {
    return errorText
  }
  return errorText
}

const fetchJson = async <T>(url: string, options?: FetchOptions) => {
  let response: Response
  try {
    response = await fetch(url, options)
  } catch (err) {
    notifyError(`请求失败: ${err instanceof Error ? err.message : '网络错误'}`, options)
    throw err
  }
  if (!response.ok) {
    const errorText = await readErrorMessage(response)
    notifyError(errorText, options)
    throw new Error(errorText)
  }
  if (response.status === 204) {
    return undefined as T
  }
  const text = await response.text()
  if (!text.trim()) {
    return undefined as T
  }
  return JSON.parse(text) as T
}

export const getSystemStatus = () => fetchJson<SystemStatus>('/api/system/status', { silent: true })

export const getTasks = () => fetchJson<RecordingTaskDto[]>('/api/tasks', { silent: true })

export const createTask = (payload: {
  channelId: number
  startTime: string
  endTime: string
  taskName: string | null
}) =>
  fetchJson<RecordingTaskDto>('/api/tasks', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  })

export const stopTask = (id: number) =>
  fetchJson<{ message: string }>(`/api/tasks/${id}/stop`, { method: 'POST' })

export const deleteTask = (id: number) =>
  fetchJson<{ message: string }>(`/api/tasks/${id}`, { method: 'DELETE' })

export const getTaskMediaInfo = async (id: number) => {
  const data = await fetchJson<{ content?: string }>(`/api/tasks/${id}/mediainfo`)
  return data?.content ?? ''
}

export const getChannels = () => fetchJson<ChannelConfig[]>('/api/channels', { silent: true })

export const addChannel = (payload: { name: string; url: string }) =>
  fetchJson<ChannelConfig>('/api/channels', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  })

export const updateChannel = (id: number, payload: ChannelConfig) =>
  fetchJson<ChannelConfig>(`/api/channels/${id}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  })

export const removeChannel = (id: number) =>
  fetchJson<{ message: string }>(`/api/channels/${id}`, { method: 'DELETE' })

export const getRecordings = () => fetchJson<RecordingFileInfo[]>('/api/recordings', { silent: true })

export const getRecordingMediaInfo = (filePath: string) =>
  fetchJson<{ content?: string }>(`/api/recordings/mediainfo?filePath=${encodeURIComponent(filePath)}`).then(
    (data) => data?.content ?? '',
  )

export const getConfig = () => fetchJson<AppConfig>('/api/config', { silent: true })

export const updateConfig = (payload: { maxRecordingTasks: number; recordingTransport: AppConfig['recordingTransport'] }) =>
  fetchJson<AppConfig>('/api/config', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  })

// EPG API
export const getEpgChannels = () => fetchJson<EpgChannel[]>('/api/epg/channels', { silent: true })

export const getEpgChannelPrograms = (channelId: string) => 
  fetchJson<BackendEpgProgram[]>(`/api/epg/channels/${channelId}/programs`, { silent: true })

export const getEpgCurrentPrograms = () => fetchJson<BackendCurrentProgramInfo[]>(`/api/epg/current?t=${Date.now()}`, { silent: true })

export const getEpgCategories = () => fetchJson<string[]>('/api/epg/categories', { silent: true })

export const getEpgCategoryChannels = (category: string) => 
  fetchJson<EpgChannel[]>(`/api/epg/categories/${category}/channels`, { silent: true })

export const refreshEpgData = () => fetchJson<{ message: string }>('/api/epg/refresh', { method: 'POST' })
