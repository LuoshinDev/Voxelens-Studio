using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ZhuJieJing.Core.Collaboration;

public sealed record ZhujieStudioCommand(string Action, string? ProjectDirectory = null)
{
    public const string ActivateAction = "activate";
    public const string OpenProjectAction = "openProject";

    public static ZhujieStudioCommand Activate() => new(ActivateAction);

    public static ZhujieStudioCommand OpenProject(string projectDirectory) =>
        new(OpenProjectAction, Path.GetFullPath(projectDirectory));
}

public static class ZhujieStudioBridgeProtocol
{
    public const string PipeName = "ZhuJieJing.Studio.Command.v1";
    public const string SingletonMutexName = "Local\\ZhuJieJing.Studio.Singleton.v1";
    private const int MaximumCommandCharacters = 64 * 1024;

    public static async Task<bool> TrySendAsync(
        ZhujieStudioCommand command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if(timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        using CancellationTokenSource timeoutCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            await using NamedPipeClientStream pipe = new(
                ".",
                PipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeoutCancellation.Token).ConfigureAwait(false);
            await WriteAsync(pipe, command, timeoutCancellation.Token).ConfigureAwait(false);
            return true;
        }
        catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch(IOException)
        {
            return false;
        }
    }

    public static async Task WriteAsync(
        Stream stream,
        ZhujieStudioCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(command);
        string json = JsonSerializer.Serialize(command);
        if(json.Length > MaximumCommandCharacters)
            throw new InvalidDataException("筑界镜实例命令过长。");
        using StreamWriter writer = new(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
        };
        await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ZhujieStudioCommand> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        string? json = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if(string.IsNullOrWhiteSpace(json)) throw new InvalidDataException("筑界镜实例命令为空。");
        if(json.Length > MaximumCommandCharacters) throw new InvalidDataException("筑界镜实例命令过长。");
        return JsonSerializer.Deserialize<ZhujieStudioCommand>(json) ??
               throw new InvalidDataException("筑界镜实例命令无效。");
    }
}
