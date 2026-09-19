using System.Numerics;

namespace ZhuJieJing.Renderer.Controls;

public readonly record struct ShadowCameraFrame(Matrix4x4 ViewProjection, Vector3 Center, float Radius);

/// <summary>Stable, focus-local sunlight shadow projection independent of WPF and D3D resource state.</summary>
public static class ShadowCameraMath
{
    public const int TextureSize = 2048;
    public const float DefaultRadius = 112f;
    public const float AnchorStep = 4f;

    public static ShadowCameraFrame Calculate(Vector3 focus, Vector3 sunDirection, float radius = DefaultRadius)
    {
        ValidateFinite(focus, nameof(focus));
        ValidateFinite(sunDirection, nameof(sunDirection));
        if(!float.IsFinite(radius) || radius is < 16f or > 512f) throw new ArgumentOutOfRangeException(nameof(radius));
        if(sunDirection.LengthSquared() < 0.000001f) throw new ArgumentOutOfRangeException(nameof(sunDirection));

        Vector3 direction = Vector3.Normalize(sunDirection);
        Vector3 center = new(
            Snap(focus.X, AnchorStep),
            Snap(focus.Y, AnchorStep),
            Snap(focus.Z, AnchorStep));
        Vector3 up = MathF.Abs(Vector3.Dot(direction, Vector3.UnitY)) > 0.94f ? Vector3.UnitZ : Vector3.UnitY;
        Vector3 eye = center - direction * (radius * 2f);
        Matrix4x4 view = Matrix4x4.CreateLookAt(eye, center, up);
        Matrix4x4 projection = Matrix4x4.CreateOrthographic(radius * 2f, radius * 2f, 0.1f, radius * 4f);
        return new ShadowCameraFrame(view * projection, center, radius);
    }

    public static bool NeedsUpdate(ShadowCameraFrame previous, Vector3 focus, Vector3 sunDirection)
    {
        ShadowCameraFrame next = Calculate(focus, sunDirection, previous.Radius);
        return next.Center != previous.Center || !NearlyEqual(previous.ViewProjection, next.ViewProjection, 0.00001f);
    }

    private static bool NearlyEqual(Matrix4x4 left, Matrix4x4 right, float epsilon) =>
        MathF.Abs(left.M11 - right.M11) <= epsilon && MathF.Abs(left.M12 - right.M12) <= epsilon &&
        MathF.Abs(left.M13 - right.M13) <= epsilon && MathF.Abs(left.M14 - right.M14) <= epsilon &&
        MathF.Abs(left.M21 - right.M21) <= epsilon && MathF.Abs(left.M22 - right.M22) <= epsilon &&
        MathF.Abs(left.M23 - right.M23) <= epsilon && MathF.Abs(left.M24 - right.M24) <= epsilon &&
        MathF.Abs(left.M31 - right.M31) <= epsilon && MathF.Abs(left.M32 - right.M32) <= epsilon &&
        MathF.Abs(left.M33 - right.M33) <= epsilon && MathF.Abs(left.M34 - right.M34) <= epsilon &&
        MathF.Abs(left.M41 - right.M41) <= epsilon && MathF.Abs(left.M42 - right.M42) <= epsilon &&
        MathF.Abs(left.M43 - right.M43) <= epsilon && MathF.Abs(left.M44 - right.M44) <= epsilon;

    private static float Snap(float value, float step) => MathF.Floor(value / step + 0.5f) * step;

    private static void ValidateFinite(Vector3 value, string parameterName)
    {
        if(!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
