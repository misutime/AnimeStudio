# AnimeStudio 资源导出工具开发规划

## 目标定位

本工具的核心目标是面向 PC 端 Unity 游戏，把已经打包过的资源尽可能还原成开发期可使用、可查看、可二次整理的素材库。

优先资源类型：

- 3D 模型：静态模型、角色模型、场景模型、道具、球、NPC。
- 骨骼与蒙皮：SkinnedMeshRenderer、骨架层级、bind pose、权重。
- 动画：Animator/AnimationClip 导出的骨骼动画，后续包含 blendshape 动画。
- 贴图：Texture2D，支持 Raw 快速导出、按需 PNG 后处理。
- 材质：Material 参数、贴图槽、shader 引用和可分析 metadata。
- Shader：实验功能，以研究和重建材质为目标，先保证可转储、可定位、可关联；不进入默认核心导出。

非目标或低优先级：

- 完整还原 Unity 工程。
- 还原运行时代码逻辑、MonoBehaviour 行为。
- 规避加密、绕过保护或破解私有协议。
- 默认导出所有零散资源。为了效率和可用性，默认可以接受轻微不完整。

验收取向：

- 默认 `Core` 导出能覆盖 90% 以上真实游戏开发需要的高价值 3D 资源。
- 模型、骨骼、动画、贴图、材质这些核心资产不能系统性缺失。
- 输出目录能像素材库一样浏览，单个模型目录可以直接查看模型和相关贴图。
- 保持准确前提下优先提升批量导出速度和内存稳定性。

基础准则：

> 后续每一步实现都以“导出可用素材库”为准。默认输出必须服务于开发者浏览、筛选、复用素材；特殊研究、调试、旧式内嵌动画、Raw 快速扫描等行为必须通过显式参数开启。

Unity 关系解构准则：

> 后续每一步实现都必须优先使用 Unity 自身序列化出来的通用关系来还原素材库，而不是围绕某个游戏、某个目录、某个角色名写特殊适配。游戏名、目录名、资源名前缀只能作为低优先级补充线索，不能替代 Unity 关系图。

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
4. 低优先级启发式：container、目录名、角色名、资源分类、游戏 profile。

任何针对单个游戏的规则都必须满足：

- 默认关闭或放入 profile/config。
- 有通用关系图无法表达的明确理由。
- 不影响 `Normal` 通用 Unity 导出路径。
- 文档中标明它是游戏 profile 规则，不是核心导出逻辑。

模型和动画准则：

> 模型默认保持干净，动画默认独立入库，绑定关系通过索引和后处理建立；只有按需预览或显式打包时，才把动画写入模型 glTF/GLB。

原因：

- 一个角色模型通常能使用很多动画，直接全嵌会导致 glTF 巨大、重复、难浏览。
- Unity 游戏常把角色模型、角色私有动画、通用身体动作、NPC 动画、场景动画放在不同 bundle；导出模型时未必已经加载完整动画集合。
- 同名/同类动画不一定兼容，必须通过骨骼路径、skeleton hash、Animator/AnimationClip 关系和实际 glTF channel 写入结果验证。
- 默认素材库的首要价值是“能浏览和筛选资产”，不是把所有运行时关系一次性塞进单文件。
- 角色、NPC、道具、机关、场景物件都可能有动画；绑定逻辑必须基于 Unity component/controller/clip binding，而不是只服务角色动画。

团队统一工作流：

1. 默认 `Library` 导出干净模型：mesh、material、PNG texture、skeleton/skin。
2. AnimationClip 独立进入 `Animations`。
3. 自动生成 Unity 关系图：模型、组件、Controller、Avatar、Clip、binding、PPtr 依赖。
4. 从 Unity 关系图生成绑定索引：`animation_bindings.jsonl` 和 `model_animations.json`。
5. 选中模型和动画后，按需生成可播放预览 glTF。
6. 确认一组动画后，显式生成带动画合集的 glTF/GLB。

默认素材库形态：

