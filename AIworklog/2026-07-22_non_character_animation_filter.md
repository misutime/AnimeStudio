# 2026-07-22 Library 动画导出增加非角色动画过滤

## 背景与目的

使用 `--animation_package Embedded` 或 `Separate` 显式导出动画时，所有 AnimationClip 都会被尝试导出，包括 VFX 特效动画、UI 动画、Timeline/过场动画、场景/道具动画等非角色动画。这些动画：

1. 不是可复用的角色动作素材，对素材库没有价值
2. 部分 VFX 动画片段（如 `vfx_无回阵_销毁`、`vfxui_白虎墨_大招全屏攻击`）会触发 `AnimationClipConverter.ProcessInner()` 的 NRE 崩溃，因为其 `m_MuscleClip` 为 null
3. 浪费导出时间，产生大量无意义的诊断输出

模型侧已有 `FilterDefaultVfxModels` 过滤 VFX 网格，SQLite 索引侧已有 `ClassifyControllerClipDomain` 做 domain 分类（VFX/UI/Timeline/Prop），但动画导出路径缺少对应的过滤逻辑。本次修改补齐这个 gap。

## 修改思路

复用已有的 domain 分类关键词集合（来自 `ClassifyControllerClipDomain` 和 `Non3DLibraryRootPattern`），构建一个统一的正则 `NonCharacterAnimationPattern`，在 Library 导出流程中对 AnimationClip 列表做过滤。

匹配维度与 `FilterDefaultVfxModels` / `IsLikelyMeshVfxModel` 保持一致：clip 名称（`asset.Text`）、Unity 容器路径（`asset.Container`）、来源包名（`asset.SourceFile`）。三个维度拼接后统一匹配。

过滤位置在 `FilterDeprecatedLibraryAssets` 之后、`AnimationPackage == Skip` 判断之前。无论动画是否导出，非角色动画先被过滤；`Skip` 模式下剩余的也会被清空。

## 修改范围

仅修改 `AnimeStudio.CLI/Studio.cs`，共 3 处：

### 1. 新增 `NonCharacterAnimationPattern` 正则（第 126 行）

```csharp
private static readonly Regex NonCharacterAnimationPattern = new Regex(
    @"(?:^|[/_.\-\s])(?:vfx|vfxui|fx_|fx |effect|effects|cloudeffect|particle|particles|trail|trails|slash|impact|hitfx|explosion|smoke|fire|flame|spark|sparks|beam|laser|aura|buff|debuff|projectile|spell|skill|uiface|ui_|/ui|btn|button|dropdown|slider|mousecursor|progressbar|joystick|selector|pressed|highlighted|disabled|selected|hover|transitionanim|dimanim|canvas|dialog|popup|toast|panel|payplat|webshow|loading|fade|trackeditor|timeline|cutscene|cinematic|preview|device|prop|scene|stage|decor|decoration|camera|light|audio)(?:$|[/_.\-\s0-9])",
    RegexOptions.IgnoreCase | RegexOptions.Compiled
);
```

关键词分组：
- **VFX 特效**：`vfx`、`vfxui`、`fx_`、`effect`、`particle`、`trail`、`slash`、`impact`、`skill` 等
- **UI 界面**：`ui_`、`btn`、`button`、`canvas`、`dialog`、`popup`、`panel`、`dropdown`、`slider` 等
- **Timeline/过场**：`timeline`、`cutscene`、`cinematic`、`preview`
- **场景/道具**：`prop`、`scene`、`stage`、`device`、`decor`
- **辅助对象**：`camera`、`light`、`audio`

### 2. 新增 `FilterNonCharacterAnimations` 和 `IsNonCharacterAnimation` 方法（第 1478 行）

```csharp
private static List<AssetItem> FilterNonCharacterAnimations(List<AssetItem> animations)
{
    var kept = new List<AssetItem>(animations.Count);
    var skipped = 0;
    foreach (var clip in animations)
    {
        if (IsNonCharacterAnimation(clip))
        {
            skipped++;
            continue;
        }
        kept.Add(clip);
    }
    if (skipped > 0)
    {
        Logger.Info($"Library filtered {skipped} non-character animation clip(s) (VFX/UI/Timeline/scene). Use targeted export to include these.");
        ProfileLogger.Event("library_non_character_animation_filter", new Dictionary<string, object>
        {
            ["inputCount"] = animations.Count,
            ["skippedCount"] = skipped,
            ["keptCount"] = kept.Count,
            ["rule"] = "AnimationClip name/container/source matches VFX, UI, Timeline, cutscene, preview, scene, prop, camera, light, or audio signal.",
        });
    }
    return kept;
}

private static bool IsNonCharacterAnimation(AssetItem asset)
{
    var sourceName = asset.SourceFile?.originalPath ?? asset.SourceFile?.fileName;
    var signalText = string.Join(
        "/",
        new[] { asset.Container, Path.GetFileNameWithoutExtension(sourceName), asset.Text }
            .Where(x => !string.IsNullOrWhiteSpace(x))
    ).Replace('\\', '/').ToLowerInvariant();
    return NonCharacterAnimationPattern.IsMatch(signalText);
}
```

信号文本拼接逻辑与 `IsLikelyMeshVfxModel` 完全一致，保证匹配维度统一。

### 3. 在 Library 导出流程中调用（第 1339 行）

```csharp
animations = FilterDeprecatedLibraryAssets(animations, "animation");
animations = FilterNonCharacterAnimations(animations);  // 新增
if (CliExportOptions.AnimationPackage == AnimationPackageMode.Skip && animations.Count > 0)
{
    animations.Clear();
}
```

## 自查结果

- `dotnet build AnimeStudio.CLI` 编译通过，0 error
- 过滤逻辑只作用于 `--mode Library` 的动画候选列表，不影响模型、贴图、材质、骨骼导出
- `Skip` 模式下动画全部清空，过滤方法无副作用（空列表快速返回）
- 被过滤的动画仍保留在 `unity_source_index.db` 和 `asset_catalog.jsonl` 索引中，只是不作为可浏览素材导出
- 需要这些动画时可通过 `--mode Export --types AnimationClip:Both` 显式提取，不经过 Library 过滤链路

## 预期效果

以 Realm of Ink 为例（4335 个 AnimationClip），使用 `--animation_package Embedded` 时：

- `vfx_*`、`vfxui_*` 等特效动画不再尝试导出，避免 NRE 崩溃
- `ui_*`、`*button*`、`*dialog*` 等 UI 动画被过滤
- `*timeline*`、`*cutscene*`、`*preview*` 等过场动画被过滤
- 只保留角色/怪物/NPC 等身体动画，宁缺毋滥
- 日志会输出过滤数量，方便确认过滤效果
