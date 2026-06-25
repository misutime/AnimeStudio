# AnimeStudio 通用 Unity 资源导出规范

本文档是团队推进 AnimeStudio 的共同准则。无论由团队成员还是 AI 继续开发，都应先按这里的目标、边界、验收方式和节奏推进。

## 1. 核心目标

AnimeStudio 的核心目标是面向 PC 端 Unity 游戏，把已经打包过的资源还原成“可供开发者使用的素材库”。

可用素材库不是 Unity 运行时对象转储，也不是完整 Unity 工程恢复。它应该满足：

- 打开输出目录就能浏览尽可能完整、可用、可分类的资产。
- 模型、骨骼、贴图、材质、动画尽可能准确、可复用。
- 默认输出优先保留可能有用的素材；通过分类、标签、报告和后续筛选控制质量，而不是在底层导出阶段过早丢弃。
- 主线目标是真实游戏内使用、可复用的原始素材，例如正式角色、NPC、怪物、武器、道具、机关、建筑、场景和环境物件，不是把每个对话、UI、Timeline、剧情专用实例或预览实例都当成可用素材。
- 关系来源可追溯，能说明模型、模块、动画、材质、贴图来自哪个 Unity 引用或结构。
- 对无法完整还原的部分明确标注，不假装成功。

当前接受的工程取向：

- 面向 PC Unity 游戏。
- 默认优先 3D 资产，同时保留材质贴图、Texture2DArray、动画、音效等可用开发素材。
- 默认 Library 先专注 prefab/Animator 组合模型、贴图、材质、骨骼和动画，避免普通裸 Mesh、VFX metadata 和特效网格放大默认输出。
- 普通静态 Mesh 需要显式 `--include_static_meshes`；VFX 需要显式 `--include_vfx`。开启后才把这些扩展域纳入素材库、分类、打标签和写报告。
- 只在底层导出阶段过滤非常确定的垃圾或损坏对象，例如 collider、navmesh、socket、joint、bone、明显空对象、不可解析对象、明确 obsolete/deprecated 资源。
- Dialog、Timeline、UI、preview、deco、pose、camera、cutscene instance 等对话/UI/剧情专用实例或变体，默认只作为索引线索、关系诊断或显式扩展输出。除非能通过 Unity 显式引用证明它就是正式主模型，并且模型、材质、贴图、骨骼完整，否则不能作为默认 `Library` 主资源、生产验收样本或动画 smoke 通过证据。这类样本即使有骨骼、Avatar 或动画关系研究价值，也不能牵引主线修复优先级；白模、缺材质、缺件、只服务对话/UI 的不可用资源应先标注、隔离和降级，主线时间优先投入真实游戏内可复用模型与场景资源。遇到类似 Endfield 的剧情/Timeline 角色实例时，应先判断它是否属于正式可复用素材；如果材质链不完整或只用于剧情/UI 展示，不要为了让它通过验收而消耗大量主线修复时间。
- 资源分类必须通用、保守、可解释。优先使用 Unity 路径、container、Renderer 使用关系和跨游戏常见词元识别 Character、Unit、Vehicle、Animal、Buildings、Environment、Prop 等类别；不能为了降低 `Unknown` 数量而按单个游戏私有命名硬猜。信号不足时保留 `Unknown` 是正确结果。
- 精品筛选是后处理能力。`Core`、`Curated`、`Playable`、`ProductionReady` 等筛选层应基于 SQLite 索引、验证报告、模型尺寸、材质完整度、动画匹配度、命名语义和人工标记生成。

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

精准导出可以使用 `--containers`、`--names` 过滤候选，但 CAB / PPtr 依赖图必须来自完整源目录。默认 `Library` 不允许用 3D 路径过滤裁剪源文件列表；源文件只做 Unity-loadable/sidecar 防御过滤。

不要为了样本变快而只复制少量 bundle 当输入。Unity 游戏常把脸、头发、附件、材质、Mesh、动画拆到外部 CAB，裁掉依赖源会导致模型缺件或引用断链。

