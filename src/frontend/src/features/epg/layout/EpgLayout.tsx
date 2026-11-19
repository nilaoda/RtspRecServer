import { Suspense } from 'react'
import { RefreshCw, ArrowLeft } from 'lucide-react'
import { useEpgContext } from '../context/useEpgContext'
import Loading from '../../shared/Loading'
import { Link, Outlet, useLocation } from 'react-router-dom'
import { Button } from "@/components/ui/button"
import { cn } from '@/lib/utils'
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
  const { refresh, loading, refreshing } = useEpgContext()
  const location = useLocation()

  const isDetailPage = location.pathname.includes('/epg/channels/')
  const loadingIndicator = <Loading tip="正在加载节目表..." />

  return (
    <div className={cn(
      'flex flex-col gap-4 sm:gap-5',
      isDetailPage
        ? 'h-[calc(100dvh-5.5rem)] min-h-[23rem] md:h-[calc(100dvh-6.5rem)]'
        : 'min-h-[calc(100dvh-5.5rem)] md:min-h-[calc(100dvh-6.5rem)]',
    )}>
      {isDetailPage ? (
        <div className="shrink-0">
          <Button variant="ghost" asChild className="-ml-2 gap-1.5 px-2 text-muted-foreground">
            <Link to="/epg">
              <ArrowLeft className="h-4 w-4" aria-hidden="true" />
              返回概览
            </Link>
          </Button>
        </div>
      ) : (
        <div className="flex shrink-0 flex-wrap items-center justify-between gap-3 border-b pb-3">
          <h2 className="text-sm font-semibold text-muted-foreground">电子节目表</h2>
          <AlertDialog>
            <AlertDialogTrigger asChild>
              <Button variant="outline" size="sm" disabled={loading || refreshing} className="gap-2">
                <RefreshCw className="h-3.5 w-3.5" aria-hidden="true" />
                {refreshing ? '正在刷新节目表...' : '刷新节目表'}
              </Button>
            </AlertDialogTrigger>
            <AlertDialogContent>
              <AlertDialogHeader>
                <AlertDialogTitle>刷新节目表</AlertDialogTitle>
                <AlertDialogDescription>
                  更新节目表需要从远程服务器下载并解析大量数据，请尽量不要频繁操作。确定要继续吗？
                </AlertDialogDescription>
              </AlertDialogHeader>
              <AlertDialogFooter>
                <AlertDialogCancel>取消</AlertDialogCancel>
                <AlertDialogAction onClick={refresh}>确定</AlertDialogAction>
              </AlertDialogFooter>
            </AlertDialogContent>
          </AlertDialog>
        </div>
      )}

      <div className="flex-1 min-h-0" aria-busy={loading || refreshing}>
        {loading ? loadingIndicator : (
          <Suspense fallback={loadingIndicator}>
            <div className="h-full">
              <Outlet />
            </div>
          </Suspense>
        )}
      </div>
    </div>
  )
}
