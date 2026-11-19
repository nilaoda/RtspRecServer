import { toast } from 'sonner'
import type { RecordingTaskDto, SystemStatus, TaskErrorLogMessage, WebSocketMessage } from '../types'

type WebSocketHandlers = {
  onTaskUpdate: (task: RecordingTaskDto) => void
  onSystemStatus: (status: SystemStatus) => void
  onTaskErrorLog?: (message: TaskErrorLogMessage) => void
  onConnectionStatus?: (status: 'connecting' | 'connected' | 'reconnecting' | 'disconnected') => void
}

export const connectWebSocket = ({ onTaskUpdate, onSystemStatus, onTaskErrorLog, onConnectionStatus }: WebSocketHandlers) => {
  let cancelled = false
  let socket: WebSocket | null = null
  let reconnectTimer: number | undefined
  let reconnectAttempts = 0
  let notified = false

  const connect = () => {
    if (cancelled) return
    onConnectionStatus?.(reconnectAttempts === 0 ? 'connecting' : 'reconnecting')
    const scheme = window.location.protocol === 'https:' ? 'wss' : 'ws'
    const connection = new WebSocket(`${scheme}://${window.location.host}/ws`)
    socket = connection

    connection.onopen = () => {
      if (cancelled) return
      reconnectAttempts = 0
      notified = false
      onConnectionStatus?.('connected')
    }

    connection.onmessage = (event) => {
      if (cancelled) return
      try {
        const message = JSON.parse(event.data) as WebSocketMessage
        if (message.type === 'taskUpdated') {
          onTaskUpdate(message.task)
          return
        }
        if (message.type === 'systemStatus') {
          onSystemStatus(message.status)
          return
        }
        if (message.type === 'taskErrorLog') {
          onTaskErrorLog?.(message)
        }
      } catch {
        return
      }
    }

    connection.onclose = () => {
      socket = null
      if (!cancelled) {
        reconnectAttempts += 1
        onConnectionStatus?.(reconnectAttempts >= 10 ? 'disconnected' : 'reconnecting')
        if (reconnectAttempts >= 10 && !notified) {
          toast.error('连接服务端失败，请检查服务是否可用')
          notified = true
        }
        reconnectTimer = window.setTimeout(connect, 2000)
      }
    }

    connection.onerror = () => {
      if (cancelled) return
      onConnectionStatus?.(reconnectAttempts >= 10 ? 'disconnected' : 'reconnecting')
      connection.close()
    }
  }

  connect()

  return () => {
    cancelled = true
    window.clearTimeout(reconnectTimer)
    socket?.close()
  }
}
