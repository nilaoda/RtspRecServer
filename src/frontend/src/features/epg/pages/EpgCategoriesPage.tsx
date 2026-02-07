import { Folder } from 'lucide-react'
import { useEpgContext } from '../context/useEpgContext'
import Loading from '../../shared/Loading'

import {
  Card,
  CardHeader,
  CardTitle,
  CardDescription,
} from "@/components/ui/card"

export default function EpgCategoriesPage() {
  const { categories, loading } = useEpgContext()

  if (loading && categories.length === 0) {
    return <Loading tip="正在加载分类列表..." />
  }

  return (
    <div className="space-y-6">
      <h2 className="text-2xl font-semibold flex items-center gap-2">
        <Folder className="h-6 w-6" />
        节目分类
      </h2>
      
      <div className="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-4">
        {categories.map((category) => (
          <Card key={category} className="hover:shadow-lg transition-shadow cursor-pointer">
            <CardHeader>
              <CardTitle className="flex items-center gap-2">
                <Folder className="h-4 w-4" />
                <span>{category}</span>
              </CardTitle>
              <CardDescription>
                点击查看该分类下的所有频道
              </CardDescription>
            </CardHeader>
          </Card>
        ))}
      </div>
    </div>
  )
}
