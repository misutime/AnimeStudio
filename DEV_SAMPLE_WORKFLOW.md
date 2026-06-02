# AnimeStudio 开发样本工作流

本项目后续功能开发以“导出可用素材库”为基础准则。不要只靠全量导出验证功能；全量太慢，也不利于定位问题。默认使用 Freedunk 的真实资源建立一个小型、固定、可重复的开发样本库。

## 固定样本目录

默认输出：

```text
D:\Assets\Freedunk_Data_Dev\AnimeStudio_DevSamples
```

重建样本：

```powershell
cd D:\misutime\AnimeStudio
tools\Export-FreedunkDevSamples.ps1
```

保留已有输出并追加：

```powershell
tools\Export-FreedunkDevSamples.ps1 -KeepExisting
```

## 多模型静态验收样本

模型、贴图、材质、骨骼、skin 的基础逻辑不要只看 Bill。默认使用 12 个 Freedunk 角色变体做交叉验证：

```powershell
cd D:\misutime\AnimeStudio
tools\Export-FreedunkMultiModelStaticSample.ps1
```

默认输出：

```text
D:\Assets\Freedunk_Data_Dev\MultiModelStaticSample
```

这个脚本会复制 12 个角色的 prefab/fbx/material/texture bundle 到：

```text
D:\Assets\Freedunk_Data_Dev\MultiModelMiniInput
```

验收重点：

- `model_validation.json` 里至少应有 10 个以上模型。
- 多数角色模型应有 `skin`。
- 所有导出的 glTF 不应出现无效 image、texture、material、mesh accessor、skin joint 或 inverseBindMatrices。
- 如果某个角色有 face、mask、cap、bag 等模块化部件，优先按 Unity `SkinnedMeshRenderer.mesh`、`SkinnedMeshRenderer.bones`、Transform 层级装配进角色 glTF；如果它本身也是独立 GameObject/root，则也允许作为单独模型出现。不要因为名称里有 `face` 就强制合并或强制拆分。
- 角色看起来“没脸”时，先检查 glTF 是否含有 face/mask mesh、材质贴图和 skin，再用 F3D/模型校验确认它是否被头盔、面罩、头发遮挡，或者是否因为 skin/bind pose 变换跑飞。不要直接按目录名或角色名补特殊规则。

截至当前基线，这个样本导出 38 个 glTF，`model_validation.json` 全部为 `ok`，其中 37 个带 skin，38 个都有贴图。

## 样本来源

默认脚本使用：

```text
C:\Program Files (x86)\Freedunk\Game\Freedunk_Data
```

覆盖的真实素材：

- `graphics\character\pc\bill_01_00`：角色模型、骨骼、材质、PNG 贴图。
- `graphics\trophy`：静态道具、prefab 材质贴图、模型拆分噪声。
- `graphics\stage\models.ab`：球场、篮筐、场景模型，用于验证场景动画候选绑定。
- `graphics\shaders.ab`：实验 shader 原始归档和 metadata，脚本显式传 `--include_shaders`。
- `graphics\stage\animation.ab`：场景/篮筐相关动画。
- `graphics\character\npc\prefab\animation_npc.ab`：NPC 动画。

## 验收文件

样本导出后重点看：

```text
asset_catalog.jsonl
asset_summary.json
animation_bindings.jsonl
model_animations.json
unity_relations.jsonl
unity_relation_summary.json
model_validation.json
export_manifest.jsonl
Models\
Animations\
Shaders\   # 只有实验 shader 样本会出现
Textures\_ModelDependencies\
```

`asset_catalog.jsonl` 是后续自动检查的主入口。模型条目应该包含：

- `resourceKind`
- `meshCount`
- `vertexCount`
- `materialCount`
- `textureCount`
- `boneCount`
- `skeletonHash`
- `animationCount`

## 当前基线

Bill 样本应该满足：

- 有 glTF 模型。
- 有 `skins`。
- 有骨骼统计和 `skeletonHash`。
- 有 PNG 贴图引用。
- `model_validation.json` 不应报告无效 image、texture、material、mesh accessor、skin joint 或 inverseBindMatrices。
- `animations` 为 0，不能再把 Animator Controller 的全局动作库塞进模型。

Shader 样本应该满足：

- 只有显式 `--include_shaders` 时导出 `.shader.raw` 和 `.shader.raw.json`。
- 不运行 native D3D 反汇编。
- 不因 shader 反编译崩溃导致整个导出失败。

动画样本应该满足：

- 独立写入 `Animations`。
- 不依赖某个模型文件内嵌。
- `animation_bindings.jsonl` 里能看到按 `resourceKind` 匹配出来的候选模型。

索引样本应该满足：

