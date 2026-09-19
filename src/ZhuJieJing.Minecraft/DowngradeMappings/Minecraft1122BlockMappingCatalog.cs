using System.Collections.ObjectModel;
using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

/// <summary>One inspectable built-in substitution shown by the desktop mapping editor.</summary>
public sealed record Minecraft1122BlockMappingDefinition(
    string SourcePattern,
    string SourceDisplay,
    BlockState TargetState,
    LegacyBlockEncoding LegacyEncoding,
    MinecraftBlockMappingQuality Quality,
    string RuleId,
    string Explanation);

/// <summary>One selectable and valid Java 1.12.2 target state.</summary>
public sealed record Minecraft1122LegacyMappingTarget(
    LegacyBlockEncoding LegacyEncoding,
    BlockState State,
    string DisplayName);

/// <summary>
/// Public, read-only mapping catalog. Runtime conversion and the mapping editor consume this same table,
/// preventing the UI from documenting replacements that the exporter does not actually use.
/// </summary>
public static class Minecraft1122BlockMappingCatalog
{
    private static readonly Lazy<IReadOnlyList<Minecraft1122LegacyMappingTarget>> Targets = new(
        BuildLegacyTargets,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<Minecraft1122BlockMappingDefinition> BuiltInMappings =>
        Minecraft1122BuiltInBlockMappingTable.Definitions;

    public static IReadOnlyList<Minecraft1122LegacyMappingTarget> LegacyTargets => Targets.Value;

    public static MinecraftBlockMapping ResolveBuiltIn(BlockState source) =>
        Minecraft1122BlockDowngradeRules.Instance.Resolve(source, MinecraftTargetProfile.Java1122);

    /// <summary>Creates an editor target with its bundled Simplified Chinese display name.</summary>
    public static Minecraft1122LegacyMappingTarget CreateLegacyTarget(
        LegacyBlockEncoding encoding,
        BlockState state) =>
        new(encoding, state, Minecraft1122BlockDisplayNames.GetDisplayName(state.Name));

    private static IReadOnlyList<Minecraft1122LegacyMappingTarget> BuildLegacyTargets()
    {
        var byState = new SortedDictionary<string, Minecraft1122LegacyMappingTarget>(StringComparer.Ordinal);
        Minecraft1122VanillaBlockRegistry registry = Minecraft1122VanillaBlockRegistry.Instance;
        for(ushort id = 0; id <= 255; id++)
        {
            for(byte metadata = 0; metadata <= 15; metadata++)
            {
                if(id == 0 && metadata != 0) continue;
                MinecraftRegistryResolution resolution = registry.ResolveLegacy(id, metadata);
                if(resolution.Source == MinecraftRegistryResolutionSource.VisibleUnknownPlaceholder) continue;
                Minecraft1122LegacyMappingTarget target = CreateLegacyTarget(
                    new LegacyBlockEncoding(id, metadata),
                    resolution.State);
                byState.TryAdd(resolution.State.CanonicalKey, target);
            }
        }
        return new ReadOnlyCollection<Minecraft1122LegacyMappingTarget>(byState.Values.ToArray());
    }
}

internal static class Minecraft1122BuiltInBlockMappingTable
{
    private static readonly IReadOnlyList<Minecraft1122BlockMappingDefinition> Table = Build();

    public static IReadOnlyList<Minecraft1122BlockMappingDefinition> Definitions => Table;

    public static bool TryResolve(BlockState source, out Minecraft1122BlockMappingDefinition definition)
    {
        foreach(Minecraft1122BlockMappingDefinition candidate in Table)
        {
            if(PatternMatches(candidate.SourcePattern, source.Name))
            {
                definition = candidate;
                return true;
            }
        }
        definition = null!;
        return false;
    }

