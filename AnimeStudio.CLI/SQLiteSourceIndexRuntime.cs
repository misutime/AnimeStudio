using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;

namespace AnimeStudio.CLI
{
    internal static class SQLiteSourceIndexRuntime
    {
        public static LoadResult CurrentLoadResult { get; private set; }

        public sealed class LoadResult
        {
            public string DatabasePath { get; init; }
            public string SourceRoot { get; init; }
            public string Game { get; init; }
            public int SourceFileCount { get; init; }
            public int SerializedFileCount { get; init; }
            public int DependencyEntryCount { get; init; }
            public int RelationCount { get; init; }
            public int AnimationBindingCount { get; init; }
            public int FailedBatches { get; init; }
        }

        public static string ResolveIndexPath(string explicitIndexPath, string inputRoot, string outputRoot)
        {
            if (!string.IsNullOrWhiteSpace(explicitIndexPath))
            {
                return Path.GetFullPath(explicitIndexPath);
            }

            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(outputRoot))
            {
                candidates.Add(Path.Combine(Path.GetFullPath(outputRoot), "unity_source_index.db"));
            }
            if (!string.IsNullOrWhiteSpace(inputRoot))
            {
                candidates.Add(Path.Combine(Path.GetFullPath(inputRoot), "unity_source_index.db"));
            }

            return candidates.FirstOrDefault(File.Exists);
        }

