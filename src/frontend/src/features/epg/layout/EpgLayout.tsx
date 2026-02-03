import { Typography, Space, Button, Spin, Popconfirm } from 'antd'
import { ReloadOutlined, CalendarOutlined, ArrowLeftOutlined } from '@ant-design/icons'
import { useEpgData } from '../hooks/useEpgData'
import { Outlet, useLocation, useNavigate } from 'react-router-dom'

const { Title } = Typography

export const EpgLayout: React.FC = () => {
  const { refresh, loading } = useEpgData()
  const location = useLocation()
  const navigate = useNavigate()

  const isDetailPage = location.pathname.includes('/epg/channels/')

  return (
    <div style={{ height: 'calc(100vh - 112px)', display: 'flex', flexDirection: 'column' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 24, flexShrink: 0 }}>
        <Title level={2} style={{ margin: 0 }}>
          <CalendarOutlined style={{ marginRight: 8 }} />
          电子节目单
        </Title>
        <Space>
          {isDetailPage ? (
            <Button
              icon={<ArrowLeftOutlined />}
              onClick={() => navigate('/epg')}
            >
              返回概览
            </Button>
          ) : (
            <Popconfirm
              title="刷新节目单"
              description="更新节目单需要从远程服务器下载并解析大量数据，请尽量不要频繁操作。确定要继续吗？"
              onConfirm={refresh}
              okText="确定"
              cancelText="取消"
            >
              <Button
                type="primary"
                icon={<ReloadOutlined />}
                loading={loading}
              >
                刷新节目单
              </Button>
            </Popconfirm>
          )}
        </Space>
      </div>
      <Spin spinning={loading} tip="节目单更新中..." style={{ flex: 1, display: 'flex', flexDirection: 'column' }} wrapperClassName="epg-spin-wrapper">
        <Outlet />
      </Spin>
    </div>
  )
}