import { useEffect, useMemo, useRef, useState } from 'react'
import { desktopBridge } from './bridge'
import type { AppState, Job, LiveSegment, Page, Segment } from './types'
import { ResizableTable, TableColumn } from './ResizableTable'

const uiScaleOptions = [
  { id: 'small', name: '小' },
  { id: 'medium', name: '中' },
  { id: 'large', name: '大' },
]

const defaultGeminiSample = '嗯这个地方我们可以把这个粒子拖尾调得更自然一点。'
const aiOutputPolicyDescriptions: Record<string, string> = {
  overwriteBackup: '普通模式会覆盖同名 .srt，并在旁边保留 original 备份。',
  preserve: '保留原 .srt，另存为 .optimized.srt，适合诊断和人工对比。',
}

const getFilename = (path: string) => {
  if (!path) return ''
  const parts = path.split(/[\\/]/)
  return parts[parts.length - 1]
}

const emptyState: AppState = {
  engines: [], languages: [], formats: [], outputLocations: [], aiModelProviders: [], geminiModels: [], aiSubtitleOutputPolicies: [], selectedEngine: '', selectedLanguage: '',
  selectedFormat: '', selectedOutputLocation: '', selectedAiModelProvider: 'gemini', selectedGeminiModel: 'gemini-3.1-flash-lite',
  selectedLocalAiModel: 'qwen3:8b', localAiBaseUrl: 'http://localhost:11434/v1/', selectedAiSubtitleOutputPolicy: 'overwriteBackup',
  effectiveAiSubtitleOutputPolicy: 'overwriteBackup',
  geminiApiKeyConfigured: false, isAiModelConfigured: false, geminiStatus: '未配置 API Key', isGeminiBusy: false, geminiSampleResult: '',
  translate: false, modelPath: '', modelStatus: '正在连接桌面宿主',
  buildTime: '',
  modelProgress: 0, modelProgressIndeterminate: false, modelLoadElapsedSeconds: 0, autoLoadModel: true,
  uiScale: 'medium',
  terminologyEnabled: false, developerDiagnostics: false, terminologyDirectory: '', terminologyPacks: [],
  outputFolder: '', globalStatus: '正在初始化...', isRunning: false, isLoadingModel: false,
  isScanningMedia: false,
  canStartAiSubtitleOptimization: false, isAiRunning: false, aiBatchStatus: '等待字幕优化任务', aiBatchReportPath: '', aiBatchSummary: '',
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
  const previousPage = useRef<Page>('batch')
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
  const openAbout = () => {
    setPage(current => {
      if (current === 'about') return previousPage.current === 'about' ? 'batch' : previousPage.current
      previousPage.current = current
      return 'about'
    })
  }
  const closeAbout = () => setPage(previousPage.current === 'about' ? 'batch' : previousPage.current)

  return (
    <div className={`app-shell ui-font-${state.uiScale || 'medium'}`}>
      <aside className="sidebar">
        <button className="brand" type="button" onClick={openAbout} aria-label="打开关于页面" title="关于 Whisper Desktop">
          <span className="brand-mark">W</span><span><strong>Whisper</strong><small>Desktop Studio</small></span>
        </button>
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
        {page === 'about' && <AboutPage state={state} onBack={closeAbout} />}
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
  const skippedCount = state.jobs.filter(job => job.status === '已跳过').length
  const aiActionLabel = state.isAiRunning
    ? '正在 AI 优化...'
    : state.developerDiagnostics ? 'AI 优化并评分' : 'AI 优化'
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
      <td><span className={`job-status ${job.status === '完成' ? 'done' : job.status === '已跳过' ? 'skipped' : ''}`}>{job.status}</span></td>
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
          <div className="panel-toolbar"><PanelHeading title="任务队列" caption={`已选 ${selectedCount} · 总时长 ${formatCompactDuration(state.batchStatistics.totalDurationSeconds)} · 已完成 ${completedCount}/${selectedCount} · 已跳过 ${skippedCount}`} />
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
      {` · AI ${state.aiBatchStatus}`}
      {state.aiBatchSummary && ` · ${state.aiBatchSummary}`}
    </span></div>
      <div className="batch-actions">
        <button className="button" disabled={!state.isRunning && !state.isAiRunning} onClick={() => command(state.isAiRunning ? 'stopAiSubtitleOptimization' : 'stop')}>停止</button>
        <button className="button" disabled={!state.canStartAiSubtitleOptimization && !state.isAiRunning} onClick={() => command(state.isAiRunning ? 'stopAiSubtitleOptimization' : 'startAiSubtitleOptimization')}>{aiActionLabel}</button>
        <button className="button primary start" disabled={!state.canStart} onClick={() => command('start')}>{state.isRunning ? '正在转录...' : '开始转录'}</button>
      </div>
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

function AboutPage({ state, onBack }: { state: AppState; onBack(): void }) {
  const selectedEngine = state.engines.find(engine => engine.id === state.selectedEngine)?.name || state.selectedEngine || '未选择'
  const selectedLanguage = state.languages.find(language => language.id === state.selectedLanguage)?.name || state.selectedLanguage || '未选择'
  const activeTerminologyPacks = state.terminologyPacks.filter(pack => pack.selected).map(pack => pack.name).join('、') || '未启用'
  const rows = [
    ['运行方式', '本地 Whisper / whisper.cpp / WebView2 / React'],
    ['编译时间', state.buildTime || '未知'],
    ['推理引擎', selectedEngine],
    ['识别语言', selectedLanguage],
    ['当前模型', state.modelStatus || '尚未加载'],
    ['默认输出目录', state.outputFolder || '未设置'],
    ['日志文件', state.logFilePath || '尚未创建'],
    ['术语词库目录', state.terminologyDirectory || '未初始化'],
    ['已选术语词库', activeTerminologyPacks],
  ]

  return <div className="page narrow-page about-page">
    <header className="page-header about-header">
      <div><p className="eyebrow">ABOUT</p><h1>Whisper Desktop</h1><p>面向批量字幕与实时字幕的本地转写工作台。</p></div>
      <button className="button" onClick={onBack}>返回</button>
    </header>

    <section className="about-hero panel">
      <span className="about-mark">W</span>
      <div>
        <h2>Whisper Desktop Studio</h2>
        <p>把模型、任务队列、日志与字幕预览集中在一个桌面界面里，适合剪辑、教程和长素材转写流程。</p>
      </div>
    </section>

    <section className="panel about-section">
      <PanelHeading title="当前环境" caption="这些信息来自当前宿主状态，便于排查模型、输出和词库配置" />
      <div className="about-grid">
        {rows.map(([label, value]) => <div className="about-row" key={label}><span>{label}</span><strong title={value}>{value}</strong></div>)}
      </div>
    </section>

    <section className="panel about-section">
      <PanelHeading title="隐私与组件" caption="默认工作流在本机完成，后续接入在线 AI 前会明确提供开关" />
      <p className="about-note">批量与实时转写默认在本机运行；当前界面的模型加载、字幕生成、日志记录和术语词库读取都由桌面宿主协调。在线 AI 优化功能接入前，不会自动上传字幕内容。</p>
    </section>
  </div>
}

function SettingsPage({ state, command, updateSetting }: { state: AppState; command: Command; updateSetting(name: string, value: unknown): void }) {
  const [apiKeyInput, setApiKeyInput] = useState('')
  const [sampleText, setSampleText] = useState(defaultGeminiSample)
  const [localBaseUrlInput, setLocalBaseUrlInput] = useState(state.localAiBaseUrl || 'http://localhost:11434/v1/')
  const [localModelInput, setLocalModelInput] = useState(state.selectedLocalAiModel || 'qwen3:8b')
  const isLocalAi = state.selectedAiModelProvider === 'localOpenAi'
  const canUseAi = state.isAiModelConfigured && !state.isGeminiBusy

  useEffect(() => setLocalBaseUrlInput(state.localAiBaseUrl || 'http://localhost:11434/v1/'), [state.localAiBaseUrl])
  useEffect(() => setLocalModelInput(state.selectedLocalAiModel || 'qwen3:8b'), [state.selectedLocalAiModel])

  return <div className="page narrow-page"><header className="page-header"><div><p className="eyebrow">CONFIGURATION</p><h1>模型与设置</h1><p>管理推理引擎、模型以及识别语言配置。</p></div></header>
    <div className="settings-grid">
      <section className="panel"><PanelHeading title="界面显示" caption="根据屏幕尺寸调整文字可读性" />
        <Field label="界面字号"><Select options={uiScaleOptions} value={state.uiScale || 'medium'} onChange={value => updateSetting('uiScale', value)} /></Field>
      </section>
      <section className="panel"><PanelHeading title="识别与翻译" caption="设置输入音频的实际语言" /><Field label="源语言"><Select disabled={state.isRunning || state.isLiveRunning || state.isLivePreparing} options={state.languages} value={state.selectedLanguage} onChange={value => updateSetting('language', value)} /></Field>
        <label className="toggle-row"><input type="checkbox" checked={state.translate} disabled={state.selectedLanguage === 'en' || state.isRunning || state.isLiveRunning || state.isLivePreparing} onChange={e => updateSetting('translate', e.target.checked)} /><span><strong>翻译为英文</strong><small>Whisper 原生翻译任务仅输出英文</small></span></label>
      </section>
      <section className="panel ai-panel"><PanelHeading title="AI 字幕模型" caption="用于字幕优化和质量评分" />
        <Field label="AI Provider"><Select disabled={state.isGeminiBusy || state.isAiRunning} options={state.aiModelProviders} value={state.selectedAiModelProvider || 'gemini'} onChange={value => updateSetting('aiModelProvider', value)} /></Field>
        {isLocalAi ? <>
          <Field label="Base URL"><input value={localBaseUrlInput} placeholder="http://localhost:11434/v1/" onChange={e => setLocalBaseUrlInput(e.target.value)} onBlur={() => updateSetting('localAiBaseUrl', localBaseUrlInput)} /></Field>
          <Field label="模型名"><input value={localModelInput} placeholder="qwen3:8b" onChange={e => setLocalModelInput(e.target.value)} onBlur={() => updateSetting('localAiModel', localModelInput)} /></Field>
          <p className="field-note">Ollama 默认 OpenAI-compatible 地址是 http://localhost:11434/v1/，模型名填写本机已 pull 的 tag。</p>
        </> : <>
          <Field label="AI 模型"><Select disabled={state.isGeminiBusy} options={state.geminiModels} value={state.selectedGeminiModel || 'gemini-3.1-flash-lite'} onChange={value => updateSetting('geminiModel', value)} /></Field>
          <Field label={state.geminiApiKeyConfigured ? '替换 API Key' : 'API Key'}><div className="input-action">
            <input type="password" value={apiKeyInput} placeholder={state.geminiApiKeyConfigured ? '已配置，可输入新密钥替换' : '粘贴 Google AI Studio API Key'} onChange={e => setApiKeyInput(e.target.value)} />
            <button className="button" disabled={state.isGeminiBusy || !apiKeyInput.trim()} onClick={() => { command('saveGeminiApiKey', { apiKey: apiKeyInput }); setApiKeyInput('') }}>保存</button>
          </div></Field>
        </>}
        <Field label="AI 输出"><Select disabled={state.isAiRunning || state.isRunning} options={state.aiSubtitleOutputPolicies} value={state.selectedAiSubtitleOutputPolicy || 'overwriteBackup'} onChange={value => updateSetting('aiSubtitleOutputPolicy', value)} /></Field>
        <p className="field-note">{state.developerDiagnostics
          ? '开发者诊断已开启，本次会强制保留原字幕并输出 .optimized.srt。'
          : aiOutputPolicyDescriptions[state.effectiveAiSubtitleOutputPolicy] || aiOutputPolicyDescriptions.overwriteBackup}</p>
        <div className="ai-actions">
          <button className="button" disabled={!canUseAi} onClick={() => command('testAiModelConnection')}>测试连接</button>
          {!isLocalAi && <button className="button" disabled={state.isGeminiBusy || !state.geminiApiKeyConfigured} onClick={() => command('clearGeminiApiKey')}>清除密钥</button>}
        </div>
        <p className={`ai-status ${state.isAiModelConfigured ? 'ready' : ''}`}>{state.isGeminiBusy ? '处理中...' : state.geminiStatus}</p>
      </section>
      <section className="panel ai-panel"><PanelHeading title="优化试跑" caption="先验证一条字幕的润色效果" />
        <textarea className="sample-textarea" value={sampleText} onChange={e => setSampleText(e.target.value)} />
        <div className="ai-actions">
          <button className="button primary" disabled={!canUseAi || !sampleText.trim()} onClick={() => command('optimizeAiSample', { text: sampleText })}>优化试跑</button>
        </div>
        <div className="sample-result">{state.geminiSampleResult || '配置 AI Provider 后，可在这里试跑一条字幕。'}</div>
      </section>
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
      <section className="panel full"><div className="panel-toolbar"><PanelHeading title="术语词库" caption={state.terminologyDirectory || '词库目录尚未初始化'} />
        <div className="text-actions"><button onClick={() => command('refreshTerminology')}>刷新</button><button onClick={() => command('openTerminologyFolder')}>打开目录</button></div>
      </div>
        <label className="toggle-row"><input type="checkbox" checked={state.terminologyEnabled} disabled={state.isRunning || state.isLiveRunning || state.isLivePreparing} onChange={e => updateSetting('terminologyEnabled', e.target.checked)} /><span><strong>启用术语词库</strong><small>转写前注入高优先级术语，转写后按配置做安全纠错</small></span></label>
        <div className="terminology-list">
          {state.terminologyPacks.length === 0 && <EmptyState text="打开目录后添加 JSON 词库文件" />}
          {state.terminologyPacks.map(pack => <label className="terminology-item" key={pack.id}>
            <input type="checkbox" checked={pack.selected} disabled={state.isRunning || state.isLiveRunning || state.isLivePreparing} onChange={e => command('setTerminologyPackSelected', { id: pack.id, selected: e.target.checked })} />
            <span><strong>{pack.name}</strong><small title={pack.filePath || pack.id}>{pack.description || pack.id}</small>
              <span className="terminology-terms">{pack.terms.slice(0, 24).map(term => <b key={`${pack.id}-${term.text}`} title={`${term.category || '术语'}${term.corrections?.length ? ` · 纠错：${term.corrections.join(', ')}` : ''}`}>{term.text}</b>)}</span>
            </span>
            <em>{pack.termCount} 词</em>
          </label>)}
        </div>
      </section>
      <section className="panel diagnostics-panel"><PanelHeading title="开发者诊断" caption="预留给字幕质量排查" />
        <label className="toggle-row"><input type="checkbox" checked={state.developerDiagnostics} disabled={state.isRunning || state.isLiveRunning || state.isLivePreparing} onChange={e => updateSetting('developerDiagnostics', e.target.checked)} /><span><strong>启用开发者诊断模式</strong><small>当前版本只保存开关并标记诊断意图；接入 AI 后再生成评分、规则命中和质量报告。</small></span></label>
      </section>
      <section className="panel diagnostics-panel"><PanelHeading title="诊断输出" caption="后续质量报告预留位" />
        <div className="diagnostics-preview diagnostics-preview-compact">
          <div><span>报告</span><strong>批次摘要</strong></div>
          <div><span>评分</span><strong>单文件质量</strong></div>
          <div><span>对比</span><strong>优化前后</strong></div>
        </div>
      </section>
    </div>
  </div>
}

function PanelHeading({ title, caption }: { title: string; caption?: string }) { return <div className="panel-heading"><h2>{title}</h2>{caption && <p>{caption}</p>}</div> }
function EmptyState({ text }: { text: string }) { return <div className="empty-state"><span>＋</span><p>{text}</p></div> }
function Field({ label, children }: { label: string; children: React.ReactNode }) { return <label className="field"><span>{label}</span>{children}</label> }
function Select({ options, value, disabled, onChange }: { options: { id: string; name: string }[]; value: string; disabled?: boolean; onChange(value: string): void }) { return <select disabled={disabled} value={value} onChange={e => onChange(e.target.value)}>{options.map(option => <option key={option.id} value={option.id}>{option.name}</option>)}</select> }

export default App
