# AnimeStudio Library Browser 模型动画预览设计

本文记录 `AnimeStudio.LibraryBrowser` 关于模型、动画候选和动画预览的实现方向，方便后续迭代时保持边界清楚。

## 目标

Library Browser 的目标是快速浏览 AnimeStudio 导出的可用素材库：

- 快速看模型缩略图。
- 选中模型后看到可靠的动画候选。
- 像 Mixamo 一样按需查看“模型 + 单个动画”的效果。
- 成功预览后缓存结果，避免重复生成。

默认不把所有动画提前写进所有模型，也不批量生成全部动画预览。VRising 这类库有几千动画，全量预览会产生巨大耗时和大量中间文件。

## 数据分层

### 第一层：显式 Unity 关系

优先使用 Unity 原生引用：

- `GameObject / Animator -> AnimatorController`
- `Animation -> AnimationClip`
- `AnimatorController / AnimatorOverrideController -> AnimationClip`

这类关系可信度最高，列表中标为 `显式`。

大库即使跳过结构型全矩阵匹配，也必须保留这类显式关系。

### 第二层：选中模型后的定向匹配

对没有显式关系的模型，Browser 在用户选中模型后才做定向匹配：

- 使用模型的 `bonePaths` / `nodePaths`。
- 查询动画的 `AnimationClip transformBindingPaths`。
- 命中后标为 `可匹配` / `需验证`。

这一步只说明结构路径可能匹配，不代表视觉一定正确。必须通过 preview glTF 和 `preview_validation.json` 验证。

### 第三层：结构候选补充

后续可继续增强：

- Avatar / HumanDescription 兼容。
- 可见 mesh / skinned joint 覆盖。
- 武器、道具、挂点、动作语义一致。
- 表情、BlendShape、Legacy、材质/激活类动画分流。

第三层不能退化成无约束的模型乘动画全矩阵。

## SQLite 与 JSON/JSONL 分工

桌面工具高频查询默认走 SQLite：

- 模型列表。
- 动画 binding path 定向匹配。
- 缩略图状态。
- 筛选统计。
- 预览缓存状态。

JSON/JSONL 保留用于：

- 人工排查。
- 可追溯原始数据。
- 兼容旧流程。
- 重新建库。

默认 Library 导出和 `--rebuild_library_indexes` 都应生成 `library_index.db`。`--build_sqlite_index` 是“不重导素材，只刷新 SQLite 工具索引”的手动入口，常用于 CLI 索引逻辑更新后补齐 `model_animation_candidates`。

示例：

```powershell
dotnet run --project D:\misutime\AnimeStudio\AnimeStudio.CLI\AnimeStudio.CLI.csproj -f net9.0-windows -- `
  --build_sqlite_index "D:\Assets\AS-Assets\VRising-Assets"
