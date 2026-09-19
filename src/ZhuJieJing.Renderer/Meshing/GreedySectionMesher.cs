using System.IO;
using ZhuJieJing.Core;
using ZhuJieJing.Minecraft;

namespace ZhuJieJing.Renderer.Meshing;

public sealed class GreedySectionMesher
{
    private const int SectionSize = 16;
    private const int SectionVolume = SectionSize * SectionSize * SectionSize;
    private static readonly BlockFace[] Faces = Enum.GetValues<BlockFace>();

    private readonly IBlockRenderResolver _renderResolver;
    private readonly BlockRenderDefinition _unknownDefinition;

    public GreedySectionMesher(IBlockRenderResolver renderResolver, string unknownModelKey = "zhujie:unknown_block")
    {
        _renderResolver = renderResolver ?? throw new ArgumentNullException(nameof(renderResolver));
        _unknownDefinition = BlockRenderDefinition.Unknown(unknownModelKey);
    }

    public SectionMesh Build(
        NormalizedMinecraftSection section,
        ISectionNeighborQuery? neighborQuery = null,
        bool missingSectionsAreAir = false)
    {
        NormalizedSectionNeighborQuery.ValidateSection(section);
        var states = new BlockState[SectionVolume];
        var definitions = new BlockRenderDefinition[SectionVolume];
        var definitionCache = new Dictionary<string, BlockRenderDefinition>(StringComparer.Ordinal);
        var instances = new List<ModelInstanceRequest>();
        var indices = section.PaletteIndices.Span;
        var paletteDefinitions = new BlockRenderDefinition?[section.Palette.Count];
        bool onlySolidFullCubes = true;

        for(var index = 0; index < SectionVolume; index++)
        {
            var paletteIndex = indices[index];
            if(paletteIndex >= section.Palette.Count)
            {
                throw new InvalidDataException($"Section {section.Coordinate} 的调色板索引 {paletteIndex} 越界。");
            }

            var state = section.Palette[paletteIndex] ?? throw new InvalidDataException($"Section {section.Coordinate} 的调色板包含空方块状态。");
            var definition = paletteDefinitions[paletteIndex] ??= ResolveVisibleDefinition(state, definitionCache);
            states[index] = state;
            definitions[index] = definition;
            onlySolidFullCubes &= definition.GeometryKind == BlockGeometryKind.FullCube &&
                                  definition.Layer == BlockRenderLayer.Opaque && definition.OccludingFaces == BlockFaceMask.All;

        }

        for(var index = 0; !onlySolidFullCubes && index < SectionVolume; index++)
        {
            var state = states[index];
            var definition = definitions[index];
            if(definition.GeometryKind is not (BlockGeometryKind.Model or BlockGeometryKind.UnknownPlaceholder)) continue;
            if(definition.GeometryKind == BlockGeometryKind.Model)
            {
                var connectedState = InferHorizontalConnections(index, state);
                if(!connectedState.Equals(state))
                {
                    var connectedDefinition = ResolveVisibleDefinition(connectedState, definitionCache);
                    if(connectedDefinition.GeometryKind == BlockGeometryKind.Model)
                    {
                        state = connectedState;
                        definition = connectedDefinition;
                    }
                }
            }
            instances.Add(new ModelInstanceRequest(
                WorldPosition(section.Coordinate, index),
                state,
                definition.Layer,
                definition.ModelKey!,
                definition.GeometryKind == BlockGeometryKind.UnknownPlaceholder,
                definition.GeometryKind == BlockGeometryKind.Model ? HiddenFaces(index) : BlockFaceMask.None,
                BuildModelLighting(index, state)));
        }

        var quadsByBatch = new Dictionary<BatchKey, List<GreedyQuad>>();
        foreach(var face in Faces)
        {
            // In a solid Section, every internal slice is already proven enclosed. Only its six
            // outer boundaries can contribute geometry; neighboring solid Sections yield no mesh.
            int firstSlice = onlySolidFullCubes && face is BlockFace.East or BlockFace.Up or BlockFace.South ? SectionSize - 1 : 0;
            int lastSlice = onlySolidFullCubes ? firstSlice : SectionSize - 1;
            for(var slice = firstSlice; slice <= lastSlice; slice++)
            {
                var mask = BuildFaceMask(face, slice);
                MergeMask(face, slice, mask, quadsByBatch, section.Coordinate);
            }
        }

        var batches = quadsByBatch
            .OrderBy(pair => pair.Key.Layer)
            .ThenBy(pair => pair.Key.State.CanonicalKey, StringComparer.Ordinal)
            .Select(pair => new GreedyQuadBatch(pair.Key.State, pair.Key.Layer, pair.Value.ToArray()))
            .ToArray();
        return new SectionMesh(section.Coordinate, batches, instances.ToArray());

        BlockFaceMask HiddenFaces(int localIndex)
        {
            int x = localIndex & 15;
            int z = localIndex >> 4 & 15;
            int y = localIndex >> 8 & 15;
            var hidden = BlockFaceMask.None;
            foreach(var face in Faces)
            {
                var (offsetX, offsetY, offsetZ) = FaceOffset(face);
                int neighborX = x + offsetX;
                int neighborY = y + offsetY;
                int neighborZ = z + offsetZ;
                BlockRenderDefinition? neighborDefinition = null;
                if(IsInside(neighborX, neighborY, neighborZ))
                {
                    neighborDefinition = definitions[ToIndex(neighborX, neighborY, neighborZ)];
                }
                else if(neighborQuery is not null)
                {
                    var worldNeighbor = WorldPosition(section.Coordinate, neighborX, neighborY, neighborZ);
                    if(neighborQuery.TryGetBlock(section.Coordinate.Dimension, worldNeighbor, out var neighborState))
                    {
                        neighborDefinition = ResolveVisibleDefinition(neighborState, definitionCache);
                    }
                }
                if(neighborDefinition is not null && Occludes(neighborDefinition, Opposite(face))) hidden |= ToMask(face);
            }
            return hidden;
        }

        BlockState InferHorizontalConnections(int localIndex, BlockState original)
        {
            string path = original.Name[(original.Name.IndexOf(':') + 1)..];
            bool isFence = path == "fence" || path.EndsWith("_fence", StringComparison.Ordinal);
            bool isWall = path.EndsWith("_wall", StringComparison.Ordinal);
            bool isPane = IsPaneOrBars(path);
            if(!isFence && !isWall && !isPane) return original;

            int x = localIndex & 15;
            int z = localIndex >> 4 & 15;
            int y = localIndex >> 8 & 15;
            var properties = new Dictionary<string, string>(original.Properties, StringComparer.Ordinal);
            foreach(var face in new[] { BlockFace.West, BlockFace.East, BlockFace.North, BlockFace.South })
            {
                var (offsetX, _, offsetZ) = FaceOffset(face);
                int neighborX = x + offsetX;
                int neighborZ = z + offsetZ;
                BlockState? neighborState = null;
                BlockRenderDefinition? neighborDefinition = null;
                if(IsInside(neighborX, y, neighborZ))
                {
                    int neighborIndex = ToIndex(neighborX, y, neighborZ);
                    neighborState = states[neighborIndex];
                    neighborDefinition = definitions[neighborIndex];
                }
                else if(neighborQuery is not null)
                {
                    var worldNeighbor = WorldPosition(section.Coordinate, neighborX, y, neighborZ);
                    if(neighborQuery.TryGetBlock(section.Coordinate.Dimension, worldNeighbor, out var externalState))
                    {
                        neighborState = externalState;
                        neighborDefinition = ResolveVisibleDefinition(externalState, definitionCache);
                    }
                }

                bool connects = neighborState is not null && neighborDefinition is not null &&
                                (Occludes(neighborDefinition, Opposite(face)) ||
                                 ConnectsToSameFamily(neighborState, isFence, isWall, isPane));
                properties[DirectionProperty(face)] = connects ? "true" : "false";
            }
            // The legacy registry cannot persist wall neighbor state. Keeping the post visible is a stable,
            // recognizable fallback even when a straight-wall post could be omitted by the live game.
            if(isWall) properties["up"] = "true";
            return new BlockState(original.Name, properties);
        }

        FaceCell?[] BuildFaceMask(BlockFace face, int slice)
        {
            var mask = new FaceCell?[SectionSize * SectionSize];
            for(var v = 0; v < SectionSize; v++)
            {
                for(var u = 0; u < SectionSize; u++)
                {
                    var (x, y, z) = LocalCoordinates(face, slice, u, v);
                    var localIndex = ToIndex(x, y, z);
                    var definition = definitions[localIndex];
                    if(definition.GeometryKind != BlockGeometryKind.FullCube) continue;

                    var (offsetX, offsetY, offsetZ) = FaceOffset(face);
                    var neighborX = x + offsetX;
                    var neighborY = y + offsetY;
                    var neighborZ = z + offsetZ;
                    BlockRenderDefinition? neighborDefinition = null;
                    BlockState? neighborState = null;
                    bool missingExternalNeighbor = false;
                    if(IsInside(neighborX, neighborY, neighborZ))
                    {
                        var neighborIndex = ToIndex(neighborX, neighborY, neighborZ);
                        neighborDefinition = definitions[neighborIndex];
                        neighborState = states[neighborIndex];
                    }
                    else if(neighborQuery is not null)
                    {
                        var worldNeighbor = WorldPosition(section.Coordinate, neighborX, neighborY, neighborZ);
                        if(neighborQuery.TryGetBlock(section.Coordinate.Dimension, worldNeighbor, out var externalNeighborState))
                        {
                            neighborDefinition = ResolveVisibleDefinition(externalNeighborState, definitionCache);
                            neighborState = externalNeighborState;
                        }
                        else
                        {
                            missingExternalNeighbor = true;
                        }
                    }

                    // Streaming world windows may not have loaded a neighboring Section yet, while sparse scene
                    // stores use the same absence to represent authoritative air. The caller owns that distinction.
                    if(missingExternalNeighbor && !missingSectionsAreAir && IsHorizontal(face)) continue;
                    if(neighborDefinition is not null &&
                       (Occludes(neighborDefinition, Opposite(face)) ||
                         neighborDefinition.GeometryKind == BlockGeometryKind.FullCube &&
                         Equals(neighborState, states[localIndex]) ||
                         ContinuesFluid(states[localIndex], neighborState))) continue;
                    mask[v * SectionSize + u] = new FaceCell(
                        states[localIndex],
                        definition.Layer,
                        BuildFaceLighting(face, x, y, z, states[localIndex]));
                }
            }

            return mask;
        }

        VoxelFaceLighting BuildModelLighting(int localIndex, BlockState state)
        {
            int x = localIndex & 15;
            int z = localIndex >> 4 & 15;
            int y = localIndex >> 8 & 15;
            VoxelLightSample brightest = SampleLight(x, y, z);
            foreach(BlockFace face in Enum.GetValues<BlockFace>())
            {
                var offset = FaceOffset(face);
                VoxelLightSample adjacent = SampleLight(x + offset.X, y + offset.Y, z + offset.Z);
                brightest = new VoxelLightSample(
                    Math.Max(brightest.SkyLight, adjacent.SkyLight),
                    Math.Max(brightest.BlockLight, adjacent.BlockLight));
            }
            return VoxelFaceLighting.Uniform(brightest, ResolveEmission(x, y, z, state));
        }

        VoxelFaceLighting BuildFaceLighting(BlockFace face, int x, int y, int z, BlockState state)
        {
            var outside = FaceOffset(face);
            VoxelLightSample light = SampleLight(x + outside.X, y + outside.Y, z + outside.Z);
            byte[] ao = BuildAmbientOcclusion(face, x, y, z);
            return new VoxelFaceLighting(
                light.SkyLight,
                light.BlockLight,
                ResolveEmission(x, y, z, state),
                ao[0],
                ao[1],
                ao[2],
                ao[3]);
        }

        byte[] BuildAmbientOcclusion(BlockFace face, int x, int y, int z)
        {
            var outside = FaceOffset(face);
            var (uAxis, vAxis, corners) = FaceAmbientOcclusionAxes(face);
            var result = new byte[4];
            for(int cornerIndex = 0; cornerIndex < corners.Length; cornerIndex++)
            {
                var corner = corners[cornerIndex];
                bool sideU = IsOccludingAt(
                    x + outside.X + uAxis.X * corner.U,
                    y + outside.Y + uAxis.Y * corner.U,
                    z + outside.Z + uAxis.Z * corner.U);
                bool sideV = IsOccludingAt(
                    x + outside.X + vAxis.X * corner.V,
                    y + outside.Y + vAxis.Y * corner.V,
                    z + outside.Z + vAxis.Z * corner.V);
                bool diagonal = IsOccludingAt(
                    x + outside.X + uAxis.X * corner.U + vAxis.X * corner.V,
                    y + outside.Y + uAxis.Y * corner.U + vAxis.Y * corner.V,
                    z + outside.Z + uAxis.Z * corner.U + vAxis.Z * corner.V);
                result[cornerIndex] = VoxelLightingMath.AmbientOcclusionLevel(sideU, sideV, diagonal);
            }
            return result;
        }

        bool IsOccludingAt(int x, int y, int z)
        {
            BlockRenderDefinition? definition = null;
            if(IsInside(x, y, z))
            {
                definition = definitions[ToIndex(x, y, z)];
            }
            else if(neighborQuery is not null)
            {
                BlockPosition position = WorldPosition(section.Coordinate, x, y, z);
                if(neighborQuery.TryGetBlock(section.Coordinate.Dimension, position, out BlockState state))
                {
                    definition = ResolveVisibleDefinition(state, definitionCache);
                }
            }
            return definition is not null &&
                   definition.GeometryKind == BlockGeometryKind.FullCube &&
                   definition.OccludingFaces != BlockFaceMask.None;
        }

        VoxelLightSample SampleLight(int x, int y, int z)
        {
            if(IsInside(x, y, z)) return VoxelLightingMath.Sample(section, ToIndex(x, y, z));
            if(neighborQuery is not null)
            {
                BlockPosition position = WorldPosition(section.Coordinate, x, y, z);
                if(neighborQuery.TryGetLight(section.Coordinate.Dimension, position, out VoxelLightSample external)) return external;
            }
            return VoxelLightSample.VisibleFallback;
        }

        byte ResolveEmission(int x, int y, int z, BlockState state)
        {
            byte known = VoxelLightingMath.EmissionLevel(state);
            VoxelLightSample source = SampleLight(x, y, z);
            byte brightestNeighbor = 0;
            foreach(BlockFace face in Enum.GetValues<BlockFace>())
            {
                var offset = FaceOffset(face);
                brightestNeighbor = Math.Max(brightestNeighbor, SampleLight(x + offset.X, y + offset.Y, z + offset.Z).BlockLight);
            }
            byte inferred = source.BlockLight > brightestNeighbor ? source.BlockLight : (byte)0;
            return Math.Max(known, inferred);
        }
    }

