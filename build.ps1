$ErrorActionPreference = 'Stop'

function Get-LockingProcesses {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    Get-CimInstance Win32_Process | Where-Object {
        $_.ProcessId -ne $PID -and (
            ($_.ExecutablePath -and [System.IO.Path]::GetFullPath($_.ExecutablePath).StartsWith($fullPath, [System.StringComparison]::OrdinalIgnoreCase)) -or
            ($_.CommandLine -and $_.CommandLine.IndexOf($fullPath, [System.StringComparison]::OrdinalIgnoreCase) -ge 0)
        )
    }
}

function Assert-DirectoryIsNotLocked {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $lockingProcesses = @(Get-LockingProcesses -Path $Path)
    if ($lockingProcesses.Count -gt 0) {
        $details = ($lockingProcesses | ForEach-Object {
            "PID $($_.ProcessId) $($_.Name) $($_.ExecutablePath)"
        }) -join [Environment]::NewLine
        throw "Cannot update '$Path' because running process(es) are using it. Stop export/preview processes and run build.ps1 again.$([Environment]::NewLine)$details"
    }
}

# prepare patcher
dotnet build AnimeStudio.Patcher -c Release -f net9.0
$patcher = "AnimeStudio.Patcher\bin\Release\net9.0\AnimeStudio.Patcher.exe"

foreach ($tfm in 'net9.0-windows') {
    # config
    $outputDir = ".\dist\$tfm"
    $stagingDir = ".\dist\$tfm.__staging_$PID"
    $configuration = 'Release'

    # prepare paths
    $guiOut = "AnimeStudio.GUI/bin/$configuration/$tfm"
    $cliOut = "AnimeStudio.CLI/bin/$configuration/$tfm"

    $guiExe = "$guiOut/AnimeStudio.GUI.exe"
    $cliExe = "$cliOut/AnimeStudio.CLI.exe"

    Assert-DirectoryIsNotLocked -Path $outputDir

    # build cli and gui & patch them
    # The patcher mutates the apphost exe in-place. Clean the per-target output first
    # so repeated builds do not turn bin\App.dll into bin\bin\App.dll.
    if (Test-Path -LiteralPath $cliOut) { Remove-Item -LiteralPath $cliOut -Recurse -Force }
    if (Test-Path -LiteralPath $guiOut) { Remove-Item -LiteralPath $guiOut -Recurse -Force }
    dotnet build AnimeStudio.CLI -c $configuration -f $tfm -t:Rebuild
    & $patcher $cliExe -d bin
    dotnet build AnimeStudio.GUI -c $configuration -f $tfm -t:Rebuild
    & $patcher $guiExe -d bin

    # prepare staging output first; only replace dist after all build artifacts are ready.
    if (Test-Path -LiteralPath $stagingDir) { Remove-Item -LiteralPath $stagingDir -Recurse -Force }
    New-Item -ItemType Directory -Path $stagingDir | Out-Null
    New-Item -ItemType Directory -Path "$stagingDir/bin" | Out-Null

    # copy to output
    Copy-Item "$cliOut/*" "$stagingDir/bin" -Recurse
    Copy-Item "$guiOut/*" "$stagingDir/bin" -Recurse -Force

    # move files out
    foreach ($exe in 'AnimeStudio.GUI.exe', 'AnimeStudio.CLI.exe') {
        Move-Item "$stagingDir/bin/$exe" $stagingDir
    }
    Move-Item "$stagingDir/bin/LICENSE" $stagingDir

    Assert-DirectoryIsNotLocked -Path $outputDir
    if (Test-Path -LiteralPath $outputDir) { Remove-Item -LiteralPath $outputDir -Recurse -Force }
    Move-Item -LiteralPath $stagingDir -Destination $outputDir
}
