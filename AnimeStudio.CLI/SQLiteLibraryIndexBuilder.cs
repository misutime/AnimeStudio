using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
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
        private const int SummaryQueryTimeoutSeconds = 60;

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

        private static readonly HashSet<string> TextureFileExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png",
            ".jpg",
            ".jpeg",
            ".bmp",
            ".tga",
            ".dds",
            ".ktx",
            ".ktx2",
            ".webp",
            ".rawtex"
        };

        public static string Build(string exportRoot, string indexPath = null, bool skipFileIndex = false, bool skipSidecarScan = false, bool skipJsonDocuments = false, string sourceIndexPath = null)
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
            var preservedBakeCache = LoadExistingAnimationBakeCacheRows(dbPath);
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }

            var totalWatch = Stopwatch.StartNew();
            Logger.Info($"Building SQLite library index: {dbPath}");
            var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();
            Execute(connection, "PRAGMA journal_mode = WAL;");
            Execute(connection, "PRAGMA synchronous = NORMAL;");

            long RunCountStage(string name, Func<long> work)
            {
                var watch = Stopwatch.StartNew();
                var count = work();
                Logger.Info($"SQLite index stage '{name}' finished in {watch.Elapsed.TotalSeconds:F1}s; rows={count}");
                return count;
            }

            void RunStage(string name, Action work)
            {
                var watch = Stopwatch.StartNew();
                work();
                Logger.Info($"SQLite index stage '{name}' finished in {watch.Elapsed.TotalSeconds:F1}s");
            }

            RunStage("create schema", () => CreateSchema(connection));

            using var transaction = connection.BeginTransaction();
            InsertMetadata(connection, transaction, "schemaVersion", "1");
            InsertMetadata(connection, transaction, "root", root);
            InsertMetadata(connection, transaction, "createdUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            InsertMetadata(connection, transaction, "rule", "索引要全，导出要精。SQLite v1 indexes exported Library/AudioLibrary artifacts; raw Unity source graph indexing will extend this schema later.");

            var assetCatalogRows = RunCountStage("asset catalog", () => ImportAssetCatalog(connection, transaction, root));
            counts["textureAssets"] = RunCountStage("texture assets", () => ImportTextureAssets(connection, transaction, root));
            counts["assets"] = assetCatalogRows + counts["textureAssets"];
            counts["assetCatalog"] = assetCatalogRows;
            counts["modelBindingPaths"] = RunCountStage("model binding paths", () => ImportModelBindingPaths(connection, transaction, root));
            counts["unityAssets"] = 0;
            counts["unityRelations"] = 0;
            counts["animationBindings"] = 0;
            counts["animationBindingPaths"] = 0;
            RunStage("unity relations", () => ImportUnityRelations(connection, transaction, root, counts));
            counts["modelAnimationCandidates"] = RunCountStage("model animation candidates sidecar", () => ImportModelAnimationCandidates(connection, transaction, root));
            counts["explicitSourceModelAnimationCandidates"] = RunCountStage("model animation candidates source index", () => ImportExplicitModelAnimationCandidatesFromSourceIndex(connection, transaction, root, sourceIndexPath));
            counts["modelAnimationCandidates"] += counts["explicitSourceModelAnimationCandidates"];
            counts["exportManifest"] = RunCountStage("export manifest", () => ImportExportManifest(connection, transaction, root));
            counts["jsonDocuments"] = skipJsonDocuments ? 0 : RunCountStage("json documents", () => ImportJsonDocuments(connection, transaction, root));
            counts["files"] = skipFileIndex ? 0 : RunCountStage("files", () => ImportFiles(connection, transaction, root, dbPath));
            counts["animationBakeCachePreserved"] = RunCountStage("animation bake cache preserved", () => ImportPreservedAnimationBakeCache(connection, transaction, preservedBakeCache));
            counts["filesSkipped"] = skipFileIndex ? 1 : 0;
            counts["sidecarScanSkipped"] = skipSidecarScan ? 1 : 0;
            counts["jsonDocumentsSkipped"] = skipJsonDocuments ? 1 : 0;
            if (skipFileIndex)
            {
                Logger.Info("SQLite index stage 'files' skipped by --skip_sqlite_file_index.");
            }
            if (skipJsonDocuments)
            {
                Logger.Info("SQLite index stage 'json documents' skipped by --skip_sqlite_json_documents.");
            }

            RunStage("commit imports", () => transaction.Commit());
            RunStage("create sql indexes", () => CreateIndexes(connection));
            counts["modelAnimationCandidateModelSummary"] = RunCountStage("model animation candidate summaries", () => BuildModelAnimationCandidateSummaries(connection));

            RunStage("write summary", () => WriteSummary(connection, root, dbPath, counts, skipFileIndex, skipSidecarScan, sourceIndexPath));
            Logger.Info($"SQLite library index written: {dbPath}; elapsed={totalWatch.Elapsed.TotalSeconds:F1}s");
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
    relation_source TEXT NOT NULL CHECK(relation_source = 'explicit'),
    confidence TEXT,
    score REAL,
    status TEXT,
    needs_validation INTEGER,
    raw_json TEXT NOT NULL
);
CREATE TABLE model_animation_candidate_model_summary (
    model_output TEXT PRIMARY KEY,
    explicit_count INTEGER NOT NULL,
    direct_preview_count INTEGER NOT NULL,
    internal_humanoid_count INTEGER NOT NULL,
    legacy_unity_bake_count INTEGER NOT NULL
);
CREATE TABLE model_animation_candidate_animation_summary (
    animation_output TEXT PRIMARY KEY,
    explicit_model_count INTEGER NOT NULL,
    direct_preview_model_count INTEGER NOT NULL,
    internal_humanoid_model_count INTEGER NOT NULL,
    legacy_unity_bake_model_count INTEGER NOT NULL
);
CREATE TABLE animation_preview_cache (
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
);
CREATE TABLE animation_bake_cache (
    model_output TEXT NOT NULL,
    animation_output TEXT NOT NULL,
    status TEXT NOT NULL,
    request_path TEXT,
    result_path TEXT,
    baked_gltf_path TEXT,
    baked_fbx_path TEXT,
    message TEXT,
    bake_mode TEXT,
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
            var statements = new[]
            {
                "CREATE INDEX idx_assets_kind ON assets(kind, resource_kind);",
                "CREATE INDEX idx_assets_name ON assets(name);",
                "CREATE INDEX idx_assets_source ON assets(source, path_id);",
                "CREATE INDEX idx_assets_output ON assets(output);",
                "CREATE INDEX idx_assets_kind_output ON assets(kind, output);",
                "CREATE INDEX idx_unity_assets_source ON unity_assets(source, path_id);",
                "CREATE INDEX idx_unity_assets_name ON unity_assets(name);",
                "CREATE INDEX idx_unity_relations_from ON unity_relations(from_source, from_path_id);",
                "CREATE INDEX idx_unity_relations_to ON unity_relations(to_source, to_path_id);",
                "CREATE INDEX idx_unity_relations_relation ON unity_relations(relation, confidence);",
                "CREATE INDEX idx_animation_bindings_name ON animation_bindings(animation_name);",
                "CREATE INDEX idx_animation_binding_paths_path ON animation_binding_paths(binding_path);",
                "CREATE INDEX idx_animation_binding_paths_animation ON animation_binding_paths(animation_binding_id);",
                "CREATE INDEX idx_model_binding_paths_model ON model_binding_paths(model_output);",
                "CREATE INDEX idx_model_binding_paths_path ON model_binding_paths(binding_path);",
                "CREATE INDEX idx_model_animation_candidates_model ON model_animation_candidates(model_output, status);",
                "CREATE INDEX idx_model_animation_candidates_animation ON model_animation_candidates(animation_output);",
                "CREATE INDEX idx_model_animation_candidates_confidence ON model_animation_candidates(confidence, relation_source);",
                "CREATE INDEX idx_model_animation_candidates_source_model ON model_animation_candidates(relation_source, model_output, status);",
                "CREATE INDEX idx_model_animation_candidates_source_animation ON model_animation_candidates(relation_source, animation_output);",
                "CREATE INDEX idx_model_animation_candidate_model_summary_count ON model_animation_candidate_model_summary(explicit_count);",
                "CREATE INDEX idx_model_animation_candidate_animation_summary_count ON model_animation_candidate_animation_summary(explicit_model_count);",
                "CREATE INDEX idx_export_manifest_output ON export_manifest(output);",
                "CREATE INDEX idx_files_kind ON files(kind);",
            };

            foreach (var statement in statements)
            {
                var watch = Stopwatch.StartNew();
                Execute(connection, statement);
                Logger.Info($"SQLite index statement finished in {watch.Elapsed.TotalSeconds:F1}s: {statement}");
            }
        }

        private static List<AnimationBakeCacheRow> LoadExistingAnimationBakeCacheRows(string dbPath)
        {
            var rows = new List<AnimationBakeCacheRow>();
            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            {
                return rows;
            }

            try
            {
                using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
                connection.Open();
                if (!HasTable(connection, "animation_bake_cache"))
                {
                    return rows;
                }

                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT model_output, animation_output, status, request_path, result_path, baked_gltf_path, baked_fbx_path, message, bake_mode, updated_utc
FROM animation_bake_cache;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new AnimationBakeCacheRow(
                        ReadNullableString(reader, 0),
                        ReadNullableString(reader, 1),
                        ReadNullableString(reader, 2),
                        ReadNullableString(reader, 3),
                        ReadNullableString(reader, 4),
                        ReadNullableString(reader, 5),
                        ReadNullableString(reader, 6),
                        ReadNullableString(reader, 7),
                        ReadNullableString(reader, 8),
                        ReadNullableString(reader, 9)));
                }

                SqliteConnection.ClearPool(connection);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Unable to preserve existing animation_bake_cache before SQLite rebuild: {ex.Message}");
            }

            return rows
                .Where(x => !string.IsNullOrWhiteSpace(x.ModelOutput) && !string.IsNullOrWhiteSpace(x.AnimationOutput))
                .GroupBy(x => $"{x.ModelOutput}|{x.AnimationOutput}", StringComparer.OrdinalIgnoreCase)
                .Select(x => x.OrderByDescending(row => row.UpdatedUtc, StringComparer.OrdinalIgnoreCase).First())
                .ToList();
        }

        private static long ImportPreservedAnimationBakeCache(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<AnimationBakeCacheRow> rows)
        {
            if (rows == null || rows.Count == 0)
            {
                return 0;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT OR REPLACE INTO animation_bake_cache(model_output, animation_output, status, request_path, result_path, baked_gltf_path, baked_fbx_path, message, bake_mode, updated_utc)
VALUES ($modelOutput, $animationOutput, $status, $requestPath, $resultPath, $bakedGltfPath, $bakedFbxPath, $message, $bakeMode, $updatedUtc);";
            var p = AddParameters(command, "$modelOutput", "$animationOutput", "$status", "$requestPath", "$resultPath", "$bakedGltfPath", "$bakedFbxPath", "$message", "$bakeMode", "$updatedUtc");

            long count = 0;
            foreach (var row in rows)
            {
                Set(p, "$modelOutput", row.ModelOutput);
                Set(p, "$animationOutput", row.AnimationOutput);
                Set(p, "$status", row.Status);
                Set(p, "$requestPath", row.RequestPath);
                Set(p, "$resultPath", row.ResultPath);
                Set(p, "$bakedGltfPath", row.BakedGltfPath);
                Set(p, "$bakedFbxPath", row.BakedFbxPath);
                Set(p, "$message", row.Message);
                Set(p, "$bakeMode", row.BakeMode);
                Set(p, "$updatedUtc", row.UpdatedUtc);
                command.ExecuteNonQuery();
                count++;
            }

            return count;
        }

        private static string ReadNullableString(SqliteDataReader reader, int index)
        {
            return reader.IsDBNull(index) ? null : reader.GetString(index);
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

        private static long ImportTextureAssets(SqliteConnection connection, SqliteTransaction transaction, string root)
        {
            var textureRoot = Path.Combine(root, "Textures");
            if (!Directory.Exists(textureRoot))
            {
                return 0;
            }

            var existingOutputs = LoadExistingAssetOutputKeys(connection, root);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO assets(kind, resource_kind, name, source_type, container, source, path_id, output, audio_kind, animation_type, skeleton_hash, raw_json)
VALUES ($kind, $resourceKind, $name, $sourceType, $container, $source, $pathId, $output, $audioKind, $animationType, $skeletonHash, $rawJson);";
            var p = AddParameters(command, "$kind", "$resourceKind", "$name", "$sourceType", "$container", "$source", "$pathId", "$output", "$audioKind", "$animationType", "$skeletonHash", "$rawJson");

            long count = 0;
            foreach (var file in Directory.EnumerateFiles(textureRoot, "*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(file);
                if (!TextureFileExtensions.Contains(extension))
                {
                    continue;
                }

                var relativeOutput = MakeRelative(root, file);
                var outputKey = NormalizeAssetOutputKey(root, relativeOutput);
                if (!existingOutputs.Add(outputKey))
                {
                    continue;
                }

                var textureRelative = MakeRelative(textureRoot, file);
                var bucket = GetTextureBucket(textureRelative);
                var info = new FileInfo(file);
                var raw = new JObject
                {
                    ["kind"] = "Texture",
                    ["resourceKind"] = bucket,
                    ["name"] = Path.GetFileNameWithoutExtension(file) ?? "",
                    ["sourceType"] = "TextureFile",
                    ["source"] = relativeOutput,
                    ["output"] = relativeOutput,
                    ["modelPreview"] = relativeOutput,
                    ["texturePath"] = textureRelative.Replace('\\', '/'),
                    ["extension"] = extension.TrimStart('.').ToLowerInvariant(),
                    ["fileSize"] = info.Length,
                    ["indexedFrom"] = "texture_directory_index",
                    ["status"] = "exported_texture_file"
                };

                Set(p, "$kind", "Texture");
                Set(p, "$resourceKind", bucket);
                Set(p, "$name", Path.GetFileNameWithoutExtension(file) ?? "");
                Set(p, "$sourceType", "TextureFile");
                Set(p, "$container", null);
                Set(p, "$source", relativeOutput);
                Set(p, "$pathId", null);
                Set(p, "$output", relativeOutput);
                Set(p, "$audioKind", null);
                Set(p, "$animationType", null);
                Set(p, "$skeletonHash", null);
                Set(p, "$rawJson", raw.ToString(Formatting.None));
                command.ExecuteNonQuery();
                count++;
            }

            return count;
        }

        private static HashSet<string> LoadExistingAssetOutputKeys(SqliteConnection connection, string root)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT output FROM assets WHERE output IS NOT NULL AND output <> '';";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(NormalizeAssetOutputKey(root, reader.GetString(0)));
            }

            return result;
        }

        private static string GetTextureBucket(string textureRelativePath)
        {
            var parts = (textureRelativePath ?? string.Empty)
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1 ? parts[0] : "Textures";
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
            long skippedNonExplicit = 0;
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
                    if (!IsExplicitModelAnimationRelation(relationSource, confidence))
                    {
                        skippedNonExplicit++;
                        continue;
                    }

                    Set(p, "$modelOutput", modelOutput);
                    Set(p, "$animationOutput", animationOutput);
                    Set(p, "$relationSource", relationSource);
                    Set(p, "$confidence", confidence);
                    Set(p, "$score", D(candidate, "score"));
                    Set(p, "$status", "explicit");
                    Set(p, "$needsValidation", 0);
                    Set(p, "$rawJson", candidate.ToString(Formatting.None));
                    command.ExecuteNonQuery();
                    count++;
                }
            }

            if (skippedNonExplicit > 0)
            {
                Logger.Info($"Skipped {skippedNonExplicit} non-explicit compact model-animation candidate(s); fallback/diagnostic relations are not imported into model_animation_candidates.");
            }

            return count;
        }

        private static bool IsExplicitModelAnimationRelation(string relationSource, string confidence)
        {
            // 默认候选表只接受关系来源本身就是 explicit 的记录。
            // confidence 只能说明这条显式关系的质量，不能单独把 fallback/diagnostic 提升为默认绑定。
            return string.Equals(relationSource, "explicit", StringComparison.OrdinalIgnoreCase);
        }

        private static long ImportExplicitModelAnimationCandidatesFromSourceIndex(SqliteConnection connection, SqliteTransaction transaction, string root, string sourceIndexPath)
        {
            var sourceIndex = string.IsNullOrWhiteSpace(sourceIndexPath)
                ? Path.Combine(root, "unity_source_index.db")
                : Path.GetFullPath(sourceIndexPath);
            var catalogPath = Path.Combine(root, "asset_catalog.jsonl");
            if (!File.Exists(sourceIndex) || !File.Exists(catalogPath))
            {
                return 0;
            }

            var catalog = ReadJsonLines(catalogPath).ToList();
            var graph = SourceAnimationGraph.Load(sourceIndex);
            if (graph.Objects.Count == 0)
            {
                return 0;
            }

            var models = catalog
                .Where(x => string.Equals(S(x, "kind"), "Model", StringComparison.OrdinalIgnoreCase))
                .Where(IsPrefabLikeModel)
                .Select(x => new CatalogAsset(
                    graph.KeyFromCatalog(x),
                    S(x, "name"),
                    S(x, "output"),
                    x))
                .Where(x => x.Key.IsValid && !string.IsNullOrWhiteSpace(x.Output))
                .ToList();
            var animations = catalog
                .Where(x => string.Equals(S(x, "kind"), "Animation", StringComparison.OrdinalIgnoreCase))
                .Select(x => new CatalogAsset(
                    graph.KeyFromCatalog(x),
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

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO model_animation_candidates(model_output, animation_output, relation_source, confidence, score, status, needs_validation, raw_json)
VALUES ($modelOutput, $animationOutput, 'explicit', 'explicit_unity_source_index', 100, 'explicit', 0, $rawJson);";
            var p = AddParameters(command, "$modelOutput", "$animationOutput", "$rawJson");

            long count = 0;
            var processedModels = 0;
            var modelWatch = Stopwatch.StartNew();
            var insertedOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in models)
            {
                var seenAnimationOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var relation in graph.FindAnimationClipsForModel(model.Key))
                {
                    if (!animations.TryGetValue(relation.ClipKey, out var animation))
                    {
                        continue;
                    }

                    if (!seenAnimationOutputs.Add(animation.Output))
                    {
                        continue;
                    }

                    if (!insertedOutputs.Add(model.Output + "|" + animation.Output))
                    {
                        continue;
                    }

                    var raw = BuildExplicitSourceCandidateJson(model.Raw, animation.Raw, relation);
                    Set(p, "$modelOutput", model.Output);
                    Set(p, "$animationOutput", animation.Output);
                    Set(p, "$rawJson", raw.ToString(Formatting.None));
                    command.ExecuteNonQuery();
                    count++;
                }

                processedModels++;
                if (processedModels % 10000 == 0)
                {
                    Logger.Info($"SQLite source-index candidate expansion processed {processedModels}/{models.Count} model(s) in {modelWatch.Elapsed.TotalSeconds:F1}s; inserted={count}");
                }
            }

            if (count > 0)
            {
                Logger.Info($"SQLite library index added {count} explicit model-animation candidate(s) from unity_source_index.db.");
            }
            if (graph.SkippedStaleOverrideControllers > 0)
            {
                Logger.Warning($"SQLite library index skipped {graph.SkippedStaleOverrideControllers} stale AnimatorOverrideController expansion(s) because unity_source_index.db has baseController relations without animatorOverrideController.overrideSet/clipPair details. Rebuild the source index to recover precise override candidates.");
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

        private static JObject BuildExplicitSourceCandidateJson(JObject model, JObject animation, SourceAnimationRelation relation)
        {
            var animationRequiresHumanoidSolve = RequiresHumanoidBake(animation);
            var standaloneBodyBakeReady = IsStandaloneBodyBakeReady(animation);
            var requiresAnimatorControllerContext = animationRequiresHumanoidSolve && !standaloneBodyBakeReady;
            var hasDirectTransformPreview = HasDirectTransformPreview(animation);
            var modelHasInternalHumanoidSolver = HasCompleteInternalHumanoidSolver(model);
            var modelHasProductionUnityBakeAvatar = HasProductionUnityBakeAvatar(model);
            var standaloneHumanoidBakeRequired = animationRequiresHumanoidSolve && standaloneBodyBakeReady;
            var requiresInternalHumanoidSolve = standaloneHumanoidBakeRequired && modelHasInternalHumanoidSolver;
            var fullHumanoidBakeBlocked = standaloneHumanoidBakeRequired && !modelHasProductionUnityBakeAvatar;
            var canRequestUnityBake = standaloneHumanoidBakeRequired && modelHasProductionUnityBakeAvatar;
            var missingInternalHumanoidSolver = standaloneHumanoidBakeRequired && !modelHasInternalHumanoidSolver && !hasDirectTransformPreview;
            var missingProductionAvatar = standaloneHumanoidBakeRequired && !modelHasProductionUnityBakeAvatar;
            var productionBlockedReason = requiresAnimatorControllerContext
                ? "requires_animator_controller_context"
                : missingProductionAvatar
                    ? GetProductionUnityBakeAvatarMissingReason(model)
                    : null;
            var nextAction = requiresAnimatorControllerContext
                ? "inspect_animator_controller_context"
                : missingInternalHumanoidSolver
                ? "inspect_missing_humanoid_avatar"
                : standaloneHumanoidBakeRequired
                    ? modelHasProductionUnityBakeAvatar
                        ? "generate_unity_baked_gltf"
                        : "refresh_avatar_human_description"
                : "generate_preview_gltf";
            return new JObject
            {
                // 大库会产生数百万条候选。候选 raw 只保留后续筛选/预览必须字段，
                // 名称、输出路径、动画类型等重复信息从 assets 表按 output join 读取。
                ["animationAsset"] = S(animation, "animationAsset"),
                ["relation"] = relation.Relation,
                ["relationSource"] = "explicit",
                ["confidence"] = "explicit_unity_source_index",
                ["legacyUnityBakeSupported"] = canRequestUnityBake,
                ["requiresUnityBake"] = canRequestUnityBake,
                ["fullHumanoidBakeRequired"] = standaloneHumanoidBakeRequired,
                ["productionUnityBakeReady"] = canRequestUnityBake,
                ["productionUnityBakeBlocked"] = requiresAnimatorControllerContext || missingProductionAvatar,
                ["productionUnityBakeBlockedReason"] = productionBlockedReason,
                ["productionAnimationPath"] = requiresAnimatorControllerContext
                    ? "NeedsAnimatorControllerContext"
                    : standaloneHumanoidBakeRequired
                    ? modelHasProductionUnityBakeAvatar
                        ? "UnityBakeToGltf"
                        : "NeedsUnityAvatarMetadata"
                    : "DirectGltf",
                ["requiresInternalHumanoidSolve"] = requiresInternalHumanoidSolve,
                ["missingInternalHumanoidSolver"] = missingInternalHumanoidSolver,
                ["missingInternalHumanoidSolverReason"] = missingInternalHumanoidSolver
                    ? GetInternalHumanoidSolverMissingReason(model)
                    : null,
                ["fullHumanoidBakeBlocked"] = requiresAnimatorControllerContext || fullHumanoidBakeBlocked,
                ["fullHumanoidBakeBlockedReason"] = requiresAnimatorControllerContext
                    ? "requires_animator_controller_context"
                    : fullHumanoidBakeBlocked
                        ? GetProductionUnityBakeAvatarMissingReason(model)
                        : null,
                ["standaloneBodyBakeReady"] = standaloneBodyBakeReady,
                ["standaloneBodyBakeStatus"] = S(animation, "standaloneBodyBakeStatus"),
                ["standaloneBodyBakeReason"] = S(animation, "standaloneBodyBakeReason"),
                ["partialDirectGltf"] = standaloneHumanoidBakeRequired && hasDirectTransformPreview && !modelHasInternalHumanoidSolver,
                ["partialDirectGltfReason"] = standaloneHumanoidBakeRequired && hasDirectTransformPreview && !modelHasInternalHumanoidSolver
                    ? "Animation has deterministic Transform TRS curves, but full Humanoid/Muscle bake is blocked by missing Avatar HumanBone mapping or reference pose."
                    : null,
                ["nextAction"] = nextAction,
                ["matchReason"] = relation.Reason,
            };
        }

        private static bool IsStandaloneBodyBakeReady(JObject animation)
        {
            var token = animation?["standaloneBodyBakeReady"];
            if (token == null || token.Type == JTokenType.Null)
            {
                // 旧库没有这个字段时保持兼容；重新导出/重建索引后才启用严格门禁。
                return true;
            }

            return token.Type == JTokenType.Boolean
                ? token.Value<bool>()
                : bool.TryParse(token.ToString(), out var value) && value;
        }

        private static bool HasDirectTransformPreview(JObject animation)
        {
            var transformBindingCount = I(animation, "transformBindingCount") ?? 0;
            var coreTransformBindingCount = I(animation, "coreTransformBindingCount") ?? 0;
            var type = S(animation, "animationType") ?? string.Empty;
            return transformBindingCount > 0
                && coreTransformBindingCount > 0
                && type.Contains("Transform", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasCompleteInternalHumanoidSolver(JObject model)
        {
            return GetInternalHumanoidSolverMissingReason(model) is null;
        }

        private static bool HasProductionUnityBakeAvatar(JObject model)
        {
            return GetProductionUnityBakeAvatarMissingReason(model) is null;
        }

        private static string GetProductionUnityBakeAvatarMissingReason(JObject model)
        {
            var avatar = model?["avatar"] as JObject;
            if (avatar == null)
            {
                return "missing_avatar_object";
            }

            // 生产 Unity bake 只接受真实 Unity prefab/Animator.avatar，或完整
            // HumanDescription.humanBones + skeletonBones。AvatarConstant/internalSolver
            // 虽然来自 Unity 序列化数据，但重新 BuildHumanAvatar 不等于游戏原始
            // UnityEngine.Avatar；原神样本已经证明这条路会出现背手、折腿等错误。
            var humanBones = avatar["humanBones"] as JArray;
            var skeletonBones = avatar["skeletonBones"] as JArray;
            if (humanBones != null && humanBones.Count > 0 && skeletonBones != null && skeletonBones.Count > 0)
            {
                return null;
            }

            var hasHumanDescriptionFlag = B(avatar, "hasHumanDescription") == 1;
            if (humanBones == null || humanBones.Count == 0)
            {
                return HasCompleteAvatarConstantOracle(avatar)
                    ? "avatar_constant_oracle_diagnostic_only"
                    : hasHumanDescriptionFlag
                        ? "empty_human_description_human_bones"
                        : "missing_human_description_human_bones";
            }
            if (skeletonBones == null || skeletonBones.Count == 0)
            {
                return HasCompleteAvatarConstantOracle(avatar)
                    ? "avatar_constant_oracle_diagnostic_only"
                    : hasHumanDescriptionFlag
                        ? "empty_human_description_skeleton_bones"
                        : "missing_human_description_skeleton_bones";
            }

            return null;
        }

        private static string GetInternalHumanoidSolverMissingReason(JObject model)
        {
            var avatar = model?["avatar"] as JObject;
            if (avatar == null)
            {
                return "missing_avatar_object";
            }
            if (avatar["internalSolver"] is not JObject solver || !solver.HasValues)
            {
                return "missing_internal_solver";
            }

            // 旧索引可能只有 preQ/postQ。缺 Unity Avatar 参考姿态时会把手脚解反，不能进入生产预览队列。
            var skeleton = solver["skeleton"] as JObject;
            var nodes = skeleton?["nodes"] as JArray;
            var humanSkeletonPose = skeleton?["humanSkeletonPose"] as JArray;
            var humanBoneIndex = solver["humanBoneIndex"] as JArray;
            var avatarSkeleton = solver["avatarSkeleton"] as JObject;
            var avatarSkeletonNodes = avatarSkeleton?["nodes"] as JArray;
            var avatarSkeletonDefaultPose = avatarSkeleton?["defaultPose"] as JArray;
            if (nodes is null || nodes.Count == 0)
            {
                if (avatarSkeletonNodes is null || avatarSkeletonNodes.Count == 0)
                {
                    return "missing_solver_skeleton_nodes";
                }
                if (avatarSkeletonDefaultPose is null || avatarSkeletonDefaultPose.Count < avatarSkeletonNodes.Count)
                {
                    return "missing_avatar_pose_metadata";
                }
            }
            else if (humanSkeletonPose is null || humanSkeletonPose.Count < nodes.Count)
            {
                return "missing_avatar_pose_metadata";
            }
            if (!HasValidHumanBoneIndex(humanBoneIndex))
            {
                return "missing_human_bone_index";
            }

            return null;
        }

        private static bool HasValidHumanBoneIndex(JArray humanBoneIndex)
        {
            if (humanBoneIndex is null || humanBoneIndex.Count == 0)
            {
                return false;
            }

            return humanBoneIndex
                .Values<int?>()
                .Any(x => x.GetValueOrDefault(-1) >= 0);
        }

        private static bool HasCompleteAvatarConstantOracle(JObject avatar)
        {
            return HasCompleteOraclePayload(avatar?["oracle"] as JObject)
                || HasCompleteLegacyAvatarConstant(avatar?["internalSolver"] as JObject);
        }

        private static bool HasCompleteOraclePayload(JObject oracle)
        {
            var humanBoneIndex = oracle?["humanBoneIndex"] as JArray;
            var humanSkeleton = oracle?["humanSkeleton"] as JObject;
            var avatarSkeleton = oracle?["avatarSkeleton"] as JObject;
            var humanNodes = humanSkeleton?["nodes"] as JArray;
            var humanPose = humanSkeleton?["pose"] as JArray;
            var avatarNodes = avatarSkeleton?["nodes"] as JArray;
            var avatarDefaultPose = avatarSkeleton?["defaultPose"] as JArray;
            return HasValidHumanBoneIndex(humanBoneIndex)
                && humanNodes != null && humanNodes.Count > 0
                && humanPose != null && humanPose.Count >= humanNodes.Count
                && avatarNodes != null && avatarNodes.Count > 0
                && avatarDefaultPose != null && avatarDefaultPose.Count >= avatarNodes.Count;
        }

        private static bool HasCompleteLegacyAvatarConstant(JObject solver)
        {
            var humanBoneIndex = solver?["humanBoneIndex"] as JArray;
            var skeleton = solver?["skeleton"] as JObject;
            var nodes = skeleton?["nodes"] as JArray;
            var humanSkeletonPose = skeleton?["humanSkeletonPose"] as JArray;
            var avatarSkeleton = solver?["avatarSkeleton"] as JObject;
            var avatarSkeletonNodes = avatarSkeleton?["nodes"] as JArray;
            var avatarSkeletonDefaultPose = avatarSkeleton?["defaultPose"] as JArray;
            return HasValidHumanBoneIndex(humanBoneIndex)
                && nodes != null && nodes.Count > 0
                && humanSkeletonPose != null && humanSkeletonPose.Count >= nodes.Count
                && avatarSkeletonNodes != null && avatarSkeletonNodes.Count > 0
                && avatarSkeletonDefaultPose != null && avatarSkeletonDefaultPose.Count >= avatarSkeletonNodes.Count;
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
                if (ShouldSkipSQLiteArtifact(root, fullPath, fullDbPath))
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

        private static long BuildModelAnimationCandidateSummaries(SqliteConnection connection)
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, "DELETE FROM model_animation_candidate_model_summary;", transaction);
            Execute(connection, "DELETE FROM model_animation_candidate_animation_summary;", transaction);

            Execute(connection, @"
INSERT INTO model_animation_candidate_model_summary(
    model_output,
    explicit_count,
    direct_preview_count,
    internal_humanoid_count,
    legacy_unity_bake_count
)
SELECT model_output,
       COUNT(*) AS explicit_count,
       SUM(CASE
           WHEN COALESCE(json_extract(raw_json, '$.nextAction'), '') = 'generate_preview_gltf'
            AND COALESCE(json_extract(raw_json, '$.requiresInternalHumanoidSolve'), 0) = 0
            AND COALESCE(json_extract(raw_json, '$.missingInternalHumanoidSolver'), 0) = 0
           THEN 1 ELSE 0 END) AS direct_preview_count,
       SUM(CASE
           WHEN COALESCE(json_extract(raw_json, '$.nextAction'), '') = 'generate_preview_gltf'
            AND COALESCE(json_extract(raw_json, '$.requiresInternalHumanoidSolve'), 0) = 1
           THEN 1
           WHEN COALESCE(json_extract(raw_json, '$.nextAction'), '') = 'generate_unity_baked_gltf'
            AND COALESCE(json_extract(raw_json, '$.requiresUnityBake'), 0) = 1
           THEN 1
           WHEN COALESCE(json_extract(raw_json, '$.missingInternalHumanoidSolver'), 0) = 1
             OR COALESCE(json_extract(raw_json, '$.nextAction'), '') = 'inspect_missing_humanoid_avatar'
           THEN 1 ELSE 0 END) AS internal_humanoid_count,
       SUM(CASE
           WHEN COALESCE(json_extract(raw_json, '$.legacyUnityBakeSupported'), 0) = 1
           THEN 1 ELSE 0 END) AS legacy_unity_bake_count
FROM model_animation_candidates
WHERE relation_source='explicit'
GROUP BY model_output;", transaction);

            Execute(connection, @"
INSERT INTO model_animation_candidate_animation_summary(
    animation_output,
    explicit_model_count,
    direct_preview_model_count,
    internal_humanoid_model_count,
    legacy_unity_bake_model_count
)
SELECT animation_output,
       COUNT(*) AS explicit_model_count,
       SUM(CASE
           WHEN COALESCE(json_extract(raw_json, '$.nextAction'), '') = 'generate_preview_gltf'
            AND COALESCE(json_extract(raw_json, '$.requiresInternalHumanoidSolve'), 0) = 0
            AND COALESCE(json_extract(raw_json, '$.missingInternalHumanoidSolver'), 0) = 0
           THEN 1 ELSE 0 END) AS direct_preview_model_count,
       SUM(CASE
           WHEN COALESCE(json_extract(raw_json, '$.nextAction'), '') = 'generate_preview_gltf'
            AND COALESCE(json_extract(raw_json, '$.requiresInternalHumanoidSolve'), 0) = 1
           THEN 1
           WHEN COALESCE(json_extract(raw_json, '$.nextAction'), '') = 'generate_unity_baked_gltf'
            AND COALESCE(json_extract(raw_json, '$.requiresUnityBake'), 0) = 1
           THEN 1
           WHEN COALESCE(json_extract(raw_json, '$.missingInternalHumanoidSolver'), 0) = 1
             OR COALESCE(json_extract(raw_json, '$.nextAction'), '') = 'inspect_missing_humanoid_avatar'
           THEN 1 ELSE 0 END) AS internal_humanoid_model_count,
       SUM(CASE
           WHEN COALESCE(json_extract(raw_json, '$.legacyUnityBakeSupported'), 0) = 1
           THEN 1 ELSE 0 END) AS legacy_unity_bake_model_count
FROM model_animation_candidates
WHERE relation_source='explicit'
GROUP BY animation_output;", transaction);

            transaction.Commit();
            return ScalarLong(connection, "SELECT COUNT(*) FROM model_animation_candidate_model_summary;");
        }

        private static bool ShouldSkipSQLiteArtifact(string root, string fullPath, string fullDbPath)
        {
            if (string.Equals(fullPath, fullDbPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullPath, fullDbPath + "-wal", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullPath, fullDbPath + "-shm", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var relative = MakeRelative(root, fullPath).Replace('\\', '/');
            var fileName = Path.GetFileName(fullPath);
            var extension = Path.GetExtension(fileName);
            if (!relative.Contains('/', StringComparison.Ordinal)
                && fileName.StartsWith("library_index", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(extension, ".db", StringComparison.OrdinalIgnoreCase)
                    || fileName.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase)
                    || fileName.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase)
                    || fileName.Contains(".db.", StringComparison.OrdinalIgnoreCase)))
            {
                // 根目录常保留旁路、备份或中断的 SQLite 索引。它们是索引构建产物，
                // 不应作为普通素材文件进入 files 表，否则全量重建会扫描很多十几 GB 的 DB。
                return true;
            }

            return false;
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

        private static void WriteSummary(SqliteConnection connection, string root, string dbPath, Dictionary<string, long> counts, bool skipFileIndex, bool skipSidecarScan, string sourceIndexPath)
        {
            var summaryPath = GetSQLiteSummaryPath(root, dbPath);
            var animationRelationCoverage = BuildAnimationRelationCoverageSummarySafely(connection, root, skipSidecarScan, sourceIndexPath);
            var summary = new JObject
            {
                ["schemaVersion"] = 1,
                ["database"] = dbPath,
                ["root"] = root,
                ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["rule"] = "索引要全，导出要精。SQLite v1 面向已导出的 Library/AudioLibrary 文件；后续会扩展到原始 Unity CAB/Object/PPtr 全量索引。",
                ["fileIndexSkipped"] = skipFileIndex,
                ["sidecarScanSkipped"] = skipSidecarScan,
                ["fileIndexNote"] = skipFileIndex
                    ? "本次重建跳过 files 表，用于快速刷新模型-动画关系、sidecar 能力和源索引健康检查。需要完整文件浏览/审计时重新运行不带 --skip_sqlite_file_index 的 build。"
                    : "本次包含 files 表，适合完整文件浏览和审计；大型素材库会明显增加重建时间。",
                ["counts"] = JObject.FromObject(counts),
                ["animationRelationCoverage"] = animationRelationCoverage
            };
            File.WriteAllText(summaryPath, summary.ToString(Formatting.Indented));

            var readme = UsesDefaultSQLiteIndexPath(root, dbPath)
                ? Path.Combine(root, "SQLITE_INDEX_README.md")
                : Path.Combine(Path.GetDirectoryName(summaryPath) ?? root, Path.GetFileNameWithoutExtension(dbPath) + ".README.md");
            File.WriteAllText(readme,
                "# SQLite Library Index\n\n" +
                "这是 AnimeStudio 的素材库索引数据库。当前 v1 从已经导出的 Library/AudioLibrary 文件构建，目标是让后续浏览、筛选、调试、二次打包不用反复扫描大量 JSONL。\n\n" +
                "核心规则：索引要全，导出要精。进入索引不代表默认导出或推荐使用；导出仍按 Unity 显式关系、结构兼容和严格匹配规则执行。\n\n" +
                "桌面工具默认优先读取 SQLite。高频查询，例如模型列表、动画 binding path 定向匹配、缩略图状态、筛选统计，应尽量走 SQLite 索引；JSON/JSONL 保留给人工排查、兼容旧流程和重新建库。\n\n" +
                "主要表：`assets`、`unity_assets`、`unity_relations`、`animation_bindings`、`animation_binding_paths`、`export_manifest`、`json_documents`、`files`。每条结构化记录都尽量保留 `raw_json`，方便后续无损迁移。\n\n" +
                "音频说明：当前 AudioLibrary 可以导出原始 `.fsb` 等文件；FMOD/native 转 WAV 作为后续批量转换阶段，不阻塞索引与素材库建设。\n");
        }

        private static JObject BuildAnimationRelationCoverageSummarySafely(SqliteConnection connection, string root, bool skipSidecarScan, string sourceIndexPath)
        {
            try
            {
                return BuildAnimationRelationCoverageSummary(connection, root, skipSidecarScan, sourceIndexPath);
            }
            catch (Exception e)
            {
                Logger.Warning($"SQLite summary animation relation coverage failed: {e.GetType().Name}: {e.Message}");
                return new JObject
                {
                    ["status"] = "error",
                    ["error"] = e.GetType().Name + ": " + e.Message,
                    ["note"] = "SQLite DB 已完成写入；仅动画关系覆盖摘要统计失败。可稍后单独诊断 summary 查询，不应阻塞素材库生产索引。"
                };
            }
        }

        private static string GetSQLiteSummaryPath(string root, string dbPath)
        {
            if (UsesDefaultSQLiteIndexPath(root, dbPath))
            {
                return Path.Combine(root, "sqlite_index_summary.json");
            }

            var directory = Path.GetDirectoryName(dbPath) ?? root;
            var name = Path.GetFileNameWithoutExtension(dbPath);
            return Path.Combine(directory, name + ".summary.json");
        }

        private static bool UsesDefaultSQLiteIndexPath(string root, string dbPath)
        {
            var defaultPath = Path.GetFullPath(Path.Combine(root, "library_index.db"));
            return string.Equals(Path.GetFullPath(dbPath), defaultPath, StringComparison.OrdinalIgnoreCase);
        }

        private static JObject BuildAnimationRelationCoverageSummary(SqliteConnection connection, string root, bool skipSidecarScan, string sourceIndexPath)
        {
            var totalModels = ScalarLong(connection, "SELECT COUNT(*) FROM assets WHERE kind='Model';");
            var totalAnimations = ScalarLong(connection, "SELECT COUNT(*) FROM assets WHERE kind='Animation';");
            var explicitCandidates = ScalarLong(connection, "SELECT COALESCE(SUM(explicit_count), 0) FROM model_animation_candidate_model_summary;");
            var modelsWithExplicit = ScalarLong(connection, "SELECT COUNT(*) FROM model_animation_candidate_model_summary WHERE explicit_count > 0;");
            var animationsWithExplicit = ScalarLong(connection, "SELECT COUNT(*) FROM model_animation_candidate_animation_summary WHERE explicit_model_count > 0;");
            var relationSources = QueryGroupedCounts(connection, @"
SELECT COALESCE(relation_source, '(null)') AS key, COUNT(*) AS count
FROM model_animation_candidates
GROUP BY COALESCE(relation_source, '(null)')
ORDER BY count DESC;");
            var confidence = QueryGroupedCounts(connection, @"
SELECT COALESCE(confidence, '(null)') AS key, COUNT(*) AS count
FROM model_animation_candidates
GROUP BY COALESCE(confidence, '(null)')
ORDER BY count DESC
LIMIT 16;");
            var candidateNextActions = QueryGroupedCounts(connection, @"
SELECT COALESCE(json_extract(raw_json, '$.nextAction'), '(null)') AS key, COUNT(*) AS count
FROM model_animation_candidates
WHERE relation_source='explicit'
GROUP BY COALESCE(json_extract(raw_json, '$.nextAction'), '(null)')
ORDER BY count DESC
LIMIT 16;");
            var missingInternalHumanoidSolverReasons = QueryGroupedCounts(connection, @"
SELECT COALESCE(json_extract(raw_json, '$.missingInternalHumanoidSolverReason'), '(null)') AS key, COUNT(*) AS count
FROM model_animation_candidates
WHERE relation_source='explicit'
  AND COALESCE(json_extract(raw_json, '$.missingInternalHumanoidSolver'), 0)=1
GROUP BY COALESCE(json_extract(raw_json, '$.missingInternalHumanoidSolverReason'), '(null)')
ORDER BY count DESC
LIMIT 16;");
            var missingInternalHumanoidSolverModelReasons = QueryGroupedCounts(connection, @"
SELECT reason AS key, COUNT(*) AS count
FROM (
    SELECT c.model_output,
           COALESCE(json_extract(c.raw_json, '$.missingInternalHumanoidSolverReason'), '(null)') AS reason
    FROM model_animation_candidates c
    WHERE c.relation_source='explicit'
      AND COALESCE(json_extract(c.raw_json, '$.missingInternalHumanoidSolver'), 0)=1
    GROUP BY c.model_output, reason
)
GROUP BY reason
ORDER BY count DESC
LIMIT 16;");
            var candidateProcessingFlags = QueryGroupedCounts(connection, @"
SELECT 'requiresInternalHumanoidSolve' AS key, COALESCE(SUM(internal_humanoid_count), 0) AS count
FROM model_animation_candidate_model_summary
UNION ALL
SELECT 'directPreviewCandidate' AS key, COALESCE(SUM(direct_preview_count), 0) AS count
FROM model_animation_candidate_model_summary
UNION ALL
SELECT 'internalHumanoidPreviewCandidate' AS key, COALESCE(SUM(internal_humanoid_count), 0) AS count
FROM model_animation_candidate_model_summary
UNION ALL
SELECT 'legacyUnityBakeSupportedDiagnostic' AS key, COALESCE(SUM(legacy_unity_bake_count), 0) AS count
FROM model_animation_candidate_model_summary;");
            var linkedAnimationTypes = QueryGroupedCounts(connection, @"
SELECT COALESCE(animation_type, json_extract(raw_json, '$.animationType'), '(null)') AS key, COUNT(*) AS count
FROM model_animation_candidate_animation_summary s
JOIN assets a ON a.output = s.animation_output AND a.kind = 'Animation'
WHERE s.explicit_model_count > 0
GROUP BY COALESCE(animation_type, json_extract(raw_json, '$.animationType'), '(null)')
ORDER BY count DESC
LIMIT 32;");
            var linkedAnimationSignals = QueryGroupedCounts(connection, @"
SELECT 'hasMuscleClip' AS key, COUNT(*) AS count
FROM (
    SELECT a.output, a.raw_json
    FROM model_animation_candidate_animation_summary s
    JOIN assets a ON a.output = s.animation_output AND a.kind = 'Animation'
    WHERE s.explicit_model_count > 0
)
WHERE json_extract(raw_json, '$.hasMuscleClip') = 1
UNION ALL
SELECT 'hasTransformBindings' AS key, COUNT(*) AS count
FROM (
    SELECT a.output, a.raw_json
    FROM model_animation_candidate_animation_summary s
    JOIN assets a ON a.output = s.animation_output AND a.kind = 'Animation'
    WHERE s.explicit_model_count > 0
)
WHERE COALESCE(json_extract(raw_json, '$.transformBindingCount'), 0) > 0
UNION ALL
SELECT 'hasCoreTransformBindings' AS key, COUNT(*) AS count
FROM (
    SELECT a.output, a.raw_json
    FROM model_animation_candidate_animation_summary s
    JOIN assets a ON a.output = s.animation_output AND a.kind = 'Animation'
    WHERE s.explicit_model_count > 0
)
WHERE COALESCE(json_extract(raw_json, '$.coreTransformBindingCount'), 0) > 0
UNION ALL
SELECT 'hasBlendShapeBindings' AS key, COUNT(*) AS count
FROM (
    SELECT a.output, a.raw_json
    FROM model_animation_candidate_animation_summary s
    JOIN assets a ON a.output = s.animation_output AND a.kind = 'Animation'
    WHERE s.explicit_model_count > 0
)
WHERE COALESCE(json_extract(raw_json, '$.trueBlendShapeBindingCount'), 0) > 0
UNION ALL
SELECT 'hasAmbiguousSkinnedMeshRendererFloatBindings' AS key, COUNT(*) AS count
FROM (
    SELECT a.output, a.raw_json
    FROM model_animation_candidate_animation_summary s
    JOIN assets a ON a.output = s.animation_output AND a.kind = 'Animation'
    WHERE s.explicit_model_count > 0
)
WHERE COALESCE(json_extract(raw_json, '$.blendShapeBindingCount'), 0) > 0
UNION ALL
SELECT 'hasRendererMaterialBindings' AS key, COUNT(*) AS count
FROM (
    SELECT a.output, a.raw_json
    FROM model_animation_candidate_animation_summary s
    JOIN assets a ON a.output = s.animation_output AND a.kind = 'Animation'
    WHERE s.explicit_model_count > 0
)
WHERE COALESCE(json_extract(raw_json, '$.rendererMaterialBindingCount'), 0) > 0
UNION ALL
SELECT 'hasRendererPropertyBindings' AS key, COUNT(*) AS count
FROM (
    SELECT a.output, a.raw_json
    FROM model_animation_candidate_animation_summary s
    JOIN assets a ON a.output = s.animation_output AND a.kind = 'Animation'
    WHERE s.explicit_model_count > 0
)
WHERE COALESCE(json_extract(raw_json, '$.rendererPropertyBindingCount'), 0) > 0
UNION ALL
SELECT 'hasActiveStateBindings' AS key, COUNT(*) AS count
FROM (
    SELECT a.output, a.raw_json
    FROM model_animation_candidate_animation_summary s
    JOIN assets a ON a.output = s.animation_output AND a.kind = 'Animation'
    WHERE s.explicit_model_count > 0
)
WHERE COALESCE(json_extract(raw_json, '$.activeStateBindingCount'), 0) > 0
UNION ALL
SELECT 'hasAuxiliaryBindings' AS key, COUNT(*) AS count
FROM (
    SELECT a.output, a.raw_json
    FROM model_animation_candidate_animation_summary s
    JOIN assets a ON a.output = s.animation_output AND a.kind = 'Animation'
    WHERE s.explicit_model_count > 0
)
WHERE COALESCE(json_extract(raw_json, '$.auxiliaryBindingCount'), 0) > 0;");
            var resourceKinds = QueryResourceKindCoverage(connection);
            var perModel = QueryLongList(connection, @"
SELECT explicit_count
FROM model_animation_candidate_model_summary
WHERE explicit_count > 0
ORDER BY explicit_count;");
            var sidecarCapabilities = skipSidecarScan
                ? new JObject
                {
                    ["status"] = "skipped",
                    ["note"] = "本次重建使用 --skip_sqlite_sidecar_scan，未逐个读取动画 sidecar。候选关系和源索引健康检查仍然有效；需要 decoded/directGltfReady 覆盖统计时重新运行不带该参数的 build。"
                }
                : QueryLinkedAnimationSidecarCapabilities(connection, root);
            var avatarProductionGate = BuildAvatarProductionGateSummary(connection);
            var unityBakeProduction = BuildUnityBakeProductionSummary(connection);

            return new JObject
            {
                ["rule"] = "只统计 SQLite 中已经建立的模型-动画关系。默认推荐候选必须来自 relation_source=explicit；结构兼容、骨骼数量、名称和路径只能作为诊断或显式 fallback。",
                ["modelCoverageNote"] = "allModelsIncludingStatic 会把大量静态模型、场景件和暂不处理的 VFX/特效网格放进分母，不能单独当作动画关系失败率。更可靠的验收应结合 resourceKind、显式候选、sidecar decoded 能力和 glTF 预览验证。",
                ["totals"] = new JObject
                {
                    ["models"] = totalModels,
                    ["animations"] = totalAnimations,
                    ["explicitCandidates"] = explicitCandidates,
                    ["modelsWithExplicitCandidates"] = modelsWithExplicit,
                    ["animationsWithExplicitCandidates"] = animationsWithExplicit,
                    ["allModelExplicitCoverage"] = Ratio(modelsWithExplicit, totalModels),
                    ["animationExplicitCoverage"] = Ratio(animationsWithExplicit, totalAnimations)
                },
                ["relationSources"] = relationSources,
                ["confidence"] = confidence,
                ["candidateNextActions"] = candidateNextActions,
                ["missingInternalHumanoidSolverReasons"] = missingInternalHumanoidSolverReasons,
                ["missingInternalHumanoidSolverModelReasons"] = missingInternalHumanoidSolverModelReasons,
                ["candidateProcessingFlags"] = candidateProcessingFlags,
                ["sourceIndexAnimationRelationHealth"] = BuildSourceIndexAnimationRelationHealth(root, sourceIndexPath),
                ["avatarProductionGate"] = avatarProductionGate,
                ["unityBakeProduction"] = unityBakeProduction,
                ["linkedAnimationSidecarCapabilities"] = sidecarCapabilities,
                ["linkedAnimationTypes"] = linkedAnimationTypes,
                ["linkedAnimationSignals"] = linkedAnimationSignals,
                ["modelResourceKinds"] = resourceKinds,
                ["explicitCandidatesPerLinkedModel"] = BuildDistribution(perModel)
            };
        }

        private static JObject BuildAvatarProductionGateSummary(SqliteConnection connection)
        {
            var modelAvatarSql = @"
SELECT COUNT(*) AS avatarModels,
       SUM(CASE WHEN COALESCE(json_array_length(json_extract(raw_json, '$.avatar.humanBones')), 0) > 0
                 AND COALESCE(json_array_length(json_extract(raw_json, '$.avatar.skeletonBones')), 0) > 0
                THEN 1 ELSE 0 END) AS productionAvatarModels,
       SUM(CASE WHEN COALESCE(json_extract(raw_json, '$.avatar.hasHumanDescription'), 0)=1 THEN 1 ELSE 0 END) AS humanDescriptionFlagModels,
       SUM(CASE WHEN COALESCE(json_extract(raw_json, '$.avatar.hasHumanDescription'), 0)=1
                 AND (
                   COALESCE(json_array_length(json_extract(raw_json, '$.avatar.humanBones')), 0)=0
                   OR COALESCE(json_array_length(json_extract(raw_json, '$.avatar.skeletonBones')), 0)=0
                 )
                THEN 1 ELSE 0 END) AS emptyHumanDescriptionModels,
       SUM(CASE WHEN json_extract(raw_json, '$.avatar.oracle') IS NOT NULL
                 OR json_extract(raw_json, '$.avatar.internalSolver') IS NOT NULL
                THEN 1 ELSE 0 END) AS avatarConstantOracleModels
FROM assets
WHERE kind='Model'
  AND json_extract(raw_json, '$.avatar') IS NOT NULL;";
            var avatarModels = 0L;
            var productionAvatarModels = 0L;
            var humanDescriptionFlagModels = 0L;
            var emptyHumanDescriptionModels = 0L;
            var avatarConstantOracleModels = 0L;
            using (var command = connection.CreateCommand())
            {
                command.CommandTimeout = SummaryQueryTimeoutSeconds;
                command.CommandText = modelAvatarSql;
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    avatarModels = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                    productionAvatarModels = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                    humanDescriptionFlagModels = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                    emptyHumanDescriptionModels = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
                    avatarConstantOracleModels = reader.IsDBNull(4) ? 0 : reader.GetInt64(4);
                }
            }

            var explicitHumanoidCandidates = ScalarLong(connection, @"
SELECT COUNT(*)
FROM model_animation_candidates
WHERE relation_source='explicit'
  AND COALESCE(json_extract(raw_json, '$.fullHumanoidBakeRequired'), 0)=1;");
            var explicitHumanoidModels = ScalarLong(connection, @"
SELECT COUNT(DISTINCT model_output)
FROM model_animation_candidates
WHERE relation_source='explicit'
  AND COALESCE(json_extract(raw_json, '$.fullHumanoidBakeRequired'), 0)=1;");
            var blockedCandidates = ScalarLong(connection, @"
SELECT COUNT(*)
FROM model_animation_candidates
WHERE relation_source='explicit'
  AND COALESCE(json_extract(raw_json, '$.fullHumanoidBakeRequired'), 0)=1
  AND COALESCE(json_extract(raw_json, '$.productionUnityBakeBlocked'), 0)=1;");
            var blockedModels = ScalarLong(connection, @"
SELECT COUNT(DISTINCT model_output)
FROM model_animation_candidates
WHERE relation_source='explicit'
  AND COALESCE(json_extract(raw_json, '$.fullHumanoidBakeRequired'), 0)=1
  AND COALESCE(json_extract(raw_json, '$.productionUnityBakeBlocked'), 0)=1;");
            var readyCandidates = ScalarLong(connection, @"
SELECT COUNT(*)
FROM model_animation_candidates
WHERE relation_source='explicit'
  AND COALESCE(json_extract(raw_json, '$.fullHumanoidBakeRequired'), 0)=1
  AND COALESCE(json_extract(raw_json, '$.productionUnityBakeReady'), 0)=1;");
            var readyModels = ScalarLong(connection, @"
SELECT COUNT(DISTINCT model_output)
FROM model_animation_candidates
WHERE relation_source='explicit'
  AND COALESCE(json_extract(raw_json, '$.fullHumanoidBakeRequired'), 0)=1
  AND COALESCE(json_extract(raw_json, '$.productionUnityBakeReady'), 0)=1;");
            var blockedReasons = QueryGroupedCounts(connection, @"
SELECT COALESCE(json_extract(raw_json, '$.productionUnityBakeBlockedReason'), '(null)') AS key,
       COUNT(*) AS count
FROM model_animation_candidates
WHERE relation_source='explicit'
  AND COALESCE(json_extract(raw_json, '$.fullHumanoidBakeRequired'), 0)=1
  AND COALESCE(json_extract(raw_json, '$.productionUnityBakeBlocked'), 0)=1
GROUP BY COALESCE(json_extract(raw_json, '$.productionUnityBakeBlockedReason'), '(null)')
ORDER BY count DESC, key COLLATE NOCASE;");

            return new JObject
            {
                ["rule"] = "生产 Unity bake 只接受真实 Unity prefab/Animator.avatar，或完整 HumanDescription.humanBones + skeletonBones。AvatarConstant/internalSolver/oracle 只作为诊断和后续恢复输入，不能单独计入 production ready。",
                ["avatarModels"] = avatarModels,
                ["productionAvatarModels"] = productionAvatarModels,
                ["humanDescriptionModels"] = humanDescriptionFlagModels,
                ["humanDescriptionFlagModels"] = humanDescriptionFlagModels,
                ["completeHumanDescriptionModels"] = productionAvatarModels,
                ["emptyHumanDescriptionModels"] = emptyHumanDescriptionModels,
                ["avatarConstantOracleModels"] = avatarConstantOracleModels,
                ["productionAvatarModelCoverage"] = Ratio(productionAvatarModels, avatarModels),
                ["productionAvatarModelCoveragePercent"] = Percent(productionAvatarModels, avatarModels),
                ["explicitHumanoidCandidates"] = explicitHumanoidCandidates,
                ["explicitHumanoidModels"] = explicitHumanoidModels,
                ["readyCandidates"] = readyCandidates,
                ["readyModels"] = readyModels,
                ["blockedCandidates"] = blockedCandidates,
                ["blockedModels"] = blockedModels,
                ["readyCandidateCoverage"] = Ratio(readyCandidates, explicitHumanoidCandidates),
                ["readyCandidateCoveragePercent"] = Percent(readyCandidates, explicitHumanoidCandidates),
                ["blockedReasonCounts"] = blockedReasons,
            };
        }

        private static JObject BuildSourceIndexAnimationRelationHealth(string root, string sourceIndexPath = null)
        {
            var sourceIndex = string.IsNullOrWhiteSpace(sourceIndexPath)
                ? Path.Combine(root, "unity_source_index.db")
                : Path.GetFullPath(sourceIndexPath);
            if (!File.Exists(sourceIndex))
            {
                return new JObject
                {
                    ["status"] = "missing",
                    ["sourceIndex"] = sourceIndex,
                    ["note"] = "未找到 unity_source_index.db；SQLite Library 只能使用已导出的旧关系，无法从完整 Unity PPtr 图恢复 Animator/Animation 确定性关系。",
                };
            }

            try
            {
                using var sourceConnection = new SqliteConnection($"Data Source={sourceIndex};Mode=ReadOnly");
                sourceConnection.Open();
                var overrideControllerObjects = SourceScalarLong(sourceConnection, "SELECT COUNT(*) FROM source_objects WHERE type='AnimatorOverrideController';");
                var controllerClips = SourceRelationCount(sourceConnection, "animatorController.clip");
                var animatorControllers = SourceRelationCount(sourceConnection, "animator.controller");
                var animationClips = SourceRelationCount(sourceConnection, "animation.clip");
                var overrideBase = SourceRelationCount(sourceConnection, "animatorOverrideController.baseController");
                var overrideSet = SourceRelationCount(sourceConnection, "animatorOverrideController.overrideSet");
                var nonEmptyOverrideSet = SourceRelationTargetCountPositive(sourceConnection, "animatorOverrideController.overrideSet");
                var overrideOriginal = SourceRelationCount(sourceConnection, "animatorOverrideController.originalClip");
                var overrideClip = SourceRelationCount(sourceConnection, "animatorOverrideController.overrideClip");
                var overridePair = SourceRelationCount(sourceConnection, "animatorOverrideController.clipPair");
                var features = SourceScalarString(sourceConnection, "SELECT value FROM metadata WHERE key='animationRelationFeatures' LIMIT 1;");
                var staleOverridePairs = overrideControllerObjects > 0
                    && (overrideSet == 0 || (nonEmptyOverrideSet > 0 && overridePair == 0));

                return new JObject
                {
                    ["status"] = staleOverridePairs ? "warning" : "ok",
                    ["sourceIndex"] = sourceIndex,
                    ["animationRelationFeatures"] = string.IsNullOrWhiteSpace(features) ? null : features,
                    ["relationCounts"] = new JObject
                    {
                        ["animator.controller"] = animatorControllers,
                        ["animatorController.clip"] = controllerClips,
                        ["animation.clip"] = animationClips,
                        ["animatorOverrideController.baseController"] = overrideBase,
                        ["animatorOverrideController.overrideSet"] = overrideSet,
                        ["animatorOverrideController.originalClip"] = overrideOriginal,
                        ["animatorOverrideController.overrideClip"] = overrideClip,
                        ["animatorOverrideController.clipPair"] = overridePair,
                    },
                    ["objectCounts"] = new JObject
                    {
                        ["AnimatorOverrideController"] = overrideControllerObjects,
                    },
                    ["nonEmptyOverrideSetCount"] = nonEmptyOverrideSet,
                    ["staleOverridePairIndex"] = staleOverridePairs,
                    ["note"] = staleOverridePairs
                        ? "当前源索引存在 AnimatorOverrideController，但缺少当前工具写入的 animatorOverrideController.overrideSet/clipPair 精确标记。旧索引即使有分离的 originalClip/overrideClip，也不能可靠表达 original -> override 或空替换表；Library 重建会跳过这类不完整 OverrideController 的 base controller 粗扩散。请先用当前工具重建 unity_source_index.db，再重建 library_index.db，以恢复精确 override 动画候选。"
                        : "源索引包含当前工具可识别的动画关系；候选质量仍需结合 resourceKind、sidecar decoded 和 glTF 预览验证。",
                    ["recommendedAction"] = staleOverridePairs
                        ? "先用完整 Unity 源目录重建 unity_source_index.db，再对素材库运行 --build_sqlite_index。不要用骨骼数量或全量模型×动画匹配补这类缺口。"
                        : "可直接使用该源索引重建 library_index.db 或运行预览验证。",
                };
            }
            catch (Exception e)
            {
                return new JObject
                {
                    ["status"] = "error",
                    ["sourceIndex"] = sourceIndex,
                    ["error"] = e.GetType().Name + ": " + e.Message,
                };
            }
        }

        private static JObject BuildUnityBakeProductionSummary(SqliteConnection connection)
        {
            var explicitUnityBakeCandidates = ScalarLong(connection, @"
SELECT COUNT(*)
FROM model_animation_candidates
WHERE relation_source='explicit'
  AND COALESCE(json_extract(raw_json, '$.fullHumanoidBakeRequired'), 0)=1;");
            var uniqueExplicitUnityBakeCandidates = ScalarLong(connection, @"
SELECT COUNT(*)
FROM (
  SELECT DISTINCT model_output, animation_output
  FROM model_animation_candidates
  WHERE relation_source='explicit'
    AND COALESCE(json_extract(raw_json, '$.fullHumanoidBakeRequired'), 0)=1
);");
            var explicitUnityBakeModels = ScalarLong(connection, @"
SELECT COUNT(DISTINCT model_output)
FROM model_animation_candidates
WHERE relation_source='explicit'
  AND COALESCE(json_extract(raw_json, '$.fullHumanoidBakeRequired'), 0)=1;");
            var explicitUnityBakeAnimations = ScalarLong(connection, @"
SELECT COUNT(DISTINCT animation_output)
FROM model_animation_candidates
WHERE relation_source='explicit'
  AND COALESCE(json_extract(raw_json, '$.fullHumanoidBakeRequired'), 0)=1;");
            var bakeReadyExplicitUnityBakeCandidates = ScalarLong(connection, @"
SELECT COUNT(*)
FROM model_animation_candidates c
JOIN assets m ON m.kind='Model' AND m.output=c.model_output
WHERE c.relation_source='explicit'
  AND COALESCE(json_extract(c.raw_json, '$.fullHumanoidBakeRequired'), 0)=1
  AND " + BakeReadyAvatarSql("m") + @";");
            var uniqueBakeReadyExplicitUnityBakeCandidates = ScalarLong(connection, @"
SELECT COUNT(*)
FROM (
  SELECT DISTINCT c.model_output, c.animation_output
  FROM model_animation_candidates c
  JOIN assets m ON m.kind='Model' AND m.output=c.model_output
  WHERE c.relation_source='explicit'
    AND COALESCE(json_extract(c.raw_json, '$.fullHumanoidBakeRequired'), 0)=1
    AND " + BakeReadyAvatarSql("m") + @"
);");

            if (!HasTable(connection, "animation_bake_cache"))
            {
                return new JObject
                {
                    ["status"] = "no_cache_table",
                    ["rule"] = "Unity bake production only counts relation_source=explicit Humanoid/Muscle candidates. Run --bake_animation_previews_from_library to populate animation_bake_cache.",
                    ["explicitUnityBakeCandidates"] = explicitUnityBakeCandidates,
                    ["uniqueExplicitUnityBakeCandidates"] = uniqueExplicitUnityBakeCandidates,
                    ["explicitUnityBakeModels"] = explicitUnityBakeModels,
                    ["explicitUnityBakeAnimations"] = explicitUnityBakeAnimations,
                    ["bakeReadyExplicitUnityBakeCandidates"] = bakeReadyExplicitUnityBakeCandidates,
                    ["uniqueBakeReadyExplicitUnityBakeCandidates"] = uniqueBakeReadyExplicitUnityBakeCandidates,
                    ["bakeReadyExplicitUnityBakeCoverage"] = Ratio(bakeReadyExplicitUnityBakeCandidates, explicitUnityBakeCandidates),
                    ["uniqueBakeReadyExplicitUnityBakeCoverage"] = Ratio(uniqueBakeReadyExplicitUnityBakeCandidates, uniqueExplicitUnityBakeCandidates),
                    ["bakeReadyExplicitUnityBakeCoveragePercent"] = Percent(bakeReadyExplicitUnityBakeCandidates, explicitUnityBakeCandidates),
                    ["uniqueBakeReadyExplicitUnityBakeCoveragePercent"] = Percent(uniqueBakeReadyExplicitUnityBakeCandidates, uniqueExplicitUnityBakeCandidates),
                    ["cachedCandidates"] = 0,
                    ["requestWrittenCandidates"] = 0,
                    ["bakedCandidates"] = 0,
                    ["trustedBakedCandidates"] = 0,
                    ["bakedMissingGltfCandidates"] = 0,
                    ["failedCandidates"] = 0,
                    ["cacheCoverage"] = 0.0,
                    ["bakedCoverage"] = 0.0,
                    ["trustedBakedCoverage"] = 0.0,
                    ["cacheCoveragePercent"] = 0.0,
                    ["bakedCoveragePercent"] = 0.0,
                    ["trustedBakedCoveragePercent"] = 0.0,
                    ["uniqueCachedCandidates"] = 0,
                    ["uniqueRequestWrittenCandidates"] = 0,
                    ["uniqueBakedCandidates"] = 0,
                    ["uniqueTrustedBakedCandidates"] = 0,
                    ["uniqueBakedMissingGltfCandidates"] = 0,
                    ["uniqueNeedsReviewCandidates"] = 0,
                    ["uniqueFailedCandidates"] = 0,
                    ["duplicateCacheRows"] = 0,
                    ["pendingUnityBakeCandidates"] = bakeReadyExplicitUnityBakeCandidates,
                    ["uniquePendingUnityBakeCandidates"] = uniqueBakeReadyExplicitUnityBakeCandidates,
                    ["uniqueCacheCoverage"] = 0.0,
                    ["uniqueTrustedBakedCoverage"] = 0.0,
                    ["uniqueCacheCoveragePercent"] = 0.0,
                    ["uniqueTrustedBakedCoveragePercent"] = 0.0,
                };
            }

            var bakeReadyCacheWhere = BuildBakeReadyCacheWhere("bc");
            var cachedCandidates = ScalarLong(connection, $@"
SELECT COUNT(*)
FROM animation_bake_cache bc
WHERE {bakeReadyCacheWhere};");
            var requestWrittenCandidates = ScalarLong(connection, $@"
SELECT COUNT(*)
FROM animation_bake_cache bc
WHERE bc.status='request_written'
  AND {bakeReadyCacheWhere};");
            var bakedCandidates = ScalarLong(connection, $@"
SELECT COUNT(*)
FROM animation_bake_cache bc
WHERE bc.status='baked'
  AND {bakeReadyCacheWhere};");
            var libraryRoot = GetLibraryRootFromConnection(connection);
            var trustedBakedCandidates = CountTrustedBakedCacheRows(connection, libraryRoot);
            var staticPoseCandidates = CountStaticPoseCacheRows(connection, libraryRoot);
            var needsReviewCandidates = CountNeedsReviewCacheRows(connection, libraryRoot);
            var bakedMissingGltfCandidates = CountBakedMissingGltfCacheRows(connection, libraryRoot);
            var failedCandidates = CountUntrustedFailedCacheRows(connection, libraryRoot);
            var summary = new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Unity bake production only counts relation_source=explicit Humanoid/Muscle candidates whose model has a real Unity prefab/Animator.avatar, complete HumanDescription.humanBones + skeletonBones, or an explicitly imported original Unity Avatar asset. trustedBaked requires status=baked, baked glTF exists, unity_bake_apply_report.json is ok/warning, frameVaryingTracks > 0, avatarTrust.TrustedProductionBake=true, and imported_unity_avatar_asset source when the original request supplied unityAssetPaths.avatarAsset. AvatarConstant/internalSolver oracle is diagnostic only until original Unity Avatar/HumanDescription recovery is proven.",
                ["explicitUnityBakeCandidates"] = explicitUnityBakeCandidates,
                ["uniqueExplicitUnityBakeCandidates"] = uniqueExplicitUnityBakeCandidates,
                ["explicitUnityBakeModels"] = explicitUnityBakeModels,
                ["explicitUnityBakeAnimations"] = explicitUnityBakeAnimations,
                ["bakeReadyExplicitUnityBakeCandidates"] = bakeReadyExplicitUnityBakeCandidates,
                ["uniqueBakeReadyExplicitUnityBakeCandidates"] = uniqueBakeReadyExplicitUnityBakeCandidates,
                ["bakeReadyExplicitUnityBakeCoverage"] = Ratio(bakeReadyExplicitUnityBakeCandidates, explicitUnityBakeCandidates),
                ["uniqueBakeReadyExplicitUnityBakeCoverage"] = Ratio(uniqueBakeReadyExplicitUnityBakeCandidates, uniqueExplicitUnityBakeCandidates),
                ["bakeReadyExplicitUnityBakeCoveragePercent"] = Percent(bakeReadyExplicitUnityBakeCandidates, explicitUnityBakeCandidates),
                ["uniqueBakeReadyExplicitUnityBakeCoveragePercent"] = Percent(uniqueBakeReadyExplicitUnityBakeCandidates, uniqueExplicitUnityBakeCandidates),
                ["cachedCandidates"] = cachedCandidates,
                ["requestWrittenCandidates"] = requestWrittenCandidates,
                ["bakedCandidates"] = bakedCandidates,
                ["trustedBakedCandidates"] = trustedBakedCandidates,
                ["staticPoseCandidates"] = staticPoseCandidates,
                ["needsReviewCandidates"] = needsReviewCandidates,
                ["bakedMissingGltfCandidates"] = bakedMissingGltfCandidates,
                ["failedCandidates"] = failedCandidates,
                ["cacheCoverage"] = Ratio(cachedCandidates, bakeReadyExplicitUnityBakeCandidates),
                ["bakedCoverage"] = Ratio(bakedCandidates, bakeReadyExplicitUnityBakeCandidates),
                ["trustedBakedCoverage"] = Ratio(trustedBakedCandidates, bakeReadyExplicitUnityBakeCandidates),
                ["cacheCoveragePercent"] = Percent(cachedCandidates, bakeReadyExplicitUnityBakeCandidates),
                ["bakedCoveragePercent"] = Percent(bakedCandidates, bakeReadyExplicitUnityBakeCandidates),
                ["trustedBakedCoveragePercent"] = Percent(trustedBakedCandidates, bakeReadyExplicitUnityBakeCandidates),
                ["statusCounts"] = QueryGroupedCounts(connection, $@"
SELECT COALESCE(status, '<null>') AS key, COUNT(*) AS count
FROM animation_bake_cache bc
WHERE {bakeReadyCacheWhere}
GROUP BY COALESCE(status, '<null>')
ORDER BY count DESC;"),
                ["effectiveStatusCounts"] = BuildEffectiveBakeCacheStatusCounts(connection, libraryRoot),
            };
            summary.Merge(BuildUniqueBakeCacheCounts(connection, libraryRoot, bakeReadyExplicitUnityBakeCandidates, uniqueBakeReadyExplicitUnityBakeCandidates));
            return summary;
        }

        private static long CountTrustedBakedCacheRows(SqliteConnection connection, string libraryRoot)
        {
            var count = 0L;
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = $@"
SELECT bc.baked_gltf_path
FROM animation_bake_cache bc
WHERE COALESCE(bc.baked_gltf_path, '')<>''
  AND {BuildBakeReadyCacheWhere("bc")};";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (IsTrustedBakedGltfPath(reader.IsDBNull(0) ? null : reader.GetString(0), libraryRoot))
                {
                    count++;
                }
            }
            return count;
        }

        private static JArray BuildEffectiveBakeCacheStatusCounts(SqliteConnection connection, string libraryRoot)
        {
            var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = $@"
SELECT bc.status, bc.baked_gltf_path
FROM animation_bake_cache bc
WHERE {BuildBakeReadyCacheWhere("bc")};";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var status = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var bakedGltfPath = reader.IsDBNull(1) ? null : reader.GetString(1);
                var key = IsTrustedBakedGltfPath(bakedGltfPath, libraryRoot)
                    ? "trusted_baked"
                    : IsStaticPoseBakedGltfPath(bakedGltfPath, libraryRoot)
                        ? "static_pose"
                        : IsNeedsReviewBakedGltfPath(bakedGltfPath, libraryRoot)
                            ? "needs_review"
                            : string.Equals(status, "baked", StringComparison.OrdinalIgnoreCase)
                                ? "untrusted_baked"
                                : string.IsNullOrWhiteSpace(status) ? "<null>" : status;
                counts[key] = counts.TryGetValue(key, out var current) ? current + 1 : 1;
            }

            return new JArray(counts
                .OrderByDescending(x => x.Value)
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => new JObject
                {
                    ["key"] = x.Key,
                    ["count"] = x.Value,
                }));
        }

        private static long CountUntrustedFailedCacheRows(SqliteConnection connection, string libraryRoot)
        {
            var count = 0L;
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = $@"
SELECT bc.baked_gltf_path
FROM animation_bake_cache bc
WHERE bc.status='failed'
  AND {BuildBakeReadyCacheWhere("bc")};";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var bakedGltfPath = reader.IsDBNull(0) ? null : reader.GetString(0);
                if (!IsTrustedBakedGltfPath(bakedGltfPath, libraryRoot)
                    && !IsStaticPoseBakedGltfPath(bakedGltfPath, libraryRoot)
                    && !IsNeedsReviewBakedGltfPath(bakedGltfPath, libraryRoot))
                {
                    count++;
                }
            }
            return count;
        }

        private static long CountBakedMissingGltfCacheRows(SqliteConnection connection, string libraryRoot)
        {
            var count = 0L;
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = $@"
SELECT bc.baked_gltf_path
FROM animation_bake_cache bc
WHERE bc.status='baked'
  AND {BuildBakeReadyCacheWhere("bc")};";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var bakedGltfPath = reader.IsDBNull(0) ? null : reader.GetString(0);
                if (!IsTrustedBakedGltfPath(bakedGltfPath, libraryRoot)
                    && !IsStaticPoseBakedGltfPath(bakedGltfPath, libraryRoot))
                {
                    count++;
                }
            }
            return count;
        }

        private static long CountStaticPoseCacheRows(SqliteConnection connection, string libraryRoot)
        {
            var count = 0L;
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = $@"
SELECT bc.baked_gltf_path
FROM animation_bake_cache bc
WHERE COALESCE(bc.baked_gltf_path, '')<>''
  AND {BuildBakeReadyCacheWhere("bc")};";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (IsStaticPoseBakedGltfPath(reader.IsDBNull(0) ? null : reader.GetString(0), libraryRoot))
                {
                    count++;
                }
            }
            return count;
        }

        private static long CountNeedsReviewCacheRows(SqliteConnection connection, string libraryRoot)
        {
            var count = 0L;
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = $@"
SELECT bc.baked_gltf_path
FROM animation_bake_cache bc
WHERE COALESCE(bc.baked_gltf_path, '')<>''
  AND {BuildBakeReadyCacheWhere("bc")};";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (IsNeedsReviewBakedGltfPath(reader.IsDBNull(0) ? null : reader.GetString(0), libraryRoot))
                {
                    count++;
                }
            }
            return count;
        }

        private static JObject BuildUniqueBakeCacheCounts(
            SqliteConnection connection,
            string libraryRoot,
            long bakeReadyExplicitUnityBakeCandidates,
            long uniqueBakeReadyExplicitUnityBakeCandidates)
        {
            var groups = new Dictionary<string, UniqueBakeCacheEntry>(StringComparer.OrdinalIgnoreCase);
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = $@"
SELECT bc.model_output, bc.animation_output, bc.status, bc.baked_gltf_path
FROM animation_bake_cache bc
WHERE {BuildBakeReadyCacheWhere("bc")};";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var modelOutput = reader.IsDBNull(0) ? null : reader.GetString(0);
                var animationOutput = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (string.IsNullOrWhiteSpace(modelOutput) || string.IsNullOrWhiteSpace(animationOutput))
                {
                    continue;
                }

                var key = CanonicalizeLibraryOutput(modelOutput, libraryRoot)
                    + "|"
                    + CanonicalizeLibraryOutput(animationOutput, libraryRoot);
                if (!groups.TryGetValue(key, out var entry))
                {
                    entry = new UniqueBakeCacheEntry();
                    groups[key] = entry;
                }

                entry.RowCount++;
                var status = reader.IsDBNull(2) ? null : reader.GetString(2);
                var bakedGltf = reader.IsDBNull(3) ? null : reader.GetString(3);
                var hasTrustedBakedGltf = IsTrustedBakedGltfPath(bakedGltf, libraryRoot);
                if (hasTrustedBakedGltf)
                {
                    entry.HasBaked = true;
                    entry.HasTrustedBaked = true;
                }
                else if (IsStaticPoseBakedGltfPath(bakedGltf, libraryRoot))
                {
                    entry.HasStaticPose = true;
                }
                else if (IsNeedsReviewBakedGltfPath(bakedGltf, libraryRoot))
                {
                    entry.HasNeedsReview = true;
                }
                else if (string.Equals(status, "request_written", StringComparison.OrdinalIgnoreCase))
                {
                    entry.HasRequestWritten = true;
                }
                else if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    entry.HasFailed = true;
                }
                else if (string.Equals(status, "baked", StringComparison.OrdinalIgnoreCase))
                {
                    entry.HasBaked = true;
                }
            }

            var uniqueCachedCandidates = groups.Count;
            var uniqueTrustedBakedCandidates = groups.Values.Count(x => x.HasTrustedBaked);
            var uniqueBakedCandidates = groups.Values.Count(x => x.HasBaked);
            var uniqueBakedMissingGltfCandidates = groups.Values.Count(x => x.HasBaked && !x.HasTrustedBaked);
            var uniqueStaticPoseCandidates = groups.Values.Count(x => !x.HasTrustedBaked && x.HasStaticPose);
            var uniqueNeedsReviewCandidates = groups.Values.Count(x => !x.HasTrustedBaked && !x.HasStaticPose && x.HasNeedsReview);
            var uniqueFailedCandidates = groups.Values.Count(x => !x.HasBaked && !x.HasStaticPose && !x.HasNeedsReview && x.HasFailed);
            var uniqueRequestWrittenCandidates = groups.Values.Count(x => !x.HasBaked && !x.HasStaticPose && !x.HasNeedsReview && !x.HasFailed && x.HasRequestWritten);
            var duplicateCacheRows = groups.Values.Sum(x => Math.Max(0, x.RowCount - 1));
            var terminalDiagnosticCandidates = uniqueStaticPoseCandidates + uniqueNeedsReviewCandidates;

            return new JObject
            {
                ["uniqueCachedCandidates"] = uniqueCachedCandidates,
                ["uniqueRequestWrittenCandidates"] = uniqueRequestWrittenCandidates,
                ["uniqueBakedCandidates"] = uniqueBakedCandidates,
                ["uniqueTrustedBakedCandidates"] = uniqueTrustedBakedCandidates,
                ["uniqueStaticPoseCandidates"] = uniqueStaticPoseCandidates,
                ["uniqueNeedsReviewCandidates"] = uniqueNeedsReviewCandidates,
                ["uniqueBakedMissingGltfCandidates"] = uniqueBakedMissingGltfCandidates,
                ["uniqueFailedCandidates"] = uniqueFailedCandidates,
                ["duplicateCacheRows"] = duplicateCacheRows,
                ["pendingUnityBakeCandidates"] = Math.Max(0, bakeReadyExplicitUnityBakeCandidates - uniqueTrustedBakedCandidates - terminalDiagnosticCandidates),
                ["uniquePendingUnityBakeCandidates"] = Math.Max(0, uniqueBakeReadyExplicitUnityBakeCandidates - uniqueTrustedBakedCandidates - terminalDiagnosticCandidates),
                ["uniqueCacheCoverage"] = Ratio(uniqueCachedCandidates, uniqueBakeReadyExplicitUnityBakeCandidates),
                ["uniqueTrustedBakedCoverage"] = Ratio(uniqueTrustedBakedCandidates, uniqueBakeReadyExplicitUnityBakeCandidates),
                ["uniqueCacheCoveragePercent"] = Percent(uniqueCachedCandidates, uniqueBakeReadyExplicitUnityBakeCandidates),
                ["uniqueTrustedBakedCoveragePercent"] = Percent(uniqueTrustedBakedCandidates, uniqueBakeReadyExplicitUnityBakeCandidates),
            };
        }

        private static bool IsTrustedBakedGltfPath(string bakedGltfPath, string libraryRoot)
        {
            var bakedGltf = ResolveLibraryPath(libraryRoot, bakedGltfPath);
            if (string.IsNullOrWhiteSpace(bakedGltf) || !File.Exists(bakedGltf))
            {
                return false;
            }

            var reportPath = Path.Combine(Path.GetDirectoryName(bakedGltf) ?? string.Empty, "unity_bake_apply_report.json");
            if (!File.Exists(reportPath))
            {
                return false;
            }

            try
            {
                var report = JObject.Parse(File.ReadAllText(reportPath));
                var status = (string)report["status"];
                if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(status, "warning", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return (int?)report["frameVaryingTracks"] > 0
                    && !UsesFirstSampleHumanoidDelta(report)
                    && IsTrustedAvatarBake(report);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsStaticPoseBakedGltfPath(string bakedGltfPath, string libraryRoot)
        {
            var bakedGltf = ResolveLibraryPath(libraryRoot, bakedGltfPath);
            if (string.IsNullOrWhiteSpace(bakedGltf) || !File.Exists(bakedGltf))
            {
                return false;
            }

            var reportPath = Path.Combine(Path.GetDirectoryName(bakedGltf) ?? string.Empty, "unity_bake_apply_report.json");
            if (!File.Exists(reportPath))
            {
                return false;
            }

            try
            {
                var report = JObject.Parse(File.ReadAllText(reportPath));
                return string.Equals((string)report["status"], "static_pose", StringComparison.OrdinalIgnoreCase)
                    && ((int?)report["frameVaryingTracks"] ?? 0) == 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsNeedsReviewBakedGltfPath(string bakedGltfPath, string libraryRoot)
        {
            var bakedGltf = ResolveLibraryPath(libraryRoot, bakedGltfPath);
            if (string.IsNullOrWhiteSpace(bakedGltf) || !File.Exists(bakedGltf))
            {
                return false;
            }

            var reportPath = Path.Combine(Path.GetDirectoryName(bakedGltf) ?? string.Empty, "unity_bake_apply_report.json");
            if (!File.Exists(reportPath))
            {
                return false;
            }

            try
            {
                var report = JObject.Parse(File.ReadAllText(reportPath));
                return string.Equals((string)report["status"], "needs_review", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsTrustedAvatarBake(JObject report)
        {
            var avatarTrust = report["avatarTrust"] as JObject;
            if (avatarTrust == null)
            {
                return false;
            }

            var trusted = (bool?)avatarTrust["TrustedProductionBake"]
                ?? (bool?)avatarTrust["trustedProductionBake"]
                ?? false;
            if (!trusted)
            {
                return false;
            }

            var source = (string)avatarTrust["Source"] ?? (string)avatarTrust["source"];
            if (ReportRequestHasExplicitAvatarAsset(report))
            {
                return string.Equals(source, "imported_unity_avatar_asset", StringComparison.OrdinalIgnoreCase)
                    && ReportHasImportedAvatarAssetProof(report);
            }

            return IsProductionAvatarTrustSource(source);
        }

        private static bool ReportRequestHasExplicitAvatarAsset(JObject report)
        {
            if (!string.IsNullOrWhiteSpace((string)report?["unityBakeRequestedAvatarAsset"]))
            {
                return true;
            }

            var requestPath = (string)report?["request"];
            if (string.IsNullOrWhiteSpace(requestPath) || !File.Exists(requestPath))
            {
                return false;
            }

            try
            {
                var request = JObject.Parse(File.ReadAllText(requestPath));
                return !string.IsNullOrWhiteSpace((string)request["unityAssetPaths"]?["avatarAsset"]);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsProductionAvatarTrustSource(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return false;
            }

            if (source.Contains("internal_solver", StringComparison.OrdinalIgnoreCase)
                || source.Contains("avatar_constant", StringComparison.OrdinalIgnoreCase)
                || source.Contains("oracle", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private static bool ReportHasImportedAvatarAssetProof(JObject report)
        {
            if ((bool?)report?["unityBakeImportedAvatarAssetValid"] ?? false)
            {
                return true;
            }

            return string.Equals((string)report?["unityBakeRigRestPoseSource"], "imported_unity_avatar_asset", StringComparison.OrdinalIgnoreCase)
                && ((bool?)report?["unityBakeRigRestPoseApplied"] ?? false);
        }

        private static bool UsesFirstSampleHumanoidDelta(JObject report)
        {
            return string.Equals(
                (string)report?["humanoidDeltaBase"],
                "first_sample",
                StringComparison.OrdinalIgnoreCase);
        }

        private static string GetLibraryRootFromConnection(SqliteConnection connection)
        {
            try
            {
                return Path.GetDirectoryName(Path.GetFullPath(connection.DataSource)) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string CanonicalizeLibraryOutput(string path, string libraryRoot)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(libraryRoot) && Path.IsPathRooted(path))
            {
                try
                {
                    var fullRoot = Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var fullPath = Path.GetFullPath(path);
                    if (fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        return NormalizeLibraryOutput(Path.GetRelativePath(fullRoot, fullPath));
                    }
                }
                catch
                {
                    return NormalizeLibraryOutput(path);
                }
            }

            return NormalizeLibraryOutput(path);
        }

        private static string NormalizeLibraryOutput(string path)
        {
            return (path ?? string.Empty)
                .Trim()
                .Trim('"')
                .Replace('\\', '/')
                .TrimStart('/')
                .TrimEnd('/');
        }

        private static string BuildBakeReadyCacheWhere(string cacheAlias)
        {
            return $@"
EXISTS (
  SELECT 1
  FROM model_animation_candidates c
  JOIN assets m ON m.kind='Model' AND m.output=c.model_output
  WHERE c.model_output={cacheAlias}.model_output
    AND c.animation_output={cacheAlias}.animation_output
    AND c.relation_source='explicit'
    AND (
      COALESCE(json_extract(c.raw_json, '$.requiresUnityBake'), 0)=1
      OR COALESCE(json_extract(c.raw_json, '$.legacyUnityBakeSupported'), 0)=1
      OR COALESCE(json_extract(c.raw_json, '$.requiresInternalHumanoidSolve'), 0)=1
    )
    AND {BakeReadyAvatarSql("m")}
)";
        }

        private static string BakeReadyAvatarSql(string modelAlias)
        {
            var raw = $"{modelAlias}.raw_json";
            return $@"(
    COALESCE(json_array_length(json_extract({raw}, '$.avatar.humanBones')), 0) > 0
    AND COALESCE(json_array_length(json_extract({raw}, '$.avatar.skeletonBones')), 0) > 0
  )";
        }

        private sealed class UniqueBakeCacheEntry
        {
            public int RowCount { get; set; }
            public bool HasRequestWritten { get; set; }
            public bool HasBaked { get; set; }
            public bool HasTrustedBaked { get; set; }
            public bool HasStaticPose { get; set; }
            public bool HasNeedsReview { get; set; }
            public bool HasFailed { get; set; }
        }

        private static long SourceRelationCount(SqliteConnection connection, string relation)
        {
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = "SELECT COUNT(*) FROM source_relations WHERE relation=$relation;";
            command.Parameters.AddWithValue("$relation", relation);
            return Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }

        private static long SourceRelationTargetCountPositive(SqliteConnection connection, string relation)
        {
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = "SELECT COUNT(*) FROM source_relations WHERE relation=$relation AND COALESCE(target_count, 0) > 0;";
            command.Parameters.AddWithValue("$relation", relation);
            return Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }

        private static long SourceScalarLong(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }

        private static string SourceScalarString(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = sql;
            return command.ExecuteScalar() as string;
        }

        private static JArray QueryResourceKindCoverage(SqliteConnection connection)
        {
            var result = new JArray();
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = @"
SELECT COALESCE(a.resource_kind, '(null)') AS resource_kind,
       COUNT(*) AS total,
       SUM(CASE WHEN s.model_output IS NULL THEN 0 ELSE 1 END) AS with_explicit
FROM assets a
LEFT JOIN model_animation_candidate_model_summary s ON s.model_output = a.output AND s.explicit_count > 0
WHERE a.kind='Model'
GROUP BY COALESCE(a.resource_kind, '(null)')
ORDER BY with_explicit DESC, total DESC;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var total = reader.GetInt64(1);
                var linked = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                result.Add(new JObject
                {
                    ["resourceKind"] = reader.GetString(0),
                    ["models"] = total,
                    ["modelsWithExplicitCandidates"] = linked,
                    ["explicitCoverage"] = Ratio(linked, total)
                });
            }
            return result;
        }

        private static JObject QueryLinkedAnimationSidecarCapabilities(SqliteConnection connection, string root)
        {
            var rows = new List<(string Output, string AnimationAsset, long CandidateCount)>();
            using (var command = connection.CreateCommand())
            {
                command.CommandTimeout = SummaryQueryTimeoutSeconds;
                command.CommandText = @"
SELECT a.output,
       json_extract(a.raw_json, '$.animationAsset') AS animation_asset,
       s.explicit_model_count AS candidate_count
FROM model_animation_candidate_animation_summary s
JOIN assets a ON a.output = s.animation_output AND a.kind='Animation'
WHERE s.explicit_model_count > 0;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add((
                        reader.IsDBNull(0) ? null : reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? 0 : reader.GetInt64(2)
                    ));
                }
            }

            var decodedStatus = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var playbackKinds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            long withSidecar = 0;
            long missingSidecar = 0;
            long unreadableSidecar = 0;
            long decodedOkAnimations = 0;
            long directGltfReadyAnimations = 0;
            long requiresInternalHumanoidSolveAnimations = 0;
            long skippedFullCurvesAnimations = 0;
            long decodedOkCandidates = 0;
            long directGltfReadyCandidates = 0;
            long requiresInternalHumanoidSolveCandidates = 0;

            foreach (var row in rows)
            {
                var sidecarPath = ResolveLibraryPath(root, row.AnimationAsset);
                if (string.IsNullOrWhiteSpace(sidecarPath) || !File.Exists(sidecarPath))
                {
                    missingSidecar++;
                    continue;
                }

                withSidecar++;
                if (!TryReadDecodedCapability(sidecarPath, out var capability))
                {
                    unreadableSidecar++;
                    AddCount(decodedStatus, "unreadable");
                    continue;
                }

                var status = string.IsNullOrWhiteSpace(capability.Status) ? "(missing)" : capability.Status;
                AddCount(decodedStatus, status);
                if (!string.IsNullOrWhiteSpace(capability.PlaybackKind))
                {
                    AddCount(playbackKinds, capability.PlaybackKind);
                }

                var decodedOk = string.Equals(capability.Status, "ok", StringComparison.OrdinalIgnoreCase);
                var directCandidate = IsDirectGltfSidecarCandidate(capability);
                var internalHumanoidCandidate = IsInternalHumanoidSidecarCandidate(capability);

                if (decodedOk)
                {
                    decodedOkAnimations++;
                    decodedOkCandidates += row.CandidateCount;
                }
                if (directCandidate)
                {
                    directGltfReadyAnimations++;
                    directGltfReadyCandidates += row.CandidateCount;
                }
                if (internalHumanoidCandidate)
                {
                    requiresInternalHumanoidSolveAnimations++;
                    requiresInternalHumanoidSolveCandidates += row.CandidateCount;
                }
                if (string.Equals(capability.Status, "skipped", StringComparison.OrdinalIgnoreCase))
                {
                    skippedFullCurvesAnimations++;
                }
            }

            return new JObject
            {
                ["rule"] = "只扫描已经被 explicit Unity 关系引用到的动画 sidecar。这里是直接 glTF 生成能力的输入状态，不等于视觉验收通过。",
                ["linkedAnimations"] = rows.Count,
                ["withAnimationAssetSidecar"] = withSidecar,
                ["missingAnimationAssetSidecar"] = missingSidecar,
                ["unreadableAnimationAssetSidecar"] = unreadableSidecar,
                ["decodedOkAnimations"] = decodedOkAnimations,
                ["directGltfReadyAnimations"] = directGltfReadyAnimations,
                ["requiresInternalHumanoidSolveAnimations"] = requiresInternalHumanoidSolveAnimations,
                ["skippedFullCurvesAnimations"] = skippedFullCurvesAnimations,
                ["decodedOkCandidates"] = decodedOkCandidates,
                ["directGltfReadyCandidates"] = directGltfReadyCandidates,
                ["requiresInternalHumanoidSolveCandidates"] = requiresInternalHumanoidSolveCandidates,
                ["inferenceNote"] = "旧 sidecar 没有 directGltfReady/playbackKind 字段时，会用 decoded.curveCounts 与 float category 保守推断：Transform/BlendShape 曲线可直接写 glTF；Animator/Humanoid float 需要内部 Humanoid solver。",
                ["decodedStatus"] = ToCountArray(decodedStatus),
                ["playbackKinds"] = ToCountArray(playbackKinds),
            };
        }

        private static bool IsDirectGltfSidecarCandidate(DecodedCapability capability)
        {
            if (!string.Equals(capability.Status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (capability.DirectGltfReady == true)
            {
                return true;
            }
            if (IsInternalHumanoidSidecarCandidate(capability))
            {
                return false;
            }
            return capability.TransformCurveCount > 0 || capability.HasBlendShapeFloat;
        }

        private static bool IsInternalHumanoidSidecarCandidate(DecodedCapability capability)
        {
            if (!string.Equals(capability.Status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return capability.RequiresInternalHumanoidSolve == true
                || string.Equals(capability.PlaybackKind, "HumanoidMuscleNeedsInternalSolver", StringComparison.OrdinalIgnoreCase)
                || capability.HasHumanoidFloat;
        }

        private static string ResolveLibraryPath(string root, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }
            return Path.IsPathRooted(path)
                ? path
                : Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        }

        private static bool TryReadDecodedCapability(string path, out DecodedCapability capability)
        {
            capability = default;
            try
            {
                using var stream = File.OpenRead(path);
                using var text = new StreamReader(stream);
                using var reader = new JsonTextReader(text);
                while (reader.Read())
                {
                    if (reader.TokenType != JsonToken.PropertyName
                        || !string.Equals((string)reader.Value, "decoded", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!reader.Read() || reader.TokenType != JsonToken.StartObject)
                    {
                        return true;
                    }

                    var decodedDepth = reader.Depth;
                    while (reader.Read())
                    {
                        if (reader.TokenType == JsonToken.EndObject && reader.Depth == decodedDepth)
                        {
                            return true;
                        }
                        if (reader.TokenType != JsonToken.PropertyName)
                        {
                            continue;
                        }

                        var name = (string)reader.Value;
                        if (!reader.Read())
                        {
                            return true;
                        }

                        switch (name)
                        {
                            case "status":
                                capability.Status = reader.Value?.ToString();
                                break;
                            case "playbackKind":
                                capability.PlaybackKind = reader.Value?.ToString();
                                break;
                            case "directGltfReady":
                                capability.DirectGltfReady = ReadBool(reader.Value);
                                break;
                            case "requiresInternalHumanoidSolve":
                                capability.RequiresInternalHumanoidSolve = ReadBool(reader.Value);
                                break;
                            case "curveCounts":
                                ReadCurveCounts(reader, ref capability);
                                break;
                            case "floats":
                                ReadFloatCurveKinds(reader, ref capability);
                                break;
                            default:
                                reader.Skip();
                                break;
                        }
                    }
                    return true;
                }
                return true;
            }
            catch (Exception e) when (e is IOException || e is JsonException || e is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool? ReadBool(object value)
        {
            if (value is bool b)
            {
                return b;
            }
            return bool.TryParse(value?.ToString(), out var parsed) ? parsed : null;
        }

        private static void ReadCurveCounts(JsonTextReader reader, ref DecodedCapability capability)
        {
            if (reader.TokenType != JsonToken.StartObject)
            {
                reader.Skip();
                return;
            }

            var depth = reader.Depth;
            while (reader.Read())
            {
                if (reader.TokenType == JsonToken.EndObject && reader.Depth == depth)
                {
                    return;
                }
                if (reader.TokenType != JsonToken.PropertyName)
                {
                    continue;
                }

                var name = (string)reader.Value;
                if (!reader.Read())
                {
                    return;
                }

                var value = ReadInt(reader.Value);
                switch (name)
                {
                    case "translations":
                    case "rotations":
                    case "scales":
                    case "eulers":
                        capability.TransformCurveCount += value;
                        break;
                }
            }
        }

        private static void ReadFloatCurveKinds(JsonTextReader reader, ref DecodedCapability capability)
        {
            if (reader.TokenType != JsonToken.StartArray)
            {
                reader.Skip();
                return;
            }

            var depth = reader.Depth;
            while (reader.Read())
            {
                if (reader.TokenType == JsonToken.EndArray && reader.Depth == depth)
                {
                    return;
                }
                if (reader.TokenType != JsonToken.StartObject)
                {
                    continue;
                }

                ReadOneFloatCurveKind(reader, ref capability);
            }
        }

        private static void ReadOneFloatCurveKind(JsonTextReader reader, ref DecodedCapability capability)
        {
            var depth = reader.Depth;
            string category = null;
            string classId = null;
            string attribute = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonToken.EndObject && reader.Depth == depth)
                {
                    break;
                }
                if (reader.TokenType != JsonToken.PropertyName)
                {
                    continue;
                }

                var name = (string)reader.Value;
                if (!reader.Read())
                {
                    break;
                }

                switch (name)
                {
                    case "category":
                        category = reader.Value?.ToString();
                        break;
                    case "classID":
                        classId = reader.Value?.ToString();
                        break;
                    case "attribute":
                        attribute = reader.Value?.ToString();
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            if (string.Equals(category, "HumanoidMuscleOrAnimator", StringComparison.OrdinalIgnoreCase)
                || string.Equals(classId, "Animator", StringComparison.OrdinalIgnoreCase))
            {
                capability.HasHumanoidFloat = true;
            }
            if (string.Equals(category, "BlendShape", StringComparison.OrdinalIgnoreCase)
                || (attribute ?? string.Empty).StartsWith("blendShape.", StringComparison.OrdinalIgnoreCase))
            {
                capability.HasBlendShapeFloat = true;
            }
        }

        private static int ReadInt(object value)
        {
            if (value is int i)
            {
                return i;
            }
            if (value is long l)
            {
                return l > int.MaxValue ? int.MaxValue : (int)l;
            }
            return int.TryParse(value?.ToString(), out var parsed) ? parsed : 0;
        }

        private static void AddCount(Dictionary<string, long> counts, string key, long value = 1)
        {
            key = string.IsNullOrWhiteSpace(key) ? "(missing)" : key;
            counts.TryGetValue(key, out var current);
            counts[key] = current + value;
        }

        private static JArray ToCountArray(Dictionary<string, long> counts)
        {
            var result = new JArray();
            foreach (var item in counts.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(new JObject
                {
                    ["key"] = item.Key,
                    ["count"] = item.Value
                });
            }
            return result;
        }

        private struct DecodedCapability
        {
            public string Status { get; set; }
            public string PlaybackKind { get; set; }
            public bool? DirectGltfReady { get; set; }
            public bool? RequiresInternalHumanoidSolve { get; set; }
            public int TransformCurveCount { get; set; }
            public bool HasHumanoidFloat { get; set; }
            public bool HasBlendShapeFloat { get; set; }
        }

        private static JObject BuildDistribution(List<long> values)
        {
            if (values.Count == 0)
            {
                return new JObject
                {
                    ["linkedModelCount"] = 0
                };
            }

            return new JObject
            {
                ["linkedModelCount"] = values.Count,
                ["min"] = values[0],
                ["median"] = Percentile(values, 0.50),
                ["p95"] = Percentile(values, 0.95),
                ["max"] = values[^1],
                ["average"] = Ratio(values.Sum(), values.Count)
            };
        }

        private static JArray QueryGroupedCounts(SqliteConnection connection, string sql)
        {
            var result = new JArray();
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new JObject
                {
                    ["key"] = reader.GetString(0),
                    ["count"] = reader.GetInt64(1)
                });
            }
            return result;
        }

        private static List<long> QueryLongList(SqliteConnection connection, string sql)
        {
            var result = new List<long>();
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(reader.GetInt64(0));
            }
            return result;
        }

        private static long ScalarLong(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = sql;
            return command.ExecuteScalar() is long value ? value : 0;
        }

        private static bool HasTable(SqliteConnection connection, string tableName)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
            command.Parameters.AddWithValue("$name", tableName);
            return Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture) > 0;
        }

        private static double Ratio(long numerator, long denominator)
        {
            return denominator <= 0 ? 0 : Math.Round((double)numerator / denominator, 6);
        }

        private static double Percent(long numerator, long denominator)
        {
            return denominator <= 0 ? 0 : Math.Round((double)numerator * 100.0 / denominator, 6);
        }

        private static double Percentile(List<long> sortedValues, double percentile)
        {
            if (sortedValues.Count == 0)
            {
                return 0;
            }
            var index = (int)Math.Floor((sortedValues.Count - 1) * percentile);
            if (index < 0)
            {
                index = 0;
            }
            if (index >= sortedValues.Count)
            {
                index = sortedValues.Count - 1;
            }
            return sortedValues[index];
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

        private static void Execute(SqliteConnection connection, string sql, SqliteTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
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

        private static string NormalizeAssetOutputKey(string root, string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return string.Empty;
            }

            var fullPath = Path.IsPathRooted(output)
                ? output
                : Path.Combine(root, output);
            return Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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
            private readonly Dictionary<SourceKey, SourceKey> _transformParents = new();
            private readonly Dictionary<SourceKey, List<SourceKey>> _animatorControllers = new();
            private readonly Dictionary<SourceKey, List<SourceKey>> _animationClips = new();
            private readonly Dictionary<SourceKey, List<SourceKey>> _controllerClips = new();
            private readonly Dictionary<SourceKey, List<SourceKey>> _overrideBaseControllers = new();
            private readonly Dictionary<SourceKey, List<SourceKey>> _overrideOriginalClips = new();
            private readonly Dictionary<SourceKey, List<SourceKey>> _overrideClips = new();
            private readonly Dictionary<SourceKey, List<OverrideClipPair>> _overrideClipPairs = new();
            private readonly Dictionary<SourceKey, int> _overrideSetCounts = new();
            private readonly Dictionary<SourceKey, SourceKey> _sourceObjectAliases = new();
            private readonly HashSet<SourceKey> _ambiguousSourceObjectAliases = new();
            private readonly Dictionary<SourceKey, List<SourceAnimationRelation>> _hierarchyClipCache = new();
            private long _skippedStaleOverrideControllers;
            private string _sourceRoot;

            public Dictionary<SourceKey, SourceObject> Objects { get; } = new();

            public long SkippedStaleOverrideControllers => _skippedStaleOverrideControllers;

            public static SourceAnimationGraph Load(string sourceIndex)
            {
                var watch = Stopwatch.StartNew();
                var graph = new SourceAnimationGraph();
                using var connection = new SqliteConnection($"Data Source={sourceIndex};Mode=ReadOnly");
                connection.Open();
                graph.LoadMetadata(connection);
                graph.LoadObjects(connection);
                graph.LoadRelations(connection);
                Logger.Info($"SQLite source animation graph loaded in {watch.Elapsed.TotalSeconds:F1}s; objects={graph.Objects.Count}");
                return graph;
            }

            public SourceKey KeyFromCatalog(JObject obj)
            {
                var source = S(obj, "source") ?? S(obj, "sourceFile") ?? string.Empty;
                var pathId = I(obj, "pathId") ?? 0;
                foreach (var file in BuildCatalogFileKeys(source))
                {
                    var key = ResolveSourceObjectKey(new SourceKey(file, pathId).Normalize());
                    if (Objects.ContainsKey(key))
                    {
                        return key;
                    }
                }

                return new SourceKey(Path.GetFileName(source), pathId).Normalize();
            }

            public IEnumerable<SourceAnimationRelation> FindAnimationClipsForModel(SourceKey modelKey)
            {
                modelKey = ResolveSourceObjectKey(modelKey.Normalize());
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

                foreach (var relation in FindHierarchyAnimationClips(modelKey))
                {
                    if (seen.Add(relation.ClipKey))
                    {
                        yield return relation;
                    }
                }

                foreach (var relation in FindAncestorAnimationClips(modelKey, seen))
                {
                    yield return relation;
                }
            }

            private IReadOnlyList<SourceAnimationRelation> FindHierarchyAnimationClips(SourceKey gameObject)
            {
                gameObject = ResolveSourceObjectKey(gameObject.Normalize());
                if (_hierarchyClipCache.TryGetValue(gameObject, out var cached))
                {
                    return cached;
                }

                return BuildHierarchyAnimationClips(gameObject, new HashSet<SourceKey>(), 0);
            }

            private List<SourceAnimationRelation> BuildHierarchyAnimationClips(SourceKey gameObject, HashSet<SourceKey> active, int depth)
            {
                if (_hierarchyClipCache.TryGetValue(gameObject, out var cached))
                {
                    return cached;
                }
                if (depth > MaxHierarchyDepth || !active.Add(gameObject))
                {
                    return new List<SourceAnimationRelation>();
                }

                var result = new List<SourceAnimationRelation>();
                var seen = new HashSet<SourceKey>();
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
                            AddHierarchyRelation(result, seen, new SourceAnimationRelation(clip, "gameObject.hierarchy.animator.controller.clip", $"Animator '{componentObject.Name}' is attached to the exported GameObject hierarchy."));
                        }
                    }
                    else if (string.Equals(componentObject.Type, "Animation", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var clip in GetMany(_animationClips, component))
                        {
                            AddHierarchyRelation(result, seen, new SourceAnimationRelation(clip, "gameObject.hierarchy.animation.clip", $"Animation component '{componentObject.Name}' explicitly references this clip."));
                        }
                    }
                    else if (string.Equals(componentObject.Type, "Transform", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var childTransform in GetMany(_transformChildren, component))
                        {
                            if (!_componentGameObjects.TryGetValue(childTransform, out var childGameObject))
                            {
                                continue;
                            }

                            foreach (var relation in BuildHierarchyAnimationClips(childGameObject, active, depth + 1))
                            {
                                AddHierarchyRelation(result, seen, relation);
                            }
                        }
                    }
                }

                active.Remove(gameObject);
                _hierarchyClipCache[gameObject] = result;
                return result;
            }

            private static void AddHierarchyRelation(List<SourceAnimationRelation> result, HashSet<SourceKey> seen, SourceAnimationRelation relation)
            {
                if (relation.ClipKey.IsValid && seen.Add(relation.ClipKey))
                {
                    result.Add(relation);
                }
            }

            private IEnumerable<SourceAnimationRelation> FindAncestorAnimationClips(SourceKey gameObject, HashSet<SourceKey> seen)
            {
                foreach (var transform in GetMany(_gameObjectComponents, gameObject)
                    .Where(x => Objects.TryGetValue(x, out var obj) && string.Equals(obj.Type, "Transform", StringComparison.OrdinalIgnoreCase)))
                {
                    var current = transform;
                    for (var depth = 0; depth < MaxHierarchyDepth && _transformParents.TryGetValue(current, out var parentTransform); depth++)
                    {
                        current = parentTransform;
                        if (!_componentGameObjects.TryGetValue(current, out var parentGameObject))
                        {
                            continue;
                        }

                        foreach (var component in GetMany(_gameObjectComponents, parentGameObject))
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
                                        yield return new SourceAnimationRelation(clip, "gameObject.ancestor.animator.controller.clip", $"Ancestor Animator '{componentObject.Name}' controls the exported GameObject hierarchy.");
                                    }
                                }
                            }
                            else if (string.Equals(componentObject.Type, "Animation", StringComparison.OrdinalIgnoreCase))
                            {
                                foreach (var clip in GetMany(_animationClips, component))
                                {
                                    if (seen.Add(clip))
                                    {
                                        yield return new SourceAnimationRelation(clip, "gameObject.ancestor.animation.clip", $"Ancestor Animation component '{componentObject.Name}' controls the exported GameObject hierarchy.");
                                    }
                                }
                            }
                        }
                    }
                }
            }

            private SourceKey ResolveSourceObjectKey(SourceKey modelKey)
            {
                if (Objects.ContainsKey(modelKey))
                {
                    return modelKey;
                }

                return _sourceObjectAliases.TryGetValue(modelKey, out var resolved)
                    ? resolved
                    : modelKey;
            }

            private IEnumerable<string> BuildCatalogFileKeys(string source)
            {
                if (string.IsNullOrWhiteSpace(source))
                {
                    yield break;
                }

                var normalized = source.Replace('\\', '/');
                if (!string.IsNullOrWhiteSpace(_sourceRoot))
                {
                    var root = _sourceRoot.Replace('\\', '/').TrimEnd('/');
                    if (normalized.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        yield return normalized[(root.Length + 1)..];
                    }
                }

                yield return normalized;
                yield return Path.GetFileName(normalized);
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

                var overridePairs = GetMany(_overrideClipPairs, controller).ToArray();
                var baseControllers = GetMany(_overrideBaseControllers, controller).ToArray();
                var overrideOriginalClips = GetMany(_overrideOriginalClips, controller).ToArray();
                var overrideClips = GetMany(_overrideClips, controller).ToArray();
                var hasKnownOverrideSet = _overrideSetCounts.ContainsKey(controller);
                var overrideSetCount = hasKnownOverrideSet ? _overrideSetCounts[controller] : -1;
                var overriddenOriginalClips = overridePairs
                    .Where(x => x.OverrideClip.IsValid)
                    .Select(x => x.OriginalClip)
                    .Where(x => x.IsValid)
                    .ToHashSet();

                var isOverrideController = Objects.TryGetValue(controller, out var controllerObject)
                    && string.Equals(controllerObject.Type, "AnimatorOverrideController", StringComparison.OrdinalIgnoreCase);
                if (isOverrideController && baseControllers.Length > 0 && overridePairs.Length == 0)
                {
                    var hasSeparatedOverrideRefs = overrideOriginalClips.Length > 0 || overrideClips.Length > 0;
                    var hasNonEmptyOverrideSetWithoutPairs = hasKnownOverrideSet && overrideSetCount > 0;
                    if (!hasKnownOverrideSet || hasSeparatedOverrideRefs || hasNonEmptyOverrideSetWithoutPairs)
                    {
                        // 旧 source_index 只有 originalClip/overrideClip 分离列表时，无法证明
                        // original -> override 的一一对应关系。生产候选宁可缺失，也不能猜 pair。
                        _skippedStaleOverrideControllers++;
                        yield break;
                    }
                }

                foreach (var clip in GetMany(_controllerClips, controller))
                {
                    yield return clip;
                }

                foreach (var baseController in baseControllers)
                {
                    foreach (var clip in FindControllerClips(baseController, visited))
                    {
                        if (!overriddenOriginalClips.Contains(clip))
                        {
                            yield return clip;
                        }
                    }
                }

                if (overridePairs.Length > 0)
                {
                    foreach (var pair in overridePairs)
                    {
                        var selected = pair.OverrideClip.IsValid ? pair.OverrideClip : pair.OriginalClip;
                        if (selected.IsValid)
                        {
                            yield return selected;
                        }
                    }
                }
            }

            private void LoadObjects(SqliteConnection connection)
            {
                var watch = Stopwatch.StartNew();
                long count = 0;
                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT source_path, serialized_file, path_id, type, name
FROM source_objects
WHERE type IN ('GameObject','Animator','Animation','AnimatorController','AnimatorOverrideController','AnimationClip');";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var sourcePath = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    var serializedFile = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    var pathId = reader.GetInt64(2);
                    var key = new SourceKey(serializedFile, pathId).Normalize();
                    Objects[key] = new SourceObject(reader.GetString(3), reader.IsDBNull(4) ? "" : reader.GetString(4));
                    AddSourceObjectAlias(new SourceKey(sourcePath, pathId), key);
                    AddSourceObjectAlias(new SourceKey(Path.GetFileName(sourcePath), pathId), key);
                    count++;
                }
                Logger.Info($"SQLite source animation graph loaded source_objects in {watch.Elapsed.TotalSeconds:F1}s; rows={count}");
            }

            private void LoadMetadata(SqliteConnection connection)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM metadata WHERE key = 'sourceRoot' LIMIT 1;";
                _sourceRoot = command.ExecuteScalar() as string;
            }

            private void AddSourceObjectAlias(SourceKey alias, SourceKey target)
            {
                alias = alias.Normalize();
                target = target.Normalize();
                if (!alias.IsValid || !target.IsValid || alias.Equals(target))
                {
                    return;
                }

                // 一个 .blk 里可能拆出多个 CAB。只有别名唯一时才用于回连，避免把同 pathId 的不同对象绑错。
                if (_sourceObjectAliases.TryGetValue(alias, out var existing) && !existing.Equals(target))
                {
                    _sourceObjectAliases.Remove(alias);
                    _ambiguousSourceObjectAliases.Add(alias);
                    return;
                }

                if (!_ambiguousSourceObjectAliases.Contains(alias))
                {
                    _sourceObjectAliases[alias] = target;
                }
            }

            private void LoadRelations(SqliteConnection connection)
            {
                var watch = Stopwatch.StartNew();
                long count = 0;
                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT relation, from_file, from_path_id, to_file, to_path_id, from_type, from_name
FROM source_relations
WHERE relation IN (
  'component.gameObject',
  'transform.parent',
  'transform.child',
  'animator.controller',
  'animation.clip',
  'animatorController.clip',
  'animatorOverrideController.baseController',
  'animatorOverrideController.overrideSet',
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
                        case "component.gameObject":
                            var componentType = reader.IsDBNull(5) ? "" : reader.GetString(5);
                            if (IsAnimationRelevantComponentType(componentType))
                            {
                                Objects[from] = new SourceObject(componentType, reader.IsDBNull(6) ? "" : reader.GetString(6));
                                Add(_gameObjectComponents, to, from);
                                if (string.Equals(componentType, "Transform", StringComparison.OrdinalIgnoreCase))
                                {
                                    _componentGameObjects[from] = to;
                                }
                            }
                            break;
                        case "transform.parent":
                            _transformParents[from] = to;
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
                        case "animatorOverrideController.overrideSet":
                            _overrideSetCounts[from] = 0;
                            break;
                        case "animatorOverrideController.overrideClip":
                            Add(_overrideClips, from, to);
                            break;
                        case "animatorOverrideController.originalClip":
                            Add(_overrideOriginalClips, from, to);
                            break;
                    }
                    count++;
                }

                using (var pairCommand = connection.CreateCommand())
                {
                    pairCommand.CommandText = @"
SELECT from_file, from_path_id, raw_json
FROM source_relations
WHERE relation = 'animatorOverrideController.clipPair';";
                    using var pairReader = pairCommand.ExecuteReader();
                    while (pairReader.Read())
                    {
                        var from = KeyFromRelation(pairReader, 0, 1).Normalize();
                        if (!from.IsValid || !TryReadOverrideClipPair(pairReader.IsDBNull(2) ? null : pairReader.GetString(2), out var pair))
                        {
                            continue;
                        }

                        Add(_overrideClipPairs, from, pair);
                        count++;
                    }
                }
                LoadOverrideSetCounts(connection);
                Logger.Info($"SQLite source animation graph loaded source_relations in {watch.Elapsed.TotalSeconds:F1}s; rows={count}");
                if (_overrideBaseControllers.Count > 0 && _overrideClipPairs.Count == 0 && _overrideOriginalClips.Count == 0 && _overrideClips.Count == 0 && _overrideSetCounts.Count == 0)
                {
                    Logger.Warning("SQLite source animation graph has AnimatorOverrideController baseController relations but no animatorOverrideController.overrideSet/clipPair details; stale override controllers will not expand base controller clips.");
                }
            }

            private void LoadOverrideSetCounts(SqliteConnection connection)
            {
                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT from_file, from_path_id, target_count, raw_json
FROM source_relations
WHERE relation = 'animatorOverrideController.overrideSet';";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var from = KeyFromRelation(reader, 0, 1).Normalize();
                    if (!from.IsValid)
                    {
                        continue;
                    }

                    var count = reader.IsDBNull(2) ? TryReadOverrideSetCount(reader.IsDBNull(3) ? null : reader.GetString(3)) : reader.GetInt32(2);
                    _overrideSetCounts[from] = Math.Max(0, count);
                }
            }

            private static IEnumerable<SourceKey> GetMany(Dictionary<SourceKey, List<SourceKey>> map, SourceKey key)
            {
                return map.TryGetValue(key, out var values) ? values : Enumerable.Empty<SourceKey>();
            }

            private static bool IsAnimationRelevantComponentType(string type)
            {
                return string.Equals(type, "Transform", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(type, "Animator", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(type, "Animation", StringComparison.OrdinalIgnoreCase);
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

            private static IEnumerable<OverrideClipPair> GetMany(Dictionary<SourceKey, List<OverrideClipPair>> map, SourceKey key)
            {
                return map.TryGetValue(key, out var values) ? values : Enumerable.Empty<OverrideClipPair>();
            }

            private static void Add(Dictionary<SourceKey, List<OverrideClipPair>> map, SourceKey key, OverrideClipPair value)
            {
                if (!map.TryGetValue(key, out var list))
                {
                    list = new List<OverrideClipPair>();
                    map[key] = list;
                }

                list.Add(value);
            }

            private static bool TryReadOverrideClipPair(string rawJson, out OverrideClipPair pair)
            {
                pair = default;
                if (string.IsNullOrWhiteSpace(rawJson))
                {
                    return false;
                }

                try
                {
                    var raw = JObject.Parse(rawJson);
                    var details = raw["details"] as JObject;
                    var original = KeyFromPPtr(details?["original"] as JObject).Normalize();
                    var replacement = KeyFromPPtr(details?["override"] as JObject).Normalize();
                    if (!original.IsValid && !replacement.IsValid)
                    {
                        return false;
                    }

                    pair = new OverrideClipPair(original, replacement);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            private static int TryReadOverrideSetCount(string rawJson)
            {
                if (string.IsNullOrWhiteSpace(rawJson))
                {
                    return 0;
                }

                try
                {
                    var raw = JObject.Parse(rawJson);
                    return (int?)raw["details"]?["count"] ?? 0;
                }
                catch
                {
                    return 0;
                }
            }

            private static SourceKey KeyFromPPtr(JObject ptr)
            {
                return new SourceKey((string)ptr?["file"] ?? string.Empty, (long?)ptr?["pathId"] ?? 0);
            }
        }

        private readonly record struct OverrideClipPair(SourceKey OriginalClip, SourceKey OverrideClip);

        private sealed record AnimationBakeCacheRow(
            string ModelOutput,
            string AnimationOutput,
            string Status,
            string RequestPath,
            string ResultPath,
            string BakedGltfPath,
            string BakedFbxPath,
            string Message,
            string BakeMode,
            string UpdatedUtc);

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
