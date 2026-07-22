# AnimeStudio 项目文档

这里集中放项目级说明、导出规范、工具设计和迭代记录。根目录只保留 `README.md` 作为仓库入口，`AGENTS.md` 作为项目内协作约定入口。

## 强制标准入口

- [PROJECT_EXPORT_STANDARDS.md](PROJECT_EXPORT_STANDARDS.md)：唯一的资源导出强制标准入口。涉及 Unity 资源导出、默认 Library、动画边界、材质、索引、性能和验收时先读这个文件。

## 当前参考文档

- [CLI_USAGE.md](CLI_USAGE.md)：CLI 参数、常用命令、导出流程和排查说明。它是详细参考，不是标准来源；若和 `PROJECT_EXPORT_STANDARDS.md` 冲突，以标准文档为准。
- [DEV_SAMPLE_WORKFLOW.md](DEV_SAMPLE_WORKFLOW.md)：开发样本、验证步骤和复现流程。
- [game-profiles/README.md](game-profiles/README.md)：单游戏特殊适配和验证证据索引。它只说明各游戏在通用标准下的特殊情况，不是第二套标准。
- [RESOURCE_EXPORT_ROADMAP.md](RESOURCE_EXPORT_ROADMAP.md)：资源导出路线图、阶段目标和待办。
- [INDEX_ARCHITECTURE_REVIEW.md](INDEX_ARCHITECTURE_REVIEW.md)：索引体系分析和 SQLite/JSON 分工。

## 存档资料

- [archive/README.md](archive/README.md)：旧动画研究、单游戏审计和历史交接资料。存档文档可作为证据和背景，但不能覆盖当前强制标准。
