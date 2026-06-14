# AnimeStudio Library Browser 模型动画预览设计

本文记录 `AnimeStudio.LibraryBrowser` 关于模型、动画候选和动画预览的实现方向，方便后续迭代时保持边界清楚。

## 目标

Library Browser 的目标是快速浏览 AnimeStudio 导出的可用素材库：

- 快速看模型缩略图。
- 选中模型后看到可靠的动画候选。
- 像 Mixamo 一样按需查看“模型 + 单个动画”的效果。
- 成功预览后缓存结果，避免重复生成。

默认不把所有动画提前写进所有模型，也不批量生成全部动画预览。VRising 这类库有几千动画，全量预览会产生巨大耗时和大量中间文件。

## 数据分层

### 第一层：显式 Unity 关系

优先使用 Unity 原生引用：

- `GameObject / Animator -> AnimatorController`
- `Animation -> AnimationClip`
- `AnimatorController / AnimatorOverrideController -> AnimationClip`

这类关系可信度最高，列表中标为 `显式`。

大库即使跳过结构型全矩阵匹配，也必须保留这类显式关系。

### 第二层：选中模型后的定向匹配

对没有显式关系的模型，Browser 在用户选中模型后才做定向匹配：

- 使用模型的 `bonePaths` / `nodePaths`。
- 查询动画的 `AnimationClip transformBindingPaths`。
- 命中后标为 `可匹配` / `需验证`。

这一步只说明结构路径可能匹配，不代表视觉一定正确。必须通过 preview glTF 和 `preview_validation.json` 验证。

### 第三层：结构候选补充

后续可继续增强：

- Avatar / HumanDescription 兼容。
- 可见 mesh / skinned joint 覆盖。
- 武器、道具、挂点、动作语义一致。
- 表情、BlendShape、Legacy、材质/激活类动画分流。

第三层不能退化成无约束的模型乘动画全矩阵。

## SQLite 与 JSON/JSONL 分工

桌面工具高频查询默认走 SQLite：

- 模型列表。
- 动画 binding path 定向匹配。
- 缩略图状态。
- 筛选统计。
- 预览缓存状态。

JSON/JSONL 保留用于：

- 人工排查。
- 可追溯原始数据。
- 兼容旧流程。
- 重新建库。

默认 Library 导出和 `--rebuild_library_indexes` 都应生成 `library_index.db`。`--build_sqlite_index` 是“不重导素材，只刷新 SQLite 工具索引”的手动入口，常用于 CLI 索引逻辑更新后补齐 `model_animation_candidates`。

示例：

```powershell
dotnet run --project D:\misutime\AnimeStudio\AnimeStudio.CLI\AnimeStudio.CLI.csproj -f net9.0-windows -- `
  --build_sqlite_index "D:\Assets\AS-Assets\VRising-Assets"
