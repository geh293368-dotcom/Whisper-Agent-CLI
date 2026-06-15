# WhisperDesktop 设计文档

本目录用于保存尚未实现或正在规划的架构方案、功能规格和技术决策。

## 规划中

- [本地 AI 字幕后处理与 RAG 设计](ai-subtitle-postprocessing-rag.md)
  - 使用 Gemma 4 对 Whisper 字幕进行标点、断句、保守纠错和翻译润色。
  - 使用术语库、历史修正和虚拟领域画像进行检索增强。
- [产品体验、品牌与关于页规划](product-experience-branding.md)
  - 记录 WebView2 桌面化、字号、UI 视觉探索、关于页和产品改名方案。
- [任务完成时间估算与模型自动加载设计](task-progress-and-model-autoload.md)
  - 基于实际日志评估字幕 ETA，并规划启动时自动加载上次模型的异步流程、进度反馈和失败回退。
- [实时字幕功能设计](live-subtitles-design.md)
  - 规划麦克风采集、VAD 分句、CUDA 实时推理、字幕界面、保存导出、状态机和分阶段实施路线。

文档中的“规划中”功能不代表已经进入当前发行版本。正式实施时应根据届时的模型、运行库和硬件支持重新验证具体版本。
