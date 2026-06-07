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
        private readonly Dictionary<string, List<LibraryAnimationCandidate>> _byModelOutput;
        private readonly Dictionary<string, IReadOnlyList<LibraryAnimationCandidate>> _targetedCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _root;
        private readonly object _cacheLock = new();

        private LibraryAnimationIndex(string root, Dictionary<string, List<LibraryAnimationCandidate>> byModelOutput)
        {
            _root = root;
            _byModelOutput = byModelOutput;
        }

        public static LibraryAnimationIndex Empty { get; } = new("", new Dictionary<string, List<LibraryAnimationCandidate>>(StringComparer.OrdinalIgnoreCase));

        public IReadOnlyList<LibraryAnimationCandidate> FindForModel(LibraryModelItem model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.OutputPath))
            {
                return Array.Empty<LibraryAnimationCandidate>();
            }

            return _byModelOutput.TryGetValue(NormalizePath(model.OutputPath), out var candidates)
                ? candidates
                : Array.Empty<LibraryAnimationCandidate>();
        }

        public int CountForModel(LibraryModelItem model)
        {
            return FindForModel(model).Count;
        }

        public int CountExplicitForModel(LibraryModelItem model)
        {
            return FindForModel(model).Count(x => x.IsExplicit);
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
            if (sqlite != null)
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
                if (!HasTable(connection, "model_animation_candidates"))
                {
                    return null;
                }

                var result = new Dictionary<string, List<LibraryAnimationCandidate>>(StringComparer.OrdinalIgnoreCase);
                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT c.model_output, c.raw_json, a.raw_json
FROM model_animation_candidates c
JOIN assets a ON a.kind = 'Animation' AND a.output = c.animation_output
ORDER BY c.model_output, c.score DESC;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var modelOutput = reader.GetString(0);
                    using var candidateDocument = JsonDocument.Parse(reader.GetString(1));
                    using var animationDocument = JsonDocument.Parse(reader.GetString(2));
                    var candidate = candidateDocument.RootElement;
                    var animation = animationDocument.RootElement;
                    if (!result.TryGetValue(NormalizePath(modelOutput), out var list))
                    {
                        list = new List<LibraryAnimationCandidate>();
                        result[NormalizePath(modelOutput)] = list;
                    }

                    list.Add(MergeCandidate(animation, candidate));
                }

                foreach (var key in result.Keys.ToArray())
                {
                    result[key] = SortCandidates(result[key]);
                }

                return new LibraryAnimationIndex(root, result);
            }
            catch (SqliteException)
            {
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static LibraryAnimationIndex LoadCompact(string path)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var animations = LoadAnimationMap(root);
            var modelOutputs = LoadModelOutputMap(root);
            var result = new Dictionary<string, List<LibraryAnimationCandidate>>(StringComparer.OrdinalIgnoreCase);

            if (!root.TryGetProperty("modelAnimationRefs", out var refs) || refs.ValueKind != JsonValueKind.Array)
            {
                    return new LibraryAnimationIndex(Path.GetDirectoryName(path) ?? "", result);
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

                    list.Add(MergeCandidate(animation, candidate));
                }

                if (list.Count > 0)
                {
                    result[NormalizePath(modelOutput)] = SortCandidates(list);
                }
            }

            return new LibraryAnimationIndex(Path.GetDirectoryName(path) ?? "", result);
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

        private static Dictionary<string, string> LoadModelOutputMap(JsonElement root)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var model in models.EnumerateArray())
            {
                var id = ReadString(model, "id");
                var output = ReadString(model, "output");
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

            if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            {
                return new LibraryAnimationIndex(Path.GetDirectoryName(path) ?? "", result);
            }

            foreach (var modelEntry in models.EnumerateArray())
            {
                var model = modelEntry.TryGetProperty("model", out var modelInfo) ? modelInfo : modelEntry;
                var output = ReadString(model, "output");
                if (string.IsNullOrWhiteSpace(output)
                    || !modelEntry.TryGetProperty("candidates", out var candidates)
                    || candidates.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var list = new List<LibraryAnimationCandidate>();
                foreach (var candidate in candidates.EnumerateArray())
                {
                    list.Add(ReadCandidate(candidate));
                }

                if (list.Count > 0)
                {
                    result[NormalizePath(output)] = SortCandidates(list);
                }
            }

            return new LibraryAnimationIndex(Path.GetDirectoryName(path) ?? "", result);
        }

        private static LibraryAnimationCandidate MergeCandidate(JsonElement animation, JsonElement candidate)
        {
            return new LibraryAnimationCandidate
            {
                Name = ReadString(animation, "name") ?? ReadString(candidate, "name") ?? "",
                OutputPath = ReadString(animation, "output") ?? ReadString(candidate, "output") ?? "",
                AnimationAssetPath = ReadString(animation, "animationAsset") ?? "",
                Source = ReadString(animation, "source") ?? "",
                AnimationType = ReadString(animation, "animationType") ?? "",
                Capability = ReadString(animation, "animationCapability") ?? "",
                Relation = ReadString(candidate, "relation") ?? ReadString(candidate, "relationSource") ?? "",
                RelationSource = ReadString(candidate, "relationSource") ?? "",
                Confidence = ReadString(candidate, "confidence") ?? "",
                Score = ReadDouble(candidate, "score"),
                Duration = ReadDouble(animation, "duration"),
                SampleRate = ReadDouble(animation, "sampleRate"),
                CurveCount = ReadInt32(animation, "curveCount"),
                MatchedPathCount = ReadArrayLength(candidate, "matchedBindingPaths"),
                RequiresHumanoidBake = ReadBool(candidate, "requiresHumanoidBake")
            };
        }

        private static LibraryAnimationCandidate ReadCandidate(JsonElement candidate)
        {
            return new LibraryAnimationCandidate
            {
                Name = ReadString(candidate, "name") ?? "",
                OutputPath = ReadString(candidate, "output") ?? "",
                AnimationAssetPath = ReadString(candidate, "animationAsset") ?? "",
                Source = ReadString(candidate, "source") ?? "",
                AnimationType = ReadString(candidate, "animationType") ?? "",
                Capability = ReadString(candidate, "animationCapability") ?? "",
                Relation = ReadString(candidate, "relation") ?? ReadString(candidate, "relationSource") ?? "",
                RelationSource = ReadString(candidate, "relationSource") ?? "",
                Confidence = ReadString(candidate, "confidence") ?? "",
                Score = ReadDouble(candidate, "score"),
                Duration = ReadDouble(candidate, "duration"),
                SampleRate = ReadDouble(candidate, "sampleRate"),
                CurveCount = ReadInt32(candidate, "curveCount"),
                MatchedPathCount = ReadArrayLength(candidate, "matchedBindingPaths"),
                RequiresHumanoidBake = ReadBool(candidate, "requiresHumanoidBake")
            };
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

                var bestPath = ReadString(animation, "output") ?? ReadString(animation, "animationAsset") ?? "";
                if (!string.IsNullOrWhiteSpace(bestPath) && existing.Contains(bestPath))
                {
                    continue;
                }

                var animationPaths = ReadStringArray(animation, "transformBindingPaths");
                var matched = CountMatchedBindingPaths(modelPaths, animationPaths);
                if (!IsTargetedMatch(matched, animationPaths.Length))
                {
                    continue;
                }

                result.Add(BuildTargetedCandidate(animation, matched));
            }

            return SortCandidates(result);
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
                        var bestPath = ReadString(animation, "output") ?? ReadString(animation, "animationAsset") ?? "";
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
                    .Where(x => IsTargetedMatch(x.Matched, ReadArrayLength(x.Animation, "transformBindingPaths")))
                    .Select(x => BuildTargetedCandidate(x.Animation, x.Matched))
                    .ToList());
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

        private static LibraryAnimationCandidate BuildTargetedCandidate(JsonElement animation, int matched)
        {
            return new LibraryAnimationCandidate
            {
                Name = ReadString(animation, "name") ?? "",
                OutputPath = ReadString(animation, "output") ?? "",
                AnimationAssetPath = ReadString(animation, "animationAsset") ?? "",
                Source = ReadString(animation, "source") ?? "",
                AnimationType = ReadString(animation, "animationType") ?? "",
                Capability = ReadString(animation, "animationCapability") ?? "",
                Relation = "animationClip.bindingPath.targetedModelMatch",
                RelationSource = "targeted",
                Confidence = "targeted_structural_needs_validation",
                Score = Math.Min(95, matched * 2),
                Duration = ReadDouble(animation, "duration"),
                SampleRate = ReadDouble(animation, "sampleRate"),
                CurveCount = ReadInt32(animation, "curveCount"),
                MatchedPathCount = matched,
                RequiresHumanoidBake = ReadBool(animation, "hasMuscleClip"),
                NeedsValidation = true
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

        private static bool IsTargetedMatch(int matchedPathCount, int animationPathCount)
        {
            if (matchedPathCount <= 0)
            {
                return false;
            }

            return animationPathCount <= 3 ? matchedPathCount == animationPathCount : matchedPathCount >= 3;
        }

        private static List<LibraryAnimationCandidate> SortCandidates(List<LibraryAnimationCandidate> candidates)
        {
            return candidates
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.MatchedPathCount)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string NormalizePath(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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
            return obj.TryGetProperty(name, out var property) && property.TryGetInt32(out var value) ? value : 0;
        }

        private static double ReadDouble(JsonElement obj, string name)
        {
            return obj.TryGetProperty(name, out var property) && property.TryGetDouble(out var value) ? value : 0;
        }

        private static bool ReadBool(JsonElement obj, string name)
        {
            return obj.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
        }

        private static int ReadArrayLength(JsonElement obj, string name)
        {
            return obj.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array ? property.GetArrayLength() : 0;
        }
    }
}
