# Naraka 首版可用导出审计

审计日期：2026-06-26

本页记录永劫无间（Naraka: Bladepoint）当前第一版可用导出状态。它不是全量导出报告，而是小范围代表性 smoke 的事实清单，用来判断工具是否已经打通模型、贴图、材质、索引和报告闭环。

## 结论

当前 Naraka P0/P1 静态素材库链路已经可用：`StreamingAssets` 输入、Naraka header 修正、源索引、定向 Library 导出、共享贴图、材质 sidecar、`asset_library.json`、`library_index.db`、glTF 校验和浏览器索引验证都能跑通。

动画现在已经从纯诊断推进到“受限生产预览”阶段。标准 `AnimatorController` 角色生产链仍为 0，代表 Library 继续保持 `asset_library.json.capabilities.animations=false`；但 Zhumu `SimpleAnimation` 样本已经通过一条独立的 `VerifiedAnimationPreview` 门禁，来自脚本 PPtr、TypeTree 自动默认 state、已合并 glTF TRS、glTF validator 和渲染像素运动证据。该关系只在 Zhumu 预览样本库中启用 `capabilities.animations=true`，并固定 `previewOnly=true`、`embeddedModelRequired=true`，不能描述成独立无 skin AnimationClip 或普通 AnimatorController 恢复。

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
| Zhumu soul attack prefab | `D:\Assets\Naraka\Naraka_ZhumuSoul_AttackPrefab_ModelProbe_Current` | SimpleAnimation 已验证预览样本 | `mo_pve_b_zhumu_soul_01` attack prefab 模型导出通过，`model_validation=ok`、带 skin 和贴图；同名源模型的 `SimpleAnimation` 自动默认 state 已升级为 `VerifiedAnimationPreview`，该样本库有 `Animation=1`、候选/关系/usable 关系各 1 条，但仍是嵌入模型的预览包。 |

本轮复查的 SQLite 摘要：

```text
hadi_body: models=1 textures=29 material_sidecars=11 texture_links=32 link_errors=0 custom_shader_materials=0 validation=ok:1
face_shader: models=1 textures=17 material_sidecars=3 texture_links=5 link_errors=0 custom_shader_materials=2 validation=warning:1
weapon_bow: models=1 textures=10 material_sidecars=3 texture_links=12 link_errors=0 custom_shader_materials=0 validation=ok:1
device_hongbao: models=1 textures=4 material_sidecars=1 texture_links=4 link_errors=0 custom_shader_materials=0 validation=ok:1
static_bigtree: models=1 textures=12 material_sidecars=1 texture_links=13 link_errors=0 custom_shader_materials=0 validation=ok:1
samurai_ghost_bundle_root: models=1 textures=18 material_sidecars=6 texture_links=20 link_errors=0 custom_shader_materials=0 validation=ok:1 animation_candidates=0
zhumu_soul_attack_prefab: models=1 textures=10 material_sidecars=2 texture_links=11 link_errors=0 custom_shader_materials=0 validation=ok:1 animations=1 verified_preview_assets=1 animation_candidates=1 relation_animations=1 usable_relation_animations=1 preview_only=true embedded_model_required=true
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

如果后续遇到更多 `_IrisDiffuseTex`、`_IrisNormalTex`、`_IrisSpecTex` 或类似 Naraka 私有 shader 分层槽，当前验收口径保持不变：Mesh、UV、Renderer 材质槽、原始贴图引用和已导出贴图能确定性保留时，应归类为 `unsupportedShader` / `customShaderRequired` / `layeredMaterialUnresolved`，而不是贴图丢失或材质错绑。best-effort PBR 预览可以保留，但必须继续标记为降级预览，不能为了接近游戏画面而硬猜通道混合或覆盖 Unity 原始材质数据。

## 可复现命令

推荐一行 smoke：

```powershell
tools\Export-NarakaFirstUsableSmoke.ps1
```

默认输入 `C:\Game163\program\NarakaBladepoint_Data\StreamingAssets`，默认使用 `D:\Assets\Naraka\SourceIndex_Full_HeaderFix1\unity_source_index.db`，输出到 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_Current`。脚本会跑输入探针，并用完整源索引闭包定向导出四个默认 Library 代表模型：`ch_m_hadi_lv_s9` 角色部件、`ch_f_jiantianshi_lv_s1` 正式路径带骨骼角色视觉部件、`weapon_drop_bow_dongjun` 武器道具、`device_hongbao_02` 普通道具；随后重建 `library_index.db`，并内置检查 `asset_library.json` 的 AssetLibrary v1 合约：`schemaVersion=1`、`libraryKind=AssetLibrary`、`sourceGame=Naraka`、`index=library_index.db`、`capabilities.models=true`、`capabilities.animations=false`，以及 SQLite `metadata/assets/model_validation/texture_links/material_sidecars/library_reports` 核心表。脚本还会检查核心素材库文件契约，要求 `asset_catalog.jsonl`、`model_animations.json`、`model_animations.compact.json`、`animation_bindings.jsonl`、`unity_relations.jsonl`、`unity_relation_summary.json`、`export_manifest.jsonl`、`model_validation.json`、`sqlite_index_summary.json`、`profile_summary.json`、`source_index_usage.json`、`skeletons.json`、`character_assemblies.json` 和 `asset_summary.json` 均存在；其中 `animation_bindings.jsonl` / `unity_relations.jsonl` 在当前无生产动画或旧 JSONL 关系时允许为空，但不能缺失。脚本还会检查根目录 `LIBRARY_README.md` / `SQLITE_INDEX_README.md`，并要求每个代表 Model 目录都有非空 `ASSET_README.md` 和 `MATERIAL_REPORT.md`，确保素材库既有机器索引也有人读入口。脚本还会在本机工具存在时执行 4 个代表模型 glTF validator、AssetLibrary Browser 验证和缩略图渲染。脚本还会默认校验 `D:\Assets\Naraka\FaceMaleBattle_ShaderBoundary_Current`，只把它当作 Naraka 私有 shader/分层材质边界样本，要求贴图链接为 0 错误、`material_sidecars` 保留 custom/layered/degraded 标记、关键贴图槽、导出贴图、未复刻步骤、raw sidecar 和 Unity shader PPtr 证据，并验证它不是贴图丢失或材质错绑。脚本还会只读校验 `D:\Assets\Naraka\Smoke_static_jisui_device_bigtree_04_Current`，把它当作静态环境/道具 Mesh 的显式扩展样本，要求模型验证通过、贴图/材质 sidecar 存在、SQLite 贴图链接无错误；这个检查不改变默认 Library 仍优先 prefab/Animator/GameObject 组合模型的范围。脚本还会只读校验 `D:\Assets\Naraka\Naraka_CompleteCharacterCandidate_SamuraiGhost_BundleRoot_Current`，把它当作更强的 skinned 人形/怪物候选，要求模型验证通过、skin 和贴图存在、skin joint 数量足够、bbox 不再是小挂件级别、SQLite 贴图链接无错误，并要求 `model_animation_candidates` / `model_animation_relations` / `relation_animations` 为 0，防止把同包动画或 Avatar 兼容线索伪装成生产动画关系。脚本还会只读校验 `D:\Assets\Naraka\Naraka_ZhumuSoul_AttackPrefab_ModelProbe_Current`，把它当作 SimpleAnimation 已验证预览样本：模型本体必须 `ok`、带 skin/贴图/材质 sidecar、SQLite 贴图链接无错误；样本库必须 `capabilities.animations=true`，并且 `assets.kind='Animation'`、`resource_kind='VerifiedAnimationPreview'`、`model_animation_candidates`、`model_animation_relations`、`relation_animations` 和 usable 关系各不少于 1。它还必须有 `verified_animation_preview_import.json`，并保持 `productionReadiness=productionPreviewReady`、`previewOnly=true`、`embeddedModelRequired=true`，同时 compact 索引要保留 `confidence=naraka_simple_animation_verified_preview`。脚本还会只读校验 `D:\Assets\Naraka\Zhumu_AttackA4_MergedModelAnimationProbe_Current`：`merge_animation_gltf_report.json` 必须保持 `needs_review`，merged glTF validator 必须通过，三帧 Blender render probe 文件必须存在、PNG 中能识别出可见主体，且 bbox 相对 rest 帧发生可测运动，并且 `standalone_animation_not_production_ready`、`standalone_animation_experimental`、`humanoid_solver_known_limb_risk`、`low_humanoid_channel_coverage` 风险原因必须保留。脚本还会用完整源索引机器检查 SamuraiGhost bundle：必须保留 `Animator.avatar` 和同 bundle Humanoid/Muscle binding 线索，同时 `Animator.controller` / `Animation.clip` / `AnimatorController.clip` 显式边必须为 0。完整源索引里的 legacy `Animation.clip` 显式边也会被机器分类和解析率检查；当前这些边落在 VFX/marker 诊断域，不会启用角色身体动画能力。脚本还会强制 `animatorControllerProductionGate.withAvatarAndControllerClipEdges=0`；如果这个值变成非 0，说明出现了 Animator+Avatar+ControllerClip 生产候选信号，必须重新做模型/clip 关系与视觉验收。脚本同时记录 `monoBehaviourAnimationClipPPtrSummary` 和 `sourceIndexScriptAnimationClipScripts`，把 MonoBehaviour 脚本字段里显式指向 AnimationClip 的 PPtr 作为诊断地图，并额外按 `SimpleAnimation` 聚合 clip 域；Character、HumanNameToken、MonsterOrNpc 域只帮助后续挑选求解样本，仍需要脚本语义、确定性模型关系、TRS 写出和视觉验收，不能直接升级成默认模型-动画绑定。脚本还会默认跑 Dijiang A8 独立动画 glTF 诊断，验证 `Avatar.m_TOS` 路径恢复和动画 glTF 写出；报告必须保留 `diagnosticOnly=true` / `notDefaultModelAnimationRelation=true`，因此它不会把当前手动动画诊断升级成生产动画库能力。只想跑 P0/P1 静态链路时传 `-SkipAnimationDiagnostic`。