- `asset_summary.json` 汇总模型、动画、实验 shader 数量。
- `asset_catalog.jsonl` 记录每个模型的 mesh、material、texture、bone、skeletonHash。
- `animation_bindings.jsonl` 记录独立动画和候选模型。
- `unity_relations.jsonl` 记录 GameObject、组件、Animator、Controller、Avatar、SkinnedMeshRenderer、AnimationClip binding 等 Unity 原生关系。
- `unity_relation_summary.json` 汇总关系数量和关键覆盖率，便于快速判断样本是否真的加载到了 Animator Controller、Avatar、Muscle Clip、skin bones。
- `model_validation.json` 验证 glTF 模型、贴图、材质、skin/joint 的基础结构。先确认模型基础结构，再推进动画。
- `model_animations.json` 从模型视角记录候选动画、匹配依据、匹配分数和下一步动作；默认候选必须来自 Unity 显式引用或结构兼容关系，不等于最终 retarget 结果。

## 失败案例

`D:\Assets\Freedunk_Data_core_png_anim` 是旧策略失败案例：`Animator + Auto` 把 Freedunk 的全局篮球动作库重复嵌进每个角色模型，导致 `Bill_01_00.gltf` 巨大且不适合浏览。

后续开发中，默认输出不得回到这种形态。需要旧式内嵌动画时，必须显式使用：

```powershell
--animation_package Embedded
```

## 后续开发规则

每做一项导出逻辑改动，都应：

1. 运行 `dotnet build AnimeStudio.CLI\AnimeStudio.CLI.csproj`。
2. 运行 `tools\Export-FreedunkDevSamples.ps1`。
3. 先检查 `model_validation.json`，确认模型、贴图、材质、skin/joint 基础结构自洽。
4. 再检查 `asset_catalog.jsonl`、`unity_relations.jsonl` 和关键 glTF。
5. 确认 `D:\Assets\Freedunk_Data_Dev\AnimeStudio_DevSamples` 仍然像可用素材库，而不是散乱对象转储。
6. 确认新增逻辑优先使用 Unity 原生关系，而不是按游戏名、目录名、角色名写死。

Unity 原生关系包括：

- Animator / Animation 组件挂在哪个 GameObject 上。
- AnimatorController / AnimatorOverrideController 直接引用哪些 AnimationClip。
- Animator 使用哪个 Avatar。
- Avatar / HumanDescription 如何映射 human bone 和 skeleton。
- SkinnedMeshRenderer 使用哪些 bones、bind pose、blendshape。
- SkinnedMeshRenderer 所在 Transform 和 bone Transform 的层级关系；Unity bind pose 转 glTF inverseBindMatrices 时必须避免重复应用 renderer/mesh node transform。
- AnimationClip binding 的 path、type/classID、attribute/property、customType。
- AssetBundle / SerializedFile / PPtr 依赖关系。

只有在 Unity 关系缺失或被游戏打包流程破坏，并且命令或 profile 显式开启 fallback 时，才允许用 container、目录名、资源名、游戏 profile 作为补充线索。fallback 结果必须在索引中标明，不能伪装成 Unity 引用关系。

## 模型动画绑定规则

后续开发统一遵循：

- 默认 `Library` 输出干净模型，不默认嵌入全局动作库。
- 动画默认独立进入 `Animations`。
- 自动索引告诉团队“这个模型可能支持哪些动画”。
- 按需生成预览 glTF，用来查看某个模型播放某个动画的效果。
- 确认一组动画后，再显式生成带动画合集的 glTF/GLB。

测试时不要把“一个角色能找到的所有动画”直接塞进模型。Freedunk 角色会引用大量公共篮球动作，直接全嵌会回到旧失败案例：文件巨大、重复、难浏览。

绑定关系应优先验证这些信息：

- Animator / Animation 组件是否直接挂在该模型根或 prefab 层级上。
- AnimatorController / AnimatorOverrideController 是否直接引用该 AnimationClip。
- Animator 的 Avatar 是否能解释该 Humanoid/Muscle AnimationClip。
- AnimationClip Transform binding path 是否覆盖模型层级或骨骼路径。
- AnimationClip BlendShape binding 是否命中 mesh morph channel。
- skeleton hash / bone path / human bone 映射是否兼容。
- 实际写入 glTF 后是否产生有效 animation channels。
- 显式 fallback 模式下，才允许参考 AnimationClip source/container 是否和角色、职业、性别、动作库有关。

当前 `animation_bindings.jsonl` 和 `model_animations.json` 仍然只是候选索引；`--generate_preview_gltf` 会把候选动画实际写入 glTF 并生成 `preview_validation.json`，用于验证 channel、skin、主体骨骼覆盖和 bbox。

