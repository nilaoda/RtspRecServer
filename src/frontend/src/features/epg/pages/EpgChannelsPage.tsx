import { PlayCircle } from 'lucide-react'
import { useEpgContext } from '../context/useEpgContext'
import Loading from '../../shared/Loading'

import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  CardDescription,
} from "@/components/ui/card"
import { Avatar, AvatarFallback, AvatarImage } from "@/components/ui/avatar"
import { Badge } from "@/components/ui/badge"

export default function EpgChannelsPage() {
  const { channels, loading } = useEpgContext()

  if (loading && channels.length === 0) {
    return <Loading tip="正在加载频道列表..." />
  }

  return (
    <div className="space-y-6">
      <h2 className="text-2xl font-semibold flex items-center gap-2">
        <PlayCircle className="h-6 w-6" />
        所有频道
      </h2>

      <div className="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-4">
        {channels.map((channel) => (
          <Card key={channel.id} className="hover:shadow-lg transition-shadow cursor-pointer">
            <CardHeader className="flex flex-row items-center gap-4">
              <Avatar className="h-12 w-12">
                <AvatarImage src={channel.iconUrl} alt={channel.name} />
                <AvatarFallback className="bg-primary text-primary-foreground font-bold">
                    {channel.name.charAt(0)}
                </AvatarFallback>
              </Avatar>
              <div className="flex flex-col overflow-hidden">
                  <CardTitle className="text-base truncate">{channel.name}</CardTitle>
                  <CardDescription className="flex flex-wrap gap-1 mt-1">
                    {channel.categories.slice(0, 2).map((category) => (
                      <Badge key={category} variant="secondary" className="text-[10px] px-1 py-0">
                        {category}
                      </Badge>
                    ))}
                  </CardDescription>
              </div>
            </CardHeader>
            <CardContent>
                <div className="text-xs text-muted-foreground">
                    {channel.language} · {channel.country}
                </div>
            </CardContent>
          </Card>
        ))}
      </div>
    </div>
  )
}
