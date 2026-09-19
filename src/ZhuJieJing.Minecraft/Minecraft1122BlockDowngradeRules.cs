using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

/// <summary>
/// Deterministic, visual-fidelity-first mappings from canonical Java block states to vanilla 1.12.2.
/// Existing 1.12.2 states are recovered from the verified embedded registry; newer blocks use explicit
/// shape, orientation, and color substitutions. Unlisted blocks use a deterministic semantic material
/// fallback; only the intrinsically invisible modern light block may become air while retaining light data.
/// </summary>
public sealed class Minecraft1122BlockDowngradeRules : IRevisionedMinecraftBlockDowngradeRules
{
    private const string BuiltInRevision = "minecraft-1.12.2-mappings-v3";
    private static readonly Lazy<RegistryIndex> LegacyRegistry = new(
        BuildRegistryIndex,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Minecraft1122BlockMappingOverrideSet overrides;

    private static readonly (string Name, byte Metadata)[] DyeColors =
    [
        ("light_blue", 3),
        ("light_gray", 8),
        ("white", 0),
        ("orange", 1),
        ("magenta", 2),
        ("yellow", 4),
        ("lime", 5),
        ("pink", 6),
        ("gray", 7),
        ("cyan", 9),
        ("purple", 10),
        ("blue", 11),
        ("brown", 12),
        ("green", 13),
        ("red", 14),
        ("black", 15),
    ];

    private static readonly IReadOnlyDictionary<string, ushort> LegacyStairIds =
        new Dictionary<string, ushort>(StringComparer.Ordinal)
        {
            ["oak_stairs"] = 53,
            ["cobblestone_stairs"] = 67,
            ["brick_stairs"] = 108,
            ["stone_brick_stairs"] = 109,
            ["nether_brick_stairs"] = 114,
            ["sandstone_stairs"] = 128,
            ["spruce_stairs"] = 134,
            ["birch_stairs"] = 135,
            ["jungle_stairs"] = 136,
            ["quartz_stairs"] = 156,
            ["acacia_stairs"] = 163,
            ["dark_oak_stairs"] = 164,
            ["red_sandstone_stairs"] = 180,
            ["purpur_stairs"] = 203,
        };

    public Minecraft1122BlockDowngradeRules() : this(Minecraft1122BlockMappingOverrideSet.Empty)
    {
    }

    private Minecraft1122BlockDowngradeRules(Minecraft1122BlockMappingOverrideSet overrides)
    {
        this.overrides = overrides ?? throw new ArgumentNullException(nameof(overrides));
        Revision = $"{BuiltInRevision}+{overrides.Revision}";
    }

    public static Minecraft1122BlockDowngradeRules Instance { get; } = new();

    public IReadOnlyList<Minecraft1122BlockMappingOverride> Overrides => overrides.Entries;

    public string Revision { get; }

    public static Minecraft1122BlockDowngradeRules WithOverrides(Minecraft1122BlockMappingOverrideSet overrides) => new(overrides);

    public MinecraftBlockMapping Resolve(BlockState source, MinecraftTargetProfile target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        if (!IsSupportedTarget(target))
        {
            return new MinecraftBlockMapping(
                source,
                null,
                null,
                MinecraftBlockMappingQuality.Blocked,
                "target.unsupported",
                $"规则集仅支持 Minecraft Java 1.12.2，当前目标为 {target.Id}。");
        }

        if (source.IsAir)
        {
            return Map(
                source,
                new LegacyBlockEncoding(0, 0),
                MinecraftBlockMappingQuality.Exact,
                "legacy.air",
                "空气变体统一写为 1.12.2 空气。");
        }

        if(overrides.TryResolve(source, out Minecraft1122BlockMappingOverride? userOverride))
            return ApplyUserOverride(source, userOverride);

        if (!TryGetVanillaPath(source.Name, out string path)) return VisibleFallback(source);

        if(LegacySkullStates.TryDescribe(source, out _, out bool wallSkull))
            return new MinecraftBlockMapping(source, source, LegacySkullStates.Encoding(source, wallSkull),
                MinecraftBlockMappingQuality.Exact, "legacy.skull.entity_state",
                "保留头颅种类与朝向；旧版子 ID 表示放置面，旋转写入方块实体 Rot。");

        if (TryMapFluid(source, path, out MinecraftBlockMapping mapping)) return mapping;
        if (TryMapColored(source, path, out mapping)) return mapping;
        if (LegacyRegistry.Value.Exact.TryGetValue(source.CanonicalKey, out LegacyEntry? exact))
        {
            return Create(
                source,
                exact,
                MinecraftBlockMappingQuality.Exact,
                "legacy.exact",
                "该规范方块状态在 1.12.2 中有精确 ID:data。");
        }

        if (TryMapWood(source, path, out mapping)) return mapping;
        if (TryMapStairs(source, path, out mapping)) return mapping;
        if (TryMapSlabs(source, path, out mapping)) return mapping;
        if (TryMapCompatibleLegacyState(source, path, out mapping)) return mapping;
        if (TryMapModernShape(source, path, out mapping)) return mapping;
        if (TryMapBuiltInTable(source, out mapping)) return mapping;
        if (TryMapModernMaterial(source, path, out mapping)) return mapping;
        return VisibleFallback(source);
    }

    private static MinecraftBlockMapping ApplyUserOverride(
        BlockState source,
        Minecraft1122BlockMappingOverride userOverride)
    {
        LegacyBlockEncoding encoding = userOverride.LegacyEncoding;
        if(!userOverride.SourceKey.Contains('[', StringComparison.Ordinal) && encoding.NumericId != 0)
            encoding = PreserveCompatibleTargetState(source, encoding);
        bool allowsInvisible = encoding.NumericId == 0 &&
                               Minecraft1122BlockMappingOverrideSet.IsInvisibleLightKey(source.CanonicalKey);
        return Map(
            source,
            encoding,
            MinecraftBlockMappingQuality.Similar,
            "user.override",
            userOverride.Note ?? "使用方块映射表中的用户替换；按方块名覆盖时自动保留目标可表达的朝向与形态属性。",
            allowsInvisible);
    }

    private static bool TryMapBuiltInTable(BlockState source, out MinecraftBlockMapping mapping)
    {
        mapping = null!;
        if(!Minecraft1122BuiltInBlockMappingTable.TryResolve(source, out Minecraft1122BlockMappingDefinition? definition))
            return false;
        LegacyBlockEncoding encoding = definition.LegacyEncoding.NumericId == 0
            ? definition.LegacyEncoding
            : PreserveCompatibleTargetState(source, definition.LegacyEncoding);
        mapping = Map(
            source,
            encoding,
            definition.Quality,
            definition.RuleId,
            definition.Explanation,
            definition.LegacyEncoding.NumericId == 0 && source.Name == "minecraft:light");
        return true;
    }

    private static bool TryMapFluid(BlockState source, string path, out MinecraftBlockMapping mapping)
    {
        mapping = null!;
        if (path is not ("water" or "lava")) return false;

        int level = IntegerProperty(source, "level", 0, 0, 15);
        ushort id = path == "water"
            ? (ushort)(level == 0 ? 9 : 8)
            : (ushort)(level == 0 ? 11 : 10);
        mapping = Map(
            source,
            new LegacyBlockEncoding(id, (byte)level),
            MinecraftBlockMappingQuality.Exact,
            "legacy.fluid_level",
            "保留流体种类与 level，源方块使用静态 ID，流动方块使用流动 ID。");
        return true;
    }

    private static bool TryMapColored(BlockState source, string path, out MinecraftBlockMapping mapping)
    {
        mapping = null!;
        bool legacyPropertyState = TryReadLegacyDyeProperty(source, path, out byte color);
        if (!legacyPropertyState && !TryReadDyePrefix(path, out color)) return false;

        ushort id;
        byte metadata = color;
        string family;
        if (legacyPropertyState && path == "stained_glass_pane" ||
            !legacyPropertyState && path.EndsWith("_stained_glass_pane", StringComparison.Ordinal))
        {
            id = 160;
            family = "stained_glass_pane";
        }
        else if (legacyPropertyState && path == "stained_glass" ||
                 !legacyPropertyState && path.EndsWith("_stained_glass", StringComparison.Ordinal))
        {
            id = 95;
            family = "stained_glass";
        }
        else if (legacyPropertyState && path == "concrete_powder" ||
                 !legacyPropertyState && path.EndsWith("_concrete_powder", StringComparison.Ordinal))
        {
            id = 252;
            family = "concrete_powder";
        }
        else if (path.EndsWith("_glazed_terracotta", StringComparison.Ordinal))
        {
            id = (ushort)(235 + color);
            metadata = GlazedTerracottaFacing(source);
            family = "glazed_terracotta";
        }
        else if (legacyPropertyState && path == "stained_hardened_clay" ||
                 !legacyPropertyState && path.EndsWith("_terracotta", StringComparison.Ordinal))
        {
            id = 159;
            family = "terracotta";
        }
        else if (legacyPropertyState && path == "concrete" ||
                 !legacyPropertyState && path.EndsWith("_concrete", StringComparison.Ordinal))
        {
            id = 251;
            family = "concrete";
        }
        else if (legacyPropertyState && path == "carpet" ||
                 !legacyPropertyState && path.EndsWith("_carpet", StringComparison.Ordinal))
        {
            id = 171;
            family = "carpet";
        }
        else if (legacyPropertyState && path == "wool" ||
                 !legacyPropertyState && path.EndsWith("_wool", StringComparison.Ordinal))
        {
            id = 35;
            family = "wool";
        }
        else
        {
            return false;
        }

        mapping = Map(
            source,
            new LegacyBlockEncoding(id, metadata),
            MinecraftBlockMappingQuality.Exact,
            $"legacy.color.{family}",
            "保留 16 色系的颜色 metadata，同时保留方块材质类型。");
        return true;
    }

    private static bool TryReadLegacyDyeProperty(BlockState source, string path, out byte metadata)
    {
        metadata = 0;
        if(path is not ("wool" or "stained_glass" or "stained_glass_pane" or
                        "stained_hardened_clay" or "carpet" or "concrete" or "concrete_powder") ||
           !source.Properties.TryGetValue("color", out string? color)) return false;

        foreach((string name, byte value) in DyeColors)
        {
            if(!string.Equals(name, color, StringComparison.Ordinal)) continue;
            metadata = value;
            return true;
        }
        return false;
    }

    private static bool TryMapWood(BlockState source, string path, out MinecraftBlockMapping mapping)
    {
        mapping = null!;
        if (!TryWoodFamily(path, out WoodFamily family)) return false;

        string materialPath = path.StartsWith("stripped_", StringComparison.Ordinal) ? path[9..] : path;
        bool exact = family.IsLegacy && !path.StartsWith("stripped_", StringComparison.Ordinal);
        MinecraftBlockMappingQuality quality = exact
            ? MinecraftBlockMappingQuality.Exact
            : MinecraftBlockMappingQuality.Similar;
        string explanation = exact
            ? "保留 1.12.2 木材种类与可编码状态。"
            : $"使用颜色和明度最接近的 {family.LegacyDisplayName}，并保留可编码的形状与朝向。";

        if (materialPath.EndsWith("_planks", StringComparison.Ordinal) ||
            materialPath == "bamboo_mosaic")
        {
            mapping = MapWood(source, 5, family, 0, quality, "planks", explanation);
            return true;
        }

        if (materialPath.EndsWith("_sapling", StringComparison.Ordinal) || materialPath == "mangrove_propagule")
        {
            byte stage = BooleanOrOneProperty(source, "stage") ? (byte)8 : (byte)0;
            mapping = MapWood(source, 6, family, stage, quality, "sapling", explanation);
            return true;
        }

        bool allBark = materialPath.EndsWith("_wood", StringComparison.Ordinal) ||
                       materialPath.EndsWith("_hyphae", StringComparison.Ordinal);
        bool logLike = materialPath.EndsWith("_log", StringComparison.Ordinal) ||
                       materialPath.EndsWith("_stem", StringComparison.Ordinal) ||
                       allBark ||
                       materialPath == "bamboo_block";
        if (logLike)
        {
            (ushort id, byte species) = family.LegacySpecies <= 3
                ? ((ushort)17, family.LegacySpecies)
                : ((ushort)162, (byte)(family.LegacySpecies - 4));
            byte axis = allBark ? (byte)12 : AxisBits(source);
            mapping = Map(
                source,
                new LegacyBlockEncoding(id, (byte)(species | axis)),
                quality,
                exact ? "legacy.wood.log_axis" : "modern.wood.log_axis",
                explanation);
            return true;
        }

        if (materialPath.EndsWith("_leaves", StringComparison.Ordinal))
        {
            (ushort id, byte species) = family.LegacySpecies <= 3
                ? ((ushort)18, family.LegacySpecies)
                : ((ushort)161, (byte)(family.LegacySpecies - 4));
            byte persistent = BooleanProperty(source, "persistent") ? (byte)4 : (byte)0;
            mapping = Map(
                source,
                new LegacyBlockEncoding(id, (byte)(species | persistent)),
                quality,
                exact ? "legacy.wood.leaves_persistent" : "modern.wood.leaves_persistent",
                explanation);
            return true;
        }

        if (materialPath.EndsWith("_fence", StringComparison.Ordinal))
        {
            ushort id = family.LegacySpecies switch
            {
                0 => 85,
                1 => 188,
                2 => 189,
                3 => 190,
                4 => 192,
                _ => 191,
            };
            mapping = Map(source, new LegacyBlockEncoding(id, 0), quality, "legacy.wood.fence", explanation);
            return true;
        }

        if (materialPath.EndsWith("_fence_gate", StringComparison.Ordinal))
        {
            ushort id = family.LegacySpecies switch
            {
                0 => 107,
                1 => 183,
                2 => 184,
                3 => 185,
                4 => 187,
                _ => 186,
            };
            mapping = Map(source, new LegacyBlockEncoding(id, FenceGateMetadata(source)), quality, "legacy.wood.fence_gate", explanation);
            return true;
        }

        if (materialPath.EndsWith("_door", StringComparison.Ordinal))
        {
            ushort id = family.LegacySpecies switch
            {
                0 => 64,
                1 => 193,
                2 => 194,
                3 => 195,
                4 => 196,
                _ => 197,
            };
            mapping = Map(source, new LegacyBlockEncoding(id, DoorMetadata(source)), quality, "legacy.wood.door", explanation);
            return true;
        }

        if (materialPath.EndsWith("_trapdoor", StringComparison.Ordinal))
        {
            mapping = Map(source, new LegacyBlockEncoding(96, TrapdoorMetadata(source)), quality, "legacy.wood.trapdoor", explanation);
            return true;
        }

        if (materialPath.EndsWith("_button", StringComparison.Ordinal))
        {
            mapping = Map(source, new LegacyBlockEncoding(143, ButtonMetadata(source)), quality, "legacy.wood.button", explanation);
            return true;
        }

        if (materialPath.EndsWith("_pressure_plate", StringComparison.Ordinal))
        {
            byte powered = BooleanProperty(source, "powered") ? (byte)1 : (byte)0;
            mapping = Map(source, new LegacyBlockEncoding(72, powered), quality, "legacy.wood.pressure_plate", explanation);
            return true;
        }

        if (materialPath.EndsWith("_wall_sign", StringComparison.Ordinal))
        {
            mapping = Map(source, new LegacyBlockEncoding(68, WallFacingMetadata(source)), quality, "legacy.wood.wall_sign", explanation);
            return true;
        }

        if (materialPath.EndsWith("_sign", StringComparison.Ordinal))
        {
            byte rotation = (byte)IntegerProperty(source, "rotation", 0, 0, 15);
            mapping = Map(source, new LegacyBlockEncoding(63, rotation), quality, "legacy.wood.sign", explanation);
            return true;
        }

        return false;
    }

    private static bool TryMapStairs(BlockState source, string path, out MinecraftBlockMapping mapping)
    {
        mapping = null!;
        if (!path.EndsWith("_stairs", StringComparison.Ordinal)) return false;

        ushort id;
        bool exact = LegacyStairIds.TryGetValue(path, out id);
        string replacement;
        if (exact)
        {
            replacement = path;
        }
        else if (TryWoodFamily(path, out WoodFamily family))
        {
            replacement = family.LegacySpecies switch
            {
                0 => "oak_stairs",
                1 => "spruce_stairs",
                2 => "birch_stairs",
                3 => "jungle_stairs",
                4 => "acacia_stairs",
                _ => "dark_oak_stairs",
            };
            id = LegacyStairIds[replacement];
        }
        else
        {
            replacement = SimilarStair(path);
            id = LegacyStairIds[replacement];
        }

        mapping = Map(
            source,
            new LegacyBlockEncoding(id, StairMetadata(source)),
            exact ? MinecraftBlockMappingQuality.Exact : MinecraftBlockMappingQuality.Similar,
            exact ? "legacy.stairs.facing_half" : $"modern.stairs.{replacement}",
            exact
                ? "保留楼梯材质、facing 与 half；shape 由 1.12.2 客户端按邻块重算。"
                : $"替换为 {replacement}，保留楼梯形体、facing 与 half。");
        return true;
    }

    private static bool TryMapSlabs(BlockState source, string path, out MinecraftBlockMapping mapping)
    {
        mapping = null!;
        if (!path.EndsWith("_slab", StringComparison.Ordinal)) return false;

        bool isDouble = Property(source, "type") == "double";
        byte top = Property(source, "type") == "top" ? (byte)8 : (byte)0;
        if (TryWoodFamily(path, out WoodFamily wood))
        {
            ushort id = isDouble ? (ushort)125 : (ushort)126;
            byte metadata = (byte)(wood.LegacySpecies | (isDouble ? 0 : top));
            mapping = Map(
                source,
                new LegacyBlockEncoding(id, metadata),
                wood.IsLegacy && !path.Contains("mosaic", StringComparison.Ordinal)
                    ? MinecraftBlockMappingQuality.Exact
                    : MinecraftBlockMappingQuality.Similar,
                wood.IsLegacy ? "legacy.slab.wood_type" : "modern.slab.wood_substitute",
                "保留木台阶的单/双层、top/bottom 与最接近木色。");
            return true;
        }

        SlabSubstitution slab = SimilarSlab(path);
        ushort slabId = slab.Kind switch
        {
            SlabKind.RedSandstone => isDouble ? (ushort)181 : (ushort)182,
            SlabKind.Purpur => isDouble ? (ushort)204 : (ushort)205,
            _ => isDouble ? (ushort)43 : (ushort)44,
        };
        byte slabMetadata = slab.Kind switch
        {
            SlabKind.RedSandstone or SlabKind.Purpur => isDouble ? (byte)0 : top,
            _ => (byte)(slab.Variant | (isDouble ? 0 : top)),
        };
        mapping = Map(
            source,
            new LegacyBlockEncoding(slabId, slabMetadata),
            slab.Exact ? MinecraftBlockMappingQuality.Exact : MinecraftBlockMappingQuality.Similar,
            slab.Exact ? "legacy.slab.type" : $"modern.slab.{slab.RuleName}",
            slab.Exact
                ? "保留台阶材质、单/双层与 top/bottom。"
                : $"替换为最接近的 {slab.RuleName} 台阶，保留单/双层与 top/bottom。");
        return true;
    }

    private static bool TryMapCompatibleLegacyState(BlockState source, string path, out MinecraftBlockMapping mapping)
    {
        mapping = null!;
        string fullName = $"minecraft:{path}";
        if (!LegacyRegistry.Value.ByName.TryGetValue(fullName, out IReadOnlyList<LegacyEntry>? candidates)) return false;

        LegacyEntry? best = null;
        int bestScore = int.MinValue;
        foreach (LegacyEntry candidate in candidates)
        {
            int score = CompatibilityScore(source, candidate.State, path);
            if (score < 0 || score <= bestScore) continue;
            best = candidate;
            bestScore = score;
        }
        if (best is null) return false;

        mapping = Create(
            source,
            best,
            MinecraftBlockMappingQuality.Exact,
            "legacy.compatible_state",
            "该方块存在于 1.12.2；保留当时可写入 ID:data 的属性，忽略由邻块或新版本派生的属性。");
        return true;
    }

    private static bool TryMapModernShape(BlockState source, string path, out MinecraftBlockMapping mapping)
    {
        mapping = null!;
        if (path.EndsWith("_wall", StringComparison.Ordinal))
        {
            byte mossy = path.Contains("mossy", StringComparison.Ordinal) ? (byte)1 : (byte)0;
            mapping = Similar(source, 139, mossy, "modern.shape.wall", "替换为圆石墙，保留墙体连接形状。");
            return true;
        }

        if (path.EndsWith("_door", StringComparison.Ordinal))
        {
            ushort id = path.Contains("oxidized", StringComparison.Ordinal) || path.Contains("weathered", StringComparison.Ordinal)
                ? (ushort)197
                : (ushort)196;
            mapping = Similar(source, id, DoorMetadata(source), "modern.shape.door", "替换为颜色接近的旧版门，保留上下半、朝向、开合与铰链状态。");
            return true;
        }

        if (path.EndsWith("_trapdoor", StringComparison.Ordinal))
        {
            mapping = Similar(source, 167, TrapdoorMetadata(source), "modern.shape.trapdoor", "替换为铁活板门，保留 facing、half 与 open。");
            return true;
        }

        if (path.EndsWith("_button", StringComparison.Ordinal))
        {
            mapping = Similar(source, 77, ButtonMetadata(source), "modern.shape.button", "替换为石按钮，保留安装面、朝向与按下状态。");
            return true;
        }

        if (path.EndsWith("_pressure_plate", StringComparison.Ordinal))
        {
            byte powered = BooleanProperty(source, "powered") ? (byte)1 : (byte)0;
            mapping = Similar(source, 70, powered, "modern.shape.pressure_plate", "替换为石质压力板，保留触发状态。");
            return true;
        }

        return false;
    }

    private static bool TryMapModernMaterial(BlockState source, string path, out MinecraftBlockMapping mapping)
    {
        mapping = null!;

        if (path.StartsWith("deepslate_", StringComparison.Ordinal) && path.EndsWith("_ore", StringComparison.Ordinal))
        {
            (int id, string ore) = path switch
            {
                "deepslate_coal_ore" => (16, "coal"),
                "deepslate_iron_ore" => (15, "iron"),
                "deepslate_gold_ore" => (14, "gold"),
                "deepslate_redstone_ore" => (73, "redstone"),
                "deepslate_emerald_ore" => (129, "emerald"),
                "deepslate_lapis_ore" => (21, "lapis"),
                "deepslate_diamond_ore" => (56, "diamond"),
                "deepslate_copper_ore" => (15, "iron"),
                _ => (1, "stone"),
            };
            mapping = Similar(source, (ushort)id, 0, $"modern.deepslate_ore.{ore}", "保留矿石类别；铜矿用颜色最接近的铁矿替代。");
            return true;
        }

        if (path.Contains("deepslate", StringComparison.Ordinal))
        {
            LegacyBlockEncoding encoding = path.Contains("reinforced", StringComparison.Ordinal)
                ? new LegacyBlockEncoding(49, 0)
                : path.Contains("chiseled", StringComparison.Ordinal)
                    ? new LegacyBlockEncoding(98, 3)
                    : path.Contains("brick", StringComparison.Ordinal) || path.Contains("tile", StringComparison.Ordinal)
                        ? new LegacyBlockEncoding(98, path.Contains("cracked", StringComparison.Ordinal) ? (byte)2 : (byte)0)
                        : new LegacyBlockEncoding(1, path.Contains("polished", StringComparison.Ordinal) ? (byte)6 : (byte)0);
            mapping = Similar(source, encoding.NumericId, encoding.Metadata, "modern.deepslate", "使用明度和砌筑纹理接近的石材，保留粗石/砖/雕刻语义。");
            return true;
        }

        if (path.Contains("tuff", StringComparison.Ordinal))
        {
            LegacyBlockEncoding encoding = path.Contains("chiseled", StringComparison.Ordinal)
                ? new LegacyBlockEncoding(98, 3)
                : path.Contains("brick", StringComparison.Ordinal)
                    ? new LegacyBlockEncoding(98, 0)
                    : new LegacyBlockEncoding(1, path.Contains("polished", StringComparison.Ordinal) ? (byte)6 : (byte)5);
            mapping = Similar(source, encoding.NumericId, encoding.Metadata, "modern.tuff", "用安山岩/石砖保留冷灰色调与砌筑纹理。");
            return true;
        }

        if (path.Contains("copper_ore", StringComparison.Ordinal))
        {
            mapping = Similar(source, 15, 0, "modern.copper_ore", "铜矿替换为颜色与斑驳程度最接近的铁矿。");
            return true;
        }

        if (path.Contains("copper", StringComparison.Ordinal))
        {
            byte color = path.Contains("oxidized", StringComparison.Ordinal)
                ? (byte)9
                : path.Contains("weathered", StringComparison.Ordinal)
                    ? (byte)3
                    : path.Contains("exposed", StringComparison.Ordinal)
                        ? (byte)8
                        : (byte)1;
            mapping = Similar(source, 159, color, "modern.copper", "用染色陶瓦区分铜的原始、斑驳、锈蚀阶段，避免铜色块消失。");
            return true;
        }

        if (path.Contains("amethyst", StringComparison.Ordinal))
        {
            bool cluster = path.Contains("cluster", StringComparison.Ordinal) || path.Contains("bud", StringComparison.Ordinal);
            mapping = Similar(
                source,
                cluster ? (ushort)95 : (ushort)201,
                cluster ? (byte)10 : (byte)0,
                cluster ? "modern.amethyst.cluster" : "modern.amethyst.block",
                cluster ? "紫水晶簇用紫色玻璃保留通透紫色。" : "紫水晶块用紫珀块保留明度与色相。");
            return true;
        }

        if (path.Contains("sculk", StringComparison.Ordinal))
        {
            if (path.Contains("vein", StringComparison.Ordinal))
            {
                mapping = Similar(source, 171, 9, "modern.sculk.vein", "幽匠脉络用青色地毯保留薄层形态与青色。");
            }
            else if (path.Contains("sensor", StringComparison.Ordinal))
            {
                mapping = Similar(source, 151, 0, "modern.sculk.sensor", "幽匠感测体用日光传感器保留低矮功能块形态。");
            }
            else
            {
                mapping = Similar(source, 168, 2, "modern.sculk.block", "幽匠方块用暗海晶石保留深青黑色调。");
            }
            return true;
        }

        if (path.Contains("mud", StringComparison.Ordinal))
        {
            if (path.Contains("brick", StringComparison.Ordinal))
            {
                mapping = Similar(source, 45, 0, "modern.mud.bricks", "泥砖用红砖保留砌筑纹理与暖色。");
            }
            else if (path.Contains("packed", StringComparison.Ordinal) || path.Contains("roots", StringComparison.Ordinal))
            {
                mapping = Similar(source, 172, 0, "modern.mud.packed", "板结泥/泥巴红树根用硬化粘土保留土质纹理。");
            }
            else
            {
                mapping = Similar(source, 159, 7, "modern.mud", "泥巴用灰色染色陶瓦保留深灰色与实心形态。");
            }
            return true;
        }

        if (path.Contains("mangrove_roots", StringComparison.Ordinal))
        {
            mapping = Similar(source, 17, 12, "modern.mangrove_roots", "红树根用全树皮橡木保留根系的木质表面。");
            return true;
        }

        if (path.Contains("calcite", StringComparison.Ordinal))
        {
            mapping = Similar(source, 155, 0, "modern.calcite", "方解石用石英块保留明亮的白色石质。");
            return true;
        }

        if (path.Contains("dripstone", StringComparison.Ordinal))
        {
            mapping = path.Contains("pointed", StringComparison.Ordinal)
                ? Similar(source, 139, 0, "modern.dripstone.pointed", "尖滴水石用细窄的圆石墙保留竖向形体。")
                : Similar(source, 172, 0, "modern.dripstone.block", "滴水石块用硬化粘土保留暖棕色石质。");
            return true;
        }

        if (path.Contains("pale_moss", StringComparison.Ordinal))
        {
            ushort id = path.Contains("carpet", StringComparison.Ordinal) ? (ushort)171 : (ushort)35;
            mapping = Similar(source, id, 8, "modern.pale_moss", "苍白苔藓用浅灰羊毛/地毯保留灰白色和薄层形态。");
            return true;
        }

        if (path.Contains("moss", StringComparison.Ordinal) || path.Contains("azalea", StringComparison.Ordinal))
        {
            ushort id = path.Contains("carpet", StringComparison.Ordinal) ? (ushort)171 : (ushort)35;
            mapping = Similar(source, id, 13, "modern.moss_azalea", "苔藓/杜鹃用绿色羊毛或地毯保留绿色和实心/薄层形态。");
            return true;
        }

        if (path.Contains("blackstone", StringComparison.Ordinal) || path.Contains("basalt", StringComparison.Ordinal))
        {
            mapping = Similar(source, 173, 0, "modern.dark_stone", "黑石/玄武岩用煤炭块保留黑色高对比外观。");
            return true;
        }

        if (path.Contains("ancient_debris", StringComparison.Ordinal))
        {
            mapping = Similar(source, 49, 0, "modern.ancient_debris", "远古残骸用黑曜石保留深色、高硬度视觉重量。");
            return true;
        }

        if (path.Contains("netherite", StringComparison.Ordinal))
        {
            mapping = Similar(source, 42, 0, "modern.netherite", "下界合金块用铁块保留金属块形态。");
            return true;
        }

        if (path == "soul_soil")
        {
            mapping = Similar(source, 88, 0, "modern.soul_soil", "灵魂土替换为灵魂沙，保留色调与下界材质语义。");
            return true;
        }

        if (path.Contains("warped_wart", StringComparison.Ordinal) || path.Contains("warped_nylium", StringComparison.Ordinal))
        {
            mapping = Similar(source, 168, 2, "modern.warped_surface", "诡异菌类表面用暗海晶石保留深青色调。");
            return true;
        }

        if (path.Contains("crimson_nylium", StringComparison.Ordinal))
        {
            mapping = Similar(source, 214, 0, "modern.crimson_surface", "绯红菌岩用地狱疣块保留暗红色调。");
            return true;
        }

        if (path.Contains("shroomlight", StringComparison.Ordinal) ||
            path.Contains("froglight", StringComparison.Ordinal) ||
            path is "lantern" or "soul_lantern" or "sea_pickle")
        {
            mapping = Similar(source, 89, 0, "modern.light_source", "新版光源用可见萤石保留发光位置，不会透明消失。");
            return true;
        }

        if (path.Contains("powder_snow", StringComparison.Ordinal))
        {
            mapping = Similar(source, 80, 0, "modern.powder_snow", "细雪用雪块保留白色体积。");
            return true;
        }

        if (path == "bamboo")
        {
            mapping = Similar(source, 83, 0, "modern.bamboo_plant", "竹子用细长的甘蔗保留竖向植物轮廓。");
            return true;
        }

        if (path.Contains("kelp", StringComparison.Ordinal) || path.Contains("seagrass", StringComparison.Ordinal))
        {
            mapping = Similar(source, 31, 1, "modern.aquatic_plant", "水生植物用高草保留绿色、细长植物轮廓。");
            return true;
        }

        if (path.Contains("coral", StringComparison.Ordinal))
        {
            byte color = (byte)(StableHash(source.CanonicalKey) & 15);
            mapping = Similar(source, 159, color, "modern.coral", "珊瑚用稳定颜色的染色陶瓦保留色彩分区，避免整片消失。");
            return true;
        }

        if (path.Contains("honey", StringComparison.Ordinal) || path.Contains("ochre", StringComparison.Ordinal))
        {
            mapping = Similar(source, 41, 0, "modern.honey_ochre", "蜂蜜/赭黄色块用金块保留明亮暖黄色。");
            return true;
        }

        if (path.Contains("resin", StringComparison.Ordinal))
        {
            mapping = Similar(source, 159, 1, "modern.resin", "树脂用橙色染色陶瓦保留暖橙色。");
            return true;
        }

        if (path == "crafter")
        {
            mapping = Similar(source, 58, 0, "modern.crafter", "合成器用工作台保留制作设备的木质外观。");
            return true;
        }

        if (path == "trial_spawner")
        {
            mapping = Similar(source, 52, 0, "modern.trial_spawner", "试炼刷怪笼用刷怪笼保留笼状轮廓。");
            return true;
        }

        if (path == "vault")
        {
            mapping = Similar(source, 42, 0, "modern.vault", "宝库用铁块保留金属实心体积。");
            return true;
        }

        return false;
    }

    private static MinecraftBlockMapping VisibleFallback(BlockState source)
    {
        string path = source.Name[(source.Name.IndexOf(':') + 1)..];
        (LegacyBlockEncoding Encoding, string Family) fallback = path switch
        {
            _ when path.Contains("glass", StringComparison.Ordinal) => (new LegacyBlockEncoding(20, 0), "glass"),
            _ when path.Contains("leaves", StringComparison.Ordinal) ||
                   path.Contains("bush", StringComparison.Ordinal) ||
                   path.Contains("plant", StringComparison.Ordinal) => (new LegacyBlockEncoding(18, 0), "foliage"),
            _ when path.Contains("flower", StringComparison.Ordinal) ||
                   path.Contains("grass", StringComparison.Ordinal) ||
                   path.Contains("sprout", StringComparison.Ordinal) => (new LegacyBlockEncoding(31, 1), "small_plant"),
            _ when path.Contains("plank", StringComparison.Ordinal) ||
                   path.Contains("wood", StringComparison.Ordinal) ||
                   path.Contains("log", StringComparison.Ordinal) => (new LegacyBlockEncoding(5, 0), "wood"),
            _ when path.Contains("brick", StringComparison.Ordinal) ||
                   path.Contains("tile", StringComparison.Ordinal) => (new LegacyBlockEncoding(98, 0), "masonry"),
            _ when path.Contains("iron", StringComparison.Ordinal) ||
                   path.Contains("metal", StringComparison.Ordinal) => (new LegacyBlockEncoding(42, 0), "metal"),
            _ when path.Contains("quartz", StringComparison.Ordinal) ||
                   path.Contains("white", StringComparison.Ordinal) => (new LegacyBlockEncoding(155, 0), "light_stone"),
            _ when path.Contains("black", StringComparison.Ordinal) ||
                   path.Contains("dark", StringComparison.Ordinal) => (new LegacyBlockEncoding(173, 0), "dark_solid"),
            _ when path.Contains("red", StringComparison.Ordinal) => (new LegacyBlockEncoding(159, 14), "red_solid"),
            _ when path.Contains("orange", StringComparison.Ordinal) => (new LegacyBlockEncoding(159, 1), "orange_solid"),
            _ when path.Contains("yellow", StringComparison.Ordinal) => (new LegacyBlockEncoding(159, 4), "yellow_solid"),
            _ when path.Contains("green", StringComparison.Ordinal) => (new LegacyBlockEncoding(159, 13), "green_solid"),
            _ when path.Contains("cyan", StringComparison.Ordinal) => (new LegacyBlockEncoding(159, 9), "cyan_solid"),
            _ when path.Contains("blue", StringComparison.Ordinal) => (new LegacyBlockEncoding(159, 11), "blue_solid"),
            _ when path.Contains("purple", StringComparison.Ordinal) => (new LegacyBlockEncoding(159, 10), "purple_solid"),
            _ when path.Contains("magenta", StringComparison.Ordinal) ||
                   path.Contains("pink", StringComparison.Ordinal) => (new LegacyBlockEncoding(159, 6), "pink_solid"),
            _ when path.Contains("brown", StringComparison.Ordinal) ||
                   path.Contains("mud", StringComparison.Ordinal) ||
                   path.Contains("dirt", StringComparison.Ordinal) => (new LegacyBlockEncoding(159, 12), "earth"),
            _ => (new LegacyBlockEncoding(1, 0), "neutral_stone"),
        };
        return Map(
            source,
            fallback.Encoding,
            MinecraftBlockMappingQuality.VisibleFallback,
            $"fallback.semantic.{fallback.Family}",
            $"未命中显式规则；根据名称语义选择 {fallback.Family} 类旧版材质。该结果确定、可见且不会随机变色或变成萤石。");
    }

    private static MinecraftBlockMapping Similar(
        BlockState source,
        ushort id,
        byte metadata,
        string ruleId,
        string explanation) => Map(
        source,
        new LegacyBlockEncoding(id, metadata),
        MinecraftBlockMappingQuality.Similar,
        ruleId,
        explanation);

    private static MinecraftBlockMapping MapWood(
        BlockState source,
        ushort id,
        WoodFamily family,
        byte extraMetadata,
        MinecraftBlockMappingQuality quality,
        string ruleSuffix,
        string explanation) => Map(
        source,
        new LegacyBlockEncoding(id, (byte)(family.LegacySpecies | extraMetadata)),
        quality,
        quality == MinecraftBlockMappingQuality.Exact
            ? $"legacy.wood.{ruleSuffix}"
            : $"modern.wood.{ruleSuffix}",
        explanation);

    private static MinecraftBlockMapping Map(
        BlockState source,
        LegacyBlockEncoding encoding,
        MinecraftBlockMappingQuality quality,
        string ruleId,
        string explanation,
        bool allowsInvisibleTarget = false)
    {
        MinecraftRegistryResolution target = Minecraft1122VanillaBlockRegistry.Instance.ResolveLegacy(
            encoding.NumericId,
            encoding.Metadata);
        if (target.Source == MinecraftRegistryResolutionSource.VisibleUnknownPlaceholder)
        {
            throw new InvalidOperationException(
                $"降级规则 {ruleId} 产生了非法 1.12.2 编码 {encoding.NumericId}:{encoding.Metadata}。");
        }
        if (!source.IsAir && target.State.IsAir && !allowsInvisibleTarget)
        {
            throw new InvalidOperationException($"降级规则 {ruleId} 不得把非空气方块写成空气。");
        }

        return new MinecraftBlockMapping(source, target.State, encoding, quality, ruleId, explanation, allowsInvisibleTarget);
    }

    private static LegacyBlockEncoding PreserveCompatibleTargetState(BlockState source, LegacyBlockEncoding selectedEncoding)
    {
        MinecraftRegistryResolution selected = Minecraft1122VanillaBlockRegistry.Instance.ResolveLegacy(
            selectedEncoding.NumericId,
            selectedEncoding.Metadata);
        if(!LegacyRegistry.Value.ByName.TryGetValue(selected.State.Name, out IReadOnlyList<LegacyEntry>? candidates))
            return selectedEncoding;

        string targetPath = selected.State.Name[(selected.State.Name.IndexOf(':') + 1)..];
        LegacyBlockEncoding best = selectedEncoding;
        int bestScore = CompatibilityScore(source, selected.State, targetPath);
        foreach(LegacyEntry candidate in candidates)
        {
            int score = CompatibilityScore(source, candidate.State, targetPath);
            if(score <= bestScore) continue;
            bestScore = score;
            best = candidate.Encoding;
        }
        return best;
    }

    private static MinecraftBlockMapping Create(
        BlockState source,
        LegacyEntry entry,
        MinecraftBlockMappingQuality quality,
        string ruleId,
        string explanation) => new(source, entry.State, entry.Encoding, quality, ruleId, explanation);

    private static RegistryIndex BuildRegistryIndex()
    {
        Dictionary<string, LegacyEntry> exact = new(StringComparer.Ordinal);
        Dictionary<string, List<LegacyEntry>> byName = new(StringComparer.Ordinal);
        Minecraft1122VanillaBlockRegistry registry = Minecraft1122VanillaBlockRegistry.Instance;
        for (ushort id = 0; id <= 255; id++)
        {
            for (byte metadata = 0; metadata <= 15; metadata++)
            {
                if (id == 0 && metadata != 0) continue;
                MinecraftRegistryResolution resolution = registry.ResolveLegacy(id, metadata);
                if (resolution.Source == MinecraftRegistryResolutionSource.VisibleUnknownPlaceholder) continue;
                LegacyEntry entry = new(resolution.State, new LegacyBlockEncoding(id, metadata));
                exact.TryAdd(resolution.State.CanonicalKey, entry);
                if (!byName.TryGetValue(resolution.State.Name, out List<LegacyEntry>? entries))
                {
                    entries = [];
                    byName.Add(resolution.State.Name, entries);
                }
                entries.Add(entry);
            }
        }

        return new RegistryIndex(
            exact,
            byName.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<LegacyEntry>)pair.Value,
                StringComparer.Ordinal));
    }

    private static int CompatibilityScore(BlockState source, BlockState candidate, string path)
    {
        int score = 0;
        foreach ((string key, string value) in source.Properties)
        {
            if (IsNonLegacyDerivedProperty(path, key)) continue;
            if (!candidate.Properties.TryGetValue(key, out string? candidateValue)) continue;
            if (!string.Equals(value, candidateValue, StringComparison.Ordinal)) return -1;
            score += 2;
        }

        foreach ((string key, string value) in candidate.Properties)
        {
            if (IsNonLegacyDerivedProperty(path, key)) continue;
            if (source.Properties.TryGetValue(key, out string? sourceValue) &&
                string.Equals(value, sourceValue, StringComparison.Ordinal)) score++;
        }
        return score;
    }

    private static bool IsNonLegacyDerivedProperty(string path, string property)
    {
        if (property == "waterlogged") return true;
        if (property == "snowy" && path is "grass_block" or "podzol" or "mycelium") return true;
        if (property == "distance" && path.EndsWith("_leaves", StringComparison.Ordinal)) return true;
        if (property == "shape" && path.EndsWith("_stairs", StringComparison.Ordinal)) return true;
        if (property == "type" && path is "chest" or "trapped_chest" or "ender_chest") return true;
        if (property is not ("north" or "east" or "south" or "west" or "up" or "down")) return false;
        return path.EndsWith("_fence", StringComparison.Ordinal) ||
               path.EndsWith("_pane", StringComparison.Ordinal) ||
               path.EndsWith("_wall", StringComparison.Ordinal) ||
               path is "iron_bars" or "redstone_wire" or "tripwire" or "fire" or "chorus_plant";
    }

    private static SlabSubstitution SimilarSlab(string path)
    {
        return path switch
        {
            "stone_slab" => new SlabSubstitution(SlabKind.Stone, 0, true, "stone"),
            "sandstone_slab" => new SlabSubstitution(SlabKind.Stone, 1, true, "sandstone"),
            "petrified_oak_slab" => new SlabSubstitution(SlabKind.Stone, 2, true, "petrified_oak"),
            "cobblestone_slab" => new SlabSubstitution(SlabKind.Stone, 3, true, "cobblestone"),
            "brick_slab" => new SlabSubstitution(SlabKind.Stone, 4, true, "brick"),
            "stone_brick_slab" => new SlabSubstitution(SlabKind.Stone, 5, true, "stone_brick"),
            "nether_brick_slab" => new SlabSubstitution(SlabKind.Stone, 6, true, "nether_brick"),
            "quartz_slab" => new SlabSubstitution(SlabKind.Stone, 7, true, "quartz"),
            "red_sandstone_slab" => new SlabSubstitution(SlabKind.RedSandstone, 0, true, "red_sandstone"),
            "purpur_slab" => new SlabSubstitution(SlabKind.Purpur, 0, true, "purpur"),
            _ when path.Contains("red_sandstone", StringComparison.Ordinal) => new SlabSubstitution(SlabKind.RedSandstone, 0, false, "red_sandstone"),
            _ when path.Contains("sandstone", StringComparison.Ordinal) => new SlabSubstitution(SlabKind.Stone, 1, false, "sandstone"),
            _ when path.Contains("cobblestone", StringComparison.Ordinal) || path.Contains("blackstone", StringComparison.Ordinal) => new SlabSubstitution(SlabKind.Stone, 3, false, "cobblestone"),
            _ when path.Contains("stone_brick", StringComparison.Ordinal) || path.Contains("deepslate", StringComparison.Ordinal) || path.Contains("tuff", StringComparison.Ordinal) || path.Contains("end_stone", StringComparison.Ordinal) => new SlabSubstitution(SlabKind.Stone, 5, false, "stone_brick"),
            _ when path.Contains("mud_brick", StringComparison.Ordinal) || path.Contains("brick", StringComparison.Ordinal) && !path.Contains("stone_brick", StringComparison.Ordinal) => new SlabSubstitution(SlabKind.Stone, 4, false, "brick"),
            _ when path.Contains("nether", StringComparison.Ordinal) => new SlabSubstitution(SlabKind.Stone, 6, false, "nether_brick"),
            _ when path.Contains("quartz", StringComparison.Ordinal) || path.Contains("calcite", StringComparison.Ordinal) => new SlabSubstitution(SlabKind.Stone, 7, false, "quartz"),
            _ when path.Contains("purpur", StringComparison.Ordinal) || path.Contains("amethyst", StringComparison.Ordinal) => new SlabSubstitution(SlabKind.Purpur, 0, false, "purpur"),
            _ when path.Contains("copper", StringComparison.Ordinal) => new SlabSubstitution(SlabKind.Stone, 4, false, "brick"),
            _ => new SlabSubstitution(SlabKind.Stone, 0, false, "stone"),
        };
    }

    private static string SimilarStair(string path)
    {
        if (path.Contains("red_sandstone", StringComparison.Ordinal)) return "red_sandstone_stairs";
        if (path.Contains("sandstone", StringComparison.Ordinal)) return "sandstone_stairs";
        if (path.Contains("mud_brick", StringComparison.Ordinal) || path.Contains("copper", StringComparison.Ordinal)) return "brick_stairs";
        if (path.Contains("nether", StringComparison.Ordinal)) return "nether_brick_stairs";
        if (path.Contains("quartz", StringComparison.Ordinal) || path.Contains("calcite", StringComparison.Ordinal)) return "quartz_stairs";
        if (path.Contains("purpur", StringComparison.Ordinal) || path.Contains("amethyst", StringComparison.Ordinal)) return "purpur_stairs";
        if (path.Contains("stone_brick", StringComparison.Ordinal) || path.Contains("deepslate", StringComparison.Ordinal) || path.Contains("tuff", StringComparison.Ordinal) || path.Contains("end_stone", StringComparison.Ordinal)) return "stone_brick_stairs";
        if (path.Contains("brick", StringComparison.Ordinal)) return "brick_stairs";
        return "cobblestone_stairs";
    }

    private static bool TryWoodFamily(string path, out WoodFamily family)
    {
        string candidate = path.StartsWith("stripped_", StringComparison.Ordinal) ? path[9..] : path;
        if (candidate.StartsWith("oak_", StringComparison.Ordinal))
        {
            family = new WoodFamily(0, true, "橡木");
            return true;
        }
        if (candidate.StartsWith("spruce_", StringComparison.Ordinal))
        {
            family = new WoodFamily(1, true, "云杉木");
            return true;
        }
        if (candidate.StartsWith("birch_", StringComparison.Ordinal))
        {
            family = new WoodFamily(2, true, "白桦木");
            return true;
        }
        if (candidate.StartsWith("jungle_", StringComparison.Ordinal))
        {
            family = new WoodFamily(3, true, "丛林木");
            return true;
        }
        if (candidate.StartsWith("acacia_", StringComparison.Ordinal))
        {
            family = new WoodFamily(4, true, "金合欢木");
            return true;
        }
        if (candidate.StartsWith("dark_oak_", StringComparison.Ordinal))
        {
            family = new WoodFamily(5, true, "深色橡木");
            return true;
        }
        if (candidate.StartsWith("mangrove_", StringComparison.Ordinal))
        {
            family = new WoodFamily(5, false, "深色橡木");
            return true;
        }
        if (candidate.StartsWith("cherry_", StringComparison.Ordinal))
        {
            family = new WoodFamily(4, false, "金合欢木");
            return true;
        }
        if (candidate.StartsWith("pale_oak_", StringComparison.Ordinal))
        {
            family = new WoodFamily(2, false, "白桦木");
            return true;
        }
        if (candidate.StartsWith("bamboo_", StringComparison.Ordinal) || candidate == "bamboo_mosaic")
        {
            family = new WoodFamily(3, false, "丛林木");
            return true;
        }
        if (candidate.StartsWith("crimson_", StringComparison.Ordinal) || candidate.StartsWith("warped_", StringComparison.Ordinal))
        {
            family = new WoodFamily(5, false, "深色橡木");
            return true;
        }

        family = default;
        return false;
    }

    private static bool TryReadDyePrefix(string path, out byte metadata)
    {
        foreach ((string name, byte value) in DyeColors)
        {
            if (!path.StartsWith($"{name}_", StringComparison.Ordinal)) continue;
            metadata = value;
            return true;
        }
        metadata = 0;
        return false;
    }

    private static byte StairMetadata(BlockState source)
    {
        byte facing = Property(source, "facing") switch
        {
            "west" => 1,
            "south" => 2,
            "north" => 3,
            _ => 0,
        };
        return (byte)(facing | (Property(source, "half") == "top" ? 4 : 0));
    }

    private static byte FenceGateMetadata(BlockState source)
    {
        byte facing = Property(source, "facing") switch
        {
            "west" => 1,
            "north" => 2,
            "east" => 3,
            _ => 0,
        };
        if (BooleanProperty(source, "open")) facing |= 4;
        if (BooleanProperty(source, "powered")) facing |= 8;
        return facing;
    }

    private static byte DoorMetadata(BlockState source)
    {
        if (Property(source, "half") == "upper")
        {
            byte upper = 8;
            if (Property(source, "hinge") == "right") upper |= 1;
            if (BooleanProperty(source, "powered")) upper |= 2;
            return upper;
        }

        byte facing = Property(source, "facing") switch
        {
            "south" => 1,
            "west" => 2,
            "north" => 3,
            _ => 0,
        };
        if (BooleanProperty(source, "open")) facing |= 4;
        return facing;
    }

    private static byte TrapdoorMetadata(BlockState source)
    {
        byte facing = Property(source, "facing") switch
        {
            "south" => 1,
            "west" => 2,
            "east" => 3,
            _ => 0,
        };
        if (BooleanProperty(source, "open")) facing |= 4;
        if (Property(source, "half") == "top") facing |= 8;
        return facing;
    }

    private static byte ButtonMetadata(BlockState source)
    {
        byte face = Property(source, "face") switch
        {
            "ceiling" => 0,
            "floor" => 5,
            _ => Property(source, "facing") switch
            {
                "east" => 1,
                "west" => 2,
                "south" => 3,
                "north" => 4,
                _ => 1,
            },
        };
        if (BooleanProperty(source, "powered")) face |= 8;
        return face;
    }

    private static byte GlazedTerracottaFacing(BlockState source) => Property(source, "facing") switch
    {
        "west" => 1,
        "north" => 2,
        "east" => 3,
        _ => 0,
    };

    private static byte WallFacingMetadata(BlockState source) => Property(source, "facing") switch
    {
        "north" => 2,
        "south" => 3,
        "west" => 4,
        "east" => 5,
        _ => 2,
    };

    private static byte AxisBits(BlockState source) => Property(source, "axis") switch
    {
        "x" => 4,
        "z" => 8,
        "none" => 12,
        _ => 0,
    };

    private static string? Property(BlockState source, string name) =>
        source.Properties.TryGetValue(name, out string? value) ? value : null;

    private static bool BooleanProperty(BlockState source, string name) =>
        string.Equals(Property(source, name), "true", StringComparison.Ordinal);

    private static bool BooleanOrOneProperty(BlockState source, string name)
    {
        string? value = Property(source, name);
        return value is "true" or "1";
    }

    private static int IntegerProperty(BlockState source, string name, int defaultValue, int minimum, int maximum)
    {
        return int.TryParse(Property(source, name), out int value)
            ? Math.Clamp(value, minimum, maximum)
            : defaultValue;
    }

    private static bool TryGetVanillaPath(string name, out string path)
    {
        const string prefix = "minecraft:";
        if (name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length)
        {
            path = name[prefix.Length..];
            return true;
        }
        path = string.Empty;
        return false;
    }

    private static bool IsSupportedTarget(MinecraftTargetProfile target) =>
        target.RequiresLegacyNumericEncoding &&
        string.Equals(target.Id, MinecraftTargetProfile.Java1122.Id, StringComparison.Ordinal);

    private static uint StableHash(string value)
    {
        uint hash = 2_166_136_261;
        foreach (char character in value)
        {
            hash ^= character;
            hash *= 16_777_619;
        }
        return hash;
    }

    private sealed record RegistryIndex(
        IReadOnlyDictionary<string, LegacyEntry> Exact,
        IReadOnlyDictionary<string, IReadOnlyList<LegacyEntry>> ByName);

    private sealed record LegacyEntry(BlockState State, LegacyBlockEncoding Encoding);

    private readonly record struct WoodFamily(byte LegacySpecies, bool IsLegacy, string LegacyDisplayName);

    private readonly record struct SlabSubstitution(SlabKind Kind, byte Variant, bool Exact, string RuleName);

    private enum SlabKind
    {
        Stone,
        RedSandstone,
        Purpur,
    }
}
