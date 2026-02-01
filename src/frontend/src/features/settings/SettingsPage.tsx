import { useCallback } from 'react'
import { Button, Card, Form, InputNumber, Space, Typography, message } from 'antd'
import { useAppContext } from '../../app/context'

const SettingsPage = () => {
  const { appConfig, updateConfig } = useAppContext()
  const initialValue = appConfig?.maxRecordingTasks ?? 1

  const onSave = useCallback(
    (values: { maxRecordingTasks: number }) => {
      if (!Number.isFinite(values.maxRecordingTasks) || values.maxRecordingTasks < 1) {
        message.error('最大并发必须大于 0')
        return
      }
      void updateConfig({ maxRecordingTasks: Math.floor(values.maxRecordingTasks) })
    },
    [updateConfig],
  )

  return (
    <Space direction="vertical" size="middle" style={{ width: '100%' }}>
      <Card bodyStyle={{ padding: 24 }}>
        <Space direction="vertical" size={16} style={{ width: '100%' }}>
          <div>
            <Typography.Title level={4} style={{ marginBottom: 4 }}>
              应用配置
            </Typography.Title>
            <Typography.Text type="secondary">仅开放 maxRecordingTasks，其他配置由服务端管理</Typography.Text>
          </div>
          <div style={{ display: 'grid', gap: 16, gridTemplateColumns: 'minmax(240px, 360px) auto' }}>
            <Form
              key={initialValue}
              layout="vertical"
              initialValues={{ maxRecordingTasks: initialValue }}
              onFinish={onSave}
              style={{ maxWidth: 360 }}
            >
              <Form.Item
                name="maxRecordingTasks"
                label="最大并发录制任务数"
                rules={[{ required: true, message: '请输入最大并发录制任务数' }]}
              >
                <InputNumber min={1} style={{ width: '100%' }} />
              </Form.Item>
              <Button type="primary" htmlType="submit">
                保存配置
              </Button>
            </Form>
            <div
              style={{
                padding: 16,
                borderRadius: 12,
                border: '1px solid rgba(0,0,0,0.08)',
                background: 'linear-gradient(135deg, rgba(24,144,255,0.08), rgba(82,196,26,0.08))',
              }}
            >
              <Typography.Text strong>提示</Typography.Text>
              <Typography.Text type="secondary" style={{ marginTop: 8, display: 'block' }}>
                建议根据服务器性能设置并发数，避免磁盘与带宽竞争导致录制失败。
              </Typography.Text>
            </div>
          </div>
        </Space>
      </Card>
    </Space>
  )
}

export default SettingsPage
