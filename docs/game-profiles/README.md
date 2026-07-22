# 游戏 Profile 文档

这里记录单个游戏的当前有效特殊支持。它不是第二套标准。

使用规则：

- 强制标准只看 [../PROJECT_EXPORT_STANDARDS.md](../PROJECT_EXPORT_STANDARDS.md)。
- 每个游戏 profile 只记录该游戏在通用标准下的特殊入口、显式开关、拒绝条件、已知边界和验证样本。
- 如果某条经验能推广到所有 Unity 游戏，应提炼到 `PROJECT_EXPORT_STANDARDS.md`，并在游戏 profile 中标记“已上升为通用标准”。
- 如果某条规则依赖单个游戏的私有命名、私有 shader、私有打包格式、旧诊断命令或实验路径，只能留在对应 profile，不能进入 `Normal` 通用路径。
- 历史审计和旧研究资料放在 `../archive/`。profile 可以链接它们，但不能让存档资料覆盖当前结论。

## 当前 Profile

- [NoRestForTheWicked.md](NoRestForTheWicked.md)
- [Naraka.md](Naraka.md)
- [Endfield.md](Endfield.md)
- [GenshinImpact.md](GenshinImpact.md)
- [VRising.md](VRising.md)
- [Freedunk.md](Freedunk.md)

## 推荐模板

```markdown
# 游戏名 Profile

## 当前状态

## 特殊入口和显式开关

## 默认导出边界

## 已上升为通用标准的经验

## 仍只属于本游戏的特殊规则

## 已知拒绝/降级条件

## 最新验证证据

## 相关存档资料
```
