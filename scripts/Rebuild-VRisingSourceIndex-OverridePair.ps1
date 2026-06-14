param(
    [string]$SourceRoot = "D:\BaiduNetdiskDownload\unity-VRising",
    [string]$OutputDir = "F:\Unity-AS-Assets\VRising-Assets\SourceIndexRebuild_OverridePair_20260614",
    [int]$BatchFiles = 16
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $SourceRoot)) {
    throw "找不到 VRising Unity 源目录: $SourceRoot"
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "AnimeStudio.CLI\AnimeStudio.CLI.csproj"
$profileLog = Join-Path $OutputDir "source_index_profile.jsonl"
$indexPath = Join-Path $OutputDir "unity_source_index.db"

dotnet run --project $project -c Debug --framework net9.0-windows -- `
    $SourceRoot `
    $OutputDir `
    --game Normal `
    --build_source_sqlite_index `
    --batch_files $BatchFiles `
    --profile_log $profileLog

dotnet run --project $project -c Debug --framework net9.0-windows -- `
    --verify_source_index $indexPath `
    --require_fresh_source_animation_relations
