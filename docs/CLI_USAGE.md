# AnimeStudio CLI 通用资源导出说明

本文档说明如何使用 `AnimeStudio.CLI` 导出 Unity 游戏资源。目标是让普通 Unity 游戏和原神等特殊游戏使用同一套工作流，只在 `--game`、资源路径、asset index 等必要位置区分。

## 构建与 CLI 入口

### 正式 Release / dist 构建

真实游戏全量索引或全量导出建议使用仓库自带构建脚本：

```powershell
cd D:\misutime\AnimeStudio
.\build.ps1
```

脚本会构建 `Release` 版 CLI/GUI、执行 patcher，并把可运行产物整理到：

```text
D:\misutime\AnimeStudio\dist\net9.0-windows
D:\misutime\AnimeStudio\dist\net8.0-windows
```

推荐优先使用 net9.0-windows：

```powershell
D:\misutime\AnimeStudio\dist\net9.0-windows\AnimeStudio.CLI.exe
```

`dist` 目录更适合团队复现、长时间全量任务和真实素材导出。GUI 构建阶段如果出现 FBXNative 复制 warning，不影响 CLI 的源索引、glTF 模型、PNG 贴图和 SQLite 建库。

### 开发 Debug 构建

快速调试 CLI 可以只构建 CLI 项目：

```powershell
cd D:\misutime\AnimeStudio
dotnet build AnimeStudio.CLI\AnimeStudio.CLI.csproj -f net9.0-windows
```

Debug 版 CLI 输出在：

```text
D:\misutime\AnimeStudio\AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe
```

Debug 构建适合开发和小样本验证；真实全量任务优先使用 `.\build.ps1` 生成的 `dist` 版本。

### CLI 入口

```powershell
D:\misutime\AnimeStudio\dist\net9.0-windows\AnimeStudio.CLI.exe
```

建议先进入仓库目录再运行：

```powershell
cd D:\misutime\AnimeStudio
```

然后用相对入口：

```powershell
dist\net9.0-windows\AnimeStudio.CLI.exe
```

## 核心概念

### game

普通 Unity 游戏使用：

```powershell
--game Normal
```

原神使用：

```powershell
--game GI
```

其他加密或定制 Unity 游戏需要选择对应的 `--game`。可以通过 `--help` 查看当前支持列表。

### 自动 map

通常不需要手动传 `--map_name`。

不传时，CLI 会根据：

- `--game`
- 输入根目录

自动生成稳定 map 名，例如：

```text
auto_Normal_7d5f5ad9af87
auto_GI_3a1b2c4d5e6f
```

同一个游戏和同一个输入目录会复用同一个 map；换游戏或换输入目录会使用不同 map，避免错误复用旧依赖图。

CAB map 是完整 Unity 外部引用索引，不是单次导出的裁剪缓存。模型、动画、材质、贴图等 3D 导出会用完整输入目录建立 CAB / SerializedFile / PPtr 依赖图，然后再用 `--containers`、`--names` 过滤导出候选。这样即使只导出一个角色，也不会切断脸、附件、材质、Mesh、动画等外部 CAB 引用。

新版 CAB map 会记录来源文件数量和完整输入集合 fingerprint。只要游戏目录没有变化，同一个 map 可以长期复用；如果源文件发生变化、显式复用了错误 map、或 map 格式升级，CLI 会自动重建完整 map，而不是继续使用可能断链的旧索引。

如果需要兼容旧流程或固定共享 map，可以手动指定：

```powershell
--map_name assets_map
```

### group assets

常用分组方式：

```powershell
--group_assets ByContainer
```

按资源容器路径组织输出，适合普通 Unity 游戏。

```powershell
--group_assets ByLibrary
```

输出为素材库结构，适合原神这类目录恢复不完整但需要模型、贴图、材质分库管理的场景。

## 默认目标：导出可用素材库

AnimeStudio CLI 现在默认以“可供开发者直接使用的素材库”为导出目标，而不是尽量复刻 Unity 运行时 prefab 的杂乱结构。

默认行为：

- `--mode Library`：导出素材库。
- `--group_assets ByLibrary`：按 `Models`、`Animations`、`Textures`、`Materials` 等库目录组织。
- `--model_format Gltf`：模型默认 glTF。
- `--model_source PrefabPrimary`：模型默认以 prefab/Animator 组合体为主；raw fbx/source parts 只进入索引或 `RawUnreferenced`。
- 普通裸 Mesh 默认不作为浏览模型导出；需要场景、建筑、地形块、道具等独立 Mesh 时显式加 `--include_static_meshes`。
- VFX 默认不导出，也不生成 VFX catalog、VFX 预览缓存或 mesh-VFX 分类；需要特效元数据和近似预览时显式加 `--include_vfx`。
- `--texture_mode Png`：贴图默认 PNG，模型目录下有 `Textures` 硬链接，方便直接查看。
- `--animation_package Separate`：动画默认独立入库，不嵌入每个模型。
- 默认 Library 先专注 prefab/Animator 模型、贴图、材质、骨骼和动画。普通 Mesh 和 VFX 属于显式扩展域，避免默认批量导出被场景碎片、特效网格和 VFX metadata 放大。
- 显式 `--include_vfx` 后才生成 `VFX/` 特效元数据目录：Unity `ParticleSystem`、`LineRenderer`、`TrailRenderer`、`VisualEffect`、GPU Particle/VFX 对象会进入 `vfx.json` / `VFX_REPORT.md`；mesh 型特效会在 `Models` 中标注 `resourceKind=VFX`。当前这是 metadata-first 管线，不代表 ParticleSystem/shader 动画已经完整烘焙为可播放标准格式。

最重要的准则：

> 默认导出结果应该像完整可浏览素材库，而不是像 Unity 运行时对象转储，也不是最终精品包。模型要尽量完整覆盖、分类清晰、可追溯；动画要独立可复用；贴图要默认可见；manifest 和报告负责记录源路径、材质状态、可信度和后续绑定关系。

更底层的实现准则：

> 动画、骨骼、模型、材质、贴图的绑定关系必须来自 Unity 自身序列化引用和结构。不要把单个游戏的目录习惯、角色命名、资源前缀当成核心逻辑；如无不得已的明确理由，默认逻辑绝不自行推断关系。

CLI 生成索引时，关系优先级应为：

1. Unity 显式引用：Animator、Animation、AnimatorController、AnimatorOverrideController、PPtr。
2. Unity 结构兼容：Avatar、HumanDescription、SkinnedMeshRenderer bones、AnimationClip binding path/type/property、blendshape channel。
3. 实际导出验证：glTF channel、skin/joint、主体骨骼覆盖、bbox。
4. 显式 fallback：container、目录名、资源名、游戏 profile。fallback 必须明确标注，不能进入默认绑定结果。

跨游戏验证准则：

- 默认规则必须在多个 Unity 游戏上交叉验证，不能为了某个游戏的目录或命名做核心逻辑特判。
- Freedunk 用于验证篮球人物、球场、道具和 Humanoid/Transform/BlendShape 动画；VRising、Valheim 等其他 PC Unity 游戏用于验证通用资源入口、bundle/SerializedFile 发现、模型/贴图/骨骼/动画索引规则。
- 跨游戏初期可以接受有少量重复或 debug 基础体混入，但不能系统性漏掉成熟游戏里的建筑、植被、POI、岩石、地形块、道具或静态 Mesh。对动画候选仍然严格：没有驱动可见 mesh、或武器/道具语义明显冲突的候选宁可降级为待检查，也不要污染默认推荐关系。

### 模型来源规则

默认 `Library` 使用 `--model_source PrefabPrimary`：

- `Models` 默认放 Unity prefab、Animator、完整 GameObject 组合模型，优先保证角色/道具/组合模型干净可浏览。
- `fbx` 原始身体、face、附件等 source parts 不默认作为可浏览模型导出，避免 `Jimmy_01_00.gltf` 这类只有身体、没有 prefab 组合关系的文件污染浏览结果。
- 这些 raw/source parts 不丢弃，会写入 `asset_catalog.jsonl`，`kind=ModelSourcePart`，并用 `libraryRole=RawModel`、`SourcePart` 或 `AttachmentSource` 标记。
- 如果某个 raw fbx/source part 没有对应的 prefab/Animator 组合模型，它会作为 `libraryRole=RawUnreferenced` 导出到 `Models/RawUnreferenced`。
- 需要研究零散部件时显式使用 `--model_source PrefabAndParts`；只看 raw 部件时使用 `--model_source RawPartsOnly`。

静态环境、建筑、地形块、植被、道具等独立 Mesh 默认不导出。需要研究这类素材时显式使用 `--include_static_meshes`，开启后 `StaticMeshPrimary` 的信号包括：

- 明确的 `AssetBundle.m_Container` / preload 容器路径，且路径语义指向 environment、building、prop、world、stage、terrain、levelbuild 等素材。
- 来源 AssetBundle / SerializedFile 本身具有静态素材语义，例如 `LevelBuildElements`、`Terrain`、`Environment`、`World`、`Building`。
- SQLite 源索引能证明该 Mesh 经 `MeshFilter.mesh` 或 `SkinnedMeshRenderer.mesh` 被 Renderer 使用，并且 Renderer 有 `renderer.material`。

第三种情况会直接恢复材质：glTF 的 `materials[].extras.animeStudioMaterial.status` 会写 `boundRendererMaterial`，模型旁 `ASSET_README.md` 会列出选中的 Renderer 和 Material，贴图会落到 `Textures/_ModelDependencies` 并被 glTF 引用。没有 Renderer 材质绑定的裸 Mesh 不会硬猜材质，只会标记 `needsRendererBinding` 或 `missingRendererMaterial`。

### 材质和 ColorMask/Tint 规则

默认 glTF 导出会优先生成“可浏览、可复用”的标准材质，而不是尝试完整复刻每个游戏的 Unity shader。标准 PBR 能表达的内容会直接写入 glTF，例如基础贴图、透明/裁剪、双面、法线贴图等。

很多 Unity 游戏的角色换色不是单纯一张成品贴图，而是：

- `_BaseColorMap`：灰度或中性基础贴图。
- `_ColorMask` / `_MaskMap`：告诉 shader 哪些区域应该染色。
- 材质 color/float 或角色 customization 配置：运行时决定皮肤、衣服、头发等颜色。

AnimeStudio 的默认策略是：

1. 保留 Unity 原始材质关系。
   glTF `extras.unityMaterial` 会记录材质贴图槽、float、color、源 CAB/PathID 等信息。
2. 建立 ColorMask/Tint 索引。
   如果发现 `_ColorMask`、`_MaskMap`、`_BaseColorMap` 等槽位，会在 `extras.animeStudioMaterial` 写入 `workflow=ColorMaskTint`。
3. 能确定颜色时才烘焙预览贴图。
   找到明确 tint 参数或后续 customization 配置后，可以生成预览用 base color，方便作为素材库直接查看。
4. 不能确定时不硬猜。
   如果只有 mask 和基础贴图，没有可用颜色配置，会标记 `status=needsCustomizationTint`。这表示模型和贴图关系是完整的，但需要后续解析 shader/customization tint 才能恢复最终配色。

以 VRising 为例，身体材质可能显示为灰色，但 glTF 里已经保留 `_BaseColorMap`、`_ColorMask`、`_MaskMap` 和 `_NormalMap`。这不是贴图丢失，而是游戏运行时的 tint 配置还没有还原。后续应继续解析 Unity 配置或角色 customization 数据，再按通用 ColorMask/Tint 管线生成预览贴图。

每个 glTF/GLB 模型目录还会生成 `MATERIAL_REPORT.md`。这是给人看的材质说明，会列出材质状态、Unity 贴图槽、mask、需要特殊处理的原因和建议；机器可读的完整数据仍以 glTF `extras` 和材质 JSON 为准。

每个模型目录还会生成 `ASSET_README.md`。这是模型使用入口，会汇总基础信息、材质状态、动画候选、模块/头发/头部/附件组装关系和使用建议。正常浏览素材时先看 `ASSET_README.md`，需要材质细节时再看 `MATERIAL_REPORT.md`。

### Texture2DArray / Texture2DArrayImage

Unity 的 `Texture2DArray` 是数组贴图，常见于地形、地表、shader 采样、材质变体或运行时混合。它通常不是普通模型 glTF PBR 材质能直接引用的一张图片，所以默认模型导出不会把它强行塞进 glTF。

默认 `Library` 输出会导出材质/地表贴图库：

- `Textures/_ModelDependencies`：模型 glTF 直接显示所需的贴图。
- `Textures/MaterialLibrary`：Unity `Material.m_SavedProperties.m_TexEnvs` 明确引用的 `Texture2D`。这覆盖大量 terrain、surface、material、mask、normal、base map 等贴图，同时避免把未被材质引用的 UI/图标噪声默认塞进 3D Library。
- `Textures/Texture2DArray`：解析到的可视 `Texture2DArray`，作为独立 terrain/material/shader 贴图库资源保存。
- `Textures/DataTexture2DArray`：float/HDR/未知语义的数组贴图，常见于 shader、terrain 或运行时数据采样。默认只导出 `.texture2darray.json` 和 catalog 记录，不逐层写 PNG；如果将来显式开启诊断预览，PNG 看起来像雪花也不一定表示解码错误。

AnimeStudio 参考 AssetStudio 的方式，把 `Texture2DArray` 的每一层拆成导出阶段的 fake `Texture2DArrayImage`，再复用现有贴图解码器导出 PNG。数组贴图目录会包含：

- 可视数组会有 `xxx_001.png`、`xxx_002.png` 等每层图片；数据数组默认不写 PNG layer。
- `xxx.texture2darray.json`，记录 width、height、depth、GraphicsFormat、TextureFormat、源文件、PathID、stream offset/size 和每层导出状态。
- `asset_catalog.jsonl` 中的 `Texture2DArray` / `DataTexture2DArray` 条目，方便后续材质报告或素材库 UI 反查。
- 根目录 `TEXTURE_LIBRARY.md` 和 `texture_library.json` 会说明这些贴图来自哪些 Unity 材质槽或为什么作为独立数组贴图库导出。

如果只想单独抽数组贴图库，仍可使用显式命令：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "D:\BaiduNetdiskDownload\unity-VRising\VRising_Data" `
  "D:\Assets\VRising_Texture2DArray" `
  --game Normal `
  --mode Export `
  --types Texture2DArray:Both AssetBundle:Parse ResourceManager:Parse `
  --group_assets ByLibrary `
  --export_type Convert
```

如果某个模型材质引用了数组贴图，`MATERIAL_REPORT.md` 应记录这个 Unity 贴图槽和引用关系；但除非后续有明确 shader/customization 管线能还原采样方式，否则它仍作为独立贴图库资源保存。

### AudioClip / 音效素材库

短音效属于开发素材，例如脚步声、命中、技能、按钮、篮球入网、环境点缀等。但它不属于默认 3D Library，不能和模型、贴图、骨骼、动画混在同一个浏览目录里。

默认 `--mode Library` 会关闭 `AudioClip`、`VideoClip`、`MovieTexture`。需要音效时使用独立音频素材库：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "D:\BaiduNetdiskDownload\unity-VRising\VRising_Data" `
  "D:\Assets\VRising_AudioLibrary" `
  --game Normal `
  --mode AudioLibrary `
  --group_assets ByLibrary `
  --export_type Convert
```

输出结构：

- `Audio/SFX/`：短音效，优先用于游戏开发复用。
- `Audio/Music/`：BGM、主题音乐、较长循环音乐。
- `Audio/Voice/`：对白、语音、角色台词。
- `Audio/Other/`：无法可靠判断的音频。
- `AUDIO_LIBRARY_README.md`：人工入口，解释分类和统计。
- `*.audio.json`：每个音频旁边的元数据，记录长度、声道、采样率、压缩格式、Unity 路径、分类等。
- `asset_catalog.jsonl`：机器索引，包含 `kind=Audio` 和 `audioKind`。

分类规则是通用启发式：优先看 Unity container/source/name 中的 `voice/dialog/bgm/music/sfx/sound/effect` 等语义，再用音频时长兜底。短于约 15 秒的未知音频默认归 `SFX`，长于约 45 秒的未知音频默认归 `Music`。这只是素材库浏览分类，不会修改原始 Unity 资源。

如果本机 FMOD/native 解码器不可用，CLI 会降级导出原始音频数据，例如 `.fsb`，并在 `.audio.json` 中标记 `convertedToWav=false`。这比整批失败更适合作为素材库归档；后续可以用专门音频工具批量转码。

如果只想做底层显式提取，也可以用原始类型开关：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "D:\Game\Game_Data" `
  "D:\Assets\Game_AudioRaw" `
  --game Normal `
  --mode Export `
  --types AudioClip:Both `
  --group_assets ByLibrary `
  --export_type Convert
```

`VideoClip` / `MovieTexture` 仍然不进入默认素材库；如果确实要提取视频，需要显式用 `--mode Export --types VideoClip:Both`。

### SQLite 素材库索引

当已经有一个 Library 或 AudioLibrary 导出目录时，可以把机器索引、Unity 关系、报告 JSON 和实际文件列表合并进 SQLite，供后续浏览、查询、调试、批量 bake、材质补全或二次打包使用：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --build_sqlite_index "D:\Assets\Freedunk_Data_Dev\CrossGame_VRising_ColorMaskTint_V1"
```

本仓库调试版也可以直接用项目运行：

```powershell
dotnet run --project D:\misutime\AnimeStudio\AnimeStudio.CLI\AnimeStudio.CLI.csproj -f net9.0-windows -- `
  --build_sqlite_index "D:\Assets\AS-Assets\VRising-Assets"
```

这条命令不会重新导出模型、贴图或动画，只会重建 `library_index.db`。它适合这些情况：

- CLI 的索引逻辑更新了，但已有素材文件不需要重导。
- 已经有 `asset_catalog.jsonl` 和 `unity_source_index.db`，只想补/刷新 `model_animation_candidates`。
- Library Browser 需要看到最新的显式模型动画关系、缩略图/预览查询索引。

注意：重建前请先关闭正在打开该素材库的 Library Browser，否则 `library_index.db` 可能被占用而无法覆盖。

默认输出：

```text
library_index.db
sqlite_index_summary.json
SQLITE_INDEX_README.md
```

`sqlite_index_summary.json` 会写入 `animationRelationCoverage`，用于验收动画关系是否真正来自 Unity 确定性引用：

- `totals.explicitCandidates`：SQLite 中显式模型-动画候选数量，只统计 `relation_source=explicit`。
- `totals.animationsWithExplicitCandidates` / `totals.animationExplicitCoverage`：有显式模型关系的动画资产数量和比例。
- `totals.modelsWithExplicitCandidates` / `totals.allModelExplicitCoverage`：有显式动画候选的模型数量和比例；这个分母包含大量静态模型、场景件和暂不处理的 VFX/特效网格，不能单独当成动画关系失败率。
- `modelResourceKinds`：按模型 `resourceKind` 拆分显式候选覆盖，便于把 Stage、Prop、Unknown、Avatar 等分开看。
- `explicitCandidatesPerLinkedModel`：已有关联模型的候选数量分布，用于发现某些模型候选异常过多或过少。
- `candidateNextActions` / `candidateProcessingFlags`：显式候选下一步处理路径。Transform/BlendShape 等直接动画默认进入 `generate_preview_gltf`；Humanoid/Muscle 生产路径进入 `generate_unity_baked_gltf`，表示先用 Unity 采样，再由 AnimeStudio 写回 glTF/GLB。`requiresInternalHumanoidSolve` / `internalHumanoidPreviewCandidate` 只保留给内部公式实验验证。
- `linkedAnimationTypes` / `linkedAnimationSignals`：已经被显式关系引用到的动画资产类型与 binding 信号，例如 Humanoid Muscle、Transform、BlendShape、辅助节点等。它用于拆分“关系已建立但直接 glTF 能力还没补完”的缺口，不等于视觉验收通过。
- `sourceIndexAnimationRelationHealth`：检查 `unity_source_index.db` 是否包含当前工具需要的 Animator/Animation/OverrideController 关系。若素材库里存在 `AnimatorOverrideController`，但源索引没有 `animatorOverrideController.overrideSet` / `clipPair` 精确标记，说明源索引是旧版本或不完整版本；Library 重建会保守跳过这类不完整 OverrideController 的 base controller 粗扩散，应先重建 `unity_source_index.db`，再重建 `library_index.db` 来恢复精确 override 候选。`overrideSet(count=0)` 表示 Unity 明确给出了空替换表，可以确定性继承 base controller 动画。
- `linkedAnimationSidecarCapabilities`：只扫描已经被显式关系引用到的动画 sidecar，用于判断 decoded、direct glTF、Humanoid 内部 solver 输入覆盖。它是输入能力统计，不等于视觉验收通过。

重导或重建 SQLite 后，可以用轻量诊断脚本快速回答“默认模型-动画候选有没有混入猜测关系”：

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass `
  -File scripts\Measure-DeterministicAnimationCoverage.ps1 `
  -LibraryPath "D:\Assets\AS-Assets\YuanShen-Assets" `
  -OutputDir "D:\Assets\AS-Assets\YuanShen-Assets\AnimationRelationDiagnostics"
```

自动验收或批处理里可以加 `-FailOnWarning`。这会在默认候选表混入非显式关系，或源索引动画关系健康检查出现 warning 时返回非零退出码；报告仍会写出，方便直接定位原因：

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass `
  -File scripts\Measure-DeterministicAnimationCoverage.ps1 `
  -LibraryPath "D:\Assets\AS-Assets\YuanShen-Assets" `
  -OutputDir "D:\Assets\AS-Assets\YuanShen-Assets\AnimationRelationDiagnostics" `
  -FailOnWarning
```

原神这类大库如果只想在重建后快速拦截“默认候选表混入猜测关系”，加 `-GateOnly`。它只跑候选表和源索引健康门禁，跳过 Unity bake cache、Avatar blocker 和分类覆盖大统计；需要完整覆盖率报告时再去掉这个参数：

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass `
  -File scripts\Measure-DeterministicAnimationCoverage.ps1 `
  -LibraryPath "D:\Assets\AS-Assets\YuanShen-Assets" `
  -OutputDir "D:\Assets\AS-Assets\YuanShen-Assets\AnimationRelationDiagnostics_Gate" `
  -GateOnly `
  -FailOnWarning
```

如果要看大库的 Unity bake 生产覆盖，但不想跑最重的 top 模型/动画/Avatar blocker 明细，可以加 `-SummaryOnly`。原神这类依赖导入原始 `UnityEngine.Avatar` asset 的库，还必须传入 bake 工程 `-UnityProject`，脚本才会扫描 `Assets/AnimeStudioBake/ImportedAvatar/*.asset`，并按和 CLI bake 一致的精确 Avatar 名/模型名 key 统计 `importedAvatarAssetBakeReadyExplicitUnityBakeCandidates`。如果素材库根目录存在 fresh 的 `ImportedAvatarProbe*/imported_avatar_probe_batch.json`，`SummaryOnly` 会和 Browser/CLI bake 一样只把 `isValid && isHuman` 的 asset 写入 readiness key 表，并在报告里标出 `probeEnforced=true`；验证失败、过期或数量不匹配的 probe 不会被强行当成通过。没有传 `-UnityProject` 时，脚本不会猜测 Avatar oracle，ImportedAvatar ready 覆盖会保守显示为不可测或 0。大库统计会把显式候选的 Humanoid/Direct glTF 标记和模型级 Avatar oracle 先物化成临时表复用；这只是性能优化，不新增模型-动画关系，也不改变 `relation_source=explicit` 门禁：

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass `
  -File scripts\Measure-DeterministicAnimationCoverage.ps1 `
  -LibraryPath "D:\Assets\AS-Assets\YuanShen-Assets" `
  -UnityProject "D:\misutime\AnimeStudioUnityProject" `
  -OutputDir "D:\Assets\AS-Assets\YuanShen-Assets\AnimationRelationDiagnostics_Summary" `
  -SummaryOnly
```

如果只是阶段性确认“根目录 bake cache 摘要还在、bake 工程里 ImportedAvatar asset 能被看到、最近批次报告在哪里”，可以用 `-FastSummary`。这个模式不扫描大型 SQLite 候选表，适合原神这类 10GB 级 `library_index.db` 的快速验收入口；它不会替代 `-GateOnly` / `-SummaryOnly` / 完整模式的确定性关系门禁：

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass `
  -File scripts\Measure-DeterministicAnimationCoverage.ps1 `
  -LibraryPath "D:\Assets\AS-Assets\YuanShen-Assets" `
  -UnityProject "D:\misutime\AnimeStudioUnityProject" `
  -OutputDir "D:\Assets\AS-Assets\YuanShen-Assets\AnimationRelationDiagnostics_Fast" `
  -FastSummary
