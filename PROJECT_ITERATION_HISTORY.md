# AnimeStudio 项目迭代复盘与交接文档

本文档记录 AnimeStudio 到当前阶段的目标演化、技术路径、经验教训、已解决问题和仍需推进的方向。它不是命令手册，也不是单纯开发计划；它的目的是让后续接手的团队成员或 AI 能快速理解：我们为什么走到现在这套架构、哪些坑已经踩过、哪些规则不能轻易改、下一步应该如何判断方向。

相关文档分工：

- `AGENTS.md`：最高优先级协作规则，AI 和团队成员都应遵守。
- `PROJECT_EXPORT_STANDARDS.md`：当前导出规范和验收准则。
- `CLI_USAGE.md`：命令、参数、工作流和排查说明。
- `RESOURCE_EXPORT_ROADMAP.md`：阶段路线图和功能规划。
- `INDEX_ARCHITECTURE_REVIEW.md`：索引体系分析。
- 本文档：历史脉络、经验复盘和交接说明。

## 1. 项目核心目标

AnimeStudio 当前核心目标是：

> 面向 PC 端 Unity 游戏，把已经打包过的资源还原成“完整可浏览、可筛选、可继续加工”的开发素材库。

这里的“素材库”不是完整 Unity 工程，也不是运行时对象转储。它应尽量包含：

- 模型：角色、NPC、道具、建筑、地形、场景、静态 Mesh。
- 骨骼：骨架层级、skin、bind pose、Avatar/HumanDescription。
- 贴图：模型贴图、材质贴图、地表贴图、Texture2DArray。
- 材质：标准 PBR 可表达部分、Unity 原始材质槽、参数、shader 引用。
- 动画：独立 AnimationClip、可按需 bake 成模型预览或成品。
- 音效：作为独立 AudioLibrary，不混入默认 3D Library。

当前最重要的产品原则已经从早期的“宁缺毋滥精品导出”调整为：

> 默认 Library 是完整可浏览素材库，不是最终精品包。导出阶段只过滤非常确定的垃圾或损坏对象；不确定但可能有用的素材优先保留、分类、标注和写报告。精品筛选应作为后处理能力。

这个调整来自 Humankind、Valheim、OldWorld 等游戏的交叉验证：成熟 Unity 游戏里大量建筑、植被、地形、POI、道具并不总是以完整 prefab 形式出现；如果默认只收高置信 prefab，会系统性漏掉真正有用的静态素材。

## 2. 工具选择与底层定位

最初对比过两个代码库：

- `D:\misutime\AnimeStudio`：Escartem/AnimeStudio 源码。
- `D:\misutime\AssetStudio`：aelurum/AssetStudio 源码。

结论：

- AssetStudio 在 Unity 资源解析、Texture2DArray、Animator/FBX 等方面有成熟参考价值。
- AnimeStudio 更适合作为我们定制“可用素材库导出器”的底层，因为我们能直接改 CLI、索引、glTF、材质报告、动画 bake、跨游戏验证流程。
- 后续遇到通用 Unity 类型解析问题时，应继续参考 AssetStudio / AssetRipper / UnityGLTF / Blender / Unity Editor 等成熟实现，不要闭门手写猜测。

当前默认主格式选择：

- `glTF/GLB` 是模型、材质、贴图、骨骼和动画预览的主格式。
- `FBX` 只作为兼容、对照验证或实验输出，不作为默认正确性基准。

原因：

- glTF 更开放、结构清晰，适合写 `extras` 保留 Unity 原始信息。
- FBX 在 Blender/Unity/DCC 间坐标、骨骼显示轴和材质表现差异较多，早期实验中导致模型缩放、骨骼显示和动画扭曲排查成本很高。
- 但 FBX 仍可作为某些工作流的兼容输出，尤其是用户明确需要 DCC 兼容时。

## 3. 目标演化路径

### 阶段一：从“选工具”到“3D 资源优先”

最初问题是：导出 Unity 游戏资源应选 AnimeStudio 还是 AssetStudio 做底层。

用户明确补充：主要导出 3D 游戏资源，几乎不涉及 2D。

