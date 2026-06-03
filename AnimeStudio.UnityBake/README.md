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

## 当前边界

如果 request 里没有 Unity prefab/clip 路径，helper 会从 AnimeStudio glTF 重建骨架、从 Avatar humanBones 构建 Human Avatar，并把 `.anim` 导入 Unity 工程。这个 fallback 只用于动画采样，不等于完整 Unity prefab 复原。
