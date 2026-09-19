namespace ZhuJieJing.Minecraft;

/// <summary>
/// Identifies a Java Edition dimension without restricting custom namespaced dimensions.
/// </summary>
public readonly record struct MinecraftDimensionId(string Value)
{
    public static readonly MinecraftDimensionId Overworld = new("minecraft:overworld");
    public static readonly MinecraftDimensionId Nether = new("minecraft:the_nether");
    public static readonly MinecraftDimensionId End = new("minecraft:the_end");

    public override string ToString() => Value;
}

/// <summary>Identifies a chunk in one dimension.</summary>
public readonly record struct MinecraftChunkAddress(MinecraftDimensionId Dimension, int X, int Z);

/// <summary>Identifies the Anvil region that contains a chunk.</summary>
public readonly record struct MinecraftRegionAddress(MinecraftDimensionId Dimension, int X, int Z)
{
    public const int ChunksPerAxis = 32;

    public static MinecraftRegionAddress FromChunk(MinecraftChunkAddress chunk) =>
        new(chunk.Dimension, FloorDiv(chunk.X, ChunksPerAxis), FloorDiv(chunk.Z, ChunksPerAxis));

    private static int FloorDiv(int value, int divisor)
    {
        int quotient = value / divisor;
        int remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }
}

/// <summary>Inclusive vertical block-coordinate range.</summary>
public readonly record struct BlockYRange
{
    public BlockYRange(int minimum, int maximum)
    {
        if (minimum > maximum)
            throw new ArgumentOutOfRangeException(nameof(minimum), "Minimum Y cannot be greater than maximum Y.");

        Minimum = minimum;
        Maximum = maximum;
    }

    public int Minimum { get; }

    public int Maximum { get; }

    public long Height => (long)Maximum - Minimum + 1;

    public BlockYRange Translate(int offset) =>
        new(checked(Minimum + offset), checked(Maximum + offset));
}

/// <summary>Known Java Anvil storage families relevant to normalization.</summary>
public enum MinecraftStorageFamily
{
    Unknown,
    LegacyNumericAnvil,
    FlattenedPalette,
    ModernSectionPalette,
}

/// <summary>Version evidence read from level or chunk metadata.</summary>
public sealed record MinecraftVersionDescriptor(
    int? DataVersion,
    string? VersionName,
    MinecraftStorageFamily StorageFamily);

/// <summary>Compression byte values used by modern Anvil chunk records.</summary>
public enum MinecraftChunkCompression
{
    GZip = 1,
    ZLib = 2,
    None = 3,
    Lz4 = 4,
    Unknown = 255,
}

/// <summary>Describes whether the chunk payload is inline or stored in an external MCC file.</summary>
public enum MinecraftChunkStorageKind
{
    UnknownUntilRead,
    InlineRegionSector,
    ExternalMcc,
}
