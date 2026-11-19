import { useEffect, useMemo, useRef, useState } from 'react'
import { useForm, useWatch } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import * as z from 'zod'
import { Check, ChevronsUpDown } from 'lucide-react'
import { useAppContext } from '../../app/context'
import { formatChannelCodecSummary } from '../shared/channel-probe'
import { getRecordingSourceMode } from '../shared/recording-source'
import { ChannelProtocolBadge } from '@/components/channel-protocol-badge'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import {
  Form,
  FormControl,
  FormDescription,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
} from '@/components/ui/form'
import { Input } from '@/components/ui/input'
import {
  Command,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
} from '@/components/ui/command'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { DateTimePicker } from '@/components/ui/date-time-picker'

const taskSchema = z
  .object({
    channelId: z.string().min(1, '请选择频道'),
    startTime: z.date({ message: '请选择开始时间' }),
    endTime: z.date({ message: '请选择结束时间' }),
    taskName: z.string().optional(),
  })
  .refine((data) => data.endTime > data.startTime, {
    message: '结束时间必须大于开始时间',
    path: ['endTime'],
  })

type TaskFormValues = z.infer<typeof taskSchema>

export type TaskFormInitialValues = {
  channelId?: number
  startTime?: Date
  endTime?: Date
  taskName?: string
}

