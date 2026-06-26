param(
    [string]$GameRoot = "C:\Game163\program",
    [string]$SourceRoot = "",
    [string]$SourceIndex = "D:\Assets\Naraka\SourceIndex_Full_HeaderFix1\unity_source_index.db",
    [string]$OutputRoot = "D:\Assets\Naraka\Naraka_FirstUsableSmoke_Current",
    [string]$Configuration = "Release",
    [switch]$KeepExisting,
    [switch]$SkipBrowserValidation,
    [switch]$SkipGltfValidation
)

$ErrorActionPreference = "Stop"

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Label,
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    Write-Host ""
    Write-Host "== $Label =="
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed with exit code $LASTEXITCODE"
    }
}

function Test-FileRequired {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    if (!(Test-Path -LiteralPath $Path)) {
        throw "$Label not found: $Path"
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$targetFramework = "net9.0-windows"
$cli = Join-Path $repoRoot "AnimeStudio.CLI\bin\$Configuration\$targetFramework\AnimeStudio.CLI.exe"

if (!(Test-Path -LiteralPath $cli)) {
    dotnet build (Join-Path $repoRoot "AnimeStudio.CLI\AnimeStudio.CLI.csproj") -c $Configuration
}

Test-FileRequired -Path $cli -Label "AnimeStudio CLI"

if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $streamingAssets = Join-Path $GameRoot "NarakaBladepoint_Data\StreamingAssets"
    if (Test-Path -LiteralPath $streamingAssets) {
        $SourceRoot = $streamingAssets
    }
    else {
        $SourceRoot = $GameRoot
    }
}

Test-FileRequired -Path $SourceRoot -Label "Naraka source root"
Test-FileRequired -Path $SourceIndex -Label "Naraka unity_source_index.db"

if (!$KeepExisting -and (Test-Path -LiteralPath $OutputRoot)) {
    Remove-Item -LiteralPath $OutputRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

$probeOutput = Join-Path $OutputRoot "SourceInputProbe"
$hadiOutput = Join-Path $OutputRoot "HadiBody_s9"

Invoke-Checked `
    -Label "Probe Naraka input" `
    -FilePath $cli `
    -Arguments @(
        $SourceRoot,
        $probeOutput,
        "--game", "Naraka",
        "--probe_source_input"
    )

# 这个样本来自完整源索引的确定性闭包，用来证明标准角色部件模型链路。
Invoke-Checked `
    -Label "Export Hadi body s9 smoke" `
    -FilePath $cli `
    -Arguments @(
        $SourceRoot,
        $hadiOutput,
        "--game", "Naraka",
        "--mode", "Library",
        "--group_assets", "ByLibrary",
        "--profile_3d", "Core",
        "--model_format", "Gltf",
        "--texture_mode", "Png",
        "--animation_package", "Separate",
        "--fbx_animation", "Skip",
        "--source_index", $SourceIndex,
        "--source_files", "4\c\4c08b7069a411750",
        "--path_ids", "-6619473669887381141"
    )

Invoke-Checked `
    -Label "Rebuild AssetLibrary v1 index" `
    -FilePath $cli `
    -Arguments @(
        "--build_sqlite_index", $hadiOutput,
        "--source_index", $SourceIndex,
        "--game", "Naraka",
        "--skip_sqlite_file_index"
    )

$manifest = Join-Path $hadiOutput "asset_library.json"
$db = Join-Path $hadiOutput "library_index.db"
$validation = Join-Path $hadiOutput "model_validation.json"
$gltf = Join-Path $hadiOutput "Models\assets\res\prefab\actor_visual_part\ch_m_hadi\ch_m_hadi_lv_s9\ch_m_hadi_lv_s9.gltf"

Test-FileRequired -Path $manifest -Label "asset_library.json"
Test-FileRequired -Path $db -Label "library_index.db"
Test-FileRequired -Path $validation -Label "model_validation.json"
Test-FileRequired -Path $gltf -Label "Hadi glTF"

if (!$SkipGltfValidation) {
    $gltfTransform = Get-Command "gltf-transform.cmd" -ErrorAction SilentlyContinue
    if ($null -ne $gltfTransform) {
        Invoke-Checked `
            -Label "Validate Hadi glTF" `
            -FilePath $gltfTransform.Source `
            -Arguments @("validate", $gltf)
    }
    else {
        Write-Warning "gltf-transform.cmd not found; skipped glTF validator."
    }
}

if (!$SkipBrowserValidation) {
    $browserCli = "D:\misutime\UnrealExporter\dist\AssetLibraryBrowser.Cli\AssetLibraryBrowser.Cli.exe"
    if (Test-Path -LiteralPath $browserCli) {
        Invoke-Checked `
            -Label "Validate AssetLibrary v1" `
            -FilePath $browserCli `
            -Arguments @("validate-library", $hadiOutput)

        Invoke-Checked `
            -Label "Render browser thumbnail" `
            -FilePath $browserCli `
            -Arguments @("build-thumbnails", $hadiOutput, "1", "1")
    }
    else {
        Write-Warning "AssetLibraryBrowser.Cli not found; skipped browser validation."
    }
}

$assetLibrary = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
$modelValidation = Get-Content -LiteralPath $validation -Raw | ConvertFrom-Json

Write-Host ""
Write-Host "Naraka first usable smoke completed."
Write-Host "Output: $OutputRoot"
Write-Host "Library: $hadiOutput"
Write-Host ("Capabilities: models={0}, animations={1}" -f $assetLibrary.capabilities.models, $assetLibrary.capabilities.animations)
if ($null -ne $modelValidation.totals) {
    $modelValidation.totals | Format-List
}
