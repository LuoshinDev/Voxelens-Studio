using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.RegularExpressions;
using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

/// <summary>Summary of the non-destructive protection pass applied inside an export staging directory.</summary>
public sealed record MinecraftWorldExportProtectionResult(
    long LevelMetadataFilesProtected,
    long RegionFilesScanned,
    long RegionFilesRewritten,
    long ChunkPayloadsScanned,
    long ChunkPayloadsRewritten,
    long ScheduledTickContainersCleared);

/// <summary>
/// Applies building-showcase protection only to a completed export staging tree. Unknown NBT is copied
/// structurally and byte-for-byte; an unknown tag/compression or malformed record aborts publication instead
/// of emitting a partially protected world.
/// </summary>
internal static partial class MinecraftWorldExportProtection
{
    private const int SectorBytes = 4096;
    private const int RegionHeaderBytes = SectorBytes * 2;
    private const int MaximumStoredNbtBytes = 65 * 1024 * 1024;
    private const int MaximumDecompressedNbtBytes = 64 * 1024 * 1024;

    [GeneratedRegex(@"^r\.(-?\d+)\.(-?\d+)\.(?:mca|mcr)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RegionFileNameRegex();

    public static async Task<MinecraftWorldExportProtectionResult> ApplyAsync(
        string stagingWorldDirectory,
        CancellationToken cancellationToken = default)
    {
        if(string.IsNullOrWhiteSpace(stagingWorldDirectory))
            throw new ArgumentException("Staging world directory cannot be empty.", nameof(stagingWorldDirectory));

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingWorldDirectory));
        if(!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Export staging world does not exist: {root}");
        if(IsReparsePoint(root))
            throw new IOException("A reparse-point export staging root cannot be protected safely.");

        string levelDat = Path.Combine(root, "level.dat");
        if(!File.Exists(levelDat))
            throw new InvalidDataException("Export staging world does not contain level.dat.");

        long levelFiles = 0;
        await ProtectLevelMetadataAsync(levelDat, cancellationToken).ConfigureAwait(false);
        levelFiles++;
        string levelDatOld = Path.Combine(root, "level.dat_old");
        if(File.Exists(levelDatOld))
        {
            await ProtectLevelMetadataAsync(levelDatOld, cancellationToken).ConfigureAwait(false);
            levelFiles++;
        }

        long regionFilesScanned = 0;
        long regionFilesRewritten = 0;
        long chunksScanned = 0;
        long chunksRewritten = 0;
        long tickContainersCleared = 0;
        foreach(string regionPath in EnumerateChunkRegionFiles(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RegionProtectionResult result = await ProtectRegionAsync(regionPath, cancellationToken)
                .ConfigureAwait(false);
            regionFilesScanned++;
            if(result.WasRewritten) regionFilesRewritten++;
            chunksScanned = checked(chunksScanned + result.ChunksScanned);
            chunksRewritten = checked(chunksRewritten + result.ChunksRewritten);
            tickContainersCleared = checked(tickContainersCleared + result.TickContainersCleared);
        }

        return new MinecraftWorldExportProtectionResult(
            levelFiles,
            regionFilesScanned,
            regionFilesRewritten,
            chunksScanned,
            chunksRewritten,
            tickContainersCleared);
    }

    internal static void ApplyBaselineGameRules(IDictionary<string, string> gameRules)
    {
        ArgumentNullException.ThrowIfNull(gameRules);
        foreach((string name, string value) in MinecraftWorldExportProtectionGameRules.All)
            gameRules[name] = value;
    }

    internal static LegacyBlockEncoding MakeLegacyFluidStatic(LegacyBlockEncoding encoding) => encoding.NumericId switch
    {
        8 => new LegacyBlockEncoding(9, encoding.Metadata),
        10 => new LegacyBlockEncoding(11, encoding.Metadata),
        _ => encoding,
    };

    internal static async Task RelocateSpawnAsync(
        string stagingWorldDirectory,
        BlockPosition spawn,
        CancellationToken cancellationToken)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingWorldDirectory));
        string levelDat = Path.Combine(root, "level.dat");
        if(!File.Exists(levelDat))
            throw new InvalidDataException("裁剪暂存世界不包含 level.dat，无法迁移出生点。");