    private BlockRenderDefinition ResolveVisibleDefinition(
        BlockState state,
        Dictionary<string, BlockRenderDefinition> cache)
    {
        if(state.IsAir) return BlockRenderDefinition.Empty;
        if(cache.TryGetValue(state.CanonicalKey, out var cached)) return cached;

        var resolved = _renderResolver.Resolve(state);
        if(resolved is null || resolved.GeometryKind == BlockGeometryKind.Empty)
        {
            resolved = _unknownDefinition;
        }
        else if(resolved.GeometryKind is BlockGeometryKind.Model or BlockGeometryKind.UnknownPlaceholder &&
                string.IsNullOrWhiteSpace(resolved.ModelKey))
        {
            resolved = _unknownDefinition;
        }

        cache.Add(state.CanonicalKey, resolved);
        return resolved;
    }

    private static void MergeMask(
        BlockFace face,
        int slice,
        FaceCell?[] mask,
        Dictionary<BatchKey, List<GreedyQuad>> quadsByBatch,
        SectionCoordinate coordinate)
    {
        for(var v = 0; v < SectionSize; v++)
        {
            for(var u = 0; u < SectionSize;)
            {
                var cell = mask[v * SectionSize + u];
                if(cell is null)
                {
                    u++;
                    continue;
                }

                var width = 1;
                while(u + width < SectionSize && Equals(mask[v * SectionSize + u + width], cell)) width++;

                var height = 1;
                while(v + height < SectionSize && RowMatches(v + height, u, width, cell)) height++;

                for(var clearV = 0; clearV < height; clearV++)
                {
                    for(var clearU = 0; clearU < width; clearU++)
                    {
                        mask[(v + clearV) * SectionSize + u + clearU] = null;
                    }
                }

                var batchKey = new BatchKey(cell.Value.State, cell.Value.Layer);
                if(!quadsByBatch.TryGetValue(batchKey, out var quads))
                {
                    quads = [];
                    quadsByBatch.Add(batchKey, quads);
                }

                quads.Add(new GreedyQuad(face, QuadOrigin(coordinate, face, slice, u, v), width, height, cell.Value.Lighting));
                u += width;
            }
        }

        bool RowMatches(int row, int startU, int width, FaceCell? expected)
        {
            for(var offset = 0; offset < width; offset++)
            {
                if(!Equals(mask[row * SectionSize + startU + offset], expected)) return false;
            }

            return true;
        }
    }

