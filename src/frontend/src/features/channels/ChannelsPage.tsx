import { useCallback, useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import * as z from 'zod'
import { Activity, AlertCircle, CheckCircle2, FileUp, Info, Loader2, Plus, Save, Trash2 } from 'lucide-react'
import { toast } from 'sonner'

import { useAppContext } from '../../app/context'
import { getChannelProbes, probeChannels } from '../../services/api'
import type { ChannelProbeResult } from '../../types'
import { formatDateTime } from '../shared/format'
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

const channelSchema = z.object({
  name: z.string().min(1, "请输入频道名称"),
  url: z.string().min(1, "请输入频道地址"),
})

type ChannelFormValues = z.infer<typeof channelSchema>

const formatProbeBitrate = (kbps?: number) => {
  if (!kbps || kbps <= 0) {
    return '码率未识别'
  }
  return kbps >= 1000 ? `${(kbps / 1000).toFixed(1)} Mbps` : `${Math.round(kbps)} Kb/s`
}

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
  const { channels, addChannel, updateChannel, deleteChannel } = useAppContext()
  const [drafts, setDrafts] = useState<Record<number, { name: string; url: string }>>({})
  const [probes, setProbes] = useState<Record<number, ChannelProbeResult>>({})
  const [probingIds, setProbingIds] = useState<Set<number>>(() => new Set())
  const [detailChannelId, setDetailChannelId] = useState<number | null>(null)

  const form = useForm<ChannelFormValues>({
    resolver: zodResolver(channelSchema),
    defaultValues: {
      name: '',
      url: '',
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
      const results = await probeChannels(ids)
      setProbes((previous) => {
        const next = { ...previous }
        results.forEach((result) => {
          next[result.channelId] = result
        })
        return next
      })
    } catch {
      toast.error('频道检测失败，请检查服务端状态')
    } finally {
      setProbingIds((previous) => {
        const next = new Set(previous)
        ids.forEach((id) => next.delete(id))
        return next
      })
    }
  }, [])

  const probeAll = () => {
    if (probingIds.size > 0) {
      return
    }
    void runProbe(channels.map((channel) => channel.id))
  }

  const selectedProbeChannel = channels.find((channel) => channel.id === detailChannelId)
  const selectedProbe = detailChannelId === null ? undefined : probes[detailChannelId]
  const selectedProbeChecking = detailChannelId !== null && probingIds.has(detailChannelId)

  const onAdd = async (values: ChannelFormValues) => {
    try {
        await addChannel({ name: values.name.trim(), url: values.url.trim() })
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
      try {
        await updateChannel({ id, name: draft.name.trim(), url: draft.url.trim() })
        setDrafts((prev) => {
          const next = { ...prev }
          delete next[id]
          return next
        })
        setProbes((prev) => {
          const next = { ...prev }
          delete next[id]
          return next
        })
      } catch {
          // Handled in context
      }
    },
    [drafts, updateChannel],
  )

  return (
    <div className="space-y-6">
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
            <Button
                size="sm"
                onClick={probeAll}
                disabled={channels.length === 0 || probingIds.size > 0}
            >
                {probingIds.size > 0 ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <Activity className="mr-2 h-4 w-4" />}
                {probingIds.size > 0 ? '检测中' : '检测全部'}
            </Button>
        </CardHeader>
        <CardContent className="p-0">
            <div className="rounded-md border-t">
                <Table>
                    <TableHeader>
                        <TableRow>
                            <TableHead className="w-[64px]">ID</TableHead>
                            <TableHead className="w-[230px]">名称</TableHead>
                            <TableHead className="w-[72px]">协议</TableHead>
                            <TableHead className="min-w-[260px]">地址</TableHead>
                            <TableHead className="min-w-[230px]">检测</TableHead>
                            <TableHead className="w-[200px] text-right">操作</TableHead>
                        </TableRow>
                    </TableHeader>
                    <TableBody>
                        {channels.length === 0 ? (
                            <TableRow>
                                <TableCell colSpan={6} className="h-24 text-center">
                                    暂无频道
                                </TableCell>
                            </TableRow>
                        ) : (
                            channels.map((channel) => {
                                const draft = drafts[channel.id] ?? channel
                                const hasChanged = draft.name !== channel.name || draft.url !== channel.url
                                const probe = probes[channel.id]
                                const checking = probingIds.has(channel.id)
                                
                                return (
                                    <TableRow key={channel.id}>
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
                                                        <div>正在采样约 5 秒</div>
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
              {selectedProbeChannel ? `${selectedProbeChannel.name} · 采样约 5 秒` : '暂无内容'}
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
    </div>
  )
}

export default ChannelsPage
