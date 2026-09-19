using System.Buffers;
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

/// <summary>Inclusive rectangular boundary in Chunk coordinates.</summary>
public readonly record struct MinecraftChunkSquareBounds
{
    public MinecraftChunkSquareBounds(int minChunkX, int minChunkZ, int maxChunkX, int maxChunkZ)
    {
        if(minChunkX > maxChunkX)
            throw new ArgumentOutOfRangeException(nameof(minChunkX), "最小 Chunk X 不能大于最大 Chunk X。");
        if(minChunkZ > maxChunkZ)
            throw new ArgumentOutOfRangeException(nameof(minChunkZ), "最小 Chunk Z 不能大于最大 Chunk Z。");
        long width = (long)maxChunkX - minChunkX + 1;
        long depth = (long)maxChunkZ - minChunkZ + 1;

        MinChunkX = minChunkX;
        MinChunkZ = minChunkZ;
        MaxChunkX = maxChunkX;
        MaxChunkZ = maxChunkZ;
        Width = width;
        Depth = depth;
        SideLength = Math.Max(width, depth);
    }

    public int MinChunkX { get; }

    public int MinChunkZ { get; }

    public int MaxChunkX { get; }

    public int MaxChunkZ { get; }

    public long Width { get; }

    public long Depth { get; }

    public long SideLength { get; }

    public bool Contains(int chunkX, int chunkZ) =>
        chunkX >= MinChunkX && chunkX <= MaxChunkX &&
        chunkZ >= MinChunkZ && chunkZ <= MaxChunkZ;

    public bool Contains(MinecraftChunkAddress address) => Contains(address.X, address.Z);
}

public static class MinecraftWorldCropSpawnPolicy
{
    public static bool IsInside(BlockPosition spawn, MinecraftChunkSquareBounds bounds) =>
        bounds.Contains(FloorDiv(spawn.X, 16), FloorDiv(spawn.Z, 16));

    public static BlockPosition MoveToCenter(MinecraftChunkSquareBounds bounds, int retainedY)
    {
        long minimumX = (long)bounds.MinChunkX * 16L;
        long minimumZ = (long)bounds.MinChunkZ * 16L;
        long widthInBlocks = checked(bounds.Width * 16L);
        long depthInBlocks = checked(bounds.Depth * 16L);
        return new BlockPosition(
            checked((int)(minimumX + widthInBlocks / 2L)),
            retainedY,
            checked((int)(minimumZ + depthInBlocks / 2L)));
    }

    private static int FloorDiv(int value, int divisor)
    {
        int quotient = value / divisor;
        int remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }
}

public sealed record MinecraftWorldCropAnalyzeRequest(
    IReadOnlyMinecraftWorld SourceWorld,
    string ExpectedSourceRevision,
    MinecraftDimensionId Dimension,
    MinecraftChunkSquareBounds Bounds);

public enum MinecraftWorldCropStage
{
    ValidatingSource,
    AnalyzingRegions,
    AnalyzingSidecars,
    PreparingStaging,
    CopyingFiles,
    RepackingRegions,
    ValidatingStaging,
    ReadyToCommit,
}

public sealed record MinecraftWorldCropProgress(
    MinecraftWorldCropStage Stage,
    long CompletedItems,
    long TotalItems,
    string? Message = null,
    long BytesRead = 0,
    long BytesWritten = 0,
    MinecraftChunkAddress? CurrentChunk = null);

public sealed class MinecraftWorldCropAnalysis
{
    internal MinecraftWorldCropAnalysis(
        IReadOnlyMinecraftWorld sourceWorld,
        IReadOnlyMinecraftWorldFileSource worldFiles,
        IReadOnlyDictionary<string, MinecraftFileFingerprint> files,
        IReadOnlyList<MinecraftChunkIndexEntry> entries,
        IReadOnlyDictionary<string, CropFileAction> fileActions,
        IReadOnlyList<CropRegionPlan> regionPlans,
        IReadOnlySet<string> targetDataDirectories,
        MinecraftDimensionId dimension,
        MinecraftChunkSquareBounds bounds,
        BlockPosition? relocatedSpawnLocation,
        long targetDimensionChunkCount,
        long removedChunkCount,
        long retainedTargetDimensionChunkCount,
        long sourceRegionFileCount,
        long removedRegionCount,
        long repackedRegionCount,
        long sidecarRegionCount,
        long removedSidecarRegionCount,
        long repackedSidecarRegionCount,
        long indexedSidecarRecordCount,
        long removedSidecarRecordCount,
        long retainedSidecarRecordCount,
        long unknownSidecarRegionCount,
        long estimatedReclaimableBytes,
        IReadOnlyList<MinecraftDiagnostic> diagnostics)
    {
        SourceWorld = sourceWorld;
        WorldFiles = worldFiles;
        Files = files;
        Entries = entries;
        FileActions = fileActions;
        RegionPlans = regionPlans;
        TargetDataDirectories = targetDataDirectories;
        HasChanges = relocatedSpawnLocation is not null || fileActions.Values.Any(static action =>
            action is CropFileAction.Omit or CropFileAction.Repack or CropFileAction.WrittenByRegionRepack);
        SourceDirectory = sourceWorld.Descriptor.RootPath;
        SourceRevision = sourceWorld.Descriptor.SourceRevision;
        Dimension = dimension;
        Bounds = bounds;
        RelocatedSpawnLocation = relocatedSpawnLocation;
        TotalChunkCount = entries.Count;
        TargetDimensionChunkCount = targetDimensionChunkCount;
        OtherDimensionChunkCount = entries.Count - targetDimensionChunkCount;
        RemovedChunkCount = removedChunkCount;
        RetainedTargetDimensionChunkCount = retainedTargetDimensionChunkCount;
        RetainedChunkCount = entries.Count - removedChunkCount;
        SourceRegionFileCount = sourceRegionFileCount;
        RemovedRegionCount = removedRegionCount;
        RepackedRegionCount = repackedRegionCount;
        SidecarRegionCount = sidecarRegionCount;
        RemovedSidecarRegionCount = removedSidecarRegionCount;
        RepackedSidecarRegionCount = repackedSidecarRegionCount;
        IndexedSidecarRecordCount = indexedSidecarRecordCount;
        RemovedSidecarRecordCount = removedSidecarRecordCount;
        RetainedSidecarRecordCount = retainedSidecarRecordCount;
        UnknownSidecarRegionCount = unknownSidecarRegionCount;
        EstimatedReclaimableBytes = estimatedReclaimableBytes;
        Diagnostics = diagnostics;
    }

    public string SourceDirectory { get; }
    public string SourceRevision { get; }
    public MinecraftDimensionId Dimension { get; }
    public MinecraftChunkSquareBounds Bounds { get; }
    public BlockPosition? RelocatedSpawnLocation { get; }
    public bool HasChanges { get; }
    public long TotalChunkCount { get; }
    public long TargetDimensionChunkCount { get; }
    public long OtherDimensionChunkCount { get; }
    public long RemovedChunkCount { get; }
    public long RetainedTargetDimensionChunkCount { get; }
    public long RetainedChunkCount { get; }
    public long SourceRegionFileCount { get; }
    public long RemovedRegionCount { get; }
    public long RepackedRegionCount { get; }
    public long SidecarRegionCount { get; }
    public long RemovedSidecarRegionCount { get; }
    public long RepackedSidecarRegionCount { get; }
    public long IndexedSidecarRecordCount { get; }
    public long RemovedSidecarRecordCount { get; }
    public long RetainedSidecarRecordCount { get; }
    public long UnknownSidecarRegionCount { get; }
    public long EstimatedReclaimableBytes { get; }
    public IReadOnlyList<MinecraftDiagnostic> Diagnostics { get; }

