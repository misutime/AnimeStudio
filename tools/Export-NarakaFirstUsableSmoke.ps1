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
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed with exit code $LASTEXITCODE"
    }
}

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
}

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
$hadiOutput = Join-Path $OutputRoot "HadiBody_s9"
$animationOutput = Join-Path $OutputRoot "AnimationDiagnostic_DijiangA8"
$hadiGltfValidationStatus = if ($SkipGltfValidation) { "skipped" } else { "toolMissing" }
$animationGltfValidationStatus = if ($SkipGltfValidation -or $SkipAnimationDiagnostic) { "skipped" } else { "toolMissing" }
$animationDiagnosticStatus = if ($SkipAnimationDiagnostic) { "skipped" } else { "pending" }
$animationReportJson = $null
$animationGltfPath = $null
$browserValidationStatus = if ($SkipBrowserValidation) { "skipped" } else { "toolMissing" }
$thumbnailStatus = if ($SkipBrowserValidation) { "skipped" } else { "toolMissing" }

Invoke-Checked `
    -Label "Probe Naraka input" `
    -FilePath $cli `
    -Arguments @(
        $SourceRoot,
        $probeOutput,
        "--game", "Naraka",
        "--probe_source_input"
    )

# 这个样本来自完整源索引的确定性闭包，用来证明标准角色部件模型链路。
Invoke-Checked `
    -Label "Export Hadi body s9 smoke" `
    -FilePath $cli `
    -Arguments @(
        $SourceRoot,
        $hadiOutput,
        "--game", "Naraka",
        "--mode", "Library",
        "--group_assets", "ByLibrary",
        "--profile_3d", "Core",
        "--model_format", "Gltf",
        "--texture_mode", "Png",
        "--animation_package", "Separate",
        "--fbx_animation", "Skip",
        "--source_index", $SourceIndex,
        "--source_files", "4\c\4c08b7069a411750",
        "--path_ids", "-6619473669887381141"
    )

Invoke-Checked `
    -Label "Rebuild AssetLibrary v1 index" `
    -FilePath $cli `
    -Arguments @(
        "--build_sqlite_index", $hadiOutput,
        "--source_index", $SourceIndex,
        "--game", "Naraka",
        "--skip_sqlite_file_index"
    )

$manifest = Join-Path $hadiOutput "asset_library.json"
$db = Join-Path $hadiOutput "library_index.db"
$validation = Join-Path $hadiOutput "model_validation.json"
$gltf = Join-Path $hadiOutput "Models\assets\res\prefab\actor_visual_part\ch_m_hadi\ch_m_hadi_lv_s9\ch_m_hadi_lv_s9.gltf"

Test-FileRequired -Path $manifest -Label "asset_library.json"
Test-FileRequired -Path $db -Label "library_index.db"
Test-FileRequired -Path $validation -Label "model_validation.json"
Test-FileRequired -Path $gltf -Label "Hadi glTF"

if (!$SkipGltfValidation) {
    $gltfTransform = Get-Command "gltf-transform.cmd" -ErrorAction SilentlyContinue
    if ($null -ne $gltfTransform) {
        Invoke-Checked `
            -Label "Validate Hadi glTF" `
            -FilePath $gltfTransform.Source `
            -Arguments @("validate", $gltf)
        $hadiGltfValidationStatus = "ok"
    }
    else {
        Write-Warning "gltf-transform.cmd not found; skipped glTF validator."
    }
}

if (!$SkipAnimationDiagnostic) {
    Test-FileRequired -Path $AnimationSourceIndex -Label "Naraka animation diagnostic source index"
    Test-FileRequired -Path $AnimationSidecar -Label "Naraka animation sidecar"

    # 手动动画诊断只证明路径恢复和独立 glTF 写出，不创建默认模型-动画推荐关系。
    Invoke-Checked `
        -Label "Export Dijiang A8 standalone animation diagnostic" `
        -FilePath $cli `
        -Arguments @(
        "--export_animation_gltf_from_files", $gltf,
        "--preview_animation", $AnimationSidecar,
        "--preview_output", $animationOutput,
        "--source_index", $AnimationSourceIndex,
        "--preview_avatar", $AnimationPreviewAvatar
    )
    $animationDiagnosticStatus = "ok"

    $animationReport = Join-Path $animationOutput "standalone_animation_gltf_report.json"
    Test-FileRequired -Path $animationReport -Label "standalone_animation_gltf_report.json"
    $animationReportJson = Get-Content -LiteralPath $animationReport -Raw | ConvertFrom-Json

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
                Invoke-Checked `
                    -Label "Validate Dijiang A8 animation glTF" `
                    -FilePath $gltfTransform.Source `
                    -Arguments @("validate", $animationGltfPath)
                $animationGltfValidationStatus = "ok"
            }
        }
    }
}

if (!$SkipBrowserValidation) {
    $browserCli = "D:\misutime\UnrealExporter\dist\AssetLibraryBrowser.Cli\AssetLibraryBrowser.Cli.exe"
    if (Test-Path -LiteralPath $browserCli) {
        Invoke-Checked `
            -Label "Validate AssetLibrary v1" `
            -FilePath $browserCli `
            -Arguments @("validate-library", $hadiOutput)
        $browserValidationStatus = "ok"

        Invoke-Checked `
            -Label "Render browser thumbnail" `
            -FilePath $browserCli `
            -Arguments @("build-thumbnails", $hadiOutput, "1", "1")
        $thumbnailStatus = "ok"
    }
    else {
        Write-Warning "AssetLibraryBrowser.Cli not found; skipped browser validation."
    }
}