脚本结束后会在 `OutputRoot` 写出两个汇总文件：

- `SMOKE_REPORT.md`：人读 smoke 结论，汇总静态模型、glTF 校验、浏览器校验、缩略图、SQLite 索引计数、贴图链接质量门槛、特殊 shader 降级计数、静态环境扩展样本、角色候选样本、动画关系覆盖摘要和动画诊断边界。
- `smoke_summary.json`：机器读 smoke 摘要，用来复查产物路径、`assetLibraryContract`、`libraryCoreArtifacts`、`modelReportEntrypoints`、能力标记、验证状态、SQLite 索引计数、`qualityGates`、`hadiModularBoundary`、`shaderBoundary`、`staticEnvironment`、`characterCandidate`、`zhumuScriptAnimationProbe`、`zhumuMergedAnimationPreview`、`characterCandidateSourceIndexBoundary`、`sourceIndexLegacyAnimationClipDomains`、`animatorControllerProductionGate`、`monoBehaviourAnimationClipPPtrSummary`、`sourceIndexScriptAnimationClipScripts`、`sourceIndexSimpleAnimationClipDomains`、`scriptAnimationComponentDiagnostics`、`sourceModelAvatarDiagnostics` 和动画诊断状态。关键动画边界诊断同时保留在顶层和 `animationRelationCoverage` 下，方便机器审计直接读取。脚本写完后会立刻反读解析，避免报告可读但机器摘要损坏。

这两个文件只汇总 smoke 证据，不会改变正式 `RepresentativeModels` 素材库，也不会把诊断动画写成默认动画关系。

