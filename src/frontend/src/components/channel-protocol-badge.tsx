import { Badge } from '@/components/ui/badge'
import { cn } from '@/lib/utils'

type ProtocolKind = 'rtsp' | 'rtp' | 'udp' | 'http' | 'rtmp' | 'other'

export type ChannelProtocol = {
  label: string
  kind: ProtocolKind
  description: string
}

const protocolStyles: Record<ProtocolKind, string> = {
  rtsp: 'border-violet-500/30 bg-violet-500/10 text-violet-600 dark:text-violet-400',
  rtp: 'border-sky-500/30 bg-sky-500/10 text-sky-600 dark:text-sky-400',
  udp: 'border-teal-500/30 bg-teal-500/10 text-teal-600 dark:text-teal-400',
  http: 'border-amber-500/30 bg-amber-500/10 text-amber-600 dark:text-amber-400',
  rtmp: 'border-rose-500/30 bg-rose-500/10 text-rose-600 dark:text-rose-400',
  other: 'border-border bg-muted text-muted-foreground',
}

export const getChannelProtocol = (url?: string): ChannelProtocol => {
  const value = url?.trim() ?? ''
  if (!value) {
    return { label: '未知', kind: 'other', description: '未填写频道地址' }
  }

  const scheme = value.match(/^([a-z][a-z0-9+.-]*):\/\//i)?.[1]?.toLowerCase()
  if (!scheme) {
    return { label: '未知', kind: 'other', description: '无法识别地址协议' }
  }

  if (scheme === 'rtsp' || scheme === 'rtsps') {
    return { label: 'RTSP', kind: 'rtsp', description: 'RTSP 回放流' }
  }
  if (scheme === 'rtp') {
    return { label: 'RTP', kind: 'rtp', description: 'RTP 组播流' }
  }
  if (scheme === 'udp') {
    return { label: 'UDP', kind: 'udp', description: 'UDP 组播流' }
  }
  if (scheme === 'rtmp' || scheme === 'rtmps') {
    return { label: 'RTMP', kind: 'rtmp', description: 'RTMP 流' }
  }
  if (scheme === 'http' || scheme === 'https') {
    return { label: 'HTTP', kind: 'http', description: 'HTTP/HTTPS 流' }
  }

  return {
    label: scheme.toUpperCase(),
    kind: 'other',
    description: `${scheme.toUpperCase()} 协议`,
  }
}

export const ChannelProtocolBadge = ({
  url,
  className,
}: {
  url?: string
  className?: string
}) => {
  const protocol = getChannelProtocol(url)
  return (
    <Badge
      variant="outline"
      title={protocol.description}
      className={cn(
        'shrink-0 px-1.5 py-0 font-mono text-[10px] font-medium tracking-wide',
        protocolStyles[protocol.kind],
        className,
      )}
    >
      {protocol.label}
    </Badge>
  )
}
