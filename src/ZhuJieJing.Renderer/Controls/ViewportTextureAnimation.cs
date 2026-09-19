using Vortice.Direct3D11;
using Vortice.Mathematics;
using ZhuJieJing.Renderer.Minecraft;

namespace ZhuJieJing.Renderer.Controls;

public partial class VoxelViewport
{
    private void UpdateAtlasAnimations(ID3D11DeviceContext context)
    {
        if(!_enhancedLightingEnabled || _content != ViewportContent.Sections || _minecraftResources is null || _gpuAtlas is null || _resourceTransition is not null ||
           _gpuAtlas.Width != _minecraftResources.Atlas.Width || _gpuAtlas.Height != _minecraftResources.Atlas.Height) return;
        IReadOnlyList<MinecraftTextureAnimationUpdate> updates = _minecraftResources.Atlas.AdvanceAnimations(_animationClock.Elapsed.TotalSeconds, _gpuAtlas.Revision);
        foreach(MinecraftTextureAnimationUpdate update in updates)
        {
            context.UpdateSubresource(update.BgraPixels.AsSpan(), _gpuAtlas.Texture, 0, (uint)(update.Size * 4), 0,
                new Box(update.X, update.Y, 0, update.X + update.Size, update.Y + update.Size, 1));
        }
        if(updates.Count != 0) context.GenerateMips(_gpuAtlas.View);
    }
}
