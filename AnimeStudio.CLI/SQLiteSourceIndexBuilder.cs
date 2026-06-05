using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AnimeStudio.CLI
{
    public static class SQLiteSourceIndexBuilder
    {
        public static string Build(
            string inputPath,
            string outputPath,
            Game game,
            string unityVersion,
            int batchFiles,
            string indexPath = null)
        {
            using var totalProfile = ProfileLogger.Measure("source_index_total", new Dictionary<string, object>
            {
                ["inputPath"] = inputPath,
                ["outputPath"] = outputPath,
                ["batchFiles"] = batchFiles,
            });

            if (game == null)
            {
                throw new ArgumentNullException(nameof(game));
            }

            if (string.IsNullOrWhiteSpace(inputPath) || (!File.Exists(inputPath) && !Directory.Exists(inputPath)))
            {
                throw new FileNotFoundException($"Unity source input not found: {inputPath}", inputPath);
            }

            SQLitePCL.Batteries_V2.Init();

            var outputRoot = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(outputRoot);
            var dbPath = string.IsNullOrWhiteSpace(indexPath)
                ? Path.Combine(outputRoot, "unity_source_index.db")
                : Path.GetFullPath(indexPath);
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath) ?? outputRoot);
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }

            string sourceRoot;
            string[] files;
            using (ProfileLogger.Measure("source_index_scan_files", new Dictionary<string, object>
            {
                ["inputPath"] = inputPath,
            }))
            {
                sourceRoot = Directory.Exists(inputPath)
                    ? Path.GetFullPath(inputPath)
                    : Path.GetDirectoryName(Path.GetFullPath(inputPath));
                files = Directory.Exists(inputPath)
                    ? Directory.GetFiles(inputPath, "*.*", SearchOption.AllDirectories).OrderBy(x => x.Length).ToArray()
                    : new[] { Path.GetFullPath(inputPath) };
            }

            var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["sourceFiles"] = files.Length,
                ["serializedFiles"] = 0,
                ["sourceObjects"] = 0,
                ["sourceExternals"] = 0,
                ["sourceRelations"] = 0,
                ["sourceAnimationBindings"] = 0,
                ["failedBatches"] = 0,
            };

            using var connection = new SqliteConnection($"Data Source={dbPath}");
            using (ProfileLogger.Measure("source_index_init_db", new Dictionary<string, object>
            {
                ["dbPath"] = dbPath,
                ["fileCount"] = files.Length,
            }))
            {
                connection.Open();
                Execute(connection, "PRAGMA journal_mode = WAL;");
                Execute(connection, "PRAGMA synchronous = NORMAL;");
                CreateSchema(connection);
                using var transaction = connection.BeginTransaction();
                InsertMetadata(connection, transaction, "schemaVersion", "1");
                InsertMetadata(connection, transaction, "kind", "unity_source_index");
                InsertMetadata(connection, transaction, "sourceRoot", sourceRoot);
                InsertMetadata(connection, transaction, "game", game.Name);
                InsertMetadata(connection, transaction, "unityVersionOverride", unityVersion ?? string.Empty);
                InsertMetadata(connection, transaction, "createdUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                InsertMetadata(connection, transaction, "rule", "索引要全，导出要精。This database indexes Unity source files, SerializedFiles, objects, PPtr relations, and animation bindings without exporting assets.");
                using (ProfileLogger.Measure("source_index_insert_source_files", new Dictionary<string, object>
                {
                    ["fileCount"] = files.Length,
                }))
                {
                    InsertSourceFiles(connection, transaction, sourceRoot, files);
                }
                transaction.Commit();
            }

            var effectiveBatch = Math.Max(1, batchFiles);
            var totalBatches = (int)Math.Ceiling(files.Length / (double)effectiveBatch);
            for (var batchIndex = 0; batchIndex < totalBatches; batchIndex++)
            {
                var batch = files.Skip(batchIndex * effectiveBatch).Take(effectiveBatch).ToArray();
                Logger.Info($"[source-index {batchIndex + 1}/{totalBatches}] Loading {batch.Length} file(s)...");
                var manager = new AssetsManager
                {
                    Game = game,
                    SpecifyUnityVersion = unityVersion,
                    ResolveDependencies = false,
                    Silent = true,
                };

                try
                {
                    using (ProfileLogger.Measure("source_index_load_batch", new Dictionary<string, object>
                    {
                        ["batchIndex"] = batchIndex + 1,
                        ["totalBatches"] = totalBatches,
                        ["batchFileCount"] = batch.Length,
                        ["batchBytes"] = batch.Sum(x => new FileInfo(x).Length),
                    }))
                    {
                        manager.LoadFiles(batch);
                    }

                    using (ProfileLogger.Measure("source_index_write_batch", new Dictionary<string, object>
                    {
                        ["batchIndex"] = batchIndex + 1,
                        ["serializedFileCount"] = manager.assetsFileList.Count,
                        ["objectCount"] = manager.assetsFileList.Sum(x => x.m_Objects?.Count ?? 0),
                        ["parsedObjectCount"] = manager.assetsFileList.Sum(x => x.Objects?.Count ?? 0),
                    }))
                    {
                        using var transaction = connection.BeginTransaction();
                        IndexLoadedAssets(connection, transaction, sourceRoot, manager, counts);
                        transaction.Commit();
                    }
                }
                catch (Exception e)
                {
                    counts["failedBatches"]++;
                    Logger.Warning($"Source index batch failed: {e.Message}");
                }
                finally
                {
                    using (ProfileLogger.Measure("source_index_clear_batch", new Dictionary<string, object>
                    {
                        ["batchIndex"] = batchIndex + 1,
                    }))
                    {
                        manager.Clear();
                    }
                }
            }

            using (ProfileLogger.Measure("source_index_create_sql_indexes", new Dictionary<string, object>
            {
                ["dbPath"] = dbPath,
            }))
            {
                CreateIndexes(connection);
            }

            using (ProfileLogger.Measure("source_index_write_summary", new Dictionary<string, object>
            {
                ["dbPath"] = dbPath,
                ["sourceObjects"] = counts["sourceObjects"],
                ["sourceRelations"] = counts["sourceRelations"],
            }))
            {
                WriteSummary(outputRoot, dbPath, sourceRoot, files.Length, counts);
            }
            Logger.Info($"SQLite Unity source index written: {dbPath}");
            return dbPath;
        }

        private static void CreateSchema(SqliteConnection connection)
        {
            Execute(connection, @"
CREATE TABLE metadata (
    key TEXT PRIMARY KEY,
    value TEXT
);
CREATE TABLE source_files (
    path TEXT PRIMARY KEY,
    size INTEGER,
    modified_utc TEXT
);
CREATE TABLE serialized_files (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    source_path TEXT,
    file_name TEXT,
    original_path TEXT,
    offset INTEGER,
    unity_version TEXT,
    platform TEXT,
    object_count INTEGER,
    external_count INTEGER,
    raw_json TEXT NOT NULL
);
CREATE TABLE source_objects (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    source_path TEXT,
    serialized_file TEXT,
    path_id INTEGER,
    type TEXT,
    class_id INTEGER,
    name TEXT,
    byte_start INTEGER,
    byte_size INTEGER,
    raw_json TEXT NOT NULL
);
CREATE TABLE source_externals (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    source_path TEXT,
    serialized_file TEXT,
    file_id INTEGER,
    file_name TEXT,
    path_name TEXT,
    guid TEXT,
    external_type INTEGER
);
CREATE TABLE source_relations (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    relation TEXT,
    confidence TEXT,
    from_source TEXT,
    from_file TEXT,
    from_type TEXT,
    from_name TEXT,
    from_path_id INTEGER,
    to_file_id INTEGER,
    to_file TEXT,
    to_type_hint TEXT,
    to_path_id INTEGER,
    target_count INTEGER,
    raw_json TEXT NOT NULL
);
CREATE TABLE source_animation_bindings (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    animation_name TEXT,
    animation_source TEXT,
    animation_file TEXT,
    animation_path_id INTEGER,
    binding_count INTEGER,
    has_muscle_clip INTEGER,
    raw_json TEXT NOT NULL
);");
        }

        private static void CreateIndexes(SqliteConnection connection)
        {
            Execute(connection, @"
CREATE INDEX idx_serialized_files_name ON serialized_files(file_name);
CREATE INDEX idx_source_objects_source_path ON source_objects(source_path, path_id);
CREATE INDEX idx_source_objects_file_path ON source_objects(serialized_file, path_id);
CREATE INDEX idx_source_objects_type ON source_objects(type);
CREATE INDEX idx_source_objects_name ON source_objects(name);
CREATE INDEX idx_source_externals_file ON source_externals(serialized_file, file_id);
CREATE INDEX idx_source_relations_from ON source_relations(from_file, from_path_id);
CREATE INDEX idx_source_relations_to ON source_relations(to_file, to_path_id);
CREATE INDEX idx_source_relations_relation ON source_relations(relation);
CREATE INDEX idx_source_animation_bindings_clip ON source_animation_bindings(animation_file, animation_path_id);");
        }

        private static void InsertSourceFiles(SqliteConnection connection, SqliteTransaction transaction, string sourceRoot, string[] files)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR REPLACE INTO source_files(path, size, modified_utc) VALUES ($path, $size, $modifiedUtc);";
            var pPath = command.Parameters.Add("$path", SqliteType.Text);
            var pSize = command.Parameters.Add("$size", SqliteType.Integer);
            var pModified = command.Parameters.Add("$modifiedUtc", SqliteType.Text);
            foreach (var file in files)
            {
                var info = new FileInfo(file);
                pPath.Value = MakeRelative(sourceRoot, file);
                pSize.Value = info.Length;
                pModified.Value = info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture);
                command.ExecuteNonQuery();
            }
        }

        private static void IndexLoadedAssets(SqliteConnection connection, SqliteTransaction transaction, string sourceRoot, AssetsManager manager, Dictionary<string, long> counts)
        {
            foreach (var assetsFile in manager.assetsFileList)
            {
                InsertSerializedFile(connection, transaction, sourceRoot, assetsFile);
                counts["serializedFiles"]++;

                for (var i = 0; i < assetsFile.m_Externals.Count; i++)
                {
                    InsertExternal(connection, transaction, sourceRoot, assetsFile, i + 1, assetsFile.m_Externals[i]);
                    counts["sourceExternals"]++;
                }

                foreach (var objectInfo in assetsFile.m_Objects)
                {
                    var obj = assetsFile.ObjectsDic.TryGetValue(objectInfo.m_PathID, out var parsed) ? parsed : null;
                    InsertSourceObject(connection, transaction, sourceRoot, assetsFile, objectInfo, obj);
                    counts["sourceObjects"]++;
                }

                foreach (var obj in assetsFile.Objects)
                {
                    WriteObjectRelations(connection, transaction, obj, counts);
                }
            }
        }

        private static void InsertSerializedFile(SqliteConnection connection, SqliteTransaction transaction, string sourceRoot, SerializedFile assetsFile)
        {
            var raw = new
            {
                source = MakeRelativeOrName(sourceRoot, assetsFile.originalPath ?? assetsFile.fullName),
                assetsFile.fileName,
                assetsFile.originalPath,
                assetsFile.offset,
                assetsFile.unityVersion,
                platform = assetsFile.m_TargetPlatform.ToString(),
                objectCount = assetsFile.m_Objects?.Count ?? 0,
                externalCount = assetsFile.m_Externals?.Count ?? 0,
            };
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO serialized_files(source_path, file_name, original_path, offset, unity_version, platform, object_count, external_count, raw_json)
VALUES ($sourcePath, $fileName, $originalPath, $offset, $unityVersion, $platform, $objectCount, $externalCount, $rawJson);";
            command.Parameters.AddWithValue("$sourcePath", raw.source ?? string.Empty);
            command.Parameters.AddWithValue("$fileName", assetsFile.fileName ?? string.Empty);
            command.Parameters.AddWithValue("$originalPath", assetsFile.originalPath ?? string.Empty);
            command.Parameters.AddWithValue("$offset", assetsFile.offset);
            command.Parameters.AddWithValue("$unityVersion", assetsFile.unityVersion ?? string.Empty);
            command.Parameters.AddWithValue("$platform", assetsFile.m_TargetPlatform.ToString());
            command.Parameters.AddWithValue("$objectCount", assetsFile.m_Objects?.Count ?? 0);
            command.Parameters.AddWithValue("$externalCount", assetsFile.m_Externals?.Count ?? 0);
            command.Parameters.AddWithValue("$rawJson", JsonConvert.SerializeObject(raw));
            command.ExecuteNonQuery();
        }

        private static void InsertExternal(SqliteConnection connection, SqliteTransaction transaction, string sourceRoot, SerializedFile assetsFile, int fileId, FileIdentifier external)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO source_externals(source_path, serialized_file, file_id, file_name, path_name, guid, external_type)
