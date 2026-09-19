using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using ZhuJieJing.Core;

namespace ZhuJieJing.Renderer.Meshing;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct VoxelVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector4 Color,
    Vector2 TextureCoordinate,
    Vector4 TextureRegion,
    float Shade = 1f,
    Vector4 Lighting = default,
    float MaterialFlags = 0f);

public readonly record struct SectionDrawRange(BlockRenderLayer Layer, int StartIndex, int IndexCount);

public sealed record SectionRenderGeometry(
    SectionCoordinate Coordinate,
    VoxelVertex[] Vertices,
    uint[] Indices,
    IReadOnlyList<SectionDrawRange>? DrawRanges = null)
{
    public int TriangleCount => Indices.Length / 3;


    public IReadOnlyDictionary<int, float> WaterSurfaceAreas { get; init; } = new Dictionary<int, float>();

    internal static IReadOnlyDictionary<int, float> CalculateWaterAreas(IReadOnlyList<VoxelVertex> vertices, IReadOnlyList<uint> indices)
    {
        var areas = new Dictionary<int, float>();
        for(int index = 0; index + 2 < indices.Count; index += 3)
        {
            VoxelVertex a = vertices[(int)indices[index]], b = vertices[(int)indices[index + 1]], c = vertices[(int)indices[index + 2]];
            if(a.MaterialFlags < 0.5f || a.Normal.Y < 0.75f) continue;
            int height = (int)MathF.Round((a.Position.Y + b.Position.Y + c.Position.Y) / 3f * 16f);
            float area = Vector3.Cross(b.Position - a.Position, c.Position - a.Position).Length() * 0.5f;
            areas[height] = areas.GetValueOrDefault(height) + area;
        }
        return areas;
    }

    public IReadOnlyList<SectionDrawRange> EffectiveDrawRanges =>
        DrawRanges ?? [new SectionDrawRange(BlockRenderLayer.Opaque, 0, Indices.Length)];
}

/// <summary>
/// Converts Section meshes into upload-ready geometry. Resource-pack element models are emitted when available;
/// unresolved model requests deliberately remain visible cubes.
/// </summary>
public static class SectionRenderGeometryBuilder
{
    private static readonly ConditionalWeakTable<ResolvedBlockModel, bool[]> EnclosedModelElements = new();
    public static SectionRenderGeometry Build(SectionMesh mesh, IBlockFaceMaterialResolver? materialResolver = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var quadCount = checked(mesh.QuadCount + mesh.ModelInstances.Count * 6);
        var vertices = new List<VoxelVertex>(checked(quadCount * 4));
        var indices = new List<uint>(checked(quadCount * 6));
        var ranges = new List<SectionDrawRange>();
        var cubeColors = new Dictionary<string, Vector4>(StringComparer.Ordinal);
        var placeholderColors = new Dictionary<(string Name, bool IsUnknown), Vector4>();

        foreach(var batch in mesh.QuadBatches)
        {
            if(!cubeColors.TryGetValue(batch.State.Name, out var color))
            {
                color = FallbackBlockColor.ForCanonicalName(batch.State.Name);
                cubeColors.Add(batch.State.Name, color);
            }
            int start = indices.Count;
            foreach(var quad in batch.Quads)
            {
                IReadOnlyList<BlockFaceMaterial> materials = materialResolver switch
                {
                    IBlockFaceLayerResolver layered => layered.ResolveFaceLayers(batch.State, quad.Face),
                    not null => [materialResolver.ResolveFace(batch.State, quad.Face)],
                    _ => [BlockFaceMaterial.Untextured(color)],
                };
                foreach(var material in materials) AddQuad(vertices, indices, batch.State, quad, material);
            }
            AppendRange(ranges, batch.Layer, start, indices.Count - start);
        }

        // Group model instances by layer so a Section normally needs at most one model range per pipeline,
        // rather than alternating GPU state for every stair, fence or plant in storage order.
        foreach(var layerGroup in mesh.ModelInstances.GroupBy(instance => instance.Layer))
        {
            int layerStart = indices.Count;
            foreach(var instance in layerGroup)
            {
                var key = (instance.State.Name, instance.IsUnknownPlaceholder);
                if(!placeholderColors.TryGetValue(key, out var color))
                {
                    color = FallbackBlockColor.ForModelPlaceholder(instance.State.Name, instance.IsUnknownPlaceholder);
                    placeholderColors.Add(key, color);
                }
                IReadOnlyList<ResolvedBlockModel> models = [];
                bool emittedModel = !instance.IsUnknownPlaceholder &&
                                    materialResolver is IBlockModelGeometryResolver modelResolver &&
                                    modelResolver.TryResolveModel(instance.State, out models) &&
                                    models.Count > 0;
                if(emittedModel)
                {
                    foreach(var model in models)
                    {
                        AddResolvedModel(
                            vertices,
                            indices,
                            instance.State,
                            instance.Position,
                            instance.HiddenFaces,
                            model,
                            instance.EffectiveLighting);
                    }
                }
                else
                {
                    AddPlaceholderCube(
                        vertices,
                        indices,
                        instance.Position,
                        instance.State,
                        color,
                        materialResolver,
                        instance.EffectiveLighting);
                }
            }
            AppendRange(ranges, layerGroup.Key, layerStart, indices.Count - layerStart);
        }

        return new SectionRenderGeometry(mesh.Coordinate, vertices.ToArray(), indices.ToArray(), ranges)
        {
            WaterSurfaceAreas = SectionRenderGeometry.CalculateWaterAreas(vertices, indices),
        };
    }

