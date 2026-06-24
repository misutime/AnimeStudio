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
    internal static class AnimatorControllerContextRefresher
    {
        public static string Refresh(
            string libraryRoot,
            string unityFileInspectPath,
            string sourceIndexPath = null,
            string indexPath = null,
            string modelSelector = null,
            string animationSelector = null)
        {
            if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
            {
                throw new DirectoryNotFoundException($"Library root not found: {libraryRoot}");
            }
            if (string.IsNullOrWhiteSpace(unityFileInspectPath) || !File.Exists(unityFileInspectPath))
            {
                throw new FileNotFoundException($"unity_file_inspect.json not found: {unityFileInspectPath}", unityFileInspectPath);
            }

            SQLitePCL.Batteries_V2.Init();
            var root = Path.GetFullPath(libraryRoot);
            var dbPath = string.IsNullOrWhiteSpace(indexPath)
                ? Path.Combine(root, "library_index.db")
                : Path.GetFullPath(indexPath);
            var sourceIndex = ResolveSourceIndex(root, sourceIndexPath);
            if (!File.Exists(dbPath))
            {
                throw new FileNotFoundException($"library_index.db not found: {dbPath}", dbPath);
            }
            if (!File.Exists(sourceIndex))
            {
                throw new FileNotFoundException($"unity_source_index.db not found: {sourceIndex}", sourceIndex);
            }

            var contexts = LoadControllerContexts(unityFileInspectPath);
            Logger.Info($"Loaded {contexts.Count} AnimatorController baseLayerClip context(s) from {unityFileInspectPath}.");
            var importedAvatarAssets = LoadImportedAvatarAssets(root);
            Logger.Info($"Loaded {importedAvatarAssets.Count} imported Unity Avatar asset lookup key(s).");
            var controllerResolver = new ModelControllerResolver(sourceIndex);
            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();

            var rows = ReadBlockedCandidateRows(connection, modelSelector, animationSelector);
            var changed = 0;
            var matched = 0;
            var blockedReasonCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var blockedItems = new JArray();
            using var transaction = connection.BeginTransaction();
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE model_animation_candidates SET raw_json=$rawJson WHERE id=$id;";
            var rawJsonParameter = update.Parameters.Add("$rawJson", SqliteType.Text);
            var idParameter = update.Parameters.Add("$id", SqliteType.Integer);

            using var deleteCache = connection.CreateCommand();
            deleteCache.Transaction = transaction;
            deleteCache.CommandText = "DELETE FROM animation_bake_cache WHERE model_output=$modelOutput AND animation_output=$animationOutput;";
            var cacheModel = deleteCache.Parameters.Add("$modelOutput", SqliteType.Text);
            var cacheAnimation = deleteCache.Parameters.Add("$animationOutput", SqliteType.Text);

            foreach (var row in rows)
            {
                var animationPathId = (long?)row.Animation?["pathId"];
                if (animationPathId == null)
                {
                    AddBlockedItem(blockedItems, blockedReasonCounts, row, "missing_animation_path_id", null);
                    continue;
                }

                var modelPathId = (long?)row.Model?["pathId"];
                var modelSource = S(row.Model, "source") ?? S(row.Model, "sourceFile");
                if (modelPathId == null || string.IsNullOrWhiteSpace(modelSource))
                {
                    AddBlockedItem(blockedItems, blockedReasonCounts, row, "missing_model_path_id_or_source", null);
                    continue;
                }

                var controllerPathIds = controllerResolver.FindControllerPathIds(modelSource, modelPathId.Value)
                    .Concat(ReadControllerPathIdsFromRelationEvidence(row.RawJson))
                    .Distinct()
                    .ToArray();
                if (controllerPathIds.Length == 0)
                {
                    AddBlockedItem(blockedItems, blockedReasonCounts, row, "missing_animator_controller_relation", new JObject
                    {
                        ["modelSource"] = modelSource,
                        ["modelPathId"] = modelPathId.Value,
                    });
                    continue;
                }

                var rowChanged = false;
                foreach (var controllerPathId in controllerPathIds)
                {
                    if (!contexts.TryGetValue((controllerPathId, animationPathId.Value), out var context))
                    {
                        continue;
                    }

                    matched++;
                    var refreshed = RefreshCandidateJson(
                        row.RawJson,
                        row.Model,
                        row.ModelOutput,
                        context,
                        unityFileInspectPath,
                        importedAvatarAssets);
                    rawJsonParameter.Value = refreshed.ToString(Formatting.None);
                    idParameter.Value = row.Id;
                    update.ExecuteNonQuery();

                    cacheModel.Value = row.ModelOutput;
                    cacheAnimation.Value = row.AnimationOutput;
                    deleteCache.ExecuteNonQuery();
                    changed++;
                    rowChanged = true;
                    break;
                }

                if (!rowChanged)
                {
                    AddBlockedItem(blockedItems, blockedReasonCounts, row, "controller_context_not_found_in_inspect", new JObject
                    {
                        ["modelSource"] = modelSource,
                        ["modelPathId"] = modelPathId.Value,
                        ["animationPathId"] = animationPathId.Value,
                        ["controllerPathIds"] = new JArray(controllerPathIds),
                    });
                }
            }

            transaction.Commit();
            var report = new JObject
            {
                ["status"] = "ok",
                ["libraryRoot"] = root,
                ["libraryIndex"] = dbPath,
                ["sourceIndex"] = sourceIndex,
                ["unityFileInspect"] = Path.GetFullPath(unityFileInspectPath),
                ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["controllerContextCount"] = contexts.Count,
                ["importedAvatarAssetKeyCount"] = importedAvatarAssets.Count,
                ["blockedCandidateRows"] = rows.Count,
                ["matchedRows"] = matched,
                ["refreshedRows"] = changed,
                ["blockedReasonCounts"] = JObject.FromObject(blockedReasonCounts),
                // 只写前 200 条，避免原神这类大库报告变成另一个巨型索引。
                ["blockedItemsSample"] = blockedItems,
                ["rule"] = "只用 Unity Animator.controller 显式关系、候选 relationEvidence.controllerPathId 和 AnimatorController state/default layer 上下文修复 Humanoid bake 上下文；不按名称、骨骼数量或目录猜测。",
            };
            var reportPath = Path.Combine(root, "animator_controller_context_refresh.json");
            File.WriteAllText(reportPath, report.ToString(Formatting.Indented));
            Logger.Info($"AnimatorController context refresh updated {changed} candidate row(s). Report: {reportPath}");
            return reportPath;
        }

        private static string ResolveSourceIndex(string libraryRoot, string explicitSourceIndexPath)
        {
            if (!string.IsNullOrWhiteSpace(explicitSourceIndexPath))
            {
                return Path.GetFullPath(explicitSourceIndexPath);
            }

            foreach (var candidate in EnumerateSourceIndexCandidates(libraryRoot))
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            // 保留旧默认路径作为错误信息，方便定位小库是否缺少源索引引用。
            return Path.Combine(libraryRoot, "unity_source_index.db");
        }

        private static IEnumerable<string> EnumerateSourceIndexCandidates(string libraryRoot)
        {
            if (string.IsNullOrWhiteSpace(libraryRoot))
            {
                yield break;
            }

            yield return Path.Combine(libraryRoot, "unity_source_index.db");
            foreach (var reportName in new[]
            {
                "source_index_usage.json",
                "animator_controller_context_refresh.json",
                "sqlite_index_summary.json",
            })
            {
                var reportPath = Path.Combine(libraryRoot, reportName);
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

        private static void AddBlockedItem(
            JArray blockedItems,
            Dictionary<string, int> blockedReasonCounts,
            CandidateRow row,
            string reason,
            JObject details)
        {
            if (!string.IsNullOrWhiteSpace(reason))
            {
                blockedReasonCounts[reason] = blockedReasonCounts.TryGetValue(reason, out var count)
                    ? count + 1
                    : 1;
            }

            if (blockedItems == null || blockedItems.Count >= 200)
            {
                return;
            }

            var item = new JObject
            {
                ["modelOutput"] = row.ModelOutput,
                ["animationOutput"] = row.AnimationOutput,
                ["reason"] = reason,
            };
            if (details != null)
            {
                item["details"] = details;
            }
            blockedItems.Add(item);
        }

        private static List<CandidateRow> ReadBlockedCandidateRows(SqliteConnection connection, string modelSelector, string animationSelector)
        {
            var result = new List<CandidateRow>();
            var modelOutputs = SelectMatchingAssetOutputs(connection, "Model", modelSelector, 4096);
            var animationOutputs = SelectMatchingAssetOutputs(connection, "Animation", animationSelector, 8192);
            if (modelOutputs != null && modelOutputs.Count == 0)
            {
                return result;
            }
            if (animationOutputs != null && animationOutputs.Count == 0)
            {
                return result;
            }

            using var command = connection.CreateCommand();
            var modelFilterSql = AddOutputFilterParameters(command, "modelOutput", modelOutputs, "c.model_output");
            var animationFilterSql = AddOutputFilterParameters(command, "animationOutput", animationOutputs, "c.animation_output");
            command.CommandText = @"
SELECT c.id, c.model_output, c.animation_output, c.raw_json, m.raw_json, a.raw_json, m.name, a.name
FROM model_animation_candidates c
JOIN assets m ON m.kind='Model' AND m.output=c.model_output
JOIN assets a ON a.kind='Animation' AND a.output=c.animation_output
WHERE c.relation_source='explicit'
" + modelFilterSql + animationFilterSql + @"
  AND (
    (
      json_extract(c.raw_json, '$.animatorControllerContext.baseLayerClip.clip.pathId') IS NULL
      AND (
        json_extract(c.raw_json, '$.productionAnimationPath')='NeedsAnimatorControllerContext'
        OR json_extract(c.raw_json, '$.productionUnityBakeBlockedReason')='requires_animator_controller_context'
        OR json_extract(c.raw_json, '$.nextAction')='inspect_animator_controller_context'
        OR (
          json_extract(c.raw_json, '$.productionAnimationPath')='NeedsDirectTrsAnimation'
          AND json_extract(c.raw_json, '$.animatorControllerContext.source') IS NOT NULL
        )
        OR (
          json_extract(c.raw_json, '$.productionAnimationPath')='NeedsUnityAvatarMetadata'
          AND (
            COALESCE(json_extract(a.raw_json, '$.hasMuscleClip'), 0)=1
            OR COALESCE(json_extract(a.raw_json, '$.humanoid.hasMuscleClip'), 0)=1
            OR json_extract(a.raw_json, '$.animationType') LIKE '%Humanoid%'
          )
        )
      )
    )
    OR (
      json_extract(c.raw_json, '$.animatorControllerContext.baseLayerClip.clip.pathId') IS NOT NULL
      AND json_extract(c.raw_json, '$.productionUnityBakeBlockedReason')='missing_production_avatar_oracle'
    )
    OR (
      json_extract(c.raw_json, '$.animatorControllerContext.additionalLayerClipRule') LIKE 'AnimatorController default non-empty layer states whose state path matches the selected motion stem%'
      AND json_extract(c.raw_json, '$.animatorControllerContext.additionalLayerClipRule') NOT LIKE '%exactly one deterministic clip%'
    )
  );";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var modelOutput = reader.GetString(1);
                var animationOutput = reader.GetString(2);
                var modelName = reader.IsDBNull(6) ? null : reader.GetString(6);
                var animationName = reader.IsDBNull(7) ? null : reader.GetString(7);
                if (!MatchesSelector(modelSelector, modelName, modelOutput)
                    || !MatchesSelector(animationSelector, animationName, animationOutput))
                {
                    continue;
                }

                result.Add(new CandidateRow(
                    reader.GetInt64(0),
                    modelOutput,
                    animationOutput,
                    JObject.Parse(reader.GetString(3)),
                    JObject.Parse(reader.GetString(4)),
                    JObject.Parse(reader.GetString(5))));
            }

            return result;
        }

        private static HashSet<string> SelectMatchingAssetOutputs(SqliteConnection connection, string kind, string selector, int limit)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                return null;
            }

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT name, output
FROM assets
WHERE kind=$kind
ORDER BY name COLLATE NOCASE
LIMIT 200000;";
            command.Parameters.AddWithValue("$kind", kind);
            using var reader = command.ExecuteReader();
            while (reader.Read() && result.Count < Math.Max(1, limit))
            {
                var name = reader.IsDBNull(0) ? null : reader.GetString(0);
                var output = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (string.IsNullOrWhiteSpace(output) || !MatchesSelector(selector, name, output))
                {
                    continue;
                }

                result.Add(output);
            }

            return result;
        }

        private static string AddOutputFilterParameters(SqliteCommand command, string prefix, HashSet<string> outputs, string column)
        {
            if (outputs == null)
            {
                return string.Empty;
            }
            if (outputs.Count == 0)
            {
                return " AND 1=0";
            }

            var names = new List<string>();
            var index = 0;
            foreach (var output in outputs)
            {
                var parameterName = "$" + prefix + index.ToString(CultureInfo.InvariantCulture);
                command.Parameters.AddWithValue(parameterName, output);
                names.Add(parameterName);
                index++;
            }

            return $" AND {column} IN ({string.Join(",", names)})";
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

            // Windows 绝对路径里的反斜杠会被 Regex 当转义符。Browser 传路径时只按路径规则匹配。
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

        private static JObject RefreshCandidateJson(
            JObject raw,
            JObject model,
            string modelOutput,
            JObject context,
            string inspectPath,
            IReadOnlyDictionary<string, string> importedAvatarAssets)
        {
            var avatarResolution = ResolveProductionAvatarAsset(model, modelOutput, raw, importedAvatarAssets);
            var hasProductionAvatar = avatarResolution.HasProductionAvatar;
            var result = (JObject)raw.DeepClone();
            result["requiresUnityBake"] = true;
            result["requiresHumanoidBake"] = true;
            result["legacyUnityBakeSupported"] = true;
            result["fullHumanoidBakeRequired"] = true;
            result["productionAnimationPath"] = "UnityBakeToGltf";
            result["nextAction"] = "generate_unity_baked_gltf";
            result["animatorControllerBodyClipReady"] = true;
            result["animatorControllerContext"] = context;
            result["productionUnityBakeReady"] = hasProductionAvatar;
            result["productionUnityBakeBlocked"] = !hasProductionAvatar;
            result["productionUnityBakeBlockedReason"] = hasProductionAvatar ? null : "missing_production_avatar_oracle";
            result["fullHumanoidBakeBlocked"] = !hasProductionAvatar;
            result["fullHumanoidBakeBlockedReason"] = hasProductionAvatar ? null : "missing_production_avatar_oracle";
            if (!string.IsNullOrWhiteSpace(avatarResolution.AvatarAsset))
            {
                result["unityAvatarAsset"] = avatarResolution.AvatarAsset;
                result["unityAvatarMatchKey"] = avatarResolution.MatchKey;
                result["productionUnityBakeAvatarSource"] = "imported_unity_avatar_asset";
                result["importedAvatarAssetValidated"] = true;
            }
            result["animatorControllerContextRefresh"] = new JObject
            {
                ["source"] = Path.GetFullPath(inspectPath),
                ["refreshedUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["rule"] = "Animator.controller -> AnimatorController state/default layer context; default additional layer clips are diagnostic until original RuntimeAnimatorController sampling is recovered",
            };
            return result;
        }

        private static AvatarResolution ResolveProductionAvatarAsset(
            JObject model,
            string modelOutput,
            JObject candidate,
            IReadOnlyDictionary<string, string> importedAvatarAssets)
        {
            var existing = S(candidate, "unityAvatarAsset");
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return new AvatarResolution(true, existing, S(candidate, "unityAvatarMatchKey") ?? "candidate.unityAvatarAsset");
            }

            var avatar = model?["avatar"] as JObject;
            if ((avatar?["humanBones"] as JArray)?.Count > 0
                && (avatar?["skeletonBones"] as JArray)?.Count > 0)
            {
                return new AvatarResolution(true, null, "model.avatar.humanDescription");
            }

            if (importedAvatarAssets != null && importedAvatarAssets.Count > 0)
            {
                foreach (var key in BuildUnityAvatarLookupKeys(model, modelOutput))
                {
                    if (importedAvatarAssets.TryGetValue(key, out var assetPath))
                    {
                        return new AvatarResolution(true, assetPath, key);
                    }
                }
            }

            return new AvatarResolution(false, null, null);
        }

        private static IReadOnlyDictionary<string, string> LoadImportedAvatarAssets(string libraryRoot)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var localSettings = LoadJsonObject(Path.Combine(libraryRoot, ".as_browser_cache", "unity_bake_settings.json"));
            var globalSettings = LoadJsonObject(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AnimeStudio",
                "LibraryBrowser",
                "settings.json"));
            var unityProject = FirstNotEmpty(
                S(localSettings, "unityProject"),
                S(globalSettings, "unityProject"),
                Environment.GetEnvironmentVariable("ANIMESTUDIO_UNITY_BAKE_PROJECT"));

            AddStringMap(result, globalSettings?["unityAvatarAssets"] as JObject);
            AddStringMap(result, localSettings?["unityAvatarAssets"] as JObject);
            DiscoverImportedAvatarAssets(result, unityProject, libraryRoot);
            return result;
        }

        private static void DiscoverImportedAvatarAssets(Dictionary<string, string> result, string unityProject, string libraryRoot)
        {
            if (result == null || string.IsNullOrWhiteSpace(unityProject))
            {
                return;
            }

            var directory = Path.Combine(unityProject, "Assets", "AnimeStudioBake", "ImportedAvatar");
            if (!Directory.Exists(directory))
            {
                return;
            }

            var assetFiles = Directory.EnumerateFiles(directory, "*.asset", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .ToArray();
            var validKeys = LoadFreshImportedAvatarProbeKeys(libraryRoot, assetFiles);
            if (assetFiles.Length > 0 && validKeys == null)
            {
                Logger.Warning("ImportedAvatar 目录存在 Avatar asset，但没有新鲜的 probe 验证报告；刷新器不会把未验证 Avatar 标成生产可用。");
                return;
            }

            foreach (var file in assetFiles)
            {
                var name = Path.GetFileNameWithoutExtension(file.FullName);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (validKeys != null
                    && !validKeys.Contains(name)
                    && !(name.EndsWith("_ModelAvatar", StringComparison.OrdinalIgnoreCase)
                        && validKeys.Contains(name[..^"_ModelAvatar".Length])))
                {
                    continue;
                }

                var unityPath = Path.GetRelativePath(unityProject, file.FullName).Replace('\\', '/');
                if (!unityPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AddImportedAvatarAssetMapKey(result, name, unityPath);
            }
        }

        private static HashSet<string> LoadFreshImportedAvatarProbeKeys(string libraryRoot, FileInfo[] assetFiles)
        {
            if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
            {
                return null;
            }

            FileInfo reportFile = null;
            try
            {
                reportFile = Directory.EnumerateDirectories(libraryRoot, "ImportedAvatarProbe*", SearchOption.TopDirectoryOnly)
                    .Select(dir => Path.Combine(dir, "imported_avatar_probe_batch.json"))
                    .Where(File.Exists)
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }

            if (reportFile == null)
            {
                return null;
            }

            var newestAssetTime = assetFiles.Length == 0
                ? DateTime.MinValue
                : assetFiles.Max(file => file.LastWriteTimeUtc);
            if (reportFile.LastWriteTimeUtc < newestAssetTime)
            {
                return null;
            }

            try
            {
                var root = JObject.Parse(File.ReadAllText(reportFile.FullName));
                if ((int?)root["totalAssets"] != assetFiles.Length)
                {
                    return null;
                }

                var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in root["items"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    if (!string.Equals(S(item, "status"), "ok", StringComparison.OrdinalIgnoreCase)
                        || !((bool?)item["isValid"] ?? false)
                        || !((bool?)item["isHuman"] ?? false))
                    {
                        continue;
                    }

                    var avatarAssetPath = S(item, "avatarAssetPath");
                    var name = Path.GetFileNameWithoutExtension((avatarAssetPath ?? "").Replace('/', Path.DirectorySeparatorChar));
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = S(item, "avatarName");
                    }

                    AddImportedAvatarAssetMapKey(result, name);
                }

                return result;
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<string> BuildUnityAvatarLookupKeys(JObject model, string modelOutput)
        {
            var avatarName = S(model?["avatar"] as JObject, "name");
            var modelName = S(model, "name");
            var output = S(model, "output") ?? modelOutput;
            var fileName = string.IsNullOrWhiteSpace(output) ? null : Path.GetFileName(output);
            var stem = string.IsNullOrWhiteSpace(fileName) ? null : Path.GetFileNameWithoutExtension(fileName);
            return new[] { avatarName, modelName, stem, fileName }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static void AddStringMap(Dictionary<string, string> target, JObject map)
        {
            if (target == null || map == null)
            {
                return;
            }

            foreach (var property in map.Properties())
            {
                var value = property.Value?.ToString();
                if (!string.IsNullOrWhiteSpace(property.Name) && !string.IsNullOrWhiteSpace(value))
                {
                    target[property.Name] = NormalizeUnityAssetPath(value);
                }
            }
        }

        private static void AddImportedAvatarAssetMapKey(Dictionary<string, string> target, string name, string assetPath)
        {
            if (target == null || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(assetPath))
            {
                return;
            }

            target[name] = NormalizeUnityAssetPath(assetPath);
            if (name.EndsWith("_ModelAvatar", StringComparison.OrdinalIgnoreCase))
            {
                target[name[..^"_ModelAvatar".Length]] = NormalizeUnityAssetPath(assetPath);
            }
        }

        private static void AddImportedAvatarAssetMapKey(HashSet<string> target, string name)
        {
            if (target == null || string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            target.Add(name);
            if (name.EndsWith("_ModelAvatar", StringComparison.OrdinalIgnoreCase))
            {
                target.Add(name[..^"_ModelAvatar".Length]);
            }
        }

        private static JObject LoadJsonObject(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
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

        private static string FirstNotEmpty(params string[] values)
            => values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        private static string NormalizeUnityAssetPath(string value)
            => (value ?? string.Empty).Trim().Trim('"').Replace('\\', '/');

        public static Dictionary<long, long[]> FindBaseLayerClipDependencies(
            string inspectPath,
            IReadOnlySet<long> selectedClipPathIds)
        {
            var result = new Dictionary<long, HashSet<long>>();
            if (string.IsNullOrWhiteSpace(inspectPath)
                || !File.Exists(inspectPath)
                || selectedClipPathIds == null
                || selectedClipPathIds.Count == 0)
            {
                return new Dictionary<long, long[]>();
            }

            var contexts = LoadControllerContexts(inspectPath);
            foreach (var item in contexts)
            {
                var selectedClipPathId = item.Key.ClipPathId;
                if (!selectedClipPathIds.Contains(selectedClipPathId))
                {
                    continue;
                }

                var basePathId = (long?)item.Value["baseLayerClip"]?["clip"]?["pathId"];
                if (basePathId == null || basePathId.Value == selectedClipPathId)
                {
                    continue;
                }

                if (!result.TryGetValue(selectedClipPathId, out var baseClips))
                {
                    baseClips = new HashSet<long>();
                    result[selectedClipPathId] = baseClips;
                }
                baseClips.Add(basePathId.Value);
            }

            return result.ToDictionary(
                x => x.Key,
                x => x.Value.OrderBy(v => v).ToArray());
        }

        private static Dictionary<(long ControllerPathId, long ClipPathId), JObject> LoadControllerContexts(string inspectPath)
        {
            var result = new Dictionary<(long, long), JObject>();
            var root = JObject.Parse(File.ReadAllText(inspectPath));
            foreach (var file in root["files"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var source = S(file, "source");
                var serializedFile = S(file, "file");
                foreach (var controller in file["animatorControllers"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    var controllerPathId = (long?)controller["pathId"];
                    if (controllerPathId == null)
                    {
                        continue;
                    }

                    var layerByMachine = (controller["layers"] as JArray)?
                        .OfType<JObject>()
                        .Where(x => (int?)x["stateMachineIndex"] != null)
                        .GroupBy(x => (int)x["stateMachineIndex"])
                        .ToDictionary(x => x.Key, x => x.First())
                        ?? new Dictionary<int, JObject>();
                    var stateMachines = controller["stateMachines"]?.OfType<JObject>().ToList() ?? new List<JObject>();
                    var defaultBaseLayerClip = BuildDefaultBaseLayerClip(stateMachines, layerByMachine);
                    foreach (var stateMachine in stateMachines)
                    {
                        var machineIndex = (int?)stateMachine["machineIndex"] ?? -1;
                        layerByMachine.TryGetValue(machineIndex, out var layer);
                        foreach (var state in stateMachine["states"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                        {
                            var trees = state["blendTrees"]?.OfType<JObject>().ToList() ?? new List<JObject>();
                            for (var treeIndex = 0; treeIndex < trees.Count; treeIndex++)
                            {
                                foreach (var nodePair in ReadClipNodes(trees[treeIndex]))
                                {
                                    var node = nodePair.Value;
                                    var context = BuildStateNodeContext(
                                        source,
                                        serializedFile,
                                        controller,
                                        controllerPathId.Value,
                                        stateMachine,
                                        state,
                                        layer,
                                        treeIndex,
                                        node);

                                    ApplyDefaultBaseLayerClipContext(context, defaultBaseLayerClip, machineIndex, layer);

                                    if (machineIndex == 0)
                                    {
                                        var additionalLayerWarnings = new JArray();
                                        var additionalLayerClips = BuildDefaultAdditionalLayerClips(stateMachines, layerByMachine, machineIndex, state, node.PathId, additionalLayerWarnings);
                                        if (additionalLayerClips.Count > 0)
                                        {
                                            context["additionalLayerClips"] = additionalLayerClips;
                                            context["additionalLayerClipRule"] = "AnimatorController default non-empty layer states whose state path matches the selected motion stem and resolves to exactly one deterministic clip; diagnostic context until original RuntimeAnimatorController is recoverable.";
                                        }
                                        if (additionalLayerWarnings.Count > 0)
                                        {
                                            context["additionalLayerContextWarnings"] = additionalLayerWarnings;
                                        }
                                    }

                                    result[(controllerPathId.Value, node.PathId)] = context;
                                }
                            }

                            if (trees.Count < 2)
                            {
                                continue;
                            }

                            var baseNodes = ReadClipNodes(trees[0]);
                            for (var treeIndex = 1; treeIndex < trees.Count; treeIndex++)
                            {
                                foreach (var nodePair in ReadClipNodes(trees[treeIndex]))
                                {
                                    var node = nodePair.Value;
                                    if (!baseNodes.TryGetValue(node.NodeIndex, out var baseNode))
                                    {
                                        continue;
                                    }

                                    var context = BuildStateNodeContext(
                                        source,
                                        serializedFile,
                                        controller,
                                        controllerPathId.Value,
                                        stateMachine,
                                        state,
                                        layer,
                                        treeIndex,
                                        node);
                                    ApplyDefaultBaseLayerClipContext(context, defaultBaseLayerClip, machineIndex, layer);
                                    context["requestedClip"] = new JObject { ["clip"] = node.Clip };
                                    context["baseLayerClip"] = new JObject
                                    {
                                        ["clip"] = baseNode.Clip,
                                        ["nodeIndex"] = baseNode.NodeIndex,
                                        ["treeIndex"] = 0,
                                    };
                                    context["refreshRule"] = "same AnimatorController state; auxiliary layer clip mapped to layer 0 body clip by identical blend-tree node index";
                                    result[(controllerPathId.Value, node.PathId)] = context;
                                }
                            }
                        }
                    }
                }
            }

            return result;
        }

        private static DefaultBaseLayerClipContext BuildDefaultBaseLayerClip(
            IReadOnlyList<JObject> stateMachines,
            IReadOnlyDictionary<int, JObject> layerByMachine)
        {
            var baseLayer = layerByMachine
                .Select(x => new { MachineIndex = x.Key, Layer = x.Value })
                .OrderBy(x => ReadLayerInt(x.Layer, "index"))
                .FirstOrDefault(x => ReadLayerInt(x.Layer, "index") == 0)
                ?? layerByMachine
                    .Select(x => new { MachineIndex = x.Key, Layer = x.Value })
                    .OrderBy(x => ReadLayerInt(x.Layer, "index"))
                    .FirstOrDefault();
            var baseMachineIndex = baseLayer?.MachineIndex ?? 0;
            var baseMachine = stateMachines?
                .FirstOrDefault(x => ((int?)x["machineIndex"] ?? -1) == baseMachineIndex);
            var defaultStateIndex = (int?)baseMachine?["defaultState"];
            if (baseMachine == null || defaultStateIndex == null || defaultStateIndex.Value < 0)
            {
                return null;
            }

            var state = baseMachine["states"]?.OfType<JObject>()
                .FirstOrDefault(x => ((int?)x["stateIndex"] ?? -1) == defaultStateIndex.Value);
            if (state == null)
            {
                return null;
            }

            if (!TryReadDeterministicDefaultLayerClip(state, long.MinValue, out var node, out _))
            {
                return null;
            }

            var clip = new JObject
            {
                ["clip"] = node.Clip,
                ["nodeIndex"] = node.NodeIndex,
                ["treeIndex"] = 0,
                ["stateMachineIndex"] = baseMachineIndex,
                ["stateIndex"] = state["stateIndex"],
                ["stateName"] = state["name"],
                ["statePath"] = state["fullPath"],
                ["stateFullPath"] = state["fullPath"],
                ["stateSpeed"] = state["speed"],
                ["stateCycleOffset"] = state["cycleOffset"],
                ["stateIKOnFeet"] = state["iKOnFeet"],
                ["stateLoop"] = state["loop"],
                ["stateMirror"] = state["mirror"],
                ["source"] = "animator_controller_inspect.base_layer_default_state",
                ["rule"] = "same AnimatorController base layer default state resolved to exactly one deterministic body clip",
            };
            if (!string.IsNullOrWhiteSpace(node.SelectionRule))
            {
                clip["selectionRule"] = node.SelectionRule;
                clip["selectionParameter"] = node.SelectionParameter;
                clip["selectionValue"] = node.SelectionValue;
                clip["selectionThreshold"] = node.SelectionThreshold;
            }

            return new DefaultBaseLayerClipContext(baseMachineIndex, clip);
        }

        private static void ApplyDefaultBaseLayerClipContext(
            JObject context,
            DefaultBaseLayerClipContext defaultBaseLayerClip,
            int machineIndex,
            JObject layer)
        {
            if (context == null || defaultBaseLayerClip == null || machineIndex == defaultBaseLayerClip.MachineIndex)
            {
                return;
            }

            var layerIndex = ReadLayerInt(layer, "index", machineIndex == defaultBaseLayerClip.MachineIndex ? 0 : 1);
            if (layerIndex <= 0)
            {
                return;
            }

            context["baseLayerClip"] = defaultBaseLayerClip.Clip.DeepClone();
            context["baseLayerClipRule"] = "non-base AnimatorController layer mapped to deterministic base layer default-state body clip; diagnostic until runtime controller state/parameters are fully recovered";
        }

        private static IEnumerable<long> ReadControllerPathIdsFromRelationEvidence(JObject raw)
        {
            var pathId = (long?)raw?["relationEvidence"]?["controllerPathId"];
            if (pathId != null)
            {
                yield return pathId.Value;
            }
        }

        private static JObject BuildStateNodeContext(
            string source,
            string serializedFile,
            JObject controller,
            long controllerPathId,
            JObject stateMachine,
            JObject state,
            JObject layer,
            int treeIndex,
            ClipNode node)
        {
            var context = new JObject
            {
                ["source"] = source,
                ["controllerName"] = S(controller, "name"),
                ["controllerPathId"] = controllerPathId,
                ["controllerFile"] = serializedFile,
                ["stateMachineIndex"] = stateMachine["machineIndex"],
                ["stateIndex"] = state["stateIndex"],
                ["stateName"] = state["name"],
                ["statePath"] = state["fullPath"],
                ["stateFullPath"] = state["fullPath"],
                ["stateSpeed"] = state["speed"],
                ["stateCycleOffset"] = state["cycleOffset"],
                ["stateIKOnFeet"] = state["iKOnFeet"],
                ["stateLoop"] = state["loop"],
                ["stateMirror"] = state["mirror"],
                ["stateParameters"] = state["stateParameters"]?.DeepClone(),
                ["blendTreeIndex"] = treeIndex,
                ["nodeIndex"] = node.NodeIndex,
                ["nodeClipId"] = node.ClipSlot,
                ["nodeClipIndex"] = node.ClipSlot,
                ["nodeDuration"] = node.Duration,
                ["nodeCycleOffset"] = node.CycleOffset,
                ["nodeMirror"] = node.Mirror,
                ["clip"] = node.Clip,
                ["refreshRule"] = "AnimatorController explicit state/blend-tree node context",
            };

            var layerContext = BuildLayerContext(layer);
            if (layerContext != null)
            {
                context["layers"] = new JArray(layerContext);
            }

            return context;
        }

        private static JArray BuildDefaultAdditionalLayerClips(
            IReadOnlyList<JObject> stateMachines,
            IReadOnlyDictionary<int, JObject> layerByMachine,
            int baseMachineIndex,
            JObject baseState,
            long baseClipPathId,
            JArray warnings)
        {
            var result = new JArray();
            var motionStem = ReadMotionStem(baseState);
            if (string.IsNullOrWhiteSpace(motionStem))
            {
                return result;
            }

            var machinesByIndex = stateMachines
                .Where(x => (int?)x["machineIndex"] != null)
                .ToDictionary(x => (int)x["machineIndex"], x => x);
            foreach (var pair in layerByMachine.OrderBy(x => ReadLayerInt(x.Value, "index")))
            {
                var machineIndex = pair.Key;
                if (machineIndex == baseMachineIndex || !machinesByIndex.TryGetValue(machineIndex, out var machine))
                {
                    continue;
                }

                var defaultStateIndex = (int?)machine["defaultState"];
                if (defaultStateIndex == null || defaultStateIndex.Value < 0)
                {
                    continue;
                }

                var state = machine["states"]?.OfType<JObject>()
                    .FirstOrDefault(x => ((int?)x["stateIndex"] ?? -1) == defaultStateIndex.Value);
                if (state == null || !StateMentionsMotionStem(state, motionStem))
                {
                    continue;
                }

                if (!TryReadDeterministicDefaultLayerClip(state, baseClipPathId, out var node, out var skipReason))
                {
                    warnings?.Add(new JObject
                    {
                        ["layerIndex"] = ReadLayerInt(pair.Value, "index"),
                        ["stateFullPath"] = state["fullPath"],
                        ["reason"] = skipReason,
                        ["rule"] = "skip non-deterministic default layer; original AnimatorController state/blend parameters are required",
                    });
                    continue;
                }

                var layerClip = BuildAdditionalLayerClip(pair.Value, state, node);
                if (layerClip != null)
                {
                    result.Add(layerClip);
                }
            }

            return result;
        }

        private static bool TryReadDeterministicDefaultLayerClip(JObject state, long baseClipPathId, out ClipNode clipNode, out string skipReason)
        {
            clipNode = null;
            skipReason = null;
            var trees = state?["blendTrees"]?.OfType<JObject>()?.ToList() ?? new List<JObject>();
            if (trees.Count == 0)
            {
                skipReason = "default state has no blend tree clip";
                return false;
            }

            var clips = new List<ClipNode>();
            var hasParameterizedNode = false;
            foreach (var tree in trees)
            {
                foreach (var node in tree["nodes"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    if (HasBlendTreeParameter(node))
                    {
                        hasParameterizedNode = true;
                    }
                }

                clips.AddRange(ReadClipNodes(tree).Values.Where(x => x.PathId != baseClipPathId));
            }

            if (clips.Count > 1 && TrySelectClipFromStateParameters(state, trees, baseClipPathId, out var selectedNode, out var selectReason))
            {
                clipNode = selectedNode;
                return true;
            }

            var uniqueClips = clips
                .GroupBy(x => x.PathId)
                .Select(x => x.First())
                .ToList();
            if (uniqueClips.Count != 1)
            {
                skipReason = $"default state resolves to {uniqueClips.Count} clip(s); BlendTree parameters or weights are required";
                return false;
            }

            if (hasParameterizedNode && clips.Count > 1)
            {
                skipReason = "default state contains parameterized BlendTree nodes; cannot assign all child clips weight 1";
                return false;
            }

            clipNode = uniqueClips[0];
            return true;
        }

        private static bool TrySelectClipFromStateParameters(
            JObject state,
            IReadOnlyList<JObject> trees,
            long baseClipPathId,
            out ClipNode clipNode,
            out string reason)
        {
            clipNode = null;
            reason = null;
            var parameterValues = ReadStateParameterValues(state);
            if (parameterValues.Count == 0)
            {
                reason = "state has no serialized parameter defaults";
                return false;
            }

            ClipNode selected = null;
            foreach (var tree in trees ?? Array.Empty<JObject>())
            {
                var nodes = tree["nodes"]?.OfType<JObject>()?.ToList() ?? new List<JObject>();
                var clipNodes = ReadClipNodes(tree);
                foreach (var node in nodes)
                {
                    var parameterName = S(node, "blendEvent");
                    if (string.IsNullOrWhiteSpace(parameterName)
                        || !parameterValues.TryGetValue(parameterName, out var parameterValue))
                    {
                        continue;
                    }

                    var children = node["childIndices"]?.Values<int>()?.ToArray() ?? Array.Empty<int>();
                    var thresholds = ReadThresholds(node);
                    if (children.Length == 0 || thresholds.Length == 0 || children.Length != thresholds.Length)
                    {
                        continue;
                    }

                    var bestIndex = SelectClosestThresholdIndex(thresholds, parameterValue);
                    if (bestIndex < 0 || bestIndex >= children.Length)
                    {
                        continue;
                    }

                    if (!clipNodes.TryGetValue(children[bestIndex], out var candidate) || candidate.PathId == baseClipPathId)
                    {
                        continue;
                    }

                    if (selected != null && selected.PathId != candidate.PathId)
                    {
                        reason = $"state parameters select multiple clips; first={selected.PathId}, next={candidate.PathId}";
                        return false;
                    }

                    selected = candidate with
                    {
                        SelectionRule = "state_parameter_threshold",
                        SelectionParameter = parameterName,
                        SelectionValue = parameterValue,
                        SelectionThreshold = thresholds[bestIndex],
                    };
                }
            }

            if (selected == null)
            {
                reason = "state parameters did not select a clip child";
                return false;
            }

            clipNode = selected;
            return true;
        }

        private static JObject BuildAdditionalLayerClip(JObject layer, JObject state, ClipNode node)
        {
            if (node.Clip == null)
            {
                return null;
            }

            var result = new JObject
            {
                ["name"] = node.Clip["name"],
                ["fileId"] = node.Clip["fileId"],
                ["file"] = node.Clip["file"],
                ["pathId"] = node.Clip["pathId"],
                ["layerIndex"] = ReadLayerInt(layer, "index"),
                ["layerBlendingMode"] = ReadLayerInt(layer, "blendingMode", StateLooksAdditive(state) ? 1 : 0),
                ["layerDefaultWeight"] = ReadLayerFloat(layer, "defaultWeight", 1f),
                ["layerBodyMask"] = layer?["bodyMask"]?.DeepClone(),
                ["layerSkeletonMask"] = layer?["skeletonMask"]?.DeepClone(),
                ["stateName"] = state["name"],
                ["statePath"] = state["fullPath"],
                ["stateFullPath"] = state["fullPath"],
                ["stateSpeed"] = state["speed"],
                ["stateCycleOffset"] = state["cycleOffset"],
                ["stateIKOnFeet"] = state["iKOnFeet"],
                ["stateLoop"] = state["loop"],
                ["stateMirror"] = state["mirror"],
                ["nodeCycleOffset"] = node.CycleOffset,
                ["nodeMirror"] = node.Mirror,
                ["source"] = "animator_controller_inspect.default_layer_state",
            };
            if (!string.IsNullOrWhiteSpace(node.SelectionRule))
            {
                result["selectionRule"] = node.SelectionRule;
                result["selectionParameter"] = node.SelectionParameter;
                result["selectionValue"] = node.SelectionValue;
                result["selectionThreshold"] = node.SelectionThreshold;
            }
            return result;
        }

        private static JObject BuildLayerContext(JObject layer)
        {
            if (layer == null)
            {
                return null;
            }

            return new JObject
            {
                ["layerIndex"] = layer["index"],
                ["layerStateMachineIndex"] = layer["stateMachineIndex"],
                ["layerStateMachineMotionSetIndex"] = layer["stateMachineMotionSetIndex"],
                ["layerBinding"] = layer["binding"],
                ["layerBlendingMode"] = layer["blendingMode"],
                ["layerDefaultWeight"] = layer["defaultWeight"],
                ["layerIKPass"] = layer["iKPass"],
                ["layerSyncedLayerAffectsTiming"] = layer["syncedLayerAffectsTiming"],
                ["layerBodyMask"] = layer["bodyMask"]?.DeepClone(),
                ["layerSkeletonMask"] = layer["skeletonMask"]?.DeepClone(),
            };
        }

        private static string ReadMotionStem(JObject state)
        {
            var stateText = string.Join(" ", (string)state?["name"], (string)state?["fullPath"]);
            foreach (var stem in new[] { "Idle", "Walk", "Run", "Sprint", "Jump", "Turn", "Attack", "Skill", "Move", "Bump" })
            {
                if (!string.IsNullOrWhiteSpace(stateText)
                    && stateText.IndexOf(stem, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return stem;
                }
            }

            return null;
        }

        private static bool StateMentionsMotionStem(JObject state, string motionStem)
        {
            var stateText = string.Join(" ", (string)state?["name"], (string)state?["fullPath"]);
            return !string.IsNullOrWhiteSpace(stateText)
                && stateText.IndexOf("empty", StringComparison.OrdinalIgnoreCase) < 0
                && stateText.IndexOf(motionStem, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool StateLooksAdditive(JObject state)
        {
            var stateText = string.Join(" ", (string)state?["name"], (string)state?["fullPath"]);
            return !string.IsNullOrWhiteSpace(stateText)
                && stateText.IndexOf("Add", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int ReadLayerInt(JObject layer, string name, int defaultValue = 0)
        {
            var token = layer?[name];
            return token != null && token.Type != JTokenType.Null ? token.Value<int>() : defaultValue;
        }

        private static float ReadLayerFloat(JObject layer, string name, float defaultValue = 0f)
        {
            var token = layer?[name];
            return token != null && token.Type != JTokenType.Null ? token.Value<float>() : defaultValue;
        }

        private static Dictionary<string, double> ReadStateParameterValues(JObject state)
        {
            var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var parameter in state?["stateParameters"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var name = S(parameter, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                result[name] = (double?)parameter["value"] ?? 0d;
            }

            return result;
        }

        private static double[] ReadThresholds(JObject node)
        {
            var thresholds = node?["childThresholds"]?.Values<double>()?.ToArray() ?? Array.Empty<double>();
            if (thresholds.Length > 0)
            {
                return thresholds;
            }

            return node?["blend1dChildThresholds"]?.Values<double>()?.ToArray() ?? Array.Empty<double>();
        }

        private static int SelectClosestThresholdIndex(IReadOnlyList<double> thresholds, double value)
        {
            if (thresholds == null || thresholds.Count == 0)
            {
                return -1;
            }

            var bestIndex = 0;
            var bestDistance = Math.Abs(thresholds[0] - value);
            for (var i = 1; i < thresholds.Count; i++)
            {
                var distance = Math.Abs(thresholds[i] - value);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        private static Dictionary<int, ClipNode> ReadClipNodes(JObject tree)
        {
            var result = new Dictionary<int, ClipNode>();
            foreach (var node in tree["nodes"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var clip = node["clipPPtr"] as JObject;
                var pathId = (long?)clip?["pathId"];
                var nodeIndex = (int?)node["nodeIndex"];
                if (pathId == null || nodeIndex == null)
                {
                    continue;
                }

                result[nodeIndex.Value] = new ClipNode(
                    nodeIndex.Value,
                    pathId.Value,
                    (int?)node["clipSlot"] ?? (int?)node["clipIndex"] ?? -1,
                    (double?)node["duration"] ?? 0,
                    (double?)node["cycleOffset"] ?? 0,
                    (bool?)node["mirror"] ?? false,
                    (JObject)clip.DeepClone(),
                    null,
                    null,
                    0,
                    0);
            }

            return result;
        }

        private static bool HasBlendTreeParameter(JObject node)
        {
            return !string.IsNullOrWhiteSpace(S(node, "blendEvent"))
                || !string.IsNullOrWhiteSpace(S(node, "blendEventY"))
                || HasNamedTosArray(node, "directChildBlendEvents")
                || HasNamedTosArray(node, "directChildPoseTimeEvents")
                || HasNamedTosArray(node, "sequenceChildBlendEvents")
                || HasNamedTosArray(node, "sequenceChildPoseTimeEvents")
                || node?["childThresholds"] is JArray thresholds && thresholds.Count > 0;
        }

        private static bool HasNamedTosArray(JObject node, string name)
        {
            return node?[name] is JArray values
                && values.OfType<JObject>().Any(x => !string.IsNullOrWhiteSpace(S(x, "name")));
        }

        private static string S(JObject obj, string name)
        {
            var value = obj?[name];
            return value == null || value.Type == JTokenType.Null ? null : value.ToString();
        }

        private sealed record CandidateRow(long Id, string ModelOutput, string AnimationOutput, JObject RawJson, JObject Model, JObject Animation);

        private sealed record ClipNode(
            int NodeIndex,
            long PathId,
            int ClipSlot,
            double Duration,
            double CycleOffset,
            bool Mirror,
            JObject Clip,
            string SelectionRule,
            string SelectionParameter,
            double SelectionValue,
            double SelectionThreshold);

        private sealed record DefaultBaseLayerClipContext(int MachineIndex, JObject Clip);

        private sealed record AvatarResolution(bool HasProductionAvatar, string AvatarAsset, string MatchKey);

        private sealed class ModelControllerResolver
        {
            private readonly string _sourceIndex;
            private readonly Dictionary<(string Source, long PathId), long[]> _cache = new();

            public ModelControllerResolver(string sourceIndex)
            {
                _sourceIndex = sourceIndex;
            }

            public IReadOnlyList<long> FindControllerPathIds(string modelSource, long modelPathId)
            {
                var key = (NormalizeSource(modelSource), modelPathId);
                if (_cache.TryGetValue(key, out var cached))
                {
                    return cached;
                }

                var directControllers = new List<ControllerRef>();
                using var connection = new SqliteConnection($"Data Source={_sourceIndex};Mode=ReadOnly");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT to_file, to_path_id, from_source
FROM source_relations
WHERE relation='animator.controller'
  AND from_path_id=$pathId;";
                command.Parameters.AddWithValue("$pathId", modelPathId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var source = reader.IsDBNull(2) ? "" : NormalizeSource(reader.GetString(2));
                    if (!SourceMatches(key.Item1, source))
                    {
                        continue;
                    }

                    directControllers.Add(new ControllerRef(
                        reader.IsDBNull(0) ? "" : reader.GetString(0),
                        reader.GetInt64(1)));
                }

                var result = new List<long>();
                var visited = new HashSet<(string File, long PathId)>();
                foreach (var controller in directControllers)
                {
                    AddControllerAndBaseControllers(connection, controller, result, visited, 0);
                }

                cached = result.Distinct().ToArray();
                _cache[key] = cached;
                return cached;
            }

            private static void AddControllerAndBaseControllers(
                SqliteConnection connection,
                ControllerRef controller,
                List<long> result,
                HashSet<(string File, long PathId)> visited,
                int depth)
            {
                if (depth > 16 || !visited.Add((NormalizeFile(controller.File), controller.PathId)))
                {
                    return;
                }

                result.Add(controller.PathId);
                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT to_file, to_path_id
FROM source_relations
WHERE relation='animatorOverrideController.baseController'
  AND from_path_id=$pathId
  AND ($file='' OR lower(from_file)=lower($file));";
                command.Parameters.AddWithValue("$pathId", controller.PathId);
                command.Parameters.AddWithValue("$file", NormalizeFile(controller.File));
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    AddControllerAndBaseControllers(
                        connection,
                        new ControllerRef(reader.IsDBNull(0) ? "" : reader.GetString(0), reader.GetInt64(1)),
                        result,
                        visited,
                        depth + 1);
                }
            }

            private static bool SourceMatches(string modelSource, string relationSource)
            {
                if (string.IsNullOrWhiteSpace(modelSource) || string.IsNullOrWhiteSpace(relationSource))
                {
                    return true;
                }

                return string.Equals(modelSource, relationSource, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFileName(modelSource), Path.GetFileName(relationSource), StringComparison.OrdinalIgnoreCase)
                    || modelSource.EndsWith("/" + relationSource, StringComparison.OrdinalIgnoreCase)
                    || relationSource.EndsWith("/" + modelSource, StringComparison.OrdinalIgnoreCase);
            }

            private static string NormalizeSource(string value)
                => (value ?? string.Empty).Replace('\\', '/').Trim();

            private static string NormalizeFile(string value)
                => (value ?? string.Empty).Trim().ToUpperInvariant();

            private sealed record ControllerRef(string File, long PathId);
        }
    }
}
