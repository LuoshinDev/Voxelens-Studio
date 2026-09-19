using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

/// <summary>Controls how a source world is mounted without ever mutating it.</summary>
public sealed record ReadOnlyWorldMountRequest(
    string WorldDirectory,
    ConcurrentWorldWritePolicy ConcurrentWritePolicy = ConcurrentWorldWritePolicy.RejectActiveWriter,
    bool IncludeCustomDimensions = true,
    bool CaptureSourceFingerprints = true);

/// <summary>Coarse-grained stages exposed while a read-only world mount is prepared.</summary>
public enum MinecraftWorldMountStage
{
    DiscoveringFiles,
    FingerprintingFiles,
    ReadingLevelMetadata,
    DiscoveringRegions,
    IndexingRegions,
    Completed,
}

/// <summary>Progress emitted while source files are fingerprinted and region headers are indexed.</summary>
public sealed record MinecraftWorldMountProgress(
    MinecraftWorldMountStage Stage,
    int CompletedItems,
    int TotalItems,
    string? CurrentRelativePath = null);

/// <summary>Policy used when Minecraft may still be writing the selected world.</summary>
public enum ConcurrentWorldWritePolicy
{
    /// <summary>Fail the mount if the session lock or changing file metadata indicates an active writer.</summary>
    RejectActiveWriter,

    /// <summary>Permit reading but verify every indexed file before its chunk is consumed.</summary>
    VerifyEveryRead,
}

/// <summary>Observed state of the world's session lock at mount time.</summary>
public enum MinecraftSessionLockState
{
    Missing,
    PresentAndStable,
    SuspectedActiveWriter,
    Unknown,
}

/// <summary>States whether a source revision hashes file contents or only stable filesystem metadata.</summary>
public enum MinecraftSourceRevisionStrength
{
    MetadataOnly,
    ContentHash,
}

/// <summary>Immutable metadata describing one mounted world.</summary>
public sealed record MinecraftWorldDescriptor(
    string RootPath,
    string LevelName,
    string SourceRevision,
    MinecraftSourceRevisionStrength SourceRevisionStrength,
    MinecraftVersionDescriptor Version,
    BlockPosition? SpawnLocation,
    IReadOnlyList<MinecraftDimensionDescriptor> Dimensions,
    MinecraftSessionLockState SessionLockState,
    DateTimeOffset MountedAtUtc,
    IReadOnlyList<MinecraftDiagnostic> Diagnostics);

/// <summary>Metadata for one dimension and its region directory.</summary>
public sealed record MinecraftDimensionDescriptor(
    MinecraftDimensionId Id,
    IReadOnlyList<string> RegionDirectories,
    BlockYRange? DeclaredBuildRange);

/// <summary>
/// A read-only view over a world folder. Implementations own open handles and must never expose write access
/// to the mounted source.
/// </summary>
public interface IReadOnlyMinecraftWorld : IAsyncDisposable, IMinecraftAuxiliaryFileSource
{
    MinecraftWorldDescriptor Descriptor { get; }

    IMinecraftChunkIndex ChunkIndex { get; }

    IMinecraftChunkReader ChunkReader { get; }

    /// <summary>Reads level.dat as a source payload for lossless metadata passthrough.</summary>
    ValueTask<MinecraftOpaquePayload> ReadLevelMetadataAsync(CancellationToken cancellationToken = default);

    /// <summary>Verifies that indexed source files have not changed since the mount was created.</summary>
    ValueTask<WorldSourceValidationResult> ValidateSourceAsync(CancellationToken cancellationToken = default);
}

/// <summary>Read-only descriptor for a world file outside level.dat and region payloads.</summary>
public sealed record MinecraftAuxiliaryFile(
    string RelativePath,
    long Length,
    MinecraftFileFingerprint Fingerprint,
    MinecraftAuxiliaryFileKind Kind,
    MinecraftOpaqueScope Scope = MinecraftOpaqueScope.AuxiliaryFile);

/// <summary>Classification used to avoid copying unsafe files such as session.lock into an export.</summary>
public enum MinecraftAuxiliaryFileKind
{
    WorldData,
    PlayerData,
    Advancements,
    Statistics,
    EntityData,
    PointOfInterestData,
    DataPacks,
    WorldIcon,
    EmbeddedResourcePack,
    SessionLock,
    Unknown,
}

/// <summary>Streams source files that are not represented as canonical chunks.</summary>
public interface IMinecraftAuxiliaryFileSource
{
    /// <summary>Enumerates descriptors without loading file contents.</summary>
    IAsyncEnumerable<MinecraftAuxiliaryFile> EnumerateAuxiliaryFilesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Opens one previously enumerated file with read-only access.</summary>
    ValueTask<Stream> OpenAuxiliaryFileAsync(
        MinecraftAuxiliaryFile file,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Streams every file captured by one read-only world mount, including level.dat and region payloads.
/// Descriptors are mount-owned capabilities and must be passed back unchanged when a file is opened.
/// </summary>
public interface IReadOnlyMinecraftWorldFileSource
{
    IAsyncEnumerable<MinecraftFileFingerprint> EnumerateWorldFilesAsync(
        CancellationToken cancellationToken = default);

    ValueTask<Stream> OpenWorldFileAsync(
        MinecraftFileFingerprint file,
        CancellationToken cancellationToken = default);
}

/// <summary>Creates read-only mounts; parsing and rendering are deliberately separate responsibilities.</summary>
public interface IReadOnlyMinecraftWorldMounter
{
    ValueTask<IReadOnlyMinecraftWorld> MountAsync(
        ReadOnlyWorldMountRequest request,
        CancellationToken cancellationToken = default,
        IProgress<MinecraftWorldMountProgress>? progress = null);
}

/// <summary>Result of checking source files against their mount-time fingerprints.</summary>
public sealed record WorldSourceValidationResult(
    bool IsStable,
    IReadOnlyList<string> ChangedRelativePaths,
    IReadOnlyList<MinecraftDiagnostic> Diagnostics);

/// <summary>A structured diagnostic suitable for UI, logs, conversion reports, and AI tools.</summary>
public sealed record MinecraftDiagnostic(
    string Code,
    MinecraftDiagnosticSeverity Severity,
    string Message,
    MinecraftChunkAddress? Chunk = null,
    string? NbtPath = null);

public enum MinecraftDiagnosticSeverity
{
    Information,
    Warning,
    Error,
    Blocking,
}