$assetLibrary = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
$modelValidation = Get-Content -LiteralPath $validation -Raw | ConvertFrom-Json
$thumbnailCache = Join-Path $hadiOutput ".asset_browser_cache"
$thumbnailFileCount = 0
if (Test-Path -LiteralPath $thumbnailCache) {
    $thumbnailFileCount = (Get-ChildItem -LiteralPath $thumbnailCache -Recurse -File | Measure-Object).Count
}

# 这两个报告只汇总已经验证过的产物，不改变正式素材库内容或动画关系结论。
$smokeSummary = [ordered]@{
    generatedAt = (Get-Date).ToString("o")
    game = "Naraka"
    sourceRoot = $SourceRoot
    sourceIndex = $SourceIndex
    outputRoot = $OutputRoot
    libraryRoot = $hadiOutput
    assetLibrary = $manifest
    libraryIndex = $db
    modelValidation = $validation
    modelGltf = $gltf
    checks = [ordered]@{
        hadiGltfValidation = $hadiGltfValidationStatus
        browserValidation = $browserValidationStatus
        thumbnailRender = $thumbnailStatus
        thumbnailFileCount = $thumbnailFileCount
        animationDiagnostic = $animationDiagnosticStatus
        animationGltfValidation = $animationGltfValidationStatus
    }
    capabilities = [ordered]@{
        models = $assetLibrary.capabilities.models
        animations = $assetLibrary.capabilities.animations
        animationPreviewComposer = $assetLibrary.capabilities.animationPreviewComposer
    }
    modelTotals = $modelValidation.totals
    animationDiagnostic = if ($null -ne $animationReportJson) {
        [ordered]@{
            status = $animationReportJson.status
            message = $animationReportJson.message
            gltf = $animationGltfPath
            avatarInjectionMode = $animationReportJson.avatarInjection.mode
            diagnosticOnly = $animationReportJson.avatarInjection.diagnosticOnly
            notDefaultModelAnimationRelation = $animationReportJson.avatarInjection.notDefaultModelAnimationRelation
            manualReviewRequired = $animationReportJson.avatarInjection.manualReviewRequired
            tosCount = $animationReportJson.avatarInjection.tosCount
        }
    } else {
        [ordered]@{
            status = $animationDiagnosticStatus
            diagnosticOnly = $true
            notDefaultModelAnimationRelation = $true
        }
    }
}

$summaryJsonPath = Join-Path $OutputRoot "smoke_summary.json"
$smokeSummary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $summaryJsonPath -Encoding UTF8

$reportPath = Join-Path $OutputRoot "SMOKE_REPORT.md"
$reportLines = [System.Collections.Generic.List[string]]::new()
$reportLines.Add("# Naraka first usable smoke report")
$reportLines.Add("")
$reportLines.Add(("Generated: {0}" -f $smokeSummary.generatedAt))
$reportLines.Add("")
$reportLines.Add("## Summary")
$reportLines.Add("")
$reportLines.Add(('- Source root: `{0}`' -f $SourceRoot))
$reportLines.Add(('- Source index: `{0}`' -f $SourceIndex))
$reportLines.Add(('- Output root: `{0}`' -f $OutputRoot))
$reportLines.Add(('- Library root: `{0}`' -f $hadiOutput))
$reportLines.Add("")
$reportLines.Add("## Static Library")
$reportLines.Add("")
$reportLines.Add(('- Capabilities: models=`{0}`, animations=`{1}`, animationPreviewComposer=`{2}`' -f $assetLibrary.capabilities.models, $assetLibrary.capabilities.animations, (ConvertTo-SmokeText $assetLibrary.capabilities.animationPreviewComposer "null")))
$reportLines.Add(('- Hadi glTF validator: `{0}`' -f $hadiGltfValidationStatus))
$reportLines.Add(('- AssetLibrary browser validation: `{0}`' -f $browserValidationStatus))
$reportLines.Add(('- Thumbnail render: `{0}`, cache files=`{1}`' -f $thumbnailStatus, $thumbnailFileCount))
if ($null -ne $modelValidation.totals) {
    $reportLines.Add(('- Model totals: models=`{0}`, ok=`{1}`, warning=`{2}`, error=`{3}`' -f `
        (ConvertTo-SmokeText $modelValidation.totals.models "0"),
        (ConvertTo-SmokeText $modelValidation.totals.ok "0"),
        (ConvertTo-SmokeText $modelValidation.totals.warning "0"),
        (ConvertTo-SmokeText $modelValidation.totals.error "0")))
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
$reportLines.Add(('- `model_validation.json`: `{0}`' -f $validation))
$reportLines.Add(('- `smoke_summary.json`: `{0}`' -f $summaryJsonPath))
$reportLines.Add(('- Hadi model glTF: `{0}`' -f $gltf))
if ($null -ne $animationGltfPath) {
    $reportLines.Add(('- Diagnostic animation glTF: `{0}`' -f $animationGltfPath))
}
$reportLines | Set-Content -LiteralPath $reportPath -Encoding UTF8

Write-Host ""
Write-Host "Naraka first usable smoke completed."
Write-Host "Output: $OutputRoot"
Write-Host "Library: $hadiOutput"
Write-Host "Report: $reportPath"
Write-Host "Summary JSON: $summaryJsonPath"
Write-Host ("Capabilities: models={0}, animations={1}" -f $assetLibrary.capabilities.models, $assetLibrary.capabilities.animations)
if (!$SkipAnimationDiagnostic) {
    Write-Host "Animation diagnostic: $animationOutput"
}
if ($null -ne $modelValidation.totals) {
    $modelValidation.totals | Format-List
}
