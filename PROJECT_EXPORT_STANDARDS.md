# AnimeStudio 通用 Unity 资源导出规范

本文档是团队推进 AnimeStudio 的共同准则。无论由团队成员还是 AI 继续开发，都应先按这里的目标、边界、验收方式和节奏推进。

## 1. 核心目标

AnimeStudio 的核心目标是面向 PC 端 Unity 游戏，把已经打包过的资源还原成“可供开发者使用的素材库”。

可用素材库不是 Unity 运行时对象转储，也不是完整 Unity 工程恢复。它应该满足：

- 打开输出目录就能浏览高价值资产。
- 模型、骨骼、贴图、材质、动画尽可能准确、可复用。
- 默认输出干净，少噪声，少无关 UI、声音、视频、管理器、测试对象。
- 关系来源可追溯，能说明模型、模块、动画、材质、贴图来自哪个 Unity 引用或结构。
- 对无法完整还原的部分明确标注，不假装成功。

当前接受的工程取向：

- 面向 PC Unity 游戏。
- 默认优先 3D 资产。
- 可以接受轻微不完整，换取更高准确性、可浏览性和效率。
- 默认宁缺毋滥：不确定的动画、模块、材质效果不应污染素材库。
- 高价值素材的 80% 到 90% 可用，比低质量全量 dump 更重要。

## 2. 非目标

以下内容不是默认目标：

- 完整还原 Unity 工程。
- 还原运行时代码、MonoBehaviour 业务逻辑或私有服务协议。
- 规避加密、绕过保护或破解私有格式。
- 默认导出所有零散对象、临时对象、测试对象和无关组件。
- 完整复刻每个游戏的自定义 shader。
- 默认把一个模型可能关联的所有动画全部嵌入同一个 glTF。

这些内容如果需要研究，必须通过显式参数、实验 profile 或独立工具开启，并在文档里标明它不是通用默认路径。

## 3. 最高优先级规则

### Unity 关系优先

默认逻辑必须优先解析 Unity 自身的通用序列化引用和结构。不能围绕单个游戏、目录、角色名、资源名前缀写默认逻辑。

关系优先级固定为：

1. Unity 显式引用：`Animator`、`Animation`、`AnimatorController`、`AnimatorOverrideController`、`PPtr`、AssetBundle/SerializedFile 依赖。
2. Unity 结构兼容：`Avatar`、`HumanDescription`、`SkinnedMeshRenderer.bones`、bind pose、skeleton hash、`AnimationClip` binding path/type/property、blendshape channel。
3. 实际导出验证：glTF node、skin/joint、material、animation channel、bbox、主体骨骼覆盖。
4. 显式 fallback：container、目录名、资源名、游戏 profile。

fallback 必须在索引或报告里标注，不能混入默认绑定结果。

### 完整依赖图优先

精准导出可以使用 `--containers`、`--names` 过滤候选，但 CAB / PPtr 依赖图必须来自完整源目录。

不要为了样本变快而只复制少量 bundle 当输入。Unity 游戏常把脸、头发、附件、材质、Mesh、动画拆到外部 CAB，裁掉依赖源会导致模型缺件或引用断链。

### glTF 主格式

glTF/GLB 是默认主格式，用于模型、骨骼、材质、贴图和动画预览。

FBX 只作为兼容旧 DCC、对照验证或实验输出。默认正确性验证以 glTF 的节点、skin、material、animation channel、bbox 和 Unity 关系索引为准。

## 4. 默认 Library 形态

默认 `Library` 输出应像素材库：

- `Models/`：完整 prefab、Animator 或完整 GameObject 组合模型。
- `Animations/`：独立 AnimationClip 资产。
- `Textures/`：共享贴图库，模型目录可用硬链接引用，节省空间。
- `Materials/`：材质 JSON、Unity 原始属性和 glTF material extras。
- `LIBRARY_README.md`：素材库根目录的人工入口，解释目录结构和各索引文件用途。
- `ASSET_README.md`：每个模型目录下的人工使用入口，汇总基础信息、材质、动画候选、模块组装和注意事项。
- `MATERIAL_REPORT.md`：每个 glTF/GLB 模型目录下的人工可读材质说明。
- `asset_catalog.jsonl`：资产主索引。
- `model_animations.json`：模型和动画候选绑定索引。
- `animation_bindings.jsonl`：更细粒度动画绑定关系。
- `unity_relations.jsonl` / `unity_relation_summary.json`：Unity 原生关系图。
- `model_validation.json` / `preview_validation.json`：导出质量验证结果。
- `export_manifest.jsonl`：导出文件和源资源关系。
- `library_index.db`：可选 SQLite 素材库索引数据库，用于浏览、查询、调试和后续批量处理。
- `sqlite_index_summary.json` / `SQLITE_INDEX_README.md`：SQLite 索引摘要和人工说明。

