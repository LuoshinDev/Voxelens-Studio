using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ZhuJieJing.Core;

public readonly record struct SectionCoordinate(string Dimension, int X, int Y, int Z) : IComparable<SectionCoordinate>
{
    public int CompareTo(SectionCoordinate other)
    {
        var dimensionComparison = string.Compare(Dimension, other.Dimension, StringComparison.Ordinal);
        if (dimensionComparison != 0) return dimensionComparison;

        var xComparison = X.CompareTo(other.X);
        if (xComparison != 0) return xComparison;

        var yComparison = Y.CompareTo(other.Y);
        return yComparison != 0 ? yComparison : Z.CompareTo(other.Z);
    }

    public static SectionCoordinate FromBlock(string dimension, BlockPosition position) => new(
        dimension,
        FloorDivideBySixteen(position.X),
        FloorDivideBySixteen(position.Y),
        FloorDivideBySixteen(position.Z));

    public static int LocalIndex(BlockPosition position)
    {
        var localX = FloorModSixteen(position.X);
        var localY = FloorModSixteen(position.Y);
        var localZ = FloorModSixteen(position.Z);
        return localY << 8 | localZ << 4 | localX;
    }

    public BlockPosition ToBlockPosition(int localIndex)
    {
        if (localIndex is < 0 or >= 4096) throw new ArgumentOutOfRangeException(nameof(localIndex));
        var localX = localIndex & 15;
        var localZ = localIndex >> 4 & 15;
        var localY = localIndex >> 8 & 15;
        return new BlockPosition(
            checked(X * 16 + localX),
            checked(Y * 16 + localY),
            checked(Z * 16 + localZ));
    }

    private static int FloorDivideBySixteen(int value) => value >= 0 ? value / 16 : (value + 1) / 16 - 1;

    private static int FloorModSixteen(int value) => value - FloorDivideBySixteen(value) * 16;
}

public sealed record VoxelChange
{
    public VoxelChange(int localIndex, BlockState before, BlockState after, string operationId)
    {
        if (localIndex is < 0 or >= 4096) throw new ArgumentOutOfRangeException(nameof(localIndex));
        if (string.IsNullOrWhiteSpace(operationId)) throw new ArgumentException("OperationId 不能为空。", nameof(operationId));

        LocalIndex = localIndex;
        Before = before ?? throw new ArgumentNullException(nameof(before));
        After = after ?? throw new ArgumentNullException(nameof(after));
        OperationId = operationId;
    }

    public int LocalIndex { get; }

    public BlockState Before { get; }

    public BlockState After { get; }

    public string OperationId { get; }
}

public sealed class SectionDelta
{
    public SectionDelta(SectionCoordinate section, IEnumerable<VoxelChange> changes)
    {
        Section = section;
        var orderedChanges = changes.OrderBy(change => change.LocalIndex).ToArray();
        if (orderedChanges.Length == 0) throw new ArgumentException("SectionDelta 至少需要一项变化。", nameof(changes));
        if (orderedChanges.Select(change => change.LocalIndex).Distinct().Count() != orderedChanges.Length)
        {
            throw new ArgumentException("同一 Section 中不能出现重复体素索引。", nameof(changes));
        }

        Changes = orderedChanges;
        StableHash = DeltaHash.ComputeSection(this);
    }

    public SectionCoordinate Section { get; }

    public IReadOnlyList<VoxelChange> Changes { get; }

    public string StableHash { get; }
}

public sealed class SceneDelta
{
    public SceneDelta(string baseRevision, IEnumerable<SectionDelta> sections)
    {
        if (string.IsNullOrWhiteSpace(baseRevision)) throw new ArgumentException("BaseRevision 不能为空。", nameof(baseRevision));
        BaseRevision = baseRevision;

        var orderedSections = sections.OrderBy(section => section.Section).ToArray();
        if (orderedSections.Select(section => section.Section).Distinct().Count() != orderedSections.Length)
        {
            throw new ArgumentException("SceneDelta 中不能出现重复 Section。", nameof(sections));
        }

        Sections = orderedSections;
        StableHash = DeltaHash.ComputeScene(this);
    }

    public string BaseRevision { get; }

    public IReadOnlyList<SectionDelta> Sections { get; }

    public string StableHash { get; }

    public int ChangedVoxelCount => Sections.Sum(section => section.Changes.Count);

    public SceneDelta Invert(string baseRevision) => new(
        baseRevision,
        Sections.Select(section => new SectionDelta(
            section.Section,
            section.Changes.Select(change => new VoxelChange(change.LocalIndex, change.After, change.Before, change.OperationId)))));
}

internal static class DeltaHash
{
    public static string ComputeSection(SectionDelta section)
    {
        using var hash = new StableHashBuilder();
        hash.Add("zhujie.section-delta/1");
        hash.Add(section.Section.Dimension);
        hash.Add(section.Section.X);
        hash.Add(section.Section.Y);
        hash.Add(section.Section.Z);
        hash.Add(section.Changes.Count);
        foreach (var change in section.Changes)
        {
            hash.Add(change.LocalIndex);
            hash.Add(change.Before.CanonicalKey);
            hash.Add(change.After.CanonicalKey);
            hash.Add(change.OperationId);
        }

        return hash.Finish();
    }

    public static string ComputeScene(SceneDelta scene)
    {
        using var hash = new StableHashBuilder();
        hash.Add("zhujie.scene-delta/1");
        hash.Add(scene.BaseRevision);
        hash.Add(scene.Sections.Count);
        foreach (var section in scene.Sections) hash.Add(section.StableHash);
        return hash.Finish();
    }
}

internal sealed class StableHashBuilder : IDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool _finished;

    public void Add(int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        _hash.AppendData(buffer);
    }

    public void Add(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Add(bytes.Length);
        _hash.AppendData(bytes);
    }

    public string Finish()
    {
        if (_finished) throw new InvalidOperationException("稳定哈希已经完成。 ");
        _finished = true;
        return Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
    }

    public void Dispose() => _hash.Dispose();
}
