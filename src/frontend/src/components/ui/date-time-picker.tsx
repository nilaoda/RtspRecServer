import * as React from "react"
import { format } from "date-fns"
import { zhCN } from "date-fns/locale"
import { Calendar as CalendarIcon } from "lucide-react"
import { cn } from "@/lib/utils"
import { Button } from "@/components/ui/button"
import { Calendar } from "@/components/ui/calendar"
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from "@/components/ui/dialog"
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover"

const compactPicker = "(max-width: 639px), (max-height: 560px), (pointer: coarse)"

function TimeColumn({ label, count, value, onChange }: {
  label: string
  count: number
  value: number
  onChange: (value: number) => void
}) {
  const listRef = React.useRef<HTMLUListElement>(null)

  React.useLayoutEffect(() => {
    const list = listRef.current
    const selected = list?.querySelector<HTMLButtonElement>('[aria-pressed="true"]')
    if (list && selected) {
      list.scrollTop = selected.offsetTop - (list.clientHeight - selected.offsetHeight) / 2
    }
  }, [value])

  const handleKeyDown = (event: React.KeyboardEvent<HTMLUListElement>) => {
    let next: number
    switch (event.key) {
      case "ArrowUp":
        next = Math.max(0, value - 1)
        break
      case "ArrowDown":
        next = Math.min(count - 1, value + 1)
        break
      case "Home":
        next = 0
        break
      case "End":
        next = count - 1
        break
      default:
        return
    }

    event.preventDefault()
    onChange(next)
    listRef.current?.children[next]?.querySelector('button')?.focus({ preventScroll: true })
  }

  return (
    <div className="min-w-0 flex-1">
      <div className="border-b py-2 text-center text-sm font-medium">{label}</div>
      <ul
        ref={listRef}
        aria-label={label}
        className="relative h-44 touch-pan-y select-none overflow-y-auto overscroll-contain py-[66px] scrollbar-hide"
        onKeyDown={handleKeyDown}
      >
        {Array.from({ length: count }, (_, option) => (
          <li key={option} className="px-1">
            <button
              type="button"
              aria-pressed={option === value}
              tabIndex={option === value ? 0 : -1}
              className={cn(
                "h-11 w-full rounded-sm text-sm tabular-nums focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring",
                option === value ? "bg-primary text-primary-foreground" : "hover:bg-muted",
              )}
              onClick={() => onChange(option)}
            >
              {option.toString().padStart(2, "0")}
            </button>
          </li>
        ))}
      </ul>
    </div>
  )
}

export function DateTimePicker({
  date,
  setDate,
}: {
  date?: Date
  setDate: (date: Date | undefined) => void
}) {
  const [open, setOpen] = React.useState(false)
  const [compact, setCompact] = React.useState(() => window.matchMedia(compactPicker).matches)
  const [selectedDate, setSelectedDate] = React.useState(() => date ?? new Date())
  const [month, setMonth] = React.useState(selectedDate)

  React.useEffect(() => {
    const media = window.matchMedia(compactPicker)
    const update = () => setCompact(media.matches)
    media.addEventListener("change", update)
    return () => media.removeEventListener("change", update)
  }, [])

  const handleOpenChange = (next: boolean) => {
    if (next) {
      const current = date ?? new Date()
      setSelectedDate(current)
      setMonth(current)
    }
    setOpen(next)
  }

  const handleNow = () => {
    const now = new Date()
    setSelectedDate(now)
    setMonth(now)
  }

  const handleConfirm = () => {
    setDate(selectedDate)
    setOpen(false)
  }

  const handleDateSelect = (day: Date | undefined) => {
    if (!day) return
    const next = new Date(day)
    next.setHours(selectedDate.getHours(), selectedDate.getMinutes(), selectedDate.getSeconds())
    setSelectedDate(next)
  }

  const handleTimeChange = (part: "hour" | "minute" | "second", value: number) => {
    const next = new Date(selectedDate)
    if (part === "hour") next.setHours(value)
    if (part === "minute") next.setMinutes(value)
    if (part === "second") next.setSeconds(value)
    setSelectedDate(next)
  }

  const triggerButton = (
    <Button
      type="button"
      variant="outline"
      className={cn(
        "w-full min-w-0 justify-start text-left font-normal",
        !date && "text-muted-foreground",
      )}
    >
      <CalendarIcon className="mr-2 h-4 w-4 shrink-0" />
      <span className="truncate">{date ? format(date, "yyyy-MM-dd HH:mm:ss") : "选择日期和时间"}</span>
    </Button>
  )

  const pickerContent = (
    <>
      <div className="min-h-0 overflow-y-auto overscroll-contain">
        <div className="flex flex-col divide-y sm:flex-row sm:divide-x sm:divide-y-0">
          <Calendar
            className="mx-auto shrink-0"
            mode="single"
            selected={selectedDate}
            month={month}
            onMonthChange={setMonth}
            onSelect={handleDateSelect}
            locale={zhCN}
          />
          <div className="flex shrink-0 divide-x sm:w-44">
            <TimeColumn label="小时" count={24} value={selectedDate.getHours()} onChange={(value) => handleTimeChange("hour", value)} />
            <TimeColumn label="分钟" count={60} value={selectedDate.getMinutes()} onChange={(value) => handleTimeChange("minute", value)} />
            <TimeColumn label="秒" count={60} value={selectedDate.getSeconds()} onChange={(value) => handleTimeChange("second", value)} />
          </div>
        </div>
      </div>
      <div className="flex shrink-0 items-center justify-between border-t bg-muted/20 p-3">
        <Button type="button" variant="ghost" onClick={handleNow}>此刻</Button>
        <Button type="button" onClick={handleConfirm}>确定</Button>
      </div>
    </>
  )

  if (compact) {
    return (
      <Dialog open={open} onOpenChange={handleOpenChange}>
        <DialogTrigger asChild>{triggerButton}</DialogTrigger>
        <DialogContent
          aria-describedby={undefined}
          className="flex max-h-[calc(100dvh-2rem)] w-[calc(100%-2rem)] max-w-md flex-col gap-0 overflow-hidden rounded-lg p-0"
        >
          <DialogHeader className="shrink-0 border-b px-10 py-4">
            <DialogTitle>选择日期和时间</DialogTitle>
          </DialogHeader>
          {pickerContent}
        </DialogContent>
      </Dialog>
    )
  }

  return (
    <Popover modal open={open} onOpenChange={handleOpenChange}>
      <PopoverTrigger asChild>{triggerButton}</PopoverTrigger>
      <PopoverContent
        aria-label="选择日期和时间"
        className="flex max-h-[var(--radix-popover-content-available-height)] w-auto flex-col overflow-hidden p-0"
        align="start"
        collisionPadding={16}
      >
        {pickerContent}
      </PopoverContent>
    </Popover>
  )
}
