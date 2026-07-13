# Microsoft Store 发行版开发计划

## 1. 文档目的

本文用于指导 WhisperDesktop 从当前免安装便携版演进为可提交 Microsoft Store 的正式发行版。

商店发行版不是另一套产品，也不应分叉出独立功能代码。它继续复用现有的 WPF + WebView2 + React UI、WhisperNet、D3D11、whisper.cpp CPU/CUDA 和本地 Agent 协议，只增加发行所需的打包、本地化、首装体验、合规材料和认证测试。

本文中的优先级含义：

- **P0**：首个商店版本的发布阻塞项。
- **P1**：首发建议完成，但在不影响认证时可以延后一个小版本。
- **P2**：上架后根据用户反馈和数据决定。

## 2. 发行目标与边界

首个 Microsoft Store 版本应满足以下目标：

1. Windows 10/11 x64 用户从商店安装后可以直接启动，不需要预装 .NET 10。
2. 在没有 NVIDIA 显卡、没有预先下载模型、没有配置在线 AI 的干净电脑上，仍能完成一次离线语音转录。
3. 内置简体中文和英语界面，应用根据系统语言自动选择，也允许用户手动切换。
4. 界面语言与语音识别语言彼此独立；切换英语界面不能自动把转录语言改成英语。
5. 默认离线处理。只有用户明确选择并授权在线 AI Provider 后，字幕文本才可以发送到第三方服务。
6. 能通过 Windows App Certification Kit，并在干净 Windows 虚拟机上完成安装、启动、升级和卸载测试。

首个版本不要求：

- 一次完成所有语言；
- 用户账号、云同步或订阅系统；
- ARM64；
- 内置 `small`、`medium` 或更大的 Whisper 模型；
- 把 CUDA 设为唯一可用引擎；
- 在商店之外再实现一套自动更新器；
- 重写现有视觉设计或迁移到 WinUI。

## 3. 当前基线

### 3.1 已具备的能力

- WPF + WebView2 + React 的桌面架构可以使用 MSIX 发布。
- 已有 CPU、CUDA 和 D3D11 三类推理路径。
- 配置、任务记录、术语包和日志已经主要写入 `%LOCALAPPDATA%\WhisperDesktop`。
- Gemini API Key 使用 Windows 凭据管理器保存。
- Agent Named Pipe 使用 `CurrentUserOnly`，并限制请求长度。
- 已有中文界面截图、功能说明、日志和字幕管线测试。
- 仓库已经保留 MPL 2.0、whisper.cpp 及部分第三方许可证文件。

### 3.2 当前发布阻塞

- 仓库没有 Windows Application Packaging Project、`AppxManifest.xml` 或 `.msixupload` 产物。
- `Tools/package-daily.ps1` 生成的是 framework-dependent 便携 ZIP，目标电脑仍需安装 .NET 10 Desktop Runtime。
- WebView2 当前未显式指定 User Data Folder，默认会在 EXE 旁创建 `.WebView2` 目录；MSIX 安装目录为只读位置。
- 首次运行要求用户自行准备模型，认证人员无法开箱验证核心功能。
- 当前默认引擎为 CUDA，普通认证虚拟机通常没有 NVIDIA GPU。
- React 与 WPF 的用户可见文本大量直接写在源码中，没有统一的 i18n/资源系统。
- 当前没有正式隐私政策、在线 AI 明示授权和“报告不当 AI 输出”入口。
- EXE 的产品名、公司名、图标和版本仍接近工程默认值。
- 根 README 中仍有 whisper.cpp 旧版本和“无需任何额外运行时”等过时或互相冲突的描述。
- 尚未形成完整的 `ThirdPartyNotices.txt` 和模型来源/哈希清单。

## 4. 双轨发行策略

便携版和商店版应共享同一套业务代码，但使用不同发行入口：

| 项目 | 便携版 | Microsoft Store 版 |
|---|---|---|
| 产物 | ZIP 文件夹 | `.msixupload` / MSIX |
| 更新 | 用户手动下载 | Microsoft Store 自动更新 |
| .NET | 当前依赖 Desktop Runtime | `win-x64` 自包含 |
| 默认模型 | 用户自行选择 | 内置一个多语言基础模型 |
| 默认引擎 | 可偏向 CUDA | CPU 或 D3D11 安全默认，自动检测 CUDA |
| WebView2 数据 | LocalAppData | LocalAppData / 包 LocalState |
| CLI | 文件夹内 `whisperctl.exe` | 注册 App Execution Alias，或首版暂不提供 |
| 发布元数据 | `BUILD-INFO.txt` | 包版本、商店 listing、认证备注 |