export default function TaskFormDialog({
  open,
  onOpenChange,
  initialValues,
  entry,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  initialValues?: TaskFormInitialValues
  entry?: 'channel' | 'retry'
}) {
  const { channels, channelProfiles, createTask, now } = useAppContext()
  const channelGroups = useMemo(() => {
    const groups = new Map<string, typeof channels>()
    for (const channel of channels) {
      const group = channel.group?.trim() || '未分组'
      const items = groups.get(group)
      if (items) {
        items.push(channel)
      } else {
        groups.set(group, [channel])
      }
    }
    return [...groups.entries()].sort(([left], [right]) => {
      if (left === '未分组') return 1
      if (right === '未分组') return -1
      return left.localeCompare(right, 'zh-CN')
    })
  }, [channels])
  const [openCombobox, setOpenCombobox] = useState(false)
  const fromEpg = initialValues != null && entry == null

  const form = useForm<TaskFormValues>({
    resolver: zodResolver(taskSchema),
    defaultValues: { taskName: '' },
  })
  const channelId = useWatch({ name: 'channelId', control: form.control })
  const selectedChannel = channels.find((channel) => String(channel.id) === channelId)
  const isLive = selectedChannel != null && getRecordingSourceMode(selectedChannel.url) === 'live'
  const submitting = form.formState.isSubmitting

  // 仅在弹窗从关闭到打开的那次渲染重置表单。打开期间 initialValues/channels 等引用
  // 可能变化（如 EPG 节目切换、后台刷新），不应覆盖用户正在编辑的内容。
  const wasOpenRef = useRef(false)

  useEffect(() => {
    if (!open) {
      wasOpenRef.current = false
      return
    }
    if (wasOpenRef.current) {
      return
    }
    wasOpenRef.current = true

    if (initialValues) {
      const start = initialValues.startTime ?? (entry ? now : new Date())
      form.reset({
        channelId:
          initialValues.channelId != null ? String(initialValues.channelId) : String(channels[0]?.id ?? ''),
        startTime: start,
        endTime: initialValues.endTime ?? new Date(start.getTime() + 30 * 60 * 1000),
        taskName: initialValues.taskName ?? '',
      })
      return
    }

    let draft: { channelId?: number; startTime?: string; endTime?: string; taskName?: string } | null = null
    try {
      const stored = window.localStorage.getItem('rtsp_task_draft')
      if (stored) draft = JSON.parse(stored)
    } catch {
      // 浏览器禁用存储或旧草稿损坏时，继续使用新任务默认值。
    }
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
    const preferredChannelId =
      draft?.channelId && channels.some((channel) => channel.id === draft.channelId)
        ? String(draft.channelId)
        : String(channels[0]?.id ?? '')
    form.reset({ channelId: preferredChannelId, startTime, endTime, taskName: draft?.taskName ?? '' })
  }, [open, initialValues, channels, entry, form, now])

  const onSubmit = async (values: TaskFormValues) => {
    const channelId = parseInt(values.channelId)
    if (!fromEpg && isLive && values.endTime <= now) {
      form.setError('endTime', { message: '直播录制的结束时间必须晚于当前时间' })
      return
    }
    const payload = {
      channelId,
      startTime: values.startTime.toISOString(),
      endTime: values.endTime.toISOString(),
      taskName: values.taskName?.trim() || null,
    }
    const created = await createTask(payload)
    if (created && !initialValues) {
      try {
        window.localStorage.setItem('rtsp_task_draft', JSON.stringify(payload))
      } catch {
        // 保存草稿失败不影响已创建的任务。
      }
    }
    if (created || fromEpg) {
      onOpenChange(false)
    }
  }

  const recordNow = (minutes: number) => {
    form.setValue('startTime', now, { shouldDirty: true })
    form.setValue('endTime', new Date(now.getTime() + minutes * 60 * 1000), { shouldDirty: true })
    void form.handleSubmit(onSubmit)()
  }

  return (
    <Dialog open={open} onOpenChange={(next) => { if (!submitting || fromEpg) onOpenChange(next) }}>
      <DialogContent
        className="max-h-[90dvh] max-w-[calc(100vw-2rem)] overflow-x-hidden overflow-y-auto sm:max-w-[500px]"
      >
        <DialogHeader>
          <DialogTitle>{fromEpg ? '预约录制' : entry === 'retry' ? '重新安排直播录制' : '新建录制任务'}</DialogTitle>
          <DialogDescription>
            {fromEpg
              ? '已按节目表预填频道和时间，节目表仅供参考，可自行修改。时间为本地时区。'
              : entry === 'retry'
                ? '原直播时段已结束，请选择新的录制时间。原任务记录会保留。'
                : '请设置录制频道和时间段。时间为本地时区。'}
          </DialogDescription>
        </DialogHeader>
        <Form {...form}>
          <form onSubmit={form.handleSubmit(onSubmit)} className="space-y-4">
            <FormField
              control={form.control}
              name="channelId"
              render={({ field }) => {
                const selectedChannel = channels.find((channel) => String(channel.id) === field.value)
                const selectedCodec = selectedChannel
                  ? formatChannelCodecSummary(channelProfiles[selectedChannel.id])
                  : null
                return (
                  <FormItem className="flex flex-col">
                    <FormLabel>频道</FormLabel>
                    <Popover modal open={openCombobox} onOpenChange={setOpenCombobox}>
                      <PopoverTrigger asChild>
                        <FormControl>
                          <Button
                            variant="outline"
                            role="combobox"
                            className={cn('h-auto min-h-10 w-full min-w-0 justify-between py-1.5', !field.value && 'text-muted-foreground')}
                          >
                            {selectedChannel ? (
                              <span className="flex w-full min-w-0 flex-col items-start gap-0.5 text-left">
                                <span className="flex w-full min-w-0 items-center gap-2">
                                  <span className="truncate">{selectedChannel.name}</span>
                                  <ChannelProtocolBadge url={selectedChannel.url} className="shrink-0" />
                                </span>
                                {selectedCodec && (
                                  <span className="w-full max-w-full truncate text-left text-[11px] font-normal leading-tight text-muted-foreground">
                                    {selectedCodec}
                                  </span>
                                )}
                              </span>
                            ) : (
                              <span className="truncate">选择频道...</span>
                            )}
                            <ChevronsUpDown className="ml-2 h-4 w-4 shrink-0 opacity-50" />
                          </Button>
                        </FormControl>
                      </PopoverTrigger>
                      <PopoverContent
                        className="flex max-h-[var(--radix-popover-content-available-height)] w-[var(--radix-popover-trigger-width)] flex-col overflow-hidden p-0"
                        align="start"
                        sideOffset={4}
                        collisionPadding={16}
                        onOpenAutoFocus={(event) => {
                          // 触屏设备不自动聚焦搜索框，避免每次打开都弹出软键盘
                          if (window.matchMedia('(pointer: coarse)').matches) {
                            event.preventDefault()
                          }
                        }}
                      >
                        <Command className="min-h-0 [&_[cmdk-input-wrapper]]:shrink-0">
                          <CommandInput placeholder="搜索频道..." />
                          <CommandList className="min-h-0 overscroll-contain">
                            <CommandEmpty>未找到频道</CommandEmpty>
                            {channelGroups.map(([group, groupChannels]) => (
                              <CommandGroup key={group} heading={group}>
                                {groupChannels.map((channel) => {
                                  const codec = formatChannelCodecSummary(channelProfiles[channel.id])
                                  return (
                                    <CommandItem
                                      value={`${channel.id} ${channel.name} ${group} ${codec ?? ''}`}
                                      key={channel.id}
                                      className="gap-2"
                                      onSelect={() => {
                                        form.setValue('channelId', String(channel.id))
                                        setOpenCombobox(false)
                                      }}
                                    >
                                      <Check
                                        className={cn(
                                          'mr-2 h-4 w-4 shrink-0',
                                          channel.id.toString() === field.value ? 'opacity-100' : 'opacity-0',
                                        )}
                                      />
                                      <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                                        <span className="truncate">
                                          {channel.id} - {channel.name}
                                        </span>
                                        <span className="truncate text-[11px] text-muted-foreground">
                                          {codec ?? '未检测编码'}
                                        </span>
                                      </div>
                                      <ChannelProtocolBadge url={channel.url} className="ml-auto shrink-0" />
                                    </CommandItem>
                                  )
                                })}
                              </CommandGroup>
                            ))}
                          </CommandList>
                        </Command>
                      </PopoverContent>
                    </Popover>
                    <FormMessage />
                  </FormItem>
                )
              }}
            />
            {entry === 'channel' && isLive && (
              <div className="flex flex-wrap gap-2">
                <Button type="button" variant="outline" size="sm" disabled={submitting} onClick={() => recordNow(30)}>现在录 30 分钟</Button>
                <Button type="button" variant="outline" size="sm" disabled={submitting} onClick={() => recordNow(60)}>现在录 1 小时</Button>
              </div>
            )}
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
                  <FormDescription>如果不填写，将自动生成默认名称。</FormDescription>
                  <FormMessage />
                </FormItem>
              )}
            />
            <DialogFooter className="gap-2 sm:gap-0">
              <Button type="button" variant="outline" disabled={!fromEpg && submitting} onClick={() => onOpenChange(false)}>
                取消
              </Button>
              <Button type="submit" disabled={!fromEpg && submitting}>{!fromEpg && submitting ? '提交中…' : '提交任务'}</Button>
            </DialogFooter>
          </form>
        </Form>
      </DialogContent>
    </Dialog>
  )
}