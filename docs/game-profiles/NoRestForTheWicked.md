# NoRest For The Wicked Profile

## 当前状态

NoRest 近期主要用于验证 prefab 组装、被 prefab 覆盖的 renderer part 清理、静态 Mesh 扩展输出、材质 sidecar 和小样本性能策略。当前应继续走 `--game Normal` 的通用 Unity 路径，不应增加 NoRest 私有命名规则。

## 特殊入口和显式开关

- 推荐使用完整源目录 + `unity_source_index.db` 作为依赖底座。
- 定向 smoke 可使用 `--source_files` 和 `--source_object_keys SerializedFile:PathID`，但输入 root 仍必须指向完整游戏源目录。
- 静态环境/建筑/道具 Mesh 需要显式 `--include_static_meshes`。
- 动画默认跳过，`asset_library.json.capabilities.animations=false`。

## 默认导出边界

- prefab / Animator / 完整 GameObject / `PrefabAssemblyPrimary` 是正式可浏览主模型。
- `PrefabRendererPart` / `CoveredByPrefab` 只作为组装证据进入 catalog、SQLite 和 `prefab_assemblies.json`；默认删除中间 glTF 文件。
- 被 prefab 覆盖的 Mesh 即使开启 `--include_static_meshes`，也不能升级成 `StaticMeshPrimary`。
- 没有被 prefab 覆盖、且有静态素材语义或 Renderer 使用关系的 Mesh，才可作为 `StaticMeshPrimary`。

## 已上升为通用标准的经验

- prefab 组合主模型优先，covered renderer parts 不作为正式素材入口。
- covered part 默认删除文件，但保留 catalog/SQLite/report 证据；`export_manifest.jsonl` 不得指向已删除路径。
- PrefabAssembly 使用短目录和可读文件名，完整 Unity container 路径保留在索引和报告里。
- 小样本/定向 smoke 不预热全量 source_relations 大表缓存，候选少时优先按目标对象精确查询。
- 自定义 shader 或分层材质造成的降级预览必须写明 `needsCustomShaderLayer` / `bestEffortDegradedPreview` 等状态。
- 超大 `AssetBundle.containerPreload` 不能盲目展开成千万级关系；应写 `assetBundle.containerPreloadRange` 压缩记录，并保留展开/压缩统计。
- 非定向 source model 候选只是烟测选样，不是全库排行榜；默认 broad 扫描必须保持有界，需要具体资源时用 `--names` / `--preview_model` 定向。

## 仍只属于本游戏的特殊规则

当前没有应写入默认 `Normal` 路径的 NoRest 私有命名规则。若后续发现 NoRest 私有 shader、目录或资源命名，只能放在本 profile 或显式 profile/config 中。

## 最新验证证据

- `D:\Assets\unityAssets\NoRest_SourceIndex_WalGuard_Duplicate_20260702`
  - 输入 `duplicateassetisolation_assets_all_1241714521824fe3cb084d28d2b047b9.bundle`，单 bundle 6.51 GB。
  - 源索引约 1 分钟完成；最终 `unity_source_index.db=2.22 GB`，最终 WAL 为 0。
  - `sourceObjects=308511`，`sourceRelations=1272699`。
  - `assetBundleContainerPreloadExpandedRelations=500000`，`assetBundleContainerPreloadRangeRelations=24899`，`assetBundleContainerPreloadCompactedRanges=22783`，`assetBundleContainerPreloadCompactedSkippedRows=44104033`。
  - `--list_source_model_candidates --include_static_meshes --source_candidate_limit 40` 总耗时约 11.66s，`container_primary` 约 1.31s，查询后 WAL 仍为 0。
- `D:\Assets\unityAssets\NoRest_SourceIndex_WalGuard_Qdb_20260702`
  - 输入 `qdb_assets_all_6c538eadb43f7e2c9a1bd77b6f2d6c99.bundle`。
  - `sourceObjects=68475`，`sourceRelations=1092509`；写入中曾出现约 1 GB WAL，批内 checkpoint 后清零，最终无 WAL。
- `D:\Assets\unityAssets\NoRest_RepresentativeVisualSmoke_DefaultDeleteParts3`
  - `PrefabRendererPart=110`，全部 `output=null`、`diagnosticFileStatus=deletedCoveredByPrefabPart`。
  - `StaticMeshPrimary=8`，`PrefabAssemblyPrimary=10`。
  - `export_manifest.jsonl` 为 18 条正式输出，失效路径 0，GameObject manifest 行 0。
  - F3D 视觉 smoke 18/18 成功。
- `D:\Assets\unityAssets\NoRest_RepresentativeVisualSmoke_PerfCheck_StaticMeshBinding`
  - 小样本静态 Mesh 材质绑定不再全量预热 `static_mesh_material_binding_cache_build`。
  - per-mesh 查询约 14.5s；临时 target-cache 实验约 46s，已撤回。

## 相关存档资料

暂无单独 NoRest 存档审计。通用结论已写入 [../PROJECT_EXPORT_STANDARDS.md](../PROJECT_EXPORT_STANDARDS.md)。
