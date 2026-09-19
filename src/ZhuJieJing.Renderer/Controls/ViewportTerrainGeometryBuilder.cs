using ZhuJieJing.Core;
using ZhuJieJing.Renderer.Meshing;

namespace ZhuJieJing.Renderer.Controls;

/// <summary>Builds the finite working terrain around the camera without changing scene facts.</summary>
public static class ViewportTerrainGeometryBuilder
{
    public static (VoxelVertex[] Vertices, uint[] Indices) Build(
        ViewportTerrainMode mode,
        int centerX,
        int centerZ,
        int radius,
        IBlockFaceMaterialResolver? materials)
    {
        if(mode == ViewportTerrainMode.Transparent) throw new ArgumentOutOfRangeException(nameof(mode));
        if(radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
        int minimumX = checked(centerX - radius);
        int minimumZ = checked(centerZ - radius);
        int maximumX = checked(centerX + radius);
        int maximumZ = checked(centerZ + radius);
        int span = checked(radius * 2);
        var coordinate = new SectionCoordinate("minecraft:overworld", 0, 0, 0);

        if(mode == ViewportTerrainMode.Chunks)
        {
            var guide = new BlockState("minecraft:stone");
            var mesh = new SectionMesh(
                coordinate,
                [new GreedyQuadBatch(guide, BlockRenderLayer.Translucent,
                [
                    new GreedyQuad(BlockFace.Up, new BlockPosition(minimumX, 0, minimumZ), span, span),
                    new GreedyQuad(BlockFace.Down, new BlockPosition(minimumX, 0, minimumZ), span, span),
                ])],
                []);
            SectionRenderGeometry geometry = SectionRenderGeometryBuilder.Build(mesh);
            return (geometry.Vertices, geometry.Indices);
        }

        var grass = new BlockState("minecraft:grass_block", new Dictionary<string, string> { ["snowy"] = "false" });
        var dirt = new BlockState("minecraft:dirt");
        var bedrock = new BlockState("minecraft:bedrock");
        GreedyQuad[] grassFaces =
        [
            new(BlockFace.Up, new BlockPosition(minimumX, 0, minimumZ), span, span),
            new(BlockFace.West, new BlockPosition(minimumX, -1, minimumZ), span, 1),
            new(BlockFace.East, new BlockPosition(maximumX, -1, minimumZ), span, 1),
            new(BlockFace.North, new BlockPosition(minimumX, -1, minimumZ), span, 1),
            new(BlockFace.South, new BlockPosition(minimumX, -1, maximumZ), span, 1),
        ];
        GreedyQuad[] dirtFaces = SideFaces(minimumX, minimumZ, maximumX, maximumZ, -3, span, 2);
        GreedyQuad[] bedrockFaces =
        [
            .. SideFaces(minimumX, minimumZ, maximumX, maximumZ, -4, span, 1),
            new(BlockFace.Down, new BlockPosition(minimumX, -4, minimumZ), span, span),
        ];
        var terrainMesh = new SectionMesh(
            coordinate,
            [
                new GreedyQuadBatch(grass, BlockRenderLayer.Opaque, grassFaces),
                new GreedyQuadBatch(dirt, BlockRenderLayer.Opaque, dirtFaces),
                new GreedyQuadBatch(bedrock, BlockRenderLayer.Opaque, bedrockFaces),
            ],
            []);
        SectionRenderGeometry terrain = SectionRenderGeometryBuilder.Build(terrainMesh, materials);
        return (terrain.Vertices, terrain.Indices);
    }

    private static GreedyQuad[] SideFaces(
        int minimumX,
        int minimumZ,
        int maximumX,
        int maximumZ,
        int y,
        int span,
        int height) =>
    [
        new(BlockFace.West, new BlockPosition(minimumX, y, minimumZ), span, height),
        new(BlockFace.East, new BlockPosition(maximumX, y, minimumZ), span, height),
        new(BlockFace.North, new BlockPosition(minimumX, y, minimumZ), span, height),
        new(BlockFace.South, new BlockPosition(minimumX, y, maximumZ), span, height),
    ];
}
