using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AnimeStudio.LibraryBrowser
{
    internal static class LibraryIndexReader
    {
        public static void ValidateLibraryRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                throw new DirectoryNotFoundException("请选择一个存在的素材库目录。");
            }

            var dbPath = Path.Combine(root, "library_index.db");
            if (!File.Exists(dbPath))
            {
                throw new FileNotFoundException(
                    "没有找到 library_index.db。AnimeStudio.LibraryBrowser 生产模式只读取 SQLite 索引，请重新导出或重建素材库索引。",
                    dbPath);
            }

            var hasModelsDirectory = Directory.Exists(Path.Combine(root, "Models"));
            if (!hasModelsDirectory)
            {
                throw new InvalidDataException(
                    "这个目录有 library_index.db，但缺少 Models/。" +
                    "请确认选择的是 AnimeStudio 导出的素材库根目录，而不是单独拷出的索引文件目录。");
            }
        }

        public static List<LibraryModelItem> LoadModels(string root)
        {
            ValidateLibraryRoot(root);

            var coverageByOutput = LoadUnrealModelCoverage(root);
            return LoadModelsFromSqlite(root, coverageByOutput);
        }

        private static List<LibraryModelItem> LoadModelsFromSqlite(
            string root,
            IReadOnlyDictionary<string, UnrealModelCoverage> coverageByOutput)
        {
            var dbPath = Path.Combine(root, "library_index.db");

            SQLitePCL.Batteries_V2.Init();
            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();
            if (!HasTable(connection, "assets") || !HasColumn(connection, "assets", "raw_json"))
            {
                throw new InvalidDataException("library_index.db 缺少 assets.raw_json，不能作为 Browser 生产索引读取。");
            }

            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT kind, resource_kind, name, source_type, source, path_id, output,
       json_extract(raw_json, '$.libraryRole'),
       json_extract(raw_json, '$.confidence'),
       json_extract(raw_json, '$.status'),
       json_extract(raw_json, '$.modelPreview'),
       json_extract(raw_json, '$.objectPath'),
       json_extract(raw_json, '$.vfxCategory'),
       json_extract(raw_json, '$.meshCount'),
       json_extract(raw_json, '$.vertexCount'),
       json_extract(raw_json, '$.materialCount'),
       json_extract(raw_json, '$.textureCount'),
       json_extract(raw_json, '$.componentCount'),
       json_extract(raw_json, '$.materialRefCount'),
       json_extract(raw_json, '$.textureRefCount'),
       json_extract(raw_json, '$.texturePreviewCount'),
       json_extract(raw_json, '$.meshRefCount'),
       json_extract(raw_json, '$.occurrenceCount'),
       json_extract(raw_json, '$.boneCount'),
       json_extract(raw_json, '$.bonePaths'),
       json_extract(raw_json, '$.nodePaths'),
       json_extract(raw_json, '$.signals'),
       json_extract(raw_json, '$.previewHints')
FROM assets
WHERE kind IN ('Model', 'VFX', 'Texture')
   OR source_type IN ('Texture2D', 'Texture2DArray')
   OR resource_kind LIKE '%Texture%'
ORDER BY kind, resource_kind, name;";

            var models = new List<LibraryModelItem>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var kind = ReadSqliteString(reader, 0) ?? "";
                var resourceKind = ReadSqliteString(reader, 1) ?? "Unknown";
                var sourceType = ReadSqliteString(reader, 3) ?? "";
                var isTexture = IsTextureCatalogEntry(kind, sourceType, resourceKind);
                if (!string.Equals(kind, "Model", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(kind, "VFX", StringComparison.OrdinalIgnoreCase)
                    && !isTexture)
                {
                    continue;
                }

                var output = ReadSqliteString(reader, 6);
                if (string.IsNullOrWhiteSpace(output))
                {
                    continue;
                }

                var isVfx = string.Equals(kind, "VFX", StringComparison.OrdinalIgnoreCase);
                if (isVfx)
                {
                    output = LibraryPathResolver.ResolveExistingDirectory(root, output);
                    if (!Directory.Exists(output))
                    {
                        continue;
                    }
                }
                else if (!File.Exists(output))
                {
                    output = LibraryPathResolver.ResolveExistingFile(root, output);
                    if (!File.Exists(output))
                    {
                        continue;
                    }
                }

                var modelPreview = LibraryPathResolver.ResolveExistingFile(root, ReadSqliteString(reader, 10) ?? "");
                UnrealModelCoverage coverage = null;
                coverageByOutput?.TryGetValue(NormalizePath(output), out coverage);
                models.Add(new LibraryModelItem
                {
                    AssetKind = kind,
                    Name = ReadSqliteString(reader, 2) ?? Path.GetFileNameWithoutExtension(output) ?? "",
                    ResourceKind = resourceKind,
                    VfxCategory = ReadSqliteString(reader, 12) ?? "",
                    Confidence = ReadSqliteString(reader, 8) ?? "",
                    Status = ReadSqliteString(reader, 9) ?? "",
                    ValidationStatus = coverage?.ValidationStatus ?? "",
                    LibraryRole = ReadSqliteString(reader, 7) ?? "",
                    SourceType = sourceType,
                    Source = ReadSqliteString(reader, 4) ?? "",
                    ObjectPath = ReadSqliteString(reader, 11) ?? coverage?.ObjectPath ?? "",
                    PathId = ReadSqliteInt64(reader, 5),
                    OutputPath = output,
                    ModelPreviewPath = modelPreview ?? "",
                    MeshCount = ReadSqliteInt32(reader, 13),
                    VertexCount = ReadSqliteInt32(reader, 14),
                    MaterialCount = ReadSqliteInt32(reader, 15, coverage?.MaterialCount ?? 0),
                    TextureCount = ReadSqliteInt32(reader, 16, coverage?.TextureCount ?? 0),
                    ComponentCount = ReadSqliteInt32(reader, 17),
                    MaterialRefCount = ReadSqliteInt32(reader, 18),
                    TextureRefCount = ReadSqliteInt32(reader, 19),
                    TexturePreviewCount = ReadSqliteInt32(reader, 20),
                    MeshRefCount = ReadSqliteInt32(reader, 21),
                    OccurrenceCount = Math.Max(1, ReadSqliteInt32(reader, 22)),
                    BoneCount = ReadSqliteInt32(reader, 23),
                    HasSkin = coverage?.HasSkin ?? false,
                    HasSkeletonPath = coverage?.HasSkeletonPath ?? false,
                    IsStaticModel = coverage?.IsStatic ?? false,
                    ComponentReferenceCount = coverage?.ComponentReferenceCount ?? 0,
                    SourceIndexObjectCount = coverage?.SourceIndexObjectCount ?? 0,
                    AnimationCandidateCount = coverage?.AnimationCandidateCount ?? 0,
                    IsTaskOrProp = coverage?.IsTaskOrProp ?? false,
                    IsPathOnlyTask = coverage?.IsPathOnlyTask ?? false,
                    MissingMaterials = coverage?.MissingMaterials ?? false,
                    NoExternalTextureSlots = coverage?.NoExternalTextureSlots ?? false,
                    NeedsReview = coverage?.NeedsReview ?? false,
                    ReviewReasons = coverage?.ReviewReasons ?? Array.Empty<string>(),
                    RelationNeedsReview = coverage?.RelationNeedsReview ?? false,
                    RelationReviewReasons = coverage?.RelationReviewReasons ?? Array.Empty<string>(),
                    BonePaths = ReadJsonStringArray(ReadSqliteString(reader, 24)),
                    NodePaths = ReadJsonStringArray(ReadSqliteString(reader, 25)),
                    Signals = ReadJsonStringArray(ReadSqliteString(reader, 26)),
                    TaskSignals = coverage?.TaskSignals ?? Array.Empty<string>(),
                    VfxTexturePreviewPaths = Array.Empty<string>(),
                    VfxPreviewHintsJson = ReadSqliteString(reader, 27) ?? ""
                });
            }

            return models;
        }

        private static Dictionary<string, UnrealModelCoverage> LoadUnrealModelCoverage(string root)
        {
            var sqliteCoverage = LoadUnrealModelCoverageFromSqlite(root);
            if (sqliteCoverage != null && sqliteCoverage.Count > 0)
            {
                return sqliteCoverage;
            }

            var path = Path.Combine(root, "model_coverage.json");
            if (!File.Exists(path))
            {
                return new Dictionary<string, UnrealModelCoverage>(StringComparer.OrdinalIgnoreCase);
            }

            var result = new Dictionary<string, UnrealModelCoverage>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (!document.RootElement.TryGetProperty("models", out var models)
                    || models.ValueKind != JsonValueKind.Array)
                {
                    return result;
                }

                foreach (var model in models.EnumerateArray())
                {
                    var output = LibraryPathResolver.ResolveExistingFile(root, ReadString(model, "Output") ?? "");
                    if (string.IsNullOrWhiteSpace(output))
                    {
                        continue;
                    }

                    var materialCount = ReadInt32(model, "MaterialCount");
                    var textureCount = ReadInt32(model, "TextureCount");
                    var componentReferenceCount = ReadInt32(model, "ComponentReferenceCount");
                    var sourceIndexObjectCount = ReadInt32(model, "SourceIndexObjectCount");
                    var animationCandidateCount = ReadInt32(model, "AnimationCandidateCount");
                    var resourceKind = ReadString(model, "ResourceKind") ?? "";
                    var validationStatus = ReadString(model, "ValidationStatus") ?? "";
                    var taskSignals = ReadStringArray(model, "TaskSignals");
                    var isTaskOrProp = taskSignals.Length > 0 || string.Equals(resourceKind, "Prop", StringComparison.OrdinalIgnoreCase);

                    result[NormalizePath(output)] = new UnrealModelCoverage
                    {
                        ObjectPath = ReadString(model, "ObjectPath") ?? "",
                        IsStatic = ReadBool(model, "IsStatic"),
                        HasSkin = ReadBool(model, "HasSkin"),
                        HasSkeletonPath = ReadBool(model, "HasSkeletonPath"),
                        MaterialCount = materialCount,
                        TextureCount = textureCount,
                        ComponentReferenceCount = componentReferenceCount,
                        SourceIndexObjectCount = sourceIndexObjectCount,
                        AnimationCandidateCount = animationCandidateCount,
                        ValidationStatus = validationStatus,
                        IsTaskOrProp = isTaskOrProp,
                        IsPathOnlyTask = isTaskOrProp && componentReferenceCount == 0 && sourceIndexObjectCount == 0,
                        MissingMaterials = isTaskOrProp && materialCount == 0,
                        NoExternalTextureSlots = isTaskOrProp && textureCount == 0,
                        NeedsReview = isTaskOrProp && (
                            materialCount == 0 ||
                            textureCount == 0 ||
                            string.Equals(validationStatus, "warning", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(validationStatus, "error", StringComparison.OrdinalIgnoreCase)),
                        ReviewReasons = BuildLegacyReviewReasons(isTaskOrProp, materialCount, textureCount, validationStatus),
                        RelationNeedsReview = isTaskOrProp && componentReferenceCount == 0 && sourceIndexObjectCount == 0,
                        RelationReviewReasons = isTaskOrProp && componentReferenceCount == 0 && sourceIndexObjectCount == 0
                            ? new[] { "pathOnlyRelation" }
                            : Array.Empty<string>(),
                        TaskSignals = taskSignals
                    };
                }
            }
            catch (JsonException)
            {
            }

            return result;
        }

        private static Dictionary<string, UnrealModelCoverage> LoadUnrealModelCoverageFromSqlite(string root)
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
                if (!HasTable(connection, "model_coverage")
                    || !HasColumn(connection, "model_coverage", "is_task_or_prop")
                    || !HasColumn(connection, "model_coverage", "validation_status")
                    || !HasColumn(connection, "model_coverage", "needs_review"))
                {
                    return null;
                }

                var hasSourceIndexObjectCount = HasColumn(connection, "model_coverage", "source_index_object_count");
                var hasReviewReasonColumns = HasColumn(connection, "model_coverage", "review_reasons_json")
                    && HasColumn(connection, "model_coverage", "relation_needs_review")
                    && HasColumn(connection, "model_coverage", "relation_review_reasons_json");
                var result = new Dictionary<string, UnrealModelCoverage>(StringComparer.OrdinalIgnoreCase);
                using var command = connection.CreateCommand();
                command.CommandText = hasSourceIndexObjectCount && hasReviewReasonColumns
                    ? @"
