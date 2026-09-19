using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

/// <summary>
/// Streams canonical chunks into a new Java 1.12.2 world folder. Every chunk is re-encoded into numeric
/// ID:data sections and appended directly to its MCA file; the source world and source payloads are read-only.
/// </summary>
public sealed class Minecraft1122WorldExporter : IMinecraftWorldExporter
{
    private const int SectorBytes = 4096;
    private const int RegionHeaderBytes = SectorBytes * 2;
    private const int MaximumChunkNbtBytes = 64 * 1024 * 1024;
    private readonly IMinecraftBlockDowngradeRules rules;

    public Minecraft1122WorldExporter(IMinecraftBlockDowngradeRules? rules = null)
    {
        this.rules = rules ?? Minecraft1122BlockDowngradeRules.Instance;
    }

    public async Task<MinecraftWorldExportResult> ExportAsync(
        MinecraftWorldExportRequest request,
        IProgress<MinecraftWorldExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        Report(progress, MinecraftWorldExportStage.Validating, 0, 0, 0, 0, null, "正在验证导出请求");

        string sourceRevision = request.Chunks.Revision;
        if(!string.Equals(request.ExpectedSourceRevision, sourceRevision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected source revision {request.ExpectedSourceRevision}, but the chunk source is {sourceRevision}.");
        }
        ValidateTarget(request.Target);
        ValidateDimensions(request.Chunks.Dimensions);
        ValidateOpaquePayloadHash(request.SourceLevelMetadata, "source level metadata");
        BlockPosition outputSpawn = TranslateSpawn(request.SpawnLocation, request.YOffset, request.Target.BuildRange);
        DestinationPlan destination = PlanDestination(request.DestinationDirectory, request.DestinationPolicy);

        Directory.CreateDirectory(destination.ParentDirectory);
        string stagingDirectory = Path.Combine(
            destination.ParentDirectory,
            $".{destination.OutputName}.zjj-staging-{Guid.NewGuid():N}");
        if(Directory.Exists(stagingDirectory) || File.Exists(stagingDirectory))
            throw new IOException($"Generated staging path already exists: {stagingDirectory}");

        string? backupDirectory = null;
        long chunksRead = 0;
        long chunksWritten = 0;
        long regionsWritten = 0;
        long bytesWritten = 0;
        long blockCount = 0;
        List<MinecraftDiagnostic> diagnostics = new();
        Dictionary<BlockState, LegacyBlockEncoding> encodingCache = new();
        ExportDiagnosticAccumulator diagnosticAccumulator = new(diagnostics);
        Minecraft1122LightBaker lightBaker = new(request.Chunks, state => ResolveEncoding(state, request.Target, encodingCache));

        try
        {
            Report(progress, MinecraftWorldExportStage.PreparingStagingDirectory, 0, 0, 0, 0, null, "正在创建临时世界目录");
            Directory.CreateDirectory(stagingDirectory);
            await using Minecraft1122RegionWriter regionWriter = new();

            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, MinecraftWorldExportStage.WritingLevelMetadata, 0, 0, 0, 0, null, "正在写入 1.12.2 世界元数据");
            Minecraft1122LevelDatTranscodeResult levelDat = Minecraft1122LevelDatTranscoder.Transcode(
                request.SourceLevelMetadata,
                destination.OutputName,
                outputSpawn);
            foreach(MinecraftDiagnostic diagnostic in levelDat.Diagnostics)
                diagnostics.Add(diagnostic);
            string levelPath = Path.Combine(stagingDirectory, "level.dat");
            await File.WriteAllBytesAsync(levelPath, levelDat.GZipBytes.ToArray(), cancellationToken).ConfigureAwait(false);
            bytesWritten = checked(bytesWritten + levelDat.GZipBytes.Length);
            diagnostics.Add(new MinecraftDiagnostic(
                "export.level.regenerated",
                MinecraftDiagnosticSeverity.Information,
                "level.dat 已按 Java 1.12.2 重建；仅经校验的兼容设置被转码，源版本专属元数据未原样复用。"));
            diagnostics.Add(new MinecraftDiagnostic(
                "export.world.building_protection",
                MinecraftDiagnosticSeverity.Information,
                "已开启建筑展示保护：关闭随机刻、火焰更新、生物生成与生物破坏，不携带调度刻并将水/熔岩写为静态 ID。"));

            Report(progress, MinecraftWorldExportStage.WritingRegions, 0, 0, 0, bytesWritten, null, "正在逐区块写入 MCA");
            foreach(MinecraftDimensionId dimension in request.Chunks.Dimensions.Distinct())
            {
                await foreach(NormalizedMinecraftChunk chunk in request.Chunks
                                   .EnumerateAsync(dimension, cancellationToken: cancellationToken)
                                   .ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureSourceRevision(request.Chunks, sourceRevision);
                    if(chunk.Address.Dimension != dimension)
                        throw new InvalidDataException($"Chunk source returned {chunk.Address} while enumerating {dimension}.");
                    if(chunk.Diagnostics.Any(static item =>
                           item.Severity == MinecraftDiagnosticSeverity.Blocking))
                    {
                        throw new InvalidDataException($"Chunk {chunk.Address} contains a blocking source diagnostic.");
                    }
                    foreach(MinecraftDiagnostic sourceDiagnostic in chunk.Diagnostics)
                        diagnostics.Add(sourceDiagnostic);
                    if(chunk.Passthrough.UnknownFragments.Any(static item =>
                           item.Scope != MinecraftOpaqueScope.BlockEntity) ||
                       chunk.Sections.Any(static section => section.UnknownFragments.Count > 0))
                    {
                        diagnosticAccumulator.ReportOpaqueRootRegenerated(chunk.Address);
                    }
                    chunksRead = checked(chunksRead + 1);

                    BakedLegacyChunkLighting lighting = await lightBaker.BakeAsync(chunk, cancellationToken).ConfigureAwait(false);
                    EncodedLegacyChunk encoded = EncodeChunk(
                        chunk,
                        lighting,
                        request.Target,
                        encodingCache,
                        diagnosticAccumulator,
                        Path.Combine(stagingDirectory, "zjj-archived-block-entities"),
                        cancellationToken);
                    byte[] compressed = CompressZlib(encoded.Nbt);
                    MinecraftRegionAddress region = MinecraftRegionAddress.FromChunk(chunk.Address);
                    string regionDirectory = ResolveRegionDirectory(stagingDirectory, dimension);
                    Directory.CreateDirectory(regionDirectory);
                    string regionPath = Path.Combine(regionDirectory, $"r.{region.X}.{region.Z}.mca");
                    MinecraftRegionAppendResult append = await regionWriter.AppendAsync(
                        regionPath,
                        chunk.Address,
                        compressed,
                        cancellationToken).ConfigureAwait(false);
                    if(append.CreatedRegion)
                    {
                        regionsWritten = checked(regionsWritten + 1);
                        bytesWritten = checked(bytesWritten + RegionHeaderBytes);
                    }
                    bytesWritten = checked(bytesWritten + append.RecordBytes);
                    chunksWritten = checked(chunksWritten + 1);
                    blockCount = checked(blockCount + encoded.NonAirBlockCount);
                    Report(
                        progress,
                        MinecraftWorldExportStage.WritingRegions,
                        chunksRead,
                        chunksWritten,
                        regionsWritten,
                        bytesWritten,
                        chunk.Address,
                        "正在写入 1.12.2 区块");
                }
            }

            await regionWriter.FlushAndCloseAsync(cancellationToken).ConfigureAwait(false);

            EnsureSourceRevision(request.Chunks, sourceRevision);
            cancellationToken.ThrowIfCancellationRequested();
            Report(
                progress,
                MinecraftWorldExportStage.CopyingAuxiliaryFiles,
                chunksRead,
                chunksWritten,
                regionsWritten,
                bytesWritten,
                null,
                "正在复制允许的附属文件");
            if(request.AuxiliaryFiles is not null)
            {
                long copiedBytes = await CopyAuxiliaryFilesAsync(
                    request.AuxiliaryFiles,
                    request.AuxiliaryFilePolicy ?? new MinecraftAuxiliaryExportPolicy(),
                    stagingDirectory,
                    diagnostics,
                    cancellationToken).ConfigureAwait(false);
                bytesWritten = checked(bytesWritten + copiedBytes);
            }

            if(request.AuxiliaryFiles is IReadOnlyMinecraftWorld sourceWorld)
            {
                WorldSourceValidationResult validation = await sourceWorld
                    .ValidateSourceAsync(cancellationToken)
                    .ConfigureAwait(false);
                if(!validation.IsStable)
                {
                    string changed = validation.ChangedRelativePaths.Count == 0
                        ? "源世界文件"
                        : string.Join("、", validation.ChangedRelativePaths.Take(8));
                    throw new IOException($"源世界在导出期间发生变化，已取消提交：{changed}");
                }
            }

            diagnosticAccumulator.Flush();
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSourceRevision(request.Chunks, sourceRevision);
            Report(
                progress,
                MinecraftWorldExportStage.CommittingDirectory,
                chunksRead,
                chunksWritten,
                regionsWritten,
                bytesWritten,
                null,
                "正在提交世界目录");
            backupDirectory = CommitStagingDirectory(stagingDirectory, destination);

            Report(
                progress,
                MinecraftWorldExportStage.Complete,
                chunksRead,
                chunksWritten,
                regionsWritten,
                bytesWritten,
                null,
                "世界文件夹导出完成");
            return new MinecraftWorldExportResult(
                destination.OutputDirectory,
                backupDirectory,
                chunksWritten,
                regionsWritten,
                blockCount,
                CopiedOpaqueChunkCount: 0,
                ReencodedChunkCount: chunksWritten,
                VerticallyClippedBlockCount: 0,
                diagnostics.ToArray());
        }
        catch(Exception exception)
        {
            Exception? cleanupFailure = TryDeleteStagingDirectory(stagingDirectory, destination.ParentDirectory);
            if(cleanupFailure is not null)
                throw new AggregateException("World export failed and its staging directory could not be removed.", exception, cleanupFailure);
            throw;
        }
    }

