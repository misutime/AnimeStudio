param(
    [string]$CliPath = "",
    [string]$OutputRoot = "D:\Assets\AS-Assets",
    [switch]$StopOnError
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($CliPath)) {
    $distExe = Join-Path $repoRoot "dist\net9.0-windows\AnimeStudio.CLI.exe"
    $releaseExe = Join-Path $repoRoot "AnimeStudio.CLI\bin\Release\net9.0-windows\AnimeStudio.CLI.exe"
    if (Test-Path -LiteralPath $distExe) {
        $CliPath = $distExe
    }
    else {
        $CliPath = $releaseExe
    }
}

if (-not (Test-Path -LiteralPath $CliPath)) {
    throw "AnimeStudio.CLI not found: $CliPath. Run .\build.ps1, or pass -CliPath."
}

$cliDir = Split-Path -Parent $CliPath
$runtimeConfig = [System.IO.Path]::ChangeExtension($CliPath, ".runtimeconfig.json")
$distRuntimeConfig = Join-Path (Join-Path $cliDir "bin") "AnimeStudio.CLI.runtimeconfig.json"
if (-not (Test-Path -LiteralPath $runtimeConfig) -and -not (Test-Path -LiteralPath $distRuntimeConfig)) {
    throw "AnimeStudio.CLI runtimeconfig not found near: $CliPath. Run .\build.ps1 and use dist\net9.0-windows\AnimeStudio.CLI.exe, or pass a complete CLI path."
}

# 与 Export-ASAssets-Games.ps1 保持同一批启用游戏。
# 这里重建的是已导出素材库索引，所以只需要输出目录，不需要源 Unity 目录。
$games = @(
    @{
        Name = "Humankind"
    },
    @{
        Name = "Homura Hime"
        OutputName = "HomuraHime"
    },
    @{
        Name = "Freedunk"
    },
    @{
        Name = "OldWorld"
    },
    @{
        Name = "YuanShen"
    }
)

$summary = New-Object System.Collections.Generic.List[object]
$startedAll = Get-Date

foreach ($game in $games) {
    $name = [string]$game.Name
    $outputName = if ($game.ContainsKey("OutputName")) { [string]$game.OutputName } else { $name }
    $libraryPath = Join-Path $OutputRoot "$outputName-Assets"

    if (-not (Test-Path -LiteralPath $libraryPath)) {
        $message = "Library path not found: $libraryPath"
        Write-Warning "[$name] $message"
        $summary.Add([pscustomobject]@{
            Game = $name
            Library = $libraryPath
            Status = "Skipped"
            ExitCode = $null
            StartedAt = $null
            EndedAt = $null
            Duration = $null
            Message = $message
        })
        if ($StopOnError) { throw $message }
        continue
    }

    $started = Get-Date
    Write-Host ""
    Write-Host "============================================================"
    Write-Host "[$name] Rebuild indexes started: $started"
    Write-Host "[$name] Library: $libraryPath"
    Write-Host "============================================================"

    if ([System.IO.Path]::GetExtension($CliPath).Equals(".dll", [System.StringComparison]::OrdinalIgnoreCase)) {
        & dotnet $CliPath --rebuild_library_indexes $libraryPath
    }
    else {
        & $CliPath --rebuild_library_indexes $libraryPath
    }
    $exitCode = $LASTEXITCODE
    $ended = Get-Date
    $duration = $ended - $started

    if ($exitCode -eq 0) {
        Write-Host "[$name] Rebuild indexes finished successfully in $duration"
        $status = "Succeeded"
        $message = ""
    }
    else {
        $status = "Failed"
        $message = "AnimeStudio.CLI exited with code $exitCode"
        Write-Warning "[$name] $message"
    }

    $summary.Add([pscustomobject]@{
        Game = $name
        Library = $libraryPath
        Status = $status
        ExitCode = $exitCode
        StartedAt = $started.ToString("o")
        EndedAt = $ended.ToString("o")
        Duration = $duration.ToString()
        Message = $message
    })

    $summaryPath = Join-Path $OutputRoot "rebuild_indexes_batch_summary.json"
    $summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

    if ($exitCode -ne 0 -and $StopOnError) {
        throw "[$name] Rebuild indexes failed. Summary written to $summaryPath"
    }
}

$endedAll = Get-Date
$totalDuration = $endedAll - $startedAll
$summaryPath = Join-Path $OutputRoot "rebuild_indexes_batch_summary.json"
$summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

Write-Host ""
Write-Host "============================================================"
Write-Host "Batch rebuild indexes finished in $totalDuration"
Write-Host "Summary: $summaryPath"
Write-Host "============================================================"
