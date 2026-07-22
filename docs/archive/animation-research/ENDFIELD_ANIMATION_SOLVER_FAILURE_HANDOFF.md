# Endfield 动画求解任务失败交接记录

记录时间：2026-06-25

本轮任务结论：失败收尾。当前 AnimeStudio 对 Arknights Endfield 的模型、材质、贴图、Avatar 恢复、AnimatorController 诊断和 UnityBakeAccelerated 诊断链路已经推进到可复现阶段，但人形动画求解还没有达到“可生产复用”的标准。尤其是 Pelica `idle_loop`、`run_loop` 等样本仍不能证明动作语义正确，不能计入生产 smoke。

## 1. 目标与验收规则

本轮目标不是生成一个能动的 glTF，而是恢复游戏内真实可用资源：

- 第一阶段：模型可用。Mesh、UV、材质、贴图、skin、骨骼、bbox 必须先通过。
- 第二阶段：动画可用。动画必须通过确定性 Unity 关系找到，并输出可脱离 Unity 播放的 glTF TRS / weights。
- Unity bake / Unity native Humanoid 只允许作为自动化求解器或 oracle，最终产物仍必须是 glTF TRS。
- `matchedTracks`、`frameVaryingTracks`、glTF 可打开、Avatar 有效，都不能单独证明动画正确。
- 人形动画必须有清晰 rest/start/mid/end 或手脚近景视觉证据；肉眼可见扭曲必须判失败。

## 2. 已确认有效的部分

### Endfield 输入与源索引

- Endfield VFS / `.blc` 两层资源读取已能用于定向 inspect。
- 定向 inspect 某个 `.blc` 时必须使用 `--endfield_vfs_files` 限制内部 UnityFS 文件。例如 Pelica controller 小样本使用：

```powershell
dotnet run --project .\AnimeStudio.CLI\AnimeStudio.CLI.csproj -f net9.0-windows -- `
  "C:\Program Files\Hypergryph Launcher\games\Arknights Endfield\Endfield_Data\StreamingAssets\VFS\7064D8E2\7064D8E2.blc" `
  "D:\Assets\fangzhou\Endfield_PelicaControllerInspect_BlendParamRelations_Filtered_Current" `
  --game ArknightsEndfield `
  --inspect_unity_files `
  --profile_3d All `
  --batch_files 1 `
  --endfield_vfs_files "Data/Bundles/Windows/main/0a280e65d9d8c1f16f74fdc0\.ab$"
