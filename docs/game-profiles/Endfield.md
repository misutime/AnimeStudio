# Endfield Profile

## 当前状态

Endfield 当前主要是显式诊断和研究对象，不属于默认 Library 动画目标。涉及 ACL scalar tracks、Humanoid/Muscle、IK goal、AnimatorController runtime 参数和剧情/Timeline 实例边界时，必须保持诊断状态，不能把单 clip 或生成 controller 的结果伪装成生产可复用动画。

## 特殊入口和显式开关

- 动画研究必须显式开启诊断命令或实验 solver。
- VFS / `.blc` / bundle 定位、ACL 解压、controller inspect 等只作为诊断路径。
- 默认 Library 仍只验收模型、材质、贴图、骨骼/skin、索引和报告；动画不入默认库。

## 默认导出边界

- 剧情、Timeline、UI、preview、postmodel 等实例默认只作为诊断线索。
- 若模型材质链不完整、只用于剧情/UI 展示或主体模型不完整，不应为通过验收投入主线修复。
- 已知模型导出不完整或不准确的样本，应在执行前拒绝或降级，并给出原因，避免跑完才发现主模型不可用。

## 已上升为通用标准的经验

- `m_ValueArrayDelta` 不能直接当逐帧曲线；必须先找到真实采样载荷。
- ACL transform/scalar、streamed/dense/constant 等数据必须按真实含义解析，不能硬插值成动画。
- AnimatorController 单 clip 引用不等于完整动作上下文；缺 layer、BlendTree、runtime 参数或 IK 语义时必须标注诊断。
- 旧 Unity bake / generated controller 只能作为 oracle 或诊断，不能单独证明生产可复用。

## 仍只属于本游戏的特殊规则

- Endfield VFS / `.blc` 定位经验。
- Pelica 等样本的 IK、runtime 参数和 controller inspect 结论。
- Endfield 具体 ACL layout 和字段名解释。

## 已知拒绝/降级条件

- 只有辅助节点、头发、衣摆、socket、twist/helper 等 TransformBufferData 覆盖时，不能作为完整身体动作通过。
- IK target/readback 成功但最终骨骼不移动时，保持 `diagnostic_limb_ik_goal_driver` 或等价阻断状态。
- 缺原始 controller 上下文、runtime 参数来源或清晰视觉验收时，不能标成生产可复用。

## 相关存档资料

- [../archive/animation-research/ENDFIELD_ANIMATION_SOLVER_FAILURE_HANDOFF.md](../archive/animation-research/ENDFIELD_ANIMATION_SOLVER_FAILURE_HANDOFF.md)
- [../archive/animation-research/HUMANOID_MUSCLE_BATCH_SOLVER_ANALYSIS.md](../archive/animation-research/HUMANOID_MUSCLE_BATCH_SOLVER_ANALYSIS.md)
