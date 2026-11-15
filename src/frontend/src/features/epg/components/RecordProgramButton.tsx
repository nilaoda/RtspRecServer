import { useMemo, useState } from 'react'
import { CalendarCheck, CalendarPlus } from 'lucide-react'
import { useAppContext } from '../../../app/context'
import type { EpgProgram } from '../types/epg'
import { findReservedTask, matchRecordingChannel } from '../lib/recording'
import TaskFormDialog, { type TaskFormInitialValues } from '../../tasks/TaskFormDialog'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'

export default function RecordProgramButton({
  program,
  channelName,
  compact = false,
  className,
}: {
  program: EpgProgram
  channelName: string
  compact?: boolean
  className?: string
}) {
  const { channels, tasks, now } = useAppContext()
  const [open, setOpen] = useState(false)
  const reservedTask = findReservedTask(program, channelName, channels, tasks)
  const isPast = program.endTime.getTime() <= now.getTime()

  const matchedChannel = useMemo(
    () => matchRecordingChannel(channelName, channels),
    [channelName, channels],
  )
  const initialValues = useMemo<TaskFormInitialValues>(
    () => ({
      channelId: matchedChannel?.id,
      startTime: program.startTime,
      endTime: program.endTime,
      taskName: program.title,
    }),
    [matchedChannel, program],
  )

  if (reservedTask) {
    return (
      <Badge
        variant="secondary"
        className={cn(
          'gap-1 border-emerald-200 bg-emerald-100 text-emerald-700 dark:border-emerald-800 dark:bg-emerald-900/30 dark:text-emerald-400',
          className,
        )}
      >
        <CalendarCheck className="h-3 w-3" />
        已预约
      </Badge>
    )
  }

  if (isPast) {
    return null
  }

  return (
    <>
      {compact ? (
        <Button
          variant="ghost"
          size="icon"
          title="预约录制"
          className={cn('h-7 w-7 text-muted-foreground hover:text-primary', className)}
          onClick={() => setOpen(true)}
        >
          <CalendarPlus className="h-4 w-4" />
          <span className="sr-only">预约录制</span>
        </Button>
      ) : (
        <Button variant="outline" size="sm" className={cn('gap-1.5', className)} onClick={() => setOpen(true)}>
          <CalendarPlus className="h-3.5 w-3.5" />
          录制
        </Button>
      )}
      <TaskFormDialog open={open} onOpenChange={setOpen} initialValues={initialValues} />
    </>
  )
}