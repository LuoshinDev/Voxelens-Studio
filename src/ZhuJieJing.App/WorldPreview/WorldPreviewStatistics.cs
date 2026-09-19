using System.Buffers;
using System.IO;
using ZhuJieJing.Core;
using ZhuJieJing.Minecraft;

namespace ZhuJieJing.App.WorldPreview;

public readonly record struct WorldPreviewBlockCoordinate(long X, long Y, long Z);

public readonly record struct WorldPreviewBlockSize(long Width, long Height, long Depth);

/// <summary>Inclusive bounds of non-air blocks in absolute world coordinates.</summary>
public readonly record struct WorldPreviewOccupiedBounds
{
    public WorldPreviewOccupiedBounds(WorldPreviewBlockCoordinate minimum, WorldPreviewBlockCoordinate maximum)
    {
        if(minimum.X > maximum.X || minimum.Y > maximum.Y || minimum.Z > maximum.Z)
            throw new ArgumentOutOfRangeException(nameof(minimum), "Minimum occupied coordinate cannot exceed maximum.");

        Minimum = minimum;
        Maximum = maximum;
    }

    public WorldPreviewBlockCoordinate Minimum { get; }

    public WorldPreviewBlockCoordinate Maximum { get; }

    /// <summary>Stable translation origin for a local model representation.</summary>
    public WorldPreviewBlockCoordinate Origin => Minimum;

    public WorldPreviewBlockSize Size => new(
        checked(Maximum.X - Minimum.X + 1),
        checked(Maximum.Y - Minimum.Y + 1),
        checked(Maximum.Z - Minimum.Z + 1));

    public WorldPreviewOccupiedBounds Union(WorldPreviewOccupiedBounds other) => new(
        new WorldPreviewBlockCoordinate(
            Math.Min(Minimum.X, other.Minimum.X),
            Math.Min(Minimum.Y, other.Minimum.Y),
            Math.Min(Minimum.Z, other.Minimum.Z)),
        new WorldPreviewBlockCoordinate(
            Math.Max(Maximum.X, other.Maximum.X),
            Math.Max(Maximum.Y, other.Maximum.Y),
            Math.Max(Maximum.Z, other.Maximum.Z)));
}

/// <summary>Immutable statistics for exactly the normalized chunks included by the caller.</summary>
public sealed record WorldPreviewStatistics
{
    public WorldPreviewStatistics(
        long chunkCount,
        long sectionCount,
        long nonAirBlockCount,
        long blockEntityCount,
        WorldPreviewOccupiedBounds? occupiedBounds)
    {
        if(chunkCount < 0) throw new ArgumentOutOfRangeException(nameof(chunkCount));
        if(sectionCount < 0) throw new ArgumentOutOfRangeException(nameof(sectionCount));
        if(nonAirBlockCount < 0) throw new ArgumentOutOfRangeException(nameof(nonAirBlockCount));
        if(blockEntityCount < 0) throw new ArgumentOutOfRangeException(nameof(blockEntityCount));
        if(nonAirBlockCount == 0 && occupiedBounds is not null)
            throw new ArgumentException("Empty non-air statistics cannot have occupied bounds.", nameof(occupiedBounds));
        if(nonAirBlockCount > 0 && occupiedBounds is null)
            throw new ArgumentException("Non-air statistics require occupied bounds.", nameof(occupiedBounds));

        ChunkCount = chunkCount;
        SectionCount = sectionCount;
        NonAirBlockCount = nonAirBlockCount;
        BlockEntityCount = blockEntityCount;
        OccupiedBounds = occupiedBounds;
    }

    public static WorldPreviewStatistics Empty { get; } = new(0, 0, 0, 0, null);

    public long ChunkCount { get; }

    public long SectionCount { get; }

    public long NonAirBlockCount { get; }

    public long BlockEntityCount { get; }

    public WorldPreviewOccupiedBounds? OccupiedBounds { get; }

    public WorldPreviewBlockCoordinate? OccupiedMinimum => OccupiedBounds?.Minimum;

    public WorldPreviewBlockCoordinate? OccupiedMaximum => OccupiedBounds?.Maximum;

    public WorldPreviewBlockCoordinate? Origin => OccupiedBounds?.Origin;

    public WorldPreviewBlockSize? Size => OccupiedBounds?.Size;

    public WorldPreviewStatistics Merge(WorldPreviewStatistics other)
    {
        ArgumentNullException.ThrowIfNull(other);
        WorldPreviewOccupiedBounds? bounds = OccupiedBounds is null
            ? other.OccupiedBounds
            : other.OccupiedBounds is null
                ? OccupiedBounds
                : OccupiedBounds.Value.Union(other.OccupiedBounds.Value);
        return new WorldPreviewStatistics(
            checked(ChunkCount + other.ChunkCount),
            checked(SectionCount + other.SectionCount),
            checked(NonAirBlockCount + other.NonAirBlockCount),
            checked(BlockEntityCount + other.BlockEntityCount),
            bounds);
    }
}

/// <summary>
/// Caller-owned accumulator. Chunk inspection happens while normalized data is already flowing, so it performs
/// no source-world reads and needs no retained set of chunk addresses.
/// </summary>
public sealed class WorldPreviewStatisticsAccumulator
{
    private long chunkCount;
    private long sectionCount;
    private long nonAirBlockCount;
    private long blockEntityCount;
    private WorldPreviewOccupiedBounds? occupiedBounds;

