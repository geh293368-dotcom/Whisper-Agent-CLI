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
  const headerScrollRef = useRef<HTMLDivElement | null>(null)
  const bodyScrollRef = useRef<HTMLDivElement | null>(null)
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

  const renderColGroup = () => (
    <colgroup>
      {columns.map(col => {
        const width = widths[col.id]
        return <col key={col.id} style={width !== undefined ? { width } : undefined} />
      })}
    </colgroup>
  )

  const syncHeaderScroll = () => {
    if (headerScrollRef.current && bodyScrollRef.current) {
      headerScrollRef.current.scrollLeft = bodyScrollRef.current.scrollLeft
    }
  }

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
      <div className="table-head-scroll" ref={headerScrollRef}>
        <table>
          {renderColGroup()}
          <thead>
            <tr>
              {columns.map(col => {
                const style: React.CSSProperties = { position: 'relative' }
                if (col.align) style.textAlign = col.align
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
        </table>
      </div>
      <div className="table-body-scroll" ref={bodyScrollRef} onScroll={syncHeaderScroll}>
        <table>
          {renderColGroup()}
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
    </div>
  )
}