SELECT output, object_path, is_static, has_skin, has_skeleton_path,
       material_count, texture_count, component_reference_count, source_index_object_count, animation_candidate_count, validation_status,
       is_task_or_prop, is_path_only_task, missing_materials, no_external_texture_slots, needs_review,
       task_signals_json, review_reasons_json, relation_needs_review, relation_review_reasons_json
FROM model_coverage;"
                    : hasSourceIndexObjectCount
                    ? @"
SELECT output, object_path, is_static, has_skin, has_skeleton_path,
       material_count, texture_count, component_reference_count, source_index_object_count, animation_candidate_count, validation_status,
       is_task_or_prop, is_path_only_task, missing_materials, no_external_texture_slots, needs_review,
       task_signals_json, '[]' AS review_reasons_json, 0 AS relation_needs_review, '[]' AS relation_review_reasons_json
FROM model_coverage;"
                    : @"
SELECT output, object_path, is_static, has_skin, has_skeleton_path,
       material_count, texture_count, component_reference_count, 0 AS source_index_object_count, animation_candidate_count, validation_status,
       is_task_or_prop, is_path_only_task, missing_materials, no_external_texture_slots, needs_review,
       task_signals_json, '[]' AS review_reasons_json, 0 AS relation_needs_review, '[]' AS relation_review_reasons_json
