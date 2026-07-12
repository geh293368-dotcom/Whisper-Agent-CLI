using System.Text.Json;
using WhisperDesktop.Protocol;

namespace WhisperDesktop.Cli;

static class Program
{
    sealed record CliCommand(DesktopCommandRequest Request, bool Json, bool PollUntilTerminal = false);

    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length > 0 && args[0] is "-h" or "--help")
        {
            PrintUsage();
            return 0;
        }

        if (!TryParse(args, out CliCommand? command, out string? error))
        {
            if (!string.IsNullOrWhiteSpace(error))
                Console.Error.WriteLine(error);
            PrintUsage();
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        DesktopCommandResponse response;
        try
        {
            response = command!.PollUntilTerminal
                ? await WaitForTerminalStateAsync(command.Request.JobId!, cancellation.Token)
                : await DesktopCommandProtocol.SendAsync(command.Request, TimeSpan.FromSeconds(5), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("操作已取消。");
            return 130;
        }

        WriteResponse(response, command!.Json);
        if (response.Success)
            return 0;
        return string.Equals(response.ErrorCode, "desktop_unavailable", StringComparison.OrdinalIgnoreCase) ? 3 : 4;
    }

    static async Task<DesktopCommandResponse> WaitForTerminalStateAsync(
        string jobId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            DesktopCommandResponse response = await DesktopCommandProtocol.SendAsync(
                new DesktopCommandRequest { Action = "status", JobId = jobId, Activate = false },
                TimeSpan.FromSeconds(5),
                cancellationToken);
            if (!response.Success || response.Jobs.Length == 0)
                return response;

            string status = response.Jobs[0].Status;
            if (status == "completed")
                return response;
            if (status is "failed" or "canceled" or "skipped" or "interrupted")
            {
                return new DesktopCommandResponse
                {
                    RequestId = response.RequestId,
                    Success = false,
                    Message = $"任务以 {status} 状态结束。",
                    ErrorCode = $"job_{status}",
                    Jobs = response.Jobs,
                };
            }

            await Task.Delay(500, cancellationToken);
        }
    }

    static void WriteResponse(DesktopCommandResponse response, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(response, DesktopCommandProtocol.JsonOptions));
            return;
        }

        TextWriter output = response.Success ? Console.Out : Console.Error;
        output.WriteLine(response.Message);
        foreach (DesktopCommandJob job in response.Jobs)
        {
            output.WriteLine($"[{job.Status}] {job.JobId}  {job.InputPath}");
            if (!string.IsNullOrWhiteSpace(job.OutputPath))
                output.WriteLine($"  -> {job.OutputPath}");
            if (!string.IsNullOrWhiteSpace(job.Error))
                output.WriteLine($"  {job.Error}");
        }
    }

    static bool TryParse(string[] args, out CliCommand? command, out string? error)
    {
        command = null;
        error = null;
        if (args.Length == 0 || args[0] is "-h" or "--help")
            return false;

        string action = args[0].ToLowerInvariant();
        if (action == "ping")
            return TryParseSimple(args, "ping", needsJobId: false, out command, out error);
        if (action == "list")
            return TryParseList(args, out command, out error);
        if (action is "status" or "result" or "cancel" or "start")
            return TryParseSimple(args, action, needsJobId: true, out command, out error);
        if (action == "wait")
        {
            if (!TryParseSimple(args, "status", needsJobId: true, out command, out error))
                return false;
            command = command! with { PollUntilTerminal = true };
            return true;
        }
        if (action is not ("submit" or "transcribe"))
        {
            error = $"未知命令：{args[0]}";
            return false;
        }

        bool start = action == "transcribe";
        bool wait = action == "transcribe";
        bool activate = true;
        bool json = false;
        string? clientRequestId = null;
        var paths = new List<string>();

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--start": start = true; break;
                case "--wait": wait = true; start = true; break;
                case "--json": json = true; break;
                case "--no-activate": activate = false; break;
                case "--request-id":
                    if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
                    {
                        error = "--request-id 后需要提供非空标识。";
                        return false;
                    }
                    clientRequestId = args[i];
                    break;
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

        command = new CliCommand(new DesktopCommandRequest
        {
            Action = "submit",
            Paths = paths.ToArray(),
            ClientRequestId = clientRequestId,
            Start = start,
            Wait = wait,
            Activate = activate,
        }, json);
        return true;
    }

    static bool TryParseSimple(
        string[] args,
        string action,
        bool needsJobId,
        out CliCommand? command,
        out string? error)
    {
        command = null;
        error = null;
        string? jobId = null;
        bool json = false;

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--json")
                json = true;
            else if (needsJobId && jobId is null && !args[i].StartsWith('-'))
                jobId = args[i];
            else
            {
                error = $"未知参数：{args[i]}";
                return false;
            }
        }

        if (needsJobId && string.IsNullOrWhiteSpace(jobId))
        {
            error = $"{action} 需要 jobId。";
            return false;
        }

        command = new CliCommand(new DesktopCommandRequest
        {
            Action = action,
            JobId = jobId,
            Activate = false,
        }, json);
        return true;
    }

    static bool TryParseList(string[] args, out CliCommand? command, out string? error)
    {
        command = null;
        error = null;
        bool json = false;
        int limit = 100;

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--json")
                json = true;
            else if (args[i] == "--limit" && ++i < args.Length && int.TryParse(args[i], out int parsed))
                limit = Math.Clamp(parsed, 1, 500);
            else
            {
                error = $"未知参数：{args[i]}";
                return false;
            }
        }

        command = new CliCommand(new DesktopCommandRequest
        {
            Action = "list",
            Limit = limit,
            Activate = false,
        }, json);
        return true;
    }

    static void PrintUsage()
    {
        Console.WriteLine("Whisper Desktop 控制工具");
        Console.WriteLine();
        Console.WriteLine("用法：");
        Console.WriteLine("  whisperctl ping [--json]");
        Console.WriteLine("  whisperctl submit <文件或目录>... [--start] [--wait] [--request-id ID] [--json] [--no-activate]");
        Console.WriteLine("  whisperctl transcribe <文件或目录>... [--request-id ID] [--json] [--no-activate]");
        Console.WriteLine("  whisperctl status <job-id> [--json]");
        Console.WriteLine("  whisperctl start <job-id> [--json]");
        Console.WriteLine("  whisperctl wait <job-id> [--json]");
        Console.WriteLine("  whisperctl result <job-id> [--json]");
        Console.WriteLine("  whisperctl cancel <job-id> [--json]");
        Console.WriteLine("  whisperctl list [--limit N] [--json]");
        Console.WriteLine();
        Console.WriteLine("transcribe 等价于 submit --start --wait。桌面端必须已经启动。");
    }
}
