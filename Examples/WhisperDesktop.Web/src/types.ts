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
  isRunning: boolean
  isLoadingModel: boolean
  canStart: boolean
  jobs: Job[]
  sources: SourceFolder[]
  segments: Segment[]
  logs: string
  logFilePath: string
  captureDevices: CaptureDevice[]
  selectedCaptureDevice?: string
  liveStatus: string
  recentModels: string[]
}

export interface HostMessage {
  type: 'state' | 'error'
  payload: AppState | { message: string }
}

export interface DesktopCommand {
  action: string
  payload?: unknown
  requestId?: string
}
