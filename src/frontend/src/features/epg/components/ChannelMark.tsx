import { Avatar, AvatarFallback, AvatarImage } from '@/components/ui/avatar'
import { cn } from '@/lib/utils'
import type { EpgChannel } from '../types/epg'

export default function ChannelMark({ channel, className }: { channel: EpgChannel; className?: string }) {
  const name = channel.name.trim()
  const mark = name.match(/^CCTV[\s-]*(\d{1,2}[K+]?)(?![a-z\d])/i)?.[1] ?? name.charAt(0)

  return (
    <Avatar aria-hidden="true" className={cn('rounded-lg border bg-background', className)}>
      {channel.iconUrl && <AvatarImage src={channel.iconUrl} alt="" className="object-contain p-1" />}
      <AvatarFallback className="rounded-none bg-muted/60 text-sm font-semibold text-foreground/80">
        {mark}
      </AvatarFallback>
    </Avatar>
  )
}