FROM model_coverage;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var output = LibraryPathResolver.ResolveExistingFile(root, reader.GetString(0));
                    if (string.IsNullOrWhiteSpace(output))
                    {
                        continue;
                    }

                    var validationStatus = reader.IsDBNull(10) ? "" : reader.GetString(10);
                    var isTaskOrProp = ReadSqliteBool(reader, 11);
                    var componentReferenceCount = reader.GetInt32(7);
                    var sourceIndexObjectCount = reader.GetInt32(8);
                    var materialCount = reader.GetInt32(5);
                    var textureCount = reader.GetInt32(6);
                    var reviewReasons = ReadJsonStringArray(reader.IsDBNull(17) ? "" : reader.GetString(17));
                    if (reviewReasons.Length == 0)
                    {
                        reviewReasons = BuildLegacyReviewReasons(isTaskOrProp, materialCount, textureCount, validationStatus);
                    }

                    var relationNeedsReview = ReadSqliteBool(reader, 18);
                    var relationReviewReasons = ReadJsonStringArray(reader.IsDBNull(19) ? "" : reader.GetString(19));
                    if (isTaskOrProp && relationReviewReasons.Length == 0 && componentReferenceCount == 0 && sourceIndexObjectCount == 0)
                    {
                        relationNeedsReview = true;
                        relationReviewReasons = new[] { "pathOnlyRelation" };
                    }

                    result[NormalizePath(output)] = new UnrealModelCoverage
                    {
                        ObjectPath = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        IsStatic = ReadSqliteBool(reader, 2),
                        HasSkin = ReadSqliteBool(reader, 3),
                        HasSkeletonPath = ReadSqliteBool(reader, 4),
                        MaterialCount = materialCount,
                        TextureCount = textureCount,
                        ComponentReferenceCount = componentReferenceCount,
                        SourceIndexObjectCount = sourceIndexObjectCount,
                        AnimationCandidateCount = reader.GetInt32(9),
                        ValidationStatus = validationStatus,
                        IsTaskOrProp = isTaskOrProp,
                        IsPathOnlyTask = ReadSqliteBool(reader, 12) && sourceIndexObjectCount == 0,
                        MissingMaterials = ReadSqliteBool(reader, 13),
                        NoExternalTextureSlots = ReadSqliteBool(reader, 14),
                        NeedsReview = ReadSqliteBool(reader, 15),
                        TaskSignals = ReadJsonStringArray(reader.IsDBNull(16) ? "" : reader.GetString(16)),
                        ReviewReasons = reviewReasons,
                        RelationNeedsReview = relationNeedsReview,
                        RelationReviewReasons = relationReviewReasons
                    };
                }

                return result;
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

        private static bool IsTextureCatalogEntry(string kind, string sourceType, string resourceKind)
        {
            if (string.Equals(kind, "VfxTexturePreviewTexture", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return string.Equals(kind, "Texture", StringComparison.OrdinalIgnoreCase)
                || Contains(kind, "Texture2DArray")
                || Contains(kind, "MaterialTexture")
                || string.Equals(sourceType, "Texture2D", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sourceType, "Texture2DArray", StringComparison.OrdinalIgnoreCase)
                || Contains(resourceKind, "Texture");
        }

        private static bool Contains(string value, string text)
        {
            return value?.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool StringEquals(JsonElement obj, string name, string value)
        {
            return obj.TryGetProperty(name, out var property)
                && property.ValueKind == JsonValueKind.String
                && string.Equals(property.GetString(), value, StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadString(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
        }

        private static int ReadInt32(JsonElement obj, string name, int fallback = 0)
        {
            return obj.TryGetProperty(name, out var property)
                && property.ValueKind == JsonValueKind.Number
                && property.TryGetInt32(out var value)
                    ? value
                    : fallback;
        }

        private static bool ReadBool(JsonElement obj, string name)
        {
            return obj.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
        }

        private static string ReadRawJson(JsonElement obj, string name)
        {
            return obj.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null
                ? property.GetRawText()
                : "";
        }

        private static long ReadInt64(JsonElement obj, string name)
        {
            return obj.TryGetProperty(name, out var property)
                && property.ValueKind == JsonValueKind.Number
                && property.TryGetInt64(out var value)
                    ? value
                    : 0;
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

        private static string[] ReadJsonStringArray(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return Array.Empty<string>();
            }

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            return document.RootElement.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();
        }

        private static string[] BuildLegacyReviewReasons(bool isTaskOrProp, int materialCount, int textureCount, string validationStatus)
        {
            if (!isTaskOrProp)
            {
                return Array.Empty<string>();
            }

            var reasons = new List<string>();
            if (string.Equals(validationStatus, "warning", StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("modelValidationWarning");
            }

            if (string.Equals(validationStatus, "error", StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("modelValidationError");
            }

            if (materialCount == 0)
            {
                reasons.Add("missingMaterials");
            }

            if (textureCount == 0)
            {
                reasons.Add("noExternalTextureSlots");
            }

            return reasons.ToArray();
        }

        private static bool ReadSqliteBool(SqliteDataReader reader, int index)
        {
            return !reader.IsDBNull(index) && reader.GetInt32(index) != 0;
        }

        private static string ReadSqliteString(SqliteDataReader reader, int index)
        {
            return reader.IsDBNull(index) ? null : reader.GetString(index);
        }

        private static int ReadSqliteInt32(SqliteDataReader reader, int index, int fallback = 0)
        {
            return reader.IsDBNull(index) ? fallback : Convert.ToInt32(reader.GetValue(index));
        }

        private static long ReadSqliteInt64(SqliteDataReader reader, int index, long fallback = 0)
        {
            return reader.IsDBNull(index) ? fallback : reader.GetInt64(index);
        }

        private static bool HasTable(SqliteConnection connection, string name)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
            command.Parameters.AddWithValue("$name", name);
            return Convert.ToInt32(command.ExecuteScalar()) > 0;
        }

        private static bool HasColumn(SqliteConnection connection, string table, string name)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({table});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(1) && string.Equals(reader.GetString(1), name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string[] ReadTexturePreviewPaths(string libraryRoot, JsonElement obj, string outputDirectory)
        {
            if (!obj.TryGetProperty("texturePreviews", out var property) || property.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var candidates = property.EnumerateArray()
                .Select(x =>
                {
                    if (x.ValueKind != JsonValueKind.Object)
                    {
                        return null;
                    }

                    var output = LibraryPathResolver.ResolveExistingFile(libraryRoot, ReadString(x, "output"));
                    if (!string.IsNullOrWhiteSpace(output) && File.Exists(output))
                    {
                        return new VfxTexturePreviewCandidate(output, ReadString(x, "slot"));
                    }

                    var relative = ReadString(x, "relativePath");
                    if (!string.IsNullOrWhiteSpace(relative) && Directory.Exists(outputDirectory))
                    {
                        var path = Path.Combine(outputDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
                        return File.Exists(path) ? new VfxTexturePreviewCandidate(path, ReadString(x, "slot")) : null;
                    }

                    return null;
                })
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Path))
                .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .OrderByDescending(ScoreVfxTexturePreview)
                .ThenBy(x => Path.GetFileName(x.Path), StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (candidates.Length == 0 || ScoreVfxTexturePreview(candidates[0]) < 0)
            {
                return Array.Empty<string>();
            }

            return candidates
                .Where(x => ScoreVfxTexturePreview(x) >= 0)
                .Select(x => x.Path)
                .ToArray();
        }

        private sealed class VfxTexturePreviewCandidate
        {
            public VfxTexturePreviewCandidate(string path, string slot)
            {
                Path = path;
                Slot = slot ?? "";
            }

            public string Path { get; }
            public string Slot { get; }
        }

        private static int ScoreVfxTexturePreview(VfxTexturePreviewCandidate candidate)
        {
            var score = ScoreVfxTexturePreviewPath(candidate.Path);
            var slot = candidate.Slot?.ToLowerInvariant() ?? "";

            score += ContainsAny(slot, "maintex", "basemap", "basecolor", "basecolormap", "albedo", "diffuse", "color", "colour", "txbase", "_base") ? 50 : 0;
            score += ContainsAny(slot, "alpha", "dissolve", "edge", "gradient", "ramp", "mask") ? 16 : 0;
            score += ContainsAny(slot, "normal", "cheaplitnormal") ? -80 : 0;
            score += ContainsAny(slot, "distort", "disnoise", "noise", "flow", "motion", "vector") ? -45 : 0;

            return score;
        }

        private static int ScoreVfxTexturePreviewPath(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path)?.ToLowerInvariant() ?? "";
            var score = 0;

            score += ContainsAny(name, "particle", "vfx", "fx", "decal", "sprite") ? 24 : 0;
            score += ContainsAny(name, "glow", "slash", "smoke", "fire", "spark", "trail", "beam", "ring", "circle", "wave", "shock", "impact", "orb", "flare", "blood", "dust", "cloud", "ripple", "swirl", "aura", "light", "line", "streak") ? 18 : 0;
            score += ContainsAny(name, "albedo", "base", "basecolor", "diffuse", "color", "colour", "_bc", "_d", "_c", "_a", "alpha") ? 12 : 0;

            score -= ContainsAny(name, "normal", "_n", "mask", "_m", "metal", "rough", "smooth", "ao", "height", "data", "lut", "flow", "motion", "vector") ? 28 : 0;
            score -= ContainsAny(name, "noise", "distort", "distortion", "dissolve", "disnoise", "voronoi", "gradient", "ramp") ? 12 : 0;
            score -= ContainsAny(name, "terrain", "ground", "surface", "tile", "palette") ? 8 : 0;

            return score;
        }

        private static bool ContainsAny(string value, params string[] needles)
        {
            return needles.Any(value.Contains);
        }

        private static string NormalizePath(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private sealed class UnrealModelCoverage
        {
            public string ObjectPath { get; init; } = "";
            public bool IsStatic { get; init; }
            public bool HasSkin { get; init; }
            public bool HasSkeletonPath { get; init; }
            public int MaterialCount { get; init; }
            public int TextureCount { get; init; }
            public int ComponentReferenceCount { get; init; }
            public int SourceIndexObjectCount { get; init; }
            public int AnimationCandidateCount { get; init; }
            public string ValidationStatus { get; init; } = "";
            public bool IsTaskOrProp { get; init; }
            public bool IsPathOnlyTask { get; init; }
            public bool MissingMaterials { get; init; }
            public bool NoExternalTextureSlots { get; init; }
            public bool NeedsReview { get; init; }
            public string[] ReviewReasons { get; init; } = Array.Empty<string>();
            public bool RelationNeedsReview { get; init; }
            public string[] RelationReviewReasons { get; init; } = Array.Empty<string>();
            public string[] TaskSignals { get; init; } = Array.Empty<string>();
        }
    }
}