2026-06-26 复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_VerifiedPreview_Quick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic -SkipGltfValidation`）通过，默认代表库仍保持 `models=4`、`ok=4`、`withSkin=3`、`withTextures=4`、`capabilities.animations=false`。新增 `zhumuScriptAnimationProbe.status=ok`，并要求独立 Zhumu 样本库 `capabilities.animations=true`、`animationAssetRows=1`、`verifiedPreviewAssetRows=1`、`modelAnimationCandidateRows=1`、`modelAnimationRelationRows=1`、`relationAnimationRows=1`、`usableRelationAnimationRows=1`；`verified_animation_preview_import.json` 必须保持 `productionReadiness=productionPreviewReady`、`previewOnly=true`、`embeddedModelRequired=true`。同日单独执行 glTF validator 校验 `Animations/VerifiedPreviews/...animation.merged.gltf`，结果为 no errors / no warnings（仅有 unused tangent、empty node、unused texture 等 info）。这标志 Naraka 已有一条可回归的 SimpleAnimation 生产预览关系，但仍不是独立动画库，也不改变代表库默认动画能力关闭的边界。
脚本会要求 `qualityGates.textureLinkErrors=0`；如果 glTF 贴图引用链路断开，smoke 会直接失败。`customShaderRequiredSidecars` / `layeredMaterialUnresolvedSidecars` / `customShaderReferenceSidecars` 只作为 Naraka 私有 shader 边界证据记录，不会被当成贴图丢失。
2026-06-26 复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_ZhumuMergedGate_Quick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）通过，默认代表库保持 `models=4`、`ok=4`、`withSkin=3`、`withTextures=4`，`capabilities.models=true`、`capabilities.animations=false`。新增 `zhumuMergedAnimationPreview.status=ok`、`gltfValidation=ok`、`animationCountAdded=1`、`channelCount=23`、`sourceAssessmentStatus=needs_review`、`renderProbeStatus=ok`、`renderFrameCount=3`；`reasonCodes` 保留 `standalone_animation_not_production_ready`、`standalone_animation_experimental`、`humanoid_solver_known_limb_risk`、`low_humanoid_channel_coverage`。这把 Zhumu “模型 + 单动画”预览诊断从手工记录固化为 smoke 只读门禁，但仍不创建默认动画关系，也不改变生产动画能力。
2026-06-26 继续给 `--merge_animation_gltf` 增加 `animationJointCoverage` 诊断：合并报告会统计动画 channel 命中的 skin joint 数、总 skin joint 数、覆盖率和已/未驱动骨骼样本。重新生成 `D:\Assets\Naraka\Zhumu_AttackA4_MergedModelAnimationProbe_Current` 后，Zhumu A4 预览仍为 `needs_review`，但低覆盖风险变成可审计事实：`channelCount=23`、`animatedSkinJointCount=21`、`skinJointCount=96`、`animatedSkinJointCoverage=0.2188`。Blender 三帧 render probe 已刷新，bbox 从 rest 到 mid/end 有变化；复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_ZhumuCoverage_Quick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）通过，smoke 会要求该覆盖摘要存在且至少命中部分 skin joint。这个字段只服务诊断和后续 solver 选点，不能单独证明动画视觉正确或生产可用。
2026-06-26 继续把 `animationJointCoverage` 扩展到顶点权重层：`vertexWeightCoverage` 会读取 merged glTF 的 `JOINTS_0` / `WEIGHTS_0`，统计动画命中 joint 对实际 mesh 顶点权重的影响范围。重新生成 Zhumu A4 合并报告后，虽然 skin joint 覆盖仍是 `21/96=0.2188`，但可见顶点权重覆盖为 `animatedWeightedVertexCount=12236` / `weightedVertexCount=16668`，`animatedWeightedVertexCoverage=0.7341`、`animatedWeightCoverage=0.6097`；两个 primitive 分别约 73% / 74% 顶点被已驱动 joint 影响。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_ZhumuVertexWeightCoverage_Quick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）通过，smoke 现在要求顶点权重覆盖诊断为 `ok`。这个结果说明 Zhumu 不是“完全没驱动可见网格”，但仍缺主体骨骼覆盖、solver 公式确认和清晰视觉验收，因此继续保持 `needs_review`、`capabilities.animations=false`、默认动画候选 0。
2026-06-26 继续把 Zhumu 合并预览的覆盖诊断拆到主体骨层：`animationJointCoverage.coreBodyCoverage` 会按通用人体主体骨词元统计 Pelvis/Spine/Head/Arm/Leg/Foot/Toe 等核心骨，排除 finger/twist/helper/hair/socket 等辅助骨。重新生成 `D:\Assets\Naraka\Zhumu_AttackA4_MergedModelAnimationProbe_Current` 后，Zhumu A4 仍为 `needs_review`，全量 skin joint 覆盖仍是 `21/96=0.2188`，但主体骨覆盖为 `animatedCoreBodyJointCount=21` / `coreBodyJointCount=22`，`animatedCoreBodyJointCoverage=0.9545`，唯一未直接命中的主体骨样本是 `gMan Pelvis`。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_ZhumuCoreBodyCoverage_Quick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）通过，smoke 现在要求主体骨覆盖诊断存在且至少命中核心骨。这个字段说明“低全量 joint 覆盖”主要被辅助骨、手指、twist 和头发骨稀释，但仍不能替代 solver 公式确认、Pelvis/root 处理和清晰视觉验收；`capabilities.animations=false` 和默认动画候选 0 不变。
2026-06-26 继续把 Zhumu 三帧 render probe 升级为 bbox 运动门禁：smoke 会解析 `render_probe_summary.txt` 的 `rest/mid/end` bbox，并要求 mid 或 end 相对 rest 的最大坐标变化大于阈值，避免只有静态截图文件存在也被误判为动画有效。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_ZhumuRenderBboxMotion_Quick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）通过，`renderBboxMotion.status=ok`、`maxCoordinateDelta=1.4038`，默认代表库仍为 `models=4`、`ok=4`、`textureLinkErrors=0`、`capabilities.animations=false`、`animationSupport.defaultModelAnimationCandidateCount=0`。这个门禁只能证明合并动画产物产生了几何包围盒变化，不能替代清晰视觉验收，也不会把 Zhumu 升级成生产动画关系。
2026-06-26 继续给 Zhumu 三帧 PNG 增加主体可见性门禁：新增 `tools/analyze_render_image_subject.py` 会用图片边框估计背景色，统计前景像素比例和前景 bbox 高度，防止空白图或极小远景截图进入 smoke。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_ZhumuRenderSubjectOccupancy_Quick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）通过，`renderSubjectOccupancy.status=ok`、`minForegroundPixelRatio=0.02394`、`minForegroundHeightRatio=0.154688`，同时 `renderBboxMotion.status=ok`、`maxCoordinateDelta=1.4038`、默认动画候选仍为 0。该字段只证明当前截图里有可见主体，不代表动作自然；同日 `tools/render_gltf_frames.py` 的相机取景改为按相机空间 bbox 自适应正交缩放，后续新生成的人工复查帧会更清楚。
2026-06-26 随后把 `tools/render_gltf_frames.py` 的三帧渲染变成可复现探针参数：`--frames 1,15,30 --frame_labels rest,mid,end --file_prefix zhumu_attack_a4 --summary_text render_probe_summary.txt` 会直接生成 smoke 使用的三张 PNG 和 bbox 摘要。已用该命令重建 `D:\Assets\Naraka\Zhumu_AttackA4_MergedModelAnimationProbe_Current\RenderProbe`，并复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_ZhumuRenderProbeRegenerated_Quick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）通过；新版 `renderSubjectOccupancy.minForegroundPixelRatio=0.11153`、`minForegroundHeightRatio=0.46875`，`renderBboxMotion.status=ok`、`maxCoordinateDelta=1.4038`，整体仍保持 `models=4`、`ok=4`、`capabilities.animations=false`、默认动画候选 0。
2026-06-26 继续把 Zhumu PNG 主体可见性门槛从“非空/不是极小远景”收紧为 `minForegroundPixelRatio>=0.08`、`minForegroundHeightRatio>=0.45`。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_ZhumuRenderSubjectStrict_Quick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）通过，当前三帧最小值仍为 `0.11153` / `0.46875`，`capabilities.animations=false` 和默认动画候选 0 不变。这个门槛只保护截图清晰度，不能替代动作自然性、肢体正确性或 AnimatorController 上下文验收。
2026-06-26 继续复验严格 Zhumu 渲染门槛的默认完整 smoke `D:\Assets\Naraka\Naraka_FirstUsableSmoke_RenderStrict_DefaultFull_Current`：不带跳过项执行 `tools\Export-NarakaFirstUsableSmoke.ps1` 通过，默认代表库保持 `models=4`、`ok=4`、`withSkin=3`、`withTextures=4`，SQLite 贴图链接错误为 0。AssetLibrary Browser 校验和缩略图渲染均为 `ok`，Dijiang A8 独立动画诊断为 `status=ok`、`diagnosticOnly=true`。Zhumu 合并预览门禁为 `status=ok`、`gltfValidation=ok`、`channelCount=23`、`renderSubjectOccupancy.status=ok`，主体占画面最小值为 `minForegroundPixelRatio=0.11153`、`minForegroundHeightRatio=0.46875`，高于当前阈值 `0.08` / `0.45`；`renderBboxMotion.maxCoordinateDelta=1.4038`。本轮完整复验继续要求 `capabilities.animations=false` 和 `animationSupport.defaultModelAnimationCandidateCount=0`，说明严格截图清晰度门禁已进入默认路径，但 Zhumu/Dijiang 仍只是诊断动画证据。

同日继续给 Zhumu 三帧 PNG 增加像素运动门禁：`tools/analyze_render_image_subject.py` 现在会在主体占屏之外计算相邻帧和首尾帧的前景区域像素差，输出 `motion.maxMotionPixelRatio` 与 `motion.maxForegroundMotionRatio`。它只用于防止“bbox 有变化但截图几乎静止”或静态帧误过，不证明动作自然、骨骼正确或可生产复用。实测 `D:\Assets\Naraka\Zhumu_AttackA4_MergedModelAnimationProbe_Current\RenderProbe` 三帧得到 `maxMotionPixelRatio=0.209237`、`maxForegroundMotionRatio=0.819851`，后续 smoke 阈值暂定 `minMotionPixelRatio=0.01`，并继续保持 `capabilities.animations=false`。
2026-06-26 继续补齐 `smoke_summary.json.animationDiagnostic.gltfValidation`，让 Dijiang 独立动画诊断对象自身也携带 glTF validator 状态，避免机器审计必须同时读取 `checks.animationGltfValidation`。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_AnimationDiagnosticGltfField_Current`（`-SkipBrowserValidation`）通过，`checks.animationGltfValidation=ok`、`animationDiagnostic.gltfValidation=ok`、`animationDiagnostic.diagnosticOnly=true`，默认代表库仍为 `models=4`、`ok=4`、`capabilities.animations=false`、`animationSupport.defaultModelAnimationCandidateCount=0`。这只是摘要字段补齐，不改变动画生产能力边界。
2026-06-26 继续给 Dijiang A8 独立动画诊断补齐生产阻断字段：`smoke_summary.json.animationDiagnostic.productionReadiness=blocked`，`blockedProductionRequirements=explicitModelAnimationRelation,validatedModelGltf,productionHumanoidSolverValidation,visualReview`，并在 `SMOKE_REPORT.md` 同步输出。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_DijiangProductionReadiness_Quick_Current`（`-SkipBrowserValidation`）通过，Dijiang 诊断仍为 `status=ok`、`gltfValidation=ok`、`diagnosticOnly=true`、`notDefaultModelAnimationRelation=true`，默认代表库仍为 `models=4`、`ok=4`、`capabilities.animations=false`、默认动画候选 0。这个字段和 Zhumu 的 `productionReadiness=blocked` 一起明确区分“手动/诊断动画 glTF 可写出”与“生产动画库可用”。
2026-06-26 继续给 Zhumu 合并预览补齐生产阻断字段：`smoke_summary.json.zhumuMergedAnimationPreview.productionReadiness=blocked`，`blockedProductionRequirements=explicitModelAnimationRelation,scriptSemanticsOrControllerContext,productionHumanoidSolverValidation,subjectBoneCoverage,visualReview`，并在 `SMOKE_REPORT.md` 人读报告同步输出。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_ZhumuProductionReadiness_Quick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）通过，Zhumu 预览仍为 `status=ok`、`sourceAssessmentStatus=needs_review`、`renderSubjectOccupancy.status=ok`、`minForegroundPixelRatio=0.11153`、`minForegroundHeightRatio=0.46875`，默认代表库仍为 `models=4`、`ok=4`、`capabilities.animations=false`、默认动画候选 0。这个字段专门防止机器消费端把“能打开和渲染的诊断预览”误读成生产可复用动画。
2026-06-26 继续把 Zhumu 合并预览的生产阻断字段升级为 smoke 硬门禁：当 `zhumuMergedAnimationPreview.status=ok` 时，脚本会要求 `productionReadiness=blocked`，并逐项检查 `blockedProductionRequirements` 必须包含 `explicitModelAnimationRelation`、`scriptSemanticsOrControllerContext`、`productionHumanoidSolverValidation`、`subjectBoneCoverage`、`visualReview`。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_ZhumuProductionReadinessGate_Quick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）通过，默认代表库仍为 `models=4`、`ok=4`、`textureLinkErrors=0`、`capabilities.animations=false`、默认动画候选 0。以后如果有人把 Zhumu 诊断预览误升级为生产状态，smoke 会直接失败并要求重新做确定性关系和视觉验收。
2026-06-26 继续把 Dijiang 和 Zhumu 的生产阻断状态升级到最终 `smoke_summary.json` 反读门禁：脚本写完机器摘要后会再次解析 JSON，要求 `animationDiagnostic.productionReadiness=blocked` 且包含 `explicitModelAnimationRelation,validatedModelGltf,productionHumanoidSolverValidation,visualReview`，并在 Zhumu 合并预览为 `ok` 时要求 `zhumuMergedAnimationPreview.productionReadiness=blocked` 且包含 `explicitModelAnimationRelation,scriptSemanticsOrControllerContext,productionHumanoidSolverValidation,subjectBoneCoverage,visualReview`。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_ProductionReadinessSummaryGate_Current`（`-SkipBrowserValidation`）通过，默认代表库仍为 `models=4`、`ok=4`、`textureLinkErrors=0`、`capabilities.animations=false`、默认动画候选 0；Dijiang/Zhumu 的阻断字段都已落在最终机器摘要里，避免消费端只读顶层 JSON 时误判动画已经生产可用。
2026-06-26 继续补齐浏览器缩略图覆盖门禁：默认代表库已有 4 个模型后，`AssetLibraryBrowser.Cli build-thumbnails` 不再写死只生成 3 张，而是按代表 glTF 数量生成并要求缓存文件数不少于 `thumbnailExpectedCount`。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_ThumbnailCoverage_Current`（`-SkipAnimationDiagnostic`）通过，`checks.browserValidation=ok`、`checks.thumbnailRender=ok`、`thumbnailExpectedCount=4`、`thumbnailFileCount=4`，默认代表库仍为 `models=4`、`ok=4`、`textureLinkErrors=0`、`capabilities.animations=false`、默认动画候选 0。这个门禁只加强 AssetLibrary v1 可浏览证据，不改变正式模型、材质或动画能力边界。
2026-06-26 继续复验最新默认完整 smoke `D:\Assets\Naraka\Naraka_FirstUsableSmoke_DefaultFull_ThumbnailCoverage_Current`：不带跳过项执行 `tools\Export-NarakaFirstUsableSmoke.ps1 -OutputRoot ...` 通过。默认代表库保持 `models=4`、`ok=4`、`withSkin=3`、`withTextures=4`，`textureLinkErrors=0`；AssetLibrary Browser 校验为 `ok`，缩略图门禁为 `thumbnailExpectedCount=4` / `thumbnailFileCount=4`。Dijiang A8 独立动画诊断仍为 `status=ok`、`gltfValidation=ok`、`productionReadiness=blocked`，Zhumu 合并预览仍为 `status=ok`、`productionReadiness=blocked`；`capabilities.animations=false` 和 `animationSupport.defaultModelAnimationCandidateCount=0` 不变。这是当前“模型、贴图、材质、索引、报告、浏览器预览、诊断动画边界”一行 smoke 的最新完整证据。
2026-06-26 继续补齐 SQLite 实际文件列表门禁：代表库很小，不应为了重建索引速度传 `--skip_sqlite_file_index`，否则最终 `library_index.db.files` 会被清空，削弱浏览器文件级搜索和文件存在性审计。脚本现在完整重建 AssetLibrary v1 索引，并要求 `sqlite_index_summary.json.counts.files>0` 且 `filesSkipped=0`；`SMOKE_REPORT.md` 的 SQLite 摘要也会写出 files 数。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_SqliteFilesGate_Current`（`-SkipAnimationDiagnostic`）通过，`sqliteCounts.files=225`、`filesSkipped=0`，SQLite 实表 `files` 也为 225 行；默认代表库仍为 `models=4`、`ok=4`、`textureLinkErrors=0`、浏览器校验 `ok`、缩略图 4 张、`capabilities.animations=false`、默认动画候选 0。
2026-06-26 继续复验带 SQLite files 门禁的默认完整 smoke `D:\Assets\Naraka\Naraka_FirstUsableSmoke_DefaultFull_SqliteFilesGate_Current`：不带跳过项执行通过，确认 files 表门禁、浏览器和动画诊断可以同时成立。默认代表库保持 `models=4`、`ok=4`、`withSkin=3`、`withTextures=4`，`textureLinkErrors=0`；`sqliteCounts.files=225`、`filesSkipped=0`，SQLite 实表 `files=225`，分类为 `Index=20`、`Model=97`、`Texture=108`；AssetLibrary Browser 校验为 `ok`，缩略图 `thumbnailExpectedCount=4` / `thumbnailFileCount=4`。Dijiang A8 独立动画诊断仍为 `status=ok`、`gltfValidation=ok`、`productionReadiness=blocked`，Zhumu 合并预览仍为 `status=ok`、`productionReadiness=blocked`；`capabilities.animations=false` 和默认动画候选 0 不变。
2026-06-26 继续把 AssetLibrary v1 动画关系表纳入 Naraka smoke 合约：`Test-AssetLibraryV1Contract` 现在要求 `model_animation_relations` 和 `relation_animations` 表存在，并把 `modelAnimationRelationRows`、`relationAnimationRows`、`usableRelationAnimationRows` 写入 `smoke_summary.json.assetLibraryContract` 和 `SMOKE_REPORT.md`。当 `asset_library.json.capabilities.animations=false` 时，`usableRelationAnimationRows` 必须为 0；以后即使出现诊断/阻断关系行，也不会被误判为可用生产动画。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_AnimationRelationTables_Quick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）通过，默认代表库仍为 `models=4`、`ok=4`、`withSkin=3`、`withTextures=4`，关系表计数为 `modelRelations=0`、`relationAnimations=0`、`usableRelationAnimations=0`，`capabilities.animations=false`、`animationSupport.defaultModelAnimationCandidateCount=0` 不变。
2026-06-26 继续把 `SimpleAnimation` 动画求解选点从字符串样本升级为结构化短名单：`sourceIndexSimpleAnimationClipDomains.probeShortlist` 现在按 `Character`、`MonsterOrNpc`、`HumanNameToken` 域各保留最多 4 条脚本节点/clip/source/pathId 线索，并显式标记 `diagnosticOnly=true`、`notDefaultModelAnimationRelation=true`。smoke 要求短名单至少保留 3 条，防止后续查询退化成只有总数没有可操作样本。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_SimpleAnimationShortlist_Quick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）通过，`probeShortlistCount=12`，样本包括 `ch_f_japan_yaodaoji_lv_s14_wings -> ch_f_japan_yaodaoji_lv_s14_wings_idle`、`ch_f_japan_yaodaoji_lv_ss1_flyingball -> idle/run/sprint`、`faxiang -> mo_pve_b_mingwang2_attack_t1/t2_faxiang`。默认代表库仍为 `models=4`、`ok=4`、`withSkin=3`、`withTextures=4`，该短名单继续 `productionReadiness=blocked`，阻塞项为 `scriptSemantics`、`deterministicModelRelation`、`validatedModelGltf`、`animationTrsExport`、`visualReview`；`capabilities.animations=false` 和默认动画候选 0 不变。
2026-06-26 继续复验默认完整 smoke `D:\Assets\Naraka\Naraka_FirstUsableSmoke_ZhumuMergedGate_DefaultFull_Current`：不带跳过项执行 `tools\Export-NarakaFirstUsableSmoke.ps1` 通过。默认代表库保持 `models=4`、`ok=4`、`withSkin=3`、`withTextures=4`，SQLite `textureAssets=54`、`textureLinks=62`、`materialSidecars=19`、`textureLinkErrors=0`；AssetLibrary Browser 校验和缩略图渲染均为 `ok`，Dijiang A8 独立动画诊断为 `animationDiagnostic=ok` / `animationGltfValidation=ok`。Zhumu 合并预览只读门禁也为 `zhumuMergedAnimationPreview.status=ok`、`gltfValidation=ok`、`channelCount=23`、`renderFrameCount=3`，风险原因继续保留 `standalone_animation_not_production_ready`、`standalone_animation_experimental`、`humanoid_solver_known_limb_risk`、`low_humanoid_channel_coverage`。本轮完整复验继续要求 `capabilities.animations=false` 和 `defaultModelAnimationCandidateCount=0`，说明模型、贴图、材质、索引闭环已经通过，但动画仍停留在诊断/降级预览阶段。
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

