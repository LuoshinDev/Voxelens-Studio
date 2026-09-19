using System.Buffers;
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ZhuJieJing.Minecraft;

/// <summary>
/// Requests a conservative slimming analysis of one content-fingerprinted, read-only world mount.
/// A chunk is removable only when its normalized block content is entirely air and no safety signal remains.
/// </summary>
public sealed record MinecraftWorldSlimmingAnalyzeRequest(
    IReadOnlyMinecraftWorld SourceWorld,
    string ExpectedSourceRevision,
    int SpawnRetentionRadiusChunks = 1,
    int AnalysisConcurrency = MinecraftWorldSlimmingService.DefaultAnalysisConcurrency);

public enum MinecraftWorldSlimmingStage
{
    ValidatingSource,
    AuditingRegionPayloads,
    AnalyzingCompanionData,
    AnalyzingChunks,
    PreparingStaging,
    CopyingFiles,
    RepackingRegions,
    ValidatingStaging,
    ReadyToCommit,
}

/// <summary>Progress shared by analysis and staging preparation.</summary>
public sealed record MinecraftWorldSlimmingProgress(
    MinecraftWorldSlimmingStage Stage,
    long CompletedItems,
    long TotalItems,
    string? Message = null,
    long BytesRead = 0,
    long BytesWritten = 0,
    MinecraftChunkAddress? CurrentChunk = null);

/// <summary>
/// Immutable decision produced from one exact source revision. The internal address set cannot be widened by UI code.
/// </summary>
public sealed class MinecraftWorldSlimmingAnalysis
{
    internal MinecraftWorldSlimmingAnalysis(
        IReadOnlyMinecraftWorld sourceWorld,
        IReadOnlyMinecraftWorldFileSource worldFiles,
        IReadOnlyDictionary<string, MinecraftFileFingerprint> files,
        IReadOnlyList<MinecraftChunkIndexEntry> entries,
        IReadOnlySet<MinecraftChunkAddress> removableChunks,
        IReadOnlySet<MinecraftRegionAddress> repackableRegions,
        IReadOnlySet<string> managedPrimaryPayloads,
        long estimatedReclaimableBytes,
        long protectedBySpawnCount,
        long protectedBySidecarCount,
        long protectedByNonAirCount,
        long protectedByActivityCount,
        long protectedByUncertaintyCount,
        IReadOnlyList<MinecraftDiagnostic> diagnostics)
    {
        SourceWorld = sourceWorld;
        WorldFiles = worldFiles;
        Files = files;
        Entries = entries;
        RemovableChunks = removableChunks;
        RepackableRegions = repackableRegions;
        ManagedPrimaryPayloads = managedPrimaryPayloads;
        SourceDirectory = sourceWorld.Descriptor.RootPath;
        SourceRevision = sourceWorld.Descriptor.SourceRevision;
        TotalChunkCount = entries.Count;
        RemovableChunkCount = removableChunks.Count;
        RetainedChunkCount = entries.Count - removableChunks.Count;
        EstimatedReclaimableBytes = estimatedReclaimableBytes;
        ProtectedBySpawnCount = protectedBySpawnCount;
        ProtectedBySidecarCount = protectedBySidecarCount;
        ProtectedByNonAirCount = protectedByNonAirCount;
        ProtectedByActivityCount = protectedByActivityCount;
        ProtectedByUncertaintyCount = protectedByUncertaintyCount;
        Diagnostics = diagnostics;
    }

    public string SourceDirectory { get; }

    public string SourceRevision { get; }

    public long TotalChunkCount { get; }

    public long RemovableChunkCount { get; }

    public long RetainedChunkCount { get; }

    /// <summary>Conservative lower-bound estimate based on removed MCA allocations and MCC payloads.</summary>
    public long EstimatedReclaimableBytes { get; }

    public long ProtectedBySpawnCount { get; }

    public long ProtectedBySidecarCount { get; }

    public long ProtectedByNonAirCount { get; }

    public long ProtectedByActivityCount { get; }

    public long ProtectedByUncertaintyCount { get; }

    public IReadOnlyList<MinecraftDiagnostic> Diagnostics { get; }

    internal IReadOnlyMinecraftWorld SourceWorld { get; }

    internal IReadOnlyMinecraftWorldFileSource WorldFiles { get; }

    internal IReadOnlyDictionary<string, MinecraftFileFingerprint> Files { get; }

    internal IReadOnlyList<MinecraftChunkIndexEntry> Entries { get; }

    internal IReadOnlySet<MinecraftChunkAddress> RemovableChunks { get; }

    internal IReadOnlySet<MinecraftRegionAddress> RepackableRegions { get; }

    internal IReadOnlySet<string> ManagedPrimaryPayloads { get; }
}

public sealed record MinecraftWorldSlimmingResult(
    string SourceDirectory,
    string SourceRevision,
    string StagingDirectory,
    long SourceBytes,
    long StagedBytes,
    long BytesFreed,
    long RemovedChunkCount,
    long RetainedChunkCount,
    long RepackedRegionCount,
    long RemovedRegionCount,
    long RetainedExternalChunkCount,
    IReadOnlyList<MinecraftDiagnostic> Diagnostics);

public enum PreparedMinecraftWorldSlimmingState
{
    Prepared,
    CommitFailedNeedsRecovery,
    Committed,
    Finalized,
    RolledBack,
}

/// <summary>
/// Prepared same-volume directory transaction. Commit, finalize, and rollback are intentionally non-cancellable.
/// The caller must dispose the old mount before Commit, remount the new world, then call FinalizeCommit. A failed
/// remount must call Rollback. The rollback directory is temporary transaction state, not a user backup.
/// </summary>
public sealed class PreparedMinecraftWorldSlimming : IAsyncDisposable
{
    private readonly object transitionGate = new();
    private readonly string parentDirectory;
    private readonly string rollbackDirectory;
    private PreparedMinecraftWorldSlimmingState state = PreparedMinecraftWorldSlimmingState.Prepared;

    internal PreparedMinecraftWorldSlimming(
        MinecraftWorldSlimmingResult result,
        string rollbackDirectory,
        IReadOnlyDictionary<string, MinecraftFileFingerprint> sourceFiles)
    {
        Result = result;
        this.rollbackDirectory = rollbackDirectory;
        SourceFiles = sourceFiles;
        parentDirectory = Path.GetDirectoryName(result.SourceDirectory) ??
                          throw new InvalidDataException("瘦身源世界必须有父目录。");
    }

    public MinecraftWorldSlimmingResult Result { get; }

    public string SourceDirectory => Result.SourceDirectory;

    public string StagingDirectory => Result.StagingDirectory;

    public PreparedMinecraftWorldSlimmingState State
    {
        get
        {
            lock(transitionGate) return state;
        }
    }

    /// <summary>Non-fatal cleanup warning recorded only after the authoritative world is already safe.</summary>
    public string? CleanupWarning { get; private set; }

    public string? ResidualDirectoryPath { get; private set; }

    internal IReadOnlyDictionary<string, MinecraftFileFingerprint> SourceFiles { get; }

    /// <summary>Publishes the staged world. Do not call while the old world mount is still in use.</summary>
    public void Commit()
    {
        lock(transitionGate)
        {
            EnsureState(PreparedMinecraftWorldSlimmingState.Prepared);
            EnsureSafeTransactionPath(SourceDirectory, parentDirectory, expectedName: null, mustExist: true);
            EnsureSafeTransactionPath(
                StagingDirectory,
                parentDirectory,
                ".zjj-slimming-staging-",
                mustExist: true);
            EnsureSafeTransactionPath(
                rollbackDirectory,
                parentDirectory,
                ".zjj-slimming-rollback-",
                mustExist: false);
            EnsureDirectoryTreeHasNoReparsePoints(SourceDirectory);
            EnsureSourceContentStillMatches(SourceDirectory, SourceFiles);
            EnsureWorldIsNotActivelyLocked(SourceDirectory);

            WindowsDirectoryMove.Move(SourceDirectory, rollbackDirectory);
            state = PreparedMinecraftWorldSlimmingState.CommitFailedNeedsRecovery;
            try
            {
                WindowsDirectoryMove.Move(StagingDirectory, SourceDirectory);
                state = PreparedMinecraftWorldSlimmingState.Committed;
            }
            catch(Exception commitFailure)
            {
                try
                {
                    WindowsDirectoryMove.Move(rollbackDirectory, SourceDirectory);
                    state = PreparedMinecraftWorldSlimmingState.Prepared;
                }
                catch(Exception restoreFailure)
                {
                    throw new AggregateException(
                        $"瘦身提交失败；原世界仍位于临时回滚目录 {rollbackDirectory}，自动恢复也失败。",
                        commitFailure,
                        restoreFailure);
                }

                throw;
            }
        }
    }

