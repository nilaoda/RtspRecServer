import { useCallback, useMemo, useState } from 'react'
import { Info, Loader2, RefreshCw } from 'lucide-react'

import { useAppContext } from '../../app/context'
import { formatBytes, formatDateTime } from '../shared/format'

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

const RecordingsPage = () => {
  const { recordings, reloadRecordings, getRecordingMediaInfo, tasks } = useAppContext()
  const [infoModal, setInfoModal] = useState<{ title: string; content: string } | null>(null)
  const [infoLoading, setInfoLoading] = useState(false)

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

  const visibleRecordings = useMemo(() => {
    const normalizeName = (value: string) => value.split(/[/\\]/).pop() ?? value
    const isTsFile = (value: string) => value.toLowerCase().endsWith('.ts')
    const activeNames = new Set(
      tasks
        .filter((task) => task.status === 'Recording')
        .flatMap((task) => {
          const names: string[] = []
          if (task.filePath) {
            names.push(normalizeName(task.filePath))
          }
          if (task.taskName) {
            names.push(task.taskName.endsWith('.ts') ? task.taskName : `${task.taskName}.ts`)
          }
          return names
        }),
    )
    return recordings.filter(
      (record) =>
        isTsFile(record.fileName) &&
        !activeNames.has(record.fileName) &&
        !activeNames.has(normalizeName(record.filePath)),
    )
  }, [recordings, tasks])

  return (
    <div className="space-y-6">
      <div className="flex flex-col sm:flex-row justify-between items-start sm:items-center gap-4">
        <Button onClick={reloadRecordings} variant="outline">
            <RefreshCw className="mr-2 h-4 w-4" />
            刷新列表
        </Button>
        <div className="text-sm text-muted-foreground bg-muted/50 px-3 py-1 rounded-md">
            已录文件: <span className="font-mono font-medium text-foreground">{visibleRecordings.length}</span>
        </div>
      </div>

      <Card>
        <CardHeader className="pb-3">
            <CardTitle className="text-lg font-medium">录制文件</CardTitle>
        </CardHeader>
        <CardContent className="p-0">
            <div className="rounded-md border-t">
                <Table>
                    <TableHeader>
                        <TableRow>
                            <TableHead className="min-w-[200px]">文件名</TableHead>
                            <TableHead className="w-[120px]">大小</TableHead>
                            <TableHead className="w-[180px]">录制时间</TableHead>
                            <TableHead className="hidden md:table-cell">路径</TableHead>
                            <TableHead className="w-[100px] text-right">操作</TableHead>
                        </TableRow>
                    </TableHeader>
                    <TableBody>
                        {visibleRecordings.length === 0 ? (
                            <TableRow>
                                <TableCell colSpan={5} className="h-24 text-center">
                                    暂无录制文件
                                </TableCell>
                            </TableRow>
                        ) : (
                            visibleRecordings.map((record) => (
                                <TableRow key={record.filePath}>
                                    <TableCell className="font-medium truncate max-w-[200px]" title={record.fileName}>
                                        {record.fileName}
                                    </TableCell>
                                    <TableCell className="text-sm text-muted-foreground">
                                        {formatBytes(record.fileSizeBytes)}
                                    </TableCell>
                                    <TableCell className="text-sm">
                                        {formatDateTime(new Date(record.recordedAt))}
                                    </TableCell>
                                    <TableCell className="hidden md:table-cell text-xs text-muted-foreground truncate max-w-[300px]" title={record.filePath}>
                                        {record.filePath}
                                    </TableCell>
                                    <TableCell className="text-right">
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
                                    </TableCell>
                                </TableRow>
                            ))
                        )}
                    </TableBody>
                </Table>
            </div>
        </CardContent>
      </Card>

      <Dialog open={Boolean(infoModal)} onOpenChange={(open) => !open && setInfoModal(null)}>
        <DialogContent className="max-w-3xl max-h-[80vh] flex flex-col">
            <DialogHeader>
                <DialogTitle>{infoModal?.title}</DialogTitle>
            </DialogHeader>
            <div className="flex-1 overflow-auto bg-muted/50 p-4 rounded-md font-mono text-xs">
                {infoLoading ? (
                    <div className="flex items-center justify-center h-40">
                        <Loader2 className="h-8 w-8 animate-spin text-muted-foreground" />
                    </div>
                ) : (
                    <pre className="whitespace-pre-wrap">{infoModal?.content}</pre>
                )}
            </div>
        </DialogContent>
      </Dialog>
    </div>
  )
}

export default RecordingsPage
