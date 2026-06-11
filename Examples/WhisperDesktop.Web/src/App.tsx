import { useEffect, useMemo, useState } from 'react'
import { desktopBridge } from './bridge'
import type { AppState, Job, Page, Segment } from './types'
import { ResizableTable, TableColumn } from './ResizableTable'

const getFilename = (path: string) => {
  if (!path) return ''
  const parts = path.split(/[\\/]/)
  return parts[parts.length - 1]
}

const emptyState: AppState = {
  engines: [], languages: [], formats: [], outputLocations: [], selectedEngine: '', selectedLanguage: '',
  selectedFormat: '', selectedOutputLocation: '', translate: false, modelPath: '', modelStatus: '正在连接桌面宿主',
  modelProgress: 0, outputFolder: '', globalStatus: '正在初始化...', isRunning: false, isLoadingModel: false,
  canStart: false, jobs: [], sources: [], segments: [], logs: '', logFilePath: '', captureDevices: [], liveStatus: '正在读取录音设备',
  recentModels: [],
}

function App() {
  const [page, setPage] = useState<Page>('batch')
  const [state, setState] = useState<AppState>(emptyState)
  const [error, setError] = useState('')
  const [previewTab, setPreviewTab] = useState<'segments' | 'logs'>('segments')
  const selectedCount = useMemo(() => state.jobs.filter(job => job.selected).length, [state.jobs])

  useEffect(() => {
    const unsubscribe = desktopBridge.subscribe(message => {
      if (message.type === 'state') {
        setState(message.payload as AppState)
        setError('')
      } else if (message.type === 'patch') {
        setState(current => ({ ...current, ...(message.payload as Partial<AppState>) }))
      } else if (message.type === 'jobUpdate') {
        const update = message.payload as Job
        setState(current => ({
          ...current,
          jobs: current.jobs.map(job => job.id === update.id ? update : job),
        }))
      } else if (message.type === 'segmentAdded') {
        const segment = message.payload as Segment
        setState(current => ({ ...current, segments: [...current.segments.slice(-4_999), segment] }))
      } else if (message.type === 'logAdded') {
        const { line } = message.payload as { line: string }
        setState(current => ({ ...current, logs: `${current.logs}${line}`.slice(-200_000) }))
      } else if (message.type === 'error') {
        setError((message.payload as { message: string }).message)
      }
    })
    desktopBridge.send('initialize')
    return unsubscribe
  }, [])

  const command = (action: string, payload?: unknown) => desktopBridge.send(action, payload)
  const updateSetting = (name: string, value: unknown) => command('updateSetting', { name, value })

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="brand"><span className="brand-mark">W</span><div><strong>Whisper</strong><small>Desktop Studio</small></div></div>
        <nav>
          <NavButton active={page === 'batch'} label="批量字幕" icon="▤" onClick={() => setPage('batch')} />
          <NavButton active={page === 'live'} label="实时字幕" icon="◉" onClick={() => setPage('live')} />
          <NavButton active={page === 'settings'} label="模型与设置" icon="⚙" onClick={() => setPage('settings')} />
        </nav>
        <div className="sidebar-status">
          <span className={state.modelProgress >= 1 ? 'status-dot ready' : 'status-dot'} />
          <div><small>当前模型</small><strong title={state.modelStatus}>{state.modelStatus}</strong></div>
        </div>
      </aside>

      <main className="workspace">
        {error && <div className="error-banner">{error}<button onClick={() => setError('')}>×</button></div>}
        {page === 'batch' && <BatchPage state={state} selectedCount={selectedCount} previewTab={previewTab}
          setPreviewTab={setPreviewTab} command={command} />}
        {page === 'live' && <LivePage state={state} command={command} updateSetting={updateSetting} />}
        {page === 'settings' && <SettingsPage state={state} command={command} updateSetting={updateSetting} />}
      </main>
    </div>
  )
}

function NavButton({ active, label, icon, onClick }: { active: boolean; label: string; icon: string; onClick(): void }) {
  return <button className={`nav-button ${active ? 'active' : ''}`} onClick={onClick}><span>{icon}</span>{label}</button>
}

type Command = (action: string, payload?: unknown) => void

