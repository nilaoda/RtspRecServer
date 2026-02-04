import { toast } from 'sonner'
import type { RecordingTaskDto, SystemStatus, WebSocketMessage } from '../types'

type WebSocketHandlers = {
  onTaskUpdate: (task: RecordingTaskDto) => void
  onSystemStatus: (status: SystemStatus) => void
  onConnectionStatus?: (status: 'connecting' | 'connected' | 'reconnecting' | 'disconnected') => void
}

export const connectWebSocket = ({ onTaskUpdate, onSystemStatus, onConnectionStatus }: WebSocketHandlers) => {
  let cancelled = false
  let socket: WebSocket | null = null
  let reconnectAttempts = 0
  let notified = false

  const connect = () => {
    onConnectionStatus?.(reconnectAttempts === 0 ? 'connecting' : 'reconnecting')
    const scheme = window.location.protocol === 'https:' ? 'wss' : 'ws'
    socket = new WebSocket(`${scheme}://${window.location.host}/ws`)

    socket.onopen = () => {
      reconnectAttempts = 0
      notified = false
      onConnectionStatus?.('connected')
    }

    socket.onmessage = (event) => {
      try {
        const message = JSON.parse(event.data) as WebSocketMessage
        if (message.type === 'taskUpdated') {
          onTaskUpdate(message.task)
          return
        }
        if (message.type === 'systemStatus') {
          onSystemStatus(message.status)
        }
      } catch {
        return
      }
    }

    socket.onclose = () => {
      if (!cancelled) {
        reconnectAttempts += 1
        onConnectionStatus?.(reconnectAttempts >= 10 ? 'disconnected' : 'reconnecting')
        if (reconnectAttempts >= 10 && !notified) {
          toast.error('连接服务端失败，请检查服务是否可用')
          notified = true
        }
        window.setTimeout(connect, 2000)
      }
    }

    socket.onerror = () => {
      onConnectionStatus?.(reconnectAttempts >= 10 ? 'disconnected' : 'reconnecting')
      socket?.close()
    }
  }

  connect()

  return () => {
    cancelled = true
    onConnectionStatus?.('disconnected')
    socket?.close()
  }
}