    internal IReadOnlyMinecraftWorld SourceWorld { get; }
    internal IReadOnlyMinecraftWorldFileSource WorldFiles { get; }
    internal IReadOnlyDictionary<string, MinecraftFileFingerprint> Files { get; }
    internal IReadOnlyList<MinecraftChunkIndexEntry> Entries { get; }
    internal IReadOnlyDictionary<string, CropFileAction> FileActions { get; }
    internal IReadOnlyList<CropRegionPlan> RegionPlans { get; }
    internal IReadOnlySet<string> TargetDataDirectories { get; }
}

public sealed record MinecraftWorldCropResult(
    string SourceDirectory,
    string SourceRevision,
    string StagingDirectory,
    MinecraftDimensionId Dimension,
    MinecraftChunkSquareBounds Bounds,
    BlockPosition? RelocatedSpawnLocation,
    long SourceBytes,
    long StagedBytes,
    long BytesFreed,
    long RemovedChunkCount,
    long RetainedChunkCount,
    long RemovedRegionCount,
    long RepackedRegionCount,
    long RemovedSidecarRegionCount,
    long RepackedSidecarRegionCount,
    long RemovedSidecarRecordCount,
    long RetainedSidecarRecordCount,
    long RetainedExternalChunkCount,
    IReadOnlyList<MinecraftDiagnostic> Diagnostics);

public enum PreparedMinecraftWorldCropState
{
    Prepared,
    CommitFailedNeedsRecovery,
    Committed,
    Finalized,
    RolledBack,
}

public sealed class PreparedMinecraftWorldCrop : IAsyncDisposable
{
    private readonly PreparedWorldDirectoryTransaction transaction;

    internal PreparedMinecraftWorldCrop(
        MinecraftWorldCropResult result,
        string rollbackDirectory,
        IReadOnlyDictionary<string, MinecraftFileFingerprint> sourceFiles)
    {
        Result = result;
        transaction = new PreparedWorldDirectoryTransaction(
            result.SourceDirectory,
            result.StagingDirectory,
            rollbackDirectory,
            sourceFiles,
            ".zjj-crop-staging-",
            ".zjj-crop-rollback-",
            "地图裁剪");
    }

    public MinecraftWorldCropResult Result { get; }
    public string SourceDirectory => Result.SourceDirectory;
    public string StagingDirectory => Result.StagingDirectory;
    public PreparedMinecraftWorldCropState State => (PreparedMinecraftWorldCropState)transaction.State;
    public string? CleanupWarning => transaction.CleanupWarning;
    public string? ResidualDirectoryPath => transaction.ResidualDirectoryPath;
    public void Commit() => transaction.Commit();
    public void FinalizeCommit() => transaction.FinalizeCommit();
    public void Rollback() => transaction.Rollback();
    public ValueTask DisposeAsync() => transaction.DisposeAsync();
}

internal enum CropFileAction
{
    Copy,
    Omit,
    Repack,
    WrittenByRegionRepack,
}

internal enum CropRegionKind
{
    Main,
    Entities,
    PointOfInterest,
}

internal enum CropRegionRelation
{
    Outside,
    Inside,
    Boundary,
}

internal sealed record CropRegionPlan(
    string RelativePath,
    MinecraftRegionAddress Region,
    CropRegionKind Kind,
    CropRegionRelation Relation,
    CropFileAction Action,
    IReadOnlyList<MinecraftChunkIndexEntry> IndexedEntries,
    IReadOnlyList<MinecraftChunkIndexEntry> RetainedEntries,
    bool IndexKnown);

