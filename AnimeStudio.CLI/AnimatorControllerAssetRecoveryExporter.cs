using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AnimeStudio.CLI
{
    internal static class AnimatorControllerAssetRecoveryExporter
    {
        private const string ImportedAnimatorControllerFolder = "Assets/AnimeStudioBake/ImportedAnimatorController";

        public static string Recover(
            string libraryRoot,
            string unityProject,
            string unityEditor,
            string unityFileInspect,
            string modelSelector,
            string animationSelector,
            int limit,
            bool force,
            bool runUnity,
            string explicitIndexPath = null,
            string sourceIndexPath = null,
            IEnumerable<string> extraClipLibraryRoots = null)
        {
            if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
            {
                throw new DirectoryNotFoundException($"Library root not found: {libraryRoot}");
            }
            var hasUnityProject = !string.IsNullOrWhiteSpace(unityProject) && Directory.Exists(unityProject);
            if (runUnity && !hasUnityProject)
            {
                throw new DirectoryNotFoundException("--unity_project is required and must point to a Unity bake project when --run_unity_bake is used.");
            }
            if (string.IsNullOrWhiteSpace(unityFileInspect) || !File.Exists(unityFileInspect))
            {
                throw new FileNotFoundException("--unity_file_inspect is required for AnimatorController recovery.", unityFileInspect);
            }

            var libraryPath = Path.GetFullPath(libraryRoot);
            var dbPath = string.IsNullOrWhiteSpace(explicitIndexPath)
                ? Path.Combine(libraryPath, "library_index.db")
                : Path.GetFullPath(explicitIndexPath);
            if (!File.Exists(dbPath))
            {
                throw new FileNotFoundException($"library_index.db not found: {dbPath}", dbPath);
            }

            var started = DateTimeOffset.UtcNow;
            var reportDir = Path.Combine(libraryPath, ".as_browser_cache", "diagnostics", $"imported_animator_controller_recovery_{started:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(reportDir);
            var reportPath = Path.Combine(reportDir, "imported_animator_controller_recovery.json");

            var controllers = LoadControllerInspect(unityFileInspect);
            var animationAssets = LoadAnimationAssets(dbPath, libraryPath);
            var extraClipLibraries = ResolveExtraClipLibraries(extraClipLibraryRoots).ToList();
            foreach (var extraLibrary in extraClipLibraries)
            {
                MergeAnimationAssets(animationAssets, LoadAnimationAssetsFromLibrary(extraLibrary));
            }
            sourceIndexPath = ResolveRecoverySourceIndex(libraryPath, sourceIndexPath);
            MergeSourceAnimationNames(animationAssets, sourceIndexPath);
            var importedClips = DiscoverImportedAnimationClips(hasUnityProject ? unityProject : null, libraryPath);
            foreach (var extraLibrary in extraClipLibraries)
            {
                AddStringMap(importedClips, LoadUnityBakeStringMap(extraLibrary, "unityAnimationClips"));
            }
            var requests = ReadRecoveryRequests(dbPath, modelSelector, animationSelector, controllers, limit);
            var results = new JArray();
            var recoveredMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var request in requests)
            {
                if (!controllers.TryGetValue(request.ControllerPathId, out var controllerInspect)
                    && (string.IsNullOrWhiteSpace(request.ControllerName) || !controllers.TryGetValue(request.ControllerName, out controllerInspect)))
                {
                    results.Add(WriteResult(request, "missing_controller_inspect", null, "Controller was not found in unity_file_inspect.json."));
                    continue;
                }

                var requestJson = BuildUnityRecoveryRequest(
                    unityProject,
                    reportDir,
                    controllerInspect,
                    animationAssets,
                    importedClips,
                    force);
                var requestPath = Path.Combine(reportDir, Exporter.FixFileName((string)controllerInspect["name"] ?? request.ControllerName) + ".animator_controller_recovery_request.json");
                File.WriteAllText(requestPath, requestJson.ToString(Formatting.Indented));
                var requestDiagnostics = BuildRequestDiagnostics(requestJson, requestPath);

                if (!runUnity)
                {
                    results.Add(WriteResult(request, "request_written", (string)requestJson["controllerAssetPath"], $"Unity recovery request written: {requestPath}", null, requestDiagnostics));
                    continue;
                }

                var unityResult = RunUnityRecovery(unityProject, unityEditor, requestPath, Path.Combine(reportDir, "animator_controller_recovery.log"));
                var status = (string)unityResult?["status"] ?? "error";
                var assetPath = (string)unityResult?["controllerAssetPath"] ?? (string)requestJson["controllerAssetPath"];
                if (status == "ok" || status == "warning" || status == "already_exists")
                {
                    RememberRecoveredController(recoveredMap, request, controllerInspect, assetPath);
                }
                results.Add(WriteResult(request, status, assetPath, (string)unityResult?["message"] ?? "Unity recovery finished.", unityResult, requestDiagnostics));
            }

            if (hasUnityProject)
            {
                UpdateLocalBrowserSettings(libraryPath, unityProject, recoveredMap);
            }
            var available = results.Count(x =>
                string.Equals((string)x["status"], "ok", StringComparison.OrdinalIgnoreCase)
                || string.Equals((string)x["status"], "warning", StringComparison.OrdinalIgnoreCase)
                || string.Equals((string)x["status"], "already_exists", StringComparison.OrdinalIgnoreCase));
            var report = new JObject
            {
                ["status"] = requests.Count == 0 ? "nothing_to_recover" : available == requests.Count ? "ok" : available > 0 ? "partial" : runUnity ? "failed" : "request_written",
                ["libraryRoot"] = libraryPath,
                ["unityProject"] = hasUnityProject ? Path.GetFullPath(unityProject) : string.Empty,
                ["requestOnly"] = !runUnity,
                ["unityEditor"] = unityEditor ?? string.Empty,
                ["unityFileInspect"] = Path.GetFullPath(unityFileInspect),
                ["sourceIndex"] = sourceIndexPath ?? string.Empty,
                ["extraClipLibraries"] = new JArray(extraClipLibraries),
                ["createdUtc"] = started.ToString("O", CultureInfo.InvariantCulture),
                ["selectedAnimatorControllers"] = requests.Count,
                ["availableAnimatorControllers"] = available,
                ["rule"] = "从显式 model_animation_candidates 与 unity_file_inspect.json 精确重建默认层/默认状态 AnimatorController；第一版不声称 transition、condition、复杂 BlendTree 或 runtime 参数已完整恢复。",
                ["results"] = results,
            };
            File.WriteAllText(reportPath, report.ToString(Formatting.Indented));
            Logger.Info($"Imported AnimatorController recovery report written: {reportPath}");
            return reportPath;
        }

        private static string ResolveRecoverySourceIndex(string libraryPath, string explicitSourceIndexPath)
        {
            if (!string.IsNullOrWhiteSpace(explicitSourceIndexPath))
            {
                return Path.GetFullPath(explicitSourceIndexPath);
            }

            foreach (var candidate in EnumerateRecoverySourceIndexCandidates(libraryPath))
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            return null;
        }

        private static IEnumerable<string> EnumerateRecoverySourceIndexCandidates(string libraryPath)
        {
            if (string.IsNullOrWhiteSpace(libraryPath))
            {
                yield break;
            }

            yield return Path.Combine(libraryPath, "unity_source_index.db");
            foreach (var reportName in new[]
            {
                "source_index_usage.json",
                "animator_controller_context_refresh.json",
                "sqlite_index_summary.json",
            })
            {
                var reportPath = Path.Combine(libraryPath, reportName);
                var sourceIndex = TryReadSourceIndexFromReport(reportPath);
                if (!string.IsNullOrWhiteSpace(sourceIndex))
                {
                    yield return sourceIndex;
                }
            }
        }

        private static string TryReadSourceIndexFromReport(string reportPath)
        {
            if (string.IsNullOrWhiteSpace(reportPath) || !File.Exists(reportPath))
            {
                return null;
            }

            try
            {
                var root = JObject.Parse(File.ReadAllText(reportPath));
                return (string)root["sourceIndex"]
                    ?? (string)root["sourceIndexAnimationRelationHealth"]?["sourceIndex"];
            }
            catch
            {
                return null;
            }
        }

        private static List<AnimatorControllerRecoveryRequestInfo> ReadRecoveryRequests(
            string dbPath,
            string modelSelector,
            string animationSelector,
            IReadOnlyDictionary<object, JObject> controllers,
            int limit)
        {
            var result = new Dictionary<long, AnimatorControllerRecoveryRequestInfo>();
            SQLitePCL.Batteries_V2.Init();
            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT m.name, m.output, a.name, a.output, c.raw_json
FROM model_animation_candidates c
JOIN assets m ON m.kind='Model' AND m.output=c.model_output
JOIN assets a ON a.kind='Animation' AND a.output=c.animation_output
WHERE c.relation_source='explicit';";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var modelName = ReadDbString(reader, 0);
                var modelOutput = ReadDbString(reader, 1);
                var animationName = ReadDbString(reader, 2);
                var animationOutput = ReadDbString(reader, 3);
                if (!MatchesSelector(modelSelector, modelName, modelOutput)
                    || !MatchesSelector(animationSelector, animationName, animationOutput))
                {
                    continue;
                }

                var raw = ReadDbString(reader, 4);
                JObject json;
                try
                {
                    json = JObject.Parse(raw ?? "{}");
                }
                catch
                {
                    continue;
                }

                var pathId = (long?)json["relationEvidence"]?["controllerPathId"]
                    ?? (long?)json["animatorControllerContext"]?["controllerPathId"];
                if (pathId == null)
                {
                    continue;
                }

                if (!result.TryGetValue(pathId.Value, out var item))
                {
                    item = new AnimatorControllerRecoveryRequestInfo(
                        pathId.Value,
                        (string)json["relationEvidence"]?["controllerName"]
                            ?? (string)json["animatorControllerContext"]?["controllerName"]);
                    result[pathId.Value] = item;
                }

                item.Examples.Add(new JObject
                {
                    ["model"] = modelName,
                    ["modelOutput"] = modelOutput,
                    ["animation"] = animationName,
                    ["animationOutput"] = animationOutput,
                });
            }

            IEnumerable<AnimatorControllerRecoveryRequestInfo> ordered = result.Values
                .OrderByDescending(x => x.Examples.Count)
                .ThenBy(x => x.ControllerName, StringComparer.OrdinalIgnoreCase);
            if (limit > 0)
            {
                ordered = ordered.Take(limit);
            }
            return ordered.ToList();
        }

        private static JObject BuildUnityRecoveryRequest(
            string unityProject,
            string reportDir,
            JObject controller,
            IReadOnlyDictionary<long, AnimationAssetInfo> animationAssets,
            IReadOnlyDictionary<string, string> importedClips,
            bool force)
        {
            var controllerName = (string)controller["name"] ?? "AnimatorController";
            var assetPath = $"{ImportedAnimatorControllerFolder}/{Exporter.FixFileName(controllerName)}.controller";
            var request = new JObject
            {
                ["outputJson"] = Path.Combine(reportDir, Exporter.FixFileName(controllerName) + ".animator_controller_recovery_result.json"),
                ["controllerName"] = controllerName,
                ["controllerAssetPath"] = assetPath,
                ["force"] = force,
                ["clips"] = BuildClipArray(controller, animationAssets, importedClips),
                ["parameters"] = BuildControllerParameterArray(controller),
                ["layers"] = new JArray((controller["layers"] as JArray)?.OfType<JObject>().Select(layer => new JObject
                {
                    ["index"] = (int?)layer["index"] ?? 0,
                    ["stateMachineIndex"] = (int?)layer["stateMachineIndex"] ?? 0,
                    ["stateMachineMotionSetIndex"] = (int?)layer["stateMachineMotionSetIndex"] ?? 0,
                    ["binding"] = (long?)layer["binding"] ?? 0,
                    ["blendingMode"] = (int?)layer["blendingMode"] ?? 0,
                    ["defaultWeight"] = (float?)layer["defaultWeight"] ?? 1f,
                    ["iKPass"] = (bool?)layer["iKPass"] ?? false,
                    ["syncedLayerAffectsTiming"] = (bool?)layer["syncedLayerAffectsTiming"] ?? false,
                    ["bodyMask"] = layer["bodyMask"]?.DeepClone(),
                    ["skeletonMask"] = layer["skeletonMask"]?.DeepClone(),
                }) ?? Enumerable.Empty<JObject>()),
                ["stateMachines"] = new JArray((controller["stateMachines"] as JArray)?.OfType<JObject>().Select(BuildStateMachine) ?? Enumerable.Empty<JObject>()),
            };
            return request;
        }

        private static JArray BuildControllerParameterArray(JObject controller)
        {
            var result = new JArray();
            foreach (var value in controller?["controllerValueDefaults"]?["values"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var name = (string)value["name"];
                var type = (int?)value["type"];
                var resolvedKind = (string)value["resolvedDefaultKind"];
                var defaultFloat = string.Equals(resolvedKind, "float", StringComparison.OrdinalIgnoreCase)
                    ? (float?)value["resolvedDefaultValue"]
                    : null;
                defaultFloat ??= (float?)value["defaultCandidates"]?["float"];
                if (string.IsNullOrWhiteSpace(name) || type != 1 || defaultFloat == null)
                {
                    continue;
                }

                result.Add(new JObject
                {
                    ["name"] = name,
                    ["defaultFloat"] = defaultFloat.Value,
                    ["source"] = string.Equals(resolvedKind, "float", StringComparison.OrdinalIgnoreCase)
                        ? "controllerResolvedDefaultValue"
                        : "controllerDefaultValue",
                });
            }

            return result;
        }

        private static JArray BuildClipArray(
            JObject controller,
            IReadOnlyDictionary<long, AnimationAssetInfo> animationAssets,
            IReadOnlyDictionary<string, string> importedClips)
        {
            return new JArray((controller["clips"] as JArray)?.OfType<JObject>().Select(clip =>
            {
                var pathId = (long?)clip["pathId"] ?? 0;
                animationAssets.TryGetValue(pathId, out var asset);
                var unityPath = ResolveImportedClipPath(importedClips, asset?.Name, pathId);
                return new JObject
                {
                    ["index"] = (int?)clip["index"] ?? 0,
                    ["name"] = asset?.Name ?? string.Empty,
                    ["pathId"] = pathId,
                    ["sourceAnimPath"] = asset?.OutputPath ?? string.Empty,
                    ["libraryAssetAvailable"] = !string.IsNullOrWhiteSpace(asset?.OutputPath),
                    ["unityAssetPath"] = unityPath ?? string.Empty,
                };
            }) ?? Enumerable.Empty<JObject>());
        }

        private static JObject BuildStateMachine(JObject machine)
        {
            return new JObject
            {
                ["machineIndex"] = (int?)machine["machineIndex"] ?? 0,
                ["defaultState"] = (int?)machine["defaultState"] ?? -1,
                ["states"] = new JArray((machine["states"] as JArray)?.OfType<JObject>().Select(state => new JObject
                {
                    ["stateIndex"] = (int?)state["stateIndex"] ?? 0,
                    ["name"] = (string)state["name"] ?? string.Empty,
                    ["fullPath"] = (string)state["fullPath"] ?? (string)state["name"] ?? string.Empty,
                    ["speed"] = (float?)state["speed"] ?? 1f,
                    ["cycleOffset"] = (float?)state["cycleOffset"] ?? 0f,
                    ["iKOnFeet"] = (bool?)state["iKOnFeet"] ?? (bool?)state["stateIKOnFeet"] ?? false,
                    ["mirror"] = (bool?)state["mirror"] ?? false,
                    ["stateParameters"] = state["stateParameters"]?.DeepClone() ?? new JArray(),
                    ["blendTrees"] = new JArray((state["blendTrees"] as JArray)?.OfType<JObject>().Select(tree => new JObject
                    {
                        ["treeIndex"] = (int?)tree["treeIndex"] ?? 0,
                        ["nodes"] = new JArray((tree["nodes"] as JArray)?.OfType<JObject>().Select(BuildNode) ?? Enumerable.Empty<JObject>()),
                    }) ?? Enumerable.Empty<JObject>()),
                }) ?? Enumerable.Empty<JObject>()),
            };
        }

        private static JObject BuildNode(JObject node)
        {
            var clipPPtr = node["clipPPtr"] as JObject;
            return new JObject
            {
                ["nodeIndex"] = (int?)node["nodeIndex"] ?? 0,
                ["blendType"] = (int?)node["blendType"] ?? 0,
                ["blendEvent"] = (string)node["blendEvent"] ?? string.Empty,
                ["childIndices"] = node["childIndices"]?.DeepClone() ?? new JArray(),
                ["childThresholds"] = node["childThresholds"]?.DeepClone() ?? new JArray(),
                ["blend1dChildThresholds"] = node["blend1dChildThresholds"]?.DeepClone() ?? new JArray(),
                ["clipSlot"] = (int?)node["clipSlot"] ?? -1,
                ["clipPathId"] = (long?)clipPPtr?["pathId"] ?? 0,
            };
        }

        private static Dictionary<object, JObject> LoadControllerInspect(string inspectPath)
        {
            var root = JObject.Parse(File.ReadAllText(inspectPath));
            var result = new Dictionary<object, JObject>();
            foreach (var controller in root["files"]?.OfType<JObject>()
                .SelectMany(file => file["animatorControllers"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                ?? Enumerable.Empty<JObject>())
            {
                var pathId = (long?)controller["pathId"];
                var name = (string)controller["name"];
                if (pathId != null)
                {
                    result[pathId.Value] = controller;
                }
                if (!string.IsNullOrWhiteSpace(name))
                {
                    result[name] = controller;
                }
            }
            return result;
        }

        private static Dictionary<long, AnimationAssetInfo> LoadAnimationAssets(string dbPath, string libraryRoot)
        {
            var result = new Dictionary<long, AnimationAssetInfo>();
            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name, output, raw_json FROM assets WHERE kind='Animation';";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var name = ReadDbString(reader, 0);
                var output = ResolveLibraryPath(libraryRoot, ReadDbString(reader, 1));
                try
                {
                    var pathId = (long?)JObject.Parse(ReadDbString(reader, 2) ?? "{}")["pathId"];
                    if (pathId != null)
                    {
                        result[pathId.Value] = new AnimationAssetInfo(name, output);
                    }
                }
                catch
                {
                    // 单个坏 raw_json 不影响其他动画。
                }
            }
            return result;
        }

        private static Dictionary<long, AnimationAssetInfo> LoadAnimationAssetsFromLibrary(string libraryRoot)
        {
            var dbPath = Path.Combine(libraryRoot, "library_index.db");
            return File.Exists(dbPath)
                ? LoadAnimationAssets(dbPath, libraryRoot)
                : new Dictionary<long, AnimationAssetInfo>();
        }

        private static IEnumerable<string> ResolveExtraClipLibraries(IEnumerable<string> roots)
        {
            foreach (var root in roots ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                var fullPath = Path.GetFullPath(root);
                if (Directory.Exists(fullPath) && File.Exists(Path.Combine(fullPath, "library_index.db")))
                {
                    yield return fullPath;
                }
                else
                {
                    Logger.Warning($"AnimatorController extra clip library skipped because library_index.db was not found: {fullPath}");
                }
            }
        }

        private static void MergeAnimationAssets(
            IDictionary<long, AnimationAssetInfo> target,
            IReadOnlyDictionary<long, AnimationAssetInfo> source)
        {
            foreach (var item in source)
            {
                if (!target.TryGetValue(item.Key, out var existing)
                    || string.IsNullOrWhiteSpace(existing.OutputPath))
                {
                    target[item.Key] = item.Value;
                }
            }
        }

        private static void MergeSourceAnimationNames(IDictionary<long, AnimationAssetInfo> result, string sourceIndexPath)
        {
            if (string.IsNullOrWhiteSpace(sourceIndexPath) || !File.Exists(sourceIndexPath))
            {
                return;
            }

            // controller inspect 里 clip 槽位只有 PathID，定向导出的 .anim 文件名则来自原始 AnimationClip.m_Name。
            // 这里只用完整源索引补“PathID -> 原名”，不新增模型-动画关系，也不靠目录或角色名推断。
            using var connection = new SqliteConnection($"Data Source={sourceIndexPath};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT path_id, name FROM source_objects WHERE type='AnimationClip' AND name IS NOT NULL AND name<>'';";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var pathId = reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0);
                var name = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (pathId == null || string.IsNullOrWhiteSpace(name) || result.ContainsKey(pathId.Value))
                {
                    continue;
                }

                result[pathId.Value] = new AnimationAssetInfo(name, null);
            }
        }

        private static Dictionary<string, string> DiscoverImportedAnimationClips(string unityProject, string libraryRoot)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddStringMap(result, LoadUnityBakeStringMap(libraryRoot, "unityAnimationClips"));
            if (string.IsNullOrWhiteSpace(unityProject) || !Directory.Exists(unityProject))
            {
                return result;
            }
            var directory = Path.Combine(unityProject, "Assets", "AnimeStudioBake", "ImportedAnimationClip");
            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*.anim", SearchOption.TopDirectoryOnly))
                {
                    var unityPath = Path.GetRelativePath(unityProject, file).Replace('\\', '/');
                    result[Path.GetFileNameWithoutExtension(file)] = unityPath;
                    result[Path.GetFileName(file)] = unityPath;
                }
            }
            return result;
        }

        private static string ResolveImportedClipPath(IReadOnlyDictionary<string, string> clips, string name, long pathId)
        {
            foreach (var key in new[]
            {
                name,
                string.IsNullOrWhiteSpace(name) ? null : name + ".anim",
                pathId.ToString(CultureInfo.InvariantCulture),
            }.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                if (clips.TryGetValue(key, out var value))
                {
                    return value;
                }
            }
            return null;
        }

        private static JObject RunUnityRecovery(string unityProject, string unityEditor, string requestPath, string logPath)
        {
            if (string.IsNullOrWhiteSpace(unityEditor) || !File.Exists(unityEditor))
            {
                return new JObject
                {
                    ["status"] = "request_written",
                    ["message"] = "--unity_editor was not supplied; wrote recovery request only.",
                };
            }

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = unityEditor,
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments = string.Join(" ", new[]
                {
                    "-batchmode",
                    "-quit",
                    "-projectPath", Quote(Path.GetFullPath(unityProject)),
                    "-executeMethod", "AnimeStudio.UnityBake.AnimeStudioBakeCli.Run",
                    "-animeStudioAnimatorControllerRecovery", Quote(requestPath),
                    "-logFile", Quote(logPath),
                }),
            });
            process.WaitForExit();

            var request = JObject.Parse(File.ReadAllText(requestPath));
            var resultPath = (string)request["outputJson"];
            if (File.Exists(resultPath))
            {
                return JObject.Parse(File.ReadAllText(resultPath));
            }
            return new JObject
            {
                ["status"] = "error",
                ["message"] = $"Unity exited with code {process.ExitCode}, but no recovery result was written. Log: {logPath}",
            };
        }

        private static void RememberRecoveredController(
            IDictionary<string, string> recoveredMap,
            AnimatorControllerRecoveryRequestInfo request,
            JObject controllerInspect,
            string unityPath)
        {
            if (string.IsNullOrWhiteSpace(unityPath))
            {
                return;
            }
            var name = (string)controllerInspect["name"] ?? request.ControllerName;
            foreach (var key in new[]
            {
                name,
                string.IsNullOrWhiteSpace(name) ? null : name + ".controller",
                request.ControllerPathId.ToString(CultureInfo.InvariantCulture),
            }.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                recoveredMap[key] = unityPath;
            }
        }

        private static void UpdateLocalBrowserSettings(string libraryRoot, string unityProject, IReadOnlyDictionary<string, string> recoveredMap)
        {
            if (recoveredMap == null || recoveredMap.Count == 0)
            {
                return;
            }
            var cacheDir = Path.Combine(libraryRoot, ".as_browser_cache");
            Directory.CreateDirectory(cacheDir);
            var settingsPath = Path.Combine(cacheDir, "unity_bake_settings.json");
            var settings = File.Exists(settingsPath) ? JObject.Parse(File.ReadAllText(settingsPath)) : new JObject();
            settings["unityProject"] = Path.GetFullPath(unityProject);
            if (settings["unityAnimatorControllers"] is not JObject map)
            {
                map = new JObject();
                settings["unityAnimatorControllers"] = map;
            }
            foreach (var item in recoveredMap.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                map[item.Key] = item.Value;
            }
            File.WriteAllText(settingsPath, settings.ToString(Formatting.Indented));
        }

        private static void AddStringMap(IDictionary<string, string> target, JObject source)
        {
            if (source == null)
            {
                return;
            }
            foreach (var prop in source.Properties())
            {
                if (!string.IsNullOrWhiteSpace(prop.Name) && prop.Value.Type == JTokenType.String)
                {
                    target[prop.Name] = (string)prop.Value;
                }
            }
        }

        private static JObject LoadUnityBakeStringMap(string libraryRoot, string propertyName)
        {
            JObject result = null;
            foreach (var root in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AnimeStudio", "LibraryBrowser", "settings.json"),
                string.IsNullOrWhiteSpace(libraryRoot) ? null : Path.Combine(libraryRoot, ".as_browser_cache", "unity_bake_settings.json"),
            })
            {
                if (string.IsNullOrWhiteSpace(root) || !File.Exists(root))
                {
                    continue;
                }
                try
                {
                    var map = JObject.Parse(File.ReadAllText(root))[propertyName] as JObject;
                    if (map == null)
                    {
                        continue;
                    }
                    result ??= new JObject();
                    foreach (var prop in map.Properties())
                    {
                        result[prop.Name] = prop.Value;
                    }
                }
                catch
                {
                    // Settings are optional.
                }
            }
            return result;
        }

        private static JObject BuildRequestDiagnostics(JObject request, string requestPath)
        {
            var parameters = request?["parameters"] as JArray;
            var clips = request?["clips"] as JArray;
            var layers = request?["layers"] as JArray;
            var stateMachines = request?["stateMachines"] as JArray;
            var controllerDefaultParameterCount = parameters?.OfType<JObject>().Count(x =>
                string.Equals((string)x["source"], "controllerDefaultValue", StringComparison.OrdinalIgnoreCase)) ?? 0;
            var stateDefaultParameterCount = parameters?.OfType<JObject>().Count(x =>
                string.Equals((string)x["source"], "stateParameter", StringComparison.OrdinalIgnoreCase)) ?? 0;
            var fallbackParameterCount = parameters?.OfType<JObject>().Count(x =>
                string.Equals((string)x["source"], "blendEventFallbackZero", StringComparison.OrdinalIgnoreCase)) ?? 0;
            var missingClipCount = clips?.OfType<JObject>().Count(x =>
                string.IsNullOrWhiteSpace((string)x["unityAssetPath"])) ?? 0;
            var missingLibraryAssetCount = clips?.OfType<JObject>().Count(x =>
                (bool?)x["libraryAssetAvailable"] != true) ?? 0;
            var libraryAssetAvailableCount = clips?.OfType<JObject>().Count(x =>
                (bool?)x["libraryAssetAvailable"] == true) ?? 0;
            return new JObject
            {
                ["requestPath"] = requestPath ?? string.Empty,
                ["parameterCount"] = parameters?.Count ?? 0,
                ["controllerDefaultParameterCount"] = controllerDefaultParameterCount,
                ["stateDefaultParameterCount"] = stateDefaultParameterCount,
                ["fallbackParameterCount"] = fallbackParameterCount,
                ["clipCount"] = clips?.Count ?? 0,
                ["libraryAssetAvailableCount"] = libraryAssetAvailableCount,
                ["missingLibraryAssetCount"] = missingLibraryAssetCount,
                ["missingImportedClipCount"] = missingClipCount,
                ["missingClipCount"] = missingClipCount,
                ["layerCount"] = layers?.Count ?? 0,
                ["stateMachineCount"] = stateMachines?.Count ?? 0,
                ["rule"] = "诊断摘要只说明 request 输入证据是否齐全。libraryAssetAvailable 代表素材库里有原始 .anim；unityAssetPath 代表它已导入 Unity 工程可供 helper 加载。二者都不代表 AnimatorController runtime 语义或动画视觉验收通过。",
            };
        }

        private static JObject WriteResult(AnimatorControllerRecoveryRequestInfo request, string status, string unityAssetPath, string message, JObject unityResult = null, JObject requestDiagnostics = null)
        {
            return new JObject
            {
                ["status"] = status,
                ["controllerName"] = request.ControllerName ?? string.Empty,
                ["controllerPathId"] = request.ControllerPathId,
                ["unityAssetPath"] = unityAssetPath ?? string.Empty,
                ["message"] = message ?? string.Empty,
                ["modelExampleCount"] = request.Examples.Count,
                ["modelExamples"] = new JArray(request.Examples.Take(10)),
                ["requestDiagnostics"] = requestDiagnostics,
                ["unityResult"] = unityResult,
            };
        }

        private static string ReadDbString(SqliteDataReader reader, int index)
        {
            return reader.IsDBNull(index) ? null : reader.GetString(index);
        }

        private static bool MatchesSelector(string selector, params string[] values)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                return true;
            }
            return values.Any(value => !string.IsNullOrWhiteSpace(value)
                && value.IndexOf(selector, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string ResolveLibraryPath(string libraryRoot, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            {
                return path;
            }
            return Path.GetFullPath(Path.Combine(libraryRoot, path));
        }

        private static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";

        private sealed class AnimatorControllerRecoveryRequestInfo
        {
            public AnimatorControllerRecoveryRequestInfo(long controllerPathId, string controllerName)
            {
                ControllerPathId = controllerPathId;
                ControllerName = controllerName;
            }

            public long ControllerPathId { get; }
            public string ControllerName { get; }
            public List<JObject> Examples { get; } = new();
        }

        private sealed record AnimationAssetInfo(string Name, string OutputPath);
    }
}