    private EncodedLegacyChunk EncodeChunk(
        NormalizedMinecraftChunk chunk,
        BakedLegacyChunkLighting lighting,
        MinecraftTargetProfile target,
        IDictionary<BlockState, LegacyBlockEncoding> encodingCache,
        ExportDiagnosticAccumulator diagnostics,
        string archiveDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        HashSet<int> sectionYs = new();
        List<LegacySectionPayload> sections = new(chunk.Sections.Count);
        int[] heightMap = lighting.HeightMap;
        long nonAirBlockCount = 0;

        foreach(NormalizedMinecraftSection section in chunk.Sections.OrderBy(static item => item.Coordinate.Y))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateSection(chunk.Address, section, sectionYs);
            LegacySectionPayload encoded = EncodeSection(
                chunk.Address,
                section,
                target,
                encodingCache,
                heightMap,
                diagnostics,
                cancellationToken);
            sections.Add(encoded);
            nonAirBlockCount = checked(nonAirBlockCount + encoded.NonAirBlockCount);
        }
        foreach((int sectionY, MinecraftSectionLighting baked) in lighting.Sections)
        {
            LegacySectionPayload? section = sections.FirstOrDefault(item => item.Y == sectionY);
            if(section is null)
            {
                section = new LegacySectionPayload(sectionY, new byte[4096], new byte[2048], null,
                    new byte[2048], new byte[2048], 0, true);
                sections.Add(section);
            }
            baked.BlockLightNibbles.Span.CopyTo(section.BlockLight);
            baked.SkyLightNibbles.Span.CopyTo(section.SkyLight);
        }
        sections.Sort(static (left, right) => left.Y.CompareTo(right.Y));
        diagnostics.ReportLightingBaked(chunk.Address, lighting.HasStaticSources);