```

`-FastSummary` 会优先读取根目录 `animation_bake_cache_summary.json`、`sqlite_index_summary.json`、`Assets/AnimeStudioBake/ImportedAvatar/*.asset`、最近的 `ImportedAvatarProbe*/imported_avatar_probe_batch.json` 和 `.as_browser_cache/unity_bake_batch_reports/*.json`。旧库里的 summary JSON 如果因为历史编码问题读坏，脚本会把读取错误写进报告，而不是中断整个快速验收。维护这个 PowerShell 脚本时，新增在 PowerShell 主体里的字符串应保持 ASCII；中文说明优先写到 Markdown 文档或 Python 生成内容里，避免 Windows PowerShell 5 按 ANSI 解析 UTF-8 无 BOM 文件时误报语法错误。

如果读取到 ImportedAvatar probe，FastSummary 会写出 `importedAvatarProbeFreshness`：`fresh` 表示 probe 的 asset 数量与当前 `ImportedAvatar` 目录一致，且报告不早于目录内最新 `.asset`；`stale` / `mismatch` 表示恢复或替换过 Avatar asset 后还没重新验证，Browser 会提示重新运行“验证AvatarAsset”。这个 freshness 只保护 Avatar oracle 验证报告的新鲜度，不会新增或修改模型-动画关系。

非 `-FastSummary` 的深度模式主要读取 `library_index.db` 和 `unity_source_index.db`，输出 `deterministic_animation_coverage.json` 与 `DETERMINISTIC_ANIMATION_COVERAGE.md`。验收时优先看：

- `gate.status`：默认应为 `ok`。
- `totals.nonExplicitCandidates`：必须为 `0`，否则说明默认候选表混入了非 Unity 显式关系，需要追查是否退回骨骼数量、名称或路径 fallback。
- `candidateTableSchema.status`：默认应为 `ok`。它检查 `model_animation_candidates.relation_source` 是否同时有 `NOT NULL` 和 `CHECK(relation_source='explicit')` 约束；如果旧库数据已经全是 explicit、但 schema 还没约束住，也要重建 `library_index.db`，不能让后续流程重新写入猜测关系。
- `totals.animatedCategoryExplicitCoverage`：按 `NPC`、`Avatar`、`Monster`、`Animal`、`Partner`、`Vehicle` 这类明显动画模型目录粗看显式覆盖；它比全库模型覆盖更接近角色/怪物/动物动画绑定进度。
- `sourceIndexAnimationRelationHealth.staleOverridePairIndex`：为 `true` 时说明源索引是旧版或不完整版本，应先重建 `unity_source_index.db`，再重建 `library_index.db`。这不是让工具回退猜测关系的理由。

如果命令没有显式传 `-SourceIndex`，脚本会先读取 `sqlite_index_summary.json` 中 `animationRelationCoverage.sourceIndexAnimationRelationHealth.sourceIndex`，也就是当前 `library_index.db` 实际重建时使用的源索引；找不到时才回退到素材库根目录的 `unity_source_index.db`。这能避免 VRising 这类“正式 library_index.db 由旁路 fresh source index 重建”的库，在 Browser 手动刷新 GateOnly 时误读旧根目录源索引。

这份报告只回答“默认模型-动画候选是不是来自 Unity 确定性关系”。它不证明 Humanoid/Muscle 姿态已经能由 AnimeStudio 内部公式正确转成 glTF TRS；这类动画还必须继续看 preview/bake 验证报告。若项目阶段允许把 Unity bake 作为生产桥接路径，bake 结果仍应写回 glTF/GLB 并保留 relation_source、bakeMode 和验证状态，不能把 bake 成功当作新增或猜测模型-动画关系。

`library_index.db` 还会生成两张候选统计表，供浏览器、覆盖度看板和批量验证直接查询，避免每次从几百万条候选明细重新 `GROUP BY`：

```text
model_animation_candidate_model_summary
model_animation_candidate_animation_summary
```

这两张表只汇总 `relation_source=explicit` 的确定性 Unity 关系，不新增、删除或猜测任何模型-动画绑定。候选明细仍保留在 `model_animation_candidates.raw_json`，summary 只是把常用统计结果物化出来。

临时预览或单样本导出可以传 `--skip_sqlite_index` 跳过导出结束后的 `library_index.db` 构建，避免为了一个预览包重建完整 SQLite。默认完整 Library 导出不应开启它，因为正式素材库需要 SQLite 作为浏览、筛选和后续打包底座。

大型素材库如果只是刷新模型-动画候选、sidecar 能力统计或源索引健康检查，可以给手动重建命令加 `--skip_sqlite_file_index`，跳过很大的 `files` 表：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --build_sqlite_index "D:\Assets\AS-Assets\YuanShen-Assets" `
  --skip_sqlite_file_index
```

这不会影响 `model_animation_candidates`、`animationRelationCoverage` 和 `sourceIndexAnimationRelationHealth`。需要完整文件浏览、文件存在性审计或 Library Browser 文件级搜索时，再重新运行不带 `--skip_sqlite_file_index` 的完整建库。

如果只想快速定位大库 SQLite 重建卡点，或当前只关心候选关系和源索引健康检查，可以再加 `--skip_sqlite_sidecar_scan`。它会跳过 `sqlite_index_summary.json` 里的 `linkedAnimationSidecarCapabilities` 逐文件读取，但不会跳过候选入库：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --build_sqlite_index "D:\Assets\AS-Assets\YuanShen-Assets" `
  --skip_sqlite_file_index `
  --skip_sqlite_sidecar_scan `
  --skip_sqlite_json_documents
```

使用这个参数生成的 summary 不能用于判断 decoded/direct glTF 动画覆盖率；需要这部分能力统计时，重新运行不带 `--skip_sqlite_sidecar_scan` 的 build。

`--skip_sqlite_json_documents` 只跳过把 `model_animations.json`、`library_summary.json` 等大型 JSON 原文复制到 `json_documents` 表；`assets`、`model_animation_candidates`、`animation_bindings`、`sourceIndexAnimationRelationHealth` 等结构化关系仍会正常生成。需要 SQLite 里保留大型 JSON 原文供桌面端全文读取时，再重新运行不带该参数的完整建库。

也可以显式指定数据库路径：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --build_sqlite_index "D:\Assets\Freedunk_Data_Dev\CrossGame_VRising_ColorMaskTint_V1" `
  --index_path "D:\Assets\Freedunk_Data_Dev\CrossGame_VRising_ColorMaskTint_V1\library_index.db"
```

当 `--index_path` 不是素材库根目录的默认 `library_index.db` 时，摘要会写到数据库同目录的 `<dbName>.summary.json`，说明文件写到 `<dbName>.README.md`；这样可以用旁路 DB 做大库探针，而不会覆盖正式 `sqlite_index_summary.json`。

SQLite v1 会读取：

```text
asset_catalog.jsonl
unity_source_index.db
unity_relations.jsonl
animation_bindings.jsonl
export_manifest.jsonl
model_animations.json
model_animations.compact.json
character_assemblies.json
unity_relation_summary.json
model_validation.json
*.audio.json
```

其中 `unity_source_index.db` 用于从 Unity PPtr 关系恢复显式动画绑定，例如：

```text
GameObject -> 子 GameObject -> Animator/Animation -> Controller/OverrideController -> AnimationClip
```

`files` 表会记录素材库里的实际资源文件和源索引入口；根目录下的 `library_index*.db*` 旁路库、备份库、中断 WAL/SHM 会被跳过，避免重建索引时把旧索引产物当成普通素材继续登记。`unity_source_index.db` 仍会作为源关系索引记录在 `files` 表中。

完整 `Library` 导出结束时会自动重建 `library_index.db`；`--build_sqlite_index` 是“不重导素材，只刷新索引”的手动入口。

核心原则是“索引要全，默认 Library 要尽量完整，精品筛选后处理”。进入 `library_index.db` 只是说明资源和关系可查询，不代表推荐使用或动画/材质已经视觉验收通过；进入默认 Library 也只代表它可能是可用开发素材，不代表最终精品。当前 v1 是从已有导出目录建库；后续会扩展为对完整 Unity 源目录建立 CAB/Object/PPtr 全量索引，减少重复扫描和调试等待。

### SQLite Unity 源目录索引

SQLite 源索引有三种用法，团队内统一按下面规则选择。

1. 单独构建源索引。
   适合真实游戏第一次接入、开发调试、性能分析，或准备给多次导出复用同一份索引。这个命令只建 `unity_source_index.db`，不导出模型、贴图、动画素材：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "D:\BaiduNetdiskDownload\unity-VRising\VRising_Data" `
  "D:\Assets\Freedunk_Data_Dev\SQLiteSourceIndex_VRising_Full" `
  --game Normal `
  --build_source_sqlite_index `
  --batch_files 16 `
  --profile_log source_index_profile.jsonl
```

默认输出到第二个参数指定的目录：

```text
unity_source_index.db
unity_source_index_summary.json
UNITY_SOURCE_INDEX_README.md
source_index_profile.jsonl
```

`unity_source_index_summary.json` 会写入 `animationRelationHealth`，用于判断源索引是否具备当前动画关系恢复需要的确定性引用：

- `relationCounts.animator.controller` / `animatorController.clip` / `animation.clip`：基础 Animator、Controller、legacy Animation 显式关系数量。
- `relationCounts.animatorOverrideController.overrideSet`：当前工具确认读到的 OverrideController 替换表数量。`target_count=0` 表示 Unity 对象明确是空替换表，可以确定性继承 base controller 动画；旧索引没有这个标记时不能把 base controller 动画粗扩散成精确候选。
- `relationCounts.animatorOverrideController.clipPair`：OverrideController 的 original -> override 成对关系。只看 base controller clip 会让候选偏宽，必须有 clipPair 才能精确替换。
- `staleOverridePairIndex`：如果源目录存在 `AnimatorOverrideController`，但源索引既没有 `overrideSet`，或存在非空替换表却没有 `clipPair`，会标为 `true`，说明这是旧版或不完整源索引，应该先重建源索引，再重建 Library SQLite。

也可以显式指定数据库路径，适合把索引放到共享目录或固定缓存目录：

```powershell
--index_path "D:\Assets\Freedunk_Data_Dev\SQLiteSourceIndex_VRising_Full\unity_source_index.db"
```

当 `--index_path` 不是输出目录默认 `unity_source_index.db` 时，摘要会写到同目录 `<dbName>.summary.json`，说明文件写到 `<dbName>.README.md`，避免旁路测试覆盖正式源索引摘要。

已有源索引也可以单独做健康检查，不需要重建素材库：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --verify_source_index "D:\Assets\AS-Assets\YuanShen-Assets\unity_source_index.db"
```

默认会在同目录写 `<dbName>.animation_relation_health.json`。如果报告里 `animationRelationHealth.staleOverridePairIndex=true`，说明源索引仍缺当前工具写入的 `AnimatorOverrideController.overrideSet` / `clipPair` 精确标记。旧索引即使有分离的 `originalClip` / `overrideClip`，也只能兼容粗略恢复，不能可靠表达 `original -> override` 或“空替换表继承 base controller”的确定性关系。

生产重建前建议开启严格门槛；只要源索引缺这些当前工具需要的精确关系，命令会直接失败，避免继续生成一份看似正常但少掉 OverrideController 显式动画的 `library_index.db`：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --verify_source_index "D:\Assets\AS-Assets\YuanShen-Assets\unity_source_index.db" `
  --require_fresh_source_animation_relations
```

修改 `AnimatorOverrideController` 源索引或 Library 候选展开逻辑后，先跑最小回归脚本。它会构造四个合成库：完整 `clipPair` 场景验证 base controller 含 `OriginalClip + KeepClip`、OverrideController 将 `OriginalClip -> OverrideClip` 时，最终候选只有 `OverrideClip + KeepClip` 且健康状态为 `ok`；空 `overrideSet(count=0)` 场景验证空替换表会确定性继承 base controller 的 `OriginalClip + KeepClip` 且健康状态为 `ok`；半旧 separated 场景验证只有分离的 `originalClip/overrideClip` 时可兼容恢复 `OverrideClip + KeepClip`，但健康状态仍是 `warning`；完全旧 base-only 场景验证只有 `baseController`、没有 `clipPair/original/override` 细节时，Library 不会把 base controller 的动画粗扩散成确定性候选。

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File scripts\Test-OverrideClipPairRegression.ps1
```

VRising 这类旧库如果报告 `staleOverridePairIndex=true`，先旁路重建源索引，不要覆盖正式素材库里的旧 `unity_source_index.db`。仓库里提供了一个 VRising 专用脚本，默认读取 `D:\BaiduNetdiskDownload\unity-VRising`，输出到 `F:\Unity-AS-Assets\VRising-Assets\SourceIndexRebuild_OverridePair_20260614`，并在结束后自动执行严格健康检查：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Rebuild-VRisingSourceIndex-OverridePair.ps1
```

旁路源索引通过 `--require_fresh_source_animation_relations` 后，再显式传给 Library SQLite 重建。这样可以验证新的 OverrideController `overrideSet/clipPair` 关系是否恢复候选覆盖，同时保留原素材库和旧索引用于对照：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --build_sqlite_index "F:\Unity-AS-Assets\VRising-Assets" `
  --source_index "F:\Unity-AS-Assets\VRising-Assets\SourceIndexRebuild_OverridePair_20260614\unity_source_index.db" `
  --skip_sqlite_file_index `
  --require_fresh_source_animation_relations
```

`unity_source_index.db` 和 `library_index.db` 的定位不同：

- `unity_source_index.db`：面向完整 Unity 源目录，记录源文件、SerializedFile、Object、external CAB/PPtr、Unity 组件关系、AnimationClip binding。不导出素材。
- `library_index.db`：面向已经导出的 Library/AudioLibrary 目录，记录可浏览素材、导出文件、报告、侧车 JSON 和已生成的关系索引。

2. 直接导出素材，自动构建源索引。
   这是默认推荐的最简单用法。CLI 会先查找 `输出目录\unity_source_index.db`，再查找 `输入目录\unity_source_index.db`。如果都不存在，会自动在输出目录构建一份，然后继续导出 Library 素材：

```powershell
D:\misutime\AnimeStudio\dist\net9.0-windows\AnimeStudio.CLI.exe `
  "D:\BaiduNetdiskDownload\unity-VRising\VRising_Data" `
  "D:\Assets\VRising_AnimeStudioExport_Library" `
  --game Normal `
  --require_fresh_source_animation_relations
```

这种方式会让同一个输出目录同时包含源索引和导出的素材库。第一次运行会先花时间建索引；后续同目录再次导出会直接复用已有 `unity_source_index.db`。生产全量导出建议保留 `--require_fresh_source_animation_relations`：如果复用到旧 source index，且缺 `AnimatorOverrideController.overrideSet` / `clipPair` 这类当前工具需要的精确关系，导出会在加载源索引前失败，避免生成不完整的确定性动画关系。

3. 显式使用已有源索引。
   如果已经单独构建过索引，或者想让多个导出目录复用同一份源索引，使用 `--source_index` 指定路径。指定后 CLI 会直接读取这份索引，不会重新生成：

```powershell
D:\misutime\AnimeStudio\dist\net9.0-windows\AnimeStudio.CLI.exe `
  "D:\BaiduNetdiskDownload\unity-VRising\VRising_Data" `
  "D:\Assets\VRising_AnimeStudioExport_Library" `
  --game Normal `
  --source_index "D:\Assets\VRising_AnimeStudioExport_Full\unity_source_index.db" `
  --require_fresh_source_animation_relations
```

CLI 会读取 SQLite 的 `serialized_files` / `source_externals`，把它作为运行时 Unity 外部引用解析底座，并跳过旧 CAB map 的自动构建。旧 CAB map / AssetMap 只保留给显式 `--map_op` 调试或兼容旧流程，不再作为默认全量导出路径。导出目录会写入：

```text
source_index_usage.json
```

用于记录本次导出使用了哪个源索引、索引规模、关系数量和 animation binding 数量。

源索引主要表：

```text
source_files
serialized_files
source_objects
source_externals
source_relations
source_animation_bindings
```

`--batch_files` 控制每批加载多少源文件。批次越大，跨文件对象解析机会越多，但内存占用更高；批次越小，内存更稳。源索引会直接记录 PPtr 的 `fileID/pathID/external fileName`，因此即使目标对象不在当前批次，也不会丢掉 Unity 引用关系。

源索引用 `--profile_log` 记录耗时和内存。全量游戏源索引较慢时，优先看这些 stage：

```text
source_index_scan_files
source_index_init_db
source_index_insert_source_files
source_index_load_batch
source_index_write_batch
source_index_clear_batch
source_index_create_sql_indexes
source_index_write_summary
source_index_total
```

其中 `source_index_load_batch` 代表 Unity 文件加载/解析耗时，`source_index_write_batch` 代表对象、PPtr 关系、动画 binding 写入 SQLite 的耗时，`source_index_create_sql_indexes` 代表最后创建 SQL 查询索引的耗时。源索引只建关系数据库，不导出模型和贴图，因此不会出现 PNG 转换耗时；PNG、raw texture、模型写出等瓶颈要在后续实际 `Library` 导出时看 `model_texture`、`model_texture_raw`、`model_write` 等 profile stage。

### 模型和动画规则

一个模型可能支持很多动画，尤其是角色资源：私有表情动画、角色展示动画、通用身体动作、职业/性别动作、Animator Controller 引用动作、外部公共动作库等。默认不能把这些动画全部嵌进每个模型，否则会产生巨大、重复、难浏览的 glTF。

团队后续统一采用这套规则：

1. 模型文件保持干净。
   默认 `Library` 输出的 glTF 只包含模型、材质、贴图、骨骼、skin，以及模型自身必要结构，不默认嵌入全局动作库。
2. 动画独立入库。
   AnimationClip 默认进入 `Animations`，作为可复用资产保存。
3. 自动生成 Unity 关系图和绑定索引。
   `unity_relations.jsonl` 记录 Unity 原生关系，`unity_relation_summary.json` 给出轻量摘要；`animation_bindings.jsonl` 和 `model_animations.json` 负责说明“哪些动画可能适配哪些模型”。
4. 按需生成预览 glTF。
   看到某个模型后，再选择某个动画生成临时可播放 glTF，用于查看动作效果。
5. 可选打包动画合集。
   确认一组动画属于某个模型后，再显式生成带动画合集的 glTF/GLB。
6. 模块化角色保持模块化。
   换脸、换发、配件、披风、武器等模块默认不强行合并进主体模型；Library 会记录可组合关系，浏览/验证时再生成 assembled preview。

推荐工作流：

```text
默认 Library 导出
  -> 干净 Models + 独立 Animations
  -> 生成绑定索引
  -> 选中模型和动画
  -> 生成预览 glTF
  -> 可选生成模块化角色组装预览
  -> 可选打包动画合集
```

### 模块化角色组装规则

很多 Unity 游戏不会把“完整角色”保存成一个单一 prefab。常见结构是身体/衣服作为主体，脸、头发、面具、配件、武器等作为独立 prefab 或换装模块。素材库默认必须保留这种模块化结构：

- 默认 `Library`：不强行合并，`Models` 里保留 body、face、hair、accessory 等原始模块。
- 自动索引：生成 `character_assemblies.json`，记录哪些 body 可以搭配哪些 face/hair/accessory。
- 关系依据：优先使用 Unity 导出的节点/骨骼关系；模块的 skin/bone joints 必须能按同名 Unity joint 映射到主体骨架，才允许自动组装预览。
- 浏览预览：用 `--generate_assembled_preview_gltf` 生成临时完整角色 glTF，方便快速查看。
- 成品导出：后续可以基于同一索引选择 body + face + hair + accessory + animation，生成用户指定组合的 assembled glTF/GLB。

这条规则避免两类问题：一是把某个默认脸/头发误认为唯一正确版本；二是把无法确定 attachment 的模块硬塞进角色，导致素材库不准确。宁可把模块标为候选，也不要默认生成看似完整但关系不可靠的角色。

生成组装预览示例：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --generate_assembled_preview_gltf "D:\Assets\Freedunk_Data_Dev\CrossGame_VRising_CustomizationExport_V2\model_animations.json" `
  --game Normal `
  --preview_model "HYB_VampireFemale_CustomizationScreen_Prefab" `
  --preview_animation "^VampireFemale_1H_Idle$" `
  --preview_output "D:\Assets\Freedunk_Data_Dev\CrossGame_VRising_AssembledPreview_V3" `
  --assembly_modules "Face,Hair,Accessory"
```

输出仍然是临时预览，不改变默认素材库：

```text
CrossGame_VRising_AssembledPreview_V3\
  preview_validation.json
  Models\...\HYB_VampireFemale_CustomizationScreen_Prefab.gltf
  Models\...\assembly_report.json
```

`assembly_report.json` 会记录每个模块的角色、来源 glTF、是否可组装、缺失 joint 数、实际新增 mesh node 数、最终 glTF 的 mesh/skin/animation/channel 检查结果。只有 `MissingJointCount = 0` 且 `invalidChannels = 0`、`invalidSkinJoints = 0` 的模块化预览，才适合作为可视验证结果。

### 按需生成动画预览

`model_animations.json` 里的候选关系需要经过实际 glTF 写入验证。选中一个模型和一个候选动画后，用 `--generate_preview_gltf` 生成临时可播放 glTF：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --generate_preview_gltf "D:\Assets\Freedunk_Data_Dev\LibraryIndexSample\model_animations.json" `
  --game Normal `
  --preview_model "^Bill_01_00_ingame$" `
  --preview_animation "^NORMALMOVE_STAND_01$" `
  --preview_output "D:\Assets\Freedunk_Data_Dev\Preview_Bill_NormalMove"
```

这个命令会优先走 FastPreview：复制已导出的模型 glTF，读取动画旁 `*.animation_asset.json` 的 `decoded` 曲线，直接写入 glTF animation。只有当 sidecar 缺少完整 decoded keyframes，例如 `decoded.status=compacted`，才会回退到定向重导。

回退时仍然使用完整 `unity_source_index.db` 作为 CAB/PPtr 依赖底座，但 CLI 会把选中模型和动画的源文件以显式 `--source_files` 传给子导出，只加载目标源文件和依赖闭包；同时把模型和动画的 Unity `PathID` 以 `--path_ids` 传给子导出，只导出这两个确定对象，避免为了单个预览遍历依赖闭包里的大量同名或同源候选。`--source_files` / `--path_ids` 只能用于这种定向刷新/诊断；默认全量 Library 导出不要设置它们。

输出包括：

```text
preview_validation.json
Models\...\<model>.gltf
```

`preview_validation.json` 会记录 animation channel 数、无效 channel 数、skin/joint 数、主体骨骼覆盖率、raw bbox 和 skinned bbox。只有当动画写入成功、channel 指向有效节点、skin 存在、命中主体骨骼且 skinned bbox 没有明显异常时，预览状态才会是 `ok`。如果只命中 `Ball_Point`、socket、twist/helper 这类辅助节点，报告会标为 `warning`。

BlendShape/表情动画使用 glTF morph target 路径，不按 Humanoid 身体骨骼规则验收。Unity 的 `SkinnedMeshRenderer` 浮点曲线也可能是材质、Renderer 属性或显隐动画，不能只看历史字段 `blendShapeBindingCount` 就判定为 morph。预览报告里的 `morph` 会记录：

- `expectedBindingCount`：Unity AnimationClip 中确认是 BlendShape 的 binding 数，对应 `trueBlendShapeBindingCount`。
- `ambiguousBindingCount`：历史粗字段里的 SkinnedMeshRenderer 浮点 binding 数，只能说明需要继续分类，不能直接等同于 glTF morph weights。
- `targetCount`：glTF mesh primitive 写出的 morph target 数。
- `weightChannelCount`：glTF animation 中写出的 `weights` channel 数。
- `targetNames`：导出的 morph target 名称。

如果 `targetCount > 0`、`weightChannelCount > 0` 且 `invalidChannels = 0`，说明表情/形变动画结构已经写入 glTF。注意：Unity 一个 BlendShape channel 如果有多帧形态，当前 glTF 主线先取最后一帧作为 morph target；权重曲线仍按 AnimationClip 播放。后续如遇到真正依赖多帧形态插值的模型，再扩展为“一条 Unity channel 多个 glTF target”的高级模式。

按需预览和动画打包里的 FastPreview 会直接从 `animation_asset.json decoded.floats` 读取确认的 BlendShape 曲线，并按已导出 glTF mesh 的 `extras.targetNames` 写入 `weights` channel。材质、显隐和 Renderer 属性曲线不会进入这个 morph 路径。

### Unity bake 与原始 Avatar asset

Humanoid/Muscle 动画的生产主线是 Unity bake。普通 Unity 项目优先使用原始 prefab / Animator 上的 `Animator.avatar`，或完整 `HumanDescription.humanBones + skeletonBones` 复建 Avatar。原神这类库如果只有 `AvatarConstant/internalSolver`，必须先从打包 Unity 对象恢复原始 `UnityEngine.Avatar` asset，导入 bake 工程，再通过显式参数传入；这仍然是 Unity 确定性数据，不是按骨骼数量或名称猜测。

单次 CLI 预览可传：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --bake_animation_previews_from_library "D:\Assets\AS-Assets\YuanShen-Assets" `
  --preview_model "^NPC_Male_Standard_Model$" `
  --pack_animations "^Ani_NPC_Male_Idle01$" `
  --preview_validation_output "D:\Assets\AS-Assets\YuanShen-Assets\UnityBakePreview_NPCMaleIdle" `
  --unity_project "D:\misutime\AnimeStudioUnityProject" `
  --unity_editor "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe" `
  --unity_avatar_asset "Assets/AnimeStudioBake/ImportedAvatar/NPC_Male_Common_ModelAvatar.asset" `
  --run_unity_bake
```

`--unity_avatar_asset` 必须指向 bake 工程内的 `Assets/.../*.asset` 路径。AnimeStudio 会把它写入 request 的 `unityAssetPaths.avatarAsset`，Unity helper 会直接 `AssetDatabase.LoadAssetAtPath<Avatar>`；加载到有效 human Avatar 时，结果报告才允许标记 `avatarTrust.source=imported_unity_avatar_asset` 和 `trustedProductionBake=true`。一旦 request 显式带了 `unityAssetPaths.avatarAsset`，Unity helper 加载失败会直接让 bake 失败，不能静默退回 `BuildHumanAvatar`；`unity_bake_result.json` 会写出 `requestedAvatarAsset`、`importedAvatarAsset` 和 `importedAvatarAssetValid`，`unity_bake_apply_report.json` 会同步写出 `unityBakeRequestedAvatarAsset`、`unityBakeImportedAvatarAsset` 和 `unityBakeImportedAvatarAssetValid`。apply、Browser、SQLite 摘要和覆盖率脚本读取阶段也只接受这个导入 Avatar asset 证明可信；旧报告没有 `unityBakeImportedAvatarAssetValid` 时，必须至少同时看到 `unityBakeRigRestPoseSource=imported_unity_avatar_asset` 和 `unityBakeRigRestPoseApplied=true`，不能只凭 `avatarTrust.source` 放行。即使 request 里同时存在 `HumanDescription.skeletonBones`、prefab 或 AvatarConstant/internalSolver 诊断数据，也不能在 Avatar asset 无效或旧报告来源不匹配时把结果标成可信。这个参数只能用于明确 `--preview_model` 的单模型定向预览/诊断，不能在未指定模型或命中多个模型时把同一个 Avatar 批量套用到不同模型。批量生产 bake 应把恢复出的 Avatar 放入 `Assets/AnimeStudioBake/ImportedAvatar` 或写入 `unityAvatarAssets` 映射，让工具按模型 Avatar 名/模型名精确匹配。`unity_bake_batch_report.json` 会在顶层记录 `avatarAssetCounts`、`avatarSourceCounts`、`avatarMatchKeyCounts`，并在 `items[*]` 里记录 `unityAvatarAsset`、`avatarSource` 和 `avatarMatchKey`，方便人工直接审查每条 request 是否用了正确的 Avatar oracle，以及它是通过哪个模型 key 命中的。如果没有这个参数，流程回到普通 Unity 项目的 prefab/HumanDescription 路径；`AvatarConstant/internalSolver` 只保留诊断作用，不能单独算可信生产动画。

Library Browser 也支持同一条路径。素材库根目录可写本地配置 `.as_browser_cache\unity_bake_settings.json`，本地配置会覆盖全局配置：

```json
{
  "unityProject": "D:\\misutime\\AnimeStudioUnityProject",
  "unityEditor": "C:\\Program Files\\Unity\\Hub\\Editor\\6000.4.11f1\\Editor\\Unity.exe",
  "unityAvatarAssets": {
    "NPC_Male_Standard_Model": "Assets/AnimeStudioBake/ImportedAvatar/NPC_Male_Common_ModelAvatar.asset",
    "Avatar_Boy_Bow_Gorou": "Assets/AnimeStudioBake/ImportedAvatar/Avatar_Boy_Bow_Gorou_ModelAvatar.asset"
  }
}
```

`unityAvatarAssets` 是显式模型名到 Avatar asset 的映射。没有命中的模型不会自动套用这些 Avatar，因此 VRising、Freedunk 等普通 Unity 库仍走默认 `Animator.avatar` / HumanDescription 路径。

映射 key 可以写模型名，也可以写模型元数据里的 `avatar.name`。推荐对原神这类共用 Avatar 的 NPC 写 Avatar 名，例如 `NPC_Male_Common_ModelAvatar`；Browser 会从 `library_index.db assets.raw_json.avatar.name` 读取该模型真实 Avatar 名后再查映射，这样一个配置可以覆盖所有引用同一个 Unity Avatar 的模型。这个关系来自导出的 Unity Avatar 元数据，不是按目录或角色名前缀推断。

要继续补齐原神这类库的 Avatar oracle，可以先生成恢复优先级计划。计划只读取 `assets.raw_json.avatar.name/source/pathId` 和 `model_animation_candidate_model_summary`，不会新增模型-动画关系，也不会按名称猜测绑定：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Write-UnityAvatarAssetRecoveryPlan.ps1 `
  -LibraryPath "D:\Assets\AS-Assets\YuanShen-Assets" `
  -UnityProject "D:\misutime\AnimeStudioUnityProject" `
  -OutputDir "D:\Assets\AS-Assets\YuanShen-Assets\UnityAvatarAssetRecoveryPlan" `
  -OnlyMissing
```

`-OnlyMissing` 会扫描 `Assets/AnimeStudioBake/ImportedAvatar/*.asset`，把已经精确命中的 Avatar 标成 recovered 并从输出列表中过滤掉，只列出剩余待恢复项。如果素材库根目录下有 fresh 的 `ImportedAvatarProbe*/imported_avatar_probe_batch.json`，计划会强制只把 Unity 实测 `avatar.isValid && avatar.isHuman` 的 Avatar asset 算作 recovered；无效或未出现在 probe 中的 `.asset` 不会被当成可信 oracle。CSV/Markdown 里会保留 `source/pathId`、建议的 `Assets/AnimeStudioBake/ImportedAvatar/<AvatarName>.asset` 路径、样例模型、`avatarAssetProbeStatus`，以及 `cumulativeMissingInternalHumanoidCoveragePercent` 这类累计收益字段；优先恢复表头几项，就能直接看到预计补齐多少仍缺 Avatar oracle 的 Humanoid/internal 候选。恢复出的 `.asset` 放进 bake 工程后，再运行 Browser 的“验证AvatarAsset”或 `Test-UnityImportedAvatarAssets.ps1`，随后用 Browser 的“快速摘要”或 `Measure-DeterministicAnimationCoverage.ps1 -FastSummary` 检查 ImportedAvatar 数量和有效 Avatar oracle 覆盖是否上升。

如果要确认恢复出的 `.asset` 不只是文件存在，而是 Unity 真的能加载成有效 Humanoid Avatar，可以先运行批量探针。它只调用 bake 工程里的 `AnimeStudioImportedAvatarProbe`，不会新增模型-动画关系，也不会烘焙动画：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Test-UnityImportedAvatarAssets.ps1 `
  -UnityProject "D:\misutime\AnimeStudioUnityProject" `
  -UnityEditor "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe" `
  -OutputDir "D:\Assets\AS-Assets\YuanShen-Assets\ImportedAvatarProbe"
```

### 独立动画资产

默认 Library 导出会把动画作为独立资产保存，而不是直接塞进模型：

```text
Animations\...\NORMALMOVE_STAND_01.anim
Animations\...\NORMALMOVE_STAND_01.animation_asset.json
```

`.anim` 是从 Unity `AnimationClip` 转出的 YAML 资产，保留原始曲线数据。`.animation_asset.json` 是给工具链和团队阅读用的结构化 sidecar，会记录：

- AnimationClip 的 source、container、pathId、sampleRate、duration、eventCount。
- `animationType`、Transform/Humanoid/BlendShape/辅助节点 binding 数量。
- 每条 `genericBinding` 的 Unity path hash、解析后的 path、typeID、customType、attribute 和可读 `attributeName`。
- Humanoid/Muscle 的 `RootT.y`、`LeftFootQ.x`、`Spine Front-Back` 等 Unity muscle 语义。
- `m_MuscleClip` 的 start/stop time、root motion、foot start、averageSpeed、loop/root-motion flags。
- `decoded` 曲线块：从 Unity ACL/stream/dense/constant 数据解码出的 `translations`、`rotations`、`scales`、`eulers`、`floats`、`pptrs`。Transform 曲线保存 path + keyframes，Humanoid muscle 保存 attribute + keyframes，数值仍是 Unity serialized 空间。
- `decoded.playbackKind` / `decoded.directGltfReady` / `decoded.requiresInternalHumanoidSolve`。Transform TRS 曲线可以进入直接 glTF 预览；Humanoid/Muscle 的默认生产处理是 Unity bake -> glTF，`requiresInternalHumanoidSolve` 只保留给内部公式实验和对照验证，不能伪装成已经可直接播放。

默认全量 Library 为了性能会跳过完整 decoded keyframes，只保留分类、binding 和曲线数量。做单模型/单动画诊断、FastPreview 或内部 solver 验证时，可以显式开启：

```powershell
--export_full_decoded_animation_curves
```

如果只需要刷新少数动画的完整 decoded sidecar，应配合完整输入 root、完整 `unity_source_index.db` 和显式源文件过滤，而不是复制少量 bundle 当输入：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files\miHoYo Launcher\games\Genshin Impact Game\YuanShen_Data\StreamingAssets\AssetBundles\blocks" `
  "D:\Assets\AS-Assets\YuanShen-Assets\AnimationDecodedRefresh\Ani_NPC_Male_Idle01" `
  --game GI `
  --mode Library `
  --group_assets ByLibrary `
  --model_format Gltf `
  --animation_package Separate `
  --export_full_decoded_animation_curves `
  --skip_sqlite_index `
  --source_index "D:\Assets\AS-Assets\YuanShen-Assets\unity_source_index.db" `
  --names "^Ani_NPC_Male_Idle01$" `
  --source_files "00/07429156.blk" `
  --path_ids -4471942187353965215
```

