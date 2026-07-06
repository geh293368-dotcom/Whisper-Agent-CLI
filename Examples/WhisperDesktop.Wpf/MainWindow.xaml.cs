using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Whisper;
using WhisperDesktop.Modern.Models;
using WhisperDesktop.Modern.Services;

namespace WhisperDesktop.Modern;

public sealed record LanguageOption(string Id, string Name, eLanguage Value);
public sealed record OutputFormatOption(string Id, string Name, OutputFormat Value);
public sealed record EngineOption(string Id, string Name, Func<ITranscriptionEngine> Create);
public sealed record OutputLocationOption(string Id, string Name, OutputLocationMode Value, string Description);
public sealed record AiModelProviderOption(string Id, string Name, string Description);
public sealed record GeminiModelOption(string Id, string Name);
public sealed record AiSubtitleOutputPolicyOption(string Id, string Name, AiSubtitleOutputPolicy Value, string Description);

public enum OutputLocationMode
{
    SelectedFolder,
    SourceFolder,
    PreserveStructure,
}

public partial class MainWindow : Window
{
    const int MaxInMemoryLogCharacters = 200_000;
    static readonly TimeSpan ProgressUpdateInterval = TimeSpan.FromMilliseconds(200);
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".wave", ".mp3", ".wma", ".mp4", ".mpeg4", ".mkv", ".m4a", ".aac", ".flac",
    };

    readonly List<TranscriptionJob> jobs = [];
    readonly List<SourceFolderItem> sourceFolders = [];
    readonly List<SubtitlePreviewItem> previewSegments = [];
    readonly List<LiveSubtitleItem> liveSegments = [];
    readonly List<CaptureDeviceOption> captureDevices = [];
    readonly AppSettingsStore settingsStore = new();
    readonly GeminiApiKeyStore geminiApiKeyStore = new();
    readonly GeminiModelClient geminiClient = new();
    readonly OpenAiCompatibleModelClient localAiClient = new();
    readonly AiSubtitleOptimizationService aiSubtitleService;
    readonly TerminologyService terminologyService = new();
    IReadOnlyList<TerminologyPack> terminologyPacks = [];
    readonly IReadOnlyList<EngineOption> engines;
    readonly IReadOnlyList<LanguageOption> languages;
    readonly IReadOnlyList<OutputFormatOption> outputFormats;
    readonly IReadOnlyList<OutputLocationOption> outputLocations;
    readonly IReadOnlyList<AiModelProviderOption> aiModelProviders =
    [
        new("gemini", "Gemini（在线）", "使用 Google Gemini API"),
        new("localOpenAi", "本地 OpenAI 兼容", "Ollama / llama.cpp / LM Studio"),
    ];
    readonly IReadOnlyList<GeminiModelOption> geminiModels =
    [
        new(GeminiModelClient.DefaultModel, "Gemini 3.1 Flash-Lite（低成本，推荐）"),
        new("gemini-3.5-flash", "Gemini 3.5 Flash（更强，成本更高）"),
        new("gemini-2.5-flash-lite", "Gemini 2.5 Flash-Lite（兼容备选）"),
    ];
    readonly IReadOnlyList<AiSubtitleOutputPolicyOption> aiSubtitleOutputPolicies =
    [
        new("overwriteBackup", "覆盖原字幕（保留备份）", AiSubtitleOutputPolicy.OverwriteWithBackup, "普通模式推荐，直接得到最终 .srt"),
        new("preserve", "保留原字幕，输出优化副本", AiSubtitleOutputPolicy.PreserveOriginal, "诊断或审校时使用，生成 .optimized.srt"),
    ];

    ITranscriptionEngine transcription;
    iMediaFoundation? captureMediaFoundation;
    CancellationTokenSource? operationCancellation;
    CancellationTokenSource? aiCancellation;
    CancellationTokenSource? durationScanCancellation;
    CancellationTokenSource? liveCancellation;
    readonly LiveTranscriptionService liveTranscription = new();
    readonly DispatcherTimer liveTimer;
    readonly DispatcherTimer batchStatisticsTimer;
    readonly DispatcherTimer modelLoadTimer;
    string modelPath = string.Empty;
    string outputFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    string modelStatus = "尚未加载模型";
    string globalStatus = "准备就绪";
    string logOutput = string.Empty;
    string LogFilePath => AppLogger.CurrentLogPath;
    string liveStatus = "正在读取录音设备";
    double modelProgress;
    double modelLoadElapsedSeconds;
    double smoothedBatchSpeed;
    double lastBatchProcessedSeconds;
    double lastBatchElapsedSeconds;
    bool isRunning;
    bool isLoadingModel;
    bool modelProgressIndeterminate;
    bool autoLoadModel = true;
    bool autoLoadAttempted;
    bool hasBatchSpeedSample;
    bool isScanningMedia;
    bool isLiveRunning;
    bool isLivePreparing;
    bool liveVoiceDetected;
    bool liveTranscribing;
    bool liveStalled;
    bool translate;
    string uiScale = "medium";
    string selectedAiModelProvider = "gemini";
    string selectedGeminiModel = GeminiModelClient.DefaultModel;
    string selectedLocalAiModel = OpenAiCompatibleModelClient.DefaultModel;
    string localAiBaseUrl = OpenAiCompatibleModelClient.DefaultBaseUrl;
    AiSubtitleOutputPolicy selectedAiSubtitleOutputPolicy = AiSubtitleOutputPolicy.OverwriteWithBackup;
    string geminiStatus = "未配置 API Key";
    string geminiSampleResult = string.Empty;
    string aiBatchStatus = "等待字幕优化任务";
    string aiBatchReportPath = string.Empty;
    string aiBatchSummary = string.Empty;
    bool terminologyEnabled;
    bool developerDiagnostics;
    bool terminologySelectionConfigured;
    bool isGeminiBusy;
    bool isAiRunning;
    Stopwatch? batchStopwatch;
    Stopwatch? modelLoadStopwatch;
    Stopwatch? liveStopwatch;
    double liveModelProgress;
    string? liveOutputPath;
    EngineOption selectedEngine;
    LanguageOption selectedLanguage;
    OutputFormatOption selectedOutputFormat;
    OutputLocationOption selectedOutputLocation;
    CaptureDeviceOption? selectedCaptureDevice;
    string? selectedCaptureEndpoint;
    List<string> recentModels = [];
    List<string> selectedTerminologyPackIds = [];
    AiSubtitleOutputPolicy EffectiveAiSubtitleOutputPolicy =>
        developerDiagnostics ? AiSubtitleOutputPolicy.PreserveOriginal : selectedAiSubtitleOutputPolicy;

    bool IsLocalAiProviderSelected => string.Equals(selectedAiModelProvider, "localOpenAi", StringComparison.OrdinalIgnoreCase);

    string SelectedAiModel => IsLocalAiProviderSelected ? selectedLocalAiModel : selectedGeminiModel;

    bool IsSelectedAiModelConfigured => IsLocalAiProviderSelected
        ? !string.IsNullOrWhiteSpace(localAiBaseUrl) && !string.IsNullOrWhiteSpace(selectedLocalAiModel)
        : geminiApiKeyStore.IsConfigured;

    public MainWindow()
    {
        engines =
        [
            new("cuda", "whisper.cpp 1.9.1（CUDA / NVIDIA，推荐）", () => new WhisperCppTranscriptionEngine("WhisperCppBackendCuda.dll")),
            new("cpu", "whisper.cpp 1.9.1（CPU，兼容）", () => new WhisperCppTranscriptionEngine("WhisperCppBackendCpu.dll")),
            new("d3d11", "WhisperDesktop D3D11（兼容版）", () => new TranscriptionService()),
        ];
        languages =
        [
            new("zh", "中文", eLanguage.Chinese), new("yue", "粤语", eLanguage.Cantonese),
            new("en", "英语", eLanguage.English), new("ja", "日语", eLanguage.Japanese),
            new("ko", "韩语", eLanguage.Korean), new("fr", "法语", eLanguage.French),
            new("de", "德语", eLanguage.German), new("es", "西班牙语", eLanguage.Spanish),
            new("ru", "俄语", eLanguage.Russian),
        ];
        outputFormats =
        [
            new("srt", "SubRip 字幕 (.srt)", OutputFormat.SubRip),
            new("vtt", "WebVTT 字幕 (.vtt)", OutputFormat.WebVtt),
            new("txt", "纯文本 (.txt)", OutputFormat.Text),
            new("timestamps", "带时间戳文本 (.txt)", OutputFormat.TextWithTimestamps),
        ];
        outputLocations =
        [
            new("selected", "统一输出目录", OutputLocationMode.SelectedFolder, "全部字幕写入指定目录"),
            new("source", "原文件旁", OutputLocationMode.SourceFolder, "字幕写入媒体文件所在目录"),
            new("preserve", "保留目录结构", OutputLocationMode.PreserveStructure, "在输出目录中保留导入文件夹的层级"),
        ];

        selectedEngine = engines[0];
        selectedLanguage = languages[0];
        selectedOutputFormat = outputFormats[0];
        selectedOutputLocation = outputLocations[0];
        aiSubtitleService = new AiSubtitleOptimizationService(GetSelectedAiModelClient);

        liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        liveTimer.Tick += (_, _) => SendPatch(new { liveElapsedSeconds = liveStopwatch?.Elapsed.TotalSeconds ?? 0 });
        batchStatisticsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        batchStatisticsTimer.Tick += (_, _) => SendPatch(new { batchStatistics = CreateBatchStatistics(updateEstimate: true) });
        modelLoadTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        modelLoadTimer.Tick += (_, _) =>
        {
            modelLoadElapsedSeconds = modelLoadStopwatch?.Elapsed.TotalSeconds ?? modelLoadElapsedSeconds;
            SendPatch(new { modelLoadElapsedSeconds });
        };

        LoadConfig();
        geminiStatus = CreateAiProviderStatus();
        transcription = selectedEngine.Create();
        logOutput = AppLogger.StartSession(selectedEngine.Name);
        LoadTerminologyPacks();

        InitializeComponent();
        Library.setLogSink(eLogLevel.Info, eLoggerFlags.SkipFormatMessage, OnNativeLog);
        AppendLog("应用", "Whisper Desktop 启动");
    }

    async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await WebView.EnsureCoreWebView2Async();
            CoreWebView2Settings settings = WebView.CoreWebView2.Settings;
