# AnimeStudio CLI 通用资源导出说明

本文档说明如何使用 `AnimeStudio.CLI` 导出 Unity 游戏资源。目标是让普通 Unity 游戏和原神等特殊游戏使用同一套工作流，只在 `--game`、资源路径、asset index 等必要位置区分。

## CLI 入口

Debug 版 CLI：

```powershell
D:\misutime\AnimeStudio\AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe
```

建议先进入仓库目录再运行：

```powershell
cd D:\misutime\AnimeStudio
```

然后用相对入口：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe
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
- `--model_source PrefabPrimary`：模型默认以 prefab/Animator 组合体为主，raw fbx/source parts 只进入索引。
- `--texture_mode Png`：贴图默认 PNG，模型目录下有 `Textures` 硬链接，方便直接查看。
- `--animation_package Separate`：动画默认独立入库，不嵌入每个模型。
- `--profile_3d Core`：默认过滤 UI、sound、video、camera、effect、manager、test、dummy 等低价值噪声。

最重要的准则：

> 默认导出结果应该像素材库，而不是像 Unity 运行时对象转储。模型要干净可浏览，动画要独立可复用，贴图要默认可见，manifest 负责记录源路径和后续绑定关系。

更底层的实现准则：

> 动画、骨骼、模型、材质、贴图的绑定关系必须来自 Unity 自身序列化引用和结构。不要把单个游戏的目录习惯、角色命名、资源前缀当成核心逻辑；如无不得已的明确理由，默认逻辑绝不自行推断关系。

CLI 生成索引时，关系优先级应为：

1. Unity 显式引用：Animator、Animation、AnimatorController、AnimatorOverrideController、PPtr。
2. Unity 结构兼容：Avatar、HumanDescription、SkinnedMeshRenderer bones、AnimationClip binding path/type/property、blendshape channel。
3. 实际导出验证：glTF channel、skin/joint、主体骨骼覆盖、bbox。
4. 显式 fallback：container、目录名、资源名、游戏 profile。fallback 必须明确标注，不能进入默认绑定结果。

### 模型来源规则

默认 `Library` 使用 `--model_source PrefabPrimary`：

- `Models` 只放 Unity prefab、Animator 或完整 GameObject 组合模型，优先保证“打开就是可用素材”。
- `fbx` 原始身体、face、附件等 source parts 不默认作为可浏览模型导出，避免 `Jimmy_01_00.gltf` 这类只有身体、没有 prefab 组合关系的文件污染浏览结果。
- 这些 raw/source parts 不丢弃，会写入 `asset_catalog.jsonl`，`kind=ModelSourcePart`，并用 `libraryRole=RawModel`、`SourcePart` 或 `AttachmentSource` 标记。
- 如果某个 raw fbx/source part 没有对应的 prefab/Animator 组合模型，它会作为 `libraryRole=RawUnreferenced` 导出到 `Models/RawUnreferenced`。
- 需要研究零散部件时显式使用 `--model_source PrefabAndParts`；只看 raw 部件时使用 `--model_source RawPartsOnly`。

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

推荐工作流：

```text
默认 Library 导出
  -> 干净 Models + 独立 Animations
  -> 生成绑定索引
  -> 选中模型和动画
  -> 生成预览 glTF
  -> 可选打包动画合集
```

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

这个命令会读取索引里的模型 source 和动画 source，重新加载相关 bundle，只把选中的动画写进选中的模型，并输出：

```text
preview_validation.json
Models\...\<model>.gltf
```

`preview_validation.json` 会记录 animation channel 数、无效 channel 数、skin/joint 数、主体骨骼覆盖率、raw bbox 和 skinned bbox。只有当动画写入成功、channel 指向有效节点、skin 存在、命中主体骨骼且 skinned bbox 没有明显异常时，预览状态才会是 `ok`。如果只命中 `Ball_Point`、socket、twist/helper 这类辅助节点，报告会标为 `warning`。

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
- `gltfPlaybackStatus`。Humanoid/Muscle 动画会标为 `RequiresHumanoidSolverOrBake`，表示它已经是可复用 Unity 动画资产，但还不能直接保证 glTF 播放正确。

`asset_catalog.jsonl`、`animation_bindings.jsonl`、`model_animations.json` 会带上 `animationAsset` 路径，后续预览、打包或 Unity/Blender 转换器都应该优先读取这个 sidecar，而不是靠动画文件名猜测语义。

Freedunk 当前已确认的情况：`NORMALMOVE_STAND_01`、`DASH_01` 属于 `MixedHumanoidTransform`，核心数据在 Unity Humanoid/Muscle 曲线中。`ApproximateHumanoidMuscleV1` 只能用于生成实验 channel 和调试报告，不能作为最终动画资产验收。当前优先级是先把 `.anim` + `.animation_asset.json` 作为可复用动画资产稳定落盘，再通过 Unity Editor bake 流程把 Humanoid 动画采样成目标骨架 TRS。

