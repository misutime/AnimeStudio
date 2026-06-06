using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AnimeStudio.CLI
{
    internal static class ModularPreviewAssembler
    {
        public static void Generate(
            string indexPath,
            string gameName,
            string modelSelector,
            string animationSelector,
            string outputDirectory,
            string sourceRootOverride,
            string moduleSelector)
        {
            var gltfPath = PreviewGltfGenerator.Generate(
                indexPath,
                gameName,
                modelSelector,
                animationSelector,
                outputDirectory,
                sourceRootOverride
            );
            if (string.IsNullOrWhiteSpace(gltfPath) || !File.Exists(gltfPath))
            {
                return;
            }

            var index = JObject.Parse(File.ReadAllText(indexPath));
            var catalogPath = (string)index["catalog"]
                ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(indexPath)) ?? Directory.GetCurrentDirectory(), "asset_catalog.jsonl");
            if (!File.Exists(catalogPath))
            {
                Logger.Warning($"asset_catalog.jsonl not found; assembled preview keeps base model only: {catalogPath}");
                return;
            }

            var baseModel = SelectModel(index, modelSelector);
            if (baseModel == null)
            {
                Logger.Warning("Unable to resolve selected model from model_animations.json; assembled preview keeps base model only.");
                return;
            }

            var baseInfo = baseModel["model"] as JObject;
            var requestedRoles = ParseRequestedRoles(moduleSelector);
            var catalog = ReadCatalog(catalogPath);
            var modules = SelectCompatibleModules(gltfPath, baseInfo, catalog, requestedRoles);
            var report = Assemble(gltfPath, baseInfo, modules);
            var reportPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(gltfPath)) ?? Directory.GetCurrentDirectory(), "assembly_report.json");
            File.WriteAllText(reportPath, JsonConvert.SerializeObject(report, Formatting.Indented));
            Logger.Info($"Assembled preview glTF: {gltfPath}");
            Logger.Info($"Assembly report: {reportPath}");
        }

        private static JObject SelectModel(JObject index, string modelSelector)
        {
            return index["models"]?
                .OfType<JObject>()
                .FirstOrDefault(model =>
                {
                    var modelInfo = model["model"] as JObject;
                    return Matches(modelSelector, (string)modelInfo?["name"], (string)modelInfo?["output"]);
                });
        }

        private static string[] ParseRequestedRoles(string moduleSelector)
        {
            var defaults = new[] { "Face", "Hair", "Accessory" };
            if (string.IsNullOrWhiteSpace(moduleSelector))
            {
                return defaults;
            }

            return moduleSelector
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();
        }

        private static List<JObject> ReadCatalog(string catalogPath)
        {
            return File.ReadLines(catalogPath)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(line =>
                {
                    try
                    {
                        return JObject.Parse(line);
                    }
                    catch
                    {
                        return null;
                    }
                })
                .Where(x => x != null && (string)x["kind"] == "Model")
                .ToList();
        }

        private static List<ModuleCandidate> SelectCompatibleModules(
            string baseGltfPath,
            JObject baseInfo,
            List<JObject> catalog,
            string[] requestedRoles)
        {
            var baseName = (string)baseInfo?["name"] ?? string.Empty;
            var family = GetCharacterFamily(baseName);
            var baseOutput = Path.GetFullPath(baseGltfPath);
            var baseNodeNames = LoadNodeNames(baseGltfPath);
            var selected = new List<ModuleCandidate>();

            foreach (var roleOrSelector in requestedRoles)
            {
                var role = NormalizeRole(roleOrSelector);
                var candidates = catalog
                    .Where(x => !string.Equals(Path.GetFullPath((string)x["output"] ?? string.Empty), baseOutput, StringComparison.OrdinalIgnoreCase))
                    .Where(x => File.Exists((string)x["output"]))
                    .Select(x => new ModuleCandidate(x, InferModuleRole((string)x["name"], x), GetCharacterFamily((string)x["name"] ?? string.Empty)))
                    .Where(x => ModuleMatchesRequest(x, roleOrSelector, role))
                    .Where(x => string.IsNullOrWhiteSpace(family) || string.Equals(x.Family, family, StringComparison.OrdinalIgnoreCase))
                    .Select(x => x with { Compatibility = AnalyzeCompatibility(baseNodeNames, (string)x.Catalog["output"]) })
                    .Where(x => x.Compatibility.CanAssemble)
                    .OrderByDescending(x => ModulePriority(x))
                    .ThenBy(x => (string)x.Catalog["name"], StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var best = candidates.FirstOrDefault();
                if (best != null)
                {
                    selected.Add(best);
                }
            }

            return selected
                .GroupBy(x => (string)x.Catalog["output"], StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToList();
        }

        private static object Assemble(string baseGltfPath, JObject baseInfo, List<ModuleCandidate> modules)
        {
            var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(baseGltfPath)) ?? Directory.GetCurrentDirectory();
            var baseGltf = JObject.Parse(File.ReadAllText(baseGltfPath));
            var reportModules = new List<object>();
            foreach (var module in modules)
            {
                var modulePath = (string)module.Catalog["output"];
                if (string.IsNullOrWhiteSpace(modulePath) || !File.Exists(modulePath))
                {
                    continue;
                }

                var result = MergeModule(baseGltf, baseDirectory, modulePath, module.Role);
                reportModules.Add(new
                {
                    role = module.Role,
                    name = (string)module.Catalog["name"],
                    output = modulePath,
                    compatibility = module.Compatibility,
                    assembly = result,
                });
            }

            baseGltf["asset"] ??= new JObject();
            baseGltf["asset"]["extras"] ??= new JObject();
            baseGltf["asset"]["extras"]["animeStudioAssembly"] = JObject.FromObject(new
            {
                generatedAt = DateTime.UtcNow.ToString("O"),
                rule = "Default Library stays modular. This preview non-destructively adds modules only when module skin joints remap to the base glTF skeleton by exact Unity node names.",
                baseModel = new
                {
                    name = (string)baseInfo?["name"],
                    output = (string)baseInfo?["output"],
                },
                modules = reportModules,
            });

            File.WriteAllText(baseGltfPath, JsonConvert.SerializeObject(baseGltf, Formatting.Indented));
            return new
            {
                generatedAt = DateTime.UtcNow.ToString("O"),
                rule = "Default Library output remains modular. This file is a generated viewing/validation preview assembled from compatible module assets.",
                gltf = baseGltfPath,
                baseModel = new
                {
                    name = (string)baseInfo?["name"],
                    output = (string)baseInfo?["output"],
                    skeletonHash = (string)baseInfo?["skeletonHash"],
                },
                requestedModuleCount = modules.Count,
                modules = reportModules,
                counts = new
                {
                    nodes = baseGltf["nodes"]?.Count() ?? 0,
                    meshes = baseGltf["meshes"]?.Count() ?? 0,
                    skins = baseGltf["skins"]?.Count() ?? 0,
                    animations = baseGltf["animations"]?.Count() ?? 0,
                    invalidChannels = CountInvalidAnimationChannels(baseGltf),
                    invalidSkinJoints = CountInvalidSkinJoints(baseGltf),
                },
            };
        }

        private static object MergeModule(JObject baseGltf, string baseDirectory, string modulePath, string role)
        {
            var moduleDirectory = Path.GetDirectoryName(Path.GetFullPath(modulePath)) ?? Directory.GetCurrentDirectory();
            var moduleGltf = JObject.Parse(File.ReadAllText(modulePath));
            var baseNodes = EnsureArray(baseGltf, "nodes");
            var baseNodeByName = baseNodes
                .OfType<JObject>()
                .Select((node, index) => new { name = (string)node["name"], index })
                .Where(x => !string.IsNullOrWhiteSpace(x.name))
                .GroupBy(x => x.name, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.First().index, StringComparer.Ordinal);

            var moduleNodes = moduleGltf["nodes"] as JArray ?? new JArray();
            var moduleToBaseJoint = moduleNodes
                .OfType<JObject>()
                .Select((node, index) => new { name = (string)node["name"], index })
                .Where(x => !string.IsNullOrWhiteSpace(x.name) && baseNodeByName.ContainsKey(x.name))
                .ToDictionary(x => x.index, x => baseNodeByName[x.name]);

            var prefix = SafeName($"{role}_{Path.GetFileNameWithoutExtension(modulePath)}");
            var bufferMap = CopyBuffers(baseGltf, moduleGltf, moduleDirectory, baseDirectory, prefix);
            var imageOffset = AppendArray(baseGltf, moduleGltf, "images", image =>
            {
                var uri = (string)image["uri"];
                if (!string.IsNullOrWhiteSpace(uri) && !uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    var newUri = $"{prefix}_{Path.GetFileName(uri)}";
                    CopyFileIfExists(Path.Combine(moduleDirectory, uri), Path.Combine(baseDirectory, newUri));
                    image["uri"] = newUri;
                }
            });
            var samplerOffset = AppendArray(baseGltf, moduleGltf, "samplers");
            var textureOffset = AppendArray(baseGltf, moduleGltf, "textures", texture =>
            {
                AddOffset(texture, "source", imageOffset);
                AddOffset(texture, "sampler", samplerOffset);
            });
            var materialOffset = AppendArray(baseGltf, moduleGltf, "materials", material => OffsetMaterialTextureIndexes(material, textureOffset));
            var bufferViewOffset = AppendArray(baseGltf, moduleGltf, "bufferViews", bufferView =>
            {
                var buffer = (int?)bufferView["buffer"];
                if (buffer != null && buffer.Value >= 0 && buffer.Value < bufferMap.Length)
                {
                    bufferView["buffer"] = bufferMap[buffer.Value];
                }
            });
            var accessorOffset = AppendArray(baseGltf, moduleGltf, "accessors", accessor => AddOffset(accessor, "bufferView", bufferViewOffset));
            var meshOffset = AppendArray(baseGltf, moduleGltf, "meshes", mesh =>
            {
                foreach (var primitive in mesh["primitives"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    foreach (var property in primitive["attributes"]?.OfType<JProperty>() ?? Enumerable.Empty<JProperty>())
                    {
                        property.Value = ((int)property.Value) + accessorOffset;
                    }
                    AddOffset(primitive, "indices", accessorOffset);
                    AddOffset(primitive, "material", materialOffset);
                    foreach (var target in primitive["targets"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                    {
                        foreach (var property in target.Properties().ToArray())
                        {
                            property.Value = ((int)property.Value) + accessorOffset;
                        }
                    }
                }
            });

            var skinMap = BuildSkinMap(baseGltf, moduleGltf, moduleToBaseJoint, accessorOffset);
            var baseRigIndex = FindBaseRigNode(baseNodes);
            var added = new List<object>();
            foreach (var item in moduleNodes.OfType<JObject>().Select((node, index) => new { node, index }))
            {
                if (item.node["mesh"] == null)
                {
                    continue;
                }
                var skin = (int?)item.node["skin"];
                if (skin != null && !skinMap.ContainsKey(skin.Value))
                {
                    continue;
                }

                var node = (JObject)item.node.DeepClone();
                node.Remove("children");
                node["name"] = $"{prefix}_{(string)item.node["name"] ?? $"node{item.index}"}";
                AddOffset(node, "mesh", meshOffset);
                if (skin != null)
                {
                    node["skin"] = skinMap[skin.Value];
                }

                var newIndex = baseNodes.Count;
                baseNodes.Add(node);
                AttachToBaseRig(baseGltf, baseRigIndex, newIndex);
                added.Add(new
                {
                    node = newIndex,
                    name = (string)node["name"],
                    mesh = (int?)node["mesh"],
                    skin = (int?)node["skin"],
                });
            }

            return new
            {
                addedMeshNodeCount = added.Count,
                added,
                skinCount = skinMap.Count,
            };
        }

        private static Compatibility AnalyzeCompatibility(HashSet<string> baseNodeNames, string modulePath)
        {
            try
            {
                var gltf = JObject.Parse(File.ReadAllText(modulePath));
                var nodes = gltf["nodes"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
                var skins = gltf["skins"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
                var meshNodes = nodes.Count(x => x["mesh"] != null);
                var requiredJointNames = skins
                    .SelectMany(x => x["joints"]?.Values<int>() ?? Enumerable.Empty<int>())
                    .Where(x => x >= 0 && x < nodes.Length)
                    .Select(x => (string)nodes[x]["name"])
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var missing = requiredJointNames
                    .Where(x => !baseNodeNames.Contains(x))
                    .Take(32)
                    .ToArray();

                return new Compatibility(
                    meshNodes > 0 && requiredJointNames.Length > 0 && missing.Length == 0,
                    meshNodes,
                    requiredJointNames.Length,
                    missing.Length,
                    missing
                );
            }
            catch (Exception ex)
            {
                return new Compatibility(false, 0, 0, 1, new[] { ex.Message });
            }
        }

        private static HashSet<string> LoadNodeNames(string gltfPath)
        {
            var gltf = JObject.Parse(File.ReadAllText(gltfPath));
            return gltf["nodes"]?
                .OfType<JObject>()
                .Select(x => (string)x["name"])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        }

        private static string InferModuleRole(string name, JObject catalog)
        {
            name ??= string.Empty;
            if (Regex.IsMatch(name, "(^|[_-])Face|Head", RegexOptions.IgnoreCase))
            {
                return "Face";
            }
            if (Regex.IsMatch(name, "(^|[_-])Hair", RegexOptions.IgnoreCase))
            {
                return "Hair";
            }
            if (Regex.IsMatch(name, "Accessori|Accessory|Mask|Choker|Cape|Cloth", RegexOptions.IgnoreCase))
            {
                return "Accessory";
            }
            return "Unknown";
        }

        private static bool ModuleMatchesRequest(ModuleCandidate candidate, string selector, string normalizedRole)
        {
            if (string.Equals(candidate.Role, normalizedRole, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return Matches(selector, (string)candidate.Catalog["name"], (string)candidate.Catalog["output"]);
        }

        private static string NormalizeRole(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "Unknown";
            }
            if (value.Contains("face", StringComparison.OrdinalIgnoreCase) || value.Contains("head", StringComparison.OrdinalIgnoreCase))
            {
                return "Face";
            }
            if (value.Contains("hair", StringComparison.OrdinalIgnoreCase))
            {
                return "Hair";
            }
            if (value.Contains("access", StringComparison.OrdinalIgnoreCase) || value.Contains("mask", StringComparison.OrdinalIgnoreCase) || value.Contains("cloth", StringComparison.OrdinalIgnoreCase))
            {
                return "Accessory";
            }
            return value.Trim();
        }

        private static int ModulePriority(ModuleCandidate candidate)
        {
            var name = (string)candidate.Catalog["name"] ?? string.Empty;
            var score = 0;
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
            score += ((int?)candidate.Catalog["textureCount"] ?? 0) * 10;
            score += ((int?)candidate.Catalog["meshCount"] ?? 0) * 5;
            score += candidate.Compatibility.RequiredJointCount;
            return score;
        }

        private static string GetCharacterFamily(string name)
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

        private static int[] CopyBuffers(JObject baseGltf, JObject moduleGltf, string moduleDirectory, string baseDirectory, string prefix)
        {
            var moduleBuffers = moduleGltf["buffers"] as JArray ?? new JArray();
            var baseBuffers = EnsureArray(baseGltf, "buffers");
            var map = new int[moduleBuffers.Count];
            for (var i = 0; i < moduleBuffers.Count; i++)
            {
                var buffer = (JObject)moduleBuffers[i].DeepClone();
                var uri = (string)buffer["uri"];
                if (!string.IsNullOrWhiteSpace(uri) && !uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    var newUri = $"{prefix}_{Path.GetFileName(uri)}";
                    CopyFileIfExists(Path.Combine(moduleDirectory, uri), Path.Combine(baseDirectory, newUri));
                    buffer["uri"] = newUri;
                }
                map[i] = baseBuffers.Count;
                baseBuffers.Add(buffer);
            }
            return map;
        }

        private static int AppendArray(JObject baseGltf, JObject moduleGltf, string name, Action<JObject> mutate = null)
        {
            var baseArray = EnsureArray(baseGltf, name);
            var moduleArray = moduleGltf[name] as JArray;
            var offset = baseArray.Count;
            if (moduleArray == null)
            {
                return offset;
            }

            foreach (var item in moduleArray.OfType<JObject>())
            {
                var clone = (JObject)item.DeepClone();
                mutate?.Invoke(clone);
                baseArray.Add(clone);
            }
            return offset;
        }

        private static Dictionary<int, int> BuildSkinMap(JObject baseGltf, JObject moduleGltf, Dictionary<int, int> moduleToBaseJoint, int accessorOffset)
        {
            var skins = moduleGltf["skins"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var baseSkins = EnsureArray(baseGltf, "skins");
            var map = new Dictionary<int, int>();
            for (var i = 0; i < skins.Length; i++)
            {
                var joints = skins[i]["joints"]?.Values<int>().ToArray() ?? Array.Empty<int>();
                if (joints.Length == 0 || joints.Any(x => !moduleToBaseJoint.ContainsKey(x)))
                {
                    continue;
                }
                var skin = (JObject)skins[i].DeepClone();
                skin["joints"] = new JArray(joints.Select(x => moduleToBaseJoint[x]));
                AddOffset(skin, "inverseBindMatrices", accessorOffset);
                var skeleton = (int?)skin["skeleton"];
                if (skeleton != null && moduleToBaseJoint.TryGetValue(skeleton.Value, out var mappedSkeleton))
                {
                    skin["skeleton"] = mappedSkeleton;
                }
                else
                {
                    skin["skeleton"] = moduleToBaseJoint[joints[0]];
                }
                map[i] = baseSkins.Count;
                baseSkins.Add(skin);
            }
            return map;
        }

        private static int FindBaseRigNode(JArray baseNodes)
        {
            var preferred = baseNodes
                .OfType<JObject>()
                .Select((node, index) => new { node, index })
                .FirstOrDefault(x =>
                    ((string)x.node["name"] ?? string.Empty).EndsWith("_LOD00_rig", StringComparison.OrdinalIgnoreCase)
                    || ((string)x.node["name"] ?? string.Empty).Contains("Standard", StringComparison.OrdinalIgnoreCase)
                );
            return preferred?.index ?? -1;
        }

        private static void AttachToBaseRig(JObject baseGltf, int baseRigIndex, int newNodeIndex)
        {
            if (baseRigIndex >= 0)
            {
                var nodes = (JArray)baseGltf["nodes"];
                var rig = (JObject)nodes[baseRigIndex];
                rig["children"] ??= new JArray();
                ((JArray)rig["children"]).Add(newNodeIndex);
                return;
            }

            var scenes = EnsureArray(baseGltf, "scenes");
            var sceneIndex = (int?)baseGltf["scene"] ?? 0;
            if (sceneIndex < 0 || sceneIndex >= scenes.Count)
            {
                sceneIndex = 0;
            }
            var scene = (JObject)scenes[sceneIndex];
            scene["nodes"] ??= new JArray();
            ((JArray)scene["nodes"]).Add(newNodeIndex);
        }

        private static int CountInvalidAnimationChannels(JObject gltf)
        {
            var nodeCount = gltf["nodes"]?.Count() ?? 0;
            return gltf["animations"]?
                .OfType<JObject>()
                .SelectMany(x => x["channels"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                .Count(x =>
                {
                    var node = (int?)x["target"]?["node"];
                    return node == null || node < 0 || node >= nodeCount;
                }) ?? 0;
        }

        private static int CountInvalidSkinJoints(JObject gltf)
        {
            var nodeCount = gltf["nodes"]?.Count() ?? 0;
            return gltf["skins"]?
                .OfType<JObject>()
                .SelectMany(x => x["joints"]?.Values<int>() ?? Enumerable.Empty<int>())
                .Count(x => x < 0 || x >= nodeCount) ?? 0;
        }

        private static void OffsetMaterialTextureIndexes(JToken token, int textureOffset, string parentName = null)
        {
            if (token is JObject obj)
            {
                if (parentName != null
                    && parentName.IndexOf("Texture", StringComparison.OrdinalIgnoreCase) >= 0
                    && obj["index"] != null)
                {
                    obj["index"] = ((int)obj["index"]) + textureOffset;
                }
                foreach (var property in obj.Properties().ToArray())
                {
                    OffsetMaterialTextureIndexes(property.Value, textureOffset, property.Name);
                }
            }
            else if (token is JArray array)
            {
                foreach (var item in array)
                {
                    OffsetMaterialTextureIndexes(item, textureOffset, parentName);
                }
            }
        }

        private static void AddOffset(JObject obj, string property, int offset)
        {
            if (obj[property] != null)
            {
                obj[property] = ((int)obj[property]) + offset;
            }
        }

        private static JArray EnsureArray(JObject obj, string property)
        {
            obj[property] ??= new JArray();
            return (JArray)obj[property];
        }

        private static void CopyFileIfExists(string source, string destination)
        {
            if (!File.Exists(source))
            {
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? Directory.GetCurrentDirectory());
            if (!File.Exists(destination))
            {
                File.Copy(source, destination);
            }
        }

        private static bool Matches(string selector, params string[] values)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                return true;
            }
            if (values.Any(x => string.Equals(x, selector, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            try
            {
                var regex = new Regex(selector, RegexOptions.IgnoreCase);
                return values.Where(x => !string.IsNullOrWhiteSpace(x)).Any(x => regex.IsMatch(x));
            }
            catch (ArgumentException)
            {
                return values.Where(x => !string.IsNullOrWhiteSpace(x)).Any(x => x.IndexOf(selector, StringComparison.OrdinalIgnoreCase) >= 0);
            }
        }

        private static string SafeName(string value)
        {
            var name = string.IsNullOrWhiteSpace(value) ? "module" : value;
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        private sealed record ModuleCandidate(JObject Catalog, string Role, string Family)
        {
            public Compatibility Compatibility { get; init; } = new(false, 0, 0, 0, Array.Empty<string>());
        }

        private sealed record Compatibility(
            bool CanAssemble,
            int MeshNodeCount,
            int RequiredJointCount,
            int MissingJointCount,
            string[] MissingJointNames);
    }
}
