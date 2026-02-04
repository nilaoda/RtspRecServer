import { Link, useLocation } from 'react-router-dom'
import {
  LayoutList,
  Tv,
  Video,
  CalendarDays,
  Settings,
  HardDrive,
  Clock,
  User,
  Menu as MenuIcon,
  Activity
} from 'lucide-react'
import { useAppContext } from '../../app/context'
import { formatBytes, formatDateTime } from '../shared/format'
import AppRoutes from '../routes.tsx'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import { ModeToggle } from '@/components/mode-toggle'
import {
  Sheet,
  SheetContent,
  SheetTrigger,
} from "@/components/ui/sheet"

const navItems = [
  { key: 'tasks', label: '任务管理', path: '/', icon: LayoutList },
  { key: 'channels', label: '频道配置', path: '/channels', icon: Tv },
  { key: 'recordings', label: '录制管理', path: '/recordings', icon: Video },
  { key: 'epg', label: '节目单', path: '/epg', icon: CalendarDays },
  { key: 'settings', label: '应用配置', path: '/settings', icon: Settings },
]

const LayoutShell = () => {
  const { systemStatus, now } = useAppContext()
  const location = useLocation()
  
  const diskFree = systemStatus ? formatBytes(systemStatus.disk.freeBytes) : '--'
  const diskTotal = systemStatus ? formatBytes(systemStatus.disk.totalBytes) : '--'
  const systemTime = formatDateTime(now)
  const user = systemStatus?.currentUser ?? '--'

  // Helper to determine if a route is active
  const isActive = (path: string) => {
    if (path === '/') return location.pathname === '/'
    return location.pathname.startsWith(path)
  }

  const currentTitle = navItems.find(item => isActive(item.path))?.label || 'RTSP Playback Hub'

  return (
    <div className="flex min-h-screen flex-col md:flex-row bg-background">
      {/* Mobile Sidebar */}
      <Sheet>
        <SheetTrigger asChild>
          <Button variant="outline" size="icon" className="md:hidden absolute left-4 top-3 z-50">
            <MenuIcon className="h-4 w-4" />
          </Button>
        </SheetTrigger>
        <SheetContent side="left" className="w-64 p-0">
          <div className="flex h-14 items-center border-b px-4 gap-2">
             <Activity className="h-5 w-5 text-primary" />
             <span className="font-bold text-lg">RTSP Hub</span>
          </div>
          <nav className="flex flex-col gap-1 p-4">
             {navItems.map((item) => (
                <Link
                  key={item.key}
                  to={item.path}
                  className={cn(
                    "flex items-center gap-3 rounded-lg px-3 py-2 transition-all hover:text-primary",
                    isActive(item.path)
                      ? "bg-secondary text-primary font-medium"
                      : "text-muted-foreground"
                  )}
                >
                  <item.icon className="h-4 w-4" />
                  {item.label}
                </Link>
              ))}
          </nav>
        </SheetContent>
      </Sheet>

      {/* Desktop Sidebar */}
      <aside className="hidden w-64 flex-col border-r bg-card/50 md:flex">
        <div className="flex h-14 items-center border-b px-6 gap-2">
          <Activity className="h-6 w-6 text-primary" />
          <span className="font-bold text-lg tracking-tight">RTSP Hub</span>
        </div>
        <nav className="flex-1 flex flex-col gap-1 p-4 overflow-y-auto">
          {navItems.map((item) => (
            <Link
              key={item.key}
              to={item.path}
              className={cn(
                "flex items-center gap-3 rounded-md px-3 py-2 text-sm transition-colors hover:bg-accent hover:text-accent-foreground",
                isActive(item.path)
                  ? "bg-secondary text-secondary-foreground shadow-sm font-medium"
                  : "text-muted-foreground"
              )}
            >
              <item.icon className="h-4 w-4" />
              {item.label}
            </Link>
          ))}
        </nav>
        <div className="border-t p-4 space-y-3 bg-muted/20">
             <div className="flex items-center gap-2 text-xs text-muted-foreground" title="Current User">
                <User className="h-3.5 w-3.5" />
                <span className="truncate font-medium">{user}</span>
             </div>
             <div className="flex items-center gap-2 text-xs text-muted-foreground" title="Disk Usage">
                <HardDrive className="h-3.5 w-3.5" />
                <span className="truncate">{diskFree} / {diskTotal}</span>
             </div>
             <div className="flex items-center gap-2 text-xs text-muted-foreground" title="System Time">
                <Clock className="h-3.5 w-3.5" />
                <span className="tabular-nums font-mono">{systemTime}</span>
             </div>
        </div>
      </aside>

      {/* Main Content */}
      <main className="flex-1 flex flex-col min-w-0 overflow-hidden bg-background">
        <header className="flex h-14 items-center justify-between border-b px-4 md:px-6 bg-background/95 backdrop-blur supports-[backdrop-filter]:bg-background/60 sticky top-0 z-10">
           <div className="flex items-center ml-10 md:ml-0">
             <h1 className="text-lg font-semibold md:text-xl text-foreground/90">
               {currentTitle}
             </h1>
           </div>
           <div className="flex items-center gap-4">
             <ModeToggle />
           </div>
        </header>
        <div className="flex-1 overflow-auto p-4 md:p-6">
            <div className="mx-auto w-full max-w-[1600px]">
              <AppRoutes />
            </div>
        </div>
      </main>
    </div>
  )
}

export default LayoutShell
