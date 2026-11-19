export const getRecordingSourceMode = (value: string): 'live' | 'catchup' | null => {
  const url = value.trim()
  if (/^(rtp|udp):\/\//i.test(url)) return 'live'
  if (/^rtsps?:\/\//i.test(url)) return 'catchup'

  try {
    const { protocol, pathname } = new URL(url)
    if ((protocol === 'http:' || protocol === 'https:') && /\/(rtp|udp)\//i.test(pathname)) {
      return 'live'
    }
  } catch {
    return null
  }
  return null
}
