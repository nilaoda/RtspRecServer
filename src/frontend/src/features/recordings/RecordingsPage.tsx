import { useCallback, useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { ClipboardList, Info, Loader2, RefreshCw, Trash2 } from 'lucide-react'

import { useAppContext } from '../../app/context'
import { formatBytes, formatDateTime } from '../shared/format'
import PlayLinkMenu from '../shared/play-link-menu'

import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"
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
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
} from "@/components/ui/tooltip"
import { cn } from "@/lib/utils"

type DeleteTarget = { fileNames: string[]; totalBytes: number }

const getRecordingPlayPath = (fileName: string) => `/api/recordings/play/${encodeURIComponent(fileName)}`

const recordingPathKey = (path: string) => /^[a-z]:[/\\]|^\\\\/i.test(path)
  ? path.replaceAll('\\', '/').toLowerCase()
  : path

const RecordingTaskLink = ({ taskId }: { taskId?: number }) => taskId === undefined ? null : (
  <Button asChild variant="ghost" size="sm">
    <Link to={`/?task=${taskId}`}>
      <ClipboardList className="mr-1.5 h-4 w-4" />
      查看任务
    </Link>
  </Button>
)

const RecordingsPage = () => {
  const { recordings, reloadRecordings, getRecordingMediaInfo, tasks, deleteRecordings } = useAppContext()
  const [infoModal, setInfoModal] = useState<{ title: string; content: string } | null>(null)
  const [infoLoading, setInfoLoading] = useState(false)
  const [selectedNames, setSelectedNames] = useState<Set<string>>(() => new Set())
  const [fileSearch, setFileSearch] = useState('')
  const [recordedDate, setRecordedDate] = useState('')
  const [deleteTarget, setDeleteTarget] = useState<DeleteTarget | null>(null)
  const [deleting, setDeleting] = useState(false)

  useEffect(() => {
    void reloadRecordings()
  }, [reloadRecordings])

  const onViewInfo = useCallback(
    async (filePath: string, fileName: string) => {
      setInfoLoading(true)
      setInfoModal({ title: fileName, content: '' })
      try {
        const info = await getRecordingMediaInfo(filePath)
        setInfoModal({ title: fileName, content: info })
      } catch {
        setInfoModal(null)
      } finally {
        setInfoLoading(false)
      }
    },
    [getRecordingMediaInfo],
  )

  const { availableRecordings, taskIdByPath } = useMemo(() => {
    const activePaths = new Set<string>()
    const taskIdByPath = new Map<string, number>()
    for (const task of tasks) {
      if (!task.filePath) continue
      const path = recordingPathKey(task.filePath)
      if (task.status === 'Recording' || task.status === 1) {
        activePaths.add(path)
      } else if (task.id > (taskIdByPath.get(path) ?? 0)) {
        taskIdByPath.set(path, task.id)
      }
    }
    return {
      availableRecordings: recordings.filter((record) =>
        record.fileName.toLowerCase().endsWith('.ts') && !activePaths.has(recordingPathKey(record.filePath))),
      taskIdByPath,
    }
  }, [recordings, tasks])

  const visibleRecordings = useMemo(() => {
    const query = fileSearch.trim().toLowerCase()
    return availableRecordings.filter((record) =>
      (!query || record.fileName.toLowerCase().includes(query)) &&
      (!recordedDate || formatDateTime(new Date(record.recordedAt)).slice(0, 10) === recordedDate))
  }, [availableRecordings, fileSearch, recordedDate])

  const hasFilters = Boolean(fileSearch.trim() || recordedDate)
  const emptyMessage = hasFilters ? '没有符合条件的录像' : '暂无录制文件'

  const selectedRecordings = useMemo(
    () => visibleRecordings.filter((record) => selectedNames.has(record.fileName)),
    [visibleRecordings, selectedNames],
  )
  const allSelected =
    visibleRecordings.length > 0 && visibleRecordings.every((record) => selectedNames.has(record.fileName))
  const someSelected = visibleRecordings.some((record) => selectedNames.has(record.fileName)) && !allSelected
  const totalSelectedBytes = useMemo(
    () => selectedRecordings.reduce((total, record) => total + record.fileSizeBytes, 0),
    [selectedRecordings],
  )

  const toggleSelection = useCallback((fileName: string) => {
    setSelectedNames((previous) => {
      const next = new Set(previous)
      if (next.has(fileName)) {
        next.delete(fileName)
      } else {
        next.add(fileName)
      }
      return next
    })
  }, [])

  const toggleAll = useCallback(() => {
    setSelectedNames((previous) => {
      const next = new Set(previous)
      if (allSelected) {
        visibleRecordings.forEach((record) => next.delete(record.fileName))
      } else {
        visibleRecordings.forEach((record) => next.add(record.fileName))
      }
      return next
    })
  }, [allSelected, visibleRecordings])

  const clearSelection = useCallback(() => setSelectedNames(new Set()), [])

  const confirmDelete = useCallback(async () => {
    if (!deleteTarget || deleting) {
      return
    }

    setDeleting(true)
    try {
      const result = await deleteRecordings(deleteTarget.fileNames)
      if (!result) {
        return
      }

      setSelectedNames(new Set(result.failedItems.map((item) => item.fileName)))
      setDeleteTarget(null)
    } finally {
      setDeleting(false)
    }
  }, [deleteTarget, deleteRecordings, deleting])

  return (
    <div className="space-y-6">
      <div className="flex flex-col sm:flex-row justify-between items-start sm:items-center gap-4">
        <div className="flex flex-wrap items-center gap-2">
          <Button onClick={reloadRecordings} variant="outline">
            <RefreshCw className="mr-2 h-4 w-4" />
            刷新列表
          </Button>
        </div>
        <div className="text-sm text-muted-foreground bg-muted/50 px-3 py-1 rounded-md">
          显示 <span className="font-mono font-medium text-foreground">{visibleRecordings.length} / {availableRecordings.length}</span> 个文件
        </div>
      </div>

      <Card>
        <CardHeader className="gap-3 pb-3">
          <CardTitle className="text-lg font-medium">录制文件</CardTitle>
          <div className="flex flex-wrap items-center gap-2">
            <Input
              type="search"
              aria-label="搜索录像文件名"
              placeholder="搜索文件名"
              value={fileSearch}
              onChange={(event) => {
                setFileSearch(event.target.value)
                setSelectedNames(new Set())
              }}
              className="h-8 w-full sm:w-52"
            />
            <label className="flex items-center gap-2 text-sm text-muted-foreground">
              录制日期
              <Input type="date" value={recordedDate} onChange={(event) => {
                setRecordedDate(event.target.value)
                setSelectedNames(new Set())
              }} className="h-8 w-auto" />
            </label>
            {hasFilters && (
              <Button variant="ghost" size="sm" onClick={() => {
                setFileSearch('')
                setRecordedDate('')
                setSelectedNames(new Set())
              }}>清除筛选</Button>
            )}
          </div>
        </CardHeader>
        <CardContent className="p-0">
          <div className="flex flex-wrap items-center justify-between gap-2 border-b bg-muted/20 px-4 py-2">
            <span className="text-xs text-muted-foreground">
              已选 <span className="font-medium text-foreground">{selectedRecordings.length}</span> 项 · {formatBytes(totalSelectedBytes)}
            </span>
            <div className="flex flex-wrap items-center gap-2">
              <Button
                variant="destructive"
                size="sm"
                className="h-8"
                disabled={selectedRecordings.length === 0 || deleting}
                onClick={() =>
                  setDeleteTarget({
                    fileNames: selectedRecordings.map((record) => record.fileName),
                    totalBytes: totalSelectedBytes,
                  })
                }
              >
                <Trash2 className="mr-1.5 h-4 w-4" />
                删除选中
              </Button>
              <Button
                variant="ghost"
                size="sm"
                className="h-8"
                onClick={clearSelection}
                disabled={selectedRecordings.length === 0 || deleting}
              >
                取消选择
              </Button>
            </div>
          </div>
          <div className="hidden md:block rounded-md">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead className="w-[3%]">
                    <input
                      type="checkbox"
                      className="h-4 w-4 accent-primary"
                      checked={allSelected}
                      ref={(input) => {
                        if (input) input.indeterminate = someSelected
                      }}
                      onChange={toggleAll}
                      aria-label="全选录像文件"
                    />
                  </TableHead>
                  <TableHead className="min-w-[200px]">文件名</TableHead>
                  <TableHead className="w-[120px]">大小</TableHead>
                  <TableHead className="w-[180px]">录制时间</TableHead>
                  <TableHead>路径</TableHead>
                  <TableHead className="w-[150px] text-right">操作</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {visibleRecordings.length === 0 ? (
                  <TableRow>
                    <TableCell colSpan={6} className="h-24 text-center">
                      {emptyMessage}
                    </TableCell>
                  </TableRow>
                ) : (
                  visibleRecordings.map((record) => (
                    <TableRow key={record.filePath}>
                      <TableCell>
                        <input
                          type="checkbox"
                          className="h-4 w-4 accent-primary"
                          checked={selectedNames.has(record.fileName)}
                          onChange={() => toggleSelection(record.fileName)}
                          aria-label={`选择录像 ${record.fileName}`}
                        />
                      </TableCell>
                      <TableCell className="font-medium truncate max-w-[200px]" title={record.fileName}>
                        {record.fileName}
                      </TableCell>
                      <TableCell className="text-sm text-muted-foreground">
                        {formatBytes(record.fileSizeBytes)}
                      </TableCell>
                      <TableCell className="text-sm">
                        {formatDateTime(new Date(record.recordedAt))}
                      </TableCell>
                      <TableCell className="text-xs text-muted-foreground truncate max-w-[300px]" title={record.filePath}>
                        {record.filePath}
                      </TableCell>
                      <TableCell className="text-right">
                        <div className="flex justify-end gap-1">
                          <PlayLinkMenu path={getRecordingPlayPath(record.fileName)} name={record.fileName} className="h-8 w-8" />
                          <RecordingTaskLink taskId={taskIdByPath.get(recordingPathKey(record.filePath))} />
                          <TooltipProvider>
                            <Tooltip>
                              <TooltipTrigger asChild>
                                <Button variant="ghost" size="sm" onClick={() => onViewInfo(record.filePath, record.fileName)}>
                                  <Info className="mr-2 h-4 w-4" />
                                  信息
                                </Button>
                              </TooltipTrigger>
                              <TooltipContent>
                                <p>查看媒体信息</p>
                              </TooltipContent>
                            </Tooltip>
                          </TooltipProvider>
                          <Button
                            variant="ghost"
                            size="sm"
                            className="text-destructive hover:text-destructive"
                            onClick={() => setDeleteTarget({ fileNames: [record.fileName], totalBytes: record.fileSizeBytes })}
                            aria-label={`删除 ${record.fileName}`}
                          >
                            <Trash2 className="h-4 w-4" />
                          </Button>
                        </div>
                      </TableCell>
                    </TableRow>
                  ))
                )}
              </TableBody>
            </Table>
          </div>

          <div className="md:hidden">
            {visibleRecordings.length > 0 && (
              <label className="flex items-center gap-2 px-3 pt-3 text-xs text-muted-foreground">
                <input
                  type="checkbox"
                  className="h-4 w-4 accent-primary"
                  checked={allSelected}
                  ref={(input) => {
                    if (input) input.indeterminate = someSelected
                  }}
                  onChange={toggleAll}
                  aria-label="全选录像文件"
                />
                全选
              </label>
            )}
            <div className="space-y-2 p-3">
              {visibleRecordings.length === 0 ? (
                <div className="py-8 text-center text-sm text-muted-foreground">{emptyMessage}</div>
              ) : (
                visibleRecordings.map((record) => (
                  <div
                    key={record.filePath}
                    className={cn(
                      'space-y-2 rounded-lg border p-3',
                      selectedNames.has(record.fileName) && 'border-primary/40 bg-primary/5',
                    )}
                  >
                    <div className="flex items-start gap-2">
                      <input
                        type="checkbox"
                        className="mt-1 h-4 w-4 shrink-0 accent-primary"
                        checked={selectedNames.has(record.fileName)}
                        onChange={() => toggleSelection(record.fileName)}
                        aria-label={`选择录像 ${record.fileName}`}
                      />
                      <div className="min-w-0 flex-1">
                        <div className="truncate text-sm font-medium" title={record.fileName}>
                          {record.fileName}
                        </div>
                        <div className="text-xs text-muted-foreground">
                          {formatBytes(record.fileSizeBytes)} · {formatDateTime(new Date(record.recordedAt))}
                        </div>
                      </div>
                    </div>
                    <div className="flex flex-wrap justify-end gap-1">
                      <PlayLinkMenu path={getRecordingPlayPath(record.fileName)} name={record.fileName} withLabel />
                      <RecordingTaskLink taskId={taskIdByPath.get(recordingPathKey(record.filePath))} />
                      <Button variant="ghost" size="sm" onClick={() => onViewInfo(record.filePath, record.fileName)}>
                        <Info className="mr-1.5 h-4 w-4" />
                        信息
                      </Button>
                      <Button
                        variant="ghost"
                        size="sm"
                        className="text-destructive hover:text-destructive"
                        onClick={() => setDeleteTarget({ fileNames: [record.fileName], totalBytes: record.fileSizeBytes })}
                      >
                        <Trash2 className="mr-1.5 h-4 w-4" />
                        删除
                      </Button>
                    </div>
                  </div>
                ))
              )}
            </div>
          </div>
        </CardContent>
      </Card>

      <AlertDialog
        open={deleteTarget != null}
        onOpenChange={(open) => {
          if (!open && !deleting) {
            setDeleteTarget(null)
          }
        }}
      >
        <AlertDialogContent className="max-w-[calc(100vw-2rem)] sm:max-w-[440px]">
          <AlertDialogHeader>
            <AlertDialogTitle>确认删除录像文件？</AlertDialogTitle>
            <AlertDialogDescription asChild>
              <div className="space-y-2">
                <p>
                  将永久删除 {deleteTarget?.fileNames.length ?? 0} 个文件（共 {formatBytes(deleteTarget?.totalBytes ?? 0)}），
                  此操作不可恢复，同名录制日志会一并删除。
                </p>
                <ul className="max-h-32 overflow-y-auto rounded-md border bg-muted/30 p-2 font-mono text-[11px]">
                  {deleteTarget?.fileNames.slice(0, 8).map((name) => (
                    <li key={name} className="truncate" title={name}>
                      {name}
                    </li>
                  ))}
                  {deleteTarget && deleteTarget.fileNames.length > 8 && (
                    <li className="text-muted-foreground">…等 {deleteTarget.fileNames.length} 个</li>
                  )}
                </ul>
              </div>
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel disabled={deleting}>取消</AlertDialogCancel>
            <AlertDialogAction
              className="bg-destructive hover:bg-destructive/90"
              disabled={deleting}
              onClick={(event) => {
                event.preventDefault()
                void confirmDelete()
              }}
            >
              {deleting ? '删除中…' : '确认删除'}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>

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

export default RecordingsPage