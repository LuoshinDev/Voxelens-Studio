using System.Numerics;

namespace ZhuJieJing.Renderer.Controls;

/// <summary>A conservative source-copy rectangle in render pixels; Right and Bottom are exclusive.</summary>
internal readonly record struct RefractionCopyBounds(int Left, int Top, int Right, int Bottom)
{
    internal bool IsEmpty => Right <= Left || Bottom <= Top;

    internal static RefractionCopyBounds Full(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        return new(0, 0, width, height);
    }

    internal static RefractionCopyBounds Union(RefractionCopyBounds first, RefractionCopyBounds second)
    {
        if(first.IsEmpty) return second.IsEmpty ? default : second;
        if(second.IsEmpty) return first;
        return new(Math.Min(first.Left, second.Left), Math.Min(first.Top, second.Top),
            Math.Max(first.Right, second.Right), Math.Max(first.Bottom, second.Bottom));
    }

    internal static RefractionCopyBounds ForTriangle(
        Vector3 a, Vector3 b, Vector3 c, Matrix4x4 viewProjection, int width, int height, float uvPadding = 0.01f)
    {
        if(!float.IsFinite(uvPadding) || uvPadding < 0) throw new ArgumentOutOfRangeException(nameof(uvPadding));
        RefractionCopyBounds full = Full(width, height);
        Vector4 ca = Vector4.Transform(new Vector4(a, 1), viewProjection);
        Vector4 cb = Vector4.Transform(new Vector4(b, 1), viewProjection);
        Vector4 cc = Vector4.Transform(new Vector4(c, 1), viewProjection);
        if(!IsFinite(ca) || !IsFinite(cb) || !IsFinite(cc)) return full;
        if(ca.W <= 0 && cb.W <= 0 && cc.W <= 0) return default;
        // A crossing at the eye or near plane requires homogeneous clipping. Keep the complete source
        // instead of dividing a behind-eye vertex or underestimating its visible clipped polygon.
        if(ca.W <= 0 || cb.W <= 0 || cc.W <= 0) return full;
        if(ca.Z < 0 && cb.Z < 0 && cc.Z < 0) return default;
        if(ca.Z > ca.W && cb.Z > cb.W && cc.Z > cc.W) return default;
        if(ca.Z < 0 || cb.Z < 0 || cc.Z < 0) return full;

        // Default shader refraction offsets each UV axis by at most 0.01. A tighter proven material
        // bound may be supplied; zero covers only rasterized writes. Two additional pixels remain for
        // filtering and rasterization boundaries. Retain this padding even for an offscreen triangle
        // near an edge, and return empty only when the entire padded rectangle misses the viewport.
        double paddingX = Math.Ceiling(width * (double)uvPadding) + 2;
        double paddingY = Math.Ceiling(height * (double)uvPadding) + 2;
        double ax = ScreenX(ca, width), bx = ScreenX(cb, width), cx = ScreenX(cc, width);
        double ay = ScreenY(ca, height), by = ScreenY(cb, height), cy = ScreenY(cc, height);
        double left = Math.Min(ax, Math.Min(bx, cx)) - paddingX;
        double right = Math.Max(ax, Math.Max(bx, cx)) + paddingX;
        double top = Math.Min(ay, Math.Min(by, cy)) - paddingY;
        double bottom = Math.Max(ay, Math.Max(by, cy)) + paddingY;
        // Clamp in double before converting: a finite clip coordinate with tiny positive W can project
        // far beyond Int32 without being invalid or requiring an unsafe integer conversion.
        int x0 = (int)Math.Floor(Math.Clamp(left, 0, width));
        int y0 = (int)Math.Floor(Math.Clamp(top, 0, height));
        int x1 = (int)Math.Ceiling(Math.Clamp(right, 0, width));
        int y1 = (int)Math.Ceiling(Math.Clamp(bottom, 0, height));
        return x1 <= x0 || y1 <= y0 ? default : new(x0, y0, x1, y1);
    }

    private static double ScreenX(Vector4 clip, int width) => ((double)clip.X / clip.W * 0.5d + 0.5d) * width;
    private static double ScreenY(Vector4 clip, int height) => (0.5d - (double)clip.Y / clip.W * 0.5d) * height;
    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
}
