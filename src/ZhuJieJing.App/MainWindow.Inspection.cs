using System.Numerics;
using System.Windows;
using System.Windows.Input;
using ZhuJieJing.App.Resources;
using ZhuJieJing.Core;
using ZhuJieJing.Minecraft;
using ZhuJieJing.Renderer.Controls;

namespace ZhuJieJing.App;

public partial class MainWindow
{
    private int inspectionGeneration;
    private MinecraftBlockThumbnailProvider? inspectionThumbnails;

    private void InspectionToggle_Click(object sender, RoutedEventArgs e)
    {
        inspectionGeneration++;
        BlockInspectionPanel.Visibility = InspectionToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UiText.Bind(BlockInspectionText, "Text", UiText.Text("请在画面中左键点选一个方块。\n拖动镜头前请关闭方块检查。"));
        BlockTexturePanel.Visibility = Visibility.Collapsed;
        BlockTextureImage.Source = null;
    }

    private static string InspectionName(BlockState state)
    {
        if(UiText.Language != "zh-CN") return $"{UiText.BlockName(state.Name)}\n{state.Name}";
        string chinese = Minecraft1122BlockDisplayNames.TryGetDisplayName(state.Name, out string name) ? name : "暂无中文译名";
        return $"{chinese} / {MinecraftBlockEnglishNames.GetName(state.Name)}\n{state.Name}";
    }

