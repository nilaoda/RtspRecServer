import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type React from 'react'
import { Button, Card, Modal, Space, Spin, Table, Typography } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { Resizable } from 'react-resizable'
import type { ResizeCallbackData } from 'react-resizable'
import { useAppContext } from '../../app/context'
import type { RecordingFileInfo } from '../../types'
import { formatBytes, formatDateTime } from '../shared/format'

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

const RecordingsPage = () => {
  const { recordings, reloadRecordings, getRecordingMediaInfo, tasks } = useAppContext()
  const [infoModal, setInfoModal] = useState<{ title: string; content: string } | null>(null)
  const [infoLoading, setInfoLoading] = useState(false)
  const [columnWidths, setColumnWidths] = useState<Record<string, number>>({
    fileName: 260,
    fileSizeBytes: 140,
    recordedAt: 200,
    filePath: 360,
    actions: 160,
  })
  const tableWrapRef = useRef<HTMLDivElement | null>(null)
  const [tableWidth, setTableWidth] = useState(0)

  const onViewInfo = useCallback(
    async (filePath: string, fileName: string) => {
      setInfoLoading(true)
      try {
        const info = await getRecordingMediaInfo(filePath)
        setInfoModal({ title: fileName, content: info })
      } catch {
        return
      } finally {
        setInfoLoading(false)
      }
    },
    [getRecordingMediaInfo],
  )
  const visibleRecordings = useMemo(() => {
    const normalizeName = (value: string) => value.split(/[/\\]/).pop() ?? value
    const isTsFile = (value: string) => value.toLowerCase().endsWith('.ts')
    const activeNames = new Set(
      tasks
        .filter((task) => task.status === 'Recording')
        .flatMap((task) => {
          const names: string[] = []
          if (task.filePath) {
            names.push(normalizeName(task.filePath))
          }
          if (task.taskName) {
            names.push(task.taskName.endsWith('.ts') ? task.taskName : `${task.taskName}.ts`)
          }
          return names
        }),
    )
    return recordings.filter(
      (record) =>
        isTsFile(record.fileName) &&
        !activeNames.has(record.fileName) &&
        !activeNames.has(normalizeName(record.filePath)),
    )
  }, [recordings, tasks])

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

  const columns = useMemo<ColumnsType<RecordingFileInfo>>(
    () => [
      { title: '文件名', dataIndex: 'fileName', key: 'fileName', width: columnWidths.fileName, ellipsis: true },
      {
        title: '大小',
        dataIndex: 'fileSizeBytes',
        key: 'fileSizeBytes',
        width: columnWidths.fileSizeBytes,
        render: (value: number) => formatBytes(value),
      },
      {
        title: '录制时间',
        dataIndex: 'recordedAt',
        key: 'recordedAt',
        width: columnWidths.recordedAt,
        render: (value: string) => formatDateTime(new Date(value)),
      },
      {
        title: '路径',
        dataIndex: 'filePath',
        key: 'filePath',
        width: columnWidths.filePath,
        render: (value: string) => (
          <Typography.Text type="secondary" ellipsis>
            {value}
          </Typography.Text>
        ),
      },
      {
        title: '操作',
        dataIndex: 'filePath',
        key: 'actions',
        width: columnWidths.actions,
        render: (_, record) => (
          <Button size="small" onClick={() => onViewInfo(record.filePath, record.fileName)}>
            查看信息
          </Button>
        ),
      },
    ],
    [columnWidths, onViewInfo],
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
          <Button onClick={reloadRecordings}>刷新列表</Button>
          <Typography.Text type="secondary">已录文件: {visibleRecordings.length}</Typography.Text>
        </div>
      </Card>
      <Card title="录制文件">
        <div ref={tableWrapRef}>
          <Table
            rowKey="filePath"
            columns={resizableColumns}
            dataSource={visibleRecordings}
            pagination={false}
            size="small"
            scroll={shouldScroll ? { x: totalWidth } : undefined}
            locale={{ emptyText: '暂无录制文件' }}
            tableLayout="fixed"
            components={{ header: { cell: ResizableTitle } }}
          />
        </div>
      </Card>
      <Modal
        open={Boolean(infoModal) || infoLoading}
        title={infoModal?.title ?? ''}
        onCancel={() => {
          setInfoModal(null)
          setInfoLoading(false)
        }}
        footer={null}
        width={720}
      >
        {infoLoading ? (
          <div style={{ textAlign: 'center', padding: '40px 0' }}>
            <Spin size="large" tip="正在加载媒体信息..." />
          </div>
        ) : (
          <pre className="info-code">{infoModal?.content ?? ''}</pre>
        )}
      </Modal>
    </Space>
  )
}

export default RecordingsPage
