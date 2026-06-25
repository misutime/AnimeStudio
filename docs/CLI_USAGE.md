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

当已经有一个 Library 或 AudioLibrary 导出目录时，可以把机器索引、Unity 关系、报告 JSON 和实际文件列表合并进 SQLite，供后续浏览、查询、调试、材质补全、二次打包或旧 bake 诊断使用：

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
- `candidateNextActions` / `candidateProcessingFlags`：显式候选下一步处理路径。Transform/BlendShape 等直接动画默认进入 `generate_preview_gltf`；Humanoid/Muscle 不能再把旧式外部 Unity 工程/插件/helper bake 当作生产桥接路径，生产验收必须落成 AnimeStudio 写出的 glTF TRS / weights。`generate_unity_baked_gltf` 只表示旧库诊断或迁移对照，`UnityBakeAccelerated` 则是新的高吞吐求解路径，需要看 `unityBakeAcceleratedReady`、Avatar 来源、性能报告和视觉验收。Endfield 这类只有 AvatarConstant `avatarSkeleton/defaultPose`、但 `humanBoneIndex` 全是 `-1` 的模型，会标为 `experimentalInternalHumanoidSolverReady=true`、`internalHumanoidSolverProductionReady=false`：它可以生成实验 glTF TRS 预览，但仍不能当作生产可复用动画验收。
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

如果只是阶段性确认“根目录 bake cache 摘要还在、ImportedAvatar 统计是否仍 fresh、最近批次报告在哪里”，可以用 `-FastSummary`。这个模式不扫描大型 SQLite 候选表，适合原神这类 10GB 级 `library_index.db` 的快速验收入口；它不会替代 `-GateOnly` / `-SummaryOnly` / 完整模式的确定性关系门禁：

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass `
  -File scripts\Measure-DeterministicAnimationCoverage.ps1 `
  -LibraryPath "D:\Assets\AS-Assets\YuanShen-Assets" `
  -UnityProject "D:\misutime\AnimeStudioUnityProject" `
  -OutputDir "D:\Assets\AS-Assets\YuanShen-Assets\AnimationRelationDiagnostics_Fast" `
  -FastSummary
```

`-FastSummary` 会优先读取根目录 `animation_bake_cache_summary.json`、`sqlite_index_summary.json`、`Assets/AnimeStudioBake/ImportedAvatar/*.asset`、最近的 `ImportedAvatarProbe*/imported_avatar_probe_batch.json` 和 `.as_browser_cache/unity_bake_batch_reports/*.json`。如果传入 `-UnityProject`，脚本会现场扫描 bake 工程里的 ImportedAvatar 文件；如果没有传 `-UnityProject`，但根目录 bake cache 已记录 fresh 的 ImportedAvatar 探针统计，FastSummary 会沿用 cache 里的文件数、可信文件数和 probe freshness，避免把“没有现场扫描 Unity 工程”误报成 Avatar asset 为 0。这只复用已经写入 cache 的确定性统计，不新增模型-动画关系，也不猜测 Avatar oracle。旧库里的 summary JSON 如果因为历史编码问题读坏，脚本会把读取错误写进报告，而不是中断整个快速验收。维护这个 PowerShell 脚本时，新增在 PowerShell 主体里的字符串应保持 ASCII；中文说明优先写到 Markdown 文档或 Python 生成内容里，避免 Windows PowerShell 5 按 ANSI 解析 UTF-8 无 BOM 文件时误报语法错误。

如果读取到 ImportedAvatar probe，FastSummary 会写出 `importedAvatarProbeFreshness`：`fresh` 表示 probe 的 asset 数量与当前 `ImportedAvatar` 目录一致，且报告不早于目录内最新 `.asset`；`stale` / `mismatch` 表示恢复或替换过 Avatar asset 后还没重新验证，Browser 会提示重新运行“验证AvatarAsset”。这个 freshness 只保护 Avatar oracle 验证报告的新鲜度，不会新增或修改模型-动画关系。

改动动画导出、Browser 动画列表、Unity bake request/apply 或 Avatar oracle 相关代码后，先跑本仓库级一键门禁：

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass `
  -File scripts\Test-AnimationPipelineGates.ps1
```

它会检查两条红线：默认动画候选不能用 `confidence`、结构兼容、骨骼数量或名称提升为显式关系；`unityAssetPaths.avatarAsset` 一旦显式传入，Unity bake helper、apply 和 Browser 都必须只信任有效的导入 `UnityEngine.Avatar` asset，不能静默退回 `BuildHumanAvatar`、`AvatarConstant`、`internalSolver` 或 glTF rest pose。默认还会构建 CLI 和 Browser。需要同时跑 `AnimatorOverrideController` clip-pair 样本回归时，加 `-IncludeOverrideRegression`；只想快速跑源码门禁时，加 `-SkipBuild`。

非 `-FastSummary` 的深度模式主要读取 `library_index.db` 和 `unity_source_index.db`，输出 `deterministic_animation_coverage.json` 与 `DETERMINISTIC_ANIMATION_COVERAGE.md`。验收时优先看：

- `gate.status`：默认应为 `ok`。
- `totals.nonExplicitCandidates`：必须为 `0`，否则说明默认候选表混入了非 Unity 显式关系，需要追查是否退回骨骼数量、名称或路径 fallback。
- `candidateTableSchema.status`：默认应为 `ok`。它检查 `model_animation_candidates.relation_source` 是否同时有 `NOT NULL` 和 `CHECK(relation_source='explicit')` 约束；如果旧库数据已经全是 explicit、但 schema 还没约束住，也要重建 `library_index.db`，不能让后续流程重新写入猜测关系。
- `totals.animatedCategoryExplicitCoverage`：按 `NPC`、`Avatar`、`Monster`、`Animal`、`Partner`、`Vehicle` 这类明显动画模型目录粗看显式覆盖；它比全库模型覆盖更接近角色/怪物/动物动画绑定进度。
- `sourceIndexAnimationRelationHealth.staleOverridePairIndex`：为 `true` 时说明源索引是旧版或不完整版本，应先重建 `unity_source_index.db`，再重建 `library_index.db`。这不是让工具回退猜测关系的理由。

如果命令没有显式传 `-SourceIndex`，脚本会先读取 `sqlite_index_summary.json` 中 `animationRelationCoverage.sourceIndexAnimationRelationHealth.sourceIndex`，也就是当前 `library_index.db` 实际重建时使用的源索引；找不到时才回退到素材库根目录的 `unity_source_index.db`。这能避免 VRising 这类“正式 library_index.db 由旁路 fresh source index 重建”的库，在 Browser 手动刷新 GateOnly 时误读旧根目录源索引。

这份报告只回答“默认模型-动画候选是不是来自 Unity 确定性关系”。它不证明 Humanoid/Muscle 姿态已经能由 AnimeStudio 内部公式正确转成 glTF TRS；这类动画还必须继续看直接 glTF 预览和曲线验证报告。外部 Unity 工程/插件/helper bake 只能作为旧库诊断或迁移对照，不能再作为生产桥接路径；即使保留历史 bake 结果，也必须标清 `bakeMode`、诊断状态和不可作为默认可复用动画的原因，不能把 bake 成功当作新增或猜测模型-动画关系。

深度报告里的 `modelAvatarRefreshBlockers` 用于定位仍缺生产 Avatar oracle 的模型。它按当前有效口径计算：显式 Humanoid/Muscle 候选如果已经有完整 `HumanDescription.humanBones + skeletonBones`，或已精确命中 fresh ImportedAvatar asset，就会从 blocker 中扣除。旧索引 raw 里的 `productionUnityBakeReady` / `productionUnityBakeBlocked` 只能作为历史诊断字段，不能单独决定“已 ready”或“仍缺 Avatar”。因此导入新的原始 Unity Avatar asset 后，应重新运行 `-SummaryOnly` 或完整深度报告，让 blocker 列表反映当前 oracle 状态。

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
- `resolvedTargetCounts` / `missingTargetCounts`：统计 `animatorController.clip`、legacy `animation.clip`、`animatorOverrideController.originalClip`、`animatorOverrideController.overrideClip` 是否能解析到真实 `AnimationClip`。只要存在缺失目标，当前源索引就不能作为动画 smoke 的完整依赖底座。
- `staleOverridePairIndex`：如果源目录存在 `AnimatorOverrideController`，但源索引既没有 `overrideSet`，或存在非空替换表却没有 `clipPair`，会标为 `true`，说明这是旧版或不完整源索引，应该先重建源索引，再重建 Library SQLite。

源索引还会记录 `MonoBehaviour` 的轻量确定性引用：

- `monoBehaviour.script`：脚本组件指向的 `MonoScript`，`source_objects.raw_json.monoScript` 会保留 class、namespace 和 assembly。
- `monoBehaviour.pptr`：脚本 TypeTree 字段里的非空 PPtr，`raw_json.details.path` 记录字段路径。它只用于继续追 prefab/配置/运行时引用链，不能单独升级成默认模型-动画候选。

源索引也会记录 Unity 容器和预载表关系：

- `assetBundle.preload`：`AssetBundle.m_PreloadTable` 里的原始 PPtr。
- `assetBundle.containerAsset`：`AssetBundle.m_Container` 条目指向的主资源 PPtr，并保留 container 路径、preloadIndex、preloadSize。
- `assetBundle.containerPreload`：container 对应 preload 范围里的对象 PPtr，用于追踪一个容器路径实际带出的依赖对象。
- `resourceManager.container`：`ResourceManager.m_Container` 的路径到对象 PPtr。

这些关系是 Unity 原始来源和依赖证据，可用于静态 Mesh 主资源识别、缺件排查、材质/贴图依赖闭包和 Endfield VFS 定位；但 container、目录名或 bundle 名仍然只是显式 fallback/诊断信号，不能单独升级为默认模型-动画绑定关系。
`unity_source_index_summary.json` 和 `--verify_source_index` 输出会写 `sourceRelationHealth`、`modelDependencyHealth` 和 `materialRelationHealth`。如果源索引里有大量 `AssetBundle` / `ResourceManager` 对象，但 `assetBundle.*` / `resourceManager.container` 计数为 0，说明这是旧索引或不完整索引；如果 `modelDependencyHealth` 显示缺 `meshFilter.mesh` / `skinnedMeshRenderer.mesh` 目标，说明当前数据库还没有解析到模型本体 Mesh；如果显示缺 `animator.avatar` 目标，说明优化层级或 Humanoid/Skinned 模型可能无法恢复骨骼；如果 `optimizedAnimatorNullAvatar=true`，说明存在 `hasTransformHierarchy=false` 但 `Animator.m_Avatar` PPtr 本身为空的对象，这不是 CAB 闭包问题，不能靠同容器其他 Avatar 硬补。以上都属于模型第一阶段失败，不能继续验收动画。如果 `materialRelationHealth.missingRendererMaterialTargets=true`，说明已经捕获到 Renderer 材质 PPtr，但目标 `Material` 没有解析进当前数据库，模型第一阶段不能把这类样本当作材质完整。`missingMeshFilterMeshTargetSamples` / `missingSkinnedMeshTargetSamples` / `missingAnimatorAvatarTargetSamples` / `optimizedAnimatorNullAvatarSamples` / `missingRendererMaterialTargetSamples` / `missingMaterialTextureTargetSamples` 会列出被引用最多的缺失目标 CAB、PathID、引用方和 external 记录样本；`missingAnimatorControllerClipTargetSamples` / `missingAnimationClipTargetSamples` / `missingAnimatorOverrideOriginalClipTargetSamples` / `missingAnimatorOverrideClipTargetSamples` 会列出缺失 `AnimationClip` 目标样本，用于继续追 VFS/CAB 依赖闭包或解析覆盖；这些字段是诊断线索，不是模型或动画可用证明。需要用完整源目录重建或继续修复 VFS/CAB 依赖闭包后，才能可靠排查模型缺件、静态 Mesh 来源、Avatar 依赖、材质贴图依赖闭包、动画 clip 依赖闭包和 Endfield VFS 定位。

新版源索引会把 Bundle/VFS 内层来源写进 `serialized_files.source_path` 和 `source_objects.source_path`，格式类似 `Persistent/VFS/7064D8E2/7064D8E2.blc|Data/Bundles/Windows/main/xxx.ab|CAB-...`。这条链路只用于溯源和闭包诊断，不改变 Unity 的 `serialized_file` / `path_id` 关系键；看到旧索引只有外层 `.blc` 时，应重建源索引再排查“目标 CAB 到底在哪个内部 .ab 里”。

Endfield 这类 postmodel prefab 当前常见状态是：模型 `Animator` 有 Avatar，但 `animator.controller` 为空；挂载脚本多为 IK、动态骨骼、渲染 helper 等。即使 `monoBehaviour.pptr` 能索引到这些字段，只要没有进一步追到 Unity 显式 controller/clip/配置引用，`model_animation_candidates` 仍应保持 0，不能用角色名、目录名或动画名前缀补关系。

可以从已有 `unity_source_index.db` 里先列出“模型优先”烟测候选，再挑选真实可用模型做小范围导出：

```powershell
dotnet AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.dll `
  "C:\Program Files\Hypergryph Launcher\games\Arknights Endfield\Endfield_Data" `
  "D:\Assets\fangzhou\Endfield_SourceModelCandidates_ActorChr_Current" `
  --list_source_model_candidates "D:\Assets\fangzhou\Endfield_FullGame_VFS_SourceIndex_MonoPPtr_Current\unity_source_index.db" `
  --names "(P_actor|chr_)" `
  --source_candidate_limit 80
```

这个命令只读取源索引里的确定性 Unity 关系，例如 `Animator`、`SkinnedMeshRenderer`、`renderer.material`、`animator.avatar`、`animator.controller`、`AssetBundle.containerAsset/containerPreload`。它输出 `source_model_candidates.json` 和 `source_model_candidates.csv`，用于选择第一阶段模型 smoke 样本；不会导出素材，也不会创建模型-动画绑定。报告会给 Dialog / Timeline / UI / preview / postmodel / cutscene / ability entity 等候选写 `excludeHint`，这类对象默认只适合索引诊断或显式扩展，不应作为生产模型验收样本。若源索引是当前版本并包含 `assetBundle.containerAsset` / `assetBundle.containerPreload` / `resourceManager.container`，候选还会写 `containerPath`，用来提前识别 `dialog/tmpobject` 这类路径语义；旧源索引没有 container 关系时该列会为空，此时必须靠导出后的 `asset_catalog.jsonl` / 输出路径 / 截图继续降级临时对象。只有后续导出的模型通过 `model_validation.json`、glTF 校验和清晰静态截图确认 Mesh、UV、材质、贴图、skin、bbox 正常，才允许进入动画候选和动画预览阶段。

`--list_source_model_candidates` 的筛选优先使用 `--preview_model`，没有传时使用 `--names`，再没有传时才使用 `--containers`。简单字面量名字（例如 `P_actor_chen_01`）会写出 `selectorQueryMode=targeted_exact_name`，并通过 `source_objects.name -> component.gameObject` 的确定性关系在大源索引里快速定位候选；复杂正则仍走普通候选扫描，避免 SQL 预筛选误漏结果。若命中对象只是 prefab 里的子 Renderer，报告会保留同一个 Unity container 的 `ContainerPrimaryModel` 主根，并把命中的子对象名写入 `sampleNames`，避免把完整组合模型误筛掉。输出目录优先使用 `--preview_output`，否则使用普通 `output_path`。

如果同时传入 `--preview_source_root`，每个候选会额外写 `targetedLibraryExport`，包含机器可读 `arguments` 和 PowerShell 友好的 `powershellCommand`。命令会用 `&` 调用生成报告时正在运行的 CLI，避免带空格或带引号的 exe 路径在 PowerShell 里不能直接执行。这只是把候选行里的 `sourcePath + pathId` 组合成定向 Library 导出命令：输入仍必须是完整 Unity 源目录，依赖闭包仍来自完整 `unity_source_index.db`，本次加载只用 `--source_files + --path_ids` 收窄。这个命令只能作为模型第一阶段 smoke 起点，不能替代后续 `model_validation.json`、glTF validator 和清晰截图验收；候选本身带 `excludeHint` 时，报告会把 `readyForModelSmoke=false`。

`--names` 也可以传纯数字 PathID 做精确查询，报告会写 `selectorQueryMode=targeted_exact_path_id` 和 `selectorExactPathId`。Endfield 这类游戏常有大量同名 `GameObject` 变体：有的是真实可见模型，有的只有 Animator/Avatar，有的只用于剧情或 UI。排查动画关系时应优先用 `source_model_candidates.json` 里通过模型门禁的具体 PathID，再用同一个 PathID 查询 `--list_source_model_animations`，避免同名变体把模型质量和动画关系混在一起。

`selectorQueryMode=targeted_exact_name` / `targeted_exact_path_id` 默认不再沿 `Transform.child` 子树做深层递归。Endfield 大源索引里同名 actor 变体很多，某些根节点层级很深或只是配置/场景实例，默认递归会让交互式候选查询卡住。候选报告现在优先快速返回 Animator、Avatar、Controller 和直接 SkinnedRenderer 线索；是否真的有完整 Mesh/Material/Texture/skin，必须通过实际 Library 导出、`model_validation.json`、glTF validator 和清晰截图确认。

Naraka 这类项目可能把可见部件放在自定义 `MonoBehaviour` 数据里。排查 `AvatarMeshDataAsset` 时，可以先用 `--types MonoBehaviour --export_type Convert` 导出 TypeTree JSON，再把单个 JSON 转成静态诊断 glTF：

```powershell
.\AnimeStudio.CLI\bin\Release\net8.0-windows\AnimeStudio.CLI.exe `
  --export_avatar_mesh_data_gltf "D:\Assets\Naraka\MonoDump_HadiHairMeshData_Convert\MonoBehaviour\hair_01_mesh_data.json" `
  --preview_output "D:\Assets\Naraka\AvatarMeshDataProbe_HadiHairGltf"
```

如果已经有完整 `unity_source_index.db`，可以先生成确定性导出计划，避免手工查 `ActorBodyVisualCell`、`LXRendererAssistant`、`AvatarMeshDataAsset`、材质和贴图 PathID：

```powershell
.\AnimeStudio.CLI\bin\Release\net8.0-windows\AnimeStudio.CLI.exe `
  --export_naraka_avatar_mesh_plan "D:\Assets\Naraka\SourceIndex_Full_HeaderFix1\unity_source_index.db" `
  --preview_model ch_m_hadi_hair_lv_ss0 `
  --preview_source_root "C:\Game163\program\NarakaBladepoint_Data\StreamingAssets" `
  --preview_output "D:\Assets\Naraka\HadiHair_SourceIndexPlan"
