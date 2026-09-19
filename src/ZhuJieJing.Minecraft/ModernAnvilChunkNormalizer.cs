using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

/// <summary>
/// Normalizes Java 1.13+ local block-state palettes from both Level.Sections and root sections layouts.
/// Packed values are selected by their exact encoded length, covering both pre-1.16 tight packing and the
/// later rule that prevents values from crossing a 64-bit word boundary.
/// </summary>
public sealed class ModernAnvilChunkNormalizer : IMinecraftChunkNormalizer
{
    public const int MaximumChunkNbtBytes = 32 * 1024 * 1024;
    public const int MaximumStoredChunkBytes = 64 * 1024 * 1024;
    private const int BlocksPerSection = 4096;

    public ValueTask<NormalizedMinecraftChunk> NormalizeAsync(
        MinecraftNormalizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Chunk);
        ArgumentNullException.ThrowIfNull(request.Registry);
        ArgumentNullException.ThrowIfNull(request.UnknownDataPolicy);
        cancellationToken.ThrowIfCancellationRequested();

        RawMinecraftChunk rawChunk = request.Chunk;
        ArgumentNullException.ThrowIfNull(rawChunk.IndexEntry);
        ArgumentNullException.ThrowIfNull(rawChunk.OriginalStoredPayload);
        if(!rawChunk.IsDecompressed)
            throw new InvalidDataException("Modern Anvil normalization requires a decompressed NBT payload.");
        if(rawChunk.NbtPayload.IsEmpty || rawChunk.NbtPayload.Length > MaximumChunkNbtBytes)
        {
            throw new InvalidDataException(
                $"Decompressed chunk NBT length must be 1..{MaximumChunkNbtBytes:N0} bytes.");
        }
        if(rawChunk.OriginalStoredPayload.Bytes.IsEmpty ||
           rawChunk.OriginalStoredPayload.Bytes.Length > MaximumStoredChunkBytes)
        {
            throw new InvalidDataException(
                $"Stored chunk payload length must be 1..{MaximumStoredChunkBytes:N0} bytes.");
        }