#if DEBUG
            settings.AreDevToolsEnabled = true;
            settings.AreDefaultContextMenusEnabled = true;
            settings.IsZoomControlEnabled = true;
#else
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsZoomControlEnabled = false;
#endif
            WebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

#if DEBUG
            Uri? devServerUri = await TryGetWebDevServerUriAsync();
            if (devServerUri is not null)
            {
                AppendLog("WebView2", $"连接 Vite 开发服务器：{devServerUri}");
                WebView.Source = devServerUri;
                RefreshCaptureDevices();
                return;
            }
#endif

            string webRoot = Path.Combine(AppContext.BaseDirectory, "Web");
            if (!File.Exists(Path.Combine(webRoot, "index.html")))
                throw new FileNotFoundException("React 前端资源不存在，请重新编译项目。", Path.Combine(webRoot, "index.html"));

            WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "app.whisperdesktop",
                webRoot,
                CoreWebView2HostResourceAccessKind.DenyCors);
            WebView.Source = new Uri("https://app.whisperdesktop/index.html");
            RefreshCaptureDevices();
        }
        catch (Exception ex)
        {
            AppendLog("WebView2", "初始化失败", ex, "ERROR");
            MessageBox.Show(this, ex.Message, "WebView2 初始化失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

#if DEBUG
    static async Task<Uri?> TryGetWebDevServerUriAsync()
    {
        var uri = new Uri("http://localhost:5173/");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(400) };
            using HttpResponseMessage response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            return response.IsSuccessStatusCode ? uri : null;
        }
        catch
        {
            return null;
        }
    }
