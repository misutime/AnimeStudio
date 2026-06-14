param(
    [string]$RepoRoot,
    [switch]$SkipBuild,
    [switch]$IncludeOverrideRegression,
    [string]$CliPath = "AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
} else {
    $RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
}

function Run-Step {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    Write-Output "== $Name"
    & $Action
    Write-Output "OK: $Name"
}

Run-Step "Explicit animation relation gate" {
    & (Join-Path $RepoRoot "scripts\Test-ExplicitAnimationRelationGate.ps1") -RepoRoot $RepoRoot
}

Run-Step "Unity Avatar asset trust gate" {
    & (Join-Path $RepoRoot "scripts\Test-UnityAvatarAssetTrustGate.ps1") -RepoRoot $RepoRoot
}

Run-Step "Unity bake cache trust gate" {
    & (Join-Path $RepoRoot "scripts\Test-UnityBakeCacheTrustGate.ps1") -RepoRoot $RepoRoot
}

if ($IncludeOverrideRegression) {
    Run-Step "AnimatorOverrideController clip-pair regression" {
        & (Join-Path $RepoRoot "scripts\Test-OverrideClipPairRegression.ps1") -CliPath $CliPath
    }
}

if (-not $SkipBuild) {
    Run-Step "Build AnimeStudio.CLI" {
        dotnet build (Join-Path $RepoRoot "AnimeStudio.CLI\AnimeStudio.CLI.csproj")
    }

    Run-Step "Build AnimeStudio.LibraryBrowser" {
        dotnet build (Join-Path $RepoRoot "AnimeStudio.LibraryBrowser\AnimeStudio.LibraryBrowser.csproj")
    }
}

Write-Output "OK: animation pipeline gates passed."