同日新增源索引 `SimpleAnimation -> AnimationClip` 域聚合诊断。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_SimpleAnimationDomainsOptimized_Quick_Final`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）通过，整体仍为 `models=4`、`ok=4`、`withSkin=3`、`withTextures=4`，`capabilities.animations=false`。`sourceIndexSimpleAnimationClipDomains.status=ok`，共 `totalRelations=20998`、`distinctClipBuckets=8663`；域分布为 `Fx=18085/7501`、`Other=1263/517`、`HumanNameToken=1109/457`、`MonsterOrNpc=230/95`、`UI=210/42`、`Character=101/51`（relations/distinctClips）。其中 Character/HumanNameToken/MonsterOrNpc 是后续挑选 Naraka 动画 solver 样本的线索，但该字段继续写 `productionReadiness=blocked`，阻塞项为 `scriptSemantics`、`deterministicModelRelation`、`validatedModelGltf`、`animationTrsExport`、`visualReview`，不能绕过 Unity 显式关系和视觉验收。该查询已改为先筛 `SimpleAnimation` MonoBehaviour 再连接 clip，单独 SQL 约 3.6 秒，本次完整 quick smoke 为 77.44 秒；不要退回全量脚本字段笛卡尔式扫描。
随后补充 `SimpleAnimation` 直接 GameObject 上下文统计。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_SimpleAnimationDirectContext_Quick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）通过，整体仍为 `models=4`、`ok=4`、`withSkin=3`、`withTextures=4`，`capabilities.animations=false`，耗时 87.33 秒。新增字段显示所有 `SimpleAnimation` clip 行都挂在带 Animator 的 GameObject 上，但 Character/HumanNameToken/MonsterOrNpc/UI/Other 域的 `rowsWithDirectVisibleRenderer=0`；只有 Fx 域有 32 行直接 Renderer。对应 GameObject 数为 `Fx=7041`、`Other=618`、`HumanNameToken=551`、`MonsterOrNpc=115`、`UI=103`、`Character=41`。这说明疑似角色/怪物动作名多半先落在 Animator/脚本控制节点，不是可直接验收的模型根；后续如果继续推进，应从确定性 prefab/可见 Renderer 子树、Avatar、Controller/Clip 或脚本语义追踪，而不是按 clip 名或 GameObject 名硬绑定。

2026-06-26 继续把“动画关系是否完全没找到”的答案写入最终 smoke 摘要：新增 `sourceIndexScriptAnimationClipScripts`，从完整源索引聚合所有 `MonoBehaviour.script -> monoBehaviour.pptr -> AnimationClip` 关系，并和 `sourceIndexSimpleAnimationClipDomains` 互相校验。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_ScriptClipScripts_Quick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）通过，默认代表库保持 `models=4`、`ok=4`、`withSkin=3`、`withTextures=4`，`capabilities.animations=false`、`modelAnimationRelations=0`。新增摘要为 `totalRelations=43217`、`distinctClipObjects=17138`、`scriptCount=5`，top 脚本为 `SimpleAnimation=20998/8663/scriptPlayable`、`UIStateAnimator=19320/5820/uiState`、`AnimationPlayableAsset=1745/1545/timelineOrCutscene`、`AnimationTrack=1044/1044/timelineOrCutscene`、`SpeedAnimationPlayer=110/66/speedAnimationPlayer`（relations/distinctClips/kind）。结论是：Naraka 不是完全没有动画关系，而是当前没有可直接生产验收的 `AnimatorController` 角色绑定链；大规模线索在脚本字段 PPtr，必须先解析脚本语义、证明确定性模型关系、写出 glTF TRS 并做视觉验收，才能进入默认动画库。

同日继续补齐 `sourceIndexSimpleAnimationVisibleSubtreeProbes`，把已有按需 `--list_source_model_animations` 诊断压成 SimpleAnimation 可见子树摘要，避免为了全库递归扫描拖慢默认 smoke。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_SimpleAnimationVisibleSubtree_Quick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）通过，默认代表库仍为 `models=4`、`ok=4`、`withSkin=3`、`withTextures=4`、`capabilities.animations=false`、`modelAnimationRelations=0`。新增摘要为 `probeCount=2`、`visibleSubtreeProbeCount=2`、`skinnedSubtreeProbeCount=2`、`animatorProbeCount=2`：`mo_pve_b_zhumu_soul_01` 有 `scriptRows=40`、`subtreeVisibleRendererRows=40`、`subtreeSkinnedRendererRows=40`、`animatorRows=40`，首个 clip 为 `mo_pve_b_zhumu2_attack_a4_01_soul`；`ch_f_japan_yaodaoji_lv_s14_wings` 有 `scriptRows=4`、`subtreeVisibleRendererRows=4`、`subtreeSkinnedRendererRows=4`、`animatorRows=4`，首个 clip 为 `ch_f_japan_yaodaoji_lv_s14_wings_idle`。这说明部分 SimpleAnimation 节点确实能落到可见蒙皮模型子树，但摘要继续 `productionReadiness=blocked`，仍不能绕过脚本语义、确定性模型关系、TRS 写出和视觉验收。

同日继续把 SimpleAnimation 字段路径语义上浮到 smoke 摘要。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_SimpleAnimationFieldPaths_Quick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）通过，默认代表库保持 `models=4`、`ok=4`、`withSkin=3`、`withTextures=4`，`capabilities.animations=false`、`modelAnimationRelations=0`。`sourceIndexSimpleAnimationVisibleSubtreeProbes.probes[*].fieldPathCounts` 现在保留 `m_Clip` 和 `m_States.data.clip` 两类字段，并作为 smoke 门禁：Zhumu 为 `m_Clip=20`、`m_States.data.clip=20`，Yaodaoji wings 为 `m_Clip=2`、`m_States.data.clip=2`；`animationNameSamples` 同步记录 Zhumu 多个 attack/pre clip 和 Yaodaoji wings idle clip。该字段解释了 SimpleAnimation 关系目前至少包含默认 clip 与 states clip 两层脚本字段，但仍不是 AnimatorController 生产绑定。

同日继续新增 `sourceIndexSimpleAnimationProbeReadiness`，把 SimpleAnimation 字段、可见 skinned 子树和 Avatar 结构重叠压成下一步求解探针排序。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_SimpleAnimationProbeReadiness_Quick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）通过，默认代表库仍为 `models=4`、`ok=4`、`withSkin=3`、`withTextures=4`，`capabilities.animations=false`、`modelAnimationRelations=0`。摘要推荐 `recommendedNextProbe=mo_pve_b_zhumu_soul_01`：Zhumu 行为 `nextStep=humanoidSolverProbeCandidate`、`scriptAnimationRows=40`、`subtreeSkinnedRendererRows=40`、`modelAvatarHighOverlapRows=40`、`modelAvatarMaxCoverage=1.0`；Yaodaoji wings 行为 `nextStep=avatarOverlapBlockedAttachmentOrCustomSkeletonProbe`、`scriptAnimationRows=4`、`subtreeSkinnedRendererRows=4`、`modelAvatarHighOverlapRows=0`、`modelAvatarMaxCoverage=0.0`。结论是 Zhumu 更适合继续做 Humanoid/TRS 求解探针，Yaodaoji wings 暂时应按附件或自定义骨架边界处理；两者都继续 `productionReadiness=blocked`，不能写入默认动画推荐关系。

同日继续把 SimpleAnimation 公开实现语义固化为机器摘要：`source_model_animation_candidates.json.scriptAnimationComponentDiagnosticSummary.simpleAnimationSemanticSummary` 会把 `m_Clip` 标成 `defaultClip`，把 `m_States.data.clip` 标成 `stateClip`，并统计同一个 SimpleAnimation 组件里二者是否指向同一个 AnimationClip。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_SimpleAnimationSemantic_Quick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）通过，默认代表库仍为 `models=4`、`ok=4`、`withSkin=3`、`withTextures=4`，`capabilities.animations=false`、`modelAnimationRelations=0`。Zhumu 语义摘要为 `componentCount=20`、`defaultClipRows=20`、`stateClipRows=20`、`pairedDefaultStateClipRows=20`、`unresolvedFieldRows=0`；Yaodaoji wings 为 `componentCount=1`、`defaultClipRows=2`、`stateClipRows=2`、`pairedDefaultStateClipRows=1`。该字段只证明脚本字段语义和默认/state clip 配对，不证明运行时选中哪个 state，也不解除 `scriptRuntimeSemantics`、`selectedStateOrPlayCall`、`validatedModelGltf`、`animationTrsExport`、`visualReview` 生产阻断。

同日继续把 SimpleAnimation default/state 配对从计数升级为可复查样本：`simpleAnimationSemanticSummary.pairedClipSamples[]` 现在记录同组件配对的 GameObject、MonoBehaviour 和 AnimationClip 来源、PathID 与 `roles=["defaultClip","stateClip"]`，并额外写 `*PathIdString`，避免 JavaScript 消费端读取 64 位 PathID 时丢精度。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_SimpleAnimationPairedSamples_PathIdString_Quick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）通过，默认代表库仍为 `models=4`、`ok=4`、`withSkin=3`、`withTextures=4`，`capabilities.animations=false`、`modelAnimationRelations=0`。Zhumu 保留 8 条配对样本，首条为 `mo_pve_b_zhumu2_attack_a4_01_soul#-3269044911608736500`；Yaodaoji wings 首条为 `ch_f_japan_yaodaoji_lv_s14_wings_idle#2494205227176918652`。这些样本是下一步 Humanoid/TRS 求解探针输入，仍固定 `diagnosticOnly=true`、`notDefaultModelAnimationRelation=true`，不能写入默认动画推荐关系。

