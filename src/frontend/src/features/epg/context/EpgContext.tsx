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
  const [loading, setLoading] = useState<boolean>(false)
  const [error, setError] = useState<string | null>(null)
  const lastRefreshRef = React.useRef<number>(0)
  const isRefreshingRef = React.useRef<boolean>(false)

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

  const loadAllData = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      await Promise.all([
        fetchChannels(),
        fetchCategories(),
        fetchCurrentPrograms()
      ])
    } catch {
      setError('加载EPG数据失败')
    } finally {
      setLoading(false)
    }
  }, [fetchChannels, fetchCategories, fetchCurrentPrograms])

  useEffect(() => {
    loadAllData()
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

      const needsRefresh = currentPrograms.some(cp => {
        const endTime = cp.endTime.getTime();
        // 核心修复逻辑：
        // 1. 如果当前时间已经超过了节目的结束时间，说明节目肯定已经换了，必须刷新。
        // 2. 为了平滑过渡，我们在节目结束后的 1-2 秒内触发刷新，确保后端数据已更新。
        // 3. 之前逻辑是提前 5 秒刷新，这会导致后端还没切换到新节目，前端就拿到了旧数据并进入 10 秒冷却。
        const diff = currentTime - endTime;
        return diff >= 1000 && diff <= 15000; // 节目结束后的 1 到 15 秒内触发刷新
      });

      if (needsRefresh) {
        console.log('Detected program ended, refreshing current programs to get next one...');
        fetchCurrentPrograms();
      }
    };

    const timer = setInterval(checkAndRefresh, 5000); // 每 5 秒检查一次

    return () => clearInterval(timer);
  }, [fetchCurrentPrograms, currentPrograms]); // 移除 now 依赖，靠 setInterval 驱动

  const refresh = async () => {
    setLoading(true)
    setError(null)
    try {
      await api.refreshEpgData()
      await loadAllData()
    } catch {
      setError('节目单刷新失败')
    } finally {
      setLoading(false)
    }
  }

  const getChannelPrograms = async (channelId: string) => {
    try {
      const data = await api.getEpgChannelPrograms(channelId)
      return (data || []).map(mapBackendProgram)
    } catch (err) {
      console.error(`Failed to fetch programs for channel ${channelId}`, err)
      return []
    }
  }

  const getCategoryChannels = async (category: string) => {
    try {
      const data = await api.getEpgCategoryChannels(category)
      return data || []
    } catch (err) {
      console.error(`Failed to fetch channels for category ${category}`, err)
      return []
    }
  }

  return (
    <EpgContext.Provider
      value={{
        channels,
        currentPrograms,
        categories,
        loading,
        error,
        refresh,
        loadAllData,
        getChannelPrograms,
        getCategoryChannels,
        now
      }}
    >
      {children}
    </EpgContext.Provider>
  )
}
