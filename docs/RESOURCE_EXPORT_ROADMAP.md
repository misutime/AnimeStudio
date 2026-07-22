# AnimeStudio 资源导出工具开发规划

本文件是路线图和待办记录，不是强制标准。资源导出的唯一强制标准是 [PROJECT_EXPORT_STANDARDS.md](PROJECT_EXPORT_STANDARDS.md)；单游戏特殊适配看 [game-profiles/README.md](game-profiles/README.md)。本文出现的具体游戏、命令和输出目录只代表阶段性样本或历史任务，不能覆盖通用标准。

## 目标定位

本工具的核心目标是面向 PC 端 Unity 游戏，把已经打包过的资源尽可能还原成开发期可使用、可查看、可二次整理的素材库。

优先资源类型：

- 3D 模型：静态模型、角色模型、场景模型、道具、球、NPC。
- 骨骼与蒙皮：SkinnedMeshRenderer、骨架层级、bind pose、权重。
- 贴图：Texture2D，支持 Raw 快速导出、按需 PNG 后处理。
- 材质：Material 参数、贴图槽、shader 引用和可分析 metadata。
- Shader：实验功能，以研究和重建材质为目标，先保证可转储、可定位、可关联；不进入默认核心导出。

非目标或低优先级：

- 完整还原 Unity 工程。
- 还原运行时代码逻辑、MonoBehaviour 行为。
- 规避加密、绕过保护或破解私有协议。
- 无脑导出所有零散资源且不分类、不标注。默认 Library 应尽量完整，但必须保持目录分类、标签和报告清晰。
- 默认导出、转换、绑定、预览或验收动画。AnimationClip、AnimatorController、Timeline、材质动画、摄像机动画和 VFX 动画只作为 out-of-scope 诊断线索。

验收取向：

- 默认 `Library` 是“完整可浏览模型素材库”，优先把可能有用的模型、贴图、材质、骨骼/skin、索引和报告拿出来。
- 模型、骨骼/skin、贴图、材质这些核心资产不能系统性缺失。
- 输出目录能像素材库一样浏览，单个模型目录可以直接查看模型、相关贴图、材质报告和使用说明。
- 只过滤非常确定的垃圾或损坏对象；不确定对象优先分类、打标签、写报告。
- 精品筛选作为后处理能力推进，后续通过 `Core` / `Curated` / `Playable` / `ProductionReady` 等层从完整素材库里筛出高质量子集。
- 保持准确前提下优先提升批量导出速度和内存稳定性。

基础准则：

> 后续每一步实现都以“导出完整可浏览模型素材库”为准。默认输出必须优先保留可能有用的模型、贴图、材质和骨骼/skin，并通过分类、标签、报告、索引和后续筛选控制质量；特殊研究、调试、旧式内嵌动画、Raw 快速扫描等行为必须通过显式参数开启。

Unity 关系解构准则：

> 后续每一步实现都必须使用 Unity 自身序列化出来的通用引用和结构来还原素材库，而不是围绕某个游戏、某个目录、某个角色名写特殊适配。默认逻辑如无不得已的明确理由，绝不自行推断关系；游戏名、目录名、资源名前缀只能作为显式 fallback，不能替代 Unity 关系图。

CAB / PPtr 依赖索引策略：

> CAB map 是一次完整构建、长期复用的 Unity 外部引用索引，不应为某次导出做瘦身版。导出候选可以裁剪，但依赖图必须来自完整输入源目录。新版 CAB map 记录来源文件数量和完整输入集合 fingerprint；只有源文件变化、map 格式升级或显式复用了错误 map 时才自动重建。这样后续新增材质、贴图、shader 或其他素材导出逻辑时，不会因为旧的裁剪 map 切断 Unity 外部引用。

材质和 shader 解构准则：

> 材质还原必须来自 Unity `Material.m_SavedProperties`、Renderer 材质槽、Texture PPtr、shader 引用和渲染状态。默认 glTF 导出应把 Unity 可标准表达的部分转成 glTF PBR、`alphaMode`、`alphaCutoff`、`doubleSided` 和标准贴图引用；不能被 glTF 标准表达的自定义 shader slot、float、color、scale/offset 必须保留到材质 JSON 和 `materials[].extras.unityMaterial`。后续 face/eye、mask、ramp、toon、skin、sweat 等效果应基于这些 Unity 原始属性做通用模板或烘焙，而不是按单个游戏目录或角色名硬编码。

