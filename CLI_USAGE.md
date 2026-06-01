# AnimeStudio CLI 通用资源导出说明

本文档说明如何使用 `AnimeStudio.CLI` 导出 Unity 游戏资源。目标是让普通 Unity 游戏和原神等特殊游戏使用同一套工作流，只在 `--game`、资源路径、asset index 等必要位置区分。

## CLI 入口

Debug 版 CLI：

```powershell
D:\misutime\AnimeStudio\AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe
```

建议先进入仓库目录再运行：

```powershell
cd D:\misutime\AnimeStudio
```

然后用相对入口：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe
```

## 核心概念

### game

普通 Unity 游戏使用：

```powershell
--game Normal
```

原神使用：

```powershell
--game GI
```

其他加密或定制 Unity 游戏需要选择对应的 `--game`。可以通过 `--help` 查看当前支持列表。

### 自动 map

通常不需要手动传 `--map_name`。

不传时，CLI 会根据：

- `--game`
- 输入根目录

自动生成稳定 map 名，例如：

```text
auto_Normal_7d5f5ad9af87
auto_GI_3a1b2c4d5e6f
```

同一个游戏和同一个输入目录会复用同一个 map；换游戏或换输入目录会使用不同 map，避免错误复用旧依赖图。

如果需要兼容旧流程或固定共享 map，可以手动指定：

```powershell
--map_name assets_map
```

### group assets

常用分组方式：

```powershell
--group_assets ByContainer
```

按资源容器路径组织输出，适合普通 Unity 游戏。

```powershell
--group_assets ByLibrary
```

输出为素材库结构，适合原神这类目录恢复不完整但需要模型、贴图、材质分库管理的场景。

## 3D 导出模式

3D 导出分成两类：

- 模型导出：角色、NPC、球、场景、道具等模型。
- 动画导出：Animator/AnimationClip 相关动画。

不要把模型导出和动画导出混在同一次全量任务里。

### 模型模式

```powershell
--mode SplitObjects
--fbx_animation Skip
```

模型模式按 GameObject 导出模型。当前默认模型格式是 glTF：

```powershell
--model_format Gltf
```

可选：

```powershell
--model_format Gltf
--model_format Glb
--model_format Fbx
```

全量资源提取建议使用默认 `Gltf`。它会输出 `.gltf + .bin`，比 FBX 更适合批量导出、恢复导出和后处理。FBX 只建议在需要兼容旧流程或特定 DCC 工具时使用。

模型依赖贴图默认使用 Raw 模式：

```powershell
--texture_mode Raw
```

贴图模式：

- `--texture_mode Raw`：默认值。导出 Unity 原始/压缩贴图数据为 `.rawtex`，旁边写 `.rawtex.json` 记录宽高、Unity TextureFormat、mip 数、源 asset/pathId、Unity 版本和平台；glTF 材质 `extras.unityTextures` 保留贴图引用。速度最快，适合全量提取和后续批量转换。
- `--texture_mode Png`：解码并导出 `.png`，glTF 会直接引用 PNG。PNG 会先写入顶层共享目录 `Textures\_ModelDependencies`，再在模型目录的 `Textures` 子目录创建硬链接；这样模型目录便于单独查看，同时重复贴图不额外占用磁盘空间。最兼容 Blender/预览器，但全量角色会明显变慢。
- `--texture_mode Reference`：只在 glTF 材质 `extras.unityTextures` 记录引用，不写贴图数据。最快，适合先扫模型结构。

### 贴图工作流和技术逻辑

当前推荐流程是先看模型，再按需处理贴图：

1. 全量模型导出默认使用 `--texture_mode Raw`。
2. 先用 `.gltf/.glb` 浏览模型结构、命名、骨骼、网格和材质槽。
3. 看到值得保留的模型后，直接对该模型的 `.gltf` 执行 `--convert_model_textures`。
4. CLI 会读取 glTF 材质里的 `extras.unityTextures`，到顶层 `Textures\_ModelDependencies` 找对应 `.rawtex/.rawtex.json`，只转换这个模型实际用到的贴图。

这样做的原因：

- Unity 贴图常见格式是 DXT/BC/ETC/ASTC/Crunched 等 GPU 压缩或 Unity 原始数据。
- 全量导出时把这些贴图全部解码成 PNG 会非常慢；角色模型尤其明显，很多耗时都集中在 normal/baseColor/mask 等贴图解码上。
- Raw 模式只复制原始贴图数据，不做完整解码，因此模型导出速度和内存稳定性更好。
- `.rawtex` 不是 glTF 标准图片格式，所以 glTF 不会把它写进标准 `images/textures` 通道；否则很多查看器会报错或误读。
- Raw/Reference 模式会把贴图关系写入材质 `extras.unityTextures`，这是给后处理工具和人工排查用的保留信息。
- `--texture_mode Png` 仍然保留，用于小范围导出、需要 Blender/查看器直接显示贴图、或确认材质效果的场景。

Raw 模式输出文件：

```text
Textures\_ModelDependencies\xxx.rawtex
Textures\_ModelDependencies\xxx.rawtex.json
```

`.rawtex.json` 会记录：

- `format`：Unity `TextureFormat`，例如 `DXT1Crunched`、`DXT5`、`BC7`、`ASTC_6x6`。
- `width` / `height`：贴图尺寸。
- `mipCount`：mip 数量。
- `sourceAssetPath` / `sourceFileName` / `sourcePathId`：原始 Unity 资源来源，方便追踪重复贴图和问题贴图。
- `unityVersion` / `platform`：后处理解码需要的 Unity 版本和平台信息。
- `rawDataSize`：`.rawtex` 原始字节长度。
- `dataFile`：对应 `.rawtex` 文件名。

选中模型后单独转贴图：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  --convert_model_textures "D:\Assets\Freedunk_Data_gltf\assets\ingame\prefabs\characters\Qiqi_03_00\Qiqi_03_00.gltf"
```

