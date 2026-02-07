import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import * as z from 'zod'
import { Lightbulb } from 'lucide-react'

import { useAppContext } from '../../app/context'

import { Button } from "@/components/ui/button"
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"
import {
  Form,
  FormControl,
  FormDescription,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
} from "@/components/ui/form"
import { Input } from "@/components/ui/input"
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"

const settingsSchema = z.object({
  maxRecordingTasks: z.coerce.number().min(1, "最大并发必须大于 0").int("必须是整数"),
  recordingTransport: z.enum(["MP2T/TCP", "MP2T/RTP/TCP"]),
})

type SettingsFormValues = z.infer<typeof settingsSchema>

const SettingsPage = () => {
  const { appConfig, updateConfig } = useAppContext()
  const initialValue = appConfig?.maxRecordingTasks ?? 1
  const initialTransport = appConfig?.recordingTransport ?? "MP2T/TCP"

  const form = useForm<SettingsFormValues>({
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    resolver: zodResolver(settingsSchema) as any,
    defaultValues: {
      maxRecordingTasks: initialValue,
      recordingTransport: initialTransport,
    },
    values: { // Update form when initialValue changes
        maxRecordingTasks: initialValue,
        recordingTransport: initialTransport,
    }
  })

  const onSave = async (values: SettingsFormValues) => {
    try {
        await updateConfig({ maxRecordingTasks: values.maxRecordingTasks, recordingTransport: values.recordingTransport })
    } catch {
        // Handled in context
    }
  }

  return (
    <div className="space-y-6 max-w-4xl">
      <Card>
        <CardHeader>
          <CardTitle>应用配置</CardTitle>
          <CardDescription>
            仅开放部分配置，其他配置由服务端管理
          </CardDescription>
        </CardHeader>
        <CardContent>
          <div className="grid gap-6 md:grid-cols-2">
            <Form {...form}>
              <form onSubmit={form.handleSubmit(onSave)} className="space-y-4">
                <FormField
                  control={form.control}
                  name="maxRecordingTasks"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>最大并发录制任务数</FormLabel>
                      <FormControl>
                        <Input type="number" min={1} {...field} />
                      </FormControl>
                      <FormDescription>
                        限制同时进行的录制任务数量。
                      </FormDescription>
                      <FormMessage />
                    </FormItem>
                  )}
                />
                <FormField
                  control={form.control}
                  name="recordingTransport"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>封装方式</FormLabel>
                      <Select onValueChange={field.onChange} value={field.value}>
                        <FormControl>
                          <SelectTrigger>
                            <SelectValue placeholder="选择封装方式" />
                          </SelectTrigger>
                        </FormControl>
                        <SelectContent>
                          <SelectItem value="MP2T/TCP">MP2T/TCP</SelectItem>
                          <SelectItem value="MP2T/RTP/TCP">MP2T/RTP/TCP</SelectItem>
                        </SelectContent>
                      </Select>
                      <FormDescription>
                        默认使用 MP2T/TCP。若需RTP重传等扩展能力选择 MP2T/RTP/TCP。
                      </FormDescription>
                      <FormMessage />
                    </FormItem>
                  )}
                />
                <Button type="submit">保存配置</Button>
              </form>
            </Form>

            <div className="bg-muted/50 p-6 rounded-xl border flex flex-col gap-2">
                <div className="flex items-center gap-2 font-medium">
                    <Lightbulb className="h-4 w-4 text-yellow-500" />
                    <span>提示</span>
                </div>
                <p className="text-sm text-muted-foreground leading-relaxed">
                    建议根据服务器性能设置并发数，避免磁盘与带宽竞争导致录制失败。
                    如果是机械硬盘，建议并发数不要超过 5 路。
                </p>
            </div>
          </div>
        </CardContent>
      </Card>
    </div>
  )
}

export default SettingsPage
