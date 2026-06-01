# AnimeStudio CLI 原神资源导出说明

本文记录当前改造版 AnimeStudio CLI 的使用方式，目标是低内存导出原神资源，优先导出贴图和 FBX 模型。

## 构建产物

Debug 版 CLI 入口：

```powershell
D:\misutime\AnimeStudio\AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe
```

运行命令时建议直接使用完整路径，不依赖 `$exe` 变量。

## 资源目录

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

贴图、模型、材质、UI、场景等大多在 `StreamingAssets\AssetBundles\blocks`。如果需要更完整结果，再额外导出 `Persistent\AssetBundles\blocks`。

## 按类型导出贴图

```powershell
& "D:\misutime\AnimeStudio\AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe" `
  --game GI `
  --types Texture2D:Both Sprite:Both SpriteAtlas:Parse Material:Both AssetBundle:Parse ResourceManager:Parse `
  --export_type Convert `
  --group_assets ByLibrary `
  "C:\Program Files\miHoYo Launcher\games\Genshin Impact Game\YuanShen_Data\StreamingAssets\AssetBundles\blocks" `
  "D:\tmp\AnimeStudio_GI_Library"
```

输出结构类似：

```text
D:\tmp\AnimeStudio_GI_Library\
  Textures\
  Materials\
```

`Convert` 会把贴图转换为 PNG，材质会导出 JSON。`ByLibrary` 会优先使用 container 路径；如果没有可恢复的原始路径，就按资源名前缀分组，例如 `Area`、`Level`、`UI`。

## 导出 FBX 模型并带材质

当前 CLI 配置已开启：

```xml
<add key="exportMaterials" value="True" />
```

配置文件位置：

```text
D:\misutime\AnimeStudio\AnimeStudio.CLI\App.config
```

导出命令：

```powershell
& "D:\misutime\AnimeStudio\AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe" `
  --game GI `
  --model_roots_only `
  --types GameObject:Both Transform:Parse MeshFilter:Parse MeshRenderer:Parse SkinnedMeshRenderer:Parse Mesh:Parse Material:Parse Texture2D:Parse `
  --export_type Convert `
  --group_assets ByLibrary `
  "C:\Program Files\miHoYo Launcher\games\Genshin Impact Game\YuanShen_Data\StreamingAssets\AssetBundles\blocks" `
  "D:\tmp\AnimeStudio_GI_Library"
```

说明：

- `GameObject:Both` 是 FBX 模型导出的核心类型。
- 单独 `Mesh:Both` 会导出 OBJ，不是 FBX。
- `Material:Parse` 和 `Texture2D:Parse` 用于让模型导出器解析材质和贴图依赖。
- `Transform`、`MeshFilter`、`MeshRenderer`、`SkinnedMeshRenderer` 用于恢复静态模型层级、网格和材质槽。
- 当前静态导出配置关闭了 `exportSkins`、`exportAnimations`、`collectAnimations`，不会导出动画，也不会主动解析 Animator/Avatar。
- FBX 会建立材质和贴图引用，但不是 GLB 那种把所有贴图内嵌进单文件。
- `--model_roots_only` 只导出 AssetBundle/ResourceManager 容器里明确记录的主 GameObject。没有可解析主入口的资源包会跳过 GameObject 模型，避免把 Bark、Leaf、ShadowMesh、子 LOD 等依赖部件误当成完整模型。
- `--model_roots_only` 还会跳过明显不是素材主体的根对象，例如 `Cs_` 过场模型、`_Convert`、碰撞体、`ShadowMesh`。
- 模型导出会保留所有 LOD。比如同时存在 `Foo_Lod0`、`Foo_Lod1`、`Foo_Lod2` 时会全部导出，方便后续按使用场景选择。
- 模型导出会自动加载或构建 `Maps\assets_map.bin` 作为 CAB 依赖图，用于解析跨 bundle 的材质和贴图。首次运行会较慢，之后会复用该文件。
- 如果某个 FBX 模型完全解析不到材质，CLI 会跳过该模型并打印 warning，避免导出只有 mesh、没有正确材质的错误结果。

## 一个命令导出模型、贴图、材质库

```powershell
& "D:\misutime\AnimeStudio\AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe" `
  --game GI `
  --model_roots_only `
  --types GameObject:Both Texture2D:Both Sprite:Both Material:Both Transform:Parse MeshFilter:Parse MeshRenderer:Parse SkinnedMeshRenderer:Parse Mesh:Parse SpriteAtlas:Parse AssetBundle:Parse ResourceManager:Parse `
  --export_type Convert `
  --group_assets ByLibrary `
  "C:\Program Files\miHoYo Launcher\games\Genshin Impact Game\YuanShen_Data\StreamingAssets\AssetBundles\blocks" `
  "D:\tmp\AnimeStudio_GI_Library"
```

输出结构：

```text
D:\tmp\AnimeStudio_GI_Library\
  Models\
  Textures\
  Materials\
```

