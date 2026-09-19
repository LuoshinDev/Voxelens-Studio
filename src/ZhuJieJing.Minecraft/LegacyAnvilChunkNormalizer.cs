using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

/// <summary>
/// Strict normalizer for the numeric section layout used by Java Edition 1.12.2. Malformed source data is
/// rejected; unknown non-air IDs become visible stable placeholders and are never converted to air.
/// </summary>
public sealed class LegacyAnvilChunkNormalizer : IMinecraftChunkNormalizer
{
    public const int MaximumChunkNbtBytes = 32 * 1024 * 1024;
    public const int MaximumStoredChunkBytes = 64 * 1024 * 1024;

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
        if (!rawChunk.IsDecompressed)
            throw new InvalidDataException("Legacy Anvil normalization requires a decompressed NBT payload.");
        if (rawChunk.NbtPayload.IsEmpty || rawChunk.NbtPayload.Length > MaximumChunkNbtBytes)
        {
            throw new InvalidDataException(
                $"Decompressed chunk NBT length must be 1..{MaximumChunkNbtBytes:N0} bytes.");
        }
        if (rawChunk.OriginalStoredPayload.Bytes.IsEmpty ||
            rawChunk.OriginalStoredPayload.Bytes.Length > MaximumStoredChunkBytes)
        {
            throw new InvalidDataException(
                $"Stored chunk payload length must be 1..{MaximumStoredChunkBytes:N0} bytes.");
        }
        string sourcePayloadHash = Convert.ToHexString(SHA256.HashData(
            rawChunk.OriginalStoredPayload.Bytes.Span));
        if (rawChunk.OriginalStoredPayload.ContentHash is not null &&
            !sourcePayloadHash.Equals(rawChunk.OriginalStoredPayload.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Original stored chunk payload no longer matches its recorded content hash.");
        }
        if (request.WorldVersion.DataVersion is > 1343 ||
            request.WorldVersion.StorageFamily is MinecraftStorageFamily.FlattenedPalette or
                MinecraftStorageFamily.ModernSectionPalette)
        {
            throw new NotSupportedException(
                $"Legacy normalizer cannot decode DataVersion {request.WorldVersion.DataVersion?.ToString() ?? "unknown"} " +
                $"with storage family {request.WorldVersion.StorageFamily}.");
        }

        byte[] nbtBytes = rawChunk.NbtPayload.ToArray();
        LegacyChunkNbtData source = new LegacyChunkNbtReader(nbtBytes).Read();
        MinecraftChunkAddress expectedAddress = rawChunk.IndexEntry.Address;
        if (source.ChunkX != expectedAddress.X || source.ChunkZ != expectedAddress.Z)
        {
            throw new InvalidDataException(
                $"Chunk NBT coordinates {source.ChunkX},{source.ChunkZ} do not match indexed coordinates " +
                $"{expectedAddress.X},{expectedAddress.Z}.");
        }

        List<MinecraftDiagnostic> diagnostics = new();
        if (request.WorldVersion.DataVersion is null &&
            request.WorldVersion.StorageFamily == MinecraftStorageFamily.Unknown)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "legacy.version.unverified",
                MinecraftDiagnosticSeverity.Information,
                "区块采用旧版数字 ID 布局，但 level.dat 未提供可核验的 DataVersion。",
                expectedAddress));
        }

        List<NormalizedMinecraftSection> sections = new(source.Sections.Count);
        HashSet<(ushort Id, byte Metadata, string Code)> reportedRegistryDiagnostics = new();
        Dictionary<int, MinecraftRegistryResolution> resolutionCache = new();
        IReadOnlyDictionary<int, LegacySectionNbtData> sourceSections = source.Sections.ToDictionary(
            static section => section.Y);
        foreach (LegacySectionNbtData section in source.Sections.OrderBy(static item => item.Y))
        {
            cancellationToken.ThrowIfCancellationRequested();
            sections.Add(NormalizeSection(
                expectedAddress,
                section,
                sourceSections,
                request.Registry,
                resolutionCache,
                diagnostics,
                reportedRegistryDiagnostics));
        }

        string sourceRelativePath = rawChunk.OriginalStoredPayload.SourceRelativePath;
        string nbtHash = Convert.ToHexString(SHA256.HashData(nbtBytes));
        MinecraftOpaquePayload rootPayload = new(
            MinecraftOpaquePayloadFormat.DecompressedNbtDocument,
            nbtBytes,
            sourceRelativePath,
            "$",
            nbtHash);
        List<MinecraftOpaqueFragment> opaqueFragments = new()
        {
            new MinecraftOpaqueFragment(
                MinecraftOpaqueScope.ChunkRoot,
                "$",
                rootPayload,
                RequiredForRoundTrip: true),
        };
        List<NormalizedMinecraftBlockEntity> blockEntities = NormalizeBlockEntities(
            expectedAddress,
            source.BlockEntities,
            sourceRelativePath,
            opaqueFragments,
            diagnostics);

        LegacySkullStates.Restore(sections, blockEntities);
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

    private static NormalizedMinecraftSection NormalizeSection(
        MinecraftChunkAddress chunkAddress,
        LegacySectionNbtData source,
        IReadOnlyDictionary<int, LegacySectionNbtData> sourceSections,
        IMinecraftBlockRegistry registry,
        IDictionary<int, MinecraftRegistryResolution> resolutionCache,
        ICollection<MinecraftDiagnostic> diagnostics,
        ISet<(ushort Id, byte Metadata, string Code)> reportedRegistryDiagnostics)
    {
        List<BlockState> palette = new();
        Dictionary<BlockState, ushort> paletteLookup = new();
        ushort[] indices = new ushort[4096];
        ushort[] sourceStates = new ushort[4096];

        for (int index = 0; index < indices.Length; index++)
        {
            byte metadata = ReadNibble(source.Data, index);
            ushort numericId = source.Blocks[index];
            if (source.Add is not null)
                numericId |= checked((ushort)(ReadNibble(source.Add, index) << 8));
            sourceStates[index] = (ushort)(numericId << 4 | metadata);

            BlockState state;
            if (numericId == 0)
            {
                state = BlockState.Air;
            }
            else
            {
                int legacyKey = numericId << 4 | metadata;
                if (!resolutionCache.TryGetValue(legacyKey, out MinecraftRegistryResolution? resolution))
                {
                    resolution = registry.ResolveLegacy(numericId, metadata) ??
                                 throw new InvalidOperationException(
                                     $"Registry returned null for {numericId}:{metadata}.");
                    resolutionCache.Add(legacyKey, resolution);
                }
                state = resolution.State ?? throw new InvalidOperationException(
                    $"Registry returned a null state for {numericId}:{metadata}.");
                if (state.IsAir)
                {
                    throw new InvalidDataException(
                        $"Registry attempted to map non-air legacy ID {numericId}:{metadata} to air.");
                }

                if (numericId == 175 && (metadata & 0x08) != 0)
                {
                    if (TryResolveDoublePlantUpperState(
                            source,
                            sourceSections,
                            index,
                            registry,
                            resolutionCache,
                            out BlockState? contextualState))
                    {
                        state = contextualState;
                    }
                    else if (reportedRegistryDiagnostics.Add((numericId, metadata, "legacy.double_plant.upper_without_lower")))
                    {
                        diagnostics.Add(new MinecraftDiagnostic(
                            "legacy.double_plant.upper_without_lower",
                            MinecraftDiagnosticSeverity.Warning,
                            $"双高植物上半株 {numericId}:{metadata} 的正下方没有可识别的下半株；保留原始可见状态。",
                            chunkAddress,
                            $"Level.Sections[Y={source.Y}].Blocks[{index}]"));
                    }
                }

                MinecraftDiagnostic? diagnostic = resolution.Diagnostic;
                if (resolution.Source == MinecraftRegistryResolutionSource.VisibleUnknownPlaceholder &&
                    diagnostic is null)
                {
                    diagnostic = new MinecraftDiagnostic(
                        "legacy.block.unknown",
                        MinecraftDiagnosticSeverity.Warning,
                        $"未知的非空气旧版方块 {numericId}:{metadata} 已使用醒目占位方块。");
                }

                if (diagnostic is not null &&
                    reportedRegistryDiagnostics.Add((numericId, metadata, diagnostic.Code)))
                {
                    diagnostics.Add(diagnostic with
                    {
                        Chunk = chunkAddress,
                        NbtPath = $"Level.Sections[Y={source.Y}].Blocks[{index}]",
                    });
                }
            }

            if (!paletteLookup.TryGetValue(state, out ushort paletteIndex))
            {
                paletteIndex = checked((ushort)palette.Count);
                palette.Add(state);
                paletteLookup.Add(state, paletteIndex);
            }
            indices[index] = paletteIndex;
        }

        MinecraftSectionLighting? lighting = null;
        if (source.SkyLight is not null || source.BlockLight is not null)
        {
            lighting = new MinecraftSectionLighting(
                source.SkyLight ?? Array.Empty<byte>(),
                source.BlockLight ?? Array.Empty<byte>());
        }
        if (source.BlockLight is null)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "legacy.section.block_light.missing",
                MinecraftDiagnosticSeverity.Information,
                $"Section Y={source.Y} 没有 BlockLight；未伪造光照数组。",
                chunkAddress,
                $"Level.Sections[Y={source.Y}].BlockLight"));
        }
        if (source.SkyLight is null)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "legacy.section.sky_light.missing",
                MinecraftDiagnosticSeverity.Information,
                $"Section Y={source.Y} 没有 SkyLight；这在无天空维度可能是正常情况。",
                chunkAddress,
                $"Level.Sections[Y={source.Y}].SkyLight"));
        }

        SectionCoordinate coordinate = new(
            chunkAddress.Dimension.Value,
            chunkAddress.X,
            source.Y,
            chunkAddress.Z);
        return new NormalizedMinecraftSection(
            coordinate,
            palette.ToArray(),
            indices,
            lighting,
            Array.Empty<MinecraftOpaqueFragment>()) { SourceLegacyStates = sourceStates };
    }

    private static bool TryResolveDoublePlantUpperState(
        LegacySectionNbtData source,
        IReadOnlyDictionary<int, LegacySectionNbtData> sourceSections,
        int upperIndex,
        IMinecraftBlockRegistry registry,
        IDictionary<int, MinecraftRegistryResolution> resolutionCache,
        out BlockState state)
    {
        LegacySectionNbtData lowerSection = source;
        int lowerIndex;
        if ((upperIndex >> 8) == 0)
        {
            if (!sourceSections.TryGetValue(source.Y - 1, out LegacySectionNbtData? precedingSection))
            {
                state = BlockState.Air;
                return false;
            }
            lowerSection = precedingSection;
            lowerIndex = (15 << 8) | (upperIndex & 0xff);
        }
        else
        {
            lowerIndex = upperIndex - 256;
        }

        ushort lowerId = lowerSection.Blocks[lowerIndex];
        if (lowerSection.Add is not null)
            lowerId |= checked((ushort)(ReadNibble(lowerSection.Add, lowerIndex) << 8));
        byte lowerMetadata = ReadNibble(lowerSection.Data, lowerIndex);
        if (lowerId != 175 || lowerMetadata > 5)
        {
            state = BlockState.Air;
            return false;
        }

        int lowerLegacyKey = lowerId << 4 | lowerMetadata;
        if (!resolutionCache.TryGetValue(lowerLegacyKey, out MinecraftRegistryResolution? lowerResolution))
        {
            lowerResolution = registry.ResolveLegacy(lowerId, lowerMetadata) ??
                              throw new InvalidOperationException(
                                  $"Registry returned null for {lowerId}:{lowerMetadata}.");
            resolutionCache.Add(lowerLegacyKey, lowerResolution);
        }
        BlockState lowerState = lowerResolution.State ?? throw new InvalidOperationException(
            $"Registry returned a null state for {lowerId}:{lowerMetadata}.");
        if (lowerState.IsAir)
        {
            throw new InvalidDataException(
                $"Registry attempted to map non-air legacy ID {lowerId}:{lowerMetadata} to air.");
        }
        if (!lowerState.Properties.TryGetValue("half", out string? lowerHalf) || lowerHalf != "lower")
        {
            state = BlockState.Air;
            return false;
        }

        Dictionary<string, string> upperProperties = new(lowerState.Properties, StringComparer.Ordinal)
        {
            ["half"] = "upper",
        };
        state = new BlockState(lowerState.Name, upperProperties);
        return true;
    }

    private static List<NormalizedMinecraftBlockEntity> NormalizeBlockEntities(
        MinecraftChunkAddress chunkAddress,
        IReadOnlyList<LegacyBlockEntityNbtData> sourceEntities,
        string sourceRelativePath,
        ICollection<MinecraftOpaqueFragment> opaqueFragments,
        ICollection<MinecraftDiagnostic> diagnostics)
    {
        List<NormalizedMinecraftBlockEntity> result = new(sourceEntities.Count);
        if (sourceEntities.Count > 0)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "legacy.block_entities.opaque_passthrough",
                MinecraftDiagnosticSeverity.Warning,
                $"区块含 {sourceEntities.Count} 个方块实体；当前仅规范化身份与坐标，完整 NBT 已原样透传。",
                chunkAddress,
                "Level.TileEntities"));
        }

        foreach (LegacyBlockEntityNbtData source in sourceEntities)
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

            if (string.IsNullOrWhiteSpace(source.TypeId) ||
                source.X is null || source.Y is null || source.Z is null)
            {
                diagnostics.Add(new MinecraftDiagnostic(
                    "legacy.block_entity.identity_missing",
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

    private static byte ReadNibble(byte[] values, int blockIndex)
    {
        byte packed = values[blockIndex >> 1];
        return (byte)((blockIndex & 1) == 0 ? packed & 0x0f : packed >> 4 & 0x0f);
    }

    private static string ComputeCanonicalHash(
        MinecraftChunkAddress address,
        IReadOnlyList<NormalizedMinecraftSection> sections,
        IReadOnlyList<NormalizedMinecraftBlockEntity> blockEntities)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendString(hash, "zhujiejing.normalized-legacy-chunk/1");
        AppendString(hash, address.Dimension.Value);
        AppendInt32(hash, address.X);
        AppendInt32(hash, address.Z);
        AppendInt32(hash, sections.Count);
        foreach (NormalizedMinecraftSection section in sections)
        {
            AppendInt32(hash, section.Coordinate.Y);
            AppendInt32(hash, section.Palette.Count);
            foreach (BlockState state in section.Palette)
                AppendString(hash, state.CanonicalKey);
            AppendInt32(hash, section.PaletteIndices.Length);
            ReadOnlySpan<ushort> paletteIndices = section.PaletteIndices.Span;
            byte[] encodedIndices = GC.AllocateUninitializedArray<byte>(paletteIndices.Length * sizeof(ushort));
            for (int index = 0; index < paletteIndices.Length; index++)
                BinaryPrimitives.WriteUInt16BigEndian(encodedIndices.AsSpan(index * sizeof(ushort)), paletteIndices[index]);
            hash.AppendData(encodedIndices);
            if (section.Lighting is null)
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
        foreach (NormalizedMinecraftBlockEntity entity in blockEntities)
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
