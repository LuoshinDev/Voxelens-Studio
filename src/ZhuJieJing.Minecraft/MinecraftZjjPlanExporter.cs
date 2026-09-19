using System.Text;
using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

public sealed record MinecraftZjjPlanExportRequest(
    string DestinationPath,
    INormalizedMinecraftChunkSource Source,
    MinecraftDimensionId Dimension,
    string PlanId,
    string? ProtectedSourcePath = null,
    IReadOnlyMinecraftWorld? ValidationSource = null,
    ZjjVisualProfile? VisualProfile = null,
    int ReadConcurrency = MinecraftZjjPlanExporter.DefaultReadConcurrency,
    BlockPosition? SpawnPoint = null);

public sealed record MinecraftZjjPlanExportResult(
    string OutputPath,
    ZjjPlan Plan,
    long ChunkCount,
    long NonAirBlockCount,
    long OmittedBlockEntityCount,
    BoxSelection Bounds);

/// <summary>
/// Exports canonical Minecraft chunks into deterministic, editable AI-plan operations. Consecutive blocks of
/// the same state become vertical column runs, keeping the JSON understandable without expanding solid builds
/// into one operation per voxel.
/// </summary>
public sealed class MinecraftZjjPlanExporter
{
    public const int MaximumNonAirBlocks = ZjjPlanValidator.MaximumAttemptedWrites;
    public const int MinimumReadConcurrency = 1;
    public const int DefaultReadConcurrency = 8;
    public const int MaximumReadConcurrency = 32;

