import React, { useState, useEffect, useCallback, type ReactNode } from 'react'
import type { EpgChannel, EpgProgram, CurrentProgramInfo, BackendEpgProgram } from '../types/epg'
import * as api from '../../../services/api'
import { useAppContext } from '../../../app/context'
import { EpgContext } from './useEpgContext'

export const EpgProvider: React.FC<{ children: ReactNode }> = ({ children }) => {
  const { now: systemNow } = useAppContext()
  const now = systemNow.getTime()
  
  const [channels, setChannels] = useState<EpgChannel[]>([])
  const [currentPrograms, setCurrentPrograms] = useState<CurrentProgramInfo[]>([])
  const [categories, setCategories] = useState<string[]>([])
  const [loading, setLoading] = useState(true)
  const [refreshing, setRefreshing] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const lastRefreshRef = React.useRef<number>(0)
  const isRefreshingRef = React.useRef<boolean>(false)
  const initialLoadRef = React.useRef<Promise<unknown> | null>(null)

  const mapBackendProgram = (p: BackendEpgProgram): EpgProgram => ({
    ...p,
    startTime: new Date(p.startTime),
    endTime: new Date(p.endTime)
  })

  const fetchChannels = useCallback(async () => {
    try {
      const data = await api.getEpgChannels()
      setChannels(data || [])
    } catch (err) {
      console.error('Failed to fetch channels', err)
    }
  }, [])

  const fetchCategories = useCallback(async () => {
    try {
      const data = await api.getEpgCategories()
      setCategories(data || [])
    } catch (err) {
      console.error('Failed to fetch categories', err)
    }
  }, [])

  const fetchCurrentPrograms = useCallback(async () => {
    if (isRefreshingRef.current) return;
    isRefreshingRef.current = true;
    try {
      // 添加时间戳防止缓存
      const data = await api.getEpgCurrentPrograms()
      const mapped: CurrentProgramInfo[] = (data || []).map((item) => ({
        channelId: item.channel.id,
        channelName: item.channel.name,
        program: mapBackendProgram(item.currentProgram),
        startTime: new Date(item.currentProgram.startTime),
        endTime: new Date(item.currentProgram.endTime)
      }))
      setCurrentPrograms(mapped)
      lastRefreshRef.current = Date.now();
    } catch (err) {
      console.error('Failed to fetch current programs', err)
    } finally {
      isRefreshingRef.current = false;
    }
  }, [])

  const loadAllData = useCallback(() => Promise.all([
    fetchChannels(),
    fetchCategories(),
    fetchCurrentPrograms(),
  ]), [fetchChannels, fetchCategories, fetchCurrentPrograms])

  useEffect(() => {
    let active = true
    initialLoadRef.current ??= loadAllData()
    void initialLoadRef.current.catch(() => {
      if (active) setError('加载EPG数据失败')
    }).finally(() => {
      if (active) setLoading(false)
    })
    return () => { active = false }
  }, [loadAllData])

  // 自动刷新正在播放的节目信息
  useEffect(() => {
    // 检查是否有节目即将结束，提前或准时刷新
    const checkAndRefresh = () => {
      const currentTime = Date.now();

      // 增加冷却时间：如果 10 秒内刚刚刷新过，则跳过，防止死循环请求
      if (currentTime - lastRefreshRef.current < 10000) {
        return;
      }

      // 列表为空说明首屏加载撞上了数据源未就绪（或刷新竞态），需要自愈重取，
      // 否则空状态会一直卡住（some() 在空数组上恒为 false，永远不会自动恢复）
      const needsRefresh =
        currentPrograms.length === 0 ||
        currentPrograms.some(cp => {
          const endTime = cp.endTime.getTime();
          // 核心修复逻辑：
          // 1. 如果当前时间已经超过了节目的结束时间，说明节目肯定已经换了，必须刷新。
          // 2. 为了平滑过渡，我们在节目结束后的 1-2 秒内触发刷新，确保后端数据已更新。
          // 3. 之前逻辑是提前 5 秒刷新，这会导致后端还没切换到新节目，前端就拿到了旧数据并进入 10 秒冷却。
          const diff = currentTime - endTime;
          return diff >= 1000 && diff <= 15000; // 节目结束后的 1 到 15 秒内触发刷新
        });

      if (needsRefresh || channels.length === 0) {
        console.log('Detected program ended or data not ready, refreshing current programs to get next one...');
        fetchCurrentPrograms();
        if (channels.length === 0) {
          fetchChannels();
        }
      }
    };

    const timer = setInterval(checkAndRefresh, 5000); // 每 5 秒检查一次

    return () => clearInterval(timer);
  }, [fetchCurrentPrograms, currentPrograms, channels, fetchChannels]); // 移除 now 依赖，靠 setInterval 驱动

  const refresh = async () => {
    setRefreshing(true)
    setError(null)
    try {
      await api.refreshEpgData()
      await loadAllData()
    } catch {
      setError('节目表刷新失败')
    } finally {
      setRefreshing(false)
    }
  }

  // 必须保持引用稳定：EpgChannelPage 的数据加载 effect 依赖 getChannelPrograms，
  // 而 Provider 因 AppContext 的 now 每秒重渲染，内联函数会导致每秒重新拉取节目表，
  // 进而把节目列表（含打开中的预约弹窗）反复卸载重建。
  const getChannelPrograms = useCallback(async (channelId: string) => {
    try {
      const data = await api.getEpgChannelPrograms(channelId)
      return (data || []).map(mapBackendProgram)
    } catch (err) {
      console.error(`Failed to fetch programs for channel ${channelId}`, err)
      return []
    }
  }, [])

  const getCategoryChannels = useCallback(async (category: string) => {
    try {
      const data = await api.getEpgCategoryChannels(category)
      return data || []
    } catch (err) {
      console.error(`Failed to fetch channels for category ${category}`, err)
      return []
    }
  }, [])

  return (
    <EpgContext.Provider
      value={{
        channels,
        currentPrograms,
        categories,
        loading,
        refreshing,
        error,
        refresh,
        getChannelPrograms,
        getCategoryChannels,
        now
      }}
    >
      {children}
    </EpgContext.Provider>
  )
}
