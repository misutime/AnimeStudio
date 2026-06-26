param(
    [string]$GameRoot = "C:\Game163\program",
    [string]$SourceRoot = "",
    [string]$SourceIndex = "D:\Assets\Naraka\SourceIndex_Full_HeaderFix1\unity_source_index.db",
    [string]$AnimationSourceIndex = "D:\Assets\Naraka\SourceIndex_Naraka_TosMini_Current\unity_source_index.db",
    [string]$AnimationSidecar = "D:\Assets\Naraka\Dijiang_AttackA8_FullDecodedSidecar_Current\Animations\assets\res\animclipadapter\05\mo_pve_b_dijiang_attack_a8_01.animation_asset.json",
    [string]$AnimationPreviewAvatar = "mo_pve_b_dijiang_01_skeletonAvatar",
    [string]$ShaderBoundarySampleRoot = "D:\Assets\Naraka\FaceMaleBattle_ShaderBoundary_Current",
    [string]$StaticEnvironmentSampleRoot = "D:\Assets\Naraka\Smoke_static_jisui_device_bigtree_04_Current",
    [string]$CharacterCandidateSampleRoot = "D:\Assets\Naraka\Naraka_CompleteCharacterCandidate_SamuraiGhost_BundleRoot_Current",
    [string]$CharacterCandidateSourceIndexBundle = "b\6\b6449028544fa466",
    [string]$CharacterCandidateSourceIndexSerializedFile = "CAB-43d9a2106c54892c7f775b8d7ab8b193",
    [string]$CharacterCandidateSourceIndexAnimatorName = "ch_m_japan_samurai_ghost",
    [long]$CharacterCandidateSourceIndexRootPathId = 7640773285473327857,
    [string]$OutputRoot = "D:\Assets\Naraka\Naraka_FirstUsableSmoke_Current",
    [string]$Configuration = "Release",
    [switch]$KeepExisting,
    [switch]$SkipBrowserValidation,
    [switch]$SkipGltfValidation,
    [switch]$SkipAnimationDiagnostic
)

$ErrorActionPreference = "Stop"

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Label,
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    Write-Host ""
    Write-Host "== $Label =="
    if ($env:ANIMESTUDIO_SMOKE_DEBUG_ARGS -eq "1") {
        Write-Host ("Args({0}): {1}" -f $Arguments.Count, ($Arguments -join " | "))
    }
    & $FilePath @($Arguments)
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed with exit code $LASTEXITCODE"
    }
};

function Test-FileRequired {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    if (!(Test-Path -LiteralPath $Path)) {
        throw "$Label not found: $Path"
    }
};

function ConvertTo-SmokeText {
    param(
        [object]$Value,
        [string]$Default = "unknown"
    )

    if ($null -eq $Value) {
        return $Default
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $Default
    }

    return $text
}

function ConvertTo-SmokeJsonLiteral {
    param(
        [object]$Value,
        [int]$Depth = 10
    )

    if ($null -eq $Value) {
        return "null"
    }

    $json = $Value | ConvertTo-Json -Depth $Depth -Compress
    if ([string]::IsNullOrWhiteSpace($json)) {
        return "null"
    }

    return $json
}

function ConvertTo-SqliteTextLiteral {
    param(
        [string]$Value
    )

    if ($null -eq $Value) {
        return "NULL"
    }

    return "'" + $Value.Replace("'", "''") + "'"
}

function Read-SourceModelScriptAnimationDiagnostic {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Selector,
        [Parameter(Mandatory = $true)]
        [string]$OutputRoot
    )

    $report = Join-Path $OutputRoot "source_model_animation_candidates.json"
    Test-FileRequired -Path $report -Label "source_model_animation_candidates.json"

    $json = Get-Content -LiteralPath $report -Raw -Encoding UTF8 | ConvertFrom-Json
    $scriptRows = @($json.scriptAnimationComponentDiagnostics)
    $invalidBoundaryRows = @($scriptRows | Where-Object { $_.diagnosticOnly -ne $true -or $_.notDefaultModelAnimationRelation -ne $true })
    $visibleRendererRows = @($scriptRows | Where-Object { [Convert]::ToInt64($_.gameObject.visibleRendererCount) -gt 0 })
    $animatorRows = @($scriptRows | Where-Object { [Convert]::ToInt64($_.gameObject.animatorCount) -gt 0 })
    $firstRow = $null
    if ($scriptRows.Count -gt 0) {
        $firstRow = $scriptRows[0]
    }
    $firstScriptName = $null
    $firstFieldPath = $null
    $firstClipName = $null
    if ($null -ne $firstRow) {
        $firstScriptName = [string]$firstRow.monoBehaviour.scriptName
        $firstFieldPath = [string]$firstRow.reference.path
        $firstClipName = [string]$firstRow.reference.animation.name
    }
    $selectedModelCount = [Convert]::ToInt64($json.selectedModelCount)
    $candidateCount = [Convert]::ToInt64($json.candidateCount)
    $scriptAnimationRows = [Convert]::ToInt64($scriptRows.Count)
    $invalidBoundaryRowCount = [Convert]::ToInt64($invalidBoundaryRows.Count)
    $visibleRendererRowCount = [Convert]::ToInt64($visibleRendererRows.Count)
    $animatorRowCount = [Convert]::ToInt64($animatorRows.Count)

    # 这里只读报告并压成 smoke 摘要；脚本动画语义仍然需要游戏脚本解释，不能升级成默认候选。
    $summaryJsonLines = @()
    $summaryJsonLines += "{"
    $summaryJsonLines += '  "selector": ' + (ConvertTo-SmokeJsonLiteral $Selector) + ","
    $summaryJsonLines += '  "outputRoot": ' + (ConvertTo-SmokeJsonLiteral $OutputRoot) + ","
    $summaryJsonLines += '  "report": ' + (ConvertTo-SmokeJsonLiteral $report) + ","
    $summaryJsonLines += '  "selectedModelCount": ' + (ConvertTo-SmokeJsonLiteral $selectedModelCount) + ","
    $summaryJsonLines += '  "candidateCount": ' + (ConvertTo-SmokeJsonLiteral $candidateCount) + ","
    $summaryJsonLines += '  "scriptAnimationRows": ' + (ConvertTo-SmokeJsonLiteral $scriptAnimationRows) + ","
    $summaryJsonLines += '  "invalidBoundaryRows": ' + (ConvertTo-SmokeJsonLiteral $invalidBoundaryRowCount) + ","
    $summaryJsonLines += '  "visibleRendererRows": ' + (ConvertTo-SmokeJsonLiteral $visibleRendererRowCount) + ","
    $summaryJsonLines += '  "animatorRows": ' + (ConvertTo-SmokeJsonLiteral $animatorRowCount) + ","
    $summaryJsonLines += '  "firstScriptName": ' + (ConvertTo-SmokeJsonLiteral $firstScriptName) + ","
    $summaryJsonLines += '  "firstFieldPath": ' + (ConvertTo-SmokeJsonLiteral $firstFieldPath) + ","
    $summaryJsonLines += '  "firstClipName": ' + (ConvertTo-SmokeJsonLiteral $firstClipName)
    $summaryJsonLines += "}"
    return (($summaryJsonLines -join [Environment]::NewLine) | ConvertFrom-Json)
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$targetFramework = "net9.0-windows"
$cli = Join-Path $repoRoot "AnimeStudio.CLI\bin\$Configuration\$targetFramework\AnimeStudio.CLI.exe"

if (!(Test-Path -LiteralPath $cli)) {
    dotnet build (Join-Path $repoRoot "AnimeStudio.CLI\AnimeStudio.CLI.csproj") -c $Configuration
}

Test-FileRequired -Path $cli -Label "AnimeStudio CLI"

if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $streamingAssets = Join-Path $GameRoot "NarakaBladepoint_Data\StreamingAssets"
    if (Test-Path -LiteralPath $streamingAssets) {
        $SourceRoot = $streamingAssets
    }
    else {
        $SourceRoot = $GameRoot
    }
}

Test-FileRequired -Path $SourceRoot -Label "Naraka source root"
Test-FileRequired -Path $SourceIndex -Label "Naraka unity_source_index.db"
$characterCandidateSourceIndexSource = (Join-Path $SourceRoot $CharacterCandidateSourceIndexBundle) + "|" + $CharacterCandidateSourceIndexSerializedFile
$characterCandidateSourceIndexRelativeSource = ($CharacterCandidateSourceIndexBundle -replace "\\", "/") + "|" + $CharacterCandidateSourceIndexSerializedFile

