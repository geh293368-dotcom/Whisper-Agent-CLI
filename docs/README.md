# WhisperDesktop 设计文档

本目录用于保存已实现功能的设计记录、正在规划的架构方案、功能规格和技术决策。

## 已实现

- [Agent 与自动化接入指南](agent-integration.md)
  - 当前规范入口：说明 `whisperctl.exe`、本地 Named Pipe、持久任务、JSON 合同、界面观察和安全边界。
  - [第一阶段](agent-control-phase-1.md)、[第二阶段](agent-control-phase-2.md)和[界面观察能力](agent-ui-observability.md)作为实现阶段记录保留。
- [任务完成时间估算与模型自动加载设计](task-progress-and-model-autoload.md)
  - 已实现批量字幕 ETA、预计完成时刻、未知时长提示、启动自动加载、加载已用时、取消加载和一次性保护。
- [实时字幕功能设计](live-subtitles-design.md)
  - 已实现第一版麦克风实时字幕，使用 VAD 分句、状态展示、确认字幕列表、复制和 TXT/SRT 导出。

## 规划中

- [Microsoft Store 发行版开发计划](microsoft-store-release-plan.md)
  - 规划 MSIX、自包含运行时、内置多语言模型、简体中文/英语本地化、隐私与 AI 合规、商店素材及认证测试。
  - 明确区分界面语言与语音识别语言，并按首版阻塞项、后续语言和增强功能分阶段执行。
- [本地 AI 字幕后处理与 RAG 设计](ai-subtitle-postprocessing-rag.md)
  - 使用 Gemma 4 对 Whisper 字幕进行标点、断句、保守纠错和翻译润色。
  - 使用术语库、历史修正和虚拟领域画像进行检索增强。
- [产品体验、品牌与关于页规划](product-experience-branding.md)
  - 记录 WebView2 桌面化、字号、UI 视觉探索、关于页和产品改名方案。
- [听录 TINGLU 轻量品牌规范](brand-guide.md)
  - 定义当前工作品牌、中英文标准名、图标概念、标准字、基础颜色和发行前资产边界。

文档中的“规划中”功能不代表已经进入当前发行版本。已实现文档记录的是当前第一版能力和剩余边界，后续迭代仍应根据模型、运行库和硬件支持重新验证具体版本。
