import type { TsCheckSummary, TsStreamTrack } from '../../types'

export const tsVerdictLabel: Record<string, string> = {
  pass: '正常',
  warning: '警告',
  error: '异常',
  incomplete: '未完成',
  unknown: '未知',
}

export const tsVerdictClass = (verdict?: string) => {
  if (verdict === 'pass') return 'border-green-500/30 bg-green-500/10 text-green-600 dark:text-green-400'
  if (verdict === 'warning') return 'border-amber-500/30 bg-amber-500/10 text-amber-600 dark:text-amber-400'
  if (verdict === 'error') return 'border-red-500/30 bg-red-500/10 text-red-600 dark:text-red-400'
  return 'border-border bg-muted text-muted-foreground'
}

export const tsTrackText = (tracks?: TsStreamTrack[], fallback?: string) => {
  const labels = tracks?.map((track) => track.label).filter(Boolean) ?? []
  const unique = [...new Set(labels)]
  return unique.length > 0 ? unique.join(' / ') : fallback || '未识别'
}

export const tsCodecText = (tsCheck?: TsCheckSummary) => {
  if (!tsCheck) return '--'
  const streams = [
    tsTrackText(tsCheck.videoTracks, tsCheck.video),
    tsTrackText(tsCheck.audioTracks, tsCheck.audio),
  ].filter((value) => value !== '未识别')
  return streams.length > 0 ? streams.join(' + ') : '未识别轨道'
}