# Naraka 首版可用导出审计

审计日期：2026-06-26

本页记录永劫无间（Naraka: Bladepoint）当前第一版可用导出状态。它不是全量导出报告，而是小范围代表性 smoke 的事实清单，用来判断工具是否已经打通模型、贴图、材质、索引和报告闭环。

## 结论

当前 Naraka P0/P1 静态素材库链路已经可用：`StreamingAssets` 输入、Naraka header 修正、源索引、定向 Library 导出、共享贴图、材质 sidecar、`asset_library.json`、`library_index.db`、glTF 校验和浏览器索引验证都能跑通。

动画仍是 P2 诊断阶段。当前已有手动 `--preview_avatar` 入口能把源索引里的 Avatar.m_TOS 临时注入，用于 hash-only TRS 动画路径恢复诊断；但它不会创建默认模型-动画关系，也不能让 `asset_library.json.capabilities.animations` 变成 `true`。生产动画能力还需要继续找到完整角色、Avatar、Controller/Clip 或其它 Unity 显式关系，并补足清晰视觉验收。

## 输入形态

本机 Naraka 主资源入口：

```text
C:\Game163\program\NarakaBladepoint_Data\StreamingAssets
```

当前 smoke 结论是这批资源可以直接走 `--game Naraka` 的 Unity 素材读取路径。`C:\Game163\program` 下少量 `.pak` 属于 webview/CEF 资源，不是当前模型、贴图、材质和骨骼主入口。网上流传的 AES key 只能作为其它发行形态或旧版本线索，不能写入默认 Library 流程。

社区资料只作为参考：ResHax 线程提到 Naraka 在 2024 年多次改过解包/加密方式，并指向 Rivelia/Studio、cs.rin 等线索；这不能替代本地 `StreamingAssets` 探针和源索引验证。

## 代表样本

| 样本 | 输出目录 | 当前状态 | 说明 |
| --- | --- | --- | --- |
| Hadi body s9 | `D:\Assets\Naraka\HadiBody_s9_IndexV1_Rebuild_Current` | P0/P1 正样本 | 1 个 Model、29 个 Texture、11 个 Material sidecar，`model_validation=ok`，`texture_links.link_error=0`。 |
| Face male battle | `D:\Assets\Naraka\FaceMaleBattle_ShaderBoundary_Current` | 特殊 shader 边界样本 | 1 个 Model、17 个 Texture、3 个 Material sidecar，2 个材质标记 `customShaderRequired/layeredMaterialUnresolved`，`model_validation=warning`。 |
| Bow prop | `D:\Assets\Naraka\CandidateBatch_SkinRootFix\wp_bow_dongjun\Smoke_weapon_drop_bow_dongjun_root_3879445205109982761` | 非角色正样本 | 武器/道具 prefab，材质和贴图链接正常。 |
| Device hongbao | `D:\Assets\Naraka\SourceCandidates_TextureHealthOk1\Smoke_device_hongbao_02_3817277305598733592_PropKind_Current` | 非角色正样本 | device/prop 样本，`model_validation=ok`。 |
| Static bigtree | `D:\Assets\Naraka\Smoke_static_jisui_device_bigtree_04_Current` | 静态场景/道具样本 | 静态 mesh/prop 样本，`model_validation=ok`。 |
| Samurai ghost | `D:\Assets\Naraka\Naraka_CompleteCharacterCandidate_SamuraiGhost_BundleRoot_Current` | 大型 skinned 候选样本 | 源索引由 `Animator.avatar -> GameObject -> Transform tree -> SkinnedMeshRenderer` 关系筛出，`model_validation=ok`，bbox 与 skin 规模更接近完整人形/怪物候选；同 bundle 有 Humanoid/Muscle clip 线索，但没有 `Animator.controller` / `Animation.clip` / `AnimatorController.clip` 显式挂接，smoke 已把这个边界机器化检查，不能单独升级为生产动画样本。 |

本轮复查的 SQLite 摘要：

```text
hadi_body: models=1 textures=29 material_sidecars=11 texture_links=32 link_errors=0 custom_shader_materials=0 validation=ok:1
face_shader: models=1 textures=17 material_sidecars=3 texture_links=5 link_errors=0 custom_shader_materials=2 validation=warning:1
weapon_bow: models=1 textures=10 material_sidecars=3 texture_links=12 link_errors=0 custom_shader_materials=0 validation=ok:1
device_hongbao: models=1 textures=4 material_sidecars=1 texture_links=4 link_errors=0 custom_shader_materials=0 validation=ok:1
static_bigtree: models=1 textures=12 material_sidecars=1 texture_links=13 link_errors=0 custom_shader_materials=0 validation=ok:1
samurai_ghost_bundle_root: models=1 textures=18 material_sidecars=6 texture_links=20 link_errors=0 custom_shader_materials=0 validation=ok:1 animation_candidates=0
```

