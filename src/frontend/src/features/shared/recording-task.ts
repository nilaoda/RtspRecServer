import type { RecordingStatus, RecordingTaskDto, TaskWaitReason } from '../../types'

export type RecordingStatusKey = 'Pending' | 'Recording' | 'Completed' | 'Failed' | 'Stopped'

const statusKeys: RecordingStatusKey[] = ['Pending', 'Recording', 'Completed', 'Failed', 'Stopped']

export const normalizeStatus = (status: RecordingStatus): RecordingStatusKey =>
  typeof status === 'number' ? statusKeys[status] ?? 'Pending' : statusKeys.includes(status) ? status : 'Pending'

export const waitReasonLabel: Record<TaskWaitReason, string> = {
  scheduled: '未到开始时间',
  catchup: '等待回看片段生成',
  sourceLimit: '等待收流名额',
  writerLimit: '等待写盘名额',
  dispatch: '等待调度',
}

export const getRecordingBitrateKbps = (task: RecordingTaskDto, now: number) => {
  if (task.currentBitrateKbps && task.currentBitrateKbps > 0) return task.currentBitrateKbps

  const startedAt = Date.parse(task.startedAt ?? '')
  if (!Number.isFinite(startedAt) || task.bytesWritten === 0) return 0

  const durationSeconds = Math.max(1, (now - startedAt) / 1000)
  return (task.bytesWritten * 8) / (durationSeconds * 1000)
}
