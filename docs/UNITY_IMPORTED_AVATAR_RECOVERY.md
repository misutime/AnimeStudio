# Unity Imported Avatar 恢复说明

本文记录原神这类缺少完整 `HumanDescription.skeletonBones` 的库，如何恢复可供 Unity bake 使用的原始 `UnityEngine.Avatar` asset。

## 主规则

- 只从 `library_index.db` 里模型 `avatar.source` + `avatar.pathId` 指向的 Unity 原对象恢复 Avatar。
- 不使用骨骼数量、骨骼名、目录名或当前姿态推断 Avatar/动画关系。
- 恢复出的 `.asset` 必须放到 Unity bake 工程的 `Assets/AnimeStudioBake/ImportedAvatar/`。
- `.asset` 必须经过 Unity 探针验证：`isValid=true` 且 `isHuman=true` 才能进入 Browser 和 bake 主流程。
- 验证失败的 Avatar 会移到 `Assets/AnimeStudioBake/InvalidImportedAvatar/`，不能用于生产 bake。

## CLI 用法

```powershell
dotnet run --project AnimeStudio.CLI/AnimeStudio.CLI.csproj -f net9.0-windows -- `
  --recover_imported_avatar_assets "D:\Assets\AS-Assets\YuanShen-Assets" `
  --game GI `
  --unity_project "D:\misutime\AnimeStudioUnityProject" `
  --unity_editor "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe" `
  --run_unity_bake
```

正常 Library 导出或 `--build_sqlite_index` 后，如果命令里提供了 `--unity_project`，CLI 会自动尝试恢复 ImportedAvatar。提供 `--unity_editor` 时会自动跑 Unity probe，并只接入验证通过的 Avatar。

## Browser 行为

- Browser 的“刷新动画门禁”菜单里有“恢复AvatarAsset”。
- Browser 会读取 `unity_source_index.db` 的 `game` metadata，缺失时不会猜游戏名。
- Browser 只信任 fresh probe 里通过的 ImportedAvatar；没有 fresh probe 时，不把 ImportedAvatar 当生产 Avatar oracle。
- `.as_browser_cache/unity_bake_settings.json` 只作为 Unity 工程路径和已验证 Avatar 查找映射，不覆盖 probe 门禁。

## 实现注意

Unity Avatar YAML 里的多个字段是 `OffsetPtr<T>`，导出时必须保留 `data:` 包裹，例如：

- `m_AvatarSkeleton`
- `m_AvatarSkeletonPose`
- `m_DefaultPose`
- `m_Human`
- `m_RootMotionSkeleton`
- `m_RootMotionSkeletonPose`
- `m_Human.m_Skeleton`
- `m_Human.m_SkeletonPose`
- `m_Human.m_LeftHand`
- `m_Human.m_RightHand`

缺少这层 `data:` 时，Unity 可能仍能导入 `.asset`，但 `Avatar.isHuman` 会失败，导致动画 bake 重新退回错误姿态。

## 当前原神库验证结果

2026-06-15 对 `D:\Assets\AS-Assets\YuanShen-Assets` 执行全库恢复：

- 恢复并保留有效 Human Avatar：231 个。
- Unity probe：`231/231` 有效，`invalidAssets=0`。
- 无效 Avatar 已隔离到 `InvalidImportedAvatar`。
- 快速摘要显示 ImportedAvatar fresh probe 已强制启用，显式 Humanoid/Muscle 候选 bake-ready 覆盖约 `97.59%`。
