using System.IO;
using ZhuJieJing.Core;
using ZhuJieJing.Core.Collaboration;
using ZhuJieJing.SceneStore;

namespace ZhuJieJing.App.Projects;

public sealed record AiProjectDescriptor(
    Guid ProjectId,
    string DisplayName,
    string ProjectDirectory,
    string SceneStorePath,
    string TargetDimension);

public sealed record AiInstructionHistoryItem(
    Guid InstructionId,
    string DisplayText,
    string ReceiptText,
    string StateText,
    bool CanDelete,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? AcknowledgedAtUtc);

public sealed record AiProjectDashboard(
    ZhujieArchitectStatus? ArchitectStatus,
    IReadOnlyList<AiInstructionHistoryItem> Instructions,
    IReadOnlyList<ZhujieBatchRecord> Batches,
    string? ReadError);

public sealed record AiInstructionContextSnapshot(
    string Dimension,
    BlockPosition? Camera,
    string? BasedOnRevision);

/// <summary>Monitors protocol changes with a low-frequency metadata probe for missed file events.</summary>
public sealed class AiProjectSession : IDisposable, IAsyncDisposable
{
    public static string DefaultWorkspaceRoot => ZhujieProjectLayout.DefaultProjectsRoot;
    internal static readonly TimeSpan FileChangeDebounce = TimeSpan.FromMilliseconds(200);
    internal static readonly TimeSpan IntegrityRescanInterval = TimeSpan.FromSeconds(30);

    private readonly ZhujieIncrementalProjectStore projectStore;
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly AiProjectDashboardCache dashboardCache;
    private readonly Timer debounceTimer;
    private readonly Timer integrityRescanTimer;
    // Kept alive until GC: canceled entrants may still be unwinding after the store
    // is closed and must never encounter a disposed semaphore while releasing it.
    private readonly SemaphoreSlim readGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private readonly object disposalGate = new();
    private Task? disposalTask;
    private int pendingRefreshKind;
    private volatile bool disposed;