```

运行前需要关闭正在打开该素材库的 Library Browser，避免 `library_index.db` 被占用。

原神这类几百万候选的大库不能在 Browser 启动时一次性读取全部 `model_animation_candidates`。Browser 应优先使用 SQLite lazy 模式：启动阶段只统计每个模型的显式候选数量、每个动画关联的模型数量；选中模型时再按 `model_output` 查询该模型自己的候选，选中动画时再按 `animation_output` 查询该动画关联的模型。SQLite 加载失败时必须写出 `.as_browser_cache/animation_index_sqlite_error.txt`，不能静默回退成“0 动画”误导用户。素材库索引中的相对路径只能相对当前 Library root 解析，不能回退到 Browser 当前工作目录，避免旧索引或缺失文件误命中仓库里的同名 `Models/`。

动画反查页要把“索引模型数”和“当前库可见模型数”分开显示。索引模型数表示 SQLite 里由 Unity 显式关系记录下来的全部模型-动画关系；当前库可见模型数表示这些关系里，当前 Browser 实际加载到可浏览模型列表的数量。旧导出或筛选后的库可能有确定性关系但缺少对应模型文件，这种情况要显示为“缺失或未导出模型”，不能把索引覆盖率误当成当前可预览覆盖率。

## Unity Bake 配置（生产主线）

当前 Humanoid/Muscle 的可验收生产主线是 Unity bake -> glTF。Browser 调用 CLI 时应只针对 `library_index.db` 中 `relation_source=explicit` 的候选生成请求，由 Unity 采样 `Animator` / `Avatar` / `PlayableGraph`，再让 AnimeStudio 把采样后的目标骨架 TRS 写回 glTF。

生产 bake 的 Avatar 来源必须是 Unity 确定性数据，按以下顺序使用：

1. 普通 Unity 项目优先使用工程内真实 prefab / Animator 上的 `Animator.avatar`，例如 VRising、Freedunk 这类已能直接加载原始 Avatar 关系的库。
2. 如果索引里保留了完整 `HumanDescription.humanBones + skeletonBones`，可以由 Unity `AvatarBuilder.BuildHumanAvatar` 复建 Avatar。
3. 原神这类只靠 `AvatarConstant` / `internalSolver` 会出现肢体扭曲的库，必须先从打包 Unity 对象恢复原始 `UnityEngine.Avatar` asset，导入 bake 工程，并在 request 的 `unityAssetPaths.avatarAsset` 里显式指定。导入后仍由 Unity 自身加载 Avatar，因此它也是确定性生产来源，不是名称或骨骼数量猜测。

Library Browser 和 CLI 批量 Unity bake 会自动扫描 `UnityBakeProject/Assets/AnimeStudioBake/ImportedAvatar/*.asset`，按文件名把已导入的 Avatar asset 加入候选映射；全局或素材库本地 `unityAvatarAssets` 仍可显式覆盖。自动匹配只接受模型 `avatar.name`、模型名和模型文件名这些 key 的精确命中，不做 contains 模糊匹配。这样原神恢复新的 Avatar asset 后，通常只需要把 `.asset` 放进固定目录并让 Unity 导入，不需要每次手写 JSON 或给每个批量命令传同一个 `--unity_avatar_asset`。如果素材库里存在 fresh 的 `ImportedAvatarProbe*/imported_avatar_probe_batch.json`，Browser 自动扫描只接受该报告里 `isValid && isHuman` 的 Avatar asset；验证失败或验证报告明确无效的 asset 不能让候选进入生产 bake ready。Browser 候选详情要显示 Avatar oracle 来源、Avatar asset 和命中的 key，方便区分“导入 Avatar asset”“原始模型 Avatar/HumanDescription”和“缺生产 Avatar oracle”，也方便发现配置错配；这些字段只用于展示和门禁说明，不新增模型-动画关系。CLI 的 `--unity_avatar_asset` 只保留给明确 `--preview_model` 的单模型定向预览；未指定模型或一次命中多个模型时必须拒绝，避免把同一个 Avatar 当成全局 fallback。
显式指定或精确命中的 `unityAssetPaths.avatarAsset` 必须加载到有效 Humanoid `Avatar`。加载失败、路径写错或 Avatar 无效时，Unity bake 应直接失败并写错误结果，不能继续退回临时 `BuildHumanAvatar`，否则会把 Avatar oracle 主线伪装成旧诊断路径。同一个 request 同时提供 prefab 和 Avatar asset 时，也必须用 Avatar asset 覆盖 Animator.avatar，确保配置的 oracle 真正参与采样。

CLI 全量摘要也会把这条路径单独统计出来。`--bake_animation_previews_from_library ... --preview_validation_limit 0` 会写出 `importedAvatarAssetBakeReadyExplicitUnityBakeCandidates` 和 `effectiveBakeReadyExplicitUnityBakeCandidates`：前者只看导入 Avatar asset 命中的显式候选，后者是 HumanDescription / 原始 prefab Avatar 与导入 Avatar asset 的联合覆盖。这个数字只说明“已经具备确定性 Unity bake oracle”，不新增模型-动画关系，也不代表视觉已经人工验收。

从打包 `AvatarConstant` 恢复出的 `avatar.oracle` / `internalSolver` 只能作为定位原始 Avatar asset 和后续公式研究的诊断输入，不能单独算生产 bake ready。只有 Unity 返回有效 Humanoid Avatar，且 apply 报告里 `avatarTrust.TrustedProductionBake=true`，Browser 才把 baked glTF 当作可播放生产预览。内部 `Avatar/Muscle -> glTF TRS` 求解保留为实验诊断入口，不作为默认预览或验收路径。

Library Browser 不要求用户通过环境变量配置 Unity 路径。需要运行 Unity bake 生产预览时，配置优先级如下：

1. 素材库本地配置：`<LibraryRoot>\.as_browser_cache\unity_bake_settings.json`。
2. Library Browser 全局配置：`%APPDATA%\AnimeStudio\LibraryBrowser\settings.json`。
3. Unity Hub 默认安装目录自动检测，例如 `C:\Program Files\Unity\Hub\Editor\<version>\Editor\Unity.exe`。
4. 环境变量兜底：`ANIMESTUDIO_UNITY_BAKE_PROJECT`、`ANIMESTUDIO_UNITY_EDITOR`。

素材库本地配置只用于某个 Library 需要特殊 Unity 工程或 Editor 版本时覆盖全局设置。正常情况下团队成员应优先使用 Library Browser 的全局配置，避免把个人机器路径写进素材库目录。

全局配置示例：

```json
{
  "unityProject": "D:\\Assets\\AnimeStudioUnityBakeProject",
  "unityEditor": "C:\\Program Files\\Unity\\Hub\\Editor\\6000.4.5f1"
}
```

`unityEditor` 可以写 Unity 版本目录，也可以直接写 `Unity.exe`；Browser 会自动规范成 `...\Editor\Unity.exe`。顶部工具栏的 `Unity设置` 按钮会同时修改全局 `unityEditor` 和 `unityProject`，不会改环境变量，也不会改素材库本地配置。

注意：`Unity Editor` 和 `UnityBakeProject` 是两件事。前者是 `Unity.exe`，后者是用于运行 AnimeStudio.UnityBake helper 的 Unity 工程目录。只配置 `Unity.exe` 仍然无法执行烘焙。
Browser 校验 UnityBakeProject 时不仅检查 `AnimeStudioBakeCli.cs` 是否存在，也会检查 helper 是否包含导入 Avatar asset 强校验和 `importedAvatarAssetValid` 证明字段。若提示 helper 版本过旧，应把仓库里的 `AnimeStudio.UnityBake\Assets\AnimeStudio.UnityBake` 重新复制到 bake 工程的 `Assets` 目录；否则原神这类依赖导入 Avatar oracle 的结果可能无法被统计为可信。

### Unity Bake Worker

Humanoid/Muscle 采样需要 Unity `Animator`/`Avatar`，冷启动 Unity 会非常慢。LibraryBrowser 可以使用常驻 Unity Bake Worker 来优化这个生产预览流程：

- CLI 参数：`--unity_bake_worker_queue <queue-dir>`
- Browser 默认队列：`<LibraryRoot>\.as_browser_cache\unity_bake_worker`
- 第一次烘焙会启动 Unity worker，后续同一素材库的 Humanoid 烘焙会复用已启动的 Unity 进程。
- 同一个 Unity 工程同一时间应复用同一个固定队列。默认使用 Browser 的素材库队列；只有隔离诊断时才临时指定其他队列，避免多个 Unity batchmode 同时打开同一工程导致 worker 早退。
- Worker 通过 `worker_heartbeat.json` 表示存活，通过 `*.request.json` / `*.done.json` / `*.error.json` 文件队列处理任务。
- 采样结果仍然写 `unity_bake_result.json`，再由 AnimeStudio CLI 合成 baked glTF；模型、贴图、骨骼、动画数据路径不变。
- 顶部工具栏提供 `Unity Worker` 下拉菜单：
  - `状态`：读取当前素材库队列的 `worker_heartbeat.json`，显示运行、未启动、未响应等状态。
  - `启动`：按当前有效 Unity 配置启动该素材库的常驻 worker。
  - `重启`：先停止当前队列 worker，再重新启动。
  - `停止`：优先写入 `worker_stop.request` 让 worker 自己退出；如果旧版 worker 不认识停止信号，会兜底关闭命令行同时包含 `AnimeStudioBakeWorker.Run` 和当前队列路径的 Unity batchmode 进程。

这个 worker 只优化调度速度，不改变动画还原算法。若需要完全关闭 worker，可结束对应的 Unity batchmode 进程；下次预览会自动重新启动。

## 动画预览流程

预览采用按需生成，并优先走快速路径：

1. 用户选中模型。
2. Browser 显示显式动画候选或定向匹配候选。
3. 用户双击动画。
4. Browser 调用 `AnimeStudio.CLI --generate_preview_from_library <LibraryRoot>`。
5. CLI 从 `library_index.db` 的 `model_animation_candidates` 表读取 `relation_source=explicit` 的选中模型-动画关系，再联表读取模型和动画 `raw_json`。
6. CLI 在内存里构造单模型单动画预览选择，不写临时索引 JSON；如果命令行手工指定的模型和动画之间没有 Unity 显式关系，默认预览会直接拒绝。
7. CLI 先尝试快速路径：复制已导出的模型 glTF 目录，读取动画旁 `*.animation_asset.json` 的 `decoded` TRS / Humanoid Muscle / BlendShape 曲线，转换到 glTF 坐标基后写入 glTF animation。
8. 快速路径成功时写出 `preview_validation.json`，Browser 记录 `preview_state.json`，并用 f3d 打开 glTF。
9. 如果 sidecar 只有 `decoded.status=compacted`，CLI 使用完整 `unity_source_index.db`、显式 `--source_files` 和选中对象的 `--path_ids` 定向刷新选中模型/动画的源文件依赖闭包，再生成 FastPreview。
10. 快速路径和定向刷新都失败时，才标记失败或进入显式 Unity bake 生产路径，不应静默退回模型 × 动画猜测。

快速路径不假装解决所有动画：

- `animation_asset.json` 的 decoded TRS 是 Unity 本地坐标。写入 glTF 前必须和模型导出保持一致：`translation.x=-x`，`rotation.y/z=-y/-z`。否则四足动物、长身体角色等会出现明显卷曲或压缩。
- Humanoid/Muscle 动画默认走 Unity bake -> glTF 生产路径；内部 Humanoid/Muscle -> 目标骨架 TRS 求解器只作为实验诊断，不进入默认播放验收。
- Humanoid 内部求解时，glTF node rest rotation 必须先转回 Unity 空间，再与 Unity `AvatarConstant.m_Human` 的 `preQ/postQ` 和 Muscle delta 相乘，最后写回 glTF。若直接把 glTF rest 当 Unity rest 使用，手臂和腿部容易出现向后扭曲。
- BlendShape、材质、激活、事件类动画需要独立通道支持。
- 节点匹配必须来自 glTF 节点路径/节点名，不按角色名或目录硬猜。

VRising `HYB_CreatureGhoul_Blackfang_Peon03 + CreatureGhoul_1HPickaxe_Idle` 的实测快速预览耗时约 `0.75s`，生成 `210` 个 glTF animation channel，`invalidChannels=0`。

缓存目录示例：

```text
.as_browser_cache/
  animation_previews/
    <modelKey>/
      <animationKey>/
        preview_state.json
        output/
          preview_validation.json
          Models/.../*.gltf
```

## 状态定义

动画列表中的状态：

- `未生成`：还没有生成过预览。
- `生成中`：CLI 正在生成 preview glTF。
- `可播放`：已生成 glTF，并找到 `preview_validation.json` 或 glTF 文件。
- `失败`：CLI 失败、源文件缺失或没有生成 glTF。
- `需 Unity 烘焙`：Humanoid/Muscle 动画，需要 Unity bake 采样后写回 glTF 才进入当前生产验收。
- `需 Unity 烘焙(Avatar)`：同样需要 Unity bake，但当前候选已经匹配到导入 bake 工程的 `UnityEngine.Avatar` asset；这是原神这类库的 Avatar oracle 主线，不是骨骼数量或名称猜测。
- `需 Avatar 元数据`：候选关系来自 Unity 显式索引，但模型既没有完整 HumanDescription，也没有完整 AvatarConstant oracle，不能生成可信 Humanoid bake。
- `拒绝预览` / `拒绝烘焙`：候选不是 Unity 显式关系，Browser 不会把结构匹配、骨骼兼容、名称匹配或人工拼接写成默认可播放缓存。

`可播放` 仍不等于人工视觉验收通过，只说明 preview 文件存在并通过当前最小检查。Unity bake 结果还必须看 `unity_bake_apply_report.json` 里的 `frameVaryingTracks`、`avatarTrust`、skin/joint/bbox 等字段；非 Unity bake 快速预览仍看 `preview_validation.json`。

打开素材库时，Browser 会读取根目录的 `animation_bake_cache_summary.json`，在状态栏和模型详情中显示全局 Unity bake oracle 覆盖、普通 prefab/HumanDescription 路径候选数、ImportedAvatar 路径候选数、可信 baked glTF 数、静态姿态和不可信 baked 统计。这个摘要只来自 CLI 批量烘焙或 `--preview_validation_limit 0` 刷新，不会在 Browser 加载时重新扫描几百万候选，避免打开大库时卡住。
Browser 内触发单个或批量 Unity bake 后，会重新读取已有 `animation_bake_cache_summary.json`，让状态栏和详情区尽快看到 CLI 写回的 cache 变化；但 Browser 不会自动跑全量 `--preview_validation_limit 0` 统计，避免一次 UI 操作卡住原神这类大库。模型详情也会读取最新的 Browser 批量报告，显示最近一次成功、失败、已烘焙和缺 Avatar oracle 跳过数量；这只是操作审计，不写入全局候选关系，也不改变缓存覆盖率。
Browser 也会读取最新的 `AnimationRelationDiagnostics*/deterministic_animation_coverage.json` 或根目录同名报告，在状态栏和模型详情中显示“动画关系门禁”。如果报告包含 `unityBakeProduction` 或 FastSummary 内嵌的 `animationBakeCacheSummary`，Browser 会同时显示有效 Avatar oracle、导入 Avatar asset 文件/key、可信文件数、导入 Avatar oracle 候选数，以及 ImportedAvatar probe 是否 fresh/enforced，便于确认原神这类库是否已经通过 `unityAssetPaths.avatarAsset` 进入生产 bake ready。这个结果只来自 `Measure-DeterministicAnimationCoverage.ps1 -FastSummary/-GateOnly/-SummaryOnly/-FailOnWarning` 等离线命令；Browser 打开素材库时不会主动执行 SQLite 全库诊断，避免大库 UI 卡顿。

顶部工具栏的“刷新动画门禁”是下拉入口：`快速摘要` 运行 FastSummary，只读取根目录 bake cache、ImportedAvatar asset 和最近批次报告，适合原神这类大库做阶段性确认；`快速门禁` 运行 GateOnly，检查默认候选是否全是 Unity 显式关系；`烘焙摘要` 运行 SummaryOnly，统计 Unity bake / Avatar oracle 覆盖，并在已配置 Unity Bake Project 时传入 `-UnityProject` 读取 `Assets/AnimeStudioBake/ImportedAvatar/*.asset`。三者都不会烘焙动画，也不会新增或猜测模型-动画关系。
同一个下拉里的 `Avatar恢复计划` 会运行 `Write-UnityAvatarAssetRecoveryPlan.ps1 -OnlyMissing`，按 Unity Avatar 对象列出仍未导入 `ImportedAvatar` 的缺口，并输出 CSV/Markdown/JSON 和配置模板。这个入口只生成计划，不调用 UnityRipper/uTinyRipper，不写入素材库关系，也不会把缺失 Avatar 当成可烘焙。

同一个下拉里的 `验证AvatarAsset` 会运行 `Test-UnityImportedAvatarAssets.ps1`，让 Unity Editor 用 `AssetDatabase.LoadAssetAtPath<Avatar>` 批量验证 `Assets/AnimeStudioBake/ImportedAvatar/*.asset`。只有 `avatar.isValid && avatar.isHuman` 的条目才算有效 Avatar oracle；这个入口只做验证和报告，不新增模型-动画关系，也不触发动画 bake。验证成功后 Browser 会自动刷新一次 `FastSummary`，让顶部状态立刻显示当前 probe 的 `fresh` / 有效数量。

Browser 的批量 Unity 烘焙入口只会处理已经具备生产 Avatar oracle 的显式候选。缺少原始 `Animator.avatar`、完整 `HumanDescription` 或导入 Avatar asset 的项会写入批次报告 `skipped_missing_avatar_oracle`，不调用 Unity bake，也不按旧 `BuildHumanAvatar` / AvatarConstant / 骨骼兼容 fallback 继续尝试。

模型列表的大图标签会把动画状态压成短 badge：`显式N` 表示当前模型有 N 个 Unity 显式关系动画，`烘焙X/Y` 表示显式关系的主线 Unity bake 进度，`AvatarN` 表示 N 个显式候选已命中导入 Avatar asset，`缺AN` 表示 N 个显式 Humanoid/Muscle 候选仍缺生产 Avatar oracle。模型筛选里的 `缺Avatar` 与 `缺AN` 使用同一判断，只定位这类缺口，不会把它们加入可烘焙队列。

单个动画预览读取到 CLI `unity_bake_batch_report.json` 的 `noop_missing_avatar_oracle` / `skipped_missing_avatar_oracle` 时，也应直接提示“缺少可信生产 Avatar oracle”，引导恢复/导入原始 Unity Avatar asset 或补完整 HumanDescription，而不是只显示笼统的“没有生成 baked glTF”。

当 GateOnly 报告显示 `candidateTableSchema.status != ok`，但 `totals.nonExplicitCandidates == 0` 时，说明现有候选数据是干净的、只是旧 `library_index.db` schema 还没有硬约束。Browser 顶部“重建动画索引”按钮会运行 `--build_sqlite_index`，并默认带上 `--require_fresh_source_animation_relations --skip_sqlite_file_index --skip_sqlite_sidecar_scan --skip_sqlite_json_documents`，只刷新结构化索引、显式动画候选和候选表 schema，不重新导出模型、贴图或动画。这个按钮仍然只消费 `unity_source_index.db` 的 Unity 确定性关系；如果源索引缺少当前工具要求的 Animator/Animation/OverrideController 精确关系，应失败并要求先重建源索引，不能退回骨骼数量、名称或路径猜测。

如果同一素材库目录里存在旁路验证 DB，例如 `library_index.schema_verify.db`，对应的 GateOnly 报告只用于人工对照。Browser 状态栏和模型详情优先读取 `libraryIndex` 指向正式 `<LibraryRoot>/library_index.db` 的报告，避免旁路报告通过后把仍未替换的正式库显示成已通过。

旧 `model_animations.json` 入口也必须遵守同一条规则：`--generate_preview_gltf`、`--pack_model_animations` 和 `--generate_unity_bake_request` 只能处理显式 Unity 关系候选。SQLite 重建导入 `model_animations.compact.json` 时也必须要求 `relationSource=explicit`，`model_animation_candidates.relation_source` 本身有 `CHECK(relation_source='explicit')` 约束；`confidence=explicit_unity_reference` 只能作为显式关系的质量说明，不能单独把 fallback/diagnostic 提升成默认候选。嵌入 sidecar、结构匹配、名称匹配或手工选择可以保留为诊断数据，但不能进入默认可播放 glTF、动画包或生产 Unity bake 请求。

模型页“质量”筛选也提供动画主线入口：`有导入Avatar候选` 只看已经匹配 ImportedAvatar oracle 的显式 Unity bake 候选；`有原始Avatar候选` 只看走原始 prefab / HumanDescription / 生产 Avatar 标记路径的显式 Unity bake 候选；`待可信烘焙` 排除已有可信 `可播放` 缓存，用来找下一批需要 Unity bake 的模型；`需Avatar元数据` 用来定位仍缺生产 Avatar/HumanDescription 的显式候选。选中单个模型后，模型详情里的动画列表也提供同一组状态筛选：`待可信烘焙`、`导入Avatar`、`原始Avatar`、`需Avatar元数据`、`失败/需复查`。这样可以在一个模型内直接筛出失败或待处理动画，再执行“批量烘焙当前可见动画”，不需要从批次报告里手工逐个复制动画名。

动画页右侧模型列表有独立状态筛选：`待可信烘焙`、`导入Avatar`、`原始Avatar`、`需Avatar元数据`、`失败/需复查` 都按“当前动画 + 当前模型”的显式候选记录判断，而不是只看全局动画记录。这样同一个 AnimationClip 关联多个模型时，Browser 会使用每个模型自己的 Avatar asset、匹配键和缓存状态。

动画页详情区会汇总当前右侧可见模型的状态计数：可播放、待可信烘焙、导入 Avatar、原始 Avatar、需 Avatar 元数据、失败或复查。这个计数跟随右侧文本过滤和状态筛选变化，方便在批量烘焙前确认当前操作范围。

批量 Unity bake 每次执行都会在 `<LibraryRoot>\.as_browser_cache\unity_bake_batch_reports\` 写一份操作报告，记录本批次的模型、动画、显式关系来源、Avatar asset 命中、Avatar 来源/asset/key 汇总、成功/失败、失败原因、baked glTF 和 apply report 路径。Browser 的全局摘要会读取最近批次的 Avatar 来源统计，显示本批实际走了 ImportedAvatar oracle 还是原始 prefab / HumanDescription 路径。这个报告只用于 Browser 操作审计和失败重试，不写入 `model_animation_candidates`，也不新增任何模型-动画关系。失败重试应通过模型页的动画名过滤，或动画页右侧“失败/需复查”状态筛选缩小当前可见范围后再次执行批量烘焙；重试仍会重新走同一条 Unity 显式关系 + 可信 Avatar gate。

## 后续迭代

优先级建议：

1. 在 Browser 中增加独立动画候选面板的搜索和过滤。
2. 增加“只看显式 / 只看可匹配 / 只看可播放 / 只看失败”筛选。
3. 给 `preview_validation.json` 做结构化摘要展示。
4. 增加批量生成：只对用户收藏、显式绑定或当前筛选结果生成预览。
5. 增加内嵌 3D 播放器；在这之前默认用 f3d 打开生成的 preview glTF。
6. 扩展批量 Unity bake 的进度展示和失败重试，让 Browser 可以稳定消化原神这种几百万显式候选的大库。

原则：先保证候选关系可信、预览按需、缓存稳定，再做更复杂的实时播放器。
