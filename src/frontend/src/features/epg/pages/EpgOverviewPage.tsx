import { Card, List, Space, Typography, Row, Col, Statistic, Tag, Progress, Empty } from 'antd'
import { PlayCircleOutlined, EyeOutlined, ClockCircleOutlined, RightOutlined } from '@ant-design/icons'
import { useNavigate } from 'react-router-dom'
import { useEpgData } from '../hooks/useEpgData'
import Loading from '../../shared/Loading'

const { Title } = Typography

export default function EpgOverviewPage() {
  const navigate = useNavigate()
  const { channels, currentPrograms, loading, now } = useEpgData()

  if (loading && channels.length === 0) {
    return <Loading tip="正在加载节目单数据..." />
  }

  // 辅助函数：获取频道当前正在播放的节目
  const getChannelCurrentProgram = (channelId: string) => {
    return currentPrograms.find(p => p.channelId === channelId)
  }

  return (
    <div style={{ padding: '0 8px' }}>
      <div style={{ marginBottom: 24 }}>
        <Title level={3}>
          <PlayCircleOutlined style={{ marginRight: 8 }} />
          EPG 实时节目单
        </Title>
      </div>

      <Row gutter={[16, 16]} style={{ marginBottom: 24 }}>
        <Col span={12}>
          <Card size="small">
            <Statistic
              title="在线频道"
              value={channels.length}
              prefix={<EyeOutlined />}
              valueStyle={{ color: '#1890ff' }}
            />
          </Card>
        </Col>
        <Col span={12}>
          <Card size="small">
            <Statistic
              title="正在播出"
              value={currentPrograms.length}
              prefix={<ClockCircleOutlined />}
              valueStyle={{ color: '#1890ff' }}
            />
          </Card>
        </Col>
      </Row>

      <div style={{ marginTop: 24 }}>
        <Title level={4}>所有频道正在播放</Title>
        {channels.length > 0 ? (
          <List
            grid={{ gutter: 16, xs: 1, sm: 2, md: 3, lg: 3, xl: 4, xxl: 4 }}
            dataSource={channels}
            renderItem={(channel) => {
              const currentInfo = getChannelCurrentProgram(channel.id)
              const program = currentInfo?.program
              
              let progress = 0
              if (program) {
                const total = program.endTime.getTime() - program.startTime.getTime()
                const elapsed = now - program.startTime.getTime()
                progress = Math.min(Math.max(Math.round((elapsed / total) * 100), 0), 100)
              }

              return (
                <List.Item>
                  <Card
                    hoverable
                    onClick={() => navigate(`/epg/channels/${channel.id}`)}
                    styles={{ body: { padding: '12px' } }}
                    style={{
                      borderLeft: program ? '4px solid #1890ff' : '4px solid rgba(0,0,0,0.1)',
                      height: '100%',
                      display: 'flex',
                      flexDirection: 'column',
                      background: program ? 'rgba(24, 144, 255, 0.02)' : 'transparent'
                    }}
                  >
                    <div style={{ display: 'flex', alignItems: 'flex-start', marginBottom: 8 }}>
                      {channel.iconUrl ? (
                        <img
                          alt={channel.name}
                          src={channel.iconUrl}
                          style={{ width: 40, height: 40, objectFit: 'contain', marginRight: 12, borderRadius: 4 }}
                        />
                      ) : (
                        <div style={{ 
                          width: 40, height: 40, 
                          display: 'flex', 
                          alignItems: 'center', 
                          justifyContent: 'center',
                          background: '#f0f2f5',
                          marginRight: 12,
                          borderRadius: 4,
                          fontSize: 18,
                          color: '#1890ff',
                          fontWeight: 'bold'
                        }}>
                          {channel.name.charAt(0)}
                        </div>
                      )}
                      <div style={{ flex: 1, overflow: 'hidden' }}>
                        <div style={{ fontWeight: 'bold', fontSize: 16, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
                          {channel.name}
                        </div>
                        <Space size={4}>
                          {channel.categories.slice(0, 1).map(cat => (
                            <Tag key={cat} color="blue">{cat}</Tag>
                          ))}
                        </Space>
                      </div>
                      <RightOutlined style={{ color: '#bfbfbf', marginTop: 4 }} />
                    </div>

                    <div style={{ flex: 1 }}>
                      {program ? (
                        <div style={{ 
                          background: 'rgba(82, 196, 26, 0.05)', 
                          padding: '8px', 
                          borderRadius: 4, 
                          border: '1px solid rgba(82, 196, 26, 0.2)' 
                        }}>
                          <div style={{ color: '#52c41a', fontWeight: '500', marginBottom: 4 }}>
                            正在播放：{program.title}
                          </div>
                          <div style={{ fontSize: '12px', opacity: 0.7, display: 'flex', justifyContent: 'space-between', marginBottom: 4 }}>
                            <span>{program.startTime.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</span>
                            <span>{program.endTime.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</span>
                          </div>
                          <Progress
                            percent={progress}
                            size="small"
                            status="active"
                            strokeColor="#1890ff"
                            showInfo={false}
                          />
                        </div>
                      ) : (
                        <div style={{ color: '#bfbfbf', fontStyle: 'italic', padding: '8px' }}>
                          暂无节目信息
                        </div>
                      )}
                    </div>
                  </Card>
                </List.Item>
              )
            }}
          />
        ) : (
          <Empty description="暂无频道数据" />
        )}
      </div>
    </div>
  )
}