using ZhuJieJing.Minecraft;

namespace ZhuJieJing.App.WorldPreview;

public static class NormalizedMinecraftChunkSourceFactory
{
    public static INormalizedMinecraftChunkSource Create(IReadOnlyMinecraftWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        return world.Descriptor.Version.StorageFamily switch
        {
            MinecraftStorageFamily.LegacyNumericAnvil => new LegacyNormalizedMinecraftChunkSource(world),
            MinecraftStorageFamily.FlattenedPalette or MinecraftStorageFamily.ModernSectionPalette =>
                new ModernNormalizedMinecraftChunkSource(world),
            _ => throw new NotSupportedException("无法确定该世界使用的 Anvil 方块存储格式。"),
        };
    }
}