    private static void AddQuad(
        List<VoxelVertex> vertices,
        List<uint> indices,
        BlockState state,
        GreedyQuad quad,
        BlockFaceMaterial material)
    {
        var origin = new Vector3(quad.Origin.X, quad.Origin.Y, quad.Origin.Z);
        var u = quad.ULength;
        var v = quad.VLength;
        Vector3 normal;
        Vector3[] corners;

        switch(quad.Face)
        {
            case BlockFace.West:
                normal = -Vector3.UnitX;
                corners = [origin + new Vector3(0, 0, u), origin + new Vector3(0, v, u), origin + new Vector3(0, v, 0), origin];
                break;
            case BlockFace.East:
                normal = Vector3.UnitX;
                corners = [origin, origin + new Vector3(0, v, 0), origin + new Vector3(0, v, u), origin + new Vector3(0, 0, u)];
                break;
            case BlockFace.Down:
                normal = -Vector3.UnitY;
                corners = [origin, origin + new Vector3(u, 0, 0), origin + new Vector3(u, 0, v), origin + new Vector3(0, 0, v)];
                break;
            case BlockFace.Up:
                normal = Vector3.UnitY;
                corners = [origin + new Vector3(0, 0, v), origin + new Vector3(u, 0, v), origin + new Vector3(u, 0, 0), origin];
                break;
            case BlockFace.North:
                normal = -Vector3.UnitZ;
                corners = [origin, origin + new Vector3(0, v, 0), origin + new Vector3(u, v, 0), origin + new Vector3(u, 0, 0)];
                break;
            case BlockFace.South:
                normal = Vector3.UnitZ;
                corners = [origin + new Vector3(u, 0, 0), origin + new Vector3(u, v, 0), origin + new Vector3(0, v, 0), origin];
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(quad));
        }

