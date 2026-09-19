using ZhuJieJing.Core;

namespace ZhuJieJing.Renderer.Meshing;

public enum BlockRenderLayer
{
    Opaque,
    Cutout,
    Translucent,
}

public enum BlockGeometryKind
{
    Empty,
    Invisible,
    FullCube,
    Model,
    UnknownPlaceholder,
}

public enum BlockFace
{
    West,
    East,
    Down,
    Up,
    North,
    South,
}

[Flags]
public enum BlockFaceMask
{
    None = 0,
    West = 1 << 0,
    East = 1 << 1,
    Down = 1 << 2,
    Up = 1 << 3,
    North = 1 << 4,
    South = 1 << 5,
    All = West | East | Down | Up | North | South,
}

public sealed record BlockRenderDefinition(
    BlockGeometryKind GeometryKind,
    BlockRenderLayer Layer,
    BlockFaceMask OccludingFaces,
    string? ModelKey = null)
{
    public static BlockRenderDefinition Empty { get; } = new(
        BlockGeometryKind.Empty,
        BlockRenderLayer.Opaque,
        BlockFaceMask.None);

    /// <summary>
    /// An intentional non-air block with no rendered geometry, such as the vanilla barrier block.
    /// This is distinct from Empty so the mesher can continue turning accidental non-air Empty
    /// resolutions into visible unknown placeholders.
    /// </summary>
    public static BlockRenderDefinition Invisible { get; } = new(
        BlockGeometryKind.Invisible,
        BlockRenderLayer.Opaque,
        BlockFaceMask.None);

    public static BlockRenderDefinition FullCube(
        BlockRenderLayer layer = BlockRenderLayer.Opaque,
        BlockFaceMask occludingFaces = BlockFaceMask.All) => new(
        BlockGeometryKind.FullCube,
        layer,
        occludingFaces);

    public static BlockRenderDefinition Model(
        string modelKey,
        BlockRenderLayer layer = BlockRenderLayer.Cutout,
        BlockFaceMask occludingFaces = BlockFaceMask.None) => new(
        BlockGeometryKind.Model,
        layer,
        occludingFaces,
        modelKey);

    public static BlockRenderDefinition Unknown(string modelKey = "zhujie:unknown_block") => new(
        BlockGeometryKind.UnknownPlaceholder,
        BlockRenderLayer.Opaque,
        BlockFaceMask.None,
        modelKey);
}

public interface IBlockRenderResolver
{
    BlockRenderDefinition? Resolve(BlockState state);
}

/// <summary>
/// Lets a lazy render-resource resolver coalesce externally visible cache revisions while one Section batch is
/// being meshed. The scope must not suppress the underlying resource loads, only their publication signal.
/// </summary>
public interface ISectionRenderBatchSource
{
    IDisposable BeginSectionRenderBatch();
}

/// <summary>
/// Resource-pack-independent fallback used by the viewport until blockstate/model parsing is connected.
/// Every non-air state remains visible as a cube, while the normalizer's explicit unknown state stays marked
/// as an attention-grabbing unknown placeholder.
/// </summary>
public sealed class CanonicalFallbackBlockRenderResolver : IBlockRenderResolver
{
    public const string VisibleUnknownLegacyBlock = "zhujiejing:visible_unknown_legacy_block";

    public BlockRenderDefinition Resolve(BlockState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if(state.IsAir) return BlockRenderDefinition.Empty;
        if(state.Name == "minecraft:barrier") return BlockRenderDefinition.Invisible;
        if(FallbackBlockColor.AlphaForCanonicalName(state.Name) < 1f)
            return BlockRenderDefinition.FullCube(BlockRenderLayer.Translucent, BlockFaceMask.None);
        return string.Equals(state.Name, VisibleUnknownLegacyBlock, StringComparison.Ordinal)
            ? BlockRenderDefinition.Unknown()
            : BlockRenderDefinition.FullCube();
    }
}

public sealed class BlockRenderRegistry : IBlockRenderResolver
{
    private readonly Dictionary<string, BlockRenderDefinition> _definitions = new(StringComparer.Ordinal);

    public BlockRenderRegistry(string unknownModelKey = "zhujie:unknown_block")
    {
        if(string.IsNullOrWhiteSpace(unknownModelKey)) throw new ArgumentException("未知方块模型键不能为空。", nameof(unknownModelKey));
        UnknownDefinition = BlockRenderDefinition.Unknown(unknownModelKey);
    }

    public BlockRenderDefinition UnknownDefinition { get; }

    public BlockRenderRegistry Register(BlockState state, BlockRenderDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(definition);
        if(state.IsAir) throw new ArgumentException("空气由网格器固定处理，不能注册为可见模型。", nameof(state));
        if(definition.GeometryKind == BlockGeometryKind.Empty) throw new ArgumentException("非空气方块不能注册为空模型。", nameof(definition));

        _definitions[state.CanonicalKey] = definition;
        return this;
    }

    public BlockRenderRegistry RegisterFullCube(
        BlockState state,
        BlockRenderLayer layer = BlockRenderLayer.Opaque,
        BlockFaceMask occludingFaces = BlockFaceMask.All) => Register(state, BlockRenderDefinition.FullCube(layer, occludingFaces));

    public BlockRenderRegistry RegisterInvisible(BlockState state) => Register(state, BlockRenderDefinition.Invisible);

    public BlockRenderRegistry RegisterModel(
        BlockState state,
        string modelKey,
        BlockRenderLayer layer = BlockRenderLayer.Cutout,
        BlockFaceMask occludingFaces = BlockFaceMask.None) => Register(state, BlockRenderDefinition.Model(modelKey, layer, occludingFaces));

    public BlockRenderDefinition Resolve(BlockState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if(state.IsAir) return BlockRenderDefinition.Empty;
        return _definitions.TryGetValue(state.CanonicalKey, out var definition) ? definition : UnknownDefinition;
    }
}
