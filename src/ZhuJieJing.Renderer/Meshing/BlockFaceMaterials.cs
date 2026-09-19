using System.Numerics;
using ZhuJieJing.Core;

namespace ZhuJieJing.Renderer.Meshing;

/// <summary>A stable rectangle in a shared texture atlas.</summary>
public readonly record struct TextureAtlasRegion(float U, float V, float Width, float Height)
{
    public static TextureAtlasRegion EntireTexture { get; } = new(0f, 0f, 1f, 1f);
}

/// <summary>Texture and multiplicative tint for one resolved block face.</summary>
public readonly record struct BlockFaceMaterial(
    TextureAtlasRegion AtlasRegion,
    Vector4 Tint,
    string? TextureKey,
    bool IsFallback = false,
    bool HasTransparentPixels = false,
    bool HasTranslucentPixels = false)
{
    public static BlockFaceMaterial Untextured(Vector4 tint) => new(
        TextureAtlasRegion.EntireTexture,
        tint,
        null,
        true);
}

public interface IBlockFaceMaterialResolver
{
    BlockFaceMaterial ResolveFace(BlockState state, BlockFace face);
}

public interface IBlockFaceLayerResolver : IBlockFaceMaterialResolver
{
    IReadOnlyList<BlockFaceMaterial> ResolveFaceLayers(BlockState state, BlockFace face);
}

/// <summary>A face from a resource-pack model element. Positions and UVs use Minecraft's 0..16 model space.</summary>
public sealed record ResolvedModelFace(
    BlockFace Face,
    Vector4 TextureUv,
    BlockFace? CullFace,
    int TextureRotation,
    BlockFaceMaterial Material);

public sealed record ResolvedModelElement(
    Vector3 From,
    Vector3 To,
    IReadOnlyList<ResolvedModelFace> Faces,
    ModelElementRotation? Rotation = null,
    bool Shade = true);

public sealed record ModelElementRotation(
    Vector3 Origin,
    char Axis,
    float AngleDegrees,
    bool Rescale);

public sealed record ResolvedBlockModel(
    IReadOnlyList<ResolvedModelElement> Elements,
    int XRotation,
    int YRotation,
    bool UvLock);

public interface IBlockModelGeometryResolver : IBlockFaceMaterialResolver
{
    bool TryResolveModel(BlockState state, out IReadOnlyList<ResolvedBlockModel> models);
}
