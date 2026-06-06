param(
    [string]$CliPath = "",
    [string]$OutputRoot = "D:\Assets\AS-Assets",
    [switch]$CleanOutput,
    [switch]$StopOnError
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($CliPath)) {
    $releaseDll = Join-Path $repoRoot "AnimeStudio.CLI\bin\Release\net9.0-windows\AnimeStudio.CLI.dll"
    $distExe = Join-Path $repoRoot "dist\net9.0-windows\AnimeStudio.CLI.exe"
    if (Test-Path -LiteralPath $releaseDll) {
        $CliPath = $releaseDll
    }
    else {
        $CliPath = $distExe
    }
}

if (-not (Test-Path -LiteralPath $CliPath)) {
    throw "AnimeStudio.CLI not found: $CliPath. Run dotnet build AnimeStudio.CLI\AnimeStudio.CLI.csproj -c Release -f net9.0-windows, or pass -CliPath."
}

$games = @(
    @{
        Name = "VRising"
        Input = "D:\BaiduNetdiskDownload\unity-VRising"
    },
    @{
        Name = "OldWorld"
        Input = "D:\BaiduNetdiskDownload\unity-Old World"
    },
    @{
        Name = "Humankind"
        Input = "D:\BaiduNetdiskDownload\unity-Humankind"
    },
    @{
        Name = "Valheim"
        Input = "D:\BaiduNetdiskDownload\unity-Valheim.Build.21981559"
    },
    @{
        Name = "Freedunk"
        Input = "C:\Program Files (x86)\Freedunk\Game"
    },
    @{
        Name = "Homura Hime"
        Input = "D:\BaiduNetdiskDownload\unity-Homura Hime"
    }
)

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

$summary = New-Object System.Collections.Generic.List[object]
$startedAll = Get-Date

foreach ($game in $games) {
    $name = [string]$game.Name
    $inputPath = [string]$game.Input
    $outputPath = Join-Path $OutputRoot "$name-Assets"

    if (-not (Test-Path -LiteralPath $inputPath)) {
        $message = "Input path not found: $inputPath"
        Write-Warning "[$name] $message"
        $summary.Add([pscustomobject]@{
            Game = $name
            Input = $inputPath
            Output = $outputPath
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

    if ($CleanOutput -and (Test-Path -LiteralPath $outputPath)) {
        Write-Host "[$name] Removing existing output: $outputPath"
        Remove-Item -LiteralPath $outputPath -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

    $started = Get-Date
    Write-Host ""
    Write-Host "============================================================"
    Write-Host "[$name] Export started: $started"
    Write-Host "[$name] Input : $inputPath"
    Write-Host "[$name] Output: $outputPath"
    Write-Host "============================================================"

    if ([System.IO.Path]::GetExtension($CliPath).Equals(".dll", [System.StringComparison]::OrdinalIgnoreCase)) {
        & dotnet $CliPath $inputPath $outputPath --game Normal
    }
    else {
        & $CliPath $inputPath $outputPath --game Normal
    }
    $exitCode = $LASTEXITCODE
    $ended = Get-Date
    $duration = $ended - $started

    if ($exitCode -eq 0) {
        Write-Host "[$name] Export finished successfully in $duration"
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
        Input = $inputPath
        Output = $outputPath
        Status = $status
        ExitCode = $exitCode
        StartedAt = $started.ToString("o")
        EndedAt = $ended.ToString("o")
        Duration = $duration.ToString()
        Message = $message
    })

    $summaryPath = Join-Path $OutputRoot "export_batch_summary.json"
    $summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

    if ($exitCode -ne 0 -and $StopOnError) {
        throw "[$name] Export failed. Summary written to $summaryPath"
    }
}

$endedAll = Get-Date
$totalDuration = $endedAll - $startedAll
$summaryPath = Join-Path $OutputRoot "export_batch_summary.json"
$summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

Write-Host ""
Write-Host "============================================================"
Write-Host "Batch export finished in $totalDuration"
Write-Host "Summary: $summaryPath"
Write-Host "============================================================"
