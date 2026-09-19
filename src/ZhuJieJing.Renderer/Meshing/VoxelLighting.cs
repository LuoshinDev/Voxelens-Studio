using System.Numerics;
using System.IO;
using ZhuJieJing.Core;
using ZhuJieJing.Minecraft;

namespace ZhuJieJing.Renderer.Meshing;

/// <summary>Exact Minecraft light levels at one block position.</summary>
public readonly record struct VoxelLightSample(byte SkyLight, byte BlockLight)
{
    public static VoxelLightSample VisibleFallback { get; } = new(15, 0);
}

/// <summary>Lighting baked into one visible face before it is uploaded to the GPU.</summary>
public readonly record struct VoxelFaceLighting(
    byte SkyLight,
    byte BlockLight,
    byte Emission,
    byte AmbientOcclusion0,
    byte AmbientOcclusion1,
    byte AmbientOcclusion2,
    byte AmbientOcclusion3)
{
    public static VoxelFaceLighting FullBright { get; } = new(15, 0, 0, 3, 3, 3, 3);

    public static VoxelFaceLighting Uniform(VoxelLightSample light, byte emission = 0) =>
        new(light.SkyLight, light.BlockLight, emission, 3, 3, 3, 3);

    public Vector4 ToVertexData(int corner)
    {
        byte ao = corner switch
        {
            0 => AmbientOcclusion0,
            1 => AmbientOcclusion1,
            2 => AmbientOcclusion2,
            3 => AmbientOcclusion3,
            _ => throw new ArgumentOutOfRangeException(nameof(corner)),
        };
        return new Vector4(
            SkyLight / 15f,
            BlockLight / 15f,
            0.55f + Math.Clamp(ao, (byte)0, (byte)3) * 0.15f,
            Emission / 15f);
    }
}

/// <summary>Nibble decoding, AO and legacy-compatible light-source metadata kept CPU-testable.</summary>
public static class VoxelLightingMath
{
    public static VoxelLightSample Sample(NormalizedMinecraftSection section, int localIndex)
    {
        ArgumentNullException.ThrowIfNull(section);
        if(localIndex is < 0 or >= 4096) throw new ArgumentOutOfRangeException(nameof(localIndex));
        MinecraftSectionLighting? lighting = section.Lighting;
        if(lighting is null) return VoxelLightSample.VisibleFallback;
        return new VoxelLightSample(
            ReadNibble(lighting.SkyLightNibbles.Span, localIndex, VoxelLightSample.VisibleFallback.SkyLight),
            ReadNibble(lighting.BlockLightNibbles.Span, localIndex, VoxelLightSample.VisibleFallback.BlockLight));
    }

    public static byte ReadNibble(ReadOnlySpan<byte> packed, int localIndex, byte missingFallback)
    {
        if(localIndex is < 0 or >= 4096) throw new ArgumentOutOfRangeException(nameof(localIndex));
        if(missingFallback > 15) throw new ArgumentOutOfRangeException(nameof(missingFallback));
        if(packed.Length == 0) return missingFallback;
        if(packed.Length != 2048) throw new InvalidDataException("Minecraft Section 光照数组必须为空或包含 2048 字节。");
        byte value = packed[localIndex >> 1];
        return (byte)((localIndex & 1) == 0 ? value & 0x0f : value >> 4);
    }

    public static byte AmbientOcclusionLevel(bool side1Occludes, bool side2Occludes, bool cornerOccludes)
    {
        if(side1Occludes && side2Occludes) return 0;
        return (byte)(3 - (side1Occludes ? 1 : 0) - (side2Occludes ? 1 : 0) - (cornerOccludes ? 1 : 0));
    }

    public static byte EmissionLevel(BlockState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        string path = state.Name[(state.Name.IndexOf(':') + 1)..];
        if(path is "lava" or "flowing_lava" or "glowstone" or "sea_lantern" or "beacon" or
           "fire" or "jack_o_lantern" or "lit_pumpkin" or "redstone_lamp_on") return 15;
        if(path is "torch" or "end_rod") return 14;
        if(path is "lit_furnace") return 13;
        if(path is "portal" or "nether_portal") return 11;
        if(path is "redstone_torch" or "redstone_torch_on") return 7;
        if(path is "magma" or "magma_block") return 3;
        if(path is "brewing_stand" or "brown_mushroom" or "dragon_egg") return 1;
        if(path.Contains("campfire", StringComparison.Ordinal)) return 15;
        if(path.Contains("lantern", StringComparison.Ordinal) || path.Contains("shroomlight", StringComparison.Ordinal)) return 15;
        if(path.Contains("candle", StringComparison.Ordinal)) return 12;
        if(path.Contains("light", StringComparison.Ordinal) &&
           state.Properties.TryGetValue("lit", out string? lit) && string.Equals(lit, "true", StringComparison.Ordinal)) return 15;
        return 0;
    }
}
