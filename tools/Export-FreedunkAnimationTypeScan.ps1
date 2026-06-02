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
$miniInput = Join-Path $OutputRoot "AnimationTypeMiniInput"
$output = Join-Path $OutputRoot "AnimationTypeScan"

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
    "graphics\character\animations\ingame\public\01_normal.ab",
    "graphics\character\animations\ingame\public\02_dribble.ab",
    "graphics\character\animations\ingame\public\04_receive.ab",
    "graphics\character\animations\ingame\public\05_pass.ab",
    "graphics\character\animations\ingame\public\06_rebound.ab",
    "graphics\character\animations\ingame\public\07_block.ab",
    "graphics\character\animations\ingame\public\08_steal.ab",
    "graphics\character\animations\ingame\public\11_hopstep.ab",
    "graphics\character\animations\ingame\public\12_tipin.ab",
    "graphics\character\animations\outgame\idle.ab",
    "graphics\character\animations\outgame\lobby.ab",
    "graphics\character\animations\outgame\potential.ab",
    "graphics\character\animations\outgame\ballroom.ab",
    "graphics\character\animations\lobby\bill.ab",
    "graphics\character\animations\lobby\asami.ab",
    "graphics\character\animations\lobby\gary.ab"
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
    --animation_package Separate `
    --fbx_animation Skip `
    --batch_files 128 `
    --profile_log off `
    --silent

if ($LASTEXITCODE -ne 0) {
    throw "Animation type scan failed."
}

$catalog = Join-Path $output "asset_catalog.jsonl"
if (Test-Path $catalog) {
    $animations = Get-Content $catalog |
        ForEach-Object { $_ | ConvertFrom-Json } |
        Where-Object { $_.kind -eq "AnimationClip" }

    Write-Host ""
    Write-Host "Animation type summary:"
    $animations |
        Group-Object animationType |
        Sort-Object Name |
        ForEach-Object { "{0}: {1}" -f $_.Name, $_.Count }

    Write-Host ""
    Write-Host "Transform body candidates:"
    $animations |
        Where-Object { $_.animationType -eq "TransformBodyAnimation" -or $_.coreTransformBindingCount -ge 6 } |
        Select-Object -First 20 name, animationType, coreTransformBindingCount, humanoidBindingCount, sourceContainer |
        Format-Table -AutoSize

    Write-Host ""
    Write-Host "Humanoid/mixed sample:"
    $animations |
        Where-Object { $_.animationType -like "*Humanoid*" } |
        Select-Object -First 20 name, animationType, hasMuscleClip, coreTransformBindingCount, humanoidBindingCount, sourceContainer |
        Format-Table -AutoSize
}

Write-Host ""
Write-Host "Animation type scan exported to $output"
