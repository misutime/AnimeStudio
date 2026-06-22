# Humanoid/Muscle 动画与 Unity Bake 规范评估

本文重新评估当前规范：

> 生产级动画导出不依赖外部 Unity bake，必须由 AnimeStudio 直接从 Unity 序列化数据、ACL、AnimationClip、Avatar/HumanDescription 等确定性来源写出 glTF TRS/weights。

结论：

- 这条规范的方向仍然正确。它保护的是“可用素材库”的自足性、可追溯性和批量稳定性，不是单纯排斥 Unity。
- 如果严格遵守规范，动画还原仍有路线：Generic/Transform/BlendShape 直接写 glTF；ACL transform 直接写 glTF；ACL scalar / Humanoid muscle 进入 AnimeStudio 内部 solver；AnimatorController 上下文不足的 clip 先标注，不硬说可播放。
- 如果放宽规范，Unity bake 必须重新定义为 `UnityBakeAccelerated`：一个全新的实验 helper worker，只负责调用 Unity 6 native Humanoid/Muscle 求解器，不继承完整 prefab + PlayableGraph + Transform 逐帧读回的重链路。
- `UnityBakeAccelerated` 需要一个通用 Unity 宿主来调用 Unity native 层。这个宿主应是干净的 `AnimeStudioUnityBakeWorker`，作为本机工具环境提前创建、长期复用，不绑定任何单一游戏项目。
- `UnityBakeAccelerated` 只允许基于当前统一支持的 Unity 6 worker。不能为了原神或其他旧游戏回退依赖 Unity 2017.4.40f1 等旧 Editor；如果 Unity 6 无法加载或求解某游戏的 Avatar/Clip，宁可标为不支持 UnityDerived 批量 bake，也不要引入旧 Unity 版本矩阵。
- 当前阶段只尝试设计和验证实验 helper worker，不接入任何 Unity 素材主流程，不写入默认 Library / SQLite / `model_animations.json`，也不作为生产动画支持证明。
- 建议把规范从“禁止 Unity bake”升级成三层：`ProductionDirect`、`UnityOracle`、`UnityBakeAccelerated`。默认素材库只信 `ProductionDirect`；显式 profile 可生成 Unity 派生动画，但必须标明来源和不可复现风险。

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

### 6.3 中期实现

- 完成 ACL scalar/transform 解压。
- 建立 `AnimatorMuscle` binding 到 `HumanTrait.MuscleName`、TDOF、RootT、RootQ 的映射。
- 实现内部 Humanoid solver 第一版。
- 用 UnityOracle 自动生成误差报告。

### 6.4 长期策略

- 默认 `Library` 和 SQLite 推荐只认 `ProductionDirect`。
- UI 暂不接入 `UnityBakeAccelerated` 结果；等 worker 实验通过、误差报告稳定后，再讨论单独实验视图。
- 若某游戏只能靠 Unity bake 才能动，标 `UnityDerived` / `notProductionDirect`，不要宣称生产级直接还原。

## 7. 决策建议

不建议完全放弃“不允许 Unity bake”规范。

更好的升级是：

1. 默认生产主线继续禁止依赖 Unity bake。
2. 明确允许 `UnityOracle`，专门为内部 solver 和视觉验收服务。
3. 新增 `UnityBakeAccelerated` 显式 profile，给需要快速批量可播放结果的用户使用。
4. 所有 Unity bake 结果都必须带来源、Unity 版本、Avatar、clip、采样率、controller context、是否 direct 缺失等元数据。
5. 性能优化优先级固定为：internal avatar pose 批处理 > 持久 worker 分组缓存 > 多 worker 分片 > PlayableGraph 完整 bake。
