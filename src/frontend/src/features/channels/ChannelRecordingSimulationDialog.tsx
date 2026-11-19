import { useId, useState } from 'react'
import { ChannelProtocolBadge } from '@/components/channel-protocol-badge'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Progress } from '@/components/ui/progress'
import { startChannelRecordingSimulation, stopChannelRecordingSimulation } from '../../services/api'
import type { ChannelConfig, ChannelRecordingSimulationUpdate } from '../../types'
import { formatBytes } from '../shared/format'
import { TsCheckDetails } from '../tasks/TsCheckDialog'

const statusLabels: Record<ChannelRecordingSimulationUpdate['status'], string> = {
  running: '模拟录制中',
  stopping: '正在停止模拟',
  stopped: '已停止',
  completed: '模拟录制完成',
  failed: '模拟录制失败',
}

export default function ChannelRecordingSimulationDialog({ channel, update, hasChanges, synced, onRefresh, onClose }: {
  channel: ChannelConfig
  update?: ChannelRecordingSimulationUpdate
  hasChanges: boolean
  synced: boolean
  onRefresh: () => Promise<void>
  onClose: () => void
}) {
  const durationId = useId()
  const [duration, setDuration] = useState(60)
  const [pending, setPending] = useState(false)
  const active = update?.status === 'running' || update?.status === 'stopping'

  const changeSimulation = async (operation: () => Promise<void>) => {
    setPending(true)
    try {
      await operation()
    } catch {
      // API 错误由统一提示处理。冲突时也需要重新读取后端状态。
    } finally {
      await onRefresh()
      setPending(false)
    }
  }

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="flex max-h-[calc(100dvh-2rem)] max-w-[calc(100vw-2rem)] flex-col overflow-hidden sm:max-w-[520px]">
        <DialogHeader className="shrink-0 pr-5">
          <DialogTitle>模拟录制</DialogTitle>
          <DialogDescription className="flex flex-wrap items-center gap-x-2 gap-y-1">
            <span className="break-all">{channel.name}</span>
            <ChannelProtocolBadge url={channel.url} />
            <span>实时检测 TS，接收的数据直接丢弃。</span>
          </DialogDescription>
        </DialogHeader>

        <div className="min-h-0 space-y-4 overflow-y-auto overscroll-contain">
          {!active && (
            <div className="flex flex-wrap items-center justify-between gap-2">
              <label htmlFor={durationId} className="text-sm font-medium">测试时长</label>
              <select
                id={durationId}
                className="h-9 rounded-md border border-input bg-background px-2 text-base disabled:opacity-50 sm:text-sm"
                value={duration}
                disabled={pending}
                onChange={(event) => setDuration(Number(event.target.value))}
              >
                <option value={30}>30 秒</option>
                <option value={60}>1 分钟</option>
                <option value={120}>2 分钟</option>
                <option value={300}>5 分钟</option>
              </select>
            </div>
          )}

          {!synced && <p role="status" className="text-sm text-muted-foreground">正在同步后端状态…</p>}
          {!active && hasChanges && <p className="text-sm text-muted-foreground">请先保存频道修改。</p>}
          {update && (
            <div className="space-y-2">
              <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
                <span role="status" className="font-medium">{statusLabels[update.status]}</span>
                <span className="tabular-nums text-muted-foreground">
                  {update.elapsedSeconds.toFixed(1)} / {update.durationSeconds} 秒 · 已接收 {formatBytes(update.bytesReceived)}
                </span>
              </div>
              {active && (
                <Progress value={Math.min(100, update.elapsedSeconds / update.durationSeconds * 100)} aria-label="模拟录制进度" />
              )}
              {update.errorMessage && <p role="alert" className="break-all text-sm text-destructive">{update.errorMessage}</p>}
              {!update.tsCheck && active && <p className="text-sm text-muted-foreground">正在等待 TS 检测数据…</p>}
            </div>
          )}

          {update?.tsCheck && <TsCheckDetails tsCheck={update.tsCheck} />}
        </div>

        <DialogFooter className="shrink-0 gap-2 sm:gap-0">
          <Button type="button" variant="outline" onClick={onClose}>关闭</Button>
          {active ? (
            <Button
              type="button"
              disabled={!synced || pending || update.status === 'stopping'}
              onClick={() => void changeSimulation(() => stopChannelRecordingSimulation(channel.id, update.id))}
            >
              停止模拟
            </Button>
          ) : (
            <Button
              type="button"
              disabled={!synced || pending || hasChanges}
              onClick={() => void changeSimulation(() => startChannelRecordingSimulation(channel.id, duration))}
            >
              {update ? '重新模拟' : '开始模拟'}
            </Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
