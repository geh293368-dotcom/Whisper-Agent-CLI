export type Page = 'batch' | 'live' | 'settings'

export interface Option {
  id: string
  name: string
  description?: string
}

export interface Job {
  id: string
  fileName: string
  inputPath: string
  relativePath: string
  sourceRoot?: string
  selected: boolean
  status: string
  progress: number
  durationSeconds?: number
  elapsedSeconds?: number
  error?: string
  outputPath?: string
}

export interface SourceFolder {
  path: string
  name: string
  fileCount: number
  selected: boolean
}

export interface Segment {
  index: number
  fileName: string
  begin: string
  end: string
  text: string
}

export interface CaptureDevice {
  id: string
  name: string
}

export interface LiveSegment {
  index: number
  begin: string
  end: string
  text: string
}

export interface AppState {
  engines: Option[]
  languages: Option[]
  formats: Option[]
  outputLocations: Option[]
  selectedEngine: string
  selectedLanguage: string
  selectedFormat: string
  selectedOutputLocation: string
  translate: boolean
  modelPath: string
  modelStatus: string
  modelProgress: number
  outputFolder: string
  globalStatus: string
  batchStatistics: BatchStatistics
  isScanningMedia: boolean
  isRunning: boolean
  isLoadingModel: boolean
  canStart: boolean
  canStartLive: boolean
  jobs: Job[]
  sources: SourceFolder[]
  segments: Segment[]
  logs: string
  logFilePath: string
  captureDevices: CaptureDevice[]
  selectedCaptureDevice?: string
  liveStatus: string
  isLiveRunning: boolean
  isLivePreparing: boolean
  liveVoiceDetected: boolean
  liveTranscribing: boolean
  liveStalled: boolean
  liveModelProgress: number
  liveElapsedSeconds: number
  liveSegments: LiveSegment[]
  liveOutputPath?: string
  recentModels: string[]
}

export interface BatchStatistics {
  selectedCount: number
  knownDurationCount: number
  totalDurationSeconds: number
  processedDurationSeconds: number
  elapsedSeconds: number
  speed: number
  etaSeconds?: number
}

export interface HostMessage {
  type: 'state' | 'patch' | 'jobUpdate' | 'segmentAdded' | 'liveSegmentAdded' | 'logAdded' | 'error'
  payload: AppState | Partial<AppState> | Job | Segment | LiveSegment | { line: string } | { message: string }
}

export interface DesktopCommand {
  action: string
  payload?: unknown
  requestId?: string
}