默认行为：

- 自动从 glTF 路径向上查找导出根目录，直到发现 `Textures\_ModelDependencies`。
- 输出到模型目录旁边的 `Textures` 子目录。
- 默认转成 PNG。
- 默认修改该 `.gltf` 的标准 `images/textures` 引用，让 Blender/预览器能直接加载贴图。

可选参数：

```powershell
--texture_asset_root "D:\Assets\Freedunk_Data_gltf"
--texture_output "D:\Assets\Freedunk_Data_gltf\assets\ingame\prefabs\characters\Qiqi_03_00\Textures"
--texture_output_format Png
--update_gltf_texture_refs false
```

如果只想测试转换、不想改原 glTF，可以加：

```powershell
--update_gltf_texture_refs false
```

模型模式默认会过滤：

- `ui`
- `sound`
- `audio`
- `video`
- `emoji`
- `camera`
- `animations`

`animations` 在模型模式下默认排除，避免导出动画目录里的绑定对象、临时对象和重复 prefab。

### 动画模式

```powershell
--mode Animator
--fbx_animation Auto
```

动画模式按 Animator 导出。默认过滤：

- `ui`
- `sound`
- `audio`
- `video`
- `emoji`
- `camera`

动画模式不会过滤 `animations`。

如果希望把当前过滤范围里的全部 AnimationClip 都传给 FBX：

```powershell
--fbx_animation All
```

`All` 容易混入不相关动画，建议只在小目录测试。

### 全量性能参数

3D 模式默认启用受控批处理：

```powershell
--batch_files 4
```

`--batch_files` 表示每批加载多少个源资源文件及其依赖。数值越大，重复依赖加载越少，但内存峰值越高；数值越小，内存更稳但总耗时可能增加。大游戏全量导出优先保持默认值，内存充足时再调高。

3D 模式还会按模型数量定期做一次完整 GC：

```powershell
--model_gc_interval 32
```

64GB 内存机器建议保持默认值或调大到 `64`，优先提高吞吐；如果观察到内存持续上升不回落，再调小到 `16`。传 `0` 可以关闭模型级完整 GC，只保留批次结束时的清理。

导出过程会写入：

```text
export_manifest.jsonl
```

每成功导出一个模型追加一行 JSON，便于统计进度、排查中断位置和后续做恢复导出。

默认还会写入性能日志：

```text
export_profile.jsonl
```

