param(
    [string]$CliPath = "",
    [string]$OutputRoot = "D:\Assets\AS-Assets",
    [switch]$StopOnError
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($CliPath)) {
    $candidates = @(
        (Join-Path $repoRoot "dist\net9.0-windows\AnimeStudio.CLI.exe"),
        (Join-Path $repoRoot "AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe"),
        (Join-Path $repoRoot "AnimeStudio.CLI\bin\Release\net9.0-windows\AnimeStudio.CLI.exe")
    )

    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate)) {
            continue
        }

        $help = & $candidate --help 2>$null
        if ($LASTEXITCODE -eq 0 -and (($help -join "`n") -like "*--migrate_library_relative_paths*")) {
            $CliPath = $candidate
            break
        }
    }

    if ([string]::IsNullOrWhiteSpace($CliPath)) {
        $CliPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
}

if (-not (Test-Path -LiteralPath $CliPath)) {
    throw "AnimeStudio.CLI not found: $CliPath. Run .\build.ps1, or pass -CliPath."
}

$games = @(
    @{
        Name = "Humankind"
    },
    @{
        Name = "HomuraHime"
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
    $libraryPath = Join-Path $OutputRoot "$name-Assets"

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
    Write-Host "[$name] Relative path migration started: $started"
    Write-Host "[$name] Library: $libraryPath"
    Write-Host "============================================================"

    if ([System.IO.Path]::GetExtension($CliPath).Equals(".dll", [System.StringComparison]::OrdinalIgnoreCase)) {
        & dotnet $CliPath --migrate_library_relative_paths $libraryPath
    }
    else {
        & $CliPath --migrate_library_relative_paths $libraryPath
    }
    $exitCode = $LASTEXITCODE
    $ended = Get-Date
    $duration = $ended - $started

    if ($exitCode -eq 0) {
        Write-Host "[$name] Migration finished successfully in $duration"
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

    $summaryPath = Join-Path $OutputRoot "relative_path_migration_batch_summary.json"
    $summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

    if ($exitCode -ne 0 -and $StopOnError) {
        throw "[$name] Relative path migration failed. Summary written to $summaryPath"
    }
}

$endedAll = Get-Date
$totalDuration = $endedAll - $startedAll
$summaryPath = Join-Path $OutputRoot "relative_path_migration_batch_summary.json"
$summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

Write-Host ""
Write-Host "============================================================"
Write-Host "Batch relative path migration finished in $totalDuration"
Write-Host "Summary: $summaryPath"
Write-Host "============================================================"
