export type ImportedChannelType = 'live' | 'playback' | 'unknown'
export type ImportedChannelStatus = 'new' | 'existing'

export type ParsedM3uChannel = {
  id: string
  name: string
  tvgName: string
  group: string
  url: string
  type: ImportedChannelType
  status: ImportedChannelStatus
}

export type ParsedM3uResult = {
  channels: ParsedM3uChannel[]
  invalidCount: number
}

const parseAttributes = (line: string) => {
  const attributes: Record<string, string> = {}
  const pattern = /([\w-]+)="([^"]*)"/g
  let match: RegExpExecArray | null = null
  while ((match = pattern.exec(line)) !== null) {
    attributes[match[1]] = match[2]
  }
  return attributes
}

export const detectM3uType = (url: string): ImportedChannelType => {
  const scheme = url.split(':', 1)[0].toLowerCase()
  if (scheme === 'rtsp') {
    return 'playback'
  }
  if (scheme === 'rtp' || scheme === 'udp') {
    return 'live'
  }
  if ((scheme === 'http' || scheme === 'https') && /\/(rtp|udp)\//i.test(url)) {
    return 'live'
  }
  return 'unknown'
}

export const parseM3u = (content: string, existingUrls: Set<string>): ParsedM3uResult => {
  const lines = content.split(/\r?\n/)
  const channels: ParsedM3uChannel[] = []
  let invalidCount = 0
  let index = 0

  while (index < lines.length) {
    const line = lines[index].trim()
    if (!line) {
      index += 1
      continue
    }

    if (!/^#EXTINF/i.test(line)) {
      index += 1
      continue
    }

    const attributes = parseAttributes(line)
    const commaIndex = line.lastIndexOf(',')
    const displayName = commaIndex >= 0 ? line.slice(commaIndex + 1).trim() : ''

    let url = ''
    let urlIndex = index + 1
    while (urlIndex < lines.length) {
      const candidate = lines[urlIndex].trim()
      if (!candidate) {
        urlIndex += 1
        continue
      }
      if (candidate.startsWith('#')) {
        urlIndex += 1
        continue
      }
      url = candidate
      break
    }

    if (!url || !/^[a-z][a-z0-9+.-]*:\/\//i.test(url)) {
      invalidCount += 1
      index = urlIndex + 1
      continue
    }

    const name = displayName || attributes['tvg-name'] || url
    channels.push({
      id: `m3u-${channels.length}-${url}`,
      name,
      tvgName: attributes['tvg-name'] || '',
      group: attributes['group-title'] || '未分组',
      url,
      type: detectM3uType(url),
      status: existingUrls.has(url) ? 'existing' : 'new',
    })

    index = urlIndex + 1
  }

  return { channels, invalidCount }
}

export const readM3uFile = async (file: File) => {
  const buffer = await file.arrayBuffer()
  const utf8 = new TextDecoder('utf-8').decode(buffer)
  if (!utf8.includes('\uFFFD')) {
    return utf8
  }

  try {
    return new TextDecoder('gb18030').decode(buffer)
  } catch {
    return utf8
  }
}
