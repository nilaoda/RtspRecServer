import { Card, List, Space, Typography } from 'antd'
import { FolderOutlined } from '@ant-design/icons'
import { useEpgData } from '../hooks/useEpgData'
import Loading from '../../shared/Loading'

const { Title } = Typography

export default function EpgCategoriesPage() {
  const { categories, loading } = useEpgData()

  if (loading && categories.length === 0) {
    return <Loading tip="正在加载分类列表..." />
  }

  return (
    <div style={{ padding: '0 8px' }}>
      <Title level={2}>
        <FolderOutlined style={{ marginRight: 8 }} />
        节目分类
      </Title>
      
      <List
        grid={{ gutter: 16, xs: 1, sm: 2, md: 3, lg: 4, xl: 4, xxl: 4 }}
        dataSource={categories}
        renderItem={(category) => (
          <List.Item>
            <Card hoverable>
              <Card.Meta
                title={
                  <Space>
                    <FolderOutlined />
                    <span style={{ fontSize: '16px', fontWeight: 'bold' }}>
                      {category}
                    </span>
                  </Space>
                }
                description={
                  <div style={{ fontSize: '14px', color: '#666' }}>
                    点击查看该分类下的所有频道
                  </div>
                }
              />
            </Card>
          </List.Item>
        )}
      />
    </div>
  )
}