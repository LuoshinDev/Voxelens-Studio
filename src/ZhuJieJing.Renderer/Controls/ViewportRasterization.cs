using Vortice.Direct3D11;

namespace ZhuJieJing.Renderer.Controls;

/// <summary>Explicit D3D rasterization policy for Minecraft and imported-model geometry.</summary>
public static class ViewportRasterization
{
    /// <summary>
    /// Vanilla block models describe both sides of zero-thickness geometry (for example the north/south faces
    /// of crossed grass and the up/down faces of a lily pad). Back-face culling is therefore required: drawing
    /// every face two-sided makes those coincident pairs fight in the depth buffer while the camera moves.
    /// </summary>
    public static RasterizerDescription CreateVoxelDescription()
    {
        RasterizerDescription description = RasterizerDescription.CullBack;
        // Section geometry emits outward faces counter-clockwise on the D3D render target.
        description.FrontCounterClockwise = true;
        description.DepthClipEnable = true;
        return description;
    }

    /// <summary>Keeps the synthetic guide terrain behind real blocks when their faces share y=0.</summary>
    public static RasterizerDescription CreateTerrainDescription()
    {
        RasterizerDescription description = CreateVoxelDescription();
        description.DepthBias = 2;
        description.SlopeScaledDepthBias = 1f;
        // D3D treats a zero clamp as unbounded. Limit the slope contribution so grazing views and cameras below
        // y=0 cannot push the synthetic guide plane far enough to pop or clip while it still loses z-fights.
        description.DepthBiasClamp = 0.0005f;
        return description;
    }

    /// <summary>Moves shadow casters slightly away from the light to prevent broad coplanar faces shadowing themselves.</summary>
    public static RasterizerDescription CreateShadowDescription()
    {
        // Shadow depth is rendered from an independent light camera. Do not reuse the visible-camera winding
        // policy here: imported and Minecraft cutout models can otherwise lose every caster from one light angle.
        RasterizerDescription description = RasterizerDescription.CullNone;
        description.DepthClipEnable = true;
        description.DepthBias = 64;
        description.SlopeScaledDepthBias = 0.72f;
        description.DepthBiasClamp = 0.00035f;
        return description;
    }
}
