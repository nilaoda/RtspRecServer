import { useState } from 'react'
import { CalendarClock, Plus, Timer as TimerIcon, Trash2, Pencil } from 'lucide-react'
import { useAppContext } from '../../app/context'
import type { RecordingTimer, TimerUpsertPayload } from '../../types'
import { formatDateTime } from '../shared/format'
import { ChannelProtocolBadge } from '@/components/channel-protocol-badge'
import { nextWindowAt, describeWeekdays } from './lib/window'
import TimerFormDialog from './TimerFormDialog'

import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from '@/components/ui/alert-dialog'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Switch } from '@/components/ui/switch'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'

const toUpsertPayload = (timer: RecordingTimer, enabled: boolean): TimerUpsertPayload => ({
  name: timer.name,
  channelId: timer.channelId,
  startTime: timer.startTime,
  endTime: timer.endTime,
  weekdays: timer.weekdays,
  preOffsetMinutes: timer.preOffsetMinutes,
  postOffsetMinutes: timer.postOffsetMinutes,
  startDate: timer.startDate ?? null,
  endDate: timer.endDate ?? null,
  enabled,
})

const TimersPage = () => {
  const { timers, channels, now, updateTimer, deleteTimer } = useAppContext()
  const [formOpen, setFormOpen] = useState(false)
  const [editing, setEditing] = useState<RecordingTimer | null>(null)
  const [deleteTarget, setDeleteTarget] = useState<RecordingTimer | null>(null)

  const openCreate = () => {
    setEditing(null)
    setFormOpen(true)
  }

  const openEdit = (timer: RecordingTimer) => {
    setEditing(timer)
    setFormOpen(true)
  }

  const handleToggleEnabled = async (timer: RecordingTimer, enabled: boolean) => {
    await updateTimer(timer.id, toUpsertPayload(timer, enabled))
  }

  const handleDelete = async () => {
    if (!deleteTarget) return
    await deleteTimer(deleteTarget.id)
    setDeleteTarget(null)
  }

  const channelExists = (timer: RecordingTimer) => channels.some((channel) => channel.id === timer.channelId)

  return (
    <div className="space-y-6">
      <div className="flex flex-col sm:flex-row justify-between items-start sm:items-center gap-4">
        <div>
          <h2 className="text-2xl font-semibold flex items-center gap-2">
            <TimerIcon className="h-6 w-6" />
            定时规则
          </h2>
          <p className="text-sm text-muted-foreground mt-1">
            按星期和时间窗口自动创建录制任务，未来三天的计划会显示在任务列表中；命中窗口时若已有同频道任务则跳过。
          </p>
        </div>
        <Button onClick={openCreate} className="w-full sm:w-auto">
          <Plus className="mr-2 h-4 w-4" />
          新建定时规则
        </Button>
      </div>

      {timers.length === 0 ? (
        <Card>
          <CardContent className="flex flex-col items-center justify-center gap-2 py-12 text-center">
            <CalendarClock className="h-10 w-10 text-muted-foreground" />
            <p className="text-muted-foreground">暂无定时规则，点击右上角新建一条。</p>
          </CardContent>
        </Card>
      ) : (
        <Card>
          <CardContent className="p-0">
            <div className="overflow-x-auto">
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>规则</TableHead>
                    <TableHead>时间窗口</TableHead>
                    <TableHead>重复</TableHead>
                    <TableHead>余量</TableHead>
                    <TableHead>下次执行</TableHead>
                    <TableHead>上次触发</TableHead>
                    <TableHead>启用</TableHead>
                    <TableHead className="text-right">操作</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {timers.map((timer) => {
                    const next = timer.enabled ? nextWindowAt(timer, now) : null
                    return (
                      <TableRow key={timer.id} className={timer.enabled ? undefined : 'opacity-60'}>
                        <TableCell>
                          <div className="flex flex-col gap-1 min-w-0">
                            <span className="font-medium">{timer.name}</span>
                            <span className="flex items-center gap-1.5 text-xs text-muted-foreground">
                              <span className="truncate">{timer.channelName}</span>
                              {channelExists(timer) ? (
                                <ChannelProtocolBadge url={timer.url} />
                              ) : (
                                <Badge variant="destructive" className="text-[10px] px-1 py-0">频道缺失</Badge>
                              )}
                            </span>
                          </div>
                        </TableCell>
                        <TableCell className="font-mono text-sm whitespace-nowrap">
                          {timer.startTime} - {timer.endTime}
                        </TableCell>
                        <TableCell>
                          <Badge variant="secondary">{describeWeekdays(timer.weekdays)}</Badge>
                        </TableCell>
                        <TableCell className="text-xs text-muted-foreground whitespace-nowrap">
                          {timer.preOffsetMinutes === 0 && timer.postOffsetMinutes === 0
                            ? '—'
                            : `提前 ${timer.preOffsetMinutes} / 延后 ${timer.postOffsetMinutes}`}
                        </TableCell>
                        <TableCell className="text-sm whitespace-nowrap">
                          {next ? formatDateTime(next) : '—'}
                        </TableCell>
                        <TableCell className="text-sm text-muted-foreground whitespace-nowrap">
                          {timer.lastTriggeredAt ? formatDateTime(new Date(timer.lastTriggeredAt)) : '—'}
                        </TableCell>
                        <TableCell>
                          <Switch
                            checked={timer.enabled}
                            onCheckedChange={(checked) => handleToggleEnabled(timer, checked)}
                            aria-label={`启用 ${timer.name}`}
                          />
                        </TableCell>
                        <TableCell className="text-right">
                          <div className="flex justify-end gap-1">
                            <Button variant="ghost" size="icon" title="编辑规则" onClick={() => openEdit(timer)}>
                              <Pencil className="h-4 w-4" />
                            </Button>
                            <Button
                              variant="ghost"
                              size="icon"
                              title="删除规则"
                              className="text-destructive hover:text-destructive"
                              onClick={() => setDeleteTarget(timer)}
                            >
                              <Trash2 className="h-4 w-4" />
                            </Button>
                          </div>
                        </TableCell>
                      </TableRow>
                    )
                  })}
                </TableBody>
              </Table>
            </div>
          </CardContent>
        </Card>
      )}

      <TimerFormDialog open={formOpen} onOpenChange={setFormOpen} editing={editing} />

      <AlertDialog open={deleteTarget != null} onOpenChange={(open) => !open && setDeleteTarget(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>删除定时规则</AlertDialogTitle>
            <AlertDialogDescription>
              确定删除规则「{deleteTarget?.name}」？已生成的录制任务不会被删除。
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>取消</AlertDialogCancel>
            <AlertDialogAction onClick={handleDelete}>删除</AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  )
}

export default TimersPage
