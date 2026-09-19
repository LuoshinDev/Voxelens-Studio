using System.Numerics;
using ZhuJieJing.Core;

namespace ZhuJieJing.Renderer.Controls;

internal static class SectionVisibility
{
    internal static bool Intersects(SectionCoordinate coordinate, Matrix4x4 viewProjection) =>
        new SectionFrustum(viewProjection).Intersects(coordinate);
}

/// <summary>Extract once per view; test the Section support point against each D3D clip half-space.</summary>
internal readonly struct SectionFrustum
{
    private readonly Vector4 _left, _right, _bottom, _top, _near, _far;

    internal SectionFrustum(Matrix4x4 matrix)
    {
        Vector4 x = new(matrix.M11, matrix.M21, matrix.M31, matrix.M41);
        Vector4 y = new(matrix.M12, matrix.M22, matrix.M32, matrix.M42);
        Vector4 z = new(matrix.M13, matrix.M23, matrix.M33, matrix.M43);
        Vector4 w = new(matrix.M14, matrix.M24, matrix.M34, matrix.M44);
        _left = w + x; _right = w - x;
        _bottom = w + y; _top = w - y;
        _near = z; _far = w - z;
    }

    internal bool Intersects(SectionCoordinate coordinate)
    {
        Vector3 minimum = new(coordinate.X * 16f, coordinate.Y * 16f, coordinate.Z * 16f);
        return Inside(_left, minimum) && Inside(_right, minimum) && Inside(_bottom, minimum) &&
               Inside(_top, minimum) && Inside(_near, minimum) && Inside(_far, minimum);
    }

    private static bool Inside(Vector4 plane, Vector3 minimum)
    {
        Vector3 support = minimum + new Vector3(plane.X >= 0 ? 16f : 0, plane.Y >= 0 ? 16f : 0, plane.Z >= 0 ? 16f : 0);
        // Conservative at clip boundaries despite floating-point cancellation in large worlds.
        return plane.X * support.X + plane.Y * support.Y + plane.Z * support.Z + plane.W >= -0.0001f;
    }
}
