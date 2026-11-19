import { Suspense } from 'react'
import { CalendarDays, RefreshCw, ArrowLeft } from 'lucide-react'
import { useEpgContext } from '../context/useEpgContext'
import Loading from '../../shared/Loading'
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
  const { refresh, loading, refreshing } = useEpgContext()
  const location = useLocation()
  const navigate = useNavigate()

  const isDetailPage = location.pathname.includes('/epg/channels/')
  const loadingIndicator = <Loading tip="正在加载节目表..." />

  return (
    <div className="flex flex-col h-[calc(100vh-112px)] space-y-6">
      <div className="flex justify-between items-center flex-shrink-0">
        <h2 className="text-2xl font-semibold flex items-center gap-2">
          <CalendarDays className="h-6 w-6" />
          电子节目表
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
                <Button disabled={loading || refreshing}>
                  <RefreshCw className="mr-2 h-4 w-4" />
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
          )}
        </div>
      </div>
      
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