    /// <summary>Deletes temporary rollback state after the caller has successfully remounted the new world.</summary>
    public void FinalizeCommit()
    {
        lock(transitionGate)
        {
            EnsureState(PreparedMinecraftWorldSlimmingState.Committed);
            EnsureSafeTransactionPath(SourceDirectory, parentDirectory, expectedName: null, mustExist: true);
            // From this point onward the newly remounted world is authoritative. Marking the transaction final
            // before cleanup prevents a partial recursive deletion from ever making the old directory eligible
            // for rollback over a verified new world.
            state = PreparedMinecraftWorldSlimmingState.Finalized;
            try
            {
                DeleteVerifiedTransactionDirectory(
                    rollbackDirectory,
                    parentDirectory,
                    ".zjj-slimming-rollback-");
            }
            catch(Exception exception)
            {
                ResidualDirectoryPath = rollbackDirectory;
                CleanupWarning =
                    $"瘦身结果已经生效，但临时回滚目录未能完整清理：{rollbackDirectory}；{exception.Message}";
                throw new IOException(
                    $"瘦身结果已经生效，但临时回滚目录未能完整清理：{rollbackDirectory}",
                    exception);
            }
        }
    }

    /// <summary>Restores the original directory after a failed post-commit remount, or discards an uncommitted stage.</summary>
    public void Rollback()
    {
        lock(transitionGate)
        {
            if(state == PreparedMinecraftWorldSlimmingState.RolledBack) return;
            if(state == PreparedMinecraftWorldSlimmingState.Finalized)
                throw new InvalidOperationException("瘦身事务已经完成，临时回滚目录已删除。");

            if(state == PreparedMinecraftWorldSlimmingState.Prepared)
            {
                DeleteVerifiedTransactionDirectory(
                    StagingDirectory,
                    parentDirectory,
                    ".zjj-slimming-staging-");
                state = PreparedMinecraftWorldSlimmingState.RolledBack;
                return;
            }

            if(state == PreparedMinecraftWorldSlimmingState.CommitFailedNeedsRecovery)
            {
                EnsureSafeTransactionPath(
                    rollbackDirectory,
                    parentDirectory,
                    ".zjj-slimming-rollback-",
                    mustExist: true);
                if(Directory.Exists(SourceDirectory) || File.Exists(SourceDirectory))
                {
                    throw new IOException(
                        $"提交失败后世界路径已被占用，未自动覆盖；原世界仍位于 {rollbackDirectory}。");
                }
                WindowsDirectoryMove.Move(rollbackDirectory, SourceDirectory);
                state = PreparedMinecraftWorldSlimmingState.RolledBack;
                TryCleanupAfterWorldIsSafe(
                    StagingDirectory,
                    ".zjj-slimming-staging-");
                return;
            }

            EnsureSafeTransactionPath(
                rollbackDirectory,
                parentDirectory,
                ".zjj-slimming-rollback-",
                mustExist: true);
            string failedOutput = StagingDirectory;
            EnsureSafeTransactionPath(
                failedOutput,
                parentDirectory,
                ".zjj-slimming-staging-",
                mustExist: false);

            bool movedFailedOutput = false;
            if(Directory.Exists(SourceDirectory))
            {
                EnsureSafeTransactionPath(SourceDirectory, parentDirectory, expectedName: null, mustExist: true);
                WindowsDirectoryMove.Move(SourceDirectory, failedOutput);
                movedFailedOutput = true;
            }
            else if(File.Exists(SourceDirectory))
            {
                throw new IOException($"世界路径被文件占用，无法回滚：{SourceDirectory}");
            }

            try
            {
                WindowsDirectoryMove.Move(rollbackDirectory, SourceDirectory);
            }
            catch(Exception restoreFailure)
            {
                if(movedFailedOutput && !Directory.Exists(SourceDirectory))
                {
                    try
                    {
                        WindowsDirectoryMove.Move(failedOutput, SourceDirectory);
                    }
                    catch(Exception republishFailure)
                    {
                        throw new AggregateException(
                            $"恢复原世界失败；原世界仍位于 {rollbackDirectory}，新世界位于 {failedOutput}。",
                            restoreFailure,
                            republishFailure);
                    }
                }
                throw;
            }

            state = PreparedMinecraftWorldSlimmingState.RolledBack;
            if(movedFailedOutput)
            {
                TryCleanupAfterWorldIsSafe(
                    failedOutput,
                    ".zjj-slimming-staging-");
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock(transitionGate)
        {
            if(state == PreparedMinecraftWorldSlimmingState.Prepared)
            {
                DeleteVerifiedTransactionDirectory(
                    StagingDirectory,
                    parentDirectory,
                    ".zjj-slimming-staging-");
                state = PreparedMinecraftWorldSlimmingState.RolledBack;
            }

            // A committed world is never silently swapped back during disposal. The caller must explicitly
            // finalize after a successful remount or roll back after a failed remount.
        }
        return ValueTask.CompletedTask;
    }

    private void EnsureState(PreparedMinecraftWorldSlimmingState expected)
    {
        if(state != expected)
            throw new InvalidOperationException($"瘦身事务当前为 {state}，要求状态为 {expected}。");
    }

    private void TryCleanupAfterWorldIsSafe(string path, string expectedName)
    {
        try
        {
            DeleteVerifiedTransactionDirectory(path, parentDirectory, expectedName);
        }
        catch(Exception exception)
        {
            ResidualDirectoryPath = path;
            CleanupWarning = $"原世界已经恢复，但临时目录未能清理：{path}；{exception.Message}";
        }
    }

    private static void EnsureSourceContentStillMatches(
        string sourceDirectory,
        IReadOnlyDictionary<string, MinecraftFileFingerprint> sourceFiles)
    {
        IReadOnlyList<string> currentPaths = MinecraftSourceFiles.EnumerateWorldFiles(sourceDirectory);
        HashSet<string> currentRelative = currentPaths
            .Select(path => MinecraftSourceFiles.NormalizeRelativePath(sourceDirectory, path))
            .Where(static path => !IsSessionLock(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] expectedRelative = sourceFiles.Keys
            .Where(static path => !IsSessionLock(path))
            .ToArray();
        if(currentRelative.Count != expectedRelative.Length || expectedRelative.Any(path => !currentRelative.Contains(path)))
            throw new IOException("源世界文件集合在准备完成后发生变化，拒绝提交瘦身结果。");
        foreach(MinecraftFileFingerprint fingerprint in sourceFiles.Values.Where(
                    static file => !IsSessionLock(file.RelativePath)))
        {
            if(!MinecraftSourceFiles.MatchesFingerprintMetadata(sourceDirectory, fingerprint))
                throw new IOException($"源世界文件在准备完成后发生变化：{fingerprint.RelativePath}");
            if(string.IsNullOrWhiteSpace(fingerprint.ContentHash))
                throw new InvalidDataException($"源世界文件缺少内容哈希：{fingerprint.RelativePath}");

            byte[] expected;
            try
            {
                expected = Convert.FromHexString(fingerprint.ContentHash);
            }
            catch(FormatException exception)
            {
                throw new InvalidDataException($"源世界文件内容哈希无效：{fingerprint.RelativePath}", exception);
            }
            string fullPath = MinecraftSourceFiles.ResolveRelativePath(sourceDirectory, fingerprint.RelativePath);
            using FileStream stream = MinecraftSourceFiles.OpenRead(
                fullPath,
                FileOptions.SequentialScan);
            byte[] actual = SHA256.HashData(stream);
            if(!CryptographicOperations.FixedTimeEquals(expected, actual) ||
               !MinecraftSourceFiles.MatchesFingerprintMetadata(sourceDirectory, fingerprint))
            {
                throw new IOException($"源世界文件内容在准备完成后发生变化：{fingerprint.RelativePath}");
            }
        }
    }

    private static void EnsureWorldIsNotActivelyLocked(string sourceDirectory)
    {
        string path = Path.Combine(sourceDirectory, "session.lock");
        if(!File.Exists(path)) return;
        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None,
                1,
                FileOptions.RandomAccess);
            _ = stream.Length;
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException("世界可能仍被 Minecraft 打开，已拒绝提交瘦身结果。", exception);
        }
    }

    private static bool IsSessionLock(string relativePath) =>
        relativePath.Equals("session.lock", StringComparison.OrdinalIgnoreCase);

    private static void EnsureSafeTransactionPath(
        string path,
        string expectedParent,
        string? expectedName,
        bool mustExist)
    {
        string resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string parent = Path.GetDirectoryName(resolved) ?? string.Empty;
        string name = Path.GetFileName(resolved);
        if(!parent.Equals(Path.GetFullPath(expectedParent), StringComparison.OrdinalIgnoreCase) ||
           name.Length == 0 ||
           expectedName is not null && !name.Contains(expectedName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"拒绝操作未验证的瘦身事务路径：{resolved}");
        }
        if(mustExist)
        {
            if(!Directory.Exists(resolved) || File.Exists(resolved))
                throw new DirectoryNotFoundException($"瘦身事务目录不存在：{resolved}");
            if((File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"瘦身事务目录不能是重解析点：{resolved}");
        }
        else if(Directory.Exists(resolved) || File.Exists(resolved))
        {
            throw new IOException($"瘦身事务路径已存在：{resolved}");
        }
    }

    private static void DeleteVerifiedTransactionDirectory(
        string path,
        string expectedParent,
        string expectedName)
    {
        if(!Directory.Exists(path)) return;
        EnsureSafeTransactionPath(path, expectedParent, expectedName, mustExist: true);
        DeleteDirectoryTreeWithoutFollowingReparsePoints(path);
    }

    internal static void EnsureDirectoryTreeHasNoReparsePoints(string rootPath)
    {
        string root = Path.GetFullPath(rootPath);
        Stack<string> directories = new();
        directories.Push(root);
        while(directories.Count > 0)
        {
            string directory = directories.Pop();
            FileAttributes directoryAttributes = File.GetAttributes(directory);
            if((directoryAttributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"地图瘦身不接受重解析点目录：{directory}");
            foreach(string entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"地图瘦身不接受目录树内的重解析点：{entry}");
                if((attributes & FileAttributes.Directory) != 0) directories.Push(entry);
            }
        }
    }

    internal static void DeleteDirectoryTreeWithoutFollowingReparsePoints(string rootPath)
    {
        FileAttributes rootAttributes = File.GetAttributes(rootPath);
        if((rootAttributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"拒绝递归删除重解析点根目录：{rootPath}");
        foreach(string entry in Directory.EnumerateFileSystemEntries(rootPath, "*", SearchOption.TopDirectoryOnly))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if((attributes & FileAttributes.Directory) != 0)
            {
                if((attributes & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(entry, recursive: false);
                else
                    DeleteDirectoryTreeWithoutFollowingReparsePoints(entry);
            }
            else
            {
                File.Delete(entry);
            }
        }
        Directory.Delete(rootPath, recursive: false);
    }
}

/// <summary>
/// Conservatively removes only proven-empty chunks and repacks the exact retained Anvil records.
/// </summary>
public sealed class MinecraftWorldSlimmingService
{
    public const int MinimumAnalysisConcurrency = 1;
    public const int DefaultAnalysisConcurrency = 4;
    public const int MaximumAnalysisConcurrency = 100;

    private const int SectorBytes = 4096;
    private const int RegionHeaderBytes = SectorBytes * 2;
    private const int CopyBufferBytes = 128 * 1024;
    private const int MaximumChunkNbtBytes = 32 * 1024 * 1024;
    private const int MaximumPreparationConcurrency = 2;
    private static readonly Regex RegionFileName = new(
        @"^r\.(-?\d+)\.(-?\d+)\.mca$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExternalChunkFileName = new(
        @"^c\.(-?\d+)\.(-?\d+)\.mcc$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<MinecraftWorldSlimmingAnalysis> AnalyzeAsync(
        MinecraftWorldSlimmingAnalyzeRequest request,
        IProgress<MinecraftWorldSlimmingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyMinecraftWorld world = request.SourceWorld;
        IReadOnlyMinecraftWorldFileSource worldFiles = world as IReadOnlyMinecraftWorldFileSource ??
            throw new NotSupportedException("当前世界挂载未提供完整文件源，不能安全执行地图瘦身。");

        Report(progress, MinecraftWorldSlimmingStage.ValidatingSource, 0, 1, "正在验证源世界");
        EnsureNoReparsePoints(world.Descriptor.RootPath);
        await EnsureSourceStableAsync(world, cancellationToken).ConfigureAwait(false);

        IReadOnlyDictionary<string, MinecraftFileFingerprint> files = await CollectFilesAsync(
            worldFiles,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<MinecraftChunkIndexEntry> entries = await CollectChunkEntriesAsync(
            world,
            cancellationToken).ConfigureAwait(false);
        Dictionary<MinecraftRegionAddress, IReadOnlyList<MinecraftChunkIndexEntry>> entriesByRegion = entries
            .GroupBy(static entry => entry.Region)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<MinecraftChunkIndexEntry>)group.ToArray());
        List<MinecraftDiagnostic> diagnostics = [];
        PrimaryPayloadAudit primaryPayloads = await AuditPrimaryPayloadsAsync(
            world,
            files,
            entriesByRegion,
            diagnostics,
            progress,
            request.AnalysisConcurrency,
            cancellationToken).ConfigureAwait(false);
        HashSet<MinecraftChunkAddress> sidecarEvidence = await CollectCompanionEvidenceAsync(
            world,
            files,
            entriesByRegion,
            diagnostics,
            progress,
            request.AnalysisConcurrency,
            cancellationToken).ConfigureAwait(false);
        HashSet<MinecraftChunkAddress> spawnProtected = CreateSpawnProtection(
            world.Descriptor.SpawnLocation,
            request.SpawnRetentionRadiusChunks,
            entries);

        MinecraftChunkReadOptions readOptions = new(
            DecompressPayload: true,
            VerifySourceFingerprint: true,
            MaximumDecompressedBytes: MaximumChunkNbtBytes);
        HashSet<MinecraftChunkAddress> removable = [];
        long protectedBySpawn = 0;
        long protectedBySidecar = 0;
        long protectedByNonAir = 0;
        long protectedByActivity = 0;
        long protectedByUncertainty = 0;
        IndexedChunkEntry[][] analysisGroups = entries
            .Select(static (entry, index) => new IndexedChunkEntry(index, entry))
            .GroupBy(static item => item.Entry.Region)
            .OrderBy(static group => group.Key.Dimension.Value, StringComparer.Ordinal)
            .ThenBy(static group => group.Key.X)
            .ThenBy(static group => group.Key.Z)
            .Select(static group => group.OrderBy(static item => item.Entry.SectorIndex).ToArray())
            .ToArray();
        ChunkSlimmingDecision[] decisions = new ChunkSlimmingDecision[entries.Count];
        long analyzedChunks = 0;
        long lastReportedChunks = 0;
        object analysisProgressGate = new();
        int analysisConcurrency = request.AnalysisConcurrency;
        await Parallel.ForEachAsync(
            analysisGroups,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = analysisConcurrency,
            },
            async (group, token) =>
            {
                IMinecraftChunkNormalizer? normalizer = CreateNormalizer(world.Descriptor.Version.StorageFamily);
                foreach(IndexedChunkEntry indexed in group)
                {
                    decisions[indexed.Index] = await ClassifyChunkAsync(
                        world,
                        indexed.Entry,
                        primaryPayloads.RepackableRegions,
                        sidecarEvidence,
                        spawnProtected,
                        normalizer,
                        readOptions,
                        token).ConfigureAwait(false);
                    long completed = Interlocked.Increment(ref analyzedChunks);
                    if(!ShouldReport(completed, entries.Count)) continue;
                    lock(analysisProgressGate)
                    {
                        long latest = Volatile.Read(ref analyzedChunks);
                        if(latest <= lastReportedChunks) continue;
                        lastReportedChunks = latest;
                        Report(
                            progress,
                            MinecraftWorldSlimmingStage.AnalyzingChunks,
                            Math.Min(latest, entries.Count),
                            entries.Count,
                            $"正在以 {analysisConcurrency:N0} 路并行判定空区块",
                            currentChunk: indexed.Entry.Address);
                    }
                }
            }).ConfigureAwait(false);
        if(entries.Count > 0)
            Report(progress, MinecraftWorldSlimmingStage.AnalyzingChunks, entries.Count, entries.Count,
                "空区块并行判定完成");

        foreach(ChunkSlimmingDecision decision in decisions)
        {
            switch(decision.Reason)
            {
                case ChunkSlimmingReason.Removable:
                    removable.Add(decision.Address);
                    break;
                case ChunkSlimmingReason.Spawn:
                    protectedBySpawn++;
                    break;
                case ChunkSlimmingReason.Sidecar:
                    protectedBySidecar++;
                    break;
                case ChunkSlimmingReason.NonAir:
                    protectedByNonAir++;
                    break;
                case ChunkSlimmingReason.Activity:
                    protectedByActivity++;
                    break;
                default:
                    protectedByUncertainty++;
                    break;
            }
            if(decision.Diagnostic is not null) diagnostics.Add(decision.Diagnostic);
        }

        if(entries.Count > 0 && removable.Count == entries.Count)
        {
            MinecraftChunkIndexEntry sentinel = SelectSentinelChunk(entries, world.Descriptor.SpawnLocation);
            removable.Remove(sentinel.Address);
            protectedBySpawn++;
            diagnostics.Add(new MinecraftDiagnostic(
                "slimming.minimum_world_chunk_retained",
                MinecraftDiagnosticSeverity.Information,
                "所有区块均满足空区块条件；为保证世界仍可挂载，已保留最接近出生点的一个区块。",
                sentinel.Address));
        }

        await EnsureSourceStableAsync(world, cancellationToken).ConfigureAwait(false);
        long estimate = EstimateReclaimableBytes(removable, entriesByRegion, files);
        Report(progress, MinecraftWorldSlimmingStage.ValidatingSource, 1, 1, "源世界验证完成");
        return new MinecraftWorldSlimmingAnalysis(
            world,
            worldFiles,
            new ReadOnlyDictionary<string, MinecraftFileFingerprint>(
                new Dictionary<string, MinecraftFileFingerprint>(files, StringComparer.OrdinalIgnoreCase)),
            Array.AsReadOnly(entries.ToArray()),
            new HashSet<MinecraftChunkAddress>(removable),
            new HashSet<MinecraftRegionAddress>(primaryPayloads.RepackableRegions),
            new HashSet<string>(primaryPayloads.ManagedPayloads, StringComparer.OrdinalIgnoreCase),
            estimate,
            protectedBySpawn,
            protectedBySidecar,
            protectedByNonAir,
            protectedByActivity,
            protectedByUncertainty,
            Array.AsReadOnly(diagnostics.ToArray()));
    }

    public async Task<PreparedMinecraftWorldSlimming> PrepareAsync(
        MinecraftWorldSlimmingAnalysis analysis,
        IProgress<MinecraftWorldSlimmingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyMinecraftWorld world = analysis.SourceWorld;
        if(!string.Equals(
               world.Descriptor.SourceRevision,
               analysis.SourceRevision,
               StringComparison.Ordinal))
        {
            throw new InvalidOperationException("瘦身分析与当前世界 revision 不一致，请重新分析。");
        }

        Report(progress, MinecraftWorldSlimmingStage.ValidatingSource, 0, 1, "正在复核源世界");
        EnsureNoReparsePoints(analysis.SourceDirectory);
        await EnsureSourceStableAsync(world, cancellationToken).ConfigureAwait(false);

        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(analysis.SourceDirectory));
        string parent = Path.GetDirectoryName(source) ??
                        throw new InvalidDataException("瘦身源世界必须有父目录。");
        string name = Path.GetFileName(source);
        if(name.Length == 0 || source.Equals(Path.GetPathRoot(source), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("不能对文件系统根目录执行地图瘦身。");
        if((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("瘦身源世界的父目录不能是重解析点。");

        string token = Guid.NewGuid().ToString("N");
        string staging = Path.Combine(parent, $".{name}.zjj-slimming-staging-{token}");
        string rollback = Path.Combine(parent, $".{name}.zjj-slimming-rollback-{token}");
        if(Directory.Exists(staging) || File.Exists(staging) ||
           Directory.Exists(rollback) || File.Exists(rollback))
        {
            throw new IOException("生成的瘦身事务路径已存在。");
        }

        long sourceBytes = analysis.Files.Values.Sum(static file => file.Length);
        long bytesRead = 0;
        long bytesWritten = 0;
        long repackedRegions = 0;
        long removedRegions = 0;
        long retainedExternalChunks = 0;
        List<MinecraftDiagnostic> diagnostics = [.. analysis.Diagnostics];
        IReadOnlyList<MinecraftFileFingerprint> copiedFiles = analysis.Files.Values
            .Where(file => !IsSessionLock(file.RelativePath))
            .Where(file => !analysis.ManagedPrimaryPayloads.Contains(file.RelativePath))
            .OrderBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        IGrouping<MinecraftRegionAddress, MinecraftChunkIndexEntry>[] regionGroups = analysis.Entries
            .Where(entry => analysis.RepackableRegions.Contains(entry.Region))
            .GroupBy(static entry => entry.Region)
            .OrderBy(static group => group.Key.Dimension.Value, StringComparer.Ordinal)
            .ThenBy(static group => group.Key.X)
            .ThenBy(static group => group.Key.Z)
            .ToArray();
        long totalPrepareItems = copiedFiles.Count + regionGroups.Length;
        long completedItems = 0;

        try
        {
            Report(progress, MinecraftWorldSlimmingStage.PreparingStaging, 0, totalPrepareItems, "正在创建同卷临时世界");
            Directory.CreateDirectory(staging);

            object preparationProgressGate = new();
            int preparationConcurrency = Math.Min(MaximumPreparationConcurrency, Math.Max(1, Environment.ProcessorCount));
            await Parallel.ForEachAsync(
                copiedFiles,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = preparationConcurrency,
                },
                async (file, token) =>
                {
                    string outputPath = ResolveStagePath(staging, file.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    long copied = await CopyAndVerifyAsync(
                        analysis.WorldFiles,
                        file,
                        outputPath,
                        token).ConfigureAwait(false);
                    File.SetLastWriteTimeUtc(outputPath, file.LastWriteTimeUtc.UtcDateTime);
                    Interlocked.Add(ref bytesRead, copied);
                    Interlocked.Add(ref bytesWritten, copied);
                    lock(preparationProgressGate)
                    {
                        completedItems = checked(completedItems + 1);
                        Report(
                            progress,
                            MinecraftWorldSlimmingStage.CopyingFiles,
                            completedItems,
                            totalPrepareItems,
                            $"正在以 {preparationConcurrency:N0} 路并行复制 {file.RelativePath}",
                            Volatile.Read(ref bytesRead),
                            Volatile.Read(ref bytesWritten));
                    }
                }).ConfigureAwait(false);

            RegionPreparationWork[] regionWork = regionGroups
                .Select(group => new RegionPreparationWork(
                    group.Key,
                    group
                        .Where(entry => !analysis.RemovableChunks.Contains(entry.Address))
                        .OrderBy(static entry => entry.LocalIndex)
                        .ToArray()))
                .ToArray();
            await Parallel.ForEachAsync(
                regionWork,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = preparationConcurrency,
                },
                async (work, token) =>
                {
                    RegionRepackResult written = default;
                    if(work.RetainedEntries.Length == 0)
                    {
                        Interlocked.Increment(ref removedRegions);
                    }
                    else
                    {
                        written = await RepackRegionAsync(
                            staging,
                            world,
                            work.RetainedEntries,
                            analysis.Files,
                            token).ConfigureAwait(false);
                        Interlocked.Add(ref bytesRead, written.BytesRead);
                        Interlocked.Add(ref bytesWritten, written.BytesWritten);
                        Interlocked.Add(ref retainedExternalChunks, written.ExternalChunkCount);
                        Interlocked.Increment(ref repackedRegions);
                    }

                    lock(preparationProgressGate)
                    {
                        completedItems = checked(completedItems + 1);
                        Report(
                            progress,
                            MinecraftWorldSlimmingStage.RepackingRegions,
                            completedItems,
                            totalPrepareItems,
                            $"正在以 {preparationConcurrency:N0} 路并行重打包 {work.Region.Dimension} r.{work.Region.X}.{work.Region.Z}.mca",
                            Volatile.Read(ref bytesRead),
                            Volatile.Read(ref bytesWritten));
                    }
                }).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            await EnsureSourceStableAsync(world, cancellationToken).ConfigureAwait(false);
            Report(
                progress,
                MinecraftWorldSlimmingStage.ValidatingStaging,
                0,
                1,
                "正在验证重构后的世界",
                bytesRead,
                bytesWritten);
            await ValidateStagingAsync(
                staging,
                analysis,
                cancellationToken).ConfigureAwait(false);
            await EnsureSourceStableAsync(world, cancellationToken).ConfigureAwait(false);

            long stagedBytes = GetDirectoryFileBytes(staging);
            MinecraftWorldSlimmingResult result = new(
                source,
                analysis.SourceRevision,
                staging,
                sourceBytes,
                stagedBytes,
                Math.Max(0, sourceBytes - stagedBytes),
                analysis.RemovableChunkCount,
                analysis.RetainedChunkCount,
                repackedRegions,
                removedRegions,
                retainedExternalChunks,
                Array.AsReadOnly(diagnostics.ToArray()));
            Report(
                progress,
                MinecraftWorldSlimmingStage.ReadyToCommit,
                1,
                1,
                "临时世界验证通过，等待提交",
                bytesRead,
                stagedBytes);
            return new PreparedMinecraftWorldSlimming(result, rollback, analysis.Files);
        }
        catch(Exception exception)
        {
            Exception? cleanupFailure = TryDeleteStaging(staging, parent);
            if(cleanupFailure is not null)
            {
                throw new AggregateException(
                    "地图瘦身准备失败，且临时目录无法清理。",
                    exception,
                    cleanupFailure);
            }
            throw;
        }
    }

    private static void ValidateRequest(MinecraftWorldSlimmingAnalyzeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.SourceWorld);
        if(string.IsNullOrWhiteSpace(request.ExpectedSourceRevision))
            throw new ArgumentException("ExpectedSourceRevision 不能为空。", nameof(request));
        if(!string.Equals(
               request.ExpectedSourceRevision,
               request.SourceWorld.Descriptor.SourceRevision,
               StringComparison.Ordinal))
        {
            throw new InvalidOperationException("请求的源 revision 与已挂载世界不一致。");
        }
        if(request.SourceWorld.Descriptor.SourceRevisionStrength != MinecraftSourceRevisionStrength.ContentHash)
            throw new InvalidOperationException("地图瘦身要求使用内容哈希挂载世界。");
        if(request.SpawnRetentionRadiusChunks is < 0 or > 64)
            throw new ArgumentOutOfRangeException(nameof(request), "出生点保护半径必须在 0..64 Chunk。 ");
        if(request.AnalysisConcurrency is < MinimumAnalysisConcurrency or > MaximumAnalysisConcurrency)
            throw new ArgumentOutOfRangeException(nameof(request), $"判空并行数必须在 {MinimumAnalysisConcurrency}..{MaximumAnalysisConcurrency}。 ");
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

    private static async Task<ChunkSlimmingDecision> ClassifyChunkAsync(
        IReadOnlyMinecraftWorld world,
        MinecraftChunkIndexEntry entry,
        IReadOnlySet<MinecraftRegionAddress> repackableRegions,
        IReadOnlySet<MinecraftChunkAddress> sidecarEvidence,
        IReadOnlySet<MinecraftChunkAddress> spawnProtected,
        IMinecraftChunkNormalizer? normalizer,
        MinecraftChunkReadOptions readOptions,
        CancellationToken cancellationToken)
    {
        if(spawnProtected.Contains(entry.Address))
            return new ChunkSlimmingDecision(entry.Address, ChunkSlimmingReason.Spawn);
        if(!repackableRegions.Contains(entry.Region) || normalizer is null)
            return new ChunkSlimmingDecision(entry.Address, ChunkSlimmingReason.Uncertainty);
        if(sidecarEvidence.Contains(entry.Address))
            return new ChunkSlimmingDecision(entry.Address, ChunkSlimmingReason.Sidecar);

        try
        {
            RawMinecraftChunk raw = await world.ChunkReader.ReadAsync(
                entry,
                readOptions,
                cancellationToken).ConfigureAwait(false);
            NormalizedMinecraftChunk chunk = await normalizer.NormalizeAsync(
                new MinecraftNormalizationRequest(
                    raw,
                    world.Descriptor.Version,
                    Minecraft1122VanillaBlockRegistry.Instance,
                    new MinecraftUnknownDataPolicy()),
                cancellationToken).ConfigureAwait(false);
            if(!ContainsOnlyAir(chunk))
                return new ChunkSlimmingDecision(entry.Address, ChunkSlimmingReason.NonAir);
            if(chunk.BlockEntities.Count > 0)
                return new ChunkSlimmingDecision(entry.Address, ChunkSlimmingReason.Activity);

            MinecraftChunkSafetySignals signals = MinecraftChunkSafetySignalReader.Read(raw.NbtPayload.Span);
            if(signals.HasActivity)
                return new ChunkSlimmingDecision(entry.Address, ChunkSlimmingReason.Activity);
            return signals.HasUnknownDangerousMetadata
                ? new ChunkSlimmingDecision(entry.Address, ChunkSlimmingReason.Uncertainty)
                : new ChunkSlimmingDecision(entry.Address, ChunkSlimmingReason.Removable);
        }
        catch(OperationCanceledException)
        {
            throw;
        }
        catch(Exception exception) when(IsConservativeRetentionFailure(exception))
        {
            return new ChunkSlimmingDecision(
                entry.Address,
                ChunkSlimmingReason.Uncertainty,
                new MinecraftDiagnostic(
                    "slimming.chunk.retained_on_parse_failure",
                    MinecraftDiagnosticSeverity.Warning,
                    $"区块无法完成安全判定，已保留：{exception.Message}",
                    entry.Address));
        }
    }

    private static async Task<RegionPayloadAuditResult> AuditRegionPayloadAsync(
        IReadOnlyMinecraftWorld world,
        MinecraftRegionAddress region,
        IReadOnlyList<MinecraftChunkIndexEntry> entries,
        IReadOnlyDictionary<string, MinecraftFileFingerprint> files,
        MinecraftChunkReadOptions options,
        CancellationToken cancellationToken)
    {
        string relativePath = entries[0].RegionRelativePath;
        HashSet<string> regionPayloads = new(StringComparer.OrdinalIgnoreCase) { relativePath };
        bool canRepack = entries.All(entry =>
            entry.RegionRelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase));
        Exception? failure = canRepack ? null : new InvalidDataException("同一 Region 索引指向多个源文件。");
        if(canRepack && !files.ContainsKey(relativePath))
        {
            canRepack = false;
            failure = new IOException($"Region 源文件未包含在内容哈希快照中：{relativePath}");
        }

        if(canRepack)
        {
            try
            {
                foreach(MinecraftChunkIndexEntry entry in entries.OrderBy(static item => item.SectorIndex))
                {
                    RawMinecraftChunk raw = await world.ChunkReader.ReadAsync(
                        entry,
                        options,
                        cancellationToken).ConfigureAwait(false);
                    ValidateStoredPayload(raw.OriginalStoredPayload);
                    if(raw.IndexEntry.StorageKind == MinecraftChunkStorageKind.ExternalMcc)
                    {
                        string external = raw.IndexEntry.ExternalPayloadRelativePath ??
                                          throw new InvalidDataException($"外部区块 {entry.Address} 缺少 MCC 路径。");
                        string expected = CombineRelative(
                            GetRelativeParent(relativePath),
                            $"c.{entry.Address.X}.{entry.Address.Z}.mcc");
                        if(!external.Equals(expected, StringComparison.OrdinalIgnoreCase) || !files.ContainsKey(external))
                            throw new InvalidDataException($"外部区块载荷路径不完整：{entry.Address}");
                        regionPayloads.Add(external);
                    }
                    else if(raw.IndexEntry.StorageKind != MinecraftChunkStorageKind.InlineRegionSector)
                    {
                        throw new InvalidDataException($"区块 {entry.Address} 的存储类型未解析。");
                    }
                }
            }
            catch(OperationCanceledException)
            {
                throw;
            }
            catch(Exception exception) when(IsConservativeRetentionFailure(exception))
            {
                canRepack = false;
                failure = exception;
            }
        }

        return new RegionPayloadAuditResult(region, relativePath, canRepack, regionPayloads, failure);
    }

    private static async Task<CompanionEvidenceResult> ReadCompanionEvidenceAsync(
        IReadOnlyMinecraftWorld world,
        MinecraftFileFingerprint file,
        string parent,
        IReadOnlyDictionary<string, MinecraftDimensionId> roots,
        IReadOnlyDictionary<MinecraftRegionAddress, IReadOnlyList<MinecraftChunkIndexEntry>> entriesByRegion,
        CancellationToken cancellationToken)
    {
        Match match = RegionFileName.Match(Path.GetFileName(file.RelativePath));
        if(!int.TryParse(match.Groups[1].Value, out int regionX) ||
           !int.TryParse(match.Groups[2].Value, out int regionZ))
            return new CompanionEvidenceResult([], []);

        MinecraftRegionAddress region = new(roots[parent], regionX, regionZ);
        List<MinecraftChunkAddress> evidence = [];
        List<MinecraftDiagnostic> diagnostics = [];
        try
        {
            string path = MinecraftSourceFiles.ResolveRelativePath(world.Descriptor.RootPath, file.RelativePath);
            IReadOnlyList<MinecraftChunkIndexEntry> sidecarEntries = await AnvilChunkIndex.ReadRegionHeaderAsync(
                path,
                region,
                file,
                diagnostics,
                cancellationToken).ConfigureAwait(false);
            evidence.AddRange(sidecarEntries.Select(static entry => entry.Address));
        }
        catch(OperationCanceledException)
        {
            throw;
        }
        catch(Exception exception) when(IsConservativeRetentionFailure(exception))
        {
            if(entriesByRegion.TryGetValue(region, out IReadOnlyList<MinecraftChunkIndexEntry>? mainEntries))
                evidence.AddRange(mainEntries.Select(static entry => entry.Address));
            diagnostics.Add(new MinecraftDiagnostic(
                "slimming.sidecar.retained_on_parse_failure",
                MinecraftDiagnosticSeverity.Warning,
                $"实体或 POI 区域文件无法安全判定，已保留对应主 Region 的全部区块：{file.RelativePath}；{exception.Message}"));
        }
        return new CompanionEvidenceResult(evidence, diagnostics);
    }

    private static async Task<PrimaryPayloadAudit> AuditPrimaryPayloadsAsync(
        IReadOnlyMinecraftWorld world,
        IReadOnlyDictionary<string, MinecraftFileFingerprint> files,
        IReadOnlyDictionary<MinecraftRegionAddress, IReadOnlyList<MinecraftChunkIndexEntry>> entriesByRegion,
        ICollection<MinecraftDiagnostic> diagnostics,
        IProgress<MinecraftWorldSlimmingProgress>? progress,
        int analysisConcurrency,
        CancellationToken cancellationToken)
    {
        HashSet<MinecraftRegionAddress> repackableRegions = [];
        HashSet<string> managedPayloads = new(StringComparer.OrdinalIgnoreCase);
        KeyValuePair<MinecraftRegionAddress, IReadOnlyList<MinecraftChunkIndexEntry>>[] regions = entriesByRegion
            .OrderBy(static item => item.Key.Dimension.Value, StringComparer.Ordinal)
            .ThenBy(static item => item.Key.X)
            .ThenBy(static item => item.Key.Z)
            .ToArray();
        MinecraftChunkReadOptions options = new(
            DecompressPayload: false,
            VerifySourceFingerprint: true,
            MaximumDecompressedBytes: 64 * 1024 * 1024);

        RegionPayloadAuditResult[] results = new RegionPayloadAuditResult[regions.Length];
        long completedRegions = 0;
        object progressGate = new();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, regions.Length),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = analysisConcurrency,
            },
            async (regionIndex, token) =>
            {
                (MinecraftRegionAddress region, IReadOnlyList<MinecraftChunkIndexEntry> regionEntries) = regions[regionIndex];
                RegionPayloadAuditResult result = await AuditRegionPayloadAsync(
                    world,
                    region,
                    regionEntries,
                    files,
                    options,
                    token).ConfigureAwait(false);
                results[regionIndex] = result;
                lock(progressGate)
                {
                    completedRegions = checked(completedRegions + 1);
                    Report(
                        progress,
                        MinecraftWorldSlimmingStage.AuditingRegionPayloads,
                        completedRegions,
                        regions.Length,
                        $"正在以 {analysisConcurrency:N0} 路并行核对 {result.RelativePath}");
                }
            }).ConfigureAwait(false);

        foreach(RegionPayloadAuditResult result in results)
        {
            if(result.CanRepack)
            {
                repackableRegions.Add(result.Region);
                managedPayloads.UnionWith(result.Payloads);
                continue;
            }
            diagnostics.Add(new MinecraftDiagnostic(
                "slimming.region.retained_on_payload_audit_failure",
                MinecraftDiagnosticSeverity.Warning,
                $"Region 无法证明可被完整重打包，原文件及其中全部区块已保留：{result.RelativePath}；{result.Failure?.Message}"));
        }

        return new PrimaryPayloadAudit(repackableRegions, managedPayloads);
    }

