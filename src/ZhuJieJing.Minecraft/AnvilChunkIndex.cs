using System.Buffers.Binary;

namespace ZhuJieJing.Minecraft;

/// <summary>Immutable index produced only from validated Anvil region headers.</summary>
public sealed class AnvilChunkIndex : IMinecraftChunkIndex
{
    private readonly IReadOnlyDictionary<MinecraftChunkAddress, MinecraftChunkIndexEntry> entries;
    private readonly IReadOnlyDictionary<MinecraftDimensionId, IReadOnlyList<MinecraftChunkIndexEntry>> entriesByDimension;
    private readonly IReadOnlyDictionary<MinecraftDimensionId, IReadOnlyList<MinecraftRegionAddress>> regionsByDimension;
    private readonly IReadOnlyDictionary<MinecraftRegionAddress, IReadOnlyList<MinecraftChunkIndexEntry>> entriesByRegion;

    internal AnvilChunkIndex(IEnumerable<MinecraftChunkIndexEntry> entries)
    {
        Dictionary<MinecraftChunkAddress, MinecraftChunkIndexEntry> byAddress = new();
        foreach (MinecraftChunkIndexEntry entry in entries)
        {
            if (!byAddress.TryAdd(entry.Address, entry))
                throw new InvalidDataException($"Duplicate chunk address was indexed: {entry.Address}");
        }

        this.entries = byAddress;
        entriesByRegion = byAddress.Values.GroupBy(static entry => entry.Region).ToDictionary(
            static group => group.Key,
            static group => (IReadOnlyList<MinecraftChunkIndexEntry>)group.OrderBy(static entry => entry.LocalIndex).ToArray());
        entriesByDimension = byAddress.Values
            .GroupBy(static entry => entry.Address.Dimension)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<MinecraftChunkIndexEntry>)group
                    .OrderBy(static entry => entry.Address.X)
                    .ThenBy(static entry => entry.Address.Z)
                    .ToArray());
        regionsByDimension = byAddress.Values
            .Select(static entry => entry.Region)
            .Distinct()
            .GroupBy(static region => region.Dimension)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<MinecraftRegionAddress>)group
                    .OrderBy(static region => region.X)
                    .ThenBy(static region => region.Z)
                    .ToArray());
    }

    public int TotalChunkCount => entries.Count;

    public int GetChunkCount(MinecraftDimensionId dimension) =>
        entriesByDimension.TryGetValue(dimension, out IReadOnlyList<MinecraftChunkIndexEntry>? dimensionEntries)
            ? dimensionEntries.Count
            : 0;

    public int GetRegionCount(MinecraftDimensionId dimension) =>
        regionsByDimension.TryGetValue(dimension, out IReadOnlyList<MinecraftRegionAddress>? regions)
            ? regions.Count
            : 0;

    internal bool TryGetAuthoritativeEntry(
        MinecraftChunkIndexEntry candidate,
        out MinecraftChunkIndexEntry authoritative)
    {
        if (entries.TryGetValue(candidate.Address, out MinecraftChunkIndexEntry? indexed) &&
            indexed.Region == candidate.Region &&
            StringComparer.OrdinalIgnoreCase.Equals(indexed.RegionRelativePath, candidate.RegionRelativePath) &&
            indexed.LocalIndex == candidate.LocalIndex &&
            indexed.SectorIndex == candidate.SectorIndex &&
            indexed.AllocatedSectorCount == candidate.AllocatedSectorCount &&
            indexed.SourceFingerprint == candidate.SourceFingerprint)
        {
            authoritative = indexed;
            return true;
        }

        authoritative = null!;
        return false;
    }

    public ValueTask<MinecraftChunkIndexEntry?> FindAsync(
        MinecraftChunkAddress address,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        entries.TryGetValue(address, out MinecraftChunkIndexEntry? entry);
        return ValueTask.FromResult(entry);
    }

    public async IAsyncEnumerable<MinecraftChunkIndexEntry> EnumerateAsync(
        MinecraftDimensionId dimension,
        MinecraftChunkBounds? bounds = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        if (!entriesByDimension.TryGetValue(dimension, out IReadOnlyList<MinecraftChunkIndexEntry>? dimensionEntries))
            yield break;

        if(bounds is { } selection)
        {
            int minRegionX = FloorDiv(selection.MinimumX, 32);
            int maxRegionX = FloorDiv(selection.MaximumX, 32);
            int minRegionZ = FloorDiv(selection.MinimumZ, 32);
            int maxRegionZ = FloorDiv(selection.MaximumZ, 32);
            long regionWidth = (long)maxRegionX - minRegionX + 1;
            long regionDepth = (long)maxRegionZ - minRegionZ + 1;
            // Small windows use address lookups; enormous sparse bounds enumerate only existing regions.
            IEnumerable<MinecraftRegionAddress> candidates = regionWidth > 0 && regionDepth > 0 &&
                regionWidth <= 4096 && regionDepth <= 4096 && regionWidth * regionDepth <= regionsByDimension[dimension].Count
                ? EnumerateRegionWindow(dimension, minRegionX, maxRegionX, minRegionZ, maxRegionZ)
                : regionsByDimension[dimension].Where(region => region.X >= minRegionX && region.X <= maxRegionX &&
                    region.Z >= minRegionZ && region.Z <= maxRegionZ);
            foreach(MinecraftRegionAddress region in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if(!entriesByRegion.TryGetValue(region, out IReadOnlyList<MinecraftChunkIndexEntry>? regionEntries)) continue;
                foreach(MinecraftChunkIndexEntry entry in regionEntries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if(selection.Contains(entry.Address)) yield return entry;
                }
            }
            yield break;
        }
        foreach (MinecraftChunkIndexEntry entry in dimensionEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bounds is null || bounds.Value.Contains(entry.Address))
                yield return entry;
        }
    }

    private static IEnumerable<MinecraftRegionAddress> EnumerateRegionWindow(MinecraftDimensionId dimension,
        int minX, int maxX, int minZ, int maxZ)
    {
        for(long x = minX; x <= maxX; x++)
        for(long z = minZ; z <= maxZ; z++)
            yield return new MinecraftRegionAddress(dimension, (int)x, (int)z);
    }

    private static int FloorDiv(int value, int divisor)
    {
        int quotient = value / divisor;
        return value % divisor < 0 ? quotient - 1 : quotient;
    }

    public async IAsyncEnumerable<MinecraftRegionAddress> EnumerateRegionsAsync(
        MinecraftDimensionId dimension,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        if (!regionsByDimension.TryGetValue(dimension, out IReadOnlyList<MinecraftRegionAddress>? regions))
            yield break;

        foreach (MinecraftRegionAddress region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return region;
        }
    }

    internal static async ValueTask<IReadOnlyList<MinecraftChunkIndexEntry>> ReadRegionHeaderAsync(
        string regionPath,
        MinecraftRegionAddress region,
        MinecraftFileFingerprint fingerprint,
        ICollection<MinecraftDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        const int sectorSize = 4096;
        const int headerSize = sectorSize * 2;

        if (fingerprint.Length < headerSize)
            throw new InvalidDataException($"Anvil region is shorter than its 8192-byte header: {fingerprint.RelativePath}");
        if (fingerprint.Length % sectorSize != 0)
            throw new InvalidDataException($"Anvil region length is not sector-aligned: {fingerprint.RelativePath}");

        byte[] header = GC.AllocateUninitializedArray<byte>(headerSize);
        await using FileStream stream = MinecraftSourceFiles.OpenRead(regionPath);
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);

        long fileSectorCount = fingerprint.Length / sectorSize;
        HashSet<int> occupied = new() { 0, 1 };
        List<MinecraftChunkIndexEntry> entries = new();

        for (int localIndex = 0; localIndex < 1024; localIndex++)
        {
            int locationOffset = localIndex * 4;
            int sectorIndex = header[locationOffset] << 16 |
                              header[locationOffset + 1] << 8 |
                              header[locationOffset + 2];
            int allocatedSectorCount = header[locationOffset + 3];

            if (sectorIndex == 0 && allocatedSectorCount == 0)
                continue;

            MinecraftChunkAddress address = CreateChunkAddress(region, localIndex, fingerprint.RelativePath);
            if (sectorIndex == 0 || allocatedSectorCount == 0)
                throw new InvalidDataException(
                    $"Anvil location entry {localIndex} has a partial zero allocation: {fingerprint.RelativePath}");
            if (sectorIndex < 2)
                throw new InvalidDataException(
                    $"Anvil location entry {localIndex} overlaps the header: {fingerprint.RelativePath}");

            int effectiveAllocatedSectorCount = allocatedSectorCount;
            long allocationEnd = (long)sectorIndex + allocatedSectorCount;
            if (allocationEnd > fileSectorCount)
            {
                int? recoveredSectorCount = await TryRecoverAllocationAtEndOfFileAsync(
                    stream,
                    sectorIndex,
                    allocatedSectorCount,
                    fileSectorCount,
                    cancellationToken).ConfigureAwait(false);
                if (recoveredSectorCount is null)
                    throw new InvalidDataException(
                        $"Anvil location entry {localIndex} extends beyond the region file: {fingerprint.RelativePath}");

                effectiveAllocatedSectorCount = recoveredSectorCount.Value;
                allocationEnd = (long)sectorIndex + effectiveAllocatedSectorCount;
                diagnostics.Add(new MinecraftDiagnostic(
                    "anvil.location.allocation_recovered",
                    MinecraftDiagnosticSeverity.Warning,
                    $"Region {fingerprint.RelativePath} 的位置条目 {localIndex} 声明占用 {allocatedSectorCount} 个扇区并越过文件末尾；" +
                    $"筑界镜根据完整区块记录安全恢复为 {effectiveAllocatedSectorCount} 个扇区，未修改原存档。",
                    address));
            }

            for (int sector = sectorIndex; sector < allocationEnd; sector++)
            {
                if (!occupied.Add(sector))
                    throw new InvalidDataException(
                        $"Anvil location entry {localIndex} overlaps another allocation at sector {sector}: {fingerprint.RelativePath}");
            }

            uint timestamp = BinaryPrimitives.ReadUInt32BigEndian(
                header.AsSpan(sectorSize + locationOffset, sizeof(uint)));
            entries.Add(new MinecraftChunkIndexEntry(
                address,
                region,
                fingerprint.RelativePath,
                localIndex,
                sectorIndex,
                effectiveAllocatedSectorCount,
                timestamp,
                MinecraftChunkStorageKind.UnknownUntilRead,
                null,
                fingerprint));
        }

        return entries;
    }

    private static MinecraftChunkAddress CreateChunkAddress(
        MinecraftRegionAddress region,
        int localIndex,
        string relativePath)
    {
        int localX = localIndex & 31;
        int localZ = localIndex >> 5;
        try
        {
            int globalX = checked(region.X * MinecraftRegionAddress.ChunksPerAxis + localX);
            int globalZ = checked(region.Z * MinecraftRegionAddress.ChunksPerAxis + localZ);
            return new MinecraftChunkAddress(region.Dimension, globalX, globalZ);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                $"Region coordinates overflow the global chunk range: {relativePath}",
                exception);
        }
    }

    private static async ValueTask<int?> TryRecoverAllocationAtEndOfFileAsync(
        FileStream stream,
        int sectorIndex,
        int declaredSectorCount,
        long fileSectorCount,
        CancellationToken cancellationToken)
    {
        const int sectorSize = 4096;
        long availableSectorCount = fileSectorCount - sectorIndex;
        if (availableSectorCount <= 0)
            return null;

        long recordOffset = checked((long)sectorIndex * sectorSize);
        if (stream.Length - recordOffset < 5)
            return null;

        byte[] prefix = new byte[5];
        stream.Position = recordOffset;
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);

        uint encodedLength = BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(0, sizeof(uint)));
        byte compressionByte = prefix[4];
        bool isExternal = (compressionByte & 0x80) != 0;
        byte compressionId = (byte)(compressionByte & 0x7f);
        if (compressionId is < 1 or > 4 ||
            (isExternal && encodedLength != 1) ||
            (!isExternal && encodedLength < 2))
        {
            return null;
        }

        long framedLength = (long)encodedLength + sizeof(uint);
        long requiredSectorCount = (framedLength + sectorSize - 1) / sectorSize;
        long effectiveSectorCount = Math.Min(declaredSectorCount, availableSectorCount);
        if (requiredSectorCount <= 0 || requiredSectorCount > effectiveSectorCount)
        {
            return null;
        }

        return checked((int)effectiveSectorCount);
    }
}
