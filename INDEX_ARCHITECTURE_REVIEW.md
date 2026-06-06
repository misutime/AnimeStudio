# AnimeStudio 索引体系分析与生产标准复查

本文档用于团队同步 AnimeStudio 当前索引体系的职责、代码路径、完整度、性能边界和生产化状态。结论先行：

- 当前索引方向是正确的：`unity_source_index.db` 负责完整 Unity 源关系，`library_index.db` 负责导出后素材库查询。
- 当前实现已经达到“可用于真实游戏全量导出的 1.0 基线”，但还不应称为“索引驱动的完全生产级导出器”。
- 生产级下一步重点不是再加过滤，而是让导出器更多直接消费 SQLite 关系，减少重复批加载和 JSON 中间索引压力。

## 1. 我们为什么需要索引

Unity 游戏资源通常不是“一个模型一个文件”：

- 模型 prefab、Mesh、Material、Texture、Avatar、AnimationClip 可能分散在多个 bundle / CAB / SerializedFile。
- 一个 GameObject 的 `SkinnedMeshRenderer.m_Mesh` 可能指向外部文件。
- 材质和贴图可能通过 `Renderer.m_Materials`、`Material` 参数、Texture2D/Texture2DArray 等多层关系间接连接。
- Animator/AnimationClip、Avatar、骨骼、blendshape 和 transform binding 也可能跨文件。

如果导出时只加载被 `--containers` 命中的少数 bundle，就容易出现：

- 模型没脸、没头发、少附件。
- SkinnedMeshRenderer 找不到 mesh。
- 材质/贴图断链。
- 动画和模型只能靠名字猜。

所以项目原则是：

**索引要全，导出要精。**

索引阶段尽量完整记录 Unity 源关系；导出阶段再根据“可用素材库”规则严格选择默认输出。

## 2. 两类 SQLite

### `unity_source_index.db`

这是“源索引”，面向完整 Unity 游戏目录。

用途：

- 记录所有可加载 Unity 源文件。
- 记录每个 SerializedFile。
- 记录 Object 基础信息：type、name、pathID、byte range。
- 记录 external CAB/PPtr 信息。
- 记录核心关系：GameObject/Transform/Animator/Animation/MeshFilter/Renderer/SkinnedMeshRenderer/AssetBundle/ResourceManager 等。
- 记录 AnimationClip binding 摘要。
- 给全量 Library 导出提供依赖闭包，避免跨 bundle 引用被切断。

主要代码：

- [SQLiteSourceIndexBuilder.cs](/D:/misutime/AnimeStudio/AnimeStudio.CLI/SQLiteSourceIndexBuilder.cs:16)
- [SQLiteSourceIndexRuntime.cs](/D:/misutime/AnimeStudio/AnimeStudio.CLI/SQLiteSourceIndexRuntime.cs:11)
- [Program.cs](/D:/misutime/AnimeStudio/AnimeStudio.CLI/Program.cs:363)

主要表：

- `metadata`
- `source_files`
- `serialized_files`
- `source_objects`
- `source_externals`
- `source_relations`
- `source_animation_bindings`

当前自动使用顺序：

1. 显式 `--source_index`
2. 输出目录 `unity_source_index.db`
3. 输入目录 `unity_source_index.db`
4. 都没有时，默认 `Library` 自动在输出目录构建

如果 `unity_source_index_summary.json` 记录了 failed batch，导出会拒绝继续。这是正确的：半坏索引不应用于精品全量导出。

### `library_index.db`

这是“素材库索引”，面向已经导出的 Library / AudioLibrary 目录。

用途：

- 把 `asset_catalog.jsonl`、`unity_relations.jsonl`、`animation_bindings.jsonl`、`export_manifest`、各类 summary/report 文件收进 SQLite。
- 方便后续做 UI、小工具、查询、筛选和打包。
- 不替代源索引；它记录的是“导出后的素材库状态”。

主要代码：

- [SQLiteLibraryIndexBuilder.cs](/D:/misutime/AnimeStudio/AnimeStudio.CLI/SQLiteLibraryIndexBuilder.cs:11)

主要表：

- `assets`
- `unity_assets`
- `unity_relations`
- `animation_bindings`
- `export_manifest`
- `json_documents`
- `files`