    private static BlockPosition QuadOrigin(SectionCoordinate section, BlockFace face, int slice, int u, int v)
    {
        var sectionX = checked(section.X * SectionSize);
        var sectionY = checked(section.Y * SectionSize);
        var sectionZ = checked(section.Z * SectionSize);
        return face switch
        {
            BlockFace.West => new BlockPosition(sectionX + slice, sectionY + v, sectionZ + u),
            BlockFace.East => new BlockPosition(checked(sectionX + slice + 1), sectionY + v, sectionZ + u),
            BlockFace.Down => new BlockPosition(sectionX + u, sectionY + slice, sectionZ + v),
            BlockFace.Up => new BlockPosition(sectionX + u, checked(sectionY + slice + 1), sectionZ + v),
            BlockFace.North => new BlockPosition(sectionX + u, sectionY + v, sectionZ + slice),
            BlockFace.South => new BlockPosition(sectionX + u, sectionY + v, checked(sectionZ + slice + 1)),
            _ => throw new ArgumentOutOfRangeException(nameof(face)),
        };
    }

    private static BlockPosition WorldPosition(SectionCoordinate section, int localIndex)
    {
        var x = localIndex & 15;
        var z = localIndex >> 4 & 15;
        var y = localIndex >> 8 & 15;
        return WorldPosition(section, x, y, z);
    }

