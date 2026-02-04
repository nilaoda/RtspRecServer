import { useState, useEffect, useRef } from 'react'
import { PlayCircle, Clock, Calendar as CalendarIcon } from 'lucide-react'
import { useParams, useNavigate } from 'react-router-dom'
import { useEpgData } from '../hooks/useEpgData'
import type { EpgProgram } from '../types/epg'
import Loading from '../../shared/Loading'

import { Badge } from "@/components/ui/badge"
import { Progress } from "@/components/ui/progress"
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group"
import { ScrollArea } from "@/components/ui/scroll-area"
import { cn } from "@/lib/utils"

export default function EpgChannelPage() {
  const { channelId: urlChannelId } = useParams<{ channelId: string }>()
  const navigate = useNavigate()
  const { channels, getChannelPrograms, loading: globalLoading, now } = useEpgData()
  
  const [selectedChannelId, setSelectedChannelId] = useState<string | null>(urlChannelId || null)
  const [programs, setPrograms] = useState<EpgProgram[]>([])
  const [loadingPrograms, setLoadingPrograms] = useState(false)
  const [currentProgramId, setCurrentProgramId] = useState<string | null>(null)
  const [selectedDate, setSelectedDate] = useState<string>(() => {
    const d = new Date();
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  })

  const formatDate = (date: Date) => {
    const year = date.getFullYear();
    const month = String(date.getMonth() + 1).padStart(2, '0');
    const day = String(date.getDate()).padStart(2, '0');
    return `${year}-${month}-${day}`;
  }

  const currentProgramRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (programs.length > 0) {
      const current = programs.find(p => p.startTime.getTime() <= now && p.endTime.getTime() > now)
      if (current?.id !== currentProgramId) {
        setCurrentProgramId(current?.id || null)
      }
    }
  }, [now, programs, currentProgramId])

  useEffect(() => {
    if (currentProgramId && !loadingPrograms) {
      const timer = setTimeout(() => {
        currentProgramRef.current?.scrollIntoView({ behavior: 'smooth', block: 'center' })
      }, 500)
      return () => clearTimeout(timer)
    }
  }, [currentProgramId, loadingPrograms])

  useEffect(() => {
    if (urlChannelId) {
      setSelectedChannelId(urlChannelId)
    } else if (channels.length > 0 && !selectedChannelId) {
      setSelectedChannelId(channels[0].id)
    }
  }, [urlChannelId, channels, selectedChannelId])

  useEffect(() => {
    if (selectedChannelId) {
      const fetchPrograms = async () => {
        setLoadingPrograms(true)
        try {
          const data = await getChannelPrograms(selectedChannelId)
          setPrograms(data)
        } finally {
          setLoadingPrograms(false)
        }
      }
      fetchPrograms()
    }
  }, [selectedChannelId])

  const selectedChannel = channels.find(c => c.id === selectedChannelId)

  const dateOptions = Array.from({ length: 4 }).map((_, i) => {
    const d = new Date()
    d.setDate(d.getDate() - i)
    return {
      label: i === 0 ? '今天' : i === 1 ? '昨天' : formatDate(d),
      value: formatDate(d)
    }
  }).reverse()

  const filteredPrograms = programs.filter(p => {
    const programDate = formatDate(p.startTime)
    return programDate === selectedDate
  })

  if (globalLoading && channels.length === 0) {
    return <Loading tip="正在加载频道列表..." />
  }

  return (
    <div className="flex h-full bg-background overflow-hidden border rounded-lg">
      <aside className="w-64 border-r bg-muted/10 flex flex-col hidden md:flex">
        <div className="p-4 border-b font-semibold flex items-center gap-2 bg-background/50">
          <PlayCircle className="h-5 w-5" />
          频道列表
        </div>
        <ScrollArea className="flex-1">
          <div className="p-2 space-y-1">
            {channels.map(c => (
                <button
                    key={c.id}
                    onClick={() => {
                        setSelectedChannelId(c.id)
                        navigate(`/epg/channels/${c.id}`)
                    }}
                    className={cn(
                        "w-full text-left px-3 py-2 rounded-md text-sm flex items-center gap-2 transition-colors",
                        selectedChannelId === c.id ? "bg-secondary text-secondary-foreground font-medium" : "hover:bg-muted"
                    )}
                >
                    {c.iconUrl ? (
                        <img src={c.iconUrl} alt="" className="w-5 h-5 object-contain" />
                    ) : (
                        <div className="w-5 h-5 bg-muted rounded flex items-center justify-center text-[10px] font-bold">
                            {c.name.charAt(0)}
                        </div>
                    )}
                    <span className="truncate">{c.name}</span>
                </button>
            ))}
          </div>
        </ScrollArea>
      </aside>
      
      <main className="flex-1 flex flex-col min-w-0 h-full">
        {selectedChannel ? (
          <>
            <div className="p-6 border-b bg-background/50">
                <div className="flex justify-between items-start">
                  <div className="space-y-2">
                    <h2 className="text-2xl font-bold">{selectedChannel.name}</h2>
                    <div className="flex flex-wrap gap-2 items-center">
                      {selectedChannel.categories.map((category) => (
                        <Badge key={category} variant="secondary">
                          {category}
                        </Badge>
                      ))}
                      <span className="text-xs text-muted-foreground ml-2">
                        {selectedChannel.language} {selectedChannel.country && `· ${selectedChannel.country}`}
                      </span>
                    </div>
                  </div>
                  <div className="text-sm text-muted-foreground flex items-center gap-1">
                    <CalendarIcon className="h-4 w-4" />
                    {new Date().toLocaleDateString()}
                  </div>
                </div>
            </div>

            <div className="flex-1 flex flex-col min-h-0 bg-muted/5">
                <div className="p-4 border-b bg-background flex justify-between items-center sticky top-0 z-10">
                    <div className="flex items-center gap-2 font-medium">
                        <Clock className="h-4 w-4 text-muted-foreground" />
                        <span>节目单</span>
                    </div>
                    <ToggleGroup type="single" value={selectedDate} onValueChange={(val) => val && setSelectedDate(val)}>
                        {dateOptions.map(option => (
                            <ToggleGroupItem key={option.value} value={option.value} size="sm" className="text-xs px-2 h-7">
                                {option.label}
                            </ToggleGroupItem>
                        ))}
                    </ToggleGroup>
                </div>

                <div className="flex-1 overflow-y-auto p-4 relative">
                    {loadingPrograms ? (
                        <Loading tip="加载节目单..." />
                    ) : filteredPrograms.length > 0 ? (
                        <div className="space-y-4 relative ml-4 border-l pl-4 py-2">
                            {filteredPrograms.map((program) => {
                                const isCurrent = program.startTime.getTime() <= now && program.endTime.getTime() > now
                                const isPast = program.endTime.getTime() <= now
                                
                                let progress = 0
                                if (isCurrent) {
                                    const total = program.endTime.getTime() - program.startTime.getTime()
                                    const elapsed = now - program.startTime.getTime()
                                    progress = Math.min(Math.max(Math.round((elapsed / total) * 100), 0), 100)
                                }

                                return (
                                    <div 
                                        key={program.id} 
                                        ref={isCurrent ? currentProgramRef : null}
                                        className="relative"
                                    >
                                        <div className={cn(
                                            "absolute -left-[21px] top-3 w-2.5 h-2.5 rounded-full border-2 bg-background",
                                            isCurrent ? "border-primary bg-primary animate-pulse" : isPast ? "border-muted-foreground/30 bg-muted" : "border-primary"
                                        )} />
                                        <div className="flex gap-4">
                                            <div className={cn("w-14 text-xs font-mono pt-2 text-right", isCurrent ? "text-primary font-bold" : isPast ? "text-muted-foreground" : "")}>
                                                {program.startTime.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                                            </div>
                                            <div className={cn(
                                                "flex-1 p-3 rounded-lg border transition-colors",
                                                isCurrent ? "bg-primary/5 border-primary/20 shadow-sm" : "bg-card border-transparent hover:border-border hover:bg-accent/5",
                                                isPast && "opacity-60"
                                            )}>
                                                <div className="flex justify-between items-start gap-2">
                                                    <div className={cn("font-medium text-sm", isCurrent && "text-primary")}>
                                                        {program.title}
                                                    </div>
                                                    {isCurrent && <Badge className="text-[10px] px-1 py-0 h-5">正在播出</Badge>}
                                                </div>
                                                
                                                {isCurrent && (
                                                    <div className="mt-3 space-y-1">
                                                        <Progress value={progress} className="h-1.5" />
                                                        <div className="text-[10px] text-muted-foreground text-right">
                                                            剩余 {Math.round((program.endTime.getTime() - now) / 60000)} 分钟
                                                        </div>
                                                    </div>
                                                )}
                                                
                                                {program.description && !isPast && (
                                                    <div className="mt-2 text-xs text-muted-foreground line-clamp-2">
                                                        {program.description}
                                                    </div>
                                                )}
                                            </div>
                                        </div>
                                    </div>
                                )
                            })}
                        </div>
                    ) : (
                        <div className="h-full flex items-center justify-center text-muted-foreground">
                            该频道暂无节目单信息
                        </div>
                    )}
                </div>
            </div>
          </>
        ) : (
          <div className="h-full flex items-center justify-center text-muted-foreground">
            未找到频道信息
          </div>
        )}
      </main>
    </div>
  )
}
