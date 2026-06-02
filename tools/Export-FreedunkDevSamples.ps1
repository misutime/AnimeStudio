param(
    [string]$GameData = "C:\Program Files (x86)\Freedunk\Game\Freedunk_Data",
    [string]$Output = "D:\Assets\Freedunk_Data_Dev\AnimeStudio_DevSamples",
    [switch]$KeepExisting
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

if (!$KeepExisting -and (Test-Path $Output)) {
    Remove-Item -LiteralPath $Output -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $Output | Out-Null

$assets = Join-Path $GameData "StreamingAssets\assets"
$jobs = @(
    @{
        Name = "character_bill_01_00"
        Input = Join-Path $assets "graphics\character\pc\bill_01_00"
        Args = @("--batch_files", "16")
    },
    @{
        Name = "trophy_props"
        Input = Join-Path $assets "graphics\trophy"
        Args = @("--batch_files", "16")
    },
    @{
        Name = "stage_models"
        Input = Join-Path $assets "graphics\stage\models.ab"
        Args = @()
    },
    @{
        Name = "global_shaders"
        Input = Join-Path $assets "graphics\shaders.ab"
        Args = @("--include_shaders")
    },
    @{
        Name = "stage_animations"
        Input = Join-Path $assets "graphics\stage\animation.ab"
        Args = @()
    },
    @{
        Name = "npc_animations"
        Input = Join-Path $assets "graphics\character\npc\prefab\animation_npc.ab"
        Args = @()
    }
)

foreach ($job in $jobs) {
    if (!(Test-Path $job.Input)) {
        Write-Warning "Skipping missing sample input: $($job.Input)"
        continue
    }

    Write-Host "Exporting sample: $($job.Name)"
    & $cli $job.Input $Output --game Normal --logger_flags Warning Error --profile_log off @($job.Args)
    if ($LASTEXITCODE -ne 0) {
        throw "Sample export failed: $($job.Name)"
    }
}

$catalog = Join-Path $Output "asset_catalog.jsonl"
if (Test-Path $catalog) {
    Write-Host ""
    Write-Host "Catalog summary:"
    Get-Content $catalog |
        ForEach-Object { $_ | ConvertFrom-Json } |
        Group-Object kind |
        Sort-Object Name |
        ForEach-Object { "{0}: {1}" -f $_.Name, $_.Count }
}

Write-Host ""
Write-Host "Dev samples exported to $Output"
