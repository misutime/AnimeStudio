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
$browserCache = Read-RepoFile "AnimeStudio.LibraryBrowser\AnimationPreviewCache.cs"
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
Assert-Contains $bakeResult 'restPoseSource, "imported_unity_avatar_asset"' "Imported Avatar validity must require imported rest pose source."

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

$browserBakeCache = Get-MethodBodyText $browserCache "private Dictionary<string, AnimationPreviewStatus> LoadSqliteBakeCacheCore"
Assert-Contains $browserBakeCache "HasTrustedUnityBakeReport(applyReport)" "Browser SQLite bake cache must require trusted apply report before playable."
Assert-Contains $browserBakeCache "IsUnityBakedGltf(bakedGltf)" "Browser SQLite bake cache must require glTF Unity bake marker before playable."
Assert-Contains $browserBakeCache "hasTrustedBakedGltf" "Browser SQLite bake cache may mark playable only after trusted proof."
Assert-Contains $browserBakeCache "? new AnimationPreviewStatus" "Browser SQLite bake cache must create the playable status only through the trusted branch."

$productionSource = Get-MethodBodyText $browserCache "private static bool IsProductionAvatarTrustSource"
Assert-Contains $productionSource '"internal_solver"' "Browser must reject internal_solver trust sources."
Assert-Contains $productionSource '"avatar_constant"' "Browser must reject avatar_constant trust sources."
Assert-Contains $productionSource '"oracle"' "Browser must reject oracle trust sources."

Assert-Contains $unityReadme "unityAssetPaths.avatarAsset" "Unity bake README must document explicit Avatar asset requests."
Assert-Contains $unityReadme "BuildHumanAvatar" "Unity bake README must document BuildHumanAvatar fallback limits."
Assert-Contains $standards "unityAssetPaths.avatarAsset" "Project standards must document explicit Avatar asset requests."
Assert-Contains $standards "BuildHumanAvatar" "Project standards must document BuildHumanAvatar fallback limits."

Write-Output "OK: Unity Avatar asset trust gate rejects fallback trust for explicit avatarAsset requests."
