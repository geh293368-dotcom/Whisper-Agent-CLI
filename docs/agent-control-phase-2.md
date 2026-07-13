# Agent 控制能力：第二阶段

> 本文是第二阶段实现记录。当前完整命令、JSON 字段和接入边界以 [Agent 与自动化接入指南](agent-integration.md) 为准。

第二阶段把第一阶段的同步调用扩展为可供剪辑软件使用的异步任务合同。调用方提交媒体后会得到稳定 `jobId`，之后可以断开进程，再通过新连接查询、等待、取消或取得字幕结果。

## 推荐接入流程

```powershell
# 1. 提交并进入处理队列；request-id 用于避免调用方重复提交
.\whisperctl.exe submit "D:\media\lesson.mp4" --start --request-id "editor-project-42-track-7" --json

# 2. 保存响应中的 jobId，随后可以从任意新进程查询
.\whisperctl.exe status <job-id> --json

# 3. 轮询到终态；wait 内部会反复建立短连接查询，而不是长期占用一条 Pipe
.\whisperctl.exe wait <job-id> --json

# 4. 获取并校验输出字幕路径
.\whisperctl.exe result <job-id> --json
```

其他管理命令：

```powershell
.\whisperctl.exe list --limit 50 --json
.\whisperctl.exe start <job-id> --json
.\whisperctl.exe cancel <job-id> --json
```

原有同步命令继续兼容：

```powershell
.\whisperctl.exe transcribe "D:\media\lesson.mp4" --json --no-activate
```

## 任务状态

- `pending`：已写入队列，尚未开始。
- `running`：正在转录。
- `completed`：已生成输出文件。
- `failed`：模型、媒体或输出过程失败。
- `canceled`：调用方或用户取消。
- `skipped`：没有有效音轨或没有识别出可输出内容。
- `interrupted`：听录上次退出时任务仍在运行。

第二阶段不会尝试从音频中间断点续算。`interrupted` 任务可以重新提交原文件继续，新的运行会重用原任务记录。

## 持久化与重连

任务日志默认保存在：

```text
%LOCALAPPDATA%\WhisperDesktop\jobs.json
```

日志最多保留最近更新的 500 个任务，只保存输入路径、配置快照、状态、进度、输出路径、错误和时间信息，不复制媒体或字幕内容。写入采用临时文件替换，避免程序退出时留下半个 JSON。

本地“断线重连”不是恢复旧 Named Pipe，而是调用方保存 `jobId`，稍后建立新连接执行 `status` 或 `result`。桌面端重启后，已完成、等待和失败任务会恢复；原来处于 `running` 的任务恢复为 `interrupted`。

测试或便携环境可以通过 `WHISPERDESKTOP_DATA_DIR` 指定任务日志目录。

## 幂等请求

剪辑软件可以为一次业务操作提供稳定 `--request-id`。相同 request ID 重复提交时，桌面端返回原有任务，不会创建第二条任务。重试一个已经 `interrupted` 或失败的任务时，应使用新的 request ID，或者不传 request ID。

## 当前边界

- 桌面进程仍然是模型和队列宿主，尚未拆分独立 `WhisperWorker`。
- 配置快照已经记录，但重跑任务仍使用桌面端当前配置。
- 多个 `--start` 请求会在桌面端串行等待 GPU；不会并行加载多个模型。
- MCP 不属于第二阶段；未来只需要在这套任务合同外增加薄适配层。
- 只读的当前页面、焦点、可见控件与 PNG 捕获已经作为独立观察层加入，见 [Agent 界面观察能力](agent-ui-observability.md)。