if (!$KeepExisting -and (Test-Path -LiteralPath $OutputRoot)) {
    Remove-Item -LiteralPath $OutputRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

$probeOutput = Join-Path $OutputRoot "SourceInputProbe"
$libraryOutput = Join-Path $OutputRoot "RepresentativeModels"
$animationOutput = Join-Path $OutputRoot "AnimationDiagnostic_DijiangA8"
$scriptAnimationHadiOutput = Join-Path $OutputRoot "SourceModelAnimation_HadiBody_ScriptComponentDiagnostic"
$scriptAnimationFxOutput = Join-Path $OutputRoot "SourceModelAnimation_FxAttack_ScriptComponentDiagnostic"
$representativeGltfValidationStatus = if ($SkipGltfValidation) { "skipped" } else { "toolMissing" }
$animationGltfValidationStatus = if ($SkipGltfValidation -or $SkipAnimationDiagnostic) { "skipped" } else { "toolMissing" }
$shaderBoundaryGltfValidationStatus = if ($SkipGltfValidation) { "skipped" } else { "toolMissing" }
$shaderBoundaryStatus = "notChecked"
$shaderBoundaryRule = "Naraka private/layered shader boundary: raw texture slots are preserved; glTF PBR is a degraded preview, not original game shading, and must not be treated as texture loss."
$shaderBoundarySummaryJson = $null
$shaderBoundaryMaterialSidecarRows = $null
$shaderBoundaryCustomShaderRows = $null
$shaderBoundaryGltf = $null
$staticEnvironmentStatus = "notChecked"
$staticEnvironmentRule = "Static environment/prop meshes are an explicit extension sample. They prove mesh, UV, material slots, textures and AssetLibrary index health, but they do not change the default prefab/Animator-focused Library scope."
$staticEnvironmentSummaryJson = $null
$staticEnvironmentValidationJson = $null
$staticEnvironmentGltfValidationStatus = if ($SkipGltfValidation) { "skipped" } else { "toolMissing" }
$staticEnvironmentGltf = $null
$staticEnvironmentTextureLinkErrors = $null
$staticEnvironmentMaterialSidecarRows = $null
$staticEnvironmentModelRows = $null
$characterCandidateStatus = "notChecked"
$characterCandidateRule = "This sample is a stronger humanoid/character candidate selected from Unity source-index Animator.avatar -> GameObject -> SkinnedMeshRenderer evidence. It proves model, skin, material and texture health for a large skinned candidate, but still does not create production animation bindings."
$characterCandidateSummaryJson = $null
$characterCandidateValidationJson = $null
$characterCandidateGltfValidationStatus = if ($SkipGltfValidation) { "skipped" } else { "toolMissing" }
$characterCandidateGltf = $null
$characterCandidateTextureLinkErrors = $null
$characterCandidateMaterialSidecarRows = $null
$characterCandidateModelRows = $null
$characterCandidateModelAnimationCandidateRows = $null
$characterCandidateModelAnimationRelationRows = $null
$characterCandidateRelationAnimationRows = $null
$characterCandidateMaxBoundsSize = $null
$characterCandidateSkinJointCount = $null
$hadiModularBoundary = [pscustomobject]@{
    status = "notChecked"
    rule = "Hadi body is a usable modular body/clothing asset, not a complete character. It must stay marked modular_incomplete and blocked from production animation smoke until deterministic Face/Hair assembly is available."
}
$characterCandidateSourceIndexBoundary = [pscustomobject]@{
    status = "notChecked"
    rule = "SamuraiGhost bundle source-index boundary: Animator.avatar and same-bundle Humanoid/Muscle clips are diagnostic evidence only; production animation binding still requires explicit Animator.controller, Animation.clip or AnimatorController.clip relation plus visual validation."
    source = $characterCandidateSourceIndexSource
    relativeSource = $characterCandidateSourceIndexRelativeSource
    animatorName = $CharacterCandidateSourceIndexAnimatorName
    rootPathId = $CharacterCandidateSourceIndexRootPathId
}
$sourceIndexAvatarAnimatorDomains = [pscustomobject]@{
    status = "notChecked"
    rule = "Animator.avatar is only source-index evidence. It can explain model skeleton context, but it never creates a default model-animation binding without explicit clip/controller relation and model validation."
    totalAnimators = 0
    withController = 0
    domainCounts = @()
}
$sourceIndexLegacyAnimationClipDomains = [pscustomobject]@{
    status = "notChecked"
    rule = "Legacy Animation.clip edges are explicit Unity references, but Naraka's current matches are VFX/marker style diagnostics, not character body production animation candidates."
    totalRelations = 0
    resolvedTargets = 0
    unresolvedTargets = 0
    domainCounts = @()
}
$scriptAnimationComponentDiagnostics = [pscustomobject]@{
    status = "notChecked"
    rule = "Selected-model script AnimationClip PPtr diagnostics are local evidence only. They must stay diagnosticOnly/notDefaultModelAnimationRelation and never create default model-animation candidates without script semantics, model validation and visual validation."
    hadiBody = $null
    fxAttack = $null
}
$animationDiagnosticStatus = if ($SkipAnimationDiagnostic) { "skipped" } else { "pending" }
$animationReportJson = $null
$animationGltfPath = $null
$browserValidationStatus = if ($SkipBrowserValidation) { "skipped" } else { "toolMissing" }
$thumbnailStatus = if ($SkipBrowserValidation) { "skipped" } else { "toolMissing" }

Write-Host ""
Write-Host "== Probe Naraka input =="
& $cli $SourceRoot $probeOutput "--game" "Naraka" "--probe_source_input"
if ($LASTEXITCODE -ne 0) {
    throw "Probe Naraka input failed with exit code $LASTEXITCODE"
}

# 这些样本来自完整源索引的确定性闭包，用来同时覆盖角色部件、武器和普通道具。
Write-Host ""
Write-Host "== Export Naraka representative model smoke =="
& $cli $SourceRoot $libraryOutput "--game" "Naraka" "--mode" "Library" "--group_assets" "ByLibrary" "--profile_3d" "Core" "--model_format" "Gltf" "--texture_mode" "Png" "--animation_package" "Separate" "--fbx_animation" "Skip" "--source_index" $SourceIndex "--source_files" "4\c\4c08b7069a411750" "d\1\d1d0bc7b6c107e00" "0\f\0f2ab2b1ab070ac0" "--path_ids" "-6619473669887381141" "3879445205109982761" "3817277305598733592"
if ($LASTEXITCODE -ne 0) {
    throw "Export Naraka representative model smoke failed with exit code $LASTEXITCODE"
}

Write-Host ""
Write-Host "== Rebuild AssetLibrary v1 index =="
& $cli "--build_sqlite_index" $libraryOutput "--source_index" $SourceIndex "--game" "Naraka" "--skip_sqlite_file_index"
if ($LASTEXITCODE -ne 0) {
    throw "Rebuild AssetLibrary v1 index failed with exit code $LASTEXITCODE"
}

$manifest = Join-Path $libraryOutput "asset_library.json"
$db = Join-Path $libraryOutput "library_index.db"
$sqliteSummary = Join-Path $libraryOutput "sqlite_index_summary.json"
$validation = Join-Path $libraryOutput "model_validation.json"
$assetCatalog = Join-Path $libraryOutput "asset_catalog.jsonl"
$gltf = Join-Path $libraryOutput "Models\assets\res\prefab\actor_visual_part\ch_m_hadi\ch_m_hadi_lv_s9\ch_m_hadi_lv_s9.gltf"
$bowGltf = Join-Path $libraryOutput "Models\assets\res\prefab\drop_item_generate\weapon\weapon_drop_bow_dongjun\weapon_drop_bow_dongjun.gltf"
$deviceGltf = Join-Path $libraryOutput "Models\assets\res\prefab\device_generate\device_hongbao_02\device_hongbao_02.gltf"
$representativeGltfs = @($gltf, $bowGltf, $deviceGltf)

Test-FileRequired -Path $manifest -Label "asset_library.json"
Test-FileRequired -Path $db -Label "library_index.db"
Test-FileRequired -Path $sqliteSummary -Label "sqlite_index_summary.json"
Test-FileRequired -Path $validation -Label "model_validation.json"
Test-FileRequired -Path $assetCatalog -Label "asset_catalog.jsonl"
Test-FileRequired -Path $gltf -Label "Hadi glTF"
Test-FileRequired -Path $bowGltf -Label "Bow prop glTF"
Test-FileRequired -Path $deviceGltf -Label "Device prop glTF"

if (!$SkipGltfValidation) {
    $gltfTransform = Get-Command "gltf-transform.cmd" -ErrorAction SilentlyContinue
    if ($null -ne $gltfTransform) {
        foreach ($representativeGltf in $representativeGltfs) {
            Invoke-Checked -Label "Validate representative glTF" -FilePath $gltfTransform.Source -Arguments @("validate", $representativeGltf)
        }
        $representativeGltfValidationStatus = "ok"
    }
    else {
        Write-Warning "gltf-transform.cmd not found; skipped glTF validator."
    }
}

if (!$SkipAnimationDiagnostic) {
    Test-FileRequired -Path $AnimationSourceIndex -Label "Naraka animation diagnostic source index"
    Test-FileRequired -Path $AnimationSidecar -Label "Naraka animation sidecar"

    # 手动动画诊断只证明路径恢复和独立 glTF 写出，不创建默认模型-动画推荐关系。
    Write-Host ""
    Write-Host "== Export Dijiang A8 standalone animation diagnostic =="
    & $cli "--export_animation_gltf_from_files" $gltf "--preview_animation" $AnimationSidecar "--preview_output" $animationOutput "--source_index" $AnimationSourceIndex "--preview_avatar" $AnimationPreviewAvatar
    if ($LASTEXITCODE -ne 0) {
        throw "Export Dijiang A8 standalone animation diagnostic failed with exit code $LASTEXITCODE"
    }
    $animationDiagnosticStatus = "ok"

    $animationReport = Join-Path $animationOutput "standalone_animation_gltf_report.json"
    Test-FileRequired -Path $animationReport -Label "standalone_animation_gltf_report.json"
    $animationReportJson = Get-Content -LiteralPath $animationReport -Raw -Encoding UTF8 | ConvertFrom-Json

    if ($animationReportJson.status -ne "ok") {
        throw "Animation diagnostic did not finish as ok: $($animationReportJson.status) $($animationReportJson.reason)"
    }
    if ($animationReportJson.avatarInjection.diagnosticOnly -ne $true -or $animationReportJson.avatarInjection.notDefaultModelAnimationRelation -ne $true) {
        throw "Animation diagnostic report lost diagnostic-only relation boundary."
    }

    if (![string]::IsNullOrWhiteSpace($animationReportJson.gltf)) {
        $animationGltfPath = [string]$animationReportJson.gltf
        Test-FileRequired -Path $animationGltfPath -Label "standalone animation glTF"
        if (!$SkipGltfValidation) {
            $gltfTransform = Get-Command "gltf-transform.cmd" -ErrorAction SilentlyContinue
            if ($null -ne $gltfTransform) {
                Invoke-Checked -Label "Validate Dijiang A8 animation glTF" -FilePath $gltfTransform.Source -Arguments @("validate", $animationGltfPath)
                $animationGltfValidationStatus = "ok"
            }
        }
    }
}

Write-Host ""
Write-Host "== List Naraka selected-model script animation diagnostics =="
Invoke-Checked -Label "List Hadi body source-model animations" -FilePath $cli -Arguments @(
    "--list_source_model_animations", $SourceIndex,
    "--preview_model", "ch_m_hadi_lv_s9",
    "--preview_output", $scriptAnimationHadiOutput,
    "--source_candidate_limit", "40")
Invoke-Checked -Label "List fxattack script source-model animations" -FilePath $cli -Arguments @(
    "--list_source_model_animations", $SourceIndex,
    "--preview_model", "fxattack_male_sw_attack_heavy_02",
    "--preview_output", $scriptAnimationFxOutput,
    "--source_candidate_limit", "20")

$hadiScriptAnimationDiagnostic = Read-SourceModelScriptAnimationDiagnostic -Selector "ch_m_hadi_lv_s9" -OutputRoot $scriptAnimationHadiOutput
$fxScriptAnimationDiagnostic = Read-SourceModelScriptAnimationDiagnostic -Selector "fxattack_male_sw_attack_heavy_02" -OutputRoot $scriptAnimationFxOutput

if ($hadiScriptAnimationDiagnostic.selectedModelCount -lt 1) {
    throw "Hadi body script animation diagnostic did not select any source model."
}
if ($hadiScriptAnimationDiagnostic.candidateCount -ne 0 -or $hadiScriptAnimationDiagnostic.scriptAnimationRows -ne 0) {
    throw "Hadi body unexpectedly produced default or script animation rows. candidates=$($hadiScriptAnimationDiagnostic.candidateCount) scriptRows=$($hadiScriptAnimationDiagnostic.scriptAnimationRows)"
}
if ($fxScriptAnimationDiagnostic.selectedModelCount -lt 1) {
    throw "FxAttack script animation diagnostic did not select any source model."
}
if ($fxScriptAnimationDiagnostic.candidateCount -ne 0) {
    throw "FxAttack script diagnostic unexpectedly produced default animation candidates: $($fxScriptAnimationDiagnostic.candidateCount)"
}
if ($fxScriptAnimationDiagnostic.scriptAnimationRows -lt 1) {
    throw "FxAttack script diagnostic lost local MonoBehaviour -> AnimationClip evidence."
}
if ($fxScriptAnimationDiagnostic.invalidBoundaryRows -ne 0) {
    throw "FxAttack script diagnostic lost diagnostic-only relation boundary. invalidRows=$($fxScriptAnimationDiagnostic.invalidBoundaryRows)"
}
if ($fxScriptAnimationDiagnostic.visibleRendererRows -ne 0 -or $fxScriptAnimationDiagnostic.animatorRows -lt 1) {
    throw "FxAttack script diagnostic no longer looks like a non-rendered animation control node. visibleRendererRows=$($fxScriptAnimationDiagnostic.visibleRendererRows) animatorRows=$($fxScriptAnimationDiagnostic.animatorRows)"
}

$scriptAnimationComponentDiagnostics = [pscustomobject]@{
    status = "ok"
    rule = $scriptAnimationComponentDiagnostics.rule
    hadiBody = $hadiScriptAnimationDiagnostic
    fxAttack = $fxScriptAnimationDiagnostic
}

if (!$SkipBrowserValidation) {
    $browserCli = "D:\misutime\UnrealExporter\dist\AssetLibraryBrowser.Cli\AssetLibraryBrowser.Cli.exe"
    if (Test-Path -LiteralPath $browserCli) {
        Invoke-Checked -Label "Validate AssetLibrary v1" -FilePath $browserCli -Arguments @("validate-library", $libraryOutput)
        $browserValidationStatus = "ok"

        Invoke-Checked -Label "Render browser thumbnail" -FilePath $browserCli -Arguments @("build-thumbnails", $libraryOutput, "1", "3")
        $thumbnailStatus = "ok"
    }
    else {
        Write-Warning "AssetLibraryBrowser.Cli not found; skipped browser validation."
    }
}

if (![string]::IsNullOrWhiteSpace($ShaderBoundarySampleRoot)) {
    if (Test-Path -LiteralPath $ShaderBoundarySampleRoot) {
        Write-Host ""
        Write-Host "== Validate Naraka shader boundary sample =="

        $shaderBoundaryGltf = Join-Path $ShaderBoundarySampleRoot "MonoBehaviour_lod0.gltf"
        $shaderBoundaryReport = Join-Path $ShaderBoundarySampleRoot "MATERIAL_REPORT.md"
        $shaderBoundarySqliteSummary = Join-Path $ShaderBoundarySampleRoot "sqlite_index_summary.json"
        $shaderBoundaryDb = Join-Path $ShaderBoundarySampleRoot "library_index.db"

        Test-FileRequired -Path $shaderBoundaryGltf -Label "shader boundary glTF"
        Test-FileRequired -Path $shaderBoundaryReport -Label "shader boundary MATERIAL_REPORT.md"
        Test-FileRequired -Path $shaderBoundarySqliteSummary -Label "shader boundary sqlite_index_summary.json"
        Test-FileRequired -Path $shaderBoundaryDb -Label "shader boundary library_index.db"

        $shaderBoundarySummaryJson = Get-Content -LiteralPath $shaderBoundarySqliteSummary -Raw -Encoding UTF8 | ConvertFrom-Json
        $shaderBoundaryQuality = $shaderBoundarySummaryJson.qualityGates
        if ($null -eq $shaderBoundaryQuality) {
            throw "Shader boundary sqlite_index_summary.json lost qualityGates section."
        }
        if ([long]$shaderBoundaryQuality.textureLinkErrors -ne 0) {
            throw "Shader boundary sample has texture link errors: $($shaderBoundaryQuality.textureLinkErrors)"
        }
        if ([long]$shaderBoundaryQuality.customShaderRequiredSidecars -lt 1 -or [long]$shaderBoundaryQuality.layeredMaterialUnresolvedSidecars -lt 1) {
            throw "Shader boundary sample did not preserve custom shader/layered material markers."
        }

        $sqlite3 = Get-Command "sqlite3.exe" -ErrorAction SilentlyContinue
        if ($null -ne $sqlite3) {
            $shaderBoundaryMaterialSidecarRowsText = & $sqlite3.Source -readonly -batch -noheader $shaderBoundaryDb "SELECT COUNT(*) FROM material_sidecars;"
            if ($LASTEXITCODE -ne 0) {
                throw "sqlite3 failed while counting shader boundary material_sidecars."
            }
            $shaderBoundaryCustomShaderRowsText = & $sqlite3.Source -readonly -batch -noheader $shaderBoundaryDb "SELECT COUNT(*) FROM material_sidecars WHERE custom_shader_required = 1 AND layered_material_unresolved = 1 AND pbr_preview_status = 'bestEffortDegradedPreview';"
            if ($LASTEXITCODE -ne 0) {
                throw "sqlite3 failed while checking shader boundary custom shader material_sidecars."
            }
            $shaderBoundaryMaterialSidecarRows = [long]$shaderBoundaryMaterialSidecarRowsText
            $shaderBoundaryCustomShaderRows = [long]$shaderBoundaryCustomShaderRowsText
            if ($shaderBoundaryCustomShaderRows -lt 1) {
                throw "Shader boundary material_sidecars table did not preserve degraded custom shader rows."
            }
        }
        else {
            Write-Warning "sqlite3.exe not found; shader boundary material_sidecars table check skipped."
        }

        if (!$SkipGltfValidation) {
            $gltfTransform = Get-Command "gltf-transform.cmd" -ErrorAction SilentlyContinue
            if ($null -ne $gltfTransform) {
                Invoke-Checked -Label "Validate shader boundary glTF" -FilePath $gltfTransform.Source -Arguments @("validate", $shaderBoundaryGltf)
                $shaderBoundaryGltfValidationStatus = "ok"
            }
        }

        $shaderBoundaryStatus = "ok"
    }
    else {
        $shaderBoundaryStatus = "missing"
        Write-Warning "Shader boundary sample not found; skipped: $ShaderBoundarySampleRoot"
    }
}
else {
    $shaderBoundaryStatus = "skipped"
}

if (![string]::IsNullOrWhiteSpace($StaticEnvironmentSampleRoot)) {
    if (Test-Path -LiteralPath $StaticEnvironmentSampleRoot) {
        Write-Host ""
        Write-Host "== Validate Naraka static environment extension sample =="

        $staticEnvironmentAssetLibrary = Join-Path $StaticEnvironmentSampleRoot "asset_library.json"
        $staticEnvironmentValidation = Join-Path $StaticEnvironmentSampleRoot "model_validation.json"
        $staticEnvironmentSqliteSummary = Join-Path $StaticEnvironmentSampleRoot "sqlite_index_summary.json"
        $staticEnvironmentDb = Join-Path $StaticEnvironmentSampleRoot "library_index.db"
        $staticEnvironmentReportFiles = @(Get-ChildItem -LiteralPath $StaticEnvironmentSampleRoot -Recurse -Filter "MATERIAL_REPORT.md" -File)
        $staticEnvironmentGltfs = @(Get-ChildItem -LiteralPath (Join-Path $StaticEnvironmentSampleRoot "Models") -Recurse -Filter "*.gltf" -File)

        Test-FileRequired -Path $staticEnvironmentAssetLibrary -Label "static environment asset_library.json"
        Test-FileRequired -Path $staticEnvironmentValidation -Label "static environment model_validation.json"
        Test-FileRequired -Path $staticEnvironmentSqliteSummary -Label "static environment sqlite_index_summary.json"
        Test-FileRequired -Path $staticEnvironmentDb -Label "static environment library_index.db"
        if ($staticEnvironmentReportFiles.Count -lt 1) {
            throw "Static environment sample is missing MATERIAL_REPORT.md."
        }
        if ($staticEnvironmentGltfs.Count -lt 1) {
            throw "Static environment sample is missing exported glTF models."
        }
        $staticEnvironmentGltf = $staticEnvironmentGltfs[0].FullName

        $staticEnvironmentValidationJson = Get-Content -LiteralPath $staticEnvironmentValidation -Raw -Encoding UTF8 | ConvertFrom-Json
        $staticEnvironmentSummaryJson = Get-Content -LiteralPath $staticEnvironmentSqliteSummary -Raw -Encoding UTF8 | ConvertFrom-Json
        if ([long]$staticEnvironmentValidationJson.totals.models -lt 1 -or [long]$staticEnvironmentValidationJson.totals.ok -lt 1) {
            throw "Static environment sample has no ok model validation result."
        }
        if ([long]$staticEnvironmentValidationJson.totals.withTextures -lt 1) {
            throw "Static environment sample has no validated texture usage."
        }
        if ([long]$staticEnvironmentSummaryJson.counts.textureAssets -lt 1 -or [long]$staticEnvironmentSummaryJson.counts.materialSidecars -lt 1) {
            throw "Static environment sample lost texture assets or material sidecars."
        }

        $sqlite3 = Get-Command "sqlite3.exe" -ErrorAction SilentlyContinue
        if ($null -ne $sqlite3) {
            $staticEnvironmentTextureLinkErrorsText = & $sqlite3.Source -readonly -batch -noheader $staticEnvironmentDb "SELECT COUNT(*) FROM texture_links WHERE link_error IS NOT NULL AND trim(link_error) <> '';"
            if ($LASTEXITCODE -ne 0) {
                throw "sqlite3 failed while checking static environment texture link errors."
            }
            $staticEnvironmentMaterialSidecarRowsText = & $sqlite3.Source -readonly -batch -noheader $staticEnvironmentDb "SELECT COUNT(*) FROM material_sidecars;"
            if ($LASTEXITCODE -ne 0) {
                throw "sqlite3 failed while counting static environment material_sidecars."
            }
            $staticEnvironmentModelRowsText = & $sqlite3.Source -readonly -batch -noheader $staticEnvironmentDb "SELECT COUNT(*) FROM assets WHERE kind = 'Model';"
            if ($LASTEXITCODE -ne 0) {
                throw "sqlite3 failed while counting static environment model assets."
            }
            $staticEnvironmentTextureLinkErrors = [long]$staticEnvironmentTextureLinkErrorsText
            $staticEnvironmentMaterialSidecarRows = [long]$staticEnvironmentMaterialSidecarRowsText
            $staticEnvironmentModelRows = [long]$staticEnvironmentModelRowsText
            if ($staticEnvironmentTextureLinkErrors -ne 0) {
                throw "Static environment sample has texture link errors: $staticEnvironmentTextureLinkErrors"
            }
            if ($staticEnvironmentMaterialSidecarRows -lt 1 -or $staticEnvironmentModelRows -lt 1) {
                throw "Static environment sample lost material sidecar or model rows in library_index.db."
            }
        }
        else {
            Write-Warning "sqlite3.exe not found; static environment table checks skipped."
        }

        if (!$SkipGltfValidation) {
            $gltfTransform = Get-Command "gltf-transform.cmd" -ErrorAction SilentlyContinue
            if ($null -ne $gltfTransform) {
                Invoke-Checked -Label "Validate static environment glTF" -FilePath $gltfTransform.Source -Arguments @("validate", $staticEnvironmentGltf)
                $staticEnvironmentGltfValidationStatus = "ok"
            }
        }

        $staticEnvironmentStatus = "ok"
    }
    else {
        $staticEnvironmentStatus = "missing"
        Write-Warning "Static environment sample not found; skipped: $StaticEnvironmentSampleRoot"
    }
}
else {
    $staticEnvironmentStatus = "skipped"
}

if (![string]::IsNullOrWhiteSpace($CharacterCandidateSampleRoot)) {
    if (Test-Path -LiteralPath $CharacterCandidateSampleRoot) {
        Write-Host ""
        Write-Host "== Validate Naraka character candidate sample =="

        $characterCandidateAssetLibrary = Join-Path $CharacterCandidateSampleRoot "asset_library.json"
        $characterCandidateValidation = Join-Path $CharacterCandidateSampleRoot "model_validation.json"
        $characterCandidateSqliteSummary = Join-Path $CharacterCandidateSampleRoot "sqlite_index_summary.json"
        $characterCandidateDb = Join-Path $CharacterCandidateSampleRoot "library_index.db"
        $characterCandidateGltfs = @(Get-ChildItem -LiteralPath (Join-Path $CharacterCandidateSampleRoot "Models") -Recurse -Filter "*.gltf" -File)

        Test-FileRequired -Path $characterCandidateAssetLibrary -Label "character candidate asset_library.json"
        Test-FileRequired -Path $characterCandidateValidation -Label "character candidate model_validation.json"
        Test-FileRequired -Path $characterCandidateSqliteSummary -Label "character candidate sqlite_index_summary.json"
        Test-FileRequired -Path $characterCandidateDb -Label "character candidate library_index.db"
        if ($characterCandidateGltfs.Count -lt 1) {
            throw "Character candidate sample is missing exported glTF models."
        }
        $characterCandidateGltf = $characterCandidateGltfs[0].FullName

        $characterCandidateValidationJson = Get-Content -LiteralPath $characterCandidateValidation -Raw -Encoding UTF8 | ConvertFrom-Json
        $characterCandidateSummaryJson = Get-Content -LiteralPath $characterCandidateSqliteSummary -Raw -Encoding UTF8 | ConvertFrom-Json
        if ([long]$characterCandidateValidationJson.totals.models -lt 1 -or [long]$characterCandidateValidationJson.totals.ok -lt 1) {
            throw "Character candidate sample has no ok model validation result."
        }
        if ([long]$characterCandidateValidationJson.totals.withSkin -lt 1 -or [long]$characterCandidateValidationJson.totals.withTextures -lt 1) {
            throw "Character candidate sample must keep both skin and texture evidence."
        }
        $characterCandidateModel = @($characterCandidateValidationJson.models)[0]
        $characterCandidateSkinJointCount = [long]$characterCandidateModel.Body.SkinJointCount
        if ($characterCandidateSkinJointCount -lt 50) {
            throw "Character candidate sample has too few skin joints for a strong humanoid candidate: $characterCandidateSkinJointCount"
        }
        $boundsSize = @($characterCandidateModel.Body.Bounds.Size)
        foreach ($sizeValue in $boundsSize) {
            $sizeNumber = [double]$sizeValue
            if ($null -eq $characterCandidateMaxBoundsSize -or $sizeNumber -gt $characterCandidateMaxBoundsSize) {
                $characterCandidateMaxBoundsSize = $sizeNumber
            }
        }
        if ($null -eq $characterCandidateMaxBoundsSize -or $characterCandidateMaxBoundsSize -lt 1.0) {
            throw "Character candidate sample bbox is too small for a strong humanoid candidate: $characterCandidateMaxBoundsSize"
        }
        if ([long]$characterCandidateSummaryJson.counts.textureAssets -lt 1 -or [long]$characterCandidateSummaryJson.counts.materialSidecars -lt 1) {
            throw "Character candidate sample lost texture assets or material sidecars."
        }

        $sqlite3 = Get-Command "sqlite3.exe" -ErrorAction SilentlyContinue
        if ($null -ne $sqlite3) {
            $characterCandidateTextureLinkErrorsText = & $sqlite3.Source -readonly -batch -noheader $characterCandidateDb "SELECT COUNT(*) FROM texture_links WHERE link_error IS NOT NULL AND trim(link_error) <> '';"
            if ($LASTEXITCODE -ne 0) {
                throw "sqlite3 failed while checking character candidate texture link errors."
            }
            $characterCandidateMaterialSidecarRowsText = & $sqlite3.Source -readonly -batch -noheader $characterCandidateDb "SELECT COUNT(*) FROM material_sidecars;"
            if ($LASTEXITCODE -ne 0) {
                throw "sqlite3 failed while counting character candidate material_sidecars."
            }
            $characterCandidateModelRowsText = & $sqlite3.Source -readonly -batch -noheader $characterCandidateDb "SELECT COUNT(*) FROM assets WHERE kind = 'Model';"
            if ($LASTEXITCODE -ne 0) {
                throw "sqlite3 failed while counting character candidate model assets."
            }
            $characterCandidateModelAnimationCandidateRowsText = & $sqlite3.Source -readonly -batch -noheader $characterCandidateDb "SELECT COUNT(*) FROM model_animation_candidates;"
            if ($LASTEXITCODE -ne 0) {
                throw "sqlite3 failed while counting character candidate model_animation_candidates."
            }
            $characterCandidateModelAnimationRelationRowsText = & $sqlite3.Source -readonly -batch -noheader $characterCandidateDb "SELECT COUNT(*) FROM model_animation_relations;"
            if ($LASTEXITCODE -ne 0) {
                throw "sqlite3 failed while counting character candidate model_animation_relations."
            }
            $characterCandidateRelationAnimationRowsText = & $sqlite3.Source -readonly -batch -noheader $characterCandidateDb "SELECT COUNT(*) FROM relation_animations;"
            if ($LASTEXITCODE -ne 0) {
                throw "sqlite3 failed while counting character candidate relation_animations."
            }
            $characterCandidateTextureLinkErrors = [long]$characterCandidateTextureLinkErrorsText
            $characterCandidateMaterialSidecarRows = [long]$characterCandidateMaterialSidecarRowsText
            $characterCandidateModelRows = [long]$characterCandidateModelRowsText
            $characterCandidateModelAnimationCandidateRows = [long]$characterCandidateModelAnimationCandidateRowsText
            $characterCandidateModelAnimationRelationRows = [long]$characterCandidateModelAnimationRelationRowsText
            $characterCandidateRelationAnimationRows = [long]$characterCandidateRelationAnimationRowsText
            if ($characterCandidateTextureLinkErrors -ne 0) {
                throw "Character candidate sample has texture link errors: $characterCandidateTextureLinkErrors"
            }
            if ($characterCandidateMaterialSidecarRows -lt 1 -or $characterCandidateModelRows -lt 1) {
                throw "Character candidate sample lost material sidecar or model rows in library_index.db."
            }
            if ($characterCandidateModelAnimationCandidateRows -ne 0 -or $characterCandidateModelAnimationRelationRows -ne 0 -or $characterCandidateRelationAnimationRows -ne 0) {
                throw "Character candidate sample unexpectedly created production animation relation rows. candidates=$characterCandidateModelAnimationCandidateRows modelRelations=$characterCandidateModelAnimationRelationRows relationAnimations=$characterCandidateRelationAnimationRows"
            }
        }
        else {
            Write-Warning "sqlite3.exe not found; character candidate table checks skipped."
        }

        if (!$SkipGltfValidation) {
            $gltfTransform = Get-Command "gltf-transform.cmd" -ErrorAction SilentlyContinue
            if ($null -ne $gltfTransform) {
                Invoke-Checked -Label "Validate character candidate glTF" -FilePath $gltfTransform.Source -Arguments @("validate", $characterCandidateGltf)
                $characterCandidateGltfValidationStatus = "ok"
            }
        }

        $characterCandidateStatus = "ok"
    }
    else {
        $characterCandidateStatus = "missing"
        Write-Warning "Character candidate sample not found; skipped: $CharacterCandidateSampleRoot"
    }
}
else {
    $characterCandidateStatus = "skipped"
}

$assetLibrary = Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
$modelValidation = Get-Content -LiteralPath $validation -Raw -Encoding UTF8 | ConvertFrom-Json
$sqliteSummaryJson = Get-Content -LiteralPath $sqliteSummary -Raw -Encoding UTF8 | ConvertFrom-Json
$hadiCatalogRow = $null
foreach ($catalogLine in Get-Content -LiteralPath $assetCatalog -Encoding UTF8) {
    if ([string]::IsNullOrWhiteSpace($catalogLine)) {
        continue
    }

    $catalogRow = $catalogLine | ConvertFrom-Json
    if ($catalogRow.name -eq "ch_m_hadi_lv_s9") {
        $hadiCatalogRow = $catalogRow
        break
    }
}
if ($null -eq $hadiCatalogRow) {
    throw "asset_catalog.jsonl lost Hadi representative model row."
}

$hadiMissingRoles = @($hadiCatalogRow.modelCompletenessMissingRoles)
if ($hadiCatalogRow.libraryRole -ne "ModularCharacterBase" -or
    $hadiCatalogRow.resourceKind -ne "CharacterPart" -or
    $hadiCatalogRow.modelCompletenessStatus -ne "modular_incomplete" -or
    ($hadiMissingRoles -notcontains "Face") -or
    ($hadiMissingRoles -notcontains "Hair")) {
    throw "Hadi modular boundary changed unexpectedly. libraryRole=$($hadiCatalogRow.libraryRole) resourceKind=$($hadiCatalogRow.resourceKind) completeness=$($hadiCatalogRow.modelCompletenessStatus) missingRoles=$($hadiMissingRoles -join ',')"
}
$hadiModularBoundary = [pscustomobject]@{
    status = "ok"
    rule = $hadiModularBoundary.rule
    name = [string]$hadiCatalogRow.name
    libraryRole = [string]$hadiCatalogRow.libraryRole
    resourceKind = [string]$hadiCatalogRow.resourceKind
    characterAssemblyRole = [string]$hadiCatalogRow.characterAssemblyRole
    characterAssemblyFamily = [string]$hadiCatalogRow.characterAssemblyFamily
    modelCompletenessStatus = [string]$hadiCatalogRow.modelCompletenessStatus
    missingRoles = $hadiMissingRoles
    modelCompletenessRule = [string]$hadiCatalogRow.modelCompletenessRule
}
$sqlite3ForSourceIndex = Get-Command "sqlite3.exe" -ErrorAction SilentlyContinue
if ($null -ne $sqlite3ForSourceIndex) {
    $avatarAnimatorDomainSql = @"
WITH avatar AS (
  SELECT from_source, from_name, from_path_id
  FROM source_relations
  WHERE relation='animator.avatar'
),
ctrl AS (
  SELECT from_source, from_path_id
  FROM source_relations
  WHERE relation='animator.controller'
),
classified AS (
  SELECT a.from_name,
    CASE
      WHEN lower(a.from_name) LIKE '%ui%' OR lower(a.from_source) LIKE '%/ui/%' THEN 'UiOrPreview'
      WHEN lower(a.from_name) LIKE '%cutscene%' OR lower(a.from_name) LIKE '%preview%' THEN 'PreviewOrTimeline'
      WHEN lower(a.from_name) LIKE 'fx\_%' ESCAPE '\' OR lower(a.from_name) LIKE '%effect%' THEN 'VfxOrEffect'
      WHEN lower(a.from_name) LIKE 'device\_%' ESCAPE '\' THEN 'DeviceOrProp'
      WHEN lower(a.from_name) LIKE 'wp\_%' ESCAPE '\' THEN 'WeaponOrProp'
      WHEN lower(a.from_name) LIKE '%skeleton%' THEN 'SkeletonSource'
      WHEN lower(a.from_name) LIKE 'ch\_%' ESCAPE '\' THEN 'CharacterOrPart'
      ELSE 'Other'
    END AS domain,
    CASE WHEN c.from_path_id IS NULL THEN 0 ELSE 1 END AS has_controller
  FROM avatar a
  LEFT JOIN ctrl c ON c.from_source = a.from_source AND c.from_path_id = a.from_path_id
)
SELECT domain, COUNT(*) AS animators, SUM(has_controller) AS withController,
       substr(group_concat(from_name, ', '), 1, 180) AS samples
FROM classified
GROUP BY domain
ORDER BY animators DESC;
"@
    $avatarAnimatorDomainRowsText = & $sqlite3ForSourceIndex.Source -readonly -batch -json $SourceIndex $avatarAnimatorDomainSql
    if ($LASTEXITCODE -ne 0) {
        throw "sqlite3 failed while querying Naraka source-index animator.avatar domains."
    }
    $avatarAnimatorDomainRowsParsed = ($avatarAnimatorDomainRowsText -join [Environment]::NewLine) | ConvertFrom-Json
    $avatarAnimatorDomainRows = @()
    foreach ($row in $avatarAnimatorDomainRowsParsed) {
        $avatarAnimatorDomainRows += $row
    }
    $avatarAnimatorTotal = 0L
    $avatarAnimatorWithController = 0L
    foreach ($row in $avatarAnimatorDomainRows) {
        $avatarAnimatorTotal += [long]$row.animators
        $avatarAnimatorWithController += [long]$row.withController
    }
    $sourceIndexAvatarAnimatorDomains = [pscustomobject]@{
        status = "ok"
        rule = $sourceIndexAvatarAnimatorDomains.rule
        totalAnimators = $avatarAnimatorTotal
        withController = $avatarAnimatorWithController
        domainCounts = $avatarAnimatorDomainRows
    }

    $characterSourceSqlLiteral = ConvertTo-SqliteTextLiteral $characterCandidateSourceIndexSource
    $characterRelativeSourceSqlLiteral = ConvertTo-SqliteTextLiteral $characterCandidateSourceIndexRelativeSource
    $characterAnimatorSqlLiteral = ConvertTo-SqliteTextLiteral $CharacterCandidateSourceIndexAnimatorName
    $characterCandidateSourceBoundarySql = @"
SELECT
  (SELECT COUNT(*) FROM source_relations WHERE from_source = $characterSourceSqlLiteral AND relation = 'animator.avatar') AS animatorAvatarRows,
  (SELECT COUNT(*) FROM source_relations WHERE from_source = $characterSourceSqlLiteral AND relation = 'animator.avatar' AND from_name = $characterAnimatorSqlLiteral) AS characterAnimatorAvatarRows,
  (SELECT COUNT(DISTINCT to_path_id) FROM source_relations WHERE from_source = $characterSourceSqlLiteral AND relation = 'animator.avatar' AND from_name = $characterAnimatorSqlLiteral) AS distinctCharacterAvatarTargets,
  (SELECT COUNT(*) FROM source_relations WHERE from_source = $characterSourceSqlLiteral AND relation = 'animator.controller') AS animatorControllerRows,
  (SELECT COUNT(*) FROM source_relations WHERE from_source = $characterSourceSqlLiteral AND relation = 'animation.clip') AS animationClipRows,
  (SELECT COUNT(*) FROM source_relations WHERE from_source = $characterSourceSqlLiteral AND relation = 'animatorController.clip') AS animatorControllerClipRows,
  (SELECT COUNT(*) FROM source_animation_bindings WHERE animation_source = $characterSourceSqlLiteral) AS animationBindings,
  (SELECT COUNT(*) FROM source_animation_bindings WHERE animation_source = $characterSourceSqlLiteral AND has_muscle_clip = 1) AS muscleAnimationBindings,
  (SELECT substr(group_concat(animation_name, ', '), 1, 240) FROM (
      SELECT animation_name
      FROM source_animation_bindings
      WHERE animation_source = $characterSourceSqlLiteral
      ORDER BY binding_count DESC
      LIMIT 6
   )) AS topAnimationNames,
  (SELECT COUNT(*) FROM source_objects WHERE source_path = $characterRelativeSourceSqlLiteral AND type = 'SkinnedMeshRenderer') AS skinnedMeshRendererObjects,
  (SELECT COUNT(*) FROM source_objects WHERE source_path = $characterRelativeSourceSqlLiteral AND type = 'MeshRenderer') AS meshRendererObjects;
"@
    $characterCandidateSourceBoundaryText = & $sqlite3ForSourceIndex.Source -readonly -batch -json $SourceIndex $characterCandidateSourceBoundarySql
    if ($LASTEXITCODE -ne 0) {
        throw "sqlite3 failed while querying SamuraiGhost source-index animation boundary."
    }
    $characterCandidateSourceBoundaryRows = ($characterCandidateSourceBoundaryText -join [Environment]::NewLine) | ConvertFrom-Json
    $characterCandidateSourceBoundaryRow = @($characterCandidateSourceBoundaryRows)[0]
    $characterCandidateSourceIndexBoundary = [pscustomobject]@{
        status = "ok"
        rule = $characterCandidateSourceIndexBoundary.rule
        source = $characterCandidateSourceIndexSource
        relativeSource = $characterCandidateSourceIndexRelativeSource
        animatorName = $CharacterCandidateSourceIndexAnimatorName
        rootPathId = $CharacterCandidateSourceIndexRootPathId
        animatorAvatarRows = [long]$characterCandidateSourceBoundaryRow.animatorAvatarRows
        characterAnimatorAvatarRows = [long]$characterCandidateSourceBoundaryRow.characterAnimatorAvatarRows
        distinctCharacterAvatarTargets = [long]$characterCandidateSourceBoundaryRow.distinctCharacterAvatarTargets
        animatorControllerRows = [long]$characterCandidateSourceBoundaryRow.animatorControllerRows
        animationClipRows = [long]$characterCandidateSourceBoundaryRow.animationClipRows
        animatorControllerClipRows = [long]$characterCandidateSourceBoundaryRow.animatorControllerClipRows
        animationBindings = [long]$characterCandidateSourceBoundaryRow.animationBindings
        muscleAnimationBindings = [long]$characterCandidateSourceBoundaryRow.muscleAnimationBindings
        topAnimationNames = [string]$characterCandidateSourceBoundaryRow.topAnimationNames
        skinnedMeshRendererObjects = [long]$characterCandidateSourceBoundaryRow.skinnedMeshRendererObjects
        meshRendererObjects = [long]$characterCandidateSourceBoundaryRow.meshRendererObjects
    }
    if ($characterCandidateSourceIndexBoundary.animatorAvatarRows -lt 1 -or $characterCandidateSourceIndexBoundary.characterAnimatorAvatarRows -lt 1) {
        throw "SamuraiGhost source-index boundary lost Animator.avatar evidence. animatorAvatarRows=$($characterCandidateSourceIndexBoundary.animatorAvatarRows) characterAnimatorAvatarRows=$($characterCandidateSourceIndexBoundary.characterAnimatorAvatarRows)"
    }
    if ($characterCandidateSourceIndexBoundary.animationBindings -lt 1 -or $characterCandidateSourceIndexBoundary.muscleAnimationBindings -lt 1) {
        throw "SamuraiGhost source-index boundary lost same-bundle Humanoid/Muscle animation binding evidence. animationBindings=$($characterCandidateSourceIndexBoundary.animationBindings) muscleAnimationBindings=$($characterCandidateSourceIndexBoundary.muscleAnimationBindings)"
    }
    if ($characterCandidateSourceIndexBoundary.animatorControllerRows -ne 0 -or $characterCandidateSourceIndexBoundary.animationClipRows -ne 0 -or $characterCandidateSourceIndexBoundary.animatorControllerClipRows -ne 0) {
        throw "SamuraiGhost source-index boundary unexpectedly found explicit production animation edges. animatorController=$($characterCandidateSourceIndexBoundary.animatorControllerRows) animationClip=$($characterCandidateSourceIndexBoundary.animationClipRows) animatorControllerClip=$($characterCandidateSourceIndexBoundary.animatorControllerClipRows)"
    }

    $legacyAnimationClipDomainSql = @"
WITH legacy AS (
  SELECT r.from_source, r.from_path_id, r.to_file, r.to_path_id,
         so.name AS clip_name,
         so.id AS target_object_id
  FROM source_relations r
  LEFT JOIN source_objects so
    ON lower(so.serialized_file) = lower(r.to_file)
   AND so.path_id = r.to_path_id
   AND so.type = 'AnimationClip'
  WHERE r.relation = 'animation.clip'
),
classified AS (
  SELECT *,
    CASE
      WHEN lower(COALESCE(clip_name, '')) LIKE 'fx\_%' ESCAPE '\' OR lower(COALESCE(clip_name, '')) LIKE '%effect%' OR lower(COALESCE(clip_name, '')) LIKE '%ghost%' THEN 'VfxOrEffect'
      WHEN lower(COALESCE(clip_name, '')) LIKE '%ui%' THEN 'UiOrPreview'
      WHEN lower(COALESCE(clip_name, '')) LIKE '%showoff%' OR lower(COALESCE(clip_name, '')) LIKE '%platform%' THEN 'ShowoffOrPresentation'
      WHEN lower(COALESCE(clip_name, '')) LIKE '%aimmark%' OR lower(COALESCE(clip_name, '')) LIKE '%marker%' THEN 'AimOrMarker'
      ELSE 'Other'
    END AS domain
  FROM legacy
)
SELECT domain,
       COUNT(*) AS relations,
       COUNT(DISTINCT from_source || ':' || from_path_id) AS animationComponents,
       SUM(CASE WHEN target_object_id IS NULL THEN 0 ELSE 1 END) AS resolvedTargets,
       SUM(CASE WHEN target_object_id IS NULL THEN 1 ELSE 0 END) AS unresolvedTargets,
       substr(group_concat(COALESCE(clip_name, '<unresolved>'), ', '), 1, 220) AS samples
FROM classified
GROUP BY domain
ORDER BY relations DESC, domain;
"@
    $legacyAnimationClipDomainRowsText = & $sqlite3ForSourceIndex.Source -readonly -batch -json $SourceIndex $legacyAnimationClipDomainSql
    if ($LASTEXITCODE -ne 0) {
        throw "sqlite3 failed while querying Naraka source-index legacy Animation.clip domains."
    }
    $legacyAnimationClipDomainRowsParsed = ($legacyAnimationClipDomainRowsText -join [Environment]::NewLine) | ConvertFrom-Json
    $legacyAnimationClipDomainRows = @()
    foreach ($row in $legacyAnimationClipDomainRowsParsed) {
        $legacyAnimationClipDomainRows += $row
    }
    $legacyAnimationClipTotal = 0L
    $legacyAnimationClipResolved = 0L
    $legacyAnimationClipUnresolved = 0L
    foreach ($row in $legacyAnimationClipDomainRows) {
        $legacyAnimationClipTotal += [long]$row.relations
        $legacyAnimationClipResolved += [long]$row.resolvedTargets
        $legacyAnimationClipUnresolved += [long]$row.unresolvedTargets
    }
    $sourceIndexLegacyAnimationClipDomains = [pscustomobject]@{
        status = "ok"
        rule = $sourceIndexLegacyAnimationClipDomains.rule
        totalRelations = $legacyAnimationClipTotal
        resolvedTargets = $legacyAnimationClipResolved
        unresolvedTargets = $legacyAnimationClipUnresolved
        domainCounts = $legacyAnimationClipDomainRows
    }
    if ($legacyAnimationClipTotal -gt 0 -and $legacyAnimationClipUnresolved -ne 0) {
        throw "Naraka source-index legacy Animation.clip targets have unresolved PPtr target(s): $legacyAnimationClipUnresolved / $legacyAnimationClipTotal"
    }
}
else {
    $sourceIndexAvatarAnimatorDomains.status = "toolMissing"
    $characterCandidateSourceIndexBoundary.status = "toolMissing"
    $sourceIndexLegacyAnimationClipDomains.status = "toolMissing"
}
if ($null -eq $sqliteSummaryJson.qualityGates) {
    throw "sqlite_index_summary.json lost qualityGates section."
}
$textureLinkErrors = 0
if ($null -ne $sqliteSummaryJson.qualityGates.textureLinkErrors) {
    $textureLinkErrors = [long]$sqliteSummaryJson.qualityGates.textureLinkErrors
}
if ($textureLinkErrors -ne 0) {
    throw "Texture link quality gate failed: textureLinkErrors=$textureLinkErrors"
}
$thumbnailCache = Join-Path $libraryOutput ".asset_browser_cache"
$thumbnailFileCount = 0
if (Test-Path -LiteralPath $thumbnailCache) {
    $thumbnailFileCount = (Get-ChildItem -LiteralPath $thumbnailCache -Recurse -File | Measure-Object).Count
}

$generatedAt = (Get-Date).ToString("o")
$coverageModels = 0
$coverageAnimations = 0
$coverageExplicitCandidates = 0
$coverageModelsWithExplicitCandidates = 0
$sourceIndexAnimationRelationHealthStatus = "unknown"
$animatorControllerClipRelations = 0
$resolvedAnimatorControllerClipTargets = 0
$missingAnimatorControllerClipTargets = 0
$missingAnimatorControllerClipTargetSamplesJson = "null"
$explicitControllerClipDomainsJson = "null"
$explicitAnimatorControllerUsagesJson = "null"
$monoBehaviourAnimationClipPPtrSummaryJson = "null"
$animatorControllerProductionGate = [pscustomobject]@{
    status = "unknown"
    rule = "Current Naraka smoke expects no Animator that has both Avatar and a Controller with resolved clip edges. If this becomes non-zero, it is a production animation candidate signal and must be re-validated instead of silently enabling animations."
    totalAnimators = 0
    withAvatar = 0
    withControllerClipEdges = 0
    withAvatarAndControllerClipEdges = 0
}

if ($null -ne $sqliteSummaryJson.animationRelationCoverage) {
    $coverageModels = $sqliteSummaryJson.animationRelationCoverage.totals.models
    $coverageAnimations = $sqliteSummaryJson.animationRelationCoverage.totals.animations
    $coverageExplicitCandidates = $sqliteSummaryJson.animationRelationCoverage.totals.explicitCandidates
    $coverageModelsWithExplicitCandidates = $sqliteSummaryJson.animationRelationCoverage.totals.modelsWithExplicitCandidates
    $sourceIndexAnimationRelationHealthStatus = $sqliteSummaryJson.animationRelationCoverage.sourceIndexAnimationRelationHealth.status
    $animatorControllerClipRelations = $sqliteSummaryJson.animationRelationCoverage.sourceIndexAnimationRelationHealth.relationCounts.'animatorController.clip'
    $resolvedAnimatorControllerClipTargets = $sqliteSummaryJson.animationRelationCoverage.sourceIndexAnimationRelationHealth.resolvedTargetCounts.'animatorController.clip'
    $missingAnimatorControllerClipTargets = $sqliteSummaryJson.animationRelationCoverage.sourceIndexAnimationRelationHealth.missingTargetCounts.'animatorController.clip'
    $missingAnimatorControllerClipTargetSamplesJson = ConvertTo-SmokeJsonLiteral $sqliteSummaryJson.animationRelationCoverage.sourceIndexAnimationRelationHealth.missingAnimatorControllerClipTargetSamples 10
    $explicitControllerClipDomainsJson = ConvertTo-SmokeJsonLiteral $sqliteSummaryJson.animationRelationCoverage.sourceIndexAnimationRelationHealth.explicitControllerClipDomains 10
    $explicitAnimatorControllerUsagesJson = ConvertTo-SmokeJsonLiteral $sqliteSummaryJson.animationRelationCoverage.sourceIndexAnimationRelationHealth.explicitAnimatorControllerUsages 10
    $monoBehaviourAnimationClipPPtrSummaryJson = ConvertTo-SmokeJsonLiteral $sqliteSummaryJson.animationRelationCoverage.sourceIndexAnimationRelationHealth.monoBehaviourAnimationClipPPtrSummary 10
    if ($null -ne $sqliteSummaryJson.animationRelationCoverage.sourceIndexAnimationRelationHealth.explicitAnimatorControllerUsages) {
        $explicitAnimatorControllerUsages = $sqliteSummaryJson.animationRelationCoverage.sourceIndexAnimationRelationHealth.explicitAnimatorControllerUsages
        $animatorControllerProductionGate = [pscustomobject]@{
            status = "ok"
            rule = $animatorControllerProductionGate.rule
            totalAnimators = [long]$explicitAnimatorControllerUsages.totalAnimators
            withAvatar = [long]$explicitAnimatorControllerUsages.withAvatar
            withControllerClipEdges = [long]$explicitAnimatorControllerUsages.withControllerClipEdges
            withAvatarAndControllerClipEdges = [long]$explicitAnimatorControllerUsages.withAvatarAndControllerClipEdges
        }
        if ($animatorControllerProductionGate.withAvatarAndControllerClipEdges -ne 0) {
            throw "Naraka source-index found Animator+Avatar+ControllerClip production candidate signal. withAvatarAndControllerClipEdges=$($animatorControllerProductionGate.withAvatarAndControllerClipEdges). Re-validate before enabling default animation capability."
        }
    }
}

if ($null -ne $animationReportJson) {
    $animationDiagnosticLines = @()
    $animationDiagnosticLines += "{"
    $animationDiagnosticLines += '  "status": ' + (ConvertTo-SmokeJsonLiteral $animationReportJson.status) + ","
    $animationDiagnosticLines += '  "message": ' + (ConvertTo-SmokeJsonLiteral $animationReportJson.message) + ","
    $animationDiagnosticLines += '  "gltf": ' + (ConvertTo-SmokeJsonLiteral $animationGltfPath) + ","
    $animationDiagnosticLines += '  "avatarInjectionMode": ' + (ConvertTo-SmokeJsonLiteral $animationReportJson.avatarInjection.mode) + ","
    $animationDiagnosticLines += '  "diagnosticOnly": ' + (ConvertTo-SmokeJsonLiteral $animationReportJson.avatarInjection.diagnosticOnly) + ","
    $animationDiagnosticLines += '  "notDefaultModelAnimationRelation": ' + (ConvertTo-SmokeJsonLiteral $animationReportJson.avatarInjection.notDefaultModelAnimationRelation) + ","
    $animationDiagnosticLines += '  "manualReviewRequired": ' + (ConvertTo-SmokeJsonLiteral $animationReportJson.avatarInjection.manualReviewRequired) + ","
    $animationDiagnosticLines += '  "tosCount": ' + (ConvertTo-SmokeJsonLiteral $animationReportJson.avatarInjection.tosCount)
    $animationDiagnosticLines += "}"
    $animationDiagnosticJson = $animationDiagnosticLines -join [Environment]::NewLine
} else {
    $animationDiagnosticLines = @()
    $animationDiagnosticLines += "{"
    $animationDiagnosticLines += '  "status": ' + (ConvertTo-SmokeJsonLiteral $animationDiagnosticStatus) + ","
    $animationDiagnosticLines += '  "diagnosticOnly": true,'
    $animationDiagnosticLines += '  "notDefaultModelAnimationRelation": true'
    $animationDiagnosticLines += "}"
    $animationDiagnosticJson = $animationDiagnosticLines -join [Environment]::NewLine
}

# 这两个报告只汇总已经验证过的产物，不改变正式素材库内容或动画关系结论。
$summaryJsonLines = @()
$summaryJsonLines += "{"
$summaryJsonLines += '  "generatedAt": ' + (ConvertTo-SmokeJsonLiteral $generatedAt) + ","
$summaryJsonLines += '  "game": "Naraka",'
$summaryJsonLines += '  "sourceRoot": ' + (ConvertTo-SmokeJsonLiteral $SourceRoot) + ","
$summaryJsonLines += '  "sourceIndex": ' + (ConvertTo-SmokeJsonLiteral $SourceIndex) + ","
$summaryJsonLines += '  "outputRoot": ' + (ConvertTo-SmokeJsonLiteral $OutputRoot) + ","
$summaryJsonLines += '  "libraryRoot": ' + (ConvertTo-SmokeJsonLiteral $libraryOutput) + ","
$summaryJsonLines += '  "assetLibrary": ' + (ConvertTo-SmokeJsonLiteral $manifest) + ","
$summaryJsonLines += '  "libraryIndex": ' + (ConvertTo-SmokeJsonLiteral $db) + ","
$summaryJsonLines += '  "sqliteSummary": ' + (ConvertTo-SmokeJsonLiteral $sqliteSummary) + ","
$summaryJsonLines += '  "modelValidation": ' + (ConvertTo-SmokeJsonLiteral $validation) + ","
$summaryJsonLines += '  "modelGltf": ' + (ConvertTo-SmokeJsonLiteral $gltf) + ","
$summaryJsonLines += '  "representativeModels": ['
$summaryJsonLines += '    { "name": "ch_m_hadi_lv_s9", "role": "CharacterPart", "gltf": ' + (ConvertTo-SmokeJsonLiteral $gltf) + ' },'
$summaryJsonLines += '    { "name": "weapon_drop_bow_dongjun", "role": "WeaponProp", "gltf": ' + (ConvertTo-SmokeJsonLiteral $bowGltf) + ' },'
$summaryJsonLines += '    { "name": "device_hongbao_02", "role": "Prop", "gltf": ' + (ConvertTo-SmokeJsonLiteral $deviceGltf) + ' }'
$summaryJsonLines += '  ],'
$summaryJsonLines += '  "hadiModularBoundary": ' + (ConvertTo-SmokeJsonLiteral $hadiModularBoundary 10) + ","
$summaryJsonLines += '  "shaderBoundary": {'
$summaryJsonLines += '    "status": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundaryStatus) + ","
$summaryJsonLines += '    "sampleRoot": ' + (ConvertTo-SmokeJsonLiteral $ShaderBoundarySampleRoot) + ","
$summaryJsonLines += '    "gltf": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundaryGltf) + ","
$summaryJsonLines += '    "gltfValidation": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundaryGltfValidationStatus) + ","
$summaryJsonLines += '    "textureLinkErrors": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundarySummaryJson.qualityGates.textureLinkErrors) + ","
$summaryJsonLines += '    "customShaderRequiredSidecars": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundarySummaryJson.qualityGates.customShaderRequiredSidecars) + ","
$summaryJsonLines += '    "layeredMaterialUnresolvedSidecars": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundarySummaryJson.qualityGates.layeredMaterialUnresolvedSidecars) + ","
$summaryJsonLines += '    "degradedPreviewSidecars": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundarySummaryJson.qualityGates.degradedPreviewSidecars) + ","
$summaryJsonLines += '    "materialSidecarRows": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundaryMaterialSidecarRows) + ","
$summaryJsonLines += '    "customShaderMaterialSidecarRows": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundaryCustomShaderRows) + ","
$summaryJsonLines += '    "rule": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundaryRule)
$summaryJsonLines += '  },'
$summaryJsonLines += '  "staticEnvironment": {'
$summaryJsonLines += '    "status": ' + (ConvertTo-SmokeJsonLiteral $staticEnvironmentStatus) + ","
$summaryJsonLines += '    "sampleRoot": ' + (ConvertTo-SmokeJsonLiteral $StaticEnvironmentSampleRoot) + ","
$summaryJsonLines += '    "gltf": ' + (ConvertTo-SmokeJsonLiteral $staticEnvironmentGltf) + ","
$summaryJsonLines += '    "gltfValidation": ' + (ConvertTo-SmokeJsonLiteral $staticEnvironmentGltfValidationStatus) + ","
$summaryJsonLines += '    "models": ' + (ConvertTo-SmokeJsonLiteral $staticEnvironmentValidationJson.totals.models) + ","
$summaryJsonLines += '    "ok": ' + (ConvertTo-SmokeJsonLiteral $staticEnvironmentValidationJson.totals.ok) + ","
$summaryJsonLines += '    "withTextures": ' + (ConvertTo-SmokeJsonLiteral $staticEnvironmentValidationJson.totals.withTextures) + ","
$summaryJsonLines += '    "textureAssets": ' + (ConvertTo-SmokeJsonLiteral $staticEnvironmentSummaryJson.counts.textureAssets) + ","
$summaryJsonLines += '    "textureLinks": ' + (ConvertTo-SmokeJsonLiteral $staticEnvironmentSummaryJson.counts.textureLinks) + ","
$summaryJsonLines += '    "textureLinkErrors": ' + (ConvertTo-SmokeJsonLiteral $staticEnvironmentTextureLinkErrors) + ","
$summaryJsonLines += '    "materialSidecars": ' + (ConvertTo-SmokeJsonLiteral $staticEnvironmentSummaryJson.counts.materialSidecars) + ","
$summaryJsonLines += '    "materialSidecarRows": ' + (ConvertTo-SmokeJsonLiteral $staticEnvironmentMaterialSidecarRows) + ","
$summaryJsonLines += '    "modelRows": ' + (ConvertTo-SmokeJsonLiteral $staticEnvironmentModelRows) + ","
$summaryJsonLines += '    "rule": ' + (ConvertTo-SmokeJsonLiteral $staticEnvironmentRule)
$summaryJsonLines += '  },'
$summaryJsonLines += '  "characterCandidate": {'
$summaryJsonLines += '    "status": ' + (ConvertTo-SmokeJsonLiteral $characterCandidateStatus) + ","
$summaryJsonLines += '    "sampleRoot": ' + (ConvertTo-SmokeJsonLiteral $CharacterCandidateSampleRoot) + ","
$summaryJsonLines += '    "gltf": ' + (ConvertTo-SmokeJsonLiteral $characterCandidateGltf) + ","
$summaryJsonLines += '    "gltfValidation": ' + (ConvertTo-SmokeJsonLiteral $characterCandidateGltfValidationStatus) + ","
$summaryJsonLines += '    "models": ' + (ConvertTo-SmokeJsonLiteral $characterCandidateValidationJson.totals.models) + ","
$summaryJsonLines += '    "ok": ' + (ConvertTo-SmokeJsonLiteral $characterCandidateValidationJson.totals.ok) + ","
$summaryJsonLines += '    "withSkin": ' + (ConvertTo-SmokeJsonLiteral $characterCandidateValidationJson.totals.withSkin) + ","
$summaryJsonLines += '    "withTextures": ' + (ConvertTo-SmokeJsonLiteral $characterCandidateValidationJson.totals.withTextures) + ","
$summaryJsonLines += '    "skinJointCount": ' + (ConvertTo-SmokeJsonLiteral $characterCandidateSkinJointCount) + ","
$summaryJsonLines += '    "maxBoundsSize": ' + (ConvertTo-SmokeJsonLiteral $characterCandidateMaxBoundsSize) + ","
$summaryJsonLines += '    "textureAssets": ' + (ConvertTo-SmokeJsonLiteral $characterCandidateSummaryJson.counts.textureAssets) + ","
$summaryJsonLines += '    "textureLinks": ' + (ConvertTo-SmokeJsonLiteral $characterCandidateSummaryJson.counts.textureLinks) + ","
$summaryJsonLines += '    "textureLinkErrors": ' + (ConvertTo-SmokeJsonLiteral $characterCandidateTextureLinkErrors) + ","
$summaryJsonLines += '    "materialSidecars": ' + (ConvertTo-SmokeJsonLiteral $characterCandidateSummaryJson.counts.materialSidecars) + ","
$summaryJsonLines += '    "materialSidecarRows": ' + (ConvertTo-SmokeJsonLiteral $characterCandidateMaterialSidecarRows) + ","
$summaryJsonLines += '    "modelRows": ' + (ConvertTo-SmokeJsonLiteral $characterCandidateModelRows) + ","
$summaryJsonLines += '    "modelAnimationCandidateRows": ' + (ConvertTo-SmokeJsonLiteral $characterCandidateModelAnimationCandidateRows) + ","
$summaryJsonLines += '    "modelAnimationRelationRows": ' + (ConvertTo-SmokeJsonLiteral $characterCandidateModelAnimationRelationRows) + ","
$summaryJsonLines += '    "relationAnimationRows": ' + (ConvertTo-SmokeJsonLiteral $characterCandidateRelationAnimationRows) + ","
$summaryJsonLines += '    "rule": ' + (ConvertTo-SmokeJsonLiteral $characterCandidateRule)
$summaryJsonLines += '  },'
$summaryJsonLines += '  "characterCandidateSourceIndexBoundary": ' + (ConvertTo-SmokeJsonLiteral $characterCandidateSourceIndexBoundary 10) + ","
$summaryJsonLines += '  "checks": {'
$summaryJsonLines += '    "representativeGltfValidation": ' + (ConvertTo-SmokeJsonLiteral $representativeGltfValidationStatus) + ","
$summaryJsonLines += '    "shaderBoundary": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundaryStatus) + ","
$summaryJsonLines += '    "shaderBoundaryGltfValidation": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundaryGltfValidationStatus) + ","
$summaryJsonLines += '    "staticEnvironment": ' + (ConvertTo-SmokeJsonLiteral $staticEnvironmentStatus) + ","
$summaryJsonLines += '    "staticEnvironmentGltfValidation": ' + (ConvertTo-SmokeJsonLiteral $staticEnvironmentGltfValidationStatus) + ","
$summaryJsonLines += '    "characterCandidate": ' + (ConvertTo-SmokeJsonLiteral $characterCandidateStatus) + ","
$summaryJsonLines += '    "hadiModularBoundary": ' + (ConvertTo-SmokeJsonLiteral $hadiModularBoundary.status) + ","
$summaryJsonLines += '    "characterCandidateGltfValidation": ' + (ConvertTo-SmokeJsonLiteral $characterCandidateGltfValidationStatus) + ","
$summaryJsonLines += '    "characterCandidateSourceIndexBoundary": ' + (ConvertTo-SmokeJsonLiteral $characterCandidateSourceIndexBoundary.status) + ","
$summaryJsonLines += '    "sourceIndexAvatarAnimatorDomains": ' + (ConvertTo-SmokeJsonLiteral $sourceIndexAvatarAnimatorDomains.status) + ","
$summaryJsonLines += '    "sourceIndexLegacyAnimationClipDomains": ' + (ConvertTo-SmokeJsonLiteral $sourceIndexLegacyAnimationClipDomains.status) + ","
$summaryJsonLines += '    "scriptAnimationComponentDiagnostics": ' + (ConvertTo-SmokeJsonLiteral $scriptAnimationComponentDiagnostics.status) + ","
$summaryJsonLines += '    "animatorControllerProductionGate": ' + (ConvertTo-SmokeJsonLiteral $animatorControllerProductionGate.status) + ","
$summaryJsonLines += '    "browserValidation": ' + (ConvertTo-SmokeJsonLiteral $browserValidationStatus) + ","
$summaryJsonLines += '    "thumbnailRender": ' + (ConvertTo-SmokeJsonLiteral $thumbnailStatus) + ","
$summaryJsonLines += '    "thumbnailFileCount": ' + (ConvertTo-SmokeJsonLiteral $thumbnailFileCount) + ","
$summaryJsonLines += '    "animationDiagnostic": ' + (ConvertTo-SmokeJsonLiteral $animationDiagnosticStatus) + ","
$summaryJsonLines += '    "animationGltfValidation": ' + (ConvertTo-SmokeJsonLiteral $animationGltfValidationStatus)
$summaryJsonLines += '  },'
$summaryJsonLines += '  "capabilities": {'
$summaryJsonLines += '    "models": ' + (ConvertTo-SmokeJsonLiteral $assetLibrary.capabilities.models) + ","
$summaryJsonLines += '    "animations": ' + (ConvertTo-SmokeJsonLiteral $assetLibrary.capabilities.animations) + ","
$summaryJsonLines += '    "animationPreviewComposer": ' + (ConvertTo-SmokeJsonLiteral $assetLibrary.capabilities.animationPreviewComposer)
$summaryJsonLines += '  },'
$summaryJsonLines += '  "modelTotals": ' + (ConvertTo-SmokeJsonLiteral $modelValidation.totals 10) + ","
$summaryJsonLines += '  "sqliteCounts": ' + (ConvertTo-SmokeJsonLiteral $sqliteSummaryJson.counts 10) + ","
$summaryJsonLines += '  "qualityGates": ' + (ConvertTo-SmokeJsonLiteral $sqliteSummaryJson.qualityGates 10) + ","
$summaryJsonLines += '  "animationRelationCoverage": {'
$summaryJsonLines += '    "models": ' + (ConvertTo-SmokeJsonLiteral $coverageModels) + ","
$summaryJsonLines += '    "animations": ' + (ConvertTo-SmokeJsonLiteral $coverageAnimations) + ","
$summaryJsonLines += '    "explicitCandidates": ' + (ConvertTo-SmokeJsonLiteral $coverageExplicitCandidates) + ","
$summaryJsonLines += '    "modelsWithExplicitCandidates": ' + (ConvertTo-SmokeJsonLiteral $coverageModelsWithExplicitCandidates) + ","
$summaryJsonLines += '    "modelAnimationRelations": ' + (ConvertTo-SmokeJsonLiteral $sqliteSummaryJson.counts.modelAnimationRelations) + ","
$summaryJsonLines += '    "sourceIndexAnimationRelationHealth": ' + (ConvertTo-SmokeJsonLiteral $sourceIndexAnimationRelationHealthStatus) + ","
$summaryJsonLines += '    "animatorControllerClipRelations": ' + (ConvertTo-SmokeJsonLiteral $animatorControllerClipRelations) + ","
$summaryJsonLines += '    "resolvedAnimatorControllerClipTargets": ' + (ConvertTo-SmokeJsonLiteral $resolvedAnimatorControllerClipTargets) + ","
$summaryJsonLines += '    "missingAnimatorControllerClipTargets": ' + (ConvertTo-SmokeJsonLiteral $missingAnimatorControllerClipTargets) + ","
$summaryJsonLines += '    "missingAnimatorControllerClipTargetSamples": ' + $missingAnimatorControllerClipTargetSamplesJson + ","
$summaryJsonLines += '    "explicitControllerClipDomains": ' + $explicitControllerClipDomainsJson + ","
$summaryJsonLines += '    "explicitAnimatorControllerUsages": ' + $explicitAnimatorControllerUsagesJson + ","
$summaryJsonLines += '    "monoBehaviourAnimationClipPPtrSummary": ' + $monoBehaviourAnimationClipPPtrSummaryJson + ","
$summaryJsonLines += '    "animatorControllerProductionGate": ' + (ConvertTo-SmokeJsonLiteral $animatorControllerProductionGate 10) + ","
$summaryJsonLines += '    "avatarAnimatorDomains": ' + (ConvertTo-SmokeJsonLiteral $sourceIndexAvatarAnimatorDomains 10) + ","
$summaryJsonLines += '    "legacyAnimationClipDomains": ' + (ConvertTo-SmokeJsonLiteral $sourceIndexLegacyAnimationClipDomains 10) + ","
$summaryJsonLines += '    "scriptAnimationComponentDiagnostics": ' + (ConvertTo-SmokeJsonLiteral $scriptAnimationComponentDiagnostics 10)
$summaryJsonLines += '  },'
$summaryJsonLines += '  "animationDiagnostic": ' + $animationDiagnosticJson
$summaryJsonLines += "}"
$summaryJson = $summaryJsonLines -join [Environment]::NewLine
$summaryJsonPath = [System.IO.Path]::Combine($OutputRoot, "smoke_summary.json")
if ([string]::IsNullOrWhiteSpace($summaryJsonPath)) {
    throw "smoke_summary.json output path is empty. OutputRoot='$OutputRoot'"
}
$summaryJson | Set-Content -LiteralPath $summaryJsonPath -Encoding UTF8
# smoke_summary.json 是后续自动验收入口，写完后立刻反读，避免报告可读但机器摘要损坏。
$null = Get-Content -LiteralPath $summaryJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json

$reportPath = [System.IO.Path]::Combine($OutputRoot, "SMOKE_REPORT.md")
$reportLines = [System.Activator]::CreateInstance([System.Collections.Generic.List[string]])
$reportLines.Add("# Naraka first usable smoke report")
$reportLines.Add("")
$reportLines.Add(("Generated: {0}" -f $generatedAt))
$reportLines.Add("")
$reportLines.Add("## Summary")
$reportLines.Add("")
$reportLines.Add(('- Source root: `{0}`' -f $SourceRoot))
$reportLines.Add(('- Source index: `{0}`' -f $SourceIndex))
$reportLines.Add(('- Output root: `{0}`' -f $OutputRoot))
$reportLines.Add(('- Library root: `{0}`' -f $libraryOutput))
$reportLines.Add("")
$reportLines.Add("## Static Library")
$reportLines.Add("")
$reportLines.Add(('- Capabilities: models=`{0}`, animations=`{1}`, animationPreviewComposer=`{2}`' -f $assetLibrary.capabilities.models, $assetLibrary.capabilities.animations, (ConvertTo-SmokeText $assetLibrary.capabilities.animationPreviewComposer "null")))
$reportLines.Add(('- Representative glTF validator: `{0}`' -f $representativeGltfValidationStatus))
$reportLines.Add(('- AssetLibrary browser validation: `{0}`' -f $browserValidationStatus))
$reportLines.Add(('- Thumbnail render: `{0}`, cache files=`{1}`' -f $thumbnailStatus, $thumbnailFileCount))
if ($null -ne $modelValidation.totals) {
    $reportLines.Add(('- Model totals: models=`{0}`, ok=`{1}`, warning=`{2}`, error=`{3}`' -f `
        (ConvertTo-SmokeText $modelValidation.totals.models "0"),
        (ConvertTo-SmokeText $modelValidation.totals.ok "0"),
        (ConvertTo-SmokeText $modelValidation.totals.warning "0"),
        (ConvertTo-SmokeText $modelValidation.totals.error "0")))
}
$reportLines.Add(('- Representative samples: `ch_m_hadi_lv_s9` (CharacterPart), `weapon_drop_bow_dongjun` (WeaponProp), `device_hongbao_02` (Prop)'))
if ($hadiModularBoundary.status -eq "ok") {
    $reportLines.Add(('- Hadi modular boundary: libraryRole=`{0}`, resourceKind=`{1}`, completeness=`{2}`, missingRoles=`{3}`' -f `
        (ConvertTo-SmokeText $hadiModularBoundary.libraryRole),
        (ConvertTo-SmokeText $hadiModularBoundary.resourceKind),
        (ConvertTo-SmokeText $hadiModularBoundary.modelCompletenessStatus),
        (ConvertTo-SmokeText ($hadiModularBoundary.missingRoles -join ","))))
    $reportLines.Add('- Hadi modular rule: this body/clothing sample is usable as a model asset, but it is not a complete character and must not be used as a production animation smoke model until deterministic Face/Hair assembly is available.')
}
else {
    $reportLines.Add(('- Hadi modular boundary: `{0}`' -f $hadiModularBoundary.status))
}
$reportLines.Add("")
$reportLines.Add("## SQLite Index")
$reportLines.Add("")
if ($null -ne $sqliteSummaryJson.counts) {
    $reportLines.Add(('- Assets=`{0}`, assetCatalog=`{1}`, textureAssets=`{2}`, textureLinks=`{3}`, materialSidecars=`{4}`, modelValidation=`{5}`' -f `
        (ConvertTo-SmokeText $sqliteSummaryJson.counts.assets "0"),
        (ConvertTo-SmokeText $sqliteSummaryJson.counts.assetCatalog "0"),
        (ConvertTo-SmokeText $sqliteSummaryJson.counts.textureAssets "0"),
        (ConvertTo-SmokeText $sqliteSummaryJson.counts.textureLinks "0"),
        (ConvertTo-SmokeText $sqliteSummaryJson.counts.materialSidecars "0"),
        (ConvertTo-SmokeText $sqliteSummaryJson.counts.modelValidation "0")))
    $reportLines.Add(('- Model animation candidates=`{0}`, model animation relations=`{1}`' -f `
        (ConvertTo-SmokeText $sqliteSummaryJson.counts.modelAnimationCandidates "0"),
        (ConvertTo-SmokeText $sqliteSummaryJson.counts.modelAnimationRelations "0")))
}
if ($null -ne $sqliteSummaryJson.qualityGates) {
    $reportLines.Add(('- Quality gates: textureLinkErrors=`{0}`, customShaderSidecars=`{1}`, layeredMaterialUnresolvedSidecars=`{2}`, degradedPreviewSidecars=`{3}`, modelsNeedingCustomShaderLayer=`{4}`, modelsNeedingCustomizationTint=`{5}`' -f `
        (ConvertTo-SmokeText $sqliteSummaryJson.qualityGates.textureLinkErrors "0"),
        (ConvertTo-SmokeText $sqliteSummaryJson.qualityGates.customShaderRequiredSidecars "0"),
        (ConvertTo-SmokeText $sqliteSummaryJson.qualityGates.layeredMaterialUnresolvedSidecars "0"),
        (ConvertTo-SmokeText $sqliteSummaryJson.qualityGates.degradedPreviewSidecars "0"),
        (ConvertTo-SmokeText $sqliteSummaryJson.qualityGates.modelsNeedingCustomShaderLayer "0"),
        (ConvertTo-SmokeText $sqliteSummaryJson.qualityGates.modelsNeedingCustomizationTint "0")))
}
if ($null -ne $sqliteSummaryJson.animationRelationCoverage) {
    $relationHealth = $sqliteSummaryJson.animationRelationCoverage.sourceIndexAnimationRelationHealth
    $coverageTotals = $sqliteSummaryJson.animationRelationCoverage.totals
    $reportLines.Add(('- Explicit animation coverage: models=`{0}`, animations=`{1}`, explicitCandidates=`{2}`, modelsWithExplicitCandidates=`{3}`' -f `
        (ConvertTo-SmokeText $coverageTotals.models "0"),
        (ConvertTo-SmokeText $coverageTotals.animations "0"),
        (ConvertTo-SmokeText $coverageTotals.explicitCandidates "0"),
        (ConvertTo-SmokeText $coverageTotals.modelsWithExplicitCandidates "0")))
    $reportLines.Add(('- Source-index animation relation health: `{0}`, animatorController.clip=`{1}`, resolved=`{2}`, missing=`{3}`' -f `
        (ConvertTo-SmokeText $relationHealth.status),
        (ConvertTo-SmokeText $relationHealth.relationCounts.'animatorController.clip' "0"),
        (ConvertTo-SmokeText $relationHealth.resolvedTargetCounts.'animatorController.clip' "0"),
        (ConvertTo-SmokeText $relationHealth.missingTargetCounts.'animatorController.clip' "0")))
    if ($null -ne $relationHealth.missingAnimatorControllerClipTargetSamples -and $relationHealth.missingAnimatorControllerClipTargetSamples.Count -gt 0) {
        $firstMissingClip = $relationHealth.missingAnimatorControllerClipTargetSamples[0]
        $reportLines.Add(('- Missing animatorController.clip target samples=`{0}`, firstTarget=`{1}:{2}`, sampleController=`{3}`' -f `
            $relationHealth.missingAnimatorControllerClipTargetSamples.Count,
            (ConvertTo-SmokeText $firstMissingClip.targetFile),
            (ConvertTo-SmokeText $firstMissingClip.targetPathId "0"),
            (ConvertTo-SmokeText $firstMissingClip.sampleReferrer.name)))
    }
    if ($null -ne $relationHealth.explicitControllerClipDomains -and $null -ne $relationHealth.explicitControllerClipDomains.domainCounts) {
        $domainParts = @()
        foreach ($domain in $relationHealth.explicitControllerClipDomains.domainCounts) {
            $domainParts += ('{0}={1}/{2}' -f `
                (ConvertTo-SmokeText $domain.domain),
                (ConvertTo-SmokeText $domain.controllers "0"),
                (ConvertTo-SmokeText $domain.clipEdges "0"))
        }
        if ($domainParts.Count -gt 0) {
            $reportLines.Add(('- Explicit controller clip domains: `{0}` (controllers/clipEdges)' -f ($domainParts -join ', ')))
        }
    }
    if ($null -ne $relationHealth.explicitAnimatorControllerUsages -and $null -ne $relationHealth.explicitAnimatorControllerUsages.domainCounts) {
        $usageParts = @()
        foreach ($domain in $relationHealth.explicitAnimatorControllerUsages.domainCounts) {
            $usageParts += ('{0}={1}/{2}/{3}' -f `
                (ConvertTo-SmokeText $domain.domain),
                (ConvertTo-SmokeText $domain.animators "0"),
                (ConvertTo-SmokeText $domain.withAvatar "0"),
                (ConvertTo-SmokeText $domain.controllerClipEdges "0"))
        }
        if ($usageParts.Count -gt 0) {
            $reportLines.Add(('- Explicit animator controller usages: `{0}` (animators/withAvatar/controllerClipEdges)' -f ($usageParts -join ', ')))
        }
        $reportLines.Add(('- Animator controller production gate: withAvatar=`{0}`, withAvatarAndControllerClipEdges=`{1}`' -f `
            (ConvertTo-SmokeText $relationHealth.explicitAnimatorControllerUsages.withAvatar "0"),
            (ConvertTo-SmokeText $relationHealth.explicitAnimatorControllerUsages.withAvatarAndControllerClipEdges "0")))
        $reportLines.Add(('- Animator controller production gate status: `{0}`, totalAnimators=`{1}`, withControllerClipEdges=`{2}`' -f `
            (ConvertTo-SmokeText $animatorControllerProductionGate.status),
            (ConvertTo-SmokeText $animatorControllerProductionGate.totalAnimators "0"),
            (ConvertTo-SmokeText $animatorControllerProductionGate.withControllerClipEdges "0")))
        $reportLines.Add('- Animator controller production gate rule: a non-zero Animator+Avatar+ControllerClip count is a new production-candidate signal and must trigger explicit model/clip validation before default animation capability changes.')
    }
    if ($null -ne $relationHealth.monoBehaviourAnimationClipPPtrSummary) {
        $monoClipSummary = $relationHealth.monoBehaviourAnimationClipPPtrSummary
        $reportLines.Add(('- MonoBehaviour AnimationClip PPtr diagnostic: totalRelations=`{0}`, objects=`{1}`, distinctClips=`{2}`' -f `
            (ConvertTo-SmokeText $monoClipSummary.totalRelations "0"),
            (ConvertTo-SmokeText $monoClipSummary.objectCount "0"),
            (ConvertTo-SmokeText $monoClipSummary.distinctClipCount "0")))
        foreach ($scriptRow in @($monoClipSummary.topScripts | Select-Object -First 8)) {
            $reportLines.Add(('-   {0}: relations=`{1}`, objects=`{2}`, distinctClips=`{3}`, fieldPaths=`{4}`, gameObjects=`{5}`, sampleGameObject=`{6}`, sampleClip=`{7}`' -f `
                (ConvertTo-SmokeText $scriptRow.scriptName),
                (ConvertTo-SmokeText $scriptRow.relationCount "0"),
                (ConvertTo-SmokeText $scriptRow.objectCount "0"),
                (ConvertTo-SmokeText $scriptRow.distinctClipCount "0"),
                (ConvertTo-SmokeText $scriptRow.fieldPaths ""),
                (ConvertTo-SmokeText $scriptRow.attachedGameObjectCount "0"),
                (ConvertTo-SmokeText $scriptRow.sample.gameObjectName ""),
                (ConvertTo-SmokeText $scriptRow.sample.clipName "")))
        }
        $reportLines.Add('- MonoBehaviour AnimationClip PPtr rule: these are explicit script-field references for investigation, but custom script semantics are required before any default model-animation binding can be created.')
    }
    if ($scriptAnimationComponentDiagnostics.status -eq "ok") {
        $hadiScript = $scriptAnimationComponentDiagnostics.hadiBody
        $fxScript = $scriptAnimationComponentDiagnostics.fxAttack
        $reportLines.Add(('- Selected-model script AnimationClip diagnostic: Hadi selected=`{0}`, candidates=`{1}`, scriptRows=`{2}`; Fx selected=`{3}`, candidates=`{4}`, scriptRows=`{5}`, invalidBoundaryRows=`{6}`' -f `
            (ConvertTo-SmokeText $hadiScript.selectedModelCount "0"),
            (ConvertTo-SmokeText $hadiScript.candidateCount "0"),
            (ConvertTo-SmokeText $hadiScript.scriptAnimationRows "0"),
            (ConvertTo-SmokeText $fxScript.selectedModelCount "0"),
            (ConvertTo-SmokeText $fxScript.candidateCount "0"),
            (ConvertTo-SmokeText $fxScript.scriptAnimationRows "0"),
            (ConvertTo-SmokeText $fxScript.invalidBoundaryRows "0")))
        $reportLines.Add(('-   Fx first script=`{0}`, field=`{1}`, clip=`{2}`, visibleRendererRows=`{3}`, animatorRows=`{4}`' -f `
            (ConvertTo-SmokeText $fxScript.firstScriptName ""),
            (ConvertTo-SmokeText $fxScript.firstFieldPath ""),
            (ConvertTo-SmokeText $fxScript.firstClipName ""),
            (ConvertTo-SmokeText $fxScript.visibleRendererRows "0"),
            (ConvertTo-SmokeText $fxScript.animatorRows "0")))
        $reportLines.Add('- Selected-model script AnimationClip rule: Hadi proves normal visible model selection is not promoted; FxAttack proves local SimpleAnimation-style clip PPtrs are retained as diagnostic evidence only, not default model-animation candidates.')
    }
    else {
        $reportLines.Add(('- Selected-model script AnimationClip diagnostic: `{0}`' -f $scriptAnimationComponentDiagnostics.status))
    }
    if ($sourceIndexAvatarAnimatorDomains.status -eq "ok") {
        $reportLines.Add(('- Source-index animator.avatar domains: totalAnimators=`{0}`, withController=`{1}`' -f `
            (ConvertTo-SmokeText $sourceIndexAvatarAnimatorDomains.totalAnimators "0"),
            (ConvertTo-SmokeText $sourceIndexAvatarAnimatorDomains.withController "0")))
        foreach ($domainRow in @($sourceIndexAvatarAnimatorDomains.domainCounts | Select-Object -First 8)) {
            $reportLines.Add(('-   {0}: animators=`{1}`, withController=`{2}`, samples=`{3}`' -f `
                (ConvertTo-SmokeText $domainRow.domain),
                (ConvertTo-SmokeText $domainRow.animators "0"),
                (ConvertTo-SmokeText $domainRow.withController "0"),
                (ConvertTo-SmokeText $domainRow.samples "")))
        }
        $reportLines.Add('- Avatar-domain rule: animator.avatar alone is diagnostic skeleton context; it is not a default model-animation binding without explicit clip/controller relation and model validation.')
    }
    else {
        $reportLines.Add(('- Source-index animator.avatar domain query: `{0}`' -f $sourceIndexAvatarAnimatorDomains.status))
    }
    if ($sourceIndexLegacyAnimationClipDomains.status -eq "ok") {
        $reportLines.Add(('- Source-index legacy Animation.clip domains: totalRelations=`{0}`, resolvedTargets=`{1}`, unresolvedTargets=`{2}`' -f `
            (ConvertTo-SmokeText $sourceIndexLegacyAnimationClipDomains.totalRelations "0"),
            (ConvertTo-SmokeText $sourceIndexLegacyAnimationClipDomains.resolvedTargets "0"),
            (ConvertTo-SmokeText $sourceIndexLegacyAnimationClipDomains.unresolvedTargets "0")))
        foreach ($domainRow in @($sourceIndexLegacyAnimationClipDomains.domainCounts | Select-Object -First 8)) {
            $reportLines.Add(('-   {0}: relations=`{1}`, animationComponents=`{2}`, samples=`{3}`' -f `
                (ConvertTo-SmokeText $domainRow.domain),
                (ConvertTo-SmokeText $domainRow.relations "0"),
                (ConvertTo-SmokeText $domainRow.animationComponents "0"),
                (ConvertTo-SmokeText $domainRow.samples "")))
        }
        $reportLines.Add('- Legacy Animation.clip rule: these are recorded as explicit Unity references, but current Naraka matches are VFX/marker style diagnostics and do not enable default character body animation capability.')
    }
    else {
        $reportLines.Add(('- Source-index legacy Animation.clip domain query: `{0}`' -f $sourceIndexLegacyAnimationClipDomains.status))
    }
}
$reportLines.Add("")
$reportLines.Add("## Shader Boundary Diagnostic")
$reportLines.Add("")
$reportLines.Add(('- Status: `{0}`' -f $shaderBoundaryStatus))
if ($shaderBoundaryStatus -eq "ok") {
    $reportLines.Add(('- Sample root: `{0}`' -f $ShaderBoundarySampleRoot))
    $reportLines.Add(('- glTF validator: `{0}`' -f $shaderBoundaryGltfValidationStatus))
    $reportLines.Add(('- Quality gates: textureLinkErrors=`{0}`, customShaderSidecars=`{1}`, layeredMaterialUnresolvedSidecars=`{2}`, degradedPreviewSidecars=`{3}`' -f `
        (ConvertTo-SmokeText $shaderBoundarySummaryJson.qualityGates.textureLinkErrors "0"),
        (ConvertTo-SmokeText $shaderBoundarySummaryJson.qualityGates.customShaderRequiredSidecars "0"),
        (ConvertTo-SmokeText $shaderBoundarySummaryJson.qualityGates.layeredMaterialUnresolvedSidecars "0"),
        (ConvertTo-SmokeText $shaderBoundarySummaryJson.qualityGates.degradedPreviewSidecars "0")))
    $reportLines.Add(('- material_sidecars rows=`{0}`, degraded custom shader rows=`{1}`' -f `
        (ConvertTo-SmokeText $shaderBoundaryMaterialSidecarRows "unknown"),
        (ConvertTo-SmokeText $shaderBoundaryCustomShaderRows "unknown")))
    $reportLines.Add('- Rule: this is a degraded PBR preview boundary check for Naraka private/layered shaders; it preserves raw texture slots and must not be treated as texture loss or fully restored game shading.')
}
elseif ($shaderBoundaryStatus -eq "missing") {
    $reportLines.Add(('- Sample root missing: `{0}`' -f $ShaderBoundarySampleRoot))
}
$reportLines.Add("")
$reportLines.Add("## Static Environment Extension Diagnostic")
$reportLines.Add("")
$reportLines.Add(('- Status: `{0}`' -f $staticEnvironmentStatus))
if ($staticEnvironmentStatus -eq "ok") {
    $reportLines.Add(('- Sample root: `{0}`' -f $StaticEnvironmentSampleRoot))
    $reportLines.Add(('- glTF: `{0}`' -f $staticEnvironmentGltf))
    $reportLines.Add(('- glTF validator: `{0}`' -f $staticEnvironmentGltfValidationStatus))
    $reportLines.Add(('- Model validation: models=`{0}`, ok=`{1}`, withTextures=`{2}`' -f `
        (ConvertTo-SmokeText $staticEnvironmentValidationJson.totals.models "0"),
        (ConvertTo-SmokeText $staticEnvironmentValidationJson.totals.ok "0"),
        (ConvertTo-SmokeText $staticEnvironmentValidationJson.totals.withTextures "0")))
    $reportLines.Add(('- SQLite counts: textureAssets=`{0}`, textureLinks=`{1}`, textureLinkErrors=`{2}`, materialSidecars=`{3}`, modelRows=`{4}`' -f `
        (ConvertTo-SmokeText $staticEnvironmentSummaryJson.counts.textureAssets "0"),
        (ConvertTo-SmokeText $staticEnvironmentSummaryJson.counts.textureLinks "0"),
        (ConvertTo-SmokeText $staticEnvironmentTextureLinkErrors "unknown"),
        (ConvertTo-SmokeText $staticEnvironmentSummaryJson.counts.materialSidecars "0"),
        (ConvertTo-SmokeText $staticEnvironmentModelRows "unknown")))
    $reportLines.Add('- Rule: this sample is an explicit static environment/prop extension check. It proves deterministic mesh, UV, texture and material-sidecar export for a visible static asset, but it does not broaden the default Library smoke beyond representative prefab/GameObject models.')
}
elseif ($staticEnvironmentStatus -eq "missing") {
    $reportLines.Add(('- Sample root missing: `{0}`' -f $StaticEnvironmentSampleRoot))
}
$reportLines.Add("")
$reportLines.Add("## Character Candidate Diagnostic")
$reportLines.Add("")
$reportLines.Add(('- Status: `{0}`' -f $characterCandidateStatus))
if ($characterCandidateStatus -eq "ok") {
    $reportLines.Add(('- Sample root: `{0}`' -f $CharacterCandidateSampleRoot))
    $reportLines.Add(('- glTF: `{0}`' -f $characterCandidateGltf))
    $reportLines.Add(('- glTF validator: `{0}`' -f $characterCandidateGltfValidationStatus))
    $reportLines.Add(('- Model validation: models=`{0}`, ok=`{1}`, withSkin=`{2}`, withTextures=`{3}`, skinJoints=`{4}`, maxBoundsSize=`{5}`' -f `
        (ConvertTo-SmokeText $characterCandidateValidationJson.totals.models "0"),
        (ConvertTo-SmokeText $characterCandidateValidationJson.totals.ok "0"),
        (ConvertTo-SmokeText $characterCandidateValidationJson.totals.withSkin "0"),
        (ConvertTo-SmokeText $characterCandidateValidationJson.totals.withTextures "0"),
        (ConvertTo-SmokeText $characterCandidateSkinJointCount "0"),
        (ConvertTo-SmokeText $characterCandidateMaxBoundsSize "0")))
    $reportLines.Add(('- SQLite counts: textureAssets=`{0}`, textureLinks=`{1}`, textureLinkErrors=`{2}`, materialSidecars=`{3}`, modelRows=`{4}`, modelAnimationCandidates=`{5}`, modelAnimationRelations=`{6}`, relationAnimations=`{7}`' -f `
        (ConvertTo-SmokeText $characterCandidateSummaryJson.counts.textureAssets "0"),
        (ConvertTo-SmokeText $characterCandidateSummaryJson.counts.textureLinks "0"),
        (ConvertTo-SmokeText $characterCandidateTextureLinkErrors "unknown"),
        (ConvertTo-SmokeText $characterCandidateSummaryJson.counts.materialSidecars "0"),
        (ConvertTo-SmokeText $characterCandidateModelRows "unknown"),
        (ConvertTo-SmokeText $characterCandidateModelAnimationCandidateRows "unknown"),
        (ConvertTo-SmokeText $characterCandidateModelAnimationRelationRows "unknown"),
        (ConvertTo-SmokeText $characterCandidateRelationAnimationRows "unknown")))
    if ($characterCandidateSourceIndexBoundary.status -eq "ok") {
        $reportLines.Add(('- Source-index boundary: animator.avatar=`{0}`, characterAnimatorAvatar=`{1}`, sameBundleAnimationBindings=`{2}`, muscleBindings=`{3}`, explicitEdges animator.controller/animation.clip/animatorController.clip=`{4}/{5}/{6}`' -f `
            (ConvertTo-SmokeText $characterCandidateSourceIndexBoundary.animatorAvatarRows "0"),
            (ConvertTo-SmokeText $characterCandidateSourceIndexBoundary.characterAnimatorAvatarRows "0"),
            (ConvertTo-SmokeText $characterCandidateSourceIndexBoundary.animationBindings "0"),
            (ConvertTo-SmokeText $characterCandidateSourceIndexBoundary.muscleAnimationBindings "0"),
            (ConvertTo-SmokeText $characterCandidateSourceIndexBoundary.animatorControllerRows "0"),
            (ConvertTo-SmokeText $characterCandidateSourceIndexBoundary.animationClipRows "0"),
            (ConvertTo-SmokeText $characterCandidateSourceIndexBoundary.animatorControllerClipRows "0")))
        $reportLines.Add(('- Source-index boundary samples: source=`{0}`, topAnimationNames=`{1}`' -f `
            (ConvertTo-SmokeText $characterCandidateSourceIndexBoundary.relativeSource),
            (ConvertTo-SmokeText $characterCandidateSourceIndexBoundary.topAnimationNames "")))
    }
    else {
        $reportLines.Add(('- Source-index boundary query: `{0}`' -f $characterCandidateSourceIndexBoundary.status))
    }
    $reportLines.Add('- Rule: this is a stronger skinned humanoid/character candidate selected from Unity source-index Avatar and Renderer evidence. It can guide the next animation smoke target, but it still does not create a production model-animation binding without explicit clip/controller relation and visual validation.')
}
elseif ($characterCandidateStatus -eq "missing") {
    $reportLines.Add(('- Sample root missing: `{0}`' -f $CharacterCandidateSampleRoot))
}
$reportLines.Add("")
$reportLines.Add("## Animation Diagnostic")
$reportLines.Add("")
if ($null -ne $animationReportJson) {
    $reportLines.Add(('- Diagnostic status: `{0}`' -f (ConvertTo-SmokeText $animationReportJson.status)))
    $reportLines.Add(("- Message: {0}" -f (ConvertTo-SmokeText $animationReportJson.message)))
    $reportLines.Add(('- Animation glTF: `{0}`' -f (ConvertTo-SmokeText $animationGltfPath)))
    $reportLines.Add(('- Animation glTF validator: `{0}`' -f $animationGltfValidationStatus))
    $reportLines.Add(('- Avatar injection: mode=`{0}`, diagnosticOnly=`{1}`, notDefaultModelAnimationRelation=`{2}`, manualReviewRequired=`{3}`, tosCount=`{4}`' -f `
        (ConvertTo-SmokeText $animationReportJson.avatarInjection.mode),
        (ConvertTo-SmokeText $animationReportJson.avatarInjection.diagnosticOnly),
        (ConvertTo-SmokeText $animationReportJson.avatarInjection.notDefaultModelAnimationRelation),
        (ConvertTo-SmokeText $animationReportJson.avatarInjection.manualReviewRequired),
        (ConvertTo-SmokeText $animationReportJson.avatarInjection.tosCount "0")))
}
else {
    $reportLines.Add(('- Diagnostic status: `{0}`' -f $animationDiagnosticStatus))
}
$reportLines.Add("")
$reportLines.Add('This animation output is diagnostic-only. It must not be treated as a default model-animation relation, and it does not enable `asset_library.json.capabilities.animations=true`.')
$reportLines.Add("")
$reportLines.Add("## Artifacts")
$reportLines.Add("")
$reportLines.Add(('- `asset_library.json`: `{0}`' -f $manifest))
$reportLines.Add(('- `library_index.db`: `{0}`' -f $db))
$reportLines.Add(('- `sqlite_index_summary.json`: `{0}`' -f $sqliteSummary))
$reportLines.Add(('- `model_validation.json`: `{0}`' -f $validation))
$reportLines.Add(('- `smoke_summary.json`: `{0}`' -f $summaryJsonPath))
$reportLines.Add(('- Hadi script animation diagnostic JSON: `{0}`' -f $scriptAnimationComponentDiagnostics.hadiBody.report))
$reportLines.Add(('- FxAttack script animation diagnostic JSON: `{0}`' -f $scriptAnimationComponentDiagnostics.fxAttack.report))
$reportLines.Add(('- Hadi model glTF: `{0}`' -f $gltf))
$reportLines.Add(('- Bow prop glTF: `{0}`' -f $bowGltf))
$reportLines.Add(('- Device prop glTF: `{0}`' -f $deviceGltf))
if ($shaderBoundaryStatus -eq "ok") {
    $reportLines.Add(('- Shader boundary glTF: `{0}`' -f $shaderBoundaryGltf))
    $reportLines.Add(('- Shader boundary material report: `{0}`' -f (Join-Path $ShaderBoundarySampleRoot "MATERIAL_REPORT.md")))
}
if ($staticEnvironmentStatus -eq "ok") {
    $reportLines.Add(('- Static environment glTF: `{0}`' -f $staticEnvironmentGltf))
    $reportLines.Add(('- Static environment SQLite summary: `{0}`' -f (Join-Path $StaticEnvironmentSampleRoot "sqlite_index_summary.json")))
}
if ($characterCandidateStatus -eq "ok") {
    $reportLines.Add(('- Character candidate glTF: `{0}`' -f $characterCandidateGltf))
    $reportLines.Add(('- Character candidate SQLite summary: `{0}`' -f (Join-Path $CharacterCandidateSampleRoot "sqlite_index_summary.json")))
}
if ($null -ne $animationGltfPath) {
    $reportLines.Add(('- Diagnostic animation glTF: `{0}`' -f $animationGltfPath))
}
$reportLines | Set-Content -LiteralPath $reportPath -Encoding UTF8

Write-Host ""
Write-Host "Naraka first usable smoke completed."
Write-Host "Output: $OutputRoot"
Write-Host "Library: $libraryOutput"
Write-Host "Report: $reportPath"
Write-Host "Summary JSON: $summaryJsonPath"
Write-Host ("Capabilities: models={0}, animations={1}" -f $assetLibrary.capabilities.models, $assetLibrary.capabilities.animations)
if (!$SkipAnimationDiagnostic) {
    Write-Host "Animation diagnostic: $animationOutput"
}
if ($null -ne $modelValidation.totals) {
    $modelValidation.totals | Format-List
}
