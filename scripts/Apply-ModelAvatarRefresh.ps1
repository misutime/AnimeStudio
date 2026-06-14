param(
    [Parameter(Mandatory = $true)]
    [string]$LibraryRoot,

    [Parameter(Mandatory = $true)]
    [string]$RefreshRoot,

    [string]$ModelName,
    [string]$IndexPath,
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
if ([string]::IsNullOrWhiteSpace($IndexPath)) {
    $IndexPath = Join-Path $LibraryRoot "library_index.db"
}

$helperDir = Join-Path ([System.IO.Path]::GetTempPath()) "AnimeStudioModelAvatarRefreshApply"
$helperPy = Join-Path $helperDir ("apply_model_avatar_refresh_{0}.py" -f ([guid]::NewGuid().ToString("N")))
New-Item -ItemType Directory -Force -Path $helperDir | Out-Null

$helperSource = @'
import argparse
import json
import os
import shutil
import sqlite3

def load_json(path):
    with open(path, "r", encoding="utf-8-sig") as f:
        return json.load(f)

def write_jsonl(path, rows):
    with open(path, "w", encoding="utf-8") as f:
        for row in rows:
            f.write(json.dumps(row, ensure_ascii=False) + "\n")

def iter_catalog_rows(path):
    if not os.path.exists(path):
        return
    with open(path, "r", encoding="utf-8-sig") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                yield json.loads(line)
            except Exception:
                continue

def find_refresh_models(refresh_root, model_name):
    result = []
    for dirpath, _, files in os.walk(refresh_root):
        if "asset_catalog.jsonl" not in files:
            continue
        catalog = os.path.join(dirpath, "asset_catalog.jsonl")
        for row in iter_catalog_rows(catalog):
            if row.get("kind") != "Model":
                continue
            if model_name and row.get("name") != model_name:
                continue
            avatar = row.get("avatar") or {}
            if avatar.get("internalSolver"):
                result.append((catalog, row))
    return result

def identity(row):
    return (row.get("name"), row.get("pathId"))

def avatar_summary(avatar):
    avatar = avatar or {}
    solver = avatar.get("internalSolver") or {}
    return {
        "name": avatar.get("name"),
        "pathId": avatar.get("pathId"),
        "hasHumanDescription": avatar.get("hasHumanDescription"),
        "humanBoneCount": avatar.get("humanBoneCount"),
        "skeletonBoneCount": avatar.get("skeletonBoneCount"),
        "hasInternalSolver": bool(avatar.get("internalSolver")),
        "validHumanBoneIndexCount": sum(1 for x in (solver.get("humanBoneIndex") or []) if isinstance(x, int) and x >= 0),
    }

def candidate_needs_solver_promotion(raw):
    return (
        bool(raw.get("missingInternalHumanoidSolver"))
        or raw.get("nextAction") == "inspect_missing_humanoid_avatar"
    )

def candidate_needs_incomplete_avatar_demote(raw):
    return (
        bool(raw.get("missingInternalHumanoidSolver"))
        or raw.get("nextAction") == "inspect_missing_humanoid_avatar"
        or (
            bool(raw.get("requiresUnityBake"))
            and raw.get("nextAction") == "generate_unity_baked_gltf"
        )
    )

def has_valid_human_bone_index(avatar):
    solver = (avatar or {}).get("internalSolver") or {}
    return any(isinstance(x, int) and x >= 0 for x in (solver.get("humanBoneIndex") or []))

def has_reference_pose(avatar):
    avatar = avatar or {}
    if avatar.get("skeletonBones"):
        return True
    solver = avatar.get("internalSolver") or {}
    skeleton = solver.get("skeleton") or {}
    nodes = skeleton.get("nodes") or []
    if nodes:
        human_pose = skeleton.get("humanSkeletonPose") or []
        default_pose = skeleton.get("avatarDefaultPose") or []
        return len(human_pose) >= len(nodes) and len(default_pose) >= len(nodes)
    avatar_skeleton = solver.get("avatarSkeleton") or {}
    avatar_nodes = avatar_skeleton.get("nodes") or []
    avatar_default_pose = avatar_skeleton.get("defaultPose") or []
    return bool(avatar_nodes) and len(avatar_default_pose) >= len(avatar_nodes)

def avatar_ready_for_bake_promotion(avatar):
    avatar = avatar or {}
    has_human_bones = bool(avatar.get("humanBones"))
    return (has_human_bones or has_valid_human_bone_index(avatar)) and has_reference_pose(avatar)

def avatar_missing_reason(avatar):
    avatar = avatar or {}
    if not avatar:
        return "missing_avatar_object"
    solver = avatar.get("internalSolver") or {}
    if not solver:
        return "missing_internal_solver"
    skeleton = solver.get("skeleton") or {}
    avatar_skeleton = solver.get("avatarSkeleton") or {}
    nodes = skeleton.get("nodes") or []
    avatar_nodes = avatar_skeleton.get("nodes") or []
    if not nodes and not avatar_nodes:
        return "missing_solver_skeleton_nodes"
    if nodes:
        human_pose = skeleton.get("humanSkeletonPose") or []
        default_pose = skeleton.get("avatarDefaultPose") or []
        if len(human_pose) < len(nodes) or len(default_pose) < len(nodes):
            return "missing_avatar_pose_metadata"
    elif avatar_nodes:
        avatar_default_pose = avatar_skeleton.get("defaultPose") or []
        if len(avatar_default_pose) < len(avatar_nodes):
            return "missing_avatar_pose_metadata"
    if not has_valid_human_bone_index(avatar) and not avatar.get("humanBones"):
        return "missing_human_bone_index"
    return None

def promote_candidate_after_avatar_refresh(raw, refresh_root):
    raw["requiresHumanoidBake"] = True
    raw["legacyUnityBakeSupported"] = True
    raw["requiresUnityBake"] = True
    raw["fullHumanoidBakeRequired"] = True
    raw["fullHumanoidBakeBlocked"] = False
    raw.pop("fullHumanoidBakeBlockedReason", None)
    raw["partialDirectGltf"] = False
    raw.pop("partialDirectGltfReason", None)
    raw["productionAnimationPath"] = "UnityBakeToGltf"
    raw["requiresInternalHumanoidSolve"] = True
    raw["missingInternalHumanoidSolver"] = False
    raw.pop("missingInternalHumanoidSolverReason", None)
    raw["nextAction"] = "generate_unity_baked_gltf"
    raw["candidateRefresh"] = {
        "appliedFrom": refresh_root,
        "rule": "Model avatar metadata is now available, so the explicit Humanoid candidate can enter the Unity bake to glTF production path.",
    }
    return raw

def demote_candidate_after_incomplete_avatar_refresh(raw, refresh_root, missing_reason):
    raw["requiresHumanoidBake"] = True
    raw["legacyUnityBakeSupported"] = False
    raw["requiresUnityBake"] = False
    raw["fullHumanoidBakeRequired"] = True
    raw["fullHumanoidBakeBlocked"] = True
    raw["fullHumanoidBakeBlockedReason"] = missing_reason or "incomplete_avatar_metadata"
    raw["partialDirectGltf"] = False
    raw.pop("partialDirectGltfReason", None)
    raw["productionAnimationPath"] = "UnityBakeToGltf"
    raw["requiresInternalHumanoidSolve"] = False
    raw["missingInternalHumanoidSolver"] = True
    raw["missingInternalHumanoidSolverReason"] = missing_reason or "incomplete_avatar_metadata"
    raw["nextAction"] = "inspect_missing_humanoid_avatar"
    raw["candidateRefresh"] = {
        "appliedFrom": refresh_root,
        "rule": "Model Avatar metadata was refreshed, but it still lacks a valid HumanBone mapping or reference pose. Keep the explicit candidate out of Unity bake production.",
    }
    return raw


def rebuild_candidate_summaries(con):
    tables = {
        row[0]
        for row in con.execute("SELECT name FROM sqlite_master WHERE type='table' AND name IN ('model_animation_candidate_model_summary','model_animation_candidate_animation_summary')")
    }
    if "model_animation_candidate_model_summary" not in tables or "model_animation_candidate_animation_summary" not in tables:
        return False
    con.execute("DELETE FROM model_animation_candidate_model_summary")
    con.execute("DELETE FROM model_animation_candidate_animation_summary")
    con.execute("""
INSERT INTO model_animation_candidate_model_summary(
    model_output,
    explicit_count,
    direct_preview_count,
    internal_humanoid_count,
    legacy_unity_bake_count
)
SELECT model_output,
       COUNT(*) AS explicit_count,
       SUM(CASE
           WHEN COALESCE(json_extract(raw_json, '$.nextAction'), '') = 'generate_preview_gltf'
            AND COALESCE(json_extract(raw_json, '$.requiresInternalHumanoidSolve'), 0) = 0
            AND COALESCE(json_extract(raw_json, '$.missingInternalHumanoidSolver'), 0) = 0
           THEN 1 ELSE 0 END) AS direct_preview_count,
       SUM(CASE
           WHEN COALESCE(json_extract(raw_json, '$.nextAction'), '') = 'generate_preview_gltf'
            AND COALESCE(json_extract(raw_json, '$.requiresInternalHumanoidSolve'), 0) = 1
           THEN 1
           WHEN COALESCE(json_extract(raw_json, '$.nextAction'), '') = 'generate_unity_baked_gltf'
            AND COALESCE(json_extract(raw_json, '$.requiresUnityBake'), 0) = 1
           THEN 1
           WHEN COALESCE(json_extract(raw_json, '$.missingInternalHumanoidSolver'), 0) = 1
             OR COALESCE(json_extract(raw_json, '$.nextAction'), '') = 'inspect_missing_humanoid_avatar'
           THEN 1 ELSE 0 END) AS internal_humanoid_count,
       SUM(CASE
           WHEN COALESCE(json_extract(raw_json, '$.legacyUnityBakeSupported'), 0) = 1
           THEN 1 ELSE 0 END) AS legacy_unity_bake_count
FROM model_animation_candidates
WHERE relation_source='explicit'
GROUP BY model_output
""")
    con.execute("""
INSERT INTO model_animation_candidate_animation_summary(
    animation_output,
    explicit_model_count,
    direct_preview_model_count,
    internal_humanoid_model_count,
    legacy_unity_bake_model_count
)
SELECT animation_output,
       COUNT(*) AS explicit_model_count,
       SUM(CASE
           WHEN COALESCE(json_extract(raw_json, '$.nextAction'), '') = 'generate_preview_gltf'
            AND COALESCE(json_extract(raw_json, '$.requiresInternalHumanoidSolve'), 0) = 0
            AND COALESCE(json_extract(raw_json, '$.missingInternalHumanoidSolver'), 0) = 0
           THEN 1 ELSE 0 END) AS direct_preview_model_count,
       SUM(CASE
           WHEN COALESCE(json_extract(raw_json, '$.nextAction'), '') = 'generate_preview_gltf'
            AND COALESCE(json_extract(raw_json, '$.requiresInternalHumanoidSolve'), 0) = 1
           THEN 1
           WHEN COALESCE(json_extract(raw_json, '$.nextAction'), '') = 'generate_unity_baked_gltf'
            AND COALESCE(json_extract(raw_json, '$.requiresUnityBake'), 0) = 1
           THEN 1
           WHEN COALESCE(json_extract(raw_json, '$.missingInternalHumanoidSolver'), 0) = 1
             OR COALESCE(json_extract(raw_json, '$.nextAction'), '') = 'inspect_missing_humanoid_avatar'
           THEN 1 ELSE 0 END) AS internal_humanoid_model_count,
       SUM(CASE
           WHEN COALESCE(json_extract(raw_json, '$.legacyUnityBakeSupported'), 0) = 1
           THEN 1 ELSE 0 END) AS legacy_unity_bake_model_count
FROM model_animation_candidates
WHERE relation_source='explicit'
GROUP BY animation_output
""")
    return True

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--library-root", required=True)
    parser.add_argument("--refresh-root", required=True)
    parser.add_argument("--model-name")
    parser.add_argument("--index-path")
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--no-backup", action="store_true")
    args = parser.parse_args()

    library_root = os.path.abspath(args.library_root)
    refresh_root = os.path.abspath(args.refresh_root)
    catalog_path = os.path.join(library_root, "asset_catalog.jsonl")
    refresh_models = find_refresh_models(refresh_root, args.model_name)
    results = []
    if not refresh_models:
        print(json.dumps({
            "libraryRoot": library_root,
            "refreshRoot": refresh_root,
            "modelName": args.model_name,
            "dryRun": args.dry_run,
            "status": "no_refresh_model_with_internal_solver",
            "results": [],
        }, ensure_ascii=False, indent=2))
        return

    refresh_by_identity = {identity(row): (catalog, row) for catalog, row in refresh_models}
    catalog_rows = list(iter_catalog_rows(catalog_path))
    changed_catalog = False
    for index, row in enumerate(catalog_rows):
        key = identity(row)
        if key not in refresh_by_identity:
            continue
        refresh_catalog, refresh_row = refresh_by_identity[key]
        old_avatar = row.get("avatar") or {}
        new_avatar = refresh_row.get("avatar") or {}
        bake_promotion_ready = avatar_ready_for_bake_promotion(new_avatar)
        result = {
            "name": row.get("name"),
            "pathId": row.get("pathId"),
            "catalogPath": catalog_path,
            "refreshCatalog": refresh_catalog,
            "oldAvatar": avatar_summary(old_avatar),
            "newAvatar": avatar_summary(new_avatar),
            "bakePromotionReady": bake_promotion_ready,
            "status": "dry_run" if args.dry_run else "updated",
        }
        if not args.dry_run:
            catalog_rows[index]["avatar"] = new_avatar
            catalog_rows[index]["avatarRefresh"] = {
                "appliedFrom": refresh_catalog,
                "rule": "Only model avatar metadata is replaced. Identity must match model name and Unity PathID.",
                "bakePromotionReady": bake_promotion_ready,
            }
            changed_catalog = True
        results.append(result)

    if changed_catalog and not args.dry_run:
        if not args.no_backup:
            backup = catalog_path + ".avatar_refresh.bak"
            if not os.path.exists(backup):
                shutil.copy2(catalog_path, backup)
            for result in results:
                result["catalogBackupPath"] = backup
        write_jsonl(catalog_path, catalog_rows)

    db_updated = 0
    candidate_updated = 0
    candidate_demoted = 0
    candidate_reason_cleaned = 0
    candidate_would_update = 0
    candidate_promotion_skipped = 0
    summaries_rebuilt = False
    index_path = args.index_path
    if index_path and os.path.exists(index_path):
        con = sqlite3.connect(index_path)
        con.row_factory = sqlite3.Row
        try:
            for _, refresh_row in refresh_by_identity.values():
                if args.model_name and refresh_row.get("name") != args.model_name:
                    continue
                current = con.execute(
                    "select id, output, raw_json from assets where kind='Model' and name=? and path_id=?",
                    (refresh_row.get("name"), refresh_row.get("pathId")),
                ).fetchone()
                if not current:
                    continue
                current_json = json.loads(current["raw_json"])
                new_avatar = refresh_row.get("avatar") or {}
                can_promote = avatar_ready_for_bake_promotion(new_avatar)
                missing_reason = avatar_missing_reason(new_avatar)
                current_json["avatar"] = new_avatar
                current_json["avatarRefresh"] = {
                    "appliedFrom": refresh_root,
                    "rule": "Only model avatar metadata is replaced. Identity must match model name and Unity PathID.",
                    "bakePromotionReady": can_promote,
                }
                if not args.dry_run:
                    if not args.no_backup:
                        backup = index_path + ".avatar_refresh.bak"
                        if not os.path.exists(backup):
                            shutil.copy2(index_path, backup)
                    con.execute(
                        "update assets set raw_json=? where id=?",
                        (json.dumps(current_json, ensure_ascii=False), current["id"]),
                    )
                    db_updated += 1
                    for candidate in con.execute(
                        """
select id, raw_json
from model_animation_candidates
where relation_source='explicit'
  and model_output=?
  and (
       COALESCE(json_extract(raw_json, '$.missingInternalHumanoidSolver'), 0)=1
       OR COALESCE(json_extract(raw_json, '$.nextAction'), '')='inspect_missing_humanoid_avatar'
       OR (
            COALESCE(json_extract(raw_json, '$.requiresUnityBake'), 0)=1
            AND COALESCE(json_extract(raw_json, '$.nextAction'), '')='generate_unity_baked_gltf'
          )
  )
""",
                        (current["output"],),
                    ).fetchall():
                        raw = json.loads(candidate["raw_json"])
                        if can_promote and not candidate_needs_solver_promotion(raw):
                            continue
                        if not can_promote and not candidate_needs_incomplete_avatar_demote(raw):
                            continue
                        if not can_promote:
                            raw = demote_candidate_after_incomplete_avatar_refresh(raw, refresh_root, missing_reason)
                            candidate_demoted += 1
                        else:
                            raw = promote_candidate_after_avatar_refresh(raw, refresh_root)
                            candidate_updated += 1
                        con.execute(
                            "update model_animation_candidates set raw_json=? where id=?",
                            (json.dumps(raw, ensure_ascii=False), candidate["id"]),
                        )
                    for candidate in con.execute(
                        """
select id, raw_json
from model_animation_candidates
where relation_source='explicit'
  and model_output=?
  and COALESCE(json_extract(raw_json, '$.missingInternalHumanoidSolver'), 0)=0
  and COALESCE(json_extract(raw_json, '$.nextAction'), '') in ('generate_preview_gltf', 'generate_unity_baked_gltf')
  and json_type(raw_json, '$.missingInternalHumanoidSolverReason') IS NOT NULL
""",
                        (current["output"],),
                    ).fetchall():
                        raw = json.loads(candidate["raw_json"])
                        if "missingInternalHumanoidSolverReason" not in raw:
                            continue
                        raw.pop("missingInternalHumanoidSolverReason", None)
                        con.execute(
                            "update model_animation_candidates set raw_json=? where id=?",
                            (json.dumps(raw, ensure_ascii=False), candidate["id"]),
                        )
                        candidate_reason_cleaned += 1
                elif args.dry_run:
                    pending_count = con.execute(
                        """
select count(*)
from model_animation_candidates
where relation_source='explicit'
  and model_output=?
  and (
       COALESCE(json_extract(raw_json, '$.missingInternalHumanoidSolver'), 0)=1
       OR COALESCE(json_extract(raw_json, '$.nextAction'), '')='inspect_missing_humanoid_avatar'
       OR (
            COALESCE(json_extract(raw_json, '$.requiresUnityBake'), 0)=1
            AND COALESCE(json_extract(raw_json, '$.nextAction'), '')='generate_unity_baked_gltf'
          )
  )
""",
                        (current["output"],),
                    ).fetchone()[0]
                    if can_promote:
                        candidate_would_update += pending_count
                    else:
                        candidate_promotion_skipped += pending_count
            if (candidate_updated or candidate_demoted or candidate_reason_cleaned) and not args.dry_run:
                summaries_rebuilt = rebuild_candidate_summaries(con)
            if not args.dry_run:
                con.commit()
        finally:
            con.close()

    summary = {
        "libraryRoot": library_root,
        "refreshRoot": refresh_root,
        "indexPath": index_path,
        "modelName": args.model_name,
        "dryRun": args.dry_run,
        "catalogMatches": len(results),
        "databaseUpdated": db_updated,
        "candidateUpdated": candidate_updated,
        "candidateDemoted": candidate_demoted,
        "candidateReasonCleaned": candidate_reason_cleaned,
        "candidateWouldUpdate": candidate_would_update,
        "candidatePromotionSkipped": candidate_promotion_skipped,
        "candidateSummariesRebuilt": summaries_rebuilt,
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
if (-not [string]::IsNullOrWhiteSpace($ModelName)) {
    $arguments += @("--model-name", $ModelName)
}
if (-not [string]::IsNullOrWhiteSpace($IndexPath)) {
    $arguments += @("--index-path", $IndexPath)
}
if ($DryRun) {
    $arguments += "--dry-run"
}
if ($NoBackup) {
    $arguments += "--no-backup"
}

try {
    python @arguments
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
finally {
    Remove-Item -LiteralPath $helperPy -Force -ErrorAction SilentlyContinue
}