    public void AddChunk(NormalizedMinecraftChunk chunk, CancellationToken cancellationToken = default)
    {
        Add(WorldPreviewStatisticsAnalyzer.AnalyzeChunk(chunk, cancellationToken));
    }

    public void Add(WorldPreviewStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        chunkCount = checked(chunkCount + statistics.ChunkCount);
        sectionCount = checked(sectionCount + statistics.SectionCount);
        nonAirBlockCount = checked(nonAirBlockCount + statistics.NonAirBlockCount);
        blockEntityCount = checked(blockEntityCount + statistics.BlockEntityCount);
        if(statistics.OccupiedBounds is WorldPreviewOccupiedBounds addedBounds)
        {
            occupiedBounds = occupiedBounds is WorldPreviewOccupiedBounds currentBounds
                ? currentBounds.Union(addedBounds)
                : addedBounds;
        }
    }

    public WorldPreviewStatistics Snapshot() => new(
        chunkCount,
        sectionCount,
        nonAirBlockCount,
        blockEntityCount,
        occupiedBounds);

    public void Clear()
    {
        chunkCount = 0;
        sectionCount = 0;
        nonAirBlockCount = 0;
        blockEntityCount = 0;
        occupiedBounds = null;
    }
}

public static class WorldPreviewStatisticsAnalyzer
{
    private const int BlocksPerSection = 4096;

    public static WorldPreviewStatistics AnalyzeChunk(
        NormalizedMinecraftChunk chunk,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<NormalizedMinecraftSection> sections = chunk.Sections ??
            throw new InvalidDataException($"Chunk {chunk.Address} has no normalized section collection.");
        IReadOnlyList<NormalizedMinecraftBlockEntity> blockEntities = chunk.BlockEntities ??
            throw new InvalidDataException($"Chunk {chunk.Address} has no normalized block-entity collection.");
        long nonAirCount = 0;
        bool hasOccupiedBlock = false;
        long minimumX = 0;
        long minimumY = 0;
        long minimumZ = 0;
        long maximumX = 0;
        long maximumY = 0;
        long maximumZ = 0;

        foreach(NormalizedMinecraftSection section in sections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(section is null)
                throw new InvalidDataException($"Chunk {chunk.Address} contains a null normalized section.");

            IReadOnlyList<BlockState> palette = section.Palette ??
                throw new InvalidDataException($"Section {section.Coordinate} has no palette.");
            if(palette.Count == 0)
                throw new InvalidDataException($"Section {section.Coordinate} has an empty palette.");

            ReadOnlySpan<ushort> indices = section.PaletteIndices.Span;
            if(indices.Length != BlocksPerSection)
            {
                throw new InvalidDataException(
                    $"Section {section.Coordinate} contains {indices.Length} palette indices instead of {BlocksPerSection}.");
            }

            byte[] nonAirPalette = ArrayPool<byte>.Shared.Rent(palette.Count);
            try
            {
                for(int paletteIndex = 0; paletteIndex < palette.Count; paletteIndex++)
                {
                    BlockState state = palette[paletteIndex] ??
                        throw new InvalidDataException($"Section {section.Coordinate} contains a null block state.");
                    nonAirPalette[paletteIndex] = state.IsAir ? (byte)0 : (byte)1;
                }

                long sectionX = (long)section.Coordinate.X * 16;
                long sectionY = (long)section.Coordinate.Y * 16;
                long sectionZ = (long)section.Coordinate.Z * 16;
                for(int localIndex = 0; localIndex < BlocksPerSection; localIndex++)
                {
                    if((localIndex & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                    ushort paletteIndex = indices[localIndex];
                    if(paletteIndex >= palette.Count)
                    {
                        throw new InvalidDataException(
                            $"Section {section.Coordinate} palette index {paletteIndex} is out of range.");
                    }
                    if(nonAirPalette[paletteIndex] == 0) continue;

                    nonAirCount = checked(nonAirCount + 1);
                    long x = sectionX + (localIndex & 15);
                    long z = sectionZ + (localIndex >> 4 & 15);
                    long y = sectionY + (localIndex >> 8 & 15);
                    if(!hasOccupiedBlock)
                    {
                        minimumX = maximumX = x;
                        minimumY = maximumY = y;
                        minimumZ = maximumZ = z;
                        hasOccupiedBlock = true;
                        continue;
                    }

                    minimumX = Math.Min(minimumX, x);
                    minimumY = Math.Min(minimumY, y);
                    minimumZ = Math.Min(minimumZ, z);
                    maximumX = Math.Max(maximumX, x);
                    maximumY = Math.Max(maximumY, y);
                    maximumZ = Math.Max(maximumZ, z);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(nonAirPalette);
            }
        }

        WorldPreviewOccupiedBounds? occupied = hasOccupiedBlock
            ? new WorldPreviewOccupiedBounds(
                new WorldPreviewBlockCoordinate(minimumX, minimumY, minimumZ),
                new WorldPreviewBlockCoordinate(maximumX, maximumY, maximumZ))
            : null;
        return new WorldPreviewStatistics(
            1,
            sections.Count,
            nonAirCount,
            blockEntities.Count,
            occupied);
    }
}