通用关系来源包括：

- `GameObject` / prefab 层级。
- `Animator` 组件挂载关系。
- `Animation` legacy 组件挂载关系。
- `AnimatorController` / `AnimatorOverrideController` 对 `AnimationClip` 的引用。
- `Animator.Avatar`、`Avatar`、`HumanDescription`、骨架层级和 human bone 映射。
- `SkinnedMeshRenderer` 的 bones、bind pose、blendshape/morph channel。
- `AnimationClip` binding：path、type/classID、attribute/property、customType。
- Humanoid/Muscle 曲线、Transform TRS 曲线、BlendShape 曲线、object reference 曲线。
- AssetBundle / SerializedFile / PPtr 依赖关系。

关系优先级：

1. Unity 显式引用：Animator/Animation 组件、Controller、OverrideController、PPtr 依赖。
2. Unity 结构兼容：Avatar/human bone、skeleton hash、bone path、clip binding path、blendshape channel。
3. 实际验证：预览/打包后 glTF channel、skin/joint、主体骨骼覆盖、bbox。
4. 显式 fallback：container、目录名、角色名、资源分类、游戏 profile。fallback 必须明确标注，不能混入默认绑定结果。

任何针对单个游戏的规则都必须满足：

- 默认关闭或放入 profile/config。
- 有通用关系图无法表达的明确理由。
- 不影响 `Normal` 通用 Unity 导出路径。
- 文档中标明它是游戏 profile 规则，不是核心导出逻辑。

模型和动画边界：

> 模型默认保持干净，动画默认不入库、不绑定、不预览、不验收。只有显式诊断/旧库迁移命令可以处理动画，而且输出必须标注为诊断，不改变默认 AssetLibrary 能力。

原因：

- 动画会显著放大默认导出复杂度和验收成本，当前目标是先打通模型、贴图、材质、骨骼/skin、AssetLibrary v1 索引和报告闭环。
- Unity 游戏常把角色模型、附件、材质、贴图和动画拆在不同 bundle；默认模型导出只保证模型依赖闭包，不为动画建立生产关系。
- 同名/同类动画不一定兼容，旧动画诊断资料只能作为研究背景，不能进入默认推荐关系。
- 默认素材库的首要价值是“能浏览和筛选模型资产”，不是把运行时动作关系一次性塞进单文件。

团队统一工作流：

1. 默认 `Library` 导出 prefab/Animator/GameObject 组合模型：mesh、material、PNG texture、skeleton/skin。
2. 遇到 AnimationClip、AnimatorController、Timeline 等动画资源时默认跳过，并记录 `animation_out_of_scope.json` 或源索引诊断计数。
3. 生成并检查静态模型验收报告：模型、贴图、材质、skin/joint 基础结构必须自洽。
4. 自动生成 Unity 关系图：模型、组件、Renderer、Material、Texture、Avatar、PPtr 依赖。
5. 写出 AssetLibrary v1：`asset_library.json`、`library_index.db`、材质 sidecar、验证报告和人读 README。

默认素材库形态：

- 模型默认 glTF，进入 `Models`。
- `Models` 默认放完整 prefab、Animator、GameObject 组合模型，以及有明确 Unity 关系或静态素材语义的独立 Mesh；raw/source parts 只进索引或 `RawUnreferenced`。
- raw fbx 身体、face、附件等 source parts 默认不作为可浏览模型导出；它们必须写入 `asset_catalog.jsonl`，标记为 `ModelSourcePart`、`RawModel`、`SourcePart`、`AttachmentSource` 或类似角色。
- 如果 raw fbx 没有被任何组合模型覆盖，但它本身是可用模型，则进入 `Models/RawUnreferenced`。
- 需要研究零散 fbx/source parts 时，显式使用 `--model_source PrefabAndParts` 或 `RawPartsOnly`。
- 角色/蒙皮模型默认保留 skeleton/skin，不能为了模型浏览速度丢掉骨骼。
- 贴图默认 PNG，进入共享 `Textures/_ModelDependencies`，模型目录通过硬链接引用。
- 动画默认不进入 `Animations`，也不嵌入每个模型。
- Shader 默认不导出；实验研究时显式 `--include_shaders`，进入 `Shaders`，以安全 raw archive + metadata 形式保留。
- 材质、贴图、骨骼/skin、源路径关系由 manifest/catalog 记录；动画只记录 out-of-scope 边界或源索引诊断。
- 默认 Library 不再以过早 Core 过滤作为覆盖策略。UI、video、camera、manager、明确空对象、collider、navmesh、socket、joint、bone、损坏对象等确定垃圾可以过滤；effect、decal、helper、terrain tile、building part、VFX mesh 等不确定对象优先分类、标注或写报告。