        // Horizontal face corners advance ULength along world X and VLength along world Z. Their vertex order
        // is rotated relative to the vertical faces, so the texture spans must be swapped here as well.
        Vector2[] textureCoordinates = quad.Face is BlockFace.Up or BlockFace.Down
            ? new[] { new Vector2(0, u), Vector2.Zero, new Vector2(v, 0), new Vector2(v, u) }
            : new[] { new Vector2(0, v), Vector2.Zero, new Vector2(u, 0), new Vector2(u, v) };
        AddFace(
            vertices,
            indices,
            corners,
            normal,
            material,
            textureCoordinates,
            lighting: quad.EffectiveLighting,
            materialFlags: MaterialFlagsFor(state, material));
    }

    private static void AddPlaceholderCube(
        List<VoxelVertex> vertices,
        List<uint> indices,
        BlockPosition position,
        BlockState state,
        Vector4 color,
        IBlockFaceMaterialResolver? materialResolver,
        VoxelFaceLighting lighting)
    {
        var origin = new Vector3(position.X, position.Y, position.Z);
        Add(BlockFace.West, [origin + new Vector3(0, 0, 1), origin + new Vector3(0, 1, 1), origin + new Vector3(0, 1, 0), origin], -Vector3.UnitX);
        Add(BlockFace.East, [origin + new Vector3(1, 0, 0), origin + new Vector3(1, 1, 0), origin + new Vector3(1, 1, 1), origin + new Vector3(1, 0, 1)], Vector3.UnitX);
        Add(BlockFace.Down, [origin, origin + new Vector3(1, 0, 0), origin + new Vector3(1, 0, 1), origin + new Vector3(0, 0, 1)], -Vector3.UnitY);
        Add(BlockFace.Up, [origin + new Vector3(0, 1, 1), origin + new Vector3(1, 1, 1), origin + new Vector3(1, 1, 0), origin + new Vector3(0, 1, 0)], Vector3.UnitY);
        Add(BlockFace.North, [origin, origin + new Vector3(0, 1, 0), origin + new Vector3(1, 1, 0), origin + new Vector3(1, 0, 0)], -Vector3.UnitZ);
        Add(BlockFace.South, [origin + new Vector3(1, 0, 1), origin + new Vector3(1, 1, 1), origin + new Vector3(0, 1, 1), origin + new Vector3(0, 0, 1)], Vector3.UnitZ);

        void Add(BlockFace face, Vector3[] corners, Vector3 normal)
        {
            var resolved = materialResolver?.ResolveFace(state, face) ?? BlockFaceMaterial.Untextured(color);
            var tinted = resolved with { Tint = Multiply(resolved.Tint, color) };
            AddFace(
                vertices,
                indices,
                corners,
                normal,
                tinted,
                UnitUv(),
                lighting: lighting,
                materialFlags: MaterialFlagsFor(state, tinted));
        }
    }

    private static void AddResolvedModel(
        List<VoxelVertex> vertices,
        List<uint> indices,
        BlockState state,
        BlockPosition position,
        BlockFaceMask hiddenFaces,
        ResolvedBlockModel model,
        VoxelFaceLighting lighting)
    {
        var blockOrigin = new Vector3(position.X, position.Y, position.Z);
        bool[]? enclosedElements = hiddenFaces == BlockFaceMask.All
            ? EnclosedModelElements.GetValue(model, static value => value.Elements
                .Select(element => IsElementWithinBlock(element, value)).ToArray())
            : null;
        for(int elementIndex = 0; elementIndex < model.Elements.Count; elementIndex++)
        {
            // A model fully surrounded by opaque neighbors is invisible even when its resource
            // pack omitted cullface. Elements protruding outside the block remain conservative.
            if(enclosedElements?[elementIndex] == true) continue;
            var element = model.Elements[elementIndex];
            foreach(var face in element.Faces)
            {
                if(face.CullFace is BlockFace cullFace &&
                   (hiddenFaces & ToMask(RotateFace(cullFace, model.XRotation, model.YRotation))) != 0) continue;
                var (corners, normal) = ElementFace(element.From, element.To, face.Face);
                for(var index = 0; index < corners.Length; index++)
                {
                    corners[index] = TransformModelPoint(corners[index], element.Rotation, model.XRotation, model.YRotation);
                }
                if(IsCoveredBoundaryFace(corners, hiddenFaces)) continue;
                for(var index = 0; index < corners.Length; index++) corners[index] = corners[index] / 16f + blockOrigin;
                normal = TransformModelNormal(normal, element.Rotation, model.XRotation, model.YRotation);
                AddFace(
                    vertices,
                    indices,
                    corners,
                    normal,
                    face.Material,
                    FaceUv(face.TextureUv, face.TextureRotation),
                    element.Shade ? 1f : 0f,
                    lighting,
                    MaterialFlagsFor(state, face.Material));
            }
        }
    }

    private static bool IsElementWithinBlock(ResolvedModelElement element, ResolvedBlockModel model)
    {
        for(int index = 0; index < 8; index++)
        {
            Vector3 point = new((index & 1) == 0 ? element.From.X : element.To.X,
                (index & 2) == 0 ? element.From.Y : element.To.Y,
                (index & 4) == 0 ? element.From.Z : element.To.Z);
            point = TransformModelPoint(point, element.Rotation, model.XRotation, model.YRotation);
            if(!float.IsFinite(point.X + point.Y + point.Z) ||
               point.X < 0f || point.Y < 0f || point.Z < 0f || point.X > 16f || point.Y > 16f || point.Z > 16f) return false;
        }
        return true;
    }

    private static bool IsCoveredBoundaryFace(Vector3[] corners, BlockFaceMask hiddenFaces)
    {
        if(hiddenFaces == BlockFaceMask.None) return false;
        Vector3 minimum = corners[0], maximum = corners[0];
        foreach(Vector3 corner in corners)
        {
            minimum = Vector3.Min(minimum, corner);
            maximum = Vector3.Max(maximum, corner);
        }
        // Only exact block-boundary faces can be inferred without cullface metadata. Inset,
        // sloped and protruding faces keep their normal geometry unless fully enclosed above.
        if(minimum.X < 0f || minimum.Y < 0f || minimum.Z < 0f || maximum.X > 16f || maximum.Y > 16f || maximum.Z > 16f) return false;
        return (hiddenFaces.HasFlag(BlockFaceMask.West) && minimum.X == 0f && maximum.X == 0f) ||
               (hiddenFaces.HasFlag(BlockFaceMask.East) && minimum.X == 16f && maximum.X == 16f) ||
               (hiddenFaces.HasFlag(BlockFaceMask.Down) && minimum.Y == 0f && maximum.Y == 0f) ||
               (hiddenFaces.HasFlag(BlockFaceMask.Up) && minimum.Y == 16f && maximum.Y == 16f) ||
               (hiddenFaces.HasFlag(BlockFaceMask.North) && minimum.Z == 0f && maximum.Z == 0f) ||
               (hiddenFaces.HasFlag(BlockFaceMask.South) && minimum.Z == 16f && maximum.Z == 16f);
    }

    private static void AddFace(
        List<VoxelVertex> vertices,
        List<uint> indices,
        IReadOnlyList<Vector3> corners,
        Vector3 normal,
        BlockFaceMaterial material,
        IReadOnlyList<Vector2> textureCoordinates,
        float shade = 1f,
        VoxelFaceLighting? lighting = null,
        float materialFlags = 0f)
    {
        if(corners.Count != 4 || textureCoordinates.Count != 4) throw new ArgumentException("四边形必须有 4 个顶点。");
        var start = checked((uint)vertices.Count);
        var region = new Vector4(
            material.AtlasRegion.U,
            material.AtlasRegion.V,
            material.AtlasRegion.Width,
            material.AtlasRegion.Height);
        for(var index = 0; index < 4; index++)
        {
            Vector4 vertexLighting = (lighting ?? VoxelFaceLighting.FullBright).ToVertexData(index);
            vertices.Add(new VoxelVertex(corners[index], normal, material.Tint, textureCoordinates[index], region, shade, vertexLighting, materialFlags));
        }
        indices.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
    }

    private static float MaterialFlagsFor(BlockState state, BlockFaceMaterial material)
    {
        const float water = 1f;
        if(state.Name is "minecraft:water" or "minecraft:flowing_water") return water;
        return material.TextureKey?.Contains("water", StringComparison.OrdinalIgnoreCase) == true ? water : 0f;
    }

    private static (Vector3[] Corners, Vector3 Normal) ElementFace(Vector3 from, Vector3 to, BlockFace face) => face switch
    {
        BlockFace.West => ([new(from.X, from.Y, to.Z), new(from.X, to.Y, to.Z), new(from.X, to.Y, from.Z), new(from.X, from.Y, from.Z)], -Vector3.UnitX),
        BlockFace.East => ([new(to.X, from.Y, from.Z), new(to.X, to.Y, from.Z), new(to.X, to.Y, to.Z), new(to.X, from.Y, to.Z)], Vector3.UnitX),
        BlockFace.Down => ([new(from.X, from.Y, from.Z), new(to.X, from.Y, from.Z), new(to.X, from.Y, to.Z), new(from.X, from.Y, to.Z)], -Vector3.UnitY),
        BlockFace.Up => ([new(from.X, to.Y, to.Z), new(to.X, to.Y, to.Z), new(to.X, to.Y, from.Z), new(from.X, to.Y, from.Z)], Vector3.UnitY),
        BlockFace.North => ([new(from.X, from.Y, from.Z), new(from.X, to.Y, from.Z), new(to.X, to.Y, from.Z), new(to.X, from.Y, from.Z)], -Vector3.UnitZ),
        BlockFace.South => ([new(to.X, from.Y, to.Z), new(to.X, to.Y, to.Z), new(from.X, to.Y, to.Z), new(from.X, from.Y, to.Z)], Vector3.UnitZ),
        _ => throw new ArgumentOutOfRangeException(nameof(face)),
    };

    private static Vector3 TransformModelPoint(Vector3 point, ModelElementRotation? elementRotation, int xRotation, int yRotation)
    {
        if(elementRotation is not null)
        {
            var relative = point - elementRotation.Origin;
            float radians = MathF.PI / 180f * elementRotation.AngleDegrees;
            Matrix4x4 rotation = elementRotation.Axis switch
            {
                'x' => Matrix4x4.CreateRotationX(radians),
                'y' => Matrix4x4.CreateRotationY(radians),
                'z' => Matrix4x4.CreateRotationZ(radians),
                _ => Matrix4x4.Identity,
            };
            relative = Vector3.Transform(relative, rotation);
            if(elementRotation.Rescale)
            {
                float scale = 1f / MathF.Max(MathF.Abs(MathF.Cos(radians)), 0.01f);
                relative = elementRotation.Axis switch
                {
                    'x' => new Vector3(relative.X, relative.Y * scale, relative.Z * scale),
                    'y' => new Vector3(relative.X * scale, relative.Y, relative.Z * scale),
                    'z' => new Vector3(relative.X * scale, relative.Y * scale, relative.Z),
                    _ => relative,
                };
            }
            point = relative + elementRotation.Origin;
        }

        var centered = point - new Vector3(8f);
        if(xRotation != 0) centered = Vector3.Transform(centered, Matrix4x4.CreateRotationX(xRotation * MathF.PI / 180f));
        if(yRotation != 0) centered = Vector3.Transform(centered, Matrix4x4.CreateRotationY(-yRotation * MathF.PI / 180f));
        return centered + new Vector3(8f);
    }

    private static Vector3 TransformModelNormal(Vector3 normal, ModelElementRotation? elementRotation, int xRotation, int yRotation)
    {
        if(elementRotation is not null)
        {
            float radians = elementRotation.AngleDegrees * MathF.PI / 180f;
            normal = Vector3.TransformNormal(normal, elementRotation.Axis switch
            {
                'x' => Matrix4x4.CreateRotationX(radians),
                'y' => Matrix4x4.CreateRotationY(radians),
                'z' => Matrix4x4.CreateRotationZ(radians),
                _ => Matrix4x4.Identity,
            });
        }
        if(xRotation != 0) normal = Vector3.TransformNormal(normal, Matrix4x4.CreateRotationX(xRotation * MathF.PI / 180f));
        if(yRotation != 0) normal = Vector3.TransformNormal(normal, Matrix4x4.CreateRotationY(-yRotation * MathF.PI / 180f));
        return Vector3.Normalize(normal);
    }

    private static BlockFace RotateFace(BlockFace face, int xRotation, int yRotation)
    {
        Vector3 normal = face switch
        {
            BlockFace.West => -Vector3.UnitX,
            BlockFace.East => Vector3.UnitX,
            BlockFace.Down => -Vector3.UnitY,
            BlockFace.Up => Vector3.UnitY,
            BlockFace.North => -Vector3.UnitZ,
            BlockFace.South => Vector3.UnitZ,
            _ => throw new ArgumentOutOfRangeException(nameof(face)),
        };
        normal = TransformModelNormal(normal, null, xRotation, yRotation);
        if(MathF.Abs(normal.X) > 0.5f) return normal.X > 0 ? BlockFace.East : BlockFace.West;
        if(MathF.Abs(normal.Y) > 0.5f) return normal.Y > 0 ? BlockFace.Up : BlockFace.Down;
        return normal.Z > 0 ? BlockFace.South : BlockFace.North;
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

    private static Vector2[] FaceUv(Vector4 uv, int rotation)
    {
        Vector2[] result =
        [
            new(uv.X / 16f, uv.W / 16f),
            new(uv.X / 16f, uv.Y / 16f),
            new(uv.Z / 16f, uv.Y / 16f),
            new(uv.Z / 16f, uv.W / 16f),
        ];
        int turns = ((rotation % 360) + 360) % 360 / 90;
        if(turns == 0) return result;
        var rotated = new Vector2[4];
        for(var index = 0; index < 4; index++) rotated[(index + turns) % 4] = result[index];
        return rotated;
    }

    private static Vector2[] UnitUv() => [new(0, 1), Vector2.Zero, new(1, 0), Vector2.One];

    private static void AppendRange(List<SectionDrawRange> ranges, BlockRenderLayer layer, int start, int count)
    {
        if(count <= 0) return;
        if(ranges.Count > 0 && ranges[^1].Layer == layer && ranges[^1].StartIndex + ranges[^1].IndexCount == start)
        {
            ranges[^1] = ranges[^1] with { IndexCount = ranges[^1].IndexCount + count };
        }
        else
        {
            ranges.Add(new SectionDrawRange(layer, start, count));
        }
    }

    private static Vector4 Multiply(Vector4 left, Vector4 right) => new(
        left.X * right.X,
        left.Y * right.Y,
        left.Z * right.Z,
        left.W * right.W);
}

