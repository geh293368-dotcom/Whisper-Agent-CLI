using System.IO;
using System.Windows;
using System.Windows.Threading;
using WhisperDesktop.Modern.Services;
using WhisperDesktop.Protocol;

namespace WhisperDesktop.Modern;

public partial class App : Application
{
    SingleInstanceCoordinator? singleInstance;
    DesktopCommandServer? commandServer;

    protected override void OnStartup(StartupEventArgs e)
    {
        _ = AppLogger.CurrentLogPath;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        base.OnStartup(e);

        if (TryGetArgumentValue(e.Args, "--ai-verify", out string? sampleDirectory) && sampleDirectory is not null)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunAiVerifyAsync(sampleDirectory);
            return;
        }

        singleInstance = new SingleInstanceCoordinator();
        if (!singleInstance.IsPrimaryInstance)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = ForwardToPrimaryInstanceAsync(e.Args);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        commandServer = new DesktopCommandServer((request, cancellationToken) =>
            DispatchDesktopCommandAsync(window, request, cancellationToken));
        commandServer.Start();
        window.Show();

        if (e.Args.Length > 0)
            _ = HandleInitialCommandAsync(window, e.Args);
    }

    static async Task<DesktopCommandResponse> DispatchDesktopCommandAsync(
        MainWindow window,
        DesktopCommandRequest request,
        CancellationToken cancellationToken)
    {
        Task<DesktopCommandResponse> commandTask = await window.Dispatcher.InvokeAsync(
            () => window.ExecuteDesktopCommandAsync(request, cancellationToken));
        return await commandTask;
    }

    async Task ForwardToPrimaryInstanceAsync(string[] args)
    {
        int exitCode = 0;
        try
        {
            if (!TryCreateDesktopRequest(args, out DesktopCommandRequest? request, out string? error))
                throw new ArgumentException(error);

            DesktopCommandResponse response = await DesktopCommandProtocol.SendAsync(
                request!,
                TimeSpan.FromSeconds(10));
            if (!response.Success)
            {
                exitCode = 1;
                MessageBox.Show(
                    response.Message,
                    "Whisper Desktop",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            exitCode = 1;
            AppLogger.Write("Agent 命令", "转发到现有桌面实例失败", ex, "ERROR");
            MessageBox.Show(ex.Message, "Whisper Desktop", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Shutdown(exitCode);
        }
    }

    static async Task HandleInitialCommandAsync(MainWindow window, string[] args)
    {
        if (!TryCreateDesktopRequest(args, out DesktopCommandRequest? request, out string? error))
        {
            AppLogger.Write("Agent 命令", error ?? "无法解析启动命令", level: "ERROR");
            return;
        }

        DesktopCommandResponse response = await window.ExecuteDesktopCommandAsync(request!, CancellationToken.None);
        AppLogger.Write("Agent 命令", response.Message, level: response.Success ? "INFO" : "ERROR");
    }

    static bool TryCreateDesktopRequest(
        string[] args,
        out DesktopCommandRequest? request,
        out string? error)
    {
        error = null;
        if (args.Length == 0)
        {
            request = new DesktopCommandRequest { Action = "ping", Activate = true };
            return true;
        }

        bool start = false;
        bool wait = false;
        bool activate = true;
        var paths = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--enqueue":
                    if (++i >= args.Length)
                    {
                        request = null;
                        error = "--enqueue 后需要提供文件或目录路径。";
                        return false;
                    }
                    paths.Add(args[i]);
                    break;
                case "--start": start = true; break;
                case "--wait": wait = true; start = true; break;
                case "--no-activate": activate = false; break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        request = null;
                        error = $"未知参数：{args[i]}";
                        return false;
                    }
                    paths.Add(args[i]);
                    break;
            }
        }

        request = new DesktopCommandRequest
        {
            Action = paths.Count == 0 ? "ping" : "submit",
            Paths = paths.ToArray(),
            Start = start,
            Wait = wait,
            Activate = activate,
        };
        return true;
    }

    async Task RunAiVerifyAsync(string sampleDirectory)
    {
        int exitCode = 0;
        try
        {
            if (!Directory.Exists(sampleDirectory))
                throw new DirectoryNotFoundException(sampleDirectory);

            var apiKeyStore = new GeminiApiKeyStore();
            string? apiKey = apiKeyStore.Read();
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Gemini API Key 未配置。请先在设置页保存 API Key。");

            var settingsStore = new AppSettingsStore();
            AppSettingsStore.AppConfig? config = settingsStore.Load((message, exception) =>
                AppLogger.Write("配置", message, exception, "WARN"));
            string model = string.IsNullOrWhiteSpace(config?.GeminiModel)
                ? GeminiModelClient.DefaultModel
                : config.GeminiModel;

            var terminologyService = new TerminologyService();
            IReadOnlyList<TerminologyPack> packs = terminologyService.LoadPacks((message, exception) =>
                AppLogger.Write("术语词库", message, exception, "WARN"));
            ActiveTerminology activeTerminology = config?.TerminologyEnabled == true
                ? terminologyService.BuildActiveTerminology(packs, config.SelectedTerminologyPacks ?? [])
                : new ActiveTerminology([], null, 0, []);

            string[] mediaPaths = Directory.EnumerateFiles(sampleDirectory)
                .Where(path => new[] { ".mp4", ".mkv", ".mov", ".avi", ".mp3", ".wav", ".m4a", ".flac" }
                    .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (mediaPaths.Length == 0)
                throw new InvalidOperationException("样本目录里没有找到媒体文件。");

            var service = new AiSubtitleOptimizationService(new GeminiModelClient());
            var progress = new Progress<AiSubtitleProgress>(value =>
                AppLogger.Write("AI 字幕验证", value.Message));
            AppLogger.Write("AI 字幕验证", $"开始验证样本目录：{sampleDirectory}，媒体文件 {mediaPaths.Length} 个，模型 {model}");
            AiSubtitleBatchReport report = await service.OptimizeAndEvaluateAsync(
                mediaPaths,
                apiKey,
                model,
                "中文",
                activeTerminology.InitialPrompt ?? "Unity 游戏特效课程；重点术语：Unity、粒子、拖尾、溶解、贴图、材质、模型、光晕、法阵、关键帧、压感",
                AiSubtitleOutputPolicy.PreserveOriginal,
                progress,
                CancellationToken.None);
            AppLogger.Write("AI 字幕验证",
                $"验证完成：报告 {report.ReportMarkdownPath}，Token {report.Usage.TotalTokens}，成本约 ¥{report.EstimatedCostCny:F4}");
        }
        catch (Exception ex)
        {
            exitCode = 1;
            AppLogger.Write("AI 字幕验证", "验证失败", ex, "ERROR");
        }
        finally
        {
            Shutdown(exitCode);
        }
    }

    static bool TryGetArgumentValue(string[] args, string name, out string? value)
    {
        value = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (i + 1 >= args.Length)
                return false;
            value = args[i + 1];
            return !string.IsNullOrWhiteSpace(value);
        }

        return false;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        commandServer?.Dispose();
        singleInstance?.Dispose();
        AppLogger.Write("应用", $"正常退出，退出代码 {e.ApplicationExitCode}");
        base.OnExit(e);
    }

    static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Write("界面未处理异常", e.Exception.Message, e.Exception, "FATAL");
        MessageBox.Show(
            $"程序遇到未处理异常，详细信息已写入日志：{Environment.NewLine}{AppLogger.CurrentLogPath}",
            "Whisper Desktop",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            AppLogger.Write("进程未处理异常", exception.Message, exception, "FATAL");
        else
            AppLogger.Write("进程未处理异常", e.ExceptionObject?.ToString() ?? "未知异常", level: "FATAL");
    }

    static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLogger.Write("后台任务异常", e.Exception.Message, e.Exception, "ERROR");
        e.SetObserved();
    }
}