## 特殊材质边界

`FaceMaleBattle_ShaderBoundary_Current\MATERIAL_REPORT.md` 已经按当前规则标记 Naraka 私有 shader 分层材质：

- `male_eye` 的 `_IrisDiffuseTex`、`_IrisNormalTex`、`_IrisSpecTex` 保留为 `preservedOnly`。
- `male_face_custom` 的眉毛 decal、皱纹 diffuse/normal/spec 槽保留为 `preservedOnly`。
- 对应材质在报告和 SQLite `material_sidecars` 中标记 `unsupportedShader`、`customShaderRequired`、`layeredMaterialUnresolved`。
- `pbr_preview_status=bestEffortDegradedPreview`，表示 glTF 只是降级预览，不代表原游戏最终 shader 效果。

这类样本不应被归类为贴图丢失或材质错绑；它们说明 Mesh、UV、贴图引用和原始材质槽已经确定性保留，但仍需要 Naraka 私有 shader 复刻或人工材质重建。
smoke 会额外检查 `material_sidecars` 中的自定义 shader 行是否保留 `key_texture_slots_json`、`exported_textures_json`、`unresolved_steps_json`、`raw_json` 和 Unity `m_Shader` PPtr 引用。新建索引会把 shader 引用写入 `shader_reference_json`，并用 `shader_name_status=referenceOnly` 区分“已保留原始 shader 引用但当前无法解析名称”。`sqlite_index_summary.json.qualityGates` 也会写出 `shaderReferenceSidecars`、`shaderReferenceOnlySidecars`、`customShaderReferenceSidecars` 和 `customShaderMissingNameSidecars`，方便 smoke 和人工报告直接判断。`shader_name` 目前在既有 Face 样本中仍为空，smoke 会记录 `missingShaderNameRows` 作为后续上游材质 sidecar 追踪项，但不把少量私有 shader 名缺失误判成贴图丢失或材质错绑。
通用 glTF 模型导出路径也已补齐 catalog 顶层 `materialNeedsCustomShaderLayer` 汇总：即使某个材质同时能生成 `standardGltfMaterial` 级别的 base color / normal 预览，只要 glTF `extras.animeStudioMaterial` 里存在 `needsCustomShaderLayer`、`customShaderRequired` 或 `layeredMaterialUnresolved`，模型行仍会保留这个私有 shader 风险标记。SQLite `assets.material_needs_custom_shader_layer` 和动画门禁可直接读取该字段，不需要绕到单个 material sidecar 才能发现“需要私有 shader/人工材质重建”状态。

## 可复现命令

推荐一行 smoke：

```powershell
tools\Export-NarakaFirstUsableSmoke.ps1
```

