import { toast } from 'sonner'
import type { RecordingTaskDto, SystemStatus, WebSocketMessage } from '../types'

type WebSocketHandlers = {
  onTaskUpdate: (task: RecordingTaskDto) => void
  onSystemStatus: (status: SystemStatus) => void
}

export const connectWebSocket = ({ onTaskUpdate, onSystemStatus }: WebSocketHandlers) => {
  let cancelled = false
  let socket: WebSocket | null = null
  let reconnectAttempts = 0
  let notified = false

  const connect = () => {
    const scheme = window.location.protocol === 'https:' ? 'wss' : 'ws'
    socket = new WebSocket(`${scheme}://${window.location.host}/ws`)

    socket.onopen = () => {
      reconnectAttempts = 0
      notified = false
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
        if (reconnectAttempts >= 10 && !notified) {
          toast.error('连接服务端失败，请检查服务是否可用')
          notified = true
        }
        window.setTimeout(connect, 2000)
      }
    }

    socket.onerror = () => {
      socket?.close()
    }
  }

  connect()

  return () => {
    cancelled = true
    socket?.close()
  }
}
