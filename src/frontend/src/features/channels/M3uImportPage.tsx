import { useMemo, useState, type DragEvent as ReactDragEvent } from 'react'
import { Link } from 'react-router-dom'
import {
  AlertTriangle,
  ArrowLeft,
  Check,
  ChevronRight,
  Copy,
  FileText,
  FileUp,
  Folder,
  Loader2,
  RefreshCw,
  Search,
  Upload,
  X,
} from 'lucide-react'
import { toast } from 'sonner'

import { useAppContext } from '../../app/context'
import { importChannels } from '../../services/api'
import { ChannelProtocolBadge } from '@/components/channel-protocol-badge'
import { parseM3u, readM3uFile, type ParsedM3uChannel } from './m3u'

import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import { cn } from '@/lib/utils'

const AVATAR_CLASSES = [
  'bg-blue-500/10 text-blue-600 dark:text-blue-400',
  'bg-emerald-500/10 text-emerald-600 dark:text-emerald-400',
  'bg-amber-500/10 text-amber-600 dark:text-amber-400',
  'bg-violet-500/10 text-violet-600 dark:text-violet-400',
  'bg-rose-500/10 text-rose-600 dark:text-rose-400',
]

const typeLabel: Record<ParsedM3uChannel['type'], string> = {
  live: '直播',
  playback: '回放',
  unknown: '未知',
}

const statusLabel: Record<ParsedM3uChannel['status'], string> = {
  new: '新频道',
  existing: '已存在',
}

const typeBadgeClass = (type: ParsedM3uChannel['type']) => {
  if (type === 'live') return 'border-primary/30 bg-primary/10 text-primary'
  if (type === 'playback') return 'border-border bg-muted text-muted-foreground'
  return 'border-amber-500/30 bg-amber-500/10 text-amber-600 dark:text-amber-400'
}

const statusBadgeClass = (status: ParsedM3uChannel['status']) =>
  status === 'existing'
    ? 'border-amber-500/30 bg-amber-500/10 text-amber-600 dark:text-amber-400'
    : 'border-green-500/30 bg-green-500/10 text-green-600 dark:text-green-400'

const initialOf = (name: string) => Array.from(name.trim())[0] ?? '?'

const avatarClass = (name: string) => {
  let hash = 0
  for (const char of name) {
    hash = (hash * 31 + char.charCodeAt(0)) >>> 0
  }
  return AVATAR_CLASSES[hash % AVATAR_CLASSES.length]
}

type ImportResult = {
  success: number
  failed: number
}

