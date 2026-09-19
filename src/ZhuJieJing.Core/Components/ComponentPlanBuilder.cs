namespace ZhuJieJing.Core.Components;

/// <summary>Creates self-contained components from compiled non-air content; placement never erases surrounding blocks.</summary>
public static class ComponentPlanBuilder
{
    public const int MaximumComponentBlocks = 131_072;
    public const long MaximumCandidateWrites = 1_048_576;

    public static CompilationResult CompileCandidate(ZjjPlan plan, CancellationToken cancellationToken = default)
    {
        ZjjPlanValidator.EnsureValid(plan);
        if(plan.Base is not null) throw new InvalidDataException("组件候选必须是独立蓝图（base 为 null），不能依赖当前工程 revision。");
        long writes = 0;
        foreach(PlanOperation operation in plan.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writes = checked(writes + (operation switch
            {
                FillBoxOperation fill => fill.Bounds.Volume,
                ColumnsOperation columns => checked((long)columns.Height * columns.Origins.Count),
                _ => throw new InvalidDataException("组件包含不支持的施工操作。"),
            }));
            if(writes > MaximumCandidateWrites) throw new InvalidDataException($"单个组件候选最多允许 {MaximumCandidateWrites:N0} 次方块写入，请拆分为较小组件。");
        }
        CompilationResult result = new ZjjPlanCompiler().Compile(plan, new EmptySceneSnapshot(), cancellationToken);
        int count = result.Delta.Sections.Sum(section => section.Changes.Count(change => !change.After.IsAir));
        if(count == 0) throw new InvalidDataException("组件没有可见方块，不能保存为空组件。");
        if(count > MaximumComponentBlocks) throw new InvalidDataException($"单个组件最多包含 {MaximumComponentBlocks:N0} 个方块，请拆分后导入。");
        return result;
    }

    public static BoxSelection OccupiedBounds(SceneDelta delta)
    {
        BlockPosition[] points = delta.Sections.SelectMany(section => section.Changes
            .Where(change => !change.After.IsAir).Select(change => section.Section.ToBlockPosition(change.LocalIndex))).ToArray();
        if(points.Length == 0) throw new InvalidDataException("组件没有可见方块。");
        return new BoxSelection(new(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z)),
            new(checked(points.Max(p => p.X) + 1), checked(points.Max(p => p.Y) + 1), checked(points.Max(p => p.Z) + 1)));
    }

    public static ZjjPlan CreatePlacement(ZjjPlan source, SceneDelta delta, BlockPosition anchor, int quarterTurns,
        string? dimension = null, CancellationToken cancellationToken = default)
    {
        if(quarterTurns is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(quarterTurns));
        BoxSelection sourceBounds = OccupiedBounds(delta);
        int width = checked(sourceBounds.MaxExclusive.X - sourceBounds.Min.X);
        int depth = checked(sourceBounds.MaxExclusive.Z - sourceBounds.Min.Z);
        var groups = new SortedDictionary<string, (BlockState State, List<BlockPosition> Positions)>(StringComparer.Ordinal);
        foreach(SectionDelta section in delta.Sections)
        foreach(VoxelChange change in section.Changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(change.After.IsAir) continue;
            BlockPosition original = section.Section.ToBlockPosition(change.LocalIndex);
            int x = checked(original.X - sourceBounds.Min.X);
            int z = checked(original.Z - sourceBounds.Min.Z);
            (int rotatedX, int rotatedZ) = quarterTurns switch
            {
                1 => (depth - 1 - z, x),
                2 => (width - 1 - x, depth - 1 - z),
                3 => (z, width - 1 - x),
                _ => (x, z),
            };
            BlockPosition placed = anchor.Offset(rotatedX, checked(original.Y - sourceBounds.Min.Y), rotatedZ);
            BlockState state = RotateBlockState(change.After, quarterTurns);
            if(!groups.TryGetValue(state.CanonicalKey, out var group))
            {
                group = (state, []);
                groups.Add(state.CanonicalKey, group);
            }
            group.Positions.Add(placed);
        }
        BoxSelection bounds = BoxSelection.FromMinAndSize(anchor,
            quarterTurns % 2 == 0 ? width : depth,
            checked(sourceBounds.MaxExclusive.Y - sourceBounds.Min.Y),
            quarterTurns % 2 == 0 ? depth : width);
        ZjjPlan result = new()
        {
            PlanId = "component-placement",
            Base = null,
            Dimension = dimension ?? source.Dimension,
            VisualProfile = source.VisualProfile,
            SpawnPoint = null,
            Module = new() { Id = "component", DisplayName = "建筑组件", Bounds = bounds },
            Selections = new(StringComparer.Ordinal) { ["component"] = bounds },
        };
        int materialIndex = 0;
        int operationIndex = 0;
        foreach(var group in groups.Values)
        {
            string materialId = $"m{materialIndex++}";
            result.Materials.Add(materialId, MaterialIntent.Exact(group.State));
            foreach(BlockPosition[] positions in group.Positions.Order().Chunk(20_000))
                result.Operations.Add(new ColumnsOperation
                {
                    Id = $"place-{operationIndex++}", SelectionId = "component", MaterialId = materialId,
                    Height = 1, Origins = [.. positions], WriteStrategy = WriteStrategy.FailOnConflict,
                });
        }
        ZjjPlanValidator.EnsureValid(result);
        return result;
    }

    public static BlockState RotateBlockState(BlockState source, int quarterTurns)
    {
        if(quarterTurns is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(quarterTurns));
        if(quarterTurns == 0) return source;
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach(var property in source.Properties)
        {
            string key = RotateDirection(property.Key, quarterTurns);
            string value = property.Value;
            if(property.Key is "facing" or "horizontal_facing") value = RotateDirection(value, quarterTurns);
            else if(property.Key == "axis" && quarterTurns % 2 != 0) value = value switch { "x" => "z", "z" => "x", _ => value };
            else if(property.Key == "rotation" && int.TryParse(value, out int rotation) && rotation is >= 0 and < 16)
                value = ((rotation + quarterTurns * 4) % 16).ToString(System.Globalization.CultureInfo.InvariantCulture);
            else if(property.Key == "shape" && source.Name.Contains("rail", StringComparison.Ordinal))
                value = RotateRail(value, quarterTurns);
            else if(property.Key == "orientation")
                value = string.Join('_', value.Split('_').Select(part => RotateDirection(part, quarterTurns)));
            properties.Add(key, value);
        }
        return new BlockState(source.Name, properties);
    }

    private static string RotateDirection(string value, int quarterTurns)
    {
        string[] directions = ["north", "east", "south", "west"];
        int index = Array.IndexOf(directions, value);
        return index < 0 ? value : directions[(index + quarterTurns) % 4];
    }

    private static string RotateRail(string value, int quarterTurns)
    {
        if(value.StartsWith("ascending_", StringComparison.Ordinal)) return "ascending_" + RotateDirection(value[10..], quarterTurns);
        string[] parts = value.Split('_');
        if(parts.Length != 2) return value;
        HashSet<string> directions = parts.Select(part => RotateDirection(part, quarterTurns)).ToHashSet(StringComparer.Ordinal);
        if(directions.SetEquals(["north", "south"])) return "north_south";
        if(directions.SetEquals(["east", "west"])) return "east_west";
        foreach(string canonical in new[] { "south_east", "south_west", "north_west", "north_east" })
            if(directions.SetEquals(canonical.Split('_'))) return canonical;
        return value;
    }
}
