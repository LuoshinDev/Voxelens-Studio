using ZhuJieJing.Core;

namespace ZhuJieJing.SceneStore;

public sealed record SceneCommitResult(
    string RevisionId,
    string ParentRevisionId,
    string DeltaHash,
    int ChangeCount);

public sealed record StoredSceneRevision(
    long Sequence,
    string RevisionId,
    string? ParentRevisionId,
    string? DeltaHash,
    int ChangeCount);

public sealed record StoredSceneSection(
    SectionCoordinate Coordinate,
    IReadOnlyDictionary<int, BlockState> Blocks);

public sealed class StoredSceneSnapshot : ISceneSnapshot
{
    private readonly IReadOnlyDictionary<SectionCoordinate, StoredSceneSection> sections;

    public StoredSceneSnapshot(string revision, IEnumerable<StoredSceneSection> sections)
    {
        if(string.IsNullOrWhiteSpace(revision)) throw new ArgumentException("Revision 不能为空。", nameof(revision));
        Revision = revision;
        this.sections = sections.ToDictionary(static section => section.Coordinate);
        Sections = this.sections.Values.OrderBy(static section => section.Coordinate).ToArray();
    }

    public string Revision { get; }

    public IReadOnlyList<StoredSceneSection> Sections { get; }

    public BlockState GetBlock(string dimension, BlockPosition position)
    {
        SectionCoordinate coordinate = SectionCoordinate.FromBlock(dimension, position);
        return sections.TryGetValue(coordinate, out StoredSceneSection? section) &&
               section.Blocks.TryGetValue(SectionCoordinate.LocalIndex(position), out BlockState? state)
            ? state
            : BlockState.Air;
    }
}

public sealed class SceneRevisionConflictException : Exception
{
    public SceneRevisionConflictException(string expectedRevision, string actualRevision)
        : base($"场景 revision 冲突：提交基于 {expectedRevision}，当前为 {actualRevision}。")
    {
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    public string ExpectedRevision { get; }

    public string ActualRevision { get; }
}

public sealed class SceneVoxelConflictException : Exception
{
    public SceneVoxelConflictException(
        SectionCoordinate section,
        int localIndex,
        BlockState expected,
        BlockState actual,
        string operationId)
        : base($"场景体素冲突：{section} 的 local index {localIndex} 当前为 {actual.CanonicalKey}，操作 {operationId} 预期为 {expected.CanonicalKey}。")
    {
        Section = section;
        LocalIndex = localIndex;
        Expected = expected;
        Actual = actual;
        OperationId = operationId;
    }

    public SectionCoordinate Section { get; }

    public int LocalIndex { get; }

    public BlockState Expected { get; }

    public BlockState Actual { get; }

    public string OperationId { get; }
}

public sealed class SceneStoreCorruptionException : Exception
{
    public SceneStoreCorruptionException(string message) : base(message)
    {
    }
}
