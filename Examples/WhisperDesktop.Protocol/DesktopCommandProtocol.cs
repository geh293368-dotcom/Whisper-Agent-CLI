using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace WhisperDesktop.Protocol;

public static class DesktopCommandProtocol
{
    public const string Version = "1";
    public const string PipeName = "WhisperDesktop.Modern.Command.v1";
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<DesktopCommandResponse> SendAsync(
        DesktopCommandRequest request,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken = default)
    {
        using var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCancellation.CancelAfter(connectTimeout);

        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(connectCancellation.Token);

            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions).AsMemory(), cancellationToken);

            string? responseJson = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(responseJson))
                return DesktopCommandResponse.Failure("桌面端没有返回命令结果。", "empty_response");

            return JsonSerializer.Deserialize<DesktopCommandResponse>(responseJson, JsonOptions)
                ?? DesktopCommandResponse.Failure("桌面端返回了无效结果。", "invalid_response");
        }
        catch (OperationCanceledException) when (connectCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return DesktopCommandResponse.Failure(
                "无法连接 Whisper Desktop。请先启动桌面端后重试。",
                "desktop_unavailable");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DesktopCommandResponse.Failure(ex.Message, "desktop_unavailable");
        }
    }
}

public sealed record DesktopCommandRequest
{
    public string ProtocolVersion { get; init; } = DesktopCommandProtocol.Version;
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
    public string Action { get; init; } = "submit";
    public string[] Paths { get; init; } = [];
    public bool Start { get; init; }
    public bool Wait { get; init; }
    public bool Activate { get; init; } = true;
}

public sealed record DesktopCommandJob
{
    public required string JobId { get; init; }
    public required string InputPath { get; init; }
    public required string FileName { get; init; }
    public required string Status { get; init; }
    public double Progress { get; init; }
    public string? OutputPath { get; init; }
    public string? Error { get; init; }
}

public sealed record DesktopCommandResponse
{
    public string ProtocolVersion { get; init; } = DesktopCommandProtocol.Version;
    public string? RequestId { get; init; }
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? ErrorCode { get; init; }
    public DesktopCommandJob[] Jobs { get; init; } = [];

    public static DesktopCommandResponse Failure(string message, string errorCode, string? requestId = null) => new()
    {
        RequestId = requestId,
        Success = false,
        Message = message,
        ErrorCode = errorCode,
    };
}
