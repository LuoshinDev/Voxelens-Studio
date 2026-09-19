using System.Numerics;
using ZhuJieJing.Renderer.Meshing;

namespace ZhuJieJing.Renderer.Controls;

internal static class WaterPlaneSelection
{
    internal static float[] Select(IReadOnlyList<SectionRenderGeometry> sections, Vector3 camera, int maximumPlanes)
    {
        var areas = new Dictionary<int, double>();
        foreach(SectionRenderGeometry section in sections)
        {
            foreach((int height, float area) in section.WaterSurfaceAreas)
            {
                Vector3 center = new(section.Coordinate.X * 16f + 8f, height / 16f, section.Coordinate.Z * 16f + 8f);
                double projectedImportance = area / Math.Max(256, Vector3.DistanceSquared(camera, center));
                areas[height] = areas.GetValueOrDefault(height) + projectedImportance;
            }
        }
        return areas.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).Take(maximumPlanes).Select(pair => pair.Key / 16f).ToArray();
    }
}