VALUES ($sourcePath, $serializedFile, $fileId, $fileName, $pathName, $guid, $externalType);";
            command.Parameters.AddWithValue("$sourcePath", MakeRelativeOrName(sourceRoot, assetsFile.originalPath ?? assetsFile.fullName));
            command.Parameters.AddWithValue("$serializedFile", assetsFile.fileName ?? string.Empty);
            command.Parameters.AddWithValue("$fileId", fileId);
            command.Parameters.AddWithValue("$fileName", external.fileName ?? string.Empty);
            command.Parameters.AddWithValue("$pathName", external.pathName ?? string.Empty);
            command.Parameters.AddWithValue("$guid", external.guid.ToString());
            command.Parameters.AddWithValue("$externalType", external.type);
            command.ExecuteNonQuery();
        }

        private static void InsertSourceObject(SqliteConnection connection, SqliteTransaction transaction, string sourceRoot, SerializedFile assetsFile, ObjectInfo objectInfo, AnimeStudio.Object obj)
        {
            var type = obj?.type.ToString() ?? (Enum.IsDefined(typeof(ClassIDType), objectInfo.classID) ? ((ClassIDType)objectInfo.classID).ToString() : "UnknownType");
            var raw = new
            {
                type,
                name = obj?.Name,
                source = MakeRelativeOrName(sourceRoot, assetsFile.originalPath ?? assetsFile.fullName),
                file = assetsFile.fileName,
                pathId = objectInfo.m_PathID,
                objectInfo.classID,
                objectInfo.typeID,
                objectInfo.byteStart,
                objectInfo.byteSize,
                objectInfo.isDestroyed,
                objectInfo.stripped,
            };
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO source_objects(source_path, serialized_file, path_id, type, class_id, name, byte_start, byte_size, raw_json)
VALUES ($sourcePath, $serializedFile, $pathId, $type, $classId, $name, $byteStart, $byteSize, $rawJson);";
            command.Parameters.AddWithValue("$sourcePath", raw.source ?? string.Empty);
            command.Parameters.AddWithValue("$serializedFile", assetsFile.fileName ?? string.Empty);
            command.Parameters.AddWithValue("$pathId", objectInfo.m_PathID);
            command.Parameters.AddWithValue("$type", type);
            command.Parameters.AddWithValue("$classId", objectInfo.classID);
            command.Parameters.AddWithValue("$name", obj?.Name ?? string.Empty);
            command.Parameters.AddWithValue("$byteStart", objectInfo.byteStart);
            command.Parameters.AddWithValue("$byteSize", objectInfo.byteSize);
            command.Parameters.AddWithValue("$rawJson", JsonConvert.SerializeObject(raw));
            command.ExecuteNonQuery();
        }

        private static void WriteObjectRelations(SqliteConnection connection, SqliteTransaction transaction, AnimeStudio.Object obj, Dictionary<string, long> counts)
        {
            switch (obj)
            {
                case GameObject gameObject:
                    foreach (var ptr in gameObject.m_Components ?? Enumerable.Empty<PPtr<Component>>())
                    {
                        AddPPtrRelation(connection, transaction, counts, "gameObject.component", gameObject, ptr.m_FileID, ptr.m_PathID, "Component", null);
                    }
                    break;
                case Transform transform:
                    AddPPtrRelation(connection, transaction, counts, "component.gameObject", transform, transform.m_GameObject.m_FileID, transform.m_GameObject.m_PathID, "GameObject", null);
                    if (transform.m_Father != null && !transform.m_Father.IsNull)
                    {
                        AddPPtrRelation(connection, transaction, counts, "transform.parent", transform, transform.m_Father.m_FileID, transform.m_Father.m_PathID, "Transform", null);
                    }
                    foreach (var ptr in transform.m_Children ?? Enumerable.Empty<PPtr<Transform>>())
                    {
                        AddPPtrRelation(connection, transaction, counts, "transform.child", transform, ptr.m_FileID, ptr.m_PathID, "Transform", null);
                    }
                    break;
                case Animator animator:
                    WriteComponentGameObject(connection, transaction, animator, counts);
                    AddPPtrRelation(connection, transaction, counts, "animator.avatar", animator, animator.m_Avatar.m_FileID, animator.m_Avatar.m_PathID, "Avatar", null);
                    AddPPtrRelation(connection, transaction, counts, "animator.controller", animator, animator.m_Controller.m_FileID, animator.m_Controller.m_PathID, "RuntimeAnimatorController", null);
                    break;
                case Animation animation:
                    WriteComponentGameObject(connection, transaction, animation, counts);
                    foreach (var ptr in animation.m_Animations ?? Enumerable.Empty<PPtr<AnimationClip>>())
                    {
                        AddPPtrRelation(connection, transaction, counts, "animation.clip", animation, ptr.m_FileID, ptr.m_PathID, "AnimationClip", null);
                    }
                    break;
                case AnimatorController controller:
                    foreach (var ptr in controller.m_AnimationClips ?? Enumerable.Empty<PPtr<AnimationClip>>())
                    {
                        AddPPtrRelation(connection, transaction, counts, "animatorController.clip", controller, ptr.m_FileID, ptr.m_PathID, "AnimationClip", null);
                    }
                    break;
                case AnimatorOverrideController overrideController:
                    AddPPtrRelation(connection, transaction, counts, "animatorOverrideController.baseController", overrideController, overrideController.m_Controller.m_FileID, overrideController.m_Controller.m_PathID, "RuntimeAnimatorController", null);
                    foreach (var clip in overrideController.m_Clips ?? Enumerable.Empty<AnimationClipOverride>())
                    {
                        AddPPtrRelation(connection, transaction, counts, "animatorOverrideController.originalClip", overrideController, clip.m_OriginalClip.m_FileID, clip.m_OriginalClip.m_PathID, "AnimationClip", null);
                        AddPPtrRelation(connection, transaction, counts, "animatorOverrideController.overrideClip", overrideController, clip.m_OverrideClip.m_FileID, clip.m_OverrideClip.m_PathID, "AnimationClip", null);
                    }
                    break;
                case SkinnedMeshRenderer skinned:
                    WriteComponentGameObject(connection, transaction, skinned, counts);
                    AddPPtrRelation(connection, transaction, counts, "skinnedMeshRenderer.mesh", skinned, skinned.m_Mesh.m_FileID, skinned.m_Mesh.m_PathID, "Mesh", null);
                    if (skinned.m_RootBone != null && !skinned.m_RootBone.IsNull)
                    {
                        AddPPtrRelation(connection, transaction, counts, "skinnedMeshRenderer.rootBone", skinned, skinned.m_RootBone.m_FileID, skinned.m_RootBone.m_PathID, "Transform", null);
                    }
                    var bones = skinned.m_Bones ?? new List<PPtr<Transform>>();
                    AddPPtrRelation(connection, transaction, counts, "skinnedMeshRenderer.bones", skinned, 0, 0, "Transform", new { count = bones.Count, targets = bones.Take(512).Select(x => DescribePPtr(skinned.assetsFile, x.m_FileID, x.m_PathID, "Transform")).ToArray(), truncated = bones.Count > 512 });
                    WriteRendererMaterials(connection, transaction, skinned, counts);
                    break;
                case MeshRenderer meshRenderer:
                    WriteComponentGameObject(connection, transaction, meshRenderer, counts);
                    WriteRendererMaterials(connection, transaction, meshRenderer, counts);
                    break;
                case MeshFilter meshFilter:
                    WriteComponentGameObject(connection, transaction, meshFilter, counts);
                    AddPPtrRelation(connection, transaction, counts, "meshFilter.mesh", meshFilter, meshFilter.m_Mesh.m_FileID, meshFilter.m_Mesh.m_PathID, "Mesh", null);
                    break;
                case AnimationClip clip:
                    InsertAnimationBindings(connection, transaction, clip);
                    counts["sourceAnimationBindings"]++;
                    break;
            }
        }

        private static void WriteComponentGameObject(SqliteConnection connection, SqliteTransaction transaction, Component component, Dictionary<string, long> counts)
        {
            AddPPtrRelation(connection, transaction, counts, "component.gameObject", component, component.m_GameObject.m_FileID, component.m_GameObject.m_PathID, "GameObject", null);
        }

        private static void WriteRendererMaterials(SqliteConnection connection, SqliteTransaction transaction, Renderer renderer, Dictionary<string, long> counts)
        {
            foreach (var ptr in renderer.m_Materials ?? Enumerable.Empty<PPtr<Material>>())
            {
                AddPPtrRelation(connection, transaction, counts, "renderer.material", renderer, ptr.m_FileID, ptr.m_PathID, "Material", null);
            }
        }

        private static void AddPPtrRelation(SqliteConnection connection, SqliteTransaction transaction, Dictionary<string, long> counts, string relation, AnimeStudio.Object from, int toFileId, long toPathId, string toTypeHint, object details)
        {
            if (InsertPPtrRelation(connection, transaction, relation, from, toFileId, toPathId, toTypeHint, details))
            {
                counts["sourceRelations"]++;
            }
        }

        private static bool InsertPPtrRelation(SqliteConnection connection, SqliteTransaction transaction, string relation, AnimeStudio.Object from, int toFileId, long toPathId, string toTypeHint, object details)
        {
            if (toPathId == 0 && details == null)
            {
                return false;
            }

            var target = DescribePPtr(from.assetsFile, toFileId, toPathId, toTypeHint);
            var raw = new
            {
                kind = "sourceRelation",
                relation,
                confidence = "explicit_pptr",
                from = DescribeObject(from),
                to = target,
                details,
            };
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO source_relations(relation, confidence, from_source, from_file, from_type, from_name, from_path_id, to_file_id, to_file, to_type_hint, to_path_id, target_count, raw_json)
VALUES ($relation, $confidence, $fromSource, $fromFile, $fromType, $fromName, $fromPathId, $toFileId, $toFile, $toTypeHint, $toPathId, $targetCount, $rawJson);";
            command.Parameters.AddWithValue("$relation", relation);
            command.Parameters.AddWithValue("$confidence", "explicit_pptr");
            command.Parameters.AddWithValue("$fromSource", from.assetsFile?.originalPath ?? from.assetsFile?.fullName ?? string.Empty);
            command.Parameters.AddWithValue("$fromFile", from.assetsFile?.fileName ?? string.Empty);
            command.Parameters.AddWithValue("$fromType", from.type.ToString());
            command.Parameters.AddWithValue("$fromName", from.Name ?? string.Empty);
            command.Parameters.AddWithValue("$fromPathId", from.m_PathID);
            command.Parameters.AddWithValue("$toFileId", toFileId);
            command.Parameters.AddWithValue("$toFile", target["file"]?.ToString() ?? string.Empty);
            command.Parameters.AddWithValue("$toTypeHint", toTypeHint ?? string.Empty);
            command.Parameters.AddWithValue("$toPathId", toPathId);
            command.Parameters.AddWithValue("$targetCount", details == null ? DBNull.Value : (details.GetType().GetProperty("count")?.GetValue(details) ?? DBNull.Value));
            command.Parameters.AddWithValue("$rawJson", JsonConvert.SerializeObject(raw));
            command.ExecuteNonQuery();
            return true;
        }

        private static void InsertAnimationBindings(SqliteConnection connection, SqliteTransaction transaction, AnimationClip clip)
        {
            var bindings = clip.m_ClipBindingConstant?.genericBindings ?? new List<GenericBinding>();
            var tos = clip.FindTOS();
            var entries = bindings.Select(x => new
            {
                path = tos != null && tos.TryGetValue(x.path, out var path) ? path : null,
                type = x.typeID.ToString(),
                attribute = x.attribute,
                customType = ((BindingCustomType)x.customType).ToString(),
                isPPtrCurve = x.isPPtrCurve == 1,
            }).ToArray();
            var raw = new
            {
                kind = "sourceAnimationBindings",
                confidence = "structural",
                animation = DescribeObject(clip),
                hasMuscleClip = clip.m_MuscleClip != null,
                bindingCount = entries.Length,
                bindings = entries.Take(1024).ToArray(),
                truncated = entries.Length > 1024,
            };
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO source_animation_bindings(animation_name, animation_source, animation_file, animation_path_id, binding_count, has_muscle_clip, raw_json)
VALUES ($animationName, $animationSource, $animationFile, $animationPathId, $bindingCount, $hasMuscleClip, $rawJson);";
            command.Parameters.AddWithValue("$animationName", clip.Name ?? string.Empty);
            command.Parameters.AddWithValue("$animationSource", clip.assetsFile?.originalPath ?? clip.assetsFile?.fullName ?? string.Empty);
            command.Parameters.AddWithValue("$animationFile", clip.assetsFile?.fileName ?? string.Empty);
            command.Parameters.AddWithValue("$animationPathId", clip.m_PathID);
            command.Parameters.AddWithValue("$bindingCount", entries.Length);
            command.Parameters.AddWithValue("$hasMuscleClip", clip.m_MuscleClip != null ? 1 : 0);
            command.Parameters.AddWithValue("$rawJson", JsonConvert.SerializeObject(raw));
            command.ExecuteNonQuery();
        }

        private static JObject DescribeObject(AnimeStudio.Object obj)
        {
            return new JObject
            {
                ["type"] = obj.type.ToString(),
                ["name"] = obj.Name,
                ["source"] = obj.assetsFile?.originalPath ?? obj.assetsFile?.fullName,
                ["file"] = obj.assetsFile?.fileName,
                ["pathId"] = obj.m_PathID,
            };
        }

        private static JObject DescribePPtr(SerializedFile fromFile, int fileId, long pathId, string typeHint)
        {
            string fileName = fromFile?.fileName;
            string pathName = null;
            if (fileId > 0 && fromFile != null && fileId - 1 < fromFile.m_Externals.Count)
            {
                var external = fromFile.m_Externals[fileId - 1];
                fileName = external.fileName;
                pathName = external.pathName;
            }

            return new JObject
            {
                ["fileId"] = fileId,
                ["file"] = fileName,
                ["pathName"] = pathName,
                ["pathId"] = pathId,
                ["typeHint"] = typeHint,
            };
        }

        private static void WriteSummary(string outputRoot, string dbPath, string sourceRoot, int fileCount, Dictionary<string, long> counts)
        {
            var summary = new JObject
            {
                ["schemaVersion"] = 1,
                ["database"] = dbPath,
                ["sourceRoot"] = sourceRoot,
                ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["rule"] = "索引要全，导出要精。Source SQLite v1 stores Unity source files, SerializedFiles, Objects, externals, PPtr relations, and animation bindings.",
                ["inputFileCount"] = fileCount,
                ["counts"] = JObject.FromObject(counts),
            };
            File.WriteAllText(Path.Combine(outputRoot, "unity_source_index_summary.json"), summary.ToString(Formatting.Indented));
            File.WriteAllText(Path.Combine(outputRoot, "UNITY_SOURCE_INDEX_README.md"),
                "# Unity Source SQLite Index\n\n" +
                "这是完整 Unity 源目录索引，不是导出结果索引。它用于一次性记录源文件、SerializedFile、Object、external CAB/PPtr、Animator/Animation/Renderer/Skin/AnimationClip binding 等关系。\n\n" +
                "核心原则：索引要全，导出要精。源索引中出现的对象不代表默认素材库会导出，也不代表视觉验收通过。\n\n" +
                "主要表：`source_files`、`serialized_files`、`source_objects`、`source_externals`、`source_relations`、`source_animation_bindings`。\n\n" +
                "性能日志：启用 `--profile_log` 后，重点比较 `source_index_load_batch`、`source_index_write_batch`、`source_index_create_sql_indexes` 和 `source_index_total`。\n\n" +
                "第二阶段当前重点是建好可查询底座；后续导出器可以逐步改为读取这个数据库来减少重复扫描。\n");
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

        private static string MakeRelative(string root, string path)
        {
            return Path.GetRelativePath(root, path).Replace('\\', '/');
        }

        private static string MakeRelativeOrName(string root, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                if (Path.IsPathRooted(path))
                {
                    return MakeRelative(root, path);
                }
            }
            catch
            {
                // Keep original Unity path when it is not a filesystem path.
            }
            return path.Replace('\\', '/');
        }
    }
}