单模型/单动画预览、少量动画 decoded sidecar 刷新等定向诊断任务，可以显式使用 `--source_files` 缩小本次加载的源文件集合，并用 `--path_ids` 定位选中的 Unity 对象；但前提是输入 root 仍指向完整 Unity 源目录，并且使用完整 `unity_source_index.db` 提供依赖闭包。`--source_files` / `--path_ids` 不能参与默认模型-动画匹配，也不能替代全量源索引。

### glTF 主格式

glTF/GLB 是默认主格式，用于模型、骨骼、材质、贴图和动画预览。

FBX 只作为兼容旧 DCC、对照验证或实验输出。默认正确性验证以 glTF 的节点、skin、material、animation channel、bbox 和 Unity 关系索引为准。

### 模型优先

任何新游戏支持都必须先把模型本体导出做准，再讨论动画。模型本体包括 GameObject/Prefab 层级、Mesh、顶点/索引、UV、贴图、材质、Renderer 材质槽、骨骼、skin/bind pose、bbox 和可浏览分类。模型不完整、不准确、贴图或 UV 错乱、骨骼/skin 明显错误时，不能因为动画 sidecar 或预览部分成功就宣称游戏素材支持完成。

动画是建立在模型正确性之上的第二阶段能力。优先级应是：先稳定模型 + 贴图 + UV + 材质 + 骨骼，再做动画匹配、动画 glTF TRS/weights、预览和打包。

模型验收未通过的资源不得进入动画生产验收或动画 smoke。`model_validation.json`、glTF validator、静态截图或来源分类已经显示模型本体 error、明显缺件/破面、UV/skin/bbox 异常、主要材质贴图链路缺失，或来源属于 Dialog / Timeline / UI / cutscene / postmodel 等诊断样本时，只能保留为关系诊断；不能因为动画能生成、能播放或 `matchedTracks` 不为 0 就升级为动画样本。

材质贴图门禁不能只看 `materialCount`、`textureCount` 或 glTF 能否打开。多数可见 primitive 没有标准 `baseColorTexture`、截图呈白模/黑白 mask 块状/未复原 tint，或主要可见面仍是默认色时，即使 Mesh、skin、bbox 和 validator 没有 error，也只能算模型材质诊断样本，必须阻断动画生产验收。ColorMask/Tint 没有找到确定性配色前应保留 mask 和 sidecar，不要为了通过门禁硬猜颜色。

源索引层也要执行同一门禁。`modelDependencyHealth` 或 `materialRelationHealth` 显示 `meshFilter.mesh`、`skinnedMeshRenderer.mesh`、`animator.avatar`、`renderer.material`、`material.texture` 等确定性 PPtr 目标缺失时，说明当前模型闭包还不完整；应先补 CAB/VFS 依赖闭包、重建源索引并导出静态模型验证，不能把这类白模、缺件、缺 Avatar 或缺材质样本推进到动画合成或动画 smoke。优化层级、Humanoid 或 Skinned 模型依赖 Avatar 恢复骨骼时，Avatar 缺失属于模型第一阶段失败，不是动画阶段问题。`Animator.m_Avatar` PPtr 本身为空时不是 CAB 闭包问题，不能靠文件名或同容器其他 Avatar 硬补；除非后续能从 Unity 显式关系证明替代 Avatar，否则该模型只能降级为诊断样本或换真实可用主模型。

手动文件入口也不能绕过模型门禁。`--export_animation_gltf_from_files`、`--merge_animation_gltf`、单模型单动画 preview/cache、UnityBakeAccelerated 写回结果等命令，在生成可播放 glTF 前必须先检查模型 glTF 本身的 Mesh、UV、材质、贴图、skin 和 bbox。白模、无材质、无贴图、skin 绑定不完整或 glTF 静态结构为 warning/error 时，应写出 `model_not_animation_ready` 报告并停止生成动画产物；这类文件只能作为诊断样本保留，不能进入生产 smoke 或统一浏览器的可播放结论。

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