```

运行前需要关闭正在打开该素材库的 Library Browser，避免 `library_index.db` 被占用。

## Unity Bake 配置

Library Browser 不要求用户通过环境变量配置 Unity 路径。动画 bake 使用下面的配置优先级：

1. 素材库本地配置：`<LibraryRoot>\.as_browser_cache\unity_bake_settings.json`。
2. Library Browser 全局配置：`%APPDATA%\AnimeStudio\LibraryBrowser\settings.json`。
3. Unity Hub 默认安装目录自动检测，例如 `C:\Program Files\Unity\Hub\Editor\<version>\Editor\Unity.exe`。
4. 环境变量兜底：`ANIMESTUDIO_UNITY_BAKE_PROJECT`、`ANIMESTUDIO_UNITY_EDITOR`。

素材库本地配置只用于某个 Library 需要特殊 Unity 工程或 Editor 版本时覆盖全局设置。正常情况下团队成员应优先使用 Library Browser 的全局配置，避免把个人机器路径写进素材库目录。

全局配置示例：

```json
{
  "unityProject": "D:\\Assets\\AnimeStudioUnityBakeProject",
  "unityEditor": "C:\\Program Files\\Unity\\Hub\\Editor\\6000.4.5f1"
}
```

`unityEditor` 可以写 Unity 版本目录，也可以直接写 `Unity.exe`；Browser 会自动规范成 `...\Editor\Unity.exe`。顶部工具栏的 `Unity设置` 按钮会同时修改全局 `unityEditor` 和 `unityProject`，不会改环境变量，也不会改素材库本地配置。

注意：`Unity Editor` 和 `UnityBakeProject` 是两件事。前者是 `Unity.exe`，后者是用于运行 AnimeStudio.UnityBake helper 的 Unity 工程目录。只配置 `Unity.exe` 仍然无法执行烘焙。

### Unity Bake Worker

Humanoid/Muscle 动画需要 Unity `Animator`/`Avatar` 采样，冷启动 Unity 会非常慢。LibraryBrowser 默认使用常驻 Unity Bake Worker：

- CLI 参数：`--unity_bake_worker_queue <queue-dir>`
- Browser 默认队列：`<LibraryRoot>\.as_browser_cache\unity_bake_worker`
- 第一次烘焙会启动 Unity worker，后续同一素材库的 Humanoid 烘焙会复用已启动的 Unity 进程。
- Worker 通过 `worker_heartbeat.json` 表示存活，通过 `*.request.json` / `*.done.json` / `*.error.json` 文件队列处理任务。
- 采样结果仍然写 `unity_bake_result.json`，再由 AnimeStudio CLI 合成 baked glTF；模型、贴图、骨骼、动画数据路径不变。
- 顶部工具栏提供 `Unity Worker` 下拉菜单：
  - `状态`：读取当前素材库队列的 `worker_heartbeat.json`，显示运行、未启动、未响应等状态。
  - `启动`：按当前有效 Unity 配置启动该素材库的常驻 worker。
  - `重启`：先停止当前队列 worker，再重新启动。
  - `停止`：优先写入 `worker_stop.request` 让 worker 自己退出；如果旧版 worker 不认识停止信号，会兜底关闭命令行同时包含 `AnimeStudioBakeWorker.Run` 和当前队列路径的 Unity batchmode 进程。

这个 worker 只优化调度速度，不改变动画还原算法。若需要完全关闭 worker，可结束对应的 Unity batchmode 进程；下次预览会自动重新启动。

## 动画预览流程

预览采用按需生成，并优先走快速路径：

1. 用户选中模型。
2. Browser 显示显式动画候选或定向匹配候选。
3. 用户双击动画。
4. Browser 调用 `AnimeStudio.CLI --generate_preview_from_library <LibraryRoot>`。
5. CLI 从 `library_index.db` 的 `assets` 表读取选中模型和动画的 `raw_json`。
6. CLI 在内存里构造单模型单动画预览选择，不写临时索引 JSON。
7. CLI 先尝试快速路径：复制已导出的模型 glTF 目录，读取动画旁 `*.animation_asset.json` 的 `decoded` TRS 曲线，转换到 glTF 坐标基后写入 glTF animation。
8. 快速路径成功时写出 `preview_validation.json`，Browser 记录 `preview_state.json`，并用 f3d 打开 glTF。
9. 快速路径失败时，才回退到旧慢路径：重新导出“一个模型 + 一个动画”的 preview glTF。

快速路径只处理已经解码出的 Transform translation / rotation / scale 曲线。它不假装解决所有动画：

- `animation_asset.json` 的 decoded TRS 是 Unity 本地坐标。写入 glTF 前必须和模型导出保持一致：`translation.x=-x`，`rotation.y/z=-y/-z`。否则四足动物、长身体角色等会出现明显卷曲或压缩。
- Humanoid/Muscle-only 动画仍需要 Unity bake 才能作为最终可靠身体动画。
- BlendShape、材质、激活、事件类动画需要独立通道支持。
- 节点匹配必须来自 glTF 节点路径/节点名，不按角色名或目录硬猜。

VRising `HYB_CreatureGhoul_Blackfang_Peon03 + CreatureGhoul_1HPickaxe_Idle` 的实测快速预览耗时约 `0.75s`，生成 `210` 个 glTF animation channel，`invalidChannels=0`。

缓存目录示例：

```text
.as_browser_cache/
  animation_previews/
    <modelKey>/
      <animationKey>/
        preview_state.json
        output/
          preview_validation.json
          Models/.../*.gltf
```

## 状态定义

动画列表中的状态：

- `未生成`：还没有生成过预览。
- `生成中`：CLI 正在生成 preview glTF。
- `可播放`：已生成 glTF，并找到 `preview_validation.json` 或 glTF 文件。
- `失败`：CLI 失败、源文件缺失或没有生成 glTF。
- `需烘焙`：Humanoid/Muscle 动画，需要 Unity bake 才能可靠验收。

`可播放` 仍不等于生产可用，只说明 preview 文件存在并通过当前最小检查。最终仍应看 `preview_validation.json` 里的 channel、skin、joint、bbox、humanoid bake 状态。

## 后续迭代

优先级建议：

1. 在 Browser 中增加独立动画候选面板的搜索和过滤。
2. 增加“只看显式 / 只看可匹配 / 只看可播放 / 只看失败”筛选。
3. 给 `preview_validation.json` 做结构化摘要展示。
4. 增加批量生成：只对用户收藏、显式绑定或当前筛选结果生成预览。
5. 增加内嵌 3D 播放器；在这之前默认用 f3d 打开生成的 preview glTF。
6. 对 Humanoid/Muscle 接 Unity bake 流程，并把 bake 状态写入预览状态。

原则：先保证候选关系可信、预览按需、缓存稳定，再做更复杂的实时播放器。
