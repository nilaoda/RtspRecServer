import { useLocation, useNavigate } from 'react-router-dom'
import { useMemo } from 'react'
import { Layout, Menu, Space, Switch, Typography } from 'antd'
import { useAppContext } from '../../app/context'
import { formatBytes, formatDateTime } from '../shared/format'
import AppRoutes from '../routes.tsx'

const LayoutShell = () => {
  const { systemStatus, now, themeMode, setThemeMode } = useAppContext()
  const location = useLocation()
  const navigate = useNavigate()
  const selectedKey = useMemo(() => {
    if (location.pathname.startsWith('/channels')) return 'channels'
    if (location.pathname.startsWith('/recordings')) return 'recordings'
    if (location.pathname.startsWith('/settings')) return 'settings'
    if (location.pathname.startsWith('/epg')) return 'epg'
    return 'tasks'
  }, [location.pathname])
  const menuItems = useMemo(
    () => [
      { key: 'tasks', label: '任务管理', path: '/' },
      { key: 'channels', label: '频道配置', path: '/channels' },
      { key: 'recordings', label: '录制管理', path: '/recordings' },
      { key: 'epg', label: '节目单', path: '/epg' },
      { key: 'settings', label: '应用配置', path: '/settings' },
    ],
    [],
  )

  const diskFree = systemStatus ? formatBytes(systemStatus.disk.freeBytes) : '--'
  const diskTotal = systemStatus ? formatBytes(systemStatus.disk.totalBytes) : '--'
  const systemTime = formatDateTime(now)
  const user = systemStatus?.currentUser ?? '--'

  return (
    <Layout style={{ minHeight: '100vh' }}>
      <Layout.Header style={{ background: 'transparent', padding: '0 24px', height: 72, lineHeight: 'normal' }}>
        <div style={{ display: 'flex', alignItems: 'center', height: '100%', gap: 16 }}>
          <Space direction="vertical" size={0} style={{ minWidth: 180 }}>
            <Typography.Title level={5} style={{ margin: 0 }}>
              RTSP Playback Hub
            </Typography.Title>
            <Typography.Text type="secondary">Scheduler Console</Typography.Text>
          </Space>
          <div style={{ flex: 1, display: 'flex', justifyContent: 'center' }}>
            <Menu
              theme={themeMode === 'dark' ? 'dark' : 'light'}
              mode="horizontal"
              selectedKeys={[selectedKey]}
              items={menuItems.map((item) => ({ key: item.key, label: item.label }))}
              onClick={({ key }) => {
                const target = menuItems.find((item) => item.key === key)
                if (target) {
                  navigate(target.path)
                }
              }}
              style={{ borderBottom: 'none', minWidth: 420, justifyContent: 'center' }}
            />
          </div>
          <Space size="large" align="center" wrap={false}>
            <Space size="small" align="center">
              <Typography.Text type="secondary">暗色模式</Typography.Text>
              <Switch
                checked={themeMode === 'dark'}
                onChange={(checked) => setThemeMode(checked ? 'dark' : 'light')}
              />
            </Space>
            <Space size="middle" align="center" wrap={false}>
              <Space size={4} align="center">
                <Typography.Text type="secondary">用户</Typography.Text>
                <Typography.Text>{user}</Typography.Text>
              </Space>
              <Space size={4} align="center">
                <Typography.Text type="secondary">磁盘</Typography.Text>
                <Typography.Text>
                  {diskFree}/{diskTotal}
                </Typography.Text>
              </Space>
              <Space size={4} align="center">
                <Typography.Text type="secondary">时间</Typography.Text>
                <Typography.Text style={{ fontVariantNumeric: 'tabular-nums' }}>{systemTime}</Typography.Text>
              </Space>
            </Space>
          </Space>
        </div>
      </Layout.Header>
      <Layout.Content style={{ padding: '16px 24px 24px' }}>
        <AppRoutes />
      </Layout.Content>
    </Layout>
  )
}

export default LayoutShell
