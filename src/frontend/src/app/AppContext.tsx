import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { toast } from 'sonner'
import type { AppConfig, ChannelConfig, RecordingFileInfo, RecordingTaskDto, SystemStatus, RecordingStatus } from '../types'
import {
  addChannel,
  createTask,
  deleteTask,
  getChannels,
  getConfig,
  getRecordings,
  getRecordingMediaInfo,
  getSystemStatus,
  getTaskMediaInfo,
  getTasks,
  removeChannel,
  stopTask,
  updateChannel,
  updateConfig,
} from '../services/api'
import { connectWebSocket } from '../services/ws'
import { AppContext, type AppContextValue } from './context'
import { useTheme } from '@/components/theme-provider'

type RecordingStatusKey = 'Pending' | 'Recording' | 'Completed' | 'Failed' | 'Stopped'

const statusKeys: RecordingStatusKey[] = ['Pending', 'Recording', 'Completed', 'Failed', 'Stopped']

const normalizeStatus = (value: RecordingStatus): RecordingStatusKey => {
  if (typeof value === 'number') {
    return statusKeys[value] ?? 'Pending'
  }
  if (statusKeys.includes(value as RecordingStatusKey)) {
    return value as RecordingStatusKey
  }
  return 'Pending'
}

export const AppProvider = ({ children }: { children: React.ReactNode }) => {
  const [tasks, setTasks] = useState<RecordingTaskDto[]>([])
  const [channels, setChannels] = useState<ChannelConfig[]>([])
  const [recordings, setRecordings] = useState<RecordingFileInfo[]>([])
  const [systemStatus, setSystemStatus] = useState<SystemStatus | null>(null)
  const [appConfig, setAppConfig] = useState<AppConfig | null>(null)
  const [now, setNow] = useState(new Date())
  const [wsConnectionStatus, setWsConnectionStatus] = useState<'connecting' | 'connected' | 'reconnecting' | 'disconnected'>('connecting')
  const [browserOnline, setBrowserOnline] = useState(() => navigator.onLine)
  const [lastApiSuccessAt, setLastApiSuccessAt] = useState<number | null>(null)
  const [debouncedTotalBitrateKbps, setDebouncedTotalBitrateKbps] = useState(0)
  const bitrateDebounceTimerRef = useRef<number | undefined>(undefined)
  
  const { theme, setTheme } = useTheme()
  const themeMode = (theme === 'system' ? (window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light') : theme) as 'light' | 'dark'

  const setThemeMode = (mode: 'light' | 'dark') => {
      setTheme(mode)
  }

  const serverOffsetMsRef = useRef(0)
  const notifySuccess = useCallback((text: string) => {
    toast.success(text)
  }, [])

  const notifyError = useCallback((text: string) => {
    toast.error(text)
  }, [])

  // Theme effect handled by ThemeProvider

  const reloadTasks = useCallback(async () => {
    try {
      const data = await getTasks()
      setTasks(data)
    } catch {
      notifyError('加载任务失败')
    }
  }, [notifyError])

  const reloadChannels = useCallback(async () => {
    try {
      const data = await getChannels()
      setChannels(data)
    } catch {
      notifyError('加载频道失败')
    }
  }, [notifyError])

  const reloadRecordings = useCallback(async () => {
    try {
      const data = await getRecordings()
      setRecordings(data)
    } catch {
      notifyError('加载录制文件失败')
    }
  }, [notifyError])

  const reloadConfig = useCallback(async () => {
    try {
      const data = await getConfig()
      setAppConfig(data)
    } catch {
      notifyError('加载配置失败')
    }
  }, [notifyError])

  const refreshSystemStatus = useCallback(async () => {
    try {
      const status = await getSystemStatus()
      serverOffsetMsRef.current = new Date(status.systemTime).getTime() - Date.now()
      setSystemStatus(status)
      setNow(new Date(Date.now() + serverOffsetMsRef.current))
    } catch {
      notifyError('加载系统状态失败')
    }
  }, [notifyError])

  useEffect(() => {
    const timer = window.setTimeout(() => {
      refreshSystemStatus()
      reloadTasks()
      reloadChannels()
      reloadRecordings()
      reloadConfig()
    }, 0)
    return () => window.clearTimeout(timer)
  }, [refreshSystemStatus, reloadTasks, reloadChannels, reloadRecordings, reloadConfig])

  useEffect(() => {
    const timer = window.setInterval(() => {
      setNow(new Date(Date.now() + serverOffsetMsRef.current))
    }, 1000)
    return () => window.clearInterval(timer)
  }, [])

  useEffect(() => {
    const onOnline = () => setBrowserOnline(true)
    const onOffline = () => setBrowserOnline(false)
    window.addEventListener('online', onOnline)
    window.addEventListener('offline', onOffline)
    return () => {
      window.removeEventListener('online', onOnline)
      window.removeEventListener('offline', onOffline)
    }
  }, [])

  useEffect(() => {
    let cancelled = false

    const ping = async () => {
      try {
        const status = await getSystemStatus()
        if (cancelled) return
        setLastApiSuccessAt(Date.now())
        serverOffsetMsRef.current = new Date(status.systemTime).getTime() - Date.now()
        setSystemStatus(status)
      } catch {
        return
      }
    }

    ping()
    const timer = window.setInterval(ping, 15000)
    return () => {
      cancelled = true
      window.clearInterval(timer)
    }
  }, [])

  useEffect(() => {
    const disconnect = connectWebSocket({
      onTaskUpdate: (task) => {
        setTasks((prev) => {
          const index = prev.findIndex((item) => item.id === task.id)
          if (index >= 0) {
            const next = [...prev]
            next[index] = task
            return next
          }
          return [...prev, task]
        })
      },
      onSystemStatus: (status) => {
        serverOffsetMsRef.current = new Date(status.systemTime).getTime() - Date.now()
        setSystemStatus(status)
      },
      onConnectionStatus: setWsConnectionStatus,
    })
    return () => disconnect()
  }, [])

  const createTaskAction = useCallback(
    async (payload: { channelId: number; startTime: string; endTime: string; taskName: string | null }) => {
      try {
        await createTask(payload)
        notifySuccess('任务已创建')
        reloadTasks()
      } catch {
        return
      }
    },
    [notifySuccess, reloadTasks],
  )

  const stopTaskAction = useCallback(
    async (id: number) => {
      try {
        await stopTask(id)
        notifySuccess('已发送停止请求')
        reloadTasks()
      } catch {
        return
      }
    },
    [notifySuccess, reloadTasks],
  )

  const deleteTaskAction = useCallback(
    async (id: number) => {
      try {
        await deleteTask(id)
        setTasks((prev) => prev.filter((task) => task.id !== id))
        notifySuccess('已删除任务')
        reloadTasks()
      } catch {
        return
      }
    },
    [notifySuccess, reloadTasks],
  )

  const addChannelAction = useCallback(
    async (payload: { name: string; url: string }) => {
      try {
        await addChannel(payload)
        notifySuccess('已添加频道')
        reloadChannels()
      } catch {
        return
      }
    },
    [notifySuccess, reloadChannels],
  )

  const updateChannelAction = useCallback(
    async (payload: ChannelConfig) => {
      try {
        await updateChannel(payload.id, payload)
        notifySuccess('已保存')
        reloadChannels()
      } catch {
        return
      }
    },
    [notifySuccess, reloadChannels],
  )

  const deleteChannelAction = useCallback(
    async (id: number) => {
      try {
        await removeChannel(id)
        notifySuccess('已删除频道')
        reloadChannels()
      } catch {
        return
      }
    },
    [notifySuccess, reloadChannels],
  )

  const updateConfigAction = useCallback(async (payload: { maxRecordingTasks: number }) => {
    try {
      const data = await updateConfig(payload)
      setAppConfig(data)
      notifySuccess('保存成功')
    } catch {
      return
    }
  }, [notifySuccess])

  // 计算实时总码率
  const totalBitrateKbps = useMemo(() => {
    return tasks
      .filter(task => normalizeStatus(task.status) === 'Recording')
      .reduce((total, task) => {
        // 优先使用后端提供的实时码率
        if (task.currentBitrateKbps !== undefined && task.currentBitrateKbps > 0) {
          return total + task.currentBitrateKbps
        }
        
        // 如果没有实时码率，使用之前的平均码率计算作为备选
        const startedAt = task.startedAt ? new Date(task.startedAt).getTime() : Number.NaN
        if (!Number.isFinite(startedAt) || task.bytesWritten === 0) {
          return total
        }
        
        const durationSeconds = Math.max(1, (now.getTime() - startedAt) / 1000)
        const bitrateKbps = (task.bytesWritten * 8) / (durationSeconds * 1024)
        return total + bitrateKbps
      }, 0)
  }, [tasks, now])

  // 防抖处理实时码率，避免频繁变化
  useEffect(() => {
    if (bitrateDebounceTimerRef.current) {
      window.clearTimeout(bitrateDebounceTimerRef.current)
    }
    
    bitrateDebounceTimerRef.current = window.setTimeout(() => {
      setDebouncedTotalBitrateKbps(totalBitrateKbps)
    }, 300) // 300ms防抖
    
    return () => {
      if (bitrateDebounceTimerRef.current) {
        window.clearTimeout(bitrateDebounceTimerRef.current)
      }
    }
  }, [totalBitrateKbps])

  const value = useMemo<AppContextValue>(
    () => ({
      tasks,
      channels,
      recordings,
      systemStatus,
      appConfig,
      now,
      connectionStatus: (() => {
        if (!browserOnline) return 'disconnected'
        if (wsConnectionStatus === 'connected') return 'connected'
        const apiRecentlyOk = lastApiSuccessAt !== null && Date.now() - lastApiSuccessAt < 30000
        if (apiRecentlyOk) {
          return wsConnectionStatus === 'connecting' ? 'connecting' : 'reconnecting'
        }
        return wsConnectionStatus === 'connecting' ? 'connecting' : 'disconnected'
      })(),
      themeMode,
      setThemeMode,
      reloadTasks,
      reloadChannels,
      reloadRecordings,
      reloadConfig,
      createTask: createTaskAction,
      stopTask: stopTaskAction,
      deleteTask: deleteTaskAction,
      addChannel: addChannelAction,
      updateChannel: updateChannelAction,
      deleteChannel: deleteChannelAction,
      updateConfig: updateConfigAction,
      getTaskMediaInfo,
      getRecordingMediaInfo,
      totalBitrateKbps: debouncedTotalBitrateKbps,
    }),
    [
      tasks,
      channels,
      recordings,
      systemStatus,
      appConfig,
      now,
      wsConnectionStatus,
      browserOnline,
      lastApiSuccessAt,
      themeMode,
      reloadTasks,
      reloadChannels,
      reloadRecordings,
      reloadConfig,
      createTaskAction,
      stopTaskAction,
      deleteTaskAction,
      addChannelAction,
      updateChannelAction,
      deleteChannelAction,
      updateConfigAction,
      debouncedTotalBitrateKbps,
    ],
  )

  return <AppContext.Provider value={value}>{children}</AppContext.Provider>
}
