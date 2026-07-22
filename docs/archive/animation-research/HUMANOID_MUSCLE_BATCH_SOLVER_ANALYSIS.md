# Humanoid/Muscle 动画与 Unity Bake 规范评估

本文重新评估当前规范：

> 生产级动画导出不要求使用者再进 Unity 手工 bake；最终必须由 AnimeStudio 写出可脱离 Unity 使用的 glTF TRS/weights。求解可以来自 AnimeStudio 直接解析，也可以来自自动化、高吞吐、可复现的 Unity native Humanoid 求解。

结论：

- 这条规范的方向仍然正确。它保护的是“可用素材库”的最终可用性、可追溯性和批量稳定性，不是单纯排斥 Unity，也不是强制内部 solver 替代 Unity native Humanoid。
- 动画还原路线包括：Generic/Transform/BlendShape 直接写 glTF；ACL transform 直接写 glTF；ACL scalar / Humanoid muscle 进入 `UnityBakeAccelerated` 或 AnimeStudio 内部 solver；AnimatorController 上下文不足的 clip 先标注，不硬说可播放。
- 当前放宽规范后，Unity bake 可以作为求解手段，但最终目标不变：输出必须是可脱离 Unity 使用的 glTF TRS/weights 动画数据。`UnityBakeAccelerated` 应定义为高吞吐 Humanoid/Muscle solver worker，负责调用 Unity 6 native Humanoid/Muscle 求解器，不继承完整 prefab + PlayableGraph + Transform 逐帧读回的重链路。
- `UnityBakeAccelerated` 需要一个通用 Unity 宿主来调用 Unity native 层。这个宿主应是干净的 `AnimeStudioUnityBakeWorker`，作为本机工具环境提前创建、长期复用，不绑定任何单一游戏项目。
- `UnityBakeAccelerated` 只允许基于当前统一支持的 Unity 6 worker。不能为了原神或其他旧游戏回退依赖 Unity 2017.4.40f1 等旧 Editor；如果 Unity 6 无法加载或求解某游戏的 Avatar/Clip，宁可标为不支持 UnityDerived 批量 bake，也不要引入旧 Unity 版本矩阵。
- 当前阶段可以继续把 worker 作为显式求解器验证，不直接把未验收结果写入默认可信推荐；一旦 `UnityBakeAccelerated` 满足确定性关系、Avatar 来源、性能报告、glTF 写回和清晰视觉验收，它可以进入生产求解路径。
- 建议把规范从“禁止 Unity bake”升级成三层：`ProductionDirect`、`UnityOracle`、`UnityBakeAccelerated`。默认素材库的最终动画都必须是 glTF TRS/weights；Unity 派生 TRS 必须标明来源、Unity 版本、Avatar、采样率、耗时、可复现风险和视觉验收状态。

2026-06-25 失败收尾：

- 本轮 Endfield 人形动画求解没有达到生产验收。Pelica 等样本证明模型、Avatar、AnimationClip、AnimatorController 诊断链路可复现，但 IK goal、TDOF、BlendTree、runtime 参数、additive/layer 语义仍未完整求解，视觉上仍有 idle 语义失败或需要诊断路径才能改善的情况。
- 后续重启动画研究前先读 `docs/archive/animation-research/ENDFIELD_ANIMATION_SOLVER_FAILURE_HANDOFF.md`。该文档记录本轮执行过程、已确认有效的旧原神/Browser bake 经验、失败点、代码安全边界和下一轮建议。
- 当前所有 Endfield Unity bake / UnityOracle / recovered controller 结果继续保持保守门禁：`writesReusableGltfTrsCandidate=false`、`diagnosticOnly=true` 或 `needs_review` 时，不得进入 production smoke 或默认可播放推荐。

2026-06-24 旧 Genshin / Browser Unity bake 审计补充：

- 旧 `AnimeStudio.LibraryBrowser` 双击动画触发的 Unity bake 不是生产导出热路径，但有可复用经验：它只允许 Unity 显式关系候选进入 bake，要求可信 Avatar oracle，接受 `static_pose`、`needs_review`、`needs_animator_controller_context` 等非可播放终态写回缓存，并用 `frameVaryingTracks + Avatar/Clip 信任证明 + glTF extras` 才把结果标为“可播放”。
- 可迁移到 Endfield 的精华是 `AnimeStudioPlayableBaker` 的 Unity `Animator` / `AnimationClipPlayable` / `AnimatorControllerPlayable` / `AnimationLayerMixerPlayable` 采样框架、`ImportedAvatar` 探针、`ImportedAnimationClip` / `ImportedAnimatorController` 精确映射、`animation_bake_cache` 终态缓存和报告门禁。这些能力适合作为 `UnityOracle` 对照和 `UnityBakeAccelerated` 不足时的自动 fallback。
- 不能迁移的是旧重链路本身：不能要求用户为每个游戏维护临时 Unity 工程、插件/helper、手动导入一堆临时资产，再靠浏览器双击时临时 bake 才能使用动画。生成的 single-state / multi-layer controller 也只能是诊断，不等价于游戏原始 RuntimeAnimatorController。
- 对 Pelica / Endfield 的直接启发是：limb goal / TDOF 这类曲线不能只靠 `UnityBakeAcceleratedSolver` 的离线 `HumanPoseHandler.SetHumanPose/SetInternalHumanPose` 快路径消费；必须优先尝试原始 `AnimationClip + AnimatorController + layer/mask/BlendTree/IK` 语义，或在直接 solver 中等价实现。没有原始 controller 或 controller 参数/BlendTree/IK 语义不完整时，报告必须阻断生产结论。
- 2026-06-25 再次对照 Pelica 输出后确认：旧原神链路最值得复用的是“精确关系 + 精确信任门禁 + 终态缓存”，不是“能 bake 就算成功”。`Endfield_PelicaActor_IdleLoop_ProvidedControllerFullClipClosure_Current` 已能精确加载 ImportedAvatar、ImportedAnimationClip 和 recovered ImportedAnimatorController，并应用 layer mask，但没有启用 IK goal driver 时清晰截帧仍显示双手抬高、右手贴头、左手悬胸，`idle_loop` 视觉失败。`Endfield_PelicaActor_IdleLoop_AcceleratedAutoFallback_ControllerIk_Current` 自动打开 unsupported Humanoid 曲线 probe 和 editor-curve IK goal driver 后，双手落回身体两侧，手腕扭曲明显改善；但报告仍正确标为 `diagnostic_limb_ik_goal_driver` / `writesReusableGltfTrsCandidate=false`。下一步应把这条证据用于恢复或直接求解 Hand/Foot IK goal、TDOF、controller layer mask、BlendTree 参数、additive reference pose 和 runtime 权重，而不是把无 IK 的 provided-controller TRS 当作可复用动画。

2026-06-22 Endfield gameplay 复测补充：

- `A_actor_yvonne_battle_attack_01` 等 gameplay clip 虽然是 `MixedHumanoidTransform`，但直接 Transform 轨道主要落在 twist、`tx/ty/tz plus/minus` 修正骨、手指、衣袖、飘带、面部等辅助节点；`Bip001_L_UpperArm`、`Bip001_R_UpperArm`、`Forearm`、`Spine`、`Thigh` 等主体骨没有可直接使用的 rotation channel。
- 这类辅助 Transform 不能把 clip 标成 `standaloneBodyBakeReady=direct_transform_body_trs`。当前代码已收紧 core body TRS 判定：只有明确主体骨叶子名命中，才算核心身体覆盖；修正骨、附件、面部和手指不能升级生产状态。
- 手动导出/合并验证目录：`D:\Assets\fangzhou\Endfield_YvonneGameplayManualDiagnostic_*`。内部 Humanoid solver 路径因为目标 Avatar 缺少可用 axis，主体骨 `solvedBodyTrackCount=0`；直接 TRS 路径能生成 glTF channel，但视觉上手臂仍保持基础横展姿态，不能作为动作恢复成功样本。
- gameplay controller 仍存在关系缺口：`P_actor_yvonne_01` 的 Animator 序列化 controller 为空，而 `AC_chr_0017_yvonne` 等 battle controller 在源索引里是孤立 controller。默认推荐关系不能靠名字猜；后续应继续查运行时配置、MonoBehaviour、Addressables 或显式 profile 规则，并把 fallback 明确标注。

2026-06-24 Pelica 共享 Avatar 桥接复测补充：

- `P_actor_pelica_01` 是比白模/Timeline 实例更适合推进的真实角色模型样本：静态模型导出材质、贴图、skin 和 glTF validator 均通过，可进入动画关系诊断。
- 确定性关系不是来自名称猜测，而是 `data_facemorph_avatar_pelica` 同时 PPtr 到模型和 `SK_actor_pelica_01Avatar`，再通过同 Avatar 找到带 controller 的 `P_npc_npc_chr_0004_pelica -> AC_npc_human_girl_pelica_optNew -> A_actor_pelica_idle_loop`。这类 `sharedAvatarController` 候选应保留完整 `relationEvidence`，默认标 `explicitCandidateRequiresVisualValidation`，在视觉和报告通过前不进入生产推荐。
- Avatar recovery 已能从桥接证据自动恢复 `Assets/AnimeStudioBake/ImportedAvatar/SK_actor_pelica_01Avatar.asset`，Unity probe 显示 `isValid=true`、`isHuman=true`，说明 Avatar oracle 这一步不是当前阻塞点。
- `UnityBakeAccelerated` result 能写出 glTF TRS 诊断文件，但 apply 报告为 `needs_review_unsupported_humanoid_curves`：只写出 `21` 条 TRS track，源 clip 仍有 `34` 条当前 HumanPoseHandler 路径没有消费的 Humanoid 曲线，其中 limb goal `28` 条、TDOF `6` 条。Blender 截图能看到模型材质完整，但手臂和手部姿态异常。
- 结论：Pelica 链路证明“模型过门 -> 确定性关系 -> Avatar 恢复 -> 高吞吐 TRS 写回”已经打通；真正的动画缺口是 limb IK/TDOF 等 Unity Humanoid 曲线没有进入求解。下一步应实现完整 `AnimationClip` / `PlayableGraph` 采样，或在直接 solver 中显式处理 limb goal、TDOF 与 root/body 曲线，再用同样的模型门禁、报告和截图验收。

2026-06-24 Pelica PlayableGraph 对照补充：