这条命令里的关系和依赖仍来自完整 Unity 源索引；`--source_files` 只是显式缩小本次加载范围，`--path_ids` 只是定位 Unity 对象本身。它们都不参与模型-动画匹配，也不能替代全量源索引。

如果这次定点导出只是为了刷新旧 Library 里缺失或压缩过的 sidecar，不要手工改 JSON。先用脚本把目标字段合并回正式素材库，再重建或替换 `library_index.db`：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Apply-AnimationDecodedRefresh.ps1 `
  -LibraryRoot "D:\Assets\AS-Assets\YuanShen-Assets" `
  -RefreshRoot "D:\Assets\AS-Assets\YuanShen-Assets\AnimationDecodedRefresh\Ani_NPC_Male_Idle01" `
  -AnimationName "Ani_NPC_Male_Idle01"

powershell -ExecutionPolicy Bypass -File scripts\Apply-ModelAvatarRefresh.ps1 `
  -LibraryRoot "D:\Assets\AS-Assets\YuanShen-Assets" `
  -RefreshRoot "D:\Assets\AS-Assets\YuanShen-Assets\AnimationDecodedRefresh\Ani_NPC_Male_Idle01" `
  -ModelName "NPC_Male_Standard_Model" `
  -IndexPath "D:\Assets\AS-Assets\YuanShen-Assets\library_index.db"
```

这两个脚本只按 Unity 对象的名称、PathID 和 Library 相对路径替换 `decoded` / `avatar` 这类已确认字段，并会先写 `.bak` 备份；它们不是匹配规则，也不会新增模型-动画候选。

如果要批量补旧 Library 里缺失的 `avatar.internalSolver`，先生成刷新计划。计划会按“该模型能解锁多少条显式 Humanoid 候选”排序，并写出定向导出 + 回写命令；它不会执行导出，也不会新增模型-动画关系：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Write-ModelAvatarRefreshPlan.ps1 `
  -LibraryRoot "D:\Assets\AS-Assets\YuanShen-Assets" `
  -SourceRoot "C:\Program Files\miHoYo Launcher\games\Genshin Impact Game\YuanShen_Data\StreamingAssets\AssetBundles\blocks" `
  -SourceIndex "D:\Assets\AS-Assets\YuanShen-Assets\unity_source_index.db" `
  -OutputDir "D:\Assets\AS-Assets\YuanShen-Assets\ModelAvatarRefreshPlan" `
  -Limit 20 `
  -NoBackupInCommands
```

重点看 `model_avatar_refresh_summary.json` 的 `totalPlannedUnlockCount` / `missingReasonCounts` / `globalMissing`，以及 `model_avatar_refresh_plan.csv` 里的 `missing_internal_solver_candidate_count` 和 `missing_reason`。计划脚本同时兼容旧库的 `requiresInternalHumanoidSolve=true` 候选，以及新索引里的 `missingInternalHumanoidSolver=true` / `inspect_missing_humanoid_avatar` 候选；它们都表示“关系来自 Unity，但模型缺完整 Avatar 参考姿态或内部 Humanoid 元数据”。`missing_reason` 会区分 `missing_avatar_object`、`missing_internal_solver`、`missing_solver_skeleton_nodes`、`missing_avatar_pose_metadata`、`missing_human_bone_index` 和 `missing_human_bone_limits`；其中旧库常见的 `missing_avatar_pose_metadata` 表示已有 internalSolver 但缺 `humanSkeletonPose` 或 `humanBoneIndex`，也必须刷新，不能继续生成会手脚反折的预览。部分 Unity 版本或游戏会缺 `internalSolver.skeleton.nodes/humanSkeletonPose`，但仍有 `internalSolver.avatarSkeleton.nodes/defaultPose`；这是 Unity AvatarConstant 的确定性骨架参考姿态，只能用于诊断和后续真实 Avatar/HumanDescription 恢复，不能单独升级为 Unity bake 主线候选或 bake-ready 判断。`humanBoneIndex` 必须至少有一个有效节点索引；如果数组存在但全是 `-1`，仍然是 `missing_human_bone_index`。`missing_human_bone_limits` 表示模型有 Unity `HumanDescription`，但旧库没有导出每根 human bone 的原始 limit；它是后续反推 Unity muscle 轴、stretch/twist 公式的重要输入。`globalMissing.scanMode=summary_table` 表示脚本用 `model_animation_candidate_model_summary` 快速统计全库 Avatar/internalSolver 缺口，不扫描几百万条候选 `raw_json`；只有非常旧的 SQLite 没有 summary 表时，才需要显式加 `-IncludeGlobalMissingScan` 允许慢扫。`plannedCandidateCoverageOfGlobalMissing` 可以粗看当前这批刷新计划覆盖了全局缺口的多少候选。如果 `unity_source_index.db` 的 `metadata.sourceRoot` 和手工传入的 `-SourceRoot` 不一致，计划脚本会优先使用源索引记录的 root 生成命令，确保 `--source_files` 与依赖图同根。若 `library_index.db` 是通过外部 fresh source index 重建的，显式传 `-SourceIndex`；未传时脚本会优先读取 `sqlite_index_summary.json` 中本库实际使用的 source index，避免误回退到 Library 根目录下的旧 `unity_source_index.db`。执行 `model_avatar_refresh_commands.ps1` 前应人工抽查命令里的 `--source_index`、`--source_files`、`--path_ids` 和模型名是否符合预期。`Apply-ModelAvatarRefresh.ps1` 回写模型 avatar 后，只有在 Avatar 具备完整 `HumanDescription.humanBones + skeletonBones` 时，才允许把同一模型上的显式 Humanoid 候选升级为 `requiresUnityBake=true`、`productionAnimationPath=UnityBakeToGltf`、`nextAction=generate_unity_baked_gltf`；如果刷新后仍只有 AvatarConstant/internalSolver，会保留或降回 `refresh_avatar_human_description` 并写入缺失原因。刷新后再跑 `--bake_animation_previews_from_library ... --preview_validation_limit 0`，看 `animation_bake_cache_summary.json` 里的 `bakeReadyExplicitUnityBakeCandidates` 是否上升。

如果模型已经带 `avatar.internalSolver`，但动画 sidecar 仍是 `compacted`、`missing`、`no_decoded_keyframes` 或没有完整 decoded 曲线，先生成动画 decoded 刷新计划。计划会优先排序“已经有可处理模型 solver 的显式 Humanoid 候选”，并写出定向导出 + 回写命令；它不会执行导出，也不会新增模型-动画关系：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Write-AnimationDecodedRefreshPlan.ps1 `
  -LibraryRoot "D:\Assets\AS-Assets\YuanShen-Assets" `
  -SourceRoot "C:\Program Files\miHoYo Launcher\games\Genshin Impact Game\YuanShen_Data\StreamingAssets\AssetBundles\blocks" `
  -OutputDir "D:\Assets\AS-Assets\YuanShen-Assets\AnimationDecodedRefreshPlan" `
  -Limit 20
```

重点看 `animation_decoded_refresh_plan.csv` 里的 `processable_internal_solver_candidate_count`。这个字段只统计模型已具备 `avatar.internalSolver`、候选关系来自 Unity 显式引用、且动画需要内部 Humanoid/Muscle 求解的组合；它比单纯的 `candidate_count` 更适合决定下一批 decoded 刷新顺序。和模型刷新计划一样，如果 `unity_source_index.db` 记录了 source root，命令会优先使用该 root，避免定向 decoded 刷新因为源根目录差一级而失败。`decodedRefreshReason=ok_but_no_decoded_curves` 表示旧 sidecar 曾经误标 `ok` 但实际没有任何可采样曲线，刷新后若仍为 `no_decoded_keyframes`，应当作为解码器缺口继续追踪，不能当作可播放成功。如果刷新后是 `empty_humanoid_clip`，说明 Unity clip 只有时长/同步/标记信息，没有 binding、ACL、streamed、dense、constant、ValueDelta 或 reference pose 曲线；保留显式关系用于溯源，但不要把它算作应生成 glTF 身体动画的失败项。执行 `animation_decoded_refresh_commands.ps1` 后，再用 `--validate_animation_previews_from_library ... --preview_validation_kind InternalHumanoid` 验证实际 glTF 是否为 `ok`。

`asset_catalog.jsonl`、`animation_bindings.jsonl`、`model_animations.json` 会带上 `animationAsset` 路径，后续预览、打包或 Unity/Blender 转换器都应该优先读取这个 sidecar，而不是靠动画文件名猜测语义。

Freedunk 当前已确认的情况：`NORMALMOVE_STAND_01`、`DASH_01` 属于 `MixedHumanoidTransform`，核心数据在 Unity Humanoid/Muscle 曲线中。`ApproximateHumanoidMuscleV*` 只能用于生成实验 channel 和调试报告，不能作为最终动画资产验收。长期目标仍是 AnimeStudio 内部 Humanoid/Muscle -> 目标骨架 TRS 求解器，让模型和动画能直接合成 glTF/GLB；但在内部公式未通过 Unity oracle 大规模验证前，如果阶段目标是先交付可验收动画库，可以显式使用 Unity bake 作为生产桥接路径，再把采样后的目标骨架 TRS 写入 glTF/GLB。

如果 `model_animations.json` 来自一个临时小样本目录，预览/打包时必须传完整 Unity 源目录，避免旧样本缺少外部 CAB 依赖导致脸、附件、材质或 Mesh 再次断链：

```powershell
--preview_source_root "C:\Program Files (x86)\Freedunk\Game\Freedunk_Data"
```

预览命令会使用完整 source root 重新定位索引里的 source 文件，并用完整 CAB map 解析外部 PPtr。`preview_validation.json` 也会记录 `exportIssues`；只要导出日志出现 `Unable to resolve ...` 这类依赖错误，状态就不能是 `ok`。

注意：`ApproximateHumanoidMuscleV*` 仍是实验求解/诊断输出。它只能说明“Muscle 曲线被写成了 glTF channel”，不能说明动作姿态正确。凡是使用该输出的预览会标为 `experimental`，不能作为最终可复用动画资产验收。

### 批量生成模型动画验证包

当已经有 `model_animations.json`，并想小范围验证一个模型的多条候选动画时，可以使用 `--pack_model_animations`：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --pack_model_animations "D:\Assets\Freedunk_Data_Dev\SkeletonIndexV1Sample\model_animations.json" `
  --game Normal `
  --preview_source_root "C:\Program Files (x86)\Freedunk\Game\Freedunk_Data" `
  --preview_model "Bill_01_00_ingame" `
  --pack_animations "NORMALMOVE_STAND_01,DASH_01,DEFENSEMOVE_RUN_0_01" `
  --pack_output "D:\Assets\Freedunk_Data_Dev\Bill_AnimationPack_V1" `
  --pack_limit 3
```

输出结构：

```text
Bill_AnimationPack_V1\
  animation_pack_report.json
  NORMALMOVE_STAND_01\
    preview_validation.json
    Models\...\Bill_01_00_ingame.gltf
  DASH_01\
    preview_validation.json
    Models\...\Bill_01_00_ingame.gltf
```

这个命令会对每个动画复用按需预览流程，生成“同一个模型 + 单条动画”的可播放 glTF，并汇总 `animation_pack_report.json`。报告会列出 `status`、`channels`、`coreBoneNodes`、`invalidChannels`、`baked`、`bakeMode`、`bakedTrackCount`、`bakedKeyframeCount` 和输出 glTF 路径。

如果素材库已经有 `library_index.db`，优先从 SQLite 中的确定性候选直接打包，避免依赖旧版 `model_animations.json` 是否完整：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --pack_model_animations_from_library "D:\Assets\AS-Assets\YuanShen-Assets" `
  --game Normal `
  --preview_model "Avatar_Girl_Sword" `
  --pack_animations "Idle,Run" `
  --pack_output "D:\Assets\AS-Assets\YuanShen-Assets\AnimationPacks\Avatar_Girl_Sword" `
  --pack_limit 8
```

`--pack_model_animations_from_library` 只读取 `library_index.db` 的 `model_animation_candidates`，并且只接受 `relation_source=explicit` 的候选。也就是说，候选关系必须已经来自 Unity 的 Animator、Animation、Controller、OverrideController、PPtr 或源索引依赖图；这个命令不会做模型 × 动画全量猜测，也不会用骨骼数量兜底匹配。

`--pack_animations` 可以写逗号分隔的名字、路径片段或正则。简单名字会先走 SQLite 粗筛；带 `|()` 等正则分组/分支的选择器会跳过 SQL 粗筛，最后仍由 CLI 正则匹配过滤，避免在预筛阶段漏掉确定性候选。

如果想先用旁路 SQLite 验证而不覆盖正式库，可以同时传 `--index_path "...\library_index.rebuilt.db"`；素材文件路径仍按 `--pack_model_animations_from_library` 指定的 Library 根目录解析。

如果要批量验收“确定性关系是否能直接生成可播放 glTF”，使用 `--validate_animation_previews_from_library`。它会从 `library_index.db` 读取未缓存的 `relation_source=explicit` 候选，生成有限数量的预览 glTF，读取 `preview_validation.json`，并写回 `animation_preview_cache`：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --validate_animation_previews_from_library "D:\Assets\AS-Assets\YuanShen-Assets" `
  --game GI `
  --preview_model "^NPC_Male_Standard_Model$" `
  --pack_animations "^Ani_NPC_Male_Idle01$" `
  --preview_validation_limit 1 `
  --preview_validation_kind InternalHumanoid `
  --preview_validation_force `
  --preview_validation_output "D:\Assets\AS-Assets\YuanShen-Assets\AnimationPreviewValidationSmoke"
```

这条命令不会新建模型-动画关系，也不会做全量猜测；它只是把已经确定的候选推进到“直接 glTF 预览是否成功”的验收层。后续覆盖率应同时看 `model_animation_candidate_*_summary` 的关系覆盖和 `animation_preview_cache` 的实际预览结果。

默认情况下，批量验证会跳过 `animation_preview_cache` 已经记录且 solver 版本仍然有效的候选，避免全量验收时反复重跑。需要给人工验收重新生成同一组 glTF，或刚修了动画解码/内部 Humanoid 求解器并想覆盖旧样本时，显式加 `--preview_validation_force`。这个参数只影响缓存过滤，不改变候选来源；仍然只处理 `relation_source=explicit` 的 Unity 确定性关系。

`--preview_validation_kind` 只控制批量抽样范围，不改变关系来源：

- `All`：默认行为，从所有未缓存的显式候选里抽样。
- `Direct`：只抽 `requiresInternalHumanoidSolve=false` 的 Transform / BlendShape / 非角色 Transform 候选。
- `InternalHumanoid`：只抽 `requiresInternalHumanoidSolve=true` 的 Humanoid/Muscle 候选，用来保留 AnimeStudio 内部 TRS 求解器的实验验证；当前生产主线不再依赖它。

每次运行后都会写出 `animation_preview_cache_summary.json`，并同步放到 Library 根目录，方便持续看验收进度。关键字段：

如果只想刷新覆盖率摘要、不生成新的预览 glTF，可以把 `--preview_validation_limit` 设为 `0`。这适合在批量回写 `avatar.internalSolver` 或 decoded sidecar 后快速检查 `internalHumanoidProcessableCandidates` 等统计是否变化。

- `totals.explicitCandidates`：当前 SQLite 里来自 Unity 确定性关系的模型-动画候选总数。
- `totals.directPreviewCandidates` / `totals.internalHumanoidCandidates`：可直接 Transform/BlendShape 预览和需要内部 Humanoid/Muscle 求解的候选数。
- `totals.internalHumanoidProcessableCandidates`：旧库或新库中，模型确实带完整 `avatar.internalSolver`、可以进入 AnimeStudio 内部 Humanoid/Muscle 求解器的候选数。这里的“完整”至少要求 `skeleton.nodes`、覆盖节点数的 `skeleton.humanSkeletonPose` 和 `humanBoneIndex`；只有 `preQ/postQ` 的旧 Avatar 元数据会被视为缺 solver，避免生成手脚反折的误导性预览。
- `totals.internalHumanoidMissingModelSolverCandidates`：动画看起来需要 Humanoid/Muscle，但目标模型缺 `avatar.internalSolver` 的显式关系；这些关系保留，但不能进入默认内部求解验收队列。
- `cache.cachedExplicitCandidates`：已经跑过 glTF 预览验证并写入缓存的确定性候选数。
- `cache.coverageOfExplicitCandidates` / `cache.okCoverageOfExplicitCandidates`：预览缓存覆盖率和 `ok` 覆盖率；这是验收进度，不是重新匹配模型和动画。
- `cache.emptyHumanoidClipCandidates` / `cache.okCoverageExcludingEmptyHumanoidClips`：把空 Humanoid 同步/标记 clip 单独扣出来后的可播放覆盖率口径。
- `cache.noHumanoidMuscleCurveCandidates`：显式 Unity 关系存在，但当前 decoded clip 没有可驱动身体的 Humanoid muscle 曲线；这类样本通常不是求解器把腿手算坏，而是动画数据还没有解出身体轨道。
- `cache.knownLimbFormulaRiskCandidates` / `cache.warningInternalHumanoidKnownLimbRiskCandidates`：内部 Humanoid solver 已生成 glTF channel，但命中了前臂/小腿已知 limb delta 公式风险。这类样本应保留为可复现预览和 oracle 反推输入，但不能计入 `okInternalHumanoidCandidates`。
- `cache.internalSolvedCachedCandidates`：`preview_validation.json` 里确认走过 AnimeStudio 内部 Humanoid/Muscle 求解器的缓存数量，用来和 `okInternalHumanoidCandidates` 对照。
- `cache.coverageOfInternalHumanoidProcessableCandidates` / `cache.okCoverageOfInternalHumanoidProcessableCandidates`：只针对可内部求解 Humanoid 队列的验收覆盖率；这比旧的 `internalHumanoidCandidates` 总数更适合作为“不再需要 Unity bake”的进度口径。
- `cache.statusCounts` / `cache.diagnosticStatusCounts`：前者按最终验证状态分组，后者按 Humanoid 求解诊断原因分组；排查原神库时优先看 `no_humanoid_muscle_curves`、`experimental_solved`、`Unknown` 的比例。
- `modelResourceKinds` / `animationCapabilities`：按模型资源类型和动画处理能力拆分的缓存状态，用来区分角色、道具、场景件、VFX/材质类动画等不同问题。

`animation_preview_cache.status` 按验证层级记录：

- `ok`：glTF 结构有效，动画 channel 能驱动目标节点或 morph target。角色动画会检查 skin 和主体骨骼；非角色 Transform 动画只要驱动可见 mesh 节点即可，不要求 skin 或人体骨骼。
- `empty_humanoid_clip`：显式 Unity 关系指向了 Humanoid/Muscle clip，但刷新后的 sidecar 证明它没有 binding、ACL、streamed、dense、constant、ValueDelta 或 reference pose 曲线。它保留在关系索引里，但不计入“应生成可播放身体动画”的失败项。
- `warning`：已生成 glTF，但验证器认为它不是完整可播放模型动画，例如只驱动 helper、材质/特效遮罩，或有导出警告。此类候选仍保留，后续应按 VFX/材质/激活类动画单独处理。
- `experimental`：生成成功但属于实验求解路径，例如旧近似 Humanoid bake 结果。它可用于诊断，不作为默认生产验收。

Humanoid/Muscle 内部求解缓存会记录 `solver_version`、`diagnostic_status`、`internal_solved` 和 `muscle_curve_count`。如果求解器的坐标空间或乘法顺序修正了，旧缓存即使曾经是 `ok`，也会在下一次 `--validate_animation_previews_from_library --preview_validation_kind InternalHumanoid` 时重新进入候选队列；Direct/Transform/BlendShape 预览不受 Humanoid 求解版本影响。`diagnostic_status=no_humanoid_muscle_curves` 表示当前 clip 没有解出身体 muscle 曲线，应先回到动画 decoded sidecar 或 Unity 对照采样排查，而不是把它当作骨骼姿态公式失败。
- `failed`：没有生成可验证 glTF，或生成流程发生异常。

如果旧 Library 的 `library_index.db` 里 `model_animation_candidates` 还是 0，但 Library 根目录已有 `unity_source_index.db`，先重建 SQLite 索引即可恢复确定性 Unity 关系，不需要重跑全量模型导出，也不能退回模型 × 动画全量猜测：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --build_sqlite_index "D:\Assets\AS-Assets\YuanShen-Assets" `
  --require_fresh_source_animation_relations
```

需要先旁路验证时，使用 `--index_path "...\library_index.verify.db"`；确认后再让默认输出覆盖正式 `library_index.db`。
如果已经有一份旁路重建的 fresh `unity_source_index.db`，必须同时显式传入 `--source_index`；`--rebuild_library_indexes` 只使用 Library 根目录旁的源索引，不适合这种旁路替换验证：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --build_sqlite_index "D:\Assets\AS-Assets\YuanShen-Assets" `
  --source_index "D:\Assets\AS-Assets\YuanShen-Assets\SourceIndexRebuild_OverridePair_20260613\unity_source_index.db" `
  --index_path "D:\Assets\AS-Assets\YuanShen-Assets\library_index.overrideSet.verify.db" `
  --require_fresh_source_animation_relations
```

当前 `--pack_model_animations` 的定位是“可复用动画验证包”：用于确认某个模型能否播放一组候选动画、快速筛选动作、给后续动画合集打包提供已验证输入。它不是最终的 animation-only 资产格式。最终素材库仍应继续向“干净模型 + 独立动画资产 + 绑定索引 + 按需合并/预览”推进。

### Unity Humanoid 烘焙生产主线

当前可验收主线是 Unity bake -> glTF/GLB：模型-动画关系仍然只来自 `library_index.db` 的 `relation_source=explicit` 候选，Unity 只负责用 `Animator`、`Avatar` 和 `PlayableGraph` 采样 Humanoid/Muscle 动画，AnimeStudio 再把采样后的目标骨架 TRS 写回 glTF/GLB。这样可以先交付可浏览、可验证、可复用的动画素材库，同时把内部 Humanoid 公式求解保留为实验诊断。

批量生产从 Library SQLite 候选表开始：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --bake_animation_previews_from_library "D:\Assets\AS-Assets\YuanShen-Assets" `
  --preview_model "^Avatar_Boy_Bow_Gorou$" `
  --pack_animations "^Ani_Avatar_Boy_Bow_AimRunBS$" `
  --preview_validation_limit 1 `
  --preview_validation_output "D:\Assets\AS-Assets\YuanShen-Assets\UnityBakeProductionSmoke" `
  --unity_project "D:\Assets\AnimeStudioUnityBakeProject" `
  --unity_editor "C:\Program Files\Unity\Hub\Editor\<version>\Editor\Unity.exe" `
  --run_unity_bake
```

这个命令只选择显式 Humanoid/Muscle 候选，不做模型 × 动画猜测。未加 `--run_unity_bake` 时只生成 `unity_bake_request.json` 和 `unity_bake_batch_report.json`，适合先审查请求；dry run 不写入 `library_index.db.animation_bake_cache`，避免把半成品 request 当成生产覆盖率。加上 `--run_unity_bake` 后会生成 `unity_bake_result.json`、带动画的 baked glTF 和 `unity_bake_apply_report.json`，并把 `baked`、`static_pose`、`failed` 写入缓存。`--preview_validation_limit` 控制本轮最多处理多少个待烘焙候选，适合把原神这类大库拆成可续跑的小批次；默认未指定模型/动画筛选时会尽量跨模型轮转取样，方便早期发现不同骨架族的 bake 问题；同一轮内部会优先选择有核心 Transform binding、Transform binding、曲线数量和有效时长证据的动态候选，`Standby` / `Idle` / `Loop` / `Sync` 这类名字只做轻微降权，不会作为绑定依据或硬过滤。普通批次结束只写 `summaryMode=fast_cache_only` 的轻量 `animation_bake_cache_summary.json`，避免每批都 join 几百万显式候选；设为 `0` 时才刷新带全量候选分母的 `unity_bake_batch_report.json` 和 `animation_bake_cache_summary.json`，不会生成 request 或写入 failed。全量摘要会同时写出 `bakeReadyExplicitUnityBakeCandidates`、`importedAvatarAssetBakeReadyExplicitUnityBakeCandidates` 和 `effectiveBakeReadyExplicitUnityBakeCandidates`；其中 effective 是真实 prefab / HumanDescription 路径与 `Assets/AnimeStudioBake/ImportedAvatar/*.asset` 导入 Avatar 路径的联合覆盖，只代表这些显式候选已经具备确定性 Unity bake oracle，不代表新增了候选关系，也不代表视觉已验收。`status=summary_only` 的 `unity_bake_batch_report.json` 也会写出同一组 Avatar oracle 路径拆分和 `avatarSourceCounts`，方便 Browser 在不读取完整根摘要时也能显示本次摘要的来源口径。原神这类依赖导入 Avatar asset 的库，即使只是运行 `--preview_validation_limit 0` 刷统计，也必须传入正确的 `--unity_project`，否则 CLI 找不到 `Assets/AnimeStudioBake/ImportedAvatar`，`importedAvatarAssetBakeReadyExplicitUnityBakeCandidates` 和 effective 覆盖会显示为 0。根目录 `animation_bake_cache_summary.json` 会同时写出 ImportedAvatar 目录文件数、probe 放行的可信文件数、probe freshness 和 `probeEnforced`，Browser 状态栏按这些字段展示当前 Avatar oracle 是否来自 fresh Unity 验证。缓存覆盖率字段（如 `cacheCoveragePercent`、`uniqueTrustedBakedCoveragePercent`）使用 effective 分母，避免原神这类库因为旧索引缺 HumanDescription 而把导入 Avatar oracle 覆盖错误显示为 0。批量命令默认只跳过可信 `baked`、已确认 `static_pose` 或 `needs_review` 的终态诊断；旧 helper 生成的 `baked` 如果缺少可信 Avatar 证明，会在 `effectiveStatusCounts` 中显示为 `untrusted_baked`，并重新进入待烘焙队列。缓存写回时也只有可信 `baked`、`static_pose`、`needs_review` 会保护旧终态；不可信旧 `baked` 可以被后续失败或新请求状态覆盖，避免错误缓存长期占坑。比较缓存时会把 Library 内绝对路径和相对路径先规范到同一个 key，适合原神这类大库断点续跑；如果当前筛选窗口全都已经处理，会写出 `unity_bake_batch_report.json`，其中 `status=noop_all_cached`、`skippedBakedCache=true`，表示这不是失败，只是本轮无需处理。如果筛选窗口命中了显式 Humanoid/Muscle 候选，但这些候选都缺少生产 Avatar oracle，会写出 `status=noop_missing_avatar_oracle` 和 `items[*].status=skipped_missing_avatar_oracle`；这说明关系已经是确定性的，但还需要恢复/导入原始 Unity Avatar asset 或补完整 HumanDescription，不能退回骨骼数量、名称、AvatarConstant 或当前姿态猜测。如果要重新生成已成功或静态样本，显式加 `--preview_validation_force`。后续统计生产 bake 覆盖率时优先看 `explicitUnityBakeCandidates`、`requestWrittenCandidates`、`uniqueTrustedBakedCandidates`、`untrustedBakedCandidates`、`duplicateCacheRows`、`uniqueCacheCoveragePercent` 和 `uniqueTrustedBakedCoveragePercent`。`unity_bake_apply_report.json` 只有在状态为 `ok` / `warning`、`frameVaryingTracks > 0` 且 `avatarTrust.TrustedProductionBake=true` 时才算可信生产 bake；如果 apply report 指向的 request 显式带了 `unityAssetPaths.avatarAsset`，还必须看到 `avatarTrust.source=imported_unity_avatar_asset`，否则按不可信旧结果处理。Humanoid 采样写回默认使用 `humanoidDeltaBase=rest_pose`，也就是把 Unity bake 姿态相对 Unity rest pose 的变化写回 glTF 模型自己的 rest pose。不能把第一采样帧当默认基准，否则 idle、aim、pose-only 这类第一帧已经有效的动作会被抹掉。模型进入生产 bake 只能依赖 Unity 工程内真实 prefab/Animator.avatar，完整 `HumanDescription.humanBones + skeletonBones`，或显式导入的原始 `UnityEngine.Avatar` asset；`AvatarConstant` / `avatar.oracle` / 旧库 `avatar.internalSolver` 只能作为诊断和后续 Avatar 恢复输入，不能直接计入可信覆盖率。`--baked_fbx_output <dir-or-fbx>` 是可选兼容输出，会先生成 baked glTF，再通过 Blender 转 FBX；FBX 不进入当前主线验收。

`--preview_validation_limit 0` 写 `status=summary_only` 批次报告时，会复用刚刷新的 `animation_bake_cache_summary.json` 里的候选分母和 Avatar 来源字段，避免为了 Browser 状态再重复扫描几百万显式候选。

读取旧 `unity_bake_apply_report.json` 时，Browser、CLI 摘要和覆盖率脚本会按当前规则重新判断 Avatar 信任来源：`internal_solver`、`avatar_constant`、`oracle` 这类来源即使旧报告写了 `TrustedProductionBake=true`，也只算诊断结果，不再计入可信可播放 baked 动画；显式 `unityAssetPaths.avatarAsset` 请求还必须看到 `imported_unity_avatar_asset` 来源和有效导入 Avatar 证明。

先从 `model_animations.json` 生成请求：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --generate_unity_bake_request "D:\Assets\Freedunk_Data_Dev\AnimationBindingV1Sample\model_animations.json" `
  --preview_model "Bill_01_00_ingame" `
  --preview_animation "NORMALMOVE_STAND_01" `
  --preview_output "D:\Assets\Freedunk_Data_Dev\UnityBake_Bill_NormalMove" `
  --unity_project "D:\Assets\Freedunk_Data_Dev\UnityBakeProject" `
  --unity_model_prefab "Assets/AnimeStudioBake/Input/Bill_01_00_ingame.prefab" `
  --unity_animation_clip "Assets/AnimeStudioBake/Input/NORMALMOVE_STAND_01.anim"
```

这会输出：

- `unity_bake_request.json`：AnimeStudio 选中的模型、动画、Avatar/骨架信息，以及 Unity 工程中的 prefab/clip 路径。
- `unity_bake_result.json`：Unity helper 运行后生成的逐帧骨骼 TRS 结果。

如果 Unity 工程和资源已经准备好，可以直接运行：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --generate_unity_bake_request "D:\Assets\Freedunk_Data_Dev\AnimationBindingV1Sample\model_animations.json" `
  --preview_model "Bill_01_00_ingame" `
  --preview_animation "NORMALMOVE_STAND_01" `
  --preview_output "D:\Assets\Freedunk_Data_Dev\UnityBake_Bill_NormalMove" `
  --unity_project "D:\Assets\Freedunk_Data_Dev\UnityBakeProject" `
  --unity_editor "C:\Program Files\Unity\Hub\Editor\<version>\Editor\Unity.exe" `
  --unity_model_prefab "Assets/AnimeStudioBake/Input/Bill_01_00_ingame.prefab" `
  --unity_animation_clip "Assets/AnimeStudioBake/Input/NORMALMOVE_STAND_01.anim" `
  --baked_gltf_output "D:\Assets\Freedunk_Data_Dev\UnityBake_Bill_NormalMove\BakedPreview\Bill_01_00_ingame__NORMALMOVE_STAND_01.gltf" `
  --run_unity_bake
```

Unity helper 脚本在 `AnimeStudio.UnityBake\Assets\AnimeStudio.UnityBake\Editor`。把这个目录复制进 Unity 工程的 `Assets` 后，batchmode 会调用 `AnimeStudio.UnityBake.AnimeStudioBakeCli.Run`。
CLI 在 `--run_unity_bake` 前会检查 bake 工程里的 helper 是否包含导入 Avatar asset 强校验和 `importedAvatarAssetValid` 证明字段。若提示 helper 过旧，先重新复制仓库里的 `AnimeStudio.UnityBake\Assets\AnimeStudio.UnityBake` 到 Unity 工程 `Assets`，再重跑烘焙；不要继续使用旧 helper 生成原神 Avatar oracle 样本。

如果 `--unity_model_prefab` 或 `--unity_animation_clip` 为空，helper 会优先使用 request 里的 AnimeStudio 资产：

- 从 glTF 节点层级和 local TRS 重建采样骨架，并在 glTF 与 Unity 本地坐标之间做显式 TRS 基坐标转换。
- 从 Unity Avatar/HumanDescription 的 `humanBones`、原始 `skeletonBones` pose、twist/stretch 等参数构建 Human Avatar。
- 把 AnimeStudio 导出的 `.anim` 复制进 Unity 工程并作为 AnimationClip 导入。

注意：`humanBones` 只说明“哪根骨头对应哪个人体部位”，不能单独作为 Humanoid 动画正确性的验收依据。Humanoid bake 必须使用 Unity 原始 Avatar 参考姿态；如果只用 glTF 当前骨架姿态或 AvatarConstant/internalSolver 临时构建 Avatar，Unity 仍可能给出有效 Avatar，但手臂、腿部方向会明显错误。旧索引如果缺完整 `HumanDescription.humanBones + skeletonBones`，即使已经保存 AvatarConstant oracle（`humanBoneIndex`、human skeleton pose、avatar skeleton defaultPose），请求生成阶段也只能把它作为诊断/恢复输入，不能进入可信生产 bake。

定向诊断样本有时只有模型自带 embedded animation，`model_animations.json` 里 `candidateCount=0`，但导出目录的 `Animations/**/*.animation_asset.json` 仍保存了 decoded Humanoid/Muscle sidecar。`--generate_unity_bake_request` 在选不到候选时会按 `--preview_animation` 从同目录 `Animations` 下查找匹配 sidecar，并生成 `relationSource=embedded_sidecar_diagnostic` 的人工诊断 request。这个路径只用于 Unity oracle / 公式反推，不会把 embedded sidecar 提升为默认模型-动画推荐关系；正式 Library 关系仍必须来自 Animator、Animation、Controller、OverrideController、PPtr 或源索引确定性关系。

`--run_unity_bake` 成功后会自动把 `unity_bake_result.json` 合进源 glTF，输出带标准 glTF animation channel 的 baked preview，并写出 `unity_bake_apply_report.json`。也可以单独执行：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --apply_unity_bake_result "D:\Assets\Freedunk_Data_Dev\UnityBake_Bill_NormalMove\unity_bake_request.json" `
  --baked_gltf_output "D:\Assets\Freedunk_Data_Dev\UnityBake_Bill_NormalMove\BakedPreview\Bill_01_00_ingame__NORMALMOVE_STAND_01.gltf"