因此早期方向确定为：

- 模型、骨骼、贴图、动画优先。
- UI、Video、Camera、Emoji、Sound 等不属于默认 3D Library。
- 音效后来被重新定义为“可以单独导出”，但不进入默认 3D Library。

### 阶段二：Freedunk 作为首个真实测试游戏

Freedunk 路径：

```text
C:\Program Files (x86)\Freedunk\Game
```

主要验证内容：

- 角色模型。
- 模型贴图。
- face/hair/accessory 模块化。
- Humanoid 身体动画。
- BlendShape 表情动画。
- 非角色 Transform 动画。

早期命令出现过输出目录为空、全量导出慢、日志噪声大、模型杂乱、角色缺脸、skin 拉爆、动画错绑等问题。Freedunk 让我们快速暴露了以下关键事实：

- Unity 依赖不能因 `--containers` 或局部输入被切断。
- 模型和动画不能默认全嵌。
- face/head/hair/accessory 很多时候是模块化资源，不能硬合并。
- Humanoid 动画不能靠简单曲线写入 glTF；要用 Unity Avatar/Animator 采样 bake。

### 阶段三：从“精品导出”转为“完整可浏览素材库”

最初我们倾向做 Core profile，尽量排除无关模型。

后来 Humankind 和 Valheim 暴露出重大问题：很多有效静态模型不是 prefab，也没有漂亮的容器路径；如果筛选过严，会漏掉大量建筑、植被、地形、岩石和 POI。

因此当前策略变为：

1. 第一层：完整素材库。
   只过滤确定垃圾，尽可能保留可用素材。

2. 第二层：分类和标注。
   debug box、VFX mesh、helper、decal、terrain tile、building part 等不确定资源不直接丢，优先分类、打标签、写报告。

3. 第三层：精品筛选。
   后续基于索引、报告、材质完整度、动画匹配度、人工标记生成 Curated/Core/Playable 子集。

这条原则必须继续坚持。不要在底层导出阶段为了目录干净而过早丢素材。

## 4. Unity 关系解构准则

项目当前最高技术准则：

> 尽可能所有逻辑都从 Unity 原始引用和结构而来。如无不得已的明确理由，绝不把单个游戏的目录习惯、角色命名或资源前缀作为核心绑定逻辑。

关系优先级：

1. Unity 显式引用。
   `PPtr`、AssetBundle 依赖、GameObject 组件、Animator、Animation、AnimatorController、AnimatorOverrideController。

2. Unity 结构兼容。
   Avatar、HumanDescription、SkinnedMeshRenderer.bones、bind pose、skeleton hash、AnimationClip binding path/type/property、blendshape channel。

3. 实际导出验证。
   glTF node、skin/joint、material、animation channel、bbox、主体骨骼覆盖。

4. 显式 fallback。
   container、目录名、资源名、游戏 profile。fallback 必须标注，不能伪装成 Unity 关系。

这个准则来自多个问题：

- Freedunk 里 face/head 可能在外部 CAB，如果输入被裁剪，模型就缺脸。
- VRising 模块化角色 body/head/hair/accessory 的组合关系不能靠名字硬猜。
- 动画只按骨架能套上会出现“弓模型播放喝酒/炸弹动作”这类语义错误。
- Humankind 静态 Mesh 如果只看 prefab 会漏资源，必须结合 container、source bundle、Renderer 使用关系。

## 5. 索引体系演化

### 早期 CAB map / AssetMap

早期依赖 CAB map 解决跨包引用。后来发现：

- 为了某次导出裁剪 map 会切断 Unity 外部引用。
- 后续新增动画、材质、Texture2DArray、StaticMesh 时频繁需要更完整关系。
- 只做轻量 map 不足以支持跨游戏、全量导出和后续 UI 查询。

### SQLite 源索引

当前方向是用 SQLite 做源索引底座：

```text
unity_source_index.db
```

目标：

- 全量记录 Unity 源目录的文件、对象、PPtr、external、组件、Renderer、Material、AnimationClip binding 等关系。
- 索引要尽量完整；导出要可控、可分类、可报告。
- 全量导出时优先使用 SQLite 源索引；没有则自动构建。

