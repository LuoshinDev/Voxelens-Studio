using System.IO;
using System.Numerics;
using System.Text.Json;
using ZhuJieJing.Minecraft;
using ZhuJieJing.Renderer.Camera;
using ZhuJieJing.Renderer.Controls;

namespace ZhuJieJing.App;

public partial class MainWindow
{
    private readonly Dictionary<string, SavedMapView> savedMapViews = LoadMapViews();
    private string? activeMapViewPath;
    private static string MapViewsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "筑界镜 Studio", "map-views.json");

    private sealed record SavedMapView(float X, float Y, float Z, float Yaw, float Pitch, float Distance,
        CameraNavigationMode Mode, string Dimension, float FieldOfViewDegrees = 47f)
    {
        public bool IsValid => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z) &&
            Math.Abs((double)X) < int.MaxValue - 1024 && Math.Abs((double)Y) < int.MaxValue - 1024 &&
            Math.Abs((double)Z) < int.MaxValue - 1024 && float.IsFinite(Yaw) && float.IsFinite(Pitch) &&
            Pitch is >= -1.48f and <= 1.48f && float.IsFinite(Distance) && Distance is >= 3f and <= 512f &&
            Enum.IsDefined(Mode) && !string.IsNullOrWhiteSpace(Dimension);
    }

    private static Dictionary<string, SavedMapView> LoadMapViews()
    {
        try
        {
            if(File.Exists(MapViewsPath) && new FileInfo(MapViewsPath).Length <= 2 * 1024 * 1024)
            {
                var values = JsonSerializer.Deserialize<Dictionary<string, SavedMapView>>(File.ReadAllText(MapViewsPath));
                if(values is not null)
                {
                    var result = new Dictionary<string, SavedMapView>(StringComparer.OrdinalIgnoreCase);
                    foreach(var pair in values)
                        if(pair.Value is { IsValid: true }) result[pair.Key] = pair.Value;
                    return result;
                }
            }
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException) { }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    private SavedMapView? FindMapView(string path) => savedMapViews.GetValueOrDefault(Path.GetFullPath(path));

    private void SaveCurrentMapView()
    {
        if(activeMapViewPath is null || displayedContent == DisplayedContent.None ||
           !string.Equals(WorldPathText.Text, activeMapViewPath, StringComparison.OrdinalIgnoreCase)) return;
        var pose = Viewport.CaptureCameraPose().Camera;
        Vector3 focus = FromPreviewRenderPosition(pose.Focus);
        string dimension = displayedContent is DisplayedContent.WorldSections or DisplayedContent.ConvertedWorldSections
            ? displayedWorldDimension.Value : "minecraft:overworld";
        var saved = new SavedMapView(focus.X, focus.Y, focus.Z, pose.Yaw, pose.Pitch, pose.Distance, pose.NavigationMode, dimension,
            (float)PhotographyFieldOfViewSlider.Value);
        if(!saved.IsValid) return;
        savedMapViews[activeMapViewPath] = saved;
        string temporary = MapViewsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MapViewsPath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(savedMapViews));
            File.Move(temporary, MapViewsPath, true);
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { if(File.Exists(temporary)) File.Delete(temporary); }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException) { }
        }
    }

    private void RestoreMapView(string path, SavedMapView? saved)
    {
        activeMapViewPath = Path.GetFullPath(path);
        float degrees = saved?.FieldOfViewDegrees is >= 30f and <= 90f ? saved.FieldOfViewDegrees : 47f;
        PhotographyFieldOfViewSlider.Value = degrees;
        Viewport.SetVerticalFieldOfViewDegrees(degrees);
        if(saved is null) return;
        float fov = degrees * MathF.PI / 180f;
        Viewport.ApplyCameraPose(new ViewportCameraPose(new OrbitCameraPose(
            ToPreviewRenderPosition(new Vector3(saved.X, saved.Y, saved.Z)), saved.Yaw, saved.Pitch,
            saved.Distance, fov, saved.Mode)));
    }
}
