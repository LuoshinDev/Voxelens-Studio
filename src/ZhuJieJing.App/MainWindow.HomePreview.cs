using ZhuJieJing.App.Preferences;
using ZhuJieJing.App.Resources;
using ZhuJieJing.Minecraft;

namespace ZhuJieJing.App;

public partial class MainWindow
{
    private async Task<string> ConfigureHomeResourcesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(IsSolidMaterialMode())
        {
            Viewport.ClearMinecraftResources();
            activeResourceSelection = null;
            return UiText.Get("纯色性能模式已启用：已跳过纹理图集、材质包和复杂方块模型解析");
        }

        SetMaterialLoadingStage("正在查找匹配的 Minecraft 材质");
        StudioSettings settings = StudioSettingsStore.Load();
        MinecraftVersionDescriptor legacy = MinecraftTargetProfile.Java1122.Version;
        MinecraftVersionDescriptor modern = new(4556, "1.21.10", MinecraftStorageFamily.ModernSectionPalette);
        // A home scene has no world version. Prefer the user's configured family before automatic discovery.
        MinecraftVersionDescriptor preferred = settings.LegacyJar is null && settings.ModernJar is not null ? modern : legacy;
        MinecraftResourceSelectionResult located = await minecraftResourceCacheService.ResolveAsync(
            AppContext.BaseDirectory, preferred, cancellationToken: cancellationToken);
        if(!located.IsAvailable && settings.LegacyJar is null && settings.ModernJar is null)
            located = await minecraftResourceCacheService.ResolveAsync(
                AppContext.BaseDirectory, modern, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if(!located.IsAvailable)
        {
            Viewport.ClearMinecraftResources();
            activeResourceSelection = null;
            return located.Status;
        }

        ResourceSelection selection = new(located.ClientJarPath!, []);
        SetMaterialLoadingStage("正在后台索引 Minecraft 材质资源");
        // Resource installation keeps the demo geometry and camera; ClearSections would replace them with a world.
        await Viewport.ConfigureMinecraft1122ResourcesAsync(CreateRendererConfiguration(selection), cancellationToken);
        activeResourceSelection = selection;
        return located.Status;
    }
}
