# Genshin Impact Profile

## 当前状态

Genshin Impact 保留为历史重 Unity bake 链路和 Avatar/Animation 诊断经验来源。当前默认 Library 不应依赖游戏专用 Unity 工程、插件/helper、Editor/Player 批处理或旧 Browser 双击 bake 才能交付结果。

## 特殊入口和显式开关

- 旧式 Unity bake、AvatarConstant/internalSolver、Browser bake cache 等只能作为显式诊断或历史迁移路径。
- 默认模型导出仍应遵守通用 Unity 关系、glTF 主格式、材质/贴图/骨骼/skin 验收规则。

## 默认导出边界

- 默认不把模型可能关联到的动画嵌入 glTF/GLB。
- 默认不写 `model_animations.json` 或 SQLite 动画推荐候选。
- AvatarConstant/internalSolver 这类临时 Avatar 恢复不能覆盖显式 Unity Avatar asset 请求的失败。

## 已上升为通用标准的经验

- 外部 Unity 工程重 bake 不能作为默认新游戏支持路径。
- “能 bake / 能播放”不等于生产可复用，必须有确定性关系、glTF TRS/weights、主体骨骼覆盖和清晰视觉验收。
- 旧 bake cache 或 report status 不能绕过模型静态验收。

## 仍只属于本游戏的特殊规则

- 历史原神 bake 工程、旧 Browser 交互和相关缓存格式。
- 旧 AvatarConstant/internalSolver 诊断链路。

## 已知拒绝/降级条件

- 需要用户维护游戏专用 Unity 工程才能得到结果时，不得作为默认 Library 支持完成。
- 显式 Avatar asset 加载失败时，不能静默退回临时 Avatar 并伪装生产结果。

## 相关存档资料

- [../archive/animation-research/UNITY_IMPORTED_AVATAR_RECOVERY.md](../archive/animation-research/UNITY_IMPORTED_AVATAR_RECOVERY.md)
- [../archive/animation-research/LIBRARY_BROWSER_ANIMATION_PREVIEW_DESIGN.md](../archive/animation-research/LIBRARY_BROWSER_ANIMATION_PREVIEW_DESIGN.md)
- [../archive/animation-research/HUMANOID_MUSCLE_BATCH_SOLVER_ANALYSIS.md](../archive/animation-research/HUMANOID_MUSCLE_BATCH_SOLVER_ANALYSIS.md)
