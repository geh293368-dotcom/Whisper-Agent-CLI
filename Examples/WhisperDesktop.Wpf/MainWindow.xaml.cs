using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using Whisper;
using WhisperDesktop.Modern.Models;
using WhisperDesktop.Modern.Services;

namespace WhisperDesktop.Modern;

public sealed record LanguageOption(string Name, eLanguage Value);
public sealed record OutputFormatOption(string Name, OutputFormat Value);
public sealed record EngineOption(string Name, Func<ITranscriptionEngine> Create);
public sealed record OutputLocationOption(string Name, OutputLocationMode Value, string Description);

public enum OutputLocationMode
{
    SelectedFolder,
    SourceFolder,
    PreserveStructure,
}

public partial class MainWindow : Window, INotifyPropertyChanged
{
    static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".wave", ".mp3", ".wma", ".mp4", ".mpeg4", ".mkv", ".m4a", ".aac", ".flac",
    };

    ITranscriptionEngine transcription;
    iMediaFoundation? captureMediaFoundation;
    CancellationTokenSource? operationCancellation;
    string modelPath = string.Empty;
    string outputFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    string modelStatus = "尚未加载模型";
    string globalStatus = "准备就绪";
    string logOutput = string.Empty;
    string liveStatus = "请选择麦克风并刷新设备列表";
    double modelProgress;
    bool isRunning;
    bool isLoadingModel;
    bool translate;
    bool syncingSourceSelection;
    LanguageOption selectedLanguage;
    OutputFormatOption selectedOutputFormat;
    EngineOption selectedEngine;
    OutputLocationOption selectedOutputLocation;
    CaptureDeviceOption? selectedCaptureDevice;

    public MainWindow()
    {
        Engines =
        [
            new("whisper.cpp 1.8.6（CUDA / NVIDIA，推荐）", () => new WhisperCppTranscriptionEngine("WhisperCppBackendCuda.dll")),
            new("whisper.cpp 1.8.6（CPU，兼容）", () => new WhisperCppTranscriptionEngine("WhisperCppBackendCpu.dll")),
            new("WhisperDesktop D3D11（兼容版）", () => new TranscriptionService()),
        ];
        Languages =
        [
            new("中文", eLanguage.Chinese),
            new("粤语", eLanguage.Cantonese),
            new("英语", eLanguage.English),
            new("日语", eLanguage.Japanese),
            new("韩语", eLanguage.Korean),
            new("法语", eLanguage.French),
            new("德语", eLanguage.German),
            new("西班牙语", eLanguage.Spanish),
            new("俄语", eLanguage.Russian),
        ];
        OutputFormats =
        [
            new("SubRip 字幕 (.srt)", OutputFormat.SubRip),
            new("WebVTT 字幕 (.vtt)", OutputFormat.WebVtt),
            new("纯文本 (.txt)", OutputFormat.Text),
            new("带时间戳文本 (.txt)", OutputFormat.TextWithTimestamps),
        ];
        OutputLocations =
        [
            new("统一输出目录", OutputLocationMode.SelectedFolder, "全部字幕写入指定目录"),
            new("原文件旁", OutputLocationMode.SourceFolder, "字幕写入媒体文件所在目录"),
            new("保留目录结构", OutputLocationMode.PreserveStructure, "在输出目录中保留导入文件夹的层级"),
        ];

        selectedLanguage = Languages[0];
        selectedOutputFormat = OutputFormats[0];
        selectedEngine = Engines[0];
        selectedOutputLocation = OutputLocations[0];
        transcription = selectedEngine.Create();

        InitializeComponent();
        DataContext = this;
        Library.setLogSink(eLogLevel.Info, eLoggerFlags.SkipFormatMessage, OnNativeLog);
        RefreshCaptureDevices();
    }

    public ObservableCollection<TranscriptionJob> Jobs { get; } = [];
    public ObservableCollection<SourceFolderItem> SourceFolders { get; } = [];
    public ObservableCollection<SubtitlePreviewItem> PreviewSegments { get; } = [];
    public ObservableCollection<CaptureDeviceOption> CaptureDevices { get; } = [];
    public IReadOnlyList<LanguageOption> Languages { get; }
    public IReadOnlyList<OutputFormatOption> OutputFormats { get; }
    public IReadOnlyList<EngineOption> Engines { get; }
    public IReadOnlyList<OutputLocationOption> OutputLocations { get; }

    public string ModelPath
    {
        get => modelPath;
        set { modelPath = value; OnPropertyChanged(); OnStateChanged(); }
    }

    public string OutputFolder
    {
        get => outputFolder;
        set { outputFolder = value; OnPropertyChanged(); }
    }

    public string ModelStatus
    {
        get => modelStatus;
        set { modelStatus = value; OnPropertyChanged(); }
    }

    public string GlobalStatus
    {
        get => globalStatus;
        set { globalStatus = value; OnPropertyChanged(); }
    }

    public string LogOutput
    {
        get => logOutput;
        set { logOutput = value; OnPropertyChanged(); }
    }

    public string LiveStatus
    {
        get => liveStatus;
        set { liveStatus = value; OnPropertyChanged(); }
    }

    public double ModelProgress
    {
        get => modelProgress;
        set { modelProgress = value; OnPropertyChanged(); }
    }

    public bool Translate
    {
        get => translate;
        set { translate = value && CanTranslate; OnPropertyChanged(); }
    }

    public bool CanTranslate => SelectedLanguage.Value != eLanguage.English;

    public LanguageOption SelectedLanguage
    {
        get => selectedLanguage;
        set
        {
            selectedLanguage = value;
            if (!CanTranslate)
                translate = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Translate));
            OnPropertyChanged(nameof(CanTranslate));
        }
    }

    public OutputFormatOption SelectedOutputFormat
    {
        get => selectedOutputFormat;
        set { selectedOutputFormat = value; OnPropertyChanged(); }
    }

    public OutputLocationOption SelectedOutputLocation
    {
        get => selectedOutputLocation;
        set { selectedOutputLocation = value; OnPropertyChanged(); }
    }

    public EngineOption SelectedEngine
    {
        get => selectedEngine;
        set
        {
            if (value is null || ReferenceEquals(selectedEngine, value) || IsRunning || isLoadingModel)
                return;

            transcription.Dispose();
            selectedEngine = value;
            transcription = value.Create();
            ModelStatus = "尚未加载模型";
            ModelProgress = 0;
            GlobalStatus = $"已切换到 {value.Name}";
            OnPropertyChanged();
            OnStateChanged();
        }
    }

    public CaptureDeviceOption? SelectedCaptureDevice
    {
        get => selectedCaptureDevice;
        set { selectedCaptureDevice = value; OnPropertyChanged(); }
    }

    public bool IsRunning
    {
        get => isRunning;
        private set { isRunning = value; OnPropertyChanged(); OnStateChanged(); }
    }

    public int SelectedJobCount => Jobs.Count(job => job.IsSelected);
    public string QueueSummary => $"{Jobs.Count} 个文件，已选择 {SelectedJobCount} 个";
    public bool CanEditQueue => !IsRunning && !isLoadingModel;
    public bool CanLoadModel => !IsRunning && !isLoadingModel && File.Exists(ModelPath);
    public bool CanStart => transcription.IsModelLoaded && SelectedJobCount > 0 && !IsRunning && !isLoadingModel;

    void ChooseModel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Whisper GGML 模型",
            Filter = "Whisper 模型 (*.bin)|*.bin|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
            ModelPath = dialog.FileName;
    }

    async void LoadModel_Click(object sender, RoutedEventArgs e)
    {
        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        isLoadingModel = true;
        OnStateChanged();
        ModelStatus = "正在加载模型";
        ModelProgress = 0;
        GlobalStatus = "正在将模型载入内存与显存...";

        try
        {
            var progress = new Progress<double>(value => ModelProgress = value);
            await transcription.LoadModelAsync(ModelPath, progress, operationCancellation.Token);
            ModelProgress = 1;
            ModelStatus = $"已加载 {Path.GetFileName(ModelPath)}";
            GlobalStatus = "模型加载完成，可以开始转录";
        }
        catch (OperationCanceledException)
        {
            ModelStatus = "模型加载已取消";
            GlobalStatus = "已取消";
        }
        catch (Exception ex)
        {
            ModelStatus = "模型加载失败";
            GlobalStatus = ex.Message;
            MessageBox.Show(this, ex.Message, "加载模型失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            isLoadingModel = false;
            OnStateChanged();
        }
    }

    void AddFiles_Click(object sender, RoutedEventArgs e)
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

    void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择要递归扫描的媒体目录", Multiselect = false };
        if (dialog.ShowDialog(this) != true)
            return;

        string root = dialog.FolderName;
        int before = Jobs.Count;
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
        GlobalStatus = $"从 {root} 添加了 {Jobs.Count - before} 个媒体文件";
    }

    void AddJob(string path, string? sourceRoot)
    {
        if (Jobs.Any(job => string.Equals(job.InputPath, path, StringComparison.OrdinalIgnoreCase)))
            return;

        var job = new TranscriptionJob { InputPath = path, SourceRoot = sourceRoot };
        job.PropertyChanged += Job_PropertyChanged;
        Jobs.Add(job);

        if (string.IsNullOrWhiteSpace(sourceRoot))
            return;
        SourceFolderItem? source = SourceFolders.FirstOrDefault(item => string.Equals(item.Path, sourceRoot, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            source = new SourceFolderItem { Path = sourceRoot, FileCount = 1 };
            source.PropertyChanged += Source_PropertyChanged;
            SourceFolders.Add(source);
        }
        else
        {
            source.FileCount++;
        }
    }

    void Source_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (syncingSourceSelection || e.PropertyName != nameof(SourceFolderItem.IsSelected) || sender is not SourceFolderItem source)
            return;

        syncingSourceSelection = true;
        foreach (TranscriptionJob job in Jobs.Where(job => string.Equals(job.SourceRoot, source.Path, StringComparison.OrdinalIgnoreCase)))
            job.IsSelected = source.IsSelected;
        syncingSourceSelection = false;
        QueueChanged();
    }

    void Job_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TranscriptionJob.IsSelected))
            QueueChanged();
    }

    void SelectAll_Click(object sender, RoutedEventArgs e) => SetAllSelected(true);
    void SelectNone_Click(object sender, RoutedEventArgs e) => SetAllSelected(false);

    void SetAllSelected(bool selected)
    {
        syncingSourceSelection = true;
        foreach (TranscriptionJob job in Jobs)
            job.IsSelected = selected;
        foreach (SourceFolderItem source in SourceFolders)
            source.IsSelected = selected;
        syncingSourceSelection = false;
        QueueChanged();
    }

    void RemoveFiles_Click(object sender, RoutedEventArgs e)
    {
        foreach (TranscriptionJob job in JobGrid.SelectedItems.Cast<TranscriptionJob>().ToArray())
        {
            job.PropertyChanged -= Job_PropertyChanged;
            Jobs.Remove(job);
        }
        RebuildSources();
        QueueChanged();
    }

    void ClearFiles_Click(object sender, RoutedEventArgs e)
    {
        foreach (TranscriptionJob job in Jobs)
            job.PropertyChanged -= Job_PropertyChanged;
        foreach (SourceFolderItem source in SourceFolders)
            source.PropertyChanged -= Source_PropertyChanged;
        Jobs.Clear();
        SourceFolders.Clear();
        PreviewSegments.Clear();
        GlobalStatus = "队列已清空";
        QueueChanged();
    }

    void RebuildSources()
    {
        foreach (SourceFolderItem source in SourceFolders)
            source.PropertyChanged -= Source_PropertyChanged;
        SourceFolders.Clear();
        foreach (IGrouping<string, TranscriptionJob> group in Jobs.Where(job => !string.IsNullOrWhiteSpace(job.SourceRoot))
            .GroupBy(job => job.SourceRoot!, StringComparer.OrdinalIgnoreCase))
        {
            var source = new SourceFolderItem { Path = group.Key, FileCount = group.Count(), IsSelected = group.Any(job => job.IsSelected) };
            source.PropertyChanged += Source_PropertyChanged;
            SourceFolders.Add(source);
        }
    }

    void ChooseOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择字幕输出目录",
            InitialDirectory = Directory.Exists(OutputFolder) ? OutputFolder : null,
        };
        if (dialog.ShowDialog(this) == true)
            OutputFolder = dialog.FolderName;
    }

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedOutputLocation.Value != OutputLocationMode.SourceFolder && !Directory.Exists(OutputFolder))
        {
            MessageBox.Show(this, "请选择有效的输出目录。", "无法开始", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        IsRunning = true;
        PreviewSegments.Clear();
        int completed = 0;
        int failed = 0;

        try
        {
            foreach (TranscriptionJob job in Jobs.Where(job => job.IsSelected).ToArray())
            {
                operationCancellation.Token.ThrowIfCancellationRequested();
                job.State = JobState.Running;
                job.StatusText = "转录中";
                job.Progress = 0;
                job.Error = null;
                GlobalStatus = $"正在处理 {job.FileName}";
                int segmentIndex = 0;

                var progress = new Progress<double>(value => job.Progress = value);
                try
                {
                    string targetFolder = GetOutputFolder(job);
                    Directory.CreateDirectory(targetFolder);
                    job.OutputPath = await transcription.TranscribeAsync(
                        job.InputPath,
                        targetFolder,
                        SelectedLanguage.Value,
                        Translate,
                        SelectedOutputFormat.Value,
                        progress,
                        segment => Dispatcher.BeginInvoke(() => PreviewSegments.Add(new SubtitlePreviewItem(
                            ++segmentIndex, job.FileName, segment.Begin, segment.End, segment.Text))),
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
                    AppendLog(job.FileName, $"错误：{ex.Message}");
                }
            }
            GlobalStatus = $"批量转录完成：成功 {completed}，失败 {failed}";
        }
        catch (OperationCanceledException)
        {
            GlobalStatus = $"任务已停止：成功 {completed}，失败 {failed}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    string GetOutputFolder(TranscriptionJob job)
    {
        return SelectedOutputLocation.Value switch
        {
            OutputLocationMode.SourceFolder => Path.GetDirectoryName(job.InputPath) ?? OutputFolder,
            OutputLocationMode.PreserveStructure when !string.IsNullOrWhiteSpace(job.SourceRoot) =>
                Path.Combine(OutputFolder, Path.GetFileName(job.SourceRoot.TrimEnd(Path.DirectorySeparatorChar)),
                    Path.GetDirectoryName(job.RelativePath) ?? string.Empty),
            _ => OutputFolder,
        };
    }

    void Stop_Click(object sender, RoutedEventArgs e)
    {
        GlobalStatus = "正在停止当前任务...";
        operationCancellation?.Cancel();
    }

    void RefreshDevices_Click(object sender, RoutedEventArgs e) => RefreshCaptureDevices();

    void RefreshCaptureDevices()
    {
        try
        {
            captureMediaFoundation ??= Library.initMediaFoundation();
            CaptureDeviceId[] devices = captureMediaFoundation.listCaptureDevices() ?? [];
            CaptureDevices.Clear();
            foreach (CaptureDeviceId device in devices)
                CaptureDevices.Add(new CaptureDeviceOption(device.displayName, device.endpoint));
            SelectedCaptureDevice = CaptureDevices.FirstOrDefault();
            LiveStatus = devices.Length == 0 ? "未发现可用麦克风" : $"已发现 {devices.Length} 个录音设备";
        }
        catch (Exception ex)
        {
            LiveStatus = $"无法读取录音设备：{ex.Message}";
        }
    }

    void AppendLog(string source, string text)
    {
        LogOutput += $"[{DateTime.Now:HH:mm:ss}] [{source}] {text.Trim()}{Environment.NewLine}";
    }

    void OnNativeLog(eLogLevel level, string message)
    {
        Dispatcher.BeginInvoke(() => AppendLog(level.ToString(), message));
    }

    void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (IsRunning || isLoadingModel)
        {
            MessageBoxResult result = MessageBox.Show(
                this,
                "模型或转录任务仍在运行，确定要退出吗？",
                "确认退出",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
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

    void QueueChanged()
    {
        OnPropertyChanged(nameof(SelectedJobCount));
        OnPropertyChanged(nameof(QueueSummary));
        OnStateChanged();
        if (!IsRunning)
            GlobalStatus = QueueSummary;
    }

    void OnStateChanged()
    {
        OnPropertyChanged(nameof(CanEditQueue));
        OnPropertyChanged(nameof(CanLoadModel));
        OnPropertyChanged(nameof(CanStart));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
