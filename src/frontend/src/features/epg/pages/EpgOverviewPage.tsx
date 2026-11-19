import { useMemo } from 'react'
import { ChevronRight, Tv } from 'lucide-react'
import { Link } from 'react-router-dom'
import { Progress } from '@/components/ui/progress'
import ChannelMark from '../components/ChannelMark'
import { useEpgContext } from '../context/useEpgContext'
import { formatProgramTime, getProgramProgress } from '../lib/schedule'

const dateFormatter = new Intl.DateTimeFormat('zh-CN', { month: 'long', day: 'numeric', weekday: 'long' })

export default function EpgOverviewPage() {
  const { channels, currentPrograms, now } = useEpgContext()
  const currentByChannel = useMemo(
    () => new Map(currentPrograms.map(({ channelId, program }) => [channelId, program])),
    [currentPrograms],
  )
  const rows = channels.map(channel => {
    const program = currentByChannel.get(channel.id)
    const isOnAir = program && program.startTime.getTime() <= now && program.endTime.getTime() > now
    return { channel, program: isOnAir ? program : undefined }
  })
  const onAirCount = rows.filter(row => row.program).length

  return (
    <section aria-labelledby="epg-on-air" className="space-y-4">
      <div className="flex flex-wrap items-end justify-between gap-x-8 gap-y-3">
        <div className="space-y-1.5">
          <p className="text-xs font-medium text-muted-foreground">{dateFormatter.format(now)}</p>
          <h3 id="epg-on-air" className="text-3xl font-semibold tracking-tight sm:text-4xl">正在播出</h3>
        </div>
        <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-sm text-muted-foreground">
          <span><span className="font-semibold tabular-nums text-foreground">{channels.length}</span> 个频道</span>
          <span className="inline-flex items-center gap-1.5">
            <span aria-hidden="true" className="h-2 w-2 rounded-full bg-emerald-500" />
            <span><span className="font-semibold tabular-nums text-foreground">{onAirCount}</span> 个在播</span>
          </span>
        </div>
      </div>

      {rows.length === 0 ? (
        <div className="flex min-h-48 flex-col items-center justify-center gap-3 rounded-xl border border-dashed text-sm text-muted-foreground">
          <Tv aria-hidden="true" className="h-6 w-6" />
          <p>暂无频道数据</p>
        </div>
      ) : (
        <div className="overflow-hidden rounded-xl border bg-card">
          <div aria-hidden="true" className="hidden grid-cols-[minmax(8rem,1fr)_minmax(0,1.8fr)_minmax(8rem,.8fr)_1rem] gap-x-5 border-b bg-muted/45 px-5 py-3 text-xs font-medium text-muted-foreground lg:grid">
            <span>频道</span>
            <span>当前节目</span>
            <span>播出时段</span>
          </div>
          <ul className="divide-y">
            {rows.map(({ channel, program }) => (
              <li key={channel.id}>
                <Link
                  to={`/epg/channels/${encodeURIComponent(channel.id)}`}
                  className="group grid grid-cols-[minmax(0,1fr)_1rem] items-center gap-x-3 gap-y-1.5 px-3.5 py-3.5 transition-colors hover:bg-muted/40 focus-visible:bg-muted/40 focus-visible:outline focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-ring sm:px-5 lg:grid-cols-[minmax(8rem,1fr)_minmax(0,1.8fr)_minmax(8rem,.8fr)_1rem] lg:gap-x-5"
                >
                  <div className="col-start-1 row-start-1 flex min-w-0 items-center gap-3">
                    <ChannelMark channel={channel} className="h-9 w-9" />
                    <div className="min-w-0">
                      <p className="truncate text-sm font-semibold" title={channel.name}>{channel.name}</p>
                      {channel.categories[0] && <p className="truncate text-xs text-muted-foreground">{channel.categories[0]}</p>}
                    </div>
                  </div>
                  <div className="col-start-1 row-start-2 min-w-0 pl-12 lg:col-start-2 lg:row-start-1 lg:pl-0">
                    {program ? (
                      <div className="flex min-w-0 items-center gap-2">
                        <span aria-label="正在播出" className="h-1.5 w-1.5 shrink-0 rounded-full bg-emerald-500" />
                        <span className="truncate text-sm font-medium sm:text-base" title={program.title}>{program.title}</span>
                      </div>
                    ) : (
                      <span className="text-sm text-muted-foreground">暂无节目信息</span>
                    )}
                  </div>
                  {program && (
                    <div className="col-start-1 row-start-3 flex min-w-0 items-center gap-3 pl-12 lg:col-start-3 lg:row-start-1 lg:block lg:pl-0">
                      <p className="whitespace-nowrap text-xs tabular-nums text-muted-foreground">
                        {formatProgramTime(program.startTime)} – {formatProgramTime(program.endTime)}
                      </p>
                      <Progress
                        aria-label={`${program.title}播放进度`}
                        value={getProgramProgress(program, now)}
                        className="h-1 w-16 bg-muted-foreground/15 sm:w-24 lg:mt-1.5 lg:w-full"
                        indicatorClassName="bg-emerald-500 motion-reduce:transition-none"
                      />
                    </div>
                  )}
                  <ChevronRight aria-hidden="true" className="col-start-2 row-start-1 h-4 w-4 text-muted-foreground/65 transition-transform group-hover:translate-x-0.5 motion-reduce:transform-none motion-reduce:transition-none lg:col-start-4" />
                </Link>
              </li>
            ))}
          </ul>
        </div>
      )}
    </section>
  )
}
