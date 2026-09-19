using System.Globalization;
using System.Text.RegularExpressions;

namespace ZhuJieJing.Minecraft;

/// <summary>Mounts a Java Edition Anvil world without opening any source file for writing.</summary>
public sealed class AnvilWorldMounter : IReadOnlyMinecraftWorldMounter
{
    private const int ProgressReportInterval = 16;
    private static readonly Regex RegionFileName = new(
        @"^r\.(-?\d+)\.(-?\d+)\.mca$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async ValueTask<IReadOnlyMinecraftWorld> MountAsync(
        ReadOnlyWorldMountRequest request,
        CancellationToken cancellationToken = default,
        IProgress<MinecraftWorldMountProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.WorldDirectory))
            throw new ArgumentException("WorldDirectory cannot be empty.", nameof(request));

        string rootPath = Path.GetFullPath(request.WorldDirectory);
        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"Minecraft world directory was not found: {rootPath}");

        string levelDatPath = Path.Combine(rootPath, "level.dat");
        if (!File.Exists(levelDatPath))
            throw new InvalidDataException($"The selected folder has no level.dat: {rootPath}");

        MinecraftSessionLockState sessionLockState = InspectSessionLock(rootPath);
        if (sessionLockState == MinecraftSessionLockState.SuspectedActiveWriter &&
            request.ConcurrentWritePolicy == ConcurrentWorldWritePolicy.RejectActiveWriter)
        {
            throw new IOException(
                "The world appears to be open for writing. Close Minecraft or mount with VerifyEveryRead.");
        }

        List<MinecraftDiagnostic> diagnostics = new();
        if (sessionLockState == MinecraftSessionLockState.SuspectedActiveWriter)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "world.session_lock.active",
                MinecraftDiagnosticSeverity.Warning,
                "检测到存档可能仍在被 Minecraft 写入；每次区块读取都会重新核验源文件。"));
        }

        ReportProgress(progress, MinecraftWorldMountStage.DiscoveringFiles, 0, 0);
        IReadOnlyList<string> sourceFiles = MinecraftSourceFiles.EnumerateWorldFiles(rootPath);
        ReportProgress(progress, MinecraftWorldMountStage.FingerprintingFiles, 0, sourceFiles.Count);
        Dictionary<string, MinecraftFileFingerprint> fingerprints =
            new(StringComparer.OrdinalIgnoreCase);
        for (int sourceIndex = 0; sourceIndex < sourceFiles.Count; sourceIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string sourcePath = sourceFiles[sourceIndex];
            string sourceRelativePath = MinecraftSourceFiles.NormalizeRelativePath(rootPath, sourcePath);
            bool hashContents = request.CaptureSourceFingerprints &&
                                !sourceRelativePath.Equals("session.lock", StringComparison.OrdinalIgnoreCase);
            MinecraftFileFingerprint fingerprint = await MinecraftSourceFiles.CaptureFingerprintAsync(
                rootPath,
                sourcePath,
                hashContents,
                cancellationToken).ConfigureAwait(false);
            if (!fingerprints.TryAdd(fingerprint.RelativePath, fingerprint))
                throw new InvalidDataException($"Duplicate source path after normalization: {fingerprint.RelativePath}");
            int completedFiles = sourceIndex + 1;
            if (ShouldReportProgress(completedFiles, sourceFiles.Count))
            {
                ReportProgress(
                    progress,
                    MinecraftWorldMountStage.FingerprintingFiles,
                    completedFiles,
                    sourceFiles.Count,
                    sourceRelativePath);
            }
        }

        if (!fingerprints.TryGetValue("level.dat", out MinecraftFileFingerprint? levelFingerprint))
            throw new IOException("level.dat disappeared while the world was being mounted.");

        ReportProgress(progress, MinecraftWorldMountStage.ReadingLevelMetadata, 0, 1, "level.dat");
        LevelDatReadResult level = await MinimalLevelDatReader.ReadAsync(
            rootPath,
            levelDatPath,
            cancellationToken).ConfigureAwait(false);
        ReportProgress(progress, MinecraftWorldMountStage.ReadingLevelMetadata, 1, 1, "level.dat");
        diagnostics.AddRange(level.Diagnostics);
        if (!await MinecraftSourceFiles.MatchesFingerprintAsync(
                rootPath,
                levelFingerprint,
                cancellationToken).ConfigureAwait(false))
        {
            throw new IOException("level.dat changed while the world was being mounted.");
        }

        ReportProgress(progress, MinecraftWorldMountStage.DiscoveringRegions, 0, 0);
        IReadOnlyList<RegionSource> regions = DiscoverRegions(rootPath, request.IncludeCustomDimensions, diagnostics);
        if (regions.Count == 0)
            throw new InvalidDataException("No Anvil region/*.mca files were found in the selected world.");

        ReportProgress(progress, MinecraftWorldMountStage.IndexingRegions, 0, regions.Count);
        HashSet<MinecraftRegionAddress> uniqueRegions = new();
        List<MinecraftChunkIndexEntry> chunkEntries = new();
        for (int regionIndex = 0; regionIndex < regions.Count; regionIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RegionSource regionSource = regions[regionIndex];
            string relativePath = MinecraftSourceFiles.NormalizeRelativePath(rootPath, regionSource.FilePath);
            if (!fingerprints.TryGetValue(relativePath, out MinecraftFileFingerprint? fingerprint))
                throw new IOException($"Region disappeared while the world was being mounted: {relativePath}");

            MinecraftRegionAddress region = new(regionSource.Dimension, regionSource.RegionX, regionSource.RegionZ);
            if (!uniqueRegions.Add(region))
                throw new InvalidDataException($"Duplicate Anvil region address was found: {region}");

            IReadOnlyList<MinecraftChunkIndexEntry> regionEntries =
                await AnvilChunkIndex.ReadRegionHeaderAsync(
                    regionSource.FilePath,
                    region,
                    fingerprint,
                    diagnostics,
                    cancellationToken).ConfigureAwait(false);
            chunkEntries.AddRange(regionEntries);
            int completedRegions = regionIndex + 1;
            if (ShouldReportProgress(completedRegions, regions.Count))
            {
                ReportProgress(
                    progress,
                    MinecraftWorldMountStage.IndexingRegions,
                    completedRegions,
                    regions.Count,
                    relativePath);
            }
        }

        AnvilChunkIndex chunkIndex = new(chunkEntries);
        if (chunkIndex.TotalChunkCount == 0)
            throw new InvalidDataException(
                "Anvil region files were found, but every location header was empty. The mount was rejected instead of returning an empty world.");

        IReadOnlyList<MinecraftDimensionDescriptor> dimensions = regions
            .GroupBy(static source => source.Dimension)
            .Select(group => new MinecraftDimensionDescriptor(
                group.Key,
                group.Select(source => MinecraftSourceFiles.NormalizeRelativePath(rootPath, source.RegionDirectory))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                DeclaredBuildRange: null))
            .OrderBy(static dimension => dimension.Id.Value, StringComparer.Ordinal)
            .ToArray();
        IReadOnlyList<MinecraftAuxiliaryFile> auxiliaryFiles = BuildAuxiliaryFiles(rootPath, fingerprints);
        string sourceRevision = MinecraftSourceFiles.ComputeRevision(fingerprints.Values);
        string fallbackLevelName = new DirectoryInfo(rootPath).Name;
        MinecraftWorldDescriptor descriptor = new(
            rootPath,
            string.IsNullOrWhiteSpace(level.LevelName) ? fallbackLevelName : level.LevelName,
            sourceRevision,
            request.CaptureSourceFingerprints
                ? MinecraftSourceRevisionStrength.ContentHash
                : MinecraftSourceRevisionStrength.MetadataOnly,
            level.Version,
            level.SpawnLocation,
            dimensions,
            sessionLockState,
            DateTimeOffset.UtcNow,
            diagnostics.ToArray());
        bool forceVerification = request.ConcurrentWritePolicy == ConcurrentWorldWritePolicy.VerifyEveryRead;

        MountedAnvilWorld mountedWorld = new(
            rootPath,
            descriptor,
            chunkIndex,
            fingerprints,
            auxiliaryFiles,
            level.OriginalPayload,
            forceVerification);
        ReportProgress(progress, MinecraftWorldMountStage.Completed, 1, 1);
        return mountedWorld;
    }

    private static bool ShouldReportProgress(int completedItems, int totalItems) =>
        completedItems == 1 || completedItems == totalItems || completedItems % ProgressReportInterval == 0;

    private static void ReportProgress(
        IProgress<MinecraftWorldMountProgress>? progress,
        MinecraftWorldMountStage stage,
        int completedItems,
        int totalItems,
        string? currentRelativePath = null)
    {
        if (progress is null) return;
        try
        {
            progress.Report(new MinecraftWorldMountProgress(
                stage,
                completedItems,
                totalItems,
                currentRelativePath));
        }
        catch
        {
            // A status observer must never turn a safe read-only mount into a failure.
        }
    }

    private static MinecraftSessionLockState InspectSessionLock(string rootPath)
    {
        string lockPath = Path.Combine(rootPath, "session.lock");
        if (!File.Exists(lockPath))
            return MinecraftSessionLockState.Missing;

        try
        {
            using FileStream stream = new(
                lockPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None,
                bufferSize: 1,
                FileOptions.RandomAccess);
            _ = stream.Length;
            return MinecraftSessionLockState.PresentAndStable;
        }
        catch (IOException)
        {
            return MinecraftSessionLockState.SuspectedActiveWriter;
        }
        catch (UnauthorizedAccessException)
        {
            return MinecraftSessionLockState.Unknown;
        }
    }

    private static IReadOnlyList<RegionSource> DiscoverRegions(
        string rootPath,
        bool includeCustomDimensions,
        ICollection<MinecraftDiagnostic> diagnostics)
    {
        EnumerationOptions recursiveOptions = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        List<RegionSource> sources = new();

        foreach (string regionDirectory in Directory.EnumerateDirectories(
                     rootPath,
                     "region",
                     recursiveOptions).OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            string relativeDirectory = MinecraftSourceFiles.NormalizeRelativePath(rootPath, regionDirectory);
            (MinecraftDimensionId Dimension, bool IsCustom, bool IsRecognized) classification =
                ClassifyDimension(relativeDirectory);
            if (classification.IsCustom && !includeCustomDimensions)
                continue;
            if (!classification.IsRecognized)
            {
                diagnostics.Add(new MinecraftDiagnostic(
                    "dimension.local_path",
                    MinecraftDiagnosticSeverity.Information,
                    $"非标准维度目录 {relativeDirectory} 已作为 {classification.Dimension} 挂载。"));
            }

            foreach (string filePath in Directory.EnumerateFiles(regionDirectory, "*", SearchOption.TopDirectoryOnly)
                         .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
            {
                Match match = RegionFileName.Match(Path.GetFileName(filePath));
                if (!match.Success)
                    continue;
                if (!int.TryParse(match.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int regionX) ||
                    !int.TryParse(match.Groups[2].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int regionZ))
                {
                    throw new InvalidDataException($"Region coordinates are outside Int32: {filePath}");
                }

                sources.Add(new RegionSource(
                    classification.Dimension,
                    regionDirectory,
                    filePath,
                    regionX,
                    regionZ));
            }
        }

        return sources;
    }

    private static (MinecraftDimensionId Dimension, bool IsCustom, bool IsRecognized) ClassifyDimension(
        string relativeRegionDirectory)
    {
        string[] segments = relativeRegionDirectory.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 1 && segments[0].Equals("region", StringComparison.OrdinalIgnoreCase))
            return (MinecraftDimensionId.Overworld, false, true);
        if (segments.Length >= 2 && segments[^2].Equals("DIM-1", StringComparison.OrdinalIgnoreCase))
            return (MinecraftDimensionId.Nether, false, true);
        if (segments.Length >= 2 && segments[^2].Equals("DIM1", StringComparison.OrdinalIgnoreCase))
            return (MinecraftDimensionId.End, false, true);

        int dimensionsIndex = Array.FindIndex(
            segments,
            static segment => segment.Equals("dimensions", StringComparison.OrdinalIgnoreCase));
        if (dimensionsIndex >= 0 && dimensionsIndex + 3 <= segments.Length - 1)
        {
            string dimensionNamespace = SanitizeIdentifierPart(segments[dimensionsIndex + 1]);
            string dimensionPath = string.Join(
                '/',
                segments[(dimensionsIndex + 2)..^1].Select(SanitizeIdentifierPart));
            return (new MinecraftDimensionId($"{dimensionNamespace}:{dimensionPath}"), true, true);
        }

        string localPath = string.Join(
            '/',
            segments[..^1].Select(SanitizeIdentifierPart));
        if (localPath.Length == 0)
            localPath = "unknown";
        return (new MinecraftDimensionId($"zhujiejing:{localPath}"), true, false);
    }

    private static string SanitizeIdentifierPart(string value)
    {
        Span<char> result = stackalloc char[value.Length];
        int written = 0;
        foreach (char character in value.ToLowerInvariant())
        {
            result[written++] = character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.'
                ? character
                : '_';
        }

        return written == 0 ? "unknown" : new string(result[..written]);
    }

    private static IReadOnlyList<MinecraftAuxiliaryFile> BuildAuxiliaryFiles(
        string rootPath,
        IReadOnlyDictionary<string, MinecraftFileFingerprint> fingerprints) => fingerprints.Values
        .Where(static fingerprint => !IsRegionPayload(fingerprint.RelativePath) &&
                                     !fingerprint.RelativePath.Equals("level.dat", StringComparison.OrdinalIgnoreCase))
        .Select(fingerprint => new MinecraftAuxiliaryFile(
            fingerprint.RelativePath,
            fingerprint.Length,
            fingerprint,
            ClassifyAuxiliaryFile(fingerprint.RelativePath)))
        .OrderBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static bool IsRegionPayload(string relativePath)
    {
        string extension = Path.GetExtension(relativePath);
        if (!extension.Equals(".mca", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".mcc", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? parent = Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar));
        return parent is not null && Path.GetFileName(parent).Equals("region", StringComparison.OrdinalIgnoreCase);
    }

    private static MinecraftAuxiliaryFileKind ClassifyAuxiliaryFile(string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/');
        string fileName = Path.GetFileName(normalized);
        if (normalized.Equals("session.lock", StringComparison.OrdinalIgnoreCase))
            return MinecraftAuxiliaryFileKind.SessionLock;
        if (normalized.Equals("icon.png", StringComparison.OrdinalIgnoreCase))
            return MinecraftAuxiliaryFileKind.WorldIcon;
        if (normalized.Equals("resources.zip", StringComparison.OrdinalIgnoreCase))
            return MinecraftAuxiliaryFileKind.EmbeddedResourcePack;
        if (HasDirectorySegment(normalized, "playerdata"))
            return MinecraftAuxiliaryFileKind.PlayerData;
        if (HasDirectorySegment(normalized, "advancements"))
            return MinecraftAuxiliaryFileKind.Advancements;
        if (HasDirectorySegment(normalized, "stats"))
            return MinecraftAuxiliaryFileKind.Statistics;
        if (HasDirectorySegment(normalized, "entities"))
            return MinecraftAuxiliaryFileKind.EntityData;
        if (HasDirectorySegment(normalized, "poi"))
            return MinecraftAuxiliaryFileKind.PointOfInterestData;
        if (HasDirectorySegment(normalized, "datapacks"))
            return MinecraftAuxiliaryFileKind.DataPacks;
        if (HasDirectorySegment(normalized, "data") ||
            fileName.Equals("level.dat_old", StringComparison.OrdinalIgnoreCase))
        {
            return MinecraftAuxiliaryFileKind.WorldData;
        }

        return MinecraftAuxiliaryFileKind.Unknown;
    }

    private static bool HasDirectorySegment(string path, string segment) =>
        path.Split('/').SkipLast(1).Any(item => item.Equals(segment, StringComparison.OrdinalIgnoreCase));

    private sealed record RegionSource(
        MinecraftDimensionId Dimension,
        string RegionDirectory,
        string FilePath,
        int RegionX,
        int RegionZ);
}