```

如果只给 `.blc` 而不限制内部 bundle，会展开大量 VFS 内容，容易生成数 GB 临时索引。

### Pelica 模型样本

`P_actor_pelica_01` 是当前较好的真实角色回归样本：

- 模型材质、贴图、skin 相对完整。
- 比 `P_npc_npc_chr_0003_endminf` 这类白模/剧情实例更适合作为主线样本。
- 但 Pelica 动画仍未生产通过。

已确认的确定性关系链：

```text
data_facemorph_avatar_pelica
-> P_actor_pelica_01
-> SK_actor_pelica_01Avatar
-> P_npc_npc_chr_0004_pelica
-> AC_npc_human_girl_pelica_optNew
-> A_actor_pelica_idle_loop
```

这条关系来自 Unity PPtr / Avatar / AnimatorController，不是名称猜测。但它跨了同 Avatar 的另一个运行时实例，所以必须保留 `sharedAvatarBridgeNeedsVisualValidation`，视觉通过前不能升级为默认生产推荐。

### Avatar / Clip / Controller 恢复

已实现或已验证的方向：

- ImportedAvatar 可以从 Unity 原始 Avatar 资产恢复。
- Unity probe 能验证 `Avatar.isValid && Avatar.isHuman`。
- ImportedAnimationClip 可以恢复到 Unity bake 工程。
- ImportedAnimatorController 可以从 `unity_file_inspect.json` 恢复基本 layer/state/clip/BlendTree 结构。
- `animation_bake_cache` 和 `unity_bake_apply_report.json.animationSolve` 已能表达 `needs_review`、`diagnosticOnly`、`writesReusableGltfTrsCandidate=false` 等状态，避免旧 Browser 把诊断 glTF 当成可播放。

这些是旧原神 / Browser Unity bake 链路值得保留的精华：强证据链、可信 Avatar/Clip/Controller 映射、终态缓存和诚实门禁。

## 3. 失败点

### Pelica `idle_loop` 视觉失败

用户提供并复核过的失败表现：

- 文件名是 `idle_loop`，但双手异常抬高。
- 手腕扭曲，像麻花。
- 动作语义明显不符合 idle。

该类结果即使 glTF validator 无 error、有 TRS channel、有 frame-varying tracks，也必须判失败。

### 单 clip 或生成 controller 不等价原始运行时

旧的 generated single-state / generated multi-layer controller 可以证明管线能跑通，但不能证明动作正确。Pelica 的失败说明：

- 单个 `AnimationClip` 可能只是状态机上下文中的片段。
- 原始 AnimatorController 的 layer、mask、BlendTree、默认参数、运行时参数、IK pass、additive reference pose 都可能影响最终姿态。
- 没有原始 RuntimeAnimatorController 上下文或等价 solver 时，Humanoid/Muscle 结果必须标为诊断。

### IK goal / TDOF 仍未生产化

Endfield 人形 clip 的主体动画不只是普通 Transform TRS：

- 主体 Humanoid/Muscle 曲线在 ACL scalar / AnimatorMuscle / TDOF / RootT / RootQ 等数据里。
- `m_ValueArrayDelta` 只能作为 binding/value layout 证据，不能硬插值成关键帧。
- Pelica 中发现 limb goal、TDOF、controller IK/cloth/look-at 参数等复杂语义。

诊断结果显示：

- 打开 editor-curve IK goal driver 后，部分坏姿态可以改善。
- 但 IK goal driver 仍是诊断路径，不是生产 solver。
- 有些复测中 IK target/readback 成功，但最终 hand/foot 骨骼几乎不移动，说明 Unity `OnAnimatorIK` 采样路径、IK pass、layer/mask/PlayableGraph 顺序或 endpoint 语义仍未完全恢复。

### Controller runtime 参数来源不足

已解析到 Pelica controller 参数默认值，例如：

- `IdleTailIndex = 0`
- `ClothIK_SWeight = 1`
- `ClothBase_SWeight = 1`
- `LoopBodyAddWeight = 0`
- `LoopClothAddWeight = 0`
- `LoopBodyAddLayer = 0`
- `LoopClothAddLayer = 0`

这些默认值来自 `AnimatorController.m_Values/m_DefaultValues`，是确定性 controller 数据。但它们不是游戏运行时脚本改写后的值，不能直接拿来解除生产门禁。

已查过 transition condition：

- `LoopBodyAddWeight`、`LoopClothAddWeight`、`IdleTailIndex`、`ClothIK_SWeight` 等没有出现在 transition condition 中。
- 问题更可能在 BlendTree/direct/sequence 参数、runtime 参数源、additive layer 权重或 IK/cloth/look-at 求解语义。

## 4. 本轮参考旧原神/Browser bake 的结论

可以继续借鉴：

- 只允许 Unity 显式关系候选进入 bake。
- ImportedAvatar 必须 fresh probe 且 `isValid/isHuman`。
- ImportedAnimationClip / ImportedAnimatorController 必须精确映射。
- `static_pose`、`needs_review`、`needs_animator_controller_context` 这些失败/待审终态也要写入缓存，避免 UI 一直重复生成或误显示成功。
- glTF 里写入 `asset.extras.animeStudioAnimationPreview`，让产物本身携带诊断状态。
- SQLite / Browser / batch report 都必须读 `animationSolve`，不能只看老字段。

不能继续借鉴：

- Unity4 或游戏专用 Unity 工程。
- 用户手动新建 Unity 项目、安装 helper、导入一堆临时 Avatar/Clip/Prefab。
- 浏览器双击时临时烘焙才得到可用动画。
- “能 bake 出 glTF”就显示为可播放。

## 5. 本轮代码状态与安全边界

本轮收尾前只保留了低风险诊断改动：

- `AnimeStudio.CLI/Program.cs`
  - `--inspect_unity_files` 的 `controllerValueDefaults.values[]` 增加 `typeName`、`resolvedDefaultKind`、`resolvedDefaultValue`、`resolvedDefaultSource`。
  - 只解析明确的 Unity AnimatorController 参数类型：`Float=1`、`Int=3`、`Bool=4`、`Trigger=9`。
  - 规则文本明确：这是 controller 序列化默认值，不是运行时参数。
- `AnimeStudio.CLI/AnimatorControllerAssetRecoveryExporter.cs`
  - recovery 优先读取 `resolvedDefaultKind=float/resolvedDefaultValue`。
  - 旧的 `defaultCandidates.float` 仍作为兼容 fallback。
- `AnimeStudio.UnityBake/Assets/AnimeStudio.UnityBake/Editor/AnimeStudioAnimatorControllerRecovery.cs`
  - 报告统计同时接受 `controllerResolvedDefaultValue` 和旧 `controllerDefaultValue`。

这些改动只影响 AnimatorController inspect/recovery 的诊断和恢复默认参数，不会改变默认模型导出、普通 Unity 游戏导出、VRising/Freedunk 等直接 TRS 动画逻辑，也不会自动把 Endfield 动画标成生产通过。

## 6. 后续重启建议

建议下次不要从“再跑一次 Pelica bake”开始，而是按下面顺序推进：

1. 先固定一组回归样本：
   - `P_actor_pelica_01 + idle_loop`
   - `P_actor_pelica_01 + run_loop`
   - 至少再找 2-3 个真实角色 prefab，不要用 Dialog/UI/Timeline/postmodel/白模样本。
2. 对每个样本先跑模型门禁，模型失败直接换样本。
3. 对 controller 继续补结构证据：
   - BlendTree direct/sequence 参数的默认值和当前选择。
   - layer default weight、runtime layer weight 参数来源。
   - additive reference pose。
   - IK pass 是否真的作用到最终骨架。
4. 如果继续依赖 UnityOracle：
   - 优先恢复原始 RuntimeAnimatorController 语义，尽量避免 `AnimationLayerMixerPlayable` 绕过 BlendTree。
   - 所有 Unity 派生输出仍保持 `needs_review`，直到跨样本截图通过。
5. 如果做直接 solver：
   - 先用 UnityOracle 对照 muscle/root/body 曲线误差。
   - 再单独处理 Hand/Foot IK goal、TDOF、RootT/RootQ。
   - 不允许固定 offset 或反号试错掩盖问题。
6. 成功标准仍是至少 10 个不同“模型 + 动画”组合，包含 idle、walk/run、attack、jump、skill 等动作，并有清晰截图。

## 7. 当前失败结论

Endfield 人形动画支持没有完成。当前产物最多是诊断链路和失败回归样本，不能声明“大多数模型动画可直接转换/求解”。工具应继续保持保守门禁：模型第一阶段不过不进动画；动画视觉失败不进 production；Unity bake 诊断结果不自动升级为可复用 glTF TRS。