## 当前完成度

总体判断：当前工具已经进入“可用于 PC Unity 3D 游戏批量资源提取”的阶段，但还没有达到“高可信开发素材库还原器”的完整状态。

粗略完成度：

| 模块 | 当前状态 | 完成度 |
| --- | --- | --- |
| Unity 文件读取/依赖解析 | 已可加载 UnityFS/SerializedFile，支持 CAB 依赖 map，Freedunk 1 字节前导 UnityFS 已兼容 | 75% |
| 3D 模型导出 | 支持 `SplitObjects`、`Animator`，默认 glTF，可导出 mesh、skin、基础层级 | 70% |
| glTF 输出 | 已有 `.gltf + .bin`、`.glb`、材质贴图引用、skin；默认不写动画 channel | 60% |
| FBX 输出 | 继承原有 FBX exporter；新增 Unity bake 后的 Blender FBX 后端，可把 baked glTF 打包为模型+骨骼+动作的成熟 FBX 验收资产 | 78% |
| 贴图导出 | PNG 默认、Raw 可选、按模型后处理、硬链接省空间 | 80% |
| 材质导出 | 能导出材质 JSON、基础 PBR 映射、glTF alpha/double-sided 状态、extras 保留 Unity slot/float/color 信息 | 62% |
| 动画导出 | 已降级为显式诊断/旧库迁移能力；默认 Library 不导出 AnimationClip、不写模型-动画候选、不声明动画能力 | 非默认目标 |
| Shader 导出 | 实验功能；显式 `--include_shaders` 时安全归档 raw + metadata，避免 native 反汇编崩溃；反编译仍需单独实验模式 | 45% |
| 覆盖与分类 | 已从过早 Core 过滤转向默认完整可浏览 Library；StaticMeshPrimary、Renderer/Material 关系绑定和报告标注仍需继续增强 | 70% |
| 性能与诊断 | 有 profile jsonl、manifest、阶段耗时、缓存、批处理、GC 策略 | 70% |
| 输出可浏览性 | 默认 `PrefabPrimary`，`Models` 放组合模型和有意义的 StaticMeshPrimary，raw fbx/source parts 进入索引或 `RawUnreferenced`，模型依赖贴图集中共享并硬链接到模型目录；`skeletons.json` 从骨架视角聚合可浏览模型，不承诺动画候选 | 78% |

## 已有关键能力

### 默认完整 Library 与后处理筛选

默认 Library 的目标是完整可浏览模型素材库，不是最终精品包。工具应优先保留可能有用的模型、贴图、材质、骨骼/skin 和来源关系；动画、音频等非模型资产不进入默认 Library，只保留必要的源索引或 out-of-scope 诊断线索。

底层导出阶段可以确定过滤：

- collider、navmesh、occlusion。
- socket、joint、bone、纯空挂点。
- 明确损坏、不可解析或没有几何/贴图/动画价值的对象。
- 明确 obsolete/deprecated 路径下的资源。

不确定对象优先保留并分类：

- debug box、基础体、decal、helper、VFX mesh、terrain tile、building part、LOD/部件级模型。
- 这类资源必须通过 `asset_catalog.jsonl`、`ASSET_README.md`、`MATERIAL_REPORT.md`、分类目录和后续报告说明可信度、材质绑定状态和使用注意事项。

后续再基于完整素材库做精品筛选：

### VFX / ParticleSystem

VFX 进入默认素材库，但分阶段还原：

