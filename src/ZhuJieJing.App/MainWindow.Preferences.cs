using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ZhuJieJing.App.Preferences;
using ZhuJieJing.Renderer.Controls;

namespace ZhuJieJing.App;

public partial class MainWindow
{
    private readonly PreviewPreferencesStore previewPreferencesStore = PreviewPreferencesStore.CreateDefault();
    private DispatcherTimer? previewPreferencesSaveTimer;
    private Rect lastNormalWindowBounds;

    private void RestorePreviewPreferences()
    {
        PreviewPreferences preferences = previewPreferencesStore.Load();
        RestoreWindowPlacement(preferences);

        // Navigation and pointer lock are session state: every launch intentionally starts in model browsing.
        OrbitPreviewModeChoice.IsChecked = true;
        ObserverPreviewModeChoice.IsChecked = false;
        Viewport.SetNavigationMode(ViewportNavigationMode.Orbit);

        // The welcome model has a fixed composition; map views are restored after opening a map.
        PhotographyFieldOfViewSlider.Value = 47;
        Viewport.ApplyCameraPose(new ViewportCameraPose(new ZhuJieJing.Renderer.Camera.OrbitCameraPose(
            new System.Numerics.Vector3(0f, 0.29f, 0f), -0.78f, -0.58f,
            ZoomPercentageToDistance(36), 47f * MathF.PI / 180f,
            ZhuJieJing.Renderer.Camera.CameraNavigationMode.Orbit)));
        Viewport.ObserverMouseSensitivity = preferences.ObserverMouseSensitivity;
        KeyboardMoveSpeedSlider.Value = preferences.KeyboardMovementSpeed;
        LoadedChunkRadiusSlider.Value = WorldPreview.WorldPreviewNavigation.RadiusForChunkCount(
            preferences.MaximumLoadedChunkCount);
        ChunkLoadConcurrencySlider.Value = preferences.ChunkLoadConcurrency;
        TimeOfDaySlider.Value = preferences.TimeOfDay;
        CloudsToggle.IsChecked = preferences.CloudsEnabled;
        RainToggle.IsChecked = preferences.RainEnabled;
        FogToggle.IsChecked = preferences.FogEnabled;
        EnhancedLightingToggle.IsChecked = preferences.EnhancedLightingEnabled;
        ChunksTerrainChoice.IsChecked = preferences.TerrainMode == ViewportTerrainMode.Chunks;
        SuperflatTerrainChoice.IsChecked = preferences.TerrainMode == ViewportTerrainMode.Superflat;
        TransparentTerrainChoice.IsChecked = preferences.TerrainMode == ViewportTerrainMode.Transparent;
        SolidMaterialChoice.IsChecked = preferences.MaterialMode == PreviewMaterialMode.Solid;
        MinecraftMaterialChoice.IsChecked = preferences.MaterialMode == PreviewMaterialMode.Minecraft;
    }