同日发现 `SourceModelAnimationLister` 读取 SQLite PathID 时如果走 `reader.GetValue()` 再 `Convert.ToInt64(...)`，部分 64 位 PathID 会被 provider 中间值按浮点口径四舍五入，导致脚本动画诊断中的 MonoBehaviour/GameObject PathID 与源索引原始值不一致。已改为 `ReadLong` 优先 `GetInt64()`，并在脚本动画诊断 JSON 的 GameObject、MonoBehaviour、AnimationClip 对象上补 `pathIdString`。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_SimpleAnimationPathIdPrecision_Quick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）通过，Zhumu 首条配对样本现在精确为 clip `-3269044911608736500`、MonoBehaviour `-4218170017723787480`、GameObject `3634053173836377605`；Yaodaoji wings 为 clip `2494205227176918652`、MonoBehaviour `1740956470544823793`、GameObject `8603102049881510752`。这只是关系证据精度修复，不改变 `capabilities.animations=false` 和 `modelAnimationRelations=0` 的生产边界。

同日继续把 Zhumu `SimpleAnimation` 关系证据从 PPtr 字段推进到 TypeTree 状态配置。定向导出 `D:\Assets\Naraka\SimpleAnimation_Zhumu_TypeTreeProbe_ExportMode_Current\MonoBehaviour\MonoBehaviour_68217.json` 证明 MonoBehaviour PathID `-4218170017723787480` 内部有 `m_PlayAutomatically=1`、`m_Clip=-3269044911608736500`、`m_States[0].name=Default`、`m_States[0].defaultState=1`，且 state clip 同样指向 `mo_pve_b_zhumu2_attack_a4_01_soul`。随后给源索引 `source_objects.raw_json.monoBehaviour.simpleAnimation` 增加轻量 TypeTree 摘要，并用局部索引 `D:\Assets\Naraka\SimpleAnimation_Zhumu_TypeTreeSourceIndex_Current\unity_source_index.db` 复验：SQL 读数为 `typeTreeStatus=ok`、`playAutomatically=1`、`stateCount=1`、`defaultStateCount=1`、`defaultStateNames=["Default"]`、`clip.pathId=-3269044911608736500`、`stateClipPathIds=[-3269044911608736500]`。再用 `--list_source_model_animations` 输出 `D:\Assets\Naraka\SimpleAnimation_Zhumu_TypeTreeListerProbe_Current\source_model_animation_candidates.json`，`simpleAnimationSemanticSummary` 写出 `typeTreeMetadataRows=2`、`typeTreeMetadataNotIndexedRows=0`、`typeTreePlayAutomaticallyRows=2`、`typeTreeDefaultStateRows=2`，paired sample 的 `simpleAnimationTypeTree` 保留 `Default` state 和 clip PathID。后续又把这类强证据单独结构化为 `automaticDefaultStateClipRows` / `automaticDefaultStateClipSamples[]`：只有 TypeTree 确认自动播放、唯一 default state、默认 clip 和 state clip 都指向当前 AnimationClip 时才计数。该证据说明 Zhumu A4 已有“自动播放默认 state -> 默认/状态 clip”脚本配置链，但它仍固定 `diagnosticOnly=true`、`notDefaultModelAnimationRelation=true`；运行时 Play/Stop 调用、完整模型门禁、Humanoid/TRS 求解质量和清晰视觉验收未通过前，不能升级为生产动画库关系。

同日继续补上旧全量源索引的 TypeTree 回填路径：新增显式维护命令 `--refresh_simple_animation_typetree`，它只加载指定 `--source_files`，把已存在 `source_objects.raw_json` 中的 `SimpleAnimation` 摘要补齐，不重建完整 `unity_source_index.db`，也不创建生产动画关系。对全量索引 `D:\Assets\Naraka\SourceIndex_Full_HeaderFix1\unity_source_index.db` 执行 `--source_files 4\f\4fd035612d51ea0f` 后，报告 `D:\Assets\Naraka\SimpleAnimation_TypeTreeRefresh_ZhumuFullIndex_Current\simple_animation_typetree_refresh_report.json` 显示 `candidateRows=1209`、`loadedPhysicalFileCount=1`、`parsedSimpleAnimationObjects=1209`、`updatedRows=1209`、`missingInLoadedAssetsRows=0`、`rawJsonReadFailedRows=0`。随后复查 `D:\Assets\Naraka\SimpleAnimation_Zhumu_FullIndexAfterRefresh_Current\source_model_animation_candidates.json`：Zhumu 的 `simpleAnimationSemanticSummary` 变为 `simpleAnimationRows=42`、`componentCount=21`、`pairedDefaultStateClipRows=21`、`typeTreeMetadataRows=42`、`typeTreeMetadataNotIndexedRows=0`、`typeTreePlayAutomaticallyRows=42`、`typeTreeDefaultStateRows=42`、`automaticDefaultStateClipRows=21`。这证明全量索引可以稳定查询“GameObject -> SimpleAnimation MonoBehaviour -> 自动播放 Default state -> AnimationClip”的显式 Unity 序列化链。

