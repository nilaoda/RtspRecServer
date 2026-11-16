import { useCallback, useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import * as z from 'zod'
import { Activity, AlertCircle, CheckCircle2, FileUp, Info, Loader2, Plus, Save, Settings2, Trash2 } from 'lucide-react'
import { toast } from 'sonner'

import { useAppContext } from '../../app/context'
import { deleteChannels, getChannelProbes, probeChannels } from '../../services/api'
import type { ChannelProbeResult } from '../../types'
import { formatDateTime } from '../shared/format'
import { formatProbeBitrate } from '../shared/channel-probe'
import { ChannelProtocolBadge } from '@/components/channel-protocol-badge'

import { Button } from "@/components/ui/button"
import { Badge } from "@/components/ui/badge"
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
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
} from "@/components/ui/form"
import { Input } from "@/components/ui/input"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogTrigger,
} from "@/components/ui/alert-dialog"
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
} from "@/components/ui/tooltip"
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover"
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"

const PROBE_CONCURRENCY_KEY = 'rtsp_probe_concurrency'
const PROBE_RETRIES_KEY = 'rtsp_probe_retries'
const ALL_GROUPS = '__all_groups__'
const UNGROUPED = '__ungrouped__'
const GROUP_OPTIONS_ID = 'channel-group-options'

const clampProbeSetting = (value: number, min: number, max: number) =>
  Math.min(max, Math.max(min, Math.round(value)))

const readProbeSetting = (key: string, fallback: number, min: number, max: number) => {
  try {
    const raw = localStorage.getItem(key)
    const parsed = raw === null ? Number.NaN : Number(raw)
    return Number.isFinite(parsed) ? clampProbeSetting(parsed, min, max) : fallback
  } catch {
    return fallback
  }
}

const channelSchema = z.object({
  name: z.string().min(1, "请输入频道名称"),
  url: z.string().min(1, "请输入频道地址"),
  group: z.string().optional(),
})

type ChannelFormValues = z.infer<typeof channelSchema>

type ChannelDraft = { name: string; url: string; group: string }

const trackLabels = (tracks: ChannelProbeResult['videoTracks']) => {
  const labels = tracks.map((track) => track.label).filter(Boolean)
  return labels.length > 0 ? labels.join(' / ') : '未识别'
}

const probeErrorHint = (url: string) => {
  const value = url.trim().toLowerCase()
  if (value.startsWith('rtp://') || value.startsWith('udp://') ||
      value.includes('/rtp/') || value.includes('/udp/')) {
    return '检查组播地址、VLAN、防火墙和多网卡 localaddr 配置。'
  }
  if (value.startsWith('rtsp://')) {
    return '检查 RTSP 地址、鉴权信息和服务器状态。'
  }
  return '检查频道地址、上游服务和网络连通性。'
}