/// <summary>Stable temporary colors used before resource-pack blockstate and texture resolution is available.</summary>
public static class FallbackBlockColor
{
    internal static float AlphaForCanonicalName(string canonicalName)
    {
        string path = canonicalName[(canonicalName.IndexOf(':') + 1)..];
        if(path is "water" or "flowing_water") return 0.58f;
        if(path is "glass" or "glass_pane" or "stained_glass" or "stained_glass_pane" ||
           path.EndsWith("_stained_glass", StringComparison.Ordinal) || path.EndsWith("_stained_glass_pane", StringComparison.Ordinal)) return 0.28f;
        return 1f;
    }

    public static Vector4 ForCanonicalName(string canonicalName)
    {
        if(string.IsNullOrWhiteSpace(canonicalName)) throw new ArgumentException("规范方块名不能为空。", nameof(canonicalName));
        string path = canonicalName[(canonicalName.IndexOf(':') + 1)..];
        if(TryMinecraftFamilyColor(path, out Vector3 familyColor))
        {
            return new Vector4(ApplyStableVariation(familyColor, StableHash(canonicalName)), AlphaForCanonicalName(canonicalName));
        }

        var hash = StableHash(canonicalName);
        var hue = (hash & 0xffff) / 65535f;
        var saturation = 0.24f + ((hash >> 16) & 0xff) / 255f * 0.16f;
        var value = 0.66f + ((hash >> 24) & 0xff) / 255f * 0.14f;
        var rgb = HsvToRgb(hue, saturation, value);
        return new Vector4(rgb, AlphaForCanonicalName(canonicalName));
    }

