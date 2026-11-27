import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import * as z from 'zod'
import { AlertTriangle, Lightbulb } from 'lucide-react'

import { useAppContext } from '../../app/context'

import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from '@/components/ui/card'
import {
  Form,
  FormControl,
  FormDescription,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
} from '@/components/ui/form'
import { Input } from '@/components/ui/input'
import { Switch } from '@/components/ui/switch'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'

const restartLabels: Record<string, string> = {
  port: '端口',
  host: '监听地址',
}

const settingsSchema = z
  .object({
    port: z.coerce.number().int('必须是整数').min(1, '端口范围 1-65535').max(65535, '端口范围 1-65535'),
    host: z.string().min(1, '监听地址不能为空'),
    recordPath: z.string().min(1, '录制目录不能为空'),
    useAuth: z.boolean(),
    username: z.string(),
    password: z.string(),
    maxRecordingTasks: z.coerce.number().int('必须是整数').min(1, '最大并发收流数必须大于 0'),
    maxRecordingWriters: z.coerce.number().int('必须是整数').min(0, '最大同时写盘任务数不能小于 0'),
    recordingTransport: z.enum(['MP2T/TCP', 'MP2T/RTP/TCP']),
    writeRecordingLog: z.boolean(),
    taskErrorLogMaxEntries: z.coerce
      .number()
      .int('必须是整数')
      .min(100, '范围 100-50000')
      .max(50_000, '范围 100-50000'),
    epgUrl: z
      .string()
      .min(1, '节目表地址不能为空')
      .refine((value) => /^https?:\/\//i.test(value), '请输入 http(s) 地址'),
    epgUpdateTime: z.string().regex(/^([01]\d|2[0-3]):[0-5]\d$/, '格式为 HH:mm'),
    epgCacheDurationHours: z.coerce.number().int('必须是整数').min(1, '必须大于 0').max(720, '最多 720 小时'),
  })
  .superRefine((values, ctx) => {
    if (values.useAuth && !values.username.trim()) {
      ctx.addIssue({ code: 'custom', path: ['username'], message: '启用认证时用户名不能为空' })
    }
  })

type SettingsFormValues = z.infer<typeof settingsSchema>

function RestartBadge() {
  return (
    <Badge
      variant="outline"
      className="border-amber-500/40 bg-amber-500/10 text-[10px] font-normal text-amber-600 dark:text-amber-400"
    >
      需重启
    </Badge>
  )
}

const SettingsPage = () => {
  const { appConfig, updateConfig } = useAppContext()
  const restartFields = appConfig?.restartRequiredFields ?? []

  const initialValues: SettingsFormValues = {
    port: appConfig?.port ?? 8080,
    host: (appConfig?.host ?? ['0.0.0.0']).join(', '),
    recordPath: appConfig?.recordPath ?? './records',
    useAuth: appConfig?.useAuth ?? false,
    username: appConfig?.username ?? 'admin',
    password: '',
    maxRecordingTasks: appConfig?.maxRecordingTasks ?? 1,
    maxRecordingWriters: appConfig?.maxRecordingWriters ?? 0,
    recordingTransport: appConfig?.recordingTransport ?? 'MP2T/TCP',
    writeRecordingLog: appConfig?.writeRecordingLog ?? true,
    taskErrorLogMaxEntries: appConfig?.taskErrorLogMaxEntries ?? 10_000,
    epgUrl: appConfig?.epg?.url ?? '',
    epgUpdateTime: appConfig?.epg?.updateTime ?? '08:00',
    epgCacheDurationHours: appConfig?.epg?.cacheDurationHours ?? 24,
  }

  const form = useForm<SettingsFormValues>({
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    resolver: zodResolver(settingsSchema) as any,
    defaultValues: initialValues,
    values: initialValues,
  })

  const onSave = async (values: SettingsFormValues) => {
    const hosts = values.host
      .split(/[,\s]+/)
      .map((host) => host.trim())
      .filter(Boolean)
    if (hosts.length === 0) {
      form.setError('host', { message: '监听地址不能为空' })
      return
    }

    try {
      await updateConfig({
        port: values.port,
        host: hosts,
        recordPath: values.recordPath.trim(),
        useAuth: values.useAuth,
        username: values.username.trim(),
        password: values.password ? values.password : null,
        maxRecordingTasks: values.maxRecordingTasks,
        maxRecordingWriters: values.maxRecordingWriters,
        recordingTransport: values.recordingTransport,
        writeRecordingLog: values.writeRecordingLog,
        taskErrorLogMaxEntries: values.taskErrorLogMaxEntries,
        epg: {
          url: values.epgUrl.trim(),
          updateTime: values.epgUpdateTime,
          cacheDurationHours: values.epgCacheDurationHours,
        },
      })
    } catch {
      // 错误提示由 api 层统一处理
    }
  }

  return (
    <div className="space-y-6 max-w-4xl">
      {restartFields.length > 0 && (
        <div
          role="alert"
          className="flex items-start gap-3 rounded-lg border border-amber-500/50 bg-amber-500/10 p-4"
        >
          <AlertTriangle className="mt-0.5 h-5 w-5 shrink-0 text-amber-600 dark:text-amber-400" />
          <div className="space-y-1">
            <div className="font-semibold text-amber-800 dark:text-amber-300">需要重启服务才能生效</div>
            <p className="text-sm text-amber-800/90 dark:text-amber-200/90">
              已保存的修改（{restartFields.map((field) => restartLabels[field] ?? field).join('、')}
              ）仅在重启服务后生效，重启后本提示会自动消失。
            </p>
          </div>
        </div>
      )}

      <Form {...form}>
        <form onSubmit={form.handleSubmit(onSave)} className="space-y-6">
          <Card>
            <CardHeader>
              <CardTitle>服务与访问</CardTitle>
              <CardDescription>监听端口、地址和录像目录。端口与监听地址修改后需要重启服务。</CardDescription>
            </CardHeader>
            <CardContent className="grid gap-4 md:grid-cols-2">
              <FormField
                control={form.control}
                name="port"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel className="flex items-center gap-1.5">
                      监听端口
                      <RestartBadge />
                    </FormLabel>
                    <FormControl>
                      <Input type="number" min={1} max={65535} {...field} />
                    </FormControl>
                    <FormDescription>Web 服务监听端口，默认 8080。</FormDescription>
                    <FormMessage />
                  </FormItem>
                )}
              />
              <FormField
                control={form.control}
                name="host"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel className="flex items-center gap-1.5">
                      监听地址
                      <RestartBadge />
                    </FormLabel>
                    <FormControl>
                      <Input placeholder="0.0.0.0, ::" {...field} />
                    </FormControl>
                    <FormDescription>
                      多个地址用逗号分隔。0.0.0.0 监听所有 IPv4 网卡；:: 监听所有 IPv6 并同时覆盖 IPv4
                      （双栈）；::1 仅本机 IPv6 回环。
                    </FormDescription>
                    <FormMessage />
                  </FormItem>
                )}
              />
              <FormField
                control={form.control}
                name="recordPath"
                render={({ field }) => (
                  <FormItem className="md:col-span-2">
                    <FormLabel>录像目录</FormLabel>
                    <FormControl>
                      <Input placeholder="./records" {...field} />
                    </FormControl>
                    <FormDescription>录像与日志的保存目录，支持相对路径；新录制的任务使用新目录，旧目录中的录像不再出现在录制列表里。</FormDescription>
                    <FormMessage />
                  </FormItem>
                )}
              />
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>访问认证</CardTitle>
              <CardDescription>启用后访问页面和接口需要输入用户名密码（Basic 认证）。</CardDescription>
            </CardHeader>
            <CardContent className="space-y-4">
              <FormField
                control={form.control}
                name="useAuth"
                render={({ field }) => (
                  <FormItem className="flex flex-row items-center justify-between gap-4 rounded-lg border p-3">
                    <div className="space-y-0.5">
                      <FormLabel>启用认证</FormLabel>
                      <FormDescription>
                        修改后立即生效，无需重启。启用后浏览器会弹出登录框，请牢记账号密码；忘记密码需手动修改 recsettings.json 并重启。
                      </FormDescription>
                    </div>
                    <FormControl>
                      <Switch checked={field.value} onCheckedChange={field.onChange} />
                    </FormControl>
                  </FormItem>
                )}
              />
              <div className="grid gap-4 md:grid-cols-2">
                <FormField
                  control={form.control}
                  name="username"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>用户名</FormLabel>
                      <FormControl>
                        <Input autoComplete="username" {...field} />
                      </FormControl>
                      <FormMessage />
                    </FormItem>
                  )}
                />
                <FormField
                  control={form.control}
                  name="password"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>密码</FormLabel>
                      <FormControl>
                        <Input
                          type="password"
                          autoComplete="new-password"
                          placeholder={appConfig?.hasPassword ? '已设置，留空则不修改' : '留空则不修改'}
                          {...field}
                        />
                      </FormControl>
                      <FormDescription>出于安全考虑不回显当前密码，留空表示保持不变。</FormDescription>
                      <FormMessage />
                    </FormItem>
                  )}
                />
              </div>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>录制</CardTitle>
              <CardDescription>并发、封装方式与录制日志。</CardDescription>
            </CardHeader>
            <CardContent className="space-y-4">
              <div className="grid gap-4 md:grid-cols-2">
                <FormField
                  control={form.control}
                  name="maxRecordingTasks"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>最大并发收流数</FormLabel>
                      <FormControl>
                        <Input type="number" min={1} {...field} />
                      </FormControl>
                      <FormDescription>同源直播任务共用一路，回放任务各占一路。</FormDescription>
                      <FormMessage />
                    </FormItem>
                  )}
                />
                <FormField
                  control={form.control}
                  name="maxRecordingWriters"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>最大同时写盘任务数</FormLabel>
                      <FormControl>
                        <Input type="number" min={0} {...field} />
                      </FormControl>
                      <FormDescription>填 0 自动取 2 和收流上限两者中的较大值。</FormDescription>
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
                      <FormDescription>仅对 RTSP 回放录制生效；直播源不使用该设置。</FormDescription>
                      <FormMessage />
                    </FormItem>
                  )}
                />
              </div>
              <FormField
                control={form.control}
                name="writeRecordingLog"
                render={({ field }) => (
                  <FormItem className="flex flex-row items-center justify-between gap-4 rounded-lg border p-3">
                    <div className="space-y-0.5">
                      <FormLabel>写入录制日志</FormLabel>
                      <FormDescription>
                        在录像文件旁生成同名 .log，记录 TS 检测和录制状态。关闭后仅影响之后启动的录制。
                      </FormDescription>
                    </div>
                    <FormControl>
                      <Switch checked={field.value} onCheckedChange={field.onChange} />
                    </FormControl>
                  </FormItem>
                )}
              />
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>日志</CardTitle>
              <CardDescription>底部实时错误日志面板的内存缓冲设置。</CardDescription>
            </CardHeader>
            <CardContent>
              <FormField
                control={form.control}
                name="taskErrorLogMaxEntries"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>实时错误日志上限（条）</FormLabel>
                    <FormControl>
                      <Input type="number" min={100} max={50000} {...field} />
                    </FormControl>
                    <FormDescription>
                      内存环形缓冲条数，超出后丢弃最旧记录；修改后立即生效（约 5 秒内），服务重启会清空。
                    </FormDescription>
                    <FormMessage />
                  </FormItem>
                )}
              />
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>节目表</CardTitle>
              <CardDescription>节目表数据源与自动更新设置。</CardDescription>
            </CardHeader>
            <CardContent className="space-y-4">
              <FormField
                control={form.control}
                name="epgUrl"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>节目表地址</FormLabel>
                    <FormControl>
                      <Input placeholder="http://epg.example.com/e.xml.gz" {...field} />
                    </FormControl>
                    <FormDescription>XMLTV 地址，支持 .gz 压缩包。</FormDescription>
                    <FormMessage />
                  </FormItem>
                )}
              />
              <div className="grid gap-4 md:grid-cols-2">
                <FormField
                  control={form.control}
                  name="epgUpdateTime"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>每日自动更新时间</FormLabel>
                      <FormControl>
                        <Input type="time" {...field} />
                      </FormControl>
                      <FormDescription>修改后约 1 分钟内生效。</FormDescription>
                      <FormMessage />
                    </FormItem>
                  )}
                />
                <FormField
                  control={form.control}
                  name="epgCacheDurationHours"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>缓存时长（小时）</FormLabel>
                      <FormControl>
                        <Input type="number" min={1} max={720} {...field} />
                      </FormControl>
                      <FormDescription>频道列表内存缓存时长，修改后对新写入的缓存生效，节目表刷新时自动清空。</FormDescription>
                      <FormMessage />
                    </FormItem>
                  )}
                />
              </div>
            </CardContent>
          </Card>

          <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
            <p className="flex items-center gap-1.5 text-xs text-muted-foreground">
              <Lightbulb className="h-3.5 w-3.5 text-yellow-500" />
              除标注「需重启」的项外，其余修改保存后立即生效。
            </p>
            <Button type="submit" className="w-full sm:w-auto">
              保存配置
            </Button>
          </div>
        </form>
      </Form>
    </div>
  )
}

export default SettingsPage