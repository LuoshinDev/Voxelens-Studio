<p align="center">
  <img src="docs/branding/voxelens-studio-logo.png" alt="Voxelens Studio logo" width="128" height="128">
</p>

<h1 align="center">Voxelens Studio</h1>

[简体中文](README.zh-CN.md) · English

[![QQ交流群：309702265](https://img.shields.io/badge/QQ%E4%BA%A4%E6%B5%81%E7%BE%A4-309702265-555555?style=for-the-badge&logo=qq&logoColor=white&labelColor=12B7F5)](https://qm.qq.com/q/IQmVK34dKs)

**A modern Minecraft map tool.**

Voxelens Studio brings world browsing, first-person exploration, modern-to-1.12.2 conversion, visual cropping and world-wide block replacement into one native Windows desktop interface. View builds with Minecraft materials, choose blocks visually and compare conversion previews to make inspecting, organizing and editing maps more intuitive.

**Preview software · Windows 11 x64 · .NET 10 / WPF / Direct3D 11 · MIT**

**[Download for Windows (portable ZIP)](https://github.com/LuoshinDev/Voxelens-Studio/releases/download/v0.5.43/Voxelens-Studio-0.5.43-win-x64.zip)** · [Release notes](https://github.com/LuoshinDev/Voxelens-Studio/releases/tag/v0.5.43)

No programming or developer tools required. **Extract all files**, then double-click `ZhuJieJing.Studio.exe`. Choose the Windows ZIP rather than GitHub's automatically generated “Source code” archives.

![A Minecraft build viewed in Voxelens Studio](docs/images/world-overview.png)

From inspecting a single build to organizing an entire world: explore the layout from above, then switch to **first-person movement** to look around inside it. Related block candidates are ranked automatically, while replacement handles compatible orientation and structure states to reduce repetitive setup.

## What it does

- Switches between orbit viewing and first-person observer navigation, with WASD movement, mouse look and adjustable movement speed.
- Streams Java Anvil worlds by region, chunk and section, including legacy 1.12.2 and modern palette formats.
- Previews Minecraft materials from your own local client JAR, with optional enhanced lighting, shadows and water reflections.
- Inspects block names, canonical states, legacy ID:data and textures; compares original and converted previews.
- Converts modern worlds to 1.12.2 with visual block mappings, custom replacement rules and previews before export.
- Crops worlds, removes eligible empty chunks and replaces block types across an entire world.
- Supports `.zz` blueprints, versioned `.zjjscene` projects, a component approval library and the `zjj` CLI for external AI tools.
- Provides English, Simplified Chinese and editable language files. Resource paths, map views and window preferences persist locally.

## Features in detail

### Modern worlds to 1.12.2: choose replacements, then preview

Convert a modern world to 1.12.2. When a block does not exist in the older version, use the built-in mapping or choose a replacement that better matches the build's colors and materials.

- **Block mapping table:** inspect each modern block and its 1.12.2 replacement, with textures, names, legacy ID:data and counts in the current world.
- **Search and filtering:** find source blocks or target rules; filter to blocks used in the current world.
- **Custom mappings:** pick a target from block icons, compare the built-in rule with your choice, restore defaults when needed, then save and apply.
- **Automatic candidate ranking:** selecting a source reorders the target list. The active and built-in targets come first; other candidates are ranked by shape, material, color names and state matches. Slabs, for example, favor related slab candidates.
- **Less repetition:** orientation variants are grouped instead of repeating identical thumbnails. Sources used in the current world come first, with higher-count blocks prioritized.
- **Find a location:** jump to an occurrence of the selected block and judge a replacement in the context of the build.
- **Legacy preview:** switch between the original world and the 1.12.2 preview to inspect material and shape changes before export.
- **Conversion comparison:** compare original and converted views with synchronized cameras.
- **Height handling and export:** 1.12.2 supports Y=0–255. Review height offsets and explicitly accept top clipping when needed. Conversion writes a new world directory and keeps the original.

![1.12.2 block mapping table with source counts, built-in rules and custom targets](docs/images/block-mapping.png)

Mappings address block compatibility and visual tradeoffs. They do not guarantee lossless conversion of modded content, block entities or modern game mechanics; inspect exported worlds in Minecraft.

Ranking helps selection without overriding your choice. Saved custom mappings are reused for later conversions; an existing conversion preview is regenerated with the updated rules.

### Large-world browsing and first-person movement

Worlds stream in by chunk, with configurable chunk limits and loading workers. The sidebar shows the world version, block count, dimensions, dimension entries and region files to help navigate the data being viewed.

Use **Orbit** to examine the layout or **Observer** to move freely inside a build in first person. Controls include WASD, mouse look, vertical movement, adjustable speed and field of view, coordinate jumps and returning to spawn. Saved camera settings are restored when reopening a map. See the controls below. Observer mode is free navigation, without full game gravity or collision simulation.

### Materials, lighting and water reflections

Load textures from your local Minecraft resource JAR and preview a build with optional enhanced lighting, shadows and water reflections. Lighting presets and time/weather controls help inspect a scene under different conditions.

Enhanced graphics is an independent toggle; choosing a lighting preset does not force it on. Clouds, rain, fog and chunk/superflat/transparent terrain backgrounds are preview settings and do not alter world terrain. Save a screenshot directly from the viewport.

![Close-up of a colorful build and its reflection in water](docs/images/water-reflections.png)

### Visual world cropping

Select the area to keep on a top-down map, refine the chunk bounds and review the retained/deleted chunk counts before applying the crop.

Cropping affects the current dimension and removes chunks and associated entity/POI data outside the selection. An overworld selection must include spawn. Use it to keep a build area from a larger map; applying the crop modifies the original world.

![Top-down map with a rectangular crop selection and chunk counts](docs/images/world-cropping.png)

### Replace blocks across a world

Choose the source and replacement from block icons. Source blocks are grouped by type and sorted by count; related targets are ranked by type, material and color. Compatible orientation and structure states are carried over automatically.

![World-wide block replacement with source and target block icons](docs/images/block-replacement.png)

Replacement covers saved chunks in every dimension, including chunks not currently loaded in the viewport, and updates the original world. Compatible state is preserved for related types, such as orientation and structure when replacing one door with another; unrelated target types receive appropriate states automatically.

| Detail | How Studio handles it |
| --- | --- |
| Find the biggest changes first | Source types are ordered by total count, combining their state variants. |
| Find a suitable target | Selecting a source immediately reranks targets, prioritizing type and considering material, dye color, name and average texture color. Color similarity does not outweigh a clear type difference. |
| Replace every orientation | Choose a block type once; compatible target states are resolved for each source block. |
| Preserve structural details | Door-to-door replacement preserves compatible direction, halves and open state; stairs, slabs and pillars follow their own state-transfer rules. |
| Avoid incomplete structures | A regular single block cannot be replaced directly with a two-block door. Unsupported structures are reported and blocked. |
| Review the scope before writing | Confirm the affected block count and dimensions before applying. Recommendations do not execute replacements automatically. |
| Continue with another replacement | The current scene refreshes on completion; scan again for the next replacement. |

### World cleanup

Analyze eligible chunks first, then confirm removal. Cleanup targets eligible empty chunks without block entities or active data and keeps natural terrain; an area without player buildings is not automatically disposable. Back up and close the game/server before cropping, cleanup or replacement.

### Block inspector

Enable **Inspect** and click a block to see its Chinese/English names, Minecraft ID, state properties, texture, coordinates, dimension and lighting. Legacy blocks include numeric ID and metadata; conversion information shows the 1.12.2 target. Copy the details when reporting an incorrect block or mapping.

### AI blueprints and component library

External AI tools can work with Studio through `.zz` blueprints and the `zjj` CLI, with scene changes previewed and reviewed before application. The component library separates **Pending approval** from **Approved**; newly created content starts pending for centralized review. `.zjjscene` projects store scenes and version history. No cloud-model account or credentials are built in. See the [blueprint format](docs/AI_WORLD_FORMAT.md) and [component library](docs/COMPONENT_LIBRARY.md).

### Languages, resources and performance logs

- **Languages:** switch instantly between English and Chinese; edit or add language files in the installation's `languages/` directory.
- **Resources:** select or auto-detect separate legacy and modern client resource JARs. Saved selections persist across launches.
- **Preferences:** last-opened locations are remembered by file type, camera settings and field of view by map, and the map-tools window remembers its size.
- **Debug:** record performance information and see the saved status. The adjacent **Logs** button opens the log folder inside the software directory for reporting or manual cleanup.

<details>
<summary>See the home screen and preview controls</summary>

![Home demo, terrain options and preview controls](docs/images/home-preview.png)

</details>

Screenshots supplied by the project author show the Chinese interface. The application also supports English. The pictured world and Minecraft resource files are not bundled with the project.

## Move around a map

Choose **Observer** under Preview mode, then click inside the viewport to control the camera. Use **WASD** to move and the **mouse** to look around; press **Esc** to leave observer mode. Adjust **Movement speed** and **Field of view** on the right. Map camera settings, including field of view, are saved per map.

For an overview, choose **Orbit**: drag with the left mouse button to rotate, drag with the right button to pan, and scroll to zoom. Coordinate and spawn-point controls let you reach a specific location directly.

## Start using it

Unpack a Windows x64 release and run `ZhuJieJing.Studio.exe`. The self-contained package includes the .NET runtime. Keep the accompanying `languages/`, `licenses/` and notice files.

1. Open **Settings** and choose your language.
2. Select your own Minecraft client resource JAR for 1.12.2 and/or modern versions. Studio caches the selection; you do not need to reselect it next launch.
3. Open a world folder, legacy `.schematic` or `.zz` blueprint.
4. Inspect the preview before making changes. Missing resources fall back to solid colors.

**World operations:** reading and previewing are read-only; conversion exports to a new folder. Crop, cleanup and block replacement modify the original world after confirmation. Back up the world and close Minecraft/server before applying those tools. Transaction recovery is not a backup.

Minecraft client archives, original game textures and private worlds are not included. Voxelens Studio is an independent project, not an official Minecraft product and not approved by or associated with Mojang or Microsoft.

## Build from source

On Windows, install the SDK specified in [global.json](global.json), then run:

```powershell
dotnet restore ZhuJieJing.sln
dotnet build ZhuJieJing.sln -c Release --no-restore
dotnet run --project src/ZhuJieJing.App -c Release --no-build
```

Create a self-contained release with `pwsh ./tools/Publish-Studio.ps1`. See [Building](docs/BUILDING.md) for prerequisites, output files and workspace configuration.

## Project layout

| Directory | Responsibility |
| --- | --- |
| `src/ZhuJieJing.Core` | Canonical states, blueprints, scene commands and collaboration protocol |
| `src/ZhuJieJing.Minecraft` | Anvil/NBT, normalization, conversion and transactional world operations |
| `src/ZhuJieJing.Renderer` | Direct3D 11 meshes, textures, lighting and camera |
| `src/ZhuJieJing.SceneStore` | Scene persistence, revisions and history |
| `src/ZhuJieJing.Assets` | Local resource archives and overlays |
| `src/ZhuJieJing.App` / `ZhuJieJing.Cli` | Desktop UI / command-line interface |
| `samples`, `schemas` | A blueprint example and format schemas |

The `ZhuJieJing` assembly names and existing file formats remain for compatibility. They are not separate products.

## Documentation

- [Known limitations](docs/KNOWN_LIMITATIONS.md)
- [Custom languages](src/ZhuJieJing.App/Localization/README.md)
- [Architecture](docs/ARCHITECTURE.md) · [Blueprint format](docs/AI_WORLD_FORMAT.md) · [Component library](docs/COMPONENT_LIBRARY.md)
- [Changelog](CHANGELOG.md)
- [Contributing](CONTRIBUTING.md) · [Security](SECURITY.md)

Some low-level diagnostics and CLI output remain Chinese. Conversion fidelity and performance depend on the source world and hardware; see the documented limits and validate exported worlds in Minecraft.

## License and credits

Copyright (c) 2026 洛神Field. Original project code is available under the [MIT License](LICENSE). Third-party dependencies and adapted assets retain their own licenses; see [Third-party notices](THIRD_PARTY_NOTICES.md) and [asset provenance](docs/ASSETS.md).
