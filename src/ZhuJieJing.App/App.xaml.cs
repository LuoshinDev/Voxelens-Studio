using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using ZhuJieJing.Core.Collaboration;

namespace ZhuJieJing.App;

public partial class App : Application
{
    private readonly CancellationTokenSource studioCommandCancellation = new();
    private readonly object pendingStudioCommandLock = new();
    private Mutex? singletonMutex;
    private bool ownsSingletonMutex;
    private ZhujieStudioCommand? pendingStudioCommand;

    protected override void OnStartup(StartupEventArgs e)
    {
        Localization.UiText.EnsureLanguageFiles();
        SetCurrentProcessExplicitAppUserModelID("ZhuJieJing.Studio");
        base.OnStartup(e);

        singletonMutex = new Mutex(
            initiallyOwned: true,
            ZhujieStudioBridgeProtocol.SingletonMutexName,
            out ownsSingletonMutex);
        ZhujieStudioCommand startupCommand = ParseStartupCommand(e.Args);
        if(!ownsSingletonMutex)
        {
            bool delivered = ZhujieStudioBridgeProtocol.TrySendAsync(
                    startupCommand,
                    TimeSpan.FromSeconds(3))
                .GetAwaiter()
                .GetResult();
            Shutdown(delivered ? 0 : 2);
            return;
        }

        QueuePendingStudioCommand(startupCommand);
        _ = ListenForStudioCommandsAsync(studioCommandCancellation.Token);
        MainWindow window = new();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        studioCommandCancellation.Cancel();
        studioCommandCancellation.Dispose();
        if(ownsSingletonMutex)
        {
            try
            {
                singletonMutex?.ReleaseMutex();
            }
            catch(ApplicationException)
            {
                // 进程退出竞态下互斥体可能已被系统释放。
            }
        }
        singletonMutex?.Dispose();
        base.OnExit(e);
    }

    internal ZhujieStudioCommand? TakePendingStudioCommand()
    {
        lock(pendingStudioCommandLock)
        {
            ZhujieStudioCommand? command = pendingStudioCommand;
            pendingStudioCommand = null;
            return command;
        }
    }

    private static ZhujieStudioCommand ParseStartupCommand(IReadOnlyList<string> args)
    {
        for(int index = 0; index < args.Count; index++)
        {
            if(!string.Equals(args[index], "--ai-project", StringComparison.Ordinal)) continue;
            if(index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
                throw new ArgumentException("--ai-project 缺少工程目录。");
            return ZhujieStudioCommand.OpenProject(args[index + 1]);
        }
        return ZhujieStudioCommand.Activate();
    }

    private void QueuePendingStudioCommand(ZhujieStudioCommand command)
    {
        lock(pendingStudioCommandLock) pendingStudioCommand = command;
    }

    private async Task ListenForStudioCommandsAsync(CancellationToken cancellationToken)
    {
        while(!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using NamedPipeServerStream pipe = new(
                    ZhujieStudioBridgeProtocol.PipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                ZhujieStudioCommand command = await ZhujieStudioBridgeProtocol.ReadAsync(
                    pipe,
                    cancellationToken).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() => DispatchStudioCommand(command));
            }
            catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch(Exception exception) when(exception is IOException or InvalidDataException or JsonException)
            {
                // 单次命令损坏不应终止后续实例唤醒。
            }
        }
    }

    private void DispatchStudioCommand(ZhujieStudioCommand command)
    {
        if(MainWindow is MainWindow window && window.IsLoaded)
        {
            window.HandleStudioCommand(command);
            return;
        }
        QueuePendingStudioCommand(command);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