同日根据本地游戏目录和公开资料调整 Naraka 动画策略：`C:\Game163\program` 是 IL2CPP 形态，主要有 `GameAssembly*.dll` 和 `NarakaBladepoint_Data\il2cpp_data*\Metadata\global-metadata.dat`，没有可直接读取的普通 `Managed` C# 脚本程序集；同时 Unity 的 SimpleAnimation/PlayableGraph 公开实现本来就允许绕过完整 AnimatorController 直接播放 AnimationClip。因此 Naraka 不能继续把“普通 AnimatorController 生产链路”当唯一解法。后续 Naraka profile 允许把满足 `automaticDefaultStateClip`、可见蒙皮子树、Avatar/骨架覆盖、模型验证 ok、glTF TRS 写回 ok、像素/视觉运动 ok 的 SimpleAnimation 样本升级为生产候选；未满足这些门槛的脚本字段、UIStateAnimator、Timeline/PlayableAsset 和特效 clip 继续保持诊断，不计入 `capabilities.animations=true`。

同日用全量索引回填后的状态复跑 quick smoke：`D:\Assets\Naraka\Naraka_FirstUsableSmoke_SimpleAnimationFullIndexRefresh_Quick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）通过，默认代表库保持 `models=4`、`ok=4`、`withSkin=3`、`withTextures=4`、`capabilities.animations=false`、`modelAnimationRelations=0`。报告里的 SimpleAnimation readiness 已更新为 Zhumu `typeTree=40/notIndexed:0`、`playAuto=40`、`defaultState=40`、`autoDefaultStateClip=20`；Yaodaoji wings 仍因未回填所在源文件而显示 `typeTree=0/notIndexed:4`，继续作为附件/自定义骨架边界样本。该 smoke 还保留 Zhumu 合并动画诊断和 glTF validator 检查，用于证明 TRS/渲染探针链路仍能跑通，但默认动画 capability 继续关闭。

同日抽样脚本动画挂载 GameObject 的直接组件：`fxattack_male_sw_attack_heavy_02`、`ani`、`node_body`、`hero_wuchen_fuchen` 等高频 `SimpleAnimation` 节点多为 `Transform + MonoBehaviour + Animator`，未在自身 GameObject 上直接出现 `MeshRenderer` / `SkinnedMeshRenderer`；`ActivityArmorHeroProgressWidget` 为 `RectTransform + CanvasRenderer + Animator`，`Gen_Anim_bow` 为 UI/RectTransform 类节点。全量“脚本动画 -> GameObject -> 组件”上下文查询若不小心走错索引会超时，后续若要固化只能做按需、分层或选中模型查询。当前样本说明脚本动画字段是重要入口，但还需要继续追层级子节点、可见 Renderer、Prefab/Avatar/Controller 上下文和模型静态验收，不能把脚本挂载关系直接当生产动画绑定。

同日给 `--list_source_model_animations` 增加按需 `scriptAnimationComponentDiagnostics`：只检查选中 GameObject 和直接子节点上的 MonoBehaviour 组件是否有 AnimationClip PPtr 字段，不做全量深层扫描，也不写 CSV 候选。复验 `ch_m_hadi_lv_s9` 输出 `D:\Assets\Naraka\SourceModelAnimations_HadiBody_ScriptComponentDiag_Current`：命中 8 个同名/特效变体，`candidateCount=0`、`scriptAnimationComponentDiagnostics=0`。复验 `fxattack_male_sw_attack_heavy_02` 输出 `D:\Assets\Naraka\SourceModelAnimations_FxAttackSimpleAnimation_ScriptComponentDiag_Current`：命中 20 个同名节点，`candidateCount=0`、`scriptAnimationComponentDiagnostics=20`，样本行明确为 `diagnosticOnly=true` / `notDefaultModelAnimationRelation=true`，`SimpleAnimation.m_Clip -> fxattack_male_sw_attack_heavy_02_pvpve`，挂载 GameObject 自身 `visibleRendererCount=0`、`animatorCount=1`。随后补充受限子树可见性摘要：每行会在 `gameObject.subtree` 记录当前脚本节点及有限深度后代的 `visibleRendererCount`、`skinnedMeshRendererCount`、`visibleGameObjectSamples` 和 `truncated`。复验 `D:\Assets\Naraka\SourceModelAnimations_FxAttack_SubtreeDiag_Current` 显示 FxAttack 自身仍是无 Renderer 控制节点，但 18 行在受限子树中发现可见 MeshRenderer，`subtreeTruncatedRows=0`；这能帮助追踪 VFX/脚本网格上下文，但仍不证明脚本 clip 会驱动可见模型。该字段只作为诊断证据，不能升级成模型动画生产关系。

同日把选中模型脚本动画组件诊断纳入 Naraka smoke 自动门禁。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_ScriptAnimationComponentDiag_Quick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）：`scriptAnimationComponentDiagnostics.status=ok`，Hadi 样本 `selectedModelCount=8`、`candidateCount=0`、`scriptAnimationRows=0`；FxAttack 样本 `selectedModelCount=20`、`candidateCount=0`、`scriptAnimationRows=20`、`invalidBoundaryRows=0`、`visibleRendererRows=0`、`animatorRows=20`，首条仍为 `SimpleAnimation.m_Clip -> fxattack_male_sw_attack_heavy_02_pvpve`。后续 smoke 摘要还会记录 `subtreeVisibleRendererRows`、`subtreeSkinnedRendererRows`、`subtreeTruncatedRows` 和首行子树样本，并要求 FxAttack 受限子树统计不能截断。该 smoke 继续保持 `asset_library.json.capabilities.animations=false`，因此脚本动画字段只作为诊断证据，不会创建默认模型-动画关系。
随后复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_ScriptSummary_Quick_Current`：`source_model_animation_candidates.json.scriptAnimationComponentDiagnosticSummary` 已成为 `scriptAnimationComponentDiagnostics` 的机器摘要。Hadi 摘要为 `diagnosticSummaryStatus=empty`、`scriptAnimationRows=0`；FxAttack 摘要为 `diagnosticSummaryStatus=diagnosticOnly`、`scriptAnimationRows=20`、`subtreeVisibleRendererRows=18`、`subtreeSkinnedRendererRows=0`、`subtreeTruncatedRows=0`、`invalidBoundaryRows=0`。摘要中的 `defaultCandidateCount` 必须保持 0；它只说明脚本字段和局部/子树可见对象上下文可追踪，不能绕过脚本语义、模型验证、TRS 写回和清晰视觉验收。

同日继续给 `--list_source_model_animations` 补齐 `avatarTosClipDiagnosticSummary` 和 `modelAvatarCompatibilityDiagnosticSummary`。复验 `D:\Assets\Naraka\SourceModelAnimations_Dijiang_AvatarTosSummary_Current`：Dijiang skeleton 对 `mo_pve_b_dijiang_attack_a8_01` 的 TOS hash 覆盖为 `avatarTosStatus=diagnosticOnly`、`avatarTosRows=1`、`avatarTosMaxCoverage=1`，但 `candidateCount=0`、`defaultCandidateCount=0`。复验 `D:\Assets\Naraka\SourceModelAnimations_SamuraiGhost_AvatarCompatibilitySummary_Current`：SamuraiGhost 有 `modelAvatarRows=30`、`highOverlapRows=30`、`maxCoverage=1`，但同样 `candidateCount=0`、`defaultCandidateCount=0`。Hadi 和 FxAttack 对照样本也保持 0 默认候选。这个摘要只把 Avatar/TOS 和模型骨架结构线索机器化，方便后续 solver/oracle 探针选点，不能升级成生产动画关系。

随后把 Avatar/TOS 与模型-Avatar 结构摘要纳入 Naraka smoke 自动门禁。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_AvatarSummaryGate_Quick_Current`（`-SkipBrowserValidation`）：`sourceModelAvatarDiagnostics.status=ok`，Dijiang 摘要为 `candidateCount=0`、`avatarTosRows=1`、`avatarTosMaxCoverage=1`、`invalidBoundaryRows=0`；SamuraiGhost 摘要为 `candidateCount=0`、`modelAvatarRows=30`、`modelAvatarHighOverlapRows=30`、`modelAvatarMaxCoverage=1`、`invalidBoundaryRows=0`。smoke 仍输出 `capabilities.animations=false`，说明这些结构线索只用于后续动画求解选点，不会自动启用生产动画能力。

随后执行不跳过浏览器和动画诊断的完整 smoke：`D:\Assets\Naraka\Naraka_FirstUsableSmoke_AvatarSummaryGate_DefaultFull_Current`。默认代表模型保持 `models=3`、`ok=3`、`withSkin=2`、`withTextures=3`；SQLite 计数为 `textureAssets=43`、`textureLinks=48`、`materialSidecars=15`、`textureLinkErrors=0`、`modelBindingPaths=410`；`browserValidation=ok`、`thumbnailRender=ok`、`thumbnailFileCount=3`。动画边界门禁也保持预期：Dijiang TOS 诊断 `avatarTosRows=1`、`avatarTosMaxCoverage=1`、`candidateCount=0`；SamuraiGhost 模型-Avatar 兼容诊断 `modelAvatarRows=30`、`modelAvatarHighOverlapRows=30`、`modelAvatarMaxCoverage=1`、`candidateCount=0`；脚本动画诊断 `scriptAnimationRows=20`、`invalidBoundaryRows=0`；Dijiang A8 独立动画 `animationDiagnostic=ok`、`animationGltfValidation=ok`。该完整复验仍要求 `capabilities.models=true`、`capabilities.animations=false`，确保 Avatar/TOS、脚本字段和手动动画 glTF 都只作为诊断证据，不会伪装成默认生产动画库。

随后补充动画诊断摘要的生产门槛字段：`scriptAnimationComponentDiagnosticSummary`、`avatarTosClipDiagnosticSummary` 和 `modelAvatarCompatibilityDiagnosticSummary` 都会写 `productionReadiness=blocked` 与 `blockedProductionRequirements`，把缺少脚本语义、显式动画关系、模型 glTF 验证、动画 TRS 导出和视觉验收这些原因机器化。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_AnimationBlockedFields_Quick_Current`（`-SkipBrowserValidation`）通过：FxAttack 脚本诊断 blocked requirements 为 `scriptSemantics,validatedModelGltf,animationTrsExport,visualReview`；Dijiang Avatar/TOS 诊断为 `explicitAnimatorControllerOrAnimationClipRelation,validatedModelGltf,animationTrsExport,visualReview`；SamuraiGhost 模型-Avatar 兼容诊断为 `explicitAnimationClipRelation,validatedModelGltf,animationTrsExport,visualReview`；三者 `invalidBoundaryRows=0` 且 `capabilities.animations=false`。这只是把“为什么还不能升级生产动画”从说明文字变成可校验字段，不改变当前动画支持等级。

