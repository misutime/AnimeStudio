param(
    [Parameter(Mandatory = $true)]
    [Alias("LibraryRoot")]
    [string]$LibraryPath,

    [string]$LibraryIndex,

    [Alias("OutputDirectory")]
    [string]$OutputDir,

    [int]$Limit = 200,

    [string]$UnityProject = "D:\misutime\AnimeStudioUnityProject",

    [string]$UnityEditor = "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe",

    [switch]$OnlyMissing
)

if ($PSVersionTable.PSVersion.Major -lt 6) {
    $pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
    if ($pwsh -ne $null) {
        $forwardArgs = @()
        foreach ($entry in $PSBoundParameters.GetEnumerator()) {
            if ($entry.Value -is [System.Management.Automation.SwitchParameter]) {
                if ($entry.Value.IsPresent) {
                    $forwardArgs += "-$($entry.Key)"
                }
                continue
            }

            $forwardArgs += "-$($entry.Key)"
            if ($entry.Value -is [Array]) {
                $forwardArgs += $entry.Value
            }
            else {
                $forwardArgs += [string]$entry.Value
            }
        }

        & $pwsh.Source -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath @forwardArgs
        exit $LASTEXITCODE
    }
}

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($LibraryIndex)) {
    $LibraryIndex = Join-Path $LibraryPath "library_index.db"
}
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $OutputDir = Join-Path $LibraryPath "UnityAvatarAssetRecoveryPlan_$stamp"
}

