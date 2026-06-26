param(
    [string]$GameRoot = "C:\Game163\program",
    [string]$SourceRoot = "",
    [string]$SourceIndex = "D:\Assets\Naraka\SourceIndex_Full_HeaderFix1\unity_source_index.db",
    [string]$AnimationSourceIndex = "D:\Assets\Naraka\SourceIndex_Naraka_TosMini_Current\unity_source_index.db",
    [string]$AnimationSidecar = "D:\Assets\Naraka\Dijiang_AttackA8_FullDecodedSidecar_Current\Animations\assets\res\animclipadapter\05\mo_pve_b_dijiang_attack_a8_01.animation_asset.json",
    [string]$AnimationPreviewModel = "mo_pve_b_dijiang_01_skeleton",
    [string]$AnimationPreviewClip = "mo_pve_b_dijiang_attack_a8_01",
    [string]$AnimationPreviewAvatar = "mo_pve_b_dijiang_01_skeletonAvatar",
    [string]$ShaderBoundarySampleRoot = "D:\Assets\Naraka\FaceMaleBattle_ShaderBoundary_Current",
    [string]$StaticEnvironmentSampleRoot = "D:\Assets\Naraka\Smoke_static_jisui_device_bigtree_04_Current",
    [string]$CharacterCandidateSampleRoot = "D:\Assets\Naraka\Naraka_CompleteCharacterCandidate_SamuraiGhost_BundleRoot_Current",
    [string]$ZhumuScriptAnimationSampleRoot = "D:\Assets\Naraka\Naraka_ZhumuSoul_AttackPrefab_ModelProbe_Current",
    [string]$ZhumuMergedAnimationProbeRoot = "D:\Assets\Naraka\Zhumu_AttackA4_MergedModelAnimationProbe_Current",
    [string]$CharacterCandidateSourceIndexBundle = "b\6\b6449028544fa466",
    [string]$CharacterCandidateSourceIndexSerializedFile = "CAB-43d9a2106c54892c7f775b8d7ab8b193",
    [string]$CharacterCandidateSourceIndexAnimatorName = "ch_m_japan_samurai_ghost",
    [long]$CharacterCandidateSourceIndexRootPathId = 7640773285473327857,
    [string]$FormalSkinnedRepresentativeBundle = "c\8\c8f77e18090d4d34",
    [long]$FormalSkinnedRepresentativeRootPathId = 2767142543816441398,
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

function Get-SmokePropertyValue {
    param(
        [object]$Object,
        [string]$Name
    )

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function ConvertTo-SmokeInt64 {
    param(
        [object]$Value,
        [long]$Default = 0
    )

    if ($null -eq $Value) {
        return $Default
    }

    try {
        return [Convert]::ToInt64($Value)
    }
    catch {
        return $Default
    }
}

function ConvertTo-SmokeDouble {
    param(
        [object]$Value,
        [double]$Default = 0.0
    )

    if ($null -eq $Value) {
        return $Default
    }

    try {
        return [Convert]::ToDouble($Value, [System.Globalization.CultureInfo]::InvariantCulture)
    }
    catch {
        return $Default
    }
}

function Resolve-SmokePython {
    $python = Get-Command "python" -ErrorAction SilentlyContinue
    if ($null -ne $python) {
        return $python.Source
    }

    $fallback = "C:\Users\Misu\AppData\Local\Programs\Python\Python313\python.exe"
    if (Test-Path -LiteralPath $fallback) {
        return $fallback
    }

    throw "Python runtime not found; cannot run PNG render subject diagnostics."
}

function Read-RenderProbeBboxMotion {
    param(
        [string[]]$Lines
    )

    $frames = @{}
    foreach ($line in @($Lines)) {
        $match = [regex]::Match($line, '^(?<label>rest|mid|end):\s+frame=(?<frame>\d+)\s+bboxMin=\((?<min>[^)]+)\)\s+bboxMax=\((?<max>[^)]+)\)', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (!$match.Success) {
            continue
        }

        $mins = @($match.Groups["min"].Value -split "," | ForEach-Object { ConvertTo-SmokeDouble $_ })
        $maxs = @($match.Groups["max"].Value -split "," | ForEach-Object { ConvertTo-SmokeDouble $_ })
        if ($mins.Count -ne 3 -or $maxs.Count -ne 3) {
            continue
        }

        $frames[$match.Groups["label"].Value.ToLowerInvariant()] = [pscustomobject]@{
            frame = ConvertTo-SmokeInt64 $match.Groups["frame"].Value
            min = $mins
            max = $maxs
        }
    }

    foreach ($required in @("rest", "mid", "end")) {
        if (!$frames.ContainsKey($required)) {
            return [pscustomobject]@{
                status = "missingFrame"
                missingFrame = $required
                maxCoordinateDelta = 0.0
                rule = "三帧 bbox 运动诊断只能证明合并动画产生了几何包围盒变化，不能替代视觉验收。"
            }
        }
    }

    $rest = $frames["rest"]
    $maxDelta = 0.0
    foreach ($label in @("mid", "end")) {
        $frame = $frames[$label]
        for ($i = 0; $i -lt 3; $i++) {
            $maxDelta = [Math]::Max($maxDelta, [Math]::Abs([double]$frame.min[$i] - [double]$rest.min[$i]))
            $maxDelta = [Math]::Max($maxDelta, [Math]::Abs([double]$frame.max[$i] - [double]$rest.max[$i]))
        }
    }

    return [pscustomobject]@{
        status = if ($maxDelta -gt 0.01) { "ok" } else { "static" }
        maxCoordinateDelta = [Math]::Round($maxDelta, 4)
        rest = $frames["rest"]
        mid = $frames["mid"]
        end = $frames["end"]
        rule = "三帧 bbox 运动诊断只能证明合并动画产生了几何包围盒变化，不能替代视觉验收。"
    }
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
    $diagnosticSummary = Get-SmokePropertyValue -Object $json -Name "scriptAnimationComponentDiagnosticSummary"
    $scriptRows = @($json.scriptAnimationComponentDiagnostics)
    $invalidBoundaryRows = @($scriptRows | Where-Object { $_.diagnosticOnly -ne $true -or $_.notDefaultModelAnimationRelation -ne $true })
    $visibleRendererRows = @($scriptRows | Where-Object { [Convert]::ToInt64($_.gameObject.visibleRendererCount) -gt 0 })
    $subtreeVisibleRendererRows = @($scriptRows | Where-Object { [Convert]::ToInt64($_.gameObject.subtree.visibleRendererCount) -gt 0 })
    $subtreeSkinnedRendererRows = @($scriptRows | Where-Object { [Convert]::ToInt64($_.gameObject.subtree.skinnedMeshRendererCount) -gt 0 })
    $subtreeTruncatedRows = @($scriptRows | Where-Object { $_.gameObject.subtree.truncated -eq $true })
    $animatorRows = @($scriptRows | Where-Object { [Convert]::ToInt64($_.gameObject.animatorCount) -gt 0 })
    $firstRow = $null
    if ($scriptRows.Count -gt 0) {
        $firstRow = $scriptRows[0]
    }
    $firstScriptName = $null
    $firstFieldPath = $null
    $firstClipName = $null
    $firstSubtreeVisibleRendererCount = 0L
    $firstSubtreeSkinnedMeshRendererCount = 0L
    $firstSubtreeVisibleSamples = @()
    if ($null -ne $firstRow) {
        $firstScriptName = [string]$firstRow.monoBehaviour.scriptName
        $firstFieldPath = [string]$firstRow.reference.path
        $firstClipName = [string]$firstRow.reference.animation.name
        $firstSubtreeVisibleRendererCount = [Convert]::ToInt64($firstRow.gameObject.subtree.visibleRendererCount)
        $firstSubtreeSkinnedMeshRendererCount = [Convert]::ToInt64($firstRow.gameObject.subtree.skinnedMeshRendererCount)
        $firstSubtreeVisibleSamples = @($firstRow.gameObject.subtree.visibleGameObjectSamples)
    }
    $selectedModelCount = [Convert]::ToInt64($json.selectedModelCount)
    $candidateCount = [Convert]::ToInt64($json.candidateCount)
    $scriptAnimationRows = [Convert]::ToInt64($scriptRows.Count)
    $invalidBoundaryRowCount = [Convert]::ToInt64($invalidBoundaryRows.Count)
    $visibleRendererRowCount = [Convert]::ToInt64($visibleRendererRows.Count)
    $subtreeVisibleRendererRowCount = [Convert]::ToInt64($subtreeVisibleRendererRows.Count)
    $subtreeSkinnedRendererRowCount = [Convert]::ToInt64($subtreeSkinnedRendererRows.Count)
    $subtreeTruncatedRowCount = [Convert]::ToInt64($subtreeTruncatedRows.Count)
    $animatorRowCount = [Convert]::ToInt64($animatorRows.Count)
    $summaryStatus = $null
    $summaryProductionReadiness = $null
    $summaryBlockedRequirements = @()
    if ($null -ne $diagnosticSummary) {
        # 新报告直接给机器摘要；旧报告没有该节点时继续使用行数组统计。
        $summaryStatus = [string](Get-SmokePropertyValue -Object $diagnosticSummary -Name "status")
        $summaryRowCount = Get-SmokePropertyValue -Object $diagnosticSummary -Name "rowCount"
        $summaryLocalVisibleRendererRows = Get-SmokePropertyValue -Object $diagnosticSummary -Name "localVisibleRendererRows"
        $summarySubtreeVisibleRendererRows = Get-SmokePropertyValue -Object $diagnosticSummary -Name "subtreeVisibleRendererRows"
        $summarySubtreeSkinnedRendererRows = Get-SmokePropertyValue -Object $diagnosticSummary -Name "subtreeSkinnedRendererRows"
        $summarySubtreeTruncatedRows = Get-SmokePropertyValue -Object $diagnosticSummary -Name "subtreeTruncatedRows"
        $summaryLocalAnimatorRows = Get-SmokePropertyValue -Object $diagnosticSummary -Name "localAnimatorRows"
        $summaryDiagnosticOnly = Get-SmokePropertyValue -Object $diagnosticSummary -Name "diagnosticOnly"
        $summaryNotDefaultRelation = Get-SmokePropertyValue -Object $diagnosticSummary -Name "notDefaultModelAnimationRelation"
        $summaryDefaultCandidateCount = Get-SmokePropertyValue -Object $diagnosticSummary -Name "defaultCandidateCount"
        $summaryProductionReadiness = Get-SmokePropertyValue -Object $diagnosticSummary -Name "productionReadiness"
        $summaryBlockedRequirementsValue = Get-SmokePropertyValue -Object $diagnosticSummary -Name "blockedProductionRequirements"
        if ($null -ne $summaryBlockedRequirementsValue) {
            $summaryBlockedRequirements = @($summaryBlockedRequirementsValue)
        }
        if ($null -ne $summaryRowCount) {
            $scriptAnimationRows = [Convert]::ToInt64($summaryRowCount)
        }
        if ($null -ne $summaryLocalVisibleRendererRows) {
            $visibleRendererRowCount = [Convert]::ToInt64($summaryLocalVisibleRendererRows)
        }
        if ($null -ne $summarySubtreeVisibleRendererRows) {
            $subtreeVisibleRendererRowCount = [Convert]::ToInt64($summarySubtreeVisibleRendererRows)
        }
        if ($null -ne $summarySubtreeSkinnedRendererRows) {
            $subtreeSkinnedRendererRowCount = [Convert]::ToInt64($summarySubtreeSkinnedRendererRows)
        }
        if ($null -ne $summarySubtreeTruncatedRows) {
            $subtreeTruncatedRowCount = [Convert]::ToInt64($summarySubtreeTruncatedRows)
        }
        if ($null -ne $summaryLocalAnimatorRows) {
            $animatorRowCount = [Convert]::ToInt64($summaryLocalAnimatorRows)
        }
        if ($summaryDiagnosticOnly -ne $true -or $summaryNotDefaultRelation -ne $true -or [Convert]::ToInt64($summaryDefaultCandidateCount) -ne 0) {
            $invalidBoundaryRowCount++
        }
        if ([string]$summaryProductionReadiness -ne "blocked" -or $summaryBlockedRequirements.Count -le 0) {
            $invalidBoundaryRowCount++
        }
    }
    if ([string]::IsNullOrWhiteSpace($summaryStatus)) {
        $summaryStatus = if ($scriptAnimationRows -gt 0) { "diagnosticOnly" } else { "empty" }
    }

    # 这里只读报告并压成 smoke 摘要；脚本动画语义仍然需要游戏脚本解释，不能升级成默认候选。
    $summaryJsonLines = @()
    $summaryJsonLines += "{"
    $summaryJsonLines += '  "selector": ' + (ConvertTo-SmokeJsonLiteral $Selector) + ","
    $summaryJsonLines += '  "outputRoot": ' + (ConvertTo-SmokeJsonLiteral $OutputRoot) + ","
    $summaryJsonLines += '  "report": ' + (ConvertTo-SmokeJsonLiteral $report) + ","
    $summaryJsonLines += '  "selectedModelCount": ' + (ConvertTo-SmokeJsonLiteral $selectedModelCount) + ","
    $summaryJsonLines += '  "candidateCount": ' + (ConvertTo-SmokeJsonLiteral $candidateCount) + ","
    $summaryJsonLines += '  "diagnosticSummaryStatus": ' + (ConvertTo-SmokeJsonLiteral $summaryStatus) + ","
    $summaryJsonLines += '  "productionReadiness": ' + (ConvertTo-SmokeJsonLiteral $summaryProductionReadiness) + ","
    $summaryJsonLines += '  "blockedProductionRequirements": ' + (ConvertTo-SmokeJsonLiteral $summaryBlockedRequirements 10) + ","
    $summaryJsonLines += '  "scriptAnimationRows": ' + (ConvertTo-SmokeJsonLiteral $scriptAnimationRows) + ","
    $summaryJsonLines += '  "invalidBoundaryRows": ' + (ConvertTo-SmokeJsonLiteral $invalidBoundaryRowCount) + ","
    $summaryJsonLines += '  "visibleRendererRows": ' + (ConvertTo-SmokeJsonLiteral $visibleRendererRowCount) + ","
    $summaryJsonLines += '  "subtreeVisibleRendererRows": ' + (ConvertTo-SmokeJsonLiteral $subtreeVisibleRendererRowCount) + ","
    $summaryJsonLines += '  "subtreeSkinnedRendererRows": ' + (ConvertTo-SmokeJsonLiteral $subtreeSkinnedRendererRowCount) + ","
    $summaryJsonLines += '  "subtreeTruncatedRows": ' + (ConvertTo-SmokeJsonLiteral $subtreeTruncatedRowCount) + ","
    $summaryJsonLines += '  "animatorRows": ' + (ConvertTo-SmokeJsonLiteral $animatorRowCount) + ","
    $summaryJsonLines += '  "firstScriptName": ' + (ConvertTo-SmokeJsonLiteral $firstScriptName) + ","
    $summaryJsonLines += '  "firstFieldPath": ' + (ConvertTo-SmokeJsonLiteral $firstFieldPath) + ","
    $summaryJsonLines += '  "firstClipName": ' + (ConvertTo-SmokeJsonLiteral $firstClipName) + ","
    $summaryJsonLines += '  "firstSubtreeVisibleRendererCount": ' + (ConvertTo-SmokeJsonLiteral $firstSubtreeVisibleRendererCount) + ","
    $summaryJsonLines += '  "firstSubtreeSkinnedMeshRendererCount": ' + (ConvertTo-SmokeJsonLiteral $firstSubtreeSkinnedMeshRendererCount) + ","
    $summaryJsonLines += '  "firstSubtreeVisibleSamples": ' + (ConvertTo-SmokeJsonLiteral $firstSubtreeVisibleSamples 5)
    $summaryJsonLines += "}"
    return (($summaryJsonLines -join [Environment]::NewLine) | ConvertFrom-Json)
}

function Read-SourceModelAvatarAnimationDiagnostic {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Selector,
        [Parameter(Mandatory = $true)]
        [string]$OutputRoot
    )

    $report = Join-Path $OutputRoot "source_model_animation_candidates.json"
    Test-FileRequired -Path $report -Label "source_model_animation_candidates.json"

    $json = Get-Content -LiteralPath $report -Raw -Encoding UTF8 | ConvertFrom-Json
    $avatarTosSummary = Get-SmokePropertyValue -Object $json -Name "avatarTosClipDiagnosticSummary"
    $modelAvatarSummary = Get-SmokePropertyValue -Object $json -Name "modelAvatarCompatibilityDiagnosticSummary"
    $avatarTosRows = @($json.avatarTosClipDiagnostics)
    $modelAvatarRows = @($json.modelAvatarCompatibilityDiagnostics)

    $selectedModelCount = ConvertTo-SmokeInt64 $json.selectedModelCount
    $candidateCount = ConvertTo-SmokeInt64 $json.candidateCount
    $invalidBoundaryRows = 0L

    $avatarTosStatus = if ($avatarTosRows.Count -gt 0) { "diagnosticOnly" } else { "empty" }
    $avatarTosRowCount = [long]$avatarTosRows.Count
    $avatarTosDefaultCandidateCount = 0L
    $avatarTosMaxCoverage = 0.0
    $avatarTosFullCoverageRows = 0L
    $avatarTosUnresolvedHashRows = 0L
    $avatarTosProductionReadiness = $null
    $avatarTosBlockedRequirements = @()
    if ($null -ne $avatarTosSummary) {
        $avatarTosStatus = [string](Get-SmokePropertyValue -Object $avatarTosSummary -Name "status")
        $avatarTosRowCount = ConvertTo-SmokeInt64 (Get-SmokePropertyValue -Object $avatarTosSummary -Name "rowCount")
        $avatarTosDefaultCandidateCount = ConvertTo-SmokeInt64 (Get-SmokePropertyValue -Object $avatarTosSummary -Name "defaultCandidateCount")
        $avatarTosMaxCoverage = ConvertTo-SmokeDouble (Get-SmokePropertyValue -Object $avatarTosSummary -Name "maxCoverageRatio")
        $avatarTosFullCoverageRows = ConvertTo-SmokeInt64 (Get-SmokePropertyValue -Object $avatarTosSummary -Name "fullCoverageRows")
        $avatarTosUnresolvedHashRows = ConvertTo-SmokeInt64 (Get-SmokePropertyValue -Object $avatarTosSummary -Name "unresolvedHashRows")
        $avatarTosDiagnosticOnly = Get-SmokePropertyValue -Object $avatarTosSummary -Name "diagnosticOnly"
        $avatarTosNotDefaultRelation = Get-SmokePropertyValue -Object $avatarTosSummary -Name "notDefaultModelAnimationRelation"
        $avatarTosProductionReadiness = Get-SmokePropertyValue -Object $avatarTosSummary -Name "productionReadiness"
        $avatarTosBlockedRequirementsValue = Get-SmokePropertyValue -Object $avatarTosSummary -Name "blockedProductionRequirements"
        if ($null -ne $avatarTosBlockedRequirementsValue) {
            $avatarTosBlockedRequirements = @($avatarTosBlockedRequirementsValue)
        }
        if ($avatarTosDiagnosticOnly -ne $true -or $avatarTosNotDefaultRelation -ne $true -or $avatarTosDefaultCandidateCount -ne 0) {
            $invalidBoundaryRows++
        }
        if ([string]$avatarTosProductionReadiness -ne "blocked" -or $avatarTosBlockedRequirements.Count -le 0) {
            $invalidBoundaryRows++
        }
    }
    foreach ($row in $avatarTosRows) {
        if ($row.diagnosticOnly -ne $true -or $row.notDefaultModelAnimationRelation -ne $true) {
            $invalidBoundaryRows++
        }
    }

    $modelAvatarStatus = if ($modelAvatarRows.Count -gt 0) { "diagnosticOnly" } else { "empty" }
    $modelAvatarRowCount = [long]$modelAvatarRows.Count
    $modelAvatarDefaultCandidateCount = 0L
    $modelAvatarHighOverlapRows = 0L
    $modelAvatarFullOverlapRows = 0L
    $modelAvatarMaxCoverage = 0.0
    $modelAvatarVisibleRendererRows = 0L
    $modelAvatarProductionReadiness = $null
    $modelAvatarBlockedRequirements = @()
    if ($null -ne $modelAvatarSummary) {
        $modelAvatarStatus = [string](Get-SmokePropertyValue -Object $modelAvatarSummary -Name "status")
        $modelAvatarRowCount = ConvertTo-SmokeInt64 (Get-SmokePropertyValue -Object $modelAvatarSummary -Name "rowCount")
        $modelAvatarDefaultCandidateCount = ConvertTo-SmokeInt64 (Get-SmokePropertyValue -Object $modelAvatarSummary -Name "defaultCandidateCount")
        $modelAvatarHighOverlapRows = ConvertTo-SmokeInt64 (Get-SmokePropertyValue -Object $modelAvatarSummary -Name "highOverlapRows")
        $modelAvatarFullOverlapRows = ConvertTo-SmokeInt64 (Get-SmokePropertyValue -Object $modelAvatarSummary -Name "fullOverlapRows")
        $modelAvatarMaxCoverage = ConvertTo-SmokeDouble (Get-SmokePropertyValue -Object $modelAvatarSummary -Name "maxCoverageRatio")
        $modelAvatarVisibleRendererRows = ConvertTo-SmokeInt64 (Get-SmokePropertyValue -Object $modelAvatarSummary -Name "visibleRendererRows")
        $modelAvatarDiagnosticOnly = Get-SmokePropertyValue -Object $modelAvatarSummary -Name "diagnosticOnly"
        $modelAvatarNotDefaultRelation = Get-SmokePropertyValue -Object $modelAvatarSummary -Name "notDefaultModelAnimationRelation"
        $modelAvatarProductionReadiness = Get-SmokePropertyValue -Object $modelAvatarSummary -Name "productionReadiness"
        $modelAvatarBlockedRequirementsValue = Get-SmokePropertyValue -Object $modelAvatarSummary -Name "blockedProductionRequirements"
        if ($null -ne $modelAvatarBlockedRequirementsValue) {
            $modelAvatarBlockedRequirements = @($modelAvatarBlockedRequirementsValue)
        }
        if ($modelAvatarDiagnosticOnly -ne $true -or $modelAvatarNotDefaultRelation -ne $true -or $modelAvatarDefaultCandidateCount -ne 0) {
            $invalidBoundaryRows++
        }
        if ([string]$modelAvatarProductionReadiness -ne "blocked" -or $modelAvatarBlockedRequirements.Count -le 0) {
            $invalidBoundaryRows++
        }
    }
    foreach ($row in $modelAvatarRows) {
        if ($row.diagnosticOnly -ne $true -or $row.notDefaultModelAnimationRelation -ne $true) {
            $invalidBoundaryRows++
        }
    }

    # 这里只压缩 Avatar/TOS 与结构兼容证据。它们是后续求解选点，不是默认动画绑定。
    return [pscustomobject]@{
        selector = $Selector
        outputRoot = $OutputRoot
        report = $report
        selectedModelCount = $selectedModelCount
        candidateCount = $candidateCount
        invalidBoundaryRows = $invalidBoundaryRows
        avatarTosStatus = $avatarTosStatus
        avatarTosRows = $avatarTosRowCount
        avatarTosDefaultCandidateCount = $avatarTosDefaultCandidateCount
        avatarTosProductionReadiness = $avatarTosProductionReadiness
        avatarTosBlockedProductionRequirements = $avatarTosBlockedRequirements
        avatarTosMaxCoverage = $avatarTosMaxCoverage
        avatarTosFullCoverageRows = $avatarTosFullCoverageRows
        avatarTosUnresolvedHashRows = $avatarTosUnresolvedHashRows
        modelAvatarStatus = $modelAvatarStatus
        modelAvatarRows = $modelAvatarRowCount
        modelAvatarDefaultCandidateCount = $modelAvatarDefaultCandidateCount
        modelAvatarProductionReadiness = $modelAvatarProductionReadiness
        modelAvatarBlockedProductionRequirements = $modelAvatarBlockedRequirements
        modelAvatarHighOverlapRows = $modelAvatarHighOverlapRows
        modelAvatarFullOverlapRows = $modelAvatarFullOverlapRows
        modelAvatarMaxCoverage = $modelAvatarMaxCoverage
        modelAvatarVisibleRendererRows = $modelAvatarVisibleRendererRows
    }
}

function Read-SourceIndexSimpleAnimationClipDomains {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceIndexPath
    )

    $sqlite3 = Get-Command "sqlite3.exe" -ErrorAction SilentlyContinue
    if ($null -eq $sqlite3) {
        return [pscustomobject]@{
            status = "toolMissing"
            rule = "sqlite3.exe missing; SimpleAnimation clip-domain diagnostic was skipped."
            totalRelations = 0
            distinctClipBuckets = 0
            domainCounts = @()
            probeShortlist = @()
            probeShortlistCount = 0
            productionReadiness = "blocked"
            blockedProductionRequirements = @("scriptSemantics", "deterministicModelRelation", "validatedModelGltf", "animationTrsExport", "visualReview")
        }
    }

    # 这里只做源索引诊断：按 SimpleAnimation 的脚本字段聚合 clip 域，不能生成默认模型-动画绑定。
    $sql = @"
CREATE TEMP TABLE simple_mb AS
SELECT from_file, from_path_id AS mb_path_id
FROM source_relations
WHERE relation='monoBehaviour.script'
  AND json_extract(raw_json,'$.details.scriptName')='SimpleAnimation';
CREATE INDEX temp.idx_simple_mb_key ON simple_mb(from_file, mb_path_id);
CREATE TEMP TABLE simple_clip AS
SELECT r.from_file, r.from_path_id AS mb_path_id, r.to_file AS clip_file, r.to_path_id AS clip_path_id, c.name AS clip_name
FROM simple_mb s
JOIN source_relations r ON r.relation='monoBehaviour.pptr' AND r.from_file=s.from_file AND r.from_path_id=s.mb_path_id
JOIN source_objects c ON c.serialized_file=r.to_file AND c.path_id=r.to_path_id AND c.type='AnimationClip';
WITH classified AS (
  SELECT
    CASE
      WHEN lower(clip_name) LIKE 'fx_%' THEN 'Fx'
      WHEN lower(clip_name) LIKE 'ui_%' OR lower(clip_name) LIKE 'anim_ui%' THEN 'UI'
      WHEN lower(clip_name) LIKE 'mo_%' THEN 'MonsterOrNpc'
      WHEN lower(clip_name) LIKE 'ch_%' THEN 'Character'
      WHEN lower(clip_name) LIKE '%male%' OR lower(clip_name) LIKE '%female%' THEN 'HumanNameToken'
      ELSE 'Other'
    END AS clip_domain,
    from_file,
    mb_path_id,
    clip_file,
    clip_path_id,
    clip_name
  FROM simple_clip
),
direct_context AS (
  SELECT
    classified.*,
    goRel.to_file AS go_file,
    goRel.to_path_id AS go_path_id,
    COALESCE(go.name, '') AS go_name,
    CASE WHEN EXISTS (
      SELECT 1
      FROM source_relations comp
      JOIN source_objects obj ON obj.serialized_file=comp.to_file AND obj.path_id=comp.to_path_id
      WHERE comp.relation='gameObject.component'
        AND comp.from_file=goRel.to_file
        AND comp.from_path_id=goRel.to_path_id
        AND obj.type='Animator'
    ) THEN 1 ELSE 0 END AS has_animator,
    CASE WHEN EXISTS (
      SELECT 1
      FROM source_relations comp
      JOIN source_objects obj ON obj.serialized_file=comp.to_file AND obj.path_id=comp.to_path_id
      WHERE comp.relation='gameObject.component'
        AND comp.from_file=goRel.to_file
        AND comp.from_path_id=goRel.to_path_id
        AND obj.type IN ('MeshRenderer','SkinnedMeshRenderer')
    ) THEN 1 ELSE 0 END AS has_direct_visible_renderer
  FROM classified
  LEFT JOIN source_relations goRel ON goRel.relation='component.gameObject' AND goRel.from_file=classified.from_file AND goRel.from_path_id=classified.mb_path_id
  LEFT JOIN source_objects go ON go.serialized_file=goRel.to_file AND go.path_id=goRel.to_path_id AND go.type='GameObject'
)
SELECT
  clip_domain,
  COUNT(*) AS relations,
  COUNT(DISTINCT clip_file || '#' || clip_path_id) AS distinct_clips,
  COUNT(DISTINCT CASE WHEN go_file IS NOT NULL THEN go_file || '#' || go_path_id END) AS distinct_game_objects,
  SUM(has_animator) AS rows_with_animator,
  SUM(has_direct_visible_renderer) AS rows_with_direct_visible_renderer,
  substr(group_concat(DISTINCT go_name),1,220) AS sample_game_objects,
  substr(group_concat(DISTINCT clip_name),1,220) AS sample_clips
FROM direct_context
GROUP BY clip_domain
ORDER BY relations DESC;
"@

    $rows = @(& $sqlite3.Source -readonly -batch -tabs $SourceIndexPath $sql)
    if ($LASTEXITCODE -ne 0) {
        throw "sqlite3 failed while reading SimpleAnimation clip-domain diagnostic from source index."
    }

    $domainRows = @()
    $totalRelations = 0L
    $totalDistinctClipBuckets = 0L
    foreach ($row in $rows) {
        if ([string]::IsNullOrWhiteSpace($row)) {
            continue
        }

        $parts = ([string]$row).Split("`t")
        if ($parts.Count -lt 8) {
            continue
        }

        $relations = ConvertTo-SmokeInt64 $parts[1]
        $distinctClips = ConvertTo-SmokeInt64 $parts[2]
        $totalRelations += $relations
        $totalDistinctClipBuckets += $distinctClips
        $domainRows += [pscustomobject]@{
            domain = [string]$parts[0]
            relations = $relations
            distinctClips = $distinctClips
            distinctGameObjects = ConvertTo-SmokeInt64 $parts[3]
            rowsWithAnimator = ConvertTo-SmokeInt64 $parts[4]
            rowsWithDirectVisibleRenderer = ConvertTo-SmokeInt64 $parts[5]
            sampleGameObjects = [string]$parts[6]
            sampleClips = [string]$parts[7]
        }
    }

    # 这份短名单只用于挑后续诊断样本。它保留脚本节点、clip 和 source/pathId，
    # 但不创建默认模型-动画关系，也不绕过模型、TRS 和视觉验收门槛。
    $shortlistSql = @"
CREATE TEMP TABLE simple_mb AS
SELECT from_file, from_path_id AS mb_path_id
FROM source_relations
WHERE relation='monoBehaviour.script'
  AND json_extract(raw_json,'$.details.scriptName')='SimpleAnimation';
CREATE INDEX temp.idx_simple_mb_key ON simple_mb(from_file, mb_path_id);
CREATE TEMP TABLE simple_clip AS
SELECT r.from_file, r.from_path_id AS mb_path_id, r.to_file AS clip_file, r.to_path_id AS clip_path_id, c.name AS clip_name
FROM simple_mb s
JOIN source_relations r ON r.relation='monoBehaviour.pptr' AND r.from_file=s.from_file AND r.from_path_id=s.mb_path_id
JOIN source_objects c ON c.serialized_file=r.to_file AND c.path_id=r.to_path_id AND c.type='AnimationClip';
WITH classified AS (
  SELECT
    CASE
      WHEN lower(clip_name) LIKE 'fx_%' THEN 'Fx'
      WHEN lower(clip_name) LIKE 'ui_%' OR lower(clip_name) LIKE 'anim_ui%' THEN 'UI'
      WHEN lower(clip_name) LIKE 'mo_%' THEN 'MonsterOrNpc'
      WHEN lower(clip_name) LIKE 'ch_%' THEN 'Character'
      WHEN lower(clip_name) LIKE '%male%' OR lower(clip_name) LIKE '%female%' THEN 'HumanNameToken'
      ELSE 'Other'
    END AS clip_domain,
    from_file,
    mb_path_id,
    clip_file,
    clip_path_id,
    clip_name
  FROM simple_clip
),
direct_context AS (
  SELECT DISTINCT
    classified.clip_domain,
    classified.from_file,
    classified.mb_path_id,
    classified.clip_file,
    classified.clip_path_id,
    classified.clip_name,
    goRel.to_file AS go_file,
    goRel.to_path_id AS go_path_id,
    COALESCE(go.name, '') AS go_name,
    COALESCE(go.source_path, '') AS go_source_path,
    CASE WHEN EXISTS (
      SELECT 1
      FROM source_relations comp
      JOIN source_objects obj ON obj.serialized_file=comp.to_file AND obj.path_id=comp.to_path_id
      WHERE comp.relation='gameObject.component'
        AND comp.from_file=goRel.to_file
        AND comp.from_path_id=goRel.to_path_id
        AND obj.type='Animator'
    ) THEN 1 ELSE 0 END AS has_animator,
    CASE WHEN EXISTS (
      SELECT 1
      FROM source_relations comp
      JOIN source_objects obj ON obj.serialized_file=comp.to_file AND obj.path_id=comp.to_path_id
      WHERE comp.relation='gameObject.component'
        AND comp.from_file=goRel.to_file
        AND comp.from_path_id=goRel.to_path_id
        AND obj.type IN ('MeshRenderer','SkinnedMeshRenderer')
    ) THEN 1 ELSE 0 END AS has_direct_visible_renderer
  FROM classified
  LEFT JOIN source_relations goRel ON goRel.relation='component.gameObject' AND goRel.from_file=classified.from_file AND goRel.from_path_id=classified.mb_path_id
  LEFT JOIN source_objects go ON go.serialized_file=goRel.to_file AND go.path_id=goRel.to_path_id AND go.type='GameObject'
),
ranked AS (
  SELECT *,
         ROW_NUMBER() OVER (
           PARTITION BY clip_domain
           ORDER BY has_direct_visible_renderer DESC, has_animator DESC, go_name, clip_name, mb_path_id
         ) AS rn
  FROM direct_context
  WHERE clip_domain IN ('Character','MonsterOrNpc','HumanNameToken')
)
SELECT clip_domain,
       go_name,
       clip_name,
       go_source_path,
       go_file,
       go_path_id,
       mb_path_id,
       clip_file,
       clip_path_id,
       has_animator,
       has_direct_visible_renderer
FROM ranked
WHERE rn <= 4
ORDER BY CASE clip_domain WHEN 'Character' THEN 1 WHEN 'MonsterOrNpc' THEN 2 WHEN 'HumanNameToken' THEN 3 ELSE 9 END, rn;
"@

    $shortlistRowsText = @(& $sqlite3.Source -readonly -batch -tabs $SourceIndexPath $shortlistSql)
    if ($LASTEXITCODE -ne 0) {
        throw "sqlite3 failed while reading SimpleAnimation probe shortlist from source index."
    }

    $probeShortlist = @()
    foreach ($row in $shortlistRowsText) {
        if ([string]::IsNullOrWhiteSpace($row)) {
            continue
        }

        $parts = ([string]$row).Split("`t")
        if ($parts.Count -lt 11) {
            continue
        }

        $probeShortlist += [pscustomobject]@{
            domain = [string]$parts[0]
            gameObjectName = [string]$parts[1]
            clipName = [string]$parts[2]
            sourcePath = [string]$parts[3]
            gameObjectFile = [string]$parts[4]
            gameObjectPathId = ConvertTo-SmokeInt64 $parts[5]
            monoBehaviourPathId = ConvertTo-SmokeInt64 $parts[6]
            clipFile = [string]$parts[7]
            clipPathId = ConvertTo-SmokeInt64 $parts[8]
            hasAnimator = (ConvertTo-SmokeInt64 $parts[9]) -ne 0
            hasDirectVisibleRenderer = (ConvertTo-SmokeInt64 $parts[10]) -ne 0
            diagnosticOnly = $true
            notDefaultModelAnimationRelation = $true
        }
    }

    return [pscustomobject]@{
        status = "ok"
        rule = "SimpleAnimation -> AnimationClip PPtr rows are Naraka script-field evidence only. Character/HumanNameToken/MonsterOrNpc domains and probeShortlist help pick future solver samples, but they do not create default model-animation candidates without script semantics, deterministic model relation, TRS export and visual review."
        totalRelations = $totalRelations
        distinctClipBuckets = $totalDistinctClipBuckets
        domainCounts = $domainRows
        probeShortlist = $probeShortlist
        probeShortlistCount = $probeShortlist.Count
        productionReadiness = "blocked"
        blockedProductionRequirements = @("scriptSemantics", "deterministicModelRelation", "validatedModelGltf", "animationTrsExport", "visualReview")
    }
}

function Test-AssetLibraryV1Contract {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ManifestPath,
        [Parameter(Mandatory = $true)]
        [string]$DbPath,
        [Parameter(Mandatory = $true)]
        [object]$AssetLibrary,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedGame
    )

    $manifestDirectory = Split-Path -Parent $ManifestPath
    if ([int]$AssetLibrary.schemaVersion -ne 1) {
        throw "asset_library.json schemaVersion must be 1. Actual=$($AssetLibrary.schemaVersion)"
    }
    if ([string]$AssetLibrary.libraryKind -ne "AssetLibrary") {
        throw "asset_library.json libraryKind must be AssetLibrary. Actual=$($AssetLibrary.libraryKind)"
    }
    if ([string]$AssetLibrary.sourceTool -ne "AnimeStudio") {
        throw "asset_library.json sourceTool must be AnimeStudio. Actual=$($AssetLibrary.sourceTool)"
    }
    if ([string]$AssetLibrary.sourceGame -ne $ExpectedGame) {
        throw "asset_library.json sourceGame must be $ExpectedGame. Actual=$($AssetLibrary.sourceGame)"
    }
    if ([string]::IsNullOrWhiteSpace([string]$AssetLibrary.index)) {
        throw "asset_library.json index must be library_index.db. Actual=<empty>"
    }
    if ([string]$AssetLibrary.index -ne "library_index.db") {
        throw "asset_library.json index must be library_index.db. Actual=$($AssetLibrary.index)"
    }
    $expectedIndexPath = Join-Path $manifestDirectory ([string]$AssetLibrary.index)
    if ((Resolve-Path -LiteralPath $expectedIndexPath).Path -ne (Resolve-Path -LiteralPath $DbPath).Path) {
        throw "asset_library.json index does not point to the smoke library_index.db. Index=$expectedIndexPath Db=$DbPath"
    }
    if ($AssetLibrary.capabilities.models -ne $true) {
        throw "asset_library.json capabilities.models must be true for Naraka model smoke."
    }
    if ($AssetLibrary.capabilities.animations -ne $false) {
        throw "asset_library.json capabilities.animations must stay false until production model-animation relations are verified."
    }

    $requiredTables = @(
        "metadata",
        "assets",
        "model_validation",
        "texture_links",
        "material_sidecars",
        "library_reports",
        "model_animation_relations",
        "relation_animations"
    )
    $sqliteStatus = "toolMissing"
    $missingTables = @()
    $animationSupportStatus = "toolMissing"
    $animationSupportProductionReady = $null
    $animationSupportDefaultCandidateCount = $null
    $modelAnimationRelationRows = $null
    $relationAnimationRows = $null
    $usableRelationAnimationRows = $null
    $sqlite3 = Get-Command "sqlite3.exe" -ErrorAction SilentlyContinue
    if ($null -ne $sqlite3) {
        $tableRows = & $sqlite3.Source -readonly -batch -noheader $DbPath "SELECT name FROM sqlite_master WHERE type='table';"
        if ($LASTEXITCODE -ne 0) {
            throw "sqlite3 failed while checking AssetLibrary v1 required tables."
        }
        $tableSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($table in @($tableRows)) {
            if (![string]::IsNullOrWhiteSpace($table)) {
                [void]$tableSet.Add([string]$table)
            }
        }
        foreach ($requiredTable in $requiredTables) {
            if (!$tableSet.Contains($requiredTable)) {
                $missingTables += $requiredTable
            }
        }
        if ($missingTables.Count -gt 0) {
            throw "AssetLibrary v1 SQLite index is missing required table(s): $($missingTables -join ', ')"
        }
        $sqliteStatus = "ok"

        $animationSupportRaw = & $sqlite3.Source -readonly -batch -noheader $DbPath "SELECT value FROM metadata WHERE key='animationSupport' LIMIT 1;"
        if ($LASTEXITCODE -ne 0) {
            throw "sqlite3 failed while checking AssetLibrary animationSupport metadata."
        }
        if ([string]::IsNullOrWhiteSpace([string]$animationSupportRaw)) {
            throw "AssetLibrary SQLite metadata missing animationSupport."
        }
        $animationSupportJson = ([string]$animationSupportRaw) | ConvertFrom-Json
        $animationSupportStatus = [string]$animationSupportJson.status
        $animationSupportProductionReady = [bool]$animationSupportJson.productionReady
        $animationSupportDefaultCandidateCount = [long]$animationSupportJson.defaultModelAnimationCandidateCount
        if ($animationSupportProductionReady -ne $false -or $animationSupportDefaultCandidateCount -ne 0) {
            throw "AssetLibrary animationSupport metadata must stay non-production for Naraka smoke. productionReady=$animationSupportProductionReady candidates=$animationSupportDefaultCandidateCount"
        }

        $modelAnimationRelationRowsText = & $sqlite3.Source -readonly -batch -noheader $DbPath "SELECT COUNT(*) FROM model_animation_relations;"
        if ($LASTEXITCODE -ne 0) {
            throw "sqlite3 failed while counting AssetLibrary model_animation_relations."
        }
        $relationAnimationRowsText = & $sqlite3.Source -readonly -batch -noheader $DbPath "SELECT COUNT(*) FROM relation_animations;"
        if ($LASTEXITCODE -ne 0) {
            throw "sqlite3 failed while counting AssetLibrary relation_animations."
        }
        $usableRelationAnimationRowsText = & $sqlite3.Source -readonly -batch -noheader $DbPath "SELECT COUNT(*) FROM relation_animations WHERE is_usable_candidate=1;"
        if ($LASTEXITCODE -ne 0) {
            throw "sqlite3 failed while counting AssetLibrary usable relation_animations."
        }
        $modelAnimationRelationRows = [long]([string]$modelAnimationRelationRowsText).Trim()
        $relationAnimationRows = [long]([string]$relationAnimationRowsText).Trim()
        $usableRelationAnimationRows = [long]([string]$usableRelationAnimationRowsText).Trim()
        if ($AssetLibrary.capabilities.animations -ne $true -and $usableRelationAnimationRows -ne 0) {
            throw "AssetLibrary relation_animations has usable candidates while capabilities.animations=false. usableRelationAnimations=$usableRelationAnimationRows"
        }
    }
    else {
        Write-Warning "sqlite3.exe not found; AssetLibrary v1 required table check skipped."
    }

    return [pscustomobject]@{
        status = "ok"
        schemaVersion = [int]$AssetLibrary.schemaVersion
        libraryKind = [string]$AssetLibrary.libraryKind
        libraryName = [string]$AssetLibrary.libraryName
        sourceTool = [string]$AssetLibrary.sourceTool
        sourceGame = [string]$AssetLibrary.sourceGame
        index = [string]$AssetLibrary.index
        capabilitiesModels = [bool]$AssetLibrary.capabilities.models
        capabilitiesAnimations = [bool]$AssetLibrary.capabilities.animations
        sqliteTableCheck = $sqliteStatus
        animationSupportStatus = $animationSupportStatus
        animationSupportProductionReady = $animationSupportProductionReady
        animationSupportDefaultCandidateCount = $animationSupportDefaultCandidateCount
        modelAnimationRelationRows = $modelAnimationRelationRows
        relationAnimationRows = $relationAnimationRows
        usableRelationAnimationRows = $usableRelationAnimationRows
        requiredTables = $requiredTables
        missingTables = $missingTables
    }
}