```

这个命令会写 `naraka_avatar_mesh_export_plan.json` 和 `naraka_avatar_mesh_export_commands.ps1`。计划脚本会复用生成计划时正在运行的 CLI 路径，避免 Debug/Release 版本错位。计划里的 `--source_files` 和 `--path_ids` 只来自源索引确定性关系：`ActorBodyVisualCell.lod0RendererAssistants -> LXRendererAssistant.avatarMeshAsset`、`LXRendererAssistant._renderer`、`renderer.material`、`material.texture`，以及同 SerializedFile 内可追溯的 `SkinnedMeshRenderer`、`Transform` 和 BoneDriver 诊断线索。脚本会按 source file 分段导出 TypeTree JSON、材质/贴图，再生成诊断 glTF 并重建 `asset_library.json` / `library_index.db`；每个步骤完成后会在 `.naraka_plan_state/` 写 checkpoint，命令中断后可以直接重跑脚本跳过已完成步骤。Naraka 大 bundle 加载可能很慢，profile 里应保留 `load_batch`、`export_batch`、`clear_batch` 证据。

这个命令只读取 JSON 里的 `m_VertexData`、`m_Indices`、`m_HairUVSetData` / `m_UVData` 等确定性字段，输出灰色静态 glTF、bin 和 `avatar_mesh_data_report.json`。它不会猜 prefab 模块关系、Renderer 材质槽、骨骼或 skin 绑定；产物只能作为“自定义网格数据存在且可解码”的诊断证据，不能直接作为默认 Library 主资源、材质验收或动画 smoke 证据。

同一个参数也可以传 JSON 目录，工具会把目录内每个 `AvatarMeshDataAsset` JSON 写成同一个 glTF 里的独立 mesh/node，并在报告里汇总 `meshCount`、总顶点/三角数和整体 bbox。目录模式是全数据诊断视图：如果目录里同时包含 `hair_01_mesh_data` 和 `hair_01_L1_mesh_data` 这类 LOD 变体，报告会写 `containsLodVariants=true`，截图会看到多级 LOD 叠在一起。这只能证明各自定义网格片可解码，正式模型导出仍必须按 `ActorBodyVisualCell.lod*RendererAssistants` 或更明确的 Unity 字段选择正确 LOD，并绑定 Renderer 材质、骨骼/skin。

如果目录里同时包含 `ActorBodyVisualCell`、它引用的 `LXRendererAssistant`、对应的 `AvatarMeshDataAsset` JSON，以及同级或上级 `export_manifest.jsonl`，目录模式会优先按 `ActorBodyVisualCell.lod0RendererAssistants -> LXRendererAssistant.avatarMeshAsset` 选择 LOD0 部件，输出 `NarakaActorBodyVisualCellLodGltf` 报告。这个路径只使用 Unity PPtr 和 TypeTree 字段，不靠名字猜 LOD；它适合确认 Naraka 自定义发型/部件能按视觉组件关系组装。只有目录里已经有确定性导出的 `Material` JSON 和 `Texture2D` PNG 时，才会生成保守预览材质；骨骼或 skin 仍必须等 Renderer/自定义网格暴露出可验证绑定后再写入。

同一命令可追加 `--source_index D:\Assets\Naraka\SourceIndex_Full_HeaderFix1\unity_source_index.db`，报告会按 `LXRendererAssistant._renderer -> renderer.material -> material.texture` 写出每个 LOD0 部件的材质引用和纹理引用数量。这里仍只记录 Unity 源关系证据；是否有贴图和预览材质，取决于同一个输出目录里是否已经通过计划脚本或定向 `Export` 导出了对应 `Material`/`Texture2D`。即使能生成 base color / normal / alpha 预览，也不能把它升级成完整 shader、customization tint、skin 或动画验收通过样本。

如果同一个 `--preview_output` 目录已经通过定向 `Export` 导出了这些 `Material/*.json` 和 `Texture2D/*.png`，`ActorBodyVisualCell` 目录模式会尝试生成保守 glTF 预览材质：只把通用 base color / normal 槽写入 glTF，ID、mask、SH、dir、LUT 等自定义 shader 数据只保留在 `extras.animeStudioMaterial.textureSlots`。Naraka 发型常见的 `_MainTex=id`、`_MainSHMap=shader 数据` 不会被硬当作发色；报告和 glTF extras 会标记 `needsCustomizationTint=true`，直到找到确定性染色/customization 配置。

`ActorBodyVisualCell` 目录模式还会在 `--preview_output` 根目录写入 `asset_catalog.jsonl` 和 `model_validation.json`，方便继续运行 `--build_sqlite_index <preview_output> --source_index <unity_source_index.db> --game Naraka` 生成 AssetLibrary v1 入口。这里的 catalog 记录使用 `libraryRole=CustomMeshDiagnostic`、`resourceKind=NarakaCustomMeshPart`，验证状态固定为 `warning`：它只让浏览器和 SQLite 能定位这份诊断 glTF，不表示模型已经具备 skin、完整角色装配、发色 customization 或动画验收资格。

定向 `--list_source_model_candidates` 会识别 Naraka 已知的显式链路 `ActorBodyVisualCell -> LXRendererAssistant.avatarMeshAsset -> AvatarMeshDataAsset`，并在报告里写 `customAvatarMeshCount`。这说明 prefab 的标准 `SkinnedMeshRenderer.mesh` 为空，但自定义 MonoBehaviour 里存在可解码网格；报告会同时标记 `customAvatarMeshNeedsExporterBinding`，在真正把 custom mesh、Renderer 材质、骨骼/skin 绑定成 glTF 前，不能把这类候选当成模型 smoke 通过样本。

2026-06-24 对 `P_actor_pelica_01`、`P_actor_endminf_01`、`P_actor_endminm_01`、`P_actor_boynpc_01` 等 Endfield 样本复查后，经验是：很多 `P_actor_*` 在 `source_objects` 的 GameObject/Animator 层只是 levelseq、cutscene、skeletalmorph/gamedata 或配置线索，报告会标 `noVisibleMesh,noMaterial`。这不一定表示完全不能导出结构模型，例如 skeletalmorph 路径可以导出带 skin 的模型；但如果导出截图仍呈现 mask/tint 未复原、白模、黑白块或主要材质不完整，就只能作为模型/材质诊断，不能进入动画 smoke。`P_actor_boynpc_01` 本轮导出 Mesh/skin/bbox 和 validator 无 error，但重建后的 `model_validation.status=warning`，因为标准 `baseColorTexture` 只覆盖 `1/14` 个 primitive；截图也显示 ColorMask/Tint 预览材质仍明显不完整，并且没有显式动画候选，因此不能算生产动画样本。

默认候选扫描优先服务 prefab/Animator/SkinnedMeshRenderer 角色主线，不做全库 `MeshRenderer + MeshFilter` 静态 join。需要为场景、建筑、道具寻找第一阶段样本时，可以在同一命令上追加 `--include_static_meshes`，报告会额外列出 `StaticRendererModel` 行。这个开关只扩大模型候选诊断范围，不会让静态模型自动进入动画 smoke；静态候选仍必须先导出并通过 Mesh、UV、材质、贴图、bbox 验收。静态 Renderer 如果缺少 container 语义，报告会标记 `staticRendererNeedsContainerReview`；`initialassets/intro` 这类 intro 小场景会标记 `introOnly`，不能作为生产 smoke 证据。

候选报告里的 `materialCount` 表示 Renderer 材质引用数量，`resolvedMaterialCount` 表示这些引用能在当前 `unity_source_index.db` 中解析到真实 `Material` 对象的数量；`texturedMaterialCount` 表示有确定性 `Material -> Texture` 关系的材质数量，`materialTextureRefCount` 表示这些材质指向的贴图引用数量。`TexturedRendererContainer` 是从 `Material -> Texture` 反查到 Renderer 和 `assetBundle.containerPreload` 的容器线索，只说明“这里存在确定性贴图链路”，不等于该容器就是完整主模型。`RawContainerModel` 是从 `AssetBundle.containerAsset/containerPreload` 找到的成组 Mesh/Avatar，例如 Endfield 的 `assets/beyond/arts/entity/actor/.../models/*.fbx`；它说明原始模型部件存在，但缺 prefab Renderer/Material 绑定时会标记 `sourcePart,rawModelNeedsPrefabOrRendererBinding,noMaterial`，不能作为模型 smoke 或动画 smoke 证据。报告顶层的 `usableForModelSmokeCount` 只统计没有源索引排除提示的候选；如果它为 0，`animationGateDecision` 会明确提示不要从这批样本进入动画 smoke。`noVisibleMesh`、`noMaterial`、`missingResolvedMaterial`、`missingMaterialTexture`、`optimizedAnimatorMissingAvatar`、`containerLeadNeedsExportValidation`、`rawModelNeedsPrefabOrRendererBinding` 这类 `excludeHint` 是模型优先门禁信号：它们说明当前样本最多适合诊断，不应直接进入动画 smoke。尤其是只用单个 `.blc` 或少量 bundle 建索引时，材质 CAB 常会被裁掉，应改用完整 VFS/完整源目录索引再判断模型质量。

模型通过第一阶段验收后，可以直接从源索引列出这个模型的显式动画引用链，用来确认“动画候选从哪里来”：

```powershell
dotnet AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.dll `
  --list_source_model_animations "D:\Assets\fangzhou\Endfield_FullGame_VFS_SourceIndex_MonoPPtr_Current\unity_source_index.db" `
  --preview_model "P_actor_endmin(f|m)_01" `
  --preview_output "D:\Assets\fangzhou\Endfield_SourceModelAnimations_ActorGender_Current" `
  --source_candidate_limit 40
```

这个命令按已选模型收窄查询，只列 `GameObject -> Animator -> AnimatorController -> AnimationClip`、legacy `GameObject -> Animation -> AnimationClip`，以及保守的 `MonoBehaviour config -> model + Avatar <- Animator.controller -> AnimationClip` 这类 Unity 显式关系，输出 `source_model_animation_candidates.json` 和 `source_model_animation_candidates.csv`。它不会导出动画，不会写 `model_animations.json`，也不会把候选标成可播放。查询顺序必须从已选 GameObject 出发，再查它自己的 Animator / Animation 组件或同源配置资产，避免在大源索引里为了单模型关系诊断全库扫描 Animator。报告里的 `modelReadyForAnimation=false` 是刻意保守的：源索引只能证明引用关系存在，不能证明模型 Mesh、UV、材质、贴图、skin 或 bbox 已经合格。同名 GameObject 可能有多个 prefab、UI、postmodel 或诊断变体；生产验证应优先使用已经通过静态模型验收的具体 pathId，或把同名结果先和 `source_model_candidates.json` / `model_validation.json` 对齐后再进入动画。报告还会写 `animatorDiagnostics`，列出匹配 GameObject 是否有 Animator、Avatar、Controller 和 Controller clip；这只解释为什么显式候选为 0，例如 Endfield 常见的“同名模型有 Avatar 但 AnimatorController 为空”，不能把同名 Animator、Avatar 或骨架兼容升级成动画关系。报告还会写 `monoBehaviourPPtrDiagnostics`，列出哪些 MonoBehaviour PPtr 字段指向选中模型，以及同一个 MonoBehaviour 还指向哪些 Avatar、Prefab、Clip、Controller 或其他对象；这类字段是确定性的配置线索，可以帮助追查角色数据链路，但不能单独升级为可播放动画候选。若源索引包含当前版本的 `assetBundle.containerAsset` / `assetBundle.containerPreload` / `resourceManager.container`，报告会在命中模型后回填 `model.containerPath`，并把 Dialog / Timeline / UI / cutscene / tmp / preview 等容器或路径语义写入 `reviewHint`；旧源索引缺 container 关系时该字段可能为空，需要先用 `--verify_source_index` 确认是否应该重建源索引。这类候选最多作为诊断线索；只有模型静态验收通过、动画 sidecar/曲线可稳定解码、并且清晰截帧证明动作合理后，才能进入正式动画 smoke 或 `relation_animations` 推荐结果。

2026-06-24 针对 Endfield 大源索引补充了缺 `idx_source_relations_to` 时的轻量 shared Avatar 桥接：如果源索引有 `idx_source_relations_from` 和 `idx_source_relations_relation`，但没有巨大的 `to_file/to_path_id` 反查索引，`--list_source_model_animations` 会先在选中模型同一个 SerializedFile 内找 MonoBehaviour 配置，要求该配置同时 PPtr 到选中模型和 Avatar；随后只扫描很小的 `animator.avatar` 关系集合，找显式使用同 Avatar 且有 `animator.controller -> animatorController.clip` 的 Animator。报告会写 `sourceIndexPerformance.sharedAvatarBridgeScanMode=forward_config_plus_relation_scan`。这条 fallback 已能在当前主索引上重新找回 `P_actor_pelica_01#5105657407662044875` 的 40 条 sharedAvatarController 候选，并找到第二个强关系候选 `P_actor_endminf_01#-8558895232557186863 -> SK_actor_endminf_01Avatar -> P_npc_npc_chr_0003_endminf -> AC_npc_human_girl_endminf_optNew`。同一逻辑复查 `P_actor_aglina_01` 和 `P_actor_mifu_01` 仍为 0 候选，说明它们目前只能作为模型正样本或 Avatar 线索，不能进入动画 smoke。

2026-06-24 针对 `P_actor_endminf_01#-8558895232557186863` 做了模型优先导出回归。命令使用完整 Endfield VFS、主 `unity_source_index.db` 和 `--path_ids` 精确选择模型，输出到 `D:\Assets\fangzhou\Endfield_EndminfActor_ModelFirst_RelationClosureSkip_Current`。本轮修复了自动 Endfield VFS target filter 的一个慢点：如果 `source_relations` 没有以 `from_file` 开头的查询索引，工具不会再用 `WHERE from_file=$cab` 对 1.58 亿关系做 CAB 闭包全表扫描，而是记录 warning/profile，并只使用已有索引支撑的 `source_externals` 闭包。该导出约 94 秒完成，`model_validation.status=ok`，glTF validator 无 error，模型有 56 meshes、19 materials、43 images、56 skins，Blender 静态截图显示不是白模或大面积 mask 块。它可以作为 Endfield 模型第一阶段正样本，但动画关系仍是 shared Avatar bridge，后续必须继续做 TRS 写回和清晰动画截帧验证，不能直接写成生产推荐。

大源索引的反查性能依赖 `idx_source_relations_to`。如果报告顶层 `sourceIndexPerformance.hasRelationToIndex=false`，`--list_source_model_animations` 不会执行需要 `to_file/to_path_id` 的全库反查，也会跳过 `monoBehaviourPPtrDiagnostics`；但只要还有 `idx_source_relations_from` 和 `idx_source_relations_relation`，它会使用保守的 forward config shared Avatar 桥接，避免在 Endfield 这种 `source_relations` 过亿行的库上全表扫描卡住。此时不能用“反查索引不存在”证明没有共享 Avatar 桥接关系；应看报告里的 `sourceIndexPerformance.sharedAvatarBridgeScanMode`，或在磁盘和时间足够时对源索引副本补反查索引后再查。

Endfield 还存在一种确定性但更保守的共享 Avatar 桥接关系：`MonoBehaviour` 配置同时 PPtr 指向已选模型和一个 Unity `Avatar`，另一个带 `AnimatorController` 的 `Animator` 也 PPtr 指向同一个 Avatar，再由 controller 指向 clip。当前工具把这类链路标成 `relationKind=sharedAvatarController`，并把 config、Avatar、Animator、controller、clip 全部写入 `relationEvidence`。它不是名称猜测，也不是目录猜测；但因为 controller 挂在另一个运行时实例上，默认必须写 `explicitCandidateRequiresVisualValidation=true`、`relationReviewHint=sharedAvatarBridgeNeedsVisualValidation`，并且在清晰模型截图、动画截图和 glTF TRS 报告通过前，不进入 `relation_animations` 默认可用推荐。这个桥接关系可以用来恢复 Avatar asset、生成 `UnityBakeAccelerated` request 或做小范围诊断，但不能跳过模型第一阶段门禁。

2026-06-24 Library SQLite 也补齐了同一条缺反查索引时的 forward config shared Avatar 桥接。`D:\Assets\fangzhou\Endfield_EndminfActor_IdleAdditive_ModelAnimationAsset_Current` 重建 `library_index.db` 时，旧逻辑因为没有 `idx_source_relations_to` 会让 targeted import 报错并回退加载完整 source graph，单次约 198 秒；新逻辑用 `idx_source_relations_relation` 扫小集合，约 1.9 秒完成，并保留 `P_actor_endminf_01 -> A_actor_endminf_idle_loop_additive` 的 1 条 `relation_source=explicit` 候选。SQLite `raw_json.relationEvidence.bridge=forwardConfigPrefabAvatarToAnimatorController`，同时保留 `SK_actor_endminf_01Avatar`、`AC_npc_human_girl_endminf_optNew` 和 `ClothCombine.SequenceNode` 等证据；它仍是 `needs_validation`，不能跳过动画视觉验收。

同一 Endminf 小样本的动画求解状态：`UnityBakeAccelerated` 因 sidecar 还没有展开 Endfield ACL scalar / Humanoid Muscle 逐帧 float curves 而失败，错误为 `Animation sidecar has no decoded float curves for UnityBakeAccelerated`；完整 `AnimationClip` / `PlayableGraph` 诊断现在通过 `UnityOraclePlayableGraphFallback` 自动触发，当前输出在 `D:\Assets\fangzhou\Endfield_EndminfActor_IdleAdditive_AcceleratedAutoFallback_Run_Current`，可写回 glTF TRS 且 validator 无 error，但报告为 `needs_review`，截图显示像 additive/controller 上层动作单独套在 rest 上。因此它只能证明 UnityOracle/PlayableGraph 链路可用于对照，不能计入生产动画 smoke。

2026-06-24 起，`--generate_unity_bake_accelerated_request_from_library` 遇到两类 fast path 不安全场景时，会尝试自动生成 `UnityOraclePlayableGraphFallback`：一是 sidecar 没有真实逐帧 `decoded.floats`，二是有 limb goal / TDOF 这类 `HumanPoseHandler.SetHumanPose/SetInternalHumanPose` 不消费的 Humanoid 曲线。fallback 必须能从 `ImportedAnimationClip` 或 `unityAnimationClips` 精确匹配到原始 Unity `AnimationClip`；没有精确 clip asset 时仍然失败，不能按名字或目录猜。fallback report 写在输出目录 `unity_bake_accelerated_fallback_report.json`，`mode=UnityOraclePlayableGraphFallback`、`productionReady=false`，用于说明这是完整 AnimationClip/PlayableGraph 语义的自动化对照路径，不是 `ProductionDirect`。

如果 fallback 是因为 limb goal / TDOF 触发，CLI 会自动打开 unsupported Humanoid 曲线 probe；存在 `Left/Right Foot/Hand T/Q` 这类手脚 goal 曲线时，还会自动启用 editor-curve IK goal driver。fallback report 的 `fallbackDiagnostics.probeUnsupportedHumanoidCurves` 和 `fallbackDiagnostics.enableEditorCurveIkGoalDriver` 会记录这两个决定。这个行为只是为了让 UnityOracle/PlayableGraph 诊断尽量接近 Unity 原生手脚 IK 语义，输出仍必须保持 `productionReady=false`，并用 rest/start/mid/end 和手部近景做清晰视觉验收。

IK goal 诊断结果要优先看 `unity_bake_apply_report.json` 的 `animationSolve.ikGoalDriverVerification`。`statusBasis=final_dominant_goal` 表示按每帧同一 hand/foot 的最高 layer weight 目标做最终对照，避免把逐层统计误当最终姿态；`maxIkReadbackDistanceToGoal=0` 只说明 Unity `Animator.SetIKPosition/Rotation/Weight` 已接收目标，不代表最终骨骼一定贴合，也不代表动作正确；`maxLayerTargetSpread=0` 说明当前恢复出的多个 layer 没有对同一个 goal 写互相冲突的位置，如果仍有残差，应继续查 goal 目标点定义、TDOF、Humanoid 约束、controller 参数和 IK 权重语义。`finalGoals[*].closestDescendant*` / `boneTargetCandidates` 用于判断 goal 更像对齐 HumanHand/HumanFoot、手指、脚趾、wrist 修正骨，还是逐帧变化的最近子节点。Pelica `run_loop` 当前验证目录 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_ClosestDescendantDiag_Current` 的结果是：glTF validator 无 error，IK readback 距离为 `0`，layer target spread 为 `0`，但手/脚最终残差仍约 `0.14m` / `0.19m`，报告保持 `productionStatus=diagnostic_limb_ik_goal_driver`、`writesReusableGltfTrsCandidate=false`。这类结果只能作为 UnityOracle 诊断和直接 solver 回归依据，不能进入生产动画 smoke。

2026-06-25 追加 geometry hint 诊断字段：`AnimeStudioEditorCurveIkGoalDriver` 会在写 Hand/Foot IK goal 时，把当前帧的 elbow/knee Transform 作为 `Animator.SetIKHintPosition` 写入，并在 `goals[*].hint`、`hintPath`、`hasIkHintReadback`、`maxIkHintReadbackDistanceToHint`、`maxIkHintReadbackWeight` 里记录。`D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_GeometryHintDiag_Current` 复测显示 hint readback 完整（距离 `0`、权重 `1`），但 hand/foot 最终残差仍约 `0.145m` / `0.191m`，报告仍为 `goal_alignment_needs_review`。这说明 hint 写入成功只能排除“IK hint 没喂进去”，不能证明 goal 目标点、TDOF、controller runtime 语义或视觉动作已经正确。

同日追加 direct two-bone IK 诊断字段：`AnimeStudioEditorCurveIkGoalDriver` 会在 Unity/PlayableGraph 采样后、记录 glTF TRS 前，用 Humanoid 上臂/前臂/手和腿/小腿/脚链条做一次简单 CCD，直接追当前帧 Hand/Foot goal。报告字段包括 `directTwoBoneIkAttemptCount`、`directTwoBoneIkSolveCount`、`maxDirectTwoBoneIkPostDistanceToGoal` 和 `maxDirectTwoBoneIkImprovement`。`D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_DirectTwoBoneIkDiag_Current` 复测显示求解运行了 `88` 次且没有缺链，但最大改善只有约 `9.7e-7m`，手脚残差仍约 `0.191m`，所以它只能排除“简单二骨链追目标即可修好”的假设；结果继续保持 `needs_review`，不能进入生产 smoke。

二骨诊断现在还会写链长/可达性字段：`maxDirectTwoBoneIkTargetDistanceFromUpper`、`maxDirectTwoBoneIkChainLength`、`maxDirectTwoBoneIkReachShortfall`、`maxDirectTwoBoneIkInsideMinReachAmount`。`D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_TwoBoneReachDiag_Current` 显示 Pelica run 的 Hand/Foot 目标都超出二骨链最大可达距离，且 `reachShortfall` 基本等于最终残差：手约 `0.143/0.145m`，脚约 `0.181/0.191m`。这说明当前目标点不是简单的 HumanHand/HumanFoot pivot 可达点；后续要查 RootT/RootQ + body-local goal 空间、Avatar humanScale、goal endpoint/pivot 和 controller IK 权重语义，不能继续用固定 wrist/foot offset 或更强 CCD 硬补。

同一诊断还会写 body-local scale-fit 字段：`min/maxDirectTwoBoneIkAvatarHumanScale`、`min/maxDirectTwoBoneIkReachFitScale`、`maxDirectTwoBoneIkReachFitScaleOffsetFromCurrentGoal`。这些字段只回答“如果沿当前 body-local goal 方向缩放，缩到二骨链刚好可达需要多少比例”，用于区分统一尺度问题和 endpoint/controller 语义问题。`D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_GoalScaleFitDiag_Current` 显示 Pelica run 的 humanScale 为 `1.0`，四肢 fitScale 不收敛到统一比例，手部还会在接近 `0` 到 `1.7` 间跳变；因此它不能用一个全局 humanScale、固定 wrist/foot offset 或更强 CCD 修好。报告仍必须保持 `diagnostic_limb_ik_goal_driver` / `writesReusableGltfTrsCandidate=false`，下一步继续查 goal endpoint/pivot、IK 权重曲线、controller runtime 参数、additive/layer 语义和 Unity Humanoid IK 解算时机。

`goalSpaceCandidates` 也会写候选目标自身的二骨链可达性：`min/maxTargetDistanceFromUpper`、`min/maxReachShortfall`、`maxInsideMinReachAmount`。apply report 里每个 `finalGoals[*]` 还会提升 `bestReachableGoalSpaceCandidate`、`bestReachableGoalSpaceCandidateMaxPostIkDistance` 和 `bestReachableGoalSpaceCandidateMaxReachShortfall`。如果 `bestReachableGoalSpaceCandidate` 为空，说明当前列举的 Root/body/raw world 空间解释没有一个能让该 hand/foot goal 在整段采样中保持二骨链可达。`D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_GoalCandidateReachDiag_Current` 复测中四个 goal 都为空；手部最好的 `body_position_root_rotation` 仍有约 `0.09..0.10m` reach shortfall，脚部当前 body-local 仍有约 `0.18..0.19m` shortfall。因此后续不要继续堆同类 Root/body 空间变体，应转向 Unity Humanoid IK endpoint/pivot、goal 权重/effector、controller runtime 参数和 additive/layer 组合。

`axis_basis_scan_*` 是同一套 IK goal 诊断里的轴基穷举字段。它会对 Hand/Foot T 的 body-local 向量做 48 种 signed axis basis 重排，只计算候选 world target 的距离和二骨链可达性；它不会改变 Unity `SetIKPosition` 的实际目标，也不会让诊断输出进入生产。`D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_AxisBasisDiag_Current` 复测显示 LeftHand/RightHand 分别存在可达轴基，但命中的轴基不一致；LeftFoot/RightFoot 仍无全段可达轴基，最佳 shortfall 约 `0.149m/0.145m`。这说明 Pelica run 当前不像是“统一坐标轴整体翻错”导致的，后续应继续追 endpoint/pivot、IK 权重/effector、controller runtime 参数和 additive/layer 语义。

`animator_body_*` 是同一套诊断里的 Body Transform 对照候选。Unity Humanoid 文档说明 muscle curves 与 Hand/Foot IK goals 相对 Body Transform 保存，因此该候选用 `Animator.bodyPosition/bodyRotation` 替代从 `RootT/RootQ` 或 `MotionT/MotionQ` 解析出的 body world transform，只写诊断距离，不改变实际 IK target。`D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_AnimatorBodyGoalDiag_Current` 复测显示手部候选可达性较好，但 LeftFoot 仍约 `0.18m`、RightFoot 约 `0.14m` 残差，不能形成统一修复。因此当前不要把 `animator_body_local` 直接升级为默认求解；它只排除了“简单替换 Body Transform 来源即可修复 Pelica run”的假设。

同轮追加 IK goal 权重诊断：`ikGoalDriverVerification.weightRule`、`hasExplicitIkGoalWeightCurves`、`explicitIkGoalWeightCurveCount` 会说明当前是否找到明确 Hand/Foot IK 权重曲线。Pelica run 的 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_IkWeightMissingDiag_Current` 显示 `hasExplicitIkGoalWeightCurves=false`、`explicitIkGoalWeightCurveCount=0`；也就是说当前 driver 使用 layer weight 是诊断近似，可能过度施加游戏运行时会由 IK weight 或 controller 参数门控的 goal。只要缺少真实权重语义，`usedIkGoalDriverDiagnostic=true` 的结果仍必须保持 `diagnostic_limb_ik_goal_driver`，不能进入生产 smoke。

2026-06-25 追加 IK pass 采样有效性字段：`unityBakeAnimatorControllerIkPassEffectiveForSampling`、`unityBakeAnimatorControllerIkPassSamplingMode` 和 `unityBakeAnimatorControllerIkPassSamplingWarning`。如果报告显示 `ikPassEnabledLayerCount>0` 但 `ikPassSamplingMode=animation_layer_mixer_diagnostic_unproven`，表示 worker 确实创建了启用 IK pass 的 controller copy，但本次输出来自 `AnimationLayerMixerPlayable` clip mixer；此时 `OnAnimatorIK` target/readback 只能作为诊断，不能证明 Unity Humanoid IK 已经改写最终骨骼。Pelica run 的 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_IkPassReport_Current` 就是该状态，结果仍必须保持 `needs_review` / `diagnostic_limb_ik_goal_driver`。

同日追加 recovered controller runtime mask promotion。`AnimeStudioAnimatorControllerRecovery` 会先尝试把 layer skeleton mask 中已有的 transform path 写成真实 `AvatarMask` 子资产并挂到 `AnimatorControllerLayer.avatarMask`；如果只有 path hash，则只记录 `createdAvatarMaskCount`、`recoveredAvatarMaskPathCount`、`skippedAvatarMaskPathCount`，不会猜路径。Pelica controller recovery 诊断目录 `D:\Assets\fangzhou\Endfield_PelicaControllerRecovery_AvatarMaskDiag_Current` 显示当前三层 mask 都是 hash-only，`createdAvatarMaskCount=0`、`skippedAvatarMaskPathCount=264`。因此 bake worker 会在目标模型层级已经存在时，用 runtime transform lookup 解析 hash，并把可完整解析的 mask 提升到 in-memory `AnimatorControllerLayer.avatarMask`；成功后采样改走 `AnimatorControllerPlayable`，报告会显示 `unityBakeAnimatorControllerLayerMasksApplied=true`、`unityBakeAnimatorControllerIkPassEffectiveForSampling=true`、`unityBakeAnimatorControllerIkPassSamplingMode=animator_controller_playable`。Pelica run 复测 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_RuntimeMaskPromotion_Current` 已触发该路径，glTF validator 无 error，`changedTrackCount=104`；但 apply report 仍为 `needs_review / diagnostic_limb_ik_goal_driver`，`writesReusableGltfTrsCandidate=false`，因为 `maxFinalIkBoneMoveDistance` 和 `maxFinalIkDistanceImprovement` 仍只有约 `6.7e-7m`。这说明 layer mixer 绕过 controller 不是当前最后主因，后续应继续查 IK goal 权重、endpoint/effector、controller runtime 参数或直接 IK/TRS 求解。

同日新增 `unityBakeAnimatorControllerRuntimeParameterSummary`。它从 `animatorControllerParameterDiagnostics` 中保守提取参数名里包含 `weight`、`SWeight`、`layer`、`IK`、`lookat`、`speed`、`time`、`cycle` 等 token 的候选，只作为 runtime 参数门控诊断，不改变采样。Pelica 复测目录 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_RuntimeParamSummary_Current` 显示 `totalParameterCount=91`、`possibleRuntimeWeightCount=49`、`possibleLayerWeightCount=16`、`possibleIkWeightCount=6`、`possibleLookAtWeightCount=12`、`nonZeroCandidateCount=8`。其中 `LoopBodyAddWeight/LoopClothAddWeight` 默认值为 0，但当前 layer 采样权重仍来自 recovered layer default 1；这提示后续要恢复 AnimatorController 参数绑定和运行时参数值，不能继续把 layer default weight 当作完整 runtime 语义。该字段不会让任何 `needs_review` 结果升级为可播放。

`unityBakeAnimatorControllerRuntimeParameterSummary.layerParameterHints` 会进一步按 layer 名称和 controller 参数名称的结构相似性，列出疑似 runtime layer/IK/additive weight gate。例如 `LoopBodyAddLayer` 可以提示 `LoopBodyAddWeight`。如果命中默认值为 0 的 layer weight 候选，apply report 的 `animationSolve.reusableCandidateBlockedReasons` 会增加 `animator_controller_runtime_layer_weight_gate_unresolved`。这只是诊断门禁，不会自动改采样权重；必须找到确定性参数绑定或运行时参数来源并通过视觉验收后，才能把它变成生产规则。

同日补充 `AnimatorState.iKOnFeet` 证据链：`unity_file_inspect.json` 的 state、`unity_source_index.db` 的 `animatorController.clip.details.stateIKOnFeet`、`AnimatorControllerContextRefresher`、ImportedAnimatorController recovery request 和 Unity bake layer diagnostics 都会保留状态级 Foot IK 开关。Pelica filtered inspect 目录 `D:\Assets\fangzhou\Endfield_PelicaControllerInspect_StateIKOnFeet_Filtered_Current` 显示 `AC_npc_human_girl_pelica_optNew` 的 80 个 state 全部 `iKOnFeet=false`；request-only recovery 目录 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_ReexportForFallback_Current\.as_browser_cache\diagnostics\imported_animator_controller_recovery_20260624_214629` 也确认 80 个 state 都带 `iKOnFeet=false`。因此 Pelica run 的脚部 IK 残差不能继续归因于漏传 `m_IKOnFeet`，后续应查 IK 采样时机、权重/effector、目标点语义和 controller layer/additive 组合。

同日继续收紧 target point 诊断：`finalGoals[*].bestAnyBoneTargetCandidate` 表示“某个候选在它出现的帧里距离最小”，可能只覆盖少数帧；`bestStableBoneTargetCandidate` 要求覆盖率至少 `80%`，旧字段 `bestBoneTargetCandidate` 也改为指向稳定候选，避免把短暂 closest descendant 误读成整段动作目标点。Pelica run 复测中左右手的 `bestAny` 都是 closest descendant，但覆盖率只有 `0.318`；稳定候选仍是 `human:LeftHand/RightHand`，覆盖率 `1.0`，最大距离约 `0.143m/0.145m`。因此这条样本当前只能说明目标点语义仍未稳定解释，不能用少数帧贴近修正骨或手指来放行。

2026-06-25 追加本地 offset 诊断：`finalGoals[*].boneTargetCandidates[*]` 会写 `localOffsetToDominantGoal`、`localOffsetSpan`、`minLocalOffsetLength` 和 `maxLocalOffsetLength`，用于判断 hand/foot goal 是否只是缺了一个稳定的手掌/脚底端点偏移。Pelica run 在 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_GeometryHintDiag_Current` 复测中，左右手脚稳定候选覆盖率都是 `1.0`，但 `localOffsetSpan` 约 `0.16m-0.20m`，最小长度接近 `0`，最大长度接近当前残差。这说明当前残差不是“给 HumanHand/HumanFoot 补一个固定 local offset”就能解决；下一步应继续追动态 goal 语义、TDOF/root/body、controller runtime 参数、IK 权重和 additive/layer 组合。

2026-06-25 追加 TDOF 摘要：`unityBakeEditorCurveHumanPoseDiagnostic.tdofVector3Summaries` 会把 `SpineTDOF`、`ChestTDOF` 这类三轴曲线聚合为 `valueRange`、`valueRangeSpan`、`min/maxLength` 和 `maxDeltaFromFirst`。Pelica run 复测目录 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_TdofSummaryDiag_Current` 显示 `SpineTDOF` 与 `ChestTDOF` 各有 3 条曲线，但 `dynamicCurveCount=0`、`valueRangeSpan=0`、`maxDeltaFromFirst=0`。因此这条样本当前手脚残差不能归因于动态 TDOF；后续应优先继续查 IK goal 目标点定义、controller runtime IK 权重、additive/layer 组合和 Unity Humanoid 解算残差。这个字段是诊断证据，不会把 `diagnostic_limb_ik_goal_driver` 升级为生产通过。

同日继续追加 IK 最终骨骼运动量诊断：`animationSolve.ikGoalDriverVerification` 会写 `maxFinalIkBoneMoveDistance`、`maxFinalIkDistanceImprovement`、`maxFinalIkDistanceRegression`，每个 `finalGoals[*]` 也会写 `hasPreIkBone`、`maxPreIkDistanceToDominantGoal`、`maxIkBoneMoveDistance`、`maxIkDistanceImprovement`、`maxIkDistanceRegression`。这些字段用来回答“Unity IK pass 是否真的把手脚骨骼往目标点移动”，而不是只看 `SetIKPosition/Weight` readback。Pelica `run_loop` 复测目录 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_IkMovementDiag_Current` 显示 `maxFinalIkBoneMoveDistance≈7.3e-7m`、`maxFinalIkDistanceImprovement≈6.4e-7m`，但最终手/脚到目标仍约 `0.145m` / `0.191m`。这说明当前 batch/PlayableGraph 采样路径里 IK 目标和权重虽然被 Unity 接收，最终解算却几乎没有改写骨架；下一步应查 `OnAnimatorIK` 采样时机、controller runtime IK 权重语义、Animator layer/PlayableGraph 评估顺序和 Humanoid IK 约束，而不是继续按“缺 hint / 固定 offset / 动态 TDOF”修补。该样本截图有改善但手部仍不自然，仍保持 `diagnostic_limb_ik_goal_driver`。

同日继续做负向对照：把 recovered controller 的 IK 诊断路径临时切到 `AnimatorControllerPlayable`，并修正 recovered controller 默认 state 播放，复测 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_ControllerPlayableIkDiag_Current`。结果不再出现 `State could not be found`，但 `maxIkBoneMoveDistance` 仍只有 `6.7e-7m` 左右；再临时追加 `Animator.Update(0)` 后，`OnAnimatorIK` 调用从 `72` 增加到 `138`，骨骼移动仍是 `6.5e-7m` 量级。这两个实验说明当前问题不是单纯 `AnimationLayerMixerPlayable` 覆盖、state 字符串没播到，或 IK 结果需要 0 delta Animator update 才提交。默认代码不保留这两个无效强制路径，只保留 recovered controller 使用默认 state 的小修；后续应转向恢复/求解 IK goal 的真实语义、权重曲线、controller 参数和可能的直接 Humanoid IK 求解。

同一自动 fallback 入口还会复用 `Assets/AnimeStudioBake/ImportedAnimatorController/*.controller` 中已经恢复的 RuntimeAnimatorController。匹配依据来自候选的 `relationEvidence.controllerName` / `animatorControllerContext.controllerName`、controller PathID、模型/动画组合 key 或显式 `--unity_animator_controller`，不是按目录或角色名猜。命中后 `unity_bake_request.json.unityAssetPaths.animatorController` 会写入真实 controller 路径，fallback report 会记录 `unityAnimatorController`、`unityAnimatorControllerSource` 和 `unityAnimatorControllerMatchKey`。

2026-06-24 之后，Humanoid clip 只要带确定性 `animatorControllerContext`，但没有精确命中 ImportedAnimatorController / 原始 RuntimeAnimatorController，`--generate_unity_bake_accelerated_request_from_library` 默认不再自动生成 temporary single-state controller 预览；它只写 `unity_bake_accelerated_fallback_report.json`，`status=blocked_generated_controller_context`。这是为了避免 Pelica idle 旧样本那类“双手抬高、手腕扭曲但文件能动”的误导输出。确实要研究 generated controller 时，必须显式加 `--unity_bake_allow_generated_controller_diagnostic`，并且结果只能作为诊断，不能进入生产 smoke 或可复用动画结论。

`missing_decoded_float_curves` 和 limb/TDOF 两种 fallback 都必须执行同一套 controller 匹配规则。2026-06-24 修正后，`D:\Assets\fangzhou\Endfield_EndminfActor_IdleAdditive_ControllerLookupFix_Current` 的 fallback report 已能命中 `AC_npc_human_girl_endminf_optNew.controller`；Unity result 为 `provided_runtime_controller`，layer 诊断映射回原始 `sourceLayerIndex=0/6/7`，并应用 `223/95` 条 skeleton mask。该样本仍不能生产通过：选中的 `A_actor_endminf_idle_loop_additive` 来自非 base controller layer，候选没有同状态 `baseLayerClip` 证据，apply report 会写 `productionStatus=needs_animator_controller_base_layer_context` 和 blocked reason `selected_controller_layer_clip_missing_base_layer_context`。这类结果只能作为 controller/layer 诊断，不能因为 glTF TRS 写出、validator 无 error 或双臂没有飞骨就当完整 idle。

`missing_decoded_float_curves` fallback 现在也会默认打开 `probeMuscles`，用于补齐 Unity editor curve 分类诊断。它不会因此自动开启 IK goal driver；只有 fast path 已经从 decoded curves 明确发现 hand/foot goal 时，才自动启用 IK 诊断。`D:\Assets\fangzhou\Endfield_EndminfActor_IdleAdditive_MissingDecodedProbe_Current` 验证到：fallback report 写 `probeUnsupportedHumanoidCurves=true`、`enableEditorCurveIkGoalDriver=false`，apply report 的 `editorCurveCategorySummary` 能看到 `humanoidLimbGoal.dynamicCount=28`、`humanoidRootBody.dynamicCount=3`、`humanoidTdof.dynamicCount=0`。该样本仍是失败诊断：非 base clip 缺 `baseLayerClip`，且清晰截图双臂高举/外展，所以 blocked reasons 继续包含 `selected_controller_layer_clip_missing_base_layer_context` 和 `dynamic_limb_goal_curves_not_solved`。

Endminf 后续诊断应优先使用 full decoded sidecar。`D:\Assets\fangzhou\Endfield_EndminfActor_IdleAdditive_ReexportFullCurves_Current` 用同一模型/动画 PathID 重新导出并追加 `--export_full_decoded_animation_curves` 后，`A_actor_endminf_idle_loop_additive.animation_asset.json` 写出 `decoded.status=ok`、`translations=356`、`rotations=356`、`scales=356`、`floats=143`，ACL manifest 为 `outputTrackCount=356`、`rootTrackCount=28`、`floatCurveCount=143`。这说明旧 `ModelAnimationAsset_Current` 的 `missing_decoded_float_curves` 主要是导出输入不完整，不应再拿它判断解码器能力。基于新库的自动求解输出 `D:\Assets\fangzhou\Endfield_EndminfActor_IdleAdditive_ReexportFullCurves_AutoSolve_Current` 已把 fallback reason 收敛为 `unsupported_limb_goal_or_tdof_curves`，request 同时使用 `ImportedAnimationClip`、`ImportedAnimatorController`、`ImportedAvatar`，并打开 `probeMuscles=true`、`enableEditorCurveIkGoalDriver=true`。报告仍必须阻断生产：`animationSolve.productionStatus=needs_animator_controller_base_layer_context`，blocked reasons 包含 `selected_controller_layer_clip_missing_base_layer_context` 和 `limb_ik_goal_driver_diagnostic_unverified`；截图里手腕不再麻花、双臂不再举到头顶，但整体仍是双臂外展的 additive/controller 片段感，不是完整 idle。

2026-06-24 追加复用原神旧 Browser/Unity bake 链路经验：旧流程里最有价值的不是手工 Unity 工程，而是它已经把 Avatar oracle、ImportedAnimationClip、AnimatorController context、bake cache 终态和报告门禁分清。现在 `--refresh_animator_controller_contexts` 与源索引写入都会额外处理一种 Endfield 常见情况：选中的 clip 来自非 base layer，但同一个 `AnimatorController` 的 base layer 默认状态能确定解析成唯一 clip。此时会写入 `animatorControllerContext.baseLayerClip`，来源标为 `animator_controller_inspect.base_layer_default_state` 或 `AnimatorController.baseLayer.defaultState`；如果 base layer 默认状态需要复杂 BlendTree 参数、多个 child 或运行时条件，就继续不写，不能靠动画名/目录名猜。

2026-06-25 追加普通 `--generate_unity_bake_request_from_library` 的同类保护：如果动画 sidecar 已经解出动态 `Left/Right Hand/Foot T/Q` 曲线、候选要求 Humanoid bake、并且 request 能使用原始 `RuntimeAnimatorController`，CLI 会自动打开 `probeMuscles=true` 和 `enableEditorCurveIkGoalDriver=true`，同时在 request 的 `autoEnabledIkGoalDriver`、`autoEnabledIkGoalDriverReason` 和 `animeStudioAssets.animation.unsupportedHumanPoseCurveSummary` 里记录原因。这个行为只用于 UnityOracle/PlayableGraph 诊断，apply report 仍会把 `usedIkGoalDriverDiagnostic=true` 的结果阻断为 `needs_review`，不能因为双手不再飞到头顶就算生产动画通过。

同日继续补普通 request 入口：`--unity_bake_sample_recoverable_skipped_layers_diagnostic` 现在也会传给 `--generate_unity_bake_request_from_library`，不再只在批量 bake 入口生效。Pelica 复测 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_ControllerDefaultSampledLayer_Rerun_Current` 显示 request/result 都为 `sampleRecoverableSkippedLayersDiagnostic=true`，`LoopClothAddLayer` 诊断采样数为 `1`，`fallbackZeroBlendParameterLayerCount=0`，`defaultParameterSource=controllerDefaultValue`。报告仍保持 `status=needs_review`、`productionStatus=diagnostic_recoverable_skipped_layer_sampling`、`writesReusableGltfTrsCandidate=false`，说明这只是把 controller 默认参数证据稳定接入诊断采样，不是生产通过。

Endminf 验证命令示例：

```powershell
dotnet run --project .\AnimeStudio.CLI\AnimeStudio.CLI.csproj -f net9.0-windows --no-restore -- `
  --refresh_animator_controller_contexts "D:\Assets\fangzhou\Endfield_EndminfActor_IdleAdditive_ReexportFullCurves_Current" `
  --unity_file_inspect "D:\Assets\fangzhou\Endfield_EndminfControllerInspect_EndfieldGame_Current\unity_file_inspect.json" `
  --source_index "D:\Assets\fangzhou\Endfield_FullGame_SourceIndex_PersistentStreamingFix_Current\unity_source_index.db" `
  --preview_model "P_actor_endminf_01" `
  --preview_animation "A_actor_endminf_idle_loop_additive"
```

本次刷新加载 `71` 条 controller context，更新 `1` 条候选；后续自动 fallback 输出 `D:\Assets\fangzhou\Endfield_EndminfActor_IdleAdditive_BaseLayerClipContext_Run_Current`。apply report 不再出现 `selected_controller_layer_clip_missing_base_layer_context`，但仍为 `needs_review`，`animationSolve.productionStatus=diagnostic_limb_ik_goal_driver`，blocked reasons 为 `requires_clear_visual_validation` 与 `limb_ik_goal_driver_diagnostic_unverified`。这说明 base layer context 缺口已推进，但不是动画完成；还要继续恢复/验证 controller 参数、BlendTree、layer 权重、IK goal 空间和跨样本视觉结果。

Endminf 自动 fallback 验证命令：

```powershell
dotnet AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.dll `
  --generate_unity_bake_accelerated_request_from_library "D:\Assets\fangzhou\Endfield_EndminfActor_IdleAdditive_ModelAnimationAsset_Current" `
  --preview_model "P_actor_endminf_01" `
  --preview_animation "A_actor_endminf_idle_loop_additive" `
  --preview_output "D:\Assets\fangzhou\Endfield_EndminfActor_IdleAdditive_AcceleratedAutoFallback_Run_Current" `
  --unity_project "D:\misutime\AnimeStudioUnityBakeWorker" `
  --unity_editor "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe" `
  --unity_avatar_asset "Assets/AnimeStudioBake/ImportedAvatar/SK_actor_endminf_01Avatar.asset" `
  --unity_bake_fps 30 `
  --run_unity_bake
```

该命令现在会写 `unity_bake_request.json` 而不是 `unity_bake_accelerated_request.json`，并在 fallback report 中记录 `fallbackReason=missing_decoded_float_curves`。本次验证 Unity result 为 `status=ok`、`isHumanMotion=true`、`animationClipSource=unityAssetPaths.animationClip`、`changedTrackCount=194`；apply report 为 `status=needs_review`、`path=UnityOraclePlayableGraph`、`writesGltfTrs=true`、`writtenTracks=194`、`frameVaryingTracks=125`、`coreBodyFrameVaryingTracks=4`，glTF validator 无 error。Blender 截图目录为 `Frames_ModelRest_Current` 和 `Frames_Animated_Current`；视觉上没有拉丝/飞骨，但动作语义仍像 additive/controller 上层片段单独套在 rest 上，所以仍只能算诊断样本。

注意旧 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_ExportFullCurves_Current` 索引里 Animation asset `output` 指向不存在的 `animations/3c/A_actor_pelica_idle_loop.anim`，真实文件在 `Animations/assets/beyond/...` 下。这个历史路径问题会让当前 SQLite 选择器过滤掉候选；不要为了复测自动 fallback 在生产代码中按文件名全库搜索。Pelica limb/TDOF 自动 fallback 应使用路径正确的新导出库，或先修复旧库 asset_catalog/library_index 的输出路径后再验收。

Pelica 已用路径正确的新 reexport 库复测自动 fallback。先重新导出小样本：

```powershell
dotnet AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.dll `
  "C:\Program Files\Hypergryph Launcher\games\Arknights Endfield\Endfield_Data\StreamingAssets\VFS" `
  "D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_ReexportForFallback_Current" `
  --game ArknightsEndfield `
  --mode Library `
  --group_assets ByLibrary `
  --source_index "D:\Assets\fangzhou\Endfield_FullGame_SourceIndex_PersistentStreamingFix_Current\unity_source_index.db" `
  --path_ids 5105657407662044875 3251858251387051210 `
  --export_full_decoded_animation_curves
```

再生成自动 fallback：

```powershell
dotnet AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.dll `
  --generate_unity_bake_accelerated_request_from_library "D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_ReexportForFallback_Current" `
  --preview_model "P_actor_pelica_01" `
  --preview_animation "A_actor_pelica_idle_loop" `
  --preview_output "D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_AcceleratedAutoFallback_Reexport_Current" `
  --unity_project "D:\misutime\AnimeStudioUnityBakeWorker" `
  --unity_editor "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe" `
  --unity_avatar_asset "Assets/AnimeStudioBake/ImportedAvatar/SK_actor_pelica_01Avatar.asset" `
  --unity_bake_fps 30 `
  --run_unity_bake
```

本次验证 `unity_bake_accelerated_fallback_report.json` 为 `mode=UnityOraclePlayableGraphFallback`、`fallbackReason=unsupported_limb_goal_or_tdof_curves`、`productionReady=false`；Unity result 为 `status=ok`、`isHumanMotion=true`、`changedTrackCount=105`；旧 apply report 曾写 `status=needs_review`、`path=UnityDerivedPlayableGraph`、`writesReusableGltfTrsCandidate=true`，但人工复查截图后判定这个报告语义过于乐观。输出 glTF validator 无 error、`frameVaryingTracks=78`、`coreBodyFrameVaryingTracks=17` 只能说明 TRS 写回跑通；`animatorControllerSamplingMode=generated_single_state_controller` 表示它不是原始 RuntimeAnimatorController 上下文。截图目录为 `Frames_ModelRest_Current` 和 `Frames_Animated_Current`，可见 `idle_loop` 双手异常抬高、手腕扭曲，动作语义不符合 idle，因此 Pelica 只能作为失败回归样本，不能计入生产完成。

同一 Pelica 样本在 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_UnityOracleIkGoalDiag_Current` 做 IK goal 诊断复测后，Unity result 为 `animatorControllerSamplingMode=provided_runtime_controller`、`animatorControllerSamplingState=Main_Idle_IdleLoop`、`effectiveEditorCurveIkGoalDriver=true`、`editorCurveIkGoalDriverDiagnostic.appliedGoalCount=756`。Blender 中帧和左右手近景显示双手已回到身体两侧，手腕不再像旧截图那样扭曲；但 apply report 仍为 `status=needs_review`、`productionStatus=diagnostic_limb_ik_goal_driver`、`writesReusableGltfTrsCandidate=false`。这说明 IK goal driver 是必要诊断分支，但不是生产通过证据；后续仍要恢复 controller 参数、BlendTree、additive reference pose、layer 权重和跨样本视觉验收。

2026-06-24 继续把上述路径接回 `--generate_unity_bake_accelerated_request_from_library` 自动入口，验证输出为 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_AcceleratedAutoFallback_ControllerIk_Current`。fallback report 显示 `unityAnimationClip=Assets/AnimeStudioBake/ImportedAnimationClip/A_actor_pelica_idle_loop.anim`、`unityAnimatorController=Assets/AnimeStudioBake/ImportedAnimatorController/AC_npc_human_girl_pelica_optNew.controller`、`fallbackDiagnostics.probeUnsupportedHumanoidCurves=true`、`enableEditorCurveIkGoalDriver=true`；Unity result 为 `provided_runtime_controller`、`Main_Idle_IdleLoop`、IK goal `189/756`；apply report 仍为 `needs_review / diagnostic_limb_ik_goal_driver / writesReusableGltfTrsCandidate=false`。清晰截图再次显示双手落回身体两侧，说明自动入口已经不再退化到旧的 generated single-state 失败样本，但它仍只是诊断求解结果。

同日补充修正自动入口：`--generate_unity_bake_accelerated_request_from_library + --unity_file_inspect` 现在会像旧 batch bake 一样先执行 AnimatorController context refresh 和 ImportedAnimationClip recovery；小样本库如果没有内置 `unity_source_index.db`，刷新器会从 `source_index_usage.json`、`animator_controller_context_refresh.json` 或 `sqlite_index_summary.json` 找到全量源索引。验证输出 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_AcceleratedAutoContextPrep_Current`：context refresh 命中全量源索引并更新 `1` 条候选，fallback 使用 `ImportedAnimationClip/A_actor_pelica_idle_loop.anim` 和 `ImportedAnimatorController/AC_npc_human_girl_pelica_optNew.controller`；Unity result 为 `provided_runtime_controller / Main_Idle_IdleLoop / IK applied=756`，glTF validator 无 error。apply report 仍为 `needs_review`、`productionStatus=needs_animator_controller_layer_context`、`writesReusableGltfTrsCandidate=false`，因为仍有 `LoopClothAddLayer` 被跳过；`VisualFrames\upper_mid_frame_24.png`、`left_hand_mid_frame_24.png`、`right_hand_mid_frame_24.png` 显示双手已落在身体两侧，没有旧截图的手腕麻花，但仍不能作为生产 idle 通过。

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

默认会在同目录写 `<dbName>.animation_relation_health.json`。如果报告里 `animationRelationHealth.staleOverridePairIndex=true`，说明源索引仍缺当前工具写入的 `AnimatorOverrideController.overrideSet` / `clipPair` 精确标记。旧索引即使有分离的 `originalClip` / `overrideClip`，也不能可靠表达 `original -> override` 或“空替换表继承 base controller”的确定性关系；当前生产候选会跳过这类 OverrideController，不再做粗略恢复。如果 `missingTargetCounts` 里任何 clip 关系大于 0，说明当前数据库记录到了确定性的 clip PPtr，但目标 `AnimationClip` 不在索引中；这时应先补 CAB/VFS 依赖闭包或重建源索引，不能把这批关系直接推进到动画 smoke。

如果旧 `unity_source_index.db` 是在创建 SQL 查询索引前中断的，可以显式补查询索引：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --ensure_source_index_query_indexes "D:\Assets\fangzhou\Endfield_FullGame_SourceIndex_PersistentStreamingFix_Current\unity_source_index.db"
```

这个命令只维护 SQLite index，不重建 Unity 关系内容。注意：Endfield 全量源索引的 `source_relations` 可能超过一亿行，补 `idx_source_relations_to(to_path_id, relation, to_file)` 会非常耗时并显著增加数据库体积；生产环境建议先在源索引副本上运行，确认磁盘空间和耗时可接受后再替换正式索引。缺这个反查索引时，精确模型的 direct Animator 诊断仍可执行，但 shared Avatar 桥接和 MonoBehaviour PPtr 反查会被工具主动跳过并写入报告。

生产重建前建议开启严格门槛；只要源索引缺这些当前工具需要的精确关系，命令会直接失败，避免继续生成一份看似正常但少掉 OverrideController 显式动画的 `library_index.db`：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --verify_source_index "D:\Assets\AS-Assets\YuanShen-Assets\unity_source_index.db" `
  --require_fresh_source_animation_relations
```

修改 `AnimatorOverrideController` 源索引或 Library 候选展开逻辑后，先跑最小回归脚本。它会构造四个合成库：完整 `clipPair` 场景验证 base controller 含 `OriginalClip + KeepClip`、OverrideController 将 `OriginalClip -> OverrideClip` 时，最终候选只有 `OverrideClip + KeepClip` 且健康状态为 `ok`；空 `overrideSet(count=0)` 场景验证空替换表会确定性继承 base controller 的 `OriginalClip + KeepClip` 且健康状态为 `ok`；半旧 separated 场景验证只有分离的 `originalClip/overrideClip` 时不会产出候选，健康状态为 `warning`，必须重建源索引拿到 `clipPair`；完全旧 base-only 场景验证只有 `baseController`、没有 `clipPair/original/override` 细节时，Library 不会把 base controller 的动画粗扩散成确定性候选。

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

注意：Browser 顶部“重建动画索引”按钮会优先复用 `sqlite_index_summary.json` 记录的 `sourceIndex`；没有可用记录时才读取素材库根目录的 `unity_source_index.db`。旁路 fresh 源索引第一次接入仍应使用上面的 CLI 命令显式传 `--source_index`，这样 summary 会记录实际源索引，后续 Browser 重建才会继续使用它，避免重新消费根目录旧索引。

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

`--assembly_modules` 也可以显式传入一个 `.gltf` / `.glb` 文件路径，例如 Naraka `--export_avatar_mesh_data_gltf` 生成的 face 诊断 glTF。显式路径允许追加“有 mesh、无 skin”的诊断模块，用来检查坐标、材质和可见装配效果；报告会标记 `DiagnosticUnskinned=true`，不能把它当作正式 prefab 绑定、skin 绑定或动画验收证据。默认按角色自动选择的模块仍必须通过同名 joint 映射。

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

### 导出独立动画 glTF

如果要把确定性候选里的动画单独作为可复用资产保存，可以从 `library_index.db` 导出 skinless glTF 动画：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --export_animation_gltf_from_library "F:\Unity-AS-Assets\VRising-Assets" `
  --preview_model "^HYB_NPCCultist_Standard$" `
  --preview_animation "^NPCCultist_Run$" `
  --preview_output "D:\Assets\fangzhou\VRising_DirectAnimationGltfSmoke\Standalone_NPCCultist_Run"
```

这个命令只读取 `library_index.db` 里的 `relation_source=explicit` 候选，不做模型 × 动画猜测。输出 glTF 会保留选中模型的 node/rest TRS 层级，移除 mesh/material/texture/skin，只写 `animation_asset.json decoded` 里的直接 Transform TRS channel。这样生成的文件类似“带骨架目标节点的独立动画资产”，可用于浏览、归档和后续合成。

默认独立 glTF 导出只接受已经能直接写出的 Generic/Transform TRS。`decoded.status` 不是 `ok`、没有 TRS 曲线、或只有 BlendShape weights 的动画会被拒绝；拒绝不是失败兜底，而是避免把缺少生产级 glTF TRS 的动画误标成可复用。Humanoid/Muscle 动画如果要走 AnimeStudio 内部直接求解，必须显式使用文件入口的 `--preview_force_internal_humanoid_solve`，输出会标记为实验/待视觉验收。

调试时也可以显式指定模型 glTF 和动画 sidecar，把已选动画写成 skinless animation glTF：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --export_animation_gltf_from_files "D:\Assets\fangzhou\SomeLibrary\Models\chr_0030_zhuangfy_postmodel.gltf" `
  --preview_animation "D:\Assets\fangzhou\SomeLibrary\Animations\A_actor_zhuangfy_battle_attack_01.animation_asset.json" `
  --preview_output "D:\Assets\fangzhou\SomeLibrary\AnimationGltf\zhuangfy_attack_01"
```

这个入口只做“已选择文件 -> 独立动画 glTF”的格式转换，不创建 `model_animations.json` / SQLite 默认推荐关系。它适合验证解码和合成，但可信绑定仍必须来自 Unity 显式引用、controller 上下文或后续人工验证。

文件入口也执行模型优先门禁。模型 glTF 如果是白模、缺材质、缺贴图、缺主要 image、skin 绑定不完整，或 `model_validation` 等价检查不是 `ok`，命令会停止并写 `standalone_animation_gltf_report.json`，状态为 `blocked` / `model_not_animation_ready`。这类样本只能继续排查模型闭包、材质贴图或 skin，不能用独立动画 glTF 结果证明动画可用。

如果要把 Humanoid/Muscle 直接求解结果导出为 skinless 动画资产，可以在同一个文件入口加 `--preview_force_internal_humanoid_solve`。这个模式会从模型所在素材库的 `asset_catalog.jsonl` 找回 `avatar.internalSolver`，把 decoded muscle/root motion 直接求解成目标骨架 TRS，并输出无 mesh/skin 的独立动画 glTF：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --export_animation_gltf_from_files "D:\Assets\fangzhou\SomeLibrary\Models\chr_0030_zhuangfy_postmodel.gltf" `
  --preview_animation "D:\Assets\fangzhou\SomeLibrary\Animations\A_actor_zhuangfy_battle_attack_01.animation_asset.json" `
  --preview_output "D:\Assets\fangzhou\SomeLibrary\AnimationGltf\zhuangfy_attack_01" `
  --preview_force_internal_humanoid_solve
```

这个命令只用于直接求解器和动画资产格式验证：它不会创建默认模型-动画推荐关系，也不会把 `experimental_internal_solver` 自动升级为生产可复用动画。验收仍必须把 standalone 动画合回模型，检查 glTF animation extras、合成结果和 rest/mid/end 清晰截图。skinless glTF 不写 mesh/morph target，所以 BlendShape weights 仍需要带目标模型的预览/合成路径单独验收。

如果候选来自 `unity_source_index.db` 的显式 `Animator -> AnimatorController -> AnimationClip` 链，但模型缺完整 `HumanDescription.humanBones` 或 `humanBoneIndex`，SQLite 会把生产状态继续阻断为 `NeedsDirectTrsAnimation` / `missing_human_bone_index`。只要 `experimentalInternalHumanoidSolverReady=true`，CLI/Browser 可以把它放入内部求解器实验预览队列；但报告、索引和截图都必须保留 `experimental` / `notProductionReady` 语义，直到跨样本视觉验收和公式校准完成。

内部 Humanoid/Muscle 求解器会把每个 Humanoid 骨骼的 3 个 muscle DoF 按 Unity AvatarLimit 的 X/Y/Z 轴直通还原，再用 `preQ * swing(Y/Z) * axisX(X) * inverse(postQ)` 写回骨骼本地旋转；不能因为肉眼觉得某个属性像 twist，就把 `Down-Up/Stretch` 和 `Twist` 全局调换。求解器也会读取 `model.avatar.internalSolver.twist` / `HumanDescription` 的 `armTwist`、`foreArmTwist`、`upperLegTwist`、`legTwist`，按 Unity Avatar 的父/子骨 twist share 分配长骨 twist。Endfield 当前常见配置是 `arm=1, foreArm=0, upperLeg=1, leg=0`，也就是上臂/上腿 twist 写到远端，前臂/小腿 twist 留在近端。这个修正只解决确定性的轴映射和 twist 分配，不等于 Forearm/LowerLeg stretch 成对 delta 公式已经完全复现；只要报告里还有 `experimental_solved_known_limb_formula_risk` 或 `notProductionReady=true`，仍不能当作生产验收通过。

2026-06-22 对 Endfield `chr_0005_chen_postmodel + A_actor_girl_run_loop` 做过一次负向烟测：把 UpperArm/Forearm/UpperLeg/LowerLeg 这类长骨 twist 的 limit 统一改为来源骨骼 X/twist 轴，虽然符合部分单 muscle oracle 斜率线索，但合成 glTF 后上半身摆臂明显退化，更接近横臂/T pose。因此 `sourceAxis=X/twist` 只能作为 oracle 诊断线索，不能直接迁入 FastPreview 生产或实验默认公式；下一步仍应继续追 source/target 轴、zero/base 组合、Forearm Stretch 联动和乘法空间。

手指 Humanoid/Muscle 曲线会单独记录在 `unityHumanoid.diagnostics.finger`。如果 AvatarConstant 的 `m_HandBoneIndex` 可用，后续应优先使用它建立指节目标；Endfield 当前样本里该数组多为 `-1`，因此求解器会从 Unity 确定的 LeftHand/RightHand 目标节点出发，在 glTF 手部子层级中寻找 `Bip001_L_Finger*` / `Bip001_R_Finger*` 三段指骨，并把 `LeftHand.Index.1 Stretched`、`LeftHand.Thumb.Spread` 等 muscle 曲线写成指骨 rotation channel。这个路径会标记为 `experimental_solved_structural_fallback` 和 `genericFallbackNoAvatarFingerLimit`：它能把确定性 finger muscle 数据落成 glTF TRS，但每指节真实 limit、轴向和正负方向仍需 Unity oracle 或清晰局部截图继续校准，不能因为有 finger channel 就升级为生产完成。

`unityHumanoid.diagnostics.zeroPoseSummary` 会比较当前内部公式在 `muscle=0` 时生成的本地旋转与 glTF rest pose 的夹角。`rawZeroVsGltfRestDegrees` 是原始公式偏差；导出时会再用目标 glTF 节点 rest rotation 做一次确定性的本地空间 rest-anchor 修正，让当前模型的零肌肉姿态锚回自己的 rest pose，修正后的残差写在 `zeroVsGltfRestDegrees`。当前 FastPreview 实验公式使用右乘锚点 `rotation * inverse(rawZero) * targetRest`，版本串里标为 `RightRestAnchorCorrection`。2026-06-22 对 Endfield 10 组小样本重跑后，standalone 导出、模型合成和 Blender 截图都成功，未见明显拉爆或飞骨；但所有样本仍保留 `experimental_solved_known_limb_formula_risk`，因为这个校正只解决 zero/base anchor 偏差，不等于 Forearm/LowerLeg stretch 成对 delta 公式已经完全复现。只要报告里仍有该状态，或缺少 Unity oracle / 跨样本视觉证据，结果仍不能升级为生产完成。

生成视觉证据时，优先使用 Blender 多视图截帧，而不是只看远景。`tools/render_gltf_frames.py` 支持 `--views full,upper,left_hand,right_hand`，会同一次导入输出全身、上半身和左右手近景的 rest/mid/end 帧：

```powershell
& "C:\Program Files\Blender Foundation\Blender 5.1\blender.exe" --background `
  --python tools\render_gltf_frames.py -- `
  --input "D:\Assets\fangzhou\SomeLibrary\Merged\model__clip.merged.gltf" `
  --output "D:\Assets\fangzhou\SomeLibrary\MergedFrames" `
  --size 1200 `
  --views full,upper,left_hand,right_hand
```

手部近景会优先查找 Blender Armature pose bone 的 `*_L_Hand` / `*_R_Hand`，找不到时退回普通对象名。若某个游戏骨骼命名不同，近景可能退回全身视图；这种情况要先修验证脚本的定位规则，不能拿远景替代手部验收。

注意：`directGltfAnimationStatus=direct_trs` 只说明 sidecar 已经有可写入 glTF 的 TRS channel。Additive clip、上层叠加 clip、root/附件/头发/表情片段，或需要同一 `AnimatorController` state/base layer/blend tree 组合的 clip，单独合成后可能只表现为轻微姿态变化或接近 T pose。不能把 channel 数、`matchedTracks` 或 `direct_trs` 当成视觉验收通过；必须看 rest/mid/end 截图，并在需要上下文时标记 `needsAnimatorControllerContext` 或待验证。

### Unity Humanoid glTF 求解与原始 Avatar asset

Unity bake 现在按“求解手段”处理，不再一刀切禁止。硬目标仍然不变：最终交付必须是可脱离 Unity 使用的 glTF TRS / weights 动画数据。Unity native Humanoid 可以作为 `UnityOracle` 对照，也可以通过显式 `UnityBakeAccelerated` worker 把 muscle/root pose 解成目标骨架 TRS；结果必须写清楚 Unity 版本、Avatar、clip、采样率、求解路径、耗时和视觉验收状态。

仍然需要限制的是原神旧流程那类重工程 bake：临时新建游戏专用 Unity 工程、安装插件/helper、导入 Avatar/AnimationClip/Prefab，再通过 PlayableGraph/AnimationClipPlayable/Transform 逐帧读回。它保留为显式诊断或迁移路径，不能作为默认 Library 热路径，也不能不加标注地写入推荐索引。普通导出、重建 SQLite 和统一浏览器索引不会自动恢复旧 bake 资产；遇到只有 `AvatarConstant/internalSolver` 或旧 `requiresUnityBake` 的候选，应标记为 `needsDirectTrsAnimation` / `notProductionReady`，直到它能输出并验证 glTF TRS。

`UnityBakeAccelerated` worker 建议先显式同步 helper，再验证通用 Unity 6 工程、helper marker 和 Unity 侧 smoke test：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --sync_unity_bake_accelerated_worker `
  --unity_project "D:\misutime\AnimeStudioUnityBakeWorker" `
  --unity_editor "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe"
```

如果只想检查，不覆盖 worker 工程里的 helper 文件，把 `--sync_unity_bake_accelerated_worker` 换成 `--check_unity_bake_accelerated_worker`。

独立 request 可以单次运行，也可以投递给常驻 worker 队列。当前入口只读写显式 request/result，不读 Library 索引、不新增模型-动画关系：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --run_unity_bake_accelerated "D:\Assets\fangzhou\request.json" `
  --unity_project "D:\misutime\AnimeStudioUnityBakeWorker" `
  --unity_editor "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe" `
  --unity_bake_accelerated_worker_queue "D:\Assets\fangzhou\UnityBakeAcceleratedQueue"
```

`UnityBakeAccelerated` request 的 Avatar 来源有两种：优先使用 `avatarAsset` 指向 Unity 工程内真实 `Avatar.asset`；没有 asset 时，可以在 request 的 `avatar` 字段提供完整 `humanBones` 和 `skeletonBones`，worker 会临时 `BuildHumanAvatar` 后调用 `HumanPoseHandler`。如果只有 AvatarConstant/internalSolver/defaultPose 但没有 human bone 映射，worker 必须失败，不能按骨骼名猜一个 Humanoid Avatar。

从已导出的 Library 显式候选生成 accelerated request 时，使用下面入口。它只读取 `library_index.db` / `model_animation_candidates` 中的 `relation_source=explicit` 候选、模型 glTF、动画 sidecar decoded muscle/root 曲线和已恢复的 Unity Avatar asset；不会用名称、目录或骨骼数量新增模型-动画关系：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --generate_unity_bake_accelerated_request_from_library "D:\Assets\fangzhou\Endfield_EndminfNpc_ModelPlus2MuscleClips" `
  --preview_model "P_npc_npc_chr_0003_endminf" `
  --preview_animation "A_actor_girl_run_loop" `
  --preview_output "D:\Assets\fangzhou\Endfield_AcceleratedBakeSmoke\Run" `
  --unity_project "D:\misutime\AnimeStudioUnityBakeWorker" `
  --unity_editor "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe" `
  --unity_bake_fps 60
```

worker 求解完成后，把 result 写回普通 glTF TRS 动画：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --apply_unity_bake_accelerated_result "D:\Assets\fangzhou\Endfield_AcceleratedBakeSmoke\Run\unity_bake_accelerated_result.json" `
  --baked_gltf_output "D:\Assets\fangzhou\Endfield_AcceleratedBakeSmoke\Run\AcceleratedPreview\P_npc_npc_chr_0003_endminf__A_actor_girl_run_loop.gltf"
```

`--apply_unity_bake_accelerated_result` 每次都会从 request 记录的干净模型 glTF 重新复制并写入动画，避免重复执行时把同一个 clip 追加多遍；输出路径不能等于源模型路径。写回前会先执行模型优先门禁：如果源模型 glTF 白模、无材质/贴图、主要 image 缺失、skin 绑定不完整或静态结构不是 `ok`，命令只写 `unity_bake_accelerated_apply_report.json`，状态为 `blocked` / `model_not_animation_ready`，不生成动画 glTF。模型通过后，报告会写到输出目录的 `unity_bake_accelerated_apply_report.json`，至少应检查 `sampleCount`、`writtenTracks`、`coreChangedTracks`、`skippedPathCount` 和 glTF validator 结果。该报告只证明 TRS channel 已写出，不替代 rest/mid/end 清晰截图。

如果 apply 报告返回 `needs_review_unsupported_humanoid_curves`，表示 worker 已经写出诊断 glTF TRS，但源 clip 仍包含当前 `HumanPoseHandler.SetHumanPose` / `SetInternalHumanPose` 路径没有消费的 Unity Humanoid 曲线，例如 limb IK goal 或 TDOF。此状态不能升级为生产可用动画；需要继续实现完整 `AnimationClip` / `PlayableGraph` 采样，或在直接 solver 中显式处理这些曲线，并重新通过截图和报告验收。

下面旧命令仅供诊断或旧库迁移：

### Arknights Endfield VFS 诊断过滤

Endfield 正式 PC 包的 `StreamingAssets/VFS/*.blc` 会被识别为新版 VFS block list，并在 `--game ArknightsEndfield` 下展开其中的 UnityFS bundle。默认全量 Library / 源索引仍应读取完整 `.blc`，保证 CAB / PPtr 依赖图不被裁掉。

构建 `unity_source_index.db` 时，Endfield `.blc` 会自动按内部 UnityFS 文件分批加载，不会把一个超大的 `Bundle` 组一次性全部展开。自动分批覆盖完整 `.blc`，不是 partial 诊断；索引 metadata 会写入 `endfieldVfsAutoInnerBatch=true`。完整安装目录里 `Persistent/VFS` 和 `StreamingAssets/VFS` 常有同 bucket `.blc`：默认以本机 chunk 闭包完整的块为主，同等条件下优先 `Persistent` patch 层；`StreamingAssets` / 另一层只补主块没有的 inner UnityFS 文件，或同名但 byte length 不同的 inner 文件。同名同长度 inner 文件默认只统计，不进入默认索引批次；如果怀疑同名同长度也有不同 CAB 闭包，再显式加 `--endfield_vfs_keep_same_length_supplemental` 做慢速诊断。metadata 会记录 `endfieldVfsSameNamedDifferentLengthInnerFileCount`、`endfieldVfsSameNamedSameLengthInnerFileCount` 和 `endfieldVfsSupplementalInnerFileCount`，用于判断本次索引保留了多少补充内层文件。只有显式使用下面的诊断过滤时，才会写入 `endfieldVfsPartialDiagnostic=true`。

内部文件过滤必须走精确 HashSet 或按文件名缩小后的后缀匹配。不要在每个 VFS 条目上遍历整个 selected 列表做 `EndsWith`，否则像 `7064D8E2.blc` 这种几十万内部条目的包会把每批加载放大到数秒甚至十几秒。2026-06-23 的 smoke 显示，修正后 `7064D8E2.blc` 自动 172 个 inner batch 完整源索引约 68.7 秒完成，`source_index_load_batch` 平均约 57ms；旧过滤器 10 分钟只跑到第 49 批。

完整 PC 安装目录建议直接把 `Endfield_Data` 作为源目录构建索引，让工具同时看到 `Persistent/VFS`、`StreamingAssets/VFS`、`Resources` 和内置 assets；非 Unity sidecar 和非 Bundle `.blc` 会被跳过或只保留空 source 记录，真正进入索引的是能展开 UnityFS 的 bundle block。若历史命令仍传 `Endfield_Data\StreamingAssets\VFS` 或 `Endfield_Data\Persistent\VFS`，当前工具会自动把扫描根提升到 `Endfield_Data` 并合并两层 VFS，避免只扫基础包或只扫 patch 层导致模型、材质、贴图、Avatar、AnimationClip 或配置关系漏索引：

Endfield 全量源索引可能非常大。若 DB 或 WAL 超过安全阈值，工具会在最后使用 `PRAGMA wal_checkpoint(PASSIVE)`，避免 `TRUNCATE` checkpoint 在数百 GB 数据库上卡住数小时。这种输出仍可读，但维护、复制或归档时必须把 `unity_source_index.db`、`unity_source_index.db-wal` 和 `unity_source_index.db-shm` 放在一起；后续可在空闲时单独做 SQLite 维护 checkpoint。

```powershell
dotnet run --project AnimeStudio.CLI\AnimeStudio.CLI.csproj --framework net9.0-windows -- `
  "C:\Program Files\Hypergryph Launcher\games\Arknights Endfield\Endfield_Data" `
  "D:\Assets\fangzhou\Endfield_FullGame_VFS_SourceIndex" `
  --game ArknightsEndfield `
  --build_source_sqlite_index
```

旧验证样本中，完整 `VFS` 源索引会扫描 792 个文件，跳过 767 个 sidecar/non-Unity 文件，25 个 `.blc` 中实际由 `0CE8FA57` 和 `7064D8E2` 贡献 Unity serialized files。旧索引计数约为 `serialized_files=258423`、`source_objects=5319614`、`source_relations=8831375`、`source_animation_bindings=117849`，耗时约 33 分钟，峰值 private memory 约 49 GB。注意：这批旧索引可能缺 `sourceRelationFeatures` 和 AssetBundle container/preload 关系；生产排查模型来源、缺件、静态 Mesh 主资源或 Dialog/Timeline 降级前，应使用当前工具重建并用 `--verify_source_index` 检查 `sourceRelationHealth`。

基于完整源索引做定向 Library smoke 时，可以仍然把完整 `VFS` 目录作为输入，CLI 会从 `--source_index` 和 `--names` / `--path_ids` 反推目标 CAB 闭包，并只暴露命中的内部 UnityFS 文件：

```powershell
dotnet run --project AnimeStudio.CLI\AnimeStudio.CLI.csproj --framework net9.0-windows -- `
  "C:\Program Files\Hypergryph Launcher\games\Arknights Endfield\Endfield_Data\StreamingAssets\VFS" `
  "D:\Assets\fangzhou\Endfield_FullGameInput_ModelAnimSmoke" `
  --game ArknightsEndfield `
  --mode Library `
  --source_index "D:\Assets\fangzhou\Endfield_FullGame_VFS_SourceIndex\unity_source_index.db" `
  --names "eny_0079_nefarp2_postmodel|A_monster_nefarp2_hit_additive_shaking_01" `
  --export_full_decoded_animation_curves
```

这个 smoke 的验收重点不是“动画已经和模型建立推荐关系”，而是先证明模型本体完整：`model_validation.json` 应显示 `modelBodyOk=1`、UV0 / skin / texture / bbox 无缺失；动画 sidecar 应显示 `directGltfAnimationStatus=direct_trs`、`deprecatedUnityBakeOnly=false`，不能出现生产 `requiresUnityBake=true`。

Endfield 动画解码目前支持 `AnimationClip.m_AclCompressedBuffer.TransformBufferData` 中的 ACL v10 `qvvf` transform tracks，也支持 `FloatBufferData` 中的 ACL scalar tracks。TransformBufferData 会按 Unity `GenericBinding` 还原 Transform translation/rotation/scale，写出 glTF 前会做四元数连续性修正，避免相邻帧符号翻转造成插值跳变。FloatBufferData 会按 Animator binding 还原 Humanoid/Muscle、RootT/RootQ、TDOF 和其他 Animator scalar 曲线；其中 `AnimatorMuscle` 曲线是 Endfield 动作 clip 主体骨骼求解的重要输入。`m_ValueArrayDelta` 会作为诊断和 binding 标量布局证据保留，但不能当成逐帧动画曲线使用。

注意：部分 Endfield 动作的 TransformBufferData 只覆盖头发、衣摆、socket、twist/helper 或附件等辅助节点，主体人形动作在 FloatBufferData 的 Humanoid/Muscle 曲线中。遇到这种 clip 时，不能仅凭 `direct_trs` 或 Transform channel 数宣布身体动画可用；必须通过 `--preview_force_internal_humanoid_solve` 或后续生产求解路径把 muscle 曲线转成目标骨架 TRS，并用清晰截图验收。

2026-06-22 对 `UnityBakeAccelerated` 做过一个诊断闭环：`P_npc_npc_chr_0003_endminf` + `A_actor_girl_run_loop` 来自 `D:\Assets\fangzhou\Endfield_EndminfNpc_ModelPlus2MuscleClips` 的显式 `model_animation_candidates` 关系，Avatar asset 为 `Assets/AnimeStudioBake/ImportedAvatar/SK_actor_endminf_01Avatar.asset`。request 包含 `465` 个 joint path、`42` 个 60fps sample、每帧 `95` 个 muscle；Unity 6 worker 输出 `status=ok`、`avatarValid=true`、`avatarHuman=true`、`restValueCount=3255`，求解约 `9.59ms`。写回 glTF 后 `unity_bake_accelerated_apply_report.json` 显示 `writtenTracks=79`、`coreChangedTracks=76`、`skippedPathCount=0`；`gltf-transform inspect` 显示单个 `A_actor_girl_run_loop` 动画、`80` channels、`3360` keyframes，`validate` 无 error。这个样本现在只作为求解器/关系链诊断，不能作为生产 smoke 通过证据：源模型材质链不完整，导出结果接近白模；用户复看动画时还发现 run 动作存在双脚同向、手臂外翻和扭曲变形。按模型优先规则，模型未通过第一阶段“模型可用”门槛时，不能进入第二阶段动画验收；后续应优先选择 `P_actor_endminf_01`、`P_actor_endminm_01` 这类真实 game prefab 且材质/贴图/skin 完整的模型，再推进动画关系和 Humanoid/Muscle 求解。

2026-06-24 对 `P_actor_pelica_01` + `A_actor_pelica_idle_loop` 做过共享 Avatar 桥接 smoke：模型第一阶段通过，`model_validation` 为 `ok`，glTF validator 无 error，材质 18、图片 43、skin 51；关系来自 `data_facemorph_avatar_pelica -> P_actor_pelica_01 + SK_actor_pelica_01Avatar`，再通过同 Avatar 找到带 controller 的 `P_npc_npc_chr_0004_pelica -> AC_npc_human_girl_pelica_optNew -> A_actor_pelica_idle_loop`。Avatar recovery 可从 `relationEvidence.avatar*` 自动恢复 `Assets/AnimeStudioBake/ImportedAvatar/SK_actor_pelica_01Avatar.asset`，Unity probe 显示 `isValid=true`、`isHuman=true`。`UnityBakeAccelerated` request/result 能跑通并写出普通 glTF TRS，`gltf-transform validate` 无 error；但 apply 报告为 `needs_review_unsupported_humanoid_curves`，只写出 `21` 条 track / `17` 条 core frame-varying track，源 clip 还有 `34` 条未消费 Humanoid 曲线，其中 limb goal `28` 条、TDOF `6` 条。Blender rest/mid/end 截图显示模型材质完整但手臂/手部姿态异常，因此这个样本只证明“模型过门 + 确定性关系 + Avatar 恢复 + 高吞吐求解入口”链路成立，不能作为动画生产验收通过证据。

同日继续用完整 Unity `AnimationClip` / `PlayableGraph` 诊断链路验证 Pelica：先用 `--recover_imported_animation_clips` 从显式候选恢复 `Assets/AnimeStudioBake/ImportedAnimationClip/A_actor_pelica_idle_loop.anim`，再用 `--generate_unity_bake_request_from_library ... --unity_avatar_asset Assets/AnimeStudioBake/ImportedAvatar/SK_actor_pelica_01Avatar.asset --unity_animation_clip Assets/AnimeStudioBake/ImportedAnimationClip/A_actor_pelica_idle_loop.anim --run_unity_bake` 采样。Unity result 显示 `status=ok`、`isHumanMotion=true`、`animationClipSource=unityAssetPaths.animationClip`、`importedAvatarAssetValid=true`、`changedTrackCount=105`；apply 后 glTF validator 无 error，报告 `frameVaryingTracks=78`、`coreBodyFrameVaryingTracks=17`、`avatarTrust.source=imported_unity_avatar_asset`。但当前可复现输出 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_AcceleratedAutoFallback_Reexport_Current` 使用的是 `generated_single_state_controller`，不是原始 `AC_npc_human_girl_pelica_optNew` RuntimeAnimatorController；人工复查发现 `idle_loop` 双手抬高、手腕扭曲，视觉失败。修复方向是恢复并采样原始 AnimatorController 的 state/layer/blend/override 上下文，或让直接 solver 等价处理这些上下文；单 clip 诊断输出应标为 `needs_original_animator_controller_context`，不能作为 UnityDerived 可复用候选。

同日后续复测 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_SkippedLayerDiag_Current` 时，Unity result / apply report 增加了 `animatorControllerSkippedUntrustedLayerDiagnostics` / `unityBakeAnimatorControllerSkippedUntrustedLayerDiagnostics`。这个字段用于解释被跳过的 recovered controller layer：例如 `LoopClothAddLayer` 有 `hasLayerMetadata=true`、`hasSkeletonMask=true`、`skeletonMaskEntryCount=152`，并且 Simple1D `IdleTailIndex=0` 会选 `A_actor_pelica_idle_loop_additive_cloth`，但 `hasRequestLayerContext=false`，所以仍不能直接进入生产采样。它是下一步恢复 controller layer/runtime 语义的诊断证据，不是通过标志；只要 `productionStatus=needs_animator_controller_layer_context` 或 blocked reasons 仍含 `animator_controller_untrusted_layers_skipped`，该输出就不能计入生产 smoke。

2026-06-22 定向验证过一个完全由源索引确定的 UI 闭环：`chr_0005_chen_uimodel` 的 `Animator` 显式引用 `chr_0005_chen_controller`，controller 再显式引用 `A_actor_chen_ui_overview_loop`。用 `--path_ids 1155343899411143876 3980923002881381300` 导出后，`library_index.db` 生成 1 条 `relation_source=explicit` / `confidence=explicit_unity_source_index` 候选；重建索引后候选字段为 `requiresInternalHumanoidSolve=true`、`experimentalInternalHumanoidSolverReady=true`、`internalHumanoidSolverProductionReady=false`、`nextAction=generate_preview_gltf`。随后用 `--export_animation_gltf_from_library ... --preview_force_internal_humanoid_solve` 导出 skinless 动画 glTF，得到 688 个 glTF animation channel；再合回模型并用 Blender 输出 `full/upper/left_hand/right_hand` 的 rest/mid/end 截图，未见明显飞骨或 T pose。这个样本证明“确定性关系 -> 独立动画 glTF -> 合并可播放 glTF”的实验链路可跑通，但仍不能替代 Forearm/LowerLeg 等 Humanoid/Muscle 公式的生产验收。

已验证的最小人形样本命令：

```powershell
dotnet AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.dll `
  "C:\Program Files\Hypergryph Launcher\games\Arknights Endfield\Endfield_Data\StreamingAssets\VFS" `
  "D:\Assets\fangzhou\Endfield_ZhuangfyUimodel_Idle01_DirectAclTrs_QContinuity" `
  --game ArknightsEndfield `
  --mode Library `
  --group_assets ByLibrary `
  --profile_3d All `
  --model_format Gltf `
  --texture_mode Png `
  --animation_package Both `
  --fbx_animation All `
  --export_full_decoded_animation_curves `
  --source_index "D:\Assets\fangzhou\Endfield_FullGame_VFS_SourceIndex_ControllerFixed2\unity_source_index.db" `
  --path_ids -8064556845540476995 3009060560603074838 `
  --skip_sqlite_file_index `
  --skip_sqlite_json_documents
```

该样本 `A_actor_zhuangfy_uiteam_idle01` 应生成 `decoded.status=ok`、`directGltfAnimationStatus=direct_trs`、`translations=438`、`rotations=438`、`keyframes=132276` 的动画 sidecar。然后可用 `--export_animation_gltf_from_library` 导出 skinless animation glTF，再用 `--merge_animation_gltf` 合成单模型单动画预览。这个样本只是 smoke，不满足 10 组代表性人形验收标准。

调试超大 `Bundle` 组时，可以显式缩小内部 UnityFS 文件集合：

```powershell
dotnet run --project AnimeStudio.CLI\AnimeStudio.CLI.csproj --framework net9.0-windows -- `
  "C:\Program Files\Hypergryph Launcher\games\Arknights Endfield\Endfield_Data\StreamingAssets\VFS\7064D8E2\7064D8E2.blc" `
  "D:\Assets\fangzhou\Endfield_MainBundle_Large3_Inspect" `
  --game ArknightsEndfield `
  --inspect_unity_files `
  --endfield_vfs_files "d42192003ea4169a31c072ec|a00b46e8d2a75e3ffaf95868|2045f0c26a0ef2f41bb6e02c"
```

`--endfield_vfs_files` 是 `.blc` 内部 UnityFS 文件路径的 regex，`--endfield_vfs_file_limit` 是每个 `.blc` 最多暴露多少个内部 UnityFS 文件。两者都只允许用于烟测、定位和性能诊断；用它们构建的 `unity_source_index.db` 会写入 `endfieldVfsPartialDiagnostic=true`，不能当作生产全量依赖索引。

默认 Endfield 源索引会把 `Persistent/VFS` 视为同 bucket 的 patched 主块；`StreamingAssets/VFS` 只补主块没有的 inner UnityFS 文件，或同名但 byte length 不同的 inner 文件。同名同长度 inner 文件默认只统计到 `endfieldVfsSameNamedSameLengthInnerFileCount`，不再进入默认索引批次，避免全量源索引膨胀到无法使用。若正在追某个缺失 Material/CAB，怀疑同名同长度也可能不是重复内容，可以显式加 `--endfield_vfs_keep_same_length_supplemental` 做慢速完整闭包诊断；这种 DB 会写 `endfieldVfsKeepSameLengthSupplemental=true`，不应作为日常全量生产源索引默认路径。

追 Endfield 缺失 CAB / Material / Texture 时，优先分两步做只读诊断：

如果要连续追多轮材质/贴图闭包，先建立一次 Endfield CAB 位置索引，避免每次 `--locate_endfield_missing_source_cabs` 都递归扫描全部 VFS inner bundle：

```powershell
dotnet AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.dll `
  "C:\Program Files\Hypergryph Launcher\games\Arknights Endfield\Endfield_Data" `
  "D:\Assets\fangzhou\Endfield_CabLocationIndex_Current" `
  --game ArknightsEndfield `
  --build_endfield_cab_location_index
```

输出 `endfield_cab_location_index.json`，只记录 `CAB -> block/chunk/inner .ab` 物理位置。这个索引不解析 Unity 对象，不替代 `unity_source_index.db`；它只是给模型第一阶段缺 Mesh、缺材质、缺贴图闭包查询加速。小范围调试时也可以配合 `--endfield_vfs_files` 只建部分 CAB 位置索引，但这种局部索引只能覆盖被过滤到的 inner bundle。

如果已有 `unity_source_index.db` 的 `modelDependencyHealth` 或 `materialRelationHealth` 显示缺 `meshFilter.mesh`、`skinnedMeshRenderer.mesh`、`animator.avatar`、`renderer.material` 或 `material.texture` 目标，可以先让 CLI 自动从源索引里提取被引用最多的缺失 CAB，并递归扫描 Endfield VFS 内部 bundle，输出“缺失 CAB -> 物理 inner `.ab`”闭包报告：

```powershell
dotnet AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.dll `
  "C:\Program Files\Hypergryph Launcher\games\Arknights Endfield\Endfield_Data" `
  "D:\Assets\fangzhou\Endfield_MissingSourceCabClosure_Current" `
  --game ArknightsEndfield `
  --locate_endfield_missing_source_cabs "D:\Assets\fangzhou\Endfield_SourceIndex_EndfieldData_DefaultVfsRule_Current\unity_source_index.db" `
  --source_candidate_limit 200
```

如果已经有 `endfield_cab_location_index.json`，给同一个命令追加 `--endfield_cab_location_index`，CLI 会直接从索引里筛目标 CAB，不再重扫 VFS：

```powershell
dotnet AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.dll `
  "C:\Program Files\Hypergryph Launcher\games\Arknights Endfield\Endfield_Data" `
  "D:\Assets\fangzhou\Endfield_MissingSourceCabClosure_Current" `
  --game ArknightsEndfield `
  --locate_endfield_missing_source_cabs "D:\Assets\fangzhou\Endfield_SourceIndex_EndfieldData_DefaultVfsRule_Current\unity_source_index.db" `
  --endfield_cab_location_index "D:\Assets\fangzhou\Endfield_CabLocationIndex_Current\endfield_cab_location_index.json" `
  --source_candidate_limit 200
```

报告写到 `endfield_missing_source_cab_closure.json`。其中 `selectedTargets` 来自源索引里的确定性 PPtr 缺失目标，`relationBuckets` 按 `skinnedMeshRenderer.mesh`、`animator.avatar`、`renderer.material`、`animatorController.clip` 等 Unity 关系汇总缺失 CAB，`dependencyDomainBuckets` 再按 `model`、`material`、`animationClip` 三类门禁汇总；`locations` 是只读 VFS 扫描或 CAB 位置索引定位到的真实 inner bundle，`locatedUnityBundleFiles` 是需要补进小范围源索引复现的依赖包，`endfieldVfsFilesRegexForLocatedMissingBundles` 可以直接作为额外 `--endfield_vfs_files` 正则使用。这个命令不改 DB、不生成材质绑定、不导出模型；它只是把“模型第一阶段为什么缺 Mesh/Avatar/白模/缺材质/缺贴图”和“源索引里的 Controller/Animation 为什么缺 AnimationClip 目标”变成可追溯闭包证据。若模型本身仍缺 Mesh、Avatar、材质、贴图、UV、skin 或 bbox 验收失败，仍不能进入动画 smoke；若补到的 clip 属于 UI/widget/deco，也只能作为诊断线索，不能升级为生产角色动画样本。

已经有闭包报告后，小范围重建源索引时可以直接把报告传给 `--endfield_source_cab_closure`，不必手抄 `locatedUnityBundleFiles` 正则。模型第一阶段必须保留 root 上下文：通常从 `source_model_candidates.json` 里选一个候选的 `sourcePath`，把其中的 `Data/Bundles/Windows/.../*.ab` 写成 `--endfield_vfs_files` root 正则，再叠加 model/material closure。这样源索引既有 prefab/Renderer/Container 上下文，也有缺失 Mesh/Material/Texture 目标；只补闭包目标而不保留 root 上下文时，候选报告通常只会看到 raw fbx/source part，不能作为模型 smoke。

```powershell
dotnet AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.dll `
  "C:\Program Files\Hypergryph Launcher\games\Arknights Endfield\Endfield_Data" `
  "D:\Assets\fangzhou\Endfield_SourceIndex_RootPlusMissingCabClosure_Current" `
  --game ArknightsEndfield `
  --build_source_sqlite_index `
  --endfield_vfs_files "Data/Bundles/Windows/main/916e57093cacbf02bbe6b5b6\.ab$" `
  --endfield_source_cab_closure "D:\Assets\fangzhou\Endfield_MissingSourceCabClosure_Top4_Current\endfield_missing_source_cab_closure.json" `
  --profile_log source_index_profile.jsonl
```

`--endfield_source_cab_closure` 会把 `--endfield_vfs_file_limit` 视为 0，避免截断已定位依赖。默认会纳入报告里全部 located bundle；如果当前只在修模型第一阶段，建议追加 `--endfield_source_cab_closure_domains model material`，只补 `Mesh` / `Avatar` / `Material` / `Texture` 闭包，避免把大量 `AnimationClip` CAB 过早混进模型 smoke。等模型 Mesh、UV、材质、贴图、skin、bbox 通过后，再单独用 `--endfield_source_cab_closure_domains animationClip` 追动画 clip 闭包。`--endfield_source_cab_closure_include_auto_roots` 只适合没有明确 root bundle 时的重型诊断，它可能把 Endfield 默认 VFS root/context 扩成几十 GB 的大源索引；已知候选 root 时优先用显式 `--endfield_vfs_files`。它仍然是显式诊断/闭包复现入口，不是完整生产默认索引策略；生产化前应继续把 CAB 定位、manifest 依赖和源索引 health 结合起来，避免每次都全量递归扫描 VFS。

`--endfield_source_cab_closure` 可以一次传多个报告，适合分层闭包：第一轮报告补 `meshFilter.mesh` / `skinnedMeshRenderer.mesh` 缺失 Mesh CAB，后续报告继续补 `renderer.material` 缺失 Material CAB、`material.texture` 缺失 Texture CAB，以及 `animatorController.clip` / `animation.clip` 等缺失 AnimationClip CAB。例如：

```powershell
dotnet AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.dll `
  "C:\Program Files\Hypergryph Launcher\games\Arknights Endfield\Endfield_Data" `
  "D:\Assets\fangzhou\Endfield_SourceIndex_RootPlusMatTexClosure_Current" `
  --game ArknightsEndfield `
  --build_source_sqlite_index `
  --endfield_vfs_files "Data/Bundles/Windows/main/916e57093cacbf02bbe6b5b6\.ab$" `
  --endfield_source_cab_closure `
    "D:\Assets\fangzhou\Endfield_MissingSourceCabClosure_Top20_FromFullCabIndex_Current\endfield_missing_source_cab_closure.json" `
    "D:\Assets\fangzhou\Endfield_MissingSourceCabClosure_TextureTop20_FromFullCabIndex_Current\endfield_missing_source_cab_closure.json"
```

多报告闭包只是在源索引层补齐确定性依赖，不能跳过模型第一阶段验收。报告里的 `modelDependencyHealth.status=warning` 或 `materialRelationHealth.status=warning` 仍表示还有缺失目标；必须继续扩展闭包、重建源索引或换更完整样本，直到模型 glTF 的 Mesh、Avatar/骨骼、材质、贴图、UV、skin 和 bbox 通过静态验收，才允许进入动画阶段。

```powershell
dotnet AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.dll `
  "C:\Program Files\Hypergryph Launcher\games\Arknights Endfield\Endfield_Data" `
  "D:\Assets\fangzhou\Endfield_CabLocator_TopMissing_Current" `
  --game ArknightsEndfield `
  --locate_endfield_cabs CAB-6810cbcd1358d17f0ded1b80c6928d3c CAB-e291362aa0b61dff84be0492435e52ec
```

`--locate_endfield_cabs` 扫描 inner UnityFS bundle 的 fileList，只回答“这个 CAB 是否作为 bundle 内文件存在”。如果结果是 0 命中，但 `unity_source_index.db` 的 `source_externals.path_name` 里有 `archive:/CAB-...`，说明它是 Unity external 关系名，不一定是可直接按 fileList 找到的物理文件。

```powershell
dotnet AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.dll `
  "C:\Program Files\Hypergryph Launcher\games\Arknights Endfield\Endfield_Data\StreamingAssets\VFS\7064D8E2\7064D8E2.blc" `
  "D:\Assets\fangzhou\Endfield_StringLocator_TopMissing_Current" `
  --game ArknightsEndfield `
  --locate_endfield_strings CAB-6810cbcd1358d17f0ded1b80c6928d3c CAB-e291362aa0b61dff84be0492435e52ec b29a9989255bcb1702a81a20353add73
```

`--locate_endfield_strings` 直接扫描 `.chk` 明文字节，并用 VFS metadata 把命中 offset 映射回 inner 文件，输出 `endfield_string_locations.json`。报告里的 `targetSummaries` 按目标 CAB/字符串聚合，`innerFileSummaries` 按 `Data/Bundles/Windows/main/*.ab` 聚合，方便确认缺失 CAB 字符串来自哪些 inner bundle，继续反推 manifest/dependency 关系；它不解析 Unity 对象，不创建模型-动画绑定，也不能把缺材质样本升级为模型 smoke。

如果缺失 CAB 字符串已经能映射到 `Data/Bundles/Windows/main/*.ab`，下一步用 Endfield manifest 只读解析 bundle 依赖闭包。下面示例里的 `manifest.decompressed.bin` 是诊断输出文件；若 `Endfield_VfsManifestProbe_Current` 已被清理，需要先重新生成或传入新的 manifest 路径：

```powershell
dotnet AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.dll `
  "<manifest.decompressed.bin>" `
  "D:\Assets\fangzhou\Endfield_ManifestDeps_Current" `
  --game ArknightsEndfield `
  --inspect_endfield_manifest_deps main/6c24156ea2863cda2cedd17e.ab main/4725f1e91f7790c5b9fea875.ab
```

`--inspect_endfield_manifest_deps` 输入已解压或可识别 Brotli 的 Endfield manifest，输出 `endfield_manifest_dependencies.json`。报告里的 `dependencies` 是 manifest 记录的 bundle 依赖路径，可用于判断当前 `unity_source_index.db` 是否漏了 Material / Texture 所在依赖包。这个命令仍是诊断工具：它不解析 Unity 对象，不生成绑定关系，也不能让模型第一阶段未通过的白模样本进入动画 smoke。确认依赖闭包后，可以用 `--endfield_vfs_files` 做小范围 source index 复现；正式修复应把 manifest 闭包接入 Endfield 源索引选择，而不是靠名字或目录猜材质关系。

小范围重建源索引时，可以让 `--endfield_manifest_deps` 自动把 root bundle 扩展为“root + manifest 直接依赖”，不用手写几十个依赖 regex：

```powershell
dotnet AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.dll `
  "C:\Program Files\Hypergryph Launcher\games\Arknights Endfield\Endfield_Data" `
  "D:\Assets\fangzhou\Endfield_SourceIndex_ManifestAutoDirectDeps_6c241_Current" `
  --game ArknightsEndfield `
  --build_source_sqlite_index `
  --endfield_vfs_files "Data/Bundles/Windows/main/6c24156ea2863cda2cedd17e\.ab$" `
  --endfield_manifest_deps "<manifest.decompressed.bin>"
```

这里 `--endfield_vfs_files` 只选择 root bundle；`--endfield_manifest_deps` 会解析 manifest，自动把 root 和直接依赖 bundle 做成精确 inner-file filter，并把 `--endfield_vfs_file_limit` 视为 0，避免截断依赖。当前默认只扩展直接依赖，不递归展开所有依赖，原因是递归闭包可能从 1 个 root 膨胀到上万个 bundle，失去小样本诊断价值。若后续确实需要递归闭包，应作为显式慢速参数并用 profile 证明必要性。

定向 Library 导出时，如果已经提供完整或足够覆盖目标的 `--source_index`，并且命令带了 `--names` 或 `--path_ids`，CLI 会从源索引里查找命中对象所在 CAB、AssetBundle 内部路径和 external CAB 闭包，自动收窄本次 `.blc` 内部 UnityFS 加载范围。这个优化只改变加载范围，关系仍来自 SQLite 源索引；如果源索引缺目标外部依赖，对应材质、贴图或附件仍可能缺失，应重新构建更完整的源索引。

如果小样本涉及 AnimatorController 分层动画，且已经有 `unity_file_inspect.json`，定向导出时应同时传入 `--unity_file_inspect`。CLI 会把确定的 `animatorControllerContext.baseLayerClip` 自动补进本次 `--path_ids` 导出，并在 `library_index.db` 建好后刷新 controller context。这样选中的非 base layer / additive clip 不会单独入库后被误当成完整身体动作；自动补导只来自 Unity controller 显式结构，不新增模型-动画推荐关系。

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

`--unity_avatar_asset` 必须指向 bake 工程内的 `Assets/.../*.asset` 路径。AnimeStudio 会把它写入 request 的 `unityAssetPaths.avatarAsset`，Unity helper 会直接 `AssetDatabase.LoadAssetAtPath<Avatar>`；加载到有效 human Avatar 时，结果报告才允许标记 `avatarTrust.source=imported_unity_avatar_asset` 和 `trustedProductionBake=true`。一旦 request 显式带了 `unityAssetPaths.avatarAsset`，Unity helper 加载失败会直接让 bake 失败，不能静默退回 `BuildHumanAvatar`；`unity_bake_result.json` 会写出 `requestedAvatarAsset`、`importedAvatarAsset` 和 `importedAvatarAssetValid`，同时写出 `requestedAnimationClip`、`importedAnimationClip`、`animationClipSource` 和 Unity 导入后的 `isHumanMotion`，用于确认真正进入 PlayableGraph 的 clip 来源。`unity_bake_apply_report.json` 会同步写出 `unityBakeRequestedAvatarAsset`、`unityBakeImportedAvatarAsset` 和 `unityBakeImportedAvatarAssetValid`。apply、Browser、SQLite 摘要和覆盖率脚本读取阶段也只接受这个导入 Avatar asset 证明可信；旧报告没有 `unityBakeImportedAvatarAssetValid` 时，必须至少同时看到 `unityBakeRigRestPoseSource=imported_unity_avatar_asset` 和 `unityBakeRigRestPoseApplied=true`，不能只凭 `avatarTrust.source` 放行。即使 request 里同时存在 `HumanDescription.skeletonBones`、prefab 或 AvatarConstant/internalSolver 诊断数据，也不能在 Avatar asset 无效或旧报告来源不匹配时把结果标成可信。这个参数只能用于明确 `--preview_model` 的单模型定向预览/诊断，不能在未指定模型或命中多个模型时把同一个 Avatar 批量套用到不同模型。批量生产 bake 应把恢复出的 Avatar 放入 `Assets/AnimeStudioBake/ImportedAvatar` 或写入 `unityAvatarAssets` 映射，让工具按模型 Avatar 名/模型名精确匹配。`unity_bake_batch_report.json` 会在顶层记录 `avatarAssetCounts`、`avatarSourceCounts`、`avatarMatchKeyCounts`，并在 `items[*]` 里记录 `unityAvatarAsset`、`avatarSource` 和 `avatarMatchKey`，方便人工直接审查每条 request 是否用了正确的 Avatar oracle，以及它是通过哪个模型 key 命中的。如果没有这个参数，流程回到普通 Unity 项目的 prefab/HumanDescription 路径；`AvatarConstant/internalSolver` 只保留诊断作用，不能单独算可信生产动画。

Ambor 这类角色专属 `Ani_Avatar_Girl_Bow_Ambor_*` clip 可能是 AnimatorController 里的辅助 clip，只包含 root、附件、姿态层或局部叠加，单独导入 Unity 时会出现 `isHumanMotion=false`、静态姿态、半身入地或动作语义不符。生产预览必须先用 `--refresh_animator_controller_contexts` 从源索引恢复它所在的 controller/state/blend-tree 上下文，把请求重定向到同一状态的完整身体 clip，例如 `Ani_Avatar_Girl_Bow_Ambor_RunCycle` 对应 `Ani_Avatar_Girl_RunCycle`。这仍然来自 Unity 显式控制器关系，不是按名字猜测；不能把辅助 clip 直接硬 bake 成可播放 glTF。

旧 helper 生成的 `baked` 如果缺少可信 Avatar 证明，不能当成已经完成的生产动画。批量 request 生成器只会跳过通过 `unity_bake_apply_report.json` 复核的可信 baked、确认静态姿态或需要人工复核项；不可信旧 `baked` 会重新进入待 bake 队列，并且不可信旧 `baked` 可以被后续失败或新请求状态覆盖，避免历史错误结果长期占坑。文档和源码门禁用 `cacheTrustGate=untrusted_requeue_overwriteable` 标记这条规则。

如果旧库里已经存在“需 AnimatorController 上下文”的显式候选，可以用 Unity 控制器 inspect 结果回写候选上下文。这个命令只沿 `Animator.controller` / `AnimatorOverrideController.baseController` 显式关系查找控制器，并把辅助层 clip 映射到同一 state、同一 blend-tree node 的基础身体 clip；不会按动画名、骨骼数量或目录猜测关系。刷新器也会读取素材库本地 Browser 设置和 bake 工程 `Assets/AnimeStudioBake/ImportedAvatar/*.asset`，只有 fresh probe 证明 `Avatar.isValid && Avatar.isHuman` 的导入 Avatar 才会让候选进入生产 bake ready：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --refresh_animator_controller_contexts "D:\Assets\AS-Assets\YuanShen-Assets" `
  --unity_file_inspect "D:\Assets\AS-Assets\YuanShen-Assets\.as_browser_cache\diagnostics\ambor_controller_inspect\unity_file_inspect.json"
```

刷新器对默认附加层要保守处理。只有非 base layer 的默认状态能确定为“唯一 clip”时，才会写入 `animatorControllerContext.additionalLayerClips`；如果默认状态是多 clip BlendTree、参数驱动 BlendTree 或需要权重/参数才能判断，刷新器会写入 `additionalLayerContextWarnings`，并要求恢复原始 RuntimeAnimatorController 上下文。不能把多 clip BlendTree 的所有 child clip 都按 1.0 权重叠到生成 controller 上。Pelica 的 `LoopClothAddLayer.LoopClothAddIdle` 就属于这种情况：它需要 `IdleTailIndex` 等参数，当前只能作为 `needs_original_animator_controller_context` 诊断，不可标成生产动画通过。

Unity bake 诊断可用 `--unity_bake_clip_filter_mode` 显式覆盖 clip 过滤：

- `auto` / `default`：使用当前安全门禁；Endfield 这类 MixedHumanoidTransform 只要带 AnimatorController context、但还没有显式原始 RuntimeAnimatorController asset，就会优先降成 `transform_only` 诊断，避免生成 Pelica idle 那种双手抬高/手腕扭曲但看起来像完整动作的误导性 full-body bake。
- `transform_only`：只采样确定 Transform/controller layer 曲线，排除主 Humanoid/Muscle 曲线；适合定位主 Humanoid 曲线是否引入扭曲，但结果不是完整身体动作。
- `full` / `none` / `full_clip`：采样完整 AnimationClip；适合和 `transform_only` 做对照。如果结果来自 `generated_single_state_controller` 或 `generated_multi_layer_controller`，仍然只能算诊断，必须等待原始 AnimatorController 语义恢复和清晰视觉验收。

如果已经把原始 RuntimeAnimatorController 恢复到 Unity bake 工程，可以用 `--unity_animator_controller "Assets/.../*.controller"` 显式传入，或在 `unity_bake_settings.json` 里写 `unityAnimatorControllers` 映射。CLI 只把这个路径写入 request，Unity helper 会用 `AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>` 加载；加载成功时 result 记录 `animatorControllerSamplingMode=provided_runtime_controller`。如果 request 传了 controller 但 Unity 加载失败，result 会记录 `controller_asset_missing`，apply report 必须阻断为 `needs_original_animator_controller_asset`，不能因为 glTF 写出了 TRS channel 就标成可复用动画。

Library Browser 也支持同一条路径。素材库根目录可写本地配置 `.as_browser_cache\unity_bake_settings.json`，本地配置会覆盖全局配置：

```json
{
  "unityProject": "D:\\misutime\\AnimeStudioUnityProject",
  "unityEditor": "C:\\Program Files\\Unity\\Hub\\Editor\\6000.4.11f1\\Editor\\Unity.exe",
  "unityAvatarAssets": {
    "NPC_Male_Standard_Model": "Assets/AnimeStudioBake/ImportedAvatar/NPC_Male_Common_ModelAvatar.asset",
    "Avatar_Boy_Bow_Gorou": "Assets/AnimeStudioBake/ImportedAvatar/Avatar_Boy_Bow_Gorou_ModelAvatar.asset"
  },
  "unityAnimationClips": {
    "Ani_Avatar_Girl_RunCycle": "Assets/AnimeStudioBake/ImportedAnimationClip/Ani_Avatar_Girl_RunCycle.anim",
    "Ani_Avatar_Girl_Channel01Loop": "Assets/AnimeStudioBake/ImportedAnimationClip/Ani_Avatar_Girl_Channel01Loop.anim"
  },
  "unityAnimatorControllers": {
    "AC_npc_human_girl_pelica_optNew": "Assets/AnimeStudioBake/ImportedAnimatorController/AC_npc_human_girl_pelica_optNew.controller"
  }
}
```

`unityAvatarAssets` 是显式模型名到 Avatar asset 的映射。没有命中的模型不会自动套用这些 Avatar，因此 VRising、Freedunk 等普通 Unity 库仍走默认 `Animator.avatar` / HumanDescription 路径。

Endfield 这类模型如果 `Animator.avatar` 指向 `*_genericAvatar`，恢复器只允许在同一个 `unity_source_index.db` 源文件里查找“去掉 `_genericAvatar` 后精确同名、且 `humanBoneCount/skeletonBoneCount` 都大于 0”的 Avatar，并把原 generic 名作为 alias 写入 `unityAvatarAssets`。这只是补齐生产 Avatar oracle，不会新增模型-动画关系。即使 Avatar 已通过 Unity probe，`UnityBakeAccelerated` 仍要求动画 sidecar 里存在真实 `decoded.floats` 逐帧 Humanoid/Muscle 曲线；只有 `m_ValueArrayDelta` / binding layout 时会标为 `missing_decoded_float_curves`，不能生成生产 request。

`unityAnimationClips` 是显式动画名到 Unity `AnimationClip` asset 的映射，也可以直接把 `.anim` 放进 bake 工程 `Assets/AnimeStudioBake/ImportedAnimationClip/`，CLI 会按当前实际要 bake 的动画名或文件名精确匹配。Ambor 这类 controller context 修正后，匹配 key 是修正后的完整身体 clip，例如 `Ani_Avatar_Girl_RunCycle`，不是用户最初点到的辅助 clip `Ani_Avatar_Girl_Bow_Ambor_RunCycle`。命中后 request 会写 `unityAssetPaths.animationClip`，Unity helper 会用 `AssetDatabase.LoadAssetAtPath<AnimationClip>` 加载并在结果里记录 `animationClipSource=unityAssetPaths.animationClip`；未命中时才回到导出的 `.anim` sidecar 导入路径。手动 `--unity_animation_clip` 只允许单动画定向 bake，批量命中多个动画时会拒绝，避免把同一个 clip 套给不同候选。

新导出或重建 SQLite 后，如果素材库已经配置了 Unity bake project，CLI 会自动尝试恢复 ImportedAvatar 和 ImportedAnimationClip。AnimationClip 恢复只读取 `library_index.db` 里的 `relation_source=explicit` Humanoid/Muscle 候选；如果候选来自 AnimatorController 辅助层，会按同一 state 的 `animatorControllerContext.baseLayerClip` 恢复真正身体主 clip，不按名字、目录、骨骼数量猜测。也可以手动运行：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --recover_imported_animation_clips "D:\Assets\AS-Assets\YuanShen-Assets" `
  --unity_project "D:\misutime\AnimeStudioUnityProject"
```

命令会把需要的 `.anim` 复制到 `Assets/AnimeStudioBake/ImportedAnimationClip/`，并写入素材库本地 `.as_browser_cache\unity_bake_settings.json` 的 `unityAnimationClips` 映射。可用 `--preview_model` 或 `--preview_animation` 做定向恢复，`--avatar_recovery_limit` 限制数量，`--avatar_recovery_force` 强制覆盖已存在文件。

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
- `decoded.playbackKind` / `decoded.directGltfReady` / `decoded.requiresInternalHumanoidSolve`。Transform TRS 曲线可以进入直接 glTF 预览；Humanoid/Muscle 的生产目标也是 AnimeStudio 直接生成可用 glTF TRS / weights。旧的 Unity bake -> glTF 只能保留为诊断和对照验证，`requiresInternalHumanoidSolve` 不能伪装成已经可直接播放。

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

重点看 `model_avatar_refresh_summary.json` 的 `totalPlannedUnlockCount` / `missingReasonCounts` / `globalMissing`，以及 `model_avatar_refresh_plan.csv` 里的 `missing_internal_solver_candidate_count` 和 `missing_reason`。计划脚本同时兼容旧库的 `requiresInternalHumanoidSolve=true` 候选，以及新索引里的 `missingInternalHumanoidSolver=true` / `inspect_missing_humanoid_avatar` 候选；它们都表示“关系来自 Unity，但模型缺完整 Avatar 参考姿态或内部 Humanoid 元数据”。`missing_reason` 会区分 `missing_avatar_object`、`missing_internal_solver`、`missing_solver_skeleton_nodes`、`missing_avatar_pose_metadata`、`missing_human_bone_index` 和 `missing_human_bone_limits`；其中旧库常见的 `missing_avatar_pose_metadata` 表示已有 internalSolver 但缺 `humanSkeletonPose` 或 `humanBoneIndex`，也必须刷新，不能继续生成会手脚反折的预览。部分 Unity 版本或游戏会缺 `internalSolver.skeleton.nodes/humanSkeletonPose`，但仍有 `internalSolver.avatarSkeleton.nodes/defaultPose`；这是 Unity AvatarConstant 的确定性骨架参考姿态，只能用于诊断和后续求解研究，不能单独升级为生产可播放依据。`humanBoneIndex` 必须至少有一个有效节点索引；如果数组存在但全是 `-1`，仍然是 `missing_human_bone_index`。`human_bone_limits` 这类 Avatar 元数据可帮助后续研究 Humanoid/Muscle -> 目标骨架 TRS，但不能单独让候选进入 `UnityBakeAccelerated` 生产路线；生产路线还需要真实 Avatar asset 或完整 HumanDescription、glTF TRS 写回、性能报告和视觉验收。`globalMissing.scanMode=summary_table` 表示脚本用 `model_animation_candidate_model_summary` 快速统计全库 Avatar/internalSolver 缺口，不扫描几百万条候选 `raw_json`；只有非常旧的 SQLite 没有 summary 表时，才需要显式加 `-IncludeGlobalMissingScan` 允许慢扫。`plannedCandidateCoverageOfGlobalMissing` 可以粗看当前这批刷新计划覆盖了全局缺口的多少候选。如果 `unity_source_index.db` 的 `metadata.sourceRoot` 和手工传入的 `-SourceRoot` 不一致，计划脚本会优先使用源索引记录的 root 生成命令，确保 `--source_files` 与依赖图同根。若 `library_index.db` 是通过外部 fresh source index 重建的，显式传 `-SourceIndex`；未传时脚本会优先读取 `sqlite_index_summary.json` 中本库实际使用的 source index，避免误回退到 Library 根目录下的旧 `unity_source_index.db`。执行 `model_avatar_refresh_commands.ps1` 前应人工抽查命令里的 `--source_index`、`--source_files`、`--path_ids` 和模型名是否符合预期。`Apply-ModelAvatarRefresh.ps1` 回写模型 avatar 后，也只能把 Avatar / HumanDescription 字段作为诊断资料或后续求解输入，不能再把显式 Humanoid 候选升级为旧 `requiresUnityBake=true`、`productionAnimationPath=UnityBakeToGltf` 或 `nextAction=generate_unity_baked_gltf`。旧的 bake 预览命令只保留给历史库对照和迁移排错，不再作为生产验收路径。

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

Freedunk 当前已确认的情况：`NORMALMOVE_STAND_01`、`DASH_01` 属于 `MixedHumanoidTransform`，核心数据在 Unity Humanoid/Muscle 曲线中。`ApproximateHumanoidMuscleV*` 只能用于生成实验 channel 和调试报告，不能作为最终动画资产验收。生产目标是让模型和动画最终能直接合成 glTF/GLB：可以走 AnimeStudio 内部 Humanoid/Muscle 求解，也可以走自动化、高吞吐、可复现的 `UnityBakeAccelerated`。若一个游戏只能依赖旧式游戏专用 Unity 工程、手工插件/helper 或低吞吐逐资源人工 bake 才能恢复动画，就不要把它升级为生产动画支持；应明确标记 `needsDirectTrsAnimation` / `deprecatedUnityBakeOnly` / `notProductionReady`。

如果 `model_animations.json` 来自一个临时小样本目录，预览/打包时必须传完整 Unity 源目录，避免旧样本缺少外部 CAB 依赖导致脸、附件、材质或 Mesh 再次断链：

```powershell
--preview_source_root "C:\Program Files (x86)\Freedunk\Game\Freedunk_Data"
```

预览命令会使用完整 source root 重新定位索引里的 source 文件，并用完整 CAB map 解析外部 PPtr。`preview_validation.json` 也会记录 `exportIssues`；只要导出日志出现 `Unable to resolve ...` 这类依赖错误，状态就不能是 `ok`。

注意：`ApproximateHumanoidMuscleV*` 仍是实验求解/诊断输出。它只能说明“Muscle 曲线被写成了 glTF channel”，不能说明动作姿态正确。凡是使用该输出的预览会标为 `experimental`，不能作为最终可复用动画资产验收。

### 查询模型的确定性动画列表

当已经有 `library_index.db`，可以先只列出某个模型的可信动画候选，再决定导出哪条独立动画或合成预览。这个命令只读取 SQLite 中 `relation_source=explicit` 的候选，不会按名称、目录、骨骼数量或结构兼容新增关系：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --list_model_animations_from_library "D:\Assets\fangzhou\Endfield_ExplicitController11_ActionSmoke_NormalizedWeights" `
  --preview_model "^chr_0017_yvonne_uimodel$" `
  --preview_output "D:\Assets\fangzhou\Endfield_ExplicitController11_AnimationLists\yvonne" `
  --pack_limit 100
```

输出 `model_animation_candidates.json`，包含模型 glTF、材质/骨骼摘要、每条动画的 `relation`、`confidence`、`modelReadyForAnimation`、`modelAnimationGate`、`productionAnimationPath`、`directGltfAnimationStatus`、`requiresInternalHumanoidSolve`、`controllerState` 和推荐的独立动画导出/合成命令。顶层和每个模型都会写 `usableCandidateCount` / `modelGateBlockedCount`；这个文件适合作为统一素材浏览器或命令行选择器的模型详情数据源，但被模型 gate 阻断的显式关系只能做诊断，不应显示为可播放候选。

如果候选为 0，且 Library 根目录有 `source_index_usage.json` 或 `unity_source_index.db`，报告会在模型上写 `sourceRelationDiagnostic`。这个字段只解释源索引里实际存在的 Unity 关系，例如“Animator 只有 `animator.avatar`，没有 `animator.controller` / `animation.clip`”，或“源索引有 controller 但 Library 候选表未导入”。它是排查信息，不会因为同名、同目录、同 Avatar 或骨架兼容而新增默认绑定；看到 0 候选时应先按这里的 `nextAction` 修索引或继续寻找真正的 controller/clip/PPtr 关系。

Endfield postmodel 这类样本如果自身 `Animator.controller` 为空，0 候选报告还会写 `nameTokenControllerHints`。它只列出同角色 token 附近可能存在的 `AnimatorController` / `AnimatorOverrideController`、入边数量和样例 clip，用来判断“controller 存在但缺模型引用桥”还是“根本没有 controller 线索”。这个字段是 diagnostic-only，不能把 `AC_chr_*`、目录或角色名提升为默认模型-动画绑定。

`directGltfAnimationReady=true` 只表示动画 sidecar 已经有可写入 glTF channel 的 TRS/weights 数据，可用于诊断预览或后续合成测试；它不自动等于独立身体动作生产可用。若 sidecar 标记 `standaloneBodyBakeReady=false` / `standaloneBodyBakeStatus=requires_animator_controller_context`，候选会保留 `directGltfPreviewReady=true`，但 `productionAnimationReady=false`、`productionAnimationPath=NeedsAnimatorControllerContext`。这类 clip 必须先恢复或采样原始 AnimatorController state/layer/blend tree 上下文，不能仅凭 direct TRS channel 数量进入默认可复用动画结论。

同理，若 sidecar 标记 `standaloneBodyBakeReady=false` / `standaloneBodyBakeStatus=needs_direct_trs_animation`，即使 `decoded.directGltfReady=true`，也只能说明已有部分可预览 TRS 曲线。候选应保持 `directGltfPreviewReady=true`，但 `productionAnimationReady=false`、`productionAnimationPath=NeedsDirectTrsAnimation`，直到 Humanoid/Muscle、root/aux float 或缺失主体骨骼被求解成完整目标骨架 TRS。

若 sidecar 标记 `standaloneBodyBakeReady=true` / `standaloneBodyBakeStatus=direct_transform_body_trs`，表示该 clip 虽然可能带 Humanoid/Muscle 外壳，但核心身体骨骼已经有可直接写入 glTF 的 Transform TRS 曲线。此时候选可以进入 `ProductionReady` 的结构门槛，后续仍必须通过 `--export_animation_gltf_from_library`、`--merge_animation_gltf` 和清晰截图/截帧做视觉验收，不能只靠 channel 数量宣布角色动画正确。

模型质量是动画候选的前置 gate。`library_index.db` 可以保留 `Animator -> Controller -> AnimationClip` 的显式关系作诊断，但如果缺少 `model_validation.json`，或模型 catalog / `model_validation.json` 显示材质、贴图、图片、skin 或来源域不适合默认素材库，例如 `materialCount=0`、`textureCount=0`、`materialImageCount=0`，或来源是 Dialog / Timeline / LevelSeq / UI / preview / postmodel / abilityentity / tmpobject / cutscene 专用实例，候选会写成 `status=model_not_animation_ready`、`nextAction=fix_model_first`、`modelReadyForAnimation=false`，并在 `modelAnimationGate.reasons` 里说明原因。此时 `relation_animations.is_usable_candidate=0`，`model_animation_relations.animation_count` 只统计已通过模型门禁的可用候选；被阻断的显式关系保留在 `raw_json.totalCandidateCount` / `modelGateBlockedCount` 和 `relation_animations` 里作诊断。`--generate_preview_gltf`、`--generate_preview_from_library`、`--pack_model_animations`、`--pack_model_animations_from_library` 和 `--export_animation_gltf_from_library` 会拒绝 gate 缺失或 blocked 的候选。浏览器和批量预览不应把它当作可播放素材；应先修模型或换真实游戏内可复用模型，再进入动画阶段。

`asset_library.json.capabilities.animations` 也遵守同一门禁：只有素材库里实际存在 `Animation` 资产，并且 `relation_animations` 至少有一条 `is_usable_candidate=1` 的确定性关系时才写 `true`。模型-only smoke、动画资产缺失、或所有关系都被模型优先 gate 阻断时会写 `animations=false`，避免统一浏览器误以为可以直接生成动画预览。

动画本身也有默认语义 gate。`AnimatorController` 显式引用到 `cs_`、cutscene、dialog、timeline、UI、preview、pose、deco 等上下文 clip 时，关系仍会写入 `model_animation_candidates` / `relation_animations`，但状态为 `animation_not_default_candidate`，`relation_animations.is_usable_candidate=0`，`asset_library.json.capabilities.animations=false`。这类关系只能说明 Unity 状态机里存在上下文动画，不能作为生产动作 smoke 或可复用动画能力证明；应继续寻找正式 gameplay/locomotion/skill/attack/idle 等动作，或恢复 AnimatorController 上下文后单独验收。

### 合并独立动画 glTF

`--export_animation_gltf_from_library` 或文件入口 `--export_animation_gltf_from_files` 生成的是无 mesh/skin 的独立动画 glTF，适合作为类似 Mixamo 的“纯动画资产”。需要把它重新应用到模型时，用 `--merge_animation_gltf`：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --merge_animation_gltf "F:\Unity-AS-Assets\VRising-Assets\Models\HYB\HYB_NPCCultist_Standard\HYB_NPCCultist_Standard.gltf" `
  --preview_animation "D:\Assets\fangzhou\VRising_DirectAnimation_CurrentCode_Smoke\Standalone_CultistStandard_Run_AvatarDiag\NPCCultist_Run.animation.gltf" `
  --preview_output "D:\Assets\fangzhou\VRising_DirectAnimation_CurrentCode_Smoke\Merged_CultistStandard_Run_FromStandalone"
```

这个命令只做 glTF 层面的确定性合并：模型、材质、贴图、skin 来自模型 glTF；动画 channel 来自 standalone animation glTF；节点按完整路径、唯一后缀或唯一叶子名重映射。节点无法确定匹配时命令失败并写 `merge_animation_gltf_report.json`，不会为了输出文件而硬猜。Humanoid/Muscle standalone 动画可以来自 AnimeStudio 直接 TRS 求解，也可以来自显式 Unity native 求解后的 TRS 写回；无论来源如何，都必须标注 solve path，并通过 glTF channel 和视觉验收。

合并前同样会先验证模型 glTF。若模型没有材质/贴图 image、材质槽覆盖不足、外部贴图或 buffer 缺失、skin 绑定异常，`merge_animation_gltf_report.json` 会写 `status=blocked`、`reason=model_not_animation_ready` 和 `modelGate.reasons`，并且不会生成 merged glTF。已经能把动画 channel 合进去，不代表模型可用；模型第一阶段不过关时必须先修模型。

`--preview_output` 默认表示输出目录；如果明确传入以 `.gltf` 结尾的路径，则该路径会作为最终 glTF 文件名，`Buffers/`、`Textures/`、`AnimationBuffers/` 和 `merge_animation_gltf_report.json` 写在同级目录。显式文件模式不会清空父目录，避免覆盖同目录里的其它验收材料。

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

### Unity Humanoid glTF 写回主线

当前可验收主线是“确定性关系 + Humanoid 求解 + glTF/GLB 写回”：模型-动画关系仍然只来自 `library_index.db` 的 `relation_source=explicit` 候选。Unity bake 可以作为求解器，负责用 Animator/Avatar/Unity native Humanoid 把 muscle 动画采样成目标骨架 TRS；AnimeStudio 再把 TRS 写回 glTF/GLB。验收看最终 glTF 数据和截图，不要求用户在 Unity 里播放。

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

2026-06-24 起，批量报告和 `animation_bake_cache` 会透传 `unity_bake_apply_report.json.animationSolve` 的核心门禁字段，包括 `solvePath`、`productionStatus`、`requiresVisualValidation`、`writesReusableGltfTrsCandidate` 和完整 `animationSolve` JSON。批量 smoke 不能只看 `items[*].status=needs_review` 或 glTF 文件存在；应优先看 `animationSolve.path`、`productionStatus`、`requiresVisualValidation`、`writesReusableGltfTrsCandidate`、`reusableCandidateBlockedReasons` 和截图是否通过。Pelica 回归已经证明：generated single-state controller 即使写出 glTF TRS、validator 无 error、有核心骨骼变化，也可能产生 idle 语义明显错误的双手抬高/手腕扭曲结果；这类报告必须阻断为 `needs_original_animator_controller_context`，不能作为生产完成。

2026-06-24 追加：recovered AnimatorController 不能照抄 Unity 序列化中的 base layer `defaultWeight=0`，第 0 层恢复时必须按运行时主层处理为 1；非 base layer 默认状态没有可恢复 motion 时也不能创建权重 1 的空 override/additive 层，否则会遮住下层动作。修复后 Pelica recovered controller 不再保留空 Upper/Arm/Head/MeshSpace 层，但 `P_actor_pelica_01 + A_actor_pelica_idle_loop` 仍然是 `runtime_pose_not_driven/static_pose`：动态 editor curves 存在，HumanPose 运行时不随帧变化。它继续是失败回归样本，不是生产动画样本。

2026-06-24 再追加：`UnityBakeResultApplier` 现在会在 Humanoid runtime `tracks` 静止、但 `editorCurveSetHumanPoseTransformTracks` 有核心骨骼逐帧变化时，写出显式诊断 glTF。报告字段为 `unityBakeTrackSource=editor_curve_set_human_pose_diagnostic`、`animationSolve.path=UnityEditorCurveHumanPoseDiagnostic`、`productionStatus=diagnostic_editor_curve_human_pose_solver`、`writesReusableGltfTrsCandidate=false`。Pelica 验证输出 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_EditorCurveHumanPoseDiagnostic_Current`：`writtenTracks=12`、`frameVaryingTracks=12`、`coreBodyFrameVaryingTracks=11`，glTF validator 无 error；截图显示双手不再举高、手腕不再麻花式扭曲，但仍缺 limb IK/TDOF/root/controller 细节，不能算生产通过。后续批量报告必须把这类结果当作 solver 诊断，而不是可复用动画候选。

同日再追加：`unityBakeTrackSource=animation_mode_sample_clip_diagnostic` 表示 Unity Editor 的 `AnimationMode.SampleAnimationClip` 能采到逐帧 Transform，但这不是生产求解路径。Pelica 验证输出 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_AnimationModeSampleClip_Current`：`writtenTracks=182`、`frameVaryingTracks=78`、`coreBodyFrameVaryingTracks=17`，glTF validator 无 error；截图显示它比旧 generated controller 少了双手举高问题，但整体仍接近展开基准姿态，不符合可信 idle。该状态必须保持 `needs_review` / `diagnostic_animation_mode_sample_clip`，不能进入可复用动画索引。

apply 报告现在会写 `unityBakeEditorCurveCategorySummary` 和 `animationSolve.editorCurveCategorySummary`，用来解释 runtime HumanPose 没动时哪些 editor curve 实际在动。Pelica 当前统计为普通 Transform 曲线动态 311 条、Animator 空路径曲线动态 55 条、Humanoid hand/foot goal 动态 28 条、Root/Body 动态 7 条、TDOF 动态 0 条；这说明该样本不是普通 ACL/TRS 漏写，也不是单纯 muscle/root 求解就能完成，而是需要 controller layer/IK/权重语义或等价直接合成。

`unityBakeAnimatorControllerLayerDiagnostics` 会列出 Unity bake worker 实际采样的 AnimatorController 层、状态、权重、additive 标记和 request mask 对齐情况。Pelica recovered controller 诊断输出 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_RecoveredControllerLayerDiagnostics2_Current` 表明 `LoopBodyAddLayer` 命中了 human mask 和 112 条 skeleton mask entry，但 `maskApplied=false`；apply report 因此写 `productionStatus=needs_animator_controller_layer_masks`、`reusableCandidateBlockedReasons=["animator_controller_layer_masks_not_applied"]`。这类结果即使 `frameVaryingTracks` 很多，也不能作为可复用动画。

`D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_RecoveredControllerMaskedLayerMixer_Current` 进一步验证了 masked layer mixer 路径：report 中 `animatorControllerLayerMasksApplied=true`，`LoopBodyAddLayer.maskApplied=true`，并解析了 `112/112` 条 skeleton mask path。但清晰上半身/手部截图仍显示 `idle_loop` 双手抬高，视觉失败。因此 `UnityBakeResultApplier` 会把这类 UnityDerivedPlayableGraph 输出继续标为 `productionStatus=needs_visual_validation`、`writesReusableGltfTrsCandidate=false`、`reusableCandidateBlockedReasons=["requires_clear_visual_validation"]`。批量 smoke 看到 mask 已应用、TRS 已写入、validator 无 error 时，也必须继续看 rest/start/mid/end 截图；截图失败时不能升级为可复用动画。

`D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_ControllerFidelityDiagnostics_Current` 起，Unity bake result/apply report 会透传 `unityBakeAnimatorControllerParameterDiagnostics`、`unityBakeAnimatorControllerBlendTreeDiagnostics`、`unityBakeAnimatorControllerLayerMixerBypassesControllerBlendTrees` 和 `unityBakeAnimatorControllerFidelityWarning`。Pelica 诊断显示 recovered controller 参数包括 `IdleTailIndex`、`idleBasePoseWeight`、`floorNormalAmount` 等 9 个 float，当前默认值都是 `0`；`LoopClothAddLayer` 的 Simple1D BlendTree 在 `IdleTailIndex=0` 下会选 `A_actor_pelica_idle_loop_additive_cloth`。但 masked layer mixer 为了应用 skeleton mask 绕过了 recovered controller 的 BlendTree 参数求值，所以 apply report 会写 `productionStatus=needs_animator_controller_blend_tree_context`、blocked reason=`animator_controller_blend_tree_parameters_bypassed_by_layer_mixer`。这类结果比“mask 未应用”更进一步，但仍不是生产动画。

`D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_RecoveredControllerBlendTreeDefaultMixer_Current` 起，recovered-controller layer mixer 会从 recovered AnimatorController asset 的默认层读取 motion，并把 Simple1D BlendTree 按 recovered 参数默认值扁平成选中的 child。Pelica 里 `LoopClothAddLayer` 的 `IdleTailIndex=0` 已能选中 `A_actor_pelica_idle_loop_additive_cloth`，因此 `animatorControllerLayerMixerBypassesControllerBlendTrees=false`。但该输出仍视觉失败，并暴露了更具体的问题：`ClothCombine` 是非 base、非 additive、权重 1、无 request mask 上下文的 override 层，却以全身层进入 mixer。

`D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_UnmaskedOverrideLayerDiagnostics_Current` 起，apply report 会写 `unityBakeAnimatorControllerUnmaskedOverrideLayerCount` / `unityBakeAnimatorControllerUnmaskedOverrideLayerNames`，并把这类风险阻断为 `productionStatus=needs_animator_controller_layer_masks`、blocked reason=`animator_controller_unmasked_override_layers`。Pelica 当前命中 `["ClothCombine"]`。后续修复方向是把 `unity_file_inspect.json` 中每个 AnimatorController layer 的 `bodyMask` / `skeletonMask` / runtime layer 权重带入 recovery request/result，并在 layer mixer 中逐层应用；不能把缺 mask 上下文的 override 层当成生产动画。

`D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_SkipUntrustedLayers_Current` 起，recovered-controller layer mixer 会跳过缺少确定性 recovery metadata 或 request mask context 的非 base 层，并在 Unity result/apply report 写 `animatorControllerSkippedUntrustedLayerCount`、`animatorControllerSkippedUntrustedLayerNames`、`unityBakeAnimatorControllerSkippedUntrustedLayerCount`。本次 Pelica 复测里 recovered metadata 已把实际采样收敛为 `Main`、`LoopClothAddLayer`、`LoopBodyAddLayer` 三层，`ClothCombine` 不再进入 mixer，`animatorControllerUnmaskedOverrideLayerCount=0`，并成功应用 `2/2` 个 skeleton mask、解析 `264/264` 条 masked transform path。但截图仍显示 idle 双手高举，报告仍为 `productionStatus=needs_limb_ik_goal_solver`，blocked reasons 包含 `dynamic_limb_goal_curves_not_solved`；这说明当前阻塞已经从 layer mask 污染转向 limb IK goal / goal 权重 / goal 空间 / controller IK pass 语义。

`ANIMESTUDIO_UNITY_BAKE_ENABLE_IK_GOAL_DRIVER=1` 会打开一个显式 IK goal 诊断：worker 会复制 AnimatorController、开启 layer IK pass，并挂带 `[ExecuteAlways]` 的临时 `OnAnimatorIK` 组件，把 `Left/Right Hand/Foot T/Q` editor curves 喂给 Unity IK。这个开关只用于研究。Pelica idle 复测显示，未触发 `OnAnimatorIK` 时会继续出现双手高举；加上 `[ExecuteAlways]` 后，诊断报告为 `callCount=63`、`appliedGoalCount=252`，截图中双手和手腕明显恢复到更接近 idle 的姿态。这证明 limb goal 曲线不能忽略，但当前仍使用隐式权重 1 和诊断 goal 空间解释，且缺原始 controller runtime 语义，所以默认不要开启，也不能把该输出当作生产动画。

`D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_SkipUntrustedLayers_IkGoal_Current` 进一步说明：在 recovered-controller `AnimationLayerMixerPlayable` 路径里，即使显式启用 `ANIMESTUDIO_UNITY_BAKE_ENABLE_IK_GOAL_DRIVER=1`，当前 `OnAnimatorIK` 也只记录 `callCount=6`、`appliedGoalCount=24`，明显少于单状态诊断的 `63/252`，视觉与无 IK 版基本一致，仍是失败样本。因此不能把“IK driver enabled=true”当作 IK 已求解；必须同时看 `ikGoalDriverCallCount`、`ikGoalDriverAppliedGoalCount`、采样帧覆盖和清晰截图。

`D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_LayerMixerIkEvaluate_Current` 修正了上述 layer mixer 诊断采样：IK goal 诊断开启时，worker 会在设置各层 clip time 后 `graph.Evaluate(0.0001f)`，并避免再用 `AnimationMode.SamplePlayableGraph` 覆盖 IK 结果。Pelica 复测的 `ikGoalDriverCallCount=189`、`ikGoalDriverAppliedGoalCount=756`，对应 63 个采样点 × 3 层 × 4 个 goal；截图中双手落到身体两侧，手腕不再明显扭曲。apply report 仍为 `writesReusableGltfTrsCandidate=false`，但 `animationSolve.productionStatus=diagnostic_limb_ik_goal_driver`，blocked reasons 为 `requires_clear_visual_validation` 和 `limb_ik_goal_driver_diagnostic_unverified`。这条路径证明 limb goal 诊断求解有效，但仍不是生产通过：目标空间、权重语义、更多动作类型和多角色 smoke 都还没验收。

同一路径现在可以不用环境变量复现。CLI 显式参数 `--unity_bake_enable_ik_goal_driver` 会写入 request 的 `enableEditorCurveIkGoalDriver=true`，Unity result 会回写 `requestEnableEditorCurveIkGoalDriver` 和 `effectiveEditorCurveIkGoalDriver`，apply report 会透传 `unityBakeRequestEnableEditorCurveIkGoalDriver` / `unityBakeEffectiveEditorCurveIkGoalDriver`。复测输出 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_RequestFlagSmoke_Current` 在不设置 `ANIMESTUDIO_UNITY_BAKE_ENABLE_IK_GOAL_DRIVER` 的情况下仍得到 `callCount=189`、`appliedGoalCount=756`。后续复现和批量 smoke 应优先使用 CLI 参数，环境变量只保留为旧诊断兼容入口。

`D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_SkipAmbiguousLayerBake_Current` 起，自动 fallback 会把同一 AnimatorController layer 解析到多个 additional clip 的上下文降级为 `additionalLayerContextWarnings`，并在 Unity worker 的 recovered-controller layer mixer 中真正跳过该层。Pelica 的 layer 6 `LoopClothAddLayer` 因需要 BlendTree 参数/权重语义被跳过，Unity result 只采样 `Main` 和 `LoopBodyAddLayer`；apply report 写 `productionStatus=needs_animator_controller_blend_tree_context`，blocked reasons 包含 `animator_controller_untrusted_layers_skipped`。这类输出可以避免旧样本双手抬高、手腕麻花的误导性结果，但它仍是诊断：被跳过层的 runtime BlendTree、参数、权重和 IK 语义没有完整恢复前，不能标成可复用 idle 动画。

如果需要判断被跳过层是否“有恢复价值”，可以显式加 `--unity_bake_sample_recoverable_skipped_layers_diagnostic`。该开关只用于诊断，会采样已经具备 recovered layer metadata、可解析 motion 和 skeleton mask 的 skipped layer，并在报告里写 `unityBakeRequestSampleRecoverableSkippedLayersDiagnostic`、`unityBakeAnimatorControllerDiagnosticSampledSkippedLayerCount` 和 `unityBakeAnimatorControllerDiagnosticSampledSkippedLayerNames`。只要采样过这类层，`UnityBakeResultApplier` 必须把结果阻断为 `productionStatus=diagnostic_recoverable_skipped_layer_sampling`、`writesReusableGltfTrsCandidate=false`，blocked reason 包含 `animator_controller_recoverable_skipped_layers_sampled_diagnostic`。Pelica 验证输出 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_RecoverableSkippedLayerSample_Current` 中，`LoopClothAddLayer` 被诊断采样后双手落回身体两侧，手腕不再明显扭曲，但它仍然不能计入生产 smoke；后续必须继续恢复 runtime layer 参数、BlendTree、IK goal 空间/权重和跨样本视觉验收。

`--bake_animation_previews_from_library` 批量入口也支持 `--unity_bake_sample_recoverable_skipped_layers_diagnostic`。批量报告会在 `items[*]` 提升 `sampleRecoverableSkippedLayersDiagnostic`、`animatorControllerDiagnosticSampledSkippedLayerCount` 和 `animatorControllerDiagnosticSampledSkippedLayerNames`，方便 smoke 阶段直接筛出“为了诊断主动采样了 normally skipped layer”的输出。Pelica 批量复测目录 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_BatchRecoverableSkippedLayerSample_Current` 里，item 显示 `LoopClothAddLayer` 采样数为 1、`productionStatus=diagnostic_recoverable_skipped_layer_sampling`、`writesReusableGltfTrsCandidate=false`。

显式开启 `--unity_bake_enable_ik_goal_driver` 时，CLI 会自动把 request 的 `probeMuscles` 也置为 true。原因是 Unity worker 的 IK goal driver 需要先读取 editor curves，才能拿到 `Left/Right Hand/Foot T/Q`。如果只写 `enableEditorCurveIkGoalDriver=true` 但没有曲线 probe，result 会变成 `effectiveEditorCurveIkGoalDriver=false`、`ikGoalDriverCallCount=0`，视觉可能退回双手抬高的失败状态。修复后同一批量 Pelica 样本得到 `effectiveEditorCurveIkGoalDriver=true`、`ikGoalDriverCallCount=189`、`ikGoalDriverAppliedGoalCount=756`；但这仍只是诊断生效，不是生产通过。批量 smoke 必须同时看 `productionStatus`、blocked reasons 和清晰截图。

批量 `unity_bake_batch_report.json.items[*]` 会把 apply report 里的 IK goal 诊断摘要提升为 `ikGoalDriverDiagnosticEnabled`、`ikGoalDriverCallCount`、`ikGoalDriverAppliedGoalCount`。这三个字段只用于 smoke triage：`callCount=0` 表示 IK pass/`OnAnimatorIK` 没真正进采样链路，`callCount>0` 只说明诊断驱动生效，仍必须继续看 `animationSolve.productionStatus`、blocked reasons 和清晰截图，不能单独作为生产通过。

`animationSolve.ikGoalDriverVerification` 现在会区分两套证据：`goals` 是逐 `layer + goal` 的目标距离，`finalGoals` 是同一帧同一 hand/foot 按最高 layer weight 选择的最终主导目标距离。`statusBasis=final_dominant_goal` 表示状态判断优先看 `finalGoals`；`maxLayerTargetSpread` 用来判断各层目标是否互相冲突。这个字段仍然只是证据：即使 `maxLayerTargetSpread=0`、手脚没有明显飞骨，只要 `productionStatus=diagnostic_limb_ik_goal_driver` 或 blocked reasons 里有 `limb_ik_goal_driver_diagnostic_unverified`，就不能计入生产 smoke。Pelica `run_loop` 验证目录 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_FinalGoalDiag_Current` 的结果说明当前问题更像 goal 空间、IK 权重/Unity 解算残差或目标点解释，而不是多层目标互相冲突。

`finalGoals[*].goalSpaceCandidates` 和根级 `goalSpaceCandidateSummary` 用来比较多种 IK goal 空间解释。当前会同时报告 `current_body_local_root_tr`、`root_transform_point`、`root_rotation_offset`、`body_position_root_rotation`、`body_rotation_root_origin`、`body_position_no_rotation`、`raw_world` 等候选到最终手脚骨骼的距离。它只回答“哪种空间解释更接近本次 Unity 采样结果”，不改变输出姿态，不代表动画通过。Pelica `run_loop` 的 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_GoalSpaceDiag_Current` 显示当前解释仍是全局最好，root/world 类候选明显更差；因此不能用简单 root/world 空间切换来修复 Endfield IK，下一步应继续查 IK 权重、目标点定义、TDOF 和 controller runtime 语义。

`goals[*].hasIkReadback`、`maxIkReadbackDistanceToGoal`、`min/maxIkReadbackPositionWeight`、`maxIkReadbackRotationWeight` 用来确认 Unity 是否真的接受了 `SetIKPosition/Rotation/Weight`。Pelica `run_loop` 的 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_IkReadbackDiag_Current` 显示 readback 距离为 0、position/rotation weight 都是 1，说明 IK pass 确实调用且 target/weight 没被 Unity 覆盖。若最终手脚骨仍和目标有距离，应继续查目标点定义、Humanoid/TDOF 约束、controller runtime IK 权重或视觉验收，而不是继续怀疑 `OnAnimatorIK` 没生效。

批量入口的 ImportedAvatar 自动匹配现在也会读取显式候选里的 `relationEvidence.avatarName`、`animatorControllerContext.avatarName` 以及 `candidate.*` 下的同名字段。这样 Endfield 的 sharedAvatarController 候选即使模型 raw_json 里没有直接写 Avatar asset，只要候选关系里保留了 Unity Avatar 名，且 `ImportedAvatarProbe*` 新鲜验证通过，就能自动命中 `Assets/AnimeStudioBake/ImportedAvatar/*.asset`。这个匹配仍然只做精确 key 命中，不使用模型名、角色名、目录名或 contains 模糊匹配；没有 fresh probe 时不能把 ImportedAvatar 当生产 oracle。

Pelica 批量自动 Avatar 验证目录为 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_BatchAutoAvatar_Current`。命令没有传 `--unity_avatar_asset`，batch report 仍写出 `unityAvatarAsset=Assets/AnimeStudioBake/ImportedAvatar/SK_actor_pelica_01Avatar.asset`、`avatarMatchKey=SK_actor_pelica_01Avatar`、`unityAnimationClip=Assets/AnimeStudioBake/ImportedAnimationClip/A_actor_pelica_idle_loop.anim`。glTF validator 无 error，`VisualFrames\full_mid_frame_24.png`、`upper_mid_frame_24.png`、`left_hand_mid_frame_24.png`、`right_hand_mid_frame_24.png` 显示双手在身体两侧，未复现旧截图的手腕麻花；但 apply report 仍是 `productionStatus=diagnostic_recoverable_skipped_layer_sampling`、`writesReusableGltfTrsCandidate=false`，不能计入 10 样本生产 smoke。

apply report 和 batch report 现在会提升 `animatorControllerRecoverableSkippedLayerSummary`。它把 normally skipped layer 里已经能从 recovered controller metadata 确定的内容汇总出来，例如 `recoverableSkippedLayerCount`、`simple1DDefaultSelectedLayerCount`、`blendParameterNames` 和每层的 `selectedChildMotionName`。Pelica 新复测目录 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_BatchAutoAvatarSummary_Current` 显示 `LoopClothAddLayer` 是 `Simple1D`，`IdleTailIndex=0` 选中 `A_actor_pelica_idle_loop_additive_cloth`。这个字段只用于 smoke triage 和后续生产化判断，不能把 `diagnostic_recoverable_skipped_layer_sampling` 升级成通过。

`animatorControllerParameterDiagnostics`、`animatorControllerBlendTreeDiagnostics` 和 `animatorControllerRecoverableSkippedLayerSummary.layers[*]` 现在会写 `defaultParameterSource` 与 `selectedChildSelectionRule`。`stateParameter` 表示默认值来自原始 AnimatorController inspect 的 state 参数；`blendEventFallbackZero` / `missingParameterFallbackZero` 表示只是为了让 recovered BlendTree 可采样而使用 0 值兜底。批量摘要里的 `fallbackZeroBlendParameterLayerCount` 大于 0 时，只能说明 controller 关系可继续研究，不能作为生产可播放动画通过证据。

`--recover_imported_animator_controllers` 现在会自动复用素材库已经记录的完整源索引。未显式传 `--source_index` 时，CLI 会从 `source_index_usage.json`、`animator_controller_context_refresh.json`、`sqlite_index_summary.json` 读取 `sourceIndex`，用源索引里的 `AnimationClip PathID -> m_Name` 补齐 controller recovery request，再精确匹配 Unity bake 工程现有 `Assets/AnimeStudioBake/ImportedAnimationClip/*.anim`。这能避免定向小库只包含主 clip 时把 recovered controller 退化成单层；但没有找到或没有导入的 clip 仍按 `missingClipCount` 明确报告，不能伪造或按名字模糊补关系。Pelica 验证输出 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_AutoSourceIndexControllerRecovery_Current` 仍保持 `productionStatus=diagnostic_recoverable_skipped_layer_sampling`、`writesReusableGltfTrsCandidate=false`。

不带 `--run_unity_bake` 时，`--recover_imported_animator_controllers` 可以在没有 Unity bake 工程的环境下只写 recovery request 和诊断报告。这个模式用于审计 AnimatorController 参数默认值、clip 缺失、layer/blend tree 结构和确定性来源；它不会调用 Unity，不会生成 `.controller`，也不会把 recovered controller 写入浏览器设置。报告的 `results[*].requestDiagnostics` 会提升 `parameterCount`、`controllerDefaultParameterCount`、`clipCount`、`missingClipCount` 和 `layerCount`，方便快速判断 request 输入证据是否齐全。只有显式传 `--run_unity_bake` 且配置有效 Unity bake 工程时，才会执行 Unity 恢复并在成功后更新可复用映射。

定向导出 AnimatorController 上下文时，可以显式加 `--include_animator_controller_clip_closure`。该开关只在 `--path_ids` 精确选择 AnimationClip 的诊断/刷新导出中生效，会通过 `unity_source_index.db` 的 `source_relations relation='animatorController.clip'` 先找引用当前 clip 的 AnimatorController，再把同一个 `AnimatorController.m_AnimationClips` 引用到的全部 AnimationClip PathID 加入本次导出。它用于补齐 `--recover_imported_animator_controllers` 所需的 ImportedAnimationClip 闭包，不新增模型-动画推荐关系，也不能替代原始 controller runtime 语义和视觉验收。Pelica 验证命令用 `A_actor_pelica_idle_loop pathId=3251858251387051210` 扩展出 215 个同 controller/baseLayer 相关 clip，并在 `D:\Assets\fangzhou\Endfield_PelicaControllerClipClosureExport_Current` 导出 216 个 `.anim`；这证明此前 request 里 118 个 missing clip 主要是定向库没有导出 controller clip 闭包，而不是源数据缺失。

`--recover_imported_animator_controllers` 可配合 `--animator_controller_clip_library <LibraryRoot>` 读取额外动画闭包库的 `assets.kind=Animation` 和 `.as_browser_cache/unity_bake_settings.json`。推荐顺序是：先用 `--include_animator_controller_clip_closure` 导出闭包库；再对闭包库运行 `--recover_imported_animation_clips ... --unity_project <WorkerProject>`，把这些 `.anim` 放进 Unity worker 的 `Assets/AnimeStudioBake/ImportedAnimationClip/`；最后对模型库运行 `--recover_imported_animator_controllers ... --animator_controller_clip_library <ClosureLibrary>`。Pelica 当前验证：`Endfield_PelicaControllerClipClosureExport_Current` 里 216 个 `.anim` 全部恢复到 `D:\misutime\AnimeStudioUnityBakeWorker` 后，`AC_npc_human_girl_pelica_optNew` request 变为 `clipCount=119`、`libraryAssetAvailableCount=119`、`missingLibraryAssetCount=0`、`missingImportedClipCount=0`。这只证明 controller request 的 clip 依赖闭包已齐，不证明 Unity controller asset 已生成，也不证明 idle 动画视觉正确。

同日继续复查 Pelica recovered controller 后确认，`A_actor_pelica_idle_loop`、`A_actor_pelica_idle_loop_additive`、`A_actor_pelica_idle_loop_additive_cloth` 虽然来自确定性 controller/Avatar 关系，且 Unity `AnimationUtility` 能看到动态 editor curves，但 Unity runtime HumanPose 没有帧间变化。这类结果应视为 `static_pose` / `editor_curves_not_driving_runtime_pose` / `needs_controller_runtime_context`，不能因为名字含 `idle_loop`、曲线存在或 glTF validator 无 error 就当作完整 idle 动作验收。`--unity_probe_muscles` 和 `--unity_bake_rebuild_editor_curve_clip` 现在也会透传到 `--bake_animation_previews_from_library` 批量命令，便于单样本确认“editor curves 是否真的驱动 runtime Humanoid pose”；这些开关仍是诊断用途，不能替代最终 rest/start/mid/end 视觉验收。

`requiresHumanoidBake=true` 的候选还必须通过 Unity 的 `AnimationClip.isHumanMotion` 检查。即使模型已经有可信 `unityAssetPaths.avatarAsset`，如果导入到 bake 工程里的 `.anim` 被 Unity 报告为 `isHumanMotion=false`，PlayableGraph 只会采样普通 Transform 曲线，Humanoid/Muscle 身体主动作不会被真实驱动。这种结果会被直接标为 `failed`，不会再写出或打开 baked glTF；需要回到原始 Unity AnimationClip asset 恢复/引用链路继续修，而不能把半跪、入地、静态默认姿态当作“需人工验收”产物。

Endminf 作为第二个人形回归样本验证了同一套门禁不能只服务 Pelica。`D:\Assets\fangzhou\Endfield_EndminfActor_IdleAdditive_IkRequestFlagSmoke_Current` 使用 `--unity_bake_enable_ik_goal_driver` 和默认安全门禁生成 `transform_only` 诊断：Unity result 为 `isHumanMotion=false`、`ikGoalDriverCallCount=63`、`ikGoalDriverAppliedGoalCount=0`，apply report 阻断为 `needs_original_animator_controller_context`，blocked reasons 包含 `unity_clip_is_not_human_motion`、`transform_only_clip_filter`、`generated_animator_controller_context`。同一输入显式 `--unity_bake_clip_filter_mode full` 的 `D:\Assets\fangzhou\Endfield_EndminfActor_IdleAdditive_IkFullClipSmoke_Current` 回到 `isHumanMotion=true`、`ikGoalDriverAppliedGoalCount=252`，但仍是 generated single-state controller，核心身体变化只有 5 条，截图显示双臂展开，像 additive/controller 上层姿态单独套在模型上。结论：Endminf 证明 IK goal 诊断可跨第二角色触发，但 additive clip 即使不拉丝、不飞骨，也不能作为完整 idle/生产动画通过；必须恢复原始 controller state/layer/blend/weight 上下文或直接 solver 等价语义。

`D:\Assets\fangzhou\Endfield_EndminfActor_IdleAdditive_ProvidedControllerIk_Current` 进一步确认：`--recover_imported_animator_controllers` 已能从 Endfield VFS inspect 恢复 `AC_npc_human_girl_endminf_optNew.controller`，并让 bake 进入 `animatorControllerSamplingMode=provided_runtime_controller`；IK 诊断也实际触发 `ikGoalDriverCallCount=126`、`ikGoalDriverAppliedGoalCount=504`。但 recovery report 只成功挂载 1 个 clip，缺失 31 个 clip，并跳过多个复杂 BlendTree/空 motion 层；apply report 仍是 `needs_review`，`animationSolve.productionStatus=diagnostic_limb_ik_goal_driver`，`writesReusableGltfTrsCandidate=false`。清晰 full/upper 截帧几乎没有足够核心动作变化，双臂仍保持外展，说明“有原始 controller asset”不等于完整恢复 controller runtime 语义；缺失 clip、BlendTree 参数、层权重和 IK goal 空间/权重仍必须继续求解。

AnimatorController 多层状态要区分“身体主动作”和“角色专属辅助层”。例如原神角色 controller 里同一个 state 可能同时引用通用 Humanoid 身体 clip 和角色发型、服装、武器等辅助 clip；后者单独 bake 会出现半身入地、动作语义不符或 pose-only。生产索引只有在源索引从同一个 AnimatorController state / blend tree node 明确解析出 `animatorControllerContext.baseLayerClip.clip.pathId` 时，才允许把用户选中的辅助 clip 切到同状态的身体主 clip 生成 Unity bake request。这个切换仍然来自 Unity 状态机结构，不按动画名、骨骼数量或目录猜测；没有 `baseLayerClip` 的候选继续显示“需 AnimatorController 上下文”，不能进入主线 bake。

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

`--apply_unity_bake_result` 同样会先检查源模型 glTF 的 Mesh、UV、材质、贴图、skin 和 bbox。模型阶段未通过时，`unity_bake_apply_report.json` 会写 `status=blocked`、`reason=model_not_animation_ready` 和 `modelGate.reasons`，并停止生成 baked glTF；旧 Unity bake 结果只能作为诊断线索，不能绕过模型第一阶段验收。

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
- `animation_bindings.jsonl` 和 `model_animations.json` 目前是候选索引，不等于最终可播放验证结果；默认推荐候选只能来自 Unity 显式关系，例如 Animator、Animation、AnimatorController、AnimatorOverrideController、PPtr 和源索引依赖图。结构兼容、骨骼路径、Avatar 兼容、路径/名称语义只能作为诊断、过滤或显式人工预览依据，不能进入默认 `model_animations.json` / SQLite 推荐候选。
- 候选索引会记录 `relationSource`、`confidence`、`score`、`matchedBindingPaths`、`unmatchedBindingPaths`、`requiresHumanoidBake`。`relationSource=explicit` 才表示可进入默认 Unity 动画候选；`confidence=explicit_unity_reference` 只能说明这条显式关系的质量，不能单独把旧 fallback/diagnostic 候选升级成默认候选。`structural_unity_binding` / `structural_unity_avatar` 等结构兼容结果只能保留为诊断或人工强制预览线索。
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
- `materialMissingRendererBinding`：缺 Renderer 材质绑定、需要材质修补或后续关系索引增强。该字段会合并 `model_validation.json` 的 primitive 材质覆盖结果，不只看 glTF 材质 extras。
- `materialMissingRendererPrimitiveCount` / `materialMissingRendererPrimitives`：缺材质 primitive 数量和名称样本，方便浏览器或批处理直接筛掉灰模/缺绑定模型。
- `materialHasBaseColorTexture` / `materialHasNormalTexture` / `materialImageCount`：判断模型是否已有标准贴图显示能力。`materialImageCount` 会优先同步验证后的 glTF image 数量。
- `modelValidationStatus` / `modelBodyStatus`：从 `model_validation.json` 回写到 catalog 的模型验收状态，SQLite `assets.validation_status` 也会同步该值。

AnimationClip 条目会记录 `animationType`、`hasMuscleClip`、`coreTransformBindingCount`、`humanoidBindingCount`、`blendShapeBindingCount`、`trueBlendShapeBindingCount`、`rendererMaterialBindingCount`、`rendererPropertyBindingCount`、`activeStateBindingCount`、`auxiliaryBindingCount` 和 `classificationNotes`。这些字段用于判断动画是普通骨骼 TRS 曲线、Humanoid/Muscle 动画、真正的 BlendShape 动画、材质/Renderer/显隐动画，还是只作用在 socket/helper 上的辅助动画。`blendShapeBindingCount` 是兼容旧索引的粗略 SkinnedMeshRenderer 计数，默认能力判断应优先看 `trueBlendShapeBindingCount`。

`asset_summary.json` 是总览报告，记录模型、动画、实验 shader 的数量和模型是否带贴图、骨骼、morph。`model_validation.json` 是静态模型验收报告，检查 glTF image、texture、material、mesh accessor、skin joint、inverseBindMatrices 是否自洽。`animation_bindings.jsonl` 从动画视角列出候选模型，`model_animations.json` 从模型视角列出候选动画、匹配依据、匹配分数和下一步动作。`model_animations.compact.json` 是规范化紧凑索引：模型、动画、模型-动画候选引用分表保存，避免把完整动画对象重复写进每个模型。`character_assemblies.json` 记录模块化角色的 body/face/hair/accessory 候选和推荐组合；它只描述可组装关系，不改变默认 Library 中的原始模块输出。后续素材库浏览、搜索、直接 glTF 动画验证、UI、模块化预览和性能优化应优先读取 compact/assembly 索引；verbose `model_animations.json` 保留给人工排查和兼容旧命令。

`model_animations.json` 和 `model_animations.compact.json` 都会记录 `animationCapability`。它只表示下一步安全处理路径，不等于动画已经视觉验证通过。候选里的 `requiresUnityBake=true` 是旧库兼容字段，只能说明历史流程可能生成外部 Unity bake 请求，不能作为生产可播放证明；新素材库应改用 `directGltfAnimationStatus`、`directTrsAnimationReady`、`directWeightsAnimationReady`、`needsDirectTrsAnimation` 和 `deprecatedUnityBakeOnly` 判断是否能直接交付 glTF TRS / weights。`fullHumanoidBakeRequired=true` 只表示完整 Humanoid/Muscle 直接 TRS 结果尚未补齐，若同时有 `fullHumanoidBakeBlocked=true`，说明当前库还缺 HumanBone 映射或参考姿态，不能把它当成可复用动画：

- `HumanoidBodyNeedsUnityBake` / `HumanoidBodyNeedsInternalSolver`：旧索引用来表示 Humanoid/Muscle 身体动画尚不能直接写出目标骨架 TRS。它们现在只能进入诊断、迁移或内部求解器实验队列，不能作为默认可验收结果。
- `TransformBodyPreviewReady`：普通 Transform 身体动画，或没有 Humanoid `humanBones` 但 AnimationClip 直接包含骨骼 Transform binding 的 Generic 角色动画，可以进入直接预览/打包流程。
- `DirectGltfTransformOnly`：部分 Humanoid/Muscle clip 同时带有确定性的 Transform TRS 曲线。若模型缺完整 Avatar HumanBone 映射或参考姿态，索引会把这类显式关系标成 `partialDirectGltf=true`、`fullHumanoidBakeRequired=true`、`fullHumanoidBakeBlocked=true`，允许先生成只包含 Transform 曲线的 glTF 快速预览；它不是完整 Humanoid 直接 TRS 结果，不能替代后续生产验收。
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
- Humanoid/Muscle 诊断输出如果出现 `unityBakeTrackSource=editor_curve_set_human_pose_diagnostic`，必须查看 `unityBakeEditorCurveHumanPoseDiagnostic`。其中 `dynamicLimbGoalCurveCount > 0` 表示仍有四肢 IK goal 曲线未求解；即使 glTF 有 TRS channel、能打开、手腕不再扭曲，也不能直接当作生产可用动画。
- `--apply_unity_bake_result` 写出的预览目录会生成 `ANIMATION_PREVIEW_STATUS.md`，glTF `asset.extras.animeStudioAnimationPreview` 也会写入同一套轻量状态。浏览器和人工检查应优先看这里的 `productionReady`、`writesReusableGltfTrsCandidate`、`diagnosticOnly`、`productionStatus` 和 `blockedReasons`。例如 `generated_single_state_controller` / `generated_multi_layer_controller` 产物即使能播放，也会标为 `diagnosticOnly=true`、`needs_original_animator_controller_context`，不能作为 Endfield/Pelica 这类人形动画的生产通过样本。
- 旧 `AnimeStudio.LibraryBrowser`、批量 bake 续跑和 SQLite 摘要也必须读取 `unity_bake_apply_report.json.animationSolve`。只要 `writesReusableGltfTrsCandidate=false`、`productionReady=false` 或 `requiresVisualValidation=true`，就不能把 baked glTF 计入 trusted bake；这条规则优先于旧的 `status=ok/warning + frameVaryingTracks + Avatar/Clip 可信` 快速判断。
- `directTwoBoneIk*` 是 IK goal 几何可达性诊断，不再写入最终 glTF TRS 采样。`maxFinalIkBoneMoveDistance` / `maxFinalIkDistanceImprovement` 表示 Unity/PlayableGraph 原始 IK pass 后的手脚骨移动；如果这些值接近 0，而 `directTwoBoneIk*` 有变化，只能说明诊断求解器能尝试追目标，不能说明 Unity 原始动画已经正确恢复。
- `--inspect_unity_files` 会在 AnimatorController state 里输出 `transitions.conditions`，源索引的 `animatorController.clip` 关系也会带 `stateTransitions`。AnimatorController BlendTree node 还会输出 `directChildBlendEvents`、`directChildPoseTimeEvents`、`sequenceChildBlendEvents`、`sequenceChildPoseTimeEvents` 等 direct/sequence 参数证据。`controllerValueDefaults.values[]` 会写 `typeName`、`resolvedDefaultKind`、`resolvedDefaultValue` 和 `resolvedDefaultSource`，用于说明 `m_Values/m_DefaultValues` 中明确可解析的 controller 参数默认值。源索引还会写 `animatorController.blendTreeParameter` 诊断关系，专门记录没有直接 clip 的 BlendTree/Sequence 参数节点。它们用于追查 controller 参数、transition gate、layer/IK/BlendTree 权重是否有 Unity 序列化证据；这些字段不能单独改变采样权重、建立模型-动画绑定或解除生产门禁。
- Endfield 定向 inspect 某个 `.blc` 时，如果只想看一个内部 bundle，必须同时传 `--endfield_vfs_files` 过滤内部 UnityFS 文件。例如 Pelica controller 复测使用 `--endfield_vfs_files "Data/Bundles/Windows/main/0a280e65d9d8c1f16f74fdc0\.ab$"`。只给 VFS 分组目录或只给 `.blc` 而不限制内部 bundle，会展开大量 VFS 内容，可能生成数 GB 诊断源索引。
