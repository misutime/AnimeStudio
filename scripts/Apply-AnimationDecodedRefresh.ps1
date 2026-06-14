param(
    [Parameter(Mandatory = $true)]
    [string]$LibraryRoot,

    [Parameter(Mandatory = $true)]
    [string]$RefreshRoot,

    [string]$AnimationName,
    [switch]$DryRun,
    [switch]$NoBackup
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $LibraryRoot)) {
    throw "LibraryRoot does not exist: $LibraryRoot"
}
if (-not (Test-Path -LiteralPath $RefreshRoot)) {
    throw "RefreshRoot does not exist: $RefreshRoot"
}

$helperDir = Join-Path ([System.IO.Path]::GetTempPath()) "AnimeStudioAnimationDecodedRefreshApply"
$helperPy = Join-Path $helperDir "apply_decoded_refresh.py"
New-Item -ItemType Directory -Force -Path $helperDir | Out-Null

$helperSource = @'
import argparse
import json
import os
import shutil
from pathlib import Path

def load_json(path):
    with open(path, "r", encoding="utf-8-sig") as f:
        return json.load(f)

def write_json(path, data):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
        f.write("\n")

def iter_sidecars(root):
    for dirpath, _, files in os.walk(root):
        for name in files:
            if name.endswith(".animation_asset.json"):
                yield os.path.join(dirpath, name)

def rel_key(root, path):
    rel = os.path.relpath(path, root).replace(os.sep, "/")
    parts = rel.split("/")
    if "Animations" in parts:
        index = parts.index("Animations")
        return "/".join(parts[index:])
    return rel

def decoded_summary(decoded):
    decoded = decoded or {}
    return {
        "status": decoded.get("status"),
        "playbackKind": decoded.get("playbackKind"),
        "directGltfReady": decoded.get("directGltfReady"),
        "requiresInternalHumanoidSolve": decoded.get("requiresInternalHumanoidSolve"),
        "curveCounts": decoded.get("curveCounts"),
    }

def decoded_can_replace_target(decoded):
    return (decoded.get("status") or "").lower() in ("ok", "no_decoded_keyframes")

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--library-root", required=True)
    parser.add_argument("--refresh-root", required=True)
    parser.add_argument("--animation-name")
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--no-backup", action="store_true")
    args = parser.parse_args()

    library_root = os.path.abspath(args.library_root)
    refresh_root = os.path.abspath(args.refresh_root)
    results = []
    for refresh_path in iter_sidecars(refresh_root):
        refresh_json = load_json(refresh_path)
        if args.animation_name and refresh_json.get("name") != args.animation_name:
            continue
        decoded = refresh_json.get("decoded") or {}
        if not decoded_can_replace_target(decoded):
            results.append({
                "refreshPath": refresh_path,
                "status": "skipped_refresh_decoded_not_replaceable",
                "decoded": decoded_summary(decoded),
            })
            continue

        key = rel_key(refresh_root, refresh_path)
        target_path = os.path.join(library_root, key.replace("/", os.sep))
        if not os.path.exists(target_path):
            target_path = None
        if not target_path:
            matches = []
            for path in iter_sidecars(library_root):
                try:
                    item = load_json(path)
                except Exception:
                    continue
                if item.get("name") == refresh_json.get("name") and item.get("pathId") == refresh_json.get("pathId"):
                    matches.append(path)
            if len(matches) == 1:
                target_path = matches[0]

        if not target_path:
            results.append({
                "refreshPath": refresh_path,
                "relativeKey": key,
                "name": refresh_json.get("name"),
                "pathId": refresh_json.get("pathId"),
                "status": "missing_target_sidecar",
            })
            continue

        target_json = load_json(target_path)
        identity_ok = (
            target_json.get("name") == refresh_json.get("name")
            and target_json.get("pathId") == refresh_json.get("pathId")
        )
        if not identity_ok:
            results.append({
                "refreshPath": refresh_path,
                "targetPath": target_path,
                "status": "identity_mismatch",
                "refresh": {"name": refresh_json.get("name"), "pathId": refresh_json.get("pathId")},
                "target": {"name": target_json.get("name"), "pathId": target_json.get("pathId")},
            })
            continue

        old_decoded = target_json.get("decoded") or {}
        result = {
            "refreshPath": refresh_path,
            "targetPath": target_path,
            "name": refresh_json.get("name"),
            "pathId": refresh_json.get("pathId"),
            "oldDecoded": decoded_summary(old_decoded),
            "newDecoded": decoded_summary(decoded),
            "status": "dry_run" if args.dry_run else "updated",
        }
        if not args.dry_run:
            if not args.no_backup:
                backup = target_path + ".decoded_refresh.bak"
                if not os.path.exists(backup):
                    shutil.copy2(target_path, backup)
                result["backupPath"] = backup
            target_json["decoded"] = decoded
            target_json["decodedRefresh"] = {
                "appliedFrom": refresh_path,
                "rule": "Only decoded block is replaced. Identity must match animation name and Unity PathID.",
            }
            write_json(target_path, target_json)
        results.append(result)

    summary = {
        "libraryRoot": library_root,
        "refreshRoot": refresh_root,
        "animationName": args.animation_name,
        "dryRun": args.dry_run,
        "count": len(results),
        "updated": sum(1 for x in results if x.get("status") == "updated"),
        "dryRunMatches": sum(1 for x in results if x.get("status") == "dry_run"),
        "skipped": sum(1 for x in results if x.get("status") not in ("updated", "dry_run")),
        "results": results,
    }
    print(json.dumps(summary, ensure_ascii=False, indent=2))

if __name__ == "__main__":
    main()
'@

Set-Content -LiteralPath $helperPy -Value $helperSource -Encoding UTF8

$arguments = @(
    $helperPy,
    "--library-root", $LibraryRoot,
    "--refresh-root", $RefreshRoot
)
if (-not [string]::IsNullOrWhiteSpace($AnimationName)) {
    $arguments += @("--animation-name", $AnimationName)
}
if ($DryRun) {
    $arguments += "--dry-run"
}
if ($NoBackup) {
    $arguments += "--no-backup"
}

python @arguments
