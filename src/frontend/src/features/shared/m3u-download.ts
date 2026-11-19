type PlaylistEntry = { name: string; url: string; group?: string | null }

export const toAbsolutePlaybackUrl = (path: string) => new URL(path, window.location.origin).href

const singleLine = (value: string) => value.replace(/[\r\n]+/g, ' ').trim()

export const downloadM3u = (name: string, entries: readonly PlaylistEntry[]) => {
  const fileName = name.replace(/\.ts$/i, '').replace(/[\\/:*?"<>|]/g, '_').trim() || 'play'
  const lines = ['#EXTM3U']
  for (const entry of entries) {
    const group = entry.group && singleLine(entry.group).replaceAll('"', "'")
    const attributes = group ? ` group-title="${group}"` : ''
    lines.push(`#EXTINF:-1${attributes},${singleLine(entry.name) || '频道'}`, entry.url)
  }

  const blob = new Blob([`${lines.join('\n')}\n`], { type: 'audio/x-mpegurl;charset=utf-8' })
  const objectUrl = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = objectUrl
  anchor.download = `${fileName}.m3u`
  document.body.appendChild(anchor)
  anchor.click()
  anchor.remove()
  window.setTimeout(() => URL.revokeObjectURL(objectUrl), 1000)
}