默认输入 `C:\Game163\program\NarakaBladepoint_Data\StreamingAssets`，默认使用 `D:\Assets\Naraka\SourceIndex_Full_HeaderFix1\unity_source_index.db`，输出到 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_Current`。脚本会跑输入探针，并用完整源索引闭包定向导出三个默认 Library 代表模型：`ch_m_hadi_lv_s9` 角色部件、`weapon_drop_bow_dongjun` 武器道具、`device_hongbao_02` 普通道具；随后重建 `library_index.db`，并内置检查 `asset_library.json` 的 AssetLibrary v1 合约：`schemaVersion=1`、`libraryKind=AssetLibrary`、`sourceGame=Naraka`、`index=library_index.db`、`capabilities.models=true`、`capabilities.animations=false`，以及 SQLite `metadata/assets/model_validation/texture_links/material_sidecars/library_reports` 核心表。脚本还会检查核心素材库文件契约，要求 `asset_catalog.jsonl`、`model_animations.json`、`model_animations.compact.json`、`animation_bindings.jsonl`、`unity_relations.jsonl`、`unity_relation_summary.json`、`export_manifest.jsonl`、`model_validation.json`、`sqlite_index_summary.json`、`profile_summary.json`、`source_index_usage.json`、`skeletons.json`、`character_assemblies.json` 和 `asset_summary.json` 均存在；其中 `animation_bindings.jsonl` / `unity_relations.jsonl` 在当前无生产动画或旧 JSONL 关系时允许为空，但不能缺失。脚本还会检查根目录 `LIBRARY_README.md` / `SQLITE_INDEX_README.md`，并要求每个代表 Model 目录都有非空 `ASSET_README.md` 和 `MATERIAL_REPORT.md`，确保素材库既有机器索引也有人读入口。脚本还会在本机工具存在时执行 3 个代表模型 glTF validator、AssetLibrary Browser 验证和 3 张缩略图渲染。脚本还会默认校验 `D:\Assets\Naraka\FaceMaleBattle_ShaderBoundary_Current`，只把它当作 Naraka 私有 shader/分层材质边界样本，要求贴图链接为 0 错误、`material_sidecars` 保留 custom/layered/degraded 标记、关键贴图槽、导出贴图、未复刻步骤、raw sidecar 和 Unity shader PPtr 证据，并验证它不是贴图丢失或材质错绑。脚本还会只读校验 `D:\Assets\Naraka\Smoke_static_jisui_device_bigtree_04_Current`，把它当作静态环境/道具 Mesh 的显式扩展样本，要求模型验证通过、贴图/材质 sidecar 存在、SQLite 贴图链接无错误；这个检查不改变默认 Library 仍优先 prefab/Animator/GameObject 组合模型的范围。脚本还会只读校验 `D:\Assets\Naraka\Naraka_CompleteCharacterCandidate_SamuraiGhost_BundleRoot_Current`，把它当作更强的 skinned 人形/怪物候选，要求模型验证通过、skin 和贴图存在、skin joint 数量足够、bbox 不再是小挂件级别、SQLite 贴图链接无错误，并要求 `model_animation_candidates` / `model_animation_relations` / `relation_animations` 为 0，防止把同包动画或 Avatar 兼容线索伪装成生产动画关系。脚本还会用完整源索引机器检查 SamuraiGhost bundle：必须保留 `Animator.avatar` 和同 bundle Humanoid/Muscle binding 线索，同时 `Animator.controller` / `Animation.clip` / `AnimatorController.clip` 显式边必须为 0。完整源索引里的 legacy `Animation.clip` 显式边也会被机器分类和解析率检查；当前这些边落在 VFX/marker 诊断域，不会启用角色身体动画能力。脚本还会强制 `animatorControllerProductionGate.withAvatarAndControllerClipEdges=0`；如果这个值变成非 0，说明出现了 Animator+Avatar+ControllerClip 生产候选信号，必须重新做模型/clip 关系与视觉验收。脚本同时记录 `monoBehaviourAnimationClipPPtrSummary`，把 MonoBehaviour 脚本字段里显式指向 AnimationClip 的 PPtr 作为诊断地图，但这类关系需要游戏脚本语义解析，不能直接升级成默认模型-动画绑定。脚本还会默认跑 Dijiang A8 独立动画 glTF 诊断，验证 `Avatar.m_TOS` 路径恢复和动画 glTF 写出；报告必须保留 `diagnosticOnly=true` / `notDefaultModelAnimationRelation=true`，因此它不会把当前手动动画诊断升级成生产动画库能力。只想跑 P0/P1 静态链路时传 `-SkipAnimationDiagnostic`。

脚本结束后会在 `OutputRoot` 写出两个汇总文件：

- `SMOKE_REPORT.md`：人读 smoke 结论，汇总静态模型、glTF 校验、浏览器校验、缩略图、SQLite 索引计数、贴图链接质量门槛、特殊 shader 降级计数、静态环境扩展样本、角色候选样本、动画关系覆盖摘要和动画诊断边界。
- `smoke_summary.json`：机器读 smoke 摘要，用来复查产物路径、`assetLibraryContract`、`libraryCoreArtifacts`、`modelReportEntrypoints`、能力标记、验证状态、SQLite 索引计数、`qualityGates`、`hadiModularBoundary`、`shaderBoundary`、`staticEnvironment`、`characterCandidate`、`characterCandidateSourceIndexBoundary`、`sourceIndexLegacyAnimationClipDomains`、`animatorControllerProductionGate`、`monoBehaviourAnimationClipPPtrSummary`、`scriptAnimationComponentDiagnostics`、`sourceModelAvatarDiagnostics` 和动画诊断状态。关键动画边界诊断同时保留在顶层和 `animationRelationCoverage` 下，方便机器审计直接读取。脚本写完后会立刻反读解析，避免报告可读但机器摘要损坏。

这两个文件只汇总 smoke 证据，不会改变正式 `RepresentativeModels` 素材库，也不会把诊断动画写成默认动画关系。
脚本会要求 `qualityGates.textureLinkErrors=0`；如果 glTF 贴图引用链路断开，smoke 会直接失败。`customShaderRequiredSidecars` / `layeredMaterialUnresolvedSidecars` / `customShaderReferenceSidecars` 只作为 Naraka 私有 shader 边界证据记录，不会被当成贴图丢失。
2026-06-26 复验默认一行 smoke `D:\Assets\Naraka\Naraka_FirstUsableSmoke_DefaultFull_Current`：不带跳过项执行 `tools\Export-NarakaFirstUsableSmoke.ps1` 通过。默认代表模型 `models=3`、`ok=3`、`withSkin=2`、`withTextures=3`；SQLite `textureAssets=43`、`textureLinks=48`、`materialSidecars=15`、`textureLinkErrors=0`；`assetLibraryContract.status=ok`，`modelReportEntrypoints.status=ok` 且 `ASSET_README=3`、`MATERIAL_REPORT=3`；代表 glTF validator、AssetLibrary Browser 校验和 3 张缩略图均为 `ok`。`shaderBoundary`、`staticEnvironment`、`characterCandidate`、`characterCandidateSourceIndexBoundary`、`scriptAnimationComponentDiagnostics` 和 `animatorControllerProductionGate` 均为 `ok`。Dijiang A8 独立动画诊断写出 159 个 channel，`animationGltfValidation=ok`，但继续标记 `diagnosticOnly=true`，`capabilities.animations=false` 不变。
2026-06-26 复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_AvatarDomain_Clean_Current`：默认 smoke 批次生成 `models=3`、`ok=3`、`withTextures=3`、`textureAssets=43`、`materialSidecars=15`、`textureLinkErrors=0`，代表模型 glTF validator、AssetLibrary Browser 校验和 3 张缩略图均通过。`shaderBoundary.status=ok`，`customShaderRequiredSidecars=2`、`layeredMaterialUnresolvedSidecars=2`、`degradedPreviewSidecars=2`、`materialSidecarRows=3`、`customShaderMaterialSidecarRows=2`，Face 样本 glTF validator 也通过。这把一行 smoke 从单 Hadi 样本扩展为角色部件、武器、道具三类默认 Library 正样本，并把 Naraka 私有 shader 边界和 Avatar 域诊断纳入默认验证；静态树已作为显式扩展样本进入 smoke 只读质量门禁。
2026-06-26 复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_RelationHealth_Current`：`sourceIndexAnimationRelationHealth.status=ok`，`animatorController.clip=98`、`resolved=98`、`missing=0`。此前 `resolved=97/missing=1` 是 Library 摘要查询没有按 SerializedFile 大小写不敏感匹配造成的假 warning，不代表当前源索引缺 AnimationClip CAB。
同一轮新增 `explicitControllerClipDomains` 诊断：当前 24 个显式 AnimatorController 中 `UiStateController=21/95`、`VfxOrEffect=3/3`（controllers/clipEdges）。这说明源索引里的 controller-clip 关系可解析，但目前主要是 UI 状态机和特效 clip，不能作为角色身体动画生产关系。
`explicitAnimatorControllerUsages` 继续确认生产动画边界：当前 `withAvatar=15`、`withAvatarAndControllerClipEdges=0`，带 Avatar 的 Animator+Controller 样本都落在 `PreviewOrTimeline/TrackEditorPreview` 一侧，只能作为诊断线索，不能作为角色生产动画关系。
`avatarAnimatorDomains` 进一步统计完整源索引里的 `animator.avatar`：`totalAnimators=6769`、`withController=15`，主要分布为 `WeaponOrProp=1802/0`、`DeviceOrProp=1478/6`、`UiOrPreview=817/1`、`SkeletonSource=690/0`、`VfxOrEffect=325/5`、`CharacterOrPart=228/0`（animators/withController）。这说明 Naraka 很多武器、道具、局部角色部件和骨架源对象都有 Avatar 上下文；Avatar 只能说明骨架/skin 诊断背景，不能在没有显式 clip/controller 关系和模型验收的情况下创建默认模型-动画绑定。
2026-06-26 复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_StaticEnvironment_Clean_Current`：默认代表模型仍是 `models=3`、`ok=3`、`withTextures=3`、`textureAssets=43`、`materialSidecars=15`、`textureLinkErrors=0`，代表模型 glTF validator、AssetLibrary Browser、缩略图和 Dijiang A8 动画诊断均通过。新增 `staticEnvironment.status=ok`，样本 `Smoke_static_jisui_device_bigtree_04_Current` 为 `models=1`、`ok=1`、`withTextures=1`、`textureAssets=12`、`textureLinks=13`、`textureLinkErrors=0`、`materialSidecars=1`，静态树 glTF validator 无 error。该样本只证明显式静态环境/道具 Mesh 扩展链路可用，不改变默认 Library 的 prefab/Animator/GameObject 主线。
2026-06-26 源索引离线扫描 `Animator.avatar -> GameObject -> Transform tree -> SkinnedMeshRenderer`：499 个 `ch_*` Avatar 候选中，265 个层级含 SkinnedMeshRenderer。`ch_fcalw_s_tuzi` 的源索引信号很强（45 个 SkinnedMeshRenderer、45 个材质引用），但导出 bbox 只有约 `0.48 x 0.18 x 0.26`，仍像 `actor_extra_part` 小件，不能作为完整角色样本。最初 `skill_device_prefab` 里的 `ch_m_japan_samurai_ghost` 导出为 `D:\Assets\Naraka\Naraka_CompleteCharacterCandidate_SamuraiGhost_Current`，`model_validation=ok`，5 个 skinned mesh、73 个 skin joints、bbox 约 `1.99 x 3.42 x 3.29`、`texture_links.link_error=0`。继续反查源索引后，`b\6\b6449028544fa466` bundle 内的 SamuraiGhost 根节点更完整：3 个 `ch_m_japan_samurai_ghost` Animator 均显式引用 `ch_m_japan_samurai_ghost_skeletonAvatar`，其中最大根 `pathId=7640773285473327857` 的 Transform 子树约 112 个 GameObject、5 个 SkinnedMeshRenderer、7 个 MeshRenderer。该根导出为 `D:\Assets\Naraka\Naraka_CompleteCharacterCandidate_SamuraiGhost_BundleRoot_Current`，`model_validation=ok`，73 个 skin joints、bbox 约 `2.36 x 3.42 x 4.83`、`texture_links.link_error=0`、18 个贴图资产、6 个材质 sidecar，glTF validator 无 error。该 bundle 同时含 `male_hero_takeda_*` / `ghost_*` Humanoid/Muscle AnimationClip 线索，但没有 `Animator.controller`、`Animation.clip` 或 `AnimatorController.clip` 显式挂接到该模型，导出索引中 `model_animation_candidates=0`；因此它是当前更强的动画前置静态样本，但仍不能直接标成生产动画样本。
同日新增 SamuraiGhost bundle source-index 边界 smoke：`b/6/b6449028544fa466|CAB-43d9a2106c54892c7f775b8d7ab8b193` 必须查到 `animator.avatar=10`、`ch_m_japan_samurai_ghost` 自身 Avatar 行不为 0、同 bundle `source_animation_bindings=55` 且均为 Humanoid/Muscle 线索；同时 `animator.controller=0`、`animation.clip=0`、`animatorController.clip=0`。这把“同包有动画数据但没有显式生产绑定”的手动结论纳入自动烟测。
同日新增 legacy `Animation.clip` 显式边诊断：完整源索引当前有 19 条 `Animation.clip`，目标 AnimationClip 全部能通过 PPtr 解析；域分布为 VFX/Effect 和 Aim/Marker，没有可直接作为角色身体动画生产样本的完整模型关系。该诊断只记录显式 Unity 引用边界，不会改变 `asset_library.json.capabilities.animations=false` 的结论。
同日新增 AnimatorController 生产门禁：完整源索引当前 `Animator.controller=213`、带 Avatar 的 Animator 为 15、带 controller clip edges 的 Animator 为 198，但 `withAvatarAndControllerClipEdges=0`。smoke 会把这个值作为硬门禁；非 0 时必须先做确定性模型-动画关系和视觉验收，不能静默启用默认动画能力。
同日新增 MonoBehaviour -> AnimationClip PPtr 诊断：完整源索引当前有 48007 条脚本字段显式引用 AnimationClip，来自 18857 个 MonoBehaviour，覆盖 19234 个不同 clip。主要脚本域为 `SimpleAnimation=23471/9468/8934`、`UIStateAnimator=20226/6504/6276`、`AnimationPlayableAsset=1769/1769/1568`、`CutsceneAnimHolder=1369/30/1369`、`AnimationTrack=1044/1042/1044`、`SpeedAnimationPlayer=128/44/72`（relations/objects/distinctClips）。2026-06-26 补充字段路径诊断，smoke 输出 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_MonoBehaviourClipFieldPaths_Quick_Current` 已记录 `SimpleAnimation` 的 `m_Clip,m_States.data.clip`、`UIStateAnimator` 的 `logicLayers.data.states.data.animationQueue.data`、`AnimationPlayableAsset` 的 `m_Clip`、`CutsceneAnimHolder` 的 `allAnimClips.data`、`AnimationTrack` 的 `m_InfiniteClip,m_Curves`、`SpeedAnimationPlayer` 的 `m_States.data.clip`。同日继续补充 GameObject 上下文，smoke 输出 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_MonoBehaviourGameObjectContext_Quick_Current` 记录 `SimpleAnimation.gameObjects=9464`、`UIStateAnimator.gameObjects=6504`、`SpeedAnimationPlayer.gameObjects=44`，而 `AnimationPlayableAsset`、`CutsceneAnimHolder`、`AnimationTrack` 为 0，说明前者多挂在实际 GameObject 上，后者更偏 Timeline/配置资产诊断域；随后把摘要 SQL 改为从 `AnimationClip` 反查 `monoBehaviour.pptr`，复验输出 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_MonoBehaviourReverseQuery_Quick_Current` 保持同样计数，`write summary` 首轮约 6.8 秒、重建约 5.9 秒。后续继续扩展脚本动画诊断时仍应优先按需或分层查询。这些是下一步追踪 Naraka 自定义脚本动画入口的重要线索，但不是通用 Unity Animator/Animation/Controller 关系，不能在未解析脚本语义和模型关系前进入默认动画候选。

同日抽样脚本动画挂载 GameObject 的直接组件：`fxattack_male_sw_attack_heavy_02`、`ani`、`node_body`、`hero_wuchen_fuchen` 等高频 `SimpleAnimation` 节点多为 `Transform + MonoBehaviour + Animator`，未在自身 GameObject 上直接出现 `MeshRenderer` / `SkinnedMeshRenderer`；`ActivityArmorHeroProgressWidget` 为 `RectTransform + CanvasRenderer + Animator`，`Gen_Anim_bow` 为 UI/RectTransform 类节点。全量“脚本动画 -> GameObject -> 组件”上下文查询若不小心走错索引会超时，后续若要固化只能做按需、分层或选中模型查询。当前样本说明脚本动画字段是重要入口，但还需要继续追层级子节点、可见 Renderer、Prefab/Avatar/Controller 上下文和模型静态验收，不能把脚本挂载关系直接当生产动画绑定。

同日给 `--list_source_model_animations` 增加按需 `scriptAnimationComponentDiagnostics`：只检查选中 GameObject 和直接子节点上的 MonoBehaviour 组件是否有 AnimationClip PPtr 字段，不做全量深层扫描，也不写 CSV 候选。复验 `ch_m_hadi_lv_s9` 输出 `D:\Assets\Naraka\SourceModelAnimations_HadiBody_ScriptComponentDiag_Current`：命中 8 个同名/特效变体，`candidateCount=0`、`scriptAnimationComponentDiagnostics=0`。复验 `fxattack_male_sw_attack_heavy_02` 输出 `D:\Assets\Naraka\SourceModelAnimations_FxAttackSimpleAnimation_ScriptComponentDiag_Current`：命中 20 个同名节点，`candidateCount=0`、`scriptAnimationComponentDiagnostics=20`，样本行明确为 `diagnosticOnly=true` / `notDefaultModelAnimationRelation=true`，`SimpleAnimation.m_Clip -> fxattack_male_sw_attack_heavy_02_pvpve`，挂载 GameObject 自身 `visibleRendererCount=0`、`animatorCount=1`。随后补充受限子树可见性摘要：每行会在 `gameObject.subtree` 记录当前脚本节点及有限深度后代的 `visibleRendererCount`、`skinnedMeshRendererCount`、`visibleGameObjectSamples` 和 `truncated`。复验 `D:\Assets\Naraka\SourceModelAnimations_FxAttack_SubtreeDiag_Current` 显示 FxAttack 自身仍是无 Renderer 控制节点，但 18 行在受限子树中发现可见 MeshRenderer，`subtreeTruncatedRows=0`；这能帮助追踪 VFX/脚本网格上下文，但仍不证明脚本 clip 会驱动可见模型。该字段只作为诊断证据，不能升级成模型动画生产关系。

同日把选中模型脚本动画组件诊断纳入 Naraka smoke 自动门禁。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_ScriptAnimationComponentDiag_Quick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）：`scriptAnimationComponentDiagnostics.status=ok`，Hadi 样本 `selectedModelCount=8`、`candidateCount=0`、`scriptAnimationRows=0`；FxAttack 样本 `selectedModelCount=20`、`candidateCount=0`、`scriptAnimationRows=20`、`invalidBoundaryRows=0`、`visibleRendererRows=0`、`animatorRows=20`，首条仍为 `SimpleAnimation.m_Clip -> fxattack_male_sw_attack_heavy_02_pvpve`。后续 smoke 摘要还会记录 `subtreeVisibleRendererRows`、`subtreeSkinnedRendererRows`、`subtreeTruncatedRows` 和首行子树样本，并要求 FxAttack 受限子树统计不能截断。该 smoke 继续保持 `asset_library.json.capabilities.animations=false`，因此脚本动画字段只作为诊断证据，不会创建默认模型-动画关系。
随后复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_ScriptSummary_Quick_Current`：`source_model_animation_candidates.json.scriptAnimationComponentDiagnosticSummary` 已成为 `scriptAnimationComponentDiagnostics` 的机器摘要。Hadi 摘要为 `diagnosticSummaryStatus=empty`、`scriptAnimationRows=0`；FxAttack 摘要为 `diagnosticSummaryStatus=diagnosticOnly`、`scriptAnimationRows=20`、`subtreeVisibleRendererRows=18`、`subtreeSkinnedRendererRows=0`、`subtreeTruncatedRows=0`、`invalidBoundaryRows=0`。摘要中的 `defaultCandidateCount` 必须保持 0；它只说明脚本字段和局部/子树可见对象上下文可追踪，不能绕过脚本语义、模型验证、TRS 写回和清晰视觉验收。

