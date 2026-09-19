# 已知边界 / Known limitations

- 当前桌面发行目标为 Windows 11 x64；不是 Minecraft 客户端或完整游戏模拟器。
- Java 1.12.2 和 1.13+ 世界使用不同格式路径。现代版本、模组方块和方块实体的支持程度不同，不保证任何存档都能无损降级。
- 1.12.2 的高度为 Y=0–255。超高世界需要用户明确接受裁切；转换界面会说明偏移及损失。映射检查和 Minecraft 内验收都不能省略。
- 未知非空气方块保持可见并报告；简化模型、默认生物群系染色、方块实体外观等可能不同于原版游戏。随机模型变体使用确定性选择。
- 观察者支持自由移动，不等于完整的重力、碰撞和玩家实体模拟。参考地形不写入地图。
- 大世界采用区块流式加载，加载和网格重建可能产生帧时间波动；144 FPS 不是对所有地图和硬件的保证。
- 裁剪、清理和方块替换直接修改原地图，转换导出另存新目录。事务回滚不等于用户备份；修改前自行备份并关闭游戏和服务端。
- 部分底层诊断和 CLI 输出仍为中文。公开预览阶段欢迎报告漏译。
- 程序依赖用户自己的资源 JAR；不分发 Minecraft 原版资源。AI 协作协议可由外部工具使用，Studio 不内置云端模型账号或凭据。

## English

The desktop targets Windows 11 x64 and is not a full Minecraft client. Downgrade compatibility varies by version, block and block entity; out-of-range worlds require explicit clipping decisions. Rendering includes simplified entity models, default biome tinting and deterministic variant selection. Observer mode is free navigation, not full player physics.

Streaming and mesh rebuilds can cause frame-time spikes; no universal 144 FPS guarantee is made. Crop, cleanup and replacement modify the original world, while conversion exports to a new folder. Close Minecraft/server and make your own backup before in-place operations. Some low-level diagnostics and CLI output remain Chinese. Supply your own client JAR; no Minecraft game assets or cloud-model credentials are included.
