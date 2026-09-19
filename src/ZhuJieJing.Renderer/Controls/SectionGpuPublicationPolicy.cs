using ZhuJieJing.Renderer.Meshing;

namespace ZhuJieJing.Renderer.Controls;

/// <summary>Guards the all-or-nothing publication point for a visible Section topology.</summary>
internal static class SectionGpuPublicationPolicy
{
    internal static bool IsReady(
        IReadOnlyList<SectionRenderGeometry> target,
        Func<SectionRenderGeometry, bool> isPublished,
        Func<SectionRenderGeometry, bool> isStaged)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(isPublished);
        ArgumentNullException.ThrowIfNull(isStaged);
        return target.All(geometry => isPublished(geometry) || isStaged(geometry));
    }
}
