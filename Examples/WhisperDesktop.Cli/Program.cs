using System.Text.Json;
using WhisperDesktop.Protocol;

namespace WhisperDesktop.Cli;

static class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length > 0 && args[0] is "-h" or "--help")
        {
            PrintUsage();
            return 0;
        }

        if (!TryParse(args, out DesktopCommandRequest? request, out bool json, out string? error))
        {
            if (!string.IsNullOrWhiteSpace(error))
                Console.Error.WriteLine(error);
            PrintUsage();
            return 2;
        }

        DesktopCommandResponse response = await DesktopCommandProtocol.SendAsync(
            request!,
            TimeSpan.FromSeconds(5));

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(response, DesktopCommandProtocol.JsonOptions));
        }
        else
        {
            TextWriter output = response.Success ? Console.Out : Console.Error;
            output.WriteLine(response.Message);
            foreach (DesktopCommandJob job in response.Jobs)
            {
                output.WriteLine($"[{job.Status}] {job.InputPath}");
                if (!string.IsNullOrWhiteSpace(job.OutputPath))
                    output.WriteLine($"  -> {job.OutputPath}");
                if (!string.IsNullOrWhiteSpace(job.Error))
                    output.WriteLine($"  {job.Error}");
            }
        }

        if (response.Success)
            return 0;
        return string.Equals(response.ErrorCode, "desktop_unavailable", StringComparison.OrdinalIgnoreCase) ? 3 : 4;
    }

    static bool TryParse(
        string[] args,
        out DesktopCommandRequest? request,
        out bool json,
        out string? error)
    {
        request = null;
        json = false;
        error = null;

        if (args.Length == 0 || args[0] is "-h" or "--help")
            return false;

        string command = args[0].ToLowerInvariant();
        if (command == "ping")
        {
            foreach (string arg in args.Skip(1))
            {
                if (arg == "--json")
                    json = true;
                else
                {
                    error = $"未知参数：{arg}";
                    return false;
                }
            }
            request = new DesktopCommandRequest { Action = "ping", Activate = false };
            return true;
        }

        if (command is not ("submit" or "transcribe"))
        {
            error = $"未知命令：{args[0]}";
            return false;
        }

        bool start = command == "transcribe";
        bool wait = command == "transcribe";
        bool activate = true;
        var paths = new List<string>();

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--start": start = true; break;
                case "--wait": wait = true; start = true; break;
                case "--json": json = true; break;
                case "--no-activate": activate = false; break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        error = $"未知参数：{args[i]}";
                        return false;
                    }
                    paths.Add(args[i]);
                    break;
            }
        }

        if (paths.Count == 0)
        {
            error = "请至少提供一个媒体文件或目录。";
            return false;
        }

        request = new DesktopCommandRequest
        {
            Action = "submit",
            Paths = paths.ToArray(),
            Start = start,
            Wait = wait,
            Activate = activate,
        };
        return true;
    }

    static void PrintUsage()
    {
        Console.WriteLine("Whisper Desktop 控制工具");
        Console.WriteLine();
        Console.WriteLine("用法：");
        Console.WriteLine("  whisperctl ping [--json]");
        Console.WriteLine("  whisperctl submit <文件或目录>... [--start] [--wait] [--json] [--no-activate]");
        Console.WriteLine("  whisperctl transcribe <文件或目录>... [--json] [--no-activate]");
        Console.WriteLine();
        Console.WriteLine("transcribe 等价于 submit --start --wait。桌面端必须已经启动。");
    }
}
