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
        private readonly Dictionary<string, IReadOnlyList<LibraryAnimationCandidate>> _targetedCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _root;
        private readonly object _cacheLock = new();

        private LibraryAnimationIndex(
            string root,
            Dictionary<string, List<LibraryAnimationCandidate>> byModelOutput,
            IEnumerable<LibraryAnimationCandidate> allAnimations = null,
            string loadSource = "",
            int indexedCandidateCount = 0)
        {
            _root = root;
            _byModelOutput = byModelOutput;
            _byAnimationKey = BuildAnimationUsageMap(byModelOutput, allAnimations);
            LoadSource = loadSource;
            IndexedModelCount = byModelOutput.Count;
            IndexedCandidateCount = indexedCandidateCount > 0
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

            return _byModelOutput.TryGetValue(NormalizePath(model.OutputPath), out var candidates)
                ? candidates
                : Array.Empty<LibraryAnimationCandidate>();
        }

        public int CountForModel(LibraryModelItem model)
        {
            return CountUsableForModel(model);
        }

        public int CountAllForModel(LibraryModelItem model)
        {
            return FindForModel(model).Count;
        }

        public int CountUsableForModel(LibraryModelItem model)
        {
            return FindForModel(model).Count(x => x.IsUsableCandidate);
        }

        public int CountExplicitForModel(LibraryModelItem model)
        {
            return FindForModel(model).Count(x => x.IsExplicit);
        }

        public IReadOnlyList<LibraryAnimationUsage> FindAllAnimations()
        {
            return _byAnimationKey.Values
                .OrderBy(x => x.Animation.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Animation.BestPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
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
                var result = new Dictionary<string, List<LibraryAnimationCandidate>>(StringComparer.OrdinalIgnoreCase);
                if (HasTable(connection, "model_animation_candidates"))
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = @"
SELECT c.model_output, c.raw_json, a.raw_json
FROM model_animation_candidates c
JOIN assets a ON a.kind = 'Animation' AND a.output = c.animation_output
ORDER BY c.model_output, c.score DESC;";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var modelOutput = LibraryPathResolver.ResolveExistingFile(root, reader.GetString(0));
                        using var candidateDocument = JsonDocument.Parse(reader.GetString(1));
                        using var animationDocument = JsonDocument.Parse(reader.GetString(2));
                        var candidate = candidateDocument.RootElement;
                        var animation = animationDocument.RootElement;
                        if (!result.TryGetValue(NormalizePath(modelOutput), out var list))
                        {
                            list = new List<LibraryAnimationCandidate>();
                            result[NormalizePath(modelOutput)] = list;
                        }

                        list.Add(MergeCandidate(root, animation, candidate));
                    }
                }
                else if (HasTable(connection, "model_animation_relations") && HasTable(connection, "relation_animations"))
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = @"
SELECT mar.model, mar.raw_json, ra.raw_json
FROM model_animation_relations mar
JOIN relation_animations ra ON ra.relation_id = mar.id
ORDER BY mar.model, ra.name;";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var modelOutput = LibraryPathResolver.ResolveExistingFile(root, reader.GetString(0));
                        using var relationDocument = JsonDocument.Parse(reader.GetString(1));
                        using var animationDocument = JsonDocument.Parse(reader.GetString(2));
                        AddCandidate(result, modelOutput, ReadUnrealRelationAnimation(root, relationDocument.RootElement, animationDocument.RootElement));
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
            catch (SqliteException)
            {
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
            catch (Exception) when (!System.Diagnostics.Debugger.IsAttached)
            {
                return null;
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

                    list.Add(MergeCandidate(libraryRoot, animation, candidate));
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
                        list.Add(MergeCandidate(root, animation, candidate));
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
                    list.Add(ReadCandidate(Path.GetDirectoryName(path) ?? "", candidate));
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
                    AddCandidate(result, modelOutput, ReadUnrealRelationAnimation(libraryRoot, relation, animation));
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
        {
            return new LibraryAnimationCandidate
            {
                Name = ReadString(animation, "name") ?? ReadString(candidate, "name") ?? "",
                OutputPath = LibraryPathResolver.ResolveExistingFile(libraryRoot, ReadString(animation, "output") ?? ReadString(candidate, "output") ?? ""),
                AnimationAssetPath = LibraryPathResolver.ResolveExistingFile(libraryRoot, ReadString(animation, "animationAsset") ?? ""),
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
                FrameCount = ReadInt32(animation, "frameCount"),
                TrackCount = ReadInt32(animation, "trackCount"),
                SegmentCount = ReadInt32(animation, "segmentCount"),
                MatchedPathCount = ReadArrayLength(candidate, "matchedBindingPaths"),
                TrackCoverage = ReadDouble(animation, "trackCoverage"),
                ValidationStatus = ReadString(animation, "validationStatus") ?? "",
                ValidationCategory = ReadString(animation, "validationCategory") ?? "",
                ValidationReason = ReadString(animation, "validationReason") ?? "",
                RequiresHumanoidBake = ReadBool(candidate, "requiresHumanoidBake"),
                IsContainerAnimation = ReadBool(animation, "isContainerAnimation"),
                BindingPaths = ReadStringArray(animation, "transformBindingPaths")
            };
        }

        private static LibraryAnimationCandidate ReadCandidate(string libraryRoot, JsonElement candidate)
        {
            return new LibraryAnimationCandidate
            {
                Name = ReadString(candidate, "name") ?? "",
                OutputPath = LibraryPathResolver.ResolveExistingFile(libraryRoot, ReadString(candidate, "output") ?? ""),
                AnimationAssetPath = LibraryPathResolver.ResolveExistingFile(libraryRoot, ReadString(candidate, "animationAsset") ?? ""),
                Source = ReadString(candidate, "source") ?? "",
                AnimationType = ReadString(candidate, "animationType") ?? "",
                Capability = ReadString(candidate, "animationCapability") ?? "",
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
                IsContainerAnimation = ReadBool(candidate, "isContainerAnimation"),
                BindingPaths = ReadStringArray(candidate, "transformBindingPaths")
            };
        }

        private static LibraryAnimationCandidate ReadAnimation(string libraryRoot, JsonElement animation)
        {
            return new LibraryAnimationCandidate
            {
                Name = ReadString(animation, "name") ?? "",
                OutputPath = LibraryPathResolver.ResolveExistingFile(libraryRoot, ReadString(animation, "output") ?? ""),
                AnimationAssetPath = LibraryPathResolver.ResolveExistingFile(libraryRoot, ReadString(animation, "animationAsset") ?? ""),
                Source = ReadString(animation, "source") ?? "",
                AnimationType = ReadString(animation, "animationType") ?? "",
                Capability = ReadString(animation, "animationCapability") ?? "",
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
                IsContainerAnimation = ReadBool(animation, "isContainerAnimation"),
                BindingPaths = ReadStringArray(animation, "transformBindingPaths")
            };
        }

        private static LibraryAnimationCandidate ReadUnrealRelationAnimation(string libraryRoot, JsonElement relation, JsonElement animation)
        {
            var exportStatus = ReadString(animation, "status") ?? "";
            var validationStatus = ReadString(animation, "validationStatus") ?? exportStatus;
            var validationCategory = ReadString(animation, "validationCategory") ?? "";
            var confidence = ReadString(relation, "confidence") ?? "";
            var trackCoverage = ReadDouble(animation, "trackCoverage");
            var trackCount = ReadInt32(animation, "trackCount");
            var isUsableCandidate = ReadBool(animation, "isUsableCandidate")
                || ((string.IsNullOrWhiteSpace(exportStatus) || string.Equals(exportStatus, "ok", StringComparison.OrdinalIgnoreCase))
                    && !string.Equals(validationStatus, "error", StringComparison.OrdinalIgnoreCase));
            var relationSource = confidence.Contains("Explicit", StringComparison.OrdinalIgnoreCase)
                || string.Equals(confidence, "ComponentOwner", StringComparison.OrdinalIgnoreCase)
                    ? "explicit"
                    : "unreal";

            return new LibraryAnimationCandidate
            {
                Name = ReadString(animation, "name") ?? "",
                OutputPath = LibraryPathResolver.ResolveExistingFile(libraryRoot, ReadString(animation, "output") ?? ""),
                Source = ReadString(animation, "source") ?? "",
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

        private static LibraryAnimationCandidate BuildTargetedCandidate(string libraryRoot, JsonElement animation, int matched)
        {
            return new LibraryAnimationCandidate
            {
                Name = ReadString(animation, "name") ?? "",
                OutputPath = LibraryPathResolver.ResolveExistingFile(libraryRoot, ReadString(animation, "output") ?? ""),
                AnimationAssetPath = LibraryPathResolver.ResolveExistingFile(libraryRoot, ReadString(animation, "animationAsset") ?? ""),
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
                NeedsValidation = true,
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
            IEnumerable<LibraryAnimationCandidate> allAnimations)
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
    }
}