1. 已实现/当前目标：`VFX/` 元数据索引，记录 Unity `ParticleSystem`、`ParticleSystemRenderer`、`LineRenderer`、`TrailRenderer`、`VisualEffect`、GPU Particle/VFX 对象和 mesh 型特效线索，并从 TypeTree 轻量提取 ParticleSystem/Renderer 的 `previewHints`。源索引同时记录 `ParticleSystemRenderer -> Material -> Texture` 和直接 VFX texture PPtr，输出为 `textureRefCount` / `textures`，用于区分材质贴图驱动的 billboard、trail、mesh particle 和 ground-plane 特效。
2. 输出形态：每个特效目录包含 `vfx.json` 和 `VFX_REPORT.md`，全局包含 `VFX/vfx_library.json` 和 `VFX/VFX_LIBRARY.md`；mesh 型特效仍以 glTF 模型导出并标注 `resourceKind=VFX`。
3. 暂不伪装：shader UV 动画、Texture Sheet 图集切帧、VFX Graph、动画事件触发、prefab 运行时绑定没有完整还原时，只记录为 metadata/diagnostic。
4. Browser 预览：`AnimeStudio.LibraryBrowser` 应提供 VFX 类型筛选、缩略图、限制说明和近似动态预览。mesh 型特效优先使用真实 glTF 缩略图；纯 ParticleSystem/VisualEffect 在 runtime 数据未烘焙前，应优先基于 `previewHints`，再结合 Unity 元数据、名称、分类、组件、材质、贴图和 Mesh 引用生成 approximate 预览，至少区分 trail、shockwave、aura、smoke、projectile、beam、distortion、mesh particle、ground plane、stretch billboard、textured billboard 等形态，不能所有 VFX 共用一个小圆点占位模板。
5. 后续增强：实现材质贴图/Texture Sheet atlas 采样、shader/material 参数近似、动画事件到 VFX prefab 的关系索引，并逐步从 approximate 预览升级为更接近 Unity 行为的 runtime 预览。

VFX 索引字段扩展时，旧素材库可继续浏览，但预览质量会受限。验证 VFX 功能时建议重建 `unity_source_index.db` 并重新生成 `VFX/` catalog；如果要让 `asset_catalog.jsonl`、`vfx.json`、缩略图缓存和报告完全一致，推荐重新执行一次 Library 导出。

- `Core`：高概率游戏核心素材。
- `Curated`：人工或规则确认过的精选素材。
- `Playable`：模型、骨骼、材质、动画已验证可播放。
- `ProductionReady`：材质、动画、模块组装和报告都达到可直接复用标准。

### 默认 glTF + PNG 素材库工作流

默认模型格式：

```powershell
--model_format Gltf
```

默认贴图模式：

```powershell
--texture_mode Png
```

PNG 模式会把可查看贴图保存为共享实体，并在模型目录建立硬链接：

```text
Textures/_ModelDependencies/*.png
Models/.../<Model>/Textures/*.png
```

Raw 模式仍作为特殊高速扫描路径，会把 Unity 原始贴图数据保存为：

```text
Textures/_ModelDependencies/*.rawtex
Textures/_ModelDependencies/*.rawtex.json
```

`.rawtex.json` 已记录：

- Unity TextureFormat
- 宽高
- mipCount
- source asset path
- pathId
- Unity version
- platform
- raw data size

这使得全量导出时不必把所有贴图转 PNG，能显著降低导出耗时。

### Freedunk 失败案例：不要把全局动作库嵌进每个模型

`D:\Assets\Freedunk_Data_core_png_anim` 使用了：

```powershell
--mode Animator
--texture_mode Png
--fbx_animation Auto
```

结果 `assets\ingame\prefabs\characters\Bill_01_00\Bill_01_00.gltf` 变成了“角色 prefab + 脸部/辅助节点 + 上千个通用篮球动作”的混合包。这个文件不是干净角色素材，打开时容易显得混乱，文件体积也明显膨胀。

修正后的默认策略：

- 默认 `--mode Library`。
- 默认 `--animation_package Skip`。
- 模型 glTF 不嵌入 Animator Controller 的全局动作库。
- AnimationClip 默认不写入 `Animations`，只记录 out-of-scope 边界或源索引诊断。
- 后续如果研究动画，只能走显式诊断/旧库迁移命令，不能改变默认 AssetLibrary 能力。

### 按模型单独转换贴图

已支持不依赖原游戏目录的后处理命令：

```powershell
AnimeStudio.CLI.exe `
  --convert_model_textures "D:\Assets\Freedunk_Data_Dev\Freedunk_Data_library\Models\assets\ingame\prefabs\characters\Qiqi_03_00\Qiqi_03_00.gltf"