不得把商店专用判断散落到业务逻辑。建议通过 MSBuild 属性或发布 profile 控制模型、后端和打包内容，例如：

```text
DistributionChannel=Portable
DistributionChannel=MicrosoftStore
```

## 5. 多语言设计

### 5.1 两类语言必须分开

应用需要保存两个不同字段：

```text
uiLanguage            界面语言，例如 zh-CN、en-US
transcriptionLanguage 语音识别语言，例如 zh、en、ja、auto
```

`uiLanguage` 影响按钮、导航、错误提示、日期、数字和商店截图。

`transcriptionLanguage` 只传给 Whisper 推理层。用户可以使用英语界面转录中文，也可以使用中文界面转录英语。

### 5.2 首发语言

首个商店版本只正式声明并完整支持：

| 语言 | Locale | 用途 | 首发要求 |
|---|---|---|---|
| 简体中文 | `zh-CN` | 当前核心用户与现有界面 | 完整 UI、错误、帮助、隐私与商店 listing |
| 英语（美国） | `en-US` | 全球默认回退语言 | 完整 UI、错误、帮助、隐私与商店 listing |

英语应作为未知系统语言的回退资源。中文 Windows 自动选择 `zh-CN`，其他系统默认选择 `en-US`。用户选择写入设置，后续启动优先使用用户选择。

### 5.3 后续语言顺序

在没有商店下载数据和用户反馈前，不宣称某种语言一定有最多用户。建议按以下批次准备：

1. **P1**：繁体中文 `zh-TW`、日语 `ja-JP`、韩语 `ko-KR`、西班牙语 `es-ES`。
2. **P2**：德语 `de-DE`、法语 `fr-FR`、葡萄牙语（巴西）`pt-BR`。
3. 阿拉伯语等 RTL 语言需要额外布局验证，不和普通翻译混在同一批次。

只有达到“完整翻译 + 截图 + 商店 listing + 回归测试”的语言，才在包清单和 Partner Center 中声明支持。Microsoft Store 要求应用所声明语言的产品描述与实际体验保持一致。

### 5.4 React 本地化结构

建议使用成熟的 `i18next` + `react-i18next`，建立：

```text
Examples/WhisperDesktop.Web/src/i18n/index.ts
Examples/WhisperDesktop.Web/src/i18n/locales/en-US.ts
Examples/WhisperDesktop.Web/src/i18n/locales/zh-CN.ts
```

资源键使用稳定的英文标识：

```ts
t('nav.batch')
t('model.load')
t('errors.modelNotFound')
t('ai.onlineConsent.title')
```

禁止使用中文正文作为资源键。日期、时间、百分比、文件大小和费用使用 `Intl.DateTimeFormat`、`Intl.NumberFormat` 等区域格式化 API。

### 5.5 WPF 本地化结构

WPF 负责的原生对话框、启动错误、WebView2 错误、凭据提示、后台任务和日志摘要应使用 `.resx` / `ResourceManager`：

```text
Examples/WhisperDesktop.Wpf/Resources/Strings.resx
Examples/WhisperDesktop.Wpf/Resources/Strings.zh-CN.resx
Examples/WhisperDesktop.Wpf/Resources/Strings.en-US.resx
```

WPF 与 React 之间的协议字段、命令名、错误码和 JSON 属性继续使用英文，不随界面语言变化。协议优先传递稳定错误码，React 根据错误码显示本地化文案；需要原生显示时由 WPF 资源文件格式化。

### 5.6 文本迁移范围

第一轮必须迁移：

- 导航、标题、按钮、表单标签、状态、空状态和确认对话框；
- 模型、引擎、麦克风、输出目录和 AI Provider 的提示；
- 用户可以看到的错误与恢复建议；
- 关于页、隐私入口、第三方许可入口；
- 首次启动、模型说明和在线 AI 授权；
- 商店截图中出现的所有文本。

可保留英文或延后处理：

- 协议字段和 CLI JSON；
- 文件名、模型 ID、引擎 ID；
- 仅开发者可见的内部诊断事件名；
- 第三方原始错误正文，但必须附带一条本地化摘要。

### 5.7 本地化质量门槛

