import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import * as z from 'zod'
import { Loader2, Plus, Info, Download, Trash2, StopCircle, Ban, Check, ChevronsUpDown, RefreshCw, CalendarClock, ChevronLeft, ChevronRight } from 'lucide-react'
import { toast } from 'sonner'

import { useAppContext } from '../../app/context'
import type { RecordingStatus, RecordingTaskDto, TsCheckSummary, TsStreamTrack } from '../../types'
import { formatBytes, formatDateTime } from '../shared/format'
import { ChannelProtocolBadge } from '@/components/channel-protocol-badge'

import { cn } from "@/lib/utils"
import { Button } from "@/components/ui/button"
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"
import {
  Form,
  FormControl,
  FormDescription,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
} from "@/components/ui/form"
import { Input } from "@/components/ui/input"
import {
  Command,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
} from "@/components/ui/command"
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover"
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
} from "@/components/ui/tooltip"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import { Badge } from "@/components/ui/badge"
import { Progress } from "@/components/ui/progress"
import { DateTimePicker } from "@/components/ui/date-time-picker"
import {
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
} from "@/components/ui/tabs"
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog"

type RecordingStatusKey = 'Pending' | 'Recording' | 'Completed' | 'Failed' | 'Stopped'

const statusKeys: RecordingStatusKey[] = ['Pending', 'Recording', 'Completed', 'Failed', 'Stopped']

const normalizeStatus = (value: RecordingStatus): RecordingStatusKey => {
  if (typeof value === 'number') {
    return statusKeys[value] ?? 'Pending'
  }
  if (statusKeys.includes(value as RecordingStatusKey)) {
    return value as RecordingStatusKey
  }
  return 'Pending'
}

const formatDuration = (milliseconds: number) => {
  if (!Number.isFinite(milliseconds) || milliseconds < 0) {
    return '--'
  }
  const totalSeconds = Math.floor(milliseconds / 1000)
  const hours = Math.floor(totalSeconds / 3600)
  const minutes = Math.floor((totalSeconds % 3600) / 60)
  const seconds = totalSeconds % 60
  const pad = (value: number) => String(value).padStart(2, '0')
  return `${pad(hours)}:${pad(minutes)}:${pad(seconds)}`
}

const statusLabel: Record<RecordingStatusKey, string> = {
  Pending: '等待中',
  Recording: '录制中',
  Completed: '已完成',
  Failed: '失败',
  Stopped: '已停止',
}

const statusVariant: Record<RecordingStatusKey, "default" | "secondary" | "destructive" | "outline"> = {
  Pending: 'secondary', // warning -> secondary (yellowish/gray)
  Recording: 'default', // processing -> primary (blue/black)
  Completed: 'outline', // success -> outline (greenish via class)
  Failed: 'destructive', // error -> destructive (red)
  Stopped: 'secondary', // default -> secondary
}

type TaskTab = 'active' | 'completed'

const PAGE_SIZE = 20

const taskTabs: Record<TaskTab, { label: string; empty: string }> = {
  active: { label: '正在录制', empty: '暂无正在录制或等待中的任务' },
  completed: { label: '已完成录制', empty: '暂无已完成录制' },
}

const getTaskTab = (status: RecordingStatusKey): TaskTab => {
  if (status === 'Pending' || status === 'Recording') {
    return 'active'
  }
  return 'completed'
}

const parseTaskTime = (value?: string) => {
  if (!value) {
    return Number.NaN
  }
  const time = new Date(value).getTime()
  return Number.isFinite(time) ? time : Number.NaN
}

const getTaskEndTime = (task: RecordingTaskDto) => {
  const finishedAt = parseTaskTime(task.finishedAt)
  return Number.isFinite(finishedAt) ? finishedAt : parseTaskTime(task.endTime)
}

type TaskDisplay = {
  normalizedStatus: RecordingStatusKey
  start: number
  displayStart: number
  startedAt: number
  plannedDuration: number
  recordedDuration: number
  progress: number
  bitrateDisplay: string
  showInfoActions: boolean
  canEditEndTime: boolean
  canRetry: boolean
}

const getTaskDisplay = (task: RecordingTaskDto, nowMs: number): TaskDisplay => {
  const normalizedStatus = normalizeStatus(task.status)
  const start = parseTaskTime(task.startTime)
  const end = parseTaskTime(task.endTime)
  const startedAt = parseTaskTime(task.startedAt)
  const finishedAt = parseTaskTime(task.finishedAt)
  const displayStart = task.isLive && Number.isFinite(startedAt) ? startedAt : start
  const durationStart = task.isLive
    ? (Number.isFinite(startedAt)
      ? startedAt
      : normalizedStatus === 'Pending' && nowMs > start
        ? nowMs
        : start)
    : start
  const plannedDuration = Number.isFinite(durationStart) && Number.isFinite(end)
    ? Math.max(0, end - durationStart)
    : 0
  const recordedDuration = task.pcrElapsedSeconds !== undefined && task.pcrElapsedSeconds > 0
    ? task.pcrElapsedSeconds * 1000
    : (Number.isFinite(startedAt)
      ? Math.max(0, (normalizedStatus === 'Recording' ? nowMs : Number.isFinite(finishedAt) ? finishedAt : nowMs) - startedAt)
      : 0)
  const progress = normalizedStatus === 'Pending'
    ? 0
    : normalizedStatus === 'Completed'
      ? 100
      : (plannedDuration > 0 ? Math.min(100, Math.max(0, (recordedDuration / plannedDuration) * 100)) : 0)

  let bitrateDisplay = '--'
  if (task.currentBitrateKbps !== undefined && task.currentBitrateKbps > 0) {
    bitrateDisplay = `${task.currentBitrateKbps.toFixed(1)} Kb/s`
  } else if (Number.isFinite(startedAt) && task.bytesWritten > 0) {
    const endTime = Number.isFinite(finishedAt) ? finishedAt : nowMs
    const durationSeconds = Math.max(1, (endTime - startedAt) / 1000)
    const bitrateKbps = (task.bytesWritten * 8) / (durationSeconds * 1024)
    bitrateDisplay = `${bitrateKbps.toFixed(1)} Kb/s`
  }

  return {
    normalizedStatus,
    start,
    displayStart,
    startedAt,
    plannedDuration,
    recordedDuration,
    progress,
    bitrateDisplay,
    showInfoActions: ['Completed', 'Failed', 'Stopped'].includes(normalizedStatus),
    canEditEndTime: task.isLive && (normalizedStatus === 'Pending' || normalizedStatus === 'Recording'),
    canRetry: normalizedStatus === 'Failed' || normalizedStatus === 'Stopped',
  }
}

const getTaskDownloadFileName = (task: RecordingTaskDto) => {
  const pathName = task.filePath?.split(/[\\/]/).pop()
  if (pathName) {
    return pathName
  }

  const taskName = task.taskName.trim() || `task-${task.id}`
  return taskName.toLowerCase().endsWith('.ts') ? taskName : `${taskName}.ts`
}

const getTaskDownloadUrl = (task: RecordingTaskDto) =>
  `/api/tasks/${task.id}/download/${encodeURIComponent(getTaskDownloadFileName(task))}`

