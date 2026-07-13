# Agent 与自动化接入指南

听录（TINGLU）除了提供图形界面，也可以作为本机语音转录任务宿主。Agent、脚本或剪辑软件通过 `whisperctl.exe` 把媒体提交给已经运行的桌面端，复用桌面端加载的模型、GPU、配置和串行任务队列。

这是一套本机自动化接口，不是通用桌面控制或完整 Agent 平台。当前没有 MCP 服务器，也不开放鼠标点击、键盘输入、任意 JavaScript 或通用 CDP 调用。

## 工具形态

```text
用户操作 React 界面 ───────────────┐
                                  ▼
Agent / 脚本 → whisperctl.exe → Named Pipe → 听录桌面端（WhisperDesktop.Modern.exe）
                                                │
                                                ├─ 已加载的 Whisper 模型
                                                ├─ CPU / CUDA / D3D11 后端
                                                ├─ 串行转录队列
                                                └─ %LOCALAPPDATA%\WhisperDesktop\jobs.json
```

- `WhisperDesktop.Modern.exe` 是模型、配置和任务队列的宿主，必须先启动。
- `whisperctl.exe` 是桌面端控制工具，适合 Agent、脚本和外部应用集成。
- `whisper-cli.exe` 和 `Examples/TranscribeCS` 是独立转录路径，不依赖桌面端，也不会复用桌面端已经加载的模型。
- 每日便携包会把 `whisperctl.exe` 与桌面端放在同一目录。

## 快速开始

先启动 `WhisperDesktop.Modern.exe`，在设置页选择有效模型并确认所需引擎，然后从同一发布目录执行：

```powershell
# 检查桌面端是否可连接
.\whisperctl.exe ping --json

# 提交、开始并等待字幕生成完成
.\whisperctl.exe transcribe "D:\media\lesson.mp4" --json --no-activate
```

`transcribe` 等价于 `submit --start --wait`。成功响应中的 `jobs[0].outputPath` 是生成的字幕路径；调用方还应检查 `jobs[0].outputExists`。

需要断开后继续查询时，推荐使用异步任务流程：

```powershell
# 1. 提交并立即开始；业务侧保存返回的 jobId
.\whisperctl.exe submit "D:\media\lesson.mp4" --start --request-id "editor-project-42-track-7" --json --no-activate

# 2. 随后从任意新进程查询、等待并取得结果
.\whisperctl.exe status <job-id> --json
.\whisperctl.exe wait <job-id> --json
.\whisperctl.exe result <job-id> --json
```

## 命令表

| 命令 | 作用 | 是否改变任务或界面 |
|---|---|---|
| `ping` | 检查桌面端连接 | 否 |
| `submit <路径>...` | 导入文件或目录，默认只入队 | 是 |
| `transcribe <路径>...` | 入队、开始并等待完成 | 是 |
| `status <job-id>` | 查询一个任务 | 否 |
| `start <job-id>` | 启动或重新运行任务 | 是 |
| `wait <job-id>` | 每 500 ms 通过短连接轮询到终态 | 否 |
| `result <job-id>` | 取得完成任务的输出路径 | 否 |
| `cancel <job-id>` | 取消等待中或运行中的任务 | 是 |
| `list [--limit N]` | 列出最近任务，范围为 1–500 | 否 |
| `ui-state` | 读取当前页面、焦点和可见控件 | 否 |
| `screenshot [--output <png>]` | 捕获 React 主界面 PNG | 否 |

提交命令支持：

- `--start`：入队后立即开始。
- `--wait`：等待终态；同时隐含 `--start`。
- `--request-id <ID>`：业务幂等标识，避免同一操作重复创建任务。
- `--no-activate`：不要因为提交任务而唤起桌面窗口。
- `--json`：输出适合程序解析的单行 JSON。

## 任务生命周期与持久化

任务状态包括：

- `pending`：已进入队列。
- `running`：正在转录。
- `completed`：已生成输出文件。
- `failed`：模型、媒体或输出过程失败。
- `canceled`：用户或调用方取消。
- `skipped`：没有有效音轨或没有可输出内容。
- `interrupted`：桌面端上次退出时任务仍在运行。

任务记录默认保存在：

