using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace WhisperDesktop.Modern.Services;

internal static class AppLogger
{
    const int RetentionDays = 30;
    const long MaxFileBytes = 20L * 1024 * 1024;
    static readonly object SyncRoot = new();
    static readonly UTF8Encoding Utf8NoBom = new(false);
    static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WhisperDesktop",
        "Logs");

    static bool initialized;
    static string currentLogPath = string.Empty;

    public static string CurrentLogPath
    {
        get
        {
            lock (SyncRoot)
            {
                EnsureInitialized();
                return currentLogPath;
            }
        }
    }

    public static string StartSession(string engineName)
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        string separator = new('=', 80);
        string sessionHeader = string.Join(Environment.NewLine,
            string.Empty,
            separator,
            $"会话开始: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}",
            $"会话 ID: {Guid.NewGuid():N}",
            $"应用版本: {version}",
            $"推理引擎: {engineName}",
            $"操作系统: {RuntimeInformation.OSDescription}",
            $"进程架构: {RuntimeInformation.ProcessArchitecture}",
            $"运行时: {RuntimeInformation.FrameworkDescription}",
            $"处理器数量: {Environment.ProcessorCount}",
            separator,
            string.Empty);

        WriteRaw(sessionHeader);
        return sessionHeader;
    }

    public static string Write(string source, string text, Exception? exception = null, string level = "INFO")
    {
        string message = text.Trim();
        if (exception is not null)
            message = $"{message}{Environment.NewLine}{exception}";

        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{source}] {message}{Environment.NewLine}";
        WriteRaw(line);
        return line;
    }

    static void WriteRaw(string text)
    {
        try
        {
            lock (SyncRoot)
            {
                EnsureInitialized();
                RotateIfNeeded(Utf8NoBom.GetByteCount(text));

                using FileStream stream = new(currentLogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using StreamWriter writer = new(stream, Utf8NoBom);
                writer.Write(text);
            }
        }
        catch
        {
            // Logging must never terminate the application.
        }
    }

    static void EnsureInitialized()
    {
        if (initialized)
            return;

        Directory.CreateDirectory(LogDirectory);
        CleanupOldLogs();
        currentLogPath = FindWritableLogPath(DateTime.Now);
        initialized = true;
    }

    static void CleanupOldLogs()
    {
        DateTime cutoffUtc = DateTime.UtcNow.AddDays(-RetentionDays);
        foreach (string path in Directory.EnumerateFiles(LogDirectory, "whisper-desktop-*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoffUtc)
                    File.Delete(path);
            }
            catch
            {
                // A locked or inaccessible old log can be retried next launch.
            }
        }
    }

    static void RotateIfNeeded(int incomingBytes)
    {
        string todayPrefix = $"whisper-desktop-{DateTime.Now:yyyyMMdd}";
        if (!Path.GetFileName(currentLogPath).StartsWith(todayPrefix, StringComparison.OrdinalIgnoreCase))
        {
            currentLogPath = FindWritableLogPath(DateTime.Now);
            return;
        }

        long currentLength = File.Exists(currentLogPath) ? new FileInfo(currentLogPath).Length : 0;
        if (currentLength + incomingBytes <= MaxFileBytes)
            return;

        currentLogPath = FindNextLogPath(DateTime.Now);
    }

    static string FindWritableLogPath(DateTime date)
    {
        string baseName = $"whisper-desktop-{date:yyyyMMdd}";
        string[] candidates = Directory
            .EnumerateFiles(LogDirectory, $"{baseName}*.log")
            .OrderBy(path => GetLogIndex(path, baseName))
            .ToArray();

        string? latest = candidates.LastOrDefault();
        if (latest is null || new FileInfo(latest).Length >= MaxFileBytes)
            return FindNextLogPath(date);

        return latest;
    }

    static string FindNextLogPath(DateTime date)
    {
        string baseName = $"whisper-desktop-{date:yyyyMMdd}";
        string basePath = Path.Combine(LogDirectory, $"{baseName}.log");
        if (!File.Exists(basePath))
            return basePath;

        for (int index = 2; ; index++)
        {
            string candidate = Path.Combine(LogDirectory, $"{baseName}-{index:000}.log");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    static int GetLogIndex(string path, string baseName)
    {
        string fileName = Path.GetFileNameWithoutExtension(path);
        if (fileName.Equals(baseName, StringComparison.OrdinalIgnoreCase))
            return 1;

        string suffix = fileName[(baseName.Length + 1)..];
        return int.TryParse(suffix, out int index) ? index : 0;
    }
}
