using System.Buffers.Binary;
using System.Numerics;
using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

internal static partial class MinecraftWorldExportProtection
{
    internal static async Task<long> ReplaceRegionBlocksAsync(string path, MinecraftBlockReplacementRequest request, MinecraftDimensionId dimension, IReadOnlySet<MinecraftChunkAddress> affected, ReplacementLightingSource? source, Minecraft1122LightBaker? baker, CancellationToken token)
    {
        RegionProtectionResult result = await ProtectRegionAsync(path, token, bytes =>
        {
            ReplacementChunk chunk = new(bytes);
            return affected.Contains(chunk.Address(dimension)) ? chunk.Replace(request) : new(bytes, false, 0);
        }, source is null || baker is null ? null : async (bytes, cancellation) =>
        {
            ReplacementChunk chunk = new(bytes);
            NormalizedMinecraftChunk normalized = await source.FindAsync(chunk.Address(dimension), cancellation).ConfigureAwait(false)
                ?? throw new InvalidDataException("光照重建找不到原区块。");
            BakedLegacyChunkLighting lighting = await baker.BakeAsync(normalized, cancellation).ConfigureAwait(false);
            return chunk.Replace(request, lighting);
        }).ConfigureAwait(false);
        return result.TickContainersCleared;
    }

    internal static async Task InvalidateReplacementPoiAsync(string path, CancellationToken token)
    {
        await ProtectRegionAsync(path, token, bytes =>
        {
            if(bytes.Length < 4 || bytes[0] != 10) throw new InvalidDataException("POI 根 NBT 无效。");
            int prefix = 3 + BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(1));
            var fields = NbtCompoundFields.Read(bytes.AsMemory(prefix));
            if(!fields.TryGetValue("Sections", out var sectionTag)) return new(bytes, false, 0);
            var sections = sectionTag.Compound();
            foreach(string key in sections.Keys.ToArray())
            {
                var section = sections[key].Compound();
                section["Valid"] = NbtCompoundFields.Integer(0, 1);
                sections[key] = NbtCompoundFields.Compound(section);
            }
            fields["Sections"] = NbtCompoundFields.Compound(sections);
            byte[] body = NbtCompoundFields.Write(fields);
            byte[] result = new byte[prefix + body.Length];
            bytes.AsSpan(0, prefix).CopyTo(result);
            body.CopyTo(result, prefix);
            return new(result, true, 0);
        }).ConfigureAwait(false);
    }
}
