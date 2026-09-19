using ZhuJieJing.Core;

namespace ZhuJieJing.Renderer.Meshing;

public readonly record struct GreedyQuad(
    BlockFace Face,
    BlockPosition Origin,
    int ULength,
    int VLength,
    VoxelFaceLighting? Lighting = null)
{
    public int Area => checked(ULength * VLength);

    public VoxelFaceLighting EffectiveLighting => Lighting ?? VoxelFaceLighting.FullBright;
}

public sealed record GreedyQuadBatch(
    BlockState State,
    BlockRenderLayer Layer,
    IReadOnlyList<GreedyQuad> Quads);

public sealed record ModelInstanceRequest(
    BlockPosition Position,
    BlockState State,
    BlockRenderLayer Layer,
    string ModelKey,
    bool IsUnknownPlaceholder,
    BlockFaceMask HiddenFaces = BlockFaceMask.None,
    VoxelFaceLighting? Lighting = null)
{
    public VoxelFaceLighting EffectiveLighting => Lighting ?? VoxelFaceLighting.FullBright;
}

public sealed record SectionMesh(
    SectionCoordinate Coordinate,
    IReadOnlyList<GreedyQuadBatch> QuadBatches,
    IReadOnlyList<ModelInstanceRequest> ModelInstances)
{
    public int QuadCount => QuadBatches.Sum(batch => batch.Quads.Count);
}
