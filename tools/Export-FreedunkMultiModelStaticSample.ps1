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
$miniInput = Join-Path $OutputRoot "MultiModelMiniInput"
$output = Join-Path $OutputRoot "MultiModelStaticSample"

if (!$KeepMiniInput -and (Test-Path $miniInput)) {
    Remove-Item -LiteralPath $miniInput -Recurse -Force
}
if (Test-Path $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}

$characters = @(
    "aimar_01_00",
    "asami_01_00",
    "bill_01_00",
    "diego_01_00",
    "duke_01_00",
    "emma_01_00",
    "frorin_01_00",
    "gary_01_00",
    "jimmy_01_00",
    "kent_01_00",
    "kiara_01_00",
    "lilly_01_00"
)

$files = New-Object System.Collections.Generic.List[string]
foreach ($character in $characters) {
    $files.Add("graphics\character\pc\$character.ab")
    $files.Add("graphics\character\pc\$character\fbx.ab")
    $files.Add("graphics\character\pc\$character\material_ingame.ab")
    $files.Add("graphics\character\pc\$character\material.ab")
    $files.Add("graphics\character\pc\$character\texture.ab")
}

foreach ($rel in $files) {
    $src = Join-Path $assets $rel
    $dst = Join-Path $miniInput $rel
    if (!(Test-Path $src)) {
        throw "Sample source file not found: $src"
    }
    New-Item -ItemType Directory -Force -Path (Split-Path $dst -Parent) | Out-Null
    Copy-Item -LiteralPath $src -Destination $dst -Force
}

$namePattern = "^(" + (($characters | ForEach-Object { [Regex]::Escape($_) }) -join "|") + ")"

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
    --names $namePattern `
    --batch_files 64 `
    --profile_log off `
    --silent

if ($LASTEXITCODE -ne 0) {
    throw "Multi model static sample export failed."
}

$validation = Join-Path $output "model_validation.json"
if (Test-Path $validation) {
    $json = Get-Content $validation -Raw | ConvertFrom-Json
    $json.totals | Format-List
    $json.models |
        Select-Object Status,
            @{Name = "Name"; Expression = { [IO.Path]::GetFileNameWithoutExtension($_.Path) }},
            @{Name = "Meshes"; Expression = { $_.Counts.Meshes }},
            @{Name = "Materials"; Expression = { $_.Counts.Materials }},
            @{Name = "Images"; Expression = { $_.Counts.Images }},
            @{Name = "Skins"; Expression = { $_.Counts.Skins }},
            @{Name = "Notes"; Expression = { $_.Notes -join "; " }} |
        Format-Table -AutoSize
}

Write-Host ""
Write-Host "Multi model static sample exported to $output"