## 3. JSON、JSONL 和 SQLite 的关系

当前不是所有索引信息都只放 SQLite。原因是我们同时服务三类使用者：

- 机器查询：SQLite。
- 程序流式写入：JSONL，如 `asset_catalog.jsonl`。
- 人读报告：JSON / Markdown，如 `asset_summary.json`、`export_run_summary.json`、`ASSET_README.md`。

当前默认 Library 会生成：

- `unity_source_index.db`：源依赖底座。
- `source_index_usage.json`：本次导出使用了哪个源索引。
- `asset_catalog.jsonl`：导出资产清单，后续索引和工具的核心输入。
- `asset_summary.json`：素材库汇总。
- `export_run_summary.json`：导出候选、成功、失败、跳过原因统计。
- `model_validation.json`：模型验证信息。
- `skeletons.json`：骨骼/骨架摘要。
- `animation_bindings.jsonl`：动画到候选模型的关系记录。
- `model_animations.json` / `model_animations.compact.json`：模型动画候选索引。
- `character_assemblies.json`：模块化角色装配候选。
- `library_index.db`：从导出目录构建的素材库查询索引。

这套结构目前合理，但文件较多。后续 UI 工具应优先读 SQLite，人读只看 `ASSET_README.md`、`asset_summary.json`、`export_run_summary.json` 和必要的 per-model README。

## 4. 源索引完整度复查

### 已达标

- 源文件扫描只做 Unity-loadable/sidecar 防御过滤，不再用 3D path filter 裁剪源文件列表。
- `--containers` 现在只过滤导出候选，不切断完整源依赖。
- 源索引记录所有 source file，包括被判定为 sidecar/non-Unity 的文件数量。
- external CAB/PPtr 通过 `source_externals` 和 `source_relations` 保留。
- Renderer / SkinnedMeshRenderer 关系有专用轻量读取：
  - `component.gameObject`
  - `renderer.material`
  - `skinnedMeshRenderer.mesh`
  - `skinnedMeshRenderer.rootBone`
  - `skinnedMeshRenderer.bones`
- AnimationClip binding 进入 `source_animation_bindings`，可作为后续动画匹配和预览工具的数据来源。
- 解析失败不是直接崩溃；可恢复对象会保留 lightweight placeholder，并记录失败统计。

### 仍需增强

- `source_animation_bindings` 目前主要记录 binding hash/type/attribute，path resolution 仍是 deferred。后续应进一步解析 path hash 到 Transform 路径，提升动画匹配准确性。
- `unity_source_index.db` 目前主要用于加载依赖闭包；导出器还没有全面直接按 SQL 关系查询 Mesh/Material/Texture/Animator。也就是说它是“依赖底座 + 可查询证据库”，还不是完全的“SQL 驱动导出计划器”。
- Renderer 轻量读取依赖 TypeTree；TypeTree 不完整或版本异常时有 fallback，但仍可能存在 `lightweightRendererFailures`。报告必须保留这个计数。
- Material/Texture 参数关系目前仍需要继续扩展：标准材质、URP/HDRP、Texture2DArray、ColorMask/Tint 都应逐步进入源关系表或派生索引。

## 5. 性能复查

### 已达标

- 源索引按 batch 加载，默认 `--batch_files 16`。
- 大文件超过 256MB 会单文件成批，避免和其他文件一起放大内存峰值。
- SQLite 写入使用事务，并按 `SourceIndexWriteCommitInterval = 10000` 分段提交。
- 创建 SQL index 放在全部写入后，避免边写边维护索引导致性能恶化。
- `source_index_load_batch_heartbeat` 和 `source_index_write_batch_heartbeat` 每 30 秒记录进度、当前文件、对象序号、内存。
- 全量导出 profile 默认开启，`profile_summary.json` 会汇总阶段耗时。
- 模型循环内 GC 默认关闭，避免 Valheim 这类全量导出被 `model_gc` 拖死。

### 仍需增强

