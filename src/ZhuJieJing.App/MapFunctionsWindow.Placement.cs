using System.IO;
using System.Text.Json;
using System.Windows;

namespace ZhuJieJing.App;

public partial class MapFunctionsWindow
{
    private static readonly string PlacementPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "筑界镜 Studio", "map-tools-window.json");

    private sealed record MapToolsPlacement(double Width, double Height, bool Maximized);

    private void RestoreWindowSize()
    {
        try
        {
            if(!File.Exists(PlacementPath)) return;
            MapToolsPlacement? saved = JsonSerializer.Deserialize<MapToolsPlacement>(File.ReadAllText(PlacementPath));
            if(saved is null || !double.IsFinite(saved.Width) || !double.IsFinite(saved.Height)) return;
            Width = Math.Clamp(saved.Width, MinWidth, Math.Max(MinWidth, SystemParameters.WorkArea.Width));
            Height = Math.Clamp(saved.Height, MinHeight, Math.Max(MinHeight, SystemParameters.WorkArea.Height));
            if(saved.Maximized) WindowState = WindowState.Maximized;
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private void SaveWindowSize()
    {
        Rect bounds = WindowState == WindowState.Normal ? new Rect(0, 0, ActualWidth, ActualHeight) : RestoreBounds;
        if(bounds.IsEmpty || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) || bounds.Width < MinWidth || bounds.Height < MinHeight) return;
        string temporaryPath = PlacementPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PlacementPath)!);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new MapToolsPlacement(bounds.Width, bounds.Height, WindowState == WindowState.Maximized)));
            File.Move(temporaryPath, PlacementPath, overwrite: true);
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { if(File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch(Exception exception) when(exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
