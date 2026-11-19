import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import { ArrowDownToLine, Download, FileText, Loader2, RefreshCw } from 'lucide-react'
import { toast } from 'sonner'

import { getLogContent, getLogs } from '../../services/api'
import type { LogFileContent, LogFileInfo } from '../../types'
import { formatBytes, formatDateTime } from '../shared/format'

import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardHeader } from '@/components/ui/card'
import { Switch } from '@/components/ui/switch'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'

const BOTTOM_THRESHOLD = 2

const LogsPage = () => {
  const [files, setFiles] = useState<LogFileInfo[]>([])
  const [selectedFile, setSelectedFile] = useState('')
  const [content, setContent] = useState<LogFileContent | null>(null)
  const [loading, setLoading] = useState(false)
  const [autoRefresh, setAutoRefresh] = useState(true)
  const [following, setFollowing] = useState(true)
  const viewportRef = useRef<HTMLDivElement>(null)
  const followingRef = useRef(true)
  const latestContent = useRef<LogFileContent | null>(null)
  const contentRequest = useRef<AbortController | null>(null)

  const updateFollowing = useCallback((value: boolean) => {
    followingRef.current = value
    setFollowing(value)
  }, [])

  const loadFiles = useCallback(async () => {
    try {
      const data = await getLogs()
      setFiles(data)
      setSelectedFile((current) => {
        if (current && data.some((file) => file.fileName === current)) {
          return current
        }
        return data[0]?.fileName ?? ''
      })
    } catch {
      toast.error('加载日志列表失败')
    }
  }, [])

  const loadContent = useCallback(async (fileName: string, silent = false) => {
    if (!fileName || (silent && contentRequest.current)) return

    contentRequest.current?.abort()
    const request = new AbortController()
    contentRequest.current = request
    if (!silent && !latestContent.current) {
      setLoading(true)
    }

    try {
      const data = await getLogContent(fileName, request.signal)
      if (request.signal.aborted) return

      latestContent.current = data
      // 向上翻阅时固定当前内容，避免日志尾部滑动后把正在读的行换掉。
      if (followingRef.current) setContent(data)
    } catch {
      if (!silent && !request.signal.aborted) {
        toast.error('加载日志内容失败')
      }
    } finally {
      if (contentRequest.current === request) {
        contentRequest.current = null
        setLoading(false)
      }
    }
  }, [])

  useEffect(() => {
    void loadFiles()
  }, [loadFiles])

  useEffect(() => {
    latestContent.current = null
    updateFollowing(true)
    setContent(null)
    setLoading(Boolean(selectedFile))
    if (selectedFile) void loadContent(selectedFile)

    return () => {
      contentRequest.current?.abort()
      contentRequest.current = null
    }
  }, [loadContent, selectedFile, updateFollowing])

  useEffect(() => {
    if (!autoRefresh || !selectedFile) {
      return
    }

    const timer = window.setInterval(() => {
      void loadContent(selectedFile, true)
    }, 3000)

    return () => window.clearInterval(timer)
  }, [autoRefresh, loadContent, selectedFile])

  useLayoutEffect(() => {
    const viewport = viewportRef.current
    if (viewport && content && followingRef.current) {
      viewport.scrollTop = viewport.scrollHeight
    }
  }, [content])

  const handleScroll = () => {
    const viewport = viewportRef.current
    if (!viewport || !content) return

    const atBottom = viewport.scrollHeight - viewport.clientHeight - viewport.scrollTop <= BOTTOM_THRESHOLD
    if (atBottom === followingRef.current) return
    updateFollowing(atBottom)
    if (atBottom) setContent(latestContent.current)
  }

  const jumpToLatest = () => {
    updateFollowing(true)
    setContent(latestContent.current)
    const viewport = viewportRef.current
    if (viewport) viewport.scrollTop = viewport.scrollHeight
    void loadContent(selectedFile, true)
  }

  const selectedInfo = files.find((file) => file.fileName === selectedFile)

  return (
    <div className="space-y-6">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">运行日志</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            查看服务端每日运行日志。日志默认保留 30 天。
          </p>
        </div>
        <Button variant="outline" onClick={() => { void loadFiles(); void loadContent(selectedFile) }}>
          <RefreshCw className="mr-2 h-4 w-4" />
          刷新
        </Button>
      </div>

      <Card>
        <CardHeader className="flex flex-col gap-3 pb-4 sm:flex-row sm:items-center sm:justify-between">
          <div className="flex flex-wrap items-center gap-3">
            <Select value={selectedFile} onValueChange={setSelectedFile} disabled={files.length === 0}>
              <SelectTrigger className="w-[220px]">
                <FileText className="mr-2 h-4 w-4 text-muted-foreground" />
                <SelectValue placeholder="选择日志文件" />
              </SelectTrigger>
              <SelectContent>
                {files.map((file) => (
                  <SelectItem key={file.fileName} value={file.fileName}>
                    {file.fileName}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            {selectedInfo && (
              <span className="text-xs text-muted-foreground">
                {formatBytes(selectedInfo.fileSizeBytes)} · {formatDateTime(new Date(selectedInfo.modifiedAt))}
              </span>
            )}
            {content?.truncated && <Badge variant="outline" className="text-[10px]">仅显示尾部</Badge>}
          </div>

          <div className="flex flex-wrap items-center gap-3">
            <label className="flex items-center gap-2 text-xs text-muted-foreground">
              <Switch checked={autoRefresh} onCheckedChange={setAutoRefresh} />
              自动刷新
            </label>
            {content && !following && (
              <Button variant="outline" size="sm" onClick={jumpToLatest}>
                <ArrowDownToLine className="mr-2 h-4 w-4" />
                跳到最新
              </Button>
            )}
            {selectedFile && (
              <Button asChild variant="outline" size="sm">
                <a href={`/api/logs/${encodeURIComponent(selectedFile)}/download`}>
                  <Download className="mr-2 h-4 w-4" />
                  下载
                </a>
              </Button>
            )}
          </div>
        </CardHeader>

        <CardContent className="p-0">
          <div
            ref={viewportRef}
            onScroll={handleScroll}
            role="region"
            aria-label="运行日志内容"
            tabIndex={0}
            className="min-h-[360px] max-h-[65vh] overflow-auto rounded-b-xl bg-slate-950 text-slate-100"
          >
            {loading ? (
              <div className="flex h-[360px] items-center justify-center">
                <Loader2 className="h-6 w-6 animate-spin text-slate-400" />
              </div>
            ) : content ? (
              <pre className="whitespace-pre p-4 font-mono text-xs leading-5">
                {content.content || '暂无日志内容'}
              </pre>
            ) : (
              <div className="flex h-[360px] flex-col items-center justify-center gap-2 text-sm text-slate-400">
                <FileText className="h-7 w-7" />
                <span>{files.length === 0 ? '暂无运行日志' : '请选择日志文件'}</span>
              </div>
            )}
          </div>
        </CardContent>
      </Card>
    </div>
  )
}

export default LogsPage
