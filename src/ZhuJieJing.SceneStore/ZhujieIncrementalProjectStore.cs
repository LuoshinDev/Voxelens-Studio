using System.Security.Cryptography;
using System.Text;
using ZhuJieJing.Core;
using ZhuJieJing.Core.Collaboration;

namespace ZhuJieJing.SceneStore;

/// <summary>Owns the revision-backed AI construction workflow for one 筑界镜 project.</summary>
public sealed class ZhujieIncrementalProjectStore : IAsyncDisposable
{
    private readonly ZjjSceneStore sceneStore;

    private ZhujieIncrementalProjectStore(ZhujieProjectWorkspace workspace, ZjjSceneStore sceneStore)
    {
        Workspace = workspace;
        this.sceneStore = sceneStore;
    }

    public ZhujieProjectWorkspace Workspace { get; }

    public static async Task<ZhujieIncrementalProjectStore> CreateAsync(
        string projectPath,
        string? displayName = null,
        ZhujieProjectTarget? target = null,
        string? projectsRoot = null,
        CancellationToken cancellationToken = default)
    {
        ZhujieProjectWorkspace workspace = await ZhujieProjectWorkspace.CreateAsync(
            projectPath,
            displayName,
            target,
            projectsRoot,
            cancellationToken).ConfigureAwait(false);
        ZjjSceneStore store = await ZjjSceneStore.CreateAsync(workspace.Layout.SceneStorePath, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new ZhujieIncrementalProjectStore(workspace, store);
    }

    public static async Task<ZhujieIncrementalProjectStore> OpenAsync(
        string projectPath,
        string? projectsRoot = null,
        CancellationToken cancellationToken = default)
    {
        ZhujieProjectWorkspace workspace = await ZhujieProjectWorkspace.OpenAsync(projectPath, projectsRoot, cancellationToken)
            .ConfigureAwait(false);
        ZjjSceneStore store = await ZjjSceneStore.OpenAsync(workspace.Layout.SceneStorePath, cancellationToken).ConfigureAwait(false);
        return new ZhujieIncrementalProjectStore(workspace, store);
    }

    public Task<string> GetCurrentRevisionAsync(CancellationToken cancellationToken = default) =>
        sceneStore.GetCurrentRevisionAsync(cancellationToken);

    public Task<StoredSceneSnapshot> ReadHeadSnapshotAsync(
        IReadOnlyCollection<SectionCoordinate>? sections = null,
        CancellationToken cancellationToken = default) =>
        sceneStore.ReadHeadSnapshotAsync(sections, cancellationToken);

    public Task<StoredSceneSnapshot> ReadSnapshotAsync(
        string revision,
        IReadOnlyCollection<SectionCoordinate>? sections = null,
        CancellationToken cancellationToken = default) =>
        sceneStore.ReadSnapshotAsync(revision, sections, cancellationToken);

    public Task<SceneDelta> ReadDeltaAsync(string revision, CancellationToken cancellationToken = default) =>
        sceneStore.ReadDeltaAsync(revision, cancellationToken);

    public Task<IReadOnlyList<ZhujieBatchRecord>> ListBatchesAsync(CancellationToken cancellationToken = default) =>
        Workspace.ListBatchesAsync(cancellationToken);

    public async Task<ZhujiePatchPublishResult> PublishPatchAsync(
        string draftPath,
        CancellationToken cancellationToken = default)
    {
        ZhujiePatchDraft draft = await Workspace.ReadPatchDraftAsync(draftPath, cancellationToken).ConfigureAwait(false);
        string currentRevision = await sceneStore.GetCurrentRevisionAsync(cancellationToken).ConfigureAwait(false);
        if(!string.Equals(draft.Plan.Base!.Revision, currentRevision, StringComparison.Ordinal))
            throw new SceneRevisionConflictException(draft.Plan.Base.Revision, currentRevision);

        IReadOnlySet<SectionCoordinate> touchedSections = ZjjPlanSectionIndex.GetTouchedSections(draft.Plan);
        StoredSceneSnapshot baseSnapshot = await sceneStore.ReadHeadSnapshotAsync(touchedSections, cancellationToken).ConfigureAwait(false);
        CompilationResult compilation = new ZjjPlanCompiler().Compile(draft.Plan, baseSnapshot, cancellationToken);
        SceneCommitResult commit = await sceneStore.CommitAsync(compilation.Delta, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ZhujieBatchRecord> existing = await Workspace.ListBatchesAsync(cancellationToken).ConfigureAwait(false);
        long sequence = existing.Count + 1L;
        string planRelativePath = $"{ZhujieProjectLayout.ModulesRelativePath}/{draft.Plan.Module.Id}/batches/{sequence:D8}-{draft.PlanSha256[..12]}.zz";
        ZhujieBatchRecord batch = new()
        {
            BatchId = Guid.NewGuid(),
            ProjectId = Workspace.Manifest.ProjectId,
            Sequence = sequence,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Kind = ZhujieBatchKind.Construction,
            Revision = commit.RevisionId,
            BaseRevision = commit.ParentRevisionId,
            PlanSha256 = draft.PlanSha256,
            PlanId = draft.Plan.PlanId,
            Module = draft.Plan.Module,
            PlanFile = planRelativePath,
            SceneDeltaHash = commit.DeltaHash,
            ChangedVoxelCount = commit.ChangeCount,
            SectionCount = compilation.Delta.Sections.Count,
            Dimension = draft.Plan.Dimension,
            VisualProfile = draft.Plan.VisualProfile,
            SpawnPoint = draft.Plan.SpawnPoint ?? new BlockPosition(0, 32, 0),
        };
        await Workspace.WriteBatchAsync(batch, draft.Utf8Json, cancellationToken).ConfigureAwait(false);
        return new ZhujiePatchPublishResult(
            batch,
            Workspace.Layout.ResolveProjectPath(planRelativePath),
            draft.PlanSha256,
            commit.RevisionId,
            commit.ParentRevisionId,
            commit.DeltaHash,
            commit.ChangeCount,
            compilation.Delta.Sections.Count,
            compilation.AttemptedWriteCount,
            compilation.AppliedWriteCount,
            compilation.SkippedWriteCount);
    }

    public async Task<ZhujieBatchRecord> RevertBatchAsync(
        string revision,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ZhujieBatchRecord> batches = await Workspace.ListBatchesAsync(cancellationToken).ConfigureAwait(false);
        ZhujieBatchRecord target = batches.SingleOrDefault(batch => string.Equals(batch.Revision, revision, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"找不到 revision {revision} 对应的施工批次。");
        SceneDelta inverse = await sceneStore.CreateSelectiveInverseDeltaAsync(revision, cancellationToken).ConfigureAwait(false);
        SceneCommitResult commit = await sceneStore.CommitAsync(inverse, cancellationToken).ConfigureAwait(false);
        long sequence = batches.Count + 1L;
        string document = $$"""
            {
              "format": "zhujie.revert/1",
              "revertedRevision": "{{revision}}",
              "baseRevision": "{{commit.ParentRevisionId}}"
            }
            """;
        byte[] bytes = Encoding.UTF8.GetBytes(document.Replace("\r\n", "\n", StringComparison.Ordinal));
        string documentSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string planRelativePath = $"{ZhujieProjectLayout.ModulesRelativePath}/{target.Module.Id}/batches/{sequence:D8}-{documentSha[..12]}.revert.json";
        ZhujieBatchRecord batch = new()
        {
            BatchId = Guid.NewGuid(),
            ProjectId = Workspace.Manifest.ProjectId,
            Sequence = sequence,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Kind = ZhujieBatchKind.Revert,
            Revision = commit.RevisionId,
            BaseRevision = commit.ParentRevisionId,
            PlanSha256 = documentSha,
            PlanId = $"revert:{target.PlanId}",
            Module = target.Module,
            PlanFile = planRelativePath,
            RevertedRevision = target.Revision,
            SceneDeltaHash = commit.DeltaHash,
            ChangedVoxelCount = commit.ChangeCount,
            SectionCount = inverse.Sections.Count,
            Dimension = target.Dimension,
            VisualProfile = target.VisualProfile,
            SpawnPoint = target.SpawnPoint,
        };
        await Workspace.WriteBatchAsync(batch, bytes, cancellationToken).ConfigureAwait(false);
        return batch;
    }

    public ValueTask DisposeAsync() => sceneStore.DisposeAsync();
}