- 模型默认 glTF，进入 `Models`。
- 角色/蒙皮模型默认保留 skeleton/skin，不能为了模型浏览速度丢掉骨骼。
- 贴图默认 PNG，进入共享 `Textures/_ModelDependencies`，模型目录通过硬链接引用。
- 动画默认独立进入 `Animations`，不嵌入每个模型。
- Shader 默认不导出；实验研究时显式 `--include_shaders`，进入 `Shaders`，以安全 raw archive + metadata 形式保留。
- 材质、贴图、动画、源路径关系由 manifest/catalog 记录。
- `Core` profile 默认过滤 UI、sound、video、camera、effect、manager、test、dummy 等低价值噪声。

## 当前完成度

总体判断：当前工具已经进入“可用于 PC Unity 3D 游戏批量资源提取”的阶段，但还没有达到“高可信开发素材库还原器”的完整状态。

粗略完成度：

| 模块 | 当前状态 | 完成度 |
| --- | --- | --- |
| Unity 文件读取/依赖解析 | 已可加载 UnityFS/SerializedFile，支持 CAB 依赖 map，Freedunk 1 字节前导 UnityFS 已兼容 | 75% |
| 3D 模型导出 | 支持 `SplitObjects`、`Animator`，默认 glTF，可导出 mesh、skin、基础层级 | 70% |
| glTF 输出 | 已有 `.gltf + .bin`、`.glb`、材质贴图引用、skin、基础 TRS 动画 | 60% |
| FBX 输出 | 继承原有 FBX exporter，支持 blendshape 和动画能力较完整 | 70% |
| 贴图导出 | PNG 默认、Raw 可选、按模型后处理、硬链接省空间 | 80% |
| 材质导出 | 能导出材质 JSON、基础 PBR 映射、extras 保留 Unity 贴图信息 | 55% |
| 动画导出 | 默认独立导出 AnimationClip；`Animator` 模式仍可调试收集，模型默认不嵌全局动作库；支持按需生成预览 glTF、验证报告和 AnimationClip 类型分类 | 68% |
| Shader 导出 | 实验功能；显式 `--include_shaders` 时安全归档 raw + metadata，避免 native 反汇编崩溃；反编译仍需单独实验模式 | 45% |
| 噪声过滤 | 已有 `--profile_3d Core|All`，默认过滤常见非核心模型 | 65% |
| 性能与诊断 | 有 profile jsonl、manifest、阶段耗时、缓存、批处理、GC 策略 | 70% |
| 输出可浏览性 | 目录按 container 组织，模型依赖贴图集中共享并硬链接到模型目录 | 70% |

## 已有关键能力

### 默认 3D Core Profile

`--profile_3d Core` 已作为默认 3D profile。

默认排除：

- `assets/outgame/res/effect`
- `assets/graphics/effect`
- `assets/ingame/prefabs/managers`
- `assets/ingame/prefabs/datas`
- `assets/stagetest`
- `assets/graphics/temp`
- `assets/graphics/stageoutgame/playerselect`
- `assets/graphics/character/pc/_common`
- `sphere`
- `shadow` / `dummy` / `test` / `groundshadow` 等名称噪声

保留方向：

- 角色、NPC、球、场景、篮筐、道具、奖杯、可直接用于开发素材浏览的高价值模型。

需要完整排查时可以使用：

```powershell
--profile_3d All
```

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
- 默认 `--animation_package Separate`。
- 模型 glTF 不嵌入 Animator Controller 的全局动作库。
- AnimationClip 独立写入 `Animations`。
- 后续通过 skeleton hash、binding manifest、animation-only glTF 把模型和动画重新关联。

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

`asset_catalog.jsonl` 是素材库索引。模型条目记录 resourceKind、mesh/vertex/material/texture/animation/bone 数和 skeletonHash；动画会作为独立资产登记，实验 shader 只在 `--include_shaders` 时登记。

AnimationClip 条目会额外记录：

- `animationType`：`TransformBodyAnimation`、`MixedHumanoidTransform`、`HumanoidMuscleAnimation`、`AuxiliaryAnimation`、`BlendShapeAnimation` 等。
- `hasMuscleClip`：是否存在 Unity Humanoid/Muscle 数据。
- `coreTransformBindingCount`：直接命中主体骨骼的 Transform binding 数。
- `humanoidBindingCount`：Animator/Humanoid binding 数。
- `blendShapeBindingCount`：SkinnedMeshRenderer / blendshape binding 数。
- `auxiliaryBindingCount`：point、socket、twist、helper 等辅助节点 binding 数。
- `classificationNotes`：是否需要 Humanoid/Muscle bake、是否主要是辅助节点动画等提示。