SQLite 索引规则：

- 索引要全，导出要精。进入 SQLite 不代表默认导出、推荐使用或视觉验收通过。
- SQLite 应尽量记录 `asset_catalog.jsonl`、`unity_relations.jsonl`、`animation_bindings.jsonl`、`export_manifest.jsonl`、报告 JSON、模型/动画/模块索引和实际文件列表。
- 结构化字段用于常见查询，`raw_json` 用于保留原始信息，避免后续字段扩展时必须重新导出。
- 当前 SQLite v1 面向“已导出的 Library/AudioLibrary 目录”建库；后续可扩展为直接读取完整 Unity 源目录的 CAB/Object/PPtr 全量索引。
- 不允许为了缩小索引而切断 Unity 外部依赖关系；导出候选可以精简，Unity 关系索引必须完整可追溯。

SQLite 源索引规则：

- `unity_source_index.db` 面向完整 Unity 源目录，不导出素材，只记录源关系。
- 源索引必须按批加载、写库、清理，避免全量游戏建库时内存无止境增长。
- 源索引至少包含 `source_files`、`serialized_files`、`source_objects`、`source_externals`、`source_relations`、`source_animation_bindings`。
- 跨 bundle 关系不能依赖当前批次是否已加载目标对象；必须记录原始 PPtr 的 `fileID`、`pathID` 和 external `fileName/pathName`，避免分批加载切断 Unity 引用。
- 源索引是后续导出加速和调试底座，不替代 `model_validation.json`、`preview_validation.json` 和人工验收。
- 源索引全量构建必须保留 profile 日志。默认推荐 `--profile_log source_index_profile.jsonl`，用于拆分扫描、加载批次、写 SQLite、清理、创建 SQL 索引和摘要写出耗时。源索引不转 PNG；贴图转换和模型写出性能要在实际 Library 导出 profile 中分析。
- Library 导出 profile 必须保留贴图细分 stage：`model_texture_decode`、`model_texture_image_load`、`model_texture_flip`、`model_texture_encode_*`、`model_texture_write`、`model_texture_link`。后续讨论 native PNG、KTX2/BasisU、Raw/Reference 默认策略时，必须以这些日志数据为依据。
- 每次启用 profile 时必须同时生成 `profile_summary.json`，作为人工快速判断入口；JSONL 仍是机器分析和深挖单个资源瓶颈的原始数据。

音频素材库是独立 profile，不属于默认 3D Library：

- 短音效、按钮声、脚步、命中、技能、环境点缀等 `AudioClip` 可以作为开发素材保存。
- 默认 `Library` 不导出 `AudioClip`、`VideoClip`、`MovieTexture`，避免污染 3D 模型素材库。
- 需要音效时使用 `--mode AudioLibrary`，输出到 `Audio/SFX`、`Audio/Music`、`Audio/Voice`、`Audio/Other`。
- 每个音频旁边必须有 `.audio.json`，根目录必须有 `AUDIO_LIBRARY_README.md`，说明分类规则和统计。
- 当前允许先保存 `.fsb` 等原始音频数据；FMOD/native 转 WAV 作为后续批量转换阶段处理。`.audio.json` 必须记录 `convertedToWav=false` 或实际输出格式，不能假装已转码。
- `VideoClip` / `MovieTexture` 只属于显式媒体提取，不进入默认可用素材库。

默认 `Models/` 使用 `PrefabPrimary`：

- 只放 prefab、Animator 或完整 GameObject 组合模型。
- raw fbx 身体、face、附件等 source part 默认不作为可浏览模型导出。
- raw/source part 必须进入 `asset_catalog.jsonl`，标记为 `SourcePart`、`RawModel`、`AttachmentSource` 等。
- 没有被任何组合模型覆盖、但本身可用的 raw 模型可进入 `Models/RawUnreferenced`。
- 需要研究零散部件时，显式使用 `PrefabAndParts` 或 `RawPartsOnly`。

## 5. 模型、骨骼、模块

模型导出必须优先保证：

- mesh accessor、index、vertex buffer 自洽。
- material/texture 引用有效。
- SkinnedMeshRenderer 的 bones、bind pose、joint、inverseBindMatrices 有效。
- 模型 bbox 合理，不出现拉爆、缩小 100 倍、偏离异常等明显错误。
- prefab/Animator/GameObject 层级应尽量保留 Unity 原结构。

模块化角色默认保持模块化：