它记录批次加载、资产数据构建、模型转换、`model_mesh`、`model_skin`、`model_material`、`model_texture`、`model_texture_raw`、模型写出和 GC 的耗时及内存快照。觉得全量导出慢时，保留这个文件就能直接分析瓶颈。关闭日志：

```powershell
--profile_log off
```

指定日志路径：

```powershell
--profile_log "D:\Assets\profile.jsonl"
```

## 通用 3D 模型导出命令

适合普通 Unity 游戏：

```powershell
cd D:\misutime\AnimeStudio

AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "<Unity_Data目录或资源目录>" `
  "<输出目录>" `
  --game Normal `
  --mode SplitObjects `
  --model_roots_only `
  --group_assets ByContainer `
  --model_format Gltf `
  --texture_mode Raw `
  --fbx_animation Skip
```

说明：

- `--mode SplitObjects`：按 GameObject 拆分导出模型。
- `--model_roots_only`：尽量只导出模型根节点，减少子 mesh 零件重复导出。
- `--group_assets ByContainer`：按资源容器路径组织输出。
- `--model_format Gltf`：导出 `.gltf + .bin`；这是默认值，可以省略。
- `--texture_mode Raw`：导出原始贴图数据，避免 PNG 解码拖慢全量模型导出；这是默认值，可以省略。
- `--fbx_animation Skip`：模型导出不附带动画。参数名仍沿用旧名，控制模型导出时是否收集动画数据。

## 通用动画导出命令

```powershell
cd D:\misutime\AnimeStudio

AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "<Unity_Data目录或资源目录>" `
  "<输出目录>" `
  --game Normal `
  --mode Animator `
  --group_assets ByContainer `
  --model_format Gltf `
  --fbx_animation Auto
```

## Freedunk 示例

游戏目录：

```text
C:\Program Files (x86)\Freedunk\Game
```

Unity 数据目录：

```text
C:\Program Files (x86)\Freedunk\Game\Freedunk_Data
```

主要资源目录：

```text
C:\Program Files (x86)\Freedunk\Game\Freedunk_Data\StreamingAssets\assets
```

Freedunk 的 `.ab` 文件带 1 字节前导标记，AnimeStudio 已兼容这种 `01 + UnityFS` 格式。

### Freedunk 全量模型

```powershell
cd D:\misutime\AnimeStudio

AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files (x86)\Freedunk\Game\Freedunk_Data" `
  "D:\Assets\Freedunk_Data_models" `
  --game Normal `
  --mode SplitObjects `
  --model_roots_only `
  --group_assets ByContainer `
  --model_format Gltf `
  --texture_mode Raw `
  --fbx_animation Skip
```

全量会产生大量模型。默认 glTF + Raw 贴图 + 批处理会优先保证可持续导出和较低内存峰值；如果机器内存充足，可以增加 `--batch_files`。需要 Blender 直接显示贴图时，再改用 `--texture_mode Png` 或后续单独批量转贴图。

### Freedunk 全量动画

```powershell
cd D:\misutime\AnimeStudio

AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files (x86)\Freedunk\Game\Freedunk_Data" `
  "D:\Assets\Freedunk_Data_animators" `
  --game Normal `
  --mode Animator `
  --group_assets ByContainer `
  --model_format Gltf `
  --fbx_animation Auto
```

### Freedunk 分目录模型

导出球场：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files (x86)\Freedunk\Game\Freedunk_Data\StreamingAssets\assets\graphics\stage" `
  "D:\Assets\stages" `
  --game Normal `
  --mode SplitObjects `
  --model_roots_only `
  --group_assets ByContainer `
  --fbx_animation Skip
```

导出球：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files (x86)\Freedunk\Game\Freedunk_Data\StreamingAssets\assets\graphics\ball" `
  "D:\Assets\balls" `
  --game Normal `
  --mode SplitObjects `
  --model_roots_only `
  --group_assets ByContainer `
  --fbx_animation Skip
```

导出玩家角色：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files (x86)\Freedunk\Game\Freedunk_Data\StreamingAssets\assets\graphics\character\pc" `
  "D:\Assets\characters_pc" `
  --game Normal `
  --mode SplitObjects `
  --model_roots_only `
  --group_assets ByContainer `
  --fbx_animation Skip
```

