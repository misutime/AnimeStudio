# AnimeStudio Quick Start

从 Unity 打包游戏中提取可用的 3D 素材库（模型、贴图、材质、骨骼）。

## 环境要求

- Windows 10/11
- .NET 9.0 SDK（构建用）

## 第一步：构建

```powershell
cd D:\misutime\AnimeStudio
.\build.ps1
```

产物在 `dist\net9.0-windows\AnimeStudio.CLI.exe`。后续所有命令都用这个入口。

> 开发调试可用快速构建：`dotnet build AnimeStudio.CLI\AnimeStudio.CLI.csproj -f net9.0-windows`
> 入口变为 `AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe`

## 第二步：导出素材库

### 标准命令

```powershell
cd D:\misutime\AnimeStudio

.\dist\net9.0-windows\AnimeStudio.CLI.exe `
  "<游戏_Data目录>" `
  "<输出目录>" `
  --game Normal `
  --mode Library
```

**示例**（导出 Realm of Ink）：

```powershell
.\dist\net9.0-windows\AnimeStudio.CLI.exe `
  "D:\BaiduNetdiskDownload\RealmofInk\Realm of Ink_Data" `
  "D:\Assets\RealmOfInk_Library" `
  --game Normal `
  --mode Library
```

**输入路径**：指向 Unity 游戏的 `_Data` 目录（包含 `StreamingAssets`、`globalgamemanagers` 等的那个目录）。

**输出路径**：自定义，建议放在 `D:\Assets\<游戏名>_Library`。

**`--game`**：
- 普通 Unity 游戏 → `Normal`
- 原神 → `GI`
- 其他加密游戏查看 `--help`

### 首次运行注意

首次运行会自动构建源索引（扫描全部 bundle 和 .assets 文件），大游戏可能需要 10-30 分钟。同一输入目录后续运行会复用索引，速度会快很多。

## 默认导出什么

默认 `--mode Library` 导出以下内容：

| 资源类型 | 默认行为 | 说明 |
|---|---|---|
| 3D 模型 | 导出 | prefab/Animator 组合模型，glTF 格式 |
| 贴图 | 导出 | PNG 格式，按用途分目录 |
| 材质 | 导出 | glTF PBR + Unity 原始属性 |
| 骨骼/skin | 导出 | SkinnedMeshRenderer bones、bind pose |
| SQLite 索引 | 导出 | `library_index.db`，方便查询 |
| 报告文件 | 导出 | 每个模型有 `ASSET_README.md` 和 `MATERIAL_REPORT.md` |
| 静态 Mesh | 不导出 | 场景/建筑/道具等独立 Mesh，需显式开启 |
| VFX 特效 | 不导出 | 需显式开启 |
| 动画 | 不导出 | 默认跳过，仅记录边界信息 |
| 音频 | 不导出 | 需单独用 `AudioLibrary` 模式 |

## 默认过滤什么

CLI 自动过滤以下垃圾/非素材内容，不会出现在 `Models/` 目录中：

- collider、NavMesh、OcclusionMesh 等不可视几何
- Socket、Joint、Bone 等辅助对象
- 空对象、损坏对象、obsolete/deprecated 资源
- 普通裸 Mesh（没有 prefab/Animator 组合关系的）
- Dialog、Timeline、UI、preview、cutscene 等专用实例
- VFX 特效网格

**简单说**：默认只导出角色、NPC、怪物、武器、道具等"组合模型"，不会被场景碎片淹没。

## 输出目录结构

```
<输出目录>/
  Models/                  # 模型（glTF），按分类子目录
  Textures/
    _ModelDependencies/    # 模型直接使用的贴图
    MaterialLibrary/       # Unity 材质引用的贴图
    Texture2DArray/        # 数组贴图（地形/地表等）
  Materials/               # 材质 JSON
  LIBRARY_README.md        # 素材库入口说明
  asset_catalog.jsonl      # 资产主索引（机器可读）
  animation_out_of_scope.json  # 动画边界记录
  unity_source_index.db    # Unity 源索引（依赖底座）
  library_index.db         # SQLite 素材库索引
  export_manifest.jsonl    # 导出文件清单
```

每个模型目录下：
- `ASSET_README.md` — 模型使用入口，先看这个
- `MATERIAL_REPORT.md` — 材质细节
- `model.gltf` + 贴图 — 实际素材文件

## 常用扩展参数

### 额外导出场景/建筑/道具 Mesh

```powershell
--include_static_meshes
```

默认只导出角色等组合模型。加这个参数后，有明确 container 路径或 Renderer 使用关系的静态 Mesh 也会导出到 `Models/`。

### 额外导出 VFX 特效元数据

```powershell
--include_vfx
```

开启后 `VFX/` 目录会记录 ParticleSystem、LineRenderer 等特效的元数据和近似预览参数。当前是 metadata-first 管线，不代表特效已完整烘焙。

### 定向导出特定模型

```powershell
--containers "Assets/Prefabs/Hero"
```

按 Unity container 路径过滤导出候选。依赖图仍来自完整源目录，不会切断外部引用。

### 性能分析

```powershell
--profile_log profile.jsonl
```

记录每阶段耗时，导出结束后生成 `profile_summary.json`。

## 其他模式

### 音频素材库

音频不在默认 Library 中，需要单独导出：

```powershell
.\dist\net9.0-windows\AnimeStudio.CLI.exe `
  "<游戏_Data目录>" `
  "<输出目录>" `
  --game Normal `
  --mode AudioLibrary
```

输出 `Audio/SFX/`、`Audio/Music/`、`Audio/Voice/`、`Audio/Other/`，每个音频旁有 `.audio.json` 元数据。

### 底层显式提取

需要精确控制导出类型时：

```powershell
.\dist\net9.0-windows\AnimeStudio.CLI.exe `
  "<游戏_Data目录>" `
  "<输出目录>" `
  --game Normal `
  --mode Export `
  --types Texture2D:Both Mesh:Both `
  --group_assets ByLibrary `
  --export_type Convert
```

## 常见问题

**Q: 导出的模型是灰色/白色的？**
A: 部分游戏使用 ColorMask/Tint 运行时换色，CLI 保留了原始贴图和 mask，但无法自动确定配色。查看 `MATERIAL_REPORT.md` 中的 `needsCustomizationTint` 说明。这不是贴图丢失。

**Q: 模型看起来缺件？**
A: Unity 游戏常把脸、头发、附件拆到不同 bundle。CLI 会自动解析跨 bundle 依赖，但如果输入目录不完整（比如只复制了部分 bundle）会导致缺件。确保指向完整的游戏 `_Data` 目录。

**Q: 能导出动画吗？**
A: 动画当前不是默认目标。默认跳过动画，只记录边界信息。动画导出仍处于诊断/研究阶段，结果需要逐个验证。如需尝试，加 `--animation_package Embedded`。

**Q: 同一个游戏第二次跑为什么快很多？**
A: 首次运行会构建源索引和 CAB map，后续运行复用已有索引，只需做增量检查。

**Q: 怎么查看支持哪些 `--game`？**
A: `.\dist\net9.0-windows\AnimeStudio.CLI.exe --help`

## 深入文档

- `docs/CLI_USAGE.md` — 完整命令参考和所有参数说明
- `docs/PROJECT_EXPORT_STANDARDS.md` — 导出规范、模型验收标准、材质策略
- `docs/game-profiles/` — 单游戏特殊支持记录
- `docs/RESOURCE_EXPORT_ROADMAP.md` — 资源导出演进路线
