import type { AppState, DesktopCommand, HostMessage } from './types'

declare global {
  interface Window {
    chrome?: {
      webview?: {
        postMessage(message: DesktopCommand): void
        addEventListener(type: 'message', listener: (event: MessageEvent<HostMessage>) => void): void
        removeEventListener(type: 'message', listener: (event: MessageEvent<HostMessage>) => void): void
      }
    }
  }
}

export interface DesktopBridge {
  send(action: string, payload?: unknown): void
  subscribe(listener: (message: HostMessage) => void): () => void
}

class WebView2Bridge implements DesktopBridge {
  send(action: string, payload?: unknown) {
    window.chrome?.webview?.postMessage({ action, payload })
  }

  subscribe(listener: (message: HostMessage) => void) {
    const webview = window.chrome?.webview
    if (!webview) return () => undefined
    const handler = (event: MessageEvent<HostMessage>) => listener(event.data)
    webview.addEventListener('message', handler)
    return () => webview.removeEventListener('message', handler)
  }
}

const mockState: AppState = {
  engines: [
    { id: 'cuda', name: 'whisper.cpp 1.9.1（CUDA / NVIDIA，推荐）' },
    { id: 'cpu', name: 'whisper.cpp 1.9.1（CPU，兼容）' },
    { id: 'd3d11', name: 'D3D11（兼容版）' },
  ],
  languages: [
    { id: 'zh', name: '中文' }, { id: 'yue', name: '粤语' }, { id: 'en', name: '英语' },
    { id: 'ja', name: '日语' }, { id: 'ko', name: '韩语' },
  ],
  formats: [
    { id: 'srt', name: 'SubRip 字幕 (.srt)' }, { id: 'vtt', name: 'WebVTT 字幕 (.vtt)' },
    { id: 'txt', name: '纯文本 (.txt)' }, { id: 'timestamps', name: '带时间戳文本 (.txt)' },
  ],
  outputLocations: [
    { id: 'selected', name: '统一输出目录', description: '全部字幕写入指定目录' },
    { id: 'source', name: '原文件旁', description: '字幕写入媒体文件所在目录' },
    { id: 'preserve', name: '保留目录结构', description: '在输出目录中保留导入目录层级' },
  ],
  aiModelProviders: [
    { id: 'gemini', name: 'Gemini（在线）', description: '使用 Google Gemini API' },
    { id: 'localOpenAi', name: '本地 OpenAI 兼容', description: 'Ollama / llama.cpp / LM Studio' },
  ],
  geminiModels: [
    { id: 'gemini-3.1-flash-lite', name: 'Gemini 3.1 Flash-Lite（低成本，推荐）' },
    { id: 'gemini-3.5-flash', name: 'Gemini 3.5 Flash（更强，成本更高）' },
    { id: 'gemini-2.5-flash-lite', name: 'Gemini 2.5 Flash-Lite（兼容备选）' },
  ],
  aiSubtitleOutputPolicies: [
    { id: 'overwriteBackup', name: '覆盖原字幕（保留备份）', description: '普通模式推荐，直接得到最终 .srt' },
    { id: 'preserve', name: '保留原字幕，输出优化副本', description: '诊断或审校时使用，生成 .optimized.srt' },
  ],
  selectedEngine: 'cuda', selectedLanguage: 'zh', selectedFormat: 'srt', selectedOutputLocation: 'selected',
  selectedAiModelProvider: 'gemini', selectedGeminiModel: 'gemini-3.1-flash-lite',
  selectedLocalAiModel: 'qwen3:8b', localAiBaseUrl: 'http://localhost:11434/v1/',
  selectedAiSubtitleOutputPolicy: 'overwriteBackup', effectiveAiSubtitleOutputPolicy: 'overwriteBackup',
  geminiApiKeyConfigured: false, isAiModelConfigured: false, geminiStatus: '未配置 API Key',
  isGeminiBusy: false, geminiSampleResult: '',
  translate: false, modelPath: '', modelStatus: '尚未加载模型',
  gpuName: '', gpuMemoryUsedBytes: undefined, gpuMemoryTotalBytes: undefined,
  modelProgress: 0,
  buildTime: '',
  modelProgressIndeterminate: false, modelLoadElapsedSeconds: 0, autoLoadModel: true,
  uiScale: 'medium',
  terminologyEnabled: false, developerDiagnostics: false, terminologyDirectory: 'C:\\Users\\Public\\AppData\\Local\\WhisperDesktop\\terminology',
  terminologyPacks: [
    { id: 'game-vfx-course', name: 'Unity 游戏特效课程', description: '来自字幕评估报告的 Unity、MOBA 和游戏特效错词', enabled: true, priority: 90, termCount: 16, selected: true, terms: [
      { text: '光晕', category: '特效', corrections: ['光运'] },
      { text: '拖尾', category: '特效', corrections: ['脱尾', '托尾', '油尾'] },
      { text: '溶解', category: '材质', corrections: ['融解'] },
    ] },
    { id: 'hand-drawn-animation', name: '手绘动画教程', description: '来自字幕评估报告的手绘、关键帧和动画错词', enabled: true, priority: 80, termCount: 13, selected: true, terms: [
      { text: '手绘', category: '动画', corrections: ['手会'] },
      { text: '关键帧', category: '动画', corrections: ['关键针', '关键真', '关键震'] },
      { text: '压感', category: '绘制', corrections: ['压杆'] },
    ] },
  ],
  outputFolder: 'C:\\Users\\Public\\Documents', globalStatus: '浏览器预览模式',
  isScanningMedia: false,
  batchStatistics: { selectedCount: 0, knownDurationCount: 0, unknownDurationCount: 0, totalDurationSeconds: 0, processedDurationSeconds: 0, elapsedSeconds: 0, speed: 0, etaIsPartial: false, isEstimating: false },
  isRunning: false, isLoadingModel: false, canStart: false, jobs: [], sources: [], segments: [], logs: '',
  canStartAiSubtitleOptimization: false, isAiRunning: false, aiBatchStatus: '等待字幕优化任务', aiBatchReportPath: '', aiBatchSummary: '',
  logFilePath: 'C:\\Users\\Public\\AppData\\Local\\WhisperDesktop\\Logs\\whisper-desktop.log',
  captureDevices: [{ id: 'default', name: '默认麦克风' }], selectedCaptureDevice: 'default',
  liveStatus: '浏览器预览不读取本机录音设备',
  canStartLive: true, isLiveRunning: false, isLivePreparing: false, liveVoiceDetected: false,
  liveTranscribing: false, liveStalled: false, liveModelProgress: 0, liveElapsedSeconds: 0, liveSegments: [],
  recentModels: [],
}