导出 NPC：

```powershell
AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files (x86)\Freedunk\Game\Freedunk_Data\StreamingAssets\assets\graphics\character\npc" `
  "D:\Assets\characters_npc" `
  --game Normal `
  --mode SplitObjects `
  --model_roots_only `
  --group_assets ByContainer `
  --fbx_animation Skip
```

## 原神示例

原神资源目录示例：

```text
C:\Program Files\miHoYo Launcher\games\Genshin Impact Game\YuanShen_Data
```

主要资源包：

```text
C:\Program Files\miHoYo Launcher\games\Genshin Impact Game\YuanShen_Data\StreamingAssets\AssetBundles\blocks
```

补丁/缓存资源包：

```text
C:\Program Files\miHoYo Launcher\games\Genshin Impact Game\YuanShen_Data\Persistent\AssetBundles\blocks
```

如果需要更完整结果，可以分别导出 `StreamingAssets` 和 `Persistent`。

### 原神贴图/材质

```powershell
cd D:\misutime\AnimeStudio

AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files\miHoYo Launcher\games\Genshin Impact Game\YuanShen_Data\StreamingAssets\AssetBundles\blocks" `
  "D:\tmp\AnimeStudio_GI_Library" `
  --game GI `
  --types Texture2D:Both Sprite:Both SpriteAtlas:Parse Material:Both AssetBundle:Parse ResourceManager:Parse `
  --export_type Convert `
  --group_assets ByLibrary
```

### 原神静态 FBX 模型

这是兼容旧原神导出流程的命令：

```powershell
cd D:\misutime\AnimeStudio

AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files\miHoYo Launcher\games\Genshin Impact Game\YuanShen_Data\StreamingAssets\AssetBundles\blocks" `
  "D:\tmp\AnimeStudio_GI_Library" `
  --game GI `
  --model_roots_only `
  --types GameObject:Both Transform:Parse MeshFilter:Parse MeshRenderer:Parse SkinnedMeshRenderer:Parse Mesh:Parse Material:Parse Texture2D:Parse `
  --export_type Convert `
  --group_assets ByLibrary `
  --fbx_animation Skip
```

说明：

- `GameObject:Both` 是 FBX 模型导出的核心类型。
- 单独 `Mesh:Both` 会导出 OBJ，不是 FBX。
- `Material:Parse` 和 `Texture2D:Parse` 用于解析材质和贴图依赖。
- `Transform`、`MeshFilter`、`MeshRenderer`、`SkinnedMeshRenderer` 用于恢复层级、网格和材质槽。
- `--model_roots_only` 只导出 AssetBundle/ResourceManager 容器里明确记录的主 GameObject。
- `--fbx_animation Skip` 保持静态模型导出，不主动收集动画。

如果需要完全复用旧的 `Maps\assets_map.bin`，额外添加：

```powershell
--map_name assets_map
```

### 原神模型、贴图、材质库

```powershell
cd D:\misutime\AnimeStudio

AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files\miHoYo Launcher\games\Genshin Impact Game\YuanShen_Data\StreamingAssets\AssetBundles\blocks" `
  "D:\tmp\AnimeStudio_GI_Library" `
  --game GI `
  --model_roots_only `
  --types GameObject:Both Texture2D:Both Sprite:Both Material:Both Transform:Parse MeshFilter:Parse MeshRenderer:Parse SkinnedMeshRenderer:Parse Mesh:Parse SpriteAtlas:Parse AssetBundle:Parse ResourceManager:Parse `
  --export_type Convert `
  --group_assets ByLibrary `
  --fbx_animation Skip
```

输出结构大致为：

```text
D:\tmp\AnimeStudio_GI_Library\
  Models\
  Textures\
  Materials\
```

模型依赖贴图会统一写入顶层 `Textures\_ModelDependencies`。默认 `--texture_mode Raw` 会写 `.rawtex` 和 `.rawtex.json`；如果使用 `--texture_mode Png`，则会写 PNG。模型依赖的材质 JSON 会和模型放在同一目录，独立材质会进入顶层 `Materials`。

`--texture_mode Png` 下，glTF 会优先引用模型目录旁边 `Textures` 子目录里的 PNG；这些 PNG 是从顶层 `Textures\_ModelDependencies` 创建的硬链接。硬链接失败时才会退回普通复制。

### 原神 Shader

```powershell
cd D:\misutime\AnimeStudio

AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Program Files\miHoYo Launcher\games\Genshin Impact Game\YuanShen_Data\StreamingAssets\AssetBundles\blocks" `
  "D:\tmp\AnimeStudio_GI_Shaders" `
  --game GI `
  --types Shader:Both `
  --export_type Convert `
  --group_assets ByLibrary
```

Shader 导出是研究资料，不等于 Blender 可以直接使用。后续重建 Blender 材质时，应结合 `.shader`、材质 JSON、贴图文件一起分析。

## 原神目录恢复和 asset index

`--group_assets ByContainer` 会尝试按资源 container/path 分组。原神在没有对应版本 asset index 时，无法完整还原原始目录。

CLI 支持：

```powershell
--ai_file <asset_index.json>
--ai_version <version>
```

如果没有可用的当前版本 asset index，建议使用：

```powershell
--group_assets ByLibrary
```

`ByLibrary` 能恢复 container 时按 container；不能恢复时按资源名前缀分类。它不会假装还原不存在的目录，但输出会比纯 `ByType` 更接近素材库结构。

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

这些参数支持正则，也支持传文本文件路径。文本文件中每行一个正则，空行会被跳过。

## FBX 参数

```powershell
--fbx_animation Skip
```

不导动画，适合模型导出。

```powershell
--fbx_animation Auto
```

自动收集动画，适合 Animator 模式。

```powershell
--fbx_animation All
```

把当前过滤范围内所有 AnimationClip 都传给 FBX，适合小范围测试。

```powershell
--fbx_scale_factor 1
--fbx_bone_size 10
```

覆盖 FBX 缩放和骨骼显示尺寸。

## 材质和 Shader 边界

CLI 的 FBX 导出目标是“Blender 可打开、基础材质可见”，不是完整还原游戏自定义 shader。

常见贴图槽会尽量映射到 FBX 标准材质：

- `_MainTex`、`Diffuse`、`Albedo`、`BaseMap`、`BaseColor` 会挂到 Diffuse。
- `Normal` 会挂到 NormalMap。
- `_BumpMap` 会挂到 Bump。
- `Specular` 会挂到 Specular。
- `Emission`、`Emissive` 会挂到 Emissive。
- `Reflect` 会挂到 Reflection。

很多游戏包含自定义 shader 槽，例如 `Mask`、`DetailDiffuse`、`DetailNormal`、`Tint`、`Ramp`、`Smoothness`、`Blend` 等。FBX 标准材质不能完整表达这些混合逻辑，所以这些贴图和参数会保留在材质 JSON 和贴图文件中，但不保证在 FBX 里自动还原游戏内效果。

## 建议流程

1. 先用小目录测试。
2. 全量或大目录模型导出优先使用默认 `--texture_mode Raw`，先快速浏览模型。
3. 看到喜欢或需要保留的模型后，再单独转换该模型依赖的贴图。
4. 如果需要查看器直接显示贴图，小范围重跑 `--texture_mode Png`。
5. 再扩大到角色、场景、道具等目录。
6. 最后再考虑完整 `*_Data` 全量导出。
7. 动画单独用 `--mode Animator --fbx_animation Auto` 跑。

全量导出会消耗较多内存和时间。CLI 会按文件逐个加载、导出、清理，但跨 bundle 依赖仍可能拉起大量资源，建议优先分目录导出。

## 注意事项

- 不建议用 GUI 直接打开大型完整 `*_Data` 目录。
- `Unknown ClassIDType` 警告通常可以先忽略，只要目标资源能导出即可。
- FBX 依赖 `AnimeStudio.FBXNative.dll`。
- `Convert` 会解码贴图/模型材质贴图，内存压力高于 `Raw`。
- 如果只想保持旧原神导出行为，可以继续使用 `--types ... --group_assets ByLibrary`，并按需添加 `--map_name assets_map`。