    public static Vector4 ForModelPlaceholder(string canonicalName, bool isUnknown)
    {
        var stable = ForCanonicalName(canonicalName);
        var accent = isUnknown
            ? new Vector3(1f, 0.04f, 0.72f)
            : new Vector3(1f, 0.72f, 0.08f);
        var amount = isUnknown ? 0.64f : 0.42f;
        var rgb = Vector3.Lerp(new Vector3(stable.X, stable.Y, stable.Z), accent, amount);
        return new Vector4(rgb, 1f);
    }

    private static bool TryMinecraftFamilyColor(string path, out Vector3 color)
    {
        if(TryDyedBlockColor(path, out color)) return true;

        color = path switch
        {
            _ when path.Contains("water", StringComparison.Ordinal) => Rgb(63, 118, 228),
            _ when path.Contains("lava", StringComparison.Ordinal) || path.Contains("magma", StringComparison.Ordinal) => Rgb(236, 91, 24),
            _ when path.Contains("grass", StringComparison.Ordinal) => Rgb(108, 159, 69),
            _ when path.Contains("leaves", StringComparison.Ordinal) || path.Contains("vine", StringComparison.Ordinal) || path.Contains("fern", StringComparison.Ordinal) => Rgb(75, 128, 58),
            _ when path.Contains("flower", StringComparison.Ordinal) || path.Contains("sapling", StringComparison.Ordinal) || path.Contains("bush", StringComparison.Ordinal) => Rgb(83, 139, 65),
            _ when path.Contains("deepslate", StringComparison.Ordinal) => Rgb(78, 78, 82),
            _ when path.Contains("granite", StringComparison.Ordinal) => Rgb(149, 103, 85),
            _ when path.Contains("diorite", StringComparison.Ordinal) => Rgb(188, 188, 188),
            _ when path.Contains("andesite", StringComparison.Ordinal) => Rgb(132, 135, 135),
            _ when path.Contains("quartz", StringComparison.Ordinal) => Rgb(226, 221, 207),
            _ when path.Contains("red_sand", StringComparison.Ordinal) => Rgb(186, 99, 38),
            _ when path.Contains("sand", StringComparison.Ordinal) => Rgb(216, 202, 150),
            _ when path.Contains("nether_brick", StringComparison.Ordinal) => Rgb(68, 38, 42),
            _ when path.Contains("brick", StringComparison.Ordinal) => Rgb(151, 76, 62),
            _ when path.Contains("stone", StringComparison.Ordinal) || path.Contains("cobble", StringComparison.Ordinal) || path.Contains("bedrock", StringComparison.Ordinal) => Rgb(126, 126, 126),
            _ when path.Contains("dark_oak", StringComparison.Ordinal) => Rgb(65, 43, 20),
            _ when path.Contains("spruce", StringComparison.Ordinal) => Rgb(102, 77, 46),
            _ when path.Contains("birch", StringComparison.Ordinal) => Rgb(196, 179, 123),
            _ when path.Contains("acacia", StringComparison.Ordinal) => Rgb(168, 90, 50),
            _ when path.Contains("jungle", StringComparison.Ordinal) => Rgb(151, 109, 76),
            _ when path.Contains("oak", StringComparison.Ordinal) || path.Contains("wood", StringComparison.Ordinal) || path.Contains("log", StringComparison.Ordinal) || path.Contains("plank", StringComparison.Ordinal) => Rgb(142, 111, 64),
            _ when path.Contains("dirt", StringComparison.Ordinal) || path.Contains("farmland", StringComparison.Ordinal) => Rgb(134, 96, 67),
            _ when path.Contains("clay", StringComparison.Ordinal) => Rgb(159, 164, 177),
            _ when path.Contains("snow", StringComparison.Ordinal) => Rgb(239, 246, 246),
            _ when path.Contains("ice", StringComparison.Ordinal) => Rgb(145, 183, 250),
            _ when path.Contains("glass", StringComparison.Ordinal) => Rgb(181, 211, 214),
            _ when path.Contains("diamond", StringComparison.Ordinal) => Rgb(98, 219, 213),
            _ when path.Contains("emerald", StringComparison.Ordinal) => Rgb(58, 197, 92),
            _ when path.Contains("gold", StringComparison.Ordinal) => Rgb(249, 214, 70),
            _ when path.Contains("iron", StringComparison.Ordinal) => Rgb(213, 211, 204),
            _ when path.Contains("redstone", StringComparison.Ordinal) => Rgb(171, 27, 9),
            _ when path.Contains("obsidian", StringComparison.Ordinal) => Rgb(35, 24, 50),
            _ when path.Contains("netherrack", StringComparison.Ordinal) => Rgb(111, 54, 52),
            _ => default,
        };
        return color != default;
    }

