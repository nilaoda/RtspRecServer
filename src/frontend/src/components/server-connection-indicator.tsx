import { Loader2, Wifi, WifiOff } from "lucide-react"

import { useAppContext } from "@/app/context"
import { Button } from "@/components/ui/button"
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
} from "@/components/ui/tooltip"

export function ServerConnectionIndicator() {
  const { connectionStatus } = useAppContext()

  const { label, Icon, className } = (() => {
    if (connectionStatus === "connected") {
      return { label: "已连接", Icon: Wifi, className: "text-green-600 dark:text-green-500" }
    }
    if (connectionStatus === "connecting") {
      return { label: "连接中", Icon: Loader2, className: "text-muted-foreground animate-spin" }
    }
    if (connectionStatus === "reconnecting") {
      return { label: "重连中", Icon: Loader2, className: "text-yellow-600 dark:text-yellow-500 animate-spin" }
    }
    return { label: "已断开", Icon: WifiOff, className: "text-destructive" }
  })()

  return (
    <TooltipProvider>
      <Tooltip>
        <TooltipTrigger asChild>
          <Button variant="outline" size="icon" aria-label={`服务器连接：${label}`}>
            <Icon className={className} />
          </Button>
        </TooltipTrigger>
        <TooltipContent>
          <p>服务器连接：{label}</p>
        </TooltipContent>
      </Tooltip>
    </TooltipProvider>
  )
}

