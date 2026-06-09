param(
    [string]$CliPath = "",
    [string]$OutputRoot = "D:\Assets\AS-Assets",
    [switch]$CleanOutput,
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

$games = @(
    @{
        Name = "VRising"
        Input = "D:\BaiduNetdiskDownload\unity-VRising"
         Output = "D:\Assets\AS-Assets\VRising-Assets"
    }
    # @{
    #     Name = "Humankind"
    #     Input = "D:\BaiduNetdiskDownload\unity-Humankind"
    #     # 可选：单独指定这个游戏的导出目录。留空时继续使用 $OutputRoot\<Name>-Assets。
    #     # Output = "F:\Unity-AS-Assets\Humankind-Assets"
    # },
    # # @{
    # #     Name = "Valheim"
    # #     Input = "D:\BaiduNetdiskDownload\unity-Valheim.Build.21981559"
    # # },
    # @{
    #     Name = "HomuraHime"
    #     Input = "D:\BaiduNetdiskDownload\unity-Homura Hime"
    # },
    # @{
    #     Name = "Freedunk"
    #     Input = "C:\Program Files (x86)\Freedunk\Game"
    # },
    # @{
    #     Name = "OldWorld"
    #     Input = "D:\BaiduNetdiskDownload\unity-Old World"
    # },
    # @{
    #     Name = "YuanShen"
    #     Input = "C:\Program Files\miHoYo Launcher\games\Genshin Impact Game\YuanShen_Data\StreamingAssets\AssetBundles\blocks"
    #     Game = "GI"
    # }
)

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

$summary = New-Object System.Collections.Generic.List[object]
$startedAll = Get-Date

foreach ($game in $games) {
    $name = [string]$game.Name
    $inputPath = [string]$game.Input
    $gameType = if ($game.ContainsKey("Game")) { [string]$game.Game } else { "Normal" }
    $configuredOutput = if ($game.ContainsKey("Output")) { [string]$game.Output } else { "" }
    $outputPath = if ([string]::IsNullOrWhiteSpace($configuredOutput)) {
        Join-Path $OutputRoot "$name-Assets"
    }
    else {
        $configuredOutput
    }

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
    Write-Host "[$name] Game  : $gameType"
    Write-Host "============================================================"

    if ([System.IO.Path]::GetExtension($CliPath).Equals(".dll", [System.StringComparison]::OrdinalIgnoreCase)) {
        & dotnet $CliPath $inputPath $outputPath --game $gameType
    }
    else {
        & $CliPath $inputPath $outputPath --game $gameType
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
