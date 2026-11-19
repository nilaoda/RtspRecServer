import { useMemo, type ReactNode } from 'react'
import { Activity, ChevronRight, CircleAlert, HardDrive, Radio, TriangleAlert, Video } from 'lucide-react'
import { Link } from 'react-router-dom'

import { useAppContext } from '../../app/context'
import type { DiskStatus, RecordingTaskDto, TaskErrorLogEntry } from '../../types'
import { ChannelProtocolBadge } from '@/components/channel-protocol-badge'
import { Progress } from '@/components/ui/progress'
import { cn } from '@/lib/utils'
import { formatBytes, formatDateTime } from '../shared/format'
import { getRecordingBitrateKbps, normalizeStatus, waitReasonLabel } from '../shared/recording-task'
import { useRecordingRuntime } from '../shared/useRecordingRuntime'

const nextDay = 24 * 60 * 60 * 1000
const recentTsWindow = 15 * 60 * 1000
const clock = new Intl.DateTimeFormat('zh-CN', { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' })
const taskName = (task: RecordingTaskDto) => task.displayName || task.taskName || task.channelName

type OverviewAlert = {
  id: string
  kind: 'disk' | 'error' | 'warning'
  title: string
  message: string
  task?: RecordingTaskDto
  timestamp?: number
}

const getAlerts = (tasks: RecordingTaskDto[], entries: TaskErrorLogEntry[], disk: DiskStatus | null, now: number) => {
  const alerts: OverviewAlert[] = []
  const active = new Map<number, RecordingTaskDto>()
  for (const task of tasks) {
    if (normalizeStatus(task.status) === 'Recording') active.set(task.id, task)
    const finishedAt = Date.parse(task.finishedAt ?? '')
    if (normalizeStatus(task.status) === 'Failed' && finishedAt >= now - nextDay) {
      alerts.push({ id: `failed-${task.id}`, kind: 'error', title: `${task.channelName} · 录制失败`,
        message: task.errorMessage || taskName(task), task, timestamp: finishedAt })
    }
  }

  const latestTsEntries = new Map<number, TaskErrorLogEntry>()
  for (const entry of active.size === 0 ? [] : entries) {
    if (!active.has(entry.taskId)) continue
    const timestamp = Date.parse(entry.timestamp)
    if (!Number.isFinite(timestamp) || timestamp < now - recentTsWindow) continue
    const previous = latestTsEntries.get(entry.taskId)
    if (!previous || timestamp >= Date.parse(previous.timestamp)) latestTsEntries.set(entry.taskId, entry)
  }
  for (const entry of latestTsEntries.values()) {
    alerts.push({ id: `ts-${entry.taskId}`, kind: entry.severity, title: `${entry.channelName} · TS 异常`,
      message: entry.message, task: active.get(entry.taskId), timestamp: Date.parse(entry.timestamp) })
  }
  alerts.sort((left, right) => (right.timestamp ?? 0) - (left.timestamp ?? 0))

  if (disk && disk.freeBytes < Math.min(20 * 1024 ** 3, disk.totalBytes / 10)) {
    alerts.unshift({ id: 'disk', kind: 'disk', title: '录像磁盘空间不足', message: `剩余 ${formatBytes(disk.freeBytes)}，可前往录制管理检查占用。` })
  }
  return alerts
}

const Metric = ({ icon, label, value, unit, children, className }: {
  icon: ReactNode
  label: string
  value: string | number
  unit?: string
  children: ReactNode
  className?: string
}) => (
  <div className={cn('min-w-0', className)}>
    <dt className="flex items-center gap-2 text-xs font-medium text-[#b3c7c2] sm:text-sm">{icon}{label}</dt>
    <dd className="mt-3 flex flex-wrap items-baseline gap-x-1.5 gap-y-0.5 text-[#f2f8f5]">
      <span className="text-[1.8rem] font-semibold leading-none tracking-tight tabular-nums sm:text-3xl xl:text-[2.5rem]">{value}</span>
      {unit && <span className="text-xs text-[#b3c7c2] sm:text-sm">{unit}</span>}
    </dd>
    <dd className="mt-2 text-xs leading-5 text-[#b3c7c2]">{children}</dd>
  </div>
)

const OverviewRow = ({ to, children, className }: {
  to: string
  children: ReactNode
  className?: string
}) => (
  <Link to={to} className={cn('group flex w-full items-center gap-3 rounded-md px-2 py-3 text-left transition-colors hover:bg-muted/50 focus-visible:outline focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-ring sm:px-3', className)}>
    {children}
    <ChevronRight aria-hidden="true" className="h-4 w-4 shrink-0 text-muted-foreground motion-safe:transition-transform motion-safe:group-hover:translate-x-0.5" />
  </Link>
)

const Empty = ({ children }: { children: ReactNode }) => (
  <p className="px-3 py-7 text-sm text-muted-foreground">{children}</p>
)

export default function OverviewPage() {
  const app = useAppContext()
  const runtime = useRecordingRuntime(true)
  const { tasks, channels, taskErrorLog, totalBitrateKbps } = app
  const diskStatus = app.systemStatus?.disk
  const disk = diskStatus && diskStatus.totalBytes > 0 ? diskStatus : null
  const now = app.now.getTime()
  const active = useMemo(() => tasks.filter(task => normalizeStatus(task.status) === 'Recording'), [tasks])
  const planned = useMemo(() => tasks.filter(task =>
    normalizeStatus(task.status) === 'Pending' && Date.parse(task.startTime) <= now + nextDay)
    .sort((left, right) => Date.parse(left.startTime) - Date.parse(right.startTime)), [tasks, now])
  const alerts = useMemo(() => getAlerts(tasks, taskErrorLog, disk, now), [tasks, taskErrorLog, disk, now])
  const channelById = useMemo(() => new Map(channels.map(channel => [channel.id, channel])), [channels])
  const diskWarning = alerts[0]?.kind === 'disk'
  const [freeSpace, freeUnit] = disk ? formatBytes(disk.freeBytes).split(' ') : ['—']

  return (
    <div className="mx-auto max-w-[1240px] space-y-6 pb-6">
      {app.connectionStatus === 'disconnected' && (
        <p role="status" className="rounded-lg border border-amber-500/25 bg-amber-500/10 px-4 py-3 text-sm">服务连接中断，任务和磁盘状态可能已过时。</p>
      )}

      <section aria-labelledby="overview-active" className="motion-safe:animate-in motion-safe:fade-in motion-safe:slide-in-from-bottom-1">
        <div className="rounded-xl bg-[#162725] px-4 py-5 text-[#f2f8f5] sm:px-7 sm:py-6 dark:ring-1 dark:ring-white/10">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h2 id="overview-active" className="flex items-center gap-2.5 text-xl font-semibold tracking-tight sm:text-2xl">
              <span aria-hidden="true" className={cn('h-2 w-2 rounded-full', (runtime?.activeWriterCount ?? 0) > 0 ? 'bg-emerald-400' : 'bg-[#82948f]')} />
              正在录制
            </h2>
            <div className="flex flex-wrap items-center gap-1.5 sm:gap-2">
              {alerts.length > 0 && (
                <a href="#overview-alerts" className="flex items-center gap-1.5 rounded-md bg-amber-300/10 px-2 py-1 text-xs font-medium text-amber-100 transition-colors hover:bg-amber-300/20">
                  <TriangleAlert aria-hidden="true" className="h-3.5 w-3.5" />{alerts.length} 项需处理
                </a>
              )}
              <Link to="/tasks" className="flex items-center gap-1 rounded-md px-2 py-1 text-xs text-[#c5d8d1] transition-colors hover:bg-white/10 hover:text-white sm:text-sm">
                全部任务 <ChevronRight aria-hidden="true" className="h-4 w-4" />
              </Link>
            </div>
          </div>
          <dl className="mt-5 grid grid-cols-2 gap-x-2 gap-y-5 border-t border-white/15 pt-5 sm:mt-6 sm:grid-cols-4 sm:gap-0 sm:pt-6">
            <Metric icon={<Video aria-hidden="true" className="h-4 w-4" />} label="写入任务" value={runtime?.activeWriterCount ?? '—'} unit={runtime ? '个' : undefined}>
              {runtime ? `写入上限 ${runtime.writerLimit} 个` : '等待服务状态'}
            </Metric>
            <Metric icon={<Radio aria-hidden="true" className="h-4 w-4" />} label="收流占用" value={runtime?.activeSourceCount ?? '—'} unit={runtime ? '路' : undefined} className="border-l border-white/15 pl-3 sm:pl-5">
              {runtime ? `收流上限 ${runtime.sourceLimit} 路` : '等待服务状态'}
            </Metric>
            <Metric icon={<Activity aria-hidden="true" className="h-4 w-4" />} label="总写入速率" value={runtime ? (totalBitrateKbps / 1000).toFixed(1) : '—'} unit={runtime ? 'Mbps' : undefined} className="sm:border-l sm:border-white/15 sm:pl-5">
              各录像文件写入合计
            </Metric>
            <Metric icon={<HardDrive aria-hidden="true" className="h-4 w-4" />} label="磁盘剩余" value={freeSpace} unit={freeUnit} className="border-l border-white/15 pl-3 sm:pl-5">
              {disk ? (
                <span className="flex items-center gap-2">
                  <Progress aria-label="磁盘空闲百分比" value={Math.min(100, disk.freeBytes / disk.totalBytes * 100)}
                    className="h-1 max-w-14 bg-white/15" indicatorClassName={cn('motion-reduce:transition-none', diskWarning ? 'bg-amber-400' : 'bg-emerald-400')} />
                  <span className="whitespace-nowrap">共 {formatBytes(disk.totalBytes)}</span>
                </span>
              ) : '空间数据不可用'}
            </Metric>
          </dl>
        </div>

        <ul aria-label="正在录制的任务" className="mt-2 divide-y border-b px-1 sm:px-3">
          {active.slice(0, 5).map(task => {
            const channel = channelById.get(task.channelId)
            const shared = runtime?.sharedTaskCounts[task.id] ?? 0
            const rate = getRecordingBitrateKbps(task, now)
            return (
              <li key={task.id}>
                <OverviewRow to={`/tasks?task=${task.id}`}>
                  <span aria-hidden="true" className="h-1.5 w-1.5 shrink-0 rounded-full bg-emerald-600 dark:bg-emerald-400" />
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-sm font-semibold" title={taskName(task)}>{taskName(task)}</p>
                    <div className="mt-1 flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-muted-foreground">
                      <span>{task.channelName}</span>
                      {channel && <ChannelProtocolBadge url={channel.url} />}
                      {shared > 1 && <span className="text-emerald-700 dark:text-emerald-300">共享收流 · {shared} 个任务</span>}
                    </div>
                  </div>
                  <div className="shrink-0 text-right text-xs tabular-nums">
                    <p className="font-medium">{rate > 0 ? `${(rate / 1000).toFixed(1)} Mbps` : '等待数据'}</p>
                    {task.isLive && <p className="mt-1 text-muted-foreground">{clock.format(new Date(task.endTime))} 停录</p>}
                  </div>
                </OverviewRow>
              </li>
            )
          })}
          {active.length === 0 && <li><Empty>{runtime ? '暂无正在录制的任务' : '正在获取录制状态…'}</Empty></li>}
        </ul>
      </section>

      <div className="grid gap-7 xl:grid-cols-[1.08fr_1fr] xl:gap-9 motion-safe:animate-in motion-safe:fade-in motion-safe:slide-in-from-bottom-1">
        <section aria-labelledby="overview-planned">
          <div className="flex items-baseline justify-between gap-2 border-b pb-3">
            <h2 id="overview-planned" className="text-lg font-semibold tracking-tight sm:text-xl">接下来要录</h2>
            <span className="text-xs text-muted-foreground">未来 24 小时{planned.length > 0 && ` · ${planned.length} 项`}</span>
          </div>
          {planned.length === 0 ? <Empty>{runtime ? '暂无待录任务' : '正在获取录制计划…'}</Empty> : (
            <ul className="divide-y">
              {planned.slice(0, 5).map(task => {
                const start = new Date(task.startTime)
                const reason = runtime?.pendingReasons[task.id]
                const waitingForCapacity = reason === 'sourceLimit' || reason === 'writerLimit'
                const onSameDay = start.toDateString() === new Date(now).toDateString()
                return (
                  <li key={task.id}>
                    <OverviewRow to={`/tasks?task=${task.id}`} className="gap-3 sm:gap-4">
                      <time dateTime={task.startTime} className="w-12 shrink-0 text-sm font-semibold tabular-nums sm:w-16">
                        {!onSameDay && <span className="block text-[11px] font-normal text-muted-foreground">{formatDateTime(start).slice(5, 10)}</span>}
                        {clock.format(start)}
                      </time>
                      <div className="min-w-0 flex-1 border-l pl-3">
                        <p className="truncate text-sm font-semibold" title={taskName(task)}>{taskName(task)}</p>
                        <p className="mt-1 truncate text-xs text-muted-foreground">{task.channelName} {task.timerId && '· 定时规则'}</p>
                      </div>
                      <span className={cn('max-w-[7rem] text-right text-xs leading-5', waitingForCapacity ? 'text-amber-700 dark:text-amber-300' : 'text-muted-foreground')}>
                        {reason ? waitReasonLabel[reason] : '待录制'}
                      </span>
                    </OverviewRow>
                  </li>
                )
              })}
            </ul>
          )}
        </section>

        <section aria-labelledby="overview-alerts">
          <div className="flex items-baseline justify-between gap-2 border-b pb-3">
            <h2 id="overview-alerts" className="scroll-mt-4 text-lg font-semibold tracking-tight sm:text-xl">需要处理</h2>
            <span className="text-xs text-muted-foreground">当前异常 · 近 24 小时失败</span>
          </div>
          {alerts.length === 0 ? <Empty>{runtime ? '暂无需要处理的异常' : '正在获取异常状态…'}</Empty> : (
            <ul className="divide-y">
              {alerts.slice(0, 5).map(alert => {
                const Icon = alert.kind === 'disk' ? HardDrive : alert.kind === 'error' ? CircleAlert : TriangleAlert
                return (
                  <li key={alert.id}>
                    <OverviewRow to={alert.task ? `/tasks?task=${alert.task.id}` : '/recordings'}>
                      <span className={cn('flex h-8 w-8 shrink-0 items-center justify-center rounded-md', alert.kind === 'error'
                        ? 'bg-red-500/10 text-red-700 dark:text-red-300' : 'bg-amber-500/10 text-amber-700 dark:text-amber-300')}>
                        <Icon aria-hidden="true" className="h-4 w-4" />
                      </span>
                      <div className="min-w-0 flex-1">
                        <p className="text-sm font-semibold">{alert.title}</p>
                        <p className="mt-1 line-clamp-2 text-xs leading-5 text-muted-foreground">{alert.message}</p>
                      </div>
                      {alert.timestamp !== undefined && <time dateTime={new Date(alert.timestamp).toISOString()} className="shrink-0 text-xs tabular-nums text-muted-foreground">{clock.format(alert.timestamp)}</time>}
                    </OverviewRow>
                  </li>
                )
              })}
            </ul>
          )}
        </section>
      </div>
    </div>
  )
}
