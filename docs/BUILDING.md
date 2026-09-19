# 构建 / Building

## 环境

- Windows 11 x64，支持 Direct3D 11 的显卡。
- `global.json` 指定的 .NET SDK。
- PowerShell 7；首次恢复 NuGet 依赖需要联网。
- 编译不需要 Minecraft 客户端或世界文件，材质预览时再选择自己的资源 JAR。

## 编译与运行

在仓库根目录执行：

```powershell
dotnet restore ZhuJieJing.sln
dotnet build ZhuJieJing.sln -c Release --no-restore
dotnet run --project src/ZhuJieJing.App -c Release --no-build
```

解决方案包含桌面应用、CLI 和所需的五个库。修改渲染着色器后，可用 `pwsh ./tools/Compile-ViewportShaders.ps1` 检查 HLSL 编译。

## 打包

```powershell
pwsh ./tools/Publish-Studio.ps1
```

在 `artifacts/release/` 生成 Windows x64 自包含 ZIP、SHA-256 校验文件和可运行目录。包内包含 .NET 运行时和 `zjj` CLI；脚本不自动安装或上传。重复打包请指定新的输出目录：

```powershell
pwsh ./tools/Publish-Studio.ps1 -OutputDirectory ./artifacts/release-next
```

发布包应保留 `LICENSE`、`THIRD_PARTY_NOTICES.md`、`licenses/` 和 `languages/`。不附带 Minecraft 资源 JAR、用户地图、日志和个人设置。

## CLI 与工程目录

```powershell
dotnet run --project src/ZhuJieJing.Cli -- --help
```

AI 工程默认位于系统“文档”下的 `Voxelens Studio/Projects`；可用环境变量 `ZJJ_PROJECTS_ROOT` 或 CLI 的 `--projects-root` 指定目录。旧版安装继续兼容已有工程位置。

`ZhuJieJing.*` 程序集、`ZhuJieJing.Studio.exe`、`zjj`、`.zz` 和 `.zjjscene` 是实际使用的名称与格式。

## English

Use Windows 11 x64, a Direct3D 11-capable GPU, PowerShell 7 and the SDK specified by `global.json`. Run the restore/build/run commands above from the repository root. Minecraft resources are not needed to compile.

The solution contains the desktop app, CLI and five libraries. `tools/Publish-Studio.ps1` creates a self-contained Windows ZIP and SHA-256 checksum under `artifacts/release/`. For another package, supply a new output directory. Keep licenses and language files with distributions; do not include Minecraft archives or personal worlds/settings.

Use the CLI help command above for blueprint commands. AI projects default to Documents/Voxelens Studio/Projects; `ZJJ_PROJECTS_ROOT` and `--projects-root` override the location. Existing installations retain their compatible workspace settings.
