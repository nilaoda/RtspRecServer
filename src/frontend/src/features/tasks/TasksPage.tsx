import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type React from 'react'
import {
  Button,
  Card,
  DatePicker,
  Form,
  Input,
  Modal,
  Popconfirm,
  Progress,
  Select,
  Space,
  Table,
  Tag,
  Typography,
  message,
} from 'antd'
import type { ColumnsType } from 'antd/es/table'
import dayjs, { type Dayjs } from 'dayjs'
import { Resizable } from 'react-resizable'
import type { ResizeCallbackData } from 'react-resizable'
import { useAppContext } from '../../app/context'
import type { RecordingStatus, RecordingTaskDto } from '../../types'
import { formatBytes, formatDateTime } from '../shared/format'

type TaskFormValues = {
  channelId: number
  startTime: Dayjs
  endTime: Dayjs
  taskName?: string
}

type RecordingStatusKey = 'Pending' | 'Recording' | 'Completed' | 'Failed' | 'Stopped'

const statusKeys: RecordingStatusKey[] = ['Pending', 'Recording', 'Completed', 'Failed', 'Stopped']

const normalizeStatus = (value: RecordingStatus): RecordingStatusKey => {
  if (typeof value === 'number') {
    return statusKeys[value] ?? 'Pending'
  }
  if (statusKeys.includes(value as RecordingStatusKey)) {
    return value as RecordingStatusKey
  }
  return 'Pending'
}

const formatDuration = (milliseconds: number) => {
  if (!Number.isFinite(milliseconds) || milliseconds < 0) {
    return '--'
  }
  const totalSeconds = Math.floor(milliseconds / 1000)
  const hours = Math.floor(totalSeconds / 3600)
  const minutes = Math.floor((totalSeconds % 3600) / 60)
  const seconds = totalSeconds % 60
  const pad = (value: number) => String(value).padStart(2, '0')
  return `${pad(hours)}:${pad(minutes)}:${pad(seconds)}`
}

const statusLabel: Record<RecordingStatusKey, string> = {
  Pending: '等待中',
  Recording: '录制中',
  Completed: '已完成',
  Failed: '失败',
  Stopped: '已停止',
}

