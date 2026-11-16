import type { ChannelProbeResult } from '../../types'

const formatBitrate = (kbps?: number) => {
  if (!kbps || kbps <= 0) {
    return null
  }
  return kbps >= 1000 ? `${(kbps / 1000).toFixed(1)} Mbps` : `${Math.round(kbps)} kbps`
}

export const formatProbeBitrate = (kbps?: number) => formatBitrate(kbps) ?? '码率未识别'

export const formatChannelCodecSummary = (probe?: ChannelProbeResult) => {
  if (!probe || !probe.accessible) {
    return null
  }

  const uniqueLabels = (tracks: ChannelProbeResult['videoTracks']) =>
    [...new Set(tracks.map((track) => track.label).filter(Boolean))]

  const codec = [...uniqueLabels(probe.videoTracks), ...uniqueLabels(probe.audioTracks)].join(' / ')
  const bitrate = formatBitrate(probe.bitrateKbps)
  if (!codec && !bitrate) {
    return null
  }
  return [codec || '编码未识别', bitrate].filter(Boolean).join(' · ')
}
