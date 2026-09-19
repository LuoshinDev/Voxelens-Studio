using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZhuJieJing.Assets;
using ZhuJieJing.Core;
using ZhuJieJing.Renderer.Meshing;

namespace ZhuJieJing.Renderer.Minecraft;

public sealed record Minecraft1122ResourceConfiguration(
    string ClientJarPath,
    IReadOnlyList<MinecraftResourceOverlay>? Overlays = null,
    int AtlasTileSize = 32,
    int AtlasTilesPerRow = 64)
{
    public int? ResourcePackFormat { get; init; }
}

/// <summary>
/// Resolves Java 1.12.2 blockstates, inherited models and PNGs from the ordered local resource stack.
/// Resolution and PNG decoding are palette-driven and lazy rather than loading the whole client JAR.
/// </summary>
public sealed class Minecraft1122BlockRenderResources : IBlockRenderResolver, IBlockModelGeometryResolver, IBlockFaceLayerResolver, ISectionRenderBatchSource, IDisposable
{
    private const int LegacyResourcePackFormat = 3;
    private const int CanonicalResourceNamesPackFormat = 4;
    private const int ModernChestTextureLayoutPackFormat = 5;
    private const long MaximumPackMetadataBytes = 64 * 1024;
    private static readonly IReadOnlyDictionary<string, string> LegacyAliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["azure_bluet"] = "houstonia",
        ["bricks"] = "brick_block",
        ["carved_pumpkin"] = "pumpkin",
        ["chiseled_stone_bricks"] = "chiseled_stonebrick",
        ["cobblestone_stairs"] = "stone_stairs",
        ["cobweb"] = "web",
        ["cracked_stone_bricks"] = "cracked_stonebrick",
        ["cut_red_sandstone"] = "smooth_red_sandstone",
        ["cut_sandstone"] = "smooth_sandstone",
        ["end_stone_bricks"] = "end_bricks",
        ["grass_block"] = "grass",
        ["grass"] = "tall_grass",
        ["jack_o_lantern"] = "lit_pumpkin",
        ["lily_pad"] = "waterlily",
        ["magma_block"] = "magma",
        ["melon"] = "melon_block",
        ["mossy_stone_bricks"] = "mossy_stonebrick",
        ["nether_bricks"] = "nether_brick",
        ["nether_portal"] = "portal",
        ["nether_quartz_ore"] = "quartz_ore",
        ["note_block"] = "noteblock",
        ["oak_button"] = "wooden_button",
        ["oak_door"] = "wooden_door",
        ["oak_fence"] = "fence",
        ["oak_fence_gate"] = "fence_gate",
        ["oak_pressure_plate"] = "wooden_pressure_plate",
        ["oak_trapdoor"] = "trapdoor",
        ["polished_andesite"] = "smooth_andesite",
        ["polished_diorite"] = "smooth_diorite",
        ["polished_granite"] = "smooth_granite",
        ["powered_rail"] = "golden_rail",
        ["red_nether_bricks"] = "red_nether_brick",
        ["slime_block"] = "slime",
        ["snow_block"] = "snow",
        ["spawner"] = "mob_spawner",
        ["stone_bricks"] = "stonebrick",
        ["sugar_cane"] = "reeds",
        ["terracotta"] = "hardened_clay",
        ["wall_torch"] = "torch",
    };

    private static readonly IReadOnlyDictionary<string, string> InfestedAliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["infested_chiseled_stone_bricks"] = "chiseled_brick_monster_egg",
        ["infested_cobblestone"] = "cobblestone_monster_egg",
        ["infested_cracked_stone_bricks"] = "cracked_brick_monster_egg",
        ["infested_mossy_stone_bricks"] = "mossy_brick_monster_egg",
        ["infested_stone"] = "stone_monster_egg",
        ["infested_stone_bricks"] = "stone_brick_monster_egg",
    };

    private static readonly IReadOnlyDictionary<string, string> DoublePlantAliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["large_fern"] = "double_fern",
        ["lilac"] = "syringa",
        ["peony"] = "paeonia",
        ["rose_bush"] = "double_rose",
        ["sunflower"] = "sunflower",
        ["tall_grass"] = "double_grass",
    };

    private static readonly IReadOnlyDictionary<string, string> PottedContents = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["potted_acacia_sapling"] = "acacia_sapling",
        ["potted_allium"] = "allium",
        ["potted_azure_bluet"] = "houstonia",
        ["potted_birch_sapling"] = "birch_sapling",
        ["potted_blue_orchid"] = "blue_orchid",
        ["potted_brown_mushroom"] = "mushroom_brown",
        ["potted_cactus"] = "cactus",
        ["potted_dandelion"] = "dandelion",
        ["potted_dark_oak_sapling"] = "dark_oak_sapling",
        ["potted_dead_bush"] = "dead_bush",
        ["potted_fern"] = "fern",
        ["potted_jungle_sapling"] = "jungle_sapling",
        ["potted_oak_sapling"] = "oak_sapling",
        ["potted_orange_tulip"] = "orange_tulip",
        ["potted_oxeye_daisy"] = "oxeye_daisy",
        ["potted_pink_tulip"] = "pink_tulip",
        ["potted_poppy"] = "rose",
        ["potted_red_mushroom"] = "mushroom_red",
        ["potted_red_tulip"] = "red_tulip",
        ["potted_spruce_sapling"] = "spruce_sapling",
        ["potted_white_tulip"] = "white_tulip",
    };

    private static readonly Vector4 DefaultGrassTint = new(0.49f, 0.72f, 0.32f, 1f);
    private static readonly Vector4 DefaultFoliageTint = new(0.42f, 0.67f, 0.30f, 1f);
    private static readonly Vector4 DefaultLilyPadTint = Rgb(32, 128, 48);
    private static readonly IReadOnlyDictionary<string, Vector4> DyeColors = new Dictionary<string, Vector4>(StringComparer.Ordinal)
    {
        ["white"] = Rgb(249, 255, 254),
        ["orange"] = Rgb(249, 128, 29),
        ["magenta"] = Rgb(199, 78, 189),
        ["light_blue"] = Rgb(58, 179, 218),
        ["yellow"] = Rgb(254, 216, 61),
        ["lime"] = Rgb(128, 199, 31),
        ["pink"] = Rgb(243, 139, 170),
        ["gray"] = Rgb(71, 79, 82),
        ["light_gray"] = Rgb(157, 157, 151),
        ["cyan"] = Rgb(22, 156, 156),
        ["purple"] = Rgb(137, 50, 184),
        ["blue"] = Rgb(60, 68, 170),
        ["brown"] = Rgb(131, 84, 50),
        ["green"] = Rgb(94, 124, 22),
        ["red"] = Rgb(176, 46, 38),
        ["black"] = Rgb(29, 29, 33),
    };
    private readonly object _gate = new();
    private readonly MinecraftResourceStack _stack;
    private readonly bool _ownsStack;
    private readonly int _resourcePackFormat;
    private readonly IReadOnlyDictionary<int, int> _resourcePackFormatsByLayer;
    private readonly Lazy<string> _resourceRevision;
    private readonly Dictionary<string, ResolvedState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<ResourceLocation, ParsedModel> _models = [];
    private bool _disposed;

    private Minecraft1122BlockRenderResources(
        MinecraftResourceStack stack,
        bool ownsStack,
        int atlasTileSize,
        int atlasTilesPerRow,
        int resourcePackFormat,
        bool resourcePackFormatExplicit)
    {
        if(resourcePackFormat <= 0) throw new ArgumentOutOfRangeException(nameof(resourcePackFormat));
        _stack = stack;
        _ownsStack = ownsStack;
        _resourcePackFormat = resourcePackFormat;
        _resourcePackFormatsByLayer = stack.Layers.ToDictionary(
            layer => layer.LayerIndex,
            layer => resourcePackFormatExplicit
                ? resourcePackFormat
                : TryReadLayerPackFormat(layer) ?? resourcePackFormat);
        _resourceRevision = new Lazy<string>(ComputeResourceRevision, LazyThreadSafetyMode.ExecutionAndPublication);
        Atlas = new MinecraftTextureAtlas(atlasTileSize, atlasTilesPerRow);
    }

    public MinecraftTextureAtlas Atlas { get; }

    public IDisposable BeginSectionRenderBatch()
    {
        ThrowIfDisposed();
        return Atlas.BeginBatchUpdate();
    }

    public string ResourceRevision
    {
        get
        {
            ThrowIfDisposed();
            return _resourceRevision.Value;
        }
    }

    public int ResolvedStateCount
    {
        get
        {
            lock(_gate) return _states.Count;
        }
    }

    public static Minecraft1122BlockRenderResources Open(Minecraft1122ResourceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var stack = MinecraftResourceStack.Open(configuration.ClientJarPath, configuration.Overlays);
        try
        {
            int resourcePackFormat = configuration.ResourcePackFormat ??
                TryReadBaseClientPackFormat(stack) ??
                LegacyResourcePackFormat;
            return new Minecraft1122BlockRenderResources(
                stack,
                true,
                configuration.AtlasTileSize,
                configuration.AtlasTilesPerRow,
                resourcePackFormat,
                configuration.ResourcePackFormat.HasValue);
        }
        catch
        {
            stack.Dispose();
            throw;
        }
    }

    public static Minecraft1122BlockRenderResources FromStack(
        MinecraftResourceStack stack,
        int atlasTileSize = 32,
        int atlasTilesPerRow = 64,
        bool takeOwnership = false)
    {
        ArgumentNullException.ThrowIfNull(stack);
        int resourcePackFormat = TryReadBaseClientPackFormat(stack) ?? LegacyResourcePackFormat;
        return new Minecraft1122BlockRenderResources(
            stack,
            takeOwnership,
            atlasTileSize,
            atlasTilesPerRow,
            resourcePackFormat,
            false);
    }

    public static Minecraft1122BlockRenderResources FromStack(
        MinecraftResourceStack stack,
        int atlasTileSize,
        int atlasTilesPerRow,
        bool takeOwnership,
        int resourcePackFormat)
    {
        ArgumentNullException.ThrowIfNull(stack);
        return new Minecraft1122BlockRenderResources(
            stack,
            takeOwnership,
            atlasTileSize,
            atlasTilesPerRow,
            resourcePackFormat,
            true);
    }

    public BlockRenderDefinition Resolve(BlockState state) => ResolveState(state).Definition;

    public BlockFaceMaterial ResolveFace(BlockState state, BlockFace face)
    {
        var resolved = ResolveState(state);
        if(resolved.Definition.GeometryKind is BlockGeometryKind.Empty or BlockGeometryKind.Invisible)
        {
            return SolidMaterial(Vector4.Zero);
        }
        if(resolved.CubeFaceLayers.TryGetValue(face, out var materials) && materials.Count > 0) return materials[0];
        var modelMaterial = resolved.Models
            .SelectMany(model => model.Elements)
            .SelectMany(element => element.Faces)
            .FirstOrDefault(candidate => candidate.Face == face)?.Material;
        return modelMaterial ?? MissingMaterial(state, face);
    }

    public IReadOnlyList<BlockFaceMaterial> ResolveFaceLayers(BlockState state, BlockFace face)
    {
        var resolved = ResolveState(state);
        if(resolved.Definition.GeometryKind is BlockGeometryKind.Empty or BlockGeometryKind.Invisible) return [];
        if(resolved.CubeFaceLayers.TryGetValue(face, out var materials) && materials.Count > 0) return materials;
        var modelMaterials = resolved.Models
            .SelectMany(model => model.Elements)
            .SelectMany(element => element.Faces)
            .Where(candidate => candidate.Face == face)
            .Select(candidate => candidate.Material)
            .ToArray();
        return modelMaterials.Length > 0 ? modelMaterials : [MissingMaterial(state, face)];
    }

    public bool TryResolveModel(BlockState state, out IReadOnlyList<ResolvedBlockModel> models)
    {
        var resolved = ResolveState(state);
        models = resolved.Models;
        return models.Count > 0;
    }

    public void Dispose()
    {
        if(_disposed) return;
        _disposed = true;
        if(_ownsStack) _stack.Dispose();
    }

    private ResolvedState ResolveState(BlockState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ThrowIfDisposed();
        if(state.IsAir) return ResolvedState.Empty;
        lock(_gate)
        {
            if(_states.TryGetValue(state.CanonicalKey, out var cached)) return cached;
            var resolved = ResolveStateCore(state);
            _states.Add(state.CanonicalKey, resolved);
            return resolved;
        }
    }

    private ResolvedState ResolveStateCore(BlockState state)
    {
        if(TryResolveBuiltInSpecialState(state, out var special)) return special;

        if(string.Equals(state.Name, CanonicalFallbackBlockRenderResolver.VisibleUnknownLegacyBlock, StringComparison.Ordinal))
        {
            return MissingState(state, true);
        }

        if(TryResolveFluidState(state, out var fluid)) return fluid;

        foreach(var candidate in ResourceCandidates(state))
        {
            if(!TryReadBlockState(candidate.Location, out var document)) continue;
            using(document)
            {
                IReadOnlyList<ModelApplication> applications;
                try
                {
                    applications = SelectApplications(document.RootElement, candidate.Properties, candidate.Location.Namespace);
                }
                catch(InvalidDataException)
                {
                    continue;
                }
                if(applications.Count == 0) continue;

                var resolvedModels = new List<ResolvedBlockModel>(applications.Count);
                bool failed = false;
                foreach(var application in applications)
                {
                    if(!TryBuildResolvedModel(application, state, out var model))
                    {
                        failed = true;
                        break;
                    }
                    resolvedModels.Add(model!);
                }
                if(failed || resolvedModels.Count == 0) continue;

                var materials = resolvedModels
                    .SelectMany(model => model.Elements)
                    .SelectMany(element => element.Faces)
                    .Select(face => face.Material)
                    .ToArray();
                bool fullCube = IsGreedyFullCube(resolvedModels);
                var layer = ClassifyLayer(state, materials, fullCube);
                var cubeFaces = fullCube
                    ? SelectCubeFaceMaterials(resolvedModels[0], state)
                    : new Dictionary<BlockFace, IReadOnlyList<BlockFaceMaterial>>();
                var definition = fullCube
                    ? BlockRenderDefinition.FullCube(layer, layer == BlockRenderLayer.Opaque ? BlockFaceMask.All : BlockFaceMask.None)
                    : BlockRenderDefinition.Model(candidate.Location.ToString(), layer, BlockFaceMask.None);
                return new ResolvedState(definition, cubeFaces, resolvedModels, materials.Any(material => material.IsFallback));
            }
        }

        return MissingState(state, true);
    }

    private bool TryResolveBuiltInSpecialState(BlockState state, out ResolvedState result)
    {
        bool damagedWallSign = IsDamagedLegacyWallSign(state);
        if(damagedWallSign)
        {
            result = BuildSignState(state, SignModelKind.Wall, "oak");
            return true;
        }

        var (resourceNamespace, path) = SplitName(state.Name);
        if(resourceNamespace != "minecraft")
        {
            result = null!;
            return false;
        }

        if(path is "barrier" or "light")
        {
            result = ResolvedState.Invisible;
            return true;
        }

        if(path is "chest" or "trapped_chest" or "ender_chest")
        {
            result = BuildChestState(state, path);
            return true;
        }

        if(path is "skeleton_skull" or "skeleton_wall_skull" or "wither_skeleton_skull" or "wither_skeleton_wall_skull")
        {
            result = BuildSkeletonSkullState(state, path);
            return true;
        }

        if(TrySignKind(path, out SignModelKind signKind, out string woodType))
        {
            result = BuildSignState(state, signKind, woodType);
            return true;
        }

        if(path == "decorated_pot")
        {
            result = BuildDecoratedPotState(state);
            return true;
        }

        if(path == "conduit")
        {
            result = BuildConduitState(state);
            return true;
        }

        if(TryBannerKind(path, out bool wallMounted, out string color))
        {
            result = BuildBannerState(state, wallMounted, color);
            return true;
        }

        result = null!;
        return false;
    }

    private ResolvedState BuildSkeletonSkullState(BlockState state, string path)
    {
        // Heads use entity textures and have no ordinary blockstate/model JSON in 1.12.2.
        bool wallMounted = path.EndsWith("_wall_skull", StringComparison.Ordinal);
        string texture = path.StartsWith("wither_", StringComparison.Ordinal)
            ? "textures/entity/skeleton/wither_skeleton"
            : "textures/entity/skeleton/skeleton";
        var faces = LoadEntityCuboidMaterials(state, texture, 0, 0, 8, 8, 8,
            MissingMaterial(state, BlockFace.North), false, 32);
        // Rotation 0 faces south; wall facing describes the direction away from its support.
        float yaw = wallMounted ? FacingYaw(state, "north") : 180f + StandingRotationYaw(state);
        Vector3 from = wallMounted ? new(4f, 4f, 8f) : new(4f, 0f, 4f);
        Vector3 to = wallMounted ? new(12f, 12f, 16f) : new(12f, 8f, 12f);
        return ModelState($"zhujie:special/{path}", BlockRenderLayer.Cutout,
            [CreateEntityCuboid(from, to, faces, RotationAroundCenter(yaw))]);
    }

    private ResolvedState BuildChestState(BlockState state, string chestKind)
    {
        string entityTexture = chestKind switch
        {
            "trapped_chest" => "textures/entity/chest/trapped",
            "ender_chest" => "textures/entity/chest/ender",
            _ => "textures/entity/chest/normal",
        };
        var fallbackWood = LoadTextureOrSolid(
            state,
            BlockFace.North,
            chestKind == "ender_chest"
                ? new Vector4(0.18f, 0.13f, 0.24f, 1f)
                : new Vector4(0.63f, 0.40f, 0.20f, 1f),
            chestKind == "ender_chest" ? "minecraft:blocks/obsidian" : "minecraft:blocks/planks_oak",
            "minecraft:blocks/planks_oak");
        var fallbackLatch = LoadTextureOrSolid(
            state,
            BlockFace.North,
            new Vector4(0.86f, 0.72f, 0.25f, 1f),
            "minecraft:blocks/gold_block");
        bool modernTextureLayout = UsesModernChestTextureLayout(entityTexture);
        var lidFaces = LoadEntityCuboidMaterials(state, entityTexture, 0, 0, 14, 5, 14, fallbackWood, modernTextureLayout);
        var bodyFaces = LoadEntityCuboidMaterials(state, entityTexture, 0, 19, 14, 10, 14, fallbackWood, modernTextureLayout);
        var latchFaces = LoadEntityCuboidMaterials(state, entityTexture, 0, 0, 2, 4, 1, fallbackLatch, modernTextureLayout);
        var rotation = RotationAroundCenter(FacingYaw(state, "north"));
        ResolvedModelElement[] elements =
        [
            CreateEntityCuboid(new Vector3(1f, 0f, 1f), new Vector3(15f, 10f, 15f), bodyFaces, rotation),
            CreateEntityCuboid(new Vector3(1f, 9f, 1f), new Vector3(15f, 14f, 15f), lidFaces, rotation),
            CreateEntityCuboid(new Vector3(7f, 7f, 0f), new Vector3(9f, 11f, 1f), latchFaces, rotation),
        ];
        return ModelState($"zhujie:special/{chestKind}", BlockRenderLayer.Opaque, elements);
    }

    private IReadOnlyDictionary<BlockFace, BlockFaceMaterial> LoadEntityCuboidMaterials(
        BlockState state,
        string textureReference,
        int textureX,
        int textureY,
        int width,
        int height,
        int depth,
        BlockFaceMaterial fallback,
        bool modernTextureLayout, int logicalTextureHeight = 64)
    {
        int downTextureX = textureX + depth + (modernTextureLayout ? 0 : width);
        int upTextureX = textureX + depth + (modernTextureLayout ? width : 0);
        return new Dictionary<BlockFace, BlockFaceMaterial>
        {
            // Pack format 5 changed which horizontal panel represents the chest exterior. Bake that layout
            // difference, the entity-model Y/Z flip, and the north-facing base turn into world-space faces.
            [BlockFace.West] = LoadEntityTextureRegionOrFallback(state, BlockFace.West, textureReference, textureX + depth + width, textureY + depth, depth, height, fallback, logicalTextureHeight),
            [BlockFace.East] = LoadEntityTextureRegionOrFallback(state, BlockFace.East, textureReference, textureX, textureY + depth, depth, height, fallback, logicalTextureHeight),
            [BlockFace.Down] = LoadEntityTextureRegionOrFallback(state, BlockFace.Down, textureReference, downTextureX, textureY, width, depth, fallback, logicalTextureHeight),
            [BlockFace.Up] = LoadEntityTextureRegionOrFallback(state, BlockFace.Up, textureReference, upTextureX, textureY, width, depth, fallback, logicalTextureHeight),
            [BlockFace.North] = LoadEntityTextureRegionOrFallback(state, BlockFace.North, textureReference, textureX + depth, textureY + depth, width, height, fallback, logicalTextureHeight),
            [BlockFace.South] = LoadEntityTextureRegionOrFallback(state, BlockFace.South, textureReference, textureX + depth * 2 + width, textureY + depth, width, height, fallback, logicalTextureHeight),
        };
    }

    private bool UsesModernChestTextureLayout(string textureReference)
    {
        var key = MinecraftResourceKey.Parse($"assets/minecraft/{textureReference}.png");
        if(!_stack.TryGetSource(key, out MinecraftResourceOrigin? origin))
            return _resourcePackFormat >= ModernChestTextureLayoutPackFormat;
        int format = _resourcePackFormatsByLayer.GetValueOrDefault(origin!.LayerIndex, _resourcePackFormat);
        return format >= ModernChestTextureLayoutPackFormat;
    }

    private BlockFaceMaterial LoadEntityTextureRegionOrFallback(
        BlockState state,
        BlockFace face,
        string textureReference,
        int x,
        int y,
        int width,
        int height,
        BlockFaceMaterial fallback, int logicalTextureHeight = 64)
    {
        ResourceLocation texture;
        try
        {
            texture = ResourceLocation.Parse(textureReference, "minecraft", "");
        }
        catch(InvalidDataException)
        {
            return fallback;
        }
        string path = texture.Path.StartsWith("textures/", StringComparison.Ordinal)
            ? texture.Path
            : $"textures/{texture.Path}";
        MinecraftResourceKey key;
        try
        {
            key = MinecraftResourceKey.Parse($"assets/{texture.Namespace}/{path}.png");
        }
        catch(ArgumentException)
        {
            return fallback;
        }
        if(!_stack.Exists(key)) return fallback;

        var tile = Atlas.GetOrAddPngRegion(key.Value, () => _stack.OpenRead(key), x, y, width, height, 64, logicalTextureHeight);
        if(tile.IsFallback) return fallback;
        return new BlockFaceMaterial(
            tile.Region,
            ResolveTint(state, face, false),
            $"{key.Value}#region={x},{y},{width},{height}",
            false,
            tile.HasTransparentPixels,
            tile.HasTranslucentPixels);
    }

    private ResolvedState BuildSignState(BlockState state, SignModelKind kind, string woodType)
    {
        bool wallMounted = kind is SignModelKind.Wall or SignModelKind.WallHanging;
        bool hanging = kind is SignModelKind.Hanging or SignModelKind.WallHanging;
        string preferredTexture = hanging
            ? HangingSignWoodTexture(woodType)
            : $"minecraft:block/{woodType}_planks";
        var wood = LoadTextureOrSolid(
            state,
            BlockFace.North,
            SignWoodColor(woodType),
            preferredTexture,
            $"minecraft:block/{woodType}_planks",
            LegacyPlanksTexture(woodType));
        float yaw = wallMounted ? FacingYaw(state, "north") : StandingRotationYaw(state);
        var rotation = RotationAroundCenter(yaw);
        var elements = new List<ResolvedModelElement>();
        if(hanging)
        {
            elements.Add(CreateCuboid(new Vector3(1f, 3f, 7f), new Vector3(15f, 13f, 9f), wood, rotation));
            elements.Add(CreateCuboid(new Vector3(3f, 13f, 7.5f), new Vector3(5f, 16f, 8.5f), wood, rotation));
            elements.Add(CreateCuboid(new Vector3(11f, 13f, 7.5f), new Vector3(13f, 16f, 8.5f), wood, rotation));
        }
        else
        {
            elements.Add(wallMounted
                ? CreateCuboid(new Vector3(1f, 4f, 14.5f), new Vector3(15f, 13f, 15.5f), wood, rotation)
                : CreateCuboid(new Vector3(1f, 7f, 7f), new Vector3(15f, 15f, 9f), wood, rotation));
            if(!wallMounted)
            {
                elements.Add(CreateCuboid(new Vector3(7f, 0f, 7f), new Vector3(9f, 7f, 9f), wood, rotation));
            }
        }
        return ModelState(
            $"zhujie:special/{woodType}_{kind.ToString().ToLowerInvariant()}_sign",
            BlockRenderLayer.Opaque,
            elements);
    }

    private ResolvedState BuildDecoratedPotState(BlockState state)
    {
        BlockFaceMaterial side = LoadTextureOrSolid(
            state,
            BlockFace.North,
            Rgb(154, 94, 68),
            "minecraft:entity/decorated_pot/decorated_pot_side",
            "minecraft:block/decorated_pot_side",
            "minecraft:block/terracotta");
        BlockFaceMaterial baseMaterial = LoadTextureOrSolid(
            state,
            BlockFace.Up,
            Rgb(128, 75, 54),
            "minecraft:entity/decorated_pot/decorated_pot_base",
            "minecraft:block/terracotta");
        ResolvedModelElement[] elements =
        [
            CreateCuboid(new Vector3(2f, 1f, 2f), new Vector3(14f, 12f, 14f), side, null),
            CreateCuboid(new Vector3(5f, 12f, 5f), new Vector3(11f, 16f, 11f), side, null),
            CreateCuboid(new Vector3(4f, 0f, 4f), new Vector3(12f, 1f, 12f), baseMaterial, null),
        ];
        return ModelState("zhujie:special/decorated_pot", BlockRenderLayer.Opaque, elements);
    }

    private ResolvedState BuildConduitState(BlockState state)
    {
        BlockFaceMaterial shell = LoadTextureOrSolid(
            state,
            BlockFace.North,
            Rgb(83, 146, 150),
            "minecraft:block/conduit",
            "minecraft:entity/conduit/base");
        return ModelState(
            "zhujie:special/conduit",
            BlockRenderLayer.Opaque,
            [CreateCuboid(new Vector3(5f), new Vector3(11f), shell, null)]);
    }

    private ResolvedState BuildBannerState(BlockState state, bool wallMounted, string color)
    {
        Vector4 dye = DyeColors.TryGetValue(color, out var resolvedColor) ? resolvedColor : Vector4.One;
        string legacyColor = color == "light_gray" ? "silver" : color;
        BlockFaceMaterial cloth;
        if(TryLoadTextureMaterial("minecraft", $"minecraft:blocks/wool_colored_{legacyColor}", state, BlockFace.North) is { } exact)
        {
            cloth = exact;
        }
        else if(TryLoadTextureMaterial("minecraft", "minecraft:blocks/wool_colored_white", state, BlockFace.North) is { } white)
        {
            cloth = white with { Tint = dye };
        }
        else
        {
            cloth = SolidMaterial(dye);
        }
        var wood = LoadTextureOrSolid(
            state,
            BlockFace.North,
            new Vector4(0.48f, 0.31f, 0.16f, 1f),
            "minecraft:blocks/planks_oak");
        float yaw = wallMounted ? FacingYaw(state, "north") : StandingRotationYaw(state);
        var rotation = RotationAroundCenter(yaw);
        var elements = new List<ResolvedModelElement>
        {
            wallMounted
                ? CreateCuboid(new Vector3(2f, 3f, 14.75f), new Vector3(14f, 15f, 15.25f), cloth, rotation)
                : CreateCuboid(new Vector3(2f, 3f, 7.75f), new Vector3(14f, 15f, 8.25f), cloth, rotation),
        };
        if(wallMounted)
        {
            elements.Add(CreateCuboid(new Vector3(1.5f, 14.5f, 14f), new Vector3(14.5f, 15.5f, 16f), wood, rotation));
        }
        else
        {
            elements.Add(CreateCuboid(new Vector3(7.5f, 0f, 7.5f), new Vector3(8.5f, 16f, 8.5f), wood, rotation));
            elements.Add(CreateCuboid(new Vector3(1.5f, 14.5f, 7f), new Vector3(14.5f, 15.5f, 9f), wood, rotation));
        }
        return ModelState(
            wallMounted ? "zhujie:special/wall_banner" : "zhujie:special/standing_banner",
            BlockRenderLayer.Opaque,
            elements);
    }

    private ResolvedState ModelState(
        string modelKey,
        BlockRenderLayer layer,
        IReadOnlyList<ResolvedModelElement> elements)
    {
        var model = new ResolvedBlockModel(elements, 0, 0, false);
        bool fallback = elements.SelectMany(element => element.Faces).Any(face => face.Material.IsFallback);
        return new ResolvedState(
            BlockRenderDefinition.Model(modelKey, layer, BlockFaceMask.None),
            new Dictionary<BlockFace, IReadOnlyList<BlockFaceMaterial>>(),
            [model],
            fallback);
    }

    private BlockFaceMaterial LoadTextureOrSolid(
        BlockState state,
        BlockFace face,
        Vector4 solidColor,
        params string[] textureReferences)
    {
        foreach(string textureReference in textureReferences)
        {
            if(TryLoadTextureMaterial("minecraft", textureReference, state, face) is { } material) return material;
        }
        return SolidMaterial(solidColor);
    }

    private BlockFaceMaterial? TryLoadTextureMaterial(
        string defaultNamespace,
        string textureReference,
        BlockState state,
        BlockFace face)
    {
        var material = LoadTextureMaterial(defaultNamespace, textureReference, state, face, false);
        return material.IsFallback ? null : material;
    }

    private BlockFaceMaterial SolidMaterial(Vector4 color) => new(
        Atlas.WhiteTile.Region,
        color,
        null,
        false);

    private static ResolvedModelElement CreateCuboid(
        Vector3 from,
        Vector3 to,
        BlockFaceMaterial material,
        ModelElementRotation? rotation)
    {
        var faces = Enum.GetValues<BlockFace>()
            .Select(face => new ResolvedModelFace(face, DefaultFaceUv(face, from, to), null, 0, material))
            .ToArray();
        return new ResolvedModelElement(from, to, faces, rotation);
    }

    private static ResolvedModelElement CreateEntityCuboid(
        Vector3 from,
        Vector3 to,
        IReadOnlyDictionary<BlockFace, BlockFaceMaterial> materials,
        ModelElementRotation? rotation)
    {
        var faces = Enum.GetValues<BlockFace>()
            .Select(face => new ResolvedModelFace(
                face,
                face == BlockFace.Down
                    ? new Vector4(0f, 0f, 16f, 16f)
                    : new Vector4(16f, 0f, 0f, 16f),
                null,
                face == BlockFace.Down ? 90 : face == BlockFace.Up ? 270 : 0,
                materials[face]))
            .ToArray();
        return new ResolvedModelElement(from, to, faces, rotation);
    }

    private static ModelElementRotation? RotationAroundCenter(float yaw) => MathF.Abs(yaw) < 0.001f
        ? null
        : new ModelElementRotation(new Vector3(8f), 'y', yaw, false);

    private static float FacingYaw(BlockState state, string defaultFacing)
    {
        string facing = state.Properties.TryGetValue("facing", out var value) ? value : defaultFacing;
        return facing switch
        {
            "east" => -90f,
            "south" => 180f,
            "west" => 90f,
            _ => 0f,
        };
    }

    private static float StandingRotationYaw(BlockState state)
    {
        if(!state.Properties.TryGetValue("rotation", out string? value) ||
           !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rotation)) return 0f;
        return -(rotation & 15) * 22.5f;
    }

    private static bool IsDamagedLegacyWallSign(BlockState state) =>
        string.Equals(state.Name, CanonicalFallbackBlockRenderResolver.VisibleUnknownLegacyBlock, StringComparison.Ordinal) &&
        state.Properties.TryGetValue("legacy_id", out string? id) && id == "68";

    private static bool TrySignKind(string path, out SignModelKind kind, out string woodType)
    {
        if(path is "sign" or "standing_sign" or "wall_sign")
        {
            kind = path == "wall_sign" ? SignModelKind.Wall : SignModelKind.Standing;
            woodType = "oak";
            return true;
        }

        const string WallHangingSuffix = "_wall_hanging_sign";
        const string HangingSuffix = "_hanging_sign";
        const string WallSuffix = "_wall_sign";
        const string StandingSuffix = "_sign";
        if(path.EndsWith(WallHangingSuffix, StringComparison.Ordinal))
        {
            kind = SignModelKind.WallHanging;
            woodType = path[..^WallHangingSuffix.Length];
        }
        else if(path.EndsWith(HangingSuffix, StringComparison.Ordinal))
        {
            kind = SignModelKind.Hanging;
            woodType = path[..^HangingSuffix.Length];
        }
        else if(path.EndsWith(WallSuffix, StringComparison.Ordinal))
        {
            kind = SignModelKind.Wall;
            woodType = path[..^WallSuffix.Length];
        }
        else if(path.EndsWith(StandingSuffix, StringComparison.Ordinal))
        {
            kind = SignModelKind.Standing;
            woodType = path[..^StandingSuffix.Length];
        }
        else
        {
            kind = default;
            woodType = string.Empty;
            return false;
        }
        return woodType.Length > 0;
    }

    private static string LegacyPlanksTexture(string woodType) => woodType switch
    {
        "dark_oak" => "minecraft:blocks/planks_big_oak",
        "pale_oak" => "minecraft:blocks/planks_birch",
        "mangrove" or "cherry" => "minecraft:blocks/planks_acacia",
        "bamboo" => "minecraft:blocks/planks_oak",
        "crimson" or "warped" => "minecraft:blocks/planks_big_oak",
        _ => $"minecraft:blocks/planks_{woodType}",
    };

    private static string HangingSignWoodTexture(string woodType) => woodType switch
    {
        "bamboo" => "minecraft:block/stripped_bamboo_block",
        "crimson" => "minecraft:block/stripped_crimson_stem",
        "warped" => "minecraft:block/stripped_warped_stem",
        _ => $"minecraft:block/stripped_{woodType}_log",
    };

    private static Vector4 SignWoodColor(string woodType) => woodType switch
    {
        "spruce" or "dark_oak" => Rgb(92, 59, 32),
        "birch" or "pale_oak" => Rgb(196, 179, 123),
        "jungle" or "mangrove" => Rgb(151, 100, 72),
        "acacia" or "cherry" => Rgb(178, 104, 78),
        "crimson" => Rgb(101, 48, 70),
        "warped" => Rgb(45, 118, 111),
        "bamboo" => Rgb(193, 173, 77),
        _ => Rgb(176, 135, 79),
    };

    private static bool TryBannerKind(string path, out bool wallMounted, out string color)
    {
        const string WallSuffix = "_wall_banner";
        const string StandingSuffix = "_banner";
        if(path is "wall_banner" or "standing_banner" or "banner")
        {
            wallMounted = path == "wall_banner";
            color = "white";
            return true;
        }
        if(path.EndsWith(WallSuffix, StringComparison.Ordinal))
        {
            wallMounted = true;
            color = path[..^WallSuffix.Length];
            return color.Length > 0;
        }
        if(path.EndsWith(StandingSuffix, StringComparison.Ordinal))
        {
            wallMounted = false;
            color = path[..^StandingSuffix.Length];
            return color.Length > 0;
        }
        wallMounted = false;
        color = string.Empty;
        return false;
    }

    private static Vector4 Rgb(byte red, byte green, byte blue) => new(red / 255f, green / 255f, blue / 255f, 1f);

    private bool TryResolveFluidState(BlockState state, out ResolvedState result)
    {
        var (resourceNamespace, path) = SplitName(state.Name);
        if(resourceNamespace != "minecraft" || path is not ("water" or "lava"))
        {
            result = null!;
            return false;
        }

        var faces = new Dictionary<BlockFace, IReadOnlyList<BlockFaceMaterial>>();
        foreach(var face in Enum.GetValues<BlockFace>())
        {
            faces[face] = path == "water"
                ? [LoadTextureOrSolid(
                    state,
                    face,
                    new Vector4(0.30f, 0.52f, 0.93f, 0.82f),
                    "minecraft:block/water_still",
                    "minecraft:blocks/water_still")]
                : [LoadTextureOrSolid(
                    state,
                    face,
                    new Vector4(1f, 0.43f, 0.08f, 1f),
                    "minecraft:block/lava_still",
                    "minecraft:blocks/lava_still")];
        }
        var layer = path == "water" ? BlockRenderLayer.Translucent : BlockRenderLayer.Opaque;
        result = new ResolvedState(
            BlockRenderDefinition.FullCube(layer, layer == BlockRenderLayer.Opaque ? BlockFaceMask.All : BlockFaceMask.None),
            faces,
            [],
            faces.Values.SelectMany(value => value).Any(material => material.IsFallback));
        return true;
    }

    private bool TryBuildResolvedModel(ModelApplication application, BlockState state, out ResolvedBlockModel? result)
    {
        if(!TryLoadModel(application.Model, new HashSet<ResourceLocation>(), out var parsed))
        {
            result = null;
            return false;
        }

        var elements = new List<ResolvedModelElement>(parsed.Elements.Count);
        foreach(var element in parsed.Elements)
        {
            var faces = new List<ResolvedModelFace>(element.Faces.Count);
            foreach(var face in element.Faces)
            {
                string? textureReference = ResolveTextureReference(face.Value.Texture, parsed.Textures);
                var material = textureReference is null
                    ? MissingMaterial(state, face.Key)
                    : LoadTextureMaterial(application.Model.Namespace, textureReference, state, face.Key, face.Value.TintIndex >= 0);
                faces.Add(new ResolvedModelFace(
                    face.Key,
                    face.Value.Uv,
                    face.Value.CullFace,
                    face.Value.Rotation,
                    material));
            }
            elements.Add(new ResolvedModelElement(element.From, element.To, faces, element.Rotation, element.Shade));
        }

        result = new ResolvedBlockModel(elements, application.XRotation, application.YRotation, application.UvLock);
        return elements.Count > 0;
    }

    private BlockFaceMaterial LoadTextureMaterial(
        string defaultNamespace,
        string textureReference,
        BlockState state,
        BlockFace face,
        bool tintIndex)
    {
        ResourceLocation texture;
        try
        {
            texture = ResourceLocation.Parse(textureReference, defaultNamespace, "");
        }
        catch(InvalidDataException)
        {
            return MissingMaterial(state, face);
        }
        string path = texture.Path.StartsWith("textures/", StringComparison.Ordinal)
            ? texture.Path
            : $"textures/{texture.Path}";
        MinecraftResourceKey key;
        try
        {
            key = MinecraftResourceKey.Parse($"assets/{texture.Namespace}/{path}.png");
        }
        catch(ArgumentException)
        {
            return MissingMaterial(state, face);
        }
        if(!_stack.Exists(key)) return MissingMaterial(state, face);

        MinecraftResourceKey animationKey = MinecraftResourceKey.Parse(key.Value + ".mcmeta");
        var tile = Atlas.GetOrAddPng(key.Value, () => _stack.OpenRead(key), _stack.Exists(animationKey) ? () => _stack.OpenRead(animationKey) : null);
        var tint = ResolveTint(state, face, tintIndex);
        return new BlockFaceMaterial(
            tile.Region,
            tint,
            key.Value,
            tile.IsFallback,
            tile.HasTransparentPixels,
            tile.HasTranslucentPixels);
    }

    private BlockFaceMaterial MissingMaterial(BlockState state, BlockFace face) => new(
        Atlas.MissingTile.Region,
        ResolveTint(state, face, false),
        null,
        true);

    private static Vector4 ResolveTint(BlockState state, BlockFace face, bool tintIndex)
    {
        string path = SplitName(state.Name).Path;
        if(path is "water" or "flowing_water") return new Vector4(0.30f, 0.52f, 0.93f, 0.82f);
        if(path.Contains("leaves", StringComparison.Ordinal) || path is "vine") return DefaultFoliageTint;
        if(tintIndex && path is "lily_pad" or "waterlily") return DefaultLilyPadTint;
        if(path == "grass_block" && (tintIndex || face == BlockFace.Up)) return DefaultGrassTint;
        if(tintIndex && IsGrassTintedPlant(path)) return DefaultGrassTint;
        return Vector4.One;
    }

    private static bool IsGrassTintedPlant(string path) =>
        path is "grass" or "tall_grass" or "fern" or "large_fern" ||
        path.Contains("sapling", StringComparison.Ordinal);

    private static BlockRenderLayer ClassifyLayer(BlockState state, IReadOnlyList<BlockFaceMaterial> materials, bool fullCube)
    {
        string path = SplitName(state.Name).Path;
        if(path is "water" or "flowing_water" ||
           path.Contains("glass", StringComparison.Ordinal) ||
           path.Contains("ice", StringComparison.Ordinal) ||
           path is "slime_block" or "portal" or "nether_portal")
        {
            return BlockRenderLayer.Translucent;
        }
        if(fullCube && path == "grass_block") return BlockRenderLayer.Opaque;
        if(materials.Any(material => material.HasTranslucentPixels)) return BlockRenderLayer.Translucent;
        if(materials.Any(material => material.HasTransparentPixels)) return BlockRenderLayer.Cutout;
        if(path.Contains("leaves", StringComparison.Ordinal) || IsKnownCutout(path)) return BlockRenderLayer.Cutout;
        return BlockRenderLayer.Opaque;
    }

    private static bool IsKnownCutout(string path) =>
        path.Contains("sapling", StringComparison.Ordinal) ||
        path.Contains("flower", StringComparison.Ordinal) ||
        path.Contains("mushroom", StringComparison.Ordinal) ||
        path.Contains("grass", StringComparison.Ordinal) && path != "grass_block" ||
        path.Contains("fern", StringComparison.Ordinal) ||
        path.Contains("pane", StringComparison.Ordinal) ||
        path.Contains("door", StringComparison.Ordinal) ||
        path.Contains("trapdoor", StringComparison.Ordinal) ||
        path is "vine" or "ladder" or "cobweb" or "web" or "reeds" or "sugar_cane" or "chain";

    private static bool IsGreedyFullCube(IReadOnlyList<ResolvedBlockModel> models)
    {
        if(models.Count != 1 || models[0].XRotation % 360 != 0 || models[0].YRotation % 360 != 0) return false;
        var elements = models[0].Elements;
        if(elements.Count == 0 || elements.Any(element => element.Rotation is not null || !IsFullExtent(element))) return false;
        return Enum.GetValues<BlockFace>().All(face => elements.Any(element => element.Faces.Any(candidate => candidate.Face == face)));
    }

    private static bool IsFullExtent(ResolvedModelElement element) =>
        element.From == Vector3.Zero && element.To == new Vector3(16f);

    private static Dictionary<BlockFace, IReadOnlyList<BlockFaceMaterial>> SelectCubeFaceMaterials(ResolvedBlockModel model, BlockState state)
    {
        var accumulated = Enum.GetValues<BlockFace>().ToDictionary(face => face, _ => new List<BlockFaceMaterial>());
        foreach(var element in model.Elements)
        {
            foreach(var face in element.Faces)
            {
                accumulated[face.Face].Add(face.Material);
            }
        }
        var result = new Dictionary<BlockFace, IReadOnlyList<BlockFaceMaterial>>();
        foreach(var face in Enum.GetValues<BlockFace>())
        {
            result[face] = accumulated[face].Count > 0
                ? accumulated[face].ToArray()
                : [BlockFaceMaterial.Untextured(FallbackBlockColor.ForCanonicalName(state.Name))];
        }
        return result;
    }

    private ResolvedState MissingState(BlockState state, bool unknown)
    {
        var materials = Enum.GetValues<BlockFace>().ToDictionary<BlockFace, BlockFace, IReadOnlyList<BlockFaceMaterial>>(
            face => face,
            face => [MissingMaterial(state, face)]);
        return new ResolvedState(
            unknown ? BlockRenderDefinition.Unknown() : BlockRenderDefinition.Model("zhujie:unsupported_model"),
            materials,
            [],
            true);
    }

    private bool TryReadBlockState(ResourceLocation location, out JsonDocument document)
    {
        return TryReadJson($"assets/{location.Namespace}/blockstates/{location.Path}.json", out document);
    }

    private bool TryReadJson(string value, out JsonDocument document)
    {
        document = null!;
        MinecraftResourceKey key;
        try
        {
            key = MinecraftResourceKey.Parse(value);
        }
        catch(Exception exception) when(exception is ArgumentException or FormatException)
        {
            return false;
        }
        if(!_stack.Exists(key)) return false;
        try
        {
            using var stream = _stack.OpenRead(key);
            document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            return true;
        }
        catch(JsonException)
        {
            return false;
        }
    }

    private bool TryLoadModel(ResourceLocation location, HashSet<ResourceLocation> visiting, out ParsedModel result)
    {
        if(_models.TryGetValue(location, out result!)) return true;
        if(!visiting.Add(location))
        {
            result = null!;
            return false;
        }
        try
        {
            if(!TryReadJson($"assets/{location.Namespace}/models/{location.Path}.json", out var document))
            {
                result = null!;
                return false;
            }
            using(document)
            {
                var root = document.RootElement;
                ParsedModel? parent = null;
                if(root.TryGetProperty("parent", out var parentNode) && parentNode.ValueKind == JsonValueKind.String)
                {
                    var parentLocation = ResourceLocation.Parse(parentNode.GetString()!, location.Namespace, "block/");
                    if(!TryLoadModel(parentLocation, visiting, out parent))
                    {
                        result = null!;
                        return false;
                    }
                }

                var textures = parent is null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(parent.Textures, StringComparer.Ordinal);
                if(root.TryGetProperty("textures", out var texturesNode) && texturesNode.ValueKind == JsonValueKind.Object)
                {
                    foreach(var property in texturesNode.EnumerateObject())
                    {
                        if(property.Value.ValueKind == JsonValueKind.String) textures[property.Name] = property.Value.GetString()!;
                    }
                }

                IReadOnlyList<ParsedElement> elements = parent?.Elements ?? [];
                if(root.TryGetProperty("elements", out var elementsNode)) elements = ParseElements(elementsNode);
                result = new ParsedModel(textures, elements);
                _models.Add(location, result);
                return true;
            }
        }
        catch(InvalidDataException)
        {
            result = null!;
            return false;
        }
        finally
        {
            visiting.Remove(location);
        }
    }

    private static IReadOnlyList<ParsedElement> ParseElements(JsonElement node)
    {
        if(node.ValueKind != JsonValueKind.Array) throw new InvalidDataException("模型 elements 必须是数组。");
        var result = new List<ParsedElement>();
        foreach(var elementNode in node.EnumerateArray())
        {
            if(elementNode.ValueKind != JsonValueKind.Object) throw new InvalidDataException("模型 element 必须是对象。");
            Vector3 from = ReadVector3(elementNode, "from");
            Vector3 to = ReadVector3(elementNode, "to");
            bool shade = !elementNode.TryGetProperty("shade", out var shadeNode) || shadeNode.ValueKind != JsonValueKind.False;
            var rotation = ParseElementRotation(elementNode);
            if(!elementNode.TryGetProperty("faces", out var facesNode) || facesNode.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("模型 element 缺少 faces。");
            }

            var faces = new Dictionary<BlockFace, ParsedFace>();
            foreach(var faceProperty in facesNode.EnumerateObject())
            {
                if(!TryParseFace(faceProperty.Name, out var face) || faceProperty.Value.ValueKind != JsonValueKind.Object) continue;
                var faceNode = faceProperty.Value;
                if(!faceNode.TryGetProperty("texture", out var textureNode) || textureNode.ValueKind != JsonValueKind.String) continue;
                var uv = faceNode.TryGetProperty("uv", out var uvNode)
                    ? ReadVector4(uvNode)
                    : DefaultFaceUv(face, from, to);
                BlockFace? cullFace = null;
                if(faceNode.TryGetProperty("cullface", out var cullNode) && cullNode.ValueKind == JsonValueKind.String &&
                   TryParseFace(cullNode.GetString()!, out var parsedCull)) cullFace = parsedCull;
                int textureRotation = faceNode.TryGetProperty("rotation", out var rotationNode) && rotationNode.TryGetInt32(out var parsedRotation)
                    ? NormalizeRightAngle(parsedRotation)
                    : 0;
                int tintIndex = faceNode.TryGetProperty("tintindex", out var tintNode) && tintNode.TryGetInt32(out var parsedTint)
                    ? parsedTint
                    : -1;
                faces[face] = new ParsedFace(textureNode.GetString()!, uv, cullFace, textureRotation, tintIndex);
            }
            if(faces.Count > 0) result.Add(new ParsedElement(from, to, faces, rotation, shade));
        }
        return result;
    }

    private static ModelElementRotation? ParseElementRotation(JsonElement elementNode)
    {
        if(!elementNode.TryGetProperty("rotation", out var rotationNode) || rotationNode.ValueKind != JsonValueKind.Object) return null;
        Vector3 origin = ReadVector3(rotationNode, "origin");
        if(!rotationNode.TryGetProperty("axis", out var axisNode) || axisNode.ValueKind != JsonValueKind.String) return null;
        string axisText = axisNode.GetString()!;
        if(axisText.Length != 1 || axisText[0] is not ('x' or 'y' or 'z')) throw new InvalidDataException("模型 element rotation axis 无效。");
        if(!rotationNode.TryGetProperty("angle", out var angleNode) || !angleNode.TryGetSingle(out float angle) || !float.IsFinite(angle))
        {
            throw new InvalidDataException("模型 element rotation angle 无效。");
        }
        bool rescale = rotationNode.TryGetProperty("rescale", out var rescaleNode) && rescaleNode.ValueKind == JsonValueKind.True;
        return new ModelElementRotation(origin, axisText[0], angle, rescale);
    }

    private static IReadOnlyList<ModelApplication> SelectApplications(
        JsonElement root,
        IReadOnlyDictionary<string, string> properties,
        string defaultNamespace)
    {
        var applications = new List<ModelApplication>();
        if(root.TryGetProperty("variants", out var variantsNode) && variantsNode.ValueKind == JsonValueKind.Object)
        {
            JsonProperty? best = null;
            int bestScore = -1;
            JsonProperty? normal = null;
            foreach(var variant in variantsNode.EnumerateObject())
            {
                if(variant.Name is "" or "normal") normal ??= variant;
                if(!VariantMatches(variant.Name, properties, out int score)) continue;
                if(score > bestScore)
                {
                    best = variant;
                    bestScore = score;
                }
            }
            JsonProperty? selected = best ?? normal;
            if(selected is null && properties.Count == 0)
            {
                JsonProperty[] unmatched = variantsNode.EnumerateObject().Take(2).ToArray();
                if(unmatched.Length == 1) selected = unmatched[0];
            }
            if(selected is JsonProperty selectedProperty && TryParseApplication(selectedProperty.Value, defaultNamespace, out var application))
            {
                applications.Add(application);
            }
            return applications;
        }

        if(root.TryGetProperty("multipart", out var multipartNode) && multipartNode.ValueKind == JsonValueKind.Array)
        {
            foreach(var part in multipartNode.EnumerateArray())
            {
                if(part.ValueKind != JsonValueKind.Object) continue;
                if(part.TryGetProperty("when", out var whenNode) && !WhenMatches(whenNode, properties)) continue;
                if(part.TryGetProperty("apply", out var applyNode) && TryParseApplication(applyNode, defaultNamespace, out var application))
                {
                    applications.Add(application);
                }
            }
        }
        return applications;
    }

    private static bool TryParseApplication(JsonElement node, string defaultNamespace, out ModelApplication application)
    {
        if(node.ValueKind == JsonValueKind.Array)
        {
            var enumerator = node.EnumerateArray();
            if(!enumerator.MoveNext())
            {
                application = default;
                return false;
            }
            node = enumerator.Current;
        }
        if(node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("model", out var modelNode) || modelNode.ValueKind != JsonValueKind.String)
        {
            application = default;
            return false;
        }
        var model = ResourceLocation.Parse(modelNode.GetString()!, defaultNamespace, "block/");
        int x = node.TryGetProperty("x", out var xNode) && xNode.TryGetInt32(out var parsedX) ? NormalizeRightAngle(parsedX) : 0;
        int y = node.TryGetProperty("y", out var yNode) && yNode.TryGetInt32(out var parsedY) ? NormalizeRightAngle(parsedY) : 0;
        bool uvLock = node.TryGetProperty("uvlock", out var uvLockNode) && uvLockNode.ValueKind == JsonValueKind.True;
        application = new ModelApplication(model, x, y, uvLock);
        return true;
    }

    private static bool VariantMatches(string key, IReadOnlyDictionary<string, string> properties, out int score)
    {
        score = 0;
        if(key is "" or "normal") return properties.Count == 0;
        if(!key.Contains('='))
        {
            if(properties.TryGetValue(key, out var enabled) && string.Equals(enabled, "true", StringComparison.Ordinal))
            {
                score = 1;
                return true;
            }
            return false;
        }
        foreach(string pair in key.Split(','))
        {
            int separator = pair.IndexOf('=');
            if(separator <= 0 || separator == pair.Length - 1) return false;
            string name = pair[..separator];
            string expected = pair[(separator + 1)..];
            if(!properties.TryGetValue(name, out var actual) || !expected.Split('|').Contains(actual, StringComparer.Ordinal)) return false;
            score++;
        }
        return true;
    }

    private static bool WhenMatches(JsonElement node, IReadOnlyDictionary<string, string> properties)
    {
        if(node.ValueKind != JsonValueKind.Object) return false;
        if(node.TryGetProperty("OR", out var orNode) && orNode.ValueKind == JsonValueKind.Array)
        {
            return orNode.EnumerateArray().Any(candidate => WhenMatches(candidate, properties));
        }
        foreach(var condition in node.EnumerateObject())
        {
            string? expected = condition.Value.ValueKind switch
            {
                JsonValueKind.String => condition.Value.GetString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };
            if(expected is null || !properties.TryGetValue(condition.Name, out var actual)) return false;
            if(!expected.Split('|').Contains(actual, StringComparer.Ordinal)) return false;
        }
        return true;
    }

    private static string? ResolveTextureReference(string reference, IReadOnlyDictionary<string, string> textures)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while(reference.StartsWith('#'))
        {
            string variable = reference[1..];
            if(!visited.Add(variable) || !textures.TryGetValue(variable, out reference!)) return null;
        }
        return string.IsNullOrWhiteSpace(reference) ? null : reference;
    }

    private IEnumerable<ResourceCandidate> ResourceCandidates(BlockState state)
    {
        var candidates = BuildResourceCandidates(state)
            .Select((candidate, preferenceIndex) => new
            {
                Candidate = candidate,
                PreferenceIndex = preferenceIndex,
                Ordering = ResourceCandidateOrdering(candidate),
            })
            .OrderBy(candidate => candidate.Candidate.IsDirectCanonical == candidate.Ordering.PreferCanonical ? 0 : 1)
            .ThenByDescending(candidate => candidate.Ordering.LayerIndex)
            .ThenBy(candidate => candidate.PreferenceIndex);
        foreach(var candidate in candidates) yield return candidate.Candidate;
    }

    private (int LayerIndex, bool PreferCanonical) ResourceCandidateOrdering(ResourceCandidate candidate)
    {
        MinecraftResourceKey key;
        try
        {
            key = MinecraftResourceKey.Parse(
                $"assets/{candidate.Location.Namespace}/blockstates/{candidate.Location.Path}.json");
        }
        catch(FormatException)
        {
            return (-1, _resourcePackFormat >= CanonicalResourceNamesPackFormat);
        }
        if(!_stack.TryGetSource(key, out MinecraftResourceOrigin? origin))
        {
            return (-1, _resourcePackFormat >= CanonicalResourceNamesPackFormat);
        }
        int format = _resourcePackFormatsByLayer.GetValueOrDefault(origin!.LayerIndex, _resourcePackFormat);
        return (origin.LayerIndex, format >= CanonicalResourceNamesPackFormat);
    }

    private static IEnumerable<ResourceCandidate> BuildResourceCandidates(BlockState state)
    {
        var (resourceNamespace, path) = SplitName(state.Name);
        var properties = new Dictionary<string, string>(state.Properties, StringComparer.Ordinal);

        if(resourceNamespace != "minecraft")
        {
            yield return new ResourceCandidate(new ResourceLocation(resourceNamespace, path), properties, true);
            yield break;
        }

        if(resourceNamespace == "minecraft")
        {
            if(TryGetLegacyColoredResource(path, properties, out string coloredResource, out var coloredProperties))
            {
                yield return new ResourceCandidate(
                    new ResourceLocation(resourceNamespace, coloredResource),
                    coloredProperties);
            }
            if(path == "chain")
            {
                yield return new ResourceCandidate(new ResourceLocation(resourceNamespace, "iron_chain"), properties);
            }
            if(InfestedAliases.TryGetValue(path, out var infested))
            {
                yield return new ResourceCandidate(new ResourceLocation(resourceNamespace, infested), new Dictionary<string, string>());
            }
            if(DoublePlantAliases.TryGetValue(path, out var doublePlant))
            {
                var doublePlantProperties = new Dictionary<string, string>(StringComparer.Ordinal);
                if(properties.TryGetValue("half", out string? half)) doublePlantProperties["half"] = half;
                yield return new ResourceCandidate(new ResourceLocation(resourceNamespace, doublePlant), doublePlantProperties);
            }
            if(path is "oak_trapdoor" or "iron_trapdoor")
            {
                var trapdoorProperties = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach(string propertyName in new[] { "facing", "half", "open" })
                {
                    if(properties.TryGetValue(propertyName, out string? value)) trapdoorProperties[propertyName] = value;
                }
                string trapdoor = path == "oak_trapdoor" ? "trapdoor" : "iron_trapdoor";
                yield return new ResourceCandidate(new ResourceLocation(resourceNamespace, trapdoor), trapdoorProperties);
            }
            if(PottedContents.TryGetValue(path, out var contents))
            {
                yield return new ResourceCandidate(
                    new ResourceLocation(resourceNamespace, "flower_pot"),
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["contents"] = contents });
            }
            if(path == "flower_pot")
            {
                yield return new ResourceCandidate(
                    new ResourceLocation(resourceNamespace, "flower_pot"),
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["contents"] = "empty" });
            }
            if(path is "anvil" or "chipped_anvil" or "damaged_anvil")
            {
                var anvilProperties = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["damage"] = path switch
                    {
                        "chipped_anvil" => "1",
                        "damaged_anvil" => "2",
                        _ => "0",
                    },
                };
                if(properties.TryGetValue("facing", out string? facing)) anvilProperties["facing"] = facing;
                yield return new ResourceCandidate(new ResourceLocation(resourceNamespace, "anvil"), anvilProperties);
            }
            if(path == "torch")
            {
                yield return new ResourceCandidate(
                    new ResourceLocation(resourceNamespace, "torch"),
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["facing"] = "up" });
            }
            if(path == "quartz_pillar")
            {
                yield return new ResourceCandidate(new ResourceLocation(resourceNamespace, "quartz_column"), properties);
            }
            if(path == "wet_sponge")
            {
                yield return new ResourceCandidate(
                    new ResourceLocation(resourceNamespace, "sponge"),
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["wet"] = "true" });
            }
            if(TryGetLegacyMushroomVariant(path, properties, out string mushroomResource, out string mushroomVariant))
            {
                yield return new ResourceCandidate(
                    new ResourceLocation(resourceNamespace, mushroomResource),
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["variant"] = mushroomVariant });
            }
            if(path is "smooth_stone" or "smooth_quartz")
            {
                string doubleSlab = path == "smooth_stone" ? "stone_double_slab" : "quartz_double_slab";
                yield return new ResourceCandidate(
                    new ResourceLocation(resourceNamespace, doubleSlab),
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["all"] = "true" });
            }
            if(path is "repeater" or "comparator")
            {
                bool powered = properties.TryGetValue("powered", out var poweredValue) && poweredValue == "true";
                var deviceProperties = new Dictionary<string, string>(properties, StringComparer.Ordinal);
                if(path == "repeater") deviceProperties.Remove("powered");
                string deviceName = $"{(powered ? "powered" : "unpowered")}_{path}";
                yield return new ResourceCandidate(new ResourceLocation(resourceNamespace, deviceName), deviceProperties);
            }
            if(path is "redstone_torch" or "redstone_wall_torch")
            {
                bool lit = !properties.TryGetValue("lit", out var litValue) || litValue == "true";
                var torchProperties = new Dictionary<string, string>(properties, StringComparer.Ordinal);
                torchProperties.Remove("lit");
                if(path == "redstone_torch") torchProperties["facing"] = "up";
                string torchName = lit ? "redstone_torch" : "unlit_redstone_torch";
                yield return new ResourceCandidate(new ResourceLocation(resourceNamespace, torchName), torchProperties);
            }
            if(path.EndsWith("_terracotta", StringComparison.Ordinal) &&
               !path.EndsWith("_glazed_terracotta", StringComparison.Ordinal))
            {
                string color = path[..^"_terracotta".Length];
                string legacyColor = color == "light_gray" ? "silver" : color;
                string terracotta = $"{legacyColor}_stained_hardened_clay";
                yield return new ResourceCandidate(new ResourceLocation(resourceNamespace, terracotta), new Dictionary<string, string>());
            }
            if(path is "cobblestone_wall" or "mossy_cobblestone_wall" &&
               !properties.Values.Contains("true", StringComparer.Ordinal))
            {
                var wallProperties = new Dictionary<string, string>(properties, StringComparer.Ordinal) { ["up"] = "true" };
                yield return new ResourceCandidate(new ResourceLocation(resourceNamespace, path), wallProperties);
            }
            if(path == "petrified_oak_slab")
            {
                var slabProperties = new Dictionary<string, string>(properties, StringComparer.Ordinal);
                slabProperties.Remove("type");
                if(properties.TryGetValue("type", out var petrifiedType) && petrifiedType is "top" or "bottom")
                {
                    slabProperties["half"] = petrifiedType;
                }
                string slabName = properties.TryGetValue("type", out petrifiedType) && petrifiedType == "double"
                    ? "wood_old_double_slab"
                    : "wood_old_slab";
                yield return new ResourceCandidate(new ResourceLocation(resourceNamespace, slabName), slabProperties);
            }
            if(path.EndsWith("_slab", StringComparison.Ordinal) && properties.TryGetValue("type", out var slabType))
            {
                var slabProperties = new Dictionary<string, string>(properties, StringComparer.Ordinal);
                slabProperties.Remove("type");
                if(slabType is "top" or "bottom") slabProperties["half"] = slabType;
                string slabName = slabType == "double" ? DoubleSlabName(path) : path;
                yield return new ResourceCandidate(new ResourceLocation(resourceNamespace, slabName), slabProperties);
            }
            if(path.EndsWith("_wood", StringComparison.Ordinal))
            {
                string log = path[..^5] + "_log";
                var barkProperties = new Dictionary<string, string>(properties, StringComparer.Ordinal) { ["axis"] = "none" };
                yield return new ResourceCandidate(new ResourceLocation(resourceNamespace, log), barkProperties);
            }
            if(LegacyAliases.TryGetValue(path, out var alias) &&
               !DoublePlantAliases.ContainsKey(path) &&
               path != "oak_trapdoor")
            {
                yield return new ResourceCandidate(new ResourceLocation(resourceNamespace, alias), properties);
            }
            if(path.StartsWith("light_gray_", StringComparison.Ordinal))
            {
                string silver = "silver_" + path["light_gray_".Length..];
                yield return new ResourceCandidate(new ResourceLocation(resourceNamespace, silver), properties);
            }
        }
        yield return new ResourceCandidate(new ResourceLocation(resourceNamespace, path), properties, true);
    }

    private static bool TryGetLegacyColoredResource(
        string path,
        IReadOnlyDictionary<string, string> properties,
        out string resourcePath,
        out IReadOnlyDictionary<string, string> resourceProperties)
    {
        resourcePath = string.Empty;
        resourceProperties = properties;
        if(!properties.TryGetValue("color", out string? color) || !DyeColors.ContainsKey(color)) return false;

        string suffix = path switch
        {
            "wool" => "wool",
            "stained_glass" => "stained_glass",
            "stained_glass_pane" => "stained_glass_pane",
            "stained_hardened_clay" => "stained_hardened_clay",
            "carpet" => "carpet",
            "concrete" => "concrete",
            "concrete_powder" => "concrete_powder",
            _ => string.Empty,
        };
        if(suffix.Length == 0) return false;

        string legacyColor = color == "light_gray" ? "silver" : color;
        resourcePath = $"{legacyColor}_{suffix}";
        var withoutColor = new Dictionary<string, string>(properties, StringComparer.Ordinal);
        withoutColor.Remove("color");
        resourceProperties = withoutColor;
        return true;
    }

    private static bool TryGetLegacyMushroomVariant(
        string path,
        IReadOnlyDictionary<string, string> properties,
        out string resourcePath,
        out string variant)
    {
        resourcePath = path == "mushroom_stem" ? "brown_mushroom_block" : path;
        variant = string.Empty;
        if(path is not ("brown_mushroom_block" or "red_mushroom_block" or "mushroom_stem")) return false;

        string[] faces = ["north", "east", "south", "west", "up", "down"];
        int faceMask = 0;
        for(int index = 0; index < faces.Length; index++)
        {
            if(!properties.TryGetValue(faces[index], out string? value) ||
               !bool.TryParse(value, out bool visible)) return false;
            if(visible) faceMask |= 1 << index;
        }

        variant = path == "mushroom_stem"
            ? faceMask switch
            {
                15 => "stem",
                63 => "all_stem",
                _ => string.Empty,
            }
            : faceMask switch
            {
                0 => "all_inside",
                25 => "north_west",
                17 => "north",
                19 => "north_east",
                24 => "west",
                16 => "center",
                18 => "east",
                28 => "south_west",
                20 => "south",
                22 => "south_east",
                63 => "all_outside",
                _ => string.Empty,
            };
        return variant.Length > 0;
    }

    private static int? TryReadBaseClientPackFormat(MinecraftResourceStack stack)
    {
        MinecraftResourceOrigin? baseClient = stack.Layers.FirstOrDefault(
            layer => layer.Role == MinecraftResourceLayerRole.BaseClient);
        return baseClient is null ? null : TryReadLayerPackFormat(baseClient);
    }

    private static int? TryReadLayerPackFormat(MinecraftResourceOrigin layer)
    {
        try
        {
            if(layer.ContainerKind == MinecraftResourceContainerKind.Directory)
            {
                string metadataPath = Path.Combine(layer.SourcePath, "pack.mcmeta");
                var metadata = new FileInfo(metadataPath);
                if(!metadata.Exists || metadata.Length <= 0 || metadata.Length > MaximumPackMetadataBytes) return null;
                using var stream = new FileStream(metadata.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                return ReadPackFormat(stream);
            }

            using var file = new FileStream(layer.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
            ZipArchiveEntry? entry = archive.GetEntry("pack.mcmeta");
            if(entry is null || entry.Length <= 0 || entry.Length > MaximumPackMetadataBytes) return null;
            using var entryStream = entry.Open();
            return ReadPackFormat(entryStream);
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return null;
        }
    }

    private static int? ReadPackFormat(Stream stream)
    {
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16,
        });
        if(document.RootElement.ValueKind != JsonValueKind.Object ||
           !document.RootElement.TryGetProperty("pack", out JsonElement pack) ||
           pack.ValueKind != JsonValueKind.Object ||
           !pack.TryGetProperty("pack_format", out JsonElement format) ||
           !format.TryGetInt32(out int value) ||
           value <= 0)
        {
            return null;
        }
        return value;
    }

    private string ComputeResourceRevision()
    {
        string layerFormats = string.Join(
            ',',
            _resourcePackFormatsByLayer.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}"));
        string identity = $"{_stack.Revision}\npack_format={_resourcePackFormat}\nlayers={layerFormats}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private static string DoubleSlabName(string path) => path switch
    {
        "stone_slab" => "stone_double_slab",
        "sandstone_slab" => "sandstone_double_slab",
        "petrified_oak_slab" => "wood_old_double_slab",
        "cobblestone_slab" => "cobblestone_double_slab",
        "brick_slab" => "brick_double_slab",
        "stone_brick_slab" => "stone_brick_double_slab",
        "nether_brick_slab" => "nether_brick_double_slab",
        "quartz_slab" => "quartz_double_slab",
        "purpur_slab" => "purpur_double_slab",
        _ => path[..^5] + "_double_slab",
    };

    private static Vector3 ReadVector3(JsonElement owner, string propertyName)
    {
        if(!owner.TryGetProperty(propertyName, out var node) || node.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"模型缺少 {propertyName}。");
        var values = node.EnumerateArray().Select(ReadFiniteSingle).ToArray();
        if(values.Length != 3) throw new InvalidDataException($"模型 {propertyName} 必须有 3 个分量。");
        return new Vector3(values[0], values[1], values[2]);
    }

    private static Vector4 ReadVector4(JsonElement node)
    {
        if(node.ValueKind != JsonValueKind.Array) throw new InvalidDataException("面 UV 必须是数组。");
        var values = node.EnumerateArray().Select(ReadFiniteSingle).ToArray();
        if(values.Length != 4) throw new InvalidDataException("面 UV 必须有 4 个分量。");
        return new Vector4(values[0], values[1], values[2], values[3]);
    }

    private static float ReadFiniteSingle(JsonElement node)
    {
        if(!node.TryGetSingle(out float value) || !float.IsFinite(value)) throw new InvalidDataException("模型数值无效。");
        return value;
    }

    private static Vector4 DefaultFaceUv(BlockFace face, Vector3 from, Vector3 to) => face switch
    {
        BlockFace.Down => new Vector4(from.X, 16f - to.Z, to.X, 16f - from.Z),
        BlockFace.Up => new Vector4(from.X, from.Z, to.X, to.Z),
        BlockFace.North => new Vector4(16f - to.X, 16f - to.Y, 16f - from.X, 16f - from.Y),
        BlockFace.South => new Vector4(from.X, 16f - to.Y, to.X, 16f - from.Y),
        BlockFace.West => new Vector4(from.Z, 16f - to.Y, to.Z, 16f - from.Y),
        BlockFace.East => new Vector4(16f - to.Z, 16f - to.Y, 16f - from.Z, 16f - from.Y),
        _ => throw new ArgumentOutOfRangeException(nameof(face)),
    };

    private static bool TryParseFace(string value, out BlockFace face)
    {
        face = value switch
        {
            "west" => BlockFace.West,
            "east" => BlockFace.East,
            "down" => BlockFace.Down,
            "up" => BlockFace.Up,
            "north" => BlockFace.North,
            "south" => BlockFace.South,
            _ => (BlockFace)(-1),
        };
        return (int)face >= 0;
    }

    private static int NormalizeRightAngle(int value)
    {
        value = (value % 360 + 360) % 360;
        if(value % 90 != 0) throw new InvalidDataException("方块状态模型旋转必须是 90 度的倍数。");
        return value;
    }

    private static (string Namespace, string Path) SplitName(string name)
    {
        int separator = name.IndexOf(':');
        return separator < 0 ? ("minecraft", name) : (name[..separator], name[(separator + 1)..]);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private enum SignModelKind
    {
        Standing,
        Wall,
        Hanging,
        WallHanging,
    }

    private sealed record ResolvedState(
        BlockRenderDefinition Definition,
        IReadOnlyDictionary<BlockFace, IReadOnlyList<BlockFaceMaterial>> CubeFaceLayers,
        IReadOnlyList<ResolvedBlockModel> Models,
        bool UsesFallback)
    {
        public static ResolvedState Empty { get; } = new(
            BlockRenderDefinition.Empty,
            new Dictionary<BlockFace, IReadOnlyList<BlockFaceMaterial>>(),
            [],
            false);

        public static ResolvedState Invisible { get; } = new(
            BlockRenderDefinition.Invisible,
            new Dictionary<BlockFace, IReadOnlyList<BlockFaceMaterial>>(),
            [],
            false);
    }

    private sealed record ParsedModel(
        IReadOnlyDictionary<string, string> Textures,
        IReadOnlyList<ParsedElement> Elements);

    private sealed record ParsedElement(
        Vector3 From,
        Vector3 To,
        IReadOnlyDictionary<BlockFace, ParsedFace> Faces,
        ModelElementRotation? Rotation,
        bool Shade);

    private sealed record ParsedFace(
        string Texture,
        Vector4 Uv,
        BlockFace? CullFace,
        int Rotation,
        int TintIndex);

    private readonly record struct ModelApplication(ResourceLocation Model, int XRotation, int YRotation, bool UvLock);

    private sealed record ResourceCandidate(
        ResourceLocation Location,
        IReadOnlyDictionary<string, string> Properties,
        bool IsDirectCanonical = false);

    private readonly record struct ResourceLocation(string Namespace, string Path)
    {
        public static ResourceLocation Parse(string value, string defaultNamespace, string defaultPathPrefix)
        {
            if(string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("资源位置为空。");
            int separator = value.IndexOf(':');
            string resourceNamespace = separator >= 0 ? value[..separator] : defaultNamespace;
            string path = separator >= 0 ? value[(separator + 1)..] : value;
            if(path.Length == 0 || path.StartsWith('/') || path.Contains("..", StringComparison.Ordinal) || path.Contains('\\'))
            {
                throw new InvalidDataException($"非法资源位置：{value}");
            }
            if(defaultPathPrefix.Length > 0 && !path.Contains('/')) path = defaultPathPrefix + path;
            return new ResourceLocation(resourceNamespace, path);
        }

        public override string ToString() => $"{Namespace}:{Path}";
    }
}