已总结出的索引原则：

- 索引是“磨刀”，可以耗时，但必须有持续日志、内存稳定、可追踪进度。
- 大包必须分批加载、写库、清理，不能把 1GB/6GB 包解析成几十 GB 内存后无响应。
- 跨 bundle 关系不能依赖目标对象是否在当前 batch；必须记录原始 PPtr fileID/pathID/external。
- 源索引阶段不应为了关系过度完整而把所有重对象全深解析到失控；但也不能因为怕慢就丢掉后续模型/材质/动画必要关系。正确做法是轻量关系解析、防御性 placeholder、持续 profile。

### 旧 JSON 索引仍然存在的原因

当前导出目录仍会生成：

- `asset_catalog.jsonl`
- `model_animations.compact.json`
- `model_animations.json`
- `skeletons.json`
- `animation_bindings.jsonl`
- `character_assemblies.json`
- `texture_library.json`

这些不是源索引的替代品，而是“已导出素材库索引”。它们面向素材浏览、后处理、UI、小工具和人工排查。

未来理想状态：

- `unity_source_index.db`：源 Unity 关系底座。
- `asset_catalog.jsonl` + compact JSON：导出结果和素材库浏览。
- `library_index.db`：可选，把导出结果 JSONL/JSON 再汇总进 SQLite 供 UI 查询。

## 6. 模型与静态 Mesh 的经验

### PrefabPrimary 不是全部

早期默认模型策略更偏向 prefab/Animator/GameObject 组合模型，避免零散 body/face/附件污染目录。

Freedunk 证明这对角色模型有效；但 Humankind 证明这会漏掉大量静态模型。

当前规则：

- prefab/Animator/GameObject 组合模型仍是主资源。
- 有明确 container/preload、静态素材来源语义、或 Renderer 使用关系的独立 Mesh，也应进入默认 `Models/`，标记为 `StaticMeshPrimary`。
- Renderer 材质关系能提高质量，但不是保留 Mesh 的硬条件。宁可导出灰模并标记缺材质，也不要漏掉可能是建筑、地形、道具的模型。

### 已遇到的问题

1. 模型拉爆或 skin 错乱。
   原因多与 skin/bind pose/骨骼层级处理有关。修复后 Freedunk Bill 等模型身材恢复正常。

2. 模型缺脸。
   原因可能是模块化 face、shader 渲染脸、或外部 CAB 引用被裁剪。最终策略不是强制合并，而是保持模块化并建立 assembly/preview。

3. FBX 缩放、骨骼显示异常。
   Blender 中 FBX 骨骼显示轴可能和 Unity/glTF 不一致；不应把骨骼锥体显示方向直接当作关节位置错误。最终主格式转向 glTF。

4. 静态环境漏很多。
   原因是过早只导 prefab，忽略直接 Mesh。已开始通过 StaticMeshPrimary、Renderer 关系和保守分类补足。

5. 材质缺失的灰模。
   当前允许导出，并在 `asset_catalog.jsonl`、glTF extras、README/MATERIAL_REPORT 标注 `missingRendererMaterial`、`needsRendererBinding` 或 `rendererMaterialUnresolved`。

## 7. 材质与贴图经验

### 贴图硬链接

默认 PNG 模式下，模型依赖贴图写入共享目录：

```text
Textures/_ModelDependencies
```

模型目录下 `Textures/` 尽量使用硬链接，节省空间并方便单模型查看。这是重要能力，不应回退。

### Raw / Reference / PNG

全量导出最耗时之一是贴图解码和 PNG 编码。已有 profile stage：

- `model_texture_decode`
- `model_texture_image_load`
- `model_texture_flip`
- `model_texture_encode_png`
- `model_texture_write`
- `model_texture_link`

当前默认仍是 PNG，因为目标是可浏览素材库。Raw/Reference 适合特殊快速扫描或后处理工作流。

### Texture2DArray

VRising 和 OldWorld 验证了 Texture2DArray 的必要性。