        List<byte[]> blockEntities = new(chunk.BlockEntities.Count);
        foreach(NormalizedMinecraftBlockEntity entity in LegacySkullStates.WithMissingEntities(chunk))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateBlockEntityPosition(chunk.Address, entity);
            LegacySectionPayload? entitySection = sections.FirstOrDefault(item => item.Y == entity.Position.Y / 16);
            int entityIndex = (entity.Position.Y & 15) << 8 | (entity.Position.Z & 15) << 4 | entity.Position.X & 15;
            ushort entityBlockId = entitySection is null ? (ushort)0 : entitySection.Blocks[entityIndex];
            if(entitySection?.Add is { } add) entityBlockId |= (ushort)(ReadNibble(add, entityIndex) << 8);
            byte entityBlockData = entitySection is null ? (byte)0 : ReadNibble(entitySection.Data, entityIndex);
            if(entityBlockId != 0 && !Minecraft1122BlockEntityCodec.SupportsBlockEntity(entityBlockId))
            {
                // These decorative replacements cannot host their original entities. Archive before
                // omitting the incompatible tile entity; fail the export if archiving fails.
                Directory.CreateDirectory(archiveDirectory);
                string archiveName = $"block-entity-{Guid.NewGuid():N}.json";
                File.WriteAllText(Path.Combine(archiveDirectory, archiveName), System.Text.Json.JsonSerializer.Serialize(new
                {
                    dimension = chunk.Address.Dimension.Value,
                    targetPosition = entity.Position,
                    targetBlock = $"{entityBlockId}:{entityBlockData}",
                    entity.TypeId,
                    entity.SourceDataVersion,
                    originalPayload = entity.OriginalNbt,
                    note = "原方块已替换为不支持方块实体的旧版方块；原始实体数据仅归档，不会在 1.12.2 游戏中生效。",
                }));
                diagnostics.ReportArchivedReplacedEntity(chunk.Address, entity.TypeId, entityBlockId, archiveName);
                continue;
            }
            LegacyBlockEntityRewriteResult rewritten = Minecraft1122BlockEntityCodec.Rewrite(entity,
                new LegacyBlockEncoding(entityBlockId, entityBlockData), rules,
                entity.SourceDataVersion ?? chunk.SourceVersion.DataVersion);
            if(!rewritten.PreservedUnknownData)
            {
                diagnostics.ReportReducedBlockEntity(chunk.Address, entity.TypeId, rewritten.ReductionReason);
            }
            BlockState? entityState = LegacySkullStates.StateAt(chunk, entity.Position);
            blockEntities.Add(entityBlockId == 144 && entityState is not null
                ? LegacySkullStates.WriteState(rewritten.CompoundPayload, entityState)
                : rewritten.CompoundPayload);
        }
        if(blockEntities.Count > 0)
            diagnostics.ReportBlockEntityCompatibility(chunk.Address, blockEntities.Count);

        diagnostics.ReportDefaultBiomes();
        diagnostics.ReportEntitiesUnavailable();
        byte[] nbt = BuildChunkNbt(
            chunk.Address,
            sections,
            heightMap,
            blockEntities,
            completeLighting: true);
        if(nbt.Length > MaximumChunkNbtBytes)
            throw new InvalidDataException($"Chunk {chunk.Address} NBT exceeds {MaximumChunkNbtBytes:N0} bytes.");
        return new EncodedLegacyChunk(nbt, nonAirBlockCount);
    }

    private LegacySectionPayload EncodeSection(
        MinecraftChunkAddress chunkAddress,
        NormalizedMinecraftSection section,
        MinecraftTargetProfile target,
        IDictionary<BlockState, LegacyBlockEncoding> encodingCache,
        Span<int> heightMap,
        ExportDiagnosticAccumulator diagnostics,
        CancellationToken cancellationToken)
    {
        byte[] blocks = new byte[4096];
        byte[] data = new byte[2048];
        byte[]? add = null;
        long nonAir = 0;
        ReadOnlySpan<ushort> indices = section.PaletteIndices.Span;
        for(int localIndex = 0; localIndex < indices.Length; localIndex++)
        {
            if((localIndex & 255) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            ushort paletteIndex = indices[localIndex];
            if(paletteIndex >= section.Palette.Count)
            {
                throw new InvalidDataException(
                    $"Section {section.Coordinate} index {localIndex} references palette[{paletteIndex}] " +
                    $"but the palette has {section.Palette.Count} entries.");
            }
            BlockState state = section.Palette[paletteIndex];
            if(state.IsAir)
                continue;

            LegacyBlockEncoding encoding = MinecraftWorldExportProtection.MakeLegacyFluidStatic(
                ResolveEncoding(state, target, encodingCache));
            if(encoding.NumericId == 0)
                continue;
            blocks[localIndex] = (byte)(encoding.NumericId & 0xff);
            SetNibble(data, localIndex, encoding.Metadata);
            if(encoding.NumericId > byte.MaxValue)
            {
                add ??= new byte[2048];
                SetNibble(add, localIndex, (byte)(encoding.NumericId >> 8));
            }
            nonAir = checked(nonAir + 1);

            int localY = localIndex >> 8;
            int columnIndex = localIndex & 255;
            int worldY = checked(section.Coordinate.Y * 16 + localY);
            // HeightMap is computed from target light opacity by the cross-chunk lighting pass.
        }

        byte[] blockLight = new byte[2048];
        byte[] skyLight = new byte[2048];
        return new LegacySectionPayload(
            section.Coordinate.Y,
            blocks,
            data,
            add,
            blockLight,
            skyLight,
            nonAir,
            false);
    }

    private LegacyBlockEncoding ResolveEncoding(
        BlockState state,
        MinecraftTargetProfile target,
        IDictionary<BlockState, LegacyBlockEncoding> cache)
    {
        if(cache.TryGetValue(state, out LegacyBlockEncoding cached))
            return cached;

        MinecraftBlockMapping? mapping = rules.Resolve(state, target);
        if(mapping is null || !state.Equals(mapping.Source))
            throw new InvalidDataException($"Downgrade rules returned an invalid source mapping for {state.CanonicalKey}.");
        bool validInvisibleEncoding = mapping.AllowsInvisibleTarget &&
                                      mapping.Target?.IsAir == true &&
                                      mapping.LegacyEncoding is { NumericId: 0, Metadata: 0 };
        if(mapping.Quality == MinecraftBlockMappingQuality.Blocked ||
           mapping.Target is null ||
           mapping.Target.IsAir && !validInvisibleEncoding ||
           mapping.LegacyEncoding is null ||
           mapping.LegacyEncoding.Value.NumericId == 0 && !validInvisibleEncoding)
        {
            throw new InvalidDataException(
                $"Non-air state {state.CanonicalKey} has no safe non-air 1.12.2 encoding " +
                $"(rule {mapping.RuleId}, quality {mapping.Quality}).");
        }
        cache.Add(state, mapping.LegacyEncoding.Value);
        return mapping.LegacyEncoding.Value;
    }

    private static void ValidateSection(
        MinecraftChunkAddress chunkAddress,
        NormalizedMinecraftSection section,
        ISet<int> sectionYs)
    {
        ArgumentNullException.ThrowIfNull(section);
        if(section.Coordinate.Dimension != chunkAddress.Dimension.Value ||
           section.Coordinate.X != chunkAddress.X ||
           section.Coordinate.Z != chunkAddress.Z)
        {
            throw new InvalidDataException($"Section {section.Coordinate} does not belong to chunk {chunkAddress}.");
        }
        if(section.Coordinate.Y is < 0 or > 15)
            throw new InvalidDataException($"Section Y={section.Coordinate.Y} is outside the 1.12.2 range 0..15.");
        if(!sectionYs.Add(section.Coordinate.Y))
            throw new InvalidDataException($"Chunk {chunkAddress} contains duplicate Section Y={section.Coordinate.Y}.");
        if(section.Palette.Count == 0 || section.PaletteIndices.Length != 4096)
            throw new InvalidDataException($"Section {section.Coordinate} has an invalid canonical palette layout.");
    }

    private static void ValidateBlockEntityPosition(
        MinecraftChunkAddress chunkAddress,
        NormalizedMinecraftBlockEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if(entity.Position.Y is < 0 or > 255)
            throw new InvalidDataException($"Block entity {entity.TypeId} lies outside target Y: {entity.Position}.");
        if(FloorDiv(entity.Position.X, 16) != chunkAddress.X || FloorDiv(entity.Position.Z, 16) != chunkAddress.Z)
            throw new InvalidDataException($"Block entity {entity.TypeId} at {entity.Position} does not belong to {chunkAddress}.");
    }

    private static byte[] BuildChunkNbt(
        MinecraftChunkAddress address,
        IReadOnlyList<LegacySectionPayload> sections,
        ReadOnlySpan<int> heightMap,
        IReadOnlyList<byte[]> blockEntities,
        bool completeLighting)
    {
        using MemoryStream stream = new(128 * 1024);
        LegacyNbtWriter writer = new(stream);
        writer.WriteRootCompoundStart();
        writer.WriteInt("DataVersion", 1343);
        writer.WriteCompoundStart("Level");
        writer.WriteInt("xPos", address.X);
        writer.WriteInt("zPos", address.Z);
        writer.WriteLong("LastUpdate", 0);
        writer.WriteLong("InhabitedTime", 0);
        writer.WriteByte("TerrainPopulated", 1);
        writer.WriteByte("LightPopulated", completeLighting ? (byte)1 : (byte)0);
        writer.WriteByte("V", 1);
        writer.WriteIntArray("HeightMap", heightMap);
        writer.WriteByteArray("Biomes", Enumerable.Repeat((byte)1, 256).ToArray());
        writer.WriteListStart("Sections", 10, sections.Count);
        foreach(LegacySectionPayload section in sections)
        {
            writer.WriteByte("Y", (byte)section.Y);
            writer.WriteByteArray("Blocks", section.Blocks);
            writer.WriteByteArray("Data", section.Data);
            if(section.Add is not null)
                writer.WriteByteArray("Add", section.Add);
            writer.WriteByteArray("BlockLight", section.BlockLight);
            writer.WriteByteArray("SkyLight", section.SkyLight);
            writer.WriteCompoundEnd();
        }
        writer.WriteListStart("Entities", 10, 0);
        writer.WriteListStart("TileEntities", 10, blockEntities.Count);
        foreach(byte[] blockEntity in blockEntities)
            writer.WriteRawCompoundListElement(blockEntity);
        writer.WriteListStart("TileTicks", 10, 0);
        writer.WriteCompoundEnd();
        writer.WriteCompoundEnd();
        return stream.ToArray();
    }

    private static byte[] CompressZlib(ReadOnlySpan<byte> nbt)
    {
        using MemoryStream compressed = new();
        using(ZLibStream zlib = new(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(nbt);
        return compressed.ToArray();
    }


    private static async Task<long> CopyAuxiliaryFilesAsync(
        IMinecraftAuxiliaryFileSource source,
        MinecraftAuxiliaryExportPolicy policy,
        string stagingDirectory,
        ICollection<MinecraftDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        long bytesCopied = 0;
        HashSet<string> destinations = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<MinecraftAuxiliaryFileKind, AuxiliarySkipSummary> skipped = new();
        bool reportedSessionLock = false;
        await foreach(MinecraftAuxiliaryFile file in source
                           .EnumerateAuxiliaryFilesAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = ValidateAuxiliaryPath(stagingDirectory, file.RelativePath, out string destinationPath);
            if(IsSessionLock(relativePath))
            {
                TrackSkippedAuxiliaryFile(skipped, MinecraftAuxiliaryFileKind.SessionLock, file.Length);
                if(!reportedSessionLock)
                {
                    diagnostics.Add(new MinecraftDiagnostic(
                        "export.auxiliary.session_lock_skipped",
                        MinecraftDiagnosticSeverity.Information,
                        "session.lock 不会复制到导出世界。"));
                    reportedSessionLock = true;
                }
                continue;
            }
            if(IsReservedGeneratedPath(relativePath))
                throw new InvalidDataException($"Auxiliary file conflicts with generated world data: {relativePath}");
            if(!ShouldCopy(relativePath, file.Kind, policy))
            {
                TrackSkippedAuxiliaryFile(skipped, file.Kind, file.Length);
                continue;
            }
            if(!destinations.Add(relativePath))
                throw new InvalidDataException($"Duplicate auxiliary destination: {relativePath}");
            if(File.Exists(destinationPath) || Directory.Exists(destinationPath))
                throw new InvalidDataException($"Auxiliary destination already exists: {relativePath}");

            string? parent = Path.GetDirectoryName(destinationPath);
            if(parent is null)
                throw new InvalidDataException($"Auxiliary file has no safe parent: {relativePath}");
            Directory.CreateDirectory(parent);
            await using Stream input = await source.OpenAuxiliaryFileAsync(file, cancellationToken).ConfigureAwait(false);
            await using FileStream output = new(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using IncrementalHash? hash = file.Fingerprint.ContentHash is null
                ? null
                : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[64 * 1024];
            long written = 0;
            while(true)
            {
                int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if(read == 0)
                    break;
                written = checked(written + read);
                if(written > file.Length)
                    throw new IOException($"Auxiliary file grew while copying: {relativePath}");
                hash?.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            if(written != file.Length)
                throw new IOException($"Auxiliary file length changed while copying: {relativePath}");
            if(hash is not null)
            {
                byte[] actual = hash.GetHashAndReset();
                byte[] expected;
                try
                {
                    expected = Convert.FromHexString(file.Fingerprint.ContentHash!);
                }
                catch(FormatException exception)
                {
                    throw new InvalidDataException($"Auxiliary fingerprint hash is invalid: {relativePath}", exception);
                }
                if(!CryptographicOperations.FixedTimeEquals(expected, actual))
                    throw new IOException($"Auxiliary file content changed while copying: {relativePath}");
            }
            bytesCopied = checked(bytesCopied + written);
        }
        AppendAuxiliarySkipDiagnostics(diagnostics, skipped);
        return bytesCopied;
    }

    private static string ValidateAuxiliaryPath(
        string stagingDirectory,
        string suppliedPath,
        out string destinationPath)
    {
        if(string.IsNullOrWhiteSpace(suppliedPath) || Path.IsPathRooted(suppliedPath))
            throw new InvalidDataException("Auxiliary path must be non-empty and relative.");
        string normalizedSeparators = suppliedPath.Replace('\\', '/');
        string[] segments = normalizedSeparators.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if(segments.Length == 0 || segments.Any(static segment => segment is "." or ".."))
            throw new InvalidDataException($"Auxiliary path traversal is not allowed: {suppliedPath}");

        destinationPath = MinecraftSourceFiles.ResolveRelativePath(stagingDirectory, normalizedSeparators);
        string safeRelative = MinecraftSourceFiles.NormalizeRelativePath(stagingDirectory, destinationPath);
        if(!string.Equals(safeRelative, string.Join('/', segments), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Auxiliary path is not canonical: {suppliedPath}");
        return safeRelative;
    }

    private static bool IsSessionLock(string relativePath) =>
        relativePath.Equals("session.lock", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(relativePath).Equals("session.lock", StringComparison.OrdinalIgnoreCase);

    private static bool IsReservedGeneratedPath(string relativePath)
    {
        if(relativePath.Equals("level.dat", StringComparison.OrdinalIgnoreCase))
            return true;
        string[] segments = relativePath.Split('/');
        return segments.Any(static segment => segment.Equals("region", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldCopy(
        string relativePath,
        MinecraftAuxiliaryFileKind kind,
        MinecraftAuxiliaryExportPolicy policy)
    {
        if(IsModernOnlyAuxiliaryPath(relativePath))
            return false;

        return kind switch
        {
            MinecraftAuxiliaryFileKind.WorldData => policy.PreserveWorldData,
            MinecraftAuxiliaryFileKind.PlayerData => policy.PreservePlayerData,
            MinecraftAuxiliaryFileKind.Advancements => policy.PreserveAdvancements,
            MinecraftAuxiliaryFileKind.Statistics => policy.PreserveStatistics,
            MinecraftAuxiliaryFileKind.EntityData => false,
            MinecraftAuxiliaryFileKind.PointOfInterestData => false,
            MinecraftAuxiliaryFileKind.DataPacks => false,
            MinecraftAuxiliaryFileKind.WorldIcon =>
                relativePath.Equals("icon.png", StringComparison.OrdinalIgnoreCase) && policy.PreserveWorldIcon,
            MinecraftAuxiliaryFileKind.EmbeddedResourcePack =>
                relativePath.Equals("resources.zip", StringComparison.OrdinalIgnoreCase) &&
                policy.PreserveEmbeddedResourcePack,
            MinecraftAuxiliaryFileKind.SessionLock => false,
            MinecraftAuxiliaryFileKind.Unknown => false,
            _ => false,
        };
    }

    private static bool IsModernOnlyAuxiliaryPath(string relativePath)
    {
        string[] directories = relativePath.Split('/').SkipLast(1).ToArray();
        return directories.Any(static directory =>
            directory.Equals("entities", StringComparison.OrdinalIgnoreCase) ||
            directory.Equals("poi", StringComparison.OrdinalIgnoreCase) ||
            directory.Equals("datapacks", StringComparison.OrdinalIgnoreCase));
    }

    private static void TrackSkippedAuxiliaryFile(
        IDictionary<MinecraftAuxiliaryFileKind, AuxiliarySkipSummary> skipped,
        MinecraftAuxiliaryFileKind kind,
        long length)
    {
        if(!skipped.TryGetValue(kind, out AuxiliarySkipSummary? summary))
        {
            summary = new AuxiliarySkipSummary();
            skipped.Add(kind, summary);
        }
        summary.Count = checked(summary.Count + 1);
        summary.Bytes = checked(summary.Bytes + Math.Max(0, length));
    }

    private static void AppendAuxiliarySkipDiagnostics(
        ICollection<MinecraftDiagnostic> diagnostics,
        IReadOnlyDictionary<MinecraftAuxiliaryFileKind, AuxiliarySkipSummary> skipped)
    {
        if(skipped.Count == 0)
            return;

        long totalCount = skipped.Values.Sum(static summary => summary.Count);
        long totalBytes = skipped.Values.Sum(static summary => summary.Bytes);
        string categories = string.Join("、", skipped
            .OrderBy(static item => item.Key)
            .Select(static item => $"{DescribeAuxiliaryKind(item.Key)} {item.Value.Count:N0} 个"));
        bool skippedWorldContent = skipped.Keys.Any(static kind =>
            kind is not MinecraftAuxiliaryFileKind.SessionLock and
                not MinecraftAuxiliaryFileKind.WorldIcon and
                not MinecraftAuxiliaryFileKind.EmbeddedResourcePack);
        diagnostics.Add(new MinecraftDiagnostic(
            "export.auxiliary.skipped.summary",
            skippedWorldContent ? MinecraftDiagnosticSeverity.Warning : MinecraftDiagnosticSeverity.Information,
            $"按 Java 1.12.2 跨版本安全策略跳过 {totalCount:N0} 个附属文件（{totalBytes:N0} 字节）：{categories}。"));
    }

    private static string DescribeAuxiliaryKind(MinecraftAuxiliaryFileKind kind) => kind switch
    {
        MinecraftAuxiliaryFileKind.WorldData => "世界 data/level.dat_old",
        MinecraftAuxiliaryFileKind.PlayerData => "玩家数据",
        MinecraftAuxiliaryFileKind.Advancements => "进度数据",
        MinecraftAuxiliaryFileKind.Statistics => "统计数据",
        MinecraftAuxiliaryFileKind.EntityData => "现代实体区域",
        MinecraftAuxiliaryFileKind.PointOfInterestData => "现代 POI 区域",
        MinecraftAuxiliaryFileKind.DataPacks => "数据包",
        MinecraftAuxiliaryFileKind.WorldIcon => "世界图标",
        MinecraftAuxiliaryFileKind.EmbeddedResourcePack => "内置资源包",
        MinecraftAuxiliaryFileKind.SessionLock => "session.lock",
        MinecraftAuxiliaryFileKind.Unknown => "未知文件",
        _ => "其他文件",
    };

    private static void ValidateRequest(MinecraftWorldExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Chunks);
        ArgumentNullException.ThrowIfNull(request.Target);
        ArgumentNullException.ThrowIfNull(request.SourceLevelMetadata);
        ArgumentNullException.ThrowIfNull(request.UnknownDataPolicy);
        if(string.IsNullOrWhiteSpace(request.DestinationDirectory))
            throw new ArgumentException("DestinationDirectory cannot be empty.", nameof(request));
        if(string.IsNullOrWhiteSpace(request.ExpectedSourceRevision))
            throw new ArgumentException("ExpectedSourceRevision cannot be empty.", nameof(request));
    }

    private static void ValidateTarget(MinecraftTargetProfile target)
    {
        if(!target.Id.Equals(MinecraftTargetProfile.Java1122.Id, StringComparison.Ordinal) ||
           target.Version.DataVersion != 1343 ||
           target.Version.StorageFamily != MinecraftStorageFamily.LegacyNumericAnvil ||
           !target.RequiresLegacyNumericEncoding ||
           target.BuildRange != new BlockYRange(0, 255))
        {
            throw new NotSupportedException("Minecraft1122WorldExporter only writes the Java 1.12.2 0..255 format.");
        }
    }

    private static void ValidateDimensions(IEnumerable<MinecraftDimensionId> dimensions)
    {
        foreach(MinecraftDimensionId dimension in dimensions.Distinct())
        {
            if(dimension != MinecraftDimensionId.Overworld &&
               dimension != MinecraftDimensionId.Nether &&
               dimension != MinecraftDimensionId.End)
            {
                throw new NotSupportedException($"Java 1.12.2 export cannot safely encode custom dimension {dimension}.");
            }
        }
    }

    private static BlockPosition TranslateSpawn(BlockPosition source, int offset, BlockYRange targetRange)
    {
        long translatedY = (long)source.Y + offset;
        if(translatedY < targetRange.Minimum || translatedY > targetRange.Maximum)
            throw new InvalidDataException($"Translated spawn Y={translatedY} lies outside {targetRange.Minimum}..{targetRange.Maximum}.");
        return source with { Y = (int)translatedY };
    }

    private static void ValidateOpaquePayloadHash(MinecraftOpaquePayload payload, string description)
    {
        if(payload.Bytes.IsEmpty)
            throw new InvalidDataException($"{description} is empty.");
        if(payload.ContentHash is null)
            return;
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(payload.ContentHash);
        }
        catch(FormatException exception)
        {
            throw new InvalidDataException($"{description} has an invalid content hash.", exception);
        }
        byte[] actual = SHA256.HashData(payload.Bytes.Span);
        if(!CryptographicOperations.FixedTimeEquals(expected, actual))
            throw new InvalidDataException($"{description} no longer matches its content hash.");
    }

    private static DestinationPlan PlanDestination(
        string requestedPath,
        MinecraftExportDestinationPolicy policy)
    {
        string requested = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedPath));
        string? parent = Path.GetDirectoryName(requested);
        string name = Path.GetFileName(requested);
        if(parent is null || name.Length == 0 ||
           string.Equals(requested, Path.GetPathRoot(requested), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A world export destination cannot be a filesystem root.");
        }
        if(File.Exists(requested))
            throw new IOException($"World export destination is a file: {requested}");
        bool exists = Directory.Exists(requested);
        if(exists && IsReparsePoint(requested))
            throw new IOException($"World export destination cannot be a reparse point: {requested}");

        return policy switch
        {
            MinecraftExportDestinationPolicy.FailIfExists when exists =>
                throw new IOException($"World export destination already exists: {requested}"),
            MinecraftExportDestinationPolicy.FailIfExists => new DestinationPlan(parent, requested, name, false),
            MinecraftExportDestinationPolicy.CreateUniqueSibling => PlanUniqueDestination(parent, requested, name),
            MinecraftExportDestinationPolicy.ReplaceWithTimestampedBackup =>
                new DestinationPlan(parent, requested, name, exists),
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        };
    }

    private static DestinationPlan PlanUniqueDestination(string parent, string requested, string name)
    {
        if(!Directory.Exists(requested) && !File.Exists(requested))
            return new DestinationPlan(parent, requested, name, false);
        for(int suffix = 1; suffix <= 10_000; suffix++)
        {
            string candidateName = $"{name} ({suffix})";
            string candidate = Path.Combine(parent, candidateName);
            if(!Directory.Exists(candidate) && !File.Exists(candidate))
                return new DestinationPlan(parent, candidate, candidateName, false);
        }
        throw new IOException($"Could not allocate a unique destination beside {requested}.");
    }

    private static string? CommitStagingDirectory(string stagingDirectory, DestinationPlan destination)
    {
        string? backup = null;
        if(destination.ReplaceExisting)
        {
            if(!Directory.Exists(destination.OutputDirectory))
                throw new IOException($"Destination disappeared before replacement: {destination.OutputDirectory}");
            if(IsReparsePoint(destination.OutputDirectory))
                throw new IOException($"Destination became a reparse point before replacement: {destination.OutputDirectory}");
            backup = AllocateBackupPath(destination.OutputDirectory);
            WindowsDirectoryMove.Move(destination.OutputDirectory, backup);
        }

        try
        {
            WindowsDirectoryMove.Move(stagingDirectory, destination.OutputDirectory);
            return backup;
        }
        catch(Exception commitFailure)
        {
            if(backup is not null && Directory.Exists(backup) &&
               !Directory.Exists(destination.OutputDirectory) && !File.Exists(destination.OutputDirectory))
            {
                try
                {
                    WindowsDirectoryMove.Move(backup, destination.OutputDirectory);
                }
                catch(Exception restoreFailure)
                {
                    throw new AggregateException(
                        $"Commit failed; the previous world remains recoverable at {backup}, but automatic restoration failed.",
                        commitFailure,
                        restoreFailure);
                }
            }
            throw;
        }
    }

    private static string AllocateBackupPath(string destination)
    {
        string stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        string candidate = $"{destination}.backup-{stamp}";
        for(int suffix = 0; suffix <= 10_000; suffix++)
        {
            string path = suffix == 0 ? candidate : $"{candidate}-{suffix}";
            if(!Directory.Exists(path) && !File.Exists(path))
                return path;
        }
        throw new IOException($"Could not allocate a backup path for {destination}.");
    }

    private static Exception? TryDeleteStagingDirectory(string stagingDirectory, string expectedParent)
    {
        try
        {
            if(!Directory.Exists(stagingDirectory))
                return null;
            string resolved = Path.GetFullPath(stagingDirectory);
            string? parent = Path.GetDirectoryName(resolved);
            string name = Path.GetFileName(resolved);
            if(!string.Equals(parent, Path.GetFullPath(expectedParent), StringComparison.OrdinalIgnoreCase) ||
               !name.Contains(".zjj-staging-", StringComparison.Ordinal))
            {
                return new InvalidOperationException($"Refused to recursively delete an unverified staging path: {resolved}");
            }
            Directory.Delete(resolved, recursive: true);
            return null;
        }
        catch(Exception exception)
        {
            return exception;
        }
    }

    private static string ResolveRegionDirectory(string root, MinecraftDimensionId dimension)
    {
        if(dimension == MinecraftDimensionId.Overworld)
            return Path.Combine(root, "region");
        if(dimension == MinecraftDimensionId.Nether)
            return Path.Combine(root, "DIM-1", "region");
        if(dimension == MinecraftDimensionId.End)
            return Path.Combine(root, "DIM1", "region");
        throw new NotSupportedException($"Unsupported 1.12.2 dimension {dimension}.");
    }

    private static void EnsureSourceRevision(INormalizedMinecraftChunkSource source, string expected)
    {
        if(!string.Equals(source.Revision, expected, StringComparison.Ordinal))
            throw new InvalidOperationException("Chunk source revision changed during export.");
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static int FloorDiv(int value, int divisor)
    {
        int quotient = value / divisor;
        int remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private static byte ReadNibble(ReadOnlySpan<byte> values, int index) => (byte)((values[index >> 1] >> ((index & 1) * 4)) & 15);

    private static void SetNibble(Span<byte> values, int index, byte value)
    {
        int packedIndex = index >> 1;
        if((index & 1) == 0)
            values[packedIndex] = (byte)(values[packedIndex] & 0xf0 | value & 0x0f);
        else
            values[packedIndex] = (byte)(values[packedIndex] & 0x0f | (value & 0x0f) << 4);
    }

    private static void Report(
        IProgress<MinecraftWorldExportProgress>? progress,
        MinecraftWorldExportStage stage,
        long chunksRead,
        long chunksWritten,
        long regionsWritten,
        long bytesWritten,
        MinecraftChunkAddress? currentChunk,
        string message) => progress?.Report(new MinecraftWorldExportProgress(
        stage,
        chunksRead,
        chunksWritten,
        regionsWritten,
        bytesWritten,
        currentChunk,
        message));

    private sealed record DestinationPlan(
        string ParentDirectory,
        string OutputDirectory,
        string OutputName,
        bool ReplaceExisting);

    private sealed record LegacySectionPayload(
        int Y,
        byte[] Blocks,
        byte[] Data,
        byte[]? Add,
        byte[] BlockLight,
        byte[] SkyLight,
        long NonAirBlockCount,
        bool HadCompleteLighting);

    private sealed record EncodedLegacyChunk(byte[] Nbt, long NonAirBlockCount);


    private sealed class AuxiliarySkipSummary
    {
        public long Count { get; set; }

        public long Bytes { get; set; }
    }

    private sealed class ExportDiagnosticAccumulator
    {
        private readonly ICollection<MinecraftDiagnostic> diagnostics;
        private long litChunks;
        private bool staticLightReported;
        private long reducedBlockEntities;
        private long regeneratedOpaqueChunks;
        private bool defaultBiomes;
        private bool entitiesUnavailable;

        public ExportDiagnosticAccumulator(ICollection<MinecraftDiagnostic> diagnostics)
        {
            this.diagnostics = diagnostics;
        }

        public void ReportLightingBaked(MinecraftChunkAddress chunk, bool hasStaticSources)
        {
            litChunks++;
            if(litChunks == 1)
            {
                diagnostics.Add(new MinecraftDiagnostic(
                    "export.lighting.baked",
                    MinecraftDiagnosticSeverity.Information,
                    "已按 1.12.2 注册表重建天光与方块光，并在区块边界传播光照；导出区块包含完整光照。",
                    chunk));
            }
            if(hasStaticSources && !staticLightReported)
            {
                staticLightReported = true;
                diagnostics.Add(new MinecraftDiagnostic(
                    "export.lighting.static_sources",
                    MinecraftDiagnosticSeverity.Warning,
                    "现代隐形光源已烘焙到旧版区块光照，没有增加可见灯块。1.12.2 不支持隐形光源；游戏中改动附近方块或强制重新光照后，这部分静态照明可能消退。",
                    chunk));
            }
        }

        public void ReportReducedBlockEntity(
            MinecraftChunkAddress chunk,
            string typeId,
            string? reason)
        {
            reducedBlockEntities++;
            diagnostics.Add(new MinecraftDiagnostic(
                "export.block_entity.reduced",
                MinecraftDiagnosticSeverity.Warning,
                $"方块实体 {typeId} 的原始 compound 无法安全坐标重写，仅保留 id/坐标：{reason}",
                chunk));
        }

        public void ReportArchivedReplacedEntity(MinecraftChunkAddress chunk, string type, ushort targetId, string file)
        {
            reducedBlockEntities++;
            diagnostics.Add(new MinecraftDiagnostic("export.block_entity.replacement_archived", MinecraftDiagnosticSeverity.Warning,
                $"{type} 替换为方块 {targetId}；原实体未写入世界，其完整原始数据已归档至 zjj-archived-block-entities/{file}。", chunk));
        }

        public void ReportBlockEntityCompatibility(MinecraftChunkAddress chunk, int count)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "export.block_entity.target_validated",
                MinecraftDiagnosticSeverity.Information,
                $"已校验 {count} 个方块实体与目标方块类型，并转码受支持的容器物品；未知扩展字段原样保留。木桶转换为箱子后遵循旧版箱子的相邻合并规则。",
                chunk));
        }

        public void ReportOpaqueRootRegenerated(MinecraftChunkAddress chunk)
        {
            regeneratedOpaqueChunks++;
            if(regeneratedOpaqueChunks == 1)
            {
                diagnostics.Add(new MinecraftDiagnostic(
                    "export.chunk.opaque_root_regenerated",
                    MinecraftDiagnosticSeverity.Warning,
                    "源 Chunk 根 NBT 已按 1.12.2 结构重建；除单独重写的方块实体外，未支持字段没有被伪装为无损合并。",
                    chunk,
                    "$"));
            }
        }

        public void ReportDefaultBiomes() => defaultBiomes = true;

        public void ReportEntitiesUnavailable() => entitiesUnavailable = true;

        public void Flush()
        {
            if(litChunks > 1)
            {
                diagnostics.Add(new MinecraftDiagnostic(
                    "export.lighting.baked.summary",
                    MinecraftDiagnosticSeverity.Information,
                    $"已完成 {litChunks:N0} 个 Chunk 的天光、方块光及边界光照烘焙。"));
            }
            if(reducedBlockEntities > 0)
            {
                diagnostics.Add(new MinecraftDiagnostic(
                    "export.block_entity.reduced.summary",
                    MinecraftDiagnosticSeverity.Warning,
                    $"共有 {reducedBlockEntities:N0} 个方块实体降级为基础 id/坐标 compound。"));
            }
            if(regeneratedOpaqueChunks > 1)
            {
                diagnostics.Add(new MinecraftDiagnostic(
                    "export.chunk.opaque_root_regenerated.summary",
                    MinecraftDiagnosticSeverity.Warning,
                    $"共有 {regeneratedOpaqueChunks:N0} 个 Chunk 的源根 NBT 在跨版本导出时被明确重建。"));
            }
            if(defaultBiomes)
            {
                diagnostics.Add(new MinecraftDiagnostic(
                    "export.biomes.defaulted",
                    MinecraftDiagnosticSeverity.Information,
                    "规范场景未携带旧版 256 列 Biomes，导出使用 plains(1)。"));
            }
            if(entitiesUnavailable)
            {
                diagnostics.Add(new MinecraftDiagnostic(
                    "export.entities.empty",
                    MinecraftDiagnosticSeverity.Warning,
                    "规范 Chunk 不包含实体事实层，Entities 已写为空列表，未声称保留源实体。"));
            }
        }
    }
}