如果 `model_animations.json` 来自一个临时小样本目录，预览/打包时必须传完整 Unity 源目录，避免旧样本缺少外部 CAB 依赖导致脸、附件、材质或 Mesh 再次断链：

```powershell
--preview_source_root "C:\Program Files (x86)\Freedunk\Game\Freedunk_Data"
```

预览命令会使用完整 source root 重新定位索引里的 source 文件，并用完整 CAB map 解析外部 PPtr。`preview_validation.json` 也会记录 `exportIssues`；只要导出日志出现 `Unable to resolve ...` 这类依赖错误，状态就不能是 `ok`。

注意：`ApproximateHumanoidMuscleV1` 仍是实验 bake。它只能说明“Muscle 曲线被写成了 glTF channel”，不能说明动作姿态正确。凡是使用该 bake 的预览会标为 `experimental`，不能作为最终可复用动画资产验收。

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

当前 `--pack_model_animations` 的定位是“可复用动画验证包”：用于确认某个模型能否播放一组候选动画、快速筛选动作、给后续动画合集打包提供已验证输入。它不是最终的 animation-only 资产格式。最终素材库仍应继续向“干净模型 + 独立动画资产 + 绑定索引 + 按需合并/预览”推进。

### Unity Humanoid 烘焙请求

Unity Humanoid/Muscle 动画不能靠 AnimeStudio 在本机侧硬猜旋转。正式路径是参照 UnityGLTF：让 Unity Editor 用 `Animator`、`Avatar` 和 `PlayableGraph` 对目标模型逐帧采样，再把采样后的骨骼 local TRS 写回 AnimeStudio 后续合成 glTF/GLB。

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

如果 `--unity_model_prefab` 或 `--unity_animation_clip` 为空，helper 会优先使用 request 里的 AnimeStudio 资产：

- 从 glTF 节点层级和 local TRS 重建采样骨架，并在 glTF 与 Unity 本地坐标之间做显式 TRS 基坐标转换。
- 从 Unity Avatar/HumanDescription 的 `humanBones`、原始 `skeletonBones` pose、twist/stretch 等参数构建 Human Avatar。
- 把 AnimeStudio 导出的 `.anim` 复制进 Unity 工程并作为 AnimationClip 导入。

注意：`humanBones` 只说明“哪根骨头对应哪个人体部位”，不能单独作为 Humanoid 动画正确性的验收依据。Humanoid bake 必须尽量使用 Unity 原始 Avatar 参考姿态；如果只用 glTF 当前骨架姿态临时构建 Avatar，Unity 仍可能给出有效 Avatar，但手臂、腿部方向会明显错误。缺少完整 `humanBones` 的旧索引会被请求生成阶段直接拒绝，避免生成看似成功但动作错误的结果。

`--run_unity_bake` 成功后会自动把 `unity_bake_result.json` 合进源 glTF，输出带标准 glTF animation channel 的 baked preview，并写出 `unity_bake_apply_report.json`。也可以单独执行：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --apply_unity_bake_result "D:\Assets\Freedunk_Data_Dev\UnityBake_Bill_NormalMove\unity_bake_request.json" `
  --baked_gltf_output "D:\Assets\Freedunk_Data_Dev\UnityBake_Bill_NormalMove\BakedPreview\Bill_01_00_ingame__NORMALMOVE_STAND_01.gltf"
```

### Unity bake 后的 glTF 动画主线

当前动画验收优先采用“Unity bake + baked glTF/GLB”主线：

1. AnimeStudio 解析 Unity 资源关系，导出干净模型、贴图、骨架和独立 AnimationClip。
2. Unity Editor 使用 Animator/Avatar/PlayableGraph 把 Humanoid/Muscle 动画采样成目标骨架 local TRS。
3. AnimeStudio 把采样结果写入 baked glTF。
4. 用 glTF 的 node、skin、animation channel、bbox 和验证报告作为默认正确性基准。

这条路径的意义是：不要让 AnimeStudio 自己硬猜 Humanoid retarget 细节；Humanoid/Avatar 交给 Unity，动画结果写回开放、可检查、适合批量验证的 glTF/GLB。FBX 只作为兼容旧流程、特定 DCC 或对照验证的可选输出，不作为默认正确性基准。

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

输出报告：

- `unity_bake_apply_report.json`：baked glTF 写入结果、skin/joint/animation target 验证。`InvalidTargets` 必须为 `0`；`HierarchyParentTargets` 表示动画写到了含 skinned joint 子孙的层级父节点，属于有效的 glTF 层级传播，不应算作错误。
- `blender_fbx_export_report.json`：仅在指定 `--baked_fbx_output` 时生成，记录 Blender 导入 glTF、导出 FBX 的对象、mesh、armature、action 和进程日志摘要。

