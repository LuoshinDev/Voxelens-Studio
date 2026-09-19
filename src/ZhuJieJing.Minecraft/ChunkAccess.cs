namespace ZhuJieJing.Minecraft;

/// <summary>Optional bounds used to stream only the chunk columns currently needed by a caller.</summary>
public readonly record struct MinecraftChunkBounds(int MinimumX, int MinimumZ, int MaximumX, int MaximumZ)
{
    public bool Contains(MinecraftChunkAddress address) =>
        address.X >= MinimumX && address.X <= MaximumX && address.Z >= MinimumZ && address.Z <= MaximumZ;
}

/// <summary>Mount-time fingerprint used to detect a stale region index.</summary>
public sealed record MinecraftFileFingerprint(
    string RelativePath,
    long Length,
    DateTimeOffset LastWriteTimeUtc,
    string? ContentHash = null);

/// <summary>
/// Location of one chunk payload. Offset and allocation describe the region file only; the reader validates
/// the length and compression byte before returning any data.
/// </summary>
public sealed record MinecraftChunkIndexEntry(
    MinecraftChunkAddress Address,
    MinecraftRegionAddress Region,
    string RegionRelativePath,
    int LocalIndex,
    int SectorIndex,
    int AllocatedSectorCount,
    uint Timestamp,
    MinecraftChunkStorageKind StorageKind,
    string? ExternalPayloadRelativePath,
    MinecraftFileFingerprint SourceFingerprint);

/// <summary>Index over region headers. Enumerating it must not decompress or materialize chunk NBT.</summary>
public interface IMinecraftChunkIndex
{
    /// <summary>Total number of non-empty Chunk location entries captured at mount time.</summary>
    int TotalChunkCount { get; }

    /// <summary>Returns an O(1) count without enumerating the dimension on the caller thread.</summary>
    int GetChunkCount(MinecraftDimensionId dimension);

    /// <summary>Returns an O(1) count of non-empty Region addresses for one dimension.</summary>
    int GetRegionCount(MinecraftDimensionId dimension);

    ValueTask<MinecraftChunkIndexEntry?> FindAsync(
        MinecraftChunkAddress address,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<MinecraftChunkIndexEntry> EnumerateAsync(
        MinecraftDimensionId dimension,
        MinecraftChunkBounds? bounds = null,
        CancellationToken cancellationToken = default);

    /// <summary>Enumerates non-empty region headers without reading any chunk payload.</summary>
    IAsyncEnumerable<MinecraftRegionAddress> EnumerateRegionsAsync(
        MinecraftDimensionId dimension,
        CancellationToken cancellationToken = default);
}

/// <summary>Options for a bounded random chunk read.</summary>
public sealed record MinecraftChunkReadOptions(
    bool DecompressPayload = true,
    bool VerifySourceFingerprint = true,
    int MaximumDecompressedBytes = 16 * 1024 * 1024);

/// <summary>
/// Raw data returned by a random chunk read. NbtPayload contains either decompressed NBT or the exact stored
/// payload according to the supplied options; it is not a fabricated parsed chunk.
/// </summary>
public sealed record RawMinecraftChunk(
    MinecraftChunkIndexEntry IndexEntry,
    MinecraftChunkCompression Compression,
    bool IsDecompressed,
    ReadOnlyMemory<byte> NbtPayload,
    MinecraftOpaquePayload OriginalStoredPayload);

/// <summary>Performs validated random reads from mounted region and MCC files.</summary>
public interface IMinecraftChunkReader
{
    ValueTask<RawMinecraftChunk> ReadAsync(
        MinecraftChunkIndexEntry entry,
        MinecraftChunkReadOptions options,
        CancellationToken cancellationToken = default);
}