- 目前大 bundle 仍需要 `AssetsManager.LoadFiles()` 完整读 bundle/SerializedFile 元数据和部分对象；对 4GB、6GB 包，仍可能单批长时间占用大量内存。
- heartbeat 能证明“还在工作”，但没有硬性 watchdog，例如 N 分钟没有 object index 变化时自动降级/跳过/写诊断。
- 当前源索引仍会在构建阶段走 AssetsManager 对象读取路径。更高级的做法是为索引单独实现“只读 SerializedFile object table + TypeTree/PPtr 字段流式扫描”的 reader，进一步减少对象实例化和内存占用。
- 批次大小是固定参数，尚未根据可用内存、文件大小、bundle 内 SerializedFile 数量动态调整。

## 6. 导出完整度复查

### 已达标

- Library 导出现在强制需要 SQLite 源索引，旧 CAB map 不再是默认路径。
- 默认 `profile_3d` 是 `All`，不再默认启用 Core 的强路径过滤。
- 默认不再按 `sfx/fx/vfx/spawner/fader/Armature/mixamorig` 静默排除潜在有效模型。
- `Decal`、`Shadow` 不再仅凭名字从 StaticMeshPrimary 中排除。
- StaticMeshPrimary 管线已补：有明确 Unity container/preload 路径和环境/建筑/道具语义的 Mesh 可以作为默认模型资产导出为 glTF。
- Prefab/Animator/GameObject 组合模型仍是主资源；raw source part 不默认污染 Models，但会进入索引。

### 仍需增强

- StaticMeshPrimary 目前仍依赖路径语义识别 environment/building/prop/world/stage 等。如果游戏资源路径命名很特殊，可能漏掉有意义静态 Mesh。
- 裸 Mesh 没有 Renderer 材质绑定时，目前只能给同容器材质候选和 `needsRendererBinding` 标记，不能保证材质准确。
- 对环境资产、地形、Texture2DArray、材质调色/ColorMask/Tint 的关联还未达到“自动可用 90%”的成熟程度。
- 目前分类规则仍以通用路径词为主，后续应将“分类不确定但几何有效”的资源进入 `Models/Unclassified` 或报告，而不是完全沉默。

## 7. 动画索引复查

### 已达标

- 默认模型和动画分离：模型干净，动画独立。
- 大型全量库不做模型 × 动画的海量组合搜索，超过 100,000 pair 会 defer。
- 动画候选现在不仅看骨架兼容，还加入了武器/动作语义约束，避免 bow 模型匹配 bottle/bomb/sword 等明显不一致动画。
- Humanoid bake preview 已在 Freedunk 和 VRising 样本验证过多条动画。

### 仍需增强

- 当前全量库的 `model_animations.json` 在大型项目会 intentionally deferred。后续 UI/小工具需要按选中模型做定向 SQL 查询和预览生成。
- AnimationClip binding path/hash 解析需要继续增强，否则模型动画匹配仍会依赖导出时可见 skeleton/bone path 和语义规则。
- 非角色 Transform 动画、材质动画、激活/事件动画、道具/球/特效联动仍是后续阶段。

## 8. 生产标准评估

| 维度 | 当前状态 | 评级 | 说明 |
| --- | --- | --- | --- |
| 源依赖完整性 | Library 强制使用源 SQLite，源文件不再被候选过滤裁剪 | 通过 | 满足“不切断 Unity 引用”的 1.0 标准 |
| 源关系覆盖 | 已覆盖 GameObject/Transform/Animator/Renderer/Skin/MeshFilter/AnimationClip 基础关系 | 基本通过 | Material/Texture/Animation path resolution 仍需增强 |
| 导出候选完整度 | 默认 All，StaticMeshPrimary 进入默认模型库 | 基本通过 | 特殊命名静态 Mesh 仍可能漏 |
| 噪声控制 | raw part 不默认污染 Models，大量动画匹配 defer | 通过 | 宁缺毋滥策略成立 |
| 性能 | batch、large singleton、heartbeat、profile、GC 优化已具备 | 基本通过 | 超大 bundle 仍需 watchdog 和更流式 reader |
| 可恢复性 | failed batch 会阻止导出，parse failure 有统计 | 通过 | 符合精品资源安全策略 |
| 可运维性 | profile_summary、export_run_summary、usage report、README 均存在 | 通过 | 后续可进一步减少人读文件数量 |
| 查询/工具化 | library_index.db 和 source_index.db 均可查询 | 基本通过 | 导出器本身尚未全面 SQL-driven |

