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

function Get-TextBetweenMarkers {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Start,
        [Parameter(Mandatory = $true)][string]$End
    )

    $startIndex = $Text.IndexOf($Start, [System.StringComparison]::Ordinal)
    if ($startIndex -lt 0) {
        throw "Missing marker: $Start"
    }

    $endIndex = $Text.IndexOf($End, $startIndex + $Start.Length, [System.StringComparison]::Ordinal)
    if ($endIndex -lt 0) {
        throw "Missing marker: $End"
    }

    return $Text.Substring($startIndex, $endIndex - $startIndex)
}

$requestGenerator = Read-RepoFile "AnimeStudio.CLI\UnityBakeRequestGenerator.cs"
$libraryIndexBuilder = Read-RepoFile "AnimeStudio.CLI\SQLiteLibraryIndexBuilder.cs"
$libraryBrowserMainForm = Read-RepoFile "AnimeStudio.LibraryBrowser\MainForm.cs"
$libraryBrowserPreviewCache = Read-RepoFile "AnimeStudio.LibraryBrowser\AnimationPreviewCache.cs"
$libraryBrowserAnimationIndex = Read-RepoFile "AnimeStudio.LibraryBrowser\LibraryAnimationIndex.cs"
$coverageScript = Read-RepoFile "scripts\Measure-DeterministicAnimationCoverage.ps1"
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
Assert-Contains $preserve '"baked"' "Bake cache writeback may preserve baked only after trust proof."
Assert-Contains $preserve "IsTrustedBakedGltfPath(bakedGltfPath, libraryRoot)" "Bake cache writeback must re-check trusted baked glTF before preserving baked rows."
Assert-NotContains $preserve '"static_pose"' "Bake cache writeback must not preserve old static_pose rows because newer ImportedAnimationClip bakes must be able to replace them."
Assert-NotContains $preserve '"needs_review"' "Bake cache writeback must not preserve old needs_review rows because newer ImportedAnimationClip bakes must be able to replace them."

$upsert = Get-MethodBodyText $requestGenerator "private static void UpsertBakeCacheRow"
Assert-Contains $upsert "ShouldPreserveExistingBakeCache" "Bake cache upsert must call terminal preservation gate."
Assert-Contains $upsert "excluded.status NOT IN ('baked', 'static_pose', 'needs_review')" "Bake cache upsert may preserve old terminal rows only against non-terminal updates."

$trustedGenerator = Get-MethodBodyText $requestGenerator "private static bool IsTrustedBakedGltfPath"
Assert-Contains $trustedGenerator '"frameVaryingTracks"' "Unity bake request summary must require frame-varying tracks for trusted baked rows."
Assert-Contains $trustedGenerator "IsTrustedAvatarBake(report)" "Unity bake request summary must require Avatar trust for trusted baked rows."
Assert-Contains $trustedGenerator "UsesFirstSampleHumanoidDelta(report)" "Unity bake request summary must reject first-sample delta legacy bakes."

$uniqueGenerator = Get-MethodBodyText $requestGenerator "private static JObject BuildUniqueBakeCacheCounts"
Assert-Contains $uniqueGenerator "IsTrustedBakedGltfPath(bakedGltf, libraryRoot)" "Unity bake request summary unique counts must recompute trusted baked proof."
Assert-Contains $uniqueGenerator "IsNeedsReviewBakedGltfPath(bakedGltf, libraryRoot)" "Unity bake request summary must treat needs_review as terminal diagnostics."
Assert-Contains $uniqueGenerator "uniqueNeedsReviewCandidates" "Unity bake request summary must expose needs_review terminal counts."
Assert-Contains $uniqueGenerator "terminalDiagnosticCandidates" "Unity bake request summary pending count must subtract terminal diagnostics."
Assert-Contains $uniqueGenerator "uniquePendingUnityBakeCandidates" "Unity bake request summary must keep pending counts visible."
Assert-Contains $uniqueGenerator "uniqueTrustedBakedCandidates" "Unity bake request summary must separate trusted baked from raw baked."

$trustedIndex = Get-MethodBodyText $libraryIndexBuilder "private static bool IsTrustedBakedGltfPath"
Assert-Contains $trustedIndex '"frameVaryingTracks"' "SQLite summary must require frame-varying tracks for trusted baked rows."
Assert-Contains $trustedIndex "IsTrustedAvatarBake(report)" "SQLite summary must require Avatar trust for trusted baked rows."
Assert-Contains $trustedIndex "UsesFirstSampleHumanoidDelta(report)" "SQLite summary must reject first-sample delta legacy bakes."

