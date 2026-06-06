using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AnimeStudio.CLI
{
    internal static class AssetReadmeGenerator
    {
        public static void Generate(string savePath)
        {
            var catalogPath = Path.Combine(savePath, "asset_catalog.jsonl");
            if (!File.Exists(catalogPath))
            {
                return;
            }

            var models = LoadCatalogModels(catalogPath).ToArray();
            if (models.Length == 0)
            {
                return;
            }

            var compact = LoadJson(Path.Combine(savePath, "model_animations.compact.json"));
            var assemblies = LoadJson(Path.Combine(savePath, "character_assemblies.json"));
            var animationById = BuildAnimationMap(compact);
            var animationRefsByModel = BuildAnimationRefs(compact);
            var assemblyByOutput = BuildAssemblyMap(assemblies);

            WriteLibraryReadme(savePath, models.Length, compact, assemblies);

            foreach (var model in models)
            {
                var output = (string)model["output"];
                if (string.IsNullOrWhiteSpace(output) || !File.Exists(output))
                {
                    continue;
                }

                var modelDir = Path.GetDirectoryName(Path.GetFullPath(output));
                if (string.IsNullOrWhiteSpace(modelDir))
                {
                    continue;
                }

                var readmePath = Path.Combine(modelDir, "ASSET_README.md");
                File.WriteAllText(
                    readmePath,
                    BuildReadme(model, output, compact, animationById, animationRefsByModel, assemblyByOutput),
                    Encoding.UTF8
                );
            }
        }

        private static void WriteLibraryReadme(string savePath, int modelCount, JObject compact, JObject assemblies)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# AnimeStudio 素材库说明");
            sb.AppendLine();
            sb.AppendLine("这份文件是给人看的素材库入口。正常浏览模型时，优先进入 `Models/` 找模型目录，然后打开该目录里的 `ASSET_README.md`。");
            sb.AppendLine();

            sb.AppendLine("## 推荐阅读顺序");
            sb.AppendLine();
            sb.AppendLine("1. `Models/.../ASSET_README.md`：单个模型怎么用，材质、动画、模块有什么注意事项。");
            sb.AppendLine("2. `Models/.../MATERIAL_REPORT.md`：单个模型的材质/贴图/tint 细节。");
            sb.AppendLine("3. `model_animations.compact.json`：模型和动画候选关系的轻量索引。");
            sb.AppendLine("4. `character_assemblies.json`：body、face、hair、accessory 等模块组装候选。");
            sb.AppendLine("5. 其他 JSON/JSONL：给工具、诊断和后续自动化使用。");
            sb.AppendLine();

            sb.AppendLine("## 文件职责");
            sb.AppendLine();
            sb.AppendLine("| 文件 | 给谁看 | 用途 |");
            sb.AppendLine("| --- | --- | --- |");
            sb.AppendLine("| `ASSET_README.md` | 人 | 单模型使用入口，汇总基础信息、材质状态、动画候选、模块组装和建议。 |");
            sb.AppendLine("| `MATERIAL_REPORT.md` | 人 | 单模型材质说明，解释灰色、透明、mask、tint 等问题。 |");
            sb.AppendLine("| `asset_catalog.jsonl` | 工具/诊断 | 所有导出资产的主索引，一行一个资产。 |");
            sb.AppendLine("| `asset_summary.json` | 人/工具 | 素材库数量摘要。 |");
            sb.AppendLine("| `model_animations.compact.json` | 工具/高级用户 | 轻量模型-动画候选索引，适合浏览器/工具读取。 |");
            sb.AppendLine("| `model_animations.json` | 工具/诊断 | 完整模型-动画关系，信息更全但更大。 |");
            sb.AppendLine("| `animation_bindings.jsonl` | 工具/诊断 | 按动画视角记录可匹配模型。 |");
            sb.AppendLine("| `character_assemblies.json` | 工具/高级用户 | 模块化角色组装关系，比如 face/hair/accessory。 |");
            sb.AppendLine("| `model_validation.json` | 工具/诊断 | glTF 静态结构验证结果。 |");
            sb.AppendLine("| `preview_validation.json` | 人/工具 | 单次预览 glTF 的验证结果。 |");
            sb.AppendLine("| `unity_relations.jsonl` | 工具/诊断 | Unity 原始关系图，通常很大，不建议人工直接看。 |");
            sb.AppendLine("| `unity_relation_summary.json` | 人/工具 | Unity 关系图摘要。 |");
            sb.AppendLine("| `skeletons.json` | 工具 | 骨架索引和 skeleton hash。 |");
            sb.AppendLine("| `export_manifest.jsonl` | 工具/诊断 | 导出文件和源资源关系。 |");
            sb.AppendLine();

            sb.AppendLine("## 当前库摘要");
            sb.AppendLine();
            sb.AppendLine($"- 模型数量: `{modelCount}`");
            sb.AppendLine($"- 动画数量: `{(compact?["animations"] as JArray)?.Count ?? 0}`");
            sb.AppendLine($"- 模块组装条目: `{(int?)assemblies?["assemblyCount"] ?? 0}`");
            sb.AppendLine();

            sb.AppendLine("## 设计原则");
            sb.AppendLine();
            sb.AppendLine("- 模型和动画默认分离：模型保持干净，动画独立入库。");
            sb.AppendLine("- 索引负责说明匹配关系：真正要看效果时，再生成预览 glTF/GLB。");
            sb.AppendLine("- 人读文件只保留入口和结论；机器读文件保留完整数据。");
            sb.AppendLine("- 遇到灰色材质、缺少 tint、模块未组装等问题，先看模型目录里的 `ASSET_README.md` 和 `MATERIAL_REPORT.md`。");
            sb.AppendLine();

            File.WriteAllText(Path.Combine(savePath, "LIBRARY_README.md"), sb.ToString(), Encoding.UTF8);
        }

        private static string BuildReadme(
            JObject model,
            string output,
            JObject compact,
            Dictionary<string, JObject> animationById,
            Dictionary<string, JArray> animationRefsByModel,
            Dictionary<string, JObject> assemblyByOutput)
        {
            var sb = new StringBuilder();
            var modelName = (string)model["name"] ?? Path.GetFileNameWithoutExtension(output);
            var modelId = FindCompactModelId(compact, output, modelName);

            sb.AppendLine($"# {modelName}");
            sb.AppendLine();
            sb.AppendLine("这份说明面向人工使用素材库。机器可读的完整关系仍以 `asset_catalog.jsonl`、`model_animations.json`、`character_assemblies.json`、glTF `extras` 和各类 validation/report JSON 为准。");
            sb.AppendLine();

            AppendBasicInfo(sb, model, output);
            AppendMaterialInfo(sb, output);
            AppendAnimationInfo(sb, modelId, animationById, animationRefsByModel);
            AppendAssemblyInfo(sb, output, assemblyByOutput);
            AppendUseAdvice(sb, model, output);

            return sb.ToString();
        }

        private static void AppendBasicInfo(StringBuilder sb, JObject model, string output)
        {
            sb.AppendLine("## 基础信息");
            sb.AppendLine();
            sb.AppendLine($"- 模型文件: `{Path.GetFileName(output)}`");
            sb.AppendLine($"- Library 角色: `{(string)model["libraryRole"] ?? "Unknown"}`");
            sb.AppendLine($"- 格式: `{(string)model["format"] ?? Path.GetExtension(output).TrimStart('.')}`");
            sb.AppendLine($"- Mesh: `{(int?)model["meshCount"] ?? 0}`");
            sb.AppendLine($"- 材质: `{(int?)model["materialCount"] ?? 0}`");
            sb.AppendLine($"- 贴图: `{(int?)model["textureCount"] ?? 0}`");
            sb.AppendLine($"- 骨骼: `{(int?)model["boneCount"] ?? 0}`");
            sb.AppendLine($"- Morph/BlendShape: `{(int?)model["morphCount"] ?? 0}`");
            if (!string.IsNullOrWhiteSpace((string)model["skeletonHash"]))
            {
                sb.AppendLine($"- SkeletonHash: `{(string)model["skeletonHash"]}`");
            }
            sb.AppendLine();
        }

        private static void AppendMaterialInfo(StringBuilder sb, string output)
        {
            sb.AppendLine("## 材质和贴图");
            sb.AppendLine();
            var materialReport = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output)) ?? string.Empty, "MATERIAL_REPORT.md");
            if (File.Exists(materialReport))
            {
                sb.AppendLine("- 详细说明: [MATERIAL_REPORT.md](MATERIAL_REPORT.md)");
            }

            var gltf = LoadJson(output);
            var materials = gltf?["materials"] as JArray;
            if (materials == null || materials.Count == 0)
            {
                sb.AppendLine("- 未读取到 glTF 材质信息。");
                sb.AppendLine();
                return;
            }

            foreach (var material in materials.OfType<JObject>())
            {
                var name = (string)material["name"] ?? "Material";
                var anime = material["extras"]?["animeStudioMaterial"] as JObject;
                var status = (string)anime?["status"];
                if (string.IsNullOrWhiteSpace(status))
                {
                    continue;
                }

                sb.AppendLine($"- `{name}`: `{status}`");
                if (status == "needsCustomizationTint")
                {
                    sb.AppendLine("  - 需要运行时 customization/tint 配置或人工烘焙颜色；当前灰色/中性色通常不是贴图丢失。");
                }
                else if (status == "bakedPreview")
                {
                    sb.AppendLine("  - 已生成预览颜色贴图。");
                }
            }
            sb.AppendLine();
        }

        private static void AppendAnimationInfo(
            StringBuilder sb,
            string modelId,
            Dictionary<string, JObject> animationById,
            Dictionary<string, JArray> animationRefsByModel)
        {
            sb.AppendLine("## 动画候选");
            sb.AppendLine();
            if (string.IsNullOrWhiteSpace(modelId)
                || !animationRefsByModel.TryGetValue(modelId, out var candidates)
                || candidates.Count == 0)
            {
                sb.AppendLine("- 没有在当前索引里找到可用动画候选。");
                sb.AppendLine("- 这不代表模型一定不能动，可能需要完整导出或解析更多 Animator/AnimationClip 关系。");
                sb.AppendLine();
                return;
            }

            foreach (var candidate in candidates.OfType<JObject>().Take(12))
            {
                var animationId = (string)candidate["animationId"];
                animationById.TryGetValue(animationId ?? string.Empty, out var animation);
                var name = (string)animation?["name"] ?? animationId ?? "Animation";
                var nextAction = (string)candidate["nextAction"] ?? "inspect";
                var capability = (string)candidate["animationCapability"] ?? "Unknown";
                var confidence = (string)candidate["confidence"] ?? "Unknown";
                sb.AppendLine($"- `{name}`");
                sb.AppendLine($"  - 能力: `{capability}`");
                sb.AppendLine($"  - 置信度: `{confidence}`");
                sb.AppendLine($"  - 建议动作: `{nextAction}`");
                if (candidate["matchReasons"] is JArray reasons)
                {
                    foreach (var reason in reasons.Values<string>().Take(2))
                    {
                        sb.AppendLine($"  - 依据: {reason}");
                    }
                }
            }
            if (candidates.Count > 12)
            {
                sb.AppendLine($"- 其余 `{candidates.Count - 12}` 条候选请查看 `model_animations.compact.json` 或 `model_animations.json`。");
            }
            sb.AppendLine();
        }

        private static void AppendAssemblyInfo(StringBuilder sb, string output, Dictionary<string, JObject> assemblyByOutput)
        {
            sb.AppendLine("## 模块和组装");
            sb.AppendLine();
            if (!assemblyByOutput.TryGetValue(NormalizePath(output), out var assembly))
            {
                sb.AppendLine("- 当前索引没有找到模块化组装信息。");
                sb.AppendLine("- 如果这个模型来自模块化角色，可能需要导出更多 body/face/hair/accessory 候选后重建索引。");
                sb.AppendLine();
                return;
            }

            sb.AppendLine($"- Family: `{(string)assembly["family"] ?? "Unknown"}`");
            sb.AppendLine($"- 推荐模块数: `{(int?)assembly["recommendedModuleCount"] ?? 0}`");
            AppendModules(sb, "推荐模块", assembly["recommendedModules"] as JArray);
            AppendModules(sb, "候选模块", assembly["moduleCandidates"] as JArray);
            sb.AppendLine();
        }

        private static void AppendModules(StringBuilder sb, string title, JArray modules)
        {
            if (modules == null || modules.Count == 0)
            {
                sb.AppendLine($"- {title}: 无");
                return;
            }

            sb.AppendLine($"- {title}:");
            foreach (var module in modules.OfType<JObject>().Take(12))
            {
                sb.AppendLine($"  - `{(string)module["role"] ?? "Module"}`: `{(string)module["name"] ?? "Unknown"}`");
                sb.AppendLine($"    - 可自动组装: `{((bool?)module["canAutoAssemble"] ?? false ? "yes" : "no")}`");
                if (!string.IsNullOrWhiteSpace((string)module["reason"]))
                {
                    sb.AppendLine($"    - 原因: {(string)module["reason"]}");
                }
            }
        }

        private static void AppendUseAdvice(StringBuilder sb, JObject model, string output)
        {
            sb.AppendLine("## 使用建议");
            sb.AppendLine();
            sb.AppendLine("- 默认优先打开 glTF/GLB 查看模型、材质、骨骼和预览动画。");
            sb.AppendLine("- 如果材质显示灰色或中性色，先看 `MATERIAL_REPORT.md`，不要直接判断为贴图丢失。");
            sb.AppendLine("- 如果需要换头、头发、附件，先看“模块和组装”部分；没有明确可自动组装关系时，不建议硬合并。");
            sb.AppendLine("- 如果需要动画，优先用“动画候选”里的建议动作生成或查看预览 glTF。");
            if (((int?)model["boneCount"] ?? 0) > 0)
            {
                sb.AppendLine("- 这是带骨骼模型，后续动画和模块组装都应保持 skeleton/joint 关系。");
            }
            sb.AppendLine();
        }

        private static JObject[] LoadCatalogModels(string catalogPath)
        {
            return File.ReadLines(catalogPath)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x =>
                {
                    try { return JObject.Parse(x); }
                    catch { return null; }
                })
                .Where(x => x != null && (string)x["kind"] == "Model")
                .ToArray();
        }

        private static JObject LoadJson(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }
            try
            {
                return JObject.Parse(File.ReadAllText(path));
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<string, JObject> BuildAnimationMap(JObject compact)
        {
            return ((compact?["animations"] as JArray)?
                .OfType<JObject>()
                .Where(x => !string.IsNullOrWhiteSpace((string)x["id"]))
                .GroupBy(x => (string)x["id"], StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase))
                ?? new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, JArray> BuildAnimationRefs(JObject compact)
        {
            return ((compact?["modelAnimationRefs"] as JArray)?
                .OfType<JObject>()
                .Where(x => !string.IsNullOrWhiteSpace((string)x["modelId"]))
                .GroupBy(x => (string)x["modelId"], StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    x => x.Key,
                    x =>
                    {
                        var merged = new JArray();
                        foreach (var item in x)
                        {
                            if (item["candidates"] is JArray candidates)
                            {
                                foreach (var candidate in candidates)
                                {
                                    merged.Add(candidate);
                                }
                            }
                        }
                        return merged;
                    },
                    StringComparer.OrdinalIgnoreCase))
                ?? new Dictionary<string, JArray>(StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, JObject> BuildAssemblyMap(JObject assemblies)
        {
            return ((assemblies?["assemblies"] as JArray)?
                .OfType<JObject>()
                .Where(x => !string.IsNullOrWhiteSpace((string)x["baseModel"]?["output"]))
                .GroupBy(x => NormalizePath((string)x["baseModel"]["output"]), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase))
                ?? new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        }

        private static string FindCompactModelId(JObject compact, string output, string name)
        {
            var normalizedOutput = NormalizePath(output);
            return (compact?["models"] as JArray)?
                .OfType<JObject>()
                .FirstOrDefault(x => string.Equals(NormalizePath((string)x["output"]), normalizedOutput, StringComparison.OrdinalIgnoreCase)
                    || string.Equals((string)x["name"], name, StringComparison.OrdinalIgnoreCase))?["id"]
                ?.ToString();
        }

        private static string NormalizePath(string path)
        {
            return string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