- 同一模型和同一动画用完整 Unity `AnimationClip` / `PlayableGraph` 诊断链路复测，显式使用 `Assets/AnimeStudioBake/ImportedAvatar/SK_actor_pelica_01Avatar.asset` 和恢复出的 `Assets/AnimeStudioBake/ImportedAnimationClip/A_actor_pelica_idle_loop.anim`。
- Unity result 为 `status=ok`、`isHumanMotion=true`、`animationClipSource=unityAssetPaths.animationClip`、`importedAvatarAssetValid=true`、`changedTrackCount=105`。apply 后 `avatarTrust.source=imported_unity_avatar_asset`，glTF validator 无 error，`frameVaryingTracks=78`、`coreBodyFrameVaryingTracks=17`。
- Blender 中帧和手部近景比 `UnityBakeAccelerated`/`HumanPoseHandler` 快速路径少了一些手指外翻，但人工复查后仍然失败：`idle_loop` 语义下角色双手异常抬高，手腕明显扭曲，不能作为正确 idle。说明 Pelica 的主要问题不只是快速路径没有消费 limb goal/TDOF，也包括当前自动 fallback 只用生成的单状态 controller 采样单个 clip，缺少原始 AnimatorController 的完整 state/layer/blend/override 上下文。
- 2026-06-24 复写 apply report 后新增 `animationSolve` 分层字段。初版报告把 11 个 skin 外动画目标当作阻断项；复查后确认它们是 `Bip001_Footsteps`、`Head_Lookat_Joints`、`wep_M` 和 IK foot/knee/hand/weapon 控制点，均为无 mesh、无可见 mesh 子节点的辅助控制节点，不是主体骨骼或 skin joint 缺失。报告门禁已改为把这类节点写入 `AuxiliaryNonSkinTargets`，真正阻断项 `InvalidTargets` 只保留可疑 skin 外目标。
- 旧 `Endfield_PelicaActor_IdleLoop_PlayableBake_Current` 对照目录已被后续清理；当前可复现证据改为路径正确的新 reexport 库和自动 fallback：`D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_ReexportForFallback_Current` 与 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_AcceleratedAutoFallback_Reexport_Current`。
- 新 fallback report 为 `mode=UnityOraclePlayableGraphFallback`、`fallbackReason=unsupported_limb_goal_or_tdof_curves`、`productionReady=false`；Unity result 为 `status=ok`、`isHumanMotion=true`、`animationClipSource=unityAssetPaths.animationClip`、`importedAvatarAssetValid=true`、`changedTrackCount=105`。旧 apply report 曾写 `status=needs_review`、`writesReusableGltfTrsCandidate=true`，但这是过于乐观的报告语义；`animatorControllerSamplingMode=generated_single_state_controller` 只能证明单 clip 诊断采样跑通，不能证明原游戏 idle 状态恢复正确。
- 新截图目录为 `Frames_ModelRest_Current` 与 `Frames_Animated_Current`。人工复查全身中帧和用户提供的手部近景后，应判定动画视觉失败：双手高举、手腕扭曲，动作语义明显不符合 idle。glTF validator 无 error、`frameVaryingTracks>0`、`coreBodyFrameVaryingTracks>0` 只能证明结构写回，不是正确性证据。
- 结论：Pelica 仍是当前最好的“模型通过 + 确定性关系 + Avatar/clip 恢复 + 失败动画回归”样本，但不能再称为准确性正证据。后续必须恢复并采样原始 `AC_npc_human_girl_pelica_optNew` 的 RuntimeAnimatorController state/layer/blend/override 语义，或在直接 solver 中等价复现这些上下文；生成的单状态 controller 结果只能标为 `needs_original_animator_controller_context` / 视觉失败，不能写入生产推荐。

2026-06-24 Pelica controller layer 复测补充：

- 对 `P_actor_pelica_01 + A_actor_pelica_idle_loop` 增加了 `--unity_bake_clip_filter_mode` 诊断开关。`full/none` 会采样完整 AnimationClip，`transform_only` 会排除主 Humanoid/Muscle 曲线，只保留确定 Transform/controller layer 诊断结果，`auto/default` 保持既有安全门禁。
- `transform_only + additionalLayerClips=3` 的结果上半身比旧 fallback 稍好，但下半身/站姿明显错误；`full + additionalLayerClips=3` 下半身站住，但双手仍异常抬高，不符合 idle。说明问题不是单纯 clipFilter 选择，而是当前生成 controller 不能等价原始 RuntimeAnimatorController。
- 刷新器已收紧默认附加层恢复：只有默认层能确定为“唯一 clip”时才写入 `additionalLayerClips`。Pelica 的 layer 6 `LoopClothAddLayer.LoopClothAddIdle` 默认状态解析到 2 个 clip，需要 `IdleTailIndex` 等 BlendTree 参数或权重，不能再把所有 child clip 当作 1.0 权重叠加；刷新后主 idle 只保留 layer 7 这一条确定性附加层，并把 layer 6 写入 `additionalLayerContextWarnings`。
- 复测输出 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_DeterministicLayerFullClipBake_Current`：request `additionalLayerClips=1`、warning=1；Unity result `status=ok`、`isHumanMotion=true`、`changedTrackCount=105`；apply report 仍为 `needs_animator_controller_context`，`animationSolve.productionStatus=needs_original_animator_controller_context`，`writesReusableGltfTrsCandidate=false`。
- Blender 截图 `VisualFrames\full_mid_frame_24.png`、`upper_mid_frame_24.png`、`left_hand_mid_frame_24.png` 仍显示双手高举/前举，idle 语义失败。因此这次修复只是“防止错误叠层和误判通过”，不是动画求解完成。下一步必须恢复原始 RuntimeAnimatorController 的 state、layer、mask、BlendTree 参数、默认参数和可能的 entry transition 语义；生成的 multi-layer controller 只能作为诊断对照。
- CLI 已把原本预留的 `unityAssetPaths.animatorController` 接到 `--unity_animator_controller` 和 `unity_bake_settings.json.unityAnimatorControllers`。Unity helper 之前已有 `AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>` 分支；现在 request 能真正传入原始 controller asset path，并在 result 里区分 `provided_runtime_controller` 与 `controller_asset_missing`。
- Pelica smoke `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_ProvidedControllerMissingBake_Current` 使用显式路径 `Assets/AnimeStudioBake/ImportedAnimatorController/AC_npc_human_girl_pelica_optNew.controller`。当前 Unity bake 工程还没有这个真实 controller asset，所以 result 为 `animatorControllerSamplingMode=controller_asset_missing`、`changedTrackCount=105`。apply report 已收紧为 `status=needs_animator_controller_context`、`animationSolve.productionStatus=needs_original_animator_controller_asset`、`writesReusableGltfTrsCandidate=false`、blocked reasons=`animator_controller_asset_missing, needs_animator_controller_context`。这确认下一步阻塞是恢复/导入原始 RuntimeAnimatorController asset，而不是继续依赖 generated controller。
- 2026-06-24 再次确认用户截图中的 `Endfield_PelicaActor_IdleLoop_AcceleratedAutoFallback_Reexport_Current\BakedPreview\P_actor_pelica_01__A_actor_pelica_idle_loop.gltf` 是视觉失败样本：idle 语义下双手抬高、手腕扭曲。CLI 默认门禁已收紧：MixedHumanoidTransform 候选只要带 AnimatorController context、但没有显式原始 RuntimeAnimatorController asset，`auto/default` 就降为 `transform_only` 诊断；只有显式传入原始 controller 或人工指定 `--unity_bake_clip_filter_mode full` 才会尝试 full-body 对照。
- 2026-06-24 追加针对用户截图的 IK goal 诊断复测。旧坏样本是 `generated_single_state_controller`，fallback 请求没有打开 `probeMuscles` / `enableEditorCurveIkGoalDriver`，所以虽然 sidecar 已发现 `limbGoal=28`、`TDOF=6`，实际采样没有让手脚 IK goal 进入 Animator IK pass。CLI 已改成：`UnityBakeAccelerated` fast path 一旦因为 limb goal/TDOF 不安全而自动转入 `UnityOraclePlayableGraphFallback`，fallback request 会自动打开 unsupported Humanoid 曲线 probe；存在 hand/foot goal 曲线时再启用 editor-curve IK goal driver，并在 fallback report 写 `fallbackDiagnostics`。
- 验证输出 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_UnityOracleIkGoalDiag_Current`：Unity result 为 `animatorControllerSamplingMode=provided_runtime_controller`、`animatorControllerSamplingState=Main_Idle_IdleLoop`、`effectiveEditorCurveIkGoalDriver=true`、IK pass 应用 `756` 个 goal；Blender 截图显示双手回到身体两侧，手腕不再出现用户截图中的麻花式扭曲。apply report 仍正确保持 `status=needs_review`、`productionStatus=diagnostic_limb_ik_goal_driver`、`writesReusableGltfTrsCandidate=false`，blocked reasons 包含 `requires_clear_visual_validation` 和 `limb_ik_goal_driver_diagnostic_unverified`。结论：IK goal driver 是修正 Pelica 手腕/双手高举问题的重要证据，但它仍是诊断求解路径；没有跨样本视觉验收、controller 参数/BlendTree/additive reference pose/权重完整恢复前，不能把它升级为生产动画通过。
- 2026-06-24 又把恢复 controller 和 IK goal 诊断接回 `--generate_unity_bake_accelerated_request_from_library` 自动 fallback。代码现在会在 accelerated fallback 中发现 `ImportedAnimatorController`，并把 `--unity_animator_controller` / `--unity_bake_clip_filter_mode` 等上下文继续传给 `GenerateSelection`，避免自动入口退化成旧的 generated single-state controller。验证输出 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_AcceleratedAutoFallback_ControllerIk_Current`：fallback report 命中 `AC_npc_human_girl_pelica_optNew.controller`，request 写入 `probeMuscles=true`、`enableEditorCurveIkGoalDriver=true`，Unity result 为 `provided_runtime_controller / Main_Idle_IdleLoop / IK 189/756`，Blender 中帧和左右手近景与手动 IK 诊断一致，双手已落到身体两侧。apply report 仍为 `needs_review / diagnostic_limb_ik_goal_driver / writesReusableGltfTrsCandidate=false`，因此这只是“自动诊断链路已复现修正效果”，不是生产动画完成。
- 2026-06-24 补上自动入口前置准备：`--generate_unity_bake_accelerated_request_from_library` 在带 `--unity_file_inspect` 时会先执行 AnimatorController context refresh 和 ImportedAnimationClip recovery，避免 accelerated 路径绕过旧 batch bake 的 controller/clip 准备。`AnimatorControllerContextRefresher` 也会从小库的 `source_index_usage.json` / `animator_controller_context_refresh.json` / `sqlite_index_summary.json` 自动解析全量 `unity_source_index.db`，不再要求把 1.58 亿关系的源索引复制进每个小样本库。验证输出 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_AcceleratedAutoContextPrep_Current`：刷新器加载 71 条 controller context，更新 1 条候选；Unity result 为 `provided_runtime_controller / Main_Idle_IdleLoop / IK applied=756`，apply report 为 `needs_review`、`productionStatus=needs_animator_controller_layer_context`、`writesReusableGltfTrsCandidate=false`，因为 `LoopClothAddLayer` 仍缺确定 BlendTree/layer 语义。截图中手腕问题已消失，但报告阻断正确，下一步应恢复或求解被跳过 layer 的参数、mask、权重和 additive reference pose。
- 2026-06-24 继续收紧 Pelica 自动 fallback 的复杂层处理。`Endfield_PelicaActor_IdleLoop_SkipAmbiguousLayerBake_Current` 中，request 侧会把同一 AnimatorController layer 解析到多个 additional clip 的 layer 6 写入 `additionalLayerContextWarnings`，不再带入 `additionalLayerClips`；Unity worker 也会读取该 warning，真正跳过 recovered controller 里的 `LoopClothAddLayer`，避免复杂 BlendTree/参数层被当作确定 1.0 权重层。Unity result 只采样 `Main` 和 `LoopBodyAddLayer`，apply report 为 `productionStatus=needs_animator_controller_blend_tree_context`，blocked reasons 包含 `animator_controller_untrusted_layers_skipped` 和 `limb_ik_goal_driver_diagnostic_unverified`。清晰 upper/hand 截图显示双手落在身体两侧，手腕不再麻花，但这是“防止不可信层污染 + IK 诊断”结果；被跳过层的 BlendTree 参数、权重和 runtime 语义尚未恢复，所以仍不能作为生产 idle 通过。

2026-06-24 Endfield 新样本筛选补充：

- 为了扩展 Pelica 之外的样本，复查了 `P_actor_endminf_01`、`P_actor_endminm_01`、`P_actor_boynpc_01`、`P_actor_aglina_01`、`P_actor_camille_01` 等 source-index 候选。候选工具已把 targeted 查询从会卡住的层级递归改成快速 Animator/SkinnedRenderer 线索；`P_actor_endminf_01` exact-name 查询从超时改为约 18 秒返回，PathID 查询约 28 秒返回。
- 结果说明：很多 `P_actor_*` 的 GameObject/Animator 只是 levelseq、cutscene、skeletalmorph/gamedata 或配置线索，本身 `noVisibleMesh,noMaterial`。这类行可以帮助追 Avatar、controller 或配置关系，但不能直接作为模型 smoke，更不能作为动画 smoke。
- `P_actor_boynpc_01` 用完整源索引定向导出后，Mesh/skin/bbox/texture_links 和 glTF validator 均无 error；但 Blender 静态截图显示 ColorMask/Tint 预览材质仍明显不完整，衣服和身体呈黑白 mask 块状效果，且没有显式动画候选。2026-06-24 已把 `model_validation` 门禁收紧为检查标准 `baseColorTexture` 覆盖，重建后该样本为 `status=warning`、基础贴图覆盖 `1/14` 个 primitive，动画门禁应阻断。因此它只能算“结构模型/材质诊断样本”，不能计入生产动画验收。
- 同日继续用新门禁复查历史模型导出：`P_actor_mifu_01` Round4 为 `status=ok`、基础贴图覆盖 `45/63`；`P_actor_aglina_01` 为 `status=ok`、基础贴图覆盖 `41/41`；Pelica 为 `51/51`。三者都来自 `gamedata/skeletalmorph/skeletalmorphcfg` 路径，Blender 静态截图没有 Boynpc 那种大面积白模或黑白 mask 块，适合作为模型第一阶段正样本。
- 但 `P_actor_mifu_01` 和 `P_actor_aglina_01` 的精确 PathID 查动画关系时，主模型均为 `no_animator_component`。它们只有 `data_facemorph_avatar_* -> prefabWithRendererHelper + Avatar` 这类确定性 Avatar 桥；沿同 Avatar 找到的 postmodel Animator 也没有 `animator.controller` 关系。因此它们当前只能作为模型正样本和 Avatar 关系线索，不能作为“模型 + 动画”验收组合。
- broad `--list_source_model_candidates` 在当前 Endfield 源索引上仍会超时，源索引规模约为 `source_objects=5610204`、`source_relations=157820391`。2026-06-24 曾在主库上补出 `idx_source_relations_from(from_path_id, relation, from_file)`，但 `idx_source_relations_to` 反查索引因耗时/空间过大未完成；后续 `--list_source_model_animations` 已改为缺反查索引时跳过 sharedAvatar/MonoBehaviour 反查，避免卡死。交互式排查应继续用精确名称或 PathID；需要 shared Avatar 桥接关系时，应在源索引副本上补完整 `to_path_id` 反查索引，或使用已经产出的小样本关系报告。不能把宽表超时或 `sharedAvatarBridgeScanned=false` 当成没有可用模型/动画关系的证据。
- `P_actor_endminf_01#-8558895232557186863` 已用完整 Endfield VFS 和主源索引做模型优先定向导出，输出在 `D:\Assets\fangzhou\Endfield_EndminfActor_ModelFirst_RelationClosureSkip_Current`。这次修复了 Endfield VFS targeted inner-file 自动过滤的一个性能坑：主源索引没有 `source_relations(from_file)` 前缀索引时，不能按 `from_file=$cab` 去全扫 1.58 亿关系做 CAB 闭包；工具现在会跳过这段 relation 闭包，只使用已有索引支撑的 `source_externals` 闭包，并写 warning/profile。导出用时约 94 秒，`model_validation.status=ok`，glTF validator 无 error，材质 19、images 43、skins 56，Blender 静态截图显示不是白模、不是 Boynpc 那种大面积 mask 块；因此它可以升级为 Endfield 的模型第一阶段正样本。
- 但 `P_actor_endminf_01` 的动画关系仍来自 `data_facemorph_avatar_endminf -> SK_actor_endminf_01Avatar -> P_npc_npc_chr_0003_endminf -> AC_npc_human_girl_endminf_optNew` 的 shared Avatar bridge。这个模型可以进入下一步动画求解诊断，但在 TRS 写回、controller/clip 语义、清晰动画截帧都通过前，不能算生产动画样本。
- 2026-06-24 继续导出 `P_actor_endminf_01 + A_actor_endminf_idle_loop_additive` 小样本，输出在 `D:\Assets\fangzhou\Endfield_EndminfActor_IdleAdditive_ModelAnimationAsset_Current`。Library SQLite 现在在缺 `idx_source_relations_to` 时会复用 `--list_source_model_animations` 的 forward config shared Avatar 桥接：同 SerializedFile 内配置 MonoBehaviour PPtr 到模型和 Avatar，再用 `idx_source_relations_relation` 扫 `animator.avatar` 小集合，最终找回 `AC_npc_human_girl_endminf_optNew -> A_actor_endminf_idle_loop_additive`。重建 `library_index.db` 从之前回退全量 source graph 约 198 秒，降到约 1.9 秒，并保留 1 条 `relation_source=explicit` 候选；`relationEvidence.bridge=forwardConfigPrefabAvatarToAnimatorController`，不是名称或目录猜测。
- 同一小样本的 `UnityBakeAccelerated` 仍被阻断：sidecar 标出 `hasMuscleClip=true`、ACL scalar `floatCurveCount=143`，但当前 sidecar 没有展开成可消费的逐帧 `decoded.floatCurves`，所以报错 `Animation sidecar has no decoded float curves for UnityBakeAccelerated`。这再次确认 Endfield 的核心缺口不是筛选“能动的 transform 轨”，而是 ACL scalar / Humanoid Muscle 曲线到目标骨架 TRS 的求解。
- 用完整 Unity `AnimationClip` / `PlayableGraph` 对 `A_actor_endminf_idle_loop_additive` 做诊断采样后，旧纯 `Endfield_EndminfActor_IdleAdditive_PlayableBake_Current` 目录已清理；当前可复现证据在 `D:\Assets\fangzhou\Endfield_EndminfActor_IdleAdditive_AcceleratedAutoFallback_Run_Current`。Unity result 为 `status=ok`、`isHumanMotion=true`、`importedAvatarAssetValid=true`、`changedTrackCount=194`；apply 后 glTF validator 无 error，`writtenTracks=194`、`frameVaryingTracks=125`、`coreBodyFrameVaryingTracks=4`。清晰帧图未见拉丝、飞骨或手指炸开，但动作明显像 additive/controller 上层状态单独套在 rest 上，因此只能作为 UnityOracle 诊断样本，不能计入生产动画通过。
- 2026-06-24 进一步把这个判断接到 `--generate_unity_bake_accelerated_request_from_library`：当 fast path 遇到 `missing_decoded_float_curves`，或发现 limb goal/TDOF 等 `HumanPoseHandler` 不消费的曲线时，如果已经能精确匹配 ImportedAnimationClip，就自动生成 `UnityOraclePlayableGraphFallback` request，而不是只报错停住。验证输出在 `D:\Assets\fangzhou\Endfield_EndminfActor_IdleAdditive_AcceleratedAutoFallback_Run_Current`；fallback report 写 `fallbackReason=missing_decoded_float_curves`、`productionReady=false`，Unity result 为 `status=ok`、`isHumanMotion=true`、`changedTrackCount=194`，apply report 仍是 `needs_review`。这让批量诊断能自动走完整 AnimationClip 语义，但仍不替代 ACL scalar / Humanoid direct solver。
- 同时尝试用旧 `Endfield_PelicaActor_IdleLoop_ExportFullCurves_Current` 验证 limb/TDOF 自动 fallback 时，发现该旧库的 Animation asset `output` 仍指向不存在的 `animations/3c/A_actor_pelica_idle_loop.anim`，真实文件在 `Animations/assets/beyond/.../A_actor_pelica_idle_loop.anim`。这属于历史库索引路径问题，不能为了测试在生产逻辑里按文件名全库搜索；Pelica 的自动 fallback 需要用路径正确的新导出库，或先修复旧库 asset_catalog/library_index 路径后再复测。
- 2026-06-24 已用路径正确的新 reexport 库复测 Pelica 自动 fallback。新库 `model_validation.status=ok`，`library_index.db` 保留 1 条显式候选；fallback 精确命中 `Assets/AnimeStudioBake/ImportedAnimationClip/A_actor_pelica_idle_loop.anim`，因 limb goal/TDOF 自动转入 PlayableGraph 诊断链路。但人工复查发现 `idle_loop` 双手抬高、手腕扭曲，判为视觉失败；问题指向单 clip / generated controller 上下文不足，而不是模型本体。
- 结论：Pelica 是当前最强的人形动画失败回归样本，而不是通过样本。下一批样本必须同时满足实际导出模型材质贴图视觉正常、source-index 或 Library 中有确定性动画关系、动画来源不是 UI/Dialog/Timeline/Cutscene 专用实例，并且 rest/start/mid/end 视觉语义通过；否则宁可记录失败原因，也不能为了凑 10 个组合降低门槛。

2026-06-24 Pelica recovered controller / pose-layer 复测补充：