$sqliteBakeReadyAvatar = Get-MethodBodyText $libraryIndexBuilder "private static string BakeReadyAvatarSql"
Assert-Contains $sqliteBakeReadyAvatar '$.avatar.humanBones' "SQLite production bake-ready summary must require HumanDescription humanBones."
Assert-Contains $sqliteBakeReadyAvatar '$.avatar.skeletonBones' "SQLite production bake-ready summary must require HumanDescription skeletonBones."
Assert-NotContains $sqliteBakeReadyAvatar '$.avatar.oracle' "SQLite production bake-ready summary must not count AvatarConstant/oracle as production Avatar."
Assert-NotContains $sqliteBakeReadyAvatar '$.avatar.internalSolver' "SQLite production bake-ready summary must not count AvatarConstant/internalSolver as production Avatar."

$uniqueIndex = Get-MethodBodyText $libraryIndexBuilder "private static JObject BuildUniqueBakeCacheCounts"
Assert-Contains $uniqueIndex "IsTrustedBakedGltfPath(bakedGltf, libraryRoot)" "SQLite summary unique counts must recompute trusted baked proof."
Assert-Contains $uniqueIndex "IsNeedsReviewBakedGltfPath(bakedGltf, libraryRoot)" "SQLite summary must treat needs_review as terminal diagnostics."
Assert-Contains $uniqueIndex "uniqueNeedsReviewCandidates" "SQLite summary must expose needs_review terminal counts."
Assert-Contains $uniqueIndex "terminalDiagnosticCandidates" "SQLite summary pending count must subtract terminal diagnostics."
Assert-Contains $uniqueIndex "uniquePendingUnityBakeCandidates" "SQLite summary must keep untrusted baked rows pending."
Assert-Contains $uniqueIndex "uniqueTrustedBakedCandidates" "SQLite summary must separate trusted baked from raw baked."

$browserProcessed = Get-MethodBodyText $libraryBrowserMainForm "private bool IsUnityBakeAlreadyProcessedTerminal"
Assert-Contains $browserProcessed "status?.Status" "Browser batch bake must inspect preview status before skipping terminal rows."
Assert-Contains $browserProcessed "StringComparison.OrdinalIgnoreCase" "Browser batch bake terminal status checks must be explicit."
Assert-Contains $browserProcessed "|| string.Equals" "Browser batch bake must skip all processed terminal statuses."

$browserKnownAvatar = Get-MethodBodyText $libraryBrowserMainForm "private static bool IsKnownProductionAvatarSource"
Assert-Contains $browserKnownAvatar '"model_human_description"' "Browser production Avatar source must accept complete model HumanDescription."
Assert-NotContains $browserKnownAvatar "candidate_production_avatar" "Browser production Avatar source must not accept legacy candidate-only flags."

$browserProductionOracle = Get-MethodBodyText $libraryBrowserMainForm "private static bool HasProductionBakeOracle"
Assert-Contains $browserProductionOracle "HasOriginalAvatarOracle(animation)" "Browser batch bake oracle gate must require original model Avatar/HumanDescription."
Assert-Contains $browserProductionOracle "HasImportedAvatarAssetOracle(animation)" "Browser batch bake oracle gate must accept explicit imported Unity Avatar assets."
Assert-NotContains $browserProductionOracle "ProductionUnityBakeReady" "Browser batch bake oracle gate must not trust old productionUnityBakeReady flags by themselves."

$browserDetermineAvatarSource = Get-MethodBodyText $libraryBrowserAnimationIndex "private static string DetermineProductionUnityBakeAvatarSource"
Assert-Contains $browserDetermineAvatarSource '"imported_unity_avatar_asset"' "Browser animation index must keep explicit imported Avatar asset source."
Assert-Contains $browserDetermineAvatarSource '"model_human_description"' "Browser animation index must keep complete HumanDescription source."
Assert-NotContains $browserDetermineAvatarSource '"candidate_production_avatar"' "Browser animation index must not convert old candidate flags into production Avatar sources."

$browserModelFilter = Get-MethodBodyText $libraryBrowserMainForm "private bool MatchesModelAnimationStateFilter"
Assert-Contains $browserModelFilter "IsUnityBakeAlreadyProcessedTerminal(model, animation)" "Browser model animation pending filter must exclude terminal diagnostics."
Assert-Contains $browserModelFilter "Contains(status" "Browser model animation review filter must inspect status text for terminal diagnostics."
Assert-Contains $browserModelFilter "|| Contains(status" "Browser model animation review filter must include multiple review/failure terminal diagnostics."

