using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AnimeStudio.LibraryBrowser
{
    internal sealed class LibraryAnimationIndex
    {
        private const int MaxTargetedCandidateCount = 256;
        private const int LargeSqliteCandidateLazyThreshold = 200_000;
        private static readonly HashSet<string> TargetedSemanticStopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "anim",
            "animation",
            "animations",
            "asset",
            "assets",
            "gltf",
            "hyb",
            "lod00",
            "model",
            "models",
            "prefab",
            "rig",
            "root",
            "root_jnt",
            "standard",
        };

        private readonly Dictionary<string, List<LibraryAnimationCandidate>> _byModelOutput;
        private readonly Dictionary<string, LibraryAnimationUsage> _byAnimationKey;
        private readonly Dictionary<string, int> _sqliteCandidateCountByModelOutput;
        private readonly Dictionary<string, int> _sqliteModelCountByAnimationKey;
        private readonly Dictionary<string, IReadOnlyList<string>> _sqliteAnimationModelOutputCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<LibraryAnimationCandidate>> _targetedCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _root;
        private readonly string _sqliteDbPath;
        private readonly LibraryBrowserSettings _settings;
        private readonly bool _lazySqlite;
        private readonly object _cacheLock = new();

        private LibraryAnimationIndex(
            string root,
            Dictionary<string, List<LibraryAnimationCandidate>> byModelOutput,
            IEnumerable<LibraryAnimationCandidate> allAnimations = null,
            string loadSource = "",
            int indexedCandidateCount = 0,
            Dictionary<string, int> sqliteCandidateCountByModelOutput = null,
            Dictionary<string, int> sqliteModelCountByAnimationKey = null,
            string sqliteDbPath = null,
            LibraryBrowserSettings settings = null,
            bool lazySqlite = false)
        {
            _root = root;
            _byModelOutput = byModelOutput;
            _sqliteCandidateCountByModelOutput = sqliteCandidateCountByModelOutput;
            _sqliteModelCountByAnimationKey = sqliteModelCountByAnimationKey;
            _sqliteDbPath = sqliteDbPath;
            _settings = settings;
            _lazySqlite = lazySqlite;
            _byAnimationKey = BuildAnimationUsageMap(byModelOutput, allAnimations, sqliteModelCountByAnimationKey);
            LoadSource = loadSource;
            IndexedModelCount = _lazySqlite && _sqliteCandidateCountByModelOutput != null
                ? _sqliteCandidateCountByModelOutput.Count
                : byModelOutput.Count;
            IndexedCandidateCount = _lazySqlite && _sqliteCandidateCountByModelOutput != null
                ? _sqliteCandidateCountByModelOutput.Values.Sum()
                : indexedCandidateCount > 0
                ? Math.Min(indexedCandidateCount, byModelOutput.Values.Sum(x => x.Count(y => y.IsUsableCandidate)))
                : byModelOutput.Values.Sum(x => x.Count(y => y.IsUsableCandidate));
        }

        public static LibraryAnimationIndex Empty { get; } = new("", new Dictionary<string, List<LibraryAnimationCandidate>>(StringComparer.OrdinalIgnoreCase));
        public string LoadSource { get; }
        public int IndexedModelCount { get; }
        public int IndexedCandidateCount { get; }

        public IReadOnlyList<LibraryAnimationCandidate> FindForModel(LibraryModelItem model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.OutputPath))
            {
                return Array.Empty<LibraryAnimationCandidate>();
            }

            var key = NormalizePath(model.OutputPath);
            if (_byModelOutput.TryGetValue(key, out var candidates))
            {
                return candidates;
            }

            if (!_lazySqlite)
            {
                return Array.Empty<LibraryAnimationCandidate>();
            }

            lock (_cacheLock)
            {
                if (_byModelOutput.TryGetValue(key, out candidates))
                {
                    return candidates;
                }
            }

            candidates = LoadSqliteCandidatesForModel(model).ToList();
            lock (_cacheLock)
            {
                _byModelOutput[key] = candidates;
            }
            return candidates;
        }

        public int CountForModel(LibraryModelItem model)
        {
            return CountUsableForModel(model);
        }

        public int CountAllForModel(LibraryModelItem model)
        {
            if (_lazySqlite
                && model != null
                && _sqliteCandidateCountByModelOutput != null
                && _sqliteCandidateCountByModelOutput.TryGetValue(NormalizePath(model.OutputPath), out var count))
            {
                return count;
            }

            return FindForModel(model).Count;
        }

        public int CountUsableForModel(LibraryModelItem model)
        {
            if (_lazySqlite)
            {
                // 大库列表只需要一个稳定的显式候选数量，具体可用性在打开模型详情时再按候选精确判断。
                return CountAllForModel(model);
            }

            return FindForModel(model).Count(x => x.IsUsableCandidate);
        }

        public int CountExplicitForModel(LibraryModelItem model)
        {
            if (_lazySqlite)
            {
                return CountAllForModel(model);
            }

            return FindForModel(model).Count(x => x.IsExplicit);
        }

        public IReadOnlyList<LibraryAnimationUsage> FindAllAnimations()
        {
            return _byAnimationKey.Values
                .OrderBy(x => x.Animation.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Animation.BestPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public IReadOnlyList<string> FindModelOutputsForAnimation(LibraryAnimationUsage usage)
        {
            if (usage == null)
            {
                return Array.Empty<string>();
            }

            if (!_lazySqlite)
            {
                return usage.ModelOutputs;
            }

            var key = usage.Key;
            if (string.IsNullOrWhiteSpace(key))
            {
                return Array.Empty<string>();
            }

            lock (_cacheLock)
            {
                if (_sqliteAnimationModelOutputCache.TryGetValue(key, out var cached))
                {
                    return cached;
                }
            }

            var outputs = LoadSqliteModelOutputsForAnimation(usage).ToArray();
            lock (_cacheLock)
            {
                _sqliteAnimationModelOutputCache[key] = outputs;
            }
            return outputs;
        }

        public IReadOnlyList<LibraryAnimationCandidate> FindTargetedForModel(LibraryModelItem model)
        {
            if (model == null || string.IsNullOrWhiteSpace(_root))
            {
                return Array.Empty<LibraryAnimationCandidate>();
            }

            lock (_cacheLock)
            {
                if (_targetedCache.TryGetValue(model.StableKey, out var cached))
                {
                    return cached;
                }
            }

            var result = FindTargetedForModelCore(model);
            lock (_cacheLock)
            {
                _targetedCache[model.StableKey] = result;
            }
            return result;
        }

        public static LibraryAnimationIndex Load(string root)
        {
            var sqlite = LoadSqlite(root);
            if (sqlite != null && sqlite.FindAllAnimations().Count > 0)
            {
                return sqlite;
            }

            var compactPath = Path.Combine(root, "model_animations.compact.json");
            if (File.Exists(compactPath))
            {
                return LoadCompact(compactPath);
            }

            var verbosePath = Path.Combine(root, "model_animations.json");
            if (File.Exists(verbosePath))
            {
                return LoadVerbose(verbosePath);
            }

            return Empty;
        }

        private static LibraryAnimationIndex LoadSqlite(string root)
        {
            var dbPath = Path.Combine(root, "library_index.db");
            if (!File.Exists(dbPath))
            {
                return null;
            }

            try
            {
                SQLitePCL.Batteries_V2.Init();
                using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                connection.Open();
                var settings = LibraryBrowserSettings.LoadEffective(root);
                var result = new Dictionary<string, List<LibraryAnimationCandidate>>(StringComparer.OrdinalIgnoreCase);
                if (HasTable(connection, "model_animation_candidates"))
                {
                    var candidateCount = CountExplicitSqliteCandidates(connection);
                    if (candidateCount > LargeSqliteCandidateLazyThreshold)
                    {
                        var counts = LoadSqliteCandidateCounts(root, connection);
                        var animationCounts = LoadSqliteAnimationModelCounts(root, connection);
                        return new LibraryAnimationIndex(
                            root,
                            result,
                            LoadAllAnimationsFromSqlite(root, connection),
                            "SQLite-lazy",
                            (int)Math.Min(candidateCount, int.MaxValue),
                            counts,
                            animationCounts,
                            dbPath,
                            settings,
                            lazySqlite: true);
                    }

                    using var command = connection.CreateCommand();
                    command.CommandText = @"
SELECT c.model_output, c.raw_json, a.raw_json, m.raw_json
FROM model_animation_candidates c
JOIN assets m ON m.kind = 'Model' AND m.output = c.model_output
JOIN assets a ON a.kind = 'Animation' AND a.output = c.animation_output
WHERE c.relation_source = 'explicit'
ORDER BY c.model_output, c.score DESC;";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var modelOutput = LibraryPathResolver.ResolveExistingFile(root, reader.GetString(0));
                        using var candidateDocument = JsonDocument.Parse(reader.GetString(1));
                        using var animationDocument = JsonDocument.Parse(reader.GetString(2));
                        using var modelDocument = JsonDocument.Parse(reader.GetString(3));
                        var candidate = candidateDocument.RootElement;
                        var animation = animationDocument.RootElement;
                        var model = modelDocument.RootElement;
                        var importedAvatarAsset = ResolveImportedAvatarAsset(settings, model, modelOutput);
                        if (!result.TryGetValue(NormalizePath(modelOutput), out var list))
                        {
                            list = new List<LibraryAnimationCandidate>();
                            result[NormalizePath(modelOutput)] = list;
                        }

                        var merged = MergeCandidate(root, animation, candidate, model, importedAvatarAsset);
                        if (merged.IsExplicit)
                        {
                            list.Add(merged);
                        }
                    }
                }
                else if (HasTable(connection, "model_animation_relations") && HasTable(connection, "relation_animations"))
                {
                    var hasStructuredRelationAnimationColumns =
                        HasColumn(connection, "relation_animations", "is_usable_candidate")
                        && HasColumn(connection, "relation_animations", "track_count")
                        && HasColumn(connection, "relation_animations", "relation_source");
                    using var command = connection.CreateCommand();
                    command.CommandText = hasStructuredRelationAnimationColumns
                        ? @"
SELECT mar.model, mar.confidence, ra.raw_json,
       ra.output, ra.status, ra.duration, ra.frame_count, ra.track_count,
       ra.relation_source, ra.validation_status, ra.validation_category,
       ra.validation_reason, ra.track_coverage, ra.is_usable_candidate,
       ra.segment_count
FROM model_animation_relations mar
JOIN relation_animations ra ON ra.relation_id = mar.id
-- 旧表也只读取确定性显式关系；fallback/diagnostic 不能进入默认动画列表。
WHERE LOWER(COALESCE(ra.relation_source, '')) IN ('explicit', 'componentowner', 'componentownerblendspacesample', 'componentanimclass')
   OR mar.confidence = 'explicit_unity_reference'
ORDER BY mar.model, ra.name;"
                        : @"
SELECT mar.model, mar.confidence, ra.raw_json
FROM model_animation_relations mar
JOIN relation_animations ra ON ra.relation_id = mar.id
ORDER BY mar.model, ra.name;";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var modelOutput = LibraryPathResolver.ResolveExistingFile(root, reader.GetString(0));
                        var confidence = reader.IsDBNull(1) ? "" : reader.GetString(1);
                        var rawJson = hasStructuredRelationAnimationColumns
                            ? PatchUnrealRelationAnimationJson(reader)
                            : reader.GetString(2);
                        using var animationDocument = JsonDocument.Parse(rawJson);
                        var candidate = ReadUnrealRelationAnimation(root, confidence, animationDocument.RootElement);
                        if (candidate.IsExplicit)
                        {
                            AddCandidate(result, modelOutput, candidate);
                        }
                    }
                }

                foreach (var key in result.Keys.ToArray())
                {
                    result[key] = SortCandidates(result[key]);
                }

                return new LibraryAnimationIndex(
                    root,
                    result,
                    LoadAllAnimationsFromSqlite(root, connection),
                    "SQLite",
                    result.Values.Sum(x => x.Count));
            }
            catch (SqliteException ex)
            {
                WriteSqliteLoadError(root, ex.ToString());
                return null;
            }
            catch (JsonException ex)
            {
                WriteSqliteLoadError(root, ex.ToString());
                return null;
            }
            catch (Exception ex) when (!System.Diagnostics.Debugger.IsAttached)
            {
                WriteSqliteLoadError(root, ex.ToString());
                return null;
            }
        }

        private static void WriteSqliteLoadError(string root, string message)
        {
            try
            {
                var cache = Path.Combine(root, ".as_browser_cache");
                Directory.CreateDirectory(cache);
                File.WriteAllText(
                    Path.Combine(cache, "animation_index_sqlite_error.txt"),
                    $"{DateTime.UtcNow:O}{Environment.NewLine}{message}");
            }
            catch
            {
                // 诊断文件写失败不能影响 Browser 打开素材库；UI 仍会回退旧索引。
            }
        }

        private static long CountExplicitSqliteCandidates(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT COUNT(*)
FROM model_animation_candidates
WHERE relation_source='explicit';";
            return Convert.ToInt64(command.ExecuteScalar());
        }

        private static Dictionary<string, int> LoadSqliteCandidateCounts(string root, SqliteConnection connection)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT model_output, COUNT(*)
