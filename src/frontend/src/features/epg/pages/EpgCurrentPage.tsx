import { PlayCircle } from 'lucide-react'
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

export default function EpgCurrentPage() {
  const { currentPrograms, loading, now } = useEpgContext()

  if (loading && currentPrograms.length === 0) {
    return <Loading tip="正在加载当前节目..." />
  }

  const getProgress = (startTime: Date, endTime: Date) => {
    const start = startTime.getTime()
    const end = endTime.getTime()
    
    if (now >= end) return 100
    if (now <= start) return 0
    
    return Math.round(((now - start) / (end - start)) * 100)
  }

  return (
    <div className="space-y-6">
      <h2 className="text-2xl font-semibold flex items-center gap-2">
        <PlayCircle className="h-6 w-6" />
        当前播放
      </h2>

      <div className="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-4">
        {currentPrograms.map((currentProgram) => {
          const progress = getProgress(currentProgram.startTime, currentProgram.endTime)
          
          return (
            <Card key={currentProgram.channelId} className="bg-primary/5 border-primary/20">
              <CardHeader className="flex flex-row items-center gap-4 pb-2">
                <div className="w-12 h-12 bg-primary text-primary-foreground rounded-full flex items-center justify-center">
                    <PlayCircle className="h-6 w-6" />
                </div>
                <div className="flex flex-col">
                    <CardTitle className="text-base">{currentProgram.channelName}</CardTitle>
                </div>
              </CardHeader>
              <CardContent>
                <div className="space-y-2">
                    <div className="font-bold text-sm">
                        {currentProgram.program.title}
                    </div>
                    <div className="text-xs text-muted-foreground flex justify-between">
                        <span>{currentProgram.startTime.toLocaleTimeString()}</span>
                        <span>{currentProgram.endTime.toLocaleTimeString()}</span>
                    </div>
                    {currentProgram.program.description && (
                        <div className="text-xs text-muted-foreground line-clamp-2">
                            {currentProgram.program.description}
                        </div>
                    )}
                    {currentProgram.program.category && (
                        <div>
                            <Badge variant="secondary" className="text-[10px] px-1 py-0">
                                {currentProgram.program.category}
                            </Badge>
                        </div>
                    )}
                    <div className="space-y-1">
                        <Progress value={progress} className="h-1.5" />
                        <div className="text-[10px] text-right text-muted-foreground">{progress}%</div>
                    </div>
                </div>
              </CardContent>
            </Card>
          )
        })}
      </div>
    </div>
  )
}