- `zh-CN` 与 `en-US` 资源键完全一致，CI 检查缺失键。
- 英语模式下不得出现未列入白名单的中文 UI 文本。
- 验证长英语文本不会截断、遮挡或挤压按钮。
- 测试 100%、125%、150%、200% Windows 缩放。
- 使用伪本地化或加长文本检查弹性布局。
- 每种已声明语言至少生成四张桌面截图，截图和说明文字使用对应语言。

## 6. 内置模型与首装体验

### 6.1 首发默认方案

建议内置多语言 `ggml-base.bin`，约 142 MiB，兼顾中文、英语和首次体验。若首包体积必须最小化，可退回约 75 MiB 的 `ggml-tiny.bin`。

不得使用 `tiny.en` 或 `base.en` 作为唯一内置模型，因为它们不适合中文用户。

内置模型以只读内容放入 MSIX：

```text
Models/ggml-base.bin
Models/MODEL-INFO.json
```

`MODEL-INFO.json` 至少记录：

- 模型 ID 与显示名；
- 文件大小；
- SHA-256；
- 来源 URL；
- 许可证标识；
- 支持语言；
- 构建/转换来源。

配置中保存 `builtin:base`，不要保存 WindowsApps 下的绝对路径。应用启动时使用 `AppContext.BaseDirectory` 解析当前版本的内置模型。

### 6.2 后续模型管理器

用户下载的模型保存到：

```text
%LOCALAPPDATA%\WhisperDesktop\Models
```

下载流程应具备：

- 下载前显示名称、大小、语言和质量等级；
- HTTPS 固定版本 URL；
- 断点或失败重试；
- 取消按钮；
- 临时文件下载完成后校验 SHA-256，再原子重命名；
- 磁盘空间不足提示；
- 删除下载模型，但不得尝试删除内置只读模型；
- 下载失败时仍可回退到内置模型。

### 6.3 首次运行闭环

在没有任何历史配置时：

1. 根据系统语言选择 `zh-CN` 或 `en-US`。
2. 选择 CPU 或 D3D11 安全引擎；检测到完整 CUDA 条件后再提示加速。
3. 自动识别并加载内置模型。
4. 提供一个小型示例音频或清晰的“添加音视频”入口。
5. 不要求登录、不要求 API Key、不要求联网即可完成第一次转录。

## 7. MSIX 与运行时工作

### 7.1 包项目

建议新增：

```text
Examples/WhisperDesktop.Package/
  WhisperDesktop.Package.wapproj
  Package.appxmanifest
  Assets/
```

包目标为 Windows Desktop x64。首发先不声明 ARM64。

### 7.2 .NET 与 WebView2

- 使用 `dotnet publish -r win-x64 --self-contained true` 生成商店输入。
- 不启用未经验证的 trimming 或 single-file；WPF、反射和原生依赖应先保证正确性。
- WebView2 User Data Folder 显式指向可写的 LocalAppData/包 LocalState。
- 使用 WebView2 Evergreen Runtime，并在运行时缺失时给出本地化说明；是否改用 Fixed Version 另行评估包体积。

### 7.3 能力与入口

清单只声明实际需要的能力，预计包括：

- WPF 桌面程序所需的 full trust；
- 麦克风；
- 在线 AI 所需网络访问；
- 如保留 `whisperctl.exe`，为其声明 App Execution Alias。

首版不申请 `broadFileSystemAccess`。用户通过 Win32 文件选择器选择音视频、模型和输出目录即可。

### 7.4 CPU、D3D11 与 CUDA 策略

商店首发必须在无 NVIDIA 环境可用：

- 默认 CPU 或 D3D11；
- 检测 AVX/F16C、D3D11、NVIDIA Driver 和 CUDA DLL；
- 不满足条件时禁用对应引擎并显示原因；
- 引擎加载失败后允许切换，不得直接退出程序。

CUDA 是否进入首个商店包是一个发布决策点：

- **不包含 CUDA**：包显著更小，认证变量更少，但失去主要性能卖点。
- **包含 CUDA**：保留完整体验，但需审核约 500 MiB CUDA Runtime、NVIDIA 重分发条款和无驱动机器行为。

推荐先完成 CPU/D3D11 商店候选包，再用同一测试清单评估完整 CUDA 包；不要在 MSIX 基础验证之前绑定该决策。

## 8. 隐私、AI 与许可证

### 8.1 默认数据承诺

商店描述和隐私政策应明确：

