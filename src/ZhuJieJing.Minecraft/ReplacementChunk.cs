using System.Buffers.Binary;
using System.Numerics;
using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

internal sealed class ReplacementChunk
{
    private readonly ReadOnlyMemory<byte> source;
    private readonly int prefixLength;
    private readonly Dictionary<string, NbtField> root;
    private readonly Dictionary<string, NbtField> body;
    private readonly bool wrapped;
    private readonly string sectionKey;
    internal List<ReplacementSection> Sections { get; } = [];

    internal ReplacementChunk(ReadOnlyMemory<byte> source)
    {
        this.source = source;
        if(source.Length < 4 || source.Span[0] != 10) throw new InvalidDataException("区块 NBT 根不是 compound。");
        prefixLength = 3 + BinaryPrimitives.ReadUInt16BigEndian(source.Span[1..]);
        root = NbtCompoundFields.Read(source[prefixLength..]);
        wrapped = root.TryGetValue("Level", out NbtField? level);
        body = wrapped ? level!.Compound() : root;
        sectionKey = wrapped ? "Sections" : "sections";
        if(body.TryGetValue(sectionKey, out NbtField? sections))
            foreach(NbtField section in NbtCompoundFields.List(sections)) Sections.Add(new(section, root.TryGetValue("DataVersion", out var version) && version.IntegerValue() >= 2529));
        if(Sections.Select(s => s.Fields["Y"].IntegerValue()).Distinct().Count() != Sections.Count)
            throw new InvalidDataException("区块包含重复高度的 Section，无法安全替换。");
    }

    internal HashSet<string> EntityBlockNames()
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        if(!body.TryGetValue(wrapped ? "TileEntities" : "block_entities", out var entities)) return names;
        foreach(var entity in NbtCompoundFields.List(entities))
        {
            var fields = entity.Compound();
            if(!fields.ContainsKey("x") || !fields.ContainsKey("y") || !fields.ContainsKey("z"))
                throw new InvalidDataException("方块实体缺少坐标，无法确认替换范围。");
            int y = fields["y"].IntegerValue();
            var section = Sections.FirstOrDefault(s => s.Fields["Y"].IntegerValue() == (y >> 4));
            if(section is null || section.Indices.Length == 0) continue;
            int index = (y & 15) * 256 + (fields["z"].IntegerValue() & 15) * 16 + (fields["x"].IntegerValue() & 15);
            names.Add(section.Palette[section.Indices[index]].State.Name);
        }
        return names;
    }

    internal MinecraftChunkAddress Address(MinecraftDimensionId dimension) => new(dimension, body["xPos"].IntegerValue(), body["zPos"].IntegerValue());

    internal bool ContainsMatch(MinecraftBlockReplacementRequest request) => Sections.Any(section =>
    {
        bool[] matches = section.Palette.Select(request.Changes).ToArray();
        return section.Indices.Any(i => matches[i]);
    });

    internal NbtRewriteOutcome Replace(MinecraftBlockReplacementRequest request, BakedLegacyChunkLighting? lighting = null)
    {
        int changed = 0;
        HashSet<(int X, int Y, int Z)> positions = [];
        int cx = body.TryGetValue("xPos", out var x) ? x.IntegerValue() : throw new InvalidDataException("区块缺少 xPos。");
        int cz = body.TryGetValue("zPos", out var z) ? z.IntegerValue() : throw new InvalidDataException("区块缺少 zPos。");
        foreach(ReplacementSection section in Sections)
            changed += section.Replace(request, cx, cz, positions);

        if(changed > 0)
        {
            string entityKey = wrapped ? "TileEntities" : "block_entities";
            if(body.TryGetValue(entityKey, out NbtField? entities))
            {
                List<NbtField> keep = [], archive = body.TryGetValue("zjj_replaced_block_entities", out var oldArchive) ? NbtCompoundFields.List(oldArchive).ToList() : [];
                foreach(NbtField entity in NbtCompoundFields.List(entities))
                {
                    Dictionary<string, NbtField> fields = entity.Compound();
                    if(!fields.ContainsKey("x") || !fields.ContainsKey("y") || !fields.ContainsKey("z"))
                        throw new InvalidDataException("方块实体缺少坐标，无法安全替换该区块。");
                    bool hit = positions.Contains((fields["x"].IntegerValue(), fields["y"].IntegerValue(), fields["z"].IntegerValue()));
                    if(hit && request.Source.State.Name != request.Target.State.Name) archive.Add(entity); else keep.Add(entity);
                }
                body[entityKey] = NbtCompoundFields.List(10, keep);
                if(archive.Count > 0) body["zjj_replaced_block_entities"] = NbtCompoundFields.List(10, archive);
            }
            foreach(string key in new[] { "TileTicks", "LiquidTicks", "block_ticks", "fluid_ticks" })
                if(body.TryGetValue(key, out var ticks))
                    body[key] = NbtCompoundFields.List(10, NbtCompoundFields.List(ticks).Where(t =>
                    {
                        var f = t.Compound();
                        if(!f.ContainsKey("x") || !f.ContainsKey("y") || !f.ContainsKey("z")) throw new InvalidDataException("调度刻缺少坐标。");
                        return !positions.Contains((f["x"].IntegerValue(), f["y"].IntegerValue(), f["z"].IntegerValue()));
                    }).ToArray());
            body.Remove("ToBeTicked");
            body.Remove("LiquidsToBeTicked");
        }

        // The caller includes a one-chunk halo, since a changed emitter also affects its neighbours.
        foreach(ReplacementSection section in Sections) section.InvalidateLight();
        if(lighting is not null)
        {
            foreach((int y, MinecraftSectionLighting sectionLight) in lighting.Sections)
            {
                ReplacementSection? section = Sections.FirstOrDefault(s => s.Fields["Y"].IntegerValue() == y);
                if(section is null) { section = ReplacementSection.CreateEmptyLegacy(y); Sections.Add(section); }
                section.SetLighting(sectionLight);
            }
        }
        body[sectionKey] = NbtCompoundFields.List(10, Sections.Select(s => NbtCompoundFields.Compound(s.Fields)).ToArray());
        body[wrapped ? "LightPopulated" : "isLightOn"] = NbtCompoundFields.Integer(lighting is null ? 0 : 1, 1);
        if(lighting is null) body["isLightOn"] = NbtCompoundFields.Integer(0, 1);
        body.Remove("Heightmaps");
        if(body.ContainsKey("HeightMap"))
        {
            byte[] heights = new byte[4 + 256 * 4];
            BinaryPrimitives.WriteInt32BigEndian(heights, 256);
            if(lighting is not null)
                for(int i = 0; i < 256; i++) BinaryPrimitives.WriteInt32BigEndian(heights.AsSpan(4 + i * 4), lighting.HeightMap[i]);
            else foreach(ReplacementSection section in Sections) section.AccumulateLegacyHeightMap(heights);
            body["HeightMap"] = new NbtField(11, heights);
        }
        if(wrapped) root["Level"] = NbtCompoundFields.Compound(body);
        byte[] payload = NbtCompoundFields.Write(root);
        byte[] result = new byte[prefixLength + payload.Length];
        source.Span[..prefixLength].CopyTo(result);
        payload.CopyTo(result, prefixLength);
        return new(result, true, changed);
    }
}