        await RelocateSpawnFileAsync(levelDat, spawn, cancellationToken).ConfigureAwait(false);
        string levelDatOld = Path.Combine(root, "level.dat_old");
        if(File.Exists(levelDatOld))
            await RelocateSpawnFileAsync(levelDatOld, spawn, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RelocateSpawnFileAsync(
        string path,
        BlockPosition spawn,
        CancellationToken cancellationToken)
    {
        byte[] stored = await ReadBoundedFileAsync(path, MaximumStoredNbtBytes, cancellationToken)
            .ConfigureAwait(false);
        bool gzip = stored.Length >= 2 && stored[0] == 0x1f && stored[1] == 0x8b;
        byte[] nbt = gzip
            ? await DecompressStreamAsync(stored, useGZip: true, cancellationToken).ConfigureAwait(false)
            : stored;
        NbtRewriteOutcome rewritten = NbtDocumentRewriter.RelocateSpawn(nbt, spawn);
        if(!rewritten.Changed) return;
        byte[] relocated = gzip ? CompressGZip(rewritten.Bytes.Span) : rewritten.Bytes.ToArray();
        await WriteReplacementFileAsync(path, relocated, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ProtectLevelMetadataAsync(string path, CancellationToken cancellationToken)
    {
        byte[] stored = await ReadBoundedFileAsync(path, MaximumStoredNbtBytes, cancellationToken)
            .ConfigureAwait(false);
        bool gzip = stored.Length >= 2 && stored[0] == 0x1f && stored[1] == 0x8b;
        byte[] nbt = gzip
            ? await DecompressStreamAsync(stored, useGZip: true, cancellationToken).ConfigureAwait(false)
            : stored;
        NbtRewriteOutcome rewritten = NbtDocumentRewriter.ProtectLevelMetadata(nbt);
        if(!rewritten.Changed)
            return;
        byte[] protectedStored = gzip ? CompressGZip(rewritten.Bytes.Span) : rewritten.Bytes.ToArray();
        await WriteReplacementFileAsync(path, protectedStored, cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<string> EnumerateChunkRegionFiles(string root)
    {
        Stack<string> directories = new();
        directories.Push(root);
        while(directories.Count > 0)
        {
            string current = directories.Pop();
            foreach(string directory in Directory.EnumerateDirectories(current))
            {
                if(IsReparsePoint(directory))
                    throw new IOException($"Export staging tree contains a reparse-point directory: {directory}");
                directories.Push(directory);
            }

            if(!Path.GetFileName(current).Equals("region", StringComparison.OrdinalIgnoreCase))
                continue;
            foreach(string file in Directory.EnumerateFiles(current))
            {
                if(RegionFileNameRegex().IsMatch(Path.GetFileName(file)))
                    yield return file;
            }
        }
    }

    private static async Task<RegionProtectionResult> ProtectRegionAsync(
        string regionPath,
        CancellationToken cancellationToken,
        Func<byte[], NbtRewriteOutcome>? transform = null,
        Func<byte[], CancellationToken, Task<NbtRewriteOutcome>>? writeTransform = null)
    {
        Match match = RegionFileNameRegex().Match(Path.GetFileName(regionPath));
        if(!match.Success ||
           !int.TryParse(match.Groups[1].Value, out int regionX) ||
           !int.TryParse(match.Groups[2].Value, out int regionZ))
        {
            throw new InvalidDataException($"Invalid Anvil region filename: {regionPath}");
        }

        byte[] header = GC.AllocateUninitializedArray<byte>(RegionHeaderBytes);
        List<RegionEntry> entries;
        HashSet<int> modified = [];
        long cleared = 0;
        await using(FileStream input = OpenRegionRead(regionPath))
        {
            if(input.Length < RegionHeaderBytes || input.Length % SectorBytes != 0)
                throw new InvalidDataException($"Anvil region is not sector-aligned: {regionPath}");
            await input.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            entries = ParseRegionEntries(header, input.Length, regionPath);

            foreach(RegionEntry entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StoredChunkPayload stored = await ReadStoredChunkAsync(
                    input,
                    regionPath,
                    regionX,
                    regionZ,
                    entry,
                    cancellationToken).ConfigureAwait(false);
                byte[] nbt = await DecompressChunkAsync(stored.Bytes, stored.Compression, cancellationToken)
                    .ConfigureAwait(false);
                NbtRewriteOutcome rewrite = transform is null ? NbtDocumentRewriter.ClearScheduledTicks(nbt) : transform(nbt);
                cleared = checked(cleared + rewrite.ChangedContainerCount);
                if(!rewrite.Changed)
                    continue;

                modified.Add(entry.LocalIndex);
            }
        }

        if(modified.Count == 0)
            return new RegionProtectionResult(entries.Count, 0, cleared, WasRewritten: false);

        await RewriteRegionAsync(
            regionPath,
            regionX,
            regionZ,
            header,
            entries,
            modified,
            cancellationToken, transform, writeTransform).ConfigureAwait(false);
        return new RegionProtectionResult(entries.Count, modified.Count, cleared, WasRewritten: true);
    }

    private static List<RegionEntry> ParseRegionEntries(byte[] header, long fileLength, string regionPath)
    {
        long sectorCount = fileLength / SectorBytes;
        HashSet<long> occupied = [0, 1];
        List<RegionEntry> entries = new();
        for(int localIndex = 0; localIndex < 1024; localIndex++)
        {
            int offset = localIndex * 4;
            int sectorIndex = header[offset] << 16 | header[offset + 1] << 8 | header[offset + 2];
            int allocatedSectors = header[offset + 3];
            if(sectorIndex == 0 && allocatedSectors == 0)
                continue;
            if(sectorIndex < 2 || allocatedSectors == 0 || (long)sectorIndex + allocatedSectors > sectorCount)
                throw new InvalidDataException($"Invalid Anvil location entry {localIndex}: {regionPath}");
            for(int sector = sectorIndex; sector < sectorIndex + allocatedSectors; sector++)
            {
                if(!occupied.Add(sector))
                    throw new InvalidDataException($"Overlapping Anvil allocation at sector {sector}: {regionPath}");
            }
            uint timestamp = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(SectorBytes + offset, 4));
            entries.Add(new RegionEntry(localIndex, sectorIndex, allocatedSectors, timestamp));
        }
        return entries;
    }

    private static async Task<StoredChunkPayload> ReadStoredChunkAsync(
        FileStream region,
        string regionPath,
        int regionX,
        int regionZ,
        RegionEntry entry,
        CancellationToken cancellationToken)
    {
        long recordOffset = checked((long)entry.SectorIndex * SectorBytes);
        int allocationBytes = checked(entry.AllocatedSectors * SectorBytes);
        byte[] prefix = new byte[5];
        region.Position = recordOffset;
        await region.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        uint encodedLength = BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(0, 4));
        byte compressionByte = prefix[4];
        bool external = (compressionByte & 0x80) != 0;
        MinecraftChunkCompression compression = DecodeCompression((byte)(compressionByte & 0x7f), regionPath, entry.LocalIndex);

        if(external)
        {
            if(encodedLength != 1)
                throw new InvalidDataException($"External Anvil record has invalid length at {regionPath} #{entry.LocalIndex}.");
            (int chunkX, int chunkZ) = GetChunkCoordinates(regionX, regionZ, entry.LocalIndex);
            string externalPath = Path.Combine(Path.GetDirectoryName(regionPath)!, $"c.{chunkX}.{chunkZ}.mcc");
            if(!File.Exists(externalPath))
                throw new InvalidDataException($"External Anvil payload is missing: {externalPath}");
            byte[] externalBytes = await ReadBoundedFileAsync(externalPath, MaximumStoredNbtBytes, cancellationToken)
                .ConfigureAwait(false);
            return new StoredChunkPayload(externalBytes, compression, IsExternal: true, externalPath);
        }

        if(encodedLength < 2 || encodedLength > (uint)(allocationBytes - sizeof(uint)) || encodedLength > int.MaxValue)
            throw new InvalidDataException($"Inline Anvil record has invalid length at {regionPath} #{entry.LocalIndex}.");
        int payloadLength = checked((int)encodedLength - 1);
        if(payloadLength > MaximumStoredNbtBytes)
            throw new InvalidDataException($"Stored Anvil payload exceeds the safe limit at {regionPath} #{entry.LocalIndex}.");
        byte[] payload = GC.AllocateUninitializedArray<byte>(payloadLength);
        await region.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return new StoredChunkPayload(payload, compression, IsExternal: false, ExternalPath: null);
    }

    private static async Task RewriteRegionAsync(
        string regionPath,
        int regionX,
        int regionZ,
        byte[] sourceHeader,
        IReadOnlyList<RegionEntry> entries,
        IReadOnlySet<int> modified,
        CancellationToken cancellationToken,
        Func<byte[], NbtRewriteOutcome>? transform = null,
        Func<byte[], CancellationToken, Task<NbtRewriteOutcome>>? writeTransform = null)
    {
        string temporaryPath = $"{regionPath}.zjj-protect-{Guid.NewGuid():N}.tmp";
        byte[] outputHeader = new byte[RegionHeaderBytes];
        sourceHeader.AsSpan(SectorBytes, SectorBytes).CopyTo(outputHeader.AsSpan(SectorBytes));
        try
        {
            await using(FileStream input = OpenRegionRead(regionPath))
            await using(FileStream output = new(
                            temporaryPath,
                            FileMode.CreateNew,
                            FileAccess.ReadWrite,
                            FileShare.None,
                            128 * 1024,
                            FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await output.WriteAsync(outputHeader, cancellationToken).ConfigureAwait(false);
                int nextSector = 2;
                foreach(RegionEntry entry in entries.OrderBy(static value => value.LocalIndex))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    byte[] record;
                    int recordSectors;
                    if(modified.Contains(entry.LocalIndex))
                    {
                        StoredChunkPayload stored = await ReadStoredChunkAsync(
                            input,
                            regionPath,
                            regionX,
                            regionZ,
                            entry,
                            cancellationToken).ConfigureAwait(false);
                        byte[] nbt = await DecompressChunkAsync(stored.Bytes, stored.Compression, cancellationToken)
                            .ConfigureAwait(false);
                        NbtRewriteOutcome rewrite = writeTransform is not null ? await writeTransform(nbt, cancellationToken).ConfigureAwait(false)
                            : transform is null ? NbtDocumentRewriter.ClearScheduledTicks(nbt) : transform(nbt);
                        if(!rewrite.Changed)
                            throw new InvalidDataException($"Scheduled tick data changed during export protection: {regionPath} #{entry.LocalIndex}.");
                        byte[] compressedNbt = CompressZLib(rewrite.Bytes.Span);
                        if(stored.IsExternal)
                        {
                            string externalPath = stored.ExternalPath ??
                                throw new InvalidDataException("Modified external chunk has no MCC path.");
                            await WriteReplacementFileAsync(externalPath, compressedNbt, cancellationToken)
                                .ConfigureAwait(false);
                            recordSectors = 1;
                            record = new byte[SectorBytes];
                            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(0, 4), 1);
                            record[4] = (byte)((byte)MinecraftChunkCompression.ZLib | 0x80);
                        }
                        else
                        {
                            int encodedLength = checked(compressedNbt.Length + 1);
                            int framedLength = checked(encodedLength + sizeof(int));
                            recordSectors = checked((framedLength + SectorBytes - 1) / SectorBytes);
                            if(recordSectors is <= 0 or > byte.MaxValue)
                                throw new InvalidDataException($"Protected chunk no longer fits an inline Anvil record: {regionPath} #{entry.LocalIndex}.");
                            record = new byte[checked(recordSectors * SectorBytes)];
                            BinaryPrimitives.WriteInt32BigEndian(record.AsSpan(0, 4), encodedLength);
                            record[4] = (byte)MinecraftChunkCompression.ZLib;
                            compressedNbt.CopyTo(record, 5);
                        }
                    }
                    else
                    {
                        recordSectors = entry.AllocatedSectors;
                        record = GC.AllocateUninitializedArray<byte>(checked(recordSectors * SectorBytes));
                        input.Position = checked((long)entry.SectorIndex * SectorBytes);
                        await input.ReadExactlyAsync(record, cancellationToken).ConfigureAwait(false);
                    }

                    if(nextSector > 0x00ff_ffff || (long)nextSector + recordSectors > 0x0100_0000)
                        throw new InvalidDataException($"Protected region exceeds the Anvil 24-bit sector limit: {regionPath}");
                    await output.WriteAsync(record, cancellationToken).ConfigureAwait(false);
                    int headerOffset = entry.LocalIndex * 4;
                    outputHeader[headerOffset] = (byte)(nextSector >> 16);
                    outputHeader[headerOffset + 1] = (byte)(nextSector >> 8);
                    outputHeader[headerOffset + 2] = (byte)nextSector;
                    outputHeader[headerOffset + 3] = checked((byte)recordSectors);
                    BinaryPrimitives.WriteUInt32BigEndian(
                        outputHeader.AsSpan(SectorBytes + headerOffset, 4),
                        entry.Timestamp);
                    nextSector = checked(nextSector + recordSectors);
                }

                output.Position = 0;
                await output.WriteAsync(outputHeader, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, regionPath, overwrite: true);
        }
        finally
        {
            if(File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static async Task<byte[]> DecompressChunkAsync(
        byte[] stored,
        MinecraftChunkCompression compression,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(compression == MinecraftChunkCompression.None)
        {
            if(stored.Length > MaximumDecompressedNbtBytes)
                throw new InvalidDataException("Uncompressed chunk NBT exceeds the safe limit.");
            return stored;
        }
        if(compression == MinecraftChunkCompression.Lz4)
            return MinecraftLz4BlockDecoder.Decode(stored, MaximumDecompressedNbtBytes, cancellationToken).ToArray();
        return await DecompressStreamAsync(
            stored,
            useGZip: compression == MinecraftChunkCompression.GZip,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> DecompressStreamAsync(
        byte[] stored,
        bool useGZip,
        CancellationToken cancellationToken)
    {
        using MemoryStream source = new(stored, writable: false);
        using Stream decoder = useGZip
            ? new GZipStream(source, CompressionMode.Decompress)
            : new ZLibStream(source, CompressionMode.Decompress);
        using MemoryStream destination = new(Math.Min(stored.Length * 2, 256 * 1024));
        byte[] buffer = new byte[64 * 1024];
        while(true)
        {
            int read = await decoder.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if(read == 0) break;
            if(destination.Length + read > MaximumDecompressedNbtBytes)
                throw new InvalidDataException("Decompressed NBT exceeds the safe limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return destination.ToArray();
    }

    private static byte[] CompressGZip(ReadOnlySpan<byte> nbt)
    {
        using MemoryStream destination = new();
        using(GZipStream gzip = new(destination, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(nbt);
        return destination.ToArray();
    }

    private static byte[] CompressZLib(ReadOnlySpan<byte> nbt)
    {
        using MemoryStream destination = new();
        using(ZLibStream zlib = new(destination, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(nbt);
        return destination.ToArray();
    }

    private static async Task<byte[]> ReadBoundedFileAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        long length = new FileInfo(path).Length;
        if(length is <= 0 || length > maximumBytes)
            throw new InvalidDataException($"NBT file length is outside the safe 1..{maximumBytes:N0} range: {path}");
        byte[] bytes = GC.AllocateUninitializedArray<byte>(checked((int)length));
        await using FileStream input = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if(await input.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
            throw new IOException($"NBT file grew while it was being protected: {path}");
        return bytes;
    }

    private static async Task WriteReplacementFileAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        string temporaryPath = $"{path}.zjj-protect-{Guid.NewGuid():N}.tmp";
        try
        {
            await using(FileStream output = new(
                            temporaryPath,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            64 * 1024,
                            FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if(File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static FileStream OpenRegionRead(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        128 * 1024,
        FileOptions.Asynchronous | FileOptions.RandomAccess);

    private static MinecraftChunkCompression DecodeCompression(byte id, string regionPath, int localIndex) => id switch
    {
        1 => MinecraftChunkCompression.GZip,
        2 => MinecraftChunkCompression.ZLib,
        3 => MinecraftChunkCompression.None,
        4 => MinecraftChunkCompression.Lz4,
        _ => throw new NotSupportedException(
            $"Unsupported Anvil compression {id} at {regionPath} #{localIndex}; protected export was aborted."),
    };

    private static (int X, int Z) GetChunkCoordinates(int regionX, int regionZ, int localIndex) =>
        (checked(regionX * 32 + (localIndex & 31)), checked(regionZ * 32 + (localIndex >> 5)));

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private sealed record StoredChunkPayload(
        byte[] Bytes,
        MinecraftChunkCompression Compression,
        bool IsExternal,
        string? ExternalPath);

    private readonly record struct RegionEntry(int LocalIndex, int SectorIndex, int AllocatedSectors, uint Timestamp);

    private readonly record struct RegionProtectionResult(
        long ChunksScanned,
        long ChunksRewritten,
        long TickContainersCleared,
        bool WasRewritten);
}

internal readonly record struct NbtRewriteOutcome(
    ReadOnlyMemory<byte> Bytes,
    bool Changed,
    int ChangedContainerCount);

internal ref struct NbtDocumentRewriter
{
    private const int MaximumDepth = 64;
    private const int MaximumCollectionElements = 1_048_576;
    private const int MaximumVisitedTags = 1_048_576;
    private const int MaximumOutputBytes = 65 * 1024 * 1024;

    private static readonly HashSet<string> ScheduledTickListNames = new(StringComparer.Ordinal)
    {
        "TileTicks",
        "LiquidTicks",
        "block_ticks",
        "fluid_ticks",
        "ToBeTicked",
        "LiquidsToBeTicked",
    };

    private readonly ReadOnlySpan<byte> source;
    private readonly MemoryStream output;
    private readonly BlockPosition? replacementSpawn;
    private int position;
    private int visitedTags;
    private int changedContainerCount;
    private bool changed;

    private NbtDocumentRewriter(ReadOnlySpan<byte> source, BlockPosition? replacementSpawn = null)
    {
        if(source.IsEmpty || source.Length > MaximumOutputBytes)
            throw new InvalidDataException("NBT document length is outside the protected rewrite limit.");
        this.source = source;
        this.replacementSpawn = replacementSpawn;
        output = new MemoryStream(Math.Min(source.Length + 512, MaximumOutputBytes));
        position = 0;
        visitedTags = 0;
        changedContainerCount = 0;
        changed = false;
    }

    public static NbtRewriteOutcome ProtectLevelMetadata(ReadOnlySpan<byte> source)
    {
        NbtDocumentRewriter rewriter = new(source);
        rewriter.RewriteRoot(NbtRewriteMode.LevelMetadata);
        if(!rewriter.changed)
            return new NbtRewriteOutcome(source.ToArray(), Changed: false, ChangedContainerCount: 0);
        return new NbtRewriteOutcome(rewriter.output.ToArray(), Changed: true, rewriter.changedContainerCount);
    }

    public static NbtRewriteOutcome ClearScheduledTicks(ReadOnlySpan<byte> source)
    {
        NbtDocumentRewriter rewriter = new(source);
        rewriter.RewriteRoot(NbtRewriteMode.ScheduledTicks);
        if(!rewriter.changed)
            return new NbtRewriteOutcome(source.ToArray(), Changed: false, ChangedContainerCount: 0);
        return new NbtRewriteOutcome(rewriter.output.ToArray(), Changed: true, rewriter.changedContainerCount);
    }

    public static NbtRewriteOutcome RelocateSpawn(ReadOnlySpan<byte> source, BlockPosition spawn)
    {
        NbtDocumentRewriter rewriter = new(source, spawn);
        rewriter.RewriteRoot(NbtRewriteMode.SpawnLocation);
        if(!rewriter.changed)
            return new NbtRewriteOutcome(source.ToArray(), Changed: false, ChangedContainerCount: 0);
        return new NbtRewriteOutcome(rewriter.output.ToArray(), Changed: true, ChangedContainerCount: 0);
    }

    private void RewriteRoot(NbtRewriteMode mode)
    {
        NbtTagType rootType = ReadTagType();
        if(rootType != NbtTagType.Compound)
            throw new InvalidDataException($"NBT root must be TAG_Compound, found {rootType}.");
        WriteByte((byte)rootType);
        CopyRawString();
        if(mode == NbtRewriteMode.ScheduledTicks)
            RewriteChunkCompound(0);
        else
            RewriteLevelRootCompound(0, mode);
        if(position != source.Length)
            throw new InvalidDataException("Trailing bytes follow the NBT root compound.");
    }

    private void RewriteLevelRootCompound(int depth, NbtRewriteMode mode)
    {
        EnsureDepth(depth);
        bool foundData = false;
        while(true)
        {
            NbtTagType type = ReadTagType();
            if(type == NbtTagType.End)
            {
                if(!foundData)
                    throw new InvalidDataException("level.dat does not contain the required Data compound.");
                WriteByte(0);
                return;
            }
            CountTag();
            int nameStart = position;
            string name = ReadModifiedUtf8();
            int nameLength = position - nameStart;
            if(name == "Data")
            {
                if(foundData)
                    throw new InvalidDataException("level.dat contains duplicate Data tags.");
                if(type != NbtTagType.Compound)
                    throw new InvalidDataException("level.dat Data tag is not a compound.");
                foundData = true;
                WriteTagHeaderFromSource(type, nameStart, nameLength);
                if(mode == NbtRewriteMode.LevelMetadata)
                    RewriteDataCompound(depth + 1);
                else
                    RewriteSpawnDataCompound(depth + 1);
                continue;
            }
            WriteTagHeaderFromSource(type, nameStart, nameLength);
            CopyPayload(type, depth + 1, inspectScheduledTicks: false);
        }
    }

    private void RewriteDataCompound(int depth)
    {
        EnsureDepth(depth);
        bool foundGameRules = false;
        while(true)
        {
            NbtTagType type = ReadTagType();
            if(type == NbtTagType.End)
            {
                if(!foundGameRules)
                {
                    WriteNamedCompoundStart("GameRules");
                    WriteMissingGameRules(new HashSet<string>(StringComparer.Ordinal));
                    WriteByte(0);
                    changed = true;
                }
                WriteByte(0);
                return;
            }
            CountTag();
            int nameStart = position;
            string name = ReadModifiedUtf8();
            int nameLength = position - nameStart;
            if(name == "GameRules")
            {
                if(foundGameRules)
                    throw new InvalidDataException("level.dat contains duplicate Data.GameRules tags.");
                foundGameRules = true;
                if(type != NbtTagType.Compound)
                {
                    SkipPayload(type, depth + 1);
                    WriteNamedCompoundStart("GameRules");
                    WriteMissingGameRules(new HashSet<string>(StringComparer.Ordinal));
                    WriteByte(0);
                    changed = true;
                    continue;
                }
                WriteTagHeaderFromSource(type, nameStart, nameLength);
                RewriteGameRulesCompound(depth + 1);
                continue;
            }
            WriteTagHeaderFromSource(type, nameStart, nameLength);
            CopyPayload(type, depth + 1, inspectScheduledTicks: false);
        }
    }

    private void RewriteSpawnDataCompound(int depth)
    {
        EnsureDepth(depth);
        BlockPosition spawn = replacementSpawn ??
                              throw new InvalidOperationException("未提供要写入 level.dat 的出生点。");
        HashSet<string> found = new(StringComparer.Ordinal);
        while(true)
        {
            NbtTagType type = ReadTagType();
            if(type == NbtTagType.End)
            {
                WriteMissingSpawnCoordinates(found, spawn);
                if(!found.Contains("GameRules"))
                {
                    WriteNamedCompoundStart("GameRules");
                    WriteMissingGameRules(new HashSet<string>(StringComparer.Ordinal), spawnOnly: true);
                    WriteByte(0);
                }
                WriteByte(0);
                return;
            }
            CountTag();
            int nameStart = position;
            string name = ReadModifiedUtf8();
            int nameLength = position - nameStart;
            if(name == "GameRules")
            {
                if(!found.Add(name)) throw new InvalidDataException("level.dat contains duplicate Data.GameRules tags.");
                WriteNamedCompoundStart("GameRules");
                if(type == NbtTagType.Compound) RewriteGameRulesCompound(depth + 1, spawnOnly: true);
                else
                {
                    SkipPayload(type, depth + 1);
                    WriteMissingGameRules(new HashSet<string>(StringComparer.Ordinal), spawnOnly: true);
                    WriteByte(0);
                }
                continue;
            }
            if(!TryGetSpawnCoordinate(name, spawn, out int replacement))
            {
                WriteTagHeaderFromSource(type, nameStart, nameLength);
                CopyPayload(type, depth + 1, inspectScheduledTicks: false);
                continue;
            }
            if(!found.Add(name))
                throw new InvalidDataException($"level.dat contains duplicate Data.{name} tags.");

            if(type == NbtTagType.Int)
            {
                int current = ReadInt32();
                if(current == replacement)
                {
                    WriteTagHeaderFromSource(type, nameStart, nameLength);
                    WriteInt32(current);
                    continue;
                }
            }
            else
            {
                SkipPayload(type, depth + 1);
            }

            WriteNamedInt(name, replacement);
            changed = true;
        }
    }

    private void WriteMissingSpawnCoordinates(ISet<string> found, BlockPosition spawn)
    {
        foreach((string name, int value) in new[]
                {
                    ("SpawnX", spawn.X),
                    ("SpawnY", spawn.Y),
                    ("SpawnZ", spawn.Z),
                })
        {
            if(found.Contains(name)) continue;
            WriteNamedInt(name, value);
            changed = true;
        }
    }

    private static bool TryGetSpawnCoordinate(string name, BlockPosition spawn, out int value)
    {
        switch(name)
        {
            case "SpawnX":
                value = spawn.X;
                return true;
            case "SpawnY":
                value = spawn.Y;
                return true;
            case "SpawnZ":
                value = spawn.Z;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    private void RewriteGameRulesCompound(int depth, bool spawnOnly = false)
    {
        EnsureDepth(depth);
        HashSet<string> found = new(StringComparer.Ordinal);
        while(true)
        {
            NbtTagType type = ReadTagType();
            if(type == NbtTagType.End)
            {
                WriteMissingGameRules(found, spawnOnly);
                WriteByte(0);
                return;
            }
            CountTag();
            int nameStart = position;
            string name = ReadModifiedUtf8();
            int nameLength = position - nameStart;
            string protectedValue = "0";
            bool replace = spawnOnly ? name == "spawnRadius" : MinecraftWorldExportProtectionGameRules.TryGet(name, out protectedValue);
            if(!replace)
            {
                WriteTagHeaderFromSource(type, nameStart, nameLength);
                CopyPayload(type, depth + 1, inspectScheduledTicks: false);
                continue;
            }
            if(!found.Add(name))
                throw new InvalidDataException($"level.dat contains duplicate Data.GameRules.{name} tags.");

            if(type == NbtTagType.String)
            {
                int valueStart = position;
                string currentValue = ReadModifiedUtf8();
                if(currentValue == protectedValue)
                {
                    WriteTagHeaderFromSource(type, nameStart, nameLength);
                    WriteSourceSlice(valueStart, position - valueStart);
                    continue;
                }
            }
            else
            {
                SkipPayload(type, depth + 1);
            }

            WriteNamedString(name, protectedValue);
            changed = true;
        }
    }

    private void WriteMissingGameRules(ISet<string> found, bool spawnOnly = false)
    {
        if(spawnOnly)
        {
            if(!found.Contains("spawnRadius"))
            {
                WriteNamedString("spawnRadius", "0");
                changed = true;
            }
            return;
        }
        foreach((string name, string value) in MinecraftWorldExportProtectionGameRules.All)
        {
            if(found.Contains(name)) continue;
            WriteNamedString(name, value);
            changed = true;
        }
    }

    private void RewriteChunkCompound(int depth)
    {
        EnsureDepth(depth);
        while(true)
        {
            NbtTagType type = ReadTagType();
            if(type == NbtTagType.End)
            {
                WriteByte(0);
                return;
            }
            CountTag();
            int nameStart = position;
            string name = ReadModifiedUtf8();
            int nameLength = position - nameStart;
            if(ScheduledTickListNames.Contains(name))
            {
                if(type != NbtTagType.List)
                    throw new InvalidDataException($"Scheduled tick container {name} is not TAG_List.");
                NbtTagType elementType = ReadTagType();
                int count = ReadCollectionLength();
                if(count > 0 && elementType == NbtTagType.End)
                    throw new InvalidDataException($"Non-empty scheduled tick list {name} uses TAG_End elements.");
                for(int index = 0; index < count; index++)
                {
                    CountTag();
                    SkipPayload(elementType, depth + 2);
                }
                WriteTagHeaderFromSource(type, nameStart, nameLength);
                WriteByte((byte)elementType);
                WriteInt32(0);
                if(count > 0)
                {
                    changed = true;
                    changedContainerCount++;
                }
                continue;
            }

            WriteTagHeaderFromSource(type, nameStart, nameLength);
            CopyPayload(type, depth + 1, inspectScheduledTicks: true);
        }
    }

    private void CopyPayload(NbtTagType type, int depth, bool inspectScheduledTicks)
    {
        EnsureDepth(depth);
        switch(type)
        {
            case NbtTagType.Byte:
                CopyBytes(1);
                break;
            case NbtTagType.Short:
                CopyBytes(sizeof(short));
                break;
            case NbtTagType.Int:
            case NbtTagType.Float:
                CopyBytes(sizeof(int));
                break;
            case NbtTagType.Long:
            case NbtTagType.Double:
                CopyBytes(sizeof(long));
                break;
            case NbtTagType.ByteArray:
                CopyArray(sizeof(byte));
                break;
            case NbtTagType.String:
                CopyRawString();
                break;
            case NbtTagType.List:
                CopyList(depth, inspectScheduledTicks);
                break;
            case NbtTagType.Compound:
                if(inspectScheduledTicks) RewriteChunkCompound(depth);
                else CopyCompound(depth);
                break;
            case NbtTagType.IntArray:
                CopyArray(sizeof(int));
                break;
            case NbtTagType.LongArray:
                CopyArray(sizeof(long));
                break;
            default:
                throw new InvalidDataException($"Unknown NBT tag type {(byte)type}.");
        }
    }

    private void CopyCompound(int depth)
    {
        EnsureDepth(depth);
        while(true)
        {
            NbtTagType type = ReadTagType();
            WriteByte((byte)type);
            if(type == NbtTagType.End) return;
            CountTag();
            CopyRawString();
            CopyPayload(type, depth + 1, inspectScheduledTicks: false);
        }
    }

    private void CopyList(int depth, bool inspectScheduledTicks)
    {
        NbtTagType elementType = ReadTagType();
        int count = ReadCollectionLength();
        if(count > 0 && elementType == NbtTagType.End)
            throw new InvalidDataException("Non-empty NBT list uses TAG_End elements.");
        WriteByte((byte)elementType);
        WriteInt32(count);
        for(int index = 0; index < count; index++)
        {
            CountTag();
            CopyPayload(elementType, depth + 1, inspectScheduledTicks);
        }
    }

    private void SkipPayload(NbtTagType type, int depth)
    {
        EnsureDepth(depth);
        switch(type)
        {
            case NbtTagType.Byte:
                Skip(1);
                break;
            case NbtTagType.Short:
                Skip(sizeof(short));
                break;
            case NbtTagType.Int:
            case NbtTagType.Float:
                Skip(sizeof(int));
                break;
            case NbtTagType.Long:
            case NbtTagType.Double:
                Skip(sizeof(long));
                break;
            case NbtTagType.ByteArray:
                SkipArray(sizeof(byte));
                break;
            case NbtTagType.String:
                SkipRawString();
                break;
            case NbtTagType.List:
                NbtTagType elementType = ReadTagType();
                int count = ReadCollectionLength();
                if(count > 0 && elementType == NbtTagType.End)
                    throw new InvalidDataException("Non-empty NBT list uses TAG_End elements.");
                for(int index = 0; index < count; index++)
                {
                    CountTag();
                    SkipPayload(elementType, depth + 1);
                }
                break;
            case NbtTagType.Compound:
                while(true)
                {
                    NbtTagType childType = ReadTagType();
                    if(childType == NbtTagType.End) break;
                    CountTag();
                    SkipRawString();
                    SkipPayload(childType, depth + 1);
                }
                break;
            case NbtTagType.IntArray:
                SkipArray(sizeof(int));
                break;
            case NbtTagType.LongArray:
                SkipArray(sizeof(long));
                break;
            default:
                throw new InvalidDataException($"Unknown NBT tag type {(byte)type}.");
        }
    }

    private void CopyArray(int elementSize)
    {
        int count = ReadCollectionLength();
        WriteInt32(count);
        int byteCount = checked(count * elementSize);
        CopyBytes(byteCount);
    }

    private void SkipArray(int elementSize)
    {
        int count = ReadCollectionLength();
        Skip(checked(count * elementSize));
    }

    private void CopyRawString()
    {
        int start = position;
        SkipRawString();
        WriteSourceSlice(start, position - start);
    }

    private void SkipRawString()
    {
        EnsureAvailable(sizeof(ushort));
        ushort length = BinaryPrimitives.ReadUInt16BigEndian(source.Slice(position, sizeof(ushort)));
        position += sizeof(ushort);
        Skip(length);
    }

    private string ReadModifiedUtf8()
    {
        EnsureAvailable(sizeof(ushort));
        ushort byteLength = BinaryPrimitives.ReadUInt16BigEndian(source.Slice(position, sizeof(ushort)));
        position += sizeof(ushort);
        EnsureAvailable(byteLength);
        ReadOnlySpan<byte> bytes = source.Slice(position, byteLength);
        position += byteLength;
        char[] characters = GC.AllocateUninitializedArray<char>(byteLength);
        int characterCount = 0;
        int index = 0;
        while(index < bytes.Length)
        {
            byte first = bytes[index++];
            if((first & 0x80) == 0)
            {
                if(first == 0) throw new InvalidDataException("Modified UTF-8 contains a raw NUL byte.");
                characters[characterCount++] = (char)first;
                continue;
            }
            if((first & 0xe0) == 0xc0)
            {
                if(index >= bytes.Length) throw new InvalidDataException("Truncated Modified UTF-8 sequence.");
                byte second = bytes[index++];
                if((second & 0xc0) != 0x80) throw new InvalidDataException("Invalid Modified UTF-8 continuation byte.");
                int value = (first & 0x1f) << 6 | second & 0x3f;
                if(value != 0 && value < 0x80) throw new InvalidDataException("Overlong Modified UTF-8 sequence.");
                characters[characterCount++] = (char)value;
                continue;
            }
            if((first & 0xf0) == 0xe0)
            {
                if(index + 1 >= bytes.Length) throw new InvalidDataException("Truncated Modified UTF-8 sequence.");
                byte second = bytes[index++];
                byte third = bytes[index++];
                if((second & 0xc0) != 0x80 || (third & 0xc0) != 0x80)
                    throw new InvalidDataException("Invalid Modified UTF-8 continuation byte.");
                int value = (first & 0x0f) << 12 | (second & 0x3f) << 6 | third & 0x3f;
                if(value < 0x800) throw new InvalidDataException("Overlong Modified UTF-8 sequence.");
                characters[characterCount++] = (char)value;
                continue;
            }
            throw new InvalidDataException("Unsupported four-byte Modified UTF-8 sequence.");
        }
        return new string(characters, 0, characterCount);
    }

    private NbtTagType ReadTagType()
    {
        byte raw = ReadByte();
        if(raw > (byte)NbtTagType.LongArray)
            throw new InvalidDataException($"Unknown NBT tag type {raw}.");
        return (NbtTagType)raw;
    }

    private int ReadCollectionLength()
    {
        int count = ReadInt32();
        if(count < 0 || count > MaximumCollectionElements)
            throw new InvalidDataException($"NBT collection length {count:N0} exceeds its safe limit.");
        return count;
    }

    private int ReadInt32()
    {
        EnsureAvailable(sizeof(int));
        int value = BinaryPrimitives.ReadInt32BigEndian(source.Slice(position, sizeof(int)));
        position += sizeof(int);
        return value;
    }

    private byte ReadByte()
    {
        EnsureAvailable(1);
        return source[position++];
    }

    private void CopyBytes(int count)
    {
        EnsureAvailable(count);
        WriteSourceSlice(position, count);
        position += count;
    }

    private void Skip(int count)
    {
        EnsureAvailable(count);
        position += count;
    }

    private void WriteTagHeaderFromSource(NbtTagType type, int nameStart, int nameLength)
    {
        WriteByte((byte)type);
        WriteSourceSlice(nameStart, nameLength);
    }

    private void WriteNamedCompoundStart(string name)
    {
        WriteByte((byte)NbtTagType.Compound);
        WriteModifiedUtf8(name);
    }

    private void WriteNamedString(string name, string value)
    {
        WriteByte((byte)NbtTagType.String);
        WriteModifiedUtf8(name);
        WriteModifiedUtf8(value);
    }

    private void WriteNamedInt(string name, int value)
    {
        WriteByte((byte)NbtTagType.Int);
        WriteModifiedUtf8(name);
        WriteInt32(value);
    }

    private void WriteModifiedUtf8(string value)
    {
        using MemoryStream encoded = new(value.Length * 3);
        foreach(char character in value)
        {
            if(character is >= '\u0001' and <= '\u007f')
            {
                encoded.WriteByte((byte)character);
            }
            else if(character <= '\u07ff')
            {
                encoded.WriteByte((byte)(0xc0 | character >> 6 & 0x1f));
                encoded.WriteByte((byte)(0x80 | character & 0x3f));
            }
            else
            {
                encoded.WriteByte((byte)(0xe0 | character >> 12 & 0x0f));
                encoded.WriteByte((byte)(0x80 | character >> 6 & 0x3f));
                encoded.WriteByte((byte)(0x80 | character & 0x3f));
            }
        }
        if(encoded.Length > ushort.MaxValue)
            throw new InvalidDataException("NBT string exceeds the Modified UTF-8 length limit.");
        WriteUInt16(checked((ushort)encoded.Length));
        WriteBytes(encoded.GetBuffer().AsSpan(0, checked((int)encoded.Length)));
    }

    private void WriteSourceSlice(int start, int length) => WriteBytes(source.Slice(start, length));

    private void WriteBytes(scoped ReadOnlySpan<byte> bytes)
    {
        if(output.Length + bytes.Length > MaximumOutputBytes)
            throw new InvalidDataException("Protected NBT output exceeds the safe size limit.");
        output.Write(bytes);
    }

    private void WriteByte(byte value)
    {
        if(output.Length >= MaximumOutputBytes)
            throw new InvalidDataException("Protected NBT output exceeds the safe size limit.");
        output.WriteByte(value);
    }

    private void WriteInt32(int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        WriteBytes(bytes);
    }

    private void WriteUInt16(ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        WriteBytes(bytes);
    }

    private void CountTag()
    {
        visitedTags++;
        if(visitedTags > MaximumVisitedTags)
            throw new InvalidDataException($"NBT tag count exceeds the {MaximumVisitedTags:N0}-tag limit.");
    }

    private void EnsureAvailable(int count)
    {
        if(count < 0 || position > source.Length - count)
            throw new InvalidDataException("NBT data is truncated or contains an invalid length.");
    }

    private static void EnsureDepth(int depth)
    {
        if(depth > MaximumDepth)
            throw new InvalidDataException($"NBT depth exceeds the {MaximumDepth}-level limit.");
    }

    private enum NbtRewriteMode
    {
        LevelMetadata,
        ScheduledTicks,
        SpawnLocation,
    }

    private enum NbtTagType : byte
    {
        End = 0,
        Byte = 1,
        Short = 2,
        Int = 3,
        Long = 4,
        Float = 5,
        Double = 6,
        ByteArray = 7,
        String = 8,
        List = 9,
        Compound = 10,
        IntArray = 11,
        LongArray = 12,
    }
}

internal static class MinecraftWorldExportProtectionGameRules
{
    private static readonly IReadOnlyDictionary<string, string> Values =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["doFireTick"] = "false",
            ["doMobSpawning"] = "false",
            ["mobGriefing"] = "false",
            ["randomTickSpeed"] = "0",
        };

    public static IEnumerable<KeyValuePair<string, string>> All => Values.OrderBy(static item => item.Key, StringComparer.Ordinal);

    public static bool TryGet(string name, out string value) => Values.TryGetValue(name, out value!);
}
