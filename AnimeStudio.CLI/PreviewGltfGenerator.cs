using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AnimeStudio.CLI
{
    internal static class PreviewGltfGenerator
    {
        public static string Generate(
            string indexPath,
            string gameName,
            string modelSelector,
            string animationSelector,
            string outputDirectory,
            string sourceRootOverride = null
        )
        {
            if (string.IsNullOrWhiteSpace(gameName))
            {
                Logger.Error("--game is required with --generate_preview_gltf.");
                return null;
            }
            if (string.IsNullOrWhiteSpace(indexPath) || !File.Exists(indexPath))
            {
                Logger.Error($"model_animations.json not found: {indexPath}");
                return null;
            }

            var index = JObject.Parse(File.ReadAllText(indexPath));
            var selection = SelectPreview(index, modelSelector, animationSelector);
            if (selection == null)
            {
                if (IsDeferredLargeLibraryIndex(index))
                {
                    var libraryRoot = Path.GetDirectoryName(Path.GetFullPath(indexPath)) ?? "";
                    var dbPath = Path.Combine(libraryRoot, "library_index.db");
                    if (File.Exists(dbPath))
                    {
                        Logger.Info("model_animations.json is a deferred large-Library stub; selecting preview candidate from sibling library_index.db.");
                        selection = SelectPreviewFromLibraryDb(dbPath, modelSelector, animationSelector);
                        if (selection != null)
                        {
                            if (!IsSelectionModelReadyForAnimation(selection, out var deferredBlockedReason))
                            {
                                Logger.Error("Selected deferred SQLite preview candidate is blocked by the model-first animation gate.");
                                Logger.Error(deferredBlockedReason);
                                return null;
                            }
                            ResolveSelectionLibraryPaths(selection, libraryRoot);
                            var deferredSourceIndex = ResolveExistingSourceIndex(libraryRoot);
                            return GenerateSelection(dbPath, gameName, selection, outputDirectory, sourceRootOverride, deferredSourceIndex);
                        }
                    }
                }
                Logger.Error("No model/animation candidate matched the preview selectors.");
                return null;
            }

            if (!IsSelectionModelReadyForAnimation(selection, out var blockedReason))
            {
                Logger.Error("Selected preview candidate is blocked by the model-first animation gate.");
                Logger.Error(blockedReason);
                return null;
            }

            ResolveSelectionLibraryPaths(selection, Path.GetDirectoryName(Path.GetFullPath(indexPath)) ?? "");
            var sourceIndex = ResolveExistingSourceIndex(Path.GetDirectoryName(Path.GetFullPath(indexPath)) ?? "");
            return GenerateSelection(indexPath, gameName, selection, outputDirectory, sourceRootOverride, sourceIndex);
        }

        private static bool IsDeferredLargeLibraryIndex(JObject index)
        {
            return string.Equals((string)index?["matchingDeferred"], "true", StringComparison.OrdinalIgnoreCase) ||
                (bool?)index?["matchingDeferred"] == true;
        }

        public static string GenerateFromLibrary(
            string libraryRoot,
            string gameName,
            string modelSelector,
            string animationSelector,
            string outputDirectory,
            string sourceRootOverride = null
        )
        {
            if (string.IsNullOrWhiteSpace(gameName))
            {
                Logger.Error("--game is required with --generate_preview_from_library.");
                return null;
            }
            if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
            {
                Logger.Error($"Library root not found: {libraryRoot}");
                return null;
            }

            var dbPath = Path.Combine(libraryRoot, "library_index.db");
            modelSelector = NormalizeLibrarySelector(libraryRoot, modelSelector);
            animationSelector = NormalizeLibrarySelector(libraryRoot, animationSelector);
            if (!File.Exists(dbPath))
            {
                Logger.Error($"library_index.db not found: {dbPath}. Rebuild the Library export or run --build_sqlite_index.");
                return null;
            }

            var selection = SelectPreviewFromLibraryDb(dbPath, modelSelector, animationSelector);
            if (selection == null)
            {
                Logger.Error("No model/animation matched the SQLite preview selectors.");
                return null;
            }
            if (!IsSelectionModelReadyForAnimation(selection, out var blockedReason))
            {
                Logger.Error("Selected SQLite preview candidate is blocked by the model-first animation gate.");
                Logger.Error(blockedReason);
                return null;
            }

            ResolveSelectionLibraryPaths(selection, libraryRoot);
            var sourceIndex = ResolveExistingSourceIndex(libraryRoot);
            return GenerateSelection(dbPath, gameName, selection, outputDirectory, sourceRootOverride, sourceIndex);
        }

        public static string ExportStandaloneAnimationFromLibrary(
            string libraryRoot,
            string gameName,
            string modelSelector,
            string animationSelector,
            string outputDirectory,
            bool forceInternalHumanoidSolve = false)
        {
            if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
            {
                Logger.Error($"Library root not found: {libraryRoot}");
                return null;
            }

            var dbPath = Path.Combine(libraryRoot, "library_index.db");
            if (!File.Exists(dbPath))
            {
                Logger.Error($"library_index.db not found: {dbPath}. Rebuild the Library export or run --build_sqlite_index.");
                return null;
            }

            var selection = SelectPreviewFromLibraryDb(dbPath, modelSelector, animationSelector);
            if (selection == null)
            {
                Logger.Error("No model/animation matched the SQLite animation glTF selectors.");
                return null;
            }
            if (!IsExplicitPreviewRelation(selection.Animation))
            {
                Logger.Error("Selected animation glTF candidate is not a Unity explicit model-animation relation.");
                return null;
            }
            if (!IsSelectionModelReadyForAnimation(selection, out var blockedReason))
            {
                Logger.Error("Selected animation glTF candidate is blocked by the model-first animation gate.");
                Logger.Error(blockedReason);
                return null;
            }

            ResolveSelectionLibraryPaths(selection, libraryRoot);
            var modelName = (string)selection.Model?["model"]?["name"];
            var animationName = (string)selection.Animation?["name"];
            var output = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(
                    Path.GetFullPath(libraryRoot),
                    "AnimationGltf",
                    $"{SafeName(modelName ?? "Model")}__{SafeName(animationName ?? "Animation")}"
                )
                : outputDirectory;

            if (FastPreviewGltfBuilder.TryExportStandaloneAnimation(
                selection.Model,
                selection.Animation,
                animationName,
                output,
                out var gltfPath,
                out var message,
                forceInternalHumanoidSolve))
            {
                var report = new JObject
                {
                    ["status"] = "ok",
                    ["game"] = gameName ?? string.Empty,
                    ["model"] = selection.Model?["model"],
                    ["animation"] = selection.Animation,
                    ["gltf"] = gltfPath,
                    ["message"] = message,
                    ["forceInternalHumanoidSolve"] = forceInternalHumanoidSolve,
                    ["rule"] = forceInternalHumanoidSolve
                        ? "独立动画 glTF 只从 relation_source=explicit 的 Unity 确定性候选生成；当前资产保留模型 node/rest TRS 并移除 mesh/material/texture/skin，使用 AnimeStudio 内部 Humanoid/Muscle 实验求解器写出 TRS channel。结果必须保留 notProductionReady，直到视觉验收和公式校准通过。"
                        : "独立动画 glTF 只从 relation_source=explicit 的 Unity 确定性候选生成；当前资产保留模型 node/rest TRS 并移除 mesh/material/texture/skin，只写 AnimeStudio decoded 的直接 TRS channel。",
                };
                var reportPath = Path.Combine(output, "standalone_animation_gltf_report.json");
                File.WriteAllText(reportPath, report.ToString(Formatting.Indented));
                Logger.Info(message);
                Logger.Info($"Standalone animation glTF: {gltfPath}");
                Logger.Info($"Standalone animation report: {reportPath}");
                return gltfPath;
            }

            Logger.Error($"Standalone animation glTF export failed: {message}");
            return null;
        }

        public static string MergeStandaloneAnimationGltf(
            string modelGltfPath,
            string animationGltfPath,
            string outputDirectory)
        {
            if (string.IsNullOrWhiteSpace(modelGltfPath) || !File.Exists(modelGltfPath))
            {
                Logger.Error($"Model glTF not found: {modelGltfPath}");
                return null;
            }
            if (string.IsNullOrWhiteSpace(animationGltfPath) || !File.Exists(animationGltfPath))
            {
                Logger.Error("--preview_animation must point to a skinless standalone animation glTF.");
                return null;
            }
            if (!string.Equals(Path.GetExtension(modelGltfPath), ".gltf", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetExtension(animationGltfPath), ".gltf", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Error("--merge_animation_gltf currently accepts .gltf files with external buffers. Convert GLB to glTF first if needed.");
                return null;
            }

            var modelDir = Path.GetDirectoryName(Path.GetFullPath(modelGltfPath)) ?? Directory.GetCurrentDirectory();
            var animationDir = Path.GetDirectoryName(Path.GetFullPath(animationGltfPath)) ?? Directory.GetCurrentDirectory();
            var modelName = Path.GetFileNameWithoutExtension(modelGltfPath);
            var animationName = Path.GetFileNameWithoutExtension(animationGltfPath);
            var requestedOutput = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(modelDir, "MergedAnimations", $"{SafeName(modelName)}__{SafeName(animationName)}")
                : outputDirectory;
            var requestedOutputExtension = Path.GetExtension(requestedOutput);
            var writesExplicitGltfFile = string.Equals(requestedOutputExtension, ".gltf", StringComparison.OrdinalIgnoreCase);
            var output = writesExplicitGltfFile
                ? Path.GetDirectoryName(Path.GetFullPath(requestedOutput)) ?? Directory.GetCurrentDirectory()
                : requestedOutput;
            var outputGltf = writesExplicitGltfFile
                ? Path.GetFullPath(requestedOutput)
                : Path.Combine(output, $"{SafeName(modelName)}__{SafeName(animationName)}.merged.gltf");

            // 只有默认“输出目录”模式可以清空目录；显式 .gltf 路径的父目录可能还放着别的验收文件。
            if (!writesExplicitGltfFile && Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
            Directory.CreateDirectory(output);

            if (!TryValidateModelGltfForAnimation(modelGltfPath, out var modelGate))
            {
                if (File.Exists(outputGltf))
                {
                    File.Delete(outputGltf);
                }

                var reportPath = Path.Combine(output, "merge_animation_gltf_report.json");
                File.WriteAllText(reportPath, new JObject
                {
                    ["status"] = "blocked",
                    ["reason"] = "model_not_animation_ready",
                    ["modelGltf"] = modelGltfPath,
                    ["animationGltf"] = animationGltfPath,
                    ["outputGltf"] = outputGltf,
                    ["modelGate"] = modelGate,
                    ["rule"] = "模型 glTF 单独通过 Mesh/UV/材质/贴图/skin/bbox 静态验收后，才允许合并独立动画。白模、缺材质贴图或 skin 异常只能作为诊断样本。",
                }.ToString(Formatting.Indented));
                Logger.Error($"Merge blocked by model-first gate: {reportPath}");
                return null;
            }

            var model = JObject.Parse(File.ReadAllText(modelGltfPath));
            var animation = JObject.Parse(File.ReadAllText(animationGltfPath));
            var merged = (JObject)model.DeepClone();
            var copyNotes = new JArray();
            CopyGltfUriResources(merged, modelDir, output, null, copyNotes, compactNames: true);

            var modelNodeMap = BuildGltfNodePathMap(merged);
            var animationNodeMap = BuildGltfNodePathMap(animation);
            var animationIndexToPath = animationNodeMap
                .GroupBy(x => x.Value)
                .ToDictionary(x => x.Key, x => x.OrderBy(y => y.Key.Length).First().Key);

            var bufferOffset = ArrayCount(merged, "buffers");
            var bufferViewOffset = ArrayCount(merged, "bufferViews");
            var accessorOffset = ArrayCount(merged, "accessors");
            AppendAnimationBuffers(merged, animation, animationDir, output, animationName, copyNotes);
            AppendOffsetArray(merged, animation, "bufferViews", item =>
            {
                if ((int?)item["buffer"] is int buffer)
                {
                    item["buffer"] = buffer + bufferOffset;
                }
            });
            AppendOffsetArray(merged, animation, "accessors", item =>
            {
                if ((int?)item["bufferView"] is int bufferView)
                {
                    item["bufferView"] = bufferView + bufferViewOffset;
                }
            });

            var mergedAnimations = EnsureJArray(merged, "animations");
            var missingNodes = new JArray();
            var channelCount = 0;
            foreach (var sourceAnimation in animation["animations"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var animationClone = (JObject)sourceAnimation.DeepClone();
                foreach (var sampler in animationClone["samplers"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    if ((int?)sampler["input"] is int input)
                    {
                        sampler["input"] = input + accessorOffset;
                    }
                    if ((int?)sampler["output"] is int outputAccessor)
                    {
                        sampler["output"] = outputAccessor + accessorOffset;
                    }
                }

                foreach (var channel in animationClone["channels"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    channelCount++;
                    var target = channel["target"] as JObject;
                    var oldNodeIndex = (int?)target?["node"];
                    if (target == null || oldNodeIndex == null)
                    {
                        missingNodes.Add(new JObject
                        {
                            ["reason"] = "missing_channel_target_node",
                            ["channelIndex"] = channelCount - 1,
                        });
                        continue;
                    }

                    if (!animationIndexToPath.TryGetValue(oldNodeIndex.Value, out var animationNodePath)
                        || !TryMapGltfNode(modelNodeMap, animationNodePath, out var newNodeIndex))
                    {
                        missingNodes.Add(new JObject
                        {
                            ["reason"] = "target_node_not_found_in_model",
                            ["channelIndex"] = channelCount - 1,
                            ["animationNodeIndex"] = oldNodeIndex.Value,
                            ["animationNodePath"] = animationNodePath,
                        });
                        continue;
                    }

                    target["node"] = newNodeIndex;
                }

                animationClone["extras"] ??= new JObject();
                animationClone["extras"]["animeStudioMergedAnimation"] = new JObject
                {
                    ["sourceAnimationGltf"] = animationGltfPath,
                    ["sourceModelGltf"] = modelGltfPath,
                    ["mode"] = "MergeStandaloneAnimationGltf",
                    ["nodeMapping"] = "animation glTF channel targets are remapped to model glTF nodes by full node path, then unique suffix/leaf fallback",
                };
                mergedAnimations.Add(animationClone);
            }

            if (missingNodes.Count > 0)
            {
                var reportPath = Path.Combine(output, "merge_animation_gltf_report.json");
                File.WriteAllText(reportPath, new JObject
                {
                    ["status"] = "failed",
                    ["reason"] = "animation_channel_target_nodes_missing",
                    ["modelGltf"] = modelGltfPath,
                    ["animationGltf"] = animationGltfPath,
                    ["missingNodes"] = missingNodes,
                    ["copiedResources"] = copyNotes,
                }.ToString(Formatting.Indented));
                Logger.Error($"Merge failed: {missingNodes.Count} animation channel target node(s) could not be mapped to the model. Report: {reportPath}");
                return null;
            }

            merged["extras"] ??= new JObject();
            var mergeAssessment = AssessStandaloneAnimationForMerge(animation, channelCount);
            var hiddenLodMeshes = HideNonPrimaryLodMeshes(merged["nodes"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>());
            merged["extras"]["animeStudioMergedAnimation"] = new JObject
            {
                ["mode"] = "MergeStandaloneAnimationGltf",
                ["sourceModelGltf"] = modelGltfPath,
                ["sourceAnimationGltf"] = animationGltfPath,
                ["animationCountAdded"] = animation["animations"]?.Count() ?? 0,
                ["channelCount"] = channelCount,
                ["hiddenNonPrimaryLodMeshCount"] = hiddenLodMeshes.Count,
                ["validationStatus"] = mergeAssessment.Status,
                ["validationReasons"] = mergeAssessment.Reasons,
                ["rule"] = "只合并已经导出的 standalone animation glTF，不重新猜模型-动画关系；节点无法确定匹配时失败。",
            };

            RemoveEmptyTopLevelArrays(merged);
            File.WriteAllText(outputGltf, merged.ToString(Formatting.Indented));
            var okReportPath = Path.Combine(output, "merge_animation_gltf_report.json");
            File.WriteAllText(okReportPath, new JObject
            {
                ["status"] = mergeAssessment.Status,
                ["modelGltf"] = modelGltfPath,
                ["animationGltf"] = animationGltfPath,
                ["outputGltf"] = outputGltf,
                ["animationCountAdded"] = animation["animations"]?.Count() ?? 0,
                ["channelCount"] = channelCount,
                ["hiddenNonPrimaryLodMeshCount"] = hiddenLodMeshes.Count,
                ["hiddenNonPrimaryLodMeshes"] = JArray.FromObject(hiddenLodMeshes),
                ["sourceAssessment"] = mergeAssessment.ToJson(),
                ["copiedResources"] = copyNotes,
                ["rule"] = "模型、贴图、材质、skin 来自模型 glTF；动画 channel 来自独立动画 glTF；合并只重映射确定节点。",
            }.ToString(Formatting.Indented));
            Logger.Info($"Merged model+animation glTF: {outputGltf}");
            Logger.Info($"Merge report: {okReportPath}");
            return outputGltf;
        }

        private static List<HiddenLodMeshReport> HideNonPrimaryLodMeshes(JObject[] nodes)
        {
            var result = new List<HiddenLodMeshReport>();
            if (nodes.Length == 0)
            {
                return result;
            }

            var groups = nodes
                .Select((node, index) => new
                {
                    Node = node,
                    Index = index,
                    Name = (string)node["name"] ?? string.Empty,
                    Mesh = (int?)node["mesh"],
                    Lod = TryParseLodNode((string)node["name"], out var lodInfo) ? lodInfo : null,
                })
                .Where(x => x.Mesh.HasValue && x.Lod != null)
                .GroupBy(x => x.Lod.BaseName, StringComparer.OrdinalIgnoreCase);

            foreach (var group in groups)
            {
                var ordered = group
                    .OrderBy(x => x.Lod.Lod)
                    .ThenBy(x => x.Index)
                    .ToArray();
                if (ordered.Length <= 1)
                {
                    continue;
                }

                var selected = ordered[0];
                foreach (var item in ordered.Skip(1))
                {
                    var extras = item.Node["extras"] as JObject;
                    if (extras == null)
                    {
                        extras = new JObject();
                        item.Node["extras"] = extras;
                    }

                    extras["animeStudioLod"] = new JObject
                    {
                        ["hiddenByAnimeStudio"] = true,
                        ["reason"] = "non_primary_lod_preview",
                        ["group"] = item.Lod.BaseName,
                        ["selectedNode"] = selected.Name,
                        ["selectedLod"] = selected.Lod.Lod,
                        ["hiddenLod"] = item.Lod.Lod,
                        ["hiddenMesh"] = item.Mesh.Value,
                    };
                    item.Node.Remove("mesh");
                    item.Node.Remove("skin");
                    result.Add(new HiddenLodMeshReport(
                        Node: item.Index,
                        Name: item.Name,
                        Mesh: item.Mesh.Value,
                        Group: item.Lod.BaseName,
                        HiddenLod: item.Lod.Lod,
                        SelectedNode: selected.Name,
                        SelectedLod: selected.Lod.Lod));
                }
            }

            return result;
        }

        private static bool TryParseLodNode(string name, out LodNodeInfo info)
        {
            info = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            var match = Regex.Match(name, @"^(?<base>.+?)(?:[_\-. ]lod(?<lod>\d+))$", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return false;
            }

            if (!int.TryParse(match.Groups["lod"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lod))
            {
                return false;
            }

            info = new LodNodeInfo(match.Groups["base"].Value, lod);
            return true;
        }

        private static StandaloneAnimationMergeAssessment AssessStandaloneAnimationForMerge(JObject animationGltf, int channelCount)
        {
            var reasons = new JArray();
            var sourceModes = new JArray();
            var needsReview = false;

            foreach (var animation in animationGltf["animations"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var extras = animation["extras"] as JObject;
                var asset = extras?["animeStudioAnimationAsset"] as JObject;
                if (asset != null)
                {
                    var mode = (string)asset["mode"];
                    if (!string.IsNullOrWhiteSpace(mode))
                    {
                        sourceModes.Add(mode);
                    }

                    if ((bool?)asset["notProductionReady"] == true)
                    {
                        needsReview = true;
                        reasons.Add(new JObject
                        {
                            ["reason"] = "standalone_animation_not_production_ready",
                            ["mode"] = mode,
                            ["detail"] = (string)asset["notProductionReadyReason"],
                        });
                    }

                    if ((bool?)asset["experimental"] == true)
                    {
                        needsReview = true;
                        reasons.Add(new JObject
                        {
                            ["reason"] = "standalone_animation_experimental",
                            ["mode"] = mode,
                        });
                    }

                    var humanoid = asset["unityHumanoid"] as JObject;
                    var diagnostics = humanoid?["diagnostics"] as JObject;
                    var knownRiskTargetCount = (int?)diagnostics?["knownRiskTargetCount"] ?? 0;
                    if (knownRiskTargetCount > 0)
                    {
                        needsReview = true;
                        reasons.Add(new JObject
                        {
                            ["reason"] = "humanoid_solver_known_limb_risk",
                            ["knownRiskTargetCount"] = knownRiskTargetCount,
                            ["status"] = (string)diagnostics["status"],
                        });
                    }
                }

                var accelerated = extras?["animeStudioUnityBakeAccelerated"] as JObject;
                if (accelerated != null)
                {
                    needsReview = true;
                    sourceModes.Add("UnityBakeAccelerated");
                    reasons.Add(new JObject
                    {
                        ["reason"] = "unity_bake_accelerated_requires_visual_validation",
                        ["sourceRequest"] = (string)accelerated["sourceRequest"],
                        ["sourceResult"] = (string)accelerated["sourceResult"],
                        ["detail"] = "UnityBakeAccelerated 输出的是 glTF TRS 求解结果，但在清晰视觉验收通过前不能把合并预览标为 ok。",
                    });
                }
            }

            if (channelCount > 0 && channelCount < 30 && sourceModes.Any(x =>
                    string.Equals((string)x, "UnityBakeAccelerated", StringComparison.OrdinalIgnoreCase)
                    || ((string)x)?.IndexOf("Humanoid", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                needsReview = true;
                reasons.Add(new JObject
                {
                    ["reason"] = "low_humanoid_channel_coverage",
                    ["channelCount"] = channelCount,
                    ["detail"] = "人形 Humanoid/UnityBakeAccelerated 动画通道过少，只能视为诊断预览；必须经过主体骨骼覆盖和视觉验收。",
                });
            }

            if (reasons.Count == 0)
            {
                reasons.Add(new JObject
                {
                    ["reason"] = "standalone_animation_channels_mapped",
                    ["detail"] = "独立动画 channel 均已确定映射到模型节点；这只证明 glTF 结构可合并，不等同于视觉验收通过。",
                });
            }

            return new StandaloneAnimationMergeAssessment(
                needsReview ? "needs_review" : "ok",
                sourceModes,
                reasons);
        }

        public static string ExportStandaloneAnimationFromFiles(
            string modelGltfPath,
            string animationSidecarPath,
            string outputDirectory,
            bool forceInternalHumanoidSolve = false,
            string additionalLayerSidecars = null,
            string sourceIndexPath = null,
            string previewAvatarSelector = null)
        {
            if (string.IsNullOrWhiteSpace(modelGltfPath) || !File.Exists(modelGltfPath))
            {
                Logger.Error($"Model glTF not found: {modelGltfPath}");
                return null;
            }
            if (string.IsNullOrWhiteSpace(animationSidecarPath) || !File.Exists(animationSidecarPath))
            {
                Logger.Error("--preview_animation must point to an animation_asset.json sidecar.");
                return null;
            }

            JObject sidecar;
            try
            {
                sidecar = JObject.Parse(File.ReadAllText(animationSidecarPath));
            }
            catch (Exception e)
            {
                Logger.Error($"Unable to read animation sidecar: {animationSidecarPath}. {e.GetType().Name}: {e.Message}");
                return null;
            }

            var animationName = (string)sidecar["name"];
            if (string.IsNullOrWhiteSpace(animationName))
            {
                animationName = Path.GetFileNameWithoutExtension(animationSidecarPath)
                    ?.Replace(".animation_asset", string.Empty);
            }

            var output = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(modelGltfPath)) ?? Directory.GetCurrentDirectory(),
                    "AnimationGltf",
                    SafeName(animationName ?? "Animation")
                )
                : outputDirectory;
            Directory.CreateDirectory(output);
            if (!TryValidateModelGltfForAnimation(modelGltfPath, out var modelGate))
            {
                var reportPath = Path.Combine(output, "standalone_animation_gltf_report.json");
                File.WriteAllText(reportPath, new JObject
                {
                    ["status"] = "blocked",
                    ["reason"] = "model_not_animation_ready",
                    ["modelGltf"] = modelGltfPath,
                    ["animationSidecar"] = animationSidecarPath,
                    ["animationName"] = animationName,
                    ["modelGate"] = modelGate,
                    ["rule"] = "手动 animation_asset.json 入口也必须先通过模型本体验收；不能用动画生成结果掩盖白模、缺贴图、缺材质或 skin 问题。",
                }.ToString(Formatting.Indented));
                Logger.Error($"Standalone animation glTF export blocked by model-first gate: {reportPath}");
                return null;
            }

            if (forceInternalHumanoidSolve)
            {
                return ExportForcedHumanoidStandaloneFromFiles(
                    modelGltfPath,
                    animationSidecarPath,
                    sidecar,
                    animationName,
                    outputDirectory,
                    sourceIndexPath,
                    previewAvatarSelector);
            }

            JObject modelEntry;
            JObject avatarInjection = null;
            if (!string.IsNullOrWhiteSpace(previewAvatarSelector))
            {
                if (!TryBuildModelEntryFromCatalog(
                        modelGltfPath,
                        sourceIndexPath,
                        previewAvatarSelector,
                        requireInternalSolver: false,
                        out modelEntry,
                        out _,
                        out avatarInjection,
                        out var modelMessage))
                {
                    Logger.Error(modelMessage);
                    return null;
                }
            }
            else
            {
                modelEntry = new JObject
                {
                    ["model"] = new JObject
                    {
                        ["name"] = Path.GetFileNameWithoutExtension(modelGltfPath),
                        ["output"] = modelGltfPath,
                    },
                };
            }

            var animationEntry = new JObject
            {
                ["name"] = animationName,
                ["animationAsset"] = animationSidecarPath,
                ["output"] = (string)sidecar["output"],
                ["source"] = (string)sidecar["source"],
                ["sourceType"] = (string)sidecar["sourceType"],
                ["pathId"] = sidecar["pathId"],
                ["animationType"] = (string)sidecar["animationType"],
                ["relationSource"] = "manualFileSelection",
                ["confidence"] = "manual_explicit_file_selection",
            };
            AttachManualAdditionalLayerSidecars(animationEntry, additionalLayerSidecars);

            if (FastPreviewGltfBuilder.TryExportStandaloneAnimation(
                modelEntry,
                animationEntry,
                animationName,
                output,
                out var gltfPath,
                out var message))
            {
                var reportPath = Path.Combine(output, "standalone_animation_gltf_report.json");
                File.WriteAllText(reportPath, new JObject
                {
                    ["status"] = "ok",
                    ["modelGltf"] = modelGltfPath,
                    ["animationSidecar"] = animationSidecarPath,
                    ["additionalLayerSidecars"] = new JArray(SplitManualAdditionalLayerSidecars(additionalLayerSidecars)),
                    ["animationName"] = animationName,
                    ["gltf"] = gltfPath,
                    ["message"] = message,
                    ["avatarInjection"] = avatarInjection,
                    ["rule"] = "手动文件入口只把已选择的 animation_asset.json decoded TRS 写成独立动画 glTF，不创建默认模型-动画推荐关系；可信关系仍应来自 Unity 显式引用或后续验证索引。",
                }.ToString(Formatting.Indented));
                Logger.Info(message);
                Logger.Info($"Standalone animation glTF: {gltfPath}");
                Logger.Info($"Standalone animation report: {reportPath}");
                return gltfPath;
            }

            var failureReportPath = Path.Combine(output, "standalone_animation_gltf_report.json");
            Directory.CreateDirectory(output);
            File.WriteAllText(failureReportPath, new JObject
            {
                ["status"] = "failed",
                ["reason"] = "standalone_animation_gltf_export_failed",
                ["modelGltf"] = modelGltfPath,
                ["animationSidecar"] = animationSidecarPath,
                ["additionalLayerSidecars"] = new JArray(SplitManualAdditionalLayerSidecars(additionalLayerSidecars)),
                ["animationName"] = animationName,
                ["message"] = message,
                ["avatarInjection"] = avatarInjection,
                ["rule"] = "手动文件入口失败也写报告。显式 Avatar TOS 只用于 hash-only 路径解析诊断；失败不代表模型贴图或材质关系丢失。",
            }.ToString(Formatting.Indented));
            Logger.Error($"Standalone animation glTF export failed: {message}");
            Logger.Error($"Standalone animation failure report: {failureReportPath}");
            return null;
        }

        private static bool TryValidateModelGltfForAnimation(string modelGltfPath, out JObject modelGate)
        {
            modelGate = ModelLibraryValidator.ValidateSingleModelForAnimationGate(modelGltfPath);
            ApplyCatalogModelGateIfAvailable(modelGltfPath, modelGate);
            return (bool?)modelGate?["animationGate"]?["ready"] == true;
        }

        private static void ApplyCatalogModelGateIfAvailable(string modelGltfPath, JObject modelGate)
        {
            if (modelGate?["animationGate"] is not JObject gate)
            {
                return;
            }

            var model = TryFindCatalogModel(modelGltfPath);
            if (model == null)
            {
                return;
            }

            var reasons = gate["reasons"] as JArray ?? new JArray();
            var unresolved = (int?)model["unresolvedModelDependencyCount"] ?? 0;
            if (unresolved > 0)
            {
                AddReason("unresolved_model_dependencies");
            }
            if ((bool?)model["materialMissingRendererBinding"] == true)
            {
                AddReason("missing_renderer_material_binding");
            }

            if (reasons.Count > 0)
            {
                gate["ready"] = false;
                gate["status"] = "blocked";
                gate["reasons"] = reasons;
            }

            gate["catalogEvidence"] = new JObject
            {
                ["source"] = (string)model["source"],
                ["output"] = (string)model["output"],
                ["unresolvedModelDependencyCount"] = unresolved,
                ["unresolvedModelDependencyTypes"] = model["unresolvedModelDependencyTypes"]?.DeepClone(),
                ["materialMissingRendererBinding"] = (bool?)model["materialMissingRendererBinding"] ?? false,
            };

            void AddReason(string reason)
            {
                if (!reasons.Values<string>().Any(x => string.Equals(x, reason, StringComparison.OrdinalIgnoreCase)))
                {
                    reasons.Add(reason);
                }
            }
        }

        private static JObject TryFindCatalogModel(string modelGltfPath)
        {
            var fullModelPath = Path.GetFullPath(modelGltfPath);
            var current = new DirectoryInfo(Path.GetDirectoryName(fullModelPath) ?? Directory.GetCurrentDirectory());
            while (current != null)
            {
                var catalogPath = Path.Combine(current.FullName, "asset_catalog.jsonl");
                if (!File.Exists(catalogPath))
                {
                    current = current.Parent;
                    continue;
                }

                var root = current.FullName;
                return ReadJsonLines(catalogPath)
                    .Where(x => string.Equals((string)x["kind"], "Model", StringComparison.OrdinalIgnoreCase))
                    .Select(x =>
                    {
                        var clone = (JObject)x.DeepClone();
                        ResolvePathProperty(clone, root, "output");
                        return clone;
                    })
                    .FirstOrDefault(x => PathsEqual((string)x["output"], fullModelPath));
            }

            return null;
        }

        private static void AttachManualAdditionalLayerSidecars(JObject animationEntry, string additionalLayerSidecars)
        {
            var paths = SplitManualAdditionalLayerSidecars(additionalLayerSidecars)
                .Where(File.Exists)
                .ToArray();
            if (paths.Length == 0)
            {
                return;
            }

            var clips = new JArray();
            foreach (var requestPath in paths.Where(IsUnityBakeRequestPath))
            {
                var request = JObject.Parse(File.ReadAllText(requestPath));
                foreach (var clip in request["animeStudioAssets"]?["animation"]?["animatorControllerContext"]?["additionalLayerClips"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    var copy = (JObject)clip.DeepClone();
                    copy["source"] = "manual_unity_bake_request_context";
                    clips.Add(copy);
                }
            }

            foreach (var path in paths)
            {
                if (IsUnityBakeRequestPath(path))
                {
                    continue;
                }

                var sidecar = JObject.Parse(File.ReadAllText(path));
                var name = (string)sidecar["name"] ?? Path.GetFileNameWithoutExtension(path)?.Replace(".animation_asset", string.Empty);
                var existing = clips
                    .OfType<JObject>()
                    .Where(x => string.Equals((string)x["name"], name, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (existing.Length > 0)
                {
                    foreach (var clip in existing)
                    {
                        clip["animationAsset"] = path;
                    }
                    continue;
                }

                clips.Add(new JObject
                {
                    ["name"] = name,
                    ["animationAsset"] = path,
                    ["layerIndex"] = clips.Count + 1,
                    ["layerBlendingMode"] = 1,
                    ["layerDefaultWeight"] = 1.0,
                    ["source"] = "manual_additional_layer_sidecar",
                });
            }

            animationEntry["animatorControllerContext"] = new JObject
            {
                ["source"] = "manual_file_selection.additional_layers",
                ["additionalLayerClips"] = clips,
            };
        }

        private static bool IsUnityBakeRequestPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            return string.Equals(Path.GetFileName(path), "unity_bake_request.json", StringComparison.OrdinalIgnoreCase);
        }

        private static string[] SplitManualAdditionalLayerSidecars(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? Array.Empty<string>()
                : value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        private static string ExportForcedHumanoidStandaloneFromFiles(
            string modelGltfPath,
            string animationSidecarPath,
            JObject sidecar,
            string animationName,
            string outputDirectory,
            string sourceIndexPath,
            string previewAvatarSelector)
        {
            if (!TryBuildModelEntryFromCatalog(
                    modelGltfPath,
                    sourceIndexPath,
                    previewAvatarSelector,
                    requireInternalSolver: true,
                    out var modelEntry,
                    out var libraryRoot,
                    out var avatarInjection,
                    out var modelMessage))
            {
                Logger.Error(modelMessage);
                return null;
            }

            var output = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(modelGltfPath)) ?? Directory.GetCurrentDirectory(),
                    "AnimationGltf",
                    SafeName(animationName ?? "Animation"))
                : outputDirectory;

            var animationEntry = new JObject
            {
                ["name"] = animationName,
                ["animationAsset"] = animationSidecarPath,
                ["output"] = (string)sidecar["output"],
                ["source"] = (string)sidecar["source"],
                ["sourceType"] = (string)sidecar["sourceType"],
                ["pathId"] = sidecar["pathId"],
                ["animationType"] = (string)sidecar["animationType"],
                ["relationSource"] = "manualFileSelection",
                ["confidence"] = "manual_explicit_file_selection",
            };

            if (!FastPreviewGltfBuilder.TryExportStandaloneAnimation(
                    modelEntry,
                    animationEntry,
                    animationName,
                    output,
                    out var gltfPath,
                    out var message,
                    forceInternalHumanoidSolve: true))
            {
                var failureReportPath = Path.Combine(output, "standalone_animation_gltf_report.json");
                Directory.CreateDirectory(output);
                File.WriteAllText(failureReportPath, new JObject
                {
                    ["status"] = "failed",
                    ["reason"] = "forced_internal_humanoid_export_failed",
                    ["modelGltf"] = modelGltfPath,
                    ["animationSidecar"] = animationSidecarPath,
                    ["animationName"] = animationName,
                    ["message"] = message,
                    ["libraryRoot"] = libraryRoot,
                    ["avatarInjection"] = avatarInjection,
                    ["rule"] = "手动文件入口强制 Humanoid/Muscle 求解失败时也写报告。显式 Avatar oracle 注入只证明诊断输入被采用，不代表该 clip 含有人形身体曲线或可作为生产动画。",
                }.ToString(Formatting.Indented));
                Logger.Error($"Forced internal Humanoid/Muscle standalone animation export failed: {message}");
                Logger.Error($"Standalone animation failure report: {failureReportPath}");
                return null;
            }

            var reportPath = Path.Combine(output, "standalone_animation_gltf_report.json");
            File.WriteAllText(reportPath, new JObject
            {
                ["status"] = "ok",
                ["modelGltf"] = modelGltfPath,
                ["animationSidecar"] = animationSidecarPath,
                ["animationName"] = animationName,
                ["gltf"] = gltfPath,
                ["message"] = message,
                ["libraryRoot"] = libraryRoot,
                ["avatarInjection"] = avatarInjection,
                ["rule"] = "手动文件入口强制使用 AnimeStudio 内部 Humanoid/Muscle 求解器生成无 mesh/skin 的独立动画 glTF；只用于直接 TRS 求解验证，不创建默认模型-动画推荐关系，也不替代视觉验收。",
            }.ToString(Formatting.Indented));

            Logger.Info(message);
            Logger.Info($"Forced internal Humanoid/Muscle standalone animation glTF: {gltfPath}");
            Logger.Info($"Standalone animation report: {reportPath}");
            return gltfPath;
        }

        private static bool TryBuildModelEntryFromCatalog(
            string modelGltfPath,
            string sourceIndexPath,
            string previewAvatarSelector,
            bool requireInternalSolver,
            out JObject modelEntry,
            out string libraryRoot,
            out JObject avatarInjection,
            out string message)
        {
            modelEntry = null;
            libraryRoot = null;
            avatarInjection = null;
            message = null;

            var fullModelPath = Path.GetFullPath(modelGltfPath);
            var current = new DirectoryInfo(Path.GetDirectoryName(fullModelPath) ?? Directory.GetCurrentDirectory());
            while (current != null)
            {
                var catalogPath = Path.Combine(current.FullName, "asset_catalog.jsonl");
                if (File.Exists(catalogPath))
                {
                    var root = current.FullName;
                    libraryRoot = root;
                    var model = ReadJsonLines(catalogPath)
                        .Where(x => string.Equals((string)x["kind"], "Model", StringComparison.OrdinalIgnoreCase))
                        .Select(x =>
                        {
                            var clone = (JObject)x.DeepClone();
                            ResolvePathProperty(clone, root, "output");
                            ResolvePathProperty(clone, root, "modelPreview");
                            return clone;
                        })
                        .FirstOrDefault(x => PathsEqual((string)x["output"], fullModelPath));

                    if (model == null)
                    {
                        message = $"asset_catalog.jsonl was found, but no Model row matches {fullModelPath}.";
                        return false;
                    }
                    if (!string.IsNullOrWhiteSpace(previewAvatarSelector))
                    {
                        if (string.IsNullOrWhiteSpace(sourceIndexPath) || !File.Exists(sourceIndexPath))
                        {
                            message = "--preview_avatar for manual animation preview requires a readable --source_index unity_source_index.db.";
                            return false;
                        }

                        if (!TryLoadDiagnosticAvatarFromSourceIndex(
                                sourceIndexPath,
                                previewAvatarSelector,
                                requireInternalSolver,
                                out var injectedAvatar,
                                out avatarInjection,
                                out var avatarMessage))
                        {
                            message = avatarMessage;
                            return false;
                        }

                        // 这里只修改本次内存里的 model entry，正式 asset_catalog.jsonl 不会被回写。
                        model["avatar"] = injectedAvatar;
                    }

                    var avatar = model["avatar"] as JObject;
                    if (requireInternalSolver && avatar?["internalSolver"] == null)
                    {
                        message = "Forced internal Humanoid/Muscle preview needs model.avatar.internalSolver from asset_catalog.jsonl. Re-export the model with current Avatar metadata first.";
                        return false;
                    }

                    modelEntry = new JObject
                    {
                        ["model"] = model,
                        ["candidateCount"] = 1,
                    };
                    return true;
                }
                current = current.Parent;
            }

            message = $"Cannot find asset_catalog.jsonl by walking up from model glTF: {fullModelPath}";
            return false;
        }

        private static bool TryLoadDiagnosticAvatarFromSourceIndex(
            string sourceIndexPath,
            string selector,
            bool requireInternalSolver,
            out JObject avatar,
            out JObject injectionReport,
            out string message)
        {
            avatar = null;
            injectionReport = null;
            message = null;

            SQLitePCL.Batteries_V2.Init();
            using var connection = new SqliteConnection($"Data Source={Path.GetFullPath(sourceIndexPath)};Mode=ReadOnly");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT name, source_path, serialized_file, path_id, raw_json
FROM source_objects
WHERE type='Avatar'
ORDER BY name COLLATE NOCASE, path_id;";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var name = ReadString(reader, 0);
                var sourcePath = ReadString(reader, 1);
                var serializedFile = ReadString(reader, 2);
                var pathId = ReadLong(reader, 3);
                if (!MatchesAvatarSelector(selector, name, sourcePath, serializedFile, pathId))
                {
                    continue;
                }

                JObject raw;
                try
                {
                    raw = JObject.Parse(ReadString(reader, 4));
                }
                catch (JsonException)
                {
                    continue;
                }

                var rawAvatar = raw["avatar"] as JObject;
                var tos = CloneArray(rawAvatar?["tos"] as JArray);
                var oracle = rawAvatar?["oracle"] as JObject;
                var oracleMessage = string.Empty;
                var hasCompleteOracle = oracle != null && HasCompleteSourceAvatarOracle(oracle, out oracleMessage);
                if (requireInternalSolver && oracle == null)
                {
                    message = $"Selected Avatar does not contain a complete AvatarConstant oracle: {name}#{pathId}. missing oracle";
                    return false;
                }
                if (requireInternalSolver && !hasCompleteOracle)
                {
                    message = $"Selected Avatar does not contain a complete AvatarConstant oracle: {name}#{pathId}. {oracleMessage}";
                    return false;
                }
                if (!requireInternalSolver && tos.Count == 0 && !hasCompleteOracle)
                {
                    message = $"Selected Avatar cannot be used for direct TRS path diagnostics: {name}#{pathId}. missing Avatar.m_TOS and complete AvatarConstant oracle";
                    return false;
                }

                avatar = new JObject
                {
                    ["name"] = string.IsNullOrWhiteSpace(name) ? $"Avatar_{pathId}" : name,
                    ["source"] = sourcePath,
                    ["serializedFile"] = serializedFile,
                    ["pathId"] = pathId,
                    ["hasHumanDescription"] = rawAvatar?["hasHumanDescription"] ?? false,
                    ["humanBoneCount"] = rawAvatar?["humanBoneCount"] ?? 0,
                    ["skeletonBoneCount"] = rawAvatar?["skeletonBoneCount"] ?? 0,
                    ["avatarSkeletonNodeCount"] = rawAvatar?["avatarSkeletonNodeCount"] ?? 0,
                    ["oracleSource"] = "unity_source_index.avatar.oracle",
                    ["diagnosticOnly"] = true,
                    ["tos"] = tos,
                };
                if (hasCompleteOracle)
                {
                    avatar["internalSolver"] = BuildInternalSolverFromSourceAvatarOracle(oracle);
                }
                injectionReport = new JObject
                {
                    ["status"] = "ok",
                    ["diagnosticOnly"] = true,
                    ["manualReviewRequired"] = true,
                    ["notDefaultModelAnimationRelation"] = true,
                    ["mode"] = requireInternalSolver ? "internalHumanoidSolver" : "directTrsPathHashMapping",
                    ["internalSolverInjected"] = hasCompleteOracle,
                    ["tosCount"] = tos.Count,
                    ["sourceIndex"] = Path.GetFullPath(sourceIndexPath),
                    ["selector"] = selector,
                    ["avatar"] = new JObject
                    {
                        ["name"] = avatar["name"],
                        ["sourcePath"] = sourcePath,
                        ["serializedFile"] = serializedFile,
                        ["pathId"] = pathId,
                        ["humanBoneCount"] = avatar["humanBoneCount"],
                        ["skeletonBoneCount"] = avatar["skeletonBoneCount"],
                        ["avatarSkeletonNodeCount"] = avatar["avatarSkeletonNodeCount"],
                    },
                    ["rule"] = requireInternalSolver
                        ? "显式手动预览把源索引 Avatar oracle 临时装入 model.avatar.internalSolver，只用于 Humanoid/Muscle 求解诊断；不会回写 asset_catalog.jsonl，也不会创建默认模型-动画关系。"
                        : "显式手动预览把源索引 Avatar.m_TOS 临时装入 model.avatar.tos，只用于 hash-only AnimationClip 路径解析诊断；不会回写 asset_catalog.jsonl，也不会创建默认模型-动画关系。",
                };
                return true;
            }

            message = $"No Avatar in source index matched --preview_avatar selector: {selector}";
            return false;
        }

        private static JObject BuildInternalSolverFromSourceAvatarOracle(JObject oracle)
        {
            var human = oracle["human"] as JObject;
            var humanSkeleton = oracle["humanSkeleton"] as JObject;
            var avatarSkeleton = oracle["avatarSkeleton"] as JObject;
            var humanNodes = CloneNodeArrayWithNames(humanSkeleton?["nodes"] as JArray);
            var avatarNodes = CloneNodeArrayWithNames(avatarSkeleton?["nodes"] as JArray);
            var avatarDefaultPose = CloneArray(avatarSkeleton?["defaultPose"] as JArray);

            return new JObject
            {
                ["version"] = 1,
                ["source"] = "Unity AvatarConstant via unity_source_index.avatar.oracle",
                ["rule"] = "Diagnostic-only manual preview input. It is deterministic Avatar metadata, but it is not a Unity explicit model-animation relation and must not be treated as production animation readiness.",
                ["humanBoneIndex"] = CloneArray(oracle["humanBoneIndex"] as JArray),
                ["scale"] = human?["scale"] ?? 1.0,
                ["hasTranslationDoF"] = human?["hasTDoF"] ?? false,
                ["root"] = CloneObject(human?["root"] as JObject),
                ["human"] = new JObject
                {
                    ["hasLeftHand"] = human?["hasLeftHand"] ?? false,
                    ["hasRightHand"] = human?["hasRightHand"] ?? false,
                    ["leftHandBoneIndex"] = CloneArray(human?["leftHandBoneIndex"] as JArray),
                    ["rightHandBoneIndex"] = CloneArray(human?["rightHandBoneIndex"] as JArray),
                    ["humanBoneMass"] = CloneArray(oracle["humanBoneMass"] as JArray),
                },
                ["twist"] = new JObject
                {
                    ["arm"] = human?["armTwist"] ?? 0.5,
                    ["foreArm"] = human?["foreArmTwist"] ?? 0.5,
                    ["upperLeg"] = human?["upperLegTwist"] ?? 0.5,
                    ["leg"] = human?["legTwist"] ?? 0.5,
                    ["armStretch"] = human?["armStretch"] ?? 0.05,
                    ["legStretch"] = human?["legStretch"] ?? 0.05,
                    ["feetSpacing"] = human?["feetSpacing"] ?? 0.0,
                },
                ["humanBoneLimits"] = new JObject(),
                ["humanSkeletonIndexArray"] = CloneArray(oracle["humanSkeletonIndexArray"] as JArray),
                ["humanSkeletonReverseIndexArray"] = CloneArray(oracle["humanSkeletonReverseIndexArray"] as JArray),
                ["skeleton"] = new JObject
                {
                    ["nodeCount"] = humanSkeleton?["nodeCount"] ?? humanNodes.Count,
                    ["axesCount"] = humanSkeleton?["axesCount"] ?? JsonArrayCount(humanSkeleton?["axes"]),
                    ["humanSkeletonPoseCount"] = JsonArrayCount(humanSkeleton?["pose"]),
                    ["avatarDefaultPoseCount"] = avatarDefaultPose.Count,
                    ["nodes"] = humanNodes,
                    ["axes"] = CloneArray(humanSkeleton?["axes"] as JArray),
                    ["humanSkeletonPose"] = CloneArray(humanSkeleton?["pose"] as JArray),
                    ["avatarDefaultPose"] = avatarDefaultPose,
                },
                ["avatarSkeleton"] = new JObject
                {
                    ["nodeCount"] = avatarSkeleton?["nodeCount"] ?? avatarNodes.Count,
                    ["axesCount"] = avatarSkeleton?["axesCount"] ?? JsonArrayCount(avatarSkeleton?["axes"]),
                    ["poseCount"] = JsonArrayCount(avatarSkeleton?["pose"]),
                    ["defaultPoseCount"] = avatarDefaultPose.Count,
                    ["nodes"] = avatarNodes,
                    ["axes"] = CloneArray(avatarSkeleton?["axes"] as JArray),
                    ["pose"] = CloneArray(avatarSkeleton?["pose"] as JArray),
                    ["defaultPose"] = avatarDefaultPose,
                },
                ["rootMotion"] = CloneObject(oracle["rootMotion"] as JObject),
            };
        }

        private static bool HasCompleteSourceAvatarOracle(JObject oracle, out string message)
        {
            message = null;
            var humanBoneIndex = oracle?["humanBoneIndex"] as JArray;
            var humanSkeleton = oracle?["humanSkeleton"] as JObject;
            var humanNodes = humanSkeleton?["nodes"] as JArray;
            var humanPose = humanSkeleton?["pose"] as JArray;
            var avatarSkeleton = oracle?["avatarSkeleton"] as JObject;
            var avatarNodes = avatarSkeleton?["nodes"] as JArray;
            var defaultPose = avatarSkeleton?["defaultPose"] as JArray;
            if (humanBoneIndex == null || humanBoneIndex.Count == 0)
            {
                message = "missing humanBoneIndex";
                return false;
            }
            if (humanNodes == null || humanNodes.Count == 0 || humanPose == null || humanPose.Count < humanNodes.Count)
            {
                message = "missing complete humanSkeleton nodes/pose";
                return false;
            }
            if (avatarNodes == null || avatarNodes.Count == 0 || defaultPose == null || defaultPose.Count < avatarNodes.Count)
            {
                message = "missing complete avatarSkeleton nodes/defaultPose";
                return false;
            }

            return true;
        }

        private static JArray CloneNodeArrayWithNames(JArray nodes)
        {
            var result = new JArray();
            foreach (var node in nodes?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var clone = (JObject)node.DeepClone();
                var path = (string)clone["path"];
                if (string.IsNullOrWhiteSpace((string)clone["name"]))
                {
                    clone["name"] = GetLastPathSegment(path);
                }
                result.Add(clone);
            }
            return result;
        }

        private static string GetLastPathSegment(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            path = path.Replace('\\', '/').Trim('/');
            var index = path.LastIndexOf('/');
            return index < 0 ? path : path[(index + 1)..];
        }

        private static bool MatchesAvatarSelector(string selector, string name, string sourcePath, string serializedFile, long pathId)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                return false;
            }

            var trimmed = selector.Trim();
            return string.Equals(pathId.ToString(CultureInfo.InvariantCulture), trimmed, StringComparison.OrdinalIgnoreCase)
                || ContainsIgnoreCase(name, trimmed)
                || ContainsIgnoreCase(sourcePath, trimmed)
                || ContainsIgnoreCase(serializedFile, trimmed);
        }

        private static bool ContainsIgnoreCase(string value, string token)
        {
            return !string.IsNullOrWhiteSpace(value)
                && !string.IsNullOrWhiteSpace(token)
                && value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static JArray CloneArray(JArray array)
        {
            return array == null ? new JArray() : (JArray)array.DeepClone();
        }

        private static JObject CloneObject(JObject value)
        {
            return value == null ? new JObject() : (JObject)value.DeepClone();
        }

        private static int JsonArrayCount(JToken token)
        {
            return token is JArray array ? array.Count : 0;
        }

        private static string ReadString(SqliteDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
        }

        private static long ReadLong(SqliteDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }

        private static string GenerateSelection(
            string indexPath,
            string gameName,
            PreviewSelection selection,
            string outputDirectory,
            string sourceRootOverride,
            string sourceIndexOverride = null)
        {
            var modelName = (string)selection.Model["model"]?["name"];
            var animationName = (string)selection.Animation["name"];
            var modelSource = ResolveSourcePath((string)selection.Model["model"]?["source"], sourceRootOverride);
            var animationSource = ResolveSourcePath((string)selection.Animation["source"], sourceRootOverride);
            if (string.IsNullOrWhiteSpace(modelName) || string.IsNullOrWhiteSpace(animationName))
            {
                Logger.Error("Selected preview entry is missing model or animation name.");
                return null;
            }
            if (!IsExplicitPreviewRelation(selection.Animation))
            {
                Logger.Error("Selected preview candidate is not a Unity explicit model-animation relation. Default preview generation refuses structure/name/manual matches.");
                Logger.Error($"Relation source: {(string)selection.Animation["relationSource"] ?? "(none)"}; confidence: {(string)selection.Animation["confidence"] ?? "(none)"}");
                return null;
            }
            if (!IsSelectionModelReadyForAnimation(selection, out var blockedReason))
            {
                Logger.Error("Selected preview candidate is blocked by the model-first animation gate.");
                Logger.Error(blockedReason);
                return null;
            }
            var output = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(indexPath)) ?? Directory.GetCurrentDirectory(),
                    "Previews",
                    $"{SafeName(modelName)}__{SafeName(animationName)}"
                )
                : outputDirectory;
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
            Directory.CreateDirectory(output);

            var forceInternalHumanoidSolve = CandidateRequiresInternalHumanoidSolve(selection.Animation);
            if (FastPreviewGltfBuilder.TryGenerate(selection.Model, selection.Animation, animationName, output, out var fastGltfPath, out var fastMessage, forceInternalHumanoidSolve))
            {
                Logger.Info(fastMessage);
                var fastReport = GltfPreviewValidator.Validate(fastGltfPath, modelName, animationName, selection.Model, selection.Animation, Array.Empty<string>());
                var fastReportPath = Path.Combine(output, "preview_validation.json");
                File.WriteAllText(fastReportPath, JsonConvert.SerializeObject(fastReport, Formatting.Indented));
                Logger.Info($"Fast preview glTF: {fastGltfPath}");
                Logger.Info($"Preview validation: {fastReportPath}");
                return fastGltfPath;
            }
            Logger.Info($"Fast preview skipped: {fastMessage}");
            if (!File.Exists(modelSource) || !File.Exists(animationSource))
            {
                Logger.Error("Selected preview entry source files no longer exist. Re-run the Library export from an accessible game/source folder or provide exported glTF + animation sidecar data that FastPreview can consume.");
                Logger.Error($"Model source: {modelSource}");
                Logger.Error($"Animation source: {animationSource}");
                return null;
            }
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
            Directory.CreateDirectory(output);

            var inputRoot = GetCommonRoot(new[] { Path.GetDirectoryName(modelSource), Path.GetDirectoryName(animationSource) });
            var sourceIndexRoot = ReadSourceIndexRoot(sourceIndexOverride);
            if (!string.IsNullOrWhiteSpace(sourceIndexRoot)
                && IsUnderDirectory(modelSource, sourceIndexRoot)
                && IsUnderDirectory(animationSource, sourceIndexRoot))
            {
                inputRoot = sourceIndexRoot;
            }
            var modelFilter = BuildSourceFilter(modelSource);
            var animationFilter = BuildSourceFilter(animationSource);
            var sourceFilter = $"({modelFilter})|({animationFilter})";
            var sourceFiles = BuildSourceFileArguments(inputRoot, modelSource, animationSource);
            var pathIds = BuildPathIdArguments(selection);
            var names = $"^{Regex.Escape(modelName)}$|^{Regex.Escape(animationName)}$";
            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            {
                Logger.Error("Unable to resolve current CLI executable path.");
                return null;
            }

            Logger.Info($"Generating preview glTF: {modelName} + {animationName}");
            var args = new List<string>
            {
                inputRoot,
                output,
                "--game", gameName,
                "--mode", "Library",
                "--group_assets", "ByLibrary",
                "--profile_3d", "All",
                "--model_format", "Gltf",
                "--texture_mode", "Png",
                "--animation_package", "Both",
                "--fbx_animation", "All",
                "--export_full_decoded_animation_curves",
                "--names", names,
                "--containers", sourceFilter,
                "--batch_files", "64",
                "--profile_log", "off",
                "--skip_sqlite_index",
                "--silent",
            };
            if (sourceFiles.Length > 0)
            {
                args.Add("--source_files");
                args.AddRange(sourceFiles);
            }
            if (pathIds.Length > 0)
            {
                args.Add("--path_ids");
                args.AddRange(pathIds);
            }
            if (!string.IsNullOrWhiteSpace(sourceIndexOverride) && File.Exists(sourceIndexOverride))
            {
                args.Add("--source_index");
                args.Add(sourceIndexOverride);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            var process = Process.Start(startInfo);
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(stdout))
            {
                Logger.Info(stdout.Trim());
            }
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                Logger.Warning(stderr.Trim());
            }
            if (process.ExitCode != 0)
            {
                Logger.Error($"Preview export failed with exit code {process.ExitCode}.");
                return null;
            }

            var gltfPath = Directory
                .EnumerateFiles(output, $"{SafeName(modelName)}.gltf", SearchOption.AllDirectories)
                .FirstOrDefault()
                ?? Directory.EnumerateFiles(output, "*.gltf", SearchOption.AllDirectories).FirstOrDefault();
            if (gltfPath == null)
            {
                Logger.Error($"Preview export did not produce a glTF file in {output}.");
                return null;
            }

            var exportIssues = ExtractExportIssues(stdout, stderr);
            if (TryGenerateFastPreviewFromExport(output, modelName, animationName, exportIssues, out var solvedGltfPath))
            {
                return solvedGltfPath;
            }

            var report = GltfPreviewValidator.Validate(gltfPath, modelName, animationName, selection.Model, selection.Animation, exportIssues);
            var reportPath = Path.Combine(output, "preview_validation.json");
            File.WriteAllText(reportPath, JsonConvert.SerializeObject(report, Formatting.Indented));
            Logger.Info($"Preview glTF: {gltfPath}");
            Logger.Info($"Preview validation: {reportPath}");
            return gltfPath;
        }

        private static bool TryGenerateFastPreviewFromExport(
            string exportRoot,
            string modelName,
            string animationName,
            string[] exportIssues,
            out string gltfPath)
        {
            gltfPath = null;
            var catalogPath = Path.Combine(exportRoot, "asset_catalog.jsonl");
            if (!File.Exists(catalogPath))
            {
                return false;
            }

            var entries = ReadJsonLines(catalogPath).ToList();
            var model = entries.FirstOrDefault(x =>
                string.Equals((string)x["kind"], "Model", StringComparison.OrdinalIgnoreCase)
                && Matches(modelName, (string)x["name"], (string)x["output"]));
            var animation = entries.FirstOrDefault(x =>
                string.Equals((string)x["kind"], "Animation", StringComparison.OrdinalIgnoreCase)
                && Matches(animationName, (string)x["name"], (string)x["output"]));
            if (model == null || animation == null)
            {
                return false;
            }
            ResolvePathProperty(model, exportRoot, "output");
            ResolvePathProperty(model, exportRoot, "modelPreview");
            ResolvePathProperty(animation, exportRoot, "output");
            ResolvePathProperty(animation, exportRoot, "animationAsset");

            var selectionModel = new JObject
            {
                ["model"] = model,
                ["candidateCount"] = 1,
                ["candidates"] = new JArray(animation),
            };
            var fastOutput = Path.Combine(exportRoot, "FastPreview");
            var forceInternalHumanoidSolve = CandidateRequiresInternalHumanoidSolve(animation);
            if (!FastPreviewGltfBuilder.TryGenerate(selectionModel, animation, animationName, fastOutput, out var fastGltfPath, out var message, forceInternalHumanoidSolve))
            {
                Logger.Info($"Post-export fast preview skipped: {message}");
                return false;
            }

            Logger.Info(message);
            var report = GltfPreviewValidator.Validate(fastGltfPath, modelName, animationName, selectionModel, animation, exportIssues);
            var reportPath = Path.Combine(exportRoot, "preview_validation.json");
            File.WriteAllText(reportPath, JsonConvert.SerializeObject(report, Formatting.Indented));
            Logger.Info($"Preview glTF: {fastGltfPath}");
            Logger.Info($"Preview validation: {reportPath}");
            gltfPath = fastGltfPath;
            return true;
        }

        public static void GeneratePack(
            string indexPath,
            string gameName,
            string modelSelector,
            string animationSelectors,
            string outputDirectory,
            int limit,
            string sourceRootOverride = null
        )
        {
            if (string.IsNullOrWhiteSpace(gameName))
            {
                Logger.Error("--game is required with --pack_model_animations.");
                return;
            }
            if (string.IsNullOrWhiteSpace(indexPath) || !File.Exists(indexPath))
            {
                Logger.Error($"model_animations.json not found: {indexPath}");
                return;
            }

            var index = JObject.Parse(File.ReadAllText(indexPath));
            var model = SelectModel(index, modelSelector);
            if (model == null)
            {
                Logger.Error("No model matched --preview_model for --pack_model_animations.");
                return;
            }

            var modelInfo = model["model"] as JObject;
            var modelName = (string)modelInfo?["name"];
            var output = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(indexPath)) ?? Directory.GetCurrentDirectory(),
                    "AnimationPacks",
                    SafeName(modelName ?? "Model")
                )
                : outputDirectory;
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
            Directory.CreateDirectory(output);

            var animations = SelectPackAnimations(model, animationSelectors, Math.Max(1, limit));
            if (animations.Count == 0)
            {
                Logger.Error("No animation candidates matched --pack_animations after the model-first animation gate.");
                return;
            }

            Logger.Info($"Generating animation pack: {modelName} + {animations.Count} animation(s)");
            var results = new List<object>();
            foreach (var animation in animations)
            {
                var animationName = (string)animation["name"];
                var itemOutput = Path.Combine(output, SafeName(animationName ?? "Animation"));
                Generate(indexPath, gameName, modelName, $"^{Regex.Escape(animationName ?? string.Empty)}$", itemOutput, sourceRootOverride);

                var validationPath = Path.Combine(itemOutput, "preview_validation.json");
                JObject validation = null;
                if (File.Exists(validationPath))
                {
                    validation = JObject.Parse(File.ReadAllText(validationPath));
                }
                var gltfPath = validation == null
                    ? Directory.EnumerateFiles(itemOutput, "*.gltf", SearchOption.AllDirectories).FirstOrDefault()
                    : (string)validation["gltf"];
                results.Add(BuildPackResult(modelName, animationName, gltfPath, validationPath, validation));
            }

            var report = new
            {
                generatedAt = DateTime.UtcNow.ToString("O"),
                rule = "Each item is a reusable playable model+single-animation glTF generated from Unity relation indexes. Use these assets for small-scope validation before creating larger animation bundles.",
                index = indexPath,
                sourceRootOverride,
                model = modelInfo,
                requestedAnimations = animationSelectors,
                count = results.Count,
                animations = results,
            };
            var reportPath = Path.Combine(output, "animation_pack_report.json");
            File.WriteAllText(reportPath, JsonConvert.SerializeObject(report, Formatting.Indented));
            Logger.Info($"Animation pack report: {reportPath}");
        }

        private static object BuildPackResult(string modelName, string animationName, string gltfPath, string validationPath, JObject validation)
        {
            return new
            {
                model = modelName,
                animation = animationName,
                output = gltfPath,
                validation = validationPath,
                status = (string)validation?["status"] ?? "missing",
                channels = (int?)validation?["counts"]?["channels"] ?? 0,
                coreBoneNodes = (int?)validation?["animationCoverage"]?["coreBoneNodeCount"] ?? 0,
                invalidChannels = (int?)validation?["counts"]?["invalidChannels"] ?? 0,
                baked = (bool?)validation?["humanoid"]?["baked"] ?? false,
                bakeMode = (string)validation?["humanoid"]?["bakeMode"],
                bakedTrackCount = (int?)validation?["humanoid"]?["bakedTrackCount"] ?? 0,
                bakedKeyframeCount = (int?)validation?["humanoid"]?["bakedKeyframeCount"] ?? 0,
                internalSolved = (bool?)validation?["humanoid"]?["internalSolved"] ?? false,
                solvedTrackCount = (int?)validation?["humanoid"]?["solvedTrackCount"] ?? 0,
                solvedKeyframeCount = (int?)validation?["humanoid"]?["solvedKeyframeCount"] ?? 0,
                rootMotion = validation?["humanoid"]?["rootMotion"],
                exportIssueCount = (int?)validation?["exportIssues"]?["count"] ?? 0,
                humanoid = validation?["humanoid"],
                exportIssues = validation?["exportIssues"],
                notes = validation?["notes"],
            };
        }

        public static void GeneratePackFromLibrary(
            string libraryRoot,
            string gameName,
            string modelSelector,
            string animationSelectors,
            string outputDirectory,
            int limit,
            string sourceRootOverride = null,
            string indexPathOverride = null
        )
        {
            if (string.IsNullOrWhiteSpace(gameName))
            {
                Logger.Error("--game is required with --pack_model_animations_from_library.");
                return;
            }
            if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
            {
                Logger.Error($"Library root not found: {libraryRoot}");
                return;
            }

            var dbPath = string.IsNullOrWhiteSpace(indexPathOverride)
                ? Path.Combine(libraryRoot, "library_index.db")
                : indexPathOverride;
            if (!File.Exists(dbPath))
            {
                Logger.Error($"library_index.db not found: {dbPath}. Rebuild the Library export or run --build_sqlite_index.");
                return;
            }

            SQLitePCL.Batteries_V2.Init();
            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();
            var modelInfo = SelectAssetFromLibraryDb(connection, "Model", modelSelector);
            if (modelInfo == null)
            {
                Logger.Error("No model matched --preview_model for --pack_model_animations_from_library.");
                return;
            }

            var modelName = (string)modelInfo["name"];
            var candidates = SelectPackAnimationCandidatesFromLibraryDb(connection, modelInfo, animationSelectors, Math.Max(1, limit), libraryRoot);
            if (candidates.Count == 0)
            {
                Logger.Error("No deterministic SQLite animation candidates matched --pack_animations.");
                return;
            }
            ResolvePathProperty(modelInfo, libraryRoot, "output");
            ResolvePathProperty(modelInfo, libraryRoot, "modelPreview");

            var output = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(libraryRoot, "AnimationPacks", SafeName(modelName ?? "Model"))
                : outputDirectory;
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
            Directory.CreateDirectory(output);

            Logger.Info($"Generating SQLite animation pack: {modelName} + {candidates.Count} animation(s)");
            var results = new List<object>();
            foreach (var candidate in candidates)
            {
                var animationName = (string)candidate["name"];
                var itemOutput = Path.Combine(output, SafeName(animationName ?? "Animation"));
                var selection = new PreviewSelection(
                    new JObject
                    {
                        ["model"] = modelInfo.DeepClone(),
                        ["candidateCount"] = candidates.Count,
                        ["candidates"] = new JArray(candidate),
                    },
                    candidate);
                var sourceIndex = ResolveExistingSourceIndex(libraryRoot);
                GenerateSelection(dbPath, gameName, selection, itemOutput, sourceRootOverride, sourceIndex);

                var validationPath = Path.Combine(itemOutput, "preview_validation.json");
                JObject validation = null;
                if (File.Exists(validationPath))
                {
                    validation = JObject.Parse(File.ReadAllText(validationPath));
                }

                var gltfPath = validation == null
                    ? Directory.EnumerateFiles(itemOutput, "*.gltf", SearchOption.AllDirectories).FirstOrDefault()
                    : (string)validation["gltf"];
                results.Add(BuildPackResult(modelName, animationName, gltfPath, validationPath, validation));
            }

            var report = new
            {
                generatedAt = DateTime.UtcNow.ToString("O"),
                rule = "Each item is generated from library_index.db model_animation_candidates. Candidates must already come from Unity deterministic relations; this command does not run model x animation guessing.",
                libraryRoot,
                dbPath,
                sourceRootOverride,
                model = modelInfo,
                requestedAnimations = animationSelectors,
                count = results.Count,
                animations = results,
            };
            var reportPath = Path.Combine(output, "animation_pack_report.json");
            File.WriteAllText(reportPath, JsonConvert.SerializeObject(report, Formatting.Indented));
            Logger.Info($"Animation pack report: {reportPath}");
        }

        public static void ValidatePreviewBatchFromLibrary(
            string libraryRoot,
            string gameName,
            string modelSelector,
            string animationSelectors,
            string outputDirectory,
            int limit,
            string validationKind,
            bool force,
            string sourceRootOverride = null,
            string indexPathOverride = null)
        {
            if (string.IsNullOrWhiteSpace(gameName))
            {
                Logger.Error("--game is required with --validate_animation_previews_from_library.");
                return;
            }
            if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
            {
                Logger.Error($"Library root not found: {libraryRoot}");
                return;
            }

            var dbPath = string.IsNullOrWhiteSpace(indexPathOverride)
                ? Path.Combine(libraryRoot, "library_index.db")
                : indexPathOverride;
            if (!File.Exists(dbPath))
            {
                Logger.Error($"library_index.db not found: {dbPath}. Rebuild the Library export or run --build_sqlite_index.");
                return;
            }

            var output = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(libraryRoot, "AnimationPreviewValidation")
                : outputDirectory;
            Directory.CreateDirectory(output);

            SQLitePCL.Batteries_V2.Init();
            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();
            EnsurePreviewCacheTable(connection);

            var normalizedValidationKind = NormalizePreviewValidationKind(validationKind);
            if (limit <= 0)
            {
                Logger.Info("Preview validation limit is 0; writing animation preview cache summary only.");
                WritePreviewCacheSummary(connection, libraryRoot, dbPath, output, modelSelector, animationSelectors, normalizedValidationKind);
                return;
            }

            var candidates = SelectPreviewValidationCandidates(connection, modelSelector, animationSelectors, normalizedValidationKind, Math.Max(1, limit), libraryRoot, force);
            if (candidates.Count == 0)
            {
                var cacheHint = force ? string.Empty : " uncached";
                Logger.Warning($"No{cacheHint} deterministic SQLite preview candidates matched the validation selectors.");
                WritePreviewCacheSummary(connection, libraryRoot, dbPath, output, modelSelector, animationSelectors, normalizedValidationKind);
                return;
            }

            Logger.Info($"Validating SQLite animation previews: {candidates.Count} candidate(s){(force ? " (force re-run enabled)" : string.Empty)}");
            var sourceIndex = ResolveExistingSourceIndex(libraryRoot);
            var results = new List<object>();
            var okCount = 0;
            foreach (var item in candidates)
            {
                var modelName = (string)item.Model["name"];
                var animationName = (string)item.Animation["name"];
                var itemOutput = Path.Combine(output, $"{SafeName(modelName ?? "Model")}__{SafeName(animationName ?? "Animation")}");
                ResolvePathProperty(item.Model, libraryRoot, "output");
                ResolvePathProperty(item.Model, libraryRoot, "modelPreview");
                ResolvePathProperty(item.Animation, libraryRoot, "output");
                ResolvePathProperty(item.Animation, libraryRoot, "animationAsset");

                var selection = new PreviewSelection(
                    new JObject
                    {
                        ["model"] = item.Model,
                        ["candidateCount"] = 1,
                        ["candidates"] = new JArray(item.Animation),
                    },
                    item.Animation);

                string gltfPath = null;
                string validationPath = null;
                JObject validation = null;
                string status = "failed";
                string message = null;
                try
                {
                    gltfPath = GenerateSelection(dbPath, gameName, selection, itemOutput, sourceRootOverride, sourceIndex);
                    validationPath = Path.Combine(itemOutput, "preview_validation.json");
                    if (File.Exists(validationPath))
                    {
                        validation = JObject.Parse(File.ReadAllText(validationPath));
                    }
                    status = (string)validation?["status"]
                        ?? (gltfPath == null ? "failed" : "warning");
                    message = (string)validation?["status"] ?? (gltfPath == null ? "preview_generation_failed" : "preview_validation_missing");
                    if (TryReadGeneratedDecodedStatus(itemOutput, animationName, out var decodedStatus, out var decodedHint)
                        && string.Equals(decodedStatus, "empty_humanoid_clip", StringComparison.OrdinalIgnoreCase))
                    {
                        status = "empty_humanoid_clip";
                        message = decodedHint;
                    }
                }
                catch (Exception e)
                {
                    message = e.GetType().Name + ": " + e.Message;
                    Logger.Warning($"Preview validation failed for {modelName} + {animationName}: {message}");
                }

                var solverVersion = ResolvePreviewCacheSolverVersion(validation);
                var diagnosticStatus = ResolvePreviewCacheDiagnosticStatus(validation);
                var internalSolved = ResolvePreviewCacheInternalSolved(validation);
                var muscleCurveCount = ResolvePreviewCacheMuscleCurveCount(validation);
                UpsertPreviewCache(
                    connection,
                    item.ModelOutput,
                    item.AnimationOutput,
                    status,
                    gltfPath,
                    validationPath,
                    message,
                    solverVersion,
                    diagnosticStatus,
                    internalSolved,
                    muscleCurveCount);
                if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    okCount++;
                }
                results.Add(new
                {
                    model = modelName,
                    animation = animationName,
                    modelOutput = item.ModelOutput,
                    animationOutput = item.AnimationOutput,
                    status,
                    gltfPath,
                    validationPath,
                    validation,
                    message,
                });
            }

            var report = new
            {
                generatedAt = DateTime.UtcNow.ToString("O"),
                rule = "Batch validates only deterministic SQLite model-animation candidates (relation_source=explicit). It writes animation_preview_cache and does not guess model-animation relations.",
                libraryRoot,
                dbPath,
                sourceRootOverride,
                modelSelector,
                animationSelectors,
                previewValidationKind = normalizedValidationKind,
                previewValidationForce = force,
                limit,
                count = results.Count,
                ok = okCount,
                results,
            };
            var reportPath = Path.Combine(output, "animation_preview_validation_report.json");
            File.WriteAllText(reportPath, JsonConvert.SerializeObject(report, Formatting.Indented));
            Logger.Info($"Animation preview validation report: {reportPath}");
            WritePreviewCacheSummary(connection, libraryRoot, dbPath, output, modelSelector, animationSelectors, normalizedValidationKind);
        }

        private static bool TryReadGeneratedDecodedStatus(string itemOutput, string animationName, out string status, out string hint)
        {
            status = null;
            hint = null;
            if (string.IsNullOrWhiteSpace(itemOutput) || !Directory.Exists(itemOutput))
            {
                return false;
            }

            var files = Directory.EnumerateFiles(itemOutput, "*.animation_asset.json", SearchOption.AllDirectories).ToArray();
            if (files.Length == 0)
            {
                return false;
            }

            var safeAnimation = SafeName(animationName ?? string.Empty);
            var selected = files
                .OrderByDescending(x => string.Equals(Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(x)), animationName, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x => string.Equals(Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(x)), safeAnimation, StringComparison.OrdinalIgnoreCase))
                .ThenBy(x => x.Length)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(selected))
            {
                return false;
            }

            var json = JObject.Parse(File.ReadAllText(selected));
            var decoded = json["decoded"] as JObject;
            status = (string)decoded?["status"];
            if (string.IsNullOrWhiteSpace(status))
            {
                return false;
            }

            var note = (string)decoded["note"];
            var decoderInput = decoded["decoderInput"] as JObject;
            if (decoderInput != null)
            {
                hint = $"decoded.status={status}; bindings={(int?)decoderInput["clipBindingCount"] ?? 0}; valueBindings={(int?)decoderInput["valueBindingCount"] ?? 0}; acl={(int?)decoderInput["aclCurveCount"] ?? 0}; streamed={(int?)decoderInput["streamedCurveCount"] ?? 0}; dense={(int?)decoderInput["denseCurveCount"] ?? 0}; constant={(int?)decoderInput["constantValueCount"] ?? 0}";
            }
            else
            {
                hint = $"decoded.status={status}";
            }
            if (!string.IsNullOrWhiteSpace(note))
            {
                hint += $"; note={note}";
            }
            return true;
        }

        private static void WritePreviewCacheSummary(
            SqliteConnection connection,
            string libraryRoot,
            string dbPath,
            string output,
            string modelSelector,
            string animationSelectors,
            string validationKind)
        {
            var totalExplicitCandidates = HasTable(connection, "model_animation_candidate_model_summary")
                ? ScalarLong(connection, "SELECT COALESCE(SUM(explicit_count), 0) FROM model_animation_candidate_model_summary;")
                : ScalarLong(connection, "SELECT COUNT(*) FROM model_animation_candidates WHERE relation_source='explicit';");
            var directPreviewCandidates = HasTable(connection, "model_animation_candidate_model_summary")
                ? ScalarLong(connection, "SELECT COALESCE(SUM(direct_preview_count), 0) FROM model_animation_candidate_model_summary;")
                : ScalarLong(connection, @"SELECT COUNT(*) FROM model_animation_candidates WHERE relation_source='explicit' AND COALESCE(json_extract(raw_json, '$.nextAction'), '')='generate_preview_gltf' AND COALESCE(json_extract(raw_json, '$.requiresInternalHumanoidSolve'), 0)=0;");
            var internalHumanoidCandidates = HasTable(connection, "model_animation_candidate_model_summary")
                ? ScalarLong(connection, "SELECT COALESCE(SUM(internal_humanoid_count), 0) FROM model_animation_candidate_model_summary;")
                : ScalarLong(connection, @"SELECT COUNT(*) FROM model_animation_candidates WHERE relation_source='explicit' AND (
    COALESCE(json_extract(raw_json, '$.requiresInternalHumanoidSolve'), 0)=1
    OR COALESCE(json_extract(raw_json, '$.missingInternalHumanoidSolver'), 0)=1
    OR COALESCE(json_extract(raw_json, '$.nextAction'), '')='inspect_missing_humanoid_avatar'
);");
            var legacyUnityBakeCandidates = HasTable(connection, "model_animation_candidate_model_summary")
                ? ScalarLong(connection, "SELECT COALESCE(SUM(legacy_unity_bake_count), 0) FROM model_animation_candidate_model_summary;")
                : ScalarLong(connection, @"SELECT COUNT(*) FROM model_animation_candidates WHERE relation_source='explicit' AND COALESCE(json_extract(raw_json, '$.legacyUnityBakeSupported'), 0)=1;");
            EnsureInternalSolverModelTempTable(connection);
            var internalHumanoidProcessableCandidates = HasTable(connection, "model_animation_candidate_model_summary")
                ? ScalarLong(connection, @"
SELECT COALESCE(SUM(s.internal_humanoid_count), 0)
FROM model_animation_candidate_model_summary s
JOIN temp_internal_solver_models m ON m.output=s.model_output;")
                : ScalarLong(connection, @"
SELECT COUNT(*)
FROM model_animation_candidates c
JOIN temp_internal_solver_models m ON m.output=c.model_output
WHERE c.relation_source='explicit'
  AND COALESCE(json_extract(c.raw_json, '$.nextAction'), '')='generate_preview_gltf'
  AND COALESCE(json_extract(c.raw_json, '$.requiresInternalHumanoidSolve'), 0)=1;");
            var internalHumanoidMissingModelSolverCandidates = Math.Max(0, internalHumanoidCandidates - internalHumanoidProcessableCandidates);

            var cachedExplicitCandidates = ScalarLong(connection, @"
SELECT COUNT(*)
FROM animation_preview_cache;");
            var okCachedCandidates = ScalarLong(connection, @"
SELECT COUNT(*)
FROM animation_preview_cache
WHERE status='ok';");
            var emptyHumanoidClipCandidates = ScalarLong(connection, @"
SELECT COUNT(*)
FROM animation_preview_cache
WHERE status='empty_humanoid_clip';");
            var noHumanoidMuscleCurveCandidates = ScalarLong(connection, @"
SELECT COUNT(*)
FROM animation_preview_cache
WHERE diagnostic_status='no_humanoid_muscle_curves';");
            var knownLimbFormulaRiskCandidates = ScalarLong(connection, @"
SELECT COUNT(*)
FROM animation_preview_cache
WHERE diagnostic_status='experimental_solved_known_limb_formula_risk';");
            var internalSolvedCachedCandidates = ScalarLong(connection, @"
SELECT COUNT(*)
FROM animation_preview_cache
WHERE internal_solved=1;");
            var currentHumanoidSolverSql = SqlStringLiteral(FastPreviewGltfBuilder.HumanoidSolverCacheVersion);
            var cachedInternalHumanoidCandidates = ScalarLong(connection, $@"
SELECT COUNT(*)
FROM animation_preview_cache cache
JOIN temp_internal_solver_models m ON m.output=cache.model_output
WHERE EXISTS (
    SELECT 1
    FROM model_animation_candidates c
    WHERE c.model_output=cache.model_output
      AND c.animation_output=cache.animation_output
      AND c.relation_source='explicit'
      AND COALESCE(json_extract(c.raw_json, '$.nextAction'), '')='generate_preview_gltf'
      AND COALESCE(json_extract(c.raw_json, '$.requiresInternalHumanoidSolve'), 0)=1
)
  AND COALESCE(cache.solver_version, '')={currentHumanoidSolverSql};");
            var okInternalHumanoidCandidates = ScalarLong(connection, $@"
SELECT COUNT(*)
FROM animation_preview_cache cache
JOIN temp_internal_solver_models m ON m.output=cache.model_output
WHERE cache.status='ok'
  AND EXISTS (
    SELECT 1
    FROM model_animation_candidates c
    WHERE c.model_output=cache.model_output
      AND c.animation_output=cache.animation_output
      AND c.relation_source='explicit'
      AND COALESCE(json_extract(c.raw_json, '$.nextAction'), '')='generate_preview_gltf'
      AND COALESCE(json_extract(c.raw_json, '$.requiresInternalHumanoidSolve'), 0)=1
)
  AND COALESCE(cache.solver_version, '')={currentHumanoidSolverSql};");
            var warningInternalHumanoidKnownLimbRiskCandidates = ScalarLong(connection, $@"
SELECT COUNT(*)
FROM animation_preview_cache cache
JOIN temp_internal_solver_models m ON m.output=cache.model_output
WHERE cache.status='warning'
  AND cache.diagnostic_status='experimental_solved_known_limb_formula_risk'
  AND EXISTS (
    SELECT 1
    FROM model_animation_candidates c
    WHERE c.model_output=cache.model_output
      AND c.animation_output=cache.animation_output
      AND c.relation_source='explicit'
      AND COALESCE(json_extract(c.raw_json, '$.nextAction'), '')='generate_preview_gltf'
      AND COALESCE(json_extract(c.raw_json, '$.requiresInternalHumanoidSolve'), 0)=1
)
  AND COALESCE(cache.solver_version, '')={currentHumanoidSolverSql};");

            var summary = new JObject
            {
                ["generatedAt"] = DateTime.UtcNow.ToString("O"),
                ["rule"] = "Only deterministic SQLite model-animation candidates (relation_source=explicit) are counted. This report measures validation progress; it does not create or infer new model-animation relations.",
                ["libraryRoot"] = libraryRoot,
                ["dbPath"] = dbPath,
                ["modelSelector"] = modelSelector,
                ["animationSelectors"] = animationSelectors,
                ["previewValidationKind"] = validationKind,
                ["totals"] = new JObject
                {
                    ["models"] = ScalarLong(connection, "SELECT COUNT(*) FROM assets WHERE kind='Model';"),
                    ["animations"] = ScalarLong(connection, "SELECT COUNT(*) FROM assets WHERE kind='Animation';"),
                    ["modelsWithExplicitCandidates"] = HasTable(connection, "model_animation_candidate_model_summary")
                        ? ScalarLong(connection, "SELECT COUNT(*) FROM model_animation_candidate_model_summary WHERE explicit_count > 0;")
                        : ScalarLong(connection, "SELECT COUNT(DISTINCT model_output) FROM model_animation_candidates WHERE relation_source='explicit';"),
                    ["animationsWithExplicitCandidates"] = HasTable(connection, "model_animation_candidate_animation_summary")
                        ? ScalarLong(connection, "SELECT COUNT(*) FROM model_animation_candidate_animation_summary WHERE explicit_model_count > 0;")
                        : ScalarLong(connection, "SELECT COUNT(DISTINCT animation_output) FROM model_animation_candidates WHERE relation_source='explicit';"),
                    ["explicitCandidates"] = totalExplicitCandidates,
                    ["directPreviewCandidates"] = directPreviewCandidates,
                    ["internalHumanoidCandidates"] = internalHumanoidCandidates,
                    ["internalHumanoidProcessableCandidates"] = internalHumanoidProcessableCandidates,
                    ["internalHumanoidMissingModelSolverCandidates"] = internalHumanoidMissingModelSolverCandidates,
                    ["legacyUnityBakeCandidates"] = legacyUnityBakeCandidates,
                },
                ["cache"] = new JObject
                {
                    ["currentHumanoidSolverVersion"] = FastPreviewGltfBuilder.HumanoidSolverCacheVersion,
                    ["cachedExplicitCandidates"] = cachedExplicitCandidates,
                    ["okCachedCandidates"] = okCachedCandidates,
                    ["emptyHumanoidClipCandidates"] = emptyHumanoidClipCandidates,
                    ["noHumanoidMuscleCurveCandidates"] = noHumanoidMuscleCurveCandidates,
                    ["knownLimbFormulaRiskCandidates"] = knownLimbFormulaRiskCandidates,
                    ["internalSolvedCachedCandidates"] = internalSolvedCachedCandidates,
                    ["statusCounts"] = QueryGroupedCounts(connection, @"
SELECT status, COUNT(*)
FROM animation_preview_cache
GROUP BY status
ORDER BY COUNT(*) DESC, status COLLATE NOCASE;"),
                    ["diagnosticStatusCounts"] = QueryGroupedCounts(connection, @"
SELECT COALESCE(NULLIF(diagnostic_status, ''), 'Unknown'), COUNT(*)
FROM animation_preview_cache
GROUP BY COALESCE(NULLIF(diagnostic_status, ''), 'Unknown')
ORDER BY COUNT(*) DESC, COALESCE(NULLIF(diagnostic_status, ''), 'Unknown') COLLATE NOCASE;"),
                    ["coverageOfExplicitCandidates"] = Ratio(cachedExplicitCandidates, totalExplicitCandidates),
                    ["okCoverageOfExplicitCandidates"] = Ratio(okCachedCandidates, totalExplicitCandidates),
                    ["okCoverageExcludingEmptyHumanoidClips"] = Ratio(okCachedCandidates, Math.Max(0, totalExplicitCandidates - emptyHumanoidClipCandidates)),
                    ["okRateOfCachedCandidates"] = Ratio(okCachedCandidates, cachedExplicitCandidates),
                    ["cachedInternalHumanoidCandidates"] = cachedInternalHumanoidCandidates,
                    ["okInternalHumanoidCandidates"] = okInternalHumanoidCandidates,
                    ["warningInternalHumanoidKnownLimbRiskCandidates"] = warningInternalHumanoidKnownLimbRiskCandidates,
                    ["coverageOfInternalHumanoidProcessableCandidates"] = Ratio(cachedInternalHumanoidCandidates, internalHumanoidProcessableCandidates),
                    ["okCoverageOfInternalHumanoidProcessableCandidates"] = Ratio(okInternalHumanoidCandidates, internalHumanoidProcessableCandidates),
                    ["okRateOfCachedInternalHumanoidCandidates"] = Ratio(okInternalHumanoidCandidates, cachedInternalHumanoidCandidates),
                },
                ["modelResourceKinds"] = QueryPreviewCacheByModelResourceKind(connection),
                ["animationCapabilities"] = QueryPreviewCacheByAnimationCapability(connection),
            };

            var summaryPath = Path.Combine(output, "animation_preview_cache_summary.json");
            File.WriteAllText(summaryPath, JsonConvert.SerializeObject(summary, Formatting.Indented));
            Logger.Info($"Animation preview cache summary: {summaryPath}");

            var rootSummaryPath = Path.Combine(libraryRoot, "animation_preview_cache_summary.json");
            if (!string.Equals(Path.GetFullPath(summaryPath), Path.GetFullPath(rootSummaryPath), StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(rootSummaryPath, JsonConvert.SerializeObject(summary, Formatting.Indented));
                Logger.Info($"Animation preview cache summary: {rootSummaryPath}");
            }
        }

        private static JArray QueryPreviewCacheByModelResourceKind(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT COALESCE(NULLIF(m.resource_kind, ''), 'Unknown') AS resource_kind,
       cache.status,
       COUNT(*) AS count
FROM animation_preview_cache cache
JOIN assets m ON m.kind='Model' AND m.output=cache.model_output
GROUP BY COALESCE(NULLIF(m.resource_kind, ''), 'Unknown'), cache.status
ORDER BY count DESC, resource_kind COLLATE NOCASE, cache.status COLLATE NOCASE;";
            var rows = new JArray();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new JObject
                {
                    ["resourceKind"] = reader.GetString(0),
                    ["status"] = reader.GetString(1),
                    ["count"] = reader.GetInt64(2),
                });
            }
            return rows;
        }

        private static void EnsureInternalSolverModelTempTable(SqliteConnection connection)
        {
            using var create = connection.CreateCommand();
            create.CommandText = @"
CREATE TEMP TABLE IF NOT EXISTS temp_internal_solver_models (
    output TEXT PRIMARY KEY
) WITHOUT ROWID;";
            create.ExecuteNonQuery();

            using var clear = connection.CreateCommand();
            clear.CommandText = "DELETE FROM temp_internal_solver_models;";
            clear.ExecuteNonQuery();

            using var insert = connection.CreateCommand();
            insert.CommandText = @"
INSERT OR IGNORE INTO temp_internal_solver_models(output)
SELECT output
FROM assets
WHERE kind='Model'
  AND output IS NOT NULL
  AND COALESCE(json_array_length(json_extract(raw_json, '$.avatar.internalSolver.skeleton.nodes')), 0) > 0
  AND COALESCE(json_array_length(json_extract(raw_json, '$.avatar.internalSolver.skeleton.humanSkeletonPose')), 0)
      >= COALESCE(json_array_length(json_extract(raw_json, '$.avatar.internalSolver.skeleton.nodes')), 0)
  AND COALESCE(json_array_length(json_extract(raw_json, '$.avatar.internalSolver.humanBoneIndex')), 0) > 0;";
            insert.ExecuteNonQuery();
        }

        private static JArray QueryPreviewCacheByAnimationCapability(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT COALESCE(NULLIF(json_extract(a.raw_json, '$.animationCapability'), ''), NULLIF(a.animation_type, ''), 'Unknown') AS capability,
       cache.status,
       COUNT(*) AS count
FROM animation_preview_cache cache
JOIN assets a ON a.kind='Animation' AND a.output=cache.animation_output
GROUP BY COALESCE(NULLIF(json_extract(a.raw_json, '$.animationCapability'), ''), NULLIF(a.animation_type, ''), 'Unknown'), cache.status
ORDER BY count DESC, capability COLLATE NOCASE, cache.status COLLATE NOCASE;";
            var rows = new JArray();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new JObject
                {
                    ["animationCapability"] = reader.GetString(0),
                    ["status"] = reader.GetString(1),
                    ["count"] = reader.GetInt64(2),
                });
            }
            return rows;
        }

        private static JObject QueryGroupedCounts(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            var result = new JObject();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result[reader.GetString(0)] = reader.GetInt64(1);
            }
            return result;
        }

        private static long ScalarLong(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            var value = command.ExecuteScalar();
            if (value == null || value == DBNull.Value)
            {
                return 0;
            }
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        private static bool HasTable(SqliteConnection connection, string tableName)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
            command.Parameters.AddWithValue("$name", tableName);
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
        }

        private static double Ratio(long numerator, long denominator)
        {
            return denominator <= 0 ? 0 : Math.Round((double)numerator / denominator, 6);
        }

        private static string SqlStringLiteral(string value)
        {
            return "'" + (value ?? string.Empty).Replace("'", "''") + "'";
        }

        private static List<PreviewValidationCandidate> SelectPreviewValidationCandidates(
            SqliteConnection connection,
            string modelSelector,
            string animationSelectors,
            string validationKind,
            int limit,
            string libraryRoot,
            bool force)
        {
            EnsureInternalSolverModelTempTable(connection);
            using var command = connection.CreateCommand();
            var hasModelSelector = CanUseSelectorSqlPrefilter(modelSelector);
            var hasAnimationSelector = CanUseSelectorSqlPrefilter(animationSelectors);
            var modelSelectorClause = BuildAssetSelectorSqlClause(command, "modelMatch", modelSelector, "model");
            var animationSelectorClause = BuildAssetSelectorSqlClause(command, "animationMatch", animationSelectors, "animation");
            var validationKindClause = BuildPreviewValidationKindSqlClause(validationKind);
            var cacheClause = force
                ? "1=1"
                : @"
  (
    cache.model_output IS NULL
    OR (
      COALESCE(json_extract(c.raw_json, '$.requiresInternalHumanoidSolve'), 0) = 1
      AND COALESCE(cache.solver_version, '') <> $humanoidSolverVersion
    )
  )";
            command.CommandText = $@"
SELECT m.raw_json, a.raw_json, c.raw_json, c.model_output, c.animation_output
FROM model_animation_candidates c
JOIN assets m ON m.kind='Model' AND m.output=c.model_output
JOIN assets a ON a.kind='Animation' AND a.output=c.animation_output
LEFT JOIN animation_preview_cache cache
  ON cache.model_output=c.model_output AND cache.animation_output=c.animation_output
WHERE c.relation_source='explicit'
  AND COALESCE(c.status, '') <> 'model_not_animation_ready'
  AND COALESCE(json_extract(c.raw_json, '$.modelReadyForAnimation'), 0) = 1
  AND COALESCE(json_extract(c.raw_json, '$.modelAnimationGate.ready'), 0) = 1
  AND COALESCE(json_extract(c.raw_json, '$.nextAction'), '') = 'generate_preview_gltf'
  AND (
    COALESCE(json_extract(c.raw_json, '$.requiresInternalHumanoidSolve'), 0) = 0
    OR c.model_output IN (SELECT output FROM temp_internal_solver_models)
  )
  AND {cacheClause}
  AND {validationKindClause}
  AND (
    $hasModelSelector = 0
    OR c.model_output IN (
      SELECT modelMatch.output
      FROM assets modelMatch
      WHERE modelMatch.kind='Model' AND {modelSelectorClause}
      LIMIT 64
    )
  )
  AND (
    $hasAnimationSelector = 0
    OR c.animation_output IN (
      SELECT animationMatch.output
      FROM assets animationMatch
      WHERE animationMatch.kind='Animation' AND {animationSelectorClause}
      LIMIT 256
    )
  )
ORDER BY
  COALESCE(json_extract(c.raw_json, '$.requiresInternalHumanoidSolve'), 0) ASC,
  c.score DESC,
  m.name COLLATE NOCASE,
  a.name COLLATE NOCASE
LIMIT 8192;";
            command.Parameters.AddWithValue("$hasModelSelector", hasModelSelector ? 1 : 0);
            command.Parameters.AddWithValue("$hasAnimationSelector", hasAnimationSelector ? 1 : 0);
            command.Parameters.AddWithValue("$humanoidSolverVersion", FastPreviewGltfBuilder.HumanoidSolverCacheVersion);

            var result = new List<PreviewValidationCandidate>();
            using var reader = command.ExecuteReader();
            while (reader.Read() && result.Count < limit)
            {
                var model = JObject.Parse(reader.GetString(0));
                var animation = JObject.Parse(reader.GetString(1));
                var relation = JObject.Parse(reader.GetString(2));
                var modelOutput = reader.GetString(3);
                var animationOutput = reader.GetString(4);

                if (!Matches(modelSelector, (string)model["name"], modelOutput))
                {
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(animationSelectors)
                    && !FilterPackCandidates(new[] { animation }, animationSelectors).Any())
                {
                    continue;
                }

                animation["relation"] = (string)relation["relation"] ?? (string)relation["relationSource"] ?? "library.sqlite.candidate";
                animation["relationSource"] = (string)relation["relationSource"] ?? "explicit";
                animation["confidence"] = (string)relation["confidence"] ?? "explicit_unity_source_index";
                animation["score"] = (int?)relation["score"] ?? 100;
                animation["candidate"] = relation;
                result.Add(new PreviewValidationCandidate(model, animation, modelOutput, animationOutput));
            }
            return result;
        }

        private static string BuildPreviewValidationKindSqlClause(string validationKind)
        {
            return validationKind switch
            {
                "Direct" => "COALESCE(json_extract(c.raw_json, '$.requiresInternalHumanoidSolve'), 0) = 0 AND COALESCE(json_extract(c.raw_json, '$.missingInternalHumanoidSolver'), 0) = 0",
                "InternalHumanoid" => "COALESCE(json_extract(c.raw_json, '$.requiresInternalHumanoidSolve'), 0) = 1 AND c.model_output IN (SELECT output FROM temp_internal_solver_models)",
                _ => "1=1",
            };
        }

        private static string NormalizePreviewValidationKind(string validationKind)
        {
            var text = (validationKind ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text)
                || text.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                return "All";
            }
            if (text.Equals("Direct", StringComparison.OrdinalIgnoreCase))
            {
                return "Direct";
            }
            if (text.Equals("InternalHumanoid", StringComparison.OrdinalIgnoreCase)
                || text.Equals("Internal_Humanoid", StringComparison.OrdinalIgnoreCase)
                || text.Equals("internal-humanoid", StringComparison.OrdinalIgnoreCase)
                || text.Equals("humanoid", StringComparison.OrdinalIgnoreCase))
            {
                return "InternalHumanoid";
            }

            Logger.Warning($"Unknown --preview_validation_kind '{validationKind}', falling back to All.");
            return "All";
        }

        private static void EnsurePreviewCacheTable(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
CREATE TABLE IF NOT EXISTS animation_preview_cache (
    model_output TEXT NOT NULL,
    animation_output TEXT NOT NULL,
    status TEXT NOT NULL,
    gltf_path TEXT,
    validation_path TEXT,
    message TEXT,
    solver_version TEXT,
    diagnostic_status TEXT,
    internal_solved INTEGER,
    muscle_curve_count INTEGER,
    updated_utc TEXT,
    PRIMARY KEY(model_output, animation_output)
);";
            command.ExecuteNonQuery();

            EnsurePreviewCacheColumn(connection, "solver_version", "TEXT");
            EnsurePreviewCacheColumn(connection, "diagnostic_status", "TEXT");
            EnsurePreviewCacheColumn(connection, "internal_solved", "INTEGER");
            EnsurePreviewCacheColumn(connection, "muscle_curve_count", "INTEGER");
        }

        private static void UpsertPreviewCache(
            SqliteConnection connection,
            string modelOutput,
            string animationOutput,
            string status,
            string gltfPath,
            string validationPath,
            string message,
            string solverVersion,
            string diagnosticStatus,
            bool? internalSolved,
            int? muscleCurveCount)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO animation_preview_cache(model_output, animation_output, status, gltf_path, validation_path, message, solver_version, diagnostic_status, internal_solved, muscle_curve_count, updated_utc)
VALUES ($modelOutput, $animationOutput, $status, $gltfPath, $validationPath, $message, $solverVersion, $diagnosticStatus, $internalSolved, $muscleCurveCount, $updatedUtc)
ON CONFLICT(model_output, animation_output) DO UPDATE SET
    status=excluded.status,
    gltf_path=excluded.gltf_path,
    validation_path=excluded.validation_path,
    message=excluded.message,
    solver_version=excluded.solver_version,
    diagnostic_status=excluded.diagnostic_status,
    internal_solved=excluded.internal_solved,
    muscle_curve_count=excluded.muscle_curve_count,
    updated_utc=excluded.updated_utc;";
            command.Parameters.AddWithValue("$modelOutput", modelOutput ?? string.Empty);
            command.Parameters.AddWithValue("$animationOutput", animationOutput ?? string.Empty);
            command.Parameters.AddWithValue("$status", status ?? "failed");
            command.Parameters.AddWithValue("$gltfPath", (object)gltfPath ?? DBNull.Value);
            command.Parameters.AddWithValue("$validationPath", (object)validationPath ?? DBNull.Value);
            command.Parameters.AddWithValue("$message", (object)message ?? DBNull.Value);
            command.Parameters.AddWithValue("$solverVersion", (object)solverVersion ?? DBNull.Value);
            command.Parameters.AddWithValue("$diagnosticStatus", (object)diagnosticStatus ?? DBNull.Value);
            command.Parameters.AddWithValue("$internalSolved", internalSolved.HasValue ? (object)(internalSolved.Value ? 1 : 0) : DBNull.Value);
            command.Parameters.AddWithValue("$muscleCurveCount", muscleCurveCount.HasValue ? (object)muscleCurveCount.Value : DBNull.Value);
            command.Parameters.AddWithValue("$updatedUtc", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        private static void EnsurePreviewCacheColumn(SqliteConnection connection, string columnName, string sqlType)
        {
            using var check = connection.CreateCommand();
            check.CommandText = "PRAGMA table_info(animation_preview_cache);";
            using (var reader = check.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
            }

            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE animation_preview_cache ADD COLUMN {columnName} {sqlType};";
            alter.ExecuteNonQuery();
        }

        private static string ResolvePreviewCacheSolverVersion(JObject validation)
        {
            var humanoid = validation?["humanoid"] as JObject;
            if ((bool?)humanoid?["internalSolved"] != true)
            {
                return null;
            }

            var explicitVersion = (string)humanoid["solverCacheVersion"];
            if (!string.IsNullOrWhiteSpace(explicitVersion))
            {
                return explicitVersion;
            }

            var solveMode = (string)humanoid["solveMode"];
            var diagnostics = humanoid["diagnostics"] as JObject;
            var restPoseSpace = (string)diagnostics?["restPoseSpace"];
            if (string.Equals(solveMode, FastPreviewGltfBuilder.HumanoidSolverMode, StringComparison.Ordinal)
                && string.Equals(restPoseSpace, FastPreviewGltfBuilder.HumanoidSolverRestPoseSpace, StringComparison.Ordinal))
            {
                return FastPreviewGltfBuilder.HumanoidSolverCacheVersion;
            }

            // 老缓存缺少 restPoseSpace 时不能继续算作可视验收通过。
            return string.IsNullOrWhiteSpace(solveMode) ? "unknown_internal_humanoid_solver" : solveMode;
        }

        private static string ResolvePreviewCacheDiagnosticStatus(JObject validation)
        {
            var humanoid = validation?["humanoid"] as JObject;
            var diagnostics = humanoid?["diagnostics"] as JObject;
            var status = (string)diagnostics?["status"];
            return string.IsNullOrWhiteSpace(status) ? null : status;
        }

        private static bool? ResolvePreviewCacheInternalSolved(JObject validation)
        {
            return (bool?)validation?["humanoid"]?["internalSolved"];
        }

        private static int? ResolvePreviewCacheMuscleCurveCount(JObject validation)
        {
            return (int?)validation?["humanoid"]?["muscleCurveCount"];
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

        private static List<JObject> SelectPackAnimations(JObject model, string animationSelectors, int limit)
        {
            var candidates = model["candidates"]?
                .OfType<JObject>()
                .Where(IsExplicitPreviewRelation)
                .Where(x => IsJsonIndexCandidateModelReady(model, x))
                .OrderByDescending(x => (int?)x["score"] ?? 0)
                .ThenBy(x => (string)x["name"], StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<JObject>();
            if (string.IsNullOrWhiteSpace(animationSelectors))
            {
                return candidates.Take(limit).ToList();
            }

            var selectors = animationSelectors
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();
            var selected = new List<JObject>();
            foreach (var selector in selectors)
            {
                selected.AddRange(candidates.Where(x => Matches(selector, (string)x["name"], (string)x["output"])));
            }
            return selected
                .GroupBy(x => (string)x["name"], StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .Take(limit)
                .ToList();
        }

        private static List<JObject> SelectPackAnimationCandidatesFromLibraryDb(
            SqliteConnection connection,
            JObject modelInfo,
            string animationSelectors,
            int limit,
            string libraryRoot)
        {
            var modelOutput = (string)modelInfo?["output"] ?? string.Empty;
            using var command = connection.CreateCommand();
            var hasAnimationSelector = CanUseSelectorSqlPrefilter(animationSelectors);
            var animationSelectorClause = BuildAssetSelectorSqlClause(command, "animationMatch", animationSelectors, "packAnimation");
            command.CommandText = $@"
SELECT a.raw_json, c.raw_json
FROM model_animation_candidates c
JOIN assets a ON a.output = c.animation_output AND a.kind = 'Animation'
WHERE c.model_output = $modelOutput
  AND c.relation_source = 'explicit'
  AND COALESCE(c.status, '') <> 'model_not_animation_ready'
  AND COALESCE(json_extract(c.raw_json, '$.modelReadyForAnimation'), 0) = 1
  AND COALESCE(json_extract(c.raw_json, '$.modelAnimationGate.ready'), 0) = 1
  AND (
    $hasAnimationSelector = 0
    OR c.animation_output IN (
      SELECT animationMatch.output
      FROM assets animationMatch
      WHERE animationMatch.kind='Animation' AND {animationSelectorClause}
      LIMIT 256
    )
  )
ORDER BY c.score DESC, a.name COLLATE NOCASE
LIMIT 4096;";
            command.Parameters.AddWithValue("$modelOutput", modelOutput);
            command.Parameters.AddWithValue("$hasAnimationSelector", hasAnimationSelector ? 1 : 0);

            var candidates = new List<JObject>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var animation = JObject.Parse(reader.GetString(0));
                    var relation = JObject.Parse(reader.GetString(1));
                    ResolvePathProperty(animation, libraryRoot, "output");
                    ResolvePathProperty(animation, libraryRoot, "animationAsset");
                    animation["relation"] = (string)relation["relation"] ?? (string)relation["relationSource"] ?? "library.sqlite.candidate";
                    animation["relationSource"] = (string)relation["relationSource"] ?? "explicit";
                    animation["confidence"] = (string)relation["confidence"] ?? "explicit_unity_source_index";
                    animation["score"] = (int?)relation["score"] ?? 100;
                    animation["candidate"] = relation;
                    candidates.Add(animation);
                }
            }

            var filtered = FilterPackCandidates(candidates, animationSelectors)
                .Take(limit)
                .ToList();
            return filtered;
        }

        private static IEnumerable<JObject> FilterPackCandidates(IEnumerable<JObject> candidates, string animationSelectors)
        {
            var selectors = SplitSelectors(animationSelectors);
            if (selectors.Length == 0)
            {
                return candidates;
            }

            return candidates
                .Where(candidate => selectors.Any(selector => Matches(selector, (string)candidate["name"], (string)candidate["output"])))
                .GroupBy(x => (string)x["name"], StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First());
        }

        private static PreviewSelection SelectPreview(JObject index, string modelSelector, string animationSelector)
        {
            foreach (var model in index["models"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var modelInfo = model["model"] as JObject;
                if (!Matches(modelSelector, (string)modelInfo?["name"], (string)modelInfo?["output"]))
                {
                    continue;
                }
                var animation = model["candidates"]?
                    .OfType<JObject>()
                    .FirstOrDefault(x => IsJsonIndexCandidateModelReady(model, x)
                        && Matches(animationSelector, (string)x["name"], (string)x["output"]));
                if (animation != null)
                {
                    return new PreviewSelection(model, animation);
                }
            }
            return null;
        }

        private static PreviewSelection SelectPreviewFromLibraryDb(string dbPath, string modelSelector, string animationSelector)
        {
            var libraryRoot = Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? string.Empty;
            modelSelector = NormalizeLibrarySelector(libraryRoot, modelSelector);
            animationSelector = NormalizeLibrarySelector(libraryRoot, animationSelector);

            SQLitePCL.Batteries_V2.Init();
            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();

            using var command = connection.CreateCommand();
            var modelSelectorClause = BuildAssetSelectorSqlClause(command, "m", modelSelector, "previewModel");
            var animationSelectorClause = BuildAssetSelectorSqlClause(command, "a", animationSelector, "previewAnimation");
            command.CommandText = $@"
SELECT m.raw_json, a.raw_json, c.raw_json
FROM model_animation_candidates c
JOIN assets m ON m.kind='Model' AND m.output=c.model_output
JOIN assets a ON a.kind='Animation' AND a.output=c.animation_output
WHERE c.relation_source='explicit'
  AND COALESCE(c.status, '') <> 'model_not_animation_ready'
  AND COALESCE(json_extract(c.raw_json, '$.modelReadyForAnimation'), 0) = 1
  AND COALESCE(json_extract(c.raw_json, '$.modelAnimationGate.ready'), 0) = 1
  AND {modelSelectorClause}
  AND {animationSelectorClause}
ORDER BY c.score DESC, m.name COLLATE NOCASE, a.name COLLATE NOCASE
LIMIT 128;";

            var rows = new List<PreviewSelection>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var model = JObject.Parse(reader.GetString(0));
                    var animation = JObject.Parse(reader.GetString(1));
                    var relation = JObject.Parse(reader.GetString(2));
                    if (!Matches(modelSelector, (string)model["name"], (string)model["output"])
                        || !Matches(animationSelector, (string)animation["name"], (string)animation["output"]))
                    {
                        continue;
                    }

                    var candidate = new JObject
                    {
                        ["name"] = animation["name"],
                        ["output"] = animation["output"],
                        ["animationAsset"] = animation["animationAsset"],
                        ["source"] = animation["source"],
                        ["sourceType"] = animation["sourceType"],
                        ["pathId"] = animation["pathId"],
                        ["animationType"] = animation["animationType"],
                        ["animationCapability"] = animation["animationCapability"],
                        ["hasMuscleClip"] = animation["hasMuscleClip"],
                        ["curveCount"] = animation["curveCount"],
                        ["transformBindingCount"] = animation["transformBindingCount"],
                        ["humanoidBindingCount"] = animation["humanoidBindingCount"],
                        ["relation"] = (string)relation["relation"] ?? (string)relation["relationSource"] ?? "library.sqlite.explicit_candidate",
                        ["relationSource"] = (string)relation["relationSource"] ?? "explicit",
                        ["confidence"] = (string)relation["confidence"] ?? "explicit_unity_source_index",
                        ["score"] = (double?)relation["score"] ?? 100,
                        ["candidate"] = relation,
                    };
                    rows.Add(new PreviewSelection(
                        new JObject
                        {
                            ["model"] = model,
                            ["candidateCount"] = 1,
                            ["candidates"] = new JArray(candidate),
                        },
                        candidate));
                }
            }

            return rows.FirstOrDefault();
        }

        private static string NormalizeLibrarySelector(string libraryRoot, string selector)
        {
            if (string.IsNullOrWhiteSpace(selector) || string.IsNullOrWhiteSpace(libraryRoot))
            {
                return selector;
            }

            var parts = SplitSelectors(selector);
            if (parts.Length == 0)
            {
                return selector;
            }

            return string.Join(",", parts.Select(part => NormalizeSingleLibrarySelector(libraryRoot, part)));
        }

        private static string NormalizeSingleLibrarySelector(string libraryRoot, string selector)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                return selector;
            }

            var text = selector.Trim().Trim('"');
            try
            {
                if (!Path.IsPathRooted(text))
                {
                    return text.Replace('\\', '/');
                }

                var root = Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var full = Path.GetFullPath(text);
                var relative = Path.GetRelativePath(root, full);
                if (!relative.StartsWith("..", StringComparison.Ordinal)
                    && !Path.IsPathRooted(relative))
                {
                    return relative.Replace('\\', '/');
                }
            }
            catch
            {
                return text;
            }

            return text.Replace('\\', '/');
        }

        private static bool IsExplicitPreviewRelation(JObject animation)
        {
            return string.Equals((string)animation?["relationSource"], "explicit", StringComparison.OrdinalIgnoreCase)
                || string.Equals((string)animation?["relationSource"], "componentOwner", StringComparison.OrdinalIgnoreCase)
                || string.Equals((string)animation?["relationSource"], "componentOwnerBlendSpaceSample", StringComparison.OrdinalIgnoreCase)
                || string.Equals((string)animation?["relationSource"], "componentAnimClass", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsJsonIndexCandidateModelReady(JObject model, JObject animation)
        {
            return IsSelectionModelReadyForAnimation(new PreviewSelection(model, animation), out _);
        }

        private static bool IsSelectionModelReadyForAnimation(PreviewSelection selection, out string reason)
        {
            reason = null;
            var gate = selection?.Model?["modelAnimationGate"] as JObject
                ?? selection?.Animation?["modelAnimationGate"] as JObject
                ?? selection?.Animation?["candidate"]?["modelAnimationGate"] as JObject;
            var ready = ReadNullableBool(gate?["ready"])
                ?? ReadNullableBool(selection?.Model?["modelReadyForAnimation"])
                ?? ReadNullableBool(selection?.Animation?["modelReadyForAnimation"])
                ?? ReadNullableBool(selection?.Animation?["candidate"]?["modelReadyForAnimation"]);
            var candidateStatus = (string)selection?.Animation?["candidate"]?["status"]
                ?? (string)selection?.Animation?["status"];
            var nextAction = (string)selection?.Animation?["candidate"]?["nextAction"]
                ?? (string)selection?.Animation?["nextAction"];

            if (ready == true
                && !string.Equals(candidateStatus, "model_not_animation_ready", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(nextAction, "fix_model_first", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var reasons = gate?["reasons"]?.OfType<JValue>()
                .Select(x => x.ToString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray() ?? Array.Empty<string>();
            if (ready == false
                || string.Equals(candidateStatus, "model_not_animation_ready", StringComparison.OrdinalIgnoreCase)
                || string.Equals(nextAction, "fix_model_first", StringComparison.OrdinalIgnoreCase))
            {
                reason = "模型第一阶段未通过，不能进入动画预览/打包。"
                    + $" gateStatus={(string)gate?["status"] ?? "(none)"}"
                    + $" reasons={(reasons.Length == 0 ? "(none)" : string.Join(",", reasons))}"
                    + $" candidateStatus={candidateStatus ?? "(none)"}"
                    + $" nextAction={nextAction ?? "(none)"}";
                return false;
            }

            reason = "缺少模型第一阶段验证 gate，不能确认 Mesh/UV/材质/贴图/skin/bbox 已通过；请先重建 Library 索引或修复模型导出，再进入动画阶段。";
            return false;
        }

        private static bool? ReadNullableBool(JToken token)
        {
            return token?.Type switch
            {
                JTokenType.Boolean => token.Value<bool>(),
                JTokenType.Integer => token.Value<int>() != 0,
                JTokenType.String when bool.TryParse(token.ToString(), out var value) => value,
                JTokenType.String when int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value != 0,
                _ => null,
            };
        }

        private static void ResolveSelectionLibraryPaths(PreviewSelection selection, string libraryRoot)
        {
            if (selection?.Model?["model"] is JObject model)
            {
                ResolvePathProperty(model, libraryRoot, "output");
                ResolvePathProperty(model, libraryRoot, "modelPreview");
            }

            if (selection?.Animation is JObject animation)
            {
                ResolvePathProperty(animation, libraryRoot, "output");
                ResolvePathProperty(animation, libraryRoot, "animationAsset");
            }
        }

        private static void ResolvePathProperty(JObject obj, string libraryRoot, string propertyName)
        {
            var value = (string)obj[propertyName];
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            obj[propertyName] = LibraryRelativePathMigrator.ResolveLibraryPath(libraryRoot, value);
        }

        private static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            try
            {
                return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static JObject SelectAssetFromLibraryDb(SqliteConnection connection, string kind, string selector)
        {
            var sqlSelector = NormalizeSelectorForSqlSearch(selector);
            var fileName = string.IsNullOrWhiteSpace(sqlSelector) ? string.Empty : Path.GetFileName(sqlSelector);
            var stem = string.IsNullOrWhiteSpace(fileName) ? string.Empty : Path.GetFileNameWithoutExtension(fileName);
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT raw_json
FROM assets
WHERE kind = $kind
  AND (
    output = $selector
    OR name = $selector
    OR output LIKE $fileNameSelector
    OR name = $stem
    OR output LIKE $likeSelector
    OR name LIKE $likeSelector
  )
ORDER BY
  CASE
    WHEN output = $selector THEN 0
    WHEN name = $selector THEN 1
    WHEN name = $stem THEN 2
    WHEN output LIKE $fileNameSelector THEN 3
    ELSE 4
  END,
  name COLLATE NOCASE
LIMIT 32;";
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$selector", selector ?? string.Empty);
            command.Parameters.AddWithValue("$fileNameSelector", "%" + fileName);
            command.Parameters.AddWithValue("$stem", stem);
            command.Parameters.AddWithValue("$likeSelector", "%" + sqlSelector + "%");

            var rows = new List<JObject>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(JObject.Parse(reader.GetString(0)));
            }

            if (rows.Count == 0)
            {
                return null;
            }

            return rows.FirstOrDefault(x => Matches(selector, (string)x["name"], (string)x["output"]))
                ?? rows[0];
        }

        private static string NormalizeSelectorForSqlSearch(string selector)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                return string.Empty;
            }

            var text = selector.Trim();
            if (text.StartsWith("^", StringComparison.Ordinal))
            {
                text = text[1..];
            }
            if (text.EndsWith("$", StringComparison.Ordinal))
            {
                text = text[..^1];
            }

            // SQL 只做粗筛，真正的正则匹配仍由 Matches 负责；这里去掉常见转义，避免 ^Name$ / Regex.Escape(Name) 查不到行。
            text = text.Replace("\\", string.Empty);
            return text;
        }

        private static string BuildAssetSelectorSqlClause(SqliteCommand command, string tableAlias, string selectorsText, string parameterPrefix)
        {
            var selectors = SplitSelectors(selectorsText);
            if (selectors.Length == 0)
            {
                return "1=1";
            }
            if (!CanUseSelectorSqlPrefilter(selectorsText))
            {
                // SQL 只负责加速简单名字/路径选择器。带分组、分支等正则语义时，必须交给 C# Matches 做最终判断，
                // 否则 "^(Idle|Run)$" 这类合法选择器会在 LIKE 粗筛阶段被误杀。
                return "1=1";
            }

            var clauses = new List<string>();
            for (var i = 0; i < selectors.Length && i < 8; i++)
            {
                var selector = selectors[i];
                var sqlSelector = NormalizeSelectorForSqlSearch(selector);
                var fileName = string.IsNullOrWhiteSpace(sqlSelector) ? string.Empty : Path.GetFileName(sqlSelector);
                var stem = string.IsNullOrWhiteSpace(fileName) ? string.Empty : Path.GetFileNameWithoutExtension(fileName);
                var selectorParameter = "$" + parameterPrefix + "Selector" + i.ToString(CultureInfo.InvariantCulture);
                var fileNameParameter = "$" + parameterPrefix + "FileName" + i.ToString(CultureInfo.InvariantCulture);
                var stemParameter = "$" + parameterPrefix + "Stem" + i.ToString(CultureInfo.InvariantCulture);
                var likeParameter = "$" + parameterPrefix + "Like" + i.ToString(CultureInfo.InvariantCulture);

                clauses.Add($@"(
    {tableAlias}.output = {selectorParameter}
    OR {tableAlias}.name = {selectorParameter}
    OR {tableAlias}.name = {stemParameter}
    OR {tableAlias}.output LIKE {fileNameParameter}
    OR {tableAlias}.output LIKE {likeParameter}
    OR {tableAlias}.name LIKE {likeParameter}
  )");
                command.Parameters.AddWithValue(selectorParameter, selector);
                command.Parameters.AddWithValue(fileNameParameter, "%" + fileName);
                command.Parameters.AddWithValue(stemParameter, stem);
                command.Parameters.AddWithValue(likeParameter, "%" + sqlSelector + "%");
            }

            return "(" + string.Join(" OR ", clauses) + ")";
        }

        private static bool CanUseSelectorSqlPrefilter(string selectorsText)
        {
            var selectors = SplitSelectors(selectorsText);
            return selectors.Length > 0 && selectors.All(CanUseSingleSelectorSqlPrefilter);
        }

        private static bool CanUseSingleSelectorSqlPrefilter(string selector)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                return false;
            }
            var text = selector.Trim();
            if (text.StartsWith("^", StringComparison.Ordinal))
            {
                text = text[1..];
            }
            if (text.EndsWith("$", StringComparison.Ordinal))
            {
                text = text[..^1];
            }

            // 简单锚定名字仍可转成 LIKE；真正的正则分组、分支、通配和字符类不能进入 SQL 粗筛。
            return text.IndexOfAny(new[] { '|', '(', ')', '[', ']', '*', '+', '?' }) < 0;
        }

        private static string[] SplitSelectors(string selectorsText)
        {
            if (string.IsNullOrWhiteSpace(selectorsText))
            {
                return Array.Empty<string>();
            }

            return selectorsText
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
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

        private static string GetCommonRoot(IEnumerable<string> paths)
        {
            var fullPaths = paths
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => Path.GetFullPath(x).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .ToArray();
            if (fullPaths.Length == 0)
            {
                return Directory.GetCurrentDirectory();
            }
            var firstParts = fullPaths[0].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var common = new List<string>();
            for (var i = 0; i < firstParts.Length; i++)
            {
                var part = firstParts[i];
                if (fullPaths.All(x =>
                {
                    var parts = x.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    return parts.Length > i && string.Equals(parts[i], part, StringComparison.OrdinalIgnoreCase);
                }))
                {
                    common.Add(part);
                }
                else
                {
                    break;
                }
            }
            return string.Join(Path.DirectorySeparatorChar, common);
        }

        private static string BuildSourceFilter(string sourcePath)
        {
            var withoutExtension = Path.ChangeExtension(Path.GetFullPath(sourcePath), null)
                .Replace('\\', '/');
            return Regex.Escape(withoutExtension).Replace("/", "[\\\\/]");
        }

        private static string[] BuildSourceFileArguments(string inputRoot, params string[] sourcePaths)
        {
            var root = Path.GetFullPath(inputRoot ?? string.Empty).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            );
            return sourcePaths
                .Where(x => !string.IsNullOrWhiteSpace(x) && IsUnderDirectory(x, root))
                .Select(x => Path.GetRelativePath(root, x).Replace('\\', '/'))
                .Where(x => !string.IsNullOrWhiteSpace(x) && x != ".")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string[] BuildPathIdArguments(PreviewSelection selection)
        {
            return new[]
                {
                    (long?)selection?.Model?["model"]?["pathId"],
                    (long?)selection?.Animation?["pathId"],
                }
                .Where(x => x.HasValue)
                .Select(x => x.Value.ToString(CultureInfo.InvariantCulture))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string ResolveSourcePath(string indexedSourcePath, string sourceRootOverride)
        {
            if (string.IsNullOrWhiteSpace(sourceRootOverride))
            {
                return indexedSourcePath;
            }
            if (string.IsNullOrWhiteSpace(indexedSourcePath))
            {
                return indexedSourcePath;
            }
            if (!Directory.Exists(sourceRootOverride))
            {
                Logger.Warning($"--preview_source_root does not exist: {sourceRootOverride}");
                return indexedSourcePath;
            }

            var normalizedSource = indexedSourcePath.Replace('\\', '/');
            var lowerSource = normalizedSource.ToLowerInvariant();
            var anchors = new[]
            {
                "/streamingassets/",
                "/assets/",
                "/graphics/",
            };
            foreach (var anchor in anchors)
            {
                var index = lowerSource.IndexOf(anchor, StringComparison.Ordinal);
                if (index < 0)
                {
                    continue;
                }
                var relative = normalizedSource[(index + 1)..].Replace('/', Path.DirectorySeparatorChar);
                var candidate = Path.Combine(sourceRootOverride, relative);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            var fileName = Path.GetFileName(indexedSourcePath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return indexedSourcePath;
            }
            var matches = Directory.EnumerateFiles(sourceRootOverride, fileName, SearchOption.AllDirectories)
                .Take(2)
                .ToArray();
            if (matches.Length == 1)
            {
                return matches[0];
            }
            Logger.Warning(matches.Length == 0
                ? $"Unable to resolve indexed source under --preview_source_root: {indexedSourcePath}"
                : $"Multiple files named {fileName} found under --preview_source_root; keeping indexed source path.");
            return indexedSourcePath;
        }

        private static string ResolveExistingSourceIndex(string libraryRoot)
        {
            if (string.IsNullOrWhiteSpace(libraryRoot))
            {
                return null;
            }

            var sourceIndex = Path.Combine(libraryRoot, "unity_source_index.db");
            return File.Exists(sourceIndex) ? sourceIndex : null;
        }

        private static IEnumerable<JObject> ReadJsonLines(string path)
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JObject obj;
                try
                {
                    obj = JObject.Parse(line);
                }
                catch (JsonException e)
                {
                    Logger.Warning($"Skipping invalid JSONL row in {path}: {e.Message}");
                    continue;
                }
                yield return obj;
            }
        }

        private static string ReadSourceIndexRoot(string sourceIndexPath)
        {
            if (string.IsNullOrWhiteSpace(sourceIndexPath) || !File.Exists(sourceIndexPath))
            {
                return null;
            }

            try
            {
                using var connection = new SqliteConnection($"Data Source={sourceIndexPath};Mode=ReadOnly");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM metadata WHERE key='sourceRoot' LIMIT 1;";
                return command.ExecuteScalar() as string;
            }
            catch (Exception e)
            {
                Logger.Warning($"Unable to read source index root from {sourceIndexPath}: {e.Message}");
                return null;
            }
        }

        private static bool IsUnderDirectory(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase);
        }

        private static string[] ExtractExportIssues(params string[] logs)
        {
            return logs
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .SelectMany(x => x.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                .Where(line =>
                    line.IndexOf("Unable to resolve", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("missing", StringComparison.OrdinalIgnoreCase) >= 0 && line.IndexOf("mesh", StringComparison.OrdinalIgnoreCase) >= 0
                )
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(128)
                .ToArray();
        }

        private static string SafeName(string value)
        {
            var name = string.IsNullOrWhiteSpace(value) ? "preview" : value;
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        private static int ArrayCount(JObject obj, string propertyName)
        {
            return obj?[propertyName] is JArray array ? array.Count : 0;
        }

        private static JArray EnsureJArray(JObject obj, string propertyName)
        {
            if (obj[propertyName] is not JArray array)
            {
                array = new JArray();
                obj[propertyName] = array;
            }
            return array;
        }

        private static void RemoveEmptyTopLevelArrays(JObject gltf)
        {
            foreach (var key in new[] { "images", "materials", "textures", "samplers" })
            {
                if (gltf?[key] is JArray array && array.Count == 0)
                {
                    gltf.Remove(key);
                }
            }
        }

        private static void AppendOffsetArray(JObject target, JObject source, string propertyName, Action<JObject> mutate)
        {
            var targetArray = EnsureJArray(target, propertyName);
            foreach (var item in source[propertyName]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var clone = (JObject)item.DeepClone();
                mutate?.Invoke(clone);
                targetArray.Add(clone);
            }
        }

        private static void AppendAnimationBuffers(
            JObject merged,
            JObject animation,
            string animationDir,
            string outputDir,
            string animationName,
            JArray copyNotes)
        {
            var buffers = EnsureJArray(merged, "buffers");
            var sourceBuffers = animation["buffers"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            for (var i = 0; i < sourceBuffers.Length; i++)
            {
                var clone = (JObject)sourceBuffers[i].DeepClone();
                var uri = (string)clone["uri"];
                if (!string.IsNullOrWhiteSpace(uri) && !IsDataUri(uri))
                {
                    var sourcePath = ResolveGltfUriPath(animationDir, uri);
                    // 合并预览常在很深的素材库目录里生成，动画名再重复进目录会让 Blender 等工具踩到 Windows 长路径边界。
                    // buffer 内容和来源已写入报告，这里只保留短而稳定的文件名，保证 glTF 可以被常见工具直接打开。
                    var targetUri = ToGltfUri(Path.Combine("AnimationBuffers", $"anim_{i}.bin"));
                    CopyGltfResourceFile(sourcePath, outputDir, targetUri, copyNotes, "animationBuffer");
                    clone["uri"] = targetUri;
                }
                buffers.Add(clone);
            }
        }

        private static void CopyGltfUriResources(
            JObject gltf,
            string sourceDir,
            string outputDir,
            string prefix,
            JArray copyNotes,
            bool compactNames = false)
        {
            CopyGltfUriResources(gltf["buffers"]?.OfType<JObject>(), sourceDir, outputDir, prefix, copyNotes, "buffer", compactNames);
            CopyGltfUriResources(gltf["images"]?.OfType<JObject>(), sourceDir, outputDir, prefix, copyNotes, "image", compactNames);
        }

        private static void CopyGltfUriResources(
            IEnumerable<JObject> items,
            string sourceDir,
            string outputDir,
            string prefix,
            JArray copyNotes,
            string kind,
            bool compactNames = false)
        {
            var index = 0;
            foreach (var item in items ?? Enumerable.Empty<JObject>())
            {
                var uri = (string)item["uri"];
                if (string.IsNullOrWhiteSpace(uri) || IsDataUri(uri))
                {
                    continue;
                }

                var sourcePath = ResolveGltfUriPath(sourceDir, uri);
                var targetUri = compactNames
                    ? BuildCompactCopiedModelUri(uri, kind, index++)
                    : BuildCopiedModelUri(uri, prefix);
                CopyGltfResourceFile(sourcePath, outputDir, targetUri, copyNotes, kind);
                item["uri"] = targetUri;
            }
        }

        private static string BuildCompactCopiedModelUri(string uri, string kind, int index)
        {
            // merged 预览经常放在很深的调试目录；长贴图名叠加长目录会让部分 Windows 工具误判资源缺失。
            // 预览文件使用短 URI，来源和原名仍保留在 merge report 的 copiedResources 里。
            var extension = Path.GetExtension(FromGltfUri(uri));
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = string.Equals(kind, "image", StringComparison.OrdinalIgnoreCase) ? ".png" : ".bin";
            }

            var folder = string.Equals(kind, "image", StringComparison.OrdinalIgnoreCase) ? "Textures" : "Buffers";
            var name = string.Equals(kind, "image", StringComparison.OrdinalIgnoreCase)
                ? $"image_{index:D3}{extension}"
                : $"buffer_{index:D3}{extension}";
            return ToGltfUri(Path.Combine(folder, name));
        }

        private static void CopyGltfResourceFile(string sourcePath, string outputDir, string targetUri, JArray copyNotes, string kind)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                throw new FileNotFoundException($"glTF {kind} dependency not found.", sourcePath);
            }

            var targetPath = Path.Combine(outputDir, FromGltfUri(targetUri));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? outputDir);
            File.Copy(sourcePath, targetPath, overwrite: true);
            copyNotes.Add(new JObject
            {
                ["kind"] = kind,
                ["source"] = sourcePath,
                ["uri"] = targetUri,
            });
        }

        private static string BuildCopiedModelUri(string uri, string prefix)
        {
            if (IsAbsoluteGltfUri(uri))
            {
                var fileName = SafeRelativeFileName(uri, "resource.bin");
                return ToGltfUri(string.IsNullOrWhiteSpace(prefix)
                    ? Path.Combine("ExternalModelResources", fileName)
                    : Path.Combine(prefix, fileName));
            }

            var relative = FromGltfUri(uri);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                return ToGltfUri(Path.Combine("ExternalModelResources", SafeRelativeFileName(uri, "resource.bin")));
            }

            return ToGltfUri(string.IsNullOrWhiteSpace(prefix) ? relative : Path.Combine(prefix, relative));
        }

        private static string ResolveGltfUriPath(string sourceDir, string uri)
        {
            if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            {
                if (parsed.IsFile)
                {
                    return parsed.LocalPath;
                }
                throw new NotSupportedException($"External glTF URI is not supported for local merge: {uri}");
            }

            return Path.GetFullPath(Path.Combine(sourceDir, FromGltfUri(uri)));
        }

        private static string FromGltfUri(string uri)
        {
            return Uri.UnescapeDataString(uri ?? string.Empty)
                .Replace('/', Path.DirectorySeparatorChar);
        }

        private static string ToGltfUri(string path)
        {
            return path.Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
        }

        private static bool IsDataUri(string uri)
        {
            return uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAbsoluteGltfUri(string uri)
        {
            return Uri.TryCreate(uri, UriKind.Absolute, out _)
                || Path.IsPathRooted(FromGltfUri(uri));
        }

        private static string SafeRelativeFileName(string uri, string fallback)
        {
            var fileName = Path.GetFileName(FromGltfUri(uri));
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = fallback;
            }
            return SafeName(fileName);
        }

        private static Dictionary<string, int> BuildGltfNodePathMap(JObject gltf)
        {
            var nodes = gltf["nodes"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var childToParent = new Dictionary<int, int>();
            for (var i = 0; i < nodes.Length; i++)
            {
                foreach (var child in nodes[i]["children"]?.Values<int>() ?? Enumerable.Empty<int>())
                {
                    if (!childToParent.ContainsKey(child))
                    {
                        childToParent[child] = i;
                    }
                }
            }

            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < nodes.Length; i++)
            {
                var path = BuildGltfNodePath(nodes, childToParent, i);
                AddNodePathMapEntry(map, path, i);
                AddNodePathMapEntry(map, (string)nodes[i]["name"], i);
            }
            return map;
        }

        private static string BuildGltfNodePath(JObject[] nodes, Dictionary<int, int> childToParent, int index)
        {
            var parts = new List<string>();
            var guard = 0;
            var current = index;
            while (current >= 0 && current < nodes.Length && guard++ < nodes.Length)
            {
                parts.Add((string)nodes[current]["name"] ?? $"node_{current}");
                if (!childToParent.TryGetValue(current, out current))
                {
                    break;
                }
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static void AddNodePathMapEntry(Dictionary<string, int> map, string path, int index)
        {
            if (!string.IsNullOrWhiteSpace(path) && !map.ContainsKey(path))
            {
                map[path] = index;
            }
        }

        private static bool TryMapGltfNode(Dictionary<string, int> modelNodeMap, string animationNodePath, out int nodeIndex)
        {
            nodeIndex = -1;
            if (string.IsNullOrWhiteSpace(animationNodePath))
            {
                return false;
            }
            if (modelNodeMap.TryGetValue(animationNodePath, out nodeIndex))
            {
                return true;
            }

            var suffixMatches = modelNodeMap
                .Where(x => x.Key.EndsWith("/" + animationNodePath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Key.Length)
                .Take(2)
                .ToArray();
            if (suffixMatches.Length == 1)
            {
                nodeIndex = suffixMatches[0].Value;
                return true;
            }

            var leaf = animationNodePath.Split('/').LastOrDefault();
            if (string.IsNullOrWhiteSpace(leaf))
            {
                return false;
            }
            var leafMatches = modelNodeMap
                .Where(x => string.Equals(x.Key.Split('/').LastOrDefault(), leaf, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Value)
                .Distinct()
                .Take(2)
                .ToArray();
            if (leafMatches.Length == 1)
            {
                nodeIndex = leafMatches[0];
                return true;
            }
            return false;
        }

        private static bool CandidateRequiresInternalHumanoidSolve(JObject animation)
        {
            var candidate = animation?["candidate"] as JObject;
            // SQLite 候选是模型-动画关系的权威处理口径；旧 sidecar 可能还没写
            // decoded.requiresInternalHumanoidSolve，不能因此误走普通 Transform 预览。
            return (bool?)candidate?["requiresInternalHumanoidSolve"] == true
                || (bool?)animation?["requiresInternalHumanoidSolve"] == true;
        }

        private sealed record PreviewValidationCandidate(JObject Model, JObject Animation, string ModelOutput, string AnimationOutput);

        private sealed record PreviewSelection(JObject Model, JObject Animation);

        private sealed record LodNodeInfo(string BaseName, int Lod);

        private sealed record HiddenLodMeshReport(int Node, string Name, int Mesh, string Group, int HiddenLod, string SelectedNode, int SelectedLod);

        private sealed record StandaloneAnimationMergeAssessment(string Status, JArray SourceModes, JArray Reasons)
        {
            public JObject ToJson()
            {
                return new JObject
                {
                    ["status"] = Status,
                    ["sourceModes"] = SourceModes,
                    ["reasons"] = Reasons,
                };
            }
        }
    }

    internal static class GltfPreviewValidator
    {
        public static object Validate(string gltfPath, string expectedModelName, string expectedAnimationName, JObject modelIndex, JObject animationIndex, string[] exportIssues)
        {
            var gltf = JObject.Parse(File.ReadAllText(gltfPath));
            var directory = Path.GetDirectoryName(Path.GetFullPath(gltfPath));
            var buffers = LoadBuffers(gltf, directory);
            var nodes = gltf["nodes"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var animations = gltf["animations"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var skins = gltf["skins"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var meshes = gltf["meshes"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var animation = animations.FirstOrDefault(x => string.Equals((string)x["name"], expectedAnimationName, StringComparison.OrdinalIgnoreCase))
                ?? animations.FirstOrDefault();
            var channels = animation?["channels"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var humanoid = ReadHumanoidExtras(animation);
            var invalidChannels = channels.Count(x =>
            {
                var nodeIndex = (int?)x["target"]?["node"];
                return nodeIndex == null || nodeIndex < 0 || nodeIndex >= nodes.Length;
            });
            var channelTargets = channels
                .Select(x => new
                {
                    node = (int?)x["target"]?["node"],
                    nodeName = NodeName(nodes, (int?)x["target"]?["node"]),
                    path = (string)x["target"]?["path"],
                })
                .ToArray();
            var coverage = AnalyzeAnimationCoverage(channelTargets.Select(x => (x.nodeName, x.path)));
            var morph = AnalyzeMorphTargets(meshes, channels, animationIndex);
            var skinJointCount = skins.Sum(x => x["joints"]?.Count() ?? 0);
            var visibleTransformChannelCount = CountChannelsAffectingVisibleMeshes(nodes, skins, channels);
            var motion = AnalyzeAnimationMotion(gltf, buffers, nodes, skins, animation, channels);
            var meshNode = nodes
                .Select((node, index) => new { node, index })
                .FirstOrDefault(x => x.node["mesh"] != null && x.node["skin"] != null)
                ?? nodes.Select((node, index) => new { node, index }).FirstOrDefault(x => x.node["mesh"] != null);

            var bbox = meshNode == null
                ? null
                : ComputeBounds(gltf, buffers, nodes, meshNode.index);
            exportIssues ??= Array.Empty<string>();
            var hasExperimentalHumanoidBake = humanoid?.baked == true
                && (humanoid.bakeMode ?? string.Empty).StartsWith("Approximate", StringComparison.OrdinalIgnoreCase);
            var hasKnownHumanoidLimbRisk = humanoid?.internalSolved == true
                && ((int?)humanoid.diagnostics?["knownRiskTargetCount"] ?? 0) > 0;
            var nonCharacterTransformExpected = string.Equals(
                (string)animationIndex?["animationCapability"],
                "NonCharacterTransformPreviewReady",
                StringComparison.OrdinalIgnoreCase);
            var nonCharacterTransformOk = (nonCharacterTransformExpected || skins.Length == 0)
                && channels.Length > 0
                && invalidChannels == 0
                && visibleTransformChannelCount > 0
                && motion.visibleMovingChannelCount > 0;
            var morphOk = morph.expected
                && morph.meshTargetCount > 0
                && morph.targetCount > 0
                && morph.weightChannelCount > 0
                && invalidChannels == 0
                && motion.movingChannelCount > 0;
            var structurallyOk = animations.Length > 0
                && channels.Length > 0
                && invalidChannels == 0
                && motion.movingChannelCount > 0
                && (morphOk
                    || nonCharacterTransformOk
                    || (skins.Length > 0
                        && coverage.coreBoneChannelCount > 0
                        && coverage.coreBoneNodeCount >= 3))
                && bbox?.skinnedSizeLooksReasonable != false;
            var status = !structurallyOk || exportIssues.Length > 0 || hasKnownHumanoidLimbRisk
                ? "warning"
                : hasExperimentalHumanoidBake
                    ? "experimental"
                    : "ok";

            return new
            {
                generatedAt = DateTime.UtcNow.ToString("O"),
                status,
                gltf = gltfPath,
                expectedModel = expectedModelName,
                expectedAnimation = expectedAnimationName,
                model = modelIndex?["model"],
                animation = animationIndex,
                counts = new
                {
                    nodes = nodes.Length,
                    meshes = meshes.Length,
                    skins = skins.Length,
                    skinJoints = skinJointCount,
                    animations = animations.Length,
                    channels = channels.Length,
                    invalidChannels,
                    morphTargets = morph.targetCount,
                    weightChannels = morph.weightChannelCount,
                },
                exportIssues = new
                {
                    count = exportIssues.Length,
                    items = exportIssues,
                },
                animationCoverage = coverage,
                morph,
                nonCharacterTransform = new
                {
                    expected = nonCharacterTransformExpected,
                    capability = (string)animationIndex?["animationCapability"],
                    acceptedByVisibleTransform = !nonCharacterTransformExpected && skins.Length == 0 && nonCharacterTransformOk,
                    channelCount = channels.Length,
                    visibleTransformChannelCount,
                    invalidChannels,
                },
                motion,
                humanoid,
                bounds = bbox,
                channelTargets = channelTargets.Take(128).ToArray(),
                notes = BuildNotes(animations.Length, channels.Length, skins.Length, invalidChannels, coverage, morph, nonCharacterTransformOk, motion, humanoid, bbox, exportIssues),
            };
        }

        private static HumanoidAnimationReport ReadHumanoidExtras(JObject animation)
        {
            var humanoid = animation?["extras"]?["unityHumanoid"] as JObject;
            if (humanoid == null)
            {
                return new HumanoidAnimationReport(false, false, false, false, null, null, null, 0, 0, 0, 0, 0, 0, Array.Empty<string>(), null, null);
            }

            var attributes = humanoid["attributes"]?.Values<string>()?.ToArray() ?? Array.Empty<string>();
            return new HumanoidAnimationReport(
                true,
                (bool?)humanoid["requiresBake"] ?? false,
                (bool?)humanoid["baked"] ?? false,
                (bool?)humanoid["internalSolved"] ?? false,
                (string)humanoid["bakeMode"],
                (string)humanoid["solveMode"],
                (string)humanoid["solverCacheVersion"],
                (int?)humanoid["bakedTrackCount"] ?? 0,
                (int?)humanoid["bakedKeyframeCount"] ?? 0,
                (int?)humanoid["solvedTrackCount"] ?? 0,
                (int?)humanoid["solvedKeyframeCount"] ?? 0,
                (int?)humanoid["muscleCurveCount"] ?? 0,
                (int?)humanoid["keyframeCount"] ?? 0,
                attributes.Take(128).ToArray(),
                humanoid["diagnostics"] as JObject,
                humanoid["rootMotion"] as JObject
            );
        }

        private static AnimationCoverage AnalyzeAnimationCoverage(IEnumerable<(string nodeName, string path)> channelTargets)
        {
            var targets = channelTargets
                .Where(x => !string.IsNullOrWhiteSpace(x.nodeName))
                .ToArray();
            var core = targets
                .Where(x => IsCoreAnimatedBone(x.nodeName))
                .Select(x => x.nodeName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var twist = targets
                .Where(x => IsTwistOrHelperBone(x.nodeName))
                .Select(x => x.nodeName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var helper = targets
                .Where(x => IsAccessoryPoint(x.nodeName))
                .Select(x => x.nodeName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new AnimationCoverage(
                core.Length,
                targets.Count(x => IsCoreAnimatedBone(x.nodeName)),
                twist.Length,
                helper.Length,
                core,
                twist,
                helper
            );
        }

        private static MorphReport AnalyzeMorphTargets(JObject[] meshes, JObject[] channels, JObject animationIndex)
        {
            var meshTargetCount = 0;
            var targetCount = 0;
            var targetNames = new List<string>();
            foreach (var mesh in meshes)
            {
                var meshHasTargets = false;
                foreach (var primitive in mesh["primitives"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    var targets = primitive["targets"] as JArray;
                    if (targets == null || targets.Count == 0)
                    {
                        continue;
                    }
                    meshHasTargets = true;
                    targetCount += targets.Count;
                }
                if (meshHasTargets)
                {
                    meshTargetCount++;
                    targetNames.AddRange(mesh["extras"]?["targetNames"]?.Values<string>() ?? Enumerable.Empty<string>());
                }
            }

            var weightChannelCount = channels.Count(x => string.Equals((string)x["target"]?["path"], "weights", StringComparison.OrdinalIgnoreCase));
            var expectedBindingCount = (int?)animationIndex?["trueBlendShapeBindingCount"] ?? 0;
            var ambiguousBindingCount = (int?)animationIndex?["blendShapeBindingCount"] ?? 0;
            return new MorphReport(
                expectedBindingCount > 0 || weightChannelCount > 0,
                expectedBindingCount,
                ambiguousBindingCount,
                meshTargetCount,
                targetCount,
                weightChannelCount,
                targetNames.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(128).ToArray()
            );
        }

        private static bool IsCoreAnimatedBone(string nodeName)
        {
            var name = NormalizeBoneName(nodeName);
            if (IsTwistOrHelperBone(nodeName) || IsAccessoryPoint(nodeName))
            {
                return false;
            }
            return name.Contains("pelvis")
                || name.Contains("spine")
                || name.Contains("neck")
                || name.Contains("head")
                || name.Contains("clavicle")
                || name.Contains("upperarm")
                || name.Contains("forearm")
                || name.Contains("hand")
                || name.Contains("thigh")
                || name.Contains("calf")
                || name.Contains("foot")
                || name.Contains("toe");
        }

        private static bool IsTwistOrHelperBone(string nodeName)
        {
            var name = NormalizeBoneName(nodeName);
            return name.Contains("twist") || name.Contains("helper");
        }

        private static bool IsAccessoryPoint(string nodeName)
        {
            var name = NormalizeBoneName(nodeName);
            return name.Contains("point") || name.Contains("socket") || name.Contains("attach");
        }

        private static string NormalizeBoneName(string nodeName)
        {
            return Regex.Replace((nodeName ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9]+", string.Empty);
        }

        private static List<string> BuildNotes(int animationCount, int channelCount, int skinCount, int invalidChannels, AnimationCoverage coverage, MorphReport morph, bool nonCharacterTransformOk, AnimationMotionReport motion, HumanoidAnimationReport humanoid, BoundsReport bounds, string[] exportIssues)
        {
            var notes = new List<string>();
            if (animationCount == 0) notes.Add("No animation was embedded in the preview glTF.");
            if (channelCount == 0) notes.Add("The embedded animation has no valid glTF channels.");
            if (skinCount == 0 && !nonCharacterTransformOk) notes.Add("No skin was exported for the preview model.");
            if (invalidChannels > 0) notes.Add($"{invalidChannels} animation channel(s) target missing nodes.");
            if (channelCount > 0 && motion.movingChannelCount == 0)
            {
                notes.Add("Animation channels were written, but all sampled outputs are static or zero-duration; treat this as a static pose, not a playable animation.");
            }
            else if (nonCharacterTransformOk && motion.visibleMovingChannelCount == 0)
            {
                notes.Add("Non-character animation channels exist, but none of the moving channels affect visible meshes.");
            }
            if (exportIssues?.Length > 0)
            {
                notes.Add($"{exportIssues.Length} export issue(s) were reported while generating the preview; inspect exportIssues before trusting the asset.");
            }
            if (morph?.expected == true)
            {
                if (morph.meshTargetCount > 0 && morph.weightChannelCount > 0)
                {
                    notes.Add($"BlendShape/morph animation is present: {morph.targetCount} morph target(s), {morph.weightChannelCount} glTF weights channel(s).");
                }
                else
                {
                    notes.Add($"BlendShape bindings were expected ({morph.expectedBindingCount}), but glTF morph targets or weights channels are missing.");
                }
            }
            else if (morph?.ambiguousBindingCount > 0)
            {
                notes.Add($"Animation has {morph.ambiguousBindingCount} historical SkinnedMeshRenderer float binding(s), but no confirmed BlendShape binding; treat renderer/material/visibility curves separately from glTF morph weights.");
            }
            if (nonCharacterTransformOk)
            {
                notes.Add("Non-character Transform animation channels target exported glTF nodes.");
            }
            if (humanoid?.requiresBake == true)
            {
                notes.Add($"Unity Humanoid/Muscle curves are present ({humanoid.muscleCurveCount} curves, {humanoid.keyframeCount} keyframes); AnimeStudio needs an internal Humanoid/Muscle to skeleton TRS solver before this body animation can play directly in glTF.");
            }
            else if (humanoid?.internalSolved == true)
            {
                notes.Add($"Unity Humanoid/Muscle curves were solved internally with {humanoid.solveMode ?? "unknown"} into {humanoid.solvedTrackCount} skeleton track(s), {humanoid.solvedKeyframeCount} keyframes.");
                var diagnosticsStatus = (string)humanoid.diagnostics?["status"];
                if (!string.IsNullOrWhiteSpace(diagnosticsStatus))
                {
                    notes.Add($"Humanoid internal solver diagnostics: {diagnosticsStatus}, solved {(int?)humanoid.diagnostics?["solvedTrackCount"] ?? 0}/{(int?)humanoid.diagnostics?["targetCount"] ?? 0} target bone(s), sample times {(int?)humanoid.diagnostics?["sampleTimeCount"] ?? 0}.");
                }
                var knownRiskTargetCount = (int?)humanoid.diagnostics?["knownRiskTargetCount"] ?? 0;
                if (knownRiskTargetCount > 0)
                {
                    var knownRiskTargets = humanoid.diagnostics?["knownRiskTargets"]?
                        .OfType<JObject>()
                        .Select(x => (string)x["humanBone"])
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .Take(16)
                        .ToArray() ?? Array.Empty<string>();
                    notes.Add($"Humanoid internal solver wrote {knownRiskTargetCount} known-risk limb target(s): {string.Join(", ", knownRiskTargets)}. Current Forearm/LowerLeg muscle delta formula is still experimental; use Unity oracle comparison before treating this preview as visually correct.");
                }
            }
            if (string.Equals((string)humanoid?.rootMotion?["status"], "ok", StringComparison.OrdinalIgnoreCase))
            {
                notes.Add($"Humanoid RootT/RootQ root motion was written to scene root `{(string)humanoid.rootMotion["targetPath"]}` as {(int?)humanoid.rootMotion["trackCount"] ?? 0} glTF track(s).");
            }
            else if (humanoid?.present == true && humanoid.internalSolved != true && !string.IsNullOrWhiteSpace(humanoid.solveMode))
            {
                var diagnosticsStatus = (string)humanoid.diagnostics?["status"];
                if (humanoid.muscleCurveCount == 0
                    || string.Equals(diagnosticsStatus, "no_humanoid_muscle_curves", StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add($"This explicit Humanoid-related animation has no decoded body muscle curves for AnimeStudio's internal solver; diagnostics={diagnosticsStatus ?? "unknown"}. Keep the Unity relation, but treat the current glTF channels as root/helper/accessory Transform motion rather than a solved body animation.");
                }
                else
                {
                    notes.Add($"Humanoid internal solver did not produce muscle TRS tracks with {humanoid.solveMode}; diagnostics={diagnosticsStatus ?? "unknown"}. Decoded Transform TRS channels may still be valid, but Humanoid/Muscle body motion is not solved.");
                }
            }
            else if (humanoid?.baked == true)
            {
                notes.Add($"Unity Humanoid/Muscle curves were baked with {humanoid.bakeMode ?? "unknown"} into {humanoid.bakedTrackCount} skeleton track(s), {humanoid.bakedKeyframeCount} keyframes.");
                if ((humanoid.bakeMode ?? string.Empty).StartsWith("Approximate", StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add("This Humanoid/Muscle bake is approximate and experimental; it is useful for debugging channel generation, but it is not accepted as a correct reusable animation asset until visual/solver validation passes.");
                }
                var diagnosticsStatus = (string)humanoid.diagnostics?["status"];
                if (!string.IsNullOrWhiteSpace(diagnosticsStatus))
                {
                    notes.Add($"Humanoid bake diagnostics: {diagnosticsStatus}, mapped {(int?)humanoid.diagnostics?["mappedTargetCount"] ?? 0}/{(int?)humanoid.diagnostics?["targetCount"] ?? 0} target bone(s), sample times {(int?)humanoid.diagnostics?["sampleTimeCount"] ?? 0}.");
                }
            }
            if (coverage.coreBoneChannelCount == 0 && morph?.weightChannelCount == 0 && !nonCharacterTransformOk)
            {
                notes.Add("No core body bone channels were written; the preview animation currently affects only helper/accessory/twist nodes.");
            }
            else if (coverage.coreBoneNodeCount < 3 && !nonCharacterTransformOk)
            {
                notes.Add("Very few core body bones were animated; inspect whether this is an auxiliary clip rather than a body animation.");
            }
            if (bounds?.skinnedSizeLooksReasonable == false) notes.Add("Skinned rest bounds differ greatly from raw mesh bounds; inspect bind poses or joints.");
            return notes;
        }

        private static string NodeName(JObject[] nodes, int? index)
        {
            return index.HasValue && index.Value >= 0 && index.Value < nodes.Length
                ? (string)nodes[index.Value]["name"]
                : null;
        }

        private static int CountChannelsAffectingVisibleMeshes(JObject[] nodes, JObject[] skins, JObject[] channels)
        {
            var skinJoints = skins
                .SelectMany(skin => skin["joints"]?.Values<int>() ?? Enumerable.Empty<int>())
                .ToHashSet();
            var count = 0;
            foreach (var channel in channels)
            {
                var nodeIndex = (int?)channel["target"]?["node"];
                if (nodeIndex == null || nodeIndex < 0 || nodeIndex >= nodes.Length)
                {
                    continue;
                }
                if (skinJoints.Contains(nodeIndex.Value) || NodeOrDescendantHasMesh(nodes, nodeIndex.Value))
                {
                    count++;
                }
            }
            return count;
        }

        private static bool NodeOrDescendantHasMesh(JObject[] nodes, int nodeIndex)
        {
            if (nodeIndex < 0 || nodeIndex >= nodes.Length)
            {
                return false;
            }
            if (nodes[nodeIndex]["mesh"] != null)
            {
                return true;
            }
            foreach (var childIndex in nodes[nodeIndex]["children"]?.Values<int>() ?? Enumerable.Empty<int>())
            {
                if (NodeOrDescendantHasMesh(nodes, childIndex))
                {
                    return true;
                }
            }
            return false;
        }

        private static AnimationMotionReport AnalyzeAnimationMotion(JObject gltf, byte[][] buffers, JObject[] nodes, JObject[] skins, JObject animation, JObject[] channels)
        {
            var samplers = animation?["samplers"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            if (samplers.Length == 0 || channels.Length == 0)
            {
                return new AnimationMotionReport(0, 0, 0, 0, 0, Array.Empty<string>());
            }

            var movingSamplers = new HashSet<int>();
            var maxDuration = 0.0f;
            var maxOutputDelta = 0.0f;
            for (var i = 0; i < samplers.Length; i++)
            {
                var inputAccessor = (int?)samplers[i]["input"];
                var outputAccessor = (int?)samplers[i]["output"];
                if (inputAccessor == null || outputAccessor == null)
                {
                    continue;
                }

                var input = ReadAccessor(gltf, buffers, inputAccessor.Value);
                var output = ReadAccessor(gltf, buffers, outputAccessor.Value);
                var duration = input.Length == 0 ? 0 : input.Max(x => x[0]) - input.Min(x => x[0]);
                var delta = MaxOutputDelta(output);
                maxDuration = Math.Max(maxDuration, duration);
                maxOutputDelta = Math.Max(maxOutputDelta, delta);
                if (duration > 0.0001f && delta > 0.0001f)
                {
                    movingSamplers.Add(i);
                }
            }

            var movingChannelCount = 0;
            var visibleMovingChannelCount = 0;
            var visibleTargets = new List<string>();
            foreach (var channel in channels)
            {
                var samplerIndex = (int?)channel["sampler"];
                if (samplerIndex == null || !movingSamplers.Contains(samplerIndex.Value))
                {
                    continue;
                }

                movingChannelCount++;
                var nodeIndex = (int?)channel["target"]?["node"];
                var nodeName = NodeName(nodes, nodeIndex);
                if (NodeAffectsVisibleMesh(nodes, skins, nodeIndex))
                {
                    visibleMovingChannelCount++;
                    if (!string.IsNullOrWhiteSpace(nodeName))
                    {
                        visibleTargets.Add(nodeName);
                    }
                }
            }

            return new AnimationMotionReport(
                maxDuration,
                maxOutputDelta,
                movingSamplers.Count,
                movingChannelCount,
                visibleMovingChannelCount,
                visibleTargets.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(128).ToArray()
            );
        }

        private static bool NodeAffectsVisibleMesh(JObject[] nodes, JObject[] skins, int? nodeIndex)
        {
            if (nodeIndex == null || nodeIndex < 0 || nodeIndex >= nodes.Length)
            {
                return false;
            }

            var skinJoints = skins
                .SelectMany(skin => skin["joints"]?.Values<int>() ?? Enumerable.Empty<int>())
                .ToHashSet();
            return skinJoints.Contains(nodeIndex.Value) || NodeOrDescendantHasMesh(nodes, nodeIndex.Value);
        }

        private static float MaxOutputDelta(float[][] rows)
        {
            if (rows.Length < 2)
            {
                return 0;
            }

            var first = rows[0];
            var max = 0.0f;
            foreach (var row in rows.Skip(1))
            {
                for (var i = 0; i < Math.Min(first.Length, row.Length); i++)
                {
                    max = Math.Max(max, Math.Abs(row[i] - first[i]));
                }
            }
            return max;
        }

        private static byte[][] LoadBuffers(JObject gltf, string directory)
        {
            return gltf["buffers"]?
                .OfType<JObject>()
                .Select(buffer =>
                {
                    var uri = (string)buffer["uri"];
                    if (string.IsNullOrWhiteSpace(uri) || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        return Array.Empty<byte>();
                    }
                    return File.ReadAllBytes(Path.Combine(directory, uri.Replace('/', Path.DirectorySeparatorChar)));
                })
                .ToArray() ?? Array.Empty<byte[]>();
        }

        private static BoundsReport ComputeBounds(JObject gltf, byte[][] buffers, JObject[] nodes, int meshNodeIndex)
        {
            var meshNode = nodes[meshNodeIndex];
            var meshIndex = (int?)meshNode["mesh"];
            if (meshIndex == null)
            {
                return null;
            }
            var mesh = gltf["meshes"]?[meshIndex.Value] as JObject;
            var primitive = mesh?["primitives"]?.OfType<JObject>().FirstOrDefault(x => x["attributes"]?["POSITION"] != null);
            if (primitive == null)
            {
                return null;
            }
            var positions = ReadAccessor(gltf, buffers, (int)primitive["attributes"]["POSITION"]).Select(x => new Vec3(x[0], x[1], x[2])).ToArray();
            var raw = Bounds.FromPoints(positions);
            Bounds skinned = null;
            var skinIndex = (int?)meshNode["skin"];
            if (skinIndex != null && primitive["attributes"]?["JOINTS_0"] != null && primitive["attributes"]?["WEIGHTS_0"] != null)
            {
                var joints = ReadAccessor(gltf, buffers, (int)primitive["attributes"]["JOINTS_0"]);
                var weights = ReadAccessor(gltf, buffers, (int)primitive["attributes"]["WEIGHTS_0"]);
                var skin = gltf["skins"]?[skinIndex.Value] as JObject;
                var jointNodes = skin?["joints"]?.Select(x => (int)x).ToArray() ?? Array.Empty<int>();
                var inverseBindMatrices = skin?["inverseBindMatrices"] != null
                    ? ReadAccessor(gltf, buffers, (int)skin["inverseBindMatrices"]).Select(Matrix.FromArray).ToArray()
                    : Enumerable.Range(0, jointNodes.Length).Select(_ => Matrix.Identity()).ToArray();
                var worlds = BuildWorldMatrices(nodes);
                var meshWorld = worlds[meshNodeIndex];
                var skinMatrices = jointNodes
                    .Select((node, i) => worlds[node] * inverseBindMatrices[i])
                    .ToArray();
                var skinnedPoints = new Vec3[positions.Length];
                for (var i = 0; i < positions.Length; i++)
                {
                    var p = Vec3.Zero;
                    for (var k = 0; k < Math.Min(4, weights[i].Length); k++)
                    {
                        var weight = weights[i][k];
                        if (weight == 0) continue;
                        var joint = (int)joints[i][k];
                        if (joint < 0 || joint >= skinMatrices.Length) continue;
                        p += skinMatrices[joint].Transform(positions[i]) * weight;
                    }
                    skinnedPoints[i] = meshWorld.Transform(p);
                }
                skinned = Bounds.FromPoints(skinnedPoints);
            }
            var reasonable = skinned == null || raw.maxSize <= 0 || skinned.maxSize / raw.maxSize < 3.0f;
            return new BoundsReport(raw, skinned, reasonable);
        }

        private static Matrix[] BuildWorldMatrices(JObject[] nodes)
        {
            var parents = new int?[nodes.Length];
            for (var i = 0; i < nodes.Length; i++)
            {
                foreach (var child in nodes[i]["children"]?.Select(x => (int)x) ?? Enumerable.Empty<int>())
                {
                    if (child >= 0 && child < parents.Length)
                    {
                        parents[child] = i;
                    }
                }
            }
            var worlds = new Matrix[nodes.Length];
            var done = new bool[nodes.Length];
            Matrix Build(int index)
            {
                if (done[index]) return worlds[index];
                var local = Matrix.FromNode(nodes[index]);
                worlds[index] = parents[index].HasValue ? Build(parents[index].Value) * local : local;
                done[index] = true;
                return worlds[index];
            }
            for (var i = 0; i < nodes.Length; i++) Build(i);
            return worlds;
        }

        private static float[][] ReadAccessor(JObject gltf, byte[][] buffers, int accessorIndex)
        {
            var accessor = (JObject)gltf["accessors"][accessorIndex];
            var view = (JObject)gltf["bufferViews"][(int)accessor["bufferView"]];
            var buffer = buffers[(int)view["buffer"]];
            var offset = ((int?)view["byteOffset"] ?? 0) + ((int?)accessor["byteOffset"] ?? 0);
            var count = (int)accessor["count"];
            var components = ((string)accessor["type"]) switch
            {
                "SCALAR" => 1,
                "VEC2" => 2,
                "VEC3" => 3,
                "VEC4" => 4,
                "MAT4" => 16,
                _ => 1,
            };
            var componentType = (int)accessor["componentType"];
            var bytes = componentType switch
            {
                5126 => 4,
                5125 => 4,
                5123 => 2,
                5121 => 1,
                _ => 4,
            };
            var stride = (int?)view["byteStride"] ?? components * bytes;
            var rows = new float[count][];
            for (var i = 0; i < count; i++)
            {
                rows[i] = new float[components];
                for (var c = 0; c < components; c++)
                {
                    var at = offset + i * stride + c * bytes;
                    rows[i][c] = componentType switch
                    {
                        5126 => BitConverter.ToSingle(buffer, at),
                        5125 => BitConverter.ToUInt32(buffer, at),
                        5123 => BitConverter.ToUInt16(buffer, at),
                        5121 => buffer[at],
                        _ => 0,
                    };
                }
            }
            return rows;
        }

        private sealed record BoundsReport(Bounds raw, Bounds skinnedFinal, bool skinnedSizeLooksReasonable);

        private sealed record AnimationCoverage(
            int coreBoneNodeCount,
            int coreBoneChannelCount,
            int twistOrHelperNodeCount,
            int accessoryPointNodeCount,
            string[] coreBoneNodes,
            string[] twistOrHelperNodes,
            string[] accessoryPointNodes
        );

        private sealed record MorphReport(
            bool expected,
            int expectedBindingCount,
            int ambiguousBindingCount,
            int meshTargetCount,
            int targetCount,
            int weightChannelCount,
            string[] targetNames
        );

        private sealed record HumanoidAnimationReport(
            bool present,
            bool requiresBake,
            bool baked,
            bool internalSolved,
            string bakeMode,
            string solveMode,
            string solverCacheVersion,
            int bakedTrackCount,
            int bakedKeyframeCount,
            int solvedTrackCount,
            int solvedKeyframeCount,
            int muscleCurveCount,
            int keyframeCount,
            string[] attributes,
            JObject diagnostics,
            JObject rootMotion
        );

        private sealed record AnimationMotionReport(
            float maxDuration,
            float maxOutputDelta,
            int movingSamplerCount,
            int movingChannelCount,
            int visibleMovingChannelCount,
            string[] visibleMovingTargets
        );

        private sealed record Bounds(float[] min, float[] max, float[] size)
        {
            public float maxSize => size.Length == 0 ? 0 : size.Max();

            public static Bounds FromPoints(IEnumerable<Vec3> points)
            {
                var min = new[] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity };
                var max = new[] { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };
                foreach (var p in points)
                {
                    min[0] = Math.Min(min[0], p.X); min[1] = Math.Min(min[1], p.Y); min[2] = Math.Min(min[2], p.Z);
                    max[0] = Math.Max(max[0], p.X); max[1] = Math.Max(max[1], p.Y); max[2] = Math.Max(max[2], p.Z);
                }
                return new Bounds(Round(min), Round(max), Round(new[] { max[0] - min[0], max[1] - min[1], max[2] - min[2] }));
            }

            private static float[] Round(float[] values)
            {
                return values.Select(x => float.IsInfinity(x) ? x : float.Parse(x.ToString("0.###", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)).ToArray();
            }
        }

        private readonly struct Vec3
        {
            public static readonly Vec3 Zero = new Vec3(0, 0, 0);
            public readonly float X;
            public readonly float Y;
            public readonly float Z;
            public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }
            public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
            public static Vec3 operator *(Vec3 a, float s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        }

        private readonly struct Matrix
        {
            private readonly float[] m;
            private Matrix(float[] values) { m = values; }
            public static Matrix Identity() => new Matrix(new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 });
            public static Matrix FromArray(float[] values) => new Matrix(values.Take(16).ToArray());
            public static Matrix FromNode(JObject node)
            {
                if (node["matrix"] is JArray matrix)
                {
                    return FromArray(matrix.Select(x => (float)x).ToArray());
                }
                var t = node["translation"]?.Select(x => (float)x).ToArray() ?? new[] { 0f, 0f, 0f };
                var s = node["scale"]?.Select(x => (float)x).ToArray() ?? new[] { 1f, 1f, 1f };
                var q = node["rotation"]?.Select(x => (float)x).ToArray() ?? new[] { 0f, 0f, 0f, 1f };
                var x = q[0]; var y = q[1]; var z = q[2]; var w = q[3];
                var x2 = x + x; var y2 = y + y; var z2 = z + z;
                var xx = x * x2; var xy = x * y2; var xz = x * z2;
                var yy = y * y2; var yz = y * z2; var zz = z * z2;
                var wx = w * x2; var wy = w * y2; var wz = w * z2;
                return new Matrix(new[]
                {
                    (1 - (yy + zz)) * s[0], (xy + wz) * s[0], (xz - wy) * s[0], 0,
                    (xy - wz) * s[1], (1 - (xx + zz)) * s[1], (yz + wx) * s[1], 0,
                    (xz + wy) * s[2], (yz - wx) * s[2], (1 - (xx + yy)) * s[2], 0,
                    t[0], t[1], t[2], 1,
                });
            }
            public Vec3 Transform(Vec3 v) => new Vec3(
                m[0] * v.X + m[4] * v.Y + m[8] * v.Z + m[12],
                m[1] * v.X + m[5] * v.Y + m[9] * v.Z + m[13],
                m[2] * v.X + m[6] * v.Y + m[10] * v.Z + m[14]
            );
            public static Matrix operator *(Matrix a, Matrix b)
            {
                var o = new float[16];
                for (var c = 0; c < 4; c++)
                for (var r = 0; r < 4; r++)
                for (var k = 0; k < 4; k++)
                {
                    o[c * 4 + r] += a.m[k * 4 + r] * b.m[c * 4 + k];
                }
                return new Matrix(o);
            }
        }
    }
}
