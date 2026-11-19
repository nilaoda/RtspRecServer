import { useCallback, useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import * as z from 'zod'
import { Activity, AlertCircle, CheckCircle2, FileUp, FlaskConical, Info, Loader2, Plus, Save, Settings2, Square, Tags, Trash2 } from 'lucide-react'
import { toast } from 'sonner'

import { useAppContext } from '../../app/context'
import { bulkEditChannels, deleteChannels, startChannelScan, stopChannelScan } from '../../services/api'
import type { ChannelBulkEditItem, ChannelConfig, ChannelProbeResult } from '../../types'
import { formatDateTime } from '../shared/format'
import { formatProbeBitrate } from '../shared/channel-probe'
import { ChannelProtocolBadge } from '@/components/channel-protocol-badge'
import ChannelRecordingSimulationDialog from './ChannelRecordingSimulationDialog'
import { useChannelDiagnostics } from './useChannelDiagnostics'

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
const NEW_GROUP_VALUE = '__new_group__'

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

const hasChannelChanges = (channel: ChannelConfig, draft?: ChannelDraft) => !!draft && (
  draft.name !== channel.name || draft.url !== channel.url || draft.group !== (channel.group ?? '')
)

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
  const [detailChannelId, setDetailChannelId] = useState<number | null>(null)
  const [simulationChannelId, setSimulationChannelId] = useState<number | null>(null)
  const simulationChannel = channels.find((channel) => channel.id === simulationChannelId)
  const [selectedChannelIds, setSelectedChannelIds] = useState<Set<number>>(() => new Set())
  const [groupFilter, setGroupFilter] = useState(ALL_GROUPS)
  const [bulkDeleteOpen, setBulkDeleteOpen] = useState(false)
  const [probeConcurrency, setProbeConcurrency] = useState(() => readProbeSetting(PROBE_CONCURRENCY_KEY, 3, 1, 10))
  const [probeRetries, setProbeRetries] = useState(() => readProbeSetting(PROBE_RETRIES_KEY, 0, 0, 5))
  const [probeSettingsOpen, setProbeSettingsOpen] = useState(false)
  const [removeInvalidOpen, setRemoveInvalidOpen] = useState(false)
  const [addOpen, setAddOpen] = useState(false)
  const [newGroupChannelId, setNewGroupChannelId] = useState<number | null>(null)
  const [newGroupForAdd, setNewGroupForAdd] = useState(false)
  const [newGroupName, setNewGroupName] = useState('')
  const [bulkGroupOpen, setBulkGroupOpen] = useState(false)
  const [bulkGroupValue, setBulkGroupValue] = useState(UNGROUPED)
  const [bulkGroupNewName, setBulkGroupNewName] = useState('')
  const [groupManagerOpen, setGroupManagerOpen] = useState(false)
  const [renameDrafts, setRenameDrafts] = useState<Record<string, string>>({})
  const [groupDeleteTarget, setGroupDeleteTarget] = useState<string | null>(null)
  const [groupSubmitting, setGroupSubmitting] = useState(false)
  const [scanPending, setScanPending] = useState(false)
  const { snapshot, synced, refresh } = useChannelDiagnostics()
  const probeProgress = snapshot?.scan
  const scanning = probeProgress != null
  const scanUnavailable = !synced || scanPending
  const probes = useMemo(
    () => Object.fromEntries((snapshot?.probes ?? []).map((result) => [result.channelId, result])),
    [snapshot?.probes],
  )
  const probingIds = useMemo(() => new Set(probeProgress?.activeChannelIds), [probeProgress?.activeChannelIds])
  const simulations = useMemo(
    () => Object.fromEntries((snapshot?.simulations ?? []).map((run) => [run.channelId, run])),
    [snapshot?.simulations],
  )
  const scanId = probeProgress?.id

  const form = useForm<ChannelFormValues>({
    resolver: zodResolver(channelSchema),
    defaultValues: {
      name: '',
      url: '',
      group: '',
    },
  })
  const urlValue = form.watch('url')
  const groupValue = form.watch('group')

  useEffect(() => {
    if (scanId === undefined) void reloadChannelProfiles()
  }, [scanId, reloadChannelProfiles])

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

  const groupStats = useMemo(() => {
    const counts = new Map<string, number>()
    for (const channel of channels) {
      const group = channel.group?.trim()
      if (group) {
        counts.set(group, (counts.get(group) ?? 0) + 1)
      }
    }
    return [...counts.entries()].sort(([left], [right]) => left.localeCompare(right, 'zh-CN'))
  }, [channels])

  const addGroupOptions = groupValue && !groupOptions.includes(groupValue)
    ? [...groupOptions, groupValue].sort((left, right) => left.localeCompare(right, 'zh-CN'))
    : groupOptions

  useEffect(() => {
    if (!groupManagerOpen) {
      return
    }
    setRenameDrafts((previous) => {
      const next: Record<string, string> = {}
      let changed = false
      for (const [group] of groupStats) {
        next[group] = previous[group] ?? group
        if (next[group] !== previous[group]) {
          changed = true
        }
      }
      if (Object.keys(next).length !== Object.keys(previous).length) {
        changed = true
      }
      return changed ? next : previous
    })
  }, [groupManagerOpen, groupStats])

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
      if (hasChannelChanges(channel, draft)) {
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

  const runProbe = async (ids: number[]) => {
    if (ids.length === 0 || scanning || scanUnavailable) return

    setScanPending(true)
    try {
      await startChannelScan(ids, probeConcurrency, probeRetries)
    } catch {
      // 错误由 API 提示处理。
    } finally {
      await refresh()
      setScanPending(false)
    }
  }

  const stopProbe = async () => {
    if (!probeProgress || probeProgress.stopping || scanUnavailable) return

    setScanPending(true)
    try {
      await stopChannelScan(probeProgress.id)
    } catch {
      // 检测可能已在请求到达前结束，以后端快照为准。
    } finally {
      await refresh()
      setScanPending(false)
    }
  }

  const probeAll = () => void runProbe(channels.map((channel) => channel.id))
  const probeSelected = () => void runProbe(selectedChannels.map((channel) => channel.id))

  const assignGroupToChannels = useCallback(async (ids: number[], group: string | null) => {
    if (ids.length === 0) {
      return null
    }

    setGroupSubmitting(true)
    try {
      const items: ChannelBulkEditItem[] = ids.map((id) => group === null
        ? { id, clearGroup: true }
        : { id, group })
      const result = await bulkEditChannels(items)
      if (result.failed === 0) {
        toast.success(`已更新 ${result.succeeded} 个频道的分组`)
      } else if (result.succeeded > 0) {
        toast.info(`已更新 ${result.succeeded} 个频道，${result.failed} 个失败`)
      } else {
        toast.error('分组更新失败')
      }
      await reloadChannels()
      return result
    } catch {
      toast.error('分组更新失败')
      return null
    } finally {
      setGroupSubmitting(false)
    }
  }, [reloadChannels])

  const openGroupManager = () => {
    setRenameDrafts(Object.fromEntries(groupStats.map(([group]) => [group, group])))
    setGroupManagerOpen(true)
  }

  const onRenameGroup = async (oldGroup: string) => {
    const newName = (renameDrafts[oldGroup] ?? oldGroup).trim()
    if (!newName) {
      toast.error('分组名称不能为空')
      return
    }
    if (newName === oldGroup) {
      return
    }
    const ids = channels
      .filter((channel) => (channel.group?.trim() || '') === oldGroup)
      .map((channel) => channel.id)
    if (ids.length === 0) {
      return
    }
    await assignGroupToChannels(ids, newName)
  }

  const onDeleteGroup = async () => {
    const target = groupDeleteTarget
    if (!target) {
      return
    }
    const ids = channels
      .filter((channel) => (channel.group?.trim() || '') === target)
      .map((channel) => channel.id)
    setGroupDeleteTarget(null)
    await assignGroupToChannels(ids, null)
  }

  const onBulkGroupApply = async () => {
    const target = bulkGroupNewName.trim() || (bulkGroupValue === UNGROUPED ? null : bulkGroupValue)
    const result = await assignGroupToChannels(selectedChannels.map((channel) => channel.id), target)
    if (!result) {
      return
    }
    setBulkGroupOpen(false)
    if (result.failed === 0) {
      setSelectedChannelIds(new Set())
    } else {
      setSelectedChannelIds(new Set(result.failedItems.map((failure) => failure.id)))
    }
  }

  const closeNewGroupDialog = () => {
    setNewGroupChannelId(null)
    setNewGroupForAdd(false)
    setNewGroupName('')
  }

  const confirmNewGroup = () => {
    const name = newGroupName.trim()
    if (!name) {
      toast.error('分组名称不能为空')
      return
    }
    if (newGroupForAdd) {
      form.setValue('group', name, { shouldDirty: true, shouldValidate: true })
      closeNewGroupDialog()
      return
    }
    if (newGroupChannelId === null) {
      return
    }
    const channel = channels.find((item) => item.id === newGroupChannelId)
    if (!channel) {
      closeNewGroupDialog()
      return
    }
    const base = drafts[newGroupChannelId] ?? {
      name: channel.name,
      url: channel.url,
      group: channel.group ?? '',
    }
    setDrafts((previous) => ({
      ...previous,
      [newGroupChannelId]: { ...base, group: name },
    }))
    closeNewGroupDialog()
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
      await reloadChannels()
      void reloadChannelProfiles()
      void refresh()
    } catch {
      toast.error('批量删除失败')
    }
  }, [reloadChannels, reloadChannelProfiles, refresh])

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
        setAddOpen(false)
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
        if (original && original.url !== draft.url) void refresh()
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
    [channels, drafts, groupFilter, refresh, updateChannel],
  )

  return (
    <div className="flex min-h-0 flex-col gap-6 md:h-full">
      <Card className="flex flex-col md:min-h-0 md:flex-1">
        <CardHeader className="flex shrink-0 flex-col gap-3 pb-3 sm:flex-row sm:items-center sm:justify-between">
            <div>
                <CardTitle className="text-lg font-medium">频道列表</CardTitle>
                <div className="mt-1 flex flex-wrap items-center gap-x-3 gap-y-1 text-xs">
                    {snapshot && (
                        <>
                            <span className="text-green-600 dark:text-green-400">可访问 {probeSummary.accessible}</span>
                            <span className="text-red-600 dark:text-red-400">异常 {probeSummary.offline}</span>
                            <span className="text-muted-foreground">未检测 {probeSummary.idle}</span>
                        </>
                    )}
                    {!synced && <span className="text-muted-foreground">正在同步检测状态…</span>}
                    {scanning && probeProgress && (
                        <span className="flex items-center gap-1 text-primary">
                            <Loader2 className="h-3 w-3 animate-spin" />
                            {probeProgress.stopping ? '正在停止检测' : '检测中'} {probeProgress.done}/{probeProgress.total}
                        </span>
                    )}
                </div>
            </div>
            <div className="flex flex-wrap items-center gap-2">
                <Button size="sm" variant="outline" onClick={() => setAddOpen(true)}>
                    <Plus className="mr-2 h-4 w-4" />
                    添加频道
                </Button>
                <Button asChild variant="outline" size="sm">
                    <Link to="/channels/import">
                        <FileUp className="mr-2 h-4 w-4" />
                        导入 M3U
                    </Link>
                </Button>
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
                <Button size="sm" variant="outline" onClick={openGroupManager} disabled={scanning}>
                    <Tags className="mr-2 h-4 w-4" />
                    管理分组
                </Button>
                {invalidChannels.length > 0 && (
                    <Button
                        size="sm"
                        variant="outline"
                        className="text-destructive hover:text-destructive"
                        onClick={() => setRemoveInvalidOpen(true)}
                        disabled={scanning}
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
                {scanning ? (
                    <Button
                        size="sm"
                        variant="outline"
                        className="text-destructive hover:text-destructive"
                        onClick={() => void stopProbe()}
                        disabled={scanUnavailable || probeProgress.stopping}
                    >
                        <Square className="mr-2 h-4 w-4" />
                        停止检测
                    </Button>
                ) : (
                    <Button
                        size="sm"
                        onClick={probeAll}
                        disabled={channels.length === 0 || scanUnavailable}
                    >
                        <Activity className="mr-2 h-4 w-4" />
                        检测全部
                    </Button>
                )}
            </div>
        </CardHeader>
        <CardContent className="flex flex-col p-0 md:min-h-0 md:flex-1">
            <div className="flex shrink-0 flex-wrap items-center justify-between gap-2 border-b bg-muted/30 px-4 py-2">
                <span className="text-xs text-muted-foreground">
                    已选 <span className="font-medium text-foreground">{selectedCount}</span> 个频道
                </span>
                <div className="flex flex-wrap items-center gap-2">
                    <Button
                        size="sm"
                        variant="outline"
                        onClick={probeSelected}
                        disabled={selectedCount === 0 || scanning || scanUnavailable}
                    >
                        <Activity className="mr-2 h-4 w-4" />
                        检测选中
                    </Button>
                    <Button
                        size="sm"
                        variant="outline"
                        onClick={() => {
                            setBulkGroupValue(groupFilter !== ALL_GROUPS && groupFilter !== UNGROUPED ? groupFilter : UNGROUPED)
                            setBulkGroupNewName('')
                            setBulkGroupOpen(true)
                        }}
                        disabled={selectedCount === 0 || scanning}
                    >
                        <Tags className="mr-2 h-4 w-4" />
                        设置分组
                    </Button>
                    <Button
                        size="sm"
                        variant="outline"
                        className="text-destructive hover:text-destructive"
                        onClick={() => setBulkDeleteOpen(true)}
                        disabled={selectedCount === 0 || scanning}
                    >
                        <Trash2 className="mr-2 h-4 w-4" />
                        删除选中
                    </Button>
                    <Button
                        size="sm"
                        variant="ghost"
                        onClick={() => setSelectedChannelIds(new Set())}
                        disabled={selectedCount === 0}
                    >
                        取消选择
                    </Button>
                </div>
            </div>
            <div className="min-h-0 rounded-md md:flex-1">
                <Table containerClassName="max-h-[60vh] overscroll-contain md:max-h-none md:h-full">
                    <TableHeader className="[&_th]:sticky [&_th]:top-0 [&_th]:z-10 [&_th]:border-b [&_th]:bg-background">
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
                            <TableHead className="w-[130px]">分组</TableHead>
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
                                const rowGroups = draft.group && !groupOptions.includes(draft.group)
                                    ? [...groupOptions, draft.group].sort((left, right) => left.localeCompare(right, 'zh-CN'))
                                    : groupOptions
                                const hasChanged = hasChannelChanges(channel, draft)
                                const probe = probes[channel.id]
                                const checking = probingIds.has(channel.id)
                                const simulation = simulations[channel.id]
                                const simulationActive = simulation?.status === 'running' || simulation?.status === 'stopping'
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
                                            <Select
                                                value={draft.group || UNGROUPED}
                                                onValueChange={(value) => {
                                                    if (value === NEW_GROUP_VALUE) {
                                                        setNewGroupName('')
                                                        setNewGroupForAdd(false)
                                                        setNewGroupChannelId(channel.id)
                                                        return
                                                    }
                                                    setDrafts(prev => ({
                                                        ...prev,
                                                        [channel.id]: { ...draft, group: value === UNGROUPED ? '' : value }
                                                    }))
                                                }}
                                            >
                                                <SelectTrigger className="h-8 w-full text-xs" aria-label={`设置分组 ${channel.name}`}>
                                                    <SelectValue placeholder="未分组" />
                                                </SelectTrigger>
                                                <SelectContent>
                                                    <SelectItem value={UNGROUPED}>未分组</SelectItem>
                                                    {rowGroups.map((group) => (
                                                        <SelectItem key={group} value={group}>{group}</SelectItem>
                                                    ))}
                                                    <SelectItem value={NEW_GROUP_VALUE}>新建分组…</SelectItem>
                                                </SelectContent>
                                            </Select>
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
                                                    </div>
                                                    {probe.accessible ? (
                                                        <div className="text-[11px] text-muted-foreground">
                                                            {formatProbeBitrate(probe.bitrateKbps)}
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
                                                <span className="text-xs text-muted-foreground">{snapshot ? '未检测' : '—'}</span>
                                            )}
                                        </TableCell>
                                        <TableCell className="text-right py-2">
                                            <div className="flex items-center justify-end gap-1">
                                                {simulationActive && (
                                                    <Button variant="ghost" size="sm" className="h-8 shrink-0 px-2 text-xs text-primary" onClick={() => setSimulationChannelId(channel.id)}>
                                                        <Loader2 className="h-3.5 w-3.5 animate-spin" />
                                                        {simulation.status === 'stopping' ? '正在停止模拟' : '模拟录制中'}
                                                    </Button>
                                                )}
                                                <TooltipProvider>
                                                    <Tooltip>
                                                        <TooltipTrigger asChild>
                                                            <Button
                                                                size="icon"
                                                                variant="ghost"
                                                                className="h-8 w-8"
                                                                disabled={checking || hasChanged || scanning || scanUnavailable}
                                                                onClick={() => void runProbe([channel.id])}
                                                            >
                                                                {checking ? <Loader2 className="h-4 w-4 animate-spin" /> : <Activity className="h-4 w-4" />}
                                                                <span className="sr-only">检测</span>
                                                            </Button>
                                                        </TooltipTrigger>
                                                        <TooltipContent>
                                                            <p>{scanning ? '检测进行中' : hasChanged ? '请先保存频道修改' : '检测频道'}</p>
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
                                                                className="h-8 w-8"
                                                                disabled={hasChanged}
                                                                onClick={() => setSimulationChannelId(channel.id)}
                                                            >
                                                                <FlaskConical className="h-4 w-4" />
                                                                <span className="sr-only">模拟录制</span>
                                                            </Button>
                                                        </TooltipTrigger>
                                                        <TooltipContent>
                                                            <p>{hasChanged ? '请先保存频道修改' : '模拟录制'}</p>
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

      <Dialog
        open={newGroupChannelId !== null || newGroupForAdd}
        onOpenChange={(open) => {
          if (!open) {
            closeNewGroupDialog()
          }
        }}
      >
        <DialogContent className="max-w-[calc(100vw-2rem)] sm:max-w-[420px]">
          <DialogHeader>
            <DialogTitle>新建分组</DialogTitle>
            <DialogDescription>
              {newGroupForAdd ? '输入新分组名称后，将作为当前频道的分组。' : '输入新分组名称后，保存该行即可生效。'}
            </DialogDescription>
          </DialogHeader>
          <form
            onSubmit={(event) => {
              event.preventDefault()
              confirmNewGroup()
            }}
            className="space-y-4"
          >
            <Input
              autoFocus
              autoComplete="off"
              value={newGroupName}
              onChange={(event) => setNewGroupName(event.target.value)}
              placeholder="新分组名称"
            />
            <DialogFooter className="gap-2 sm:gap-0">
              <Button type="button" variant="outline" onClick={closeNewGroupDialog}>
                取消
              </Button>
              <Button type="submit">确定</Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      <Dialog open={bulkGroupOpen} onOpenChange={setBulkGroupOpen}>
        <DialogContent className="max-w-[calc(100vw-2rem)] sm:max-w-[440px]">
          <DialogHeader>
            <DialogTitle>设置分组</DialogTitle>
            <DialogDescription>为选中的 {selectedCount} 个频道设置分组。</DialogDescription>
          </DialogHeader>
          <div className="space-y-3">
            <div className="space-y-1.5">
              <label className="text-xs text-muted-foreground">选择已有分组</label>
              <Select value={bulkGroupValue} onValueChange={setBulkGroupValue}>
                <SelectTrigger className="h-9">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={UNGROUPED}>未分组</SelectItem>
                  {groupOptions.map((group) => (
                    <SelectItem key={group} value={group}>{group}</SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            <div className="space-y-1.5">
              <label className="text-xs text-muted-foreground">或输入新分组名称（优先）</label>
              <Input
                value={bulkGroupNewName}
                onChange={(event) => setBulkGroupNewName(event.target.value)}
                placeholder="留空则使用上方选择"
                autoComplete="off"
              />
            </div>
          </div>
          <DialogFooter className="gap-2 sm:gap-0">
            <Button variant="outline" onClick={() => setBulkGroupOpen(false)} disabled={groupSubmitting}>
              取消
            </Button>
            <Button onClick={() => void onBulkGroupApply()} disabled={groupSubmitting || selectedCount === 0}>
              {groupSubmitting ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <Tags className="mr-2 h-4 w-4" />}
              应用
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <Dialog open={groupManagerOpen} onOpenChange={setGroupManagerOpen}>
        <DialogContent className="max-w-[calc(100vw-2rem)] sm:max-w-[560px]">
          <DialogHeader>
            <DialogTitle>管理分组</DialogTitle>
            <DialogDescription>重命名会同步更新该组所有频道；删除后频道归入未分组。</DialogDescription>
          </DialogHeader>
          <div className="max-h-[55vh] space-y-3 overflow-y-auto p-1.5">
            {groupStats.length === 0 ? (
              <p className="py-4 text-center text-sm text-muted-foreground">暂无分组</p>
            ) : groupStats.map(([group, count]) => (
              <div key={group} className="flex items-center gap-2">
                <Input
                  value={renameDrafts[group] ?? group}
                  onChange={(event) => setRenameDrafts((previous) => ({
                    ...previous,
                    [group]: event.target.value,
                  }))}
                  className="h-8"
                  aria-label={`分组名称 ${group}`}
                  autoComplete="off"
                />
                <span className="w-14 shrink-0 text-right text-xs text-muted-foreground">{count} 个</span>
                <Button
                  size="icon"
                  variant="ghost"
                  className="h-8 w-8"
                  disabled={
                    groupSubmitting
                    || !(renameDrafts[group] ?? group).trim()
                    || (renameDrafts[group] ?? group).trim() === group
                  }
                  onClick={() => void onRenameGroup(group)}
                  title="保存重命名"
                >
                  <Save className="h-4 w-4" />
                </Button>
                <Button
                  size="icon"
                  variant="ghost"
                  className="h-8 w-8 text-destructive hover:text-destructive"
                  disabled={groupSubmitting}
                  onClick={() => setGroupDeleteTarget(group)}
                  title="删除分组"
                >
                  <Trash2 className="h-4 w-4" />
                </Button>
              </div>
            ))}
          </div>
          <p className="text-[11px] leading-relaxed text-muted-foreground">
            新建分组：在“添加频道”或“设置分组”时输入新名称。
          </p>
          <DialogFooter>
            <Button variant="outline" onClick={() => setGroupManagerOpen(false)}>
              关闭
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <AlertDialog open={groupDeleteTarget !== null} onOpenChange={(open) => { if (!open) setGroupDeleteTarget(null) }}>
        <AlertDialogContent className="max-w-[calc(100vw-2rem)] sm:max-w-[440px]">
          <AlertDialogHeader>
            <AlertDialogTitle>删除分组？</AlertDialogTitle>
            <AlertDialogDescription>
              「{groupDeleteTarget}」下的 {groupStats.find(([group]) => group === groupDeleteTarget)?.[1] ?? 0} 个频道将归入未分组，此操作无法撤销。
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>取消</AlertDialogCancel>
            <AlertDialogAction className="bg-destructive hover:bg-destructive/90" onClick={() => void onDeleteGroup()}>
              删除分组
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>

      <Dialog open={addOpen} onOpenChange={(open) => { setAddOpen(open); if (!open) form.reset() }}>
        <DialogContent className="max-w-[calc(100vw-2rem)] sm:max-w-[480px]">
          <DialogHeader>
            <DialogTitle>添加频道</DialogTitle>
            <DialogDescription>填写频道名称、分组和地址。</DialogDescription>
          </DialogHeader>
          <Form {...form}>
            <form onSubmit={form.handleSubmit(onAdd)} className="space-y-4">
              <FormField
                control={form.control}
                name="name"
                render={({ field }) => (
                  <FormItem>
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
                  <FormItem>
                    <FormLabel>分组（可选）</FormLabel>
                    <Select
                      value={field.value || UNGROUPED}
                      onValueChange={(value) => {
                        if (value === NEW_GROUP_VALUE) {
                          setNewGroupName('')
                          setNewGroupChannelId(null)
                          setNewGroupForAdd(true)
                          return
                        }
                        field.onChange(value === UNGROUPED ? '' : value)
                      }}
                    >
                      <FormControl>
                        <SelectTrigger>
                          <SelectValue placeholder="未分组" />
                        </SelectTrigger>
                      </FormControl>
                      <SelectContent>
                        <SelectItem value={UNGROUPED}>未分组</SelectItem>
                        {addGroupOptions.map((group) => (
                          <SelectItem key={group} value={group}>{group}</SelectItem>
                        ))}
                        <SelectItem value={NEW_GROUP_VALUE}>新建分组…</SelectItem>
                      </SelectContent>
                    </Select>
                    <FormMessage />
                  </FormItem>
                )}
              />
              <FormField
                control={form.control}
                name="url"
                render={({ field }) => (
                  <FormItem>
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
              <DialogFooter className="gap-2 sm:gap-0">
                <Button type="button" variant="outline" onClick={() => setAddOpen(false)}>
                  取消
                </Button>
                <Button type="submit">
                  <Plus className="mr-2 h-4 w-4" />
                  添加频道
                </Button>
              </DialogFooter>
            </form>
          </Form>
        </DialogContent>
      </Dialog>

      {simulationChannel && (
        <ChannelRecordingSimulationDialog
          channel={simulationChannel}
          update={simulations[simulationChannel.id]}
          hasChanges={hasChannelChanges(simulationChannel, drafts[simulationChannel.id])}
          synced={synced}
          onRefresh={refresh}
          onClose={() => setSimulationChannelId(null)}
        />
      )}

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
              </div>
            </div>
          )}

          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => setDetailChannelId(null)}>关闭</Button>
            <Button
              type="button"
              onClick={() => { if (selectedProbeChannel) void runProbe([selectedProbeChannel.id]) }}
              disabled={selectedProbeChecking || !selectedProbeChannel || scanning || scanUnavailable}
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