当前分类：

- `Textures/Texture2DArray`：可视数组贴图，例如 terrain/surface albedo、normal、mask。
- `Textures/DataTexture2DArray`：float/HDR/未知语义数组，常见于 shader/terrain 数据采样。

重要经验：

- DataTexture2DArray 转成 PNG 大多没有浏览价值，常像雪花。
- 当前默认只写 `.texture2darray.json` 和 catalog，不逐层写 PNG。
- 不能把数据数组的雪花图当作普通贴图解码失败。

### 普通 MaterialLibrary

当前 `_ModelDependencies` 是模型直接依赖贴图。

`MaterialLibrary` 用于独立材质明确引用的普通 Texture2D。多个游戏里它可能为空，这不一定异常，但说明全局普通贴图库覆盖仍需增强。

后续可基于 SQLite 的 Material->Texture 关系补更完整 MaterialLibrary，尤其是 terrain、surface、building material 贴图。

### ColorMask/Tint

VRising 暴露了身体灰色但关系完整的问题。

结论：

- 这不是简单贴图丢失。
- 很多游戏通过 `_BaseColorMap`、`_ColorMask`、`_MaskMap` 和 customization/tint 配置运行时着色。
- 当前默认不硬猜颜色；如果没有明确 tint，标记 `needsCustomizationTint`。
- 后续应继续做通用 ColorMask/Tint 管线，而不是完整复刻 shader。

## 8. 动画经验

### 模型和动画必须默认分离

Freedunk 早期把动画直接和模型导出到一起，导致：

- 单个 glTF 过大。
- 模型被大量无关动作污染。
- 不知道哪个动作是真正适配该模型。

当前规则：

- 默认 Library：模型干净，动画独立。
- 自动索引：说明每个模型有哪些候选动画。
- 按需预览：选中模型和动画后生成 baked preview glTF。
- 批量确认后：再生成带动画合集的 glTF/GLB。

### Humanoid 动画路径

最初尝试手写 Humanoid/Muscle 到 glTF 的转换，出现手脚扭曲、腿交叉、上下跳动等问题。

最终经验：

- Humanoid Avatar 本质是 Unity 的人体骨骼映射和 retargeting 系统。
- 手写 muscle 求解很容易错。
- 更可靠方式是让 Unity Editor 使用 Animator/Avatar/PlayableGraph 采样，把动画 bake 成目标骨架 TRS，再写回 glTF。

Freedunk 验证过一批动画：

- `DASH_01`
- `FACEUPMOVE_RUN_STANDARD_01`
- `HOLDBALL_01`
- `AIMSHOT_STANDING_01`
- `DRIVINGDUNK_01`
- `KNOCKDOWN_NORMALMOVE_01`
- `CEREMONY_CHEER_Bill_01_LOBBY`
- BlendShape 眨眼/张嘴样本

这些验证说明 Unity bake 路径是可行的。

### 动画语义匹配

VRising 后期暴露了新问题：

- 骨架能套上，不代表动作语义正确。
- 弓模型播放喝酒动作、武器拿反、道具动作错绑都属于“技术可播但素材库不合格”。

当前规则：

- 动画候选不能只看 skeleton hash。
- 还要看 Unity 显式引用、动画命名、武器/道具类型、挂点、Prefab 附件是否一致。
- 全量 Library 不做模型 × 动画巨大矩阵匹配；超过阈值时 defer。
- 后续应做“定向模型 + 语义过滤 + baked preview”命令或 UI 小工具。

## 9. 跨游戏验证经验

测试游戏与用途：

```text
C:\Program Files (x86)\Freedunk\Game
D:\BaiduNetdiskDownload\unity-VRising
D:\BaiduNetdiskDownload\unity-Humankind
D:\BaiduNetdiskDownload\unity-Valheim.Build.21981559
D:\BaiduNetdiskDownload\unity-Homura Hime
D:\BaiduNetdiskDownload\unity-Old World
```

### Freedunk

主要验证：

- 角色模型完整性。
- face/head 模块。
- Humanoid 动画 bake。
- BlendShape 表情。
- 非角色小动画。

