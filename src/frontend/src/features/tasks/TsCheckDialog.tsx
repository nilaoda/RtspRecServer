import { formatBytes } from '../shared/format'
import type { RecordingTaskDto, TsStreamTrack } from '../../types'
import { tsTrackText, tsVerdictClass, tsVerdictLabel } from './ts-display'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Separator } from '@/components/ui/separator'
import { cn } from '@/lib/utils'

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-md border bg-muted/30 px-2 py-1.5">
      <div className="text-[10px] text-muted-foreground">{label}</div>
      <div className="truncate font-mono text-xs tabular-nums">{value}</div>
    </div>
  )
}

function TrackList({ title, tracks, fallback }: { title: string; tracks?: TsStreamTrack[]; fallback?: string }) {
  return (
    <div className="space-y-1">
      <div className="text-xs text-muted-foreground">{title}</div>
      {tracks && tracks.length > 0 ? (
        <div className="flex flex-wrap gap-1.5">
          {tracks.map((track) => (
            <span
              key={`${track.pid}-${track.label}`}
              className="inline-flex items-center gap-1 rounded border bg-muted/40 px-1.5 py-0.5 text-[11px]"
            >
              {track.label}
              <span className="font-mono text-[10px] text-muted-foreground">
                0x{track.pid.toString(16).toUpperCase().padStart(4, '0')}
              </span>
            </span>
          ))}
        </div>
      ) : (
        <div className="text-xs">{fallback || tsTrackText(tracks) || '未识别'}</div>
      )}
    </div>
  )
}

export default function TsCheckDialog({
  task,
  onOpenChange,
}: {
  task: RecordingTaskDto | null
  onOpenChange: (open: boolean) => void
}) {
  const tsCheck = task?.tsCheck ?? null
  const isRecording = task != null && (task.status === 'Recording' || task.status === 1)

  return (
    <Dialog open={task != null} onOpenChange={onOpenChange}>
      <DialogContent className="max-h-[85dvh] max-w-[calc(100vw-2rem)] overflow-y-auto sm:max-w-[480px]">
        <DialogHeader>
          <DialogTitle>TS 检测详情</DialogTitle>
          <DialogDescription className="space-y-0.5">
            <span className="block truncate">{task?.displayName || task?.taskName || '--'}</span>
            <span className="block text-xs">{task?.channelName}</span>
            {isRecording && (
              <span className="mt-1 inline-flex items-center gap-1 text-xs text-primary">
                <span className="h-1.5 w-1.5 animate-pulse rounded-full bg-primary" />
                检测中 · 数据实时更新
              </span>
            )}
          </DialogDescription>
        </DialogHeader>

        {tsCheck ? (
          <div className="space-y-4">
            <div className="flex items-center gap-2">
              <Badge variant="outline" className={cn('text-xs', tsVerdictClass(tsCheck.verdict))}>
                {tsVerdictLabel[tsCheck.verdict] ?? '未知'}
              </Badge>
              <span className="text-xs text-muted-foreground">
                错误 {tsCheck.errorCount} · 警告 {tsCheck.warningCount}
              </span>
            </div>

            <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
              <Stat label="码率" value={`${tsCheck.bitrateKbps.toFixed(0)} Kb/s`} />
              <Stat label="数据包" value={tsCheck.packetCount.toLocaleString()} />
              <Stat label="扫描大小" value={formatBytes(tsCheck.bytesScanned)} />
              <Stat label="PID 数" value={String(tsCheck.pidCount)} />
            </div>

            <Separator />

            <div className="space-y-2">
              <TrackList title="视频" tracks={tsCheck.videoTracks} fallback={tsCheck.video} />
              <TrackList title="音频" tracks={tsCheck.audioTracks} fallback={tsCheck.audio} />
            </div>

            <Separator />

            <div className="space-y-1.5">
              <div className="text-xs text-muted-foreground">异常时刻（本地时间 / 相对开始，* 为估算时刻）</div>
              {tsCheck.anomalyEvents && tsCheck.anomalyEvents.length > 0 ? (
                <div className="max-h-40 overflow-y-auto rounded-md border bg-muted/30 p-2">
                  {tsCheck.anomalyEvents.map((event, index) => (
                    <div key={index} className="break-all font-mono text-[11px] leading-5">
                      {event}
                    </div>
                  ))}
                </div>
              ) : (
                <div className="text-xs text-muted-foreground">无异常事件</div>
              )}
            </div>

            {tsCheck.lastEvent && (
              <div className="space-y-1">
                <div className="text-xs text-muted-foreground">最近事件</div>
                <div className="break-all rounded-md border bg-muted/30 p-2 font-mono text-[11px]">
                  {tsCheck.lastEvent}
                </div>
              </div>
            )}
          </div>
        ) : (
          <div className="text-sm text-muted-foreground">
            {isRecording ? '正在等待检测数据…' : '暂无检测数据'}
          </div>
        )}

        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)}>
            关闭
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}