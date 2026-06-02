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
- `model_animations.json` 从模型视角记录候选动画、匹配依据、匹配分数和下一步动作；当前是启发式索引，不等于最终 retarget 结果。

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
3. 检查 `asset_catalog.jsonl` 和关键 glTF。
4. 确认 `D:\Assets\Freedunk_Data_Dev\AnimeStudio_DevSamples` 仍然像可用素材库，而不是散乱对象转储。

## 模型动画绑定规则

后续开发统一遵循：

- 默认 `Library` 输出干净模型，不默认嵌入全局动作库。
- 动画默认独立进入 `Animations`。
- 自动索引告诉团队“这个模型可能支持哪些动画”。
- 按需生成预览 glTF，用来查看某个模型播放某个动画的效果。
- 确认一组动画后，再显式生成带动画合集的 glTF/GLB。

测试时不要把“一个角色能找到的所有动画”直接塞进模型。Freedunk 角色会引用大量公共篮球动作，直接全嵌会回到旧失败案例：文件巨大、重复、难浏览。

绑定关系应优先验证这些信息：

- Animator Controller 是否直接引用该 AnimationClip。
- AnimationClip source/container 是否和角色、职业、性别、动作库有关。
- skeleton hash / bone path 是否兼容。
- 实际写入 glTF 后是否产生有效 animation channels。

当前 `animation_bindings.jsonl` 和 `model_animations.json` 只是候选索引。下一阶段要补按需预览/打包命令，把候选动画实际写入 glTF/GLB，并记录有效 channel 数。

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
