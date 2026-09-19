using System.Numerics;

namespace ZhuJieJing.App;

public partial class MainWindow
{
    private int CurrentPreviewRenderYOffset =>
        displayedContent == DisplayedContent.ConvertedWorldSections && activeConversionPreview is not null
            ? activeConversionPreview.Summary.AppliedYOffset
            : 0;

    /// <summary>
    /// Converts the renderer's translated preview position back to the source world's coordinate space.
    /// UI coordinates, navigation requests, and comparison points always use this logical space.
    /// </summary>
    private Vector3 FromPreviewRenderPosition(Vector3 renderPosition) =>
        TranslatePreviewY(renderPosition, -((double)CurrentPreviewRenderYOffset));

    /// <summary>Applies the currently displayed conversion preview's internal Y translation.</summary>
    private Vector3 ToPreviewRenderPosition(Vector3 worldPosition) =>
        TranslatePreviewY(worldPosition, CurrentPreviewRenderYOffset);

    /// <summary>Applies a pending conversion preview's internal Y translation before it becomes active.</summary>
    private static Vector3 ToPreviewRenderPosition(Vector3 worldPosition, int yOffset) =>
        TranslatePreviewY(worldPosition, yOffset);

    private static Vector3 TranslatePreviewY(Vector3 position, double yOffset)
    {
        double translatedY = position.Y + yOffset;
        if(!double.IsFinite(translatedY) || translatedY is < -float.MaxValue or > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(position), "转换预览坐标超出可渲染范围。");
        return new Vector3(position.X, (float)translatedY, position.Z);
    }
}