教训：

- 不能只针对一个游戏写特殊规则。
- face 缺失不一定是导出错误，可能是 shader 或模块化。
- 动画必须先确认模型/骨骼/贴图稳定。

### VRising

主要验证：

- 模块化角色 body/head/hair/accessory。
- ColorMask/Tint 灰模问题。
- Texture2DArray 地表贴图。
- NPC/武器动画语义匹配。

教训：

- 不能强行组装模块作为默认模型；应输出推荐组合预览和 assembly 索引。
- 材质灰色可能是缺 tint，不是缺贴图。
- 武器/动作语义必须进入动画匹配。

### Humankind

主要验证：

- 大量静态 Mesh、建筑、环境、单位。

教训：

- 只导 prefab 会严重漏模型。
- StaticMeshPrimary 必须成为默认 Library 的一等资源。
- obsolete/deprecated 可明确过滤，但 test/preview 不应轻易过滤。

### Valheim

主要验证：

- 大量模型、环境、道具、性能和 GC。

教训：

- 过于频繁的阻塞 GC 会极大拖慢导出。
- `model_gc` 从 32 提高到更大批次后，性能明显改善。
- 不要静默过滤 `fx/vfx/sfx/spawner/helper` 等可能含可见几何的资源；应分类/标注。

### OldWorld

主要验证：

- 大包解析、sidecar/non-Unity 文件防御、Texture2DArray 数据数组。

教训：

- 大包可能 1GB 到 6GB，必须有中间日志和健康检测。
- Tiny/sidecar 文件可能导致解析器卡住或异常，需参考 AssetStudio 做防御性跳过。
- Float/HDR Texture2DArray 不是普通贴图，默认不应输出雪花 PNG。

## 10. 性能与稳定性经验

### 日志和 profile

默认应保留：

- `source_index_profile.jsonl`
- `export_profile.jsonl`
- `profile_summary.json`
- `export_run_summary.json`

原因：

- 全量导出耗时长，用户需要知道进度不是卡死。
- 需要区分是 source index、bundle load、object parse、texture decode、PNG encode、model write、GC 还是后处理索引慢。
- 不能在真实游戏跑几小时后发现没有可分析日志。

### 大包处理

OldWorld / Humankind 暴露了大包问题：

- 1GB+ bundle 不一定比很多小包快。
- 如果完整解析 MeshRenderer/SkinnedMeshRenderer 等重对象，可能长时间无输出且内存暴涨。
- 需要轻量关系索引、防御性 placeholder、批次写库、持续进度日志。

原则：

- 不因怕慢而丢必要关系。
- 不深解析到失控。
- 关系索引应尽量记录 PPtr 原始关系，后续按需解析重对象。

### GC 问题

Valheim 暴露：

- 每 32 个模型做一次阻塞压缩 GC 会把导出拖到数小时。
- GC 应作为保护机制，不应成为主耗时。
- `model_gc` 批次应更大，且 profile 必须能证明 GC 占比。

### 日志噪声

早期大量：

```text
GameObject xxx has no mesh, skipping...
```

刷屏导致用户看不到重点。

当前原则：

- 逐条无 mesh skip 应降到 Debug/Verbose。
- Info 只保留批次进度、导出摘要、重要 warning。
- 最后汇总 skip reason。

## 11. 已踩过的坑与解决方案

### 坑 1：局部输入切断外部 CAB 引用

现象：

- 角色缺脸、缺附件、缺材质。

原因：

- 模型在 A 包，face/mesh/material/texture 在 B/C 包，输入或 map 被裁剪。

解决：

- 源索引和依赖图必须基于完整输入目录。
- 导出候选可过滤，依赖闭包不可裁剪。

### 坑 2：全量动画嵌入模型

现象：

- 模型文件巨大、混乱、动画无关。

解决：

- 默认模型干净，动画独立。
- 按需生成 baked preview 或 animation pack。

### 坑 3：Humanoid 动画手写求解扭曲

现象：

- 手臂扭曲、腿交叉、上下跳动。

解决：