const statusColor: Record<RecordingStatusKey, string> = {
  Pending: 'warning',
  Recording: 'processing',
  Completed: 'success',
  Failed: 'error',
  Stopped: 'default',
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

const TasksPage = () => {
  const { tasks, channels, createTask, stopTask, deleteTask, getTaskMediaInfo, now, appConfig } = useAppContext()
  const [form] = Form.useForm<TaskFormValues>()
  const [createOpen, setCreateOpen] = useState(false)
  const [infoModal, setInfoModal] = useState<{ title: string; content: string } | null>(null)
  const [columnWidths, setColumnWidths] = useState<Record<string, number>>({
    id: 50,
    channelName: 180,
    startTime: 180,
    endTime: 180,
    duration: 210,
    progress: 200,
    bytesWritten: 130,
    status: 120,
    actions: 240,
  })
  const tableWrapRef = useRef<HTMLDivElement | null>(null)
  const [tableWidth, setTableWidth] = useState(0)

  const activeCount = useMemo(
    () => tasks.filter((task) => task.status === 'Recording').length,
    [tasks],
  )

  const openModal = useCallback(() => {
    const stored = window.localStorage.getItem('rtsp_task_draft')
    const draft = stored ? (JSON.parse(stored) as { channelId?: number; startTime?: string; endTime?: string; taskName?: string }) : null
    const storedStart = draft?.startTime ? dayjs(draft.startTime) : null
    const storedEnd = draft?.endTime ? dayjs(draft.endTime) : null
    const startTime = storedStart && storedStart.isValid() ? storedStart : dayjs()
    const endTime = storedEnd && storedEnd.isValid() && storedEnd.isAfter(startTime) ? storedEnd : startTime.add(1800, 'second')
    const preferredChannelId = draft?.channelId && channels.some((channel) => channel.id === draft.channelId) ? draft.channelId : channels[0]?.id
    form.setFieldsValue({
      channelId: preferredChannelId,
      startTime,
      endTime,
      taskName: draft?.taskName ?? '',
    })
    setCreateOpen(true)
  }, [channels, form])

  const onCreateTask = useCallback(async () => {
    const values = await form.validateFields()
    if (values.endTime.valueOf() <= values.startTime.valueOf()) {
      message.error('结束时间必须大于开始时间')
      return
    }
    if (appConfig && activeCount >= appConfig.maxRecordingTasks) {
      message.info('当前并发已达上限，任务将进入等待队列')
    }
    window.localStorage.setItem(
      'rtsp_task_draft',
      JSON.stringify({
        channelId: values.channelId,
        startTime: values.startTime.format('YYYY-MM-DDTHH:mm:ssZ'),
        endTime: values.endTime.format('YYYY-MM-DDTHH:mm:ssZ'),
        taskName: values.taskName?.trim() ? values.taskName.trim() : '',
      }),
    )
    setCreateOpen(false)
    void createTask({
      channelId: values.channelId,
      startTime: values.startTime.format('YYYY-MM-DDTHH:mm:ssZ'),
      endTime: values.endTime.format('YYYY-MM-DDTHH:mm:ssZ'),
      taskName: values.taskName?.trim() ? values.taskName.trim() : null,
    })
  }, [activeCount, appConfig, createTask, form])

  const onStopTask = useCallback(
    (id: number) => {
      void stopTask(id)
    },
    [stopTask],
  )

  const onDeleteTask = useCallback(
    (id: number) => {
      void deleteTask(id)
    },
    [deleteTask],
  )

  const onViewInfo = useCallback(
    async (id: number, title: string) => {
      try {
        const info = await getTaskMediaInfo(id)
        setInfoModal({ title, content: info })
      } catch {
        return
      }
    },
    [getTaskMediaInfo],
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

  const columns = useMemo<ColumnsType<RecordingTaskDto>>(
    () => [
      { title: 'ID', dataIndex: 'id', key: 'id', width: columnWidths.id },
      {
        title: '频道名称',
        dataIndex: 'channelName',
        key: 'channelName',
        width: columnWidths.channelName,
        ellipsis: true,
        render: (_, task) => `${task.channelId} - ${task.channelName}`,
      },
      {
        title: '计划开始',
        dataIndex: 'startTime',
        key: 'startTime',
        width: columnWidths.startTime,
        render: (value: string) => formatDateTime(new Date(value)),
      },
      {
        title: '计划结束',
        dataIndex: 'endTime',
        key: 'endTime',
        width: columnWidths.endTime,
        render: (value: string) => formatDateTime(new Date(value)),
      },
      {
        title: '录制时长',
        dataIndex: 'endTime',
        key: 'duration',
        width: columnWidths.duration,
        render: (_, task) => {
          const start = new Date(task.startTime).getTime()
          const end = new Date(task.endTime).getTime()
          const nowMs = now.getTime()
          const normalizedStatus = normalizeStatus(task.status)
          const startedAt = task.startedAt ? new Date(task.startedAt).getTime() : Number.NaN
          const finishedAt = task.finishedAt ? new Date(task.finishedAt).getTime() : Number.NaN
          const plannedDuration = Number.isFinite(start) && Number.isFinite(end) ? Math.max(0, end - start) : Number.NaN
          const recordedDuration = Number.isFinite(startedAt)
            ? Number.isFinite(finishedAt)
              ? Math.max(0, finishedAt - startedAt)
              : normalizedStatus === 'Recording'
                ? Math.max(0, nowMs - startedAt)
                : 0
            : 0
          if (!Number.isFinite(plannedDuration)) {
            return '--'
          }
          return `${formatDuration(recordedDuration)}/${formatDuration(plannedDuration)}`
        },
      },
      {
        title: '进度',
        dataIndex: 'status',
        key: 'progress',
        width: columnWidths.progress,
        render: (_, task) => {
          const start = new Date(task.startTime).getTime()
          const end = new Date(task.endTime).getTime()
          const nowMs = now.getTime()
          const normalizedStatus = normalizeStatus(task.status)
          const startedAt = task.startedAt ? new Date(task.startedAt).getTime() : Number.NaN
          const finishedAt = task.finishedAt ? new Date(task.finishedAt).getTime() : Number.NaN
          const plannedDuration = Number.isFinite(start) && Number.isFinite(end) ? Math.max(0, end - start) : Number.NaN
          const progressStart = Number.isFinite(startedAt) ? startedAt : start
          const progressEnd = Number.isFinite(finishedAt)
            ? finishedAt
            : Number.isFinite(plannedDuration)
              ? progressStart + plannedDuration
              : end
          const progress =
            normalizedStatus === 'Pending'
              ? 0
              : normalizedStatus === 'Completed' || normalizedStatus === 'Stopped'
              ? 100
              : Number.isFinite(progressStart) && Number.isFinite(progressEnd) && progressEnd > progressStart
                ? Math.min(100, Math.max(0, ((Math.min(nowMs, progressEnd) - progressStart) / (progressEnd - progressStart)) * 100))
                : 0
          return (
            <div style={{ display: 'flex', alignItems: 'center', gap: 8, whiteSpace: 'nowrap' }}>
              <Typography.Text type="secondary" style={{ minWidth: 50 }}>
                {progress.toFixed(1)}%
              </Typography.Text>
              <Progress
                size="small"
                percent={Number(progress.toFixed(1))}
                showInfo={false}
                status={
                  normalizedStatus === 'Failed' ? 'exception' : normalizedStatus === 'Completed' ? 'success' : 'active'
                }
                style={{ flex: 1, minWidth: 120 }}
              />
            </div>
          )
        },
      },
      {
        title: '已录大小',
        dataIndex: 'bytesWritten',
        key: 'bytesWritten',
        width: columnWidths.bytesWritten,
        render: (value: number) => formatBytes(value),
      },
      {
        title: '状态',
        dataIndex: 'status',
        key: 'status',
        width: columnWidths.status,
        render: (value: RecordingStatus, record) => {
          const normalizedStatus = normalizeStatus(value)
          return (
            <Tag color={statusColor[normalizedStatus]} title={record.errorMessage ?? ''}>
              {statusLabel[normalizedStatus]}
            </Tag>
          )
        },
      },
      {
        title: '操作',
        dataIndex: 'id',
        key: 'actions',
        width: columnWidths.actions,
        render: (_, task) => {
          const normalizedStatus = normalizeStatus(task.status)
          const showInfoActions =
            normalizedStatus === 'Completed' || normalizedStatus === 'Failed' || normalizedStatus === 'Stopped'
          return (
            <Space size={4} wrap>
              {normalizedStatus === 'Recording' ? (
                <Popconfirm
                  title="确认停止录制？"
                  okText="停止"
                  cancelText="取消"
                  onConfirm={() => onStopTask(task.id)}
                >
                  <Button danger size="small" onClick={(event) => event.stopPropagation()}>
                    停止录制
                  </Button>
                </Popconfirm>
              ) : null}
              {normalizedStatus === 'Pending' ? (
                <Popconfirm
                  title="确认取消任务？"
                  okText="取消任务"
                  cancelText="继续等待"
                  onConfirm={() => onStopTask(task.id)}
                >
                  <Button danger size="small" onClick={(event) => event.stopPropagation()}>
                    取消任务
                  </Button>
                </Popconfirm>
              ) : null}
              {showInfoActions ? (
                <>
                  <Button size="small" onClick={() => onViewInfo(task.id, task.taskName)}>
                    查看信息
                  </Button>
                  <Button size="small" type="link" href={`/api/tasks/${task.id}/download`}>
                    下载
                  </Button>
                  <Popconfirm
                    title="确认删除该任务记录？"
                    okText="删除"
                    cancelText="取消"
                    onConfirm={() => onDeleteTask(task.id)}
                  >
                    <Button
                      danger
                      size="small"
                      onClick={(event) => event.stopPropagation()}
                    >
                      删除
                    </Button>
                  </Popconfirm>
                </>
              ) : null}
            </Space>
          )
        },
      },
    ],
    [columnWidths, now, onDeleteTask, onStopTask, onViewInfo],
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
        <div style={{ display: 'flex', justifyContent: 'space-between', flexWrap: 'wrap', gap: 12 }}>
          <Button type="primary" onClick={openModal}>
            新建录制任务
          </Button>
        </div>
      </Card>
      <Card title="录制任务">
        <div ref={tableWrapRef}>
          <Table
            rowKey="id"
            columns={resizableColumns}
            dataSource={tasks}
            pagination={false}
            size="small"
            scroll={shouldScroll ? { x: totalWidth } : undefined}
            locale={{ emptyText: '暂无任务' }}
            tableLayout="fixed"
            components={{ header: { cell: ResizableTitle } }}
          />
        </div>
      </Card>
      <Modal
        open={createOpen}
        title="新建录制任务"
        onOk={onCreateTask}
        onCancel={() => setCreateOpen(false)}
        okText="提交任务"
        cancelText="取消"
      >
        <Form form={form} layout="vertical">
          <Form.Item name="channelId" label="频道选择" rules={[{ required: true, message: '请选择频道' }]}>
            <Select options={channels.map((channel) => ({ value: channel.id, label: `${channel.id} - ${channel.name}` }))} />
          </Form.Item>
          <Form.Item name="startTime" label="开始时间" rules={[{ required: true, message: '请选择开始时间' }]}>
            <DatePicker showTime={{ format: 'HH:mm:ss' }} format="YYYY-MM-DD HH:mm:ss" />
          </Form.Item>
          <Form.Item name="endTime" label="结束时间" rules={[{ required: true, message: '请选择结束时间' }]}>
            <DatePicker showTime={{ format: 'HH:mm:ss' }} format="YYYY-MM-DD HH:mm:ss" />
          </Form.Item>
          <Form.Item name="taskName" label="任务名称">
            <Input placeholder="Playback" />
          </Form.Item>
        </Form>
        <Typography.Text type="secondary">时间为本地时区，系统将自动记忆上次输入</Typography.Text>
      </Modal>
      <Modal
        open={Boolean(infoModal)}
        title={infoModal?.title ?? ''}
        onCancel={() => setInfoModal(null)}
        footer={null}
        width={720}
      >
        <pre className="info-code">{infoModal?.content ?? ''}</pre>
      </Modal>
    </Space>
  )
}

export default TasksPage