    private void RestoreWindowPlacement(PreviewPreferences preferences)
    {
        double virtualLeft = SystemParameters.VirtualScreenLeft;
        double virtualTop = SystemParameters.VirtualScreenTop;
        double virtualWidth = Math.Max(SystemParameters.VirtualScreenWidth, MinWidth);
        double virtualHeight = Math.Max(SystemParameters.VirtualScreenHeight, MinHeight);
        double width = Math.Clamp(preferences.WindowWidth, MinWidth, virtualWidth);
        double height = Math.Clamp(preferences.WindowHeight, MinHeight, virtualHeight);

        Width = width;
        Height = height;
        if(preferences.WindowLeft is double savedLeft && preferences.WindowTop is double savedTop)
        {
            Left = Math.Clamp(savedLeft, virtualLeft, virtualLeft + virtualWidth - width);
            Top = Math.Clamp(savedTop, virtualTop, virtualTop + virtualHeight - height);
            WindowStartupLocation = WindowStartupLocation.Manual;
        }

        lastNormalWindowBounds = new Rect(Left, Top, width, height);
        if(preferences.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void BeginPreviewPreferencesTracking()
    {
        PhotographyFieldOfViewSlider.LostMouseCapture += (_, _) => SaveCurrentMapView();
        PhotographyFieldOfViewSlider.LostKeyboardFocus += (_, _) => SaveCurrentMapView();
        UiText.Bind(PhotographyFieldOfViewSlider, "ToolTip", UiText.Text("按地图记忆镜头视野，再次打开时恢复"));
        previewPreferencesSaveTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        previewPreferencesSaveTimer.Tick += PreviewPreferencesSaveTimer_Tick;
        Viewport.ZoomTargetDistanceChanged += PreviewZoomTargetDistanceChanged;
        ZoomSlider.ValueChanged += PreviewSlider_ValueChanged;
        KeyboardMoveSpeedSlider.ValueChanged += PreviewSlider_ValueChanged;
        LoadedChunkRadiusSlider.ValueChanged += PreviewSlider_ValueChanged;
        ChunkLoadConcurrencySlider.ValueChanged += PreviewSlider_ValueChanged;
        TimeOfDaySlider.ValueChanged += PreviewSlider_ValueChanged;
        CloudsToggle.Click += PreviewToggle_Click;
        RainToggle.Click += PreviewToggle_Click;
        FogToggle.Click += PreviewToggle_Click;
        ChunksTerrainChoice.Checked += PreviewToggle_Checked;
        SuperflatTerrainChoice.Checked += PreviewToggle_Checked;
        TransparentTerrainChoice.Checked += PreviewToggle_Checked;
        SolidMaterialChoice.Checked += PreviewToggle_Checked;
        MinecraftMaterialChoice.Checked += PreviewToggle_Checked;
        LocationChanged += PreviewWindowPlacementChanged;
        SizeChanged += PreviewWindowPlacementChanged;
        StateChanged += PreviewWindowStateChanged;
        Closing += PreviewPreferences_Closing;
    }

    private void PreviewZoomTargetDistanceChanged(float _) => SchedulePreviewPreferencesSave();

    private void PreviewSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        SchedulePreviewPreferencesSave();

    private void PreviewToggle_Click(object sender, RoutedEventArgs e) => SchedulePreviewPreferencesSave();

    private void PreviewToggle_Checked(object sender, RoutedEventArgs e) => SchedulePreviewPreferencesSave();

    private void PreviewWindowPlacementChanged(object? sender, EventArgs e)
    {
        RememberNormalWindowBounds();
        SchedulePreviewPreferencesSave();
    }

    private void PreviewWindowStateChanged(object? sender, EventArgs e)
    {
        RememberNormalWindowBounds();
        SchedulePreviewPreferencesSave();
    }

    private void RememberNormalWindowBounds()
    {
        if(WindowState != WindowState.Normal) return;
        Rect bounds = new(Left, Top, ActualWidth, ActualHeight);
        if(IsUsableWindowBounds(bounds)) lastNormalWindowBounds = bounds;
    }

    private void SchedulePreviewPreferencesSave()
    {
        if(!isUiReady || isClosing || previewPreferencesSaveTimer is null) return;
        previewPreferencesSaveTimer.Stop();
        previewPreferencesSaveTimer.Start();
    }

    private void PreviewPreferencesSaveTimer_Tick(object? sender, EventArgs e) => SavePreviewPreferences();

    private void PreviewPreferences_Closing(object? sender, CancelEventArgs e) => SavePreviewPreferences();

    private void SavePreviewPreferences()
    {
        previewPreferencesSaveTimer?.Stop();
        RememberNormalWindowBounds();
        Rect bounds = IsUsableWindowBounds(lastNormalWindowBounds)
            ? lastNormalWindowBounds
            : new Rect(Left, Top, Width, Height);
        previewPreferencesStore.TrySave(new PreviewPreferences
        {
            ZoomDistance = Viewport.ZoomTargetDistance,
            ObserverMouseSensitivity = Viewport.ObserverMouseSensitivity,
            KeyboardMovementSpeed = (float)KeyboardMoveSpeedSlider.Value,
            MaximumLoadedChunkCount = CurrentMaximumLoadedChunkCount,
            ChunkLoadConcurrency = CurrentChunkLoadConcurrency,
            TimeOfDay = (float)TimeOfDaySlider.Value,
            CloudsEnabled = CloudsToggle.IsChecked == true,
            RainEnabled = RainToggle.IsChecked == true,
            FogEnabled = FogToggle.IsChecked == true,
            EnhancedLightingEnabled = EnhancedLightingToggle.IsChecked == true,
            TerrainMode = SelectedTerrainMode(),
            MaterialMode = SolidMaterialChoice.IsChecked == true
                ? PreviewMaterialMode.Solid
                : PreviewMaterialMode.Minecraft,
            WindowWidth = bounds.Width,
            WindowHeight = bounds.Height,
            WindowLeft = bounds.Left,
            WindowTop = bounds.Top,
            WindowMaximized = WindowState == WindowState.Maximized,
        });
    }

    private static bool IsUsableWindowBounds(Rect bounds) =>
        !bounds.IsEmpty &&
        double.IsFinite(bounds.Left) &&
        double.IsFinite(bounds.Top) &&
        double.IsFinite(bounds.Width) &&
        double.IsFinite(bounds.Height) &&
        bounds.Width >= PreviewPreferences.MinimumWindowWidth &&
        bounds.Height >= PreviewPreferences.MinimumWindowHeight;
}