- 索引要全，默认 Library 要尽量完整，精品筛选后处理。进入 SQLite 不代表推荐使用或视觉验收通过；进入默认 Library 也不代表最终精品，只代表它可能是可用开发素材。
- SQLite 应尽量记录 `asset_catalog.jsonl`、`unity_relations.jsonl`、`animation_bindings.jsonl`、`export_manifest.jsonl`、报告 JSON、模型/动画/模块索引和实际文件列表。
- 结构化字段用于常见查询，`raw_json` 用于保留原始信息，避免后续字段扩展时必须重新导出。
- 当前 SQLite v1 面向“已导出的 Library/AudioLibrary 目录”建库；后续可扩展为直接读取完整 Unity 源目录的 CAB/Object/PPtr 全量索引。
- 不允许为了缩小索引而切断 Unity 外部依赖关系；导出候选可以精简，Unity 关系索引必须完整可追溯。

SQLite 源索引规则：

- `unity_source_index.db` 面向完整 Unity 源目录，不导出素材，只记录源关系。
- 全量 `Library` 导出必须使用 SQLite 源索引作为依赖底座。优先级固定为：显式 `--source_index`；输出目录 `unity_source_index.db`；输入目录 `unity_source_index.db`；都不存在时自动在输出目录构建。旧 CAB map / AssetMap 只作为显式 `--map_op` 调试或兼容旧流程，不能作为完整 Library 导出的默认路径。
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

默认 `Models/` 使用“完整可浏览模型资源”策略：

- prefab、Animator、完整 GameObject 组合模型是主资源。
- 有明确 Unity container/preload 路径、来源包本身具备强静态素材语义、或能通过 Renderer 使用关系证明可见的静态 Mesh，也是主资源，默认必须进入 Library。Renderer 材质关系能提高还原质量，但不是保留模型的硬条件。
- raw fbx 身体、face、附件等 source part 默认不作为可浏览模型导出。
- raw/source part 必须进入 `asset_catalog.jsonl`，标记为 `SourcePart`、`RawModel`、`AttachmentSource` 等。
- 没有被任何组合模型覆盖、但本身可用的 raw 模型可进入 `Models/RawUnreferenced`。
- 需要研究零散部件时，显式使用 `PrefabAndParts` 或 `RawPartsOnly`。

静态环境/建筑/道具 Mesh 是显式扩展能力，不是默认 Library 输出：

- 默认目标是优先导出 prefab/Animator 组合模型；需要完整覆盖可见几何模型时，显式使用 `--include_static_meshes`。
- 可以接受少量 LOD/部件级重复和少量 debug 基础体混入，前提是输出分类清晰、报告标注充分、文件名稳定可追溯。
- 仍然不能无脑导出全部 Mesh。非常确定的 collider、NavMesh、Occlusion、Socket、Joint、Bone、损坏对象和明确 obsolete/deprecated 资源应跳过或只进索引。
- 具备以下任一强信号、且几何数据完整的 Mesh，可升级为 `StaticMeshPrimary`：
  - 有明确 Unity `AssetBundle.m_Container` / preload 容器路径，且路径语义指向 environment/building/prop/world/stage/terrain/levelbuild 等可浏览素材。
  - 没有独立容器路径，但来源 AssetBundle / SerializedFile 本身具有明确静态素材语义，例如 `LevelBuildElements`、`Terrain`、`Environment`、`World`、`Building` 等。此类游戏常把大量场景/地形/建筑 Mesh 作为匿名 Mesh 存在，不能因为 Mesh 名为空就丢弃。
  - SQLite 源索引能证明该 Mesh 通过 `MeshFilter.mesh` 或 `SkinnedMeshRenderer.mesh` 被 Renderer 使用。这个信号来自 Unity PPtr 关系链，不是名称猜测；如果 Renderer 缺少或无法解析 `renderer.material`，仍应导出灰模并清楚标注缺材质绑定。
