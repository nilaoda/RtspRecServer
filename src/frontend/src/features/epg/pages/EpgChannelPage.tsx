import { useState, useEffect, useRef } from 'react'
import { Card, Tag, Space, Typography, Timeline, Empty, Progress, Layout, Menu, Radio, theme } from 'antd'
import { PlayCircleOutlined, ClockCircleOutlined, CalendarOutlined } from '@ant-design/icons'
import { useParams, useNavigate } from 'react-router-dom'
import { useEpgData } from '../hooks/useEpgData'
import type { EpgProgram } from '../types/epg'
import Loading from '../../shared/Loading'

const { Title, Text } = Typography
const { Sider, Content } = Layout
const { useToken } = theme

export default function EpgChannelPage() {
  const { token } = useToken()
  const { channelId: urlChannelId } = useParams<{ channelId: string }>()
  const navigate = useNavigate()
  const { channels, getChannelPrograms, loading: globalLoading, now } = useEpgData()
  
  const [selectedChannelId, setSelectedChannelId] = useState<string | null>(urlChannelId || null)
  const [programs, setPrograms] = useState<EpgProgram[]>([])
  const [loadingPrograms, setLoadingPrograms] = useState(false)
  const [currentProgramId, setCurrentProgramId] = useState<string | null>(null)
  const [selectedDate, setSelectedDate] = useState<string>(() => {
    const d = new Date();
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  })

  // 辅助函数：格式化日期为本地 YYYY-MM-DD
  const formatDate = (date: Date) => {
    const year = date.getFullYear();
    const month = String(date.getMonth() + 1).padStart(2, '0');
    const day = String(date.getDate()).padStart(2, '0');
    return `${year}-${month}-${day}`;
  }

  const currentProgramRef = useRef<HTMLDivElement>(null)

  // 监听时间变化，实时更新当前节目 ID
  useEffect(() => {
    if (programs.length > 0) {
      const current = programs.find(p => p.startTime.getTime() <= now && p.endTime.getTime() > now)
      if (current?.id !== currentProgramId) {
        setCurrentProgramId(current?.id || null)
      }
    }
  }, [now, programs, currentProgramId])

  // 只有当当前节目发生变化时才执行滚动
  useEffect(() => {
    if (currentProgramId && !loadingPrograms) {
      const timer = setTimeout(() => {
        currentProgramRef.current?.scrollIntoView({ behavior: 'smooth', block: 'center' })
      }, 500)
      return () => clearTimeout(timer)
    }
  }, [currentProgramId, loadingPrograms])

  // 同步 URL 参数到状态
  useEffect(() => {
    if (urlChannelId) {
      setSelectedChannelId(urlChannelId)
    } else if (channels.length > 0 && !selectedChannelId) {
      setSelectedChannelId(channels[0].id)
    }
  }, [urlChannelId, channels, selectedChannelId])

  // 当选择的频道改变时获取节目单
  useEffect(() => {
    if (selectedChannelId) {
      const fetchPrograms = async () => {
        setLoadingPrograms(true)
        try {
          const data = await getChannelPrograms(selectedChannelId)
          setPrograms(data)
        } finally {
          setLoadingPrograms(false)
        }
      }
      fetchPrograms()
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedChannelId])

  const selectedChannel = channels.find(c => c.id === selectedChannelId)

  // 获取过去几天的日期列表
  const dateOptions = Array.from({ length: 4 }).map((_, i) => {
    const d = new Date()
    d.setDate(d.getDate() - i)
    return {
      label: i === 0 ? '今天' : i === 1 ? '昨天' : formatDate(d),
      value: formatDate(d)
    }
  }).reverse()

  // 过滤节目单
  const filteredPrograms = programs.filter(p => {
    const programDate = formatDate(p.startTime)
    return programDate === selectedDate
  })

  if (globalLoading && channels.length === 0) {
    return <Loading tip="正在加载频道列表..." />
  }

  return (
    <Layout className="epg-channel-layout" style={{ background: 'transparent', height: '100%', overflow: 'hidden' }}>
      <Sider 
        width={250} 
        theme="light" 
        style={{ 
          borderRight: `1px solid ${token.colorBorderSecondary}`, 
          height: '100%',
          borderRadius: '8px 0 0 8px',
          background: token.colorBgContainer,
        }}
      >
        <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
          <div style={{ 
            padding: '16px', 
            borderBottom: `1px solid ${token.colorBorderSecondary}`, 
            fontWeight: 'bold', 
            background: token.colorBgContainer, 
            zIndex: 1,
            flexShrink: 0
          }}>
            <PlayCircleOutlined style={{ marginRight: 8 }} />
            频道列表
          </div>
          <div style={{ flex: 1, overflowY: 'auto' }}>
            <Menu
              mode="inline"
              selectedKeys={selectedChannelId ? [selectedChannelId] : []}
              onSelect={({ key }) => {
                setSelectedChannelId(key)
                navigate(`/epg/channels/${key}`)
              }}
              items={channels.map(c => ({
                key: c.id,
                label: (
                  <div style={{ display: 'flex', alignItems: 'center' }}>
                    {c.iconUrl && <img src={c.iconUrl} alt="" style={{ width: 20, height: 20, marginRight: 8, objectFit: 'contain' }} />}
                    <span style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{c.name}</span>
                  </div>
                )
              }))}
              style={{ borderRight: 'none' }}
            />
          </div>
        </div>
      </Sider>
      
      <Content style={{ padding: '0 24px', height: '100%', display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
        {selectedChannel ? (
          <>
            <div style={{ flexShrink: 0, paddingTop: 0, paddingBottom: 16 }}>
              <Card style={{ borderRadius: '0 8px 8px 0' }} styles={{ body: { padding: '16px 24px' } }}>
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start' }}>
                  <Space direction="vertical" size={0}>
                    <Title level={3} style={{ margin: '0 0 8px 0' }}>
                      {selectedChannel.name}
                    </Title>
                    <Space wrap>
                      {selectedChannel.categories.map((category) => (
                        <Tag key={category} color="blue">
                          {category}
                        </Tag>
                      ))}
                      <Text type="secondary" style={{ fontSize: '12px' }}>
                        {selectedChannel.language} {selectedChannel.country && `· ${selectedChannel.country}`}
                      </Text>
                    </Space>
                  </Space>
                  <div style={{ textAlign: 'right' }}>
                    <Text type="secondary">
                      <CalendarOutlined style={{ marginRight: 4 }} />
                      {new Date().toLocaleDateString()}
                    </Text>
                  </div>
                </div>
              </Card>
            </div>

            <Card 
              loading={loadingPrograms} 
              style={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0, marginBottom: 16 }}
              styles={{ 
                header: { position: 'sticky', top: 0, zIndex: 2, background: token.colorBgContainer, borderBottom: `1px solid ${token.colorBorderSecondary}` },
                body: { padding: '24px', flex: 1, overflowY: 'auto' } 
              }}
              title={
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', width: '100%' }}>
                  <Space>
                    <ClockCircleOutlined />
                    <span>节目单</span>
                  </Space>
                  <Space>
                    <Radio.Group 
                      size="small" 
                      value={selectedDate} 
                      onChange={e => setSelectedDate(e.target.value)}
                      buttonStyle="solid"
                    >
                      {dateOptions.map(option => (
                        <Radio.Button key={option.value} value={option.value}>
                          {option.label}
                        </Radio.Button>
                      ))}
                    </Radio.Group>
                  </Space>
                </div>
              }
            >
              {filteredPrograms.length > 0 ? (
                <Timeline
                  mode="left"
                  items={filteredPrograms.map((program) => {
                    const isCurrent = program.startTime.getTime() <= now && program.endTime.getTime() > now
                    const isPast = program.endTime.getTime() <= now
                    
                    let progress = 0
                    if (isCurrent) {
                      const total = program.endTime.getTime() - program.startTime.getTime()
                      const elapsed = now - program.startTime.getTime()
                      progress = Math.min(Math.max(Math.round((elapsed / total) * 100), 0), 100)
                    }

                    return {
                      label: (
                        <Text style={{ 
                          fontSize: '14px', 
                          fontWeight: isCurrent ? 'bold' : 'normal',
                          color: isCurrent ? '#1890ff' : (isPast ? '#bfbfbf' : 'inherit')
                        }}>
                          {program.startTime.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                        </Text>
                      ),
                      color: isCurrent ? '#1890ff' : (isPast ? '#d9d9d9' : '#1890ff'),
                      children: (
                        <div 
                          ref={isCurrent ? currentProgramRef : null}
                          style={{
                            padding: '12px 16px',
                            borderRadius: '8px',
                            background: isCurrent ? 'rgba(24, 144, 255, 0.05)' : 'transparent',
                            border: isCurrent ? '1px solid rgba(24, 144, 255, 0.2)' : '1px solid transparent',
                            transition: 'all 0.3s',
                            marginBottom: '8px'
                          }}
                        >
                          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                            <Text style={{ 
                              fontSize: '16px', 
                              fontWeight: isCurrent ? 'bold' : 'normal',
                              color: isPast ? '#bfbfbf' : 'inherit'
                            }}>
                              {program.title}
                            </Text>
                            {isCurrent && <Tag color="blue">正在播出</Tag>}
                          </div>
                          
                          {isCurrent && (
                            <div style={{ marginTop: 8 }}>
                              <Progress 
                                percent={progress} 
                                size="small" 
                                status="active" 
                                strokeColor="#1890ff"
                              />
                              <Text type="secondary" style={{ fontSize: '12px' }}>
                                剩余 {Math.round((program.endTime.getTime() - now) / 60000)} 分钟
                              </Text>
                            </div>
                          )}
                          
                          {program.description && !isPast && (
                            <div style={{ marginTop: 8, color: '#8c8c8c', fontSize: '13px' }}>
                              {program.description}
                            </div>
                          )}
                        </div>
                      )
                    }
                  })}
                />
              ) : (
                !loadingPrograms && <Empty description="该频道暂无节目单信息" />
              )}
            </Card>
          </>
        ) : (
          <Empty style={{ marginTop: 100 }} description="未找到频道信息" />
        )}
      </Content>
    </Layout>
  )
}