```

### 内部 TRS 求解实验记录

内部 Humanoid/Muscle 求解已经留下了可复现的实验基础，但当前不作为生产主线。后续如果重启这条方案，应从以下事实继续，而不是重新凭截图猜：

1. AnimeStudio 已能解析 Unity 资源关系，导出干净模型、贴图、骨架和独立 AnimationClip。
2. Transform TRS 动画可继续由 decoded sidecar 直接写入 glTF animation channel。
3. Humanoid/Muscle 内部求解当前仍存在前臂/小腿公式风险，不能算视觉验收通过。
4. 相关预览、oracle compare 和 `FastPreviewGltfBuilder` 代码保留为实验诊断，不进入默认生产路径。

生产验收仍以 baked glTF/GLB 的 node、skin、animation channel、bbox 和 `unity_bake_apply_report.json` 为准；内部求解结果只能作为对照样本或后续研究输入。

Humanoid/Muscle FastPreview 要注意坐标空间：`avatar.internalSolver`、Muscle 曲线、`preQ/postQ` 都是 Unity 空间；已导出的 glTF node rotation 已经经过 Unity -> glTF 轴转换。内部求解当前使用 Unity Avatar 轴系的 swing-twist 公式：Y/Z 合成 swing，X 作为 twist，再按 `preQ * swing * twist * inverse(postQ)` 生成骨骼本地旋转，最后转换回 glTF。Unity single-muscle probe 显示，小腿/前臂这类远端 twist 要用来源长骨的 X/twist 限位，再写到脚/手的 X/twist 本地旋转；Foot Twist 则写到 Foot 的 Y/swing 轴。Arm Twist 的单 muscle 轴拟合会提示 Y/swing 候选，但 NPC/Gorou 完整 timeline 对照会变差，暂时不能迁入生产 solver，后续必须继续用完整时间线 oracle 验证。`preview_validation.json` / glTF animation extras 中的 `restPoseSpace` 会记录当前求解方式，方便排查视觉异常。

Humanoid/Muscle FastPreview 的 glTF animation extras 里会写 `unityHumanoid.diagnostics.summary`。排查预览是否可信时先看这里：`solvedHumanBoneCount` 是当前内部 solver 实际写入身体 TRS 的人体骨数量；`missingAvatarAxisHumanBones` / `missingGltfNodeHumanBones` 表示模型 Avatar 或 glTF 节点映射缺口；`noMatchingCurveHumanBones` 表示这条动画 decoded muscle 曲线没有驱动对应人体骨。`no_humanoid_muscle_curves` 表示显式 Unity 关系存在，但 decoded float 里只有 Root/Motion 或 helper/accessory Transform 曲线，没有任何可供 Humanoid solver 使用的人体 muscle 属性；这类样本应保留关系并按 Transform/附件动画检查，不能拿来判断 Humanoid 公式好坏。只有 `solved_experimental` 的目标才进入当前预览动画；`no_tracks_solved` 的样本不能用于判断公式好坏，应先刷新 decoded sidecar 或检查动画类型。

如果 diagnostics 顶层状态是 `experimental_solved_known_limb_formula_risk`，说明 glTF channel 已生成，但命中了已知还没完全复现 Unity 的前臂/小腿 limb delta 公式。此时关系和 decoded 输入可以继续保留，预览也可用于人工观察，但不能算视觉验收通过；应继续用 Unity oracle 对照追 `Forearm Stretch + Arm Twist` 或 `LowerLeg Stretch / UpperLeg Twist` 的成对 delta 空间，而不是用骨骼数量、名称或局部反号兜底。

Humanoid/Muscle FastPreview 现在要求模型 `avatar.internalSolver.skeleton.humanSkeletonPose` 至少覆盖 `skeleton.nodes`，并且有 `humanBoneIndex`。旧库里只有 `preQ/postQ`、缺 Unity Avatar 参考姿态的模型会直接失败并提示刷新 Avatar 元数据；不要继续生成会手脚反折的 glTF。NPC/Gorou oracle 对照显示，当前大误差主要来自 zero-muscle / Avatar 参考姿态校正缺失，不是单纯的手臂 twist 轴或 Stretch 轴错误；简单把 `gltfRest`、`avatarDefaultPose`、`avatarSkeletonPose` 当统一 zero-anchor 会让四肢变差，必须继续用完整时间线 oracle 验证后再迁入生产 solver。

V3 AvatarPose 流程已用 Freedunk `Bill_01_00_ingame` 人工确认：`DASH_01` 和 `FACEUPMOVE_RUN_STANDARD_01` baked glTF 动作正常。关键修正是导出并复用 Unity 原始 `HumanDescription.skeletonBones` 参考姿态；仅靠 `humanBones` 映射或 glTF 当前 local TRS 会导致“背手”“腿方向交叉”等错误动作。

Freedunk 小批量回归基线：

- `D:\Assets\Freedunk_Data_Dev\UnityBake_MultiChar_PublicNormal_V3_AvatarPose`：4 个角色 x 2 条动作，`DASH_01`、`FACEUPMOVE_RUN_STANDARD_01`，人工确认自然。
- `D:\Assets\Freedunk_Data_Dev\UnityBake_MultiChar_PublicNormal_V3_MoreAnimations`：4 个角色 x 6 条动作成功，`NORMALMOVE_STAND_01`、`DEFENSEMOVE_RUN_0_01`、`HANDSUP_STAND_01`、`HANDSUP_RUN_0_01`、`BOXOUT_STAND_01`、`SLIDESTEP_01`，人工确认自然。
- `D:\Assets\Freedunk_Data_Dev\UnityBake_MultiChar_PublicNormal_V3_StaticPoseFix`：`JUMPBALL_READY_NORMAL`、`SCREEN_01` 属于极短/静态姿态类 clip；Unity helper 需要把采样姿态同时与原始 rest pose 比较，不能只比较帧间变化。修正后 4 个角色 x 2 条动作全部 `status=ok`、`InvalidTargetCount=0`。

从已有 bake request/result 生成 baked glTF：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --apply_unity_bake_result "D:\Assets\Freedunk_Data_Dev\UnityBake_Bill_NormalMove\unity_bake_request.json" `
  --baked_gltf_output "D:\Assets\Freedunk_Data_Dev\UnityBake_Bill_NormalMove\BakedPreview\Bill_01_00_ingame__NORMALMOVE_STAND_01.gltf"
```

也可以在 `--run_unity_bake` 时直接生成 baked glTF：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --generate_unity_bake_request "D:\Assets\Freedunk_Data_Dev\AnimationBindingV1Sample\model_animations.json" `
  --preview_model "Bill_01_00_ingame" `
  --preview_animation "NORMALMOVE_STAND_01" `
  --preview_output "D:\Assets\Freedunk_Data_Dev\UnityBake_Bill_NormalMove" `
  --unity_project "D:\Assets\Freedunk_Data_Dev\UnityBakeProject" `
  --unity_editor "C:\Program Files\Unity\Hub\Editor\<version>\Editor\Unity.exe" `
  --baked_gltf_output "D:\Assets\Freedunk_Data_Dev\UnityBake_Bill_NormalMove\BakedPreview\Bill_01_00_ingame__NORMALMOVE_STAND_01.gltf" `
  --run_unity_bake
```

如果确实需要 FBX 兼容包，可以额外加 `--baked_fbx_output` 和 `--blender`。AnimeStudio 会先生成 baked glTF，再通过 Blender 导出 FBX；该 FBX 用于 DCC 兼容检查，不替代 glTF 验证报告。

已经有可信 baked glTF/GLB 时，也可以只做一次 FBX 对照转换，不重新跑 Unity bake、不写入 `animation_bake_cache`：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --export_fbx_from_gltf "D:\Assets\YuanShen_Bake\BakedPreview\Avatar_Girl_Catalyst_Charlotte__Attack_03.gltf" `
  --baked_fbx_output "D:\Assets\YuanShen_Bake\FbxCompare\Avatar_Girl_Catalyst_Charlotte__Attack_03.fbx" `
  --blender "C:\Program Files\Blender Foundation\Blender 5.1\blender.exe"
```

输出报告：

- `unity_bake_apply_report.json`：baked glTF 写入结果、skin/joint/animation target 验证。`InvalidTargets` 必须为 `0`；`HierarchyParentTargets` 表示动画写到了含 skinned joint 子孙的层级父节点，属于有效的 glTF 层级传播，不应算作错误。
- `blender_fbx_export_report.json`：仅在指定 `--baked_fbx_output` 时生成，记录 Blender 导入 glTF、导出 FBX 的对象、mesh、armature、action 和进程日志摘要。

当内部 Humanoid/Muscle 求解结果出现手脚反折、腿部方向异常等视觉问题时，用 Unity bake 作为公式对照，而不是继续凭截图猜：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --compare_unity_bake_result "D:\Assets\YuanShen_Oracle\unity_bake_request.json" `
  --compare_gltf "D:\Assets\YuanShen_Internal\NPC_Male_Standard_Model.gltf" `
  --compare_output "D:\Assets\YuanShen_Oracle\unity_bake_compare_report.json"