同日继续给 `--list_source_model_animations` 补齐 `avatarTosClipDiagnosticSummary` 和 `modelAvatarCompatibilityDiagnosticSummary`。复验 `D:\Assets\Naraka\SourceModelAnimations_Dijiang_AvatarTosSummary_Current`：Dijiang skeleton 对 `mo_pve_b_dijiang_attack_a8_01` 的 TOS hash 覆盖为 `avatarTosStatus=diagnosticOnly`、`avatarTosRows=1`、`avatarTosMaxCoverage=1`，但 `candidateCount=0`、`defaultCandidateCount=0`。复验 `D:\Assets\Naraka\SourceModelAnimations_SamuraiGhost_AvatarCompatibilitySummary_Current`：SamuraiGhost 有 `modelAvatarRows=30`、`highOverlapRows=30`、`maxCoverage=1`，但同样 `candidateCount=0`、`defaultCandidateCount=0`。Hadi 和 FxAttack 对照样本也保持 0 默认候选。这个摘要只把 Avatar/TOS 和模型骨架结构线索机器化，方便后续 solver/oracle 探针选点，不能升级成生产动画关系。

随后把 Avatar/TOS 与模型-Avatar 结构摘要纳入 Naraka smoke 自动门禁。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_AvatarSummaryGate_Quick_Current`（`-SkipBrowserValidation`）：`sourceModelAvatarDiagnostics.status=ok`，Dijiang 摘要为 `candidateCount=0`、`avatarTosRows=1`、`avatarTosMaxCoverage=1`、`invalidBoundaryRows=0`；SamuraiGhost 摘要为 `candidateCount=0`、`modelAvatarRows=30`、`modelAvatarHighOverlapRows=30`、`modelAvatarMaxCoverage=1`、`invalidBoundaryRows=0`。smoke 仍输出 `capabilities.animations=false`，说明这些结构线索只用于后续动画求解选点，不会自动启用生产动画能力。

同日补充 Hadi 模块化边界 smoke：脚本会读取默认代表模型的 `asset_catalog.jsonl` 行，要求 `ch_m_hadi_lv_s9` 继续标记为 `libraryRole=ModularCharacterBase`、`resourceKind=CharacterPart`、`modelCompletenessStatus=modular_incomplete`，且 `modelCompletenessMissingRoles` 至少包含 `Face` 和 `Hair`。该门禁保护“body/服装部件可作为模型素材使用，但不能当完整角色或生产动画 smoke 样本”的结论。

输入探针：

```powershell
AnimeStudio.CLI\bin\Release\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Game163\program\NarakaBladepoint_Data\StreamingAssets" `
  "D:\Assets\Naraka\SourceInputProbe_Current" `
  --game Naraka `
  --probe_source_input
