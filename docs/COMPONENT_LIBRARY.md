# 本地建筑组件库

在主窗口打开“组件库”，点击“复制 AI 制作要求”，把要求与想要的门楼、屋顶、树木、家具等描述一起交给 AI。按钮只写入本机剪贴板，不发送消息。要求默认沿用当前 AI 工程、蓝图或世界的 Minecraft 视觉版本；没有这些上下文时使用 Java 1.21.10。

AI 每种素材、每个备选方案分别交付一个独立 `.zz` 文件，统一写入 `%LocalAppData%\筑界镜 Studio\component-library\pending`。界面默认打开“待批准”，新文件自动出现，关闭窗口后仍然保留。选择候选后在原生 3D 视窗中检查材质、结构、尺寸和朝向；勾选确认并点击“批准并移入已批准”才会批准。支持保存名称、分类、标签、备注，或移至系统回收站。预览失败或候选被修改时不能沿用旧确认；点击刷新重新加载。

文件必须是无 BOM 的 UTF-8 JSON，`format` 为 `zhujie.plan/2`，`base` 必须明确为 `null`；使用 `fillBox`、`columns` 两类操作。独立组件建议局部坐标从 `(0,0,0)` 开始，`spawnPoint` 为 `null`，`visualProfile` 明确记录目标材质版本。不要省略材料的 `facing`、`axis`、`half`、`shape`、`rotation` 等属性，也不要把门、床、双高植物等成对方块拆掉。

建议单件尺寸在 64×64×64 内、少于 32,768 个可见方块。导入硬限制为 8 MiB、131,072 个非空气方块、1,048,576 次尝试写入，蓝图通用限制为 10,000 个操作。尺寸建议不等于必须填满范围。大型建筑应拆成可独立检查的组件。

最小示例是一块 3×1×3 石砖底座：

```json
{
  "format": "zhujie.plan/2",
  "planId": "stone-platform-candidate",
  "dimension": "minecraft:overworld",
  "visualProfile": { "edition": "java", "versionName": "1.21.10", "dataVersion": 4556, "storageFamily": "modernSectionPalette" },
  "base": null,
  "module": { "id": "component", "displayName": "石砖底座", "kind": "workZone" },
  "spawnPoint": null,
  "selections": {
    "component": { "min": { "x": 0, "y": 0, "z": 0 }, "maxExclusive": { "x": 3, "y": 1, "z": 3 } }
  },
  "materials": {
    "stone": { "exactState": { "name": "minecraft:stone_bricks", "properties": {} }, "allowAir": false }
  },
  "operations": [
    { "op": "fillBox", "id": "base", "selectionId": "component", "materialId": "stone", "writeStrategy": "overwrite",
      "bounds": { "min": { "x": 0, "y": 0, "z": 0 }, "maxExclusive": { "x": 3, "y": 1, "z": 3 } } }
  ]
}
```

独立候选以本文的组件规则为准，导入时由筑界镜 Core 严格解析与编译。通用方块状态、坐标与操作可参考 [AI 世界格式](AI_WORLD_FORMAT.md)，但该说明中的工程增量发布要求和工程 schema 不用于验证独立候选：候选明确使用 `base: null`，不依赖当前工程 revision。只有批准组件复用到工程时，工作区才生成带当前 revision 的放置草稿。

所有新组件、备选方案和修改版本，包括在现有 AI 工程中制作的独立组件，统一存放在上述 `pending` 目录的直系普通 `.zz` 文件。先完整写入 `.tmp` 再重命名，不覆盖旧候选。复制的 AI 制作要求会给出本机绝对路径。候选本身不发布为 revision；AI 不能代替用户批准，不能写入 `approved`、批准记录、MCA、NBT、`.zjjscene` 或 Minecraft 世界。

两个收藏目录分别为 `%LocalAppData%\筑界镜 Studio\component-library\pending`（待批准）和 `approved`（已批准）。从其他位置导入时会保存候选副本；批准内容发布成功后，仅移除与已检查内容一致的待批准文件，发生更新的文件继续保留。每次批准生成不可变版本，并记录源蓝图、源路径、SHA-256、批准时间、名称、分类、标签、备注和来源说明。导入新版候选会保留旧版本；搜索可按名称、分类、标签或备注查找，取消“只显示最新版本”可查看历史。

复用时选择一个批准版本，填写锚点和 0°/90°/180°/270° 旋转。锚点是旋转后包围盒的西北底角，放置草稿只包含非空气方块，采用冲突检查。当前有 AI 工程时会排队为施工指令，由 AI 检查最新 revision 和冲突后通过 CLI 发布；没有 AI 工程时作为独立蓝图打开。复用不直接更改源世界，也不代表最终导出。

组件的 `visualProfile` 与目标工程不匹配时会拒绝复用；应按目标 Minecraft 版本重新制作候选，并重新预览、审核和批准。
