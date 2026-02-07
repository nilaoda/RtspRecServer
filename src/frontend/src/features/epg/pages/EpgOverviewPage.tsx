import { useNavigate } from 'react-router-dom'
import { PlayCircle, Eye, Clock, ChevronRight, Activity } from 'lucide-react'

import { useEpgContext } from '../context/useEpgContext'
import Loading from '../../shared/Loading'

import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"
import { Badge } from "@/components/ui/badge"
import { Progress } from "@/components/ui/progress"

export default function EpgOverviewPage() {
  const navigate = useNavigate()
  const { channels, currentPrograms, loading, now } = useEpgContext()

  if (loading && channels.length === 0) {
    return <Loading tip="正在加载节目单数据..." />
  }

  const getChannelCurrentProgram = (channelId: string) => {
    return currentPrograms.find(p => p.channelId === channelId)
  }

  return (
    <div className="space-y-6">
      <div className="flex items-center gap-2">
          <PlayCircle className="h-6 w-6 text-primary" />
          <h3 className="text-2xl font-semibold tracking-tight">EPG 实时节目单</h3>
      </div>

      <div className="grid gap-4 md:grid-cols-2">
        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
            <CardTitle className="text-sm font-medium">
              在线频道
            </CardTitle>
            <Eye className="h-4 w-4 text-muted-foreground" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold">{channels.length}</div>
          </CardContent>
        </Card>
        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
            <CardTitle className="text-sm font-medium">
              正在播出
            </CardTitle>
            <Clock className="h-4 w-4 text-muted-foreground" />
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold">{currentPrograms.length}</div>
          </CardContent>
        </Card>
      </div>

      <div className="space-y-4">
        <h4 className="text-xl font-semibold tracking-tight">所有频道正在播放</h4>
        {channels.length > 0 ? (
          <div className="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-4">
            {channels.map((channel) => {
              const currentInfo = getChannelCurrentProgram(channel.id)
              const program = currentInfo?.program
              
              let progress = 0
              if (program) {
                const total = program.endTime.getTime() - program.startTime.getTime()
                const elapsed = now - program.startTime.getTime()
                progress = Math.min(Math.max(Math.round((elapsed / total) * 100), 0), 100)
              }

              return (
                  <Card
                    key={channel.id}
                    className={`h-full hover:shadow-lg transition-shadow cursor-pointer border-l-4 ${program ? 'border-l-primary bg-primary/5' : 'border-l-transparent'}`}
                    onClick={() => navigate(`/epg/channels/${channel.id}`)}
                  >
                    <CardContent className="p-4 flex flex-col h-full gap-3">
                        <div className="flex items-start justify-between">
                            <div className="flex items-center gap-3 overflow-hidden">
                                {channel.iconUrl ? (
                                    <img
                                        alt={channel.name}
                                        src={channel.iconUrl}
                                        className="w-10 h-10 object-contain rounded-md bg-white"
                                    />
                                ) : (
                                    <div className="w-10 h-10 flex items-center justify-center bg-muted rounded-md text-primary font-bold text-lg">
                                        {channel.name.charAt(0)}
                                    </div>
                                )}
                                <div className="flex-1 overflow-hidden">
                                    <div className="font-bold truncate text-base">{channel.name}</div>
                                    <div className="flex gap-1 mt-1">
                                        {channel.categories.slice(0, 1).map(cat => (
                                            <Badge key={cat} variant="secondary" className="text-[10px] px-1 py-0">{cat}</Badge>
                                        ))}
                                    </div>
                                </div>
                            </div>
                            <ChevronRight className="h-5 w-5 text-muted-foreground" />
                        </div>

                        <div className="flex-1 flex flex-col justify-end">
                            {program ? (
                                <div className="bg-background/50 p-2 rounded border space-y-2">
                                    <div className="text-primary font-medium text-sm truncate">
                                        正在播放：{program.title}
                                    </div>
                                    <div className="flex justify-between text-xs text-muted-foreground font-mono">
                                        <span>{program.startTime.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</span>
                                        <span>{program.endTime.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</span>
                                    </div>
                                    <Progress value={progress} className="h-1.5" />
                                </div>
                            ) : (
                                <div className="text-muted-foreground italic text-sm p-2">
                                    暂无节目信息
                                </div>
                            )}
                        </div>
                    </CardContent>
                  </Card>
              )
            })}
          </div>
        ) : (
          <div className="flex flex-col items-center justify-center py-10 text-muted-foreground">
            <Activity className="h-10 w-10 mb-2 opacity-20" />
            <p>暂无频道数据</p>
          </div>
        )}
      </div>
    </div>
  )
}
