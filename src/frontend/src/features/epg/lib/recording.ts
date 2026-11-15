import type { ChannelConfig, RecordingStatus, RecordingTaskDto } from '../../../types'
import type { EpgProgram } from '../types/epg'

const statusKeys = ['Pending', 'Recording', 'Completed', 'Failed', 'Stopped'] as const
export type RecordingStatusKey = (typeof statusKeys)[number]

export const normalizeStatus = (value: RecordingStatus): RecordingStatusKey => {
  if (typeof value === 'number') {
    return statusKeys[value] ?? 'Pending'
  }
  return (statusKeys as readonly string[]).includes(value) ? (value as RecordingStatusKey) : 'Pending'
}

export const isActiveTask = (task: RecordingTaskDto) => {
  const status = normalizeStatus(task.status)
  return status === 'Pending' || status === 'Recording'
}

const normalizeName = (name: string) => name.replace(/\s+/g, '').toLowerCase()

const namesRelated = (a: string, b: string) => {
  const x = normalizeName(a)
  const y = normalizeName(b)
  return x !== '' && y !== '' && (x === y || x.includes(y) || y.includes(x))
}

export const matchRecordingChannel = (
  epgChannelName: string,
  channels: ChannelConfig[],
): ChannelConfig | null => {
  const target = normalizeName(epgChannelName)
  if (!target) {
    return null
  }

  const exact = channels.find((channel) => normalizeName(channel.name) === target)
  if (exact) {
    return exact
  }

  let best: ChannelConfig | null = null
  let bestLength = 0
  for (const channel of channels) {
    const name = normalizeName(channel.name)
    if (!name || !(name.includes(target) || target.includes(name))) {
      continue
    }
    const length = Math.min(name.length, target.length)
    if (length > bestLength) {
      best = channel
      bestLength = length
    }
  }
  return best
}

export const findReservedTask = (
  program: EpgProgram,
  epgChannelName: string,
  channels: ChannelConfig[],
  tasks: RecordingTaskDto[],
): RecordingTaskDto | null => {
  const matched = matchRecordingChannel(epgChannelName, channels)
  const start = program.startTime.getTime()
  const end = program.endTime.getTime()

  for (const task of tasks) {
    if (!isActiveTask(task)) {
      continue
    }
    const channelMatches =
      (matched != null && task.channelId === matched.id) || namesRelated(task.channelName, epgChannelName)
    if (!channelMatches) {
      continue
    }
    const taskStart = new Date(task.startTime).getTime()
    const taskEnd = new Date(task.endTime).getTime()
    const overlap = Math.min(taskEnd, end) - Math.max(taskStart, start)
    // 提前/延后填充可能跨入相邻节目，仅当重叠覆盖节目大半时长时才视为已预约，避免误标下一个节目。
    if (overlap > 0 && overlap >= (end - start) * 0.5) {
      return task
    }
  }
  return null
}