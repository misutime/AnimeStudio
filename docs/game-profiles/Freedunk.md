# Freedunk Profile

## 当前状态

Freedunk 是早期固定开发样本，主要用于模型、贴图、材质、骨骼/skin、球场/道具、BlendShape、Humanoid/Muscle 和非角色 Transform 动画诊断。当前默认 Library 目标已经不再把动画作为默认导出能力。

## 特殊入口和显式开关

- 开发样本脚本仍保留在 `tools/Export-Freedunk*.ps1`。
- 样本流程见 [../DEV_SAMPLE_WORKFLOW.md](../DEV_SAMPLE_WORKFLOW.md)。
- 动画相关样本只作为显式诊断和历史回归，不改变默认 Library 能力。

## 默认导出边界

- 不要把 Freedunk 全局篮球动作库嵌进每个角色模型。
- 默认模型应保持干净，只导出模型、贴图、材质、骨骼/skin、索引和报告。
- `NORMALMOVE_STAND_01` 等 Humanoid/Muscle 诊断不能因为有 glTF channel 就视为生产可复用。

## 已上升为通用标准的经验

- 动画不能默认嵌入模型。
- 模型静态验收优先于动画诊断。
- 全量动作库重复嵌入会让素材库难浏览、文件巨大、关系不可信。

## 仍只属于本游戏的特殊规则

- Freedunk 固定样本路径、脚本和篮球动作命名。
- Freedunk Humanoid/Muscle 诊断样本。

## 相关资料

- [../DEV_SAMPLE_WORKFLOW.md](../DEV_SAMPLE_WORKFLOW.md)
