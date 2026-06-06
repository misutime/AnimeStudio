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
            counts["unityAssets"] = 0;
            counts["unityRelations"] = 0;
            counts["animationBindings"] = 0;
            ImportUnityRelations(connection, transaction, root, counts);
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
                        InsertAnimationBinding(connection, transaction, obj, "unity_relations.jsonl");
                        counts["animationBindings"]++;
                    }
                }
            }

            var bindingPath = Path.Combine(root, "animation_bindings.jsonl");
            if (File.Exists(bindingPath))
            {
                foreach (var obj in ReadJsonLines(bindingPath))
                {
                    InsertAnimationBinding(connection, transaction, obj, "animation_bindings.jsonl");
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

        private static void InsertAnimationBinding(SqliteConnection connection, SqliteTransaction transaction, JObject obj, string sourceFile)
        {
            var animation = obj["animation"] as JObject ?? obj;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO animation_bindings(animation_name, animation_source, animation_path_id, binding_count, has_muscle_clip, source_file, raw_json)
VALUES ($animationName, $animationSource, $animationPathId, $bindingCount, $hasMuscleClip, $sourceFile, $rawJson);";
            var p = AddParameters(command, "$animationName", "$animationSource", "$animationPathId", "$bindingCount", "$hasMuscleClip", "$sourceFile", "$rawJson");
            Set(p, "$animationName", S(animation, "name") ?? S(obj, "animationName"));
            Set(p, "$animationSource", S(animation, "source") ?? S(obj, "source"));
            Set(p, "$animationPathId", I(animation, "pathId") ?? I(obj, "pathId"));
            Set(p, "$bindingCount", I(obj, "bindingCount") ?? (obj["bindings"] is JArray bindings ? bindings.Count : null));
            Set(p, "$hasMuscleClip", B(obj, "hasMuscleClip"));
            Set(p, "$sourceFile", sourceFile);
            Set(p, "$rawJson", obj.ToString(Formatting.None));
            command.ExecuteNonQuery();
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
                "主要表：`assets`、`unity_assets`、`unity_relations`、`animation_bindings`、`export_manifest`、`json_documents`、`files`。每条结构化记录都尽量保留 `raw_json`，方便后续无损迁移。\n\n" +
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
