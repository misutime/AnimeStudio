param(
    [string]$LibraryRoot,
    [string]$ModelsDir,
    [string[]]$VariantSuffixes = @("_Kanban_Edit", "_Kanban", "_Manekin", "_Remote", "_Edit"),
    [switch]$Apply,
    [switch]$RemoveEmptyParents
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ModelsDir)) {
    if ([string]::IsNullOrWhiteSpace($LibraryRoot)) {
        throw "Please pass -LibraryRoot <AS library root> or -ModelsDir <Models directory>."
    }

    $ModelsDir = Join-Path $LibraryRoot "Models"
}

if (-not (Test-Path -LiteralPath $ModelsDir)) {
    throw "Models directory does not exist: $ModelsDir"
}

$modelsRoot = (Resolve-Path -LiteralPath $ModelsDir).Path
$orderedSuffixes = $VariantSuffixes | Sort-Object Length -Descending
$deletedManifest = if ($Apply) { Join-Path $modelsRoot "_pruned_prefab_variants.jsonl" } else { $null }

function Test-ModelDirectory {
    param([System.IO.DirectoryInfo]$Dir)

    return [System.IO.Directory]::EnumerateFiles($Dir.FullName, "*.gltf", [System.IO.SearchOption]::TopDirectoryOnly).GetEnumerator().MoveNext() -or
           [System.IO.Directory]::EnumerateFiles($Dir.FullName, "*.glb", [System.IO.SearchOption]::TopDirectoryOnly).GetEnumerator().MoveNext() -or
           [System.IO.Directory]::EnumerateFiles($Dir.FullName, "*.fbx", [System.IO.SearchOption]::TopDirectoryOnly).GetEnumerator().MoveNext()
}

function Remove-EmptyParentsUpToRoot {
    param([string]$StartPath, [string]$RootPath)

    $current = [System.IO.DirectoryInfo]::new($StartPath)
    while ($null -ne $current -and $current.FullName.StartsWith($RootPath, [System.StringComparison]::OrdinalIgnoreCase) -and $current.FullName -ne $RootPath) {
        if ([System.IO.Directory]::EnumerateFileSystemEntries($current.FullName).GetEnumerator().MoveNext()) {
            break
        }

        $parent = $current.Parent
        Remove-Item -LiteralPath $current.FullName -Force
        $current = $parent
    }
}

$dirs = Get-ChildItem -LiteralPath $modelsRoot -Directory -Recurse |
    Sort-Object { $_.FullName.Length } -Descending

$candidates = New-Object System.Collections.Generic.List[object]

foreach ($dir in $dirs) {
    foreach ($suffix in $orderedSuffixes) {
        if (-not $dir.Name.EndsWith($suffix, [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $baseName = $dir.Name.Substring(0, $dir.Name.Length - $suffix.Length)
        if ([string]::IsNullOrWhiteSpace($baseName)) {
            continue
        }

        $basePath = Join-Path $dir.Parent.FullName $baseName
        if (-not (Test-Path -LiteralPath $basePath -PathType Container)) {
            continue
        }

        $baseDir = Get-Item -LiteralPath $basePath
        if (-not (Test-ModelDirectory $baseDir)) {
            continue
        }

        if (-not (Test-ModelDirectory $dir)) {
            continue
        }

        $candidates.Add([pscustomobject]@{
            Variant = $dir.FullName
            Base = $baseDir.FullName
            Suffix = $suffix
            Name = $dir.Name
        })
        break
    }
}

Write-Host "Models root: $modelsRoot"
Write-Host "Variant suffixes: $($orderedSuffixes -join ', ')"
Write-Host "Matched variant directories: $($candidates.Count)"
Write-Host "Mode: $(if ($Apply) { 'APPLY - deleting matched variants' } else { 'DRY-RUN - no files will be deleted' })"

$preview = $candidates | Select-Object -First 80
foreach ($item in $preview) {
    Write-Host "[variant] $($item.Name) -> base $(Split-Path -Leaf $item.Base)"
}

if ($candidates.Count -gt $preview.Count) {
    Write-Host "... $($candidates.Count - $preview.Count) more variant directorie(s) omitted from console preview."
}

if (-not $Apply) {
    Write-Host ""
    Write-Host "Dry-run only. Re-run with -Apply to delete these variant directories."
    exit 0
}

if (Test-Path -LiteralPath $deletedManifest) {
    Remove-Item -LiteralPath $deletedManifest -Force
}

$deleted = 0
foreach ($item in $candidates) {
    $record = [ordered]@{
        deletedAt = [DateTimeOffset]::Now.ToString("O")
        variant = $item.Variant
        base = $item.Base
        suffix = $item.Suffix
    } | ConvertTo-Json -Compress
    Add-Content -LiteralPath $deletedManifest -Value $record -Encoding UTF8

    Remove-Item -LiteralPath $item.Variant -Recurse -Force
    $deleted++

    if ($RemoveEmptyParents) {
        Remove-EmptyParentsUpToRoot -StartPath (Split-Path -Parent $item.Variant) -RootPath $modelsRoot
    }

    if ($deleted -eq 1 -or $deleted % 100 -eq 0) {
        Write-Host "Deleted $deleted/$($candidates.Count) variant directorie(s)..."
    }
}

Write-Host "Done. Deleted $deleted variant directorie(s). Manifest: $deletedManifest"
