using System.Text.Json.Serialization;

namespace ZhuJieJing.Core;

public sealed record ZjjCoordinateSystem
{
    public const string MinecraftUnit = "block";
    public const string MinecraftAxes = "x-east,y-up,z-south";

    public string Unit { get; init; } = MinecraftUnit;

    public string Axes { get; init; } = MinecraftAxes;

    public static ZjjCoordinateSystem MinecraftJava { get; } = new();
}

public sealed record ZjjSceneReference
{
    public required string Scene { get; init; }

    public required string Revision { get; init; }
}

public enum ZjjPlanModuleKind
{
    District,
    Region,
    WorkZone,
}

public sealed record ZjjPlanModule
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public ZjjPlanModuleKind Kind { get; init; } = ZjjPlanModuleKind.WorkZone;

    public BoxSelection? Bounds { get; init; }

    public int? RegionX { get; init; }

    public int? RegionZ { get; init; }
}

/// <summary>
/// Optional Minecraft presentation identity. It selects compatible client resources without making the
/// canonical block states or coordinates version-dependent.
/// </summary>
public sealed record ZjjVisualProfile
{
    public const string JavaEdition = "java";
    public const string UnknownStorageFamily = "unknown";
    public const string LegacyNumericAnvilStorageFamily = "legacyNumericAnvil";
    public const string FlattenedPaletteStorageFamily = "flattenedPalette";
    public const string ModernSectionPaletteStorageFamily = "modernSectionPalette";

    public string Edition { get; init; } = JavaEdition;

    public string? VersionName { get; init; }

    public int? DataVersion { get; init; }

    public string StorageFamily { get; init; } = UnknownStorageFamily;
}

public sealed record ZjjPlan
{
    public const string CurrentFormat = "zhujie.plan/2";

    public string Format { get; init; } = CurrentFormat;

    public required string PlanId { get; init; }

    public string Dimension { get; init; } = "minecraft:overworld";

    public ZjjVisualProfile? VisualProfile { get; init; }

    [JsonRequired]
    public ZjjSceneReference? Base { get; init; }

    [JsonRequired]
    public ZjjPlanModule Module { get; init; } = new()
    {
        Id = "main",
        DisplayName = "主体施工区",
    };

    public ZjjCoordinateSystem CoordinateSystem { get; init; } = new();

    /// <summary>The player's feet position used by preview navigation and exported Minecraft worlds.</summary>
    public BlockPosition? SpawnPoint { get; init; }

    public Dictionary<string, BoxSelection> Selections { get; init; } = new(StringComparer.Ordinal);

    public Dictionary<string, MaterialIntent> Materials { get; init; } = new(StringComparer.Ordinal);

    public List<PlanOperation> Operations { get; init; } = [];
}

public enum WriteStrategy
{
    Overwrite,
    OnlyAir,
    ReplaceMatching,
    FailOnConflict,
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(FillBoxOperation), "fillBox")]
[JsonDerivedType(typeof(ColumnsOperation), "columns")]
public abstract record PlanOperation
{
    public required string Id { get; init; }

    public required string SelectionId { get; init; }

    public required string MaterialId { get; init; }

    public WriteStrategy WriteStrategy { get; init; } = WriteStrategy.Overwrite;

    public BlockState? MatchState { get; init; }
}

public sealed record FillBoxOperation : PlanOperation
{
    public required BoxSelection Bounds { get; init; }
}

public sealed record ColumnsOperation : PlanOperation
{
    public List<BlockPosition> Origins { get; init; } = [];

    public int Height { get; init; }
}