```

`unity_bake_compare_report.json` 会按骨骼列出 Unity 采样 local rotation 和内部 glTF rotation 的角度误差，重点摘要 thigh/calf/foot/upperarm/forearm 等身体骨骼。这个命令只用于反推和验证内部求解公式；当前默认素材库生产依赖的是 Unity bake 写回 glTF 的结果，而不是内部公式求解结果。

报告里的 `zeroMuscleResidualCorrectionSummary` 会专门比较 Unity zero-muscle `InternalAvatarPose` 与当前离线零姿态公式 `preQ * inverse(postQ)`。它不使用动画帧作为依据，而是列出每根人体骨骼的静态 offset 误差、最接近的 glTF rest / Avatar pose 候选和候选投票，用来判断手脚反折是否来自缺失的 Avatar/rest pose 校正；只有跨模型、跨动作稳定的候选才允许迁入生产 solver。

`internalHumanoidSolverVariantComparison.residualCandidateTimelineFitSummary` 会额外测试 `pose * inverse(preQ*inverse(postQ))` 和 `inverse(preQ*inverse(postQ)) * pose` 这类 zero-delta 组合候选。它们用于排查“Unity zero pose 是否就是某个导出的 rest/avatar pose”。NPC/Gorou 对照显示这些组合只能解释个别骨骼，作为全局规则会让平均误差变差；因此暂时只保留为诊断，不能迁入生产 solver。

`residualCandidateTimelineFitSummary.focusedLimbPerBoneCandidateSummary` 会把 UpperArm/LowerArm/UpperLeg/LowerLeg/Foot 单独列出来，比较当前公式与“每根骨骼最佳 rest/avatar pose 修正候选”的差距。2026-06-13 的 NPC/Gorou multi-value oracle 显示：腿部大多可被 rest/localRest/avatar pose 候选压到约 `0°-14°`，但前臂仍有约 `39°-61°` 残差，且 NPC/Gorou 选择的候选族不一致。这说明可见手臂扭曲不能靠迁入单一静态 rest offset 解决，后续应继续通过 Unity `HumanPoseHandler.SetHumanPose` / `GetInternalAvatarPose` probe 反推前臂/上臂的动态 delta 空间公式。

报告里的 `internalAvatarPoseTranslationTimelineSummary` 会检查 Unity `InternalAvatarPose` 的每个 joint 本地 `T.xyz` 是否随时间变化，以及它和 glTF rest translation 是否一致。Unity 文档说明 `GetInternalAvatarPose` 的 avatar pose 是按 joint path 排列的本地 TRS，每个 joint 为 `T.xyz + Q.xyzw`；Humanoid 也存在可选 Translate DoF。若这里发现非 root 骨骼有明显 `maxFrameDeltaFromFirst`，内部 solver 后续就必须写 glTF translation channel，而不能只写 rotation。当前 NPC/Gorou oracle 样本显示非 root translation 没有帧间变化，腿手问题继续归到 rotation / Avatar zero offset 路径追踪。

2026-06-13 的原神 NPC/Gorou 对照更新后，`humanoidSolverDiagnosis.currentGltfError.errorKind=delta_space_formula_mismatch`。`unityTransformLocalPoseReferenceSummary` 已确认 SetHumanPose 前的 Unity `Transform.localRotation` 与 glTF rest 基本一致，SetHumanPose 后的 Unity `Transform.localRotation` 与 `InternalAvatarPose` 也基本一致；问题集中在 AnimeStudio 离线 solver 复现 Unity `HumanPoseHandler.SetHumanPose` 的 muscle -> local TRS 公式，而不是简单的 glTF rest 坐标或 root translation 问题。`internalHumanoidSolverVariantComparison.variants` 里的轴序、twist split、gltf rest sandwich 等候选如果没有跨模型稳定降低误差，只能继续作为诊断，不应迁入生产 FastPreview。

`internalHumanoidSolverVariantComparison.transformLocalDeltaSolverComparison` 会直接比较 Unity SetHumanPose 造成的 `Transform.local` delta 与当前离线 solver 的 delta。NPC/Gorou 当前结果显示前臂和小腿的 delta 误差仍在约 `80°-90°`，例如 NPC `LeftLowerArm` 约 `82.49°`、`LeftLowerLeg` 约 `81.13°`，Gorou `RightLowerArm` 约 `86.38°`、`LeftLowerLeg` 约 `82.23°`。这说明现有 solver 的动态 delta 公式本身还不对，不能只迁入某个首帧常量 offset；下一步应通过 Unity probe 继续暴露 Avatar 轴系、human skeleton pose 和 muscle delta 到骨骼 local delta 的固定变换关系。

`singleMuscleProbeSolverComparison.formulaHintSummary` 会把单 muscle 黑盒探针的 source/target 轴枚举结果压缩成可读摘要。`topUsefulHints` 表示当前公式误差大于 `15°`、但枚举候选已能降到 `5°` 内；`topReviewHints` 表示候选能明显改善但还在 `5°-20°`，只能作为下一轮公式线索。当前 NPC 样本的 lower-leg twist 已出现 `4` 个 useful hint，`Left/Right Upper Leg Twist In-Out -> Left/Right LowerLeg` 使用 `sourceAxis=X/twist`、`targetAxis=X/twist` 可把误差降到约 `3.6°-4.6°`；但 Gorou 同类项仍主要落在 review 区间，例如 `LeftLowerLeg | Left Upper Leg Twist In-Out` 约 `15.15°`、`RightLowerLeg | Right Upper Leg Twist In-Out` 约 `10.62°`。前臂 twist 在两个模型上也主要是 review hint，常见候选为 lower/upper arm 的 `sourceAxis=X/twist` 或 `Z/swing` 写到 lower-arm `targetAxis=Y/swing`。因此这些结果还不是生产公式，只说明下一轮应优先验证 limb twist 的 source 轴、target 轴和乘法空间，而不是继续泛化一个全身 offset。

`internalHumanoidSolverVariantComparison.variants` 中的 `leg_twist_parent_source_x`、`arm_twist_target_y_current_source`、`arm_twist_target_y_parent_source` 会把上述单 muscle hint 提升到完整动作时间线复验。2026-06-13 的 NPC/Gorou 对照显示：`leg_twist_parent_source_x` 只能把 NPC 腿部最大误差从约 `9.90°` 降到 `5.71°`，Gorou 腿部仍约 `26.07°`；两个 arm target-Y variant 会让 NPC/Gorou 的 Arm 最大误差变得更糟，NPC 约 `100.69°`、Gorou 约 `95.65°`。结论是：单 muscle probe 暴露了局部轴线索，但当前这三个候选没有通过完整时间线复验，不能迁入生产 solver。

`internalHumanoidSolverVariantComparison.armTwistTimelineAxisFitSummary.rankingSummary` 会专门把 Arm Twist 的 source bone、source axis、target axis、swing/twist 组合顺序和左右 `mirrorXY` 放到完整 Unity `GetInternalAvatarPose` timeline 上复验。2026-06-13 的 NPC/Gorou 交叉报告显示：Gorou 上 `upper_y_to_x_mirror_xy_right` 可把 Arm Twist 平均 track max 从约 `82.86°` 小降到 `82.09°`，但 NPC 没有任何候选满足 `beatsCurrent`，且两个模型没有共同 useful candidate。这个结果说明 Arm Twist 轴/镜像确实是问题线索，但还不是生产公式；下一步应把 Arm Twist 与 upper-arm swing、forearm mirror/rest source 一起做组合诊断，而不是只迁入 Gorou 上的小幅改善。

`internalHumanoidSolverVariantComparison.armCombinedTimelineFitSummary` 会把 upper-arm swing 的 top timeline variants 和 Arm Twist 的 top timeline variants 组合起来，只评估 UpperArm + Forearm 这组 Arm track。2026-06-13 的 NPC/Gorou 交叉报告显示：Gorou 的最佳组合 `x_x_pos__y_y_pos__ellipse_clamped__swing_twist__upper_y_to_x_mirror_xy_right` 可把 Arm 平均 track max 从约 `72.10°` 降到 `71.46°`；NPC 的最佳组合只有约 `0.38°` 平均改善且最大误差不改善，不满足 `beatsCurrent`。两个模型仍没有共同 useful candidate。这个负结论排除了“简单组合 upper-arm swing 轴枚举 + arm twist 轴/镜像枚举”作为生产公式；下一步应转向 forearm/rest source、Avatar skeleton pose 与左右 mirror 的缺失空间，而不是继续堆 axis permutation。

`internalHumanoidSolverVariantComparison.limbTimelineVariantRankingSummary` 会把所有完整时间线 solver variant 只按 Arm/Leg 重新排序，并用更严格的 `beatsCurrentFocus` 判断候选是否真的优于当前公式：最大误差必须明显下降，或者最大误差基本不变且平均误差明显下降。2026-06-13 的多幅度 oracle 复验显示，NPC_Male `Alert01AS` 没有任何候选严格优于 `current_swing_twist`；Gorou `Standby` 只有 `twist_split_unity_child_param` 让 Arm/Leg 最大误差从约 `84.70°` 小降到 `81.77°`，但 NPC 不支持同一候选。因此当前仍没有可迁入生产 solver 的跨模型公式；下一步应继续拆 forearm/upper-arm 的 Avatar 空间、parent chain 和左右镜像，而不是把某个 twist split 候选上线。

`internalHumanoidSolverVariantComparison.currentResidualStability.focusedLimbCorrectionSpace` 会把 Arm/Leg 的关键骨骼单独压缩成可读表：每个 pair 分别列出左右骨骼的最佳 left/right correction mode、最近的确定性候选、候选类别、gap 和首帧常量校正后的最大残差。它用于判断问题是“已有 rest/avatar 候选接近但动态公式还没解”，还是“左右 mirror 空间明显但缺少正确 source candidate”。2026-06-13 的 NPC/Gorou 复验显示，两个模型的 forearm 都落在 `mirror_space_likely_but_source_candidate_missing`：NPC forearm 最近候选 gap 约 `52°-63°`，Gorou forearm 约 `40°-61°`，但左右 mirror gap 很低。腿部则分段差异明显：NPC lowerLeg/foot 更像已有 `rest` 候选接近但动态公式未解，Gorou lowerLeg 仍偏 mirror/source 缺失。这说明 Unity 黑盒 probe 可以继续反推公式，但当前不能把单个 rest、parent-chain 或 mirror 规则直接迁入生产 solver。

原神 Library 的确定性模型-动画关系应优先看 `library_index.db` / `sqlite_index_summary.json`，不要只看 deferred stub 版 `model_animations.json`。2026-06-13 对 `D:\Assets\AS-Assets\YuanShen-Assets` 的快速 SQLite probe 显示，当前 `unity_source_index.db` 可恢复 `4,012,493` 条去重后的 `explicit_unity_source_index` 候选，覆盖 `24,536` 个模型和 `93,758` 个动画；候选的 `relation_source` 全部是 `explicit`，不是骨骼数量或名称兜底。按明显动画模型目录粗看，`Models/NPC`、`Models/Avatar`、`Models/Monster`、`Models/Animal`、`Models/Partner`、`Models/Vehicle` 合计约 `15,725 / 16,529 = 95.14%` 已有显式候选；全库模型口径只有约 `18.18%`，主要是因为分母包含大量静态、VFX、场景块和零件。当天用当前工具重建 `unity_source_index.db` 后确认，原神 `633` 个 `AnimatorOverrideController` 全部是空替换表：`animatorOverrideController.overrideSet=633`、`nonEmptyOverrideSetCount=0`、`staleOverridePairIndex=false`。旧导入逻辑在同一个可浏览模型有多个 Unity 对象路径时，会把空 OverrideController 继承的 base controller 动画重复写成 `4,016,236` 条候选；当前导入逻辑已按 `(model_output, animation_output)` 全局去重，正式 `library_index.db` 保持 `4,012,493` 条唯一显式候选，并保留 `animation_bake_cache`。仍有一个明确断点：

- 绝大多数显式候选卡在模型 Avatar 元数据：`inspect_missing_humanoid_avatar` 约 `2,047,615` 条，其中 `missing_internal_solver` 约 `1,996,810` 条候选 / `11,322` 个模型，`missing_avatar_pose_metadata` 约 `43,114` 条候选 / `78` 个模型，`missing_avatar_object` 约 `7,691` 条候选 / `2,752` 个模型。下一步应优先刷新这些模型的 Avatar/internalSolver，而不是做骨骼数量 fallback 或全量模型×动画猜测。

`humanoidSolverDiagnosis.restOffsetCandidateReadiness` 会把 learned first-frame correction 和所有确定性元数据候选做聚合排名。`status=no_production_candidate_yet` 表示当前没有可迁入生产的全局或分组公式。2026-06-13 的 NPC/Gorou 报告中，Hand / SpineHead 已有低 gap 候选，但 Arm / Leg 在两个模型上仍是弱分组；因此下一步应继续追 Arm/Leg 缺失的 Avatar/rest 空间来源，而不是把 Hand/SpineHead 的局部候选推广到全身。

`humanoidSolverDiagnosis.armLegCorrectionPattern` 会把左右 limb pair 单独摘要到顶层诊断。2026-06-13 的 NPC/Gorou 对照显示：Arm 在两个模型上都更像 `mirror_space_likely_but_candidate_missing`，最坏 pair 都是 `forearm`，说明左右镜像空间关系存在但还没有被当前 `rest/avatar/preQ/postQ` 候选解释；Leg 在 NPC 上接近 `stored_candidate_explains_group`，但 Gorou 仍是分段混合状态。下一步应先追 Arm 的左右镜像 Avatar/rest 来源，再拆 Leg 的 upper/lower/foot 分段规则，不能把一个单一全局 rest offset 写进生产 solver。

完整细项在 `internalHumanoidSolverVariantComparison.currentResidualStability.armLegCorrectionPattern`。其中 `crossSideMirroredCandidateReadyPairCount` / `maxCrossSideMirroredCandidateGapDegrees` 会把一侧 learned correction 和对侧候选的镜像形式比较，用来判断“缺的公式是否只是对侧 rest/avatar 候选镜像”。NPC/Gorou 当前结果是：NPC 的 Leg `4/4` 个 pair 低于阈值，但 Gorou 的 Leg 只有 `2/4`；两个模型的 Arm 都只有 `1/3`，且 forearm 的 cross-side gap 仍在 40 度以上。因此 Arm 的问题不能靠简单左右互换或现有候选镜像解决，后续需要继续让 Unity probe 暴露更底层的 Avatar pose、parent chain 或 internal skeleton space。

`parentChainNearestPairCount` / `nearestUsesParentChainCandidate` 用来判断 learned correction 是否更接近父 pose、父本地 pose 或父子 pose delta。2026-06-13 的 NPC/Gorou 对照显示：NPC 的 Arm/Leg 都是 `0`，Gorou 只有 Arm `1/3`、Leg `1/4`，且 forearm / lowerLeg 最大 gap 仍分别约 61 度 / 14 度。也就是说 parent-chain 候选目前只是局部线索，不足以作为生产 solver 公式；下一步应让 Unity probe 导出更直接的 Avatar internal skeleton 参考矩阵或 bind/rest 到 human/avatar skeleton 的映射关系。

`unityTransformLocalPoseReferenceSummary` 会比较 Unity 普通 `Transform.localPosition/localRotation` 快照、`HumanPoseHandler.GetInternalAvatarPose` 和 glTF rest。2026-06-13 重新跑 NPC/Gorou oracle 后确认：SetHumanPose 前的 Unity Transform.local 与 glTF rest 基本一致，最大旋转误差约 `0.04°`、translation 为 `0`；SetHumanPose 后的 Unity Transform.local 与 InternalAvatarPose 也基本一致，最大旋转误差约 `0.04°`。真正的大差异发生在 SetHumanPose 前后，NPC forearm 约 `89°`，Gorou forearm 约 `91.5°`。这说明 glTF rest / 普通 Transform 本地空间不是主要问题，缺口集中在 AnimeStudio 离线复现 Unity `HumanPoseHandler.SetHumanPose` 时的 muscle -> local TRS 求解公式。

2026-06-13 又用原神 `Avatar_Girl_Sword_PlayerGirl + Ani_Avatar_Girl_Sword_PlayerGirl_Show_01` 做了非 NPC 交叉 oracle：内部 FastPreview 能生成 `257` 个 glTF channel、`21` 个核心身体骨骼 channel、`51` 条 muscle curve，但 Unity 对照仍报告 `humanoidSolverDiagnosis.status=solver_formula_wrong`、`currentGltfError.errorKind=delta_space_formula_mismatch`，Arm 最大误差约 `93.45°`、Leg 最大误差约 `26.82°`。这证明四肢扭曲不是 NPC_Male 单模型问题。`Avatar_Lady_Bow_Sara_Edit + Ani_Avatar_Lady_Bow_Sara_PlayNatlanKeyBoard01AS` 也可生成 `195` 个 glTF channel，但诊断是 `no_humanoid_muscle_curves`，只能说明显式 Unity 关系和 Transform/root/helper 轨道存在，不能拿来判断 Humanoid 身体公式好坏。

`singleMuscleProbeSolverComparison.transformVsInternalAvatarPoseProbeSummary` 会比较 Unity 单 muscle probe 造成的普通 `Transform.localRotation` delta 和 `HumanPoseHandler.GetInternalAvatarPose` delta。PlayerGirl `Show_01` 新 oracle 中两者 `matchedTrackCount=123`、`maxDegrees≈0.056°`、`avgTrackMaxDegrees≈0.012°`，Arm/Leg 分组最大也只有约 `0.04°`。2026-06-13 补跑 NPC_Male `Alert01AS` 和 Gorou `Standby` 后，NPC 为 `matchedTrackCount=122`、`maxDegrees≈0.040°`、`avgTrackMaxDegrees≈0.014°`，Gorou 为 `matchedTrackCount=123`、`maxDegrees≈0.056°`、`avgTrackMaxDegrees≈0.013°`；但同一批 glTF 对 Unity oracle 的当前误差仍分别约 `175.12°` 和 `84.70°`。这说明 Unity 暴露的 InternalAvatarPose 与实际 Transform.local 目标空间一致，后续不要再把主要问题归因到 glTF rest 或 InternalAvatarPose/Transform 空间差异；应集中修 AnimeStudio 离线 muscle 轴映射、乘法顺序和左右镜像规则。

`singleMuscleProbeSolverComparison.singleMuscleDeltaCorrectionSummary` 会对每个单 muscle probe 计算“当前离线 delta -> Unity 真实 delta”所需的 correction quaternion，并按骨骼/身体分组看它是否稳定。2026-06-13 的 NPC_Male `Alert01AS` 结果为 `rowCount=90`、`maxCurrentErrorDegrees≈43.64°`、`maxCorrectionConsistencyByHumanBoneDegrees≈60.50°`；Gorou `Standby` 为 `rowCount=97`、`maxCurrentErrorDegrees≈54.52°`、`maxCorrectionConsistencyByHumanBoneDegrees≈73.59°`，两个样本顶层 `fixedCorrectionLikely=false`。这说明同一骨骼内不同 muscle/value 需要的 correction 差异很大，不能只靠“每根骨头补一个固定 rest offset”修正；下一步应继续拆 muscle 轴映射、swing/twist 组合顺序和左右镜像规则。

`singleMuscleProbeSolverComparison.singleMuscleDeltaAxisSummary` 会把 Unity 真实单 muscle delta 和当前离线 delta 都转成 axis-angle，分开看“旋转轴方向”和“旋转角度幅度”。2026-06-13 的 NPC_Male `Alert01AS` 为 `rowCount=90`、`maxAxisErrorDegrees≈23.75°`、`avgAxisErrorDegrees≈5.57°`、`avgAngleRatio≈0.98`；Gorou `Standby` 为 `rowCount=97`、`maxAxisErrorDegrees≈28.05°`、`avgAxisErrorDegrees≈10.75°`、`avgAngleRatio≈0.98`。两个样本都显示角度幅度基本接近 Unity，但四肢尤其 Arm/Hand/Leg 的旋转轴方向明显偏掉；因此下一步优先追 Avatar/local 轴基、左右镜像和 `preQ/postQ` 乘法空间，而不是先调 muscle limit 或整体角度缩放。

`singleMuscleDeltaAxisSummary.basisCandidateSummary` 会把当前单 muscle delta 再套一层确定性轴基候选，例如 `preQ`、`postQ`、`preQ*inverse(postQ)`、它们的 inverse 和简单 quaternion mirror，然后再和 Unity delta 对比。2026-06-13 的 NPC_Male `Alert01AS` 和 Gorou `Standby` 结果都显示 `identity` 是全局与 Arm/Leg 分组最佳候选：NPC 全局 `identity avgRotationError≈7.08°`，最近的 `zero_pre_inverse_post_sandwich≈27.61°`；Gorou 全局 `identity≈11.19°`，最近的 `zero_pre_inverse_post_sandwich≈27.80°`。这说明错误不是“当前 delta 已对了，只是输出后要再包一层 pre/post/mirror”；应回到生成 delta 的 muscle 轴定义、左右轴方向和 swing/twist 组合本身。

`singleMuscleDeltaAxisSummary.avatarAxisProjectionSummary` 会把当前 delta 和 Unity 真实 delta 反投影到候选 Avatar middle 空间，再统计最接近的带符号 `+X/-X/+Y/-Y/+Z/-Z` 轴。2026-06-13 补跑 NPC_Male `Alert01AS` 和 Gorou `Standby` 后，最佳投影模式下 `sameSignedAxisRate≈1.0`，说明大问题不是简单的 X/Y/Z 交换或左右正负号反了；但 Arm/Leg 仍有约 `18°-28°` 的轴偏斜，且 `avgAngleRatio≈0.98`。下一步应重点反推 Unity muscle 轴在 human skeleton / Avatar `preQ/postQ` 中的真实倾斜定义，以及 swing/twist 如何组合，而不是改成全局角度缩放或再包一层输出校正。

`singleMuscleDeltaAxisSummary.avatarAxisProjectionSummary.expectedAxisFitSummary` 会在各 projection mode 下，把 `+1/-1` probe 折回正方向后，检查 Unity 真实 middle-space 轴是否等于当前 muscle 语义期望的 Avatar 轴。2026-06-13 的 NPC_Male `Alert01AS` 和 Gorou `Standby` 都把最佳模式指向 `post_delta_inverse_post`，并在 `signPolicyCandidateSummary` 中稳定标出若干跨模型反号候选，例如 `RightLowerArm / Right Arm Twist In-Out`、`LeftUpperArm / Left Arm Down-Up`、`RightLowerLeg / Right Upper Leg Twist In-Out`。这些是下一轮完整 timeline 变体的候选证据：应先用同一套 sign policy 在 NPC/Gorou 完整动画帧上复验，确认 `max/avgTrackMaxDegrees` 稳定下降后，才允许迁入生产 Humanoid solver。

完整 timeline 复验结果显示，不能把这些单 muscle 的稳定反号直接迁入生产。`sign_policy_common_inverts` 在 NPC_Male `Alert01AS` 中把 Arm 最大误差从约 `70.25°` 放大到约 `151.83°`，Gorou `Standby` 中从约 `84.70°` 放大到约 `167.20°`。同一轮还测试了 `post_projected_delta_left/right`：left 基本等同 current，right 明显变差。结论是：`post_delta_inverse_post` 和反号候选只说明单 muscle probe 的投影诊断空间，不等同于完整动画的绝对 local rotation 公式。下一步不应继续直接翻曲线或重包一层 postQ，而应把 attention 放回 twist 分配、source bone、Avatar twist 参数和 parent/child delta 组合。

后续完整 timeline 又拆分了 twist 分配和 Arm Twist source/target 轴。`twist_split_foreArm_unity_child_param` 在 Gorou `Standby` 中有小幅改善，Arm 最大误差约 `84.70° -> 81.77°`，但 NPC_Male `Alert01AS` 变差，Arm 最大误差约 `70.25° -> 77.89°`；`twist_split_arm_*`、`twist_split_upperLeg_*`、`twist_split_leg_*` 也没有跨模型稳定改善。单 muscle 交集提示的 `arm_twist_target_y_side_source`（左前臂 Z->Y，右前臂 X->Y）在完整 timeline 中同样变差：NPC Arm 最大误差约 `70.25° -> 86.67°`，Gorou 约 `84.70° -> 93.31°`。因此 source/target 轴或 twist share 的局部枚举还不足以解释前臂主误差；下一步应检查 Unity 对多个 muscle 同时作用时的非线性组合、父子骨骼耦合和 AvatarConstant 更底层参数，而不是把单 muscle 最优轴直接迁入生产。

2026-06-13 又补充了完整 timeline 曲线消融变体，用来判断四肢扭曲是否来自某条曲线被错误套用。NPC_Male `Alert01AS` 与 Gorou `Standby` 都显示：移除 `Arm Twist`、`Forearm Stretch`、或同时移除两者都会让 Arm 误差变大；移除上臂 swing 也不能改善前臂主误差。腿部同样不是“删掉某条腿部曲线”能修好：`leg_no_stretch` 会把 NPC/Gorou Leg 最大误差分别放大到约 `69.55°` / `69.14°`，`upper_leg_no_swing`、`foot_no_twist` 也没有跨模型改善。结论是当前问题不是单条 muscle 曲线多余，而是 Unity 同时组合 swing/twist/stretch 时的轴空间、父子分配或非线性求解公式尚未复现。

新版 Unity oracle 还会在 `probeMuscles=true` 时输出 `muscleCombinationProbes` / `internalAvatarPoseMuscleCombinationProbes`，专门测试四肢相关 muscle 同时作用时的 Unity 黑盒结果。2026-06-13 对 NPC_Male `Alert01AS` 与 Gorou `Standby` 的组合 probe 显示，最坏项稳定集中在 `leg_twist_stretch_neg`：NPC 小腿最大误差约 `65.00°`，Gorou 右小腿约 `88.10°`、左小腿约 `73.28°`；手臂最坏项稳定集中在 `arm_twist_forearm_stretch_neg`，NPC 前臂约 `55.47°`，Gorou 前臂约 `45°-53°`。这进一步证明问题不是单 muscle 轴稳定性或简单删曲线，而是 twist + stretch / swing + twist 同时作用时的组合空间没有复现。下一步应优先枚举 `UpperLeg Twist + LowerLeg Stretch` 和 `Arm Twist + Forearm Stretch` 的组合公式：父/子骨骼写入顺序、是否先按 source limit 求 delta 再投到 child local、以及 stretch 是否实际影响的是相邻骨骼局部旋转而不是普通 X 轴旋转。

组合 probe 的 `variantRanking` 会把同一批 Unity 黑盒组合结果同时喂给多个诊断 solver 变体。2026-06-13 的 NPC/Gorou 复验里，`distal_limb_static`（临时移除 lower-arm/lower-leg 上的 twist+stretch 对）在两个模型上都明显优于 current：NPC 组合最大误差约 `65.00° -> 22.29°`，Gorou 约 `88.10° -> 35.73°`。这不能作为生产修复，因为完整动画仍需要这些曲线提供真实运动；它只说明当前公式在 distal 子骨骼上把 twist+stretch 组合成了过大的局部旋转。后续生产公式应反推 Unity 是否在组合时做了抵消、归一化、父子分摊或把 stretch 解释为 TranslateDoF/长度约束，而不是把这些曲线从 glTF 动画里删除。

同一轮又加入 `distal_pair_grid_tXX_sYY` 二维网格诊断，分别缩放 distal proximal twist 和 stretch。结果没有任何非零缩放组合超过 `distal_limb_static`：NPC 最好的非零 grid 是 `distal_pair_grid_t50_s100`，最大误差仍约 `50.74°`；Gorou 最好的非零 grid 是 `distal_pair_grid_t25_s100`，最大误差仍约 `54.65°`。反号类 `distal_pair_invert_*` 更差。这个负结果基本排除“给当前 twist/stretch 乘一个固定比例或简单反号”的修复路线；下一步应直接反推 Unity 的 distal 组合机制，例如 stretch 是否不是旋转、twist 是否先被父骨骼吸收/抵消，或 Unity 是否在同一 limb 链上做了约束求解。

组合 probe 还会记录 `translationSummary`，用于检查 Unity 黑盒结果是否在 Transform.localPosition 或 InternalAvatarPose T.xyz 上产生了位移。2026-06-13 对 NPC_Male `Alert01AS` 和 Gorou `Standby` 重新采样后，两个模型的 `internalAvatarPoseMuscleCombinationProbes=20`，但 `translationTrackCount=0`。这说明本轮最坏的 `leg_twist_stretch_neg` / `arm_twist_forearm_stretch_neg` 不是因为 Unity 输出了额外 translation 而我们漏写 glTF translation channel；应继续追旋转组合空间、父子骨骼分配、AvatarConstant twist/stretch 参数和可能的约束抵消，而不是把问题归因于 TranslateDoF。

组合 probe 的 `distalResidualCorrectionSummary` 会继续计算“当前离线 delta -> Unity 真实 delta”所需的 residual correction，并检查它在同一 distal 骨骼或同一 probe family 内是否稳定。2026-06-13 的 NPC_Male `Alert01AS` 中，distal 行数为 `16`、最大当前误差约 `65.00°`，同骨骼 correction 一致性最差约 `84.84°`；Gorou `Standby` 中最大当前误差约 `88.10°`，同骨骼一致性最差约 `101.26°`，`leg_twist_stretch` family 一致性最差约 `154.02°`。这个负结果排除“给 lower-arm/lower-leg 套一个固定 residual rotation / rest correction”的修复路线；下一步应直接反推 Unity 在同一 limb 链上如何组合 twist、stretch、swing，尤其是父子写入顺序、source/target limit 选择和是否有按 limb 链归一化或抵消。

组合 probe 的 `unitySingleMuscleCompositionSummary` 会把 Unity 单 muscle probe 的 delta 按所有简单乘法顺序组合，再和 Unity 组合 probe 对比。2026-06-13 的 NPC_Male `Alert01AS` 与 Gorou `Standby` 都显示，`arm_twist_forearm_stretch` 可以被单 muscle delta 组合到 `0°`，`leg_twist_stretch` 最大也只有约 `0.04°`，`foot_twist_chain` 最大约 `3.3°`。这条正证据非常重要：Unity 的相关 limb 组合基本是“先得到每个 muscle 的真实 delta，再按稳定顺序相乘”，不是本轮怀疑的复杂非线性链约束。下一步应回到单 muscle 公式本身，重点反推 Arm Twist、Upper Leg Twist、Forearm/LowerLeg Stretch 的真实轴、limit source 和 `preQ/postQ` 中间空间；只要单 muscle delta 对齐，组合动作大概率会自然收敛。

完整 timeline 也可以用 `oracleSingleMuscleTimelineRebuildSummary` 做更强的诊断：它把 Unity 单 muscle probe 当成 oracle 查表，按 decoded muscle 曲线逐帧插值，然后把同一骨骼上的 muscle delta 组合后和 Unity `InternalAvatarPose` timeline 对比。2026-06-13 的 NPC_Male `Alert01AS` 中，当前 solver 的 Arm/Leg focus 最大误差约 `70.25°`、平均约 `25.13°`；oracle 查表重建后最佳模式 `rightDelta_prepend` 降到最大约 `32.83°`、平均约 `4.97°`，其中 Arm 最大仅约 `3.94°`、Hand 最大约 `1.77°`，剩余主要在小腿。Gorou `Standby` 中，当前 focus 最大误差约 `84.70°`、平均约 `34.37°`；oracle 查表重建后最佳模式 `leftDelta_prepend` 降到最大约 `30.46°`、平均约 `3.51°`，Hand 最大约 `0.90°`，剩余也主要在右小腿和 clavicle。

同一报告会写 `clampedSampleCount` / `clampedMuscles`，用于判断 oracle 查表是否因为 probe 范围不足而被夹取。NPC_Male `Alert01AS` 的 `clampedSampleCount=266`，其中左右 `Lower Leg Stretch` 分别被夹取 `79` 帧；Gorou `Standby` 的 `clampedSampleCount=207`，右 `Lower Leg Stretch` 被夹取 `57` 帧，左右 Shoulder Down-Up 也各 `57` 帧。这说明剩余约 `30°` 的腿部误差很可能不是单 muscle 查表路线失败，而是当前 Unity probe 只采 `[-1,-0.5,0,0.5,1]`，无法覆盖真实动画中的 stretch/shoulder 曲线范围。下一轮应扩展 probe 幅度或按实际 decoded 曲线范围自适应采样，再验证 oracle timeline 是否进一步降到可接受范围；生产公式仍应从 AvatarConstant 推导，不能依赖 oracle 表。

2026-06-13 已把 Unity helper 的单 muscle probe 改成“固定宽范围 `[-2,2]` + 当前动画实际曲线 min/max”的自适应采样。NPC_Male `Alert01AS` 的 `clampedSampleCount` 从 `266` 降到 `0`，oracle 查表重建的最佳 timeline 误差降到最大约 `8.92°`、平均约 `1.77°`，其中 Leg 最大约 `4.29°`、Arm 最大约 `3.96°`；Gorou `Standby` 的 `clampedSampleCount` 从 `207` 降到 `0`，最佳 timeline 误差降到最大约 `4.36°`、平均约 `0.81°`，其中 Leg 最大约 `1.02°`。这条结果确认：四肢扭曲主要来自 AnimeStudio 当前单 muscle delta 公式没有复现 Unity，而不是 Unity 组合公式不可测或必须依赖 Unity bake。下一步应把 oracle 单 muscle probe 反推成 AvatarConstant 驱动的确定性公式，再生成新的 glTF 预览样本验证。

`internalHumanoidSolverVariantComparison.zeroBaseForearmPairTimelineFitSummary` 现在也会把 LowerArm 和 LowerLeg 一起纳入“zero-base + distal Stretch/proximal Twist 成对组合”的完整 timeline 诊断，并用 `pairFamily=arm/leg/any` 标出 Unity 组合 probe 的适用域。2026-06-13 的 NPC/Gorou 交叉复验显示，LowerLeg 的枚举对 NPC 很强：左右小腿可从约 `5°-6°` 降到约 `1.9°`；但 Gorou 仍只到左小腿约 `10.2°`、右小腿约 `15.7°`，且 `unity_probe_leg_sign_policy_*` 在右小腿只带来约 `0.1°` 改善。因此 Unity 黑盒测试可以继续用来反推公式，但这批 lower-leg pair 候选仍是诊断线索，不是生产 solver 规则。下一步应把已验证有效的自适应单 muscle oracle 结果压缩为 AvatarConstant 可解释的轴/乘法顺序公式，再用不同模型和动画生成 glTF 预览复验。

`singleMuscleDeltaAxisSummary.signedAxisTiltSummary` 会把 `+1/-1` 单 muscle probe 按符号折回同一个方向，并按 `humanBone + attribute` 聚合 Unity 真实轴和当前 solver 轴。2026-06-13 的 NPC_Male `Alert01AS` 显示 `groupCount=46`、`maxAxisTiltDegrees≈23.75°`、`avgAxisTiltDegrees≈5.46°`、`maxUnityAxisSpreadDegrees=0`；Gorou `Standby` 显示 `groupCount=49`、`avgAxisTiltDegrees≈12.21°`、`maxUnityAxisSpreadDegrees=0`。这说明 Unity 真实轴在单 muscle 正负 probe 中是稳定的，可以继续用 oracle 反推通用公式；当前最大稳定偏差集中在 Arm Twist、Arm Front-Back/Down-Up、Hand、Upper Leg In-Out 等四肢 muscle。Gorou 的 `Left Lower Leg Stretch` 出现约 `84.9°` 轴差，且最近轴从当前 `+X` 对到 Unity `-Y`，应先按 stretch/TranslateDoF 特例单独追踪，不要混入普通旋转 muscle 公式结论。

`singleMuscleDeltaAxisSummary.signedAxisTiltSummary.axisSourceCandidateSummary.byBodyGroupAndMuscleFamily` 会把稳定轴偏差继续按身体分组和 muscle family 聚合，例如 Arm/Twist、Arm/FrontBack、Leg/InOut、Leg/Stretch。`nextAxisWork` 只看 Arm/Leg 并给出下一步公式 probe 建议。2026-06-13 的 NPC_Male `Alert01AS` 与 Gorou `Standby` 交叉报告都把最坏 family 指向 `Arm/Twist`：NPC 平均未解释轴偏差约 `23.75°`，Gorou 约 `27.39°`，且 Unity 轴 spread 都为 `0`。这说明下一轮应集中用 Unity 单 muscle probe 枚举 Arm Twist 的 source long bone/current bone、target bone、本地/父本地乘法顺序和左右 mirror，再用完整 timeline 复验；不能把某个局部 axis hint 直接迁入生产 solver。

`singleMuscleProbeSolverComparison.singleMuscleMultiValueLinearitySummary` 会使用新版 Unity helper 的多幅度 probe（例如 `-1/-0.5/0/0.5/+1`）检查同一个 muscle 在不同输入值下的角度斜率和轴稳定性。报告会按 `muscleName + targetPath` 计算 `angle / abs(value-baseValue)`，再和 AvatarConstant `limitMin/limitMax` 的角度幅度比较；顶层 `focus*` 字段只看 Arm/Leg，避免 root/shoulder 联动的小角度噪声影响四肢公式判断。2026-06-13 修正了该诊断的限位来源：Forearm/LowerLeg/UpperLeg Twist 这类曲线写到远端骨骼时，幅度应从来源长骨的 X/twist limit 读取，而不是从 Hand/Foot 的目标轴读取。修正后 NPC_Male `Alert01AS` 与 Gorou `Standby` 的 `focusLimitExplainedRate` 都从 `0.643` 提升到 `0.857`，Forearm/LowerLeg/UpperLeg Twist 的正负斜率基本能被 AvatarConstant limit 解释；这进一步确认当前大误差主要集中在 source/target 轴、左右镜像和 swing/twist 组合空间，而不是全局角度缩放。

`formulaHintSummary.topAxisSpaceBlockers` 会把“限位幅度已解释、Unity 单 muscle 轴稳定，但当前公式仍明显错误”的行单独列出来。2026-06-13 对 NPC_Male `Alert01AS` 和 Gorou `Standby` 的交集显示，共同阻塞点集中在 `Left/Right Arm Twist In-Out -> LowerArm` 与 `Left/Right Arm Front-Back -> UpperArm`。这说明下一步应优先反推手臂的 Avatar/local 轴空间公式；Gorou 中局部腿部/脚部条目还需继续观察，但不能让它们掩盖跨模型共同的手臂主误差。单模型中看似改善的 `upper_y_to_x_mirror_xy_right` 等 Arm Twist 候选，在 NPC 完整 timeline 中不 beat current，因此仍只能作为诊断候选，不能迁入生产 solver。

`singleMuscleProbeSolverComparison.formulaReadinessSummary` 会把 `formulaHintSummary`、多幅度 `linearity` 和 signed Unity axis 稳定性合成一个公式工作队列。`ready_for_formula_probe` 表示当前误差大、limit/axis 稳定、枚举候选已低于 `5°`，可以进入下一轮完整 timeline 公式 probe；`needs_axis_formula_probe` 表示证据稳定但候选仍在 `5°-20°`，说明方向对但公式还没闭合；这些都不是生产 solver 规则。2026-06-13 的 NPC/Gorou 交叉汇总写入 `humanoid_axis_report_compare_common_formula_readiness.csv`，最稳定的共同项是 `RightLowerArm / Right Arm Twist In-Out` 和 `LeftLowerArm / Left Arm Twist In-Out`：两个模型的 limit/axis 稳定率都是 `1.0`，Unity 真实轴分别稳定为 `+X/-X`，但最佳枚举误差仍约 `12.6°/15.9°`，因此下一步应专门做 Arm Twist 的 source/target 轴与乘法空间 timeline probe，不能直接迁入 FastPreview。

同日又把 readiness 共同候选转成完整 timeline 变体 `readiness_arm_twist_left_z_right_x_to_y_*`：左前臂按 `source Z/swing -> target Y/swing`，右前臂按 `source X/twist -> target Y/swing`。NPC_Male `Alert01AS` 和 Gorou `Standby` 复验都是负结果：NPC Arm Twist timeline 从 current 约 `70.25°/69.60°` 变差到约 `86.67°/82.33°`，Gorou 从约 `84.70°/82.86°` 变差到约 `93.31°/92.06°`。这说明 `formulaReadinessSummary` 只能决定“下一步查哪里”，不能把单 muscle 的 source/target 轴直接搬到完整动画；后续应继续追 single-muscle delta 到 full timeline 的 zero/base 组合、Forearm Stretch 联动和乘法空间。

`partialOracleLowerArmTimelineReplacementSummary` 会只针对 LowerArm 做局部 oracle 替换：分别把 `Arm Twist`、`Forearm Stretch`、或两者一起替换成 Unity 单 muscle delta，其余仍走当前 lower-arm 公式。2026-06-13 的 NPC/Gorou 交叉结果非常明确：只替换 `Arm Twist` 不稳定，NPC 会从约 `70.25°` 变差到约 `104.49°`，Gorou 只小幅改善到约 `79.17°`；只替换 `Forearm Stretch` 两个模型都变差；但同时替换 `Arm Twist + Forearm Stretch` 并用 `rightDelta_prepend` 组合时，NPC/Gorou 的 LowerArm 最大误差都降到约 `0.056°`。这说明前臂主问题不是单条曲线，也不是 Unity 组合不可测，而是 `Forearm Stretch` 与 `Arm Twist` 的单 muscle delta 必须用同一套 Unity base/delta 空间一起组合。下一步生产公式应优先反推这两个 single-muscle delta 的 AvatarConstant 公式和 `rightDelta_prepend` 的 base 空间，而不是继续把当前 stretch base 与 oracle/current twist 混搭。

同一摘要的 `baseAlignmentSummary` 会把这个完美 partial oracle 使用的 Unity `baseQ` 与 deterministic base 候选比较，包括 `currentZero`、Avatar/rest pose、cross-side mirror pose 以及它们和 `currentZero` 的左右组合。NPC/Gorou 交叉结果显示，当前 `currentZero` 与 oracle base 的差距约 `90°-112°`，但已有 deterministic 候选可把 NPC 压到约 `1.34°`，Gorou 压到约 `2.25°-3.40°`。候选名在两个模型上还未统一成同一条短公式，常见成分包含 `crossSideAvatarDefaultMirrorXY*`、`avatarDefault*`、`avatarSkeletonDefault*`、`parentRest/currentZero`。这说明 lower-arm oracle base 不是不可解释黑盒；下一步应把这些近似候选归约成跨模型统一的 AvatarConstant base 公式，再与 `rightDelta_prepend` 的 ArmTwist+ForearmStretch delta 一起做完整 glTF 预览复验。

2026-06-13 又给 `baseAlignmentSummary` 增加了 `candidateFamilySummary`、`bestBaseCandidateFamily`、`bestBaseCandidateFactorCount` 和 `bestBaseCandidatePortability`，用于区分“短公式候选”和“长乘积疑似过拟合”。用完整 Avatar 元数据重跑 NPC/Gorou 后，NPC 的共同 family 是 `currentZero+crossSideAvatarDefaultMirrorXY+avatarDefault+local`，最大误差约 `1.34°`；Gorou 左右手分别落在 `currentZero+crossSideAvatarDefaultMirrorXY+avatarDefault+humanSkeleton+outerInverse` 与 `currentZero+crossSideAvatarDefaultMirrorXY+avatarSkeletonDefault+parentRest+local`，最大误差约 `3.40°`。这些结果都被标为 `medium_candidate_needs_reduction`：说明 Unity 公式可以通过黑盒测试继续反推，但还不能迁入生产 solver。特别注意：如果 NPC 只传 `ComboProbe` 自带的简化 request，而不传 `PoseMetadataRequest` 的完整 Avatar/HumanDescription 元数据，同一诊断会退化到约 `22°`，这是缺元数据假负例，不是公式结论；Humanoid request 缺完整 Avatar 元数据时应重新生成索引/请求，不允许回退到骨骼数量或名称猜测。

同日继续加入 `reducedCandidateFamilySummary`，专门测试“只用一个或两个可解释 pose 基元 + currentZero”是否足够复现 lower-arm oracle base。结果是明确负例：单基元 reduced 候选在 NPC/Gorou 上仍有约 `29°-66°` 误差；二基元 reduced 候选仍有 NPC 约 `25°-30°`、Gorou 约 `15°-22°` 误差，而完整三基元候选仍维持 NPC 约 `1.34°`、Gorou 约 `3.40°`。这说明当前 lower-arm base 不能压缩成简单的“一条 cross-side pose”或“两条 pose 相乘”公式；下一步应保留三基元结构，继续判断这些基元是否正对应 Unity AvatarConstant 的固定字段和左右手公式，而不是为了生成预览把二因子近似迁入生产 solver。

随后新增 `bestBaseCandidateExpression` / `bestBaseEffectiveRoleSignature`，把候选名中的外层 `inverse(...)` 和嵌套乘法展开成实际有效因子顺序。NPC_Male `Alert01AS` 的三基元可解释为：左前臂 `currentZero * avatarDefault.local * crossDefault.oppositeToTarget * crossMirrorDefault.oppositePose`，右前臂 `currentZero * crossMirrorDefault.oppositeLocal * crossMirrorDefault.targetToOpposite * avatarDefault.pose`。Gorou `Standby` 则分别落在 `inverse:avatarDefault.parentPose * inverse:avatarDefault.parentPose * crossMirrorDefault.targetToOpposite * inverse:currentZero` 和 `currentZero * inverse:crossMirrorDefault.oppositeToTarget * avatarSkeletonDefault.parentLocal * inverse:parentRest`。这些签名说明“有效候选都围绕 cross-side mirror/default pose + currentZero”，但左右手、模型之间的因子顺序和字段来源尚未统一，所以仍是反推线索，不是可迁入生产的公式。下一步应把候选限制到 Unity AvatarConstant 明确字段组合，做按侧/按 Avatar skeleton index 的模板 probe，而不是继续让无约束长乘积挑最优。

`templateCandidateGrid` 会把目前四个三基元模板交叉套到 NPC/Gorou 的左右前臂，验证这些模板是否具备跨模型/跨侧稳定性。2026-06-13 的结果是负例但很有定位价值：NPC 左模板和右模板在 NPC 对应侧能维持约 `1.34°`，Gorou 左模板和右模板在 Gorou 对应侧能维持约 `2.25°/3.40°`；但 NPC 模板套到 Gorou、Gorou 模板套到 NPC，或左右侧互套，误差通常会放大到约 `74°-171°`。这说明模板不是全局公式，也不是简单按 Left/Right 复用；它们很可能反映了 Avatar skeleton index、human skeleton index、parent/local pose 在不同 Avatar 元数据布局下的选择差异。下一步应新增“按 AvatarConstant 索引来源选择模板”的 probe，例如 same-index、human-bone-index、avatar-skeleton-parent/local 的离散选择，而不是把当前四个模板按模型名写死。

`scripts\Compare-HumanoidAxisReports.ps1` 会在 `partialOracleLowerArmTimelineReplacement.lowerArmBaseMigrationGate` 写入前臂 base 迁入门禁。只有跨至少 `3` 个报告的 portable template 或 index-selection base 低于阈值，并且后续完整 timeline glTF 验证通过，才能考虑进入生产 solver。2026-06-13 用 NPC/Gorou/PlayerGirl 三模型 index-selection probe 重跑后，门禁为 `not_ready_no_portable_template`：最佳 index-selection base 仍约 `6.94°/7.06°`，超过 `5°` 阈值；固定 template 跨模型更差，约 `99°-104°` 以上。这条 gate 的作用是防止把“只在来源模型/侧有效”的模板误写进 FastPreview。

`indexSelectionCandidateSummary` 会在上述模板基础上只替换 Unity AvatarConstant 的索引来源和 pose 字段，例如 same-index、human-skeleton-index、avatarSkeletonDefault、parent/local；它不做自由长乘积搜索，也不改变生产 solver。2026-06-13 的 NPC/Gorou 复验显示，这个 probe 能复现四个旧模板的低误差：NPC 左/右前臂仍约 `1.34°`，Gorou 左前臂约 `2.25°`，Gorou 右前臂约 `3.40°`。更重要的是，它把模型名模板改写成字段来源差异：NPC 使用 `avatarDefaultLocalPoseBySameIndex` / `avatarDefaultPoseBySameIndex`，Gorou 左前臂使用 `avatarDefaultHumanSkeletonIndexParentPose`，Gorou 右前臂使用 `avatarSkeletonDefaultSameIndexParentLocalPose + parentRest`。随后加入 PlayerGirl `Show01` 作为第三模型，新增 `inverse(currentZero) * avatarDefault local * inverse(localRest) * avatarDefault parentLocal` 和 `currentZero * parentRest * crossMirror oppositePose * localRest` 两类受限候选后，PlayerGirl 从约 `59°-81°` 降到约 `6.94°-7.06°`。三模型横向表明：受限候选已覆盖多种 Avatar 布局，但 NPC、Gorou、PlayerGirl 的最佳 role signature 仍不一致；下一步要找的是“由 Avatar 元数据布局决定的字段选择器”，而不是迁入某个固定候选。只有该选择器在更多模型上稳定，并通过完整 timeline glTF 预览复验，才能考虑进入内部 Humanoid solver。

外部资料也支持继续走本地 oracle 反推路线：Unity `HumanPoseHandler` 文档只公开 `Get/SetHumanPose`、`Get/SetInternalAvatarPose` 这类入口，`HumanPose.muscles` 只说明 muscle 值按 Humanoid Rig 的 `[min,max]` 范围移动骨骼；Unity C# reference 中 `HumanPoseHandler` 的创建、Get/Set 内部姿态都绑定到 native `AnimationBindings`，没有公开转换公式。因此生产 solver 仍应以 Unity oracle + AvatarConstant 元数据交叉验证为依据，而不是等待官方 C# 公式或从社区脚本复制 Unity API 调用。

`scripts\Compare-HumanoidAxisReports.ps1` 读取这些报告时固定使用 UTF-8。报告中包含中文诊断说明，Windows PowerShell 5.1 默认 ANSI 读取无 BOM UTF-8 会把字符串读坏，严重时让 `ConvertFrom-Json` 报“字符串未闭合”一类解析错误；遇到这类错误应先检查读取编码，不要误判为报告 JSON 写坏。超大的 Humanoid oracle 报告建议用 PowerShell 7 的 `pwsh` 运行比较脚本；在 Windows PowerShell 5.1 中，深层对象管道可能让 `partialOracleLowerArmTimelineReplacement` 相关 CSV 为空。

`internalHumanoidSolverVariantComparison.armAxisSpaceMigrationGateSummary` 会把 Arm Twist、UpperArm Swing 和二者组合的完整 timeline 候选合并成一个迁入门禁。2026-06-13 对 NPC_Male `Alert01AS` 的结果是 `not_ready_axis_space_unresolved / no_full_timeline_candidate_beats_current`，组合最佳候选平均只改善约 `0.38°` 且最大误差仍约 `70.49°`；Gorou `Standby` 的结果是 `not_ready_axis_space_unresolved / candidate_improvement_too_small`，组合最佳候选平均改善约 `0.65°`、最大误差仍约 `84.08°`。这说明当前枚举还没有找到可以迁入正式 solver 的手臂轴空间公式；下一步要继续反推 Unity 单 muscle delta 的 Avatar/local 轴基，而不是扩大现有候选或把单模型小幅改善写入生产导出。

`singleMuscleProbeSolverComparison.armFormulaWorkQueueSummary` 会把 `formulaHintSummary`、multi-value limit 解释和 axis-stable 结果压成手臂公式工作队列，只用于决定下一轮 oracle 反推顺序，不会改变生产 solver。2026-06-13 的 Gorou patched request 中，摘要状态为 `arm_twist_axis_space_unresolved`，`armHintRowCount=10`、`groupCount=5`、`armTwistGroupCount=2`。其中 `Right/Left Arm Twist In-Out -> LowerArm` 都满足 `limitExplainedRate=1.0`、`axisStableRate=1.0`，但 `sourceChangedRate/sourceAxisChangedRate/targetAxisChangedRate` 都是 `1.0`，下一步明确是 `derive_arm_twist_axis_space_from_avatar_pose_or_limit_axis`。`Left Arm Front-Back -> LeftUpperArm` 也满足 limit 和轴稳定，但 source axis 候选发生变化，应走 `derive_upper_arm_swing_axis_tilt_from_avatar_pose`。这些 gate 仍然必须用 NPC/Gorou 等多模型完整 timeline 复验后，才能迁入正式 Humanoid solver。

`internalHumanoidSolverVariantComparison.armFormulaTimelineGateSummary` 会把上面的 single-muscle 工作队列和完整 timeline 候选对齐。2026-06-13 的 Gorou patched request 中，`upper_y_to_x_mirror_xy_right` 与 `upper_y_to_x_twist_first_mirror_xy_right` 都能 beat current，但平均改善只有约 `0.77°` / `0.36°`，迁移状态仍是 `not_ready_improvement_too_small`。这条 gate 把“局部 single-muscle 方向正确”和“完整动画还不够好”明确分开：Arm Twist 的 axis-space 仍是下一步反推对象，但不能因为 Gorou 单模型小幅改善就迁入生产 solver。

`signedAxisTiltSummary.axisSourceCandidateSummary` 会继续检查 Unity 真实轴是否能由现有元数据的简单候选解释：当前 solver 轴、最近 canonical Avatar 轴、以及 canonical 轴经过 `preQ`、`postQ`、`preQ*inverse(postQ)` 正反 sandwich 后的方向。2026-06-13 的 NPC_Male `Alert01AS` 中 `current_predicted_axis` 仍占 `39/46` 组，Gorou `Standby` 中占 `35/49` 组；Arm Twist、Arm Front-Back、Hand 和 UpperLeg In-Out 等最坏项仍有 `14°-28°` 候选误差。这是重要的负证据：仅靠已有 `preQ/postQ/zero` 的简单轴变换不能统一解释 Unity 真实 muscle 轴。下一步应索引或反推 Unity `HumanLimit` / AvatarConstant 中更底层的轴、center、axisLength、stretch/twist 参数，而不是把某个当前候选硬迁入生产 solver。

`axisSourceCandidateSummary.poseAxisCandidateStatus` 会把 AvatarConstant 的 `humanSkeletonPose`、`avatarDefaultPose`、local pose、parent/child pose 轴候选单独统计。2026-06-13 对 Gorou `Standby` 的结果显示 pose 类候选命中 `37/49` 组，Arm 命中 `11/12` 组，说明 Unity 的真实轴确实经常受 Avatar/rest 父子空间影响。早期 NPC_Male `Alert01AS` 报告曾显示 `0/46` 命中，但复查发现该 oracle request 来自旧模型元数据，`humanSkeletonPose/avatarDefaultPose/avatarSkeleton` 都是空数组；用 `AvatarPoseCandidates/NPC_Male_Alert01AS` 的完整 Avatar 元数据重建 request 后，NPC pose 类候选恢复到 `37/46`，Arm `12/12`。因此 `0/46` 是缺元数据导致的假负例，不是公式结论。生成 Humanoid Unity oracle request 时，如果模型既没有 `HumanDescription.skeletonBones`，又缺 `avatar.internalSolver.skeleton.humanSkeletonPose/avatarDefaultPose`，CLI 会拒绝生成请求并要求先刷新 Avatar 元数据。

多个 Unity oracle 对比报告可以用 `scripts\Compare-HumanoidAxisReports.ps1` 做横向比较。它只读取 `--compare_unity_bake_result` 生成的 JSON，不修改素材库，也不新增模型-动画关系：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Compare-HumanoidAxisReports.ps1 `
  -ReportPath @(
    "D:\Assets\AS-Assets\YuanShen-Assets\AnimationPreviewUserSamples\unity_bake_compare_report_pose_axis_npc.json",
    "D:\Assets\AS-Assets\YuanShen-Assets\AnimationPreviewUserSamples\unity_bake_compare_report_pose_axis_gorou.json"
  ) `
  -OutputDir "D:\Assets\AS-Assets\YuanShen-Assets\AnimationPreviewUserSamples\HumanoidAxisCompare_NPC_Gorou"
```

