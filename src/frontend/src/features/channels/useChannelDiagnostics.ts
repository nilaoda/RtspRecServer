import { useCallback, useEffect, useRef, useState } from 'react'
import { getChannelDiagnostics } from '../../services/api'
import type { ChannelDiagnosticsSnapshot } from '../../types'

export function useChannelDiagnostics() {
  const [snapshot, setSnapshot] = useState<ChannelDiagnosticsSnapshot | null>(null)
  const [synced, setSynced] = useState(false)
  const controller = useRef<AbortController | null>(null)
  const requestId = useRef(0)
  const lastSnapshot = useRef<ChannelDiagnosticsSnapshot | null>(null)

  const refresh = useCallback(async () => {
    const signal = controller.current?.signal
    if (!signal) return

    const id = ++requestId.current
    const request = new AbortController()
    const abortRequest = () => request.abort()
    signal.addEventListener('abort', abortRequest, { once: true })
    const timeout = window.setTimeout(abortRequest, 5000)
    try {
      const next = await getChannelDiagnostics(lastSnapshot.current?.probeRevision ?? '', request.signal)
      if (!signal.aborted && id === requestId.current) {
        const view = { ...next, probes: next.probes ?? lastSnapshot.current?.probes }
        lastSnapshot.current = view
        setSnapshot(view)
        setSynced(true)
      }
    } catch {
      if (!signal.aborted && id === requestId.current) {
        setSynced(false)
      }
    } finally {
      window.clearTimeout(timeout)
      signal.removeEventListener('abort', abortRequest)
    }
  }, [])

  useEffect(() => {
    const active = new AbortController()
    controller.current = active
    let timer: number | undefined
    const poll = async () => {
      await refresh()
      if (!active.signal.aborted) {
        timer = window.setTimeout(poll, 1000)
      }
    }
    void poll()
    return () => {
      controller.current = null
      active.abort()
      window.clearTimeout(timer)
    }
  }, [refresh])

  return { snapshot, synced, refresh }
}
