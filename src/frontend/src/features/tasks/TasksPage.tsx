import { useCallback, useMemo, useRef, useState } from 'react'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import * as z from 'zod'
import { Loader2, Plus, Info, Download, Trash2, StopCircle, Ban, Check, ChevronsUpDown, RefreshCw } from 'lucide-react'
import { toast } from 'sonner'

import { useAppContext } from '../../app/context'
import type { RecordingStatus } from '../../types'
import { formatBytes, formatDateTime } from '../shared/format'

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
  const { tasks, channels, createTask, stopTask, deleteTask, getTaskMediaInfo, now, appConfig, totalBitrateKbps } = useAppContext()
  const [createOpen, setCreateOpen] = useState(false)
  const [infoModal, setInfoModal] = useState<{ title: string; content: string } | null>(null)
  const comboboxPortalRef = useRef<HTMLDivElement>(null)
  const [infoLoading, setInfoLoading] = useState(false)
  const [openCombobox, setOpenCombobox] = useState(false)

  const activeCount = useMemo(
    () => tasks.filter((task) => task.status === 'Recording').length,
    [tasks],
  )

  const [deleteConfirmOpen, setDeleteConfirmOpen] = useState<{ isOpen: boolean; taskId: number | null }>({ isOpen: false, taskId: null })
  const [stopConfirmOpen, setStopConfirmOpen] = useState<{ isOpen: boolean; taskId: number | null }>({ isOpen: false, taskId: null })
  const [cancelConfirmOpen, setCancelConfirmOpen] = useState<{ isOpen: boolean; taskId: number | null }>({ isOpen: false, taskId: null })
  const [retryConfirmOpen, setRetryConfirmOpen] = useState<{ isOpen: boolean; taskId: number | null }>({ isOpen: false, taskId: null })

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
    } catch (e) {
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
        <CardHeader className="pb-3">
            <CardTitle className="text-lg font-medium">任务列表</CardTitle>
        </CardHeader>
        <CardContent className="p-0">
          <div className="rounded-md border-t">
            <Table>
                <TableHeader>
                    <TableRow>
                        <TableHead className="w-[60px]">ID</TableHead>
                        <TableHead className="w-[100px]">状态</TableHead>
                        <TableHead className="min-w-[150px]">任务名称</TableHead>
                        <TableHead className="min-w-[120px]">频道</TableHead>
                        <TableHead className="w-[160px]">计划时间</TableHead>
                        <TableHead className="w-[180px]">时长 / 进度</TableHead>
                        <TableHead className="w-[140px]">码率 / 大小</TableHead>
                        <TableHead className="w-[180px] text-right">操作</TableHead>
                    </TableRow>
                </TableHeader>
                <TableBody>
                    {tasks.length === 0 ? (
                        <TableRow>
                            <TableCell colSpan={8} className="h-24 text-center">
                                暂无任务
                            </TableCell>
                        </TableRow>
                    ) : (
                        tasks.map((task) => {
                            const normalizedStatus = normalizeStatus(task.status)
                            
                            // Duration Logic
                            const start = new Date(task.startTime).getTime()
                            const end = new Date(task.endTime).getTime()
                            const nowMs = now.getTime()
                            const startedAt = task.startedAt ? new Date(task.startedAt).getTime() : Number.NaN
                            const finishedAt = task.finishedAt ? new Date(task.finishedAt).getTime() : Number.NaN
                            const plannedDuration = Number.isFinite(start) && Number.isFinite(end) ? Math.max(0, end - start) : 0
                            
                            let recordedDuration = 0
                            if (task.pcrElapsedSeconds !== undefined && task.pcrElapsedSeconds > 0) {
                                recordedDuration = task.pcrElapsedSeconds * 1000
                            } else {
                                recordedDuration = Number.isFinite(startedAt)
                                ? Number.isFinite(finishedAt)
                                    ? Math.max(0, finishedAt - startedAt)
                                    : normalizedStatus === 'Recording'
                                    ? Math.max(0, nowMs - startedAt)
                                    : 0
                                : 0
                            }

                            const progress = normalizedStatus === 'Pending' ? 0
                                : normalizedStatus === 'Completed' ? 100
                                : normalizedStatus === 'Stopped' 
                                    ? (plannedDuration > 0 ? Math.min(100, Math.max(0, (recordedDuration / plannedDuration) * 100)) : 0)
                                    : (plannedDuration > 0 ? Math.min(100, Math.max(0, (recordedDuration / plannedDuration) * 100)) : 0)

                            // Bitrate Logic
                            let bitrateDisplay = '--'
                            if (task.currentBitrateKbps !== undefined && task.currentBitrateKbps > 0) {
                                bitrateDisplay = `${task.currentBitrateKbps.toFixed(1)} Kb/s`
                            } else if (Number.isFinite(startedAt) && task.bytesWritten > 0) {
                                const endTime = Number.isFinite(finishedAt) ? finishedAt : nowMs
                                const durationSeconds = Math.max(1, (endTime - startedAt) / 1000)
                                const bitrateKbps = (task.bytesWritten * 8) / (durationSeconds * 1024)
                                bitrateDisplay = `${bitrateKbps.toFixed(1)} Kb/s`
                            }

                            const showInfoActions = ['Completed', 'Failed', 'Stopped'].includes(normalizedStatus)
                            const canRetry = normalizedStatus === 'Failed' || normalizedStatus === 'Stopped'

                            return (
                                <TableRow key={task.id}>
                                    <TableCell className="font-mono text-xs">{task.id}</TableCell>
                                    <TableCell>
                                        <Badge variant={statusVariant[normalizedStatus]} className={cn(
                                            normalizedStatus === 'Completed' && "bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-400 border-green-200 dark:border-green-800",
                                            normalizedStatus === 'Failed' && "bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-400 border-red-200 dark:border-red-800",
                                            normalizedStatus === 'Pending' && "bg-yellow-100 text-yellow-800 dark:bg-yellow-900/30 dark:text-yellow-400 border-yellow-200 dark:border-yellow-800",
                                            normalizedStatus === 'Recording' && "animate-pulse"
                                        )}>
                                            {statusLabel[normalizedStatus]}
                                        </Badge>
                                    </TableCell>
                                    <TableCell>
                                        <div className="font-medium truncate max-w-full" title={task.displayName || '--'}>
                                            {task.displayName || '--'}
                                        </div>
                                    </TableCell>
                                    <TableCell>
                                        <div className="truncate max-w-full text-sm text-muted-foreground" title={`${task.channelId} - ${task.channelName}`}>
                                            {task.channelId} - {task.channelName}
                                        </div>
                                    </TableCell>
                                    <TableCell>
                                        <div className="flex flex-col text-xs text-muted-foreground">
                                            <span>{formatDateTime(new Date(task.startTime))}</span>
                                            <span>{formatDateTime(new Date(task.endTime))}</span>
                                        </div>
                                    </TableCell>
                                    <TableCell>
                                        <div className="space-y-1 w-[180px]">
                                            <div className="text-xs font-mono flex justify-between">
                                                <span>{formatDuration(recordedDuration)}</span>
                                                <span className="text-muted-foreground">/ {formatDuration(plannedDuration)}</span>
                                            </div>
                                            <Progress value={progress} className="h-1.5" indicatorClassName={cn(
                                                normalizedStatus === 'Failed' ? 'bg-destructive' :
                                                normalizedStatus === 'Stopped' ? 'bg-yellow-500' :
                                                normalizedStatus === 'Completed' ? 'bg-green-500' :
                                                'bg-primary'
                                            )} />
                                        </div>
                                    </TableCell>
                                    <TableCell>
                                        <div className="flex flex-col text-xs w-[140px]">
                                            <span className="font-mono whitespace-nowrap">{bitrateDisplay}</span>
                                            <span className="text-muted-foreground whitespace-nowrap">{formatBytes(task.bytesWritten)}</span>
                                        </div>
                                    </TableCell>
                                    <TableCell className="text-right">
                                        <div className="flex justify-end gap-1">
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
                                                                    <a href={`/api/tasks/${task.id}/download`} target="_blank" rel="noreferrer">
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
        </CardContent>
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
        <DialogContent className="sm:max-w-[500px]">
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
                        render={({ field }) => (
                            <FormItem className="flex flex-col">
                                <FormLabel>频道</FormLabel>
                                <Popover open={openCombobox} onOpenChange={setOpenCombobox}>
                                    <PopoverTrigger asChild>
                                        <FormControl>
                                            <Button
                                                variant="outline"
                                                role="combobox"
                                                className={cn(
                                                    "w-full justify-between",
                                                    !field.value && "text-muted-foreground"
                                                )}
                                            >
                                                {field.value
                                                    ? channels.find(
                                                        (channel) => String(channel.id) === field.value
                                                    )?.name
                                                    : "选择频道..."}
                                                <ChevronsUpDown className="ml-2 h-4 w-4 shrink-0 opacity-50" />
                                            </Button>
                                        </FormControl>
                                    </PopoverTrigger>
                                    <div ref={comboboxPortalRef} />
                                    <PopoverContent container={comboboxPortalRef.current} className="w-[460px] p-0">
                                        <Command>
                                            <CommandInput placeholder="搜索频道..." />
                                            <CommandList>
                                                <CommandEmpty>未找到频道</CommandEmpty>
                                                <CommandGroup>
                                                    {channels.map((channel) => (
                                                        <CommandItem
                                                            value={`${channel.id} ${channel.name}`}
                                                            key={channel.id}
                                                            onSelect={() => {
                                                                form.setValue("channelId", String(channel.id))
                                                                setOpenCombobox(false)
                                                            }}
                                                        >
                                                            <Check
                                                                className={cn(
                                                                    "mr-2 h-4 w-4",
                                                                    channel.id.toString() === field.value
                                                                        ? "opacity-100"
                                                                        : "opacity-0"
                                                                )}
                                                            />
                                                            {channel.id} - {channel.name}
                                                        </CommandItem>
                                                    ))}
                                                </CommandGroup>
                                            </CommandList>
                                        </Command>
                                    </PopoverContent>
                                </Popover>
                                <FormMessage />
                            </FormItem>
                        )}
                    />
                    <div className="grid grid-cols-2 gap-4">
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
                    <DialogFooter>
                        <Button type="button" variant="outline" onClick={() => setCreateOpen(false)}>取消</Button>
                        <Button type="submit">提交任务</Button>
                    </DialogFooter>
                </form>
            </Form>
        </DialogContent>
      </Dialog>

      <Dialog open={Boolean(infoModal)} onOpenChange={(open) => !open && setInfoModal(null)}>
        <DialogContent className="max-w-3xl max-h-[80vh] flex flex-col">
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
