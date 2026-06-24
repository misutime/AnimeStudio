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
    internal static class AvatarAssetRecoveryExporter
    {
        private const string ImportedAvatarFolder = "Assets/AnimeStudioBake/ImportedAvatar";

        public static string Recover(
            string libraryRoot,
            string unityProject,
            string unityEditor,
            Game game,
            string unityVersion,
            string sourceRootOverride,
            string selector,
            int limit,
            bool force,
            bool runProbe,
            string explicitIndexPath = null,
            string sourceIndexPath = null)
        {
            if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
            {
                throw new DirectoryNotFoundException($"Library root not found: {libraryRoot}");
            }

            if (string.IsNullOrWhiteSpace(unityProject) || !Directory.Exists(unityProject))
            {
                throw new DirectoryNotFoundException("--unity_project is required and must point to a Unity bake project.");
            }

            if (game == null)
            {
                throw new ArgumentNullException(nameof(game), "--game is required so source bundles are loaded with the same Unity/game rules as export.");
            }

            var libraryPath = Path.GetFullPath(libraryRoot);
            var dbPath = string.IsNullOrWhiteSpace(explicitIndexPath)
                ? Path.Combine(libraryPath, "library_index.db")
                : Path.GetFullPath(explicitIndexPath);
            if (!File.Exists(dbPath))
            {
                throw new FileNotFoundException($"library_index.db not found: {dbPath}", dbPath);
            }

            var importedRoot = Path.Combine(Path.GetFullPath(unityProject), ImportedAvatarFolder.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(importedRoot);
            SQLitePCL.Batteries_V2.Init();

            var resolvedSourceIndex = game.Type.IsArknightsEndfieldGroup()
                ? ResolveSourceIndexPath(libraryPath, sourceIndexPath)
                : null;
            var effectiveSourceRoot = !string.IsNullOrWhiteSpace(sourceRootOverride)
                ? sourceRootOverride
                : TryReadSourceRootFromIndex(resolvedSourceIndex);
            var requests = ReadMissingAvatarRequests(dbPath, importedRoot, selector, limit, force, resolvedSourceIndex);
            var started = DateTimeOffset.UtcNow;
            var reportDir = Path.Combine(libraryPath, $"ImportedAvatarRecovery_{started:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(reportDir);
            var reportPath = Path.Combine(reportDir, "imported_avatar_recovery.json");

            Logger.Info($"Avatar asset recovery selected {requests.Count} unique Avatar object(s).");
            if (requests.Count == 0)
            {
                var existingProbePath = runProbe ? TryRunUnityProbe(libraryPath, unityProject, unityEditor, reportDir) : null;
                var refreshedCandidates = RefreshLibraryIndexImportedAvatarBakeReadiness(dbPath, libraryPath, unityProject, existingProbePath);
                var emptyReport = new JObject
                {
                    ["status"] = "nothing_to_recover",
                    ["libraryRoot"] = libraryPath,
                    ["unityProject"] = Path.GetFullPath(unityProject),
                    ["createdUtc"] = started.ToString("O", CultureInfo.InvariantCulture),
                    ["probeReport"] = existingProbePath ?? string.Empty,
                    ["refreshedCandidateRows"] = refreshedCandidates,
                };
                File.WriteAllText(reportPath, emptyReport.ToString(Formatting.Indented));
                return reportPath;
            }

            var results = new JArray();
            var endfieldLoadHints = EndfieldAvatarLoadHints.Empty;
            if (game.Type.IsArknightsEndfieldGroup())
            {
                endfieldLoadHints = LoadEndfieldAvatarLoadHints(resolvedSourceIndex, requests);
            }

            foreach (var request in requests.Where(x => !string.IsNullOrWhiteSpace(x.ExistingUnityAssetPath)))
            {
                results.Add(WriteResult(
                    request,
                    "existing_imported",
                    request.ExistingUnityAssetPath,
                    "Imported Avatar asset already exists; refreshed aliases and probe-based readiness without rewriting it."));
            }

            var bySource = requests
                .Where(x => string.IsNullOrWhiteSpace(x.ExistingUnityAssetPath))
                .GroupBy(x => ResolveSourcePath(x.Source, effectiveSourceRoot), StringComparer.OrdinalIgnoreCase);
            foreach (var sourceGroup in bySource)
            {
                var sourcePath = sourceGroup.Key;
                var sourceRequests = sourceGroup.ToArray();
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                {
                    foreach (var request in sourceRequests)
                    {
                        results.Add(WriteResult(request, "missing_source_file", null, $"Source file not found: {sourcePath ?? request.Source}"));
                    }
                    continue;
                }

                var manager = new AssetsManager
                {
                    Game = game,
                    SpecifyUnityVersion = unityVersion,
                    ResolveDependencies = false,
                    LoadSerializedFileExternals = false,
                    SkipProcess = false,
                    StoreUnparsedObjects = false,
                    ObjectParseFilter = type => type == ClassIDType.Avatar,
                    Silent = true,
                };
                var endfieldInnerFiles = endfieldLoadHints.GetInnerFiles(sourceRequests);
                if (endfieldInnerFiles.Count > 0)
                {
                    manager.EndfieldVfsInnerFileFilter = BuildExactEndfieldVfsInnerFileFilter(endfieldInnerFiles);
                    manager.EndfieldVfsInnerFileLimit = 0;
                    manager.EndfieldVfsInnerFileFilterIsDiagnostic = false;
                    Logger.Info($"Endfield Avatar recovery narrowed {Path.GetFileName(sourcePath)} to {endfieldInnerFiles.Count} inner UnityFS file(s) from source index.");
                }

                try
                {
                    Logger.Info($"Recovering Avatar asset(s) from {sourcePath}: {sourceRequests.Length}");
                    manager.LoadFiles(sourcePath);
                    foreach (var request in sourceRequests)
                    {
                        try
                        {
                            var avatar = FindAvatar(manager, request.PathId);
                            if (avatar == null)
                            {
                                results.Add(WriteResult(request, "avatar_object_not_found", null, $"Avatar pathId not found in loaded source: {request.PathId}"));
                                continue;
                            }

                            var assetPath = Path.Combine(importedRoot, Exporter.FixFileName(request.AvatarName) + ".asset");
                            AvatarAssetYamlExporter.WriteAvatarAsset(avatar, assetPath);
                            results.Add(WriteResult(request, "recovered", ToUnityAssetPath(unityProject, assetPath), "Recovered original UnityEngine.Avatar asset from source/pathId."));
                        }
                        catch (Exception e)
                        {
                            results.Add(WriteResult(request, "failed", null, e.GetType().Name + ": " + e.Message));
                        }
                    }
                }
                catch (Exception e)
                {
                    foreach (var request in sourceRequests)
                    {
                        results.Add(WriteResult(request, "source_load_failed", null, e.GetType().Name + ": " + e.Message));
                    }
                }
                finally
                {
                    manager.Clear();
                }
            }

            var probePath = runProbe ? TryRunUnityProbe(libraryPath, unityProject, unityEditor, reportDir) : null;
            var probeValidNames = LoadProbeValidAvatarNames(probePath);
            var quarantined = QuarantineInvalidRecoveredAvatars(unityProject, probePath, results, probeValidNames);
            if (quarantined > 0 && runProbe)
            {
                Logger.Warning($"Quarantined {quarantined} non-Humanoid or invalid imported Avatar asset(s); rerunning Unity probe so Browser sees a clean readiness report.");
                probePath = TryRunUnityProbe(libraryPath, unityProject, unityEditor, reportDir);
                probeValidNames = LoadProbeValidAvatarNames(probePath);
            }
            UpdateLocalBrowserSettings(libraryPath, unityProject, unityEditor, results, probeValidNames);
            var refreshedCandidateRows = RefreshLibraryIndexImportedAvatarBakeReadiness(dbPath, libraryPath, unityProject, probePath);

            var recovered = results.Count(x => IsRecoveredOrExistingStatus((string)x["status"]));
            var quarantinedInvalid = results.Count(x => string.Equals((string)x["status"], "quarantined_invalid_avatar", StringComparison.OrdinalIgnoreCase));
            var quarantinedNonHumanoid = results.Count(x => string.Equals((string)x["status"], "quarantined_non_humanoid_avatar", StringComparison.OrdinalIgnoreCase));
            var trusted = probeValidNames == null
                ? 0
                : results.Count(x => IsRecoveredOrExistingStatus((string)x["status"])
                    && probeValidNames.Contains((string)x["avatarName"]));
            var report = new JObject
            {
                ["status"] = runProbe
                    ? trusted == requests.Count ? "ok" : trusted > 0 ? "partial_probe_valid" : "needs_better_avatar_source"
                    : recovered == requests.Count ? "recovered_unverified" : recovered > 0 ? "partial_recovered_unverified" : "failed",
                ["libraryRoot"] = libraryPath,
                ["unityProject"] = Path.GetFullPath(unityProject),
                ["unityEditor"] = unityEditor ?? string.Empty,
                ["createdUtc"] = started.ToString("O", CultureInfo.InvariantCulture),
                ["selectedAvatarObjects"] = requests.Count,
                ["existingImportedAvatarObjects"] = results.Count(x => string.Equals((string)x["status"], "existing_imported", StringComparison.OrdinalIgnoreCase)),
                ["recoveredAvatarObjects"] = recovered,
                ["trustedHumanAvatarObjects"] = trusted,
                ["quarantinedAvatarObjects"] = quarantined,
                ["quarantinedInvalidAvatarObjects"] = quarantinedInvalid,
                ["quarantinedNonHumanoidAvatarObjects"] = quarantinedNonHumanoid,
                ["failedAvatarObjects"] = requests.Count - recovered,
                ["probeReport"] = probePath ?? string.Empty,
                ["refreshedCandidateRows"] = refreshedCandidateRows,
                ["rule"] = "从 library_index.db 的 Unity avatar source/pathId 精确恢复原始 UnityEngine.Avatar asset；Endfield genericAvatar 只允许同源、同基础名、带 HumanDescription 的 Avatar 精确配对；不使用骨骼数量、名称或姿态推断动画关系。",
                ["results"] = results,
            };
            File.WriteAllText(reportPath, report.ToString(Formatting.Indented));
            Logger.Info($"Imported Avatar recovery report written: {reportPath}");
            return reportPath;
        }

        private static List<AvatarRecoveryRequest> ReadMissingAvatarRequests(string dbPath, string importedRoot, string selector, int limit, bool force, string sourceIndexPath)
        {
            var requests = new Dictionary<string, AvatarRecoveryRequest>(StringComparer.OrdinalIgnoreCase);
            var humanoidAvatarPairCache = new Dictionary<string, HumanoidAvatarPair>(StringComparer.OrdinalIgnoreCase);
            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT name, output, raw_json
FROM assets
WHERE kind LIKE 'Model%'
  AND raw_json LIKE '%""avatar""%'";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var modelName = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                var modelOutput = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var raw = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                JObject json;
                try
                {
                    json = JObject.Parse(raw);
                }
                catch
                {
                    continue;
                }

                var avatar = json["avatar"] as JObject;
                var avatarName = (string)avatar?["name"];
                var source = (string)avatar?["source"];
                var pathId = (long?)avatar?["pathId"];
                if (string.IsNullOrWhiteSpace(avatarName) || string.IsNullOrWhiteSpace(source) || pathId == null)
                {
                    continue;
                }

                if (!MatchesSelector(selector, modelName, modelOutput, avatarName))
                {
                    continue;
                }

                var selectedName = avatarName;
                var selectedSource = source;
                var selectedPathId = pathId.Value;
                var recoveryReason = "model_avatar_reference";
                var sourceAvatarName = avatarName;
                var aliasKeys = new List<string>();

                // Endfield 的模型常引用 *_genericAvatar；同一个源包里还有真正带 HumanDescription 的 Avatar。
                // 这里只接受“同源 + 去掉 _genericAvatar 后精确同名 + human/skeleton 元数据完整”的配对，
                // 让 UnityBakeAccelerated 获得生产 Avatar oracle，同时保留原 generic 名作为匹配 alias。
                if (TryFindHumanoidAvatarPair(sourceIndexPath, source, avatarName, humanoidAvatarPairCache, out var humanoidPair))
                {
                    selectedName = humanoidPair.Name;
                    selectedSource = humanoidPair.Source;
                    selectedPathId = humanoidPair.PathId;
                    recoveryReason = "genericAvatarHumanoidPair";
                    sourceAvatarName = avatarName;
                    aliasKeys.Add(avatarName);
                }

                var targetAsset = Path.Combine(importedRoot, Exporter.FixFileName(selectedName) + ".asset");
                if (!force && File.Exists(targetAsset))
                {
                    var existingRequest = AddOrGetRequest(requests, selectedName, selectedSource, selectedPathId, recoveryReason, sourceAvatarName);
                    existingRequest.ExistingUnityAssetPath = ToUnityAssetPath(
                        Path.GetFullPath(Path.Combine(importedRoot, "..", "..", "..")),
                        targetAsset);
                    foreach (var aliasKey in aliasKeys)
                    {
                        existingRequest.AliasKeys.Add(aliasKey);
                    }
                    existingRequest.ModelExamples.Add(new JObject
                    {
                        ["name"] = modelName,
                        ["output"] = modelOutput,
                    });
                    continue;
                }

                var invalidAsset = Path.Combine(Path.GetDirectoryName(importedRoot) ?? importedRoot, "InvalidImportedAvatar", Exporter.FixFileName(selectedName) + ".asset");
                if (!force && File.Exists(invalidAsset))
                {
                    continue;
                }

                var request = AddOrGetRequest(requests, selectedName, selectedSource, selectedPathId, recoveryReason, sourceAvatarName);
                foreach (var aliasKey in aliasKeys)
                {
                    request.AliasKeys.Add(aliasKey);
                }
                request.ModelExamples.Add(new JObject
                {
                    ["name"] = modelName,
                    ["output"] = modelOutput,
                });
            }

            ReadSharedAvatarBridgeRequests(connection, requests, importedRoot, selector, force, sourceIndexPath);

            var ordered = requests.Values
                .OrderByDescending(x => x.ModelExamples.Count)
                .ThenBy(x => x.AvatarName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (limit > 0)
            {
                ordered = ordered.Take(limit).ToList();
            }
            return ordered;
        }

        private static void ReadSharedAvatarBridgeRequests(
            SqliteConnection connection,
            Dictionary<string, AvatarRecoveryRequest> requests,
            string importedRoot,
            string selector,
            bool force,
            string sourceIndexPath)
        {
            if (string.IsNullOrWhiteSpace(sourceIndexPath) || !File.Exists(sourceIndexPath))
            {
                return;
            }

            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT COALESCE(m.name, ''), c.model_output, c.raw_json
FROM model_animation_candidates c
LEFT JOIN assets m ON m.kind='Model' AND m.output=c.model_output
WHERE c.relation_source='explicit'
  AND COALESCE(json_extract(c.raw_json, '$.relation'), '')='sharedAvatarController'
  AND json_type(c.raw_json, '$.relationEvidence.avatarPathId') IS NOT NULL;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var modelName = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                var modelOutput = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var raw = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                JObject json;
                try
                {
                    json = JObject.Parse(raw);
                }
                catch
                {
                    continue;
                }

                var evidence = json["relationEvidence"] as JObject;
                var avatarName = (string)evidence?["avatarName"];
                var avatarFile = (string)evidence?["avatarFile"];
                var avatarPathId = (long?)evidence?["avatarPathId"];
                if (string.IsNullOrWhiteSpace(avatarName)
                    || string.IsNullOrWhiteSpace(avatarFile)
                    || avatarPathId == null
                    || !MatchesSelector(selector, modelName, modelOutput, avatarName))
                {
                    continue;
                }

                var avatarSource = ResolveAvatarSourceFromSourceIndex(sourceIndexPath, avatarFile, avatarPathId.Value);
                if (string.IsNullOrWhiteSpace(avatarSource))
                {
                    continue;
                }

                var targetAsset = Path.Combine(importedRoot, Exporter.FixFileName(avatarName) + ".asset");
                var request = AddOrGetRequest(
                    requests,
                    avatarName,
                    avatarSource,
                    avatarPathId.Value,
                    "sharedAvatarControllerAvatarBridge",
                    avatarName);

                if (!force && File.Exists(targetAsset))
                {
                    request.ExistingUnityAssetPath = ToUnityAssetPath(
                        Path.GetFullPath(Path.Combine(importedRoot, "..", "..", "..")),
                        targetAsset);
                }

                var invalidAsset = Path.Combine(Path.GetDirectoryName(importedRoot) ?? importedRoot, "InvalidImportedAvatar", Exporter.FixFileName(avatarName) + ".asset");
                if (!force && File.Exists(invalidAsset))
                {
                    continue;
                }

                request.ModelExamples.Add(new JObject
                {
                    ["name"] = modelName,
                    ["output"] = modelOutput,
                    ["relation"] = "sharedAvatarController",
                    ["controller"] = evidence?["controllerName"],
                    ["animatorModel"] = evidence?["animatorModelName"],
                });
            }
        }

        private static string ResolveAvatarSourceFromSourceIndex(string sourceIndexPath, string avatarFile, long avatarPathId)
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={Path.GetFullPath(sourceIndexPath)};Mode=ReadOnly");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT source_path
FROM source_objects
WHERE type='Avatar'
  AND path_id=$pathId
  AND lower(serialized_file)=lower($file)
LIMIT 2;";
                command.Parameters.AddWithValue("$pathId", avatarPathId);
                command.Parameters.AddWithValue("$file", avatarFile ?? string.Empty);
                using var reader = command.ExecuteReader();
                string found = null;
                while (reader.Read())
                {
                    var source = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    if (string.IsNullOrWhiteSpace(source))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(found)
                        && !string.Equals(found, source, StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }

                    found = source;
                }

                return found;
            }
            catch (Exception e) when (e is IOException || e is SqliteException)
            {
                Logger.Warning($"Unable to resolve shared Avatar bridge source {avatarFile}#{avatarPathId}. {e.GetType().Name}: {e.Message}");
                return null;
            }
        }

        private static AvatarRecoveryRequest AddOrGetRequest(
            Dictionary<string, AvatarRecoveryRequest> requests,
            string avatarName,
            string source,
            long pathId,
            string recoveryReason,
            string sourceAvatarName)
        {
            var key = source + "|" + pathId.ToString(CultureInfo.InvariantCulture);
            if (!requests.TryGetValue(key, out var request))
            {
                request = new AvatarRecoveryRequest(avatarName, source, pathId)
                {
                    RecoveryReason = recoveryReason ?? "model_avatar_reference",
                    SourceAvatarName = sourceAvatarName ?? avatarName,
                };
                requests[key] = request;
            }

            if (string.Equals(request.RecoveryReason, "model_avatar_reference", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(recoveryReason, "model_avatar_reference", StringComparison.OrdinalIgnoreCase))
            {
                request.RecoveryReason = recoveryReason;
                request.SourceAvatarName = sourceAvatarName ?? request.SourceAvatarName;
            }

            return request;
        }

        private static bool TryFindHumanoidAvatarPair(
            string sourceIndexPath,
            string modelAvatarSource,
            string modelAvatarName,
            Dictionary<string, HumanoidAvatarPair> cache,
            out HumanoidAvatarPair pair)
        {
            pair = null;
            if (string.IsNullOrWhiteSpace(sourceIndexPath)
                || !File.Exists(sourceIndexPath)
                || string.IsNullOrWhiteSpace(modelAvatarSource)
                || string.IsNullOrWhiteSpace(modelAvatarName)
                || !modelAvatarName.EndsWith("_genericAvatar", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var humanoidName = modelAvatarName[..^"_genericAvatar".Length];
            if (string.IsNullOrWhiteSpace(humanoidName))
            {
                return false;
            }

            var cacheKey = NormalizeSourceForIndexMatch(modelAvatarSource) + "|" + humanoidName;
            if (cache.TryGetValue(cacheKey, out pair))
            {
                return pair != null;
            }

            try
            {
                SQLitePCL.Batteries_V2.Init();
                using var connection = new SqliteConnection($"Data Source={Path.GetFullPath(sourceIndexPath)};Mode=ReadOnly");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT source_path, path_id, name
FROM source_objects
WHERE type='Avatar'
  AND name=$name
  AND COALESCE(json_extract(raw_json, '$.avatar.humanBoneCount'), 0) > 0
  AND COALESCE(json_extract(raw_json, '$.avatar.skeletonBoneCount'), 0) > 0;";
                command.Parameters.AddWithValue("$name", humanoidName);
                var requestedSource = NormalizeSourceForIndexMatch(modelAvatarSource);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var candidateSource = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    if (!string.Equals(NormalizeSourceForIndexMatch(candidateSource), requestedSource, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    pair = new HumanoidAvatarPair(
                        reader.IsDBNull(2) ? humanoidName : reader.GetString(2),
                        candidateSource,
                        reader.GetInt64(1));
                    cache[cacheKey] = pair;
                    return true;
                }
            }
            catch (Exception e) when (e is IOException || e is SqliteException || e is InvalidDataException)
            {
                Logger.Warning($"Unable to check source index for humanoid Avatar pair {modelAvatarName}. {e.GetType().Name}: {e.Message}");
            }

            cache[cacheKey] = null;
            pair = null;
            return false;
        }

        private static Avatar FindAvatar(AssetsManager manager, long pathId)
        {
            foreach (var assetsFile in manager.assetsFileList)
            {
                if (assetsFile.ObjectsDic != null && assetsFile.ObjectsDic.TryGetValue(pathId, out var obj) && obj is Avatar avatar)
                {
                    return avatar;
                }
            }
            return null;
        }

        private static JObject WriteResult(AvatarRecoveryRequest request, string status, string unityAssetPath, string message)
        {
            return new JObject
            {
                ["avatarName"] = request.AvatarName,
                ["sourceAvatarName"] = request.SourceAvatarName ?? request.AvatarName,
                ["recoveryReason"] = request.RecoveryReason ?? "model_avatar_reference",
                ["aliasKeys"] = new JArray(request.AliasKeys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                ["source"] = request.Source,
                ["pathId"] = request.PathId,
                ["status"] = status,
                ["unityAssetPath"] = unityAssetPath ?? string.Empty,
                ["message"] = message ?? string.Empty,
                ["modelExamples"] = new JArray(request.ModelExamples.Take(8)),
                ["modelReferenceCount"] = request.ModelExamples.Count,
            };
        }

        private static string ResolveSourceIndexPath(string libraryPath, string explicitSourceIndexPath)
        {
            if (!string.IsNullOrWhiteSpace(explicitSourceIndexPath) && File.Exists(explicitSourceIndexPath))
            {
                return Path.GetFullPath(explicitSourceIndexPath);
            }

            var usagePath = Path.Combine(libraryPath, "source_index_usage.json");
            if (File.Exists(usagePath))
            {
                try
                {
                    var usage = JObject.Parse(File.ReadAllText(usagePath));
                    var sourceIndex = (string)usage["sourceIndex"];
                    if (!string.IsNullOrWhiteSpace(sourceIndex) && File.Exists(sourceIndex))
                    {
                        return Path.GetFullPath(sourceIndex);
                    }
                }
                catch (Exception e) when (e is IOException || e is JsonException)
                {
                    Logger.Warning($"Unable to read source_index_usage.json for Avatar recovery. {e.GetType().Name}: {e.Message}");
                }
            }

            var localSourceIndex = Path.Combine(libraryPath, "unity_source_index.db");
            return File.Exists(localSourceIndex) ? localSourceIndex : null;
        }

        private static string TryReadSourceRootFromIndex(string sourceIndexPath)
        {
            if (string.IsNullOrWhiteSpace(sourceIndexPath) || !File.Exists(sourceIndexPath))
            {
                return null;
            }

            try
            {
                SQLitePCL.Batteries_V2.Init();
                using var connection = new SqliteConnection($"Data Source={Path.GetFullPath(sourceIndexPath)};Mode=ReadOnly");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM metadata WHERE key='sourceRoot' LIMIT 1;";
                var value = command.ExecuteScalar() as string;
                return !string.IsNullOrWhiteSpace(value) && Directory.Exists(value)
                    ? value
                    : null;
            }
            catch (Exception e) when (e is IOException || e is SqliteException)
            {
                Logger.Warning($"Unable to read sourceRoot from source index for Avatar recovery. {e.GetType().Name}: {e.Message}");
                return null;
            }
        }

        private static EndfieldAvatarLoadHints LoadEndfieldAvatarLoadHints(string sourceIndexPath, IEnumerable<AvatarRecoveryRequest> requests)
        {
            if (string.IsNullOrWhiteSpace(sourceIndexPath) || !File.Exists(sourceIndexPath))
            {
                Logger.Warning("Endfield Avatar recovery has no source index; falling back to normal .blc loading.");
                return EndfieldAvatarLoadHints.Empty;
            }

            try
            {
                SQLitePCL.Batteries_V2.Init();
                using var connection = new SqliteConnection($"Data Source={Path.GetFullPath(sourceIndexPath)};Mode=ReadOnly");
                connection.Open();

                var requestList = requests.ToArray();
                var cabByRequest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT source_path, serialized_file
FROM source_objects
WHERE type='Avatar'
  AND path_id=$pathId
  AND serialized_file <> '';";
                    var pathId = command.Parameters.Add("$pathId", SqliteType.Integer);
                    foreach (var request in requestList)
                    {
                        var requestSource = NormalizeSourceForIndexMatch(request.Source);
                        pathId.Value = request.PathId;
                        using var reader = command.ExecuteReader();
                        while (reader.Read())
                        {
                            var source = NormalizeSourceForIndexMatch(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
                            if (!string.Equals(source, requestSource, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            var cab = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                            if (!string.IsNullOrWhiteSpace(cab))
                            {
                                cabByRequest[EndfieldAvatarLoadHints.MakeRequestKey(request.Source, request.PathId)] = cab;
                                break;
                            }
                        }
                    }
                }

                if (cabByRequest.Count == 0)
                {
                    Logger.Warning($"Endfield Avatar recovery source index did not locate selected Avatar CABs: {sourceIndexPath}");
                    return EndfieldAvatarLoadHints.Empty;
                }

                var innerFileByCab = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT name
FROM source_objects
WHERE type='AssetBundle'
  AND serialized_file=$cab
  AND name <> ''
LIMIT 1;";
                    var cab = command.Parameters.Add("$cab", SqliteType.Text);
                    foreach (var selectedCab in cabByRequest.Values.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        cab.Value = selectedCab;
                        var innerFile = command.ExecuteScalar() as string;
                        if (!string.IsNullOrWhiteSpace(innerFile))
                        {
                            innerFileByCab[selectedCab] = NormalizeEndfieldVfsInnerFileName(innerFile);
                        }
                    }
                }

                var innerFileByRequest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in cabByRequest)
                {
                    if (innerFileByCab.TryGetValue(pair.Value, out var innerFile))
                    {
                        innerFileByRequest[pair.Key] = innerFile;
                    }
                }

                Logger.Info($"Endfield Avatar recovery loaded source-index hints: avatars={cabByRequest.Count}, innerFiles={innerFileByRequest.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count()}.");
                return innerFileByRequest.Count == 0
                    ? EndfieldAvatarLoadHints.Empty
                    : new EndfieldAvatarLoadHints(innerFileByRequest);
            }
            catch (Exception e) when (e is IOException || e is SqliteException || e is InvalidDataException)
            {
                Logger.Warning($"Unable to read Endfield Avatar source-index hints; falling back to normal .blc loading. {e.GetType().Name}: {e.Message}");
                return EndfieldAvatarLoadHints.Empty;
            }
        }

        private static Func<string, bool> BuildExactEndfieldVfsInnerFileFilter(IEnumerable<string> innerFiles)
        {
            var selected = innerFiles
                .Select(NormalizeEndfieldVfsInnerFileName)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return fileName =>
            {
                var normalized = NormalizeEndfieldVfsInnerFileName(fileName);
                return selected.Contains(normalized)
                    || selected.Any(path => normalized.EndsWith("/" + path, StringComparison.OrdinalIgnoreCase));
            };
        }

        private static string NormalizeEndfieldVfsInnerFileName(string path)
        {
            return (path ?? string.Empty)
                .Trim()
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace('\\', '/');
        }

        private static string NormalizeSourceForIndexMatch(string path)
        {
            var normalized = NormalizeEndfieldVfsInnerFileName(path);
            var marker = "/StreamingAssets/VFS/";
            var markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                return normalized[(markerIndex + marker.Length)..];
            }

            return normalized;
        }

        private static void UpdateLocalBrowserSettings(string libraryPath, string unityProject, string unityEditor, JArray results, HashSet<string> probeValidNames)
        {
            var cacheDir = Path.Combine(libraryPath, ".as_browser_cache");
            Directory.CreateDirectory(cacheDir);
            var settingsPath = Path.Combine(cacheDir, "unity_bake_settings.json");
            JObject settings = File.Exists(settingsPath)
                ? JObject.Parse(File.ReadAllText(settingsPath))
                : new JObject();

            settings["unityProject"] = Path.GetFullPath(unityProject);
            if (!string.IsNullOrWhiteSpace(unityEditor))
            {
                settings["unityEditor"] = unityEditor;
            }

            var map = settings["unityAvatarAssets"] as JObject ?? new JObject();
            foreach (var result in results.OfType<JObject>())
            {
                if (!IsRecoveredOrExistingStatus((string)result["status"]))
                {
                    continue;
                }

                var avatarName = (string)result["avatarName"];
                var unityAssetPath = (string)result["unityAssetPath"];
                if (probeValidNames == null || string.IsNullOrWhiteSpace(avatarName) || !probeValidNames.Contains(avatarName))
                {
                    if (!string.IsNullOrWhiteSpace(avatarName))
                    {
                        map.Remove(avatarName);
                        if (avatarName.EndsWith("_ModelAvatar", StringComparison.OrdinalIgnoreCase))
                        {
                            map.Remove(avatarName[..^"_ModelAvatar".Length]);
                        }
                    }
                    foreach (var aliasKey in ReadAliasKeys(result))
                    {
                        map.Remove(aliasKey);
                    }
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(avatarName) && !string.IsNullOrWhiteSpace(unityAssetPath))
                {
                    map[avatarName] = unityAssetPath;
                    if (avatarName.EndsWith("_ModelAvatar", StringComparison.OrdinalIgnoreCase))
                    {
                        map[avatarName[..^"_ModelAvatar".Length]] = unityAssetPath;
                    }
                    foreach (var aliasKey in ReadAliasKeys(result))
                    {
                        map[aliasKey] = unityAssetPath;
                    }
                }
            }

            settings["unityAvatarAssets"] = map;
            File.WriteAllText(settingsPath, settings.ToString(Formatting.Indented));
            Logger.Info($"Updated local Browser Unity bake settings: {settingsPath}");
        }

        private static IEnumerable<string> ReadAliasKeys(JObject result)
        {
            return (result?["aliasKeys"] as JArray)?.Values<string>()
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                ?? Enumerable.Empty<string>();
        }

        private static bool IsRecoveredOrExistingStatus(string status)
        {
            return string.Equals(status, "recovered", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "existing_imported", StringComparison.OrdinalIgnoreCase);
        }

        private static int RefreshLibraryIndexImportedAvatarBakeReadiness(
            string dbPath,
            string libraryPath,
            string unityProject,
            string probePath)
        {
            var importedAvatars = LoadFreshImportedAvatarAssetMap(libraryPath, unityProject, probePath);
            if (importedAvatars.Count == 0)
            {
                Logger.Info("No fresh Unity-validated ImportedAvatar assets found for SQLite candidate refresh.");
                return 0;
            }

            var rows = new List<CandidateAvatarRefreshRow>();
            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    using (var create = connection.CreateCommand())
                    {
                        create.Transaction = transaction;
                        create.CommandText = "CREATE TEMP TABLE temp_valid_imported_avatar(key TEXT PRIMARY KEY, asset TEXT NOT NULL);";
                        create.ExecuteNonQuery();
                    }

                    using (var insert = connection.CreateCommand())
                    {
                        insert.Transaction = transaction;
                        insert.CommandText = "INSERT OR IGNORE INTO temp_valid_imported_avatar(key, asset) VALUES ($key, $asset);";
                        var keyParameter = insert.CreateParameter();
                        keyParameter.ParameterName = "$key";
                        insert.Parameters.Add(keyParameter);
                        var assetParameter = insert.CreateParameter();
                        assetParameter.ParameterName = "$asset";
                        insert.Parameters.Add(assetParameter);
                        foreach (var pair in importedAvatars)
                        {
                            keyParameter.Value = pair.Key;
                            assetParameter.Value = pair.Value;
                            insert.ExecuteNonQuery();
                        }
                    }

                    using (var select = connection.CreateCommand())
                    {
                        select.Transaction = transaction;
                        select.CommandText = @"
SELECT c.id, c.raw_json, va.asset, va.key
FROM model_animation_candidates c
JOIN assets m ON m.kind='Model' AND m.output=c.model_output
JOIN temp_valid_imported_avatar va
  ON va.key=COALESCE(json_extract(m.raw_json, '$.avatar.name'), '')
  OR va.key=COALESCE(m.name, '')
  OR va.key=COALESCE(json_extract(c.raw_json, '$.relationEvidence.avatarName'), '')
WHERE c.relation_source='explicit'
  AND (
    COALESCE(json_extract(c.raw_json, '$.requiresUnityBake'), 0)=1
    OR COALESCE(json_extract(c.raw_json, '$.legacyUnityBakeSupported'), 0)=1
    OR COALESCE(json_extract(c.raw_json, '$.requiresInternalHumanoidSolve'), 0)=1
    OR COALESCE(json_extract(c.raw_json, '$.fullHumanoidBakeRequired'), 0)=1
    OR COALESCE(json_extract(c.raw_json, '$.productionUnityBakeReady'), 0)=1
    OR COALESCE(json_extract(c.raw_json, '$.directHumanoidTrsRequired'), 0)=1
    OR COALESCE(json_extract(c.raw_json, '$.directTrsSolveRequiresProductionAvatar'), 0)=1
    OR COALESCE(json_extract(c.raw_json, '$.standaloneBodyRequiresDirectTrsSolve'), 0)=1
  );";
                        using var reader = select.ExecuteReader();
                        var seenIds = new HashSet<long>();
                        while (reader.Read())
                        {
                            var id = reader.GetInt64(0);
                            if (!seenIds.Add(id))
                            {
                                continue;
                            }

                            rows.Add(new CandidateAvatarRefreshRow(
                                id,
                                reader.IsDBNull(1) ? "{}" : reader.GetString(1),
                                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                                reader.IsDBNull(3) ? string.Empty : reader.GetString(3)));
                        }
                    }

                    using (var update = connection.CreateCommand())
                    {
                        update.Transaction = transaction;
                        update.CommandText = "UPDATE model_animation_candidates SET raw_json=$raw WHERE id=$id;";
                        var rawParameter = update.CreateParameter();
                        rawParameter.ParameterName = "$raw";
                        update.Parameters.Add(rawParameter);
                        var idParameter = update.CreateParameter();
                        idParameter.ParameterName = "$id";
                        update.Parameters.Add(idParameter);

                        var changed = 0;
                        foreach (var row in rows)
                        {
                            var refreshed = RefreshCandidateJsonForImportedAvatar(
                                row.RawJson,
                                row.AssetPath,
                                row.MatchKey,
                                CandidateHasDecodedFloatCurves(row.RawJson, libraryPath));
                            if (string.Equals(refreshed, row.RawJson, StringComparison.Ordinal))
                            {
                                continue;
                            }

                            rawParameter.Value = refreshed;
                            idParameter.Value = row.Id;
                            update.ExecuteNonQuery();
                            changed++;
                        }

                        transaction.Commit();
                        Logger.Info($"Refreshed {changed} model-animation candidate row(s) with Unity-validated ImportedAvatar readiness.");
                        return changed;
                    }
                }
            }
        }

        private static Dictionary<string, string> LoadFreshImportedAvatarAssetMap(
            string libraryPath,
            string unityProject,
            string probePath)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(libraryPath) || string.IsNullOrWhiteSpace(unityProject))
            {
                return result;
            }

            var importedRoot = Path.Combine(Path.GetFullPath(unityProject), ImportedAvatarFolder.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(importedRoot))
            {
                return result;
            }

            var assetFiles = Directory.EnumerateFiles(importedRoot, "*.asset", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .ToArray();
            if (assetFiles.Length == 0)
            {
                return result;
            }

            var reportFile = ResolveProbeReportFile(libraryPath, probePath);
            if (reportFile == null || !reportFile.Exists)
            {
                return result;
            }

            var newestAssetTime = assetFiles.Max(file => file.LastWriteTimeUtc);
            if (reportFile.LastWriteTimeUtc < newestAssetTime)
            {
                return result;
            }

            try
            {
                var root = JObject.Parse(File.ReadAllText(reportFile.FullName));
                if ((int?)root["totalAssets"] != assetFiles.Length)
                {
                    return result;
                }

                foreach (var item in (root["items"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    if (!string.Equals((string)item["status"], "ok", StringComparison.OrdinalIgnoreCase)
                        || (bool?)item["isValid"] != true
                        || (bool?)item["isHuman"] != true)
                    {
                        continue;
                    }

                    var avatarAssetPath = ((string)item["avatarAssetPath"])?.Replace('\\', '/');
                    if (string.IsNullOrWhiteSpace(avatarAssetPath))
                    {
                        continue;
                    }

                    var fullPath = Path.Combine(Path.GetFullPath(unityProject), avatarAssetPath.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(fullPath))
                    {
                        continue;
                    }

                    var name = Path.GetFileNameWithoutExtension(avatarAssetPath);
                    AddImportedAvatarAssetMapKey(result, name, avatarAssetPath);
                }

                AddValidatedUnityAvatarSettingsAliases(result, libraryPath);
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            return result;
        }

        private static void AddValidatedUnityAvatarSettingsAliases(Dictionary<string, string> result, string libraryPath)
        {
            if (result == null || result.Count == 0 || string.IsNullOrWhiteSpace(libraryPath))
            {
                return;
            }

            var validAssets = result.Values
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(NormalizeUnityAssetPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (validAssets.Count == 0)
            {
                return;
            }

            var settingsPath = Path.Combine(libraryPath, ".as_browser_cache", "unity_bake_settings.json");
            if (!File.Exists(settingsPath))
            {
                return;
            }

            try
            {
                var settings = JObject.Parse(File.ReadAllText(settingsPath));
                if (settings["unityAvatarAssets"] is not JObject map)
                {
                    return;
                }

                foreach (var property in map.Properties())
                {
                    var key = property.Name?.Trim();
                    var assetPath = NormalizeUnityAssetPath((string)property.Value);
                    if (string.IsNullOrWhiteSpace(key)
                        || string.IsNullOrWhiteSpace(assetPath)
                        || !validAssets.Contains(assetPath))
                    {
                        continue;
                    }

                    result[key] = assetPath;
                }
            }
            catch (Exception e) when (e is IOException || e is JsonException)
            {
                Logger.Warning($"Unable to read local Unity avatar alias settings. {e.GetType().Name}: {e.Message}");
            }
        }

        private static FileInfo ResolveProbeReportFile(string libraryPath, string probePath)
        {
            if (!string.IsNullOrWhiteSpace(probePath))
            {
                var explicitReport = Path.Combine(probePath, "imported_avatar_probe_batch.json");
                if (File.Exists(explicitReport))
                {
                    return new FileInfo(explicitReport);
                }
            }

            try
            {
                return Directory.EnumerateDirectories(libraryPath, "ImportedAvatarProbe*", SearchOption.TopDirectoryOnly)
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
        }

        private static void AddImportedAvatarAssetMapKey(Dictionary<string, string> target, string name, string assetPath)
        {
            if (target == null || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(assetPath))
            {
                return;
            }

            target[name] = assetPath;
            if (name.EndsWith("_ModelAvatar", StringComparison.OrdinalIgnoreCase))
            {
                target[name[..^"_ModelAvatar".Length]] = assetPath;
            }
        }

        private static string NormalizeUnityAssetPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var text = value.Trim().Trim('"').Replace('\\', '/');
            var assetsIndex = text.IndexOf("Assets/", StringComparison.OrdinalIgnoreCase);
            return assetsIndex >= 0 ? text[assetsIndex..] : text;
        }

        private static bool CandidateHasDecodedFloatCurves(string rawJson, string libraryPath)
        {
            if (string.IsNullOrWhiteSpace(rawJson) || string.IsNullOrWhiteSpace(libraryPath))
            {
                return false;
            }

            try
            {
                var json = JObject.Parse(rawJson);
                var sidecarPath = ResolveCandidateLibraryPath(libraryPath, (string)json["animationAsset"]);
                return SidecarHasDecodedFloatCurves(sidecarPath);
            }
            catch (Exception e) when (e is IOException || e is JsonException)
            {
                Logger.Warning($"Unable to inspect animation sidecar decoded float curves. {e.GetType().Name}: {e.Message}");
                return false;
            }
        }

        private static string ResolveCandidateLibraryPath(string libraryPath, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            if (Path.IsPathRooted(path))
            {
                return path;
            }

            return Path.Combine(Path.GetFullPath(libraryPath), path.Replace('/', Path.DirectorySeparatorChar));
        }

        private static bool SidecarHasDecodedFloatCurves(string sidecarPath)
        {
            if (string.IsNullOrWhiteSpace(sidecarPath) || !File.Exists(sidecarPath))
            {
                return false;
            }

            using var stream = File.OpenText(sidecarPath);
            using var reader = new JsonTextReader(stream);
            var decodedObjectDepth = -1;
            while (reader.Read())
            {
                if (decodedObjectDepth >= 0
                    && reader.TokenType == JsonToken.EndObject
                    && reader.Depth == decodedObjectDepth)
                {
                    decodedObjectDepth = -1;
                    continue;
                }

                if (reader.TokenType != JsonToken.PropertyName)
                {
                    continue;
                }

                var propertyName = reader.Value as string;
                if (decodedObjectDepth < 0 && string.Equals(propertyName, "decoded", StringComparison.OrdinalIgnoreCase))
                {
                    if (!reader.Read())
                    {
                        return false;
                    }

                    if (reader.TokenType == JsonToken.StartObject)
                    {
                        decodedObjectDepth = reader.Depth;
                    }
                    continue;
                }

                if (decodedObjectDepth >= 0
                    && reader.Depth == decodedObjectDepth + 1
                    && string.Equals(propertyName, "floats", StringComparison.OrdinalIgnoreCase))
                {
                    if (!reader.Read() || reader.TokenType != JsonToken.StartArray)
                    {
                        return false;
                    }

                    if (!reader.Read())
                    {
                        return false;
                    }

                    return reader.TokenType != JsonToken.EndArray;
                }
            }

            return false;
        }

        private static string RefreshCandidateJsonForImportedAvatar(string rawJson, string assetPath, string matchKey, bool hasDecodedFloatCurves)
        {
            JObject json;
            try
            {
                json = string.IsNullOrWhiteSpace(rawJson) ? new JObject() : JObject.Parse(rawJson);
            }
            catch
            {
                json = new JObject();
            }

            var before = json.ToString(Formatting.None);
            json["legacyUnityBakeSupported"] = false;
            json["requiresUnityBake"] = false;
            json["productionUnityBakeReady"] = false;
            json["productionUnityBakeBlocked"] = !hasDecodedFloatCurves;
            json["productionUnityBakeBlockedReason"] = hasDecodedFloatCurves ? null : "missing_decoded_float_curves";
            json["unityBakeAcceleratedReady"] = hasDecodedFloatCurves;
            json["unityBakeAcceleratedBlockedReason"] = hasDecodedFloatCurves ? null : "missing_decoded_float_curves";
            json["productionAnimationReady"] = false;
            json["productionAnimationPath"] = hasDecodedFloatCurves ? "UnityBakeAccelerated" : "NeedsDirectTrsAnimation";
            json["requiresInternalHumanoidSolve"] = false;
            json["missingInternalHumanoidSolver"] = false;
            json["missingInternalHumanoidSolverReason"] = null;
            json["directTrsSolveRequiresProductionAvatar"] = false;
            json["directTrsSolveBlockedReason"] = hasDecodedFloatCurves ? null : "missing_decoded_float_curves";
            json["fullHumanoidBakeBlocked"] = !hasDecodedFloatCurves;
            json["fullHumanoidBakeBlockedReason"] = hasDecodedFloatCurves ? null : "missing_decoded_float_curves";
            json["nextAction"] = hasDecodedFloatCurves
                ? "generate_unity_bake_accelerated_request"
                : "export_full_decoded_animation_curves";
            json["unityAvatarAsset"] = assetPath;
            json["unityAvatarMatchKey"] = matchKey;
            json["productionUnityBakeAvatarSource"] = "imported_unity_avatar_asset";
            json["importedAvatarAssetValidated"] = true;

            var after = json.ToString(Formatting.None);
            return string.Equals(before, after, StringComparison.Ordinal) ? rawJson : after;
        }

        private static HashSet<string> LoadProbeValidAvatarNames(string probePath)
        {
            if (string.IsNullOrWhiteSpace(probePath))
            {
                return null;
            }

            var reportPath = Path.Combine(probePath, "imported_avatar_probe_batch.json");
            if (!File.Exists(reportPath))
            {
                return null;
            }

            try
            {
                var root = JObject.Parse(File.ReadAllText(reportPath));
                var items = root["items"] as JArray;
                if (items == null)
                {
                    return null;
                }

                var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in items.OfType<JObject>())
                {
                    if (string.Equals((string)item["status"], "ok", StringComparison.OrdinalIgnoreCase)
                        && (bool?)item["isValid"] == true
                        && (bool?)item["isHuman"] == true)
                    {
                        var avatarName = (string)item["avatarName"];
                        if (!string.IsNullOrWhiteSpace(avatarName))
                        {
                            result.Add(avatarName);
                        }
                    }
                }

                return result;
            }
            catch
            {
                return null;
            }
        }

        private static int QuarantineInvalidRecoveredAvatars(
            string unityProject,
            string probePath,
            JArray results,
            HashSet<string> probeValidNames)
        {
            if (string.IsNullOrWhiteSpace(unityProject) || string.IsNullOrWhiteSpace(probePath))
            {
                return 0;
            }

            var recovered = results.OfType<JObject>()
                .Where(x => string.Equals((string)x["status"], "recovered", StringComparison.OrdinalIgnoreCase))
                .Select(x => new
                {
                    AvatarName = (string)x["avatarName"],
                    UnityAssetPath = (string)x["unityAssetPath"],
                    Result = x,
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.AvatarName)
                    && !string.IsNullOrWhiteSpace(x.UnityAssetPath)
                    && (probeValidNames == null || !probeValidNames.Contains(x.AvatarName)))
                .ToArray();
            if (recovered.Length == 0)
            {
                return 0;
            }

            var probeStatuses = LoadProbeAvatarStatuses(probePath);
            var count = 0;
            var quarantineDir = Path.Combine(Path.GetFullPath(unityProject), "Assets", "AnimeStudioBake", "InvalidImportedAvatar");
            Directory.CreateDirectory(quarantineDir);
            foreach (var item in recovered)
            {
                if (probeStatuses != null && !probeStatuses.ContainsKey(item.AvatarName))
                {
                    continue;
                }

                var isValid = probeStatuses != null
                    && probeStatuses.TryGetValue(item.AvatarName, out var status)
                    && status.IsValid;
                var isHuman = probeStatuses != null
                    && probeStatuses.TryGetValue(item.AvatarName, out status)
                    && status.IsHuman;
                var assetPath = item.UnityAssetPath.Replace('/', Path.DirectorySeparatorChar);
                var fullPath = Path.Combine(Path.GetFullPath(unityProject), assetPath);
                if (!File.Exists(fullPath))
                {
                    continue;
                }

                var target = Path.Combine(quarantineDir, Path.GetFileName(fullPath));
                File.Move(fullPath, target, overwrite: true);
                var meta = fullPath + ".meta";
                if (File.Exists(meta))
                {
                    File.Move(meta, target + ".meta", overwrite: true);
                }

                item.Result["status"] = isValid && !isHuman
                    ? "quarantined_non_humanoid_avatar"
                    : "quarantined_invalid_avatar";
                item.Result["unityProbeIsValid"] = isValid;
                item.Result["unityProbeIsHuman"] = isHuman;
                item.Result["message"] = isValid && !isHuman
                    ? "Recovered Avatar asset is a valid Unity Avatar, but Unity reports isHuman=false. It was moved out of ImportedAvatar because it cannot be used as a Humanoid/Muscle production bake oracle."
                    : "Recovered Avatar asset was imported by Unity but failed Avatar validation, so it was moved out of ImportedAvatar and will not be used for production bake.";
                count++;
            }

            return count;
        }

        private static Dictionary<string, ProbeAvatarStatus> LoadProbeAvatarStatuses(string probePath)
        {
            var reportPath = Path.Combine(probePath, "imported_avatar_probe_batch.json");
            if (!File.Exists(reportPath))
            {
                return null;
            }

            try
            {
                var root = JObject.Parse(File.ReadAllText(reportPath));
                var items = root["items"] as JArray;
                if (items == null)
                {
                    return null;
                }

                var result = new Dictionary<string, ProbeAvatarStatus>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in items.OfType<JObject>())
                {
                    var isValid = (bool?)item["isValid"] == true;
                    var isHuman = (bool?)item["isHuman"] == true;
                    if (!string.Equals((string)item["status"], "ok", StringComparison.OrdinalIgnoreCase)
                        || !isValid
                        || !isHuman)
                    {
                        var avatarName = (string)item["avatarName"];
                        if (!string.IsNullOrWhiteSpace(avatarName))
                        {
                            result[avatarName] = new ProbeAvatarStatus(isValid, isHuman);
                        }
                    }
                }

                return result;
            }
            catch
            {
                return null;
            }
        }

        private static string TryRunUnityProbe(string libraryPath, string unityProject, string unityEditor, string reportDir)
        {
            if (string.IsNullOrWhiteSpace(unityEditor) || !File.Exists(unityEditor))
            {
                Logger.Warning("Unity probe skipped because --unity_editor was not provided or does not exist.");
                return null;
            }

            var script = Path.Combine(Environment.CurrentDirectory, "scripts", "Test-UnityImportedAvatarAssets.ps1");
            if (!File.Exists(script))
            {
                Logger.Warning($"Unity probe skipped because script was not found: {script}");
                return null;
            }

            var probeDir = Path.Combine(libraryPath, "ImportedAvatarProbe_AutoRecovery_" + DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(probeDir);
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-ExecutionPolicy Bypass -File \"{script}\" -UnityProject \"{unityProject}\" -UnityEditor \"{unityEditor}\" -OutputDir \"{probeDir}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            File.WriteAllText(Path.Combine(reportDir, "unity_probe_stdout.txt"), stdout ?? string.Empty);
            File.WriteAllText(Path.Combine(reportDir, "unity_probe_stderr.txt"), stderr ?? string.Empty);
            if (process.ExitCode != 0)
            {
                Logger.Warning($"Unity imported Avatar probe failed with exit code {process.ExitCode}. See {reportDir}");
            }
            else
            {
                Logger.Info($"Unity imported Avatar probe completed: {probeDir}");
            }

            return probeDir;
        }

        private static string ResolveSourcePath(string indexedSourcePath, string sourceRootOverride)
        {
            if (string.IsNullOrWhiteSpace(indexedSourcePath))
            {
                return indexedSourcePath;
            }

            var sourceFilePath = indexedSourcePath;
            var pipeIndex = sourceFilePath.IndexOf('|');
            if (pipeIndex >= 0)
            {
                sourceFilePath = sourceFilePath[..pipeIndex];
            }

            if (File.Exists(sourceFilePath))
            {
                return Path.GetFullPath(sourceFilePath);
            }

            if (string.IsNullOrWhiteSpace(sourceRootOverride) || !Directory.Exists(sourceRootOverride))
            {
                return sourceFilePath;
            }

            var normalized = sourceFilePath.Replace('\\', '/');
            if (!Path.IsPathRooted(sourceFilePath))
            {
                var relativeCandidate = Path.Combine(sourceRootOverride, normalized.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(relativeCandidate))
                {
                    return Path.GetFullPath(relativeCandidate);
                }
            }

            var marker = "/StreamingAssets/";
            var markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                var relative = normalized.Substring(markerIndex + 1);
                var candidate = Path.Combine(sourceRootOverride, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            var fileName = Path.GetFileName(sourceFilePath);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                var matches = Directory.EnumerateFiles(sourceRootOverride, fileName, SearchOption.AllDirectories).Take(2).ToArray();
                if (matches.Length == 1)
                {
                    return Path.GetFullPath(matches[0]);
                }
            }

            return sourceFilePath;
        }

        private static bool MatchesSelector(string selector, params string[] values)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                return true;
            }

            try
            {
                return values.Any(x => !string.IsNullOrWhiteSpace(x) && System.Text.RegularExpressions.Regex.IsMatch(x, selector, System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            }
            catch
            {
                return values.Any(x => !string.IsNullOrWhiteSpace(x) && x.IndexOf(selector, StringComparison.OrdinalIgnoreCase) >= 0);
            }
        }

        private static string ToUnityAssetPath(string unityProject, string fullPath)
        {
            var project = Path.GetFullPath(unityProject).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var path = Path.GetFullPath(fullPath);
            var relative = Path.GetRelativePath(project, path).Replace('\\', '/');
            return relative.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ? relative : path.Replace('\\', '/');
        }

        private sealed class EndfieldAvatarLoadHints
        {
            private readonly Dictionary<string, string> innerFileByRequest;

            public EndfieldAvatarLoadHints(Dictionary<string, string> innerFileByRequest)
            {
                this.innerFileByRequest = innerFileByRequest ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            public static EndfieldAvatarLoadHints Empty { get; } = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

            public static string MakeRequestKey(string source, long pathId)
            {
                return NormalizeSourceForIndexMatch(source) + "|" + pathId.ToString(CultureInfo.InvariantCulture);
            }

            public HashSet<string> GetInnerFiles(IEnumerable<AvatarRecoveryRequest> requests)
            {
                var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var request in requests)
                {
                    if (innerFileByRequest.TryGetValue(MakeRequestKey(request.Source, request.PathId), out var innerFile)
                        && !string.IsNullOrWhiteSpace(innerFile))
                    {
                        result.Add(innerFile);
                    }
                }

                return result;
            }
        }

        private readonly struct ProbeAvatarStatus
        {
            public ProbeAvatarStatus(bool isValid, bool isHuman)
            {
                IsValid = isValid;
                IsHuman = isHuman;
            }

            public bool IsValid { get; }
            public bool IsHuman { get; }
        }

        private sealed class AvatarRecoveryRequest
        {
            public AvatarRecoveryRequest(string avatarName, string source, long pathId)
            {
                AvatarName = avatarName;
                Source = source;
                PathId = pathId;
            }

            public string AvatarName { get; }
            public string Source { get; }
            public long PathId { get; }
            public string RecoveryReason { get; set; } = "model_avatar_reference";
            public string SourceAvatarName { get; set; }
            public string ExistingUnityAssetPath { get; set; }
            public HashSet<string> AliasKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<JObject> ModelExamples { get; } = new();
        }

        private sealed class HumanoidAvatarPair
        {
            public HumanoidAvatarPair(string name, string source, long pathId)
            {
                Name = name ?? string.Empty;
                Source = source ?? string.Empty;
                PathId = pathId;
            }

            public string Name { get; }
            public string Source { get; }
            public long PathId { get; }
        }

        private sealed class CandidateAvatarRefreshRow
        {
            public CandidateAvatarRefreshRow(long id, string rawJson, string assetPath, string matchKey)
            {
                Id = id;
                RawJson = rawJson ?? "{}";
                AssetPath = assetPath ?? string.Empty;
                MatchKey = matchKey ?? string.Empty;
            }

            public long Id { get; }
            public string RawJson { get; }
            public string AssetPath { get; }
            public string MatchKey { get; }
        }
    }
}