```text
%LOCALAPPDATA%\WhisperDesktop\jobs.json
```

最多保留最近更新的 500 个任务。桌面端重启后，调用方可以使用保存的 `jobId` 重新执行 `status` 或 `result`。运行中的任务不会从音频中间断点续算，而会恢复为 `interrupted`；需要重新执行时可使用 `start`，或以新的 request ID 再次提交。

测试或便携环境可通过 `WHISPERDESKTOP_DATA_DIR` 指定任务数据目录。

## JSON 合同

协议版本当前为 `1`，JSON 字段、命令名和错误码保持英文。CLI 使用 camelCase 输出。

请求字段：

| 字段 | 用途 |
|---|---|
| `protocolVersion` | 协议版本 |
| `requestId` | 每次协议请求的追踪 ID |
| `action` | `ping`、`submit`、`status` 等动作 |
| `paths` | 提交的文件或目录路径 |
| `jobId` | 任务管理命令的目标任务 |
| `clientRequestId` | 调用方提供的幂等 ID |
| `screenshotPath` | 可选截图输出路径 |
| `limit` | `list` 返回数量 |
| `start`、`wait`、`activate` | 提交和窗口行为选项 |

响应顶层字段包括 `protocolVersion`、`requestId`、`success`、`message`、`errorCode`、`jobs` 和可选的 `ui`。

每个 `jobs` 项目提供 `jobId`、输入路径、状态、进度、创建/更新时间、配置快照、输出路径、输出是否存在以及错误摘要。调用方应以 `success`、`errorCode` 和稳定状态值作程序判断，不要解析中文 `message`。

常见错误码包括：

- `desktop_unavailable`：桌面端未启动或无法连接。
- `unsupported_protocol`、`unknown_action`、`invalid_request`：协议或参数无效。
- `invalid_path`、`desktop_busy`：输入路径无效或桌面端暂时不能接收导入。
- `job_not_found`、`result_not_ready`、`output_missing`、`cannot_cancel`：任务状态不满足操作要求。
- `ui_unavailable`、`ui_invalid_response`、`ui_capture_failed`：界面观察或截图失败。

`whisperctl.exe` 退出码：

| 退出码 | 含义 |
|---|---|
| `0` | 命令成功 |
| `2` | CLI 参数错误 |
| `3` | 桌面端不可连接 |
| `4` | 桌面命令或任务失败 |
| `130` | 调用方通过 Ctrl+C 取消等待 |

## 界面观察

优先使用语义状态判断界面：

```powershell
.\whisperctl.exe ui-state --json
```

响应的 `ui` 字段包含窗口状态、当前页面和面板、焦点元素、可见对话框、错误摘要、视口信息以及最多 200 个可见交互控件。不会返回输入框的值。

只有需要视觉证据时再截图：

```powershell
.\whisperctl.exe screenshot --json
.\whisperctl.exe screenshot --output "D:\Temp\whisper-ui.png" --json
```

默认截图目录是 `%LOCALAPPDATA%\WhisperDesktop\agent-captures\`，程序会清理其中超过 7 天的默认 `ui-*.png`。截图只覆盖 WebView2 中的 React 主界面，不保证包含 Windows 文件选择器、原生消息框或标题栏。

## 安全与数据边界

- 命令通道使用仅限当前 Windows 用户的 Named Pipe，不提供远程网络监听。
- 桌面端仍是唯一的模型和 GPU 宿主，多个启动请求会串行进入队列。
- 任务日志记录路径、配置快照、状态和输出信息，不复制媒体或字幕正文。
- 截图可能包含本机路径、媒体文件名和字幕内容；调用方负责保护和清理自己指定的输出文件。
- `ui-state` 和 `screenshot` 是只读观察能力，不会激活、还原或操作窗口。
- 当前没有 MCP 适配器。未来如增加 MCP，应作为这套任务合同之外的薄适配层，不另建任务语义。

## 阶段记录

- [第一阶段：单实例与本地命令通道](agent-control-phase-1.md)
- [第二阶段：持久任务与异步管理](agent-control-phase-2.md)
- [界面观察能力实现记录](agent-ui-observability.md)

以上页面用于保留功能演进和当时的边界；当前接入方式以本文为准。
