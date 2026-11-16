import { useCallback, useEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react'
import { ChevronDown, ScrollText, Trash2 } from 'lucide-react'

import { useAppContext } from '../../app/context'
import type { RecordingTaskDto, TaskErrorLogEntry } from '../../types'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'

const HEIGHT_STORAGE_KEY = 'rtsp_error_panel_height'
const MIN_HEIGHT = 140
const MAX_HEIGHT_RATIO = 0.5

type GroupSeverity = 'ok' | 'warning' | 'error'

type LogGroup = {
  taskId: number
  taskName: string
  channelName: string
  fileName?: string
  severity: GroupSeverity
  entries: TaskErrorLogEntry[]
  errorCount: number
  warningCount: number
  latestAt: number
}

const pad = (value: number) => String(value).padStart(2, '0')

const formatClock = (value: number | string | Date) => {
  const date = new Date(value)
  return `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`
}

const hasOwnTimePrefix = (message: string) => /^\d{2}:\d{2}:\d{2}/.test(message)

const fileNameOf = (filePath?: string) => {
  if (!filePath) return undefined
  const name = filePath.split(/[/\\]/).pop()
  return name || undefined
}

const isRecordingStatus = (task: RecordingTaskDto) => task.status === 'Recording' || task.status === 1

const dotClass = (severity: GroupSeverity) =>
  severity === 'error'
    ? 'bg-red-500'
    : severity === 'warning'
      ? 'bg-amber-500'
      : 'bg-emerald-500'

const lineClass = (severity: GroupSeverity) =>
  severity === 'error'
    ? 'border-red-500/70 bg-red-500/5 text-red-700 dark:text-red-400'
    : 'border-amber-500/70 bg-amber-500/5 text-amber-700 dark:text-amber-400'

const readStoredHeight = () => {
  try {
    const stored = Number(window.localStorage.getItem(HEIGHT_STORAGE_KEY))
    return Number.isFinite(stored) && stored >= MIN_HEIGHT ? stored : 260
  } catch {
    return 260
  }
}

const clampHeight = (value: number) => {
  const max = Math.max(MIN_HEIGHT, Math.round(window.innerHeight * MAX_HEIGHT_RATIO))
  return Math.min(max, Math.max(MIN_HEIGHT, Math.round(value)))
}

const TaskErrorLogPanel = () => {
  const { taskErrorLog, tasks, clearTaskErrorLog } = useAppContext()
  const [expanded, setExpanded] = useState(false)
  const [height, setHeight] = useState(readStoredHeight)
  const heightRef = useRef(height)
  const dragRef = useRef<{ startY: number; startHeight: number } | null>(null)
  const [collapsedGroups, setCollapsedGroups] = useState<Set<number>>(() => new Set())

  useEffect(() => {
    heightRef.current = height
  }, [height])

  const groups = useMemo(() => {
    const entriesByTask = new Map<number, TaskErrorLogEntry[]>()
    for (const entry of taskErrorLog) {
      const list = entriesByTask.get(entry.taskId)
      if (list) {
        list.push(entry)
      } else {
        entriesByTask.set(entry.taskId, [entry])
      }
    }

    const result: LogGroup[] = []
    for (const [taskId, entries] of entriesByTask) {
      const sorted = [...entries].sort(
        (left, right) => new Date(right.timestamp).getTime() - new Date(left.timestamp).getTime(),
      )
      const latestEntry = sorted[0]
      const errorCount = entries.filter((entry) => entry.severity === 'error').length
      const warningCount = entries.length - errorCount
      result.push({
        taskId,
        taskName: latestEntry.taskName || latestEntry.channelName || `任务 ${taskId}`,
        channelName: latestEntry.channelName,
        fileName: latestEntry.fileName,
        severity: errorCount > 0 ? 'error' : 'warning',
        entries: sorted,
        errorCount,
        warningCount,
        latestAt: new Date(latestEntry.timestamp).getTime(),
      })
    }

    for (const task of tasks) {
      if (entriesByTask.has(task.id) || !isRecordingStatus(task)) {
        continue
      }
      result.push({
        taskId: task.id,
        taskName: task.displayName || task.taskName || `任务 ${task.id}`,
        channelName: task.channelName,
        fileName: fileNameOf(task.filePath),
        severity: 'ok',
        entries: [],
        errorCount: 0,
        warningCount: 0,
        latestAt: task.startedAt ? new Date(task.startedAt).getTime() : 0,
      })
    }

    return result.sort((left, right) => {
      const leftHasEntries = left.entries.length > 0 ? 1 : 0
      const rightHasEntries = right.entries.length > 0 ? 1 : 0
      if (leftHasEntries !== rightHasEntries) {
        return rightHasEntries - leftHasEntries
      }
      return right.latestAt - left.latestAt
    })
  }, [taskErrorLog, tasks])

  const errorGroups = groups.filter((group) => group.severity === 'error').length
  const warningGroups = groups.filter((group) => group.severity === 'warning').length
  const totalEntries = taskErrorLog.length

  const toggleExpanded = useCallback(() => setExpanded((value) => !value), [])

  const toggleGroup = useCallback((taskId: number) => {
    setCollapsedGroups((previous) => {
      const next = new Set(previous)
      if (next.has(taskId)) {
        next.delete(taskId)
      } else {
        next.add(taskId)
      }
      return next
    })
  }, [])

  const onResizeStart = useCallback((event: ReactPointerEvent<HTMLDivElement>) => {
    event.preventDefault()
    dragRef.current = { startY: event.clientY, startHeight: heightRef.current }
    try {
      event.currentTarget.setPointerCapture(event.pointerId)
    } catch {
      // 指针已失效时忽略，拖拽仍按事件流处理
    }
  }, [])

  const onResizeMove = useCallback((event: ReactPointerEvent<HTMLDivElement>) => {
    if (!dragRef.current) return
    const next = clampHeight(dragRef.current.startHeight - (event.clientY - dragRef.current.startY))
    heightRef.current = next
    setHeight(next)
  }, [])

  const onResizeEnd = useCallback((event: ReactPointerEvent<HTMLDivElement>) => {
    if (!dragRef.current) return
    dragRef.current = null
    try {
      event.currentTarget.releasePointerCapture(event.pointerId)
    } catch {
      // 忽略
    }
    try {
      window.localStorage.setItem(HEIGHT_STORAGE_KEY, String(heightRef.current))
    } catch {
      // 忽略本地存储失败
    }
  }, [])

  return (
    <section className="shrink-0 border-t bg-background" aria-label="实时错误日志">
      {expanded && (
        <div
          className="flex h-2 cursor-row-resize touch-none items-center justify-center hover:bg-muted/50"
          role="separator"
          aria-orientation="horizontal"
          aria-label="调整错误日志面板高度"
          onPointerDown={onResizeStart}
          onPointerMove={onResizeMove}
          onPointerUp={onResizeEnd}
          onPointerCancel={onResizeEnd}
        >
          <div className="h-[3px] w-11 rounded-full bg-border" />
        </div>
      )}

      <div className="flex h-9 items-center gap-1 px-3">
        <button
          type="button"
          aria-expanded={expanded}
          className="flex min-w-0 flex-1 items-center gap-2 rounded-md text-left outline-none hover:bg-muted/30"
          onClick={toggleExpanded}
        >
          <ScrollText className="h-4 w-4 shrink-0 text-muted-foreground" />
          <span className="shrink-0 text-xs font-medium whitespace-nowrap">实时错误日志</span>
          <span className="flex min-w-0 items-center gap-1.5 text-xs text-muted-foreground" aria-live="polite">
            {errorGroups === 0 && warningGroups === 0 ? (
              <>
                <span className={cn('h-1.5 w-1.5 shrink-0 rounded-full', dotClass('ok'))} />
                全部正常
              </>
            ) : (
              <>
                {errorGroups > 0 && (
                  <>
                    <span className={cn('h-1.5 w-1.5 shrink-0 rounded-full', dotClass('error'))} />
                    {errorGroups} 个任务异常
                  </>
                )}
                {warningGroups > 0 && (
                  <>
                    <span
                      className={cn(
                        'h-1.5 w-1.5 shrink-0 rounded-full',
                        dotClass('warning'),
                        errorGroups > 0 && 'ml-1.5',
                      )}
                    />
                    {warningGroups} 个任务警告
                  </>
                )}
              </>
            )}
            {totalEntries > 0 && <span className="ml-1.5 hidden sm:inline">共 {totalEntries} 条</span>}
          </span>
        </button>
        <Button
          variant="ghost"
          size="sm"
          className="h-7 shrink-0 px-2 text-xs text-muted-foreground"
          disabled={totalEntries === 0}
          onClick={() => void clearTaskErrorLog()}
        >
          <Trash2 className="mr-1 h-3.5 w-3.5" />
          清空
        </Button>
        <button
          type="button"
          aria-label={expanded ? '收起错误日志' : '展开错误日志'}
          aria-expanded={expanded}
          className="grid h-7 w-7 shrink-0 place-items-center rounded-md text-muted-foreground outline-none hover:bg-muted/60"
          onClick={toggleExpanded}
        >
          <ChevronDown
            className={cn('h-4 w-4 transition-transform', !expanded && 'rotate-180')}
          />
        </button>
      </div>

      {expanded && (
        <div className="overflow-y-auto border-t" style={{ height }}>
          {groups.length === 0 ? (
            <div className="flex h-full items-center justify-center gap-2 text-xs text-muted-foreground">
              <span className={cn('h-1.5 w-1.5 rounded-full', dotClass('ok'))} />
              暂无错误，全部正常
            </div>
          ) : (
            groups.map((group) => {
              const collapsed = collapsedGroups.has(group.taskId)
              return (
                <div key={group.taskId} className="border-b border-border/60 last:border-b-0">
                  <button
                    type="button"
                    aria-expanded={!collapsed}
                    className="flex min-h-9 w-full items-center gap-2 px-3 text-left outline-none hover:bg-muted/30"
                    onClick={() => toggleGroup(group.taskId)}
                  >
                    <span className={cn('h-2 w-2 shrink-0 rounded-full', dotClass(group.severity))} />
                    <span className="truncate text-xs font-medium">{group.taskName}</span>
                    <span className="hidden min-w-0 truncate text-[11px] text-muted-foreground sm:inline">
                      {group.channelName}
                      {group.fileName ? ` · ${group.fileName}` : ''}
                    </span>
                    <span className="ml-auto flex shrink-0 items-center gap-1">
                      {group.errorCount > 0 && (
                        <span className="rounded-full bg-red-500/10 px-1.5 py-0.5 text-[10px] text-red-600 dark:text-red-400">
                          错误 {group.errorCount}
                        </span>
                      )}
                      {group.warningCount > 0 && (
                        <span className="rounded-full bg-amber-500/10 px-1.5 py-0.5 text-[10px] text-amber-600 dark:text-amber-400">
                          警告 {group.warningCount}
                        </span>
                      )}
                      {group.entries.length === 0 && (
                        <span className="rounded-full bg-emerald-500/10 px-1.5 py-0.5 text-[10px] text-emerald-600 dark:text-emerald-400">
                          正常
                        </span>
                      )}
                      {group.latestAt > 0 && (
                        <span className="hidden text-[10px] tabular-nums text-muted-foreground sm:inline">
                          {formatClock(group.latestAt)}
                        </span>
                      )}
                      <ChevronDown
                        className={cn(
                          'h-3.5 w-3.5 text-muted-foreground transition-transform',
                          collapsed && '-rotate-90',
                        )}
                      />
                    </span>
                  </button>

                  {!collapsed && (
                    <div className="overflow-x-auto pb-1.5 pl-4 pr-2">
                      {group.entries.length === 0 ? (
                        <div className="flex items-center gap-2 whitespace-pre rounded-sm border-l-2 border-emerald-500/60 bg-emerald-500/5 px-2 py-0.5 font-mono text-[11px] leading-5 text-emerald-700 dark:text-emerald-400">
                          <span>✓</span>
                          <span>暂无异常</span>
                        </div>
                      ) : (
                        group.entries.map((entry) => (
                          <div
                            key={entry.id}
                            className={cn(
                              'flex items-baseline gap-2 whitespace-pre rounded-sm border-l-2 px-2 py-0.5 font-mono text-[11px] leading-5',
                              lineClass(entry.severity),
                            )}
                          >
                            {!hasOwnTimePrefix(entry.message) && (
                              <span className="shrink-0 text-muted-foreground">
                                {formatClock(entry.timestamp)}
                              </span>
                            )}
                            <span>{entry.message}</span>
                          </div>
                        ))
                      )}
                    </div>
                  )}
                </div>
              )
            })
          )}
        </div>
      )}
    </section>
  )
}

export default TaskErrorLogPanel