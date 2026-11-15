import type { RecordingTimer } from '../../../types'

const WEEKDAY_LABELS = ['一', '二', '三', '四', '五', '六', '日'] as const

// 0=周一 … 6=周日，与后端 RecordingTimer.Weekdays 约定一致
const weekdayIndexOf = (date: Date) => (date.getDay() + 6) % 7

const parseHM = (value: string) => {
  const match = /^(\d{2}):(\d{2})$/.exec(value)
  if (!match) return null
  return { hours: Number(match[1]), minutes: Number(match[2]) }
}

const parseDay = (value: string | undefined) => {
  if (!value) return null
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value)
  if (!match) return null
  return new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]))
}

// 与后端 TimerWindowCalculator 保持一致的展示用计算：规则的下一个尚未开始的执行窗口
export const nextWindowAt = (timer: RecordingTimer, now: Date): Date | null => {
  const start = parseHM(timer.startTime)
  if (!start) return null

  const startDay = parseDay(timer.startDate)
  const endDay = parseDay(timer.endDate)

  // 从昨天开始扫（覆盖跨午夜窗口），直到第一个尚未开始的窗口
  for (let offset = -1; offset <= 10; offset++) {
    const day = new Date(now.getFullYear(), now.getMonth(), now.getDate() + offset)
    if (!timer.weekdays[weekdayIndexOf(day)]) continue
    if (startDay && day.getTime() < startDay.getTime()) continue
    if (endDay && day.getTime() > endDay.getTime()) continue

    const windowStart = new Date(day.getFullYear(), day.getMonth(), day.getDate(), start.hours, start.minutes)
    const effectiveStart = new Date(windowStart.getTime() - timer.preOffsetMinutes * 60000)
    if (effectiveStart.getTime() > now.getTime()) {
      return effectiveStart
    }
  }
  return null
}

export const describeWeekdays = (weekdays: boolean[]): string => {
  const active = weekdays.map((on, i) => (on ? i : -1)).filter((i) => i >= 0)
  if (active.length === 7) return '每天'
  if (active.length === 5 && active.every((i) => i <= 4)) return '工作日'
  if (active.length === 2 && active[0] === 5 && active[1] === 6) return '周末'
  return active.map((i) => WEEKDAY_LABELS[i]).join('、')
}

export const weekdayLabels = WEEKDAY_LABELS
