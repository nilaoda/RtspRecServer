import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { message } from 'antd'
import type { AppConfig, ChannelConfig, RecordingFileInfo, RecordingTaskDto, SystemStatus } from '../types'
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
import { getMessageApi } from './antdApp'
import { AppContext, type AppContextValue } from './context'

export const AppProvider = ({ children }: { children: React.ReactNode }) => {
  const [tasks, setTasks] = useState<RecordingTaskDto[]>([])
  const [channels, setChannels] = useState<ChannelConfig[]>([])
  const [recordings, setRecordings] = useState<RecordingFileInfo[]>([])
  const [systemStatus, setSystemStatus] = useState<SystemStatus | null>(null)
  const [appConfig, setAppConfig] = useState<AppConfig | null>(null)
  const [now, setNow] = useState(new Date())
  const [themeMode, setThemeMode] = useState<'dark' | 'light'>(() => {
    const stored = window.localStorage.getItem('theme')
    if (stored === 'light' || stored === 'dark') {
      return stored
    }
    return window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark'
  })
  const serverOffsetMsRef = useRef(0)
  const notifySuccess = useCallback((text: string) => {
    const api = getMessageApi()
    if (api) {
      api.success(text)
      return
    }
    message.success(text)
  }, [])

  const notifyError = useCallback((text: string) => {
    const api = getMessageApi()
    if (api) {
      api.error(text)
      return
    }
    message.error(text)
  }, [])

  useEffect(() => {
    const media = window.matchMedia('(prefers-color-scheme: dark)')
    const applyTheme = (matches: boolean) => {
      setThemeMode(matches ? 'dark' : 'light')
    }
    applyTheme(media.matches)
    const handler = (event: MediaQueryListEvent) => applyTheme(event.matches)
    if (media.addEventListener) {
      media.addEventListener('change', handler)
    } else {
      media.addListener(handler)
    }
    return () => {
      if (media.removeEventListener) {
        media.removeEventListener('change', handler)
      } else {
        media.removeListener(handler)
      }
    }
  }, [notifyError])

  useEffect(() => {
    document.documentElement.dataset.theme = themeMode
    window.localStorage.setItem('theme', themeMode)
  }, [themeMode])

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

  const value = useMemo<AppContextValue>(
    () => ({
      tasks,
      channels,
      recordings,
      systemStatus,
      appConfig,
      now,
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
    }),
    [
      tasks,
      channels,
      recordings,
      systemStatus,
      appConfig,
      now,
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
    ],
  )

  return <AppContext.Provider value={value}>{children}</AppContext.Provider>
}
