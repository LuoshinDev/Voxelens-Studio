using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

/// <summary>How an exporter handles a destination that already exists.</summary>
public enum MinecraftExportDestinationPolicy
{
    FailIfExists,
    CreateUniqueSibling,
    ReplaceWithTimestampedBackup,
}

/// <summary>Metadata and streaming chunk source for a world-folder export.</summary>
public sealed record MinecraftWorldExportRequest(
    string DestinationDirectory,
    string ExpectedSourceRevision,
    MinecraftTargetProfile Target,
    INormalizedMinecraftChunkSource Chunks,
    MinecraftOpaquePayload SourceLevelMetadata,
    BlockPosition SpawnLocation,
    int YOffset,
    MinecraftUnknownDataPolicy UnknownDataPolicy,
    IMinecraftAuxiliaryFileSource? AuxiliaryFiles = null,
    MinecraftExportDestinationPolicy DestinationPolicy = MinecraftExportDestinationPolicy.FailIfExists,
    MinecraftAuxiliaryExportPolicy? AuxiliaryFilePolicy = null);

/// <summary>
/// Opt-in policy for copying source files that are outside canonical chunk data. A Java 1.12.2 cross-version
/// export only enables the two byte-oriented presentation assets by default; version- or coordinate-bearing
/// data stays excluded until a caller can prove that it is compatible with the rewritten world. Unknown files
/// are never accepted by the 1.12.2 exporter; PreserveUnknownFiles remains only for source compatibility.
/// </summary>
public sealed record MinecraftAuxiliaryExportPolicy(
    bool PreserveWorldData = false,
    bool PreservePlayerData = false,
    bool PreserveAdvancements = false,
    bool PreserveStatistics = false,
    bool PreserveWorldIcon = true,
    bool PreserveEmbeddedResourcePack = true,
    bool PreserveUnknownFiles = false);

/// <summary>Logical stage of a direct-to-folder streaming export.</summary>
public enum MinecraftWorldExportStage
{
    Validating,
    PreparingStagingDirectory,
    WritingLevelMetadata,
    WritingRegions,
    CopyingAuxiliaryFiles,
    CommittingDirectory,
    Complete,
}

/// <summary>Bounded progress information; it never contains the whole output world in memory.</summary>
public sealed record MinecraftWorldExportProgress(
    MinecraftWorldExportStage Stage,
    long ChunksRead,
    long ChunksWritten,
    long RegionsWritten,
    long BytesWritten,
    MinecraftChunkAddress? CurrentChunk,
    string Message);

/// <summary>Completed export report.</summary>
public sealed record MinecraftWorldExportResult(
    string OutputDirectory,
    string? BackupDirectory,
    long ChunkCount,
    long RegionCount,
    long BlockCount,
    long CopiedOpaqueChunkCount,
    long ReencodedChunkCount,
    long VerticallyClippedBlockCount,
    IReadOnlyList<MinecraftDiagnostic> Diagnostics);

/// <summary>
/// Writes a Java world folder through a staging directory. Implementations must consume chunks incrementally,
/// flush one target region at a time, preserve unchanged raw payloads when policy allows, and atomically commit
/// only after validation. Exporting a schematic or building an in-memory ZIP is outside this contract.
/// </summary>
public interface IMinecraftWorldExporter
{
    Task<MinecraftWorldExportResult> ExportAsync(
        MinecraftWorldExportRequest request,
        IProgress<MinecraftWorldExportProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