这是目前推荐命令。它只导出贴图、材质 JSON、静态 FBX 模型；不导出 `AnimationClip`、音频、视频，也不主动解析 Animator/Avatar。模型依赖贴图会统一写入顶层 `Textures\_ModelDependencies`，每个模型目录里也会放同名 PNG 硬链接；FBX 内直接记录同目录贴图文件名，方便在资源管理器和 Blender 里检查，同时不重复占用一份贴图空间。每个模型依赖的材质 JSON 会和该模型 FBX 放在同一目录，所有独立材质也会进入顶层 `Materials` 资源库。

## Codex 模型

`Models\Codex` 需要和普通素材模型区别对待。

这类资源通常不是单独的原始模型，而是图鉴/展示界面用的组合 GameObject。例如 `Codex_Animal_Fishable_Maritime_Macropinna_02` 里同时包含鱼本体、重复实例、`EffectMesh` 片状特效网格，以及 `Sr_Codex_Platform_Water` 展示水台。FBX 材质连接本身可能是正确的，但模型内容不是“纯鱼单体”。

后续使用时：

- `Codex` 可以保留为展示组合参考。
- 需要纯净可复用模型时，优先查找非 `Codex` 路径下的单体资源，例如 `Animal`、`Monster`、`NPC`、`Area`、`Level` 等分类。
- 如果在 Blender 中看到中间有纸片、平台、水面、展示底座等额外物体，先检查 Mesh 名称；常见来源是 `EffectMesh` 或 `Sr_Codex_Platform_*`，不一定是材质错误。

## 材质和 Shader 边界

当前导出目标是“Blender 可打开、基础材质可见”，不是完整还原原神自定义 shader。

CLI 会把常见贴图槽尽量映射到 FBX 标准材质：

- `_MainTex`、包含 `Diffuse` / `Albedo` / `BaseMap` / `BaseColor` 的贴图会挂到 FBX `Diffuse`。
- 包含 `Normal` 的贴图会挂到 FBX `NormalMap`。
- `_BumpMap` 会挂到 FBX `Bump`。
- 包含 `Specular` 的贴图会挂到 FBX `Specular`。
- 包含 `Emission` / `Emissive` 的贴图会挂到 FBX `Emissive`。
- 包含 `Reflect` 的贴图会挂到 FBX `Reflection`。

原神地形、岩石、角色等材质常包含自定义 shader 槽，例如 `Mask`、`DetailDiffuse`、`DetailNormal`、`Tint`、`Ramp`、`Smoothness`、`Blend` 等。FBX 标准材质不能完整表达这些混合逻辑，所以这些贴图和参数会被保留下来，但不保证在 FBX 里自动还原游戏内效果。

后续如果要接近游戏效果，应基于同目录的材质 JSON 和 PNG，在 Blender 中重建节点材质。

## Shader 提取

当前工具保留了 AnimeStudio 原有的 Shader 导出能力，可以把 Unity `Shader` 资源导出为 `.shader` 文本，用于后续分析材质属性、Pass、Keywords、贴图槽命名等。

示例命令：

```powershell
& "D:\misutime\AnimeStudio\AnimeStudio.CLI\bin\Debug\net9.0-windows\AnimeStudio.CLI.exe" `
  --game GI `
  --types Shader:Both `
  --export_type Convert `
  --group_assets ByLibrary `
  "C:\Program Files\miHoYo Launcher\games\Genshin Impact Game\YuanShen_Data\StreamingAssets\AssetBundles\blocks" `
  "D:\tmp\AnimeStudio_GI_Shaders"
```

注意：

- Shader 导出是研究资料，不等于 Blender 可以直接使用。
- 原神 Shader 可能包含 Unity serialized shader、平台编译 shader、自定义属性和关键词组合，部分 Shader 可能解析失败。
- 后续重建 Blender 材质时，应结合 `.shader`、模型目录内的材质 JSON、贴图 PNG 一起分析。

## 关于原始目录结构

`--group_assets ByType` 按类型分组，最稳定。

`--group_assets ByContainer` 会尝试按资源 container/path 分组，但原神 v6.0 当前没有可用的公开 asset index。没有 asset index 时，无法完整还原类似 AEP 的：

```text
Area\
UI\
Avatar\
Eff\
Monster\
NPC\
```

CLI 已支持：

```powershell
--ai_file <asset_index.json>
--ai_version <version>
```

但当前内置来源 `14eyes/gi-asset-indexes` 只到 `3.5.0`，没有 `6.0`。因此 v6.0 暂时不建议期待完整原始目录还原。

`--group_assets ByLibrary` 是当前项目优化后的折中方案：能恢复 container 时按 container；不能恢复时按资源名前缀分类。它不会假装还原不存在的 v6.0 asset index，但输出会比纯 `ByType` 更接近素材库结构。

## 注意事项

- 不建议用 GUI 直接打开完整 `YuanShen_Data`，会触发全量大小估算和高内存加载。
- CLI 现在按文件逐个加载、导出、清理，适合 16GB 内存环境。
- `Unknown ClassIDType` 警告通常可先忽略，只要目标资源能导出即可。
- FBX 依赖 `AnimeStudio.FBXNative.dll`，当前 Debug 输出目录下已有 `x64` 和 `x86` native DLL。
- `Convert` 会解码贴图/模型材质贴图，内存压力高于 `Raw`。