- 开启 `--include_static_meshes` 后，`StaticMeshPrimary` 输出 glTF，分类到 `Models/Environment`、`Models/Buildings`、`Models/Prop`、`Models/Stage` 等目录。
- 匿名且没有任何静态素材语义、碰撞、NavMesh、Occlusion、Socket、Joint、Bone、obsolete/deprecated 等 Mesh 只进入索引或被跳过，不进入默认 `Models/`。`Dummy`、`Decal`、`Shadow`、`SFX/FX/VFX`、helper 等名字不能单独作为静默丢弃理由；如果具备明确容器路径、强来源语义、Renderer 关系或可见几何，应分类或标注进入素材库。

### VFX Library

特效素材是显式扩展资源域，默认 Library 不导出 VFX。

- 只有显式 `--include_vfx` 时，`ParticleSystem`、`ParticleSystemRenderer`、`ParticleSystemForceField`、`LineRenderer`、`TrailRenderer`、`VisualEffect`、GPU Particle/VFX 对象才进入 `VFX/` 元数据索引。
- 只有显式 `--include_vfx` 时，通过模型/路径/命名识别出的 slash、impact、projectile、beam、trail、explosion、aura、skill 等 mesh 型特效才作为 VFX 处理；模型可作为 glTF 进入 `Models/`，但 `resourceKind` 应标注为 `VFX`。
- `VFX/vfx_library.json` 是全局机器索引，`VFX/VFX_LIBRARY.md` 是人工说明，每个特效目录下写 `vfx.json` 和 `VFX_REPORT.md`。
- 当前策略是 metadata + preview hints：记录 Unity 组件、来源、分类、mesh 预览和限制，并通过 TypeTree 轻量解析 `ParticleSystem` / `ParticleSystemRenderer` 的 Main、Emission、Shape、Size、Color、Trail、renderMode 等预览参数。源索引还必须记录 `ParticleSystemRenderer -> Material -> Texture`、`ParticleSystem/Renderer -> Texture` 等轻量 PPtr 关系，输出到 `textureRefCount` / `textures`，作为 Browser 近似预览和后续真实贴图采样的视觉证据。shader 动画、Texture Sheet 图集、动画事件触发、运行时 prefab 绑定没有完整还原时必须明确标注，不能伪装成可直接播放。
- `fx` / `vfx` / `sfx` / `effect` 不能作为底层过滤理由。若资源可见或有 Unity VFX 组件证据，优先导出或入索引；质量由分类、报告和后续筛选处理。
- 统一 `AssetLibrary v1` 浏览工具必须把 VFX 当作可浏览资产显示，而不是只把 `vfx.json` 当成数据文件。浏览器至少应提供 VFX 类型筛选、缩略图、组件/材质/贴图/Mesh 引用摘要、限制说明和近似动态预览；mesh 型特效优先使用真实 glTF 缩略图。旧 `AnimeStudio.LibraryBrowser` 只作为迁移期兼容入口，不再作为新能力设计目标。
- `ParticleSystem` / `VisualEffect` 不能用同一个程序化小圆点模板伪装成所有特效。完整 shader UV 动画、VFX Graph、动画事件和 prefab 运行时绑定未解析前，Browser 可以基于 `previewHints`、Unity 元数据、名称、分类、组件、材质、贴图和 Mesh 引用生成 approximate 预览，但必须明显标注“非 Unity runtime 100% 还原”，并尽量区分 trail、shockwave、aura、smoke、projectile、beam、distortion、mesh particle、ground plane、stretch billboard、textured billboard 等形态。
- VFX 字段扩展后，旧素材库不会自动拥有新的 `textureRefCount` / `textures` / 更完整 `previewHints`。要判断 VFX 质量，至少需要重建 `unity_source_index.db` 并重新生成 VFX catalog；完整批量重导不是绝对必要，但推荐在验证 VFX 功能时重新导出一次素材库，避免旧索引字段缺失导致预览退化。
- StaticMeshPrimary 应优先使用 SQLite 源索引恢复材质关系：`Mesh -> MeshFilter/SkinnedMeshRenderer -> GameObject/Renderer -> Material -> Texture2D`。命中时 glTF material 必须写入 `extras.animeStudioMaterial.status=boundRendererMaterial`，贴图进入 `Textures/_ModelDependencies`，模型旁 `ASSET_README.md` 记录选中的 Renderer 和 Material。
- 裸 Mesh 或 Renderer-used Mesh 没有 submesh-material 强绑定时，不要伪造材质关系。宁可导出默认灰模并在 `asset_catalog.jsonl`、glTF `extras` 与模型旁说明文件标记 `needsRendererBinding`、`missingRendererMaterial` 或 `rendererMaterialUnresolved`，也不要把可能是建筑、地形、道具的有效模型漏掉。
- 对匿名静态 Mesh，应使用来源包名 + PathID 生成稳定文件名，避免 `Mesh#123` 这类不利于排查的名字，也避免文件互相覆盖。

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