当前边界：自动 prefab fallback 只重建骨架和 Avatar，用于正确采样 Humanoid 动画；它不是完整 Unity prefab 复原。模型、贴图、材质仍来自 AnimeStudio 已导出的 glTF。bake 合并时会把 Unity 采样结果转换回 glTF 本地坐标，并把 `*_AnimeStudioBake` 根节点 track 映射回原 glTF root，以保留 DASH 等 root motion。

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
--batch_files 4
```

`--batch_files` 表示每批加载多少个源资源文件及其依赖。数值越大，重复依赖加载越少，但内存峰值越高；数值越小，内存更稳但总耗时可能增加。大游戏全量导出优先保持默认值，内存充足时再调高。

3D 模式还会按模型数量定期做一次完整 GC：

```powershell
--model_gc_interval 32
```

64GB 内存机器建议保持默认值或调大到 `64`，优先提高吞吐；如果观察到内存持续上升不回落，再调小到 `16`。传 `0` 可以关闭模型级完整 GC，只保留批次结束时的清理。

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

`asset_catalog.jsonl` 面向素材库浏览和自动验收。模型条目会记录资源分类、mesh 数、顶点数、材质数、贴图数、动画数、骨骼数、`skeletonHash` 和 `skeleton`。`skeletonHash` 是素材库骨架 ID；`skeleton` 里会记录 `namePathHash`、`hierarchyHash`、`bindPoseHash`、`avatarHumanHash`、`avatarSkeletonNameHash` 和 `relationBasis`，用于判断哪些模型/动画共享同一套 Unity 骨架或 Humanoid Avatar 关系。Humanoid 模型还会把 Avatar 的 `humanBones`、原始 `skeletonBones` pose 和 HumanDescription 参数带入 `model_animations.json`，供 Unity bake 复建正确 Avatar 使用。

AnimationClip 条目会记录 `animationType`、`hasMuscleClip`、`coreTransformBindingCount`、`humanoidBindingCount`、`blendShapeBindingCount`、`auxiliaryBindingCount` 和 `classificationNotes`。这些字段用于判断动画是普通骨骼 TRS 曲线、Humanoid/Muscle 动画、BlendShape 动画，还是只作用在 socket/helper 上的辅助动画。

`asset_summary.json` 是总览报告，记录模型、动画、实验 shader 的数量和模型是否带贴图、骨骼、morph。`model_validation.json` 是静态模型验收报告，检查 glTF image、texture、material、mesh accessor、skin joint、inverseBindMatrices 是否自洽。`animation_bindings.jsonl` 从动画视角列出候选模型，`model_animations.json` 从模型视角列出候选动画、匹配依据、匹配分数和下一步动作。`model_animations.compact.json` 是规范化紧凑索引：模型、动画、模型-动画候选引用分表保存，避免把完整动画对象重复写进每个模型。后续素材库浏览、搜索、批量 bake、UI 和性能优化应优先读取 compact 文件；verbose `model_animations.json` 保留给人工排查和兼容旧命令。

`model_animations.json` 和 `model_animations.compact.json` 都会记录 `animationCapability`。它只表示下一步安全处理路径，不等于动画已经视觉验证通过：

- `HumanoidBodyBakeReady`：Humanoid/Muscle 身体动画，走 Unity bake 请求和 glTF 合并验证。
- `TransformBodyPreviewReady`：普通 Transform 身体动画，可以进入直接预览/打包流程。
- `BlendShapeNotImplemented` / `BlendShapeLegacyNotImplemented`：表情或 morph 动画，后续需要单独实现 BlendShape channel。
- `LegacyNotPlayableYet`：legacy AnimationClip，后续需要单独采样。
- `NonCharacterTransformNeedsMapping`：非角色物件/道具/场景 Transform 动画，需要按 Unity node path 映射后验证。
- `AuxiliaryTransformNeedsMapping` / `UnknownNeedsInspection`：socket、helper、材质/事件类或未知动画，需要先检查绑定目标。

`skeletons.json` 从骨架视角聚合模型和候选动画：`models` 只放可浏览的带 mesh 模型，动画 FBX 等无 mesh 的源骨架会计入 `sourceSkeletonCount`，避免污染模型浏览列表。默认候选必须来自 Unity 显式引用或结构兼容关系；路径、名称、resourceKind 只能在后续显式 fallback 模式里作为带标注的补充线索。

默认还会写入性能日志：

```text
export_profile.jsonl
```

它记录批次加载、资产数据构建、模型转换、`model_mesh`、`model_skin`、`model_material`、`model_texture`、`model_texture_raw`、模型写出和 GC 的耗时及内存快照。觉得全量导出慢时，保留这个文件就能直接分析瓶颈。关闭日志：

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
- `--profile_3d Core`：默认值。保留角色、NPC、场景、道具、球等高相关 3D 模型，额外排除常见 effect 承载网格、manager/data 间接引用、stagetest/temp、shadow/dummy/test/sphere 等非核心模型。需要研究全部候选模型时改用 `--profile_3d All`。
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

这会使用默认 `Library + ByLibrary + Core + glTF + PNG + Separate animations`。模型和贴图可直接浏览，动画独立进入 `Animations`，不会把全局篮球动作重复塞进每个角色 glTF。

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

AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
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