const M3uImportPage = () => {
  const { channels, reloadChannels } = useAppContext()
  const [parsed, setParsed] = useState(false)
  const [fileName, setFileName] = useState('')
  const [items, setItems] = useState<ParsedM3uChannel[]>([])
  const [selected, setSelected] = useState<Set<string>>(() => new Set())
  const [query, setQuery] = useState('')
  const [group, setGroup] = useState('all')
  const [invalidCount, setInvalidCount] = useState(0)
  const [dragging, setDragging] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [importing, setImporting] = useState(false)
  const [result, setResult] = useState<ImportResult | null>(null)
  const [copiedId, setCopiedId] = useState<string | null>(null)

  const groups = useMemo(
    () => Array.from(new Set(items.map((item) => item.group))).sort((left, right) => left.localeCompare(right, 'zh-CN')),
    [items],
  )
  const selectedCount = selected.size
  const existingCount = useMemo(() => items.filter((item) => item.status === 'existing').length, [items])
  const normalizedQuery = query.trim().toLowerCase()
  const filtered = useMemo(
    () =>
      items.filter((item) => {
        const matchesGroup = group === 'all' || item.group === group
        const matchesQuery =
          !normalizedQuery ||
          item.name.toLowerCase().includes(normalizedQuery) ||
          item.tvgName.toLowerCase().includes(normalizedQuery) ||
          item.url.toLowerCase().includes(normalizedQuery)
        return matchesGroup && matchesQuery
      }),
    [group, items, normalizedQuery],
  )
  const allVisibleSelected =
    filtered.length > 0 && filtered.every((item) => selected.has(item.id))

  const handleFile = async (file?: File) => {
    if (!file || importing) {
      return
    }

    if (!/\.m3u8?$/i.test(file.name)) {
      setError('请选择 .m3u 或 .m3u8 文件')
      return
    }

    if (file.size > 10 * 1024 * 1024) {
      setError('文件过大，请选择 10 MB 以内的播放列表')
      return
    }

    try {
      const content = await readM3uFile(file)
      const existingUrls = new Set(channels.map((channel) => channel.url.trim()))
      const parsedResult = parseM3u(content, existingUrls)

      if (parsedResult.channels.length === 0) {
        setParsed(false)
        setFileName(file.name)
        setItems([])
        setSelected(new Set())
        setQuery('')
        setGroup('all')
        setInvalidCount(parsedResult.invalidCount)
        setError('没有识别到有效频道，请检查 M3U 格式或地址协议。')
        setResult(null)
        return
      }

      setParsed(true)
      setFileName(file.name)
      setItems(parsedResult.channels)
      setSelected(new Set(parsedResult.channels.map((item) => item.id)))
      setQuery('')
      setGroup('all')
      setInvalidCount(parsedResult.invalidCount)
      setError(null)
      setResult(null)
    } catch {
      setError('文件读取失败，请重试。')
    }
  }

  const clearFile = () => {
    if (importing) {
      return
    }
    setParsed(false)
    setFileName('')
    setItems([])
    setSelected(new Set())
    setQuery('')
    setGroup('all')
    setInvalidCount(0)
    setError(null)
    setResult(null)
  }

  const toggleItem = (id: string) => {
    if (importing) {
      return
    }
    setSelected((current) => {
      const next = new Set(current)
      if (next.has(id)) {
        next.delete(id)
      } else {
        next.add(id)
      }
      return next
    })
  }

  const toggleVisible = () => {
    if (importing) {
      return
    }
    setSelected((current) => {
      const next = new Set(current)
      if (allVisibleSelected) {
        filtered.forEach((item) => next.delete(item.id))
      } else {
        filtered.forEach((item) => next.add(item.id))
      }
      return next
    })
  }

  const selectAll = () => {
    if (!importing) {
      setSelected(new Set(items.map((item) => item.id)))
    }
  }

  const selectNewOnly = () => {
    if (!importing) {
      setSelected(new Set(items.filter((item) => item.status === 'new').map((item) => item.id)))
    }
  }

  const clearSelection = () => {
    if (!importing) {
      setSelected(new Set())
    }
  }

  const copyUrl = async (item: ParsedM3uChannel) => {
    try {
      await navigator.clipboard?.writeText(item.url)
    } catch {
      // 浏览器可能没有剪贴板权限。
    }
    setCopiedId(item.id)
    window.setTimeout(() => {
      setCopiedId((current) => (current === item.id ? null : current))
    }, 1400)
  }

  const handleImport = async () => {
    if (importing) {
      return
    }

    const targets = items.filter((item) => selected.has(item.id))
    if (targets.length === 0) {
      return
    }

    setImporting(true)
    setResult(null)

    try {
      const response = await importChannels(
        targets.map((item) => ({ name: item.name, url: item.url })),
      )
      const failed = Math.max(0, targets.length - response.addedCount)
      setResult({ success: response.addedCount, failed })
      if (failed === 0) {
        toast.success(`已批量导入 ${response.addedCount} 个频道`)
      } else {
        toast.info(`已导入 ${response.addedCount} 个频道，${failed} 个未导入`)
      }
    } catch (error) {
      setResult({ success: 0, failed: targets.length })
      toast.error(error instanceof Error ? error.message : '批量导入失败，请检查服务端状态')
    } finally {
      await reloadChannels()
      setImporting(false)
    }
  }

  const dragHandlers = {
    onDragOver: (event: ReactDragEvent<HTMLElement>) => {
      event.preventDefault()
      if (!importing) setDragging(true)
    },
    onDragLeave: () => setDragging(false),
    onDrop: (event: ReactDragEvent<HTMLElement>) => {
      event.preventDefault()
      setDragging(false)
      void handleFile(event.dataTransfer.files?.[0])
    },
  }

  return (
    <div className="space-y-5 pb-4">
      <input
        id="m3u-file-input"
        type="file"
        accept=".m3u,.m3u8,audio/x-mpegurl,application/vnd.apple.mpegurl"
        className="hidden"
        onChange={(event) => {
          const file = event.currentTarget.files?.[0]
          event.currentTarget.value = ''
          void handleFile(file)
        }}
      />

      <div className="flex items-center gap-1.5 text-xs text-muted-foreground">
        <Link to="/channels" className="transition-colors hover:text-foreground">
          频道配置
        </Link>
        <ChevronRight className="h-3.5 w-3.5" />
        <span className="text-foreground">导入 M3U</span>
      </div>

      <div className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
        <div>
          <div className="flex flex-wrap items-center gap-2">
            <h2 className="flex items-center gap-2 text-2xl font-semibold tracking-tight">
              <FileUp className="h-6 w-6 text-primary" />
              导入 M3U 频道
            </h2>
            <Badge variant="outline" className="text-[10px] font-normal">
              纯前端解析
            </Badge>
          </div>
          <p className="mt-1 max-w-2xl text-sm text-muted-foreground">
            拖入播放列表，识别频道后选择要加入频道配置的项。文件只在浏览器本地读取，不会上传。
          </p>
        </div>
        <Button asChild variant="outline">
          <Link to="/channels">
            <ArrowLeft className="mr-2 h-4 w-4" />
            返回频道配置
          </Link>
        </Button>
      </div>

      {!parsed ? (
        <Card
          className={cn(
            'border-dashed transition-colors',
            dragging && 'border-primary bg-primary/5',
          )}
          {...dragHandlers}
        >
          <CardContent className="flex flex-col items-center justify-center px-6 py-12 text-center">
            <div className="mb-4 flex h-12 w-12 items-center justify-center rounded-xl border border-primary/20 bg-primary/10 text-primary">
              <Upload className="h-6 w-6" />
            </div>
            <h3 className="text-base font-semibold">
              {dragging ? '松开即可解析文件' : '把 M3U 文件拖到这里'}
            </h3>
            <p className="mt-1 max-w-lg text-sm text-muted-foreground">
              支持 .m3u / .m3u8，自动兼容 UTF-8 与 GB18030 编码。也可以点击选择文件。
            </p>
            <div className="mt-5">
              <Button asChild>
                <label htmlFor="m3u-file-input" className="cursor-pointer">
                  <FileText className="mr-2 h-4 w-4" />
                  选择文件
                </label>
              </Button>
            </div>
            <p className="mt-3 text-xs text-muted-foreground">本地解析，不上传文件</p>
          </CardContent>
        </Card>
      ) : (
        <Card
          className={cn(
            'border-dashed transition-colors',
            dragging && 'border-primary bg-primary/5',
          )}
          {...dragHandlers}
        >
          <CardContent className="flex flex-col gap-3 p-3 sm:flex-row sm:items-center">
            <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg border bg-muted/40 text-primary">
              <FileText className="h-5 w-5" />
            </div>
            <div className="min-w-0 flex-1">
              <div className="flex min-w-0 items-center gap-2">
                <span className="truncate text-sm font-medium">{fileName}</span>
                <Badge variant="outline" className="shrink-0 text-[10px]">M3U</Badge>
              </div>
              <div className="mt-1 flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted-foreground">
                <span><span className="font-medium text-foreground">{items.length}</span> 频道</span>
                <span><span className="font-medium text-foreground">{groups.length}</span> 分组</span>
                <span className={existingCount > 0 ? 'text-amber-600 dark:text-amber-400' : undefined}>
                  {existingCount} 已存在
                </span>
                {invalidCount > 0 && <span>{invalidCount} 条已忽略</span>}
              </div>
            </div>
            <div className="flex shrink-0 flex-wrap items-center gap-2">
              <Button asChild variant="outline" size="sm">
                <label
                  htmlFor="m3u-file-input"
                  className={cn('cursor-pointer', importing && 'pointer-events-none opacity-50')}
                >
                  <RefreshCw className="mr-1.5 h-3.5 w-3.5" />
                  更换文件
                </label>
              </Button>
              <Button variant="ghost" size="sm" onClick={clearFile} disabled={importing}>
                <X className="mr-1.5 h-3.5 w-3.5" />
                清空
              </Button>
            </div>
          </CardContent>
        </Card>
      )}

      {error && (
        <div className="flex items-start gap-2 rounded-lg border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-sm text-amber-700 dark:text-amber-400">
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
          <span className="flex-1">{error}</span>
          <button type="button" className="text-xs underline" onClick={() => setError(null)}>
            关闭
          </button>
        </div>
      )}

      {parsed && (
        <>
          {existingCount > 0 && !result && (
            <div className="flex items-start gap-2 rounded-lg border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-sm text-amber-700 dark:text-amber-400">
              <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
              <span>
                检测到 {existingCount} 个已存在频道，默认仍选中；可点“仅选新频道”快速排除。
              </span>
            </div>
          )}

          {result && (
            <div
              className={cn(
                'flex items-start gap-2 rounded-lg border px-3 py-2 text-sm',
                result.failed === 0
                  ? 'border-green-500/30 bg-green-500/10 text-green-700 dark:text-green-400'
                  : 'border-amber-500/30 bg-amber-500/10 text-amber-700 dark:text-amber-400',
              )}
            >
              {result.failed === 0 ? (
                <Check className="mt-0.5 h-4 w-4 shrink-0" />
              ) : (
                <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
              )}
              <span>
                已导入 {result.success} 个频道
                {result.failed > 0 ? `，${result.failed} 个失败` : ''}。
              </span>
            </div>
          )}

          <Card>
            <CardHeader className="flex flex-col gap-3 pb-3 sm:flex-row sm:items-start sm:justify-between">
              <div>
                <CardTitle className="text-lg font-medium">识别结果</CardTitle>
                <p className="mt-1 text-sm text-muted-foreground">
                  默认全选。分组与 Logo 只用于预览，不会写入频道配置。
                </p>
              </div>
              <Badge variant="secondary" className="w-fit shrink-0">
                已选 {selectedCount} / {items.length}
              </Badge>
            </CardHeader>
            <CardContent className="p-0">
              <div className="flex flex-col gap-2 border-b p-3 sm:flex-row sm:items-center">
                <div className="relative min-w-[200px] flex-1 sm:max-w-[340px]">
                  <Search className="pointer-events-none absolute left-2.5 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
                  <Input
                    value={query}
                    onChange={(event) => setQuery(event.target.value)}
                    placeholder="搜索频道名称或地址"
                    className="h-8 pl-8"
                  />
                </div>
                <Select value={group} onValueChange={setGroup}>
                  <SelectTrigger className="h-8 w-full sm:w-[150px]">
                    <Folder className="mr-1.5 h-3.5 w-3.5 text-muted-foreground" />
                    <SelectValue placeholder="全部分组" />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="all">全部分组</SelectItem>
                    {groups.map((item) => (
                      <SelectItem key={item} value={item}>{item}</SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                <div className="flex flex-1 flex-wrap items-center justify-end gap-1">
                  <span className="mr-1 text-xs text-muted-foreground">
                    显示 {filtered.length} / {items.length}
                  </span>
                  <Button variant="ghost" size="sm" onClick={selectAll} disabled={importing}>
                    全选
                  </Button>
                  <Button variant="ghost" size="sm" onClick={selectNewOnly} disabled={importing}>
                    仅选新频道
                  </Button>
                  <Button variant="ghost" size="sm" onClick={clearSelection} disabled={importing}>
                    清空
                  </Button>
                </div>
              </div>

              <div className="hidden max-h-[480px] overflow-auto md:block">
                <Table>
                  <TableHeader className="sticky top-0 z-10 bg-card">
                    <TableRow>
                      <TableHead className="w-10">
                        <input
                          type="checkbox"
                          className="h-4 w-4 accent-primary"
                          checked={allVisibleSelected}
                          disabled={filtered.length === 0 || importing}
                          onChange={toggleVisible}
                          aria-label="选择当前筛选结果"
                        />
                      </TableHead>
                      <TableHead className="min-w-[220px]">频道</TableHead>
                      <TableHead className="w-[100px]">分组</TableHead>
                      <TableHead className="w-[72px]">类型</TableHead>
                      <TableHead className="w-[64px]">协议</TableHead>
                      <TableHead className="min-w-[260px]">地址</TableHead>
                      <TableHead className="w-[86px]">状态</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {filtered.length === 0 ? (
                      <TableRow>
                        <TableCell colSpan={7} className="h-24 text-center text-muted-foreground">
                          没有匹配的频道
                        </TableCell>
                      </TableRow>
                    ) : (
                      filtered.map((item) => {
                        const checked = selected.has(item.id)
                        return (
                          <TableRow
                            key={item.id}
                            className={checked ? 'bg-primary/5' : undefined}
                            onClick={() => toggleItem(item.id)}
                          >
                            <TableCell>
                              <input
                                type="checkbox"
                                className="h-4 w-4 accent-primary"
                                checked={checked}
                                disabled={importing}
                                onClick={(event) => event.stopPropagation()}
                                onChange={() => toggleItem(item.id)}
                                aria-label={`选择 ${item.name}`}
                              />
                            </TableCell>
                            <TableCell>
                              <div className="flex min-w-0 items-center gap-2.5">
                                <div className={cn('flex h-8 w-8 shrink-0 items-center justify-center rounded-md text-xs font-semibold', avatarClass(item.name))}>
                                  {initialOf(item.name)}
                                </div>
                                <div className="min-w-0">
                                  <div className="truncate text-sm font-medium">{item.name}</div>
                                  {item.tvgName && item.tvgName !== item.name && (
                                    <div className="truncate text-xs text-muted-foreground">{item.tvgName}</div>
                                  )}
                                </div>
                              </div>
                            </TableCell>
                            <TableCell>
                              <Badge variant="outline" className="max-w-[100px] truncate text-[10px]">
                                {item.group}
                              </Badge>
                            </TableCell>
                            <TableCell>
                              <Badge variant="outline" className={cn('text-[10px]', typeBadgeClass(item.type))}>
                                {typeLabel[item.type]}
                              </Badge>
                            </TableCell>
                            <TableCell>
                              <ChannelProtocolBadge url={item.url} />
                            </TableCell>
                            <TableCell>
                              <div className="flex min-w-0 items-center gap-1">
                                <span className="truncate font-mono text-xs text-muted-foreground" title={item.url}>
                                  {item.url}
                                </span>
                                <Button
                                  variant="ghost"
                                  size="icon"
                                  className="h-7 w-7 shrink-0"
                                  title="复制地址"
                                  onClick={(event) => {
                                    event.stopPropagation()
                                    void copyUrl(item)
                                  }}
                                >
                                  {copiedId === item.id ? <Check className="h-3.5 w-3.5" /> : <Copy className="h-3.5 w-3.5" />}
                                </Button>
                              </div>
                            </TableCell>
                            <TableCell>
                              <Badge variant="outline" className={cn('text-[10px]', statusBadgeClass(item.status))}>
                                {statusLabel[item.status]}
                              </Badge>
                            </TableCell>
                          </TableRow>
                        )
                      })
                    )}
                  </TableBody>
                </Table>
              </div>

              <div className="divide-y md:hidden">
                {filtered.length === 0 ? (
                  <div className="flex h-24 items-center justify-center text-sm text-muted-foreground">
                    没有匹配的频道
                  </div>
                ) : (
                  filtered.map((item) => {
                    const checked = selected.has(item.id)
                    return (
                      <div
                        key={item.id}
                        className={cn('flex cursor-pointer items-start gap-3 p-3', checked && 'bg-primary/5')}
                        onClick={() => toggleItem(item.id)}
                      >
                        <input
                          type="checkbox"
                          className="mt-1 h-4 w-4 shrink-0 accent-primary"
                          checked={checked}
                          disabled={importing}
                          onClick={(event) => event.stopPropagation()}
                          onChange={() => toggleItem(item.id)}
                          aria-label={`选择 ${item.name}`}
                        />
                        <div className={cn('flex h-8 w-8 shrink-0 items-center justify-center rounded-md text-xs font-semibold', avatarClass(item.name))}>
                          {initialOf(item.name)}
                        </div>
                        <div className="min-w-0 flex-1 space-y-1.5">
                          <div className="flex items-start justify-between gap-2">
                            <span className="truncate text-sm font-medium">{item.name}</span>
                            <Badge variant="outline" className={cn('shrink-0 text-[10px]', statusBadgeClass(item.status))}>
                              {statusLabel[item.status]}
                            </Badge>
                          </div>
                          <div className="flex flex-wrap gap-1.5">
                            <Badge variant="outline" className="max-w-[140px] truncate text-[10px]">{item.group}</Badge>
                            <Badge variant="outline" className={cn('text-[10px]', typeBadgeClass(item.type))}>
                              {typeLabel[item.type]}
                            </Badge>
                            <ChannelProtocolBadge url={item.url} />
                          </div>
                          <div className="flex min-w-0 items-center gap-1">
                            <span className="truncate font-mono text-[11px] text-muted-foreground" title={item.url}>
                              {item.url}
                            </span>
                            <Button
                              variant="ghost"
                              size="icon"
                              className="h-7 w-7 shrink-0"
                              title="复制地址"
                              onClick={(event) => {
                                event.stopPropagation()
                                void copyUrl(item)
                              }}
                            >
                              {copiedId === item.id ? <Check className="h-3.5 w-3.5" /> : <Copy className="h-3.5 w-3.5" />}
                            </Button>
                          </div>
                        </div>
                      </div>
                    )
                  })
                )}
              </div>
            </CardContent>
          </Card>

          <div className="flex flex-col gap-3 rounded-lg border bg-card p-3 sm:flex-row sm:items-center sm:justify-between">
            {result ? (
              <>
                <div className="text-sm text-muted-foreground">
                  本次导入：成功 <span className="font-medium text-foreground">{result.success}</span> 个
                  {result.failed > 0 && <>，失败 <span className="font-medium text-destructive">{result.failed}</span> 个</>}
                </div>
                <div className="flex flex-wrap justify-end gap-2">
                  <Button variant="outline" onClick={clearFile}>重新选择文件</Button>
                  <Button asChild>
                    <Link to="/channels">
                      返回频道配置
                      <ChevronRight className="ml-1.5 h-4 w-4" />
                    </Link>
                  </Button>
                </div>
              </>
            ) : (
              <>
                <div>
                  <div className="text-sm text-muted-foreground">
                    已选 <span className="font-medium text-foreground">{selectedCount}</span> 个频道
                  </div>
                  <div className="mt-0.5 text-xs text-muted-foreground">
                    只写入频道名称和地址；分组、Logo 不会保存。
                  </div>
                </div>
                <div className="flex flex-col items-stretch gap-2 sm:flex-row sm:items-center">
                  {importing ? (
                    <div className="flex items-center justify-end gap-1.5 text-xs text-muted-foreground">
                      <Loader2 className="h-3.5 w-3.5 animate-spin" />
                      正在批量导入 {selectedCount} 个频道...
                    </div>
                  ) : (
                    <>
                      <Button variant="ghost" onClick={clearSelection} disabled={selectedCount === 0}>
                        取消选择
                      </Button>
                      <Button onClick={handleImport} disabled={selectedCount === 0}>
                        <Upload className="mr-2 h-4 w-4" />
                        导入 {selectedCount} 个频道
                      </Button>
                    </>
                  )}
                </div>
              </>
            )}
          </div>
        </>
      )}

      <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-[11px] text-muted-foreground">
        <span className="inline-flex items-center gap-1"><Check className="h-3 w-3" />本地解析，不上传文件</span>
        <span className="inline-flex items-center gap-1"><Check className="h-3 w-3" />默认全选，可按筛选结果勾选</span>
        <span className="inline-flex items-center gap-1"><Check className="h-3 w-3" />分组 / Logo 仅预览，只导入名称与地址</span>
      </div>
    </div>
  )
}

export default M3uImportPage
