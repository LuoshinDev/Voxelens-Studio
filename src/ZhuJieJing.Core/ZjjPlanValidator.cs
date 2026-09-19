using System.Text.RegularExpressions;

namespace ZhuJieJing.Core;

public sealed record ValidationIssue(string Code, string Path, string Message);

public sealed class PlanValidationResult
{
    public PlanValidationResult(IEnumerable<ValidationIssue> issues)
    {
        Issues = issues.ToArray();
    }

    public IReadOnlyList<ValidationIssue> Issues { get; }

    public bool IsValid => Issues.Count == 0;
}

public sealed class PlanValidationException : Exception
{
    public PlanValidationException(PlanValidationResult result)
        : base(string.Join(Environment.NewLine, result.Issues.Select(issue => $"{issue.Path}: {issue.Message}")))
    {
        Result = result;
    }

    public PlanValidationResult Result { get; }
}

public static partial class ZjjPlanValidator
{
    public const int MaximumSelections = 10_000;
    public const int MaximumMaterials = 10_000;
    public const int MaximumOperations = 10_000;
    public const int MaximumColumnOrigins = 200_000;
    public const int MaximumAttemptedWrites = 16_777_216;

    private static readonly Regex NamespacedIdPattern = new("^[a-z0-9_.-]+:[a-z0-9_./-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex PropertyPattern = new("^[a-z0-9_./-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex ModuleIdPattern = new("^[a-z0-9][a-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> VisualStorageFamilies = new(StringComparer.Ordinal)
    {
        ZjjVisualProfile.UnknownStorageFamily,
        ZjjVisualProfile.LegacyNumericAnvilStorageFamily,
        ZjjVisualProfile.FlattenedPaletteStorageFamily,
        ZjjVisualProfile.ModernSectionPaletteStorageFamily,
    };

    public static PlanValidationResult Validate(ZjjPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var issues = new List<ValidationIssue>();

        if (!string.Equals(plan.Format, ZjjPlan.CurrentFormat, StringComparison.Ordinal))
        {
            issues.Add(new ValidationIssue("plan.format", "format", $"仅支持 {ZjjPlan.CurrentFormat}。"));
        }

        if (string.IsNullOrWhiteSpace(plan.PlanId)) issues.Add(new ValidationIssue("plan.id", "planId", "PlanId 不能为空。"));
        if (!IsNamespacedId(plan.Dimension)) issues.Add(new ValidationIssue("plan.dimension", "dimension", "维度必须是命名空间 ID。"));
        ValidateVisualProfile(plan.VisualProfile, issues);
        ValidateModule(plan.Module, "module", issues);

        if (plan.Base is not null)
        {
            if (string.IsNullOrWhiteSpace(plan.Base.Scene))
                issues.Add(new ValidationIssue("base.scene", "base.scene", "基础场景不能为空。"));
            if (string.IsNullOrWhiteSpace(plan.Base.Revision))
                issues.Add(new ValidationIssue("base.revision", "base.revision", "基础 revision 不能为空。"));
        }

        if (plan.CoordinateSystem is null)
        {
            issues.Add(new ValidationIssue("coordinate.missing", "coordinateSystem", "坐标系不能为空。"));
        }
        else if (!string.Equals(plan.CoordinateSystem.Unit, ZjjCoordinateSystem.MinecraftUnit, StringComparison.Ordinal) ||
                 !string.Equals(plan.CoordinateSystem.Axes, ZjjCoordinateSystem.MinecraftAxes, StringComparison.Ordinal))
        {
            issues.Add(new ValidationIssue("coordinate.unsupported", "coordinateSystem", "坐标系必须为 block / x-east,y-up,z-south。"));
        }

        ValidateSelections(plan, issues);
        ValidateMaterials(plan, issues);
        ValidateOperations(plan, issues);
        return new PlanValidationResult(issues);
    }

    public static void ValidateModule(ZjjPlanModule? module, string path, List<ValidationIssue> issues)
    {
        if(module is null)
        {
            issues.Add(new ValidationIssue("plan.module", path, "施工批次必须指定所属模块。"));
            return;
        }
        if(string.IsNullOrWhiteSpace(module.Id) || !ModuleIdPattern.IsMatch(module.Id))
            issues.Add(new ValidationIssue("plan.module_id", $"{path}.id", "模块 ID 必须是 1–64 位小写字母、数字、点、短横线或下划线。"));
        if(string.IsNullOrWhiteSpace(module.DisplayName) || module.DisplayName.Length > 128)
            issues.Add(new ValidationIssue("plan.module_name", $"{path}.displayName", "模块名称必须为 1–128 个字符。"));
        if(module.Kind == ZjjPlanModuleKind.Region && (module.RegionX is null || module.RegionZ is null))
            issues.Add(new ValidationIssue("plan.module_region", path, "Region 模块必须同时指定 regionX 与 regionZ。"));
        if(module.Kind != ZjjPlanModuleKind.Region && (module.RegionX is not null || module.RegionZ is not null))
            issues.Add(new ValidationIssue("plan.module_region", path, "只有 Region 模块可以指定 regionX 与 regionZ。"));
    }

    private static void ValidateVisualProfile(ZjjVisualProfile? profile, List<ValidationIssue> issues)
    {
        if(profile is null) return;
        if(!string.Equals(profile.Edition, ZjjVisualProfile.JavaEdition, StringComparison.Ordinal))
            issues.Add(new ValidationIssue("visual_profile.edition", "visualProfile.edition", "视觉配置目前只支持 Minecraft Java 版。"));
        if(profile.VersionName is not null &&
           (string.IsNullOrWhiteSpace(profile.VersionName) || profile.VersionName.Length > 128 ||
            profile.VersionName.Any(char.IsControl)))
        {
            issues.Add(new ValidationIssue(
                "visual_profile.version_name",
                "visualProfile.versionName",
                "材质版本名称必须是 1–128 个非控制字符，或为 null。"));
        }
        if(profile.DataVersion is < 0)
            issues.Add(new ValidationIssue("visual_profile.data_version", "visualProfile.dataVersion", "DataVersion 不能为负数。"));
        if(string.IsNullOrWhiteSpace(profile.StorageFamily) || !VisualStorageFamilies.Contains(profile.StorageFamily))
        {
            issues.Add(new ValidationIssue(
                "visual_profile.storage_family",
                "visualProfile.storageFamily",
                "存储族必须是 unknown、legacyNumericAnvil、flattenedPalette 或 modernSectionPalette。"));
        }
        if(profile.VersionName is null && profile.DataVersion is null &&
           string.Equals(profile.StorageFamily, ZjjVisualProfile.UnknownStorageFamily, StringComparison.Ordinal))
        {
            issues.Add(new ValidationIssue(
                "visual_profile.identity",
                "visualProfile",
                "视觉配置必须至少提供版本名称、DataVersion 或已知存储族。"));
        }
    }

    public static void EnsureValid(ZjjPlan plan)
    {
        var result = Validate(plan);
        if (!result.IsValid) throw new PlanValidationException(result);
    }

    private static void ValidateSelections(ZjjPlan plan, List<ValidationIssue> issues)
    {
        if (plan.Selections is null)
        {
            issues.Add(new ValidationIssue("selection.missing", "selections", "选择区集合不能为空。"));
            return;
        }
        if (plan.Selections.Count > MaximumSelections)
        {
            issues.Add(new ValidationIssue("selection.limit", "selections", $"选择区不能超过 {MaximumSelections:N0} 个。"));
        }

        foreach (var selection in plan.Selections)
        {
            var path = $"selections.{selection.Key}";
            if (string.IsNullOrWhiteSpace(selection.Key)) issues.Add(new ValidationIssue("selection.id", path, "选择区 ID 不能为空。"));
            if (!IsValid(selection.Value)) issues.Add(new ValidationIssue("selection.bounds", path, "选择区必须在三个轴上具有正尺寸。"));
        }
    }

    private static void ValidateMaterials(ZjjPlan plan, List<ValidationIssue> issues)
    {
        if (plan.Materials is null)
        {
            issues.Add(new ValidationIssue("material.missing", "materials", "材质集合不能为空。"));
            return;
        }
        if (plan.Materials.Count > MaximumMaterials)
        {
            issues.Add(new ValidationIssue("material.limit", "materials", $"材质不能超过 {MaximumMaterials:N0} 个。"));
        }

        foreach (var material in plan.Materials)
        {
            var path = $"materials.{material.Key}";
            if (string.IsNullOrWhiteSpace(material.Key)) issues.Add(new ValidationIssue("material.id", path, "材质 ID 不能为空。"));
            if (material.Value is null)
            {
                issues.Add(new ValidationIssue("material.null", path, "材质不能为空。"));
                continue;
            }

            if (material.Value.ExactState is not null) ValidateBlockState(material.Value.ExactState, $"{path}.exactState", issues);
            if (material.Value.Candidates is null)
            {
                issues.Add(new ValidationIssue("material.candidates", $"{path}.candidates", "候选方块集合不能为空。"));
            }
            else
            {
                for (var index = 0; index < material.Value.Candidates.Count; index++)
                {
                    BlockState? candidate = material.Value.Candidates[index];
                    if (candidate is null)
                        issues.Add(new ValidationIssue("material.candidate_null", $"{path}.candidates[{index}]", "候选方块不能为空。"));
                    else
                        ValidateBlockState(candidate, $"{path}.candidates[{index}]", issues);
                }
            }
            if (material.Value.Traits is null)
            {
                issues.Add(new ValidationIssue("material.traits", $"{path}.traits", "材质 traits 不能为空。"));
            }

            BlockState? resolved = ResolveSafely(material.Value);
            if (material.Value.ExactState?.IsAir == true && !material.Value.AllowAir)
                issues.Add(new ValidationIssue("material.air_not_allowed", path, "非空气材质不能解析为空气；删除操作必须显式启用 AllowAir。"));
            else if (resolved is null)
            {
                issues.Add(new ValidationIssue("material.unresolved", path, "材质没有可用方块，编译器不会静默使用空气兜底。"));
            }
        }
    }

    private static void ValidateOperations(ZjjPlan plan, List<ValidationIssue> issues)
    {
        if (plan.Operations is null)
        {
            issues.Add(new ValidationIssue("operation.missing", "operations", "操作集合不能为空。"));
            return;
        }
        if (plan.Operations.Count > MaximumOperations)
        {
            issues.Add(new ValidationIssue("operation.limit", "operations", $"操作不能超过 {MaximumOperations:N0} 个。"));
        }

        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        long attemptedWrites = 0;
        for (var index = 0; index < plan.Operations.Count; index++)
        {
            var operation = plan.Operations[index];
            var path = $"operations[{index}]";
            if (operation is null)
            {
                issues.Add(new ValidationIssue("operation.null", path, "操作不能为空。"));
                continue;
            }

            if (string.IsNullOrWhiteSpace(operation.Id)) issues.Add(new ValidationIssue("operation.id", $"{path}.id", "操作 ID 不能为空。"));
            else if (!operationIds.Add(operation.Id)) issues.Add(new ValidationIssue("operation.duplicate_id", $"{path}.id", $"操作 ID {operation.Id} 重复。"));

            BoxSelection selection = default;
            bool hasSelection = !string.IsNullOrWhiteSpace(operation.SelectionId) &&
                                plan.Selections is not null &&
                                plan.Selections.TryGetValue(operation.SelectionId, out selection);
            if (!hasSelection) issues.Add(new ValidationIssue("operation.selection", $"{path}.selectionId", $"选择区 {operation.SelectionId} 不存在。"));
            if (string.IsNullOrWhiteSpace(operation.MaterialId) ||
                plan.Materials is null ||
                !plan.Materials.ContainsKey(operation.MaterialId))
            {
                issues.Add(new ValidationIssue("operation.material", $"{path}.materialId", $"材质 {operation.MaterialId} 不存在。"));
            }
            if (operation.WriteStrategy == WriteStrategy.ReplaceMatching && operation.MatchState is null)
            {
                issues.Add(new ValidationIssue("operation.match_state", $"{path}.matchState", "ReplaceMatching 必须指定 MatchState。"));
            }

            if (operation.MatchState is not null) ValidateBlockState(operation.MatchState, $"{path}.matchState", issues);
            attemptedWrites = SaturatingAdd(attemptedWrites, EstimateWrites(operation));
            if (!hasSelection) continue;

            switch (operation)
            {
                case FillBoxOperation fillBox when !IsValid(fillBox.Bounds):
                    issues.Add(new ValidationIssue("operation.bounds", $"{path}.bounds", "FillBox 必须在三个轴上具有正尺寸。"));
                    break;
                case FillBoxOperation fillBox when !selection.Contains(fillBox.Bounds):
                    issues.Add(new ValidationIssue("operation.outside_selection", $"{path}.bounds", "FillBox 写入范围超出选择区。"));
                    break;
                case ColumnsOperation columns:
                    ValidateColumns(columns, selection, path, issues);
                    break;
            }
        }

        if (attemptedWrites > MaximumAttemptedWrites)
        {
            issues.Add(new ValidationIssue(
                "plan.write_budget",
                "operations",
                $"蓝图最多可尝试写入 {MaximumAttemptedWrites:N0} 个方块，本次声明约 {attemptedWrites:N0} 个。请拆成多个可审查变更。"));
        }
    }

    private static void ValidateColumns(ColumnsOperation columns, BoxSelection selection, string path, List<ValidationIssue> issues)
    {
        if (columns.Height <= 0)
        {
            issues.Add(new ValidationIssue("columns.height", $"{path}.height", "柱高必须为正数。"));
            return;
        }

        if (columns.Origins is null)
        {
            issues.Add(new ValidationIssue("columns.origins", $"{path}.origins", "Columns 起点集合不能为空。"));
            return;
        }
        if (columns.Origins.Count == 0) issues.Add(new ValidationIssue("columns.origins", $"{path}.origins", "Columns 至少需要一个起点。"));
        if (columns.Origins.Count > MaximumColumnOrigins)
        {
            issues.Add(new ValidationIssue("columns.origin_limit", $"{path}.origins", $"单次 Columns 不能超过 {MaximumColumnOrigins:N0} 个起点。"));
        }
        for (var index = 0; index < columns.Origins.Count; index++)
        {
            var origin = columns.Origins[index];
            var topY = origin.Y + (long)columns.Height - 1;
            if (!selection.Contains(origin) || topY >= selection.MaxExclusive.Y)
            {
                issues.Add(new ValidationIssue("operation.outside_selection", $"{path}.origins[{index}]", "柱体写入范围超出选择区。"));
            }
        }
    }

    private static void ValidateBlockState(BlockState state, string path, List<ValidationIssue> issues)
    {
        if (!IsNamespacedId(state.Name)) issues.Add(new ValidationIssue("block_state.name", $"{path}.name", "方块名称必须是小写命名空间 ID。"));
        foreach (var property in state.Properties)
        {
            if (!PropertyPattern.IsMatch(property.Key) || !PropertyPattern.IsMatch(property.Value))
            {
                issues.Add(new ValidationIssue("block_state.property", $"{path}.properties.{property.Key}", "方块属性必须使用稳定的小写标识。"));
            }
        }
    }

    private static bool IsNamespacedId(string value) => !string.IsNullOrWhiteSpace(value) && NamespacedIdPattern.IsMatch(value);

    private static BlockState? ResolveSafely(MaterialIntent material)
    {
        if (material.ExactState is not null) return material.ExactState.IsAir && !material.AllowAir ? null : material.ExactState;
        if (material.Candidates is null) return null;
        return material.Candidates.FirstOrDefault(candidate => candidate is not null && (material.AllowAir || !candidate.IsAir));
    }

    private static long EstimateWrites(PlanOperation operation) => operation switch
    {
        FillBoxOperation fillBox => SaturatingVolume(fillBox.Bounds),
        ColumnsOperation columns when columns.Height > 0 && columns.Origins is not null =>
            (long)columns.Origins.Count * columns.Height,
        _ => 0,
    };

    private static long SaturatingVolume(BoxSelection selection)
    {
        long x = (long)selection.MaxExclusive.X - selection.Min.X;
        long y = (long)selection.MaxExclusive.Y - selection.Min.Y;
        long z = (long)selection.MaxExclusive.Z - selection.Min.Z;
        if (x <= 0 || y <= 0 || z <= 0) return 0;
        if (x > long.MaxValue / y) return long.MaxValue;
        long xy = x * y;
        return xy > long.MaxValue / z ? long.MaxValue : xy * z;
    }

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private static bool IsValid(BoxSelection selection) =>
        selection.MaxExclusive.X > selection.Min.X &&
        selection.MaxExclusive.Y > selection.Min.Y &&
        selection.MaxExclusive.Z > selection.Min.Z;
}