        string sourcePayloadHash = Convert.ToHexString(SHA256.HashData(rawChunk.OriginalStoredPayload.Bytes.Span));
        if(rawChunk.OriginalStoredPayload.ContentHash is not null &&
           !sourcePayloadHash.Equals(rawChunk.OriginalStoredPayload.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Original stored chunk payload no longer matches its recorded content hash.");
        }

        byte[] nbtBytes = rawChunk.NbtPayload.ToArray();
        ModernChunkNbtData source = new ModernChunkNbtReader(nbtBytes).Read();
        MinecraftChunkAddress expectedAddress = rawChunk.IndexEntry.Address;
        if(source.ChunkX != expectedAddress.X || source.ChunkZ != expectedAddress.Z)
        {
            throw new InvalidDataException(
                $"Chunk NBT coordinates {source.ChunkX},{source.ChunkZ} do not match indexed coordinates " +
                $"{expectedAddress.X},{expectedAddress.Z}.");
        }

        int? effectiveDataVersion = source.DataVersion ?? request.WorldVersion.DataVersion;
        if(effectiveDataVersion is <= 1343 ||
           effectiveDataVersion is null && request.WorldVersion.StorageFamily == MinecraftStorageFamily.LegacyNumericAnvil)
        {
            throw new NotSupportedException(
                $"Modern normalizer cannot decode legacy DataVersion " +
                $"{effectiveDataVersion?.ToString() ?? "unknown"}.");
        }

        List<MinecraftDiagnostic> diagnostics = new();
        if(source.DataVersion is null && request.WorldVersion.DataVersion is null)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "modern.version.unverified",
                MinecraftDiagnosticSeverity.Information,
                "区块采用现代调色板布局，但区块与 level.dat 均未提供可核验的 DataVersion。",
                expectedAddress));
        }
        else if(source.DataVersion is not null && request.WorldVersion.DataVersion is not null &&
                source.DataVersion != request.WorldVersion.DataVersion)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "modern.chunk.data_version.differs",
                MinecraftDiagnosticSeverity.Information,
                $"区块 DataVersion={source.DataVersion}，世界 DataVersion={request.WorldVersion.DataVersion}；" +
                "按区块自身格式解码。",
                expectedAddress,
                "$.DataVersion"));
        }

        List<NormalizedMinecraftSection> sections = new(source.Sections.Count);
        foreach(ModernSectionNbtData section in source.Sections.OrderBy(static item => item.Y))
        {
            cancellationToken.ThrowIfCancellationRequested();
            NormalizedMinecraftSection? normalized = NormalizeSection(expectedAddress, section, diagnostics);
            if(normalized is not null)
                sections.Add(normalized);
        }

        string sourceRelativePath = rawChunk.OriginalStoredPayload.SourceRelativePath;
        string nbtHash = Convert.ToHexString(SHA256.HashData(nbtBytes));
        MinecraftOpaquePayload rootPayload = new(
            MinecraftOpaquePayloadFormat.DecompressedNbtDocument,
            nbtBytes,
            sourceRelativePath,
            "$",
            nbtHash);
        List<MinecraftOpaqueFragment> opaqueFragments =
        [
            new MinecraftOpaqueFragment(
                MinecraftOpaqueScope.ChunkRoot,
                "$",
                rootPayload,
                RequiredForRoundTrip: true),
        ];
        List<NormalizedMinecraftBlockEntity> blockEntities = NormalizeBlockEntities(
            expectedAddress,
            source.BlockEntities,
            sourceRelativePath,
            opaqueFragments,
            diagnostics);

        NormalizedMinecraftSection[] immutableSections = sections.ToArray();
        NormalizedMinecraftBlockEntity[] immutableBlockEntities = blockEntities.ToArray();
        MinecraftOpaqueFragment[] immutableOpaqueFragments = opaqueFragments.ToArray();
        MinecraftDiagnostic[] immutableDiagnostics = diagnostics.ToArray();
        string canonicalHash = ComputeCanonicalHash(expectedAddress, immutableSections, immutableBlockEntities);
        MinecraftChunkProvenance provenance = new(sourcePayloadHash, canonicalHash, canonicalHash);
        MinecraftChunkPassthrough passthrough = new(
            rawChunk.Compression,
            rawChunk.IndexEntry.StorageKind,
            rawChunk.OriginalStoredPayload,
            immutableOpaqueFragments,
            SupportsStructuredMerge: false);

        return ValueTask.FromResult(new NormalizedMinecraftChunk(
            expectedAddress,
            request.WorldVersion,
            immutableSections,
            immutableBlockEntities,
            provenance,
            passthrough,
            immutableDiagnostics));
    }

    private static NormalizedMinecraftSection? NormalizeSection(
        MinecraftChunkAddress chunkAddress,
        ModernSectionNbtData source,
        ICollection<MinecraftDiagnostic> diagnostics)
    {
        if(!source.HasBlockStates)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "modern.section.blocks.absent",
                MinecraftDiagnosticSeverity.Information,
                $"Section Y={source.Y} 不含方块状态容器；按格式语义视为空 Section，不伪造可见方块。",
                chunkAddress,
                source.NbtPath));
            return null;
        }

        IReadOnlyList<ModernPaletteEntryNbtData> sourcePalette = source.Palette ??
            throw new InvalidDataException($"{source.NbtPath}: 方块状态容器缺少 palette");
        if(sourcePalette.Count == 0)
            throw new InvalidDataException($"{source.NbtPath}: palette 不能为空");
        if(sourcePalette.Count > ushort.MaxValue)
            throw new InvalidDataException($"{source.NbtPath}: palette 超出规范化索引容量");

        BlockState[] palette = new BlockState[sourcePalette.Count];
        for(int index = 0; index < sourcePalette.Count; index++)
        {
            ModernPaletteEntryNbtData entry = sourcePalette[index];
            palette[index] = new BlockState(entry.Name, entry.Properties);
        }

        ushort[] indices;
        if(sourcePalette.Count == 1 &&
           (source.PackedBlockStates is null || source.PackedBlockStates.Length == 0))
        {
            indices = new ushort[BlocksPerSection];
        }
        else
        {
            long[] packed = source.PackedBlockStates ?? throw new InvalidDataException(
                $"{source.NbtPath}: 含 {sourcePalette.Count} 个状态的 palette 缺少打包 data/BlockStates");
            indices = DecodePaletteIndices(sourcePalette.Count, packed, source.NbtPath);
            if(sourcePalette.Count == 1)
            {
                diagnostics.Add(new MinecraftDiagnostic(
                    "modern.section.singleton.data.redundant",
                    MinecraftDiagnosticSeverity.Information,
                    $"Section Y={source.Y} 的单状态 palette 仍携带全零打包数组；已兼容读取。",
                    chunkAddress,
                    source.NbtPath));
            }
        }

        MinecraftSectionLighting? lighting = null;
        if(source.SkyLight is not null || source.BlockLight is not null)
        {
            lighting = new MinecraftSectionLighting(
                source.SkyLight ?? Array.Empty<byte>(),
                source.BlockLight ?? Array.Empty<byte>());
        }
        if(source.BlockLight is null)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "modern.section.block_light.missing",
                MinecraftDiagnosticSeverity.Information,
                $"Section Y={source.Y} 没有 BlockLight；未伪造光照数组。",
                chunkAddress,
                $"{source.NbtPath}.BlockLight"));
        }
        if(source.SkyLight is null)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "modern.section.sky_light.missing",
                MinecraftDiagnosticSeverity.Information,
                $"Section Y={source.Y} 没有 SkyLight；这在无天空维度可能是正常情况。",
                chunkAddress,
                $"{source.NbtPath}.SkyLight"));
        }

        SectionCoordinate coordinate = new(
            chunkAddress.Dimension.Value,
            chunkAddress.X,
            source.Y,
            chunkAddress.Z);
        return new NormalizedMinecraftSection(
            coordinate,
            palette,
            indices,
            lighting,
            Array.Empty<MinecraftOpaqueFragment>());
    }

    internal static ushort[] DecodePaletteIndices(int paletteCount, ReadOnlySpan<long> packed, string nbtPath)
    {
        int bitsPerEntry = Math.Max(4, BitsRequired(paletteCount - 1));
        int tightLongCount = checked((BlocksPerSection * bitsPerEntry + 63) / 64);
        int entriesPerPaddedLong = 64 / bitsPerEntry;
        int paddedLongCount = checked((BlocksPerSection + entriesPerPaddedLong - 1) / entriesPerPaddedLong);
        bool tight = packed.Length == tightLongCount;
        bool padded = packed.Length == paddedLongCount;
        if(!tight && !padded)
        {
            throw new InvalidDataException(
                $"{nbtPath}: 打包数组长度为 {packed.Length}，但 {bitsPerEntry} 位 palette 应为 " +
                $"{tightLongCount}（连续）或 {paddedLongCount}（不跨 long）");
        }

        bool usePadded = padded && !tight;
        ulong mask = (1UL << bitsPerEntry) - 1UL;
        ushort[] result = new ushort[BlocksPerSection];
        for(int blockIndex = 0; blockIndex < result.Length; blockIndex++)
        {
            ulong paletteIndex;
            if(usePadded)
            {
                int wordIndex = blockIndex / entriesPerPaddedLong;
                int bitOffset = blockIndex % entriesPerPaddedLong * bitsPerEntry;
                paletteIndex = (unchecked((ulong)packed[wordIndex]) >> bitOffset) & mask;
            }
            else
            {
                int firstBit = checked(blockIndex * bitsPerEntry);
                int wordIndex = firstBit >> 6;
                int bitOffset = firstBit & 63;
                paletteIndex = unchecked((ulong)packed[wordIndex]) >> bitOffset;
                int lowBits = 64 - bitOffset;
                if(lowBits < bitsPerEntry)
                    paletteIndex |= unchecked((ulong)packed[wordIndex + 1]) << lowBits;
                paletteIndex &= mask;
            }

            if(paletteIndex >= (ulong)paletteCount)
            {
                throw new InvalidDataException(
                    $"{nbtPath}: 方块索引 {blockIndex} 引用了 palette[{paletteIndex}]，" +
                    $"但 palette 仅有 {paletteCount} 项；拒绝将损坏内容变成空气");
            }
            result[blockIndex] = (ushort)paletteIndex;
        }

        return result;
    }

    private static int BitsRequired(int maximumValue)
    {
        int bits = 0;
        while(maximumValue > 0)
        {
            bits++;
            maximumValue >>= 1;
        }
        return bits;
    }

    private static List<NormalizedMinecraftBlockEntity> NormalizeBlockEntities(
        MinecraftChunkAddress chunkAddress,
        IReadOnlyList<ModernBlockEntityNbtData> sourceEntities,
        string sourceRelativePath,
        ICollection<MinecraftOpaqueFragment> opaqueFragments,
        ICollection<MinecraftDiagnostic> diagnostics)
    {
        List<NormalizedMinecraftBlockEntity> result = new(sourceEntities.Count);
        if(sourceEntities.Count > 0)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "modern.block_entities.opaque_passthrough",
                MinecraftDiagnosticSeverity.Warning,
                $"区块含 {sourceEntities.Count} 个方块实体；当前仅规范化身份与坐标，完整 NBT 已原样透传。",
                chunkAddress));
        }

        foreach(ModernBlockEntityNbtData source in sourceEntities)
        {
            string hash = Convert.ToHexString(SHA256.HashData(source.CompoundPayload));
            MinecraftOpaquePayload payload = new(
                MinecraftOpaquePayloadFormat.EncodedNbtTag,
                source.CompoundPayload,
                sourceRelativePath,
                source.NbtPath,
                hash);
            opaqueFragments.Add(new MinecraftOpaqueFragment(
                MinecraftOpaqueScope.BlockEntity,
                source.NbtPath,
                payload,
                RequiredForRoundTrip: true));

            if(string.IsNullOrWhiteSpace(source.TypeId) ||
               source.X is null || source.Y is null || source.Z is null)
            {
                diagnostics.Add(new MinecraftDiagnostic(
                    "modern.block_entity.identity_missing",
                    MinecraftDiagnosticSeverity.Warning,
                    $"{source.NbtPath} 缺少 id 或坐标；实体仍以原始 NBT 保留，但不能安全规范化。",
                    chunkAddress,
                    source.NbtPath));
                continue;
            }

            result.Add(new NormalizedMinecraftBlockEntity(
                source.TypeId,
                new BlockPosition(source.X.Value, source.Y.Value, source.Z.Value),
                new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>()),
                payload));
        }

        return result;
    }

    private static string ComputeCanonicalHash(
        MinecraftChunkAddress address,
        IReadOnlyList<NormalizedMinecraftSection> sections,
        IReadOnlyList<NormalizedMinecraftBlockEntity> blockEntities)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendString(hash, "zhujiejing.normalized-modern-chunk/1");
        AppendString(hash, address.Dimension.Value);
        AppendInt32(hash, address.X);
        AppendInt32(hash, address.Z);
        AppendInt32(hash, sections.Count);
        foreach(NormalizedMinecraftSection section in sections)
        {
            AppendInt32(hash, section.Coordinate.Y);
            AppendInt32(hash, section.Palette.Count);
            foreach(BlockState state in section.Palette)
                AppendString(hash, state.CanonicalKey);
            AppendInt32(hash, section.PaletteIndices.Length);
            ReadOnlySpan<ushort> paletteIndices = section.PaletteIndices.Span;
            byte[] encodedIndices = GC.AllocateUninitializedArray<byte>(paletteIndices.Length * sizeof(ushort));
            for(int index = 0; index < paletteIndices.Length; index++)
                BinaryPrimitives.WriteUInt16BigEndian(encodedIndices.AsSpan(index * sizeof(ushort)), paletteIndices[index]);
            hash.AppendData(encodedIndices);
            if(section.Lighting is null)
            {
                AppendInt32(hash, -1);
            }
            else
            {
                AppendBytes(hash, section.Lighting.SkyLightNibbles.Span);
                AppendBytes(hash, section.Lighting.BlockLightNibbles.Span);
            }
        }

        AppendInt32(hash, blockEntities.Count);
        foreach(NormalizedMinecraftBlockEntity entity in blockEntities)
        {
            AppendString(hash, entity.TypeId);
            AppendInt32(hash, entity.Position.X);
            AppendInt32(hash, entity.Position.Y);
            AppendInt32(hash, entity.Position.Z);
            AppendString(hash, entity.OriginalNbt.ContentHash ?? string.Empty);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendBytes(IncrementalHash hash, ReadOnlySpan<byte> bytes)
    {
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }
}
