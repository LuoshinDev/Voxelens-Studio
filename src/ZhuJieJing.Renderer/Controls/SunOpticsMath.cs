using System.Numerics;

namespace ZhuJieJing.Renderer.Controls;

/// <summary>Top-left-origin screen coordinates; visibility excludes scene depth occlusion.</summary>
internal readonly record struct SunScreenProjection(Vector2 ViewportUv, Vector2 DiscRadiusUv, float VisibilityWeight)
{
    public bool IsVisible => VisibilityWeight > 0f;

    public static SunScreenProjection Hidden => new(new Vector2(0.5f), Vector2.Zero, 0f);
}

/// <summary>Directional sunlight projection shared by the viewport and photographic camera.</summary>
internal static class SunOpticsMath
{
    public const float DefaultAngularRadiusRadians = 0.00465f;
    public const float DefaultHorizonFadeRadians = 0.035f;
    public const float DefaultObserverEyeHeight = 1.62f;
    private const float MinimumForwardDistance = 0.00001f;

    public static SunScreenProjection Project(
        Vector3 directionToSun,
        ViewportSkyCameraBasis camera,
        float angularRadiusRadians = DefaultAngularRadiusRadians,
        float horizonFadeRadians = DefaultHorizonFadeRadians)
    {
        ValidateDirection(camera.Right, nameof(camera));
        ValidateDirection(camera.Up, nameof(camera));
        ValidateDirection(camera.Forward, nameof(camera));
        if(!float.IsFinite(camera.HorizontalProjectionScale) || camera.HorizontalProjectionScale <= 0f ||
           !float.IsFinite(camera.VerticalProjectionScale) || camera.VerticalProjectionScale <= 0f)
            throw new ArgumentOutOfRangeException(nameof(camera));

        return ProjectCore(
            directionToSun,
            camera.Right / camera.HorizontalProjectionScale,
            camera.Up / camera.VerticalProjectionScale,
            camera.Forward,
            angularRadiusRadians,
            horizonFadeRadians);
    }

    /// <summary>
    /// Accepts the untransposed System.Numerics perspective view-projection used for scene drawing.
    /// Homogeneous w=0 makes a directional sun independent of camera translation and scene origin.
    /// </summary>
    public static SunScreenProjection Project(
        Vector3 directionToSun,
        Matrix4x4 viewProjection,
        float angularRadiusRadians = DefaultAngularRadiusRadians,
        float horizonFadeRadians = DefaultHorizonFadeRadians)
    {
        if(!IsFinite(viewProjection)) throw new ArgumentOutOfRangeException(nameof(viewProjection));
        Vector3 forwardColumn = new(viewProjection.M14, viewProjection.M24, viewProjection.M34);
        ValidateDirection(forwardColumn, nameof(viewProjection));
        return ProjectCore(
            directionToSun,
            new Vector3(viewProjection.M11, viewProjection.M21, viewProjection.M31),
            new Vector3(viewProjection.M12, viewProjection.M22, viewProjection.M32),
            forwardColumn,
            angularRadiusRadians,
            horizonFadeRadians);
    }

    /// <summary>Soft circular weight for depth-occlusion samples; a radius of one is the solar limb.</summary>
    public static float DiscVisibilityWeight(float normalizedRadius, float featherFraction = 0.2f)
    {
        if(!float.IsFinite(normalizedRadius) || normalizedRadius < 0f)
            throw new ArgumentOutOfRangeException(nameof(normalizedRadius));
        if(!float.IsFinite(featherFraction) || featherFraction <= 0f || featherFraction > 1f)
            throw new ArgumentOutOfRangeException(nameof(featherFraction));
        return 1f - SmoothStep(1f - featherFraction, 1f, normalizedRadius);
    }

    /// <summary>Feet remain below the actual camera eye even while its pitch, FOV or orbit target changes.</summary>
    public static Vector3 ObserverBodyAnchor(Vector3 eye, float eyeHeight = DefaultObserverEyeHeight)
    {
        if(!IsFinite(eye)) throw new ArgumentOutOfRangeException(nameof(eye));
        if(!float.IsFinite(eyeHeight) || eyeHeight <= 0f) throw new ArgumentOutOfRangeException(nameof(eyeHeight));
        Vector3 anchor = eye - Vector3.UnitY * eyeHeight;
        if(!IsFinite(anchor)) throw new ArgumentOutOfRangeException(nameof(eye));
        return anchor;
    }

    private static SunScreenProjection ProjectCore(
        Vector3 directionToSun,
        Vector3 clipXColumn,
        Vector3 clipYColumn,
        Vector3 clipWColumn,
        float angularRadiusRadians,
        float horizonFadeRadians)
    {
        ValidateDirection(directionToSun, nameof(directionToSun));
        if(!float.IsFinite(angularRadiusRadians) || angularRadiusRadians <= 0f || angularRadiusRadians > 0.1f)
            throw new ArgumentOutOfRangeException(nameof(angularRadiusRadians));
        if(!float.IsFinite(horizonFadeRadians) || horizonFadeRadians <= 0f || horizonFadeRadians > 0.5f)
            throw new ArgumentOutOfRangeException(nameof(horizonFadeRadians));

        Vector3 sun = Vector3.Normalize(directionToSun);
        float clipW = Vector3.Dot(sun, clipWColumn);
        if(sun.Y <= 0f || clipW <= MinimumForwardDistance) return SunScreenProjection.Hidden;
        float clipX = Vector3.Dot(sun, clipXColumn);
        float clipY = Vector3.Dot(sun, clipYColumn);
        Vector2 uv = new(0.5f + clipX / clipW * 0.5f, 0.5f - clipY / clipW * 0.5f);

        // The projection derivative accounts for aspect ratio, FOV and off-axis disc stretching.
        // A small angular disc needs no far-away world point, inverse matrix or per-frame allocation.
        float radiusScale = MathF.Tan(angularRadiusRadians) * 0.5f / (clipW * clipW);
        Vector2 radius = new(
            (clipXColumn * clipW - clipWColumn * clipX).Length() * radiusScale,
            (clipYColumn * clipW - clipWColumn * clipY).Length() * radiusScale);
        if(!IsFinite(uv) || !IsFinite(radius) || radius.X <= 0f || radius.Y <= 0f)
            return SunScreenProjection.Hidden;

        float edgeWeight = SmoothStep(-radius.X, radius.X, uv.X) *
                           SmoothStep(-radius.X, radius.X, 1f - uv.X) *
                           SmoothStep(-radius.Y, radius.Y, uv.Y) *
                           SmoothStep(-radius.Y, radius.Y, 1f - uv.Y);
        float horizonWeight = SmoothStep(0f, MathF.Sin(horizonFadeRadians), sun.Y);
        return new SunScreenProjection(uv, radius, edgeWeight * horizonWeight);
    }

    private static float SmoothStep(float minimum, float maximum, float value)
    {
        float t = Math.Clamp((value - minimum) / (maximum - minimum), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static void ValidateDirection(Vector3 direction, string parameter)
    {
        float lengthSquared = direction.LengthSquared();
        if(!IsFinite(direction) || !float.IsFinite(lengthSquared) || lengthSquared < 0.00000001f)
            throw new ArgumentOutOfRangeException(parameter);
    }

    private static bool IsFinite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
        float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
