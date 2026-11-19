import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { toast } from 'sonner'
import type { AppConfig, ChannelConfig, ChannelProbeResult, RecordingFileDeleteResult, RecordingFileInfo, RecordingTaskDto, RecordingTimer, SystemStatus, RecordingStatus, TaskErrorLogEntry, TaskErrorLogMessage, TimerUpsertPayload } from '../types'
import {
  addChannel,
  clearTaskErrorLog as clearTaskErrorLogApi,
  createTask,
  createTimer as createTimerApi,
  deleteTask,
  deleteTasks as deleteTasksApi,
  deleteRecordings as deleteRecordingsApi,
  deleteTimer as deleteTimerApi,
  getChannels,
  getChannelProfiles,
  getConfig,
  getRecordings,
  getRecordingMediaInfo,
  getSystemStatus,
  getTaskErrorLog,
  getTaskMediaInfo,
  getTasks,
  getTimers,
  removeChannel,
  stopTask,
  stopTasks as stopTasksApi,
  updateChannel,
  updateConfig,
  updateTimer as updateTimerApi,
  updateTaskEndTime as updateTaskEndTimeApi,
} from '../services/api'
import type { UpdateConfigPayload } from '../services/api'
import { connectWebSocket } from '../services/ws'
import { AppContext, type AppContextValue } from './context'
import { useTheme } from '@/components/theme-context'

type RecordingStatusKey = 'Pending' | 'Recording' | 'Completed' | 'Failed' | 'Stopped'

const statusKeys: RecordingStatusKey[] = ['Pending', 'Recording', 'Completed', 'Failed', 'Stopped']

const CLIENT_ERROR_LOG_LIMIT = 50_000

