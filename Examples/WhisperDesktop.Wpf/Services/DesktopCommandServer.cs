using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using WhisperDesktop.Protocol;

namespace WhisperDesktop.Modern.Services;

internal sealed class DesktopCommandServer : IDisposable
{
    const int MaxRequestCharacters = 1_000_000;

    readonly Func<DesktopCommandRequest, CancellationToken, Task<DesktopCommandResponse>> handler;
    readonly CancellationTokenSource cancellation = new();
    Task? acceptLoop;

    public DesktopCommandServer(Func<DesktopCommandRequest, CancellationToken, Task<DesktopCommandResponse>> handler)
    {
        this.handler = handler;
    }

    public void Start()
    {
        acceptLoop ??= AcceptLoopAsync(cancellation.Token);
    }

    async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    DesktopCommandProtocol.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken);
                _ = HandleConnectionAsync(pipe, cancellationToken);
                pipe = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                AppLogger.Write("Agent 命令", "本地命令通道异常", ex, "ERROR");
                try
                {
                    await Task.Delay(250, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken serverCancellation)
    {
        using (pipe)
        using (var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true })
        {
            DesktopCommandResponse response;
            try
            {
                string? json = await reader.ReadLineAsync(serverCancellation);
                if (string.IsNullOrWhiteSpace(json) || json.Length > MaxRequestCharacters)
                {
                    response = DesktopCommandResponse.Failure("命令内容为空或过长。", "invalid_request");
                }
                else
                {
                    DesktopCommandRequest? request = JsonSerializer.Deserialize<DesktopCommandRequest>(
                        json,
                        DesktopCommandProtocol.JsonOptions);
                    response = request is null
                        ? DesktopCommandResponse.Failure("无法解析命令。", "invalid_request")
                        : await handler(request, serverCancellation);
                }
            }
            catch (OperationCanceledException) when (serverCancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                AppLogger.Write("Agent 命令", "处理本地命令失败", ex, "ERROR");
                response = DesktopCommandResponse.Failure(ex.Message, "command_failed");
            }

            try
            {
                await writer.WriteLineAsync(
                    JsonSerializer.Serialize(response, DesktopCommandProtocol.JsonOptions).AsMemory(),
                    serverCancellation);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                AppLogger.Write("Agent 命令", "命令客户端已断开", ex, "WARN");
            }
        }
    }

    public void Dispose()
    {
        cancellation.Cancel();
        cancellation.Dispose();
    }
}