    private static BlockPosition WorldPosition(SectionCoordinate section, int x, int y, int z) => new(
        checked(section.X * SectionSize + x),
        checked(section.Y * SectionSize + y),
        checked(section.Z * SectionSize + z));

    private static (int X, int Y, int Z) LocalCoordinates(BlockFace face, int slice, int u, int v) => face switch
    {
        BlockFace.West or BlockFace.East => (slice, v, u),
        BlockFace.Down or BlockFace.Up => (u, slice, v),
        BlockFace.North or BlockFace.South => (u, v, slice),
        _ => throw new ArgumentOutOfRangeException(nameof(face)),
    };

    private static (int X, int Y, int Z) FaceOffset(BlockFace face) => face switch
    {
        BlockFace.West => (-1, 0, 0),
        BlockFace.East => (1, 0, 0),
        BlockFace.Down => (0, -1, 0),
        BlockFace.Up => (0, 1, 0),
        BlockFace.North => (0, 0, -1),
        BlockFace.South => (0, 0, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(face)),
    };

    private static ((int X, int Y, int Z) UAxis, (int X, int Y, int Z) VAxis, (int U, int V)[] Corners)
        FaceAmbientOcclusionAxes(BlockFace face) => face switch
    {
        BlockFace.West => ((0, 0, 1), (0, 1, 0), [(1, -1), (1, 1), (-1, 1), (-1, -1)]),
        BlockFace.East => ((0, 0, 1), (0, 1, 0), [(-1, -1), (-1, 1), (1, 1), (1, -1)]),
        BlockFace.Down => ((1, 0, 0), (0, 0, 1), [(-1, -1), (1, -1), (1, 1), (-1, 1)]),
        BlockFace.Up => ((1, 0, 0), (0, 0, 1), [(-1, 1), (1, 1), (1, -1), (-1, -1)]),
        BlockFace.North => ((1, 0, 0), (0, 1, 0), [(-1, -1), (-1, 1), (1, 1), (1, -1)]),
        BlockFace.South => ((1, 0, 0), (0, 1, 0), [(1, -1), (1, 1), (-1, 1), (-1, -1)]),
        _ => throw new ArgumentOutOfRangeException(nameof(face)),
    };