随后补充正式 Library 人读入口的动画支持状态。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_LibraryAnimationStatus_StaticQuick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）通过，`modelReportEntrypoints.rootReadmeAnimationStatus=ok`；`RepresentativeModels\LIBRARY_README.md` 现在会写“当前正式库没有生产动画资产或默认模型-动画候选”，并列出模型动画门禁 `ready=2 / blocked=1 / modelReady=2`，主要阻塞原因为 `material_customization_tint_not_ready` 和 `modular_character_incomplete`。这让打开素材库根目录的人也能看到动画支持等级，而不是只在 smoke 外层或 JSON 里看到 `capabilities.animations=false`。

随后把同一动画支持等级写入 SQLite 机器索引。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_SqliteAnimationSupport_StaticQuick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）通过，`library_index.db.metadata.animationSupport.status=notProductionReady`、`productionReady=false`、`animationAssetCount=0`、`defaultModelAnimationCandidateCount=0`、`modelGateReadyCount=2`、`modelGateBlockedCount=1`、`modelReadyForAnimationCount=2`；`sqlite_index_summary.json.animationSupport` 和 `smoke_summary.json.animationSupport` 也保留同样字段。浏览器或自动化现在可以直接从 SQLite 区分“动画仍诊断/待绑定”与“索引缺失或导出失败”。

同日补充 Hadi 模块化边界 smoke：脚本会读取默认代表模型的 `asset_catalog.jsonl` 行，要求 `ch_m_hadi_lv_s9` 继续标记为 `libraryRole=ModularCharacterBase`、`resourceKind=CharacterPart`、`modelCompletenessStatus=modular_incomplete`，且 `modelCompletenessMissingRoles` 至少包含 `Face` 和 `Hair`。该门禁保护“body/服装部件可作为模型素材使用，但不能当完整角色或生产动画 smoke 样本”的结论。

同日复验 SamuraiGhost 默认代表库边界：尝试把 `b\6\b6449028544fa466` / `pathId=7640773285473327857` 加入默认 `RepresentativeModels` 合批导出时，`Core` profile 仍只生成 3 个默认模型候选；该对象来源在 `assets/res/effect/battle/takeda`，且仍没有 `Animator.controller` / `Animation.clip` / `AnimatorController.clip` 生产关系，因此不应为了扩大代表库而放宽默认过滤或加 `--include_vfx`。脚本报告现在显式写出 `Representative boundary`：`ch_m_japan_samurai_ghost` 保持在只读 skinned candidate gate。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_SamuraiBoundary_StaticQuick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）通过，默认代表库仍为 `models=3`、`ok=3`、`withSkin=2`、`withTextures=3`，`capabilities.animations=false`，`animationSupport.status=notProductionReady`、`defaultModelAnimationCandidateCount=0`；只读 SamuraiGhost 候选仍为 `models=1`、`ok=1`、`withSkin=1`、`skinJoints=73`、`modelAnimationRelationRows=0`。

同日把正式 `actor_visual_part` 路径的 `ch_f_jiantianshi_lv_s1` 加入默认代表库。先用当前 Release CLI 单独重导 `D:\Assets\Naraka\Naraka_Jiantianshi_FormalPath_Current`，确认它不再是旧样本里的 `cloth*_sim` 缺材质 warning：`model_validation=ok`、`modelBodyStatus=ok`、5 个 primitive 均有材质、`materialMissingRendererBinding=false`、`modelAnimationGate=ready`，但 `defaultModelAnimationCandidateCount=0`。随后复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_JiantianshiRepresentative_StaticQuick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）通过，默认代表库提升为 `models=4`、`ok=4`、`withSkin=3`、`withTextures=4`，SQLite 导入 `asset_catalog=4`、`textureAssets=54`、`textureLinks=62`、`materialSidecars=19`、`modelBindingPaths=542`，`textureLinkErrors=0`。继续复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_JiantianshiRepresentative_Browser_Current`（`-SkipAnimationDiagnostic`）通过，`AssetLibrary browser validation=ok`、`Thumbnail render=ok`、缩略图缓存文件数为 3。动画支持仍保持 `capabilities.animations=false`、`animationSupport.status=notProductionReady`、`productionReady=false`、`defaultModelAnimationCandidateCount=0`；模型门禁变为 `ready=3 / blocked=1 / modelReady=3`，唯一 blocked 仍是 Hadi 模块化边界。

同日继续把 `ch_f_jiantianshi_lv_s1` 固化为独立 smoke 门禁，防止它只靠四模型总数间接通过。脚本现在会从 `asset_catalog.jsonl` 检查它必须是 `PrefabPrimary` / `Character`，来源仍为 `c\8\c8f77e18090d4d34`、`pathId=2767142543816441398`，`modelValidationStatus=ok`、`modelBodyStatus=ok`、`materialMissingRendererBinding=false`、材质 primitive 覆盖完整且 `boneCount>0`；同时从 `model_animations.compact.json` 检查 `modelAnimationGate=ready`、`modelReadyForAnimation=true`、默认候选数为 0。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_JiantianshiGate_StaticQuick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）通过，`formalSkinnedRepresentativeBoundary.status=ok`，报告行显示 `materials=5/5`、`bones=92`、`animationGate=ready`、`defaultCandidates=0`，整体仍为 `models=4`、`ok=4`、`capabilities.animations=false`。

同日把 `ch_f_jiantianshi_lv_s1` 的源码动画边界也纳入 smoke。脚本会对正式模型运行 `--list_source_model_animations`，同一份报告同时压成脚本动画诊断和模型-Avatar 结构诊断：脚本侧要求 `selectedModelCount=2`、`candidateCount=0`、`scriptAnimationRows=0`、`invalidBoundaryRows=0`；Avatar 结构侧要求 `candidateCount=0`、`modelAvatarRows>=1`、`modelAvatarHighOverlapRows>=1`、`modelAvatarMaxCoverage>=0.9` 且默认候选仍为 0。复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_JiantianshiAnimationBoundary_Quick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）通过，实际 Jiantianshi 诊断为 `scriptAnimationRows=0`、`modelAvatarRows=7`、`modelAvatarHighOverlapRows=6`、`modelAvatarMaxCoverage=1`、`invalidBoundaryRows=0`。代表库仍为 `models=4`、`ok=4`、`withSkin=3`、`withTextures=4`，SQLite 仍为 `textureAssets=54`、`textureLinks=62`、`materialSidecars=19`、`modelBindingPaths=542`、`modelAnimationCandidates=0`，`capabilities.animations=false`、`animationSupport.defaultModelAnimationCandidateCount=0` 不变。该门禁只说明正式模型已有可追踪的 Avatar 结构线索，不能替代显式 Animator/Controller/Clip 关系、TRS 写回和视觉验收。

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
  --source_files "4\c\4c08b7069a411750" "c\8\c8f77e18090d4d34" "d\1\d1d0bc7b6c107e00" "0\f\0f2ab2b1ab070ac0" `
  --path_ids -6619473669887381141 2767142543816441398 3879445205109982761 3817277305598733592