public sealed class MinecraftWorldCropService
{
    private const int SectorBytes = 4096;
    private const int RegionHeaderBytes = SectorBytes * 2;
    private const int CopyBufferBytes = 128 * 1024;
    private static readonly Regex RegionFileName = new(
        @"^r\.(-?\d+)\.(-?\d+)\.mca$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExternalChunkFileName = new(
        @"^c\.(-?\d+)\.(-?\d+)\.mcc$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<MinecraftWorldCropAnalysis> AnalyzeAsync(
        MinecraftWorldCropAnalyzeRequest request,
        IProgress<MinecraftWorldCropProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyMinecraftWorld world = request.SourceWorld;
        IReadOnlyMinecraftWorldFileSource worldFiles = world as IReadOnlyMinecraftWorldFileSource ??
            throw new NotSupportedException("当前世界挂载未提供完整文件源，不能安全执行地图裁剪。");

        Report(progress, MinecraftWorldCropStage.ValidatingSource, 0, 1, "正在验证源世界");
        PreparedWorldDirectoryTransaction.EnsureDirectoryTreeHasNoReparsePoints(
            world.Descriptor.RootPath,
            "地图裁剪");
        await EnsureSourceStableAsync(world, cancellationToken).ConfigureAwait(false);

        IReadOnlyDictionary<string, MinecraftFileFingerprint> files = await CollectFilesAsync(
            worldFiles,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<MinecraftChunkIndexEntry> entries = await CollectChunkEntriesAsync(
            world,
            cancellationToken).ConfigureAwait(false);
        MinecraftChunkIndexEntry[] targetEntries = entries
            .Where(entry => entry.Address.Dimension == request.Dimension)
            .ToArray();
        MinecraftChunkIndexEntry[] retainedTargetEntries = targetEntries
            .Where(entry => request.Bounds.Contains(entry.Address))
            .ToArray();
        if(retainedTargetEntries.Length == 0)
            throw new InvalidOperationException("裁剪边界内没有可保留的 Chunk，已拒绝生成空维度。");
        if(entries.Count - targetEntries.Length + retainedTargetEntries.Length == 0)
            throw new InvalidOperationException("裁剪结果将不包含任何 Chunk，已拒绝执行。");

        List<MinecraftDiagnostic> diagnostics = [];
        BlockPosition? relocatedSpawn = null;
        if(request.Dimension == MinecraftDimensionId.Overworld &&
           world.Descriptor.SpawnLocation is BlockPosition spawn &&
           !MinecraftWorldCropSpawnPolicy.IsInside(spawn, request.Bounds))
        {
            relocatedSpawn = await MinecraftSafeSpawnFinder.FindAsync(world, retainedTargetEntries, request.Bounds, cancellationToken)
                .ConfigureAwait(false);
            diagnostics.Add(new MinecraftDiagnostic(
                "crop.spawn.relocated",
                MinecraftDiagnosticSeverity.Information,
                $"原出生点 ({spawn.X}, {spawn.Y}, {spawn.Z}) 位于选区外；已在保留区块中验证安全落脚面，出生点将迁移到 " +
                $"({relocatedSpawn.Value.X}, {relocatedSpawn.Value.Y}, {relocatedSpawn.Value.Z})，并将出生随机半径设为 0。"));
        }
        CropPlanningResult planning = await BuildPlanAsync(
            world,
            worldFiles,
            files,
            entries,
            request.Dimension,
            request.Bounds,
            diagnostics,
            progress,
            cancellationToken).ConfigureAwait(false);
        await EnsureSourceStableAsync(world, cancellationToken).ConfigureAwait(false);

        long removedChunks = targetEntries.LongCount(entry => !request.Bounds.Contains(entry.Address));
        long estimatedBytes = EstimateReclaimableBytes(files, planning.FileActions, planning.RegionPlans);
        Report(progress, MinecraftWorldCropStage.ValidatingSource, 1, 1, "裁剪分析完成");
        return new MinecraftWorldCropAnalysis(
            world,
            worldFiles,
            new ReadOnlyDictionary<string, MinecraftFileFingerprint>(
                new Dictionary<string, MinecraftFileFingerprint>(files, StringComparer.OrdinalIgnoreCase)),
            Array.AsReadOnly(entries.ToArray()),
            new ReadOnlyDictionary<string, CropFileAction>(
                new Dictionary<string, CropFileAction>(planning.FileActions, StringComparer.OrdinalIgnoreCase)),
            Array.AsReadOnly(planning.RegionPlans.ToArray()),
            new HashSet<string>(planning.TargetDataDirectories, StringComparer.OrdinalIgnoreCase),
            request.Dimension,
            request.Bounds,
            relocatedSpawn,
            targetEntries.LongLength,
            removedChunks,
            retainedTargetEntries.LongLength,
            planning.SourceMainRegionCount,
            planning.RemovedMainRegionCount,
            planning.RepackedMainRegionCount,
            planning.SidecarRegionCount,
            planning.RemovedSidecarRegionCount,
            planning.RepackedSidecarRegionCount,
            planning.IndexedSidecarRecordCount,
            planning.RemovedSidecarRecordCount,
            planning.RetainedSidecarRecordCount,
            planning.UnknownSidecarRegionCount,
            estimatedBytes,
            Array.AsReadOnly(diagnostics.ToArray()));
    }

    public async Task<PreparedMinecraftWorldCrop> PrepareAsync(
        MinecraftWorldCropAnalysis analysis,
        IProgress<MinecraftWorldCropProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyMinecraftWorld world = analysis.SourceWorld;
        if(!string.Equals(world.Descriptor.SourceRevision, analysis.SourceRevision, StringComparison.Ordinal))
            throw new InvalidOperationException("裁剪分析与当前世界 revision 不一致，请重新分析。");

        Report(progress, MinecraftWorldCropStage.ValidatingSource, 0, 1, "正在复核源世界");
        PreparedWorldDirectoryTransaction.EnsureDirectoryTreeHasNoReparsePoints(
            analysis.SourceDirectory,
            "地图裁剪");
        await EnsureSourceStableAsync(world, cancellationToken).ConfigureAwait(false);

        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(analysis.SourceDirectory));
        string parent = Path.GetDirectoryName(source) ??
                        throw new InvalidDataException("裁剪源世界必须有父目录。");
        string name = Path.GetFileName(source);
        if(name.Length == 0 || source.Equals(Path.GetPathRoot(source), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("不能对文件系统根目录执行地图裁剪。");
        if((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("裁剪源世界的父目录不能是重解析点。");

        string token = Guid.NewGuid().ToString("N");
        string staging = Path.Combine(parent, $".{name}.zjj-crop-staging-{token}");
        string rollback = Path.Combine(parent, $".{name}.zjj-crop-rollback-{token}");
        if(Directory.Exists(staging) || File.Exists(staging) ||
           Directory.Exists(rollback) || File.Exists(rollback))
        {
            throw new IOException("生成的裁剪事务路径已存在。");
        }

        MinecraftFileFingerprint[] copiedFiles = analysis.Files.Values
            .Where(file => analysis.FileActions[file.RelativePath] == CropFileAction.Copy)
            .OrderBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        CropRegionPlan[] repackedRegions = analysis.RegionPlans
            .Where(static plan => plan.Action == CropFileAction.Repack)
            .OrderBy(static plan => plan.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        long totalItems = copiedFiles.LongLength + repackedRegions.LongLength +
                          (analysis.RelocatedSpawnLocation is null ? 0L : 1L);
        long completedItems = 0;
        long bytesRead = 0;
        long bytesWritten = 0;
        long retainedExternalChunks = 0;

        try
        {
            Report(progress, MinecraftWorldCropStage.PreparingStaging, 0, totalItems, "正在创建同卷临时世界");
            Directory.CreateDirectory(staging);
            foreach(MinecraftFileFingerprint file in copiedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string outputPath = ResolveStagePath(staging, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                long copied = await CopyAndVerifyAsync(
                    analysis.WorldFiles,
                    file,
                    outputPath,
                    cancellationToken).ConfigureAwait(false);
                File.SetLastWriteTimeUtc(outputPath, file.LastWriteTimeUtc.UtcDateTime);
                bytesRead = checked(bytesRead + copied);
                bytesWritten = checked(bytesWritten + copied);
                completedItems++;
                if(ShouldReport(completedItems, totalItems))
                {
                    Report(
                        progress,
                        MinecraftWorldCropStage.CopyingFiles,
                        completedItems,
                        totalItems,
                        $"正在复制 {file.RelativePath}",
                        bytesRead,
                        bytesWritten);
                }
            }

            foreach(CropRegionPlan plan in repackedRegions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RegionRepackResult written = await RepackRegionAsync(
                    staging,
                    analysis.WorldFiles,
                    analysis.Files,
                    plan,
                    cancellationToken).ConfigureAwait(false);
                bytesRead = checked(bytesRead + written.BytesRead);
                bytesWritten = checked(bytesWritten + written.BytesWritten);
                retainedExternalChunks = checked(retainedExternalChunks + written.ExternalChunkCount);
                completedItems++;
                Report(
                    progress,
                    MinecraftWorldCropStage.RepackingRegions,
                    completedItems,
                    totalItems,
                    $"正在重打包 {plan.RelativePath}",
                    bytesRead,
                    bytesWritten);
            }

            if(analysis.RelocatedSpawnLocation is BlockPosition relocatedSpawn)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await MinecraftWorldExportProtection.RelocateSpawnAsync(
                    staging,
                    relocatedSpawn,
                    cancellationToken).ConfigureAwait(false);
                completedItems++;
                Report(
                    progress,
                    MinecraftWorldCropStage.CopyingFiles,
                    completedItems,
                    totalItems,
                    $"正在将出生点迁移到选区中心 ({relocatedSpawn.X}, {relocatedSpawn.Y}, {relocatedSpawn.Z})",
                    bytesRead,
                    bytesWritten);
            }

            await EnsureSourceStableAsync(world, cancellationToken).ConfigureAwait(false);
            Report(
                progress,
                MinecraftWorldCropStage.ValidatingStaging,
                0,
                1,
                "正在验证裁剪后的世界",
                bytesRead,
                bytesWritten);
            await ValidateStagingAsync(staging, analysis, cancellationToken).ConfigureAwait(false);
            await EnsureSourceStableAsync(world, cancellationToken).ConfigureAwait(false);

            long sourceBytes = analysis.Files.Values.Sum(static file => file.Length);
            long stagedBytes = GetDirectoryFileBytes(staging);
            MinecraftWorldCropResult result = new(
                source,
                analysis.SourceRevision,
                staging,
                analysis.Dimension,
                analysis.Bounds,
                analysis.RelocatedSpawnLocation,
                sourceBytes,
                stagedBytes,
                Math.Max(0, sourceBytes - stagedBytes),
                analysis.RemovedChunkCount,
                analysis.RetainedChunkCount,
                analysis.RemovedRegionCount,
                analysis.RepackedRegionCount,
                analysis.RemovedSidecarRegionCount,
                analysis.RepackedSidecarRegionCount,
                analysis.RemovedSidecarRecordCount,
                analysis.RetainedSidecarRecordCount,
                retainedExternalChunks,
                analysis.Diagnostics);
            Report(
                progress,
                MinecraftWorldCropStage.ReadyToCommit,
                1,
                1,
                "临时世界验证通过，等待提交",
                bytesRead,
                stagedBytes);
            return new PreparedMinecraftWorldCrop(result, rollback, analysis.Files);
        }
        catch(Exception exception)
        {
            Exception? cleanupFailure = TryDeleteStaging(staging, parent);
            if(cleanupFailure is not null)
            {
                throw new AggregateException(
                    "地图裁剪准备失败，且临时目录无法清理。",
                    exception,
                    cleanupFailure);
            }
            throw;
        }
    }

    private static async Task<CropPlanningResult> BuildPlanAsync(
        IReadOnlyMinecraftWorld world,
        IReadOnlyMinecraftWorldFileSource worldFiles,
        IReadOnlyDictionary<string, MinecraftFileFingerprint> files,
        IReadOnlyList<MinecraftChunkIndexEntry> allEntries,
        MinecraftDimensionId dimension,
        MinecraftChunkSquareBounds bounds,
        ICollection<MinecraftDiagnostic> diagnostics,
        IProgress<MinecraftWorldCropProgress>? progress,
        CancellationToken cancellationToken)
    {
        MinecraftDimensionDescriptor descriptor = world.Descriptor.Dimensions
            .Single(item => item.Id == dimension);
        Dictionary<string, CropRegionKind> dataDirectories = BuildTargetDataDirectories(descriptor);
        Dictionary<string, CropFileAction> actions = files.Keys.ToDictionary(
            static path => path,
            static path => IsSessionLock(path) ? CropFileAction.Omit : CropFileAction.Copy,
            StringComparer.OrdinalIgnoreCase);
        Dictionary<string, MinecraftChunkIndexEntry[]> mainEntriesByPath = allEntries
            .Where(entry => entry.Address.Dimension == dimension)
            .GroupBy(static entry => entry.RegionRelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderBy(static entry => entry.LocalIndex).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        MinecraftFileFingerprint[] regionFiles = files.Values
            .Where(file => dataDirectories.ContainsKey(NormalizeRelativeDirectory(GetRelativeParent(file.RelativePath))))
            .Where(file => Path.GetExtension(file.RelativePath).Equals(".mca", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        List<CropRegionPlan> plans = [];

        for(int index = 0; index < regionFiles.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MinecraftFileFingerprint file = regionFiles[index];
            string parent = NormalizeRelativeDirectory(GetRelativeParent(file.RelativePath));
            CropRegionKind kind = dataDirectories[parent];
            (int regionX, int regionZ) = ParseRegionCoordinates(file.RelativePath);
            MinecraftRegionAddress region = new(dimension, regionX, regionZ);
            CropRegionRelation relation = ClassifyRegion(regionX, regionZ, bounds);
            IReadOnlyList<MinecraftChunkIndexEntry> indexed;
            bool indexKnown = true;

            if(kind == CropRegionKind.Main)
            {
                indexed = mainEntriesByPath.GetValueOrDefault(file.RelativePath) ?? [];
                if(relation == CropRegionRelation.Boundary)
                {
                    IReadOnlyList<MinecraftChunkIndexEntry> audited = await ReadRegionHeaderAsync(
                        world.Descriptor.RootPath,
                        file,
                        region,
                        cancellationToken).ConfigureAwait(false);
                    EnsureSameIndex(file.RelativePath, indexed, audited);
                    if(indexed.Count == 0 && file.Length > RegionHeaderBytes)
                    {
                        throw new InvalidDataException(
                            $"边界 Region 没有活动位置条目却包含额外扇区，无法证明可安全裁剪：{file.RelativePath}");
                    }
                }
            }
            else
            {
                try
                {
                    indexed = await ReadRegionHeaderAsync(
                        world.Descriptor.RootPath,
                        file,
                        region,
                        cancellationToken).ConfigureAwait(false);
                }
                catch(OperationCanceledException)
                {
                    throw;
                }
                catch(Exception exception) when(IsSafePlanningFailure(exception) &&
                                                relation != CropRegionRelation.Boundary)
                {
                    indexed = [];
                    indexKnown = false;
                    diagnostics.Add(new MinecraftDiagnostic(
                        "crop.sidecar.preserved_or_removed_by_whole_region",
                        MinecraftDiagnosticSeverity.Warning,
                        relation == CropRegionRelation.Inside
                            ? $"选区内侧车 Region 无法解析，因整块保留而不改写：{file.RelativePath}；{exception.Message}"
                            : $"选区外侧车 Region 无法解析，已依据完整 Region 坐标整块移除：{file.RelativePath}；{exception.Message}"));
                }
                catch(Exception exception) when(IsSafePlanningFailure(exception))
                {
                    throw new InvalidDataException(
                        $"侧车 Region 横跨裁剪边界且无法安全解析，已中止：{file.RelativePath}",
                        exception);
                }
            }

            if(relation == CropRegionRelation.Boundary && indexed.Count == 0 && file.Length > RegionHeaderBytes)
            {
                throw new InvalidDataException(
                    $"边界 Region 没有活动位置条目却包含无法解释的额外扇区，已中止：{file.RelativePath}");
            }

            MinecraftChunkIndexEntry[] retained = indexKnown
                ? indexed.Where(entry => bounds.Contains(entry.Address)).OrderBy(static entry => entry.LocalIndex).ToArray()
                : [];
            CropFileAction action = relation switch
            {
                CropRegionRelation.Outside => CropFileAction.Omit,
                CropRegionRelation.Inside => CropFileAction.Copy,
                _ when retained.Length == indexed.Count && retained.Length > 0 => CropFileAction.Copy,
                _ when retained.Length == 0 => CropFileAction.Omit,
                _ => CropFileAction.Repack,
            };
            actions[file.RelativePath] = action;
            plans.Add(new CropRegionPlan(
                file.RelativePath,
                region,
                kind,
                relation,
                action,
                indexed,
                retained,
                indexKnown));

            Report(
                progress,
                kind == CropRegionKind.Main
                    ? MinecraftWorldCropStage.AnalyzingRegions
                    : MinecraftWorldCropStage.AnalyzingSidecars,
                index + 1,
                regionFiles.Length,
                $"正在分析 {file.RelativePath}");
        }

        HashSet<string> plannedMainPaths = plans
            .Where(static plan => plan.Kind == CropRegionKind.Main)
            .Select(static plan => plan.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        MinecraftChunkIndexEntry? unplanned = allEntries.FirstOrDefault(entry =>
            entry.Address.Dimension == dimension && !plannedMainPaths.Contains(entry.RegionRelativePath));
        if(unplanned is not null)
            throw new InvalidDataException($"目标维度 Region 未进入裁剪计划：{unplanned.RegionRelativePath}");

        HashSet<string> repackedExternalPayloads = new(StringComparer.OrdinalIgnoreCase);
        foreach(CropRegionPlan plan in plans.Where(static plan => plan.Action == CropFileAction.Repack))
        {
            await foreach((MinecraftChunkIndexEntry _, StoredRegionRecord record) in ReadStoredRegionRecordsAsync(
                worldFiles,
                files,
                plan.RetainedEntries,
                includeInlinePayload: false,
                cancellationToken).ConfigureAwait(false))
            {
                if(record.ExternalRelativePath is string external)
                    repackedExternalPayloads.Add(external);
            }
        }

        MinecraftFileFingerprint[] externalFiles = files.Values
            .Where(file => dataDirectories.ContainsKey(NormalizeRelativeDirectory(GetRelativeParent(file.RelativePath))))
            .Where(file => Path.GetExtension(file.RelativePath).Equals(".mcc", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach(MinecraftFileFingerprint file in externalFiles)
        {
            (int chunkX, int chunkZ) = ParseExternalChunkCoordinates(file.RelativePath);
            actions[file.RelativePath] = repackedExternalPayloads.Contains(file.RelativePath)
                ? CropFileAction.WrittenByRegionRepack
                : bounds.Contains(chunkX, chunkZ)
                    ? CropFileAction.Copy
                    : CropFileAction.Omit;
        }

        long sourceMainRegions = plans.LongCount(static plan => plan.Kind == CropRegionKind.Main);
        long removedMainRegions = plans.LongCount(static plan =>
            plan.Kind == CropRegionKind.Main && plan.Action == CropFileAction.Omit);
        long repackedMainRegions = plans.LongCount(static plan =>
            plan.Kind == CropRegionKind.Main && plan.Action == CropFileAction.Repack);
        CropRegionPlan[] sidecars = plans.Where(static plan => plan.Kind != CropRegionKind.Main).ToArray();
        long indexedSidecars = sidecars.Where(static plan => plan.IndexKnown)
            .Sum(static plan => (long)plan.IndexedEntries.Count);
        long removedSidecars = sidecars.Where(static plan => plan.IndexKnown)
            .Sum(plan => plan.IndexedEntries.LongCount(entry => !bounds.Contains(entry.Address)));
        long retainedSidecars = sidecars.Where(static plan => plan.IndexKnown)
            .Sum(plan => plan.IndexedEntries.LongCount(entry => bounds.Contains(entry.Address)));

        return new CropPlanningResult(
            actions,
            plans,
            new HashSet<string>(dataDirectories.Keys, StringComparer.OrdinalIgnoreCase),
            sourceMainRegions,
            removedMainRegions,
            repackedMainRegions,
            sidecars.LongLength,
            sidecars.LongCount(static plan => plan.Action == CropFileAction.Omit),
            sidecars.LongCount(static plan => plan.Action == CropFileAction.Repack),
            indexedSidecars,
            removedSidecars,
            retainedSidecars,
            sidecars.LongCount(static plan => !plan.IndexKnown));
    }

    private static void ValidateRequest(MinecraftWorldCropAnalyzeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.SourceWorld);
        if(string.IsNullOrWhiteSpace(request.ExpectedSourceRevision))
            throw new ArgumentException("ExpectedSourceRevision 不能为空。", nameof(request));
        if(string.IsNullOrWhiteSpace(request.Dimension.Value))
            throw new ArgumentException("Dimension 不能为空。", nameof(request));
        if(!string.Equals(
               request.ExpectedSourceRevision,
               request.SourceWorld.Descriptor.SourceRevision,
               StringComparison.Ordinal))
        {
            throw new InvalidOperationException("请求的源 revision 与已挂载世界不一致。");
        }
        if(request.SourceWorld.Descriptor.SourceRevisionStrength != MinecraftSourceRevisionStrength.ContentHash)
            throw new InvalidOperationException("地图裁剪要求使用内容哈希挂载世界。");
        if(!request.SourceWorld.Descriptor.Dimensions.Any(item => item.Id == request.Dimension))
            throw new ArgumentException($"当前世界不包含维度 {request.Dimension}。", nameof(request));
        if(request.Dimension == MinecraftDimensionId.Overworld)
        {
            if(request.SourceWorld.Descriptor.SpawnLocation is not BlockPosition)
                throw new InvalidOperationException("level.dat 未提供出生点，不能安全裁剪主世界。");
        }
    }

    private static async Task<IReadOnlyDictionary<string, MinecraftFileFingerprint>> CollectFilesAsync(
        IReadOnlyMinecraftWorldFileSource worldFiles,
        CancellationToken cancellationToken)
    {
        Dictionary<string, MinecraftFileFingerprint> result = new(StringComparer.OrdinalIgnoreCase);
        await foreach(MinecraftFileFingerprint file in worldFiles
                           .EnumerateWorldFilesAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            if(!result.TryAdd(file.RelativePath, file))
                throw new InvalidDataException($"挂载世界包含重复文件路径：{file.RelativePath}");
            if(!IsSessionLock(file.RelativePath) && string.IsNullOrWhiteSpace(file.ContentHash))
                throw new InvalidDataException($"挂载世界文件缺少内容哈希：{file.RelativePath}");
        }
        return result;
    }

    private static async Task<IReadOnlyList<MinecraftChunkIndexEntry>> CollectChunkEntriesAsync(
        IReadOnlyMinecraftWorld world,
        CancellationToken cancellationToken)
    {
        List<MinecraftChunkIndexEntry> result = new(world.ChunkIndex.TotalChunkCount);
        foreach(MinecraftDimensionId dimension in world.Descriptor.Dimensions
                    .Select(static item => item.Id)
                    .Distinct()
                    .OrderBy(static item => item.Value, StringComparer.Ordinal))
        {
            await foreach(MinecraftChunkIndexEntry entry in world.ChunkIndex
                               .EnumerateAsync(dimension, cancellationToken: cancellationToken)
                               .ConfigureAwait(false))
            {
                result.Add(entry);
            }
        }
        if(result.Count != world.ChunkIndex.TotalChunkCount)
            throw new InvalidDataException("区块索引总数与逐维度枚举结果不一致。");
        return result;
    }

    private static Dictionary<string, CropRegionKind> BuildTargetDataDirectories(
        MinecraftDimensionDescriptor descriptor)
    {
        Dictionary<string, CropRegionKind> result = new(StringComparer.OrdinalIgnoreCase);
        foreach(string sourceDirectory in descriptor.RegionDirectories)
        {
            string regionDirectory = NormalizeRelativeDirectory(sourceDirectory);
            AddDataDirectory(result, regionDirectory, CropRegionKind.Main);
            string dimensionRoot = GetRelativeParent(regionDirectory);
            AddDataDirectory(result, CombineRelative(dimensionRoot, "entities"), CropRegionKind.Entities);
            AddDataDirectory(result, CombineRelative(dimensionRoot, "poi"), CropRegionKind.PointOfInterest);
        }
        return result;
    }

    private static void AddDataDirectory(
        IDictionary<string, CropRegionKind> directories,
        string path,
        CropRegionKind kind)
    {
        if(directories.TryGetValue(path, out CropRegionKind existing) && existing != kind)
            throw new InvalidDataException($"维度数据目录类型冲突：{path}");
        directories[path] = kind;
    }

    private static (int X, int Z) ParseRegionCoordinates(string relativePath)
    {
        Match match = RegionFileName.Match(Path.GetFileName(relativePath));
        if(!match.Success ||
           !int.TryParse(match.Groups[1].Value, out int x) ||
           !int.TryParse(match.Groups[2].Value, out int z))
        {
            throw new InvalidDataException($"目标维度包含无法定位的 MCA 文件，已中止裁剪：{relativePath}");
        }
        return (x, z);
    }

    private static (int X, int Z) ParseExternalChunkCoordinates(string relativePath)
    {
        Match match = ExternalChunkFileName.Match(Path.GetFileName(relativePath));
        if(!match.Success ||
           !int.TryParse(match.Groups[1].Value, out int x) ||
           !int.TryParse(match.Groups[2].Value, out int z))
        {
            throw new InvalidDataException($"目标维度包含无法定位的 MCC 文件，已中止裁剪：{relativePath}");
        }
        return (x, z);
    }

    private static CropRegionRelation ClassifyRegion(
        int regionX,
        int regionZ,
        MinecraftChunkSquareBounds bounds)
    {
        long minimumX = (long)regionX * MinecraftRegionAddress.ChunksPerAxis;
        long minimumZ = (long)regionZ * MinecraftRegionAddress.ChunksPerAxis;
        long maximumX = minimumX + MinecraftRegionAddress.ChunksPerAxis - 1;
        long maximumZ = minimumZ + MinecraftRegionAddress.ChunksPerAxis - 1;
        if(maximumX < bounds.MinChunkX || minimumX > bounds.MaxChunkX ||
           maximumZ < bounds.MinChunkZ || minimumZ > bounds.MaxChunkZ)
        {
            return CropRegionRelation.Outside;
        }
        if(minimumX >= bounds.MinChunkX && maximumX <= bounds.MaxChunkX &&
           minimumZ >= bounds.MinChunkZ && maximumZ <= bounds.MaxChunkZ)
        {
            return CropRegionRelation.Inside;
        }
        return CropRegionRelation.Boundary;
    }

    private static async Task<IReadOnlyList<MinecraftChunkIndexEntry>> ReadRegionHeaderAsync(
        string sourceRoot,
        MinecraftFileFingerprint fingerprint,
        MinecraftRegionAddress region,
        CancellationToken cancellationToken)
    {
        string path = MinecraftSourceFiles.ResolveRelativePath(sourceRoot, fingerprint.RelativePath);
        List<MinecraftDiagnostic> diagnostics = [];
        return await AnvilChunkIndex.ReadRegionHeaderAsync(
            path,
            region,
            fingerprint,
            diagnostics,
            cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureSameIndex(
        string relativePath,
        IReadOnlyList<MinecraftChunkIndexEntry> mounted,
        IReadOnlyList<MinecraftChunkIndexEntry> audited)
    {
        if(mounted.Count != audited.Count)
            throw new InvalidDataException($"Region 索引数量在裁剪分析期间不一致：{relativePath}");
        MinecraftChunkIndexEntry[] left = mounted.OrderBy(static entry => entry.LocalIndex).ToArray();
        MinecraftChunkIndexEntry[] right = audited.OrderBy(static entry => entry.LocalIndex).ToArray();
        for(int index = 0; index < left.Length; index++)
        {
            if(left[index].Address != right[index].Address ||
               left[index].LocalIndex != right[index].LocalIndex ||
               left[index].SectorIndex != right[index].SectorIndex ||
               left[index].AllocatedSectorCount != right[index].AllocatedSectorCount ||
               left[index].Timestamp != right[index].Timestamp)
            {
                throw new InvalidDataException($"Region 索引在裁剪分析期间不一致：{relativePath}");
            }
        }
    }

    private static void EnsureSameAddressSet(
        string relativePath,
        IReadOnlyList<MinecraftChunkIndexEntry> expected,
        IReadOnlyList<MinecraftChunkIndexEntry> actual)
    {
        MinecraftChunkIndexEntry[] left = expected.OrderBy(static entry => entry.LocalIndex).ToArray();
        MinecraftChunkIndexEntry[] right = actual.OrderBy(static entry => entry.LocalIndex).ToArray();
        if(left.Length != right.Length)
            throw new InvalidDataException($"Region 记录数量与裁剪计划不一致：{relativePath}");
        for(int index = 0; index < left.Length; index++)
        {
            if(left[index].Address != right[index].Address ||
               left[index].LocalIndex != right[index].LocalIndex ||
               left[index].Timestamp != right[index].Timestamp)
            {
                throw new InvalidDataException($"Region 地址集合与裁剪计划不一致：{relativePath}");
            }
        }
    }

    private static async IAsyncEnumerable<(MinecraftChunkIndexEntry Entry, StoredRegionRecord Record)> ReadStoredRegionRecordsAsync(
        IReadOnlyMinecraftWorldFileSource source,
        IReadOnlyDictionary<string, MinecraftFileFingerprint> files,
        IReadOnlyList<MinecraftChunkIndexEntry> entries,
        bool includeInlinePayload,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if(entries.Count == 0) yield break;
        string relativePath = entries[0].RegionRelativePath;
        if(entries.Any(entry => !entry.RegionRelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("一个重打包读取批次包含多个 Region 文件。");
        if(!files.TryGetValue(relativePath, out MinecraftFileFingerprint? regionFile))
            throw new IOException($"Region 文件已不在源快照中：{relativePath}");
        if(string.IsNullOrWhiteSpace(regionFile.ContentHash))
            throw new InvalidDataException($"Region 文件缺少内容哈希：{relativePath}");
        byte[] expectedHash;
        try
        {
            expectedHash = Convert.FromHexString(regionFile.ContentHash);
        }
        catch(FormatException exception)
        {
            throw new InvalidDataException($"Region 文件内容哈希无效：{relativePath}", exception);
        }

        HashSet<int> positions = [];
        byte[] hashBuffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            await using Stream input = await source.OpenWorldFileAsync(regionFile, cancellationToken)
                .ConfigureAwait(false);
            if(!input.CanSeek)
                throw new NotSupportedException($"Region 文件流不支持随机读取：{relativePath}");
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            while(true)
            {
                int read = await input.ReadAsync(hashBuffer.AsMemory(0, hashBuffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if(read == 0) break;
                total = checked(total + read);
                if(total > regionFile.Length)
                    throw new IOException($"Region 文件在读取时增长：{relativePath}");
                hash.AppendData(hashBuffer, 0, read);
            }
            if(total != regionFile.Length ||
               !CryptographicOperations.FixedTimeEquals(expectedHash, hash.GetHashAndReset()))
            {
                throw new IOException($"Region 文件内容在重打包读取时变化：{relativePath}");
            }

            foreach(MinecraftChunkIndexEntry entry in entries.OrderBy(static entry => entry.LocalIndex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                int allocationBytes = checked(entry.AllocatedSectorCount * SectorBytes);
                long offset = checked((long)entry.SectorIndex * SectorBytes);
                if(offset < RegionHeaderBytes || offset + allocationBytes > regionFile.Length)
                    throw new InvalidDataException($"Region 记录越过文件边界：{entry.Address}");
                input.Position = offset;
                byte[] prefix = new byte[5];
                await input.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
                uint encodedLength = BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(0, sizeof(uint)));
                if(encodedLength < 1 || (long)encodedLength + sizeof(uint) > allocationBytes)
                    throw new InvalidDataException($"Region 记录长度字段无效：{entry.Address}");
                bool external = (prefix[4] & 0x80) != 0;
                byte[] record = prefix;
                if(includeInlinePayload && !external)
                {
                    record = GC.AllocateUninitializedArray<byte>(checked((int)encodedLength + sizeof(uint)));
                    prefix.CopyTo(record, 0);
                    await input.ReadExactlyAsync(record.AsMemory(prefix.Length), cancellationToken).ConfigureAwait(false);
                }
                StoredRegionRecord stored = ParseStoredRegionRecord(files, entry, record, allocationBytes);
                if(!positions.Add(entry.LocalIndex))
                    throw new InvalidDataException($"Region 包含重复位置条目：{relativePath} #{entry.LocalIndex}");
                yield return (entry, stored);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(hashBuffer);
        }
    }

    private static StoredRegionRecord ParseStoredRegionRecord(
        IReadOnlyDictionary<string, MinecraftFileFingerprint> files,
        MinecraftChunkIndexEntry entry,
        byte[] allocation,
        int allocationBytes)
    {
        if(allocation.Length < 5)
            throw new InvalidDataException($"Region 记录过短：{entry.Address}");
        uint encodedLength = BinaryPrimitives.ReadUInt32BigEndian(allocation.AsSpan(0, sizeof(uint)));
        byte compressionByte = allocation[4];
        bool external = (compressionByte & 0x80) != 0;
        byte compression = (byte)(compressionByte & 0x7f);
        if(compression is < 1 or > 4)
            throw new InvalidDataException($"Region 记录压缩类型无效：{entry.Address}");

        if(external)
        {
            if(encodedLength != 1)
                throw new InvalidDataException($"外部 Region 记录长度字段无效：{entry.Address}");
            string externalRelative = CombineRelative(
                GetRelativeParent(entry.RegionRelativePath),
                $"c.{entry.Address.X}.{entry.Address.Z}.mcc");
            if(!files.TryGetValue(externalRelative, out MinecraftFileFingerprint? externalFile))
                throw new InvalidDataException($"外部区块缺少 MCC 文件：{externalRelative}");
            if(externalFile.Length <= 0 || string.IsNullOrEmpty(externalFile.ContentHash))
                throw new InvalidDataException($"外部区块负载为空或缺少内容哈希：{externalRelative}");
            // The MCC is hashed during the world stability check and streamed with a hash
            // while copying. Never retain a potentially multi-gigabyte external payload.
            return new StoredRegionRecord(true, compression, ReadOnlyMemory<byte>.Empty, externalRelative);
        }

        if(encodedLength < 2 || (long)encodedLength + sizeof(uint) > allocationBytes)
            throw new InvalidDataException($"内联 Region 记录长度字段无效：{entry.Address}");
        return new StoredRegionRecord(false, compression, allocation, null);
    }

    private static async Task<RegionRepackResult> RepackRegionAsync(
        string staging,
        IReadOnlyMinecraftWorldFileSource source,
        IReadOnlyDictionary<string, MinecraftFileFingerprint> files,
        CropRegionPlan plan,
        CancellationToken cancellationToken)
    {
        if(plan.RetainedEntries.Count == 0)
            throw new ArgumentException("重打包 Region 至少需要一个保留记录。", nameof(plan));
        string outputPath = ResolveStagePath(staging, plan.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        byte[] header = new byte[RegionHeaderBytes];
        long bytesRead = 0;
        long externalCount = 0;
        long outputBytes;

        await using(FileStream output = new(
                        outputPath,
                        FileMode.CreateNew,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        CopyBufferBytes,
                        FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            int nextSector = 2;
            byte[] padding = new byte[SectorBytes];
            await foreach((MinecraftChunkIndexEntry entry, StoredRegionRecord stored) in ReadStoredRegionRecordsAsync(
                source, files, plan.RetainedEntries, includeInlinePayload: true, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadOnlyMemory<byte> record;
                int sectors;
                if(stored.IsExternal)
                {
                    byte[] externalPrefix = new byte[5];
                    BinaryPrimitives.WriteUInt32BigEndian(externalPrefix.AsSpan(0, sizeof(uint)), 1);
                    externalPrefix[4] = (byte)(stored.CompressionId | 0x80);
                    record = externalPrefix;
                    sectors = 1;
                    string externalRelative = stored.ExternalRelativePath ??
                                              throw new InvalidDataException($"外部记录缺少 MCC 路径：{entry.Address}");
                    string externalOutput = ResolveStagePath(staging, externalRelative);
                    Directory.CreateDirectory(Path.GetDirectoryName(externalOutput)!);
                    MinecraftFileFingerprint externalFingerprint = files[externalRelative];
                    bytesRead = checked(bytesRead + await CopyAndVerifyAsync(
                        source, externalFingerprint, externalOutput, cancellationToken).ConfigureAwait(false));
                    File.SetLastWriteTimeUtc(externalOutput, externalFingerprint.LastWriteTimeUtc.UtcDateTime);
                    externalCount++;
                }
                else
                {
                    sectors = checked((stored.Bytes.Length + SectorBytes - 1) / SectorBytes);
                    if(sectors is <= 0 or > byte.MaxValue)
                        throw new InvalidDataException($"内联 Region 记录扇区数无效：{entry.Address}");
                    record = stored.Bytes;
                }
                bytesRead = checked(bytesRead + stored.Bytes.Length);

                if(nextSector > 0x00ff_ffff || (long)nextSector + sectors > 0x0100_0000)
                    throw new InvalidDataException($"Region 扇区偏移超过 MCA 24 位上限：{plan.RelativePath}");
                await output.WriteAsync(record, cancellationToken).ConfigureAwait(false);
                int paddingLength = checked(sectors * SectorBytes - record.Length);
                if(paddingLength > 0) await output.WriteAsync(padding.AsMemory(0, paddingLength), cancellationToken).ConfigureAwait(false);
                int headerOffset = entry.LocalIndex * 4;
                header[headerOffset] = (byte)(nextSector >> 16);
                header[headerOffset + 1] = (byte)(nextSector >> 8);
                header[headerOffset + 2] = (byte)nextSector;
                header[headerOffset + 3] = checked((byte)sectors);
                BinaryPrimitives.WriteUInt32BigEndian(
                    header.AsSpan(SectorBytes + headerOffset, sizeof(uint)),
                    entry.Timestamp);
                nextSector = checked(nextSector + sectors);
            }

            output.Position = 0;
            await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            outputBytes = output.Length;
        }

        if(files.TryGetValue(plan.RelativePath, out MinecraftFileFingerprint? sourceRegion))
            File.SetLastWriteTimeUtc(outputPath, sourceRegion.LastWriteTimeUtc.UtcDateTime);
        return new RegionRepackResult(bytesRead, outputBytes, externalCount);
    }

    private static async Task WritePayloadAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using FileStream output = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private static async Task ValidateStagingAsync(
        string staging,
        MinecraftWorldCropAnalysis analysis,
        CancellationToken cancellationToken)
    {
        await using IReadOnlyMinecraftWorld staged = await new AnvilWorldMounter().MountAsync(
            new ReadOnlyWorldMountRequest(
                staging,
                ConcurrentWorldWritePolicy.RejectActiveWriter,
                IncludeCustomDimensions: true,
                CaptureSourceFingerprints: true),
            cancellationToken).ConfigureAwait(false);

        HashSet<MinecraftChunkAddress> expectedChunks = analysis.Entries
            .Where(entry => entry.Address.Dimension != analysis.Dimension || analysis.Bounds.Contains(entry.Address))
            .Select(static entry => entry.Address)
            .ToHashSet();
        HashSet<MinecraftChunkAddress> actualChunks = [];
        foreach(MinecraftDimensionDescriptor dimension in staged.Descriptor.Dimensions)
        {
            await foreach(MinecraftChunkIndexEntry entry in staged.ChunkIndex
                               .EnumerateAsync(dimension.Id, cancellationToken: cancellationToken)
                               .ConfigureAwait(false))
            {
                actualChunks.Add(entry.Address);
            }
        }
        if(!actualChunks.SetEquals(expectedChunks))
            throw new InvalidDataException("裁剪后世界的主 Chunk 地址集合与计划不一致。");

        if(analysis.RelocatedSpawnLocation is BlockPosition expectedSpawn &&
           staged.Descriptor.SpawnLocation != expectedSpawn)
        {
            throw new InvalidDataException(
                $"裁剪后出生点未正确迁移到选区中心 ({expectedSpawn.X}, {expectedSpawn.Y}, {expectedSpawn.Z})。");
        }

        IReadOnlyMinecraftWorldFileSource stagedFiles = staged as IReadOnlyMinecraftWorldFileSource ??
            throw new NotSupportedException("裁剪后世界未提供完整文件源，无法完成验证。");
        IReadOnlyDictionary<string, MinecraftFileFingerprint> actualFiles = await CollectFilesAsync(
            stagedFiles,
            cancellationToken).ConfigureAwait(false);
        HashSet<string> expectedPaths = analysis.FileActions
            .Where(static item => item.Value != CropFileAction.Omit)
            .Select(static item => item.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> actualPaths = actualFiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(!actualPaths.SetEquals(expectedPaths))
            throw new InvalidDataException("裁剪后世界的文件集合与计划不一致。");

        foreach((string relativePath, CropFileAction action) in analysis.FileActions)
        {
            if(action != CropFileAction.Copy) continue;
            if(analysis.RelocatedSpawnLocation is not null && IsLevelMetadata(relativePath)) continue;
            MinecraftFileFingerprint expected = analysis.Files[relativePath];
            MinecraftFileFingerprint actual = actualFiles[relativePath];
            if(expected.Length != actual.Length ||
               string.IsNullOrWhiteSpace(expected.ContentHash) ||
               !string.Equals(expected.ContentHash, actual.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"应原样保留的文件内容发生变化：{relativePath}");
            }
        }

        foreach(CropRegionPlan plan in analysis.RegionPlans)
        {
            string outputPath = MinecraftSourceFiles.ResolveRelativePath(staging, plan.RelativePath);
            if(plan.Action == CropFileAction.Omit)
            {
                if(File.Exists(outputPath))
                    throw new InvalidDataException($"应移除的 Region 仍然存在：{plan.RelativePath}");
                continue;
            }
            if(plan.Action != CropFileAction.Repack) continue;
            MinecraftFileFingerprint fingerprint = actualFiles[plan.RelativePath];
            IReadOnlyList<MinecraftChunkIndexEntry> entries = await ReadRegionHeaderAsync(
                staging,
                fingerprint,
                plan.Region,
                cancellationToken).ConfigureAwait(false);
            EnsureSameAddressSet(plan.RelativePath, plan.RetainedEntries, entries);
            if(entries.Any(entry => !analysis.Bounds.Contains(entry.Address)))
                throw new InvalidDataException($"重打包 Region 仍包含边界外记录：{plan.RelativePath}");
        }

        foreach((string relativePath, CropFileAction action) in analysis.FileActions)
        {
            if(!Path.GetExtension(relativePath).Equals(".mcc", StringComparison.OrdinalIgnoreCase)) continue;
            if(!IsTargetDataFile(analysis, relativePath)) continue;
            if(action == CropFileAction.Omit && actualFiles.ContainsKey(relativePath))
                throw new InvalidDataException($"应移除的 MCC 仍然存在：{relativePath}");
            if(action != CropFileAction.Omit)
            {
                (int x, int z) = ParseExternalChunkCoordinates(relativePath);
                if(!analysis.Bounds.Contains(x, z))
                    throw new InvalidDataException($"目标维度仍包含边界外 MCC：{relativePath}");
            }
        }

        WorldSourceValidationResult validation = await staged.ValidateSourceAsync(cancellationToken)
            .ConfigureAwait(false);
        if(!validation.IsStable)
            throw new IOException("裁剪后世界在验证期间发生变化。");
    }

    private static bool IsTargetDataFile(MinecraftWorldCropAnalysis analysis, string relativePath)
    {
        string parent = NormalizeRelativeDirectory(GetRelativeParent(relativePath));
        return analysis.TargetDataDirectories.Contains(parent);
    }

    private static bool IsLevelMetadata(string relativePath) =>
        relativePath.Equals("level.dat", StringComparison.OrdinalIgnoreCase) ||
        relativePath.Equals("level.dat_old", StringComparison.OrdinalIgnoreCase);

    private static long EstimateReclaimableBytes(
        IReadOnlyDictionary<string, MinecraftFileFingerprint> files,
        IReadOnlyDictionary<string, CropFileAction> actions,
        IReadOnlyList<CropRegionPlan> plans)
    {
        long result = 0;
        foreach((string relativePath, CropFileAction action) in actions)
        {
            if(action == CropFileAction.Omit && files.TryGetValue(relativePath, out MinecraftFileFingerprint? file))
                result = checked(result + file.Length);
        }
        foreach(CropRegionPlan plan in plans.Where(static plan => plan.Action == CropFileAction.Repack))
        {
            foreach(MinecraftChunkIndexEntry entry in plan.IndexedEntries)
            {
                if(plan.RetainedEntries.Any(retained => retained.Address == entry.Address)) continue;
                result = checked(result + (long)entry.AllocatedSectorCount * SectorBytes);
            }
        }
        return result;
    }

    private static async Task<long> CopyAndVerifyAsync(
        IReadOnlyMinecraftWorldFileSource source,
        MinecraftFileFingerprint file,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if(string.IsNullOrWhiteSpace(file.ContentHash))
            throw new InvalidDataException($"源文件缺少内容哈希：{file.RelativePath}");
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(file.ContentHash);
        }
        catch(FormatException exception)
        {
            throw new InvalidDataException($"源文件内容哈希无效：{file.RelativePath}", exception);
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using Stream input = await source.OpenWorldFileAsync(file, cancellationToken).ConfigureAwait(false);
            await using FileStream output = new(
                outputPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            long total = 0;
            while(true)
            {
                int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if(read == 0) break;
                total = checked(total + read);
                if(total > file.Length)
                    throw new IOException($"源文件在复制时增长：{file.RelativePath}");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            if(total != file.Length)
                throw new IOException($"源文件长度在复制时发生变化：{file.RelativePath}");
            if(!CryptographicOperations.FixedTimeEquals(expected, hash.GetHashAndReset()))
                throw new IOException($"源文件内容在复制时发生变化：{file.RelativePath}");
            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task EnsureSourceStableAsync(
        IReadOnlyMinecraftWorld world,
        CancellationToken cancellationToken)
    {
        WorldSourceValidationResult validation = await world.ValidateSourceAsync(cancellationToken)
            .ConfigureAwait(false);
        if(validation.IsStable) return;
        string changed = validation.ChangedRelativePaths.Count == 0
            ? "源世界无法验证"
            : string.Join("、", validation.ChangedRelativePaths.Take(8));
        throw new IOException($"源世界在地图裁剪期间发生变化：{changed}");
    }

    private static string ResolveStagePath(string staging, string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/');
        string output = MinecraftSourceFiles.ResolveRelativePath(staging, normalized);
        string roundTrip = MinecraftSourceFiles.NormalizeRelativePath(staging, output);
        if(!roundTrip.Equals(normalized, StringComparison.Ordinal))
            throw new InvalidDataException($"世界文件路径不是规范相对路径：{relativePath}");
        return output;
    }

    private static long GetDirectoryFileBytes(string path)
    {
        long result = 0;
        foreach(string file in MinecraftSourceFiles.EnumerateWorldFiles(path))
            result = checked(result + new FileInfo(file).Length);
        return result;
    }

    private static bool IsSessionLock(string relativePath) =>
        relativePath.Equals("session.lock", StringComparison.OrdinalIgnoreCase);

    private static string GetRelativeParent(string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/').Trim('/');
        int separator = normalized.LastIndexOf('/');
        return separator < 0 ? string.Empty : normalized[..separator];
    }

    private static string CombineRelative(string parent, string name) =>
        parent.Length == 0 ? name : $"{parent}/{name}";

    private static string NormalizeRelativeDirectory(string path) =>
        path.Replace('\\', '/').Trim('/');

    private static int FloorDiv(int value, int divisor)
    {
        int quotient = value / divisor;
        int remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private static bool IsSafePlanningFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or
        ArgumentException or InvalidOperationException or OverflowException or CryptographicException;

    private static bool ShouldReport(long completed, long total) =>
        completed == 1 || completed == total || completed % 16 == 0;

    private static void Report(
        IProgress<MinecraftWorldCropProgress>? progress,
        MinecraftWorldCropStage stage,
        long completed,
        long total,
        string? message,
        long bytesRead = 0,
        long bytesWritten = 0,
        MinecraftChunkAddress? currentChunk = null)
    {
        if(progress is null) return;
        try
        {
            progress.Report(new MinecraftWorldCropProgress(
                stage,
                completed,
                total,
                message,
                bytesRead,
                bytesWritten,
                currentChunk));
        }
        catch
        {
            // Progress is observational and cannot make a safe world transaction fail.
        }
    }

    private static Exception? TryDeleteStaging(string staging, string expectedParent)
    {
        try
        {
            if(!Directory.Exists(staging)) return null;
            string resolved = Path.GetFullPath(staging);
            string parent = Path.GetDirectoryName(resolved) ?? string.Empty;
            string name = Path.GetFileName(resolved);
            if(!parent.Equals(Path.GetFullPath(expectedParent), StringComparison.OrdinalIgnoreCase) ||
               !name.Contains(".zjj-crop-staging-", StringComparison.Ordinal) ||
               (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
            {
                return new InvalidOperationException($"拒绝递归删除未验证的裁剪临时目录：{resolved}");
            }
            PreparedWorldDirectoryTransaction.DeleteDirectoryTreeWithoutFollowingReparsePoints(resolved);
            return null;
        }
        catch(Exception exception)
        {
            return exception;
        }
    }

    private sealed record CropPlanningResult(
        IReadOnlyDictionary<string, CropFileAction> FileActions,
        IReadOnlyList<CropRegionPlan> RegionPlans,
        IReadOnlySet<string> TargetDataDirectories,
        long SourceMainRegionCount,
        long RemovedMainRegionCount,
        long RepackedMainRegionCount,
        long SidecarRegionCount,
        long RemovedSidecarRegionCount,
        long RepackedSidecarRegionCount,
        long IndexedSidecarRecordCount,
        long RemovedSidecarRecordCount,
        long RetainedSidecarRecordCount,
        long UnknownSidecarRegionCount);

    private sealed record StoredRegionRecord(
        bool IsExternal,
        byte CompressionId,
        ReadOnlyMemory<byte> Bytes,
        string? ExternalRelativePath);

    private readonly record struct RegionRepackResult(
        long BytesRead,
        long BytesWritten,
        long ExternalChunkCount);
}
