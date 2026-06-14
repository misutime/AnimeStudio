param(
    [Parameter(Mandatory = $true)]
    [Alias("LibraryRoot")]
    [string]$LibraryPath,

    [string]$LibraryIndex,

    [string]$SourceIndex,

    [string]$UnityProject,

    [Alias("OutputDirectory")]
    [string]$OutputDir,

    [string[]]$AnimatedCategory = @("NPC", "Avatar", "Monster", "Animal", "Partner", "Vehicle"),

    [int]$TopCategoryLimit = 24,

    [switch]$SummaryOnly,

    [switch]$GateOnly,

    [switch]$FailOnWarning
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($LibraryIndex)) {
    $LibraryIndex = Join-Path $LibraryPath "library_index.db"
}
if ([string]::IsNullOrWhiteSpace($SourceIndex)) {
    $sqliteSummary = Join-Path $LibraryPath "sqlite_index_summary.json"
    if (Test-Path -LiteralPath $sqliteSummary) {
        try {
            $summaryText = Get-Content -LiteralPath $sqliteSummary -Raw
            $summarySourceIndex = ""
            $match = [regex]::Match(
                $summaryText,
                '"sourceIndex"\s*:\s*"(?<path>(?:\\.|[^"\\])*)"',
                [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
            if ($match.Success) {
                $summarySourceIndex = [regex]::Unescape($match.Groups["path"].Value)
            }
            if (-not [string]::IsNullOrWhiteSpace($summarySourceIndex)) {
                $SourceIndex = $summarySourceIndex
            }
        }
        catch {
            $SourceIndex = $null
        }
    }

    if ([string]::IsNullOrWhiteSpace($SourceIndex)) {
        $SourceIndex = Join-Path $LibraryPath "unity_source_index.db"
    }
}
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $OutputDir = Join-Path $LibraryPath "AnimationRelationDiagnostics_$stamp"
}

if (-not (Test-Path -LiteralPath $LibraryIndex)) {
    throw "找不到 library_index.db: $LibraryIndex"
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$python = @'
import argparse
import datetime as dt
import json
import os
import re
import sqlite3
import sys


def scalar(cur, sql, args=()):
    row = cur.execute(sql, args).fetchone()
    return 0 if row is None or row[0] is None else row[0]


def rows(cur, sql, args=()):
    return [dict(row) for row in cur.execute(sql, args)]


def table_exists(cur, name):
    return scalar(
        cur,
        """
        SELECT COUNT(*) FROM (
          SELECT name FROM sqlite_master WHERE type='table'
          UNION ALL
          SELECT name FROM sqlite_temp_master WHERE type='table'
        )
        WHERE name=?;
        """,
        (name,),
    ) > 0


def candidate_table_schema_health(cur):
    row = cur.execute(
        "SELECT sql FROM sqlite_master WHERE type='table' AND name='model_animation_candidates' LIMIT 1;"
    ).fetchone()
    if row is None or row[0] is None:
        return {
            "status": "missing",
            "hasRelationSourceNotNull": False,
            "hasExplicitRelationSourceCheck": False,
            "note": "model_animation_candidates table is missing.",
        }

    sql = row[0]
    normalized = re.sub(r"\s+", " ", sql).strip().lower()
    has_not_null = re.search(r"\brelation_source\b\s+\w+\s+not\s+null\b", normalized) is not None
    has_explicit_check = re.search(
        r"\bcheck\s*\([^)]*\brelation_source\b[^)]*=[^)]*['\"]explicit['\"][^)]*\)",
        normalized,
    ) is not None
    status = "ok" if has_not_null and has_explicit_check else "warning"
    missing = []
    if not has_not_null:
        missing.append("relation_source NOT NULL")
    if not has_explicit_check:
        missing.append("CHECK(relation_source='explicit')")

    return {
        "status": status,
        "hasRelationSourceNotNull": has_not_null,
        "hasExplicitRelationSourceCheck": has_explicit_check,
        "missingRules": missing,
        "note": (
            "model_animation_candidates.relation_source is constrained to explicit."
            if status == "ok"
            else "model_animation_candidates schema is missing deterministic relation constraints: "
                 + ", ".join(missing)
                 + ". Rebuild library_index.db with the current tool."
        ),
    }


def safe_query(default, func):
    try:
        return func()
    except Exception as exc:
        return {"error": str(exc), "default": default}


def normalize_path(value):
    return (value or "").replace("\\", "/")


def canonical_library_output(value, library_root):
    if not value:
        return ""
    text = str(value).strip().strip('"')
    try:
        if os.path.isabs(text) and library_root:
            root = os.path.abspath(library_root)
            full = os.path.abspath(text)
            try:
                rel = os.path.relpath(full, root)
                if not rel.startswith(".."):
                    text = rel
            except ValueError:
                pass
    except Exception:
        pass
    return normalize_path(text).strip("/")


def resolve_library_path(value, library_root):
    if not value:
        return ""
    text = str(value).strip().strip('"')
    if os.path.isabs(text):
        return text
    return os.path.join(library_root or "", text)


def read_json_file(path):
    if not path or not os.path.exists(path):
        return None
    try:
        with open(path, "r", encoding="utf-8") as handle:
            return json.load(handle)
    except Exception:
        return None


def unity_bake_apply_report_for_gltf(baked_gltf_path, library_root):
    baked_gltf = resolve_library_path(baked_gltf_path, library_root)
    if not baked_gltf or not os.path.exists(baked_gltf):
        return "", None
    report_path = os.path.join(os.path.dirname(baked_gltf), "unity_bake_apply_report.json")
    return report_path, read_json_file(report_path)


def report_request_has_explicit_avatar_asset(report):
    request_path = str((report or {}).get("request") or "").strip()
    if not request_path or not os.path.exists(request_path):
        return False
    request = read_json_file(request_path) or {}
    unity_paths = request.get("unityAssetPaths") or {}
    return bool(str(unity_paths.get("avatarAsset") or "").strip())


def avatar_trust_matches_explicit_request(report, avatar_trust):
    if not report_request_has_explicit_avatar_asset(report):
        return True
    source = str(
        (avatar_trust or {}).get("Source")
        or (avatar_trust or {}).get("source")
        or ""
    ).strip().lower()
    return source == "imported_unity_avatar_asset" and report_has_imported_avatar_asset_proof(report)


def report_has_imported_avatar_asset_proof(report):
    if bool((report or {}).get("unityBakeImportedAvatarAssetValid")):
        return True
    source = str((report or {}).get("unityBakeRigRestPoseSource") or "").strip().lower()
    applied = bool((report or {}).get("unityBakeRigRestPoseApplied"))
    return source == "imported_unity_avatar_asset" and applied


def is_trusted_baked_gltf_path(baked_gltf_path, library_root):
    report_path, report = unity_bake_apply_report_for_gltf(baked_gltf_path, library_root)
    if not report_path or not report:
        return False
    status = str(report.get("status") or "").lower()
    if status not in ("ok", "warning"):
        return False
    try:
        avatar_trust = report.get("avatarTrust") or {}
        trusted_avatar = bool(
            avatar_trust.get("TrustedProductionBake")
            or avatar_trust.get("trustedProductionBake")
        )
        return (
            int(report.get("frameVaryingTracks") or 0) > 0
            and trusted_avatar
            and avatar_trust_matches_explicit_request(report, avatar_trust)
        )
    except Exception:
        return False


def is_static_pose_baked_gltf_path(baked_gltf_path, library_root):
    report_path, report = unity_bake_apply_report_for_gltf(baked_gltf_path, library_root)
    if not report_path or not report:
        return False
    status = str(report.get("status") or "").lower()
    try:
        frame_varying_tracks = int(report.get("frameVaryingTracks") or 0)
    except Exception:
        frame_varying_tracks = 0
    return status == "static_pose" and frame_varying_tracks == 0


def is_needs_review_baked_gltf_path(baked_gltf_path, library_root):
    report_path, report = unity_bake_apply_report_for_gltf(baked_gltf_path, library_root)
    if not report_path or not report:
        return False
    status = str(report.get("status") or "").lower()
    return status == "needs_review"


def category_from_output(output):
    normalized = normalize_path(output)
    match = re.search(r"(?:^|/)Models/([^/]+)/", normalized, re.IGNORECASE)
    return match.group(1) if match else "<uncategorized>"


def ratio(part, total):
    if not total:
        return 0.0
    return round(float(part) / float(total), 6)


def source_health(source_index):
    if not source_index or not os.path.exists(source_index):
        return {
            "present": False,
            "path": source_index,
            "status": "missing",
            "note": "unity_source_index.db is missing; only library candidate tables can be checked.",
        }

    con = sqlite3.connect(source_index)
    con.row_factory = sqlite3.Row
    cur = con.cursor()
    try:
        relation_counts = {}
        for relation in [
            "animator.controller",
            "animatorController.clip",
            "animation.clip",
            "animatorOverrideController.baseController",
            "animatorOverrideController.overrideSet",
            "animatorOverrideController.originalClip",
            "animatorOverrideController.overrideClip",
            "animatorOverrideController.clipPair",
        ]:
            relation_counts[relation] = int(scalar(cur, "SELECT COUNT(*) FROM source_relations WHERE relation=?;", (relation,)))

        override_objects = int(scalar(cur, "SELECT COUNT(*) FROM source_objects WHERE type='AnimatorOverrideController';"))
        non_empty_override_sets = int(scalar(
            cur,
            "SELECT COUNT(*) FROM source_relations WHERE relation='animatorOverrideController.overrideSet' AND COALESCE(target_count, 0) > 0;"
        ))
        stale = override_objects > 0 and (
            relation_counts["animatorOverrideController.overrideSet"] == 0
            or (non_empty_override_sets > 0 and relation_counts["animatorOverrideController.clipPair"] == 0)
        )
        features = ""
        if table_exists(cur, "metadata"):
            row = cur.execute("SELECT value FROM metadata WHERE key='animationRelationFeatures' LIMIT 1;").fetchone()
            features = "" if row is None or row[0] is None else row[0]
        return {
            "present": True,
            "path": source_index,
            "status": "warning" if stale else "ok",
            "animationRelationFeatures": features,
            "objectCounts": {
                "AnimatorOverrideController": override_objects,
            },
            "relationCounts": relation_counts,
            "nonEmptyOverrideSetCount": non_empty_override_sets,
            "staleOverridePairIndex": stale,
            "note": (
                "AnimatorOverrideController exists, but overrideSet/clipPair relation markers are incomplete. Rebuild the source index before rebuilding Library SQLite."
                if stale else
                "Source index contains the explicit animation relation markers checked by this script."
            ),
        }
    finally:
        con.close()


def create_temp_imported_avatar_asset_keys(cur, unity_project):
    cur.execute("DROP TABLE IF EXISTS temp_imported_avatar_asset_keys;")
    cur.execute("CREATE TEMP TABLE temp_imported_avatar_asset_keys(key TEXT PRIMARY KEY);")
    if not unity_project:
        return {
            "available": False,
            "unityProject": unity_project,
            "fileCount": 0,
            "keyCount": 0,
            "note": "No Unity project was supplied, so imported Unity Avatar asset readiness cannot be measured.",
        }
    imported_root = os.path.join(unity_project, "Assets", "AnimeStudioBake", "ImportedAvatar")
    if not os.path.isdir(imported_root):
        return {
            "available": False,
            "unityProject": unity_project,
            "importedAvatarRoot": imported_root,
            "fileCount": 0,
            "keyCount": 0,
            "note": "Imported Avatar asset directory was not found.",
        }

    keys = set()
    file_count = 0
    for root, _dirs, files in os.walk(imported_root):
        for file_name in files:
            if not file_name.lower().endswith(".asset"):
                continue
            if file_name.lower().endswith(".meta"):
                continue
            name = os.path.splitext(file_name)[0].strip()
            if not name:
                continue
            file_count += 1
            keys.add(name)
            suffix = "_ModelAvatar"
            if name.lower().endswith(suffix.lower()):
                keys.add(name[:-len(suffix)])

    cur.executemany(
        "INSERT OR IGNORE INTO temp_imported_avatar_asset_keys(key) VALUES (?);",
        [(key,) for key in sorted(keys, key=str.lower)]
    )
    return {
        "available": True,
        "unityProject": unity_project,
        "importedAvatarRoot": imported_root,
        "fileCount": file_count,
        "keyCount": len(keys),
        "note": "Imported Avatar assets are matched by exact avatar.name or model name keys, using the same production oracle rule as CLI bake requests.",
    }


def imported_avatar_asset_match_sql(model_alias):
    raw = f"{model_alias}.raw_json"
    return f"""
        EXISTS (
          SELECT 1
          FROM temp_imported_avatar_asset_keys importedAvatar
          WHERE importedAvatar.key = COALESCE(json_extract({raw}, '$.avatar.name'), '')
             OR importedAvatar.key = COALESCE({model_alias}.name, '')
        )
    """


def bake_ready_avatar_sql(model_alias):
    raw = f"{model_alias}.raw_json"
    return f"""
        (
          COALESCE(json_array_length(json_extract({raw}, '$.avatar.humanBones')), 0) > 0
          AND COALESCE(json_array_length(json_extract({raw}, '$.avatar.skeletonBones')), 0) > 0
        )
    """


def effective_bake_ready_avatar_sql(model_alias):
    return f"""
        (
          {bake_ready_avatar_sql(model_alias)}
          OR {imported_avatar_asset_match_sql(model_alias)}
        )
    """


def unity_bake_production(cur, library_root, include_details=True, imported_avatar_info=None):
    bake_where = """
        c.relation_source='explicit'
        AND COALESCE(json_extract(c.raw_json, '$.fullHumanoidBakeRequired'), 0)=1
    """
    cur.execute("DROP TABLE IF EXISTS temp_unity_bake_candidates;")
    # Materialize the explicit Unity-bake candidate set once. The JSON filter is
    # expensive on large libraries and many later summaries reuse the same set.
    cur.execute(
        f"""
        CREATE TEMP TABLE temp_unity_bake_candidates AS
        SELECT c.model_output AS model_output,
               c.animation_output AS animation_output
        FROM model_animation_candidates c
        WHERE {bake_where};
        """
    )
    cur.execute("CREATE INDEX IF NOT EXISTS temp_unity_bake_candidates_model ON temp_unity_bake_candidates(model_output);")
    cur.execute("CREATE INDEX IF NOT EXISTS temp_unity_bake_candidates_animation ON temp_unity_bake_candidates(animation_output);")
    cur.execute("CREATE INDEX IF NOT EXISTS temp_unity_bake_candidates_pair ON temp_unity_bake_candidates(model_output, animation_output);")
    cur.execute("DROP TABLE IF EXISTS temp_bake_ready_animation_candidates;")
    # Materialize this expensive Avatar/JSON filter once so cache statistics
    # do not repeatedly scan model_animation_candidates on large libraries.
    cur.execute(
        """
        CREATE TEMP TABLE temp_bake_ready_animation_candidates AS
        SELECT t.model_output AS model_output,
               t.animation_output AS animation_output,
               CASE WHEN """ + bake_ready_avatar_sql("m") + """ THEN 1 ELSE 0 END AS bake_ready_avatar,
               CASE WHEN """ + imported_avatar_asset_match_sql("m") + """ THEN 1 ELSE 0 END AS imported_avatar_ready
        FROM temp_unity_bake_candidates t
        JOIN assets m ON m.kind='Model' AND m.output=t.model_output
        WHERE """ + effective_bake_ready_avatar_sql("m") + """;
        """
    )
    cur.execute("CREATE INDEX IF NOT EXISTS temp_bake_ready_animation_candidates_pair ON temp_bake_ready_animation_candidates(model_output, animation_output);")
    bake_ready_cache_where = """
        EXISTS (
          SELECT 1
          FROM temp_bake_ready_animation_candidates t
          WHERE t.model_output=bc.model_output
            AND t.animation_output=bc.animation_output
        )
    """
    explicit_unity_bake_candidates = int(scalar(
        cur,
        "SELECT COUNT(*) FROM temp_unity_bake_candidates;"
    ))
    unique_explicit_unity_bake_candidates = int(scalar(
        cur,
        """
        SELECT COUNT(*)
        FROM (
          SELECT DISTINCT model_output, animation_output
          FROM temp_unity_bake_candidates
        );
        """
    ))
    explicit_unity_bake_models = int(scalar(
        cur,
        "SELECT COUNT(DISTINCT model_output) FROM temp_unity_bake_candidates;"
    ))
    explicit_unity_bake_animations = int(scalar(
        cur,
        "SELECT COUNT(DISTINCT animation_output) FROM temp_unity_bake_candidates;"
    ))
    bake_ready_explicit_unity_bake_candidates = int(scalar(
        cur,
        "SELECT COUNT(*) FROM temp_bake_ready_animation_candidates;"
    ))
    unique_bake_ready_explicit_unity_bake_candidates = int(scalar(
        cur,
        """
        SELECT COUNT(*)
        FROM (
          SELECT DISTINCT model_output, animation_output
          FROM temp_bake_ready_animation_candidates
        );
        """
    ))
    human_description_bake_ready_candidates = int(scalar(
        cur,
        "SELECT COUNT(*) FROM temp_bake_ready_animation_candidates WHERE bake_ready_avatar=1;"
    ))
    unique_human_description_bake_ready_candidates = int(scalar(
        cur,
        """
        SELECT COUNT(*)
        FROM (
          SELECT DISTINCT model_output, animation_output
          FROM temp_bake_ready_animation_candidates
          WHERE bake_ready_avatar=1
        );
        """
    ))
    imported_avatar_bake_ready_candidates = int(scalar(
        cur,
        "SELECT COUNT(*) FROM temp_bake_ready_animation_candidates WHERE imported_avatar_ready=1;"
    ))
    unique_imported_avatar_bake_ready_candidates = int(scalar(
        cur,
        """
        SELECT COUNT(*)
        FROM (
          SELECT DISTINCT model_output, animation_output
          FROM temp_bake_ready_animation_candidates
          WHERE imported_avatar_ready=1
        );
        """
    ))
    path_flags = cur.execute(
        """
        SELECT
          SUM(CASE WHEN COALESCE(json_extract(raw_json, '$.fullHumanoidBakeRequired'), 0)=1 THEN 1 ELSE 0 END) AS fullHumanoidBakeRequiredCandidates,
          SUM(CASE WHEN COALESCE(json_extract(raw_json, '$.fullHumanoidBakeBlocked'), 0)=1 THEN 1 ELSE 0 END) AS fullHumanoidBakeBlockedCandidates,
          SUM(CASE WHEN COALESCE(json_extract(raw_json, '$.partialDirectGltf'), 0)=1 THEN 1 ELSE 0 END) AS partialDirectGltfCandidates,
          SUM(CASE WHEN json_extract(raw_json, '$.productionAnimationPath')='DirectGltfTransformOnly' THEN 1 ELSE 0 END) AS directGltfTransformOnlyCandidates
        FROM model_animation_candidates
        WHERE relation_source='explicit';
        """
    ).fetchone()
    full_humanoid_bake_required_candidates = int(path_flags["fullHumanoidBakeRequiredCandidates"] or 0)
    full_humanoid_bake_blocked_candidates = int(path_flags["fullHumanoidBakeBlockedCandidates"] or 0)
    partial_direct_gltf_candidates = int(path_flags["partialDirectGltfCandidates"] or 0)
    direct_gltf_transform_only_candidates = int(path_flags["directGltfTransformOnlyCandidates"] or 0)

    result = {
        "rule": "Unity bake production only counts relation_source=explicit Humanoid/Muscle candidates.",
        "explicitUnityBakeCandidates": explicit_unity_bake_candidates,
        "uniqueExplicitUnityBakeCandidates": unique_explicit_unity_bake_candidates,
        "explicitUnityBakeModels": explicit_unity_bake_models,
        "explicitUnityBakeAnimations": explicit_unity_bake_animations,
        "bakeReadyExplicitUnityBakeCandidates": bake_ready_explicit_unity_bake_candidates,
        "uniqueBakeReadyExplicitUnityBakeCandidates": unique_bake_ready_explicit_unity_bake_candidates,
        "humanDescriptionBakeReadyExplicitUnityBakeCandidates": human_description_bake_ready_candidates,
        "uniqueHumanDescriptionBakeReadyExplicitUnityBakeCandidates": unique_human_description_bake_ready_candidates,
        "importedAvatarAssetReadiness": imported_avatar_info or {},
        "importedAvatarAssetBakeReadyExplicitUnityBakeCandidates": imported_avatar_bake_ready_candidates,
        "uniqueImportedAvatarAssetBakeReadyExplicitUnityBakeCandidates": unique_imported_avatar_bake_ready_candidates,
        "bakeReadyExplicitUnityBakeCoverage": ratio(bake_ready_explicit_unity_bake_candidates, explicit_unity_bake_candidates),
        "uniqueBakeReadyExplicitUnityBakeCoverage": ratio(unique_bake_ready_explicit_unity_bake_candidates, unique_explicit_unity_bake_candidates),
        "bakeReadyExplicitUnityBakeCoveragePercent": round(ratio(bake_ready_explicit_unity_bake_candidates, explicit_unity_bake_candidates) * 100.0, 3),
        "uniqueBakeReadyExplicitUnityBakeCoveragePercent": round(ratio(unique_bake_ready_explicit_unity_bake_candidates, unique_explicit_unity_bake_candidates) * 100.0, 3),
        "fullHumanoidBakeRequiredCandidates": full_humanoid_bake_required_candidates,
        "fullHumanoidBakeBlockedCandidates": full_humanoid_bake_blocked_candidates,
        "partialDirectGltfCandidates": partial_direct_gltf_candidates,
        "directGltfTransformOnlyCandidates": direct_gltf_transform_only_candidates,
        "cachePresent": table_exists(cur, "animation_bake_cache"),
        "cachedCandidates": 0,
        "requestWrittenCandidates": 0,
        "bakedCandidates": 0,
        "trustedBakedCandidates": 0,
        "staticPoseCandidates": 0,
        "needsReviewCandidates": 0,
        "bakedMissingGltfCandidates": 0,
        "failedCandidates": 0,
        "uniqueCachedCandidates": 0,
        "uniqueRequestWrittenCandidates": 0,
        "uniqueBakedCandidates": 0,
        "uniqueTrustedBakedCandidates": 0,
        "uniqueStaticPoseCandidates": 0,
        "uniqueNeedsReviewCandidates": 0,
        "uniqueBakedMissingGltfCandidates": 0,
        "uniqueFailedCandidates": 0,
        "duplicateCacheRows": 0,
        "pendingUnityBakeCandidates": bake_ready_explicit_unity_bake_candidates,
        "uniquePendingUnityBakeCandidates": unique_bake_ready_explicit_unity_bake_candidates,
        "cacheCoverage": 0.0,
        "bakedCoverage": 0.0,
        "trustedBakedCoverage": 0.0,
        "uniqueCacheCoverage": 0.0,
        "uniqueTrustedBakedCoverage": 0.0,
        "cacheCoveragePercent": 0.0,
        "bakedCoveragePercent": 0.0,
        "trustedBakedCoveragePercent": 0.0,
        "uniqueCacheCoveragePercent": 0.0,
        "uniqueTrustedBakedCoveragePercent": 0.0,
        "statusCounts": [],
    }

    if not result["cachePresent"]:
        result["note"] = "animation_bake_cache table is missing. Run --bake_animation_previews_from_library to populate Unity bake status."
        return result

    result["cachedCandidates"] = int(scalar(cur, f"SELECT COUNT(*) FROM animation_bake_cache bc WHERE {bake_ready_cache_where};"))
    result["requestWrittenCandidates"] = int(scalar(cur, f"SELECT COUNT(*) FROM animation_bake_cache bc WHERE status='request_written' AND {bake_ready_cache_where};"))
    result["bakedCandidates"] = int(scalar(cur, f"SELECT COUNT(*) FROM animation_bake_cache bc WHERE status='baked' AND {bake_ready_cache_where};"))

    unique = {}
    for row in cur.execute(f"SELECT model_output, animation_output, status, baked_gltf_path FROM animation_bake_cache bc WHERE {bake_ready_cache_where};"):
        model_output = canonical_library_output(row["model_output"], library_root)
        animation_output = canonical_library_output(row["animation_output"], library_root)
        if not model_output or not animation_output:
            continue
        entry = unique.setdefault(
            model_output + "|" + animation_output,
            {"rows": 0, "request": False, "baked": False, "trusted": False, "static": False, "needs_review": False, "failed": False, "missing": False},
        )
        entry["rows"] += 1
        status = (row["status"] or "").lower()
        baked_gltf_path = row["baked_gltf_path"]
        trusted = is_trusted_baked_gltf_path(baked_gltf_path, library_root)
        static_pose = is_static_pose_baked_gltf_path(baked_gltf_path, library_root)
        needs_review = is_needs_review_baked_gltf_path(baked_gltf_path, library_root)
        if trusted:
            result["trustedBakedCandidates"] += 1
            entry["baked"] = True
            entry["trusted"] = True
        elif static_pose:
            result["staticPoseCandidates"] += 1
            entry["static"] = True
        elif needs_review or status == "needs_review":
            result["needsReviewCandidates"] += 1
            entry["needs_review"] = True
        elif status == "request_written":
            entry["request"] = True
        elif status == "failed":
            result["failedCandidates"] += 1
            entry["failed"] = True
        elif status == "baked":
            result["bakedMissingGltfCandidates"] += 1
            entry["baked"] = True
            entry["missing"] = True

    result["uniqueCachedCandidates"] = len(unique)
    result["uniqueRequestWrittenCandidates"] = sum(1 for x in unique.values() if x["request"] and not x["failed"] and not x["baked"])
    result["uniqueBakedCandidates"] = sum(1 for x in unique.values() if x["baked"])
    result["uniqueTrustedBakedCandidates"] = sum(1 for x in unique.values() if x["trusted"])
    result["uniqueStaticPoseCandidates"] = sum(1 for x in unique.values() if x["static"])
    result["uniqueNeedsReviewCandidates"] = sum(1 for x in unique.values() if x["needs_review"])
    result["uniqueBakedMissingGltfCandidates"] = sum(1 for x in unique.values() if x["missing"])
    result["uniqueFailedCandidates"] = sum(1 for x in unique.values() if x["failed"] and not x["baked"])
    result["duplicateCacheRows"] = sum(max(0, x["rows"] - 1) for x in unique.values())
    terminal_or_cached = (
        result["uniqueTrustedBakedCandidates"]
        + result["uniqueStaticPoseCandidates"]
        + result["uniqueNeedsReviewCandidates"]
        + result["uniqueBakedMissingGltfCandidates"]
        + result["uniqueFailedCandidates"]
        + result["uniqueRequestWrittenCandidates"]
    )
    result["pendingUnityBakeCandidates"] = max(0, bake_ready_explicit_unity_bake_candidates - terminal_or_cached)
    result["uniquePendingUnityBakeCandidates"] = max(0, unique_bake_ready_explicit_unity_bake_candidates - terminal_or_cached)
    result["cacheCoverage"] = ratio(result["cachedCandidates"], bake_ready_explicit_unity_bake_candidates)
    result["bakedCoverage"] = ratio(result["bakedCandidates"], bake_ready_explicit_unity_bake_candidates)
    result["trustedBakedCoverage"] = ratio(result["trustedBakedCandidates"], bake_ready_explicit_unity_bake_candidates)
    result["uniqueCacheCoverage"] = ratio(result["uniqueCachedCandidates"], unique_bake_ready_explicit_unity_bake_candidates)
    result["uniqueTrustedBakedCoverage"] = ratio(result["uniqueTrustedBakedCandidates"], unique_bake_ready_explicit_unity_bake_candidates)
    result["cacheCoveragePercent"] = round(result["cacheCoverage"] * 100.0, 3)
    result["bakedCoveragePercent"] = round(result["bakedCoverage"] * 100.0, 3)
    result["trustedBakedCoveragePercent"] = round(result["trustedBakedCoverage"] * 100.0, 3)
    result["uniqueCacheCoveragePercent"] = round(result["uniqueCacheCoverage"] * 100.0, 3)
    result["uniqueTrustedBakedCoveragePercent"] = round(result["uniqueTrustedBakedCoverage"] * 100.0, 3)
    result["statusCounts"] = rows(
        cur,
        f"""
        SELECT COALESCE(status, '<null>') AS status,
               COUNT(*) AS count
        FROM animation_bake_cache bc
        WHERE {bake_ready_cache_where}
        GROUP BY COALESCE(status, '<null>')
        ORDER BY count DESC;
        """
    )
    result["effectiveStatusCounts"] = [
        {"status": "trusted_baked", "count": result["trustedBakedCandidates"]},
        {"status": "static_pose", "count": result["staticPoseCandidates"]},
        {"status": "needs_review", "count": result["needsReviewCandidates"]},
        {"status": "baked_missing_gltf_or_report", "count": result["bakedMissingGltfCandidates"]},
        {"status": "failed", "count": result["failedCandidates"]},
    ]
    if include_details:
        result["topModelsByBakeCandidates"] = rows(
            cur,
            f"""
            SELECT t.model_output AS modelOutput,
                   COUNT(*) AS candidateCount,
                   COUNT(DISTINCT t.animation_output) AS animationCount
            FROM temp_unity_bake_candidates t
            GROUP BY t.model_output
            ORDER BY candidateCount DESC, t.model_output COLLATE NOCASE
            LIMIT 24;
            """
        )
        result["topAnimationsByBakeCandidates"] = rows(
            cur,
            f"""
            SELECT t.animation_output AS animationOutput,
                   COUNT(*) AS candidateCount,
                   COUNT(DISTINCT t.model_output) AS modelCount
            FROM temp_unity_bake_candidates t
            GROUP BY t.animation_output
            ORDER BY candidateCount DESC, t.animation_output COLLATE NOCASE
            LIMIT 24;
            """
        )
        category_counts = {}
        for row in cur.execute(
            f"""
            SELECT t.model_output AS model_output,
                   COUNT(*) AS candidate_count,
                   COUNT(DISTINCT t.animation_output) AS animation_count
            FROM temp_unity_bake_candidates t
            GROUP BY t.model_output;
            """
        ):
            category = category_from_output(row["model_output"])
            item = category_counts.setdefault(
                category,
                {"category": category, "candidateCount": 0, "modelCount": 0, "animationAssignments": 0},
            )
            item["modelCount"] += 1
            item["candidateCount"] += int(row["candidate_count"] or 0)
            item["animationAssignments"] += int(row["animation_count"] or 0)
        result["topCategoriesByBakeCandidates"] = sorted(
            category_counts.values(),
            key=lambda x: (-x["candidateCount"], x["category"].lower()),
        )[:24]
    else:
        result["detailMode"] = "summary_only"
        result["topModelsByBakeCandidates"] = []
        result["topAnimationsByBakeCandidates"] = []
        result["topCategoriesByBakeCandidates"] = []
    result["note"] = "Unity bake production coverage is normalized to effective bake-ready explicit candidates: complete HumanDescription.humanBones + skeletonBones or exact matched imported Unity Avatar asset. Trusted baked requires unity_bake_apply_report.json status ok/warning, frameVaryingTracks > 0, avatarTrust.TrustedProductionBake=true, and imported_unity_avatar_asset source when the original request explicitly supplied unityAssetPaths.avatarAsset; static_pose and needs_review are terminal diagnostics and are not counted as failed or pending."
    return result


def avatar_production_gate(cur):
    if not table_exists(cur, "assets") or not table_exists(cur, "model_animation_candidates"):
        return {
            "available": False,
            "note": "assets or model_animation_candidates table is missing.",
            "blockedReasonCounts": [],
        }

    model_avatar = cur.execute(
        """
        SELECT COUNT(*) AS avatarModels,
               SUM(CASE WHEN """ + bake_ready_avatar_sql("assets") + """ THEN 1 ELSE 0 END) AS humanDescriptionProductionModels,
               SUM(CASE WHEN """ + imported_avatar_asset_match_sql("assets") + """ THEN 1 ELSE 0 END) AS importedAvatarAssetProductionModels,
               SUM(CASE WHEN """ + effective_bake_ready_avatar_sql("assets") + """ THEN 1 ELSE 0 END) AS productionAvatarModels,
               SUM(CASE WHEN COALESCE(json_extract(raw_json, '$.avatar.hasHumanDescription'), 0)=1 THEN 1 ELSE 0 END) AS humanDescriptionFlagModels,
               SUM(CASE WHEN COALESCE(json_extract(raw_json, '$.avatar.hasHumanDescription'), 0)=1
                         AND (
                           COALESCE(json_array_length(json_extract(raw_json, '$.avatar.humanBones')), 0)=0
                           OR COALESCE(json_array_length(json_extract(raw_json, '$.avatar.skeletonBones')), 0)=0
                         )
                        THEN 1 ELSE 0 END) AS emptyHumanDescriptionModels,
               SUM(CASE WHEN json_extract(raw_json, '$.avatar.oracle') IS NOT NULL
                         OR json_extract(raw_json, '$.avatar.internalSolver') IS NOT NULL
                        THEN 1 ELSE 0 END) AS avatarConstantOracleModels
        FROM assets
        WHERE kind='Model'
          AND json_extract(raw_json, '$.avatar') IS NOT NULL;
        """
    ).fetchone()
    avatar_models = int(model_avatar["avatarModels"] or 0)
    production_avatar_models = int(model_avatar["productionAvatarModels"] or 0)
    human_description_flag_models = int(model_avatar["humanDescriptionFlagModels"] or 0)
    empty_human_description_models = int(model_avatar["emptyHumanDescriptionModels"] or 0)
    avatar_constant_oracle_models = int(model_avatar["avatarConstantOracleModels"] or 0)
    explicit_humanoid_candidates = int(scalar(
        cur,
        """
        SELECT COUNT(*)
        FROM model_animation_candidates
        WHERE relation_source='explicit'
          AND COALESCE(json_extract(raw_json, '$.fullHumanoidBakeRequired'), 0)=1;
        """
    ))
    explicit_humanoid_models = int(scalar(
        cur,
        """
        SELECT COUNT(DISTINCT model_output)
        FROM model_animation_candidates
        WHERE relation_source='explicit'
          AND COALESCE(json_extract(raw_json, '$.fullHumanoidBakeRequired'), 0)=1;
        """
    ))
    if table_exists(cur, "temp_bake_ready_animation_candidates"):
        ready_candidates = int(scalar(cur, "SELECT COUNT(*) FROM temp_bake_ready_animation_candidates;"))
        ready_models = int(scalar(cur, "SELECT COUNT(DISTINCT model_output) FROM temp_bake_ready_animation_candidates;"))
        blocked_candidates = max(explicit_humanoid_candidates - ready_candidates, 0)
        blocked_models = int(scalar(
            cur,
            """
            SELECT COUNT(DISTINCT t.model_output)
            FROM temp_unity_bake_candidates t
            WHERE NOT EXISTS (
              SELECT 1
              FROM temp_bake_ready_animation_candidates ready
              WHERE ready.model_output=t.model_output
                AND ready.animation_output=t.animation_output
            );
            """
        ))
    else:
        ready_candidates = int(scalar(
            cur,
            """
            SELECT COUNT(*)
            FROM model_animation_candidates c
            JOIN assets m ON m.kind='Model' AND m.output=c.model_output
            WHERE c.relation_source='explicit'
              AND COALESCE(json_extract(c.raw_json, '$.fullHumanoidBakeRequired'), 0)=1
              AND """ + effective_bake_ready_avatar_sql("m") + """;
            """
        ))
        ready_models = int(scalar(
            cur,
            """
            SELECT COUNT(DISTINCT c.model_output)
            FROM model_animation_candidates c
            JOIN assets m ON m.kind='Model' AND m.output=c.model_output
            WHERE c.relation_source='explicit'
              AND COALESCE(json_extract(c.raw_json, '$.fullHumanoidBakeRequired'), 0)=1
              AND """ + effective_bake_ready_avatar_sql("m") + """;
            """
        ))
        blocked_candidates = max(explicit_humanoid_candidates - ready_candidates, 0)
        blocked_models = int(scalar(
            cur,
            """
            SELECT COUNT(DISTINCT c.model_output)
            FROM model_animation_candidates c
            JOIN assets m ON m.kind='Model' AND m.output=c.model_output
            WHERE c.relation_source='explicit'
              AND COALESCE(json_extract(c.raw_json, '$.fullHumanoidBakeRequired'), 0)=1
              AND NOT (""" + effective_bake_ready_avatar_sql("m") + """);
            """
        ))
    blocked_reasons = rows(
        cur,
        """
        SELECT COALESCE(json_extract(c.raw_json, '$.productionUnityBakeBlockedReason'), '(null)') AS reason,
               COUNT(*) AS count
        FROM model_animation_candidates c
        JOIN assets m ON m.kind='Model' AND m.output=c.model_output
        WHERE c.relation_source='explicit'
          AND COALESCE(json_extract(c.raw_json, '$.fullHumanoidBakeRequired'), 0)=1
          AND NOT (""" + effective_bake_ready_avatar_sql("m") + """)
        GROUP BY COALESCE(json_extract(c.raw_json, '$.productionUnityBakeBlockedReason'), '(null)')
        ORDER BY count DESC, reason COLLATE NOCASE;
        """
    )
    return {
        "available": True,
        "rule": "Production Unity bake requires the original Unity prefab/Animator.avatar, complete HumanDescription.humanBones + skeletonBones, or an exact matched imported original Unity Avatar asset. AvatarConstant/internalSolver/oracle is diagnostic recovery input only and is not production ready by itself.",
        "avatarModels": avatar_models,
        "productionAvatarModels": production_avatar_models,
        "humanDescriptionProductionModels": int(model_avatar["humanDescriptionProductionModels"] or 0),
        "importedAvatarAssetProductionModels": int(model_avatar["importedAvatarAssetProductionModels"] or 0),
        "humanDescriptionModels": human_description_flag_models,
        "humanDescriptionFlagModels": human_description_flag_models,
        "completeHumanDescriptionModels": int(model_avatar["humanDescriptionProductionModels"] or 0),
        "emptyHumanDescriptionModels": empty_human_description_models,
        "avatarConstantOracleModels": avatar_constant_oracle_models,
        "productionAvatarModelCoverage": ratio(production_avatar_models, avatar_models),
        "productionAvatarModelCoveragePercent": round(ratio(production_avatar_models, avatar_models) * 100.0, 3),
        "explicitHumanoidCandidates": explicit_humanoid_candidates,
        "explicitHumanoidModels": explicit_humanoid_models,
        "readyCandidates": ready_candidates,
        "readyModels": ready_models,
        "blockedCandidates": blocked_candidates,
        "blockedModels": blocked_models,
        "readyCandidateCoverage": ratio(ready_candidates, explicit_humanoid_candidates),
        "readyCandidateCoveragePercent": round(ratio(ready_candidates, explicit_humanoid_candidates) * 100.0, 3),
        "blockedReasonCounts": blocked_reasons,
    }


def model_avatar_refresh_blockers(cur, top_limit=24):
    if not table_exists(cur, "model_animation_candidates"):
        return {
            "available": False,
            "note": "model_animation_candidates is missing; rebuild SQLite index to get Avatar refresh blocker diagnostics.",
            "byReason": [],
            "topModels": [],
        }

    blocked_cte = """
WITH blocked AS (
    SELECT c.model_output,
           COALESCE(json_extract(c.raw_json, '$.productionUnityBakeBlockedReason'), 'unknown') AS reason
    FROM model_animation_candidates c
    WHERE c.relation_source='explicit'
      AND COALESCE(json_extract(c.raw_json, '$.productionUnityBakeBlocked'), 0)=1
)
"""
    by_reason = rows(
        cur,
        blocked_cte + """
SELECT reason,
       COUNT(DISTINCT model_output) AS modelCount,
       COUNT(*) AS candidateCount
FROM blocked
GROUP BY reason
ORDER BY candidateCount DESC, reason COLLATE NOCASE;
"""
    )
    top_models = rows(
        cur,
        blocked_cte + """
SELECT COALESCE(a.name, b.model_output) AS modelName,
       b.model_output AS modelOutput,
       b.reason AS reason,
       COUNT(*) AS candidateCount,
       (
         SELECT COUNT(*)
         FROM model_animation_candidates c2
         WHERE c2.model_output=b.model_output
           AND c2.relation_source='explicit'
       ) AS explicitCandidateCount,
       (
         SELECT COUNT(*)
         FROM model_animation_candidates c3
         WHERE c3.model_output=b.model_output
           AND c3.relation_source='explicit'
           AND COALESCE(json_extract(c3.raw_json, '$.productionUnityBakeReady'), 0)=1
       ) AS bakeReadyCandidateCount
FROM blocked b
LEFT JOIN assets a ON a.output=b.model_output
GROUP BY b.model_output, b.reason
ORDER BY candidateCount DESC, modelName COLLATE NOCASE
LIMIT ?;
""",
        (max(top_limit, 0),),
    )
    return {
        "available": True,
        "rule": "Counts explicit Humanoid/Muscle candidates blocked by missing production Unity Avatar metadata; sorted by likely unlock count for model Avatar refresh.",
        "byReason": by_reason,
        "topModels": top_models,
        "totalModelCount": sum(int(x.get("modelCount") or 0) for x in by_reason),
        "totalCandidateCount": sum(int(x.get("candidateCount") or 0) for x in by_reason),
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--library-path", required=True)
    parser.add_argument("--library-index", required=True)
    parser.add_argument("--source-index", required=True)
    parser.add_argument("--unity-project", required=False)
    parser.add_argument("--output-dir", required=True)
    parser.add_argument("--animated-category", action="append", default=[])
    parser.add_argument("--top-category-limit", type=int, default=24)
    parser.add_argument("--summary-only", action="store_true")
    parser.add_argument("--gate-only", action="store_true")
    parser.add_argument("--fail-on-warning", action="store_true")
    args = parser.parse_args()

    con = sqlite3.connect(args.library_index)
    con.row_factory = sqlite3.Row
    cur = con.cursor()
    imported_avatar_info = create_temp_imported_avatar_asset_keys(cur, args.unity_project)
    try:
        if not table_exists(cur, "assets") or not table_exists(cur, "model_animation_candidates"):
            raise RuntimeError("library_index.db is missing assets or model_animation_candidates table.")

        total_models = int(scalar(cur, "SELECT COUNT(*) FROM assets WHERE kind='Model';"))
        exported_models = int(scalar(cur, "SELECT COUNT(*) FROM assets WHERE kind='Model' AND output IS NOT NULL AND output<>'';"))
        animations = int(scalar(cur, "SELECT COUNT(*) FROM assets WHERE kind='Animation';"))
        candidates = int(scalar(cur, "SELECT COUNT(*) FROM model_animation_candidates;"))
        explicit_candidates = int(scalar(cur, "SELECT COUNT(*) FROM model_animation_candidates WHERE relation_source='explicit';"))
        non_explicit_candidates = int(scalar(cur, "SELECT COUNT(*) FROM model_animation_candidates WHERE COALESCE(relation_source, '') <> 'explicit';"))
        candidate_schema = candidate_table_schema_health(cur)
        candidate_models = int(scalar(cur, "SELECT COUNT(DISTINCT model_output) FROM model_animation_candidates;"))
        explicit_candidate_models = int(scalar(cur, "SELECT COUNT(DISTINCT model_output) FROM model_animation_candidates WHERE relation_source='explicit';"))
        candidate_animations = int(scalar(cur, "SELECT COUNT(DISTINCT animation_output) FROM model_animation_candidates;"))
        explicit_candidate_animations = int(scalar(cur, "SELECT COUNT(DISTINCT animation_output) FROM model_animation_candidates WHERE relation_source='explicit';"))

        relation_sources = rows(
            cur,
            """
            SELECT COALESCE(relation_source, '<null>') AS relationSource,
                   COUNT(*) AS candidateCount,
                   COUNT(DISTINCT model_output) AS modelCount,
                   COUNT(DISTINCT animation_output) AS animationCount
            FROM model_animation_candidates
            GROUP BY COALESCE(relation_source, '<null>')
            ORDER BY candidateCount DESC;
            """
        )
        confidences = rows(
            cur,
            """
            SELECT COALESCE(confidence, '<null>') AS confidence,
                   COUNT(*) AS candidateCount,
                   COUNT(DISTINCT model_output) AS modelCount,
                   COUNT(DISTINCT animation_output) AS animationCount
            FROM model_animation_candidates
            GROUP BY COALESCE(confidence, '<null>')
            ORDER BY candidateCount DESC;
            """
        )
        statuses = rows(
            cur,
            """
            SELECT COALESCE(status, '<null>') AS status,
                   COUNT(*) AS candidateCount
            FROM model_animation_candidates
            GROUP BY COALESCE(status, '<null>')
            ORDER BY candidateCount DESC;
            """
        )

        if args.gate_only:
            top_categories = []
            animated_categories = []
            animated_total = 0
            animated_linked = 0
            unity_bake_summary = {
                "available": False,
                "rule": "GateOnly skips Unity bake production coverage and only checks deterministic relation gates.",
                "note": "Run without -GateOnly for full bake/cache/avatar coverage.",
                "statusCounts": [],
            }
            avatar_gate_summary = {
                "available": False,
                "note": "GateOnly skips Avatar production coverage. Run without -GateOnly for full Avatar diagnostics.",
                "blockedReasonCounts": [],
            }
            avatar_blockers = {
                "available": False,
                "note": "GateOnly skips model Avatar refresh blocker queries.",
                "byReason": [],
                "topModels": [],
                "totalModelCount": 0,
                "totalCandidateCount": 0,
            }
        else:
            model_outputs = [row[0] for row in cur.execute("SELECT output FROM assets WHERE kind='Model' AND output IS NOT NULL AND output<>'';")]
            explicit_model_outputs = set(row[0] for row in cur.execute("SELECT DISTINCT model_output FROM model_animation_candidates WHERE relation_source='explicit';"))

            categories = {}
            for output in model_outputs:
                category = category_from_output(output)
                item = categories.setdefault(category, {"category": category, "models": 0, "modelsWithExplicitCandidates": 0})
                item["models"] += 1
                if output in explicit_model_outputs:
                    item["modelsWithExplicitCandidates"] += 1
            for item in categories.values():
                item["explicitCoverage"] = ratio(item["modelsWithExplicitCandidates"], item["models"])

            top_categories = sorted(categories.values(), key=lambda x: (-x["models"], x["category"].lower()))[: max(args.top_category_limit, 0)]
            animated_categories = []
            for category in args.animated_category:
                item = categories.get(category)
                if item is None:
                    item = {"category": category, "models": 0, "modelsWithExplicitCandidates": 0, "explicitCoverage": 0.0}
                animated_categories.append(item)
            animated_total = sum(x["models"] for x in animated_categories)
            animated_linked = sum(x["modelsWithExplicitCandidates"] for x in animated_categories)
            unity_bake_summary = unity_bake_production(
                cur,
                args.library_path,
                include_details=not args.summary_only,
                imported_avatar_info=imported_avatar_info)
            avatar_gate_summary = avatar_production_gate(cur)
            avatar_blockers = (
                {
                    "available": False,
                    "note": "SummaryOnly skips model Avatar refresh blocker detail queries.",
                    "byReason": [],
                    "topModels": [],
                    "totalModelCount": 0,
                    "totalCandidateCount": 0,
                }
                if args.summary_only else
                model_avatar_refresh_blockers(cur, args.top_category_limit)
            )

        gate_reasons = []
        if non_explicit_candidates != 0:
            gate_reasons.append("Default candidate table contains non-explicit relations; verify they are not bone-count, name, or path fallback.")
        if candidate_schema.get("status") != "ok":
            gate_reasons.append(candidate_schema.get("note") or "model_animation_candidates schema is not constrained to explicit relations.")

        summary = {
            "generatedAt": dt.datetime.now(dt.timezone.utc).isoformat(),
            "mode": "gateOnly" if args.gate_only else "summaryOnly" if args.summary_only else "full",
            "libraryPath": args.library_path,
            "libraryIndex": args.library_index,
            "sourceIndex": args.source_index,
            "totals": {
                "models": total_models,
                "exportedModels": exported_models,
                "animations": animations,
                "candidates": candidates,
                "explicitCandidates": explicit_candidates,
                "nonExplicitCandidates": non_explicit_candidates,
                "modelsWithAnyCandidates": candidate_models,
                "modelsWithExplicitCandidates": explicit_candidate_models,
                "animationsWithAnyCandidates": candidate_animations,
                "animationsWithExplicitCandidates": explicit_candidate_animations,
                "allModelExplicitCoverage": ratio(explicit_candidate_models, exported_models),
                "animationExplicitCoverage": ratio(explicit_candidate_animations, animations),
                "animatedCategoryModels": animated_total,
                "animatedCategoryModelsWithExplicitCandidates": animated_linked,
                "animatedCategoryExplicitCoverage": ratio(animated_linked, animated_total),
            },
            "gate": {
                "status": "ok" if not gate_reasons else "warning",
                "requiresNoNonExplicitCandidates": True,
                "requiresExplicitRelationSourceSchema": True,
                "note": (
                    "Default candidate table contains no non-explicit relation and schema rejects future non-explicit inserts."
                    if not gate_reasons else
                    " ".join(gate_reasons)
                ),
            },
            "candidateTableSchema": candidate_schema,
            "relationSources": relation_sources,
            "confidences": confidences,
            "statuses": statuses,
            "animatedCategories": animated_categories,
            "topCategories": top_categories,
            "unityBakeProduction": unity_bake_summary,
            "avatarProductionGate": avatar_gate_summary,
            "modelAvatarRefreshBlockers": avatar_blockers,
            "sourceIndexAnimationRelationHealth": source_health(args.source_index),
        }
    finally:
        con.close()

    os.makedirs(args.output_dir, exist_ok=True)
    json_path = os.path.join(args.output_dir, "deterministic_animation_coverage.json")
    md_path = os.path.join(args.output_dir, "DETERMINISTIC_ANIMATION_COVERAGE.md")
    with open(json_path, "w", encoding="utf-8") as f:
        json.dump(summary, f, ensure_ascii=False, indent=2)

    def table(items, columns):
        lines = []
        lines.append("| " + " | ".join(columns) + " |")
        lines.append("| " + " | ".join(["---"] * len(columns)) + " |")
        for item in items:
            lines.append("| " + " | ".join(str(item.get(col, "")) for col in columns) + " |")
        return lines

    md = []
    md.append("# Deterministic Animation Coverage")
    md.append("")
    md.append(f"- Library: `{args.library_path}`")
    md.append(f"- Library index: `{args.library_index}`")
    md.append(f"- Source index: `{args.source_index}`")
    md.append(f"- Mode: `{summary.get('mode')}`")
    md.append(f"- Gate: `{summary['gate']['status']}` - {summary['gate']['note']}")
    md.append("")
    schema = summary.get("candidateTableSchema") or {}
    md.append("## Candidate Table Schema")
    md.append(f"- Status: `{schema.get('status')}`")
    md.append(f"- relation_source NOT NULL: `{schema.get('hasRelationSourceNotNull')}`")
    md.append(f"- CHECK relation_source explicit: `{schema.get('hasExplicitRelationSourceCheck')}`")
    md.append(f"- Note: {schema.get('note')}")
    md.append("")
    md.extend(table([summary["totals"]], [
        "models",
        "exportedModels",
        "animations",
        "candidates",
        "explicitCandidates",
        "nonExplicitCandidates",
        "modelsWithExplicitCandidates",
        "animationsWithExplicitCandidates",
        "animatedCategoryExplicitCoverage",
    ]))
    md.append("")
    md.append("## Relation Sources")
    md.extend(table(summary["relationSources"], ["relationSource", "candidateCount", "modelCount", "animationCount"]))
    md.append("")
    md.append("## Animated Categories")
    md.extend(table(summary["animatedCategories"], ["category", "modelsWithExplicitCandidates", "models", "explicitCoverage"]))
    md.append("")
    md.append("## Unity Bake Production")
    bake = summary["unityBakeProduction"]
    md.append(f"- Rule: {bake.get('rule')}")
    md.append(f"- Cache present: `{bake.get('cachePresent')}`")
    imported_readiness = bake.get("importedAvatarAssetReadiness") or {}
    if imported_readiness:
        md.append(f"- Imported Avatar assets: `{imported_readiness.get('fileCount', 0)}` files / `{imported_readiness.get('keyCount', 0)}` keys")
        if imported_readiness.get("note"):
            md.append(f"- Imported Avatar note: {imported_readiness.get('note')}")
    md.append(f"- Note: {bake.get('note')}")
    md.append("")
    md.extend(table([bake], [
        "explicitUnityBakeCandidates",
        "uniqueExplicitUnityBakeCandidates",
        "explicitUnityBakeModels",
        "explicitUnityBakeAnimations",
        "bakeReadyExplicitUnityBakeCandidates",
        "uniqueBakeReadyExplicitUnityBakeCandidates",
        "humanDescriptionBakeReadyExplicitUnityBakeCandidates",
        "importedAvatarAssetBakeReadyExplicitUnityBakeCandidates",
        "uniqueImportedAvatarAssetBakeReadyExplicitUnityBakeCandidates",
        "bakeReadyExplicitUnityBakeCoveragePercent",
        "uniqueBakeReadyExplicitUnityBakeCoveragePercent",
        "fullHumanoidBakeRequiredCandidates",
        "fullHumanoidBakeBlockedCandidates",
        "partialDirectGltfCandidates",
        "directGltfTransformOnlyCandidates",
        "cachedCandidates",
        "uniqueCachedCandidates",
        "requestWrittenCandidates",
        "bakedCandidates",
        "trustedBakedCandidates",
        "staticPoseCandidates",
        "needsReviewCandidates",
        "uniqueTrustedBakedCandidates",
        "uniqueStaticPoseCandidates",
        "uniqueNeedsReviewCandidates",
        "uniquePendingUnityBakeCandidates",
        "duplicateCacheRows",
        "failedCandidates",
        "cacheCoveragePercent",
        "trustedBakedCoveragePercent",
        "uniqueTrustedBakedCoveragePercent",
    ]))
    if bake.get("statusCounts"):
        md.append("")
        md.append("### Bake Cache Status")
        md.extend(table(bake["statusCounts"], ["status", "count"]))
    if bake.get("effectiveStatusCounts"):
        md.append("")
        md.append("### Effective Bake Status")
        md.extend(table(bake["effectiveStatusCounts"], ["status", "count"]))
    if bake.get("topCategoriesByBakeCandidates"):
        md.append("")
        md.append("### Bake Candidate Categories")
        md.extend(table(bake["topCategoriesByBakeCandidates"], ["category", "candidateCount", "modelCount", "animationAssignments"]))
    if bake.get("topModelsByBakeCandidates"):
        md.append("")
        md.append("### Top Bake Candidate Models")
        md.extend(table(bake["topModelsByBakeCandidates"], ["modelOutput", "candidateCount", "animationCount"]))
    if bake.get("topAnimationsByBakeCandidates"):
        md.append("")
        md.append("### Top Bake Candidate Animations")
        md.extend(table(bake["topAnimationsByBakeCandidates"], ["animationOutput", "candidateCount", "modelCount"]))
    avatar_gate = summary.get("avatarProductionGate") or {}
    md.append("")
    md.append("## Avatar Production Gate")
    md.append(f"- Available: `{avatar_gate.get('available')}`")
    if avatar_gate.get("rule"):
        md.append(f"- Rule: {avatar_gate.get('rule')}")
    if avatar_gate.get("note"):
        md.append(f"- Note: {avatar_gate.get('note')}")
    md.append("")
    md.extend(table([avatar_gate], [
        "avatarModels",
        "productionAvatarModels",
        "humanDescriptionProductionModels",
        "importedAvatarAssetProductionModels",
        "humanDescriptionModels",
        "humanDescriptionFlagModels",
        "completeHumanDescriptionModels",
        "emptyHumanDescriptionModels",
        "avatarConstantOracleModels",
        "productionAvatarModelCoveragePercent",
        "explicitHumanoidCandidates",
        "explicitHumanoidModels",
        "readyCandidates",
        "readyModels",
        "blockedCandidates",
        "blockedModels",
        "readyCandidateCoveragePercent",
    ]))
    if avatar_gate.get("blockedReasonCounts"):
        md.append("")
        md.append("### Avatar Gate Blocked Reasons")
        md.extend(table(avatar_gate["blockedReasonCounts"], ["reason", "count"]))
    blockers = summary.get("modelAvatarRefreshBlockers") or {}
    md.append("")
    md.append("## Model Avatar Refresh Blockers")
    md.append(f"- Available: `{blockers.get('available')}`")
    if blockers.get("rule"):
        md.append(f"- Rule: {blockers.get('rule')}")
    if blockers.get("note"):
        md.append(f"- Note: {blockers.get('note')}")
    md.append(f"- Total blocked models: `{blockers.get('totalModelCount', 0)}`")
    md.append(f"- Total blocked candidates: `{blockers.get('totalCandidateCount', 0)}`")
    if blockers.get("byReason"):
        md.append("")
        md.append("### Avatar Blocked By Reason")
        md.extend(table(blockers["byReason"], ["reason", "modelCount", "candidateCount"]))
    if blockers.get("topModels"):
        md.append("")
        md.append("### Top Avatar Refresh Models")
        md.extend(table(blockers["topModels"], ["modelName", "reason", "candidateCount", "explicitCandidateCount", "bakeReadyCandidateCount", "modelOutput"]))
    md.append("")
    md.append("## Top Model Categories")
    md.extend(table(summary["topCategories"], ["category", "modelsWithExplicitCandidates", "models", "explicitCoverage"]))
    md.append("")
    md.append("## Source Index Health")
    health = summary["sourceIndexAnimationRelationHealth"]
    md.append(f"- Status: `{health.get('status')}`")
    md.append(f"- staleOverridePairIndex: `{health.get('staleOverridePairIndex', '')}`")
    md.append(f"- Note: {health.get('note')}")
    md.append("")
    if health.get("relationCounts"):
        md.extend(table([{"relation": k, "count": v} for k, v in health["relationCounts"].items()], ["relation", "count"]))

    with open(md_path, "w", encoding="utf-8") as f:
        f.write("\n".join(md) + "\n")

    print(json_path)
    print(md_path)
    console_summary = {
        "gate": summary["gate"],
        "totals": summary["totals"],
        "candidateTableSchema": summary["candidateTableSchema"],
        "unityBakeProduction": summary["unityBakeProduction"],
        "avatarProductionGate": summary["avatarProductionGate"],
        "sourceIndexStatus": summary["sourceIndexAnimationRelationHealth"].get("status"),
        "staleOverridePairIndex": summary["sourceIndexAnimationRelationHealth"].get("staleOverridePairIndex"),
    }
    print(json.dumps(console_summary, ensure_ascii=False))

    if args.fail_on_warning:
        failed_reasons = []
        if summary["gate"].get("status") != "ok":
            failed_reasons.append(summary["gate"].get("note") or "deterministic gate is not ok")
        source_status = summary["sourceIndexAnimationRelationHealth"].get("status")
        if source_status not in ("ok", "missing"):
            failed_reasons.append(summary["sourceIndexAnimationRelationHealth"].get("note") or f"source index status is {source_status}")
        if failed_reasons:
            print(json.dumps({
                "status": "failed",
                "rule": "-FailOnWarning treats deterministic gate/source-index warnings as command failures.",
                "reasons": failed_reasons,
                "json": json_path,
                "markdown": md_path,
            }, ensure_ascii=False), file=sys.stderr)
            sys.exit(2)


if __name__ == "__main__":
    main()
'@

$scriptPath = Join-Path $OutputDir "_measure_deterministic_animation_coverage.py"
$python | Set-Content -LiteralPath $scriptPath -Encoding UTF8

$argsList = @(
    $scriptPath,
    "--library-path", (Resolve-Path -LiteralPath $LibraryPath).Path,
    "--library-index", (Resolve-Path -LiteralPath $LibraryIndex).Path,
    "--source-index", $SourceIndex,
    "--output-dir", (Resolve-Path -LiteralPath $OutputDir).Path,
    "--top-category-limit", $TopCategoryLimit
)
foreach ($category in $AnimatedCategory) {
    $argsList += @("--animated-category", $category)
}
if ($FailOnWarning) {
    $argsList += "--fail-on-warning"
}
if ($GateOnly) {
    $argsList += "--gate-only"
}
if ($SummaryOnly) {
    $argsList += "--summary-only"
}
if (-not [string]::IsNullOrWhiteSpace($UnityProject)) {
    $argsList += @("--unity-project", (Resolve-Path -LiteralPath $UnityProject).Path)
}

try {
    python @argsList
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
finally {
    if ($LASTEXITCODE -eq 0) {
        Remove-Item -LiteralPath $scriptPath -Force -ErrorAction SilentlyContinue
    }
}