- 用 Unity Editor/Avatar/Animator bake。
- 手写 muscle 只用于实验，不作为默认正确输出。

### 坑 4：FBX 骨骼显示误判

现象：

- Blender 里骨骼锥体横着或垂直，看起来“不连”。

经验：

- Blender 的骨骼显示轴不等于 Unity 关节层级错误。
- 需要看 joint position、parent-child、skin/bind pose、动画驱动结果。
- glTF 更适合当前默认验证。

### 坑 5：过早精品过滤漏资源

现象：

- Humankind/Valheim 模型很少，环境和建筑缺失。

解决：

- 默认完整可浏览素材库。
- StaticMeshPrimary 纳入默认 Models。
- 只过滤确定垃圾。

### 坑 6：Texture2DArray 雪花图误判

现象：

- OldWorld 某些 Texture2DArray PNG 看起来像噪点。

原因：

- `R32G32B32A32_SFloat` / HDR / 数据数组不是普通贴图。

解决：

- 分为 `Texture2DArray` 与 `DataTexture2DArray`。
- Data 数组默认 metadata-only，不写 PNG layer。

### 坑 7：材质状态只在人读报告里

现象：

- 后续批量筛选灰模、缺 tint、缺材质绑定不方便。

解决：

- `asset_catalog.jsonl` 模型条目增加材质汇总字段：
  - `materialStatus`
  - `materialStatusCounts`
  - `materialNeedsCustomizationTint`
  - `materialMissingRendererBinding`
  - `materialHasBaseColorTexture`
  - `materialHasNormalTexture`
  - `materialImageCount`

## 12. 当前可认为基本成功的部分

截至目前，以下能力可以作为阶段性里程碑：

- 默认 glTF + PNG 模型导出。
- 模型依赖贴图硬链接共享。
- Prefab/Animator/GameObject 组合模型导出。
- StaticMeshPrimary 静态 Mesh 导出。
- 基础材质/PBR 映射和 Unity 材质 extras 保留。
- 模型旁 README / MATERIAL_REPORT。
- 独立 AnimationClip 库。
- Humanoid 动画 Unity bake 到 glTF 预览。
- BlendShape 表情动画样本。
- 模块化角色 assembled preview。
- Texture2DArray / DataTexture2DArray 分类。
- AudioLibrary 独立音效导出。
- SQLite 源索引基础。
- 跨游戏批量导出脚本和 profile 体系。

这些不代表产品完成，但说明“模型、贴图、骨骼、动画”主线已经走通。

## 13. 当前仍需重点推进

### P0：全局普通材质贴图库增强

现状：

- `_ModelDependencies` 和 `Texture2DArray` 已有。
- `MaterialLibrary` 在多个游戏中可能为空。

下一步：

- 从 SQLite 源索引恢复 Material->Texture2D 关系。
- 导出不是模型直接依赖、但由材质明确引用的普通 Texture2D。
- 分类为 terrain/surface/building/material/mask/normal/base map。

### P0：定向动画匹配工具

现状：

- 全量 Library 不做巨大矩阵匹配是正确的。
- `model_animations.compact.json` 候选为空可能是预期。

下一步：

- 实现“输入模型 -> 查可匹配动画列表”工具。
- 匹配规则：显式 Unity 引用、skeleton/avatar、binding path、武器/道具语义、挂点一致。
- 输出可生成 baked preview 或 animation pack。

### P0：StaticMesh 材质绑定覆盖

现状：

- Renderer-bound Mesh 可绑定材质。
- 找不到材质时导灰模并标注。

下一步：

- 提高 Renderer/Material 轻量关系索引覆盖。
- 对 submesh-material 槽位更精确。
- 对 terrain/building 材质做更强 slot 识别。

### P1：跨游戏分类质量

现状：

- 已有 Unit/Vehicle/Animal/Buildings/Environment/Prop 等通用分类。
- Unknown 可接受，但需要逐步减少明显可分类资源。

下一步：

- 保守增强通用词元。
- 不为单个游戏硬编码。
- 不能为了分类而误分类。

### P1：材质 ColorMask/Tint

现状：

