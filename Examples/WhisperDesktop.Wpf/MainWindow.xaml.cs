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

public partial class MainWindow : Window, INotifyPropertyChanged
{
    ITranscriptionEngine transcription;
    CancellationTokenSource? operationCancellation;
    string modelPath = string.Empty;
    string outputFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    string modelStatus = "尚未加载模型";
    string globalStatus = "准备就绪";
    string liveOutput = string.Empty;
    double modelProgress;
    bool isRunning;
    bool isLoadingModel;
    bool translate;
    LanguageOption selectedLanguage;
    OutputFormatOption selectedOutputFormat;
    EngineOption selectedEngine;

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
        selectedLanguage = Languages[0];
        selectedOutputFormat = OutputFormats[0];
        selectedEngine = Engines[0];
        transcription = selectedEngine.Create();

        InitializeComponent();
        DataContext = this;
        Library.setLogSink(eLogLevel.Info, eLoggerFlags.SkipFormatMessage, OnNativeLog);
    }

    public ObservableCollection<TranscriptionJob> Jobs { get; } = [];
    public IReadOnlyList<LanguageOption> Languages { get; }
    public IReadOnlyList<OutputFormatOption> OutputFormats { get; }
    public IReadOnlyList<EngineOption> Engines { get; }

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

    public string LiveOutput
    {
        get => liveOutput;
        set { liveOutput = value; OnPropertyChanged(); }
    }

    public double ModelProgress
    {
        get => modelProgress;
        set { modelProgress = value; OnPropertyChanged(); }
    }

    public bool Translate
    {
        get => translate;
        set { translate = value; OnPropertyChanged(); }
    }

    public LanguageOption SelectedLanguage
    {
        get => selectedLanguage;
        set { selectedLanguage = value; OnPropertyChanged(); }
    }

    public OutputFormatOption SelectedOutputFormat
    {
        get => selectedOutputFormat;
        set { selectedOutputFormat = value; OnPropertyChanged(); }
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

    public bool IsRunning
    {
        get => isRunning;
        private set { isRunning = value; OnPropertyChanged(); OnStateChanged(); }
    }

    public bool CanEditQueue => !IsRunning && !isLoadingModel;
    public bool CanLoadModel => !IsRunning && !isLoadingModel && File.Exists(ModelPath);
    public bool CanStart => transcription.IsModelLoaded && Jobs.Count > 0 && !IsRunning && !isLoadingModel;

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

        var existing = Jobs.Select(job => job.InputPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string path in dialog.FileNames)
        {
            if (existing.Add(path))
                Jobs.Add(new TranscriptionJob { InputPath = path });
        }
        GlobalStatus = $"队列中共有 {Jobs.Count} 个文件";
        OnStateChanged();
    }

    void RemoveFiles_Click(object sender, RoutedEventArgs e)
    {
        foreach (TranscriptionJob job in JobList.SelectedItems.Cast<TranscriptionJob>().ToArray())
            Jobs.Remove(job);
        OnStateChanged();
    }

    void ClearFiles_Click(object sender, RoutedEventArgs e)
    {
        Jobs.Clear();
        LiveOutput = string.Empty;
        GlobalStatus = "队列已清空";
        OnStateChanged();
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
        if (!Directory.Exists(OutputFolder))
        {
            MessageBox.Show(this, "请选择有效的输出目录。", "无法开始", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        IsRunning = true;
        LiveOutput = string.Empty;
        int completed = 0;
        int failed = 0;

        try
        {
            foreach (TranscriptionJob job in Jobs)
            {
                operationCancellation.Token.ThrowIfCancellationRequested();
                job.State = JobState.Running;
                job.StatusText = "转录中";
                job.Progress = 0;
                job.Error = null;
                GlobalStatus = $"正在处理 {job.FileName}";

                var progress = new Progress<double>(value => job.Progress = value);
                try
                {
                    job.OutputPath = await transcription.TranscribeAsync(
                        job.InputPath,
                        OutputFolder,
                        SelectedLanguage.Value,
                        Translate,
                        SelectedOutputFormat.Value,
                        progress,
                        text => Dispatcher.BeginInvoke(() => AppendLiveText(job.FileName, text)),
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
                    throw;
                }
                catch (Exception ex)
                {
                    job.State = JobState.Failed;
                    job.StatusText = "失败";
                    job.Error = ex.Message;
                    failed++;
                    AppendLiveText(job.FileName, $"错误: {ex.Message}");
                }
            }
            GlobalStatus = $"批量转录完成：成功 {completed}，失败 {failed}";
        }
        catch (OperationCanceledException)
        {
            foreach (TranscriptionJob pending in Jobs.Where(j => j.State == JobState.Pending))
                pending.StatusText = "未处理";
            GlobalStatus = $"任务已停止：成功 {completed}，失败 {failed}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    void Stop_Click(object sender, RoutedEventArgs e)
    {
        GlobalStatus = "正在停止当前任务...";
        operationCancellation?.Cancel();
    }

    void AppendLiveText(string fileName, string text)
    {
        LiveOutput += $"[{fileName}] {text}{Environment.NewLine}";
    }

    void OnNativeLog(eLogLevel level, string message)
    {
        Dispatcher.BeginInvoke(() => AppendLiveText(level.ToString(), message.Trim()));
    }

    void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!IsRunning && !isLoadingModel)
        {
            transcription.Dispose();
            return;
        }

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
