namespace ZhuJieJing.Renderer.Controls;

/// <summary>Shared dimensions for the camera-centred Minecraft rain volume.</summary>
public static class ViewportRainMath
{
    public const int DropCount = 720;
    public const float FallSpeed = 44f;
    public const float VerticalSpan = 128f;
    public const float TopOffset = 64f;
    public const float HorizontalRadius = 56f;

    /// <summary>Returns a drop's top endpoint relative to the camera; it only moves downward between respawns.</summary>
    public static float CalculateTopOffset(float cycleSeed, float elapsedSeconds)
    {
        if(!float.IsFinite(cycleSeed) || cycleSeed is < 0f or >= 1f) throw new ArgumentOutOfRangeException(nameof(cycleSeed));
        if(!float.IsFinite(elapsedSeconds) || elapsedSeconds < 0f) throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        float cycle = Fraction(cycleSeed + elapsedSeconds * FallSpeed / VerticalSpan);
        return TopOffset - cycle * VerticalSpan;
    }

    private static float Fraction(float value) => value - MathF.Floor(value);
}
