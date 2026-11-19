import { useEffect, useMemo, useRef, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { Progress } from '@/components/ui/progress'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'
import { cn } from '@/lib/utils'
import Loading from '../../shared/Loading'
import ChannelMark from '../components/ChannelMark'
import RecordProgramButton from '../components/RecordProgramButton'
import { useEpgContext } from '../context/useEpgContext'
import { formatProgramTime, getProgramProgress } from '../lib/schedule'
import type { EpgProgram } from '../types/epg'

const emptyPrograms: EpgProgram[] = []
const shortDateFormatter = new Intl.DateTimeFormat('zh-CN', { month: 'numeric', day: 'numeric' })
const weekdayFormatter = new Intl.DateTimeFormat('zh-CN', { weekday: 'short' })
const languageNames = new Intl.DisplayNames('zh-CN', { type: 'language', fallback: 'none' })
const regionNames = new Intl.DisplayNames('zh-CN', { type: 'region', fallback: 'none' })

const formatDate = (date: Date) => {
  const year = date.getFullYear()
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  return `${year}-${month}-${day}`
}

// 用本地时区构造日期，避免 yyyy-MM-dd 经过 UTC 解析后偏移一天。
const parseDay = (value: string) => {
  const [year, month, day] = value.split('-').map(Number)
  return new Date(year, month - 1, day)
}

const dayLabel = (date: Date, today: Date) => {
  const difference = Math.round((date.getTime() - today.getTime()) / 86400000)
  switch (difference) {
    case 0: return '今天'
    case 1: return '明天'
    case 2: return '后天'
    case -1: return '昨天'
    case -2: return '前天'
    default: return weekdayFormatter.format(date)
  }
}

export default function EpgChannelPage() {
  const { channelId: urlChannelId } = useParams<{ channelId: string }>()
  const navigate = useNavigate()
  const { channels, getChannelPrograms, now } = useEpgContext()
  const selectedChannelId = urlChannelId ?? channels[0]?.id ?? null
  const selectedChannel = channels.find(channel => channel.id === selectedChannelId)
  const [programList, setProgramList] = useState<{ channelId: string; programs: EpgProgram[] } | null>(null)
  const [chosenDate, setChosenDate] = useState<string | null>(null)
  const today = formatDate(new Date(now))
  const programs = programList?.channelId === selectedChannelId ? programList.programs : emptyPrograms
  const loadingPrograms = selectedChannelId !== null && programList?.channelId !== selectedChannelId
  const currentProgramRef = useRef<HTMLLIElement>(null)
  const scheduleRef = useRef<HTMLDivElement>(null)
  const dateStripRef = useRef<HTMLDivElement>(null)
  const selectedDateRef = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    if (!selectedChannelId) return
    let active = true
    void getChannelPrograms(selectedChannelId).then(programs => {
      if (active) setProgramList({ channelId: selectedChannelId, programs })
    })
    return () => { active = false }
  }, [selectedChannelId, getChannelPrograms])

  const programsByDay = useMemo(() => {
    const days = new Map<string, EpgProgram[]>()
    for (const program of programs) {
      if (!Number.isFinite(program.startTime.getTime())) continue
      const date = formatDate(program.startTime)
      const day = days.get(date) ?? []
      day.push(program)
      days.set(date, day)
    }
    return days
  }, [programs])
  const dates = useMemo(() => [...new Set([...programsByDay.keys(), today])].sort(), [programsByDay, today])
  const date = chosenDate && dates.includes(chosenDate) ? chosenDate : today
  const filteredPrograms = programsByDay.get(date) ?? emptyPrograms
  const currentProgram = filteredPrograms.find(program => program.startTime.getTime() <= now && now < program.endTime.getTime())
  const localToday = parseDay(today)

  useEffect(() => {
    const strip = dateStripRef.current
    const activeDate = selectedDateRef.current
    if (!strip || !activeDate) return
    const bounds = strip.getBoundingClientRect()
    const button = activeDate.getBoundingClientRect()
    if (button.left < bounds.left) strip.scrollLeft += button.left - bounds.left
    if (button.right > bounds.right) strip.scrollLeft += button.right - bounds.right
  }, [date, dates, selectedChannelId])

  useEffect(() => {
    const schedule = scheduleRef.current
    const row = currentProgramRef.current
    if (loadingPrograms || !schedule || !row) return
    const offset = row.getBoundingClientRect().top - schedule.getBoundingClientRect().top
    const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches
    schedule.scrollTo({ top: schedule.scrollTop + offset - 24, behavior: reducedMotion ? 'auto' : 'smooth' })
  }, [currentProgram?.id, loadingPrograms, selectedChannelId])

  if (!selectedChannel) {
    return <p className="flex h-full items-center justify-center text-sm text-muted-foreground">未找到频道信息</p>
  }

  const { language, country } = selectedChannel
  const languageLabel = /^[a-z]{2,3}(-[a-z]{4})?(-[a-z]{2})?$/i.test(language) ? languageNames.of(language) ?? language : language
  const countryLabel = /^[a-z]{2}$/i.test(country) ? regionNames.of(country) ?? country : country
  const channelMetadata = [...selectedChannel.categories, languageLabel, countryLabel].filter(Boolean).join(' · ')

  return (
    <div className="grid h-full min-h-0 grid-cols-1 overflow-hidden rounded-xl border bg-card lg:grid-cols-[minmax(10rem,13.5rem)_minmax(0,1fr)]">
      <aside className="hidden min-h-0 min-w-0 flex-col border-r bg-muted/25 lg:flex">
        <div className="flex items-center justify-between gap-2 border-b px-4 py-3.5">
          <p className="text-sm font-semibold">频道</p>
          <span className="text-xs tabular-nums text-muted-foreground">{channels.length}</span>
        </div>
        <nav aria-label="频道列表" className="min-h-0 flex-1 overflow-y-auto p-2">
          {channels.map(channel => (
            <Link
              key={channel.id}
              to={`/epg/channels/${encodeURIComponent(channel.id)}`}
              aria-current={selectedChannelId === channel.id ? 'page' : undefined}
              className={cn(
                'flex min-h-10 min-w-0 items-center gap-2.5 rounded-md px-2.5 py-1.5 text-sm transition-colors focus-visible:outline focus-visible:outline-2 focus-visible:outline-ring',
                selectedChannelId === channel.id ? 'bg-emerald-50 font-semibold text-emerald-900 dark:bg-emerald-500/15 dark:text-emerald-200' : 'hover:bg-muted',
              )}
            >
              <ChannelMark channel={channel} className="h-7 w-7 rounded-md" />
              <span className="truncate" title={channel.name}>{channel.name}</span>
            </Link>
          ))}
        </nav>
      </aside>

      <section className="flex min-h-0 min-w-0 flex-col" aria-labelledby="epg-channel-name">
        <header className="shrink-0 border-b px-4 py-4 sm:px-6">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div className="flex min-w-0 items-center gap-3">
              <ChannelMark channel={selectedChannel} className="h-11 w-11" />
              <div className="min-w-0">
                <h2 id="epg-channel-name" className="truncate text-xl font-semibold tracking-tight sm:text-2xl" title={selectedChannel.name}>
                  {selectedChannel.name}
                </h2>
                {channelMetadata && (
                  <p className="truncate text-xs text-muted-foreground" title={channelMetadata}>{channelMetadata}</p>
                )}
              </div>
            </div>
            <Select value={selectedChannel.id} onValueChange={id => navigate(`/epg/channels/${encodeURIComponent(id)}`)}>
              <SelectTrigger aria-label="切换频道" className="h-10 w-full sm:w-52 lg:hidden">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {channels.map(channel => <SelectItem key={channel.id} value={channel.id}>{channel.name}</SelectItem>)}
              </SelectContent>
            </Select>
          </div>
        </header>

        <div ref={dateStripRef} className="min-w-0 shrink-0 overflow-x-auto border-b bg-muted/25 px-3 py-2 sm:px-5">
          <ToggleGroup
            type="single"
            aria-label="节目日期"
            value={date}
            onValueChange={value => value && setChosenDate(value)}
            className="w-max min-w-full justify-start gap-1.5"
          >
            {dates.map(value => {
              const localDate = parseDay(value)
              return (
                <ToggleGroupItem
                  key={value}
                  ref={value === date ? selectedDateRef : undefined}
                  value={value}
                  className="h-auto min-w-[4.25rem] flex-col gap-0.5 px-3 py-1.5 text-xs data-[state=on]:bg-foreground data-[state=on]:text-background"
                >
                  <span className="font-semibold">{dayLabel(localDate, localToday)}</span>
                  <span className="tabular-nums opacity-75">{shortDateFormatter.format(localDate)}</span>
                </ToggleGroupItem>
              )
            })}
          </ToggleGroup>
        </div>

        <div key={`${selectedChannel.id}/${date}`} ref={scheduleRef} className="min-h-0 flex-1 overflow-y-auto overscroll-contain">
          {loadingPrograms ? (
            <Loading tip="加载节目表..." />
          ) : filteredPrograms.length === 0 ? (
            <p className="flex h-full min-h-32 items-center justify-center px-4 text-center text-sm text-muted-foreground">当天暂无节目</p>
          ) : (
            <ol className="divide-y">
              {filteredPrograms.map(program => {
                const isCurrent = program === currentProgram
                const isPast = program.endTime.getTime() <= now

                return (
                  <li
                    key={program.id}
                    ref={isCurrent ? currentProgramRef : null}
                    className={cn(
                      'grid grid-cols-[2.8rem_minmax(0,1fr)_auto] items-start gap-2.5 border-l-2 border-l-transparent px-3.5 py-3.5 transition-colors sm:grid-cols-[3.5rem_minmax(0,1fr)_auto] sm:gap-4 sm:px-5',
                      isCurrent ? 'border-l-emerald-500 bg-emerald-50/70 dark:bg-emerald-500/10' : 'hover:bg-muted/25',
                    )}
                  >
                    <div className="space-y-0.5 pt-0.5 tabular-nums">
                      <p className={cn('text-sm font-semibold', isCurrent && 'text-emerald-800 dark:text-emerald-300')}>
                        {formatProgramTime(program.startTime)}
                      </p>
                      <p className="text-xs text-muted-foreground">{formatProgramTime(program.endTime)}</p>
                    </div>
                    <div className="min-w-0 space-y-1.5">
                      {isCurrent && (
                        <p className="flex items-center gap-1.5 text-xs font-semibold text-emerald-800 dark:text-emerald-300">
                          <span aria-hidden="true" className="h-1.5 w-1.5 rounded-full bg-emerald-500" />
                          正在播出
                        </p>
                      )}
                      <h4 className={cn('break-words text-sm font-medium sm:text-base', isCurrent && 'font-semibold', isPast && 'text-muted-foreground')}>
                        {program.title}
                      </h4>
                      {program.description && !isPast && (
                        <p className="line-clamp-2 text-xs leading-relaxed text-muted-foreground sm:text-sm">{program.description}</p>
                      )}
                      {isCurrent && (
                        <div className="flex items-center gap-2.5 pt-1">
                          <Progress
                            aria-label="播出进度"
                            value={getProgramProgress(program, now)}
                            className="h-1 max-w-56 bg-emerald-600/15"
                            indicatorClassName="bg-emerald-500 motion-reduce:transition-none"
                          />
                          <span className="shrink-0 text-xs tabular-nums text-muted-foreground">
                            剩余 {Math.ceil((program.endTime.getTime() - now) / 60000)} 分钟
                          </span>
                        </div>
                      )}
                    </div>
                    <RecordProgramButton program={program} channelName={selectedChannel.name} compact />
                  </li>
                )
              })}
            </ol>
          )}
        </div>
      </section>
    </div>
  )
}