function Test-ModelReportEntrypoints {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LibraryRoot,
        [Parameter(Mandatory = $true)]
        [string]$AssetCatalogPath
    )

    $libraryReadmePath = Join-Path $LibraryRoot "LIBRARY_README.md"
    Test-FileRequired -Path $libraryReadmePath -Label "LIBRARY_README.md"
    Test-FileRequired -Path (Join-Path $LibraryRoot "SQLITE_INDEX_README.md") -Label "SQLITE_INDEX_README.md"
    Test-FileRequired -Path $AssetCatalogPath -Label "asset_catalog.jsonl"
    $libraryReadmeText = Get-Content -LiteralPath $libraryReadmePath -Raw -Encoding UTF8
    if ($libraryReadmeText -notmatch "## 动画支持状态") {
        throw "LIBRARY_README.md missing animation support status section."
    }

    $modelRows = @()
    foreach ($catalogLine in Get-Content -LiteralPath $AssetCatalogPath -Encoding UTF8) {
        if ([string]::IsNullOrWhiteSpace($catalogLine)) {
            continue
        }

        $catalogRow = $catalogLine | ConvertFrom-Json
        if ([string]$catalogRow.kind -eq "Model") {
            $modelRows += $catalogRow
        }
    }

    if ($modelRows.Count -lt 1) {
        throw "asset_catalog.jsonl did not contain any Model row for report entrypoint checks."
    }

    $missingAssetReadmes = @()
    $missingMaterialReports = @()
    $reportSamples = @()
    foreach ($modelRow in $modelRows) {
        $modelOutput = [string]$modelRow.output
        if ([string]::IsNullOrWhiteSpace($modelOutput)) {
            throw "Model catalog row has empty output: $($modelRow.name)"
        }

        $normalizedOutput = $modelOutput.Replace("/", [System.IO.Path]::DirectorySeparatorChar)
        $modelPath = Join-Path $LibraryRoot $normalizedOutput
        $modelDirectory = Split-Path -Parent $modelPath
        $assetReadme = Join-Path $modelDirectory "ASSET_README.md"
        $materialReport = Join-Path $modelDirectory "MATERIAL_REPORT.md"

        $assetReadmeOk = (Test-Path -LiteralPath $assetReadme) -and ((Get-Item -LiteralPath $assetReadme).Length -gt 0)
        $materialReportOk = (Test-Path -LiteralPath $materialReport) -and ((Get-Item -LiteralPath $materialReport).Length -gt 0)
        if (!$assetReadmeOk) {
            $missingAssetReadmes += [string]$modelRow.name
        }
        if (!$materialReportOk) {
            $missingMaterialReports += [string]$modelRow.name
        }

        if ($reportSamples.Count -lt 5) {
            $reportSamples += [pscustomobject]@{
                name = [string]$modelRow.name
                assetReadme = $assetReadme.Substring($LibraryRoot.Length + 1)
                materialReport = $materialReport.Substring($LibraryRoot.Length + 1)
            }
        }
    }

    if ($missingAssetReadmes.Count -gt 0 -or $missingMaterialReports.Count -gt 0) {
        throw "Model report entrypoints are incomplete. missing ASSET_README=$($missingAssetReadmes -join ', ') missing MATERIAL_REPORT=$($missingMaterialReports -join ', ')"
    }

    return [pscustomobject]@{
        status = "ok"
        modelCount = [int]$modelRows.Count
        assetReadmeCount = [int]$modelRows.Count
        materialReportCount = [int]$modelRows.Count
        rootReadme = "LIBRARY_README.md"
        rootReadmeAnimationStatus = "ok"
        sqliteReadme = "SQLITE_INDEX_README.md"
        missingAssetReadmes = $missingAssetReadmes
        missingMaterialReports = $missingMaterialReports
        samples = $reportSamples
    }
}

