param(
    [Parameter(Mandatory = $true)]
    [string]$LibraryRoot,

    [string]$IndexPath,
    [string]$OutputDir,
    [string]$Game = "GI",
    [string]$SourceRoot,
    [string]$AnimationName,
    [int]$Limit = 200,
    [string[]]$Statuses = @("skipped", "compacted", "missing", "unreadable", "no_decoded_keyframes"),
    [switch]$IncludeOk,
    [switch]$CommandsOnly
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $LibraryRoot)) {
    throw "LibraryRoot does not exist: $LibraryRoot"
}

if ([string]::IsNullOrWhiteSpace($IndexPath)) {
    $IndexPath = Join-Path $LibraryRoot "library_index.db"
}
if (-not (Test-Path -LiteralPath $IndexPath)) {
    throw "SQLite library index does not exist: $IndexPath"
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $LibraryRoot "AnimationDecodedRefreshPlan"
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$helperDir = Join-Path ([System.IO.Path]::GetTempPath()) "AnimeStudioAnimationDecodedRefreshPlan"
$helperPy = Join-Path $helperDir "write_plan.py"
New-Item -ItemType Directory -Force -Path $helperDir | Out-Null

$helperSource = @'
import argparse
import csv
import json
import os
import re
import sqlite3
from pathlib import Path

def load_json(path):
    with open(path, "r", encoding="utf-8-sig") as f:
        return json.load(f)

def resolve_path(root, value):
    if not value:
        return None
    return value if os.path.isabs(value) else os.path.join(root, value.replace("/", os.sep))

def read_decoded_status(sidecar):
    if not sidecar or not os.path.exists(sidecar):
        return {
            "status": "missing",
            "playback": None,
            "direct": None,
            "error": None,
            "curve_counts": {},
            "is_empty_ok": False,
            "refresh_reason": "missing_sidecar",
        }
    try:
        data = load_json(sidecar)
    except Exception as ex:
        return {
            "status": "unreadable",
            "playback": None,
            "direct": None,
            "error": type(ex).__name__,
            "curve_counts": {},
            "is_empty_ok": False,
            "refresh_reason": "unreadable_sidecar",
        }
    decoded = data.get("decoded") or {}
    status = decoded.get("status") or "missing"
    playback = decoded.get("playbackKind")
    direct = decoded.get("directGltfReady")
    curve_counts = {
        "translations": len(decoded.get("translations") or []),
        "rotations": len(decoded.get("rotations") or []),
        "scales": len(decoded.get("scales") or []),
        "floats": len(decoded.get("floats") or []),
        "pptrs": len(decoded.get("pptrs") or []),
    }
    is_empty_ok = status.lower() == "ok" and sum(curve_counts.values()) == 0
    return {
        "status": status,
        "playback": playback,
        "direct": direct,
        "error": None,
        "curve_counts": curve_counts,
        "is_empty_ok": is_empty_ok,
        "refresh_reason": "ok_but_no_decoded_curves" if is_empty_ok else status,
    }

def ps_single_quote(value):
    return "'" + str(value).replace("'", "''") + "'"

def regex_exact(value):
    return "^" + re.escape(value or "") + "$"

def relative_source_file(source_root, source_path):
    if not source_root or not source_path:
        return None
    try:
        rel = os.path.relpath(source_path, source_root)
    except ValueError:
        return None
    if rel.startswith(".."):
        return None
    return rel.replace(os.sep, "/")

def read_source_index_root(library_root):
    source_index = os.path.join(library_root, "unity_source_index.db")
    if not os.path.exists(source_index):
        return None
    try:
        con = sqlite3.connect(source_index)
        row = con.execute("SELECT value FROM metadata WHERE key='sourceRoot' LIMIT 1").fetchone()
        con.close()
        return row[0] if row and row[0] else None
    except Exception:
        return None

def command_for(args, row):
    source_root = args.resolved_source_root
    target = os.path.join(args.output_dir, "RefreshExports", safe_name(row["name"]))
    source_file = relative_source_file(source_root, row.get("source"))
    parts = [
        "dotnet run --project AnimeStudio.CLI\\AnimeStudio.CLI.csproj -c Debug --framework net9.0-windows --",
        ps_single_quote(source_root),
        ps_single_quote(target),
        "--game " + args.game,
        "--mode Library",
        "--group_assets ByLibrary",
        "--model_format Gltf",
        "--animation_package Separate",
        "--export_full_decoded_animation_curves",
        "--skip_sqlite_index",
        "--source_index " + ps_single_quote(os.path.join(args.library_root, "unity_source_index.db")),
        "--names " + ps_single_quote(regex_exact(row["name"])),
    ]
    if source_file:
        parts.append("--source_files " + ps_single_quote(source_file))
    if row.get("path_id") is not None:
        parts.append("--path_ids " + str(row["path_id"]))
    return " ".join(parts)

def apply_command_for(args, row):
    target = os.path.join(args.output_dir, "RefreshExports", safe_name(row["name"]))
    return " ".join([
        "powershell -ExecutionPolicy Bypass -File scripts\\Apply-AnimationDecodedRefresh.ps1",
        "-LibraryRoot " + ps_single_quote(args.library_root),
        "-RefreshRoot " + ps_single_quote(target),
        "-AnimationName " + ps_single_quote(row["name"]),
    ])

def safe_name(value):
    value = value or "animation"
    invalid = set('<>:"/\\\\|?*')
    cleaned = ''.join("_" if c in invalid or ord(c) < 32 else c for c in value)
    return cleaned.strip(" .")[:120] or "animation"

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--library-root", required=True)
    parser.add_argument("--index-path", required=True)
    parser.add_argument("--output-dir", required=True)
    parser.add_argument("--game", required=True)
    parser.add_argument("--source-root")
    parser.add_argument("--animation-name")
    parser.add_argument("--limit", type=int, default=200)
    parser.add_argument("--statuses", default="skipped,compacted,missing,unreadable,no_decoded_keyframes")
    parser.add_argument("--include-ok", action="store_true")
    args = parser.parse_args()
    source_index_root = read_source_index_root(args.library_root)
    requested_source_root = args.source_root or args.library_root
    # Targeted refresh commands must use the same root recorded in unity_source_index.db.
    args.resolved_source_root = source_index_root or requested_source_root

    wanted = {x.strip().lower() for x in args.statuses.split(",") if x.strip()}
    con = sqlite3.connect(args.index_path)
    con.row_factory = sqlite3.Row
    # 先把“可进入内部 Humanoid 求解器”的模型做成临时表。
    # 这样后续统计只按少量已具备 avatar.internalSolver 的模型走索引，不扫全量候选。
    con.execute("DROP TABLE IF EXISTS temp.processable_models")
    con.execute("""
CREATE TEMP TABLE processable_models AS
SELECT output
FROM assets
WHERE kind='Model'
  AND json_type(raw_json, '$.avatar.internalSolver') IS NOT NULL
""")
    con.execute("CREATE INDEX temp.idx_processable_models_output ON processable_models(output)")
    con.execute("DROP TABLE IF EXISTS temp.processable_animation_counts")
    con.execute("""
CREATE TEMP TABLE processable_animation_counts AS
SELECT c.animation_output,
       COUNT(*) AS processable_internal_solver_candidate_count,
       COUNT(DISTINCT c.model_output) AS processable_internal_solver_model_count
FROM processable_models m
JOIN model_animation_candidates c
  ON c.relation_source='explicit'
 AND c.model_output=m.output
WHERE COALESCE(json_extract(c.raw_json, '$.nextAction'), '')='generate_preview_gltf'
  AND COALESCE(json_extract(c.raw_json, '$.requiresInternalHumanoidSolve'), 0)=1
GROUP BY c.animation_output
""")
    con.execute("CREATE INDEX temp.idx_processable_animation_counts_output ON processable_animation_counts(animation_output)")

    if args.animation_name:
        row_limit = 0
        rows = con.execute("""
SELECT a.name,
       a.output,
       a.source,
       a.container,
       a.path_id,
       json_extract(a.raw_json, '$.animationAsset') AS animation_asset,
       json_extract(a.raw_json, '$.animationType') AS animation_type,
       json_extract(a.raw_json, '$.hasMuscleClip') AS has_muscle_clip,
       json_extract(a.raw_json, '$.humanoidBindingCount') AS humanoid_binding_count,
       json_extract(a.raw_json, '$.transformBindingCount') AS transform_binding_count,
       COALESCE(p.processable_internal_solver_candidate_count, 0) AS processable_internal_solver_candidate_count,
       COALESCE(p.processable_internal_solver_model_count, 0) AS processable_internal_solver_model_count,
       COUNT(c.id) AS candidate_count,
       COUNT(DISTINCT c.model_output) AS model_count
FROM assets a
LEFT JOIN model_animation_candidates c
  ON a.output = c.animation_output AND c.relation_source='explicit'
LEFT JOIN processable_animation_counts p
  ON p.animation_output = a.output
WHERE a.kind='Animation' AND a.name = ?
GROUP BY a.output
ORDER BY candidate_count DESC, model_count DESC, a.name COLLATE NOCASE
""", (args.animation_name,)).fetchall()
    else:
        row_limit = 0
        rows = con.execute(f"""
SELECT a.name,
       a.output,
       a.source,
       a.container,
       a.path_id,
       json_extract(a.raw_json, '$.animationAsset') AS animation_asset,
       json_extract(a.raw_json, '$.animationType') AS animation_type,
       json_extract(a.raw_json, '$.hasMuscleClip') AS has_muscle_clip,
       json_extract(a.raw_json, '$.humanoidBindingCount') AS humanoid_binding_count,
       json_extract(a.raw_json, '$.transformBindingCount') AS transform_binding_count,
       COALESCE(p.processable_internal_solver_candidate_count, 0) AS processable_internal_solver_candidate_count,
       COALESCE(p.processable_internal_solver_model_count, 0) AS processable_internal_solver_model_count,
       s.explicit_model_count AS candidate_count,
       s.explicit_model_count AS model_count
FROM model_animation_candidate_animation_summary s
JOIN assets a ON a.output = s.animation_output AND a.kind='Animation'
LEFT JOIN processable_animation_counts p
  ON p.animation_output = a.output
WHERE s.explicit_model_count > 0
ORDER BY processable_internal_solver_candidate_count DESC,
         s.internal_humanoid_model_count DESC,
         s.explicit_model_count DESC,
         a.name COLLATE NOCASE
""").fetchall()
    con.close()

    selected = []
    for row in rows:
        item = dict(row)
        sidecar = resolve_path(args.library_root, item.get("animation_asset"))
        decoded = read_decoded_status(sidecar)
        status = decoded["status"]
        item["decodedStatus"] = status
        item["decodedRefreshReason"] = decoded["refresh_reason"]
        item["playbackKind"] = decoded["playback"]
        item["directGltfReady"] = decoded["direct"]
        item["sidecarError"] = decoded["error"]
        item["decodedCurveCounts"] = decoded["curve_counts"]
        item["sidecarPath"] = sidecar
        processable_count = int(item.get("processable_internal_solver_candidate_count") or 0)
        needs_empty_curve_refresh = decoded["is_empty_ok"] and processable_count > 0
        if args.include_ok or status.lower() in wanted or needs_empty_curve_refresh:
            item["recommendedCommand"] = command_for(args, item)
            item["applyCommand"] = apply_command_for(args, item)
            selected.append(item)
        if args.limit > 0 and len(selected) >= args.limit:
            break

    os.makedirs(args.output_dir, exist_ok=True)
    jsonl_path = os.path.join(args.output_dir, "animation_decoded_refresh_plan.jsonl")
    csv_path = os.path.join(args.output_dir, "animation_decoded_refresh_plan.csv")
    ps1_path = os.path.join(args.output_dir, "animation_decoded_refresh_commands.ps1")
    summary_path = os.path.join(args.output_dir, "animation_decoded_refresh_summary.json")

    with open(jsonl_path, "w", encoding="utf-8") as f:
        for item in selected:
            f.write(json.dumps(item, ensure_ascii=False) + "\n")

    fields = [
        "name", "decodedStatus", "animation_type", "candidate_count", "model_count",
        "processable_internal_solver_candidate_count", "processable_internal_solver_model_count",
        "decodedRefreshReason", "decodedCurveCounts",
        "has_muscle_clip", "humanoid_binding_count", "transform_binding_count",
        "source", "container", "animation_asset"
    ]
    with open(csv_path, "w", encoding="utf-8-sig", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=fields)
        writer.writeheader()
        for item in selected:
            writer.writerow({k: item.get(k) for k in fields})

    with open(ps1_path, "w", encoding="utf-8-sig") as f:
        f.write("# Review before running. Each command uses full source input + unity_source_index.db and exports full decoded curves for one selected animation.\n")
        f.write("$ErrorActionPreference = \"Stop\"\n\n")
        for item in selected:
            f.write("# {0} | status={1} | reason={2} | processableHumanoid={3} | candidates={4}\n".format(
                item.get("name"),
                item.get("decodedStatus"),
                item.get("decodedRefreshReason"),
                item.get("processable_internal_solver_candidate_count"),
                item.get("candidate_count")))
            f.write(item["recommendedCommand"] + "\n\n")
            f.write(item["applyCommand"] + "\n\n")

    summary = {
        "libraryRoot": args.library_root,
        "indexPath": args.index_path,
        "outputDir": args.output_dir,
        "selectedCount": len(selected),
        "limit": args.limit,
        "candidateScanLimit": row_limit,
        "animationName": args.animation_name,
        "statuses": sorted(wanted),
        "scannedExplicitLinkedAnimations": len(rows),
        "topSelectedCandidateCount": selected[0]["candidate_count"] if selected else 0,
        "topSelectedProcessableInternalSolverCount": selected[0]["processable_internal_solver_candidate_count"] if selected else 0,
        "totalSelectedProcessableInternalSolverCount": sum(int(x["processable_internal_solver_candidate_count"] or 0) for x in selected),
        "sourceRoot": {
            "requested": requested_source_root,
            "sourceIndex": source_index_root,
            "usedInCommands": args.resolved_source_root,
            "note": "Commands use unity_source_index.db metadata.sourceRoot when available so --source_files stays relative to the same root the dependency index was built from.",
        },
        "files": {
            "jsonl": jsonl_path,
            "csv": csv_path,
            "commands": ps1_path,
        },
        "rule": "Sort by explicit Unity candidates that already have model avatar/internalSolver first, then plan animation_asset decoded curve refresh only. The script does not run exports and does not do model-animation guessing.",
    }
    with open(summary_path, "w", encoding="utf-8") as f:
        json.dump(summary, f, ensure_ascii=False, indent=2)

    print(json.dumps(summary, ensure_ascii=False, indent=2))

if __name__ == "__main__":
    main()
'@

Set-Content -LiteralPath $helperPy -Value $helperSource -Encoding UTF8

$statusText = ($Statuses -join ",")
$arguments = @(
    $helperPy,
    "--library-root", $LibraryRoot,
    "--index-path", $IndexPath,
    "--output-dir", $OutputDir,
    "--game", $Game,
    "--limit", $Limit,
    "--statuses", $statusText
)
if (-not [string]::IsNullOrWhiteSpace($SourceRoot)) {
    $arguments += @("--source-root", $SourceRoot)
}
if (-not [string]::IsNullOrWhiteSpace($AnimationName)) {
    $arguments += @("--animation-name", $AnimationName)
}
if ($IncludeOk) {
    $arguments += "--include-ok"
}

python @arguments

if ($CommandsOnly) {
    Get-Content -LiteralPath (Join-Path $OutputDir "animation_decoded_refresh_commands.ps1")
}
