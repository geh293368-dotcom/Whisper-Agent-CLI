using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
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
    readonly List<CaptureDeviceOption> captureDevices = [];
    readonly IReadOnlyList<EngineOption> engines;
    readonly IReadOnlyList<LanguageOption> languages;
    readonly IReadOnlyList<OutputFormatOption> outputFormats;
    readonly IReadOnlyList<OutputLocationOption> outputLocations;

    ITranscriptionEngine transcription;
    iMediaFoundation? captureMediaFoundation;
    CancellationTokenSource? operationCancellation;
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
    bool translate;
    EngineOption selectedEngine;
    LanguageOption selectedLanguage;
    OutputFormatOption selectedOutputFormat;
    OutputLocationOption selectedOutputLocation;
    CaptureDeviceOption? selectedCaptureDevice;
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
            case "addFiles": AddFiles(); break;
            case "addFolder": AddFolder(); break;
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
        if (!File.Exists(modelPath) || isLoadingModel || isRunning)
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

    void AddFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "添加音频或视频文件",
            Filter = "媒体文件|*.wav;*.wave;*.mp3;*.wma;*.mp4;*.mpeg4;*.mkv;*.m4a;*.aac;*.flac|所有文件|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true)
            return;
        foreach (string path in dialog.FileNames)
            AddJob(path, Path.GetDirectoryName(path));
        QueueChanged();
    }

    void AddFolder()
    {
        var dialog = new OpenFolderDialog { Title = "选择要递归扫描的媒体目录" };
        if (dialog.ShowDialog(this) != true)
            return;

        string root = dialog.FolderName;
        int before = jobs.Count;
        try
        {
            foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => SupportedExtensions.Contains(Path.GetExtension(path))))
                AddJob(path, root);
        }
        catch (UnauthorizedAccessException ex)
        {
            AppendLog("扫描", $"部分目录无法访问：{ex.Message}");
        }
        QueueChanged();
        globalStatus = $"从 {root} 添加了 {jobs.Count - before} 个媒体文件";
        PublishState();
    }

    void AddJob(string path, string? sourceRoot)
    {
        if (jobs.Any(job => string.Equals(job.InputPath, path, StringComparison.OrdinalIgnoreCase)))
            return;
        jobs.Add(new TranscriptionJob { InputPath = path, SourceRoot = sourceRoot });
        RebuildSources();
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
                selectedLanguage = languages.FirstOrDefault(item => item.Id == value.GetString()) ?? selectedLanguage;
                if (selectedLanguage.Id == "en") translate = false;
                break;
            case "format": selectedOutputFormat = outputFormats.FirstOrDefault(item => item.Id == value.GetString()) ?? selectedOutputFormat; break;
            case "outputLocation": selectedOutputLocation = outputLocations.FirstOrDefault(item => item.Id == value.GetString()) ?? selectedOutputLocation; break;
            case "translate": translate = value.GetBoolean() && selectedLanguage.Id != "en"; break;
            case "captureDevice": selectedCaptureDevice = captureDevices.FirstOrDefault(item => item.Endpoint == value.GetString()); break;
            case "modelPath":
                modelPath = value.GetString() ?? string.Empty;
                AddToRecentModels(modelPath);
                break;
        }
        SaveConfig();
        PublishState();
    }

    void SwitchEngine(string? id)
    {
        EngineOption? next = engines.FirstOrDefault(item => item.Id == id);
        if (next is null || next == selectedEngine || isRunning || isLoadingModel)
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
        previewSegments.Clear();
        int completed = 0;
        int failed = 0;
        SendPatch(new
        {
            isRunning,
            canStart = CanStart,
            segments = Array.Empty<object>(),
            globalStatus,
        });

        try
        {
            foreach (TranscriptionJob job in jobs.Where(job => job.IsSelected).ToArray())
            {
                operationCancellation.Token.ThrowIfCancellationRequested();
                job.State = JobState.Running;
                job.StatusText = "转录中";
                job.Progress = 0;
                job.Error = null;
                globalStatus = $"正在处理 {job.FileName}";
                int segmentIndex = 0;
                SendJobUpdate(job);
                SendPatch(new { globalStatus, canStart = CanStart });

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
                    AppendLog(job.FileName, $"已输出到 {job.OutputPath}");
                }
                catch (OperationCanceledException)
                {
                    job.State = JobState.Canceled;
                    job.StatusText = "已取消";
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
                SendJobUpdate(job);
            }
            globalStatus = $"批量转录完成：成功 {completed}，失败 {failed}";
        }
        catch (OperationCanceledException)
        {
            globalStatus = $"任务已停止：成功 {completed}，失败 {failed}";
        }
        finally
        {
            isRunning = false;
            SendPatch(new { isRunning, globalStatus, canStart = CanStart });
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

    void RefreshCaptureDevices()
    {
        try
        {
            captureMediaFoundation ??= Library.initMediaFoundation();
            CaptureDeviceId[] devices = captureMediaFoundation.listCaptureDevices() ?? [];
            captureDevices.Clear();
            captureDevices.AddRange(devices.Select(device => new CaptureDeviceOption(device.displayName, device.endpoint)));
            selectedCaptureDevice = captureDevices.FirstOrDefault();
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
        globalStatus = $"{jobs.Count} 个文件，已选择 {jobs.Count(job => job.IsSelected)} 个";
        PublishState();
    }

    bool CanStart => transcription.IsModelLoaded && jobs.Any(job => job.IsSelected) && !isRunning && !isLoadingModel;

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
            isRunning,
            isLoadingModel,
            canStart = CanStart,
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
        job.Error,
        job.OutputPath,
    };

    void SendMessage(object message)
    {
        if (WebView?.CoreWebView2 is not null)
            WebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (isRunning || isLoadingModel)
        {
            MessageBoxResult result = MessageBox.Show(this, "模型或转录任务仍在运行，确定要退出吗？", "确认退出", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.No)
            {
                e.Cancel = true;
                return;
            }
            operationCancellation?.Cancel();
        }
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
    }

    sealed record HostCommand(string Action, JsonElement Payload);
}