    private static async Task<HashSet<MinecraftChunkAddress>> CollectCompanionEvidenceAsync(
        IReadOnlyMinecraftWorld world,
        IReadOnlyDictionary<string, MinecraftFileFingerprint> files,
        IReadOnlyDictionary<MinecraftRegionAddress, IReadOnlyList<MinecraftChunkIndexEntry>> entriesByRegion,
        ICollection<MinecraftDiagnostic> diagnostics,
        IProgress<MinecraftWorldSlimmingProgress>? progress,
        int analysisConcurrency,
        CancellationToken cancellationToken)
    {
        Dictionary<string, MinecraftDimensionId> roots = new(StringComparer.OrdinalIgnoreCase);
        foreach(MinecraftDimensionDescriptor dimension in world.Descriptor.Dimensions)
        {
            foreach(string regionDirectory in dimension.RegionDirectories)
            {
                string normalized = NormalizeRelativeDirectory(regionDirectory);
                string baseDirectory = GetRelativeParent(normalized);
                roots[CombineRelative(baseDirectory, "entities")] = dimension.Id;
                roots[CombineRelative(baseDirectory, "poi")] = dimension.Id;
            }
        }

        var candidates = files.Values
            .Select(file => (File: file, Parent: NormalizeRelativeDirectory(GetRelativeParent(file.RelativePath))))
            .Where(item => roots.ContainsKey(item.Parent) && RegionFileName.IsMatch(Path.GetFileName(item.File.RelativePath)))
            .OrderBy(static item => item.File.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        HashSet<MinecraftChunkAddress> evidence = [];
        CompanionEvidenceResult[] results = new CompanionEvidenceResult[candidates.Length];
        long completedFiles = 0;
        object progressGate = new();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, candidates.Length),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = analysisConcurrency,
            },
            async (index, token) =>
            {
                (MinecraftFileFingerprint file, string parent) = candidates[index];
                results[index] = await ReadCompanionEvidenceAsync(
                    world,
                    file,
                    parent,
                    roots,
                    entriesByRegion,
                    token).ConfigureAwait(false);
                lock(progressGate)
                {
                    completedFiles = checked(completedFiles + 1);
                    Report(
                        progress,
                        MinecraftWorldSlimmingStage.AnalyzingCompanionData,
                        completedFiles,
                        candidates.Length,
                        $"正在以 {analysisConcurrency:N0} 路并行检查 {file.RelativePath}");
                }
            }).ConfigureAwait(false);

        foreach(CompanionEvidenceResult result in results)
        {
            evidence.UnionWith(result.Evidence);
            foreach(MinecraftDiagnostic diagnostic in result.Diagnostics) diagnostics.Add(diagnostic);
        }
        return evidence;
    }

    private static HashSet<MinecraftChunkAddress> CreateSpawnProtection(
        Core.BlockPosition? spawn,
        int radius,
        IReadOnlyList<MinecraftChunkIndexEntry> entries)
    {
        HashSet<MinecraftChunkAddress> result = [];
        if(spawn is not Core.BlockPosition position) return result;
        int centerX = FloorDiv(position.X, 16);
        int centerZ = FloorDiv(position.Z, 16);
        HashSet<MinecraftChunkAddress> existing = entries.Select(static entry => entry.Address).ToHashSet();
        for(int z = centerZ - radius; z <= centerZ + radius; z++)
        {
            for(int x = centerX - radius; x <= centerX + radius; x++)
            {
                MinecraftChunkAddress address = new(MinecraftDimensionId.Overworld, x, z);
                if(existing.Contains(address)) result.Add(address);
            }
        }
        return result;
    }

    private static IMinecraftChunkNormalizer? CreateNormalizer(MinecraftStorageFamily family) => family switch
    {
        MinecraftStorageFamily.LegacyNumericAnvil => new LegacyAnvilChunkNormalizer(),
        MinecraftStorageFamily.FlattenedPalette or MinecraftStorageFamily.ModernSectionPalette =>
            new ModernAnvilChunkNormalizer(),
        _ => null,
    };

    private static bool ContainsOnlyAir(NormalizedMinecraftChunk chunk)
    {
        foreach(NormalizedMinecraftSection section in chunk.Sections)
        {
            if(section.Palette.Count == 0)
                throw new InvalidDataException($"Section {section.Coordinate} 的调色板为空。");
            ReadOnlySpan<ushort> indices = section.PaletteIndices.Span;
            if(indices.Length != 4096)
                throw new InvalidDataException($"Section {section.Coordinate} 的方块索引数量不是 4096。");
            if(section.Palette.Count == 1)
            {
                for(int index = 0; index < indices.Length; index++)
                {
                    if(indices[index] != 0)
                        throw new InvalidDataException($"Section {section.Coordinate} 的单色调色板索引越界。");
                }
                if(!section.Palette[0].IsAir) return false;
                continue;
            }
            for(int index = 0; index < indices.Length; index++)
            {
                ushort paletteIndex = indices[index];
                if(paletteIndex >= section.Palette.Count)
                    throw new InvalidDataException($"Section {section.Coordinate} 的调色板索引越界。");
                if(!section.Palette[paletteIndex].IsAir) return false;
            }
        }
        return true;
    }

    private static MinecraftChunkIndexEntry SelectSentinelChunk(
        IReadOnlyList<MinecraftChunkIndexEntry> entries,
        Core.BlockPosition? spawn)
    {
        if(spawn is Core.BlockPosition position)
        {
            int spawnX = FloorDiv(position.X, 16);
            int spawnZ = FloorDiv(position.Z, 16);
            MinecraftChunkIndexEntry? overworld = entries
                .Where(static entry => entry.Address.Dimension == MinecraftDimensionId.Overworld)
                .OrderBy(entry => Math.Abs((long)entry.Address.X - spawnX) + Math.Abs((long)entry.Address.Z - spawnZ))
                .ThenBy(static entry => entry.Address.X)
                .ThenBy(static entry => entry.Address.Z)
                .FirstOrDefault();
            if(overworld is not null) return overworld;
        }
        return entries
            .OrderBy(static entry => entry.Address.Dimension.Value, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Address.X)
            .ThenBy(static entry => entry.Address.Z)
            .First();
    }

    private static long EstimateReclaimableBytes(
        IReadOnlySet<MinecraftChunkAddress> removable,
        IReadOnlyDictionary<MinecraftRegionAddress, IReadOnlyList<MinecraftChunkIndexEntry>> entriesByRegion,
        IReadOnlyDictionary<string, MinecraftFileFingerprint> files)
    {
        long bytes = 0;
        foreach((MinecraftRegionAddress _, IReadOnlyList<MinecraftChunkIndexEntry> entries) in entriesByRegion)
        {
            int removedInRegion = 0;
            foreach(MinecraftChunkIndexEntry entry in entries)
            {
                if(!removable.Contains(entry.Address)) continue;
                removedInRegion++;
                bytes = checked(bytes + (long)entry.AllocatedSectorCount * SectorBytes);
                string directory = GetRelativeParent(entry.RegionRelativePath);
                string external = CombineRelative(directory, $"c.{entry.Address.X}.{entry.Address.Z}.mcc");
                if(files.TryGetValue(external, out MinecraftFileFingerprint? fingerprint))
                    bytes = checked(bytes + fingerprint.Length);
            }
            if(removedInRegion == entries.Count)
                bytes = checked(bytes + RegionHeaderBytes);
        }
        return bytes;
    }

    private static async Task<RegionRepackResult> RepackRegionAsync(
        string staging,
        IReadOnlyMinecraftWorld world,
        IReadOnlyList<MinecraftChunkIndexEntry> entries,
        IReadOnlyDictionary<string, MinecraftFileFingerprint> sourceFiles,
        CancellationToken cancellationToken)
    {
        if(entries.Count == 0) throw new ArgumentException("重打包 Region 至少需要一个保留区块。", nameof(entries));
        string relativePath = entries[0].RegionRelativePath;
        if(entries.Any(entry => !entry.RegionRelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("同一 Region 重打包批次包含不同源文件。");
        string outputPath = ResolveStagePath(staging, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        byte[] header = new byte[RegionHeaderBytes];
        long bytesRead = 0;
        long externalCount = 0;

        await using FileStream output = new(
            outputPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        int nextSector = 2;
        MinecraftChunkReadOptions options = new(
            DecompressPayload: false,
            VerifySourceFingerprint: true,
            MaximumDecompressedBytes: 64 * 1024 * 1024);
        foreach(MinecraftChunkIndexEntry entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RawMinecraftChunk raw = await world.ChunkReader.ReadAsync(entry, options, cancellationToken)
                .ConfigureAwait(false);
            ValidateStoredPayload(raw.OriginalStoredPayload);
            byte[] record;
            int sectors;
            if(raw.IndexEntry.StorageKind == MinecraftChunkStorageKind.ExternalMcc)
            {
                record = new byte[SectorBytes];
                BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(0, sizeof(uint)), 1);
                record[4] = (byte)((byte)raw.Compression | 0x80);
                sectors = 1;
                string externalRelative = raw.IndexEntry.ExternalPayloadRelativePath ??
                                          throw new InvalidDataException($"外部区块 {entry.Address} 缺少 MCC 路径。");
                string expectedExternal = CombineRelative(
                    GetRelativeParent(relativePath),
                    $"c.{entry.Address.X}.{entry.Address.Z}.mcc");
                if(!externalRelative.Equals(expectedExternal, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"外部区块路径不匹配：{externalRelative}");
                string externalOutput = ResolveStagePath(staging, externalRelative);
                await WritePayloadAsync(externalOutput, raw.OriginalStoredPayload.Bytes, cancellationToken)
                    .ConfigureAwait(false);
                if(sourceFiles.TryGetValue(externalRelative, out MinecraftFileFingerprint? externalFingerprint))
                    File.SetLastWriteTimeUtc(externalOutput, externalFingerprint.LastWriteTimeUtc.UtcDateTime);
                bytesRead = checked(bytesRead + raw.OriginalStoredPayload.Bytes.Length);
                externalCount++;
            }
            else if(raw.IndexEntry.StorageKind == MinecraftChunkStorageKind.InlineRegionSector)
            {
                ReadOnlyMemory<byte> stored = raw.OriginalStoredPayload.Bytes;
                if(stored.Length < 5)
                    throw new InvalidDataException($"内联区块记录过短：{entry.Address}");
                uint encodedLength = BinaryPrimitives.ReadUInt32BigEndian(stored.Span[..4]);
                if(encodedLength + sizeof(uint) != stored.Length || (stored.Span[4] & 0x80) != 0)
                    throw new InvalidDataException($"内联区块记录框架无效：{entry.Address}");
                sectors = checked((stored.Length + SectorBytes - 1) / SectorBytes);
                if(sectors > byte.MaxValue)
                    throw new InvalidDataException($"区块 {entry.Address} 的内联记录超过 255 个扇区。");
                record = new byte[checked(sectors * SectorBytes)];
                stored.Span.CopyTo(record);
                bytesRead = checked(bytesRead + stored.Length);
            }
            else
            {
                throw new InvalidDataException($"区块 {entry.Address} 的存储类型未解析。");
            }

            if(nextSector > 0x00ff_ffff || (long)nextSector + sectors > 0x0100_0000)
                throw new InvalidDataException($"Region 扇区偏移超过 MCA 24 位上限：{relativePath}");
            await output.WriteAsync(record, cancellationToken).ConfigureAwait(false);
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
        long outputBytes = output.Length;
        await output.DisposeAsync().ConfigureAwait(false);
        if(sourceFiles.TryGetValue(relativePath, out MinecraftFileFingerprint? sourceRegion))
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

    private static void ValidateStoredPayload(MinecraftOpaquePayload payload)
    {
        if(payload.Format != MinecraftOpaquePayloadFormat.StoredChunkPayload || payload.Bytes.IsEmpty)
            throw new InvalidDataException("区块缺少可重打包的原始存储记录。");
        if(string.IsNullOrWhiteSpace(payload.ContentHash))
            throw new InvalidDataException("区块原始存储记录缺少内容哈希。");
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(payload.ContentHash);
        }
        catch(FormatException exception)
        {
            throw new InvalidDataException("区块原始存储记录的内容哈希无效。", exception);
        }
        byte[] actual = SHA256.HashData(payload.Bytes.Span);
        if(!CryptographicOperations.FixedTimeEquals(expected, actual))
            throw new InvalidDataException("区块原始存储记录与内容哈希不一致。");
    }

    private static async Task ValidateStagingAsync(
        string staging,
        MinecraftWorldSlimmingAnalysis analysis,
        CancellationToken cancellationToken)
    {
        await using IReadOnlyMinecraftWorld staged = await new AnvilWorldMounter().MountAsync(
            new ReadOnlyWorldMountRequest(
                staging,
                ConcurrentWorldWritePolicy.RejectActiveWriter,
                IncludeCustomDimensions: true,
                CaptureSourceFingerprints: true),
            cancellationToken).ConfigureAwait(false);
        if(staged.ChunkIndex.TotalChunkCount != analysis.RetainedChunkCount)
        {
            throw new InvalidDataException(
                $"重构世界包含 {staged.ChunkIndex.TotalChunkCount:N0} 个区块，预期 {analysis.RetainedChunkCount:N0} 个。");
        }

        HashSet<MinecraftChunkAddress> expected = analysis.Entries
            .Where(entry => !analysis.RemovableChunks.Contains(entry.Address))
            .Select(static entry => entry.Address)
            .ToHashSet();
        HashSet<MinecraftChunkAddress> actual = [];
        foreach(MinecraftDimensionDescriptor dimension in staged.Descriptor.Dimensions)
        {
            await foreach(MinecraftChunkIndexEntry entry in staged.ChunkIndex
                               .EnumerateAsync(dimension.Id, cancellationToken: cancellationToken)
                               .ConfigureAwait(false))
            {
                actual.Add(entry.Address);
            }
        }
        if(!actual.SetEquals(expected))
            throw new InvalidDataException("重构世界的区块地址集合与瘦身分析不一致。");
        WorldSourceValidationResult validation = await staged.ValidateSourceAsync(cancellationToken)
            .ConfigureAwait(false);
        if(!validation.IsStable)
            throw new IOException("重构世界在验证期间发生变化。");
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
        throw new IOException($"源世界在地图瘦身期间发生变化：{changed}");
    }

    private static bool IsPrimaryRegionPayload(
        string relativePath,
        IReadOnlySet<string> primaryRegionDirectories)
    {
        string parent = NormalizeRelativeDirectory(GetRelativeParent(relativePath));
        if(!primaryRegionDirectories.Contains(parent)) return false;
        string name = Path.GetFileName(relativePath);
        return RegionFileName.IsMatch(name) || ExternalChunkFileName.IsMatch(name);
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

    private static void EnsureNoReparsePoints(string rootPath)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        if((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("地图瘦身不接受重解析点世界目录。");
        Stack<string> directories = new();
        directories.Push(root);
        while(directories.Count > 0)
        {
            string directory = directories.Pop();
            foreach(string entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"地图瘦身不接受世界目录内的重解析点：{entry}");
                if((attributes & FileAttributes.Directory) != 0) directories.Push(entry);
            }
        }
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

    private static bool IsConservativeRetentionFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or
        ArgumentException or InvalidOperationException or OverflowException or CryptographicException;

    private static bool ShouldReport(long completed, long total) =>
        completed == 1 || completed == total || completed % 16 == 0;

    private static void Report(
        IProgress<MinecraftWorldSlimmingProgress>? progress,
        MinecraftWorldSlimmingStage stage,
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
            progress.Report(new MinecraftWorldSlimmingProgress(
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
               !name.Contains(".zjj-slimming-staging-", StringComparison.Ordinal) ||
               (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
            {
                return new InvalidOperationException($"拒绝递归删除未验证的瘦身临时目录：{resolved}");
            }
            PreparedMinecraftWorldSlimming.DeleteDirectoryTreeWithoutFollowingReparsePoints(resolved);
            return null;
        }
        catch(Exception exception)
        {
            return exception;
        }
    }

    private sealed record PrimaryPayloadAudit(
        IReadOnlySet<MinecraftRegionAddress> RepackableRegions,
        IReadOnlySet<string> ManagedPayloads);

    private sealed record RegionPayloadAuditResult(
        MinecraftRegionAddress Region,
        string RelativePath,
        bool CanRepack,
        IReadOnlySet<string> Payloads,
        Exception? Failure);

    private sealed record CompanionEvidenceResult(
        IReadOnlyList<MinecraftChunkAddress> Evidence,
        IReadOnlyList<MinecraftDiagnostic> Diagnostics);

    private readonly record struct IndexedChunkEntry(int Index, MinecraftChunkIndexEntry Entry);

    private readonly record struct ChunkSlimmingDecision(
        MinecraftChunkAddress Address,
        ChunkSlimmingReason Reason,
        MinecraftDiagnostic? Diagnostic = null);

    private enum ChunkSlimmingReason
    {
        Uncertainty,
        Removable,
        Spawn,
        Sidecar,
        NonAir,
        Activity,
    }

    private sealed record RegionPreparationWork(
        MinecraftRegionAddress Region,
        MinecraftChunkIndexEntry[] RetainedEntries);

    private readonly record struct RegionRepackResult(long BytesRead, long BytesWritten, long ExternalChunkCount);
}
