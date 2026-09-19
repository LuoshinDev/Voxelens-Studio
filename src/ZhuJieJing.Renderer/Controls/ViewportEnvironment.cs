using System.Numerics;

namespace ZhuJieJing.Renderer.Controls;

public enum ViewportTerrainMode
{
    Chunks,
    Superflat,
    Transparent,
}

/// <summary>Mutable viewport options kept separate from WPF so validation and daylight math remain CPU-testable.</summary>
public sealed class ViewportEnvironmentSettings
{
    public float TimeOfDay { get; private set; } = 14f;

    public bool CloudsEnabled { get; private set; } = true;

    public bool RainEnabled { get; private set; }

    public bool FogEnabled { get; private set; } = true;

    public ViewportTerrainMode TerrainMode { get; private set; } = ViewportTerrainMode.Chunks;

    public void SetTimeOfDay(float hours)
    {
        if(!float.IsFinite(hours) || hours is < 0f or > 24f) throw new ArgumentOutOfRangeException(nameof(hours));
        TimeOfDay = hours == 24f ? 0f : hours;
    }

    public void SetCloudsEnabled(bool enabled) => CloudsEnabled = enabled;

    public void SetRainEnabled(bool enabled) => RainEnabled = enabled;

    public void SetFogEnabled(bool enabled) => FogEnabled = enabled;

    public void SetTerrainMode(ViewportTerrainMode mode)
    {
        if(!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        TerrainMode = mode;
    }

    public ViewportEnvironmentFrame CreateFrame() =>
        ViewportEnvironmentMath.Calculate(TimeOfDay, RainEnabled, FogEnabled);

    public ViewportEnvironmentFrame CreateFrame(float cameraDistance) =>
        ViewportEnvironmentMath.Calculate(TimeOfDay, RainEnabled, FogEnabled, cameraDistance);
}

public readonly record struct ViewportEnvironmentFrame(
    Vector3 SunDirection,
    float SunlightStrength,
    Vector3 MoonDirection,
    float MoonlightStrength,
    Vector3 SunlightColor,
    Vector3 MoonlightColor,
    Vector3 AmbientLightColor,
    float AmbientLightStrength,
    Vector3 SkyZenithColor,
    Vector3 SkyHorizonColor,
    Vector3 FogColor,
    float StarVisibility,
    float FogDensity,
    float DaylightFactor)
{
    public Vector3 LightDirection => SunDirection;
}

public static class ViewportEnvironmentMath
{
    public const float ClearFogDensity = 0.0065f;
    public const float RainFogDensity = 0.012f;
    public const float ReferenceCameraDistance = 96f;

    public static ViewportEnvironmentFrame Calculate(float timeOfDay, bool raining, bool fogEnabled)
        => Calculate(timeOfDay, raining, fogEnabled, ReferenceCameraDistance);

    public static ViewportEnvironmentFrame Calculate(float timeOfDay, bool raining, bool fogEnabled, float cameraDistance)
    {
        if(!float.IsFinite(timeOfDay) || timeOfDay is < 0f or > 24f) throw new ArgumentOutOfRangeException(nameof(timeOfDay));
        if(!float.IsFinite(cameraDistance) || cameraDistance <= 0f) throw new ArgumentOutOfRangeException(nameof(cameraDistance));
        float hour = timeOfDay == 24f ? 0f : timeOfDay;
        float angle = (hour - 6f) / 24f * MathF.Tau;
        var sunPosition = Vector3.Normalize(new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0.28f));
        float daylight = SmoothStep(-0.30f, 0.28f, sunPosition.Y);
        float sunVisible = SmoothStep(-0.10f, 0.16f, sunPosition.Y);
        float moonVisible = 1f - SmoothStep(-0.22f, 0.16f, sunPosition.Y);
        float stars = 1f - SmoothStep(-0.18f, 0.06f, sunPosition.Y);
        float twilight = MathF.Exp(-MathF.Abs(sunPosition.Y) * 5.5f) * (1f - stars * 0.36f);

