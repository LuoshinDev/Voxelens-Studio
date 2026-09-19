#Requires -Version 7.0
[CmdletBinding()]
param([string]$DotNet = 'dotnet', [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/release'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$rootPath = Split-Path -Parent $PSScriptRoot
$project = Join-Path $rootPath 'src/ZhuJieJing.App/ZhuJieJing.App.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if($version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') { throw 'Invalid release version.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$packageName = "Voxelens-Studio-$version-win-x64"
$archive = Join-Path $output "$packageName.zip"
if(Test-Path -LiteralPath $archive) { throw 'Release ZIP already exists; use a new output directory or version.' }
$publishDirectory = Join-Path $output ($packageName + '-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
Push-Location $rootPath
try {
    & $DotNet publish $project -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $publishDirectory
    if($LASTEXITCODE -ne 0) { throw 'Studio publish failed.' }
    Copy-Item -LiteralPath (Join-Path $rootPath 'docs/QUICK_START.txt') -Destination (Join-Path $publishDirectory 'README.txt')
    foreach($required in @('ZhuJieJing.Studio.exe', 'zjj.exe', 'README.txt', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'licenses/lucide.txt', 'licenses/sqlitepclraw.txt', 'licenses/dotnet-runtime.txt', 'licenses/dotnet-third-party-notices.txt', 'languages/en-US.json', 'languages/zh-CN.json')) {
        if(!(Test-Path -LiteralPath (Join-Path $publishDirectory $required) -PathType Leaf)) { throw "Missing release file: $required" }
    }
    $unwanted = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File | Where-Object { $_.Extension -in @('.jar', '.mca', '.mcc', '.zjjscene', '.pdb', '.pfx', '.key', '.log') -or $_.Name -in @('studio-settings.json', 'preview-settings.json', 'open-locations.json', 'map-tools-window.json') })
    if($unwanted.Count) { throw 'Release contains excluded resources, symbols or user data.' }
    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archive -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $packageName.zip" | Set-Content -LiteralPath ($archive + '.sha256') -Encoding utf8
    [pscustomobject]@{Version=$version; Directory=$publishDirectory; Archive=$archive; Sha256=$hash}
} finally { Pop-Location }
