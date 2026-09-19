using System.Globalization;
using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

/// <summary>Skull metadata describes attachment; type and standing rotation belong to tile NBT.</summary>
internal static class LegacySkullStates
{
    private static readonly string[] Names = ["skeleton_skull", "wither_skeleton_skull", "zombie_head", "player_head", "creeper_head", "dragon_head"];

    internal static bool TryDescribe(BlockState state, out byte type, out bool wall)
    {
        wall = state.Name.Contains("_wall_", StringComparison.Ordinal);
        int index = state.Name switch
        {
            "minecraft:skeleton_skull" or "minecraft:skeleton_wall_skull" => 0,
            "minecraft:wither_skeleton_skull" or "minecraft:wither_skeleton_wall_skull" => 1,
            "minecraft:zombie_head" or "minecraft:zombie_wall_head" => 2,
            "minecraft:player_head" or "minecraft:player_wall_head" => 3,
            "minecraft:creeper_head" or "minecraft:creeper_wall_head" => 4,
            "minecraft:dragon_head" or "minecraft:dragon_wall_head" => 5,
            _ => -1,
        };
        type = (byte)Math.Max(index, 0);
        return index >= 0;
    }

    internal static LegacyBlockEncoding Encoding(BlockState state, bool wall) => new(144, wall
        ? state.Properties.GetValueOrDefault("facing") switch { "south" => (byte)3, "west" => (byte)4, "east" => (byte)5, _ => (byte)2 }
        : (byte)1);

    internal static BlockState? StateAt(NormalizedMinecraftChunk chunk, BlockPosition position)
    {
        var section = chunk.Sections.FirstOrDefault(s => s.Coordinate.Y == position.Y >> 4);
        if(section is null) return null;
        int index = ((position.Y & 15) << 8) | ((position.Z & 15) << 4) | (position.X & 15);
        return section.Palette[section.PaletteIndices.Span[index]];
    }

    internal static IEnumerable<NormalizedMinecraftBlockEntity> WithMissingEntities(NormalizedMinecraftChunk chunk)
    {
        var positions = chunk.BlockEntities.Select(e => e.Position).ToHashSet();
        foreach(var entity in chunk.BlockEntities) yield return entity;
        foreach(var section in chunk.Sections)
        {
            if(!section.Palette.Any(s => TryDescribe(s, out _, out _))) continue;
            for(int i = 0; i < section.PaletteIndices.Length; i++)
            {
                if(!TryDescribe(section.Palette[section.PaletteIndices.Span[i]], out _, out _)) continue;
                BlockPosition position = section.Coordinate.ToBlockPosition(i);
                if(positions.Contains(position)) continue;
                yield return new NormalizedMinecraftBlockEntity("minecraft:skull", position,
                    new Dictionary<string, object?>(), new MinecraftOpaquePayload(
                        MinecraftOpaquePayloadFormat.EncodedNbtTag, new byte[] { 0 }, "", null, null));
            }
        }
    }

    internal static byte[] WriteState(byte[] payload, BlockState state)
    {
        if(!TryDescribe(state, out byte type, out bool wall)) return payload;
        var fields = NbtCompoundFields.Read(payload);
        fields["SkullType"] = new NbtField(1, new byte[] { type });
        int rotation = state.Properties.TryGetValue("rotation", out string? value) &&
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed & 15 : 0;
        fields["Rot"] = new NbtField(1, new byte[] { wall ? (byte)0 : (byte)rotation });
        return NbtCompoundFields.Write(fields);
    }

    internal static void Restore(List<NormalizedMinecraftSection> sections, IReadOnlyList<NormalizedMinecraftBlockEntity> entities)
    {
        foreach(var group in entities.Where(e => e.TypeId is "minecraft:skull" or "Skull").GroupBy(e => e.Position.Y >> 4))
        {
            int sectionIndex = sections.FindIndex(s => s.Coordinate.Y == group.Key);
            if(sectionIndex < 0) continue;
            var section = sections[sectionIndex];
            var palette = section.Palette.ToList();
            ushort[] indices = section.PaletteIndices.ToArray();
            foreach(var entity in group)
            {
                int i = ((entity.Position.Y & 15) << 8) | ((entity.Position.Z & 15) << 4) | (entity.Position.X & 15);
                BlockState state = palette[indices[i]];
                if(!TryDescribe(state, out byte type, out bool wall)) continue;
                var rewritten = LegacyBlockEntityPayloadRewriter.Rewrite(entity);
                if(!rewritten.PreservedUnknownData) continue;
                var fields = NbtCompoundFields.Read(rewritten.CompoundPayload);
                if(fields.TryGetValue("SkullType", out var kind))
                {
                    int storedType = kind.IntegerValue();
                    if(storedType < 0 || storedType >= Names.Length) continue;
                    type = (byte)storedType;
                }
                var properties = new Dictionary<string, string>(state.Properties, StringComparer.Ordinal);
                if(!wall)
                    properties["rotation"] = (fields.TryGetValue("Rot", out var rot) ? rot.IntegerValue() & 15 : 0).ToString(CultureInfo.InvariantCulture);
                string name = Names[type];
                if(wall) name = name.Replace("_skull", "_wall_skull").Replace("_head", "_wall_head");
                var restored = new BlockState("minecraft:" + name, properties);
                int paletteIndex = palette.IndexOf(restored);
                if(paletteIndex < 0) { paletteIndex = palette.Count; palette.Add(restored); }
                indices[i] = checked((ushort)paletteIndex);
            }
            sections[sectionIndex] = section with { Palette = palette.ToArray(), PaletteIndices = indices };
        }
    }
}