const TaskStatusBadge = ({ status }: { status: RecordingStatusKey }) => (
  <Badge variant={statusVariant[status]} className={cn(
    "whitespace-nowrap",
    status === 'Completed' && "bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-400 border-green-200 dark:border-green-800",
    status === 'Failed' && "bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-400 border-red-200 dark:border-red-800",
    status === 'Pending' && "bg-yellow-100 text-yellow-800 dark:bg-yellow-900/30 dark:text-yellow-400 border-yellow-200 dark:border-yellow-800",
    status === 'Recording' && "animate-pulse"
  )}>
    {statusLabel[status]}
  </Badge>
)

const TaskProgress = ({
  recordedDuration,
  plannedDuration,
  progress,
  status,
}: {
  recordedDuration: number
  plannedDuration: number
  progress: number
  status: RecordingStatusKey
}) => (
  <div className="w-full space-y-1">
    <div className="flex justify-between gap-2 whitespace-nowrap font-mono text-xs">
      <span>{formatDuration(recordedDuration)}</span>
      <span className="text-muted-foreground">/ {formatDuration(plannedDuration)}</span>
    </div>
    <Progress value={progress} className="h-1.5" indicatorClassName={cn(
      status === 'Failed' ? 'bg-destructive' :
      status === 'Stopped' ? 'bg-yellow-500' :
      status === 'Completed' ? 'bg-green-500' :
      'bg-primary'
    )} />
  </div>
)

const tsVerdictLabel: Record<string, string> = {
  pass: '正常',
  warning: '警告',
  error: '异常',
  incomplete: '未完成',
  unknown: '未知',
}

const tsVerdictClass = (verdict?: string) => {
  if (verdict === 'pass') return 'border-green-500/30 bg-green-500/10 text-green-600 dark:text-green-400'
  if (verdict === 'warning') return 'border-amber-500/30 bg-amber-500/10 text-amber-600 dark:text-amber-400'
  if (verdict === 'error') return 'border-red-500/30 bg-red-500/10 text-red-600 dark:text-red-400'
  return 'border-border bg-muted text-muted-foreground'
}

const tsTrackText = (tracks?: TsStreamTrack[], fallback?: string) => {
  const labels = tracks?.map((track) => track.label).filter(Boolean) ?? []
  const unique = [...new Set(labels)]
  return unique.length > 0 ? unique.join(' / ') : fallback || '未识别'
}

const tsCodecText = (tsCheck?: TsCheckSummary) => {
  if (!tsCheck) return '--'
  const streams = [
    tsTrackText(tsCheck.videoTracks, tsCheck.video),
    tsTrackText(tsCheck.audioTracks, tsCheck.audio),
  ].filter((value) => value !== '未识别')
  return streams.length > 0 ? streams.join(' + ') : '未识别轨道'
}

const taskSchema = z.object({
  channelId: z.string().min(1, "请选择频道"), // Select returns string
  startTime: z.date({ message: "请选择开始时间" }),
  endTime: z.date({ message: "请选择结束时间" }),
  taskName: z.string().optional(),
}).refine(data => data.endTime > data.startTime, {
  message: "结束时间必须大于开始时间",
  path: ["endTime"],
})

type TaskFormValues = z.infer<typeof taskSchema>

