import { Card, List, Space, Typography, Avatar } from 'antd'
import { FolderOutlined } from '@ant-design/icons'
import { useEpgData } from '../hooks/useEpgData'

const { Title } = Typography

export default function EpgCategoryChannelsPage() {
  const { channels } = useEpgData()
  const categoryChannels = channels.slice(0, 4) // 模拟数据

  return (
    <div style={{ padding: '0 8px' }}>
      <Title level={2}>
        <FolderOutlined style={{ marginRight: 8 }} />
        分类频道
      </Title>

      <List
        grid={{ gutter: 16, xs: 1, sm: 2, md: 3, lg: 4, xl: 4, xxl: 4 }}
        dataSource={categoryChannels}
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
                    <div style={{ fontSize: '14px', color: '#666' }}>
                      {channel.description}
                    </div>
                    <div style={{ fontSize: '12px', color: '#999' }}>
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