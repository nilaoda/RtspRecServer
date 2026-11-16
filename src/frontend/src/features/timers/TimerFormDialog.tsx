import { useEffect, useMemo, useRef, useState } from 'react'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import * as z from 'zod'
import { Check, ChevronsUpDown } from 'lucide-react'
import { useAppContext } from '../../app/context'
import { formatChannelCodecSummary } from '../shared/channel-probe'
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
import { Switch } from '@/components/ui/switch'
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'
import type { TimerUpsertPayload } from '../../types'
import { weekdayLabels } from './lib/window'

const timerSchema = z
  .object({
    name: z.string().min(1, '请填写规则名称'),
    channelId: z.string().min(1, '请选择频道'),
    startTime: z.string().regex(/^\d{2}:\d{2}$/, '请选择开始时间'),
    endTime: z.string().regex(/^\d{2}:\d{2}$/, '请选择结束时间'),
    weekdays: z.array(z.boolean()).length(7).refine((days) => days.some(Boolean), {
      message: '请至少选择一个星期',
    }),
    preOffsetMinutes: z.string().refine((v) => /^\d+$/.test(v) && Number(v) <= 1440, {
      message: '请输入 0 ~ 1440 的分钟数',
    }),
    postOffsetMinutes: z.string().refine((v) => /^\d+$/.test(v) && Number(v) <= 1440, {
      message: '请输入 0 ~ 1440 的分钟数',
    }),
    startDate: z.string().optional(),
    endDate: z.string().optional(),
    enabled: z.boolean(),
  })
  .refine((data) => data.startTime !== data.endTime, {
    message: '结束时间必须与开始时间不同',
    path: ['endTime'],
  })
  .refine((data) => !data.startDate || !data.endDate || data.startDate <= data.endDate, {
    message: '生效开始日期不能晚于结束日期',
    path: ['endDate'],
  })

type TimerFormValues = z.infer<typeof timerSchema>

// 频道下拉与日期输入都可能落在嵌套弹层，阻止外层弹窗被误 dismiss
const isInsideNestedLayer = (event: { target: EventTarget | null; detail?: { originalEvent?: Event } }) => {
  const targets = [event.target, event.detail?.originalEvent?.target]
  return targets.some(
    (target) =>
      target instanceof Element &&
      !!target.closest('[data-radix-popper-content-wrapper], [data-radix-select-content], [role="dialog"], [cmdk-root]'),
  )
}

