import { Folder } from 'lucide-react'
import { useEpgContext } from '../context/useEpgContext'
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  CardDescription,
} from "@/components/ui/card"
import { Avatar, AvatarFallback, AvatarImage } from "@/components/ui/avatar"

export default function EpgCategoryChannelsPage() {
  const { channels } = useEpgContext()
  const categoryChannels = channels.slice(0, 4) // Mock data logic preserved

  return (
    <div className="space-y-6">
      <h2 className="text-2xl font-semibold flex items-center gap-2">
        <Folder className="h-6 w-6" />
        分类频道
      </h2>

      <div className="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-4">
        {categoryChannels.map((channel) => (
          <Card key={channel.id} className="hover:shadow-lg transition-shadow cursor-pointer">
            <CardHeader className="flex flex-row items-center gap-4">
              <Avatar className="h-12 w-12">
                <AvatarImage src={channel.iconUrl} alt={channel.name} />
                <AvatarFallback className="bg-primary text-primary-foreground font-bold">
                    {channel.name.charAt(0)}
                </AvatarFallback>
              </Avatar>
              <div className="flex flex-col">
                  <CardTitle className="text-base">{channel.name}</CardTitle>
                  <CardDescription className="line-clamp-1">{channel.description}</CardDescription>
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
