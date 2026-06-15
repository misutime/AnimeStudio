# AnimeStudio Unity Bake Helper

这个目录是一组 Unity Editor helper 脚本，用来处理 AnimeStudio 不能可靠手写求解的 Humanoid / Muscle 动画。

核心规则：

- AnimeStudio 负责解析 Unity 打包资源、导出模型/贴图/骨骼、生成 Unity 关系索引和 bake request。
- Unity Editor 负责用 `Animator`、`Avatar`、`AnimationClipPlayable` 和 `AnimationMode.SamplePlayableGraph` 采样动画。
- 输出 `unity_bake_result.json`，里面是目标模型骨架每帧的 local TRS。后续 AnimeStudio 再把这些 TRS 合进 glTF/GLB 预览或动画包。

## 安装

把 `Assets/AnimeStudio.UnityBake` 复制到一个 Unity 工程的 `Assets` 目录下。

## 运行

先用 AnimeStudio CLI 生成请求：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --generate_unity_bake_request "D:\Assets\Freedunk_Data_Dev\AnimationBindingV1Sample\model_animations.json" `
  --preview_model "Bill_01_00_ingame" `
  --preview_animation "NORMALMOVE_STAND_01" `
  --preview_output "D:\Assets\Freedunk_Data_Dev\UnityBake_Bill_NormalMove" `
  --unity_project "D:\Assets\Freedunk_Data_Dev\UnityBakeProject" `
  --unity_model_prefab "Assets/AnimeStudioBake/Input/Bill_01_00_ingame.prefab" `
  --unity_animation_clip "Assets/AnimeStudioBake/Input/NORMALMOVE_STAND_01.anim"
```

如果已经准备好 Unity 工程、prefab 和 clip，可以直接追加：

```powershell
  --run_unity_bake `
  --unity_editor "C:\Program Files\Unity\Hub\Editor\<version>\Editor\Unity.exe"
```

Unity batchmode 会调用：

```text
AnimeStudio.UnityBake.AnimeStudioBakeCli.Run
```

## 验证导入的 Avatar asset

原神这类库从打包对象恢复出的 `UnityEngine.Avatar` asset 放入 `Assets/AnimeStudioBake/ImportedAvatar` 后，可以先跑批量探针确认 Unity 真的能把它们加载成有效 Humanoid Avatar：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Test-UnityImportedAvatarAssets.ps1 `
  -UnityProject "D:\misutime\AnimeStudioUnityProject" `
  -UnityEditor "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe" `
  -OutputDir "D:\Assets\AS-Assets\YuanShen-Assets\ImportedAvatarProbe"
```

这个探针只检查 `Assets/AnimeStudioBake/ImportedAvatar/*.asset` 是否能通过 `AssetDatabase.LoadAssetAtPath<Avatar>` 加载，且 `avatar.isValid && avatar.isHuman`。它不会新增模型-动画关系，也不会触发动画 bake。

## 当前边界

如果 request 里没有 Unity prefab/clip 路径，helper 会从 AnimeStudio glTF 重建骨架，并把 `.anim` 导入 Unity 工程。request 可以通过 `unityAssetPaths.avatarAsset` 指向一个已经导入 bake 工程的原始 `UnityEngine.Avatar` asset；这类 Avatar 来自 Unity 打包对象恢复，属于可验收的 Unity oracle。只要 request 显式带了 `unityAssetPaths.avatarAsset`，这个 Avatar 就是强约束：路径缺失、加载失败或不是有效 Humanoid 时直接写入失败结果，不能静默退回 `BuildHumanAvatar`。

没有显式 Avatar asset 时，helper 才允许使用原始 prefab 的 `Animator.avatar`，或用完整 `HumanDescription.humanBones + skeletonBones` 构建 Avatar。旧库里只有 `AvatarConstant/internalSolver` 或 glTF rest pose 的路径只能作为诊断/恢复输入，不能当成可信生产 bake。

helper v3 会在 `unity_bake_result.json` 里同时记录 `requestedAnimationClip`、`importedAnimationClip` 和 `animationClipSource`。这用于区分 request 直接引用的 Unity `AnimationClip` asset，和 AnimeStudio 复制到 `Assets/AnimeStudioBake/Imported` 后由 Unity 导入的 `.anim` sidecar；排查 Ambor 这类 AnimatorController 辅助 clip 时，必须能确认真正进入 PlayableGraph 的 clip 是控制器上下文修正后的完整身体 clip，而不是原始局部辅助 clip。

Humanoid/Muscle 的生产验收必须使用原始 Unity prefab 的 `Animator.avatar`、导出索引里完整保留的 `HumanDescription.humanBones` + `HumanDescription.skeletonBones`，或从打包 Unity 对象恢复出的原始 `UnityEngine.Avatar` asset。只有 `AvatarConstant/internalSolver` 或 glTF rest pose 时，输出必须标记为诊断结果，不能算作可信动画。