if (-not (Test-Path -LiteralPath $LibraryIndex)) {
    throw "找不到 library_index.db: $LibraryIndex"
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$python = @'
import argparse
import csv
import datetime as dt
import json
import os
import sqlite3


def read_json(text):
    try:
        return json.loads(text or "{}")
    except Exception:
        return {}


def short_source(value):
    text = (value or "").replace("\\", "/")
    marker = "/AssetBundles/blocks/"
    if marker in text:
        return text.split(marker, 1)[1]
    return text


def safe_asset_file_name(name):
    safe = "".join(ch if ch.isalnum() or ch in "._-" else "_" for ch in (name or "Avatar"))
    return safe + ".asset"


parser = argparse.ArgumentParser()
parser.add_argument("--library", required=True)
parser.add_argument("--db", required=True)
parser.add_argument("--out", required=True)
parser.add_argument("--limit", type=int, default=200)
parser.add_argument("--unity-project", default="")
parser.add_argument("--unity-editor", default="")
parser.add_argument("--only-missing", action="store_true")
args = parser.parse_args()


def imported_avatar_assets(unity_project):
    result = {}
    keys = set()
    latest_write = None
    if not unity_project:
        return result, keys, latest_write
    root = os.path.join(unity_project, "Assets", "AnimeStudioBake", "ImportedAvatar")
    if not os.path.isdir(root):
        return result, keys, latest_write
    for dirpath, _dirnames, filenames in os.walk(root):
        for filename in filenames:
            if not filename.lower().endswith(".asset"):
                continue
            full_path = os.path.join(dirpath, filename)
            stem = os.path.splitext(filename)[0]
            unity_path = os.path.relpath(full_path, unity_project).replace("\\", "/")
            result[stem] = unity_path
            keys.add(stem)
            suffix = "_ModelAvatar"
            if stem.lower().endswith(suffix.lower()):
                keys.add(stem[:-len(suffix)])
            timestamp = os.path.getmtime(full_path)
            latest_write = timestamp if latest_write is None else max(latest_write, timestamp)
    return result, keys, latest_write


def latest_imported_avatar_probe(library_root, imported_asset_count, latest_asset_write):
    result = {
        "path": "",
        "freshness": "not_run",
        "status": "",
        "totalAssets": 0,
        "validHumanAvatars": 0,
        "invalidAssets": 0,
        "validKeys": set(),
        "invalidKeys": set(),
        "error": "",
    }
    if not library_root or not os.path.isdir(library_root):
        return result
    candidates = []
    for name in os.listdir(library_root):
        if not name.startswith("ImportedAvatarProbe"):
            continue
        path = os.path.join(library_root, name, "imported_avatar_probe_batch.json")
        if os.path.isfile(path):
            candidates.append(path)
    if not candidates:
        return result
    path = max(candidates, key=lambda x: os.path.getmtime(x))
    result["path"] = path
    try:
        with open(path, "r", encoding="utf-8-sig") as handle:
            probe = json.load(handle)
    except Exception as exc:
        result["freshness"] = "error"
        result["error"] = str(exc)
        return result

    result["status"] = str(probe.get("status") or "")
    result["totalAssets"] = int(probe.get("totalAssets") or 0)
    result["validHumanAvatars"] = int(probe.get("validHumanAvatars") or 0)
    result["invalidAssets"] = int(probe.get("invalidAssets") or 0)
    for item in probe.get("items") or []:
        asset_path = str(item.get("avatarAssetPath") or "").replace("\\", "/")
        stem = os.path.splitext(os.path.basename(asset_path))[0]
        if not stem:
            continue
        keys = {stem}
        suffix = "_ModelAvatar"
        if stem.lower().endswith(suffix.lower()):
            keys.add(stem[:-len(suffix)])
        if str(item.get("status") or "").lower() == "ok" and item.get("isValid") and item.get("isHuman"):
            result["validKeys"].update(keys)
        else:
            result["invalidKeys"].update(keys)

    if result["totalAssets"] != imported_asset_count:
        result["freshness"] = "mismatch"
    elif latest_asset_write is not None and os.path.getmtime(path) < latest_asset_write:
        result["freshness"] = "stale"
    else:
        result["freshness"] = "fresh"
    return result

con = sqlite3.connect(args.db)
con.row_factory = sqlite3.Row
cur = con.cursor()

imported_assets, imported_keys, imported_latest_write = imported_avatar_assets(args.unity_project)
avatar_probe = latest_imported_avatar_probe(args.library, len(imported_assets), imported_latest_write)
probe_enforced = avatar_probe["freshness"] == "fresh"
valid_imported_keys = avatar_probe["validKeys"] if probe_enforced else imported_keys
avatars = {}
sql = """
SELECT a.name, a.output, a.raw_json,
       s.explicit_count, s.direct_preview_count, s.internal_humanoid_count
FROM assets a
JOIN model_animation_candidate_model_summary s ON s.model_output = a.output
WHERE a.kind = 'Model' AND s.explicit_count > 0;
"""
for row in cur.execute(sql):
    raw = read_json(row["raw_json"])
    avatar = raw.get("avatar") or {}
    avatar_name = avatar.get("name")
    source = avatar.get("source")
    path_id = avatar.get("pathId")
    if not avatar_name or not source or path_id is None:
        continue

    key = (avatar_name, source, int(path_id))
    existing_asset = imported_assets.get(avatar_name, "")
    probe_state = "unverified"
    if probe_enforced:
        if avatar_name in avatar_probe["validKeys"]:
            probe_state = "valid"
        elif avatar_name in avatar_probe["invalidKeys"]:
            probe_state = "invalid"
        elif existing_asset:
            probe_state = "missing_probe_item"
        else:
            probe_state = "missing"
    item = avatars.setdefault(key, {
        "avatarName": avatar_name,
        "source": source,
        "sourceShort": short_source(source),
        "pathId": int(path_id),
        "isRecovered": avatar_name in valid_imported_keys,
        "existingUnityAsset": existing_asset,
        "avatarAssetProbeStatus": probe_state,
        "models": 0,
        "explicitLinks": 0,
        "internalHumanoidLinks": 0,
        "directPreviewLinks": 0,
        "samples": [],
        "suggestedUnityAsset": "Assets/AnimeStudioBake/ImportedAvatar/" + safe_asset_file_name(avatar_name),
    })
    item["models"] += 1
    item["explicitLinks"] += int(row["explicit_count"] or 0)
    item["internalHumanoidLinks"] += int(row["internal_humanoid_count"] or 0)
    item["directPreviewLinks"] += int(row["direct_preview_count"] or 0)
    if len(item["samples"]) < 8:
        item["samples"].append({
            "modelName": row["name"],
            "modelOutput": row["output"],
            "explicitLinks": int(row["explicit_count"] or 0),
            "internalHumanoidLinks": int(row["internal_humanoid_count"] or 0),
        })

items_all = sorted(
    avatars.values(),
    key=lambda x: (not x["isRecovered"], x["internalHumanoidLinks"], x["explicitLinks"], x["models"]),
    reverse=True,
)
items = [x for x in items_all if not x["isRecovered"]] if args.only_missing else items_all

missing_items = [x for x in items_all if not x["isRecovered"]]
recovered_items = [x for x in items_all if x["isRecovered"]]
total_internal_links = sum(x["internalHumanoidLinks"] for x in items_all)
total_explicit_links = sum(x["explicitLinks"] for x in items_all)
missing_internal_links = sum(x["internalHumanoidLinks"] for x in missing_items)
missing_explicit_links = sum(x["explicitLinks"] for x in missing_items)
recovered_internal_links = sum(x["internalHumanoidLinks"] for x in recovered_items)
recovered_explicit_links = sum(x["explicitLinks"] for x in recovered_items)


def percent(part, total):
    return round((float(part) / float(total) * 100.0), 6) if total else 0.0


running_internal = 0
running_explicit = 0
for item in items:
    if not item["isRecovered"]:
        running_internal += item["internalHumanoidLinks"]
        running_explicit += item["explicitLinks"]
    item["cumulativeMissingInternalHumanoidLinks"] = running_internal
    item["cumulativeMissingExplicitLinks"] = running_explicit
    item["cumulativeMissingInternalHumanoidCoveragePercent"] = percent(running_internal, missing_internal_links)
    item["cumulativeMissingExplicitCoveragePercent"] = percent(running_explicit, missing_explicit_links)

if args.limit and args.limit > 0:
    limited = items[:args.limit]
else:
    limited = items

summary = {
    "generatedAt": dt.datetime.now(dt.timezone.utc).isoformat(),
    "libraryRoot": args.library,
    "libraryIndex": args.db,
    "unityProject": args.unity_project,
    "onlyMissing": bool(args.only_missing),
    "importedAvatarAssetCount": len(imported_assets),
    "importedAvatarProbeReportPath": avatar_probe["path"],
    "importedAvatarProbeFreshness": avatar_probe["freshness"],
    "importedAvatarProbeStatus": avatar_probe["status"],
    "importedAvatarProbeTotalAssets": avatar_probe["totalAssets"],
    "importedAvatarProbeValidHumanAvatars": avatar_probe["validHumanAvatars"],
    "importedAvatarProbeInvalidAssets": avatar_probe["invalidAssets"],
    "importedAvatarProbeError": avatar_probe["error"],
    "importedAvatarProbeEnforced": probe_enforced,
    "uniqueAvatarObjects": len(items_all),
    "recoveredAvatarObjects": sum(1 for x in items_all if x["isRecovered"]),
    "missingAvatarObjects": sum(1 for x in items_all if not x["isRecovered"]),
    "listedAvatarObjects": len(limited),
    "totalModels": sum(x["models"] for x in items_all),
    "totalExplicitLinks": total_explicit_links,
    "totalInternalHumanoidLinks": total_internal_links,
    "recoveredExplicitLinks": recovered_explicit_links,
    "recoveredInternalHumanoidLinks": recovered_internal_links,
    "recoveredExplicitCoveragePercent": percent(recovered_explicit_links, total_explicit_links),
    "recoveredInternalHumanoidCoveragePercent": percent(recovered_internal_links, total_internal_links),
    "missingModels": sum(x["models"] for x in missing_items),
    "missingExplicitLinks": missing_explicit_links,
    "missingInternalHumanoidLinks": missing_internal_links,
    "missingExplicitCoveragePercent": percent(missing_explicit_links, total_explicit_links),
    "missingInternalHumanoidCoveragePercent": percent(missing_internal_links, total_internal_links),
    "items": limited,
}

json_path = os.path.join(args.out, "unity_avatar_asset_recovery_plan.json")
csv_path = os.path.join(args.out, "unity_avatar_asset_recovery_plan.csv")
md_path = os.path.join(args.out, "UNITY_AVATAR_ASSET_RECOVERY_PLAN.md")
config_path = os.path.join(args.out, "unity_bake_settings.avatar_assets.template.json")

with open(json_path, "w", encoding="utf-8-sig") as handle:
    json.dump(summary, handle, ensure_ascii=False, indent=2)

with open(csv_path, "w", encoding="utf-8-sig", newline="") as handle:
    writer = csv.writer(handle)
    writer.writerow([
        "rank",
        "status",
        "avatarName",
        "models",
        "internalHumanoidLinks",
        "explicitLinks",
        "cumulativeMissingInternalHumanoidLinks",
        "cumulativeMissingInternalHumanoidCoveragePercent",
        "cumulativeMissingExplicitLinks",
        "cumulativeMissingExplicitCoveragePercent",
        "avatarAssetProbeStatus",
        "source",
        "pathId",
        "existingUnityAsset",
        "suggestedUnityAsset",
        "sampleModel",
    ])
    for index, item in enumerate(limited, 1):
        sample = item["samples"][0]["modelName"] if item["samples"] else ""
        writer.writerow([
            index,
            "recovered" if item["isRecovered"] else "missing",
            item["avatarName"],
            item["models"],
            item["internalHumanoidLinks"],
            item["explicitLinks"],
            item["cumulativeMissingInternalHumanoidLinks"],
            item["cumulativeMissingInternalHumanoidCoveragePercent"],
            item["cumulativeMissingExplicitLinks"],
            item["cumulativeMissingExplicitCoveragePercent"],
            item["avatarAssetProbeStatus"],
            item["sourceShort"],
            item["pathId"],
            item["existingUnityAsset"],
            item["suggestedUnityAsset"],
            sample,
        ])

config = {
    "unityProject": args.unity_project,
    "unityEditor": args.unity_editor,
    "unityAvatarAssets": {
        item["avatarName"]: item["suggestedUnityAsset"]
        for item in limited
        if not item["isRecovered"]
    },
}
with open(config_path, "w", encoding="utf-8-sig") as handle:
    json.dump(config, handle, ensure_ascii=False, indent=2)

with open(md_path, "w", encoding="utf-8-sig") as handle:
    handle.write("# Unity Avatar Asset 恢复优先级\n\n")
    handle.write("这份计划只读取 Library 的确定性模型元数据：`assets.raw_json.avatar.name/source/pathId` 和 `model_animation_candidate_model_summary`。它不新增模型-动画关系，也不按名称猜测 Avatar。\n\n")
    handle.write(f"- 独立 Avatar 对象: {len(items_all)}\n")
    handle.write(f"- 已导入 Avatar asset: {len(imported_assets)}\n")
    handle.write(f"- ImportedAvatar probe freshness: {summary['importedAvatarProbeFreshness']}\n")
    handle.write(f"- ImportedAvatar probe enforced: {summary['importedAvatarProbeEnforced']}\n")
    if summary["importedAvatarProbeReportPath"]:
        handle.write(f"- ImportedAvatar probe report: {summary['importedAvatarProbeReportPath']}\n")
        handle.write(f"- ImportedAvatar probe valid/invalid: {summary['importedAvatarProbeValidHumanAvatars']} / {summary['importedAvatarProbeInvalidAssets']}\n")
    handle.write(f"- 已恢复 Avatar 对象: {summary['recoveredAvatarObjects']}\n")
    handle.write(f"- 待恢复 Avatar 对象: {summary['missingAvatarObjects']}\n")
    handle.write(f"- 列出 Avatar 对象: {len(limited)}\n")
    handle.write(f"- 覆盖模型数: {summary['totalModels']}\n")
    handle.write(f"- 覆盖显式候选链接: {summary['totalExplicitLinks']}\n")
    handle.write(f"- 覆盖 Humanoid/internal 链接: {summary['totalInternalHumanoidLinks']}\n\n")
    handle.write(f"- 已恢复 Humanoid/internal 链接: {summary['recoveredInternalHumanoidLinks']} ({summary['recoveredInternalHumanoidCoveragePercent']}%)\n")
    handle.write(f"- 待恢复 Humanoid/internal 链接: {summary['missingInternalHumanoidLinks']} ({summary['missingInternalHumanoidCoveragePercent']}%)\n")
    handle.write(f"- 已恢复显式候选链接: {summary['recoveredExplicitLinks']} ({summary['recoveredExplicitCoveragePercent']}%)\n")
    handle.write(f"- 待恢复显式候选链接: {summary['missingExplicitLinks']} ({summary['missingExplicitCoveragePercent']}%)\n\n")
    if args.only_missing:
        handle.write("当前报告启用了 OnlyMissing，只列出尚未在 `Assets/AnimeStudioBake/ImportedAvatar` 精确命中的 Avatar。\n\n")
    handle.write("| rank | status | avatar | probe | models | internal links | cumulative missing internal | cumulative % | explicit links | source | pathId |\n")
    handle.write("|---:|---|---|---|---:|---:|---:|---:|---:|---|---:|\n")
    for index, item in enumerate(limited[:80], 1):
        handle.write(
            f"| {index} | {'recovered' if item['isRecovered'] else 'missing'} | {item['avatarName']} | {item['avatarAssetProbeStatus']} | {item['models']} | {item['internalHumanoidLinks']} | "
            f"{item['cumulativeMissingInternalHumanoidLinks']} | {item['cumulativeMissingInternalHumanoidCoveragePercent']} | "
            f"{item['explicitLinks']} | {item['sourceShort']} | {item['pathId']} |\n"
        )
    handle.write("\n## 使用方式\n\n")
    handle.write("1. 按 CSV/Markdown 的优先级，用 UnityRipper/uTinyRipper 等工具从 `source/pathId` 对应的 Unity 对象恢复原始 `UnityEngine.Avatar` asset。\n")
    handle.write("2. 把恢复出的 `.asset` 放进 bake 工程，例如 `Assets/AnimeStudioBake/ImportedAvatar/<AvatarName>.asset`。\n")
    handle.write("3. 把 `unity_bake_settings.avatar_assets.template.json` 中已经实际恢复的条目复制到素材库 `.as_browser_cache/unity_bake_settings.json`。\n")
    handle.write("4. Browser 会按模型元数据里的 `avatar.name` 命中这些配置，未恢复的 Avatar 不会自动兜底。\n")

print(json_path)
print(csv_path)
print(md_path)
print(config_path)
'@

$scriptPath = Join-Path $OutputDir "_write_unity_avatar_asset_recovery_plan.py"
Set-Content -LiteralPath $scriptPath -Value $python -Encoding UTF8

python $scriptPath `
    --library $LibraryPath `
    --db $LibraryIndex `
    --out $OutputDir `
    --limit $Limit `
    --unity-project $UnityProject `
    --unity-editor $UnityEditor `
    $(if ($OnlyMissing) { "--only-missing" })