    private static bool TryDyedBlockColor(string path, out Vector3 color)
    {
        ReadOnlySpan<(string Prefix, Vector3 Color)> colors =
        [
            ("light_gray_", Rgb(142, 142, 134)),
            ("light_blue_", Rgb(58, 175, 217)),
            ("magenta_", Rgb(199, 78, 189)),
            ("orange_", Rgb(224, 97, 1)),
            ("yellow_", Rgb(241, 176, 13)),
            ("lime_", Rgb(94, 168, 24)),
            ("pink_", Rgb(214, 101, 143)),
            ("gray_", Rgb(63, 68, 72)),
            ("cyan_", Rgb(21, 137, 145)),
            ("purple_", Rgb(126, 61, 181)),
            ("blue_", Rgb(44, 46, 143)),
            ("brown_", Rgb(96, 59, 31)),
            ("green_", Rgb(73, 91, 36)),
            ("red_", Rgb(151, 35, 35)),
            ("black_", Rgb(25, 25, 29)),
            ("white_", Rgb(207, 213, 214)),
        ];

        bool isDyedMaterial = path.Contains("wool", StringComparison.Ordinal) ||
                              path.Contains("concrete", StringComparison.Ordinal) ||
                              path.Contains("terracotta", StringComparison.Ordinal) ||
                              path.Contains("stained_glass", StringComparison.Ordinal) ||
                              path.Contains("glazed", StringComparison.Ordinal);
        if(isDyedMaterial)
        {
            foreach((string prefix, Vector3 candidate) in colors)
            {
                if(path.StartsWith(prefix, StringComparison.Ordinal))
                {
                    color = candidate;
                    return true;
                }
            }
        }

        color = default;
        return false;
    }

