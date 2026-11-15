import { useCallback, useEffect, useRef, useState } from 'react'
import { Download, FileText, Loader2, RefreshCw } from 'lucide-react'
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

const LogsPage = () => {
  const [files, setFiles] = useState<LogFileInfo[]>([])
  const [selectedFile, setSelectedFile] = useState('')
  const [content, setContent] = useState<LogFileContent | null>(null)
  const [loading, setLoading] = useState(false)
  const [autoRefresh, setAutoRefresh] = useState(true)
  const viewportRef = useRef<HTMLDivElement>(null)

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
    if (!fileName) {
      setContent(null)
      return
    }

    if (!silent) {
      setLoading(true)
    }

    try {
      const data = await getLogContent(fileName)
      setContent(data)
    } catch {
      if (!silent) {
        toast.error('加载日志内容失败')
      }
    } finally {
      if (!silent) {
        setLoading(false)
      }
    }
  }, [])

  useEffect(() => {
    void loadFiles()
  }, [loadFiles])

  useEffect(() => {
    void loadContent(selectedFile)
  }, [loadContent, selectedFile])

  useEffect(() => {
    if (!autoRefresh || !selectedFile) {
      return
    }

    const timer = window.setInterval(() => {
      void loadContent(selectedFile, true)
    }, 3000)

    return () => window.clearInterval(timer)
  }, [autoRefresh, loadContent, selectedFile])

  useEffect(() => {
    const viewport = viewportRef.current
    if (viewport && autoRefresh) {
      viewport.scrollTop = viewport.scrollHeight
    }
  }, [content, autoRefresh])

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

          <div className="flex items-center gap-4">
            <label className="flex items-center gap-2 text-xs text-muted-foreground">
              <Switch checked={autoRefresh} onCheckedChange={setAutoRefresh} />
              自动刷新
            </label>
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