默认还会生成：

```text
asset_summary.json
animation_bindings.jsonl
model_animations.json
unity_relations.jsonl
unity_relation_summary.json
```

`asset_summary.json` 汇总导出数量、资源分类和模型是否带骨骼/贴图/morph。`unity_relations.jsonl` 记录 GameObject、组件、Animator、Animation、AnimatorController、AnimatorOverrideController、Avatar、SkinnedMeshRenderer、MeshFilter、Renderer/Material、AnimationClip binding 等 Unity 原生关系，`unity_relation_summary.json` 是轻量摘要。`animation_bindings.jsonl` 为独立 AnimationClip 列出候选模型，`model_animations.json` 为每个模型列出候选动画、匹配依据、匹配分数、验证状态和下一步动作。目前 `model_animations.json` 仍可能包含 `resourceKind`、资源路径和角色/场景线索等低优先级启发式匹配，后续必须主要从 Unity 关系图派生，并用 skeleton hash / bone path / preview glTF 做验证。

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

### 2. glTF 动画仍偏基础

当前 glTF 动画主要写骨骼/节点的 translation、rotation、scale。对于普通 Transform 曲线，这条路径可以生成可播放动画；对于 Unity Humanoid/Muscle 动画，还需要先把 muscle 曲线烘焙回目标骨架的 TRS 曲线。

缺口：

- blendshape 动画未写入 `weights` channel。
- Humanoid/Muscle 动画未 bake 到骨骼 TRS，所以 Freedunk 这类角色身体动作暂时只能看到辅助节点 channel。
- 动画 clip 与 AnimatorController 状态机关系已有关系图明细，但还没有形成完整可浏览索引。
- 模型与动画的适配关系已有 Unity 关系图上游数据，`model_animations.json` 仍需要从启发式升级为关系图 + 可验证关系。
- 未对动画 clip 做可读命名、角色归属、重复去重。
- 缺少动画合集 glTF/GLB 打包 CLI。

Freedunk 当前验证结论：

- `D:\Assets\Freedunk_Data_Dev\AnimationTypeScan` 小样本扫描 594 个 `AnimationClip`，全部是 `MixedHumanoidTransform`。
- `NORMALMOVE_STAND_01` 预览能写出 glTF animation channel，但 `coreBoneChannelCount` 为 0，主要命中 `Ball_Point`、twist/helper 等辅助节点。
- 预览 glTF 已保留 `animations[].extras.unityHumanoid`，`NORMALMOVE_STAND_01` 当前可看到 160 条 muscle 曲线、3177 个 keyframe，并明确标记 `requiresBake: true`。
- 这说明 Freedunk 角色身体动作的主路径是 Humanoid/Muscle bake，不是继续寻找直接 Transform body 曲线。

优先级：P0

### 3. 材质还原只是基础可见

当前材质可以：

- 输出材质 JSON。
- 基础映射 baseColor/normal/emissive。
- 在 glTF extras 保存 Unity 贴图引用。

缺口：

- Metallic/Roughness/Smoothness/Mask 贴图的语义推断不足。
- 不同游戏自定义 shader 槽没有规则化模板。
- Alpha/透明、双面、裁剪、法线强度等参数还原不足。
- 缺少材质重建策略文档和自动化测试样例。

优先级：P1

### 4. Core profile 还需要数据驱动

当前 Core profile 是基于 Freedunk 观察和通用经验写的规则。

缺口：

- 不同游戏目录命名差异很大，硬编码规则容易误伤。
- 缺少导出后噪声分析报告，无法量化 Core 命中率。
- 缺少 allowlist/denylist 配置文件，团队调规则不够方便。

优先级：P1

### 5. 输出素材库索引仍需增强

现在已有 manifest、`asset_catalog.jsonl`、`asset_summary.json` 和 `animation_bindings.jsonl`，但还不是完整的人类浏览索引。

缺口：

- 没有统一 `asset_catalog.json` 或可浏览 `catalog.html`。
- 资源分类仍是启发式，需要更强的 Character / NPC / Stage / Prop / Ball 分类规则。
- 模型与贴图、材质、动画、源文件的关系图还不够完整。
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
- 没有自动打开 glTF 验证 mesh/skin/material/texture/animation 是否存在。
- 没有导出前后资源数量、顶点数、骨骼数、动画 clip 数的差异报告。
- 没有内存峰值和耗时基线。

