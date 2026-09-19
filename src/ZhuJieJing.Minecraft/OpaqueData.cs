namespace ZhuJieJing.Minecraft;

/// <summary>Encoding of a payload that the Minecraft adapter must preserve but need not understand.</summary>
public enum MinecraftOpaquePayloadFormat
{
    RawFile,
    StoredChunkPayload,
    DecompressedNbtDocument,
    EncodedNbtTag,
}

/// <summary>
/// Byte-exact source data retained for round trips. ContentHash is required when a payload may be copied into
/// a later export so callers can prove that it still belongs to the indexed source revision.
/// </summary>
public sealed record MinecraftOpaquePayload(
    MinecraftOpaquePayloadFormat Format,
    ReadOnlyMemory<byte> Bytes,
    string SourceRelativePath,
    string? NbtPath,
    string? ContentHash);

/// <summary>Scope of an NBT fragment that is not represented by the canonical scene model.</summary>
public enum MinecraftOpaqueScope
{
    Level,
    ChunkRoot,
    Section,
    BlockEntity,
    Entity,
    AuxiliaryFile,
}

/// <summary>Opaque data associated with a normalized object and its required round-trip behavior.</summary>
public sealed record MinecraftOpaqueFragment(
    MinecraftOpaqueScope Scope,
    string NbtPath,
    MinecraftOpaquePayload Payload,
    bool RequiredForRoundTrip);

/// <summary>Handling of unknown NBT when a containing chunk must be rewritten.</summary>
public enum ModifiedChunkOpaqueDataStrategy
{
    /// <summary>Merge preserved tags into the rewritten known structure; fail if a safe merge is unavailable.</summary>
    MergeOrFail,

    /// <summary>Drop only data explicitly marked optional and emit a diagnostic for every dropped path.</summary>
    DropOptionalWithDiagnostics,

    /// <summary>Block rewriting any chunk that contains unknown data.</summary>
    RejectModifiedChunk,
}

/// <summary>
/// Unknown-data policy. Unchanged chunks are copied byte-for-byte by default; modified chunks never silently
/// lose unknown NBT.
/// </summary>
public sealed record MinecraftUnknownDataPolicy(
    bool CopyUnchangedChunkPayload = true,
    bool PreserveUnknownLevelTags = true,
    bool PreserveUnknownAuxiliaryFiles = true,
    ModifiedChunkOpaqueDataStrategy ModifiedChunkStrategy = ModifiedChunkOpaqueDataStrategy.MergeOrFail);

/// <summary>Passthrough material retained alongside one canonical chunk.</summary>
public sealed record MinecraftChunkPassthrough(
    MinecraftChunkCompression OriginalCompression,
    MinecraftChunkStorageKind OriginalStorageKind,
    MinecraftOpaquePayload OriginalStoredPayload,
    IReadOnlyList<MinecraftOpaqueFragment> UnknownFragments,
    bool SupportsStructuredMerge);