    private AiProjectSession(ZhujieIncrementalProjectStore projectStore)
    {
        this.projectStore = projectStore;
        lifetimeToken = lifetime.Token;
        ZhujieProjectWorkspace workspace = projectStore.Workspace;
        dashboardCache = new AiProjectDashboardCache(workspace);
        Project = new AiProjectDescriptor(
            workspace.Manifest.ProjectId,
            workspace.Manifest.DisplayName,
            workspace.Layout.ProjectDirectory,
            workspace.Layout.SceneStorePath,
            workspace.Manifest.Target.Dimension);
        debounceTimer = new Timer(static state => ((AiProjectSession)state!).FlushRefresh(), this, Timeout.Infinite, Timeout.Infinite);
        integrityRescanTimer = new Timer(
            static state => ((AiProjectSession)state!).RaiseRefresh(
                AiProjectRefreshKind.Dashboard | AiProjectRefreshKind.RevisionIntegrityProbe),
            this,
            IntegrityRescanInterval,
            IntegrityRescanInterval);
        foreach(string directory in new[] { Path.GetDirectoryName(workspace.Layout.SceneStorePath)!,
                    workspace.Layout.InstructionsDirectory, workspace.Layout.AcknowledgementsDirectory,
                    workspace.Layout.BatchesDirectory, workspace.Layout.ReceiptsDirectory })
        {
            FileSystemWatcher watcher = new(directory)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                Filter = "*",
            };
            watcher.Changed += OnProjectChanged;
            watcher.Created += OnProjectChanged;
            watcher.Deleted += OnProjectChanged;
            watcher.Renamed += OnProjectChanged;
            watcher.Error += OnWatcherError;
            watchers.Add(watcher);
            watcher.EnableRaisingEvents = true;
        }
    }

    public event EventHandler<AiProjectRefreshRequestedEventArgs>? RefreshRequested;

    public AiProjectDescriptor Project { get; }

    public ZhujieProjectWorkspace Workspace => projectStore.Workspace;

    public static Task<AiProjectSession> OpenAsync(string projectDirectory, CancellationToken cancellationToken = default) =>
        OpenAsync(projectDirectory, DefaultWorkspaceRoot, cancellationToken);

    public static async Task<AiProjectSession> OpenAsync(
        string projectDirectory,
        string projectsRoot,
        CancellationToken cancellationToken = default) =>
        new(await ZhujieIncrementalProjectStore.OpenAsync(projectDirectory, projectsRoot, cancellationToken).ConfigureAwait(false));

    internal async Task<AiRevisionLoadResult> LoadRevisionAsync(
        string? observedRevision,
        string? requestedRevision,
        bool force,
        CancellationToken cancellationToken = default,
        IProgress<AiRevisionLoadProgress>? progress = null)
    {
        SessionOperation operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken = operation.Token;
        try
        {
            // Microsoft.Data.Sqlite can execute its async APIs synchronously. Keep
            // snapshot reconstruction off the dispatcher even when the gate is free.
            return await Task.Run(async () =>
            {
            const int totalSteps = 5;
            progress?.Report(new AiRevisionLoadProgress(UiText.Get("正在定位工程当前 revision"), 0, totalSteps));
            string head = await projectStore.GetCurrentRevisionAsync(cancellationToken).ConfigureAwait(false);
            progress?.Report(new AiRevisionLoadProgress(UiText.Get("已定位当前 revision，正在读取批次索引"), 1, totalSteps));
            string target = string.IsNullOrWhiteSpace(requestedRevision) ? head : requestedRevision;
            if(!force && string.Equals(target, observedRevision, StringComparison.Ordinal))
            {
                progress?.Report(new AiRevisionLoadProgress(UiText.Get("当前已是最新施工批次"), totalSteps, totalSteps));
                return new AiRevisionLoadResult(AiRevisionLoadKind.Unchanged, null);
            }

            IReadOnlyList<ZhujieBatchRecord> batches = await projectStore.ListBatchesAsync(cancellationToken).ConfigureAwait(false);
            progress?.Report(new AiRevisionLoadProgress(UiText.Format($"已读取 {batches.Count:N0} 个批次，正在匹配目标 revision"), 2, totalSteps));
            ZhujieBatchRecord? batch = batches.LastOrDefault(item => string.Equals(item.Revision, target, StringComparison.Ordinal));
            if(batch is null && string.Equals(target, "root", StringComparison.Ordinal))
            {
                progress?.Report(new AiRevisionLoadProgress(UiText.Get("工程尚无施工批次，正在读取空场景"), 3, totalSteps));
                StoredSceneSnapshot empty = await projectStore.ReadSnapshotAsync("root", cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                progress?.Report(new AiRevisionLoadProgress(UiText.Get("空场景已就绪"), totalSteps, totalSteps));
                return new AiRevisionLoadResult(
                    AiRevisionLoadKind.Empty,
                    new AiRevisionSnapshot("root", false, null, batches, empty, null, null, null));
            }
            if(batch is null) throw new InvalidDataException($"Revision {target} 没有对应的施工批次元数据。");

            progress?.Report(new AiRevisionLoadProgress(UiText.Format($"正在读取第 {batch.Sequence:N0} 批 Section 增量"), 2, totalSteps));
            SceneDelta delta = await projectStore.ReadDeltaAsync(target, cancellationToken).ConfigureAwait(false);
            progress?.Report(new AiRevisionLoadProgress(UiText.Format($"第 {batch.Sequence:N0} 批增量已读取，正在还原场景快照"), 3, totalSteps));
            bool incremental = string.Equals(target, head, StringComparison.Ordinal) &&
                               string.Equals(observedRevision, batch.BaseRevision, StringComparison.Ordinal);
            IReadOnlyCollection<SectionCoordinate>? sectionFilter = incremental
                ? delta.Sections.Select(static section => section.Section).ToArray()
                : null;
            StoredSceneSnapshot scene = await projectStore.ReadSnapshotAsync(target, sectionFilter, cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(new AiRevisionLoadProgress(UiText.Format($"已还原 {scene.Sections.Count:N0} 个 Section，正在校验施工蓝图"), 4, totalSteps));
            string planPath = Workspace.Layout.ResolveProjectPath(batch.PlanFile);
            ZjjPlan? plan = null;
            if(batch.Kind == ZhujieBatchKind.Construction && planPath.EndsWith(".zz", StringComparison.OrdinalIgnoreCase))
            {
                FileInfo info = new(planPath);
                if(!info.Exists) throw new FileNotFoundException("施工批次补丁不存在。", planPath);
                if(info.Length > ZhujieProjectWorkspace.MaximumPlanBytes) throw new InvalidDataException("施工批次补丁过大。");
                byte[] bytes = await File.ReadAllBytesAsync(planPath, cancellationToken).ConfigureAwait(false);
                plan = Workspace.ParseAndValidatePatch(bytes);
            }
            progress?.Report(new AiRevisionLoadProgress(UiText.Format($"第 {batch.Sequence:N0} 批施工数据已就绪"), totalSteps, totalSteps));
            return new AiRevisionLoadResult(
                AiRevisionLoadKind.Snapshot,
                new AiRevisionSnapshot(target, incremental, batch, batches, scene, delta, plan, planPath));
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operation.Dispose();
        }
    }

    public Task<AiProjectDashboard> ReadDashboardAsync(
        AiProjectRefreshKind refreshKind,
        CancellationToken cancellationToken = default)
        => RunOperationAsync(token => dashboardCache.ReadAsync(refreshKind, token), cancellationToken);

    public Task<AiProjectDashboard> ReadDashboardAsync(CancellationToken cancellationToken = default) =>
        ReadDashboardAsync(AiProjectRefreshKind.Dashboard, cancellationToken);

    public async Task<Guid> QueueInstructionAsync(
        ZhujieInstructionKind kind,
        string text,
        AiInstructionContextSnapshot context,
        CancellationToken cancellationToken = default)
    {
        using SessionOperation operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken = operation.Token;
        var instruction = new ZhujieInstruction
        {
            InstructionId = Guid.NewGuid(),
            ProjectId = Project.ProjectId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Kind = kind,
            Text = text,
            BasedOnRevision = context.BasedOnRevision,
            Context = new ZhujieInstructionContext
            {
                Dimension = context.Dimension,
                Camera = context.Camera is BlockPosition camera
                    ? new ZhujieCameraPose { X = camera.X, Y = camera.Y, Z = camera.Z }
                    : null,
            },
        };
        string path = await Workspace.EnqueueInstructionAsync(instruction, cancellationToken).ConfigureAwait(false);
        dashboardCache.Invalidate(path);
        return instruction.InstructionId;
    }

    public async Task<bool> WritePreviewReceiptAsync(ZhujiePreviewReceipt receipt, CancellationToken cancellationToken = default)
    {
        using SessionOperation operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken = operation.Token;
        bool written = await Workspace.WritePreviewReceiptAsync(receipt, cancellationToken).ConfigureAwait(false);
        if(written) dashboardCache.Invalidate(Path.Combine(Workspace.Layout.ReceiptsDirectory, $"{receipt.PlanSha256}.json"));
        return written;
    }

    public async Task<bool> DeleteQueuedInstructionAsync(Guid instructionId, CancellationToken cancellationToken = default)
    {
        using SessionOperation operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken = operation.Token;
        bool deleted = await Workspace.DeleteQueuedInstructionAsync(instructionId, cancellationToken).ConfigureAwait(false);
        if(deleted) dashboardCache.Invalidate(Workspace.Layout.InstructionsDirectory);
        return deleted;
    }

    public async Task<ZhujieBatchRecord> RevertBatchAsync(string revision, CancellationToken cancellationToken = default)
    {
        using SessionOperation operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken = operation.Token;
        ZhujieBatchRecord batch = await Task.Run(() => projectStore.RevertBatchAsync(revision, cancellationToken), cancellationToken).ConfigureAwait(false);
        dashboardCache.Invalidate(Workspace.Layout.BatchesDirectory);
        return batch;
    }

    public void Dispose() => _ = BeginDispose();

    public ValueTask DisposeAsync() => new(BeginDispose());

    private Task BeginDispose()
    {
        TaskCompletionSource completion;
        lock(disposalGate)
        {
            if(disposalTask is not null) return disposalTask;
            disposed = true;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposalTask = completion.Task;
        }
        // Stop file events and cancel in-flight work immediately, without making the
        // UI thread wait for a database read or an incremental publish to unwind.
        Exception? shutdownError = null;
        try
        {
            foreach(FileSystemWatcher watcher in watchers) watcher.Dispose();
            debounceTimer.Dispose();
            integrityRescanTimer.Dispose();
        }
        catch(Exception exception) { shutdownError = exception; }
        try { lifetime.Cancel(); }
        catch(Exception exception) { shutdownError ??= exception; }
        _ = Task.Run(async () =>
        {
            try
            {
                await readGate.WaitAsync().ConfigureAwait(false);
                try { await projectStore.DisposeAsync().ConfigureAwait(false); }
                finally { readGate.Release(); lifetime.Dispose(); }
                if(shutdownError is not null) throw shutdownError;
                completion.TrySetResult();
            }
            catch(Exception exception) { completion.TrySetException(exception); }
        });
        // Synchronous callers intentionally do not await; observe final cleanup
        // failures here while preserving them for callers of DisposeAsync.
        _ = completion.Task.ContinueWith(static task => System.Diagnostics.Trace.TraceError(
            "AI 工程后台释放失败：{0}", task.Exception), CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return completion.Task;
    }

    private async Task<T> RunOperationAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        using SessionOperation operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await action(operation.Token).ConfigureAwait(false);
    }

    private async Task<SessionOperation> EnterOperationAsync(CancellationToken cancellationToken)
    {
        if(disposed) throw new OperationCanceledException("AI 工程会话已关闭。", lifetimeToken);
        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
        try
        {
            await readGate.WaitAsync(linked.Token).ConfigureAwait(false);
            if(disposed || linked.IsCancellationRequested)
            {
                readGate.Release();
                throw new OperationCanceledException("AI 工程操作已取消。", linked.Token);
            }
            return new SessionOperation(readGate, linked);
        }
        catch { linked.Dispose(); throw; }
    }

    private sealed class SessionOperation(SemaphoreSlim gate, CancellationTokenSource cancellation) : IDisposable
    {
        private int released;
        public CancellationToken Token => cancellation.Token;
        public void Dispose()
        {
            if(Interlocked.Exchange(ref released, 1) != 0) return;
            gate.Release();
            cancellation.Dispose();
        }
    }

    private void OnProjectChanged(object sender, FileSystemEventArgs e)
    {
        if(disposed) return;
        AiProjectFileKind fileKind = AiProjectDashboardCache.ClassifyPath(Project.ProjectDirectory, e.FullPath);
        AiProjectFileKind oldKind = AiProjectFileKind.None;
        if(e is RenamedEventArgs renamed)
        {
            oldKind = AiProjectDashboardCache.ClassifyPath(Project.ProjectDirectory, renamed.OldFullPath);
            if(oldKind != AiProjectFileKind.None) dashboardCache.Invalidate(renamed.OldFullPath);
        }
        if(fileKind == AiProjectFileKind.None && oldKind == AiProjectFileKind.None) return;
        dashboardCache.Invalidate(e.FullPath);
        AiProjectRefreshKind kind = AiProjectRefreshKind.Dashboard;
        if(fileKind is AiProjectFileKind.Batch or AiProjectFileKind.Scene || oldKind is AiProjectFileKind.Batch or AiProjectFileKind.Scene)
            kind |= AiProjectRefreshKind.RevisionWriteHint;
        Interlocked.Or(ref pendingRefreshKind, (int)kind);
        try { debounceTimer.Change(FileChangeDebounce, Timeout.InfiniteTimeSpan); }
        catch(ObjectDisposedException) when(disposed) { }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        dashboardCache.RequestIntegrityProbe();
        RaiseRefresh(AiProjectRefreshKind.Dashboard | AiProjectRefreshKind.RevisionWriteHint);
    }

    private void FlushRefresh()
    {
        AiProjectRefreshKind kind = (AiProjectRefreshKind)Interlocked.Exchange(ref pendingRefreshKind, 0);
        if(kind != AiProjectRefreshKind.None) RaiseRefresh(kind);
    }

    private void RaiseRefresh(AiProjectRefreshKind kind)
    {
        if(!disposed) RefreshRequested?.Invoke(this, new AiProjectRefreshRequestedEventArgs(kind));
    }

}