```

默认行为：

- 读取 glTF `materials[].extras.unityTextures`。
- 自动向上查找 `Textures/_ModelDependencies`。
- 只转换当前模型用到的 `.rawtex`。
- 默认输出 PNG 到模型目录的 `Textures` 子目录。
- 默认更新 glTF 的标准 `images/textures` 引用。

### PNG 硬链接节省空间

`--texture_mode Png` 下：

- PNG 实体写入顶层共享目录 `Textures/_ModelDependencies`。
- 模型目录 `Textures` 下创建硬链接。
- glTF 引用模型本地 `Textures/*.png`。
- 硬链接失败时退回普通复制。

这能兼顾模型目录可浏览性和磁盘空间。

### 性能日志、manifest 和 catalog

默认输出：

```text
export_manifest.jsonl
asset_catalog.jsonl
export_profile.jsonl
```

`asset_catalog.jsonl` 是素材库索引。模型条目记录 resourceKind、mesh/vertex/material/texture/bone 数、skeletonHash 和 skeleton 多维指纹；动画不再作为默认独立资产登记，实验 shader 只在 `--include_shaders` 时登记。当前 skeleton 指纹包含 `namePathHash`、`hierarchyHash`、`bindPoseHash`、`avatarHumanHash`、`avatarSkeletonNameHash` 和 `relationBasis`，优先使用 Unity Avatar/HumanDescription，其次使用骨骼路径、层级和 bindpose。

默认还会生成：

```text
asset_summary.json
animation_out_of_scope.json
skeletons.json
unity_relations.jsonl
unity_relation_summary.json
model_validation.json
```

`asset_summary.json` 汇总导出数量、资源分类和模型是否带骨骼/贴图/morph。`model_validation.json` 检查 glTF image、texture、material、mesh accessor、skin joint、inverseBindMatrices 是否自洽。`unity_relations.jsonl` 记录 GameObject、组件、Animator、Avatar、SkinnedMeshRenderer、MeshFilter、Renderer/Material 等 Unity 原生关系，`unity_relation_summary.json` 是轻量摘要。`animation_out_of_scope.json` 说明动画默认跳过，不生成正式动画资产或模型-动画候选。`skeletons.json` 为每个素材库骨架聚合可浏览模型；动画 FBX 等无 mesh 的源骨架不能混入 `models` 浏览列表。

`export_profile.jsonl` 已记录：

- 批次加载
- 依赖 map 加载/构建
- asset data 构建
- model_convert
- model_mesh
- model_skin
- model_material
- model_texture / model_texture_raw
- model_write
- model_gc
- clear_batch

后续慢点分析应优先基于 profile log，而不是凭命令行输出猜。

## 主要缺口

### 1. glTF blendshape/morph target 缺失

当前 `ModelConverter` 已收集 morph/blendshape 数据，FBX exporter 也有导出 blendshape 的路径，但 `GltfExporter` 目前没有把 morph target 写入 glTF mesh primitives，也没有写 blendshape weight 动画。

影响：

- 表情、脸部形变、服装细节形变可能丢失。
- glTF 作为默认格式时，角色完整度不如 FBX。

优先级：P0

### 2. 动画导出已降级为显式诊断/历史资料

动画不再是默认素材库目标。默认 Library 不导出 `AnimationClip`、不生成 `Animations`、不写模型-动画候选，也不把动画 channel 写入 glTF/GLB。遇到动画相关资源时，默认只写 `animation_out_of_scope.json` 或源索引诊断计数，用来说明“看到了但跳过了”。

保留的动画能力只用于显式诊断、旧库迁移和内部研究：

- 旧 `.anim` / `.animation_asset.json` / bake 报告只能作为诊断产物，不能算默认可用素材库资产。
- `ApproximateHumanoidMuscleV1`、Unity bake、`--pack_model_animations` 等路径只能在显式命令中使用，并且必须把输出标成诊断/实验/迁移。
- 这些路径不能把 `asset_library.json.capabilities.animations` 改成 `true`，也不能阻塞模型、贴图、材质和索引主线。

优先级：非默认目标

### 3. 材质还原仍需 shader-aware 重建

当前材质可以：

- 输出材质 JSON。
- 基础映射 baseColor/normal/emissive。
- 在 glTF extras 保存 Unity 贴图 slot、scale/offset、float、color 等原始属性。
- 把 Unity 透明、裁剪、双面状态映射到 glTF `alphaMode`、`alphaCutoff`、`doubleSided`。

缺口：

- Metallic/Roughness/Smoothness/Mask 贴图的语义推断不足。
- 不同游戏自定义 shader 槽没有规则化模板。
- Face/eye、mask、ramp、toon、skin、sweat 等自定义 shader 逻辑还没有模板化或烘焙。
- 法线强度、通道打包、mask map 语义等参数还原仍不足。
- 缺少材质重建策略文档和自动化测试样例。

优先级：P1

### 4. 精品筛选层需要数据驱动

当前阶段先保证默认 Library 覆盖完整；Core/Curated/Playable/ProductionReady 这类精品层应作为后处理筛选能力，不应在底层导出阶段提前误伤素材。

缺口：

- 不同游戏目录命名差异很大，硬编码过滤容易误伤。
- 缺少基于索引、验证报告、材质完整度、动画匹配度、模型尺寸和人工标记的筛选器。
- 缺少“为什么被推荐/为什么被降级”的人读报告。
- 缺少 allowlist/denylist/tag 配置文件，团队调筛选规则不够方便。

优先级：P1

### 5. 输出素材库索引仍需增强

现在已有 manifest、`asset_catalog.jsonl`、`asset_summary.json`、`animation_out_of_scope.json` 和 SQLite 索引，但还不是完整的人类浏览索引。

缺口：

- 没有统一 `asset_catalog.json` 或可浏览 `catalog.html`。
- 资源分类仍有 fallback 成分，需要优先寻找 Unity 类型、组件和引用依据，再标注无法由 Unity 关系解释的部分。
- 模型与贴图、材质、骨骼/skin、源文件的关系图还不够完整。
- 没有自动生成缩略图/预览图。

优先级：P1

### 6. Shader 仍是研究资料

当前 Shader 导出更接近 dump/反编译文本。

缺口：

- 没有和材质重建强绑定。
- 没有 HLSL/GLSL 到目标 DCC/引擎材质的转换路线。
- 没有按平台筛选 PC 相关 shader variant。

优先级：P2

### 7. 验证体系不足

当前主要靠构建、导出日志、人工查看。

缺口：

- 没有标准测试样本集。
- 已有 `model_validation.json` 自动验证 mesh/skin/material/texture 基础结构；仍缺少可视化缩略图和 Blender/查看器级别验收。
- 没有导出前后资源数量、顶点数、骨骼数、动画 clip 数的差异报告。
- 没有内存峰值和耗时基线。

优先级：P1

## 推荐开发路线

### P0：让默认 glTF 真的适合作为主格式

目标：glTF 成为模型库主格式，FBX 只作为兼容和对照格式。

任务：

1. 完善 Unity 关系图消费：用 GameObject/prefab、Animator、Avatar、SkinnedMeshRenderer、Renderer/Material、PPtr 依赖增强模型、骨骼、材质和贴图关系。
2. 补 glTF morph target 导出。
3. 校验 skin/joints/inverseBindMatrices 与 Blender 导入表现。
4. 补模型材质槽、贴图引用、bbox、骨骼覆盖等自动验证。
5. 用 Freedunk 等样本做回归测试：至少 5 个角色、2 个 NPC、1 个球、1 个场景或道具模型。

验收：

- Blender 打开 glTF 可看到模型、骨骼、贴图和材质。
- 有 blendshape 的角色，glTF 中存在 `targets` 和 `weights`。
- 默认 Library 模型保持干净，不嵌全局动作库，也不生成默认动画候选。
- `asset_library.json.capabilities.animations=false`，`animation_out_of_scope.json` 记录默认跳过规则。

### P1：做资源库索引和浏览友好输出

目标：导出结果像开发素材库，而不是一堆文件。

任务：

1. 将现有 `asset_catalog.jsonl` / `asset_summary.json` / `animation_out_of_scope.json` / SQLite 扩展成可浏览索引。
2. 继续完善每个模型记录：
   - 类型推断：Character / NPC / Stage / Prop / Ball / Effect / Unknown
   - 源 container
   - 源 file
   - pathId
   - mesh count
   - material count
   - texture count
   - animation out-of-scope 诊断计数
   - skeleton/bone count
3. 从模型视角列出贴图、材质、骨骼/skin、源文件和验证状态。
4. 生成可选 `catalog.html` 或轻量浏览器。
5. 给每个模型输出 `model.info.json`，方便单模型后处理。

验收：

- 用户可以按类型找到模型。
- 单个模型目录能知道它依赖哪些贴图、材质、骨骼/skin 和源文件。
- 后处理筛选推荐、降级或排除的资源都能在报告里看到数量和原因。

### P1：精品筛选层数据驱动化

目标：默认 Library 尽量完整，后处理筛出精品子集，同时减少误伤。

任务：

1. 增加筛选 profile 配置文件，例如：

```text
Profiles/core.json
Profiles/curated.json
Profiles/playable.json
Profiles/production-ready.json
```

2. 支持：

```powershell
--curate_library Core
--curate_library Playable
--curate_profile "Profiles/production-ready.json"
```

3. 输出筛选报告：

```text
curation_report.json
```

4. 记录每条资源被推荐、降级或排除的规则来源。

验收：

- 团队可以不改代码调筛选规则。
- 每次筛选能知道哪些资源进入 Core/Playable，哪些只保留在完整 Library。
- 如果发现误伤，能快速加入 allowlist 或人工标记。

### P1：材质语义增强

目标：让导出的模型在 Blender/查看器中更接近游戏内效果。

任务：

1. 扩展材质 JSON schema。
2. 对常见槽位做语义识别：
   - BaseColor / Albedo
   - Normal
   - Mask
   - Metallic
   - Roughness
   - Smoothness
   - AO
   - Emission
3. glTF extras 保留完整 Unity material properties。
4. 提供材质重建策略文档。

验收：

- 常见角色材质能自动挂 base color、normal、emission。
- mask/roughness/smoothness 至少能保留并标注用途候选。

### P2：贴图后处理升级

目标：Raw 模式继续快，后处理更方便。

任务：

1. `--convert_model_textures` 增加：
   - `--backup_gltf`
   - `--overwrite`
   - `--only_missing`
   - `--format Png|Webp|Ktx2|Dds`
2. 增加目录批处理：

```powershell
--convert_model_textures_dir "D:\Assets\...\characters"
```

3. 增加失败报告。
4. 评估 KTX2/BasisU native 工具链。

验收：

- 可以选中一个目录批量补贴图。
- 失败贴图能定位原 format 和原因。

### P2：Shader 和 PC 平台资源增强

目标：只面向 PC 端，提高 shader dump 的有效性。

任务：

1. 按 PC shader platform 过滤 variant。
2. 将 shader 与 material/catalog 关联。
3. 对常见 shader 参数生成材质模板建议。
4. 保留原始 shader binary/text dump。

验收：

- 每个材质能追踪到 shader 名称和导出文件。
- PC 相关 shader dump 不被移动端 variant 淹没。

## 推荐默认命令

### 默认可用素材库

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files (x86)\Freedunk\Game\Freedunk_Data" `
  "D:\Assets\Freedunk_Data_Dev\Freedunk_Data_library" `
  --game Normal
```

等价于 `Library + ByLibrary + 完整可浏览模型素材库 + glTF + PNG + Skip animations`。这是团队后续开发、测试和验收的主路径。

### 快速模型扫描

```powershell
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

### Animator 调试，不作为默认素材库

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files (x86)\Freedunk\Game\Freedunk_Data" `
  "D:\Assets\Freedunk_Data_Dev\Freedunk_Data_animator_debug" `
  --game Normal `
  --mode Animator `
  --group_assets ByContainer `
  --profile_3d Core `
  --model_format Gltf `
  --texture_mode Png `
  --animation_package Separate `
  --fbx_animation Auto
```

如需复现旧式“动画嵌入每个模型”的行为，必须显式传：

```powershell
--animation_package Embedded
```

## 近期建议

建议下一阶段不要继续扩大“能导出多少类型”，而是先把主路径做扎实：

1. 继续用 Unity 关系图增强模型、材质、贴图、骨骼/skin 和来源关系。
2. glTF morph target 导出。
3. asset catalog、SQLite 和 filter report。
4. 材质语义 schema。
5. Core/Curated/ProductionReady 等后处理筛选层配置化。
6. 固定 Freedunk 等样本集做模型素材库回归验证。

这样工具会从“能导出”进入“能稳定产出可用素材库”的阶段。
