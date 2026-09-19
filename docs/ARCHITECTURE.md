# Architecture / 架构

Voxelens Studio 是基于 .NET、WPF 和 Direct3D 11 的原生 Windows 应用。

## 模块

| 模块 | 职责 |
| --- | --- |
| `ZhuJieJing.Core` | 规范方块状态、蓝图、选择范围、场景命令与变更 |
| `ZhuJieJing.Minecraft` | 世界索引、Anvil/NBT、版本适配、转换和地图写入 |
| `ZhuJieJing.Assets` | 本地资源目录、ZIP/JAR 和资源覆盖层 |
| `ZhuJieJing.Renderer` | Section 网格、图集、Direct3D 11 绘制、光照和相机 |
| `ZhuJieJing.SceneStore` | SQLite 场景保存、revision 与历史 |
| `ZhuJieJing.App` | 桌面界面、后台任务和交互流程 |
| `ZhuJieJing.Cli` | `zjj` 命令行与外部蓝图协作 |

## 世界浏览

世界按 Region 建立索引，按 Chunk 读取，再将 Legacy ID:data 或 Modern Palette 规范化为 Section。渲染器只构建与更新所需的 Section 网格，不展开整张世界为稠密数组。网格、贴图和光照缓存可重新生成，不作为地图事实数据。

本地客户端 JAR 提供 blockstate、模型和贴图。资源层按需读取，渲染层负责不透明、镂空与半透明材质。规范方块身份包含名称和完整状态属性，不能只用数字主 ID 或名称代替。

## 地图修改

浏览和转换预览只读。转换导出写入新目录；裁剪、清理和全地图替换在用户确认后修改原世界。写入使用暂存、校验、提交与失败恢复，提交前检查来源是否变化。用户仍需自行备份地图。

未知的非空气方块应保持可见并报告问题；不以静默转换为空气掩盖不兼容内容。

## 蓝图与场景

`.zz` 是声明式 JSON 蓝图，经 Core 验证与确定性编译后生成场景变更。`.zjjscene` 保存规范场景和 revision，渲染结果由这些数据重建。外部工具使用 CLI 和协作协议，不直接修改场景数据库或 Minecraft 世界文件。格式见 [AI 世界格式](AI_WORLD_FORMAT.md)。

## English

The app separates UI orchestration, canonical scene data, Minecraft file adaptation, local resources, rendering and persistence. Worlds are indexed by region, read by chunk and rendered by section. Rebuildable rendering caches remain separate from authoritative block state.

Browsing is read-only. Conversion exports to a new folder; confirmed crop, cleanup and replacement operations update the original world through staged, validated transactions. Unknown non-air content remains visible and is reported. Blueprints are validated JSON, while `.zjjscene` stores canonical state and revisions.