    private static BlockFace Opposite(BlockFace face) => face switch
    {
        BlockFace.West => BlockFace.East,
        BlockFace.East => BlockFace.West,
        BlockFace.Down => BlockFace.Up,
        BlockFace.Up => BlockFace.Down,
        BlockFace.North => BlockFace.South,
        BlockFace.South => BlockFace.North,
        _ => throw new ArgumentOutOfRangeException(nameof(face)),
    };

    private static bool IsHorizontal(BlockFace face) => face is
        BlockFace.West or BlockFace.East or BlockFace.North or BlockFace.South;

    private static string DirectionProperty(BlockFace face) => face switch
    {
        BlockFace.West => "west",
        BlockFace.East => "east",
        BlockFace.North => "north",
        BlockFace.South => "south",
        _ => throw new ArgumentOutOfRangeException(nameof(face)),
    };

    private static bool ConnectsToSameFamily(BlockState neighbor, bool isFence, bool isWall, bool isPane)
    {
        string path = neighbor.Name[(neighbor.Name.IndexOf(':') + 1)..];
        if(isFence)
        {
            return path == "fence" || path.EndsWith("_fence", StringComparison.Ordinal) ||
                   path == "fence_gate" || path.EndsWith("_fence_gate", StringComparison.Ordinal);
        }
        if(isWall)
        {
            return path.EndsWith("_wall", StringComparison.Ordinal) ||
                   path == "fence_gate" || path.EndsWith("_fence_gate", StringComparison.Ordinal);
        }
        return isPane && IsPaneConnectionTarget(path);
    }

