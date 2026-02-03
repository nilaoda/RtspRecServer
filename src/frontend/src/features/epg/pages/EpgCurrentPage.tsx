import { Card, List, Tag, Space, Typography, Avatar, Progress } from 'antd'
import { PlayCircleOutlined } from '@ant-design/icons'
import { useEpgData } from '../hooks/useEpgData'
import Loading from '../../shared/Loading'

const { Title } = Typography

export default function EpgCurrentPage() {
  const { currentPrograms, loading, now } = useEpgData()

  if (loading && currentPrograms.length === 0) {
    return <Loading tip="正在加载当前节目..." />
  }

  const getProgress = (startTime: Date, endTime: Date) => {
    const start = startTime.getTime()
    const end = endTime.getTime()
    
    if (now >= end) return 100
    if (now <= start) return 0
    
    return Math.round(((now - start) / (end - start)) * 100)
  }

  return (
    <div style={{ padding: '0 8px' }}>
      <Title level={2}>
        <PlayCircleOutlined style={{ marginRight: 8 }} />
        当前播放
      </Title>

      <List
        grid={{ gutter: 16, xs: 1, sm: 2, md: 2, lg: 3, xl: 3, xxl: 4 }}
        dataSource={currentPrograms}
        renderItem={(currentProgram) => {
          const progress = getProgress(currentProgram.startTime, currentProgram.endTime)
          
          return (
            <List.Item>
              <Card
                  style={{
                    background: 'rgba(24, 144, 255, 0.05)',
                    borderColor: 'rgba(24, 144, 255, 0.2)'
                  }}
                >
                  <Card.Meta
                    avatar={
                      <Avatar size={48} style={{ backgroundColor: '#1890ff' }}>
                        <PlayCircleOutlined />
                      </Avatar>
                    }
                  title={
                    <Space>
                      <span style={{ fontSize: '16px', fontWeight: 'bold' }}>
                        {currentProgram.channelName}
                      </span>
                    </Space>
                  }
                  description={
                    <Space direction="vertical" style={{ width: '100%' }}>
                      <div style={{ fontSize: '14px', color: 'inherit', fontWeight: 'bold' }}>
                        {currentProgram.program.title}
                      </div>
                      <div style={{ fontSize: '12px', opacity: 0.7 }}>
                        {currentProgram.startTime.toLocaleTimeString()} - {currentProgram.endTime.toLocaleTimeString()}
                      </div>
                      {currentProgram.program.description && (
                        <div style={{ fontSize: '12px', opacity: 0.7 }}>
                          {currentProgram.program.description}
                        </div>
                      )}
                      <div>
                        <Tag color="blue">{currentProgram.program.category}</Tag>
                      </div>
                      <div style={{ marginTop: 8 }}>
                        <Progress
                          percent={progress}
                          size="small"
                          strokeColor="#1890ff"
                          format={() => `${progress}%`}
                        />
                      </div>
                    </Space>
                  }
                />
              </Card>
            </List.Item>
          )
        }}
      />
    </div>
  )
}