生产级动画导出从现在起只接受可直接复用的 glTF TRS/weights 动画数据。默认 Library、SQLite 索引和统一浏览器里的“可播放/可复用”结论，必须落到普通 glTF TRS 或 weights channel；不能要求使用者再进 Unity Editor bake 才能播放。Unity bake / Unity native Humanoid 可以作为求解手段，但它只是把 Humanoid/Muscle 采样成目标骨架 TRS 的 solver/oracle，最终交付仍必须是脱离 Unity 可播放的 glTF 动画数据，并保留求解来源、Unity 版本、Avatar、clip、采样率、耗时和验证状态。

压缩动画必须先找真实逐帧采样载荷，再谈求解。Endfield 样本已验证：`m_ValueArrayDelta` 数量可以和 binding 标量数量一一对应，但它不是逐帧 TRS 曲线；它只能作为 binding/value layout 证据。`m_AclCompressedBuffer.TransformBufferData` 里的 ACL `qvvf` transform tracks 可以直接还原已有 Transform TRS，但部分动作 clip 的主体人形骨骼不在这里，而在 `m_AclCompressedBuffer.FloatBufferData` 的 ACL scalar tracks 中，以 `AnimatorMuscle` / TDOF / RootT / RootQ 等 Humanoid/Muscle 曲线保存。遇到类似布局时，必须优先解压 ACL / streamed / dense / constant / float 等真实采样数据，再通过 `ProductionDirect` 或 `UnityBakeAccelerated` 把 Humanoid/Muscle 曲线求解到目标骨架 glTF TRS，不能把 ValueDelta 起止值硬插值成可播放动画。

所有直接写入 glTF 的 quaternion track 必须做连续性处理。ACL 或 Unity 序列化里相邻帧的四元数可能符号相反但表示同一旋转；写出前应按上一帧 dot product 翻转符号，避免 glTF 线性插值出现 180 度跳变。

直接解出 TRS 只说明动画有可写入 glTF channel 的逐帧数据，不等于这个 `AnimationClip` 可以单独作为完整身体动作验收。若 clip 是 additive、上层叠加、root/附件/头发表情片段、或需要同一 `AnimatorController` state 的 base layer / blend tree / override 组合，必须标注为需要 AnimatorController 上下文或视觉待验收；不能仅凭 `direct_trs`、channel 数、matchedTracks 或 `missingBones=0` 宣称动作可复用。生产级结论必须结合 Unity 显式关系、controller 上下文和 rest/mid/end 截图验证。

这里限制的是原神旧流程那类外部 Unity 程序重烘焙链路：用户需要新建游戏专用 Unity 工程、安装插件/helper、启动 Unity Editor/Player/批处理进程，再由我们的流程通过 Unity 工程、PlayableGraph、AnimationClipPlayable、导入 Avatar 或导入 AnimationClip 逐帧读 Transform。旧诊断命令可以继续用于研究和迁移历史库，但不能作为默认 Library 热路径，也不能不加标注地作为“支持新游戏”的交付路径。

