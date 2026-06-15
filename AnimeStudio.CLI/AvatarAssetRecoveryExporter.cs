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

            var requests = ReadMissingAvatarRequests(dbPath, importedRoot, selector, limit, force);
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
            var bySource = requests.GroupBy(x => ResolveSourcePath(x.Source, sourceRootOverride), StringComparer.OrdinalIgnoreCase);
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
                Logger.Warning($"Quarantined {quarantined} invalid imported Avatar asset(s); rerunning Unity probe so Browser sees a clean readiness report.");
                probePath = TryRunUnityProbe(libraryPath, unityProject, unityEditor, reportDir);
                probeValidNames = LoadProbeValidAvatarNames(probePath);
            }
            UpdateLocalBrowserSettings(libraryPath, unityProject, unityEditor, results, probeValidNames);
            var refreshedCandidateRows = RefreshLibraryIndexImportedAvatarBakeReadiness(dbPath, libraryPath, unityProject, probePath);

            var recovered = results.Count(x => string.Equals((string)x["status"], "recovered", StringComparison.OrdinalIgnoreCase));
            var trusted = probeValidNames == null
                ? 0
                : results.Count(x => string.Equals((string)x["status"], "recovered", StringComparison.OrdinalIgnoreCase)
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
                ["recoveredAvatarObjects"] = recovered,
                ["trustedHumanAvatarObjects"] = trusted,
                ["quarantinedInvalidAvatarObjects"] = quarantined,
                ["failedAvatarObjects"] = requests.Count - recovered,
                ["probeReport"] = probePath ?? string.Empty,
                ["refreshedCandidateRows"] = refreshedCandidateRows,
                ["rule"] = "从 library_index.db 的 Unity avatar source/pathId 精确恢复原始 UnityEngine.Avatar asset；不使用骨骼数量、名称或姿态推断动画关系。",
                ["results"] = results,
            };
            File.WriteAllText(reportPath, report.ToString(Formatting.Indented));
            Logger.Info($"Imported Avatar recovery report written: {reportPath}");
            return reportPath;
        }

        private static List<AvatarRecoveryRequest> ReadMissingAvatarRequests(string dbPath, string importedRoot, string selector, int limit, bool force)
        {
            var requests = new Dictionary<string, AvatarRecoveryRequest>(StringComparer.OrdinalIgnoreCase);
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

                var targetAsset = Path.Combine(importedRoot, Exporter.FixFileName(avatarName) + ".asset");
                if (!force && File.Exists(targetAsset))
                {
                    continue;
                }

                var invalidAsset = Path.Combine(Path.GetDirectoryName(importedRoot) ?? importedRoot, "InvalidImportedAvatar", Exporter.FixFileName(avatarName) + ".asset");
                if (!force && File.Exists(invalidAsset))
                {
                    continue;
                }

                var key = source + "|" + pathId.Value.ToString(CultureInfo.InvariantCulture);
                if (!requests.TryGetValue(key, out var request))
                {
                    request = new AvatarRecoveryRequest(avatarName, source, pathId.Value);
                    requests[key] = request;
                }
                request.ModelExamples.Add(new JObject
                {
                    ["name"] = modelName,
                    ["output"] = modelOutput,
                });
            }

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
                ["source"] = request.Source,
                ["pathId"] = request.PathId,
                ["status"] = status,
                ["unityAssetPath"] = unityAssetPath ?? string.Empty,
                ["message"] = message ?? string.Empty,
                ["modelExamples"] = new JArray(request.ModelExamples.Take(8)),
                ["modelReferenceCount"] = request.ModelExamples.Count,
            };
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
                if (!string.Equals((string)result["status"], "recovered", StringComparison.OrdinalIgnoreCase))
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
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(avatarName) && !string.IsNullOrWhiteSpace(unityAssetPath))
                {
                    map[avatarName] = unityAssetPath;
                    if (avatarName.EndsWith("_ModelAvatar", StringComparison.OrdinalIgnoreCase))
                    {
                        map[avatarName[..^"_ModelAvatar".Length]] = unityAssetPath;
                    }
                }
            }

            settings["unityAvatarAssets"] = map;
            File.WriteAllText(settingsPath, settings.ToString(Formatting.Indented));
            Logger.Info($"Updated local Browser Unity bake settings: {settingsPath}");
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
WHERE c.relation_source='explicit'
  AND (
    COALESCE(json_extract(c.raw_json, '$.requiresUnityBake'), 0)=1
    OR COALESCE(json_extract(c.raw_json, '$.legacyUnityBakeSupported'), 0)=1
    OR COALESCE(json_extract(c.raw_json, '$.requiresInternalHumanoidSolve'), 0)=1
    OR COALESCE(json_extract(c.raw_json, '$.fullHumanoidBakeRequired'), 0)=1
    OR COALESCE(json_extract(c.raw_json, '$.productionUnityBakeReady'), 0)=1
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
                            var refreshed = RefreshCandidateJsonForImportedAvatar(row.RawJson, row.AssetPath, row.MatchKey);
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
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            return result;
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

        private static string RefreshCandidateJsonForImportedAvatar(string rawJson, string assetPath, string matchKey)
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
            json["legacyUnityBakeSupported"] = true;
            json["requiresUnityBake"] = true;
            json["productionUnityBakeReady"] = true;
            json["productionUnityBakeBlocked"] = false;
            json["productionUnityBakeBlockedReason"] = null;
            json["productionAnimationPath"] = "UnityBakeToGltf";
            json["requiresInternalHumanoidSolve"] = false;
            json["missingInternalHumanoidSolver"] = false;
            json["missingInternalHumanoidSolverReason"] = null;
            json["fullHumanoidBakeBlocked"] = false;
            json["fullHumanoidBakeBlockedReason"] = null;
            json["nextAction"] = "generate_unity_baked_gltf";
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

            var invalidNames = LoadProbeInvalidAvatarNames(probePath);
            var count = 0;
            var quarantineDir = Path.Combine(Path.GetFullPath(unityProject), "Assets", "AnimeStudioBake", "InvalidImportedAvatar");
            Directory.CreateDirectory(quarantineDir);
            foreach (var item in recovered)
            {
                if (invalidNames != null && !invalidNames.Contains(item.AvatarName))
                {
                    continue;
                }

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

                item.Result["status"] = "quarantined_invalid_avatar";
                item.Result["message"] = "Recovered Avatar asset was imported by Unity but failed Humanoid validation, so it was moved out of ImportedAvatar and will not be used for production bake.";
                count++;
            }

            return count;
        }

        private static HashSet<string> LoadProbeInvalidAvatarNames(string probePath)
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

                var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in items.OfType<JObject>())
                {
                    if (!string.Equals((string)item["status"], "ok", StringComparison.OrdinalIgnoreCase)
                        || (bool?)item["isValid"] != true
                        || (bool?)item["isHuman"] != true)
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

            if (File.Exists(indexedSourcePath))
            {
                return Path.GetFullPath(indexedSourcePath);
            }

            if (string.IsNullOrWhiteSpace(sourceRootOverride) || !Directory.Exists(sourceRootOverride))
            {
                return indexedSourcePath;
            }

            var normalized = indexedSourcePath.Replace('\\', '/');
            var marker = "/StreamingAssets/";
            var markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                var relative = normalized.Substring(markerIndex + marker.Length);
                var candidate = Path.Combine(sourceRootOverride, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            var fileName = Path.GetFileName(indexedSourcePath);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                var matches = Directory.EnumerateFiles(sourceRootOverride, fileName, SearchOption.AllDirectories).Take(2).ToArray();
                if (matches.Length == 1)
                {
                    return Path.GetFullPath(matches[0]);
                }
            }

            return indexedSourcePath;
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
            public List<JObject> ModelExamples { get; } = new();
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
