import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type React from 'react'
import { Button, Card, Form, Input, Modal, Space, Table, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { Resizable } from 'react-resizable'
import type { ResizeCallbackData } from 'react-resizable'
import { useAppContext } from '../../app/context'
import type { ChannelConfig } from '../../types'

type ChannelFormValues = {
  name: string
  url: string
}

type ResizableTitleProps = React.HTMLAttributes<HTMLTableCellElement> & {
  onResize?: (event: React.SyntheticEvent, data: ResizeCallbackData) => void
  width?: number
}

const ResizableTitle = ({ onResize, width, ...restProps }: ResizableTitleProps) => {
  if (!width) {
    return <th {...restProps} />
  }
  return (
    <Resizable
      width={width}
      height={0}
      handle={<span className="resizable-handle" onClick={(event) => event.stopPropagation()} />}
      onResize={onResize}
      draggableOpts={{ enableUserSelectHack: false }}
    >
      <th {...restProps} />
    </Resizable>
  )
}

const ChannelsPage = () => {
  const { channels, addChannel, updateChannel, deleteChannel } = useAppContext()
  const [form] = Form.useForm<ChannelFormValues>()
  const [drafts, setDrafts] = useState<Record<number, { name: string; url: string }>>({})
  const [columnWidths, setColumnWidths] = useState<Record<string, number>>({
    id: 80,
    name: 220,
    url: 420,
    actions: 200,
  })
  const tableWrapRef = useRef<HTMLDivElement | null>(null)
  const [tableWidth, setTableWidth] = useState(0)

  const onAdd = useCallback(async () => {
    const values = await form.validateFields()
    void addChannel({ name: values.name.trim(), url: values.url.trim() }).then(() => {
      form.resetFields()
    })
  }, [addChannel, form])

  const onSave = useCallback(
    (id: number) => {
      const draft = drafts[id]
      if (!draft || !draft.name.trim() || !draft.url.trim()) {
        message.error('频道名称与地址不能为空')
        return
      }
      void updateChannel({ id, name: draft.name.trim(), url: draft.url.trim() }).then(() => {
        setDrafts((prev) => {
          const next = { ...prev }
          delete next[id]
          return next
        })
      })
    },
    [drafts, updateChannel],
  )

  const onRemove = useCallback(
    (id: number) => {
      Modal.confirm({
        title: '确认删除该频道？',
        okText: '删除',
        okButtonProps: { danger: true },
        onOk: () => {
          void deleteChannel(id)
        },
      })
    },
    [deleteChannel],
  )

  const handleResize = useCallback(
    (key: string) =>
      (_: React.SyntheticEvent, { size }: ResizeCallbackData) => {
        setColumnWidths((prev) => ({
          ...prev,
          [key]: Math.max(80, Math.round(size.width)),
        }))
      },
    [],
  )

  const columns = useMemo<ColumnsType<ChannelConfig>>(
    () => [
      { title: 'ID', dataIndex: 'id', key: 'id', width: columnWidths.id },
      {
        title: '名称',
        dataIndex: 'name',
        key: 'name',
        width: columnWidths.name,
        render: (_, channel) => {
          const draft = drafts[channel.id] ?? channel
          return (
            <Input
              value={draft.name}
              onChange={(event) =>
                setDrafts((prev) => ({
                  ...prev,
                  [channel.id]: { ...draft, name: event.target.value },
                }))
              }
            />
          )
        },
      },
      {
        title: '地址',
        dataIndex: 'url',
        key: 'url',
        width: columnWidths.url,
        render: (_, channel) => {
          const draft = drafts[channel.id] ?? channel
          return (
            <Input
              value={draft.url}
              onChange={(event) =>
                setDrafts((prev) => ({
                  ...prev,
                  [channel.id]: { ...draft, url: event.target.value },
                }))
              }
            />
          )
        },
      },
      {
        title: '操作',
        dataIndex: 'id',
        key: 'actions',
        width: columnWidths.actions,
        render: (_, channel) => {
          const draft = drafts[channel.id] ?? channel
          const hasChanged = draft.name !== channel.name || draft.url !== channel.url
          return (
            <Space>
              <Button size="small" type="primary" onClick={() => onSave(channel.id)} disabled={!hasChanged}>
                保存
              </Button>
              <Button size="small" danger onClick={() => onRemove(channel.id)}>
                删除
              </Button>
            </Space>
          )
        },
      },
    ],
    [columnWidths, drafts, onRemove, onSave],
  )

  const resizableColumns = useMemo(
    () =>
      columns.map((col) => ({
        ...col,
        onHeaderCell: col.width
          ? () => ({
              width: col.width,
              onResize: handleResize(col.key as string),
            })
          : undefined,
      })),
    [columns, handleResize],
  )
  const totalWidth = useMemo(
    () => Object.values(columnWidths).reduce((sum, width) => sum + width, 0),
    [columnWidths],
  )
  const shouldScroll = tableWidth > 0 && totalWidth > tableWidth

  useEffect(() => {
    const node = tableWrapRef.current
    if (!node) {
      return
    }
    const observer = new ResizeObserver((entries) => {
      const entry = entries[0]
      if (entry) {
        setTableWidth(entry.contentRect.width)
      }
    })
    observer.observe(node)
    return () => observer.disconnect()
  }, [])
  return (
    <Space direction="vertical" size="middle" style={{ width: '100%' }}>
      <Card size="small">
        <Form form={form} layout="vertical" onFinish={onAdd}>
          <Space wrap align="start">
            <Form.Item name="name" label="频道名称" rules={[{ required: true, message: '请输入频道名称' }]}>
              <Input placeholder="输入频道名称" style={{ width: 220 }} />
            </Form.Item>
            <Form.Item name="url" label="频道地址" rules={[{ required: true, message: '请输入频道地址' }]}>
              <Input placeholder="rtsp://..." style={{ width: 380 }} />
            </Form.Item>
            <Form.Item label=" ">
              <Button type="primary" htmlType="submit">
                添加频道
              </Button>
            </Form.Item>
          </Space>
        </Form>
      </Card>
      <Card title="频道列表">
        <div ref={tableWrapRef}>
          <Table
            rowKey="id"
            columns={resizableColumns}
            dataSource={channels}
            pagination={false}
            size="small"
            scroll={shouldScroll ? { x: totalWidth } : undefined}
            locale={{ emptyText: '暂无频道' }}
            tableLayout="fixed"
            components={{ header: { cell: ResizableTitle } }}
          />
        </div>
      </Card>
    </Space>
  )
}

export default ChannelsPage
