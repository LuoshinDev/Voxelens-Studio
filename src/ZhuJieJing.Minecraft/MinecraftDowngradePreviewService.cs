using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

/// <summary>
/// Scans canonical palettes once to produce a conversion report, then exposes a separate lazy chunk view.
/// The supplied rule provider stays version-agnostic and is never coupled to a concrete rule implementation.
/// </summary>
public sealed class MinecraftDowngradePreviewService : IMinecraftDowngradePreviewService
{
    private readonly IMinecraftBlockDowngradeRules rules;
    private readonly long cacheByteBudget;

    public MinecraftDowngradePreviewService(IMinecraftBlockDowngradeRules rules) : this(rules, 128L * 1024 * 1024) { }

    public MinecraftDowngradePreviewService(IMinecraftBlockDowngradeRules rules, long cacheByteBudget)
    {
        this.rules = rules ?? throw new ArgumentNullException(nameof(rules));
        if(cacheByteBudget < 0) throw new ArgumentOutOfRangeException(nameof(cacheByteBudget));
        this.cacheByteBudget = cacheByteBudget;
    }

    public async ValueTask<IMinecraftDowngradePreview> CreateAsync(
        MinecraftDowngradePreviewRequest request,
        IProgress<MinecraftConversionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Source);
        ArgumentNullException.ThrowIfNull(request.Target);
        ArgumentNullException.ThrowIfNull(request.Policy);
        cancellationToken.ThrowIfCancellationRequested();
        string sourceRevision = request.Source.Revision;
        if(string.IsNullOrWhiteSpace(sourceRevision))
            throw new InvalidDataException("Normalized source revision cannot be empty.");

        Dictionary<BlockState, long> stateCounts = new();
        Dictionary<int, long> occupiedByY = new();
        Dictionary<int, long> blockEntitiesByY = new();
        List<MinecraftDiagnostic> diagnostics = new();
        HashSet<(string Code, string Message, MinecraftChunkAddress? Chunk)> sourceBlockingDiagnostics = new();
        int? minimumOccupiedY = null;
        int? maximumOccupiedY = null;
        long chunksScanned = 0;
        long blocksScanned = 0;
        long lastProgressTimestamp = Stopwatch.GetTimestamp();
        object aggregationSync = new();

        void AnalyzeChunk(NormalizedMinecraftChunk chunk, MinecraftDimensionId dimension, long? chunksTotal)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateChunkAddress(chunk, dimension, request.Bounds);
            Dictionary<BlockState, long> chunkStateCounts = new();
            Dictionary<int, long> chunkOccupiedByY = new();
            Dictionary<int, long> chunkBlockEntitiesByY = new();
            List<MinecraftDiagnostic> chunkBlockingDiagnostics = [];
            int? chunkMinimumOccupiedY = null;
            int? chunkMaximumOccupiedY = null;
            long chunkBlocksScanned = 0;

            foreach(MinecraftDiagnostic diagnostic in chunk.Diagnostics)
            {
                if(diagnostic.Severity == MinecraftDiagnosticSeverity.Blocking)
                    chunkBlockingDiagnostics.Add(diagnostic);
            }