- body、face、hair、accessory、weapon、cape 等默认不强行永久合并。
- 索引记录哪些模块可以组合，关系依据优先来自 Unity 引用、同骨架 joint 映射、skin joint 可重定向关系。
- 浏览时生成 assembled preview glTF。
- 成品导出时允许用户选择 body + face + hair + accessory + animation，生成 assembled glTF/GLB。

不能把某个默认脸、默认头发或默认配件当作唯一正确版本硬塞进 Library。

## 6. 动画规则

默认规则：

- 模型保持干净。
- 动画独立入库。
- 通过索引说明每个模型支持哪些候选动画。
- 按需生成单模型 + 单动画预览 glTF。
- 批量确认后，再生成带动画合集的 glTF/GLB。

动画类型必须分开处理和标注：

- Humanoid/Muscle 身体动画。
- Generic/Transform 骨骼动画。
- BlendShape/表情动画。
- 非角色 Transform 动画。
- 材质、激活、事件类动画。

Humanoid/Muscle 动画作为可复用身体动画验收时，必须优先通过 Unity Editor 的 `Animator`、`Avatar`、`PlayableGraph` / `AnimationClipPlayable` 采样烘焙成目标骨架 TRS。内部近似 muscle 求解只能用于诊断和报告，不能作为最终正确动画导出路径。

动画候选必须严格验证：

- 有效动画应能驱动目标模型的可见 mesh、bone、blendshape 或 transform。
- 没有驱动可见对象的候选，宁可标为静态或待检查。
- 对短动作或定格动作，判断有效性时要同时比较采样姿态相对 rest pose 的变化和帧间变化。
- 不要因为 Humanoid bake 成功，就把表情、非角色、材质动画标为已验证。

## 7. 材质、贴图、shader

默认材质目标是“可用素材库预览材质”，不是完整 shader 复刻。

默认 glTF 应尽量标准化：

- base color texture。
- normal texture。
- alphaMode / alphaCutoff。
- doubleSided。
- 基础颜色、透明状态、裁剪状态。

同时必须保留 Unity 原始信息：

- `Material.m_SavedProperties`。
- Renderer 材质槽。
- Texture PPtr。
- shader 引用。
- float/color/texture slot。
- scale/offset。

无法标准表达的内容必须进入材质 JSON 和 glTF `extras.unityMaterial`。

每个模型目录必须尽量提供人工可读材质说明。`MATERIAL_REPORT.md` 用来解释材质状态、Unity 贴图槽、mask、需要特殊处理的原因和建议；机器可读数据仍以 glTF `extras` 和材质 JSON 为准。

每个模型目录还应提供 `ASSET_README.md` 作为人工使用入口。它汇总模型基础统计、材质状态、动画候选、模块/头发/头部/附件组装关系和使用建议；不要让使用者必须翻 `asset_catalog.jsonl`、`model_animations.json`、`character_assemblies.json` 才知道如何使用模型。

根目录应提供 `LIBRARY_README.md`，解释素材库里每类 JSON/JSONL/报告文件的用途。文件可以多，但职责必须清楚：人读入口少而稳定，机器读索引完整而可追溯。

ColorMask/Tint 管线规则：

- `_BaseColorMap`、`_ColorMask`、`_MaskMap`、Unity 材质颜色和 float 必须进入索引。
- 如果找到明确 tint 参数或 customization 配置，可以烘焙预览 base color。
- 如果只有 mask 和基础贴图，没有颜色配置，必须标记 `needsCustomizationTint`。
- 不能为了让模型看起来有颜色而硬猜游戏私有配色。

Texture2DArray 规则：

- `Texture2DArray` 常用于地形、地表、shader 采样、材质变体或运行时混合，默认视为独立贴图库/材质库资源。
- 默认模型 glTF 不强行嵌入数组贴图；普通 PBR 无法表达其 shader 采样逻辑时，必须保留引用和报告，而不是硬猜。
- 导出时按 AssetStudio 思路拆成 fake `Texture2DArrayImage` 层图，并写 metadata 记录 width、height、depth、GraphicsFormat、TextureFormat、源文件、PathID、stream offset/size 和每层状态。
- 如果材质引用数组贴图，`MATERIAL_REPORT.md` 应说明槽位、来源和后续需要 shader/customization 处理的原因。

Shader 默认作为实验功能：

- 默认不作为核心导出。
- 显式开启时保留 raw archive 和 metadata。
- 后续可基于 shader slot、材质参数和贴图关系做通用模板或预览烘焙。

## 8. 性能和全量导出

全量导出是目标，不是要回避的问题。

优化方向：