Unity bake 结果必须分层标注：`UnityOracle` 用于准确性对照和内部 solver 回归；`UnityBakeAccelerated` 是显式高吞吐求解器，可调用统一 Unity 6 worker/native Humanoid 把 muscle/root pose 采样成目标骨架 TRS；`ProductionDirect` 是不依赖 Unity 进程的直接解析路径。旧 `requiresUnityBake`、`UnityBakeToGltf`、`baked=true` 或旧 bake cache 不能单独作为生产可播放证明；只有写出 glTF TRS/weights、通过主体骨骼覆盖和清晰视觉验收后，才能标为可复用。

当 request 显式提供 `unityAssetPaths.avatarAsset` 时，它表示这次求解必须使用从 Unity 原对象恢复并经 Unity 加载的 Avatar。Unity helper 必须直接加载该 asset；路径缺失、加载失败、`isValid/isHuman` 失败、或结果没有证明 rest pose 来源为 `imported_unity_avatar_asset` 时，必须失败或标为不可信诊断，不能退回 `BuildHumanAvatar`、AvatarConstant/internalSolver 或 glTF rest pose 继续伪装成生产结果。`BuildHumanAvatar` 只允许用于完整 `HumanDescription.humanBones + skeletonBones` 的普通路径，或明确标注的 oracle/诊断路径；它不能覆盖显式 Avatar asset 请求的失败。

原神这类历史依赖重 Unity bake 工程链路的支持不再作为新游戏默认动画目标。后续优化可以继续研究如何从 Unity 序列化动画数据本身直接恢复目标骨架 TRS，但不强求必须用内部 solver 取代 Unity native Humanoid。只要 `UnityBakeAccelerated` 能稳定、高吞吐、可复现地把确定性 Avatar/clip 数据采样成普通 glTF TRS/weights，并通过主体骨骼覆盖和视觉验收，就可以作为生产求解路径；不能要求用户维护游戏专用 bake 工程。VRising 这类能直接导出 Generic/Transform TRS 的游戏仍是直接路径参考样本。

旧 Unity bake 诊断仍必须只作用在 Unity 显式关系候选上，不能用来新增或猜测模型-动画绑定。AnimeStudio 内部 Humanoid/Muscle 求解器可以作为直接 glTF TRS 的可选实现和 Unity oracle 对照，但只能使用 Unity 原始曲线、Avatar/HumanDescription、ACL scalar/transform 等确定性数据；在主体骨骼覆盖、glTF channel 和清晰视觉验收通过前，只能标为实验或待验证。骨骼数量兼容、名称匹配、AvatarConstant 单独存在只能作为诊断信息；没有 glTF TRS/weights 输出和视觉验收前，不得进入 `model_animations.json` / SQLite 的默认可信推荐。

`AnimatorController` 对 `AnimationClip` 的显式引用，只说明这个 clip 参与了 Unity 状态机，不等于它一定是可单独播放的完整身体动画。有些角色私有 clip 只包含 root motion、附件、头发、衣摆、表情或上层叠加姿态，真正的身体动作来自同一状态的其他 layer、blend tree、override 或基础 clip。遇到这种 clip 时，Browser/CLI 必须标记为“需要 AnimatorController 上下文”或直接失败，不能生成看似成功但身体钻地、静态定格或动作语义不符的 glTF。生产级修复方向是恢复并采样原始 AnimatorController 状态、layer、blend tree 和 override 组合，而不是把单个不完整 clip 硬当完整动作。

从确定性关系临时生成的 single-state / multi-layer AnimatorController 只能作为诊断工具。它可以帮助确认 Avatar、clip 和 TRS 写回链路是否通，但不等价于游戏原始 RuntimeAnimatorController，也不能单独证明动作语义正确。只要采样模式是 generated controller，报告必须阻断生产可复用结论，至少标为需要原始 AnimatorController 上下文或清晰视觉失败/待验收。

动画候选必须严格验证：