const ChannelsPage = () => {
  const { channels, addChannel, updateChannel, deleteChannel, reloadChannels, reloadChannelProfiles } = useAppContext()
  const [drafts, setDrafts] = useState<Record<number, ChannelDraft>>({})
  const [probes, setProbes] = useState<Record<number, ChannelProbeResult>>({})
  const [probingIds, setProbingIds] = useState<Set<number>>(() => new Set())
  const [detailChannelId, setDetailChannelId] = useState<number | null>(null)
  const [selectedChannelIds, setSelectedChannelIds] = useState<Set<number>>(() => new Set())
  const [groupFilter, setGroupFilter] = useState(ALL_GROUPS)
  const [bulkDeleteOpen, setBulkDeleteOpen] = useState(false)
  const [probeConcurrency, setProbeConcurrency] = useState(() => readProbeSetting(PROBE_CONCURRENCY_KEY, 3, 1, 10))
  const [probeRetries, setProbeRetries] = useState(() => readProbeSetting(PROBE_RETRIES_KEY, 0, 0, 5))
  const [probeSettingsOpen, setProbeSettingsOpen] = useState(false)
  const [removeInvalidOpen, setRemoveInvalidOpen] = useState(false)

  const form = useForm<ChannelFormValues>({
    resolver: zodResolver(channelSchema),
    defaultValues: {
      name: '',
      url: '',
      group: '',
    },
  })
  const urlValue = form.watch('url')

  useEffect(() => {
    let cancelled = false
    getChannelProbes()
      .then((results) => {
        if (cancelled) return
        setProbes(Object.fromEntries(results.map((result) => [result.channelId, result])))
      })
      .catch(() => {
        // 检测结果加载失败不阻塞频道管理。
      })

    return () => {
      cancelled = true
    }
  }, [])

  useEffect(() => {
    try {
      localStorage.setItem(PROBE_CONCURRENCY_KEY, String(probeConcurrency))
    } catch {
      // localStorage 不可用时仅本次会话生效
    }
  }, [probeConcurrency])

  useEffect(() => {
    try {
      localStorage.setItem(PROBE_RETRIES_KEY, String(probeRetries))
    } catch {
      // localStorage 不可用时仅本次会话生效
    }
  }, [probeRetries])

  const probeSummary = useMemo(() => {
    let accessible = 0
    let offline = 0
    let idle = 0
    for (const channel of channels) {
      if (probingIds.has(channel.id)) {
        continue
      }
      const result = probes[channel.id]
      if (!result) {
        idle += 1
      } else if (result.accessible) {
        accessible += 1
      } else {
        offline += 1
      }
    }
    return { accessible, offline, idle }
  }, [channels, probes, probingIds])

  const groupOptions = useMemo(() => {
    const groups = new Set<string>()
    for (const channel of channels) {
      const group = channel.group?.trim()
      if (group) {
        groups.add(group)
      }
    }
    return [...groups].sort((left, right) => left.localeCompare(right, 'zh-CN'))
  }, [channels])

  const filteredChannels = useMemo(() => {
    if (groupFilter === ALL_GROUPS) {
      return channels
    }
    if (groupFilter === UNGROUPED) {
      return channels.filter((channel) => !channel.group?.trim())
    }
    return channels.filter((channel) => (channel.group?.trim() || '') === groupFilter)
  }, [channels, groupFilter])

  useEffect(() => {
    if (groupFilter === ALL_GROUPS || groupFilter === UNGROUPED) {
      return
    }
    if (!groupOptions.includes(groupFilter)) {
      setGroupFilter(ALL_GROUPS)
      setSelectedChannelIds(new Set())
    }
  }, [groupFilter, groupOptions])

  const invalidChannels = useMemo(
    () => channels.filter((channel) => {
      if (probingIds.has(channel.id)) {
        return false
      }
      const draft = drafts[channel.id]
      if (draft && (draft.name !== channel.name || draft.url !== channel.url || draft.group !== (channel.group ?? ''))) {
        return false
      }
      const result = probes[channel.id]
      return result !== undefined && !result.accessible
    }),
    [channels, drafts, probes, probingIds],
  )

  const selectedChannels = useMemo(
    () => filteredChannels.filter((channel) => selectedChannelIds.has(channel.id)),
    [filteredChannels, selectedChannelIds],
  )
  const selectedCount = selectedChannels.length
  const allSelected = filteredChannels.length > 0 && filteredChannels.every((channel) => selectedChannelIds.has(channel.id))
  const someSelected = !allSelected && filteredChannels.some((channel) => selectedChannelIds.has(channel.id))

  const onGroupFilterChange = (value: string) => {
    setGroupFilter(value)
    setSelectedChannelIds(new Set())
  }

  const toggleChannelSelection = (id: number) => {
    setSelectedChannelIds((previous) => {
      const next = new Set(previous)
      if (next.has(id)) {
        next.delete(id)
      } else {
        next.add(id)
      }
      return next
    })
  }

  const toggleAllChannels = () => {
    setSelectedChannelIds((previous) => {
      const next = new Set(previous)
      if (allSelected) {
        filteredChannels.forEach((channel) => next.delete(channel.id))
      } else {
        filteredChannels.forEach((channel) => next.add(channel.id))
      }
      return next
    })
  }

  useEffect(() => {
    setSelectedChannelIds((previous) => {
      const existing = new Set(channels.map((channel) => channel.id))
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
  }, [channels])

  useEffect(() => {
    if (groupFilter === ALL_GROUPS) {
      return
    }
    const visibleIds = new Set(filteredChannels.map((channel) => channel.id))
    setSelectedChannelIds((previous) => {
      let changed = false
      const next = new Set<number>()
      for (const id of previous) {
        if (visibleIds.has(id)) {
          next.add(id)
        } else {
          changed = true
        }
      }
      return changed ? next : previous
    })
  }, [filteredChannels, groupFilter])

  const runProbe = useCallback(async (ids: number[]) => {
    if (ids.length === 0) {
      return
    }

    setProbingIds((previous) => {
      const next = new Set(previous)
      ids.forEach((id) => next.add(id))
      return next
    })

    try {
      const results = await probeChannels(ids, {
        concurrency: probeConcurrency,
        retries: probeRetries,
      })
      setProbes((previous) => {
        const next = { ...previous }
        results.forEach((result) => {
          next[result.channelId] = result
        })
        return next
      })
      void reloadChannelProfiles()
    } catch {
      toast.error('频道检测失败，请检查服务端状态')
    } finally {
      setProbingIds((previous) => {
        const next = new Set(previous)
        ids.forEach((id) => next.delete(id))
        return next
      })
    }
  }, [probeConcurrency, probeRetries, reloadChannelProfiles])

  const probeAll = () => {
    if (probingIds.size > 0) {
      return
    }
    void runProbe(channels.map((channel) => channel.id))
  }

  const probeSelected = () => {
    if (selectedCount === 0 || probingIds.size > 0) {
      return
    }
    void runProbe(selectedChannels.map((channel) => channel.id))
  }

  const removeChannels = useCallback(async (ids: number[]) => {
    if (ids.length === 0) {
      return
    }

    try {
      const result = await deleteChannels(ids)
      if (result.failed === 0) {
        toast.success(`已删除 ${result.succeeded} 个频道`)
      } else if (result.succeeded > 0) {
        toast.info(`已删除 ${result.succeeded} 个频道，${result.failed} 个失败`)
      } else {
        toast.error('批量删除失败')
      }

      const succeededSet = new Set(result.succeededIds)
      setSelectedChannelIds((previous) => new Set([...previous].filter((id) => !succeededSet.has(id))))
      setProbes((previous) => {
        const next = { ...previous }
        succeededSet.forEach((id) => delete next[id])
        return next
      })
      await reloadChannels()
      void reloadChannelProfiles()
    } catch {
      toast.error('批量删除失败')
    }
  }, [reloadChannels, reloadChannelProfiles])

  const onBulkDelete = async () => {
    const ids = selectedChannels.map((channel) => channel.id)
    setBulkDeleteOpen(false)
    await removeChannels(ids)
  }

  const onRemoveInvalid = async () => {
    const ids = invalidChannels.map((channel) => channel.id)
    setRemoveInvalidOpen(false)
    await removeChannels(ids)
  }

  const selectedProbeChannel = channels.find((channel) => channel.id === detailChannelId)
  const selectedProbe = detailChannelId === null ? undefined : probes[detailChannelId]
  const selectedProbeChecking = detailChannelId !== null && probingIds.has(detailChannelId)

  const onAdd = async (values: ChannelFormValues) => {
    try {
        await addChannel({
          name: values.name.trim(),
          url: values.url.trim(),
          group: values.group?.trim() || null,
        })
        form.reset()
    } catch {
        // Handled in context
    }
  }

  const onSave = useCallback(
    async (id: number) => {
      const draft = drafts[id]
      if (!draft || !draft.name.trim() || !draft.url.trim()) {
        toast.error('频道名称与地址不能为空')
        return
      }
      const original = channels.find((channel) => channel.id === id)
      const nextGroup = draft.group.trim() || null
      try {
        await updateChannel({ id, name: draft.name.trim(), url: draft.url.trim(), group: nextGroup })
        setDrafts((prev) => {
          const next = { ...prev }
          delete next[id]
          return next
        })
        if (original && original.url !== draft.url) {
          setProbes((prev) => {
            const next = { ...prev }
            delete next[id]
            return next
          })
        }
        if (groupFilter !== ALL_GROUPS) {
          const stillVisible = groupFilter === UNGROUPED ? !nextGroup : nextGroup === groupFilter
          if (!stillVisible) {
            setSelectedChannelIds((prev) => {
              const next = new Set(prev)
              next.delete(id)
              return next
            })
          }
        }
      } catch {
          // Handled in context
      }
    },
    [channels, drafts, groupFilter, updateChannel],
  )

  return (
    <div className="space-y-6">
      <datalist id={GROUP_OPTIONS_ID}>
        {groupOptions.map((group) => (
          <option key={group} value={group} />
        ))}
      </datalist>
      <Card>
        <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-3">
            <CardTitle className="text-lg font-medium">添加频道</CardTitle>
            <Button asChild variant="outline" size="sm">
                <Link to="/channels/import">
                    <FileUp className="mr-2 h-4 w-4" />
                    导入 M3U
                </Link>
            </Button>
        </CardHeader>
        <CardContent>
            <Form {...form}>
                <form onSubmit={form.handleSubmit(onAdd)} className="flex flex-col md:flex-row gap-4 items-end">
                    <FormField
                        control={form.control}
                        name="name"
                        render={({ field }) => (
                            <FormItem className="w-full md:w-[250px]">
                                <FormLabel>频道名称</FormLabel>
                                <FormControl>
                                    <Input placeholder="输入频道名称" {...field} />
                                </FormControl>
                                <FormMessage />
                            </FormItem>
                        )}
                    />
                    <FormField
                        control={form.control}
                        name="group"
                        render={({ field }) => (
                            <FormItem className="w-full md:w-[160px]">
                                <FormLabel>分组（可选）</FormLabel>
                                <FormControl>
                                    <Input placeholder="如：央视" list={GROUP_OPTIONS_ID} {...field} />
                                </FormControl>
                                <FormMessage />
                            </FormItem>
                        )}
                    />
                    <FormField
                        control={form.control}
                        name="url"
                        render={({ field }) => (
                            <FormItem className="flex-1 w-full">
                                <FormLabel className="flex items-center justify-between gap-2">
                                    <span>频道地址</span>
                                    {urlValue?.trim() ? <ChannelProtocolBadge url={urlValue} /> : null}
                                </FormLabel>
                                <FormControl>
                                    <Input placeholder="rtsp://..." {...field} />
                                </FormControl>
                                <FormMessage />
                            </FormItem>
                        )}
                    />
                    <Button type="submit" className="w-full md:w-auto">
                        <Plus className="mr-2 h-4 w-4" />
                        添加频道
                    </Button>
                </form>
            </Form>
        </CardContent>
      </Card>

      <Card>
        <CardHeader className="flex flex-col gap-3 pb-3 sm:flex-row sm:items-center sm:justify-between">
            <div>
                <CardTitle className="text-lg font-medium">频道列表</CardTitle>
                <div className="mt-1 flex flex-wrap items-center gap-x-3 gap-y-1 text-xs">
                    <span className="text-green-600 dark:text-green-400">可访问 {probeSummary.accessible}</span>
                    <span className="text-red-600 dark:text-red-400">异常 {probeSummary.offline}</span>
                    <span className="text-muted-foreground">未检测 {probeSummary.idle}</span>
                    {probingIds.size > 0 && (
                        <span className="flex items-center gap-1 text-primary">
                            <Loader2 className="h-3 w-3 animate-spin" />
                            检测中 {probingIds.size}
                        </span>
                    )}
                </div>
            </div>
            <div className="flex flex-wrap items-center gap-2">
                <Select value={groupFilter} onValueChange={onGroupFilterChange}>
                    <SelectTrigger className="h-8 w-[140px] text-xs" aria-label="按分组筛选">
                        <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                        <SelectItem value={ALL_GROUPS}>全部分组</SelectItem>
                        {groupOptions.map((group) => (
                            <SelectItem key={group} value={group}>{group}</SelectItem>
                        ))}
                        <SelectItem value={UNGROUPED}>未分组</SelectItem>
                    </SelectContent>
                </Select>
                {groupFilter !== ALL_GROUPS && (
                    <span className="text-xs text-muted-foreground">显示 {filteredChannels.length}</span>
                )}
                {invalidChannels.length > 0 && (
                    <Button
                        size="sm"
                        variant="outline"
                        className="text-destructive hover:text-destructive"
                        onClick={() => setRemoveInvalidOpen(true)}
                        disabled={probingIds.size > 0}
                    >
                        <Trash2 className="mr-2 h-4 w-4" />
                        移除失效 ({invalidChannels.length})
                    </Button>
                )}
                <Popover open={probeSettingsOpen} onOpenChange={setProbeSettingsOpen}>
                    <PopoverTrigger asChild>
                        <Button size="sm" variant="outline">
                            <Settings2 className="mr-2 h-4 w-4" />
                            检测设置
                        </Button>
                    </PopoverTrigger>
                    <PopoverContent align="end" className="w-64 space-y-3">
                        <div className="text-sm font-medium">检测设置</div>
                        <div className="space-y-1.5">
                            <label htmlFor="probe-concurrency" className="text-xs text-muted-foreground">
                                并行数量（1-10）
                            </label>
                            <Input
                                id="probe-concurrency"
                                type="number"
                                min={1}
                                max={10}
                                className="h-8"
                                value={probeConcurrency}
                                onChange={(event) => setProbeConcurrency(clampProbeSetting(Number(event.target.value) || 1, 1, 10))}
                            />
                        </div>
                        <div className="space-y-1.5">
                            <label htmlFor="probe-retries" className="text-xs text-muted-foreground">
                                失败重试次数（0-5）
                            </label>
                            <Input
                                id="probe-retries"
                                type="number"
                                min={0}
                                max={5}
                                className="h-8"
                                value={probeRetries}
                                onChange={(event) => setProbeRetries(clampProbeSetting(Number(event.target.value) || 0, 0, 5))}
                            />
                        </div>
                        <p className="text-[11px] leading-relaxed text-muted-foreground">
                            仅对不可访问的频道重试，每次采样约 5 秒。设置保存在当前浏览器。
                        </p>
                    </PopoverContent>
                </Popover>
                <Button
                    size="sm"
                    onClick={probeAll}
                    disabled={channels.length === 0 || probingIds.size > 0}
                >
                    {probingIds.size > 0 ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <Activity className="mr-2 h-4 w-4" />}
                    {probingIds.size > 0 ? '检测中' : '检测全部'}
                </Button>
            </div>
        </CardHeader>
        <CardContent className="p-0">
            {selectedCount > 0 && (
                <div className="flex flex-wrap items-center justify-between gap-2 border-b bg-muted/30 px-4 py-2">
                    <span className="text-xs text-muted-foreground">
                        已选 <span className="font-medium text-foreground">{selectedCount}</span> 个频道
                    </span>
                    <div className="flex flex-wrap items-center gap-2">
                        <Button
                            size="sm"
                            variant="outline"
                            onClick={probeSelected}
                            disabled={probingIds.size > 0}
                        >
                            {probingIds.size > 0 ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <Activity className="mr-2 h-4 w-4" />}
                            检测选中
                        </Button>
                        <Button
                            size="sm"
                            variant="outline"
                            className="text-destructive hover:text-destructive"
                            onClick={() => setBulkDeleteOpen(true)}
                            disabled={probingIds.size > 0}
                        >
                            <Trash2 className="mr-2 h-4 w-4" />
                            删除选中
                        </Button>
                        <Button size="sm" variant="ghost" onClick={() => setSelectedChannelIds(new Set())}>
                            取消选择
                        </Button>
                    </div>
                </div>
            )}
            <div className="rounded-md border-t">
                <Table>
                    <TableHeader>
                        <TableRow>
                            <TableHead className="w-[40px]">
                                <input
                                    type="checkbox"
                                    className="h-4 w-4 accent-primary"
                                    checked={allSelected}
                                    ref={(input) => {
                                        if (input) input.indeterminate = someSelected
                                    }}
                                    onChange={toggleAllChannels}
                                    aria-label="全选当前列表频道"
                                />
                            </TableHead>
                            <TableHead className="w-[64px]">ID</TableHead>
                            <TableHead className="w-[230px]">名称</TableHead>
                            <TableHead className="w-[110px]">分组</TableHead>
                            <TableHead className="w-[72px]">协议</TableHead>
                            <TableHead className="min-w-[260px]">地址</TableHead>
                            <TableHead className="min-w-[230px]">检测</TableHead>
                            <TableHead className="w-[200px] text-right">操作</TableHead>
                        </TableRow>
                    </TableHeader>
                    <TableBody>
                        {filteredChannels.length === 0 ? (
                            <TableRow>
                                <TableCell colSpan={8} className="h-24 text-center">
                                    {channels.length === 0 ? '暂无频道' : '该分组暂无频道'}
                                </TableCell>
                            </TableRow>
                        ) : (
                            filteredChannels.map((channel) => {
                                const draft = drafts[channel.id] ?? {
                                    name: channel.name,
                                    url: channel.url,
                                    group: channel.group ?? '',
                                }
                                const hasChanged = draft.name !== channel.name
                                    || draft.url !== channel.url
                                    || draft.group !== (channel.group ?? '')
                                const probe = probes[channel.id]
                                const checking = probingIds.has(channel.id)
                                const selected = selectedChannelIds.has(channel.id)
                                
                                return (
                                    <TableRow key={channel.id} className={selected ? 'bg-primary/5' : undefined}>
                                        <TableCell className="py-2">
                                            <input
                                                type="checkbox"
                                                className="h-4 w-4 accent-primary"
                                                checked={selected}
                                                onChange={() => toggleChannelSelection(channel.id)}
                                                aria-label={`选择频道 ${channel.name}`}
                                            />
                                        </TableCell>
                                        <TableCell className="font-mono text-xs py-2">{channel.id}</TableCell>
                                        <TableCell className="py-2">
                                            <Input 
                                                value={draft.name} 
                                                onChange={(e) => setDrafts(prev => ({
                                                    ...prev,
                                                    [channel.id]: { ...draft, name: e.target.value }
                                                }))}
                                                className="h-8"
                                            />
                                        </TableCell>
                                        <TableCell className="py-2">
                                            <Input
                                                value={draft.group}
                                                list={GROUP_OPTIONS_ID}
                                                placeholder="未分组"
                                                onChange={(e) => setDrafts(prev => ({
                                                    ...prev,
                                                    [channel.id]: { ...draft, group: e.target.value }
                                                }))}
                                                className="h-8"
                                            />
                                        </TableCell>
                                        <TableCell className="py-2">
                                            <ChannelProtocolBadge url={draft.url} />
                                        </TableCell>
                                        <TableCell className="py-2">
                                            <Input 
                                                value={draft.url} 
                                                onChange={(e) => setDrafts(prev => ({
                                                    ...prev,
                                                    [channel.id]: { ...draft, url: e.target.value }
                                                }))}
                                                className="h-8 font-mono text-xs"
                                            />
                                        </TableCell>
                                        <TableCell className="py-2">
                                            {checking ? (
                                                <div className="flex items-center gap-2 text-xs text-muted-foreground">
                                                    <Loader2 className="h-3.5 w-3.5 animate-spin text-primary" />
                                                    <div>
                                                        <div className="font-medium text-foreground">检测中…</div>
                                                        <div>每次采样约 5 秒</div>
                                                    </div>
                                                </div>
                                            ) : probe ? (
                                                <div className="space-y-1">
                                                    <div className="flex flex-wrap items-center gap-1.5">
                                                        <Badge
                                                            variant="outline"
                                                            className={probe.accessible
                                                                ? 'border-green-500/30 bg-green-500/10 text-green-600 dark:text-green-400'
                                                                : 'border-red-500/30 bg-red-500/10 text-red-600 dark:text-red-400'}
                                                        >
                                                            {probe.accessible ? <CheckCircle2 className="mr-1 h-3 w-3" /> : <AlertCircle className="mr-1 h-3 w-3" />}
                                                            {probe.accessible ? '可访问' : '不可访问'}
                                                        </Badge>
                                                        {probe.accessible && probe.videoTracks.length > 0 && (
                                                            <span className="max-w-[110px] truncate font-mono text-[10px] text-muted-foreground" title={trackLabels(probe.videoTracks)}>
                                                                {trackLabels(probe.videoTracks)}
                                                            </span>
                                                        )}
                                                        {probe.accessible && probe.audioTracks.length > 0 && (
                                                            <span className="max-w-[110px] truncate font-mono text-[10px] text-muted-foreground" title={trackLabels(probe.audioTracks)}>
                                                                + {trackLabels(probe.audioTracks)}
                                                            </span>
                                                        )}
                                                        {probe.accessible && probe.tsVerdict && probe.tsVerdict !== 'pass' && (
                                                            <span className="text-[10px] text-amber-600 dark:text-amber-400">
                                                                TS {probe.tsVerdict === 'error' ? '异常' : probe.tsVerdict === 'warning' ? '警告' : '未完成'}
                                                            </span>
                                                        )}
                                                    </div>
                                                    {probe.accessible ? (
                                                        <div className="text-[11px] text-muted-foreground">
                                                            {formatProbeBitrate(probe.bitrateKbps)} · 错误 {probe.errorCount} · 警告 {probe.warningCount}
                                                        </div>
                                                    ) : (
                                                        <div className="text-[11px] text-red-600 dark:text-red-400">
                                                            {probe.errorMessage ?? '未收到有效数据'}
                                                        </div>
                                                    )}
                                                    <div className="text-[10px] text-muted-foreground">
                                                        {formatDateTime(new Date(probe.checkedAt))} · {probe.elapsedSeconds.toFixed(1)}s
                                                        {probe.attempts && probe.attempts > 1 ? ` · 尝试 ${probe.attempts} 次` : ''}
                                                    </div>
                                                </div>
                                            ) : (
                                                <span className="text-xs text-muted-foreground">未检测</span>
                                            )}
                                        </TableCell>
                                        <TableCell className="text-right py-2">
                                            <div className="flex justify-end gap-1">
                                                <TooltipProvider>
                                                    <Tooltip>
                                                        <TooltipTrigger asChild>
                                                            <Button
                                                                size="icon"
                                                                variant="ghost"
                                                                className="h-8 w-8"
                                                                disabled={checking || hasChanged}
                                                                onClick={() => void runProbe([channel.id])}
                                                            >
                                                                {checking ? <Loader2 className="h-4 w-4 animate-spin" /> : <Activity className="h-4 w-4" />}
                                                                <span className="sr-only">检测</span>
                                                            </Button>
                                                        </TooltipTrigger>
                                                        <TooltipContent>
                                                            <p>{hasChanged ? '请先保存频道修改' : '检测频道'}</p>
                                                        </TooltipContent>
                                                    </Tooltip>
                                                </TooltipProvider>
                                                <TooltipProvider>
                                                    <Tooltip>
                                                        <TooltipTrigger asChild>
                                                            <Button
                                                                size="icon"
                                                                variant="ghost"
                                                                className="h-8 w-8"
                                                                disabled={!probe || checking}
                                                                onClick={() => setDetailChannelId(channel.id)}
                                                            >
                                                                <Info className="h-4 w-4" />
                                                                <span className="sr-only">检测详情</span>
                                                            </Button>
                                                        </TooltipTrigger>
                                                        <TooltipContent>
                                                            <p>检测详情</p>
                                                        </TooltipContent>
                                                    </Tooltip>
                                                </TooltipProvider>
                                                <TooltipProvider>
                                                    <Tooltip>
                                                        <TooltipTrigger asChild>
                                                            <Button 
                                                                size="icon" 
                                                                variant="ghost" 
                                                                className="h-8 w-8 text-primary hover:text-primary hover:bg-primary/10" 
                                                                disabled={!hasChanged}
                                                                onClick={() => onSave(channel.id)}
                                                            >
                                                                <Save className="h-4 w-4" />
                                                                <span className="sr-only">Save</span>
                                                            </Button>
                                                        </TooltipTrigger>
                                                        <TooltipContent>
                                                            <p>保存修改</p>
                                                        </TooltipContent>
                                                    </Tooltip>
                                                </TooltipProvider>
                                                
                                                <TooltipProvider>
                                                    <Tooltip>
                                                        <TooltipTrigger asChild>
                                                            <AlertDialog>
                                                                <AlertDialogTrigger asChild>
                                                                    <Button size="icon" variant="ghost" className="h-8 w-8 text-destructive hover:text-destructive hover:bg-destructive/10">
                                                                        <Trash2 className="h-4 w-4" />
                                                                        <span className="sr-only">Delete</span>
                                                                    </Button>
                                                                </AlertDialogTrigger>
                                                                <AlertDialogContent>
                                                                    <AlertDialogHeader>
                                                                        <AlertDialogTitle>确认删除该频道？</AlertDialogTitle>
                                                                        <AlertDialogDescription>
                                                                            此操作将删除频道配置，可能会影响正在进行的任务。
                                                                        </AlertDialogDescription>
                                                                    </AlertDialogHeader>
                                                                    <AlertDialogFooter>
                                                                        <AlertDialogCancel>取消</AlertDialogCancel>
                                                                        <AlertDialogAction onClick={() => deleteChannel(channel.id)} className="bg-destructive hover:bg-destructive/90">
                                                                            删除
                                                                        </AlertDialogAction>
                                                                    </AlertDialogFooter>
                                                                </AlertDialogContent>
                                                            </AlertDialog>
                                                        </TooltipTrigger>
                                                        <TooltipContent>
                                                            <p>删除频道</p>
                                                        </TooltipContent>
                                                    </Tooltip>
                                                </TooltipProvider>
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

      <Dialog open={detailChannelId !== null} onOpenChange={(open) => { if (!open) setDetailChannelId(null) }}>
        <DialogContent className="max-w-[calc(100vw-2rem)] sm:max-w-[520px]">
          <DialogHeader>
            <DialogTitle>检测详情</DialogTitle>
            <DialogDescription>
              {selectedProbeChannel ? `${selectedProbeChannel.name} · 每次采样约 5 秒` : '暂无内容'}
            </DialogDescription>
          </DialogHeader>

          {selectedProbeChannel && (
            <div className="space-y-4">
              {selectedProbeChecking ? (
                <div className="flex items-center gap-3 rounded-lg border bg-muted/40 p-3 text-sm">
                  <Loader2 className="h-4 w-4 animate-spin text-primary" />
                  <span>正在连接频道并采样…</span>
                </div>
              ) : selectedProbe?.accessible ? (
                <div className="flex items-start gap-3 rounded-lg border border-green-500/30 bg-green-500/10 p-3 text-sm text-green-700 dark:text-green-400">
                  <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0" />
                  <div>
                    <div className="font-medium">频道可访问</div>
                    <div className="mt-0.5 text-xs opacity-80">已获取 MPEG-TS 与音视频轨道信息。</div>
                  </div>
                </div>
              ) : (
                <div className="flex items-start gap-3 rounded-lg border border-red-500/30 bg-red-500/10 p-3 text-sm text-red-700 dark:text-red-400">
                  <AlertCircle className="mt-0.5 h-4 w-4 shrink-0" />
                  <div>
                    <div className="font-medium">频道不可访问</div>
                    <div className="mt-0.5 text-xs opacity-80">{selectedProbe?.errorMessage ?? '未收到有效数据'}</div>
                  </div>
                </div>
              )}

              <div className="grid grid-cols-2 gap-3 text-sm">
                <div className="rounded-lg border p-3">
                  <div className="text-xs text-muted-foreground">视频编码</div>
                  <div className="mt-1 space-y-0.5 font-mono font-medium">
                    {selectedProbe?.videoTracks.length
                      ? selectedProbe.videoTracks.map((track) => <div key={track.pid}>{track.label}</div>)
                      : '—'}
                  </div>
                </div>
                <div className="rounded-lg border p-3">
                  <div className="text-xs text-muted-foreground">音频编码</div>
                  <div className="mt-1 space-y-0.5 font-mono font-medium">
                    {selectedProbe?.audioTracks.length
                      ? selectedProbe.audioTracks.map((track) => <div key={track.pid}>{track.label}</div>)
                      : '—'}
                  </div>
                </div>
                <div className="rounded-lg border p-3">
                  <div className="text-xs text-muted-foreground">分辨率</div>
                  <div className="mt-1 font-mono font-medium" title="当前 TS 检测暂不解析分辨率">
                    {selectedProbe?.resolution || '—'}
                  </div>
                </div>
                <div className="rounded-lg border p-3">
                  <div className="text-xs text-muted-foreground">实时码率</div>
                  <div className="mt-1 font-mono font-medium">{formatProbeBitrate(selectedProbe?.bitrateKbps)}</div>
                </div>
                <div className="rounded-lg border p-3">
                  <div className="text-xs text-muted-foreground">TS 检测</div>
                  <div className="mt-1 font-medium">
                    {selectedProbe?.tsVerdict
                      ? selectedProbe.tsVerdict === 'pass' ? '正常' : selectedProbe.tsVerdict === 'warning' ? '警告' : selectedProbe.tsVerdict === 'error' ? '异常' : '未完成'
                      : '—'}
                    {selectedProbe && (selectedProbe.errorCount > 0 || selectedProbe.warningCount > 0) && (
                      <span className="ml-1 text-xs text-muted-foreground">
                        错误 {selectedProbe.errorCount} · 警告 {selectedProbe.warningCount}
                      </span>
                    )}
                  </div>
                </div>
                <div className="rounded-lg border p-3">
                  <div className="text-xs text-muted-foreground">探测耗时</div>
                  <div className="mt-1 font-mono font-medium">
                    {selectedProbe ? `${selectedProbe.elapsedSeconds.toFixed(1)} s` : '—'}
                  </div>
                </div>
              </div>

              {selectedProbe && !selectedProbe.accessible && (
                <div className="rounded-lg border border-amber-500/30 bg-amber-500/10 p-3 text-xs text-amber-700 dark:text-amber-400">
                  <div className="font-medium">排查建议</div>
                  <div className="mt-1">{probeErrorHint(selectedProbeChannel.url)}</div>
                </div>
              )}

              <div className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground">
                <span>上次检测：{selectedProbe ? formatDateTime(new Date(selectedProbe.checkedAt)) : '尚未检测'}</span>
                {selectedProbe && <span>尝试次数：{selectedProbe.attempts ?? 1}</span>}
                {selectedProbe?.checkedAt && selectedProbe.tsVerdict === 'warning' && <span>TS 存在警告，但流本身可访问</span>}
              </div>
            </div>
          )}

          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => setDetailChannelId(null)}>关闭</Button>
            <Button
              type="button"
              onClick={() => { if (selectedProbeChannel) void runProbe([selectedProbeChannel.id]) }}
              disabled={selectedProbeChecking || !selectedProbeChannel}
            >
              {selectedProbeChecking ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <Activity className="mr-2 h-4 w-4" />}
              {selectedProbeChecking ? '检测中…' : '重新检测'}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <AlertDialog open={removeInvalidOpen} onOpenChange={setRemoveInvalidOpen}>
        <AlertDialogContent className="max-w-[calc(100vw-2rem)] sm:max-w-[480px]">
          <AlertDialogHeader>
            <AlertDialogTitle>移除失效频道？</AlertDialogTitle>
            <AlertDialogDescription asChild>
              <div className="space-y-2">
                <p>将删除 {invalidChannels.length} 个检测为不可访问的频道，可能会影响引用这些频道的任务。此操作无法撤销。</p>
                <div className="max-h-40 overflow-y-auto rounded-md border bg-muted/30 p-2 text-xs">
                  {invalidChannels.map((channel) => (
                    <div key={channel.id} className="truncate py-0.5">
                      <span className="mr-1 font-mono text-muted-foreground">#{channel.id}</span>
                      {channel.name}
                    </div>
                  ))}
                </div>
              </div>
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>取消</AlertDialogCancel>
            <AlertDialogAction
              onClick={() => void onRemoveInvalid()}
              className="bg-destructive hover:bg-destructive/90"
            >
              移除 {invalidChannels.length} 个频道
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>

      <AlertDialog open={bulkDeleteOpen} onOpenChange={setBulkDeleteOpen}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>确认删除选中的频道？</AlertDialogTitle>
            <AlertDialogDescription>
              将删除 {selectedCount} 个频道配置，可能会影响引用这些频道的任务。此操作无法撤销。
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>取消</AlertDialogCancel>
            <AlertDialogAction
              onClick={() => void onBulkDelete()}
              className="bg-destructive hover:bg-destructive/90"
            >
              删除频道
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  )
}

export default ChannelsPage
