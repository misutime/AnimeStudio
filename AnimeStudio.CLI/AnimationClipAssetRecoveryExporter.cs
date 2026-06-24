using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AnimeStudio.CLI
{
    internal static class AnimationClipAssetRecoveryExporter
    {
        private const string ImportedAnimationClipFolder = "Assets/AnimeStudioBake/ImportedAnimationClip";

        public static string Recover(
            string libraryRoot,
            string unityProject,
            string modelSelector,
            string animationSelector,
            int limit,
            bool force,
            string explicitIndexPath = null)
        {
            if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
            {
                throw new DirectoryNotFoundException($"Library root not found: {libraryRoot}");
            }

            if (string.IsNullOrWhiteSpace(unityProject) || !Directory.Exists(unityProject))
            {
                throw new DirectoryNotFoundException("--unity_project is required and must point to a Unity bake project.");
            }

            var libraryPath = Path.GetFullPath(libraryRoot);
            var dbPath = string.IsNullOrWhiteSpace(explicitIndexPath)
                ? Path.Combine(libraryPath, "library_index.db")
                : Path.GetFullPath(explicitIndexPath);
            if (!File.Exists(dbPath))
            {
                throw new FileNotFoundException($"library_index.db not found: {dbPath}", dbPath);
            }

            var importedRoot = Path.Combine(Path.GetFullPath(unityProject), ImportedAnimationClipFolder.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(importedRoot);
            SQLitePCL.Batteries_V2.Init();

            var started = DateTimeOffset.UtcNow;
            var reportDir = Path.Combine(libraryPath, ".as_browser_cache", "diagnostics", $"imported_animation_clip_recovery_{started:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(reportDir);
            var reportPath = Path.Combine(reportDir, "imported_animation_clip_recovery.json");

            var requests = ReadRecoveryRequests(dbPath, libraryPath, importedRoot, modelSelector, animationSelector, limit, force);
            var results = new JArray();
            var recoveredMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var request in requests)
            {
                try
                {
                    if (!File.Exists(request.SourceAnimPath))
                    {
                        results.Add(WriteResult(request, "missing_source_anim", null, $"Animation asset file not found: {request.SourceAnimPath}"));
                        continue;
                    }

                    var target = Path.Combine(importedRoot, Exporter.FixFileName(request.AnimationName) + ".anim");
                    if (!force && File.Exists(target))
                    {
                        var existingUnityPath = ToUnityAssetPath(unityProject, target);
                        RememberRecoveredClip(recoveredMap, request, target, existingUnityPath);
                        results.Add(WriteResult(request, "already_exists", existingUnityPath, "ImportedAnimationClip already exists."));
                        continue;
                    }

                    File.Copy(request.SourceAnimPath, target, overwrite: true);
                    var unityPath = ToUnityAssetPath(unityProject, target);
                    RememberRecoveredClip(recoveredMap, request, target, unityPath);
                    results.Add(WriteResult(request, "recovered", unityPath, "Copied deterministic Unity AnimationClip .anim into the bake project."));
                }
                catch (Exception e)
                {
                    results.Add(WriteResult(request, "failed", null, e.GetType().Name + ": " + e.Message));
                }
            }

            UpdateLocalBrowserSettings(libraryPath, unityProject, recoveredMap);
            var recovered = results.Count(x =>
                string.Equals((string)x["status"], "recovered", StringComparison.OrdinalIgnoreCase)
                || string.Equals((string)x["status"], "already_exists", StringComparison.OrdinalIgnoreCase));
            var report = new JObject
            {
                ["status"] = requests.Count == 0 ? "nothing_to_recover" : recovered == requests.Count ? "ok" : recovered > 0 ? "partial" : "failed",
                ["libraryRoot"] = libraryPath,
                ["unityProject"] = Path.GetFullPath(unityProject),
                ["createdUtc"] = started.ToString("O", CultureInfo.InvariantCulture),
                ["selectedAnimationClips"] = requests.Count,
                ["availableImportedAnimationClips"] = recovered,
                ["failedAnimationClips"] = requests.Count - recovered,
                ["rule"] = "优先从 library_index.db 中 relation_source=explicit 的 Humanoid/Muscle 候选恢复实际 bake 的 AnimationClip；AnimatorController 辅助 clip 会切到同状态 baseLayerClip。若这是只有 Animation 资产的定向导出库，则按 assets.kind='Animation' 直接恢复这些已由 PathID 精确导出的 .anim，不用名称或骨骼数量猜关系。",
                ["results"] = results,
            };
            File.WriteAllText(reportPath, report.ToString(Formatting.Indented));
            Logger.Info($"Imported AnimationClip recovery report written: {reportPath}");
            return reportPath;
        }

        private static List<AnimationClipRecoveryRequest> ReadRecoveryRequests(
            string dbPath,
            string libraryRoot,
            string importedRoot,
            string modelSelector,
            string animationSelector,
            int limit,
            bool force)
        {
            var animationAssets = ReadAnimationAssets(dbPath, libraryRoot);
            var requests = new Dictionary<string, AnimationClipRecoveryRequest>(StringComparer.OrdinalIgnoreCase);
            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT m.name, m.output, a.name, a.output, c.raw_json
FROM model_animation_candidates c
JOIN assets m ON m.kind='Model' AND m.output=c.model_output
JOIN assets a ON a.kind='Animation' AND a.output=c.animation_output
WHERE c.relation_source='explicit'
  AND (
    COALESCE(json_extract(c.raw_json, '$.requiresUnityBake'), 0)=1
    OR COALESCE(json_extract(c.raw_json, '$.hasMuscleClip'), 0)=1
    OR COALESCE(json_extract(c.raw_json, '$.fullHumanoidBakeRequired'), 0)=1
    OR COALESCE(json_extract(c.raw_json, '$.humanoid.requiresUnityBake'), 0)=1
    OR json_extract(c.raw_json, '$.animatorControllerContext.baseLayerClip.clip.pathId') IS NOT NULL
  );";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var modelName = ReadDbString(reader, 0);
                var modelOutput = ReadDbString(reader, 1);
                var candidateName = ReadDbString(reader, 2);
                var candidateOutput = ResolveLibraryPath(libraryRoot, ReadDbString(reader, 3));
                var raw = ReadDbString(reader, 4);

                JObject relation;
                try
                {
                    relation = string.IsNullOrWhiteSpace(raw) ? new JObject() : JObject.Parse(raw);
                }
                catch
                {
                    relation = new JObject();
                }

                var actual = ResolveActualBakeAnimation(relation, animationAssets, candidateName, candidateOutput);
                if (actual == null || string.IsNullOrWhiteSpace(actual.OutputPath))
                {
                    continue;
                }

                if (!MatchesSelector(modelSelector, modelName, modelOutput)
                    || !MatchesSelector(animationSelector, candidateName, candidateOutput, actual.Name, actual.OutputPath))
                {
                    continue;
                }

                var target = Path.Combine(importedRoot, Exporter.FixFileName(actual.Name) + ".anim");
                if (!force && File.Exists(target))
                {
                    // 已存在也写入报告和 settings，保证 Browser 能自动引用。
                }

                AddRecoveryRequest(requests, actual.Name, actual.OutputPath, actual.Reason, null, new JObject
                {
                    ["model"] = modelName,
                    ["modelOutput"] = modelOutput,
                    ["selectedAnimation"] = candidateName,
                    ["selectedAnimationOutput"] = candidateOutput,
                });

                foreach (var additional in ResolveAdditionalLayerAnimations(relation, animationAssets))
                {
                    AddRecoveryRequest(requests, additional.Name, additional.OutputPath, additional.Reason, additional.LookupKey, new JObject
                    {
                        ["model"] = modelName,
                        ["modelOutput"] = modelOutput,
                        ["selectedAnimation"] = candidateName,
                        ["selectedAnimationOutput"] = candidateOutput,
                        ["additionalLayerClip"] = additional.Name,
                    });
                }
            }

            IEnumerable<AnimationClipRecoveryRequest> ordered = requests.Values
                .OrderByDescending(x => x.ModelExamples.Count)
                .ThenBy(x => x.AnimationName, StringComparer.OrdinalIgnoreCase);
            if (requests.Count == 0)
            {
                ordered = BuildDirectAnimationAssetRecoveryRequests(animationAssets, animationSelector);
            }
            if (limit > 0)
            {
                ordered = ordered.Take(limit);
            }

            return ordered.ToList();
        }

        private static IEnumerable<AnimationClipRecoveryRequest> BuildDirectAnimationAssetRecoveryRequests(
            IReadOnlyDictionary<long, AnimationAssetInfo> animationAssets,
            string animationSelector)
        {
            foreach (var item in animationAssets
                .OrderBy(x => x.Value.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Key))
            {
                if (!MatchesSelector(animationSelector, item.Value.Name, item.Value.OutputPath, item.Key.ToString(CultureInfo.InvariantCulture)))
                {
                    continue;
                }

                var request = new AnimationClipRecoveryRequest(
                    item.Value.Name,
                    item.Value.OutputPath,
                    "directAnimationAssetPathIdExport");
                request.LookupKeys.Add(item.Key.ToString(CultureInfo.InvariantCulture));
                request.ModelExamples.Add(new JObject
                {
                    ["source"] = "assets.kind=Animation",
                    ["pathId"] = item.Key,
                    ["animation"] = item.Value.Name,
                    ["animationOutput"] = item.Value.OutputPath,
                    ["rule"] = "这是定向导出的 AnimationClip 资产，不是模型-动画生产候选；只用于补齐 Unity worker 中的原始 .anim 依赖。"
                });
                yield return request;
            }
        }

        private static void AddRecoveryRequest(
            Dictionary<string, AnimationClipRecoveryRequest> requests,
            string name,
            string outputPath,
            string reason,
            string lookupKey,
            JObject modelExample)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(outputPath))
            {
                return;
            }

            var key = name + "|" + outputPath;
            if (!requests.TryGetValue(key, out var request))
            {
                request = new AnimationClipRecoveryRequest(name, outputPath, reason);
                requests[key] = request;
            }

            if (!string.IsNullOrWhiteSpace(lookupKey))
            {
                request.LookupKeys.Add(lookupKey);
            }
            request.ModelExamples.Add(modelExample);
        }

        private static Dictionary<long, AnimationAssetInfo> ReadAnimationAssets(string dbPath, string libraryRoot)
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
                var raw = ReadDbString(reader, 2);
                long? pathId = null;
                try
                {
                    pathId = (long?)JObject.Parse(raw ?? "{}")["pathId"];
                }
                catch
                {
                    pathId = null;
                }

                if (pathId == null || string.IsNullOrWhiteSpace(output))
                {
                    continue;
                }

                result[pathId.Value] = new AnimationAssetInfo(name, output);
            }

            return result;
        }

        private static ActualBakeAnimation ResolveActualBakeAnimation(
            JObject relation,
            IReadOnlyDictionary<long, AnimationAssetInfo> animationAssets,
            string candidateName,
            string candidateOutput)
        {
            var basePathId = ReadBaseLayerClipPathId(relation);
            if (basePathId != null && animationAssets.TryGetValue(basePathId.Value, out var baseAnimation))
            {
                return new ActualBakeAnimation(
                    baseAnimation.Name,
                    baseAnimation.OutputPath,
                    "animatorControllerContext.baseLayerClip");
            }

            return new ActualBakeAnimation(
                candidateName,
                candidateOutput,
                "explicitCandidateAnimation");
        }

        private static IEnumerable<ActualBakeAnimation> ResolveAdditionalLayerAnimations(
            JObject relation,
            IReadOnlyDictionary<long, AnimationAssetInfo> animationAssets)
        {
            foreach (var clip in relation?["animatorControllerContext"]?["additionalLayerClips"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var pathId = (long?)clip["pathId"];
                if (pathId == null || !animationAssets.TryGetValue(pathId.Value, out var animation))
                {
                    continue;
                }

                yield return new ActualBakeAnimation(
                    animation.Name,
                    animation.OutputPath,
                    "animatorControllerContext.additionalLayerClips",
                    pathId.Value.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static long? ReadBaseLayerClipPathId(JObject relation)
        {
            // 有些旧索引会把 baseLayerClip/clip 写成字符串或简单值。
            // 这里不能直接链式索引，否则 JValue 会抛异常；读不到就使用显式候选动画。
            if (relation?["animatorControllerContext"] is not JObject context
                || context["baseLayerClip"] is not JObject baseLayerClip
                || baseLayerClip["clip"] is not JObject clip)
            {
                return null;
            }

            return (long?)clip["pathId"];
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
            JObject settings;
            try
            {
                settings = File.Exists(settingsPath) ? JObject.Parse(File.ReadAllText(settingsPath)) : new JObject();
            }
            catch
            {
                settings = new JObject();
            }

            settings["unityProject"] = Path.GetFullPath(unityProject);
            if (settings["unityAnimationClips"] is not JObject clips)
            {
                clips = new JObject();
                settings["unityAnimationClips"] = clips;
            }

            foreach (var item in recoveredMap.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                clips[item.Key] = item.Value;
            }

            File.WriteAllText(settingsPath, settings.ToString(Formatting.Indented));
        }

        private static JObject WriteResult(AnimationClipRecoveryRequest request, string status, string unityAssetPath, string message)
        {
            return new JObject
            {
                ["status"] = status,
                ["animationName"] = request.AnimationName,
                ["sourceAnim"] = request.SourceAnimPath,
                ["unityAssetPath"] = unityAssetPath ?? string.Empty,
                ["reason"] = request.Reason,
                ["message"] = message ?? string.Empty,
                ["modelExampleCount"] = request.ModelExamples.Count,
                ["modelExamples"] = new JArray(request.ModelExamples.Take(10)),
            };
        }

        private static void RememberRecoveredClip(
            Dictionary<string, string> recoveredMap,
            AnimationClipRecoveryRequest request,
            string target,
            string unityPath)
        {
            recoveredMap[request.AnimationName] = unityPath;
            recoveredMap[Path.GetFileNameWithoutExtension(target)] = unityPath;
            foreach (var key in request.LookupKeys)
            {
                recoveredMap[key] = unityPath;
            }
        }

        private static bool MatchesSelector(string selector, params string[] values)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                return true;
            }

            foreach (var item in selector.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (MatchesOneSelector(item, values))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool MatchesOneSelector(string selector, params string[] values)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                return true;
            }

            if (values.Any(x => string.Equals(x, selector, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            var selectorPath = NormalizeSelectorPath(selector);
            var selectorFile = Path.GetFileName(selectorPath);
            var selectorStem = Path.GetFileNameWithoutExtension(selectorPath);
            foreach (var value in values.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                var valuePath = NormalizeSelectorPath(value);
                if (string.Equals(valuePath, selectorPath, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(selectorFile) && string.Equals(Path.GetFileName(valuePath), selectorFile, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(selectorStem) && string.Equals(Path.GetFileNameWithoutExtension(valuePath), selectorStem, StringComparison.OrdinalIgnoreCase))
                    || valuePath.EndsWith(selectorPath, StringComparison.OrdinalIgnoreCase)
                    || selectorPath.EndsWith(valuePath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // Browser 传入的是本机 glTF/.anim 绝对路径时，只做路径匹配，避免反斜杠被 Regex 误解。
            if (LooksLikePathSelector(selectorPath))
            {
                return false;
            }

            try
            {
                var regex = new Regex(selector, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                return values.Where(x => !string.IsNullOrWhiteSpace(x)).Any(x => regex.IsMatch(x));
            }
            catch
            {
                return values.Where(x => !string.IsNullOrWhiteSpace(x)).Any(x => x.IndexOf(selector, StringComparison.OrdinalIgnoreCase) >= 0);
            }
        }

        private static string NormalizeSelectorPath(string value)
        {
            return (value ?? string.Empty)
                .Trim()
                .Trim('"')
                .Replace('\\', '/')
                .TrimEnd('/');
        }

        private static bool LooksLikePathSelector(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && (value.IndexOf('/') >= 0
                    || value.IndexOf('\\') >= 0
                    || string.Equals(Path.GetExtension(value), ".gltf", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetExtension(value), ".glb", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetExtension(value), ".anim", StringComparison.OrdinalIgnoreCase));
        }

        private static string ResolveLibraryPath(string libraryRoot, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return Path.IsPathRooted(path)
                ? path
                : Path.Combine(libraryRoot, path.Replace('/', Path.DirectorySeparatorChar));
        }

        private static string ToUnityAssetPath(string unityProject, string file)
        {
            return Path.GetRelativePath(Path.GetFullPath(unityProject), Path.GetFullPath(file)).Replace('\\', '/');
        }

        private static string ReadDbString(SqliteDataReader reader, int index)
        {
            return reader.IsDBNull(index) ? null : reader.GetString(index);
        }

        private sealed record AnimationAssetInfo(string Name, string OutputPath);

        private sealed record ActualBakeAnimation(string Name, string OutputPath, string Reason, string LookupKey = null);

        private sealed record AnimationClipRecoveryRequest(string AnimationName, string SourceAnimPath, string Reason)
        {
            public List<JObject> ModelExamples { get; } = new();

            public HashSet<string> LookupKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