    private static IReadOnlyList<Minecraft1122BlockMappingDefinition> Build()
    {
        var values = new List<Minecraft1122BlockMappingDefinition>();
        Add("minecraft:light", "光源方块", 0, 0, "modern.invisible_light", "保持不可见并烘焙光照，不增加灯块；1.12.2 无持续隐形光源，附近方块更新或重新光照后可能消退。", allowsAir: true);
        Add("minecraft:short_grass", "矮草", 31, 1, "modern.plant.short_grass", "替换为 1.12.2 高草。", values);
        Add("minecraft:fern", "蕨", 31, 2, "modern.plant.fern", "替换为 1.12.2 蕨。", values);
        Add("minecraft:rooted_dirt", "缠根泥土", 3, 1, "modern.soil.rooted_dirt", "替换为砂土，保留粗糙土色。", values);
        Add("minecraft:dirt_path", "土径", 208, 0, "modern.soil.dirt_path", "替换为 1.12.2 草径。", values);
        Add("minecraft:barrel", "木桶", 54, 2, "modern.workstation.barrel", "替换为箱子，保留储物方块语义。", values);
        Add("minecraft:smoker", "烟熏炉", 61, 2, "modern.workstation.smoker", "替换为熔炉。", values);
        Add("minecraft:blast_furnace", "高炉", 61, 2, "modern.workstation.blast_furnace", "替换为熔炉。", values);
        Add("minecraft:loom", "织布机", 58, 0, "modern.workstation.loom", "替换为工作台。", values);
        Add("minecraft:cartography_table", "制图台", 58, 0, "modern.workstation.cartography_table", "替换为工作台。", values);
        Add("minecraft:fletching_table", "制箭台", 58, 0, "modern.workstation.fletching_table", "替换为工作台。", values);
        Add("minecraft:smithing_table", "锻造台", 145, 0, "modern.workstation.smithing_table", "替换为铁砧。", values);
        Add("minecraft:stonecutter", "切石机", 145, 0, "modern.workstation.stonecutter", "替换为铁砧，保留低矮石工设备语义。", values);
        Add("minecraft:grindstone", "砂轮", 145, 0, "modern.workstation.grindstone", "替换为铁砧。", values);
        Add("minecraft:lectern", "讲台", 47, 0, "modern.workstation.lectern", "替换为书架。", values);
        Add("minecraft:composter", "堆肥桶", 118, 0, "modern.workstation.composter", "替换为空炼药锅，保留开口容器轮廓。", values);
        Add("minecraft:chiseled_bookshelf", "雕纹书架", 47, 0, "modern.storage.chiseled_bookshelf", "替换为书架。", values);
        Add("minecraft:decorated_pot", "饰纹陶罐", 140, 0, "modern.decorated_pot", "替换为空花盆，保留小型陶器轮廓。", values);
        Add("minecraft:beehive", "蜂箱", 170, 0, "modern.beehive", "替换为干草块，保留暖黄色条纹。", values);
        Add("minecraft:bee_nest", "蜂巢", 170, 0, "modern.bee_nest", "替换为干草块，保留暖黄色条纹。", values);
        Add("minecraft:chain", "锁链", 101, 0, "modern.metal.chain", "替换为铁栏杆，保留细金属轮廓。", values);
        Add("minecraft:iron_chain", "铁链", 101, 0, "modern.metal.chain", "替换为铁栏杆，保留细金属轮廓。", values);
        Add("minecraft:bell", "钟", 41, 0, "modern.metal.bell", "替换为金块，保留金黄色金属。", values);
        Add("minecraft:lightning_rod", "避雷针", 198, 1, "modern.metal.lightning_rod", "替换为竖直末地烛，保留细杆轮廓。", values);
        Add("minecraft:lantern", "灯笼", 198, 1, "modern.light.lantern", "替换为末地烛，保留小型光源轮廓，避免萤石大方块。", values);
        Add("minecraft:soul_lantern", "灵魂灯笼", 198, 1, "modern.light.soul_lantern", "替换为末地烛，保留小型光源轮廓，避免萤石大方块。", values);
        Add("minecraft:shroomlight", "菌光体", 169, 0, "modern.light.shroomlight", "替换为海晶灯，保留整块发光材质。", values);
        Add("minecraft:ochre_froglight", "赭黄蛙明灯", 89, 0, "modern.light.ochre_froglight", "替换为暖黄色萤石。", values);
        Add("minecraft:verdant_froglight", "青翠蛙明灯", 169, 0, "modern.light.verdant_froglight", "替换为海晶灯。", values);
        Add("minecraft:pearlescent_froglight", "珠光蛙明灯", 155, 0, "modern.light.pearlescent_froglight", "替换为石英块，优先保留珠白外观。", values);
        Add("minecraft:sea_pickle", "海泡菜", 198, 1, "modern.aquatic.sea_pickle", "替换为末地烛，保留小型竖向轮廓。", values);
        Add("minecraft:campfire", "营火", 213, 0, "modern.light.campfire", "替换为岩浆块，保留低矮暖色发光表面。", values);
        Add("minecraft:soul_campfire", "灵魂营火", 168, 2, "modern.light.soul_campfire", "替换为暗海晶石，保留冷青色低矮表面。", values);
        Add("minecraft:lodestone", "磁石", 98, 3, "modern.stone.lodestone", "替换为雕纹石砖。", values);
        Add("minecraft:crying_obsidian", "哭泣的黑曜石", 49, 0, "modern.stone.crying_obsidian", "替换为黑曜石。", values);
        Add("minecraft:polished_basalt", "磨制玄武岩", 173, 0, "modern.stone.polished_basalt", "替换为煤炭块，保留深黑体块。", values);
        Add("minecraft:smooth_basalt", "平滑玄武岩", 173, 0, "modern.stone.smooth_basalt", "替换为煤炭块，保留深黑体块。", values);
        Add("minecraft:cracked_nether_bricks", "裂纹下界砖", 112, 0, "modern.nether.cracked_bricks", "替换为下界砖。", values);
        Add("minecraft:chiseled_nether_bricks", "雕纹下界砖", 112, 0, "modern.nether.chiseled_bricks", "替换为下界砖。", values);
        Add("minecraft:quartz_bricks", "石英砖", 155, 0, "modern.quartz.bricks", "替换为石英块。", values);
        Add("minecraft:smooth_quartz", "平滑石英", 155, 0, "modern.quartz.smooth", "替换为石英块。", values);
        Add("minecraft:smooth_stone", "平滑石头", 1, 0, "modern.stone.smooth", "替换为石头。", values);
        Add("minecraft:moss_block", "苔藓块", 251, 13, "modern.plant.moss_block", "替换为绿色混凝土，保留饱和绿色实心表面。", values);
        Add("minecraft:azalea", "杜鹃丛", 18, 0, "modern.plant.azalea", "替换为橡树叶，保留灌木体积。", values);
        Add("minecraft:flowering_azalea", "盛开的杜鹃丛", 18, 0, "modern.plant.flowering_azalea", "替换为橡树叶，保留灌木体积。", values);
        Add("minecraft:flowering_azalea_leaves", "盛开的杜鹃树叶", 18, 0, "modern.plant.flowering_azalea_leaves", "替换为橡树叶。", values);
        Add("minecraft:glow_lichen", "发光地衣", 106, 0, "modern.plant.glow_lichen", "替换为藤蔓，保留贴面植物轮廓。", values);
        Add("minecraft:cave_vines", "洞穴藤蔓", 106, 0, "modern.plant.cave_vines", "替换为藤蔓。", values);
        Add("minecraft:cave_vines_plant", "洞穴藤蔓植株", 106, 0, "modern.plant.cave_vines", "替换为藤蔓。", values);
        Add("minecraft:warped_roots", "诡异菌索", 32, 0, "modern.plant.warped_roots", "替换为枯萎灌木，保留细小植株轮廓。", values);
        Add("minecraft:crimson_roots", "绯红菌索", 32, 0, "modern.plant.crimson_roots", "替换为枯萎灌木，保留细小植株轮廓。", values);
        Add("minecraft:nether_sprouts", "下界苗", 32, 0, "modern.plant.nether_sprouts", "替换为枯萎灌木。", values);
        Add("minecraft:sweet_berry_bush", "甜浆果丛", 18, 0, "modern.plant.berry_bush", "替换为橡树叶，保留灌木体积。", values);
        Add("minecraft:lily_of_the_valley", "铃兰", 38, 6, "modern.flower.lily_of_the_valley", "替换为滨菊，保留白色小花。", values);
        Add("minecraft:cornflower", "矢车菊", 38, 1, "modern.flower.cornflower", "替换为兰花，保留蓝色花朵。", values);
        Add("minecraft:wither_rose", "凋零玫瑰", 38, 7, "modern.flower.wither_rose", "替换为粉红色郁金香，保留暗色小花轮廓。", values);
        Add("minecraft:torchflower", "火把花", 38, 4, "modern.flower.torchflower", "替换为红色郁金香。", values);
        Add("minecraft:pink_petals", "粉红花簇", 171, 6, "modern.flower.pink_petals", "替换为粉色地毯，保留贴地粉色区域。", values);
        Add("minecraft:small_dripleaf", "小型垂滴叶", 31, 2, "modern.plant.small_dripleaf", "替换为蕨。", values);
        Add("minecraft:big_dripleaf", "大型垂滴叶", 175, 3, "modern.plant.big_dripleaf", "替换为大型蕨。", values);
        Add("minecraft:big_dripleaf_stem", "大型垂滴叶茎", 175, 11, "modern.plant.big_dripleaf_stem", "替换为大型蕨上半部。", values);
        Add("minecraft:spore_blossom", "孢子花", 175, 1, "modern.flower.spore_blossom", "替换为丁香，保留紫粉花色。", values);
        Add("minecraft:pitcher_plant", "瓶子草", 175, 1, "modern.flower.pitcher_plant", "替换为丁香。", values);
        Add("minecraft:turtle_egg", "海龟蛋", 171, 0, "modern.egg.turtle", "替换为白色地毯，保留贴地亮色轮廓。", values);
        Add("minecraft:sniffer_egg", "嗅探兽蛋", 159, 12, "modern.egg.sniffer", "替换为棕色陶瓦。", values);
        Add("minecraft:scaffolding", "脚手架", 85, 0, "modern.shape.scaffolding", "替换为橡木栅栏，保留通透支架轮廓。", values);
        Add("minecraft:target", "标靶", 170, 0, "modern.redstone.target", "替换为干草块，保留靶面色调。", values);
        Add("minecraft:respawn_anchor", "重生锚", 49, 0, "modern.respawn_anchor", "替换为黑曜石，保留深色高强度方块。", values);
        Add("minecraft:conduit", "潮涌核心", 169, 0, "modern.conduit", "替换为海晶灯，保留海洋光源语义。", values);
        Add("minecraft:dried_kelp_block", "干海带块", 159, 13, "modern.aquatic.dried_kelp", "替换为绿色陶瓦。", values);
        Add("minecraft:blue_ice", "蓝冰", 174, 0, "modern.ice.blue", "替换为浮冰，保留致密蓝冰外观。", values);
        Add("minecraft:tinted_glass", "遮光玻璃", 95, 7, "modern.glass.tinted", "替换为灰色染色玻璃，保留半透明深色外观。", values);
        Add("minecraft:nether_gold_ore", "下界金矿石", 14, 0, "modern.ore.nether_gold", "替换为金矿石，保留矿物类别。", values);
        Add("minecraft:suspicious_sand", "可疑的沙", 12, 0, "modern.suspicious_sand", "替换为沙子。", values);
        Add("minecraft:suspicious_gravel", "可疑的沙砾", 13, 0, "modern.suspicious_gravel", "替换为沙砾。", values);
        Add("minecraft:bubble_column", "气泡柱", 9, 0, "modern.aquatic.bubble_column", "替换为静态水，保留水柱体积。", values);
        Add("minecraft:attached_pumpkin_stem", "结果的南瓜梗", 104, 7, "modern.crop.attached_pumpkin_stem", "替换为成熟南瓜梗。", values);
        Add("minecraft:attached_melon_stem", "结果的西瓜梗", 105, 7, "modern.crop.attached_melon_stem", "替换为成熟西瓜梗。", values);
        Add("minecraft:pumpkin", "南瓜", 86, 0, "modern.pumpkin", "替换为 1.12.2 南瓜。", values);
        Add("minecraft:shulker_box", "潜影盒", 229, 1, "modern.shulker_box", "无色潜影盒替换为紫色潜影盒。", values);
        Add("minecraft:item_frame", "物品展示框", 68, 2, "modern.item_frame", "替换为墙上告示牌，保留贴墙薄片和朝向。", values);
        Add("minecraft:glow_item_frame", "荧光物品展示框", 68, 2, "modern.glow_item_frame", "替换为墙上告示牌，保留贴墙薄片和朝向。", values);
        Add("minecraft:water_cauldron", "装水的炼药锅", 118, 3, "modern.cauldron.water", "替换为装满水的炼药锅。", values);
        Add("minecraft:lava_cauldron", "装熔岩的炼药锅", 118, 3, "modern.cauldron.lava", "替换为装满的炼药锅，保留容器轮廓。", values);
        Add("minecraft:jigsaw", "拼图方块", 255, 0, "modern.technical.jigsaw", "替换为结构方块，保留地图制作工具语义。", values);
        Add("minecraft:test_block", "测试方块", 255, 0, "modern.technical.test_block", "替换为结构方块，保留地图制作工具语义。", values);
        Add("minecraft:test_instance_block", "测试实例方块", 255, 0, "modern.technical.test_instance", "替换为结构方块，保留地图制作工具语义。", values);
        Add("minecraft:soul_fire", "灵魂火", 51, 0, "modern.soul_fire", "替换为火焰，保留动态火焰轮廓。", values);
        Add("minecraft:soul_torch", "灵魂火把", 50, 5, "modern.soul_torch", "替换为落地火把。", values);
        Add("minecraft:soul_wall_torch", "墙上的灵魂火把", 50, 1, "modern.soul_wall_torch", "替换为墙上火把并保留朝向。", values);
        Add("minecraft:twisting_vines", "缠怨藤", 106, 0, "modern.vines.twisting", "替换为藤蔓。", values);
        Add("minecraft:twisting_vines_plant", "缠怨藤植株", 106, 0, "modern.vines.twisting", "替换为藤蔓。", values);
        Add("minecraft:weeping_vines", "垂泪藤", 106, 0, "modern.vines.weeping", "替换为藤蔓。", values);
        Add("minecraft:weeping_vines_plant", "垂泪藤植株", 106, 0, "modern.vines.weeping", "替换为藤蔓。", values);
        Add("minecraft:hanging_roots", "垂根", 106, 0, "modern.vines.hanging_roots", "替换为藤蔓，保留悬挂植物轮廓。", values);
        Add("minecraft:frogspawn", "青蛙卵", 111, 0, "modern.aquatic.frogspawn", "替换为睡莲，保留水面薄片轮廓。", values);
        Add("minecraft:bush", "灌木丛", 18, 0, "modern.plant.bush", "替换为橡树叶，保留灌木体积。", values);
        Add("minecraft:firefly_bush", "萤火虫灌木丛", 18, 0, "modern.plant.firefly_bush", "替换为橡树叶，保留灌木体积。", values);
        Add("minecraft:cactus_flower", "仙人掌花", 38, 7, "modern.flower.cactus", "替换为粉红色郁金香，保留粉色花冠。", values);
        Add("minecraft:closed_eyeblossom", "闭合的眼眸花", 38, 8, "modern.flower.closed_eyeblossom", "替换为滨菊，保留浅色花冠。", values);
        Add("minecraft:open_eyeblossom", "张开的眼眸花", 38, 5, "modern.flower.open_eyeblossom", "替换为橙色郁金香。", values);
        Add("minecraft:leaf_litter", "落叶", 171, 12, "modern.plant.leaf_litter", "替换为棕色地毯，保留贴地落叶层。", values);
        Add("minecraft:wildflowers", "野花", 171, 4, "modern.flower.wildflowers", "替换为黄色地毯，保留贴地花簇。", values);
        Add("minecraft:short_dry_grass", "矮枯草", 32, 0, "modern.plant.dry_grass", "替换为枯萎灌木。", values);
        Add("minecraft:tall_dry_grass", "高枯草", 175, 2, "modern.plant.tall_dry_grass", "替换为大型草。", values);
        Add("minecraft:pitcher_crop", "瓶子草植株", 175, 1, "modern.crop.pitcher", "替换为丁香，保留双格植株轮廓。", values);
        Add("minecraft:torchflower_crop", "火把花植株", 38, 4, "modern.crop.torchflower", "替换为红色郁金香。", values);
        Add("minecraft:crimson_fungus", "绯红菌", 40, 0, "modern.fungus.crimson", "替换为红色蘑菇。", values);
        Add("minecraft:warped_fungus", "诡异菌", 39, 0, "modern.fungus.warped", "替换为棕色蘑菇，保留小型菌类轮廓。", values);
        Add("minecraft:dried_ghast", "干枯恶魂", 88, 0, "modern.dried_ghast", "替换为灵魂沙，保留下界灰褐材质。", values);
        Add("minecraft:raw_iron_block", "粗铁块", 42, 0, "modern.raw_metal.iron", "替换为铁块。", values);
        Add("minecraft:raw_gold_block", "粗金块", 41, 0, "modern.raw_metal.gold", "替换为金块。", values);
        Add("minecraft:raw_copper_block", "粗铜块", 159, 1, "modern.raw_metal.copper", "替换为橙色陶瓦。", values);
        Add("minecraft:heavy_core", "沉重核心", 145, 0, "modern.heavy_core", "替换为铁砧，保留沉重金属语义。", values);
        Add("minecraft:creaking_heart", "嘎枝之心", 17, 12, "modern.creaking_heart", "替换为全树皮橡木。", values);
        Add("minecraft:resin_bricks", "树脂砖", 45, 0, "modern.resin.bricks", "替换为红砖。", values);
        Add("minecraft:resin_clump", "树脂团", 171, 1, "modern.resin.clump", "替换为橙色地毯，保留薄层形态。", values);
        Add("minecraft:*_lightning_rod", "氧化或涂蜡避雷针", 198, 1, "modern.metal.lightning_rod", "替换为末地烛，保留细杆轮廓。", values);
        Add("minecraft:*_wall_banner", "墙上的旗帜", 177, 2, "modern.banner.wall", "替换为墙上旗帜并尽量保留朝向；图案由兼容方块实体决定。", values);
        Add("minecraft:*_banner", "旗帜", 176, 0, "modern.banner.standing", "替换为落地旗帜并尽量保留旋转；图案由兼容方块实体决定。", values);
        Add("minecraft:*_bed", "彩色床", 26, 0, "modern.bed", "替换为 1.12.2 床并保留朝向、床头/床尾与占用状态。", values);
        Add("minecraft:*_candle_cake", "插蜡烛的蛋糕", 92, 0, "modern.cake.candle", "替换为蛋糕，避免整块变成随机颜色。", values);
        Add("minecraft:candle_cake", "插蜡烛的蛋糕", 92, 0, "modern.cake.candle", "替换为蛋糕。", values);
        Add("minecraft:*_candle", "彩色蜡烛", 50, 5, "modern.light.candle", "替换为火把，保留小型光源语义。", values);
        Add("minecraft:candle", "蜡烛", 50, 5, "modern.light.candle", "替换为火把，保留小型光源语义。", values);
        Add("minecraft:potted_*", "盆栽", 140, 0, "modern.flower_pot", "替换为空花盆，保留盆栽占地与轮廓。", values);
        Add("minecraft:*_shelf", "木质搁板", 47, 0, "modern.shelf", "替换为书架，保留木质收纳墙面。", values);
        Add("minecraft:*_wall_head", "墙上的头颅", 144, 2, "modern.head.wall", "替换为墙上头颅并尽量保留朝向。", values);
        Add("minecraft:*_wall_skull", "墙上的头颅", 144, 2, "modern.head.wall", "替换为墙上头颅并尽量保留朝向。", values);
        Add("minecraft:*_head", "头颅", 144, 1, "modern.head.floor", "替换为落地头颅。", values);
        Add("minecraft:*_skull", "头颅", 144, 1, "modern.head.floor", "替换为落地头颅。", values);
        Add("minecraft:dead_tube_coral_block", "失活的管珊瑚块", 159, 8, "modern.coral.dead_block", "替换为浅灰色陶瓦。", values);
        Add("minecraft:dead_brain_coral_block", "失活的脑纹珊瑚块", 159, 7, "modern.coral.dead_block", "替换为灰色陶瓦。", values);
        Add("minecraft:dead_*_coral_block", "失活的珊瑚块", 159, 7, "modern.coral.dead_block", "替换为灰色陶瓦。", values);
        Add("minecraft:tube_coral_block", "管珊瑚块", 251, 11, "modern.coral.blue_block", "替换为蓝色混凝土。", values);
        Add("minecraft:brain_coral_block", "脑纹珊瑚块", 251, 6, "modern.coral.pink_block", "替换为粉色混凝土。", values);
        Add("minecraft:bubble_coral_block", "气泡珊瑚块", 251, 2, "modern.coral.magenta_block", "替换为品红色混凝土。", values);
        Add("minecraft:fire_coral_block", "火珊瑚块", 251, 14, "modern.coral.red_block", "替换为红色混凝土。", values);
        Add("minecraft:horn_coral_block", "鹿角珊瑚块", 251, 4, "modern.coral.yellow_block", "替换为黄色混凝土。", values);
        Add("minecraft:dead_*_coral_fan", "失活的珊瑚扇", 171, 7, "modern.coral.dead_fan", "替换为灰色地毯。", values);
        Add("minecraft:tube_coral_fan", "管珊瑚扇", 171, 11, "modern.coral.blue_fan", "替换为蓝色地毯。", values);
        Add("minecraft:brain_coral_fan", "脑纹珊瑚扇", 171, 6, "modern.coral.pink_fan", "替换为粉色地毯。", values);
        Add("minecraft:bubble_coral_fan", "气泡珊瑚扇", 171, 2, "modern.coral.magenta_fan", "替换为品红色地毯。", values);
        Add("minecraft:fire_coral_fan", "火珊瑚扇", 171, 14, "modern.coral.red_fan", "替换为红色地毯。", values);
        Add("minecraft:horn_coral_fan", "鹿角珊瑚扇", 171, 4, "modern.coral.yellow_fan", "替换为黄色地毯。", values);
        Add("minecraft:dead_*_coral_wall_fan", "墙上失活的珊瑚扇", 160, 7, "modern.coral.dead_wall_fan", "替换为灰色玻璃板，保留墙面薄片感。", values);
        Add("minecraft:tube_coral_wall_fan", "墙上的管珊瑚扇", 160, 11, "modern.coral.blue_wall_fan", "替换为蓝色玻璃板。", values);
        Add("minecraft:brain_coral_wall_fan", "墙上的脑纹珊瑚扇", 160, 6, "modern.coral.pink_wall_fan", "替换为粉色玻璃板。", values);
        Add("minecraft:bubble_coral_wall_fan", "墙上的气泡珊瑚扇", 160, 2, "modern.coral.magenta_wall_fan", "替换为品红色玻璃板。", values);
        Add("minecraft:fire_coral_wall_fan", "墙上的火珊瑚扇", 160, 14, "modern.coral.red_wall_fan", "替换为红色玻璃板。", values);
        Add("minecraft:horn_coral_wall_fan", "墙上的鹿角珊瑚扇", 160, 4, "modern.coral.yellow_wall_fan", "替换为黄色玻璃板。", values);
        Add("minecraft:dead_*_coral", "失活的珊瑚", 35, 7, "modern.coral.dead_plant", "替换为灰色羊毛。", values);
        Add("minecraft:tube_coral", "管珊瑚", 35, 11, "modern.coral.blue_plant", "替换为蓝色羊毛。", values);
        Add("minecraft:brain_coral", "脑纹珊瑚", 35, 6, "modern.coral.pink_plant", "替换为粉色羊毛。", values);
        Add("minecraft:bubble_coral", "气泡珊瑚", 35, 2, "modern.coral.magenta_plant", "替换为品红色羊毛。", values);
        Add("minecraft:fire_coral", "火珊瑚", 35, 14, "modern.coral.red_plant", "替换为红色羊毛。", values);
        Add("minecraft:horn_coral", "鹿角珊瑚", 35, 4, "modern.coral.yellow_plant", "替换为黄色羊毛。", values);
        Add("minecraft:*_coral_block", "其他珊瑚块", 159, 9, "modern.coral.block", "替换为青色陶瓦。", values);
        Add("minecraft:*_coral_fan", "其他珊瑚扇", 171, 9, "modern.coral.fan", "替换为青色地毯。", values);
        Add("minecraft:*_coral", "其他珊瑚", 35, 9, "modern.coral.plant", "替换为青色羊毛。", values);
        return new ReadOnlyCollection<Minecraft1122BlockMappingDefinition>(values);

        void Add(string source, string display, ushort id, byte data, string ruleId, string explanation, List<Minecraft1122BlockMappingDefinition>? list = null, bool allowsAir = false)
        {
            list ??= values;
            MinecraftRegistryResolution target = Minecraft1122VanillaBlockRegistry.Instance.ResolveLegacy(id, data);
            if(target.Source == MinecraftRegistryResolutionSource.VisibleUnknownPlaceholder)
                throw new InvalidDataException($"内置映射 {source} 指向无效的 1.12.2 编码 {id}:{data}。");
            if(id == 0 && !allowsAir) throw new InvalidDataException($"内置映射 {source} 不得透明消失。");
            list.Add(new Minecraft1122BlockMappingDefinition(
                source,
                display,
                target.State,
                new LegacyBlockEncoding(id, data),
                MinecraftBlockMappingQuality.Similar,
                ruleId,
                explanation));
        }
    }

    private static bool PatternMatches(string pattern, string value)
    {
        int wildcard = pattern.IndexOf('*');
        if(wildcard < 0) return string.Equals(pattern, value, StringComparison.Ordinal);
        return value.StartsWith(pattern[..wildcard], StringComparison.Ordinal) &&
               value.EndsWith(pattern[(wildcard + 1)..], StringComparison.Ordinal);
    }
}
