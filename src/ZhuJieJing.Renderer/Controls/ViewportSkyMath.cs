using System.Numerics;
using ZhuJieJing.Renderer.Camera;

namespace ZhuJieJing.Renderer.Controls;

internal readonly record struct ViewportSkyCameraBasis(
    Vector3 Right,
    Vector3 Up,
    Vector3 Forward,
    float HorizontalProjectionScale,
    float VerticalProjectionScale);

internal static class ViewportSkyMath
{
    public static ViewportSkyCameraBasis CreateCameraBasis(Vector3 forward, float viewportWidth, float viewportHeight)
        => CreateCameraBasis(forward, viewportWidth, viewportHeight, OrbitCamera.FieldOfView);

    public static ViewportSkyCameraBasis CreateCameraBasis(
        Vector3 forward,
        float viewportWidth,
        float viewportHeight,
        float verticalFieldOfView,
        Vector3? cameraUp = null)
    {
        if(!IsFinite(forward) || forward.LengthSquared() < 0.000001f)
            throw new ArgumentOutOfRangeException(nameof(forward));
        if(!float.IsFinite(viewportWidth) || viewportWidth <= 0f)
            throw new ArgumentOutOfRangeException(nameof(viewportWidth));
        if(!float.IsFinite(viewportHeight) || viewportHeight <= 0f)
            throw new ArgumentOutOfRangeException(nameof(viewportHeight));
        if(!float.IsFinite(verticalFieldOfView) ||
           verticalFieldOfView is < OrbitCamera.MinimumFieldOfView or > OrbitCamera.MaximumFieldOfView)
            throw new ArgumentOutOfRangeException(nameof(verticalFieldOfView));

        Vector3 normalizedForward = Vector3.Normalize(forward);
        Vector3 requestedUp = cameraUp ?? Vector3.UnitY;
        Vector3 referenceUp = MathF.Abs(Vector3.Dot(normalizedForward, requestedUp)) > 0.999f
            ? Vector3.UnitZ
            : requestedUp;
        Vector3 right = Vector3.Normalize(Vector3.Cross(normalizedForward, referenceUp));
        Vector3 up = Vector3.Normalize(Vector3.Cross(right, normalizedForward));
        float verticalScale = MathF.Tan(verticalFieldOfView * 0.5f);
        return new ViewportSkyCameraBasis(
            right,
            up,
            normalizedForward,
            verticalScale * viewportWidth / viewportHeight,
            verticalScale);
    }

    public static Vector3 CreateWorldRay(Vector2 screenUv, ViewportSkyCameraBasis basis)
    {
        if(!float.IsFinite(screenUv.X) || !float.IsFinite(screenUv.Y))
            throw new ArgumentOutOfRangeException(nameof(screenUv));
        Vector2 ndc = new(screenUv.X * 2f - 1f, 1f - screenUv.Y * 2f);
        return Vector3.Normalize(
            basis.Forward +
            basis.Right * (ndc.X * basis.HorizontalProjectionScale) +
            basis.Up * (ndc.Y * basis.VerticalProjectionScale));
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
