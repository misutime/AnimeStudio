你是我的项目内编程助手。先不要急着写代码。

开始任何资源导出相关开发前，先阅读 `docs/PROJECT_EXPORT_STANDARDS.md`。它是本项目“可用素材库”目标、Unity 关系解构、glTF 主格式、动画/材质/验证节奏的团队总规范。`AGENTS.md` 里列的是必须硬遵守的执行约束；如果两者冲突，以更严格、更保护 Unity 原始关系和可用素材库目标的一方为准。

请按这个顺序工作：

1. 先阅读相关代码，理解当前实现
2. 给出简洁的修改计划
3. 标出会受影响的文件、风险点和不确定点
4. 在我确认或在计划足够明确后，按最小改动原则实现
5. 修改后给出验证步骤，包括测试、构建或手动复现方法
6. 最后总结本次变更，如果你认为必要把经验写入 AGENTS.md / CLAUDE.md, 请提出

要求：

- 优先复用现有实现
- 不要臆造不存在的接口、配置、测试结果
- 发现信息不足时，明确说明不确定
- 遇到异常、视觉错误、交互错误、构建失败或测试失败时，先追根溯源找根因，不要用局部反号、吞异常、跳过检查、硬编码兜底等方式把问题遮住。暂时找不到原因时，直接说明已知事实和不确定点，然后继续通过加日志、加断言、做最小复现场景或扩展测试来追踪；只有确认边界和影响后，才做临时兼容或兜底，并写清楚为什么需要这样做。
- 输出尽量简洁，先给结论，再给依据

编码相关要求:

- 注释等可读性文本以中文为主，保持简洁、通俗、口语化
- 每次 Git Message 必须使用中文；标题尽量简洁，正文尽可能把改动内容、原因和影响写清楚。
- 默认每一步实现都补必要的中文注释，重点说明职责、数据流、关键判断和为什么这么做
- 命名优先用通俗直白、对初学者友好的词；能叫 `grid/网格` 就不要硬叫 `topology/拓扑`
- 如果一个概念当前实现很简单，命名和注释也跟着简单，不要为了以后可能扩展提前引入太工程化、太抽象的术语
- 默认优先“尽量直白、少包装”，先写初学者一眼能看懂的代码
- 只有在确实能减少重复、降低出错、或者明显保护边界时，才加一层封装
- 小功能先用最直接的变量、函数和流程表达，不要为了“结构整齐”硬拆很多类、层、接口
- 像宽高、半径这类简单兜底，优先在真正使用它们的入口处处理，不要为了省一行 `Mathf.Max(...)` 把配置类包复杂
- 一个类如果拆成多个文件，前提应该是职责已经明显分成两块，而且拆完确实更好读；不要为了炫技巧默认上 `partial`
- 新增文件或模块时，先问自己一遍：这次拆分是不是让阅读路径更短、更直白；如果不是，就继续用更简单的写法
- 能直接复用现有数据结构和流程时，不要再包一层“看起来更通用”的中间层

项目经验:

- 本项目的核心目标是把 PC 端 Unity 打包资源还原成“可供开发者使用的素材库”。后续每一步实现都必须服务这个目标：模型、骨骼、贴图、材质、动画要尽可能准确、可浏览、可复用；特殊调试、Raw 扫描、旧式全嵌动画等行为必须用显式参数开启。
- 默认 `Library` 是“完整可浏览素材库”，不是最终精品包。工具应优先保留可能有用的模型、贴图、材质、动画、音效等素材，只在底层导出阶段过滤非常确定的垃圾或损坏对象，例如 collider、navmesh、socket、joint、bone、明显空对象、不可解析对象。debug box、VFX mesh、helper、decal、terrain tile、building part 等不确定对象优先导出、分类、打标签和写报告，不要过早静默丢弃。`Core` / `Curated` / `Playable` / `ProductionReady` 等精品筛选应作为基于索引和验证报告的后处理能力。
- 资源分类必须是通用且保守的。可以用跨游戏常见路径和词元把模型归为 Character、Unit、Vehicle、Animal、Buildings、Environment、Prop 等，但不能为了减少 `Unknown` 而按单个游戏私有命名硬猜；信号不足时保留 `Unknown`。
- 导出关系必须来自 Unity 自身的通用序列化引用和结构。默认逻辑的核心目标是解析 Unity 关系和引用；如无不得已的明确理由，绝不按单个游戏、目录、角色名、资源名前缀自行推断关系。
- Unity 关系优先级固定为：1. 显式引用，包括 `Animator`、`Animation`、`AnimatorController`、`AnimatorOverrideController`、`PPtr`；2. 结构兼容，包括 `Avatar`、`HumanDescription`、`SkinnedMeshRenderer bones`、`AnimationClip binding path/type/property`、blendshape channel；3. 实际导出验证，包括 glTF channel、skin/joint、主体骨骼覆盖、bbox。container、目录名、资源名、游戏 profile 只能作为显式标注的 fallback，不能进入默认绑定结果。
- 精准导出时可以用 `--containers`、`--names` 过滤导出候选，但 CAB / PPtr 依赖图必须来自完整源目录。不要为了样本变快而只复制少量 bundle 当输入；Unity 游戏常把脸、附件、材质或 Mesh 拆到外部 CAB，裁掉依赖源会导致模型缺件。
- 默认 `Library` 模型来源使用“可用模型主资源”策略：prefab、Animator、完整 GameObject 组合模型，以及有明确 Unity container/preload 路径、或来源包本身具有 environment/building/terrain/levelbuild/world/stage 等强静态素材语义的静态 Mesh，都属于默认主资源。raw fbx 身体、face、附件等 source parts 默认不作为可浏览模型导出，但必须进入 `asset_catalog.jsonl`，标记为 `SourcePart` / `RawModel` / `AttachmentSource`。只有显式 `--model_source PrefabAndParts` 或 `RawPartsOnly` 时才导出零散部件；没有被任何组合模型覆盖的 raw fbx 可放入 `Models/RawUnreferenced`。
- 静态环境/建筑/道具 Mesh 是默认 `Library` 的一等资源，不是次要补丁；但不能默认导出没有任何静态素材语义的匿名 Mesh、碰撞、NavMesh、Occlusion、Dummy、Socket、Joint、Bone 或 obsolete/deprecated 资源。`Decal`、`Shadow`、`SFX/FX/VFX` 等可能是有效可见素材时，优先分类、标注或报告，不要仅凭名字静默丢弃。Renderer 使用关系足以让静态 Mesh 进入默认 Library；材质缺失或未解析时导出灰模并标记 `missingRendererMaterial` / `needsRendererBinding`，不能伪造材质关系，也不能因为缺材质就漏掉可能是建筑、地形、道具的有效模型。匿名静态 Mesh 要用来源包名 + PathID 生成稳定文件名，不能让 `Mesh#123` 这类临时名成为素材库入口。
- VFX 是默认 `Library` 的一等资源域。粒子、拖尾、线渲染、VisualEffect、GPU Particle 和 mesh/shader 型特效应进入 `VFX/` 元数据索引或 `Models` 中的 `resourceKind=VFX`，不要因为名称包含 `fx` / `vfx` / `sfx` 就静默过滤。当前策略是 metadata-first：先记录 Unity 组件、来源、mesh 预览和限制；没有完整还原 ParticleSystem 模块、shader 动画或动画事件时，必须在 `vfx.json` / `VFX_REPORT.md` 明确标注，不能伪装成已完整可播放。
- glTF/GLB 是模型、骨骼、材质和动画预览的主格式；FBX 只作为兼容旧流程、特定 DCC 或对照验证的可选/实验输出。默认正确性验证以 glTF 的节点、skin、material、animation channel、bbox 和 Unity 关系索引为准，不以 FBX 导入器表现作为核心基准。
- 模型和动画的默认规则是：模型保持干净，动画独立入库，绑定关系通过 Unity 关系图、索引、预览验证和显式打包建立。不要默认把一个模型可能引用到的所有动画塞进 glTF/GLB。
- Humanoid/Muscle 动画需要作为可复用身体动画验收时，必须优先通过 Unity Editor 的 `Animator`、`Avatar`、`PlayableGraph`/`AnimationClipPlayable` 采样烘焙成目标骨架 TRS。AnimeStudio 内部的近似 muscle 求解只能用于诊断和报告，不能作为最终正确动画导出路径。
- Humanoid bake 复建 Avatar 时必须尽量使用 Unity 原始 `HumanDescription.skeletonBones` 参考姿态、`humanBones` 映射和 twist/stretch 参数。`humanBones` 只能说明人体部位对应关系，不能单独作为动画正确性依据；旧索引缺少完整 Avatar 元数据时应失败或重新导出索引，不能退回纯名称/当前姿态猜测。
- Unity bake 判断 Transform track 是否有效时，必须同时比较采样姿态相对原始 rest pose 的变化和帧间变化。很多短动作或定格姿态 clip 第一帧已经是目标姿态，但帧间没有变化；只比较第一帧和后续帧会把这类有效动画误判为空。
- 材质和 shader 还原也必须从 Unity `Material.m_SavedProperties`、Renderer 材质槽、贴图 PPtr、shader 引用和渲染状态出发。默认导出应优先把 Unity 的透明、裁剪、双面、基础贴图槽映射到 glTF 标准；无法标准表达的自定义 shader slot/float/color 必须保留在材质 JSON 和 glTF `extras.unityMaterial`，作为后续材质重建或贴图烘焙依据，不能按单个游戏名称硬猜。
- ColorMask/Tint 属于“可用素材库预览材质”管线，不是完整 shader 复刻。默认逻辑必须索引 `_ColorMask`、`_MaskMap`、`_BaseColorMap`、Unity 材质颜色/float 和渲染状态；只有找到明确 tint 参数或后续 customization 配置时才自动烘焙预览 base color。找不到颜色配置时保留贴图和 mask，并在 glTF `extras.animeStudioMaterial` 标记 `needsCustomizationTint`，不要为了看起来有颜色而硬猜游戏私有配色。
- 索引策略固定为“索引要全，默认 Library 要尽量完整，精品筛选后处理”。SQLite `library_index.db` 是可复用素材库索引底座，应尽量保留 Unity 关系、导出 manifest、asset catalog、报告 JSON 和 `raw_json`；进入索引不代表推荐使用，但默认素材导出也不应因为置信度不够高就大量漏掉成熟游戏里的建筑、植被、POI、岩石、地形块、道具或可见特效网格。质量控制优先通过分类、标签、报告、验证状态和后续筛选层完成。
- SQLite 分两类：`library_index.db` 面向已导出的素材库目录，`unity_source_index.db` 面向完整 Unity 源目录。源索引必须记录 source file、SerializedFile、Object、external CAB/PPtr、核心 Unity 关系和 AnimationClip binding；全量 Library 导出必须使用 SQLite 源索引作为依赖底座。源索引解析优先级固定为：显式 `--source_index`；输出目录 `unity_source_index.db`；输入目录 `unity_source_index.db`；都不存在时自动在输出目录构建。旧 CAB map / AssetMap 只作为显式 `--map_op` 调试或兼容旧流程，不能作为完整 Library 导出的默认机制。
- 全量导出性能问题必须用 profile 数据判断。候选数量、跳过数量、`model_gc` 总耗时、批次加载/清理耗时都是质量指标；默认 Library 可以降级非常明确的 placeholder/debug/dummy/camera/light/audio helper，但不能仅凭 `sfx_`、`fx_`、`vfx_`、Spawner/Fader、`Armature`、`mixamorig:*` 这类名称全局排除，因为它们在不同 Unity 项目中可能承载有效 mesh、skin、skeleton、特效网格、projectile 或场景组件。模型循环内 GC 默认关闭，依赖 batch 结束的 `clear_batch` 做完整清理；只有明确内存风险时才显式开启轻量非阻塞 `--model_gc_interval`。不能让 GC 成为主要耗时。
- 角色、NPC、道具、机关、场景物件都可能有动画。动画适配逻辑必须基于 Unity component/controller/clip binding/avatar/bone path 等通用关系，不能只服务角色动画。
- 表情/BlendShape、legacy AnimationClip、非角色 Transform 动画、材质/激活/事件类动画要和 Humanoid 身体动画分开标注、分开验证。不能因为 Humanoid bake 成功，就把这些动画类型默认标成“可播放已验证”。
- 默认动画候选不能只靠“骨架能套上”。结构兼容或 Humanoid 兼容之后，还必须检查 Unity 引用、动画命名语义、武器/道具/挂点/Prefab 附件是否一致；例如带 bow/crossbow 的模型不能默认匹配 bomb/bottle/drink/sword 等不一致动作。语义冲突时必须拒绝或显式标注为人工强制预览，不能进入默认 `model_animations.json` 推荐结果。
- 如果确实需要针对某个游戏做特殊适配，必须默认关闭或放入 profile/config，并在文档里标明它是游戏 profile 规则，不是 `Normal` 通用 Unity 导出路径的一部分。
