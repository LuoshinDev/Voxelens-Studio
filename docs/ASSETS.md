# 素材来源 / Asset provenance

| Asset | Source and treatment |
| --- | --- |
| `src/ZhuJieJing.App/Assets/ZhuJieJing.png`, `.ico` | Rendered by `tools/Generate-AppIcon.ps1`. Layer geometry adapts [Lucide layers](https://github.com/lucide-icons/lucide/blob/main/icons/layers.svg); preserve [Lucide/Feather notices](../licenses/lucide.txt). Project colors and rendering are in the script. |
| `docs/branding/voxelens-studio-logo-transparent.png` | A 1024 × 1024 copy of the application PNG, with rounded corners and transparency, used in the README. The same Lucide/Feather notices apply. |
| `docs/branding/voxelens-studio-logo.png` | A 1024 × 1024 opaque community avatar rendered from the same application geometry with `tools/Generate-AppIcon.ps1 -Avatar -OutputDirectory docs/branding`. The same Lucide/Feather notices apply. |
| Inline WPF vector glyphs | Includes Lucide-style/adapted geometry. Preserve the same Lucide/Feather notices with distributions. |
| `src/ZhuJieJing.Renderer/Assets/StaticSky.png` | AI-generated static sky made for this project. Not extracted from a Minecraft JAR. Distributed as a project asset under the root MIT terms to the extent applicable. |
| `samples/*.zz` | Project blueprint examples, covered by the root MIT license. No player worlds are included. |
| `docs/images/*.png` | Application screenshots supplied by 洛神Field, with local paths redacted. Depicted Minecraft assets and world designs retain their respective rights; screenshots do not grant a license to those assets or distribute the underlying world/JAR. |
| Minecraft textures, models, resource JARs and worlds | User-provided local data. Excluded from source exports and release packages; no project license grant applies to these files. |

新增素材应记录制作方式、原始来源和许可证，不使用来源不明的截图、地图或材质包作为公开示例。生成素材的来源说明不代表商标或第三方权利审查。
