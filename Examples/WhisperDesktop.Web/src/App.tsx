import { useEffect, useMemo, useRef, useState } from 'react'
import { desktopBridge } from './bridge'
import type { AppState, Job, LiveSegment, Page, Segment } from './types'
import { ResizableTable, TableColumn } from './ResizableTable'

const getFilename = (path: string) => {
  if (!path) return ''
  const parts = path.split(/[\\/]/)
  return parts[parts.length - 1]
}

const emptyState: AppState = {
  engines: [], languages: [], formats: [], outputLocations: [], selectedEngine: '', selectedLanguage: '',
  selectedFormat: '', selectedOutputLocation: '', translate: false, modelPath: '', modelStatus: '正在连接桌面宿主',
  modelProgress: 0, modelProgressIndeterminate: false, modelLoadElapsedSeconds: 0, autoLoadModel: true,
  outputFolder: '', globalStatus: '正在初始化...', isRunning: false, isLoadingModel: false,
  isScanningMedia: false,
  batchStatistics: { selectedCount: 0, knownDurationCount: 0, unknownDurationCount: 0, totalDurationSeconds: 0, processedDurationSeconds: 0, elapsedSeconds: 0, speed: 0, etaIsPartial: false, isEstimating: false },
  canStart: false, jobs: [], sources: [], segments: [], logs: '', logFilePath: '', captureDevices: [], liveStatus: '正在读取录音设备',
  canStartLive: false, isLiveRunning: false, isLivePreparing: false, liveVoiceDetected: false,
  liveTranscribing: false, liveStalled: false, liveModelProgress: 0, liveElapsedSeconds: 0, liveSegments: [],
  recentModels: [],
}

