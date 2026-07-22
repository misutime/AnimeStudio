param(
    [string]$GameData = "C:\Program Files (x86)\Freedunk\Game\Freedunk_Data",
    [string]$OutputRoot = "D:\Assets\Freedunk_Data_Dev"
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
$output = Join-Path $OutputRoot "MultiModelStaticSample"
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

$namePattern = "^(" + (($characters | ForEach-Object { [Regex]::Escape($_) }) -join "|") + ")"
$containerPattern = "graphics[\\/]character[\\/]pc[\\/](" + (($characters | ForEach-Object { [Regex]::Escape($_) }) -join "|") + ")([\\/]|\.|$)"

& $cli `
    $assets `
    $output `
    --game Normal `
    --mode Library `
    --group_assets ByLibrary `
    --profile_3d Core `
    --model_format Gltf `
    --texture_mode Png `
    --animation_package Skip `
    --fbx_animation Skip `
    --names $namePattern `
    --containers $containerPattern `
    --map_name Freedunk_Data_Dev_full_v2 `
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
