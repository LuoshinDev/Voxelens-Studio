using ZhuJieJing.Core;
using ZhuJieJing.Core.Components;

namespace ZhuJieJing.App.Components;

internal static class ComponentAuthoringPrompt
{
    public static ZjjVisualProfile DefaultVisualProfile { get; } = new()
    {
        Edition = "java", VersionName = "1.21.10", DataVersion = 4556,
        StorageFamily = ZjjVisualProfile.ModernSectionPaletteStorageFamily,
    };

    public static string Create(ZjjVisualProfile? profile = null, string? dimension = null, string? pendingDirectory = null)
    {
        ZjjPlan example = CreateExamplePlan(profile, dimension);
        pendingDirectory ??= ComponentLibraryStore.CreateDefault().PendingDirectory;
        return $$"""
            请为我制作可复用的 Minecraft 建筑组件候选，具体造型由我随后补充。

            交付与审核：
            - 每一种素材、每一个候选方案分别输出一个独立 .zz 文件；不要拼成整张地图。
            - 使用无 BOM 的 UTF-8 JSON，format 为 zhujie.plan/2，明确写 base: null。组件局部原点建议为 (0,0,0)，spawnPoint 为 null。
            - visualProfile 必须与下方示例一致，使用 Java {{example.VisualProfile!.VersionName}} 对应的方块与完整状态。若我要另一版本，请先调整这四个字段并说明版本。
            - 只允许 fillBox / columns 操作；引用有效的 selections、materials，坐标范围为 min / maxExclusive，X 向东、Y 向上、Z 向南。
            - 建议单件范围在 64×64×64 以内、少于 32,768 个可见方块。硬上限：文件 8 MiB、{{ComponentPlanBuilder.MaximumComponentBlocks:N0}} 个非空气方块、{{ComponentPlanBuilder.MaximumCandidateWrites:N0}} 次尝试写入、10,000 个操作；超限请拆分。范围建议不是必须填满。
            - 保留材料和 facing / axis / half / shape / type / rotation / open 等完整属性；门、床、双高植物等应保持必要的成对方块。只制作可见方块组件，不附带脚本、NBT、实体或运行命令。
            - 不直接写入 MCA、MCC、NBT、level.dat、.zjjscene 或任何 Minecraft 世界；不直接写组件库 approved 目录，不调用施工发布来替代候选审核。
            - 所有新组件、新方案和修改版本都保存到待批准目录：{{pendingDirectory}} 。每个候选放一个直系普通 .zz 文件，使用能辨识用途的独立文件名；不覆盖旧候选。先写 .tmp，完整写完后再重命名为 .zz，界面会自动列出。
            - 即使在现有 AI 工程中制作，独立组件也统一放入上述待批准目录，不再只放在工程 .zhujie/staging；候选本身不发布为 revision，不自行创建批准记录。
            - 完成后给出文件路径、用途、尺寸、方块数、版本和需要我重点检查的细节。由我在筑界镜“建筑组件库 → 待批准”直接选择，在原生 3D 窗口检查后，亲自勾选并点击“批准并移入已批准”，才成为批准组件。AI 不得代替我批准。

            格式与审核以 docs/COMPONENT_LIBRARY.md 的独立组件规则为准，导入时由筑界镜 Core 严格解析与编译。docs/AI_WORLD_FORMAT.md 可用于查询通用方块状态、坐标与操作说明；其中工程增量发布所需的 base revision 和工程 schema 不适用于候选，独立候选始终明确写 base: null。只有批准组件复用到工程时，才由工作区生成带当前 revision 的放置草稿。
            示例是 3×1×3 石砖底座，只示范格式，请根据我要的素材替换内容：

            ```json
            {{ZjjPlanJson.Serialize(example)}}
            ```
            """;
    }

    internal static ZjjPlan CreateExamplePlan(ZjjVisualProfile? profile = null, string? dimension = null)
    {
        BoxSelection bounds = BoxSelection.FromMinAndSize(new(0, 0, 0), 3, 1, 3);
        return new ZjjPlan
        {
            PlanId = "component-candidate-example", Base = null, SpawnPoint = null,
            Dimension = dimension ?? "minecraft:overworld", VisualProfile = profile ?? DefaultVisualProfile,
            Module = new() { Id = "component", DisplayName = "组件候选", Bounds = bounds },
            Selections = new() { ["component"] = bounds },
            Materials = new() { ["stone"] = MaterialIntent.Exact(new("minecraft:stone_bricks")) },
            Operations = [new FillBoxOperation { Id = "base", SelectionId = "component", MaterialId = "stone", Bounds = bounds }],
        };
    }
}