```

代表模型批次定向 Library 导出示例：

```powershell
AnimeStudio.CLI\bin\Release\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Game163\program\NarakaBladepoint_Data\StreamingAssets" `
  "D:\Assets\Naraka\HadiBody_s9_IndexV1_Rebuild_Current" `
  --game Naraka `
  --mode Library `
  --group_assets ByLibrary `
  --profile_3d Core `
  --model_format Gltf `
  --texture_mode Png `
  --animation_package Separate `
  --fbx_animation Skip `
  --source_index "D:\Assets\Naraka\SourceIndex_Full_HeaderFix1\unity_source_index.db" `
  --source_files "4\c\4c08b7069a411750" "d\1\d1d0bc7b6c107e00" "0\f\0f2ab2b1ab070ac0" `
  --path_ids -6619473669887381141 3879445205109982761 3817277305598733592
```

刷新 AssetLibrary v1 索引：

```powershell
AnimeStudio.CLI\bin\Release\net9.0-windows\AnimeStudio.CLI.exe `
  --build_sqlite_index "D:\Assets\Naraka\HadiBody_s9_IndexV1_Rebuild_Current" `
  --source_index "D:\Assets\Naraka\SourceIndex_Full_HeaderFix1\unity_source_index.db" `
  --game Naraka `
  --skip_sqlite_file_index
```