- 本地转录默认不上传音频、视频、模型或字幕；
- 配置、任务、日志和下载模型保存在本机；
- 用户主动启用在线 AI 时，所选字幕文本会发送到用户选择的 Provider；
- API Key 的存储位置、清除方式和第三方服务责任边界；
- 本地 OpenAI-compatible 地址可能由用户改为远程地址，隐私提示按实际 URL 类型显示。

### 8.2 在线 AI 明示授权

第一次向在线 Provider 发送字幕前，必须展示并记录用户的明确选择：

- 将发送什么；
- 发送给谁；
- 用于什么；
- 可能产生的费用；
- 如何撤销授权和清除 API Key。

需要增加“报告不当 AI 输出”入口，可打开支持邮件或项目 issue 表单，并带上不包含原始字幕正文的诊断信息。

### 8.3 第三方声明

商店包至少包含：

```text
LICENSE.txt
ThirdPartyNotices.txt
Models/MODEL-INFO.json
```

发布前逐项审核：

- Const-me Whisper / MPL 2.0；
- OpenAI Whisper 模型；
- whisper.cpp / ggml；
- WebView2 SDK/Runtime；
- CUDA Runtime（若包含）；
- React、Vite、i18next 及随包分发的 npm 依赖；
- 其他原生 DLL 和字体、图标、截图素材。

公开 GitHub 源码地址和准确提交号应写入关于页或许可证说明，满足上游通知和源码获取要求。

## 9. 商店元数据与素材

首发准备两套 listing：

```text
zh-CN
en-US
```

每套至少包含：

- 产品名称和简短描述；
- 完整描述；
- 3–8 条功能要点；
- 至少一张、建议四张对应语言的桌面截图；
- 最低与推荐硬件；
- 隐私政策 URL；
- 支持联系方式；
- 版权、许可证和第三方标识；
- 在线生成式 AI 披露；
- 首版认证人员的测试步骤。

产品描述不能继续沿用 README 中未经验证的绝对化宣传。应准确说明：

- 哪些功能离线；
- 哪些功能需要网络/API Key；
- 内置什么模型；
- CPU/D3D11/CUDA 的适用条件；
- 支持的 Windows 与处理器架构；
- 当前不支持的功能或硬件。

## 10. 可访问性与桌面质量

P0/P1 验证项：

- 键盘可以完成导航、选择模型、添加文件、开始与取消任务；
- 焦点状态清晰；
- 屏幕阅读器能读取主要按钮和状态；
- 高对比度和 200% 缩放不丢失核心功能；
- 窗口缩小后英语长文本不遮挡；
- 麦克风拒绝授权、设备拔出和无设备时显示可恢复错误；
- 没有模型、模型损坏、磁盘不足、断网和 AI 超时时保持响应；
- 关闭运行中任务时能够安全取消或明确确认。

## 11. 测试与认证矩阵

### 11.1 自动化检查

- `npm run build`；
- `dotnet run --project Tools/ModernUiTests/ModernUiTests.csproj`；
- `zh-CN` / `en-US` 资源键一致性测试；
- 英语模式中文硬编码扫描，维护明确白名单；
- MSIX 产物文件、版本、内置模型大小与哈希检查；
- `ThirdPartyNotices.txt` 和隐私 URL 存在性检查。

### 11.2 干净机器测试

至少覆盖：

| 环境 | 核心验证 |
|---|---|
| Windows 11 x64，无 .NET、无 NVIDIA | 安装、启动、内置模型 CPU 转录 |
| Windows 10 x64，无 WebView2 异常环境 | 缺失依赖提示或受支持安装路径 |
| NVIDIA 机器 | CUDA 检测、启用、失败回退 |
| 无麦克风/拒绝权限 | 页面可用、错误可恢复 |
| 无网络 | 本地转录可用，在线 AI 明确失败 |
| `zh-CN` 系统 | 默认中文，英语可切换 |
| `en-US` 系统 | 默认英语，无未翻译中文 |
| 商店版本升级 | 设置、任务和用户下载模型保留 |
| 卸载 | 包文件清理，用户创建的字幕不删除 |

最终候选包运行 Windows App Certification Kit，并保存结果报告和失败修正记录。

## 12. 里程碑

### M0：发行决策冻结

- 确认产品名和 Publisher；
- 确认最小 Windows 版本；
- 确认内置 `tiny` 还是 `base`；
- 确认首版是否包含 CUDA 与 `whisperctl`；
- 准备隐私政策和支持页面的公开 URL。

