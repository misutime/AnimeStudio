# Naraka 首版可用导出审计

审计日期：2026-06-26

本页记录永劫无间（Naraka: Bladepoint）当前第一版可用导出状态。它不是全量导出报告，而是小范围代表性 smoke 的事实清单，用来判断工具是否已经打通模型、贴图、材质、索引和报告闭环。

## 结论

当前 Naraka P0/P1 静态素材库链路已经可用：`StreamingAssets` 输入、Naraka header 修正、源索引、定向 Library 导出、共享贴图、材质 sidecar、`asset_library.json`、`library_index.db`、glTF 校验和浏览器索引验证都能跑通。

动画仍是 P2 诊断阶段。当前已有手动 `--preview_avatar` 入口能把源索引里的 Avatar.m_TOS 临时注入，用于 hash-only TRS 动画路径恢复诊断；但它不会创建默认模型-动画关系，也不能让 `asset_library.json.capabilities.animations` 变成 `true`。生产动画能力还需要继续找到完整角色、Avatar、Controller/Clip 或其它 Unity 显式关系，并补足清晰视觉验收。

## 输入形态

本机 Naraka 主资源入口：

```text
C:\Game163\program\NarakaBladepoint_Data\StreamingAssets
```

当前 smoke 结论是这批资源可以直接走 `--game Naraka` 的 Unity 素材读取路径。`C:\Game163\program` 下少量 `.pak` 属于 webview/CEF 资源，不是当前模型、贴图、材质和骨骼主入口。网上流传的 AES key 只能作为其它发行形态或旧版本线索，不能写入默认 Library 流程。

社区资料只作为参考：ResHax 线程提到 Naraka 在 2024 年多次改过解包/加密方式，并指向 Rivelia/Studio、cs.rin 等线索；这不能替代本地 `StreamingAssets` 探针和源索引验证。

## 代表样本

| 样本 | 输出目录 | 当前状态 | 说明 |
| --- | --- | --- | --- |
| Hadi body s9 | `D:\Assets\Naraka\HadiBody_s9_IndexV1_Rebuild_Current` | P0/P1 正样本 | 1 个 Model、29 个 Texture、11 个 Material sidecar，`model_validation=ok`，`texture_links.link_error=0`。 |
| Face male battle | `D:\Assets\Naraka\FaceMaleBattle_ShaderBoundary_Current` | 特殊 shader 边界样本 | 1 个 Model、17 个 Texture、3 个 Material sidecar，2 个材质标记 `customShaderRequired/layeredMaterialUnresolved`，`model_validation=warning`。 |
| Bow prop | `D:\Assets\Naraka\CandidateBatch_SkinRootFix\wp_bow_dongjun\Smoke_weapon_drop_bow_dongjun_root_3879445205109982761` | 非角色正样本 | 武器/道具 prefab，材质和贴图链接正常。 |
| Device hongbao | `D:\Assets\Naraka\SourceCandidates_TextureHealthOk1\Smoke_device_hongbao_02_3817277305598733592_PropKind_Current` | 非角色正样本 | device/prop 样本，`model_validation=ok`。 |
| Static bigtree | `D:\Assets\Naraka\Smoke_static_jisui_device_bigtree_04_Current` | 静态场景/道具样本 | 静态 mesh/prop 样本，`model_validation=ok`。 |

本轮复查的 SQLite 摘要：

```text
hadi_body: models=1 textures=29 material_sidecars=11 texture_links=32 link_errors=0 custom_shader_materials=0 validation=ok:1
face_shader: models=1 textures=17 material_sidecars=3 texture_links=5 link_errors=0 custom_shader_materials=2 validation=warning:1
weapon_bow: models=1 textures=10 material_sidecars=3 texture_links=12 link_errors=0 custom_shader_materials=0 validation=ok:1
device_hongbao: models=1 textures=4 material_sidecars=1 texture_links=4 link_errors=0 custom_shader_materials=0 validation=ok:1
static_bigtree: models=1 textures=12 material_sidecars=1 texture_links=13 link_errors=0 custom_shader_materials=0 validation=ok:1
```

## 特殊材质边界

`FaceMaleBattle_ShaderBoundary_Current\MATERIAL_REPORT.md` 已经按当前规则标记 Naraka 私有 shader 分层材质：

- `male_eye` 的 `_IrisDiffuseTex`、`_IrisNormalTex`、`_IrisSpecTex` 保留为 `preservedOnly`。
- `male_face_custom` 的眉毛 decal、皱纹 diffuse/normal/spec 槽保留为 `preservedOnly`。
- 对应材质在报告和 SQLite `material_sidecars` 中标记 `unsupportedShader`、`customShaderRequired`、`layeredMaterialUnresolved`。
- `pbr_preview_status=bestEffortDegradedPreview`，表示 glTF 只是降级预览，不代表原游戏最终 shader 效果。

这类样本不应被归类为贴图丢失或材质错绑；它们说明 Mesh、UV、贴图引用和原始材质槽已经确定性保留，但仍需要 Naraka 私有 shader 复刻或人工材质重建。

## 可复现命令

推荐一行 smoke：

```powershell
tools\Export-NarakaFirstUsableSmoke.ps1
```