优先级：P1

## 推荐开发路线

### P0：让默认 glTF 真的适合作为主格式

目标：glTF 成为模型库主格式，FBX 只作为兼容和对照格式。

任务：

1. 完善 Unity 关系图消费：用 GameObject/prefab、Animator、Animation、Controller、OverrideController、Avatar、SkinnedMeshRenderer、AnimationClip binding、PPtr 依赖生成模型动画绑定索引。
2. 补 Humanoid/Muscle bake：把 Unity muscle 曲线采样成目标骨架 TRS 曲线。
3. 补 glTF morph target 导出。
4. 补 glTF blendshape weight 动画。
5. 校验 skin/joints/inverseBindMatrices 与 Blender 导入表现。
6. 给 Animator/AnimationClip 输出动画 clip 列表、来源、角色归属和候选模型。
7. 增强按需预览命令：Humanoid/Muscle 动画预览必须覆盖主体骨骼。
8. 增加动画合集打包命令：把已确认的一组动画写进 glTF/GLB。
9. 用 Freedunk 角色样本做回归测试：至少 5 个角色、2 个 NPC、1 个球、1 个场景。

验收：

- Blender 打开 glTF 可看到模型、骨骼、贴图和基础动画。
- 有 blendshape 的角色，glTF 中存在 `targets` 和 `weights`。
- 默认 Library 模型仍然干净，不嵌全局动作库。
- 预览 glTF 能播放指定动画，并记录实际写入的 channel 数。

### P1：做资源库索引和浏览友好输出

目标：导出结果像开发素材库，而不是一堆文件。

任务：

1. 将现有 `asset_catalog.jsonl` / `asset_summary.json` / `animation_bindings.jsonl` 扩展成可浏览索引。
2. 继续完善每个模型记录：
   - 类型推断：Character / NPC / Stage / Prop / Ball / Effect / Unknown
   - 源 container
   - 源 file
   - pathId
   - mesh count
   - material count
   - texture count
   - animation count
   - skeleton/bone count
3. 用 skeleton hash / bone path / AnimatorController / source container 验证动画候选绑定，减少只靠 resourceKind 的误配。
4. 生成 `model_animations.json`，从模型视角列出可用动画、匹配依据、匹配分数和验证状态。
5. 使用 `--generate_preview_gltf` 对单个模型和单个候选动画生成可播放预览，并写出 `preview_validation.json`，记录 channel、skin、joint 和 bbox 验证结果。
6. 生成可选 `catalog.html` 或轻量浏览器。
7. 给每个模型输出 `model.info.json`，方便单模型后处理。

验收：

- 用户可以按类型找到模型。
- 单个模型目录能知道它依赖哪些贴图、材质、动画。
- Core 排除的资源也能在报告里看到数量和原因。

### P1：Core profile 数据驱动化

目标：保持默认高效率，同时减少误伤。

任务：

1. 增加 profile 配置文件，例如：

```text
Profiles/3d-core.json
Profiles/3d-all.json
Profiles/freedunk-core.json
```

2. 支持：

```powershell
--profile_3d Core
--profile_3d All
--profile_file "Profiles/freedunk-core.json"
```

3. 输出 filter report：

```text
export_filter_report.json
```

4. 记录每条被排除资源的规则来源。

验收：

- 团队可以不改代码调过滤规则。
- 每次导出能知道排除了多少 effect、manager、test、shadow。
- 如果发现误伤，能快速加入 allowlist。

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

等价于 `Library + ByLibrary + Core + glTF + PNG + Separate animations`。这是团队后续开发、测试和验收的主路径。

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

1. 用 Unity 关系图驱动 `model_animations.json`，替代当前低优先级启发式主导的候选绑定。
2. Humanoid/Muscle bake。
3. glTF morph target / blendshape 动画。
4. asset catalog 和 filter report。
5. 材质语义 schema。
6. Core profile 配置化。
7. 固定 Freedunk 样本集做回归验证。

这样工具会从“能导出”进入“能稳定产出可用素材库”的阶段。
