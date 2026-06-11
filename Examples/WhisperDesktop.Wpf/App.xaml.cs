using System.Windows;
using System.Windows.Threading;
using WhisperDesktop.Modern.Services;

namespace WhisperDesktop.Modern;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        _ = AppLogger.CurrentLogPath;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