        Vector3 zenith = Vector3.Lerp(Srgb(7, 18, 37), Srgb(98, 169, 220), daylight);
        Vector3 horizon = Vector3.Lerp(Srgb(21, 31, 51), Srgb(183, 214, 231), daylight);
        horizon = Vector3.Lerp(horizon, Srgb(230, 143, 104), twilight * 0.62f);
        Vector3 ambientColor = Vector3.Lerp(Srgb(113, 132, 173), Srgb(207, 232, 255), daylight);
        if(raining)
        {
            zenith = Vector3.Lerp(zenith, Srgb(69, 84, 92), 0.72f);
            horizon = Vector3.Lerp(horizon, Srgb(104, 117, 122), 0.74f);
            ambientColor = Vector3.Lerp(ambientColor, Srgb(145, 160, 174), 0.56f);
            stars *= 0.08f;
        }

        float sunlight = 0.58f * sunVisible * (raining ? 0.62f : 1f);
        float moonlight = 0.18f * moonVisible * (raining ? 0.55f : 1f);
        float ambientStrength = (0.30f + daylight * 0.45f) * (raining ? 0.86f : 1f);
        ambientStrength = MathF.Max(ambientStrength, raining ? 0.26f : 0.30f);
        Vector3 sunlightColor = Vector3.Lerp(Srgb(255, 156, 110), Srgb(255, 243, 221), SmoothStep(0.02f, 0.48f, sunPosition.Y));
        Vector3 moonlightColor = Srgb(127, 157, 204);
        // Fog shares the rendered horizon color, matching Minecraft's seamless world-to-sky fade.
        Vector3 fogColor = horizon;
        float fogDensity = CalculateFogDensity(raining, fogEnabled, cameraDistance);
        return new ViewportEnvironmentFrame(
            -sunPosition,
            sunlight,
            sunPosition,
            moonlight,
            sunlightColor,
            moonlightColor,
            ambientColor,
            ambientStrength,
            zenith,
            horizon,
            fogColor,
            stars,
            fogDensity,
            daylight);
    }

    /// <summary>
    /// Keeps the close/model-view strength used by the web prototype while preventing a zoomed-out
    /// orbit target from becoming an opaque wall of fog.
    /// </summary>
    public static float CalculateFogDensity(bool raining, bool fogEnabled, float cameraDistance)
    {
        if(!float.IsFinite(cameraDistance) || cameraDistance <= 0f) throw new ArgumentOutOfRangeException(nameof(cameraDistance));
        if(!fogEnabled) return 0f;

        float baseDensity = raining ? RainFogDensity : ClearFogDensity;
        float maximumTargetFog = raining ? 0.88f : 0.72f;
        float targetDensityCap = MathF.Sqrt(-MathF.Log(1f - maximumTargetFog)) / cameraDistance;
        return MathF.Min(baseDensity, targetDensityCap);
    }

    public static float CalculateFogFactor(float distance, float density)
    {
        if(!float.IsFinite(distance) || distance < 0f) throw new ArgumentOutOfRangeException(nameof(distance));
        if(!float.IsFinite(density) || density < 0f) throw new ArgumentOutOfRangeException(nameof(density));
        float scaledDistance = distance * density;
        return 1f - MathF.Exp(-(scaledDistance * scaledDistance));
    }

    private static Vector3 Srgb(byte red, byte green, byte blue) => ViewportColorSpace.SrgbToLinear(
        new Vector3(red / 255f, green / 255f, blue / 255f));

    private static float SmoothStep(float minimum, float maximum, float value)
    {
        float t = Math.Clamp((value - minimum) / (maximum - minimum), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

public static class ViewportColorSpace
{
    public static Vector3 SrgbToLinear(Vector3 color) => new(
        SrgbToLinear(color.X),
        SrgbToLinear(color.Y),
        SrgbToLinear(color.Z));

    public static Vector3 LinearToSrgb(Vector3 color) => new(
        LinearToSrgb(color.X),
        LinearToSrgb(color.Y),
        LinearToSrgb(color.Z));

    public static float SrgbToLinear(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        return value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    }

    public static float LinearToSrgb(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        return value <= 0.0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1f / 2.4f) - 0.055f;
    }
}
