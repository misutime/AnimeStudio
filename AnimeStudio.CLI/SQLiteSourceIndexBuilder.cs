using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ACLLibs;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AnimeStudio.CLI
{
    public static class SQLiteSourceIndexBuilder
    {
        private const long LargeSourceIndexFileBytes = 256L * 1024L * 1024L;
        private const long EndfieldVfsInnerBatchBytes = 512L * 1024L * 1024L;
        private const int EndfieldVfsInnerBatchMinFileCount = 2048;
        private const int SourceIndexWriteCommitInterval = 10_000;
        private const int SourceIndexMaxLightweightArrayCount = 100_000;
        private const string SourceRelationFeatures = "assetBundle.preload,assetBundle.containerAsset,assetBundle.containerPreload,resourceManager.container,monoBehaviour.script,monoBehaviour.pptr";
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
            var materialHealth = report["materialRelationHealth"] as JObject;
            var materialStatus = materialHealth?["status"]?.ToString() ?? "unknown";
            Logger.Info($"Unity source material relation health status: {materialStatus}");
            var pathHashHealth = report["animationPathHashHealth"] as JObject;
            var pathHashStatus = pathHashHealth?["status"]?.ToString() ?? "unknown";
            Logger.Info($"Unity source animation pathHash health status: {pathHashStatus}");
            if (requireHealthy && !string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                var note = health?["note"]?.ToString() ?? "Source index animation relation health is not ok.";
                throw new InvalidOperationException(
                    "Unity source index animation relations are not fresh enough for production deterministic animation rebuild. " +
                    note);
            }

            return reportPath;
        }

        public static void EnsureQueryIndexes(string sourceIndexPath)
        {
            if (string.IsNullOrWhiteSpace(sourceIndexPath) || !File.Exists(sourceIndexPath))
            {
                throw new FileNotFoundException($"Unity source index not found: {sourceIndexPath}", sourceIndexPath);
            }

            SQLitePCL.Batteries_V2.Init();
            var dbPath = Path.GetFullPath(sourceIndexPath);
            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();
            using var profile = ProfileLogger.Measure("source_index_ensure_query_indexes", new Dictionary<string, object>
            {
                ["sourceIndex"] = dbPath,
            });

            EnsureIndex(connection, "idx_serialized_files_name", "serialized_files", "file_name");
            EnsureIndex(connection, "idx_source_objects_source_path", "source_objects", "source_path, path_id");
            EnsureIndex(connection, "idx_source_objects_file_path", "source_objects", "serialized_file, path_id");
            EnsureExpressionIndex(connection, "idx_source_objects_file_path_nocase", @"
CREATE INDEX idx_source_objects_file_path_nocase
ON source_objects(serialized_file COLLATE NOCASE, path_id);");
            EnsureIndex(connection, "idx_source_objects_type", "source_objects", "type");
            EnsureIndex(connection, "idx_source_objects_name", "source_objects", "name");
            EnsureIndex(connection, "idx_source_externals_file", "source_externals", "serialized_file, file_id");
            // 关系查询多数从已选模型/Avatar/Controller 的 PathID 出发。PathID 放第一位，避免 Endfield 这类超大表退化成按 relation 全扫。
            EnsureIndex(connection, "idx_source_relations_from", "source_relations", "from_path_id, relation, from_file");
            EnsureIndex(connection, "idx_source_relations_to", "source_relations", "to_path_id, relation, to_file");
            EnsureIndex(connection, "idx_source_relations_relation", "source_relations", "relation");
            // 候选报告经常按 AssetBundle container 路径反查 preload/material。
            // Naraka 这类大库没有这个表达式索引时，定向候选也会退化成全表扫描。
            EnsureExpressionIndex(connection, "idx_source_relations_container_relation", @"
CREATE INDEX idx_source_relations_container_relation
ON source_relations(json_extract(raw_json, '$.details.container'), relation);");
            EnsureIndex(connection, "idx_source_animation_bindings_clip", "source_animation_bindings", "animation_file, animation_path_id");
            RefreshNarakaInputProbeForExistingIndex(connection, dbPath);
            CheckpointSourceIndex(connection, dbPath);
            Logger.Info($"SQLite source query indexes are ready: {dbPath}");
        }

        private static void RefreshNarakaInputProbeForExistingIndex(SqliteConnection connection, string dbPath)
        {
            var gameName = ScalarString(connection, "SELECT value FROM metadata WHERE key='game' LIMIT 1;");
            var game = string.IsNullOrWhiteSpace(gameName) ? null : GameManager.GetGame(gameName);
            if (game == null || !game.Type.IsNaraka())
            {
                return;
            }

            var sourceRoot = ScalarString(connection, "SELECT value FROM metadata WHERE key='sourceRoot' LIMIT 1;");
            if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            {
                Logger.Warning($"Unable to refresh Naraka input probe because sourceRoot is missing or does not exist: {sourceRoot}");
                return;
            }

            string[] sourceFiles;
            using (ProfileLogger.Measure("source_index_refresh_naraka_input_probe", new Dictionary<string, object>
            {
                ["sourceRoot"] = sourceRoot,
                ["sourceIndex"] = dbPath,
            }))
            {
                var indexedFiles = ReadIndexedSourceFiles(connection, sourceRoot);
                sourceFiles = indexedFiles.Length > 0
                    ? indexedFiles
                    : Directory.GetFiles(sourceRoot, "*.*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            }

            var loadableFiles = sourceFiles
                .Where(x => IsLikelyUnityLoadableFile(x, game))
                .OrderBy(x => SafeFileLength(x))
                .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var narakaInputProbe = AnalyzeNarakaInputFiles(sourceFiles, loadableFiles, game);
            if (narakaInputProbe == null)
            {
                return;
            }

            using (var transaction = connection.BeginTransaction())
            {
                InsertMetadata(connection, transaction, "narakaInputProbe", narakaInputProbe.ToJson().ToString(Formatting.None));
                InsertMetadata(connection, transaction, "narakaBundleHeaderCount", narakaInputProbe.NarakaHeaderFileCount.ToString(CultureInfo.InvariantCulture));
                InsertMetadata(connection, transaction, "narakaPakFileCount", narakaInputProbe.PakFileCount.ToString(CultureInfo.InvariantCulture));
                InsertMetadata(connection, transaction, "narakaBundleHeaderOffsetCounts", JObject.FromObject(narakaInputProbe.HeaderSizeOffsetCounts).ToString(Formatting.None));
                transaction.Commit();
            }

            var outputRoot = Path.GetDirectoryName(dbPath) ?? Environment.CurrentDirectory;
            var counts = ReadSourceIndexCountsForSummary(connection, outputRoot, dbPath);
            counts["sourceFiles"] = sourceFiles.Length;
            counts["loadableSourceFiles"] = loadableFiles.Length;
            counts["skippedSidecarFiles"] = Math.Max(0, sourceFiles.Length - loadableFiles.Length);
            WriteSummary(connection, outputRoot, dbPath, sourceRoot, sourceFiles.Length, counts, narakaInputProbe);
            Logger.Info($"Refreshed Naraka input probe metadata for SQLite source index: {dbPath}");
        }

        public static string Build(
            string inputPath,
            string outputPath,
            Game game,
            string unityVersion,
            int batchFiles,
            string indexPath = null,
            Func<string, bool> endfieldVfsInnerFileFilter = null,
            int endfieldVfsInnerFileLimit = 0,
            bool endfieldVfsKeepSameLengthSupplemental = false,
            bool endfieldVfsIncludeAutoRootsWithExplicitFilter = false,
            string[] sourceFileFilters = null)
        {
            var hasExplicitSourceFileFilter = !sourceFileFilters.IsNullOrEmpty();
            using var totalProfile = ProfileLogger.Measure("source_index_total", new Dictionary<string, object>
            {
                ["inputPath"] = inputPath,
                ["outputPath"] = outputPath,
                ["batchFiles"] = batchFiles,
                ["explicitSourceFileFilter"] = hasExplicitSourceFileFilter,
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
            EndfieldVfsSourceSelection endfieldVfsSelection = EndfieldVfsSourceSelection.Empty;
            using (ProfileLogger.Measure("source_index_scan_files", new Dictionary<string, object>
            {
                ["inputPath"] = inputPath,
            }))
            {
                sourceRoot = Directory.Exists(inputPath)
                    ? Path.GetFullPath(inputPath)
                    : Path.GetDirectoryName(Path.GetFullPath(inputPath));
                if (hasExplicitSourceFileFilter)
                {
                    sourceFiles = ResolveExplicitSourceFiles(inputPath, sourceRoot, sourceFileFilters);
                    Logger.Info($"--source_files selected {sourceFiles.Length} explicit source file(s) for partial source-index diagnostics.");
                    if (sourceFiles.Length == 0)
                    {
                        throw new InvalidOperationException("--source_files did not resolve to any existing source file.");
                    }
                }
                else
                {
                    sourceFiles = Directory.Exists(inputPath)
                        ? Directory.GetFiles(inputPath, "*.*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray()
                        : new[] { Path.GetFullPath(inputPath) };
                    sourceFiles = ExpandEndfieldVfsLayerSourceFiles(inputPath, game, ref sourceRoot, sourceFiles);
                }
                loadableFiles = sourceFiles
                    .Where(x => IsLikelyUnityLoadableFile(x, game))
                    .OrderBy(x => SafeFileLength(x))
                    .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            var hasExplicitEndfieldVfsDiagnosticFilter = endfieldVfsInnerFileFilter != null || endfieldVfsInnerFileLimit > 0;
            var includeEndfieldAutoRootsWithExplicitFilter = hasExplicitEndfieldVfsDiagnosticFilter
                && endfieldVfsIncludeAutoRootsWithExplicitFilter
                && game.Type.IsArknightsEndfieldGroup();
            if (!hasExplicitEndfieldVfsDiagnosticFilter || includeEndfieldAutoRootsWithExplicitFilter)
            {
                endfieldVfsSelection = SelectEndfieldVfsSourceFiles(loadableFiles, game, endfieldVfsKeepSameLengthSupplemental);
                if (!hasExplicitEndfieldVfsDiagnosticFilter && endfieldVfsSelection.SkippedDuplicateCount > 0)
                {
                    loadableFiles = endfieldVfsSelection.SelectedFiles;
                    Logger.Info($"Endfield VFS source selection skipped {endfieldVfsSelection.SkippedDuplicateCount} duplicate .blc file(s); selected {loadableFiles.Length} Unity-loadable file(s).");
                }
                else if (includeEndfieldAutoRootsWithExplicitFilter)
                {
                    Logger.Info($"Endfield VFS source index keeps default auto root/context selection ({endfieldVfsSelection.SelectedFiles.Length} source file(s)) and adds explicit inner-file closure matches from all loadable VFS files.");
                }
            }
            var narakaInputProbe = AnalyzeNarakaInputFiles(sourceFiles, loadableFiles, game);

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
                ["lightweightMonoBehaviourObjects"] = 0,
                ["lightweightMonoBehaviourRelations"] = 0,
                ["lightweightMonoBehaviourFailures"] = 0,
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
                InsertMetadata(connection, transaction, "animationRelationFeatures", "animatorController.clip,animatorController.blendTreeParameter,animatorOverrideController.overrideSet,animatorOverrideController.originalClip,animatorOverrideController.overrideClip,animatorOverrideController.clipPair,animation.clip,monoBehaviour.script,monoBehaviour.pptr");
                InsertMetadata(connection, transaction, "sourceRelationFeatures", SourceRelationFeatures);
                InsertMetadata(connection, transaction, "rule", "索引要全，导出要精。This database indexes Unity source files, SerializedFiles, objects, PPtr relations, and animation bindings without exporting assets.");
                if (narakaInputProbe != null)
                {
                    InsertMetadata(connection, transaction, "narakaInputProbe", narakaInputProbe.ToJson().ToString(Formatting.None));
                    InsertMetadata(connection, transaction, "narakaBundleHeaderCount", narakaInputProbe.NarakaHeaderFileCount.ToString(CultureInfo.InvariantCulture));
                    InsertMetadata(connection, transaction, "narakaPakFileCount", narakaInputProbe.PakFileCount.ToString(CultureInfo.InvariantCulture));
                    InsertMetadata(connection, transaction, "narakaBundleHeaderOffsetCounts", JObject.FromObject(narakaInputProbe.HeaderSizeOffsetCounts).ToString(Formatting.None));
                    InsertMetadata(connection, transaction, "narakaInputRule", "本字段只记录永劫输入形态：当前主线读取 StreamingAssets 下的 Naraka/Unity bundle，.pak 若存在也只作为独立诊断线索，不进入默认 Library 解包路径。");
                }
                if (hasExplicitSourceFileFilter)
                {
                    InsertMetadata(connection, transaction, "sourceFilesPartialDiagnostic", "true");
                    InsertMetadata(connection, transaction, "sourceFilesPartialDiagnosticRule", "--source_files 只用于定向诊断和烟测。该源索引不代表完整 Unity 依赖闭包，不能作为生产全量 Library 的依赖底座。");
                    InsertMetadata(connection, transaction, "sourceFilesFilterCount", sourceFileFilters.Count(x => !string.IsNullOrWhiteSpace(x)).ToString(CultureInfo.InvariantCulture));
                }
                if (hasExplicitEndfieldVfsDiagnosticFilter)
                {
                    InsertMetadata(connection, transaction, "endfieldVfsPartialDiagnostic", "true");
                    InsertMetadata(connection, transaction, "endfieldVfsPartialDiagnosticRule", "This source index was built with an explicit Endfield .blc inner-file filter/limit for smoke tests. Do not use it as a production full Library dependency index.");
                    InsertMetadata(connection, transaction, "endfieldVfsInnerFileLimit", Math.Max(0, endfieldVfsInnerFileLimit).ToString(CultureInfo.InvariantCulture));
                    InsertMetadata(connection, transaction, "endfieldVfsKeepSameLengthSupplemental", endfieldVfsKeepSameLengthSupplemental ? "true" : "false");
                    InsertMetadata(connection, transaction, "endfieldVfsIncludeAutoRootsWithExplicitFilter", includeEndfieldAutoRootsWithExplicitFilter ? "true" : "false");
                    if (includeEndfieldAutoRootsWithExplicitFilter)
                    {
                        InsertMetadata(connection, transaction, "endfieldVfsAutoRootSourceFileCount", endfieldVfsSelection.SelectedFiles.Length.ToString(CultureInfo.InvariantCulture));
                        InsertMetadata(connection, transaction, "endfieldVfsDuplicateSkippedCount", endfieldVfsSelection.SkippedDuplicateCount.ToString(CultureInfo.InvariantCulture));
                        InsertMetadata(connection, transaction, "endfieldVfsSupplementalInnerFileCount", endfieldVfsSelection.SupplementalInnerFileCount.ToString(CultureInfo.InvariantCulture));
                    }
                }
                else if (loadableFiles.Any(path => IsLikelyEndfieldVfsFile(path, game)))
                {
                    InsertMetadata(connection, transaction, "endfieldVfsAutoInnerBatch", "true");
                    InsertMetadata(connection, transaction, "endfieldVfsAutoInnerBatchRule", "Endfield .blc files are indexed by loading their inner UnityFS bundle files in batches. This preserves full source coverage without loading a whole VFS group at once.");
                    InsertMetadata(connection, transaction, "endfieldVfsSourceSelectionRule", endfieldVfsKeepSameLengthSupplemental
                        ? "Strict diagnostic mode: when Persistent/VFS and StreamingAssets/VFS share the same Endfield VFS bucket, source indexing treats Persistent as primary but keeps all supplemental StreamingAssets inner UnityFS files unless the outer .blc bytes are identical. This can be very slow and is intended for unresolved material/CAB closure investigation."
                        : "Default production mode: when Persistent/VFS and StreamingAssets/VFS share the same Endfield VFS bucket, source indexing treats Persistent as primary and keeps supplemental StreamingAssets inner UnityFS files only when the inner name is absent from Persistent or the same inner name has a different byte length. Same-name same-length entries are counted but skipped by default; use --endfield_vfs_keep_same_length_supplemental only for slow diagnostic closure checks.");
                    InsertMetadata(connection, transaction, "endfieldVfsKeepSameLengthSupplemental", endfieldVfsKeepSameLengthSupplemental ? "true" : "false");
                    InsertMetadata(connection, transaction, "endfieldVfsDuplicateSkippedCount", endfieldVfsSelection.SkippedDuplicateCount.ToString(CultureInfo.InvariantCulture));
                    InsertMetadata(connection, transaction, "endfieldVfsDifferentNamedPairCount", endfieldVfsSelection.DifferentNamedPairCount.ToString(CultureInfo.InvariantCulture));
                    InsertMetadata(connection, transaction, "endfieldVfsSameNamedDifferentLengthInnerFileCount", endfieldVfsSelection.SameNamedDifferentLengthInnerFileCount.ToString(CultureInfo.InvariantCulture));
                    InsertMetadata(connection, transaction, "endfieldVfsSameNamedSameLengthInnerFileCount", endfieldVfsSelection.SameNamedSameLengthInnerFileCount.ToString(CultureInfo.InvariantCulture));
                    InsertMetadata(connection, transaction, "endfieldVfsSupplementalInnerFileCount", endfieldVfsSelection.SupplementalInnerFileCount.ToString(CultureInfo.InvariantCulture));
                }
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
            var endfieldAutoRootFiles = includeEndfieldAutoRootsWithExplicitFilter
                ? endfieldVfsSelection.SelectedFiles
                    .Select(Path.GetFullPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : null;
            var batches = BuildLoadBatches(
                loadableFiles,
                effectiveBatch,
                game,
                hasExplicitEndfieldVfsDiagnosticFilter,
                endfieldVfsSelection.InnerFileFilters,
                endfieldVfsInnerFileFilter,
                includeEndfieldAutoRootsWithExplicitFilter,
                endfieldAutoRootFiles);
            var endfieldInnerBatchCount = batches.Count(x => x.IsEndfieldVfsInnerBatch);
            if (endfieldInnerBatchCount > 0)
            {
                Logger.Info($"Endfield VFS source index will load {endfieldInnerBatchCount} inner UnityFS batch(es) instead of expanding each .blc in one pass.");
            }
            Logger.Info($"SQLite source index will load {loadableFiles.Length}/{sourceFiles.Length} source file(s) through {batches.Count} batch(es); skipped {sourceFiles.Length - loadableFiles.Length} sidecar/non-Unity file(s).");
            for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
            {
                var batch = batches[batchIndex];
                var largest = batch.Files
                    .Select(x => new { Path = x, Bytes = SafeFileLength(x) })
                    .OrderByDescending(x => x.Bytes)
                    .FirstOrDefault();
                var batchBytes = batch.Files.Sum(SafeFileLength);
                var endfieldBatchNote = batch.IsEndfieldVfsInnerBatch
                    ? $", Endfield inner files={batch.EndfieldVfsInnerFiles.Length}, inner bytes={FormatBytes(batch.EndfieldVfsInnerBytes)}"
                    : string.Empty;
                Logger.Info($"[source-index {batchIndex + 1}/{batches.Count}] Loading {batch.Files.Length} file(s), {FormatBytes(batchBytes)}{endfieldBatchNote}; largest {MakeRelativeOrName(sourceRoot, largest?.Path)} ({FormatBytes(largest?.Bytes ?? 0)}).");
                var batchEndfieldFilter = batch.IsEndfieldVfsInnerBatch
                    ? BuildExactEndfieldVfsInnerFileFilter(batch.EndfieldVfsInnerFiles)
                    : endfieldVfsInnerFileFilter;
                var manager = new AssetsManager
                {
                    Game = game,
                    SpecifyUnityVersion = unityVersion,
                    ResolveDependencies = false,
                    LoadSerializedFileExternals = false,
                    SkipProcess = true,
                    StoreUnparsedObjects = false,
                    ObjectParseFilter = ShouldParseObjectForSourceIndex,
                    EndfieldVfsInnerFileFilter = batchEndfieldFilter,
                    EndfieldVfsInnerFileLimit = batch.IsEndfieldVfsInnerBatch ? 0 : Math.Max(0, endfieldVfsInnerFileLimit),
                    EndfieldVfsInnerFileFilterIsDiagnostic = !batch.IsEndfieldVfsInnerBatch,
                    Silent = true,
                };

                try
                {
                    using (ProfileLogger.Measure("source_index_load_batch", new Dictionary<string, object>
                    {
                        ["batchIndex"] = batchIndex + 1,
                        ["totalBatches"] = batches.Count,
                        ["batchFileCount"] = batch.Files.Length,
                        ["batchBytes"] = batchBytes,
                        ["largestFile"] = MakeRelativeOrName(sourceRoot, largest?.Path),
                        ["largestFileBytes"] = largest?.Bytes ?? 0,
                        ["isLargeSingleton"] = batch.Files.Length == 1 && batchBytes >= LargeSourceIndexFileBytes,
                        ["endfieldVfsInnerFileCount"] = batch.EndfieldVfsInnerFiles?.Length ?? 0,
                        ["endfieldVfsInnerBytes"] = batch.EndfieldVfsInnerBytes,
                    }))
                    {
                        using var heartbeat = StartSourceIndexLoadHeartbeat(
                            batchIndex + 1,
                            batches.Count,
                            batch.Files.Length,
                            batchBytes,
                            MakeRelativeOrName(sourceRoot, largest?.Path),
                            largest?.Bytes ?? 0,
                            manager);
                        manager.LoadFiles(batch.Files);
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
                WriteSummary(connection, outputRoot, dbPath, sourceRoot, sourceFiles.Length, counts, narakaInputProbe);
            }

            using (ProfileLogger.Measure("source_index_checkpoint", new Dictionary<string, object>
            {
                ["dbPath"] = dbPath,
            }))
            {
                CheckpointSourceIndex(connection, dbPath);
            }

            Logger.Info($"SQLite Unity source index written: {dbPath}");
            return dbPath;
        }

        private static NarakaInputProbe AnalyzeNarakaInputFiles(string[] sourceFiles, string[] loadableFiles, Game game)
        {
            if (game == null || !game.Type.IsNaraka())
            {
                return null;
            }

            var probe = new NarakaInputProbe
            {
                SourceFileCount = sourceFiles?.Length ?? 0,
                LoadableFileCount = loadableFiles?.Length ?? 0,
            };
            var loadableSet = (loadableFiles ?? Array.Empty<string>())
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var path in sourceFiles ?? Array.Empty<string>())
            {
                if (string.Equals(Path.GetExtension(path), ".pak", StringComparison.OrdinalIgnoreCase))
                {
                    probe.PakFileCount++;
                    AddSample(probe.PakSamples, path);
                }

                if (!TryReadNarakaBundleHeader(path, out var header))
                {
                    continue;
                }

                if (header.IsUnityFs)
                {
                    probe.UnityFsHeaderFileCount++;
                    continue;
                }

                if (!header.IsNarakaHeader)
                {
                    continue;
                }

                probe.NarakaHeaderFileCount++;
                if (loadableSet.Contains(Path.GetFullPath(path)))
                {
                    probe.LoadableNarakaHeaderFileCount++;
                }

                var offsetKey = FormatHex(header.SizeOffset);
                probe.HeaderSizeOffsetCounts.TryGetValue(offsetKey, out var count);
                probe.HeaderSizeOffsetCounts[offsetKey] = count + 1;
                if (header.SizeOffset < 0)
                {
                    probe.TrailingDataHeaderFileCount++;
                }

                AddSample(probe.NarakaHeaderSamples, path, offsetKey);
            }

            if (probe.NarakaHeaderFileCount > 0 || probe.PakFileCount > 0)
            {
                Logger.Info(
                    "Naraka input probe: " +
                    $"narakaBundleHeaders={probe.NarakaHeaderFileCount}, " +
                    $"loadableNarakaBundleHeaders={probe.LoadableNarakaHeaderFileCount}, " +
                    $"unityFsHeaders={probe.UnityFsHeaderFileCount}, " +
                    $"pakFiles={probe.PakFileCount}, " +
                    $"offsets={string.Join(", ", probe.HeaderSizeOffsetCounts.OrderByDescending(x => x.Value).Select(x => $"{x.Key}:{x.Value}").Take(8))}");
            }

            return probe;
        }

        private static bool TryReadNarakaBundleHeader(string path, out NarakaBundleHeaderProbe header)
        {
            header = default;
            try
            {
                using var stream = File.OpenRead(path);
                if (stream.Length < 32)
                {
                    return false;
                }

                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
                var signature = reader.ReadBytes(8);
                var isUnityFs = Encoding.UTF8.GetString(signature, 0, 7) == "UnityFS";
                var isNarakaHeader = signature[0] == 0x15
                    && signature[1] == 0x1E
                    && signature[2] == 0x1C
                    && signature[3] == 0x0D
                    && signature[4] == 0x0D
                    && signature[5] == 0x23
                    && signature[6] == 0x21;
                if (!isUnityFs && !isNarakaHeader)
                {
                    return false;
                }

                ReadUInt32BigEndian(reader);
                SkipNullTerminatedString(reader, stream.Length);
                SkipNullTerminatedString(reader, stream.Length);
                if (stream.Position + 20 > stream.Length)
                {
                    return false;
                }

                var size = ReadInt64BigEndian(reader);
                header = new NarakaBundleHeaderProbe
                {
                    IsUnityFs = isUnityFs,
                    IsNarakaHeader = isNarakaHeader,
                    SizeOffset = size - stream.Length,
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static uint ReadUInt32BigEndian(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(4);
            if (bytes.Length != 4)
            {
                return 0;
            }

            return ((uint)bytes[0] << 24)
                | ((uint)bytes[1] << 16)
                | ((uint)bytes[2] << 8)
                | bytes[3];
        }

        private static long ReadInt64BigEndian(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(8);
            if (bytes.Length != 8)
            {
                return 0;
            }

            ulong value = ((ulong)bytes[0] << 56)
                | ((ulong)bytes[1] << 48)
                | ((ulong)bytes[2] << 40)
                | ((ulong)bytes[3] << 32)
                | ((ulong)bytes[4] << 24)
                | ((ulong)bytes[5] << 16)
                | ((ulong)bytes[6] << 8)
                | bytes[7];
            return unchecked((long)value);
        }

        private static void SkipNullTerminatedString(BinaryReader reader, long streamLength)
        {
            while (reader.BaseStream.Position < streamLength && reader.ReadByte() != 0)
            {
            }
        }

        private static string FormatHex(long value)
        {
            return value < 0
                ? "-0x" + (-value).ToString("X", CultureInfo.InvariantCulture)
                : "0x" + value.ToString("X", CultureInfo.InvariantCulture);
        }

        private static void AddSample(List<string> samples, string path, string note = null)
        {
            if (samples.Count >= 12)
            {
                return;
            }

            samples.Add(string.IsNullOrWhiteSpace(note) ? path : $"{path}|{note}");
        }

        private static string[] ExpandEndfieldVfsLayerSourceFiles(
            string inputPath,
            Game game,
            ref string sourceRoot,
            string[] sourceFiles)
        {
            if (game == null
                || !game.Type.IsArknightsEndfieldGroup()
                || !Directory.Exists(inputPath))
            {
                return sourceFiles;
            }

            var endfieldDataRoot = TryGetEndfieldDataRootFromVfsPath(inputPath);
            if (string.IsNullOrWhiteSpace(endfieldDataRoot))
            {
                return sourceFiles;
            }

            var vfsRoots = new[]
                {
                    Path.Combine(endfieldDataRoot, "Persistent", "VFS"),
                    Path.Combine(endfieldDataRoot, "StreamingAssets", "VFS"),
                }
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (vfsRoots.Length <= 1)
            {
                return sourceFiles;
            }

            var expandedFiles = vfsRoots
                .SelectMany(root => Directory.GetFiles(root, "*.*", SearchOption.AllDirectories))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (expandedFiles.Length <= sourceFiles.Length)
            {
                return sourceFiles;
            }

            sourceRoot = endfieldDataRoot;
            Logger.Info($"Endfield VFS source scan includes Persistent/Streaming layers under {endfieldDataRoot}; files {sourceFiles.Length} -> {expandedFiles.Length}.");
            return expandedFiles;
        }

        private static string[] ResolveExplicitSourceFiles(string inputPath, string sourceRoot, string[] sourceFileFilters)
        {
            if (sourceFileFilters.IsNullOrEmpty())
            {
                return Array.Empty<string>();
            }

            var inputIsFile = File.Exists(inputPath);
            var inputFile = inputIsFile ? Path.GetFullPath(inputPath) : null;
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var filter in sourceFileFilters)
            {
                if (string.IsNullOrWhiteSpace(filter))
                {
                    continue;
                }

                var normalized = NormalizeSourceFilePath(filter);
                var fullPath = Path.IsPathRooted(normalized)
                    ? Path.GetFullPath(normalized)
                    : Path.GetFullPath(Path.Combine(sourceRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
                if (inputIsFile && !string.Equals(fullPath, inputFile, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warning($"--source_files entry is outside the single-file input and will be ignored: {filter}");
                    continue;
                }

                if (!File.Exists(fullPath))
                {
                    Logger.Warning($"--source_files entry does not exist and will be ignored: {filter}");
                    continue;
                }

                if (seen.Add(fullPath))
                {
                    result.Add(fullPath);
                }
            }

            return result
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string NormalizeSourceFilePath(string path)
        {
            return path
                .Trim()
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/')
                .Replace('\\', '/');
        }

        private static string TryGetEndfieldDataRootFromVfsPath(string path)
        {
            var normalized = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            var markers = new[]
            {
                $"{Path.DirectorySeparatorChar}StreamingAssets{Path.DirectorySeparatorChar}VFS",
                $"{Path.DirectorySeparatorChar}Persistent{Path.DirectorySeparatorChar}VFS",
            };

            foreach (var marker in markers)
            {
                var index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index <= 0)
                {
                    continue;
                }

                var candidate = normalized[..index];
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            return string.Empty;
        }

        private static void CheckpointSourceIndex(SqliteConnection connection, string dbPath)
        {
            const long largeCheckpointDatabaseBytes = 64L * 1024L * 1024L * 1024L;
            const long largeCheckpointWalBytes = 8L * 1024L * 1024L * 1024L;

            var databaseBytes = SafeFileLength(dbPath);
            var walPath = dbPath + "-wal";
            var walBytes = SafeFileLength(walPath);
            if (databaseBytes >= largeCheckpointDatabaseBytes || walBytes >= largeCheckpointWalBytes)
            {
                Logger.Warning(
                    "SQLite source index is very large; using PASSIVE WAL checkpoint instead of TRUNCATE to avoid multi-hour finalization. " +
                    $"Keep the .db/.db-wal/.db-shm files together until a later maintenance checkpoint. db={FormatBytes(databaseBytes)}, wal={FormatBytes(walBytes)}");
                Execute(connection, "PRAGMA wal_checkpoint(PASSIVE);");
                return;
            }

            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
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
CREATE INDEX idx_source_objects_file_path_nocase ON source_objects(serialized_file COLLATE NOCASE, path_id);
CREATE INDEX idx_source_objects_type ON source_objects(type);
CREATE INDEX idx_source_objects_name ON source_objects(name);
CREATE INDEX idx_source_externals_file ON source_externals(serialized_file, file_id);
CREATE INDEX idx_source_relations_from ON source_relations(from_path_id, relation, from_file);
CREATE INDEX idx_source_relations_to ON source_relations(to_path_id, relation, to_file);
CREATE INDEX idx_source_relations_relation ON source_relations(relation);
CREATE INDEX idx_source_relations_container_relation ON source_relations(json_extract(raw_json, '$.details.container'), relation);
CREATE INDEX idx_source_animation_bindings_clip ON source_animation_bindings(animation_file, animation_path_id);");
        }

        private static void EnsureExpressionIndex(SqliteConnection connection, string indexName, string createSql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND name=$name;";
            command.Parameters.AddWithValue("$name", indexName);
            var existingSql = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(existingSql))
            {
                Logger.Info($"SQLite source expression index exists: {indexName}");
                return;
            }

            Logger.Info($"Creating SQLite source expression index {indexName}.");
            Execute(connection, createSql);
        }

        private static void EnsureIndex(SqliteConnection connection, string indexName, string tableName, string columnsSql)
        {
            var expectedColumns = columnsSql
                .Split(',')
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();
            var currentColumns = ReadIndexColumns(connection, indexName);
            if (currentColumns.SequenceEqual(expectedColumns, StringComparer.OrdinalIgnoreCase))
            {
                Logger.Info($"SQLite source index exists: {indexName}({string.Join(", ", currentColumns)})");
                return;
            }

            if (currentColumns.Length > 0)
            {
                Logger.Warning($"Rebuilding SQLite source index {indexName}: current=({string.Join(", ", currentColumns)}), expected=({columnsSql}).");
                Execute(connection, $"DROP INDEX IF EXISTS {indexName};");
            }
            else
            {
                Logger.Info($"Creating SQLite source index {indexName}({columnsSql}).");
            }

            Execute(connection, $"CREATE INDEX {indexName} ON {tableName}({columnsSql});");
        }

        private static string[] ReadIndexColumns(SqliteConnection connection, string indexName)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA index_info({indexName});";
            var rows = new List<(int Seq, string Name)>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var seq = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
                var name = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                rows.Add((seq, name));
            }

            return rows
                .OrderBy(x => x.Seq)
                .Select(x => x.Name)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();
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
                case MonoBehaviour monoBehaviour:
                    raw["monoBehaviour"] = new JObject
                    {
                        ["script"] = BuildPPtrJson(monoBehaviour.m_Script?.m_FileID ?? 0, monoBehaviour.m_Script?.m_PathID ?? 0),
                        ["scriptName"] = monoBehaviour.m_Script?.Name,
                        ["enabled"] = monoBehaviour.m_Enabled != 0,
                    };
                    break;
                case Renderer renderer:
                    raw["renderer"] = new JObject
                    {
                        ["enabled"] = renderer.m_Enabled,
                        ["materialSlotCount"] = renderer.m_Materials?.Count ?? 0,
                    };
                    break;
                case LODGroup lodGroup:
                    raw["lodGroup"] = new JObject
                    {
                        ["lodCount"] = lodGroup.m_LODs?.Count ?? 0,
                        ["rendererCount"] = lodGroup.m_LODs?.Sum(x => x.Renderers?.Count ?? 0) ?? 0,
                        ["lodRendererCounts"] = new JArray((lodGroup.m_LODs ?? new List<LODLevel>())
                            .Select(x => x.Renderers?.Count ?? 0)),
                    };
                    break;
                case MonoScript monoScript:
                    raw["monoScript"] = new JObject
                    {
                        ["className"] = monoScript.m_ClassName,
                        ["namespace"] = monoScript.m_Namespace,
                        ["assemblyName"] = monoScript.m_AssemblyName,
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
                        ["tos"] = BuildAvatarTosJson(avatar.m_TOS),
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

        private static JArray BuildAvatarTosJson(IReadOnlyDictionary<uint, string> tos)
        {
            if (tos == null || tos.Count == 0)
            {
                return new JArray();
            }

            // Avatar.m_TOS 是 Unity 自带的 hash -> path 证据。Naraka 很多 AnimationClip
            // binding 只有 pathHash，先把原始查表保进源索引，后续才能做确定性解析。
            return new JArray(tos
                .OrderBy(x => x.Value, StringComparer.Ordinal)
                .ThenBy(x => x.Key)
                .Select(x => new JObject
                {
                    ["pathHash"] = x.Key,
                    ["pathHashHex"] = $"0x{x.Key:X8}",
                    ["path"] = x.Value,
                }));
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
                case MonoBehaviour monoBehaviour:
                    WriteComponentGameObject(connection, transaction, monoBehaviour, counts);
                    // MonoBehaviour 字段不直接升级为动画关系；这里先记录脚本归属，
                    // 方便后续在确定性配置/TypeTree 里继续追模型和动画的真实引用链。
                    AddPPtrRelation(connection, transaction, counts, "monoBehaviour.script", monoBehaviour, monoBehaviour.m_Script.m_FileID, monoBehaviour.m_Script.m_PathID, "MonoScript", new
                    {
                        scriptName = monoBehaviour.m_Script.Name,
                    });
                    break;
                case LODGroup lodGroup:
                    WriteComponentGameObject(connection, transaction, lodGroup, counts);
                    var lodIndex = -1;
                    foreach (var lod in lodGroup.m_LODs ?? Enumerable.Empty<LODLevel>())
                    {
                        lodIndex++;
                        foreach (var ptr in lod.Renderers ?? Enumerable.Empty<PPtr<Renderer>>())
                        {
                            AddPPtrRelation(connection, transaction, counts, "lodGroup.renderer", lodGroup, ptr.m_FileID, ptr.m_PathID, "Renderer", new
                            {
                                lodIndex,
                            });
                        }
                    }
                    break;
                case AssetBundle assetBundle:
                    WriteAssetBundleRelations(connection, transaction, assetBundle, counts);
                    break;
                case ResourceManager resourceManager:
                    WriteResourceManagerRelations(connection, transaction, resourceManager, counts);
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

        private static void WriteAssetBundleRelations(SqliteConnection connection, SqliteTransaction transaction, AssetBundle assetBundle, Dictionary<string, long> counts)
        {
            var preloadTable = assetBundle.m_PreloadTable ?? new List<PPtr<AnimeStudio.Object>>();
            for (var preloadIndex = 0; preloadIndex < preloadTable.Count; preloadIndex++)
            {
                var ptr = preloadTable[preloadIndex];
                AddPPtrRelation(connection, transaction, counts, "assetBundle.preload", assetBundle, ptr.m_FileID, ptr.m_PathID, "Object", new
                {
                    preloadIndex,
                });
            }

            foreach (var pair in assetBundle.m_Container ?? Enumerable.Empty<KeyValuePair<string, AssetInfo>>())
            {
                var container = pair.Key ?? string.Empty;
                var info = pair.Value;
                if (info == null)
                {
                    continue;
                }

                // container/preload 是 Unity 原始资源归属证据，只能辅助定位依赖，
                // 不能单独升级成模型-动画默认绑定关系。
                AddPPtrRelation(connection, transaction, counts, "assetBundle.containerAsset", assetBundle, info.asset.m_FileID, info.asset.m_PathID, "Object", new
                {
                    container,
                    preloadIndex = info.preloadIndex,
                    preloadSize = info.preloadSize,
                });

                var start = Math.Max(0, info.preloadIndex);
                var end = Math.Min(preloadTable.Count, start + Math.Max(0, info.preloadSize));
                for (var tableIndex = start; tableIndex < end; tableIndex++)
                {
                    var ptr = preloadTable[tableIndex];
                    AddPPtrRelation(connection, transaction, counts, "assetBundle.containerPreload", assetBundle, ptr.m_FileID, ptr.m_PathID, "Object", new
                    {
                        container,
                        preloadIndex = info.preloadIndex,
                        preloadSize = info.preloadSize,
                        preloadTableIndex = tableIndex,
                        preloadOffset = tableIndex - start,
                    });
                }
            }
        }

        private static void WriteResourceManagerRelations(SqliteConnection connection, SqliteTransaction transaction, ResourceManager resourceManager, Dictionary<string, long> counts)
        {
            foreach (var pair in resourceManager.m_Container ?? Enumerable.Empty<KeyValuePair<string, PPtr<AnimeStudio.Object>>>())
            {
                var ptr = pair.Value;
                AddPPtrRelation(connection, transaction, counts, "resourceManager.container", resourceManager, ptr.m_FileID, ptr.m_PathID, "Object", new
                {
                    container = pair.Key ?? string.Empty,
                });
            }
        }

        private static void WriteAnimatorControllerClipRelations(SqliteConnection connection, SqliteTransaction transaction, AnimatorController controller, Dictionary<string, long> counts)
        {
            var clips = controller.m_AnimationClips ?? new List<PPtr<AnimationClip>>();
            var usedClipSlots = new HashSet<int>();
            var stateMachines = controller.m_Controller?.m_StateMachineArray ?? new List<StateMachineConstant>();
            var layerMap = BuildAnimatorControllerLayerMap(controller);
            var defaultBaseLayerClip = BuildDefaultBaseLayerClip(controller, stateMachines, layerMap);

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
                            var layerContexts = layers.Count > 0
                                ? layers
                                : new List<AnimatorControllerLayerContext> { new AnimatorControllerLayerContext(-1, 0, 0, 0, 1f, false, false) };
                            var node = nodes[nodeIndex];
                            WriteAnimatorControllerBlendTreeParameterRelation(
                                connection,
                                transaction,
                                counts,
                                controller,
                                layerContexts,
                                machineIndex,
                                stateIndex,
                                state,
                                treeIndex,
                                nodeIndex,
                                node);
                            if (!TryGetAnimatorControllerClipSlot(controller, node.m_ClipID, out var clipSlot, out var clipPtr))
                            {
                                continue;
                            }

                            usedClipSlots.Add(clipSlot);

                            // AnimatorController 里的 node.m_ClipID 在原神这类资源里是 m_AnimationClips 的槽位。
                            // 记录 state/blend/node 上下文，避免后续把叶子 clip 误当完整动作。
                            var baseLayerClip = ResolveAnimatorControllerBaseLayerClip(
                                treeIndex,
                                nodeIndex,
                                machineIndex,
                                layerContexts,
                                baseLayerClipsByNode,
                                defaultBaseLayerClip);
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
                                    layerBodyMask = layer.BodyMask,
                                    layerSkeletonMask = layer.SkeletonMask,
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
                                stateIKOnFeet = state.m_IKOnFeet,
                                stateLoop = state.m_Loop,
                                stateMirror = state.m_Mirror,
                                stateTransitions = DescribeTransitions(state.m_TransitionConstantArray, controller.m_TOS),
                                stateTransitionConditionCount = CountTransitionConditions(state.m_TransitionConstantArray),
                                stateParameters = DescribeStateParameters(state.m_StateParameterConstantArray, controller.m_TOS),
                                controllerValueDefaults = DescribeControllerValueDefaultSummary(controller),
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
                                nodeBlend1dChildThresholds = node.m_Blend1dData?.m_ChildThresholdArray,
                                nodeDirectChildBlendEvents = DescribeTosArray(node.m_BlendDirectData?.m_ChildBlendEventIDArray, controller.m_TOS),
                                nodeDirectChildPoseTimeEvents = DescribeTosArray(node.m_BlendDirectData?.m_ChildPoseTimeEventIDArray, controller.m_TOS),
                                nodeDirectNormalizedBlendValues = node.m_BlendDirectData?.m_NormalizedBlendValues,
                                nodeDirectUsePoseTimeValues = node.m_BlendDirectData?.m_UsePoseTimeValues,
                                nodeSequenceChildBlendEvents = DescribeTosArray(node.m_BlendSequenceData?.m_ChildBlendEventIDArray, controller.m_TOS),
                                nodeSequenceChildPoseTimeEvents = DescribeTosArray(node.m_BlendSequenceData?.m_ChildPoseTimeEventIDArray, controller.m_TOS),
                                nodeSequenceNormalizedBlendValues = node.m_BlendSequenceData?.m_NormalizedBlendValues,
                                nodeSequenceUsePoseTimeValues = node.m_BlendSequenceData?.m_UsePoseTimeValues,
                                nodeSequenceChildSpeed = node.m_BlendSequenceData?.m_ChildSpeed,
                                nodeSequenceChildLodThreshold = node.m_BlendSequenceData?.m_ChildLodThreshold,
                                nodeSequenceChildAbilityThreshold = node.m_BlendSequenceData?.m_ChildAbilityThreshold,
                                nodeSequenceChildCullingMode = node.m_BlendSequenceData?.m_ChildCullingMode,
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

        private static void WriteAnimatorControllerBlendTreeParameterRelation(
            SqliteConnection connection,
            SqliteTransaction transaction,
            Dictionary<string, long> counts,
            AnimatorController controller,
            IReadOnlyList<AnimatorControllerLayerContext> layerContexts,
            int machineIndex,
            int stateIndex,
            StateConstant state,
            int treeIndex,
            int nodeIndex,
            BlendTreeNodeConstant node)
        {
            var parameterRefs = DescribeAnimatorControllerNodeParameterRefs(node, controller.m_TOS);
            if (parameterRefs.Count == 0)
            {
                return;
            }

            var clipSlot = -1;
            PPtr<AnimationClip> clipPtr = null;
            var hasClip = TryGetAnimatorControllerClipSlot(controller, node.m_ClipID, out clipSlot, out clipPtr);
            if (InsertPPtrRelation(
                connection,
                transaction,
                "animatorController.blendTreeParameter",
                "explicit_controller_structure",
                controller.assetsFile,
                MakeRelativeOrName(null, controller.assetsFile?.originalPath ?? controller.assetsFile?.fullName),
                controller.assetsFile?.fileName ?? string.Empty,
                controller.type.ToString(),
                controller.Name ?? string.Empty,
                controller.m_PathID,
                0,
                0,
                "AnimatorControllerParameter",
                new
                {
                    count = parameterRefs.Count,
                    source = "AnimatorController.m_Controller.stateMachine.blendTree.node",
                    rule = "diagnostic_only: parameter references come from serialized AnimatorController BlendTree/direct/sequence data; they do not set runtime values or prove animation correctness by themselves.",
                    stateMachineIndex = machineIndex,
                    stateIndex,
                    stateName = TryGetTos(controller, state.m_NameID),
                    stateNameId = state.m_NameID,
                    stateFullPath = TryGetTos(controller, state.m_FullPathID),
                    stateFullPathId = state.m_FullPathID,
                    stateParameters = DescribeStateParameters(state.m_StateParameterConstantArray, controller.m_TOS),
                    layers = DescribeAnimatorControllerLayerContexts(layerContexts),
                    blendTreeIndex = treeIndex,
                    nodeIndex,
                    nodeBlendType = node.m_BlendType,
                    nodeChildIndices = node.m_ChildIndices,
                    nodeChildThresholds = node.m_ChildThresholdArray,
                    nodeBlend1dChildThresholds = node.m_Blend1dData?.m_ChildThresholdArray,
                    nodeClipId = node.m_ClipID,
                    nodeClipIndex = node.m_ClipIndex,
                    nodeHasClip = hasClip,
                    nodeClip = hasClip
                        ? DescribePPtr(controller.assetsFile, clipPtr.m_FileID, clipPtr.m_PathID, "AnimationClip")
                        : null,
                    controllerClipIndex = hasClip ? clipSlot : -1,
                    parameters = parameterRefs,
                    directNormalizedBlendValues = node.m_BlendDirectData?.m_NormalizedBlendValues,
                    directUsePoseTimeValues = node.m_BlendDirectData?.m_UsePoseTimeValues,
                    sequenceNormalizedBlendValues = node.m_BlendSequenceData?.m_NormalizedBlendValues,
                    sequenceUsePoseTimeValues = node.m_BlendSequenceData?.m_UsePoseTimeValues,
                    sequenceChildSpeed = node.m_BlendSequenceData?.m_ChildSpeed,
                    sequenceChildLodThreshold = node.m_BlendSequenceData?.m_ChildLodThreshold,
                    sequenceChildAbilityThreshold = node.m_BlendSequenceData?.m_ChildAbilityThreshold,
                    sequenceChildCullingMode = node.m_BlendSequenceData?.m_ChildCullingMode,
                }))
            {
                counts["sourceRelations"]++;
            }
        }

        private static JArray DescribeAnimatorControllerNodeParameterRefs(BlendTreeNodeConstant node, IReadOnlyDictionary<uint, string> tos)
        {
            var result = new JArray();

            void Add(string source, uint id, int index = -1)
            {
                if (id == 0 || id == uint.MaxValue)
                {
                    return;
                }

                var item = new JObject
                {
                    ["source"] = source,
                    ["id"] = id,
                    ["idHex"] = $"0x{id:X8}",
                    ["name"] = TryGetTos(tos, id),
                };
                if (index >= 0)
                {
                    item["index"] = index;
                }

                result.Add(item);
            }

            Add("blendEvent", node.m_BlendEventID);
            Add("blendEventY", node.m_BlendEventYID);
            AddArray("directChildBlendEvent", node.m_BlendDirectData?.m_ChildBlendEventIDArray);
            AddArray("directChildPoseTimeEvent", node.m_BlendDirectData?.m_ChildPoseTimeEventIDArray);
            AddArray("sequenceChildBlendEvent", node.m_BlendSequenceData?.m_ChildBlendEventIDArray);
            AddArray("sequenceChildPoseTimeEvent", node.m_BlendSequenceData?.m_ChildPoseTimeEventIDArray);
            return result;

            void AddArray(string source, IEnumerable<uint> ids)
            {
                var index = 0;
                foreach (var id in ids ?? Array.Empty<uint>())
                {
                    Add(source, id, index++);
                }
            }
        }

        private static JArray DescribeAnimatorControllerLayerContexts(IReadOnlyList<AnimatorControllerLayerContext> layers)
        {
            return new JArray((layers ?? Array.Empty<AnimatorControllerLayerContext>()).Select(layer => new JObject
            {
                ["layerIndex"] = layer.LayerIndex,
                ["layerStateMachineIndex"] = layer.StateMachineIndex,
                ["layerStateMachineMotionSetIndex"] = layer.StateMachineMotionSetIndex,
                ["layerBinding"] = layer.Binding,
                ["layerBlendingMode"] = layer.BlendingMode,
                ["layerDefaultWeight"] = layer.DefaultWeight,
                ["layerIKPass"] = layer.IKPass,
                ["layerSyncedLayerAffectsTiming"] = layer.SyncedLayerAffectsTiming,
                ["layerBodyMask"] = layer.BodyMask,
                ["layerSkeletonMask"] = layer.SkeletonMask,
            }));
        }

        private static JObject ResolveAnimatorControllerBaseLayerClip(
            int treeIndex,
            int nodeIndex,
            int machineIndex,
            IReadOnlyList<AnimatorControllerLayerContext> layerContexts,
            IReadOnlyDictionary<int, JObject> baseLayerClipsByNode,
            AnimatorControllerDefaultBaseLayerClip defaultBaseLayerClip)
        {
            if (treeIndex > 0 && baseLayerClipsByNode.TryGetValue(nodeIndex, out var sameStateBaseClip))
            {
                return sameStateBaseClip;
            }

            if (defaultBaseLayerClip == null || machineIndex == defaultBaseLayerClip.MachineIndex)
            {
                return null;
            }

            if (layerContexts == null || !layerContexts.Any(x => x.LayerIndex > 0))
            {
                return null;
            }

            return (JObject)defaultBaseLayerClip.Clip.DeepClone();
        }

        private static AnimatorControllerDefaultBaseLayerClip BuildDefaultBaseLayerClip(
            AnimatorController controller,
            IReadOnlyList<StateMachineConstant> stateMachines,
            IReadOnlyDictionary<int, List<AnimatorControllerLayerContext>> layerMap)
        {
            if (controller == null || stateMachines == null || stateMachines.Count == 0)
            {
                return null;
            }

            var baseLayer = layerMap?
                .SelectMany(x => x.Value ?? new List<AnimatorControllerLayerContext>())
                .OrderBy(x => x.LayerIndex)
                .FirstOrDefault(x => x.LayerIndex == 0)
                ?? layerMap?
                    .SelectMany(x => x.Value ?? new List<AnimatorControllerLayerContext>())
                    .OrderBy(x => x.LayerIndex)
                    .FirstOrDefault();
            var baseMachineIndex = baseLayer?.StateMachineIndex ?? 0;
            if (baseMachineIndex < 0 || baseMachineIndex >= stateMachines.Count)
            {
                return null;
            }

            var machine = stateMachines[baseMachineIndex];
            var states = machine.m_StateConstantArray ?? new List<StateConstant>();
            if (machine.m_DefaultState > int.MaxValue || machine.m_DefaultState >= states.Count)
            {
                return null;
            }

            var stateIndex = unchecked((int)machine.m_DefaultState);
            var state = states[stateIndex];
            if (!TryBuildUniqueStateClip(controller, baseMachineIndex, stateIndex, state, out var clip))
            {
                return null;
            }

            return new AnimatorControllerDefaultBaseLayerClip(baseMachineIndex, clip);
        }

        private static bool TryBuildUniqueStateClip(
            AnimatorController controller,
            int stateMachineIndex,
            int stateIndex,
            StateConstant state,
            out JObject clip)
        {
            clip = null;
            var trees = state?.m_BlendTreeConstantArray ?? new List<BlendTreeConstant>();
            var clips = new Dictionary<long, JObject>();
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

                    var pathId = clipPtr.m_PathID;
                    if (!clips.ContainsKey(pathId))
                    {
                        clips[pathId] = new JObject
                        {
                            ["controllerClipIndex"] = clipSlot,
                            ["nodeIndex"] = nodeIndex,
                            ["treeIndex"] = treeIndex,
                            ["nodeClipId"] = node.m_ClipID,
                            ["clip"] = DescribePPtr(controller.assetsFile, clipPtr.m_FileID, clipPtr.m_PathID, "AnimationClip"),
                            ["stateMachineIndex"] = stateMachineIndex,
                            ["stateIndex"] = stateIndex,
                            ["stateName"] = TryGetTos(controller, state.m_NameID),
                            ["stateNameId"] = state.m_NameID,
                            ["statePath"] = TryGetTos(controller, state.m_PathID),
                            ["statePathId"] = state.m_PathID,
                            ["stateFullPath"] = TryGetTos(controller, state.m_FullPathID),
                            ["stateFullPathId"] = state.m_FullPathID,
                            ["stateSpeed"] = state.m_Speed,
                            ["stateCycleOffset"] = state.m_CycleOffset,
                            ["stateIKOnFeet"] = state.m_IKOnFeet,
                            ["stateLoop"] = state.m_Loop,
                            ["stateMirror"] = state.m_Mirror,
                            ["stateTransitions"] = DescribeTransitions(state.m_TransitionConstantArray, controller.m_TOS),
                            ["stateTransitionConditionCount"] = CountTransitionConditions(state.m_TransitionConstantArray),
                            ["source"] = "AnimatorController.baseLayer.defaultState",
                            ["rule"] = "same AnimatorController base layer default state resolved to exactly one deterministic body clip",
                        };
                    }
                }
            }

            if (clips.Count != 1)
            {
                return false;
            }

            clip = clips.Values.First();
            return true;
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

        private static JObject DescribeControllerValueDefaultSummary(AnimatorController controller)
        {
            var values = controller?.m_Controller?.m_Values?.m_ValueArray ?? new List<ValueConstant>();
            var defaults = controller?.m_Controller?.m_DefaultValues;
            return new JObject
            {
                ["valueCount"] = values.Count,
                ["defaultBoolCount"] = defaults?.m_BoolValues?.Length ?? 0,
                ["defaultIntCount"] = defaults?.m_IntValues?.Length ?? 0,
                ["defaultFloatCount"] = defaults?.m_FloatValues?.Length ?? 0,
                ["defaultPositionCount"] = defaults?.m_PositionValues?.Length ?? 0,
                ["defaultQuaternionCount"] = defaults?.m_QuaternionValues?.Length ?? 0,
                ["defaultScaleCount"] = defaults?.m_ScaleValues?.Length ?? 0,
                ["rule"] = "diagnostic_only: raw AnimatorController m_Values/m_DefaultValues counts only; full value rows are emitted by unity_file_inspect.json to avoid source_index relation bloat.",
            };
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
                    layer.m_LayerBlendingMode,
                    DescribeHumanPoseMask(layer.m_BodyMask),
                    DescribeSkeletonMask(layer.m_SkeletonMask, controller.m_TOS)));
            }

            return result;
        }

        private static JObject DescribeHumanPoseMask(HumanPoseMask mask)
        {
            if (mask == null)
            {
                return null;
            }

            return new JObject
            {
                ["word0"] = mask.word0,
                ["word1"] = mask.word1,
                ["word2"] = mask.word2,
                ["isEmpty"] = mask.word0 == 0 && mask.word1 == 0 && mask.word2 == 0,
                ["rawHex"] = new JArray(
                    $"0x{mask.word0:X8}",
                    $"0x{mask.word1:X8}",
                    $"0x{mask.word2:X8}"),
            };
        }

        private static JObject DescribeSkeletonMask(SkeletonMask mask, IReadOnlyDictionary<uint, string> tos)
        {
            if (mask?.m_Data == null)
            {
                return null;
            }

            return new JObject
            {
                ["count"] = mask.m_Data.Count,
                ["nonZeroCount"] = mask.m_Data.Count(x => Math.Abs(x.m_Weight) > 0.0001f),
                ["entries"] = new JArray(mask.m_Data.Select(x => new JObject
                {
                    ["pathHash"] = x.m_PathHash,
                    ["pathHashHex"] = $"0x{x.m_PathHash:X8}",
                    ["path"] = tos != null && tos.TryGetValue(x.m_PathHash, out var path) ? path : null,
                    ["weight"] = x.m_Weight,
                })),
            };
        }

        private static JArray DescribeStateParameters(StateParameterConstant[] parameters, IReadOnlyDictionary<uint, string> tos)
        {
            return new JArray((parameters ?? Array.Empty<StateParameterConstant>()).Select(x => new JObject
            {
                ["nameId"] = x.m_NameID,
                ["nameIdHex"] = $"0x{unchecked((uint)x.m_NameID):X8}",
                ["name"] = tos != null && tos.TryGetValue(unchecked((uint)x.m_NameID), out var name) ? name : null,
                ["value"] = x.m_Value,
            }));
        }

        private static JArray DescribeTosArray(IEnumerable<uint> ids, IReadOnlyDictionary<uint, string> tos)
        {
            return new JArray((ids ?? Array.Empty<uint>()).Select((id, index) => new JObject
            {
                ["index"] = index,
                ["id"] = id,
                ["idHex"] = $"0x{id:X8}",
                ["name"] = TryGetTos(tos, id),
            }));
        }

        private static JArray DescribeTransitions(IEnumerable<TransitionConstant> transitions, IReadOnlyDictionary<uint, string> tos)
        {
            return new JArray((transitions ?? Array.Empty<TransitionConstant>()).Select((transition, transitionIndex) => new JObject
            {
                ["transitionIndex"] = transitionIndex,
                ["destinationState"] = transition.m_DestinationState,
                ["fullPathId"] = transition.m_FullPathID,
                ["fullPath"] = TryGetTos(tos, transition.m_FullPathID),
                ["id"] = transition.m_ID,
                ["userId"] = transition.m_UserID,
                ["duration"] = transition.m_TransitionDuration,
                ["offset"] = transition.m_TransitionOffset,
                ["exitTime"] = transition.m_ExitTime,
                ["hasExitTime"] = transition.m_HasExitTime,
                ["hasFixedDuration"] = transition.m_HasFixedDuration,
                ["interruptionSource"] = transition.m_InterruptionSource,
                ["orderedInterruption"] = transition.m_OrderedInterruption,
                ["canTransitionToSelf"] = transition.m_CanTransitionToSelf,
                ["conditionCount"] = transition.m_ConditionConstantArray?.Count ?? 0,
                ["conditions"] = DescribeConditions(transition.m_ConditionConstantArray, tos),
            }));
        }

        private static JArray DescribeConditions(IEnumerable<ConditionConstant> conditions, IReadOnlyDictionary<uint, string> tos)
        {
            return new JArray((conditions ?? Array.Empty<ConditionConstant>()).Select((condition, conditionIndex) => new JObject
            {
                ["conditionIndex"] = conditionIndex,
                ["mode"] = condition.m_ConditionMode,
                ["modeName"] = DescribeAnimatorConditionMode(condition.m_ConditionMode),
                ["eventId"] = condition.m_EventID,
                ["eventIdHex"] = $"0x{condition.m_EventID:X8}",
                ["eventName"] = TryGetTos(tos, condition.m_EventID),
                ["threshold"] = condition.m_EventThreshold,
                ["exitTime"] = condition.m_ExitTime,
            }));
        }

        private static int CountTransitionConditions(IEnumerable<TransitionConstant> transitions)
        {
            return (transitions ?? Array.Empty<TransitionConstant>())
                .Sum(x => x.m_ConditionConstantArray?.Count ?? 0);
        }

        private static string DescribeAnimatorConditionMode(uint mode)
        {
            return mode switch
            {
                1 => "If",
                2 => "IfNot",
                3 => "Greater",
                4 => "Less",
                6 => "Equals",
                7 => "NotEqual",
                _ => "Unknown",
            };
        }

        private static string TryGetTos(IReadOnlyDictionary<uint, string> tos, uint id)
        {
            return tos != null && tos.TryGetValue(id, out var value)
                ? value
                : null;
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
            public AnimatorControllerLayerContext(int layerIndex, int stateMachineIndex, int stateMachineMotionSetIndex, uint binding, float defaultWeight, bool ikPass, bool syncedLayerAffectsTiming, int blendingMode = 0, JObject bodyMask = null, JObject skeletonMask = null)
            {
                LayerIndex = layerIndex;
                StateMachineIndex = stateMachineIndex;
                StateMachineMotionSetIndex = stateMachineMotionSetIndex;
                Binding = binding;
                DefaultWeight = defaultWeight;
                IKPass = ikPass;
                SyncedLayerAffectsTiming = syncedLayerAffectsTiming;
                BlendingMode = blendingMode;
                BodyMask = bodyMask;
                SkeletonMask = skeletonMask;
            }

            public int LayerIndex { get; }
            public int StateMachineIndex { get; }
            public int StateMachineMotionSetIndex { get; }
            public uint Binding { get; }
            public float DefaultWeight { get; }
            public bool IKPass { get; }
            public bool SyncedLayerAffectsTiming { get; }
            public int BlendingMode { get; }
            public JObject BodyMask { get; }
            public JObject SkeletonMask { get; }
        }

        private sealed record AnimatorControllerDefaultBaseLayerClip(int MachineIndex, JObject Clip);

        private static int TryWriteLightweightRendererRelations(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string sourceRoot,
            SerializedFile assetsFile,
            ObjectInfo objectInfo,
            Dictionary<string, long> counts,
            SourceIndexWriteProgress progress)
        {
            var isMonoBehaviour = objectInfo.classID == (int)ClassIDType.MonoBehaviour;
            if (!IsLightweightRendererRelationType(objectInfo.classID) && !isMonoBehaviour)
            {
                return 0;
            }

            if (isMonoBehaviour)
            {
                counts["lightweightMonoBehaviourObjects"]++;
            }
            else
            {
                counts["lightweightRendererObjects"]++;
                progress.LightweightRendererObjects++;
            }

            if (objectInfo.serializedType?.m_Type?.m_Nodes == null || objectInfo.serializedType.m_Type.m_Nodes.Count == 0)
            {
                return !isMonoBehaviour && IsFallbackParsedRendererType(objectInfo.classID)
                    ? TryWriteFallbackParsedRendererRelations(connection, transaction, sourceRoot, assetsFile, objectInfo, counts, progress, "missing_typetree")
                    : 0;
            }

            if (!IsObjectRangeReadable(assetsFile, objectInfo))
            {
                if (isMonoBehaviour)
                {
                    counts["lightweightMonoBehaviourFailures"]++;
                }
                else
                {
                    counts["lightweightRendererFailures"]++;
                    progress.LightweightRendererFailures++;
                }
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
                        progress.RelationsWritten = counts["sourceRelations"];
                        if (isMonoBehaviour)
                        {
                            counts["lightweightMonoBehaviourRelations"]++;
                        }
                        else
                        {
                            counts["lightweightRendererRelations"]++;
                            progress.LightweightRendererRelations++;
                        }
                        relationCount++;
                    }
                }

                return relationCount;
            }
            catch (Exception e) when (e is EndOfStreamException || e is IOException || e is InvalidDataException || e is OverflowException || e is ArgumentOutOfRangeException)
            {
                return !isMonoBehaviour && IsFallbackParsedRendererType(objectInfo.classID)
                    ? TryWriteFallbackParsedRendererRelations(connection, transaction, sourceRoot, assetsFile, objectInfo, counts, progress, e.GetType().Name)
                    : RecordLightweightRelationFailure(sourceRoot, assetsFile, objectInfo, counts, progress, e.GetType().Name, e, isMonoBehaviour);
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
                return RecordLightweightRelationFailure(sourceRoot, assetsFile, objectInfo, counts, progress, reason, e, false);
            }
        }

        private static int RecordLightweightRelationFailure(
            string sourceRoot,
            SerializedFile assetsFile,
            ObjectInfo objectInfo,
            Dictionary<string, long> counts,
            SourceIndexWriteProgress progress,
            string reason,
            Exception e,
            bool isMonoBehaviour)
        {
            if (isMonoBehaviour)
            {
                counts["lightweightMonoBehaviourFailures"]++;
            }
            else
            {
                counts["lightweightRendererFailures"]++;
                progress.LightweightRendererFailures++;
            }
            ProfileLogger.Event("source_index_lightweight_renderer_failed", new Dictionary<string, object>
            {
                ["source"] = MakeRelativeOrName(sourceRoot, assetsFile.originalPath ?? assetsFile.fullName),
                ["file"] = assetsFile.fileName ?? string.Empty,
                ["pathId"] = objectInfo.m_PathID,
                ["type"] = Enum.IsDefined(typeof(ClassIDType), objectInfo.classID) ? ((ClassIDType)objectInfo.classID).ToString() : objectInfo.classID.ToString(CultureInfo.InvariantCulture),
                ["readerKind"] = isMonoBehaviour ? "MonoBehaviourPPtr" : "RendererMaterialVfxPPtr",
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
                endfieldAclInfo = DescribeEndfieldAclInfo(clip),
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

        private static JObject DescribeEndfieldAclInfo(AnimationClip clip)
        {
            var buffer = clip?.m_AclCompressedBuffer;
            if (buffer == null)
            {
                return null;
            }

            return new JObject
            {
                ["version"] = buffer.Version,
                ["outputTrackCount"] = buffer.OutputTrackCount,
                ["rootTrackCount"] = buffer.RootTrackCount,
                ["rootPosIndex"] = buffer.RootPosIndex,
                ["rootRotIndex"] = buffer.RootRotIndex,
                ["rootScaleIndex"] = buffer.RootScaleIndex,
                ["floatCurveCount"] = buffer.FloatCurveCount,
                ["transformBuffer"] = DescribeAclBufferInfo(buffer.TransformBufferData),
                ["rootMotionBuffer"] = DescribeAclBufferInfo(buffer.RootMotionBufferData),
                ["floatBuffer"] = DescribeAclBufferInfo(buffer.FloatBufferData),
                ["defaultIndexCount"] = buffer.m_DefaultIndexs?.Length ?? 0,
                ["constantIndexCount"] = buffer.m_ConstantIndexs?.Length ?? 0,
                ["constantValueCount"] = buffer.m_ConstantValues?.Length ?? 0,
            };
        }

        private static JObject DescribeAclBufferInfo(byte[] data)
        {
            var result = new JObject
            {
                ["byteCount"] = data?.Length ?? 0,
            };
            if (data == null || data.Length == 0)
            {
                return result;
            }

            try
            {
                if (EndfieldACL.TryGetInfo(data, out var info))
                {
                    result["status"] = "ok";
                    result["result"] = info.Result;
                    result["size"] = info.Size;
                    result["version"] = info.Version;
                    result["trackType"] = info.TrackType;
                    result["numTracks"] = info.NumTracks;
                    result["numSamples"] = info.NumSamples;
                    result["sampleRate"] = info.SampleRate;
                    result["duration"] = info.Duration;
                    result["outputFloatCount"] = info.OutputFloatCount;
                }
                else
                {
                    result["status"] = "unreadable";
                }
            }
            catch (Exception ex)
            {
                result["status"] = "error";
                result["error"] = ex.GetType().Name + ": " + ex.Message;
            }

            return result;
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

        private static void WriteSummary(
            SqliteConnection connection,
            string outputRoot,
            string dbPath,
            string sourceRoot,
            int fileCount,
            Dictionary<string, long> counts,
            NarakaInputProbe narakaInputProbe)
        {
            var summaryPath = GetSourceIndexSummaryPath(outputRoot, dbPath);
            var summary = new JObject
            {
                ["schemaVersion"] = 1,
                ["database"] = dbPath,
                ["sourceRoot"] = sourceRoot,
                ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["animationRelationFeatures"] = "animatorController.clip,animatorController.blendTreeParameter,animatorOverrideController.overrideSet,animatorOverrideController.originalClip,animatorOverrideController.overrideClip,animatorOverrideController.clipPair,animation.clip,monoBehaviour.script,monoBehaviour.pptr",
                ["sourceRelationFeatures"] = SourceRelationFeatures,
                ["animationRelationHealth"] = BuildAnimationRelationHealth(connection),
                ["sourceRelationHealth"] = BuildSourceRelationHealth(connection),
                ["modelDependencyHealth"] = BuildModelDependencyHealth(connection),
                ["materialRelationHealth"] = BuildMaterialRelationHealth(connection),
                ["avatarOracleHealth"] = BuildAvatarOracleHealth(connection),
                ["rule"] = "索引要全，导出要精。Source SQLite v1 stores Unity source files, SerializedFiles, Objects, externals, PPtr relations, and animation bindings.",
                ["inputFileCount"] = fileCount,
                ["counts"] = JObject.FromObject(counts),
            };
            if (narakaInputProbe != null)
            {
                summary["narakaInputProbe"] = narakaInputProbe.ToJson();
            }
            File.WriteAllText(summaryPath, summary.ToString(Formatting.Indented));
            var readmePath = UsesDefaultSourceIndexPath(outputRoot, dbPath)
                ? Path.Combine(outputRoot, "UNITY_SOURCE_INDEX_README.md")
                : Path.Combine(Path.GetDirectoryName(summaryPath) ?? outputRoot, Path.GetFileNameWithoutExtension(dbPath) + ".README.md");
            File.WriteAllText(readmePath,
                "# Unity Source SQLite Index\n\n" +
                "这是完整 Unity 源目录索引，不是导出结果索引。它用于一次性记录源文件、SerializedFile、Object、external CAB/PPtr、Animator/Animation/Renderer/Skin/AnimationClip binding 等关系。\n\n" +
                "核心原则：索引要全，导出要精。源索引中出现的对象不代表默认素材库会导出，也不代表视觉验收通过。\n\n" +
                "Naraka 输入探针：`narakaInputProbe` 只记录永劫无间源目录中 `.pak`、`UnityFS`、Naraka 替代头和 header size offset 分布，用来区分“当前 StreamingAssets bundle 主线”和“另行诊断的包解密线索”。它不代表默认流程会解包 `.pak`。\n\n" +
                "Renderer/Skin 关系采用 SourceIndex 专用轻量读取：优先按 Unity TypeTree 捕获 `component.gameObject`、`renderer.material`、`skinnedMeshRenderer.mesh/rootBone/bones`；TypeTree 不可用时仅对当前 Renderer 小对象做受控 fallback 解析。失败会记录为 partial/failure，不会阻塞整个索引。\n\n" +
                "主要表：`source_files`、`serialized_files`、`source_objects`、`source_externals`、`source_relations`、`source_animation_bindings`。\n\n" +
                "容器关系：当前源索引会记录 `assetBundle.preload`、`assetBundle.containerAsset`、`assetBundle.containerPreload` 和 `resourceManager.container`。它们用于追来源、依赖闭包、静态 Mesh 主资源识别和缺件排查；container/path 仍是 fallback/诊断信号，不能单独升级成默认模型-动画绑定。\n\n" +
                "Avatar 诊断：`avatarOracleHealth` 会统计 AvatarConstant oracle 与 `Avatar.m_TOS` 覆盖情况。`m_TOS` 是 Unity 自带的 hash -> path 查表，可用于追 Naraka 这类 hash-only AnimationClip binding，但不能单独升级成默认模型-动画关系。\n\n" +
                "性能日志：启用 `--profile_log` 后，重点比较 `source_index_load_batch`、`source_index_write_batch`、`source_index_load_batch_heartbeat`、`source_index_write_batch_heartbeat`、`source_index_create_sql_indexes` 和 `source_index_total`。长时间大文件处理时，heartbeat 会持续记录当前文件、对象序号、关系计数和内存。\n\n" +
                "统计项：`lightweightRendererObjects` 是索引阶段尝试补关系的 Renderer 数；`lightweightRendererRelations` 是成功补到的 mesh/material/bone/gameObject 关系数；`lightweightMonoBehaviourRelations` 是通过 TypeTree 捕获到的脚本 PPtr 关系数。MonoBehaviour PPtr 只作为确定性线索保留，不能直接升级成模型动画绑定。\n\n" +
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
                    ["sourceRelationFeatures"] = ScalarString(connection, "SELECT value FROM metadata WHERE key='sourceRelationFeatures' LIMIT 1;"),
                    ["createdUtc"] = ScalarString(connection, "SELECT value FROM metadata WHERE key='createdUtc' LIMIT 1;"),
                },
                ["animationRelationHealth"] = BuildAnimationRelationHealth(connection),
                ["sourceRelationHealth"] = BuildSourceRelationHealth(connection),
                ["modelDependencyHealth"] = BuildModelDependencyHealth(connection),
                ["materialRelationHealth"] = BuildMaterialRelationHealth(connection),
                ["avatarOracleHealth"] = BuildAvatarOracleHealth(connection),
                ["animationPathHashHealth"] = BuildAnimationPathHashHealth(connection),
            };
        }

        private static JObject BuildAnimationRelationHealth(SqliteConnection connection)
        {
            var overrideControllerObjects = ScalarLong(connection, "SELECT COUNT(*) FROM source_objects WHERE type='AnimatorOverrideController';");
            var monoBehaviourObjects = ScalarLong(connection, "SELECT COUNT(*) FROM source_objects WHERE type='MonoBehaviour';");
            var controllerClipResolvedTargets = SourceRelationResolvedTargetCount(connection, "animatorController.clip", "AnimationClip");
            var legacyClipResolvedTargets = SourceRelationResolvedTargetCount(connection, "animation.clip", "AnimationClip");
            var overrideOriginalClipResolvedTargets = SourceRelationResolvedTargetCount(connection, "animatorOverrideController.originalClip", "AnimationClip");
            var overrideClipResolvedTargets = SourceRelationResolvedTargetCount(connection, "animatorOverrideController.overrideClip", "AnimationClip");
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
                ["monoBehaviour.script"] = SourceRelationCount(connection, "monoBehaviour.script"),
                ["monoBehaviour.pptr"] = SourceRelationCount(connection, "monoBehaviour.pptr"),
            };
            var clipPairs = (long)relationCounts["animatorOverrideController.clipPair"];
            var overrideSets = (long)relationCounts["animatorOverrideController.overrideSet"];
            var controllerClips = (long)relationCounts["animatorController.clip"];
            var legacyClips = (long)relationCounts["animation.clip"];
            var overrideOriginalClips = (long)relationCounts["animatorOverrideController.originalClip"];
            var overrideClips = (long)relationCounts["animatorOverrideController.overrideClip"];
            var monoBehaviourPptr = (long)relationCounts["monoBehaviour.pptr"];
            var controllerClipMissingTargets = Math.Max(0, controllerClips - controllerClipResolvedTargets);
            var legacyClipMissingTargets = Math.Max(0, legacyClips - legacyClipResolvedTargets);
            var overrideOriginalClipMissingTargets = Math.Max(0, overrideOriginalClips - overrideOriginalClipResolvedTargets);
            var overrideClipMissingTargets = Math.Max(0, overrideClips - overrideClipResolvedTargets);
            var nonEmptyOverrideSets = SourceRelationTargetCountPositive(connection, "animatorOverrideController.overrideSet");
            var staleOverridePairs = overrideControllerObjects > 0
                && (overrideSets == 0 || (nonEmptyOverrideSets > 0 && clipPairs == 0));
            var missingControllerClipTargets = controllerClipMissingTargets > 0;
            var missingAnimationClipTargets = legacyClipMissingTargets > 0
                || overrideOriginalClipMissingTargets > 0
                || overrideClipMissingTargets > 0;
            var missingMonoBehaviourPptr = monoBehaviourObjects > 0 && monoBehaviourPptr == 0;
            var issues = new JArray();
            if (staleOverridePairs)
            {
                issues.Add("staleAnimatorOverrideControllerPairs");
            }
            if (missingControllerClipTargets)
            {
                issues.Add("missingAnimatorControllerClipTargets");
            }
            if (legacyClipMissingTargets > 0)
            {
                issues.Add("missingLegacyAnimationClipTargets");
            }
            if (overrideOriginalClipMissingTargets > 0 || overrideClipMissingTargets > 0)
            {
                issues.Add("missingAnimatorOverrideControllerClipTargets");
            }
            if (missingMonoBehaviourPptr)
            {
                issues.Add("missingMonoBehaviourPptrIndex");
            }
            return new JObject
            {
                ["status"] = staleOverridePairs || missingControllerClipTargets || missingAnimationClipTargets || missingMonoBehaviourPptr ? "warning" : "ok",
                ["objectCounts"] = new JObject
                {
                    ["AnimatorOverrideController"] = overrideControllerObjects,
                    ["MonoBehaviour"] = monoBehaviourObjects,
                },
                ["relationCounts"] = relationCounts,
                ["resolvedTargetCounts"] = new JObject
                {
                    ["animatorController.clip"] = controllerClipResolvedTargets,
                    ["animation.clip"] = legacyClipResolvedTargets,
                    ["animatorOverrideController.originalClip"] = overrideOriginalClipResolvedTargets,
                    ["animatorOverrideController.overrideClip"] = overrideClipResolvedTargets,
                },
                ["missingTargetCounts"] = new JObject
                {
                    ["animatorController.clip"] = controllerClipMissingTargets,
                    ["animation.clip"] = legacyClipMissingTargets,
                    ["animatorOverrideController.originalClip"] = overrideOriginalClipMissingTargets,
                    ["animatorOverrideController.overrideClip"] = overrideClipMissingTargets,
                },
                ["nonEmptyOverrideSetCount"] = nonEmptyOverrideSets,
                ["staleOverridePairIndex"] = staleOverridePairs,
                ["missingControllerClipTargets"] = missingControllerClipTargets,
                ["missingAnimationClipTargets"] = missingAnimationClipTargets,
                ["missingMonoBehaviourPptrIndex"] = missingMonoBehaviourPptr,
                ["issues"] = issues,
                ["missingAnimatorControllerClipTargetSamples"] = BuildMissingRelationTargetSamples(connection, "animatorController.clip", "AnimationClip", 16),
                ["missingAnimationClipTargetSamples"] = BuildMissingRelationTargetSamples(connection, "animation.clip", "AnimationClip", 16),
                ["missingAnimatorOverrideOriginalClipTargetSamples"] = BuildMissingRelationTargetSamples(connection, "animatorOverrideController.originalClip", "AnimationClip", 16),
                ["missingAnimatorOverrideClipTargetSamples"] = BuildMissingRelationTargetSamples(connection, "animatorOverrideController.overrideClip", "AnimationClip", 16),
                ["note"] = staleOverridePairs
                    ? "源目录存在 AnimatorOverrideController，但缺少当前工具写入的 animatorOverrideController.overrideSet/clipPair 精确标记。旧索引即使有分离的 originalClip/overrideClip，也不能可靠表达 original -> override 或空替换表；请用当前工具重建源索引。"
                    : missingControllerClipTargets || missingAnimationClipTargets
                        ? "源索引包含 AnimatorController、legacy Animation 或 AnimatorOverrideController 指向 AnimationClip 的显式关系，但部分目标没有解析到真实 AnimationClip。请用完整源目录重建索引或继续补 VFS/CAB 依赖闭包，不能把缺 clip 目标的样本推进到动画 smoke。"
                    : missingMonoBehaviourPptr
                        ? "源索引包含 MonoBehaviour 对象，但没有 monoBehaviour.pptr 关系。Endfield 这类游戏可能把正式角色模型、配置和控制器关系藏在脚本字段里；请用当前工具重建源索引后再做生产级动画关系判断。"
                    : "源索引包含当前工具可检查的动画显式关系。Library 候选仍需结合 glTF 预览验证。"
            };
        }

        private static JObject BuildSourceRelationHealth(SqliteConnection connection)
        {
            var assetBundleObjects = ScalarLong(connection, "SELECT COUNT(*) FROM source_objects WHERE type='AssetBundle';");
            var resourceManagerObjects = ScalarLong(connection, "SELECT COUNT(*) FROM source_objects WHERE type='ResourceManager';");
            // MonoBehaviour 字段引用只作为来源诊断，不能直接升级成默认模型/动画绑定。
            var monoBehaviourObjects = ScalarLong(connection, "SELECT COUNT(*) FROM source_objects WHERE type='MonoBehaviour';");
            var monoBehaviourPPtrRelations = SourceRelationCount(connection, "monoBehaviour.pptr");
            var monoBehaviourScriptRelations = SourceRelationCount(connection, "monoBehaviour.script");
            var monoBehaviourPPtrDistinctEdges = DistinctRelationEdgeCount(connection, "monoBehaviour.pptr");
            var monoBehaviourPPtrResolvedTargets = SourceRelationDistinctResolvedAnyTargetCount(connection, "monoBehaviour.pptr");
            var monoBehaviourPPtrMissingTargets = Math.Max(0, monoBehaviourPPtrDistinctEdges - monoBehaviourPPtrResolvedTargets);
            var relationCounts = new JObject
            {
                ["assetBundle.preload"] = SourceRelationCount(connection, "assetBundle.preload"),
                ["assetBundle.containerAsset"] = SourceRelationCount(connection, "assetBundle.containerAsset"),
                ["assetBundle.containerPreload"] = SourceRelationCount(connection, "assetBundle.containerPreload"),
                ["resourceManager.container"] = SourceRelationCount(connection, "resourceManager.container"),
                ["monoBehaviour.script"] = monoBehaviourScriptRelations,
                ["monoBehaviour.pptr"] = monoBehaviourPPtrRelations,
            };
            var assetBundleRelationCount = (long)relationCounts["assetBundle.preload"]
                + (long)relationCounts["assetBundle.containerAsset"]
                + (long)relationCounts["assetBundle.containerPreload"];
            var resourceManagerRelationCount = (long)relationCounts["resourceManager.container"];
            var missingAssetBundleContainerIndex = assetBundleObjects > 0 && assetBundleRelationCount == 0;
            var missingResourceManagerContainerIndex = resourceManagerObjects > 0 && resourceManagerRelationCount == 0;
            var missingMonoBehaviourPPtrIndex = monoBehaviourObjects > 0 && monoBehaviourPPtrRelations == 0;
            var missingMonoBehaviourPPtrTargets = monoBehaviourPPtrDistinctEdges > 0 && monoBehaviourPPtrMissingTargets > 0;
            var issues = new JArray();
            if (missingAssetBundleContainerIndex)
            {
                issues.Add("missingAssetBundleContainerIndex");
            }
            if (missingResourceManagerContainerIndex)
            {
                issues.Add("missingResourceManagerContainerIndex");
            }
            if (missingMonoBehaviourPPtrIndex)
            {
                issues.Add("missingMonoBehaviourPPtrIndex");
            }
            if (missingMonoBehaviourPPtrTargets)
            {
                issues.Add("missingMonoBehaviourPPtrTargets");
            }

            return new JObject
            {
                ["status"] = missingAssetBundleContainerIndex || missingResourceManagerContainerIndex || missingMonoBehaviourPPtrIndex || missingMonoBehaviourPPtrTargets ? "warning" : "ok",
                ["features"] = SourceRelationFeatures,
                ["objectCounts"] = new JObject
                {
                    ["AssetBundle"] = assetBundleObjects,
                    ["ResourceManager"] = resourceManagerObjects,
                    ["MonoBehaviour"] = monoBehaviourObjects,
                },
                ["relationCounts"] = relationCounts,
                ["monoBehaviourPPtrHealth"] = new JObject
                {
                    ["scriptRelations"] = monoBehaviourScriptRelations,
                    ["pptrRelations"] = monoBehaviourPPtrRelations,
                    ["distinctEdges"] = monoBehaviourPPtrDistinctEdges,
                    ["resolvedTargets"] = monoBehaviourPPtrResolvedTargets,
                    ["missingTargets"] = monoBehaviourPPtrMissingTargets,
                    ["resolvedTargetCoverage"] = Ratio(monoBehaviourPPtrResolvedTargets, monoBehaviourPPtrDistinctEdges),
                    ["targetTypes"] = BuildMonoBehaviourPPtrTargetTypeBreakdown(connection),
                    ["topScripts"] = BuildMonoBehaviourPPtrScriptBreakdown(connection, 24),
                    ["missingTargetSamples"] = BuildMissingRelationTargetSamples(connection, "monoBehaviour.pptr", null, 16),
                },
                ["missingAssetBundleContainerIndex"] = missingAssetBundleContainerIndex,
                ["missingResourceManagerContainerIndex"] = missingResourceManagerContainerIndex,
                ["missingMonoBehaviourPPtrIndex"] = missingMonoBehaviourPPtrIndex,
                ["missingMonoBehaviourPPtrTargets"] = missingMonoBehaviourPPtrTargets,
                ["issues"] = issues,
                ["note"] = missingAssetBundleContainerIndex || missingResourceManagerContainerIndex
                    ? "源索引包含 AssetBundle/ResourceManager 对象，但缺少当前工具写入的 container/preload 关系。需要用当前工具重建源索引，才能可靠追踪容器来源、依赖闭包、静态 Mesh 主资源和缺件问题。"
                    : missingMonoBehaviourPPtrIndex
                        ? "源索引包含 MonoBehaviour 对象，但缺少脚本字段 PPtr 关系。Naraka 这类游戏可能把脸部、换装、材质或控制器关系藏在脚本配置里；请用当前工具重建源索引后再判断资源闭包。"
                        : missingMonoBehaviourPPtrTargets
                            ? "源索引记录了 MonoBehaviour 字段 PPtr，但部分目标未解析到 source_objects。请检查完整源目录/CAB 依赖闭包，避免把缺脸、缺附件或缺配置的模型误判为可用。"
                            : "源索引包含当前工具可检查的 Unity 容器/preload 和 MonoBehaviour PPtr 关系。它们是来源和依赖证据，不能单独作为模型-动画默认绑定。"
            };
        }

        private static JObject BuildMaterialRelationHealth(SqliteConnection connection)
        {
            var materialObjects = ScalarLong(connection, "SELECT COUNT(*) FROM source_objects WHERE type='Material';");
            var texture2DOrArrayObjects = ScalarLong(connection, "SELECT COUNT(*) FROM source_objects WHERE type IN ('Texture2D', 'Texture2DArray');");
            var unityTextureObjects = ScalarLong(connection, $"SELECT COUNT(*) FROM source_objects WHERE {UnityTextureTypeSql("source_objects")};");
            var meshRendererObjects = ScalarLong(connection, "SELECT COUNT(*) FROM source_objects WHERE type='MeshRenderer';");
            var skinnedRendererObjects = ScalarLong(connection, "SELECT COUNT(*) FROM source_objects WHERE type='SkinnedMeshRenderer';");
            var rendererMaterialRelations = SourceRelationCount(connection, "renderer.material");
            var rendererMaterialDistinctEdges = ScalarLong(connection, @"
SELECT COUNT(DISTINCT r.from_file || ':' || r.from_path_id || '>' || r.to_file || ':' || r.to_path_id)
FROM source_relations r
WHERE r.relation = 'renderer.material';");
            var rendererMaterialUnityBuiltinTargets = SourceRelationUnityBuiltinTargetCount(connection, "renderer.material");
            var rendererMaterialResolvedTargets = ScalarLong(connection, @"
SELECT COUNT(DISTINCT r.from_file || ':' || r.from_path_id || '>' || r.to_file || ':' || r.to_path_id)
FROM source_relations r
JOIN source_objects o
  ON o.serialized_file = r.to_file COLLATE NOCASE
 AND o.path_id = r.to_path_id
 AND o.type = 'Material'
WHERE r.relation = 'renderer.material';");
            var materialTextureRelations = SourceRelationCount(connection, "material.texture");
            var materialTextureDistinctEdges = ScalarLong(connection, @"
SELECT COUNT(DISTINCT r.from_file || ':' || r.from_path_id || '>' || r.to_file || ':' || r.to_path_id)
FROM source_relations r
WHERE r.relation = 'material.texture';");
            var materialTextureUnityBuiltinTargets = SourceRelationUnityBuiltinTargetCount(connection, "material.texture");
            var materialTextureResolvedTargets = ScalarLong(connection, @"
SELECT COUNT(DISTINCT r.from_file || ':' || r.from_path_id || '>' || r.to_file || ':' || r.to_path_id)
FROM source_relations r
JOIN source_objects o
  ON o.serialized_file = r.to_file COLLATE NOCASE
 AND o.path_id = r.to_path_id
 AND " + UnityTextureTypeSql("o") + @"
WHERE r.relation = 'material.texture';");
            var materialsWithTexture = ScalarLong(connection, @"
SELECT COUNT(DISTINCT r.from_file || ':' || r.from_path_id)
FROM source_relations r
JOIN source_objects material
  ON material.serialized_file = r.from_file COLLATE NOCASE
 AND material.path_id = r.from_path_id
 AND material.type = 'Material'
WHERE r.relation = 'material.texture';");
            var rendererMaterialMissingTargets = Math.Max(0, rendererMaterialDistinctEdges - rendererMaterialResolvedTargets - rendererMaterialUnityBuiltinTargets);
            var materialTextureMissingTargets = Math.Max(0, materialTextureDistinctEdges - materialTextureResolvedTargets - materialTextureUnityBuiltinTargets);
            var missingRendererMaterialTargets = rendererMaterialDistinctEdges > 0 && rendererMaterialMissingTargets > 0;
            var missingMaterialTextureTargets = materialTextureDistinctEdges > 0 && materialTextureMissingTargets > 0;
            var missingRendererMaterialTargetSamples = BuildMissingRelationTargetSamples(connection, "renderer.material", "Material", 16);
            var missingMaterialTextureTargetSamples = BuildMissingRelationTargetSamples(connection, "material.texture", "UnityTexture", 16);
            var issues = new JArray();
            if (missingRendererMaterialTargets)
            {
                issues.Add("missingRendererMaterialTargets");
            }
            if (missingMaterialTextureTargets)
            {
                issues.Add("missingMaterialTextureTargets");
            }

            return new JObject
            {
                ["status"] = missingRendererMaterialTargets || missingMaterialTextureTargets ? "warning" : "ok",
                ["objectCounts"] = new JObject
                {
                    ["Material"] = materialObjects,
                    ["Texture2DOrArray"] = texture2DOrArrayObjects,
                    ["UnityTexture"] = unityTextureObjects,
                    ["MeshRenderer"] = meshRendererObjects,
                    ["SkinnedMeshRenderer"] = skinnedRendererObjects,
                },
                ["relationCounts"] = new JObject
                {
                    ["renderer.material"] = rendererMaterialRelations,
                    ["material.texture"] = materialTextureRelations,
                },
                ["distinctRelationCounts"] = new JObject
                {
                    ["renderer.material"] = rendererMaterialDistinctEdges,
                    ["material.texture"] = materialTextureDistinctEdges,
                },
                ["resolvedTargetCounts"] = new JObject
                {
                    ["renderer.material"] = rendererMaterialResolvedTargets,
                    ["material.texture"] = materialTextureResolvedTargets,
                },
                ["unityBuiltinTargetCounts"] = new JObject
                {
                    ["renderer.material"] = rendererMaterialUnityBuiltinTargets,
                    ["material.texture"] = materialTextureUnityBuiltinTargets,
                },
                ["missingTargetCounts"] = new JObject
                {
                    ["renderer.material"] = rendererMaterialMissingTargets,
                    ["material.texture"] = materialTextureMissingTargets,
                },
                ["materialsWithTexture"] = materialsWithTexture,
                ["missingRendererMaterialTargets"] = missingRendererMaterialTargets,
                ["missingMaterialTextureTargets"] = missingMaterialTextureTargets,
                ["issues"] = issues,
                ["missingRendererMaterialTargetSamples"] = missingRendererMaterialTargetSamples,
                ["missingMaterialTextureTargetSamples"] = missingMaterialTextureTargetSamples,
                ["note"] = missingRendererMaterialTargets
                    ? "源索引包含 Renderer -> Material PPtr，但部分非 Unity 内置目标没有解析到真实 Material。模型第一阶段不能把这类样本当作材质完整；应检查完整源目录、VFS/CAB 依赖闭包或源索引解析覆盖。"
                    : missingMaterialTextureTargets
                        ? "源索引包含 Material -> Texture PPtr，但部分非 Unity 内置目标没有解析到真实 Unity 纹理对象。模型可先作为材质诊断样本，不能直接作为贴图完整验收。"
                        : "源索引里的 Renderer/Material/Texture 关系目标已在当前数据库中解析。模型仍需导出 glTF 和视觉验收。"
            };
        }

        private static JObject BuildModelDependencyHealth(SqliteConnection connection)
        {
            var meshObjects = ScalarLong(connection, "SELECT COUNT(*) FROM source_objects WHERE type='Mesh';");
            var meshFilterObjects = ScalarLong(connection, "SELECT COUNT(*) FROM source_objects WHERE type='MeshFilter';");
            var skinnedRendererObjects = ScalarLong(connection, "SELECT COUNT(*) FROM source_objects WHERE type='SkinnedMeshRenderer';");
            var animatorObjects = ScalarLong(connection, "SELECT COUNT(*) FROM source_objects WHERE type='Animator';");
            var avatarObjects = ScalarLong(connection, "SELECT COUNT(*) FROM source_objects WHERE type='Avatar';");
            var meshFilterMeshRelations = SourceRelationCount(connection, "meshFilter.mesh");
            var skinnedMeshRelations = SourceRelationCount(connection, "skinnedMeshRenderer.mesh");
            var animatorAvatarRelations = SourceRelationCount(connection, "animator.avatar");
            var meshFilterMeshDistinctEdges = DistinctRelationEdgeCount(connection, "meshFilter.mesh");
            var skinnedMeshDistinctEdges = DistinctRelationEdgeCount(connection, "skinnedMeshRenderer.mesh");
            var animatorAvatarDistinctEdges = DistinctRelationEdgeCount(connection, "animator.avatar");
            var meshFilterMeshUnityBuiltinTargets = SourceRelationUnityBuiltinTargetCount(connection, "meshFilter.mesh");
            var skinnedMeshUnityBuiltinTargets = SourceRelationUnityBuiltinTargetCount(connection, "skinnedMeshRenderer.mesh");
            var animatorAvatarUnityBuiltinTargets = SourceRelationUnityBuiltinTargetCount(connection, "animator.avatar");
            var meshFilterMeshResolvedTargets = SourceRelationDistinctResolvedTargetCount(connection, "meshFilter.mesh", "Mesh");
            var skinnedMeshResolvedTargets = SourceRelationDistinctResolvedTargetCount(connection, "skinnedMeshRenderer.mesh", "Mesh");
            var animatorAvatarResolvedTargets = SourceRelationDistinctResolvedTargetCount(connection, "animator.avatar", "Avatar");
            var optimizedAnimatorNullAvatarCount = ScalarLong(connection, @"
WITH visible_containers AS (
    SELECT DISTINCT json_extract(preload.raw_json, '$.details.container') AS container_path
    FROM source_relations preload
    JOIN source_objects renderer
      ON renderer.serialized_file = preload.to_file COLLATE NOCASE
     AND renderer.path_id = preload.to_path_id
     AND renderer.type IN ('SkinnedMeshRenderer', 'MeshRenderer')
    WHERE preload.relation = 'assetBundle.containerPreload'
      AND COALESCE(json_extract(preload.raw_json, '$.details.container'), '') <> ''
)
SELECT COUNT(DISTINCT animator.serialized_file || ':' || animator.path_id)
FROM source_relations preload
JOIN visible_containers vc
  ON vc.container_path = json_extract(preload.raw_json, '$.details.container')
JOIN source_objects animator
  ON animator.serialized_file = preload.to_file COLLATE NOCASE
 AND animator.path_id = preload.to_path_id
 AND animator.type = 'Animator'
WHERE preload.relation = 'assetBundle.containerPreload'
  AND COALESCE(json_extract(animator.raw_json, '$.animator.hasTransformHierarchy'), 1) = 0
  AND COALESCE(json_extract(animator.raw_json, '$.animator.avatar.isNull'), 1) = 1;");
            var meshFilterMeshMissingTargets = Math.Max(0, meshFilterMeshDistinctEdges - meshFilterMeshResolvedTargets - meshFilterMeshUnityBuiltinTargets);
            var skinnedMeshMissingTargets = Math.Max(0, skinnedMeshDistinctEdges - skinnedMeshResolvedTargets - skinnedMeshUnityBuiltinTargets);
            var animatorAvatarMissingTargets = Math.Max(0, animatorAvatarDistinctEdges - animatorAvatarResolvedTargets - animatorAvatarUnityBuiltinTargets);
            var missingMeshTargets = (meshFilterMeshDistinctEdges > 0 && meshFilterMeshMissingTargets > 0)
                || (skinnedMeshDistinctEdges > 0 && skinnedMeshMissingTargets > 0);
            var missingAvatarTargets = animatorAvatarDistinctEdges > 0 && animatorAvatarMissingTargets > 0;
            var optimizedAnimatorNullAvatar = optimizedAnimatorNullAvatarCount > 0;
            var missingModelTargets = missingMeshTargets || missingAvatarTargets || optimizedAnimatorNullAvatar;
            var issues = new JArray();
            if (meshFilterMeshDistinctEdges > 0 && meshFilterMeshMissingTargets > 0)
            {
                issues.Add("missingMeshFilterMeshTargets");
            }
            if (skinnedMeshDistinctEdges > 0 && skinnedMeshMissingTargets > 0)
            {
                issues.Add("missingSkinnedMeshTargets");
            }
            if (missingAvatarTargets)
            {
                issues.Add("missingAnimatorAvatarTargets");
            }
            if (optimizedAnimatorNullAvatar)
            {
                issues.Add("optimizedAnimatorNullAvatar");
            }

            return new JObject
            {
                ["status"] = missingModelTargets ? "warning" : "ok",
                ["objectCounts"] = new JObject
                {
                    ["Mesh"] = meshObjects,
                    ["MeshFilter"] = meshFilterObjects,
                    ["SkinnedMeshRenderer"] = skinnedRendererObjects,
                    ["Animator"] = animatorObjects,
                    ["Avatar"] = avatarObjects,
                },
                ["relationCounts"] = new JObject
                {
                    ["meshFilter.mesh"] = meshFilterMeshRelations,
                    ["skinnedMeshRenderer.mesh"] = skinnedMeshRelations,
                    ["animator.avatar"] = animatorAvatarRelations,
                },
                ["distinctRelationCounts"] = new JObject
                {
                    ["meshFilter.mesh"] = meshFilterMeshDistinctEdges,
                    ["skinnedMeshRenderer.mesh"] = skinnedMeshDistinctEdges,
                    ["animator.avatar"] = animatorAvatarDistinctEdges,
                },
                ["resolvedTargetCounts"] = new JObject
                {
                    ["meshFilter.mesh"] = meshFilterMeshResolvedTargets,
                    ["skinnedMeshRenderer.mesh"] = skinnedMeshResolvedTargets,
                    ["animator.avatar"] = animatorAvatarResolvedTargets,
                },
                ["unityBuiltinTargetCounts"] = new JObject
                {
                    ["meshFilter.mesh"] = meshFilterMeshUnityBuiltinTargets,
                    ["skinnedMeshRenderer.mesh"] = skinnedMeshUnityBuiltinTargets,
                    ["animator.avatar"] = animatorAvatarUnityBuiltinTargets,
                },
                ["missingTargetCounts"] = new JObject
                {
                    ["meshFilter.mesh"] = meshFilterMeshMissingTargets,
                    ["skinnedMeshRenderer.mesh"] = skinnedMeshMissingTargets,
                    ["animator.avatar"] = animatorAvatarMissingTargets,
                },
                ["missingMeshTargets"] = missingMeshTargets,
                ["missingAnimatorAvatarTargets"] = missingAvatarTargets,
                ["optimizedAnimatorNullAvatarCount"] = optimizedAnimatorNullAvatarCount,
                ["optimizedAnimatorNullAvatar"] = optimizedAnimatorNullAvatar,
                ["issues"] = issues,
                ["missingMeshFilterMeshTargetSamples"] = BuildMissingRelationTargetSamples(connection, "meshFilter.mesh", "Mesh", 16),
                ["missingSkinnedMeshTargetSamples"] = BuildMissingRelationTargetSamples(connection, "skinnedMeshRenderer.mesh", "Mesh", 16),
                ["missingAnimatorAvatarTargetSamples"] = BuildMissingRelationTargetSamples(connection, "animator.avatar", "Avatar", 16),
                ["optimizedAnimatorNullAvatarSamples"] = BuildOptimizedAnimatorNullAvatarSamples(connection, 16),
                ["note"] = missingMeshTargets
                    ? "源索引包含 Renderer/MeshFilter -> Mesh PPtr，但部分非 Unity 内置目标 Mesh 没有解析进当前数据库。模型第一阶段不能把这类样本当作完整模型；应继续补 VFS/CAB 依赖闭包或使用完整源索引。"
                    : missingAvatarTargets
                        ? "源索引包含 Animator -> Avatar PPtr，但部分非 Unity 内置目标 Avatar 没有解析进当前数据库。优化层级或 Humanoid/Skinned 模型可能无法恢复骨骼，模型第一阶段不能继续推进动画。"
                        : optimizedAnimatorNullAvatar
                            ? "源索引里存在 hasTransformHierarchy=false 但 Animator.m_Avatar 为空的优化层级 Animator。这类根对象无法通过补 CAB 修复，模型第一阶段应换可用主模型或显式降级为诊断样本。"
                            : "源索引里的 MeshFilter/SkinnedMeshRenderer -> Mesh 和 Animator -> Avatar 目标已在当前数据库中解析，且没有发现空 Avatar 的优化层级 Animator。模型仍需导出 glTF 和视觉验收。"
            };
        }

        private static JArray BuildOptimizedAnimatorNullAvatarSamples(SqliteConnection connection, int limit)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
WITH visible_containers AS (
    SELECT DISTINCT json_extract(preload.raw_json, '$.details.container') AS container_path
    FROM source_relations preload
    JOIN source_objects renderer
      ON renderer.serialized_file = preload.to_file COLLATE NOCASE
     AND renderer.path_id = preload.to_path_id
     AND renderer.type IN ('SkinnedMeshRenderer', 'MeshRenderer')
    WHERE preload.relation = 'assetBundle.containerPreload'
      AND COALESCE(json_extract(preload.raw_json, '$.details.container'), '') <> ''
)
SELECT animator.source_path, animator.serialized_file, animator.path_id, animator.name,
       json_extract(preload.raw_json, '$.details.container') AS container_path
FROM source_relations preload
JOIN visible_containers vc
  ON vc.container_path = json_extract(preload.raw_json, '$.details.container')
JOIN source_objects animator
  ON animator.serialized_file = preload.to_file COLLATE NOCASE
 AND animator.path_id = preload.to_path_id
 AND animator.type = 'Animator'
WHERE preload.relation = 'assetBundle.containerPreload'
  AND COALESCE(json_extract(animator.raw_json, '$.animator.hasTransformHierarchy'), 1) = 0
  AND COALESCE(json_extract(animator.raw_json, '$.animator.avatar.isNull'), 1) = 1
ORDER BY animator.source_path COLLATE NOCASE, animator.name COLLATE NOCASE
LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", Math.Max(1, limit));
            var samples = new JArray();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                samples.Add(new JObject
                {
                    ["sourcePath"] = ReadString(reader, "source_path"),
                    ["serializedFile"] = ReadString(reader, "serialized_file"),
                    ["pathId"] = ReadInt64(reader, "path_id"),
                    ["name"] = ReadString(reader, "name"),
                    ["containerPath"] = ReadString(reader, "container_path"),
                    ["issue"] = "hasTransformHierarchy=false but Animator.m_Avatar is null"
                });
            }

            return samples;
        }

        private static JArray BuildMissingRelationTargetSamples(SqliteConnection connection, string relation, string targetType, int limit)
        {
            var targetTypeFilter = string.IsNullOrWhiteSpace(targetType)
                ? "1 = 1"
                : targetType == "UnityTexture"
                    ? UnityTextureTypeSql("target")
                : targetType == "Texture2D"
                    ? "target.type IN ('Texture2D', 'Texture2DArray')"
                    : "target.type = $targetType";
            using var command = connection.CreateCommand();
            command.CommandText = $@"
SELECT
    r.to_file,
    r.to_path_id,
    r.to_type_hint,
    COUNT(*) AS relation_count,
    COUNT(DISTINCT r.from_file || ':' || r.from_path_id) AS distinct_referrer_count,
    COUNT(DISTINCT r.from_source) AS source_file_count,
    MIN(r.from_source) AS sample_from_source,
    MIN(r.from_file) AS sample_from_file,
    MIN(r.from_type) AS sample_from_type,
    MIN(r.from_name) AS sample_from_name,
    MIN(r.from_path_id) AS sample_from_path_id,
    MIN(e.file_name) AS sample_external_file_name,
    MIN(e.path_name) AS sample_external_path_name,
    MIN(e.guid) AS sample_external_guid
FROM source_relations r
LEFT JOIN source_objects target
  ON target.serialized_file = r.to_file COLLATE NOCASE
 AND target.path_id = r.to_path_id
 AND {targetTypeFilter}
LEFT JOIN source_externals e
  ON e.serialized_file = r.from_file COLLATE NOCASE
 AND e.file_id = r.to_file_id
WHERE r.relation = $relation
  AND target.id IS NULL
  AND NOT ({UnityBuiltinTargetSql("r.to_file")})
GROUP BY r.to_file, r.to_path_id, r.to_type_hint
ORDER BY relation_count DESC, distinct_referrer_count DESC
LIMIT $limit;";
            command.Parameters.AddWithValue("$relation", relation);
            if (!string.IsNullOrWhiteSpace(targetType)
                && targetType != "Texture2D"
                && targetType != "UnityTexture")
            {
                command.Parameters.AddWithValue("$targetType", targetType);
            }
            command.Parameters.AddWithValue("$limit", limit);
            var samples = new JArray();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                samples.Add(new JObject
                {
                    ["targetFile"] = ReadString(reader, "to_file"),
                    ["targetPathId"] = ReadInt64(reader, "to_path_id"),
                    ["targetTypeHint"] = ReadString(reader, "to_type_hint"),
                    ["relationCount"] = ReadInt64(reader, "relation_count"),
                    ["distinctReferrerCount"] = ReadInt64(reader, "distinct_referrer_count"),
                    ["sourceFileCount"] = ReadInt64(reader, "source_file_count"),
                    ["sampleReferrer"] = new JObject
                    {
                        ["source"] = ReadString(reader, "sample_from_source"),
                        ["serializedFile"] = ReadString(reader, "sample_from_file"),
                        ["type"] = ReadString(reader, "sample_from_type"),
                        ["name"] = ReadString(reader, "sample_from_name"),
                        ["pathId"] = ReadInt64(reader, "sample_from_path_id"),
                    },
                    ["sampleExternal"] = new JObject
                    {
                        ["fileName"] = ReadString(reader, "sample_external_file_name"),
                        ["pathName"] = ReadString(reader, "sample_external_path_name"),
                        ["guid"] = ReadString(reader, "sample_external_guid"),
                    },
                });
            }

            return samples;
        }

        private static string UnityTextureTypeSql(string alias)
        {
            // Unity 的 Material.m_TexEnvs 是 PPtr<Texture>，目标不只可能是 Texture2D。
            return $"{alias}.type IN ('Texture2D', 'Texture2DArray', 'Texture3D', 'Cubemap')";
        }

        private static JArray BuildMonoBehaviourPPtrTargetTypeBreakdown(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT
    COALESCE(target.type, '(missing)') AS target_type,
    COUNT(*) AS relation_count,
    COUNT(DISTINCT r.from_file || ':' || r.from_path_id || '>' || r.to_file || ':' || r.to_path_id) AS edge_count,
    COUNT(DISTINCT r.from_file || ':' || r.from_path_id) AS referrer_count
FROM source_relations r
LEFT JOIN source_objects target
  ON target.serialized_file = r.to_file COLLATE NOCASE
 AND target.path_id = r.to_path_id
WHERE r.relation = 'monoBehaviour.pptr'
GROUP BY COALESCE(target.type, '(missing)')
ORDER BY relation_count DESC, edge_count DESC
LIMIT 64;";
            var result = new JArray();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new JObject
                {
                    ["targetType"] = ReadString(reader, "target_type"),
                    ["relationCount"] = ReadInt64(reader, "relation_count"),
                    ["edgeCount"] = ReadInt64(reader, "edge_count"),
                    ["referrerCount"] = ReadInt64(reader, "referrer_count"),
                });
            }

            return result;
        }

        private static JArray BuildMonoBehaviourPPtrScriptBreakdown(SqliteConnection connection, int limit)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT
    COALESCE(json_extract(mono.raw_json, '$.monoBehaviour.scriptName'), '') AS script_name,
    COUNT(*) AS relation_count,
    COUNT(DISTINCT r.from_file || ':' || r.from_path_id) AS object_count,
    COUNT(DISTINCT r.to_file || ':' || r.to_path_id) AS distinct_target_count,
    MIN(r.from_source) AS sample_source,
    MIN(r.from_file) AS sample_file,
    MIN(r.from_path_id) AS sample_path_id,
    MIN(target.type) AS sample_target_type,
    MIN(target.name) AS sample_target_name
FROM source_relations r
LEFT JOIN source_objects mono
  ON mono.serialized_file = r.from_file COLLATE NOCASE
 AND mono.path_id = r.from_path_id
 AND mono.type = 'MonoBehaviour'
LEFT JOIN source_objects target
  ON target.serialized_file = r.to_file COLLATE NOCASE
 AND target.path_id = r.to_path_id
WHERE r.relation = 'monoBehaviour.pptr'
GROUP BY COALESCE(json_extract(mono.raw_json, '$.monoBehaviour.scriptName'), '')
ORDER BY relation_count DESC, object_count DESC
LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", limit);
            var result = new JArray();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new JObject
                {
                    ["scriptName"] = ReadString(reader, "script_name"),
                    ["relationCount"] = ReadInt64(reader, "relation_count"),
                    ["objectCount"] = ReadInt64(reader, "object_count"),
                    ["distinctTargetCount"] = ReadInt64(reader, "distinct_target_count"),
                    ["sample"] = new JObject
                    {
                        ["source"] = ReadString(reader, "sample_source"),
                        ["serializedFile"] = ReadString(reader, "sample_file"),
                        ["pathId"] = ReadInt64(reader, "sample_path_id"),
                        ["targetType"] = ReadString(reader, "sample_target_type"),
                        ["targetName"] = ReadString(reader, "sample_target_name"),
                    },
                });
            }

            return result;
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
            var avatarTosAvatars = ScalarLong(connection, @"
SELECT COUNT(*)
FROM source_objects
WHERE type='Avatar'
  AND COALESCE(json_array_length(json_extract(raw_json, '$.avatar.tos')), 0) > 0;");
            var avatarTosEntryCount = ScalarLong(connection, @"
SELECT COALESCE(SUM(json_array_length(json_extract(raw_json, '$.avatar.tos'))), 0)
FROM source_objects
WHERE type='Avatar';");

            return new JObject
            {
                ["status"] = avatarObjects == 0
                    ? "not_applicable"
                    : completeOracleAvatars > 0 ? "ok" : "warning",
                ["avatarObjects"] = avatarObjects,
                ["humanDescriptionAvatars"] = humanDescriptionAvatars,
                ["avatarConstantOracleAvatars"] = avatarConstantOracleAvatars,
                ["completeAvatarConstantOracleAvatars"] = completeOracleAvatars,
                ["avatarTosAvatars"] = avatarTosAvatars,
                ["avatarTosEntryCount"] = avatarTosEntryCount,
                ["humanDescriptionCoverage"] = Ratio(humanDescriptionAvatars, avatarObjects),
                ["avatarConstantOracleCoverage"] = Ratio(avatarConstantOracleAvatars, avatarObjects),
                ["completeAvatarConstantOracleCoverage"] = Ratio(completeOracleAvatars, avatarObjects),
                ["avatarTosCoverage"] = Ratio(avatarTosAvatars, avatarObjects),
                ["note"] = completeOracleAvatars > 0
                    ? "源索引包含 AvatarConstant oracle 元数据。Avatar.m_TOS 可作为 AnimationClip pathHash 解析证据，但结构兼容仍不能单独升级成生产动画关系。"
                    : "源索引缺少完整 AvatarConstant oracle 元数据；Humanoid/Muscle bake 只能依赖完整 HumanDescription 或 Unity 工程内真实 prefab。"
            };
        }

        // 只做路径解析诊断：确认 hash-only binding 是否能被 Unity Avatar.m_TOS 还原成骨骼路径。
        private static JObject BuildAnimationPathHashHealth(SqliteConnection connection)
        {
            if (!TableExists(connection, "source_animation_bindings") || !TableExists(connection, "source_objects"))
            {
                return new JObject
                {
                    ["status"] = "empty",
                    ["note"] = "源索引缺少 source_animation_bindings 或 source_objects，无法检查 AnimationClip pathHash。"
                };
            }

            var avatarTos = ReadAvatarTosIndexes(connection);
            var allAvatarHashes = new HashSet<uint>();
            foreach (var avatar in avatarTos)
            {
                foreach (var hash in avatar.Hashes)
                {
                    allAvatarHashes.Add(hash);
                }
            }

            var sourceAnimationBindingRows = TableRowCount(connection, "source_animation_bindings");
            if (sourceAnimationBindingRows == 0)
            {
                return new JObject
                {
                    ["status"] = "empty",
                    ["sourceAnimationBindingRows"] = 0,
                    ["avatarTosAvatars"] = avatarTos.Count,
                    ["avatarTosHashCount"] = allAvatarHashes.Count,
                    ["note"] = "源索引没有 source_animation_bindings，无法检查 AnimationClip pathHash。"
                };
            }

            if (allAvatarHashes.Count == 0)
            {
                return new JObject
                {
                    ["status"] = "warning",
                    ["sourceAnimationBindingRows"] = sourceAnimationBindingRows,
                    ["avatarTosAvatars"] = avatarTos.Count,
                    ["avatarTosHashCount"] = 0,
                    ["hashOnlyClipCount"] = 0,
                    ["hashOnlyUniquePathHashTotal"] = 0,
                    ["hashOnlyResolvedByAnyAvatarTosTotal"] = 0,
                    ["hashOnlyAnyAvatarTosCoverage"] = 0.0,
                    ["fullyResolvedByAnyAvatarTosClipCount"] = 0,
                    ["zeroPathHashBindingCount"] = 0,
                    ["rawJsonParseErrorCount"] = 0,
                    ["clipSamples"] = new JArray(),
                    ["rule"] = "只检查 AnimationClip binding 的 pathHash 能否被 Avatar.m_TOS 解析。它是 Naraka hash-only 动画路径恢复证据，不会生成默认模型-动画关系，也不代表 glTF TRS 动画已通过视觉验收。",
                    ["note"] = "源索引包含动画 binding，但没有可用 Avatar.m_TOS hash -> path 查表。请先用新版工具重建 unity_source_index.db。"
                };
            }

            long hashOnlyClipCount = 0;
            long fullyResolvedByAnyAvatarTosClipCount = 0;
            long hashOnlyUniquePathHashTotal = 0;
            long hashOnlyResolvedByAnyAvatarTosTotal = 0;
            long zeroPathHashBindingCount = 0;
            long rawJsonParseErrorCount = 0;
            var parseErrorSamples = new JArray();
            var clipSamples = new List<AnimationPathHashClipSample>();

            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT animation_name, animation_source, animation_file, animation_path_id, binding_count, raw_json
FROM source_animation_bindings
ORDER BY COALESCE(binding_count, 0) DESC, animation_name COLLATE NOCASE;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var animationName = ReadString(reader, "animation_name");
                var animationSource = ReadString(reader, "animation_source");
                var animationFile = ReadString(reader, "animation_file");
                var animationPathId = ReadInt64(reader, "animation_path_id");
                var bindingCount = ReadInt64(reader, "binding_count");
                var rawJson = ReadString(reader, "raw_json");
                HashSet<uint> hashOnlyPathHashes;
                long clipZeroPathHashes;
                try
                {
                    hashOnlyPathHashes = ExtractHashOnlyBindingPathHashes(rawJson, out clipZeroPathHashes);
                }
                catch (JsonException e)
                {
                    rawJsonParseErrorCount++;
                    if (parseErrorSamples.Count < 8)
                    {
                        parseErrorSamples.Add(new JObject
                        {
                            ["animation"] = animationName,
                            ["animationFile"] = animationFile,
                            ["animationPathId"] = animationPathId,
                            ["error"] = e.GetType().Name + ": " + e.Message,
                        });
                    }
                    continue;
                }

                zeroPathHashBindingCount += clipZeroPathHashes;
                if (hashOnlyPathHashes.Count == 0)
                {
                    continue;
                }

                hashOnlyClipCount++;
                var resolvedByAnyAvatar = hashOnlyPathHashes.Count(hash => allAvatarHashes.Contains(hash));
                hashOnlyUniquePathHashTotal += hashOnlyPathHashes.Count;
                hashOnlyResolvedByAnyAvatarTosTotal += resolvedByAnyAvatar;
                if (resolvedByAnyAvatar == hashOnlyPathHashes.Count)
                {
                    fullyResolvedByAnyAvatarTosClipCount++;
                }

                if (clipSamples.Count < 24)
                {
                    clipSamples.Add(BuildAnimationPathHashClipSample(
                        animationName,
                        animationSource,
                        animationFile,
                        animationPathId,
                        bindingCount,
                        hashOnlyPathHashes,
                        resolvedByAnyAvatar,
                        avatarTos));
                }
            }

            var status = rawJsonParseErrorCount > 0
                ? "warning"
                : hashOnlyClipCount == 0
                    ? "empty"
                    : Ratio(hashOnlyResolvedByAnyAvatarTosTotal, hashOnlyUniquePathHashTotal) >= 0.9
                        ? "ok"
                        : "warning";

            return new JObject
            {
                ["status"] = status,
                ["sourceAnimationBindingRows"] = sourceAnimationBindingRows,
                ["avatarTosAvatars"] = avatarTos.Count,
                ["avatarTosHashCount"] = allAvatarHashes.Count,
                ["hashOnlyClipCount"] = hashOnlyClipCount,
                ["hashOnlyUniquePathHashTotal"] = hashOnlyUniquePathHashTotal,
                ["hashOnlyResolvedByAnyAvatarTosTotal"] = hashOnlyResolvedByAnyAvatarTosTotal,
                ["hashOnlyAnyAvatarTosCoverage"] = Ratio(hashOnlyResolvedByAnyAvatarTosTotal, hashOnlyUniquePathHashTotal),
                ["fullyResolvedByAnyAvatarTosClipCount"] = fullyResolvedByAnyAvatarTosClipCount,
                ["zeroPathHashBindingCount"] = zeroPathHashBindingCount,
                ["rawJsonParseErrorCount"] = rawJsonParseErrorCount,
                ["rawJsonParseErrorSamples"] = parseErrorSamples,
                ["clipSamples"] = new JArray(clipSamples.Select(x => x.ToJson())),
                ["rule"] = "只检查 AnimationClip binding 的 pathHash 能否被 Avatar.m_TOS 解析。它是 Naraka hash-only 动画路径恢复证据，不会生成默认模型-动画关系，也不代表 glTF TRS 动画已通过视觉验收。",
                ["note"] = hashOnlyClipCount > 0
                    ? "如果 bestAvatarTosCoverage 接近 1，可以作为后续定向模型+动画预览的结构证据；仍需 Animator/Prefab/Controller 显式关系、模型材质验收和动画截帧验收。"
                    : "当前源索引没有发现需要 Avatar.m_TOS 辅助解析的 hash-only AnimationClip binding。"
            };
        }

        private static List<AvatarTosIndex> ReadAvatarTosIndexes(SqliteConnection connection)
        {
            var result = new List<AvatarTosIndex>();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT name, source_path, serialized_file, path_id, raw_json
FROM source_objects
WHERE type='Avatar'
  AND COALESCE(json_array_length(json_extract(raw_json, '$.avatar.tos')), 0) > 0;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var rawJson = ReadString(reader, "raw_json");
                JObject raw;
                try
                {
                    raw = JObject.Parse(rawJson);
                }
                catch (JsonException)
                {
                    continue;
                }

                var paths = new Dictionary<uint, string>();
                foreach (var item in raw.SelectToken("avatar.tos") as JArray ?? new JArray())
                {
                    if (!TryReadUInt32(item["pathHash"], out var hash) || hash == 0)
                    {
                        continue;
                    }

                    var path = item["path"]?.ToString();
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    paths.TryAdd(hash, path);
                }

                if (paths.Count == 0)
                {
                    continue;
                }

                result.Add(new AvatarTosIndex(
                    ReadString(reader, "name"),
                    ReadString(reader, "source_path"),
                    ReadString(reader, "serialized_file"),
                    ReadInt64(reader, "path_id"),
                    paths));
            }

            return result;
        }

        private static HashSet<uint> ExtractHashOnlyBindingPathHashes(string rawJson, out long zeroPathHashBindingCount)
        {
            zeroPathHashBindingCount = 0;
            var result = new HashSet<uint>();
            var raw = JObject.Parse(rawJson);
            foreach (var binding in raw["bindings"] as JArray ?? new JArray())
            {
                var path = binding["path"]?.ToString();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                if (!TryReadUInt32(binding["pathHash"], out var hash) || hash == 0)
                {
                    zeroPathHashBindingCount++;
                    continue;
                }

                result.Add(hash);
            }

            return result;
        }

        private static AnimationPathHashClipSample BuildAnimationPathHashClipSample(
            string animationName,
            string animationSource,
            string animationFile,
            long animationPathId,
            long bindingCount,
            HashSet<uint> hashOnlyPathHashes,
            int resolvedByAnyAvatar,
            IReadOnlyList<AvatarTosIndex> avatarTos)
        {
            AvatarTosIndex bestAvatar = null;
            var bestResolved = 0;
            foreach (var avatar in avatarTos)
            {
                var resolved = hashOnlyPathHashes.Count(hash => avatar.Hashes.Contains(hash));
                if (resolved > bestResolved)
                {
                    bestResolved = resolved;
                    bestAvatar = avatar;
                    if (bestResolved == hashOnlyPathHashes.Count)
                    {
                        break;
                    }
                }
            }

            var resolvedSamples = new JArray();
            var unresolvedSamples = new JArray();
            if (bestAvatar != null)
            {
                foreach (var hash in hashOnlyPathHashes.OrderBy(x => x).Take(512))
                {
                    if (bestAvatar.HashToPath.TryGetValue(hash, out var path))
                    {
                        if (resolvedSamples.Count < 8)
                        {
                            resolvedSamples.Add(new JObject
                            {
                                ["pathHash"] = hash,
                                ["pathHashHex"] = $"0x{hash:X8}",
                                ["path"] = path,
                            });
                        }
                    }
                    else if (unresolvedSamples.Count < 8)
                    {
                        unresolvedSamples.Add(new JObject
                        {
                            ["pathHash"] = hash,
                            ["pathHashHex"] = $"0x{hash:X8}",
                        });
                    }

                    if (resolvedSamples.Count >= 8 && unresolvedSamples.Count >= 8)
                    {
                        break;
                    }
                }
            }

            return new AnimationPathHashClipSample
            {
                AnimationName = animationName,
                AnimationSource = animationSource,
                AnimationFile = animationFile,
                AnimationPathId = animationPathId,
                BindingCount = bindingCount,
                UniqueHashOnlyPathHashCount = hashOnlyPathHashes.Count,
                ResolvedByAnyAvatarTos = resolvedByAnyAvatar,
                BestAvatar = bestAvatar,
                BestAvatarResolved = bestResolved,
                ResolvedPathSamples = resolvedSamples,
                UnresolvedHashSamples = unresolvedSamples,
            };
        }

        private static bool TryReadUInt32(JToken token, out uint value)
        {
            value = 0;
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }

            if (token.Type == JTokenType.Integer)
            {
                var number = token.Value<long>();
                if (number < 0 || number > uint.MaxValue)
                {
                    return false;
                }

                value = (uint)number;
                return true;
            }

            var text = token.ToString().Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(2);
                return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
            }

            return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private sealed class AvatarTosIndex
        {
            public AvatarTosIndex(string name, string sourcePath, string serializedFile, long pathId, Dictionary<uint, string> hashToPath)
            {
                Name = name;
                SourcePath = sourcePath;
                SerializedFile = serializedFile;
                PathId = pathId;
                HashToPath = hashToPath;
                Hashes = hashToPath.Keys.ToHashSet();
            }

            public string Name { get; }
            public string SourcePath { get; }
            public string SerializedFile { get; }
            public long PathId { get; }
            public Dictionary<uint, string> HashToPath { get; }
            public HashSet<uint> Hashes { get; }
        }

        private sealed class AnimationPathHashClipSample
        {
            public string AnimationName { get; set; }
            public string AnimationSource { get; set; }
            public string AnimationFile { get; set; }
            public long AnimationPathId { get; set; }
            public long BindingCount { get; set; }
            public int UniqueHashOnlyPathHashCount { get; set; }
            public int ResolvedByAnyAvatarTos { get; set; }
            public AvatarTosIndex BestAvatar { get; set; }
            public int BestAvatarResolved { get; set; }
            public JArray ResolvedPathSamples { get; set; }
            public JArray UnresolvedHashSamples { get; set; }

            public JObject ToJson()
            {
                return new JObject
                {
                    ["animationName"] = AnimationName,
                    ["animationSource"] = AnimationSource,
                    ["animationFile"] = AnimationFile,
                    ["animationPathId"] = AnimationPathId,
                    ["bindingCount"] = BindingCount,
                    ["uniqueHashOnlyPathHashCount"] = UniqueHashOnlyPathHashCount,
                    ["resolvedByAnyAvatarTos"] = ResolvedByAnyAvatarTos,
                    ["anyAvatarTosCoverage"] = Ratio(ResolvedByAnyAvatarTos, UniqueHashOnlyPathHashCount),
                    ["bestAvatar"] = BestAvatar == null
                        ? null
                        : new JObject
                        {
                            ["name"] = BestAvatar.Name,
                            ["sourcePath"] = BestAvatar.SourcePath,
                            ["serializedFile"] = BestAvatar.SerializedFile,
                            ["pathId"] = BestAvatar.PathId,
                        },
                    ["bestAvatarResolvedPathHashCount"] = BestAvatarResolved,
                    ["bestAvatarTosCoverage"] = Ratio(BestAvatarResolved, UniqueHashOnlyPathHashCount),
                    ["resolvedPathSamples"] = ResolvedPathSamples ?? new JArray(),
                    ["unresolvedHashSamples"] = UnresolvedHashSamples ?? new JArray(),
                };
            }
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

        private static long SourceRelationResolvedTargetCount(SqliteConnection connection, string relation, string targetType)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT COUNT(*)
FROM source_relations r
JOIN source_objects o
  ON o.serialized_file = r.to_file COLLATE NOCASE
 AND o.path_id = r.to_path_id
 AND o.type = $targetType
WHERE r.relation = $relation;";
            command.Parameters.AddWithValue("$relation", relation);
            command.Parameters.AddWithValue("$targetType", targetType);
            return Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }

        private static long DistinctRelationEdgeCount(SqliteConnection connection, string relation)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT COUNT(DISTINCT r.from_file || ':' || r.from_path_id || '>' || r.to_file || ':' || r.to_path_id)
FROM source_relations r
WHERE r.relation = $relation;";
            command.Parameters.AddWithValue("$relation", relation);
            return Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }

        private static long SourceRelationUnityBuiltinTargetCount(SqliteConnection connection, string relation)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $@"
SELECT COUNT(DISTINCT r.from_file || ':' || r.from_path_id || '>' || r.to_file || ':' || r.to_path_id)
FROM source_relations r
WHERE r.relation = $relation
  AND {UnityBuiltinTargetSql("r.to_file")};";
            command.Parameters.AddWithValue("$relation", relation);
            return Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }

        private static string UnityBuiltinTargetSql(string column)
        {
            // Unity 内置默认资源不会出现在游戏 CAB 的 source_objects 里，不能当作 Naraka 缺依赖。
            return $"LOWER(COALESCE({column}, '')) IN ('unity default resources', 'resources/unity_builtin_extra')";
        }

        private static long SourceRelationDistinctResolvedTargetCount(SqliteConnection connection, string relation, string targetType)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT COUNT(DISTINCT r.from_file || ':' || r.from_path_id || '>' || r.to_file || ':' || r.to_path_id)
FROM source_relations r
JOIN source_objects o
  ON o.serialized_file = r.to_file COLLATE NOCASE
 AND o.path_id = r.to_path_id
 AND o.type = $targetType
WHERE r.relation = $relation;";
            command.Parameters.AddWithValue("$relation", relation);
            command.Parameters.AddWithValue("$targetType", targetType);
            return Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }

        private static long SourceRelationDistinctResolvedAnyTargetCount(SqliteConnection connection, string relation)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT COUNT(DISTINCT r.from_file || ':' || r.from_path_id || '>' || r.to_file || ':' || r.to_path_id)
FROM source_relations r
JOIN source_objects o
  ON o.serialized_file = r.to_file COLLATE NOCASE
 AND o.path_id = r.to_path_id
WHERE r.relation = $relation;";
            command.Parameters.AddWithValue("$relation", relation);
            return Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }

        private static long ScalarLong(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }

        private static string ReadString(SqliteDataReader reader, string column)
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
        }

        private static long ReadInt64(SqliteDataReader reader, string column)
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }

        private static string ScalarString(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar() as string;
        }

        private static string[] ReadIndexedSourceFiles(SqliteConnection connection, string sourceRoot)
        {
            if (!TableExists(connection, "source_files"))
            {
                return Array.Empty<string>();
            }

            var result = new List<string>();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT path FROM source_files ORDER BY path COLLATE NOCASE;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var fullPath = Path.IsPathRooted(path)
                    ? path
                    : Path.Combine(sourceRoot, path.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(fullPath))
                {
                    result.Add(Path.GetFullPath(fullPath));
                }
            }

            return result.ToArray();
        }

        private static Dictionary<string, long> ReadSourceIndexCountsForSummary(SqliteConnection connection, string outputRoot, string dbPath)
        {
            var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var summaryPath = GetSourceIndexSummaryPath(outputRoot, dbPath);
            if (File.Exists(summaryPath))
            {
                try
                {
                    var summary = JObject.Parse(File.ReadAllText(summaryPath));
                    if (summary["counts"] is JObject oldCounts)
                    {
                        foreach (var property in oldCounts.Properties())
                        {
                            if (property.Value.Type == JTokenType.Integer ||
                                long.TryParse(property.Value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                            {
                                counts[property.Name] = property.Value.Value<long>();
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.Warning($"Unable to reuse existing source index summary counts. {e.GetType().Name}: {e.Message}");
                }
            }

            counts["sourceFiles"] = TableRowCount(connection, "source_files");
            counts["serializedFiles"] = TableRowCount(connection, "serialized_files");
            counts["sourceObjects"] = TableRowCount(connection, "source_objects");
            counts["sourceExternals"] = TableRowCount(connection, "source_externals");
            counts["sourceRelations"] = TableRowCount(connection, "source_relations");
            counts["sourceAnimationBindings"] = TableRowCount(connection, "source_animation_bindings");
            return counts;
        }

        private static long TableRowCount(SqliteConnection connection, string tableName)
        {
            if (!TableExists(connection, tableName))
            {
                return 0;
            }

            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
            return Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }

        private static bool TableExists(SqliteConnection connection, string tableName)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
            command.Parameters.AddWithValue("$name", tableName);
            return Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture) > 0;
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

        private static List<SourceIndexLoadBatch> BuildLoadBatches(
            string[] files,
            int batchFiles,
            Game game,
            bool hasExplicitEndfieldVfsDiagnosticFilter,
            IReadOnlyDictionary<string, string[]> endfieldVfsInnerFileFilters = null,
            Func<string, bool> endfieldExplicitInnerFileFilter = null,
            bool includeEndfieldAutoRootsWithExplicitFilter = false,
            HashSet<string> endfieldAutoRootFiles = null)
        {
            var batches = new List<SourceIndexLoadBatch>();
            var current = new List<string>(batchFiles);

            void FlushCurrent()
            {
                if (current.Count == 0)
                {
                    return;
                }

                batches.Add(SourceIndexLoadBatch.ForFiles(current.ToArray()));
                current.Clear();
            }

            foreach (var file in files)
            {
                if (IsLikelyEndfieldVfsFile(file, game)
                    && (!hasExplicitEndfieldVfsDiagnosticFilter || includeEndfieldAutoRootsWithExplicitFilter))
                {
                    FlushCurrent();
                    string[] selectedInnerFiles = null;
                    endfieldVfsInnerFileFilters?.TryGetValue(Path.GetFullPath(file), out selectedInnerFiles);
                    var autoRootSelected = !hasExplicitEndfieldVfsDiagnosticFilter
                        || (endfieldAutoRootFiles != null && endfieldAutoRootFiles.Contains(Path.GetFullPath(file)));
                    var includeAllAutoInnerFiles = autoRootSelected && selectedInnerFiles == null;
                    var closureFilter = includeEndfieldAutoRootsWithExplicitFilter
                        ? endfieldExplicitInnerFileFilter
                        : null;
                    AddEndfieldVfsInnerBatches(
                        batches,
                        file,
                        game,
                        batchFiles,
                        selectedInnerFiles,
                        closureFilter,
                        includeAllAutoInnerFiles);
                    continue;
                }

                var length = SafeFileLength(file);
                if (length >= LargeSourceIndexFileBytes)
                {
                    FlushCurrent();
                    batches.Add(SourceIndexLoadBatch.ForFiles(new[] { file }));
                    continue;
                }

                current.Add(file);
                if (current.Count >= batchFiles)
                {
                    FlushCurrent();
                }
            }

            FlushCurrent();
            return batches;
        }

        private static EndfieldVfsSourceSelection SelectEndfieldVfsSourceFiles(string[] files, Game game, bool keepSameLengthSupplemental)
        {
            if (files == null || files.Length == 0 || game == null || !game.Type.IsArknightsEndfieldGroup())
            {
                return EndfieldVfsSourceSelection.FromSelected(files ?? Array.Empty<string>());
            }

            var selected = new List<string>(files.Length);
            var innerFileFilters = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            var endfieldGroups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                if (!IsLikelyEndfieldVfsFile(file, game))
                {
                    selected.Add(file);
                    continue;
                }

                var key = GetEndfieldVfsBucketKey(file);
                if (string.IsNullOrWhiteSpace(key))
                {
                    selected.Add(file);
                    continue;
                }

                if (!endfieldGroups.TryGetValue(key, out var group))
                {
                    group = new List<string>();
                    endfieldGroups[key] = group;
                }
                group.Add(file);
            }

            var skippedDuplicates = 0;
            var differentNamedPairs = 0;
            var sameNamedDifferentLengthInnerFiles = 0;
            var sameNamedSameLengthInnerFiles = 0;
            foreach (var group in endfieldGroups.Values)
            {
                if (group.Count == 1)
                {
                    selected.Add(group[0]);
                    continue;
                }

                var ordered = group
                    // 先选本机 chunk 闭包完整的 VFS。Endfield 的 Persistent 目录可能只有
                    // 热更新元数据或少量 chunk，盲目优先会让源索引丢 Mesh/材质依赖。
                    .OrderByDescending(path => GetEndfieldVfsReadableUnityBundleChunkRatio(path, game))
                    .ThenByDescending(IsPersistentEndfieldVfsPath)
                    .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var primary = ordered[0];
                selected.Add(primary);
                var primaryReadableRatio = GetEndfieldVfsReadableUnityBundleChunkRatio(primary, game);
                if (primaryReadableRatio < 1.0)
                {
                    Logger.Warning($"Endfield VFS selected primary has incomplete local chunks ({primaryReadableRatio:P0} readable): {primary}");
                }
                var primaryInnerFiles = ListEndfieldVfsInnerFiles(primary, game);
                var primaryInnerLengths = primaryInnerFiles
                    .GroupBy(x => NormalizeEndfieldVfsInnerFileName(x.FileName), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        x => x.Key,
                        x => x.Max(entry => Math.Max(0, entry.Length)),
                        StringComparer.OrdinalIgnoreCase);
                foreach (var file in ordered.Skip(1))
                {
                    if (AreSameFileContent(primary, file))
                    {
                        skippedDuplicates++;
                        continue;
                    }

                    var extraEntries = ListEndfieldVfsInnerFiles(file, game)
                        .OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (extraEntries.Length == 0)
                    {
                        skippedDuplicates++;
                        continue;
                    }

                    var sameNameDifferentLengthCount = extraEntries.Count(x =>
                    {
                        var normalized = NormalizeEndfieldVfsInnerFileName(x.FileName);
                        return primaryInnerLengths.TryGetValue(normalized, out var primaryLength)
                            && primaryLength != Math.Max(0, x.Length);
                    });
                    var sameNameSameLengthCount = extraEntries.Count(x =>
                    {
                        var normalized = NormalizeEndfieldVfsInnerFileName(x.FileName);
                        return primaryInnerLengths.TryGetValue(normalized, out var primaryLength)
                            && primaryLength == Math.Max(0, x.Length);
                    });
                    var extraInnerFiles = extraEntries
                        .Where(x =>
                        {
                            var normalized = NormalizeEndfieldVfsInnerFileName(x.FileName);
                            if (!primaryInnerLengths.TryGetValue(normalized, out var primaryLength))
                            {
                                return true;
                            }

                            if (primaryLength != Math.Max(0, x.Length))
                            {
                                return true;
                            }

                            return keepSameLengthSupplemental;
                        })
                        .Select(x => x.FileName)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (extraInnerFiles.Length == 0)
                    {
                        skippedDuplicates++;
                        sameNamedDifferentLengthInnerFiles += sameNameDifferentLengthCount;
                        sameNamedSameLengthInnerFiles += sameNameSameLengthCount;
                        Logger.Info($"Endfield VFS skips supplemental StreamingAssets block for {GetEndfieldVfsBucketKey(file)} because only same-name same-length inner files were found ({sameNameSameLengthCount} counted). Use --endfield_vfs_keep_same_length_supplemental for strict diagnostic closure.");
                        continue;
                    }

                    selected.Add(file);
                    innerFileFilters[Path.GetFullPath(file)] = extraInnerFiles;
                    differentNamedPairs++;
                    sameNamedDifferentLengthInnerFiles += sameNameDifferentLengthCount;
                    sameNamedSameLengthInnerFiles += sameNameSameLengthCount;
                    Logger.Info($"Endfield VFS keeps {extraInnerFiles.Length} supplemental StreamingAssets inner file(s) for {GetEndfieldVfsBucketKey(file)} ({sameNameDifferentLengthCount} same-name length-changed, {sameNameSameLengthCount} same-name same-length {(keepSameLengthSupplemental ? "kept" : "skipped")}).");
                }
            }

            var selectedArray = selected
                .OrderBy(x => SafeFileLength(x))
                .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new EndfieldVfsSourceSelection(selectedArray, skippedDuplicates, differentNamedPairs, sameNamedDifferentLengthInnerFiles, sameNamedSameLengthInnerFiles, innerFileFilters);
        }

        private static IReadOnlyList<EndfieldVfsBlockFile.UnityBundleEntry> ListEndfieldVfsInnerFiles(string blockListPath, Game game)
        {
            try
            {
                return EndfieldVfsBlockFile.ListUnityBundleFiles(blockListPath, game.Type)
                    .Where(x => !string.IsNullOrWhiteSpace(x.FileName))
                    .ToArray();
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is InvalidCastException || e is ArgumentException)
            {
                Logger.Warning($"Unable to compare Endfield VFS inner files; keeping full block: {blockListPath}. {e.GetType().Name}: {e.Message}");
                return Array.Empty<EndfieldVfsBlockFile.UnityBundleEntry>();
            }
        }

        private static string GetEndfieldVfsBucketKey(string path)
        {
            var normalized = Path.GetFullPath(path).Replace('\\', '/');
            var marker = "/VFS/";
            var markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                return string.Empty;
            }

            var relative = normalized[(markerIndex + marker.Length)..].Trim('/');
            var slash = relative.IndexOf('/');
            return slash > 0 ? relative[..slash] : Path.GetFileNameWithoutExtension(relative);
        }

        private static bool IsPersistentEndfieldVfsPath(string path)
        {
            var normalized = Path.GetFullPath(path).Replace('\\', '/');
            return normalized.Contains("/Persistent/VFS/", StringComparison.OrdinalIgnoreCase);
        }

        private static double GetEndfieldVfsReadableUnityBundleChunkRatio(string path, Game game)
        {
            try
            {
                var entries = EndfieldVfsBlockFile.ListFileEntries(path, game.Type)
                    .Where(IsEndfieldUnityBundleEntry)
                    .ToArray();
                if (entries.Length == 0)
                {
                    return 0.0;
                }

                var blockDir = Path.GetDirectoryName(path) ?? string.Empty;
                var chunkFiles = entries
                    .Select(x => x.ChunkFileName)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (chunkFiles.Length == 0)
                {
                    return 0.0;
                }

                var readableChunks = chunkFiles.Count(chunk => File.Exists(Path.Combine(blockDir, chunk)));
                return readableChunks / (double)chunkFiles.Length;
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is InvalidCastException || e is ArgumentException)
            {
                Logger.Warning($"Unable to check Endfield VFS chunk closure; treating as unreadable: {path}. {e.GetType().Name}: {e.Message}");
                return 0.0;
            }
        }

        private static bool IsEndfieldUnityBundleEntry(EndfieldVfsBlockFile.VfsFileEntry entry)
        {
            // EndfieldVfsBlockFile 的内部枚举值：InitBundle=2, Bundle=11。
            return entry != null && (entry.BlockTypeId == 2 || entry.BlockTypeId == 11);
        }

        private static bool AreSameFileContent(string left, string right)
        {
            if (string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var leftInfo = new FileInfo(left);
            var rightInfo = new FileInfo(right);
            if (!leftInfo.Exists || !rightInfo.Exists || leftInfo.Length != rightInfo.Length)
            {
                return false;
            }

            return string.Equals(ComputeFileSha256(left), ComputeFileSha256(right), StringComparison.OrdinalIgnoreCase);
        }

        private static string ComputeFileSha256(string path)
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream));
        }

        private static void AddEndfieldVfsInnerBatches(
            List<SourceIndexLoadBatch> batches,
            string blockListPath,
            Game game,
            int batchFiles,
            string[] selectedInnerFiles = null,
            Func<string, bool> additionalInnerFileFilter = null,
            bool includeAllInnerFiles = false)
        {
            IReadOnlyList<EndfieldVfsBlockFile.UnityBundleEntry> entries;
            try
            {
                entries = EndfieldVfsBlockFile.ListUnityBundleFiles(blockListPath, game.Type);
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is InvalidCastException || e is ArgumentException)
            {
                Logger.Warning($"Unable to enumerate Endfield VFS inner files for batching. Falling back to normal load: {blockListPath}. {e.GetType().Name}: {e.Message}");
                batches.Add(SourceIndexLoadBatch.ForFiles(new[] { blockListPath }));
                return;
            }

            if (entries.Count == 0)
            {
                Logger.Verbose($"Skipped Endfield VFS block without Unity bundle entries during source-index batching: {blockListPath}");
                return;
            }

            if (!includeAllInnerFiles && (selectedInnerFiles != null || additionalInnerFileFilter != null))
            {
                var selected = (selectedInnerFiles ?? Array.Empty<string>())
                    .Select(NormalizeEndfieldVfsInnerFileName)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                entries = entries
                    .Where(x =>
                    {
                        var normalized = NormalizeEndfieldVfsInnerFileName(x.FileName);
                        return selected.Contains(normalized)
                            || (additionalInnerFileFilter != null && additionalInnerFileFilter(x.FileName));
                    })
                    .ToArray();
                if (entries.Count == 0)
                {
                    Logger.Verbose($"Skipped Endfield VFS block because supplemental inner-file selection is empty: {blockListPath}");
                    return;
                }
            }

            var maxFiles = Math.Max(EndfieldVfsInnerBatchMinFileCount, Math.Max(1, batchFiles) * 8);
            var current = new List<string>(maxFiles);
            var currentBytes = 0L;

            void Flush()
            {
                if (current.Count == 0)
                {
                    return;
                }

                batches.Add(SourceIndexLoadBatch.ForEndfieldVfsInnerFiles(
                    blockListPath,
                    current.ToArray(),
                    currentBytes));
                current.Clear();
                currentBytes = 0;
            }

            foreach (var entry in entries.OrderBy(x => x.Length).ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase))
            {
                if (current.Count > 0
                    && (current.Count >= maxFiles || currentBytes + Math.Max(0, entry.Length) > EndfieldVfsInnerBatchBytes))
                {
                    Flush();
                }

                current.Add(entry.FileName);
                currentBytes += Math.Max(0, entry.Length);
            }

            Flush();
        }

        private static Func<string, bool> BuildExactEndfieldVfsInnerFileFilter(IEnumerable<string> innerFiles)
        {
            var selected = innerFiles
                .Select(NormalizeEndfieldVfsInnerFileName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selectedByFileName = selected
                .GroupBy(GetNormalizedFileName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(path => "/" + path).ToArray(),
                    StringComparer.OrdinalIgnoreCase);

            return fileName =>
            {
                var normalized = NormalizeEndfieldVfsInnerFileName(fileName);
                if (selected.Contains(normalized))
                {
                    return true;
                }

                // 这里每个批次会扫完整 VFS 元数据。不能对每个条目遍历全部 selected，
                // 先按文件名缩小候选，再保留旧的后缀兼容。
                var leaf = GetNormalizedFileName(normalized);
                return selectedByFileName.TryGetValue(leaf, out var suffixes)
                    && suffixes.Any(suffix => normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            };
        }

        private static string NormalizeEndfieldVfsInnerFileName(string fileName)
        {
            return (fileName ?? string.Empty)
                .Trim()
                .TrimStart('/', '\\')
                .Replace('\\', '/');
        }

        private static string GetNormalizedFileName(string normalizedPath)
        {
            normalizedPath ??= string.Empty;
            var index = normalizedPath.LastIndexOf('/');
            return index >= 0 ? normalizedPath[(index + 1)..] : normalizedPath;
        }

        private sealed class SourceIndexLoadBatch
        {
            public string[] Files { get; private set; }
            public string[] EndfieldVfsInnerFiles { get; private set; }
            public long EndfieldVfsInnerBytes { get; private set; }
            public bool IsEndfieldVfsInnerBatch => EndfieldVfsInnerFiles != null && EndfieldVfsInnerFiles.Length > 0;

            public static SourceIndexLoadBatch ForFiles(string[] files)
            {
                return new SourceIndexLoadBatch
                {
                    Files = files ?? Array.Empty<string>(),
                };
            }

            public static SourceIndexLoadBatch ForEndfieldVfsInnerFiles(string blockListPath, string[] innerFiles, long innerBytes)
            {
                return new SourceIndexLoadBatch
                {
                    Files = new[] { blockListPath },
                    EndfieldVfsInnerFiles = innerFiles ?? Array.Empty<string>(),
                    EndfieldVfsInnerBytes = innerBytes,
                };
            }
        }

        private sealed class EndfieldVfsSourceSelection
        {
            public EndfieldVfsSourceSelection(
                string[] selectedFiles,
                int skippedDuplicateCount,
                int differentNamedPairCount,
                int sameNamedDifferentLengthInnerFileCount,
                int sameNamedSameLengthInnerFileCount,
                IReadOnlyDictionary<string, string[]> innerFileFilters = null)
            {
                SelectedFiles = selectedFiles ?? Array.Empty<string>();
                SkippedDuplicateCount = skippedDuplicateCount;
                DifferentNamedPairCount = differentNamedPairCount;
                SameNamedDifferentLengthInnerFileCount = sameNamedDifferentLengthInnerFileCount;
                SameNamedSameLengthInnerFileCount = sameNamedSameLengthInnerFileCount;
                InnerFileFilters = innerFileFilters ?? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                SupplementalInnerFileCount = InnerFileFilters.Values.Sum(x => x?.Length ?? 0);
            }

            public static EndfieldVfsSourceSelection Empty { get; } = new(Array.Empty<string>(), 0, 0, 0, 0);

            public string[] SelectedFiles { get; }
            public int SkippedDuplicateCount { get; }
            public int DifferentNamedPairCount { get; }
            public int SameNamedDifferentLengthInnerFileCount { get; }
            public int SameNamedSameLengthInnerFileCount { get; }
            public IReadOnlyDictionary<string, string[]> InnerFileFilters { get; }
            public int SupplementalInnerFileCount { get; }

            public static EndfieldVfsSourceSelection FromSelected(string[] selectedFiles)
            {
                return new EndfieldVfsSourceSelection(selectedFiles ?? Array.Empty<string>(), 0, 0, 0, 0);
            }
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
                or ClassIDType.LODGroup
                or ClassIDType.MeshFilter
                or ClassIDType.AnimationClip
                or ClassIDType.Avatar
                or ClassIDType.MonoBehaviour
                or ClassIDType.MonoScript
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
                ValidateNodeIndex(nodeList, index, parentPath);
                var node = nodeList[index];
                var path = string.IsNullOrEmpty(parentPath) ? node.m_Name : $"{parentPath}.{node.m_Name}";
                var align = (node.m_MetaFlag & 0x4000) != 0;
                object returnValue = null;

                if (node.m_Type != null && node.m_Type.StartsWith("PPtr<", StringComparison.Ordinal))
                {
                    var ptr = new PPtr<AnimeStudio.Object>(reader);
                    CapturePPtr(path, ptr.m_FileID, ptr.m_PathID);
                    index = GetNodeEnd(nodeList, index, path) - 1;
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
                        index = GetNodeEnd(nodeList, index, path) - 1;
                        break;
                    case "TypelessData":
                        {
                            var size = binaryReader.ReadInt32();
                            if (size < 0 || size > reader.BytesLeft())
                            {
                                throw new InvalidDataException($"Invalid TypelessData size {size} while reading lightweight renderer relation.");
                            }
                            binaryReader.ReadBytes(size);
                            index = GetNodeEnd(nodeList, index, path) - 1;
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
                            var end = GetNodeEnd(nodeList, index, path);
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
                ValidateNodeIndex(nodeList, index, path);
                ValidateNodeIndex(nodeList, index + 1, path);
                // Unity TypeTree 的 vector 通常是: vector -> Array -> size -> data。
                // Endfield 个别对象的轻量 TypeTree 不完整时，不能让单个对象打断全量源索引。
                var arrayAlign = (nodeList[index + 1].m_MetaFlag & 0x4000) != 0;
                var vector = GetNodes(nodeList, index, path);
                if (vector.Count <= 3)
                {
                    throw new InvalidDataException($"Invalid lightweight renderer array typetree at {path}: expected vector data node, got {vector.Count} node(s).");
                }
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
                ValidateNodeIndex(nodeList, index, path);
                // Unity map TypeTree 里 key/value 子树位置比较固定，但不同游戏版本可能裁掉空结构。
                // 这里显式报坏结构，交给外层记录 lightweightRendererFailures。
                var map = GetNodes(nodeList, index, path);
                if (map.Count <= 4)
                {
                    throw new InvalidDataException($"Invalid lightweight renderer map typetree at {path}: expected key/value nodes, got {map.Count} node(s).");
                }
                index += map.Count - 1;
                var first = GetNodes(map, 4, path + ".key");
                var next = 4 + first.Count;
                if (next >= map.Count)
                {
                    throw new InvalidDataException($"Invalid lightweight renderer map typetree at {path}: missing value node after key subtree.");
                }
                var second = GetNodes(map, next, path + ".value");
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

                if (reader.type == ClassIDType.MonoBehaviour)
                {
                    // 脚本字段只作为确定性原始引用入库，不直接升级成模型-动画候选。
                    // m_GameObject / m_Script 已由解析后的 MonoBehaviour 明确写入，避免重复。
                    if (!EndsWithField(path, "m_GameObject") && !EndsWithField(path, "m_Script"))
                    {
                        relations.Add(new LightweightPPtrRelation
                        {
                            Relation = "monoBehaviour.pptr",
                            FileId = fileId,
                            PathId = pathId,
                            TypeHint = "Object",
                            Details = new
                            {
                                path,
                                field = GetLastFieldName(path),
                            },
                        });
                    }
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

            private static List<TypeTreeNode> GetNodes(List<TypeTreeNode> nodeList, int index, string path)
            {
                var end = GetNodeEnd(nodeList, index, path);
                var count = end - index;
                if (count <= 0)
                {
                    throw new InvalidDataException($"Invalid lightweight renderer typetree range at {path}: index={index}, end={end}, count={nodeList?.Count ?? 0}.");
                }
                return nodeList.GetRange(index, count);
            }

            private static int GetNodeEnd(List<TypeTreeNode> nodeList, int index, string path)
            {
                ValidateNodeIndex(nodeList, index, path);
                var level = nodeList[index].m_Level;
                var end = index + 1;
                while (end < nodeList.Count && nodeList[end].m_Level > level)
                {
                    end++;
                }
                return end;
            }

            private static void ValidateNodeIndex(List<TypeTreeNode> nodeList, int index, string path)
            {
                if (nodeList == null || nodeList.Count == 0)
                {
                    throw new InvalidDataException($"Invalid lightweight renderer typetree at {path}: node list is empty.");
                }

                if (index < 0 || index >= nodeList.Count)
                {
                    throw new InvalidDataException($"Invalid lightweight renderer typetree index at {path}: index={index}, count={nodeList.Count}.");
                }
            }

            private static string GetLastFieldName(string path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return null;
                }

                var index = path.LastIndexOf('.');
                return index >= 0 && index < path.Length - 1 ? path[(index + 1)..] : path;
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

            if (IsLikelyEndfieldVfsFile(path, game))
            {
                return true;
            }

            var extension = Path.GetExtension(path);
            if (string.IsNullOrEmpty(extension))
            {
                if (IsLikelyNarakaBundleFile(path, game))
                {
                    return true;
                }

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
                    return IsLikelyNarakaBundleFile(path, game) || HasUnityBundleHeader(path);
            }
        }

        private static bool IsLikelyNarakaBundleFile(string path, Game game)
        {
            if (game == null || !game.Type.IsNaraka())
            {
                return false;
            }

            try
            {
                // 永劫无间把 UnityFS 文件头改成这个 7 字节标记。
                // 后续 BundleFile 会按 Naraka profile 还原签名和块信息。
                using var stream = File.OpenRead(path);
                if (stream.Length < 7)
                {
                    return false;
                }

                Span<byte> buffer = stackalloc byte[7];
                var read = stream.Read(buffer);
                return read == 7
                    && buffer[0] == 0x15
                    && buffer[1] == 0x1E
                    && buffer[2] == 0x1C
                    && buffer[3] == 0x0D
                    && buffer[4] == 0x0D
                    && buffer[5] == 0x23
                    && buffer[6] == 0x21;
            }
            catch
            {
                return false;
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

        private static bool IsLikelyEndfieldVfsFile(string path, Game game)
        {
            if (game == null || !game.Type.IsArknightsEndfieldGroup())
            {
                return false;
            }

            var extension = Path.GetExtension(path);
            if (!string.Equals(extension, ".blc", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var normalized = Path.GetFullPath(path).Replace('\\', '/');
            return normalized.Contains("/StreamingAssets/VFS/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/Persistent/VFS/", StringComparison.OrdinalIgnoreCase);
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

        private sealed class NarakaInputProbe
        {
            public int SourceFileCount { get; set; }
            public int LoadableFileCount { get; set; }
            public int PakFileCount { get; set; }
            public int UnityFsHeaderFileCount { get; set; }
            public int NarakaHeaderFileCount { get; set; }
            public int LoadableNarakaHeaderFileCount { get; set; }
            public int TrailingDataHeaderFileCount { get; set; }
            public Dictionary<string, int> HeaderSizeOffsetCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<string> PakSamples { get; } = new();
            public List<string> NarakaHeaderSamples { get; } = new();

            public JObject ToJson()
            {
                return new JObject
                {
                    ["sourceFileCount"] = SourceFileCount,
                    ["loadableFileCount"] = LoadableFileCount,
                    ["pakFileCount"] = PakFileCount,
                    ["unityFsHeaderFileCount"] = UnityFsHeaderFileCount,
                    ["narakaHeaderFileCount"] = NarakaHeaderFileCount,
                    ["loadableNarakaHeaderFileCount"] = LoadableNarakaHeaderFileCount,
                    ["trailingDataHeaderFileCount"] = TrailingDataHeaderFileCount,
                    ["headerSizeOffsetCounts"] = JObject.FromObject(HeaderSizeOffsetCounts),
                    ["pakSamples"] = new JArray(PakSamples),
                    ["narakaHeaderSamples"] = new JArray(NarakaHeaderSamples),
                    ["rule"] = "只记录永劫输入形态，不执行 .pak 解包，也不把社区 AES key 写入默认 Unity 素材库流程。",
                };
            }
        }

        private readonly struct NarakaBundleHeaderProbe
        {
            public bool IsUnityFs { get; init; }
            public bool IsNarakaHeader { get; init; }
            public long SizeOffset { get; init; }
        }
    }
}