输出的 `HUMANOID_AXIS_REPORT_COMPARE.md`、`humanoid_axis_report_compare_summary.json` 和 CSV 会列出共同 axis family、共同单 muscle 轴阻塞点、以及 `formulaHintSummary.topAxisSpaceBlockers` 的跨模型交集。2026-06-13 的 NPC/Gorou 对比显示，去重后的共同 axis-space 阻塞只剩四个手臂核心项：`Left/Right Arm Twist In-Out -> LowerArm` 与 `Left/Right Arm Front-Back -> UpperArm`，且 `allLimitExplained=true`、`allAxisStable=true`。这进一步收窄了下一步：先反推手臂 Avatar/local 轴空间条件，再回到腿/脚局部问题。

`humanoid_axis_report_compare_axis_space_details.csv` 会把共同阻塞点在每个报告里的 `predictedAxis`、`unityAxis`、最近 signed axis、off-axis 分量和 best source/target hint 放到同一张表；`humanoid_axis_report_compare_off_axis_patterns.csv` 会进一步按共同 muscle 聚合 Unity 真实轴分量。2026-06-13 的 NPC/Gorou 明细显示：Arm Twist 的 Unity 真实轴仍以 `Left=-X` / `Right=+X` 为最近轴，但稳定带负 `Z` 分量（NPC 约 `-0.4026`，Gorou 约 `-0.45`）；Arm Front-Back 的 Unity 真实轴仍以 `-Z` 为最近轴，但左右手带相反 `X` 分量（NPC 约 `±0.3319`，Gorou 约 `+0.2723/-0.2460`），Gorou 左臂还带明显 `Y≈-0.3783`。这说明下一步公式不应改最近 signed axis，而应解释 Unity 如何从 Avatar/rest pose 计算这些稳定 off-axis 倾斜。

`humanoid_axis_report_compare_arm_formula_timeline_gate.csv` 会把每个报告里的 `internalHumanoidSolverVariantComparison.armFormulaTimelineGateSummary.rows` 展开；`humanoid_axis_report_compare_common_arm_formula_timeline_gate.csv` 只保留多个模型共同出现的候选公式，并统计 `allCandidateFound`、`allBeatsCurrent`、误差和改善幅度。它同样只是 Unity 黑盒反推诊断：共同改善说明该公式值得继续推导，不能直接迁入生产 Humanoid solver。

`humanoid_axis_report_compare_leg_formula_timeline_gate.csv` 会把 `legFormulaTimelineGateSummary.rows` 展开；`humanoid_axis_report_compare_common_leg_formula_timeline_gate.csv` 只保留多个模型共同出现的腿/脚候选。这个 gate 使用 Leg bodyGroup 指标，避免 Arm 的大误差遮住腿部真实变化。2026-06-13 的 NPC/Gorou 复验显示，`LowerLegDistalStretch` 在两个模型上都保持 current，平均 track max 约 `11.02° / 14.19°`，没有改善；Gorou 的 `LegFullBodyGroup` 上 `twist_split_unity_parent_param` 有小改善，平均约 `9.68° -> 8.77°`、最大约 `26.07° -> 15.83°`，但 NPC 没有同候选改善，门禁仍是 `not_ready_no_leg_timeline_candidate / not_ready_improvement_too_small`。因此腿/脚当前不能迁入新公式；下一步应继续追 zero/base pose 和完整组合顺序，而不是只改 LowerLeg stretch 轴或简单 twist 参数。

`humanoid_axis_report_compare_zero_base_source_candidate_gate.csv` 会展开每个报告的 `zeroBaseSourceCandidateGateSummary.rows`；`humanoid_axis_report_compare_common_zero_base_source_candidate_gate.csv` 只保留跨模型共同 `humanBone + bodyGroup + formulaFamily`。它用于区分“短乘法已经能解释 Unity zero base 的稳定来源”和“仍然只是在单模型上碰巧改善”。当前 NPC/Gorou 共同候选集中在 Shoulder/Hand/Foot 的 `rest` 族；新增 cross-side mirror 诊断后，`RightLowerArm` 也出现跨模型共同 `avatarDefaultPose` 族正证据，`formulaSources=crossSideShortProduct`。这仍然只是公式反推门禁，下一步必须做完整 timeline 和实际 glTF 预览验证，而不是继续用骨骼数量或截图兜底判断动画是否匹配。

`internalHumanoidSolverVariantComparison` 里带 `oracle_pattern_*` 名称的 variant 只用于验证 off-axis 发现是否能解释完整 timeline，不能进入生产 solver。2026-06-13 的 NPC/Gorou 复验显示，`oracle_pattern_frontback__right_delta` 对 UpperArm Front-Back 在两个模型上都改善了时间线误差：NPC 的 UpperArm 平均 track max 从约 `48.19°` 降到 `45.58°`，Gorou 从约 `61.34°` 降到 `55.72°`；但 `oracle_pattern_arm_twist_local_delta_*` 会让 Arm Twist 明显变差，NPC 从约 `69.60°` 升到 `79°-81°`，Gorou 从约 `82.86°` 升到 `89°-90°`。组合门禁仍是 `not_ready_axis_space_unresolved / candidate_improvement_too_small`，说明 off-axis 方向是有效线索，但不能把 oracle 常量或简单 local delta 写进正式导出。下一步应只沿 UpperArm Front-Back 的 Avatar/rest 推导继续收敛，同时为 Arm Twist 追查它是否需要 source/target 长骨空间、父子乘法顺序或 twist 分配，而不是直接套本地倾斜轴。

`upperArmFrontBackPoseAxisTimelineFitSummary` 会把 UpperArm Front-Back 的 Avatar pose/local/parent-child pose 候选提升到完整 timeline 上复验，避免只看单 muscle 轴。2026-06-13 用完整 Avatar pose 元数据重建 NPC request 后，NPC 与 Gorou 的最佳候选都指向 `humanSkeletonChildToParentPose__left_delta`：NPC 可把 UpperArm 平均 track max 从约 `70.35°` 降到 `36.68°`，Gorou 可从约 `81.28°` 降到 `45.20°`。这是比 oracle 常量更强的正证据，说明 UpperArm Front-Back 的 off-axis 倾斜很可能可以从 `humanSkeletonPose` 的父子空间推导。不过这份 NPC 重建报告暂时复用了旧 Unity result，最终迁入生产 solver 前仍要用新 request 重新跑 Unity bake/oracle，并确认 Arm Twist 与腿部不会被同一变体拖坏。

`armAxisSpaceMigrationGateSummary` 现在也会把 `pose_axis_frontback_humanSkeletonChildToParent_left_delta` 纳入 UpperArm + ArmTwist 组合门禁。2026-06-13 的 NPC/Gorou 复验显示，这个确定性 Avatar pose 候选比 oracle 常量更强：NPC 组合平均 track max 从约 `58.89°` 降到 `53.09°`，Gorou 从约 `72.10°` 降到 `63.65°`。但两个模型的最大误差仍分别约 `70.49°` / `84.08°`，门禁结果仍是 `not_ready_axis_space_unresolved / candidate_error_too_high`。结论是：UpperArm Front-Back 公式方向已经明显收敛，下一步应专攻 `Arm Twist In-Out -> LowerArm` 的真实 source/target 轴和父子空间；不能只把上臂 pose-axis 规则单独迁入生产 solver，否则视觉上前臂仍会明显扭曲。

`armTwistTimelineAxisFitSummary` 也包含若干 `pose_axis_arm_twist_*` 诊断候选，用来验证“把 LowerArm twist 轴按 Avatar/rest pose 倾斜后再写本地 delta”是否能解释 Unity。2026-06-13 的 NPC/Gorou 复验显示，这条路暂时不成立：Gorou 上 `pose_axis_arm_twist_avatarDefaultParentToChild_left_delta` 平均 track max 约 `83.45°`，比当前 `82.86°` 更差；NPC 上这些 pose-axis Arm Twist 候选没有进入前列。也就是说，pose 元数据能解释一部分单 muscle 轴偏斜，但不能直接当完整前臂 twist 时间线公式。下一步要继续用 Unity oracle 枚举 source long bone、target lower-arm、本地/父本地乘法顺序和左右 mirror，而不是把简单 pose-axis LowerArm twist 迁入生产 solver。

2026-06-13 进一步加入了 `single_formula_*` 和 `single_formula_zero_*` Arm Twist 变体：前者把单 muscle 拟合出的 AvatarConstant delta 叠到 Forearm Stretch base 上，后者从 `preQ * inverse(postQ)` 零姿态开始组合 Forearm Stretch 与 Arm Twist 的 left/right delta。NPC/Gorou 结果都没有超过 current；最佳 `single_formula_zero_right_append_*` 与 current 数值完全重合，LowerArm 仍约 `70°-85°`。这说明 `singleMuscleFormulaCandidateSummary` 里 Arm/Twist 近 `0°` 的单 muscle 拟合不能直接推出完整 timeline 公式，原因很可能在 base pose、left/right delta 选择、或多 muscle delta 的零点/顺序诊断还不够细。Unity oracle lookup 本身仍能把 LowerArm 压到约 `0.06°` 以内，所以正确路线不是放弃黑盒反推，而是继续把 oracle 的每条 single-muscle delta side、baseQ 和组合顺序显式记录出来，再对 AvatarConstant 公式逐项对齐。

`singleMuscleFormulaDeltaSideSummary` 会把单 muscle 公式的 `left_to_left`、`right_to_right`、`left_to_right`、`right_to_left` 拆开统计，避免历史 `min(left,right)` 把真实 side 关系藏起来。2026-06-13 的 NPC/Gorou 结果显示：Arm Twist 单 muscle 仍可在 side-specific 下达到约 `0°-0.04°`，Forearm Stretch 在 NPC 约 `0.8°`，Gorou 左右前臂约 `6.6°/12.7°`；这解释了为什么单 muscle 层看似正确，但完整时间线仍会错。随后补充的 `single_formula_mixed_*` 允许 Forearm Stretch 和 Arm Twist 独立选择 predicted delta side，再按 left/right 输出组合；NPC/Gorou 仍与 current 完全重合，没有降低 `70°-85°` 的 LowerArm 完整 timeline 误差。下一步应直接比较 Unity probe 的 `baseQ` 与 AnimeStudio 的 `preQ * inverse(postQ)`/当前 zero pose，而不是继续只枚举 delta side。

`singleMuscleFormulaDeltaSideSummary.gateSummary` 会把每个 `targetHumanBone + muscleName` 的最佳 side-specific 单 muscle 公式压成一行，供 `Compare-HumanoidAxisReports.ps1` 做跨模型共同门禁。2026-06-13 的 NPC/Gorou 新对比显示：`Left/Right Arm Twist In-Out` 和 `Left/Right Forearm Twist In-Out` 都是共同 `ready_for_timeline_formula_test`，最大误差约 `0.04°-0.14°`；但 `Left Forearm Stretch` 共同最大约 `6.60°`，`Right Forearm Stretch` 共同最大约 `12.72°` 且 Gorou 为 `not_ready`。结论是：前臂扭曲不能继续笼统归因于 Arm Twist 单 muscle delta；下一步要优先扩展 Forearm Stretch 的 Unity probe、source/target 轴、limit 缩放和左右镜像诊断，同时继续用 zero/base pose gate 验证完整 timeline。

`forearmStretchScaleProbeSummary` 会专门枚举 Forearm Stretch 的 value/angle scale 候选，包括 `AvatarConstant.m_Human` 里的 `twist.armStretch=0.05`、`twist.foreArm=0.35`、`1±参数`、反号和倒数。2026-06-13 的 NPC/Gorou 对比显示，这些缩放都没有超过当前 `scale=current=1`：NPC 左/右前臂最佳仍约 `0.803°/0.802°`，Gorou 左/右前臂仍约 `6.596°/12.723°`，共同 `minImprovementDegrees=0`。结论是：Gorou Forearm Stretch 剩余误差不是简单 Unity twist/stretch 标量缩放导致；后续应继续追 source/target 轴空间、左右镜像和 zero/base pose 组合，而不是把 `armStretch` 或 `foreArm` 直接当固定修正系数迁入生产 solver。

`distalStretchTimelineFitSummary` 现在额外包含 `pose_axis_stretch_*` 诊断候选，会用 `avatarDefaultPose`、`humanSkeletonPose`、`avatarSkeletonPose` 等 rest/avatar 姿态去倾斜 Forearm / Lower-Leg Stretch 轴，再分别测试 `left_delta`、`right_delta` 和中间轴分解。2026-06-13 的 NPC/Gorou 对比结果是强负证据：当前 `current_stretch_z_pos_vector_swing_twist` 在 NPC/Gorou 平均 track max 约 `40.31°/48.53°`，而最接近的共同 pose-axis 候选 `pose_axis_stretch_inverse_avatarDefaultPoseByHumanSkeletonIndex__middle_swing_twist` 反而升到约 `52.59°/100.12°`，共同 `allBeatCurrent=false`。结论是：Avatar/rest pose 轴倾斜能解释一部分单 muscle 静态误差，但不能直接作为完整时间线的 Forearm Stretch 公式；下一轮应继续追 zero/base pose 与多 muscle 动态 delta 的组合空间，而不是把 naive pose-axis Stretch 迁入生产 solver。

`singleMuscleProbeBasePoseSummary` 会直接比较 Unity single-muscle probe 的 `baseRotation/baseQ` 与 AnimeStudio 当前零姿态 `preQ * inverse(postQ)`。2026-06-13 的 NPC/Gorou 结果给出了强正证据：NPC 的 Left/RightLowerArm baseQ 与 current zero 相差约 `111.95°`，Gorou 的 LowerArm 相差约 `90°-93°`；同一 muscle 多个 probe 的 `maxBaseSpreadDegrees` 接近 `0°`，说明 Unity baseQ 本身稳定，不是采样噪声。`zeroMuscleResidualCorrectionSummary` 同时显示 LowerArm zero-muscle 误差仍约 `69.91°`（NPC）和 `82.54°-85.76°`（Gorou）。结论是：完整 LowerArm timeline 的 `70°-85°` 主误差主要来自零姿态/基准姿态没对齐 Unity，而不是 Arm Twist delta 或 left/right delta side。下一步应专攻从 Avatar/rest pose 元数据推导 LowerArm zero correction，并先在 `zeroMuscleResidualCorrectionSummary` gate 通过后，再迁入生产 Humanoid solver。

`residualCandidateTimelineFitSummary` 现在会额外加入 `unityZeroBase*inverse(preQ*inverse(postQ))` 与 `inverse(preQ*inverse(postQ))*unityZeroBase` 两个 oracle-only 候选。它们直接来自 Unity zero-muscle `InternalAvatarPose`，只能作为“如果 base pose 正确，完整 timeline 最多能改善多少”的上限诊断，不能作为生产导出依赖。2026-06-13 的 NPC/Gorou 复验显示，这个 zero-base oracle 可以把 NPC UpperArm 从约 `48°` 压到约 `0.5°`、LowerArm 从约 `69°-70°` 压到约 `8.5°-15.7°`；Gorou RightLowerArm 从约 `84.7°` 压到约 `13.4°`、LeftLowerArm 从约 `81.0°` 压到约 `11.4°`。这证明 Unity 黑盒探针足以验证公式路线：大扭曲确实主要来自 base pose / zero anchor，而不是必须长期依赖 Unity bake。当前已有 `avatarDefaultPose` / `humanSkeletonPose` / `avatarSkeleton` / `rest` 候选仍不能低误差解释 LowerArm zero correction，下一步应扩展 Unity 侧 AvatarConstant/limit/base-pose 元数据导出，找到 `unityZeroBase` 的确定性来源后再迁入生产 Humanoid solver。

`zeroBaseCandidateGateSummary` 会把 `residualCandidateTimelineFitSummary.focusedLimbPerBoneCandidateSummary` 提升成门禁视图：`unityZeroBaseOracle` 只表示 Unity 黑盒上限，`rest`、`localRest`、`avatarDefaultPose`、`humanSkeletonPose`、`avatarSkeletonPose` 等才是可迁入生产前继续验证的确定性元数据候选。2026-06-13 的 NPC/Gorou 横向对比显示，`LeftLowerLeg|rest` 是当前唯一跨模型确定性共同候选，NPC/Gorou 最佳误差约 `5.76° / 7.77°`；而 `Left/RightLowerArm`、`Left/RightUpperArm` 都稳定依赖 `unityZeroBaseOracle`，LowerArm 可从约 `69°-85°` 压到约 `8.5°-15.7°`。结论是：腿部部分骨骼已经能从 rest/localRest 路线继续推进；手臂主扭曲仍必须先把 `unityZeroBase` 追溯到 AvatarConstant/rest 元数据，不能把 oracle offset 直接写进生产 solver。

`zeroMuscleShortProductSearchSummary` 会进一步只用已导出的 `preQ/postQ/rest/localRest/humanSkeletonPose/avatarDefaultPose/avatarSkeletonPose` 等确定性元数据，枚举 1-3 项 quaternion 短乘法，检查是否能重建 Unity zero-muscle base pose。2026-06-13 的 NPC/Gorou 结果显示：Foot 常可被 `rest` 精确解释，腿部多数组合能压到约 `0°-3°`，UpperArm 可压到约 `6°-9°`；但 LowerArm 不稳定，NPC 左/右前臂最佳仍约 `18.3°/14.0°`，Gorou 左前臂约 `7.0°`、右前臂仍约 `37.0°`，且最佳公式族左右和跨模型都不一致。结论是：短乘法可证明已有 Avatar/rest 元数据解释了一部分 zero base，但前臂仍缺 Unity 内部基准、左右侧规则或未导出的 AvatarConstant 字段；不能把这些短公式当生产规则过拟合上线。

