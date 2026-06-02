param(
    [string]$GameData = "C:\Program Files (x86)\Freedunk\Game\Freedunk_Data",
    [string]$OutputRoot = "D:\Assets\Freedunk_Data_Dev",
    [switch]$KeepMiniInput
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$cli = Join-Path $repoRoot "AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe"

if (!(Test-Path $cli)) {
    dotnet build (Join-Path $repoRoot "AnimeStudio.CLI\AnimeStudio.CLI.csproj")
}

if (!(Test-Path $GameData)) {
    throw "Game data folder not found: $GameData"
}

$assets = Join-Path $GameData "StreamingAssets\assets"
$miniInput = Join-Path $OutputRoot "CompleteMiniInput"
$output = Join-Path $OutputRoot "CompleteBodyAnimSample"

if (!$KeepMiniInput -and (Test-Path $miniInput)) {
    Remove-Item -LiteralPath $miniInput -Recurse -Force
}
if (Test-Path $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}

$files = @(
    "graphics\character\pc\bill_01_00.ab",
    "graphics\character\pc\bill_01_00\fbx.ab",
    "graphics\character\pc\bill_01_00\material_ingame.ab",
    "graphics\character\pc\bill_01_00\material.ab",
    "graphics\character\pc\bill_01_00\texture.ab",
    "graphics\character\animations\ingame\public\01_normal.ab"
)

foreach ($rel in $files) {
    $src = Join-Path $assets $rel
    $dst = Join-Path $miniInput $rel
    if (!(Test-Path $src)) {
        throw "Sample source file not found: $src"
    }
    New-Item -ItemType Directory -Force -Path (Split-Path $dst -Parent) | Out-Null
    Copy-Item -LiteralPath $src -Destination $dst -Force
}

& $cli `
    $miniInput `
    $output `
    --game Normal `
    --mode Library `
    --group_assets ByLibrary `
    --profile_3d Core `
    --model_format Gltf `
    --texture_mode Png `
    --animation_package Both `
    --fbx_animation All `
    --names "^Bill_01_00_ingame$|^Bill_01_00_outgame$|^NORMALMOVE_STAND_01$" `
    --batch_files 64 `
    --profile_log off `
    --silent

if ($LASTEXITCODE -ne 0) {
    throw "Complete body animation sample export failed."
}

$gltf = Get-ChildItem $output -Recurse -Filter "Bill_01_00_ingame.gltf" | Select-Object -First 1
if ($gltf) {
    $json = Get-Content $gltf.FullName -Raw | ConvertFrom-Json
    [pscustomobject]@{
        Path = $gltf.FullName
        Meshes = $json.meshes.Count
        Skins = $json.skins.Count
        Animations = $json.animations.Count
        Images = $json.images.Count
        Textures = $json.textures.Count
        Materials = $json.materials.Count
        AnimationNames = ($json.animations | ForEach-Object name) -join ", "
    } | Format-List
}

Write-Host ""
Write-Host "Complete sample exported to $output"