$browserAnimationFilter = Get-MethodBodyText $libraryBrowserMainForm "private IEnumerable<LibraryModelItem> ApplyAnimationModelStateFilter"
Assert-Contains $browserAnimationFilter "IsUnityBakeAlreadyProcessedTerminal(model, modelAnimation)" "Browser animation model pending filter must exclude terminal diagnostics."
Assert-Contains $browserAnimationFilter "Contains(status" "Browser animation model review filter must inspect status text for terminal diagnostics."

$browserModelQualityFilter = Get-MethodBodyText $libraryBrowserMainForm "private bool ModelHasPendingTrustedUnityBake"
Assert-Contains $browserModelQualityFilter "IsUnityBakeAlreadyProcessedTerminal(item, animation)" "Browser main model pending filter must exclude terminal diagnostics."

$browserModelStats = Get-MethodBodyText $libraryBrowserMainForm "private sealed class ModelAnimationBakeStats"
Assert-Contains $browserModelStats "NeedsManualReview" "Browser model detail stats must expose needs_review terminal diagnostics."

$browserAnimationSummary = Get-MethodBodyText $libraryBrowserMainForm "private string BuildAnimationModelStateSummary"
Assert-Contains $browserAnimationSummary "IsUnityBakeAlreadyProcessedTerminal(model, modelAnimation)" "Browser animation page pending count must exclude terminal diagnostics."
Assert-Contains $browserAnimationSummary "Contains(status" "Browser animation page review count must include review/failure terminal diagnostics."

$browserOpenDiagnostic = Get-MethodBodyText $libraryBrowserMainForm "private static bool IsOpenableUnityBakeDiagnosticStatus"
Assert-Contains $browserOpenDiagnostic "string.Equals(status" "Browser double-click must explicitly allow diagnostic terminal statuses."
Assert-Contains $browserOpenDiagnostic "|| string.Equals(status" "Browser double-click must allow multiple diagnostic terminal statuses."

$browserPreviewPriority = Get-MethodBodyText $libraryBrowserPreviewCache "private static int BakeCacheStatusPriority"
Assert-Contains $browserPreviewPriority "=> 45" "Browser preview status must keep needs_review above request_written rows."
Assert-Contains $browserPreviewPriority "=> 44" "Browser preview status must keep static_pose above request_written rows."

$browserGetStatus = Get-MethodBodyText $libraryBrowserPreviewCache "public AnimationPreviewStatus GetStatus"
Assert-Contains $browserGetStatus "BakeCacheStatusPriority(sqliteBakeStatus) > BakeCacheStatusPriority(localStatus)" "Browser local preview state must not hide higher-priority SQLite terminal diagnostics."

$browserEnsureBake = Get-MethodBodyText $libraryBrowserPreviewCache "public async Task<AnimationPreviewStatus> EnsureUnityBakeAsync"
Assert-Contains $browserEnsureBake "ReadUnityBakeApplyStatus(report)" "Browser single Unity bake must read apply report status."
Assert-Contains $browserEnsureBake "FormatBakeCacheStatus(" "Browser single Unity bake must preserve static_pose/needs_review/untrusted baked statuses."
Assert-Contains $browserEnsureBake "BuildUntrustedBakeCacheMessage" "Browser single Unity bake must explain non-playable baked diagnostics."

$avatarBlockers = Get-TextBetweenMarkers $coverageScript "def model_avatar_refresh_blockers" "def main():"
Assert-Contains $avatarBlockers "temp_bake_ready_animation_candidates" "Coverage blocker diagnostics must subtract currently bake-ready Avatar oracle candidates."
Assert-Contains $avatarBlockers "effective_bake_ready_avatar_sql" "Coverage blocker diagnostics must use the current HumanDescription/imported Avatar oracle gate."
Assert-NotContains $avatarBlockers "json_extract(c3.raw_json, '$.productionUnityBakeReady')" "Coverage blocker diagnostics must not count old productionUnityBakeReady flags as Avatar ready proof."
Assert-NotContains $avatarBlockers "json_extract(c.raw_json, '$.productionUnityBakeBlocked'), 0)=1" "Coverage blocker diagnostics must not trust old productionUnityBakeBlocked flags as the blocker source."

Assert-Contains $cliUsage "untrusted_baked" "CLI docs must explain that untrusted baked rows re-enter the queue."
Assert-Contains $cliUsage "cacheTrustGate=untrusted_requeue_overwriteable" "CLI docs must explain that untrusted baked rows are not protected terminal cache."

Write-Output "OK: Unity bake cache trust gate rejects raw baked cache rows as terminal proof."