`zeroBaseSourceCandidateGateSummary` 会把 `zeroMuscleShortProductSearchSummary` 的短乘法结果提成更直接的“zero base 来源候选”门禁，按 `rest`、`localRest`、`parentRest`、`avatarDefaultPose`、`humanSkeletonPose`、`avatarSkeletonPose` 等公式族汇总。2026-06-13 的 NPC/Gorou 复验显示，Shoulder/Hand/Foot 在两个模型上都有共同 `rest` 族候选，最佳误差可到 `0°`；新增 cross-side mirror 诊断后，`RightLowerArm` 首次出现跨模型共同 `avatarDefaultPose` 族候选，来源为 `crossSideShortProduct`，NPC/Gorou zero base 误差约 `5.78° / 2.92°`，相对当前零姿态分别改善约 `64.13° / 82.83°`。这是前臂公式的重要正证据，说明对侧 Avatar 默认姿态和左右镜像空间很可能参与 Unity zero base；但它仍只是 zero-muscle 静态门禁，必须继续进入完整 timeline 变体、更多模型和实际 glTF 预览验收后，才能迁入生产 Humanoid solver。

同日把 `crossSideShortProduct` 提升为完整 timeline 诊断 variant 后，`zero_base_cross_side_avatar_default_*` 的 left/right/sandwich 组合全部失败：NPC Arm 平均 track max 从 current 约 `44.59°` 升到 `102°-134°`，Gorou 从约 `58.36°` 升到 `112°-136°`，Leg 也被明显拉坏。这条负证据很关键：cross-side 公式确实能解释 Unity zero base 的一部分静态来源，但不能直接按 `pose * inverse(currentZero) * solved`、右乘或 sandwich 套到完整 Humanoid delta。下一步应继续反推 Unity 在该 zero base 下如何组合 Forearm Stretch / Arm Twist 的动态 delta，而不是把这个 static pose 公式迁入 FastPreview。

随后新增的 `crossSideZeroBaseDeltaFitSummary` 会只比较 delta，不重建最终姿态：把 Unity timeline rotation 分别表示为相对 cross-side base A/B 的 left/right delta，再和当前 solver 相对 `preQ * inverse(postQ)` 的 left/right delta 交叉比较。NPC/Gorou 结果仍然是负证据：NPC Arm 最好约 `91.25°`，Gorou Arm 最好约 `119.04°`，Leg 也常接近 `180°`。因此问题不是简单的“base 找到了但左右乘方向错了”；当前动态 muscle delta 本身仍在错误空间。下一步应回到 single-muscle / multi-muscle probe，继续推导 Forearm Stretch 与 Arm Twist 的 source/target 轴、组合顺序和左右镜像，而不是继续套 static zero-base pose。

`avatarInternalSolverMetadataSummary` 会在 Unity bake compare 报告里记录 request 是否带完整 `AvatarConstant.m_Human` 元数据。2026-06-13 的旧 NPC/Gorou request 都显示 `hasHumanBlock=false`、`leftHandBoneIndexCount=0`、`rightHandBoneIndexCount=0`、`humanBoneMassCount=0`，所以旧报告里的 LowerArm 负结论不能当作“这些 Unity 字段无关”。同日用当前导出器对 `Avatar_Boy_Bow_Gorou` 做定向刷新，临时输出 `AvatarHumanMetaSmoke_Gorou` 中已写入 `avatar.internalSolver.human`：左右手索引各 `15` 个，`humanBoneMass=25`。再用刷新后的 Avatar 元数据 patch Gorou oracle request 后，报告确认 `hasHumanBlock=true`、左右手索引各 `15` 个、`humanBoneMassCount=25`，但现有 `zeroMuscleShortProductSearchSummary` 仍没有消费这些字段，右前臂最佳 zero-base 短公式仍约 `36.98°`。这说明当前负结论只覆盖既有短乘法候选，不代表 `m_LeftHand/m_RightHand/m_HumanBoneMass` 对 Unity 前臂公式无关；下一步应把这些字段显式加入 oracle 公式枚举和 side/mirror 诊断。

`zeroMuscleShortProductSearchSummary` 现在会单独报告 `bestHandContext*`，这些候选来自 `AvatarConstant.m_LeftHand/m_RightHand.m_HandBoneIndex` 指向的手链 pose，只作为诊断，不进入三重短乘法枚举，避免候选数爆炸。2026-06-13 的 Gorou patched request 显示 hand context 对右前臂只有弱改善：右前臂 best short formula 约 `36.98°`，best hand context 约 `34.06°`；左前臂 best short formula 约 `6.95°`，hand context 约 `21.89°`，更差。结论是：手链 pose 本身不是前臂 zero base 主公式。下一步应继续用 Unity oracle 测 `humanBoneMass`、twist/stretch 参数和 left/right mirror/axis 规则，而不是把 hand context 直接迁入生产 Humanoid solver。

`avatarInternalSolverParameterSummary` 会把 `AvatarConstant.m_HumanBoneMass`、twist/stretch 参数和 zero-muscle 误差并排写入报告，方便判断某个 Unity 标量是否能解释四肢主误差。2026-06-13 的 Gorou patched request 显示：`arm=1.0`、`foreArm=0.35`、`upperLeg=1.0`、`leg=0.35`、`armStretch=0.05`、`legStretch=0.05`；左右前臂 `humanBoneMass` 都是 `0.01818182`，twist role 都是 `arm:child,foreArm:parent`，但 zero error 仍约 `82.54°/85.76°`。左右小腿也同样 mass/role 对称。因此这些标量本身不是单独的 zero-base 修复公式；后续应把它们作为“组合公式参数”参与 single-muscle delta 反推，而不是直接按 mass 或 twist role 给每根骨头补固定 offset。

`zeroMuscleShortProductSearchSummary` 现在还会为 LowerArm/LowerLeg 增加 `weightedParameter(...)` 诊断候选，把 `humanBoneMass`、`twist.foreArm/leg`、`armStretch/legStretch` 等 Unity 参数当作旋转插值权重，去测试 rest/avatar/cross-side pose 是否能重建 Unity zero base。2026-06-13 的 NPC/Gorou 复验显示，这条路没有跨模型共同正证据：Gorou 前臂 best short-product 已降到 Left/Right 约 `3.67°/2.92°`，而 best weighted parameter 仍约 `15.68°/11.82°`；NPC 旧 request 缺完整 `m_Human`，weighted 候选为空，但 short-product 也能到 Left/Right 约 `8.88°/5.78°`。结论是：`humanBoneMass` / twist / stretch 不能直接当 zero-base 旋转权重迁入生产 solver；下一步应把当前跨模型有效的 cross-side/avatar-default short-product 公式提升到完整 timeline gate，再检查它如何和 Forearm Stretch / Arm Twist 动态 delta 组合。

`zeroBaseSourceTimelineFitSummary` 会把 `zeroBaseSourceCandidateGateSummary` 里每根骨骼的最佳确定性 zero-base 公式放回完整 timeline，分别测试 `base_left_delta`、`left_delta_base`、`base_right_delta`、`right_delta_base` 四种动态 delta 组合。2026-06-13 的 NPC/Gorou 对比给出当前最强正证据：`RightLowerArm|base_left_delta` 跨模型都改善，NPC/Gorou max 从约 `70.25°/84.70°` 降到 `9.27°/6.78°`；`LeftLowerArm|base_right_delta` 也跨模型改善，从约 `68.95°/81.03°` 降到 `13.33°/9.11°`。这说明 cross-side/avatar-default zero base 确实击中了前臂大扭曲主因；但剩余 `6°-15°` 仍高于生产迁移门槛，小腿也不稳定（Gorou RightLowerLeg 仍变差），所以还不能迁入 FastPreview。下一步应在这个正确 zero-base 下继续反推 Forearm Stretch / Arm Twist 的动态 delta 轴和乘法顺序，而不是回到骨骼数量兜底或 Unity bake。

`zeroBaseArmTwistTimelineFitSummary` 会在上述 best zero-base 基础上，只替换 LowerArm 的 Arm Twist 动态候选，测试现有 ArmTwist 轴/乘法候选是否能继续压低剩余误差。2026-06-13 的 NPC/Gorou 对比显示，结论偏负：在已经有效的前臂组合上，`RightLowerArm|base_left_delta|current_lower_x_to_x` 仍约 `9.27°/6.78°`，候选 ArmTwist 只能在 Gorou 右前臂小幅到 `6.09°`，NPC 没有共同改善；`LeftLowerArm|base_right_delta` 也只从 `13.33°/9.11°` 小幅到约 `12.85°/8.01°`，没有达到可迁移门槛。共同表里虽然存在 `allBeatZeroBaseCurrent=true` 的行，但多是 `right_delta_base` 等本身 `80°-90°` 的坏组合变好一点，不能作为生产正证据。结论是：剩余前臂误差不应继续只扩 ArmTwist 轴候选；下一步更应把 Forearm Stretch 与 Arm Twist 在正确 zero-base 下作为成对动态 delta 重新组合验证。

`zeroBaseForearmPairTimelineFitSummary` 会在 best zero-base 下把 Forearm Stretch 与 Arm Twist 作为成对动态 delta 重新组合，枚举少量 `stretchFormula/twistFormula`、left/right delta、append/prepend 和输出侧。2026-06-13 的 NPC/Gorou 对比给出弱正证据：`RightLowerArm|base_left_delta|pair_*_prepend_*` 可跨模型到约 `7.48°/7.15°`，比 NPC 原 best zero-base 更好，但略差于 Gorou 原 `6.78°`；`LeftLowerArm` 最好仍约 `13.33°/9.11°`，没有突破。结论是：成对动态 delta 方向比只换 ArmTwist 更接近，但仍没有达到生产迁移门槛；下一步需要继续引入 Unity single/combination probe 的真实 delta 组合顺序，或增加更多模型验证，不能把当前 pair 候选直接写进 FastPreview。

新版 `axisSourceCandidateSummary` 会在模型带 `avatar.internalSolver.humanBoneLimits` 时，把 Unity `HumanDescription.m_Human[*].m_Limit` 的 `value/min/max/range/center` 方向也加入候选比较，并在 `humanLimitCandidateStatus` 中统计这些候选是否可用、命中多少组。旧 NPC/Gorou oracle 请求缺这个字段时，状态会是 `not_available`，不会把 HumanLimit 缺失误报成公式结论。要验证 HumanLimit 是否能解释真实轴偏斜，应先用 `Write-ModelAvatarRefreshPlan.ps1` 刷新对应模型 Avatar 元数据，再重新生成 Unity oracle request 和 compare 报告。

注意：原神部分 Avatar 只有 `AvatarConstant.m_Human` 内部 solver，没有可用的 Unity `HumanDescription.m_Human` limit。2026-06-13 对 `Avatar_Boy_Bow_Gorou` 做定向 Avatar 刷新后，`humanSkeletonPose` 和 `humanBoneIndex` 可完整补齐，但 `hasHumanDescription=false`、`humanBoneLimits=0`。因此内部 Humanoid solver 不能把 HumanLimit 当作必需输入；没有 HumanLimit 时仍要基于 `AvatarConstant` 的 skeleton axes、`preQ/postQ`、limitMin/limitMax、twist/stretch 参数和 Unity 黑盒 probe 继续反推公式。

如果旧库缺 `avatar.humanBones` 但已有 `avatar.internalSolver.humanBoneIndex`，Unity bake request 会从 internalSolver 派生 HumanBone 映射，并在 request 里写入 `humanBonesSource=avatar.internalSolver.humanBoneIndex`。这是 Unity Avatar 重建数据，不是模型-动画关系证据；模型-动画绑定仍必须来自 `relation_source=explicit` 候选。重新导出的模型会在 `avatar.internalSolver.skeleton` 中保留 `humanSkeletonPose` 和 `avatarDefaultPose`，它们来自 Unity `AvatarConstant` 的原始 pose，也是 bake/内部求解对齐 Unity 的必要输入。

当前边界：自动 prefab fallback 只重建骨架和 Avatar，用于正确采样 Humanoid 动画；它不是完整 Unity prefab 复原。模型、贴图、材质仍来自 AnimeStudio 已导出的 glTF。bake 合并时会把 Unity 采样结果转换回 glTF 本地坐标，并把 `*_AnimeStudioBake` 根节点 track 映射回原 glTF root，以保留 DASH 等 root motion。

### 迁移旧素材库为相对路径索引

旧版本索引可能在 `asset_catalog.jsonl`、`model_animations*.json`、`animation_bindings.jsonl`、`export_manifest.jsonl` 和 `library_index.db` 中保存素材库内部文件的绝对路径。目录改名或移动后，这些路径会失效。可以用迁移命令把内部资产路径改成以素材库根目录为基准的相对路径：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --migrate_library_relative_paths "D:\Assets\AS-Assets\HomuraHime-Assets"
```

迁移只处理素材库内部路径，例如 `Models/...`、`Animations/...`、`Textures/...`、`VFX/...`，不会改 Unity 源文件 `source` / CAB 溯源信息。执行后会自动重建 `library_index.db`，并写出 `relative_path_migration_summary.json`。

批量迁移可用：

```powershell
.\scripts\Migrate-ASAssets-RelativePaths.ps1
```

### Freedunk 富动作验证记录

2026-06-04 使用 `D:\Assets\Freedunk_Data_Dev\AnimationBindingV3_BodyTypes_RichActions_FixedIndex` 扩大了角色和动作覆盖：

- 角色体型样本：`Bill_01_00_ingame`、`Emma_01_00_ingame`、`Gary_01_00_ingame`、`Frorin_01_00_ingame`、`Uncle_01_00_ingame`、`Thompson_02_00_ingame`、`Qiqi_03_00_ingame`、`Kiara_01_00_ingame`。
- 动作包覆盖：`02_dribble`、`03_shot`、`04_receive`、`05_pass`、`06_rebound`、`07_block`、`08_steal`、`09_collision`、`11_hopstep`、`12_tipin`、`13_pivot`、`14_anklebreak`、`15_alleyoop`、`16_shakeandbake`、`19_etc`、`outgame/lobby/_universal/celebration`。
- 索引能识别到持球/运球、投篮/扣篮/上篮、传接球、篮板、盖帽/抢断、碰撞/受击、庆祝/入场等候选动作；这些候选仍然必须经过 bake 和 glTF 写入报告验证。
- `D:\Assets\Freedunk_Data_Dev\UnityBake_RichAction_BodyTypes_V1` 中，8 个角色的 `HOLDBALL_01` 和 Bill 的 `AIMSHOT_STANDING_01`、`DRIVINGDUNK_01`、`KNOCKDOWN_NORMALMOVE_01`、`CEREMONY_CHEER_Bill_01_LOBBY` 已通过 Unity bake。报告显示 `status=ok`、`InvalidTargetCount=0`、`Skipped=0`，说明当前 Humanoid AvatarPose 流程可以覆盖更丰富的身体动作。
- `face_happy` 当前失败，Unity 报告为 legacy clip 不能直接用于 Playables。表情/BlendShape 不能假装已被 Humanoid TRS bake 覆盖，后续需要单独处理 legacy AnimationClip、blendshape curve 和 glTF morph target channel。

非角色动画探针位于 `D:\Assets\Freedunk_Data_Dev\NonCharacterAnimationProbe_V1`：

- 当前能收集并导出球、奖杯/杯体、抽卡/舞台物件、篮筐/篮网相关动画等资源，`asset_summary.json` 中可见 `Ball`、`Prop`、`Stage`、`Character` 分类。
- `BlackBox`、`AnimationModel`、`Stage_ChouKa` 等 prefab 能从 Unity `Animator`/controller 建立候选关系，但直接套用当前 Unity bake 流程时 `changedTrackCount=0`。这说明非角色 Transform/legacy/材质/激活类动画需要独立完善采样和 glTF node path 映射，不能按 Humanoid 角色流程硬套。
- 非角色 Transform 预览必须证明动画影响可见内容：除了匹配 Unity binding path 和导出 node path，还要命中导出模型的 `meshPaths` 或 skinned mesh joint。只命中 `Camera`、`Dummy`、helper/socket 的动画只能保留为 `NonCharacterTransformNeedsMapping` 或辅助线索，不能让用户验证一个看不出变化的预览。
- 预览验证还会检查 glTF sampler 的时间跨度和输出变化量。`Stage_ChouKa/Idle` 这类虽然能写出 channel、但 `maxDuration=0` 且 `movingChannelCount=0` 的结果，应标记为静态姿态，不进入可播放动画库。
- 篮球、武器、道具等物件本身可以是静态模型；它们在游戏中的移动可能来自角色 socket、物理、代码事件或场景节点动画。只有当 AnimationClip 的绑定路径实际驱动导出的可见 mesh 或 skinned joint 时，才把它当作可播放动画。
- 显式 Unity `AnimatorController` / `Animation` 引用也要继续做 binding path 可见性分析。显式引用只能证明“这个模型会用这个 clip”，不能单独证明“导出的 glTF 里能看到这个 clip 的运动”。例如抽卡舞台的 clip 绑定路径可能是相对 Animator 根节点的 `Models/SprayCan`，需要匹配到导出节点 `Stage_ChouKa/AnimationModel/Models/SprayCan` 后才可预览。
- 后续做索引结构时，应把角色 Humanoid 动画、角色 Transform/BlendShape 表情动画、非角色 Transform 动画、材质/激活/事件类动画分开标注能力状态，避免素材库把“已收集”误显示成“已可播放验证”。

实现原则：

- 默认导出阶段可以收集动画线索，但不默认嵌入动画。
- 绑定关系通过 Unity 原生关系建立：Animator/Animation 组件、AnimatorController/OverrideController、Avatar、SkinnedMeshRenderer bones、AnimationClip binding path/type/property、PPtr 依赖。
- AnimationClip container、目录名、角色名、resourceKind 不作为默认绑定依据；只有在显式 fallback 模式中，才允许作为带标注的补充线索。
- 只有显式 `--animation_package Embedded` 或 `Both`，或后续专门的预览/打包命令，才把动画写进模型 glTF。
- `unity_relations.jsonl` 是后续动画绑定的上游关系图，包含 GameObject、组件、Animator、Controller、Avatar、SkinnedMeshRenderer、AnimationClip binding 等 Unity 原生关系。
- `unity_relation_summary.json` 是关系图摘要，适合快速判断当前样本有没有 Animator Controller、Avatar、Muscle Clip、skin bones 等关键关系。
- `animation_bindings.jsonl` 和 `model_animations.json` 目前是候选索引，不等于最终可播放验证结果；默认候选只应来自 Unity 显式引用或结构兼容关系，不能靠路径/名称猜测。
- 候选索引会记录 `relationSource`、`confidence`、`score`、`matchedBindingPaths`、`unmatchedBindingPaths`、`requiresHumanoidBake`。其中 `explicit_unity_reference` 表示来自 Animator/Animation 显式引用，`structural_unity_binding` 表示 AnimationClip binding path 与模型骨骼路径兼容，`structural_unity_avatar` 表示模型 Avatar 与 Humanoid/Muscle 动画结构兼容。
- 预览/打包命令会负责验证实际写入 glTF 后的 channel 数、skin/joint 和主体骨骼覆盖。

### 默认素材库命令

普通 Unity 游戏：

```powershell
cd D:\misutime\AnimeStudio

AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "<Unity_Data目录或资源目录>" `
  "<输出目录>" `
  --game Normal
```

这等价于：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "<Unity_Data目录或资源目录>" `
  "<输出目录>" `
  --game Normal `
  --mode Library `
  --group_assets ByLibrary `
  --profile_3d Core `
  --model_format Gltf `
  --texture_mode Png `
  --animation_package Separate `
  --fbx_animation Skip
```

输出结构大致为：

```text
<输出目录>\
  Models\
    assets\...
  Animations\
    assets\...
  Textures\
    _ModelDependencies\
  export_manifest.jsonl
  export_profile.jsonl
  unity_relations.jsonl
  unity_relation_summary.json
  model_validation.json
```

说明：

- `Models` 里的 glTF 不会默认嵌入 Animator Controller 的全局动作库。
- `Models` 里的角色/蒙皮模型会保留 skeleton/skin，方便后续绑定独立动画。
- `Animations` 里保存独立 AnimationClip，后续会继续增强为 animation-only glTF 和绑定索引。
- `Textures\_ModelDependencies` 保存共享 PNG 实体，模型目录里的 `Textures` 通常是硬链接。
- Shader 默认不导出；需要研究 shader 时显式使用 `--include_shaders`，会以 `.shader.raw` 和 `.shader.raw.json` 安全归档。
- `asset_catalog.jsonl` 记录模型、动画、实验 shader 的结构化索引和模型统计。
- `asset_summary.json` 汇总导出数量、资源分类和模型统计。
- `unity_relations.jsonl` 记录 Unity 原生关系，是后续从模型定位动画、Avatar、Controller、Material、Mesh 关系的主入口。
- `unity_relation_summary.json` 汇总关系数量和关键覆盖率，便于快速排查“为什么找不到动画关系”。
- `model_validation.json` 验证 glTF 模型、贴图、材质和 skin/joint 基础结构。动画排查前应先确认这个报告没有基础结构错误。
- `animation_bindings.jsonl` 按资源分类给独立动画列出候选模型，供后续绑定/验证流程使用。

### Freedunk 失败案例

不要再把下面这种命令作为默认素材库导出：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files (x86)\Freedunk\Game\Freedunk_Data" `
  "D:\Assets\Freedunk_Data_core_png_anim" `
  --game Normal `
  --mode Animator `
  --group_assets ByContainer `
  --profile_3d Core `
  --model_format Gltf `
  --texture_mode Png `
  --fbx_animation Auto
```

这个命令会从角色 Animator Controller 收集动画。Freedunk 的角色控制器引用了大量全局篮球动作，结果 `assets\ingame\prefabs\characters\Bill_01_00\Bill_01_00.gltf` 被写成“角色 prefab + 脸部/辅助节点 + 上千个通用动作”的混合包，文件巨大，也不适合浏览模型。

现在默认素材库导出会避免这种失败形态：模型入 `Models`，动画入 `Animations`，不把全局动作库重复塞进每个角色模型。

## 3D 导出模式

底层仍保留几个模式，主要用于特殊处理和排查：

- `Library`：默认素材库导出，推荐主路径。
- `SplitObjects`：只导模型，适合排查模型候选。
- `Animator`：按 Animator 导出，适合调试动画收集。
- `Export`：传统按类型转换，适合特殊资源研究。

### 模型模式

```powershell
--mode SplitObjects
--fbx_animation Skip
```

模型模式按 GameObject 导出模型。当前默认模型格式是 glTF：

```powershell
--model_format Gltf
```

可选：

```powershell
--model_format Gltf
--model_format Glb
--model_format Fbx
```

全量资源提取建议使用默认 `Gltf`。它会输出 `.gltf + .bin`，比 FBX 更适合批量导出、恢复导出和后处理。FBX 只建议在需要兼容旧流程或特定 DCC 工具时使用。

贴图模式：

- `--texture_mode Png`：默认值。解码并导出 `.png`，glTF 会直接引用 PNG。PNG 会先写入顶层共享目录 `Textures\_ModelDependencies`，再在模型目录的 `Textures` 子目录创建硬链接；这样模型目录便于单独查看，同时重复贴图不额外占用磁盘空间。
- `--texture_mode Raw`：导出 Unity 原始/压缩贴图数据为 `.rawtex`，旁边写 `.rawtex.json` 记录宽高、Unity TextureFormat、mip 数、源 asset/pathId、Unity 版本和平台；glTF 材质 `extras.unityTextures` 保留贴图引用。速度最快，适合特殊的大规模扫描和后续批量转换。
- `--texture_mode Reference`：只在 glTF 材质 `extras.unityTextures` 记录引用，不写贴图数据。最快，适合先扫模型结构。

### 贴图工作流和技术逻辑

默认素材库导出使用 PNG，因为目标是让模型目录可以直接查看。Raw 是特殊的高速扫描/后处理工作流：

1. 使用 `--texture_mode Raw` 做大规模快速扫描。
2. 先用 `.gltf/.glb` 浏览模型结构、命名、骨骼、网格和材质槽。
3. 看到值得保留的模型后，直接对该模型的 `.gltf` 执行 `--convert_model_textures`。
4. CLI 会读取 glTF 材质里的 `extras.unityTextures`，到顶层 `Textures\_ModelDependencies` 找对应 `.rawtex/.rawtex.json`，只转换这个模型实际用到的贴图。

这样做的原因：

- Unity 贴图常见格式是 DXT/BC/ETC/ASTC/Crunched 等 GPU 压缩或 Unity 原始数据。
- 全量导出时把这些贴图全部解码成 PNG 会非常慢；角色模型尤其明显，很多耗时都集中在 normal/baseColor/mask 等贴图解码上。
- Raw 模式只复制原始贴图数据，不做完整解码，因此模型导出速度和内存稳定性更好。
- `.rawtex` 不是 glTF 标准图片格式，所以 glTF 不会把它写进标准 `images/textures` 通道；否则很多查看器会报错或误读。
- Raw/Reference 模式会把贴图关系写入材质 `extras.unityTextures`，这是给后处理工具和人工排查用的保留信息。
- `--texture_mode Png` 仍然保留，用于小范围导出、需要 Blender/查看器直接显示贴图、或确认材质效果的场景。

Raw 模式输出文件：

```text
Textures\_ModelDependencies\xxx.rawtex
Textures\_ModelDependencies\xxx.rawtex.json
```

`.rawtex.json` 会记录：

- `format`：Unity `TextureFormat`，例如 `DXT1Crunched`、`DXT5`、`BC7`、`ASTC_6x6`。
- `width` / `height`：贴图尺寸。
- `mipCount`：mip 数量。
- `sourceAssetPath` / `sourceFileName` / `sourcePathId`：原始 Unity 资源来源，方便追踪重复贴图和问题贴图。
- `unityVersion` / `platform`：后处理解码需要的 Unity 版本和平台信息。
- `rawDataSize`：`.rawtex` 原始字节长度。
- `dataFile`：对应 `.rawtex` 文件名。

