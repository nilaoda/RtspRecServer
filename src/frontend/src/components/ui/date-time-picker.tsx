import * as React from "react"
import { format } from "date-fns"
import { zhCN } from "date-fns/locale"
import { Calendar as CalendarIcon, Clock } from "lucide-react"
import { cn } from "@/lib/utils"
import { Button } from "@/components/ui/button"
import { Calendar } from "@/components/ui/calendar"
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover"

export function DateTimePicker({
  date,
  setDate,
}: {
  date?: Date
  setDate: (date: Date | undefined) => void
}) {
  const [open, setOpen] = React.useState(false)
  const [selectedDate, setSelectedDate] = React.useState<Date | undefined>(date)
  const portalContainerRef = React.useRef<HTMLDivElement>(null)
  const hourRef = React.useRef<HTMLUListElement>(null)
  const minuteRef = React.useRef<HTMLUListElement>(null)
  const secondRef = React.useRef<HTMLUListElement>(null)

  // 同步外部状态到内部
  React.useEffect(() => {
    if (open) {
      setSelectedDate(date || new Date())
    }
  }, [open, date])

  // 自动滚动到选中时间
  React.useEffect(() => {
    if (open && selectedDate) {
        // 使用 setTimeout 确保渲染完成后滚动
        const timer = setTimeout(() => {
            const scrollTo = (ref: React.RefObject<HTMLUListElement | null>, value: number) => {
                if (ref.current) {
                    const buttonHeight = 28 // h-7
                    ref.current.scrollTop = value * buttonHeight
                }
            }
            scrollTo(hourRef, selectedDate.getHours())
            scrollTo(minuteRef, selectedDate.getMinutes())
            scrollTo(secondRef, selectedDate.getSeconds())
        }, 0)
        return () => clearTimeout(timer)
    }
  }, [open, selectedDate])

  const handleDateSelect = (d: Date | undefined) => {
    if (!d) return
    const newDate = new Date(d)
    const current = selectedDate || new Date()
    newDate.setHours(current.getHours())
    newDate.setMinutes(current.getMinutes())
    newDate.setSeconds(current.getSeconds())
    setSelectedDate(newDate)
  }

  const handleTimeChange = (type: 'hour' | 'minute' | 'second', value: number) => {
      const current = selectedDate || new Date()
      const newDate = new Date(current)
      
      if (type === 'hour') newDate.setHours(value)
      if (type === 'minute') newDate.setMinutes(value)
      if (type === 'second') newDate.setSeconds(value)
      
      setSelectedDate(newDate)
  }

  const handleNow = () => {
      const now = new Date()
      setSelectedDate(now)
  }

  const handleConfirm = () => {
      setDate(selectedDate)
      setOpen(false)
  }

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button
          variant={"outline"}
          className={cn(
            "w-full justify-start text-left font-normal",
            !date && "text-muted-foreground"
          )}
        >
          <CalendarIcon className="mr-2 h-4 w-4" />
          {date ? format(date, "yyyy-MM-dd HH:mm:ss", { locale: zhCN }) : <span>选择日期和时间</span>}
        </Button>
      </PopoverTrigger>
      <div ref={portalContainerRef} />
      <PopoverContent container={portalContainerRef.current} className="w-auto p-0" align="start">
        <div className="flex flex-col sm:flex-row divide-y sm:divide-y-0 sm:divide-x">
            <Calendar
                mode="single"
                selected={selectedDate}
                onSelect={handleDateSelect}
                initialFocus
                locale={zhCN}
            />
            <div className="flex flex-col sm:w-[150px]">
                <div className="flex items-center justify-center py-3 border-b text-sm font-medium">
                    <Clock className="mr-2 h-4 w-4" />
                    时间选择
                </div>
                <div className="flex h-[300px] divide-x relative">
                    <ul ref={hourRef} className="flex-1 h-full overflow-y-auto scrollbar-hide py-[136px]">
                        {Array.from({ length: 24 }, (_, i) => i).map((hour) => (
                            <li
                                key={hour}
                                className={cn(
                                    "h-7 flex items-center justify-center text-xs cursor-pointer rounded-sm mx-1",
                                    selectedDate && selectedDate.getHours() === hour 
                                        ? "bg-primary text-primary-foreground" 
                                        : "hover:bg-muted text-foreground"
                                )}
                                onClick={() => handleTimeChange('hour', hour)}
                            >
                                {hour.toString().padStart(2, '0')}
                            </li>
                        ))}
                    </ul>
                    <ul ref={minuteRef} className="flex-1 h-full overflow-y-auto scrollbar-hide py-[136px]">
                        {Array.from({ length: 60 }, (_, i) => i).map((minute) => (
                            <li
                                key={minute}
                                className={cn(
                                    "h-7 flex items-center justify-center text-xs cursor-pointer rounded-sm mx-1",
                                    selectedDate && selectedDate.getMinutes() === minute 
                                        ? "bg-primary text-primary-foreground" 
                                        : "hover:bg-muted text-foreground"
                                )}
                                onClick={() => handleTimeChange('minute', minute)}
                            >
                                {minute.toString().padStart(2, '0')}
                            </li>
                        ))}
                    </ul>
                    <ul ref={secondRef} className="flex-1 h-full overflow-y-auto scrollbar-hide py-[136px]">
                        {Array.from({ length: 60 }, (_, i) => i).map((second) => (
                            <li
                                key={second}
                                className={cn(
                                    "h-7 flex items-center justify-center text-xs cursor-pointer rounded-sm mx-1",
                                    selectedDate && selectedDate.getSeconds() === second 
                                        ? "bg-primary text-primary-foreground" 
                                        : "hover:bg-muted text-foreground"
                                )}
                                onClick={() => handleTimeChange('second', second)}
                            >
                                {second.toString().padStart(2, '0')}
                            </li>
                        ))}
                    </ul>
                </div>
            </div>
        </div>
        <div className="p-3 border-t flex justify-between items-center bg-muted/20">
            <Button variant="ghost" size="sm" onClick={handleNow} className="text-xs h-8">
                此刻
            </Button>
            <Button size="sm" onClick={handleConfirm} className="text-xs h-8">
                确定
            </Button>
        </div>
      </PopoverContent>
    </Popover>
  )
}
