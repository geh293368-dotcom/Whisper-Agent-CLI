# Agent 控制能力：第一阶段

WhisperDesktop 第一阶段提供“单实例桌面端 + 本地命令通道”。目标是让脚本或 Agent 把媒体文件提交到已经运行的桌面端，复用已加载的模型、GPU 和任务队列。

## 使用方式

先启动 `WhisperDesktop.Modern.exe`，再在同一发布目录运行：

```powershell
# 检查桌面端是否可连接
.\whisperctl.exe ping --json

# 只加入桌面任务队列
.\whisperctl.exe submit "D:\media\lesson.mp4" --json

# 加入队列并立即开始，等待完成后返回 JSON
.\whisperctl.exe transcribe "D:\media\lesson.mp4" --json

# 扫描整个目录，加入队列后立即开始，但不等待完成
.\whisperctl.exe submit "D:\media\course" --start --json
```

`transcribe` 等价于 `submit --start --wait`。传入命令时默认唤起桌面窗口；Agent 后台调用可以追加 `--no-activate`。

## 当前协议

- 传输：仅限当前 Windows 用户的 Named Pipe。
- 协议版本：`1`。
- 请求字段：`protocolVersion`、`requestId`、`action`、`paths`、`start`、`wait`、`activate`。
- 响应字段：`success`、`message`、`errorCode` 和任务结果数组。
- `whisperctl` 退出码：`0` 成功，`2` 参数错误，`3` 桌面端不可连接，`4` 桌面命令或任务失败。

桌面程序本身也接受路径或 `--enqueue <path>`。如果已有实例，第二个实例会把命令转发给主实例并退出。

## 第一阶段边界与后续

- 第一阶段最初只支持 `ping`、入队、可选开始和等待结果。
- `whisper-cli.exe` / `TranscribeCS` 仍是独立运行路径，不依赖桌面端，也不会复用桌面端模型。
- 第二阶段已经增加真实 `jobId`、查询、取消、结果、列表和本地任务日志，见 [Agent 控制能力：第二阶段](agent-control-phase-2.md)。
- MCP 适配器仍未加入；后续可以复用同一任务协议实现。
