param(
    [string]$RepoRoot
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
} else {
    $RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
}

function Read-RepoFile {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    $path = Join-Path $RepoRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing file: $RelativePath"
    }

    return Get-Content -LiteralPath $path -Raw -Encoding UTF8
}

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Text.Contains($Needle)) {
        throw $Message
    }
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if ($Text.Contains($Needle)) {
        throw $Message
    }
}

function Get-MethodBodyText {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$MethodName
    )

    $index = $Text.IndexOf($MethodName, [System.StringComparison]::Ordinal)
    if ($index -lt 0) {
        throw "Missing method: $MethodName"
    }

    $brace = $Text.IndexOf("{", $index, [System.StringComparison]::Ordinal)
    if ($brace -lt 0) {
        throw "Missing method body: $MethodName"
    }

    $depth = 0
    for ($i = $brace; $i -lt $Text.Length; $i++) {
        if ($Text[$i] -eq "{") {
            $depth++
        } elseif ($Text[$i] -eq "}") {
            $depth--
            if ($depth -eq 0) {
                return $Text.Substring($brace, $i - $brace + 1)
            }
        }
    }

    throw "Unclosed method body: $MethodName"
}

function Get-TextAfterMarker {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Marker,
        [int]$Length = 500
    )

    $index = $Text.IndexOf($Marker, [System.StringComparison]::Ordinal)
    if ($index -lt 0) {
        throw "Missing marker: $Marker"
    }

    $lengthToRead = [System.Math]::Min($Length, $Text.Length - $index)
    return $Text.Substring($index, $lengthToRead)
}

$preview = Read-RepoFile "AnimeStudio.CLI\PreviewGltfGenerator.cs"
$bake = Read-RepoFile "AnimeStudio.CLI\UnityBakeRequestGenerator.cs"
$sqlite = Read-RepoFile "AnimeStudio.CLI\SQLiteLibraryIndexBuilder.cs"
$browserCandidate = Read-RepoFile "AnimeStudio.LibraryBrowser\LibraryAnimationCandidate.cs"
$browserIndex = Read-RepoFile "AnimeStudio.LibraryBrowser\LibraryAnimationIndex.cs"
$browserMain = Read-RepoFile "AnimeStudio.LibraryBrowser\MainForm.cs"
$browserDoc = Read-RepoFile "docs\LIBRARY_BROWSER_ANIMATION_PREVIEW_DESIGN.md"
$standards = Read-RepoFile "docs\PROJECT_EXPORT_STANDARDS.md"
$cliUsage = Read-RepoFile "docs\CLI_USAGE.md"

$previewGate = Get-MethodBodyText $preview "private static bool IsExplicitPreviewRelation"
Assert-Contains $previewGate '"relationSource"' "Preview gate must read relationSource."
Assert-NotContains $previewGate '"confidence"' "Preview gate must not promote by confidence."
Assert-NotContains $previewGate "explicit_unity_reference" "Preview gate must not promote old confidence values."

$bakeGate = Get-MethodBodyText $bake "private static bool IsExplicitBakeRelation"
Assert-Contains $bakeGate '"relationSource"' "Unity bake gate must read relationSource."
Assert-Contains $bakeGate '"explicit"' "Unity bake gate must accept only explicit Unity relations."
Assert-NotContains $bakeGate '"confidence"' "Unity bake gate must not promote by confidence."
Assert-NotContains $bakeGate "explicit_unity_reference" "Unity bake gate must not promote old confidence values."

$sqliteGate = Get-MethodBodyText $sqlite "private static bool IsExplicitModelAnimationRelation"
Assert-Contains $sqliteGate "relationSource" "SQLite compact import gate must read relationSource."
Assert-Contains $sqliteGate '"explicit"' "SQLite compact import gate must accept only explicit."
Assert-NotContains $sqliteGate "explicit_unity_reference" "SQLite compact import gate must not fall back to old confidence values."
Assert-NotContains $sqlite "OR mar.confidence = 'explicit_unity_reference'" "Old SQLite queries must not add default candidates by confidence."
Assert-NotContains $sqlite "兼容旧 source_index：旧表只有 originalClip/overrideClip" "SQLite source graph must not infer AnimatorOverrideController clip pairs from separated legacy relations."
Assert-Contains $sqlite "不能猜 pair" "SQLite source graph must explicitly reject legacy separated AnimatorOverrideController relations."

$browserExplicit = Get-TextAfterMarker $browserCandidate "public bool IsExplicit =>" 450
Assert-Contains $browserExplicit "RelationSource" "Browser explicit candidate check must read RelationSource."
Assert-NotContains $browserExplicit "Confidence" "Browser explicit candidate check must not use Confidence."
Assert-NotContains $browserIndex "OR mar.confidence = 'explicit_unity_reference'" "Browser old relation query must not add default candidates by confidence."

$browserFindCli = Get-MethodBodyText $browserMain "private static CliRunLauncher FindCliLauncher"
Assert-Contains $browserFindCli '"--build_sqlite_index", libraryRoot' "Browser rebuild animation index must rebuild SQLite from the selected library root."
Assert-Contains $browserFindCli '"--require_fresh_source_animation_relations"' "Browser rebuild animation index must fail on stale source animation relations."
Assert-Contains $browserFindCli '"--skip_sqlite_file_index"' "Browser rebuild animation index should avoid expensive file scans by default."
Assert-Contains $browserFindCli '"--skip_sqlite_json_documents"' "Browser rebuild animation index should avoid copying large JSON documents by default."

Assert-Contains $browserDoc 'confidence=explicit_unity_reference`' "Browser animation preview doc must mention old confidence values."
Assert-Contains $browserDoc 'relationSource=explicit' "Browser animation preview doc must require relationSource=explicit."
Assert-Contains $standards 'model_animations.json' "Project standards must mention default model animation candidates."
Assert-Contains $standards 'SQLite' "Project standards must mention SQLite animation candidates."
Assert-Contains $standards 'unityAssetPaths.avatarAsset' "Project standards must keep imported Avatar asset oracle rules."
Assert-Contains $cliUsage '半旧 separated 场景验证只有分离的 `originalClip/overrideClip` 时不会产出候选' "CLI docs must say separated legacy OverrideController relations are skipped."
Assert-NotContains $cliUsage '半旧 separated 场景验证只有分离的 `originalClip/overrideClip` 时可兼容恢复' "CLI docs must not claim separated legacy OverrideController relations are production-compatible."

Write-Output "OK: explicit animation relation gates have no confidence/structural fallback."
