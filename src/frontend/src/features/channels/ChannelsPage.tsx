import { useCallback, useState } from 'react'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import * as z from 'zod'
import { Plus, Save, Trash2 } from 'lucide-react'
import { toast } from 'sonner'

import { useAppContext } from '../../app/context'

import { Button } from "@/components/ui/button"
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"
import {
  Form,
  FormControl,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
} from "@/components/ui/form"
import { Input } from "@/components/ui/input"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
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
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
} from "@/components/ui/tooltip"

const channelSchema = z.object({
  name: z.string().min(1, "请输入频道名称"),
  url: z.string().min(1, "请输入频道地址"),
})

type ChannelFormValues = z.infer<typeof channelSchema>

const ChannelsPage = () => {
  const { channels, addChannel, updateChannel, deleteChannel } = useAppContext()
  const [drafts, setDrafts] = useState<Record<number, { name: string; url: string }>>({})

  const form = useForm<ChannelFormValues>({
    resolver: zodResolver(channelSchema),
    defaultValues: {
      name: '',
      url: '',
    },
  })

  const onAdd = async (values: ChannelFormValues) => {
    try {
        await addChannel({ name: values.name.trim(), url: values.url.trim() })
        form.reset()
    } catch (e) {
        // Handled in context
    }
  }

  const onSave = useCallback(
    async (id: number) => {
      const draft = drafts[id]
      if (!draft || !draft.name.trim() || !draft.url.trim()) {
        toast.error('频道名称与地址不能为空')
        return
      }
      try {
        await updateChannel({ id, name: draft.name.trim(), url: draft.url.trim() })
        setDrafts((prev) => {
          const next = { ...prev }
          delete next[id]
          return next
        })
      } catch (e) {
          // Handled in context
      }
    },
    [drafts, updateChannel],
  )

  return (
    <div className="space-y-6">
      <Card>
        <CardHeader className="pb-3">
            <CardTitle className="text-lg font-medium">添加频道</CardTitle>
        </CardHeader>
        <CardContent>
            <Form {...form}>
                <form onSubmit={form.handleSubmit(onAdd)} className="flex flex-col md:flex-row gap-4 items-end">
                    <FormField
                        control={form.control}
                        name="name"
                        render={({ field }) => (
                            <FormItem className="w-full md:w-[250px]">
                                <FormLabel>频道名称</FormLabel>
                                <FormControl>
                                    <Input placeholder="输入频道名称" {...field} />
                                </FormControl>
                                <FormMessage />
                            </FormItem>
                        )}
                    />
                    <FormField
                        control={form.control}
                        name="url"
                        render={({ field }) => (
                            <FormItem className="flex-1 w-full">
                                <FormLabel>频道地址</FormLabel>
                                <FormControl>
                                    <Input placeholder="rtsp://..." {...field} />
                                </FormControl>
                                <FormMessage />
                            </FormItem>
                        )}
                    />
                    <Button type="submit" className="w-full md:w-auto">
                        <Plus className="mr-2 h-4 w-4" />
                        添加频道
                    </Button>
                </form>
            </Form>
        </CardContent>
      </Card>

      <Card>
        <CardHeader className="pb-3">
            <CardTitle className="text-lg font-medium">频道列表</CardTitle>
        </CardHeader>
        <CardContent className="p-0">
            <div className="rounded-md border-t">
                <Table>
                    <TableHeader>
                        <TableRow>
                            <TableHead className="w-[80px]">ID</TableHead>
                            <TableHead className="w-[250px]">名称</TableHead>
                            <TableHead className="min-w-[300px]">地址</TableHead>
                            <TableHead className="w-[150px] text-right">操作</TableHead>
                        </TableRow>
                    </TableHeader>
                    <TableBody>
                        {channels.length === 0 ? (
                            <TableRow>
                                <TableCell colSpan={4} className="h-24 text-center">
                                    暂无频道
                                </TableCell>
                            </TableRow>
                        ) : (
                            channels.map((channel) => {
                                const draft = drafts[channel.id] ?? channel
                                const hasChanged = draft.name !== channel.name || draft.url !== channel.url
                                
                                return (
                                    <TableRow key={channel.id}>
                                        <TableCell className="font-mono text-xs py-2">{channel.id}</TableCell>
                                        <TableCell className="py-2">
                                            <Input 
                                                value={draft.name} 
                                                onChange={(e) => setDrafts(prev => ({
                                                    ...prev,
                                                    [channel.id]: { ...draft, name: e.target.value }
                                                }))}
                                                className="h-8"
                                            />
                                        </TableCell>
                                        <TableCell className="py-2">
                                            <Input 
                                                value={draft.url} 
                                                onChange={(e) => setDrafts(prev => ({
                                                    ...prev,
                                                    [channel.id]: { ...draft, url: e.target.value }
                                                }))}
                                                className="h-8 font-mono text-xs"
                                            />
                                        </TableCell>
                                        <TableCell className="text-right py-2">
                                            <div className="flex justify-end gap-1">
                                                <TooltipProvider>
                                                    <Tooltip>
                                                        <TooltipTrigger asChild>
                                                            <Button 
                                                                size="icon" 
                                                                variant="ghost" 
                                                                className="h-8 w-8 text-primary hover:text-primary hover:bg-primary/10" 
                                                                disabled={!hasChanged}
                                                                onClick={() => onSave(channel.id)}
                                                            >
                                                                <Save className="h-4 w-4" />
                                                                <span className="sr-only">Save</span>
                                                            </Button>
                                                        </TooltipTrigger>
                                                        <TooltipContent>
                                                            <p>保存修改</p>
                                                        </TooltipContent>
                                                    </Tooltip>
                                                </TooltipProvider>
                                                
                                                <TooltipProvider>
                                                    <Tooltip>
                                                        <TooltipTrigger asChild>
                                                            <AlertDialog>
                                                                <AlertDialogTrigger asChild>
                                                                    <Button size="icon" variant="ghost" className="h-8 w-8 text-destructive hover:text-destructive hover:bg-destructive/10">
                                                                        <Trash2 className="h-4 w-4" />
                                                                        <span className="sr-only">Delete</span>
                                                                    </Button>
                                                                </AlertDialogTrigger>
                                                                <AlertDialogContent>
                                                                    <AlertDialogHeader>
                                                                        <AlertDialogTitle>确认删除该频道？</AlertDialogTitle>
                                                                        <AlertDialogDescription>
                                                                            此操作将删除频道配置，可能会影响正在进行的任务。
                                                                        </AlertDialogDescription>
                                                                    </AlertDialogHeader>
                                                                    <AlertDialogFooter>
                                                                        <AlertDialogCancel>取消</AlertDialogCancel>
                                                                        <AlertDialogAction onClick={() => deleteChannel(channel.id)} className="bg-destructive hover:bg-destructive/90">
                                                                            删除
                                                                        </AlertDialogAction>
                                                                    </AlertDialogFooter>
                                                                </AlertDialogContent>
                                                            </AlertDialog>
                                                        </TooltipTrigger>
                                                        <TooltipContent>
                                                            <p>删除频道</p>
                                                        </TooltipContent>
                                                    </Tooltip>
                                                </TooltipProvider>
                                            </div>
                                        </TableCell>
                                    </TableRow>
                                )
                            })
                        )}
                    </TableBody>
                </Table>
            </div>
        </CardContent>
      </Card>
    </div>
  )
}

export default ChannelsPage
