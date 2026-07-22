param(
    [Parameter(Mandatory = $true)]
    [string]$LibraryRoot,

    [string]$IndexPath,
    [string]$OutputDir,
    [string]$Game = "GI",
    [string]$SourceRoot,
    [string]$SourceIndex,
    [string]$ModelName,
    [int]$Limit = 100,
    [switch]$IncludeGlobalMissingScan,
    [switch]$NoBackupInCommands,
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
    $OutputDir = Join-Path $LibraryRoot "ModelAvatarRefreshPlan"
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$helperDir = Join-Path ([System.IO.Path]::GetTempPath()) "AnimeStudioModelAvatarRefreshPlan"
$helperPy = Join-Path $helperDir "write_plan.py"
New-Item -ItemType Directory -Force -Path $helperDir | Out-Null

$helperSource = @'
import argparse
import csv
import json
import os
import re
import sqlite3

def ps_single_quote(value):
    return "'" + str(value).replace("'", "''") + "'"

def regex_exact(value):
    return "^" + re.escape(value or "") + "$"

def safe_name(value):
    value = value or "model"
    invalid = set('<>:"/\\|?*')
    cleaned = ''.join("_" if c in invalid or ord(c) < 32 else c for c in value)
    return cleaned.strip(" .")[:120] or "model"

def chunked(items, size):
    size = max(int(size or 1), 1)
    for index in range(0, len(items), size):
        yield items[index:index + size]

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

def read_library_summary_source_index(library_root):
    summary_path = os.path.join(library_root, "sqlite_index_summary.json")
    if not os.path.exists(summary_path):
        return None
    try:
        with open(summary_path, "r", encoding="utf-8-sig") as f:
            summary = json.load(f)
        health = ((summary.get("animationRelationCoverage") or {}).get("sourceIndexAnimationRelationHealth") or {})
        source_index = health.get("sourceIndex") or summary.get("sourceIndex")
        return source_index if source_index and os.path.exists(source_index) else None
    except Exception:
        return None

def read_source_index_root(source_index):
    if not os.path.exists(source_index):
        return None
    try:
        con = sqlite3.connect(source_index)
        row = con.execute("SELECT value FROM metadata WHERE key='sourceRoot' LIMIT 1").fetchone()
        con.close()
        return row[0] if row and row[0] else None
    except Exception:
        return None

def table_exists(con, name):
    row = con.execute("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=?", (name,)).fetchone()
    return bool(row and row[0])

def export_command(args, row):
    source_root = args.resolved_source_root
    refresh_root = os.path.join(args.output_dir, "RefreshExports", safe_name(row["name"]))
    source_file = relative_source_file(source_root, row.get("source"))
    parts = [
        "dotnet run --project AnimeStudio.CLI\\AnimeStudio.CLI.csproj -c Debug --framework net9.0-windows --",
        ps_single_quote(source_root),
        ps_single_quote(refresh_root),
        "--game " + args.game,
        "--mode Library",
        "--group_assets ByLibrary",
        "--model_format Gltf",
        "--animation_package Skip",
        "--skip_sqlite_index",
        "--source_index " + ps_single_quote(args.resolved_source_index),
        "--names " + ps_single_quote(regex_exact(row["name"])),
    ]
    if source_file:
        parts.append("--source_files " + ps_single_quote(source_file))
    if row.get("path_id") is not None:
        parts.append("--path_ids " + str(row["path_id"]))
    return " ".join(parts)

def batch_export_command(args, rows, batch_index):
    first = rows[0]
    source_root = args.resolved_source_root
    source_file = relative_source_file(source_root, first.get("source"))
    batch_name = safe_name((source_file or "source").replace("/", "_").replace("\\", "_"))
    refresh_root = os.path.join(args.output_dir, "RefreshExports", "_Batch_{:03d}_{}".format(batch_index, batch_name))
    parts = [
        "dotnet run --project AnimeStudio.CLI\\AnimeStudio.CLI.csproj -c Debug --framework net9.0-windows --",
        ps_single_quote(source_root),
        ps_single_quote(refresh_root),
        "--game " + args.game,
        "--mode Library",
        "--group_assets ByLibrary",
        "--model_format Gltf",
        "--animation_package Skip",
        "--skip_sqlite_index",
        "--source_index " + ps_single_quote(args.resolved_source_index),
    ]
    if source_file:
        parts.append("--source_files " + ps_single_quote(source_file))
    names = [row["name"] for row in rows if row.get("name")]
    if names:
        parts.extend("--names " + ps_single_quote(regex_exact(name)) for name in names)
    path_ids = [str(row["path_id"]) for row in rows if row.get("path_id") is not None]
    if path_ids:
        parts.append("--path_ids " + " ".join(path_ids))
    return refresh_root, " ".join(parts)

def apply_command(args, row, refresh_root=None):
    if refresh_root is None:
        refresh_root = os.path.join(args.output_dir, "RefreshExports", safe_name(row["name"]))
    parts = [
        "powershell -ExecutionPolicy Bypass -File scripts\\Apply-ModelAvatarRefresh.ps1",
        "-LibraryRoot " + ps_single_quote(args.library_root),
        "-RefreshRoot " + ps_single_quote(refresh_root),
        "-ModelName " + ps_single_quote(row["name"]),
        "-IndexPath " + ps_single_quote(args.index_path),
    ]
    if args.no_backup_in_commands:
        parts.append("-NoBackup")
    return " ".join(parts)

def batch_apply_command(args, refresh_root):
    parts = [
        "powershell -ExecutionPolicy Bypass -File scripts\\Apply-ModelAvatarRefresh.ps1",
        "-LibraryRoot " + ps_single_quote(args.library_root),
        "-RefreshRoot " + ps_single_quote(refresh_root),
        "-IndexPath " + ps_single_quote(args.index_path),
    ]
    if args.no_backup_in_commands:
        parts.append("-NoBackup")
    return " ".join(parts)

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--library-root", required=True)
    parser.add_argument("--index-path", required=True)
    parser.add_argument("--output-dir", required=True)
    parser.add_argument("--game", required=True)
    parser.add_argument("--source-root")
    parser.add_argument("--source-index")
    parser.add_argument("--model-name")
    parser.add_argument("--limit", type=int, default=100)
    parser.add_argument("--include-global-missing-scan", action="store_true")
    parser.add_argument("--no-backup-in-commands", action="store_true")
    args = parser.parse_args()
    summary_source_index = read_library_summary_source_index(args.library_root)
    requested_source_index = args.source_index or summary_source_index or os.path.join(args.library_root, "unity_source_index.db")
    source_index_root = read_source_index_root(requested_source_index)
    requested_source_root = args.source_root or args.library_root
    # Keep targeted refresh on the same source index used by the Library candidates.
    args.resolved_source_root = source_index_root or requested_source_root
    args.resolved_source_index = requested_source_index

    con = sqlite3.connect(args.index_path)
    con.row_factory = sqlite3.Row
    has_model_summary = table_exists(con, "model_animation_candidate_model_summary")
    params = []
    name_filter = ""
    if args.model_name:
        name_filter = "AND m.name = ?"
        params.append(args.model_name)

    missing_cte = """
WITH model_missing AS (
    SELECT m.*,
           CASE
             WHEN json_type(m.raw_json, '$.avatar') IS NULL THEN 'missing_avatar_object'
             WHEN json_type(m.raw_json, '$.avatar.internalSolver') IS NULL THEN 'missing_internal_solver'
             WHEN COALESCE(json_array_length(json_extract(m.raw_json, '$.avatar.internalSolver.skeleton.nodes')), 0) = 0
                  AND COALESCE(json_array_length(json_extract(m.raw_json, '$.avatar.internalSolver.avatarSkeleton.nodes')), 0) = 0 THEN 'missing_solver_skeleton_nodes'
             WHEN COALESCE(json_array_length(json_extract(m.raw_json, '$.avatar.internalSolver.skeleton.humanSkeletonPose')), 0)
                    < COALESCE(json_array_length(json_extract(m.raw_json, '$.avatar.internalSolver.skeleton.nodes')), 0)
                  AND COALESCE(json_array_length(json_extract(m.raw_json, '$.avatar.internalSolver.avatarSkeleton.defaultPose')), 0)
                    < COALESCE(json_array_length(json_extract(m.raw_json, '$.avatar.internalSolver.avatarSkeleton.nodes')), 0) THEN 'missing_avatar_pose_metadata'
             WHEN NOT EXISTS (
                    SELECT 1
                    FROM json_each(m.raw_json, '$.avatar.internalSolver.humanBoneIndex')
                    WHERE CAST(value AS INTEGER) >= 0
                  ) THEN 'missing_human_bone_index'
             WHEN COALESCE(json_extract(m.raw_json, '$.avatar.hasHumanDescription'), 0) = 1
                  AND COALESCE(json_array_length(json_extract(m.raw_json, '$.avatar.humanBoneDetails')), 0) = 0 THEN 'missing_human_bone_limits'
             ELSE NULL
           END AS missing_reason
    FROM assets m
    WHERE m.kind='Model'
)
"""
    candidate_filter = """
FROM model_animation_candidates c
JOIN model_missing m ON m.output=c.model_output
WHERE c.relation_source='explicit'
  AND m.missing_reason IS NOT NULL
  AND (
       COALESCE(json_extract(c.raw_json, '$.missingInternalHumanoidSolver'), 0)=1
       OR COALESCE(json_extract(c.raw_json, '$.nextAction'), '')='inspect_missing_humanoid_avatar'
       OR (
            COALESCE(json_extract(c.raw_json, '$.nextAction'), '')='generate_preview_gltf'
            AND COALESCE(json_extract(c.raw_json, '$.requiresInternalHumanoidSolve'), 0)=1
          )
       OR (
            COALESCE(json_extract(c.raw_json, '$.nextAction'), '')='generate_unity_baked_gltf'
            AND COALESCE(json_extract(c.raw_json, '$.requiresUnityBake'), 0)=1
          )
       )
"""
    summary_filter = """
FROM model_animation_candidate_model_summary s
JOIN model_missing m ON m.output=s.model_output
WHERE m.missing_reason IS NOT NULL
  AND s.internal_humanoid_count > 0
"""
    refreshable_summary_filter = summary_filter + """
  AND (
       json_type(m.raw_json, '$.avatar.pathId') IS NOT NULL
       OR json_type(m.raw_json, '$.avatar.name') IS NOT NULL
      )
"""

    global_rows = []
    global_scan_mode = "summary_table" if has_model_summary else "skipped_no_summary_table"
    if has_model_summary:
        global_rows = con.execute(missing_cte + f"""
SELECT m.missing_reason,
       COUNT(DISTINCT m.output) AS model_count,
       COALESCE(SUM(s.internal_humanoid_count), 0) AS candidate_count,
       0 AS animation_count
{summary_filter}
GROUP BY m.missing_reason
ORDER BY candidate_count DESC, m.missing_reason COLLATE NOCASE
""").fetchall()
    elif args.include_global_missing_scan:
        global_scan_mode = "raw_json_scan"
        global_rows = con.execute(missing_cte + f"""
SELECT m.missing_reason,
       COUNT(DISTINCT m.output) AS model_count,
       COUNT(*) AS candidate_count,
       COUNT(DISTINCT c.animation_output) AS animation_count
{candidate_filter}
GROUP BY m.missing_reason
ORDER BY candidate_count DESC, m.missing_reason COLLATE NOCASE
""").fetchall()

    limit_sql = "" if args.limit <= 0 else f"LIMIT {args.limit}"
    # Keep this completeness check aligned with SQLiteLibraryIndexBuilder / FastPreview.
    # Old models with internalSolver but missing pose metadata must also be refreshed.
    if has_model_summary:
        rows = con.execute(missing_cte + f"""
SELECT m.name,
       m.output,
       m.source,
       m.path_id,
       m.missing_reason,
       json_extract(m.raw_json, '$.avatar.name') AS avatar_name,
       json_extract(m.raw_json, '$.avatar.pathId') AS avatar_path_id,
       s.internal_humanoid_count AS missing_internal_solver_candidate_count,
       s.internal_humanoid_count AS animation_count
{refreshable_summary_filter}
  {name_filter}
GROUP BY m.output
ORDER BY missing_internal_solver_candidate_count DESC, m.name COLLATE NOCASE
{limit_sql}
""", params).fetchall()
        selection_scan_mode = "summary_table"
    else:
        rows = con.execute(missing_cte + f"""
SELECT m.name,
       m.output,
       m.source,
       m.path_id,
       m.missing_reason,
       json_extract(m.raw_json, '$.avatar.name') AS avatar_name,
       json_extract(m.raw_json, '$.avatar.pathId') AS avatar_path_id,
COUNT(*) AS missing_internal_solver_candidate_count,
       COUNT(DISTINCT c.animation_output) AS animation_count
{candidate_filter}
  AND (
       json_type(m.raw_json, '$.avatar.pathId') IS NOT NULL
       OR json_type(m.raw_json, '$.avatar.name') IS NOT NULL
      )
  {name_filter}
GROUP BY m.output
ORDER BY missing_internal_solver_candidate_count DESC, m.name COLLATE NOCASE
{limit_sql}
""", params).fetchall()
        selection_scan_mode = "raw_json_scan"
    con.close()

    selected = []
    for row in rows:
        item = dict(row)
        item["refreshOutput"] = os.path.join(args.output_dir, "RefreshExports", safe_name(item["name"]))
        item["exportCommand"] = export_command(args, item)
        item["applyCommand"] = apply_command(args, item)
        selected.append(item)

    os.makedirs(args.output_dir, exist_ok=True)
    jsonl_path = os.path.join(args.output_dir, "model_avatar_refresh_plan.jsonl")
    csv_path = os.path.join(args.output_dir, "model_avatar_refresh_plan.csv")
    commands_path = os.path.join(args.output_dir, "model_avatar_refresh_commands.ps1")
    grouped_commands_path = os.path.join(args.output_dir, "model_avatar_refresh_grouped_commands.ps1")
    summary_path = os.path.join(args.output_dir, "model_avatar_refresh_summary.json")

    with open(jsonl_path, "w", encoding="utf-8") as f:
        for item in selected:
            f.write(json.dumps(item, ensure_ascii=False) + "\n")

    fields = [
        "name", "missing_internal_solver_candidate_count", "animation_count",
        "missing_reason", "avatar_name", "avatar_path_id", "source", "path_id", "output"
    ]
    with open(csv_path, "w", encoding="utf-8-sig", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=fields)
        writer.writeheader()
        for item in selected:
            writer.writerow({k: item.get(k) for k in fields})

    with open(commands_path, "w", encoding="utf-8-sig") as f:
        f.write("# Review before running. These commands refresh model avatar/internalSolver metadata only.\n")
        f.write("# They use full source input + the selected unity_source_index.db and do not create model-animation relations.\n")
        f.write("$ErrorActionPreference = \"Stop\"\n\n")
        for item in selected:
            f.write("# {0} | {1} | unlocks {2} explicit Humanoid candidate(s)\n".format(
                item.get("name"), item.get("missing_reason"), item.get("missing_internal_solver_candidate_count")))
            f.write(item["exportCommand"] + "\n")
            f.write(item["applyCommand"] + "\n\n")

    groups = {}
    for item in selected:
        source_file = relative_source_file(args.resolved_source_root, item.get("source")) or item.get("source") or "<missing_source>"
        groups.setdefault(source_file, []).append(item)
    grouped_command_count = 0
    grouped_export_count = 0
    with open(grouped_commands_path, "w", encoding="utf-8-sig") as f:
        f.write("# Review before running. Grouped commands refresh model avatar/internalSolver metadata only.\n")
        f.write("# Models with the same source file are exported together to avoid repeatedly loading the same dependency closure.\n")
        f.write("# Apply commands still update each model independently and do not create model-animation relations.\n")
        f.write("$ErrorActionPreference = \"Stop\"\n\n")
        batch_index = 1
        for source_file in sorted(groups.keys()):
            group_rows = sorted(groups[source_file], key=lambda x: (str(x.get("name") or "").lower(), str(x.get("path_id") or "")))
            for batch_rows in chunked(group_rows, 8):
                refresh_root, command = batch_export_command(args, batch_rows, batch_index)
                grouped_export_count += 1
                grouped_command_count += 2
                f.write("# Batch {0:03d} | {1} | {2} model(s) | unlocks {3} explicit Humanoid candidate(s)\n".format(
                    batch_index,
                    source_file,
                    len(batch_rows),
                    sum(int(x.get("missing_internal_solver_candidate_count") or 0) for x in batch_rows)))
                f.write(command + "\n")
                f.write(batch_apply_command(args, refresh_root) + "\n")
                f.write("\n")
                batch_index += 1

    global_missing_by_reason = {
        row["missing_reason"]: {
            "modelCount": row["model_count"],
            "candidateCount": row["candidate_count"],
            "animationCount": row["animation_count"],
        }
        for row in global_rows
    }
    global_missing_model_count = sum(int(row["model_count"] or 0) for row in global_rows)
    global_missing_candidate_count = sum(int(row["candidate_count"] or 0) for row in global_rows)
    planned_unlock_count = sum(int(x["missing_internal_solver_candidate_count"] or 0) for x in selected)

    summary = {
        "libraryRoot": args.library_root,
        "indexPath": args.index_path,
        "outputDir": args.output_dir,
        "selectedCount": len(selected),
        "limit": args.limit,
        "modelName": args.model_name,
        "topUnlockCount": selected[0]["missing_internal_solver_candidate_count"] if selected else 0,
        "totalPlannedUnlockCount": planned_unlock_count,
        "plannedCandidateCoverageOfGlobalMissing": round((planned_unlock_count / global_missing_candidate_count), 6) if global_missing_candidate_count else None,
        "globalMissing": {
            "scanStatus": "computed" if global_rows else "skipped",
            "scanMode": global_scan_mode,
            "modelCount": global_missing_model_count,
            "candidateCount": global_missing_candidate_count,
            "byReason": global_missing_by_reason,
            "rule": "Counts explicit Unity model-animation candidates blocked by missing model Avatar/internalSolver metadata.",
            "note": "Uses model_animation_candidate_model_summary when available. For very old SQLite indexes without summary tables, pass -IncludeGlobalMissingScan to allow the slower raw_json scan.",
        },
        "selectionScanMode": selection_scan_mode,
        "missingReasonCounts": {
            reason: sum(1 for x in selected if x.get("missing_reason") == reason)
            for reason in sorted({x.get("missing_reason") for x in selected})
        },
        "sourceRoot": {
            "requested": requested_source_root,
            "sourceIndex": source_index_root,
            "usedInCommands": args.resolved_source_root,
            "note": "Commands use source index metadata.sourceRoot when available so --source_files stays relative to the same root the dependency index was built from.",
        },
        "sourceIndex": {
            "requested": args.source_index,
            "fromSqliteSummary": summary_source_index,
            "usedInCommands": args.resolved_source_index,
            "note": "When Library SQLite was rebuilt with an external fresh source index, Avatar refresh must reuse that same source index instead of the library-root fallback.",
        },
        "files": {
            "jsonl": jsonl_path,
            "csv": csv_path,
            "commands": commands_path,
            "groupedCommands": grouped_commands_path,
        },
        "groupedCommands": {
            "sourceGroupCount": len(groups),
            "exportCommandCount": grouped_export_count,
            "totalCommandCount": grouped_command_count,
            "maxModelsPerExport": 8,
            "rule": "Grouped commands only reduce repeated targeted exports for models sharing the same source file. They do not change relation matching or candidate selection.",
        },
        "rule": "Plan model avatar/internalSolver metadata refresh sorted by deterministic explicit Humanoid candidate count. This script does not run exports and does not infer model-animation relations.",
        "noBackupInCommands": args.no_backup_in_commands,
    }
    with open(summary_path, "w", encoding="utf-8") as f:
        json.dump(summary, f, ensure_ascii=False, indent=2)
    print(json.dumps(summary, ensure_ascii=False, indent=2))

if __name__ == "__main__":
    main()
'@

Set-Content -LiteralPath $helperPy -Value $helperSource -Encoding UTF8

$arguments = @(
    $helperPy,
    "--library-root", $LibraryRoot,
    "--index-path", $IndexPath,
    "--output-dir", $OutputDir,
    "--game", $Game,
    "--limit", $Limit
)
if (-not [string]::IsNullOrWhiteSpace($SourceRoot)) {
    $arguments += @("--source-root", $SourceRoot)
}
if (-not [string]::IsNullOrWhiteSpace($SourceIndex)) {
    $arguments += @("--source-index", $SourceIndex)
}
if (-not [string]::IsNullOrWhiteSpace($ModelName)) {
    $arguments += @("--model-name", $ModelName)
}
if ($NoBackupInCommands) {
    $arguments += "--no-backup-in-commands"
}
if ($IncludeGlobalMissingScan) {
    $arguments += "--include-global-missing-scan"
}

$summary = python @arguments
if ($CommandsOnly) {
    $summaryObj = $summary | ConvertFrom-Json
    Get-Content -LiteralPath $summaryObj.files.commands
} else {
    $summary
}