function Test-LibraryCoreArtifacts {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LibraryRoot
    )

    $requiredFiles = @(
        [pscustomobject]@{ path = "asset_catalog.jsonl"; allowEmpty = $false; kind = "jsonl" },
        [pscustomobject]@{ path = "model_animations.json"; allowEmpty = $false; kind = "json" },
        [pscustomobject]@{ path = "model_animations.compact.json"; allowEmpty = $false; kind = "json" },
        [pscustomobject]@{ path = "animation_bindings.jsonl"; allowEmpty = $true; kind = "jsonl" },
        [pscustomobject]@{ path = "unity_relations.jsonl"; allowEmpty = $true; kind = "jsonl" },
        [pscustomobject]@{ path = "unity_relation_summary.json"; allowEmpty = $false; kind = "json" },
        [pscustomobject]@{ path = "export_manifest.jsonl"; allowEmpty = $false; kind = "jsonl" },
        [pscustomobject]@{ path = "model_validation.json"; allowEmpty = $false; kind = "json" },
        [pscustomobject]@{ path = "sqlite_index_summary.json"; allowEmpty = $false; kind = "json" },
        [pscustomobject]@{ path = "profile_summary.json"; allowEmpty = $false; kind = "json" },
        [pscustomobject]@{ path = "source_index_usage.json"; allowEmpty = $false; kind = "json" },
        [pscustomobject]@{ path = "skeletons.json"; allowEmpty = $false; kind = "json" },
        [pscustomobject]@{ path = "character_assemblies.json"; allowEmpty = $false; kind = "json" },
        [pscustomobject]@{ path = "asset_summary.json"; allowEmpty = $false; kind = "json" }
    )

    $missing = @()
    $empty = @()
    $invalidJson = @()
    $rows = @()
    foreach ($requiredFile in $requiredFiles) {
        $path = Join-Path $LibraryRoot ([string]$requiredFile.path)
        if (!(Test-Path -LiteralPath $path)) {
            $missing += [string]$requiredFile.path
            continue
        }

        $item = Get-Item -LiteralPath $path
        if (!$requiredFile.allowEmpty -and $item.Length -le 0) {
            $empty += [string]$requiredFile.path
        }

        $lineCount = $null
        if ([string]$requiredFile.kind -eq "json") {
            try {
                $null = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
            }
            catch {
                $invalidJson += [string]$requiredFile.path
            }
        }
        else {
            $lineCount = (Get-Content -LiteralPath $path -Encoding UTF8 | Measure-Object -Line).Lines
        }

        $rows += [pscustomobject]@{
            path = [string]$requiredFile.path
            length = [long]$item.Length
            lineCount = $lineCount
            allowEmpty = [bool]$requiredFile.allowEmpty
        }
    }

    if ($missing.Count -gt 0 -or $empty.Count -gt 0 -or $invalidJson.Count -gt 0) {
        throw "Library core artifact contract failed. missing=$($missing -join ', ') empty=$($empty -join ', ') invalidJson=$($invalidJson -join ', ')"
    }

    return [pscustomobject]@{
        status = "ok"
        fileCount = [int]$requiredFiles.Count
        missing = $missing
        empty = $empty
        invalidJson = $invalidJson
        files = $rows
    }
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
$sourceModelJiantianshiOutput = Join-Path $OutputRoot "SourceModelAnimation_Jiantianshi_FormalModelDiagnostic"
$sourceModelZhumuOutput = Join-Path $OutputRoot "SourceModelAnimation_ZhumuSoul_ScriptAvatarDiagnostic"
$avatarTosDijiangOutput = Join-Path $OutputRoot "SourceModelAnimation_Dijiang_AvatarTosDiagnostic"
$avatarCompatibilitySamuraiOutput = Join-Path $OutputRoot "SourceModelAnimation_SamuraiGhost_AvatarCompatibilityDiagnostic"
$representativeGltfValidationStatus = if ($SkipGltfValidation) { "skipped" } else { "toolMissing" }
$animationGltfValidationStatus = if ($SkipGltfValidation -or $SkipAnimationDiagnostic) { "skipped" } else { "toolMissing" }
$shaderBoundaryGltfValidationStatus = if ($SkipGltfValidation) { "skipped" } else { "toolMissing" }
$shaderBoundaryStatus = "notChecked"
$shaderBoundaryRule = "Naraka private/layered shader boundary: raw texture slots are preserved; glTF PBR is a degraded preview, not original game shading, and must not be treated as texture loss."
$shaderBoundarySummaryJson = $null
$shaderBoundaryMaterialSidecarRows = $null
$shaderBoundaryCustomShaderRows = $null
$shaderBoundaryCustomShaderEvidenceRows = $null
$shaderBoundaryMissingShaderNameRows = $null
$shaderBoundaryShaderReferenceRows = $null
$shaderBoundaryShaderReferenceMode = $null
$shaderBoundaryCustomShaderEvidenceSamples = $null
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
$zhumuScriptAnimationStatus = "notChecked"
$zhumuScriptAnimationRule = "Zhumu soul is a script-animation boundary probe: the attack prefab model must stay statically usable, while SimpleAnimation and Avatar overlap evidence remain diagnostic-only and create no default animation relation."
$zhumuScriptAnimationSummaryJson = $null
$zhumuScriptAnimationValidationJson = $null
$zhumuScriptAnimationGltfValidationStatus = if ($SkipGltfValidation) { "skipped" } else { "toolMissing" }
$zhumuScriptAnimationGltf = $null
$zhumuScriptAnimationTextureLinkErrors = $null
$zhumuScriptAnimationMaterialSidecarRows = $null
$zhumuScriptAnimationModelRows = $null
$zhumuScriptAnimationModelAnimationCandidateRows = $null
$zhumuScriptAnimationModelAnimationRelationRows = $null
$zhumuScriptAnimationRelationAnimationRows = $null
$zhumuScriptAnimationMaxBoundsSize = $null
$zhumuScriptAnimationSkinJointCount = $null
$zhumuMergedBlockedProductionRequirements = @(
    "explicitModelAnimationRelation",
    "scriptSemanticsOrControllerContext",
    "productionHumanoidSolverValidation",
    "subjectBoneCoverage",
    "visualReview"
)
$zhumuMergedAnimationPreview = [pscustomobject]@{
    status = "notChecked"
    rule = "Zhumu merged animation preview is a read-only diagnostic gate. It proves model+single-animation glTF composition can open and render, but must stay needs_review and must not enable production animation capability."
    productionReadiness = "blocked"
    blockedProductionRequirements = $zhumuMergedBlockedProductionRequirements
    sampleRoot = $ZhumuMergedAnimationProbeRoot
    report = $null
    gltf = $null
    gltfValidation = if ($SkipGltfValidation) { "skipped" } else { "toolMissing" }
    animationCountAdded = 0
    channelCount = 0
    sourceAssessmentStatus = $null
    reasonCodes = @()
    renderProbeStatus = "notChecked"
    renderFrameCount = 0
    renderImages = @()
    renderSummary = $null
}
$hadiModularBoundary = [pscustomobject]@{
    status = "notChecked"
    rule = "Hadi body is a usable modular body/clothing asset, not a complete character. It must stay marked modular_incomplete and blocked from production animation smoke until deterministic Face/Hair assembly is available."
}
$formalSkinnedRepresentativeBoundary = [pscustomobject]@{
    status = "notChecked"
    rule = "Jiantianshi is the formal actor_visual_part skinned representative. Model, material and skin gates must stay ok, while default animation candidates remain zero until explicit Unity animation relations are proven."
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
    jiantianshiFormal = $null
    fxAttack = $null
}
$sourceIndexSimpleAnimationClipDomains = [pscustomobject]@{
    status = "notChecked"
    rule = "SimpleAnimation clip-domain summary is a source-index script-field diagnostic only. It helps pick future Naraka animation probes, but does not create model-animation bindings."
    totalRelations = 0
    distinctClipBuckets = 0
    domainCounts = @()
}
$sourceModelAvatarDiagnostics = [pscustomobject]@{
    status = "notChecked"
    rule = "Avatar.m_TOS hash coverage and model-avatar structural overlap are source-index diagnostics only. They can guide solver/oracle probes, but must stay defaultCandidateCount=0 until explicit Unity animation context, model validation, TRS export and visual validation are proven."
    dijiangTos = $null
    jiantianshiFormalCompatibility = $null
    samuraiGhostCompatibility = $null
}
$animationDiagnosticStatus = if ($SkipAnimationDiagnostic) { "skipped" } else { "pending" }
$animationDiagnosticBlockedRequirements = @(
    "explicitModelAnimationRelation",
    "validatedModelGltf",
    "productionHumanoidSolverValidation",
    "visualReview"
)
$animationReportJson = $null
$animationGltfPath = $null
$browserValidationStatus = if ($SkipBrowserValidation) { "skipped" } else { "toolMissing" }
$thumbnailStatus = if ($SkipBrowserValidation) { "skipped" } else { "toolMissing" }
$assetLibraryContract = $null

Write-Host ""
Write-Host "== Probe Naraka input =="
& $cli $SourceRoot $probeOutput "--game" "Naraka" "--probe_source_input"
if ($LASTEXITCODE -ne 0) {
    throw "Probe Naraka input failed with exit code $LASTEXITCODE"
}

# 这些样本来自完整源索引的确定性闭包，用来同时覆盖角色部件、正式路径带骨骼模型、武器和普通道具。
Write-Host ""
Write-Host "== Export Naraka representative model smoke =="
& $cli $SourceRoot $libraryOutput "--game" "Naraka" "--mode" "Library" "--group_assets" "ByLibrary" "--profile_3d" "Core" "--model_format" "Gltf" "--texture_mode" "Png" "--animation_package" "Separate" "--fbx_animation" "Skip" "--source_index" $SourceIndex "--source_files" "4\c\4c08b7069a411750" $FormalSkinnedRepresentativeBundle "d\1\d1d0bc7b6c107e00" "0\f\0f2ab2b1ab070ac0" "--path_ids" "-6619473669887381141" $FormalSkinnedRepresentativeRootPathId "3879445205109982761" "3817277305598733592"
if ($LASTEXITCODE -ne 0) {
    throw "Export Naraka representative model smoke failed with exit code $LASTEXITCODE"
}

Write-Host ""
Write-Host "== Rebuild AssetLibrary v1 index =="
& $cli "--build_sqlite_index" $libraryOutput "--source_index" $SourceIndex "--game" "Naraka"
if ($LASTEXITCODE -ne 0) {
    throw "Rebuild AssetLibrary v1 index failed with exit code $LASTEXITCODE"
}

$manifest = Join-Path $libraryOutput "asset_library.json"
$db = Join-Path $libraryOutput "library_index.db"
$sqliteSummary = Join-Path $libraryOutput "sqlite_index_summary.json"
$validation = Join-Path $libraryOutput "model_validation.json"
$assetCatalog = Join-Path $libraryOutput "asset_catalog.jsonl"
$modelAnimationsCompact = Join-Path $libraryOutput "model_animations.compact.json"
$gltf = Join-Path $libraryOutput "Models\assets\res\prefab\actor_visual_part\ch_m_hadi\ch_m_hadi_lv_s9\ch_m_hadi_lv_s9.gltf"
$jiantianshiGltf = Join-Path $libraryOutput "Models\assets\res\prefab\actor_visual_part\ch_f_jiantianshi\ch_f_jiantianshi_lv_s1\ch_f_jiantianshi_lv_s1.gltf"
$bowGltf = Join-Path $libraryOutput "Models\assets\res\prefab\drop_item_generate\weapon\weapon_drop_bow_dongjun\weapon_drop_bow_dongjun.gltf"
$deviceGltf = Join-Path $libraryOutput "Models\assets\res\prefab\device_generate\device_hongbao_02\device_hongbao_02.gltf"
$representativeGltfs = @($gltf, $jiantianshiGltf, $bowGltf, $deviceGltf)
$expectedThumbnailCount = [int]$representativeGltfs.Count

