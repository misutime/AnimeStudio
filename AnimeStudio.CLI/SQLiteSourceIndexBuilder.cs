using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AnimeStudio.CLI
{
    public static class SQLiteSourceIndexBuilder
    {
        private const long LargeSourceIndexFileBytes = 256L * 1024L * 1024L;
        private const int SourceIndexWriteCommitInterval = 10_000;
        private const int SourceIndexMaxLightweightArrayCount = 100_000;
        private static readonly TimeSpan SourceIndexHeartbeatInterval = TimeSpan.FromSeconds(30);

        public static string WriteAnimationRelationHealthReport(string sourceIndexPath, string outputPath = null, bool requireHealthy = false)
        {
            if (string.IsNullOrWhiteSpace(sourceIndexPath) || !File.Exists(sourceIndexPath))
            {
                throw new FileNotFoundException($"Unity source index not found: {sourceIndexPath}", sourceIndexPath);
            }

            SQLitePCL.Batteries_V2.Init();
            var dbPath = Path.GetFullPath(sourceIndexPath);
            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();
            var report = BuildAnimationRelationHealthReport(connection, dbPath);
            var reportPath = string.IsNullOrWhiteSpace(outputPath)
                ? Path.Combine(Path.GetDirectoryName(dbPath) ?? Environment.CurrentDirectory, Path.GetFileNameWithoutExtension(dbPath) + ".animation_relation_health.json")
                : Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? Environment.CurrentDirectory);
            File.WriteAllText(reportPath, report.ToString(Formatting.Indented));
            Logger.Info($"Unity source animation relation health report written: {reportPath}");
            var health = report["animationRelationHealth"] as JObject;
            var status = health?["status"]?.ToString() ?? "unknown";
            Logger.Info($"Unity source animation relation health status: {status}");
            if (requireHealthy && !string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                var note = health?["note"]?.ToString() ?? "Source index animation relation health is not ok.";
                throw new InvalidOperationException(
                    "Unity source index animation relations are not fresh enough for production deterministic animation rebuild. " +
                    note);
            }

            return reportPath;
        }

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
            string[] sourceFiles;
            string[] loadableFiles;
            using (ProfileLogger.Measure("source_index_scan_files", new Dictionary<string, object>
            {
                ["inputPath"] = inputPath,
            }))
            {
                sourceRoot = Directory.Exists(inputPath)
                    ? Path.GetFullPath(inputPath)
                    : Path.GetDirectoryName(Path.GetFullPath(inputPath));
                sourceFiles = Directory.Exists(inputPath)
                    ? Directory.GetFiles(inputPath, "*.*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray()
                    : new[] { Path.GetFullPath(inputPath) };
                loadableFiles = sourceFiles
                    .Where(x => IsLikelyUnityLoadableFile(x, game))
                    .OrderBy(x => SafeFileLength(x))
                    .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["sourceFiles"] = sourceFiles.Length,
                ["loadableSourceFiles"] = loadableFiles.Length,
                ["skippedSidecarFiles"] = sourceFiles.Length - loadableFiles.Length,
                ["serializedFiles"] = 0,
                ["sourceObjects"] = 0,
                ["sourceExternals"] = 0,
                ["sourceRelations"] = 0,
                ["sourceAnimationBindings"] = 0,
                ["lightweightRendererObjects"] = 0,
                ["lightweightRendererRelations"] = 0,
                ["lightweightRendererFailures"] = 0,
                ["failedBatches"] = 0,
            };

            using var connection = new SqliteConnection($"Data Source={dbPath}");
            using (ProfileLogger.Measure("source_index_init_db", new Dictionary<string, object>
            {
                ["dbPath"] = dbPath,
                ["fileCount"] = sourceFiles.Length,
                ["loadableFileCount"] = loadableFiles.Length,
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
                InsertMetadata(connection, transaction, "animationRelationFeatures", "animatorController.clip,animatorOverrideController.overrideSet,animatorOverrideController.originalClip,animatorOverrideController.overrideClip,animatorOverrideController.clipPair,animation.clip");
                InsertMetadata(connection, transaction, "rule", "索引要全，导出要精。This database indexes Unity source files, SerializedFiles, objects, PPtr relations, and animation bindings without exporting assets.");
                using (ProfileLogger.Measure("source_index_insert_source_files", new Dictionary<string, object>
                {
                    ["fileCount"] = sourceFiles.Length,
                }))
                {
                    InsertSourceFiles(connection, transaction, sourceRoot, sourceFiles);
                }
                transaction.Commit();
            }

            var effectiveBatch = Math.Max(1, batchFiles);
            var batches = BuildLoadBatches(loadableFiles, effectiveBatch);
            Logger.Info($"SQLite source index will load {loadableFiles.Length}/{sourceFiles.Length} source file(s); skipped {sourceFiles.Length - loadableFiles.Length} sidecar/non-Unity file(s).");
            for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
            {
                var batch = batches[batchIndex];
                var largest = batch
                    .Select(x => new { Path = x, Bytes = SafeFileLength(x) })
                    .OrderByDescending(x => x.Bytes)
                    .FirstOrDefault();
                var batchBytes = batch.Sum(SafeFileLength);
                Logger.Info($"[source-index {batchIndex + 1}/{batches.Count}] Loading {batch.Length} file(s), {FormatBytes(batchBytes)}; largest {MakeRelativeOrName(sourceRoot, largest?.Path)} ({FormatBytes(largest?.Bytes ?? 0)}).");
                var manager = new AssetsManager
                {
                    Game = game,
                    SpecifyUnityVersion = unityVersion,
                    ResolveDependencies = false,
                    LoadSerializedFileExternals = false,
                    SkipProcess = true,
                    StoreUnparsedObjects = false,
                    ObjectParseFilter = ShouldParseObjectForSourceIndex,
                    Silent = true,
                };

                try
                {
                    using (ProfileLogger.Measure("source_index_load_batch", new Dictionary<string, object>
                    {
                        ["batchIndex"] = batchIndex + 1,
                        ["totalBatches"] = batches.Count,
                        ["batchFileCount"] = batch.Length,
                        ["batchBytes"] = batchBytes,
                        ["largestFile"] = MakeRelativeOrName(sourceRoot, largest?.Path),
                        ["largestFileBytes"] = largest?.Bytes ?? 0,
                        ["isLargeSingleton"] = batch.Length == 1 && batchBytes >= LargeSourceIndexFileBytes,
                    }))
                    {
                        using var heartbeat = StartSourceIndexLoadHeartbeat(
                            batchIndex + 1,
                            batches.Count,
                            batch.Length,
                            batchBytes,
                            MakeRelativeOrName(sourceRoot, largest?.Path),
                            largest?.Bytes ?? 0,
                            manager);
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
                        var progress = new SourceIndexWriteProgress(batchIndex + 1, batches.Count, manager.assetsFileList.Sum(x => x.m_Objects?.Count ?? 0));
                        using var heartbeat = StartSourceIndexWriteHeartbeat(progress);
                        IndexLoadedAssets(connection, sourceRoot, manager, counts, progress);
                    }
                }
                catch (Exception e)
                {
                    counts["failedBatches"]++;
                    Logger.Warning($"Source index batch failed: {e}");
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
                WriteSummary(connection, outputRoot, dbPath, sourceRoot, sourceFiles.Length, counts);
            }

            using (ProfileLogger.Measure("source_index_checkpoint", new Dictionary<string, object>
            {
                ["dbPath"] = dbPath,
            }))
            {
                Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
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

        private static void IndexLoadedAssets(SqliteConnection connection, string sourceRoot, AssetsManager manager, Dictionary<string, long> counts, SourceIndexWriteProgress progress)
        {
            SqliteTransaction transaction = null;
            var operationsSinceCommit = 0;

            void EnsureTransaction()
            {
                transaction ??= connection.BeginTransaction();
            }

            void CommitIfNeeded(bool force = false)
            {
                if (transaction == null)
                {
                    return;
                }

                if (!force && operationsSinceCommit < SourceIndexWriteCommitInterval)
                {
                    return;
                }

                transaction.Commit();
                transaction.Dispose();
                transaction = null;
                operationsSinceCommit = 0;
            }

            void CountOperation()
            {
                operationsSinceCommit++;
                CommitIfNeeded();
            }

            try
            {
                foreach (var assetsFile in manager.assetsFileList)
                {
                    progress.CurrentFile = assetsFile.fileName ?? string.Empty;
                    progress.CurrentSource = MakeRelativeOrName(sourceRoot, assetsFile.originalPath ?? assetsFile.fullName);
                    EnsureTransaction();
                    InsertSerializedFile(connection, transaction, sourceRoot, assetsFile);
                    counts["serializedFiles"]++;
                    progress.SerializedFilesWritten++;
                    CountOperation();

                    for (var i = 0; i < assetsFile.m_Externals.Count; i++)
                    {
                        EnsureTransaction();
                        InsertExternal(connection, transaction, sourceRoot, assetsFile, i + 1, assetsFile.m_Externals[i]);
                        counts["sourceExternals"]++;
                        progress.ExternalsWritten++;
                        CountOperation();
                    }

                    foreach (var objectInfo in assetsFile.m_Objects)
                    {
                        progress.CurrentObjectIndex++;
                        progress.CurrentPathId = objectInfo.m_PathID;
                        progress.CurrentType = Enum.IsDefined(typeof(ClassIDType), objectInfo.classID) ? ((ClassIDType)objectInfo.classID).ToString() : objectInfo.classID.ToString(CultureInfo.InvariantCulture);
                        var obj = assetsFile.ObjectsDic.TryGetValue(objectInfo.m_PathID, out var parsed) ? parsed : null;
                        EnsureTransaction();
                        InsertSourceObject(connection, transaction, sourceRoot, assetsFile, objectInfo, obj);
                        counts["sourceObjects"]++;
                        progress.ObjectsWritten++;
                        CountOperation();
                        EnsureTransaction();
                        var lightweightRelations = TryWriteLightweightRendererRelations(connection, transaction, sourceRoot, assetsFile, objectInfo, counts, progress);
                        if (lightweightRelations > 0)
                        {
                            operationsSinceCommit += lightweightRelations;
                            CommitIfNeeded();
                        }
                    }

                    foreach (var obj in assetsFile.Objects)
                    {
                        EnsureTransaction();
                        WriteObjectRelations(connection, transaction, obj, counts);
                        progress.RelationsWritten = counts["sourceRelations"];
                        CountOperation();
                    }
                }
            }
            finally
            {
                CommitIfNeeded(force: true);
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
            var raw = BuildSourceObjectRawJson(sourceRoot, assetsFile, objectInfo, obj, type);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO source_objects(source_path, serialized_file, path_id, type, class_id, name, byte_start, byte_size, raw_json)
VALUES ($sourcePath, $serializedFile, $pathId, $type, $classId, $name, $byteStart, $byteSize, $rawJson);";
            command.Parameters.AddWithValue("$sourcePath", (string)raw["source"] ?? string.Empty);
            command.Parameters.AddWithValue("$serializedFile", assetsFile.fileName ?? string.Empty);
            command.Parameters.AddWithValue("$pathId", objectInfo.m_PathID);
            command.Parameters.AddWithValue("$type", type);
            command.Parameters.AddWithValue("$classId", objectInfo.classID);
            command.Parameters.AddWithValue("$name", obj?.Name ?? string.Empty);
            command.Parameters.AddWithValue("$byteStart", objectInfo.byteStart);
            command.Parameters.AddWithValue("$byteSize", objectInfo.byteSize);
            command.Parameters.AddWithValue("$rawJson", raw.ToString(Formatting.None));
            command.ExecuteNonQuery();
        }

        private static JObject BuildSourceObjectRawJson(string sourceRoot, SerializedFile assetsFile, ObjectInfo objectInfo, AnimeStudio.Object obj, string type)
        {
            var raw = new JObject
            {
                ["type"] = type,
                ["name"] = obj?.Name,
                ["source"] = MakeRelativeOrName(sourceRoot, assetsFile.originalPath ?? assetsFile.fullName),
                ["file"] = assetsFile.fileName,
                ["pathId"] = objectInfo.m_PathID,
                ["classID"] = objectInfo.classID,
                ["typeID"] = objectInfo.typeID,
                ["byteStart"] = objectInfo.byteStart,
                ["byteSize"] = objectInfo.byteSize,
                ["isDestroyed"] = objectInfo.isDestroyed,
                ["stripped"] = objectInfo.stripped,
            };

            switch (obj)
            {
                case Animator animator:
                    raw["animator"] = new JObject
                    {
                        ["avatar"] = BuildPPtrJson(animator.m_Avatar?.m_FileID ?? 0, animator.m_Avatar?.m_PathID ?? 0),
                        ["controller"] = BuildPPtrJson(animator.m_Controller?.m_FileID ?? 0, animator.m_Controller?.m_PathID ?? 0),
                        ["hasTransformHierarchy"] = animator.m_HasTransformHierarchy,
                    };
                    break;
                case Avatar avatar:
                    raw["avatar"] = new JObject
                    {
                        ["hasHumanDescription"] = avatar.m_HumanDescription != null,
                        ["humanDescriptionReadRule"] = avatar.m_HumanDescriptionReadRule,
                        ["humanDescriptionBytesRemainingBeforeRead"] = avatar.m_HumanDescriptionBytesRemainingBeforeRead,
                        ["humanBoneCount"] = avatar.m_HumanDescription?.m_Human?.Count ?? 0,
                        ["skeletonBoneCount"] = avatar.m_HumanDescription?.m_Skeleton?.Count ?? 0,
                        ["avatarSize"] = avatar.m_AvatarSize,
                        ["tosCount"] = avatar.m_TOS?.Count ?? 0,
                        ["avatarSkeletonNodeCount"] = avatar.m_Avatar?.m_AvatarSkeleton?.m_Node?.Count ?? 0,
                        ["avatarSkeletonPoseCount"] = avatar.m_Avatar?.m_AvatarSkeletonPose?.m_X?.Length ?? 0,
                        ["avatarDefaultPoseCount"] = avatar.m_Avatar?.m_DefaultPose?.m_X?.Length ?? 0,
                        ["humanSkeletonNodeCount"] = avatar.m_Avatar?.m_Human?.m_Skeleton?.m_Node?.Count ?? 0,
                        ["humanSkeletonPoseCount"] = avatar.m_Avatar?.m_Human?.m_SkeletonPose?.m_X?.Length ?? 0,
                        ["humanBoneIndexCount"] = avatar.m_Avatar?.m_Human?.m_HumanBoneIndex?.Length ?? 0,
                        ["oracle"] = BuildAvatarOracleJson(avatar),
                    };
                    break;
            }

            return raw;
        }

        private static JObject BuildAvatarOracleJson(Avatar avatar)
        {
            var human = avatar?.m_Avatar?.m_Human;
            var humanSkeleton = human?.m_Skeleton;
            var avatarSkeleton = avatar?.m_Avatar?.m_AvatarSkeleton;
            if (avatar == null || human == null || humanSkeleton?.m_Node == null)
            {
                return null;
            }

            return new JObject
            {
                ["version"] = 1,
                ["source"] = "Unity AvatarConstant",
                ["rule"] = "Deterministic Avatar oracle metadata used to reconstruct a Unity bake prefab/avatar. It is not a HumanDescription and must not be treated as production bake readiness by itself.",
                ["humanBoneIndex"] = ToJsonArray(human.m_HumanBoneIndex),
                ["humanBoneMass"] = ToJsonArray(human.m_HumanBoneMass),
                ["humanSkeletonIndexArray"] = ToJsonArray(avatar.m_Avatar?.m_HumanSkeletonIndexArray),
                ["humanSkeletonReverseIndexArray"] = ToJsonArray(avatar.m_Avatar?.m_HumanSkeletonReverseIndexArray),
                ["human"] = new JObject
                {
                    ["scale"] = human.m_Scale,
                    ["armTwist"] = human.m_ArmTwist,
                    ["foreArmTwist"] = human.m_ForeArmTwist,
                    ["upperLegTwist"] = human.m_UpperLegTwist,
                    ["legTwist"] = human.m_LegTwist,
                    ["armStretch"] = human.m_ArmStretch,
                    ["legStretch"] = human.m_LegStretch,
                    ["feetSpacing"] = human.m_FeetSpacing,
                    ["hasLeftHand"] = human.m_HasLeftHand,
                    ["hasRightHand"] = human.m_HasRightHand,
                    ["hasTDoF"] = human.m_HasTDoF,
                    ["leftHandBoneIndex"] = ToJsonArray(human.m_LeftHand?.m_HandBoneIndex),
                    ["rightHandBoneIndex"] = ToJsonArray(human.m_RightHand?.m_HandBoneIndex),
                    ["root"] = ToJsonXForm(human.m_RootX),
                },
                ["humanSkeleton"] = BuildSkeletonOracleJson(avatar, humanSkeleton, human.m_SkeletonPose?.m_X),
                ["avatarSkeleton"] = BuildSkeletonOracleJson(avatar, avatarSkeleton, avatar.m_Avatar?.m_AvatarSkeletonPose?.m_X, avatar.m_Avatar?.m_DefaultPose?.m_X),
                ["rootMotion"] = new JObject
                {
                    ["boneIndex"] = avatar.m_Avatar?.m_RootMotionBoneIndex ?? -1,
                    ["boneX"] = ToJsonXForm(avatar.m_Avatar?.m_RootMotionBoneX ?? XForm.Zero),
                    ["skeletonIndexArray"] = ToJsonArray(avatar.m_Avatar?.m_RootMotionSkeletonIndexArray),
                    ["skeletonPose"] = ToJsonXFormArray(avatar.m_Avatar?.m_RootMotionSkeletonPose?.m_X, avatar.m_Avatar?.m_RootMotionSkeleton?.m_Node?.Count ?? 0),
                },
            };
        }

        private static JObject BuildSkeletonOracleJson(Avatar avatar, Skeleton skeleton, XForm[] pose, XForm[] defaultPose = null)
        {
            if (skeleton?.m_Node == null)
            {
                return null;
            }

            return new JObject
            {
                ["nodeCount"] = skeleton.m_Node.Count,
                ["axesCount"] = skeleton.m_AxesArray?.Count ?? 0,
                ["nodes"] = new JArray(skeleton.m_Node.Select((node, index) => new JObject
                {
                    ["index"] = index,
                    ["parentId"] = node.m_ParentId,
                    ["axesId"] = node.m_AxesId,
                    ["path"] = TryGetAvatarSkeletonPath(avatar, skeleton, index),
                })),
                ["axes"] = new JArray((skeleton.m_AxesArray ?? new List<Axes>()).Select((axes, index) => new JObject
                {
                    ["index"] = index,
                    ["preQ"] = ToJsonVector4(axes.m_PreQ),
                    ["postQ"] = ToJsonVector4(axes.m_PostQ),
                    ["sign"] = ToJsonVector3Or4As3(axes.m_Sgn),
                    ["limitMin"] = ToJsonVector3Or4As3(axes.m_Limit?.m_Min),
                    ["limitMax"] = ToJsonVector3Or4As3(axes.m_Limit?.m_Max),
                    ["length"] = axes.m_Length,
                    ["type"] = axes.m_Type,
                })),
                ["pose"] = ToJsonXFormArray(pose, skeleton.m_Node.Count),
                ["defaultPose"] = ToJsonXFormArray(defaultPose, skeleton.m_Node.Count),
            };
        }

        private static JObject BuildPPtrJson(int fileId, long pathId)
        {
            return new JObject
            {
                ["fileId"] = fileId,
                ["pathId"] = pathId,
                ["isNull"] = fileId == 0 && pathId == 0,
            };
        }

        private static JArray ToJsonArray(int[] values)
        {
            return values == null ? new JArray() : new JArray(values);
        }

        private static JArray ToJsonArray(float[] values)
        {
            return values == null ? new JArray() : new JArray(values);
        }

        private static JArray ToJsonVector3(Vector3 value)
        {
            return new JArray(value.X, value.Y, value.Z);
        }

        private static JArray ToJsonVector4(Vector4 value)
        {
            return new JArray(value.X, value.Y, value.Z, value.W);
        }

        private static JArray ToJsonVector3Or4As3(object value)
        {
            return value switch
            {
                Vector3 v => new JArray(v.X, v.Y, v.Z),
                Vector4 v => new JArray(v.X, v.Y, v.Z),
                _ => null,
            };
        }

        private static JObject ToJsonXForm(XForm value)
        {
            return new JObject
            {
                ["t"] = ToJsonVector3(value.t),
                ["q"] = new JArray(value.q.X, value.q.Y, value.q.Z, value.q.W),
                ["s"] = ToJsonVector3(value.s),
            };
        }

        private static JArray ToJsonXFormArray(XForm[] values, int expectedCount)
        {
            if (values == null || values.Length == 0)
            {
                return new JArray();
            }

            var count = expectedCount > 0 ? Math.Min(values.Length, expectedCount) : values.Length;
            return new JArray(values.Take(count).Select(ToJsonXForm));
        }

        private static string TryGetAvatarSkeletonPath(Avatar avatar, Skeleton skeleton, int index)
        {
            if (avatar?.m_TOS == null || skeleton?.m_ID == null || index < 0 || index >= skeleton.m_ID.Length)
            {
                return null;
            }

            return avatar.m_TOS.TryGetValue(skeleton.m_ID[index], out var path) && !string.IsNullOrWhiteSpace(path)
                ? path.Replace('\\', '/')
                : null;
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
                    WriteAnimatorControllerClipRelations(connection, transaction, controller, counts);
                    break;
                case AnimatorOverrideController overrideController:
                    AddPPtrRelation(connection, transaction, counts, "animatorOverrideController.baseController", overrideController, overrideController.m_Controller.m_FileID, overrideController.m_Controller.m_PathID, "RuntimeAnimatorController", null);
                    var overrideClips = overrideController.m_Clips ?? new List<AnimationClipOverride>();
                    // 空 OverrideController 是 Unity 的确定性关系：它没有替换任何 clip，
                    // 因此应当安全继承 base controller 动画。旧索引没有这个标记时才视为不完整。
                    AddPPtrRelation(connection, transaction, counts, "animatorOverrideController.overrideSet", overrideController, 0, 0, "AnimationClipOverride", new { count = overrideClips.Count });
                    foreach (var clip in overrideClips)
                    {
                        AddPPtrRelation(connection, transaction, counts, "animatorOverrideController.originalClip", overrideController, clip.m_OriginalClip.m_FileID, clip.m_OriginalClip.m_PathID, "AnimationClip", null);
                        AddPPtrRelation(connection, transaction, counts, "animatorOverrideController.overrideClip", overrideController, clip.m_OverrideClip.m_FileID, clip.m_OverrideClip.m_PathID, "AnimationClip", null);
                        AddPPtrRelation(connection, transaction, counts, "animatorOverrideController.clipPair", overrideController, 0, 0, "AnimationClipOverride", new
                        {
                            original = DescribePPtr(overrideController.assetsFile, clip.m_OriginalClip.m_FileID, clip.m_OriginalClip.m_PathID, "AnimationClip"),
                            @override = DescribePPtr(overrideController.assetsFile, clip.m_OverrideClip.m_FileID, clip.m_OverrideClip.m_PathID, "AnimationClip"),
                        });
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

        private static void WriteAnimatorControllerClipRelations(SqliteConnection connection, SqliteTransaction transaction, AnimatorController controller, Dictionary<string, long> counts)
        {
            var clips = controller.m_AnimationClips ?? new List<PPtr<AnimationClip>>();
            var usedClipSlots = new HashSet<int>();
            var stateMachines = controller.m_Controller?.m_StateMachineArray ?? new List<StateMachineConstant>();
            var layerMap = BuildAnimatorControllerLayerMap(controller);

            for (var machineIndex = 0; machineIndex < stateMachines.Count; machineIndex++)
            {
                var machine = stateMachines[machineIndex];
                var states = machine.m_StateConstantArray ?? new List<StateConstant>();
                var layers = layerMap.TryGetValue(machineIndex, out var mappedLayers)
                    ? mappedLayers
                    : new List<AnimatorControllerLayerContext>();

                for (var stateIndex = 0; stateIndex < states.Count; stateIndex++)
                {
                    var state = states[stateIndex];
                    var trees = state.m_BlendTreeConstantArray ?? new List<BlendTreeConstant>();
                    var baseLayerClipsByNode = BuildBaseLayerClipsByNode(controller, trees);
                    for (var treeIndex = 0; treeIndex < trees.Count; treeIndex++)
                    {
                        var nodes = trees[treeIndex].m_NodeArray ?? new List<BlendTreeNodeConstant>();
                        for (var nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
                        {
                            var node = nodes[nodeIndex];
                            if (!TryGetAnimatorControllerClipSlot(controller, node.m_ClipID, out var clipSlot, out var clipPtr))
                            {
                                continue;
                            }

                            usedClipSlots.Add(clipSlot);
                            var layerContexts = layers.Count > 0
                                ? layers
                                : new List<AnimatorControllerLayerContext> { new AnimatorControllerLayerContext(-1, 0, 0, 0, 1f, false, false) };

                            // AnimatorController 里的 node.m_ClipID 在原神这类资源里是 m_AnimationClips 的槽位。
                            // 记录 state/blend/node 上下文，避免后续把叶子 clip 误当完整动作。
                            var baseLayerClip = treeIndex > 0 && baseLayerClipsByNode.TryGetValue(nodeIndex, out var baseClip)
                                ? baseClip
                                : null;
                            AddPPtrRelation(connection, transaction, counts, "animatorController.clip", controller, clipPtr.m_FileID, clipPtr.m_PathID, "AnimationClip", new
                            {
                                source = "AnimatorController.m_Controller.stateMachine.blendTree.node",
                                controllerClipIndex = clipSlot,
                                baseLayerClip,
                                layers = layerContexts.Select(layer => new
                                {
                                    layerIndex = layer.LayerIndex,
                                    layerStateMachineIndex = layer.StateMachineIndex,
                                    layerStateMachineMotionSetIndex = layer.StateMachineMotionSetIndex,
                                    layerBinding = layer.Binding,
                                    layerBlendingMode = layer.BlendingMode,
                                    layerDefaultWeight = layer.DefaultWeight,
                                    layerIKPass = layer.IKPass,
                                    layerSyncedLayerAffectsTiming = layer.SyncedLayerAffectsTiming,
                                }).ToArray(),
                                stateMachineIndex = machineIndex,
                                stateIndex,
                                stateName = TryGetTos(controller, state.m_NameID),
                                stateNameId = state.m_NameID,
                                statePath = TryGetTos(controller, state.m_PathID),
                                statePathId = state.m_PathID,
                                stateFullPath = TryGetTos(controller, state.m_FullPathID),
                                stateFullPathId = state.m_FullPathID,
                                stateSpeed = state.m_Speed,
                                stateCycleOffset = state.m_CycleOffset,
                                stateLoop = state.m_Loop,
                                stateMirror = state.m_Mirror,
                                stateBlendTreeIndexArray = state.m_BlendTreeConstantIndexArray,
                                blendTreeIndex = treeIndex,
                                nodeIndex,
                                nodeBlendType = node.m_BlendType,
                                nodeBlendEvent = TryGetTos(controller, node.m_BlendEventID),
                                nodeBlendEventId = node.m_BlendEventID,
                                nodeBlendEventY = TryGetTos(controller, node.m_BlendEventYID),
                                nodeBlendEventYId = node.m_BlendEventYID,
                                nodeChildIndices = node.m_ChildIndices,
                                nodeChildThresholds = node.m_ChildThresholdArray,
                                nodeClipId = node.m_ClipID,
                                nodeClipIndex = node.m_ClipIndex,
                                nodeDuration = node.m_Duration,
                                nodeCycleOffset = node.m_CycleOffset,
                                nodeMirror = node.m_Mirror,
                            });
                        }
                    }
                }
            }

            for (var index = 0; index < clips.Count; index++)
            {
                if (usedClipSlots.Contains(index))
                {
                    continue;
                }

                var ptr = clips[index];
                AddPPtrRelation(connection, transaction, counts, "animatorController.clip", controller, ptr.m_FileID, ptr.m_PathID, "AnimationClip", new
                {
                    source = "AnimatorController.m_AnimationClips",
                    controllerClipIndex = index,
                    hasStateMachineNodeContext = false,
                });
            }
        }

        private static Dictionary<int, JObject> BuildBaseLayerClipsByNode(AnimatorController controller, List<BlendTreeConstant> trees)
        {
            var result = new Dictionary<int, JObject>();
            if (trees == null || trees.Count == 0)
            {
                return result;
            }

            var nodes = trees[0].m_NodeArray ?? new List<BlendTreeNodeConstant>();
            for (var nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
            {
                var node = nodes[nodeIndex];
                if (!TryGetAnimatorControllerClipSlot(controller, node.m_ClipID, out var clipSlot, out var clipPtr))
                {
                    continue;
                }

                result[nodeIndex] = new JObject
                {
                    ["controllerClipIndex"] = clipSlot,
                    ["nodeIndex"] = nodeIndex,
                    ["nodeClipId"] = node.m_ClipID,
                    ["clip"] = DescribePPtr(controller.assetsFile, clipPtr.m_FileID, clipPtr.m_PathID, "AnimationClip"),
                };
            }

            return result;
        }

        private static Dictionary<int, List<AnimatorControllerLayerContext>> BuildAnimatorControllerLayerMap(AnimatorController controller)
        {
            var result = new Dictionary<int, List<AnimatorControllerLayerContext>>();
            var layers = controller.m_Controller?.m_LayerArray ?? new List<LayerConstant>();
            for (var index = 0; index < layers.Count; index++)
            {
                var layer = layers[index];
                var machineIndex = unchecked((int)layer.m_StateMachineIndex);
                if (!result.TryGetValue(machineIndex, out var list))
                {
                    list = new List<AnimatorControllerLayerContext>();
                    result[machineIndex] = list;
                }

                list.Add(new AnimatorControllerLayerContext(
                    index,
                    machineIndex,
                    unchecked((int)layer.m_StateMachineMotionSetIndex),
                    layer.m_Binding,
                    layer.m_DefaultWeight,
                    layer.m_IKPass,
                    layer.m_SyncedLayerAffectsTiming,
                    layer.m_LayerBlendingMode));
            }

            return result;
        }

        private static bool TryGetAnimatorControllerClipSlot(AnimatorController controller, uint clipId, out int clipSlot, out PPtr<AnimationClip> clipPtr)
        {
            var clips = controller.m_AnimationClips ?? new List<PPtr<AnimationClip>>();
            if (clipId <= int.MaxValue)
            {
                var index = unchecked((int)clipId);
                if (index >= 0 && index < clips.Count)
                {
                    clipSlot = index;
                    clipPtr = clips[index];
                    return true;
                }
            }

            clipSlot = -1;
            clipPtr = default;
            return false;
        }

        private static string TryGetTos(AnimatorController controller, uint id)
        {
            return controller.m_TOS != null && controller.m_TOS.TryGetValue(id, out var value)
                ? value
                : null;
        }

        private sealed class AnimatorControllerLayerContext
        {
            public AnimatorControllerLayerContext(int layerIndex, int stateMachineIndex, int stateMachineMotionSetIndex, uint binding, float defaultWeight, bool ikPass, bool syncedLayerAffectsTiming, int blendingMode = 0)
            {
                LayerIndex = layerIndex;
                StateMachineIndex = stateMachineIndex;
                StateMachineMotionSetIndex = stateMachineMotionSetIndex;
                Binding = binding;
                DefaultWeight = defaultWeight;
                IKPass = ikPass;
                SyncedLayerAffectsTiming = syncedLayerAffectsTiming;
                BlendingMode = blendingMode;
            }

            public int LayerIndex { get; }
            public int StateMachineIndex { get; }
            public int StateMachineMotionSetIndex { get; }
            public uint Binding { get; }
            public float DefaultWeight { get; }
            public bool IKPass { get; }
            public bool SyncedLayerAffectsTiming { get; }
            public int BlendingMode { get; }
        }

        private static int TryWriteLightweightRendererRelations(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string sourceRoot,
            SerializedFile assetsFile,
            ObjectInfo objectInfo,
            Dictionary<string, long> counts,
            SourceIndexWriteProgress progress)
        {
            if (!IsLightweightRendererRelationType(objectInfo.classID))
            {
                return 0;
            }

            counts["lightweightRendererObjects"]++;
            progress.LightweightRendererObjects++;

            if (objectInfo.serializedType?.m_Type?.m_Nodes == null || objectInfo.serializedType.m_Type.m_Nodes.Count == 0)
            {
                return IsFallbackParsedRendererType(objectInfo.classID)
                    ? TryWriteFallbackParsedRendererRelations(connection, transaction, sourceRoot, assetsFile, objectInfo, counts, progress, "missing_typetree")
                    : 0;
            }

            if (!IsObjectRangeReadable(assetsFile, objectInfo))
            {
                counts["lightweightRendererFailures"]++;
                progress.LightweightRendererFailures++;
                return 0;
            }

            try
            {
                var reader = new ObjectReader(assetsFile.reader, assetsFile, objectInfo, assetsFile.game);
                var relations = LightweightRendererRelationReader.Read(reader, objectInfo.serializedType.m_Type);
                var relationCount = 0;
                var type = (ClassIDType)objectInfo.classID;
                var fromSource = MakeRelativeOrName(sourceRoot, assetsFile.originalPath ?? assetsFile.fullName);
                var fromType = type.ToString();

                foreach (var relation in relations)
                {
                    if (InsertPPtrRelation(
                        connection,
                        transaction,
                        relation.Relation,
                        "explicit_pptr_lightweight",
                        assetsFile,
                        fromSource,
                        assetsFile.fileName ?? string.Empty,
                        fromType,
                        string.Empty,
                        objectInfo.m_PathID,
                        relation.FileId,
                        relation.PathId,
                        relation.TypeHint,
                        relation.Details))
                    {
                        counts["sourceRelations"]++;
                        counts["lightweightRendererRelations"]++;
                        progress.RelationsWritten = counts["sourceRelations"];
                        progress.LightweightRendererRelations++;
                        relationCount++;
                    }
                }

                return relationCount;
            }
            catch (Exception e) when (e is EndOfStreamException || e is IOException || e is InvalidDataException || e is OverflowException || e is ArgumentOutOfRangeException)
            {
                return IsFallbackParsedRendererType(objectInfo.classID)
                    ? TryWriteFallbackParsedRendererRelations(connection, transaction, sourceRoot, assetsFile, objectInfo, counts, progress, e.GetType().Name)
                    : RecordLightweightRendererFailure(sourceRoot, assetsFile, objectInfo, counts, progress, e.GetType().Name, e);
            }
        }

        private static bool IsLightweightRendererRelationType(int classId)
        {
            return classId == (int)ClassIDType.MeshRenderer
                || classId == (int)ClassIDType.SkinnedMeshRenderer
                || classId == (int)ClassIDType.Material
                || classId == (int)ClassIDType.ParticleSystem
                || classId == (int)ClassIDType.ParticleSystemRenderer
                || classId == (int)ClassIDType.ParticleSystemForceField
                || classId == (int)ClassIDType.LineRenderer
                || classId == (int)ClassIDType.TrailRenderer
                || classId == (int)ClassIDType.VFXRenderer
                || classId == (int)ClassIDType.GPUParticleSystemRenderer;
        }

        private static bool IsFallbackParsedRendererType(int classId)
        {
            return classId == (int)ClassIDType.MeshRenderer
                || classId == (int)ClassIDType.SkinnedMeshRenderer;
        }

        private static int TryWriteFallbackParsedRendererRelations(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string sourceRoot,
            SerializedFile assetsFile,
            ObjectInfo objectInfo,
            Dictionary<string, long> counts,
            SourceIndexWriteProgress progress,
            string reason)
        {
            try
            {
                var reader = new ObjectReader(assetsFile.reader, assetsFile, objectInfo, assetsFile.game);
                AnimeStudio.Object renderer = objectInfo.classID == (int)ClassIDType.SkinnedMeshRenderer
                    ? new SkinnedMeshRenderer(reader)
                    : new MeshRenderer(reader);
                var before = counts["sourceRelations"];
                WriteParsedRendererRelations(connection, transaction, renderer, sourceRoot, counts, "explicit_pptr_lightweight_fallback");
                var added = (int)(counts["sourceRelations"] - before);
                counts["lightweightRendererRelations"] += added;
                progress.RelationsWritten = counts["sourceRelations"];
                progress.LightweightRendererRelations += added;
                return added;
            }
            catch (Exception e) when (e is EndOfStreamException || e is IOException || e is InvalidDataException || e is OverflowException || e is ArgumentOutOfRangeException)
            {
                return RecordLightweightRendererFailure(sourceRoot, assetsFile, objectInfo, counts, progress, reason, e);
            }
        }

        private static int RecordLightweightRendererFailure(
            string sourceRoot,
            SerializedFile assetsFile,
            ObjectInfo objectInfo,
            Dictionary<string, long> counts,
            SourceIndexWriteProgress progress,
            string reason,
            Exception e)
        {
            counts["lightweightRendererFailures"]++;
            progress.LightweightRendererFailures++;
            ProfileLogger.Event("source_index_lightweight_renderer_failed", new Dictionary<string, object>
            {
                ["source"] = MakeRelativeOrName(sourceRoot, assetsFile.originalPath ?? assetsFile.fullName),
                ["file"] = assetsFile.fileName ?? string.Empty,
                ["pathId"] = objectInfo.m_PathID,
                ["type"] = Enum.IsDefined(typeof(ClassIDType), objectInfo.classID) ? ((ClassIDType)objectInfo.classID).ToString() : objectInfo.classID.ToString(CultureInfo.InvariantCulture),
                ["reason"] = reason,
                ["errorType"] = e.GetType().Name,
                ["message"] = e.Message,
            });
            return 0;
        }

        private static void WriteParsedRendererRelations(SqliteConnection connection, SqliteTransaction transaction, AnimeStudio.Object renderer, string sourceRoot, Dictionary<string, long> counts, string confidence)
        {
            if (renderer is not Renderer typedRenderer)
            {
                return;
            }

            void Add(string relation, int fileId, long pathId, string typeHint, object details = null)
            {
                if (InsertPPtrRelation(
                    connection,
                    transaction,
                    relation,
                    confidence,
                    renderer.assetsFile,
                    MakeRelativeOrName(sourceRoot, renderer.assetsFile?.originalPath ?? renderer.assetsFile?.fullName),
                    renderer.assetsFile?.fileName ?? string.Empty,
                    renderer.type.ToString(),
                    renderer.Name ?? string.Empty,
                    renderer.m_PathID,
                    fileId,
                    pathId,
                    typeHint,
                    details))
                {
                    counts["sourceRelations"]++;
                }
            }

            Add("component.gameObject", typedRenderer.m_GameObject.m_FileID, typedRenderer.m_GameObject.m_PathID, "GameObject");
            foreach (var ptr in typedRenderer.m_Materials ?? Enumerable.Empty<PPtr<Material>>())
            {
                Add("renderer.material", ptr.m_FileID, ptr.m_PathID, "Material");
            }

            if (renderer is SkinnedMeshRenderer skinned)
            {
                Add("skinnedMeshRenderer.mesh", skinned.m_Mesh.m_FileID, skinned.m_Mesh.m_PathID, "Mesh");
                if (skinned.m_RootBone != null && !skinned.m_RootBone.IsNull)
                {
                    Add("skinnedMeshRenderer.rootBone", skinned.m_RootBone.m_FileID, skinned.m_RootBone.m_PathID, "Transform");
                }

                var bones = skinned.m_Bones ?? new List<PPtr<Transform>>();
                Add("skinnedMeshRenderer.bones", 0, 0, "Transform", new
                {
                    count = bones.Count,
                    targets = bones.Take(512).Select(x => DescribePPtr(skinned.assetsFile, x.m_FileID, x.m_PathID, "Transform")).ToArray(),
                    truncated = bones.Count > 512,
                });
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
            return InsertPPtrRelation(
                connection,
                transaction,
                relation,
                "explicit_pptr",
                from.assetsFile,
                from.assetsFile?.originalPath ?? from.assetsFile?.fullName ?? string.Empty,
                from.assetsFile?.fileName ?? string.Empty,
                from.type.ToString(),
                from.Name ?? string.Empty,
                from.m_PathID,
                toFileId,
                toPathId,
                toTypeHint,
                details);
        }

        private static bool InsertPPtrRelation(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string relation,
            string confidence,
            SerializedFile fromAssetsFile,
            string fromSource,
            string fromFile,
            string fromType,
            string fromName,
            long fromPathId,
            int toFileId,
            long toPathId,
            string toTypeHint,
            object details)
        {
            if (toPathId == 0 && details == null)
            {
                return false;
            }

            var target = DescribePPtr(fromAssetsFile, toFileId, toPathId, toTypeHint);
            var raw = new
            {
                kind = "sourceRelation",
                relation,
                confidence,
                from = new
                {
                    source = fromSource,
                    file = fromFile,
                    type = fromType,
                    name = fromName,
                    pathId = fromPathId,
                },
                to = target,
                details,
            };
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO source_relations(relation, confidence, from_source, from_file, from_type, from_name, from_path_id, to_file_id, to_file, to_type_hint, to_path_id, target_count, raw_json)
VALUES ($relation, $confidence, $fromSource, $fromFile, $fromType, $fromName, $fromPathId, $toFileId, $toFile, $toTypeHint, $toPathId, $targetCount, $rawJson);";
            command.Parameters.AddWithValue("$relation", relation);
            command.Parameters.AddWithValue("$confidence", confidence);
            command.Parameters.AddWithValue("$fromSource", fromSource ?? string.Empty);
            command.Parameters.AddWithValue("$fromFile", fromFile ?? string.Empty);
            command.Parameters.AddWithValue("$fromType", fromType ?? string.Empty);
            command.Parameters.AddWithValue("$fromName", fromName ?? string.Empty);
            command.Parameters.AddWithValue("$fromPathId", fromPathId);
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
            var entries = bindings.Select(x => new
            {
                path = (string)null,
                pathHash = x.path,
                type = x.typeID.ToString(),
                typeId = (int)x.typeID,
                attribute = x.attribute,
                customType = ((BindingCustomType)x.customType).ToString(),
                customTypeId = x.customType,
                isPPtrCurve = x.isPPtrCurve == 1,
            }).ToArray();
            var raw = new
            {
                kind = "sourceAnimationBindings",
                confidence = "structural",
                pathResolution = "deferred_source_index_hash_only",
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

        private static void WriteSummary(SqliteConnection connection, string outputRoot, string dbPath, string sourceRoot, int fileCount, Dictionary<string, long> counts)
        {
            var summaryPath = GetSourceIndexSummaryPath(outputRoot, dbPath);
            var summary = new JObject
            {
                ["schemaVersion"] = 1,
                ["database"] = dbPath,
                ["sourceRoot"] = sourceRoot,
                ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["animationRelationFeatures"] = "animatorController.clip,animatorOverrideController.overrideSet,animatorOverrideController.originalClip,animatorOverrideController.overrideClip,animatorOverrideController.clipPair,animation.clip",
                ["animationRelationHealth"] = BuildAnimationRelationHealth(connection),
                ["rule"] = "索引要全，导出要精。Source SQLite v1 stores Unity source files, SerializedFiles, Objects, externals, PPtr relations, and animation bindings.",
                ["inputFileCount"] = fileCount,
                ["counts"] = JObject.FromObject(counts),
            };
            File.WriteAllText(summaryPath, summary.ToString(Formatting.Indented));
            var readmePath = UsesDefaultSourceIndexPath(outputRoot, dbPath)
                ? Path.Combine(outputRoot, "UNITY_SOURCE_INDEX_README.md")
                : Path.Combine(Path.GetDirectoryName(summaryPath) ?? outputRoot, Path.GetFileNameWithoutExtension(dbPath) + ".README.md");
            File.WriteAllText(readmePath,
                "# Unity Source SQLite Index\n\n" +
                "这是完整 Unity 源目录索引，不是导出结果索引。它用于一次性记录源文件、SerializedFile、Object、external CAB/PPtr、Animator/Animation/Renderer/Skin/AnimationClip binding 等关系。\n\n" +
                "核心原则：索引要全，导出要精。源索引中出现的对象不代表默认素材库会导出，也不代表视觉验收通过。\n\n" +
                "Renderer/Skin 关系采用 SourceIndex 专用轻量读取：优先按 Unity TypeTree 捕获 `component.gameObject`、`renderer.material`、`skinnedMeshRenderer.mesh/rootBone/bones`；TypeTree 不可用时仅对当前 Renderer 小对象做受控 fallback 解析。失败会记录为 partial/failure，不会阻塞整个索引。\n\n" +
                "主要表：`source_files`、`serialized_files`、`source_objects`、`source_externals`、`source_relations`、`source_animation_bindings`。\n\n" +
                "性能日志：启用 `--profile_log` 后，重点比较 `source_index_load_batch`、`source_index_write_batch`、`source_index_load_batch_heartbeat`、`source_index_write_batch_heartbeat`、`source_index_create_sql_indexes` 和 `source_index_total`。长时间大文件处理时，heartbeat 会持续记录当前文件、对象序号、关系计数和内存。\n\n" +
                "统计项：`lightweightRendererObjects` 是索引阶段尝试补关系的 Renderer 数；`lightweightRendererRelations` 是成功补到的 mesh/material/bone/gameObject 关系数；`lightweightRendererFailures` 是单对象失败数，通常代表版本字段不兼容或对象数据异常。\n\n" +
                "第二阶段当前重点是建好可查询底座；后续导出器可以逐步改为读取这个数据库来减少重复扫描。\n");
        }

        private static JObject BuildAnimationRelationHealthReport(SqliteConnection connection, string dbPath)
        {
            return new JObject
            {
                ["schemaVersion"] = 1,
                ["database"] = dbPath,
                ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["metadata"] = new JObject
                {
                    ["schemaVersion"] = ScalarString(connection, "SELECT value FROM metadata WHERE key='schemaVersion' LIMIT 1;"),
                    ["sourceRoot"] = ScalarString(connection, "SELECT value FROM metadata WHERE key='sourceRoot' LIMIT 1;"),
                    ["game"] = ScalarString(connection, "SELECT value FROM metadata WHERE key='game' LIMIT 1;"),
                    ["animationRelationFeatures"] = ScalarString(connection, "SELECT value FROM metadata WHERE key='animationRelationFeatures' LIMIT 1;"),
                    ["createdUtc"] = ScalarString(connection, "SELECT value FROM metadata WHERE key='createdUtc' LIMIT 1;"),
                },
                ["animationRelationHealth"] = BuildAnimationRelationHealth(connection),
                ["avatarOracleHealth"] = BuildAvatarOracleHealth(connection),
            };
        }

        private static JObject BuildAnimationRelationHealth(SqliteConnection connection)
        {
            var overrideControllerObjects = ScalarLong(connection, "SELECT COUNT(*) FROM source_objects WHERE type='AnimatorOverrideController';");
            var relationCounts = new JObject
            {
                ["animator.controller"] = SourceRelationCount(connection, "animator.controller"),
                ["animatorController.clip"] = SourceRelationCount(connection, "animatorController.clip"),
                ["animation.clip"] = SourceRelationCount(connection, "animation.clip"),
                ["animatorOverrideController.baseController"] = SourceRelationCount(connection, "animatorOverrideController.baseController"),
                ["animatorOverrideController.overrideSet"] = SourceRelationCount(connection, "animatorOverrideController.overrideSet"),
                ["animatorOverrideController.originalClip"] = SourceRelationCount(connection, "animatorOverrideController.originalClip"),
                ["animatorOverrideController.overrideClip"] = SourceRelationCount(connection, "animatorOverrideController.overrideClip"),
                ["animatorOverrideController.clipPair"] = SourceRelationCount(connection, "animatorOverrideController.clipPair"),
            };
            var clipPairs = (long)relationCounts["animatorOverrideController.clipPair"];
            var overrideSets = (long)relationCounts["animatorOverrideController.overrideSet"];
            var nonEmptyOverrideSets = SourceRelationTargetCountPositive(connection, "animatorOverrideController.overrideSet");
            var staleOverridePairs = overrideControllerObjects > 0
                && (overrideSets == 0 || (nonEmptyOverrideSets > 0 && clipPairs == 0));
            return new JObject
            {
                ["status"] = staleOverridePairs ? "warning" : "ok",
                ["objectCounts"] = new JObject
                {
                    ["AnimatorOverrideController"] = overrideControllerObjects,
                },
                ["relationCounts"] = relationCounts,
                ["nonEmptyOverrideSetCount"] = nonEmptyOverrideSets,
                ["staleOverridePairIndex"] = staleOverridePairs,
                ["note"] = staleOverridePairs
                    ? "源目录存在 AnimatorOverrideController，但缺少当前工具写入的 animatorOverrideController.overrideSet/clipPair 精确标记。旧索引即使有分离的 originalClip/overrideClip，也不能可靠表达 original -> override 或空替换表；请用当前工具重建源索引。"
                    : "源索引包含当前工具可检查的动画显式关系。Library 候选仍需结合 glTF 预览验证。"
            };
        }

        private static JObject BuildAvatarOracleHealth(SqliteConnection connection)
        {
            var avatarObjects = ScalarLong(connection, "SELECT COUNT(*) FROM source_objects WHERE type='Avatar';");
            var humanDescriptionAvatars = ScalarLong(connection, @"
SELECT COUNT(*)
FROM source_objects
WHERE type='Avatar'
  AND COALESCE(json_extract(raw_json, '$.avatar.hasHumanDescription'), 0)=1;");
            var avatarConstantOracleAvatars = ScalarLong(connection, @"
SELECT COUNT(*)
FROM source_objects
WHERE type='Avatar'
  AND json_type(json_extract(raw_json, '$.avatar.oracle'))='object';");
            var completeOracleAvatars = ScalarLong(connection, @"
SELECT COUNT(*)
FROM source_objects
WHERE type='Avatar'
  AND COALESCE(json_array_length(json_extract(raw_json, '$.avatar.oracle.humanBoneIndex')), 0) > 0
  AND COALESCE(json_array_length(json_extract(raw_json, '$.avatar.oracle.humanSkeleton.pose')), 0)
      >= COALESCE(json_extract(raw_json, '$.avatar.oracle.humanSkeleton.nodeCount'), 1)
  AND COALESCE(json_array_length(json_extract(raw_json, '$.avatar.oracle.avatarSkeleton.defaultPose')), 0)
      >= COALESCE(json_extract(raw_json, '$.avatar.oracle.avatarSkeleton.nodeCount'), 1);");

            return new JObject
            {
                ["status"] = avatarObjects == 0
                    ? "not_applicable"
                    : completeOracleAvatars > 0 ? "ok" : "warning",
                ["avatarObjects"] = avatarObjects,
                ["humanDescriptionAvatars"] = humanDescriptionAvatars,
                ["avatarConstantOracleAvatars"] = avatarConstantOracleAvatars,
                ["completeAvatarConstantOracleAvatars"] = completeOracleAvatars,
                ["humanDescriptionCoverage"] = Ratio(humanDescriptionAvatars, avatarObjects),
                ["avatarConstantOracleCoverage"] = Ratio(avatarConstantOracleAvatars, avatarObjects),
                ["completeAvatarConstantOracleCoverage"] = Ratio(completeOracleAvatars, avatarObjects),
                ["note"] = completeOracleAvatars > 0
                    ? "源索引包含 AvatarConstant oracle 元数据。它可用于后续恢复 Unity bake prefab/avatar，但不能单独标记为生产 bake ready。"
                    : "源索引缺少完整 AvatarConstant oracle 元数据；Humanoid/Muscle bake 只能依赖完整 HumanDescription 或 Unity 工程内真实 prefab。"
            };
        }

        private static double Ratio(long part, long total)
        {
            return total <= 0 ? 0.0 : Math.Round((double)part / total, 6);
        }

        private static long SourceRelationCount(SqliteConnection connection, string relation)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM source_relations WHERE relation=$relation;";
            command.Parameters.AddWithValue("$relation", relation);
            return Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }

        private static long SourceRelationTargetCountPositive(SqliteConnection connection, string relation)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM source_relations WHERE relation=$relation AND COALESCE(target_count, 0) > 0;";
            command.Parameters.AddWithValue("$relation", relation);
            return Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }

        private static long ScalarLong(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }

        private static string ScalarString(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar() as string;
        }

        private static string GetSourceIndexSummaryPath(string outputRoot, string dbPath)
        {
            if (UsesDefaultSourceIndexPath(outputRoot, dbPath))
            {
                return Path.Combine(outputRoot, "unity_source_index_summary.json");
            }

            var directory = Path.GetDirectoryName(dbPath) ?? outputRoot;
            var name = Path.GetFileNameWithoutExtension(dbPath);
            return Path.Combine(directory, name + ".summary.json");
        }

        private static bool UsesDefaultSourceIndexPath(string outputRoot, string dbPath)
        {
            var defaultPath = Path.GetFullPath(Path.Combine(outputRoot, "unity_source_index.db"));
            return string.Equals(Path.GetFullPath(dbPath), defaultPath, StringComparison.OrdinalIgnoreCase);
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

        private static List<string[]> BuildLoadBatches(string[] files, int batchFiles)
        {
            var batches = new List<string[]>();
            var current = new List<string>(batchFiles);

            foreach (var file in files)
            {
                var length = SafeFileLength(file);
                if (length >= LargeSourceIndexFileBytes)
                {
                    if (current.Count > 0)
                    {
                        batches.Add(current.ToArray());
                        current.Clear();
                    }

                    batches.Add(new[] { file });
                    continue;
                }

                current.Add(file);
                if (current.Count >= batchFiles)
                {
                    batches.Add(current.ToArray());
                    current.Clear();
                }
            }

            if (current.Count > 0)
            {
                batches.Add(current.ToArray());
            }

            return batches;
        }

        private static bool IsObjectRangeReadable(SerializedFile assetsFile, ObjectInfo objectInfo)
        {
            if (assetsFile?.reader == null || objectInfo == null)
            {
                return false;
            }

            if (objectInfo.byteStart < 0 || objectInfo.byteSize > int.MaxValue)
            {
                return false;
            }

            var end = objectInfo.byteStart + (long)objectInfo.byteSize;
            return end >= objectInfo.byteStart && end <= assetsFile.reader.Length;
        }

        private static IDisposable StartSourceIndexLoadHeartbeat(
            int batchIndex,
            int totalBatches,
            int batchFileCount,
            long batchBytes,
            string largestFile,
            long largestFileBytes,
            AssetsManager manager)
        {
            return new SourceIndexHeartbeat(batchIndex, totalBatches, batchFileCount, batchBytes, largestFile, largestFileBytes, manager);
        }

        private static IDisposable StartSourceIndexWriteHeartbeat(SourceIndexWriteProgress progress)
        {
            return new SourceIndexWriteHeartbeat(progress);
        }

        private static bool ShouldParseObjectForSourceIndex(ClassIDType type)
        {
            return type is ClassIDType.GameObject
                or ClassIDType.Transform
                or ClassIDType.RectTransform
                or ClassIDType.Animator
                or ClassIDType.Animation
                or ClassIDType.AnimatorController
                or ClassIDType.AnimatorOverrideController
                or ClassIDType.MeshFilter
                or ClassIDType.AnimationClip
                or ClassIDType.Avatar
                or ClassIDType.AssetBundle
                or ClassIDType.ResourceManager;
        }

        private sealed class LightweightPPtrRelation
        {
            public string Relation { get; init; }
            public int FileId { get; init; }
            public long PathId { get; init; }
            public string TypeHint { get; init; }
            public object Details { get; init; }
        }

        private sealed class LightweightRendererRelationReader
        {
            private readonly ObjectReader reader;
            private readonly List<TypeTreeNode> nodes;
            private readonly List<LightweightPPtrRelation> relations = new();
            private readonly List<object> bones = new();
            private readonly Dictionary<string, object> primitiveHints = new(StringComparer.OrdinalIgnoreCase);
            private readonly Stack<string> mapKeyStack = new();

            private LightweightRendererRelationReader(ObjectReader reader, TypeTree typeTree)
            {
                this.reader = reader;
                nodes = typeTree.m_Nodes;
            }

            public static List<LightweightPPtrRelation> Read(ObjectReader reader, TypeTree typeTree)
            {
                var collector = new LightweightRendererRelationReader(reader, typeTree);
                collector.ReadRoot();
                return collector.relations;
            }

            private void ReadRoot()
            {
                reader.Reset();
                for (var i = 1; i < nodes.Count; i++)
                {
                    _ = ReadNode(nodes, reader, ref i, string.Empty);
                }

                if (bones.Count > 0)
                {
                    relations.Add(new LightweightPPtrRelation
                    {
                        Relation = "skinnedMeshRenderer.bones",
                        TypeHint = "Transform",
                        Details = new
                        {
                            count = bones.Count,
                            targets = bones.Take(512).ToArray(),
                            truncated = bones.Count > 512,
                        },
                    });
                }

                var vfxHints = BuildVfxPreviewHints();
                if (vfxHints.Count > 0)
                {
                    relations.Add(new LightweightPPtrRelation
                    {
                        Relation = reader.type == ClassIDType.Material ? "material.metadata" : "vfx.metadata",
                        TypeHint = reader.type == ClassIDType.Material ? "Material" : "VFX",
                        Details = new
                        {
                            count = vfxHints.Count,
                            hints = vfxHints,
                            schema = 1,
                        },
                    });
                }
            }

            private object ReadNode(List<TypeTreeNode> nodeList, EndianBinaryReader binaryReader, ref int index, string parentPath)
            {
                var node = nodeList[index];
                var path = string.IsNullOrEmpty(parentPath) ? node.m_Name : $"{parentPath}.{node.m_Name}";
                var align = (node.m_MetaFlag & 0x4000) != 0;
                object returnValue = null;

                if (node.m_Type != null && node.m_Type.StartsWith("PPtr<", StringComparison.Ordinal))
                {
                    var ptr = new PPtr<AnimeStudio.Object>(reader);
                    CapturePPtr(path, ptr.m_FileID, ptr.m_PathID);
                    index = GetNodeEnd(nodeList, index) - 1;
                    if (align)
                    {
                        binaryReader.AlignStream();
                    }
                    return null;
                }

                object primitiveValue = null;
                var hasPrimitiveValue = false;
                switch (node.m_Type)
                {
                    case "SInt8":
                        primitiveValue = binaryReader.ReadSByte();
                        hasPrimitiveValue = true;
                        break;
                    case "UInt8":
                        primitiveValue = binaryReader.ReadByte();
                        hasPrimitiveValue = true;
                        break;
                    case "char":
                        binaryReader.ReadBytes(2);
                        break;
                    case "short":
                    case "SInt16":
                        primitiveValue = binaryReader.ReadInt16();
                        hasPrimitiveValue = true;
                        break;
                    case "UInt16":
                    case "unsigned short":
                        primitiveValue = binaryReader.ReadUInt16();
                        hasPrimitiveValue = true;
                        break;
                    case "int":
                    case "SInt32":
                        primitiveValue = binaryReader.ReadInt32();
                        hasPrimitiveValue = true;
                        break;
                    case "UInt32":
                    case "unsigned int":
                    case "Type*":
                        primitiveValue = binaryReader.ReadUInt32();
                        hasPrimitiveValue = true;
                        break;
                    case "long long":
                    case "SInt64":
                        primitiveValue = binaryReader.ReadInt64();
                        hasPrimitiveValue = true;
                        break;
                    case "UInt64":
                    case "unsigned long long":
                    case "FileSize":
                        primitiveValue = binaryReader.ReadUInt64();
                        hasPrimitiveValue = true;
                        break;
                    case "float":
                        primitiveValue = binaryReader.ReadSingle();
                        hasPrimitiveValue = true;
                        break;
                    case "double":
                        primitiveValue = binaryReader.ReadDouble();
                        hasPrimitiveValue = true;
                        break;
                    case "bool":
                        primitiveValue = binaryReader.ReadBoolean();
                        hasPrimitiveValue = true;
                        break;
                    case "string":
                        returnValue = binaryReader.ReadAlignedString();
                        index = GetNodeEnd(nodeList, index) - 1;
                        break;
                    case "TypelessData":
                        {
                            var size = binaryReader.ReadInt32();
                            if (size < 0 || size > reader.BytesLeft())
                            {
                                throw new InvalidDataException($"Invalid TypelessData size {size} while reading lightweight renderer relation.");
                            }
                            binaryReader.ReadBytes(size);
                            index = GetNodeEnd(nodeList, index) - 1;
                            break;
                        }
                    case "map":
                        ReadMap(nodeList, binaryReader, ref index, path);
                        break;
                    default:
                        if (index < nodeList.Count - 1 && nodeList[index + 1].m_Type == "Array")
                        {
                            ReadArray(nodeList, binaryReader, ref index, path);
                        }
                        else
                        {
                            var end = GetNodeEnd(nodeList, index);
                            for (var child = index + 1; child < end; child++)
                            {
                                _ = ReadNode(nodeList, binaryReader, ref child, path);
                            }
                            index = end - 1;
                        }
                        break;
                }

                if (hasPrimitiveValue)
                {
                    CapturePrimitiveHint(path, node.m_Name, primitiveValue);
                    returnValue = primitiveValue;
                }

                if (align)
                {
                    binaryReader.AlignStream();
                }

                return returnValue;
            }

            private void ReadArray(List<TypeTreeNode> nodeList, EndianBinaryReader binaryReader, ref int index, string path)
            {
                var arrayAlign = (nodeList[index + 1].m_MetaFlag & 0x4000) != 0;
                var vector = GetNodes(nodeList, index);
                index += vector.Count - 1;
                var size = binaryReader.ReadInt32();
                if (size < 0 || size > SourceIndexMaxLightweightArrayCount)
                {
                    throw new InvalidDataException($"Invalid lightweight renderer array size {size} at {path}.");
                }

                for (var item = 0; item < size; item++)
                {
                    var dataIndex = 3;
                    _ = ReadNode(vector, binaryReader, ref dataIndex, path);
                }

                if (arrayAlign)
                {
                    binaryReader.AlignStream();
                }
            }

            private void ReadMap(List<TypeTreeNode> nodeList, EndianBinaryReader binaryReader, ref int index, string path)
            {
                var map = GetNodes(nodeList, index);
                index += map.Count - 1;
                var first = GetNodes(map, 4);
                var next = 4 + first.Count;
                var second = GetNodes(map, next);
                var size = binaryReader.ReadInt32();
                if (size < 0 || size > SourceIndexMaxLightweightArrayCount)
                {
                    throw new InvalidDataException($"Invalid lightweight renderer map size {size} at {path}.");
                }

                for (var item = 0; item < size; item++)
                {
                    var firstIndex = 0;
                    var keyValue = ReadNode(first, binaryReader, ref firstIndex, $"{path}.key");
                    var mapKey = keyValue switch
                    {
                        string s when !string.IsNullOrWhiteSpace(s) => s,
                        null => string.Empty,
                        _ => keyValue.ToString(),
                    };

                    var secondIndex = 0;
                    if (!string.IsNullOrWhiteSpace(mapKey))
                    {
                        mapKeyStack.Push(mapKey);
                    }
                    try
                    {
                        _ = ReadNode(second, binaryReader, ref secondIndex, $"{path}.value");
                    }
                    finally
                    {
                        if (!string.IsNullOrWhiteSpace(mapKey))
                        {
                            mapKeyStack.Pop();
                        }
                    }
                }
            }

            private void CapturePPtr(string path, int fileId, long pathId)
            {
                if (pathId == 0)
                {
                    return;
                }

                if (reader.type == ClassIDType.Material)
                {
                    if (EndsWithField(path, "m_Shader"))
                    {
                        relations.Add(new LightweightPPtrRelation { Relation = "material.shader", FileId = fileId, PathId = pathId, TypeHint = "Shader" });
                        return;
                    }

                    if (EndsWithField(path, "m_Texture") || ContainsField(path, "m_TexEnvs"))
                    {
                        relations.Add(new LightweightPPtrRelation
                        {
                            Relation = "material.texture",
                            FileId = fileId,
                            PathId = pathId,
                            TypeHint = "Texture",
                            Details = new
                            {
                                path,
                                slot = TryExtractMaterialSlot(path),
                            },
                        });
                        return;
                    }
                }

                if (EndsWithField(path, "m_GameObject"))
                {
                    relations.Add(new LightweightPPtrRelation { Relation = "component.gameObject", FileId = fileId, PathId = pathId, TypeHint = "GameObject" });
                    return;
                }

                if (EndsWithField(path, "m_Mesh"))
                {
                    relations.Add(new LightweightPPtrRelation { Relation = "skinnedMeshRenderer.mesh", FileId = fileId, PathId = pathId, TypeHint = "Mesh" });
                    return;
                }

                if (EndsWithField(path, "m_RootBone"))
                {
                    relations.Add(new LightweightPPtrRelation { Relation = "skinnedMeshRenderer.rootBone", FileId = fileId, PathId = pathId, TypeHint = "Transform" });
                    return;
                }

                if (ContainsField(path, "m_Materials"))
                {
                    relations.Add(new LightweightPPtrRelation { Relation = "renderer.material", FileId = fileId, PathId = pathId, TypeHint = "Material" });
                    return;
                }

                if (IsVfxLightweightType(reader.type)
                    && (EndsWithField(path, "m_Texture") || EndsWithField(path, "m_NormalMap") || EndsWithField(path, "m_MaskMap")))
                {
                    relations.Add(new LightweightPPtrRelation
                    {
                        Relation = "vfx.texture",
                        FileId = fileId,
                        PathId = pathId,
                        TypeHint = "Texture",
                        Details = new
                        {
                            path,
                        },
                    });
                    return;
                }

                if (ContainsField(path, "m_Bones"))
                {
                    bones.Add(new
                    {
                        fileId,
                        pathId,
                        typeHint = "Transform",
                    });
                }
            }

            private void CapturePrimitiveHint(string path, string fieldName, object value)
            {
                if (!IsVfxLightweightType(reader.type) && reader.type != ClassIDType.Material)
                {
                    return;
                }

                var key = reader.type == ClassIDType.Material
                    ? GetMaterialHintKey(path, fieldName)
                    : GetVfxHintKey(path, fieldName);
                if (string.IsNullOrWhiteSpace(key) || primitiveHints.ContainsKey(key))
                {
                    return;
                }

                primitiveHints[key] = NormalizePrimitiveHint(value);
            }

            private static object NormalizePrimitiveHint(object value)
            {
                return value switch
                {
                    float f when float.IsFinite(f) => Math.Round(f, 5),
                    double d when double.IsFinite(d) => Math.Round(d, 5),
                    _ => value,
                };
            }

            private static bool IsVfxLightweightType(ClassIDType type)
            {
                return type == ClassIDType.ParticleSystem
                    || type == ClassIDType.ParticleSystemRenderer
                    || type == ClassIDType.ParticleSystemForceField
                    || type == ClassIDType.LineRenderer
                    || type == ClassIDType.TrailRenderer
                    || type == ClassIDType.VFXRenderer
                    || type == ClassIDType.GPUParticleSystemRenderer;
            }

            private static string GetVfxHintKey(string path, string fieldName)
            {
                var normalized = path.Replace(".data", "", StringComparison.Ordinal)
                    .Replace(".Array", "", StringComparison.Ordinal);
                var lower = normalized.ToLowerInvariant();

                if (EndsWithAny(lower, "m_rendermode", "m_renderingmode", "m_alignment", "m_sortingmode", "m_maskinteraction"))
                {
                    return "renderer." + fieldName;
                }
                if (EndsWithAny(lower, "m_lengthscale", "m_velocityscale", "m_cameravelocityscale", "m_normalsdirection"))
                {
                    return "renderer." + fieldName;
                }
                if (EndsWithAny(lower, "m_looping", "m_loop", "m_prewarm", "m_playonawake", "m_maxparticles", "m_autorandomseed"))
                {
                    return "main." + fieldName;
                }
                if (EndsWithAny(lower, "m_lengthinsec", "m_duration", "m_simulationspeed", "m_startdelay", "m_startspeed", "m_startsize", "m_startrotation", "m_startlifetime"))
                {
                    return "main." + fieldName;
                }
                if (lower.Contains("emission", StringComparison.Ordinal) && IsLikelyUsefulVfxLeaf(fieldName))
                {
                    return "emission." + fieldName;
                }
                if (lower.Contains("shape", StringComparison.Ordinal) && IsLikelyUsefulVfxLeaf(fieldName))
                {
                    return "shape." + fieldName;
                }
                if (lower.Contains("velocity", StringComparison.Ordinal) && IsLikelyUsefulVfxLeaf(fieldName))
                {
                    return "velocity." + fieldName;
                }
                if (lower.Contains("size", StringComparison.Ordinal) && IsLikelyUsefulVfxLeaf(fieldName))
                {
                    return "size." + fieldName;
                }
                if (lower.Contains("color", StringComparison.Ordinal) && IsLikelyUsefulVfxLeaf(fieldName))
                {
                    return "color." + fieldName;
                }
                if ((lower.Contains("texturesheet", StringComparison.Ordinal) || lower.Contains("sheet", StringComparison.Ordinal))
                    && IsLikelyUsefulVfxLeaf(fieldName))
                {
                    return "textureSheet." + fieldName;
                }
                if (lower.Contains("trail", StringComparison.Ordinal) && IsLikelyUsefulVfxLeaf(fieldName))
                {
                    return "trail." + fieldName;
                }

                return string.Empty;
            }

            private Dictionary<string, object> BuildVfxPreviewHints()
            {
                if (primitiveHints.Count == 0)
                {
                    return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                }

                var hints = primitiveHints
                    .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(160)
                    .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
                hints["sourceClass"] = reader.type.ToString();
                return hints;
            }

            private string TryExtractMaterialSlot(string path)
            {
                if (path.Contains("m_TexEnvs", StringComparison.Ordinal)
                    && mapKeyStack.Count > 0
                    && !string.IsNullOrWhiteSpace(mapKeyStack.Peek()))
                {
                    return mapKeyStack.Peek();
                }

                return path.Contains("m_TexEnvs", StringComparison.Ordinal) ? "m_TexEnvs" : string.Empty;
            }

            private static string GetMaterialHintKey(string path, string fieldName)
            {
                var normalized = path.Replace(".data", "", StringComparison.Ordinal)
                    .Replace(".Array", "", StringComparison.Ordinal);
                var lower = normalized.ToLowerInvariant();
                if (lower.Contains("m_colors", StringComparison.Ordinal) && IsLikelyUsefulVfxLeaf(fieldName))
                {
                    return "material.color." + fieldName;
                }
                if (lower.Contains("m_floats", StringComparison.Ordinal) && IsLikelyUsefulVfxLeaf(fieldName))
                {
                    return "material.float." + fieldName;
                }
                if (lower.Contains("m_texenvs", StringComparison.Ordinal)
                    && (EndsWithAny(lower, "m_scale.x", "m_scale.y", "m_offset.x", "m_offset.y") || IsLikelyUsefulVfxLeaf(fieldName)))
                {
                    return "material.texEnv." + fieldName;
                }

                return string.Empty;
            }

            private static bool IsLikelyUsefulVfxLeaf(string fieldName)
            {
                return fieldName is "enabled" or "m_Enabled"
                    or "type" or "m_Type"
                    or "radius" or "m_Radius"
                    or "angle" or "m_Angle"
                    or "length" or "m_Length"
                    or "arc" or "m_Arc"
                    or "rate" or "m_Rate"
                    or "scalar" or "m_Scalar"
                    or "minScalar" or "maxScalar"
                    or "minMaxState" or "m_MinMaxState"
                    or "x" or "y" or "z" or "w"
                    or "r" or "g" or "b" or "a"
                    or "tilesX" or "tilesY" or "m_TilesX" or "m_TilesY"
                    or "animationType" or "m_AnimationType"
                    or "rowMode" or "m_RowMode"
                    or "speed" or "m_Speed"
                    or "randomize" or "m_Randomize";
            }

            private static bool EndsWithAny(string value, params string[] suffixes)
            {
                return suffixes.Any(suffix => value.EndsWith(suffix, StringComparison.Ordinal));
            }

            private static bool EndsWithField(string path, string fieldName)
            {
                return string.Equals(path, fieldName, StringComparison.Ordinal)
                    || path.EndsWith("." + fieldName, StringComparison.Ordinal);
            }

            private static bool ContainsField(string path, string fieldName)
            {
                return string.Equals(path, fieldName, StringComparison.Ordinal)
                    || path.StartsWith(fieldName + ".", StringComparison.Ordinal)
                    || path.Contains("." + fieldName + ".", StringComparison.Ordinal);
            }

            private static List<TypeTreeNode> GetNodes(List<TypeTreeNode> nodeList, int index)
            {
                return nodeList.GetRange(index, GetNodeEnd(nodeList, index) - index);
            }

            private static int GetNodeEnd(List<TypeTreeNode> nodeList, int index)
            {
                var level = nodeList[index].m_Level;
                var end = index + 1;
                while (end < nodeList.Count && nodeList[end].m_Level > level)
                {
                    end++;
                }
                return end;
            }
        }

        private sealed class SourceIndexWriteProgress
        {
            public SourceIndexWriteProgress(int batchIndex, int totalBatches, long totalObjects)
            {
                BatchIndex = batchIndex;
                TotalBatches = totalBatches;
                TotalObjects = totalObjects;
            }

            public int BatchIndex { get; }
            public int TotalBatches { get; }
            public long TotalObjects { get; }
            public string CurrentFile { get; set; } = string.Empty;
            public string CurrentSource { get; set; } = string.Empty;
            public string CurrentType { get; set; } = string.Empty;
            public long CurrentPathId { get; set; }
            public long CurrentObjectIndex { get; set; }
            public long SerializedFilesWritten { get; set; }
            public long ExternalsWritten { get; set; }
            public long ObjectsWritten { get; set; }
            public long RelationsWritten { get; set; }
            public long LightweightRendererObjects { get; set; }
            public long LightweightRendererRelations { get; set; }
            public long LightweightRendererFailures { get; set; }
        }

        private sealed class SourceIndexHeartbeat : IDisposable
        {
            private readonly CancellationTokenSource cancellation = new();
            private readonly Task task;
            private readonly Stopwatch stopwatch = Stopwatch.StartNew();
            private readonly int batchIndex;
            private readonly int totalBatches;
            private readonly int batchFileCount;
            private readonly long batchBytes;
            private readonly string largestFile;
            private readonly long largestFileBytes;
            private readonly AssetsManager manager;

            public SourceIndexHeartbeat(
                int batchIndex,
                int totalBatches,
                int batchFileCount,
                long batchBytes,
                string largestFile,
                long largestFileBytes,
                AssetsManager manager)
            {
                this.batchIndex = batchIndex;
                this.totalBatches = totalBatches;
                this.batchFileCount = batchFileCount;
                this.batchBytes = batchBytes;
                this.largestFile = largestFile;
                this.largestFileBytes = largestFileBytes;
                this.manager = manager;
                task = Task.Run(Run);
            }

            public void Dispose()
            {
                cancellation.Cancel();
                try
                {
                    task.Wait(TimeSpan.FromSeconds(2));
                }
                catch
                {
                    // Best-effort heartbeat shutdown.
                }
                cancellation.Dispose();
            }

            private async Task Run()
            {
                while (!cancellation.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(SourceIndexHeartbeatInterval, cancellation.Token).ConfigureAwait(false);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }

                    if (cancellation.IsCancellationRequested)
                    {
                        break;
                    }

                    var process = Process.GetCurrentProcess();
                    var elapsed = stopwatch.Elapsed;
                    var serializedFileCount = manager.assetsFileList.Count;
                    var objectInfoCount = manager.assetsFileList.Sum(x => x.m_Objects?.Count ?? 0);
                    var parsedObjectCount = manager.assetsFileList.Sum(x => x.Objects?.Count ?? 0);
                    var currentLoadFile = manager.CurrentLoadFile ?? string.Empty;
                    var currentLoadPhase = manager.CurrentLoadPhase ?? string.Empty;
                    var currentLoadInnerFile = manager.CurrentLoadInnerFile ?? string.Empty;
                    var currentLoadInnerFileIndex = manager.CurrentLoadInnerFileIndex;
                    var currentLoadInnerFileCount = manager.CurrentLoadInnerFileCount;
                    var currentFile = manager.CurrentReadAssetsFile ?? string.Empty;
                    var currentType = manager.CurrentReadType.ToString();
                    var currentObjectIndex = manager.CurrentReadObjectIndex;
                    var currentObjectCount = manager.CurrentReadObjectCount;
                    var currentPathId = manager.CurrentReadPathId;
                    ProfileLogger.Event("source_index_load_batch_heartbeat", new Dictionary<string, object>
                    {
                        ["batchIndex"] = batchIndex,
                        ["totalBatches"] = totalBatches,
                        ["batchFileCount"] = batchFileCount,
                        ["batchBytes"] = batchBytes,
                        ["largestFile"] = largestFile ?? string.Empty,
                        ["largestFileBytes"] = largestFileBytes,
                        ["elapsedMs"] = elapsed.TotalMilliseconds,
                        ["serializedFileCount"] = serializedFileCount,
                        ["objectInfoCount"] = objectInfoCount,
                        ["parsedObjectCount"] = parsedObjectCount,
                        ["currentLoadFile"] = currentLoadFile,
                        ["currentLoadPhase"] = currentLoadPhase,
                        ["currentLoadInnerFile"] = currentLoadInnerFile,
                        ["currentLoadInnerFileIndex"] = currentLoadInnerFileIndex,
                        ["currentLoadInnerFileCount"] = currentLoadInnerFileCount,
                        ["currentFile"] = currentFile,
                        ["currentType"] = currentType,
                        ["currentObjectIndex"] = currentObjectIndex,
                        ["currentObjectCount"] = currentObjectCount,
                        ["currentPathId"] = currentPathId,
                    });
                    ForceInfo(
                        $"[source-index {batchIndex}/{totalBatches}] Still loading after {FormatDuration(elapsed)}; " +
                        $"{batchFileCount} file(s), {FormatBytes(batchBytes)}; largest {largestFile} ({FormatBytes(largestFileBytes)}); " +
                        $"serialized {serializedFileCount}, objectInfos {objectInfoCount}, parsed {parsedObjectCount}; " +
                        $"loadPhase {currentLoadPhase}, loadFile {currentLoadFile}, inner {currentLoadInnerFileIndex}/{currentLoadInnerFileCount} {currentLoadInnerFile}; " +
                        $"current {currentFile} {currentObjectIndex}/{currentObjectCount} {currentType} pathId {currentPathId}; " +
                        $"workingSet {FormatBytes(process.WorkingSet64)}, private {FormatBytes(process.PrivateMemorySize64)}, managed {FormatBytes(GC.GetTotalMemory(false))}.");
                }
            }
        }

        private sealed class SourceIndexWriteHeartbeat : IDisposable
        {
            private readonly CancellationTokenSource cancellation = new();
            private readonly Task task;
            private readonly Stopwatch stopwatch = Stopwatch.StartNew();
            private readonly SourceIndexWriteProgress progress;

            public SourceIndexWriteHeartbeat(SourceIndexWriteProgress progress)
            {
                this.progress = progress;
                task = Task.Run(Run);
            }

            public void Dispose()
            {
                cancellation.Cancel();
                try
                {
                    task.Wait(TimeSpan.FromSeconds(2));
                }
                catch
                {
                    // Best-effort heartbeat shutdown.
                }
                cancellation.Dispose();
            }

            private async Task Run()
            {
                while (!cancellation.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(SourceIndexHeartbeatInterval, cancellation.Token).ConfigureAwait(false);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }

                    if (cancellation.IsCancellationRequested)
                    {
                        break;
                    }

                    var process = Process.GetCurrentProcess();
                    var elapsed = stopwatch.Elapsed;
                    ProfileLogger.Event("source_index_write_batch_heartbeat", new Dictionary<string, object>
                    {
                        ["batchIndex"] = progress.BatchIndex,
                        ["totalBatches"] = progress.TotalBatches,
                        ["elapsedMs"] = elapsed.TotalMilliseconds,
                        ["currentSource"] = progress.CurrentSource ?? string.Empty,
                        ["currentFile"] = progress.CurrentFile ?? string.Empty,
                        ["currentType"] = progress.CurrentType ?? string.Empty,
                        ["currentPathId"] = progress.CurrentPathId,
                        ["currentObjectIndex"] = progress.CurrentObjectIndex,
                        ["totalObjects"] = progress.TotalObjects,
                        ["serializedFilesWritten"] = progress.SerializedFilesWritten,
                        ["externalsWritten"] = progress.ExternalsWritten,
                        ["objectsWritten"] = progress.ObjectsWritten,
                        ["relationsWritten"] = progress.RelationsWritten,
                        ["lightweightRendererObjects"] = progress.LightweightRendererObjects,
                        ["lightweightRendererRelations"] = progress.LightweightRendererRelations,
                        ["lightweightRendererFailures"] = progress.LightweightRendererFailures,
                    });
                    ForceInfo(
                        $"[source-index {progress.BatchIndex}/{progress.TotalBatches}] Still writing after {FormatDuration(elapsed)}; " +
                        $"file {progress.CurrentFile}; object {progress.CurrentObjectIndex}/{progress.TotalObjects} {progress.CurrentType} pathId {progress.CurrentPathId}; " +
                        $"written serialized {progress.SerializedFilesWritten}, objects {progress.ObjectsWritten}, relations {progress.RelationsWritten}; " +
                        $"lightweightRenderer objects {progress.LightweightRendererObjects}, relations {progress.LightweightRendererRelations}, failures {progress.LightweightRendererFailures}; " +
                        $"workingSet {FormatBytes(process.WorkingSet64)}, private {FormatBytes(process.PrivateMemorySize64)}, managed {FormatBytes(GC.GetTotalMemory(false))}.");
                }
            }
        }

        private static void ForceInfo(string message)
        {
            if (!Logger.Flags.HasFlag(LoggerEvent.Info))
            {
                return;
            }

            if (Logger.FileLogging)
            {
                Logger.File?.Log(LoggerEvent.Info, message);
            }
            Logger.Default.Log(LoggerEvent.Info, message);
        }

        internal static bool IsLikelyUnityLoadableFile(string path, Game game = null)
        {
            if (IsLikelyMhyBlockFile(path, game))
            {
                return true;
            }

            var extension = Path.GetExtension(path);
            if (string.IsNullOrEmpty(extension))
            {
                return IsKnownUnityDataFile(path) || HasUnityBundleHeader(path);
            }

            switch (extension.ToLowerInvariant())
            {
                case ".assets":
                case ".bundle":
                case ".unity3d":
                case ".ab":
                case ".entities":
                case ".entityheader":
                    return true;
                case ".ress":
                case ".resss":
                case ".resource":
                case ".json":
                case ".config":
                case ".info":
                case ".manifest":
                case ".txt":
                case ".xml":
                case ".dll":
                case ".exe":
                case ".pdb":
                case ".mdb":
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".tga":
                case ".dds":
                case ".wav":
                case ".mp3":
                case ".ogg":
                case ".wem":
                case ".bank":
                case ".mp4":
                case ".webm":
                    return false;
                default:
                    return HasUnityBundleHeader(path);
            }
        }

        private static bool IsLikelyMhyBlockFile(string path, Game game)
        {
            if (game == null || !game.Type.IsMhyGroup())
            {
                return false;
            }

            var normalized = Path.GetFullPath(path).Replace('\\', '/');
            return normalized.Contains("/AssetBundles/blocks/", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasUnityBundleHeader(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                if (stream.Length < 7)
                {
                    return false;
                }

                var buffer = new byte[Math.Min(16, stream.Length)];
                var read = stream.Read(buffer, 0, buffer.Length);
                var header = Encoding.ASCII.GetString(buffer, 0, read);
                return header.StartsWith("UnityFS", StringComparison.Ordinal)
                    || header.StartsWith("UnityWeb", StringComparison.Ordinal)
                    || header.StartsWith("UnityRaw", StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsKnownUnityDataFile(string path)
        {
            var name = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            if (string.Equals(name, "globalgamemanagers", StringComparison.OrdinalIgnoreCase)
                && File.Exists(path + ".assets"))
            {
                return false;
            }

            if (name.StartsWith("level", StringComparison.OrdinalIgnoreCase) && name.Skip(5).All(char.IsDigit))
            {
                return true;
            }

            return string.Equals(name, "globalgamemanagers", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "unity_builtin_extra", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "unity default resources", StringComparison.OrdinalIgnoreCase);
        }

        private static long SafeFileLength(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return unit == 0
                ? $"{bytes} {units[unit]}"
                : $"{value:0.##} {units[unit]}";
        }

        private static string FormatDuration(TimeSpan duration)
        {
            return duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s"
                : duration.TotalMinutes >= 1
                    ? $"{(int)duration.TotalMinutes}m {duration.Seconds}s"
                    : $"{duration.Seconds}s";
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