function BatchPage({ state, selectedCount, previewTab, setPreviewTab, command }: {
  state: AppState; selectedCount: number; previewTab: 'segments' | 'logs';
  setPreviewTab(value: 'segments' | 'logs'): void; command: Command
}) {
  const updateSetting = (name: string, value: unknown) => command('updateSetting', { name, value })

  const queueColumns: TableColumn[] = [
    { id: 'select', name: '处理', width: 54, minWidth: 50, className: 'check-cell', align: 'center' },
    { id: 'fileName', name: '文件名', width: 150, minWidth: 80 },
    { id: 'relativePath', name: '相对路径', width: 250, minWidth: 100 },
    { id: 'status', name: '状态', width: 80, minWidth: 60 },
    { id: 'progress', name: '进度', minWidth: 80 }
  ]

  const renderQueueRow = (job: any) => (
    <tr key={job.id}>
      <td className="check-cell"><input type="checkbox" checked={job.selected} onChange={e => command('setJobSelected', { id: job.id, selected: e.target.checked })} /></td>
      <td><strong className="file-name">{job.fileName}</strong></td><td title={job.inputPath}>{job.relativePath}</td>
      <td><span className={`job-status ${job.status === '完成' ? 'done' : ''}`}>{job.status}</span></td>
      <td><div className="progress-track"><i style={{ width: `${Math.round(job.progress * 100)}%` }} /></div></td>
    </tr>
  )

  const previewColumns: TableColumn[] = [
    { id: 'index', name: '#', width: 35, minWidth: 30 },
    { id: 'file', name: '文件', width: 110, minWidth: 60 },
    { id: 'time', name: '时间戳', width: 150, minWidth: 80 },
    { id: 'text', name: '字幕内容', minWidth: 100 }
  ]

  const renderPreviewRow = (segment: any) => (
    <tr key={`${segment.fileName}-${segment.index}`}>
      <td>{segment.index}</td>
      <td>{segment.fileName}</td>
      <td className="timecode">{segment.begin} → {segment.end}</td>
      <td>{segment.text}</td>
    </tr>
  )

  return <div className="page batch-page">
    <header className="page-header">
      <div><p className="eyebrow">WORKSPACE</p><h1>批量字幕</h1><p>导入音频或视频，使用本地 GPU 批量生成字幕。</p></div>
      <div className="header-actions"><button className="button" onClick={() => command('addFiles')}>添加文件</button><button className="button primary" onClick={() => command('addFolder')}>添加文件夹</button></div>
    </header>

    <div className="batch-grid">
      <div className="batch-sidebar">
        <section className="panel source-panel">
          <PanelHeading title="来源目录" caption="取消勾选可排除整个目录" />
          <div className="source-list">
            {state.sources.length === 0 && <EmptyState text="添加文件夹后会显示来源目录" />}
            {state.sources.map(source => <label className="source-item" key={source.path}>
              <input type="checkbox" checked={source.selected} onChange={e => command('setSourceSelected', { path: source.path, selected: e.target.checked })} />
              <span className="folder-icon">▰</span><span className="source-copy"><strong>{source.name}</strong><small title={source.path}>{source.path}</small><em>{source.fileCount} 个文件</em></span>
            </label>)}
          </div>
        </section>

        <section className="panel output-panel">
          <PanelHeading title="输出设置" caption="配置输出格式与保存策略" />
          <Field label="输出格式"><Select options={state.formats} value={state.selectedFormat} onChange={value => updateSetting('format', value)} /></Field>
          <Field label="输出位置"><Select options={state.outputLocations} value={state.selectedOutputLocation} onChange={value => updateSetting('outputLocation', value)} /></Field>
          {state.selectedOutputLocation !== 'source' && (
            <Field label="输出目录"><div className="input-action"><input value={state.outputFolder} readOnly title={state.outputFolder} /><button className="button" onClick={() => command('chooseOutputFolder')}>选择</button></div></Field>
          )}
        </section>
      </div>

      <div className="batch-main">
        <section className="panel queue-panel">
          <div className="panel-toolbar"><PanelHeading title="任务队列" caption={`${state.jobs.length} 个文件，已选择 ${selectedCount} 个`} />
            <div className="text-actions"><button onClick={() => command('setAllSelected', true)}>全选</button><button onClick={() => command('setAllSelected', false)}>全不选</button><button onClick={() => command('removeSelectedRows')}>移除</button><button onClick={() => command('clearJobs')}>清空</button></div>
          </div>
          <ResizableTable
            columns={queueColumns}
            data={state.jobs}
            rowKey={job => job.id}
            renderRow={renderQueueRow}
            emptyText="还没有任务，先添加文件或文件夹"
            className="queue-table"
          />
        </section>

        <section className="panel preview-panel">
          <div className="tabs"><button className={previewTab === 'segments' ? 'active' : ''} onClick={() => setPreviewTab('segments')}>字幕预览</button><button className={previewTab === 'logs' ? 'active' : ''} onClick={() => setPreviewTab('logs')}>运行日志</button></div>
          {previewTab === 'segments' ? <ResizableTable
            columns={previewColumns}
            data={state.segments}
            rowKey={segment => `${segment.fileName}-${segment.index}`}
            renderRow={renderPreviewRow}
            emptyText="开始转录后，带时间戳的字幕会显示在这里"
            className="preview-table"
          />
          : <div className="log-panel"><div className="log-toolbar"><span title={state.logFilePath}>{state.logFilePath || '日志文件尚未创建'}</span><button onClick={() => command('openLogFolder')}>打开日志目录</button></div><pre className="log-view">{state.logs || '暂无运行日志'}</pre></div>}
        </section>
      </div>
    </div>

    <footer className="action-bar"><div><strong>{state.globalStatus}</strong><span>{state.modelProgress >= 1 ? '模型已就绪' : '请先在“模型与设置”中加载模型'}</span></div>
      <div><button className="button" disabled={!state.isRunning} onClick={() => command('stop')}>停止</button><button className="button primary start" disabled={!state.canStart} onClick={() => command('start')}>{state.isRunning ? '正在转录...' : '开始转录'}</button></div>
    </footer>
  </div>
}

