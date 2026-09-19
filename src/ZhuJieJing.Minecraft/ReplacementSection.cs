using System.Buffers.Binary;
using System.Numerics;
using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

internal sealed class ReplacementSection
{
    internal Dictionary<string, NbtField> Fields { get; }
    internal List<MinecraftReplacementBlock> Palette { get; } = [];
    internal ushort[] Indices { get; private set; } = [];
    private readonly bool legacy;
    private readonly bool nested;
    private readonly bool padded;

    internal ReplacementSection(NbtField field, bool versionUsesPadded)
    {
        Fields = field.Compound();
        legacy = Fields.ContainsKey("Blocks");
        if(legacy)
        {
            if(Fields["Y"].IntegerValue() is < 0 or > 15) throw new InvalidDataException("旧版区块 Section 超出 0…255 高度范围。");
            byte[] ids = Bytes(Fields["Blocks"], 4096), data = Bytes(Fields["Data"], 2048);
            byte[] add = Fields.TryGetValue("Add", out var a) ? Bytes(a, 2048) : new byte[2048];
            Dictionary<int, ushort> map = [];
            Indices = new ushort[4096];
            for(int i = 0; i < 4096; i++)
            {
                ushort id = (ushort)(ids[i] | (Nibble(add, i) << 8));
                byte metadata = Nibble(data, i);
                int key = id * 16 + metadata;
                if(!map.TryGetValue(key, out ushort index))
                {
                    index = (ushort)Palette.Count;
                    map.Add(key, index);
                    Palette.Add(new(Minecraft1122VanillaBlockRegistry.Instance.ResolveLegacy(id, metadata).State, 0, new(id, metadata)));
                }
                Indices[i] = index;
            }
            return;
        }
        nested = Fields.TryGetValue("block_states", out NbtField? container);
        Dictionary<string, NbtField> states = nested ? container!.Compound() : Fields;
        if(!states.TryGetValue(nested ? "palette" : "Palette", out NbtField? palette)) return;
        foreach(NbtField entry in NbtCompoundFields.List(palette))
        {
            var p = entry.Compound();
            var properties = p.TryGetValue("Properties", out var propertiesTag) ? propertiesTag.Compound().ToDictionary(k => k.Key, k => k.Value.StringValue()) : null;
            Palette.Add(new(new BlockState(p["Name"].StringValue(), properties), 0, null));
        }
        if(Palette.Count is 0 or > 4096) throw new InvalidDataException("区块 palette 数量无效。");
        if(!states.TryGetValue(nested ? "data" : "BlockStates", out var packed))
        {
            if(Palette.Count != 1) throw new InvalidDataException("区块缺少方块索引。");
            Indices = new ushort[4096];
            padded = nested || versionUsesPadded;
            return;
        }
        if(packed.Type != 12 || packed.Payload.Length < 4) throw new InvalidDataException("方块索引应为 long 数组。");
        int count = BinaryPrimitives.ReadInt32BigEndian(packed.Payload.Span);
        if(count < 0 || packed.Payload.Length != 4L + count * 8L) throw new InvalidDataException("方块索引长度无效。");
        long[] words = new long[count];
        for(int i = 0; i < count; i++) words[i] = BinaryPrimitives.ReadInt64BigEndian(packed.Payload.Span[(4 + i * 8)..]);
        int bits = Math.Max(4, BitOperations.Log2((uint)Math.Max(1, Palette.Count - 1)) + 1);
        padded = nested || versionUsesPadded || count != (4096 * bits + 63) / 64;
        Indices = Palette.Count == 1 && count == 0 ? new ushort[4096] : ModernAnvilChunkNormalizer.DecodePaletteIndices(Palette.Count, words, "方块替换");
    }