- 能标记 `needsCustomizationTint`。
- VRising 身体灰色问题已定位为 tint/customization 管线缺口。

下一步：

- 索引 customization 配置。
- 识别 `_ColorMask`、`_MaskMap`、`_BaseColorMap`。
- 有明确颜色时 bake 预览贴图。

### P1：Source Index 健康检测

下一步：

- 对大包解析增加更细进度。
- 记录对象计数、写库计数、当前文件、当前对象类型、内存快照。
- 超长无写入时输出健康状态，而不是沉默。

## 14. 接手者开发节奏建议

继续推进时建议按这个循环：

1. 先明确本次目标属于哪条主线：
   模型、材质、贴图、动画、索引、性能、分类、音频。

2. 先找 Unity 关系来源。
   不要先按游戏名或路径猜。

3. 找 2 到 3 个跨游戏样本验证。
   Freedunk 不够，要用 VRising、Humankind、Valheim、OldWorld 交叉确认。

4. 小样本导出到：

```text
D:\Assets\Freedunk_Data_Dev
```

或批量验证目录：

```text
D:\Assets\AS-Assets
```

5. 生成报告和 profile。

6. 让用户验证可视结果。

7. 修文档和规范。

8. 提交 git。

不要一边改逻辑一边忘记文档。本项目很多规则来自真实素材验证，不写下来后续很容易重复踩坑。

## 15. 当前项目路径参考

源码：

```text
D:\misutime\AnimeStudio
D:\misutime\AssetStudio
```

常用测试游戏：

```text
C:\Program Files (x86)\Freedunk\Game
D:\BaiduNetdiskDownload\unity-VRising
D:\BaiduNetdiskDownload\unity-Humankind
D:\BaiduNetdiskDownload\unity-Valheim.Build.21981559
D:\BaiduNetdiskDownload\unity-Homura Hime
D:\BaiduNetdiskDownload\unity-Old World
```

常用输出：

```text
D:\Assets\Freedunk_Data_Dev
D:\Assets\AS-Assets
```

构建输出：

```text
D:\misutime\AnimeStudio\dist\net9.0-windows\bin\AnimeStudio.CLI.exe
```

推荐构建：

```powershell
.\build.ps1
```

默认全量 Library 示例：

```powershell
D:\misutime\AnimeStudio\dist\net9.0-windows\bin\AnimeStudio.CLI.exe `
  "D:\BaiduNetdiskDownload\unity-VRising" `
  "D:\Assets\AS-Assets\VRising-Assets" `
  --game Normal
```

## 16. 最重要的交接提醒

1. 不要回到“只导精品”的底层策略。
   当前默认 Library 是完整可浏览素材库；精品筛选是后处理。

2. 不要切断 Unity 外部引用。
   源索引必须基于完整源目录。

3. 不要把骨架兼容当成动画语义正确。
   武器、道具、挂点、显式引用都要参与动画候选。

4. 不要把灰模直接当作失败。
   可能是缺 Renderer 材质绑定、缺 tint、缺 shader/customization 管线；必须看 `MATERIAL_REPORT.md` 和 catalog 字段。

5. 不要把 DataTexture2DArray 的雪花当作坏贴图。
   数据数组默认 metadata-only，后续由 shader/terrain 管线解释。

6. 不要为单个游戏硬编码默认逻辑。
   如确实需要游戏 profile，必须显式开启并写入文档。

7. 不要让全量任务沉默运行很久。
   profile、summary、批次日志和健康检测是产品体验的一部分。

8. 所有改动都要考虑跨游戏。
   Freedunk 证明角色动画，VRising 证明模块化和 tint，Humankind/Valheim 证明静态资源覆盖，OldWorld 证明大包和数据数组。

## 17. 当前状态一句话

AnimeStudio 已经从“Unity 资源导出器”演化为“面向 PC Unity 游戏的完整可浏览素材库构建器”。模型、贴图、骨骼、动画主线已基本走通；下一阶段重点不是再证明能导出，而是提高跨游戏覆盖、材质/动画关系准确度、索引查询能力和全量导出的稳定性。
