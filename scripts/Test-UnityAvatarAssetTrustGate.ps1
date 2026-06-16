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

function Assert-Before {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Earlier,
        [Parameter(Mandatory = $true)][string]$Later,
        [Parameter(Mandatory = $true)][string]$Message
    )

    $earlierIndex = $Text.IndexOf($Earlier, [System.StringComparison]::Ordinal)
    $laterIndex = $Text.IndexOf($Later, [System.StringComparison]::Ordinal)
    if ($earlierIndex -lt 0 -or $laterIndex -lt 0 -or $earlierIndex -gt $laterIndex) {
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

$builder = Read-RepoFile "AnimeStudio.UnityBake\Assets\AnimeStudio.UnityBake\Editor\AnimeStudioGltfSkeletonBuilder.cs"
$baker = Read-RepoFile "AnimeStudio.UnityBake\Assets\AnimeStudio.UnityBake\Editor\AnimeStudioPlayableBaker.cs"
$applier = Read-RepoFile "AnimeStudio.CLI\UnityBakeResultApplier.cs"
$requestGenerator = Read-RepoFile "AnimeStudio.CLI\UnityBakeRequestGenerator.cs"
$program = Read-RepoFile "AnimeStudio.CLI\Program.cs"
$animationClipRecovery = Read-RepoFile "AnimeStudio.CLI\AnimationClipAssetRecoveryExporter.cs"
$controllerContextRefresh = Read-RepoFile "AnimeStudio.CLI\AnimatorControllerContextRefresher.cs"
$browserCache = Read-RepoFile "AnimeStudio.LibraryBrowser\AnimationPreviewCache.cs"
$browserSettings = Read-RepoFile "AnimeStudio.LibraryBrowser\LibraryBrowserSettings.cs"
$unityReadme = Read-RepoFile "AnimeStudio.UnityBake\README.md"
$standards = Read-RepoFile "docs\PROJECT_EXPORT_STANDARDS.md"

$loadImportedAvatar = Get-MethodBodyText $builder "public static Avatar LoadImportedAvatarAsset"
Assert-Contains $loadImportedAvatar "AssetDatabase.LoadAssetAtPath<Avatar>" "Unity helper must load the explicit Avatar asset through Unity."
Assert-Contains $loadImportedAvatar "throw new InvalidOperationException" "Explicit Avatar asset load failure must throw."
Assert-Contains $loadImportedAvatar "must fail instead of falling back" "Explicit Avatar asset failure must not fall back."
Assert-Contains $loadImportedAvatar "!avatar.isValid || !avatar.isHuman" "Explicit Avatar asset must be a valid Humanoid Avatar."
Assert-NotContains $loadImportedAvatar "BuildAvatar(" "LoadImportedAvatarAsset must not build a fallback Avatar."

$bakeResult = Get-MethodBodyText $baker "public static AnimeStudioBakeResult Bake"
Assert-Contains $bakeResult "requestedAvatarAsset" "Bake result must record requestedAvatarAsset."
Assert-Contains $bakeResult "importedAvatarAssetValid" "Bake result must record importedAvatarAssetValid."
Assert-Contains $bakeResult "requestedAnimationClip" "Bake result must record requestedAnimationClip."
Assert-Contains $bakeResult "importedAnimationClip" "Bake result must record importedAnimationClip."
Assert-Contains $bakeResult "animationClipSource" "Bake result must record animationClipSource."
Assert-Contains $bakeResult "clip.isHumanMotion" "Bake result must record Unity imported clip.isHumanMotion."
Assert-Contains $bakeResult "AnimatorController auxiliary/non-body layer" "Unity helper humanMotion=false message must explain auxiliary/non-body clips instead of implying missing original asset only."
Assert-Contains $bakeResult "deterministic baseLayerClip" "Unity helper humanMotion=false message must direct users back to deterministic controller context."
Assert-Contains $bakeResult 'restPoseSource, "imported_unity_avatar_asset"' "Imported Avatar validity must require imported rest pose source."

$helperVersionGate = Get-MethodBodyText $requestGenerator "private static string ValidateUnityBakeHelperVersion"
Assert-Contains $helperVersionGate "clip.isHumanMotion" "Unity helper version gate must use a stable humanMotion guard marker."
Assert-Contains $helperVersionGate "isHumanMotion=false" "Unity helper version gate must not depend on exact humanMotion error prose."

$loadAnimationClip = Get-MethodBodyText $baker "private static AnimationClipLoadResult LoadAnimationClip"
Assert-Contains $loadAnimationClip "unityAssetPaths.animationClip" "Unity helper must prefer explicit Unity AnimationClip asset paths."
Assert-Contains $loadAnimationClip "animeStudioAssets.animation.anim" "Unity helper must still import AnimeStudio .anim sidecars when no explicit Unity clip asset is available."
Assert-Contains $loadAnimationClip "AssetDatabase.LoadAssetAtPath<AnimationClip>" "Unity helper must load AnimationClip through Unity's AssetDatabase."
Assert-Contains $loadAnimationClip "AssetDatabase.ImportAsset" "Unity helper must import copied .anim before loading it."
Assert-Contains $loadAnimationClip 'source = "unityAssetPaths.animationClip"' "Unity helper must trace explicit Unity AnimationClip sources."
Assert-Contains $loadAnimationClip 'source = "animeStudioAssets.animation.anim"' "Unity helper must trace imported sidecar AnimationClip sources."

$trustReport = Get-MethodBodyText $applier "private static AvatarTrustReport BuildAvatarTrustReport"
Assert-Before $trustReport "if (!string.IsNullOrWhiteSpace(requestedAvatarAsset) && IsImportedAvatarAssetResultValid(result))" "var skeletonBoneCount" "Requested Avatar asset trust must be decided before HumanDescription fallback."
Assert-Before $trustReport "if (!string.IsNullOrWhiteSpace(requestedAvatarAsset))" "var skeletonBoneCount" "Invalid requested Avatar asset must be rejected before HumanDescription fallback."
Assert-Contains $trustReport "imported_unity_avatar_asset_invalid" "Invalid requested Avatar asset must be marked untrusted."
Assert-Contains $trustReport "must not fall back to HumanDescription, AvatarConstant, internalSolver, or glTF rest pose trust" "Invalid requested Avatar asset message must forbid fallback trust."
Assert-Contains $trustReport "avatar_constant_oracle_diagnostic" "AvatarConstant oracle path must be diagnostic."
Assert-Contains $trustReport "TrustedProductionBake: false" "Diagnostic Avatar paths must not be trusted production bakes."

$importedValid = Get-MethodBodyText $applier "private static bool IsImportedAvatarAssetResultValid"
Assert-Contains $importedValid 'result["importedAvatarAssetValid"]' "Applier must prefer importedAvatarAssetValid proof."
Assert-Contains $importedValid 'result["rigRestPoseSource"]' "Applier may only use legacy imported rest-pose proof as compatibility evidence."
Assert-Contains $importedValid '"imported_unity_avatar_asset"' "Imported Avatar proof must name imported_unity_avatar_asset."

$requestResolveClip = Get-MethodBodyText $requestGenerator "private static AnimationClipAssetResolution ResolveUnityAnimationClipForSelection"
Assert-Contains $requestResolveClip "unityAssetPaths.animationClip" "Request generator must write explicit Unity AnimationClip asset paths when available."
Assert-Contains $requestResolveClip "animeStudioAssets.animation.anim" "Request generator must keep sidecar fallback traceable when no Unity AnimationClip asset is available."

$requestDiscoverClip = Get-MethodBodyText $requestGenerator "private static IReadOnlyDictionary<string, string> DiscoverImportedAnimationClips"
Assert-Contains $requestDiscoverClip "ImportedAnimationClip" "Request generator must scan ImportedAnimationClip assets."
Assert-Contains $requestDiscoverClip "*.anim" "ImportedAnimationClip scan must be limited to Unity .anim assets."
Assert-Contains $requestDiscoverClip "unityAnimationClips" "Request generator must read explicit unityAnimationClips settings."

$requestClipGuard = Get-MethodBodyText $requestGenerator "private static bool ValidateSuppliedUnityAnimationClipSelections"
Assert-Contains $requestClipGuard "--unity_animation_clip" "Batch guard must protect manual Unity AnimationClip overrides."
Assert-Contains $requestClipGuard "animationKeys.Length <= 1" "Manual Unity AnimationClip override must only allow one selected animation."
Assert-Contains $requestClipGuard "return false" "Manual Unity AnimationClip override must reject multi-animation selections."

$requestBatch = Get-MethodBodyText $requestGenerator "public static void GenerateBatchFromLibrary"
Assert-Contains $requestBatch "TryDescribeBlockedExplicitCandidate" "Batch Unity bake must explain blocked AnimatorController-context candidates before generic no-match errors."

$clipRecoveryRequests = Get-MethodBodyText $animationClipRecovery "private static List<AnimationClipRecoveryRequest> ReadRecoveryRequests"
Assert-Contains $clipRecoveryRequests "model_animation_candidates" "Imported AnimationClip recovery must read deterministic SQLite model-animation candidates."
Assert-Contains $clipRecoveryRequests "c.relation_source='explicit'" "Imported AnimationClip recovery must only use explicit Unity relations."
Assert-Contains $clipRecoveryRequests "animatorControllerContext.baseLayerClip.clip.pathId" "Imported AnimationClip recovery must include AnimatorController baseLayerClip context."
Assert-Contains $clipRecoveryRequests "ResolveActualBakeAnimation" "Imported AnimationClip recovery must resolve the actual body-driving clip before copying assets."
Assert-NotContains $clipRecoveryRequests "m.name LIKE `$modelLike" "Imported AnimationClip recovery must not rely on SQL LIKE for Browser absolute model paths."
Assert-NotContains $clipRecoveryRequests "a.name LIKE `$animationLike" "Imported AnimationClip recovery must not rely on SQL LIKE for Browser absolute animation paths."

$clipRecoveryActual = Get-MethodBodyText $animationClipRecovery "private static ActualBakeAnimation ResolveActualBakeAnimation"
Assert-Contains $clipRecoveryActual "animatorControllerContext" "Imported AnimationClip recovery must use AnimatorController context when available."
Assert-Contains $clipRecoveryActual "baseLayerClip" "Imported AnimationClip recovery must use baseLayerClip for auxiliary controller clips."
Assert-Contains $clipRecoveryActual "explicitCandidateAnimation" "Imported AnimationClip recovery must keep direct explicit candidate clips when no controller base clip is present."

$clipRecoverySelector = Get-MethodBodyText $animationClipRecovery "private static bool MatchesOneSelector"
Assert-Contains $clipRecoverySelector "NormalizeSelectorPath" "Imported AnimationClip recovery must normalize Browser absolute path selectors."
Assert-Contains $clipRecoverySelector "Path.GetFileNameWithoutExtension" "Imported AnimationClip recovery must match selected .anim filenames to SQLite asset outputs."
Assert-Contains $clipRecoverySelector "LooksLikePathSelector" "Imported AnimationClip recovery must avoid treating Windows absolute paths as regex."

$controllerRefresh = Get-MethodBodyText $controllerContextRefresh "public static string Refresh"
Assert-Contains $controllerRefresh "blockedReasonCounts" "AnimatorController context refresh report must expose blocked reason counts."
Assert-Contains $controllerRefresh "blockedItemsSample" "AnimatorController context refresh report must include blocked item samples."
Assert-Contains $controllerRefresh "missing_animator_controller_relation" "AnimatorController context refresh must distinguish missing Animator.controller relations."

$controllerRefreshSelector = Get-MethodBodyText $controllerContextRefresh "private static HashSet<string> SelectMatchingAssetOutputs"
Assert-Contains $controllerRefreshSelector "LIMIT 200000" "AnimatorController context refresh must scan enough SQLite assets for Browser absolute path selectors."
Assert-Contains $controllerRefreshSelector "MatchesSelector(selector, name, output)" "AnimatorController context refresh must filter selectors with path-aware C# matching."

$controllerSelector = Get-MethodBodyText $controllerContextRefresh "private static bool MatchesOneSelector"
Assert-Contains $controllerSelector "NormalizeSelectorPath" "AnimatorController context refresh must normalize Browser absolute path selectors."
Assert-Contains $controllerSelector "Path.GetFileNameWithoutExtension" "AnimatorController context refresh must match selected .gltf/.anim filenames to SQLite asset outputs."
Assert-Contains $controllerSelector "LooksLikePathSelector" "AnimatorController context refresh must avoid treating Windows absolute paths as regex."

$programMain = Get-MethodBodyText $program "public static void Main"
Assert-Before $programMain "TryPrepareAnimatorControllerBakeContext" "UnityBakeRequestGenerator.GenerateBatchFromLibrary" "CLI batch Unity bake must prepare AnimatorController context before generating requests."

$programPrepareContext = Get-MethodBodyText $program "private static void TryPrepareAnimatorControllerBakeContext"
Assert-Contains $programPrepareContext "o.UnityFileInspect" "CLI AnimatorController context preparation must require unity_file_inspect.json."
Assert-Contains $programPrepareContext "AnimatorControllerContextRefresher.Refresh" "CLI AnimatorController context preparation must refresh deterministic controller metadata."
Assert-Contains $programPrepareContext "AnimationClipAssetRecoveryExporter.Recover" "CLI AnimatorController context preparation must recover the actual Unity AnimationClip asset before bake."
Assert-Contains $programPrepareContext "o.PackAnimations ?? o.PreviewAnimation" "CLI AnimatorController context preparation must use the current selected animation filter."
Assert-Contains $programPrepareContext "throw new InvalidOperationException" "CLI AnimatorController context preparation must fail instead of silently continuing with stale metadata."

$batchCacheWrite = Get-MethodBodyText $requestGenerator "private static void UpsertBakeCache"
Assert-Contains $batchCacheWrite "requestedAnimationOutput" "Batch Unity bake cache must also record the original user-selected AnimatorController clip."
Assert-Contains $batchCacheWrite "UpsertBakeCacheRow" "Batch Unity bake cache must write an alias row for auxiliary clip -> baseLayerClip bake results."

$applyReport = Get-MethodBodyText $applier "public static string Apply"
Assert-Contains $applyReport "unityBakeRequestedAnimationClip" "Apply report must record requested Unity AnimationClip asset."
Assert-Contains $applyReport "unityBakeImportedAnimationClip" "Apply report must record imported Unity AnimationClip asset."
Assert-Contains $applyReport "unityBakeAnimationClipSource" "Apply report must record AnimationClip source."

$browserTrust = Get-MethodBodyText $browserCache "private static bool AvatarTrustSourceMatchesExplicitRequest"
Assert-Contains $browserTrust "ReportRequestHasExplicitAvatarAsset(root)" "Browser trust must inspect explicit Avatar asset requests."
Assert-Contains $browserTrust '"imported_unity_avatar_asset"' "Browser explicit Avatar request must require imported_unity_avatar_asset source."
Assert-Contains $browserTrust "ReportHasImportedAvatarAssetProof(root)" "Browser explicit Avatar request must require imported Avatar proof."

$browserTrustedReport = Get-MethodBodyText $browserCache "private static bool HasTrustedUnityBakeReport"
Assert-Contains $browserTrustedReport '"ok"' "Browser trusted Unity bake report must require ok/warning apply status."
Assert-Contains $browserTrustedReport '"warning"' "Browser trusted Unity bake report must allow warning only after other proof."
Assert-Contains $browserTrustedReport '"frameVaryingTracks"' "Browser trusted Unity bake report must require frame-varying tracks."
Assert-Contains $browserTrustedReport "frameVaryingTracks > 0" "Browser must not mark static-pose Unity bake as playable."
Assert-Contains $browserTrustedReport "HasTrustedAvatarBake" "Browser trusted Unity bake report must require Avatar trust."
Assert-Contains $browserTrustedReport "HasTrustedAnimationClipBake" "Browser trusted Unity bake report must require imported AnimationClip proof when an explicit Avatar asset request is used."

$browserAnimationClipTrust = Get-MethodBodyText $browserCache "private static bool HasTrustedAnimationClipBake"
Assert-Contains $browserAnimationClipTrust "ReportRequestHasExplicitAvatarAsset(root)" "Browser imported AnimationClip trust must only be mandatory for explicit Avatar asset requests."
Assert-Contains $browserAnimationClipTrust '"unityAssetPaths.animationClip"' "Browser imported AnimationClip trust must require Unity AnimationClip asset source."
Assert-Contains $browserAnimationClipTrust '"unityBakeImportedAnimationClip"' "Browser imported AnimationClip trust must require imported AnimationClip proof."

$browserEnsureUnityBake = Get-MethodBodyText $browserCache "public async Task<AnimationPreviewStatus> EnsureUnityBakeAsync"
Assert-Contains $browserEnsureUnityBake "var config = LibraryBrowserSettings.LoadEffective(_root)" "Browser Unity bake must load Unity settings before deciding AnimatorController context readiness."
Assert-Contains $browserEnsureUnityBake "animation.NeedsAnimatorControllerContext && string.IsNullOrWhiteSpace(config.UnityFileInspect)" "Browser must only block AnimatorController-context clips when no unity_file_inspect.json is available."
Assert-Contains $browserEnsureUnityBake "config.UnityFileInspect" "Browser Unity bake must pass unity_file_inspect.json into the CLI request."

$browserBuildUnityArgs = Get-MethodBodyText $browserCache "private static CliLauncher BuildExeLauncher"
Assert-Contains $browserBuildUnityArgs "--unity_file_inspect" "Browser CLI launcher must pass --unity_file_inspect when available."
Assert-Contains $browserBuildUnityArgs "File.Exists(unityFileInspect)" "Browser CLI launcher must only pass existing unity_file_inspect.json files."

Assert-Contains $browserSettings "UnityFileInspect" "Browser settings must keep a configured unity_file_inspect.json path."
Assert-Contains $browserSettings "ANIMESTUDIO_UNITY_FILE_INSPECT" "Browser settings must allow an environment-provided unity_file_inspect.json path."
Assert-Contains $browserSettings "DiscoverUnityFileInspect" "Browser settings must auto-discover library diagnostics unity_file_inspect.json files."

$browserBakeCache = Get-MethodBodyText $browserCache "private Dictionary<string, AnimationPreviewStatus> LoadSqliteBakeCacheCore"
Assert-Contains $browserBakeCache "HasTrustedUnityBakeReport(applyReport)" "Browser SQLite bake cache must require trusted apply report before playable."
Assert-Contains $browserBakeCache "IsUnityBakedGltf(bakedGltf)" "Browser SQLite bake cache must require glTF Unity bake marker before playable."
Assert-Contains $browserBakeCache "hasTrustedBakedGltf" "Browser SQLite bake cache may mark playable only after trusted proof."
Assert-Contains $browserBakeCache "? new AnimationPreviewStatus" "Browser SQLite bake cache must create the playable status only through the trusted branch."
Assert-Contains $browserBakeCache "ShouldHideUntrustedBakedGltf" "Browser SQLite bake cache must not expose stale glTF paths when imported AnimationClip proof is missing."

$browserHideUntrusted = Get-MethodBodyText $browserCache "private static bool ShouldHideUntrustedBakedGltf"
Assert-Contains $browserHideUntrusted "RequiresImportedAnimationClip" "Browser stale glTF hiding must only apply to explicit Avatar asset requests that require imported AnimationClip proof."
Assert-Contains $browserHideUntrusted '"animeStudioAssets.animation.anim"' "Browser stale glTF hiding must catch old AnimeStudio .anim bake inputs."
Assert-Contains $browserHideUntrusted "ImportedAnimationClip" "Browser stale glTF hiding must catch missing imported AnimationClip proof."

$productionSource = Get-MethodBodyText $browserCache "private static bool IsProductionAvatarTrustSource"
Assert-Contains $productionSource '"internal_solver"' "Browser must reject internal_solver trust sources."
Assert-Contains $productionSource '"avatar_constant"' "Browser must reject avatar_constant trust sources."
Assert-Contains $productionSource '"oracle"' "Browser must reject oracle trust sources."

Assert-Contains $unityReadme "unityAssetPaths.avatarAsset" "Unity bake README must document explicit Avatar asset requests."
Assert-Contains $unityReadme "BuildHumanAvatar" "Unity bake README must document BuildHumanAvatar fallback limits."
Assert-Contains $standards "unityAssetPaths.avatarAsset" "Project standards must document explicit Avatar asset requests."
Assert-Contains $standards "BuildHumanAvatar" "Project standards must document BuildHumanAvatar fallback limits."

Write-Output "OK: Unity Avatar asset trust gate rejects fallback trust for explicit avatarAsset requests."
