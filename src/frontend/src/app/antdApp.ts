import { App } from 'antd'
import type { MessageInstance } from 'antd/es/message/interface'
import { useEffect } from 'react'

let messageApi: MessageInstance | null = null

export const getMessageApi = () => messageApi

export const AntdMessageBridge = () => {
  const { message } = App.useApp()

  useEffect(() => {
    messageApi = message
    return () => {
      if (messageApi === message) {
        messageApi = null
      }
    }
  }, [message])

  return null
}