const TasksPage = () => {
  const { tasks, channels, createTask, stopTask, stopTasks, updateTaskEndTime, deleteTask, deleteTasks, getTaskMediaInfo, now, appConfig, totalBitrateKbps } = useAppContext()
  const [createOpen, setCreateOpen] = useState(false)
  const [infoModal, setInfoModal] = useState<{ title: string; content: string } | null>(null)
  const [infoLoading, setInfoLoading] = useState(false)
  const [openCombobox, setOpenCombobox] = useState(false)
  const comboboxListRef = useRef<HTMLDivElement>(null)
  const [activeTab, setActiveTab] = useState<TaskTab>('active')
  const [page, setPage] = useState(1)
  const [selectedTaskIds, setSelectedTaskIds] = useState<Set<number>>(() => new Set())
  const [bulkStopConfirmOpen, setBulkStopConfirmOpen] = useState(false)
  const [bulkDeleteConfirmOpen, setBulkDeleteConfirmOpen] = useState(false)

  const activeCount = useMemo(
    () => tasks.filter((task) => normalizeStatus(task.status) === 'Recording').length,
    [tasks],
  )

  const taskCounts = useMemo(() => {
    const counts: Record<TaskTab, number> = { active: 0, completed: 0 }
    for (const task of tasks) {
      counts[getTaskTab(normalizeStatus(task.status))] += 1
    }
    return counts
  }, [tasks])

  const visibleTasks = useMemo(() => {
    const filtered = tasks.filter((task) => getTaskTab(normalizeStatus(task.status)) === activeTab)
    return [...filtered].sort((left, right) => {
      if (activeTab === 'active') {
        const leftStatus = normalizeStatus(left.status)
        const rightStatus = normalizeStatus(right.status)
        if (leftStatus !== rightStatus) {
          return leftStatus === 'Recording' ? -1 : 1
        }
        if (leftStatus === 'Recording') {
          return right.id - left.id
        }

        const leftStart = parseTaskTime(left.startTime)
        const rightStart = parseTaskTime(right.startTime)
        return (Number.isFinite(leftStart) ? leftStart : 0) - (Number.isFinite(rightStart) ? rightStart : 0) || right.id - left.id
      }

      const leftEnd = getTaskEndTime(left)
      const rightEnd = getTaskEndTime(right)
      if (Number.isFinite(leftEnd) && Number.isFinite(rightEnd) && leftEnd !== rightEnd) {
        return rightEnd - leftEnd
      }
      if (Number.isFinite(leftEnd) !== Number.isFinite(rightEnd)) {
        return Number.isFinite(leftEnd) ? -1 : 1
      }
      return right.id - left.id
    })
  }, [tasks, activeTab])

  const totalPages = Math.max(1, Math.ceil(visibleTasks.length / PAGE_SIZE))
  const currentPage = Math.min(page, totalPages)
  const pagedTasks = useMemo(
    () => visibleTasks.slice((currentPage - 1) * PAGE_SIZE, currentPage * PAGE_SIZE),
    [visibleTasks, currentPage],
  )
  const rangeStart = visibleTasks.length === 0 ? 0 : (currentPage - 1) * PAGE_SIZE + 1
  const rangeEnd = Math.min(currentPage * PAGE_SIZE, visibleTasks.length)

  const selectedTasks = useMemo(
    () => tasks.filter((task) => selectedTaskIds.has(task.id)),
    [tasks, selectedTaskIds],
  )
  const selectedRecordingIds = useMemo(
    () => selectedTasks.filter((task) => normalizeStatus(task.status) === 'Recording').map((task) => task.id),
    [selectedTasks],
  )
  const selectedDeletableIds = useMemo(
    () => selectedTasks.filter((task) => normalizeStatus(task.status) !== 'Recording').map((task) => task.id),
    [selectedTasks],
  )
  const pageTaskIds = useMemo(() => pagedTasks.map((task) => task.id), [pagedTasks])
  const allPageSelected = pageTaskIds.length > 0 && pageTaskIds.every((id) => selectedTaskIds.has(id))
  const somePageSelected = pageTaskIds.some((id) => selectedTaskIds.has(id)) && !allPageSelected
  const selectedCount = selectedTasks.length

  const toggleTaskSelection = useCallback((id: number) => {
    setSelectedTaskIds((previous) => {
      const next = new Set(previous)
      if (next.has(id)) {
        next.delete(id)
      } else {
        next.add(id)
      }
      return next
    })
  }, [])

  const togglePageSelection = useCallback(() => {
    setSelectedTaskIds((previous) => {
      const next = new Set(previous)
      if (allPageSelected) {
        pageTaskIds.forEach((id) => next.delete(id))
      } else {
        pageTaskIds.forEach((id) => next.add(id))
      }
      return next
    })
  }, [allPageSelected, pageTaskIds])

  const clearSelection = useCallback(() => {
    setSelectedTaskIds(new Set())
  }, [])

  useEffect(() => {
    setSelectedTaskIds((previous) => {
      const existing = new Set(tasks.map((task) => task.id))
      let changed = false
      const next = new Set<number>()
      for (const id of previous) {
        if (existing.has(id)) {
          next.add(id)
        } else {
          changed = true
        }
      }
      return changed ? next : previous
    })
  }, [tasks])

  useEffect(() => {
    setPage((current) => Math.min(current, totalPages))
  }, [totalPages])

  useEffect(() => {
    if (!openCombobox) {
      return
    }

    let list: HTMLDivElement | null = null
    const handleWheel = (event: WheelEvent) => {
      if (!list || event.deltaY === 0) {
        return
      }

      const maxScrollTop = list.scrollHeight - list.clientHeight
      const nextScrollTop = Math.max(0, Math.min(maxScrollTop, list.scrollTop + event.deltaY))
      if (nextScrollTop === list.scrollTop) {
        return
      }

      event.preventDefault()
      list.scrollTop = nextScrollTop
    }

    const timer = window.setTimeout(() => {
      list = comboboxListRef.current
      list?.addEventListener("wheel", handleWheel, { passive: false })
    }, 0)

    return () => {
      window.clearTimeout(timer)
      list?.removeEventListener("wheel", handleWheel)
    }
  }, [openCombobox])

  const [deleteConfirmOpen, setDeleteConfirmOpen] = useState<{ isOpen: boolean; taskId: number | null }>({ isOpen: false, taskId: null })
  const [stopConfirmOpen, setStopConfirmOpen] = useState<{ isOpen: boolean; taskId: number | null }>({ isOpen: false, taskId: null })
  const [cancelConfirmOpen, setCancelConfirmOpen] = useState<{ isOpen: boolean; taskId: number | null }>({ isOpen: false, taskId: null })
  const [retryConfirmOpen, setRetryConfirmOpen] = useState<{ isOpen: boolean; taskId: number | null }>({ isOpen: false, taskId: null })
  const [endTimeEditOpen, setEndTimeEditOpen] = useState<{ isOpen: boolean; taskId: number | null }>({ isOpen: false, taskId: null })
  const [editingEndTime, setEditingEndTime] = useState<Date | undefined>(undefined)
  const [savingEndTime, setSavingEndTime] = useState(false)

  const form = useForm<TaskFormValues>({
    resolver: zodResolver(taskSchema),
    defaultValues: {
      taskName: '',
    },
  })

  const openModal = useCallback(() => {
    const stored = window.localStorage.getItem('rtsp_task_draft')
    const draft = stored ? JSON.parse(stored) : null
    
    // Default start time: now, end time: now + 30m
    const defaultStart = new Date()
    const defaultEnd = new Date(defaultStart.getTime() + 30 * 60 * 1000)

    let startTime = defaultStart
    let endTime = defaultEnd

    if (draft?.startTime) {
        const d = new Date(draft.startTime)
        if (!isNaN(d.getTime())) startTime = d
    }
    if (draft?.endTime) {
        const d = new Date(draft.endTime)
        if (!isNaN(d.getTime()) && d > startTime) endTime = d
    }

    const preferredChannelId = draft?.channelId && channels.some((channel) => channel.id === draft.channelId) 
        ? String(draft.channelId) 
        : String(channels[0]?.id || '')

    form.reset({
      channelId: preferredChannelId,
      startTime,
      endTime,
      taskName: draft?.taskName ?? '',
    })
    setCreateOpen(true)
  }, [channels, form])

  const onSubmit = async (values: TaskFormValues) => {
    if (appConfig && activeCount >= appConfig.maxRecordingTasks) {
      toast.info('当前并发已达上限，任务将进入等待队列')
    }

    const channelId = parseInt(values.channelId)
    
    // Save draft
    window.localStorage.setItem(
      'rtsp_task_draft',
      JSON.stringify({
        channelId,
        startTime: values.startTime.toISOString(),
        endTime: values.endTime.toISOString(),
        taskName: values.taskName?.trim() || '',
      }),
    )

    try {
        await createTask({
            channelId,
            startTime: values.startTime.toISOString(), // API expects ISO string usually, assuming format compatibility
            endTime: values.endTime.toISOString(),
            taskName: values.taskName?.trim() || null,
        })
        setCreateOpen(false)
    } catch {
        // Error handled in AppContext
    }
  }

  const onViewInfo = useCallback(
    async (id: number, title: string) => {
      setInfoLoading(true)
      setInfoModal({ title, content: '' }) // Open modal first
      try {
        const info = await getTaskMediaInfo(id)
        setInfoModal({ title, content: info })
      } catch {
        setInfoModal(null)
      } finally {
        setInfoLoading(false)
      }
    },
    [getTaskMediaInfo],
  )

  const openEndTimeEditor = useCallback((id: number, endTime: string) => {
    setEditingEndTime(new Date(endTime))
    setEndTimeEditOpen({ isOpen: true, taskId: id })
  }, [])

  const onSaveEndTime = async () => {
    if (!endTimeEditOpen.taskId || !editingEndTime || savingEndTime) {
      return
    }

    if (editingEndTime.getTime() <= Date.now()) {
      toast.error('结束时间必须晚于当前时间')
      return
    }

    setSavingEndTime(true)
    try {
      const saved = await updateTaskEndTime(endTimeEditOpen.taskId, editingEndTime.toISOString())
      if (saved) {
        setEndTimeEditOpen({ isOpen: false, taskId: null })
      }
    } finally {
      setSavingEndTime(false)
    }
  }

  const addEndTimeMinutes = (minutes: number) => {
    setEditingEndTime((current) => {
      const base = current && current.getTime() > Date.now() ? current : new Date()
      return new Date(base.getTime() + minutes * 60 * 1000)
    })
  }

  const onBulkStop = async () => {
    const ids = [...selectedRecordingIds]
    setBulkStopConfirmOpen(false)
    if (ids.length === 0) {
      return
    }

    await stopTasks(ids)
    setSelectedTaskIds(new Set())
  }

  const onBulkDelete = async () => {
    const ids = [...selectedDeletableIds]
    setBulkDeleteConfirmOpen(false)
    if (ids.length === 0) {
      return
    }

    await deleteTasks(ids)
    setSelectedTaskIds(new Set())
  }

  const renderMobileActions = (task: RecordingTaskDto, display: TaskDisplay) => {
    const { normalizedStatus, showInfoActions, canEditEndTime, canRetry } = display
    return (
      <div className="flex shrink-0 items-center gap-1">
        {canEditEndTime && (
          <Button
            variant="ghost"
            size="icon"
            className="h-8 w-8"
            onClick={() => openEndTimeEditor(task.id, task.endTime)}
            aria-label="修改结束时间"
            title="修改结束时间"
          >
            <CalendarClock className="h-4 w-4" />
          </Button>
        )}
        {normalizedStatus === 'Recording' && (
          <Button
            variant="ghost"
            size="icon"
            className="h-8 w-8 text-destructive hover:bg-destructive/10 hover:text-destructive"
            onClick={() => setStopConfirmOpen({ isOpen: true, taskId: task.id })}
            aria-label="停止任务"
            title="停止任务"
          >
            <StopCircle className="h-4 w-4" />
          </Button>
        )}
        {normalizedStatus === 'Pending' && (
          <Button
            variant="ghost"
            size="icon"
            className="h-8 w-8 text-destructive hover:bg-destructive/10 hover:text-destructive"
            onClick={() => setCancelConfirmOpen({ isOpen: true, taskId: task.id })}
            aria-label="取消任务"
            title="取消任务"
          >
            <Ban className="h-4 w-4" />
          </Button>
        )}
        {showInfoActions && (
          <>
            {canRetry && (
              <Button
                variant="ghost"
                size="icon"
                className="h-8 w-8"
                onClick={() => setRetryConfirmOpen({ isOpen: true, taskId: task.id })}
                aria-label="重试任务"
                title="重试任务"
              >
                <RefreshCw className="h-4 w-4" />
              </Button>
            )}
            <Button
              variant="ghost"
              size="icon"
              className="h-8 w-8"
              onClick={() => onViewInfo(task.id, task.displayName || `Task ${task.id}`)}
              aria-label="查看详情"
              title="查看详情"
            >
              <Info className="h-4 w-4" />
            </Button>
            <Button variant="ghost" size="icon" className="h-8 w-8" asChild>
              <a href={getTaskDownloadUrl(task)} target="_blank" rel="noreferrer" aria-label="下载文件" title="下载文件">
                <Download className="h-4 w-4" />
              </a>
            </Button>
            <Button
              variant="ghost"
              size="icon"
              className="h-8 w-8 text-destructive hover:bg-destructive/10 hover:text-destructive"
              onClick={() => setDeleteConfirmOpen({ isOpen: true, taskId: task.id })}
              aria-label="删除记录"
              title="删除记录"
            >
              <Trash2 className="h-4 w-4" />
            </Button>
          </>
        )}
      </div>
    )
  }

  return (
    <div className="space-y-6">
      <div className="flex flex-col sm:flex-row justify-between items-start sm:items-center gap-4">
          <Button onClick={openModal} className="w-full sm:w-auto">
            <Plus className="mr-2 h-4 w-4" />
            新建录制任务
          </Button>
          
          <div className="flex items-center gap-2 text-sm bg-card p-2 px-4 rounded-full border shadow-sm">
            <span className="text-muted-foreground">实时码率</span>
            <span className={cn("font-mono font-bold", totalBitrateKbps > 0 ? "text-green-500" : "text-muted-foreground")}>
                {totalBitrateKbps > 0 ? `${totalBitrateKbps.toFixed(1)} Kb/s` : '无录制任务'}
            </span>
          </div>
      </div>

      <Card>
        <Tabs
          value={activeTab}
          onValueChange={(value) => {
            setActiveTab(value as TaskTab)
            setPage(1)
            setSelectedTaskIds(new Set())
          }}
        >
          <CardHeader className="flex flex-row flex-wrap items-center gap-3 space-y-0 border-b pb-3">
            <CardTitle className="text-lg font-medium">任务列表</CardTitle>
            <span className="text-xs text-muted-foreground sm:order-last sm:ml-auto">共 {tasks.length} 条</span>
            <TabsList className="grid h-auto w-full grid-cols-2 gap-1 sm:order-none sm:inline-flex sm:w-auto">
              {(Object.keys(taskTabs) as TaskTab[]).map((tab) => (
                <TabsTrigger
                  key={tab}
                  value={tab}
                  className="w-full justify-center gap-2 px-2 py-1.5 text-xs sm:w-auto sm:px-3 sm:text-sm"
                >
                  {taskTabs[tab].label}
                  <span
                    className={cn(
                      "min-w-[20px] rounded-full px-1.5 py-0.5 text-center font-mono text-[10px] tabular-nums",
                      activeTab === tab
                        ? "bg-primary/10 text-primary"
                        : "bg-background/70 text-muted-foreground",
                    )}
                  >
                    {taskCounts[tab]}
                  </span>
                </TabsTrigger>
              ))}
            </TabsList>
          </CardHeader>
          <CardContent className="p-0">
            <TabsContent value={activeTab} className="m-0">
              <div className={cn(
                "flex flex-wrap items-center justify-between gap-2 border-b bg-muted/20 px-4 py-2",
                selectedCount === 0 && "md:hidden",
              )}>
                <div className="flex items-center gap-3">
                  <label className="flex items-center gap-2 text-xs text-muted-foreground md:hidden">
                    <input
                      type="checkbox"
                      className="h-4 w-4 accent-primary"
                      checked={allPageSelected}
                      ref={(input) => {
                        if (input) input.indeterminate = somePageSelected
                      }}
                      onChange={togglePageSelection}
                      aria-label="全选当前页任务"
                    />
                    全选本页
                  </label>
                  <span className="text-xs text-muted-foreground">
                    已选 <span className="font-medium text-foreground">{selectedCount}</span> 项
                  </span>
                </div>
                {selectedCount > 0 ? (
                  <div className="flex flex-wrap items-center gap-1.5">
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      className="h-8"
                      onClick={() => setBulkStopConfirmOpen(true)}
                      disabled={selectedRecordingIds.length === 0}
                      title="仅停止正在录制的任务"
                    >
                      <StopCircle className="mr-1 h-3.5 w-3.5" />
                      停止录制
                    </Button>
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      className="h-8"
                      onClick={() => setBulkDeleteConfirmOpen(true)}
                      disabled={selectedDeletableIds.length === 0}
                      title="删除任务记录；等待中的任务会被取消，录像文件不会删除"
                    >
                      <Trash2 className="mr-1 h-3.5 w-3.5" />
                      删除记录
                    </Button>
                    <Button type="button" variant="ghost" size="sm" className="h-8" onClick={clearSelection}>
                      取消选择
                    </Button>
                  </div>
                ) : (
                  <span className="text-[11px] text-muted-foreground">勾选任务后可批量操作</span>
                )}
              </div>
              <div className="hidden rounded-md md:block">
                <Table className="w-full min-w-[1120px] table-fixed">
                <TableHeader>
                    <TableRow>
                        <TableHead className="w-[3%]">
                            <input
                                type="checkbox"
                                className="h-4 w-4 accent-primary"
                                checked={allPageSelected}
                                ref={(input) => {
                                    if (input) input.indeterminate = somePageSelected
                                }}
                                onChange={togglePageSelection}
                                aria-label="全选当前页任务"
                            />
                        </TableHead>
                        <TableHead className="w-[4%]">ID</TableHead>
                        <TableHead className="w-[7%]">状态</TableHead>
                        <TableHead className="w-[11%]">任务名称</TableHead>
                        <TableHead className="w-[12%]">频道</TableHead>
                        <TableHead className="w-[12%]">时间</TableHead>
                        <TableHead className="w-[13%]">时长 / 进度</TableHead>
                        <TableHead className="w-[10%]">码率 / 大小</TableHead>
                        <TableHead className="w-[14%]">TS 检测</TableHead>
                        <TableHead className="w-[14%] text-right">操作</TableHead>
                    </TableRow>
                </TableHeader>
                <TableBody>
                    {pagedTasks.length === 0 ? (
                        <TableRow>
                            <TableCell colSpan={10} className="h-24 text-center">
                                {taskTabs[activeTab].empty}
                            </TableCell>
                        </TableRow>
                    ) : (
                        pagedTasks.map((task) => {
                            const {
                                normalizedStatus,
                                start,
                                startedAt,
                                displayStart,
                                plannedDuration,
                                recordedDuration,
                                progress,
                                bitrateDisplay,
                                showInfoActions,
                                canEditEndTime,
                                canRetry,
                            } = getTaskDisplay(task, now.getTime())
                            const channel = channels.find((item) => item.id === task.channelId)
                            const selected = selectedTaskIds.has(task.id)

                            return (
                                <TableRow key={task.id} className={selected ? 'bg-primary/5' : undefined}>
                                    <TableCell>
                                        <input
                                            type="checkbox"
                                            className="h-4 w-4 accent-primary"
                                            checked={selected}
                                            onChange={() => toggleTaskSelection(task.id)}
                                            aria-label={`选择任务 ${task.displayName || task.id}`}
                                        />
                                    </TableCell>
                                    <TableCell className="font-mono text-xs">{task.id}</TableCell>
                                    <TableCell>
                                        <TaskStatusBadge status={normalizedStatus} />
                                    </TableCell>
                                    <TableCell>
                                        <div className="font-medium truncate max-w-full" title={task.displayName || '--'}>
                                            {task.displayName || '--'}
                                        </div>
                                    </TableCell>
                                    <TableCell>
                                        <div className="flex min-w-0 flex-col gap-0.5 text-sm text-muted-foreground" title={`${task.channelId} - ${task.channelName}`}>
                                            <span className="truncate">{task.channelId} - {task.channelName}</span>
                                            {(channel || task.isLive) && (
                                                <div className="flex items-center gap-1">
                                                    {channel && <ChannelProtocolBadge url={channel.url} />}
                                                    {task.isLive && (
                                                        <Badge variant="outline" className="shrink-0 px-1 py-0 text-[10px]">直播</Badge>
                                                    )}
                                                </div>
                                            )}
                                        </div>
                                    </TableCell>
                                    <TableCell>
                                        <div
                                            className="flex flex-col text-xs text-muted-foreground"
                                            title={task.isLive && Number.isFinite(startedAt) && displayStart !== start
                                                ? `实际开始；预约开始：${formatDateTime(new Date(start))}`
                                                : undefined}
                                        >
                                            <span className="whitespace-nowrap">{formatDateTime(new Date(displayStart))}</span>
                                            <span className="whitespace-nowrap">{formatDateTime(new Date(task.endTime))}</span>
                                        </div>
                                    </TableCell>
                                    <TableCell>
                                        <TaskProgress
                                            recordedDuration={recordedDuration}
                                            plannedDuration={plannedDuration}
                                            progress={progress}
                                            status={normalizedStatus}
                                        />
                                    </TableCell>
                                    <TableCell>
                                        <div className="flex flex-col text-xs w-full">
                                            <span className="font-mono whitespace-nowrap">{bitrateDisplay}</span>
                                            <span className="text-muted-foreground whitespace-nowrap">{formatBytes(task.bytesWritten)}</span>
                                        </div>
                                    </TableCell>
                                    <TableCell>
                                        <TooltipProvider>
                                            <Tooltip>
                                                <TooltipTrigger asChild>
                                                    <div
                                                        className="flex w-full cursor-help flex-col gap-1 text-xs"
                                                        tabIndex={0}
                                                        aria-label="TS 检测详情"
                                                    >
                                                        <div className="flex min-w-0 items-center gap-1.5">
                                                            <Badge
                                                                variant="outline"
                                                                className={cn('shrink-0 text-[10px]', tsVerdictClass(task.tsCheck?.verdict))}
                                                            >
                                                                {task.tsCheck
                                                                    ? tsVerdictLabel[task.tsCheck.verdict] ?? '未知'
                                                                    : normalizedStatus === 'Recording'
                                                                        ? '检测中'
                                                                        : '--'}
                                                            </Badge>
                                                            <span className="min-w-0 flex-1 truncate font-mono text-[10px] text-muted-foreground">
                                                                {task.tsCheck
                                                                    ? tsCodecText(task.tsCheck)
                                                                    : normalizedStatus === 'Recording'
                                                                        ? '等待数据'
                                                                        : '--'}
                                                            </span>
                                                            {task.tsCheck && (
                                                                <Info className="h-3 w-3 shrink-0 text-muted-foreground/60" />
                                                            )}
                                                        </div>
                                                        {task.tsCheck && (
                                                            <span className="text-[10px] text-muted-foreground">
                                                                错误 {task.tsCheck.errorCount} · 警告 {task.tsCheck.warningCount}
                                                            </span>
                                                        )}
                                                    </div>
                                                </TooltipTrigger>
                                                {task.tsCheck && (
                                                    <TooltipContent side="top" align="start" className="max-w-[280px] space-y-1 text-xs">
                                                        <div className="font-medium">
                                                            TS 检测：{tsVerdictLabel[task.tsCheck.verdict] ?? '未知'}
                                                        </div>
                                                        <div>错误 {task.tsCheck.errorCount} · 警告 {task.tsCheck.warningCount}</div>
                                                        <div>视频：{tsTrackText(task.tsCheck.videoTracks, task.tsCheck.video)} · 音频：{tsTrackText(task.tsCheck.audioTracks, task.tsCheck.audio)}</div>
                                                        <div>码率 {task.tsCheck.bitrateKbps.toFixed(0)} Kb/s</div>
                                                        {task.tsCheck.lastEvent && <div>最近事件：{task.tsCheck.lastEvent}</div>}
                                                    </TooltipContent>
                                                )}
                                            </Tooltip>
                                        </TooltipProvider>
                                    </TableCell>
                                    <TableCell className="text-right">
                                        <div className="flex justify-end gap-1">
                                            {canEditEndTime && (
                                                <TooltipProvider>
                                                    <Tooltip>
                                                        <TooltipTrigger asChild>
                                                            <Button
                                                                variant="ghost"
                                                                size="icon"
                                                                className="h-8 w-8"
                                                                onClick={() => openEndTimeEditor(task.id, task.endTime)}
                                                            >
                                                                <CalendarClock className="h-4 w-4" />
                                                                <span className="sr-only">修改结束时间</span>
                                                            </Button>
                                                        </TooltipTrigger>
                                                        <TooltipContent>
                                                            <p>修改结束时间</p>
                                                        </TooltipContent>
                                                    </Tooltip>
                                                </TooltipProvider>
                                            )}
                                            {normalizedStatus === 'Recording' && (
                                                <TooltipProvider>
                                                    <Tooltip>
                                                        <TooltipTrigger asChild>
                                                            <Button
                                                                variant="ghost"
                                                                size="icon"
                                                                className="h-8 w-8 text-destructive hover:text-destructive hover:bg-destructive/10"
                                                                onClick={() => setStopConfirmOpen({ isOpen: true, taskId: task.id })}
                                                            >
                                                                <StopCircle className="h-4 w-4" />
                                                                <span className="sr-only">Stop</span>
                                                            </Button>
                                                        </TooltipTrigger>
                                                        <TooltipContent>
                                                            <p>停止任务</p>
                                                        </TooltipContent>
                                                    </Tooltip>
                                                </TooltipProvider>
                                            )}
                                            {normalizedStatus === 'Pending' && (
                                                <TooltipProvider>
                                                    <Tooltip>
                                                        <TooltipTrigger asChild>
                                                            <Button
                                                                variant="ghost"
                                                                size="icon"
                                                                className="h-8 w-8 text-destructive hover:text-destructive hover:bg-destructive/10"
                                                                onClick={() => setCancelConfirmOpen({ isOpen: true, taskId: task.id })}
                                                            >
                                                                <Ban className="h-4 w-4" />
                                                                <span className="sr-only">Cancel</span>
                                                            </Button>
                                                        </TooltipTrigger>
                                                        <TooltipContent>
                                                            <p>取消任务</p>
                                                        </TooltipContent>
                                                    </Tooltip>
                                                </TooltipProvider>
                                            )}
                                            {showInfoActions && (
                                                <>
                                                    {canRetry && (
                                                        <TooltipProvider>
                                                            <Tooltip>
                                                                <TooltipTrigger asChild>
                                                                    <Button variant="ghost" size="icon" className="h-8 w-8" onClick={() => setRetryConfirmOpen({ isOpen: true, taskId: task.id })}>
                                                                        <RefreshCw className="h-4 w-4" />
                                                                        <span className="sr-only">Retry</span>
                                                                    </Button>
                                                                </TooltipTrigger>
                                                                <TooltipContent>
                                                                    <p>重试任务</p>
                                                                </TooltipContent>
                                                            </Tooltip>
                                                        </TooltipProvider>
                                                    )}
                                                    <TooltipProvider>
                                                        <Tooltip>
                                                            <TooltipTrigger asChild>
                                                                <Button variant="ghost" size="icon" className="h-8 w-8" onClick={() => onViewInfo(task.id, task.displayName || `Task ${task.id}`)}>
                                                                    <Info className="h-4 w-4" />
                                                                    <span className="sr-only">Info</span>
                                                                </Button>
                                                            </TooltipTrigger>
                                                            <TooltipContent>
                                                                <p>查看详情</p>
                                                            </TooltipContent>
                                                        </Tooltip>
                                                    </TooltipProvider>
                                                    
                                                    <TooltipProvider>
                                                        <Tooltip>
                                                            <TooltipTrigger asChild>
                                                                <Button variant="ghost" size="icon" className="h-8 w-8" asChild>
                                                                    <a href={getTaskDownloadUrl(task)} target="_blank" rel="noreferrer">
                                                                        <Download className="h-4 w-4" />
                                                                        <span className="sr-only">Download</span>
                                                                    </a>
                                                                </Button>
                                                            </TooltipTrigger>
                                                            <TooltipContent>
                                                                <p>下载文件</p>
                                                            </TooltipContent>
                                                        </Tooltip>
                                                    </TooltipProvider>

                                                    <TooltipProvider>
                                                        <Tooltip>
                                                            <TooltipTrigger asChild>
                                                                <Button 
                                                                    variant="ghost" 
                                                                    size="icon" 
                                                                    className="h-8 w-8 text-destructive hover:text-destructive hover:bg-destructive/10"
                                                                    onClick={() => setDeleteConfirmOpen({ isOpen: true, taskId: task.id })}
                                                                >
                                                                    <Trash2 className="h-4 w-4" />
                                                                    <span className="sr-only">Delete</span>
                                                                </Button>
                                                            </TooltipTrigger>
                                                            <TooltipContent>
                                                                <p>删除记录</p>
                                                            </TooltipContent>
                                                        </Tooltip>
                                                    </TooltipProvider>
                                                </>
                                            )}
                                        </div>
                                    </TableCell>
                                </TableRow>
                            )
                        })
                    )}
                </TableBody>
            </Table>
          </div>
          <div className="md:hidden">
            {pagedTasks.length === 0 ? (
              <div className="flex h-28 items-center justify-center px-4 text-center text-sm text-muted-foreground">
                {taskTabs[activeTab].empty}
              </div>
            ) : (
              <div className="divide-y">
                {pagedTasks.map((task) => {
                  const display = getTaskDisplay(task, now.getTime())
                  const normalizedStatus = display.normalizedStatus
                  const channel = channels.find((item) => item.id === task.channelId)
                  const selected = selectedTaskIds.has(task.id)
                  return (
                    <div key={task.id} className={cn("space-y-3 p-4", selected && "bg-primary/5")}>
                      <div className="flex items-start justify-between gap-3">
                        <div className="flex min-w-0 items-start gap-2.5">
                          <input
                            type="checkbox"
                            className="mt-1 h-4 w-4 shrink-0 accent-primary"
                            checked={selected}
                            onChange={() => toggleTaskSelection(task.id)}
                            aria-label={`选择任务 ${task.displayName || task.id}`}
                          />
                          <div className="min-w-0 space-y-1.5">
                            <div className="flex min-w-0 items-center gap-2">
                              <TaskStatusBadge status={normalizedStatus} />
                              <span className="truncate font-medium" title={task.displayName || '--'}>
                                {task.displayName || '--'}
                              </span>
                            </div>
                            <div className="flex min-w-0 items-center gap-1 text-xs text-muted-foreground">
                              <span className="truncate">{task.channelId} - {task.channelName}</span>
                              {channel && <ChannelProtocolBadge url={channel.url} />}
                              {task.isLive && (
                                <Badge variant="outline" className="shrink-0 px-1 py-0 text-[10px]">直播</Badge>
                              )}
                            </div>
                          </div>
                        </div>
                        <span className="shrink-0 font-mono text-xs text-muted-foreground">#{task.id}</span>
                      </div>

                      <div className="grid grid-cols-2 gap-3 text-xs">
                        <div className="space-y-1">
                          <div className="text-muted-foreground">时间</div>
                          <div className="whitespace-nowrap">{formatDateTime(new Date(display.displayStart))}</div>
                          <div className="whitespace-nowrap text-muted-foreground">{formatDateTime(new Date(task.endTime))}</div>
                        </div>
                        <div className="space-y-1">
                          <div className="text-muted-foreground">码率 / 大小</div>
                          <div className="whitespace-nowrap font-mono">{display.bitrateDisplay}</div>
                          <div className="whitespace-nowrap text-muted-foreground">{formatBytes(task.bytesWritten)}</div>
                        </div>
                      </div>

                      <TaskProgress
                        recordedDuration={display.recordedDuration}
                        plannedDuration={display.plannedDuration}
                        progress={display.progress}
                        status={normalizedStatus}
                      />

                      <div className="flex items-center justify-between gap-2">
                        <div className="min-w-0 space-y-1">
                          <div className="flex min-w-0 items-center gap-1.5">
                            <Badge
                              variant="outline"
                              className={cn('shrink-0 px-1.5 py-0 text-[10px]', tsVerdictClass(task.tsCheck?.verdict))}
                            >
                              {task.tsCheck
                                ? tsVerdictLabel[task.tsCheck.verdict] ?? '未知'
                                : normalizedStatus === 'Recording'
                                  ? '检测中'
                                  : '--'}
                            </Badge>
                            <span className="truncate font-mono text-[10px] text-muted-foreground">
                              {task.tsCheck
                                ? tsCodecText(task.tsCheck)
                                : normalizedStatus === 'Recording'
                                  ? '等待数据'
                                  : '--'}
                            </span>
                          </div>
                          {task.tsCheck && (
                            <div className="text-[10px] text-muted-foreground">
                              错误 {task.tsCheck.errorCount} · 警告 {task.tsCheck.warningCount}
                            </div>
                          )}
                          {task.tsCheck?.lastEvent && (
                            <div className="truncate text-[10px] text-muted-foreground" title={task.tsCheck.lastEvent}>
                              最近事件：{task.tsCheck.lastEvent}
                            </div>
                          )}
                        </div>
                        {renderMobileActions(task, display)}
                      </div>
                    </div>
                  )
                })}
              </div>
            )}
          </div>
          <div className="flex flex-wrap items-center justify-between gap-3 border-t px-4 py-3 text-sm">
            <span className="text-muted-foreground">
              <span className="hidden sm:inline">
                {visibleTasks.length === 0
                  ? '共 0 条'
                  : `第 ${rangeStart}-${rangeEnd} 条，共 ${visibleTasks.length} 条`}
              </span>
              <span className="sm:hidden">共 {visibleTasks.length} 条</span>
            </span>
            <div className="ml-auto flex flex-wrap items-center gap-2">
              <span className="hidden whitespace-nowrap text-xs text-muted-foreground sm:inline">每页 {PAGE_SIZE} 条</span>
              <Button
                type="button"
                variant="outline"
                size="sm"
                className="h-8"
                onClick={() => setPage(Math.max(1, currentPage - 1))}
                disabled={currentPage <= 1}
              >
                <ChevronLeft className="mr-1 h-4 w-4" />
                上一页
              </Button>
              <span className="min-w-[76px] text-center text-xs text-muted-foreground">
                第 {currentPage} / {totalPages} 页
              </span>
              <Button
                type="button"
                variant="outline"
                size="sm"
                className="h-8"
                onClick={() => setPage(Math.min(totalPages, currentPage + 1))}
                disabled={currentPage >= totalPages}
              >
                下一页
                <ChevronRight className="ml-1 h-4 w-4" />
              </Button>
            </div>
          </div>
          </TabsContent>
          </CardContent>
        </Tabs>
      </Card>

      <AlertDialog open={deleteConfirmOpen.isOpen} onOpenChange={(open) => !open && setDeleteConfirmOpen({ isOpen: false, taskId: null })}>
        <AlertDialogContent>
            <AlertDialogHeader>
                <AlertDialogTitle>确认删除记录？</AlertDialogTitle>
                <AlertDialogDescription>
                    此操作无法撤销。
                </AlertDialogDescription>
            </AlertDialogHeader>
            <AlertDialogFooter>
                <AlertDialogCancel>取消</AlertDialogCancel>
                <AlertDialogAction 
                    onClick={() => {
                        if (deleteConfirmOpen.taskId) deleteTask(deleteConfirmOpen.taskId)
                        setDeleteConfirmOpen({ isOpen: false, taskId: null })
                    }} 
                    className="bg-destructive hover:bg-destructive/90"
                >
                    删除
                </AlertDialogAction>
            </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>

      <AlertDialog open={stopConfirmOpen.isOpen} onOpenChange={(open) => !open && setStopConfirmOpen({ isOpen: false, taskId: null })}>
        <AlertDialogContent>
            <AlertDialogHeader>
                <AlertDialogTitle>确认停止录制？</AlertDialogTitle>
                <AlertDialogDescription>
                    此操作将立即停止当前录制任务。
                </AlertDialogDescription>
            </AlertDialogHeader>
            <AlertDialogFooter>
                <AlertDialogCancel>取消</AlertDialogCancel>
                <AlertDialogAction
                    onClick={() => {
                        if (stopConfirmOpen.taskId) stopTask(stopConfirmOpen.taskId)
                        setStopConfirmOpen({ isOpen: false, taskId: null })
                    }}
                    className="bg-destructive hover:bg-destructive/90"
                >
                    停止
                </AlertDialogAction>
            </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>

      <AlertDialog open={bulkStopConfirmOpen} onOpenChange={setBulkStopConfirmOpen}>
        <AlertDialogContent>
            <AlertDialogHeader>
                <AlertDialogTitle>确认批量停止录制？</AlertDialogTitle>
                <AlertDialogDescription>
                    将向 {selectedRecordingIds.length} 个正在录制的任务发送停止请求；选中的其他状态任务不会受影响。
                </AlertDialogDescription>
            </AlertDialogHeader>
            <AlertDialogFooter>
                <AlertDialogCancel>取消</AlertDialogCancel>
                <AlertDialogAction
                    onClick={() => void onBulkStop()}
                    className="bg-destructive hover:bg-destructive/90"
                >
                    停止录制
                </AlertDialogAction>
            </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>

      <AlertDialog open={bulkDeleteConfirmOpen} onOpenChange={setBulkDeleteConfirmOpen}>
        <AlertDialogContent>
            <AlertDialogHeader>
                <AlertDialogTitle>确认批量删除记录？</AlertDialogTitle>
                <AlertDialogDescription>
                    将删除 {selectedDeletableIds.length} 条任务记录；等待中的任务会被取消，录像文件不会被删除。正在录制的任务会被跳过。
                </AlertDialogDescription>
            </AlertDialogHeader>
            <AlertDialogFooter>
                <AlertDialogCancel>取消</AlertDialogCancel>
                <AlertDialogAction
                    onClick={() => void onBulkDelete()}
                    className="bg-destructive hover:bg-destructive/90"
                >
                    删除记录
                </AlertDialogAction>
            </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>

      <AlertDialog open={cancelConfirmOpen.isOpen} onOpenChange={(open) => !open && setCancelConfirmOpen({ isOpen: false, taskId: null })}>
        <AlertDialogContent>
            <AlertDialogHeader>
                <AlertDialogTitle>确认取消任务？</AlertDialogTitle>
                <AlertDialogDescription>
                    此操作将取消排队中的任务。
                </AlertDialogDescription>
            </AlertDialogHeader>
            <AlertDialogFooter>
                <AlertDialogCancel>取消</AlertDialogCancel>
                <AlertDialogAction
                    onClick={() => {
                        if (cancelConfirmOpen.taskId) deleteTask(cancelConfirmOpen.taskId)
                        setCancelConfirmOpen({ isOpen: false, taskId: null })
                    }}
                    className="bg-destructive hover:bg-destructive/90"
                >
                    取消任务
                </AlertDialogAction>
            </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>

      <AlertDialog open={retryConfirmOpen.isOpen} onOpenChange={(open) => !open && setRetryConfirmOpen({ isOpen: false, taskId: null })}>
        <AlertDialogContent>
            <AlertDialogHeader>
                <AlertDialogTitle>确认重试任务？</AlertDialogTitle>
                <AlertDialogDescription>
                    此操作将删除当前任务记录，并以相同参数创建新任务。
                </AlertDialogDescription>
            </AlertDialogHeader>
            <AlertDialogFooter>
                <AlertDialogCancel>取消</AlertDialogCancel>
                <AlertDialogAction
                    onClick={async () => {
                        if (retryConfirmOpen.taskId) {
                            const targetTask = tasks.find((task) => task.id === retryConfirmOpen.taskId)
                            if (targetTask) {
                                await deleteTask(targetTask.id)
                                await createTask({
                                    channelId: targetTask.channelId,
                                    startTime: new Date(targetTask.startTime).toISOString(),
                                    endTime: new Date(targetTask.endTime).toISOString(),
                                    taskName: targetTask.displayName?.trim() || null,
                                })
                            }
                        }
                        setRetryConfirmOpen({ isOpen: false, taskId: null })
                    }}
                    className="bg-destructive hover:bg-destructive/90"
                >
                    继续重试
                </AlertDialogAction>
            </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>

      <Dialog open={createOpen} onOpenChange={setCreateOpen}>
        <DialogContent className="max-h-[90dvh] max-w-[calc(100vw-2rem)] overflow-x-hidden overflow-y-auto sm:max-w-[500px]">
            <DialogHeader>
                <DialogTitle>新建录制任务</DialogTitle>
                <DialogDescription>
                    请设置录制频道和时间段。时间为本地时区。
                </DialogDescription>
            </DialogHeader>
            <Form {...form}>
                <form onSubmit={form.handleSubmit(onSubmit)} className="space-y-4">
                    <FormField
                        control={form.control}
                        name="channelId"
                        render={({ field }) => {
                            const selectedChannel = channels.find(
                                (channel) => String(channel.id) === field.value
                            )
                            return (
                            <FormItem className="flex flex-col">
                                <FormLabel>频道</FormLabel>
                                <Popover open={openCombobox} onOpenChange={setOpenCombobox}>
                                    <PopoverTrigger asChild>
                                        <FormControl>
                                            <Button
                                                variant="outline"
                                                role="combobox"
                                                className={cn(
                                                    "w-full min-w-0 justify-between",
                                                    !field.value && "text-muted-foreground"
                                                )}
                                            >
                                                {selectedChannel ? (
                                                    <span className="flex min-w-0 items-center gap-2">
                                                        <span className="truncate">{selectedChannel.name}</span>
                                                        <ChannelProtocolBadge url={selectedChannel.url} />
                                                    </span>
                                                ) : (
                                                    <span className="truncate">选择频道...</span>
                                                )}
                                                <ChevronsUpDown className="ml-2 h-4 w-4 shrink-0 opacity-50" />
                                            </Button>
                                        </FormControl>
                                    </PopoverTrigger>
                                    <PopoverContent
                                        className="w-[var(--radix-popover-trigger-width)] p-0"
                                        align="start"
                                        sideOffset={4}
                                        collisionPadding={16}
                                    >
                                        <Command>
                                            <CommandInput placeholder="搜索频道..." />
                                            <CommandList ref={comboboxListRef}>
                                                <CommandEmpty>未找到频道</CommandEmpty>
                                                <CommandGroup>
                                                    {channels.map((channel) => (
                                                        <CommandItem
                                                            value={`${channel.id} ${channel.name}`}
                                                            key={channel.id}
                                                            className="gap-2"
                                                            onSelect={() => {
                                                                form.setValue("channelId", String(channel.id))
                                                                setOpenCombobox(false)
                                                            }}
                                                        >
                                                            <Check
                                                                className={cn(
                                                                    "mr-2 h-4 w-4 shrink-0",
                                                                    channel.id.toString() === field.value
                                                                        ? "opacity-100"
                                                                        : "opacity-0"
                                                                )}
                                                            />
                                                            <span className="truncate">{channel.id} - {channel.name}</span>
                                                            <ChannelProtocolBadge url={channel.url} className="ml-auto" />
                                                        </CommandItem>
                                                    ))}
                                                </CommandGroup>
                                            </CommandList>
                                        </Command>
                                    </PopoverContent>
                                </Popover>
                                <FormMessage />
                            </FormItem>
                            )
                        }}
                    />
                    <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                        <FormField
                            control={form.control}
                            name="startTime"
                            render={({ field }) => (
                                <FormItem className="flex flex-col">
                                    <FormLabel>开始时间</FormLabel>
                                    <DateTimePicker date={field.value} setDate={field.onChange} />
                                    <FormMessage />
                                </FormItem>
                            )}
                        />
                        <FormField
                            control={form.control}
                            name="endTime"
                            render={({ field }) => (
                                <FormItem className="flex flex-col">
                                    <FormLabel>结束时间</FormLabel>
                                    <DateTimePicker date={field.value} setDate={field.onChange} />
                                    <FormMessage />
                                </FormItem>
                            )}
                        />
                    </div>
                    <FormField
                        control={form.control}
                        name="taskName"
                        render={({ field }) => (
                            <FormItem>
                                <FormLabel>任务名称 (可选)</FormLabel>
                                <FormControl>
                                    <Input placeholder="Playback" {...field} />
                                </FormControl>
                                <FormDescription>
                                    如果不填写，将自动生成默认名称。
                                </FormDescription>
                                <FormMessage />
                            </FormItem>
                        )}
                    />
                    <DialogFooter className="gap-2 sm:gap-0">
                        <Button type="button" variant="outline" onClick={() => setCreateOpen(false)}>取消</Button>
                        <Button type="submit">提交任务</Button>
                    </DialogFooter>
                </form>
            </Form>
        </DialogContent>
      </Dialog>

      <Dialog open={endTimeEditOpen.isOpen} onOpenChange={(open) => !open && setEndTimeEditOpen({ isOpen: false, taskId: null })}>
        <DialogContent className="max-h-[90dvh] max-w-[calc(100vw-2rem)] overflow-y-auto sm:max-w-[420px]">
          <DialogHeader>
            <DialogTitle>修改结束时间</DialogTitle>
            <DialogDescription>
              直播录制中延长会继续写入当前文件，缩短会按新时间停止。
            </DialogDescription>
          </DialogHeader>
          <div className="space-y-4">
            <div className="text-sm text-muted-foreground">
              原结束时间：
              {(() => {
                const currentTask = tasks.find((item) => item.id === endTimeEditOpen.taskId)
                return currentTask ? formatDateTime(new Date(currentTask.endTime)) : '--'
              })()}
            </div>
            <div className="space-y-2">
              <div className="text-xs text-muted-foreground">快捷延长</div>
              <div className="flex flex-wrap gap-2">
                {[5, 10, 30, 60].map((minutes) => (
                  <Button
                    key={minutes}
                    type="button"
                    variant="outline"
                    size="sm"
                    disabled={savingEndTime}
                    onClick={() => addEndTimeMinutes(minutes)}
                  >
                    +{minutes < 60 ? `${minutes} 分钟` : `${minutes / 60} 小时`}
                  </Button>
                ))}
              </div>
            </div>
            <DateTimePicker date={editingEndTime} setDate={setEditingEndTime} />
            <DialogFooter className="gap-2 sm:gap-0">
              <Button type="button" variant="outline" onClick={() => setEndTimeEditOpen({ isOpen: false, taskId: null })}>取消</Button>
              <Button type="button" onClick={onSaveEndTime} disabled={savingEndTime}>
                {savingEndTime ? '保存中...' : '保存'}
              </Button>
            </DialogFooter>
          </div>
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(infoModal)} onOpenChange={(open) => !open && setInfoModal(null)}>
        <DialogContent className="max-h-[85dvh] max-w-[calc(100vw-2rem)] flex flex-col sm:max-w-3xl">
            <DialogHeader>
                <DialogTitle>{infoModal?.title}</DialogTitle>
            </DialogHeader>
            <div className="flex-1 overflow-auto overflow-x-auto bg-muted/50 p-4 rounded-md font-mono text-xs">
                {infoLoading ? (
                    <div className="flex items-center justify-center h-40">
                        <Loader2 className="h-8 w-8 animate-spin text-muted-foreground" />
                    </div>
                ) : (
                    <pre className="whitespace-pre">{infoModal?.content}</pre>
                )}
            </div>
        </DialogContent>
      </Dialog>
    </div>
  )
}

export default TasksPage
