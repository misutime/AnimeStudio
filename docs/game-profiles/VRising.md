# V Rising Profile

## 当前状态

V Rising 主要作为模块化角色、换装、头发、ColorMask/Tint、Generic/非标准骨架和直接 Transform TRS 路径的交叉验证样本。它不需要默认游戏私有规则进入 `Normal` 路径。

## 特殊入口和显式开关

- 默认仍使用通用 Unity Library 导出。
- 需要静态环境/道具 Mesh 时显式使用 `--include_static_meshes`。
- 动画研究必须显式开启诊断或预览命令；默认 Library 不导出动画。

## 默认导出边界

- 模块化角色应保留 body、face、hair、accessory 等可组合关系，不强行永久合并。
- ColorMask/Tint 找不到确定配色时保留 mask 和 sidecar，标记 `needsCustomizationTint`。
- Generic/Transform 动画即使能直接写 glTF TRS，也必须保持显式诊断边界，不能改变默认动画能力声明。

## 已上升为通用标准的经验

- ColorMask/Tint 是可用预览材质管线，不是完整 shader 复刻。
- 模块化角色默认保持模块化，组合关系必须来自 Unity 引用、同骨架 joint 映射或明确 skin 可重定向关系。
- 能直接导出的 Generic/Transform TRS 是动画研究参考路径，但动画仍不进入默认 Library。

## 仍只属于本游戏的特殊规则

当前没有应写入默认 `Normal` 路径的 V Rising 私有命名规则。

## 相关存档资料

暂无单独 V Rising 存档审计。
