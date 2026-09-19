using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using ZhuJieJing.App.Components;
using ZhuJieJing.App.Projects;
using ZhuJieJing.Core;
using ZhuJieJing.Core.Collaboration;

namespace ZhuJieJing.App;

public partial class MainWindow
{
    partial void ConfigureComponentLibraryIntegration(ref Func<ComponentPlacementRequest, CancellationToken, Task>? handler)
        => handler = UseApprovedComponentAsync;

    private async Task UseApprovedComponentAsync(ComponentPlacementRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(isClosing) throw new OperationCanceledException("窗口正在关闭。", cancellationToken);
        if(ForegroundOperations().Any()) throw new InvalidOperationException("请先完成或取消当前任务，再放置组件。");
        if(aiProjectSession is not { } session)
        {
            await LoadPlanFileAsync(request.DraftPath, cancellationToken, propagateErrors: true);
            return;
        }
        if(string.IsNullOrWhiteSpace(aiProjectPreviewedRevision))
            throw new InvalidOperationException("请先等待当前工程预览完成，再放置组件。");
        if(!string.Equals(request.Plan.Dimension, session.Project.TargetDimension, StringComparison.Ordinal))
            throw new InvalidOperationException("组件放置维度与当前工程不一致。请重新选择当前工程维度。");
        ZjjVisualProfile? targetVisualProfile = session.Workspace.Manifest.Target.VisualProfile?.ToPlanProfile();
        if(targetVisualProfile is not null && targetVisualProfile != request.Plan.VisualProfile)
            throw new InvalidOperationException($"组件的材质版本与当前工程 Java {targetVisualProfile.VersionName ?? targetVisualProfile.DataVersion?.ToString() ?? "指定版本"} 不一致。请点击“复制 AI 制作要求”，按当前工程版本制作并重新审核候选后再使用。");
        string revision = aiProjectPreviewedRevision;
        string relative = $"{ZhujieProjectLayout.StagingRelativePath}/component-{Guid.NewGuid():N}.zz";
        string draft = session.Workspace.Layout.ResolveProjectPath(relative);
        ZjjPlan patch = request.Plan with { Base = new ZjjSceneReference { Scene = ZhujieProjectLayout.SceneStoreRelativePath, Revision = revision } };
        byte[] bytes = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] serialized = Encoding.UTF8.GetBytes(ZjjPlanJson.Serialize(patch));
            if(serialized.Length > ZhujieProjectWorkspace.MaximumPlanBytes) throw new InvalidDataException("组件放置草稿超过工程蓝图大小限制，请拆分组件。");
            _ = session.Workspace.ParseAndValidatePatch(serialized);
            return serialized;
        }, cancellationToken);
        if(isClosing || !ReferenceEquals(aiProjectSession, session)) throw new OperationCanceledException("当前 AI 工程已切换，请重新放置组件。", cancellationToken);
        await using(FileStream output = new(draft, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            await output.WriteAsync(bytes, cancellationToken);
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string instruction = $"使用我已审核的本地组件“{request.Entry.Metadata.Name}” v{request.Entry.Version}。" +
            $"草稿：{relative}；SHA-256：{hash}。锚点 ({request.Anchor.X}, {request.Anchor.Y}, {request.Anchor.Z})，旋转 {request.QuarterTurns * 90}°。" +
            "请读取当前工程 revision，检查放置冲突和出生安全；保留既有建筑，通过筑界镜 CLI 发布新的增量批次并等待预览回执。若 revision 已变化，基于最新版本重新验证，不能直接覆盖。";
        await session.QueueInstructionAsync(ZhujieInstructionKind.Instruction, instruction,
            new AiInstructionContextSnapshot(request.Plan.Dimension, request.Anchor, revision), cancellationToken);
        if(!isClosing && ReferenceEquals(aiProjectSession, session))
        {
            UiText.Bind(StatusText, "Text", UiText.Message($"组件“{request.Entry.Metadata.Name}”已作为施工指令排队，等待 AI 放置并发布预览。"));
            await QueueAiProjectRefreshAsync(session, AiProjectRefreshKind.Dashboard, false, false);
        }
    }
}
