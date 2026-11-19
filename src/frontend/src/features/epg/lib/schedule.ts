import { clamp } from '../../shared/format'
import type { EpgProgram } from '../types/epg'

const timeFormatter = new Intl.DateTimeFormat('zh-CN', { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' })

export const formatProgramTime = (time: Date) =>
  Number.isFinite(time.getTime()) ? timeFormatter.format(time) : '--:--'

export function getProgramProgress(program: EpgProgram, now: number) {
  const start = program.startTime.getTime()
  const duration = program.endTime.getTime() - start
  return duration > 0 ? clamp((now - start) / duration * 100, 0, 100) : 0
}
