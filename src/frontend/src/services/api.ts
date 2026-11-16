import { toast } from 'sonner'
import type {
  AppConfig,
  BulkTaskResult,
  ChannelBulkResult,
  ChannelConfig,
  ChannelProbeResult,
  LogFileContent,
  LogFileInfo,
  RecordingFileInfo,
  RecordingFileDeleteResult,
  RecordingTaskDto,
  RecordingTimer,
  SystemStatus,
  TaskErrorLogSnapshot,
  TimerUpsertPayload,
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

export const getTaskErrorLog = () => fetchJson<TaskErrorLogSnapshot>('/api/task-errors', { silent: true })

export const clearTaskErrorLog = () =>
  fetchJson<{ message: string }>('/api/task-errors', { method: 'DELETE' })

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

export const stopTasks = (ids: number[]) =>
  fetchJson<BulkTaskResult>('/api/tasks/bulk-stop', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ taskIds: ids }),
  })

export const updateTaskEndTime = (id: number, endTime: string) =>
  fetchJson<RecordingTaskDto>(`/api/tasks/${id}/end-time`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ endTime }),
  })

export const deleteTask = (id: number) =>
  fetchJson<{ message: string }>(`/api/tasks/${id}`, { method: 'DELETE' })

export const deleteTasks = (ids: number[]) =>
  fetchJson<BulkTaskResult>('/api/tasks/bulk-delete', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ taskIds: ids }),
  })

export const getTaskMediaInfo = async (id: number) => {
  const data = await fetchJson<{ content?: string }>(`/api/tasks/${id}/mediainfo`)
  return data?.content ?? ''
}

export const getTaskLog = (id: number) =>
  fetchJson<LogFileContent>(`/api/tasks/${id}/log`, { silent: true })

// ---- 定时录制规则 ----

export const getTimers = () => fetchJson<RecordingTimer[]>('/api/timers', { silent: true })

export const createTimer = (payload: TimerUpsertPayload) =>
  fetchJson<RecordingTimer>('/api/timers', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  })

export const updateTimer = (id: number, payload: TimerUpsertPayload) =>
  fetchJson<RecordingTimer>(`/api/timers/${id}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  })

export const deleteTimer = (id: number) =>
  fetchJson<{ message: string }>(`/api/timers/${id}`, { method: 'DELETE' })

export const getChannels = () => fetchJson<ChannelConfig[]>('/api/channels', { silent: true })

export const addChannel = (payload: { name: string; url: string }) =>
  fetchJson<ChannelConfig>('/api/channels', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  })

export const getChannelProbes = () => fetchJson<ChannelProbeResult[]>('/api/channels/probes', { silent: true })

export const getChannelProfiles = () => fetchJson<ChannelProbeResult[]>('/api/channels/profiles', { silent: true })

export const probeChannels = (
  channelIds: number[],
  options?: { concurrency?: number; retries?: number },
) =>
  fetchJson<ChannelProbeResult[]>('/api/channels/probe', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      channelIds,
      concurrency: options?.concurrency,
      retries: options?.retries,
    }),
  })

export const importChannels = (channels: { name: string; url: string }[]) =>
  fetchJson<{ addedCount: number; channels: ChannelConfig[] }>('/api/channels/import', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ channels }),
    silent: true,
  })

export const updateChannel = (id: number, payload: ChannelConfig) =>
  fetchJson<ChannelConfig>(`/api/channels/${id}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  })

export const removeChannel = (id: number) =>
  fetchJson<{ message: string }>(`/api/channels/${id}`, { method: 'DELETE' })

export const deleteChannels = (channelIds: number[]) =>
  fetchJson<ChannelBulkResult>('/api/channels/bulk-delete', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ channelIds }),
  })

export const getRecordings = () => fetchJson<RecordingFileInfo[]>('/api/recordings', { silent: true })

export const deleteRecordings = (fileNames: string[]) =>
  fetchJson<RecordingFileDeleteResult>('/api/recordings/bulk-delete', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ fileNames }),
  })

export const getRecordingMediaInfo = (filePath: string) =>
  fetchJson<{ content?: string }>(`/api/recordings/mediainfo?filePath=${encodeURIComponent(filePath)}`)
    .then((data) => data?.content ?? '')

export const getLogs = () => fetchJson<LogFileInfo[]>('/api/logs', { silent: true })

export const getLogContent = (fileName: string) =>
  fetchJson<LogFileContent>(`/api/logs/${encodeURIComponent(fileName)}`, { silent: true })

export const getConfig = () => fetchJson<AppConfig>('/api/config', { silent: true })

export type UpdateConfigPayload = {
  port?: number
  recordPath?: string
  host?: string[]
  useAuth?: boolean
  username?: string
  password?: string | null
  maxRecordingTasks?: number
  recordingTransport?: AppConfig['recordingTransport']
  writeRecordingLog?: boolean
  taskErrorLogMaxEntries?: number
  epg?: {
    url?: string
    updateTime?: string
    cacheDurationHours?: number
  }
}

export const updateConfig = (payload: UpdateConfigPayload) =>
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
