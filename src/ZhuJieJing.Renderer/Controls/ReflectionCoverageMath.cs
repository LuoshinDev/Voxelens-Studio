using System.Numerics;

namespace ZhuJieJing.Renderer.Controls;

/// <summary>Conservative reflection texels that a main-view water triangle can sample.</summary>
internal static class ReflectionCoverageMath
{
    // These bounds follow SamplePlanarReflection: normalized normal.xz * 0.018, followed by a
    // five-tap cross with one-texel offsets and linear filtering. They do not change target resolution.
    private const double MaximumNormalUvOffset = 0.018d;
    private const float MinimumSampleW = 0.0001f;

    internal static bool MightSamplePlane(Vector3 a, Vector3 b, Vector3 c, float planeY)
    {
        if(!float.IsFinite(a.Y) || !float.IsFinite(b.Y) || !float.IsFinite(c.Y) || !float.IsFinite(planeY)) return true;
        // Pixel world heights interpolate within the triangle. Include overlapping plane tolerances
        // conservatively; the shader chooses the first matching plane, so duplicates only add work.
        return Math.Min(a.Y, Math.Min(b.Y, c.Y)) <= planeY + 0.10f &&
               Math.Max(a.Y, Math.Max(b.Y, c.Y)) >= planeY - 0.10f;
    }

    internal static RefractionCopyBounds ForTriangle(
        Vector3 a, Vector3 b, Vector3 c, Matrix4x4 mainViewProjection,
        Matrix4x4 reflectionViewProjection, int width, int height)
    {
        RefractionCopyBounds full = RefractionCopyBounds.Full(width, height);
        Vector4 ma = Vector4.Transform(new Vector4(a, 1), mainViewProjection);
        Vector4 mb = Vector4.Transform(new Vector4(b, 1), mainViewProjection);
        Vector4 mc = Vector4.Transform(new Vector4(c, 1), mainViewProjection);
        if(!IsFinite(ma) || !IsFinite(mb) || !IsFinite(mc)) return full;
        if(ma.W <= 0 && mb.W <= 0 && mc.W <= 0) return default;
        if(ma.W <= 0 || mb.W <= 0 || mc.W <= 0) return full;
        if(AllOutsideMainFrustum(ma, mb, mc)) return default;
        if(ma.Z < 0 || mb.Z < 0 || mc.Z < 0) return full;

        Vector4 ra = Vector4.Transform(new Vector4(a, 1), reflectionViewProjection);
        Vector4 rb = Vector4.Transform(new Vector4(b, 1), reflectionViewProjection);
        Vector4 rc = Vector4.Transform(new Vector4(c, 1), reflectionViewProjection);
        if(!IsFinite(ra) || !IsFinite(rb) || !IsFinite(rc)) return full;
        if(ra.W <= MinimumSampleW && rb.W <= MinimumSampleW && rc.W <= MinimumSampleW) return default;
        if(ra.W <= MinimumSampleW || rb.W <= MinimumSampleW || rc.W <= MinimumSampleW) return full;
        // Do not cull on reflected Z: SamplePlanarReflection checks W and UV only. Even water outside
        // the reflected near/far depth range can sample sky or geometry stored at those valid UVs.
        // With positive W, perspective projection maps the triangle into the convex hull of its three
        // projected vertices. Its complete footprint therefore also contains the main-view clipped part.
        double ax = ScreenX(ra, width), bx = ScreenX(rb, width), cx = ScreenX(rc, width);
        double ay = ScreenY(ra, height), by = ScreenY(rb, height), cy = ScreenY(rc, height);
        double paddingX = Math.Ceiling(width * MaximumNormalUvOffset) + 2;
        double paddingY = Math.Ceiling(height * MaximumNormalUvOffset) + 2;
        int left = (int)Math.Floor(Math.Clamp(Math.Min(ax, Math.Min(bx, cx)) - paddingX, 0, width));
        int top = (int)Math.Floor(Math.Clamp(Math.Min(ay, Math.Min(by, cy)) - paddingY, 0, height));
        int right = (int)Math.Ceiling(Math.Clamp(Math.Max(ax, Math.Max(bx, cx)) + paddingX, 0, width));
        int bottom = (int)Math.Ceiling(Math.Clamp(Math.Max(ay, Math.Max(by, cy)) + paddingY, 0, height));
        return right <= left || bottom <= top ? default : new(left, top, right, bottom);
    }

    private static bool AllOutsideMainFrustum(Vector4 a, Vector4 b, Vector4 c) =>
        a.X < -a.W && b.X < -b.W && c.X < -c.W ||
        a.X > a.W && b.X > b.W && c.X > c.W ||
        a.Y < -a.W && b.Y < -b.W && c.Y < -c.W ||
        a.Y > a.W && b.Y > b.W && c.Y > c.W ||
        a.Z < 0 && b.Z < 0 && c.Z < 0 ||
        a.Z > a.W && b.Z > b.W && c.Z > c.W;

    private static double ScreenX(Vector4 clip, int width) => ((double)clip.X / clip.W * 0.5d + 0.5d) * width;
    private static double ScreenY(Vector4 clip, int height) => (0.5d - (double)clip.Y / clip.W * 0.5d) * height;
    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
}
