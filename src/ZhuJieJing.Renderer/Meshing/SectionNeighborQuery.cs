using System.IO;
using ZhuJieJing.Core;
using ZhuJieJing.Minecraft;

namespace ZhuJieJing.Renderer.Meshing;

public interface ISectionNeighborQuery
{
    bool TryGetBlock(string dimension, BlockPosition position, out BlockState state);

    bool TryGetLight(string dimension, BlockPosition position, out VoxelLightSample light);
}

public sealed class NormalizedSectionNeighborQuery : ISectionNeighborQuery
{
    private readonly IReadOnlyDictionary<SectionCoordinate, NormalizedMinecraftSection> _sections;

    public NormalizedSectionNeighborQuery(IEnumerable<NormalizedMinecraftSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        _sections = sections.ToDictionary(section => section.Coordinate);
    }

    public bool TryGetBlock(string dimension, BlockPosition position, out BlockState state)
    {
        var coordinate = SectionCoordinate.FromBlock(dimension, position);
        if(!_sections.TryGetValue(coordinate, out var section))
        {
            state = BlockState.Air;
            return false;
        }

        ValidateSection(section);
        var paletteIndex = section.PaletteIndices.Span[SectionCoordinate.LocalIndex(position)];
        if(paletteIndex >= section.Palette.Count)
        {
            throw new InvalidDataException($"Section {coordinate} 的调色板索引 {paletteIndex} 越界。");
        }

        state = section.Palette[paletteIndex];
        return true;
    }

    public bool TryGetLight(string dimension, BlockPosition position, out VoxelLightSample light)
    {
        var coordinate = SectionCoordinate.FromBlock(dimension, position);
        if(!_sections.TryGetValue(coordinate, out var section))
        {
            light = VoxelLightSample.VisibleFallback;
            return false;
        }

        ValidateSection(section);
        light = VoxelLightingMath.Sample(section, SectionCoordinate.LocalIndex(position));
        return true;
    }

    internal static void ValidateSection(NormalizedMinecraftSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if(string.IsNullOrWhiteSpace(section.Coordinate.Dimension)) throw new InvalidDataException("Section 维度不能为空。");
        if(section.Palette.Count == 0) throw new InvalidDataException($"Section {section.Coordinate} 的调色板为空。");
        if(section.PaletteIndices.Length != 4096) throw new InvalidDataException($"Section {section.Coordinate} 必须包含 4096 个调色板索引。");
        if(section.Lighting is not null)
        {
            if(section.Lighting.SkyLightNibbles.Length is not 0 and not 2048)
            {
                throw new InvalidDataException($"Section {section.Coordinate} 的 SkyLight 必须为空或包含 2048 字节。");
            }
            if(section.Lighting.BlockLightNibbles.Length is not 0 and not 2048)
            {
                throw new InvalidDataException($"Section {section.Coordinate} 的 BlockLight 必须为空或包含 2048 字节。");
            }
        }
    }
}
