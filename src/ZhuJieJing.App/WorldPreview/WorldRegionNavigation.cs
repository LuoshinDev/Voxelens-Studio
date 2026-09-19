using System.Globalization;
using System.Runtime.CompilerServices;
using ZhuJieJing.Core;
using ZhuJieJing.Minecraft;

namespace ZhuJieJing.App.WorldPreview;

public sealed record WorldDimensionRegionSummary(
    MinecraftDimensionId Dimension,
    string DisplayName,
    IReadOnlyList<string> RegionDirectories,
    int RegionCount)
{
    public string LocalizedDisplayName => WorldRegionNavigation.FormatDimensionName(Dimension);
}

public sealed record WorldRegionNavigationItem(
    MinecraftRegionAddress Address,
    string FileName,
    BlockPosition? Target,
    long? FileSizeBytes)
{
    public bool CanNavigate => Target is not null;

    public string FileSizeText => FileSizeBytes is long size
        ? WorldRegionNavigation.FormatFileSize(size)
        : UiText.Get("大小未知");
}

/// <summary>
/// Builds the lightweight dimension/Region navigation model from mount metadata and the header-only Chunk index.
/// It never enumerates Chunk payloads or reads NBT. Sparse Regions target an actually indexed Chunk instead of
/// an arbitrary geometric center that might contain no terrain.
/// </summary>
public static class WorldRegionNavigation
{
    public const int BlocksPerChunk = 16;
    public const int BlocksPerRegion = MinecraftRegionAddress.ChunksPerAxis * BlocksPerChunk;

    public static IReadOnlyList<WorldDimensionRegionSummary> DescribeDimensions(
        MinecraftWorldDescriptor descriptor,
        IMinecraftChunkIndex chunkIndex)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(chunkIndex);

        return descriptor.Dimensions
            .Select(dimension => new WorldDimensionRegionSummary(
                dimension.Id,
                FormatDimensionName(dimension.Id),
                dimension.RegionDirectories,
                chunkIndex.GetRegionCount(dimension.Id)))
            .ToArray();
    }

    public static async IAsyncEnumerable<WorldRegionNavigationItem> EnumerateRegionsAsync(
        IMinecraftChunkIndex chunkIndex,
        MinecraftDimensionId dimension,
        int preferredY,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunkIndex);

        var representativeEntries = new Dictionary<MinecraftRegionAddress, MinecraftChunkIndexEntry>();
        await foreach(MinecraftChunkIndexEntry entry in chunkIndex
                          .EnumerateAsync(dimension, cancellationToken: cancellationToken)
                          .WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            representativeEntries.TryAdd(entry.Region, entry);
        }

        await foreach(MinecraftRegionAddress region in chunkIndex
                          .EnumerateRegionsAsync(dimension, cancellationToken)
                          .WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            representativeEntries.TryGetValue(region, out MinecraftChunkIndexEntry? entry);
            BlockPosition? target = entry is not null && TryCreateChunkCenter(entry.Address, preferredY, out BlockPosition chunkCenter)
                ? chunkCenter
                : TryCreateRegionCenter(region, preferredY, out BlockPosition regionCenter)
                    ? regionCenter
                : null;
            long? fileSizeBytes = entry?.SourceFingerprint.Length;
            yield return new WorldRegionNavigationItem(region, FormatRegionFileName(region), target, fileSizeBytes);
        }
    }

    public static string FormatFileSize(long byteCount)
    {
        if(byteCount < 0) throw new ArgumentOutOfRangeException(nameof(byteCount));
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double displaySize = byteCount;
        int unitIndex = 0;
        while(displaySize >= 1024d && unitIndex < units.Length - 1)
        {
            displaySize /= 1024d;
            unitIndex++;
        }

        string number = unitIndex == 0
            ? byteCount.ToString(CultureInfo.InvariantCulture)
            : displaySize.ToString("0.#", CultureInfo.InvariantCulture);
        return $"{number} {units[unitIndex]}";
    }

    public static IReadOnlyList<WorldRegionNavigationItem> OrderByFileSizeDescending(
        IEnumerable<WorldRegionNavigationItem> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        return regions
            .OrderByDescending(region => region.FileSizeBytes.HasValue)
            .ThenByDescending(region => region.FileSizeBytes.GetValueOrDefault())
            .ThenBy(region => region.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(region => region.FileName, StringComparer.Ordinal)
            .ToArray();
    }

    public static bool TryCreateRegionCenter(
        MinecraftRegionAddress region,
        int preferredY,
        out BlockPosition target)
    {
        long centerX = (long)region.X * BlocksPerRegion + BlocksPerRegion / 2;
        long centerZ = (long)region.Z * BlocksPerRegion + BlocksPerRegion / 2;
        if(centerX < int.MinValue || centerX > int.MaxValue ||
           centerZ < int.MinValue || centerZ > int.MaxValue)
        {
            target = default;
            return false;
        }

        target = new BlockPosition((int)centerX, preferredY, (int)centerZ);
        return true;
    }

    public static bool TryCreateChunkCenter(
        MinecraftChunkAddress chunk,
        int preferredY,
        out BlockPosition target)
    {
        long centerX = (long)chunk.X * BlocksPerChunk + BlocksPerChunk / 2;
        long centerZ = (long)chunk.Z * BlocksPerChunk + BlocksPerChunk / 2;
        if(centerX < int.MinValue || centerX > int.MaxValue ||
           centerZ < int.MinValue || centerZ > int.MaxValue)
        {
            target = default;
            return false;
        }

        target = new BlockPosition((int)centerX, preferredY, (int)centerZ);
        return true;
    }

    public static string FormatRegionFileName(MinecraftRegionAddress region) => string.Create(
        CultureInfo.InvariantCulture,
        $"r.{region.X}.{region.Z}.mca");

    public static string FormatDimensionName(MinecraftDimensionId dimension)
    {
        if(dimension == MinecraftDimensionId.Overworld) return UiText.Get("主世界");
        if(dimension == MinecraftDimensionId.Nether) return UiText.Get("下界");
        if(dimension == MinecraftDimensionId.End) return UiText.Get("末地");
        return dimension.Value;
    }
}