class BrowserMockBridge implements DesktopBridge {
  private listeners = new Set<(message: HostMessage) => void>()

  send(action: string, payload?: unknown) {
    if (action === 'initialize') {
      queueMicrotask(() => this.publish())
    } else if (action === 'updateSetting') {
      const setting = payload as { name?: string; value?: unknown }
      if (setting?.name === 'geminiModel') {
        mockState.selectedGeminiModel = String(setting.value || 'gemini-3.1-flash-lite')
        this.publish()
      } else if (setting?.name === 'aiModelProvider') {
        mockState.selectedAiModelProvider = String(setting.value || 'gemini')
        mockState.isAiModelConfigured = mockState.selectedAiModelProvider === 'localOpenAi'
          ? Boolean(mockState.localAiBaseUrl && mockState.selectedLocalAiModel)
          : mockState.geminiApiKeyConfigured
        mockState.geminiStatus = mockState.selectedAiModelProvider === 'localOpenAi'
          ? `本地 AI 已配置：${mockState.selectedLocalAiModel}`
          : (mockState.geminiApiKeyConfigured ? 'API Key 已配置' : '未配置 API Key')
        this.publish()
      } else if (setting?.name === 'localAiBaseUrl') {
        mockState.localAiBaseUrl = String(setting.value || 'http://localhost:11434/v1/')
        mockState.isAiModelConfigured = Boolean(mockState.localAiBaseUrl && mockState.selectedLocalAiModel)
        this.publish()
      } else if (setting?.name === 'localAiModel') {
        mockState.selectedLocalAiModel = String(setting.value || 'qwen3:8b')
        mockState.isAiModelConfigured = Boolean(mockState.localAiBaseUrl && mockState.selectedLocalAiModel)
        mockState.geminiStatus = `本地 AI 已配置：${mockState.selectedLocalAiModel}`
        this.publish()
      } else if (setting?.name === 'aiSubtitleOutputPolicy') {
        mockState.selectedAiSubtitleOutputPolicy = String(setting.value || 'overwriteBackup')
        mockState.effectiveAiSubtitleOutputPolicy = mockState.developerDiagnostics ? 'preserve' : mockState.selectedAiSubtitleOutputPolicy
        this.publish()
      } else if (setting?.name === 'developerDiagnostics') {
        mockState.developerDiagnostics = Boolean(setting.value)
        mockState.effectiveAiSubtitleOutputPolicy = mockState.developerDiagnostics ? 'preserve' : mockState.selectedAiSubtitleOutputPolicy
        this.publish()
      } else if (setting?.name && setting.name in mockState) {
        ;(mockState as unknown as Record<string, unknown>)[setting.name] = setting.value
        this.publish()
      }
    } else if (action === 'saveGeminiApiKey') {
      mockState.geminiApiKeyConfigured = true
      mockState.isAiModelConfigured = mockState.selectedAiModelProvider === 'gemini' || Boolean(mockState.localAiBaseUrl && mockState.selectedLocalAiModel)
      mockState.geminiStatus = 'API Key 已保存'
      this.publish()
    } else if (action === 'clearGeminiApiKey') {
      mockState.geminiApiKeyConfigured = false
      mockState.isAiModelConfigured = mockState.selectedAiModelProvider === 'localOpenAi' && Boolean(mockState.localAiBaseUrl && mockState.selectedLocalAiModel)
      mockState.geminiStatus = '未配置 API Key'
      mockState.geminiSampleResult = ''
      this.publish()
    } else if (action === 'testGeminiConnection' || action === 'testAiModelConnection') {
      mockState.geminiStatus = mockState.isAiModelConfigured ? '连接成功：浏览器预览' : '请先配置 AI Provider'
      this.publish()
    } else if (action === 'optimizeGeminiSample' || action === 'optimizeAiSample') {
      const sample = payload as { text?: string }
      mockState.geminiSampleResult = sample?.text ? `${sample.text.replace(/呃|嗯/g, '').trim()}\n\n说明：浏览器预览模拟结果` : ''
      mockState.geminiStatus = mockState.isAiModelConfigured ? '字幕优化试跑完成' : '请先配置 AI Provider'
      this.publish()
    } else if (action === 'addFiles') {
      mockState.jobs = Array.from({ length: 14 }, (_, index) => ({
        id: `mock-${index}`, fileName: `示例视频-${index + 1}.mp4`, inputPath: `D:\\Media\\课程\\示例视频-${index + 1}.mp4`,
        relativePath: `课程\\示例视频-${index + 1}.mp4`, sourceRoot: 'D:\\Media', selected: true, status: '等待中', progress: 0, durationSeconds: 900 + index * 75,
      }))
      mockState.sources = [{ path: 'D:\\Media', name: 'Media', fileCount: 14, selected: true }]
      mockState.globalStatus = '14 个文件，已选择 14 个'
      mockState.batchStatistics = { selectedCount: 14, knownDurationCount: 14, unknownDurationCount: 0, totalDurationSeconds: 19425, processedDurationSeconds: 0, elapsedSeconds: 0, speed: 0, etaIsPartial: false, isEstimating: false }
      mockState.canStart = true
      this.publish()
    } else if (action === 'start') {
      mockState.isRunning = true
      mockState.canStart = false
      mockState.globalStatus = '正在处理 示例视频-1.mp4'
      mockState.batchStatistics = { ...mockState.batchStatistics, processedDurationSeconds: 414, elapsedSeconds: 31, speed: 13.4, etaSeconds: 1419, estimatedCompletionTime: new Date(Date.now() + 1419_000).toISOString() }
      mockState.jobs = mockState.jobs.map((job, index) => ({ ...job, status: index === 0 ? '转录中' : '等待中', progress: index === 0 ? .46 : 0 }))
      mockState.segments = Array.from({ length: 9 }, (_, index) => ({
        index: index + 1, fileName: '示例视频-1.mp4', begin: `00:00:${String(index * 3).padStart(2, '0')}.000`,
        end: `00:00:${String(index * 3 + 2).padStart(2, '0')}.600`, text: `这是用于检查转录过程中界面布局的第 ${index + 1} 条字幕。`,
      }))
      mockState.logs = '[2026-06-11 18:30:00.000] [应用] 开始转录示例视频-1.mp4\n'
      this.emit({
        type: 'patch',
        payload: { isRunning: true, canStart: false, globalStatus: mockState.globalStatus, segments: [] },
      })
      this.emit({ type: 'jobUpdate', payload: structuredClone(mockState.jobs[0]) })
      mockState.segments.forEach(segment => this.emit({ type: 'segmentAdded', payload: structuredClone(segment) }))
      this.emit({ type: 'logAdded', payload: { line: mockState.logs } })
    } else if (action === 'startAiSubtitleOptimization') {
      mockState.isAiRunning = false
      mockState.canStartAiSubtitleOptimization = true
      mockState.aiBatchStatus = 'AI 字幕优化完成：3 个文件，总成本约 ¥0.0180'
      mockState.aiBatchReportPath = 'D:\\Media\\ProblemCases\\ai-batch-report-20260616-090000.md'
      mockState.aiBatchSummary = 'Token 42000，输入 32000，输出 10000'
      this.publish()
    } else if (action === 'startLive') {
      mockState.isLiveRunning = true
      mockState.canStartLive = false
      mockState.liveStatus = '正在聆听'
      mockState.liveElapsedSeconds = 18
      mockState.liveSegments = [
        { index: 1, begin: '00:00:03.200', end: '00:00:07.800', text: '这是实时字幕界面的预览内容。' },
        { index: 2, begin: '00:00:09.100', end: '00:00:14.600', text: '停顿后会把这一句话确认为最终字幕。' },
      ]
      this.publish()
    } else if (action === 'stopLive') {
      mockState.isLiveRunning = false
      mockState.canStartLive = true
      mockState.liveStatus = '实时字幕已停止'
      this.publish()
    } else if (action === 'clearLiveSegments') {
      mockState.liveSegments = []
      this.publish()
    } else if (action === 'openExternal') {
      const { url } = payload as { url?: string }
      if (url) window.open(url, '_blank', 'noopener,noreferrer')
    }
  }

  subscribe(listener: (message: HostMessage) => void) {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  private emit(message: HostMessage) {
    this.listeners.forEach(listener => listener(message))
  }

  private publish() {
    this.emit({ type: 'state', payload: structuredClone(mockState) })
  }
}

export const desktopBridge: DesktopBridge = window.chrome?.webview
  ? new WebView2Bridge()
  : new BrowserMockBridge()