function LivePage({ state, command, updateSetting }: { state: AppState; command: Command; updateSetting(name: string, value: unknown): void }) {
  return <div className="page narrow-page"><header className="page-header"><div><p className="eyebrow">LIVE CAPTURE</p><h1>实时字幕</h1><p>从麦克风采集语音，逐句生成带时间戳的字幕。</p></div></header>
    <div className="settings-grid">
      <section className="panel full"><div className="panel-toolbar"><PanelHeading title="录音设备" caption={state.liveStatus} /><button className="button" onClick={() => command('refreshDevices')}>刷新设备</button></div>
        <Field label="麦克风"><select value={state.selectedCaptureDevice ?? ''} onChange={e => updateSetting('captureDevice', e.target.value)}>{state.captureDevices.map(device => <option key={device.id} value={device.id}>{device.name}</option>)}</select></Field>
      </section>
      <section className="panel"><PanelHeading title="识别参数" caption="实时设置与批量任务共用" /><Field label="源语言"><Select options={state.languages} value={state.selectedLanguage} onChange={value => updateSetting('language', value)} /></Field></section>
      <section className="panel notice"><strong>流式 CUDA 接入中</strong><p>当前已完成设备发现和界面通信。下一步会把麦克风 PCM 分块送入 whisper.cpp CUDA 后端。</p></section>
      <section className="panel full live-canvas"><EmptyState text="开始录音后，实时字幕将在这里滚动显示" /></section>
    </div>
  </div>
}

function SettingsPage({ state, command, updateSetting }: { state: AppState; command: Command; updateSetting(name: string, value: unknown): void }) {
  return <div className="page narrow-page"><header className="page-header"><div><p className="eyebrow">CONFIGURATION</p><h1>模型与设置</h1><p>管理推理引擎、模型以及识别语言配置。</p></div></header>
    <div className="settings-grid">
      <section className="panel full"><PanelHeading title="推理模型" caption="推荐使用 CUDA 后端获得最高速度" />
        <Field label="推理引擎"><Select options={state.engines} value={state.selectedEngine} onChange={value => updateSetting('engine', value)} /></Field>
        <Field label="GGML 模型文件"><div className="input-action">
          <select style={{ flex: 1, minWidth: 0 }} value={state.modelPath} onChange={e => updateSetting('modelPath', e.target.value)}>
            {state.modelPath && !state.recentModels.includes(state.modelPath) && (
              <option value={state.modelPath}>{getFilename(state.modelPath)}</option>
            )}
            {state.recentModels.map(model => (
              <option key={model} value={model}>{getFilename(model)} (路径: {model})</option>
            ))}
            {state.recentModels.length === 0 && !state.modelPath && (
              <option value="">尚未选择模型，请点击右侧选择模型...</option>
            )}
          </select>
          <button className="button" onClick={() => command('chooseModel')}>选择模型</button>
        </div></Field>
        <div className="load-row"><div><strong>{state.modelStatus}</strong><div className="progress-track large"><i style={{ width: `${Math.round(state.modelProgress * 100)}%` }} /></div></div><button className="button primary" disabled={!state.modelPath || state.isLoadingModel} onClick={() => command('loadModel')}>{state.isLoadingModel ? '加载中...' : '加载模型'}</button></div>
      </section>
      <section className="panel full"><PanelHeading title="识别与翻译" caption="设置输入音频的实际语言" /><Field label="源语言"><Select options={state.languages} value={state.selectedLanguage} onChange={value => updateSetting('language', value)} /></Field>
        <label className="toggle-row"><input type="checkbox" checked={state.translate} disabled={state.selectedLanguage === 'en'} onChange={e => updateSetting('translate', e.target.checked)} /><span><strong>翻译为英文</strong><small>Whisper 原生翻译任务仅输出英文</small></span></label>
      </section>
    </div>
  </div>
}

function PanelHeading({ title, caption }: { title: string; caption?: string }) { return <div className="panel-heading"><h2>{title}</h2>{caption && <p>{caption}</p>}</div> }
function EmptyState({ text }: { text: string }) { return <div className="empty-state"><span>＋</span><p>{text}</p></div> }
function Field({ label, children }: { label: string; children: React.ReactNode }) { return <label className="field"><span>{label}</span>{children}</label> }
function Select({ options, value, onChange }: { options: { id: string; name: string }[]; value: string; onChange(value: string): void }) { return <select value={value} onChange={e => onChange(e.target.value)}>{options.map(option => <option key={option.id} value={option.id}>{option.name}</option>)}</select> }

export default App