    private static Vector3 ApplyStableVariation(Vector3 color, uint hash)
    {
        float variation = ((hash >> 8) & 0xff) / 255f * 0.06f - 0.03f;
        return Vector3.Clamp(color + new Vector3(variation), Vector3.Zero, Vector3.One);
    }

    private static Vector3 Rgb(byte red, byte green, byte blue) => new(red / 255f, green / 255f, blue / 255f);

    private static uint StableHash(string value)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        foreach(var valueByte in Encoding.UTF8.GetBytes(value))
        {
            hash ^= valueByte;
            hash *= prime;
        }

        return hash;
    }

    private static Vector3 HsvToRgb(float hue, float saturation, float value)
    {
        var scaled = hue * 6f;
        var sector = (int)MathF.Floor(scaled) % 6;
        var fraction = scaled - MathF.Floor(scaled);
        var p = value * (1f - saturation);
        var q = value * (1f - fraction * saturation);
        var t = value * (1f - (1f - fraction) * saturation);
        return sector switch
        {
            0 => new Vector3(value, t, p),
            1 => new Vector3(q, value, p),
            2 => new Vector3(p, value, t),
            3 => new Vector3(p, q, value),
            4 => new Vector3(t, p, value),
            _ => new Vector3(value, p, q),
        };
    }
}
