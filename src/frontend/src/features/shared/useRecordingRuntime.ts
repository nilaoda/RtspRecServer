import { useEffect, useState } from 'react'
import { getTaskRuntime } from '../../services/api'
import type { RecordingTaskRuntimeSnapshot } from '../../types'

export const useRecordingRuntime = (enabled: boolean) => {
  const [runtime, setRuntime] = useState<RecordingTaskRuntimeSnapshot | null>(null)

  useEffect(() => {
    if (!enabled) return

    const controller = new AbortController()
    let timer: number | undefined
    const refresh = async () => {
      try {
        if (!document.hidden) {
          const snapshot = await getTaskRuntime(controller.signal)
          if (!controller.signal.aborted) setRuntime(snapshot)
        }
      } catch {
        if (!controller.signal.aborted) setRuntime(null)
      } finally {
        if (!controller.signal.aborted) timer = window.setTimeout(refresh, 2000)
      }
    }
    void refresh()
    return () => {
      controller.abort()
      window.clearTimeout(timer)
      setRuntime(null)
    }
  }, [enabled])

  return enabled ? runtime : null
}