- 新增 `--recover_imported_animator_controllers` 诊断链路后，已能从 `unity_file_inspect.json` 和 Library 显式候选恢复 `AC_npc_human_girl_pelica_optNew.controller`，并让 bake request 进入 `animatorControllerSamplingMode=provided_runtime_controller`。这证明阻塞不再是单纯 `controller_asset_missing`。
- 第一版 recovered controller 只恢复默认层/默认状态/简单 1D BlendTree，transition、condition、runtime 参数和复杂 state machine 仍未完整恢复。恢复出的 state 会使用扁平化名字，例如 `Main.Idle.IdleLoop` 在 Unity controller asset 中实际为 `Main_Idle_IdleLoop`；采样器已按 recovered controller 命名规则播放，避免 `Animator.GotoState: State could not be found` 被误当成动画数据问题。
- 用 `--unity_probe_muscles` 复查 `A_actor_pelica_idle_loop`，结果有 `editorCurveTracks=2056`、`humanoidPoseSamples=61`，其中 `dynamicEditorCurveTrackCount=364`、`dynamicAnimatorEditorCurveTrackCount=88`，但 `maxMuscleDelta_0_mid=0`、`frameVaryingTracks=0`。继续用 `--unity_bake_rebuild_editor_curve_clip` 从 editor curves 重建临时 humanMotion clip 后，`clipRebuildSucceeded=true`、`clipRebuildCurveCount=2056`，但 HumanPose 仍不随时间变化。
- 用正确字段 `editorCurveTracks[].values` 复查三条相关 clip：`A_actor_pelica_idle_loop` 为 `2056` 条曲线、`364` 条动态；`A_actor_pelica_idle_loop_additive` 为 `2863` 条曲线、`680` 条动态；`A_actor_pelica_idle_loop_additive_cloth` 为 `2864` 条曲线、`740` 条动态。早前把 `values` 误读成不存在的 `keys` 会得到“全常量”的假结论，后续诊断脚本必须按 Unity result schema 读取。
- 结论修正：Pelica idle 当前仍是“动态 editor curves 存在，但 Unity runtime Humanoid/HumanPose 未被驱动”的失败样本，不是简单 pose-only 常量层。后续应继续追 `AnimationClip` editor curve 到 runtime `m_MuscleClip` / AnimatorController / Humanoid solver 的转换问题；报告中应标 `editor_curves_not_driving_runtime_pose`、`static_pose`、`needs_controller_runtime_context`，不能因为名字含 `idle_loop`、模型姿态变化或 glTF validator 无 error 就算动画通过。
- 2026-06-24 继续修正 recovered AnimatorController：Unity 序列化里 base layer `defaultWeight` 可能为 0，但运行时主层仍应生效，恢复器不能把第 0 层写成 0；非 base layer 如果默认状态没有可恢复 motion，也不能以权重 1 写成空 override/additive 层，否则会盖掉下层动作。修复后 Pelica controller 从 10 层降为 4 层，空的 Upper/TwoArms/LeftArm/RightArm/Head/MeshSpace 层被跳过，`Main` 层权重为 1。
- 修复空层和 base layer 权重后，`Endfield_PelicaActor_IdleLoop_RecoveredControllerSkipEmptyLayers_Current` 仍为 `static_pose`：Unity result `provided_runtime_controller`、`changedTrackCount=109`、`humanoidPoseSamples=61`，但 `maxMuscleDeltaFromFirst=0`、`frameVaryingTracks=0`、`humanoidRuntimeSamplingStatus=runtime_pose_not_driven`、`dynamicEditorCurveTrackCount=401`。这排除了“单纯 layer 权重/空层覆盖”作为最终原因，根因仍指向 recovered `.anim` 的 runtime Humanoid payload 或 AnimatorController 运行时上下文没有把动态 editor curves 驱动为逐帧 HumanPose。
- 2026-06-24 进一步确认：同一个 Unity result 里，PlayableGraph runtime `tracks` 为静止，但 `editorCurveSetHumanPoseTransformTracks` 有 `12` 条匹配 track、`12` 条逐帧变化、`11` 条核心身体变化。也就是说 Unity runtime clip 没驱动 HumanPose，不代表 editor muscle 曲线无法求解；把 `HumanTrait.MuscleName` 对应的 editor curve 值逐帧塞给 `HumanPoseHandler.SetHumanPose` 可以得到目标骨架 TRS。
- CLI 已把这条路线接成显式诊断 track source：`unityBakeTrackSource=editor_curve_set_human_pose_diagnostic`，`animationSolve.path=UnityEditorCurveHumanPoseDiagnostic`，`productionStatus=diagnostic_editor_curve_human_pose_solver`，`writesReusableGltfTrsCandidate=false`。验证输出：`D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_EditorCurveHumanPoseDiagnostic_Current\P_actor_pelica_01__A_actor_pelica_idle_loop.gltf`；报告 `writtenTracks=12`、`frameVaryingTracks=12`、`coreBodyFrameVaryingTracks=11`、glTF validator 无 error。
- 视觉结果比旧 generated-controller 明显改善：`Frames_Animated_Current\upper_mid_frame_24.png` 中双手不再举到头顶，手腕没有麻花式扭曲；但它仍像偏 A/T 的基础姿态加轻微 idle，尚未纳入 limb IK/TDOF/root/body/controller runtime 细节，不能作为生产通过样本。下一步应把 `RootT/RootQ/MotionT/MotionQ`、Hand/Foot IK goal、TDOF 与 controller layer 语义逐步接入这条 direct HumanPose 诊断线，再用同样的视觉门禁验证。
- 2026-06-24 追加 `AnimationMode.SampleAnimationClip` 诊断。Pelica 输出 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_AnimationModeSampleClip_Current` 的报告为 `unityBakeTrackSource=animation_mode_sample_clip_diagnostic`、`writtenTracks=182`、`frameVaryingTracks=78`、`coreBodyFrameVaryingTracks=17`、`productionStatus=diagnostic_animation_mode_sample_clip`、`productionReady=false`。清晰截图显示它能避免旧 full-body fallback 的双手举高，但整体仍像接近展开基准姿态，不是可信 idle。结论：Editor 采样能证明曲线可被 Unity 编辑器解释成 Transform 变化，但它绕过真实 RuntimeAnimatorController、layer、IK 和权重语义，不能当作生产动画。
- 同一输出的 `unityBakeEditorCurveCategorySummary` 显示动态曲线分布为：普通 `transformCurve=311`、`otherAnimatorCurve=55`、`humanoidLimbGoal=28`、`humanoidRootBody=7`、`humanoidTdof=0`。进一步对比三条采样源：runtime PlayableGraph 只有极小幅辅助轨变化；`editor_curve_set_human_pose_diagnostic` 主要驱动手、前臂、上臂和少量躯干；`AnimationMode.SampleAnimationClip` 额外驱动大量手指以及 `IK_Hand/IK_Weapon` Transform/goal 节点。Pelica idle 不能靠“只消费 muscle/root”或“直接相信 AnimationMode Transform”通过，下一步必须恢复原始 controller layer/IK/权重语义，或在直接求解器里明确合成 Humanoid muscle、limb goal 和普通 Transform 曲线。
- 2026-06-24 继续给 recovered controller 采样增加 layer 诊断。输出 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_RecoveredControllerLayerDiagnostics2_Current` 显示 recovered controller 有 4 层：`Main`、`ClothCombine`、`LoopClothAddLayer`、`LoopBodyAddLayer`。其中 `LoopBodyAddLayer` 是 additive，命中 request 中的 human mask 和 112 条 skeleton mask entry，但当前 provided-controller 采样路径没有把这些 mask 应用到 controller asset，报告已收紧为 `productionStatus=needs_animator_controller_layer_masks`、blocked reason=`animator_controller_layer_masks_not_applied`。视觉上双手重新举高，说明未 masked additive layer 会污染身体主姿态；下一步应恢复 layer mask 语义，或改用能应用 skeleton/human mask 的 layer mixer / direct solver。
- 2026-06-24 继续把 recovered controller 的确定附加层改为 `AnimationLayerMixerPlayable` 采样，输出 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_RecoveredControllerMaskedLayerMixer_Current`。Unity result 显示 `animatorControllerSamplingMode=provided_runtime_controller`，`LoopBodyAddLayer.maskApplied=true`，并成功应用 `1/1` 个 skeleton mask、解析 `112/112` 条 masked transform path。这说明“mask 没有应用”这个阻塞已被移除，但 Blender 上半身和左右手近景仍显示双手抬高，不符合 `idle_loop` 语义。结论：Pelica 当前问题不再能归因于未应用 skeleton mask，下一步要追 base/body clip 解释、recovered controller 参数、BlendTree、IK goal/weight 和手部目标空间。apply report 已收紧为 `productionStatus=needs_visual_validation`、`writesReusableGltfTrsCandidate=false`、blocked reason=`requires_clear_visual_validation`；只要清晰截图肉眼失败，就不能因为 glTF TRS、核心骨骼变化或 layer mask 应用成功而进入可复用动画。
- 2026-06-24 追加 controller fidelity 诊断，输出 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_ControllerFidelityDiagnostics_Current`。Unity result 现在写出 `animatorControllerParameterDiagnostics` 和 `animatorControllerBlendTreeDiagnostics`：Pelica recovered controller 有 9 个参数，当前全部为默认 `0`；`LoopClothAddLayer` 的 Simple1D BlendTree 使用 `IdleTailIndex=0`，默认会选 `A_actor_pelica_idle_loop_additive_cloth`。但本次为了应用 skeleton mask 使用 `AnimationLayerMixerPlayable`，采样只组合 `Main` base clip 和 `LoopBodyAddLayer`，因此 `animatorControllerLayerMixerBypassesControllerBlendTrees=true`，apply report 降为 `productionStatus=needs_animator_controller_blend_tree_context`，blocked reasons 包含 `animator_controller_blend_tree_parameters_bypassed_by_layer_mixer`。清晰截图仍失败：双手高举，idle 语义不成立。下一步不能只继续调 mask，而应恢复或等价求解 controller 参数、BlendTree、additive reference/base pose、IK goal/weight 与 runtime layer 权重。
- 2026-06-24 继续让 recovered-controller layer mixer 从 recovered AnimatorController asset 的默认层读取 motion，并把 Simple1D BlendTree 按 recovered 参数默认值扁平成选中的 child。输出 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_RecoveredControllerBlendTreeDefaultMixer_Current` 显示 mixer 现在有 4 层：`Main -> A_actor_pelica_idle_loop`、`ClothCombine -> A_actor_pelica_idle_loop_additive`、`LoopClothAddLayer -> A_actor_pelica_idle_loop_additive_cloth`、`LoopBodyAddLayer -> A_actor_pelica_idle_loop_additive`；其中 `LoopBodyAddLayer` 的 112 条 skeleton mask 已应用，`LoopClothAddLayer` 的 `IdleTailIndex=0` BlendTree child 也已进入 mixer，所以“BlendTree 默认 child 未进入 mixer”被排除。但视觉仍失败，双手依旧抬高。
- 同日追加 unmasked override layer 门禁，输出 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_UnmaskedOverrideLayerDiagnostics_Current`。报告明确 `animatorControllerUnmaskedOverrideLayerNames=["ClothCombine"]`，该层是非 base、非 additive、权重 1、无 request mask 上下文的 override 层，却被扁平进 layer mixer；apply report 因此降为 `productionStatus=needs_animator_controller_layer_masks`，blocked reasons 包含 `animator_controller_unmasked_override_layers`。当前更具体的阻塞是 recovered AnimatorController asset/request 没有恢复所有 layer AvatarMask / skeleton mask / runtime layer 权重，导致 `ClothCombine` 这类层可能全身覆盖身体姿态。下一步应把 `unity_file_inspect.json` 中每个 layer 的 `bodyMask` / `skeletonMask` 带入 AnimatorController recovery request/result，并在 layer mixer 中按 layer 自身 mask 应用；没有 mask 或权重上下文的 override 层不能进入生产验收。
- 2026-06-24 复测 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_SkipUntrustedLayers_Current` 后确认，当前 recovered controller metadata 已能把 mixer 收敛到 3 层：`Main`、`LoopClothAddLayer`、`LoopBodyAddLayer`，并应用 `2/2` 个 skeleton mask、解析 `264/264` 条 masked transform path；`ClothCombine` 不再进入采样计划，`animatorControllerUnmaskedOverrideLayerCount=0`。这排除了“未遮罩 ClothCombine 覆盖身体”作为当前主要原因。但 Blender `upper_mid_frame_24.png` / `full_mid_frame_24.png` 仍显示 idle 双手抬到头部附近，报告仍为 `productionStatus=needs_limb_ik_goal_solver`、blocked reasons=`requires_clear_visual_validation,dynamic_limb_goal_curves_not_solved`。结论：mask 和复杂层污染问题已收窄，当前阻塞转向 limb IK goal 的目标空间、权重、逐帧触发和 controller IK 语义。
- 同日复测 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_SkipUntrustedLayers_IkGoal_Current`：显式 `ANIMESTUDIO_UNITY_BAKE_ENABLE_IK_GOAL_DRIVER=1` 后，layer mixer 路径里 `OnAnimatorIK` 只得到 `callCount=6`、`appliedGoalCount=24`，远低于单状态诊断的 `63/252`；截图与无 IK 版几乎一致，仍不符合 idle。说明 `AnimationLayerMixerPlayable + IK pass` 当前没有稳定按每个采样帧执行 limb goal 诊断，不能把这条路径当成 IK 已求解。下一步应先修正 layer mixer 下的逐帧 IK 采样，再继续判断 goal T/Q 的空间和权重解释。
- 2026-06-24 修正 `SamplePlayableGraph`：当 `AnimationLayerMixerPlayable` 路径显式开启 IK goal 诊断时，采样各层时间后改为 `graph.Evaluate(0.0001f)` 并直接返回，避免 `AnimationMode.SamplePlayableGraph` 覆盖 `OnAnimatorIK` 写入的姿态。复测 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_LayerMixerIkEvaluate_Current` 后，IK 诊断提升为 `callCount=189`、`appliedGoalCount=756`，对应 63 个采样点 × 3 层 × 4 个 goal；`upper_mid_frame_24.png` / `full_mid_frame_24.png` 显示双手已落到身体两侧，手腕不再麻花，明显比旧失败样本接近 idle。apply report 仍保持 `writesReusableGltfTrsCandidate=false`，但 `productionStatus` 从 `needs_limb_ik_goal_solver` 改成 `diagnostic_limb_ik_goal_driver`，blocked reasons 为 `requires_clear_visual_validation,limb_ik_goal_driver_diagnostic_unverified`。这说明 limb goal 诊断求解链路已经有效，但目标空间、权重语义和更多动作/角色 smoke 还没完成，不能升级生产。随后把开关沉淀为 CLI/request 字段：`--unity_bake_enable_ik_goal_driver` 会写入 `enableEditorCurveIkGoalDriver`，Unity result 回写 `requestEnableEditorCurveIkGoalDriver` / `effectiveEditorCurveIkGoalDriver`。`D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_RequestFlagSmoke_Current` 已验证不依赖环境变量也能复现同样的 189/756 诊断统计。
- 2026-06-24 继续用 `P_actor_endminf_01 + A_actor_endminf_idle_loop_additive` 做第二人形样本。默认安全门禁输出 `D:\Assets\fangzhou\Endfield_EndminfActor_IdleAdditive_IkRequestFlagSmoke_Current`，因为降成 `transform_only`，Unity result 为 `isHumanMotion=false`，IK driver 有 `callCount=63` 但 `appliedGoalCount=0`，apply report 正确阻断为 `needs_original_animator_controller_context`。显式 full clip 对照 `D:\Assets\fangzhou\Endfield_EndminfActor_IdleAdditive_IkFullClipSmoke_Current` 为 `isHumanMotion=true`、`appliedGoalCount=252`、`coreBodyFrameVaryingTracks=5`，glTF validator 无 error；但清晰截帧显示双臂展开，更像 additive/controller 上层姿态单独套在 rest 上，不是完整 idle。这个样本补充证明：IK goal 诊断并非只在 Pelica 生效，但单个 additive clip 即使视觉不炸，也必须保持诊断状态，不能升级生产；下一步仍是恢复原始 RuntimeAnimatorController 上下文或直接 solver 等价处理 layer/blend/weight/IK 语义。
- 2026-06-24 又用完整 Endfield VFS inspect 恢复并导入 `AC_npc_human_girl_endminf_optNew.controller`，复测输出 `D:\Assets\fangzhou\Endfield_EndminfActor_IdleAdditive_ProvidedControllerIk_Current`。这次 Unity result 进入 `provided_runtime_controller`，并触发 `ikGoalDriverCallCount=126`、`ikGoalDriverAppliedGoalCount=504`；说明 controller asset 已被使用，IK 诊断链路也生效。但 recovery report 同时显示只找到 1 个可加载 clip、缺 31 个 clip，多个复杂 BlendTree 或空 motion 层被跳过。清晰 full/upper 截帧仍显示双臂外展且首/中/末帧核心变化不足，apply report 保持 `productionStatus=diagnostic_limb_ik_goal_driver`、`writesReusableGltfTrsCandidate=false`。结论：Endminf 把问题从“缺 controller asset”推进到“controller runtime 语义恢复不完整”；后续要补全 clip 恢复、BlendTree 参数、层权重、AvatarMask 和 IK goal 空间/权重，而不是把 provided controller 本身当作通过条件。
- 2026-06-24 继续修正 Endminf 自动 fallback：`missing_decoded_float_curves` 分支现在和 limb/TDOF 分支一样会使用候选 `relationEvidence` 命中 `ImportedAnimatorController`，不再退回 generated single-state。输出 `D:\Assets\fangzhou\Endfield_EndminfActor_IdleAdditive_ControllerLookupFix_Current` 的 fallback report 记录 `unityAnimatorController=Assets/AnimeStudioBake/ImportedAnimatorController/AC_npc_human_girl_endminf_optNew.controller`；Unity result 为 `provided_runtime_controller`，layer 诊断正确映射回原始 `sourceLayerIndex=0/6/7`，并应用 `223/95` 条 skeleton mask。glTF validator 无 error，apply report 收紧为 `productionStatus=needs_animator_controller_base_layer_context`，blocked reasons=`requires_clear_visual_validation, selected_controller_layer_clip_missing_base_layer_context`。Blender 清晰中帧仍显示双臂抬高，不符合可复用 idle 语义。结论：这是 controller/metadata 恢复精度推进，不是动画通过；选中的非 base layer clip 没有同状态 `baseLayerClip` 证据时，必须继续阻断生产。
- 2026-06-24 继续补强 `missing_decoded_float_curves` 的 UnityOracle 诊断：这类 fallback 之前因为 fast path 没有 decoded float curves，无法提前知道 limb/root/TDOF 分布，所以不会打开 `probeMuscles`。现在 missing decoded 也会默认 probe editor curves，但不自动启用 IK driver；IK 仍只在 fast path 已明确发现 limb goal 时自动打开。验证输出 `D:\Assets\fangzhou\Endfield_EndminfActor_IdleAdditive_MissingDecodedProbe_Current`：fallback report 写 `probeUnsupportedHumanoidCurves=true`、`enableEditorCurveIkGoalDriver=false`；Unity probe 确认 `humanoidLimbGoal.dynamicCount=28`、`humanoidRootBody.dynamicCount=3`、`humanoidTdof.dynamicCount=0`。apply report 继续阻断为 `needs_animator_controller_base_layer_context`，blocked reasons 增加 `dynamic_limb_goal_curves_not_solved`；清晰 upper 中帧仍是双臂高举/外展。结论：missing decoded 不能只报“缺曲线”，必须补齐 editor curve 分类证据；但 probe 只增强诊断，不改变生产失败结论。
- 2026-06-24 重新导出 Endminf 小样本并强制 `--export_full_decoded_animation_curves`，输出 `D:\Assets\fangzhou\Endfield_EndminfActor_IdleAdditive_ReexportFullCurves_Current`。这次 `A_actor_endminf_idle_loop_additive.animation_asset.json` 从旧库的 `decoded.status=skipped` 变成 `ok`，解出 `translations=356`、`rotations=356`、`scales=356`、`floats=143`，ACL manifest 为 `outputTrackCount=356`、`rootTrackCount=28`、`floatCurveCount=143`。这证明旧 `missing_decoded_float_curves` 样本主要是导出时没有写完整 decoded sidecar，不能继续拿它判断 Endfield 解码器能力。随后自动入口输出 `D:\Assets\fangzhou\Endfield_EndminfActor_IdleAdditive_ReexportFullCurves_AutoSolve_Current`，fallback reason 正确变成 `unsupported_limb_goal_or_tdof_curves`，request 使用原始 `ImportedAnimationClip`、`ImportedAnimatorController` 和 `ImportedAvatar`，并打开 `probeMuscles=true`、`enableEditorCurveIkGoalDriver=true`。Unity result 为 `provided_runtime_controller / ClothCombine_SequenceNode`、IK goal `189/756`、`changedTrackCount=21`；apply report 仍为 `needs_animator_controller_base_layer_context`，blocked reasons 为 `requires_clear_visual_validation, selected_controller_layer_clip_missing_base_layer_context, limb_ik_goal_driver_diagnostic_unverified`。清晰截图显示手腕不再麻花、双臂不再高举到头顶，但全身仍是双臂外展的 additive/controller 片段感，不是完整可复用 idle。结论：后续 Endminf 诊断必须先使用 full decoded sidecar；真正阻塞是 base layer / BlendTree / layer 权重 / IK goal 语义，而不是“缺 decoded 曲线”。
- 复查原神旧 Browser 双击 bake 链路后，确认可复用经验不是“继续依赖用户维护 Unity 工程”，而是它已经把 Humanoid bake 拆成了 Avatar oracle、ImportedAnimationClip、AnimatorController context、bake cache 终态和报告门禁。旧 `AnimationClipAssetRecoveryExporter` 也已有规则：AnimatorController 辅助 clip 应切到确定的 `baseLayerClip`，不能把非 base layer / additive / slot 片段当完整身体动作。Endfield 当前问题正好落在这条经验上。
- 2026-06-24 据此补强 `AnimatorControllerContextRefresher` 与 `SQLiteSourceIndexBuilder`：如果选中 clip 来自非 base layer，且同一个 `AnimatorController` 的 base layer 默认状态能从 `unity_file_inspect.json` 或源索引解析成唯一确定 clip，则写入 `animatorControllerContext.baseLayerClip`，来源标为 `animator_controller_inspect.base_layer_default_state` 或 `AnimatorController.baseLayer.defaultState`。这不是名称匹配，也不是动作语义猜测；如果 base layer 默认状态是多 clip、复杂 BlendTree 或参数不确定，就继续不写，并保持原来的阻断。
- 用 `D:\Assets\fangzhou\Endfield_EndminfActor_IdleAdditive_ReexportFullCurves_Current` 刷新 context 后，`AC_npc_human_girl_endminf_optNew` 命中 71 条 controller context，目标候选更新 1 条；`A_actor_endminf_idle_loop_additive` 的 layer 7 候选补到 base layer default clip PathID `-1793818535242772456`。验证输出 `D:\Assets\fangzhou\Endfield_EndminfActor_IdleAdditive_BaseLayerClipContext_Run_Current` 不再触发 `selected_controller_layer_clip_missing_base_layer_context`，apply report 改为 `productionStatus=diagnostic_limb_ik_goal_driver`，blocked reasons 剩 `requires_clear_visual_validation` 与 `limb_ik_goal_driver_diagnostic_unverified`。清晰中帧显示手腕没有麻花，整体比旧 additive 片段更接近可读站姿，但仍不能作为生产通过：当前仍依赖诊断 IK goal driver，且选中 clip 是 controller layer/additive 组合的一部分，必须继续恢复/验证 controller 参数、BlendTree、layer 权重和 IK goal 空间。
- 2026-06-24 继续把上述规则接入定向 Library 导出：`Library + --path_ids + --unity_file_inspect + --source_index` 会在导出前自动把 AnimatorController 上下文里的确定 `baseLayerClip` PathID 加入 `--path_ids`，并写 `path_id_dependency_expansion.json`。导出后会自动刷新小库的 AnimatorController context。验证输出 `D:\Assets\fangzhou\Endfield_EndminfActor_IdleAdditive_AutoBaseClipAndContext_Reexport_Current` 只手动传入模型 PathID 和 additive clip PathID，工具自动补入 `A_actor_girl_idle_loop` PathID `-1793818535242772456`，最终库包含 2 个 AnimationClip；随后 `--generate_unity_bake_accelerated_request_from_library` 直接选择 `Assets/AnimeStudioBake/ImportedAnimationClip/A_actor_girl_idle_loop.anim`。这一步只补导确定依赖资产和上下文，不新增模型-动画推荐关系；生产结论仍由 glTF TRS、主体骨骼覆盖和清晰视觉验收决定。
- 2026-06-24 复查 Pelica / Endminf 的 editor curve 名称后确认，当前样本只有 `Left/Right Hand/Foot T/Q`，没有独立 IK 权重曲线；`*.Q.w` 是四元数分量，不是权重。因此 `AnimeStudioEditorCurveIkGoalDriver` 已从固定权重 1 改为读取当前 Animator layer weight，并在 `editorCurveIkGoalDriverDiagnostic.weightRule`、sample 的 `layerWeight/positionWeight/rotationWeight` 中记录。Pelica 复测 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_IkLayerWeightDiag_Current` 中 3 个参与层权重仍全为 1，视觉结论不会因此升级；它只是把 IK 诊断从“隐式满权重”推进到“层权重可追溯”。
- 2026-06-24 继续修正 Pelica 报告门禁：`ControllerLayerMixerBypassesBlendTreeSelection` 现在会排除已经写入 `SkippedUntrustedLayerNames` 的 layer，避免把同一个 `LoopClothAddLayer` 同时报成“已跳过不可信层”和“BlendTree 默认 child 未进入 mixer”。复测 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_BlendTreeSkipReportFix_Current` 后，Unity result 为 `animatorControllerLayerMixerBypassesControllerBlendTrees=false`、`animatorControllerSkippedUntrustedLayerCount=1`、`SkippedUntrustedLayerNames=["LoopClothAddLayer"]`，apply report 仍保持 `status=needs_review`、`productionStatus=needs_animator_controller_layer_context`、`writesReusableGltfTrsCandidate=false`，阻断原因收敛为 `requires_clear_visual_validation`、`animator_controller_untrusted_layers_skipped` 和 `limb_ik_goal_driver_diagnostic_unverified`。结论：这不是动画修复通过，而是把旧 Genshin bake 经验里的“关系证据和门禁要诚实”落实到 Endfield 诊断，后续真正要补的是被跳过 layer 的 deterministic runtime 语义、IK goal 空间/权重和跨样本视觉验收。
- 2026-06-24 继续给 skipped layer 增加结构化诊断，避免 `skippedCount=1` 这种粗字段挡住下一步分析。Unity result / apply report 现在写 `animatorControllerSkippedUntrustedLayerDiagnostics` / `unityBakeAnimatorControllerSkippedUntrustedLayerDiagnostics`。复测输出 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_SkippedLayerDiag_Current` 显示 `LoopClothAddLayer` 的实际状态是：`sourceLayerIndex=6`、`isAdditive=true`、`defaultWeight=1`、`hasLayerMetadata=true`、`hasRequestLayerContext=false`、`hasSkeletonMask=true`、`skeletonMaskEntryCount=152`、Simple1D `blendParameter=IdleTailIndex` 默认 `0` 会选 `A_actor_pelica_idle_loop_additive_cloth`。这说明该层不是完全缺信息，而是缺“request/runtime 层上下文”来证明默认参数、权重和 mask 语义可信。当前仍保持 `productionStatus=needs_animator_controller_layer_context`，但下一步可以围绕“把 recoverable Simple1D skipped layer 升级为显式诊断采样，并继续阻断生产”做对照，而不是盲目猜测。
- 同次复测 `gltf-transform validate` 无 error，仍有 tangent/unused texture/NPOT warning。Blender 截图已生成到 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_SkippedLayerDiag_Current\VisualFrames`；`upper_mid_frame_24.png` 和左右手中帧不再显示最初用户截图中的双手举过头顶/手腕麻花，但渲染有明显过曝和材质预览问题，且报告仍有 `requires_clear_visual_validation` 与 `limb_ik_goal_driver_diagnostic_unverified`。因此它只能作为“诊断改善证据”，不能计入 10 样本生产 smoke。
- 2026-06-24 继续把上面的判断做成显式诊断采样开关：`--unity_bake_sample_recoverable_skipped_layers_diagnostic`。它只会采样“有可解析 motion、有 recovered layer metadata、有 skeleton mask”的 skipped layer，并在 result/apply report 写 `animatorControllerDiagnosticSampledSkippedLayerCount`、`animatorControllerDiagnosticSampledSkippedLayerNames` 与每层 `diagnosticSampledSkippedLayer`。验证输出 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_RecoverableSkippedLayerSample_Current` 中，`LoopClothAddLayer` 被采样，`skippedCount=0`、`diagnosticSampledCount=1`、`ikCalls=189`、`ikApplied=756`，glTF validator 无 error；清晰 full/upper/hand 中帧显示双手落在身体两侧，未复现用户截图中的手腕麻花。apply report 仍强制 `status=needs_review`、`productionStatus=diagnostic_recoverable_skipped_layer_sampling`、`writesReusableGltfTrsCandidate=false`，blocked reasons 包含 `animator_controller_recoverable_skipped_layers_sampled_diagnostic` 和 `limb_ik_goal_driver_diagnostic_unverified`。结论：这个开关证明 layer 6 具备“可恢复价值”，但只用于对照和定位，不能把诊断采样结果升级为生产 idle 动画。
- 2026-06-24 批量入口也接入同一诊断能力。`--bake_animation_previews_from_library` 现在会把 `--unity_bake_sample_recoverable_skipped_layers_diagnostic` 传进 request，并在 `unity_bake_batch_report.json.items[*]` 提升 `sampleRecoverableSkippedLayersDiagnostic`、`animatorControllerDiagnosticSampledSkippedLayerCount`、`animatorControllerDiagnosticSampledSkippedLayerNames`，便于后续 10 样本 smoke 直接筛出这类诊断输出。复测输出 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_BatchRecoverableSkippedLayerSample_Current` 显示批量 item 为 `needs_review`，`productionStatus=diagnostic_recoverable_skipped_layer_sampling`，`LoopClothAddLayer` 采样数为 1，`writesReusableGltfTrsCandidate=false`。
- 同次复测发现一个批量入口坑：只写 `enableEditorCurveIkGoalDriver=true` 但 `probeMuscles=false` 时，Unity worker 没有 editorCurveTracks，结果 `effectiveEditorCurveIkGoalDriver=false`、`ikCalls=0`，视觉会回到双手抬高失败状态。CLI request 生成已改为“显式启用 IK goal driver 时自动打开 `probeMuscles`”，重跑后 request 为 `probeMuscles=true`、`enableEditorCurveIkGoalDriver=true`，Unity result 为 `effectiveEditorCurveIkGoalDriver=true`、`ikCalls=189`、`ikApplied=756`，批量报告也同步提升这两个 IK 计数字段。清晰中帧显示双手回到身体两侧。这个修复只保证诊断开关真正生效，仍不改变生产门禁：只要依赖 IK 诊断和 recoverable skipped layer 采样，就必须保持 `needs_review`。
- 2026-06-24 继续修正批量入口的 Avatar oracle 匹配。旧逻辑主要从模型 raw_json 找 ImportedAvatar key，Pelica 这种 sharedAvatarController 候选的可信 Avatar 名在候选 `relationEvidence.avatarName` / `animatorControllerContext.avatarName` 里，导致不手动传 `--unity_avatar_asset` 时容易被跳过。现在 `--bake_animation_previews_from_library` 会用显式候选里的 Avatar 名匹配 fresh probe 通过的 `Assets/AnimeStudioBake/ImportedAvatar/*.asset`，但不使用模型名、角色名或目录猜测。验证输出 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_BatchAutoAvatar_Current` 未传 `--unity_avatar_asset`，batch report 写 `avatarSource=unityAssetPaths.avatarAsset`、`avatarMatchKey=SK_actor_pelica_01Avatar`、`animationClipMatchKey=A_actor_pelica_idle_loop`；apply report 仍为 `productionStatus=diagnostic_recoverable_skipped_layer_sampling`、`writesReusableGltfTrsCandidate=false`。Blender 清晰 full/upper/hand 中帧显示双手在身体两侧，未复现用户截图中的双手高举/手腕麻花；但由于仍依赖 IK 诊断和 recoverable skipped layer 采样，它只能作为诊断改善证据，不能计入生产 smoke。
- 2026-06-24 追加 `animatorControllerRecoverableSkippedLayerSummary`，让 apply report 和 batch report 顶层都能直接看出 skipped layer 的可恢复程度。Pelica 复测 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_BatchAutoAvatarSummary_Current` 显示 `recoverableSkippedLayerCount=1`、`simple1DDefaultSelectedLayerCount=1`、`blendParameterNames=["IdleTailIndex"]`，对应 `LoopClothAddLayer` 默认值 `0` 选中 `A_actor_pelica_idle_loop_additive_cloth`。这说明问题已从“完全缺 layer”收敛为“recovered controller 默认参数/权重/mask 与 IK goal 诊断如何生产化”，但报告仍必须保持 `diagnostic_recoverable_skipped_layer_sampling` 和 `writesReusableGltfTrsCandidate=false`，不能把默认 child 可解析当作生产通过。
- 同次复查原神旧 Browser bake 链路后，保留的经验是“强证据和缓存门禁”，不是继续依赖用户手动维护游戏专用 Unity 工程。当前 Endfield 报告已追加 `defaultParameterSource`、`selectedChildSelectionRule`、`fallbackZeroBlendParameterLayerCount` 和 `defaultParameterSources`：`stateParameter` 表示默认值来自原始 inspect 的 state 参数；`blendEventFallbackZero` 或 `missingParameterFallbackZero` 表示只知道 BlendTree 参数名，真实运行时参数仍未知。后者只能作为诊断采样线索，不能因为 Simple1D 选中了某个 child 就升级生产通过。
- 2026-06-24 继续追 Pelica `IdleTailIndex` 的确定性来源：旧 `unity_file_inspect.json` 没有暴露 `AnimatorController.m_Values/m_DefaultValues`，导致 recovery 只能把 BlendTree 参数记成 `blendEventFallbackZero`。现在 inspect 会写 `controllerValueDefaults`，源索引 relation 只写轻量 count 摘要避免大库膨胀；直读 `StreamingAssets/VFS/7064D8E2/7064D8E2.blc` 中 `Data/Bundles/Windows/main/0a280e65d9d8c1f16f74fdc0.ab` 验证，`AC_npc_human_girl_pelica_optNew` 有 `valueCount=106/defaultFloatCount=91`，其中 `IdleTailIndex` 为 `type=1/index=59/defaultCandidates.float=0.0`。`AnimatorControllerAssetRecoveryExporter` 已把这类 float 默认值写入 recovery request，Unity recovery 端记录来源 `controllerDefaultValue`；若 state 里有 `stateParameters`，仍由 `stateParameter` 覆盖 controller 默认值。这个修复只把“参数默认值来源”从 fallback 变成原始 controller 证据，不解决运行时参数变化、IK goal 空间/权重、additive reference pose 或视觉验收，因此 Pelica 仍不能计入生产 smoke。
- 2026-06-25 继续追 Pelica provided controller 的 `missingClipCount`：`Endfield_PelicaActor_IdleLoop_ReexportForFallback_Current` 只有 1 个 `assets.kind=Animation`，所以 controller recovery request 虽然能从 `unity_file_inspect + unity_source_index` 得到 119 个 clip 槽位名称和 PathID，却只能匹配到 `A_actor_pelica_idle_loop.anim`，其余 clip 不是源数据缺失，而是没有随定向库导出。新增显式诊断开关 `--include_animator_controller_clip_closure` 后，用 `A_actor_pelica_idle_loop pathId=3251858251387051210` 可通过源索引 `animatorController.clip` 闭包扩展 215 个 PathID，并在 `D:\Assets\fangzhou\Endfield_PelicaControllerClipClosureExport_Current` 导出 216 个 `.anim`。这一步只补 controller clip 依赖闭包，不新增模型-动画绑定，也不证明 runtime layer/BlendTree/IK 语义已正确；后续需要把这些 clip 导入/映射到 UnityBakeAccelerated 或直接 solver，再继续看 `missingClipCount`、IK goal/TDOF 消费和视觉截图。
- 2026-06-25 同轮继续把闭包接入 recovery：新增 `--animator_controller_clip_library` 后，controller recovery 能读取额外动画闭包库的 `assets.kind=Animation` 与 `unityAnimationClips` 映射。把 `Endfield_PelicaControllerClipClosureExport_Current` 的 216 个 `.anim` 复制进 `D:\misutime\AnimeStudioUnityBakeWorker\Assets\AnimeStudioBake\ImportedAnimationClip` 后，再对原 Pelica 模型库生成 request，`AC_npc_human_girl_pelica_optNew` 摘要为 `clipCount=119/libraryAssetAvailableCount=119/missingLibraryAssetCount=0/missingImportedClipCount=0`。这说明“provided controller 缺 clip”已从数据闭包层解决；剩余阻塞转为实际生成/采样 recovered controller，以及 runtime layer/BlendTree/IK goal/TDOF/Root/Body 曲线是否能生成视觉正确的 glTF TRS。
- 2026-06-25 复盘原神旧 Browser 双击 bake 后，把可复用经验落到普通 request 生成：旧流程值得借的是显式关系、fresh Avatar probe、ImportedAnimationClip/AnimatorController 精确映射、终态缓存和报告门禁；不值得借的是用户维护游戏专用 Unity 工程并把临时 bake 当生产成功。现在普通 `--generate_unity_bake_request_from_library` 会预读动画 sidecar，发现动态 `Left/Right Hand/Foot T/Q` 且存在原始 controller 时自动打开 IK goal 诊断，并把 `unsupportedHumanPoseCurveSummary` 写进 request。Pelica 复测 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_AutoIkRequestSmoke_Run_Current` 中 request 为 `probeMuscles=true`、`enableEditorCurveIkGoalDriver=true`、`autoEnabledIkGoalDriver=true`，Unity result 为 `effectiveEditorCurveIkGoalDriver=true`、`animatorControllerSamplingMode=provided_runtime_controller`，apply report 为 `needs_review`、`usedIkGoalDriverDiagnostic=true`、`writesReusableGltfTrsCandidate=false`。清晰 full/upper/hand 中帧显示双手落在身体两侧，未复现用户截图里的双手高举和手腕麻花；但它仍依赖 IK 诊断和 skipped layer 门禁，只能作为 oracle 改善证据，不能计入生产 smoke。
- 2026-06-25 继续把 Pelica 的 skipped layer 诊断来源收紧：重新恢复 `AC_npc_human_girl_pelica_optNew.controller` 后，recovery request 有 `91` 个 controller 默认参数、`119/119` 个 clip 可用且无 missing imported clip，`IdleTailIndex` 在 metadata 中来自 `controllerDefaultValue`。普通 request 入口也已接通 `--unity_bake_sample_recoverable_skipped_layers_diagnostic`，复测 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_ControllerDefaultSampledLayer_Rerun_Current` 显示 `LoopClothAddLayer` 被诊断采样，`fallbackZeroBlendParameterLayerCount=0`、`defaultParameterSources=["controllerDefaultValue"]`，glTF validator 无 error，清晰中帧仍未复现手腕麻花。报告仍为 `productionStatus=diagnostic_recoverable_skipped_layer_sampling`、`writesReusableGltfTrsCandidate=false`，阻断原因保留 `animator_controller_recoverable_skipped_layers_sampled_diagnostic` 与 `limb_ik_goal_driver_diagnostic_unverified`。结论：参数默认值来源已经从 fallback 修到原始 controller 证据；剩余生产阻塞是 runtime 参数变化、additive reference pose、IK goal 空间/权重和跨样本验收，而不是 `IdleTailIndex` 默认值缺失。
- 2026-06-25 追加原神旧 bake 经验到 Pelica run 的 IK hint 排查：旧链路真正值得复用的是“显式 Avatar/Clip/Controller asset + Unity fresh probe + 报告门禁”，不是继续堆 bake 步骤。因此新增 geometry hint 诊断只用于排除原因：`AnimeStudioEditorCurveIkGoalDriver` 在写 Hand/Foot goal 时同步把当前帧 elbow/knee 写入 `Animator.SetIKHintPosition`，并把 hint readback 写进报告。复测 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_GeometryHintDiag_Current` 显示 Unity 接收了 hint（`missingIkHintReadbackCount=0`、`maxIkHintReadbackDistanceToHint=0`、`maxIkHintReadbackWeight=1`），但最终 hand/foot 残差仍约 `0.145m` / `0.191m`，`ikGoalDriverVerification.status=goal_alignment_needs_review`，视觉帧中手部仍不够自然。结论：缺 elbow/knee hint 不是当前主因；下一步应继续追 IK goal 目标点语义、Humanoid hand/foot endpoint 定义、TDOF/root/body 曲线、controller runtime 参数和 additive/layer 语义，不能把 hint readback 成功当成动画通过。
- 2026-06-25 继续复用旧 Browser / 原神 bake 的“主层 clip 精确选择”经验：Pelica `A_actor_pelica_run_loop` 请求明确加载了 `ImportedAnimationClip/A_actor_pelica_run_loop.anim`，但恢复出的 `ImportedAnimatorController` 默认状态 motion 曾是 `A_actor_pelica_idle_loop`，导致 run 请求实际采样 idle 主层。`AnimeStudioPlayableBaker` 现已在 recovered controller 的 base layer 中比较 `requestedClipName` 与 `controllerMotionName`；冲突时以显式请求 clip 为准，并在 `animatorControllerLayerDiagnostics` 写出 `requestedClipOverridesControllerMotion=true` 与 `motionSelectionRule=base_layer_request_clip_overrides_recovered_controller_motion`。复测 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_BaseClipOverrideDiag_Current` 后主层 motion 改为 `A_actor_pelica_run_loop`，截图不再复现双手高举和手腕麻花；但 apply report 仍保持 `needs_review` / `diagnostic_limb_ik_goal_driver`，因为脚部 IK reach shortfall 约 `0.169m` 且仍依赖 IK goal 诊断。结论：旧链路可取的是主层 motion 冲突门禁和报告证据，不是把恢复 controller 默认 motion 当绝对真相。
- 同轮继续加了 `directTwoBoneIkDiagnostic`：在 Unity/PlayableGraph 采样后、写 glTF TRS 前，用 Avatar 的上臂/前臂/手、腿/小腿/脚做二骨 CCD，直接追同一帧 `Left/Right Hand/Foot T` 目标。复测 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_DirectTwoBoneIkDiag_Current` 显示二骨求解确实运行（`directTwoBoneIkAttemptCount=88`、`directTwoBoneIkSolveCount=88`、`missingChain=0`），但最大改善只有约 `9.7e-7m`，手脚最大残差仍约 `0.191m`；apply report 继续是 `needs_review / diagnostic_limb_ik_goal_driver / writesReusableGltfTrsCandidate=false`。清晰中帧没有证明 run 已生产可用。这个负结果说明当前问题不只是“Unity IK pass 没写骨骼”，还涉及 goal endpoint/pivot 语义、手腕 corrective 子节点、controller/runtime IK 权重或 layer 语义；二骨诊断只能作为排查证据，不能进入生产通过条件。
- 同轮继续把二骨诊断扩展为“链长/可达性”证据。复测 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_TwoBoneReachDiag_Current` 显示四个 goal 都在二骨链最大可达距离之外：Left/RightHand 链长约 `0.448m`，目标到 upper arm 约 `0.591/0.593m`，短缺约 `0.143/0.145m`；Left/RightFoot 链长约 `0.791m`，目标到 thigh 约 `0.972/0.982m`，短缺约 `0.181/0.191m`。这些短缺值几乎等于最终残差，说明手脚已经被拉到当前二骨链的可达边界；继续调 CCD、hint 或固定 wrist offset 都不会解决。下一步应校准 RootT/RootQ + body-local goal 的空间/尺度、Avatar humanScale、goal endpoint/pivot 和 controller IK 权重语义。
- 2026-06-25 继续追加 body-local goal scale-fit 诊断，输出在 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_GoalScaleFitDiag_Current`。`Avatar.humanScale` 在当前 Unity 6 worker 可见路径下记录为 `1.0`；为了把当前 body-local goal 沿同一方向缩放到二骨链刚好可达，Left/RightFoot 的 `fitScale` 大约在 `0.80..1.76` / `0.80..1.69`，Left/RightHand 甚至在 `0.02..1.69` / `0.05..1.70` 间跳变，最大目标偏移约 `0.38m`。这说明 Pelica run 不是“乘一个统一 Avatar humanScale 或全局比例”就能修好的问题；下一步优先查 goal endpoint/pivot、IK 权重曲线、controller runtime 参数、additive/layer 语义和 Unity Humanoid IK 解算时机。新增字段只作为诊断证据：`min/maxDirectTwoBoneIkAvatarHumanScale`、`min/maxDirectTwoBoneIkReachFitScale`、`maxDirectTwoBoneIkReachFitScaleOffsetFromCurrentGoal`，不能把 `diagnostic_limb_ik_goal_driver` 升级为生产通过。
- 2026-06-25 继续补 `goalSpaceCandidates` 的二骨链可达性字段，输出在 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_GoalCandidateReachDiag_Current`。每个候选现在都会写 `min/maxTargetDistanceFromUpper`、`min/maxReachShortfall` 和 `maxInsideMinReachAmount`，apply report 还会给每个 final goal 写 `bestReachableGoalSpaceCandidate`。Pelica run 的四个 goal 都没有可达候选：Left/RightFoot 最好仍是 `current_body_local_root_tr`，reach shortfall 约 `0.18/0.19m`；Left/RightHand 最好是 `body_position_root_rotation` / `body_position_no_rotation`，reach shortfall 仍约 `0.09..0.10m`。因此当前列举的简单空间解释，包括 current body-local、root transform、body position + root rotation、raw world 等，都不能把目标点变成目标骨架可达点。视觉帧 `VisualFrames\upper_mid_frame_8.png` 可作为该诊断输出的清晰对照，但报告仍为 `needs_review / diagnostic_limb_ik_goal_driver / writesReusableGltfTrsCandidate=false`。下一步应转向 Unity Humanoid IK 的 endpoint/pivot 定义、goal 权重/effector 语义、controller runtime 参数和 additive/layer 组合，而不是继续添加同类 Root/body 空间猜测。
- 2026-06-25 继续追加 `axis_basis_scan_*` 诊断，输出在 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_AxisBasisDiag_Current`。该诊断会穷举 48 种 signed axis basis，把同一帧 Hand/Foot T 当作 body-local 向量重排后只计算目标点和二骨链可达性，不改变实际 `SetIKPosition`，也不改变 glTF 输出或生产门禁。Pelica run 结果：LeftHand 可达候选为 `axis_basis_scan_mirror_pz_py_px`，RightHand 可达候选为 `axis_basis_scan_proper_ny_pz_nx`，但两只手命中的轴基不一致；LeftFoot/RightFoot 仍没有全段可达轴基，最佳 shortfall 约 `0.149m/0.145m`。这排除了“一个统一 Unity/glTF 坐标基整体错位”这个简单解释；后续重点应继续放在 Unity Humanoid IK endpoint/pivot、真实 goal 权重/effector、controller runtime 参数、additive/layer 组合，而不是把某个轴基候选直接拿来修姿态。
- 2026-06-25 继续追加 `animator_body_*` Body Transform 对照，输出在 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_AnimatorBodyGoalDiag_Current`。Unity Humanoid 文档说明 muscle curves 和 Hand/Foot IK goals 相对 Body Transform 保存，因此该诊断用 `Animator.bodyPosition/bodyRotation` 代替从 `RootT/RootQ` 或 `MotionT/MotionQ` 解析出的 body world transform，只计算候选距离，不改变实际 IK target。结果显示手部候选可达性较好，但 LeftFoot 仍约 `0.18m`、RightFoot 约 `0.14m` 残差，不能形成统一修复；因此当前不要把 `animator_body_local` 直接升级为默认求解，它只排除了“简单替换 Body Transform 来源即可修复 Pelica run”的假设。
- 同轮追加 IK goal 权重诊断，输出在 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_IkWeightMissingDiag_Current`。raw Unity result 与 apply report 现在都会写 `weightRule`、`hasExplicitIkGoalWeightCurves`、`explicitIkGoalWeightCurveCount`。Pelica run 显示没有明确 Hand/Foot IK 权重曲线（`false/0`），所以当前 driver 使用 layer weight 是诊断近似，可能过度施加游戏运行时会由 IK weight 或 controller 参数门控的 goal。只要缺真实权重语义，`usedIkGoalDriverDiagnostic=true` 的结果仍必须保持 `diagnostic_limb_ik_goal_driver` / `writesReusableGltfTrsCandidate=false`，不能进入生产 smoke。
- 同轮复查发现 apply report 里 `bestBoneTargetCandidate` 会被低覆盖 closest descendant 误导：Pelica run 的左右手只有 `7/22` 帧最近点落在 hand/descendant 上，却因为最大距离最小被旧字段选成 best。现在报告拆成 `bestAnyBoneTargetCandidate` 和 `bestStableBoneTargetCandidate`，稳定候选要求覆盖率至少 `80%`，旧 `bestBoneTargetCandidate` 也指向稳定候选。复测后左右手 `bestAny` 覆盖率为 `0.318`，`bestStable` 仍是 `human:LeftHand/RightHand` 且残差约 `0.143m/0.145m`。这进一步证明当前缺口不是“找一个最近子节点就能解释”，而是 IK goal 目标点/控制器语义还没有稳定复原。
- 同轮又补了稳定候选本地 offset 诊断：`boneTargetCandidates` 会记录 dominant goal 到候选骨骼的局部 offset 范围、span 和长度范围。Pelica run 的 HumanHand/HumanFoot 稳定候选覆盖率都是 `1.0`，但 `localOffsetSpan` 约 `0.16m-0.20m`，最小长度接近 `0`，最大长度接近残差。这排除了“缺一个固定手掌/脚底 local offset”这个简单解释；后续重点仍是动态 IK goal 语义、TDOF/root/body、controller runtime 参数、IK 权重和 additive/layer 语义。
- 继续把 TDOF 从“只计数”改成三轴摘要后，Pelica run 的 `SpineTDOF` / `ChestTDOF` 都是静态 0：`dynamicCurveCount=0`、`valueRangeSpan=0`、`maxDeltaFromFirst=0`。这说明这条样本当前残差不是动态 TDOF 引起；TDOF 仍要在其他样本继续验证，但 Pelica run 的下一优先级应放在 IK goal endpoint/weight、controller runtime 参数、additive/layer 组合和 Unity Humanoid 解算残差。
- 2026-06-24 继续补强 AnimatorController recovery 的依赖闭包：手动 `--recover_imported_animator_controllers` 未显式传 `--source_index` 时，会从素材库 `source_index_usage.json`、`animator_controller_context_refresh.json`、`sqlite_index_summary.json` 自动找完整 `unity_source_index.db`，用 `source_objects(AnimationClip)` 补 `PathID -> 原始 m_Name`，再精确匹配 Unity bake 工程里已有的 `ImportedAnimationClip/*.anim`。Pelica 小库 `Endfield_PelicaActor_IdleLoop_ReexportForFallback_Current` 验证：recovery report 自动写 `sourceIndex=D:\Assets\fangzhou\Endfield_FullGame_SourceIndex_PersistentStreamingFix_Current\unity_source_index.db`，controller 从只恢复主层提升为 `Main / LoopClothAddLayer / LoopBodyAddLayer` 三层，`missingClipCount=24`。这一步只补确定性 clip 名称和已有导入 clip，不伪造缺失 clip；生产状态仍保持 `diagnostic_recoverable_skipped_layer_sampling`。
- Unity 官方文档佐证了当前拆分：Root Motion 文档说明 Humanoid clip 中 body transform 是 world-space，muscle curves 和 hand/foot IK goals 相对 body transform 保存；`HumanPoseHandler.SetHumanPose` 文档说明以 `avatar + root` 构造时会把 HumanPose 应用到场景 transform hierarchy。这正是 `editor_curve_set_human_pose_diagnostic` 能转出 TRS、但还必须补 root/IK/TDOF 的原因。参考：https://docs.unity3d.com/2018.2/Documentation/Manual/RootMotion.html 与 https://docs.unity3d.com/6000.3/Documentation/ScriptReference/HumanPoseHandler.SetHumanPose.html

## 1. 为什么现在需要“不允许 Unity bake”规范

这条规范主要防止四类问题。

### 1.1 防止素材库不自足

AnimeStudio 的目标是把 Unity 打包资源还原成可浏览、可复用的素材库。若一个动画必须让用户再准备 Unity 工程、导入 helper、启动 Editor/Player、跑 bake 才能播放，那么输出目录本身就不是完整可用资产库。

### 1.2 防止把“能播放”误判成“已还原”

Unity bake 能调用 native Mecanim 得到一个目标骨架上的姿态，但这不代表我们已经理解了：

- 原始 AnimationClip 里哪些是真实曲线。
- 哪些是 Humanoid muscle、TDOF、root motion。
- 哪些是 extra bone、BlendShape、材质/激活/事件曲线。
- clip 是否需要 AnimatorController layer、BlendTree、OverrideController 或 additive base pose。

如果直接把 bake 结果标成生产级，后续很难判断动画为什么对、为什么错、能否迁移到别的模型。

### 1.3 防止批量性能失控

传统 Unity 采样链路慢，通常不是单个 muscle 求解慢，而是链路太重：

- 启动 Unity Editor/Player。
- 导入或生成临时 asset。
- 实例化 prefab、Renderer、Animator、PlayableGraph。
- 动态修改 controller/graph 触发 Rebind。
- 每帧 `AnimationMode.SamplePlayableGraph` 或 `Animator.Update`。
- 每帧遍历 Transform hierarchy 读回。
- 写大 JSON、再转 glTF。

Unity 官方 Animator 文档明确说：Humanoid clip 评估通常比 Generic 多 15% 到 20% CPU；Rebind 成本高；手动 `Animator.Update` 不使用 Mecanim 并行，若要手动控制且利用并行，应把多个 Animator 放进 PlayableGraph 再统一更新。

来源：https://docs.unity3d.com/6000.4/Documentation/ScriptReference/Animator.html

### 1.4 防止环境依赖不可控

Unity bake 结果可能受 Unity 版本、导入器、Avatar import、AssetDatabase、Library cache、Editor/Player 差异影响。默认生产索引若依赖它，重建和跨机器复现都会变差。

## 2. 规范建议：从“禁止”升级为“三层输出”

建议不要简单保留“所有 Unity bake 都不能碰”，也不要简单废弃规范。更稳的是分三层：

### 2.1 `ProductionDirect`

默认生产路径。

要求：

- 不需要 Unity 进程。
- 直接从 Unity 序列化数据、AnimationClip、ACL、Avatar/HumanDescription、Renderer/SkinnedMeshRenderer/BlendShape 关系写 glTF TRS/weights。
- 可在任意机器重跑 AnimeStudio 得到同类结果。

默认 `model_animations.json`、SQLite 推荐候选、ProductionReady 判断只认这一层。

### 2.2 `UnityOracle`

显式诊断路径。

用途：

- 生成 Unity native 对照姿态。
- 做内部 Humanoid solver 的误差回归。
- 做 rest/mid/end 截图。
- 帮助定位 Avatar、root motion、TDOF、muscle axis 问题。

限制：

- 不作为默认生产可播放证明。
- 报告必须标 `diagnosticOnly` / `notProductionReady`。

### 2.3 `UnityBakeAccelerated`

显式实验 worker 路径。当前阶段只验证 Unity 6 native Humanoid/Muscle 求解能否被批量调用，不进入素材库生产主流程。

用途：

- 验证 `HumanPoseHandler(avatar, jointPaths)` + internal avatar pose 是否能作为高速求解核心。
- 给 AnimeStudio 内部 Humanoid solver 提供误差对照。
- 在独立实验目录生成 request/result/report，帮助判断这条路线是否值得继续工程化。

必须标注：

```json
{
  "animationSolvePath": "UnityBakeAccelerated",
  "unityVersion": "6000.x",
  "avatarSource": "ImportedAvatarAsset",
  "clipSource": "...",
  "sampleRate": 30,
  "requiresUnityToReproduce": true,
  "sourceIsDirectUnitySerialized": false,
  "productionStatus": "experimental_worker_only",
  "writesLibraryIndex": false,
  "writesModelAnimations": false,
  "needsDirectTrsAnimation": true
}
```

它不能被 UI、SQLite、`model_animations.json` 或默认 Library 当成可复用动画证明；当前只允许作为独立实验产物存在。

## 3. 如果严格遵守规范，怎么解决动画

严格路线不是只做 Humanoid solver。应先把动画分层。

### 3.1 Generic/Transform：直接 glTF TRS

适用：

- `AnimationClip` binding 指向 Transform local position/rotation/scale。
- 普通骨骼动画、道具、机关、场景物件。
- Humanoid clip 中保留下来的 extra bones，例如头发、衣摆、socket、武器挂点。

输出：

- glTF node TRS channel。
- quaternion 连续性处理。
- binding path 到 glTF node 的报告。

### 3.2 BlendShape/表情：直接 glTF weights

适用：

- binding 明确为 `blendShape.*`。
- mesh morph target 与 SkinnedMeshRenderer blendshape channel 能对上。

注意：

- Renderer/Material 属性曲线不能当成 BlendShape。
- 表情与身体 Humanoid 动画分开验收。

### 3.3 ACL transform tracks：直接 glTF TRS

适用：

- `m_AclCompressedBuffer.TransformBufferData` 中存在 qvvf transform tracks。
- track binding 能映射到骨骼或节点。

做法：

- 先解压 ACL transform tracks。
- 直接写 glTF TRS。
- 报告记录 track 数、matchedTracks、missingBones、辅助节点覆盖情况。

### 3.4 ACL scalar / streamed / dense muscle：内部 Humanoid solver

适用：

- `FloatBufferData`、streamed/dense/constant clip 里有 `AnimatorMuscle`、TDOF、RootT、RootQ、HumanPose 相关曲线。

内部 solver 需要：

1. 解真实逐帧 muscle/root/TDOF 曲线。
2. 用 Avatar/HumanDescription/rest pose 建 human bone 到 skeleton node 的映射。
3. 还原每个 human bone 的 local rotation/translation。
4. 合并 extra bone、BlendShape、Transform tracks。
5. 输出 glTF TRS/weights。
6. 用 UnityOracle 做误差报告和视觉样本。

这是最难但最符合规范的主线。

### 3.5 AnimatorController context：先标注，不硬解

如果 clip 是 additive、layer 片段、上半身覆盖、root motion 片段、附件片段，或需要 BlendTree/OverrideController/base layer，则不能把单 clip 标完整动作。

处理：

- 标 `needsAnimatorControllerContext`。
- 不进默认推荐。
- 后续实现 controller 状态组合求解或显式预览。

## 4. 如果放宽规范，Unity bake 怎样批量快

放宽后，Unity bake 不能沿用完整 prefab / PlayableGraph / Transform readback 重链路。推荐三档。

### 4.0 Unity 宿主工程定位

如果放宽规范并继续借 Unity 的 native Humanoid/Muscle 求解器，就必须有一个 Unity 宿主。原因很简单：`HumanPoseHandler` 的真正求解在 Unity native runtime 里，AnimeStudio 普通 .NET CLI 不能直接调用。

当前新版路线只考虑一个宿主形态：提前创建好的通用 Unity 6 工具项目 `AnimeStudioUnityBakeWorker`。

`AnimeStudioUnityBakeWorker` 的定位：

- 它是通用工具项目，不是游戏项目。
- 它由用户提前创建，或由显式 setup 命令一次性初始化，之后长期复用。
- 它只允许使用 Unity 6 / 6000.x。
- 它只放新版 `UnityBakeAccelerated` helper、队列、缓存和必要的临时工作区。
- 它不长期保存某个游戏的 Avatar、AnimationClip、Prefab 或导入缓存。
- 它当前只用于实验 worker 验证，不接入默认素材库生产流程。

版本门禁必须更严格：

- `UnityBakeAccelerated` 只支持当前统一选定的 Unity 6 worker，例如项目当前验证用的 Unity 6000.x。
- 不支持为单个游戏安装或调用旧 Unity Editor，例如 Unity 2017.4.40f1。
- 如果某个游戏的 Avatar、AnimationClip、AssetBundle 或导入器行为只有旧 Unity 才能正常工作，这个游戏不能进入 `UnityBakeAccelerated` 支持名单。
- Unity 6 不能加载或不能求解时，结果应标 `unity6Unsupported` / `notUnityBakeAcceleratedSupported` / `needsDirectTrsAnimation`，而不是扩展旧 Unity 版本矩阵。

推荐长期形态：

```text
AnimeStudio CLI
  解包/解析 AnimationClip、ACL、Muscle、RootT/RootQ、Transform/BlendShape 曲线
  生成按 Avatar 分组的 muscle/root/jointPaths 请求
        |
        v
UnityBakeWorker 常驻进程
  缓存 Avatar
  缓存 HumanPoseHandler(avatar, jointPaths)
  缓存 NativeArray、jointPaths、muscle index
  输出目标骨架每帧 local TRS
        |
        v
AnimeStudio
  写 glTF animation channels
  写来源、Unity 版本、Avatar、clip、sampleRate、UnityDerived 标记
```

关键区别：

- 如果 AnimeStudio 已经能解出 muscle/root 逐帧曲线，Unity worker 只负责 `HumanPoseHandler` 求解，不必把每个 `.anim` 导入 Unity。
- 如果还依赖 Unity 采样 `AnimationClip`，这条请求不属于第一版 `UnityBakeAccelerated`，应先拒绝或标记为需要后续研究。
- 如果依赖原始 Unity `Avatar` asset，worker 只应按 request 或 hash cache 临时加载，不能把资产长期塞进通用工程。

长期建议提前准备一个最小 `AnimeStudioUnityBakeWorker` 工程，并在文档和配置中把它标成显式可选依赖。这个工程应像 Blender、Unity Editor 路径一样作为本机工具环境配置好，而不是每次 CLI 运行时临时创建。

CLI 推荐行为：

- 用户可以显式传入 `--unity_project D:\misutime\AnimeStudioUnityBakeWorker`，或在配置里设置默认 worker 工程。
- CLI 只做快速检查：路径存在、Unity 版本是 6000.x、helper 文件存在且版本 marker 合格、worker smoke test 通过。
- 检查通过后直接启动/连接常驻 worker 并投递任务，不在批处理热路径创建 Unity 工程。
- 只有用户显式运行 setup/repair 命令时，才创建、初始化或同步 helper，例如 `--setup_unity_bake_worker D:\misutime\AnimeStudioUnityBakeWorker`。
- 游戏相关 Avatar/Clip 不应长期塞进通用工程主目录；应按 request、hash cache 或临时 workspace 管理，避免一个游戏污染另一个游戏。
- 在本阶段，CLI 即使能检查 worker，也不得自动把结果写回素材库索引或默认动画推荐；最多生成独立实验报告和独立输出目录。

多个 worker 并发时不要让多个 Unity Editor 同时打开同一个 project path；应从这个通用 worker 工程复制独立工程副本、使用独立 Library cache，或后续升级为 Unity Player worker。

当前实验代码形态：

- Unity 侧新增独立文件：`AnimeStudioUnityBakeAcceleratedModels.cs`、`AnimeStudioUnityBakeAcceleratedSolver.cs`、`AnimeStudioUnityBakeAcceleratedWorker.cs`。
- `AnimeStudioBakeCli.cs` 只增加 `-animeStudioAcceleratedBakeRequest` 单次实验入口；它不读取 `model_animations.json`，也不从 Library 选择候选。
- CLI 侧新增 `--check_unity_bake_accelerated_worker`，只验证通用 worker 工程、Unity 6 版本和 helper marker。
- CLI 侧新增 `--run_unity_bake_accelerated <request.json>`，只运行用户显式提供的独立 request JSON。
- CLI 侧新增 `--unity_bake_accelerated_worker_queue <dir>`，只用于实验常驻 worker 队列。
- 这些入口不得调用 Library 导出、SQLite 写入、动画候选生成、glTF 合并或默认预览缓存。
- Unity 侧提供 `AnimeStudio.UnityBake.AnimeStudioUnityBakeAcceleratedWorker.SmokeTest`，用于 batchmode 验证脚本编译、Unity 6 版本和 `HumanPoseHandler.SetInternalHumanPose(ref HumanPose)` 可用性。

当前验证命令：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --check_unity_bake_accelerated_worker `
  --unity_project "D:\misutime\AnimeStudioUnityBakeWorker" `
  --unity_editor "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe"

& "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe" `
  -batchmode -quit `
  -projectPath "D:\misutime\AnimeStudioUnityBakeWorker" `
  -executeMethod AnimeStudio.UnityBake.AnimeStudioUnityBakeAcceleratedWorker.SmokeTest `
  -logFile "D:\misutime\AnimeStudioUnityBakeWorker\Logs\unity_bake_accelerated_smoke.log"
```

第一版 request 只允许表达：

```json
{
  "version": 1,
  "mode": "UnityBakeAccelerated",
  "avatarAsset": "Assets/.../Avatar.asset",
  "avatarKey": "optional-stable-key",
  "outputJson": "D:/.../unity_bake_accelerated_result.json",
  "jointPaths": ["", "Hips", "Hips/Spine"],
  "clips": [
    {
      "clipKey": "clip-key",
      "clipName": "clip-name",
      "frameRate": 30,
      "samples": [
        {
          "time": 0.0,
          "bodyPosition": { "x": 0, "y": 0, "z": 0 },
          "bodyRotation": { "x": 0, "y": 0, "z": 0, "w": 1 },
          "muscles": []
        }
      ]
    }
  ]
}
```

第一版 result 只允许作为实验输出：

- `mode=UnityBakeAccelerated`。
- `writesLibraryIndex=false`。
- `writesModelAnimations=false`。
- `values` 布局固定为每个 `jointPaths` 节点 7 个 float：`T.xyz + Q.xyzw`。
- `setPoseMethod=HumanPoseHandler.SetInternalHumanPose`。

### 4.1 第一档：HumanPoseHandler internal avatar pose

这是最值得优先验证的加速路线。

核心思路：

```csharp
var handler = new HumanPoseHandler(avatar, jointPaths);
var pose = new HumanPose
{
    bodyPosition = bodyPosition,
    bodyRotation = bodyRotation,
    muscles = sampledMuscles,
};
handler.SetInternalHumanPose(ref pose); // 需要和 SetHumanPose 做 benchmark 对照
handler.GetInternalAvatarPose(nativeArray); // jointCount * 7: T.xyz + Q.xyzw
```

官方依据：

- `HumanPoseHandler(avatar, jointPaths)` 可以不绑定 Transform hierarchy，并在 HumanPose 与 local joint transforms 数组之间转换。
- Unity C# reference 中 `SetInternalHumanPose`、`GetInternalAvatarPose` 标有 `IsThreadSafe = true`。

来源：

- https://docs.unity3d.com/6000.0/Documentation/ScriptReference/HumanPoseHandler-ctor.html
- https://github.com/Unity-Technologies/UnityCsReference/blob/master/Modules/Animation/ScriptBindings/HumanPoseHandler.bindings.cs

为什么它可能快：

- 不实例化完整 prefab。
- 不需要 Renderer/SkinnedMeshRenderer。
- 不需要 AnimationMode。
- 不从 Transform hierarchy 逐帧读回。
- 可预分配 `NativeArray<float>`。
- 可按 Avatar 缓存 handler、jointPaths 和 muscle index。

它最适合：

- `sampledMuscles -> local joint pose`。
- 内部 solver 的 oracle。
- 同 Avatar 大量 clip 批处理。

必须验证：

- `SetInternalHumanPose` 与 `SetHumanPose` 哪个空间更接近 Transform bake。
- bodyPosition/bodyRotation、RootT/RootQ、TDOF 如何映射。
- hand pose、IK goal、mirror、muscle 超出 [-1, 1] 时是否一致。
- `jointPaths` 顺序与 glTF skeleton 节点顺序是否稳定。
- `IsThreadSafe` 是否能在 BatchMode 中用多个 handler 并行，而不是共享同一个 handler。

项目现状：

- `AnimeStudioPlayableBaker` 已经有 `CreateJointPathPoseHandler`、`CaptureInternalAvatarPose`、`CaptureEditorCurveInternalAvatarPoseTimeline` 等诊断代码。
- 现有 Unity 侧代码里已经出现过 `HumanPoseHandler(avatar, jointPaths)` 与 internal avatar pose 片段，但它们没有形成独立批量协议。新版应从这里抽离出轻量 `InternalAvatarPoseBatchSolver`，并用新的 request/result 格式承载。

### 4.2 第二档：持久 Unity worker + 按 Avatar 分组

项目已有 `AnimeStudio.UnityBake` worker，方向正确：不要每个动画启动 Unity。

升级目标：

- Worker 常驻。
- 请求按 Avatar/rest pose/jointPaths 分组。
- Worker 内缓存 Avatar、HumanPoseHandler、NativeArray、jointPaths、muscleName/index。
- 主进程负责解包、ACL 解压、曲线采样、glTF 写出。
- Worker 只负责 Humanoid native 求解。
- 输出紧凑二进制或 MessagePack，不写大 JSON。

推荐批任务结构：

```text
BatchRequest
  avatarKey
  jointPaths
  clips[]
    clipKey
    sampleRate
    sampleCount
    bodyPoseSamples
    muscleSamples[frame][muscle]
```

关键原则：

- 不要模型 x 动画笛卡尔积 bake。
- 同 Avatar 同 clip 只求解一次。
- 多模型复用中间 joint track，再按需要映射/打包。

### 4.3 第三档：多 worker 分片

当单 worker 跑满后，再考虑多 Unity 进程。

建议：

- 按 Avatar 或 bundle 分片。
- 每个 worker 用独立临时工程或独立 Library cache。
- 尽量不要依赖 AssetDatabase import；用预生成 Avatar asset 和纯数据请求。
- 主进程合并结果、去重、失败重试。

风险：

- Unity license、AssetDatabase lock、磁盘 IO、内存会限制扩展。
- 多进程不一定线性提速，必须用 profile 数据判断。

### 4.4 只有必要时才用 PlayableGraph bake

PlayableGraph 适合处理 internal pose 覆盖不了的情况：

- AnimatorController layer。
- BlendTree。
- OverrideController。
- IK。
- 复杂 root motion。
- 完整 prefab 组件参与结果。

优化原则：

- 不要每帧每对象 `Animator.Update`。
- 多个 Animator/clip 放进一个或少数几个 PlayableGraph，统一更新 graph。
- 稳定 graph，减少运行中增删 playable。
- 避免动态 OverrideController。
- 禁用不需要的 layer、事件、IK、Renderer。
- 只记录 glTF 需要的节点。

官方依据仍是 Animator 文档中的 Rebind 和并行执行说明：

来源：https://docs.unity3d.com/6000.4/Documentation/ScriptReference/Animator.html

## 5. 社区和 GitHub 调研结论

### 5.1 没有成熟开源 Mecanim 等价 solver

我没有查到可直接替代 Unity native Mecanim 的完整开源 `Muscle -> target skeleton TRS` solver。

社区常见项目大多属于：

- 仍调用 Unity Humanoid 系统。
- 处理已经有的 HumanPose/muscle 数据。
- 做简单 retarget。
- 做 Generic/Transform 导出。
- 做 ACL 解压或压缩，不负责 Humanoid 求解。

### 5.2 AssetStudio/AssetRipper 类工具能解析数据，但 Humanoid 仍难

AssetStudio 能解析 AnimationClip 的大量序列化结构，也支持 Animator + AnimationClip 导出 FBX。

来源：

- https://github.com/Perfare/AssetStudio
- https://github.com/Perfare/AssetStudio/blob/master/AssetStudio/Classes/AnimationClip.cs

但 AssetStudio issue 中长期存在 Humanoid 动画导出、FBX 姿态、clip 绑定、动画缺失等问题，说明“读出 AnimationClip 结构”不等于“完整恢复 Humanoid 动作”。

示例：

- https://github.com/Perfare/AssetStudio/issues/221
- https://github.com/Perfare/AssetStudio/issues/707
- https://github.com/Perfare/AssetStudio/issues/782

### 5.3 ACL 是解决真实采样载荷的关键，不是 Humanoid solver

ACL 官方项目强调快速、高精度解压和生产级压缩。

来源：https://github.com/nfrechette/acl

`AclUnity` 说明 ACL 适合 data-oriented / Burst jobs 场景，并强调无需传统曲线 keyframe search。

来源：https://github.com/Dreaming381/AclUnity

对 AnimeStudio：ACL 应作为采样载荷解码层，先把 transform/scalar tracks 还原出来，再决定 direct glTF 或 Humanoid solver。

### 5.4 Runtime retarget 项目不能直接替代 Mecanim

`RuntimeRetargeting` 提供两种方式：Quaternion 复制和 `HumanPoseHandler`。

来源：https://github.com/fengkan/RuntimeRetargeting

Quaternion 复制可用于同骨架或实验 fallback，但缺少 Avatar muscle 限制、人体比例归一和 root/body 逻辑，不能当通用生产级 Humanoid 还原。

### 5.5 HumanPose 数据项目可参考存储和插值

`MuscleCompressor` 说明可以把 Humanoid muscle 数据外部保存和压缩。

来源：https://github.com/gree/MuscleCompressor

`MuscleInertialization` 用 HumanPose muscles/body 做插值，但 README 标明是学习/实验用途。

来源：https://github.com/akasaki1211/MuscleInertialization

它们对 AnimeStudio 有参考价值，但不是解包后 `Muscle -> glTF TRS` 的完整答案。

### 5.6 UnityGLTF 证明 Unity bake 可行，但仍是 Editor bake

UnityGLTF README 写明 Humanoid 动画导出会 bake 到目标 rig，Animator export 只在 Editor 可用。

来源：https://github.com/KhronosGroup/UnityGLTF

这说明 Unity bake 是成熟思路，但也证明它不是无 Unity 的直接生产路径。

## 6. 推荐落地计划

### 6.1 先做性能真相 benchmark

新增一个小工具或命令，比较三条路径：

1. 当前 `AnimeStudioPlayableBaker`：PlayableGraph + AnimationMode + Transform readback。
2. `InternalAvatarPoseBatchSolver`：`HumanPoseHandler(avatar, jointPaths)` + internal avatar pose。
3. 直接路径：ACL/Transform TRS 写 glTF，不进 Unity。

指标：

- 每 clip 总耗时。
- 每帧求解耗时。
- 每 clip 分配量和 GC。
- 失败率和失败原因。
- 与当前 Unity bake 的骨骼旋转/位置误差。
- 输出 glTF channel 数、主体骨骼覆盖。

样本：

- Freedunk：角色身体动作、球/道具/场景 Transform 动画、BlendShape。
- Endfield：含 `m_AclCompressedBuffer.FloatBufferData` 和 `TransformBufferData` 的 clip。

### 6.2 短期实现

- 保持默认规范不变。
- 把 Unity bake 改名或标注为 `UnityOracle`。
- 从现有 `AnimeStudioPlayableBaker` 拆出轻量 internal avatar pose 批处理。
- 所有 Unity 派生结果写 `UnityDerived` / `requiresUnityToReproduce`。
- `editor_curve_set_human_pose_diagnostic` 只能作为求解器诊断。Pelica idle 复测显示：把动态 `RootT/RootQ` 写入 `HumanPose.bodyPosition/bodyRotation` 后，旧输出里双手抬高、手腕扭曲的严重错误会消失，但姿态仍接近 A/T 基准，并不是合格 idle。该样本同时存在 28 条动态四肢 IK goal 曲线，当前 `HumanPose.SetHumanPose` 诊断链路没有消费这些曲线，所以报告必须保留 `needs_review`，不能进入生产验收。
- 2026-06-24 继续验证 IK goal：给临时 AnimatorController 开启 IK pass，并用非 Editor 组件在 `OnAnimatorIK` 中按 `Left/Right Hand/Foot T/Q` 曲线喂 `Animator.SetIKPosition/Rotation`。技术结论是 `AnimatorControllerPlayable.SetTime(...)` 后如果只 `Evaluate(0)`，batchmode 中 IK pass 可能只留下静态 pose；显式 IK 实验用极小正 delta 可以触发逐帧 IK，runtime tracks 会变成 frame-varying。视觉结论仍失败：Pelica idle 又出现双手抬高到头部附近，说明当前把 goal T/Q 当作 body-space world target、并用隐式权重 1 的解释不正确，或仍缺原始 controller layer/weight/IK 权重语义。该实验只能通过 `ANIMESTUDIO_UNITY_BAKE_ENABLE_IK_GOAL_DRIVER=1` 显式开启，不能作为默认输出路径。
- 2026-06-24 复查上一条 IK goal 结论：旧实验的 `OnAnimatorIK` 实际没有被调用，`editorCurveIkGoalDriverDiagnostic.callCount=0`。原因是 batch/editor 采样链路里的临时 MonoBehaviour 需要 `[ExecuteAlways]` 才会进入 `OnAnimatorIK`。加上 `[ExecuteAlways]` 后，Pelica idle 诊断输出 `callCount=63`、`appliedGoalCount=252`、`layerIndexes=[0]`，清晰 upper/full/hand 截帧显示双手不再抬到头部，手腕也不再明显麻花，姿态更接近 idle。因此当前更准确的结论是：`Left/Right Hand/Foot T/Q` limb goal 曲线对 Endfield 人形动作很关键，不能忽略；但这个路径仍是显式 IK 诊断，使用隐式权重 1 和当前 goal 空间解释，且报告仍保留 `generated_animator_controller_context`、`needs_animator_controller_context`、`dynamic_limb_goal_curves_not_solved` 阻断，不能直接作为生产动画验收。
- 2026-06-24 Pelica idle 复测补充：`ClothCombine.SequenceNode` 是复杂/序列型 BlendTree，根节点没有可解释的 Simple1D 参数。恢复器不能因为复杂树里只解析到一个可加载 leaf clip，就把它当作整层 motion；这会把 `A_actor_pelica_idle_loop_additive` 作为 full-body Override 层叠上去。已收紧为只恢复根节点可解释的 Simple1D，复杂根跳过并写 warning。修复后 `ClothCombine` 不再进入 mixer，但 no-controller 主 clip、ComplexBlendTreeSkip 和 IK goal driver 三条对照截图仍显示双手高举，不符合 idle 语义。当前结论应是 `needs_limb_ik_goal_solver`，阻断原因包含 `dynamic_limb_goal_curves_not_solved`；不能把该样本作为动画通过证据。
- 2026-06-24 原神旧 Unity bake 复盘结论：旧链路真正可复用的是门禁和 oracle 思路，而不是手工工程链路本身。可取部分包括 ImportedAvatar 必须由 Unity `AssetDatabase.LoadAssetAtPath<Avatar>` 加载并通过 `isValid/isHuman`，PlayableGraph 采样结果只能作为 `UnityOracle` 或显式 `UnityBakeAccelerated` 求解输出，最终仍要写回普通 glTF TRS 并保留 Avatar、clip、controller、采样率和视觉验收报告。不可取部分是让用户维护游戏专用 Unity 工程、临时导入 Avatar/AnimationClip/Prefab、再把 `BuildHumanAvatar` / AvatarConstant / glTF rest pose 当生产兜底；这些只能保留为迁移或诊断。
- 2026-06-24 Pelica idle 新复测：在自动刷新 AnimatorController context、恢复 ImportedAnimationClip/ImportedAnimatorController，并显式开启 `--unity_bake_sample_recoverable_skipped_layers_diagnostic` 后，`LoopClothAddLayer` 被计入 `animatorControllerDiagnosticSampledSkippedLayerCount=1`，`animatorControllerSkippedUntrustedLayerCount=0`。清晰 upper/left_hand/right_hand 截图显示双手回到身体两侧，未再出现用户截图里的脸前举手和手腕麻花。该结果说明当前主要错误来自缺 AnimatorController 附加层/IK 上下文，而不是模型 skin 本身；但报告仍为 `productionStatus=diagnostic_recoverable_skipped_layer_sampling`，阻断原因包含 `fallbackZeroBlendParameterLayerCount=1`、`animator_controller_recoverable_skipped_layers_sampled_diagnostic` 和 `limb_ik_goal_driver_diagnostic_unverified`，所以只能作为 oracle 诊断，不能算生产动画通过。下一步应追 `IdleTailIndex` 的真实运行时/Controller 参数来源，或恢复原始 RuntimeAnimatorController layer/BlendTree/IK 权重语义。
- 2026-06-25 追加 IK goal 诊断字段：`editorCurveIkGoalDriverDiagnostic.goalLayerSummaries` 会按 `goal + layer` 汇总完整采样时间线里的 layer weight 范围、local/world goal 位置范围和样本数。`samples` 仍只保留少量明细，summary 用来确认 Pelica 这类诊断是否实际在多个 AnimatorController layer 上以相同权重写入 IK goal。这个字段只提供求解证据，不解除 `limb_ik_goal_driver_diagnostic_unverified`；生产路径仍需要恢复真实 IK 权重、goal 空间和 controller runtime 语义。
- 2026-06-25 继续收紧 IK goal 权重来源：`AnimeStudioEditorCurveIkGoalDriver` 在存在 `ControllerLayerState.Weight` 时优先使用恢复出的 controller layer 权重，并在 `layerWeightSource` / `goalLayerSummaries[*].layerWeightSource` 标明来源；只有没有显式 layer state 时才回退 `Animator.GetLayerWeight`。这避免 `AnimationLayerMixerPlayable` 诊断把 Unity Animator 当前层权重误当作已恢复的真实权重。Pelica 当前三层仍为 `1.0`，因此它证明“恢复出的默认 layer weight 就是 1”，但不证明 runtime 参数、IK 权重曲线或 goal 空间已经完整恢复。
- 2026-06-25 继续补 IK goal 空间证据：`AnimeStudioEditorCurveIkGoalDriver` 会记录每个 hand/foot goal 对应 Unity Humanoid 骨骼的 pre-IK / post-evaluate world position 和到当前解释后 world goal 的距离范围。这个诊断用于判断“goal 空间解释错”“IK 权重/层权重错”还是“controller 上下文仍不完整”；它不改变当前姿态，不解除 `limb_ik_goal_driver_diagnostic_unverified`，也不能单独证明动画生产可复用。
- 2026-06-25 复用旧 Browser / 原神 Unity bake 的信任门禁：`AnimationPreviewCache` 早已要求 ImportedAvatar oracle 场景下，`AnimationClip` 也必须来自 Unity 工程内明确导入的 `unityAssetPaths.animationClip`，并写出 `unityBakeImportedAnimationClip`，否则旧缓存不能算可信可播放。SQLite `animation_bake_cache` 摘要已同步这条规则：`IsTrustedBakedGltfPath` 不再只看 `status/frameVaryingTracks/Avatar`，还会检查 ImportedAnimationClip 证据。这个改动只影响 trusted bake 统计和覆盖率，不会把诊断 glTF 删除，也不会把动画求解结果伪装成生产通过。
- 2026-06-25 同步补齐 `UnityBakeRequestGenerator` 的快速摘要信任门禁：ImportedAvatar oracle 场景下，`animation_bake_cache_summary` 也必须看到 `unityBakeAnimationClipSource=unityAssetPaths.animationClip` 和 `unityBakeImportedAnimationClip`，才能把 baked glTF 计为 trusted。这样 Browser、SQLite 重建摘要和 bake 批量摘要三处口径一致。另修 `SourceModelAnimationLister` 的预筛逻辑：传入动画正则时先扩大 shared Avatar 候选预筛，再做严格正则，避免 dialog/cutscene 候选在 SQL `ORDER BY + LIMIT` 前段挤掉 Pelica `run_loop` / `walk_loop` 等真实动作候选。
- 2026-06-25 继续细化 IK goal 诊断：旧的 `goalLayerSummaries` 是按 `layer + goal` 统计，Unity 最终骨骼姿态却是多层混合后的结果；如果直接用每层 goal 距离判断，可能把 layer 混合效应误判为 goal 空间错误。新增 `finalGoalSummaries`，按每帧同一 hand/foot 的最高 layer weight 目标作为“主导目标”记录最终 post-evaluate 距离，并记录 `maxLayerTargetSpread` 判断各层目标是否互相冲突。Pelica `run_loop` 新验证目录 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_FinalGoalDiag_Current` 显示 `statusBasis=final_dominant_goal`、`maxLayerTargetSpread=0`，说明该样本当前不是多层 goal 互相打架；但 hand/foot 到主导目标最大距离约 `0.145/0.191m`，报告仍为 `diagnostic_limb_ik_goal_driver`，不能进入生产通过。
- 2026-06-25 继续增加 IK goal 空间候选对照：`finalGoalSummaries[*].goalSpaceCandidates` 会在不改变实际 `SetIKPosition` 目标的前提下，同时计算 `current_body_local_root_tr`、`root_transform_point`、`root_rotation_offset`、`body_position_root_rotation`、`body_rotation_root_origin`、`body_position_no_rotation`、`raw_world` 等候选 world target 到最终手脚骨骼的距离。Pelica `run_loop` 验证目录 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_GoalSpaceDiag_Current` 显示全局最优仍是当前 `current_body_local_root_tr`，最大距离约 `0.19m`；root/world 类候选明显更差，说明当前问题不像是简单把 goal T 当错 root/world 空间。手部的 `body_position_root_rotation` 略小于当前解释，但差距只有厘米级，只能作为后续公式/权重对照线索，不能直接切换求解公式或解除 `limb_ik_goal_driver_diagnostic_unverified`。
- 2026-06-25 继续验证 IK 写入是否被 Unity 接收：`goalLayerSummaries[*]` 现在记录 `hasIkReadback`、`maxIkReadbackDistanceToGoal`、`min/maxIkReadbackPositionWeight`、`maxIkReadbackRotationWeight`。Pelica `run_loop` 验证目录 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_IkReadbackDiag_Current` 显示 12 个 layer+goal 全部 `hasIkReadback=true`，`maxIkReadbackDistanceToGoal=0`，position/rotation weight 都为 `1`。这说明当前诊断路径里 Unity `Animator.SetIKPosition/Rotation/Weight` 的目标和权重已被接收，问题不在 IK pass 没调用、target 被覆盖或权重没写进去；后续应重点查 IK target 对应的人体目标点定义、TDOF/手脚骨约束、Unity Humanoid 解算残差、以及原始 controller runtime 参数/IK 权重语义。
- 2026-06-25 复查旧原神 / Browser 双击 Unity bake 链路后，确认它对 Endfield 最有用的不是 Unity4 或浏览器触发方式，而是“强证据链 + 诚实终态”：只允许 Unity 显式候选进入 bake，ImportedAvatar 必须 fresh probe 且 `isValid/isHuman`，ImportedAnimationClip / ImportedAnimatorController 必须精确映射，`static_pose`、`needs_review`、`needs_animator_controller_context` 等终态要写回缓存但不能显示为可播放。Endfield 当前自动 fallback 已沿用这些门禁；后续继续从旧链路借门禁、缓存和 PlayableGraph 对照，不借游戏专用 Unity 工程或“能 bake 就算成功”的旧外壳。
- 同轮继续把旧链路的“controller 上下文保真”经验落到 Endfield：`AnimatorController.StateConstant` 其实已经解析了 `m_IKOnFeet`，但之前没有进入 `unity_file_inspect.json`、`unity_source_index.db` 的 controller 关系、`AnimatorControllerContextRefresher`、ImportedAnimatorController recovery request/result 或 Unity bake layer diagnostics。现在已把状态级 `stateIKOnFeet/iKOnFeet` 贯通到这些诊断字段，并在 Unity worker 恢复 controller 时设置 `AnimatorState.iKOnFeet`。这一步只补证据和 controller 语义保留，不会把现有 IK 诊断输出升级成生产通过；下一次 Pelica/Endminf 复测必须先重建或刷新 controller inspect/context，再看脚部残差、Foot IK 状态和清晰截图是否共同改善。
- 复测 Pelica controller inspect 使用过滤后的单 inner bundle：`D:\Assets\fangzhou\Endfield_PelicaControllerInspect_StateIKOnFeet_Filtered_Current`，只暴露 `Data/Bundles/Windows/main/0a280e65d9d8c1f16f74fdc0.ab`，避免再次误扫全量 VFS。结果确认 `unity_file_inspect.json` 已写出 80 个 state 的 `iKOnFeet`，全部为 `false`；`unity_source_index.db` 中 178 条 `animatorController.clip` 关系里 119 条带 state 上下文，`details.stateIKOnFeet` 也全部为 `false`。随后用该 inspect 对 `Endfield_PelicaActor_IdleLoop_ReexportForFallback_Current` 跑 request-only `--recover_imported_animator_controllers`，输出 `imported_animator_controller_recovery_20260624_214629`：recovery request 80 个 state 都带 `iKOnFeet=false`，`clipCount=119`、`missingClipCount=0`。结论：Pelica run 当前脚部残差不应继续归因于漏传 AnimatorState Foot IK 开关；下一步应转向 `OnAnimatorIK` 采样时机、Unity IK 对 Humanoid transform 的实际写回、runtime IK 权重/effector、目标点定义和 additive/layer 组合。
- 2026-06-25 为避免旧坏样本被文件名误导，`UnityBakeResultApplier` 现在会在预览目录写 `ANIMATION_PREVIEW_STATUS.md`，并把同一份 `AnimeStudio.AnimationPreviewStatus.v1` 写入 glTF `asset.extras.animeStudioAnimationPreview`。这不是动画求解能力本身，但它把旧原神 bake 链路的“诚实终态”进一步前移到产物入口：`generated_single_state_controller`、缺原始 controller、IK 诊断、跳过/诊断采样 layer 等结果即使有 glTF TRS channel，也会以 `diagnosticOnly=true` 和 `blockedReasons` 明确阻断生产复用。
- 2026-06-25 继续把旧 Browser/原神 bake 的可信缓存经验同步到新版报告门禁：`AnimationPreviewCache`、`UnityBakeRequestGenerator` 和 `SQLiteLibraryIndexBuilder` 的 trusted bake 判断现在会读取 `unity_bake_apply_report.json.animationSolve`。只要 `writesReusableGltfTrsCandidate=false`、`productionReady=false` 或 `requiresVisualValidation=true`，即使报告有 `status=ok/warning`、Avatar/Clip 可信、帧间变化轨道存在，也不能统计为 trusted bake 或在旧 Browser 里显示成“可播放”。Pelica 诊断重放 `D:\Assets\fangzhou\Endfield_PelicaActor_IdleLoop_TrustedGate_Reapply_Current` 仍正确保持 `needs_review / diagnostic_recoverable_skipped_layer_sampling / diagnosticOnly=true`。
- 2026-06-25 继续补最终 IK 目标的骨骼语义诊断：`finalGoalSummaries[*]` 新增 `closestDescendant*` 与 `boneTargetCandidates`，用来判断 Hand/Foot goal 更像对齐 HumanBodyBone、手腕修正骨、手指、脚趾还是某个逐帧最近子节点。Pelica `run_loop` 验证目录 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_ClosestDescendantDiag_Current` 显示 glTF validator 无 error，`maxIkReadbackDistanceToGoal=0`、`maxLayerTargetSpread=0`，但最终手 goal 到 HumanHand 仍约 `0.143/0.145m`、脚 goal 到 HumanFoot 约 `0.181/0.191m`。手部逐帧最近子节点有时落在 Hand、Finger 或 corrective wrist，说明手部 goal 可能不是简单“骨骼原点必须完全贴合目标”；脚部则仍以 HumanFoot 为最近候选但残差较大。该结果排除了“IK 没写入”和“多层 goal 冲突”，但没有证明 goal 空间/权重/约束已生产化；报告继续保持 `diagnostic_limb_ik_goal_driver`、`writesReusableGltfTrsCandidate=false`。
- 2026-06-25 继续把 `boneTargetCandidates` 扩展为 local offset 诊断。Pelica run 的 HumanHand/HumanFoot 稳定候选并不是固定偏移：`localOffsetSpan` 约 `0.16m-0.20m`，`minLocalOffsetLength` 近似 `0`，`maxLocalOffsetLength` 接近最终残差。这个证据说明不能把生产修复写成“对 hand/foot 固定补一个端点 offset”；那会遮住动态 goal/controller/TDOF 语义问题。该字段只作为 solver 回归证据，不改变 `diagnostic_limb_ik_goal_driver` 结论。
- 2026-06-25 继续新增 `tdofVector3Summaries`，按 `TDOF` 三轴前缀记录曲线范围、长度范围和相对首帧最大变化。Pelica `run_loop` 复测 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_TdofSummaryDiag_Current` 显示 `SpineTDOF` / `ChestTDOF` 虽然存在，但全程为 0；glTF validator 无 error，apply report 仍为 `needs_review / diagnostic_limb_ik_goal_driver / writesReusableGltfTrsCandidate=false`。结论：该样本当前不能继续把 TDOF 当主因；下一步应集中验证 IK endpoint 定义、IK 权重曲线、controller runtime 参数和 additive/layer 语义。TDOF 摘要仍保留给后续其他角色/动作判断是否存在真正动态平移自由度。
- 2026-06-25 继续验证 IK 目标写入后骨骼是否真的被解算移动：`finalGoalSummaries[*]` 新增 pre/post 骨骼位置、pre 距离、最终骨骼移动量、距离改善量和回退量，CLI apply 报告同步汇总到 `maxFinalIkBoneMoveDistance` / `maxFinalIkDistanceImprovement` / `maxFinalIkDistanceRegression`。Pelica `run_loop` 复测 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_IkMovementDiag_Current` 显示 Unity 接收 IK target/weight，但最终 hand/foot 骨骼移动只有 `7.3e-7m` 量级、距离改善只有 `6.4e-7m` 量级，残差仍约 `0.145m` / `0.191m`。结论：当前缺口不是 readback、hint、固定 offset 或动态 TDOF，而是 batch/PlayableGraph 采样路径里 IK pass 没有实质改写骨架；下一步要追 `OnAnimatorIK` 时机、controller runtime IK 权重、layer/PlayableGraph 评估顺序和 Humanoid IK 约束。该结果只能作为 oracle/solver 诊断，不改变生产阻断。
- 2026-06-25 继续排除 IK 提交路径问题：临时把 recovered controller 的 IK 诊断从 `AnimationLayerMixerPlayable` 切到 `AnimatorControllerPlayable`，并让 recovered controller 使用恢复资产里的默认 state，Pelica `run_loop` 输出 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_ControllerPlayableIkDiag_Current` 不再出现 `State could not be found`，但手脚骨骼移动仍只有 `6.7e-7m` 量级；再临时追加 `Animator.Update(0)` 后 `OnAnimatorIK` 调用翻倍，骨骼仍不移动。结论：当前不是简单的 layer mixer 覆盖、state 播放字符串错误或 0 delta update 提交问题。默认逻辑撤回无效强制路径，只保留“recovered controller 非 mixer 路径使用默认 state、不强行 Play 字符串”的小修。下一步应转向 IK goal 曲线的真实权重/语义、controller 参数、BlendTree/IK pass 配置，或直接实现 hand/foot IK goal -> 目标骨架 TRS 的求解器。
- 2026-06-25 继续修正 IK 诊断采样顺序：`ApplyDirectTwoBoneIkDiagnostic` 原先在 `track.Record(...)` 前执行，会让诊断用 CCD 二骨求解污染最终 glTF TRS，也会让 `CapturePostEvaluateBones` 的“Unity IK 自己有没有移动骨骼”证据不够纯。现在顺序改为先 `CapturePostEvaluateBones`、先记录 Unity/PlayableGraph 原始 TRS，再执行二骨 CCD 只写诊断报告。后续 `maxFinalIkBoneMoveDistance` / `maxFinalIkDistanceImprovement` 应理解为 Unity/PlayableGraph 原始 IK 写回证据；`directTwoBoneIk*` 字段只说明同一目标在几何上是否可达，不能作为输出姿态或生产通过依据。
- 2026-06-25 对上述顺序修正做 Pelica run 复测，输出 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_PostCcdOrderFix_Current`。apply report 为 `needs_review`，`writtenTracks=21`、`frameVaryingTracks=21`、`coreBodyFrameVaryingTracks=20`、`avatarTrust.source=imported_unity_avatar_asset`、`animatorControllerSamplingMode=provided_runtime_controller`、`samplingState=Main_Run_RunLoop`；`maxDirectTwoBoneIkImprovement=8.19e-7`，说明诊断 CCD 基本没有改变 Unity/PlayableGraph 原始采样。Blender 截图显示没有复现 idle 样本的双手高举/手腕麻花，但该样本仍依赖 IK goal 诊断且缺少跨样本视觉验收，必须继续保持 `diagnostic_limb_ik_goal_driver` / `writesReusableGltfTrsCandidate=false`，不能计入生产 smoke。
- 2026-06-25 继续把旧 Browser/原神 bake 的“证据不能混淆”经验落到 IK pass 报告。Pelica run 复测 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_IkPassReport_Current` 显示原始 recovered controller 三层 `iKPass=false`，worker 虽然创建了 `ikPassEnabledLayerCount=3` 的 in-memory controller copy，但这次为了应用 skeleton mask 实际走 `AnimationLayerMixerPlayable`，因此新增 `animatorControllerIkPassEffectiveForSampling=false`、`animatorControllerIkPassSamplingMode=animation_layer_mixer_diagnostic_unproven` 和警告文本。结论：`OnAnimatorIK` target/readback 成功只能证明目标被写入，不证明 Unity Humanoid IK 已经改写最终骨骼；这解释了“IK readback 为 0 但 hand/foot 骨骼几乎不动”的矛盾。下一步应继续恢复不依赖 layer mixer 的 controller/mask/BlendTree 语义，或实现 hand/foot IK goal 到目标骨架 TRS 的直接求解器。
- 2026-06-25 继续推进不依赖 `AnimationLayerMixerPlayable` 的 controller/mask 语义。先在 `AnimeStudioAnimatorControllerRecovery` 尝试把 recovered layer skeleton mask 写成真实 `AnimatorControllerLayer.avatarMask`；Pelica controller recovery 复测 `D:\Assets\fangzhou\Endfield_PelicaControllerRecovery_AvatarMaskDiag_Current` 显示三层 controller 只有 hash-only skeleton mask，没有 transform path，结果 `createdAvatarMaskCount=0`、`skippedAvatarMaskPathCount=264`，因此恢复 controller asset 阶段不能硬猜 AvatarMask。随后在 bake worker 已有目标模型 transform lookup 时，把 hash-resolved skeleton mask 提升到 in-memory controller layer，并让 recovered controller 走 `AnimatorControllerPlayable`。Pelica run 复测 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_RuntimeMaskPromotion_Current` 显示 `animatorControllerLayerMasksApplied=true`、`animatorControllerIkPassEffectiveForSampling=true`、`animatorControllerIkPassSamplingMode=animator_controller_playable`，`changedTrackCount` 从上一轮 21 增到 104，证明这条路径能保留 controller layer mask 且不再依赖外部 mixer。apply report 仍为 `needs_review / diagnostic_limb_ik_goal_driver / writesReusableGltfTrsCandidate=false`：`maxIkReadbackDistanceToGoal=0`，但 `maxFinalIkBoneMoveDistance≈6.7e-7m`、`maxFinalIkDistanceImprovement≈6.7e-7m`，说明即使改成 controller playable，Unity IK target 仍几乎没有实质改写 hand/foot 骨骼。Blender 截图没有复现 idle 的双手举脸/手腕麻花，但该样本仍只能作为 oracle 诊断；后续不要继续把主因归结为 layer mixer 覆盖，应转向 IK goal 权重/effector/endpoint 语义、controller runtime 参数，或直接实现 hand/foot goal 到目标骨架 TRS 的求解器。
- 2026-06-25 继续把 controller runtime 参数缺口显式写进报告。Pelica run 复测 `D:\Assets\fangzhou\Endfield_PelicaActor_RunLoop_RuntimeParamSummary_Current` 新增 `animatorControllerRuntimeParameterSummary`，统计到 recovered controller 共 91 个参数，其中 49 个参数名疑似 runtime weight / layer / IK / look-at / speed/time gate，16 个疑似 layer/additive weight，6 个疑似 IK/cloth IK gate，12 个疑似 look-at gate，8 个默认值非零。关键线索是 `LoopBodyAddLayer=0`、`LoopBodyAddWeight=0`、`LoopClothAddLayer=0`、`LoopClothAddWeight=0`，而当前采样中的 recovered layer weight 仍为 1；另有 `ClothIK_SWeight=1`、`C_ClothIKLegL/R=0`、`LookatWeight=0` 等参数提示游戏运行时可能通过参数门控附加层、cloth/IK 或 look-at。该 summary 只按参数名做诊断提示，不参与采样权重，也不能作为生产关系；它把下一步阻塞收敛到“恢复 AnimatorController 参数绑定和运行时参数值来源，或在直接求解器里等价处理这些门控语义”。
- 2026-06-25 复查旧原神 Browser 双击 bake 后，继续把“报告门禁优先于能 bake”迁到 Endfield：`unityBakeAnimatorControllerRuntimeParameterSummary.layerParameterHints` 会按 layer 名称和 controller 参数名的结构相似性，列出疑似 runtime layer/IK/additive weight gate，例如 `LoopBodyAddLayer -> LoopBodyAddWeight`。若命中默认值为 0 的 layer weight 候选，apply report 会在 `animationSolve.reusableCandidateBlockedReasons` 写 `animator_controller_runtime_layer_weight_gate_unresolved`。这仍只是诊断证据，不会自动把参数默认值套进采样；只有找到确定性 AnimatorController 参数绑定或运行时参数来源，并通过清晰视觉验收后，才能改变生产状态。
- 2026-06-25 继续复用旧 bake 的“证据先行”经验：`unity_file_inspect.json` 和源索引的 `animatorController.clip` 关系现在会输出 `stateTransitions` / `conditions`，把 `ConditionConstant.m_EventID` 解析成 TOS 参数名、条件模式和阈值。它用于确认 `LoopBodyAddWeight`、`LoopClothAddWeight`、IK/LookAt 等参数是否真的被 AnimatorController transition 或 selector 引用。该字段仍是诊断证据，不会直接改 layer weight、BlendTree 参数或采样结果；如果只有 controller 默认值或层名相似性，没有 transition/blend/运行时参数绑定证据，结果必须继续标为 `animator_controller_runtime_layer_weight_gate_unresolved` 或待验证。
- 2026-06-25 用 Pelica controller 单包复测 transition 条件，输出 `D:\Assets\fangzhou\Endfield_PelicaControllerInspect_TransitionConditions_Current`。正确命令必须对 `.blc` 传 `--endfield_vfs_files "Data/Bundles/Windows/main/0a280e65d9d8c1f16f74fdc0\.ab$"`，否则会展开同 VFS 目录下大量内容并生成数 GB 临时源索引。复测结果：`unity_file_inspect.json` 中 80 个 state、241 条 transition condition；条件参数主要是 `npcAnimatorStateECode`、montage slot、`SG_TimeRef`、step/turn/root motion 等状态切换参数。`LoopBodyAddWeight`、`LoopClothAddWeight`、`LoopBodyAddLayer`、`LoopClothAddLayer`、`IdleTailIndex`、`ClothIK_SWeight`、`LookatWeight` 在 transition condition 中均为 0 次引用。结论：Pelica 当前 layer/cloth/IK/look-at 权重缺口不应继续归因于 state transition 条件未解析；下一步应集中查 BlendTree/direct blend 参数、controller runtime 参数来源、additive layer 权重和 IK endpoint/effector 语义。
- 2026-06-25 复查旧原神 / Browser 双击 Unity bake 链路后，继续取其“controller 上下文证据优先”的经验，而不是复用旧 Unity4/浏览器临时 bake 外壳。`AnimatorController` 解析现在会保留 Endfield 的 `BlendDirectData.m_ChildPoseTimeEventIDArray`、`m_UsePoseTimeValues` 和 `BlendSequenceData`，`unity_file_inspect.json` 也会输出 direct/sequence child blend event 与 pose-time event 名称。Pelica 单包复测输出 `D:\Assets\fangzhou\Endfield_PelicaControllerInspect_BlendEvents_Current`：`LoopBodyAddWeight`、`LoopClothAddWeight`、`LoopBodyAddLayer`、`LoopClothAddLayer` 在 transition 与 direct/sequence BlendTree 参数中仍为 0 次引用，说明这些 layer weight 线索更可能来自 runtime 参数或外部脚本；但 `IdleTailIndex` 在 `LoopClothAddLayer.LoopClothAddIdle` 的 `blendEvent` 中出现 1 次，`ClothIK_SWeight` 在 `ClothCombine.SequenceNode` 的 `sequenceChildBlendEvents` 中出现 1 次。这证明部分 cloth/tail/IK 语义存在 Unity 原始 controller 结构证据，后续可以据此恢复 BlendTree/sequence 参数上下文；但这些字段目前只作为诊断证据，不能直接改变 layer 权重或解除生产门禁。
- 同轮继续把这些证据写入 `unity_source_index.db`，新增 `source_relations.relation='animatorController.blendTreeParameter'`。它记录 state/layer/tree/node、BlendTree 参数引用、direct/sequence child blend event、pose-time event 和可选 clip PPtr，但 `to_path_id=0`，表示这是 controller 结构诊断关系，不是模型-动画绑定关系。Pelica 过滤后复测目录 `D:\Assets\fangzhou\Endfield_PelicaControllerInspect_BlendParamRelations_Filtered_Current`：`animatorController.blendTreeParameter=22`、`animatorController.clip=178`，能从 SQLite 直接查到 `LoopClothAddLayer.LoopClothAddIdle -> IdleTailIndex` 和 `ClothCombine.SequenceNode -> ClothIK_SWeight / lookat* / micExp* / cloth weight` 等参数。`0xFFFFFFFF` 已过滤为无参数哨兵，剩余命中只来自 layer body mask bitmask。结论：source index 现在不再只保留叶子 clip 关系，也能保留非 clip BlendTree/Sequence 参数节点；下一步应继续查这些参数的 runtime 默认值、脚本/MonoBehaviour 来源或直接求解语义，不能用参数名相似性自动改采样权重。

### 6.3 中期实现

- 完成 ACL scalar/transform 解压。
- 建立 `AnimatorMuscle` binding 到 `HumanTrait.MuscleName`、TDOF、RootT、RootQ 的映射。
- 补齐 limb IK goal / hand-foot goal 的直接求解或 UnityOracle 对照策略。只还原 muscle 和 root/body，不足以证明 Endfield 人形 idle、walk、run、attack 等动作语义正确；只把 IK goal T/Q 直接丢进 `SetIKPosition/Rotation` 也不够，必须恢复 goal 空间、权重曲线和 controller IK pass 语义后再视觉验收。
- 实现内部 Humanoid solver 第一版，或验证 `UnityBakeAccelerated` 已能稳定承担同类 Humanoid/Muscle 求解。
- 用 UnityOracle 自动生成误差报告。

### 6.4 长期策略

- 默认 `Library` 和 SQLite 推荐只认最终 glTF TRS/weights、确定性关系、可信 Avatar 来源和视觉验收，不强制只认 `ProductionDirect`。
- UI 可以在 worker 实验通过、误差报告稳定后接入 `UnityBakeAccelerated` 结果，但必须显示求解来源、Unity 版本、Avatar、耗时和验收状态。
- 若某游戏只能靠旧式游戏专用 Unity 工程或低吞吐人工 bake 才能动，标 `UnityDerived` / `notProductionReady`；若能通过 `UnityBakeAccelerated` 自动高吞吐输出 glTF TRS，并通过验收，可以进入生产求解路径。

## 7. 决策建议

不建议完全放弃“不允许 Unity bake”规范。

更好的升级是：

1. 默认生产主线继续禁止依赖 Unity bake。
2. 明确允许 `UnityOracle`，专门为内部 solver 和视觉验收服务。
3. 新增 `UnityBakeAccelerated` 显式 profile，给需要快速批量可播放结果的用户使用。
4. 所有 Unity bake 结果都必须带来源、Unity 版本、Avatar、clip、采样率、controller context、是否 direct 缺失等元数据。
5. 性能优化优先级固定为：internal avatar pose 批处理 > 持久 worker 分组缓存 > 多 worker 分片 > PlayableGraph 完整 bake。