FROM model_animation_candidates
WHERE relation_source='explicit'
GROUP BY model_output;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var output = LibraryPathResolver.ResolveExistingFile(root, reader.GetString(0));
                if (string.IsNullOrWhiteSpace(output))
                {
                    continue;
                }

                result[NormalizeLibraryPath(root, output)] = reader.GetInt32(1);
            }

            return result;
        }

        private static Dictionary<string, int> LoadSqliteAnimationModelCounts(string root, SqliteConnection connection)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT animation_output, COUNT(DISTINCT model_output)
FROM model_animation_candidates
WHERE relation_source='explicit'
GROUP BY animation_output;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var output = LibraryPathResolver.ResolveExistingFile(root, reader.GetString(0));
                if (string.IsNullOrWhiteSpace(output))
                {
                    continue;
                }

                result[NormalizeLibraryPath(root, output)] = reader.GetInt32(1);
            }

            return result;
        }

        private IReadOnlyList<LibraryAnimationCandidate> LoadSqliteCandidatesForModel(LibraryModelItem model)
        {
            if (model == null || string.IsNullOrWhiteSpace(_sqliteDbPath) || !File.Exists(_sqliteDbPath))
            {
                return Array.Empty<LibraryAnimationCandidate>();
            }

            try
            {
                SQLitePCL.Batteries_V2.Init();
                using var connection = new SqliteConnection($"Data Source={_sqliteDbPath};Mode=ReadOnly");
                connection.Open();
                using var command = connection.CreateCommand();
                var outputs = BuildSqliteModelOutputKeys(model.OutputPath).ToArray();
                var parameterNames = new List<string>();
                for (var i = 0; i < outputs.Length; i++)
                {
                    var name = "$p" + i;
                    parameterNames.Add(name);
                    command.Parameters.AddWithValue(name, outputs[i]);
                }

                command.CommandText = $@"
SELECT c.model_output, c.raw_json, a.raw_json, m.raw_json
FROM model_animation_candidates c
JOIN assets m ON m.kind = 'Model' AND m.output = c.model_output
JOIN assets a ON a.kind = 'Animation' AND a.output = c.animation_output
WHERE c.relation_source = 'explicit'
  AND c.model_output IN ({string.Join(",", parameterNames)})
ORDER BY c.score DESC;";
                using var reader = command.ExecuteReader();
                var result = new List<LibraryAnimationCandidate>();
                while (reader.Read())
                {
                    var modelOutput = LibraryPathResolver.ResolveExistingFile(_root, reader.GetString(0));
                    using var candidateDocument = JsonDocument.Parse(reader.GetString(1));
                    using var animationDocument = JsonDocument.Parse(reader.GetString(2));
                    using var modelDocument = JsonDocument.Parse(reader.GetString(3));
                    var candidate = candidateDocument.RootElement;
                    var animation = animationDocument.RootElement;
                    var modelJson = modelDocument.RootElement;
                    var importedAvatarAsset = ResolveImportedAvatarAsset(_settings, modelJson, modelOutput);
                    var merged = MergeCandidate(_root, animation, candidate, modelJson, importedAvatarAsset);
                    if (merged.IsExplicit)
                    {
                        result.Add(merged);
                    }
                }

                return SortCandidates(result);
            }
            catch (Exception ex) when (!System.Diagnostics.Debugger.IsAttached)
            {
                WriteSqliteLoadError(_root, ex.ToString());
                return Array.Empty<LibraryAnimationCandidate>();
            }
        }

        private IEnumerable<string> BuildSqliteModelOutputKeys(string outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                yield break;
            }

            yield return outputPath;

            if (!string.IsNullOrWhiteSpace(_root))
            {
                string relative = null;
                try
                {
                    var fullRoot = Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    var fullOutput = Path.GetFullPath(outputPath);
                    if (fullOutput.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        relative = Path.GetRelativePath(_root, fullOutput).Replace('\\', '/');
                    }
                }
                catch
                {
                    relative = null;
                }

                if (!string.IsNullOrWhiteSpace(relative))
                {
                    yield return relative;
                }
            }

            yield return outputPath.Replace('\\', '/');
        }

        private IReadOnlyList<string> LoadSqliteModelOutputsForAnimation(LibraryAnimationUsage usage)
        {
            if (usage == null || string.IsNullOrWhiteSpace(_sqliteDbPath) || !File.Exists(_sqliteDbPath))
            {
                return Array.Empty<string>();
            }

            try
            {
                SQLitePCL.Batteries_V2.Init();
                using var connection = new SqliteConnection($"Data Source={_sqliteDbPath};Mode=ReadOnly");
                connection.Open();
                using var command = connection.CreateCommand();
                var outputs = BuildSqliteModelOutputKeys(usage.Animation.BestPath).ToArray();
                if (outputs.Length == 0)
                {
                    return Array.Empty<string>();
                }

                var parameterNames = new List<string>();
                for (var i = 0; i < outputs.Length; i++)
                {
                    var name = "$p" + i;
                    parameterNames.Add(name);
                    command.Parameters.AddWithValue(name, outputs[i]);
                }

                command.CommandText = $@"
SELECT DISTINCT model_output
FROM model_animation_candidates
WHERE relation_source='explicit'
  AND animation_output IN ({string.Join(",", parameterNames)})
ORDER BY model_output;";
                using var reader = command.ExecuteReader();
                var result = new List<string>();
                while (reader.Read())
                {
                    var modelOutput = LibraryPathResolver.ResolveExistingFile(_root, reader.GetString(0));
                    if (!string.IsNullOrWhiteSpace(modelOutput))
                    {
                        result.Add(NormalizeLibraryPath(_root, modelOutput));
                    }
                }

                return result;
            }
            catch (Exception ex) when (!System.Diagnostics.Debugger.IsAttached)
            {
                WriteSqliteLoadError(_root, ex.ToString());
                return Array.Empty<string>();
            }
        }

        private static LibraryAnimationIndex LoadCompact(string path)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(File.ReadAllText(path));
            }
            catch (JsonException)
            {
                return LoadCompactJsonLines(path);
            }

            using (document)
            {
            var root = document.RootElement;
            var libraryRoot = Path.GetDirectoryName(path) ?? "";
            var animations = LoadAnimationMap(root);
            var modelOutputs = LoadModelOutputMap(libraryRoot, root);
            var result = new Dictionary<string, List<LibraryAnimationCandidate>>(StringComparer.OrdinalIgnoreCase);

            if (!root.TryGetProperty("modelAnimationRefs", out var refs) || refs.ValueKind != JsonValueKind.Array)
            {
                    return new LibraryAnimationIndex(
                        Path.GetDirectoryName(path) ?? "",
                        result,
                        MergeAllAnimations(animations.Values.Select(x => ReadAnimation(libraryRoot, x)), LoadAllAnimationsFromBindingsJsonLines(libraryRoot)),
                        "compact",
                        result.Values.Sum(x => x.Count));
            }

            foreach (var item in refs.EnumerateArray())
            {
                var modelId = ReadString(item, "modelId");
                if (string.IsNullOrWhiteSpace(modelId) || !modelOutputs.TryGetValue(modelId, out var modelOutput))
                {
                    continue;
                }

                if (!item.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var list = new List<LibraryAnimationCandidate>();
                foreach (var candidate in candidates.EnumerateArray())
                {
                    var animationId = ReadString(candidate, "animationId") ?? ReadString(candidate, "id");
                    if (string.IsNullOrWhiteSpace(animationId) || !animations.TryGetValue(animationId, out var animation))
                    {
                        continue;
                    }

                    var merged = MergeCandidate(libraryRoot, animation, candidate);
                    if (merged.IsExplicit)
                    {
                        list.Add(merged);
                    }
                }

                if (list.Count > 0)
                {
                    result[NormalizePath(modelOutput)] = SortCandidates(list);
                }
            }

            return new LibraryAnimationIndex(
                libraryRoot,
                result,
                MergeAllAnimations(animations.Values.Select(x => ReadAnimation(libraryRoot, x)), LoadAllAnimationsFromBindingsJsonLines(libraryRoot)),
                "compact",
                result.Values.Sum(x => x.Count));
            }
        }

        private static LibraryAnimationIndex LoadCompactJsonLines(string path)
        {
            var result = new Dictionary<string, List<LibraryAnimationCandidate>>(StringComparer.OrdinalIgnoreCase);
            var allAnimations = new Dictionary<string, LibraryAnimationCandidate>(StringComparer.OrdinalIgnoreCase);
            var root = Path.GetDirectoryName(path) ?? "";

            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var entry = document.RootElement;
                    if (!entry.TryGetProperty("animation", out var animationElement)
                        || animationElement.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var animation = animationElement.Clone();
                    AddAllAnimation(allAnimations, ReadAnimation(root, animation));

                    var modelOutput = LibraryPathResolver.ResolveExistingFile(root, ReadString(entry, "modelOutput"));
                    if (string.IsNullOrWhiteSpace(modelOutput)
                        || !entry.TryGetProperty("candidates", out var candidates)
                        || candidates.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    var list = new List<LibraryAnimationCandidate>();
                    foreach (var candidate in candidates.EnumerateArray())
                    {
                        var merged = MergeCandidate(root, animation, candidate);
                        if (merged.IsExplicit)
                        {
                            list.Add(merged);
                        }
                    }

                    if (list.Count > 0)
                    {
                        result[NormalizePath(modelOutput)] = SortCandidates(list);
                    }
                }
                catch (JsonException)
                {
                    continue;
                }
            }

            return new LibraryAnimationIndex(
                root,
                result,
                MergeAllAnimations(allAnimations.Values, LoadAllAnimationsFromBindingsJsonLines(root)),
                "compact-jsonl",
                result.Values.Sum(x => x.Count));
        }

        private static Dictionary<string, JsonElement> LoadAnimationMap(JsonElement root)
        {
            var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            if (!root.TryGetProperty("animations", out var animations) || animations.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var animation in animations.EnumerateArray())
            {
                var id = ReadString(animation, "id");
                if (!string.IsNullOrWhiteSpace(id))
                {
                    result[id] = animation.Clone();
                }
            }

            return result;
        }

        private static Dictionary<string, string> LoadModelOutputMap(string libraryRoot, JsonElement root)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var model in models.EnumerateArray())
            {
                var id = ReadString(model, "id");
                var output = LibraryPathResolver.ResolveExistingFile(libraryRoot, ReadString(model, "output"));
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(output))
                {
                    result[id] = output;
                }
            }

            return result;
        }

        private static LibraryAnimationIndex LoadVerbose(string path)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var result = new Dictionary<string, List<LibraryAnimationCandidate>>(StringComparer.OrdinalIgnoreCase);

            if (root.TryGetProperty("relations", out var unrealRelations) && unrealRelations.ValueKind == JsonValueKind.Array)
            {
                return LoadUnrealVerbose(path, unrealRelations);
            }

            if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            {
                return new LibraryAnimationIndex(Path.GetDirectoryName(path) ?? "", result, loadSource: "verbose");
            }

            foreach (var modelEntry in models.EnumerateArray())
            {
                var model = modelEntry.TryGetProperty("model", out var modelInfo) ? modelInfo : modelEntry;
                var output = LibraryPathResolver.ResolveExistingFile(Path.GetDirectoryName(path) ?? "", ReadString(model, "output"));
                if (string.IsNullOrWhiteSpace(output)
                    || !modelEntry.TryGetProperty("candidates", out var candidates)
                    || candidates.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var list = new List<LibraryAnimationCandidate>();
                foreach (var candidate in candidates.EnumerateArray())
                {
                    var merged = ReadCandidate(Path.GetDirectoryName(path) ?? "", candidate);
                    if (merged.IsExplicit)
                    {
                        list.Add(merged);
                    }
                }

                if (list.Count > 0)
                {
                    result[NormalizePath(output)] = SortCandidates(list);
                }
            }

            return new LibraryAnimationIndex(
                Path.GetDirectoryName(path) ?? "",
                result,
                loadSource: "verbose",
                indexedCandidateCount: result.Values.Sum(x => x.Count));
        }

        private static LibraryAnimationIndex LoadUnrealVerbose(string path, JsonElement relations)
        {
            var libraryRoot = Path.GetDirectoryName(path) ?? "";
            var result = new Dictionary<string, List<LibraryAnimationCandidate>>(StringComparer.OrdinalIgnoreCase);

            foreach (var relation in relations.EnumerateArray())
            {
                var modelOutput = LibraryPathResolver.ResolveExistingFile(libraryRoot, ReadString(relation, "model"));
                if (string.IsNullOrWhiteSpace(modelOutput)
                    || !relation.TryGetProperty("animations", out var animations)
                    || animations.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var animation in animations.EnumerateArray())
                {
                    var candidate = ReadUnrealRelationAnimation(libraryRoot, relation, animation);
                    if (candidate.IsExplicit)
                    {
                        AddCandidate(result, modelOutput, candidate);
                    }
                }
            }

            foreach (var key in result.Keys.ToArray())
            {
                result[key] = SortCandidates(result[key]);
            }

            return new LibraryAnimationIndex(
                libraryRoot,
                result,
                MergeAllAnimations(
                    result.Values.SelectMany(x => x),
                    LoadAllAnimationsFromBindingsJsonLines(libraryRoot)),
                "unreal-json",
                result.Values.Sum(x => x.Count));
        }

        private static void AddCandidate(
            Dictionary<string, List<LibraryAnimationCandidate>> result,
            string modelOutput,
            LibraryAnimationCandidate candidate)
        {
            if (string.IsNullOrWhiteSpace(modelOutput))
            {
                return;
            }

            var key = NormalizePath(modelOutput);
            if (!result.TryGetValue(key, out var list))
            {
                list = new List<LibraryAnimationCandidate>();
                result[key] = list;
            }

            list.Add(candidate);
        }

        private static LibraryAnimationCandidate MergeCandidate(string libraryRoot, JsonElement animation, JsonElement candidate)
            => MergeCandidate(libraryRoot, animation, candidate, null, null);

        private static LibraryAnimationCandidate MergeCandidate(
            string libraryRoot,
            JsonElement animation,
            JsonElement candidate,
            JsonElement? model,
            UnityAvatarAssetResolution importedAvatar)
        {
            var output = ResolveAnimationOutputPath(libraryRoot, ReadString(animation, "output") ?? ReadString(candidate, "output") ?? "");
            var animationAsset = ResolveAnimationOutputPath(libraryRoot, ReadString(animation, "animationAsset") ?? "");
            var relation = ReadString(candidate, "relation") ?? ReadString(candidate, "relationSource") ?? "";
            var relationSource = ReadString(candidate, "relationSource") ?? "";
            var animationType = ReadString(animation, "animationType") ?? ReadString(candidate, "animationType") ?? "";
            var modelHasProductionAvatar = HasProductionUnityBakeAvatar(model);
            var importedAvatarAsset = importedAvatar?.AssetPath;
            var hasImportedAvatarAsset = !string.IsNullOrWhiteSpace(importedAvatarAsset);
            var candidateProductionReady = ReadBool(candidate, "productionUnityBakeReady");
            var productionReady = candidateProductionReady || modelHasProductionAvatar || hasImportedAvatarAsset;
            var productionBlocked = ReadBool(candidate, "productionUnityBakeBlocked") && !productionReady;
            if (IsUnrealAnimationRecord(output, animationAsset, relation, relationSource, animationType, ReadString(animation, "source")))
            {
                animationType = "Unreal";
            }

            return new LibraryAnimationCandidate
            {
                Name = ReadString(animation, "name") ?? ReadString(candidate, "name") ?? "",
                OutputPath = output,
                AnimationAssetPath = animationAsset,
                Source = ReadString(animation, "source") ?? "",
                SourceType = ReadString(animation, "sourceType") ?? ReadString(candidate, "sourceType") ?? "",
                Format = ReadString(animation, "format") ?? ReadString(candidate, "format") ?? "",
                AnimationType = animationType,
                Capability = ReadString(animation, "animationCapability") ?? "",
                NextAction = ReadString(candidate, "nextAction") ?? ReadString(animation, "nextAction") ?? "",
                ProductionAnimationPath = ReadString(candidate, "productionAnimationPath") ?? ReadString(animation, "productionAnimationPath") ?? "",
                Relation = relation,
                RelationSource = relationSource,
                Confidence = ReadString(candidate, "confidence") ?? "",
                ExportStatus = ReadString(animation, "status") ?? ReadString(candidate, "status") ?? "",
                Score = ReadDouble(candidate, "score"),
                Duration = ReadDouble(animation, "duration"),
                SampleRate = ReadDouble(animation, "sampleRate"),
                CurveCount = ReadInt32(animation, "curveCount"),
                FrameCount = ReadInt32(animation, "frameCount"),
                TrackCount = ReadInt32(animation, "trackCount"),
                SegmentCount = ReadInt32(animation, "segmentCount"),
                MatchedPathCount = Math.Max(ReadArrayLength(candidate, "matchedBindingPaths"), ReadInt32(animation, "matchedTrackBones")),
                TrackCoverage = ReadDouble(animation, "trackCoverage"),
                ValidationStatus = ReadString(animation, "validationStatus") ?? "",
                ValidationCategory = ReadString(animation, "validationCategory") ?? "",
                ValidationReason = ReadString(animation, "validationReason") ?? "",
                RequiresHumanoidBake = ReadBool(candidate, "requiresHumanoidBake"),
                RequiresUnityBake = ReadBool(candidate, "requiresUnityBake") || ReadBool(animation, "requiresUnityBake") || productionReady,
                RequiresInternalHumanoidSolve = ReadBool(candidate, "requiresInternalHumanoidSolve") || ReadBool(animation, "requiresInternalHumanoidSolve"),
                ProductionUnityBakeReady = productionReady,
                ProductionUnityBakeBlocked = productionBlocked,
                ProductionUnityBakeBlockedReason = productionBlocked ? ReadString(candidate, "productionUnityBakeBlockedReason") ?? "" : "",
                ProductionUnityBakeAvatarSource = DetermineProductionUnityBakeAvatarSource(
                    hasImportedAvatarAsset,
                    modelHasProductionAvatar,
                    candidateProductionReady),
                ProductionUnityBakeAvatarAsset = importedAvatarAsset ?? ReadString(candidate, "unityAvatarAsset") ?? "",
                ProductionUnityBakeAvatarMatchKey = importedAvatar?.MatchKey ?? ReadString(candidate, "unityAvatarMatchKey") ?? "",
                HasAvatarOracle = HasAvatarOracle(model),
                IsContainerAnimation = ReadBool(animation, "isContainerAnimation"),
                BindingPaths = ReadStringArray(animation, "transformBindingPaths")
            };
        }

        private static string ResolveAnimationOutputPath(string libraryRoot, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            var resolved = LibraryPathResolver.ResolveExistingFile(libraryRoot, value);
            return string.IsNullOrWhiteSpace(resolved) ? value : resolved;
        }

        private static bool IsUnrealAnimationRecord(params string[] values)
        {
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (value.EndsWith(".ueanim", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("ueanim", StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("UAnim", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("AnimSequence", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("AnimMontage", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("AnimComposite", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("unreal", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("modelAnimationRelation", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static LibraryAnimationCandidate ReadCandidate(string libraryRoot, JsonElement candidate)
        {
            return new LibraryAnimationCandidate
            {
                Name = ReadString(candidate, "name") ?? "",
                OutputPath = LibraryPathResolver.ResolveExistingFile(libraryRoot, ReadString(candidate, "output") ?? ""),
                AnimationAssetPath = LibraryPathResolver.ResolveExistingFile(libraryRoot, ReadString(candidate, "animationAsset") ?? ""),
                Source = ReadString(candidate, "source") ?? "",
                SourceType = ReadString(candidate, "sourceType") ?? "",
                Format = ReadString(candidate, "format") ?? "",
                AnimationType = ReadString(candidate, "animationType") ?? "",
                Capability = ReadString(candidate, "animationCapability") ?? "",
                NextAction = ReadString(candidate, "nextAction") ?? "",
                ProductionAnimationPath = ReadString(candidate, "productionAnimationPath") ?? "",
                Relation = ReadString(candidate, "relation") ?? ReadString(candidate, "relationSource") ?? "",
                RelationSource = ReadString(candidate, "relationSource") ?? "",
                Confidence = ReadString(candidate, "confidence") ?? "",
                ExportStatus = ReadString(candidate, "status") ?? "",
                Score = ReadDouble(candidate, "score"),
                Duration = ReadDouble(candidate, "duration"),
                SampleRate = ReadDouble(candidate, "sampleRate"),
                CurveCount = ReadInt32(candidate, "curveCount"),
                FrameCount = ReadInt32(candidate, "frameCount"),
                TrackCount = ReadInt32(candidate, "trackCount"),
                SegmentCount = ReadInt32(candidate, "segmentCount"),
                MatchedPathCount = ReadArrayLength(candidate, "matchedBindingPaths"),
                TrackCoverage = ReadDouble(candidate, "trackCoverage"),
                ValidationStatus = ReadString(candidate, "validationStatus") ?? "",
                ValidationCategory = ReadString(candidate, "validationCategory") ?? "",
                ValidationReason = ReadString(candidate, "validationReason") ?? "",
                RequiresHumanoidBake = ReadBool(candidate, "requiresHumanoidBake"),
                RequiresUnityBake = ReadBool(candidate, "requiresUnityBake"),
                RequiresInternalHumanoidSolve = ReadBool(candidate, "requiresInternalHumanoidSolve"),
                ProductionUnityBakeReady = ReadBool(candidate, "productionUnityBakeReady"),
                ProductionUnityBakeBlocked = ReadBool(candidate, "productionUnityBakeBlocked"),
                ProductionUnityBakeBlockedReason = ReadString(candidate, "productionUnityBakeBlockedReason") ?? "",
                ProductionUnityBakeAvatarSource = DetermineProductionUnityBakeAvatarSource(
                    !string.IsNullOrWhiteSpace(ReadString(candidate, "unityAvatarAsset")),
                    false,
                    ReadBool(candidate, "productionUnityBakeReady")),
                ProductionUnityBakeAvatarAsset = ReadString(candidate, "unityAvatarAsset") ?? "",
                ProductionUnityBakeAvatarMatchKey = ReadString(candidate, "unityAvatarMatchKey") ?? "",
                IsContainerAnimation = ReadBool(candidate, "isContainerAnimation"),
                BindingPaths = ReadStringArray(candidate, "transformBindingPaths")
            };
        }

        private static LibraryAnimationCandidate ReadAnimation(string libraryRoot, JsonElement animation)
        {
            var output = LibraryPathResolver.ResolveExistingFile(libraryRoot, ReadString(animation, "output") ?? "");
            var animationAsset = LibraryPathResolver.ResolveExistingFile(libraryRoot, ReadString(animation, "animationAsset") ?? "");
            var animationType = ReadString(animation, "animationType") ?? "";
            if (IsUnrealAnimationRecord(
                output,
                animationAsset,
                animationType,
                ReadString(animation, "sourceType"),
                ReadString(animation, "format"),
                ReadString(animation, "source")))
            {
                animationType = "Unreal";
            }

            return new LibraryAnimationCandidate
            {
                Name = ReadString(animation, "name") ?? "",
                OutputPath = output,
                AnimationAssetPath = animationAsset,
                Source = ReadString(animation, "source") ?? "",
                SourceType = ReadString(animation, "sourceType") ?? "",
                Format = ReadString(animation, "format") ?? "",
                AnimationType = animationType,
                Capability = ReadString(animation, "animationCapability") ?? "",
                NextAction = ReadString(animation, "nextAction") ?? "",
                ProductionAnimationPath = ReadString(animation, "productionAnimationPath") ?? "",
                Relation = "",
                RelationSource = "",
                Confidence = "",
                ExportStatus = ReadString(animation, "status") ?? "",
                Score = 0,
                Duration = ReadDouble(animation, "duration"),
                SampleRate = ReadDouble(animation, "sampleRate"),
                CurveCount = ReadInt32(animation, "curveCount"),
                FrameCount = ReadInt32(animation, "frameCount"),
                TrackCount = ReadInt32(animation, "trackCount"),
                SegmentCount = ReadInt32(animation, "segmentCount"),
                TrackCoverage = ReadDouble(animation, "trackCoverage"),
                ValidationStatus = ReadString(animation, "validationStatus") ?? "",
                ValidationCategory = ReadString(animation, "validationCategory") ?? "",
                ValidationReason = ReadString(animation, "validationReason") ?? "",
                RequiresHumanoidBake = ReadBool(animation, "hasMuscleClip"),
                RequiresUnityBake = ReadBool(animation, "requiresUnityBake"),
                RequiresInternalHumanoidSolve = ReadBool(animation, "requiresInternalHumanoidSolve"),
                ProductionUnityBakeReady = ReadBool(animation, "productionUnityBakeReady"),
                ProductionUnityBakeBlocked = ReadBool(animation, "productionUnityBakeBlocked"),
                ProductionUnityBakeBlockedReason = ReadString(animation, "productionUnityBakeBlockedReason") ?? "",
                ProductionUnityBakeAvatarSource = DetermineProductionUnityBakeAvatarSource(
                    !string.IsNullOrWhiteSpace(ReadString(animation, "unityAvatarAsset")),
                    false,
                    ReadBool(animation, "productionUnityBakeReady")),
                ProductionUnityBakeAvatarAsset = ReadString(animation, "unityAvatarAsset") ?? "",
                ProductionUnityBakeAvatarMatchKey = ReadString(animation, "unityAvatarMatchKey") ?? "",
                IsContainerAnimation = ReadBool(animation, "isContainerAnimation"),
                BindingPaths = ReadStringArray(animation, "transformBindingPaths")
            };
        }

        private static LibraryAnimationCandidate ReadUnrealRelationAnimation(string libraryRoot, JsonElement relation, JsonElement animation)
            => ReadUnrealRelationAnimation(libraryRoot, ReadString(relation, "confidence") ?? "", animation);

        private static LibraryAnimationCandidate ReadUnrealRelationAnimation(string libraryRoot, string confidence, JsonElement animation)
        {
            var output = ResolveAnimationOutputPath(
                libraryRoot,
                ReadString(animation, "output") ?? ReadString(animation, "animationAsset") ?? "");
            var exportStatus = ReadString(animation, "status") ?? "";
            var validationStatus = ReadString(animation, "validationStatus") ?? exportStatus;
            var validationCategory = ReadString(animation, "validationCategory") ?? "";
            var trackCoverage = ReadDouble(animation, "trackCoverage");
            var trackCount = ReadInt32(animation, "trackCount");
            var isUsableCandidate = ReadBool(animation, "isUsableCandidate")
                || ((string.IsNullOrWhiteSpace(exportStatus) || string.Equals(exportStatus, "ok", StringComparison.OrdinalIgnoreCase))
                    && !string.Equals(validationStatus, "error", StringComparison.OrdinalIgnoreCase));
            var rawRelationSource = ReadString(animation, "relationSource") ?? "";
            var relationSource = !string.IsNullOrWhiteSpace(rawRelationSource)
                ? rawRelationSource
                : confidence.Contains("Explicit", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(confidence, "ComponentOwner", StringComparison.OrdinalIgnoreCase)
                    ? "explicit"
                    : "unreal";

            return new LibraryAnimationCandidate
            {
                Name = ReadString(animation, "name") ?? "",
                OutputPath = output,
                Source = ReadString(animation, "source") ?? "",
                SourceType = ReadString(animation, "sourceType") ?? "",
                Format = ReadString(animation, "format") ?? (output.EndsWith(".ueanim", StringComparison.OrdinalIgnoreCase) ? "ueanim" : ""),
                AnimationType = "Unreal",
                Capability = string.IsNullOrWhiteSpace(validationCategory) ? validationStatus : validationCategory,
                Relation = "unreal.modelAnimationRelation",
                RelationSource = isUsableCandidate ? relationSource : "diagnostic",
                Confidence = confidence,
                ExportStatus = exportStatus,
                Score = ScoreUnrealRelationAnimation(confidence, exportStatus, validationStatus, validationCategory, trackCoverage, trackCount),
                Duration = ReadDouble(animation, "duration"),
                FrameCount = ReadInt32(animation, "frameCount"),
                TrackCount = trackCount,
                SegmentCount = ReadInt32(animation, "segmentCount"),
                MatchedPathCount = ReadInt32(animation, "matchedTrackBones"),
                TrackCoverage = trackCoverage,
                ValidationStatus = validationStatus,
                ValidationCategory = validationCategory,
                ValidationReason = ReadString(animation, "validationReason") ?? ReadString(animation, "reason") ?? "",
                NeedsValidation = !isUsableCandidate || !string.Equals(validationStatus, "ok", StringComparison.OrdinalIgnoreCase),
                IsContainerAnimation = ReadBool(animation, "isContainerAnimation")
                    || ReadInt32(animation, "referencedAnimationCount") > 0
                    || ReadInt32(animation, "segmentCount") > 0
            };
        }

        private static string PatchUnrealRelationAnimationJson(SqliteDataReader reader)
        {
            using var document = JsonDocument.Parse(reader.GetString(2));
            var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                map[property.Name] = property.Value.Clone();
            }

            // SQLite 的结构化列是 UE 关系索引的稳定字段；raw_json 来自历史版本时，用这些列补齐关键预览输入。
            SetIfPresent(map, "output", reader, 3);
            SetIfPresent(map, "status", reader, 4);
            SetIfPresent(map, "duration", reader, 5);
            SetIfPresent(map, "frameCount", reader, 6);
            SetIfPresent(map, "trackCount", reader, 7);
            SetIfPresent(map, "relationSource", reader, 8);
            SetIfPresent(map, "validationStatus", reader, 9);
            SetIfPresent(map, "validationCategory", reader, 10);
            SetIfPresent(map, "validationReason", reader, 11);
            SetIfPresent(map, "trackCoverage", reader, 12);
            SetIfPresent(map, "isUsableCandidate", reader, 13, sqliteBool: true);
            SetIfPresent(map, "segmentCount", reader, 14);
            if (!map.ContainsKey("format")
                && map.TryGetValue("output", out var output)
                && output is string outputText
                && outputText.EndsWith(".ueanim", StringComparison.OrdinalIgnoreCase))
            {
                map["format"] = "ueanim";
            }

            return JsonSerializer.Serialize(map);
        }

        private static void SetIfPresent(Dictionary<string, object> map, string name, SqliteDataReader reader, int ordinal, bool sqliteBool = false)
        {
            if (reader.IsDBNull(ordinal))
            {
                return;
            }

            object value = reader.GetValue(ordinal);
            if (sqliteBool && value is long longValue)
            {
                value = longValue != 0;
            }

            map[name] = value;
        }

        private static double ScoreUnrealRelationAnimation(
            string confidence,
            string exportStatus,
            string validationStatus,
            string validationCategory,
            double trackCoverage,
            int trackCount)
        {
            var score = 50d;
            if (!string.IsNullOrWhiteSpace(exportStatus) &&
                !string.Equals(exportStatus, "ok", StringComparison.OrdinalIgnoreCase))
            {
                score -= 1000;
            }

            if (confidence.Contains("Explicit", StringComparison.OrdinalIgnoreCase)
                || string.Equals(confidence, "ComponentOwner", StringComparison.OrdinalIgnoreCase))
            {
                score += 30;
            }

            if (string.Equals(validationStatus, "ok", StringComparison.OrdinalIgnoreCase))
            {
                score += 15;
            }
            else if (string.Equals(validationStatus, "warning", StringComparison.OrdinalIgnoreCase))
            {
                score += 5;
            }

            if (string.Equals(validationCategory, "validated", StringComparison.OrdinalIgnoreCase))
            {
                score += 10;
            }

            score += Math.Min(10, trackCoverage * 10);
            score += Math.Min(5, trackCount / 20d);
            return score;
        }

        private static List<LibraryAnimationCandidate> LoadAllAnimationsFromSqlite(string libraryRoot, SqliteConnection connection)
        {
            var result = new Dictionary<string, LibraryAnimationCandidate>(StringComparer.OrdinalIgnoreCase);
            if (!HasTable(connection, "assets"))
            {
                return new List<LibraryAnimationCandidate>();
            }

            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT raw_json
FROM assets
WHERE kind = 'Animation' OR source_type = 'AnimationClip';";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    using var document = JsonDocument.Parse(reader.GetString(0));
                    var root = document.RootElement;
                    var animation = root.TryGetProperty("animation", out var animationElement)
                        ? animationElement
                        : root;
                    AddAllAnimation(result, ReadAnimation(libraryRoot, animation));
                }
                catch (JsonException)
                {
                }
            }

            return result.Values.ToList();
        }

        private static List<LibraryAnimationCandidate> LoadAllAnimationsFromBindingsJsonLines(string root)
        {
            var result = new Dictionary<string, LibraryAnimationCandidate>(StringComparer.OrdinalIgnoreCase);
            var bindingsPath = Path.Combine(root, "animation_bindings.jsonl");
            if (!File.Exists(bindingsPath))
            {
                return new List<LibraryAnimationCandidate>();
            }

            foreach (var line in File.ReadLines(bindingsPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var rootElement = document.RootElement;
                    var animation = rootElement.TryGetProperty("animation", out var animationElement)
                        ? animationElement
                        : rootElement;
                    AddAllAnimation(result, ReadAnimation(root, animation));
                }
                catch (JsonException)
                {
                    continue;
                }
            }

            return result.Values.ToList();
        }

        private static List<LibraryAnimationCandidate> MergeAllAnimations(
            IEnumerable<LibraryAnimationCandidate> primary,
            IEnumerable<LibraryAnimationCandidate> secondary)
        {
            var result = new Dictionary<string, LibraryAnimationCandidate>(StringComparer.OrdinalIgnoreCase);
            foreach (var animation in primary ?? Array.Empty<LibraryAnimationCandidate>())
            {
                AddAllAnimation(result, animation);
            }

            foreach (var animation in secondary ?? Array.Empty<LibraryAnimationCandidate>())
            {
                AddAllAnimation(result, animation);
            }

            return result.Values.ToList();
        }

        private static void AddAllAnimation(Dictionary<string, LibraryAnimationCandidate> result, LibraryAnimationCandidate animation)
        {
            var key = BuildAnimationKey(animation);
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            if (!result.TryGetValue(key, out var existing)
                || (existing.BindingPaths.Length == 0 && animation.BindingPaths.Length > 0))
            {
                result[key] = animation;
            }
        }

        private IReadOnlyList<LibraryAnimationCandidate> FindTargetedForModelCore(LibraryModelItem model)
        {
            if (TryFindTargetedForModelFromSqlite(model, out var sqliteCandidates))
            {
                return sqliteCandidates;
            }

            var bindingsPath = Path.Combine(_root, "animation_bindings.jsonl");
            if (!File.Exists(bindingsPath))
            {
                return Array.Empty<LibraryAnimationCandidate>();
            }

            var modelPaths = BuildModelBindingPathSet(model);
            if (modelPaths.Count == 0)
            {
                return Array.Empty<LibraryAnimationCandidate>();
            }

            var existing = FindForModel(model)
                .Select(x => x.BestPath)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var result = new List<LibraryAnimationCandidate>();

            foreach (var line in File.ReadLines(bindingsPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonElement animation;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (!root.TryGetProperty("animation", out animation))
                    {
                        continue;
                    }
                    animation = animation.Clone();
                }
                catch
                {
                    continue;
                }

                var bestPath = LibraryPathResolver.ResolveExistingFile(_root, ReadString(animation, "output") ?? ReadString(animation, "animationAsset") ?? "");
                if (!string.IsNullOrWhiteSpace(bestPath) && existing.Contains(bestPath))
                {
                    continue;
                }

                var animationPaths = ReadStringArray(animation, "transformBindingPaths");
                var matched = CountMatchedBindingPaths(modelPaths, animationPaths);
                if (!IsTargetedMatch(model, animation, matched, animationPaths.Length))
                {
                    continue;
                }

                result.Add(BuildTargetedCandidate(_root, animation, matched));
            }

            return SortCandidates(result).Take(MaxTargetedCandidateCount).ToList();
        }

        private bool TryFindTargetedForModelFromSqlite(LibraryModelItem model, out IReadOnlyList<LibraryAnimationCandidate> candidates)
        {
            candidates = Array.Empty<LibraryAnimationCandidate>();
            var dbPath = Path.Combine(_root, "library_index.db");
            if (!File.Exists(dbPath))
            {
                return false;
            }

            var modelPaths = BuildModelBindingPathSet(model);
            if (modelPaths.Count == 0)
            {
                return true;
            }

            try
            {
                SQLitePCL.Batteries_V2.Init();
                using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                connection.Open();
                if (!HasTable(connection, "animation_binding_paths"))
                {
                    return false;
                }

                var existing = FindForModel(model)
                    .Select(x => x.BestPath)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var byKey = new Dictionary<string, (JsonElement Animation, int Matched)>(StringComparer.OrdinalIgnoreCase);

                foreach (var chunk in modelPaths.Chunk(700))
                {
                    using var command = connection.CreateCommand();
                    var names = new List<string>();
                    for (var i = 0; i < chunk.Length; i++)
                    {
                        var parameterName = "$p" + i;
                        names.Add(parameterName);
                        command.Parameters.AddWithValue(parameterName, chunk[i]);
                    }

                    command.CommandText = $@"
SELECT ab.raw_json, COUNT(DISTINCT abp.binding_path) AS matched
FROM animation_binding_paths abp
JOIN animation_bindings ab ON ab.id = abp.animation_binding_id
WHERE abp.binding_path IN ({string.Join(",", names)})
GROUP BY ab.id;";

                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var rawJson = reader.GetString(0);
                        var matched = reader.GetInt32(1);
                        using var document = JsonDocument.Parse(rawJson);
                        var root = document.RootElement;
                        var animation = root.TryGetProperty("animation", out var animationElement)
                            ? animationElement.Clone()
                            : root.Clone();
                        var bestPath = LibraryPathResolver.ResolveExistingFile(_root, ReadString(animation, "output") ?? ReadString(animation, "animationAsset") ?? "");
                        if (!string.IsNullOrWhiteSpace(bestPath) && existing.Contains(bestPath))
                        {
                            continue;
                        }

                        var key = !string.IsNullOrWhiteSpace(bestPath)
                            ? bestPath
                            : $"{ReadString(animation, "name")}|{ReadString(animation, "source")}|{ReadString(animation, "pathId")}";
                        byKey[key] = byKey.TryGetValue(key, out var current)
                            ? (current.Animation, current.Matched + matched)
                            : (animation, matched);
                    }
                }

                candidates = SortCandidates(byKey.Values
                    .Where(x => IsTargetedMatch(model, x.Animation, x.Matched, ReadArrayLength(x.Animation, "transformBindingPaths")))
                    .Select(x => BuildTargetedCandidate(_root, x.Animation, x.Matched))
                    .ToList())
                    .Take(MaxTargetedCandidateCount)
                    .ToList();
                return true;
            }
            catch (SqliteException)
            {
                return false;
            }
        }

        private static bool HasTable(SqliteConnection connection, string tableName)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;";
            command.Parameters.AddWithValue("$name", tableName);
            return command.ExecuteScalar() != null;
        }

        private static bool HasColumn(SqliteConnection connection, string tableName, string columnName)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({tableName});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.FieldCount > 1
                    && string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static LibraryAnimationCandidate BuildTargetedCandidate(string libraryRoot, JsonElement animation, int matched)
        {
            var output = LibraryPathResolver.ResolveExistingFile(libraryRoot, ReadString(animation, "output") ?? "");
            var animationAsset = LibraryPathResolver.ResolveExistingFile(libraryRoot, ReadString(animation, "animationAsset") ?? "");
            var animationType = ReadString(animation, "animationType") ?? "";
            if (IsUnrealAnimationRecord(
                output,
                animationAsset,
                animationType,
                ReadString(animation, "sourceType"),
                ReadString(animation, "format"),
                ReadString(animation, "source")))
            {
                animationType = "Unreal";
            }

            return new LibraryAnimationCandidate
            {
                Name = ReadString(animation, "name") ?? "",
                OutputPath = output,
                AnimationAssetPath = animationAsset,
                Source = ReadString(animation, "source") ?? "",
                SourceType = ReadString(animation, "sourceType") ?? "",
                Format = ReadString(animation, "format") ?? "",
                AnimationType = animationType,
                Capability = ReadString(animation, "animationCapability") ?? "",
                Relation = "animationClip.bindingPath.targetedModelMatch",
                RelationSource = "targeted",
                Confidence = "targeted_structural_needs_validation",
                ExportStatus = ReadString(animation, "status") ?? "",
                Score = Math.Min(95, matched * 2),
                Duration = ReadDouble(animation, "duration"),
                SampleRate = ReadDouble(animation, "sampleRate"),
                CurveCount = ReadInt32(animation, "curveCount"),
                FrameCount = ReadInt32(animation, "frameCount"),
                TrackCount = ReadInt32(animation, "trackCount"),
                SegmentCount = ReadInt32(animation, "segmentCount"),
                MatchedPathCount = matched,
                TrackCoverage = ReadDouble(animation, "trackCoverage"),
                ValidationStatus = ReadString(animation, "validationStatus") ?? "",
                ValidationCategory = ReadString(animation, "validationCategory") ?? "",
                ValidationReason = ReadString(animation, "validationReason") ?? "",
                RequiresHumanoidBake = ReadBool(animation, "hasMuscleClip"),
                NeedsValidation = true,
                IsContainerAnimation = ReadBool(animation, "isContainerAnimation"),
                BindingPaths = ReadStringArray(animation, "transformBindingPaths")
            };
        }

        private static HashSet<string> BuildModelBindingPathSet(LibraryModelItem model)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in model.BonePaths.Concat(model.NodePaths))
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var normalized = NormalizeUnityPath(path);
                AddPathAndSuffixes(result, normalized);
                var rootIndex = normalized.IndexOf("/Root_JNT/", StringComparison.OrdinalIgnoreCase);
                if (rootIndex >= 0)
                {
                    AddPathAndSuffixes(result, normalized[(rootIndex + 1)..]);
                }
            }

            return result;
        }

        private static void AddPathAndSuffixes(HashSet<string> paths, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            paths.Add(path);
            var start = path.IndexOf('/');
            while (start >= 0 && start + 1 < path.Length)
            {
                paths.Add(path[(start + 1)..]);
                start = path.IndexOf('/', start + 1);
            }
        }

        private static int CountMatchedBindingPaths(HashSet<string> modelPaths, string[] animationPaths)
        {
            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in animationPaths)
            {
                var normalized = NormalizeUnityPath(path);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                if (modelPaths.Contains(normalized))
                {
                    matched.Add(normalized);
                }
            }

            return matched.Count;
        }

        private static bool IsTargetedMatch(LibraryModelItem model, JsonElement animation, int matchedPathCount, int animationPathCount)
        {
            if (model == null || matchedPathCount <= 0 || animationPathCount <= 0)
            {
                return false;
            }

            var modelBoneCount = Math.Max(model.BoneCount, model.BonePaths.Length);
            if (animationPathCount <= 3)
            {
                // 少量 Transform 轨道更像机关/道具动画。骨骼很多的角色模型不靠这类弱信号兜底匹配。
                return matchedPathCount == animationPathCount && modelBoneCount < 8;
            }

            if (modelBoneCount < 3)
            {
                return false;
            }

            var requiredMatched = Math.Max(6, (int)Math.Ceiling(animationPathCount * 0.45));
            if (matchedPathCount < requiredMatched)
            {
                return false;
            }

            // 角色/生物骨骼相似时很容易海量命中。兜底结果必须至少有通用名称词元交集，
            // 但这里只影响“需验证”预览列表，不写回默认模型-动画关系。
            return HasTargetedSemanticOverlap(model, animation);
        }

        private static bool HasTargetedSemanticOverlap(LibraryModelItem model, JsonElement animation)
        {
            var modelTokens = ExtractTargetedSemanticTokens(model.Name)
                .Concat(ExtractTargetedSemanticTokens(model.OutputPath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (modelTokens.Count == 0)
            {
                return true;
            }

            var animationTokens = ExtractTargetedSemanticTokens(ReadString(animation, "name"))
                .Concat(ExtractTargetedSemanticTokens(ReadString(animation, "output")))
                .Concat(ExtractTargetedSemanticTokens(ReadString(animation, "animationAsset")))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return animationTokens.Count == 0 || modelTokens.Overlaps(animationTokens);
        }

        private static IEnumerable<string> ExtractTargetedSemanticTokens(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                yield break;
            }

            foreach (var raw in System.Text.RegularExpressions.Regex.Split(value.ToLowerInvariant(), @"[^a-z0-9]+"))
            {
                if (raw.Length < 3 || raw.All(char.IsDigit) || TargetedSemanticStopWords.Contains(raw))
                {
                    continue;
                }

                yield return raw;
            }
        }

        private static List<LibraryAnimationCandidate> SortCandidates(List<LibraryAnimationCandidate> candidates)
        {
            return candidates
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.MatchedPathCount)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static Dictionary<string, LibraryAnimationUsage> BuildAnimationUsageMap(
            Dictionary<string, List<LibraryAnimationCandidate>> byModelOutput,
            IEnumerable<LibraryAnimationCandidate> allAnimations,
            IReadOnlyDictionary<string, int> sqliteModelCountByAnimationKey = null)
        {
            var builders = new Dictionary<string, AnimationUsageBuilder>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in byModelOutput)
            {
                foreach (var animation in pair.Value)
                {
                    var key = BuildAnimationKey(animation);
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    if (!builders.TryGetValue(key, out var builder))
                    {
                        builder = new AnimationUsageBuilder(key, animation);
                        builders[key] = builder;
                    }
                    else if (IsBetterRepresentative(animation, builder.Animation))
                    {
                        builder.Animation = animation;
                    }

                    builder.ModelOutputs.Add(NormalizePath(pair.Key));
                }
            }

            if (allAnimations != null)
            {
                foreach (var animation in allAnimations)
                {
                    var key = BuildAnimationKey(animation);
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    if (!builders.ContainsKey(key))
                    {
                        builders[key] = new AnimationUsageBuilder(key, animation);
                    }
                }
            }

            return builders.ToDictionary(
                x => x.Key,
                x => new LibraryAnimationUsage
                {
                    Key = x.Key,
                    Animation = x.Value.Animation,
                    ModelCountOverride = sqliteModelCountByAnimationKey != null
                        && sqliteModelCountByAnimationKey.TryGetValue(x.Key, out var modelCount)
                            ? modelCount
                            : null,
                    ModelOutputs = x.Value.ModelOutputs
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(y => y, StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                },
                StringComparer.OrdinalIgnoreCase);
        }

        private static string BuildAnimationKey(LibraryAnimationCandidate animation)
        {
            if (animation == null)
            {
                return "";
            }

            return !string.IsNullOrWhiteSpace(animation.BestPath)
                ? NormalizePath(animation.BestPath)
                : animation.Name ?? "";
        }

        private static bool IsBetterRepresentative(LibraryAnimationCandidate candidate, LibraryAnimationCandidate current)
        {
            if (current == null)
            {
                return true;
            }

            if (candidate.IsExplicit != current.IsExplicit)
            {
                return candidate.IsExplicit;
            }

            return candidate.Score > current.Score;
        }

        private sealed class AnimationUsageBuilder
        {
            public AnimationUsageBuilder(string key, LibraryAnimationCandidate animation)
            {
                Key = key;
                Animation = animation;
            }

            public string Key { get; }
            public LibraryAnimationCandidate Animation { get; set; }
            public HashSet<string> ModelOutputs { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private static string NormalizePath(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string NormalizeLibraryPath(string libraryRoot, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "";
            }

            var absolute = Path.IsPathRooted(path) || string.IsNullOrWhiteSpace(libraryRoot)
                ? path
                : Path.Combine(libraryRoot, path);
            return NormalizePath(absolute);
        }

        private static string NormalizeUnityPath(string path)
        {
            return (path ?? "").Replace('\\', '/').Trim('/');
        }

        private static string[] ReadStringArray(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            return property.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();
        }

        private static string ReadString(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
        }

        private static int ReadInt32(JsonElement obj, string name)
        {
            return obj.TryGetProperty(name, out var property)
                && property.ValueKind == JsonValueKind.Number
                && property.TryGetInt32(out var value)
                    ? value
                    : 0;
        }

        private static double ReadDouble(JsonElement obj, string name)
        {
            return obj.TryGetProperty(name, out var property)
                && property.ValueKind == JsonValueKind.Number
                && property.TryGetDouble(out var value)
                    ? value
                    : 0;
        }

        private static bool ReadBool(JsonElement obj, string name)
        {
            return obj.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
        }

        private static int ReadArrayLength(JsonElement obj, string name)
        {
            return obj.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array ? property.GetArrayLength() : 0;
        }

        private static bool HasProductionUnityBakeAvatar(JsonElement? model)
        {
            if (model == null || model.Value.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!model.Value.TryGetProperty("avatar", out var avatar) || avatar.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var humanBones = ReadArrayLength(avatar, "humanBones");
            var skeletonBones = ReadArrayLength(avatar, "skeletonBones");
            return humanBones > 0 && skeletonBones > 0;
        }

        private static string DetermineProductionUnityBakeAvatarSource(
            bool hasImportedAvatarAsset,
            bool modelHasProductionAvatar,
            bool candidateProductionReady)
        {
            if (hasImportedAvatarAsset)
            {
                return "imported_unity_avatar_asset";
            }

            if (modelHasProductionAvatar)
            {
                return "model_human_description";
            }

            return candidateProductionReady ? "candidate_production_avatar" : "";
        }

        private static UnityAvatarAssetResolution ResolveImportedAvatarAsset(
            LibraryBrowserSettings settings,
            JsonElement? model,
            string modelOutput)
        {
            if (settings == null || model == null || model.Value.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var avatarName = "";
            if (model.Value.TryGetProperty("avatar", out var avatar) && avatar.ValueKind == JsonValueKind.Object)
            {
                avatarName = ReadString(avatar, "name") ?? "";
            }

            return settings.ResolveUnityAvatarAssetDetails(
                avatarName,
                ReadString(model.Value, "name"),
                ReadString(model.Value, "output"),
                modelOutput);
        }

        private static bool HasAvatarOracle(JsonElement? model)
        {
            if (model == null || model.Value.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!model.Value.TryGetProperty("avatar", out var avatar) || avatar.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return HasCompleteOraclePayload(avatar, "oracle") || HasCompleteOraclePayload(avatar, "internalSolver");
        }

        private static bool HasCompleteOraclePayload(JsonElement avatar, string name)
        {
            if (!avatar.TryGetProperty(name, out var oracle) || oracle.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var humanBoneIndex = ReadArrayLength(oracle, "humanBoneIndex");
            if (humanBoneIndex <= 0)
            {
                return false;
            }

            return HasCompleteSkeletonPose(oracle, "humanSkeleton", "pose")
                || HasCompleteSkeletonPose(oracle, "skeleton", "humanSkeletonPose")
                || HasCompleteSkeletonPose(oracle, "avatarSkeleton", "defaultPose");
        }

        private static bool HasCompleteSkeletonPose(JsonElement oracle, string skeletonName, string poseName)
        {
            if (!oracle.TryGetProperty(skeletonName, out var skeleton) || skeleton.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var nodes = ReadArrayLength(skeleton, "nodes");
            var pose = ReadArrayLength(skeleton, poseName);
            return nodes > 0 && pose >= nodes;
        }
    }
}
