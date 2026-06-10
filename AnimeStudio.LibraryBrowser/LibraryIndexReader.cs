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
        private static readonly string[] LibraryMarkerFiles =
        {
            "LIBRARY_README.md",
            "asset_summary.json",
            "model_animations.json",
            "model_animations.compact.json",
            "animation_bindings.jsonl",
            "export_manifest.jsonl",
            "unity_relations.jsonl",
            "library_index.db"
        };

        private static readonly HashSet<string> TextureExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png",
            ".jpg",
            ".jpeg",
            ".bmp",
            ".tga",
            ".dds",
            ".ktx",
            ".ktx2",
            ".webp"
        };

        public static void ValidateLibraryRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                throw new DirectoryNotFoundException("请选择一个存在的素材库目录。");
            }

            var catalogPath = Path.Combine(root, "asset_catalog.jsonl");
            if (!File.Exists(catalogPath))
            {
                throw new FileNotFoundException("没有找到 asset_catalog.jsonl。请选择 AnimeStudio 导出的 Library 根目录。", catalogPath);
            }

            var hasModelsDirectory = Directory.Exists(Path.Combine(root, "Models"));
            var hasLibraryMarker = LibraryMarkerFiles.Any(x => File.Exists(Path.Combine(root, x)));
            if (!hasModelsDirectory && !hasLibraryMarker)
            {
                throw new InvalidDataException(
                    "这个目录只有 asset_catalog.jsonl，缺少 Models/ 或 Library 索引/说明文件。" +
                    "请确认选择的是 AnimeStudio 导出的素材库根目录，而不是单独拷出的索引文件目录。");
            }
        }

        public static List<LibraryModelItem> LoadModels(string root)
        {
            ValidateLibraryRoot(root);

            var catalogPath = Path.Combine(root, "asset_catalog.jsonl");
            var coverageByOutput = LoadUnrealModelCoverage(root);
            var models = new List<LibraryModelItem>();
            foreach (var line in File.ReadLines(catalogPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                var obj = document.RootElement;
                var kind = ReadString(obj, "kind") ?? "";
                var sourceType = ReadString(obj, "sourceType") ?? "";
                var resourceKind = ReadString(obj, "resourceKind") ?? "Unknown";
                var isTexture = IsTextureCatalogEntry(kind, sourceType, resourceKind, obj);
                if (!string.Equals(kind, "Model", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(kind, "VFX", StringComparison.OrdinalIgnoreCase)
                    && !isTexture)
                {
                    continue;
                }

                var output = ReadString(obj, "output");
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

                var modelPreview = LibraryPathResolver.ResolveExistingFile(root, ReadString(obj, "modelPreview") ?? "");
                coverageByOutput.TryGetValue(NormalizePath(output), out var coverage);

                models.Add(new LibraryModelItem
                {
                    AssetKind = kind,
                    Name = ReadString(obj, "name") ?? Path.GetFileNameWithoutExtension(output) ?? "",
                    ResourceKind = resourceKind,
                    VfxCategory = ReadString(obj, "vfxCategory") ?? "",
                    Confidence = ReadString(obj, "confidence") ?? "",
                    Status = ReadString(obj, "status") ?? "",
                    ValidationStatus = coverage?.ValidationStatus ?? "",
                    LibraryRole = ReadString(obj, "libraryRole") ?? "",
                    SourceType = sourceType,
                    Source = ReadString(obj, "source") ?? "",
                    ObjectPath = ReadString(obj, "objectPath") ?? coverage?.ObjectPath ?? "",
                    PathId = ReadInt64(obj, "pathId"),
                    OutputPath = output,
                    ModelPreviewPath = modelPreview ?? "",
                    MeshCount = ReadInt32(obj, "meshCount"),
                    VertexCount = ReadInt32(obj, "vertexCount"),
                    MaterialCount = ReadInt32(obj, "materialCount", coverage?.MaterialCount ?? 0),
                    TextureCount = ReadInt32(obj, "textureCount", coverage?.TextureCount ?? 0),
                    ComponentCount = ReadInt32(obj, "componentCount"),
                    MaterialRefCount = ReadInt32(obj, "materialRefCount"),
                    TextureRefCount = ReadInt32(obj, "textureRefCount"),
                    TexturePreviewCount = ReadInt32(obj, "texturePreviewCount"),
                    MeshRefCount = ReadInt32(obj, "meshRefCount"),
                    OccurrenceCount = Math.Max(1, ReadInt32(obj, "occurrenceCount")),
                    BoneCount = ReadInt32(obj, "boneCount"),
                    HasSkin = coverage?.HasSkin ?? false,
                    HasSkeletonPath = coverage?.HasSkeletonPath ?? false,
                    IsStaticModel = coverage?.IsStatic ?? false,
                    ComponentReferenceCount = coverage?.ComponentReferenceCount ?? 0,
                    AnimationCandidateCount = coverage?.AnimationCandidateCount ?? 0,
                    IsTaskOrProp = coverage?.IsTaskOrProp ?? false,
                    IsPathOnlyTask = coverage?.IsPathOnlyTask ?? false,
                    MissingMaterials = coverage?.MissingMaterials ?? false,
                    NoExternalTextureSlots = coverage?.NoExternalTextureSlots ?? false,
                    NeedsReview = coverage?.NeedsReview ?? false,
                    BonePaths = ReadStringArray(obj, "bonePaths"),
                    NodePaths = ReadStringArray(obj, "nodePaths"),
                    Signals = ReadStringArray(obj, "signals"),
                    TaskSignals = coverage?.TaskSignals ?? Array.Empty<string>(),
                    VfxTexturePreviewPaths = ReadTexturePreviewPaths(root, obj, output),
                    VfxPreviewHintsJson = ReadRawJson(obj, "previewHints")
                });
            }

            AddTextureFiles(root, models);
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
                        AnimationCandidateCount = animationCandidateCount,
                        ValidationStatus = validationStatus,
                        IsTaskOrProp = isTaskOrProp,
                        IsPathOnlyTask = isTaskOrProp && componentReferenceCount == 0,
                        MissingMaterials = isTaskOrProp && materialCount == 0,
                        NoExternalTextureSlots = isTaskOrProp && textureCount == 0,
                        NeedsReview = isTaskOrProp && (
                            componentReferenceCount == 0 ||
                            materialCount == 0 ||
                            textureCount == 0 ||
                            string.Equals(validationStatus, "warning", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(validationStatus, "error", StringComparison.OrdinalIgnoreCase)),
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

                var result = new Dictionary<string, UnrealModelCoverage>(StringComparer.OrdinalIgnoreCase);
                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT output, object_path, is_static, has_skin, has_skeleton_path,
       material_count, texture_count, component_reference_count, animation_candidate_count, validation_status,
       is_task_or_prop, is_path_only_task, missing_materials, no_external_texture_slots, needs_review,
       task_signals_json
FROM model_coverage;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var output = LibraryPathResolver.ResolveExistingFile(root, reader.GetString(0));
                    if (string.IsNullOrWhiteSpace(output))
                    {
                        continue;
                    }

                    result[NormalizePath(output)] = new UnrealModelCoverage
                    {
                        ObjectPath = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        IsStatic = ReadSqliteBool(reader, 2),
                        HasSkin = ReadSqliteBool(reader, 3),
                        HasSkeletonPath = ReadSqliteBool(reader, 4),
                        MaterialCount = reader.GetInt32(5),
                        TextureCount = reader.GetInt32(6),
                        ComponentReferenceCount = reader.GetInt32(7),
                        AnimationCandidateCount = reader.GetInt32(8),
                        ValidationStatus = reader.IsDBNull(9) ? "" : reader.GetString(9),
                        IsTaskOrProp = ReadSqliteBool(reader, 10),
                        IsPathOnlyTask = ReadSqliteBool(reader, 11),
                        MissingMaterials = ReadSqliteBool(reader, 12),
                        NoExternalTextureSlots = ReadSqliteBool(reader, 13),
                        NeedsReview = ReadSqliteBool(reader, 14),
                        TaskSignals = ReadJsonStringArray(reader.IsDBNull(15) ? "" : reader.GetString(15))
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

        private static void AddTextureFiles(string root, List<LibraryModelItem> models)
        {
            var textureRoot = Path.Combine(root, "Textures");
            if (!Directory.Exists(textureRoot))
            {
                return;
            }

            var existing = models
                .Where(x => !string.IsNullOrWhiteSpace(x.OutputPath))
                .Select(x => NormalizePath(x.OutputPath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.EnumerateFiles(textureRoot, "*.*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(textureRoot, file);
                if (relative.StartsWith("_Shared" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || relative.StartsWith("_Shared" + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TextureExtensions.Contains(Path.GetExtension(file)) || !existing.Add(NormalizePath(file)))
                {
                    continue;
                }

                var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var bucket = parts.Length > 1 ? parts[0] : "Textures";
                models.Add(new LibraryModelItem
                {
                    AssetKind = "Texture",
                    Name = Path.GetFileNameWithoutExtension(file) ?? "",
                    ResourceKind = bucket,
                    SourceType = "TextureFile",
                    Source = file,
                    OutputPath = file,
                    ModelPreviewPath = file,
                    TextureCount = 1,
                    OccurrenceCount = 1,
                    Status = "file_scan_fallback"
                });
            }
        }

        private static bool IsTextureCatalogEntry(string kind, string sourceType, string resourceKind, JsonElement obj)
        {
            if (string.Equals(kind, "VfxTexturePreviewTexture", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(kind, "Texture", StringComparison.OrdinalIgnoreCase)
                || Contains(kind, "Texture2DArray")
                || Contains(kind, "MaterialTexture"))
            {
                return true;
            }

            return string.Equals(sourceType, "Texture2D", StringComparison.OrdinalIgnoreCase)
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

        private static bool ReadSqliteBool(SqliteDataReader reader, int index)
        {
            return !reader.IsDBNull(index) && reader.GetInt32(index) != 0;
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
            public int AnimationCandidateCount { get; init; }
            public string ValidationStatus { get; init; } = "";
            public bool IsTaskOrProp { get; init; }
            public bool IsPathOnlyTask { get; init; }
            public bool MissingMaterials { get; init; }
            public bool NoExternalTextureSlots { get; init; }
            public bool NeedsReview { get; init; }
            public string[] TaskSignals { get; init; } = Array.Empty<string>();
        }
    }
}