    private async void InspectionViewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if(isClosing || InspectionToggle.IsChecked != true) return;
        e.Handled = true;
        int generation = ++inspectionGeneration;
        int sceneGeneration = previewGeneration;
        BlockTextureImage.Source = null;
        BlockTexturePanel.Visibility = Visibility.Collapsed;
        if(!Viewport.TryPickBlock(e.GetPosition(Viewport), out ViewportBlockPick pick))
        {
            UiText.Bind(BlockInspectionText, "Text", UiText.Text("这里没有已加载的可检查方块。"));
            return;
        }
        Vector3 source = FromPreviewRenderPosition(new Vector3(pick.Position.X, pick.Position.Y, pick.Position.Z));
        string material = activeResourceSelection is { } resource ? System.IO.Path.GetFileName(resource.ClientJarPath) : UiText.Get("纯色预览");
        string description = UiText.Format($"{pick.State.Name}\n\n源坐标：{source.X:0}, {source.Y:0}, {source.Z:0}\n预览坐标：{pick.Position.X}, {pick.Position.Y}, {pick.Position.Z}\n维度：{pick.Dimension}\n天空光：{pick.SkyLight}/15　方块光：{pick.BlockLight}/15\n材质基础：{material}\n\n状态：\n{pick.State.CanonicalKey}");
        BlockInspectionText.Text = description + UiText.Get("\n\n正在读取方块信息…");
        try
        {
            var actualTexture = Viewport.CaptureBlockTexture(pick);
            bool legacy = displayedContent is DisplayedContent.ConvertedWorldSections or DisplayedContent.SchematicSections ||
                (displayedContent == DisplayedContent.WorldSections && mountedWorld?.Descriptor.Version.StorageFamily == MinecraftStorageFamily.LegacyNumericAnvil) ||
                (displayedContent == DisplayedContent.PlanPreview && displayedPlanVisualVersion?.StorageFamily == MinecraftStorageFamily.LegacyNumericAnvil);
            var info = await Task.Run(() =>
            {
                MinecraftBlockMapping builtIn = Minecraft1122BlockMappingCatalog.ResolveBuiltIn(pick.State);
                return (Name: InspectionName(pick.State), Encoding: pick.SourceLegacyEncoding ?? (legacy ? builtIn.LegacyEncoding : null));
            });
            if(isClosing || generation != inspectionGeneration || sceneGeneration != previewGeneration) return;
            string BuildDescription()
            {
                string encodingText = info.Encoding is LegacyBlockEncoding sourceEncoding
                    ? UiText.Format($"\n{(pick.SourceLegacyEncoding is not null ? UiText.Get("原始 ID:子ID") : UiText.Get("1.12.2 等效 ID:子ID"))}：{sourceEncoding.NumericId}:{sourceEncoding.Metadata}\n主 ID：{sourceEncoding.NumericId}　子 ID（metadata）：{sourceEncoding.Metadata}")
                    : legacy ? UiText.Get("\n此状态没有可用的 1.12.2 编码。") : UiText.Get("\n当前版本使用命名 ID 与方块状态。");
                string details = UiText.Format($"{pick.State.Name}\n\n源坐标：{source.X:0}, {source.Y:0}, {source.Z:0}\n预览坐标：{pick.Position.X}, {pick.Position.Y}, {pick.Position.Z}\n维度：{pick.Dimension}\n天空光：{pick.SkyLight}/15　方块光：{pick.BlockLight}/15\n材质基础：{UiText.Get(material)}\n\n状态：\n{pick.State.CanonicalKey}");
                return InspectionName(pick.State) + encodingText + details[pick.State.Name.Length..];
            }
            description = BuildDescription();
            UiText.Bind(BlockInspectionText, "Text", BuildDescription);
            if(actualTexture is not null)
            {
                BlockTextureImage.Source = actualTexture;
                UiText.Bind(BlockTextureCaption, "Text", UiText.Text("当前材质包 · 点选面的实际贴图（含染色）"));
            }
            else
            {
                if(inspectionThumbnails is null)
                {
                    var created = await MinecraftBlockThumbnailProvider.CreateConfiguredAsync(thumbnailPixelSize: 64);
                    if(isClosing || generation != inspectionGeneration || sceneGeneration != previewGeneration)
                    {
                        created.Dispose();
                        return;
                    }
                    inspectionThumbnails = created;
                    Closed += (_, _) => inspectionThumbnails?.Dispose();
                }
                var thumbnails = inspectionThumbnails;
                var thumbnail = await Task.Run(() => legacy && info.Encoding is LegacyBlockEncoding encoding
                    ? thumbnails.GetLegacyThumbnail(encoding) : thumbnails.GetModernThumbnail(pick.State));
                if(isClosing || generation != inspectionGeneration || sceneGeneration != previewGeneration) return;
                BlockTextureImage.Source = thumbnail;
                UiText.Bind(BlockTextureCaption, "Text", UiText.Text("纯色预览 · Minecraft 材质参考"));
            }
            BlockTexturePanel.Visibility = Visibility.Visible;
            IMinecraftBlockDowngradeRules rules = await GetBlockMappingRulesAsync();
            MinecraftBlockMapping mapping = rules.Resolve(pick.State, MinecraftTargetProfile.Java1122);
            if(isClosing || generation != inspectionGeneration || sceneGeneration != previewGeneration) return;
            UiText.Bind(BlockInspectionText, "Text", () =>
            {
                string targetId = mapping.LegacyEncoding is LegacyBlockEncoding targetEncoding ? UiText.Format($"\nID:子ID：{targetEncoding.NumericId}:{targetEncoding.Metadata}") : "";
                return BuildDescription() + UiText.Format($"\n\n1.12.2 转换目标：\n{(mapping.Target is null ? UiText.Get("无法转换") : InspectionName(mapping.Target))}{targetId}\n{mapping.Target?.CanonicalKey}\n{mapping.Explanation}");
            });
        }
        catch(Exception exception)
        {
            if(!isClosing && generation == inspectionGeneration && sceneGeneration == previewGeneration) BlockInspectionText.Text = description + UiText.Format($"\n\n补充信息暂不可读：{exception.Message}");
        }
    }

    private void CopyBlockInspection_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(BlockInspectionText.Text); UiText.Bind(StatusText, "Text", UiText.Text("方块信息已复制。")); }
        catch(System.Runtime.InteropServices.ExternalException) { UiText.Bind(StatusText, "Text", UiText.Text("剪贴板正忙，请稍后重试。")); }
    }
}
