# AnimeStudio CLI 3D 导出使用说明

本文档说明如何用 `AnimeStudio.CLI` 导出 Unity 游戏的 3D 资源，并以 Freedunk 为例给出可直接运行的命令。

## 基本原则

3D 资源导出分成两类：

- 模型导出：导出角色、NPC、球、场景、道具等静态或带骨架的模型。
- 动画导出：导出带 Animator/动画控制器关系的角色动画。

不要把模型导出和动画导出混在同一次全量任务里。模型导出默认会排除 `animations` 路径，避免把动画目录里的绑定对象、临时对象或重复 prefab 当成模型扫出来；动画导出则会保留 `animations` 路径。

## 当前默认过滤

在 3D 模式下，CLI 会默认过滤掉跟 3D 模型/动画导出关系不大的目录：

- `ui`
- `sound`
- `audio`
- `video`
- `emoji`
- `camera`

在 `SplitObjects` 模型模式下，还会额外过滤：

- `animations`

在 `Animator` 动画模式下，不会过滤 `animations`。

此外，3D 模式还会默认按对象名排除明显的相机/helper 对象：

- `camera`
- `maincam`
- `handycam`
- `uicam`

如果某个游戏确实把有效模型放在这些目录或命名里，可以用更具体的输入路径、`--containers`、`--names` 或后续自定义过滤策略覆盖。

## 自动 map

通常不需要手动传 `--map_name`。

如果没有指定 `--map_name`，CLI 会根据：

- `--game`
- 输入根目录

自动生成稳定的 map 名，例如：

```text
auto_Normal_7d5f5ad9af87
```

同一个游戏、同一个输入目录会复用同一个 map；换游戏或换输入目录会使用不同 map，避免错误复用旧的 `assets_map.bin`。

只有在你明确想共享或固定某个 map 时才需要手动传：

```powershell
--map_name freedunk_stage05
```

## Freedunk 示例路径

游戏目录：

```text
C:\Program Files (x86)\Freedunk\Game
```

Unity 数据目录：

```text
C:\Program Files (x86)\Freedunk\Game\Freedunk_Data
```

资源包主要目录：

```text
C:\Program Files (x86)\Freedunk\Game\Freedunk_Data\StreamingAssets\assets
```

Freedunk 的 `.ab` 文件前面有 1 字节前导标记，AnimeStudio 已经兼容这种 `01 + UnityFS` 格式。

## 全量模型导出

用于导出 Freedunk 的角色、NPC、球、场景、道具等模型资源：

```powershell
cd D:\misutime\AnimeStudio

AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files (x86)\Freedunk\Game\Freedunk_Data" `
  "D:\misutime\FreedunkExport\Freedunk_Data_models" `
  --game Normal `
  --mode SplitObjects `
  --model_roots_only `
  --group_assets ByContainer `
  --fbx_animation Skip
```

说明：

- `--mode SplitObjects`：按 GameObject 拆分导出模型。
- `--model_roots_only`：尽量只导出模型根节点，减少子 mesh 零件重复导出。
- `--group_assets ByContainer`：按资源容器路径组织导出目录。
- `--fbx_animation Skip`：模型导出不附带动画。

## 全量动画导出

用于导出角色 Animator 相关资源：

```powershell
cd D:\misutime\AnimeStudio

AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files (x86)\Freedunk\Game\Freedunk_Data" `
  "D:\misutime\FreedunkExport\Freedunk_Data_animators" `
  --game Normal `
  --mode Animator `
  --group_assets ByContainer `
  --fbx_animation Auto
```

说明：

- `--mode Animator`：按 Animator 导出，适合角色动画。
- `--fbx_animation Auto`：尝试从 Animator/Controller 关系自动收集动画。
- 该模式不会默认排除 `animations` 目录。

如果希望把当前过滤范围内所有 AnimationClip 都喂给 FBX，可以改用：

```powershell
--fbx_animation All
```

但 `All` 可能会把不相关动画混入同一个导出范围，建议先小范围测试。

## 小范围模型导出

全量导出会比较慢，也会产生很多模型。实际分析时建议先按目录导出。

导出 stage05 球场：

```powershell
cd D:\misutime\AnimeStudio

AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files (x86)\Freedunk\Game\Freedunk_Data\StreamingAssets\assets\graphics\stage\stage05" `
  "D:\misutime\FreedunkExport\stage05" `
  --game Normal `
  --mode SplitObjects `
  --model_roots_only `
  --group_assets ByContainer `
  --fbx_animation Skip
```

导出所有球场：

```powershell
cd D:\misutime\AnimeStudio

AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files (x86)\Freedunk\Game\Freedunk_Data\StreamingAssets\assets\graphics\stage" `
  "D:\misutime\FreedunkExport\stages" `
  --game Normal `
  --mode SplitObjects `
  --model_roots_only `
  --group_assets ByContainer `
  --fbx_animation Skip
```

导出球模型：

```powershell
cd D:\misutime\AnimeStudio

AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files (x86)\Freedunk\Game\Freedunk_Data\StreamingAssets\assets\graphics\ball" `
  "D:\misutime\FreedunkExport\balls" `
  --game Normal `
  --mode SplitObjects `
  --model_roots_only `
  --group_assets ByContainer `
  --fbx_animation Skip
```

导出玩家角色模型：

```powershell
cd D:\misutime\AnimeStudio

AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files (x86)\Freedunk\Game\Freedunk_Data\StreamingAssets\assets\graphics\character\pc" `
  "D:\misutime\FreedunkExport\characters_pc" `
  --game Normal `
  --mode SplitObjects `
  --model_roots_only `
  --group_assets ByContainer `
  --fbx_animation Skip
```

导出 NPC 模型：

```powershell
cd D:\misutime\AnimeStudio

AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files (x86)\Freedunk\Game\Freedunk_Data\StreamingAssets\assets\graphics\character\npc" `
  "D:\misutime\FreedunkExport\characters_npc" `
  --game Normal `
  --mode SplitObjects `
  --model_roots_only `
  --group_assets ByContainer `
  --fbx_animation Skip
```

## 手动过滤

只包含某些容器路径：

```powershell
--containers "graphics[\\/]stage"
```

排除某些容器路径：

```powershell
--containers_exclude "ui|sound|video|emoji"
```

只导出某些对象名：

```powershell
--names "^BeachBall_Blue$"
```

排除某些对象名：

```powershell
--names_exclude "camera|maincam|handycam|uicam"
```

这些参数都支持传正则，也支持传一个文本文件路径。文本文件中每行一个正则，空行会被跳过。

## 常用参数速查

```text
--game Normal
```

普通 Unity 游戏使用 `Normal`。特殊加密/定制游戏需要选择对应 game。

```text
--mode SplitObjects
```

按 GameObject 导出模型，适合场景、球、道具、角色/NPC prefab。

```text
--mode Animator
```

按 Animator 导出，适合角色动画。

```text
--model_roots_only
```

只导出模型根节点，减少重复子对象。

```text
--group_assets ByContainer
```

按容器路径组织导出目录，便于回溯资源来源。

```text
--fbx_animation Skip
```

不导动画，适合模型导出。

```text
--fbx_animation Auto
```

自动收集动画，适合 Animator 模式。

```text
--fbx_animation All
```

把当前过滤范围内所有 AnimationClip 都传给 FBX，适合小范围测试。

```text
--fbx_scale_factor 1
--fbx_bone_size 10
```

覆盖 FBX 缩放和骨骼显示尺寸。

## 建议流程

1. 先用小目录测试，例如 `graphics\stage\stage05` 或 `graphics\ball`。
2. 确认 FBX、贴图、材质能正常导出。
3. 再扩大到 `graphics\stage`、`graphics\character\pc`、`graphics\character\npc`。
4. 最后再考虑 `Freedunk_Data` 全量模型导出。
5. 动画单独用 `--mode Animator --fbx_animation Auto` 跑。

全量导出可能消耗较多内存和时间。Freedunk 的 `Freedunk_Data` 全量扫描约有数千个资源包/对象，建议优先按资源目录分批导出。
