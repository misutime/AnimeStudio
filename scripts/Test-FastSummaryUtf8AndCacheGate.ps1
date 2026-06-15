param(
    [string]$RepoRoot
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
} else {
    $RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
}

function Write-Utf8NoBomJson {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Value
    )

    $json = $Value | ConvertTo-Json -Depth 32
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $json, $encoding)
}

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("AnimeStudioFastSummaryGate_" + [Guid]::NewGuid().ToString("N"))
$libraryRoot = Join-Path $tempRoot "Library"
$outputRoot = Join-Path $tempRoot "Out"
New-Item -ItemType Directory -Force -Path $libraryRoot, $outputRoot | Out-Null

try {
    New-Item -ItemType File -Force -Path (Join-Path $libraryRoot "library_index.db") | Out-Null

    $sqliteCreated = [DateTimeOffset]::UtcNow.AddMinutes(-5).ToString("O")
    $cacheCreated = [DateTimeOffset]::UtcNow.ToString("O")
    $chinesePathSegment = -join @([char]0x4E34, [char]0x65F6)
    $chineseRule = -join @(
        [char]0x7D22, [char]0x5F15, [char]0x8981, [char]0x5168,
        [char]0xFF0C,
        [char]0x5BFC, [char]0x51FA, [char]0x8981, [char]0x51C6
    )
    Write-Utf8NoBomJson -Path (Join-Path $libraryRoot "sqlite_index_summary.json") -Value ([ordered]@{
        schemaVersion = 1
        database = (Join-Path $libraryRoot "library_index.db")
        root = $libraryRoot
        createdUtc = $sqliteCreated
        sourceIndex = ("D:\" + $chinesePathSegment + "\unity_source_index.db")
        rule = $chineseRule
    })

    Write-Utf8NoBomJson -Path (Join-Path $libraryRoot "animation_bake_cache_summary.json") -Value ([ordered]@{
        generatedAt = $cacheCreated
        explicitUnityBakeCandidates = 12
        uniqueExplicitUnityBakeCandidates = 12
        importedAvatarAssetFileCount = 2
        importedAvatarAssetTrustedFileCount = 2
        importedAvatarAssetKeyCount = 4
        importedAvatarProbeFreshness = "fresh"
        importedAvatarProbeEnforced = $true
        importedAvatarProbeValidHumanAvatars = 2
        importedAvatarProbeInvalidAssets = 0
        importedAvatarAssetBakeReadyExplicitUnityBakeCandidates = 10
        uniqueImportedAvatarAssetBakeReadyExplicitUnityBakeCandidates = 10
        uniqueImportedAvatarAssetBakeReadyExplicitUnityBakeCoveragePercent = 83.333333
        effectiveBakeReadyExplicitUnityBakeCandidates = 10
        uniqueEffectiveBakeReadyExplicitUnityBakeCandidates = 10
        uniqueEffectiveBakeReadyExplicitUnityBakeCoveragePercent = 83.333333
        cachedCandidates = 1
        trustedBakedCandidates = 1
        staticPoseCandidates = 0
        untrustedBakedCandidates = 0
        failedCandidates = 0
        pendingUnityBakeCandidates = 9
        uniquePendingUnityBakeCandidates = 9
    })

    $measure = Join-Path $RepoRoot "scripts\Measure-DeterministicAnimationCoverage.ps1"
    $powershell = Join-Path $env:WINDIR "System32\WindowsPowerShell\v1.0\powershell.exe"
    & $powershell -NoProfile -ExecutionPolicy Bypass -File $measure -LibraryPath $libraryRoot -FastSummary -OutputDir $outputRoot | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "FastSummary failed under Windows PowerShell 5.1."
    }

    $jsonPath = Join-Path $outputRoot "deterministic_animation_coverage.json"
    $report = Get-Content -LiteralPath $jsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-True ($report.sqliteSummaryPresent -eq $true) "FastSummary must read UTF-8 sqlite_index_summary.json under Windows PowerShell 5.1."
    Assert-True ([string]::IsNullOrWhiteSpace($report.sqliteSummaryError)) "FastSummary must not report a sqlite summary read error for UTF-8 JSON."
    Assert-True ($report.cacheSummaryFreshness -eq "fresh") "FastSummary must compare cache freshness when sqlite summary is readable."
    Assert-True ($report.importedAvatarAssetCount -eq 2) "FastSummary without -UnityProject must reuse verified ImportedAvatar counts from bake cache."
    Assert-True ($report.importedAvatarTrustedAssetCount -eq 2) "FastSummary must preserve trusted ImportedAvatar file count from bake cache."
    Assert-True ($report.importedAvatarProbeFreshness -eq "fresh") "FastSummary must preserve ImportedAvatar probe freshness from bake cache."
    Assert-True ($report.importedAvatarProbeValidHumanAvatars -eq 2) "FastSummary must preserve valid ImportedAvatar probe count from bake cache."

    Write-Output "OK: FastSummary UTF-8/cache gate"
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