export default function TimerFormDialog({
  open,
  onOpenChange,
  editing,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  editing?: import('../../types').RecordingTimer | null
}) {
  const { channels, channelProfiles, createTimer, updateTimer } = useAppContext()
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
  const comboboxListRef = useRef<HTMLDivElement>(null)
  const isEditing = editing != null

  const form = useForm<TimerFormValues>({
    resolver: zodResolver(timerSchema),
    defaultValues: {
      name: '',
      channelId: '',
      startTime: '20:00',
      endTime: '21:00',
      weekdays: [true, true, true, true, true, true, true],
      preOffsetMinutes: '0',
      postOffsetMinutes: '0',
      startDate: '',
      endDate: '',
      enabled: true,
    },
  })

  // 仅在弹窗从关闭到打开时重置表单（新建用默认值，编辑回填规则）
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

    form.reset(
      editing
        ? {
            name: editing.name,
            channelId: String(editing.channelId),
            startTime: editing.startTime,
            endTime: editing.endTime,
            weekdays: [...editing.weekdays],
            preOffsetMinutes: String(editing.preOffsetMinutes),
            postOffsetMinutes: String(editing.postOffsetMinutes),
            startDate: editing.startDate ?? '',
            endDate: editing.endDate ?? '',
            enabled: editing.enabled,
          }
        : {
            name: '',
            channelId: String(channels[0]?.id ?? ''),
            startTime: '20:00',
            endTime: '21:00',
            weekdays: [true, true, true, true, true, true, true],
            preOffsetMinutes: '0',
            postOffsetMinutes: '0',
            startDate: '',
            endDate: '',
            enabled: true,
          },
    )
  }, [open, editing, channels, form])

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
    // Dialog 的滚动锁定会在 document 层拦截弹窗外的 touchmove，阻断冒泡以保留原生触摸滚动
    const handleTouchMove = (event: TouchEvent) => {
      event.stopPropagation()
    }

    const timer = window.setTimeout(() => {
      list = comboboxListRef.current
      list?.addEventListener('wheel', handleWheel, { passive: false })
      list?.addEventListener('touchmove', handleTouchMove, { passive: false })
    }, 0)

    return () => {
      window.clearTimeout(timer)
      list?.removeEventListener('wheel', handleWheel)
      list?.removeEventListener('touchmove', handleTouchMove)
    }
  }, [openCombobox])

  const onSubmit = async (values: TimerFormValues) => {
    const payload: TimerUpsertPayload = {
      name: values.name.trim(),
      channelId: parseInt(values.channelId),
      startTime: values.startTime,
      endTime: values.endTime,
      weekdays: values.weekdays,
      preOffsetMinutes: Number(values.preOffsetMinutes) || 0,
      postOffsetMinutes: Number(values.postOffsetMinutes) || 0,
      startDate: values.startDate || null,
      endDate: values.endDate || null,
      enabled: values.enabled,
    }
    try {
      if (isEditing && editing) {
        await updateTimer(editing.id, payload)
      } else {
        await createTimer(payload)
      }
      onOpenChange(false)
    } catch {
      // 错误已在 AppContext 中处理
    }
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent
        className="max-h-[90dvh] max-w-[calc(100vw-2rem)] overflow-x-hidden overflow-y-auto sm:max-w-[520px]"
        onPointerDownOutside={(event) => {
          if (isInsideNestedLayer(event)) {
            event.preventDefault()
          }
        }}
        onFocusOutside={(event) => {
          if (isInsideNestedLayer(event)) {
            event.preventDefault()
          }
        }}
        onInteractOutside={(event) => {
          if (isInsideNestedLayer(event)) {
            event.preventDefault()
          }
        }}
      >
        <DialogHeader>
          <DialogTitle>{isEditing ? '编辑定时规则' : '新建定时规则'}</DialogTitle>
          <DialogDescription>
            规则会在匹配的星期按时间窗口自动创建录制任务，支持跨午夜窗口（如 23:30 - 01:00）。
          </DialogDescription>
        </DialogHeader>
        <Form {...form}>
          <form onSubmit={form.handleSubmit(onSubmit)} className="space-y-4">
            <FormField
              control={form.control}
              name="name"
              render={({ field }) => (
                <FormItem>
                  <FormLabel>规则名称</FormLabel>
                  <FormControl>
                    <Input placeholder="晚间新闻" {...field} />
                  </FormControl>
                  <FormMessage />
                </FormItem>
              )}
            />
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
                    <Popover open={openCombobox} onOpenChange={setOpenCombobox}>
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
                        className="w-[var(--radix-popover-trigger-width)] p-0"
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
                        <Command>
                          <CommandInput placeholder="搜索频道..." />
                          <CommandList ref={comboboxListRef}>
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
            <div className="grid grid-cols-2 gap-4">
              <FormField
                control={form.control}
                name="startTime"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>开始时间</FormLabel>
                    <FormControl>
                      <Input type="time" {...field} />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />
              <FormField
                control={form.control}
                name="endTime"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>结束时间</FormLabel>
                    <FormControl>
                      <Input type="time" {...field} />
                    </FormControl>
                    <FormDescription>早于开始时间表示跨午夜</FormDescription>
                    <FormMessage />
                  </FormItem>
                )}
              />
            </div>
            <FormField
              control={form.control}
              name="weekdays"
              render={({ field }) => (
                <FormItem>
                  <FormLabel>重复</FormLabel>
                  <FormControl>
                    <ToggleGroup
                      type="multiple"
                      variant="outline"
                      className="flex flex-wrap gap-1.5"
                      value={field.value
                        .map((on, index) => (on ? String(index) : ''))
                        .filter((value) => value !== '')}
                      onValueChange={(values) => {
                        field.onChange(weekdayLabels.map((_, index) => values.includes(String(index))))
                      }}
                    >
                      {weekdayLabels.map((label, index) => (
                        <ToggleGroupItem
                          key={index}
                          value={String(index)}
                          className="h-9 w-11 px-0 text-xs"
                          aria-label={`星期${label}`}
                        >
                          {label}
                        </ToggleGroupItem>
                      ))}
                    </ToggleGroup>
                  </FormControl>
                  <FormMessage />
                </FormItem>
              )}
            />
            <div className="grid grid-cols-2 gap-4">
              <FormField
                control={form.control}
                name="preOffsetMinutes"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>提前开始（分钟）</FormLabel>
                    <FormControl>
                      <Input type="number" min={0} max={1440} {...field} />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />
              <FormField
                control={form.control}
                name="postOffsetMinutes"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>延后结束（分钟）</FormLabel>
                    <FormControl>
                      <Input type="number" min={0} max={1440} {...field} />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />
            </div>
            <div className="grid grid-cols-2 gap-4">
              <FormField
                control={form.control}
                name="startDate"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>生效开始日期</FormLabel>
                    <FormControl>
                      <Input type="date" {...field} />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />
              <FormField
                control={form.control}
                name="endDate"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>生效结束日期</FormLabel>
                    <FormControl>
                      <Input type="date" {...field} />
                    </FormControl>
                    <FormMessage />
                  </FormItem>
                )}
              />
            </div>
            <FormField
              control={form.control}
              name="enabled"
              render={({ field }) => (
                <FormItem className="flex flex-row items-center justify-between rounded-lg border p-3">
                  <div className="space-y-0.5">
                    <FormLabel>启用规则</FormLabel>
                    <FormDescription>停用后不再自动生成录制任务</FormDescription>
                  </div>
                  <FormControl>
                    <Switch checked={field.value} onCheckedChange={field.onChange} />
                  </FormControl>
                </FormItem>
              )}
            />
            <DialogFooter className="gap-2 sm:gap-0">
              <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
                取消
              </Button>
              <Button type="submit">{isEditing ? '保存修改' : '创建规则'}</Button>
            </DialogFooter>
          </form>
        </Form>
      </DialogContent>
    </Dialog>
  )
}