        public static LoadResult LoadIntoAssetsHelper(string dbPath, string expectedInputRoot, string expectedGame)
        {
            if (string.IsNullOrWhiteSpace(dbPath))
            {
                CurrentLoadResult = null;
                return null;
            }

            dbPath = Path.GetFullPath(dbPath);
            if (!File.Exists(dbPath))
            {
                throw new FileNotFoundException($"SQLite source index not found: {dbPath}", dbPath);
            }

            SQLitePCL.Batteries_V2.Init();
            using var profile = ProfileLogger.Measure("source_index_runtime_load", new Dictionary<string, object>
            {
                ["sourceIndex"] = dbPath,
            });

            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();

            var metadata = ReadMetadata(connection);
            var kind = metadata.TryGetValue("kind", out var kindValue) ? kindValue : string.Empty;
            if (!string.Equals(kind, "unity_source_index", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"SQLite database is not a unity_source_index: {dbPath}");
            }

            var sourceRoot = metadata.TryGetValue("sourceRoot", out var rootValue)
                ? Path.GetFullPath(rootValue)
                : string.Empty;
            var game = metadata.TryGetValue("game", out var gameValue) ? gameValue : string.Empty;

            if (!string.IsNullOrWhiteSpace(expectedInputRoot))
            {
                var expectedRoot = Directory.Exists(expectedInputRoot)
                    ? Path.GetFullPath(expectedInputRoot)
                    : Path.GetDirectoryName(Path.GetFullPath(expectedInputRoot));
                if (!string.Equals(NormalizeRoot(sourceRoot), NormalizeRoot(expectedRoot), StringComparison.OrdinalIgnoreCase)
                    && !IsEndfieldVfsLayerRootMatch(sourceRoot, expectedRoot, expectedGame))
                {
                    throw new InvalidDataException($"SQLite source index root does not match input. index={sourceRoot}, input={expectedRoot}");
                }
            }

            if (!string.IsNullOrWhiteSpace(expectedGame) && !string.Equals(game, expectedGame, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Warning($"SQLite source index game '{game}' differs from requested game '{expectedGame}'. Continuing because --game may be a compatible alias.");
            }

            var entries = LoadDependencyEntries(connection);
            var sourceFileCount = CountRows(connection, "source_files");
            var serializedFileCount = CountRows(connection, "serialized_files");
            var relationCount = CountRows(connection, "source_relations");
            var animationBindingCount = CountRows(connection, "source_animation_bindings");
            var failedBatches = ReadFailedBatchCount(dbPath);

            if (failedBatches > 0)
            {
                throw new InvalidDataException($"SQLite source index has failed batches: {failedBatches}. Rebuild the index before full Library export.");
            }

            AssetsHelper.LoadCABMapEntries(sourceRoot, entries, sourceFileCount);
            Logger.Info($"Using SQLite source index: {dbPath}");
            Logger.Info($"SQLite source index stats: sourceFiles={sourceFileCount}, serializedFiles={serializedFileCount}, relations={relationCount}, animationBindings={animationBindingCount}");

            CurrentLoadResult = new LoadResult
            {
                DatabasePath = dbPath,
                SourceRoot = sourceRoot,
                Game = game,
                SourceFileCount = sourceFileCount,
                SerializedFileCount = serializedFileCount,
                DependencyEntryCount = entries.Count,
                RelationCount = relationCount,
                AnimationBindingCount = animationBindingCount,
                FailedBatches = failedBatches,
            };
            return CurrentLoadResult;
        }

        private static bool IsEndfieldVfsLayerRootMatch(string indexRoot, string inputRoot, string expectedGame)
        {
            if (string.IsNullOrWhiteSpace(expectedGame)
                || !expectedGame.Contains("ArknightsEndfield", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var normalizedIndex = NormalizeRoot(indexRoot);
            var normalizedInput = NormalizeRoot(inputRoot);

            // 新版 Endfield 源索引会把 sourceRoot 提升到 Endfield_Data，
            // 但旧导出命令常传 StreamingAssets/VFS 或 Persistent/VFS。
            // 这里仅允许这组三层目录互相兼容，不放宽普通 Unity 游戏的 root 校验。
            return IsEndfieldDataRootForVfsLayer(normalizedIndex, normalizedInput)
                || IsEndfieldDataRootForVfsLayer(normalizedInput, normalizedIndex);
        }

        private static bool IsEndfieldDataRootForVfsLayer(string possibleDataRoot, string possibleVfsRoot)
        {
            if (string.IsNullOrWhiteSpace(possibleDataRoot) || string.IsNullOrWhiteSpace(possibleVfsRoot))
            {
                return false;
            }

            var streaming = NormalizeRoot(Path.Combine(possibleDataRoot, "StreamingAssets", "VFS"));
            var persistent = NormalizeRoot(Path.Combine(possibleDataRoot, "Persistent", "VFS"));
            return string.Equals(possibleVfsRoot, streaming, StringComparison.OrdinalIgnoreCase)
                || string.Equals(possibleVfsRoot, persistent, StringComparison.OrdinalIgnoreCase);
        }

        public static void WriteUsageReport(string outputRoot, LoadResult result)
        {
            if (result == null || string.IsNullOrWhiteSpace(outputRoot))
            {
                return;
            }

            var report = new JObject
            {
                ["sourceIndex"] = result.DatabasePath,
                ["sourceRoot"] = result.SourceRoot,
                ["game"] = result.Game,
                ["sourceFileCount"] = result.SourceFileCount,
                ["serializedFileCount"] = result.SerializedFileCount,
                ["dependencyEntryCount"] = result.DependencyEntryCount,
                ["relationCount"] = result.RelationCount,
                ["animationBindingCount"] = result.AnimationBindingCount,
                ["failedBatches"] = result.FailedBatches,
                ["rule"] = "SQLite source index is required for Library exports. Legacy CAB/Asset maps are explicit compatibility tools only.",
            };
            Directory.CreateDirectory(outputRoot);
            File.WriteAllText(Path.Combine(outputRoot, "source_index_usage.json"), report.ToString(Newtonsoft.Json.Formatting.Indented));
        }

        private static Dictionary<string, string> ReadMetadata(SqliteConnection connection)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT key, value FROM metadata;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result[reader.GetString(0)] = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            }
            return result;
        }

        private static List<KeyValuePair<string, AssetsHelper.Entry>> LoadDependencyEntries(SqliteConnection connection)
        {
            var entries = new Dictionary<string, AssetsHelper.Entry>(StringComparer.OrdinalIgnoreCase);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT file_name, source_path, offset
FROM serialized_files
WHERE file_name IS NOT NULL AND file_name <> ''
ORDER BY id;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var fileName = reader.GetString(0);
                    if (entries.ContainsKey(fileName))
                    {
                        continue;
                    }

                    entries[fileName] = new AssetsHelper.Entry
                    {
                        Path = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                        Offset = reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                        Dependencies = new List<string>(),
                    };
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT serialized_file, file_name
FROM source_externals
WHERE serialized_file IS NOT NULL AND serialized_file <> ''
  AND file_name IS NOT NULL AND file_name <> ''
ORDER BY id;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var serializedFile = reader.GetString(0);
                    var dependency = reader.GetString(1);
                    if (entries.TryGetValue(serializedFile, out var entry)
                        && !entry.Dependencies.Contains(dependency, StringComparer.OrdinalIgnoreCase))
                    {
                        entry.Dependencies.Add(dependency);
                    }
                }
            }

            return entries.Select(x => new KeyValuePair<string, AssetsHelper.Entry>(x.Key, x.Value)).ToList();
        }

        private static int CountRows(SqliteConnection connection, string table)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table};";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private static int ReadFailedBatchCount(string dbPath)
        {
            var summaryPath = Path.Combine(Path.GetDirectoryName(dbPath) ?? string.Empty, "unity_source_index_summary.json");
            if (!File.Exists(summaryPath))
            {
                return 0;
            }

            try
            {
                var json = JObject.Parse(File.ReadAllText(summaryPath));
                return (int?)json["counts"]?["failedBatches"] ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private static string NormalizeRoot(string path)
        {
            return (path ?? string.Empty)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
