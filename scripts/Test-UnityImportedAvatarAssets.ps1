param(
    [Parameter(Mandatory = $true)]
    [string]$UnityProject,

    [string]$UnityEditor = "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe",

    [string]$AvatarRoot = "Assets/AnimeStudioBake/ImportedAvatar",

    [string]$OutputDir,

    [switch]$AllowInvalid
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $UnityProject -PathType Container)) {
    throw "UnityProject does not exist: $UnityProject"
}

if (-not (Test-Path -LiteralPath $UnityEditor -PathType Leaf)) {
    throw "UnityEditor does not exist: $UnityEditor"
}

$helperCli = Join-Path $UnityProject "Assets\AnimeStudio.UnityBake\Editor\AnimeStudioBakeCli.cs"
if (-not (Test-Path -LiteralPath $helperCli -PathType Leaf)) {
    throw "Unity project does not contain AnimeStudio.UnityBake helper. Copy AnimeStudio.UnityBake/Assets/AnimeStudio.UnityBake into the Unity project Assets folder first."
}

$helperText = Get-Content -LiteralPath $helperCli -Raw -Encoding UTF8
if (-not $helperText.Contains("-animeStudioImportedAvatarProbeDir")) {
    throw "Unity project AnimeStudio.UnityBake helper is too old for batch ImportedAvatar probe. Copy the latest AnimeStudio.UnityBake/Assets/AnimeStudio.UnityBake into the Unity project Assets folder first."
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path (Get-Location).Path "ImportedAvatarProbe"
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$outputJson = Join-Path $OutputDir "imported_avatar_probe_batch.json"
$logPath = Join-Path $OutputDir "unity_imported_avatar_probe.log"
Remove-Item -LiteralPath $outputJson -Force -ErrorAction SilentlyContinue

$argsList = @(
    "-batchmode",
    "-quit",
    "-projectPath", $UnityProject,
    "-executeMethod", "AnimeStudio.UnityBake.AnimeStudioBakeCli.Run",
    "-animeStudioImportedAvatarProbeDir", $AvatarRoot,
    "-outputJson", $outputJson,
    "-logFile", $logPath
)

& $UnityEditor @argsList
$exitCode = $LASTEXITCODE

for ($i = 0; $i -lt 30 -and -not (Test-Path -LiteralPath $outputJson -PathType Leaf); $i++) {
    Start-Sleep -Milliseconds 500
}

if (-not (Test-Path -LiteralPath $outputJson -PathType Leaf)) {
    throw "Unity did not write imported Avatar probe report. Log: $logPath"
}

$report = Get-Content -LiteralPath $outputJson -Raw -Encoding UTF8 | ConvertFrom-Json

Write-Output $outputJson
Write-Output $logPath
Write-Output ("totalAssets={0} validHumanAvatars={1} invalidAssets={2} status={3}" -f $report.totalAssets, $report.validHumanAvatars, $report.invalidAssets, $report.status)

if ($null -ne $exitCode -and $exitCode -ne 0 -and -not $AllowInvalid) {
    throw "Unity imported Avatar probe failed with exit code $exitCode. Log: $logPath"
}

if (-not $AllowInvalid -and [int]$report.invalidAssets -gt 0) {
    throw "Imported Avatar probe found invalid assets. Report: $outputJson"
}
