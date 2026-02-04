import { CalendarDays, RefreshCw, ArrowLeft } from 'lucide-react'
import { useEpgData } from '../hooks/useEpgData'
import { Outlet, useLocation, useNavigate } from 'react-router-dom'
import { Button } from "@/components/ui/button"
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogTrigger,
} from "@/components/ui/alert-dialog"

export const EpgLayout: React.FC = () => {
  const { refresh, loading } = useEpgData()
  const location = useLocation()
  const navigate = useNavigate()

  const isDetailPage = location.pathname.includes('/epg/channels/')

  return (
    <div className="flex flex-col h-[calc(100vh-112px)] space-y-6">
      <div className="flex justify-between items-center flex-shrink-0">
        <h2 className="text-2xl font-semibold flex items-center gap-2">
          <CalendarDays className="h-6 w-6" />
          电子节目单
        </h2>
        <div className="flex gap-2">
          {isDetailPage ? (
            <Button
              variant="outline"
              onClick={() => navigate('/epg')}
            >
              <ArrowLeft className="mr-2 h-4 w-4" />
              返回概览
            </Button>
          ) : (
            <AlertDialog>
              <AlertDialogTrigger asChild>
                <Button disabled={loading}>
                    <RefreshCw className={`mr-2 h-4 w-4 ${loading ? 'animate-spin' : ''}`} />
                    刷新节目单
                </Button>
              </AlertDialogTrigger>
              <AlertDialogContent>
                <AlertDialogHeader>
                  <AlertDialogTitle>刷新节目单</AlertDialogTitle>
                  <AlertDialogDescription>
                    更新节目单需要从远程服务器下载并解析大量数据，请尽量不要频繁操作。确定要继续吗？
                  </AlertDialogDescription>
                </AlertDialogHeader>
                <AlertDialogFooter>
                  <AlertDialogCancel>取消</AlertDialogCancel>
                  <AlertDialogAction onClick={refresh}>确定</AlertDialogAction>
                </AlertDialogFooter>
              </AlertDialogContent>
            </AlertDialog>
          )}
        </div>
      </div>
      
      <div className="flex-1 flex flex-col min-h-0">
        {loading && (
             <div className="absolute inset-0 bg-background/50 z-50 flex items-center justify-center">
                 <div className="flex flex-col items-center gap-2">
                    <RefreshCw className="h-8 w-8 animate-spin text-primary" />
                    <span className="text-sm font-medium">节目单更新中...</span>
                 </div>
             </div>
        )}
        <div className={loading ? "opacity-50 pointer-events-none h-full" : "h-full"}>
            <Outlet />
        </div>
      </div>
    </div>
  )
}