            foreach(NormalizedMinecraftSection section in chunk.Sections)
            {
                ValidateSection(chunk.Address, section);
                ReadOnlyMemory<ushort> indices = section.PaletteIndices;
                chunkBlocksScanned = checked(chunkBlocksScanned + indices.Length);
                for(int localIndex = 0; localIndex < indices.Length; localIndex++)
                {
                    if((localIndex & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                    BlockState state = GetStateAt(section, indices.Span[localIndex], localIndex);
                    if(state.IsAir) continue;

                    chunkStateCounts[state] = checked(chunkStateCounts.GetValueOrDefault(state) + 1);
                    int blockY = GetBlockY(section.Coordinate.Y, localIndex);
                    chunkOccupiedByY[blockY] = checked(chunkOccupiedByY.GetValueOrDefault(blockY) + 1);
                    chunkMinimumOccupiedY = chunkMinimumOccupiedY is null
                        ? blockY
                        : Math.Min(chunkMinimumOccupiedY.Value, blockY);
                    chunkMaximumOccupiedY = chunkMaximumOccupiedY is null
                        ? blockY
                        : Math.Max(chunkMaximumOccupiedY.Value, blockY);
                }
            }
            foreach(NormalizedMinecraftBlockEntity entity in chunk.BlockEntities)
            {
                chunkBlockEntitiesByY[entity.Position.Y] = checked(
                    chunkBlockEntitiesByY.GetValueOrDefault(entity.Position.Y) + 1);
            }

            long currentChunksScanned;
            long currentBlocksScanned;
            bool reportCurrent;
            lock(aggregationSync)
            {
                foreach((BlockState state, long count) in chunkStateCounts)
                    stateCounts[state] = checked(stateCounts.GetValueOrDefault(state) + count);
                foreach((int y, long count) in chunkOccupiedByY)
                    occupiedByY[y] = checked(occupiedByY.GetValueOrDefault(y) + count);
                foreach((int y, long count) in chunkBlockEntitiesByY)
                    blockEntitiesByY[y] = checked(blockEntitiesByY.GetValueOrDefault(y) + count);
                if(chunkMinimumOccupiedY is int chunkMinimum)
                {
                    minimumOccupiedY = minimumOccupiedY is null
                        ? chunkMinimum
                        : Math.Min(minimumOccupiedY.Value, chunkMinimum);
                    maximumOccupiedY = maximumOccupiedY is null
                        ? chunkMaximumOccupiedY
                        : Math.Max(maximumOccupiedY.Value, chunkMaximumOccupiedY!.Value);
                }
                foreach(MinecraftDiagnostic diagnostic in chunkBlockingDiagnostics)
                {
                    if(sourceBlockingDiagnostics.Add((diagnostic.Code, diagnostic.Message, diagnostic.Chunk)))
                        diagnostics.Add(diagnostic);
                }

                chunksScanned = checked(chunksScanned + 1);
                blocksScanned = checked(blocksScanned + chunkBlocksScanned);
                currentChunksScanned = chunksScanned;
                currentBlocksScanned = blocksScanned;
                long now = Stopwatch.GetTimestamp();
                reportCurrent = chunksTotal == currentChunksScanned ||
                                Stopwatch.GetElapsedTime(lastProgressTimestamp, now) >= TimeSpan.FromMilliseconds(100);
                if(reportCurrent) lastProgressTimestamp = now;
            }

            if(reportCurrent)
            {
                progress?.Report(new MinecraftConversionProgress(
                    currentChunksScanned,
                    chunksTotal,
                    currentBlocksScanned,
                    chunk.Address,
                    "正在并行统计方块与高度范围"));
            }
        }

        if(request.MaximumConcurrency is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(request), "MaximumConcurrency must be between 1 and 256.");
        if(request.ChunkAddresses is { } addresses)
        {
            HashSet<MinecraftChunkAddress> uniqueAddresses = [];
            HashSet<MinecraftDimensionId> dimensions = request.Source.Dimensions.ToHashSet();
            foreach(MinecraftChunkAddress address in addresses)
            {
                if(!uniqueAddresses.Add(address))
                    throw new ArgumentException($"Chunk address list contains a duplicate: {address}.", nameof(request));
                if(!dimensions.Contains(address.Dimension))
                    throw new ArgumentException($"Chunk address belongs to an unavailable dimension: {address}.", nameof(request));
                if(request.Bounds is not null && !request.Bounds.Value.Contains(address))
                    throw new ArgumentException($"Chunk address lies outside the requested bounds: {address}.", nameof(request));
            }

            ParallelOptions options = new()
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = request.MaximumConcurrency,
            };
            await Parallel.ForEachAsync(
                addresses,
                options,
                async (address, workerCancellation) =>
                {
                    NormalizedMinecraftChunk? chunk = await request.Source
                        .FindAsync(address, workerCancellation)
                        .ConfigureAwait(false);
                    if(chunk is null)
                        throw new InvalidDataException($"Chunk source did not return the indexed address {address}.");
                    if(chunk.Address != address)
                        throw new InvalidDataException($"Chunk source returned {chunk.Address} while finding {address}.");
                    AnalyzeChunk(chunk, address.Dimension, addresses.Count);
                }).ConfigureAwait(false);
        }
        else
        {
            foreach(MinecraftDimensionId dimension in request.Source.Dimensions.Distinct())
            {
                await foreach(NormalizedMinecraftChunk chunk in request.Source
                                   .EnumerateAsync(dimension, request.Bounds, cancellationToken)
                                   .ConfigureAwait(false))
                {
                    AnalyzeChunk(chunk, dimension, null);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if(!string.Equals(sourceRevision, request.Source.Revision, StringComparison.Ordinal))
            throw new InvalidOperationException("Source revision changed while the downgrade preview was being scanned.");
        BlockYRange? occupiedRange = minimumOccupiedY is null
            ? null
            : new BlockYRange(minimumOccupiedY.Value, maximumOccupiedY!.Value);
        MinecraftYTranslationLimits translation = MinecraftYTranslationLimits.Calculate(
            occupiedRange,
            request.Target.BuildRange);
        int appliedOffset = request.RequestedYOffset ?? translation.SuggestedOffset;
        long verticallyClipped = CountVerticallyClipped(
            occupiedByY,
            request.Target.BuildRange,
            appliedOffset);
        long verticallyClippedBlockEntities = CountVerticallyClipped(
            blockEntitiesByY,
            request.Target.BuildRange,
            appliedOffset);

        if(!request.Policy.AllowVerticalClipping)
        {
            if(!translation.CanFitWithoutClipping)
            {
                diagnostics.Add(new MinecraftDiagnostic(
                    "downgrade.height.cannot_fit",
                    MinecraftDiagnosticSeverity.Blocking,
                    $"占用高度 {FormatRange(occupiedRange)} 无法完整放入目标高度 " +
                    $"{FormatRange(request.Target.BuildRange)}。"));
            }
            else if(!translation.Allows(appliedOffset))
            {
                diagnostics.Add(new MinecraftDiagnostic(
                    "downgrade.height.offset_out_of_range",
                    MinecraftDiagnosticSeverity.Blocking,
                    $"Y 偏移 {appliedOffset} 不在无裁切合法范围 " +
                    $"{translation.MinimumOffset}..{translation.MaximumOffset}。"));
            }
        }
        else if(verticallyClipped > 0)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "downgrade.height.clipping_enabled",
                MinecraftDiagnosticSeverity.Warning,
                $"当前 Y 偏移会裁切 {verticallyClipped:N0} 个非空气方块。"));
        }
        if(verticallyClippedBlockEntities > 0)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "downgrade.block_entity.height_out_of_range",
                request.Policy.AllowVerticalClipping
                    ? MinecraftDiagnosticSeverity.Warning
                    : MinecraftDiagnosticSeverity.Blocking,
                $"当前 Y 偏移会使 {verticallyClippedBlockEntities:N0} 个方块实体超出目标高度。"));
        }

        if(!request.Policy.PreserveLighting)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "downgrade.lighting.discarded",
                MinecraftDiagnosticSeverity.Information,
                "派生预览不保留源区块光照；目标世界需要重新计算光照。"));
        }

        Dictionary<BlockState, MinecraftBlockMapping> mappings = new();
        List<MinecraftBlockMappingSummary> summaries = new(stateCounts.Count);
        long exactCount = 0;
        long similarCount = 0;
        long fallbackCount = 0;
        long blockedCount = 0;
        foreach((BlockState state, long count) in stateCounts.OrderBy(static pair => pair.Key.CanonicalKey, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            MinecraftBlockMapping mapping = NormalizeMapping(
                state,
                rules.Resolve(state, request.Target),
                request.Target,
                request.Policy.VisibleFallback,
                diagnostics);
            mappings.Add(state, mapping);
            summaries.Add(new MinecraftBlockMappingSummary(mapping, count));
            switch(mapping.Quality)
            {
                case MinecraftBlockMappingQuality.Exact:
                    exactCount = checked(exactCount + count);
                    break;
                case MinecraftBlockMappingQuality.Similar:
                    similarCount = checked(similarCount + count);
                    break;
                case MinecraftBlockMappingQuality.VisibleFallback:
                    fallbackCount = checked(fallbackCount + count);
                    break;
                case MinecraftBlockMappingQuality.Blocked:
                    blockedCount = checked(blockedCount + count);
                    break;
                default:
                    throw new InvalidDataException($"Unknown mapping quality {mapping.Quality}.");
            }
        }

        if(blockedCount > 0)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "downgrade.mapping.blocked",
                MinecraftDiagnosticSeverity.Blocking,
                $"{blockedCount:N0} 个方块没有可导出的目标映射；预览中使用醒目可见替代方块。"));
        }

        string derivedRevision = ComputeDerivedRevision(
            request,
            sourceRevision,
            appliedOffset,
            summaries);
        MinecraftDowngradePreviewSummary summary = new(
            sourceRevision,
            request.Target,
            translation,
            appliedOffset,
            request.Policy.AllowVerticalClipping,
            exactCount,
            similarCount,
            fallbackCount,
            blockedCount,
            verticallyClipped,
            summaries.ToArray(),
            diagnostics.ToArray());
        LazyConvertedMinecraftChunkSource converted = new(
            request.Source,
            request.Target,
            request.Policy,
            appliedOffset,
            request.Bounds,
            new ReadOnlyDictionary<BlockState, MinecraftBlockMapping>(mappings),
            sourceRevision,
            derivedRevision,
            cacheByteBudget);

        progress?.Report(new MinecraftConversionProgress(
            chunksScanned,
            chunksScanned,
            blocksScanned,
            null,
            "转换预览统计完成"));
        return new MinecraftDowngradePreview(summary, converted);
    }

    private static MinecraftBlockMapping NormalizeMapping(
        BlockState source,
        MinecraftBlockMapping? candidate,
        MinecraftTargetProfile target,
        BlockState visibleFallback,
        ICollection<MinecraftDiagnostic> diagnostics)
    {
        if(candidate is null)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "downgrade.mapping.null",
                MinecraftDiagnosticSeverity.Blocking,
                $"规则没有为 {source.CanonicalKey} 返回结果。"));
            return new MinecraftBlockMapping(
                source,
                null,
                null,
                MinecraftBlockMappingQuality.Blocked,
                "service:null-rule-result",
                $"预览使用 {visibleFallback.CanonicalKey}，导出被阻止。");
        }

        if(!source.Equals(candidate.Source))
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "downgrade.mapping.source_mismatch",
                MinecraftDiagnosticSeverity.Blocking,
                $"规则为 {source.CanonicalKey} 返回了其他源状态 {candidate.Source?.CanonicalKey ?? "null"}。"));
            return new MinecraftBlockMapping(
                source,
                null,
                null,
                MinecraftBlockMappingQuality.Blocked,
                "service:source-mismatch",
                $"预览使用 {visibleFallback.CanonicalKey}，导出被阻止。");
        }

        if(candidate.Quality == MinecraftBlockMappingQuality.Blocked || candidate.Target is null)
        {
            return candidate with
            {
                Source = source,
                Target = null,
                LegacyEncoding = null,
                Quality = MinecraftBlockMappingQuality.Blocked,
            };
        }

        if(candidate.Target.IsAir &&
           (!candidate.AllowsInvisibleTarget ||
            candidate.LegacyEncoding is not { NumericId: 0, Metadata: 0 }))
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "downgrade.mapping.air_rejected",
                MinecraftDiagnosticSeverity.Blocking,
                $"规则试图把非空气方块 {source.CanonicalKey} 映射为空气，已拒绝。"));
            return new MinecraftBlockMapping(
                source,
                null,
                null,
                MinecraftBlockMappingQuality.Blocked,
                "service:air-target-rejected",
                $"预览使用 {visibleFallback.CanonicalKey}，导出被阻止。");
        }

        bool validInvisibleEncoding = candidate.AllowsInvisibleTarget &&
                                      candidate.Target.IsAir &&
                                      candidate.LegacyEncoding is { NumericId: 0, Metadata: 0 };
        if(target.RequiresLegacyNumericEncoding &&
           (candidate.LegacyEncoding is null ||
            candidate.LegacyEncoding.Value.NumericId == 0 && !validInvisibleEncoding))
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "downgrade.mapping.legacy_encoding_missing",
                MinecraftDiagnosticSeverity.Blocking,
                $"{source.CanonicalKey} 的目标映射缺少非空气旧版 ID:data 编码。"));
            return new MinecraftBlockMapping(
                source,
                null,
                null,
                MinecraftBlockMappingQuality.Blocked,
                "service:legacy-encoding-missing",
                $"预览使用 {visibleFallback.CanonicalKey}，导出被阻止。");
        }

        return candidate;
    }

    internal static void ValidateChunkAddress(
        NormalizedMinecraftChunk chunk,
        MinecraftDimensionId requestedDimension,
        MinecraftChunkBounds? bounds)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if(chunk.Address.Dimension != requestedDimension)
            throw new InvalidDataException($"Chunk source returned {chunk.Address.Dimension} while enumerating {requestedDimension}.");
        if(bounds is not null && !bounds.Value.Contains(chunk.Address))
            throw new InvalidDataException($"Chunk source returned {chunk.Address} outside the requested bounds.");
    }

    internal static void ValidateSection(MinecraftChunkAddress chunkAddress, NormalizedMinecraftSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if(section.Coordinate.Dimension != chunkAddress.Dimension.Value ||
           section.Coordinate.X != chunkAddress.X ||
           section.Coordinate.Z != chunkAddress.Z)
        {
            throw new InvalidDataException($"Section {section.Coordinate} does not belong to chunk {chunkAddress}.");
        }
        if(section.Palette.Count == 0)
            throw new InvalidDataException($"Section {section.Coordinate} has an empty palette.");
        if(section.PaletteIndices.Length != 4096)
            throw new InvalidDataException($"Section {section.Coordinate} must contain exactly 4096 palette indices.");
    }

    internal static BlockState GetStateAt(
        NormalizedMinecraftSection section,
        ushort paletteIndex,
        int localIndex)
    {
        if(paletteIndex >= section.Palette.Count)
        {
            throw new InvalidDataException(
                $"Section {section.Coordinate} index {localIndex} references palette[{paletteIndex}], " +
                $"but the palette has {section.Palette.Count} entries.");
        }
        return section.Palette[paletteIndex];
    }

    internal static int GetBlockY(int sectionY, int localIndex)
    {
        long blockY = (long)sectionY * 16 + (localIndex >> 8);
        if(blockY < int.MinValue || blockY > int.MaxValue)
            throw new InvalidDataException($"Section Y={sectionY} produces a block coordinate outside Int32.");
        return (int)blockY;
    }

    private static long CountVerticallyClipped(
        IReadOnlyDictionary<int, long> occupiedByY,
        BlockYRange target,
        int offset)
    {
        long clipped = 0;
        foreach((int sourceY, long count) in occupiedByY)
        {
            long targetY = (long)sourceY + offset;
            if(targetY < target.Minimum || targetY > target.Maximum)
                clipped = checked(clipped + count);
        }
        return clipped;
    }

    private static string ComputeDerivedRevision(
        MinecraftDowngradePreviewRequest request,
        string sourceRevision,
        int appliedOffset,
        IReadOnlyList<MinecraftBlockMappingSummary> mappings)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendString(hash, "zhujiejing.downgrade-preview/1");
        AppendString(hash, sourceRevision);
        AppendString(hash, request.Target.Id);
        AppendInt32(hash, request.Target.Version.DataVersion ?? -1);
        AppendString(hash, request.Target.Version.VersionName ?? string.Empty);
        AppendInt32(hash, request.Target.BuildRange.Minimum);
        AppendInt32(hash, request.Target.BuildRange.Maximum);
        AppendInt32(hash, appliedOffset);
        AppendInt32(hash, request.Policy.AllowVerticalClipping ? 1 : 0);
        AppendInt32(hash, request.Policy.PreserveLighting ? 1 : 0);
        AppendString(hash, request.Policy.VisibleFallback.CanonicalKey);
        if(request.Bounds is null)
        {
            AppendInt32(hash, 0);
        }
        else
        {
            AppendInt32(hash, 1);
            AppendInt32(hash, request.Bounds.Value.MinimumX);
            AppendInt32(hash, request.Bounds.Value.MinimumZ);
            AppendInt32(hash, request.Bounds.Value.MaximumX);
            AppendInt32(hash, request.Bounds.Value.MaximumZ);
        }
        foreach(MinecraftBlockMappingSummary summary in mappings)
        {
            MinecraftBlockMapping mapping = summary.Mapping;
            AppendString(hash, mapping.Source.CanonicalKey);
            AppendString(hash, mapping.Target?.CanonicalKey ?? string.Empty);
            AppendInt32(hash, (int)mapping.Quality);
            AppendString(hash, mapping.RuleId);
            AppendString(hash, mapping.Explanation);
            AppendInt32(hash, mapping.AllowsInvisibleTarget ? 1 : 0);
            AppendInt64(hash, summary.BlockCount);
            AppendInt32(hash, mapping.LegacyEncoding?.NumericId ?? -1);
            AppendInt32(hash, mapping.LegacyEncoding?.Metadata ?? -1);
        }
        return $"downgrade-preview/1:{Convert.ToHexString(hash.GetHashAndReset())}";
    }

    private static string FormatRange(BlockYRange? range) =>
        range is null ? "空" : $"{range.Value.Minimum}..{range.Value.Maximum}";

    private static string FormatRange(BlockYRange range) => $"{range.Minimum}..{range.Maximum}";

    private static void AppendString(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }
}

