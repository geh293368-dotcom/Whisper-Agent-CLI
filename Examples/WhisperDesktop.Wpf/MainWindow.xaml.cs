using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
    readonly IReadOnlyList<EngineOption> engines;
    readonly IReadOnlyList<LanguageOption> languages;
    readonly IReadOnlyList<OutputFormatOption> outputFormats;
    readonly IReadOnlyList<OutputLocationOption> outputLocations;

    ITranscriptionEngine transcription;
    iMediaFoundation? captureMediaFoundation;
    CancellationTokenSource? operationCancellation;
    CancellationTokenSource? durationScanCancellation;
    CancellationTokenSource? liveCancellation;
    readonly LiveTranscriptionService liveTranscription = new();
    readonly DispatcherTimer liveTimer;
    string modelPath = string.Empty;
    string outputFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    string modelStatus = "尚未加载模型";
    string globalStatus = "准备就绪";
    string logOutput = string.Empty;
    string LogFilePath => AppLogger.CurrentLogPath;
    string liveStatus = "正在读取录音设备";
    double modelProgress;
    bool isRunning;
    bool isLoadingModel;
    bool isScanningMedia;
    bool isLiveRunning;
    bool isLivePreparing;
    bool liveVoiceDetected;
    bool liveTranscribing;
    bool liveStalled;
    bool translate;
    Stopwatch? batchStopwatch;
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

    public MainWindow()
    {
        engines =
        [
            new("cuda", "whisper.cpp 1.8.6（CUDA / NVIDIA，推荐）", () => new WhisperCppTranscriptionEngine("WhisperCppBackendCuda.dll")),
            new("cpu", "whisper.cpp 1.8.6（CPU，兼容）", () => new WhisperCppTranscriptionEngine("WhisperCppBackendCpu.dll")),
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

        liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        liveTimer.Tick += (_, _) => SendPatch(new { liveElapsedSeconds = liveStopwatch?.Elapsed.TotalSeconds ?? 0 });

        LoadConfig();
        transcription = selectedEngine.Create();
        logOutput = AppLogger.StartSession(selectedEngine.Name);

        InitializeComponent();
        Library.setLogSink(eLogLevel.Info, eLoggerFlags.SkipFormatMessage, OnNativeLog);
        AppendLog("应用", "Whisper Desktop 启动");
    }

    async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await WebView.EnsureCoreWebView2Async();
            WebView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            WebView.CoreWebView2.Settings.IsZoomControlEnabled = true;
            WebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

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
            case "initialize": PublishState(); break;
            case "chooseModel": ChooseModel(); break;
            case "loadModel": await LoadModelAsync(); break;
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

    async Task LoadModelAsync()
    {
        if (!File.Exists(modelPath) || isLoadingModel || isRunning || isLiveRunning || isLivePreparing)
            return;

        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        isLoadingModel = true;
        modelStatus = "正在加载模型";
        modelProgress = 0;
        globalStatus = "正在将模型载入内存与显存...";
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
        }
        catch (OperationCanceledException)
        {
            modelStatus = "模型加载已取消";
            globalStatus = "已取消";
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
            isLoadingModel = false;
            PublishState();
        }
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
                            AppendLog(job.FileName, "无法读取媒体时长，ETA 将忽略此文件", failure, "WARN");
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
        }
        finally
        {
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
            job.IsSelected = selected;
        RebuildSources();
        QueueChanged();
    }

    void SetSourceSelected(JsonElement payload)
    {
        string? path = payload.GetProperty("path").GetString();
        bool selected = payload.GetProperty("selected").GetBoolean();
        foreach (TranscriptionJob job in jobs.Where(item => string.Equals(item.SourceRoot, path, StringComparison.OrdinalIgnoreCase)))
            job.IsSelected = selected;
        RebuildSources();
        QueueChanged();
    }

    void SetAllSelected(bool selected)
    {
        foreach (TranscriptionJob job in jobs)
            job.IsSelected = selected;
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
        previewSegments.Clear();
        int completed = 0;
        int failed = 0;
        TranscriptionJob[] selectedJobs = jobs.Where(job => job.IsSelected).ToArray();
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
        AppendLog("批次", $"开始转录：{selectedJobs.Length} 个文件，总时长 {FormatDuration(TimeSpan.FromSeconds(selectedJobs.Sum(job => job.Duration?.TotalSeconds ?? 0)))}");
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
                        SendPatch(new { batchStatistics = CreateBatchStatistics() });
                    }
                });
                try
                {
                    string targetFolder = GetOutputFolder(job);
                    Directory.CreateDirectory(targetFolder);
                    job.OutputPath = await transcription.TranscribeAsync(
                        job.InputPath, targetFolder, selectedLanguage.Value, translate, selectedOutputFormat.Value,
                        progress,
                        segment => Dispatcher.BeginInvoke(() =>
                        {
                            previewSegments.Add(new SubtitlePreviewItem(++segmentIndex, job.FileName, segment.Begin, segment.End, segment.Text));
                            SendSegmentAdded(previewSegments[^1]);
                        }),
                        operationCancellation.Token);
                    job.Progress = 1;
                    job.State = JobState.Completed;
                    job.StatusText = "完成";
                    completed++;
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
            globalStatus = $"批量转录完成：成功 {completed}，失败 {failed}";
        }
        catch (OperationCanceledException)
        {
            globalStatus = $"任务已停止：成功 {completed}，失败 {failed}";
        }
        finally
        {
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
            batchStopwatch = null;
        globalStatus = $"{jobs.Count} 个文件，已选择 {jobs.Count(job => job.IsSelected)} 个";
        PublishState();
    }

    bool CanStart => transcription.IsModelLoaded && jobs.Any(job => job.IsSelected) && !isRunning && !isLoadingModel && !isScanningMedia && !isLiveRunning;

    bool CanStartLive => transcription.IsModelLoaded
        && File.Exists(modelPath)
        && selectedCaptureDevice is not null
        && !isRunning
        && !isLoadingModel
        && !isScanningMedia
        && !isLiveRunning
        && !isLivePreparing;

    object CreateBatchStatistics()
    {
        TranscriptionJob[] selected = jobs.Where(job => job.IsSelected).ToArray();
        int knownDurationCount = selected.Count(job => job.Duration is not null);
        double totalDurationSeconds = selected.Sum(job => job.Duration?.TotalSeconds ?? 0);
        double processedDurationSeconds = batchStopwatch is null
            ? 0
            : selected.Sum(job => job.State switch
            {
                JobState.Completed => job.Duration?.TotalSeconds ?? 0,
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
        double? etaSeconds = isRunning && knownDurationCount == selected.Length && speed > 0
            ? remainingDurationSeconds / speed
            : null;

        return new
        {
            selectedCount = selected.Length,
            knownDurationCount,
            totalDurationSeconds,
            processedDurationSeconds,
            elapsedSeconds,
            speed,
            etaSeconds,
        };
    }

    string FormatBatchSummary()
    {
        TranscriptionJob[] completed = jobs.Where(job => job.IsSelected && job.State == JobState.Completed).ToArray();
        TimeSpan elapsed = batchStopwatch?.Elapsed ?? TimeSpan.Zero;
        TimeSpan processed = TimeSpan.FromSeconds(completed.Sum(job => job.Duration?.TotalSeconds ?? 0));
        double speed = elapsed.TotalSeconds > 0 ? processed.TotalSeconds / elapsed.TotalSeconds : 0;
        return $"完成音频 {FormatDuration(processed)}；总耗时 {FormatDuration(elapsed)}；平均速度 {(speed > 0 ? $"{speed:F1}x" : "未知")}";
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
            selectedEngine = selectedEngine.Id,
            selectedLanguage = selectedLanguage.Id,
            selectedFormat = selectedOutputFormat.Id,
            selectedOutputLocation = selectedOutputLocation.Id,
            translate,
            modelPath,
            modelStatus,
            modelProgress,
            outputFolder,
            globalStatus,
            batchStatistics = CreateBatchStatistics(),
            isScanningMedia,
            isRunning,
            isLoadingModel,
            canStart = CanStart,
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
        if (isRunning || isLoadingModel || isScanningMedia || isLiveRunning || isLivePreparing)
        {
            MessageBoxResult result = MessageBox.Show(this, "模型、媒体扫描、批量任务或实时字幕仍在运行，确定要退出吗？", "确认退出", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.No)
            {
                e.Cancel = true;
                return;
            }
            operationCancellation?.Cancel();
            durationScanCancellation?.Cancel();
            liveCancellation?.Cancel();
        }
        liveTimer.Stop();
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
        try
        {
            string configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WhisperDesktop", "config.json");
            if (File.Exists(configPath))
            {
                string json = File.ReadAllText(configPath);
                var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (config is not null)
                {
                    selectedEngine = engines.FirstOrDefault(e => e.Id == config.SelectedEngine) ?? selectedEngine;
                    selectedLanguage = languages.FirstOrDefault(l => l.Id == config.SelectedLanguage) ?? selectedLanguage;
                    selectedOutputFormat = outputFormats.FirstOrDefault(o => o.Id == config.SelectedFormat) ?? selectedOutputFormat;
                    selectedOutputLocation = outputLocations.FirstOrDefault(ol => ol.Id == config.SelectedOutputLocation) ?? selectedOutputLocation;
                    outputFolder = config.OutputFolder;
                    translate = config.Translate;
                    modelPath = config.ModelPath;
                    recentModels = config.RecentModels ?? new();
                    selectedCaptureEndpoint = config.SelectedCaptureEndpoint;
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog("配置", "加载配置失败", ex, "ERROR");
        }
    }

    private void SaveConfig()
    {
        try
        {
            string configDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WhisperDesktop");
            Directory.CreateDirectory(configDirectory);
            string configPath = Path.Combine(configDirectory, "config.json");

            var config = new AppConfig
            {
                SelectedEngine = selectedEngine.Id,
                SelectedLanguage = selectedLanguage.Id,
                SelectedFormat = selectedOutputFormat.Id,
                SelectedOutputLocation = selectedOutputLocation.Id,
                OutputFolder = outputFolder,
                Translate = translate,
                ModelPath = modelPath,
                RecentModels = recentModels,
                SelectedCaptureEndpoint = selectedCaptureDevice?.Endpoint ?? selectedCaptureEndpoint,
            };

            string json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(configPath, json);
        }
        catch (Exception ex)
        {
            AppendLog("配置", "保存配置失败", ex, "ERROR");
        }
    }

    public sealed class AppConfig
    {
        public string SelectedEngine { get; set; } = "cuda";
        public string SelectedLanguage { get; set; } = "zh";
        public string SelectedFormat { get; set; } = "srt";
        public string SelectedOutputLocation { get; set; } = "selected";
        public string OutputFolder { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        public bool Translate { get; set; } = false;
        public string ModelPath { get; set; } = string.Empty;
        public List<string> RecentModels { get; set; } = new();
        public string? SelectedCaptureEndpoint { get; set; }
    }

    sealed record HostCommand(string Action, JsonElement Payload);
}
