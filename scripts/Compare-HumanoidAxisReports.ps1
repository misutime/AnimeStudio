param(
    [Parameter(Mandatory = $true)]
    [string[]]$ReportPath,

    [string]$OutputDir,
    [int]$Top = 32
)

$ErrorActionPreference = "Stop"

if ($ReportPath.Count -lt 2) {
    throw "Please pass at least two compare report JSON files."
}

foreach ($path in $ReportPath) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Report does not exist: $path"
    }
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $firstDir = Split-Path -Parent $ReportPath[0]
    $OutputDir = Join-Path $firstDir "HumanoidAxisReportCompare"
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

function Read-JsonFile([string]$Path) {
    # 报告里有中文诊断说明。Windows PowerShell 5.1 默认按 ANSI 读无 BOM UTF-8，
    # 会把中文读坏，甚至让 JSON 字符串失去闭合，所以这里固定走 UTF-8。
    return [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
}

function Number-OrZero($Value) {
    if ($null -eq $Value) {
        return 0.0
    }
    return [double]$Value
}

function Text-OrEmpty($Value) {
    if ($null -eq $Value) {
        return ""
    }
    return [string]$Value
}

function Format-TemplateTargets($Value) {
    if ($null -eq $Value) {
        return ""
    }

    $items = @($Value)
    return (($items | ForEach-Object {
        $target = Text-OrEmpty $_.targetHumanBone
        $muscle = Text-OrEmpty $_.muscleName
        $error = [math]::Round((Number-OrZero $_.errorDegrees), 4)
        "$target/$muscle=$error"
    }) -join "; ")
}

function Layout-NumberOrEmpty($Layout, [string]$Name) {
    if ($null -eq $Layout -or $null -eq $Layout.$Name) {
        return ""
    }
    return [math]::Round((Number-OrZero $Layout.$Name), 4)
}

function Layout-IntOrEmpty($Layout, [string]$Name) {
    if ($null -eq $Layout -or $null -eq $Layout.$Name) {
        return ""
    }
    return [int](Number-OrZero $Layout.$Name)
}

function Layout-BoolOrEmpty($Layout, [string]$Name) {
    if ($null -eq $Layout -or $null -eq $Layout.$Name) {
        return ""
    }
    return [bool]$Layout.$Name
}

function Get-ReportLabel($Json, [string]$Path) {
    $clip = Text-OrEmpty $Json.clipName
    $model = Text-OrEmpty $Json.unityBakeRequest.modelName
    if (-not [string]::IsNullOrWhiteSpace($model) -and -not [string]::IsNullOrWhiteSpace($clip)) {
        return "$model / $clip"
    }
    if (-not [string]::IsNullOrWhiteSpace($clip)) {
        return $clip
    }
    return [System.IO.Path]::GetFileNameWithoutExtension($Path)
}

function Get-AxisSummary($Json) {
    return $Json.singleMuscleProbeSolverComparison.singleMuscleDeltaAxisSummary.signedAxisTiltSummary
}

function Get-SingleMuscleFormulaGate($Json) {
    $gate = $Json.singleMuscleProbeSolverComparison.singleMuscleFormulaDeltaSideSummary.gateSummary
    if ($null -ne $gate) {
        return $gate
    }
    return $Json.internalHumanoidSolverVariantComparison.singleMuscleProbeSolverComparison.singleMuscleFormulaDeltaSideSummary.gateSummary
}

function Get-ForearmStretchScaleProbe($Json) {
    $summary = $Json.singleMuscleProbeSolverComparison.forearmStretchScaleProbeSummary
    if ($null -ne $summary) {
        return $summary
    }
    return $Json.internalHumanoidSolverVariantComparison.singleMuscleProbeSolverComparison.forearmStretchScaleProbeSummary
}

function Is-PoseCandidate([string]$Name) {
    return -not [string]::IsNullOrWhiteSpace($Name) -and (
        $Name.IndexOf("Pose", [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Name.IndexOf("ParentToChild", [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Name.IndexOf("ChildToParent", [System.StringComparison]::OrdinalIgnoreCase) -ge 0)
}

function Format-Vector3($Value) {
    if ($null -eq $Value) {
        return ""
    }
    $items = @($Value)
    if ($items.Count -lt 3) {
        return ""
    }
    return ("{0:F6},{1:F6},{2:F6}" -f [double]$items[0], [double]$items[1], [double]$items[2])
}

function Get-VectorComponent($Value, [int]$Index) {
    if ($null -eq $Value) {
        return 0.0
    }
    $items = @($Value)
    if ($items.Count -le $Index) {
        return 0.0
    }
    return [math]::Round([double]$items[$Index], 6)
}

function Get-OffAxisSummary($Value, [string]$NearestAxis) {
    $x = Get-VectorComponent $Value 0
    $y = Get-VectorComponent $Value 1
    $z = Get-VectorComponent $Value 2
    switch ($NearestAxis) {
        "+X" { return "Y=$y; Z=$z" }
        "-X" { return "Y=$y; Z=$z" }
        "+Y" { return "X=$x; Z=$z" }
        "-Y" { return "X=$x; Z=$z" }
        "+Z" { return "X=$x; Y=$y" }
        "-Z" { return "X=$x; Y=$y" }
        default { return "X=$x; Y=$y; Z=$z" }
    }
}

function Write-MarkdownTable($Rows, [string[]]$Columns) {
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("| " + ($Columns -join " | ") + " |")
    $lines.Add("| " + (($Columns | ForEach-Object { "---" }) -join " | ") + " |")
    foreach ($row in $Rows) {
        $values = foreach ($column in $Columns) {
            $value = $row.$column
            if ($null -eq $value) {
                ""
            } else {
                ([string]$value).Replace("|", "\|")
            }
        }
        $lines.Add("| " + ($values -join " | ") + " |")
    }
    return $lines
}

function Add-MarkdownLines($Target, $Lines) {
    foreach ($line in $Lines) {
        $Target.Add([string]$line)
    }
}

$loaded = foreach ($path in $ReportPath) {
    $json = Read-JsonFile $path
    [pscustomobject]@{
        Path = $path
        Label = Get-ReportLabel $json $path
        Json = $json
        Axis = Get-AxisSummary $json
    }
}

$reportSummaries = foreach ($item in $loaded) {
    $pose = $item.Axis.axisSourceCandidateSummary.poseAxisCandidateStatus
    [pscustomobject]@{
        label = $item.Label
        path = $item.Path
        status = Text-OrEmpty $item.Axis.status
        groupCount = [int](Number-OrZero $item.Axis.groupCount)
        avgAxisTiltDegrees = [math]::Round((Number-OrZero $item.Axis.avgAxisTiltDegrees), 4)
        maxAxisTiltDegrees = [math]::Round((Number-OrZero $item.Axis.maxAxisTiltDegrees), 4)
        poseCandidateRowCount = [int](Number-OrZero $pose.poseCandidateRowCount)
        poseCandidateRate = [math]::Round((Number-OrZero $pose.poseCandidateRate), 4)
        focusPoseCandidateRate = [math]::Round((Number-OrZero $pose.focusPoseCandidateRate), 4)
        armPoseCandidateRate = [math]::Round((Number-OrZero $pose.armPoseCandidateRate), 4)
        avgPoseCandidateErrorDegrees = [math]::Round((Number-OrZero $pose.avgPoseCandidateErrorDegrees), 4)
        maxPoseCandidateErrorDegrees = [math]::Round((Number-OrZero $pose.maxPoseCandidateErrorDegrees), 4)
    }
}

$familyRows = foreach ($item in $loaded) {
    foreach ($row in @($item.Axis.axisSourceCandidateSummary.byBodyGroupAndMuscleFamily)) {
        [pscustomobject]@{
            label = $item.Label
            bodyGroup = Text-OrEmpty $row.bodyGroup
            muscleFamily = Text-OrEmpty $row.muscleFamily
            key = (Text-OrEmpty $row.bodyGroup) + "|" + (Text-OrEmpty $row.muscleFamily)
            rowCount = [int](Number-OrZero $row.rowCount)
            avgCandidateErrorDegrees = [math]::Round((Number-OrZero $row.avgCandidateErrorDegrees), 4)
            maxCandidateErrorDegrees = [math]::Round((Number-OrZero $row.maxCandidateErrorDegrees), 4)
            avgAxisTiltDegrees = [math]::Round((Number-OrZero $row.avgAxisTiltDegrees), 4)
            maxAxisTiltDegrees = [math]::Round((Number-OrZero $row.maxAxisTiltDegrees), 4)
            dominantNearestCandidate = Text-OrEmpty $row.dominantNearestCandidate
            worstHumanBone = Text-OrEmpty $row.worstHumanBone
            worstAttribute = Text-OrEmpty $row.worstAttribute
        }
    }
}

$commonFamilies = $familyRows |
    Group-Object key |
    Where-Object { $_.Count -ge 2 } |
    ForEach-Object {
        $items = @($_.Group)
        $first = $items[0]
        [pscustomobject]@{
            bodyGroup = $first.bodyGroup
            muscleFamily = $first.muscleFamily
            reportCount = $items.Count
            avgCandidateErrorDegrees = [math]::Round((($items | Measure-Object avgCandidateErrorDegrees -Average).Average), 4)
            maxCandidateErrorDegrees = [math]::Round((($items | Measure-Object maxCandidateErrorDegrees -Maximum).Maximum), 4)
            maxAxisTiltDegrees = [math]::Round((($items | Measure-Object maxAxisTiltDegrees -Maximum).Maximum), 4)
            dominantCandidates = (($items | ForEach-Object { "$($_.label):$($_.dominantNearestCandidate)" }) -join "; ")
            worstAttributes = (($items | ForEach-Object { "$($_.label):$($_.worstHumanBone)/$($_.worstAttribute)" }) -join "; ")
        }
    } |
    Sort-Object @{ Expression = "avgCandidateErrorDegrees"; Descending = $true }, @{ Expression = "maxAxisTiltDegrees"; Descending = $true }

$axisRows = foreach ($item in $loaded) {
    foreach ($row in @($item.Axis.axisSourceCandidateSummary.topUnexplainedAxes)) {
        $humanBone = Text-OrEmpty $row.humanBone
        $attribute = Text-OrEmpty $row.attribute
        [pscustomobject]@{
            label = $item.Label
            key = "$humanBone|$attribute"
            humanBone = $humanBone
            attribute = $attribute
            bodyGroup = Text-OrEmpty $row.bodyGroup
            nearestAxisSourceCandidate = Text-OrEmpty $row.nearestAxisSourceCandidate
            nearestAxisSourceErrorDegrees = [math]::Round((Number-OrZero $row.nearestAxisSourceErrorDegrees), 4)
            axisTiltDegrees = [math]::Round((Number-OrZero $row.axisTiltDegrees), 4)
            unityNearestSignedAxis = Text-OrEmpty $row.unityNearestSignedAxis
            predictedNearestSignedAxis = Text-OrEmpty $row.predictedNearestSignedAxis
            poseCandidate = Is-PoseCandidate (Text-OrEmpty $row.nearestAxisSourceCandidate)
        }
    }
}

$commonAxisBlockers = $axisRows |
    Group-Object key |
    Where-Object { $_.Count -ge 2 } |
    ForEach-Object {
        $items = @($_.Group)
        $first = $items[0]
        [pscustomobject]@{
            humanBone = $first.humanBone
            attribute = $first.attribute
            bodyGroup = $first.bodyGroup
            reportCount = $items.Count
            avgCandidateErrorDegrees = [math]::Round((($items | Measure-Object nearestAxisSourceErrorDegrees -Average).Average), 4)
            maxCandidateErrorDegrees = [math]::Round((($items | Measure-Object nearestAxisSourceErrorDegrees -Maximum).Maximum), 4)
            maxAxisTiltDegrees = [math]::Round((($items | Measure-Object axisTiltDegrees -Maximum).Maximum), 4)
            poseCandidateReportCount = @($items | Where-Object { $_.poseCandidate }).Count
            candidates = (($items | ForEach-Object { "$($_.label):$($_.nearestAxisSourceCandidate)[$($_.nearestAxisSourceErrorDegrees)]" }) -join "; ")
        }
    } |
    Sort-Object @{ Expression = "avgCandidateErrorDegrees"; Descending = $true }, @{ Expression = "maxAxisTiltDegrees"; Descending = $true } |
    Select-Object -First $Top

$formulaReadinessRows = foreach ($item in $loaded) {
    foreach ($row in @($item.Json.singleMuscleProbeSolverComparison.formulaReadinessSummary.rows)) {
        $targetHumanBone = Text-OrEmpty $row.targetHumanBone
        $muscleName = Text-OrEmpty $row.muscleName
        if ([string]::IsNullOrWhiteSpace($targetHumanBone) -or [string]::IsNullOrWhiteSpace($muscleName)) {
            continue
        }
        [pscustomobject]@{
            label = $item.Label
            key = "$targetHumanBone|$muscleName"
            targetHumanBone = $targetHumanBone
            muscleName = $muscleName
            bodyGroup = Text-OrEmpty $row.bodyGroup
            muscleFamily = Text-OrEmpty $row.muscleFamily
            readiness = Text-OrEmpty $row.readiness
            maxCurrentErrorDegrees = [math]::Round((Number-OrZero $row.maxCurrentErrorDegrees), 4)
            minBestEnumeratedErrorDegrees = [math]::Round((Number-OrZero $row.minBestEnumeratedErrorDegrees), 4)
            avgImprovementDegrees = [math]::Round((Number-OrZero $row.avgImprovementDegrees), 4)
            limitExplainedRate = [math]::Round((Number-OrZero $row.limitExplainedRate), 4)
            axisStableRate = [math]::Round((Number-OrZero $row.axisStableRate), 4)
            sourceChangedRate = [math]::Round((Number-OrZero $row.sourceChangedRate), 4)
            sourceAxisChangedRate = [math]::Round((Number-OrZero $row.sourceAxisChangedRate), 4)
            targetAxisChangedRate = [math]::Round((Number-OrZero $row.targetAxisChangedRate), 4)
            unityNearestAxis = Text-OrEmpty $row.unityNearestAxis
            predictedNearestAxis = Text-OrEmpty $row.predictedNearestAxis
            nearestAxisSourceCandidate = Text-OrEmpty $row.nearestAxisSourceCandidate
            nearestAxisSourceErrorDegrees = [math]::Round((Number-OrZero $row.nearestAxisSourceErrorDegrees), 4)
            axisTiltDegrees = [math]::Round((Number-OrZero $row.axisTiltDegrees), 4)
            unityAxisSpreadDegrees = [math]::Round((Number-OrZero $row.unityAxisSpreadDegrees), 4)
            bestSourceHumanBoneVotes = (($row.bestSourceHumanBoneVotes | ForEach-Object { "$(Text-OrEmpty $_.value):$([int](Number-OrZero $_.count))" }) -join "; ")
            bestSourceAxisVotes = (($row.bestSourceAxisVotes | ForEach-Object { "$(Text-OrEmpty $_.value):$([int](Number-OrZero $_.count))" }) -join "; ")
            bestTargetAxisVotes = (($row.bestTargetAxisVotes | ForEach-Object { "$(Text-OrEmpty $_.value):$([int](Number-OrZero $_.count))" }) -join "; ")
            limitHumanBoneVotes = (($row.linearityLimitHumanBoneVotes | ForEach-Object { "$(Text-OrEmpty $_.value):$([int](Number-OrZero $_.count))" }) -join "; ")
            limitAvatarAxisVotes = (($row.linearityLimitAvatarAxisVotes | ForEach-Object { "$(Text-OrEmpty $_.value):$([int](Number-OrZero $_.count))" }) -join "; ")
        }
    }
}

$commonFormulaReadinessRows = $formulaReadinessRows |
    Group-Object key |
    Where-Object { $_.Count -ge 2 } |
    ForEach-Object {
        $items = @($_.Group)
        $first = $items[0]
        $maxCurrentError = [math]::Round((($items | Measure-Object maxCurrentErrorDegrees -Maximum).Maximum), 4)
        [pscustomobject]@{
            targetHumanBone = $first.targetHumanBone
            muscleName = $first.muscleName
            bodyGroup = $first.bodyGroup
            muscleFamily = $first.muscleFamily
            reportCount = $items.Count
            readinesses = (($items | ForEach-Object { "$($_.label):$($_.readiness)" }) -join "; ")
            allReadyForFormulaProbe = -not @($items | Where-Object { $_.readiness -ne "ready_for_formula_probe" }).Count
            allNeedAxisFormulaOrBetter = -not @($items | Where-Object { $_.readiness -notin @("ready_for_formula_probe", "needs_axis_formula_probe") }).Count
            maxCurrentErrorDegrees = $maxCurrentError
            maxBestEnumeratedErrorDegrees = [math]::Round((($items | Measure-Object minBestEnumeratedErrorDegrees -Maximum).Maximum), 4)
            minAvgImprovementDegrees = [math]::Round((($items | Measure-Object avgImprovementDegrees -Minimum).Minimum), 4)
            minLimitExplainedRate = [math]::Round((($items | Measure-Object limitExplainedRate -Minimum).Minimum), 4)
            minAxisStableRate = [math]::Round((($items | Measure-Object axisStableRate -Minimum).Minimum), 4)
            maxUnityAxisSpreadDegrees = [math]::Round((($items | Measure-Object unityAxisSpreadDegrees -Maximum).Maximum), 4)
            maxNearestAxisSourceErrorDegrees = [math]::Round((($items | Measure-Object nearestAxisSourceErrorDegrees -Maximum).Maximum), 4)
            unityNearestAxes = (($items | ForEach-Object { "$($_.label):$($_.unityNearestAxis)" }) -join "; ")
            bestSourceHumanBoneVotes = (($items | ForEach-Object { "$($_.label):$($_.bestSourceHumanBoneVotes)" }) -join "; ")
            bestSourceAxisVotes = (($items | ForEach-Object { "$($_.label):$($_.bestSourceAxisVotes)" }) -join "; ")
            bestTargetAxisVotes = (($items | ForEach-Object { "$($_.label):$($_.bestTargetAxisVotes)" }) -join "; ")
        }
    } |
    Where-Object { $_.maxCurrentErrorDegrees -gt 15 } |
    Sort-Object @{ Expression = "allReadyForFormulaProbe"; Descending = $true }, @{ Expression = "allNeedAxisFormulaOrBetter"; Descending = $true }, @{ Expression = "maxBestEnumeratedErrorDegrees"; Descending = $false }, @{ Expression = "maxCurrentErrorDegrees"; Descending = $true } |
    Select-Object -First $Top

$axisSpaceRows = foreach ($item in $loaded) {
    $rows = @($item.Json.singleMuscleProbeSolverComparison.formulaHintSummary.topAxisSpaceBlockers)
    $uniqueRows = $rows |
        Group-Object { (Text-OrEmpty $_.targetHumanBone) + "|" + (Text-OrEmpty $_.muscleName) } |
        ForEach-Object {
            $_.Group |
                Sort-Object @{ Expression = { Number-OrZero $_.currentErrorDegrees }; Descending = $true }, @{ Expression = { Number-OrZero $_.bestEnumeratedErrorDegrees }; Descending = $true } |
                Select-Object -First 1
        }
    foreach ($row in $uniqueRows) {
        $bone = Text-OrEmpty $row.targetHumanBone
        $muscle = Text-OrEmpty $row.muscleName
        [pscustomobject]@{
            label = $item.Label
            key = "$bone|$muscle"
            targetHumanBone = $bone
            muscleName = $muscle
            currentErrorDegrees = [math]::Round((Number-OrZero $row.currentErrorDegrees), 4)
            bestEnumeratedErrorDegrees = [math]::Round((Number-OrZero $row.bestEnumeratedErrorDegrees), 4)
            linearityLimitExplained = [bool]$row.linearityLimitExplained
            linearityAxisStable = [bool]$row.linearityAxisStable
            bestSourceHumanBone = Text-OrEmpty $row.bestSourceHumanBone
            bestTargetAxisName = Text-OrEmpty $row.bestTargetAxisName
            bestSourceAxisName = Text-OrEmpty $row.bestSourceAxisName
        }
    }
}

$commonAxisSpaceBlockers = $axisSpaceRows |
    Group-Object key |
    Where-Object { $_.Count -ge 2 } |
    ForEach-Object {
        $items = @($_.Group)
        $first = $items[0]
        [pscustomobject]@{
            targetHumanBone = $first.targetHumanBone
            muscleName = $first.muscleName
            reportCount = $items.Count
            avgCurrentErrorDegrees = [math]::Round((($items | Measure-Object currentErrorDegrees -Average).Average), 4)
            maxCurrentErrorDegrees = [math]::Round((($items | Measure-Object currentErrorDegrees -Maximum).Maximum), 4)
            avgBestEnumeratedErrorDegrees = [math]::Round((($items | Measure-Object bestEnumeratedErrorDegrees -Average).Average), 4)
            allLimitExplained = -not @($items | Where-Object { -not $_.linearityLimitExplained }).Count
            allAxisStable = -not @($items | Where-Object { -not $_.linearityAxisStable }).Count
            bestHints = (($items | ForEach-Object { "$($_.label):$($_.bestSourceHumanBone)->$($_.bestTargetAxisName)/$($_.bestSourceAxisName)" }) -join "; ")
        }
    } |
    Sort-Object @{ Expression = "avgCurrentErrorDegrees"; Descending = $true } |
    Select-Object -First $Top

$rawAxisRowsByLabelKey = @{}
foreach ($item in $loaded) {
    foreach ($row in @($item.Axis.topAxisTilts)) {
        $humanBone = Text-OrEmpty $row.humanBone
        $attribute = Text-OrEmpty $row.attribute
        if ([string]::IsNullOrWhiteSpace($humanBone) -or [string]::IsNullOrWhiteSpace($attribute)) {
            continue
        }
        $rawAxisRowsByLabelKey["$($item.Label)|$humanBone|$attribute"] = $row
    }
}

$axisSpaceRowsByLabelKey = @{}
foreach ($row in $axisSpaceRows) {
    $axisSpaceRowsByLabelKey["$($row.label)|$($row.key)"] = $row
}

$commonAxisSpaceDetailRows = foreach ($blocker in $commonAxisSpaceBlockers) {
    foreach ($item in $loaded) {
        $key = "$($blocker.targetHumanBone)|$($blocker.muscleName)"
        $axisKey = "$($item.Label)|$key"
        $axis = $rawAxisRowsByLabelKey[$axisKey]
        $hint = $axisSpaceRowsByLabelKey[$axisKey]
        [pscustomobject]@{
            label = $item.Label
            targetHumanBone = $blocker.targetHumanBone
            muscleName = $blocker.muscleName
            bodyGroup = if ($axis) { Text-OrEmpty $axis.bodyGroup } else { "" }
            predictedNearestSignedAxis = if ($axis) { Text-OrEmpty $axis.predictedNearestSignedAxis } else { "" }
            unityNearestSignedAxis = if ($axis) { Text-OrEmpty $axis.unityNearestSignedAxis } else { "" }
            predictedAxis = if ($axis) { Format-Vector3 $axis.predictedAxis } else { "" }
            unityAxis = if ($axis) { Format-Vector3 $axis.unityAxis } else { "" }
            unityAxisX = if ($axis) { Get-VectorComponent $axis.unityAxis 0 } else { 0.0 }
            unityAxisY = if ($axis) { Get-VectorComponent $axis.unityAxis 1 } else { 0.0 }
            unityAxisZ = if ($axis) { Get-VectorComponent $axis.unityAxis 2 } else { 0.0 }
            unityOffAxisSummary = if ($axis) { Get-OffAxisSummary $axis.unityAxis (Text-OrEmpty $axis.unityNearestSignedAxis) } else { "" }
            nearestAxisSourceCandidate = if ($axis) { Text-OrEmpty $axis.nearestAxisSourceCandidate } else { "" }
            nearestAxisSourceErrorDegrees = if ($axis) { [math]::Round((Number-OrZero $axis.nearestAxisSourceErrorDegrees), 4) } else { 0.0 }
            axisTiltDegrees = if ($axis) { [math]::Round((Number-OrZero $axis.axisTiltDegrees), 4) } else { 0.0 }
            unityAxisSpreadDegrees = if ($axis) { [math]::Round((Number-OrZero $axis.unityAxisSpreadDegrees), 4) } else { 0.0 }
            currentErrorDegrees = if ($hint) { $hint.currentErrorDegrees } else { 0.0 }
            bestEnumeratedErrorDegrees = if ($hint) { $hint.bestEnumeratedErrorDegrees } else { 0.0 }
            bestSourceHumanBone = if ($hint) { $hint.bestSourceHumanBone } else { "" }
            bestTargetAxisName = if ($hint) { $hint.bestTargetAxisName } else { "" }
            bestSourceAxisName = if ($hint) { $hint.bestSourceAxisName } else { "" }
            linearityLimitExplained = if ($hint) { $hint.linearityLimitExplained } else { $false }
            linearityAxisStable = if ($hint) { $hint.linearityAxisStable } else { $false }
        }
    }
}

$offAxisPatternRows = $commonAxisSpaceDetailRows |
    Group-Object targetHumanBone, muscleName |
    ForEach-Object {
        $items = @($_.Group)
        $first = $items[0]
        [pscustomobject]@{
            targetHumanBone = $first.targetHumanBone
            muscleName = $first.muscleName
            reportCount = $items.Count
            nearestAxes = (($items | ForEach-Object { "$($_.label):$($_.unityNearestSignedAxis)" }) -join "; ")
            meanUnityAxisX = [math]::Round((($items | Measure-Object unityAxisX -Average).Average), 6)
            meanUnityAxisY = [math]::Round((($items | Measure-Object unityAxisY -Average).Average), 6)
            meanUnityAxisZ = [math]::Round((($items | Measure-Object unityAxisZ -Average).Average), 6)
            minUnityAxisX = [math]::Round((($items | Measure-Object unityAxisX -Minimum).Minimum), 6)
            maxUnityAxisX = [math]::Round((($items | Measure-Object unityAxisX -Maximum).Maximum), 6)
            minUnityAxisY = [math]::Round((($items | Measure-Object unityAxisY -Minimum).Minimum), 6)
            maxUnityAxisY = [math]::Round((($items | Measure-Object unityAxisY -Maximum).Maximum), 6)
            minUnityAxisZ = [math]::Round((($items | Measure-Object unityAxisZ -Minimum).Minimum), 6)
            maxUnityAxisZ = [math]::Round((($items | Measure-Object unityAxisZ -Maximum).Maximum), 6)
            candidates = (($items | ForEach-Object { "$($_.label):$($_.nearestAxisSourceCandidate)" }) -join "; ")
        }
    } |
    Sort-Object muscleName, targetHumanBone

$offAxisFamilyRows = $commonAxisSpaceDetailRows |
    Group-Object muscleName |
    ForEach-Object {
        $items = @($_.Group)
        [pscustomobject]@{
            muscleName = $_.Name
            rowCount = $items.Count
            nearestAxes = (($items | ForEach-Object { "$($_.targetHumanBone):$($_.unityNearestSignedAxis)" } | Select-Object -Unique) -join "; ")
            meanUnityAxisX = [math]::Round((($items | Measure-Object unityAxisX -Average).Average), 6)
            meanUnityAxisY = [math]::Round((($items | Measure-Object unityAxisY -Average).Average), 6)
            meanUnityAxisZ = [math]::Round((($items | Measure-Object unityAxisZ -Average).Average), 6)
            minUnityAxisX = [math]::Round((($items | Measure-Object unityAxisX -Minimum).Minimum), 6)
            maxUnityAxisX = [math]::Round((($items | Measure-Object unityAxisX -Maximum).Maximum), 6)
            minUnityAxisY = [math]::Round((($items | Measure-Object unityAxisY -Minimum).Minimum), 6)
            maxUnityAxisY = [math]::Round((($items | Measure-Object unityAxisY -Maximum).Maximum), 6)
            minUnityAxisZ = [math]::Round((($items | Measure-Object unityAxisZ -Minimum).Minimum), 6)
            maxUnityAxisZ = [math]::Round((($items | Measure-Object unityAxisZ -Maximum).Maximum), 6)
        }
    } |
    Sort-Object muscleName

$armFormulaTimelineRows = foreach ($item in $loaded) {
    $gate = $item.Json.internalHumanoidSolverVariantComparison.armFormulaTimelineGateSummary
    foreach ($row in @($gate.rows)) {
        if ($null -eq $row) {
            continue
        }
        $family = Text-OrEmpty $row.family
        $candidateName = Text-OrEmpty $row.candidateName
        if ([string]::IsNullOrWhiteSpace($family) -or [string]::IsNullOrWhiteSpace($candidateName)) {
            continue
        }
        [pscustomobject]@{
            label = $item.Label
            key = "$family|$candidateName"
            family = $family
            workItem = Text-OrEmpty $row.workItem
            nextFormulaWork = Text-OrEmpty $row.nextFormulaWork
            candidateName = $candidateName
            candidateFound = [bool]$row.candidateFound
            beatsCurrent = [bool]$row.beatsCurrent
            avgTrackMaxDegrees = [math]::Round((Number-OrZero $row.avgTrackMaxDegrees), 4)
            maxDegrees = [math]::Round((Number-OrZero $row.maxDegrees), 4)
            avgImprovementDegrees = [math]::Round((Number-OrZero $row.avgImprovementDegrees), 4)
            maxImprovementDegrees = [math]::Round((Number-OrZero $row.maxImprovementDegrees), 4)
            currentAvgTrackMaxDegrees = [math]::Round((Number-OrZero $row.currentAvgTrackMaxDegrees), 4)
            currentMaxDegrees = [math]::Round((Number-OrZero $row.currentMaxDegrees), 4)
            migrationStatus = Text-OrEmpty $gate.migrationStatus
        }
    }
}

$commonArmFormulaTimelineRows = $armFormulaTimelineRows |
    Group-Object key |
    Where-Object { $_.Count -ge 2 } |
    ForEach-Object {
        $items = @($_.Group)
        $foundItems = @($items | Where-Object { $_.candidateFound })
        $first = $items[0]
        [pscustomobject]@{
            family = $first.family
            candidateName = $first.candidateName
            reportCount = $items.Count
            foundReportCount = $foundItems.Count
            allCandidateFound = -not @($items | Where-Object { -not $_.candidateFound }).Count
            allBeatsCurrent = $foundItems.Count -eq $items.Count -and -not @($foundItems | Where-Object { -not $_.beatsCurrent }).Count
            avgTrackMaxDegrees = if ($foundItems.Count -eq 0) { 0.0 } else { [math]::Round((($foundItems | Measure-Object avgTrackMaxDegrees -Average).Average), 4) }
            maxDegrees = if ($foundItems.Count -eq 0) { 0.0 } else { [math]::Round((($foundItems | Measure-Object maxDegrees -Maximum).Maximum), 4) }
            minAvgImprovementDegrees = if ($foundItems.Count -eq 0) { 0.0 } else { [math]::Round((($foundItems | Measure-Object avgImprovementDegrees -Minimum).Minimum), 4) }
            maxAvgImprovementDegrees = if ($foundItems.Count -eq 0) { 0.0 } else { [math]::Round((($foundItems | Measure-Object avgImprovementDegrees -Maximum).Maximum), 4) }
            maxImprovementDegrees = if ($foundItems.Count -eq 0) { 0.0 } else { [math]::Round((($foundItems | Measure-Object maxImprovementDegrees -Maximum).Maximum), 4) }
            migrationStatuses = (($items | ForEach-Object { "$($_.label):$($_.migrationStatus)" }) -join "; ")
            perReport = (($items | ForEach-Object { "$($_.label):avg=$($_.avgTrackMaxDegrees),improve=$($_.avgImprovementDegrees),beats=$($_.beatsCurrent)" }) -join "; ")
        }
    } |
    Sort-Object @{ Expression = "allCandidateFound"; Descending = $true }, @{ Expression = "allBeatsCurrent"; Descending = $true }, @{ Expression = "minAvgImprovementDegrees"; Descending = $true }, @{ Expression = "avgTrackMaxDegrees"; Descending = $false } |
    Select-Object -First $Top

$legFormulaTimelineRows = foreach ($item in $loaded) {
    $gate = $item.Json.internalHumanoidSolverVariantComparison.legFormulaTimelineGateSummary
    foreach ($row in @($gate.rows)) {
        if ($null -eq $row) {
            continue
        }
        $family = Text-OrEmpty $row.family
        $candidateName = Text-OrEmpty $row.candidateName
        if ([string]::IsNullOrWhiteSpace($family) -or [string]::IsNullOrWhiteSpace($candidateName)) {
            continue
        }
        [pscustomobject]@{
            label = $item.Label
            key = "$family|$candidateName"
            family = $family
            workItem = Text-OrEmpty $row.workItem
            nextFormulaWork = Text-OrEmpty $row.nextFormulaWork
            candidateName = $candidateName
            currentCandidateName = Text-OrEmpty $row.currentCandidateName
            candidateFound = [bool]$row.candidateFound
            beatsCurrent = [bool]$row.beatsCurrent
            avgTrackMaxDegrees = [math]::Round((Number-OrZero $row.avgTrackMaxDegrees), 4)
            maxDegrees = [math]::Round((Number-OrZero $row.maxDegrees), 4)
            avgImprovementDegrees = [math]::Round((Number-OrZero $row.avgImprovementDegrees), 4)
            maxImprovementDegrees = [math]::Round((Number-OrZero $row.maxImprovementDegrees), 4)
            currentAvgTrackMaxDegrees = [math]::Round((Number-OrZero $row.currentAvgTrackMaxDegrees), 4)
            currentMaxDegrees = [math]::Round((Number-OrZero $row.currentMaxDegrees), 4)
            migrationStatus = Text-OrEmpty $gate.migrationStatus
        }
    }
}

$commonLegFormulaTimelineRows = $legFormulaTimelineRows |
    Group-Object key |
    Where-Object { $_.Count -ge 2 } |
    ForEach-Object {
        $items = @($_.Group)
        $foundItems = @($items | Where-Object { $_.candidateFound })
        $first = $items[0]
        [pscustomobject]@{
            family = $first.family
            candidateName = $first.candidateName
            reportCount = $items.Count
            foundReportCount = $foundItems.Count
            allCandidateFound = -not @($items | Where-Object { -not $_.candidateFound }).Count
            allBeatsCurrent = $foundItems.Count -eq $items.Count -and -not @($foundItems | Where-Object { -not $_.beatsCurrent }).Count
            avgTrackMaxDegrees = if ($foundItems.Count -eq 0) { 0.0 } else { [math]::Round((($foundItems | Measure-Object avgTrackMaxDegrees -Average).Average), 4) }
            maxDegrees = if ($foundItems.Count -eq 0) { 0.0 } else { [math]::Round((($foundItems | Measure-Object maxDegrees -Maximum).Maximum), 4) }
            minAvgImprovementDegrees = if ($foundItems.Count -eq 0) { 0.0 } else { [math]::Round((($foundItems | Measure-Object avgImprovementDegrees -Minimum).Minimum), 4) }
            maxAvgImprovementDegrees = if ($foundItems.Count -eq 0) { 0.0 } else { [math]::Round((($foundItems | Measure-Object avgImprovementDegrees -Maximum).Maximum), 4) }
            maxImprovementDegrees = if ($foundItems.Count -eq 0) { 0.0 } else { [math]::Round((($foundItems | Measure-Object maxImprovementDegrees -Maximum).Maximum), 4) }
            migrationStatuses = (($items | ForEach-Object { "$($_.label):$($_.migrationStatus)" }) -join "; ")
            perReport = (($items | ForEach-Object { "$($_.label):avg=$($_.avgTrackMaxDegrees),improve=$($_.avgImprovementDegrees),beats=$($_.beatsCurrent)" }) -join "; ")
        }
    } |
    Sort-Object @{ Expression = "allCandidateFound"; Descending = $true }, @{ Expression = "allBeatsCurrent"; Descending = $true }, @{ Expression = "minAvgImprovementDegrees"; Descending = $true }, @{ Expression = "avgTrackMaxDegrees"; Descending = $false } |
    Select-Object -First $Top

$singleMuscleFormulaGateRows = foreach ($item in $loaded) {
    $gate = Get-SingleMuscleFormulaGate $item.Json
    foreach ($row in @($gate.rows)) {
        if ($null -eq $row) {
            continue
        }
        $targetHumanBone = Text-OrEmpty $row.targetHumanBone
        $muscleName = Text-OrEmpty $row.muscleName
        if ([string]::IsNullOrWhiteSpace($targetHumanBone) -or [string]::IsNullOrWhiteSpace($muscleName)) {
            continue
        }
        [pscustomobject]@{
            label = $item.Label
            key = "$targetHumanBone|$muscleName"
            targetHumanBone = $targetHumanBone
            bodyGroup = Text-OrEmpty $row.bodyGroup
            muscleName = $muscleName
            muscleFamily = Text-OrEmpty $row.muscleFamily
            candidate = Text-OrEmpty $row.candidate
            formula = Text-OrEmpty $row.formula
            sideMode = Text-OrEmpty $row.sideMode
            sourceHumanBone = Text-OrEmpty $row.sourceHumanBone
            sourceAxisName = Text-OrEmpty $row.sourceAxisName
            targetAxisName = Text-OrEmpty $row.targetAxisName
            rowCount = [int](Number-OrZero $row.rowCount)
            maxErrorDegrees = [math]::Round((Number-OrZero $row.maxErrorDegrees), 4)
            avgErrorDegrees = [math]::Round((Number-OrZero $row.avgErrorDegrees), 4)
            p90ErrorDegrees = [math]::Round((Number-OrZero $row.p90ErrorDegrees), 4)
            under1DegreeCount = [int](Number-OrZero $row.under1DegreeCount)
            under5DegreeCount = [int](Number-OrZero $row.under5DegreeCount)
            candidateStatus = Text-OrEmpty $row.candidateStatus
            nextFormulaWork = Text-OrEmpty $row.nextFormulaWork
        }
    }
}

$commonSingleMuscleFormulaGateRows = $singleMuscleFormulaGateRows |
    Group-Object key |
    Where-Object { $_.Count -ge 2 } |
    ForEach-Object {
        $items = @($_.Group)
        $first = $items[0]
        [pscustomobject]@{
            targetHumanBone = $first.targetHumanBone
            bodyGroup = $first.bodyGroup
            muscleName = $first.muscleName
            muscleFamily = $first.muscleFamily
            reportCount = $items.Count
            allReady = -not @($items | Where-Object { $_.candidateStatus -ne "ready_for_timeline_formula_test" }).Count
            anyNotReady = @($items | Where-Object { $_.candidateStatus -eq "not_ready" }).Count -gt 0
            maxErrorDegrees = [math]::Round((($items | Measure-Object maxErrorDegrees -Maximum).Maximum), 4)
            avgMaxErrorDegrees = [math]::Round((($items | Measure-Object maxErrorDegrees -Average).Average), 4)
            maxP90ErrorDegrees = [math]::Round((($items | Measure-Object p90ErrorDegrees -Maximum).Maximum), 4)
            formulas = (($items | ForEach-Object { $_.formula } | Select-Object -Unique) -join "; ")
            sideModes = (($items | ForEach-Object { $_.sideMode } | Select-Object -Unique) -join "; ")
            sourceHints = (($items | ForEach-Object { "$($_.label):$($_.sourceHumanBone)/$($_.sourceAxisName)->$($_.targetAxisName)" }) -join "; ")
            statuses = (($items | ForEach-Object { "$($_.label):$($_.candidateStatus)" }) -join "; ")
            perReport = (($items | ForEach-Object { "$($_.label):err=$($_.maxErrorDegrees),formula=$($_.formula),side=$($_.sideMode)" }) -join "; ")
            nextFormulaWork = (($items | ForEach-Object { $_.nextFormulaWork } | Select-Object -Unique) -join "; ")
        }
    } |
    Sort-Object @{ Expression = "allReady"; Descending = $true }, @{ Expression = "anyNotReady"; Descending = $false }, @{ Expression = "maxErrorDegrees"; Descending = $false }, muscleName |
    Select-Object -First $Top

$forearmStretchScaleRows = foreach ($item in $loaded) {
    $summaryNode = Get-ForearmStretchScaleProbe $item.Json
    foreach ($row in @($summaryNode.bestByMuscle)) {
        if ($null -eq $row) {
            continue
        }
        $best = $row.bestCandidate
        $current = $row.currentCandidate
        $targetHumanBone = Text-OrEmpty $row.targetHumanBone
        $muscleName = Text-OrEmpty $row.muscleName
        if ([string]::IsNullOrWhiteSpace($targetHumanBone) -or [string]::IsNullOrWhiteSpace($muscleName) -or $null -eq $best) {
            continue
        }
        [pscustomobject]@{
            label = $item.Label
            key = "$targetHumanBone|$muscleName"
            targetHumanBone = $targetHumanBone
            bodyGroup = Text-OrEmpty $row.bodyGroup
            muscleName = $muscleName
            candidateStatus = Text-OrEmpty $row.candidateStatus
            bestBeatsCurrent = [bool]$row.bestBeatsCurrent
            improvementDegrees = [math]::Round((Number-OrZero $row.improvementDegrees), 4)
            bestCandidate = Text-OrEmpty $best.candidate
            bestFormula = Text-OrEmpty $best.formula
            bestScaleName = Text-OrEmpty $best.scaleName
            bestScaleMode = Text-OrEmpty $best.scaleMode
            bestScaleValue = [math]::Round((Number-OrZero $best.scaleValue), 6)
            bestSideMode = Text-OrEmpty $best.sideMode
            bestSourceHumanBone = Text-OrEmpty $best.sourceHumanBone
            bestSourceAxisName = Text-OrEmpty $best.sourceAxisName
            bestTargetAxisName = Text-OrEmpty $best.targetAxisName
            bestMaxErrorDegrees = [math]::Round((Number-OrZero $best.maxErrorDegrees), 4)
            bestAvgErrorDegrees = [math]::Round((Number-OrZero $best.avgErrorDegrees), 4)
            bestP90ErrorDegrees = [math]::Round((Number-OrZero $best.p90ErrorDegrees), 4)
            currentCandidate = Text-OrEmpty $current.candidate
            currentMaxErrorDegrees = [math]::Round((Number-OrZero $current.maxErrorDegrees), 4)
            currentAvgErrorDegrees = [math]::Round((Number-OrZero $current.avgErrorDegrees), 4)
        }
    }
}

$commonForearmStretchScaleRows = $forearmStretchScaleRows |
    Group-Object key |
    Where-Object { $_.Count -ge 2 } |
    ForEach-Object {
        $items = @($_.Group)
        $first = $items[0]
        [pscustomobject]@{
            targetHumanBone = $first.targetHumanBone
            bodyGroup = $first.bodyGroup
            muscleName = $first.muscleName
            reportCount = $items.Count
            allBeatCurrent = -not @($items | Where-Object { -not $_.bestBeatsCurrent }).Count
            anyNotReady = @($items | Where-Object { $_.candidateStatus -eq "not_ready" }).Count -gt 0
            maxBestErrorDegrees = [math]::Round((($items | Measure-Object bestMaxErrorDegrees -Maximum).Maximum), 4)
            avgBestErrorDegrees = [math]::Round((($items | Measure-Object bestMaxErrorDegrees -Average).Average), 4)
            minImprovementDegrees = [math]::Round((($items | Measure-Object improvementDegrees -Minimum).Minimum), 4)
            maxImprovementDegrees = [math]::Round((($items | Measure-Object improvementDegrees -Maximum).Maximum), 4)
            scaleNames = (($items | ForEach-Object { $_.bestScaleName } | Select-Object -Unique) -join "; ")
            scaleValues = (($items | ForEach-Object { "$($_.bestScaleName)=$($_.bestScaleValue)" } | Select-Object -Unique) -join "; ")
            formulas = (($items | ForEach-Object { $_.bestFormula } | Select-Object -Unique) -join "; ")
            sideModes = (($items | ForEach-Object { $_.bestSideMode } | Select-Object -Unique) -join "; ")
            sourceHints = (($items | ForEach-Object { "$($_.label):$($_.bestSourceHumanBone)/$($_.bestSourceAxisName)->$($_.bestTargetAxisName)" }) -join "; ")
            statuses = (($items | ForEach-Object { "$($_.label):$($_.candidateStatus)" }) -join "; ")
            perReport = (($items | ForEach-Object { "$($_.label):best=$($_.bestMaxErrorDegrees),current=$($_.currentMaxErrorDegrees),scale=$($_.bestScaleName),improve=$($_.improvementDegrees)" }) -join "; ")
        }
    } |
    Sort-Object @{ Expression = "allBeatCurrent"; Descending = $true }, @{ Expression = "anyNotReady"; Descending = $false }, @{ Expression = "maxBestErrorDegrees"; Descending = $false }, muscleName |
    Select-Object -First $Top

$distalStretchPoseAxisRows = foreach ($item in $loaded) {
    $summaryNode = $item.Json.internalHumanoidSolverVariantComparison.distalStretchTimelineFitSummary
    $variants = @($summaryNode.variants)
    $current = $variants | Where-Object { (Text-OrEmpty $_.name) -eq "current_stretch_z_pos_vector_swing_twist" } | Select-Object -First 1
    $currentAvg = Number-OrZero $current.avgTrackMaxDegrees
    $currentMax = Number-OrZero $current.maxDegrees
    foreach ($row in $variants) {
        if ($null -eq $row) {
            continue
        }
        $poseAxisCandidate = Text-OrEmpty $row.poseAxisCandidate
        if ([string]::IsNullOrWhiteSpace($poseAxisCandidate)) {
            continue
        }
        $name = Text-OrEmpty $row.name
        [pscustomobject]@{
            label = $item.Label
            key = $name
            candidateName = $name
            poseAxisCandidate = $poseAxisCandidate
            poseApplyMode = Text-OrEmpty $row.poseApplyMode
            matchedTrackCount = [int](Number-OrZero $row.matchedTrackCount)
            maxDegrees = [math]::Round((Number-OrZero $row.maxDegrees), 4)
            avgTrackMaxDegrees = [math]::Round((Number-OrZero $row.avgTrackMaxDegrees), 4)
            avgTrackAvgDegrees = [math]::Round((Number-OrZero $row.avgTrackAvgDegrees), 4)
            currentAvgTrackMaxDegrees = [math]::Round($currentAvg, 4)
            currentMaxDegrees = [math]::Round($currentMax, 4)
            avgImprovementDegrees = [math]::Round(($currentAvg - (Number-OrZero $row.avgTrackMaxDegrees)), 4)
            maxImprovementDegrees = [math]::Round(($currentMax - (Number-OrZero $row.maxDegrees)), 4)
            beatsCurrent = $current -ne $null -and ((Number-OrZero $row.avgTrackMaxDegrees) -lt $currentAvg - 0.5 -or ((Number-OrZero $row.avgTrackMaxDegrees) -le $currentAvg + 0.5 -and (Number-OrZero $row.maxDegrees) -lt $currentMax - 0.5))
        }
    }
}

$commonDistalStretchPoseAxisRows = $distalStretchPoseAxisRows |
    Group-Object key |
    Where-Object { $_.Count -ge 2 } |
    ForEach-Object {
        $items = @($_.Group)
        $first = $items[0]
        [pscustomobject]@{
            candidateName = $first.candidateName
            poseAxisCandidate = $first.poseAxisCandidate
            poseApplyMode = $first.poseApplyMode
            reportCount = $items.Count
            allBeatCurrent = -not @($items | Where-Object { -not $_.beatsCurrent }).Count
            anyBeatCurrent = @($items | Where-Object { $_.beatsCurrent }).Count -gt 0
            maxDegrees = [math]::Round((($items | Measure-Object maxDegrees -Maximum).Maximum), 4)
            avgTrackMaxDegrees = [math]::Round((($items | Measure-Object avgTrackMaxDegrees -Average).Average), 4)
            minAvgImprovementDegrees = [math]::Round((($items | Measure-Object avgImprovementDegrees -Minimum).Minimum), 4)
            maxAvgImprovementDegrees = [math]::Round((($items | Measure-Object avgImprovementDegrees -Maximum).Maximum), 4)
            maxImprovementDegrees = [math]::Round((($items | Measure-Object maxImprovementDegrees -Maximum).Maximum), 4)
            perReport = (($items | ForEach-Object { "$($_.label):avg=$($_.avgTrackMaxDegrees),max=$($_.maxDegrees),improve=$($_.avgImprovementDegrees),beats=$($_.beatsCurrent)" }) -join "; ")
        }
    } |
    Sort-Object @{ Expression = "allBeatCurrent"; Descending = $true }, @{ Expression = "anyBeatCurrent"; Descending = $true }, @{ Expression = "minAvgImprovementDegrees"; Descending = $true }, @{ Expression = "avgTrackMaxDegrees"; Descending = $false } |
    Select-Object -First $Top

$zeroBaseCandidateRows = foreach ($item in $loaded) {
    $gate = $item.Json.internalHumanoidSolverVariantComparison.zeroBaseCandidateGateSummary
    foreach ($row in @($gate.rows)) {
        if ($null -eq $row) {
            continue
        }
        $humanBone = Text-OrEmpty $row.humanBone
        $candidateFamily = Text-OrEmpty $row.candidateFamily
        if ([string]::IsNullOrWhiteSpace($humanBone) -or [string]::IsNullOrWhiteSpace($candidateFamily)) {
            continue
        }
        [pscustomobject]@{
            label = $item.Label
            key = "$humanBone|$candidateFamily"
            humanBone = $humanBone
            bodyGroup = Text-OrEmpty $row.bodyGroup
            candidateName = Text-OrEmpty $row.candidateName
            candidateFamily = $candidateFamily
            deterministicMetadataCandidate = [bool]$row.deterministicMetadataCandidate
            oracleOnly = [bool]$row.oracleOnly
            beatsCurrent = [bool]$row.beatsCurrent
            currentMaxDegrees = [math]::Round((Number-OrZero $row.currentMaxDegrees), 4)
            bestMaxDegrees = [math]::Round((Number-OrZero $row.bestMaxDegrees), 4)
            improvementMaxDegrees = [math]::Round((Number-OrZero $row.improvementMaxDegrees), 4)
            currentAvgDegrees = [math]::Round((Number-OrZero $row.currentAvgDegrees), 4)
            bestAvgDegrees = [math]::Round((Number-OrZero $row.bestAvgDegrees), 4)
            improvementAvgDegrees = [math]::Round((Number-OrZero $row.improvementAvgDegrees), 4)
            stillLargeAfterBest = [bool]$row.stillLargeAfterBest
            nextFormulaWork = Text-OrEmpty $row.nextFormulaWork
            migrationStatus = Text-OrEmpty $gate.migrationStatus
        }
    }
}

$commonZeroBaseCandidateRows = $zeroBaseCandidateRows |
    Group-Object key |
    Where-Object { $_.Count -ge 2 } |
    ForEach-Object {
        $items = @($_.Group)
        $first = $items[0]
        [pscustomobject]@{
            humanBone = $first.humanBone
            bodyGroup = $first.bodyGroup
            candidateFamily = $first.candidateFamily
            reportCount = $items.Count
            allDeterministic = -not @($items | Where-Object { -not $_.deterministicMetadataCandidate }).Count
            allOracleOnly = -not @($items | Where-Object { -not $_.oracleOnly }).Count
            allBeatsCurrent = -not @($items | Where-Object { -not $_.beatsCurrent }).Count
            maxBestDegrees = [math]::Round((($items | Measure-Object bestMaxDegrees -Maximum).Maximum), 4)
            avgBestDegrees = [math]::Round((($items | Measure-Object bestMaxDegrees -Average).Average), 4)
            minImprovementMaxDegrees = [math]::Round((($items | Measure-Object improvementMaxDegrees -Minimum).Minimum), 4)
            maxImprovementMaxDegrees = [math]::Round((($items | Measure-Object improvementMaxDegrees -Maximum).Maximum), 4)
            anyStillLargeAfterBest = @($items | Where-Object { $_.stillLargeAfterBest }).Count -gt 0
            nextFormulaWork = (($items | ForEach-Object { $_.nextFormulaWork } | Select-Object -Unique) -join "; ")
            perReport = (($items | ForEach-Object { "$($_.label):$($_.candidateName),best=$($_.bestMaxDegrees),improve=$($_.improvementMaxDegrees)" }) -join "; ")
        }
    } |
    Sort-Object @{ Expression = "allDeterministic"; Descending = $true }, @{ Expression = "allBeatsCurrent"; Descending = $true }, @{ Expression = "minImprovementMaxDegrees"; Descending = $true }, @{ Expression = "maxBestDegrees"; Descending = $false } |
    Select-Object -First $Top

$zeroBaseSourceRows = foreach ($item in $loaded) {
    $gate = $item.Json.zeroBaseSourceCandidateGateSummary
    foreach ($row in @($gate.rows)) {
        if ($null -eq $row) {
            continue
        }
        $humanBone = Text-OrEmpty $row.humanBone
        $formulaFamily = Text-OrEmpty $row.formulaFamily
        if ([string]::IsNullOrWhiteSpace($humanBone) -or [string]::IsNullOrWhiteSpace($formulaFamily)) {
            continue
        }
        $formulaName = Text-OrEmpty $row.formulaName
        $formulaSource = Text-OrEmpty $row.formulaSource
        if (($formulaSource -eq "shortProduct" -or [string]::IsNullOrWhiteSpace($formulaSource)) -and
            $formulaName.IndexOf("crossSide", [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $formulaSource = "crossSideShortProduct"
        }
        [pscustomobject]@{
            label = $item.Label
            key = "$humanBone|$formulaFamily"
            humanBone = $humanBone
            bodyGroup = Text-OrEmpty $row.bodyGroup
            formulaName = $formulaName
            formulaSource = $formulaSource
            formulaFamily = $formulaFamily
            beatsCurrent = [bool]$row.beatsCurrent
            currentZeroErrorDegrees = [math]::Round((Number-OrZero $row.currentZeroErrorDegrees), 4)
            bestFormulaErrorDegrees = [math]::Round((Number-OrZero $row.bestFormulaErrorDegrees), 4)
            improvementDegrees = [math]::Round((Number-OrZero $row.improvementDegrees), 4)
            shortProductCandidateCount = [int](Number-OrZero $row.shortProductCandidateCount)
            crossSideCandidateCount = [int](Number-OrZero $row.crossSideCandidateCount)
            nextFormulaWork = Text-OrEmpty $row.nextFormulaWork
            migrationStatus = Text-OrEmpty $gate.migrationStatus
        }
    }
}

$commonZeroBaseSourceRows = $zeroBaseSourceRows |
    Group-Object key |
    Where-Object { $_.Count -ge 2 } |
    ForEach-Object {
        $items = @($_.Group)
        $first = $items[0]
        [pscustomobject]@{
            humanBone = $first.humanBone
            bodyGroup = $first.bodyGroup
            formulaFamily = $first.formulaFamily
            formulaSources = (($items | ForEach-Object { $_.formulaSource } | Select-Object -Unique) -join "; ")
            reportCount = $items.Count
            allBeatsCurrent = -not @($items | Where-Object { -not $_.beatsCurrent }).Count
            maxBestFormulaErrorDegrees = [math]::Round((($items | Measure-Object bestFormulaErrorDegrees -Maximum).Maximum), 4)
            avgBestFormulaErrorDegrees = [math]::Round((($items | Measure-Object bestFormulaErrorDegrees -Average).Average), 4)
            minImprovementDegrees = [math]::Round((($items | Measure-Object improvementDegrees -Minimum).Minimum), 4)
            maxImprovementDegrees = [math]::Round((($items | Measure-Object improvementDegrees -Maximum).Maximum), 4)
            nextFormulaWork = (($items | ForEach-Object { $_.nextFormulaWork } | Select-Object -Unique) -join "; ")
            perReport = (($items | ForEach-Object { "$($_.label):$($_.formulaSource):$($_.formulaName),err=$($_.bestFormulaErrorDegrees),improve=$($_.improvementDegrees)" }) -join "; ")
        }
    } |
    Sort-Object @{ Expression = "allBeatsCurrent"; Descending = $true }, @{ Expression = "minImprovementDegrees"; Descending = $true }, @{ Expression = "maxBestFormulaErrorDegrees"; Descending = $false } |
    Select-Object -First $Top

$zeroBaseSourceTimelineRows = foreach ($item in $loaded) {
    $summaryNode = $item.Json.internalHumanoidSolverVariantComparison.zeroBaseSourceTimelineFitSummary
    foreach ($row in @($summaryNode.rows)) {
        if ($null -eq $row) {
            continue
        }
        $humanBone = Text-OrEmpty $row.humanBone
        $applyMode = Text-OrEmpty $row.applyMode
        if ([string]::IsNullOrWhiteSpace($humanBone) -or [string]::IsNullOrWhiteSpace($applyMode)) {
            continue
        }
        [pscustomobject]@{
            label = $item.Label
            key = "$humanBone|$applyMode"
            humanBone = $humanBone
            bodyGroup = Text-OrEmpty $row.bodyGroup
            applyMode = $applyMode
            formulaName = Text-OrEmpty $row.formulaName
            formulaSource = Text-OrEmpty $row.formulaSource
            formulaFamily = Text-OrEmpty $row.formulaFamily
            beatsCurrent = [bool]$row.beatsCurrent
            currentMaxDegrees = [math]::Round((Number-OrZero $row.currentMaxDegrees), 4)
            maxDegrees = [math]::Round((Number-OrZero $row.maxDegrees), 4)
            improvementMaxDegrees = [math]::Round((Number-OrZero $row.improvementMaxDegrees), 4)
            currentAvgDegrees = [math]::Round((Number-OrZero $row.currentAvgDegrees), 4)
            avgDegrees = [math]::Round((Number-OrZero $row.avgDegrees), 4)
            improvementAvgDegrees = [math]::Round((Number-OrZero $row.improvementAvgDegrees), 4)
        }
    }
}

$commonZeroBaseSourceTimelineRows = $zeroBaseSourceTimelineRows |
    Group-Object key |
    Where-Object { $_.Count -ge 2 } |
    ForEach-Object {
        $items = @($_.Group)
        $first = $items[0]
        [pscustomobject]@{
            humanBone = $first.humanBone
            bodyGroup = $first.bodyGroup
            applyMode = $first.applyMode
            formulaFamilies = (($items | ForEach-Object { $_.formulaFamily } | Select-Object -Unique) -join "; ")
            formulaSources = (($items | ForEach-Object { $_.formulaSource } | Select-Object -Unique) -join "; ")
            reportCount = $items.Count
            allBeatsCurrent = -not @($items | Where-Object { -not $_.beatsCurrent }).Count
            maxDegrees = [math]::Round((($items | Measure-Object maxDegrees -Maximum).Maximum), 4)
            avgMaxDegrees = [math]::Round((($items | Measure-Object maxDegrees -Average).Average), 4)
            minImprovementMaxDegrees = [math]::Round((($items | Measure-Object improvementMaxDegrees -Minimum).Minimum), 4)
            maxImprovementMaxDegrees = [math]::Round((($items | Measure-Object improvementMaxDegrees -Maximum).Maximum), 4)
            perReport = (($items | ForEach-Object { "$($_.label):$($_.formulaFamily):$($_.formulaSource):max=$($_.maxDegrees),cur=$($_.currentMaxDegrees),improve=$($_.improvementMaxDegrees)" }) -join "; ")
        }
    } |
    Sort-Object @{ Expression = "allBeatsCurrent"; Descending = $true }, @{ Expression = "minImprovementMaxDegrees"; Descending = $true }, @{ Expression = "maxDegrees"; Descending = $false } |
    Select-Object -First $Top

$zeroBaseArmTwistRows = foreach ($item in $loaded) {
    $summaryNode = $item.Json.internalHumanoidSolverVariantComparison.zeroBaseArmTwistTimelineFitSummary
    foreach ($row in @($summaryNode.rows)) {
        if ($null -eq $row) {
            continue
        }
        $humanBone = Text-OrEmpty $row.humanBone
        $applyMode = Text-OrEmpty $row.applyMode
        $variant = Text-OrEmpty $row.armTwistVariant
        if ([string]::IsNullOrWhiteSpace($humanBone) -or [string]::IsNullOrWhiteSpace($applyMode) -or [string]::IsNullOrWhiteSpace($variant)) {
            continue
        }
        [pscustomobject]@{
            label = $item.Label
            key = "$humanBone|$applyMode|$variant"
            humanBone = $humanBone
            bodyGroup = Text-OrEmpty $row.bodyGroup
            applyMode = $applyMode
            armTwistVariant = $variant
            formulaFamily = Text-OrEmpty $row.formulaFamily
            formulaSource = Text-OrEmpty $row.formulaSource
            maxDegrees = [math]::Round((Number-OrZero $row.maxDegrees), 4)
            avgDegrees = [math]::Round((Number-OrZero $row.avgDegrees), 4)
            zeroBaseCurrentMaxDegrees = [math]::Round((Number-OrZero $row.zeroBaseCurrentMaxDegrees), 4)
            improvementOverZeroBaseCurrentDegrees = [math]::Round((Number-OrZero $row.improvementOverZeroBaseCurrentDegrees), 4)
            beatsZeroBaseCurrent = [bool]$row.beatsZeroBaseCurrent
        }
    }
}

$commonZeroBaseArmTwistRows = $zeroBaseArmTwistRows |
    Group-Object key |
    Where-Object { $_.Count -ge 2 } |
    ForEach-Object {
        $items = @($_.Group)
        $first = $items[0]
        [pscustomobject]@{
            humanBone = $first.humanBone
            bodyGroup = $first.bodyGroup
            applyMode = $first.applyMode
            armTwistVariant = $first.armTwistVariant
            formulaFamilies = (($items | ForEach-Object { $_.formulaFamily } | Select-Object -Unique) -join "; ")
            reportCount = $items.Count
            allBeatZeroBaseCurrent = -not @($items | Where-Object { -not $_.beatsZeroBaseCurrent }).Count
            maxDegrees = [math]::Round((($items | Measure-Object maxDegrees -Maximum).Maximum), 4)
            minImprovementOverZeroBaseCurrentDegrees = [math]::Round((($items | Measure-Object improvementOverZeroBaseCurrentDegrees -Minimum).Minimum), 4)
            maxImprovementOverZeroBaseCurrentDegrees = [math]::Round((($items | Measure-Object improvementOverZeroBaseCurrentDegrees -Maximum).Maximum), 4)
            perReport = (($items | ForEach-Object { "$($_.label):max=$($_.maxDegrees),baseCur=$($_.zeroBaseCurrentMaxDegrees),improve=$($_.improvementOverZeroBaseCurrentDegrees)" }) -join "; ")
        }
    } |
    Sort-Object @{ Expression = "allBeatZeroBaseCurrent"; Descending = $true }, @{ Expression = "minImprovementOverZeroBaseCurrentDegrees"; Descending = $true }, @{ Expression = "maxDegrees"; Descending = $false } |
    Select-Object -First $Top

$zeroBaseForearmPairRows = foreach ($item in $loaded) {
    $summaryNode = $item.Json.internalHumanoidSolverVariantComparison.zeroBaseForearmPairTimelineFitSummary
    foreach ($row in @($summaryNode.rows)) {
        if ($null -eq $row) {
            continue
        }
        $humanBone = Text-OrEmpty $row.humanBone
        $applyMode = Text-OrEmpty $row.applyMode
        $pairMode = Text-OrEmpty $row.pairMode
        if ([string]::IsNullOrWhiteSpace($humanBone) -or [string]::IsNullOrWhiteSpace($applyMode) -or [string]::IsNullOrWhiteSpace($pairMode)) {
            continue
        }
        [pscustomobject]@{
            label = $item.Label
            key = "$humanBone|$applyMode|$pairMode"
            humanBone = $humanBone
            bodyGroup = Text-OrEmpty $row.bodyGroup
            applyMode = $applyMode
            pairMode = $pairMode
            pairFamily = Text-OrEmpty $row.pairFamily
            stretchFormula = Text-OrEmpty $row.stretchFormula
            twistFormula = Text-OrEmpty $row.twistFormula
            stretchSide = Text-OrEmpty $row.stretchSide
            twistSide = Text-OrEmpty $row.twistSide
            orderMode = Text-OrEmpty $row.orderMode
            outputSide = Text-OrEmpty $row.outputSide
            formulaFamily = Text-OrEmpty $row.formulaFamily
            maxDegrees = [math]::Round((Number-OrZero $row.maxDegrees), 4)
            avgDegrees = [math]::Round((Number-OrZero $row.avgDegrees), 4)
            zeroBaseCurrentMaxDegrees = [math]::Round((Number-OrZero $row.zeroBaseCurrentMaxDegrees), 4)
            improvementOverZeroBaseCurrentDegrees = [math]::Round((Number-OrZero $row.improvementOverZeroBaseCurrentDegrees), 4)
            beatsZeroBaseCurrent = [bool]$row.beatsZeroBaseCurrent
        }
    }
}

$commonZeroBaseForearmPairRowsAll = $zeroBaseForearmPairRows |
    Group-Object key |
    Where-Object { $_.Count -ge 2 } |
    ForEach-Object {
        $items = @($_.Group)
        $first = $items[0]
        [pscustomobject]@{
            humanBone = $first.humanBone
            bodyGroup = $first.bodyGroup
            applyMode = $first.applyMode
            pairMode = $first.pairMode
            pairFamily = $first.pairFamily
            stretchFormula = $first.stretchFormula
            twistFormula = $first.twistFormula
            stretchSide = $first.stretchSide
            twistSide = $first.twistSide
            orderMode = $first.orderMode
            outputSide = $first.outputSide
            formulaFamilies = (($items | ForEach-Object { $_.formulaFamily } | Select-Object -Unique) -join "; ")
            reportCount = $items.Count
            allBeatZeroBaseCurrent = -not @($items | Where-Object { -not $_.beatsZeroBaseCurrent }).Count
            maxDegrees = [math]::Round((($items | Measure-Object maxDegrees -Maximum).Maximum), 4)
            minImprovementOverZeroBaseCurrentDegrees = [math]::Round((($items | Measure-Object improvementOverZeroBaseCurrentDegrees -Minimum).Minimum), 4)
            maxImprovementOverZeroBaseCurrentDegrees = [math]::Round((($items | Measure-Object improvementOverZeroBaseCurrentDegrees -Maximum).Maximum), 4)
            perReport = (($items | ForEach-Object { "$($_.label):max=$($_.maxDegrees),baseCur=$($_.zeroBaseCurrentMaxDegrees),improve=$($_.improvementOverZeroBaseCurrentDegrees)" }) -join "; ")
        }
    } |
    Sort-Object @{ Expression = "allBeatZeroBaseCurrent"; Descending = $true }, @{ Expression = "maxDegrees"; Descending = $false }, @{ Expression = "minImprovementOverZeroBaseCurrentDegrees"; Descending = $true }

$commonZeroBaseForearmPairRows = $commonZeroBaseForearmPairRowsAll |
    Select-Object -First $Top

$partialOracleLowerArmRows = foreach ($item in $loaded) {
    $summaryNode = $item.Json.internalHumanoidSolverVariantComparison.partialOracleLowerArmTimelineReplacementSummary
    foreach ($row in @($summaryNode.modes)) {
        $mode = Text-OrEmpty $row.mode
        if ([string]::IsNullOrWhiteSpace($mode)) {
            continue
        }
        [pscustomobject]@{
            label = $item.Label
            key = $mode
            mode = $mode
            replacement = Text-OrEmpty $row.replacement
            deltaMode = Text-OrEmpty $row.deltaMode
            matchedTrackCount = [int](Number-OrZero $row.matchedTrackCount)
            maxDegrees = [math]::Round((Number-OrZero $row.maxDegrees), 4)
            avgTrackMaxDegrees = [math]::Round((Number-OrZero $row.avgTrackMaxDegrees), 4)
            avgTrackAvgDegrees = [math]::Round((Number-OrZero $row.avgTrackAvgDegrees), 4)
            maxImprovementDegrees = [math]::Round((Number-OrZero $row.maxImprovementDegrees), 4)
            avgImprovementDegrees = [math]::Round((Number-OrZero $row.avgImprovementDegrees), 4)
            beatsCurrent = [bool]$row.beatsCurrent
        }
    }
}

$commonPartialOracleLowerArmRows = $partialOracleLowerArmRows |
    Group-Object key |
    Where-Object { $_.Count -ge 2 } |
    ForEach-Object {
        $items = @($_.Group)
        $first = $items[0]
        [pscustomobject]@{
            mode = $first.mode
            replacement = $first.replacement
            deltaMode = $first.deltaMode
            reportCount = $items.Count
            allBeatCurrent = -not @($items | Where-Object { -not $_.beatsCurrent }).Count
            maxDegrees = [math]::Round((($items | Measure-Object maxDegrees -Maximum).Maximum), 4)
            avgTrackMaxDegrees = [math]::Round((($items | Measure-Object avgTrackMaxDegrees -Average).Average), 4)
            minMaxImprovementDegrees = [math]::Round((($items | Measure-Object maxImprovementDegrees -Minimum).Minimum), 4)
            minAvgImprovementDegrees = [math]::Round((($items | Measure-Object avgImprovementDegrees -Minimum).Minimum), 4)
            perReport = (($items | ForEach-Object { "$($_.label):max=$($_.maxDegrees),avgMax=$($_.avgTrackMaxDegrees),maxImp=$($_.maxImprovementDegrees),avgImp=$($_.avgImprovementDegrees),beats=$($_.beatsCurrent)" }) -join "; ")
        }
    } |
    Sort-Object @{ Expression = "allBeatCurrent"; Descending = $true }, @{ Expression = "maxDegrees"; Descending = $false }, @{ Expression = "minAvgImprovementDegrees"; Descending = $true } |
    Select-Object -First $Top

$partialOracleBaseRows = foreach ($item in $loaded) {
    $summaryNode = $item.Json.internalHumanoidSolverVariantComparison.partialOracleLowerArmTimelineReplacementSummary.baseAlignmentSummary
    foreach ($row in @($summaryNode.groups)) {
        $targetHumanBone = Text-OrEmpty $row.targetHumanBone
        $muscleName = Text-OrEmpty $row.muscleName
        if ([string]::IsNullOrWhiteSpace($targetHumanBone) -or [string]::IsNullOrWhiteSpace($muscleName)) {
            continue
        }
        $layout = $row.avatarLayout
        $layoutAngles = if ($null -eq $layout) { $null } else { $layout.poseCandidateAngles }
        [pscustomobject]@{
            label = $item.Label
            key = "$targetHumanBone|$muscleName"
            targetHumanBone = $targetHumanBone
            muscleName = $muscleName
            muscleFamily = Text-OrEmpty $row.muscleFamily
            layoutSide = Text-OrEmpty $layout.side
            layoutMetadataAvailable = ($null -ne $layout)
            layoutHumanNodeIndex = Layout-IntOrEmpty $layout "humanNodeIndex"
            layoutMappedHumanNodeIndex = Layout-IntOrEmpty $layout "mappedHumanNodeIndex"
            layoutSameAsHumanBoneIndex = Layout-BoolOrEmpty $layout "sameAsHumanBoneIndex"
            layoutAvatarSkeletonIndex = Layout-IntOrEmpty $layout "avatarSkeletonIndex"
            layoutMappedAvatarSkeletonIndex = Layout-IntOrEmpty $layout "mappedAvatarSkeletonIndex"
            layoutSameAsMappedAvatarSkeletonIndex = Layout-BoolOrEmpty $layout "sameAsMappedAvatarSkeletonIndex"
            layoutSkeletonParentIndex = Layout-IntOrEmpty $layout "skeletonParentIndex"
            layoutAvatarSkeletonParentIndex = Layout-IntOrEmpty $layout "avatarSkeletonParentIndex"
            layoutOppositeHumanNodeIndex = Layout-IntOrEmpty $layout "oppositeHumanNodeIndex"
            layoutOppositeAvatarSkeletonIndex = Layout-IntOrEmpty $layout "oppositeAvatarSkeletonIndex"
            angleDefaultPoseSameVsHumanIndex = Layout-NumberOrEmpty $layoutAngles "avatarDefaultPoseSameVsHumanSkeletonIndex"
            angleDefaultLocalSameVsHumanIndex = Layout-NumberOrEmpty $layoutAngles "avatarDefaultLocalSameVsHumanSkeletonIndex"
            angleDefaultParentPoseSameVsHumanIndex = Layout-NumberOrEmpty $layoutAngles "avatarDefaultParentPoseSameVsHumanSkeletonIndex"
            angleDefaultParentLocalSameVsHumanIndex = Layout-NumberOrEmpty $layoutAngles "avatarDefaultParentLocalSameVsHumanSkeletonIndex"
            angleAvatarSkeletonDefaultParentLocalVsHumanParentLocal = Layout-NumberOrEmpty $layoutAngles "avatarSkeletonDefaultParentLocalVsAvatarDefaultHumanParentLocal"
            angleLocalRestVsDefaultLocalSame = Layout-NumberOrEmpty $layoutAngles "localRestVsAvatarDefaultLocalSameIndex"
            angleLocalRestVsDefaultParentLocalSame = Layout-NumberOrEmpty $layoutAngles "localRestVsAvatarDefaultParentLocalSameIndex"
            angleParentRestVsAvatarSkeletonDefaultParentLocal = Layout-NumberOrEmpty $layoutAngles "parentRestVsAvatarSkeletonDefaultParentLocalSameIndex"
            angleCrossMirrorOppositeVsDefaultOpposite = Layout-NumberOrEmpty $layoutAngles "crossMirrorOppositePoseVsCrossDefaultOppositePose"
            angleCrossMirrorTargetToOppositeVsOppositeToTarget = Layout-NumberOrEmpty $layoutAngles "crossMirrorTargetToOppositeVsOppositeToTarget"
            rowCount = [int](Number-OrZero $row.rowCount)
            bestBaseCandidate = Text-OrEmpty $row.bestBaseCandidate
            bestBaseCandidateFamily = Text-OrEmpty $row.bestBaseCandidateFamily
            bestBaseCandidatePortability = Text-OrEmpty $row.bestBaseCandidatePortability
            bestBaseEffectiveFactorSignature = Text-OrEmpty $row.bestBaseEffectiveFactorSignature
            bestBaseEffectiveRoleSignature = Text-OrEmpty $row.bestBaseEffectiveRoleSignature
            maxBestBaseCandidateFactorCount = [int](Number-OrZero $row.maxBestBaseCandidateFactorCount)
            maxBestBaseErrorDegrees = [math]::Round((Number-OrZero $row.maxBestBaseErrorDegrees), 4)
            avgBestBaseErrorDegrees = [math]::Round((Number-OrZero $row.avgBestBaseErrorDegrees), 4)
            bestReducedBaseCandidate = Text-OrEmpty $row.bestReducedBaseCandidate
            bestReducedBaseCandidateFamily = Text-OrEmpty $row.bestReducedBaseCandidateFamily
            bestReducedBaseCandidatePortability = Text-OrEmpty $row.bestReducedBaseCandidatePortability
            bestReducedEffectiveFactorSignature = Text-OrEmpty $row.bestReducedEffectiveFactorSignature
            bestReducedEffectiveRoleSignature = Text-OrEmpty $row.bestReducedEffectiveRoleSignature
            maxReducedBaseCandidateFactorCount = [int](Number-OrZero $row.maxReducedBaseCandidateFactorCount)
            maxReducedBaseErrorDegrees = [math]::Round((Number-OrZero $row.maxReducedBaseErrorDegrees), 4)
            avgReducedBaseErrorDegrees = [math]::Round((Number-OrZero $row.avgReducedBaseErrorDegrees), 4)
            bestTemplateBaseCandidate = Text-OrEmpty $row.bestTemplateBaseCandidate
            bestTemplateBaseCandidateFamily = Text-OrEmpty $row.bestTemplateBaseCandidateFamily
            bestTemplateBaseCandidatePortability = Text-OrEmpty $row.bestTemplateBaseCandidatePortability
            bestTemplateEffectiveRoleSignature = Text-OrEmpty $row.bestTemplateEffectiveRoleSignature
            maxTemplateBaseErrorDegrees = [math]::Round((Number-OrZero $row.maxTemplateBaseErrorDegrees), 4)
            avgTemplateBaseErrorDegrees = [math]::Round((Number-OrZero $row.avgTemplateBaseErrorDegrees), 4)
            bestIndexSelectionBaseCandidate = Text-OrEmpty $row.bestIndexSelectionBaseCandidate
            bestIndexSelectionBaseCandidateFamily = Text-OrEmpty $row.bestIndexSelectionBaseCandidateFamily
            bestIndexSelectionBaseCandidatePortability = Text-OrEmpty $row.bestIndexSelectionBaseCandidatePortability
            bestIndexSelectionEffectiveFactorSignature = Text-OrEmpty $row.bestIndexSelectionEffectiveFactorSignature
            bestIndexSelectionEffectiveRoleSignature = Text-OrEmpty $row.bestIndexSelectionEffectiveRoleSignature
            maxIndexSelectionBaseErrorDegrees = [math]::Round((Number-OrZero $row.maxIndexSelectionBaseErrorDegrees), 4)
            avgIndexSelectionBaseErrorDegrees = [math]::Round((Number-OrZero $row.avgIndexSelectionBaseErrorDegrees), 4)
            maxCurrentZeroErrorDegrees = [math]::Round((Number-OrZero $row.maxCurrentZeroErrorDegrees), 4)
            avgCurrentZeroErrorDegrees = [math]::Round((Number-OrZero $row.avgCurrentZeroErrorDegrees), 4)
        }
    }
}

$commonPartialOracleBaseRows = $partialOracleBaseRows |
    Group-Object key |
    Where-Object { $_.Count -ge 2 } |
    ForEach-Object {
        $items = @($_.Group)
        $first = $items[0]
        [pscustomobject]@{
            targetHumanBone = $first.targetHumanBone
            muscleName = $first.muscleName
            muscleFamily = $first.muscleFamily
            reportCount = $items.Count
            layoutMetadataAvailableCount = @($items | Where-Object { $_.layoutMetadataAvailable }).Count
            maxBestBaseErrorDegrees = [math]::Round((($items | Measure-Object maxBestBaseErrorDegrees -Maximum).Maximum), 4)
            avgBestBaseErrorDegrees = [math]::Round((($items | Measure-Object avgBestBaseErrorDegrees -Average).Average), 4)
            minCurrentZeroErrorDegrees = [math]::Round((($items | Measure-Object maxCurrentZeroErrorDegrees -Minimum).Minimum), 4)
            maxCurrentZeroErrorDegrees = [math]::Round((($items | Measure-Object maxCurrentZeroErrorDegrees -Maximum).Maximum), 4)
            layoutNodeIndexes = (($items | ForEach-Object { "$($_.label):h=$($_.layoutHumanNodeIndex),a=$($_.layoutAvatarSkeletonIndex),oh=$($_.layoutOppositeHumanNodeIndex),oa=$($_.layoutOppositeAvatarSkeletonIndex)" }) -join "; ")
            layoutParentIndexes = (($items | ForEach-Object { "$($_.label):sp=$($_.layoutSkeletonParentIndex),ap=$($_.layoutAvatarSkeletonParentIndex)" }) -join "; ")
            layoutSameIndexFlags = (($items | ForEach-Object { "$($_.label):human=$($_.layoutSameAsHumanBoneIndex),avatar=$($_.layoutSameAsMappedAvatarSkeletonIndex)" }) -join "; ")
            layoutAngleDefaultLocalSameVsHuman = (($items | ForEach-Object { "$($_.label):$($_.angleDefaultLocalSameVsHumanIndex)" }) -join "; ")
            layoutAngleDefaultParentLocalSameVsHuman = (($items | ForEach-Object { "$($_.label):$($_.angleDefaultParentLocalSameVsHumanIndex)" }) -join "; ")
            layoutAngleLocalRestVsDefaultLocalSame = (($items | ForEach-Object { "$($_.label):$($_.angleLocalRestVsDefaultLocalSame)" }) -join "; ")
            layoutAngleParentRestVsAvatarSkeletonDefaultParentLocal = (($items | ForEach-Object { "$($_.label):$($_.angleParentRestVsAvatarSkeletonDefaultParentLocal)" }) -join "; ")
            layoutAngleCrossMirrorTargetToOppositeVsOppositeToTarget = (($items | ForEach-Object { "$($_.label):$($_.angleCrossMirrorTargetToOppositeVsOppositeToTarget)" }) -join "; ")
            maxBestBaseCandidateFactorCount = [int](($items | Measure-Object maxBestBaseCandidateFactorCount -Maximum).Maximum)
            families = (($items | ForEach-Object { "$($_.label):$($_.bestBaseCandidateFamily)" }) -join "; ")
            portabilities = (($items | ForEach-Object { "$($_.label):$($_.bestBaseCandidatePortability)" }) -join "; ")
            effectiveRoleSignatures = (($items | ForEach-Object { "$($_.label):$($_.bestBaseEffectiveRoleSignature)" }) -join "; ")
            effectiveFactorSignatures = (($items | ForEach-Object { "$($_.label):$($_.bestBaseEffectiveFactorSignature)" }) -join "; ")
            candidates = (($items | ForEach-Object { "$($_.label):$($_.bestBaseCandidate)[$($_.maxBestBaseErrorDegrees)]" }) -join "; ")
            maxReducedBaseErrorDegrees = [math]::Round((($items | Measure-Object maxReducedBaseErrorDegrees -Maximum).Maximum), 4)
            avgReducedBaseErrorDegrees = [math]::Round((($items | Measure-Object avgReducedBaseErrorDegrees -Average).Average), 4)
            maxReducedBaseCandidateFactorCount = [int](($items | Measure-Object maxReducedBaseCandidateFactorCount -Maximum).Maximum)
            reducedFamilies = (($items | ForEach-Object { "$($_.label):$($_.bestReducedBaseCandidateFamily)" }) -join "; ")
            reducedPortabilities = (($items | ForEach-Object { "$($_.label):$($_.bestReducedBaseCandidatePortability)" }) -join "; ")
            reducedEffectiveRoleSignatures = (($items | ForEach-Object { "$($_.label):$($_.bestReducedEffectiveRoleSignature)" }) -join "; ")
            reducedEffectiveFactorSignatures = (($items | ForEach-Object { "$($_.label):$($_.bestReducedEffectiveFactorSignature)" }) -join "; ")
            reducedCandidates = (($items | ForEach-Object { "$($_.label):$($_.bestReducedBaseCandidate)[$($_.maxReducedBaseErrorDegrees)]" }) -join "; ")
            maxTemplateBaseErrorDegrees = [math]::Round((($items | Measure-Object maxTemplateBaseErrorDegrees -Maximum).Maximum), 4)
            avgTemplateBaseErrorDegrees = [math]::Round((($items | Measure-Object avgTemplateBaseErrorDegrees -Average).Average), 4)
            templateFamilies = (($items | ForEach-Object { "$($_.label):$($_.bestTemplateBaseCandidateFamily)" }) -join "; ")
            templatePortabilities = (($items | ForEach-Object { "$($_.label):$($_.bestTemplateBaseCandidatePortability)" }) -join "; ")
            templateEffectiveRoleSignatures = (($items | ForEach-Object { "$($_.label):$($_.bestTemplateEffectiveRoleSignature)" }) -join "; ")
            templateCandidates = (($items | ForEach-Object { "$($_.label):$($_.bestTemplateBaseCandidate)[$($_.maxTemplateBaseErrorDegrees)]" }) -join "; ")
            maxIndexSelectionBaseErrorDegrees = [math]::Round((($items | Measure-Object maxIndexSelectionBaseErrorDegrees -Maximum).Maximum), 4)
            avgIndexSelectionBaseErrorDegrees = [math]::Round((($items | Measure-Object avgIndexSelectionBaseErrorDegrees -Average).Average), 4)
            indexSelectionFamilies = (($items | ForEach-Object { "$($_.label):$($_.bestIndexSelectionBaseCandidateFamily)" }) -join "; ")
            indexSelectionPortabilities = (($items | ForEach-Object { "$($_.label):$($_.bestIndexSelectionBaseCandidatePortability)" }) -join "; ")
            indexSelectionEffectiveRoleSignatures = (($items | ForEach-Object { "$($_.label):$($_.bestIndexSelectionEffectiveRoleSignature)" }) -join "; ")
            indexSelectionEffectiveFactorSignatures = (($items | ForEach-Object { "$($_.label):$($_.bestIndexSelectionEffectiveFactorSignature)" }) -join "; ")
            indexSelectionCandidates = (($items | ForEach-Object { "$($_.label):$($_.bestIndexSelectionBaseCandidate)[$($_.maxIndexSelectionBaseErrorDegrees)]" }) -join "; ")
        }
    } |
    Sort-Object @{ Expression = "maxBestBaseErrorDegrees"; Descending = $false }, @{ Expression = "maxCurrentZeroErrorDegrees"; Descending = $true } |
    Select-Object -First $Top

$partialOracleBaseTemplateGridRows = foreach ($item in $loaded) {
    $summaryNode = $item.Json.internalHumanoidSolverVariantComparison.partialOracleLowerArmTimelineReplacementSummary.baseAlignmentSummary
    foreach ($row in @($summaryNode.templateCandidateGrid)) {
        $candidate = Text-OrEmpty $row.candidate
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }
        [pscustomobject]@{
            label = $item.Label
            candidate = $candidate
            roleSignature = Text-OrEmpty $row.roleSignature
            rowCount = [int](Number-OrZero $row.rowCount)
            maxErrorDegrees = [math]::Round((Number-OrZero $row.maxErrorDegrees), 4)
            avgErrorDegrees = [math]::Round((Number-OrZero $row.avgErrorDegrees), 4)
            bestTargets = Format-TemplateTargets $row.bestTargets
            worstTargets = Format-TemplateTargets $row.worstTargets
        }
    }
}

$commonPartialOracleBaseTemplateGridRows = $partialOracleBaseTemplateGridRows |
    Group-Object candidate |
    Where-Object { $_.Count -ge 2 } |
    ForEach-Object {
        $items = @($_.Group)
        $first = $items[0]
        [pscustomobject]@{
            candidate = $first.candidate
            roleSignature = $first.roleSignature
            reportCount = $items.Count
            maxErrorDegrees = [math]::Round((($items | Measure-Object maxErrorDegrees -Maximum).Maximum), 4)
            avgErrorDegrees = [math]::Round((($items | Measure-Object avgErrorDegrees -Average).Average), 4)
            bestTargets = (($items | ForEach-Object { "$($_.label):$($_.bestTargets)" }) -join " | ")
            worstTargets = (($items | ForEach-Object { "$($_.label):$($_.worstTargets)" }) -join " | ")
            perReport = (($items | ForEach-Object { "$($_.label):max=$($_.maxErrorDegrees),avg=$($_.avgErrorDegrees)" }) -join "; ")
        }
    } |
    Sort-Object @{ Expression = "maxErrorDegrees"; Descending = $false }, @{ Expression = "avgErrorDegrees"; Descending = $false } |
    Select-Object -First $Top

$lowerArmBaseMigrationThresholdDegrees = 5.0
$lowerArmBaseMigrationMinReportCount = 3
$bestPortableBaseTemplate = @($commonPartialOracleBaseTemplateGridRows |
    Where-Object { $_.reportCount -ge $lowerArmBaseMigrationMinReportCount } |
    Sort-Object @{ Expression = "maxErrorDegrees"; Descending = $false }, @{ Expression = "avgErrorDegrees"; Descending = $false } |
    Select-Object -First 1)[0]
$bestIndexSelectionBase = @($commonPartialOracleBaseRows |
    Where-Object { $_.reportCount -ge $lowerArmBaseMigrationMinReportCount } |
    Sort-Object @{ Expression = "maxIndexSelectionBaseErrorDegrees"; Descending = $false }, @{ Expression = "avgIndexSelectionBaseErrorDegrees"; Descending = $false } |
    Select-Object -First 1)[0]
$lowerArmBaseMigrationGateStatus = "not_ready_no_portable_template"
$lowerArmBaseMigrationGateReason = "No lower-arm base template or index-selection candidate is portable across at least ${lowerArmBaseMigrationMinReportCount} reports below ${lowerArmBaseMigrationThresholdDegrees} degrees."
if ($loaded.Count -lt $lowerArmBaseMigrationMinReportCount) {
    $lowerArmBaseMigrationGateStatus = "not_ready_needs_more_reports"
    $lowerArmBaseMigrationGateReason = "Only $($loaded.Count) report(s) were compared; require at least ${lowerArmBaseMigrationMinReportCount} cross-model reports before considering lower-arm base migration."
}
elseif ($bestIndexSelectionBase -and
    $bestIndexSelectionBase.reportCount -ge $lowerArmBaseMigrationMinReportCount -and
    $bestIndexSelectionBase.maxIndexSelectionBaseErrorDegrees -gt 0 -and
    $bestIndexSelectionBase.maxIndexSelectionBaseErrorDegrees -le $lowerArmBaseMigrationThresholdDegrees) {
    $lowerArmBaseMigrationGateStatus = "candidate_ready_for_timeline_probe"
    $lowerArmBaseMigrationGateReason = "Best index-selection base candidate is below the migration threshold; it still needs full timeline glTF validation before production use."
}
elseif ($bestPortableBaseTemplate -and
    $bestPortableBaseTemplate.reportCount -ge $lowerArmBaseMigrationMinReportCount -and
    $bestPortableBaseTemplate.maxErrorDegrees -gt 0 -and
    $bestPortableBaseTemplate.maxErrorDegrees -le $lowerArmBaseMigrationThresholdDegrees) {
    $lowerArmBaseMigrationGateStatus = "template_ready_for_index_selection_probe"
    $lowerArmBaseMigrationGateReason = "Best portable base template is below the migration threshold; convert it to an Avatar metadata selector before production use."
}

$summary = [pscustomobject]@{
    generatedAt = (Get-Date).ToString("o")
    rule = "Diagnostic only: compares Unity oracle Humanoid axis reports. It does not create model-animation relations and must not be used as a production solver rule by itself."
    reportCount = $loaded.Count
    reports = $reportSummaries
    commonFamilies = @($commonFamilies | Select-Object -First $Top)
    commonAxisBlockers = @($commonAxisBlockers)
    formulaReadiness = [pscustomobject]@{
        rowCount = @($formulaReadinessRows).Count
        commonCandidateCount = @($commonFormulaReadinessRows).Count
        commonCandidates = @($commonFormulaReadinessRows)
    }
    commonAxisSpaceBlockers = @($commonAxisSpaceBlockers)
    commonAxisSpaceDetails = @($commonAxisSpaceDetailRows)
    offAxisPatternByTarget = @($offAxisPatternRows)
    offAxisPatternByMuscle = @($offAxisFamilyRows)
    armFormulaTimelineGate = [pscustomobject]@{
        rowCount = @($armFormulaTimelineRows).Count
        commonCandidateCount = @($commonArmFormulaTimelineRows).Count
        commonCandidates = @($commonArmFormulaTimelineRows)
    }
    legFormulaTimelineGate = [pscustomobject]@{
        rowCount = @($legFormulaTimelineRows).Count
        commonCandidateCount = @($commonLegFormulaTimelineRows).Count
        commonCandidates = @($commonLegFormulaTimelineRows)
    }
    singleMuscleFormulaDeltaSideGate = [pscustomobject]@{
        rowCount = @($singleMuscleFormulaGateRows).Count
        commonCandidateCount = @($commonSingleMuscleFormulaGateRows).Count
        commonCandidates = @($commonSingleMuscleFormulaGateRows)
    }
    forearmStretchScaleProbe = [pscustomobject]@{
        rowCount = @($forearmStretchScaleRows).Count
        commonCandidateCount = @($commonForearmStretchScaleRows).Count
        commonCandidates = @($commonForearmStretchScaleRows)
    }
    distalStretchPoseAxisProbe = [pscustomobject]@{
        rowCount = @($distalStretchPoseAxisRows).Count
        commonCandidateCount = @($commonDistalStretchPoseAxisRows).Count
        commonCandidates = @($commonDistalStretchPoseAxisRows)
    }
    zeroBaseCandidateGate = [pscustomobject]@{
        rowCount = @($zeroBaseCandidateRows).Count
        commonCandidateCount = @($commonZeroBaseCandidateRows).Count
        commonCandidates = @($commonZeroBaseCandidateRows)
    }
    zeroBaseSourceCandidateGate = [pscustomobject]@{
        rowCount = @($zeroBaseSourceRows).Count
        commonCandidateCount = @($commonZeroBaseSourceRows).Count
        commonCandidates = @($commonZeroBaseSourceRows)
    }
    zeroBaseSourceTimelineFit = [pscustomobject]@{
        rowCount = @($zeroBaseSourceTimelineRows).Count
        commonCandidateCount = @($commonZeroBaseSourceTimelineRows).Count
        commonCandidates = @($commonZeroBaseSourceTimelineRows)
    }
    zeroBaseArmTwistTimelineFit = [pscustomobject]@{
        rowCount = @($zeroBaseArmTwistRows).Count
        commonCandidateCount = @($commonZeroBaseArmTwistRows).Count
        commonCandidates = @($commonZeroBaseArmTwistRows)
    }
    zeroBaseForearmPairTimelineFit = [pscustomobject]@{
        rowCount = @($zeroBaseForearmPairRows).Count
        commonCandidateCount = @($commonZeroBaseForearmPairRowsAll).Count
        commonCandidates = @($commonZeroBaseForearmPairRows)
        lowerLegCommonCandidates = @($commonZeroBaseForearmPairRowsAll | Where-Object { $_.humanBone -like '*LowerLeg*' } | Sort-Object @{ Expression = "maxDegrees"; Descending = $false }, @{ Expression = "allBeatZeroBaseCurrent"; Descending = $true } | Select-Object -First $Top)
        unityProbeLegCommonCandidates = @($commonZeroBaseForearmPairRowsAll | Where-Object { $_.pairMode -like '*unity_probe_leg*' } | Sort-Object @{ Expression = "maxDegrees"; Descending = $false }, @{ Expression = "allBeatZeroBaseCurrent"; Descending = $true } | Select-Object -First $Top)
    }
    partialOracleLowerArmTimelineReplacement = [pscustomobject]@{
        rowCount = @($partialOracleLowerArmRows).Count
        commonCandidateCount = @($commonPartialOracleLowerArmRows).Count
        commonCandidates = @($commonPartialOracleLowerArmRows)
        baseAlignmentCommonCandidateCount = @($commonPartialOracleBaseRows).Count
        baseAlignmentCommonCandidates = @($commonPartialOracleBaseRows)
        baseTemplateGridRowCount = @($partialOracleBaseTemplateGridRows).Count
        baseTemplateGridCommonCandidateCount = @($commonPartialOracleBaseTemplateGridRows).Count
        baseTemplateGridCommonCandidates = @($commonPartialOracleBaseTemplateGridRows)
        lowerArmBaseMigrationGate = [pscustomobject]@{
            status = $lowerArmBaseMigrationGateStatus
            reason = $lowerArmBaseMigrationGateReason
            thresholdDegrees = $lowerArmBaseMigrationThresholdDegrees
            minReportCount = $lowerArmBaseMigrationMinReportCount
            comparedReportCount = $loaded.Count
            bestPortableTemplate = $bestPortableBaseTemplate
            bestIndexSelectionBase = $bestIndexSelectionBase
            bestIndexSelectionLayoutMetadataAvailableCount = if ($bestIndexSelectionBase) { $bestIndexSelectionBase.layoutMetadataAvailableCount } else { 0 }
            rule = "Diagnostic only. A lower-arm base formula may move toward production only after a portable Avatar metadata selector stays below the threshold across reports and then passes full timeline glTF validation."
        }
    }
}

$jsonPath = Join-Path $OutputDir "humanoid_axis_report_compare_summary.json"
$familyCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_family.csv"
$axisCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_common_axis.csv"
$formulaReadinessCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_formula_readiness.csv"
$formulaReadinessCommonCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_common_formula_readiness.csv"
$axisSpaceCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_axis_space.csv"
$axisSpaceDetailsCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_axis_space_details.csv"
$offAxisPatternCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_off_axis_patterns.csv"
$armFormulaTimelineCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_arm_formula_timeline_gate.csv"
$armFormulaTimelineCommonCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_common_arm_formula_timeline_gate.csv"
$legFormulaTimelineCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_leg_formula_timeline_gate.csv"
$legFormulaTimelineCommonCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_common_leg_formula_timeline_gate.csv"
$singleMuscleFormulaGateCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_single_muscle_formula_gate.csv"
$singleMuscleFormulaGateCommonCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_common_single_muscle_formula_gate.csv"
$forearmStretchScaleCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_forearm_stretch_scale_probe.csv"
$forearmStretchScaleCommonCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_common_forearm_stretch_scale_probe.csv"
$distalStretchPoseAxisCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_distal_stretch_pose_axis_probe.csv"
$distalStretchPoseAxisCommonCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_common_distal_stretch_pose_axis_probe.csv"
$zeroBaseCandidateCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_zero_base_candidate_gate.csv"
$zeroBaseCandidateCommonCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_common_zero_base_candidate_gate.csv"
$zeroBaseSourceCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_zero_base_source_candidate_gate.csv"
$zeroBaseSourceCommonCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_common_zero_base_source_candidate_gate.csv"
$zeroBaseSourceTimelineCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_zero_base_source_timeline_fit.csv"
$zeroBaseSourceTimelineCommonCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_common_zero_base_source_timeline_fit.csv"
$zeroBaseArmTwistCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_zero_base_arm_twist_timeline_fit.csv"
$zeroBaseArmTwistCommonCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_common_zero_base_arm_twist_timeline_fit.csv"
$zeroBaseForearmPairCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_zero_base_forearm_pair_timeline_fit.csv"
$zeroBaseForearmPairCommonCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_common_zero_base_forearm_pair_timeline_fit.csv"
$partialOracleLowerArmCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_partial_oracle_lower_arm.csv"
$partialOracleLowerArmCommonCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_common_partial_oracle_lower_arm.csv"
$partialOracleBaseCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_partial_oracle_lower_arm_base_alignment.csv"
$partialOracleBaseCommonCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_common_partial_oracle_lower_arm_base_alignment.csv"
$partialOracleBaseTemplateGridCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_partial_oracle_lower_arm_base_template_grid.csv"
$partialOracleBaseTemplateGridCommonCsvPath = Join-Path $OutputDir "humanoid_axis_report_compare_common_partial_oracle_lower_arm_base_template_grid.csv"
$markdownPath = Join-Path $OutputDir "HUMANOID_AXIS_REPORT_COMPARE.md"

$summary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
$familyRows | Export-Csv -LiteralPath $familyCsvPath -NoTypeInformation -Encoding UTF8
$commonAxisBlockers | Export-Csv -LiteralPath $axisCsvPath -NoTypeInformation -Encoding UTF8
$formulaReadinessRows | Export-Csv -LiteralPath $formulaReadinessCsvPath -NoTypeInformation -Encoding UTF8
$commonFormulaReadinessRows | Export-Csv -LiteralPath $formulaReadinessCommonCsvPath -NoTypeInformation -Encoding UTF8
$commonAxisSpaceBlockers | Export-Csv -LiteralPath $axisSpaceCsvPath -NoTypeInformation -Encoding UTF8
$commonAxisSpaceDetailRows | Export-Csv -LiteralPath $axisSpaceDetailsCsvPath -NoTypeInformation -Encoding UTF8
$offAxisPatternRows | Export-Csv -LiteralPath $offAxisPatternCsvPath -NoTypeInformation -Encoding UTF8
$armFormulaTimelineRows | Export-Csv -LiteralPath $armFormulaTimelineCsvPath -NoTypeInformation -Encoding UTF8
$commonArmFormulaTimelineRows | Export-Csv -LiteralPath $armFormulaTimelineCommonCsvPath -NoTypeInformation -Encoding UTF8
$legFormulaTimelineRows | Export-Csv -LiteralPath $legFormulaTimelineCsvPath -NoTypeInformation -Encoding UTF8
$commonLegFormulaTimelineRows | Export-Csv -LiteralPath $legFormulaTimelineCommonCsvPath -NoTypeInformation -Encoding UTF8
$singleMuscleFormulaGateRows | Export-Csv -LiteralPath $singleMuscleFormulaGateCsvPath -NoTypeInformation -Encoding UTF8
$commonSingleMuscleFormulaGateRows | Export-Csv -LiteralPath $singleMuscleFormulaGateCommonCsvPath -NoTypeInformation -Encoding UTF8
$forearmStretchScaleRows | Export-Csv -LiteralPath $forearmStretchScaleCsvPath -NoTypeInformation -Encoding UTF8
$commonForearmStretchScaleRows | Export-Csv -LiteralPath $forearmStretchScaleCommonCsvPath -NoTypeInformation -Encoding UTF8
$distalStretchPoseAxisRows | Export-Csv -LiteralPath $distalStretchPoseAxisCsvPath -NoTypeInformation -Encoding UTF8
$commonDistalStretchPoseAxisRows | Export-Csv -LiteralPath $distalStretchPoseAxisCommonCsvPath -NoTypeInformation -Encoding UTF8
$zeroBaseCandidateRows | Export-Csv -LiteralPath $zeroBaseCandidateCsvPath -NoTypeInformation -Encoding UTF8
$commonZeroBaseCandidateRows | Export-Csv -LiteralPath $zeroBaseCandidateCommonCsvPath -NoTypeInformation -Encoding UTF8
$zeroBaseSourceRows | Export-Csv -LiteralPath $zeroBaseSourceCsvPath -NoTypeInformation -Encoding UTF8
$commonZeroBaseSourceRows | Export-Csv -LiteralPath $zeroBaseSourceCommonCsvPath -NoTypeInformation -Encoding UTF8
$zeroBaseSourceTimelineRows | Export-Csv -LiteralPath $zeroBaseSourceTimelineCsvPath -NoTypeInformation -Encoding UTF8
$commonZeroBaseSourceTimelineRows | Export-Csv -LiteralPath $zeroBaseSourceTimelineCommonCsvPath -NoTypeInformation -Encoding UTF8
$zeroBaseArmTwistRows | Export-Csv -LiteralPath $zeroBaseArmTwistCsvPath -NoTypeInformation -Encoding UTF8
$commonZeroBaseArmTwistRows | Export-Csv -LiteralPath $zeroBaseArmTwistCommonCsvPath -NoTypeInformation -Encoding UTF8
$zeroBaseForearmPairRows | Export-Csv -LiteralPath $zeroBaseForearmPairCsvPath -NoTypeInformation -Encoding UTF8
$commonZeroBaseForearmPairRowsAll | Export-Csv -LiteralPath $zeroBaseForearmPairCommonCsvPath -NoTypeInformation -Encoding UTF8
$partialOracleLowerArmRows | Export-Csv -LiteralPath $partialOracleLowerArmCsvPath -NoTypeInformation -Encoding UTF8
$commonPartialOracleLowerArmRows | Export-Csv -LiteralPath $partialOracleLowerArmCommonCsvPath -NoTypeInformation -Encoding UTF8
$partialOracleBaseRows | Export-Csv -LiteralPath $partialOracleBaseCsvPath -NoTypeInformation -Encoding UTF8
$commonPartialOracleBaseRows | Export-Csv -LiteralPath $partialOracleBaseCommonCsvPath -NoTypeInformation -Encoding UTF8
$partialOracleBaseTemplateGridRows | Export-Csv -LiteralPath $partialOracleBaseTemplateGridCsvPath -NoTypeInformation -Encoding UTF8
$commonPartialOracleBaseTemplateGridRows | Export-Csv -LiteralPath $partialOracleBaseTemplateGridCommonCsvPath -NoTypeInformation -Encoding UTF8

$md = New-Object System.Collections.Generic.List[string]
$md.Add("# Humanoid Axis Report Compare")
$md.Add("")
$md.Add("Diagnostic only. This comparison helps reverse-engineer Unity Humanoid muscle axis space; it does not create production binding or solver rules.")
$md.Add("")
$md.Add("## Reports")
Add-MarkdownLines $md (Write-MarkdownTable $reportSummaries @("label", "groupCount", "avgAxisTiltDegrees", "maxAxisTiltDegrees", "poseCandidateRate", "focusPoseCandidateRate", "armPoseCandidateRate"))
$md.Add("")
$md.Add("## Common Axis Families")
Add-MarkdownLines $md (Write-MarkdownTable (@($commonFamilies | Select-Object -First 12)) @("bodyGroup", "muscleFamily", "reportCount", "avgCandidateErrorDegrees", "maxCandidateErrorDegrees", "maxAxisTiltDegrees", "dominantCandidates"))
$md.Add("")
$md.Add("## Common Axis Blockers")
Add-MarkdownLines $md (Write-MarkdownTable (@($commonAxisBlockers | Select-Object -First 12)) @("humanBone", "attribute", "bodyGroup", "avgCandidateErrorDegrees", "maxCandidateErrorDegrees", "poseCandidateReportCount", "candidates"))
$md.Add("")
$md.Add("## Formula Readiness")
$md.Add("")
$md.Add("Diagnostic only. These rows combine source/target axis enumeration, multi-value limit checks, and signed Unity axis stability. They are a work queue for the next timeline formula probe, not production solver rules.")
Add-MarkdownLines $md (Write-MarkdownTable (@($commonFormulaReadinessRows | Select-Object -First 12)) @("targetHumanBone", "muscleName", "bodyGroup", "muscleFamily", "reportCount", "allReadyForFormulaProbe", "allNeedAxisFormulaOrBetter", "maxCurrentErrorDegrees", "maxBestEnumeratedErrorDegrees", "minLimitExplainedRate", "minAxisStableRate", "unityNearestAxes", "bestSourceHumanBoneVotes", "bestSourceAxisVotes", "bestTargetAxisVotes"))
$md.Add("")
$md.Add("## Common Axis-Space Blockers")
Add-MarkdownLines $md (Write-MarkdownTable (@($commonAxisSpaceBlockers | Select-Object -First 12)) @("targetHumanBone", "muscleName", "avgCurrentErrorDegrees", "avgBestEnumeratedErrorDegrees", "allLimitExplained", "allAxisStable", "bestHints"))
$md.Add("")
$md.Add("## Common Axis-Space Details")
Add-MarkdownLines $md (Write-MarkdownTable (@($commonAxisSpaceDetailRows | Select-Object -First 16)) @("label", "targetHumanBone", "muscleName", "predictedNearestSignedAxis", "unityNearestSignedAxis", "unityOffAxisSummary", "nearestAxisSourceCandidate", "axisTiltDegrees", "bestSourceHumanBone", "bestTargetAxisName", "bestSourceAxisName"))
$md.Add("")
$md.Add("## Off-Axis Patterns")
Add-MarkdownLines $md (Write-MarkdownTable (@($offAxisPatternRows | Select-Object -First 16)) @("targetHumanBone", "muscleName", "nearestAxes", "meanUnityAxisX", "meanUnityAxisY", "meanUnityAxisZ", "minUnityAxisX", "maxUnityAxisX", "minUnityAxisZ", "maxUnityAxisZ"))
$md.Add("")
$md.Add("## Arm Formula Timeline Gate")
$md.Add("")
$md.Add("Diagnostic only. These rows compare candidate formulas on the full animation timeline; shared improvements are evidence for reverse engineering, not production solver rules.")
Add-MarkdownLines $md (Write-MarkdownTable (@($commonArmFormulaTimelineRows | Select-Object -First 12)) @("family", "candidateName", "reportCount", "foundReportCount", "allCandidateFound", "allBeatsCurrent", "avgTrackMaxDegrees", "maxDegrees", "minAvgImprovementDegrees", "maxAvgImprovementDegrees", "perReport"))
$md.Add("")
$md.Add("## Leg Formula Timeline Gate")
$md.Add("")
$md.Add("Diagnostic only. These rows compare leg/foot candidate formulas on the full animation timeline using Leg bodyGroup metrics; shared improvements are evidence for reverse engineering, not production solver rules.")
Add-MarkdownLines $md (Write-MarkdownTable (@($commonLegFormulaTimelineRows | Select-Object -First 12)) @("family", "candidateName", "reportCount", "foundReportCount", "allCandidateFound", "allBeatsCurrent", "avgTrackMaxDegrees", "maxDegrees", "minAvgImprovementDegrees", "maxAvgImprovementDegrees", "perReport"))
$md.Add("")
$md.Add("## Single Muscle Formula Gate")
$md.Add("")
$md.Add("Diagnostic only. These rows keep the best side-specific formula for each single muscle. A common ready row means the single-muscle Unity delta is reproduced across reports; full timeline still needs a separate zero/base pose check.")
Add-MarkdownLines $md (Write-MarkdownTable (@($commonSingleMuscleFormulaGateRows | Select-Object -First 12)) @("targetHumanBone", "bodyGroup", "muscleName", "muscleFamily", "reportCount", "allReady", "anyNotReady", "maxErrorDegrees", "formulas", "sideModes", "sourceHints", "perReport"))
$md.Add("")
$md.Add("## Forearm Stretch Scale Probe")
$md.Add("")
$md.Add("Diagnostic only. These rows test whether AvatarConstant twist/stretch scalar parameters explain Forearm Stretch single-muscle delta errors. Shared scale candidates are evidence for formula search, not production solver rules.")
Add-MarkdownLines $md (Write-MarkdownTable (@($commonForearmStretchScaleRows | Select-Object -First 12)) @("targetHumanBone", "bodyGroup", "muscleName", "reportCount", "allBeatCurrent", "anyNotReady", "maxBestErrorDegrees", "minImprovementDegrees", "maxImprovementDegrees", "scaleNames", "scaleValues", "formulas", "sideModes", "perReport"))
$md.Add("")
$md.Add("## Distal Stretch Pose-Axis Probe")
$md.Add("")
$md.Add("Diagnostic only. These rows test whether Avatar/rest pose tilted Stretch axes improve the full timeline. A single-model improvement is not enough; useful candidates must beat current across reports.")
Add-MarkdownLines $md (Write-MarkdownTable (@($commonDistalStretchPoseAxisRows | Select-Object -First 12)) @("candidateName", "poseAxisCandidate", "poseApplyMode", "reportCount", "allBeatCurrent", "anyBeatCurrent", "avgTrackMaxDegrees", "maxDegrees", "minAvgImprovementDegrees", "maxAvgImprovementDegrees", "perReport"))
$md.Add("")
$md.Add("## Zero Base Candidate Gate")
$md.Add("")
$md.Add("Diagnostic only. These rows compare per-bone zero/base pose candidates; unityZeroBaseOracle proves an upper bound only, while rest/localRest/avatar pose families are deterministic metadata candidates.")
Add-MarkdownLines $md (Write-MarkdownTable (@($commonZeroBaseCandidateRows | Select-Object -First 12)) @("humanBone", "bodyGroup", "candidateFamily", "reportCount", "allDeterministic", "allOracleOnly", "allBeatsCurrent", "maxBestDegrees", "minImprovementMaxDegrees", "maxImprovementMaxDegrees", "nextFormulaWork", "perReport"))
$md.Add("")
$md.Add("## Zero Base Source Candidate Gate")
$md.Add("")
$md.Add("Diagnostic only. These rows compare deterministic short-product formulas directly against Unity zero base, before full timeline residual testing.")
Add-MarkdownLines $md (Write-MarkdownTable (@($commonZeroBaseSourceRows | Select-Object -First 12)) @("humanBone", "bodyGroup", "formulaFamily", "formulaSources", "reportCount", "allBeatsCurrent", "maxBestFormulaErrorDegrees", "minImprovementDegrees", "maxImprovementDegrees", "nextFormulaWork", "perReport"))
$md.Add("")
$md.Add("## Zero Base Source Timeline Fit")
$md.Add("")
$md.Add("Diagnostic only. These rows take the best deterministic zero-base source formula and test how it combines with full timeline deltas. Shared improvements are evidence for formula migration, not production rules.")
Add-MarkdownLines $md (Write-MarkdownTable (@($commonZeroBaseSourceTimelineRows | Select-Object -First 12)) @("humanBone", "bodyGroup", "applyMode", "formulaFamilies", "formulaSources", "reportCount", "allBeatsCurrent", "maxDegrees", "minImprovementMaxDegrees", "maxImprovementMaxDegrees", "perReport"))
$md.Add("")
$md.Add("## Zero Base Arm Twist Timeline Fit")
$md.Add("")
$md.Add("Diagnostic only. These rows keep the best deterministic lower-arm zero base, then swap ranked Arm Twist dynamic variants. Shared improvements over zero-base current are evidence for remaining dynamic delta work.")
Add-MarkdownLines $md (Write-MarkdownTable (@($commonZeroBaseArmTwistRows | Select-Object -First 12)) @("humanBone", "bodyGroup", "applyMode", "armTwistVariant", "formulaFamilies", "reportCount", "allBeatZeroBaseCurrent", "maxDegrees", "minImprovementOverZeroBaseCurrentDegrees", "maxImprovementOverZeroBaseCurrentDegrees", "perReport"))
$md.Add("")
$md.Add("## Zero Base Forearm Pair Timeline Fit")
$md.Add("")
$md.Add("Diagnostic only. These rows keep the best deterministic lower-arm zero base, then recombine Forearm Stretch and Arm Twist as paired dynamic deltas.")
Add-MarkdownLines $md (Write-MarkdownTable (@($commonZeroBaseForearmPairRows | Select-Object -First 12)) @("humanBone", "bodyGroup", "applyMode", "pairMode", "pairFamily", "stretchFormula", "twistFormula", "stretchSide", "twistSide", "orderMode", "outputSide", "allBeatZeroBaseCurrent", "maxDegrees", "perReport"))
$md.Add("")
$md.Add("### Lower-Leg Common Candidates")
Add-MarkdownLines $md (Write-MarkdownTable (@($commonZeroBaseForearmPairRowsAll | Where-Object { $_.humanBone -like '*LowerLeg*' } | Sort-Object @{ Expression = "maxDegrees"; Descending = $false }, @{ Expression = "allBeatZeroBaseCurrent"; Descending = $true } | Select-Object -First 12)) @("humanBone", "bodyGroup", "applyMode", "pairMode", "pairFamily", "allBeatZeroBaseCurrent", "maxDegrees", "minImprovementOverZeroBaseCurrentDegrees", "perReport"))
$md.Add("")
$md.Add("## Partial Oracle Lower Arm Timeline Replacement")
$md.Add("")
$md.Add("Diagnostic only. These rows replace lower-arm Arm Twist, Forearm Stretch, or both with Unity single-muscle oracle deltas while keeping the rest of the lower-arm formula current. Shared wins identify which production formula segment must be reverse-engineered next.")
Add-MarkdownLines $md (Write-MarkdownTable (@($commonPartialOracleLowerArmRows | Select-Object -First 12)) @("mode", "replacement", "deltaMode", "reportCount", "allBeatCurrent", "maxDegrees", "avgTrackMaxDegrees", "minMaxImprovementDegrees", "minAvgImprovementDegrees", "perReport"))
$md.Add("")
$md.Add("### Lower-Arm Oracle Base Alignment")
Add-MarkdownLines $md (Write-MarkdownTable (@($commonPartialOracleBaseRows | Select-Object -First 12)) @("targetHumanBone", "muscleName", "muscleFamily", "reportCount", "maxBestBaseErrorDegrees", "avgBestBaseErrorDegrees", "maxReducedBaseErrorDegrees", "avgReducedBaseErrorDegrees", "maxTemplateBaseErrorDegrees", "avgTemplateBaseErrorDegrees", "maxIndexSelectionBaseErrorDegrees", "avgIndexSelectionBaseErrorDegrees", "minCurrentZeroErrorDegrees", "maxCurrentZeroErrorDegrees", "layoutNodeIndexes", "layoutParentIndexes", "layoutSameIndexFlags", "layoutAngleDefaultLocalSameVsHuman", "layoutAngleDefaultParentLocalSameVsHuman", "layoutAngleLocalRestVsDefaultLocalSame", "layoutAngleParentRestVsAvatarSkeletonDefaultParentLocal", "layoutAngleCrossMirrorTargetToOppositeVsOppositeToTarget", "maxBestBaseCandidateFactorCount", "maxReducedBaseCandidateFactorCount", "families", "portabilities", "effectiveRoleSignatures", "reducedFamilies", "reducedPortabilities", "reducedEffectiveRoleSignatures", "templateFamilies", "templatePortabilities", "templateEffectiveRoleSignatures", "indexSelectionFamilies", "indexSelectionPortabilities", "indexSelectionEffectiveRoleSignatures", "candidates", "reducedCandidates", "templateCandidates", "indexSelectionCandidates"))
$md.Add("")
$md.Add("### Lower-Arm Base Template Grid")
$md.Add("")
$md.Add("Diagnostic only. These rows cross-apply the current four lower-arm base templates across reports. A template that only works for its source model/side is a clue for AvatarConstant index selection, not a production rule.")
Add-MarkdownLines $md (Write-MarkdownTable (@($commonPartialOracleBaseTemplateGridRows | Select-Object -First 12)) @("candidate", "roleSignature", "reportCount", "maxErrorDegrees", "avgErrorDegrees", "bestTargets", "worstTargets", "perReport"))
$md.Add("")
$md.Add("### Lower-Arm Base Migration Gate")
$md.Add("")
$md.Add("Diagnostic only. This gate prevents model-specific lower-arm base templates from being mistaken for production formulas.")
Add-MarkdownLines $md (Write-MarkdownTable @([pscustomobject]@{
    status = $lowerArmBaseMigrationGateStatus
    thresholdDegrees = $lowerArmBaseMigrationThresholdDegrees
    minReportCount = $lowerArmBaseMigrationMinReportCount
    comparedReportCount = $loaded.Count
    bestTemplateMaxDegrees = if ($bestPortableBaseTemplate) { $bestPortableBaseTemplate.maxErrorDegrees } else { $null }
    bestTemplate = if ($bestPortableBaseTemplate) { $bestPortableBaseTemplate.candidate } else { "" }
    bestIndexSelectionMaxDegrees = if ($bestIndexSelectionBase) { $bestIndexSelectionBase.maxIndexSelectionBaseErrorDegrees } else { $null }
    bestIndexSelectionLayoutMetadataAvailableCount = if ($bestIndexSelectionBase) { $bestIndexSelectionBase.layoutMetadataAvailableCount } else { 0 }
    bestIndexSelection = if ($bestIndexSelectionBase) { $bestIndexSelectionBase.indexSelectionCandidates } else { "" }
    reason = $lowerArmBaseMigrationGateReason
}) @("status", "thresholdDegrees", "minReportCount", "comparedReportCount", "bestTemplateMaxDegrees", "bestTemplate", "bestIndexSelectionMaxDegrees", "bestIndexSelectionLayoutMetadataAvailableCount", "bestIndexSelection", "reason"))
$md | Set-Content -LiteralPath $markdownPath -Encoding UTF8

Write-Host "Humanoid axis compare summary: $jsonPath"
Write-Host "Family CSV: $familyCsvPath"
Write-Host "Common axis CSV: $axisCsvPath"
Write-Host "Formula readiness CSV: $formulaReadinessCsvPath"
Write-Host "Common formula readiness CSV: $formulaReadinessCommonCsvPath"
Write-Host "Axis-space CSV: $axisSpaceCsvPath"
Write-Host "Axis-space details CSV: $axisSpaceDetailsCsvPath"
Write-Host "Off-axis pattern CSV: $offAxisPatternCsvPath"
Write-Host "Arm formula timeline gate CSV: $armFormulaTimelineCsvPath"
Write-Host "Common arm formula timeline gate CSV: $armFormulaTimelineCommonCsvPath"
Write-Host "Leg formula timeline gate CSV: $legFormulaTimelineCsvPath"
Write-Host "Common leg formula timeline gate CSV: $legFormulaTimelineCommonCsvPath"
Write-Host "Single muscle formula gate CSV: $singleMuscleFormulaGateCsvPath"
Write-Host "Common single muscle formula gate CSV: $singleMuscleFormulaGateCommonCsvPath"
Write-Host "Forearm stretch scale probe CSV: $forearmStretchScaleCsvPath"
Write-Host "Common forearm stretch scale probe CSV: $forearmStretchScaleCommonCsvPath"
Write-Host "Distal stretch pose-axis probe CSV: $distalStretchPoseAxisCsvPath"
Write-Host "Common distal stretch pose-axis probe CSV: $distalStretchPoseAxisCommonCsvPath"
Write-Host "Zero base candidate gate CSV: $zeroBaseCandidateCsvPath"
Write-Host "Common zero base candidate gate CSV: $zeroBaseCandidateCommonCsvPath"
Write-Host "Zero base source candidate gate CSV: $zeroBaseSourceCsvPath"
Write-Host "Common zero base source candidate gate CSV: $zeroBaseSourceCommonCsvPath"
Write-Host "Zero base source timeline fit CSV: $zeroBaseSourceTimelineCsvPath"
Write-Host "Common zero base source timeline fit CSV: $zeroBaseSourceTimelineCommonCsvPath"
Write-Host "Zero base arm twist timeline fit CSV: $zeroBaseArmTwistCsvPath"
Write-Host "Common zero base arm twist timeline fit CSV: $zeroBaseArmTwistCommonCsvPath"
Write-Host "Zero base forearm pair timeline fit CSV: $zeroBaseForearmPairCsvPath"
Write-Host "Common zero base forearm pair timeline fit CSV: $zeroBaseForearmPairCommonCsvPath"
Write-Host "Partial oracle lower arm CSV: $partialOracleLowerArmCsvPath"
Write-Host "Common partial oracle lower arm CSV: $partialOracleLowerArmCommonCsvPath"
Write-Host "Partial oracle lower arm base alignment CSV: $partialOracleBaseCsvPath"
Write-Host "Common partial oracle lower arm base alignment CSV: $partialOracleBaseCommonCsvPath"
Write-Host "Partial oracle lower arm base template grid CSV: $partialOracleBaseTemplateGridCsvPath"
Write-Host "Common partial oracle lower arm base template grid CSV: $partialOracleBaseTemplateGridCommonCsvPath"
Write-Host "Markdown: $markdownPath"
