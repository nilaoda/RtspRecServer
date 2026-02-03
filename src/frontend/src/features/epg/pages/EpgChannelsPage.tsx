import { Card, List, Tag, Space, Typography, Avatar } from 'antd'
import { PlayCircleOutlined } from '@ant-design/icons'
import { useEpgData } from '../hooks/useEpgData'
import Loading from '../../shared/Loading'

const { Title } = Typography

export default function EpgChannelsPage() {
  const { channels, loading } = useEpgData()

  if (loading && channels.length === 0) {
    return <Loading tip="正在加载频道列表..." />
  }

  return (
    <div style={{ padding: '0 8px' }}>
      <Title level={2}>
        <PlayCircleOutlined style={{ marginRight: 8 }} />
        所有频道
      </Title>

      <List
        grid={{ gutter: 16, xs: 1, sm: 2, md: 3, lg: 4, xl: 4, xxl: 4 }}
        dataSource={channels}
        renderItem={(channel) => (
          <List.Item>
            <Card hoverable>
              <Card.Meta
                avatar={
                  channel.iconUrl ? (
                    <Avatar src={channel.iconUrl} size={48} />
                  ) : (
                    <Avatar size={48} style={{ backgroundColor: '#1890ff' }}>
                      {channel.name.charAt(0)}
                    </Avatar>
                  )
                }
                title={
                  <Space>
                    <span style={{ fontSize: '16px', fontWeight: 'bold' }}>
                      {channel.name}
                    </span>
                  </Space>
                }
                description={
                  <Space direction="vertical" style={{ width: '100%' }}>
                    <div>
                      {channel.categories.slice(0, 2).map((category) => (
                        <Tag key={category} color="blue" style={{ marginRight: 4 }}>
                          {category}
                        </Tag>
                      ))}
                    </div>
                    <div style={{ fontSize: '12px', color: '#666' }}>
                      {channel.language} · {channel.country}
                    </div>
                  </Space>
                }
              />
            </Card>
          </List.Item>
        )}
      />
    </div>
  )
}