SamuraiGhost 大型 skinned 候选导出示例：

```powershell
AnimeStudio.CLI\bin\Release\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Game163\program\NarakaBladepoint_Data\StreamingAssets" `
  "D:\Assets\Naraka\Naraka_CompleteCharacterCandidate_SamuraiGhost_BundleRoot_Current" `
  --game Naraka `
  --mode Library `
  --group_assets ByLibrary `
  --profile_3d Core `
  --model_format Gltf `
  --texture_mode Png `
  --animation_package Separate `
  --fbx_animation Skip `
  --source_index "D:\Assets\Naraka\SourceIndex_Full_HeaderFix1\unity_source_index.db" `
  --source_files "b\6\b6449028544fa466" `
  --path_ids 7640773285473327857
```

glTF 校验：

```powershell
gltf-transform validate "D:\Assets\Naraka\HadiBody_s9_IndexV1_Rebuild_Current\Models\assets\res\prefab\actor_visual_part\ch_m_hadi\ch_m_hadi_lv_s9\ch_m_hadi_lv_s9.gltf"
gltf-transform validate "D:\Assets\Naraka\FaceMaleBattle_ShaderBoundary_Current\MonoBehaviour_lod0.gltf"
```

## 动画当前能力

当前已验证一个诊断正样本：