function App() {
  const [page, setPage] = useState<Page>('batch')
  const [state, setState] = useState<AppState>(emptyState)
  const [error, setError] = useState('')
  const [previewTab, setPreviewTab] = useState<'segments' | 'logs'>('segments')
  const pendingSegments = useRef<Segment[]>([])
  const selectedCount = useMemo(() => state.jobs.filter(job => job.selected).length, [state.jobs])

  useEffect(() => {
    const flushSegments = () => {
      if (pendingSegments.current.length === 0) return
      const batch = pendingSegments.current
      pendingSegments.current = []
      setState(current => ({ ...current, segments: [...current.segments, ...batch].slice(-5_000) }))
    }
    const previewTimer = window.setInterval(flushSegments, 1_000)
    const unsubscribe = desktopBridge.subscribe(message => {
      if (message.type === 'state') {
        setState(message.payload as AppState)
        setError('')
      } else if (message.type === 'patch') {
        const patch = message.payload as Partial<AppState>
        if (Array.isArray(patch.segments) && patch.segments.length === 0) {
          pendingSegments.current = []
        }
        if (patch.isRunning === false && pendingSegments.current.length > 0) {
          const batch = pendingSegments.current
          pendingSegments.current = []
          setState(current => ({ ...current, ...patch, segments: [...current.segments, ...batch].slice(-5_000) }))
        } else {
          setState(current => ({ ...current, ...patch }))
        }
      } else if (message.type === 'jobUpdate') {
        const update = message.payload as Job
        setState(current => ({
          ...current,
          jobs: current.jobs.map(job => job.id === update.id ? update : job),
        }))
      } else if (message.type === 'segmentAdded') {
        pendingSegments.current.push(message.payload as Segment)
      } else if (message.type === 'liveSegmentAdded') {
        const segment = message.payload as LiveSegment
        setState(current => ({ ...current, liveSegments: [...current.liveSegments.slice(-4_999), segment] }))
      } else if (message.type === 'logAdded') {
        const { line } = message.payload as { line: string }
        setState(current => ({ ...current, logs: `${current.logs}${line}`.slice(-200_000) }))
      } else if (message.type === 'error') {
        setError((message.payload as { message: string }).message)
      }
    })
    desktopBridge.send('initialize')
    return () => {
      window.clearInterval(previewTimer)
      unsubscribe()
    }
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
  const formatDuration = (seconds?: number) => {
    if (seconds == null || !Number.isFinite(seconds)) return '--:--'
    const rounded = Math.max(0, Math.round(seconds))
    const hours = Math.floor(rounded / 3600)
    const minutes = Math.floor((rounded % 3600) / 60)
    const secs = rounded % 60
    return hours > 0
      ? `${String(hours).padStart(2, '0')}:${String(minutes).padStart(2, '0')}:${String(secs).padStart(2, '0')}`
      : `${String(minutes).padStart(2, '0')}:${String(secs).padStart(2, '0')}`
  }
  const completionTime = state.batchStatistics.estimatedCompletionTime
    ? new Date(state.batchStatistics.estimatedCompletionTime).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
    : ''
  const estimateText = state.batchStatistics.isEstimating
    ? '正在估算完成时间...'
    : state.batchStatistics.etaSeconds != null
      ? `预计 ${completionTime} 完成 · 剩余 ${formatDuration(state.batchStatistics.etaSeconds)}`
      : '预计完成时间 --'
  const completedCount = state.jobs.filter(job => job.selected && job.status === '完成').length
  const formatCompactDuration = (seconds: number) => {
    if (!Number.isFinite(seconds) || seconds <= 0) return '0 分钟'
    if (seconds < 3600) return `${Math.max(1, Math.round(seconds / 60))} 分钟`
    return `${(seconds / 3600).toFixed(seconds >= 36_000 ? 0 : 1)} 小时`
  }

  const queueColumns: TableColumn[] = [
    { id: 'select', name: '处理', width: 54, minWidth: 50, className: 'check-cell', align: 'center' },
    { id: 'fileName', name: '文件名', width: 150, minWidth: 80 },
    { id: 'relativePath', name: '相对路径', width: 250, minWidth: 100 },
    { id: 'duration', name: '时长', width: 78, minWidth: 66 },
    { id: 'status', name: '状态', width: 80, minWidth: 60 },
    { id: 'progress', name: '进度', minWidth: 80 }
  ]

  const renderQueueRow = (job: any) => (
    <tr key={job.id}>
      <td className="check-cell"><input type="checkbox" checked={job.selected} onChange={e => command('setJobSelected', { id: job.id, selected: e.target.checked })} /></td>
      <td><strong className="file-name">{job.fileName}</strong></td><td title={job.inputPath}>{job.relativePath}</td>
      <td className="timecode">{formatDuration(job.durationSeconds)}</td>
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
      <div className="header-actions"><button className="button" disabled={state.isScanningMedia} onClick={() => command('addFiles')}>添加文件</button><button className="button primary" disabled={state.isScanningMedia} onClick={() => command('addFolder')}>{state.isScanningMedia ? '正在读取时长...' : '添加文件夹'}</button></div>
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
          <div className="panel-toolbar"><PanelHeading title="任务队列" caption={`已选 ${selectedCount} · 总时长 ${formatCompactDuration(state.batchStatistics.totalDurationSeconds)} · 已完成 ${completedCount}/${selectedCount}`} />
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

    <footer className="action-bar"><div className="batch-summary"><strong>{state.globalStatus}</strong><span>
      总时长 {formatDuration(state.batchStatistics.totalDurationSeconds)}
      {' · '}已处理 {formatDuration(state.batchStatistics.processedDurationSeconds)}
      {' · '}已用时 {formatDuration(state.batchStatistics.elapsedSeconds)}
      {' · '}速度 {state.batchStatistics.speed > 0 ? `${state.batchStatistics.speed.toFixed(1)}x` : '--'}
      {' · '}{estimateText}
      {state.batchStatistics.unknownDurationCount > 0 && ` · ${state.batchStatistics.unknownDurationCount} 个未知时长文件未计入`}
    </span></div>
      <div><button className="button" disabled={!state.isRunning} onClick={() => command('stop')}>停止</button><button className="button primary start" disabled={!state.canStart} onClick={() => command('start')}>{state.isRunning ? '正在转录...' : '开始转录'}</button></div>
    </footer>
  </div>
}

function LivePage({ state, command, updateSetting }: { state: AppState; command: Command; updateSetting(name: string, value: unknown): void }) {
  const listRef = useRef<HTMLDivElement>(null)
  const formatDuration = (seconds: number) => {
    const value = Math.max(0, Math.floor(seconds || 0))
    const hours = Math.floor(value / 3600)
    const minutes = Math.floor((value % 3600) / 60)
    const secs = value % 60
    return `${String(hours).padStart(2, '0')}:${String(minutes).padStart(2, '0')}:${String(secs).padStart(2, '0')}`
  }

  useEffect(() => {
    listRef.current?.scrollTo({ top: listRef.current.scrollHeight, behavior: 'smooth' })
  }, [state.liveSegments.length])

  const copyAll = async () => {
    const text = state.liveSegments.map(segment => `[${segment.begin}] ${segment.text}`).join('\n')
    if (text) await navigator.clipboard.writeText(text)
  }

  const statusClass = state.liveStalled ? 'warning'
    : state.liveTranscribing ? 'transcribing'
      : state.liveVoiceDetected ? 'voice' : state.isLiveRunning ? 'listening' : 'idle'

  return <div className="page live-page">
    <header className="page-header"><div><p className="eyebrow">LIVE CAPTURE</p><h1>实时字幕</h1><p>从麦克风采集语音，在自然停顿后生成确认字幕。</p></div>
      <div className={`live-badge ${statusClass}`}><i />{state.liveStatus}</div>
    </header>

    <div className="live-controls">
      <section className="panel live-device-panel">
        <div className="panel-toolbar"><PanelHeading title="录音设备" caption={`${state.captureDevices.length} 个可用设备`} />
          <button className="button" disabled={state.isLiveRunning || state.isLivePreparing} onClick={() => command('refreshDevices')}>刷新设备</button></div>
        <Field label="麦克风"><select disabled={state.isLiveRunning || state.isLivePreparing} value={state.selectedCaptureDevice ?? ''} onChange={e => updateSetting('captureDevice', e.target.value)}>
          {state.captureDevices.length === 0 && <option value="">未发现可用麦克风</option>}
          {state.captureDevices.map(device => <option key={device.id} value={device.id}>{device.name}</option>)}
        </select></Field>
        <div className="voice-meter"><span>语音活动</span><div><i className={state.liveVoiceDetected ? 'active' : ''} /></div><strong>{state.liveVoiceDetected ? '检测到说话' : '等待人声'}</strong></div>
      </section>

      <section className="panel live-settings-panel">
        <PanelHeading title="识别参数" caption="第一版采用 VAD 均衡分句模式" />
        <Field label="源语言"><Select disabled={state.isLiveRunning || state.isLivePreparing} options={state.languages} value={state.selectedLanguage} onChange={value => updateSetting('language', value)} /></Field>
        <div className="live-mode-row"><span>分句模式</span><strong>均衡</strong><small>停顿约 0.6 秒后提交，最长约 8 秒</small></div>
      </section>
    </div>

    <section className="panel live-session-panel">
      <div className="live-session-stats">
        <div><small>会话时长</small><strong>{formatDuration(state.liveElapsedSeconds)}</strong></div>
        <div><small>确认字幕</small><strong>{state.liveSegments.length} 条</strong></div>
        <div><small>识别状态</small><strong>{state.liveTranscribing ? '识别中' : state.liveVoiceDetected ? '收集中' : state.isLiveRunning ? '聆听中' : '未开始'}</strong></div>
        <div><small>实时引擎</small><strong>兼容流式引擎</strong></div>
      </div>
      {state.isLivePreparing && <div className="live-preparing"><span>正在载入实时识别模型</span><div className="progress-track large"><i style={{ width: `${Math.round(state.liveModelProgress * 100)}%` }} /></div></div>}

      <div className="live-current">
        <small>当前状态</small>
        <strong>{state.liveStalled ? '处理速度不足，请等待模型跟上音频' : state.liveVoiceDetected ? '正在收集当前语句...' : state.isLiveRunning ? '请开始说话' : '点击开始后，字幕会显示在下方'}</strong>
      </div>

      <div className="live-transcript" ref={listRef}>
        {state.liveSegments.length === 0 && <EmptyState text="开始说话后，确认字幕会在这里滚动显示" />}
        {state.liveSegments.map(segment => <article className="live-segment" key={segment.index}>
          <time>{segment.begin} → {segment.end}</time><p>{segment.text}</p>
        </article>)}
      </div>
    </section>

    <footer className="action-bar live-action-bar"><div className="batch-summary"><strong>{state.liveStatus}</strong><span title={state.liveOutputPath}>{state.liveOutputPath ? `已导出：${state.liveOutputPath}` : '实时字幕使用 VAD 跳过静音，并在自然停顿后确认文本'}</span></div>
      <div className="live-export-actions"><button className="button" disabled={state.liveSegments.length === 0} onClick={() => command('clearLiveSegments')}>清空</button>
        <button className="button" disabled={state.liveSegments.length === 0} onClick={copyAll}>复制全部</button>
        <button className="button" disabled={state.liveSegments.length === 0} onClick={() => command('exportLive', { format: 'txt' })}>导出 TXT</button>
        <button className="button" disabled={state.liveSegments.length === 0} onClick={() => command('exportLive', { format: 'srt' })}>导出 SRT</button>
        {state.isLiveRunning
          ? <button className="button danger" onClick={() => command('stopLive')}>停止</button>
          : <button className="button primary start" disabled={!state.canStartLive} onClick={() => command('startLive')}>开始实时字幕</button>}
      </div></footer>
  </div>
}

function SettingsPage({ state, command, updateSetting }: { state: AppState; command: Command; updateSetting(name: string, value: unknown): void }) {
  return <div className="page narrow-page"><header className="page-header"><div><p className="eyebrow">CONFIGURATION</p><h1>模型与设置</h1><p>管理推理引擎、模型以及识别语言配置。</p></div></header>
    <div className="settings-grid">
      <section className="panel full"><PanelHeading title="推理模型" caption="推荐使用 CUDA 后端获得最高速度" />
        <Field label="推理引擎"><Select disabled={state.isRunning || state.isLoadingModel || state.isLiveRunning || state.isLivePreparing} options={state.engines} value={state.selectedEngine} onChange={value => updateSetting('engine', value)} /></Field>
        <Field label="GGML 模型文件"><div className="input-action">
          <select disabled={state.isRunning || state.isLoadingModel || state.isLiveRunning || state.isLivePreparing} style={{ flex: 1, minWidth: 0 }} value={state.modelPath} onChange={e => updateSetting('modelPath', e.target.value)}>
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
          <button className="button" disabled={state.isRunning || state.isLoadingModel || state.isLiveRunning || state.isLivePreparing} onClick={() => command('chooseModel')}>选择模型</button>
        </div></Field>
        <div className="load-row"><div><strong>{state.modelStatus}</strong>{state.isLoadingModel && <small>已用时 {Math.floor(state.modelLoadElapsedSeconds)} 秒</small>}<div className={`progress-track large ${state.isLoadingModel && state.modelProgressIndeterminate ? 'indeterminate' : ''}`}><i style={{ width: `${Math.round(state.modelProgress * 100)}%` }} /></div></div><button className={`button ${state.isLoadingModel ? 'danger' : 'primary'}`} disabled={!state.isLoadingModel && (!state.modelPath || state.isRunning || state.isLiveRunning || state.isLivePreparing)} onClick={() => command(state.isLoadingModel ? 'cancelModelLoad' : 'loadModel')}>{state.isLoadingModel ? '取消加载' : '加载模型'}</button></div>
        <label className="toggle-row"><input type="checkbox" checked={state.autoLoadModel} disabled={state.isLoadingModel || state.isRunning || state.isLiveRunning || state.isLivePreparing} onChange={e => updateSetting('autoLoadModel', e.target.checked)} /><span><strong>启动时自动加载上次模型</strong><small>模型路径有效时在界面就绪后后台加载，不阻塞窗口响应</small></span></label>
      </section>
      <section className="panel full"><PanelHeading title="识别与翻译" caption="设置输入音频的实际语言" /><Field label="源语言"><Select disabled={state.isRunning || state.isLiveRunning || state.isLivePreparing} options={state.languages} value={state.selectedLanguage} onChange={value => updateSetting('language', value)} /></Field>
        <label className="toggle-row"><input type="checkbox" checked={state.translate} disabled={state.selectedLanguage === 'en' || state.isRunning || state.isLiveRunning || state.isLivePreparing} onChange={e => updateSetting('translate', e.target.checked)} /><span><strong>翻译为英文</strong><small>Whisper 原生翻译任务仅输出英文</small></span></label>
      </section>
    </div>
  </div>
}

function PanelHeading({ title, caption }: { title: string; caption?: string }) { return <div className="panel-heading"><h2>{title}</h2>{caption && <p>{caption}</p>}</div> }
function EmptyState({ text }: { text: string }) { return <div className="empty-state"><span>＋</span><p>{text}</p></div> }
function Field({ label, children }: { label: string; children: React.ReactNode }) { return <label className="field"><span>{label}</span>{children}</label> }
function Select({ options, value, disabled, onChange }: { options: { id: string; name: string }[]; value: string; disabled?: boolean; onChange(value: string): void }) { return <select disabled={disabled} value={value} onChange={e => onChange(e.target.value)}>{options.map(option => <option key={option.id} value={option.id}>{option.name}</option>)}</select> }

export default App