当前已生成 `unity_relations.jsonl` 和 `unity_relation_summary.json`，把模型、组件、Controller、Avatar、Clip、binding、PPtr 依赖记录下来。`model_animations.json` 默认必须从 Unity 关系图或等价的内存 Unity 引用解析结果生成，不再主要依赖路径/名称启发式。

`asset_catalog.jsonl` 和 `model_animations.json` 里的动画候选还会记录：

- `animationType`：例如 `TransformBodyAnimation`、`MixedHumanoidTransform`、`HumanoidMuscleAnimation`、`AuxiliaryAnimation`。
- `hasMuscleClip`：是否包含 Unity Humanoid/Muscle 动画数据。
- `coreTransformBindingCount`：直接命中主体骨骼的 Transform binding 数。
- `humanoidBindingCount`：Animator/Humanoid binding 数。
- `auxiliaryBindingCount`：socket、point、twist、helper 等辅助节点 binding 数。
- `classificationNotes`：当前导出器对该动画的风险提示。

## 动画预览验证样本

验证候选动画是否真的能绑定模型时，不要改默认 Library 模型，单独生成 preview：

```powershell
cd D:\misutime\AnimeStudio
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --generate_preview_gltf "D:\Assets\Freedunk_Data_Dev\LibraryIndexSample\model_animations.json" `
  --game Normal `
  --preview_model "^Bill_01_00_ingame$" `
  --preview_animation "^NORMALMOVE_STAND_01$" `
  --preview_output "D:\Assets\Freedunk_Data_Dev\Preview_Bill_NormalMove"
```

验收重点：

- `preview_validation.json` 的 `status` 为 `ok`。如果是 `warning`，先看 `notes` 说明。
- `counts.animations` 大于 0。
- `counts.channels` 大于 0。
- `counts.invalidChannels` 为 0。
- `counts.skins` 和 `counts.skinJoints` 大于 0。
- `animationCoverage.coreBoneChannelCount` 大于 0，且 `animationCoverage.coreBoneNodeCount` 至少覆盖多个主体骨骼。
- `bounds.raw.size` 和 `bounds.skinnedFinal.size` 接近，不能回到 skin 拉爆形态。

当前 Freedunk 的 `NORMALMOVE_STAND_01` 预览会被标为 `warning`：它能写出 glTF 动画 channel，但主体骨骼覆盖为 0，主要命中 `Ball_Point`、twist/helper 之类辅助节点。这个现象不是模型 skin 再次拉爆，而是 Freedunk 身体动作主要存放在 Unity Humanoid/Muscle 数据里，当前 glTF TRS 动画写入还没有完成 Muscle bake。

当前 `D:\Assets\Freedunk_Data_Dev\Preview_Bill_NormalMove\preview_validation.json` 已能报告 Humanoid 诊断：

```text
humanoid.present: true
humanoid.requiresBake: true
humanoid.muscleCurveCount: 160
humanoid.keyframeCount: 3177
```

这说明动画数据已经被读取并保留到 glTF `animations[].extras.unityHumanoid`；下一步不是再找动画文件，而是实现 Humanoid/Muscle -> skeleton TRS bake。

## 完整模型动画样本

当需要验证“模型 + PNG 贴图 + skin/bones + 可播放身体动画”时，使用固定小输入样本，不要直接扫完整 `Freedunk_Data`：

```powershell
cd D:\misutime\AnimeStudio
tools\Export-FreedunkCompleteBodySample.ps1
```

默认输出：

```text
D:\Assets\Freedunk_Data_Dev\CompleteBodyAnimSample
```

这个脚本会把 Bill 角色相关 bundle 和 `01_normal.ab` 复制到：

```text
D:\Assets\Freedunk_Data_Dev\CompleteMiniInput
```

然后只导出 `Bill_01_00_ingame` / `Bill_01_00_outgame` 和 `NORMALMOVE_STAND_01`。这是后续验证模型动画绑定的首选小样本。

## 动画类型扫描样本

当需要判断某个游戏的角色动画是普通 Transform 曲线，还是 Unity Humanoid/Muscle 动画时，使用：

```powershell
cd D:\misutime\AnimeStudio
tools\Export-FreedunkAnimationTypeScan.ps1
```

默认输出：

```text
D:\Assets\Freedunk_Data_Dev\AnimationTypeScan
```

这个脚本会复制 Bill 模型和 Freedunk 多个 ingame/outgame/lobby 动画 bundle 到：

```text
D:\Assets\Freedunk_Data_Dev\AnimationTypeMiniInput
```

截至当前基线，这个样本共扫描到 594 个 `AnimationClip`，全部分类为 `MixedHumanoidTransform`，没有发现可直接作为身体动作的 `TransformBodyAnimation`。因此 Freedunk 角色身体动画的主路径应推进 Humanoid/Muscle bake，而不是继续寻找普通骨骼 TRS 曲线。
