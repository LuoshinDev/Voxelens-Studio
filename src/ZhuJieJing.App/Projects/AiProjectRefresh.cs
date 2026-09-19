using ZhuJieJing.Core;
using ZhuJieJing.Core.Collaboration;
using ZhuJieJing.SceneStore;

namespace ZhuJieJing.App.Projects;

[Flags]
public enum AiProjectRefreshKind
{
    None = 0,
    Dashboard = 1,
    RevisionWriteHint = 2,
    RevisionIntegrityProbe = 4,
}

public sealed class AiProjectRefreshRequestedEventArgs(AiProjectRefreshKind kind) : EventArgs
{
    public AiProjectRefreshKind Kind { get; } = kind;
}

internal enum AiRevisionLoadKind
{
    Empty,
    Unchanged,
    Snapshot,
}

internal sealed record AiRevisionSnapshot(
    string Revision,
    bool IsIncremental,
    ZhujieBatchRecord? Batch,
    IReadOnlyList<ZhujieBatchRecord> Batches,
    StoredSceneSnapshot Scene,
    SceneDelta? Delta,
    ZjjPlan? Plan,
    string? PlanPath);

internal readonly record struct AiRevisionLoadResult(
    AiRevisionLoadKind Kind,
    AiRevisionSnapshot? Snapshot);

internal readonly record struct AiRevisionLoadProgress(
    string Stage,
    int CompletedSteps,
    int TotalSteps);

internal readonly record struct AiProjectRefreshBatch(
    AiProjectRefreshKind Kind,
    bool ForceRevisionReload,
    bool FrameFirstPreview,
    long Sequence);

internal sealed class AiProjectRefreshPump
{
    private readonly object gate = new();
    private readonly Func<AiProjectRefreshBatch, Task> handler;
    private readonly Action supersedeActiveRevisionWork;
    private AiProjectRefreshKind pendingKind;
    private Task? runner;
    private long sequence;
    private bool pendingForceRevisionReload;
    private bool pendingFrameFirstPreview;
    private bool stopped;

    public AiProjectRefreshPump(Func<AiProjectRefreshBatch, Task> handler, Action supersedeActiveRevisionWork)
    {
        this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
        this.supersedeActiveRevisionWork = supersedeActiveRevisionWork ??
            throw new ArgumentNullException(nameof(supersedeActiveRevisionWork));
    }

    public Task EnqueueAsync(
        AiProjectRefreshKind kind,
        bool forceRevisionReload = false,
        bool frameFirstPreview = false)
    {
        lock(gate)
        {
            if(stopped) return Task.CompletedTask;
            pendingKind |= kind;
            pendingForceRevisionReload |= forceRevisionReload;
            pendingFrameFirstPreview |= frameFirstPreview;
            sequence++;
            if(forceRevisionReload || (kind & AiProjectRefreshKind.RevisionWriteHint) != 0)
                supersedeActiveRevisionWork();
            runner ??= RunAsync();
            return runner;
        }
    }

    public void Stop()
    {
        lock(gate)
        {
            if(stopped) return;
            stopped = true;
            pendingKind = AiProjectRefreshKind.None;
            pendingForceRevisionReload = false;
            pendingFrameFirstPreview = false;
            supersedeActiveRevisionWork();
        }
    }

    private async Task RunAsync()
    {
        await Task.Yield();
        try
        {
            while(true)
            {
                AiProjectRefreshBatch batch;
                lock(gate)
                {
                    if(stopped || pendingKind == AiProjectRefreshKind.None)
                    {
                        runner = null;
                        return;
                    }
                    batch = new AiProjectRefreshBatch(
                        pendingKind,
                        pendingForceRevisionReload,
                        pendingFrameFirstPreview,
                        sequence);
                    pendingKind = AiProjectRefreshKind.None;
                    pendingForceRevisionReload = false;
                    pendingFrameFirstPreview = false;
                }
                await handler(batch);
            }
        }
        catch
        {
            lock(gate)
            {
                runner = null;
                if(!stopped && pendingKind != AiProjectRefreshKind.None) runner = RunAsync();
            }
            throw;
        }
    }
}
