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

$requestGenerator = Read-RepoFile "AnimeStudio.CLI\UnityBakeRequestGenerator.cs"
$libraryIndexBuilder = Read-RepoFile "AnimeStudio.CLI\SQLiteLibraryIndexBuilder.cs"
$cliUsage = Read-RepoFile "docs\CLI_USAGE.md"

$skipSelector = Get-MethodBodyText $requestGenerator "private static IEnumerable<PreviewSelection> SelectExplicitBakeCandidatesFromLibraryDb"
Assert-Contains $skipSelector "skipBakedCache" "Unity bake batch selector must support cache skipping."
Assert-Contains $skipSelector "CreateTempProcessedBakeCacheTable(connection, libraryRoot)" "Unity bake batch selector must build a trusted processed-cache table before skipping."
Assert-Contains $skipSelector "temp_unity_bake_processed_cache" "Unity bake batch selector must skip only rows inserted into the processed-cache table."
Assert-Contains $skipSelector "AND NOT EXISTS" "Unity bake batch selector must keep unprocessed candidates visible."
Assert-NotContains $skipSelector "bc.status='baked')" "Unity bake batch selector must not skip raw baked cache rows without trust proof."

$processedCache = Get-MethodBodyText $requestGenerator "private static void CreateTempProcessedBakeCacheTable"
Assert-Contains $processedCache "status IN ('baked', 'static_pose', 'needs_review')" "Processed bake cache may only start from terminal bake statuses."
Assert-Contains $processedCache "IsTrustedBakedGltfPath(bakedGltf, libraryRoot)" "Processed bake cache must re-check trusted baked glTF before skipping baked rows."
Assert-Contains $processedCache "IsStaticPoseBakedGltfPath(bakedGltf, libraryRoot)" "Processed bake cache may skip static_pose only after report proof."
Assert-Contains $processedCache "IsNeedsReviewBakedGltfPath(bakedGltf, libraryRoot)" "Processed bake cache may skip needs_review only after report proof."
Assert-Contains $processedCache "continue;" "Processed bake cache must drop untrusted terminal-looking rows."
Assert-Contains $processedCache "temp_unity_bake_processed_cache" "Processed bake cache must be the only source used by skipBakedCache."

$preserve = Get-MethodBodyText $requestGenerator "private static bool ShouldPreserveExistingBakeCache"
Assert-Contains $preserve '"static_pose"' "Bake cache writeback may preserve static_pose terminal diagnostics."
Assert-Contains $preserve '"needs_review"' "Bake cache writeback may preserve needs_review terminal diagnostics."
Assert-Contains $preserve '"baked"' "Bake cache writeback may preserve baked only after trust proof."
Assert-Contains $preserve "IsTrustedBakedGltfPath(bakedGltfPath, libraryRoot)" "Bake cache writeback must re-check trusted baked glTF before preserving baked rows."

$upsert = Get-MethodBodyText $requestGenerator "private static void UpsertBakeCache"
Assert-Contains $upsert "ShouldPreserveExistingBakeCache" "Bake cache upsert must call terminal preservation gate."
Assert-Contains $upsert "excluded.status NOT IN ('baked', 'static_pose', 'needs_review')" "Bake cache upsert may preserve old terminal rows only against non-terminal updates."

$trustedGenerator = Get-MethodBodyText $requestGenerator "private static bool IsTrustedBakedGltfPath"
Assert-Contains $trustedGenerator '"frameVaryingTracks"' "Unity bake request summary must require frame-varying tracks for trusted baked rows."
Assert-Contains $trustedGenerator "IsTrustedAvatarBake(report)" "Unity bake request summary must require Avatar trust for trusted baked rows."
Assert-Contains $trustedGenerator "UsesFirstSampleHumanoidDelta(report)" "Unity bake request summary must reject first-sample delta legacy bakes."

$uniqueGenerator = Get-MethodBodyText $requestGenerator "private static JObject BuildUniqueBakeCacheCounts"
Assert-Contains $uniqueGenerator "IsTrustedBakedGltfPath(bakedGltf, libraryRoot)" "Unity bake request summary unique counts must recompute trusted baked proof."
Assert-Contains $uniqueGenerator "uniquePendingUnityBakeCandidates" "Unity bake request summary must keep pending counts visible."
Assert-Contains $uniqueGenerator "uniqueTrustedBakedCandidates" "Unity bake request summary must separate trusted baked from raw baked."

$trustedIndex = Get-MethodBodyText $libraryIndexBuilder "private static bool IsTrustedBakedGltfPath"
Assert-Contains $trustedIndex '"frameVaryingTracks"' "SQLite summary must require frame-varying tracks for trusted baked rows."
Assert-Contains $trustedIndex "IsTrustedAvatarBake(report)" "SQLite summary must require Avatar trust for trusted baked rows."
Assert-Contains $trustedIndex "UsesFirstSampleHumanoidDelta(report)" "SQLite summary must reject first-sample delta legacy bakes."

$uniqueIndex = Get-MethodBodyText $libraryIndexBuilder "private static JObject BuildUniqueBakeCacheCounts"
Assert-Contains $uniqueIndex "IsTrustedBakedGltfPath(bakedGltf, libraryRoot)" "SQLite summary unique counts must recompute trusted baked proof."
Assert-Contains $uniqueIndex "uniquePendingUnityBakeCandidates" "SQLite summary must keep untrusted baked rows pending."
Assert-Contains $uniqueIndex "uniqueTrustedBakedCandidates" "SQLite summary must separate trusted baked from raw baked."

Assert-Contains $cliUsage "untrusted_baked" "CLI docs must explain that untrusted baked rows re-enter the queue."
Assert-Contains $cliUsage "cacheTrustGate=untrusted_requeue_overwriteable" "CLI docs must explain that untrusted baked rows are not protected terminal cache."

Write-Output "OK: Unity bake cache trust gate rejects raw baked cache rows as terminal proof."
