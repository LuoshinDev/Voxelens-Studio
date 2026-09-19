using System.Windows;
using ZhuJieJing.App.Components;
using ZhuJieJing.Core;

namespace ZhuJieJing.App;

public partial class MainWindow
{
    private ComponentLibraryWindow? componentLibraryWindow;

    private void ComponentLibrary_Click(object sender, RoutedEventArgs e)
    {
        if(componentLibraryWindow is { } existing)
        {
            if(existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        Func<ComponentPlacementRequest, CancellationToken, Task>? handler = null;
        ConfigureComponentLibraryIntegration(ref handler);
        var position = FromPreviewRenderPosition(Viewport.CameraTarget);
        BlockPosition anchor = new(ClampCoordinate(position.X), ClampCoordinate(position.Y), ClampCoordinate(position.Z));
        string dimension = displayedPlanDimension?.Value ?? displayedWorldDimension.Value;
        ZjjVisualProfile? visualProfile = aiProjectSession?.Workspace.Manifest.Target.VisualProfile?.ToPlanProfile()
            ?? displayedPlanVisualProfile
            ?? (mountedWorld is null ? null : CreatePlanVisualProfile(mountedWorld.Descriptor.Version));
        ComponentLibraryWindow window = new(handler, anchor, dimension, visualProfile) { Owner = this };
        componentLibraryWindow = window;
        window.Closed += (_, _) => componentLibraryWindow = null;
        window.Show();
    }

    partial void ConfigureComponentLibraryIntegration(ref Func<ComponentPlacementRequest, CancellationToken, Task>? handler);
}