    internal int Replace(MinecraftBlockReplacementRequest request, int cx, int cz, HashSet<(int X, int Y, int Z)> positions)
    {
        if(Indices.Length == 0) return 0;
        bool[] matches = Palette.Select(request.Changes).ToArray();
        if(!matches.Any(m => m)) return 0;
        int y = Fields["Y"].IntegerValue();
        int changed = 0;
        for(int i = 0; i < 4096; i++)
            if(matches[Indices[i]])
            {
                positions.Add((checked(cx * 16 + (i & 15)), checked(y * 16 + (i >> 8)), checked(cz * 16 + ((i >> 4) & 15))));
                changed++;
            }
        if(changed == 0) return 0;
        if(legacy)
        {
            var targets = Palette.Select((block, i) => matches[i] ? request.ResolveTarget(block).LegacyEncoding!.Value : block.LegacyEncoding!.Value).ToArray();
            byte[] ids = new byte[4096], data = new byte[2048], add = new byte[2048];
            for(int i = 0; i < 4096; i++)
            {
                LegacyBlockEncoding encoding = targets[Indices[i]];
                ids[i] = (byte)encoding.NumericId;
                SetNibble(data, i, encoding.Metadata);
                SetNibble(add, i, (byte)(encoding.NumericId >> 8));
            }
            Fields["Blocks"] = ByteArray(ids);
            Fields["Data"] = ByteArray(data);
            if(add.Any(b => b != 0)) Fields["Add"] = ByteArray(add); else Fields.Remove("Add");
        }
        else
        {
            List<BlockState> palette = [];
            Dictionary<string, int> lookup = new(StringComparer.Ordinal);
            int[] remap = new int[Palette.Count];
            for(int i = 0; i < Palette.Count; i++)
            {
                BlockState state = matches[i] ? request.ResolveTarget(Palette[i]).State : Palette[i].State;
                if(!lookup.TryGetValue(state.CanonicalKey, out int index)) { index = palette.Count; lookup.Add(state.CanonicalKey, index); palette.Add(state); }
                remap[i] = index;
            }
            Dictionary<string, NbtField> states = nested ? Fields["block_states"].Compound() : Fields;
            states[nested ? "palette" : "Palette"] = NbtCompoundFields.List(10, palette.Select(s =>
            {
                Dictionary<string, NbtField> p = new() { ["Name"] = NbtCompoundFields.String(s.Name) };
                if(s.Properties.Count > 0) p["Properties"] = NbtCompoundFields.Compound(s.Properties.ToDictionary(k => k.Key, k => NbtCompoundFields.String(k.Value)));
                return NbtCompoundFields.Compound(p);
            }).ToArray());
            int bits = Math.Max(4, BitOperations.Log2((uint)Math.Max(1, palette.Count - 1)) + 1);
            int perWord = 64 / bits;
            ulong[] words = new ulong[padded ? (4096 + perWord - 1) / perWord : (4096 * bits + 63) / 64];
            for(int i = 0; i < 4096; i++)
            {
                ulong value = (ulong)remap[Indices[i]];
                int word = padded ? i / perWord : i * bits / 64;
                int shift = padded ? i % perWord * bits : i * bits % 64;
                words[word] |= value << shift;
                if(!padded && shift + bits > 64) words[word + 1] |= value >> (64 - shift);
            }
            byte[] packed = new byte[4 + words.Length * 8];
            BinaryPrimitives.WriteInt32BigEndian(packed, words.Length);
            for(int i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt64BigEndian(packed.AsSpan(4 + i * 8), words[i]);
            states[nested ? "data" : "BlockStates"] = new NbtField(12, packed);
            if(nested) Fields["block_states"] = NbtCompoundFields.Compound(states);
        }
        return changed;
    }

    internal void InvalidateLight()
    {
        if(legacy) { Fields["SkyLight"] = ByteArray(new byte[2048]); Fields["BlockLight"] = ByteArray(new byte[2048]); }
        else { Fields.Remove("SkyLight"); Fields.Remove("BlockLight"); }
    }
    internal void SetLighting(MinecraftSectionLighting light)
    {
        Fields["SkyLight"] = ByteArray(light.SkyLightNibbles.ToArray());
        Fields["BlockLight"] = ByteArray(light.BlockLightNibbles.ToArray());
    }
    internal static ReplacementSection CreateEmptyLegacy(int y) => new(NbtCompoundFields.Compound(new Dictionary<string, NbtField>
    {
        ["Y"] = NbtCompoundFields.Integer(y, 1), ["Blocks"] = ByteArray(new byte[4096]), ["Data"] = ByteArray(new byte[2048]),
    }), false);
    internal void AccumulateLegacyHeightMap(byte[] heights)
    {
        if(!legacy) return;
        byte[] ids = Bytes(Fields["Blocks"], 4096);
        byte[] add = Fields.TryGetValue("Add", out var a) ? Bytes(a, 2048) : new byte[2048];
        int sectionY = Fields["Y"].IntegerValue();
        for(int i = 0; i < 4096; i++)
        {
            int id = ids[i] | (Nibble(add, i) << 8);
            if(Minecraft1122RuntimeRegistry.GetOpacity((ushort)id) == 0) continue;
            int column = i & 255;
            int height = sectionY * 16 + (i >> 8) + 1;
            int previous = BinaryPrimitives.ReadInt32BigEndian(heights.AsSpan(4 + column * 4));
            if(height > previous) BinaryPrimitives.WriteInt32BigEndian(heights.AsSpan(4 + column * 4), height);
        }
    }
    private static byte Nibble(byte[] bytes, int i) => (byte)((bytes[i >> 1] >> ((i & 1) * 4)) & 15);
    private static void SetNibble(byte[] bytes, int i, byte value) => bytes[i >> 1] |= (byte)((value & 15) << ((i & 1) * 4));
    private static byte[] Bytes(NbtField field, int count)
    {
        if(field.Type != 7 || field.Payload.Length != count + 4 || BinaryPrimitives.ReadInt32BigEndian(field.Payload.Span) != count)
            throw new InvalidDataException("旧版方块数组长度无效。");
        return field.Payload[4..].ToArray();
    }
    private static NbtField ByteArray(byte[] bytes)
    {
        byte[] result = new byte[bytes.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(result, bytes.Length);
        bytes.CopyTo(result, 4);
        return new NbtField(7, result);
    }
}
