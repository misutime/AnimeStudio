param(
    [Parameter(Mandatory = $true)]
    [Alias("LibraryRoot")]
    [string]$LibraryPath,

    [string]$LibraryIndex,

    [Alias("OutputDirectory")]
    [string]$OutputDir,

    [int]$Limit = 200,

    [string]$UnityProject = "D:\misutime\AnimeStudioUnityProject",

    [string]$UnityEditor = "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe"
)

if ($PSVersionTable.PSVersion.Major -lt 6) {
    $pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
    if ($pwsh -ne $null) {
        $forwardArgs = @()
        foreach ($entry in $PSBoundParameters.GetEnumerator()) {
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
args = parser.parse_args()

con = sqlite3.connect(args.db)
con.row_factory = sqlite3.Row
cur = con.cursor()

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
    item = avatars.setdefault(key, {
        "avatarName": avatar_name,
        "source": source,
        "sourceShort": short_source(source),
        "pathId": int(path_id),
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

items = sorted(
    avatars.values(),
    key=lambda x: (x["internalHumanoidLinks"], x["explicitLinks"], x["models"]),
    reverse=True,
)
if args.limit and args.limit > 0:
    limited = items[:args.limit]
else:
    limited = items

summary = {
    "generatedAt": dt.datetime.now(dt.timezone.utc).isoformat(),
    "libraryRoot": args.library,
    "libraryIndex": args.db,
    "uniqueAvatarObjects": len(items),
    "listedAvatarObjects": len(limited),
    "totalModels": sum(x["models"] for x in items),
    "totalExplicitLinks": sum(x["explicitLinks"] for x in items),
    "totalInternalHumanoidLinks": sum(x["internalHumanoidLinks"] for x in items),
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
    writer.writerow(["rank", "avatarName", "models", "internalHumanoidLinks", "explicitLinks", "source", "pathId", "suggestedUnityAsset", "sampleModel"])
    for index, item in enumerate(limited, 1):
        sample = item["samples"][0]["modelName"] if item["samples"] else ""
        writer.writerow([
            index,
            item["avatarName"],
            item["models"],
            item["internalHumanoidLinks"],
            item["explicitLinks"],
            item["sourceShort"],
            item["pathId"],
            item["suggestedUnityAsset"],
            sample,
        ])

config = {
    "unityProject": args.unity_project,
    "unityEditor": args.unity_editor,
    "unityAvatarAssets": {
        item["avatarName"]: item["suggestedUnityAsset"]
        for item in limited
    },
}
with open(config_path, "w", encoding="utf-8-sig") as handle:
    json.dump(config, handle, ensure_ascii=False, indent=2)

with open(md_path, "w", encoding="utf-8-sig") as handle:
    handle.write("# Unity Avatar Asset 恢复优先级\n\n")
    handle.write("这份计划只读取 Library 的确定性模型元数据：`assets.raw_json.avatar.name/source/pathId` 和 `model_animation_candidate_model_summary`。它不新增模型-动画关系，也不按名称猜测 Avatar。\n\n")
    handle.write(f"- 独立 Avatar 对象: {len(items)}\n")
    handle.write(f"- 列出 Avatar 对象: {len(limited)}\n")
    handle.write(f"- 覆盖模型数: {summary['totalModels']}\n")
    handle.write(f"- 覆盖显式候选链接: {summary['totalExplicitLinks']}\n")
    handle.write(f"- 覆盖 Humanoid/internal 链接: {summary['totalInternalHumanoidLinks']}\n\n")
    handle.write("| rank | avatar | models | internal links | explicit links | source | pathId |\n")
    handle.write("|---:|---|---:|---:|---:|---|---:|\n")
    for index, item in enumerate(limited[:80], 1):
        handle.write(
            f"| {index} | {item['avatarName']} | {item['models']} | {item['internalHumanoidLinks']} | "
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
    --unity-editor $UnityEditor
