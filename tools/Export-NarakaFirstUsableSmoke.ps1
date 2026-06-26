param(
    [string]$GameRoot = "C:\Game163\program",
    [string]$SourceRoot = "",
    [string]$SourceIndex = "D:\Assets\Naraka\SourceIndex_Full_HeaderFix1\unity_source_index.db",
    [string]$AnimationSourceIndex = "D:\Assets\Naraka\SourceIndex_Naraka_TosMini_Current\unity_source_index.db",
    [string]$AnimationSidecar = "D:\Assets\Naraka\Dijiang_AttackA8_FullDecodedSidecar_Current\Animations\assets\res\animclipadapter\05\mo_pve_b_dijiang_attack_a8_01.animation_asset.json",
    [string]$AnimationPreviewAvatar = "mo_pve_b_dijiang_01_skeletonAvatar",
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

    return ($Value | ConvertTo-Json -Depth $Depth -Compress)
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

if (!$KeepExisting -and (Test-Path -LiteralPath $OutputRoot)) {
    Remove-Item -LiteralPath $OutputRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

$probeOutput = Join-Path $OutputRoot "SourceInputProbe"
$libraryOutput = Join-Path $OutputRoot "RepresentativeModels"
$animationOutput = Join-Path $OutputRoot "AnimationDiagnostic_DijiangA8"
$representativeGltfValidationStatus = if ($SkipGltfValidation) { "skipped" } else { "toolMissing" }
$animationGltfValidationStatus = if ($SkipGltfValidation -or $SkipAnimationDiagnostic) { "skipped" } else { "toolMissing" }
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
$gltf = Join-Path $libraryOutput "Models\assets\res\prefab\actor_visual_part\ch_m_hadi\ch_m_hadi_lv_s9\ch_m_hadi_lv_s9.gltf"
$bowGltf = Join-Path $libraryOutput "Models\assets\res\prefab\drop_item_generate\weapon\weapon_drop_bow_dongjun\weapon_drop_bow_dongjun.gltf"
$deviceGltf = Join-Path $libraryOutput "Models\assets\res\prefab\device_generate\device_hongbao_02\device_hongbao_02.gltf"
$representativeGltfs = @($gltf, $bowGltf, $deviceGltf)

Test-FileRequired -Path $manifest -Label "asset_library.json"
Test-FileRequired -Path $db -Label "library_index.db"
Test-FileRequired -Path $sqliteSummary -Label "sqlite_index_summary.json"
Test-FileRequired -Path $validation -Label "model_validation.json"
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

$assetLibrary = Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
$modelValidation = Get-Content -LiteralPath $validation -Raw -Encoding UTF8 | ConvertFrom-Json
$sqliteSummaryJson = Get-Content -LiteralPath $sqliteSummary -Raw -Encoding UTF8 | ConvertFrom-Json
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
$summaryJsonLines += '  "checks": {'
$summaryJsonLines += '    "representativeGltfValidation": ' + (ConvertTo-SmokeJsonLiteral $representativeGltfValidationStatus) + ","
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
$summaryJsonLines += '    "explicitAnimatorControllerUsages": ' + $explicitAnimatorControllerUsagesJson
$summaryJsonLines += '  },'
$summaryJsonLines += '  "animationDiagnostic": ' + $animationDiagnosticJson
$summaryJsonLines += "}"
$summaryJson = $summaryJsonLines -join [Environment]::NewLine
$summaryJsonPath = [System.IO.Path]::Combine($OutputRoot, "smoke_summary.json")
if ([string]::IsNullOrWhiteSpace($summaryJsonPath)) {
    throw "smoke_summary.json output path is empty. OutputRoot='$OutputRoot'"
}
$summaryJson | Set-Content -LiteralPath $summaryJsonPath -Encoding UTF8

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
    }
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
$reportLines.Add(('- Hadi model glTF: `{0}`' -f $gltf))
$reportLines.Add(('- Bow prop glTF: `{0}`' -f $bowGltf))
$reportLines.Add(('- Device prop glTF: `{0}`' -f $deviceGltf))
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
