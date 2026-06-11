import React, { useRef, useState } from 'react'

export interface TableColumn {
  id: string
  name: string
  width?: number
  minWidth?: number
  className?: string
  align?: 'left' | 'center' | 'right'
}

interface ResizableTableProps<T> {
  columns: TableColumn[]
  data: T[]
  rowKey: (item: T) => string | number
  renderRow: (item: T) => React.ReactNode
  emptyText: string
  className?: string
}

export function ResizableTable<T>({
  columns,
  data,
  rowKey,
  renderRow,
  emptyText,
  className = '',
}: ResizableTableProps<T>) {
  const [widths, setWidths] = useState<Record<string, number>>(() => {
    const initial: Record<string, number> = {}
    columns.forEach(col => {
      if (col.width !== undefined) {
        initial[col.id] = col.width
      }
    })
    return initial
  })

  // Refs to measure actual column widths from the DOM (for flex columns without initial width)
  const thRefs = useRef<Record<string, HTMLTableCellElement | null>>({})

  const handleMouseDown = (e: React.MouseEvent, columnId: string) => {
    e.preventDefault()
    const startX = e.clientX
    const column = columns.find(c => c.id === columnId)
    const minWidth = column?.minWidth ?? 30

    // If this column doesn't have a width in state yet, measure it from the DOM
    let startWidth = widths[columnId]
    if (startWidth === undefined) {
      const th = thRefs.current[columnId]
      startWidth = th ? th.offsetWidth : 100
      setWidths(prev => ({ ...prev, [columnId]: startWidth }))
    }

    // Prevent text selection and lock cursor during drag
    document.body.style.userSelect = 'none'
    document.body.style.cursor = 'col-resize'

    const handleMouseMove = (moveEvent: MouseEvent) => {
      const deltaX = moveEvent.clientX - startX
      const newWidth = Math.max(minWidth, startWidth + deltaX)
      setWidths(prev => ({
        ...prev,
        [columnId]: newWidth,
      }))
    }

    const handleMouseUp = () => {
      // Restore body styles
      document.body.style.userSelect = ''
      document.body.style.cursor = ''
      document.removeEventListener('mousemove', handleMouseMove)
      document.removeEventListener('mouseup', handleMouseUp)
    }

    document.addEventListener('mousemove', handleMouseMove)
    document.addEventListener('mouseup', handleMouseUp)
  }

  return (
    <div className={`table-wrap ${className}`}>
      <table>
        <thead>
          <tr>
            {columns.map(col => {
              const width = widths[col.id]
              const style: React.CSSProperties = {
                position: 'relative',
              }
              if (width !== undefined) {
                style.width = width
              }
              if (col.align) {
                style.textAlign = col.align
              }
              return (
                <th
                  key={col.id}
                  ref={el => { thRefs.current[col.id] = el }}
                  style={style}
                  className={col.className}
                >
                  {col.name}
                  <div className="resizer" onMouseDown={e => handleMouseDown(e, col.id)} />
                </th>
              )
            })}
          </tr>
        </thead>
        <tbody>
          {data.map(item => renderRow(item))}
        </tbody>
      </table>
      {data.length === 0 && (
        <div className="empty-state">
          <span>＋</span>
          <p>{emptyText}</p>
        </div>
      )}
    </div>
  )
}
