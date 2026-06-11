import type { AppState, DesktopCommand, HostMessage } from './types'

declare global {
  interface Window {
    chrome?: {
      webview?: {
        postMessage(message: DesktopCommand): void
        addEventListener(type: 'message', listener: (event: MessageEvent<HostMessage>) => void): void
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
    window.chrome?.webview?.addEventListener('message', event => listener(event.data))
    return () => undefined
  }
}

const mockState: AppState = {
  engines: [
    { id: 'cuda', name: 'whisper.cpp 1.8.6（CUDA / NVIDIA，推荐）' },
    { id: 'cpu', name: 'whisper.cpp 1.8.6（CPU，兼容）' },
    { id: 'd3d11', name: 'WhisperDesktop D3D11（兼容版）' },
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
  selectedEngine: 'cuda', selectedLanguage: 'zh', selectedFormat: 'srt', selectedOutputLocation: 'selected',
  translate: false, modelPath: '', modelStatus: '尚未加载模型', modelProgress: 0,
  outputFolder: 'C:\\Users\\Public\\Documents', globalStatus: '浏览器预览模式',
  isRunning: false, isLoadingModel: false, canStart: false, jobs: [], sources: [], segments: [], logs: '',
  logFilePath: 'C:\\Users\\Public\\AppData\\Local\\WhisperDesktop\\Logs\\whisper-desktop.log',
  captureDevices: [{ id: 'default', name: '默认麦克风' }], selectedCaptureDevice: 'default',
  liveStatus: '浏览器预览不读取本机录音设备',
  recentModels: [],
}

class BrowserMockBridge implements DesktopBridge {
  private listeners = new Set<(message: HostMessage) => void>()

  send(action: string) {
    if (action === 'initialize') {
      queueMicrotask(() => this.publish())
    } else if (action === 'addFiles') {
      mockState.jobs = Array.from({ length: 14 }, (_, index) => ({
        id: `mock-${index}`, fileName: `示例视频-${index + 1}.mp4`, inputPath: `D:\\Media\\课程\\示例视频-${index + 1}.mp4`,
        relativePath: `课程\\示例视频-${index + 1}.mp4`, sourceRoot: 'D:\\Media', selected: true, status: '等待中', progress: 0,
      }))
      mockState.sources = [{ path: 'D:\\Media', name: 'Media', fileCount: 14, selected: true }]
      mockState.globalStatus = '14 个文件，已选择 14 个'
      mockState.canStart = true
      this.publish()
    } else if (action === 'start') {
      mockState.isRunning = true
      mockState.canStart = false
      mockState.globalStatus = '正在处理 示例视频-1.mp4'
      mockState.jobs = mockState.jobs.map((job, index) => ({ ...job, status: index === 0 ? '转录中' : '等待中', progress: index === 0 ? .46 : 0 }))
      mockState.segments = Array.from({ length: 9 }, (_, index) => ({
        index: index + 1, fileName: '示例视频-1.mp4', begin: `00:00:${String(index * 3).padStart(2, '0')}.000`,
        end: `00:00:${String(index * 3 + 2).padStart(2, '0')}.600`, text: `这是用于检查转录过程中界面布局的第 ${index + 1} 条字幕。`,
      }))
      mockState.logs = '[2026-06-11 18:30:00.000] [应用] 开始转录示例视频-1.mp4\n'
      this.publish()
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
