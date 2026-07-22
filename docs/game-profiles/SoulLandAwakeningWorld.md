# Soul Land Awakening World 适配记录

## 当前结论

`D:\SteamLibrary\steamapps\common\Soul Land Awakening World` 外层是 Electron 启动器，真正的 Unity 入口在：

`D:\SteamLibrary\steamapps\common\Soul Land Awakening World\v10\slmsea`

Unity 数据目录是：

`D:\SteamLibrary\steamapps\common\Soul Land Awakening World\v10\slmsea\game_Data`

该目录包含 `UnityPlayer.dll`、`GameAssembly.dll`、`globalgamemanagers`、`resources.assets`、`StreamingAssets`，Unity 版本样本为 `2022.3.54f1`。

## 资源形态

主资源位于 `game_Data\StreamingAssets\AB` 和 `game_Data\PersistentData\Remote`，数量约 1.7 万个 `.bundle`。这些文件不是标准 `UnityFS` 头，而是以 `Odin\0\0\0\0` 开头的 SGEngine/Odin 自定义容器。

本地抽样可以看到容器内部存在 `CAB-...`、`.resS` 和 Unity 版本字符串，说明真实 Unity 序列化资源很可能在 Odin 容器里。但当前读取器不能把该容器稳定解成标准 `SerializedFile` / `AssetBundle`，源索引只解析到少量内置 Unity assets，无法得到真实模型、材质、贴图关系。

## 当前工具行为

默认 `Library` 必须拒绝该输入，原因是：

- Odin 包数量大，但没有可直接加载的 `UnityFS` / `UnityWeb` / `UnityRaw` / Naraka 替代 bundle 头。
- 当前源索引没有真实 `MeshFilter`、`SkinnedMeshRenderer`、`Animator`、`AssetBundle.m_Container` 等模型主线关系。
- 继续全量导出会得到空素材库或内置资源假阳性，违反“模型本体完整准确优先”和“不能用诊断零件假装通过”的项目标准。

允许继续执行的命令是只读诊断：`--probe_source_input`、`--inspect_unity_files`、`--build_source_sqlite_index`、`--list_source_model_candidates`。这些结果只能作为兼容开发证据，不能作为素材库验收。

## 后续兼容方向

下一步需要单独实现 SGEngine/Odin 容器解码：

- 从 `Odin` 文件头和 CAB 名附近结构解析内层文件表、数据偏移、压缩或加密规则。
- 若需要密钥或运行时代码，应从本机热更程序集、`global-metadata.dat`、`SGEngine.ResourceManagement` 运行时逻辑中验证，不能硬猜。
- 解出标准 Unity 内层流后，先重建 `unity_source_index.db`，要求候选报告出现真实 prefab/Animator/Renderer/Mesh/Material/Texture 关系。
- 再做至少 10 个代表性模型 glTF 烟测和截图验收；默认仍不导出动画。