Test-FileRequired -Path $manifest -Label "asset_library.json"
Test-FileRequired -Path $db -Label "library_index.db"
Test-FileRequired -Path $sqliteSummary -Label "sqlite_index_summary.json"
Test-FileRequired -Path $validation -Label "model_validation.json"
Test-FileRequired -Path $assetCatalog -Label "asset_catalog.jsonl"
Test-FileRequired -Path $modelAnimationsCompact -Label "model_animations.compact.json"
Test-FileRequired -Path $gltf -Label "Hadi glTF"
Test-FileRequired -Path $jiantianshiGltf -Label "Jiantianshi glTF"
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
    $animationDiagnosticActualBlockedRequirements = @($animationDiagnosticBlockedRequirements | ForEach-Object { [string]$_ })
    foreach ($requiredRequirement in @("explicitModelAnimationRelation", "validatedModelGltf", "productionHumanoidSolverValidation", "visualReview")) {
        if ($animationDiagnosticActualBlockedRequirements -notcontains $requiredRequirement) {
            throw "Animation diagnostic lost production blocked requirement: $requiredRequirement"
        }
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
Invoke-Checked -Label "List Jiantianshi formal source-model animations" -FilePath $cli -Arguments @(
    "--list_source_model_animations", $SourceIndex,
    "--preview_model", "ch_f_jiantianshi_lv_s1",
    "--preview_output", $sourceModelJiantianshiOutput,
    "--source_candidate_limit", "40")
Invoke-Checked -Label "List Zhumu soul source-model animations" -FilePath $cli -Arguments @(
    "--list_source_model_animations", $SourceIndex,
    "--preview_model", "mo_pve_b_zhumu_soul_01",
    "--preview_output", $sourceModelZhumuOutput,
    "--source_candidate_limit", "40")
Invoke-Checked -Label "List fxattack script source-model animations" -FilePath $cli -Arguments @(
    "--list_source_model_animations", $SourceIndex,
    "--preview_model", "fxattack_male_sw_attack_heavy_02",
    "--preview_output", $scriptAnimationFxOutput,
    "--source_candidate_limit", "20")

$hadiScriptAnimationDiagnostic = Read-SourceModelScriptAnimationDiagnostic -Selector "ch_m_hadi_lv_s9" -OutputRoot $scriptAnimationHadiOutput
$jiantianshiScriptAnimationDiagnostic = Read-SourceModelScriptAnimationDiagnostic -Selector "ch_f_jiantianshi_lv_s1" -OutputRoot $sourceModelJiantianshiOutput
$zhumuScriptAnimationDiagnostic = Read-SourceModelScriptAnimationDiagnostic -Selector "mo_pve_b_zhumu_soul_01" -OutputRoot $sourceModelZhumuOutput
$fxScriptAnimationDiagnostic = Read-SourceModelScriptAnimationDiagnostic -Selector "fxattack_male_sw_attack_heavy_02" -OutputRoot $scriptAnimationFxOutput
$jiantianshiAvatarCompatibilityDiagnostic = Read-SourceModelAvatarAnimationDiagnostic -Selector "ch_f_jiantianshi_lv_s1" -OutputRoot $sourceModelJiantianshiOutput
$zhumuAvatarCompatibilityDiagnostic = Read-SourceModelAvatarAnimationDiagnostic -Selector "mo_pve_b_zhumu_soul_01" -OutputRoot $sourceModelZhumuOutput

if ($hadiScriptAnimationDiagnostic.selectedModelCount -lt 1) {
    throw "Hadi body script animation diagnostic did not select any source model."
}
if ($hadiScriptAnimationDiagnostic.candidateCount -ne 0 -or $hadiScriptAnimationDiagnostic.scriptAnimationRows -ne 0) {
    throw "Hadi body unexpectedly produced default or script animation rows. candidates=$($hadiScriptAnimationDiagnostic.candidateCount) scriptRows=$($hadiScriptAnimationDiagnostic.scriptAnimationRows)"
}
if ($jiantianshiScriptAnimationDiagnostic.selectedModelCount -lt 1) {
    throw "Jiantianshi formal source-model diagnostic did not select any source model."
}
if ($jiantianshiScriptAnimationDiagnostic.candidateCount -ne 0 -or $jiantianshiScriptAnimationDiagnostic.scriptAnimationRows -ne 0 -or $jiantianshiScriptAnimationDiagnostic.invalidBoundaryRows -ne 0) {
    throw "Jiantianshi formal source-model diagnostic must stay without default/script animation rows. candidates=$($jiantianshiScriptAnimationDiagnostic.candidateCount) scriptRows=$($jiantianshiScriptAnimationDiagnostic.scriptAnimationRows) invalidRows=$($jiantianshiScriptAnimationDiagnostic.invalidBoundaryRows)"
}
if ($jiantianshiAvatarCompatibilityDiagnostic.candidateCount -ne 0 -or $jiantianshiAvatarCompatibilityDiagnostic.avatarTosDefaultCandidateCount -ne 0 -or $jiantianshiAvatarCompatibilityDiagnostic.modelAvatarDefaultCandidateCount -ne 0) {
    throw "Jiantianshi formal Avatar compatibility diagnostic unexpectedly produced default candidates. candidates=$($jiantianshiAvatarCompatibilityDiagnostic.candidateCount) avatarTosDefault=$($jiantianshiAvatarCompatibilityDiagnostic.avatarTosDefaultCandidateCount) modelAvatarDefault=$($jiantianshiAvatarCompatibilityDiagnostic.modelAvatarDefaultCandidateCount)"
}
if ($jiantianshiAvatarCompatibilityDiagnostic.invalidBoundaryRows -ne 0) {
    throw "Jiantianshi formal Avatar compatibility diagnostic lost diagnostic-only boundary. invalidRows=$($jiantianshiAvatarCompatibilityDiagnostic.invalidBoundaryRows)"
}
if ($jiantianshiAvatarCompatibilityDiagnostic.modelAvatarRows -lt 1 -or $jiantianshiAvatarCompatibilityDiagnostic.modelAvatarHighOverlapRows -lt 1 -or $jiantianshiAvatarCompatibilityDiagnostic.modelAvatarMaxCoverage -lt 0.9) {
    throw "Jiantianshi formal Avatar compatibility diagnostic lost structural overlap evidence. rows=$($jiantianshiAvatarCompatibilityDiagnostic.modelAvatarRows) highOverlapRows=$($jiantianshiAvatarCompatibilityDiagnostic.modelAvatarHighOverlapRows) maxCoverage=$($jiantianshiAvatarCompatibilityDiagnostic.modelAvatarMaxCoverage)"
}
if ($zhumuScriptAnimationDiagnostic.selectedModelCount -lt 1) {
    throw "Zhumu soul source-model diagnostic did not select any source model."
}
if ($zhumuScriptAnimationDiagnostic.candidateCount -ne 0 -or $zhumuScriptAnimationDiagnostic.scriptAnimationRows -lt 1 -or $zhumuScriptAnimationDiagnostic.invalidBoundaryRows -ne 0) {
    throw "Zhumu soul script diagnostic must stay diagnostic-only without default candidates. candidates=$($zhumuScriptAnimationDiagnostic.candidateCount) scriptRows=$($zhumuScriptAnimationDiagnostic.scriptAnimationRows) invalidRows=$($zhumuScriptAnimationDiagnostic.invalidBoundaryRows)"
}
if ($zhumuScriptAnimationDiagnostic.subtreeVisibleRendererRows -lt 1 -or $zhumuScriptAnimationDiagnostic.subtreeSkinnedRendererRows -lt 1 -or $zhumuScriptAnimationDiagnostic.animatorRows -lt 1) {
    throw "Zhumu soul script diagnostic lost visible subtree, skinned renderer, or Animator context. subtreeVisibleRows=$($zhumuScriptAnimationDiagnostic.subtreeVisibleRendererRows) subtreeSkinnedRows=$($zhumuScriptAnimationDiagnostic.subtreeSkinnedRendererRows) animatorRows=$($zhumuScriptAnimationDiagnostic.animatorRows)"
}
if ($zhumuAvatarCompatibilityDiagnostic.candidateCount -ne 0 -or $zhumuAvatarCompatibilityDiagnostic.avatarTosDefaultCandidateCount -ne 0 -or $zhumuAvatarCompatibilityDiagnostic.modelAvatarDefaultCandidateCount -ne 0) {
    throw "Zhumu soul Avatar compatibility diagnostic unexpectedly produced default candidates. candidates=$($zhumuAvatarCompatibilityDiagnostic.candidateCount) avatarTosDefault=$($zhumuAvatarCompatibilityDiagnostic.avatarTosDefaultCandidateCount) modelAvatarDefault=$($zhumuAvatarCompatibilityDiagnostic.modelAvatarDefaultCandidateCount)"
}
if ($zhumuAvatarCompatibilityDiagnostic.invalidBoundaryRows -ne 0) {
    throw "Zhumu soul Avatar compatibility diagnostic lost diagnostic-only boundary. invalidRows=$($zhumuAvatarCompatibilityDiagnostic.invalidBoundaryRows)"
}
if ($zhumuAvatarCompatibilityDiagnostic.modelAvatarRows -lt 1 -or $zhumuAvatarCompatibilityDiagnostic.modelAvatarHighOverlapRows -lt 1 -or $zhumuAvatarCompatibilityDiagnostic.modelAvatarMaxCoverage -lt 0.9) {
    throw "Zhumu soul Avatar compatibility diagnostic lost structural overlap evidence. rows=$($zhumuAvatarCompatibilityDiagnostic.modelAvatarRows) highOverlapRows=$($zhumuAvatarCompatibilityDiagnostic.modelAvatarHighOverlapRows) maxCoverage=$($zhumuAvatarCompatibilityDiagnostic.modelAvatarMaxCoverage)"
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
if ($fxScriptAnimationDiagnostic.subtreeTruncatedRows -ne 0) {
    throw "FxAttack bounded script-animation subtree diagnostic was truncated. truncatedRows=$($fxScriptAnimationDiagnostic.subtreeTruncatedRows)"
}

$scriptAnimationComponentDiagnostics = [pscustomobject]@{
    status = "ok"
    rule = $scriptAnimationComponentDiagnostics.rule
    hadiBody = $hadiScriptAnimationDiagnostic
    jiantianshiFormal = $jiantianshiScriptAnimationDiagnostic
    zhumuSoul = $zhumuScriptAnimationDiagnostic
    fxAttack = $fxScriptAnimationDiagnostic
}
$sourceIndexSimpleAnimationClipDomains = Read-SourceIndexSimpleAnimationClipDomains -SourceIndexPath $SourceIndex
if ($sourceIndexSimpleAnimationClipDomains.status -eq "ok" -and $sourceIndexSimpleAnimationClipDomains.totalRelations -lt 1) {
    throw "SimpleAnimation clip-domain diagnostic found no AnimationClip PPtr rows."
}
if ($sourceIndexSimpleAnimationClipDomains.status -eq "ok" -and $sourceIndexSimpleAnimationClipDomains.probeShortlistCount -lt 3) {
    throw "SimpleAnimation probe shortlist lost character/monster/human-token samples. count=$($sourceIndexSimpleAnimationClipDomains.probeShortlistCount)"
}

Write-Host ""
Write-Host "== List Naraka Avatar/TOS animation diagnostics =="
$dijiangAvatarDiagnostic = $null
if (!$SkipAnimationDiagnostic) {
    Invoke-Checked -Label "List Dijiang Avatar TOS source-model animations" -FilePath $cli -Arguments @(
        "--list_source_model_animations", $AnimationSourceIndex,
        "--preview_model", $AnimationPreviewModel,
        "--preview_animation", $AnimationPreviewClip,
        "--preview_output", $avatarTosDijiangOutput,
        "--source_candidate_limit", "30")
    $dijiangAvatarDiagnostic = Read-SourceModelAvatarAnimationDiagnostic -Selector $AnimationPreviewModel -OutputRoot $avatarTosDijiangOutput
    if ($dijiangAvatarDiagnostic.selectedModelCount -lt 1) {
        throw "Dijiang Avatar/TOS diagnostic did not select any source model."
    }
    if ($dijiangAvatarDiagnostic.candidateCount -ne 0 -or $dijiangAvatarDiagnostic.avatarTosDefaultCandidateCount -ne 0 -or $dijiangAvatarDiagnostic.modelAvatarDefaultCandidateCount -ne 0) {
        throw "Dijiang Avatar/TOS diagnostic unexpectedly produced default candidates. candidates=$($dijiangAvatarDiagnostic.candidateCount) avatarTosDefault=$($dijiangAvatarDiagnostic.avatarTosDefaultCandidateCount) modelAvatarDefault=$($dijiangAvatarDiagnostic.modelAvatarDefaultCandidateCount)"
    }
    if ($dijiangAvatarDiagnostic.invalidBoundaryRows -ne 0) {
        throw "Dijiang Avatar/TOS diagnostic lost diagnostic-only boundary. invalidRows=$($dijiangAvatarDiagnostic.invalidBoundaryRows)"
    }
    if ($dijiangAvatarDiagnostic.avatarTosRows -lt 1 -or $dijiangAvatarDiagnostic.avatarTosMaxCoverage -lt 0.99) {
        throw "Dijiang Avatar/TOS diagnostic lost hash coverage evidence. rows=$($dijiangAvatarDiagnostic.avatarTosRows) maxCoverage=$($dijiangAvatarDiagnostic.avatarTosMaxCoverage)"
    }
}

$samuraiAvatarDiagnosticSelector = "^" + [Regex]::Escape($CharacterCandidateSourceIndexAnimatorName) + "$"
Invoke-Checked -Label "List SamuraiGhost Avatar compatibility source-model animations" -FilePath $cli -Arguments @(
    "--list_source_model_animations", $SourceIndex,
    "--preview_model", $samuraiAvatarDiagnosticSelector,
    "--preview_output", $avatarCompatibilitySamuraiOutput,
    "--source_candidate_limit", "30")
$samuraiAvatarDiagnostic = Read-SourceModelAvatarAnimationDiagnostic -Selector $CharacterCandidateSourceIndexAnimatorName -OutputRoot $avatarCompatibilitySamuraiOutput
if ($samuraiAvatarDiagnostic.selectedModelCount -lt 1) {
    throw "SamuraiGhost Avatar compatibility diagnostic did not select any source model."
}
if ($samuraiAvatarDiagnostic.candidateCount -ne 0 -or $samuraiAvatarDiagnostic.avatarTosDefaultCandidateCount -ne 0 -or $samuraiAvatarDiagnostic.modelAvatarDefaultCandidateCount -ne 0) {
    throw "SamuraiGhost Avatar compatibility diagnostic unexpectedly produced default candidates. candidates=$($samuraiAvatarDiagnostic.candidateCount) avatarTosDefault=$($samuraiAvatarDiagnostic.avatarTosDefaultCandidateCount) modelAvatarDefault=$($samuraiAvatarDiagnostic.modelAvatarDefaultCandidateCount)"
}
if ($samuraiAvatarDiagnostic.invalidBoundaryRows -ne 0) {
    throw "SamuraiGhost Avatar compatibility diagnostic lost diagnostic-only boundary. invalidRows=$($samuraiAvatarDiagnostic.invalidBoundaryRows)"
}
if ($samuraiAvatarDiagnostic.modelAvatarRows -lt 1 -or $samuraiAvatarDiagnostic.modelAvatarHighOverlapRows -lt 1 -or $samuraiAvatarDiagnostic.modelAvatarMaxCoverage -lt 0.9) {
    throw "SamuraiGhost Avatar compatibility diagnostic lost structural overlap evidence. rows=$($samuraiAvatarDiagnostic.modelAvatarRows) highOverlapRows=$($samuraiAvatarDiagnostic.modelAvatarHighOverlapRows) maxCoverage=$($samuraiAvatarDiagnostic.modelAvatarMaxCoverage)"
}

$sourceModelAvatarDiagnostics = [pscustomobject]@{
    status = "ok"
    rule = $sourceModelAvatarDiagnostics.rule
    dijiangTos = if ($null -ne $dijiangAvatarDiagnostic) { $dijiangAvatarDiagnostic } else { [pscustomobject]@{ status = "skipped"; reason = "SkipAnimationDiagnostic" } }
    jiantianshiFormalCompatibility = $jiantianshiAvatarCompatibilityDiagnostic
    zhumuSoulCompatibility = $zhumuAvatarCompatibilityDiagnostic
    samuraiGhostCompatibility = $samuraiAvatarDiagnostic
}

if (!$SkipBrowserValidation) {
    $browserCli = "D:\misutime\UnrealExporter\dist\AssetLibraryBrowser.Cli\AssetLibraryBrowser.Cli.exe"
    if (Test-Path -LiteralPath $browserCli) {
        Invoke-Checked -Label "Validate AssetLibrary v1" -FilePath $browserCli -Arguments @("validate-library", $libraryOutput)
        $browserValidationStatus = "ok"

        # 代表库当前有 4 个模型，缩略图 smoke 也必须覆盖同一批模型，不能只渲染前三个。
        Invoke-Checked -Label "Render browser thumbnail" -FilePath $browserCli -Arguments @("build-thumbnails", $libraryOutput, "1", ([string]$expectedThumbnailCount))
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
        if ($null -ne $shaderBoundaryQuality.customShaderReferenceSidecars -and [long]$shaderBoundaryQuality.customShaderReferenceSidecars -lt 1) {
            throw "Shader boundary sqlite_index_summary.json did not preserve custom shader reference evidence."
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
            $shaderBoundaryCustomShaderEvidenceRowsSql = @"
SELECT COUNT(*)
FROM material_sidecars
WHERE custom_shader_required = 1
  AND layered_material_unresolved = 1
  AND pbr_preview_status = 'bestEffortDegradedPreview'
  AND COALESCE(key_texture_slots_json, '') <> ''
  AND COALESCE(exported_textures_json, '') <> ''
  AND COALESCE(unresolved_steps_json, '') <> ''
  AND COALESCE(raw_json, '') <> '';
"@
            $shaderBoundaryCustomShaderEvidenceRowsText = & $sqlite3.Source -readonly -batch -noheader $shaderBoundaryDb $shaderBoundaryCustomShaderEvidenceRowsSql
            if ($LASTEXITCODE -ne 0) {
                throw "sqlite3 failed while checking shader boundary custom shader evidence fields."
            }
            $shaderBoundaryMissingShaderNameRowsSql = @"
SELECT COUNT(*)
FROM material_sidecars
WHERE custom_shader_required = 1
  AND layered_material_unresolved = 1
  AND pbr_preview_status = 'bestEffortDegradedPreview'
  AND COALESCE(shader_name, '') = '';
"@
            $shaderBoundaryMissingShaderNameRowsText = & $sqlite3.Source -readonly -batch -noheader $shaderBoundaryDb $shaderBoundaryMissingShaderNameRowsSql
            if ($LASTEXITCODE -ne 0) {
                throw "sqlite3 failed while checking shader boundary shader-name evidence."
            }
            $shaderBoundaryColumnListText = & $sqlite3.Source -readonly -batch -noheader $shaderBoundaryDb "SELECT name FROM pragma_table_info('material_sidecars') ORDER BY cid;"
            if ($LASTEXITCODE -ne 0) {
                throw "sqlite3 failed while checking shader boundary material_sidecars schema."
            }
            $shaderBoundaryColumns = @($shaderBoundaryColumnListText)
            $shaderBoundaryHasStructuredShaderReference = $shaderBoundaryColumns -contains "shader_reference_json"
            if ($shaderBoundaryHasStructuredShaderReference) {
                $shaderBoundaryShaderReferenceMode = "shader_reference_json"
                $shaderBoundaryShaderReferenceRowsSql = @"
SELECT COUNT(*)
FROM material_sidecars
WHERE custom_shader_required = 1
  AND layered_material_unresolved = 1
  AND pbr_preview_status = 'bestEffortDegradedPreview'
  AND COALESCE(shader_reference_json, '') <> '';
"@
            }
            else {
                $shaderBoundaryShaderReferenceMode = "raw_json.unityMaterial.m_Shader"
                $shaderBoundaryShaderReferenceRowsSql = @"
SELECT COUNT(*)
FROM material_sidecars
WHERE custom_shader_required = 1
  AND layered_material_unresolved = 1
  AND pbr_preview_status = 'bestEffortDegradedPreview'
  AND COALESCE(
        json_extract(raw_json, '$.shaderReference.pathId'),
        json_extract(raw_json, '$.unityMaterial.m_Shader.m_PathID'),
        json_extract(raw_json, '$.unityMaterial.unityMaterial.m_Shader.m_PathID')
      ) IS NOT NULL
  AND COALESCE(
        json_extract(raw_json, '$.shaderReference.pathId'),
        json_extract(raw_json, '$.unityMaterial.m_Shader.m_PathID'),
        json_extract(raw_json, '$.unityMaterial.unityMaterial.m_Shader.m_PathID')
      ) <> 0;
"@
            }
            $shaderBoundaryShaderReferenceRowsText = & $sqlite3.Source -readonly -batch -noheader $shaderBoundaryDb $shaderBoundaryShaderReferenceRowsSql
            if ($LASTEXITCODE -ne 0) {
                throw "sqlite3 failed while checking shader boundary shader reference evidence."
            }
            $shaderBoundaryCustomShaderEvidenceSamplesSql = @"
SELECT substr(group_concat(material_name || ': slots=' || key_texture_slots_json || '; steps=' || unresolved_steps_json, ' | '), 1, 700)
FROM (
    SELECT material_name, key_texture_slots_json, unresolved_steps_json
    FROM material_sidecars
    WHERE custom_shader_required = 1
      AND layered_material_unresolved = 1
      AND pbr_preview_status = 'bestEffortDegradedPreview'
    ORDER BY material_name
    LIMIT 4
);
"@
            $shaderBoundaryCustomShaderEvidenceSamplesText = & $sqlite3.Source -readonly -batch -noheader $shaderBoundaryDb $shaderBoundaryCustomShaderEvidenceSamplesSql
            if ($LASTEXITCODE -ne 0) {
                throw "sqlite3 failed while sampling shader boundary custom shader evidence fields."
            }
            $shaderBoundaryMaterialSidecarRows = [long]$shaderBoundaryMaterialSidecarRowsText
            $shaderBoundaryCustomShaderRows = [long]$shaderBoundaryCustomShaderRowsText
            $shaderBoundaryCustomShaderEvidenceRows = [long]$shaderBoundaryCustomShaderEvidenceRowsText
            $shaderBoundaryMissingShaderNameRows = [long]$shaderBoundaryMissingShaderNameRowsText
            $shaderBoundaryShaderReferenceRows = [long]$shaderBoundaryShaderReferenceRowsText
            $shaderBoundaryCustomShaderEvidenceSamples = ($shaderBoundaryCustomShaderEvidenceSamplesText -join [Environment]::NewLine).Trim()
            if ($shaderBoundaryCustomShaderRows -lt 1) {
                throw "Shader boundary material_sidecars table did not preserve degraded custom shader rows."
            }
            if ($shaderBoundaryCustomShaderEvidenceRows -lt 1) {
                throw "Shader boundary material_sidecars table did not preserve custom shader slots, exported textures, unresolved steps and raw sidecar evidence."
            }
            if ($shaderBoundaryShaderReferenceRows -lt 1) {
                throw "Shader boundary material_sidecars table did not preserve Unity shader PPtr reference evidence."
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

if (![string]::IsNullOrWhiteSpace($ZhumuScriptAnimationSampleRoot)) {
    if (Test-Path -LiteralPath $ZhumuScriptAnimationSampleRoot) {
        Write-Host ""
        Write-Host "== Validate Naraka Zhumu script-animation model probe =="

        $zhumuScriptAnimationAssetLibrary = Join-Path $ZhumuScriptAnimationSampleRoot "asset_library.json"
        $zhumuScriptAnimationValidation = Join-Path $ZhumuScriptAnimationSampleRoot "model_validation.json"
        $zhumuScriptAnimationSqliteSummary = Join-Path $ZhumuScriptAnimationSampleRoot "sqlite_index_summary.json"
        $zhumuScriptAnimationDb = Join-Path $ZhumuScriptAnimationSampleRoot "library_index.db"
        $zhumuScriptAnimationGltfs = @(Get-ChildItem -LiteralPath (Join-Path $ZhumuScriptAnimationSampleRoot "Models") -Recurse -Filter "*.gltf" -File)

        Test-FileRequired -Path $zhumuScriptAnimationAssetLibrary -Label "Zhumu script-animation asset_library.json"
        Test-FileRequired -Path $zhumuScriptAnimationValidation -Label "Zhumu script-animation model_validation.json"
        Test-FileRequired -Path $zhumuScriptAnimationSqliteSummary -Label "Zhumu script-animation sqlite_index_summary.json"
        Test-FileRequired -Path $zhumuScriptAnimationDb -Label "Zhumu script-animation library_index.db"
        if ($zhumuScriptAnimationGltfs.Count -lt 1) {
            throw "Zhumu script-animation model probe is missing exported glTF models."
        }
        $zhumuScriptAnimationGltf = $zhumuScriptAnimationGltfs[0].FullName

        $zhumuScriptAnimationValidationJson = Get-Content -LiteralPath $zhumuScriptAnimationValidation -Raw -Encoding UTF8 | ConvertFrom-Json
        $zhumuScriptAnimationSummaryJson = Get-Content -LiteralPath $zhumuScriptAnimationSqliteSummary -Raw -Encoding UTF8 | ConvertFrom-Json
        if ([long]$zhumuScriptAnimationValidationJson.totals.models -lt 1 -or [long]$zhumuScriptAnimationValidationJson.totals.ok -lt 1) {
            throw "Zhumu script-animation model probe has no ok model validation result."
        }
        if ([long]$zhumuScriptAnimationValidationJson.totals.withSkin -lt 1 -or [long]$zhumuScriptAnimationValidationJson.totals.withTextures -lt 1) {
            throw "Zhumu script-animation model probe must keep both skin and texture evidence."
        }
        $zhumuScriptAnimationModel = @($zhumuScriptAnimationValidationJson.models)[0]
        $zhumuScriptAnimationSkinJointCount = [long]$zhumuScriptAnimationModel.Body.SkinJointCount
        if ($zhumuScriptAnimationSkinJointCount -lt 50) {
            throw "Zhumu script-animation model probe has too few skin joints: $zhumuScriptAnimationSkinJointCount"
        }
        if ([long]$zhumuScriptAnimationModel.Body.PrimitivesWithMaterial -lt 1 -or [long]$zhumuScriptAnimationModel.Body.PrimitivesWithBaseColorTexture -lt 1) {
            throw "Zhumu script-animation model probe lost material or base-color texture coverage."
        }
        $zhumuBoundsSize = @($zhumuScriptAnimationModel.Body.Bounds.Size)
        foreach ($sizeValue in $zhumuBoundsSize) {
            $sizeNumber = [double]$sizeValue
            if ($null -eq $zhumuScriptAnimationMaxBoundsSize -or $sizeNumber -gt $zhumuScriptAnimationMaxBoundsSize) {
                $zhumuScriptAnimationMaxBoundsSize = $sizeNumber
            }
        }
        if ($null -eq $zhumuScriptAnimationMaxBoundsSize -or $zhumuScriptAnimationMaxBoundsSize -lt 1.0) {
            throw "Zhumu script-animation model probe bbox is too small: $zhumuScriptAnimationMaxBoundsSize"
        }
        if ([long]$zhumuScriptAnimationSummaryJson.counts.textureAssets -lt 1 -or [long]$zhumuScriptAnimationSummaryJson.counts.materialSidecars -lt 1) {
            throw "Zhumu script-animation model probe lost texture assets or material sidecars."
        }

        $sqlite3 = Get-Command "sqlite3.exe" -ErrorAction SilentlyContinue
        if ($null -ne $sqlite3) {
            $zhumuScriptAnimationTextureLinkErrorsText = & $sqlite3.Source -readonly -batch -noheader $zhumuScriptAnimationDb "SELECT COUNT(*) FROM texture_links WHERE link_error IS NOT NULL AND trim(link_error) <> '';"
            if ($LASTEXITCODE -ne 0) {
                throw "sqlite3 failed while checking Zhumu script-animation texture link errors."
            }
            $zhumuScriptAnimationMaterialSidecarRowsText = & $sqlite3.Source -readonly -batch -noheader $zhumuScriptAnimationDb "SELECT COUNT(*) FROM material_sidecars;"
            if ($LASTEXITCODE -ne 0) {
                throw "sqlite3 failed while counting Zhumu script-animation material_sidecars."
            }
            $zhumuScriptAnimationModelRowsText = & $sqlite3.Source -readonly -batch -noheader $zhumuScriptAnimationDb "SELECT COUNT(*) FROM assets WHERE kind = 'Model';"
            if ($LASTEXITCODE -ne 0) {
                throw "sqlite3 failed while counting Zhumu script-animation model assets."
            }
            $zhumuScriptAnimationModelAnimationCandidateRowsText = & $sqlite3.Source -readonly -batch -noheader $zhumuScriptAnimationDb "SELECT COUNT(*) FROM model_animation_candidates;"
            if ($LASTEXITCODE -ne 0) {
                throw "sqlite3 failed while counting Zhumu script-animation model_animation_candidates."
            }
            $zhumuScriptAnimationModelAnimationRelationRowsText = & $sqlite3.Source -readonly -batch -noheader $zhumuScriptAnimationDb "SELECT COUNT(*) FROM model_animation_relations;"
            if ($LASTEXITCODE -ne 0) {
                throw "sqlite3 failed while counting Zhumu script-animation model_animation_relations."
            }
            $zhumuScriptAnimationRelationAnimationRowsText = & $sqlite3.Source -readonly -batch -noheader $zhumuScriptAnimationDb "SELECT COUNT(*) FROM relation_animations;"
            if ($LASTEXITCODE -ne 0) {
                throw "sqlite3 failed while counting Zhumu script-animation relation_animations."
            }
            $zhumuScriptAnimationTextureLinkErrors = [long]$zhumuScriptAnimationTextureLinkErrorsText
            $zhumuScriptAnimationMaterialSidecarRows = [long]$zhumuScriptAnimationMaterialSidecarRowsText
            $zhumuScriptAnimationModelRows = [long]$zhumuScriptAnimationModelRowsText
            $zhumuScriptAnimationModelAnimationCandidateRows = [long]$zhumuScriptAnimationModelAnimationCandidateRowsText
            $zhumuScriptAnimationModelAnimationRelationRows = [long]$zhumuScriptAnimationModelAnimationRelationRowsText
            $zhumuScriptAnimationRelationAnimationRows = [long]$zhumuScriptAnimationRelationAnimationRowsText
            if ($zhumuScriptAnimationTextureLinkErrors -ne 0) {
                throw "Zhumu script-animation model probe has texture link errors: $zhumuScriptAnimationTextureLinkErrors"
            }
            if ($zhumuScriptAnimationMaterialSidecarRows -lt 1 -or $zhumuScriptAnimationModelRows -lt 1) {
                throw "Zhumu script-animation model probe lost material sidecar or model rows in library_index.db."
            }
            if ($zhumuScriptAnimationModelAnimationCandidateRows -ne 0 -or $zhumuScriptAnimationModelAnimationRelationRows -ne 0 -or $zhumuScriptAnimationRelationAnimationRows -ne 0) {
                throw "Zhumu script-animation model probe unexpectedly created production animation relation rows. candidates=$zhumuScriptAnimationModelAnimationCandidateRows modelRelations=$zhumuScriptAnimationModelAnimationRelationRows relationAnimations=$zhumuScriptAnimationRelationAnimationRows"
            }
        }
        else {
            Write-Warning "sqlite3.exe not found; Zhumu script-animation table checks skipped."
        }

        if (!$SkipGltfValidation) {
            $gltfTransform = Get-Command "gltf-transform.cmd" -ErrorAction SilentlyContinue
            if ($null -ne $gltfTransform) {
                Invoke-Checked -Label "Validate Zhumu script-animation model glTF" -FilePath $gltfTransform.Source -Arguments @("validate", $zhumuScriptAnimationGltf)
                $zhumuScriptAnimationGltfValidationStatus = "ok"
            }
        }

        $zhumuScriptAnimationStatus = "ok"
    }
    else {
        $zhumuScriptAnimationStatus = "missing"
        Write-Warning "Zhumu script-animation model probe not found; skipped: $ZhumuScriptAnimationSampleRoot"
    }
}
else {
    $zhumuScriptAnimationStatus = "skipped"
}

if (![string]::IsNullOrWhiteSpace($ZhumuMergedAnimationProbeRoot)) {
    if (Test-Path -LiteralPath $ZhumuMergedAnimationProbeRoot) {
        Write-Host ""
        Write-Host "== Validate Naraka Zhumu merged animation preview probe =="

        $zhumuMergedReport = Join-Path $ZhumuMergedAnimationProbeRoot "merge_animation_gltf_report.json"
        $zhumuMergedRenderRoot = Join-Path $ZhumuMergedAnimationProbeRoot "RenderProbe"
        $zhumuMergedRenderSummary = Join-Path $zhumuMergedRenderRoot "render_probe_summary.txt"
        Test-FileRequired -Path $zhumuMergedReport -Label "Zhumu merged animation report"
        Test-FileRequired -Path $zhumuMergedRenderSummary -Label "Zhumu merged animation render summary"

        $zhumuMergedReportJson = Get-Content -LiteralPath $zhumuMergedReport -Raw -Encoding UTF8 | ConvertFrom-Json
        $zhumuMergedGltf = [string]$zhumuMergedReportJson.outputGltf
        if ([string]::IsNullOrWhiteSpace($zhumuMergedGltf)) {
            $zhumuMergedGltf = (Get-ChildItem -LiteralPath $ZhumuMergedAnimationProbeRoot -Recurse -Filter "*.merged.gltf" -File | Select-Object -First 1).FullName
        }
        Test-FileRequired -Path $zhumuMergedGltf -Label "Zhumu merged animation glTF"

        if ([string]$zhumuMergedReportJson.status -ne "needs_review") {
            throw "Zhumu merged animation preview must stay needs_review until solver and visual validation are production-ready. status=$($zhumuMergedReportJson.status)"
        }
        $zhumuMergedChannelCount = ConvertTo-SmokeInt64 $zhumuMergedReportJson.channelCount
        $zhumuMergedAnimationCountAdded = ConvertTo-SmokeInt64 $zhumuMergedReportJson.animationCountAdded
        if ($zhumuMergedAnimationCountAdded -lt 1 -or $zhumuMergedChannelCount -lt 1) {
            throw "Zhumu merged animation preview lost animation channels. animationCountAdded=$zhumuMergedAnimationCountAdded channelCount=$zhumuMergedChannelCount"
        }
        $zhumuMergedJointCoverage = $zhumuMergedReportJson.animationJointCoverage
        $zhumuMergedSkinJointCount = ConvertTo-SmokeInt64 $zhumuMergedJointCoverage.skinJointCount
        $zhumuMergedAnimatedSkinJointCount = ConvertTo-SmokeInt64 $zhumuMergedJointCoverage.animatedSkinJointCount
        if ($null -eq $zhumuMergedJointCoverage -or $zhumuMergedSkinJointCount -lt 1 -or $zhumuMergedAnimatedSkinJointCount -lt 1) {
            throw "Zhumu merged animation preview lost skin-joint coverage diagnostic. skinJoints=$zhumuMergedSkinJointCount animatedSkinJoints=$zhumuMergedAnimatedSkinJointCount"
        }
        $zhumuMergedVertexCoverage = $zhumuMergedJointCoverage.vertexWeightCoverage
        $zhumuMergedWeightedVertexCount = ConvertTo-SmokeInt64 $zhumuMergedVertexCoverage.weightedVertexCount
        if ([string]$zhumuMergedVertexCoverage.status -ne "ok" -or $zhumuMergedWeightedVertexCount -lt 1) {
            throw "Zhumu merged animation preview lost vertex-weight coverage diagnostic. status=$($zhumuMergedVertexCoverage.status) weightedVertices=$zhumuMergedWeightedVertexCount"
        }

        $zhumuMergedReasonCodes = @($zhumuMergedReportJson.sourceAssessment.reasons | ForEach-Object { [string]$_.reason } | Where-Object { ![string]::IsNullOrWhiteSpace($_) })
        foreach ($requiredReason in @("standalone_animation_not_production_ready", "standalone_animation_experimental", "humanoid_solver_known_limb_risk", "low_humanoid_channel_coverage")) {
            if ($zhumuMergedReasonCodes -notcontains $requiredReason) {
                throw "Zhumu merged animation preview lost expected diagnostic reason: $requiredReason"
            }
        }

        $zhumuMergedRenderImages = @(
            (Join-Path $zhumuMergedRenderRoot "zhumu_attack_a4_rest.png"),
            (Join-Path $zhumuMergedRenderRoot "zhumu_attack_a4_mid.png"),
            (Join-Path $zhumuMergedRenderRoot "zhumu_attack_a4_end.png")
        )
        foreach ($renderImage in $zhumuMergedRenderImages) {
            Test-FileRequired -Path $renderImage -Label "Zhumu merged animation render image"
            if ((Get-Item -LiteralPath $renderImage).Length -le 0) {
                throw "Zhumu merged animation render image is empty: $renderImage"
            }
        }
        $zhumuMergedSubjectReport = Join-Path $OutputRoot "zhumu_render_subject_occupancy.json"
        $subjectAnalyzer = Join-Path $PSScriptRoot "analyze_render_image_subject.py"
        Test-FileRequired -Path $subjectAnalyzer -Label "render image subject analyzer"
        $subjectAnalyzerArgs = @(
            $subjectAnalyzer,
            "--output", $zhumuMergedSubjectReport,
            "--min_foreground_pixel_ratio", "0.08",
            "--min_foreground_height_ratio", "0.45",
            "--images"
        ) + $zhumuMergedRenderImages
        Invoke-Checked -Label "Analyze Zhumu merged animation render subject occupancy" -FilePath (Resolve-SmokePython) -Arguments $subjectAnalyzerArgs
        Test-FileRequired -Path $zhumuMergedSubjectReport -Label "Zhumu render subject occupancy report"
        $zhumuMergedSubjectOccupancy = Get-Content -LiteralPath $zhumuMergedSubjectReport -Raw -Encoding UTF8 | ConvertFrom-Json
        if ([string]$zhumuMergedSubjectOccupancy.status -ne "ok") {
            throw "Zhumu merged animation render images do not show a visible subject. status=$($zhumuMergedSubjectOccupancy.status) failed=$($zhumuMergedSubjectOccupancy.failedCount)"
        }

        $zhumuMergedRenderSummaryText = (Get-Content -LiteralPath $zhumuMergedRenderSummary -Raw -Encoding UTF8).Trim()
        $zhumuMergedRenderLines = @($zhumuMergedRenderSummaryText -split "`r?`n" | Where-Object { ![string]::IsNullOrWhiteSpace($_) })
        foreach ($requiredFrame in @("rest:", "mid:", "end:")) {
            if (@($zhumuMergedRenderLines | Where-Object { $_.StartsWith($requiredFrame, [System.StringComparison]::OrdinalIgnoreCase) }).Count -lt 1) {
                throw "Zhumu merged animation render summary lost frame entry: $requiredFrame"
            }
        }
        $zhumuMergedRenderBboxMotion = Read-RenderProbeBboxMotion -Lines $zhumuMergedRenderLines
        if ([string]$zhumuMergedRenderBboxMotion.status -ne "ok") {
            throw "Zhumu merged animation render probe did not show bbox motion. status=$($zhumuMergedRenderBboxMotion.status) maxDelta=$($zhumuMergedRenderBboxMotion.maxCoordinateDelta)"
        }

        if (!$SkipGltfValidation) {
            $gltfTransform = Get-Command "gltf-transform.cmd" -ErrorAction SilentlyContinue
            if ($null -ne $gltfTransform) {
                Invoke-Checked -Label "Validate Zhumu merged animation glTF" -FilePath $gltfTransform.Source -Arguments @("validate", $zhumuMergedGltf)
                $zhumuMergedAnimationPreview.gltfValidation = "ok"
            }
        }

        $zhumuMergedAnimationPreview = [pscustomobject]@{
            status = "ok"
            rule = $zhumuMergedAnimationPreview.rule
            productionReadiness = "blocked"
            blockedProductionRequirements = $zhumuMergedBlockedProductionRequirements
            sampleRoot = $ZhumuMergedAnimationProbeRoot
            report = $zhumuMergedReport
            gltf = $zhumuMergedGltf
            gltfValidation = $zhumuMergedAnimationPreview.gltfValidation
            animationCountAdded = $zhumuMergedAnimationCountAdded
            channelCount = $zhumuMergedChannelCount
            animationJointCoverage = $zhumuMergedJointCoverage
            sourceAssessmentStatus = [string]$zhumuMergedReportJson.sourceAssessment.status
            reasonCodes = $zhumuMergedReasonCodes
            renderProbeStatus = "ok"
            renderFrameCount = [int]$zhumuMergedRenderLines.Count
            renderBboxMotion = $zhumuMergedRenderBboxMotion
            renderSubjectOccupancy = $zhumuMergedSubjectOccupancy
            renderImages = $zhumuMergedRenderImages
            renderSummary = $zhumuMergedRenderSummaryText
        }
    }
    else {
        $zhumuMergedAnimationPreview.status = "missing"
        Write-Warning "Zhumu merged animation preview probe not found; skipped: $ZhumuMergedAnimationProbeRoot"
    }
}
else {
    $zhumuMergedAnimationPreview.status = "skipped"
}

if ($zhumuMergedAnimationPreview.status -eq "ok") {
    if ([string]$zhumuMergedAnimationPreview.productionReadiness -ne "blocked") {
        throw "Zhumu merged animation preview must stay production-blocked. productionReadiness=$($zhumuMergedAnimationPreview.productionReadiness)"
    }
    $zhumuMergedActualBlockedRequirements = @($zhumuMergedAnimationPreview.blockedProductionRequirements | ForEach-Object { [string]$_ })
    foreach ($requiredRequirement in $zhumuMergedBlockedProductionRequirements) {
        if ($zhumuMergedActualBlockedRequirements -notcontains [string]$requiredRequirement) {
            throw "Zhumu merged animation preview lost production blocked requirement: $requiredRequirement"
        }
    }
}

$assetLibrary = Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
$modelValidation = Get-Content -LiteralPath $validation -Raw -Encoding UTF8 | ConvertFrom-Json
$sqliteSummaryJson = Get-Content -LiteralPath $sqliteSummary -Raw -Encoding UTF8 | ConvertFrom-Json
$assetLibraryContract = Test-AssetLibraryV1Contract -ManifestPath $manifest -DbPath $db -AssetLibrary $assetLibrary -ExpectedGame "Naraka"
$libraryCoreArtifacts = Test-LibraryCoreArtifacts -LibraryRoot $libraryOutput
$modelReportEntrypoints = Test-ModelReportEntrypoints -LibraryRoot $libraryOutput -AssetCatalogPath $assetCatalog
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

$expectedJiantianshiOutput = "Models/assets/res/prefab/actor_visual_part/ch_f_jiantianshi/ch_f_jiantianshi_lv_s1/ch_f_jiantianshi_lv_s1.gltf"
$jiantianshiCatalogRow = $null
foreach ($catalogLine in Get-Content -LiteralPath $assetCatalog -Encoding UTF8) {
    if ([string]::IsNullOrWhiteSpace($catalogLine)) {
        continue
    }

    $catalogRow = $catalogLine | ConvertFrom-Json
    if ($catalogRow.name -eq "ch_f_jiantianshi_lv_s1") {
        $jiantianshiCatalogRow = $catalogRow
        break
    }
}
if ($null -eq $jiantianshiCatalogRow) {
    throw "asset_catalog.jsonl lost Jiantianshi formal skinned representative row."
}

$jiantianshiSource = [string]$jiantianshiCatalogRow.source
$jiantianshiPrimitiveCount = [long]$jiantianshiCatalogRow.materialPrimitiveCount
$jiantianshiPrimitivesWithMaterial = [long]$jiantianshiCatalogRow.materialPrimitivesWithMaterial
if ($jiantianshiCatalogRow.libraryRole -ne "PrefabPrimary" -or
    $jiantianshiCatalogRow.resourceKind -ne "Character" -or
    $jiantianshiCatalogRow.modelValidationStatus -ne "ok" -or
    $jiantianshiCatalogRow.modelBodyStatus -ne "ok" -or
    $jiantianshiCatalogRow.materialMissingRendererBinding -ne $false -or
    $jiantianshiPrimitiveCount -le 0 -or
    $jiantianshiPrimitiveCount -ne $jiantianshiPrimitivesWithMaterial -or
    [long]$jiantianshiCatalogRow.boneCount -le 0 -or
    [long]$jiantianshiCatalogRow.pathId -ne $FormalSkinnedRepresentativeRootPathId -or
    [string]$jiantianshiCatalogRow.output -ne $expectedJiantianshiOutput -or
    $jiantianshiSource -notlike "*$FormalSkinnedRepresentativeBundle*") {
    throw "Jiantianshi formal skinned representative gate failed. libraryRole=$($jiantianshiCatalogRow.libraryRole) resourceKind=$($jiantianshiCatalogRow.resourceKind) validation=$($jiantianshiCatalogRow.modelValidationStatus) body=$($jiantianshiCatalogRow.modelBodyStatus) missingRendererBinding=$($jiantianshiCatalogRow.materialMissingRendererBinding) primitives=$jiantianshiPrimitivesWithMaterial/$jiantianshiPrimitiveCount bones=$($jiantianshiCatalogRow.boneCount) pathId=$($jiantianshiCatalogRow.pathId) output=$($jiantianshiCatalogRow.output) source=$jiantianshiSource"
}

$compactAnimationIndex = Get-Content -LiteralPath $modelAnimationsCompact -Raw -Encoding UTF8 | ConvertFrom-Json
$jiantianshiCompactModel = @($compactAnimationIndex.models | Where-Object { $_.output -eq $expectedJiantianshiOutput }) | Select-Object -First 1
if ($null -eq $jiantianshiCompactModel) {
    throw "model_animations.compact.json lost Jiantianshi model row."
}
$jiantianshiCompactRef = @($compactAnimationIndex.modelAnimationRefs | Where-Object { $_.modelId -eq $jiantianshiCompactModel.id }) | Select-Object -First 1
if ($null -eq $jiantianshiCompactRef) {
    throw "model_animations.compact.json lost Jiantianshi model animation ref row."
}
if ($jiantianshiCompactModel.modelAnimationGate.status -ne "ready" -or
    $jiantianshiCompactModel.modelReadyForAnimation -ne $true -or
    [long]$jiantianshiCompactRef.candidateCount -ne 0 -or
    [long]$jiantianshiCompactRef.usableCandidateCount -ne 0) {
    throw "Jiantianshi animation gate must be model-ready but without default animation candidates. gate=$($jiantianshiCompactModel.modelAnimationGate.status) ready=$($jiantianshiCompactModel.modelReadyForAnimation) candidates=$($jiantianshiCompactRef.candidateCount) usable=$($jiantianshiCompactRef.usableCandidateCount)"
}
$formalSkinnedRepresentativeBoundary = [pscustomobject]@{
    status = "ok"
    rule = $formalSkinnedRepresentativeBoundary.rule
    name = [string]$jiantianshiCatalogRow.name
    libraryRole = [string]$jiantianshiCatalogRow.libraryRole
    resourceKind = [string]$jiantianshiCatalogRow.resourceKind
    output = [string]$jiantianshiCatalogRow.output
    source = $jiantianshiSource
    pathId = [long]$jiantianshiCatalogRow.pathId
    modelValidationStatus = [string]$jiantianshiCatalogRow.modelValidationStatus
    modelBodyStatus = [string]$jiantianshiCatalogRow.modelBodyStatus
    materialMissingRendererBinding = [bool]$jiantianshiCatalogRow.materialMissingRendererBinding
    materialPrimitiveCount = $jiantianshiPrimitiveCount
    materialPrimitivesWithMaterial = $jiantianshiPrimitivesWithMaterial
    boneCount = [long]$jiantianshiCatalogRow.boneCount
    modelAnimationGate = [string]$jiantianshiCompactModel.modelAnimationGate.status
    modelReadyForAnimation = [bool]$jiantianshiCompactModel.modelReadyForAnimation
    defaultCandidateCount = [long]$jiantianshiCompactRef.candidateCount
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
if ($null -eq $sqliteSummaryJson.animationSupport) {
    throw "sqlite_index_summary.json lost animationSupport section."
}
if ($sqliteSummaryJson.animationSupport.productionReady -ne $false -or [long]$sqliteSummaryJson.animationSupport.defaultModelAnimationCandidateCount -ne 0) {
    throw "sqlite_index_summary.json animationSupport must stay non-production for Naraka smoke. productionReady=$($sqliteSummaryJson.animationSupport.productionReady) candidates=$($sqliteSummaryJson.animationSupport.defaultModelAnimationCandidateCount)"
}
if ($null -eq $sqliteSummaryJson.counts) {
    throw "sqlite_index_summary.json lost counts section."
}
$sqliteFiles = ConvertTo-SmokeInt64 $sqliteSummaryJson.counts.files
$sqliteFilesSkipped = ConvertTo-SmokeInt64 $sqliteSummaryJson.counts.filesSkipped
if ($sqliteFiles -le 0 -or $sqliteFilesSkipped -ne 0) {
    throw "Representative Library SQLite must keep the files table for AssetLibrary browsing/audit. files=$sqliteFiles filesSkipped=$sqliteFilesSkipped"
}
$thumbnailCache = Join-Path $libraryOutput ".asset_browser_cache"
$thumbnailFileCount = 0
if (Test-Path -LiteralPath $thumbnailCache) {
    $thumbnailFileCount = (Get-ChildItem -LiteralPath $thumbnailCache -Recurse -File | Measure-Object).Count
}
if ($thumbnailStatus -eq "ok" -and $thumbnailFileCount -lt $expectedThumbnailCount) {
    throw "Browser thumbnail smoke did not cover all representative models. expected>=$expectedThumbnailCount actual=$thumbnailFileCount"
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
    $animationDiagnosticLines += '  "gltfValidation": ' + (ConvertTo-SmokeJsonLiteral $animationGltfValidationStatus) + ","
    $animationDiagnosticLines += '  "productionReadiness": "blocked",'
    $animationDiagnosticLines += '  "blockedProductionRequirements": ' + (ConvertTo-SmokeJsonLiteral $animationDiagnosticBlockedRequirements 10) + ","
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
    $animationDiagnosticLines += '  "gltfValidation": ' + (ConvertTo-SmokeJsonLiteral $animationGltfValidationStatus) + ","
    $animationDiagnosticLines += '  "productionReadiness": "blocked",'
    $animationDiagnosticLines += '  "blockedProductionRequirements": ' + (ConvertTo-SmokeJsonLiteral $animationDiagnosticBlockedRequirements 10) + ","
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
$summaryJsonLines += '  "assetLibraryContract": ' + (ConvertTo-SmokeJsonLiteral $assetLibraryContract 10) + ","
$summaryJsonLines += '  "libraryCoreArtifacts": ' + (ConvertTo-SmokeJsonLiteral $libraryCoreArtifacts 10) + ","
$summaryJsonLines += '  "modelReportEntrypoints": ' + (ConvertTo-SmokeJsonLiteral $modelReportEntrypoints 10) + ","
$summaryJsonLines += '  "libraryIndex": ' + (ConvertTo-SmokeJsonLiteral $db) + ","
$summaryJsonLines += '  "sqliteSummary": ' + (ConvertTo-SmokeJsonLiteral $sqliteSummary) + ","
$summaryJsonLines += '  "modelValidation": ' + (ConvertTo-SmokeJsonLiteral $validation) + ","
$summaryJsonLines += '  "modelGltf": ' + (ConvertTo-SmokeJsonLiteral $gltf) + ","
$summaryJsonLines += '  "representativeModels": ['
$summaryJsonLines += '    { "name": "ch_m_hadi_lv_s9", "role": "CharacterPart", "gltf": ' + (ConvertTo-SmokeJsonLiteral $gltf) + ' },'
$summaryJsonLines += '    { "name": "ch_f_jiantianshi_lv_s1", "role": "SkinnedActorVisualPart", "gltf": ' + (ConvertTo-SmokeJsonLiteral $jiantianshiGltf) + ' },'
$summaryJsonLines += '    { "name": "weapon_drop_bow_dongjun", "role": "WeaponProp", "gltf": ' + (ConvertTo-SmokeJsonLiteral $bowGltf) + ' },'
$summaryJsonLines += '    { "name": "device_hongbao_02", "role": "Prop", "gltf": ' + (ConvertTo-SmokeJsonLiteral $deviceGltf) + ' }'
$summaryJsonLines += '  ],'
$summaryJsonLines += '  "hadiModularBoundary": ' + (ConvertTo-SmokeJsonLiteral $hadiModularBoundary 10) + ","
$summaryJsonLines += '  "formalSkinnedRepresentativeBoundary": ' + (ConvertTo-SmokeJsonLiteral $formalSkinnedRepresentativeBoundary 10) + ","
$summaryJsonLines += '  "shaderBoundary": {'
$summaryJsonLines += '    "status": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundaryStatus) + ","
$summaryJsonLines += '    "sampleRoot": ' + (ConvertTo-SmokeJsonLiteral $ShaderBoundarySampleRoot) + ","
$summaryJsonLines += '    "gltf": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundaryGltf) + ","
$summaryJsonLines += '    "gltfValidation": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundaryGltfValidationStatus) + ","
$summaryJsonLines += '    "textureLinkErrors": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundarySummaryJson.qualityGates.textureLinkErrors) + ","
$summaryJsonLines += '    "customShaderRequiredSidecars": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundarySummaryJson.qualityGates.customShaderRequiredSidecars) + ","
$summaryJsonLines += '    "layeredMaterialUnresolvedSidecars": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundarySummaryJson.qualityGates.layeredMaterialUnresolvedSidecars) + ","
$summaryJsonLines += '    "degradedPreviewSidecars": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundarySummaryJson.qualityGates.degradedPreviewSidecars) + ","
$summaryJsonLines += '    "qualityShaderReferenceSidecars": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundarySummaryJson.qualityGates.shaderReferenceSidecars) + ","
$summaryJsonLines += '    "qualityShaderReferenceOnlySidecars": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundarySummaryJson.qualityGates.shaderReferenceOnlySidecars) + ","
$summaryJsonLines += '    "qualityCustomShaderReferenceSidecars": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundarySummaryJson.qualityGates.customShaderReferenceSidecars) + ","
$summaryJsonLines += '    "qualityCustomShaderMissingNameSidecars": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundarySummaryJson.qualityGates.customShaderMissingNameSidecars) + ","
$summaryJsonLines += '    "materialSidecarRows": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundaryMaterialSidecarRows) + ","
$summaryJsonLines += '    "customShaderMaterialSidecarRows": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundaryCustomShaderRows) + ","
$summaryJsonLines += '    "customShaderEvidenceRows": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundaryCustomShaderEvidenceRows) + ","
$summaryJsonLines += '    "missingShaderNameRows": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundaryMissingShaderNameRows) + ","
$summaryJsonLines += '    "shaderReferenceRows": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundaryShaderReferenceRows) + ","
$summaryJsonLines += '    "shaderReferenceMode": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundaryShaderReferenceMode) + ","
$summaryJsonLines += '    "customShaderEvidenceSamples": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundaryCustomShaderEvidenceSamples) + ","
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
$summaryJsonLines += '  "zhumuScriptAnimationProbe": {'
$summaryJsonLines += '    "status": ' + (ConvertTo-SmokeJsonLiteral $zhumuScriptAnimationStatus) + ","
$summaryJsonLines += '    "sampleRoot": ' + (ConvertTo-SmokeJsonLiteral $ZhumuScriptAnimationSampleRoot) + ","
$summaryJsonLines += '    "gltf": ' + (ConvertTo-SmokeJsonLiteral $zhumuScriptAnimationGltf) + ","
$summaryJsonLines += '    "gltfValidation": ' + (ConvertTo-SmokeJsonLiteral $zhumuScriptAnimationGltfValidationStatus) + ","
$summaryJsonLines += '    "models": ' + (ConvertTo-SmokeJsonLiteral $zhumuScriptAnimationValidationJson.totals.models) + ","
$summaryJsonLines += '    "ok": ' + (ConvertTo-SmokeJsonLiteral $zhumuScriptAnimationValidationJson.totals.ok) + ","
$summaryJsonLines += '    "withSkin": ' + (ConvertTo-SmokeJsonLiteral $zhumuScriptAnimationValidationJson.totals.withSkin) + ","
$summaryJsonLines += '    "withTextures": ' + (ConvertTo-SmokeJsonLiteral $zhumuScriptAnimationValidationJson.totals.withTextures) + ","
$summaryJsonLines += '    "skinJointCount": ' + (ConvertTo-SmokeJsonLiteral $zhumuScriptAnimationSkinJointCount) + ","
$summaryJsonLines += '    "maxBoundsSize": ' + (ConvertTo-SmokeJsonLiteral $zhumuScriptAnimationMaxBoundsSize) + ","
$summaryJsonLines += '    "textureAssets": ' + (ConvertTo-SmokeJsonLiteral $zhumuScriptAnimationSummaryJson.counts.textureAssets) + ","
$summaryJsonLines += '    "textureLinks": ' + (ConvertTo-SmokeJsonLiteral $zhumuScriptAnimationSummaryJson.counts.textureLinks) + ","
$summaryJsonLines += '    "textureLinkErrors": ' + (ConvertTo-SmokeJsonLiteral $zhumuScriptAnimationTextureLinkErrors) + ","
$summaryJsonLines += '    "materialSidecars": ' + (ConvertTo-SmokeJsonLiteral $zhumuScriptAnimationSummaryJson.counts.materialSidecars) + ","
$summaryJsonLines += '    "materialSidecarRows": ' + (ConvertTo-SmokeJsonLiteral $zhumuScriptAnimationMaterialSidecarRows) + ","
$summaryJsonLines += '    "modelRows": ' + (ConvertTo-SmokeJsonLiteral $zhumuScriptAnimationModelRows) + ","
$summaryJsonLines += '    "modelAnimationCandidateRows": ' + (ConvertTo-SmokeJsonLiteral $zhumuScriptAnimationModelAnimationCandidateRows) + ","
$summaryJsonLines += '    "modelAnimationRelationRows": ' + (ConvertTo-SmokeJsonLiteral $zhumuScriptAnimationModelAnimationRelationRows) + ","
$summaryJsonLines += '    "relationAnimationRows": ' + (ConvertTo-SmokeJsonLiteral $zhumuScriptAnimationRelationAnimationRows) + ","
$summaryJsonLines += '    "script": ' + (ConvertTo-SmokeJsonLiteral $zhumuScriptAnimationDiagnostic 10) + ","
$summaryJsonLines += '    "avatar": ' + (ConvertTo-SmokeJsonLiteral $zhumuAvatarCompatibilityDiagnostic 10) + ","
$summaryJsonLines += '    "rule": ' + (ConvertTo-SmokeJsonLiteral $zhumuScriptAnimationRule)
$summaryJsonLines += '  },'
$summaryJsonLines += '  "zhumuMergedAnimationPreview": ' + (ConvertTo-SmokeJsonLiteral $zhumuMergedAnimationPreview 10) + ","
$summaryJsonLines += '  "characterCandidateSourceIndexBoundary": ' + (ConvertTo-SmokeJsonLiteral $characterCandidateSourceIndexBoundary 10) + ","
$summaryJsonLines += '  "checks": {'
$summaryJsonLines += '    "representativeGltfValidation": ' + (ConvertTo-SmokeJsonLiteral $representativeGltfValidationStatus) + ","
$summaryJsonLines += '    "shaderBoundary": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundaryStatus) + ","
$summaryJsonLines += '    "shaderBoundaryGltfValidation": ' + (ConvertTo-SmokeJsonLiteral $shaderBoundaryGltfValidationStatus) + ","
$summaryJsonLines += '    "staticEnvironment": ' + (ConvertTo-SmokeJsonLiteral $staticEnvironmentStatus) + ","
$summaryJsonLines += '    "staticEnvironmentGltfValidation": ' + (ConvertTo-SmokeJsonLiteral $staticEnvironmentGltfValidationStatus) + ","
$summaryJsonLines += '    "characterCandidate": ' + (ConvertTo-SmokeJsonLiteral $characterCandidateStatus) + ","
$summaryJsonLines += '    "zhumuScriptAnimationProbe": ' + (ConvertTo-SmokeJsonLiteral $zhumuScriptAnimationStatus) + ","
$summaryJsonLines += '    "zhumuScriptAnimationGltfValidation": ' + (ConvertTo-SmokeJsonLiteral $zhumuScriptAnimationGltfValidationStatus) + ","
$summaryJsonLines += '    "zhumuMergedAnimationPreview": ' + (ConvertTo-SmokeJsonLiteral $zhumuMergedAnimationPreview.status) + ","
$summaryJsonLines += '    "zhumuMergedAnimationGltfValidation": ' + (ConvertTo-SmokeJsonLiteral $zhumuMergedAnimationPreview.gltfValidation) + ","
$summaryJsonLines += '    "hadiModularBoundary": ' + (ConvertTo-SmokeJsonLiteral $hadiModularBoundary.status) + ","
$summaryJsonLines += '    "formalSkinnedRepresentativeBoundary": ' + (ConvertTo-SmokeJsonLiteral $formalSkinnedRepresentativeBoundary.status) + ","
$summaryJsonLines += '    "characterCandidateGltfValidation": ' + (ConvertTo-SmokeJsonLiteral $characterCandidateGltfValidationStatus) + ","
$summaryJsonLines += '    "characterCandidateSourceIndexBoundary": ' + (ConvertTo-SmokeJsonLiteral $characterCandidateSourceIndexBoundary.status) + ","
$summaryJsonLines += '    "sourceIndexAvatarAnimatorDomains": ' + (ConvertTo-SmokeJsonLiteral $sourceIndexAvatarAnimatorDomains.status) + ","
$summaryJsonLines += '    "sourceIndexLegacyAnimationClipDomains": ' + (ConvertTo-SmokeJsonLiteral $sourceIndexLegacyAnimationClipDomains.status) + ","
$summaryJsonLines += '    "scriptAnimationComponentDiagnostics": ' + (ConvertTo-SmokeJsonLiteral $scriptAnimationComponentDiagnostics.status) + ","
$summaryJsonLines += '    "sourceIndexSimpleAnimationClipDomains": ' + (ConvertTo-SmokeJsonLiteral $sourceIndexSimpleAnimationClipDomains.status) + ","
$summaryJsonLines += '    "sourceModelAvatarDiagnostics": ' + (ConvertTo-SmokeJsonLiteral $sourceModelAvatarDiagnostics.status) + ","
$summaryJsonLines += '    "animatorControllerProductionGate": ' + (ConvertTo-SmokeJsonLiteral $animatorControllerProductionGate.status) + ","
$summaryJsonLines += '    "browserValidation": ' + (ConvertTo-SmokeJsonLiteral $browserValidationStatus) + ","
$summaryJsonLines += '    "thumbnailRender": ' + (ConvertTo-SmokeJsonLiteral $thumbnailStatus) + ","
$summaryJsonLines += '    "thumbnailExpectedCount": ' + (ConvertTo-SmokeJsonLiteral $expectedThumbnailCount) + ","
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
$summaryJsonLines += '  "animationSupport": ' + (ConvertTo-SmokeJsonLiteral $sqliteSummaryJson.animationSupport 10) + ","
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
$summaryJsonLines += '    "scriptAnimationComponentDiagnostics": ' + (ConvertTo-SmokeJsonLiteral $scriptAnimationComponentDiagnostics 10) + ","
$summaryJsonLines += '    "sourceIndexSimpleAnimationClipDomains": ' + (ConvertTo-SmokeJsonLiteral $sourceIndexSimpleAnimationClipDomains 10) + ","
$summaryJsonLines += '    "sourceModelAvatarDiagnostics": ' + (ConvertTo-SmokeJsonLiteral $sourceModelAvatarDiagnostics 10)
$summaryJsonLines += '  },'
$summaryJsonLines += '  "sourceIndexLegacyAnimationClipDomains": ' + (ConvertTo-SmokeJsonLiteral $sourceIndexLegacyAnimationClipDomains 10) + ","
$summaryJsonLines += '  "animatorControllerProductionGate": ' + (ConvertTo-SmokeJsonLiteral $animatorControllerProductionGate 10) + ","
$summaryJsonLines += '  "monoBehaviourAnimationClipPPtrSummary": ' + $monoBehaviourAnimationClipPPtrSummaryJson + ","
$summaryJsonLines += '  "scriptAnimationComponentDiagnostics": ' + (ConvertTo-SmokeJsonLiteral $scriptAnimationComponentDiagnostics 10) + ","
$summaryJsonLines += '  "sourceIndexSimpleAnimationClipDomains": ' + (ConvertTo-SmokeJsonLiteral $sourceIndexSimpleAnimationClipDomains 10) + ","
$summaryJsonLines += '  "sourceModelAvatarDiagnostics": ' + (ConvertTo-SmokeJsonLiteral $sourceModelAvatarDiagnostics 10) + ","
$summaryJsonLines += '  "animationDiagnostic": ' + $animationDiagnosticJson
$summaryJsonLines += "}"
$summaryJson = $summaryJsonLines -join [Environment]::NewLine
$summaryJsonPath = [System.IO.Path]::Combine($OutputRoot, "smoke_summary.json")
if ([string]::IsNullOrWhiteSpace($summaryJsonPath)) {
    throw "smoke_summary.json output path is empty. OutputRoot='$OutputRoot'"
}
$summaryJson | Set-Content -LiteralPath $summaryJsonPath -Encoding UTF8
# smoke_summary.json 是后续自动验收入口，写完后立刻反读，避免报告可读但机器摘要损坏。
$summaryJsonParsed = Get-Content -LiteralPath $summaryJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ([string]$summaryJsonParsed.animationDiagnostic.productionReadiness -ne "blocked") {
    throw "smoke_summary.json animationDiagnostic must stay production-blocked. productionReadiness=$($summaryJsonParsed.animationDiagnostic.productionReadiness)"
}
$summaryAnimationBlockedRequirements = @($summaryJsonParsed.animationDiagnostic.blockedProductionRequirements | ForEach-Object { [string]$_ })
foreach ($requiredRequirement in $animationDiagnosticBlockedRequirements) {
    if ($summaryAnimationBlockedRequirements -notcontains [string]$requiredRequirement) {
        throw "smoke_summary.json animationDiagnostic lost production blocked requirement: $requiredRequirement"
    }
}
if ($summaryJsonParsed.zhumuMergedAnimationPreview.status -eq "ok") {
    if ([string]$summaryJsonParsed.zhumuMergedAnimationPreview.productionReadiness -ne "blocked") {
        throw "smoke_summary.json zhumuMergedAnimationPreview must stay production-blocked. productionReadiness=$($summaryJsonParsed.zhumuMergedAnimationPreview.productionReadiness)"
    }
    $summaryZhumuBlockedRequirements = @($summaryJsonParsed.zhumuMergedAnimationPreview.blockedProductionRequirements | ForEach-Object { [string]$_ })
    foreach ($requiredRequirement in $zhumuMergedBlockedProductionRequirements) {
        if ($summaryZhumuBlockedRequirements -notcontains [string]$requiredRequirement) {
            throw "smoke_summary.json zhumuMergedAnimationPreview lost production blocked requirement: $requiredRequirement"
        }
    }
}

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
$reportLines.Add(('- AssetLibrary v1 contract: status=`{0}`, schemaVersion=`{1}`, sourceGame=`{2}`, index=`{3}`, sqliteTableCheck=`{4}`' -f `
    (ConvertTo-SmokeText $assetLibraryContract.status),
    (ConvertTo-SmokeText $assetLibraryContract.schemaVersion),
    (ConvertTo-SmokeText $assetLibraryContract.sourceGame),
    (ConvertTo-SmokeText $assetLibraryContract.index),
    (ConvertTo-SmokeText $assetLibraryContract.sqliteTableCheck)))
$reportLines.Add(('- Animation relation tables: modelRelations=`{0}`, relationAnimations=`{1}`, usableRelationAnimations=`{2}`' -f `
    (ConvertTo-SmokeText $assetLibraryContract.modelAnimationRelationRows "unknown"),
    (ConvertTo-SmokeText $assetLibraryContract.relationAnimationRows "unknown"),
    (ConvertTo-SmokeText $assetLibraryContract.usableRelationAnimationRows "unknown")))
$reportLines.Add(('- Core artifact contract: status=`{0}`, files=`{1}`, missing=`{2}`, invalidJson=`{3}`' -f `
    (ConvertTo-SmokeText $libraryCoreArtifacts.status),
    (ConvertTo-SmokeText $libraryCoreArtifacts.fileCount "0"),
    (ConvertTo-SmokeText ($libraryCoreArtifacts.missing -join ",") "0"),
    (ConvertTo-SmokeText ($libraryCoreArtifacts.invalidJson -join ",") "0")))
$reportLines.Add(('- Model report entrypoints: status=`{0}`, models=`{1}`, ASSET_README=`{2}`, MATERIAL_REPORT=`{3}`' -f `
    (ConvertTo-SmokeText $modelReportEntrypoints.status),
    (ConvertTo-SmokeText $modelReportEntrypoints.modelCount "0"),
    (ConvertTo-SmokeText $modelReportEntrypoints.assetReadmeCount "0"),
    (ConvertTo-SmokeText $modelReportEntrypoints.materialReportCount "0")))
$reportLines.Add(('- Representative glTF validator: `{0}`' -f $representativeGltfValidationStatus))
$reportLines.Add(('- AssetLibrary browser validation: `{0}`' -f $browserValidationStatus))
$reportLines.Add(('- Thumbnail render: `{0}`, expected>=`{1}`, cache files=`{2}`' -f $thumbnailStatus, $expectedThumbnailCount, $thumbnailFileCount))
if ($null -ne $modelValidation.totals) {
    $reportLines.Add(('- Model totals: models=`{0}`, ok=`{1}`, warning=`{2}`, error=`{3}`' -f `
        (ConvertTo-SmokeText $modelValidation.totals.models "0"),
        (ConvertTo-SmokeText $modelValidation.totals.ok "0"),
        (ConvertTo-SmokeText $modelValidation.totals.warning "0"),
        (ConvertTo-SmokeText $modelValidation.totals.error "0")))
}
$reportLines.Add(('- Representative samples: `ch_m_hadi_lv_s9` (CharacterPart), `ch_f_jiantianshi_lv_s1` (SkinnedActorVisualPart), `weapon_drop_bow_dongjun` (WeaponProp), `device_hongbao_02` (Prop)'))
$reportLines.Add('- Representative boundary: `ch_m_japan_samurai_ghost` stays in the read-only skinned candidate gate because it comes from an effect/battle bundle and still has no explicit production model-animation relation.')
$reportLines.Add('- Script-animation boundary: `mo_pve_b_zhumu_soul_01` stays in a separate read-only probe because it has strong SimpleAnimation/Avatar diagnostics and a valid attack prefab model, but no production TRS export or visual animation review.')
if ($formalSkinnedRepresentativeBoundary.status -eq "ok") {
    $reportLines.Add(('- Jiantianshi formal skinned representative: validation=`{0}`, body=`{1}`, resourceKind=`{2}`, materials=`{3}/{4}`, bones=`{5}`, animationGate=`{6}`, defaultCandidates=`{7}`' -f `
        (ConvertTo-SmokeText $formalSkinnedRepresentativeBoundary.modelValidationStatus),
        (ConvertTo-SmokeText $formalSkinnedRepresentativeBoundary.modelBodyStatus),
        (ConvertTo-SmokeText $formalSkinnedRepresentativeBoundary.resourceKind),
        (ConvertTo-SmokeText $formalSkinnedRepresentativeBoundary.materialPrimitivesWithMaterial "0"),
        (ConvertTo-SmokeText $formalSkinnedRepresentativeBoundary.materialPrimitiveCount "0"),
        (ConvertTo-SmokeText $formalSkinnedRepresentativeBoundary.boneCount "0"),
        (ConvertTo-SmokeText $formalSkinnedRepresentativeBoundary.modelAnimationGate),
        (ConvertTo-SmokeText $formalSkinnedRepresentativeBoundary.defaultCandidateCount "0")))
}
else {
    $reportLines.Add(('- Jiantianshi formal skinned representative: `{0}`' -f $formalSkinnedRepresentativeBoundary.status))
}
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
    $reportLines.Add(('- Assets=`{0}`, assetCatalog=`{1}`, textureAssets=`{2}`, textureLinks=`{3}`, materialSidecars=`{4}`, modelValidation=`{5}`, files=`{6}`' -f `
        (ConvertTo-SmokeText $sqliteSummaryJson.counts.assets "0"),
        (ConvertTo-SmokeText $sqliteSummaryJson.counts.assetCatalog "0"),
        (ConvertTo-SmokeText $sqliteSummaryJson.counts.textureAssets "0"),
        (ConvertTo-SmokeText $sqliteSummaryJson.counts.textureLinks "0"),
        (ConvertTo-SmokeText $sqliteSummaryJson.counts.materialSidecars "0"),
        (ConvertTo-SmokeText $sqliteSummaryJson.counts.modelValidation "0"),
        (ConvertTo-SmokeText $sqliteSummaryJson.counts.files "0")))
    $reportLines.Add(('- Model animation candidates=`{0}`, model animation relations=`{1}`' -f `
        (ConvertTo-SmokeText $sqliteSummaryJson.counts.modelAnimationCandidates "0"),
        (ConvertTo-SmokeText $sqliteSummaryJson.counts.modelAnimationRelations "0")))
}
if ($null -ne $sqliteSummaryJson.qualityGates) {
    $reportLines.Add(('- Quality gates: textureLinkErrors=`{0}`, customShaderSidecars=`{1}`, layeredMaterialUnresolvedSidecars=`{2}`, degradedPreviewSidecars=`{3}`, shaderReferenceSidecars=`{4}`, shaderReferenceOnlySidecars=`{5}`, modelsNeedingCustomShaderLayer=`{6}`, modelsNeedingCustomizationTint=`{7}`' -f `
        (ConvertTo-SmokeText $sqliteSummaryJson.qualityGates.textureLinkErrors "0"),
        (ConvertTo-SmokeText $sqliteSummaryJson.qualityGates.customShaderRequiredSidecars "0"),
        (ConvertTo-SmokeText $sqliteSummaryJson.qualityGates.layeredMaterialUnresolvedSidecars "0"),
        (ConvertTo-SmokeText $sqliteSummaryJson.qualityGates.degradedPreviewSidecars "0"),
        (ConvertTo-SmokeText $sqliteSummaryJson.qualityGates.shaderReferenceSidecars "0"),
        (ConvertTo-SmokeText $sqliteSummaryJson.qualityGates.shaderReferenceOnlySidecars "0"),
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
    if ($sourceIndexSimpleAnimationClipDomains.status -eq "ok") {
        $simpleAnimationDomainParts = @()
        foreach ($domainRow in @($sourceIndexSimpleAnimationClipDomains.domainCounts)) {
            $simpleAnimationDomainParts += ('{0}={1}/{2}' -f `
                (ConvertTo-SmokeText $domainRow.domain),
                (ConvertTo-SmokeText $domainRow.relations "0"),
                (ConvertTo-SmokeText $domainRow.distinctClips "0"))
        }
        $simpleAnimationContextParts = @()
        foreach ($domainRow in @($sourceIndexSimpleAnimationClipDomains.domainCounts)) {
            $simpleAnimationContextParts += ('{0}=go:{1},animatorRows:{2},directRendererRows:{3}' -f `
                (ConvertTo-SmokeText $domainRow.domain),
                (ConvertTo-SmokeText $domainRow.distinctGameObjects "0"),
                (ConvertTo-SmokeText $domainRow.rowsWithAnimator "0"),
                (ConvertTo-SmokeText $domainRow.rowsWithDirectVisibleRenderer "0"))
        }
        $reportLines.Add(('- SimpleAnimation clip domains: totalRelations=`{0}`, domain relations/distinctClips=`{1}`' -f `
            (ConvertTo-SmokeText $sourceIndexSimpleAnimationClipDomains.totalRelations "0"),
            ($simpleAnimationDomainParts -join ', ')))
        $reportLines.Add(('- SimpleAnimation direct GameObject context: `{0}`' -f ($simpleAnimationContextParts -join '; ')))
        $shortlistPreview = @()
        foreach ($sample in @($sourceIndexSimpleAnimationClipDomains.probeShortlist | Select-Object -First 6)) {
            $shortlistPreview += ('{0}:{1}->{2}' -f `
                (ConvertTo-SmokeText $sample.domain),
                (ConvertTo-SmokeText $sample.gameObjectName ""),
                (ConvertTo-SmokeText $sample.clipName ""))
        }
        $reportLines.Add(('- SimpleAnimation probe shortlist: count=`{0}`, samples=`{1}`' -f `
            (ConvertTo-SmokeText $sourceIndexSimpleAnimationClipDomains.probeShortlistCount "0"),
            ($shortlistPreview -join ' | ')))
        $reportLines.Add('- SimpleAnimation domain rule: Character/HumanNameToken/MonsterOrNpc rows are useful next probes, but they remain script-field diagnostics until script semantics, deterministic model relation, TRS export and visual review are proven.')
    }
    else {
        $reportLines.Add(('- SimpleAnimation clip domains: `{0}`' -f $sourceIndexSimpleAnimationClipDomains.status))
    }
    if ($scriptAnimationComponentDiagnostics.status -eq "ok") {
        $hadiScript = $scriptAnimationComponentDiagnostics.hadiBody
        $jiantianshiScript = $scriptAnimationComponentDiagnostics.jiantianshiFormal
        $zhumuScript = $scriptAnimationComponentDiagnostics.zhumuSoul
        $fxScript = $scriptAnimationComponentDiagnostics.fxAttack
        $reportLines.Add(('- Selected-model script AnimationClip diagnostic: Hadi selected=`{0}`, candidates=`{1}`, scriptRows=`{2}`; Jiantianshi selected=`{3}`, candidates=`{4}`, scriptRows=`{5}`, invalidBoundaryRows=`{6}`; Zhumu selected=`{7}`, candidates=`{8}`, scriptRows=`{9}`, subtreeVisibleRows=`{10}`, invalidBoundaryRows=`{11}`; Fx selected=`{12}`, candidates=`{13}`, scriptRows=`{14}`, invalidBoundaryRows=`{15}`' -f `
            (ConvertTo-SmokeText $hadiScript.selectedModelCount "0"),
            (ConvertTo-SmokeText $hadiScript.candidateCount "0"),
            (ConvertTo-SmokeText $hadiScript.scriptAnimationRows "0"),
            (ConvertTo-SmokeText $jiantianshiScript.selectedModelCount "0"),
            (ConvertTo-SmokeText $jiantianshiScript.candidateCount "0"),
            (ConvertTo-SmokeText $jiantianshiScript.scriptAnimationRows "0"),
            (ConvertTo-SmokeText $jiantianshiScript.invalidBoundaryRows "0"),
            (ConvertTo-SmokeText $zhumuScript.selectedModelCount "0"),
            (ConvertTo-SmokeText $zhumuScript.candidateCount "0"),
            (ConvertTo-SmokeText $zhumuScript.scriptAnimationRows "0"),
            (ConvertTo-SmokeText $zhumuScript.subtreeVisibleRendererRows "0"),
            (ConvertTo-SmokeText $zhumuScript.invalidBoundaryRows "0"),
            (ConvertTo-SmokeText $fxScript.selectedModelCount "0"),
            (ConvertTo-SmokeText $fxScript.candidateCount "0"),
            (ConvertTo-SmokeText $fxScript.scriptAnimationRows "0"),
            (ConvertTo-SmokeText $fxScript.invalidBoundaryRows "0")))
        $reportLines.Add(('-   Fx first script=`{0}`, field=`{1}`, clip=`{2}`, visibleRendererRows=`{3}`, subtreeVisibleRows=`{4}`, subtreeSkinnedRows=`{5}`, subtreeTruncatedRows=`{6}`, animatorRows=`{7}`' -f `
            (ConvertTo-SmokeText $fxScript.firstScriptName ""),
            (ConvertTo-SmokeText $fxScript.firstFieldPath ""),
            (ConvertTo-SmokeText $fxScript.firstClipName ""),
            (ConvertTo-SmokeText $fxScript.visibleRendererRows "0"),
            (ConvertTo-SmokeText $fxScript.subtreeVisibleRendererRows "0"),
            (ConvertTo-SmokeText $fxScript.subtreeSkinnedRendererRows "0"),
            (ConvertTo-SmokeText $fxScript.subtreeTruncatedRows "0"),
            (ConvertTo-SmokeText $fxScript.animatorRows "0")))
        $reportLines.Add('- Selected-model script AnimationClip rule: Hadi and Jiantianshi prove visible/formal model selection is not promoted by name or skeleton alone; Zhumu and FxAttack prove local SimpleAnimation-style clip PPtrs and bounded subtree visibility are retained as diagnostic evidence only, not default model-animation candidates.')
    }
    else {
        $reportLines.Add(('- Selected-model script AnimationClip diagnostic: `{0}`' -f $scriptAnimationComponentDiagnostics.status))
    }
    if ($sourceModelAvatarDiagnostics.status -eq "ok") {
        $samuraiAvatar = $sourceModelAvatarDiagnostics.samuraiGhostCompatibility
        $jiantianshiAvatar = $sourceModelAvatarDiagnostics.jiantianshiFormalCompatibility
        $zhumuAvatar = $sourceModelAvatarDiagnostics.zhumuSoulCompatibility
        $reportLines.Add(('- Source-model Avatar/TOS diagnostics: status=`{0}`, Jiantianshi selected=`{1}`, candidates=`{2}`, modelAvatarRows=`{3}`, highOverlapRows=`{4}`, maxCoverage=`{5}`, invalidBoundaryRows=`{6}`; Zhumu selected=`{7}`, candidates=`{8}`, modelAvatarRows=`{9}`, highOverlapRows=`{10}`, maxCoverage=`{11}`, invalidBoundaryRows=`{12}`; Samurai selected=`{13}`, candidates=`{14}`, modelAvatarRows=`{15}`, highOverlapRows=`{16}`, maxCoverage=`{17}`, invalidBoundaryRows=`{18}`' -f `
            (ConvertTo-SmokeText $sourceModelAvatarDiagnostics.status),
            (ConvertTo-SmokeText $jiantianshiAvatar.selectedModelCount "0"),
            (ConvertTo-SmokeText $jiantianshiAvatar.candidateCount "0"),
            (ConvertTo-SmokeText $jiantianshiAvatar.modelAvatarRows "0"),
            (ConvertTo-SmokeText $jiantianshiAvatar.modelAvatarHighOverlapRows "0"),
            (ConvertTo-SmokeText $jiantianshiAvatar.modelAvatarMaxCoverage "0"),
            (ConvertTo-SmokeText $jiantianshiAvatar.invalidBoundaryRows "0"),
            (ConvertTo-SmokeText $zhumuAvatar.selectedModelCount "0"),
            (ConvertTo-SmokeText $zhumuAvatar.candidateCount "0"),
            (ConvertTo-SmokeText $zhumuAvatar.modelAvatarRows "0"),
            (ConvertTo-SmokeText $zhumuAvatar.modelAvatarHighOverlapRows "0"),
            (ConvertTo-SmokeText $zhumuAvatar.modelAvatarMaxCoverage "0"),
            (ConvertTo-SmokeText $zhumuAvatar.invalidBoundaryRows "0"),
            (ConvertTo-SmokeText $samuraiAvatar.selectedModelCount "0"),
            (ConvertTo-SmokeText $samuraiAvatar.candidateCount "0"),
            (ConvertTo-SmokeText $samuraiAvatar.modelAvatarRows "0"),
            (ConvertTo-SmokeText $samuraiAvatar.modelAvatarHighOverlapRows "0"),
            (ConvertTo-SmokeText $samuraiAvatar.modelAvatarMaxCoverage "0"),
            (ConvertTo-SmokeText $samuraiAvatar.invalidBoundaryRows "0")))
        if ($null -ne $sourceModelAvatarDiagnostics.dijiangTos -and $sourceModelAvatarDiagnostics.dijiangTos.status -ne "skipped") {
            $dijiangTos = $sourceModelAvatarDiagnostics.dijiangTos
            $reportLines.Add(('-   Dijiang TOS hash coverage: selected=`{0}`, candidates=`{1}`, avatarTosRows=`{2}`, fullCoverageRows=`{3}`, maxCoverage=`{4}`, unresolvedHashRows=`{5}`' -f `
                (ConvertTo-SmokeText $dijiangTos.selectedModelCount "0"),
                (ConvertTo-SmokeText $dijiangTos.candidateCount "0"),
                (ConvertTo-SmokeText $dijiangTos.avatarTosRows "0"),
                (ConvertTo-SmokeText $dijiangTos.avatarTosFullCoverageRows "0"),
                (ConvertTo-SmokeText $dijiangTos.avatarTosMaxCoverage "0"),
                (ConvertTo-SmokeText $dijiangTos.avatarTosUnresolvedHashRows "0")))
        }
        else {
            $reportLines.Add('-   Dijiang TOS hash coverage: `skipped` by -SkipAnimationDiagnostic')
        }
        $reportLines.Add('- Source-model Avatar/TOS rule: Avatar.m_TOS coverage and model-avatar path overlap are solver/oracle selection evidence only. They must stay defaultCandidateCount=0 and cannot enable production animation without explicit Unity animation context, model validation, TRS export and visual review.')
    }
    else {
        $reportLines.Add(('- Source-model Avatar/TOS diagnostics: `{0}`' -f $sourceModelAvatarDiagnostics.status))
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
    $reportLines.Add(('- Quality gates: textureLinkErrors=`{0}`, customShaderSidecars=`{1}`, layeredMaterialUnresolvedSidecars=`{2}`, degradedPreviewSidecars=`{3}`, customShaderReferenceSidecars=`{4}`, customShaderMissingNameSidecars=`{5}`' -f `
        (ConvertTo-SmokeText $shaderBoundarySummaryJson.qualityGates.textureLinkErrors "0"),
        (ConvertTo-SmokeText $shaderBoundarySummaryJson.qualityGates.customShaderRequiredSidecars "0"),
        (ConvertTo-SmokeText $shaderBoundarySummaryJson.qualityGates.layeredMaterialUnresolvedSidecars "0"),
        (ConvertTo-SmokeText $shaderBoundarySummaryJson.qualityGates.degradedPreviewSidecars "0"),
        (ConvertTo-SmokeText $shaderBoundarySummaryJson.qualityGates.customShaderReferenceSidecars "unknown"),
        (ConvertTo-SmokeText $shaderBoundarySummaryJson.qualityGates.customShaderMissingNameSidecars "unknown")))
    $reportLines.Add(('- material_sidecars rows=`{0}`, degraded custom shader rows=`{1}`' -f `
        (ConvertTo-SmokeText $shaderBoundaryMaterialSidecarRows "unknown"),
        (ConvertTo-SmokeText $shaderBoundaryCustomShaderRows "unknown")))
    $reportLines.Add(('- custom shader evidence rows=`{0}`, missing shader name rows=`{1}`' -f `
        (ConvertTo-SmokeText $shaderBoundaryCustomShaderEvidenceRows "unknown"),
        (ConvertTo-SmokeText $shaderBoundaryMissingShaderNameRows "unknown")))
    $reportLines.Add(('- shader reference rows=`{0}`, shader reference mode=`{1}`' -f `
        (ConvertTo-SmokeText $shaderBoundaryShaderReferenceRows "unknown"),
        (ConvertTo-SmokeText $shaderBoundaryShaderReferenceMode "unknown")))
    if (![string]::IsNullOrWhiteSpace($shaderBoundaryCustomShaderEvidenceSamples)) {
        $reportLines.Add(('- Evidence sample: `{0}`' -f $shaderBoundaryCustomShaderEvidenceSamples))
    }
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
$reportLines.Add("## Zhumu Script Animation Probe")
$reportLines.Add("")
$reportLines.Add(('- Status: `{0}`' -f $zhumuScriptAnimationStatus))
if ($zhumuScriptAnimationStatus -eq "ok") {
    $reportLines.Add(('- Sample root: `{0}`' -f $ZhumuScriptAnimationSampleRoot))
    $reportLines.Add(('- glTF: `{0}`' -f $zhumuScriptAnimationGltf))
    $reportLines.Add(('- glTF validator: `{0}`' -f $zhumuScriptAnimationGltfValidationStatus))
    $reportLines.Add(('- Model validation: models=`{0}`, ok=`{1}`, withSkin=`{2}`, withTextures=`{3}`, skinJoints=`{4}`, maxBoundsSize=`{5}`' -f `
        (ConvertTo-SmokeText $zhumuScriptAnimationValidationJson.totals.models "0"),
        (ConvertTo-SmokeText $zhumuScriptAnimationValidationJson.totals.ok "0"),
        (ConvertTo-SmokeText $zhumuScriptAnimationValidationJson.totals.withSkin "0"),
        (ConvertTo-SmokeText $zhumuScriptAnimationValidationJson.totals.withTextures "0"),
        (ConvertTo-SmokeText $zhumuScriptAnimationSkinJointCount "0"),
        (ConvertTo-SmokeText $zhumuScriptAnimationMaxBoundsSize "0")))
    $reportLines.Add(('- SQLite counts: textureAssets=`{0}`, textureLinks=`{1}`, textureLinkErrors=`{2}`, materialSidecars=`{3}`, modelRows=`{4}`, modelAnimationCandidates=`{5}`, modelAnimationRelations=`{6}`, relationAnimations=`{7}`' -f `
        (ConvertTo-SmokeText $zhumuScriptAnimationSummaryJson.counts.textureAssets "0"),
        (ConvertTo-SmokeText $zhumuScriptAnimationSummaryJson.counts.textureLinks "0"),
        (ConvertTo-SmokeText $zhumuScriptAnimationTextureLinkErrors "unknown"),
        (ConvertTo-SmokeText $zhumuScriptAnimationSummaryJson.counts.materialSidecars "0"),
        (ConvertTo-SmokeText $zhumuScriptAnimationModelRows "unknown"),
        (ConvertTo-SmokeText $zhumuScriptAnimationModelAnimationCandidateRows "unknown"),
        (ConvertTo-SmokeText $zhumuScriptAnimationModelAnimationRelationRows "unknown"),
        (ConvertTo-SmokeText $zhumuScriptAnimationRelationAnimationRows "unknown")))
    $reportLines.Add(('- Script diagnostic: selected=`{0}`, candidates=`{1}`, scriptRows=`{2}`, subtreeVisibleRows=`{3}`, subtreeSkinnedRows=`{4}`, invalidBoundaryRows=`{5}`, firstClip=`{6}`' -f `
        (ConvertTo-SmokeText $zhumuScriptAnimationDiagnostic.selectedModelCount "0"),
        (ConvertTo-SmokeText $zhumuScriptAnimationDiagnostic.candidateCount "0"),
        (ConvertTo-SmokeText $zhumuScriptAnimationDiagnostic.scriptAnimationRows "0"),
        (ConvertTo-SmokeText $zhumuScriptAnimationDiagnostic.subtreeVisibleRendererRows "0"),
        (ConvertTo-SmokeText $zhumuScriptAnimationDiagnostic.subtreeSkinnedRendererRows "0"),
        (ConvertTo-SmokeText $zhumuScriptAnimationDiagnostic.invalidBoundaryRows "0"),
        (ConvertTo-SmokeText $zhumuScriptAnimationDiagnostic.firstClipName "")))
    $reportLines.Add(('- Avatar compatibility diagnostic: selected=`{0}`, candidates=`{1}`, modelAvatarRows=`{2}`, highOverlapRows=`{3}`, maxCoverage=`{4}`, invalidBoundaryRows=`{5}`' -f `
        (ConvertTo-SmokeText $zhumuAvatarCompatibilityDiagnostic.selectedModelCount "0"),
        (ConvertTo-SmokeText $zhumuAvatarCompatibilityDiagnostic.candidateCount "0"),
        (ConvertTo-SmokeText $zhumuAvatarCompatibilityDiagnostic.modelAvatarRows "0"),
        (ConvertTo-SmokeText $zhumuAvatarCompatibilityDiagnostic.modelAvatarHighOverlapRows "0"),
        (ConvertTo-SmokeText $zhumuAvatarCompatibilityDiagnostic.modelAvatarMaxCoverage "0"),
        (ConvertTo-SmokeText $zhumuAvatarCompatibilityDiagnostic.invalidBoundaryRows "0")))
    $reportLines.Add('- Rule: Zhumu proves a stronger script-animation probe with a validated skinned model and visible child renderers, but SimpleAnimation semantics, TRS export and clear visual review are still missing. It must stay defaultCandidateCount=0 and cannot enable production animation capability.')
}
elseif ($zhumuScriptAnimationStatus -eq "missing") {
    $reportLines.Add(('- Sample root missing: `{0}`' -f $ZhumuScriptAnimationSampleRoot))
}
$reportLines.Add("")
$reportLines.Add("## Zhumu Merged Animation Preview")
$reportLines.Add("")
$reportLines.Add(('- Status: `{0}`' -f $zhumuMergedAnimationPreview.status))
if ($zhumuMergedAnimationPreview.status -eq "ok") {
    $reportLines.Add(('- Sample root: `{0}`' -f $zhumuMergedAnimationPreview.sampleRoot))
    $reportLines.Add(('- glTF: `{0}`' -f $zhumuMergedAnimationPreview.gltf))
    $reportLines.Add(('- glTF validator: `{0}`' -f $zhumuMergedAnimationPreview.gltfValidation))
    $reportLines.Add(('- Merge: animationCountAdded=`{0}`, channelCount=`{1}`, sourceAssessment=`{2}`' -f `
        (ConvertTo-SmokeText $zhumuMergedAnimationPreview.animationCountAdded "0"),
        (ConvertTo-SmokeText $zhumuMergedAnimationPreview.channelCount "0"),
        (ConvertTo-SmokeText $zhumuMergedAnimationPreview.sourceAssessmentStatus "unknown")))
    $reportLines.Add(('- Skin joint coverage: animated=`{0}` / total=`{1}`, coverage=`{2}`' -f `
        (ConvertTo-SmokeText $zhumuMergedAnimationPreview.animationJointCoverage.animatedSkinJointCount "0"),
        (ConvertTo-SmokeText $zhumuMergedAnimationPreview.animationJointCoverage.skinJointCount "0"),
        (ConvertTo-SmokeText $zhumuMergedAnimationPreview.animationJointCoverage.animatedSkinJointCoverage "0")))
    $reportLines.Add(('- Vertex weight coverage: animatedVertices=`{0}` / weightedVertices=`{1}`, vertexCoverage=`{2}`, weightCoverage=`{3}`' -f `
        (ConvertTo-SmokeText $zhumuMergedAnimationPreview.animationJointCoverage.vertexWeightCoverage.animatedWeightedVertexCount "0"),
        (ConvertTo-SmokeText $zhumuMergedAnimationPreview.animationJointCoverage.vertexWeightCoverage.weightedVertexCount "0"),
        (ConvertTo-SmokeText $zhumuMergedAnimationPreview.animationJointCoverage.vertexWeightCoverage.animatedWeightedVertexCoverage "0"),
        (ConvertTo-SmokeText $zhumuMergedAnimationPreview.animationJointCoverage.vertexWeightCoverage.animatedWeightCoverage "0")))
    $reportLines.Add(('- Diagnostic reasons: `{0}`' -f (($zhumuMergedAnimationPreview.reasonCodes | ForEach-Object { [string]$_ }) -join ', ')))
    $reportLines.Add(('- Production readiness: `{0}`; blocked requirements: `{1}`' -f `
        (ConvertTo-SmokeText $zhumuMergedAnimationPreview.productionReadiness "unknown"),
        (($zhumuMergedAnimationPreview.blockedProductionRequirements | ForEach-Object { [string]$_ }) -join ', ')))
    $reportLines.Add(('- Render probe: status=`{0}`, frames=`{1}`' -f `
        (ConvertTo-SmokeText $zhumuMergedAnimationPreview.renderProbeStatus "unknown"),
        (ConvertTo-SmokeText $zhumuMergedAnimationPreview.renderFrameCount "0")))
    $reportLines.Add(('- Render bbox motion: status=`{0}`, maxCoordinateDelta=`{1}`' -f `
        (ConvertTo-SmokeText $zhumuMergedAnimationPreview.renderBboxMotion.status "unknown"),
        (ConvertTo-SmokeText $zhumuMergedAnimationPreview.renderBboxMotion.maxCoordinateDelta "0")))
    $reportLines.Add(('- Render subject occupancy: status=`{0}`, minPixelRatio=`{1}`, minHeightRatio=`{2}`' -f `
        (ConvertTo-SmokeText $zhumuMergedAnimationPreview.renderSubjectOccupancy.status "unknown"),
        (ConvertTo-SmokeText $zhumuMergedAnimationPreview.renderSubjectOccupancy.minForegroundPixelRatio "0"),
        (ConvertTo-SmokeText $zhumuMergedAnimationPreview.renderSubjectOccupancy.minForegroundHeightRatio "0")))
    $reportLines.Add('- Rule: merged model+animation glTF proves the diagnostic composition path can open and render, but it remains needs_review because the internal Humanoid/Muscle solver and low channel coverage are not production-validated.')
}
elseif ($zhumuMergedAnimationPreview.status -eq "missing") {
    $reportLines.Add(('- Sample root missing: `{0}`' -f $ZhumuMergedAnimationProbeRoot))
}
$reportLines.Add("")
$reportLines.Add("## Animation Diagnostic")
$reportLines.Add("")
if ($null -ne $animationReportJson) {
    $reportLines.Add(('- Diagnostic status: `{0}`' -f (ConvertTo-SmokeText $animationReportJson.status)))
    $reportLines.Add(("- Message: {0}" -f (ConvertTo-SmokeText $animationReportJson.message)))
    $reportLines.Add(('- Animation glTF: `{0}`' -f (ConvertTo-SmokeText $animationGltfPath)))
    $reportLines.Add(('- Animation glTF validator: `{0}`' -f $animationGltfValidationStatus))
    $reportLines.Add(('- Production readiness: `blocked`; blocked requirements: `{0}`' -f (($animationDiagnosticBlockedRequirements | ForEach-Object { [string]$_ }) -join ', ')))
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
$reportLines.Add(('- Jiantianshi formal source-model diagnostic JSON: `{0}`' -f $scriptAnimationComponentDiagnostics.jiantianshiFormal.report))
$reportLines.Add(('- Zhumu script/avatar diagnostic JSON: `{0}`' -f $scriptAnimationComponentDiagnostics.zhumuSoul.report))
$reportLines.Add(('- FxAttack script animation diagnostic JSON: `{0}`' -f $scriptAnimationComponentDiagnostics.fxAttack.report))
if ($sourceModelAvatarDiagnostics.status -eq "ok") {
    if ($null -ne $sourceModelAvatarDiagnostics.dijiangTos -and $sourceModelAvatarDiagnostics.dijiangTos.status -ne "skipped") {
        $reportLines.Add(('- Dijiang Avatar/TOS diagnostic JSON: `{0}`' -f $sourceModelAvatarDiagnostics.dijiangTos.report))
    }
    $reportLines.Add(('- Zhumu Avatar compatibility diagnostic JSON: `{0}`' -f $sourceModelAvatarDiagnostics.zhumuSoulCompatibility.report))
    $reportLines.Add(('- SamuraiGhost Avatar compatibility diagnostic JSON: `{0}`' -f $sourceModelAvatarDiagnostics.samuraiGhostCompatibility.report))
}
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
if ($zhumuScriptAnimationStatus -eq "ok") {
    $reportLines.Add(('- Zhumu script-animation probe glTF: `{0}`' -f $zhumuScriptAnimationGltf))
    $reportLines.Add(('- Zhumu script-animation probe SQLite summary: `{0}`' -f (Join-Path $ZhumuScriptAnimationSampleRoot "sqlite_index_summary.json")))
}
if ($zhumuMergedAnimationPreview.status -eq "ok") {
    $reportLines.Add(('- Zhumu merged animation preview glTF: `{0}`' -f $zhumuMergedAnimationPreview.gltf))
    $reportLines.Add(('- Zhumu merged animation preview report: `{0}`' -f $zhumuMergedAnimationPreview.report))
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