- 有效动画应能驱动目标模型的可见 mesh、bone、blendshape 或 transform。
- 没有驱动可见对象的候选，宁可标为静态或待检查。
- 骨架/Avatar 能套上只是技术前提，不代表素材库语义合格。默认候选还必须检查 Unity 显式引用、动画命名语义、武器类型、道具类型、挂点和 prefab 附件是否一致。
- 带明确附件的模型必须拒绝冲突动作：例如 bow/crossbow 模型不能默认匹配 bomb、bottle、drink、sword 等动作；crossbow/bow/gun/sword 等动作应优先匹配同类附件或显式 Unity 引用。
- 如果只有结构兼容、但武器/道具/动作语义缺失或冲突，默认不进入推荐候选。需要研究时应通过显式预览命令人工强制选择，并在报告里标注为 fallback/人工验证。
- 对短动作或定格动作，判断有效性时要同时比较采样姿态相对 rest pose 的变化和帧间变化。
- 不要因为 Humanoid 内部求解或 Unity bake 对照成功，就把表情、非角色、材质动画标为已验证。
- `SkinnedMeshRenderer` 浮点曲线不等于 BlendShape。只有 Unity binding 明确是 BlendShape，或解析出的曲线属性是 `blendShape.*`，才能计入 `trueBlendShapeBindingCount` 并走 glTF `weights` 验收；材质、Renderer 属性、激活/显隐曲线必须单独标注、单独实现和单独验证。
- 全量 Library 不应做海量模型 × 动画组合搜索。结构候选匹配只服务于小样本/过滤导出；超过保守阈值时必须直接 defer，到定向预览、打包或 UI 查询阶段再按选中模型精确匹配。
- 默认 `model_animations.json` 和 SQLite 推荐候选必须来自 Unity 确定性关系，例如 `Animator`、`Animation`、`AnimatorController`、`AnimatorOverrideController`、PPtr 和源索引依赖图。骨骼数量、skeleton hash、binding path 兼容、Avatar 兼容、路径/名称语义只能作为诊断、过滤、验证或显式人工预览依据，不能作为默认模型-动画绑定关系。

### 统一素材浏览器

`AnimeStudio.LibraryBrowser` 进入废弃迁移期。新素材库应优先兼容统一 `AssetLibrary v1` 协议：根目录写小型 `asset_library.json`，浏览器机器数据写 `library_index.db`，正式模型/贴图/材质/动画资产和 `.asset_browser_cache/` 预览缓存分开。导出器可以保留 AnimeStudio 自己的 JSON/JSONL 诊断文件，但跨工具浏览、验收和后处理应以统一 SQLite schema 为准。

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

特殊 shader、分层材质或游戏私有非标准 PBR 管线必须明确降级。若材质槽中出现 `_IrisDiffuseTex`、`_IrisNormalTex`、`_IrisSpecTex`、decal、wrinkle 等需要游戏 shader 分层合成、混合遮罩或运行时参数计算的字段，而当前 glTF PBR 预览无法确定性复刻，不能伪装成已恢复的标准 PBR，也不能简单归类为贴图丢失或材质错绑。应标记 `unsupportedShader`、`customShaderRequired`、`layeredMaterialUnresolved` 或同等状态，并在报告、材质 sidecar、`material_sidecars` 和验证索引中保留原始 shader 引用、关键贴图槽、已导出贴图、未复刻步骤、降级预览状态和置信度。允许生成 best-effort PBR 预览，但必须说明它不是原游戏最终视觉效果；除非该问题大范围影响核心样本可识别性或暴露确定性关系错误，否则记录风险后继续推进模型、贴图、索引、动画和验证流程。

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
- 默认 Library 必须导出材质/地表贴图库：模型显示贴图进入 `Textures/_ModelDependencies`，材质明确引用的 `Texture2D` 进入 `Textures/MaterialLibrary`，可视 `Texture2DArray` 进入 `Textures/Texture2DArray`，float/HDR/未知语义数组进入 `Textures/DataTexture2DArray`。
- `Textures/DataTexture2DArray` 默认只保留 metadata/catalog，不逐层写 PNG。它不是最终 PBR 贴图；如果显式生成诊断预览，图片可能看起来像雪花或数据噪声。这类资源应通过 `.texture2darray.json`、材质参数、terrain/customization 配置或 shader 管线解释，不能简单判定为贴图损坏。
- 全局贴图库的来源必须来自 Unity 引用或结构，例如 `Material.m_SavedProperties.m_TexEnvs`、`Texture2DArray` 对象本身、后续 terrain/customization 配置。不要为了“全”而默认导出未被材质引用的 UI、图标、视频帧等噪声贴图。
- 默认模型 glTF 不强行嵌入数组贴图；普通 PBR 无法表达其 shader 采样逻辑时，必须保留引用和报告，而不是硬猜。
- 导出时按 AssetStudio 思路拆成 fake `Texture2DArrayImage` 层图，并写 metadata 记录 width、height、depth、GraphicsFormat、TextureFormat、源文件、PathID、stream offset/size 和每层状态。
- 如果材质引用数组贴图，`MATERIAL_REPORT.md` 应说明槽位、来源和后续需要 shader/customization 处理的原因。

