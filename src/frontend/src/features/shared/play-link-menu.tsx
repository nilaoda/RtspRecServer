import { useState } from 'react'
import copy from 'copy-to-clipboard'
import { Copy, ExternalLink, ListVideo, Play, Settings2 } from 'lucide-react'
import { toast } from 'sonner'

import { useAppContext } from '../../app/context'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuSub,
  DropdownMenuSubContent,
  DropdownMenuSubTrigger,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Input } from '@/components/ui/input'
import { cn } from '@/lib/utils'

const CUSTOM_PLAYER_KEY = 'rtsp_player_url_template'
const RECENT_PLAYERS_KEY = 'rtsp_play_recent_players'
const MAX_RECENT_PLAYERS = 2

type PlayerId = 'iina' | 'infuse' | 'vlc' | 'nplayer' | 'potplayer' | 'mpv'

type PlayerDefinition = {
  id: PlayerId
  label: string
  buildScheme: (absoluteUrl: string) => string | null
}

const PLAYERS: PlayerDefinition[] = [
  {
    id: 'iina',
    label: 'IINA（macOS）',
    buildScheme: (url) => `iina://weblink?url=${encodeURIComponent(url)}`,
  },
  {
    id: 'infuse',
    label: 'Infuse（iOS/macOS）',
    buildScheme: (url) => `infuse://x-callback-url/play?url=${encodeURIComponent(url)}`,
  },
  {
    id: 'vlc',
    label: 'VLC（iOS）',
    buildScheme: (url) => `vlc-x-callback://x-callback-url/stream?url=${encodeURIComponent(url)}`,
  },
  {
    id: 'nplayer',
    label: 'nPlayer（iOS）',
    buildScheme: (url) =>
      /^https?:\/\//i.test(url)
        ? url.replace(/^http:\/\//i, 'nplayer-http://').replace(/^https:\/\//i, 'nplayer-https://')
        : null,
  },
  {
    id: 'potplayer',
    label: 'PotPlayer（Windows）',
    buildScheme: (url) => `potplayer://${encodeURIComponent(url)}`,
  },
  {
    id: 'mpv',
    label: 'mpv（桌面）',
    buildScheme: (url) => `mpv://${encodeURIComponent(url)}`,
  },
]

const DEFAULT_RECENT_PLAYER: PlayerId = 'iina'

const PLAYER_BY_ID = new Map(PLAYERS.map((player) => [player.id, player]))

const PLAYER_PRESETS: { label: string; template: string }[] = [
  { label: 'PotPlayer', template: 'potplayer://{url}' },
  { label: 'mpv', template: 'mpv://{url}' },
  { label: 'VLC（iOS）', template: 'vlc-x-callback://x-callback-url/stream?url={url}' },
  { label: 'Infuse', template: 'infuse://x-callback-url/play?url={url}' },
  { label: 'IINA', template: 'iina://weblink?url={url}' },
]

const readRecentPlayerIds = (): PlayerId[] => {
  try {
    const raw = window.localStorage.getItem(RECENT_PLAYERS_KEY)
    if (raw) {
      const parsed: unknown = JSON.parse(raw)
      if (Array.isArray(parsed)) {
        const ids = parsed.filter(
          (id): id is PlayerId => typeof id === 'string' && PLAYER_BY_ID.has(id as PlayerId),
        )
        const unique = [...new Set<PlayerId>(ids)].slice(0, MAX_RECENT_PLAYERS)
        if (unique.length > 0) {
          return unique
        }
      }
    }
  } catch {
    // 忽略损坏的本地存储
  }
  return [DEFAULT_RECENT_PLAYER]
}

const readCustomTemplate = () => {
  try {
    return window.localStorage.getItem(CUSTOM_PLAYER_KEY) ?? ''
  } catch {
    return ''
  }
}

const toAbsoluteUrl = (path: string) => new URL(path, window.location.origin).href

const openScheme = (schemeUrl: string) => {
  const anchor = document.createElement('a')
  anchor.href = schemeUrl
  anchor.rel = 'noreferrer'
  document.body.appendChild(anchor)
  anchor.click()
  anchor.remove()
}

const applyTemplate = (template: string, absoluteUrl: string) =>
  template
    .replaceAll('{url}', encodeURIComponent(absoluteUrl))
    .replaceAll('{rawUrl}', absoluteUrl)

const buildM3uFileName = (name: string) => {
  const base = name
    .replace(/\.ts$/i, '')
    .replace(/[\\/:*?"<>|]/g, '_')
    .trim()
  return `${base || 'play'}.m3u`
}

const downloadM3u = (absoluteUrl: string, name: string) => {
  const displayName = name.replace(/[\r\n]+/g, ' ').trim() || name
  const content = `#EXTM3U\n#EXTINF:-1,${displayName}\n${absoluteUrl}\n`
  const blob = new Blob([content], { type: 'audio/x-mpegurl' })
  const objectUrl = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = objectUrl
  anchor.download = buildM3uFileName(name)
  document.body.appendChild(anchor)
  anchor.click()
  anchor.remove()
  window.setTimeout(() => URL.revokeObjectURL(objectUrl), 1000)
}

export default function PlayLinkMenu({
  path,
  name,
  withLabel = false,
  className,
}: {
  path: string
  name: string
  withLabel?: boolean
  className?: string
}) {
  const { appConfig } = useAppContext()
  const [customTemplate, setCustomTemplate] = useState(readCustomTemplate)
  const [recentPlayerIds, setRecentPlayerIds] = useState<PlayerId[]>(readRecentPlayerIds)
  const [customOpen, setCustomOpen] = useState(false)
  const [templateDraft, setTemplateDraft] = useState('')
  const [copyFallback, setCopyFallback] = useState<string | null>(null)

  const recentPlayers = recentPlayerIds
    .map((id) => PLAYER_BY_ID.get(id))
    .filter((player): player is PlayerDefinition => player != null)

  const rememberPlayer = (id: PlayerId) => {
    const next = [id, ...recentPlayerIds.filter((item) => item !== id)].slice(0, MAX_RECENT_PLAYERS)
    setRecentPlayerIds(next)
    try {
      window.localStorage.setItem(RECENT_PLAYERS_KEY, JSON.stringify(next))
    } catch {
      // 忽略本地存储失败
    }
  }

  const openPlayer = (player: PlayerDefinition) => {
    const scheme = player.buildScheme(toAbsoluteUrl(path))
    if (!scheme) {
      return
    }
    rememberPlayer(player.id)
    openScheme(scheme)
  }

  const handleCopy = async () => {
    const url = toAbsoluteUrl(path)
    const ok = await copy(url)
    if (ok) {
      toast.success('播放链接已复制')
      return
    }

    setCopyFallback(url)
    toast.error('自动复制失败，请手动复制链接')
  }

  const retryCopy = async () => {
    if (!copyFallback) {
      return
    }
    const ok = await copy(copyFallback)
    if (ok) {
      toast.success('播放链接已复制')
      setCopyFallback(null)
    } else {
      toast.error('仍无法自动复制，请长按或全选链接复制')
    }
  }

  const handleM3u = () => {
    downloadM3u(toAbsoluteUrl(path), name)
    toast.success('已生成 .m3u 播放列表')
  }

  const handleOpenCustom = () => {
    openScheme(applyTemplate(customTemplate, toAbsoluteUrl(path)))
  }

  const openCustomDialog = () => {
    setTemplateDraft(customTemplate)
    setCustomOpen(true)
  }

  const saveCustomTemplate = () => {
    const value = templateDraft.trim()
    if (!value.includes('{url}') && !value.includes('{rawUrl}')) {
      toast.error('模板必须包含 {url} 或 {rawUrl}')
      return
    }

    try {
      window.localStorage.setItem(CUSTOM_PLAYER_KEY, value)
    } catch {
      toast.error('保存失败，浏览器不允许本地存储')
      return
    }

    setCustomTemplate(value)
    setCustomOpen(false)
    toast.success('自定义播放器已保存')
  }

  const clearCustomTemplate = () => {
    try {
      window.localStorage.removeItem(CUSTOM_PLAYER_KEY)
    } catch {
      // 忽略
    }
    setCustomTemplate('')
    setCustomOpen(false)
    toast.success('已清除自定义播放器')
  }

  return (
    <>
      <DropdownMenu
        onOpenChange={(open) => {
          if (open) {
            // 其他行/其他标签页可能刚修改过，打开时重新读取保持一致
            setCustomTemplate(readCustomTemplate())
            setRecentPlayerIds(readRecentPlayerIds())
          }
        }}
      >
        <DropdownMenuTrigger asChild>
          <Button
            variant="ghost"
            size={withLabel ? 'sm' : 'icon'}
            className={cn(!withLabel && 'h-8 w-8', className)}
            aria-label="播放/串流"
            title="播放/串流"
          >
            <Play className="h-4 w-4" />
            {withLabel && <span className="ml-1.5">播放</span>}
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end" className="w-56">
          <DropdownMenuItem onSelect={() => void handleCopy()}>
            <Copy className="mr-2 h-4 w-4" />
            复制播放链接
          </DropdownMenuItem>
          <DropdownMenuItem onSelect={handleM3u}>
            <ListVideo className="mr-2 h-4 w-4" />
            下载 .m3u 播放列表
          </DropdownMenuItem>

          <DropdownMenuSeparator />
          {recentPlayers.map((player) => (
            <DropdownMenuItem key={player.id} onSelect={() => openPlayer(player)}>
              <Play className="mr-2 h-4 w-4" />
              {player.label}
            </DropdownMenuItem>
          ))}
          <DropdownMenuSub>
            <DropdownMenuSubTrigger>
              <ExternalLink className="mr-2 h-4 w-4" />
              其他播放器
            </DropdownMenuSubTrigger>
            <DropdownMenuSubContent className="w-56">
              {PLAYERS.map((player) => (
                <DropdownMenuItem key={player.id} onSelect={() => openPlayer(player)}>
                  {player.label}
                </DropdownMenuItem>
              ))}
              <DropdownMenuSeparator />
              <DropdownMenuLabel className="px-2 py-1 text-[11px] font-normal leading-4 text-muted-foreground">
                PotPlayer / mpv 需先注册 URL 协议
              </DropdownMenuLabel>
            </DropdownMenuSubContent>
          </DropdownMenuSub>

          {customTemplate && (
            <DropdownMenuItem onSelect={handleOpenCustom}>
              <ExternalLink className="mr-2 h-4 w-4" />
              用自定义播放器打开
            </DropdownMenuItem>
          )}

          <DropdownMenuSeparator />
          <DropdownMenuItem onSelect={openCustomDialog}>
            <Settings2 className="mr-2 h-4 w-4" />
            自定义播放器…
          </DropdownMenuItem>
          {appConfig?.useAuth && (
            <div className="px-2 py-1.5 text-[11px] leading-4 text-muted-foreground">
              已启用访问认证，外部播放器需提供账号密码
            </div>
          )}
        </DropdownMenuContent>
      </DropdownMenu>

      <Dialog open={copyFallback !== null} onOpenChange={(open) => { if (!open) setCopyFallback(null) }}>
        <DialogContent className="max-w-[calc(100vw-2rem)] sm:max-w-[480px]">
          <DialogHeader>
            <DialogTitle>复制播放链接</DialogTitle>
            <DialogDescription>
              长按或全选下面的链接可手动复制；也可以点击“复制”按钮再试一次。
            </DialogDescription>
          </DialogHeader>
          <textarea
            readOnly
            rows={3}
            value={copyFallback ?? ''}
            onFocus={(event) => event.currentTarget.select()}
            onClick={(event) => event.currentTarget.select()}
            className="flex min-h-[76px] w-full resize-none rounded-md border border-input bg-background px-3 py-2 font-mono text-xs ring-offset-background focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2"
          />
          <DialogFooter className="gap-2 sm:gap-0">
            <Button type="button" variant="outline" onClick={() => setCopyFallback(null)}>
              关闭
            </Button>
            <Button type="button" onClick={() => void retryCopy()}>
              复制
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <Dialog open={customOpen} onOpenChange={setCustomOpen}>
        <DialogContent className="max-w-[calc(100vw-2rem)] sm:max-w-[440px]">
          <DialogHeader>
            <DialogTitle>自定义播放器</DialogTitle>
            <DialogDescription>
              填写播放器的 URL Scheme 模板，<code className="font-mono">{'{url}'}</code> 会替换为编码后的播放地址，
              <code className="font-mono">{'{rawUrl}'}</code> 为未编码地址。示例：<code className="font-mono">potplayer://{'{url}'}</code>。
            </DialogDescription>
          </DialogHeader>
          <Input
            value={templateDraft}
            onChange={(event) => setTemplateDraft(event.target.value)}
            placeholder="potplayer://{url}"
            autoComplete="off"
          />
          <div className="flex flex-wrap gap-1.5">
            {PLAYER_PRESETS.map((preset) => (
              <button
                key={preset.label}
                type="button"
                onClick={() => setTemplateDraft(preset.template)}
                className="h-6 rounded border bg-background px-1.5 text-[11px] text-muted-foreground transition-colors hover:bg-muted"
              >
                {preset.label}
              </button>
            ))}
          </div>
          <p className="text-[11px] leading-4 text-muted-foreground">
            PotPlayer、mpv 等桌面播放器需要先安装并注册 URL 协议（如 PotPlayer 协议助手、mpv-handler）才能被浏览器调起。
          </p>
          <DialogFooter className="gap-2 sm:gap-0">
            {customTemplate && (
              <Button type="button" variant="outline" onClick={clearCustomTemplate}>
                清除
              </Button>
            )}
            <Button type="button" onClick={saveCustomTemplate}>
              保存
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </>
  )
}