internal sealed class MinecraftDowngradePreview : IMinecraftDowngradePreview
{
    private readonly LazyConvertedMinecraftChunkSource converted;
    private bool disposed;

    public MinecraftDowngradePreview(
        MinecraftDowngradePreviewSummary summary,
        LazyConvertedMinecraftChunkSource converted)
    {
        Summary = summary;
        this.converted = converted;
    }

    public MinecraftDowngradePreviewSummary Summary { get; }

    public INormalizedMinecraftChunkSource ConvertedChunks
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return converted;
        }
    }

    public ValueTask DisposeAsync()
    {
        if(!disposed)
        {
            disposed = true;
            converted.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}

internal sealed class LazyConvertedMinecraftChunkSource : INormalizedMinecraftChunkSource, IMinecraftChunkCacheMetrics, IUncachedNormalizedMinecraftChunkSource
{
    private readonly INormalizedMinecraftChunkSource source;
    private readonly MinecraftTargetProfile target;
    private readonly MinecraftDowngradePolicy policy;
    private readonly int yOffset;
    private readonly MinecraftChunkBounds? previewBounds;
    private readonly IReadOnlyDictionary<BlockState, MinecraftBlockMapping> mappings;
    private readonly string sourceRevision;
    private readonly ConcurrentDictionary<MinecraftChunkAddress, Lazy<Task<NormalizedMinecraftChunk?>>> pendingReads = [];
    private readonly MinecraftChunkMemoryCache convertedChunkCache;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private int disposed;

    public LazyConvertedMinecraftChunkSource(
        INormalizedMinecraftChunkSource source,
        MinecraftTargetProfile target,
        MinecraftDowngradePolicy policy,
        int yOffset,
        MinecraftChunkBounds? previewBounds,
        IReadOnlyDictionary<BlockState, MinecraftBlockMapping> mappings,
        string sourceRevision,
        string revision,
        long cacheByteBudget)
    {
        this.source = source;
        this.target = target;
        this.policy = policy;
        this.yOffset = yOffset;
        this.previewBounds = previewBounds;
        this.mappings = mappings;
        this.sourceRevision = sourceRevision;
        Revision = revision;
        convertedChunkCache = new MinecraftChunkMemoryCache(cacheByteBudget);
        Dimensions = source.Dimensions.Distinct().ToArray();
    }

    public string Revision { get; }

    public IReadOnlyList<MinecraftDimensionId> Dimensions { get; }

    public MinecraftChunkCacheStatistics CacheStatistics => convertedChunkCache.Statistics;

    public async ValueTask<NormalizedMinecraftChunk?> FindAsync(
        MinecraftChunkAddress address,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable(cancellationToken);
        if(!Dimensions.Contains(address.Dimension) ||
           previewBounds is not null && !previewBounds.Value.Contains(address))
            return null;
        if(convertedChunkCache.TryGet(address, out NormalizedMinecraftChunk? completed)) return completed;
        Lazy<Task<NormalizedMinecraftChunk?>> cached = pendingReads.GetOrAdd(
            address,
            key => new Lazy<Task<NormalizedMinecraftChunk?>>(
                () => LoadConvertAndCacheAsync(key, lifetimeCancellation.Token),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await cached.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if(cached.IsValueCreated && cached.Value.IsCompleted)
                pendingReads.TryRemove(new KeyValuePair<MinecraftChunkAddress, Lazy<Task<NormalizedMinecraftChunk?>>>(
                    address,
                    cached));
        }
    }

    public async IAsyncEnumerable<NormalizedMinecraftChunk> EnumerateAsync(
        MinecraftDimensionId dimension,
        MinecraftChunkBounds? bounds = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable(cancellationToken);
        if(!Dimensions.Contains(dimension))
            yield break;
        MinecraftChunkBounds? effectiveBounds = Intersect(previewBounds, bounds);
        if(previewBounds is not null && bounds is not null && effectiveBounds is null)
            yield break;

        await foreach(NormalizedMinecraftChunk chunk in source
                          .EnumerateAsync(dimension, effectiveBounds, cancellationToken)
                          .ConfigureAwait(false))
        {
            ThrowIfUnavailable(cancellationToken);
            MinecraftDowngradePreviewService.ValidateChunkAddress(chunk, dimension, effectiveBounds);
            // Full-world consumers must not populate the interactive cache as they stream through the map.
            yield return ConvertChunk(chunk, cancellationToken);
        }
    }

    public async IAsyncEnumerable<NormalizedMinecraftChunk> EnumerateRegionAsync(
        MinecraftRegionAddress region,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable(cancellationToken);
        if(!Dimensions.Contains(region.Dimension))
            yield break;
        MinecraftChunkBounds regionBounds = RegionBounds(region);
        if(previewBounds is not null && Intersect(previewBounds, regionBounds) is null)
            yield break;
        await foreach(NormalizedMinecraftChunk chunk in source
                          .EnumerateRegionAsync(region, cancellationToken)
                          .ConfigureAwait(false))
        {
            ThrowIfUnavailable(cancellationToken);
            if(MinecraftRegionAddress.FromChunk(chunk.Address) != region)
                throw new InvalidDataException($"Chunk source returned {chunk.Address} outside requested region {region}.");
            if(previewBounds is null || previewBounds.Value.Contains(chunk.Address))
                yield return ConvertChunk(chunk, cancellationToken);
        }
    }

    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetimeCancellation.Cancel();
        convertedChunkCache.Clear();
        pendingReads.Clear();
        lifetimeCancellation.Dispose();
    }

    public async ValueTask<NormalizedMinecraftChunk?> FindUncachedAsync(MinecraftChunkAddress address, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable(cancellationToken);
        if(!Dimensions.Contains(address.Dimension) || previewBounds is not null && !previewBounds.Value.Contains(address)) return null;
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeCancellation.Token);
        NormalizedMinecraftChunk? chunk = await source.FindAsync(address, linked.Token).ConfigureAwait(false);
        ThrowIfUnavailable(linked.Token);
        if(chunk is not null && chunk.Address != address)
            throw new InvalidDataException($"Chunk source returned {chunk.Address} while finding {address}.");
        return chunk is null ? null : ConvertChunk(chunk, linked.Token);
    }

    private async Task<NormalizedMinecraftChunk?> LoadConvertAndCacheAsync(
        MinecraftChunkAddress address,
        CancellationToken cancellationToken)
    {
        try
        {
            NormalizedMinecraftChunk? chunk = await source.FindAsync(address, cancellationToken).ConfigureAwait(false);
            ThrowIfUnavailable(cancellationToken);
            if(chunk is not null && chunk.Address != address)
                throw new InvalidDataException($"Chunk source returned {chunk.Address} while finding {address}.");
            NormalizedMinecraftChunk? converted = chunk is null ? null : ConvertChunk(chunk, cancellationToken);
            ThrowIfUnavailable(cancellationToken);
            convertedChunkCache.Add(address, converted);
            return converted;
        }
        finally
        {
            // A cancelled waiter need not come back to release a completed shared read.
            pendingReads.TryRemove(address, out _);
        }
    }

    private NormalizedMinecraftChunk ConvertChunk(
        NormalizedMinecraftChunk sourceChunk,
        CancellationToken cancellationToken)
    {
        if(sourceChunk.Address.Dimension.Value.Length == 0)
            throw new InvalidDataException("Source chunk has an empty dimension identifier.");

        Dictionary<int, ConvertedSectionBuilder> targetSections = new();
        long chunkClipped = 0;
        long blockedFallbacks = 0;
        bool discardedLighting = false;
        foreach(NormalizedMinecraftSection sourceSection in sourceChunk.Sections)
        {
            MinecraftDowngradePreviewService.ValidateSection(sourceChunk.Address, sourceSection);
            ValidateLighting(sourceSection);
            ReadOnlySpan<ushort> sourceIndices = sourceSection.PaletteIndices.Span;
            for(int sourceLocalIndex = 0; sourceLocalIndex < sourceIndices.Length; sourceLocalIndex++)
            {
                if((sourceLocalIndex & 255) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                int sourceBlockY = MinecraftDowngradePreviewService.GetBlockY(
                    sourceSection.Coordinate.Y,
                    sourceLocalIndex);
                long translatedY = (long)sourceBlockY + yOffset;
                BlockState sourceState = MinecraftDowngradePreviewService.GetStateAt(
                    sourceSection,
                    sourceIndices[sourceLocalIndex],
                    sourceLocalIndex);
                bool inTargetRange = translatedY >= target.BuildRange.Minimum &&
                                     translatedY <= target.BuildRange.Maximum;
                if(!inTargetRange)
                {
                    if(!sourceState.IsAir)
                    {
                        if(!policy.AllowVerticalClipping)
                        {
                            throw new InvalidOperationException(
                                $"Chunk {sourceChunk.Address} contains {sourceState.CanonicalKey} at Y={sourceBlockY}; " +
                                $"offset {yOffset} leaves the target range and clipping is disabled.");
                        }
                        chunkClipped = checked(chunkClipped + 1);
                    }
                    continue;
                }

                int targetY = (int)translatedY;
                int targetSectionY = FloorDiv(targetY, 16);
                int targetLocalY = FloorMod(targetY, 16);
                int targetLocalIndex = targetLocalY << 8 | sourceLocalIndex & 255;
                ConvertedSectionBuilder? builder = null;
                if(policy.PreserveLighting && HasLighting(sourceSection.Lighting))
                {
                    builder = GetBuilder(targetSections, targetSectionY, sourceChunk.Address);
                    builder.CopyLighting(sourceSection.Lighting!, sourceLocalIndex, targetLocalIndex);
                }
                else if(!policy.PreserveLighting && sourceSection.Lighting is not null)
                {
                    discardedLighting = true;
                }

                if(sourceState.IsAir)
                    continue;
                if(!mappings.TryGetValue(sourceState, out MinecraftBlockMapping? mapping))
                {
                    throw new InvalidDataException(
                        $"Source revision {sourceRevision} yielded an unscanned state {sourceState.CanonicalKey}; " +
                        "the lazy preview is no longer consistent with its report.");
                }
                BlockState targetState;
                if(mapping.Quality == MinecraftBlockMappingQuality.Blocked || mapping.Target is null)
                {
                    targetState = policy.VisibleFallback;
                    blockedFallbacks = checked(blockedFallbacks + 1);
                }
                else
                {
                    targetState = mapping.Target;
                }
                if(targetState.IsAir)
                {
                    if(mapping.AllowsInvisibleTarget &&
                       mapping.LegacyEncoding is { NumericId: 0, Metadata: 0 })
                    {
                        if(sourceState.Name == "minecraft:light")
                        {
                            builder ??= GetBuilder(targetSections, targetSectionY, sourceChunk.Address);
                            int level = sourceState.Properties.TryGetValue("level", out string? value) && int.TryParse(value, out int parsed)
                                ? Math.Clamp(parsed, 0, 15) : 15;
                            builder.SetStaticLight(targetLocalIndex, (byte)level);
                        }
                        continue;
                    }
                    throw new InvalidDataException($"Non-air source {sourceState.CanonicalKey} resolved to air during conversion.");
                }

                builder ??= GetBuilder(targetSections, targetSectionY, sourceChunk.Address);
                builder.SetBlock(targetLocalIndex, targetState);
            }
        }

        List<MinecraftDiagnostic> diagnostics = new(sourceChunk.Diagnostics)
        {
            new MinecraftDiagnostic(
                "downgrade.chunk.derived",
                MinecraftDiagnosticSeverity.Information,
                $"区块由源 revision {sourceRevision} 派生为 {Revision}，Y 偏移为 {yOffset}。",
                sourceChunk.Address),
        };
        if(chunkClipped > 0)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "downgrade.chunk.vertically_clipped",
                MinecraftDiagnosticSeverity.Warning,
                $"此区块裁切了 {chunkClipped:N0} 个非空气方块。",
                sourceChunk.Address));
        }
        if(blockedFallbacks > 0)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "downgrade.chunk.blocked_fallback",
                MinecraftDiagnosticSeverity.Warning,
                $"此区块有 {blockedFallbacks:N0} 个被阻止的映射，预览使用可见替代方块。",
                sourceChunk.Address));
        }
        if(discardedLighting)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "downgrade.chunk.lighting_discarded",
                MinecraftDiagnosticSeverity.Information,
                "此派生区块未保留源光照。",
                sourceChunk.Address));
        }

        List<NormalizedMinecraftBlockEntity> blockEntities = ConvertBlockEntities(
            sourceChunk,
            diagnostics);
        NormalizedMinecraftSection[] sections = targetSections.Values
            .OrderBy(static builder => builder.SectionY)
            .Select(static builder => builder.Build())
            .ToArray();
        string currentHash = ComputeDerivedChunkHash(sourceChunk.Address, sections, blockEntities);
        string sourceCanonicalHash = sourceChunk.Provenance.CurrentCanonicalHash;
        if(string.Equals(sourceCanonicalHash, currentHash, StringComparison.Ordinal))
            currentHash = $"DERIVED-{currentHash}";
        MinecraftChunkProvenance provenance = new(
            sourceChunk.Provenance.SourcePayloadHash,
            sourceCanonicalHash,
            currentHash);
        MinecraftChunkPassthrough passthrough = new(
            sourceChunk.Passthrough.OriginalCompression,
            sourceChunk.Passthrough.OriginalStorageKind,
            sourceChunk.Passthrough.OriginalStoredPayload,
            sourceChunk.Passthrough.UnknownFragments,
            SupportsStructuredMerge: false);
        return new NormalizedMinecraftChunk(
            sourceChunk.Address,
            target.Version,
            sections,
            blockEntities,
            provenance,
            passthrough,
            diagnostics.ToArray());
    }

    private List<NormalizedMinecraftBlockEntity> ConvertBlockEntities(
        NormalizedMinecraftChunk sourceChunk,
        ICollection<MinecraftDiagnostic> diagnostics)
    {
        List<NormalizedMinecraftBlockEntity> result = new(sourceChunk.BlockEntities.Count);
        int clipped = 0;
        foreach(NormalizedMinecraftBlockEntity entity in sourceChunk.BlockEntities)
        {
            long translatedY = (long)entity.Position.Y + yOffset;
            if(translatedY < target.BuildRange.Minimum || translatedY > target.BuildRange.Maximum)
            {
                if(!policy.AllowVerticalClipping)
                {
                    throw new InvalidOperationException(
                        $"Block entity {entity.TypeId} at Y={entity.Position.Y} leaves the target range " +
                        $"after offset {yOffset}, and clipping is disabled.");
                }
                clipped++;
                continue;
            }

            result.Add(entity with
            {
                Position = entity.Position with { Y = (int)translatedY },
                SourceDataVersion = entity.SourceDataVersion ?? sourceChunk.SourceVersion.DataVersion,
            });
        }
        if(clipped > 0)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "downgrade.chunk.block_entities_clipped",
                MinecraftDiagnosticSeverity.Warning,
                $"此区块裁切了 {clipped:N0} 个超出目标高度的方块实体。",
                sourceChunk.Address));
        }
        return result;
    }

    private static ConvertedSectionBuilder GetBuilder(
        IDictionary<int, ConvertedSectionBuilder> sections,
        int sectionY,
        MinecraftChunkAddress chunkAddress)
    {
        if(!sections.TryGetValue(sectionY, out ConvertedSectionBuilder? builder))
        {
            builder = new ConvertedSectionBuilder(sectionY, chunkAddress);
            sections.Add(sectionY, builder);
        }
        return builder;
    }

    private static bool HasLighting(MinecraftSectionLighting? lighting) =>
        lighting is not null &&
        (lighting.SkyLightNibbles.Length == 2048 || lighting.BlockLightNibbles.Length == 2048 ||
         lighting.StaticLightSourceNibbles.Length == 2048);

    private static void ValidateLighting(NormalizedMinecraftSection section)
    {
        if(section.Lighting is null)
            return;
        if(section.Lighting.SkyLightNibbles.Length is not 0 and not 2048)
            throw new InvalidDataException($"Section {section.Coordinate} has malformed SkyLight length.");
        if(section.Lighting.BlockLightNibbles.Length is not 0 and not 2048)
            throw new InvalidDataException($"Section {section.Coordinate} has malformed BlockLight length.");
        if(section.Lighting.StaticLightSourceNibbles.Length is not 0 and not 2048)
            throw new InvalidDataException($"Section {section.Coordinate} has malformed static light source length.");
    }

    private static MinecraftChunkBounds? Intersect(
        MinecraftChunkBounds? first,
        MinecraftChunkBounds? second)
    {
        if(first is null)
            return second;
        if(second is null)
            return first;
        int minimumX = Math.Max(first.Value.MinimumX, second.Value.MinimumX);
        int minimumZ = Math.Max(first.Value.MinimumZ, second.Value.MinimumZ);
        int maximumX = Math.Min(first.Value.MaximumX, second.Value.MaximumX);
        int maximumZ = Math.Min(first.Value.MaximumZ, second.Value.MaximumZ);
        return minimumX > maximumX || minimumZ > maximumZ
            ? null
            : new MinecraftChunkBounds(minimumX, minimumZ, maximumX, maximumZ);
    }

    private static MinecraftChunkBounds RegionBounds(MinecraftRegionAddress region)
    {
        long minimumX = (long)region.X * MinecraftRegionAddress.ChunksPerAxis;
        long minimumZ = (long)region.Z * MinecraftRegionAddress.ChunksPerAxis;
        long maximumX = minimumX + MinecraftRegionAddress.ChunksPerAxis - 1;
        long maximumZ = minimumZ + MinecraftRegionAddress.ChunksPerAxis - 1;
        if(minimumX < int.MinValue || maximumX > int.MaxValue ||
           minimumZ < int.MinValue || maximumZ > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "Region lies outside the supported chunk coordinate range.");
        }
        return new MinecraftChunkBounds((int)minimumX, (int)minimumZ, (int)maximumX, (int)maximumZ);
    }

    private static int FloorDiv(int value, int divisor)
    {
        int quotient = value / divisor;
        int remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private static int FloorMod(int value, int divisor)
    {
        int remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }

    private static string ComputeDerivedChunkHash(
        MinecraftChunkAddress address,
        IReadOnlyList<NormalizedMinecraftSection> sections,
        IReadOnlyList<NormalizedMinecraftBlockEntity> blockEntities)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendString(hash, "zhujiejing.downgraded-chunk/1");
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
            foreach(ushort index in section.PaletteIndices.Span)
                AppendInt32(hash, index);
            if(section.Lighting is null)
            {
                AppendInt32(hash, -1);
            }
            else
            {
                AppendBytes(hash, section.Lighting.SkyLightNibbles.Span);
                AppendBytes(hash, section.Lighting.BlockLightNibbles.Span);
                AppendBytes(hash, section.Lighting.StaticLightSourceNibbles.Span);
            }
        }
        AppendInt32(hash, blockEntities.Count);
        foreach(NormalizedMinecraftBlockEntity entity in blockEntities)
        {
            AppendString(hash, entity.TypeId);
            AppendInt32(hash, entity.Position.X);
            AppendInt32(hash, entity.Position.Y);
            AppendInt32(hash, entity.Position.Z);
            AppendInt32(hash, entity.SourceDataVersion ?? -1);
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

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendBytes(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        AppendInt32(hash, value.Length);
        hash.AppendData(value);
    }

    private void ThrowIfUnavailable(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if(!string.Equals(source.Revision, sourceRevision, StringComparison.Ordinal))
            throw new InvalidOperationException("Source revision changed after the downgrade preview was created.");
    }

    private sealed class ConvertedSectionBuilder
    {
        private readonly MinecraftChunkAddress chunkAddress;
        private readonly List<BlockState> palette = [BlockState.Air];
        private readonly Dictionary<BlockState, ushort> paletteLookup = new() { [BlockState.Air] = 0 };
        private readonly ushort[] indices = new ushort[4096];
        private byte[]? skyLight;
        private byte[]? blockLight;
        private byte[]? staticLight;

        public ConvertedSectionBuilder(int sectionY, MinecraftChunkAddress chunkAddress)
        {
            SectionY = sectionY;
            this.chunkAddress = chunkAddress;
        }

        public int SectionY { get; }

        public void SetBlock(int localIndex, BlockState state)
        {
            if(indices[localIndex] != 0)
                throw new InvalidDataException($"Two source blocks mapped to target local index {localIndex}.");
            if(!paletteLookup.TryGetValue(state, out ushort paletteIndex))
            {
                paletteIndex = checked((ushort)palette.Count);
                palette.Add(state);
                paletteLookup.Add(state, paletteIndex);
            }
            indices[localIndex] = paletteIndex;
        }

        public void CopyLighting(
            MinecraftSectionLighting source,
            int sourceLocalIndex,
            int targetLocalIndex)
        {
            if(source.SkyLightNibbles.Length == 2048)
            {
                skyLight ??= new byte[2048];
                WriteNibble(skyLight, targetLocalIndex, ReadNibble(source.SkyLightNibbles.Span, sourceLocalIndex));
            }
            if(source.BlockLightNibbles.Length == 2048)
            {
                blockLight ??= new byte[2048];
                WriteNibble(blockLight, targetLocalIndex, ReadNibble(source.BlockLightNibbles.Span, sourceLocalIndex));
            }
            if(source.StaticLightSourceNibbles.Length == 2048)
                SetStaticLight(targetLocalIndex, ReadNibble(source.StaticLightSourceNibbles.Span, sourceLocalIndex));
        }

        public void SetStaticLight(int localIndex, byte level)
        {
            if(level == 0) return;
            staticLight ??= new byte[2048];
            WriteNibble(staticLight, localIndex, level);
            blockLight ??= new byte[2048];
            WriteNibble(blockLight, localIndex, Math.Max(level, ReadNibble(blockLight, localIndex)));
        }

        public NormalizedMinecraftSection Build()
        {
            MinecraftSectionLighting? lighting = skyLight is null && blockLight is null && staticLight is null
                ? null
                : new MinecraftSectionLighting(skyLight ?? Array.Empty<byte>(), blockLight ?? Array.Empty<byte>(), staticLight ?? Array.Empty<byte>());
            return new NormalizedMinecraftSection(
                new SectionCoordinate(chunkAddress.Dimension.Value, chunkAddress.X, SectionY, chunkAddress.Z),
                palette.ToArray(),
                indices,
                lighting,
                Array.Empty<MinecraftOpaqueFragment>());
        }

        private static byte ReadNibble(ReadOnlySpan<byte> values, int index)
        {
            byte packed = values[index >> 1];
            return (byte)((index & 1) == 0 ? packed & 15 : packed >> 4 & 15);
        }

        private static void WriteNibble(Span<byte> values, int index, byte value)
        {
            int packedIndex = index >> 1;
            if((index & 1) == 0)
                values[packedIndex] = (byte)(values[packedIndex] & 0xf0 | value & 0x0f);
            else
                values[packedIndex] = (byte)(values[packedIndex] & 0x0f | (value & 0x0f) << 4);
        }
    }
}