#endif

    async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            HostCommand? command = JsonSerializer.Deserialize<HostCommand>(e.WebMessageAsJson, JsonOptions);
            if (command is null || string.IsNullOrWhiteSpace(command.Action))
                return;
            await ExecuteCommandAsync(command);
        }
        catch (Exception ex)
        {
            AppendLog("界面命令", "处理 WebView2 消息失败", ex, "ERROR");
            SendError(ex.Message);
        }
    }

    async Task ExecuteCommandAsync(HostCommand command)
    {
        switch (command.Action)
        {
            case "initialize":
                PublishState();
                await TryAutoLoadModelAsync();
                break;
            case "chooseModel": ChooseModel(); break;
            case "loadModel": await LoadModelAsync(); break;
            case "cancelModelLoad": CancelModelLoad(); break;
            case "addFiles": await AddFilesAsync(); break;
            case "addFolder": await AddFolderAsync(); break;
            case "chooseOutputFolder": ChooseOutputFolder(); break;
            case "setJobSelected": SetJobSelected(command.Payload); break;
            case "setSourceSelected": SetSourceSelected(command.Payload); break;
            case "setAllSelected": SetAllSelected(command.Payload.ValueKind == JsonValueKind.True); break;
            case "removeSelectedRows": RemoveSelectedJobs(); break;
            case "clearJobs": ClearJobs(); break;
            case "updateSetting": UpdateSetting(command.Payload); break;
            case "start": await StartTranscriptionAsync(); break;
            case "stop": StopTranscription(); break;
            case "refreshDevices": RefreshCaptureDevices(); break;
            case "startLive": StartLiveTranscription(); break;
            case "stopLive": StopLiveTranscription(); break;
            case "clearLiveSegments": ClearLiveSegments(); break;
            case "exportLive": ExportLiveSegments(command.Payload); break;
            case "openLogFolder": OpenLogFolder(); break;
            case "openTerminologyFolder": OpenTerminologyFolder(); break;
            case "refreshTerminology": LoadTerminologyPacks(); PublishState(); break;
            case "setTerminologyPackSelected": SetTerminologyPackSelected(command.Payload); break;
            case "saveGeminiApiKey": SaveGeminiApiKey(command.Payload); break;
            case "clearGeminiApiKey": ClearGeminiApiKey(); break;
            case "testGeminiConnection":
            case "testAiModelConnection":
                await TestAiModelConnectionAsync();
                break;
            case "optimizeGeminiSample":
            case "optimizeAiSample":
                await OptimizeAiSampleAsync(command.Payload);
                break;
            case "startAiSubtitleOptimization": await StartAiSubtitleOptimizationAsync(); break;
            case "stopAiSubtitleOptimization": StopAiSubtitleOptimization(); break;
        }
    }

    void ChooseModel()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Whisper GGML 模型",
            Filter = "Whisper 模型 (*.bin)|*.bin|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            modelPath = dialog.FileName;
            AddToRecentModels(modelPath);
            SaveConfig();
            PublishState();
        }
    }

    async Task TryAutoLoadModelAsync()
    {
        if (autoLoadAttempted)
            return;
        autoLoadAttempted = true;

        if (!autoLoadModel || string.IsNullOrWhiteSpace(modelPath))
            return;
        if (!File.Exists(modelPath))
        {
            modelStatus = "上次使用的模型文件不存在";
            globalStatus = "请重新选择模型文件";
            AppendLog("模型", $"自动加载已跳过，文件不存在：{modelPath}", level: "WARN");
            PublishState();
            return;
        }

        await LoadModelAsync(automatic: true);
    }

    async Task LoadModelAsync(bool automatic = false)
    {
        if (!File.Exists(modelPath) || isLoadingModel || isRunning || isLiveRunning || isLivePreparing)
            return;

        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        isLoadingModel = true;
        modelStatus = "正在加载模型";
        modelProgress = 0;
        modelProgressIndeterminate = selectedEngine.Id is "cuda" or "cpu";
        modelLoadElapsedSeconds = 0;
        modelLoadStopwatch = Stopwatch.StartNew();
        modelLoadTimer.Start();
        globalStatus = "正在将模型载入内存与显存...";
        string loadMode = automatic ? "自动" : "手动";
        AppendLog("模型", $"开始{loadMode}加载 {Path.GetFileName(modelPath)}，引擎 {selectedEngine.Name}");
        PublishState();

        try
        {
            var progress = new Progress<double>(value =>
            {
                modelProgress = value;
                SendPatch(new { modelProgress });
            });
            await transcription.LoadModelAsync(modelPath, progress, operationCancellation.Token);
            modelProgress = 1;
            modelStatus = $"已加载 {Path.GetFileName(modelPath)}";
            globalStatus = "模型加载完成，可以开始转录";
            AppendLog("模型", $"{loadMode}加载完成，耗时 {modelLoadStopwatch.Elapsed.TotalSeconds:F1} 秒");
        }
        catch (OperationCanceledException)
        {
            modelStatus = "模型加载已取消";
            globalStatus = "已取消";
            AppendLog("模型", $"{loadMode}加载已取消，耗时 {modelLoadStopwatch.Elapsed.TotalSeconds:F1} 秒", level: "WARN");
        }
        catch (Exception ex)
        {
            modelStatus = "模型加载失败";
            globalStatus = ex.Message;
            AppendLog("模型", "模型加载失败", ex, "ERROR");
            SendError(ex.Message);
        }
        finally
        {
            modelLoadStopwatch.Stop();
            modelLoadElapsedSeconds = modelLoadStopwatch.Elapsed.TotalSeconds;
            modelLoadTimer.Stop();
            isLoadingModel = false;
            modelProgressIndeterminate = false;
            PublishState();
        }
    }

    void CancelModelLoad()
    {
        if (!isLoadingModel)
            return;
        globalStatus = "正在取消模型加载...";
        operationCancellation?.Cancel();
        SendPatch(new { globalStatus });
    }

    async Task AddFilesAsync()
    {
        if (isScanningMedia)
            return;
        var dialog = new OpenFileDialog
        {
            Title = "添加音频或视频文件",
            Filter = "媒体文件|*.wav;*.wave;*.mp3;*.wma;*.mp4;*.mpeg4;*.mkv;*.m4a;*.aac;*.flac|所有文件|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true)
            return;
        var added = new List<TranscriptionJob>();
        foreach (string path in dialog.FileNames)
        {
            TranscriptionJob? job = AddJob(path, Path.GetDirectoryName(path));
            if (job is not null)
                added.Add(job);
        }
        RebuildSources();
        QueueChanged();
        LogImportSummary("添加文件", added);
        await ProbeDurationsAsync(added);
    }

    async Task AddFolderAsync()
    {
        if (isScanningMedia)
            return;
        var dialog = new OpenFolderDialog { Title = "选择要递归扫描的媒体目录" };
        if (dialog.ShowDialog(this) != true)
            return;

        string root = dialog.FolderName;
        int before = jobs.Count;
        var added = new List<TranscriptionJob>();
        try
        {
            foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => SupportedExtensions.Contains(Path.GetExtension(path))))
            {
                TranscriptionJob? job = AddJob(path, root);
                if (job is not null)
                    added.Add(job);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            AppendLog("扫描", $"部分目录无法访问：{ex.Message}");
        }
        RebuildSources();
        globalStatus = $"从 {root} 添加了 {jobs.Count - before} 个媒体文件";
        LogImportSummary("添加文件夹", added, root);
        PublishState();
        await ProbeDurationsAsync(added);
    }

    TranscriptionJob? AddJob(string path, string? sourceRoot)
    {
        if (jobs.Any(job => string.Equals(job.InputPath, path, StringComparison.OrdinalIgnoreCase)))
            return null;
        var job = new TranscriptionJob { InputPath = path, SourceRoot = sourceRoot };
        jobs.Add(job);
        return job;
    }

    async Task ProbeDurationsAsync(IReadOnlyList<TranscriptionJob> addedJobs)
    {
        if (addedJobs.Count == 0)
            return;

        isScanningMedia = true;
        Stopwatch scanStopwatch = Stopwatch.StartNew();
        durationScanCancellation?.Dispose();
        durationScanCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = durationScanCancellation.Token;
        globalStatus = $"正在读取媒体时长：0 / {addedJobs.Count}";
        PublishState();

        try
        {
            await Task.Run(async () =>
            {
                using iMediaFoundation mediaFoundation = Library.initMediaFoundation();
                for (int index = 0; index < addedJobs.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TranscriptionJob job = addedJobs[index];
                    TimeSpan? duration = null;
                    Exception? failure = null;
                    try
                    {
                        using iAudioReader reader = mediaFoundation.openAudioFile(job.InputPath);
                        duration = reader.getDuration();
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }

                    int completed = index + 1;
                    cancellationToken.ThrowIfCancellationRequested();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        job.Duration = duration;
                        if (failure is not null)
                        {
                            MarkJobSkipped(job, "未检测到可用音频轨道", "无法读取音频，已跳过", failure);
                        }
                        globalStatus = $"正在读取媒体时长：{completed} / {addedJobs.Count}";
                        SendJobUpdate(job);
                        SendPatch(new { globalStatus, batchStatistics = CreateBatchStatistics() });
                    });
                }
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            globalStatus = "媒体时长读取已取消";
            AppendLog("媒体扫描", $"读取已取消，已耗时 {scanStopwatch.Elapsed.TotalSeconds:F1} 秒", level: "WARN");
        }
        finally
        {
            scanStopwatch.Stop();
            if (!cancellationToken.IsCancellationRequested)
                LogDurationScanSummary(addedJobs, scanStopwatch.Elapsed);
            isScanningMedia = false;
            durationScanCancellation?.Dispose();
            durationScanCancellation = null;
            QueueChanged();
        }
    }

    void SetJobSelected(JsonElement payload)
    {
        string? id = payload.GetProperty("id").GetString();
        bool selected = payload.GetProperty("selected").GetBoolean();
        TranscriptionJob? job = jobs.FirstOrDefault(item => item.InputPath == id);
        if (job is not null)
            job.IsSelected = selected && job.State != JobState.Skipped;
        RebuildSources();
        QueueChanged();
    }

    void SetSourceSelected(JsonElement payload)
    {
        string? path = payload.GetProperty("path").GetString();
        bool selected = payload.GetProperty("selected").GetBoolean();
        foreach (TranscriptionJob job in jobs.Where(item => string.Equals(item.SourceRoot, path, StringComparison.OrdinalIgnoreCase)))
            job.IsSelected = selected && job.State != JobState.Skipped;
        RebuildSources();
        QueueChanged();
    }

    void SetAllSelected(bool selected)
    {
        foreach (TranscriptionJob job in jobs)
            job.IsSelected = selected && job.State != JobState.Skipped;
        RebuildSources();
        QueueChanged();
    }

    void RemoveSelectedJobs()
    {
        jobs.RemoveAll(job => job.IsSelected);
        RebuildSources();
        QueueChanged();
    }

    void ClearJobs()
    {
        jobs.Clear();
        sourceFolders.Clear();
        previewSegments.Clear();
        globalStatus = "队列已清空";
        PublishState();
    }

    void RebuildSources()
    {
        sourceFolders.Clear();
        foreach (IGrouping<string, TranscriptionJob> group in jobs.Where(job => !string.IsNullOrWhiteSpace(job.SourceRoot))
            .GroupBy(job => job.SourceRoot!, StringComparer.OrdinalIgnoreCase))
        {
            sourceFolders.Add(new SourceFolderItem
            {
                Path = group.Key,
                FileCount = group.Count(),
                IsSelected = group.All(job => job.IsSelected),
            });
        }
    }

    void ChooseOutputFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择字幕输出目录",
            InitialDirectory = Directory.Exists(outputFolder) ? outputFolder : null,
        };
        if (dialog.ShowDialog(this) == true)
        {
            outputFolder = dialog.FolderName;
            SaveConfig();
            PublishState();
        }
    }

    void UpdateSetting(JsonElement payload)
    {
        string? name = payload.GetProperty("name").GetString();
        JsonElement value = payload.GetProperty("value");
        switch (name)
        {
            case "engine": SwitchEngine(value.GetString()); break;
            case "language":
                if (!isRunning && !isLiveRunning && !isLivePreparing)
                {
                    selectedLanguage = languages.FirstOrDefault(item => item.Id == value.GetString()) ?? selectedLanguage;
                    if (selectedLanguage.Id == "en") translate = false;
                }
                break;
            case "format": selectedOutputFormat = outputFormats.FirstOrDefault(item => item.Id == value.GetString()) ?? selectedOutputFormat; break;
            case "outputLocation": selectedOutputLocation = outputLocations.FirstOrDefault(item => item.Id == value.GetString()) ?? selectedOutputLocation; break;
            case "translate":
                if (!isRunning && !isLiveRunning && !isLivePreparing)
                    translate = value.GetBoolean() && selectedLanguage.Id != "en";
                break;
            case "uiScale":
                uiScale = NormalizeUiScale(value.GetString());
                break;
            case "aiModelProvider":
                selectedAiModelProvider = NormalizeAiModelProvider(value.GetString());
                geminiStatus = CreateAiProviderStatus();
                break;
            case "geminiModel":
                selectedGeminiModel = NormalizeGeminiModel(value.GetString());
                break;
            case "localAiBaseUrl":
                localAiBaseUrl = NormalizeLocalAiBaseUrl(value.GetString());
                geminiStatus = CreateAiProviderStatus();
                break;
            case "localAiModel":
                selectedLocalAiModel = NormalizeLocalAiModel(value.GetString());
                geminiStatus = CreateAiProviderStatus();
                break;
            case "aiSubtitleOutputPolicy":
                selectedAiSubtitleOutputPolicy = NormalizeAiSubtitleOutputPolicy(value.GetString());
                break;
            case "terminologyEnabled":
                terminologyEnabled = value.GetBoolean();
                break;
            case "developerDiagnostics":
                developerDiagnostics = value.GetBoolean();
                AppendLog("开发者诊断", developerDiagnostics ? "已开启诊断模式预留开关" : "已关闭诊断模式预留开关");
                break;
            case "autoLoadModel":
                autoLoadModel = value.GetBoolean();
                break;
            case "captureDevice":
                if (!isLiveRunning && !isLivePreparing)
                {
                    selectedCaptureDevice = captureDevices.FirstOrDefault(item => item.Endpoint == value.GetString());
                    selectedCaptureEndpoint = selectedCaptureDevice?.Endpoint;
                }
                break;
            case "modelPath":
                if (!isRunning && !isLoadingModel && !isLiveRunning && !isLivePreparing)
                {
                    modelPath = value.GetString() ?? string.Empty;
                    AddToRecentModels(modelPath);
                }
                break;
        }
        SaveConfig();
        PublishState();
    }

    void SaveGeminiApiKey(JsonElement payload)
    {
        string? apiKey = payload.TryGetProperty("apiKey", out JsonElement value) ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            SendError("Gemini API Key 不能为空。");
            return;
        }

        geminiApiKeyStore.Save(apiKey);
        geminiStatus = "API Key 已保存";
        AppendLog("Gemini", "API Key 已保存到 Windows 凭据管理器");
        PublishState();
    }

    void ClearGeminiApiKey()
    {
        geminiApiKeyStore.Clear();
        geminiStatus = CreateAiProviderStatus();
        geminiSampleResult = string.Empty;
        AppendLog("Gemini", "API Key 已清除");
        PublishState();
    }

    async Task TestAiModelConnectionAsync()
    {
        if (isGeminiBusy)
            return;

        if (!IsSelectedAiModelConfigured)
        {
            SendError(CreateAiConfigurationMessage());
            return;
        }

        IAiSubtitleModelClient modelClient = GetSelectedAiModelClient();
        string apiKey = GetSelectedAiApiKey();
        string model = SelectedAiModel;
        isGeminiBusy = true;
        geminiStatus = "正在测试连接...";
        SendPatch(CreateGeminiPatch());
        try
        {
            GeminiTestResult result = await modelClient.TestConnectionAsync(apiKey, model, CancellationToken.None);
            geminiStatus = result.Ok ? $"连接成功：{result.Message}" : $"连接返回异常：{result.Message}";
            AppendLog(modelClient.ProviderDisplayName, $"连接测试完成：{geminiStatus}");
        }
        catch (Exception ex)
        {
            geminiStatus = "连接测试失败";
            AppendLog(modelClient.ProviderDisplayName, "连接测试失败", ex, "ERROR");
            SendError(ex.Message);
        }
        finally
        {
            isGeminiBusy = false;
            SendPatch(CreateGeminiPatch());
        }
    }

    async Task OptimizeAiSampleAsync(JsonElement payload)
    {
        if (isGeminiBusy)
            return;

        string? sourceText = payload.TryGetProperty("text", out JsonElement value) ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            SendError("请输入一条字幕文本用于试跑。");
            return;
        }

        if (!IsSelectedAiModelConfigured)
        {
            SendError(CreateAiConfigurationMessage());
            return;
        }

        IAiSubtitleModelClient modelClient = GetSelectedAiModelClient();
        string apiKey = GetSelectedAiApiKey();
        string model = SelectedAiModel;
        isGeminiBusy = true;
        geminiStatus = "正在优化试跑字幕...";
        geminiSampleResult = string.Empty;
        SendPatch(CreateGeminiPatch());
        try
        {
            SubtitleOptimizationResult result = await modelClient.OptimizeSubtitleTextAsync(
                apiKey,
                model,
                sourceText.Trim(),
                selectedLanguage.Name,
                CancellationToken.None);
            geminiSampleResult = string.IsNullOrWhiteSpace(result.Notes)
                ? result.OptimizedText
                : $"{result.OptimizedText}\n\n说明：{result.Notes}";
            geminiStatus = "字幕优化试跑完成";
            AppendLog(modelClient.ProviderDisplayName, "字幕优化试跑完成");
        }
        catch (Exception ex)
        {
            geminiStatus = "字幕优化试跑失败";
            AppendLog(modelClient.ProviderDisplayName, "字幕优化试跑失败", ex, "ERROR");
            SendError(ex.Message);
        }
        finally
        {
            isGeminiBusy = false;
            SendPatch(CreateGeminiPatch());
        }
    }

    object CreateGeminiPatch() => new
    {
        selectedAiModelProvider,
        selectedGeminiModel,
        selectedLocalAiModel,
        localAiBaseUrl,
        geminiApiKeyConfigured = geminiApiKeyStore.IsConfigured,
        geminiStatus,
        isGeminiBusy,
        geminiSampleResult,
        isAiModelConfigured = IsSelectedAiModelConfigured,
    };

    IAiSubtitleModelClient GetSelectedAiModelClient()
    {
        if (!IsLocalAiProviderSelected)
            return geminiClient;

        localAiClient.BaseUrl = localAiBaseUrl;
        return localAiClient;
    }

    string GetSelectedAiApiKey() => IsLocalAiProviderSelected
        ? OpenAiCompatibleModelClient.DefaultApiKey
        : geminiApiKeyStore.Read() ?? string.Empty;

    string CreateAiProviderStatus()
    {
        if (IsLocalAiProviderSelected)
            return IsSelectedAiModelConfigured
                ? $"本地 AI 已配置：{selectedLocalAiModel}"
                : "请配置本地 AI Base URL 和模型名";

        return geminiApiKeyStore.IsConfigured ? "API Key 已配置" : "未配置 API Key";
    }

    string CreateAiConfigurationMessage() => IsLocalAiProviderSelected
        ? $"请确认 Ollama 已运行，并填写本地 AI Base URL 和模型名。默认 Base URL 是 {OpenAiCompatibleModelClient.DefaultBaseUrl}"
        : "请先在“模型与设置”中保存 Gemini API Key。";

    async Task StartAiSubtitleOptimizationAsync()
    {
        if (!CanStartAiSubtitleOptimization)
            return;

        if (!IsSelectedAiModelConfigured)
        {
            SendError(CreateAiConfigurationMessage());
            return;
        }

        IAiSubtitleModelClient modelClient = GetSelectedAiModelClient();
        string apiKey = GetSelectedAiApiKey();
        string aiModel = SelectedAiModel;
        TranscriptionJob[] selectedJobs = jobs
            .Where(job => job.IsSelected && File.Exists(job.InputPath))
            .ToArray();
        if (selectedJobs.Length == 0)
        {
            SendError("请先选择需要优化评分的视频任务。");
            return;
        }

        List<AiSubtitleInputPair> pairs = AiSubtitleOptimizationService.DiscoverPairs(selectedJobs.Select(job => job.InputPath));
        if (pairs.Count == 0)
        {
            SendError("没有找到同名 SRT 字幕。请确认视频旁边存在同名 .srt 文件。");
            return;
        }

        aiCancellation?.Dispose();
        aiCancellation = new CancellationTokenSource();
        isAiRunning = true;
        aiBatchStatus = $"正在准备 AI 字幕优化：{pairs.Count} 个文件";
        aiBatchReportPath = string.Empty;
        aiBatchSummary = string.Empty;
        string aiOperationName = developerDiagnostics ? "优化并评分" : "优化";
        AiSubtitleOutputPolicy outputPolicy = EffectiveAiSubtitleOutputPolicy;
        AppendLog("AI 字幕", $"开始{aiOperationName} {pairs.Count} 个字幕文件，Provider {modelClient.ProviderDisplayName}，模型 {aiModel}，输出策略 {FormatAiSubtitleOutputPolicy(outputPolicy)}");
        SendPatch(CreateAiSubtitlePatch());

        try
        {
            ActiveTerminology activeTerminology = terminologyEnabled
                ? terminologyService.BuildActiveTerminology(terminologyPacks, selectedTerminologyPackIds)
                : new ActiveTerminology([], null, 0, []);
            var progress = new Progress<AiSubtitleProgress>(value =>
            {
                aiBatchStatus = value.Message;
                if (!string.IsNullOrWhiteSpace(value.ReportPath))
                    aiBatchReportPath = value.ReportPath;
                SendPatch(CreateAiSubtitlePatch());
            });
            AiSubtitleBatchReport report = await aiSubtitleService.OptimizeAndEvaluateAsync(
                selectedJobs.Select(job => job.InputPath).ToArray(),
                apiKey,
                aiModel,
                selectedLanguage.Name,
                activeTerminology.InitialPrompt ?? string.Empty,
                outputPolicy,
                progress,
                aiCancellation.Token);
            aiBatchStatus = report.FailedCount == 0
                ? $"AI 字幕优化完成：{report.FileCount} 个文件，总成本约 ¥{report.EstimatedCostCny:F4}"
                : $"AI 字幕优化完成：{report.FileCount} 个成功，{report.FailedCount} 个失败，总成本约 ¥{report.EstimatedCostCny:F4}";
            aiBatchReportPath = report.ReportMarkdownPath;
            int totalTextCharacters = report.Reports.Sum(item => item.Statistics.TextCharCount);
            int totalRequestCount = report.Reports.Sum(item => item.Statistics.TotalRequestCount);
            int totalRetryCount = report.Reports.Sum(item => item.Statistics.RetryCount) + report.Failures.Sum(item => item.RetryCount);
            aiBatchSummary = $"正文 {totalTextCharacters} 字，请求 {totalRequestCount} 次，重试 {totalRetryCount} 次，失败 {report.FailedCount} 个，Token {report.Usage.TotalTokens}，输入 {report.Usage.PromptTokens}，输出 {report.Usage.OutputTokens}";
            AppendLog("AI 字幕", $"{aiBatchStatus}；报告 {report.ReportMarkdownPath}");
            AppendAiSubtitleReportToLog(report);
        }
        catch (OperationCanceledException)
        {
            aiBatchStatus = "AI 字幕优化已取消";
            AppendLog("AI 字幕", "AI 字幕优化已取消", level: "WARN");
        }
        catch (Exception ex)
        {
            aiBatchStatus = "AI 字幕优化失败";
            AppendLog("AI 字幕", $"AI 字幕优化失败：{ex.Message}", ex, "ERROR");
            SendError(ex.Message);
        }
        finally
        {
            isAiRunning = false;
            SendPatch(CreateAiSubtitlePatch());
        }
    }

    void AppendAiSubtitleReportToLog(AiSubtitleBatchReport report)
    {
        double beforeAverage = report.Reports.Count == 0 ? 0 : report.Reports.Average(item => item.OverallScoreBefore);
        double afterAverage = report.Reports.Count == 0 ? 0 : report.Reports.Average(item => item.OverallScoreAfter);
        int totalTextCharacters = report.Reports.Sum(item => item.Statistics.TextCharCount);
        int totalRequestCount = report.Reports.Sum(item => item.Statistics.TotalRequestCount);
        int totalRetryCount = report.Reports.Sum(item => item.Statistics.RetryCount) + report.Failures.Sum(item => item.RetryCount);
        long totalElapsedMs = report.Reports.Sum(item => item.Statistics.TotalElapsedMs);
        AppendLog(
            "AI 字幕",
            $"汇总：成功 {report.FileCount} 个，失败 {report.FailedCount} 个，正文 {totalTextCharacters} 字，请求 {totalRequestCount} 次，重试 {totalRetryCount} 次，耗时 {FormatCompactElapsed(totalElapsedMs)}，平均评分 {beforeAverage:F1} -> {afterAverage:F1}，Token {report.Usage.TotalTokens}（输入 {report.Usage.PromptTokens} / 输出 {report.Usage.OutputTokens}），估算费用 ¥{report.EstimatedCostCny:F4}");

        foreach (AiSubtitleFileReport item in report.Reports)
        {
            string name = Path.GetFileNameWithoutExtension(item.OriginalSubtitlePath);
            AppendLog(
                "AI 字幕",
                $"{name}：正文 {item.Statistics.TextCharCount} 字，{item.CueCount} 条，请求 {item.Statistics.TotalRequestCount} 次，重试 {item.Statistics.RetryCount} 次，耗时 {FormatCompactElapsed(item.Statistics.TotalElapsedMs)}，速度 {item.Statistics.CharsPerSecond:F1} 字/秒，评分 {item.OverallScoreBefore} -> {item.OverallScoreAfter}，修改 {item.ChangedCueCount}/{item.CueCount}，输出 {FormatAiSubtitleOutputPolicy(item.OutputPolicy)}，Token {item.Usage.TotalTokens}，费用 ¥{item.EstimatedCostCny:F4}，报告 {item.ReportMarkdownPath}");

            if (!string.IsNullOrWhiteSpace(item.BackupSubtitlePath))
                AppendLog("AI 字幕", $"{name} 原字幕备份：{item.BackupSubtitlePath}");

            if (!string.IsNullOrWhiteSpace(item.Summary))
                AppendLog("AI 字幕", $"{name} 摘要：{TrimLogText(item.Summary, 180)}");

            foreach (SubtitleQualityExample example in item.Examples.Take(3))
            {
                string reason = string.IsNullOrWhiteSpace(example.Reason) ? "模型未给出原因" : example.Reason;
                AppendLog(
                    "AI 字幕",
                    $"{name} 示例 #{example.Index}：{TrimLogText(reason, 120)}；原文「{TrimLogText(example.Before, 80)}」=> 优化「{TrimLogText(example.After, 80)}」");
            }

            if (item.Risks.Count > 0)
                AppendLog("AI 字幕", $"{name} 风险：{TrimLogText(string.Join("；", item.Risks.Take(3)), 180)}", level: "WARN");
        }

        foreach (AiSubtitleFailureReport failure in report.Failures)
        {
            string name = Path.GetFileNameWithoutExtension(failure.SubtitlePath);
            string chunk = failure.FailedChunkIndex is null ? "" : $"，分块 {failure.FailedChunkIndex}";
            AppendLog(
                "AI 字幕",
                $"{name}：失败，阶段 {failure.Stage}{chunk}，类型 {FormatGeminiErrorCategory(failure.ErrorCategory)}，重试 {failure.RetryCount} 次，错误 {TrimLogText(failure.ErrorMessage, 180)}",
                level: "WARN");
        }
    }

    static string TrimLogText(string value, int maxLength)
    {
        string normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..Math.Max(0, maxLength - 1)] + "…";
    }

    static string FormatCompactElapsed(long milliseconds)
    {
        TimeSpan elapsed = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    static string FormatGeminiErrorCategory(GeminiErrorCategory category) => category switch
    {
        GeminiErrorCategory.UserConfiguration => "配置错误",
        GeminiErrorCategory.RateLimited => "限流",
        GeminiErrorCategory.TemporaryService => "临时服务错误",
        GeminiErrorCategory.Network => "网络错误",
        GeminiErrorCategory.Timeout => "请求超时",
        GeminiErrorCategory.Content => "返回内容错误",
        _ => "未知错误",
    };

    void StopAiSubtitleOptimization()
    {
        if (!isAiRunning)
            return;
        aiBatchStatus = "正在取消 AI 字幕优化...";
        aiCancellation?.Cancel();
        SendPatch(CreateAiSubtitlePatch());
    }

    object CreateAiSubtitlePatch() => new
    {
        isAiRunning,
        canStartAiSubtitleOptimization = CanStartAiSubtitleOptimization,
        aiBatchStatus,
        aiBatchReportPath,
        aiBatchSummary,
    };

    void SetTerminologyPackSelected(JsonElement payload)
    {
        string? id = payload.GetProperty("id").GetString();
        bool selected = payload.GetProperty("selected").GetBoolean();
        if (string.IsNullOrWhiteSpace(id))
            return;

        selectedTerminologyPackIds.RemoveAll(item => string.Equals(item, id, StringComparison.OrdinalIgnoreCase));
        if (selected)
            selectedTerminologyPackIds.Add(id);
        terminologySelectionConfigured = true;
        SaveConfig();
        PublishState();
    }

    void SwitchEngine(string? id)
    {
        EngineOption? next = engines.FirstOrDefault(item => item.Id == id);
        if (next is null || next == selectedEngine || isRunning || isLoadingModel || isLiveRunning || isLivePreparing)
            return;
        transcription.Dispose();
        selectedEngine = next;
        transcription = next.Create();
        modelStatus = "尚未加载模型";
        modelProgress = 0;
        globalStatus = $"已切换到 {next.Name}";
    }

    async Task StartTranscriptionAsync()
    {
        if (!CanStart)
            return;
        if (selectedOutputLocation.Value != OutputLocationMode.SourceFolder && !Directory.Exists(outputFolder))
        {
            SendError("请选择有效的输出目录。");
            return;
        }

        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        isRunning = true;
        batchStopwatch = Stopwatch.StartNew();
        smoothedBatchSpeed = 0;
        lastBatchProcessedSeconds = 0;
        lastBatchElapsedSeconds = 0;
        hasBatchSpeedSample = false;
        batchStatisticsTimer.Start();
        previewSegments.Clear();
        int completed = 0;
        int failed = 0;
        int skipped = jobs.Count(job => job.IsSelected && job.State == JobState.Skipped);
        TranscriptionJob[] selectedJobs = jobs.Where(job => job.IsSelected && job.State != JobState.Skipped).ToArray();
        ActiveTerminology activeTerminology = terminologyEnabled
            ? terminologyService.BuildActiveTerminology(terminologyPacks, selectedTerminologyPackIds)
            : new ActiveTerminology([], null, 0, []);
        foreach (TranscriptionJob job in selectedJobs)
        {
            job.Elapsed = null;
            if (job.State is JobState.Completed or JobState.Failed or JobState.Canceled)
            {
                job.State = JobState.Pending;
                job.StatusText = "等待中";
                job.Progress = 0;
            }
        }
        string outputLocation = selectedOutputLocation.Value == OutputLocationMode.SourceFolder
            ? selectedOutputLocation.Name
            : $"{selectedOutputLocation.Name}（{outputFolder}）";
        AppendLog("批次",
            $"开始转录：{selectedJobs.Length} 个文件，已跳过 {skipped} 个，总时长 {FormatDuration(TimeSpan.FromSeconds(selectedJobs.Sum(job => job.Duration?.TotalSeconds ?? 0)))}；" +
            $"总大小 {FormatFileSize(SumFileSize(selectedJobs))}；格式 {selectedOutputFormat.Name}；输出 {outputLocation}；引擎 {selectedEngine.Name}；语言 {selectedLanguage.Name}；翻译 {(translate ? "开启" : "关闭")}；术语词库 {(terminologyEnabled ? "开启" : "关闭")}");
        if (terminologyEnabled)
        {
            AppendLog("术语词库",
                $"启用 {activeTerminology.Packs.Count} 个词库，注入 {activeTerminology.PromptTermCount} 个术语，加载 {activeTerminology.Corrections.Count} 条纠错规则");
        }
        SendPatch(new
        {
            isRunning,
            canStart = CanStart,
            segments = Array.Empty<object>(),
            globalStatus,
            batchStatistics = CreateBatchStatistics(),
        });

        try
        {
            foreach (TranscriptionJob job in selectedJobs)
            {
                operationCancellation.Token.ThrowIfCancellationRequested();
                job.State = JobState.Running;
                job.StatusText = "转录中";
                job.Progress = 0;
                job.Error = null;
                globalStatus = $"正在处理 {job.FileName}";
                int segmentIndex = 0;
                Stopwatch jobStopwatch = Stopwatch.StartNew();
                SendJobUpdate(job);
                SendPatch(new { globalStatus, canStart = CanStart, batchStatistics = CreateBatchStatistics() });

                DateTime lastProgressUpdate = DateTime.MinValue;
                var progress = new Progress<double>(value =>
                {
                    job.Progress = value;
                    DateTime now = DateTime.UtcNow;
                    if (value >= 1 || now - lastProgressUpdate >= ProgressUpdateInterval)
                    {
                        lastProgressUpdate = now;
                        SendJobUpdate(job);
                    }
                });
                try
                {
                    string targetFolder = GetOutputFolder(job);
                    Directory.CreateDirectory(targetFolder);
                    TranscriptionResult result = await transcription.TranscribeAsync(
                        job.InputPath, targetFolder, selectedLanguage.Value, translate, selectedOutputFormat.Value,
                        activeTerminology.InitialPrompt,
                        activeTerminology.Corrections,
                        progress,
                        segment => Dispatcher.BeginInvoke(() =>
                        {
                            previewSegments.Add(new SubtitlePreviewItem(++segmentIndex, job.FileName, segment.Begin, segment.End, segment.Text));
                            SendSegmentAdded(previewSegments[^1]);
                        }),
                        operationCancellation.Token);
                    job.Progress = 1;
                    if (result.SegmentCount == 0 || string.IsNullOrWhiteSpace(result.OutputPath))
                    {
                        MarkJobSkipped(job, "未识别到语音内容", "转录完成但没有可输出的字幕，已跳过");
                        skipped++;
                    }
                    else
                    {
                        job.OutputPath = result.OutputPath;
                        job.State = JobState.Completed;
                        job.StatusText = "完成";
                        completed++;
                        if (result.CorrectionCount > 0)
                            AppendLog(job.FileName, $"术语词库自动修正 {result.CorrectionCount} 处");
                    }
                }
                catch (OperationCanceledException)
                {
                    job.State = JobState.Canceled;
                    job.StatusText = "已取消";
                    jobStopwatch.Stop();
                    job.Elapsed = jobStopwatch.Elapsed;
                    SendJobUpdate(job);
                    SendPatch(new { batchStatistics = CreateBatchStatistics() });
                    throw;
                }
                catch (Exception ex)
                {
                    job.State = JobState.Failed;
                    job.StatusText = "失败";
                    job.Error = ex.Message;
                    failed++;
                    AppendLog(job.FileName, "转录失败", ex, "ERROR");
                }
                finally
                {
                    jobStopwatch.Stop();
                    job.Elapsed = jobStopwatch.Elapsed;
                }
                if (job.State == JobState.Completed)
                {
                    TimeSpan elapsed = job.Elapsed ?? TimeSpan.Zero;
                    double speed = elapsed.TotalSeconds > 0 && job.Duration is not null
                        ? job.Duration.Value.TotalSeconds / elapsed.TotalSeconds
                        : 0;
                    AppendLog(job.FileName,
                        $"已输出到 {job.OutputPath}；视频时长 {FormatDuration(job.Duration)}；耗时 {FormatDuration(job.Elapsed)}；速度 {(speed > 0 ? $"{speed:F1}x" : "未知")}");
                }
                SendJobUpdate(job);
                SendPatch(new { batchStatistics = CreateBatchStatistics() });
            }
            globalStatus = $"批量转录完成：成功 {completed}，跳过 {skipped}，失败 {failed}";
        }
        catch (OperationCanceledException)
        {
            globalStatus = $"任务已停止：成功 {completed}，跳过 {skipped}，失败 {failed}";
        }
        finally
        {
            batchStatisticsTimer.Stop();
            batchStopwatch?.Stop();
            isRunning = false;
            object statistics = CreateBatchStatistics();
            AppendLog("批次", $"{globalStatus}；{FormatBatchSummary()}");
            SendPatch(new { isRunning, globalStatus, canStart = CanStart, batchStatistics = statistics });
        }
    }

    string GetOutputFolder(TranscriptionJob job) => selectedOutputLocation.Value switch
    {
        OutputLocationMode.SourceFolder => Path.GetDirectoryName(job.InputPath) ?? outputFolder,
        OutputLocationMode.PreserveStructure when !string.IsNullOrWhiteSpace(job.SourceRoot) =>
            Path.Combine(outputFolder, Path.GetFileName(job.SourceRoot.TrimEnd(Path.DirectorySeparatorChar)),
                Path.GetDirectoryName(job.RelativePath) ?? string.Empty),
        _ => outputFolder,
    };

    void StopTranscription()
    {
        globalStatus = "正在停止当前任务...";
        operationCancellation?.Cancel();
        SendPatch(new { globalStatus });
    }

    void StartLiveTranscription()
    {
        if (!CanStartLive)
        {
            SendError(!transcription.IsModelLoaded
                ? "请先在“模型与设置”中加载模型。"
                : "请选择可用的麦克风后再开始实时字幕。");
            return;
        }

        liveCancellation?.Dispose();
        liveCancellation = new CancellationTokenSource();
        liveSegments.Clear();
        liveOutputPath = null;
        liveModelProgress = 0;
        liveVoiceDetected = false;
        liveTranscribing = false;
        liveStalled = false;
        isLivePreparing = true;
        isLiveRunning = true;
        liveStatus = "正在准备实时识别模型...";
        liveStopwatch = Stopwatch.StartNew();
        liveTimer.Start();

        CaptureDeviceOption device = selectedCaptureDevice!;
        AppendLog("实时字幕", $"会话开始：设备 {device.Name}；语言 {selectedLanguage.Id}；模式 均衡");
        SendPatch(new
        {
            isLiveRunning,
            isLivePreparing,
            canStart = CanStart,
            canStartLive = CanStartLive,
            liveStatus,
            liveModelProgress,
            liveElapsedSeconds = 0,
            liveVoiceDetected,
            liveTranscribing,
            liveStalled,
            liveSegments = Array.Empty<object>(),
            liveOutputPath,
        });

        _ = RunLiveTranscriptionAsync(device, liveCancellation.Token);
    }

    async Task RunLiveTranscriptionAsync(CaptureDeviceOption device, CancellationToken cancellationToken)
    {
        int errors = 0;
        try
        {
            var progress = new Progress<double>(value =>
            {
                liveModelProgress = value;
                SendPatch(new { liveModelProgress });
            });

            await liveTranscription.RunAsync(
                modelPath,
                device.Endpoint,
                selectedLanguage.Value,
                translate,
                progress,
                status => Dispatcher.BeginInvoke(() => UpdateLiveCaptureStatus(status)),
                segment => Dispatcher.BeginInvoke(() => AddLiveSegment(segment)),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            liveStatus = "实时字幕已停止";
        }
        catch (Exception ex)
        {
            errors++;
            liveStatus = $"实时字幕失败：{ex.Message}";
            AppendLog("实时字幕", "实时字幕会话失败", ex, "ERROR");
            SendError(ex.Message);
        }
        finally
        {
            liveStopwatch?.Stop();
            liveTimer.Stop();
            isLiveRunning = false;
            isLivePreparing = false;
            liveVoiceDetected = false;
            liveTranscribing = false;
            liveStalled = false;
            TimeSpan elapsed = liveStopwatch?.Elapsed ?? TimeSpan.Zero;
            AppendLog("实时字幕", $"会话结束：时长 {FormatDuration(elapsed)}；字幕 {liveSegments.Count} 条；错误 {errors}");
            SendPatch(new
            {
                isLiveRunning,
                isLivePreparing,
                canStart = CanStart,
                canStartLive = CanStartLive,
                liveStatus,
                liveElapsedSeconds = elapsed.TotalSeconds,
                liveVoiceDetected,
                liveTranscribing,
                liveStalled,
            });
        }
    }

    void UpdateLiveCaptureStatus(eCaptureStatus status)
    {
        isLivePreparing = false;
        liveVoiceDetected = status.HasFlag(eCaptureStatus.Voice);
        liveTranscribing = status.HasFlag(eCaptureStatus.Transcribing);
        liveStalled = status.HasFlag(eCaptureStatus.Stalled);
        liveStatus = liveStalled
            ? "处理速度不足，部分音频可能被跳过"
            : liveTranscribing && liveVoiceDetected
                ? "正在识别，同时继续聆听"
                : liveTranscribing
                    ? "正在识别刚才的语音"
                    : liveVoiceDetected
                        ? "检测到说话，正在收集语音"
                        : "正在聆听";

        SendPatch(new
        {
            isLivePreparing,
            liveStatus,
            liveVoiceDetected,
            liveTranscribing,
            liveStalled,
            liveModelProgress = 1,
        });
    }

    void AddLiveSegment(LiveTranscriptionSegment segment)
    {
        if (string.IsNullOrWhiteSpace(segment.Text))
            return;

        var item = new LiveSubtitleItem(liveSegments.Count + 1, segment.Begin, segment.End, segment.Text);
        liveSegments.Add(item);
        SendMessage(new
        {
            type = "liveSegmentAdded",
            payload = SerializeLiveSegment(item),
        });
    }

    void StopLiveTranscription()
    {
        if (!isLiveRunning)
            return;
        liveStatus = "正在停止实时字幕...";
        liveCancellation?.Cancel();
        SendPatch(new { liveStatus });
    }

    void ClearLiveSegments()
    {
        liveSegments.Clear();
        liveOutputPath = null;
        SendPatch(new { liveSegments = Array.Empty<object>(), liveOutputPath });
    }

    void ExportLiveSegments(JsonElement payload)
    {
        if (liveSegments.Count == 0)
        {
            SendError("当前没有可以导出的实时字幕。");
            return;
        }

        string format = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("format", out JsonElement value)
            ? value.GetString() ?? "srt"
            : "srt";
        bool srt = string.Equals(format, "srt", StringComparison.OrdinalIgnoreCase);
        var dialog = new SaveFileDialog
        {
            Title = "导出实时字幕",
            Filter = srt ? "SubRip 字幕 (*.srt)|*.srt" : "文本文件 (*.txt)|*.txt",
            DefaultExt = srt ? ".srt" : ".txt",
            FileName = $"实时字幕-{DateTime.Now:yyyyMMdd-HHmmss}.{(srt ? "srt" : "txt")}",
            InitialDirectory = Directory.Exists(outputFolder) ? outputFolder : null,
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var builder = new StringBuilder();
        foreach (LiveSubtitleItem segment in liveSegments)
        {
            if (srt)
            {
                builder.AppendLine(segment.Index.ToString());
                builder.Append(FormatSrtTime(segment.Begin)).Append(" --> ").AppendLine(FormatSrtTime(segment.End));
                builder.AppendLine(segment.Text).AppendLine();
            }
            else
            {
                builder.Append('[').Append(segment.BeginText).Append("] ").AppendLine(segment.Text);
            }
        }

        File.WriteAllText(dialog.FileName, builder.ToString(), new UTF8Encoding(false));
        liveOutputPath = dialog.FileName;
        AppendLog("实时字幕", $"已导出到 {liveOutputPath}");
        SendPatch(new { liveOutputPath });
    }

    static string FormatSrtTime(TimeSpan value) =>
        $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00},{value.Milliseconds:000}";

    void RefreshCaptureDevices()
    {
        if (isLiveRunning || isLivePreparing)
            return;
        try
        {
            captureMediaFoundation ??= Library.initMediaFoundation();
            CaptureDeviceId[] devices = captureMediaFoundation.listCaptureDevices() ?? [];
            string? previousEndpoint = selectedCaptureDevice?.Endpoint ?? selectedCaptureEndpoint;
            captureDevices.Clear();
            captureDevices.AddRange(devices.Select(device => new CaptureDeviceOption(device.displayName, device.endpoint)));
            selectedCaptureDevice = captureDevices.FirstOrDefault(device => device.Endpoint == previousEndpoint)
                ?? captureDevices.FirstOrDefault();
            selectedCaptureEndpoint = selectedCaptureDevice?.Endpoint;
            liveStatus = devices.Length == 0 ? "未发现可用麦克风" : $"已发现 {devices.Length} 个录音设备";
        }
        catch (Exception ex)
        {
            liveStatus = $"无法读取录音设备：{ex.Message}";
            AppendLog("录音设备", "读取设备失败", ex, "ERROR");
        }
        PublishState();
    }

    void QueueChanged()
    {
        if (!isRunning)
        {
            batchStopwatch = null;
            smoothedBatchSpeed = 0;
            lastBatchProcessedSeconds = 0;
            lastBatchElapsedSeconds = 0;
            hasBatchSpeedSample = false;
        }
        globalStatus = $"{jobs.Count} 个文件，已选择 {jobs.Count(job => job.IsSelected)} 个";
        PublishState();
    }

    void LogImportSummary(string source, IReadOnlyList<TranscriptionJob> addedJobs, string? root = null)
    {
        string rootText = string.IsNullOrWhiteSpace(root) ? string.Empty : $"，来源 {root}";
        TimeSpan queueDuration = TimeSpan.FromSeconds(jobs.Sum(job => job.Duration?.TotalSeconds ?? 0));
        int queueUnknownDuration = jobs.Count(job => job.Duration is null);
        AppendLog("任务导入",
            $"{source}完成：新增 {addedJobs.Count} 个文件，队列共 {jobs.Count} 个，已选 {jobs.Count(job => job.IsSelected)} 个，" +
            $"新增大小 {FormatFileSize(SumFileSize(addedJobs))}，队列总大小 {FormatFileSize(SumFileSize(jobs))}，" +
            $"已知总时长 {FormatDuration(queueDuration)}，未知时长 {queueUnknownDuration} 个{rootText}");
    }

    void LogDurationScanSummary(IReadOnlyList<TranscriptionJob> scannedJobs, TimeSpan elapsed)
    {
        int knownDurationCount = scannedJobs.Count(job => job.Duration is not null);
        int unknownDurationCount = scannedJobs.Count - knownDurationCount;
        TimeSpan totalDuration = TimeSpan.FromSeconds(scannedJobs.Sum(job => job.Duration?.TotalSeconds ?? 0));
        AppendLog("媒体扫描",
            $"扫描完成：成功读取 {knownDurationCount} 个，未知 {unknownDurationCount} 个，" +
            $"可识别总时长 {FormatDuration(totalDuration)}，文件大小 {FormatFileSize(SumFileSize(scannedJobs))}，耗时 {elapsed.TotalSeconds:F1} 秒");
    }

    bool CanStart => transcription.IsModelLoaded && jobs.Any(job => job.IsSelected && job.State != JobState.Skipped) && !isRunning && !isLoadingModel && !isScanningMedia && !isLiveRunning && !isAiRunning;

    bool CanStartAiSubtitleOptimization => IsSelectedAiModelConfigured
        && jobs.Any(job => job.IsSelected && File.Exists(Path.ChangeExtension(job.InputPath, ".srt")))
        && !isRunning
        && !isLoadingModel
        && !isScanningMedia
        && !isLiveRunning
        && !isLivePreparing
        && !isAiRunning;

    bool CanStartLive => transcription.IsModelLoaded
        && File.Exists(modelPath)
        && selectedCaptureDevice is not null
        && !isRunning
        && !isLoadingModel
        && !isScanningMedia
        && !isLiveRunning
        && !isLivePreparing
        && !isAiRunning;

    object CreateBatchStatistics(bool updateEstimate = false)
    {
        TranscriptionJob[] selected = jobs.Where(job => job.IsSelected).ToArray();
        int knownDurationCount = selected.Count(job => job.Duration is not null);
        int unknownDurationCount = selected.Length - knownDurationCount;
        double totalDurationSeconds = selected.Sum(job => job.Duration?.TotalSeconds ?? 0);
        double processedDurationSeconds = batchStopwatch is null
            ? 0
            : selected.Sum(job => job.State switch
            {
                JobState.Completed => job.Duration?.TotalSeconds ?? 0,
                JobState.Skipped => job.Duration?.TotalSeconds ?? 0,
                JobState.Running => (job.Duration?.TotalSeconds ?? 0) * Math.Clamp(job.Progress, 0, 1),
                _ => 0,
            });
        double remainingDurationSeconds = selected.Sum(job => job.State switch
        {
            JobState.Pending => job.Duration?.TotalSeconds ?? 0,
            JobState.Running => (job.Duration?.TotalSeconds ?? 0) * (1 - Math.Clamp(job.Progress, 0, 1)),
            _ => 0,
        });
        double elapsedSeconds = batchStopwatch?.Elapsed.TotalSeconds ?? 0;
        double speed = elapsedSeconds > 0 ? processedDurationSeconds / elapsedSeconds : 0;
        if (updateEstimate && isRunning)
        {
            double deltaElapsed = elapsedSeconds - lastBatchElapsedSeconds;
            double deltaProcessed = processedDurationSeconds - lastBatchProcessedSeconds;
            if (deltaElapsed > 0 && deltaProcessed > 0)
            {
                double sampleSpeed = deltaProcessed / deltaElapsed;
                smoothedBatchSpeed = hasBatchSpeedSample
                    ? smoothedBatchSpeed * 0.8 + sampleSpeed * 0.2
                    : sampleSpeed;
                hasBatchSpeedSample = true;
            }
            lastBatchElapsedSeconds = elapsedSeconds;
            lastBatchProcessedSeconds = processedDurationSeconds;
        }

        double estimateSpeed = hasBatchSpeedSample ? smoothedBatchSpeed : speed;
        bool hasEnoughSamples = elapsedSeconds >= 10
            || processedDurationSeconds >= 120
            || selected.Any(job => job.State == JobState.Completed && job.Duration is not null);
        double? etaSeconds = isRunning && hasEnoughSamples && remainingDurationSeconds > 0 && estimateSpeed > 0
            ? remainingDurationSeconds / estimateSpeed
            : null;
        string? estimatedCompletionTime = etaSeconds is not null
            ? DateTimeOffset.Now.AddSeconds(etaSeconds.Value).ToString("O")
            : null;

        return new
        {
            selectedCount = selected.Length,
            knownDurationCount,
            unknownDurationCount,
            totalDurationSeconds,
            processedDurationSeconds,
            elapsedSeconds,
            speed,
            etaSeconds,
            estimatedCompletionTime,
            etaIsPartial = unknownDurationCount > 0,
            isEstimating = isRunning && remainingDurationSeconds > 0 && etaSeconds is null,
        };
    }

    string FormatBatchSummary()
    {
        TranscriptionJob[] completed = jobs.Where(job => job.IsSelected && job.State == JobState.Completed).ToArray();
        int skipped = jobs.Count(job => job.State == JobState.Skipped);
        TimeSpan elapsed = batchStopwatch?.Elapsed ?? TimeSpan.Zero;
        TimeSpan processed = TimeSpan.FromSeconds(completed.Sum(job => job.Duration?.TotalSeconds ?? 0));
        double speed = elapsed.TotalSeconds > 0 ? processed.TotalSeconds / elapsed.TotalSeconds : 0;
        return $"完成音频 {FormatDuration(processed)}；跳过 {skipped} 个；总耗时 {FormatDuration(elapsed)}；平均速度 {(speed > 0 ? $"{speed:F1}x" : "未知")}";
    }

    void MarkJobSkipped(TranscriptionJob job, string reason, string logMessage, Exception? exception = null)
    {
        job.State = JobState.Skipped;
        job.StatusText = "已跳过";
        job.Progress = 1;
        job.Error = reason;
        job.IsSelected = false;
        AppendLog(job.FileName, logMessage, exception, "WARN");
    }

    static string FormatDuration(TimeSpan? value)
    {
        if (value is null)
            return "未知";
        TimeSpan duration = value.Value;
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes:00}:{duration.Seconds:00}";
    }

    static long SumFileSize(IEnumerable<TranscriptionJob> items) => items.Sum(job => GetFileSize(job.InputPath));

    static long GetFileSize(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:F0} {units[unit]}" : $"{value:F1} {units[unit]}";
    }

    void AppendLog(string source, string text, Exception? exception = null, string level = "INFO")
    {
        string line = AppLogger.Write(source, text, exception, level);
        logOutput += line;
        if (logOutput.Length > MaxInMemoryLogCharacters)
            logOutput = logOutput[^MaxInMemoryLogCharacters..];
        SendMessage(new { type = "logAdded", payload = new { line } });
    }

    void OpenLogFolder()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{LogFilePath}\"") { UseShellExecute = true });
    }

    void OpenTerminologyFolder()
    {
        Directory.CreateDirectory(terminologyService.DirectoryPath);
        Process.Start(new ProcessStartInfo("explorer.exe", terminologyService.DirectoryPath) { UseShellExecute = true });
    }

    void LoadTerminologyPacks()
    {
        terminologyPacks = terminologyService.LoadPacks((message, exception) => AppendLog("术语词库", message, exception, "WARN"));
        if (!terminologySelectionConfigured)
        {
            selectedTerminologyPackIds = terminologyPacks
                .Where(pack => pack.Enabled)
                .Select(pack => pack.Id)
                .ToList();
        }
    }

    void OnNativeLog(eLogLevel level, string message) => Dispatcher.BeginInvoke(() => AppendLog(level.ToString(), message));

    void PublishState()
    {
        if (WebView.CoreWebView2 is null)
            return;

        var state = new
        {
            engines = engines.Select(item => new { item.Id, item.Name }),
            languages = languages.Select(item => new { item.Id, item.Name }),
            formats = outputFormats.Select(item => new { item.Id, item.Name }),
            outputLocations = outputLocations.Select(item => new { item.Id, item.Name, item.Description }),
            aiModelProviders = aiModelProviders.Select(item => new { item.Id, item.Name, item.Description }),
            geminiModels = geminiModels.Select(item => new { item.Id, item.Name }),
            aiSubtitleOutputPolicies = aiSubtitleOutputPolicies.Select(item => new { item.Id, item.Name, item.Description }),
            selectedEngine = selectedEngine.Id,
            selectedLanguage = selectedLanguage.Id,
            selectedFormat = selectedOutputFormat.Id,
            selectedOutputLocation = selectedOutputLocation.Id,
            translate,
            buildTime = BuildInfo.BuildTime,
            modelPath,
            modelStatus,
            modelProgress,
            modelProgressIndeterminate,
            modelLoadElapsedSeconds,
            autoLoadModel,
            uiScale,
            selectedAiModelProvider,
            selectedGeminiModel,
            selectedLocalAiModel,
            localAiBaseUrl,
            selectedAiSubtitleOutputPolicy = GetAiSubtitleOutputPolicyId(selectedAiSubtitleOutputPolicy),
            effectiveAiSubtitleOutputPolicy = GetAiSubtitleOutputPolicyId(EffectiveAiSubtitleOutputPolicy),
            geminiApiKeyConfigured = geminiApiKeyStore.IsConfigured,
            geminiStatus,
            isGeminiBusy,
            geminiSampleResult,
            isAiModelConfigured = IsSelectedAiModelConfigured,
            terminologyEnabled,
            developerDiagnostics,
            terminologyDirectory = terminologyService.DirectoryPath,
            terminologyPacks = terminologyPacks.Select(pack => new
            {
                pack.Id,
                pack.Name,
                pack.Description,
                pack.Enabled,
                pack.Priority,
                termCount = pack.Terms.Count,
                terms = pack.Terms
                    .Where(term => term.Enabled && !string.IsNullOrWhiteSpace(term.Text))
                    .OrderByDescending(term => term.Priority)
                    .Take(40)
                    .Select(term => new
                    {
                        term.Text,
                        term.Category,
                        aliases = term.Aliases,
                        corrections = term.Corrections,
                    }),
                selected = selectedTerminologyPackIds.Contains(pack.Id, StringComparer.OrdinalIgnoreCase),
                pack.FilePath,
            }),
            outputFolder,
            globalStatus,
            batchStatistics = CreateBatchStatistics(),
            isScanningMedia,
            isRunning,
            isLoadingModel,
            canStart = CanStart,
            canStartAiSubtitleOptimization = CanStartAiSubtitleOptimization,
            isAiRunning,
            aiBatchStatus,
            aiBatchReportPath,
            aiBatchSummary,
            canStartLive = CanStartLive,
            jobs = jobs.Select(SerializeJob),
            sources = sourceFolders.Select(source => new
            {
                source.Path, source.Name, source.FileCount, selected = source.IsSelected,
            }),
            segments = previewSegments.Select(segment => new
            {
                segment.Index, segment.FileName, begin = segment.BeginText, end = segment.EndText, segment.Text,
            }),
            logs = logOutput,
            logFilePath = LogFilePath,
            captureDevices = captureDevices.Select(device => new { id = device.Endpoint, name = device.Name }),
            selectedCaptureDevice = selectedCaptureDevice?.Endpoint,
            liveStatus,
            isLiveRunning,
            isLivePreparing,
            liveVoiceDetected,
            liveTranscribing,
            liveStalled,
            liveModelProgress,
            liveElapsedSeconds = liveStopwatch?.Elapsed.TotalSeconds ?? 0,
            liveSegments = liveSegments.Select(SerializeLiveSegment),
            liveOutputPath,
            recentModels = recentModels.ToArray(),
        };
        SendMessage(new { type = "state", payload = state });
    }

    void SendError(string message) => SendMessage(new { type = "error", payload = new { message } });

    void SendPatch(object patch) => SendMessage(new { type = "patch", payload = patch });

    void SendJobUpdate(TranscriptionJob job) => SendMessage(new
    {
        type = "jobUpdate",
        payload = SerializeJob(job),
    });

    void SendSegmentAdded(SubtitlePreviewItem segment) => SendMessage(new
    {
        type = "segmentAdded",
        payload = new
        {
            segment.Index,
            segment.FileName,
            begin = segment.BeginText,
            end = segment.EndText,
            segment.Text,
        },
    });

    static object SerializeJob(TranscriptionJob job) => new
    {
        id = job.InputPath,
        job.FileName,
        job.InputPath,
        job.RelativePath,
        job.SourceRoot,
        selected = job.IsSelected,
        status = job.StatusText,
        job.Progress,
        durationSeconds = job.Duration?.TotalSeconds,
        elapsedSeconds = job.Elapsed?.TotalSeconds,
        job.Error,
        job.OutputPath,
    };

    static object SerializeLiveSegment(LiveSubtitleItem segment) => new
    {
        segment.Index,
        begin = segment.BeginText,
        end = segment.EndText,
        segment.Text,
    };

    void SendMessage(object message)
    {
        if (WebView?.CoreWebView2 is not null)
            WebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (isRunning || isLoadingModel || isScanningMedia || isLiveRunning || isLivePreparing || isAiRunning)
        {
            MessageBoxResult result = MessageBox.Show(this, "模型、媒体扫描、批量任务或实时字幕仍在运行，确定要退出吗？", "确认退出", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.No)
            {
                e.Cancel = true;
                return;
            }
            operationCancellation?.Cancel();
            aiCancellation?.Cancel();
            durationScanCancellation?.Cancel();
            liveCancellation?.Cancel();
        }
        liveTimer.Stop();
        batchStatisticsTimer.Stop();
        modelLoadTimer.Stop();
        transcription.Dispose();
        captureMediaFoundation?.Dispose();
    }

    private void AddToRecentModels(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        recentModels.Remove(path);
        recentModels.Insert(0, path);
        if (recentModels.Count > 10)
        {
            recentModels.RemoveAt(recentModels.Count - 1);
        }
    }

    private void LoadConfig()
    {
        AppSettingsStore.AppConfig? config = settingsStore.Load((message, exception) =>
            AppendLog("配置", message, exception, "ERROR"));
        if (config is null)
            return;

        selectedEngine = engines.FirstOrDefault(e => e.Id == config.SelectedEngine) ?? selectedEngine;
        selectedLanguage = languages.FirstOrDefault(l => l.Id == config.SelectedLanguage) ?? selectedLanguage;
        selectedOutputFormat = outputFormats.FirstOrDefault(o => o.Id == config.SelectedFormat) ?? selectedOutputFormat;
        selectedOutputLocation = outputLocations.FirstOrDefault(ol => ol.Id == config.SelectedOutputLocation) ?? selectedOutputLocation;
        if (!string.IsNullOrWhiteSpace(config.OutputFolder))
            outputFolder = config.OutputFolder;
        translate = config.Translate;
        modelPath = config.ModelPath;
        autoLoadModel = config.AutoLoadModel;
        uiScale = NormalizeUiScale(config.UiScale);
        selectedAiModelProvider = NormalizeAiModelProvider(config.AiModelProvider);
        selectedGeminiModel = NormalizeGeminiModel(config.GeminiModel);
        selectedLocalAiModel = NormalizeLocalAiModel(config.LocalAiModel);
        localAiBaseUrl = NormalizeLocalAiBaseUrl(config.LocalAiBaseUrl);
        selectedAiSubtitleOutputPolicy = NormalizeAiSubtitleOutputPolicy(config.AiSubtitleOutputPolicy);
        recentModels = config.RecentModels ?? [];
        selectedCaptureEndpoint = config.SelectedCaptureEndpoint;
        terminologyEnabled = config.TerminologyEnabled;
        developerDiagnostics = config.DeveloperDiagnostics;
        if (config.SelectedTerminologyPacks is not null)
        {
            selectedTerminologyPackIds = config.SelectedTerminologyPacks.ToList();
            terminologySelectionConfigured = true;
        }
    }

    private void SaveConfig()
    {
        var config = new AppSettingsStore.AppConfig
        {
            SelectedEngine = selectedEngine.Id,
            SelectedLanguage = selectedLanguage.Id,
            SelectedFormat = selectedOutputFormat.Id,
            SelectedOutputLocation = selectedOutputLocation.Id,
            OutputFolder = outputFolder,
            Translate = translate,
            ModelPath = modelPath,
            AutoLoadModel = autoLoadModel,
            UiScale = uiScale,
            AiModelProvider = selectedAiModelProvider,
            GeminiModel = selectedGeminiModel,
            LocalAiModel = selectedLocalAiModel,
            LocalAiBaseUrl = localAiBaseUrl,
            AiSubtitleOutputPolicy = GetAiSubtitleOutputPolicyId(selectedAiSubtitleOutputPolicy),
            RecentModels = recentModels.ToList(),
            SelectedCaptureEndpoint = selectedCaptureDevice?.Endpoint ?? selectedCaptureEndpoint,
            TerminologyEnabled = terminologyEnabled,
            DeveloperDiagnostics = developerDiagnostics,
            SelectedTerminologyPacks = selectedTerminologyPackIds.ToList(),
        };

        settingsStore.Save(config, (message, exception) =>
            AppendLog("配置", message, exception, "ERROR"));
    }

    static string NormalizeUiScale(string? value) => value switch
    {
        "small" or "medium" or "large" => value,
        _ => "medium",
    };

    static string NormalizeAiModelProvider(string? value) => value switch
    {
        "localOpenAi" => "localOpenAi",
        _ => "gemini",
    };

    static string NormalizeGeminiModel(string? value) => value switch
    {
        GeminiModelClient.DefaultModel or "gemini-3.5-flash" or "gemini-2.5-flash-lite" => value,
        _ => GeminiModelClient.DefaultModel,
    };

    static string NormalizeLocalAiModel(string? value) =>
        string.IsNullOrWhiteSpace(value) ? OpenAiCompatibleModelClient.DefaultModel : value.Trim();

    static string NormalizeLocalAiBaseUrl(string? value) =>
        string.IsNullOrWhiteSpace(value) ? OpenAiCompatibleModelClient.DefaultBaseUrl : value.Trim();

    static AiSubtitleOutputPolicy NormalizeAiSubtitleOutputPolicy(string? value) => value switch
    {
        "preserve" => AiSubtitleOutputPolicy.PreserveOriginal,
        "overwriteBackup" => AiSubtitleOutputPolicy.OverwriteWithBackup,
        _ => AiSubtitleOutputPolicy.OverwriteWithBackup,
    };

    static string GetAiSubtitleOutputPolicyId(AiSubtitleOutputPolicy policy) => policy switch
    {
        AiSubtitleOutputPolicy.PreserveOriginal => "preserve",
        _ => "overwriteBackup",
    };

    static string FormatAiSubtitleOutputPolicy(AiSubtitleOutputPolicy policy) => policy switch
    {
        AiSubtitleOutputPolicy.PreserveOriginal => "保留原字幕并输出 .optimized.srt",
        _ => "覆盖原字幕并保留备份",
    };

    sealed record HostCommand(string Action, JsonElement Payload);
}