综合判断：

**当前索引体系可以作为 1.0 里程碑用于真实游戏全量测试；但生产级 1.1 的关键目标是“导出计划 SQL 化”和“超大 bundle 流式索引”。**

## 9. 当前不建议做的事

- 不建议重新引入强路径过滤来提速。会伤害完整度。
- 不建议把所有 Mesh 直接默认导出。会生成大量碎片和不可用垃圾。
- 不建议全量生成所有模型 × 动画组合。会让索引巨大且语义不可靠。
- 不建议在源索引 failed batch 时继续导出。半坏索引会造成更隐蔽的缺件。
- 不建议为了减少文件数量删掉 JSON/Markdown 报告。SQLite 面向工具，人读报告仍有价值。

## 10. 下一步建议

### P0：生产前必须继续观察

- 用 VRising、Humankind、Valheim、Freedunk、Homura Hime 继续全量导出。
- 每次检查：
  - `unity_source_index_summary.json`
  - `source_index_usage.json`
  - `export_run_summary.json`
  - `profile_summary.json`
  - `asset_summary.json`
  - `texture2darray_summary.json`
- 如果出现 `failedBatches > 0`，不要使用该导出结果作为正式素材库。

### P1：让导出器直接消费 SQLite 关系

目标：从“SQLite 帮助加载依赖”升级为“SQLite 生成导出计划”。

优先级：

1. 按 GameObject/Animator 找 Renderer。
2. 按 Renderer 找 Mesh / Material。
3. 按 Material 找 Texture / Texture2DArray / ColorMask/Tint 参数。
4. 按 Animator/Controller/Animation 找 AnimationClip。
5. 按 SkinnedMeshRenderer 找 bones/rootBone/Avatar。

这样能减少重复 batch 扫描，提高跨包关系准确性。

### P1：超大 bundle watchdog

建议增加：

- `source_index_watchdog_heartbeat`：记录对象计数是否持续变化。
- 长时间无进度时写 `source_index_stall_report.json`。
- 可选策略：跳过当前对象、跳过当前 SerializedFile、或保守 abort 并标记 failed batch。

默认应偏保守：宁可 failed batch 阻止正式导出，也不要悄悄产出缺件素材库。

### P2：索引 schema v2

建议新增或扩展：

- `source_material_properties`
- `source_texture_usages`
- `source_renderer_bindings`
- `source_animator_clips`
- `source_prefab_parts`
- `source_static_mesh_candidates`

好处：导出器和 UI 都可以直接查“这个模型为什么有/没有贴图、动画、头发、附件”。

### P2：Library 索引收敛

未来可以把 `model_animations.compact.json`、`character_assemblies.json`、`skeletons.json` 的主查询入口迁移到 `library_index.db`，JSON 只保留摘要和人读报告。

## 11. 推荐命令

单独构建源索引：

```powershell
D:\misutime\AnimeStudio\dist\net9.0-windows\bin\AnimeStudio.CLI.exe `
  "D:\BaiduNetdiskDownload\unity-VRising\VRising_Data" `
  "D:\Assets\VRising_AS_Library" `
  --game Normal `
  --build_source_sqlite_index `
  --batch_files 16 `
  --profile_log source_index_profile.jsonl
```

使用已有源索引导出 Library：

```powershell
D:\misutime\AnimeStudio\dist\net9.0-windows\bin\AnimeStudio.CLI.exe `
  "D:\BaiduNetdiskDownload\unity-VRising\VRising_Data" `
  "D:\Assets\VRising_AS_Library" `
  --game Normal `
  --source_index "D:\Assets\VRising_AS_Library\unity_source_index.db"
```

自动构建源索引并导出 Library：

```powershell
D:\misutime\AnimeStudio\dist\net9.0-windows\bin\AnimeStudio.CLI.exe `
  "D:\BaiduNetdiskDownload\unity-VRising\VRising_Data" `
  "D:\Assets\VRising_AS_Library" `
  --game Normal
```

从已导出素材库构建查询索引：

```powershell
D:\misutime\AnimeStudio\dist\net9.0-windows\bin\AnimeStudio.CLI.exe `
  --build_sqlite_index "D:\Assets\VRising_AS_Library"
```