Catalog 材质汇总规则：

- `asset_catalog.jsonl` 的每个模型条目必须尽量包含 `materialStatus`、`materialStatusCounts`、`materialNeedsCustomizationTint`、`materialMissingRendererBinding`、`materialHasBaseColorTexture`、`materialHasNormalTexture` 和 `materialImageCount`。
- 单模型 `MATERIAL_REPORT.md` 负责给人解释细节；catalog 字段负责批量筛选灰模、缺 tint、缺 Renderer 材质绑定或贴图不完整的模型。
- StaticMeshPrimary 和 Prefab/Animator/GameObject 模型应使用同一套顶层材质状态字段，避免后续工具为了不同来源写两套筛选逻辑。

Shader 默认作为实验功能：

- 默认不作为核心导出。
- 显式开启时保留 raw archive 和 metadata。
- 后续可基于 shader slot、材质参数和贴图关系做通用模板或预览烘焙。

## 8. 性能和全量导出

全量导出是目标，不是要回避的问题。

优化方向：

- 完整 CAB / PPtr map 一次构建、长期复用。
- 只过滤确定垃圾；不确定候选优先分类、标注和报告，避免为了短期干净输出牺牲全量覆盖。
- 模型、材质、贴图、Mesh、AnimationClip 做跨模型缓存。
- 静态模型导出彻底跳过动画转换。
- 模型导出和动画导出分阶段，避免默认重复做无关工作。
- 默认 PNG 贴图可读；必要时支持 raw/reference 和后处理转换。
- 日志要分阶段计时：mesh、skin、material、texture、animation、write、cleanup。
- 内存允许较大占用，但必须稳定释放，不能无止境叠加。
- 候选数量、跳过数量和 `model_gc` 总耗时本身就是质量指标。默认 Library 可以降级非常明确的 placeholder/debug/dummy/camera/light/audio helper，但不能仅凭 `sfx_`、`fx_`、`vfx_`、`Spawner_`、`Fader_`、`Armature`、`mixamorig:*` 这类名称全局排除；这些对象在不同 Unity 项目中可能承载有效 mesh、skin、skeleton、特效网格、projectile 或场景组件。噪声应优先通过资源分类、报告标注、显式 profile 或后续 UI 筛选解决，不应静默丢失潜在有效模型。
- 模型级 GC 不能成为主要耗时。全量导出默认关闭模型循环内 GC，依赖 batch 结束时的 `clear_batch` 释放 AssetManager、reader、resource stream 并做完整清理；只有在明确内存风险时才显式设置 `--model_gc_interval` 开启轻量非阻塞 GC。profile 中如果 `model_gc` 接近或超过 `model_convert`，优先优化 GC 策略和候选筛选，而不是先优化 PNG 或 glTF 写出。

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
- Humanoid 身体动画必须以最终写出的 glTF TRS 和 glTF channel 验证为准；Unity bake 可以作为求解器或 oracle，但结果必须脱离 Unity 可播放，并带求解来源、性能和视觉验收报告。
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
- 改动影响项目准则时，同步更新 `../AGENTS.md` 或本文档。
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
