using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AnimeStudio.CLI
{
    internal static class CharacterAssemblyIndexGenerator
    {
        public static bool AnnotateModelCompleteness(IReadOnlyList<JObject> models)
        {
            if (models == null || models.Count == 0)
            {
                return false;
            }

            var modelRows = models
                .Where(x => (string)x["kind"] == "Model" || x["kind"] == null)
                .Where(x => !string.IsNullOrWhiteSpace((string)x["output"]))
                .ToArray();
            var modules = modelRows
                .Select(x => new ModuleInfo(x, InferModuleRole((string)x["name"], x), GetFamily((string)x["name"])))
                .Where(x => !string.Equals(x.Role, "Body", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(x.Role, "Unknown", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            var changed = false;
            foreach (var model in modelRows.Where(IsCharacterBase))
            {
                if (!LooksLikeModularCharacterPart(model))
                {
                    continue;
                }

                var family = GetFamily((string)model["name"]);
                var sameFamilyModules = modules
                    .Where(x => string.Equals(x.Family, family, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var missingRoles = new List<string>();
                if (!sameFamilyModules.Any(x => string.Equals(x.Role, "Face", StringComparison.OrdinalIgnoreCase)))
                {
                    missingRoles.Add("Face");
                }
                if (!sameFamilyModules.Any(x => string.Equals(x.Role, "Hair", StringComparison.OrdinalIgnoreCase)))
                {
                    missingRoles.Add("Hair");
                }

                var status = missingRoles.Count > 0 ? "modular_incomplete" : "modular_base";
                changed |= SetCatalogValue(model, "libraryRole", "ModularCharacterBase");
                changed |= SetCatalogValue(model, "resourceKind", "CharacterPart");
                changed |= SetCatalogValue(model, "characterAssemblyRole", "Body");
                changed |= SetCatalogValue(model, "characterAssemblyFamily", family);
                changed |= SetCatalogValue(model, "modelCompletenessStatus", status);
                changed |= SetCatalogValue(model, "modelCompletenessMissingRoles", new JArray(missingRoles));
                changed |= SetCatalogValue(model, "modelCompletenessRule", missingRoles.Count > 0
                    ? "该模型是模块化角色 body/base，可作为素材部件使用；缺少 face/hair 等模块时不能当完整角色或动画验收样本。"
                    : "该模型是模块化角色 body/base；仍应通过 character_assemblies.json 组装预览后再作为完整角色验收。");
            }

            return changed;
        }

        public static void Generate(string savePath, List<JObject> models)
        {
            var modelRows = models
                .Where(x => (string)x["kind"] == "Model" || x["kind"] == null)
                .Where(x => !string.IsNullOrWhiteSpace((string)x["output"]))
                .ToArray();
            var bases = modelRows
                .Where(IsCharacterBase)
                .OrderBy(x => (string)x["name"], StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var modules = modelRows
                .Select(x => new ModuleInfo(x, InferModuleRole((string)x["name"], x), GetFamily((string)x["name"])))
                .Where(x => !string.Equals(x.Role, "Body", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(x.Role, "Unknown", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            var assemblies = bases.Select(baseModel =>
            {
                var family = GetFamily((string)baseModel["name"]);
                var baseJointNames = GetRemapTargetNames(baseModel);
                var compatibleModules = modules
                    .Where(x => string.Equals(x.Family, family, StringComparison.OrdinalIgnoreCase))
                    .Select(x => BuildModuleCandidate(x, baseJointNames))
                    .OrderBy(x => (string)x["role"], StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(x => (int)x["score"])
                    .ThenBy(x => (string)x["name"], StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var recommended = compatibleModules
                    .Where(x => (bool)x["canAutoAssemble"])
                    .GroupBy(x => (string)x["role"], StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.First())
                    .ToArray();

                return new
                {
                    baseModel = BuildAsset(baseModel),
                    family,
                    rule = "Modules stay separate in Library. Preview assembly may add a module only when its skin/bone joints can be remapped to the base skeleton by exact Unity joint names.",
                    recommendedModuleCount = recommended.Length,
                    recommendedModules = recommended,
                    moduleCandidateCount = compatibleModules.Length,
                    moduleCandidates = compatibleModules.Take(64).ToArray(),
                };
            }).ToArray();

            var report = new
            {
                generatedAt = DateTime.UtcNow.ToString("O"),
                version = 1,
                rule = "Default Library output is modular. This index records safe preview/build combinations for modular characters without merging source assets.",
                assemblyBasis = new[]
                {
                    "Unity exported node/bone paths",
                    "exact joint-name remap from module skin joints to base skeleton",
                    "same character family token when no stronger Unity customization table has been parsed yet",
                },
                limitation = "Modules with independent simulation/attachment bones are indexed but not auto-assembled until their Unity attachment transform or customization table is parsed.",
                assemblyCount = assemblies.Length,
                assemblies,
            };
            File.WriteAllText(
                Path.Combine(savePath, "character_assemblies.json"),
                JsonConvert.SerializeObject(report, Formatting.Indented)
            );
        }

        private static JObject BuildModuleCandidate(ModuleInfo module, HashSet<string> baseJointNames)
        {
            var moduleJointNames = GetJointNames(module.Catalog);
            var missing = moduleJointNames
                .Where(x => !baseJointNames.Contains(x))
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();
            var hasSkinJoints = moduleJointNames.Count > 0;
            var diagnosticOnly = IsDiagnosticModule(module.Catalog);
            var canAutoAssemble = !diagnosticOnly && hasSkinJoints && missing.Length == 0 && ((int?)module.Catalog["meshCount"] ?? 0) > 0;
            var reason = canAutoAssemble
                ? "All module skin/bone joints can be remapped to the base skeleton by exact Unity joint names."
                : diagnosticOnly
                    ? "Module is diagnosticOnly; keep it as a manual preview candidate even if joint names look compatible."
                : !hasSkinJoints
                    ? "Module has no skin/bone joints in the catalog; it needs an explicit Unity attachment transform before auto assembly."
                    : "Module has independent or missing joints; keep it modular until attachment/simulation relations are parsed.";
            return JObject.FromObject(new
            {
                role = module.Role,
                name = (string)module.Catalog["name"],
                output = (string)module.Catalog["output"],
                skeletonHash = (string)module.Catalog["skeletonHash"],
                meshCount = (int?)module.Catalog["meshCount"] ?? 0,
                textureCount = (int?)module.Catalog["textureCount"] ?? 0,
                boneCount = (int?)module.Catalog["boneCount"] ?? 0,
                diagnosticOnly,
                canAutoAssemble,
                score = ScoreModule(module, canAutoAssemble),
                remap = new
                {
                    requiredJointCount = moduleJointNames.Count,
                    missingJointCount = missing.Length,
                    missingJointNames = missing.Take(32).ToArray(),
                    basis = "exact Unity joint node name",
                },
                reason,
            });
        }

        private static bool IsDiagnosticModule(JObject model)
        {
            // 诊断模型可以帮助人工看坐标、贴图和骨骼线索，但不能进入默认自动装配推荐。
            if ((bool?)model["diagnosticOnly"] == true)
            {
                return true;
            }

            var libraryRole = (string)model["libraryRole"] ?? string.Empty;
            return libraryRole.Contains("Diagnostic", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCharacterBase(JObject model)
        {
            var role = InferModuleRole((string)model["name"], model);
            if (!string.Equals(role, "Body", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return ((int?)model["boneCount"] ?? 0) >= 30
                && ((int?)model["meshCount"] ?? 0) > 0;
        }

        private static bool LooksLikeModularCharacterPart(JObject model)
        {
            var output = ((string)model["output"] ?? string.Empty).Replace('\\', '/');
            var source = ((string)model["source"] ?? string.Empty).Replace('\\', '/');
            var container = ((string)model["container"] ?? string.Empty).Replace('\\', '/');
            var text = $"{output}\n{source}\n{container}";
            return Regex.IsMatch(text, @"(^|/)actor_visual_parts?(/|$)", RegexOptions.IgnoreCase);
        }

        private static string InferModuleRole(string name, JObject model)
        {
            name ??= string.Empty;
            var meshPaths = string.Join("\n", model["meshPaths"]?.Values<string>() ?? Enumerable.Empty<string>());
            if (Regex.IsMatch(name, "(^|[_-])Face|Head", RegexOptions.IgnoreCase)
                || Regex.IsMatch(meshPaths, "Face|Head|Eyebrow", RegexOptions.IgnoreCase))
            {
                return "Face";
            }
            if (Regex.IsMatch(name, "(^|[_-])Hair", RegexOptions.IgnoreCase)
                || Regex.IsMatch(meshPaths, "Hair", RegexOptions.IgnoreCase))
            {
                return "Hair";
            }
            if (Regex.IsMatch(name, "Accessori|Accessory|Mask|Choker|Cape|Cloth", RegexOptions.IgnoreCase))
            {
                return "Accessory";
            }
            if (Regex.IsMatch(name, "Customization|Standard|Body|LOD00_rig|ingame|outgame", RegexOptions.IgnoreCase)
                || Regex.IsMatch(meshPaths, "Body|Cape", RegexOptions.IgnoreCase))
            {
                return "Body";
            }
            return "Unknown";
        }

        private static int ScoreModule(ModuleInfo module, bool canAutoAssemble)
        {
            var name = (string)module.Catalog["name"] ?? string.Empty;
            var score = canAutoAssemble ? 10000 : 0;
            if (name.Contains("Standard", StringComparison.OrdinalIgnoreCase))
            {
                score += 1000;
            }
            if (name.Contains("Prefab", StringComparison.OrdinalIgnoreCase))
            {
                score += 500;
            }
            if (name.Contains("LOD00", StringComparison.OrdinalIgnoreCase))
            {
                score += 100;
            }
            score += ((int?)module.Catalog["textureCount"] ?? 0) * 10;
            score += ((int?)module.Catalog["meshCount"] ?? 0) * 5;
            return score;
        }

        private static object BuildAsset(JObject model)
        {
            return new
            {
                name = (string)model["name"],
                output = (string)model["output"],
                source = (string)model["source"],
                skeletonHash = (string)model["skeletonHash"],
                meshCount = (int?)model["meshCount"] ?? 0,
                textureCount = (int?)model["textureCount"] ?? 0,
                boneCount = (int?)model["boneCount"] ?? 0,
                completenessStatus = (string)model["modelCompletenessStatus"],
                missingRoles = model["modelCompletenessMissingRoles"]?.Values<string>().ToArray() ?? Array.Empty<string>(),
            };
        }

        private static HashSet<string> GetJointNames(JObject model)
        {
            return (model["bonePaths"]?.Values<string>() ?? Enumerable.Empty<string>())
                .Select(LastPathPart)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.Ordinal);
        }

        private static HashSet<string> GetRemapTargetNames(JObject model)
        {
            return (model["bonePaths"]?.Values<string>() ?? Enumerable.Empty<string>())
                .Concat(model["nodePaths"]?.Values<string>() ?? Enumerable.Empty<string>())
                .Select(LastPathPart)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.Ordinal);
        }

        private static string LastPathPart(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }
            var normalized = path.Replace('\\', '/');
            var slash = normalized.LastIndexOf('/');
            return slash < 0 ? normalized : normalized[(slash + 1)..];
        }

        private static string GetFamily(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }
            var match = Regex.Match(name, "(VampireFemale|VampireMale|[A-Za-z]+_\\d{2}_\\d{2})", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Value;
            }
            var parts = name.Split(new[] { '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? string.Empty : parts[0];
        }

        private static bool SetCatalogValue(JObject model, string key, JToken value)
        {
            var current = model[key];
            if (JToken.DeepEquals(current, value))
            {
                return false;
            }
            model[key] = value;
            return true;
        }

        private sealed record ModuleInfo(JObject Catalog, string Role, string Family);
    }
}