完成标准：关键决策写入本文，不再在实现阶段反复切换。

### M1：双语基础

- React i18n；
- WPF `.resx`；
- `uiLanguage` 配置和系统语言检测；
- 中文/英语完整迁移；
- 资源键测试和英语中文残留扫描。

完成标准：四个主要页面、原生错误和首次启动均可完整切换语言。

### M2：MSIX 最小闭环

- Packaging Project；
- 自包含 x64 publish；
- WebView2 可写数据目录；
- 图标、版本、身份和清单；
- 安装、启动、卸载 smoke test。

完成标准：干净 Windows 11 VM 无需预装 .NET 即可启动。

### M3：内置模型与离线首转录

- 内置多语言模型与元数据；
- 安全默认引擎和硬件检测；
- 首次启动自动加载；
- 示例或明确的添加文件流程；
- 模型损坏与回退测试。

完成标准：无网络、无 NVIDIA、无历史配置也能生成一份字幕。

### M4：合规与商店材料

- 隐私政策；
- 在线 AI 授权与撤销；
- AI 输出报告入口；
- 第三方许可证；
- 中文/英语 listing、截图和认证备注。

完成标准：Partner Center 所需文本、URL 和素材齐全。

### M5：候选版与首次提交

- 完成自动化和干净机器矩阵；
- 运行 WACK；
- 修复阻塞；
- 生成 `.msixupload`；
- 提交并记录认证结果。

完成标准：商店审核通过，或得到可复现、可修正的单一认证阻塞。

## 13. 工作量评估

在不重写 UI、不同时支持 ARM64 和七种语言的前提下：

| 工作包 | 估算 |
|---|---:|
| 双语资源体系与中文/英语迁移 | 3–5 个工作日 |
| MSIX、自包含运行时、WebView2 修正 | 2–4 个工作日 |
| 内置模型、首装和硬件回退 | 2–4 个工作日 |
| 隐私、AI 合规、许可证和 listing | 2–4 个工作日 |
| 干净机器、WACK 与首轮修正 | 2–4 个工作日 |

存在并行空间，但首次商店版仍应按 **约 10–15 个有效开发工作日** 规划，并为账户验证、商店审核和第一次退回修正预留额外日历时间。

## 14. 首版 Definition of Done

只有同时满足以下条件，才称为“Microsoft Store 首版完成”：

- [ ] `zh-CN` 和 `en-US` UI、原生错误、隐私与 listing 完整；
- [ ] UI 语言和转录语言互不干扰；
- [ ] `.msixupload` 可重复构建，版本和身份正确；
- [ ] 干净 Windows x64 无需预装 .NET 即可启动；
- [ ] WebView2 不写入包安装目录；
- [ ] 无网络、无 NVIDIA 时可使用内置模型完成离线转录；
- [ ] 麦克风、文件选择、输出和卸载行为符合预期；
- [ ] 在线 AI 有明示授权、撤销和报告入口；
- [ ] 模型及第三方组件的来源、许可证和哈希完整；
- [ ] 英语模式无未批准的中文残留；
- [ ] 自动化测试与 WACK 通过；
- [ ] 中文、英语截图和认证人员测试说明齐全。

## 15. 官方参考

- [微软 Win32 应用商店分发方式](https://learn.microsoft.com/en-us/windows/apps/distribute-through-store/how-to-distribute-your-win32-app-through-microsoft-store)
- [使用 Visual Studio 为桌面应用建立 MSIX](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-packaging-dot-net)
- [Microsoft Store Policies](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies)
- [MSIX 包要求](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements)
- [MSIX 多语言 Store listing](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/add-and-edit-store-listing-info)
- [导入和导出多语言 Store listing](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/import-and-export-store-listings)
- [WPF 全球化与本地化](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/wpf-globalization-and-localization-overview)
- [WebView2 User Data Folder](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/user-data-folder)
- [MSIX 桌面应用运行与文件系统行为](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes)
- [Windows App Certification Kit](https://learn.microsoft.com/en-us/windows/uwp/debug-test-perf/windows-app-certification-kit)
- [OpenAI Whisper LICENSE](https://github.com/openai/whisper/blob/main/LICENSE)
- [whisper.cpp GGML 模型列表与大小](https://huggingface.co/ggerganov/whisper.cpp)