    public async Task<MinecraftZjjPlanExportResult> ExportAsync(
        MinecraftZjjPlanExportRequest request,
        IProgress<MinecraftConversionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        string destination = Path.GetFullPath(request.DestinationPath);
        if(request.ProtectedSourcePath is not null &&
           string.Equals(destination, Path.GetFullPath(request.ProtectedSourcePath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("导出目标不能覆盖当前打开的源文件。");
        if(File.Exists(destination) || Directory.Exists(destination))
            throw new IOException($"导出目标已经存在：{destination}");
        string? parent = Path.GetDirectoryName(destination);
        if(parent is null || !Directory.Exists(parent))
            throw new DirectoryNotFoundException($"导出目录不存在：{parent}");

        await EnsureValidationSourceStableAsync(request.ValidationSource, cancellationToken).ConfigureAwait(false);
        BuildResult built = await BuildAsync(request, progress, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        string temporary = Path.Combine(parent, $".{Path.GetFileName(destination)}.zjj-staging-{Guid.NewGuid():N}");
        try
        {
            await using(FileStream output = new(
                            temporary,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            64 * 1024,
                            FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await ZjjPlanJson.SerializeAsync(output, built.Plan, indented: false, cancellationToken)
                    .ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureValidationSourceStableAsync(request.ValidationSource, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination);
            return new MinecraftZjjPlanExportResult(
                destination,
                built.Plan,
                built.ChunkCount,
                built.NonAirBlockCount,
                built.BlockEntityCount,
                built.Bounds);
        }
        finally
        {
            if(File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch(IOException)
                {
                }
                catch(UnauthorizedAccessException)
                {
                }
            }
        }
    }

    internal static async Task<BuildResult> BuildAsync(
        MinecraftZjjPlanExportRequest request,
        IProgress<MinecraftConversionProgress>? progress,
        CancellationToken cancellationToken)
    {
        Dictionary<ColumnRunKey, List<BlockPosition>> runs = [];
        Dictionary<string, BlockState> states = new(StringComparer.Ordinal);
        BlockPosition? minimum = null;
        BlockPosition? maximum = null;
        long chunks = 0;
        long nonAir = 0;
        long blockEntities = 0;
        string sourceRevision = request.Source.Revision;
        MinecraftVersionDescriptor? sourceVersion = null;
        await foreach(ChunkBuildResult chunkBuild in EnumerateChunkBuildsAsync(request, cancellationToken)
                           .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            NormalizedMinecraftChunk chunk = chunkBuild.Chunk;
            if(chunk.Address.Dimension != request.Dimension)
                throw new InvalidDataException($"导出 {request.Dimension} 时收到了其他维度区块 {chunk.Address}。");
            if(sourceVersion is null)
                sourceVersion = chunk.SourceVersion;
            else if(sourceVersion != chunk.SourceVersion)
                throw new InvalidDataException($"导出源混合了不同 Minecraft 版本：{sourceVersion} / {chunk.SourceVersion}。");
            foreach((string key, BlockState state) in chunkBuild.States)
                states.TryAdd(key, state);
            foreach((ColumnRunKey key, List<BlockPosition> origins) in chunkBuild.Runs)
            {
                if(!runs.TryGetValue(key, out List<BlockPosition>? mergedOrigins))
                {
                    mergedOrigins = [];
                    runs.Add(key, mergedOrigins);
                }
                mergedOrigins.AddRange(origins);
            }
            if(chunkBuild.Minimum is BlockPosition chunkMinimum)
            {
                minimum = minimum is BlockPosition currentMinimum
                    ? new BlockPosition(
                        Math.Min(currentMinimum.X, chunkMinimum.X),
                        Math.Min(currentMinimum.Y, chunkMinimum.Y),
                        Math.Min(currentMinimum.Z, chunkMinimum.Z))
                    : chunkMinimum;
            }
            if(chunkBuild.Maximum is BlockPosition chunkMaximum)
            {
                maximum = maximum is BlockPosition currentMaximum
                    ? new BlockPosition(
                        Math.Max(currentMaximum.X, chunkMaximum.X),
                        Math.Max(currentMaximum.Y, chunkMaximum.Y),
                        Math.Max(currentMaximum.Z, chunkMaximum.Z))
                    : chunkMaximum;
            }
            nonAir = checked(nonAir + chunkBuild.NonAirBlockCount);
            if(nonAir > MaximumNonAirBlocks)
                throw new InvalidDataException($"筑界镜蓝图最多容纳 {MaximumNonAirBlocks:N0} 个非空气方块，请缩小导出范围。");
            blockEntities = checked(blockEntities + chunkBuild.BlockEntityCount);
            chunks = checked(chunks + 1);
            progress?.Report(new MinecraftConversionProgress(
                chunks,
                null,
                nonAir,
                chunk.Address,
                "正在生成筑界镜蓝图"));
        }
        if(!string.Equals(sourceRevision, request.Source.Revision, StringComparison.Ordinal))
            throw new InvalidOperationException("导出期间场景 revision 发生变化。");
        if(minimum is not BlockPosition min || maximum is not BlockPosition max)
            throw new InvalidDataException("当前场景没有可导出的非空气方块。");

        BoxSelection bounds = new(min, max.Offset(1, 1, 1));
        ZjjVisualProfile? visualProfile = request.VisualProfile ?? CreateVisualProfile(sourceVersion);
        ZjjPlan plan = BuildPlan(
            request.PlanId,
            request.Dimension,
            bounds,
            states,
            runs,
            visualProfile,
            request.SpawnPoint);
        ZjjPlanValidator.EnsureValid(plan);
        return new BuildResult(plan, chunks, nonAir, blockEntities, bounds);
    }

    private static async IAsyncEnumerable<ChunkBuildResult> EnumerateChunkBuildsAsync(
        MinecraftZjjPlanExportRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if(request.ValidationSource is null)
        {
            await foreach(NormalizedMinecraftChunk chunk in request.Source
                              .EnumerateAsync(request.Dimension, cancellationToken: cancellationToken)
                              .ConfigureAwait(false))
            {
                yield return BuildChunkRuns(chunk, cancellationToken);
            }
            yield break;
        }

        await using IAsyncEnumerator<MinecraftChunkIndexEntry> entries = request.ValidationSource.ChunkIndex
            .EnumerateAsync(request.Dimension, cancellationToken: cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        bool hasMore = true;
        while(hasMore)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = new List<MinecraftChunkIndexEntry>(request.ReadConcurrency);
            while(window.Count < request.ReadConcurrency && (hasMore = await entries.MoveNextAsync().ConfigureAwait(false)))
                window.Add(entries.Current);
            if(window.Count == 0) yield break;

            Task<ChunkBuildResult>[] reads = window
                .Select(entry => ReadAndBuildChunkAsync(request.Source, entry.Address, cancellationToken))
                .ToArray();
            ChunkBuildResult[] chunks = await Task.WhenAll(reads).ConfigureAwait(false);
            for(int index = 0; index < chunks.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if(chunks[index].Chunk.Address != window[index].Address)
                    throw new InvalidDataException($"请求区块 {window[index].Address} 时返回了 {chunks[index].Chunk.Address}。");
                yield return chunks[index];
            }
        }
    }

    private static async Task<ChunkBuildResult> ReadAndBuildChunkAsync(
        INormalizedMinecraftChunkSource source,
        MinecraftChunkAddress address,
        CancellationToken cancellationToken)
    {
        NormalizedMinecraftChunk chunk = await source.FindAsync(address, cancellationToken).ConfigureAwait(false) ??
            throw new InvalidDataException($"导出源缺少已索引区块 {address}。");
        return BuildChunkRuns(chunk, cancellationToken);
    }

    private static ChunkBuildResult BuildChunkRuns(
        NormalizedMinecraftChunk chunk,
        CancellationToken cancellationToken)
    {
        Dictionary<ColumnRunKey, List<BlockPosition>> runs = [];
        Dictionary<string, BlockState> states = new(StringComparer.Ordinal);
        BlockPosition? minimum = null;
        BlockPosition? maximum = null;
        long nonAir = 0;
        AddChunkRuns(chunk, runs, states, ref minimum, ref maximum, ref nonAir, cancellationToken);
        return new ChunkBuildResult(
            chunk,
            runs,
            states,
            minimum,
            maximum,
            nonAir,
            chunk.BlockEntities.Count);
    }

    private static void AddChunkRuns(
        NormalizedMinecraftChunk chunk,
        IDictionary<ColumnRunKey, List<BlockPosition>> runs,
        IDictionary<string, BlockState> states,
        ref BlockPosition? minimum,
        ref BlockPosition? maximum,
        ref long nonAir,
        CancellationToken cancellationToken)
    {
        ColumnRunCursor[] columns = new ColumnRunCursor[256];
        int chunkOriginX = checked(chunk.Address.X * 16);
        int chunkOriginZ = checked(chunk.Address.Z * 16);
        foreach(NormalizedMinecraftSection section in chunk.Sections.OrderBy(static section => section.Coordinate.Y))
        {
            if(section.Coordinate.X != chunk.Address.X || section.Coordinate.Z != chunk.Address.Z)
                throw new InvalidDataException($"Section {section.Coordinate} 不属于区块 {chunk.Address}。");
            if(section.Palette.Count == 0 || section.PaletteIndices.Length != 4096)
                throw new InvalidDataException($"Section {section.Coordinate} 数据不完整。");
            ReadOnlySpan<ushort> indices = section.PaletteIndices.Span;
            int sectionOriginY = checked(section.Coordinate.Y * 16);
            for(int localIndex = 0; localIndex < indices.Length; localIndex++)
            {
                if((localIndex & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                ushort paletteIndex = indices[localIndex];
                if(paletteIndex >= section.Palette.Count)
                    throw new InvalidDataException($"Section {section.Coordinate} 含无效调色板索引。");
                BlockState state = section.Palette[paletteIndex];
                int columnIndex = (localIndex & 15) | (localIndex >> 4 & 15) << 4;
                ref ColumnRunCursor column = ref columns[columnIndex];
                if(state.IsAir)
                {
                    if(column.Active)
                        FinishColumnRun(ref column, columnIndex, chunkOriginX, chunkOriginZ, runs);
                    continue;
                }
                nonAir = checked(nonAir + 1);
                if(nonAir > MaximumNonAirBlocks)
                    throw new InvalidDataException($"筑界镜蓝图最多容纳 {MaximumNonAirBlocks:N0} 个非空气方块，请缩小导出范围。");
                int blockY = checked(sectionOriginY + (localIndex >> 8));
                if(column.HasObservedNonAir && blockY <= column.LastNonAirY)
                {
                    int worldX = checked(chunkOriginX + (columnIndex & 15));
                    int worldZ = checked(chunkOriginZ + (columnIndex >> 4));
                    throw new InvalidDataException($"区块 {chunk.Address} 的 ({worldX}, {worldZ}) 列存在重复高度。");
                }
                if(column.Active && blockY == column.PreviousY + 1 && column.State!.Equals(state))
                {
                    column.PreviousY = blockY;
                }
                else
                {
                    if(column.Active)
                        FinishColumnRun(ref column, columnIndex, chunkOriginX, chunkOriginZ, runs);
                    column.Active = true;
                    column.StartY = blockY;
                    column.PreviousY = blockY;
                    column.State = state;
                }
                column.HasObservedNonAir = true;
                column.LastNonAirY = blockY;
                states.TryAdd(state.CanonicalKey, state);
                BlockPosition position = new(
                    checked(chunkOriginX + (localIndex & 15)),
                    blockY,
                    checked(chunkOriginZ + (localIndex >> 4 & 15)));
                minimum = minimum is BlockPosition min
                    ? new BlockPosition(Math.Min(min.X, position.X), Math.Min(min.Y, position.Y), Math.Min(min.Z, position.Z))
                    : position;
                maximum = maximum is BlockPosition max
                    ? new BlockPosition(Math.Max(max.X, position.X), Math.Max(max.Y, position.Y), Math.Max(max.Z, position.Z))
                    : position;
            }
        }

        for(int columnIndex = 0; columnIndex < columns.Length; columnIndex++)
        {
            if(columns[columnIndex].Active)
                FinishColumnRun(ref columns[columnIndex], columnIndex, chunkOriginX, chunkOriginZ, runs);
        }
    }

    private static void FinishColumnRun(
        ref ColumnRunCursor column,
        int columnIndex,
        int chunkOriginX,
        int chunkOriginZ,
        IDictionary<ColumnRunKey, List<BlockPosition>> runs)
    {
        int height = checked(column.PreviousY - column.StartY + 1);
        ColumnRunKey key = new(column.State!.CanonicalKey, height);
        if(!runs.TryGetValue(key, out List<BlockPosition>? origins))
        {
            origins = [];
            runs.Add(key, origins);
        }
        origins.Add(new BlockPosition(
            checked(chunkOriginX + (columnIndex & 15)),
            column.StartY,
            checked(chunkOriginZ + (columnIndex >> 4))));
        column.Active = false;
        column.State = null;
    }

    private static ZjjPlan BuildPlan(
        string planId,
        MinecraftDimensionId dimension,
        BoxSelection bounds,
        IReadOnlyDictionary<string, BlockState> states,
        IReadOnlyDictionary<ColumnRunKey, List<BlockPosition>> runs,
        ZjjVisualProfile? visualProfile,
        BlockPosition? spawnPoint)
    {
        string[] orderedStateKeys = states.Keys.Order(StringComparer.Ordinal).ToArray();
        if(orderedStateKeys.Length > ZjjPlanValidator.MaximumMaterials)
            throw new InvalidDataException($"场景材质超过 {ZjjPlanValidator.MaximumMaterials:N0} 种，无法生成可审查蓝图。");
        Dictionary<string, string> materialIds = new(StringComparer.Ordinal);
        Dictionary<string, MaterialIntent> materials = new(StringComparer.Ordinal);
        for(int index = 0; index < orderedStateKeys.Length; index++)
        {
            string materialId = $"material-{index + 1:D4}";
            materialIds.Add(orderedStateKeys[index], materialId);
            materials.Add(materialId, MaterialIntent.Exact(states[orderedStateKeys[index]]));
        }

        List<PlanOperation> operations = [];
        int operationNumber = 0;
        foreach((ColumnRunKey key, List<BlockPosition> origins) in runs
                    .OrderBy(static pair => pair.Key.StateKey, StringComparer.Ordinal)
                    .ThenBy(static pair => pair.Key.Height))
        {
            BlockPosition[] orderedOrigins = origins
                .OrderBy(static position => position.X)
                .ThenBy(static position => position.Z)
                .ThenBy(static position => position.Y)
                .ToArray();
            for(int offset = 0; offset < orderedOrigins.Length; offset += ZjjPlanValidator.MaximumColumnOrigins)
            {
                int count = Math.Min(ZjjPlanValidator.MaximumColumnOrigins, orderedOrigins.Length - offset);
                operations.Add(new ColumnsOperation
                {
                    Id = $"columns-{++operationNumber:D5}",
                    SelectionId = "scene",
                    MaterialId = materialIds[key.StateKey],
                    WriteStrategy = WriteStrategy.Overwrite,
                    Height = key.Height,
                    Origins = orderedOrigins.AsSpan(offset, count).ToArray().ToList(),
                });
            }
        }
        if(operations.Count > ZjjPlanValidator.MaximumOperations)
            throw new InvalidDataException($"场景需要 {operations.Count:N0} 条蓝图操作，超过 {ZjjPlanValidator.MaximumOperations:N0} 条上限。");

        return new ZjjPlan
        {
            PlanId = NormalizePlanId(planId),
            Dimension = dimension.Value,
            VisualProfile = visualProfile,
            CoordinateSystem = ZjjCoordinateSystem.MinecraftJava,
            SpawnPoint = spawnPoint,
            Selections = new Dictionary<string, BoxSelection>(StringComparer.Ordinal) { ["scene"] = bounds },
            Materials = materials,
            Operations = operations,
        };
    }

    private static string NormalizePlanId(string value)
    {
        string planId = value.Trim();
        if(planId.Length == 0) return "exported-scene";
        StringBuilder builder = new(planId.Length);
        foreach(char character in planId)
            builder.Append(char.IsControl(character) ? '_' : character);
        return builder.ToString();
    }

    private static void ValidateRequest(MinecraftZjjPlanExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationPath);
        if(!Path.GetExtension(request.DestinationPath).Equals(".zz", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("筑界镜蓝图目标必须使用 .zz 扩展名。", nameof(request));
        if(string.IsNullOrWhiteSpace(request.Dimension.Value))
            throw new ArgumentException("导出维度不能为空。", nameof(request));
        if(request.ReadConcurrency is < MinimumReadConcurrency or > MaximumReadConcurrency)
            throw new ArgumentOutOfRangeException(nameof(request), $"导出读取并发必须在 {MinimumReadConcurrency}–{MaximumReadConcurrency} 之间。");
    }

    private static ZjjVisualProfile? CreateVisualProfile(MinecraftVersionDescriptor? version)
    {
        if(version is null) return null;
        string? versionName = string.IsNullOrWhiteSpace(version.VersionName) ? null : version.VersionName.Trim();
        string storageFamily = version.StorageFamily switch
        {
            MinecraftStorageFamily.LegacyNumericAnvil => ZjjVisualProfile.LegacyNumericAnvilStorageFamily,
            MinecraftStorageFamily.FlattenedPalette => ZjjVisualProfile.FlattenedPaletteStorageFamily,
            MinecraftStorageFamily.ModernSectionPalette => ZjjVisualProfile.ModernSectionPaletteStorageFamily,
            _ => ZjjVisualProfile.UnknownStorageFamily,
        };
        if(versionName is null && version.DataVersion is null &&
           string.Equals(storageFamily, ZjjVisualProfile.UnknownStorageFamily, StringComparison.Ordinal))
            return null;
        return new ZjjVisualProfile
        {
            Edition = ZjjVisualProfile.JavaEdition,
            VersionName = versionName,
            DataVersion = version.DataVersion,
            StorageFamily = storageFamily,
        };
    }

    private static async ValueTask EnsureValidationSourceStableAsync(
        IReadOnlyMinecraftWorld? source,
        CancellationToken cancellationToken)
    {
        if(source is null) return;
        WorldSourceValidationResult validation = await source.ValidateSourceAsync(cancellationToken).ConfigureAwait(false);
        if(!validation.IsStable)
            throw new IOException("原世界在导出期间发生了变化，请关闭 Minecraft 并重新打开世界。");
    }

    internal sealed record BuildResult(
        ZjjPlan Plan,
        long ChunkCount,
        long NonAirBlockCount,
        long BlockEntityCount,
        BoxSelection Bounds);

    private sealed record ChunkBuildResult(
        NormalizedMinecraftChunk Chunk,
        IReadOnlyDictionary<ColumnRunKey, List<BlockPosition>> Runs,
        IReadOnlyDictionary<string, BlockState> States,
        BlockPosition? Minimum,
        BlockPosition? Maximum,
        long NonAirBlockCount,
        long BlockEntityCount);

    private struct ColumnRunCursor
    {
        public bool Active;
        public bool HasObservedNonAir;
        public int StartY;
        public int PreviousY;
        public int LastNonAirY;
        public BlockState? State;
    }

    private readonly record struct ColumnRunKey(string StateKey, int Height);
}
