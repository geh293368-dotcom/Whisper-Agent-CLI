# Agent 界面观察能力

WhisperDesktop 为 Agent 提供只读界面观察命令。它们复用现有本地 Named Pipe，不开放鼠标点击、键盘输入、任意 JavaScript 或通用 CDP 调用。

## 读取语义状态

```powershell
.\whisperctl.exe ui-state --json
```

响应的 `ui` 字段包括：

- `windowActive`、`windowState`：桌面窗口是否激活以及正常、最小化或最大化状态。
- `page`、`panel`：当前 React 页面和批量字幕页中的活动面板。
- `focusedElement`：当前 DOM 焦点的角色、名称和稳定 `agentId`（如果控件提供）。
- `dialog`、`error`：可见 Web 对话框和错误提示的文本摘要。
- `viewportWidth`、`viewportHeight`、`deviceScaleFactor`：当前 WebView 可视区域。
- `controls`：最多 200 个可见交互控件，不返回输入框的值。

语义状态适合纯文本 Agent，也应当优先于截图用于判断软件状态。

## 捕获当前界面

```powershell
# 自动写入本机 Agent 截图目录
.\whisperctl.exe screenshot --json

# 写入调用方指定的 PNG；CLI 会在发送前转换为绝对路径
.\whisperctl.exe screenshot --output "D:\Temp\whisper-ui.png" --json
```

未指定输出时，截图写入：

```text
%LOCALAPPDATA%\WhisperDesktop\agent-captures\
```

程序会在生成默认截图时清理该目录中超过 7 天的 `ui-*.png`。

响应通过 `ui.screenshotPath` 返回 PNG 的绝对路径。Agent 必须自身具备读取本地图片的能力；只有文本能力的调用方应使用 `ui-state`。

## 当前边界

- 截图使用 WebView2 原生 `CapturePreviewAsync`，覆盖 WhisperDesktop 的 React 主界面。
- Windows 文件选择器、文件夹选择器、原生 `MessageBox` 和标题栏不属于 WebView 内容，第一版不保证出现在截图中。
- 观察命令不会激活、还原或改变窗口，也不会修改当前页面和焦点。
- 截图可能包含本机路径、媒体文件名和字幕内容；调用方负责保护和清理自己指定的输出文件。
- 协议保持版本 `1`；新增请求和响应字段是向后兼容的。