const mergeTaskErrorEntries = (previous: TaskErrorLogEntry[], incoming: TaskErrorLogEntry[]) => {
  const byId = new Map(previous.map((entry) => [entry.id, entry]))
  for (const entry of incoming) {
    const existing = byId.get(entry.id)
    if (!existing || entry.revision >= existing.revision) {
      byId.set(entry.id, entry)
    }
  }
  return Array.from(byId.values()).sort((left, right) => left.id - right.id).slice(-CLIENT_ERROR_LOG_LIMIT)
}

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
  const taskUpdatesDuringReload = useRef<Map<number, RecordingTaskDto> | null>(null)
  const [channels, setChannels] = useState<ChannelConfig[]>([])
  const [channelProfiles, setChannelProfiles] = useState<Record<number, ChannelProbeResult>>({})
  const [recordings, setRecordings] = useState<RecordingFileInfo[]>([])
  const [timers, setTimers] = useState<RecordingTimer[]>([])
  const [systemStatus, setSystemStatus] = useState<SystemStatus | null>(null)
  const [appConfig, setAppConfig] = useState<AppConfig | null>(null)
  const [taskErrorLog, setTaskErrorLog] = useState<TaskErrorLogEntry[]>([])
  const taskErrorLogGenerationRef = useRef(0)
  const [now, setNow] = useState(new Date())
  const [wsConnectionStatus, setWsConnectionStatus] = useState<'connecting' | 'connected' | 'reconnecting' | 'disconnected'>('connecting')
  const [browserOnline, setBrowserOnline] = useState(() => navigator.onLine)
  const [lastApiSuccessAt, setLastApiSuccessAt] = useState<number | null>(null)
  const [debouncedTotalBitrateKbps, setDebouncedTotalBitrateKbps] = useState(0)
  const bitrateDebounceTimerRef = useRef<number | undefined>(undefined)
  
  const { theme, setTheme } = useTheme()
  const themeMode = (theme === 'system' ? (window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light') : theme) as 'light' | 'dark'

  const setThemeMode = useCallback((mode: 'light' | 'dark') => {
      setTheme(mode)
  }, [setTheme])

  const serverOffsetMsRef = useRef(0)
  const notifySuccess = useCallback((text: string) => {
    toast.success(text)
  }, [])

  const notifyError = useCallback((text: string) => {
    toast.error(text)
  }, [])

  // Theme effect handled by ThemeProvider

  const reloadTasks = useCallback(async () => {
    const updates = new Map<number, RecordingTaskDto>()
    taskUpdatesDuringReload.current = updates
    try {
      const data = await getTasks()
      if (taskUpdatesDuringReload.current === updates) {
        const byId = new Map(data.map((task) => [task.id, task]))
        for (const [id, task] of updates) {
          byId.set(id, task)
        }
        setTasks(Array.from(byId.values()))
      }
    } catch {
      if (taskUpdatesDuringReload.current === updates) {
        notifyError('加载任务失败')
      }
    } finally {
      if (taskUpdatesDuringReload.current === updates) {
        taskUpdatesDuringReload.current = null
      }
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

  const reloadChannelProfiles = useCallback(async () => {
    try {
      const data = await getChannelProfiles()
      setChannelProfiles(Object.fromEntries(data.map((result) => [result.channelId, result])))
    } catch {
      // 检测档案加载失败不阻塞主流程
    }
  }, [])

  const reloadRecordings = useCallback(async () => {
    try {
      const data = await getRecordings()
      setRecordings(data)
    } catch {
      notifyError('加载录制文件失败')
    }
  }, [notifyError])

  const reloadTimers = useCallback(async () => {
    try {
      const data = await getTimers()
      setTimers(data)
    } catch {
      notifyError('加载定时规则失败')
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

  const reloadTaskErrorLog = useCallback(async () => {
    try {
      const data = await getTaskErrorLog()
      const previousGeneration = taskErrorLogGenerationRef.current
      taskErrorLogGenerationRef.current = data.generation
      setTaskErrorLog((previous) => {
        // 同代次下合并快照，避免拉取期间到达的增量被覆盖；代次变化（清空）则整体替换
        if (data.generation !== previousGeneration) {
          return data.entries.slice(-CLIENT_ERROR_LOG_LIMIT)
        }
        return mergeTaskErrorEntries(previous, data.entries)
      })
    } catch {
      // 错误日志加载失败不打断主流程
    }
  }, [])

  const applyTaskErrorLogMessage = useCallback((message: TaskErrorLogMessage) => {
    if (message.reset) {
      taskErrorLogGenerationRef.current = message.generation
      setTaskErrorLog(message.entries.slice(-50_000))
      return
    }

    if (message.generation !== taskErrorLogGenerationRef.current) {
      // 代次变化说明中间可能错过清空，重新拉全量快照
      void reloadTaskErrorLog()
      return
    }

    if (message.entries.length === 0) {
      return
    }

    setTaskErrorLog((previous) => mergeTaskErrorEntries(previous, message.entries))
  }, [reloadTaskErrorLog])

  const clearTaskErrorLogAction = useCallback(async () => {
    try {
      await clearTaskErrorLogApi()
      setTaskErrorLog([])
    } catch {
      // 错误提示由 api 层统一处理
    }
  }, [])

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
      reloadChannelProfiles()
      reloadRecordings()
      reloadTimers()
      reloadConfig()
      reloadTaskErrorLog()
    }, 0)
    return () => window.clearTimeout(timer)
  }, [refreshSystemStatus, reloadTasks, reloadChannels, reloadChannelProfiles, reloadRecordings, reloadTimers, reloadConfig, reloadTaskErrorLog])

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
    const sync = () => {
      refreshSystemStatus()
      reloadTasks()
      reloadChannels()
      reloadChannelProfiles()
      reloadRecordings()
      reloadTimers()
      reloadConfig()
    }
    const onVisible = () => {
      if (document.visibilityState === 'visible') {
        sync()
      }
    }
    document.addEventListener('visibilitychange', onVisible)
    window.addEventListener('focus', sync)
    return () => {
      document.removeEventListener('visibilitychange', onVisible)
      window.removeEventListener('focus', sync)
    }
  }, [refreshSystemStatus, reloadTasks, reloadChannels, reloadChannelProfiles, reloadRecordings, reloadTimers, reloadConfig])

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
        taskUpdatesDuringReload.current?.set(task.id, task)
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
      onTaskErrorLog: applyTaskErrorLogMessage,
      onConnectionStatus: setWsConnectionStatus,
    })
    return () => disconnect()
  }, [applyTaskErrorLogMessage])

  useEffect(() => {
    if (wsConnectionStatus === 'connected') {
      // 补齐连接建立前和断线期间错过的更新。
      void reloadTasks()
      void reloadTaskErrorLog()
    }
  }, [wsConnectionStatus, reloadTasks, reloadTaskErrorLog])

  const createTaskAction = useCallback(
    async (payload: { channelId: number; startTime: string; endTime: string; taskName: string | null }) => {
      try {
        await createTask(payload)
        notifySuccess('任务已创建')
        void reloadTasks()
        return true
      } catch {
        return false
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

  const stopTasksAction = useCallback(
    async (ids: number[]) => {
      if (ids.length === 0) return

      try {
        const result = await stopTasksApi(ids)
        if (result.failed === 0) {
          notifySuccess(`已发送 ${result.succeeded} 个停止请求`)
        } else if (result.succeeded > 0) {
          notifyError(`已发送 ${result.succeeded} 个停止请求，${result.failed} 个失败`)
        } else {
          notifyError('批量停止失败')
        }
      } catch {
        notifyError('批量停止失败')
      } finally {
        reloadTasks()
      }
    },
    [notifyError, notifySuccess, reloadTasks],
  )

  const updateTaskEndTimeAction = useCallback(
    async (id: number, endTime: string) => {
      try {
        const updated = await updateTaskEndTimeApi(id, endTime)
        setTasks((prev) => prev.map((task) => (task.id === id ? { ...task, endTime: updated.endTime } : task)))
        notifySuccess('结束时间已更新')
        return true
      } catch {
        return false
      }
    },
    [notifySuccess],
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

  const deleteTasksAction = useCallback(
    async (ids: number[]) => {
      if (ids.length === 0) return

      try {
        const result = await deleteTasksApi(ids)
        if (result.failed === 0) {
          notifySuccess(`已删除 ${result.succeeded} 条任务记录`)
        } else if (result.succeeded > 0) {
          notifyError(`已删除 ${result.succeeded} 条记录，${result.failed} 条失败`)
        } else {
          notifyError('批量删除失败')
        }
      } catch {
        notifyError('批量删除失败')
      } finally {
        reloadTasks()
      }
    },
    [notifyError, notifySuccess, reloadTasks],
  )

  const deleteRecordingsAction = useCallback(
    async (fileNames: string[]): Promise<RecordingFileDeleteResult | null> => {
      if (fileNames.length === 0) return null

      try {
        const result = await deleteRecordingsApi(fileNames)
        if (result.failed === 0) {
          notifySuccess(`已删除 ${result.succeeded} 个录像文件`)
        } else if (result.succeeded > 0) {
          notifyError(`已删除 ${result.succeeded} 个，${result.failed} 个失败`)
        } else {
          notifyError(result.failedItems[0]?.reason ? `删除失败：${result.failedItems[0].reason}` : '删除失败')
        }
        return result
      } catch {
        return null
      } finally {
        reloadRecordings()
      }
    },
    [notifyError, notifySuccess, reloadRecordings],
  )

  const addChannelAction = useCallback(
    async (payload: { name: string; url: string; group?: string | null }) => {
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
        reloadChannelProfiles()
      } catch {
        return
      }
    },
    [notifySuccess, reloadChannels, reloadChannelProfiles],
  )

  const deleteChannelAction = useCallback(
    async (id: number) => {
      try {
        await removeChannel(id)
        notifySuccess('已删除频道')
        reloadChannels()
        reloadChannelProfiles()
      } catch {
        return
      }
    },
    [notifySuccess, reloadChannels, reloadChannelProfiles],
  )

  const createTimerAction = useCallback(
    async (payload: TimerUpsertPayload) => {
      try {
        await createTimerApi(payload)
        notifySuccess('定时规则已创建')
        reloadTimers()
      } catch {
        return
      }
    },
    [notifySuccess, reloadTimers],
  )

  const updateTimerAction = useCallback(
    async (id: number, payload: TimerUpsertPayload) => {
      try {
        await updateTimerApi(id, payload)
        notifySuccess('定时规则已保存')
        reloadTimers()
        // 规则更新/删除会级联清理预生成的待录任务，任务列表需要同步
        reloadTasks()
      } catch {
        return
      }
    },
    [notifySuccess, reloadTimers, reloadTasks],
  )

  const deleteTimerAction = useCallback(
    async (id: number) => {
      try {
        await deleteTimerApi(id)
        notifySuccess('已删除定时规则')
        reloadTimers()
        reloadTasks()
      } catch {
        return
      }
    },
    [notifySuccess, reloadTimers, reloadTasks],
  )

  const updateConfigAction = useCallback(async (payload: UpdateConfigPayload) => {
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
        const bitrateKbps = (task.bytesWritten * 8) / (durationSeconds * 1000)
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
      channelProfiles,
      recordings,
      timers,
      systemStatus,
      appConfig,
      taskErrorLog,
      now,
      connectionStatus: (() => {
        if (!browserOnline) return 'disconnected'
        if (wsConnectionStatus === 'connected') return 'connected'
        const apiRecentlyOk = lastApiSuccessAt !== null && now.getTime() - lastApiSuccessAt < 30000
        if (apiRecentlyOk) {
          return wsConnectionStatus === 'connecting' ? 'connecting' : 'reconnecting'
        }
        return wsConnectionStatus === 'connecting' ? 'connecting' : 'disconnected'
      })(),
      themeMode,
      setThemeMode,
      reloadTasks,
      reloadChannels,
      reloadChannelProfiles,
      reloadRecordings,
      reloadTimers,
      reloadConfig,
      createTask: createTaskAction,
      stopTask: stopTaskAction,
      stopTasks: stopTasksAction,
      updateTaskEndTime: updateTaskEndTimeAction,
      deleteTask: deleteTaskAction,
      deleteTasks: deleteTasksAction,
      deleteRecordings: deleteRecordingsAction,
      clearTaskErrorLog: clearTaskErrorLogAction,
      addChannel: addChannelAction,
      updateChannel: updateChannelAction,
      deleteChannel: deleteChannelAction,
      createTimer: createTimerAction,
      updateTimer: updateTimerAction,
      deleteTimer: deleteTimerAction,
      updateConfig: updateConfigAction,
      getTaskMediaInfo,
      getRecordingMediaInfo,
      totalBitrateKbps: debouncedTotalBitrateKbps,
    }),
    [
      tasks,
      channels,
      channelProfiles,
      recordings,
      timers,
      systemStatus,
      appConfig,
      taskErrorLog,
      now,
      wsConnectionStatus,
      browserOnline,
      lastApiSuccessAt,
      themeMode,
      setThemeMode,
      reloadTasks,
      reloadChannels,
      reloadChannelProfiles,
      reloadRecordings,
      reloadTimers,
      reloadConfig,
      createTaskAction,
      stopTaskAction,
      stopTasksAction,
      updateTaskEndTimeAction,
      deleteTaskAction,
      deleteTasksAction,
      deleteRecordingsAction,
      clearTaskErrorLogAction,
      addChannelAction,
      updateChannelAction,
      deleteChannelAction,
      createTimerAction,
      updateTimerAction,
      deleteTimerAction,
      updateConfigAction,
      debouncedTotalBitrateKbps,
    ],
  )

  return <AppContext.Provider value={value}>{children}</AppContext.Provider>
}
