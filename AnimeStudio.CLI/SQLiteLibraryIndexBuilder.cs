using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AnimeStudio.CLI
{
    public static class SQLiteLibraryIndexBuilder
    {
        private static readonly string[] JsonDocumentNames =
        {
            "model_animations.json",
            "model_animations.compact.json",
            "character_assemblies.json",
            "unity_relation_summary.json",
            "model_validation.json",
            "library_summary.json",
            "audio_library_summary.json",
            "texture2darray_summary.json"
        };

        public static string Build(string exportRoot, string indexPath = null)
        {
            if (string.IsNullOrWhiteSpace(exportRoot) || !Directory.Exists(exportRoot))
            {
                throw new DirectoryNotFoundException($"Export root not found: {exportRoot}");
            }

            SQLitePCL.Batteries_V2.Init();

            var root = Path.GetFullPath(exportRoot);
            var dbPath = string.IsNullOrWhiteSpace(indexPath)
                ? Path.Combine(root, "library_index.db")
                : Path.GetFullPath(indexPath);
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath) ?? root);
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }

            var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();
            Execute(connection, "PRAGMA journal_mode = WAL;");
            Execute(connection, "PRAGMA synchronous = NORMAL;");
            CreateSchema(connection);

            using var transaction = connection.BeginTransaction();
            InsertMetadata(connection, transaction, "schemaVersion", "1");
            InsertMetadata(connection, transaction, "root", root);
            InsertMetadata(connection, transaction, "createdUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            InsertMetadata(connection, transaction, "rule", "索引要全，导出要精。SQLite v1 indexes exported Library/AudioLibrary artifacts; raw Unity source graph indexing will extend this schema later.");

            counts["assets"] = ImportAssetCatalog(connection, transaction, root);
            counts["modelBindingPaths"] = ImportModelBindingPaths(connection, transaction, root);
            counts["unityAssets"] = 0;
            counts["unityRelations"] = 0;
            counts["animationBindings"] = 0;
            counts["animationBindingPaths"] = 0;
            ImportUnityRelations(connection, transaction, root, counts);
            counts["modelAnimationCandidates"] = ImportModelAnimationCandidates(connection, transaction, root);
            counts["explicitSourceModelAnimationCandidates"] = ImportExplicitModelAnimationCandidatesFromSourceIndex(connection, transaction, root);
            counts["modelAnimationCandidates"] += counts["explicitSourceModelAnimationCandidates"];
            counts["exportManifest"] = ImportExportManifest(connection, transaction, root);
            counts["jsonDocuments"] = ImportJsonDocuments(connection, transaction, root);
            counts["files"] = ImportFiles(connection, transaction, root, dbPath);

            transaction.Commit();
            CreateIndexes(connection);

            WriteSummary(root, dbPath, counts);
            Logger.Info($"SQLite library index written: {dbPath}");
            return dbPath;
        }

        private static void CreateSchema(SqliteConnection connection)
        {
            Execute(connection, @"
CREATE TABLE metadata (
    key TEXT PRIMARY KEY,
    value TEXT
);
CREATE TABLE assets (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    kind TEXT,
    resource_kind TEXT,
    name TEXT,
    source_type TEXT,
    container TEXT,
    source TEXT,
    path_id INTEGER,
    output TEXT,
    audio_kind TEXT,
    animation_type TEXT,
    skeleton_hash TEXT,
    raw_json TEXT NOT NULL
);
CREATE TABLE unity_assets (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    type TEXT,
    name TEXT,
    source TEXT,
    file TEXT,
    path_id INTEGER,
    container TEXT,
    exported INTEGER,
    raw_json TEXT NOT NULL
);
CREATE TABLE unity_relations (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    relation TEXT,
    confidence TEXT,
    from_type TEXT,
    from_name TEXT,
    from_source TEXT,
    from_path_id INTEGER,
    to_type TEXT,
    to_name TEXT,
    to_source TEXT,
    to_path_id INTEGER,
    target_count INTEGER,
    raw_json TEXT NOT NULL
);
CREATE TABLE animation_bindings (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    animation_name TEXT,
    animation_source TEXT,
    animation_path_id INTEGER,
    binding_count INTEGER,
    has_muscle_clip INTEGER,
    source_file TEXT,
    raw_json TEXT NOT NULL
);
CREATE TABLE animation_binding_paths (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    animation_binding_id INTEGER NOT NULL,
    binding_path TEXT NOT NULL,
    binding_type TEXT,
    raw_json TEXT,
    FOREIGN KEY(animation_binding_id) REFERENCES animation_bindings(id)
);
CREATE TABLE model_binding_paths (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    model_output TEXT NOT NULL,
    binding_path TEXT NOT NULL,
    binding_type TEXT,
    raw_json TEXT
);
CREATE TABLE model_animation_candidates (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    model_output TEXT NOT NULL,
    animation_output TEXT NOT NULL,
    relation_source TEXT,
    confidence TEXT,
    score REAL,
    status TEXT,
    needs_validation INTEGER,
    raw_json TEXT NOT NULL
);
CREATE TABLE animation_preview_cache (
    model_output TEXT NOT NULL,
    animation_output TEXT NOT NULL,
    status TEXT NOT NULL,
    gltf_path TEXT,
    validation_path TEXT,
    message TEXT,
    updated_utc TEXT,
    PRIMARY KEY(model_output, animation_output)
);
CREATE TABLE export_manifest (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    type TEXT,
    name TEXT,
    output TEXT,
    source TEXT,
    path_id INTEGER,
    raw_json TEXT NOT NULL
);
CREATE TABLE json_documents (
    name TEXT PRIMARY KEY,
    path TEXT NOT NULL,
    raw_json TEXT NOT NULL
);
CREATE TABLE files (
    path TEXT PRIMARY KEY,
    kind TEXT,
    size INTEGER,
    modified_utc TEXT
);");
        }

        private static void CreateIndexes(SqliteConnection connection)
        {
            Execute(connection, @"
CREATE INDEX idx_assets_kind ON assets(kind, resource_kind);
CREATE INDEX idx_assets_name ON assets(name);
CREATE INDEX idx_assets_source ON assets(source, path_id);
CREATE INDEX idx_assets_output ON assets(output);
CREATE INDEX idx_unity_assets_source ON unity_assets(source, path_id);
CREATE INDEX idx_unity_assets_name ON unity_assets(name);
CREATE INDEX idx_unity_relations_from ON unity_relations(from_source, from_path_id);
CREATE INDEX idx_unity_relations_to ON unity_relations(to_source, to_path_id);
CREATE INDEX idx_unity_relations_relation ON unity_relations(relation, confidence);
CREATE INDEX idx_animation_bindings_name ON animation_bindings(animation_name);
CREATE INDEX idx_animation_binding_paths_path ON animation_binding_paths(binding_path);
CREATE INDEX idx_animation_binding_paths_animation ON animation_binding_paths(animation_binding_id);
CREATE INDEX idx_model_binding_paths_model ON model_binding_paths(model_output);
CREATE INDEX idx_model_binding_paths_path ON model_binding_paths(binding_path);
CREATE INDEX idx_model_animation_candidates_model ON model_animation_candidates(model_output, status);
CREATE INDEX idx_model_animation_candidates_animation ON model_animation_candidates(animation_output);
CREATE INDEX idx_model_animation_candidates_confidence ON model_animation_candidates(confidence, relation_source);
CREATE INDEX idx_export_manifest_output ON export_manifest(output);
CREATE INDEX idx_files_kind ON files(kind);");
        }

        private static long ImportAssetCatalog(SqliteConnection connection, SqliteTransaction transaction, string root)
        {
            var path = Path.Combine(root, "asset_catalog.jsonl");
            if (!File.Exists(path))
            {
                return 0;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO assets(kind, resource_kind, name, source_type, container, source, path_id, output, audio_kind, animation_type, skeleton_hash, raw_json)
VALUES ($kind, $resourceKind, $name, $sourceType, $container, $source, $pathId, $output, $audioKind, $animationType, $skeletonHash, $rawJson);";
            var p = AddParameters(command, "$kind", "$resourceKind", "$name", "$sourceType", "$container", "$source", "$pathId", "$output", "$audioKind", "$animationType", "$skeletonHash", "$rawJson");

            long count = 0;
            foreach (var obj in ReadJsonLines(path))
            {
                Set(p, "$kind", S(obj, "kind"));
                Set(p, "$resourceKind", S(obj, "resourceKind"));
                Set(p, "$name", S(obj, "name") ?? S(obj, "text"));
                Set(p, "$sourceType", S(obj, "sourceType") ?? S(obj, "type"));
                Set(p, "$container", S(obj, "container"));
                Set(p, "$source", S(obj, "source") ?? S(obj, "sourceFile"));
                Set(p, "$pathId", I(obj, "pathId"));
                Set(p, "$output", S(obj, "output") ?? S(obj, "outputPath") ?? S(obj, "assetPath"));
                Set(p, "$audioKind", S(obj, "audioKind"));
                Set(p, "$animationType", S(obj, "animationType"));
                Set(p, "$skeletonHash", S(obj, "skeletonHash"));
                Set(p, "$rawJson", obj.ToString(Formatting.None));
                command.ExecuteNonQuery();
                count++;
            }
            return count;
        }

        private static long ImportModelBindingPaths(SqliteConnection connection, SqliteTransaction transaction, string root)
        {
            var path = Path.Combine(root, "asset_catalog.jsonl");
            if (!File.Exists(path))
            {
                return 0;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO model_binding_paths(model_output, binding_path, binding_type, raw_json)
VALUES ($modelOutput, $bindingPath, $bindingType, $rawJson);";
            var p = AddParameters(command, "$modelOutput", "$bindingPath", "$bindingType", "$rawJson");

            long count = 0;
            foreach (var obj in ReadJsonLines(path))
            {
                if (!string.Equals(S(obj, "kind"), "Model", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var output = S(obj, "output");
                if (string.IsNullOrWhiteSpace(output))
                {
                    continue;
                }

                foreach (var entry in EnumerateModelBindingPaths(obj).GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Select(x => x.First()))
                {
                    Set(p, "$modelOutput", output);
                    Set(p, "$bindingPath", entry.Path);
                    Set(p, "$bindingType", entry.Type);
                    Set(p, "$rawJson", entry.RawJson?.ToString(Formatting.None));
                    command.ExecuteNonQuery();
                    count++;
                }
            }

            return count;
        }

        private static IEnumerable<(string Path, string Type, JToken RawJson)> EnumerateModelBindingPaths(JObject obj)
        {
            foreach (var token in obj["bonePaths"]?.OfType<JValue>() ?? Enumerable.Empty<JValue>())
            {
                var path = NormalizeUnityBindingPath(token.Value?.ToString());
                if (!string.IsNullOrWhiteSpace(path))
                {
                    yield return (path, "Bone", token);
                }
            }

            foreach (var token in obj["nodePaths"]?.OfType<JValue>() ?? Enumerable.Empty<JValue>())
            {
                var path = NormalizeUnityBindingPath(token.Value?.ToString());
                if (!string.IsNullOrWhiteSpace(path))
                {
                    yield return (path, "Node", token);
                }
            }
        }

        private static long ImportModelAnimationCandidates(SqliteConnection connection, SqliteTransaction transaction, string root)
        {
            var path = Path.Combine(root, "model_animations.compact.json");
            if (!File.Exists(path))
            {
                return 0;
            }

            JObject index;
            try
            {
                index = JObject.Parse(File.ReadAllText(path));
            }
            catch (JsonException e)
            {
                Logger.Warning($"Skipping invalid compact model-animation index: {e.Message}");
                return 0;
            }

            var modelOutputs = index["models"]?
                .OfType<JObject>()
                .Select(x => new { Id = S(x, "id"), Output = S(x, "output") })
                .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Output))
                .ToDictionary(x => x.Id, x => x.Output, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var animationOutputs = index["animations"]?
                .OfType<JObject>()
                .Select(x => new { Id = S(x, "id"), Output = S(x, "output") })
                .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Output))
                .ToDictionary(x => x.Id, x => x.Output, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO model_animation_candidates(model_output, animation_output, relation_source, confidence, score, status, needs_validation, raw_json)
VALUES ($modelOutput, $animationOutput, $relationSource, $confidence, $score, $status, $needsValidation, $rawJson);";
            var p = AddParameters(command, "$modelOutput", "$animationOutput", "$relationSource", "$confidence", "$score", "$status", "$needsValidation", "$rawJson");

            long count = 0;
            foreach (var modelRef in index["modelAnimationRefs"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var modelId = S(modelRef, "modelId");
                if (string.IsNullOrWhiteSpace(modelId) || !modelOutputs.TryGetValue(modelId, out var modelOutput))
                {
                    continue;
                }

                foreach (var candidate in modelRef["candidates"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    var animationId = S(candidate, "animationId") ?? S(candidate, "id");
                    if (string.IsNullOrWhiteSpace(animationId) || !animationOutputs.TryGetValue(animationId, out var animationOutput))
                    {
                        continue;
                    }

                    var relationSource = S(candidate, "relationSource");
                    var confidence = S(candidate, "confidence");
                    Set(p, "$modelOutput", modelOutput);
                    Set(p, "$animationOutput", animationOutput);
                    Set(p, "$relationSource", relationSource);
                    Set(p, "$confidence", confidence);
                    Set(p, "$score", D(candidate, "score"));
                    Set(p, "$status", string.Equals(relationSource, "explicit", StringComparison.OrdinalIgnoreCase) || string.Equals(confidence, "explicit_unity_reference", StringComparison.OrdinalIgnoreCase) ? "explicit" : "needs_validation");
                    Set(p, "$needsValidation", string.Equals(relationSource, "explicit", StringComparison.OrdinalIgnoreCase) ? 0 : 1);
                    Set(p, "$rawJson", candidate.ToString(Formatting.None));
                    command.ExecuteNonQuery();
                    count++;
                }
            }

            return count;
        }

        private static long ImportExplicitModelAnimationCandidatesFromSourceIndex(SqliteConnection connection, SqliteTransaction transaction, string root)
        {
            var sourceIndex = Path.Combine(root, "unity_source_index.db");
            var catalogPath = Path.Combine(root, "asset_catalog.jsonl");
            if (!File.Exists(sourceIndex) || !File.Exists(catalogPath))
            {
                return 0;
            }

            var catalog = ReadJsonLines(catalogPath).ToList();
            var models = catalog
                .Where(x => string.Equals(S(x, "kind"), "Model", StringComparison.OrdinalIgnoreCase))
                .Where(IsPrefabLikeModel)
                .Select(x => new CatalogAsset(
                    KeyFromCatalog(x),
                    S(x, "name"),
                    S(x, "output"),
                    x))
                .Where(x => x.Key.IsValid && !string.IsNullOrWhiteSpace(x.Output))
                .ToList();
            var animations = catalog
                .Where(x => string.Equals(S(x, "kind"), "Animation", StringComparison.OrdinalIgnoreCase))
                .Select(x => new CatalogAsset(
                    KeyFromCatalog(x),
                    S(x, "name"),
                    S(x, "output"),
                    x))
                .Where(x => x.Key.IsValid && !string.IsNullOrWhiteSpace(x.Output))
                .GroupBy(x => x.Key)
                .ToDictionary(x => x.Key, x => x.First());

            if (models.Count == 0 || animations.Count == 0)
            {
                return 0;
            }

            var graph = SourceAnimationGraph.Load(sourceIndex);
            if (graph.Objects.Count == 0)
            {
                return 0;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO model_animation_candidates(model_output, animation_output, relation_source, confidence, score, status, needs_validation, raw_json)
VALUES ($modelOutput, $animationOutput, 'explicit', 'explicit_unity_source_index', 100, 'explicit', 0, $rawJson);";
            var p = AddParameters(command, "$modelOutput", "$animationOutput", "$rawJson");

            var inserted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long count = 0;
            foreach (var model in models)
            {
                foreach (var relation in graph.FindAnimationClipsForModel(model.Key))
                {
                    if (!animations.TryGetValue(relation.ClipKey, out var animation))
                    {
                        continue;
                    }

                    var pairKey = $"{model.Output}\u001f{animation.Output}";
                    if (!inserted.Add(pairKey))
                    {
                        continue;
                    }

                    var raw = BuildExplicitSourceCandidateJson(animation.Raw, relation);
                    Set(p, "$modelOutput", model.Output);
                    Set(p, "$animationOutput", animation.Output);
                    Set(p, "$rawJson", raw.ToString(Formatting.None));
                    command.ExecuteNonQuery();
                    count++;
                }
            }

            if (count > 0)
            {
                Logger.Info($"SQLite library index added {count} explicit model-animation candidate(s) from unity_source_index.db.");
            }

            return count;
        }

        private static bool IsPrefabLikeModel(JObject obj)
        {
            var role = S(obj, "libraryRole");
            var sourceType = S(obj, "sourceType");
            return string.Equals(role, "PrefabPrimary", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sourceType, "GameObject", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sourceType, "Animator", StringComparison.OrdinalIgnoreCase);
        }

        private static JObject BuildExplicitSourceCandidateJson(JObject animation, SourceAnimationRelation relation)
        {
            return new JObject
            {
                ["name"] = S(animation, "name"),
                ["output"] = S(animation, "output"),
                ["animationAsset"] = S(animation, "animationAsset"),
                ["source"] = S(animation, "source"),
                ["animationType"] = S(animation, "animationType"),
                ["animationCapability"] = S(animation, "animationCapability"),
                ["relation"] = relation.Relation,
                ["relationSource"] = "explicit",
                ["confidence"] = "explicit_unity_source_index",
                ["score"] = 100,
                ["requiresHumanoidBake"] = RequiresHumanoidBake(animation),
                ["matchReasons"] = new JArray(
                    "Unity source index resolves GameObject/Animator/Animation PPtr references to this AnimationClip.",
                    relation.Reason),
            };
        }

        private static bool RequiresHumanoidBake(JObject animation)
        {
            var type = S(animation, "animationType") ?? string.Empty;
            var transformBindingCount = I(animation, "transformBindingCount") ?? 0;
            var humanoidBindingCount = I(animation, "humanoidBindingCount") ?? 0;
            if (transformBindingCount > 0 && humanoidBindingCount == 0 && type.Contains("Transform", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return humanoidBindingCount > 0
                || type.Contains("Humanoid", StringComparison.OrdinalIgnoreCase);
        }

        private static void ImportUnityRelations(SqliteConnection connection, SqliteTransaction transaction, string root, Dictionary<string, long> counts)
        {
            var path = Path.Combine(root, "unity_relations.jsonl");
            if (File.Exists(path))
            {
                foreach (var obj in ReadJsonLines(path))
                {
                    var kind = S(obj, "kind");
                    if (string.Equals(kind, "asset", StringComparison.OrdinalIgnoreCase))
                    {
                        InsertUnityAsset(connection, transaction, obj);
                        counts["unityAssets"]++;
                    }
                    else if (string.Equals(kind, "relation", StringComparison.OrdinalIgnoreCase))
                    {
                        InsertUnityRelation(connection, transaction, obj);
                        counts["unityRelations"]++;
                    }
                    else if (string.Equals(kind, "animationBindings", StringComparison.OrdinalIgnoreCase))
                    {
                        var bindingId = InsertAnimationBinding(connection, transaction, obj, "unity_relations.jsonl");
                        counts["animationBindingPaths"] += InsertAnimationBindingPaths(connection, transaction, bindingId, obj);
                        counts["animationBindings"]++;
                    }
                }
            }

            var bindingPath = Path.Combine(root, "animation_bindings.jsonl");
            if (File.Exists(bindingPath))
            {
                foreach (var obj in ReadJsonLines(bindingPath))
                {
                    var bindingId = InsertAnimationBinding(connection, transaction, obj, "animation_bindings.jsonl");
                    counts["animationBindingPaths"] += InsertAnimationBindingPaths(connection, transaction, bindingId, obj);
                    counts["animationBindings"]++;
                }
            }
        }

        private static long ImportExportManifest(SqliteConnection connection, SqliteTransaction transaction, string root)
        {
            var path = Path.Combine(root, "export_manifest.jsonl");
            if (!File.Exists(path))
            {
                return 0;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO export_manifest(type, name, output, source, path_id, raw_json)
VALUES ($type, $name, $output, $source, $pathId, $rawJson);";
            var p = AddParameters(command, "$type", "$name", "$output", "$source", "$pathId", "$rawJson");
            long count = 0;
            foreach (var obj in ReadJsonLines(path))
            {
                Set(p, "$type", S(obj, "type") ?? S(obj, "kind"));
                Set(p, "$name", S(obj, "name"));
                Set(p, "$output", S(obj, "output") ?? S(obj, "outputPath"));
                Set(p, "$source", S(obj, "source") ?? S(obj, "sourceFile"));
                Set(p, "$pathId", I(obj, "pathId"));
                Set(p, "$rawJson", obj.ToString(Formatting.None));
                command.ExecuteNonQuery();
                count++;
            }
            return count;
        }

        private static long ImportJsonDocuments(SqliteConnection connection, SqliteTransaction transaction, string root)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT OR REPLACE INTO json_documents(name, path, raw_json)
VALUES ($name, $path, $rawJson);";
            var p = AddParameters(command, "$name", "$path", "$rawJson");
            long count = 0;

            foreach (var name in JsonDocumentNames)
            {
                var path = Path.Combine(root, name);
                if (!File.Exists(path))
                {
                    continue;
                }

                Set(p, "$name", name);
                Set(p, "$path", MakeRelative(root, path));
                Set(p, "$rawJson", File.ReadAllText(path));
                command.ExecuteNonQuery();
                count++;
            }

            foreach (var path in Directory.EnumerateFiles(root, "*.audio.json", SearchOption.AllDirectories))
            {
                Set(p, "$name", MakeRelative(root, path).Replace('\\', '/'));
                Set(p, "$path", MakeRelative(root, path));
                Set(p, "$rawJson", File.ReadAllText(path));
                command.ExecuteNonQuery();
                count++;
            }

            return count;
        }

        private static long ImportFiles(SqliteConnection connection, SqliteTransaction transaction, string root, string dbPath)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT OR REPLACE INTO files(path, kind, size, modified_utc)
VALUES ($path, $kind, $size, $modifiedUtc);";
            var p = AddParameters(command, "$path", "$kind", "$size", "$modifiedUtc");
            var fullDbPath = Path.GetFullPath(dbPath);
            long count = 0;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var fullPath = Path.GetFullPath(file);
                if (string.Equals(fullPath, fullDbPath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(fullPath, fullDbPath + "-wal", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(fullPath, fullDbPath + "-shm", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var info = new FileInfo(file);
                Set(p, "$path", MakeRelative(root, file));
                Set(p, "$kind", ClassifyFile(root, file));
                Set(p, "$size", info.Length);
                Set(p, "$modifiedUtc", info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();
                count++;
            }
            return count;
        }

        private static void InsertUnityAsset(SqliteConnection connection, SqliteTransaction transaction, JObject obj)
        {
            var asset = obj["asset"] as JObject ?? obj;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO unity_assets(type, name, source, file, path_id, container, exported, raw_json)
VALUES ($type, $name, $source, $file, $pathId, $container, $exported, $rawJson);";
            var p = AddParameters(command, "$type", "$name", "$source", "$file", "$pathId", "$container", "$exported", "$rawJson");
            Set(p, "$type", S(asset, "type"));
            Set(p, "$name", S(asset, "name"));
            Set(p, "$source", S(asset, "source"));
            Set(p, "$file", S(asset, "file"));
            Set(p, "$pathId", I(asset, "pathId"));
            Set(p, "$container", S(asset, "container") ?? S(obj, "container"));
            Set(p, "$exported", B(obj, "exported"));
            Set(p, "$rawJson", obj.ToString(Formatting.None));
            command.ExecuteNonQuery();
        }

        private static void InsertUnityRelation(SqliteConnection connection, SqliteTransaction transaction, JObject obj)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO unity_relations(relation, confidence, from_type, from_name, from_source, from_path_id, to_type, to_name, to_source, to_path_id, target_count, raw_json)
VALUES ($relation, $confidence, $fromType, $fromName, $fromSource, $fromPathId, $toType, $toName, $toSource, $toPathId, $targetCount, $rawJson);";
            var p = AddParameters(command, "$relation", "$confidence", "$fromType", "$fromName", "$fromSource", "$fromPathId", "$toType", "$toName", "$toSource", "$toPathId", "$targetCount", "$rawJson");
            var from = obj["from"] as JObject;
            var to = obj["to"] as JObject;
            Set(p, "$relation", S(obj, "relation"));
            Set(p, "$confidence", S(obj, "confidence"));
            Set(p, "$fromType", S(from, "type"));
            Set(p, "$fromName", S(from, "name"));
            Set(p, "$fromSource", S(from, "source"));
            Set(p, "$fromPathId", I(from, "pathId"));
            Set(p, "$toType", S(to, "type"));
            Set(p, "$toName", S(to, "name"));
            Set(p, "$toSource", S(to, "source"));
            Set(p, "$toPathId", I(to, "pathId"));
            Set(p, "$targetCount", obj["targets"] is JArray targets ? targets.Count : null);
            Set(p, "$rawJson", obj.ToString(Formatting.None));
            command.ExecuteNonQuery();
        }

        private static long InsertAnimationBinding(SqliteConnection connection, SqliteTransaction transaction, JObject obj, string sourceFile)
        {
            var animation = obj["animation"] as JObject ?? obj;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO animation_bindings(animation_name, animation_source, animation_path_id, binding_count, has_muscle_clip, source_file, raw_json)
VALUES ($animationName, $animationSource, $animationPathId, $bindingCount, $hasMuscleClip, $sourceFile, $rawJson);
SELECT last_insert_rowid();";
            var p = AddParameters(command, "$animationName", "$animationSource", "$animationPathId", "$bindingCount", "$hasMuscleClip", "$sourceFile", "$rawJson");
            Set(p, "$animationName", S(animation, "name") ?? S(obj, "animationName"));
            Set(p, "$animationSource", S(animation, "source") ?? S(obj, "source"));
            Set(p, "$animationPathId", I(animation, "pathId") ?? I(obj, "pathId"));
            Set(p, "$bindingCount", I(obj, "bindingCount") ?? (obj["bindings"] is JArray bindings ? bindings.Count : null));
            Set(p, "$hasMuscleClip", B(obj, "hasMuscleClip"));
            Set(p, "$sourceFile", sourceFile);
            Set(p, "$rawJson", obj.ToString(Formatting.None));
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private static long InsertAnimationBindingPaths(SqliteConnection connection, SqliteTransaction transaction, long bindingId, JObject obj)
        {
            var paths = EnumerateAnimationBindingPaths(obj)
                .Select(x => new
                {
                    Path = NormalizeUnityBindingPath(x.Path),
                    x.Type,
                    x.RawJson,
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Path))
                .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToArray();
            if (paths.Length == 0)
            {
                return 0;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO animation_binding_paths(animation_binding_id, binding_path, binding_type, raw_json)
VALUES ($animationBindingId, $bindingPath, $bindingType, $rawJson);";
            var p = AddParameters(command, "$animationBindingId", "$bindingPath", "$bindingType", "$rawJson");
            long count = 0;
            foreach (var path in paths)
            {
                Set(p, "$animationBindingId", bindingId);
                Set(p, "$bindingPath", path.Path);
                Set(p, "$bindingType", path.Type);
                Set(p, "$rawJson", path.RawJson?.ToString(Formatting.None));
                command.ExecuteNonQuery();
                count++;
            }
            return count;
        }

        private static IEnumerable<(string Path, string Type, JToken RawJson)> EnumerateAnimationBindingPaths(JObject obj)
        {
            var animation = obj["animation"] as JObject ?? obj;
            if (animation["transformBindingPaths"] is JArray transformPaths)
            {
                foreach (var path in transformPaths.OfType<JValue>())
                {
                    yield return (path.Value?.ToString(), "Transform", path);
                }
            }

            if (obj["bindings"] is JArray bindings)
            {
                foreach (var binding in bindings.OfType<JObject>())
                {
                    yield return (S(binding, "path"), S(binding, "type"), binding);
                }
            }
        }

        private static string NormalizeUnityBindingPath(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/').Trim('/');
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

        private static void WriteSummary(string root, string dbPath, Dictionary<string, long> counts)
        {
            var summary = new JObject
            {
                ["schemaVersion"] = 1,
                ["database"] = dbPath,
                ["root"] = root,
                ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["rule"] = "索引要全，导出要精。SQLite v1 面向已导出的 Library/AudioLibrary 文件；后续会扩展到原始 Unity CAB/Object/PPtr 全量索引。",
                ["counts"] = JObject.FromObject(counts)
            };
            File.WriteAllText(Path.Combine(root, "sqlite_index_summary.json"), summary.ToString(Formatting.Indented));

            var readme = Path.Combine(root, "SQLITE_INDEX_README.md");
            File.WriteAllText(readme,
                "# SQLite Library Index\n\n" +
                "这是 AnimeStudio 的素材库索引数据库。当前 v1 从已经导出的 Library/AudioLibrary 文件构建，目标是让后续浏览、筛选、调试、二次打包不用反复扫描大量 JSONL。\n\n" +
                "核心规则：索引要全，导出要精。进入索引不代表默认导出或推荐使用；导出仍按 Unity 显式关系、结构兼容和严格匹配规则执行。\n\n" +
                "桌面工具默认优先读取 SQLite。高频查询，例如模型列表、动画 binding path 定向匹配、缩略图状态、筛选统计，应尽量走 SQLite 索引；JSON/JSONL 保留给人工排查、兼容旧流程和重新建库。\n\n" +
                "主要表：`assets`、`unity_assets`、`unity_relations`、`animation_bindings`、`animation_binding_paths`、`export_manifest`、`json_documents`、`files`。每条结构化记录都尽量保留 `raw_json`，方便后续无损迁移。\n\n" +
                "音频说明：当前 AudioLibrary 可以导出原始 `.fsb` 等文件；FMOD/native 转 WAV 作为后续批量转换阶段，不阻塞索引与素材库建设。\n");
        }

        private static void InsertMetadata(SqliteConnection connection, SqliteTransaction transaction, string key, string value)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR REPLACE INTO metadata(key, value) VALUES ($key, $value);";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }

        private static void Execute(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static Dictionary<string, SqliteParameter> AddParameters(SqliteCommand command, params string[] names)
        {
            var result = new Dictionary<string, SqliteParameter>(StringComparer.Ordinal);
            foreach (var name in names)
            {
                result[name] = command.Parameters.Add(name, SqliteType.Text);
            }
            return result;
        }

        private static void Set(Dictionary<string, SqliteParameter> parameters, string name, object value)
        {
            parameters[name].Value = value ?? DBNull.Value;
        }

        private static string S(JObject obj, string name)
        {
            return obj?[name]?.Type == JTokenType.Null ? null : obj?[name]?.ToString();
        }

        private static long? I(JObject obj, string name)
        {
            var token = obj?[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            return token.Type == JTokenType.Integer || long.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                ? token.Value<long>()
                : null;
        }

        private static double? D(JObject obj, string name)
        {
            var token = obj?[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            return token.Type == JTokenType.Float
                || token.Type == JTokenType.Integer
                || double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                ? token.Value<double>()
                : null;
        }

        private static int? B(JObject obj, string name)
        {
            var token = obj?[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }
            return token.Value<bool>() ? 1 : 0;
        }

        private static string MakeRelative(string root, string path)
        {
            return Path.GetRelativePath(root, path).Replace('\\', '/');
        }

        private static SourceKey KeyFromCatalog(JObject obj)
        {
            return new SourceKey(Path.GetFileName(S(obj, "source") ?? string.Empty), I(obj, "pathId") ?? 0);
        }

        private static SourceKey KeyFromRelation(SqliteDataReader reader, int fileOrdinal, int pathIdOrdinal)
        {
            return new SourceKey(reader.IsDBNull(fileOrdinal) ? "" : reader.GetString(fileOrdinal), reader.GetInt64(pathIdOrdinal));
        }

        private readonly record struct SourceKey(string File, long PathId)
        {
            public bool IsValid => !string.IsNullOrWhiteSpace(File) && PathId != 0;

            public SourceKey Normalize()
            {
                return new SourceKey((File ?? string.Empty).Trim().ToLowerInvariant(), PathId);
            }
        }

        private sealed record CatalogAsset(SourceKey Key, string Name, string Output, JObject Raw);

        private sealed record SourceObject(string Type, string Name);

        private sealed record SourceAnimationRelation(SourceKey ClipKey, string Relation, string Reason);

        private sealed class SourceAnimationGraph
        {
            private const int MaxHierarchyDepth = 96;
            private readonly Dictionary<SourceKey, List<SourceKey>> _gameObjectComponents = new();
            private readonly Dictionary<SourceKey, SourceKey> _componentGameObjects = new();
            private readonly Dictionary<SourceKey, List<SourceKey>> _transformChildren = new();
            private readonly Dictionary<SourceKey, List<SourceKey>> _animatorControllers = new();
            private readonly Dictionary<SourceKey, List<SourceKey>> _animationClips = new();
            private readonly Dictionary<SourceKey, List<SourceKey>> _controllerClips = new();
            private readonly Dictionary<SourceKey, List<SourceKey>> _overrideBaseControllers = new();
            private readonly Dictionary<SourceKey, List<SourceKey>> _overrideClips = new();

            public Dictionary<SourceKey, SourceObject> Objects { get; } = new();

            public static SourceAnimationGraph Load(string sourceIndex)
            {
                var graph = new SourceAnimationGraph();
                using var connection = new SqliteConnection($"Data Source={sourceIndex};Mode=ReadOnly");
                connection.Open();
                graph.LoadObjects(connection);
                graph.LoadRelations(connection);
                return graph;
            }

            public IEnumerable<SourceAnimationRelation> FindAnimationClipsForModel(SourceKey modelKey)
            {
                modelKey = modelKey.Normalize();
                if (!Objects.TryGetValue(modelKey, out var modelObject))
                {
                    yield break;
                }

                var seen = new HashSet<SourceKey>();
                if (string.Equals(modelObject.Type, "Animator", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var clip in FindAnimatorClips(modelKey))
                    {
                        if (seen.Add(clip))
                        {
                            yield return new SourceAnimationRelation(clip, "animator.controller.clip", "Model source is an Animator with an explicit RuntimeAnimatorController.");
                        }
                    }
                    yield break;
                }

                var queue = new Queue<(SourceKey GameObject, int Depth)>();
                var visitedGameObjects = new HashSet<SourceKey>();
                queue.Enqueue((modelKey, 0));
                while (queue.Count > 0)
                {
                    var (gameObject, depth) = queue.Dequeue();
                    if (!visitedGameObjects.Add(gameObject) || depth > MaxHierarchyDepth)
                    {
                        continue;
                    }

                    foreach (var component in GetMany(_gameObjectComponents, gameObject))
                    {
                        if (!Objects.TryGetValue(component, out var componentObject))
                        {
                            continue;
                        }

                        if (string.Equals(componentObject.Type, "Animator", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (var clip in FindAnimatorClips(component))
                            {
                                if (seen.Add(clip))
                                {
                                    yield return new SourceAnimationRelation(clip, "gameObject.hierarchy.animator.controller.clip", $"Animator '{componentObject.Name}' is attached to the exported GameObject hierarchy.");
                                }
                            }
                        }
                        else if (string.Equals(componentObject.Type, "Animation", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (var clip in GetMany(_animationClips, component))
                            {
                                if (seen.Add(clip))
                                {
                                    yield return new SourceAnimationRelation(clip, "gameObject.hierarchy.animation.clip", $"Animation component '{componentObject.Name}' explicitly references this clip.");
                                }
                            }
                        }
                        else if (string.Equals(componentObject.Type, "Transform", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (var childTransform in GetMany(_transformChildren, component))
                            {
                                if (_componentGameObjects.TryGetValue(childTransform, out var childGameObject))
                                {
                                    queue.Enqueue((childGameObject, depth + 1));
                                }
                            }
                        }
                    }
                }
            }

            private IEnumerable<SourceKey> FindAnimatorClips(SourceKey animator)
            {
                foreach (var controller in GetMany(_animatorControllers, animator))
                {
                    foreach (var clip in FindControllerClips(controller, new HashSet<SourceKey>()))
                    {
                        yield return clip;
                    }
                }
            }

            private IEnumerable<SourceKey> FindControllerClips(SourceKey controller, HashSet<SourceKey> visited)
            {
                if (!visited.Add(controller))
                {
                    yield break;
                }

                foreach (var clip in GetMany(_controllerClips, controller))
                {
                    yield return clip;
                }

                foreach (var clip in GetMany(_overrideClips, controller))
                {
                    yield return clip;
                }

                foreach (var baseController in GetMany(_overrideBaseControllers, controller))
                {
                    foreach (var clip in FindControllerClips(baseController, visited))
                    {
                        yield return clip;
                    }
                }
            }

            private void LoadObjects(SqliteConnection connection)
            {
                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT serialized_file, path_id, type, name
FROM source_objects
WHERE type IN ('GameObject','Transform','Animator','Animation','AnimatorController','AnimatorOverrideController','AnimationClip');";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var key = KeyFromRelation(reader, 0, 1).Normalize();
                    Objects[key] = new SourceObject(reader.GetString(2), reader.IsDBNull(3) ? "" : reader.GetString(3));
                }
            }

            private void LoadRelations(SqliteConnection connection)
            {
                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT relation, from_file, from_path_id, to_file, to_path_id
FROM source_relations
WHERE relation IN (
  'gameObject.component',
  'component.gameObject',
  'transform.child',
  'animator.controller',
  'animation.clip',
  'animatorController.clip',
  'animatorOverrideController.baseController',
  'animatorOverrideController.overrideClip',
  'animatorOverrideController.originalClip'
);";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var relation = reader.GetString(0);
                    var from = KeyFromRelation(reader, 1, 2).Normalize();
                    var to = KeyFromRelation(reader, 3, 4).Normalize();
                    if (!from.IsValid || !to.IsValid)
                    {
                        continue;
                    }

                    switch (relation)
                    {
                        case "gameObject.component":
                            Add(_gameObjectComponents, from, to);
                            break;
                        case "component.gameObject":
                            _componentGameObjects[from] = to;
                            break;
                        case "transform.child":
                            Add(_transformChildren, from, to);
                            break;
                        case "animator.controller":
                            Add(_animatorControllers, from, to);
                            break;
                        case "animation.clip":
                            Add(_animationClips, from, to);
                            break;
                        case "animatorController.clip":
                            Add(_controllerClips, from, to);
                            break;
                        case "animatorOverrideController.baseController":
                            Add(_overrideBaseControllers, from, to);
                            break;
                        case "animatorOverrideController.overrideClip":
                        case "animatorOverrideController.originalClip":
                            Add(_overrideClips, from, to);
                            break;
                    }
                }
            }

            private static IEnumerable<SourceKey> GetMany(Dictionary<SourceKey, List<SourceKey>> map, SourceKey key)
            {
                return map.TryGetValue(key, out var values) ? values : Enumerable.Empty<SourceKey>();
            }

            private static void Add(Dictionary<SourceKey, List<SourceKey>> map, SourceKey key, SourceKey value)
            {
                if (!map.TryGetValue(key, out var list))
                {
                    list = new List<SourceKey>();
                    map[key] = list;
                }

                list.Add(value);
            }
        }

        private static string ClassifyFile(string root, string file)
        {
            var relative = MakeRelative(root, file);
            var extension = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
            if (relative.StartsWith("Models/", StringComparison.OrdinalIgnoreCase)) return "Model";
            if (relative.StartsWith("Animations/", StringComparison.OrdinalIgnoreCase)) return "Animation";
            if (relative.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase)) return "Texture";
            if (relative.StartsWith("Audio/", StringComparison.OrdinalIgnoreCase)) return "Audio";
            if (extension is "gltf" or "glb" or "fbx") return "Model";
            if (extension is "png" or "jpg" or "jpeg" or "dds" or "ktx" or "ktx2" or "rawtex") return "Texture";
            if (extension is "fsb" or "wav" or "ogg" or "mp3") return "Audio";
            if (extension is "json" or "jsonl" or "md" or "db") return "Index";
            return "Other";
        }
    }
}