- 完整 CAB / PPtr map 一次构建、长期复用。
- 导出候选可以过滤，但依赖图必须完整。
- 模型、材质、贴图、Mesh、AnimationClip 做跨模型缓存。
- 静态模型导出彻底跳过动画转换。
- 模型导出和动画导出分阶段，避免默认重复做无关工作。
- 默认 PNG 贴图可读；必要时支持 raw/reference 和后处理转换。
- 日志要分阶段计时：mesh、skin、material、texture、animation、write、cleanup。
- 内存允许较大占用，但必须稳定释放，不能无止境叠加。

当命令慢时，应先看日志定位瓶颈，不要凭感觉删功能。

## 9. 开发和验证节奏

不要只靠全量导出验证功能。全量太慢，也不利于定位问题。

默认流程：

1. 选真实游戏小样本。
2. 用完整源目录建立依赖图。
3. 用 `--containers` / `--names` 精准导出少量候选。
4. 输出放到 `D:\Assets\Freedunk_Data_Dev` 下的独立目录。
5. 检查自动报告。
6. 必要时人工用 F3D、Blender、Unity 验证。
7. 小样本通过后，再扩展到多角色、多动画、多游戏。

当前交叉验证游戏：

- Freedunk：篮球人物、球、球场、篮筐、道具、Humanoid、BlendShape、非角色 Transform 动画。
- VRising：模块化角色、换装、头发、ColorMask/Tint、Generic/非标准骨架。
- Valheim：后续用于进一步验证通用 Unity 游戏入口、场景/道具/角色资源。

输出目录约定：

- 测试输出默认放 `D:\Assets\Freedunk_Data_Dev`。
- 不要把临时验证目录直接堆在 `D:\Assets` 根目录。
- 每个样本目录名应说明游戏、目标、版本，例如 `CrossGame_VRising_ColorMaskTint_V1`。

## 10. 质量门槛

模型静态验收：

- glTF 能打开。
- bbox 合理。
- mesh 不拉爆。
- skin joints 有效。
- 贴图引用有效。
- 材质至少可见；无法还原的 shader/tint 应有明确报告。

骨骼验收：

- 人形角色核心骨架视觉结构合理。
- skeleton hash、bone path、bind pose 自洽。
- Blender/F3D 显示差异不能直接作为算法事实，必须结合 glTF skin/joint 和 Unity 原关系判断。

动画验收：

- 动作自然，不明显扭曲、左右腿交叉、手臂反向、上下乱跳。
- glTF animation channel 目标有效。
- Humanoid 身体动画以 Unity bake 结果为准。
- 表情/BlendShape、非角色 Transform 动画分别验收。

材质验收：

- 标准贴图可见。
- alpha、cutout、double sided 不导致整体消失。
- ColorMask/Tint 资源被索引。
- 无法恢复配色时明确 `needsCustomizationTint`。

## 11. 处理问题的方式

遇到视觉错误、导出错误、构建失败或测试失败时：

- 先追根溯源找根因。
- 不要用局部反号、跳过检查、吞异常、硬编码兜底遮住问题。
- 暂时找不到原因时，记录已知事实和不确定点。
- 继续通过日志、断言、最小复现、对照工具或扩展样本追踪。
- 只有确认边界和影响后，才做临时兼容，并写清楚原因。

引入外部工具时：

- 可以参考 AssetStudio、AssetRipper、UnityGLTF、glTFast、Unity Editor、Blender 等成熟实现。
- 参考前先明确它解决的是解析、导出、导入、烘焙还是 DCC 兼容问题。
- 已验证不适合当前目标的实现要记录下来，避免反复绕回同一条路。

## 12. Git 和文档

- 每个重要变更都要有中文 git message。
- 改动影响项目准则时，同步更新 `AGENTS.md` 或本文档。
- 新增 CLI 行为时，同步更新 `CLI_USAGE.md`。
- 新增验证样本或流程时，同步更新 `DEV_SAMPLE_WORKFLOW.md`。
- 路线图、阶段完成度和待办放 `RESOURCE_EXPORT_ROADMAP.md`。

## 13. 给后续 AI/开发者的工作节奏

默认节奏：

1. 读代码和现有文档。
2. 说明影响文件、风险点和验证方式。
3. 做最小但完整的改动。
4. 构建。
5. 用真实小样本验证。
6. 输出报告目录和关键结论。
7. 需要人工判断视觉效果时，明确告诉用户看哪个文件、验证什么。
8. 不需要人工验证时继续推进，不要无意义停顿。

推进顺序建议：

1. 先保证模型、贴图、材质、骨骼稳定。
2. 再推进 Humanoid/Generic/BlendShape/非角色动画。
3. 再推进索引结构、批量性能和全量导出稳定性。
4. Shader 和高级材质效果放在后面，但原始信息从一开始就要保留。