```

刷新 AssetLibrary v1 索引：

```powershell
AnimeStudio.CLI\bin\Release\net9.0-windows\AnimeStudio.CLI.exe `
  --build_sqlite_index "D:\Assets\Naraka\HadiBody_s9_IndexV1_Rebuild_Current" `
  --source_index "D:\Assets\Naraka\SourceIndex_Full_HeaderFix1\unity_source_index.db" `
  --game Naraka
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

2026-06-26 追加 Zhumu `SimpleAnimation` 定向 Clip 探针：`mo_pve_b_zhumu2_attack_a4_01_soul` 来源为 `4/f/4fd035612d51ea0f|CAB-6fdd8ff580c579cc6a7b605e4f718290`、`pathId=-3269044911608736500`。最初用 `--types AnimationClip:Both --export_full_decoded_animation_curves --source_files 4\f\4fd035612d51ea0f --path_ids -3269044911608736500` 导出时，`AnimationClipExtensions.FindTOS` 在扫描上下文里的 `Animator` / `Animation` / `GameObject` TOS 来源时遇到空 PPtr 并抛出 `NullReferenceException`。这不是 Clip 本体坏，也不是材质/贴图问题；缺失的 TOS 来源只能让路径映射降级，不能让 AnimationClip 导出中断。

修复后同一命令能导出 sidecar：

```text
D:\Assets\Naraka\Zhumu_AttackA4_SimpleAnimationClip_DecodedProbe_AfterTosGuard_Current\Animations\mo\mo_pve_b_zhumu2_attack_a4_01_soul.animation_asset.json
```

sidecar 状态为 `animationType=MixedHumanoidTransform`、`decoded.status=ok`、`bindingSource=AnimationClip.m_ClipBindingConstant`，共有 131 个 binding，其中 111 个 `HumanoidMuscle`、20 个 `Transform`。普通文件入口不强制内部求解时会正确失败并写报告：`playbackKind=MixedHumanoidMuscleAndTransformTrs`，直接 Transform 曲线没有匹配到模型 glTF 节点，主体身体动作必须先走 Humanoid/Muscle 求解。显式传 `--preview_avatar base_model_female_165cm_skeletonAvatar --preview_force_internal_humanoid_solve` 后，工具能生成诊断用无 mesh 独立动画 glTF：

```text
D:\Assets\Naraka\Zhumu_AttackA4_StandaloneAnimationGltfProbe_ForcedInternal_Current\mo_pve_b_zhumu2_attack_a4_01_soul.animation.gltf
```

`gltf-transform inspect` 显示该文件包含 1 个动画、23 个 channel、757 个 keyframe；报告消息为 `experimental_solved_known_limb_formula_risk`。这说明 AnimeStudio 的 Naraka Humanoid/Muscle 诊断链路已经能从脚本动画 Clip 走到 glTF TRS 输出，但仍是显式手动预览和内部 solver 实验结果，`avatarInjection.diagnosticOnly=true`、`notDefaultModelAnimationRelation=true`，不能写入默认 `model_animations.json` 推荐关系，也不能改变 `capabilities.animations=false`。

随后用 `--merge_animation_gltf` 把该 standalone 动画合并回 Zhumu 静态模型，输出：

```text
D:\Assets\Naraka\Zhumu_AttackA4_MergedModelAnimationProbe_Current\mo_pve_b_zhumu_soul_01__mo_pve_b_zhumu2_attack_a4_01_soul.animation.merged.gltf
```

`merge_animation_gltf_report.json` 状态为 `needs_review`，新增 1 个动画、23 个 channel，模型 glTF validator 无 error/warning，`gltf-transform inspect` 能看到 2 个 skinned mesh、2 个 baseColor 材质和动画。报告仍明确列出 `standalone_animation_not_production_ready`、`standalone_animation_experimental`、`humanoid_solver_known_limb_risk` 和 `low_humanoid_channel_coverage`，所以它只能作为模型+单动画预览诊断产物。Blender 5.1 可导入并渲染 rest/mid/end 三帧：

```text
D:\Assets\Naraka\Zhumu_AttackA4_MergedModelAnimationProbe_Current\RenderProbe\zhumu_attack_a4_rest.png
D:\Assets\Naraka\Zhumu_AttackA4_MergedModelAnimationProbe_Current\RenderProbe\zhumu_attack_a4_mid.png
D:\Assets\Naraka\Zhumu_AttackA4_MergedModelAnimationProbe_Current\RenderProbe\zhumu_attack_a4_end.png
```

三帧 bbox 分别约为 `(-1.7855,-1.9233,-2.4681) -> (2.0541,1.3011,1.9651)`、`(-2.1099,-1.6354,-1.3669) -> (1.7240,2.7049,1.8846)`、`(-1.8469,-1.4915,-1.0680) -> (1.4500,2.6488,1.3946)`，说明动画确实驱动了模型节点；但它仍缺少正式 AnimatorController/脚本语义、主体骨骼覆盖和清晰视觉验收，不能升级为生产动画能力。

同日继续补充 `--merge_animation_gltf` 的 root motion 祖先诊断：`animationJointCoverage.rootMotionCoverage` 会统计未直接命中 skin joint、但目标节点是 skin joint 祖先的 TRS channel。重新生成 `D:\Assets\Naraka\Zhumu_AttackA4_MergedModelAnimationProbe_Current` 后，Zhumu A4 报告显示 `nonSkinChannelCount=2`、`skeletonAncestorChannelCount=2`、`translationChannelCount=1`、`rotationChannelCount=1`，两个 channel 都落在节点 `fx_battle_zhumu2_attack_a4_01_soul`，其下有 96 个 skin joint。这说明 `RootT/RootQ` 没有在 standalone -> model 合并时丢掉，而是写到了骨架容器祖先；但这仍只是解释 root motion 落点的诊断字段，不能证明 Pelvis/root 生产处理已经正确，也不能改变 `capabilities.animations=false`。

复验 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_ZhumuRootMotionCoverage_Quick_Current`（`-SkipBrowserValidation -SkipAnimationDiagnostic`）通过：代表库保持 `models=4`、`ok=4`、`withSkin=3`、`withTextures=4`，Zhumu 合并预览保持 `status=needs_review` 和 `productionReadiness=blocked`，root motion 祖先 channel 门禁通过，默认 `modelAnimationRelations=0`、`relationAnimationRows=0`、`capabilities.animations=false` 不变。下一步如果要把它推进到生产动画，需要恢复 SimpleAnimation 状态语义、确认目标模型根和 Avatar，并做清晰视觉验收；不能只靠这两个祖先 channel 升级默认动画关系。

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

2026-06-26 继续按“不要卡死普通 AnimatorController”的方向推进：Naraka 本地游戏目录是 IL2CPP 形态，完整源索引里 `AnimatorController` 生产链路仍集中在 UI/VFX/preview 域，而大量动作关系落在 `SimpleAnimation` MonoBehaviour PPtr 与 TypeTree 配置中。结合 Unity 公开 `SimpleAnimation` 本身是基于 PlayableGraph 的轻量组件这一点，当前判断是 Naraka 很可能通过脚本/PlayableGraph/私有 IL2CPP 运行时代码驱动一部分动作，不能只等 `Animator.controller -> AnimatorController.clip`。

因此新增显式提升命令 `--apply_verified_animation_preview`，允许把已经过严格验证的 “模型 + 单动画” glTF 预览写回 Library 索引。已在 Zhumu A4 样本执行：

```text
D:\Assets\Naraka\Naraka_ZhumuSoul_AttackPrefab_ModelProbe_Current
```

输入证据为 `SimpleAnimation` 自动默认 state TypeTree、clip/GameObject/MonoBehaviour PPtr、`merge_animation_gltf_report.json`、三帧渲染主体运动报告和静态模型验收。提升后结果为：

- `asset_library.json.capabilities.animations=true`
- `library_index.db.metadata.animationSupport.productionReady=true`
- `assets.kind=Animation` 为 1
- `model_animation_candidates` 为 1
- `relation_animations.is_usable_candidate=1`
- 动画资产输出到 `Animations/VerifiedPreviews/mo_pve_b_zhumu_soul_01__mo_pve_b_zhumu2_attack_a4_01_soul/...animation.merged.gltf`

该资产标记为 `resourceKind=VerifiedAnimationPreview`、`animationType=ModelAnimationPreviewGltf`、`productionReadiness=productionPreviewReady`、`previewOnly=true`、`embeddedModelRequired=true`。这表示它是生产可用的模型绑定预览关系，不是可任意套到其它模型的独立动画包；它也不改变默认代表库仍需继续保护“普通脚本字段不能直接升级”的规则。

2026-06-26 追加 SimpleAnimation 短名单首样本深查门禁：quick smoke 现在会对 `ch_f_japan_yaodaoji_lv_s14_wings` 运行定向 `--list_source_model_animations`，输出到：

```text
D:\Assets\Naraka\Naraka_FirstUsableSmoke_SimpleAnimationShortlistProbe_Quick_Current\SourceModelAnimation_YaodaojiWings_ShortlistProbe\source_model_animation_candidates.json
```

本轮验证结果：`selectedModelCount=14`、`candidateCount=0`、`scriptAnimationRows=4`、`subtreeVisibleRendererRows=4`、`subtreeSkinnedRendererRows=4`、`subtreeTruncatedRows=0`、`animatorRows=4`，首条脚本为 `SimpleAnimation`，字段 `m_Clip` 指向 `ch_f_japan_yaodaoji_lv_s14_wings_idle`。Avatar 兼容诊断也保持 `candidateCount=0`、`avatarTosRows=0`、`modelAvatarRows=14`、`highOverlapRows=0`、`invalidBoundaryRows=0`。这说明短名单首样本确实有“脚本 Clip 引用 + 可见 skinned 子树 + Animator/Avatar 上下文”的后续求解价值，但当前仍是 `diagnosticOnly` / `productionReadiness=blocked`，不能升级成默认 `model_animations.json` 推荐关系，也不能改变 `asset_library.json.capabilities.animations=false`。

随后复验最新默认完整 smoke：

```powershell
tools\Export-NarakaFirstUsableSmoke.ps1 -OutputRoot "D:\Assets\Naraka\Naraka_FirstUsableSmoke_YaodaojiShortlist_DefaultFull_Current"
```

本次未跳过浏览器或动画诊断，完整闭环通过：默认代表库保持 `models=4`、`ok=4`、`withSkin=3`、`withTextures=4`、`textureLinkErrors=0`；`AssetLibrary Browser` 校验为 `ok`，缩略图为 `thumbnailExpectedCount=4` / `thumbnailFileCount=4`；Dijiang A8 独立动画诊断和 glTF validator 均为 `ok`；Zhumu 合并预览保持 `status=ok`、`renderProbeStatus=ok`、`productionReadiness=blocked`；Yaodaoji wings 短名单首样本保持 `scriptAnimationRows=4`、`subtreeSkinnedRendererRows=4`、`productionReadiness=blocked`。同时 `capabilities.animations=false`、`animationSupport.defaultModelAnimationCandidateCount=0`、`modelAnimationRelations=0`、`relationAnimationRows=0` 不变，说明当前一行 smoke 已覆盖模型、贴图、材质、索引、报告、浏览器预览和诊断动画边界，但生产动画库仍未启用。

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
