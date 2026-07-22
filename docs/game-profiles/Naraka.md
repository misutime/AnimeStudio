# Naraka Profile

## 当前状态

Naraka 有明确游戏 profile 需求，主要来自特殊 bundle header、StreamingAssets 输入形态、自定义 `ActorBodyVisualCell` / `AvatarMeshDataAsset` 网格链路、发型 tint/customization 和脸部 runtime skin 诊断。默认结论必须保守：诊断模型不能因为能导出 glTF 就进入动画生产验收。

## 特殊入口和显式开关

- 使用 `--game Naraka` 处理 Naraka 替代 UnityFS 文件头和已知 block 对齐。
- 输入应优先使用可加载的 `NarakaBladepoint_Data/StreamingAssets`。`.pak` / AES 只能作为独立预处理或诊断线索，不能替代 StreamingAssets 主入口。
- 自定义网格、脸部 runtime skin、外部骨架 skin 等路径必须由显式诊断开关开启。

## 默认导出边界

- `ActorBodyVisualCell -> LXRendererAssistant -> AvatarMeshDataAsset` 可作为确定性 PPtr 证据。
- 自定义网格导出默认是诊断资产，必须标注材质 tint、skin/joint 映射和完整角色装配是否通过。
- 找不到确定 customization tint 配置时，保持 `needsCustomizationTint`，不能硬猜颜色。
- 诊断 skin 在 bind pose 空间、完整父骨、材质 tint 和视觉验收通过前，不能进入动画导出、合成或生产 smoke。

## 已上升为通用标准的经验

- 游戏私有 shader/tint 不能硬猜，必须保留原始 slot、参数、贴图和降级状态。
- 自定义模型链路必须从 Unity PPtr / component 关系出发，不能按资源名前缀猜。
- 诊断模型和生产可用模型必须在 catalog、报告和 SQLite 中明确区分。

## 仍只属于本游戏的特殊规则

- Naraka 替代 UnityFS header 修正。
- `ActorBodyVisualCell`、`LXRendererAssistant`、`AvatarMeshDataAsset`、`AvatarFaceRuntime` / `AvatarFaceData` 诊断链路。
- Naraka 发型 customization/tint 线索解释。

## 已知拒绝/降级条件

- 只有自定义网格诊断、没有完整角色装配或生产 skin 验证时，不能作为默认 Library 主资源通过。
- 外部骨架/脸部 runtime skin 仍为诊断状态时，禁止进入动画生产验收。

## 相关存档资料

- [../archive/game-audits/NARAKA_FIRST_USABLE_AUDIT.md](../archive/game-audits/NARAKA_FIRST_USABLE_AUDIT.md)