选中模型后单独转贴图：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --convert_model_textures "D:\Assets\Freedunk_Data_Dev\Freedunk_Data_library\Models\assets\ingame\prefabs\characters\Qiqi_03_00\Qiqi_03_00.gltf"
```

默认行为：

- 自动从 glTF 路径向上查找导出根目录，直到发现 `Textures\_ModelDependencies`。
- 输出到模型目录旁边的 `Textures` 子目录。
- 默认转成 PNG。
- 默认修改该 `.gltf` 的标准 `images/textures` 引用，让 Blender/预览器能直接加载贴图。

可选参数：

```powershell
--texture_asset_root "D:\Assets\Freedunk_Data_Dev\Freedunk_Data_library"
--texture_output "D:\Assets\Freedunk_Data_Dev\Freedunk_Data_library\Models\assets\ingame\prefabs\characters\Qiqi_03_00\Textures"
--texture_output_format Png
--update_gltf_texture_refs false
```

如果只想测试转换、不想改原 glTF，可以加：

```powershell
--update_gltf_texture_refs false
```

模型模式默认会过滤：

- `ui`
- `sound`
- `audio`
- `video`
- `emoji`
- `camera`
- `animations`

`animations` 在模型模式下默认排除，避免导出动画目录里的绑定对象、临时对象和重复 prefab。

### 动画模式

```powershell
--mode Animator
--fbx_animation Auto
```

动画模式按 Animator 导出。默认过滤：

- `ui`
- `sound`
- `audio`
- `video`
- `emoji`
- `camera`

动画模式不会过滤 `animations`。

如果希望把当前过滤范围里的全部 AnimationClip 都传给 FBX：

```powershell
--fbx_animation All
```

`All` 容易混入不相关动画，建议只在小目录测试。

### 全量性能参数

3D 模式默认启用受控批处理：

```powershell
--batch_files 16
```

`--batch_files` 表示每批加载多少个源资源文件及其依赖。默认值是 `16`。数值越大，重复依赖加载越少，但内存峰值越高；数值越小，内存更稳但总耗时可能增加。大游戏全量导出优先保持默认值，内存充足时再调高。

3D 模式还会按模型数量定期做一次完整 GC：

```powershell
--model_gc_interval 32
```

64GB 内存机器建议先保持默认 `16`；如果内存稳定且希望提高吞吐，可以调大到 `32` 或 `64`；如果观察到内存持续上升不回落，再调小到 `8`。传 `0` 可以关闭模型级完整 GC，只保留批次结束时的清理。

导出过程会写入：

```text
export_manifest.jsonl
asset_catalog.jsonl
asset_summary.json
animation_bindings.jsonl
model_animations.json
model_animations.compact.json
skeletons.json
model_validation.json
```

每成功导出一个模型追加一行 JSON，便于统计进度、排查中断位置和后续做恢复导出。

`asset_catalog.jsonl` 面向素材库浏览和自动验收。模型条目会记录资源分类、mesh 数、顶点数、材质数、贴图数、动画数、骨骼数、`skeletonHash` 和 `skeleton`。`skeletonHash` 是素材库骨架 ID；`skeleton` 里会记录 `namePathHash`、`hierarchyHash`、`bindPoseHash`、`avatarHumanHash`、`avatarSkeletonNameHash` 和 `relationBasis`，用于判断哪些模型/动画共享同一套 Unity 骨架或 Humanoid Avatar 关系。Humanoid 模型还会把 Avatar 的 `humanBones`、`humanBoneDetails`、原始 `skeletonBones` pose、HumanDescription 参数和 `avatar.internalSolver` 轴系数据带入 `model_animations.json`，供 AnimeStudio 内部 TRS 求解器和 Unity bake 对照诊断复建正确 Avatar 使用。

`avatar.internalSolver` 来自 Unity `AvatarConstant.m_Human`，包含 `humanBoneIndex`、Human skeleton node/axes、`preQ`、`postQ`、axis sign 和 muscle limit。新导出还会在 `avatar.humanBoneDetails` 和 `avatar.internalSolver.humanBoneLimits` 中保留 Unity `HumanDescription.m_Human[*].m_Limit` 的 `min/max/value/length/modified`，用于后续反推真实 muscle 轴、stretch/twist 和 HumanLimit 公式。它是后续离线 Humanoid/Muscle -> 目标骨架 TRS 求解的通用输入，不是游戏 profile 或名称猜测。
当游戏的 Avatar 没有 `HumanDescription` 时，内部求解器仍可通过 Unity `AvatarConstant.m_Human.m_Skeleton.m_ID` + `Avatar.m_TOS` 还原目标骨骼路径；如果这个确定性路径缺失，应报告缺口，不能退回骨骼数量或名称前缀兜底。

`AnimatorOverrideController` 需要按 Unity override pair 处理：被 override 的 original clip 不再作为该模型的默认候选，实际候选应使用 override clip；没有 override clip 的 pair 才保留 original clip。源索引会写 `animatorOverrideController.overrideSet` 和 `animatorOverrideController.clipPair`：前者说明替换表已被当前工具确认读取，`count=0` 时表示 Unity 明确给出了空替换表，可以确定性继承 base controller 动画；后者用于非空替换表，保留 `original -> override` 的精确替换关系。旧源索引如果只有 `baseController`、没有 `overrideSet/clipPair` 精确标记，Library SQLite 会跳过该 OverrideController 的 base controller 动画，避免把不完整关系伪装成精确候选。

Humanoid/Muscle 预览会把 `RootT.*` / `RootQ.*` 作为 root motion 写到 glTF scene root。Root motion 同样来自 decoded MuscleClip 曲线：默认以第一帧归零，避免把 Unity 场景世界偏移写进素材；如果缺少这些曲线，只报告缺口，不用骨骼数量或路径猜一个 root motion。

模型条目也会汇总材质状态，方便后续筛选：

- `materialStatus` / `materialStatusCounts`：模型 glTF 中材质状态汇总。
- `materialNeedsCustomizationTint`：需要 ColorMask/Tint 或 customization 配色。
- `materialMissingRendererBinding`：缺 Renderer 材质绑定、需要材质修补或后续关系索引增强。
- `materialHasBaseColorTexture` / `materialHasNormalTexture` / `materialImageCount`：判断模型是否已有标准贴图显示能力。

AnimationClip 条目会记录 `animationType`、`hasMuscleClip`、`coreTransformBindingCount`、`humanoidBindingCount`、`blendShapeBindingCount`、`trueBlendShapeBindingCount`、`rendererMaterialBindingCount`、`rendererPropertyBindingCount`、`activeStateBindingCount`、`auxiliaryBindingCount` 和 `classificationNotes`。这些字段用于判断动画是普通骨骼 TRS 曲线、Humanoid/Muscle 动画、真正的 BlendShape 动画、材质/Renderer/显隐动画，还是只作用在 socket/helper 上的辅助动画。`blendShapeBindingCount` 是兼容旧索引的粗略 SkinnedMeshRenderer 计数，默认能力判断应优先看 `trueBlendShapeBindingCount`。

`asset_summary.json` 是总览报告，记录模型、动画、实验 shader 的数量和模型是否带贴图、骨骼、morph。`model_validation.json` 是静态模型验收报告，检查 glTF image、texture、material、mesh accessor、skin joint、inverseBindMatrices 是否自洽。`animation_bindings.jsonl` 从动画视角列出候选模型，`model_animations.json` 从模型视角列出候选动画、匹配依据、匹配分数和下一步动作。`model_animations.compact.json` 是规范化紧凑索引：模型、动画、模型-动画候选引用分表保存，避免把完整动画对象重复写进每个模型。`character_assemblies.json` 记录模块化角色的 body/face/hair/accessory 候选和推荐组合；它只描述可组装关系，不改变默认 Library 中的原始模块输出。后续素材库浏览、搜索、批量 bake、UI、模块化预览和性能优化应优先读取 compact/assembly 索引；verbose `model_animations.json` 保留给人工排查和兼容旧命令。

`model_animations.json` 和 `model_animations.compact.json` 都会记录 `animationCapability`。它只表示下一步安全处理路径，不等于动画已经视觉验证通过。候选里的 `requiresUnityBake=true` 表示当前模型 Avatar 元数据已经足够生成 Unity bake 请求；`fullHumanoidBakeRequired=true` 只表示完整 Humanoid/Muscle 生产结果理论上仍需要 Unity bake 或可信 solver，若同时有 `fullHumanoidBakeBlocked=true`，说明当前库还缺 HumanBone 映射或参考姿态，不能把它排入 bake 请求：

- `HumanoidBodyNeedsUnityBake` / `HumanoidBodyNeedsInternalSolver`：Humanoid/Muscle 身体动画需要先用 Unity bake 采样到目标骨架 TRS，再写回 glTF/GLB 才能进入生产验收。旧索引里的 `HumanoidBodyNeedsInternalSolver` 兼容保留为内部公式实验队列，不能作为默认可验收结果。
- `TransformBodyPreviewReady`：普通 Transform 身体动画，或没有 Humanoid `humanBones` 但 AnimationClip 直接包含骨骼 Transform binding 的 Generic 角色动画，可以进入直接预览/打包流程。
- `DirectGltfTransformOnly`：部分 Humanoid/Muscle clip 同时带有确定性的 Transform TRS 曲线。若模型缺完整 Avatar HumanBone 映射或参考姿态，索引会把这类显式关系标成 `partialDirectGltf=true`、`fullHumanoidBakeRequired=true`、`fullHumanoidBakeBlocked=true`，允许先生成只包含 Transform 曲线的 glTF 快速预览；它不是完整 Humanoid bake 结果，不能替代 Unity bake 生产验收。
- `BlendShapePreviewReady`：确认的表情或 morph 动画，glTF 会写入 morph targets 和 `weights` animation channel，可进入预览验证。
- `NonCharacterTransformPreviewReady`：非角色 Transform 动画已经通过 Unity binding path 匹配到导出模型的 node path，并且至少命中一个可见 mesh 路径或 skinned mesh joint，可以进入 glTF 预览验证。
- `StaticPoseOnly`：Transform 类 clip 没有有效时长，或预览采样没有产生可见运动。保留为静态姿态/静态模型线索，不作为可播放动画展示。
- `EmptyHumanoidClip`：Humanoid/Muscle clip 有 Unity 时长或同步标记，但没有任何序列化曲线 payload 或 binding。保留它和模型的显式 Unity 关系，但默认不生成可播放身体动画。
- `MaterialAnimationNotMapped` / `ActiveStateAnimationNotMapped` / `RendererPropertyAnimationNotMapped`：Unity 曲线已捕获，但它们不是 BlendShape weights，需要独立材质、可见性或 Renderer 属性映射后才能作为可播放 glTF 动画验收。
- `BlendShapeLegacyNotImplemented`：legacy 表情或 morph 动画，后续需要先补 legacy clip sampling。
- `LegacyNotPlayableYet`：legacy AnimationClip，后续需要单独采样。
- `NonCharacterTransformNeedsMapping`：非角色物件/道具/场景 Transform 动画，需要按 Unity node path 映射后验证。
- `AuxiliaryTransformNeedsMapping` / `UnknownNeedsInspection`：socket、helper、材质/事件类或未知动画，需要先检查绑定目标。

`skeletons.json` 从骨架视角聚合模型和候选动画：`models` 只放可浏览的带 mesh 模型，动画 FBX 等无 mesh 的源骨架会计入 `sourceSkeletonCount`，避免污染模型浏览列表。默认候选必须来自 Unity 显式引用，例如 `Animator`、`Animation`、`AnimatorController`、`AnimatorOverrideController`、PPtr 和源索引依赖图；结构兼容、路径、名称、resourceKind 只能作为诊断、验证、过滤或显式 fallback 模式里的带标注线索。

结构兼容关系是弱关系，不能作为默认模型-动画绑定结果。Unity AnimationClip binding path、模型骨骼/节点路径、Avatar 兼容、skeleton hash、骨骼数量、路径/名称语义只能帮助解释“显式 Unity 关系是否能实际驱动目标模型”，或在人工强制预览/调试模式中筛选候选。仅“挂了完整骨架的头发/脸/配件”不能因为骨骼路径相似就获得身体动画候选。`BlackBox/Camera001` 这类只驱动相机或 dummy 的关系会被记录下来，但不会作为默认可验证动画预览。

全量 Library 不做海量模型 × 动画组合搜索。结构候选匹配只服务于小样本或过滤导出；默认保护阈值为 `100000` 组 pair，超过后 `model_animations.json` 会写入 deferred 说明，模型和动画仍保持独立入库。想验证某个角色有哪些动画时，使用定向预览、打包或后续 UI 查询，而不是让全库暴力配对。

默认还会写入性能日志：

```text
export_profile.jsonl
profile_summary.json
```

它记录批次加载、资产数据构建、模型转换、`model_mesh`、`model_skin`、`model_material`、`model_texture`、`model_texture_raw`、模型写出和 GC 的耗时及内存快照。觉得全量导出慢时，保留这个文件就能直接分析瓶颈。

贴图 PNG 路径会进一步拆成：

```text
model_texture
model_texture_decode
model_texture_image_load
model_texture_flip
model_texture_encode_png
model_texture_write
model_texture_link
```

`model_texture` 是单张贴图的整体转换耗时；`model_texture_decode` 用于判断 Unity 压缩贴图解码是否慢；`model_texture_encode_png` 用于判断 PNG 编码是否慢；`model_texture_write` / `model_texture_link` 用于判断磁盘写入、共享贴图硬链接或复制是否慢。关闭日志：

`export_profile.jsonl` 是逐事件原始日志；`profile_summary.json` 是自动汇总，按 stage 统计 `count`、`totalElapsedMs`、`averageElapsedMs`、`maxElapsedMs` 和内存峰值。长任务运行中会定期刷新，正常结束时会再写一次。

```powershell
--profile_log off
```

指定日志路径：

```powershell
--profile_log "D:\Assets\Freedunk_Data_Dev\profile.jsonl"
```

### 快速重建素材库索引

如果只是改了索引结构、候选排序、`animationCapability` 分类或验证报告逻辑，不需要重新解包和导出模型，可以直接从已有 Library 导出目录重建索引：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --rebuild_library_indexes "D:\Assets\Freedunk_Data_Dev\AnimationCompactIndexSmoke_Bill"
```

这个命令会读取已有 `asset_catalog.jsonl`，重写：

```text
asset_summary.json
model_validation.json
skeletons.json
animation_bindings.jsonl
model_animations.json
model_animations.compact.json
```

它不会加载原始 Unity 游戏目录，也不会重新导出模型、贴图或动画；小样本通常能从数分钟缩短到数秒。限制是：导出时内存里才能拿到的 Animator/Animation 显式引用无法凭空恢复，离线重建会复算 catalog 中可恢复的结构兼容关系。需要刷新 Unity 关系图、CAB/PPtr 依赖、显式 Animator Controller 引用或新增素材时，仍然要重新跑 Library 导出。

### 快速检查 Unity 文件类型

跨游戏验证或面对 Addressables/ContentArchives 这类散列文件名目录时，先用 inspection 模式看每个 Unity 文件里到底有什么，再决定导出范围：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "D:\BaiduNetdiskDownload\unity-VRising\VRising_Data\StreamingAssets\ContentArchives\5a4456c55e8ba14e483859c278648dc7" `
  "D:\Assets\Freedunk_Data_Dev\CrossGame_VRising_Inspect" `
  --inspect_unity_files `
  --game Normal `
  --mode Library `
  --profile_3d All `
  --batch_files 1
```

输出：

```text
unity_file_inspect.json
```

这个文件会记录 Unity 版本、平台、原始对象数、解析成功对象数、各类型数量和少量样本名称。它不会导出模型/贴图/动画，也不会为了模型导出提前构建 CAB map，适合快速区分：

- 纯动画/AnimatorController 包：可以作为动画库输入，但不能凭空生成模型。
- 模型/材质/贴图包：适合继续跑 Library 导出。
- 混合包：先检查是否有完整 Prefab/Animator/GameObject，避免只命中 face、hair、accessory 等 SourcePart。
- 新 Unity 版本包：如果解析报错，优先补 Unity 类型解析，而不是写游戏名特判。

当前交叉验证结论：

- Freedunk：模型、PNG 贴图、核心 Humanoid 骨架、Unity bake 后的人物身体动画、BlendShape 表情、小部分非角色 Transform 动画已经有可验证样本。
- VRising：ContentArchives 中存在纯动画包、纯模型包和混合包；严格索引会避免把 `Unknown` 脸部/头发局部模型自动挂上上千条身体动画。`HYB_VampireFemale_CustomizationScreen_Prefab` 验证出另一条通用路径：Avatar 没有 Humanoid `humanBones` 时不能走 Humanoid bake，但可通过 AnimationClip Transform binding 生成 Generic 骨骼动画预览。
- Valheim：Unity 6000 包能被识别，但当前 SkinnedMeshRenderer 解析仍有兼容问题，后续应优先补 Unity 6000 类型支持。

## 特殊模型扫描命令

只想快速扫描模型候选、不需要默认素材库结构时，可以显式使用 `SplitObjects`：

```powershell
cd D:\misutime\AnimeStudio

AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "<Unity_Data目录或资源目录>" `
  "<输出目录>" `
  --game Normal `
  --mode SplitObjects `
  --model_roots_only `
  --group_assets ByContainer `
  --profile_3d Core `
  --model_format Gltf `
  --texture_mode Raw `
  --fbx_animation Skip
```

说明：

- `--mode SplitObjects`：按 GameObject 拆分导出模型，这是模型排查模式，不是默认素材库模式。
- `--model_roots_only`：尽量只导出模型根节点，减少子 mesh 零件重复导出。
- `--group_assets ByContainer`：按资源容器路径组织输出。
- `--profile_3d Core`：这里是快速扫描显式筛选参数，用来缩小排查范围；它不代表默认完整 Library 的覆盖策略。真实全量素材库应优先完整导出，再用后处理筛选生成 Core/Curated 子集。
- `--model_format Gltf`：导出 `.gltf + .bin`；这是默认值，可以省略。
- `--texture_mode Raw`：导出原始贴图数据，避免 PNG 解码拖慢快速扫描；默认素材库导出仍然是 PNG。
- `--fbx_animation Skip`：模型导出不附带动画。参数名仍沿用旧名，控制模型导出时是否收集动画数据。

## 特殊 Animator 调试命令

`Animator` 模式主要用于排查 Animator Controller、动画收集和旧式内嵌动画，不作为默认素材库命令。默认 `--animation_package Separate` 会阻止动画写进每个模型；如果确实要复现旧行为，需要显式传 `--animation_package Embedded` 或 `Both`。

```powershell
cd D:\misutime\AnimeStudio

AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "<Unity_Data目录或资源目录>" `
  "<输出目录>" `
  --game Normal `
  --mode Animator `
  --group_assets ByContainer `
  --model_format Gltf `
  --animation_package Separate `
  --fbx_animation Auto
```

## Freedunk 示例

游戏目录：

```text
C:\Program Files (x86)\Freedunk\Game
```

Unity 数据目录：

```text
C:\Program Files (x86)\Freedunk\Game\Freedunk_Data
```

主要资源目录：

```text
C:\Program Files (x86)\Freedunk\Game\Freedunk_Data\StreamingAssets\assets
```

Freedunk 的 `.ab` 文件带 1 字节前导标记，AnimeStudio 已兼容这种 `01 + UnityFS` 格式。

### Freedunk 默认素材库

```powershell
cd D:\misutime\AnimeStudio

AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files (x86)\Freedunk\Game\Freedunk_Data" `
  "D:\Assets\Freedunk_Data_Dev\Freedunk_Data_library" `
  --game Normal
```

这会使用默认 `Library + ByLibrary + 完整可浏览素材库 + glTF + PNG + Separate animations`。模型和贴图可直接浏览，动画独立进入 `Animations`，不会把全局篮球动作重复塞进每个角色 glTF。

### Freedunk 快速模型扫描

```powershell
cd D:\misutime\AnimeStudio

AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files (x86)\Freedunk\Game\Freedunk_Data" `
  "D:\Assets\Freedunk_Data_Dev\Freedunk_Data_models_raw_scan" `
  --game Normal `
  --mode SplitObjects `
  --model_roots_only `
  --group_assets ByContainer `
  --profile_3d Core `
  --model_format Gltf `
  --texture_mode Raw `
  --fbx_animation Skip
```

这个命令用于更快排查模型候选；它不是默认素材库输出。

### Freedunk 分目录模型

导出球场：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files (x86)\Freedunk\Game\Freedunk_Data\StreamingAssets\assets\graphics\stage" `
  "D:\Assets\Freedunk_Data_Dev\stages" `
  --game Normal `
  --mode SplitObjects `
  --model_roots_only `
  --group_assets ByContainer `
  --fbx_animation Skip
```

导出球：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files (x86)\Freedunk\Game\Freedunk_Data\StreamingAssets\assets\graphics\ball" `
  "D:\Assets\Freedunk_Data_Dev\balls" `
  --game Normal `
  --mode SplitObjects `
  --model_roots_only `
  --group_assets ByContainer `
  --fbx_animation Skip
```

导出玩家角色：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files (x86)\Freedunk\Game\Freedunk_Data\StreamingAssets\assets\graphics\character\pc" `
  "D:\Assets\Freedunk_Data_Dev\characters_pc" `
  --game Normal `
  --mode SplitObjects `
  --model_roots_only `
  --group_assets ByContainer `
  --fbx_animation Skip
```

导出 NPC：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files (x86)\Freedunk\Game\Freedunk_Data\StreamingAssets\assets\graphics\character\npc" `
  "D:\Assets\Freedunk_Data_Dev\characters_npc" `
  --game Normal `
  --mode SplitObjects `
  --model_roots_only `
  --group_assets ByContainer `
  --fbx_animation Skip
```

## 原神示例

原神资源目录示例：

```text
C:\Program Files\miHoYo Launcher\games\Genshin Impact Game\YuanShen_Data
```

注意：不要把 `Genshin Impact Game` 游戏根目录或 `YuanShen_Data` 直接当作普通 Unity 目录用 `--game Normal` 跑默认 Library。这样通常只会读到 player/sharedassets/level/UI 资源，表现为 `CNPayPlatDialog`、`LoadingFade`、`Image` 等 UI/loading 候选，最终可能 0 个有效 3D 模型。原神应使用 GI profile，并输入实际 AssetBundle blocks 目录。

主要资源包：

```text
C:\Program Files\miHoYo Launcher\games\Genshin Impact Game\YuanShen_Data\StreamingAssets\AssetBundles\blocks
```

补丁/缓存资源包：

```text
C:\Program Files\miHoYo Launcher\games\Genshin Impact Game\YuanShen_Data\Persistent\AssetBundles\blocks
```

如果需要更完整结果，可以分别导出 `StreamingAssets` 和 `Persistent`。

### 原神贴图/材质

```powershell
cd D:\misutime\AnimeStudio

AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files\miHoYo Launcher\games\Genshin Impact Game\YuanShen_Data\StreamingAssets\AssetBundles\blocks" `
  "D:\tmp\AnimeStudio_GI_Library" `
  --game GI `
  --types Texture2D:Both Sprite:Both SpriteAtlas:Parse Material:Both AssetBundle:Parse ResourceManager:Parse `
  --export_type Convert `
  --group_assets ByLibrary
```

### 原神静态 FBX 模型

这是兼容旧原神导出流程的命令：

```powershell
cd D:\misutime\AnimeStudio

AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files\miHoYo Launcher\games\Genshin Impact Game\YuanShen_Data\StreamingAssets\AssetBundles\blocks" `
  "D:\tmp\AnimeStudio_GI_Library" `
  --game GI `
  --model_roots_only `
  --types GameObject:Both Transform:Parse MeshFilter:Parse MeshRenderer:Parse SkinnedMeshRenderer:Parse Mesh:Parse Material:Parse Texture2D:Parse `
  --export_type Convert `
  --group_assets ByLibrary `
  --fbx_animation Skip
```

说明：

- `GameObject:Both` 是 FBX 模型导出的核心类型。
- 单独 `Mesh:Both` 会导出 OBJ，不是 FBX。
- `Material:Parse` 和 `Texture2D:Parse` 用于解析材质和贴图依赖。
- `Transform`、`MeshFilter`、`MeshRenderer`、`SkinnedMeshRenderer` 用于恢复层级、网格和材质槽。
- `--model_roots_only` 只导出 AssetBundle/ResourceManager 容器里明确记录的主 GameObject。
- `--fbx_animation Skip` 保持静态模型导出，不主动收集动画。

如果需要完全复用旧的 `Maps\assets_map.bin`，额外添加：

```powershell
--map_name assets_map
```

### 原神模型、贴图、材质库

```powershell
cd D:\misutime\AnimeStudio

dist\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files\miHoYo Launcher\games\Genshin Impact Game\YuanShen_Data\StreamingAssets\AssetBundles\blocks" `
  "D:\tmp\AnimeStudio_GI_Library" `
  --game GI `
  --model_roots_only `
  --types GameObject:Both Texture2D:Both Sprite:Both Material:Both Transform:Parse MeshFilter:Parse MeshRenderer:Parse SkinnedMeshRenderer:Parse Mesh:Parse SpriteAtlas:Parse AssetBundle:Parse ResourceManager:Parse `
  --export_type Convert `
  --group_assets ByLibrary `
  --fbx_animation Skip
```

输出结构大致为：

```text
D:\tmp\AnimeStudio_GI_Library\
  Models\
  Textures\
  Materials\
```

模型依赖贴图会统一写入顶层 `Textures\_ModelDependencies`。默认 `--texture_mode Png` 会写 PNG；如果显式使用 `--texture_mode Raw`，则会写 `.rawtex` 和 `.rawtex.json`。模型依赖的材质 JSON 会和模型放在同一目录，独立材质会进入顶层 `Materials`。

`--texture_mode Png` 下，glTF 会优先引用模型目录旁边 `Textures` 子目录里的 PNG；这些 PNG 是从顶层 `Textures\_ModelDependencies` 创建的硬链接。硬链接失败时才会退回普通复制。

### 原神 Shader

```powershell
cd D:\misutime\AnimeStudio

AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files\miHoYo Launcher\games\Genshin Impact Game\YuanShen_Data\StreamingAssets\AssetBundles\blocks" `
  "D:\tmp\AnimeStudio_GI_Shaders" `
  --game GI `
  --types Shader:Both `
  --export_type Convert `
  --group_assets ByLibrary
```

Shader 导出是研究资料，不等于 Blender 可以直接使用。默认 Library 素材库不导出 shader；如果要把 shader 作为实验资料归档，可以在 Library 命令中加入：

```powershell
--include_shaders
```

此模式只保存 `.shader.raw` 和 `.shader.raw.json`，避免 native D3D 反汇编在部分游戏 shader 上崩溃。后续重建 Blender 材质时，应结合 shader raw、材质 JSON、贴图文件一起分析。

## 原神目录恢复和 asset index

`--group_assets ByContainer` 会尝试按资源 container/path 分组。原神在没有对应版本 asset index 时，无法完整还原原始目录。

CLI 支持：

```powershell
--ai_file <asset_index.json>
--ai_version <version>
```

如果没有可用的当前版本 asset index，建议使用：

```powershell
--group_assets ByLibrary
```

`ByLibrary` 能恢复 container 时按 container；不能恢复时按资源名前缀分类。它不会假装还原不存在的目录，但输出会比纯 `ByType` 更接近素材库结构。

## 手动过滤

只包含某些容器路径：

```powershell
--containers "graphics[\\/]stage"
```

排除某些容器路径：

```powershell
--containers_exclude "ui|sound|video|emoji"
```

只导出某些对象名：

```powershell
--names "^BeachBall_Blue$"
```

排除某些对象名：

```powershell
--names_exclude "camera|maincam|handycam|uicam"
```

这些参数支持正则，也支持传文本文件路径。文本文件中每行一个正则，空行会被跳过。

## FBX 参数

```powershell
--fbx_animation Skip
```

不导动画，适合模型导出。

```powershell
--fbx_animation Auto
```

自动收集动画，适合 Animator 模式。

```powershell
--fbx_animation All
```

把当前过滤范围内所有 AnimationClip 都传给 FBX，适合小范围测试。

```powershell
--fbx_scale_factor 100
--fbx_bone_size 10
```

覆盖 FBX 缩放和骨骼显示尺寸。CLI 默认 `scaleFactor=100`，目标是让 FBX 在 Blender/F3D/Unity 等 DCC 工具里以可见、可验收的人物尺寸打开；如果需要保留原始 Unity 单位比例，可以显式使用 `--fbx_scale_factor 1`。

### Core Humanoid 骨架预览

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --generate_skeleton_guide "D:\Assets\Freedunk_Data_Dev\DirectFbx_Bill_Static_SkeletonValidation_Png\assets\graphics\character\pc\bill_01_00\Bill_01_00_ingame\Bill_01_00_ingame.fbx" `
  --preview_output "D:\Assets\Freedunk_Data_Dev\DirectFbx_Bill_Static_CoreSkeletonGuide_CLI\assets\graphics\character\pc\bill_01_00\Bill_01_00_ingame"
```

`--generate_skeleton_guide` 是非破坏性诊断命令，支持 `.fbx`、`.gltf`、`.glb` 作为底图输入。它不会修改或重导出原模型，只会复制一份 `*.canonical.*`，并生成一个 `*_core_skeleton_guide.blend`。如果输入是 `.gltf`，命令会同时复制 glTF 引用的 `.bin` 和图片依赖。这个 `.blend` 会隐藏原始 armature，用粗红色管线和黄色关节点显示 Unity Avatar `HumanDescription` 解出的 Core Humanoid 主链，适合人工判断人体骨架是否正确。

命令会优先从 FBX 所在目录向上查找 `asset_catalog.jsonl`，并使用其中的 Unity Avatar 关系；也可以显式传：

```powershell
--skeleton_guide_catalog "D:\Assets\Freedunk_Data_Dev\DirectFbx_Bill_Static_SkeletonValidation_Png\asset_catalog.jsonl"
```

报告写入 `core_skeleton_guide_report.json`。重点看 `relationSource` 是否为 `unity_avatar_human_description`、`missingEdgeCount` 是否为 `0`。如果找不到 Unity Avatar 关系，命令会退回常见 `Bip001` 名称作为诊断 fallback，但这种结果不能当成最终骨架验收。

如果某个 FBX 触发 Blender importer 兼容问题，可以改用同一模型的 glTF 作为底图输入，并继续通过 `--skeleton_guide_catalog` 指向原 FBX/素材库导出的 `asset_catalog.jsonl`。这样骨架关系仍来自 Unity Avatar，底图只负责可视化。使用 glTF 底图时，Blender importer 可能生成 `glTF_not_exported` 辅助集合；CLI 会从骨架预览 `.blend` 中移除这些非资源对象，避免低模占位球等辅助物遮挡角色本体。对于 `Face_dummy` 这类脸部父级 armature，CLI 会保留层级但隐藏原始 armature 显示，只显示红黄 CoreHumanoid guide。

## 材质和 Shader 边界

CLI 的 FBX 导出目标是“Blender 可打开、基础材质可见”，不是完整还原游戏自定义 shader。

常见贴图槽会尽量映射到 FBX 标准材质：

- `_MainTex`、`Diffuse`、`Albedo`、`BaseMap`、`BaseColor` 会挂到 Diffuse。
- `Normal` 会挂到 NormalMap。
- `_BumpMap` 会挂到 Bump。
- `Specular` 会挂到 Specular。
- `Emission`、`Emissive` 会挂到 Emissive。
- `Reflect` 会挂到 Reflection。

很多游戏包含自定义 shader 槽，例如 `Mask`、`DetailDiffuse`、`DetailNormal`、`Tint`、`Ramp`、`Smoothness`、`Blend` 等。FBX 标准材质不能完整表达这些混合逻辑，所以这些贴图和参数会保留在材质 JSON 和贴图文件中，但不保证在 FBX 里自动还原游戏内效果。

## 建议流程

1. 默认使用 `--mode Library` 导出素材库。
2. 全量或大目录默认保留 `--texture_mode Png`，让模型目录能直接查看贴图。
3. 如果只是快速扫描候选模型，显式改用 `--texture_mode Raw` 或 `Reference`。
4. 需要排查模型重复或容器路径时，才使用 `--mode SplitObjects`。
5. 需要排查 Animator Controller 或旧式内嵌动画时，才使用 `--mode Animator`。
6. 不要默认使用 `--animation_package Embedded`，否则容易把全局动作库重复写进每个角色模型。
7. Shader 只作为实验功能，研究时显式传 `--include_shaders`。
8. 发现慢点时优先看 `export_profile.jsonl`，不要只根据控制台进度猜瓶颈。

全量导出会消耗较多内存和时间。CLI 会按文件逐个加载、导出、清理，但跨 bundle 依赖仍可能拉起大量资源，建议优先分目录导出。

## 注意事项

- 不建议用 GUI 直接打开大型完整 `*_Data` 目录。
- `Unknown ClassIDType` 警告通常可以先忽略，只要目标资源能导出即可。
- FBX 依赖 `AnimeStudio.FBXNative.dll`。
- `Convert` 会解码贴图/模型材质贴图，内存压力高于 `Raw`。
- 如果只想保持旧原神导出行为，可以继续使用 `--types ... --group_assets ByLibrary`，并按需添加 `--map_name assets_map`。