    private static bool IsPaneOrBars(string path) => path == "iron_bars" || path.EndsWith("_pane", StringComparison.Ordinal);

    private static bool IsPaneConnectionTarget(string path) =>
        IsPaneOrBars(path) ||
        path == "glass" ||
        path == "stained_glass" ||
        path.EndsWith("_stained_glass", StringComparison.Ordinal);

    private static bool Occludes(BlockRenderDefinition definition, BlockFace face) =>
        (definition.OccludingFaces & ToMask(face)) != 0;

    private static bool ContinuesFluid(BlockState state, BlockState? neighbor)
    {
        if(neighbor is null) return false;
        return state.Name switch
        {
            "minecraft:water" or "minecraft:flowing_water" => ContainsWater(neighbor),
            "minecraft:lava" or "minecraft:flowing_lava" =>
                neighbor.Name is "minecraft:lava" or "minecraft:flowing_lava",
            _ => false,
        };
    }

    private static bool ContainsWater(BlockState state)
    {
        if(state.Properties.TryGetValue("waterlogged", out string? waterlogged) &&
           string.Equals(waterlogged, "true", StringComparison.OrdinalIgnoreCase)) return true;

        // Kelp and seagrass contain a water fluid state without serializing a waterlogged property.
        return state.Name is
            "minecraft:water" or
            "minecraft:flowing_water" or
            "minecraft:bubble_column" or
            "minecraft:kelp" or
            "minecraft:kelp_plant" or
            "minecraft:seagrass" or
            "minecraft:tall_seagrass";
    }

    private static BlockFaceMask ToMask(BlockFace face) => face switch
    {
        BlockFace.West => BlockFaceMask.West,
        BlockFace.East => BlockFaceMask.East,
        BlockFace.Down => BlockFaceMask.Down,
        BlockFace.Up => BlockFaceMask.Up,
        BlockFace.North => BlockFaceMask.North,
        BlockFace.South => BlockFaceMask.South,
        _ => throw new ArgumentOutOfRangeException(nameof(face)),
    };

    private static bool IsInside(int x, int y, int z) =>
        x is >= 0 and < SectionSize &&
        y is >= 0 and < SectionSize &&
        z is >= 0 and < SectionSize;

    private static int ToIndex(int x, int y, int z) => y << 8 | z << 4 | x;

    private readonly record struct FaceCell(BlockState State, BlockRenderLayer Layer, VoxelFaceLighting Lighting);

    private readonly record struct BatchKey(BlockState State, BlockRenderLayer Layer);
}
