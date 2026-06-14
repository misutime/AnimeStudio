param(
    [string]$Root = "Temp\OverrideClipPairRegression",
    [string]$CliPath = "AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe"
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$rootPath = if ([System.IO.Path]::IsPathRooted($Root)) {
    $Root
} else {
    Join-Path $repoRoot $Root
}
$rootPath = [System.IO.Path]::GetFullPath($rootPath)

if (-not $rootPath.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refuse to write regression temp files outside repo: $rootPath"
}

if (Test-Path -LiteralPath $rootPath) {
    Remove-Item -LiteralPath $rootPath -Recurse -Force
}
New-Item -ItemType Directory -Path $rootPath | Out-Null

$pythonCreate = @'
import json
import pathlib
import sqlite3
import sys

root = pathlib.Path(sys.argv[1])
mode = sys.argv[2]
(root / "Models" / "Test").mkdir(parents=True, exist_ok=True)
(root / "Animations" / "Test").mkdir(parents=True, exist_ok=True)

assets = [
    {"kind":"Model","libraryRole":"PrefabPrimary","sourceType":"Animator","name":"ModelAnimator","source":"main.assets","pathId":100,"output":"Models/Test/ModelAnimator.gltf"},
    {"kind":"Animation","name":"OriginalClip","source":"main.assets","pathId":300,"output":"Animations/Test/OriginalClip.animation_asset.json","animationType":"Generic"},
    {"kind":"Animation","name":"OverrideClip","source":"main.assets","pathId":301,"output":"Animations/Test/OverrideClip.animation_asset.json","animationType":"Generic"},
    {"kind":"Animation","name":"KeepClip","source":"main.assets","pathId":302,"output":"Animations/Test/KeepClip.animation_asset.json","animationType":"Generic"},
]
with (root / "asset_catalog.jsonl").open("w", encoding="utf-8") as f:
    for obj in assets:
        f.write(json.dumps(obj, ensure_ascii=False, separators=(",", ":")) + "\n")

con = sqlite3.connect(root / "unity_source_index.db")
cur = con.cursor()
cur.executescript("""
CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT);
CREATE TABLE source_objects (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    source_path TEXT,
    serialized_file TEXT,
    path_id INTEGER,
    type TEXT,
    class_id INTEGER,
    name TEXT,
    byte_start INTEGER,
    byte_size INTEGER,
    raw_json TEXT NOT NULL
);
CREATE TABLE source_relations (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    relation TEXT,
    confidence TEXT,
    from_source TEXT,
    from_file TEXT,
    from_type TEXT,
    from_name TEXT,
    from_path_id INTEGER,
    to_file_id INTEGER,
    to_file TEXT,
    to_type_hint TEXT,
    to_path_id INTEGER,
    target_count INTEGER,
    raw_json TEXT NOT NULL
);
""")
cur.executemany("INSERT INTO metadata(key,value) VALUES (?,?)", [
    ("schemaVersion", "1"),
    ("sourceRoot", str(root)),
    ("animationRelationFeatures", "animatorController.clip,animatorOverrideController.originalClip,animatorOverrideController.overrideClip,animatorOverrideController.clipPair,animation.clip"),
])

def raw_obj(type_name, name, path_id):
    return json.dumps({"type":type_name,"name":name,"file":"main.assets","pathId":path_id}, separators=(",", ":"))

objects = [
    ("main.assets","main.assets",100,"Animator",95,"ModelAnimator",raw_obj("Animator","ModelAnimator",100)),
    ("main.assets","main.assets",200,"AnimatorOverrideController",221,"OverrideController",raw_obj("AnimatorOverrideController","OverrideController",200)),
    ("main.assets","main.assets",201,"AnimatorController",91,"BaseController",raw_obj("AnimatorController","BaseController",201)),
    ("main.assets","main.assets",300,"AnimationClip",74,"OriginalClip",raw_obj("AnimationClip","OriginalClip",300)),
    ("main.assets","main.assets",301,"AnimationClip",74,"OverrideClip",raw_obj("AnimationClip","OverrideClip",301)),
    ("main.assets","main.assets",302,"AnimationClip",74,"KeepClip",raw_obj("AnimationClip","KeepClip",302)),
]
cur.executemany(
    "INSERT INTO source_objects(source_path,serialized_file,path_id,type,class_id,name,byte_start,byte_size,raw_json) VALUES (?,?,?,?,?,?,?,?,?)",
    [(a,b,c,d,e,f,0,0,g) for a,b,c,d,e,f,g in objects],
)

def pptr(path_id, type_hint="AnimationClip"):
    return {"fileId":0,"file":"main.assets","pathName":None,"pathId":path_id,"typeHint":type_hint}

def insert_relation(relation, from_type, from_name, from_path, to_path, to_type, details=None):
    raw = {
        "kind":"sourceRelation",
        "relation":relation,
        "confidence":"explicit_pptr",
        "from":{"source":"main.assets","file":"main.assets","type":from_type,"name":from_name,"pathId":from_path},
        "to":pptr(to_path, to_type),
        "details":details,
    }
    cur.execute(
        "INSERT INTO source_relations(relation,confidence,from_source,from_file,from_type,from_name,from_path_id,to_file_id,to_file,to_type_hint,to_path_id,target_count,raw_json) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)",
        (relation,"explicit_pptr","main.assets","main.assets",from_type,from_name,from_path,0,"main.assets",to_type,to_path,None,json.dumps(raw,separators=(",", ":"))),
    )

insert_relation("animator.controller", "Animator", "ModelAnimator", 100, 200, "RuntimeAnimatorController")
insert_relation("animatorOverrideController.baseController", "AnimatorOverrideController", "OverrideController", 200, 201, "RuntimeAnimatorController")
insert_relation("animatorController.clip", "AnimatorController", "BaseController", 201, 300, "AnimationClip")
insert_relation("animatorController.clip", "AnimatorController", "BaseController", 201, 302, "AnimationClip")

def insert_override_set(count):
    raw = {
        "kind":"sourceRelation",
        "relation":"animatorOverrideController.overrideSet",
        "confidence":"explicit_pptr",
        "from":{"source":"main.assets","file":"main.assets","type":"AnimatorOverrideController","name":"OverrideController","pathId":200},
        "to":pptr(0, "AnimationClipOverride"),
        "details":{"count":count},
    }
    cur.execute(
        "INSERT INTO source_relations(relation,confidence,from_source,from_file,from_type,from_name,from_path_id,to_file_id,to_file,to_type_hint,to_path_id,target_count,raw_json) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)",
        ("animatorOverrideController.overrideSet","explicit_pptr","main.assets","main.assets","AnimatorOverrideController","OverrideController",200,0,"main.assets","AnimationClipOverride",0,count,json.dumps(raw,separators=(",", ":"))),
    )

if mode in ("pair", "separated"):
    insert_relation("animatorOverrideController.originalClip", "AnimatorOverrideController", "OverrideController", 200, 300, "AnimationClip")
    insert_relation("animatorOverrideController.overrideClip", "AnimatorOverrideController", "OverrideController", 200, 301, "AnimationClip")

if mode == "pair":
    insert_override_set(1)
    pair_details = {"original":pptr(300, "AnimationClip"), "override":pptr(301, "AnimationClip")}
    raw = {
        "kind":"sourceRelation",
        "relation":"animatorOverrideController.clipPair",
        "confidence":"explicit_pptr",
        "from":{"source":"main.assets","file":"main.assets","type":"AnimatorOverrideController","name":"OverrideController","pathId":200},
        "to":pptr(0, "AnimationClipOverride"),
        "details":pair_details,
    }
    cur.execute(
        "INSERT INTO source_relations(relation,confidence,from_source,from_file,from_type,from_name,from_path_id,to_file_id,to_file,to_type_hint,to_path_id,target_count,raw_json) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)",
        ("animatorOverrideController.clipPair","explicit_pptr","main.assets","main.assets","AnimatorOverrideController","OverrideController",200,0,"main.assets","AnimationClipOverride",0,None,json.dumps(raw,separators=(",", ":"))),
    )
elif mode == "empty":
    insert_override_set(0)
elif mode not in ("separated", "stale"):
    raise ValueError(f"unknown mode: {mode}")

con.commit()
con.close()
'@

$cliFullPath = if ([System.IO.Path]::IsPathRooted($CliPath)) {
    $CliPath
} else {
    Join-Path $repoRoot $CliPath
}
if (-not (Test-Path -LiteralPath $cliFullPath)) {
    throw "CLI not found. Build AnimeStudio.CLI first: $cliFullPath"
}

$pythonCheck = @'
import sqlite3
import sys

db = sys.argv[1]
mode = sys.argv[2]
con = sqlite3.connect(db)
rows = [x[0] for x in con.execute("SELECT animation_output FROM model_animation_candidates ORDER BY animation_output")]
con.close()

if mode in ("pair", "separated"):
    expected = [
        "Animations/Test/KeepClip.animation_asset.json",
        "Animations/Test/OverrideClip.animation_asset.json",
    ]
elif mode == "empty":
    expected = [
        "Animations/Test/KeepClip.animation_asset.json",
        "Animations/Test/OriginalClip.animation_asset.json",
    ]
elif mode == "stale":
    expected = []
else:
    raise ValueError(f"unknown mode: {mode}")

if rows != expected:
    print("Unexpected override candidates:")
    print("actual  =", rows)
    print("expected=", expected)
    sys.exit(1)

if mode == "pair":
    print("OK: AnimatorOverrideController.clipPair keeps override clip and drops replaced original clip.")
elif mode == "separated":
    print("OK: separated legacy override clips are compatible but still require source-index rebuild for exact pairs.")
elif mode == "empty":
    print("OK: empty AnimatorOverrideController overrideSet safely inherits base controller clips.")
else:
    print("OK: stale AnimatorOverrideController baseController does not expand imprecise base clips.")
'@

$pythonCheckHealth = @'
import json
import pathlib
import sys

report = pathlib.Path(sys.argv[1])
mode = sys.argv[2]
data = json.loads(report.read_text(encoding="utf-8"))
health = data.get("animationRelationHealth", {})
status = health.get("status")
stale = health.get("staleOverridePairIndex")
expected_status = "ok" if mode in ("pair", "empty") else "warning"
expected_stale = False if mode in ("pair", "empty") else True
if status != expected_status or stale != expected_stale:
    print("Unexpected source-index health:")
    print("mode   =", mode)
    print("status =", status, "expected", expected_status)
    print("stale  =", stale, "expected", expected_stale)
    sys.exit(1)
print(f"OK: source-index health for {mode} is {status}.")
'@

function Invoke-OverrideRegressionCase([string]$Name, [string]$Mode) {
    $caseRoot = Join-Path $rootPath $Name
    New-Item -ItemType Directory -Path $caseRoot | Out-Null

    # 这里用 Python 只负责生成最小 SQLite fixture，避免为了测试再引入新的 C# 测试工程。
    $pythonCreate | python - $caseRoot $Mode

    & $cliFullPath --build_sqlite_index $caseRoot --skip_sqlite_file_index --skip_sqlite_json_documents
    if ($LASTEXITCODE -ne 0) {
        throw "SQLite index build failed with exit code $LASTEXITCODE"
    }

    & $cliFullPath --verify_source_index (Join-Path $caseRoot "unity_source_index.db")
    if ($LASTEXITCODE -ne 0) {
        throw "Source index health verification failed with exit code $LASTEXITCODE"
    }

    $pythonCheck | python - (Join-Path $caseRoot "library_index.db") $Mode
    if ($LASTEXITCODE -ne 0) {
        throw "Override regression failed: $Name"
    }

    $pythonCheckHealth | python - (Join-Path $caseRoot "unity_source_index.animation_relation_health.json") $Mode
    if ($LASTEXITCODE -ne 0) {
        throw "Override source health regression failed: $Name"
    }
}

Invoke-OverrideRegressionCase -Name "WithClipPair" -Mode "pair"
Invoke-OverrideRegressionCase -Name "EmptyOverrideSet" -Mode "empty"
Invoke-OverrideRegressionCase -Name "SeparatedNoPair" -Mode "separated"
Invoke-OverrideRegressionCase -Name "StaleBaseOnly" -Mode "stale"