```text
D:\Assets\Naraka\Naraka_P2_Hadi_DijiangA8_SourceAvatarTosDirectTrs_Current\mo_pve_b_dijiang_attack_a8_01.animation.gltf
```

报告状态为 `ok`，写出 159 个 animation channel，并明确：

- `avatarInjection.mode=directTrsPathHashMapping`
- `diagnosticOnly=true`
- `notDefaultModelAnimationRelation=true`

当前也有一个正确失败样本：

```text
D:\Assets\Naraka\Naraka_P2_Hadi_Hengdao_InternalHumanoid_SourceAvatarDiagnostic_AfterTosSplit_Current\standalone_animation_gltf_report.json
```

它标记 `forced_internal_humanoid_export_failed`，并说明该 clip 没有身体 Humanoid 曲线，只有 root-motion/辅助 float 或局部 TRS，不能当作人形身体动画生产样本。

因此当前动画结论是：诊断工具链可用，生产级动画库未完成。

## 风险和下一步

- Hadi body 是模块化角色 body/服装部件，当前 `modelCompletenessStatus=modular_incomplete`，不能单独作为完整角色动画 smoke。
- Face/hair 等 Naraka 自定义网格还需要正式装配、skin、tint/custom shader 复刻或人工材质重建；这些问题应被报告和索引标记，不阻塞其它静态模型主线。
- `D:\Assets\Naraka\Naraka_CompleteCharacterCandidate_Xinghui_Current` 验证了一个带 Avatar 的 `ch_f_fcalw_a_xinghui` 候选：模型本体 `ok`，但来源是 `actor_extra_part`，bbox 很小，只有局部挂件/绳带骨骼，材质 `needsCustomizationTint`；它不能替代完整角色 smoke。
- 下一步优先找完整角色 prefab/配置、Avatar、Controller/Clip 的 Unity 显式关系；不要按角色名、骨架兼容或动作名硬绑定动画。
- 扩大 smoke 时应继续覆盖角色、武器、道具、建筑/环境和静态场景物，并用 SQLite、glTF validator、浏览器缩略图和清晰截图共同验收。

## 参考资料

- ResHax Naraka 讨论：https://reshax.com/topic/664-%E3%80%90pc%E3%80%91naraka-bladepoint/
- Rivelia/Studio：https://github.com/Rivelia/Studio
- QuickBMS：https://aluigi.altervista.org/quickbms.htm