默认输入 `C:\Game163\program\NarakaBladepoint_Data\StreamingAssets`，默认使用 `D:\Assets\Naraka\SourceIndex_Full_HeaderFix1\unity_source_index.db`，输出到 `D:\Assets\Naraka\Naraka_FirstUsableSmoke_Current`。脚本会跑输入探针、导出 Hadi body s9、重建 `library_index.db`，并在本机工具存在时执行 glTF validator、AssetLibrary Browser 验证和 1 张缩略图渲染。脚本还会默认跑 Dijiang A8 独立动画 glTF 诊断，验证 `Avatar.m_TOS` 路径恢复和动画 glTF 写出；报告必须保留 `diagnosticOnly=true` / `notDefaultModelAnimationRelation=true`，因此它不会把当前手动动画诊断升级成生产动画库能力。只想跑 P0/P1 静态链路时传 `-SkipAnimationDiagnostic`。

脚本结束后会在 `OutputRoot` 写出两个汇总文件：

- `SMOKE_REPORT.md`：人读 smoke 结论，汇总静态模型、glTF 校验、浏览器校验、缩略图、SQLite 索引计数、贴图链接质量门槛、特殊 shader 降级计数、动画关系覆盖摘要和动画诊断边界。
- `smoke_summary.json`：机器读 smoke 摘要，用来复查产物路径、能力标记、验证状态、SQLite 索引计数、`qualityGates` 和动画诊断状态。

这两个文件只汇总 smoke 证据，不会改变正式 `HadiBody_s9` 素材库，也不会把诊断动画写成默认动画关系。
脚本会要求 `qualityGates.textureLinkErrors=0`；如果 glTF 贴图引用链路断开，smoke 会直接失败。`customShaderRequiredSidecars` / `layeredMaterialUnresolvedSidecars` 只作为 Naraka 私有 shader 边界证据记录，不会被当成贴图丢失。

输入探针：

```powershell
AnimeStudio.CLI\bin\Release\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Game163\program\NarakaBladepoint_Data\StreamingAssets" `
  "D:\Assets\Naraka\SourceInputProbe_Current" `
  --game Naraka `
  --probe_source_input
```

Hadi body 定向 Library 导出示例：

```powershell
AnimeStudio.CLI\bin\Release\net9.0-windows\AnimeStudio.CLI.exe `
  "C:\Game163\program\NarakaBladepoint_Data\StreamingAssets" `
  "D:\Assets\Naraka\HadiBody_s9_IndexV1_Rebuild_Current" `
  --game Naraka `
  --mode Library `
  --group_assets ByLibrary `
  --profile_3d Core `
  --model_format Gltf `
  --texture_mode Png `
  --animation_package Separate `
  --fbx_animation Skip `
  --source_index "D:\Assets\Naraka\SourceIndex_Full_HeaderFix1\unity_source_index.db" `
  --source_files "4\c\4c08b7069a411750" `
  --path_ids -6619473669887381141
```

刷新 AssetLibrary v1 索引：

```powershell
AnimeStudio.CLI\bin\Release\net9.0-windows\AnimeStudio.CLI.exe `
  --build_sqlite_index "D:\Assets\Naraka\HadiBody_s9_IndexV1_Rebuild_Current" `
  --source_index "D:\Assets\Naraka\SourceIndex_Full_HeaderFix1\unity_source_index.db" `
  --game Naraka `
  --skip_sqlite_file_index
```

glTF 校验：

```powershell
gltf-transform validate "D:\Assets\Naraka\HadiBody_s9_IndexV1_Rebuild_Current\Models\assets\res\prefab\actor_visual_part\ch_m_hadi\ch_m_hadi_lv_s9\ch_m_hadi_lv_s9.gltf"
gltf-transform validate "D:\Assets\Naraka\FaceMaleBattle_ShaderBoundary_Current\MonoBehaviour_lod0.gltf"
```

## 动画当前能力

当前已验证一个诊断正样本：

```text
D:\Assets\Naraka\Naraka_P2_Hadi_DijiangA8_SourceAvatarTosDirectTrs_Current\mo_pve_b_dijiang_attack_a8_01.animation.gltf
```

报告状态为 `ok`，写出 159 个 animation channel，并明确：

- `avatarInjection.mode=directTrsPathHashMapping`
- `diagnosticOnly=true`
- `notDefaultModelAnimationRelation=true`

当前也有一个正确失败样本：

```text
D:\Assets\Naraka\Naraka_P2_Hadi_Hengdao_InternalHumanoid_SourceAvatarDiagnostic_AfterTosSplit_Current\standalone_animation_gltf_report.json
```

它标记 `forced_internal_humanoid_export_failed`，并说明该 clip 没有身体 Humanoid 曲线，只有 root-motion/辅助 float 或局部 TRS，不能当作人形身体动画生产样本。

因此当前动画结论是：诊断工具链可用，生产级动画库未完成。

## 风险和下一步

- Hadi body 是模块化角色 body/服装部件，当前 `modelCompletenessStatus=modular_incomplete`，不能单独作为完整角色动画 smoke。
- Face/hair 等 Naraka 自定义网格还需要正式装配、skin、tint/custom shader 复刻或人工材质重建；这些问题应被报告和索引标记，不阻塞其它静态模型主线。
- 下一步优先找完整角色 prefab/配置、Avatar、Controller/Clip 的 Unity 显式关系；不要按角色名、骨架兼容或动作名硬绑定动画。
- 扩大 smoke 时应继续覆盖角色、武器、道具、建筑/环境和静态场景物，并用 SQLite、glTF validator、浏览器缩略图和清晰截图共同验收。

## 参考资料

- ResHax Naraka 讨论：https://reshax.com/topic/664-%E3%80%90pc%E3%80%91naraka-bladepoint/
- Rivelia/Studio：https://github.com/Rivelia/Studio
- QuickBMS：https://aluigi.altervista.org/quickbms.htm
