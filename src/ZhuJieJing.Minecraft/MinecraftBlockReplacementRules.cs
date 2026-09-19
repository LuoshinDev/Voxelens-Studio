using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

/// <summary>Block identity and compatible state transfer shared by the picker, counts, writer and lighting.</summary>
public static class MinecraftBlockReplacementRules
{
    private static readonly Lazy<IReadOnlyDictionary<string, Minecraft1122LegacyMappingTarget[]>> LegacyByName = new(() =>
        Minecraft1122BlockMappingCatalog.LegacyTargets.GroupBy(t => t.State.Name).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal));

    public static string Identity(MinecraftReplacementBlock block) => block.State.Name == Minecraft1122VanillaBlockRegistry.VisibleUnknownBlockName
        ? $"{block.State.Name}:{block.LegacyEncoding?.NumericId}" : block.State.Name;

    public static MinecraftReplacementBlock Representative(IEnumerable<MinecraftReplacementBlock> variants)
    {
        var blocks = variants.ToArray();
        var representative = blocks.OrderBy(b => DefaultScore(b.State)).ThenBy(b => b.State.CanonicalKey, StringComparer.Ordinal).First();
        return representative with { Count = blocks.Sum(b => b.Count), HasBlockEntity = blocks.Any(b => b.HasBlockEntity) };
    }

    internal static MinecraftReplacementBlock Resolve(MinecraftReplacementBlock source, MinecraftReplacementBlock target)
    {
        if(Identity(source) == Identity(target)) return source;
        string family = Family(source.State.Name);
        bool compatible = family.Length > 0 && family == Family(target.State.Name);
        if(!compatible) return DefaultTarget(target);

        if(source.LegacyEncoding is { } original && target.LegacyEncoding is { } chosen && family is "door" or "stairs" or "trapdoor" or "gate")
        {
            // Legacy doors split orientation/open state and hinge/power between different halves.
            // Transferring their full metadata preserves that pairing without inventing missing properties.
            var encoding = new LegacyBlockEncoding(chosen.NumericId, original.Metadata);
            var state = Minecraft1122VanillaBlockRegistry.Instance.ResolveLegacy(encoding.NumericId, encoding.Metadata);
            if(state.State.Name == target.State.Name) return target with { State = state.State, LegacyEncoding = encoding };
            throw new InvalidDataException("目标无法保留来源方块的结构状态，已停止替换。");
        }

        string[] properties = TransferProperties(family);
        if(target.LegacyEncoding is not null)
        {
            if(!LegacyByName.Value.TryGetValue(target.State.Name, out var candidates)) return DefaultTarget(target);
            var compatibleTargets = candidates.Where(candidate => properties.All(key =>
                !source.State.Properties.TryGetValue(key, out string? value) || !candidate.State.Properties.TryGetValue(key, out string? other) || value == other));
            var resolved = compatibleTargets.OrderBy(t => DefaultScore(t.State)).ThenBy(t => t.LegacyEncoding.NumericId).FirstOrDefault();
            if(resolved is null) throw new InvalidDataException($"无法保留 {source.State.Name} 的结构状态到 {target.State.Name}。");
            return target with { State = resolved.State, LegacyEncoding = resolved.LegacyEncoding };
        }

        var values = new Dictionary<string, string>(DefaultTarget(target).State.Properties, StringComparer.Ordinal);
        foreach(string key in properties)
            if(values.ContainsKey(key) && source.State.Properties.TryGetValue(key, out string? value)) values[key] = value;
        return target with { State = new BlockState(target.State.Name, values) };
    }

    public static string? StructuralError(MinecraftReplacementBlock source, MinecraftReplacementBlock target)
    {
        string family = Family(target.State.Name);
        if(family == "door" && Family(source.State.Name) != "door")
            return "门需要完整的上下两格，目前支持门与门之间替换；请为当前来源选择其他方块。";
        if(target.State.Properties.TryGetValue("half", out string? half) && half is "upper" or "lower" && family.Length == 0 && source.State.Name != target.State.Name)
            return "该目标需要多格结构，目前不支持从普通方块直接生成。";
        return null;
    }

    private static MinecraftReplacementBlock DefaultTarget(MinecraftReplacementBlock target)
    {
        if(target.LegacyEncoding is not null && LegacyByName.Value.TryGetValue(target.State.Name, out var candidates))
        {
            var best = candidates.OrderBy(c => DefaultScore(c.State)).ThenBy(c => c.LegacyEncoding.NumericId).First();
            return target with { State = best.State, LegacyEncoding = best.LegacyEncoding };
        }
        var values = new Dictionary<string, string>(target.State.Properties, StringComparer.Ordinal);
        foreach(string key in TransferProperties(Family(target.State.Name)))
            if(values.ContainsKey(key) && DefaultValue(key, Family(target.State.Name)) is { } value) values[key] = value;
        return target with { State = new BlockState(target.State.Name, values) };
    }

    private static int DefaultScore(BlockState state) => state.Properties.Sum(p =>
        DefaultValue(p.Key, Family(state.Name)) is { } preferred && p.Value != preferred ? 1 : 0);

    private static string? DefaultValue(string key, string family) => key switch
    {
        "facing" => "north", "axis" => "y", "half" => family == "door" ? "lower" : "bottom",
        "type" => family == "slab" ? "bottom" : null, "shape" => family == "stairs" ? "straight" : null,
        "hinge" => "left", "open" or "powered" or "waterlogged" or "in_wall" or "persistent" => "false",
        "north" or "south" or "east" or "west" or "up" => family == "wall" ? (key == "up" ? "true" : "none") : "false",
        "rotation" or "age" or "level" or "power" => "0", "face" => "wall",
        "lit" => family is "torch" or "wall_torch" ? "true" : "false", _ => null,
    };

    private static string Family(string name)
    {
        if(!name.StartsWith("minecraft:", StringComparison.Ordinal)) return "";
        string path = name[(name.IndexOf(':') + 1)..];
        if(path.EndsWith("_trapdoor", StringComparison.Ordinal)) return "trapdoor";
        if(path.EndsWith("_door", StringComparison.Ordinal)) return "door";
        if(path.EndsWith("_fence_gate", StringComparison.Ordinal)) return "gate";
        if(path.EndsWith("_stairs", StringComparison.Ordinal)) return "stairs";
        if(path.EndsWith("_slab", StringComparison.Ordinal)) return "slab";
        if(path.EndsWith("_log", StringComparison.Ordinal) || path.EndsWith("_wood", StringComparison.Ordinal) || path.EndsWith("_stem", StringComparison.Ordinal) && !path.Contains("pumpkin") && !path.Contains("melon") || path.EndsWith("_hyphae", StringComparison.Ordinal) || path is "bone_block" or "hay_block" or "quartz_pillar" or "purpur_pillar") return "pillar";
        if(path.EndsWith("_fence", StringComparison.Ordinal)) return "fence";
        if(path.EndsWith("_pane", StringComparison.Ordinal) || path == "iron_bars") return "pane";
        if(path.EndsWith("_wall", StringComparison.Ordinal)) return "wall";
        if(path.EndsWith("_button", StringComparison.Ordinal)) return "button";
        if(path.EndsWith("_leaves", StringComparison.Ordinal)) return "leaves";
        if(path.EndsWith("_glazed_terracotta", StringComparison.Ordinal)) return "glazed";
        if(path is "wall_torch" or "redstone_wall_torch" or "soul_wall_torch") return "wall_torch";
        if(path is "torch" or "redstone_torch" or "soul_torch") return "torch";
        return "";
    }

    private static string[] TransferProperties(string family) => family switch
    {
        "door" => ["facing", "half", "hinge", "open", "powered"],
        "stairs" => ["facing", "half", "shape", "waterlogged"],
        "slab" => ["type", "waterlogged"],
        "trapdoor" => ["facing", "half", "open", "powered", "waterlogged"],
        "gate" => ["facing", "open", "powered", "in_wall"],
        "pillar" => ["axis"],
        "fence" or "pane" => ["north", "east", "south", "west", "waterlogged"],
        "wall" => ["north", "east", "south", "west", "up", "waterlogged"],
        "button" => ["face", "facing", "powered"],
        "leaves" => ["persistent", "distance", "waterlogged"],
        "glazed" => ["facing"],
        "torch" => ["lit"],
        "wall_torch" => ["facing", "lit"],
        _ => [],
    };
}
