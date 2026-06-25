using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AnimeStudio.CLI
{
    public static class SQLiteLibraryIndexBuilder
    {
        private const int SummaryQueryTimeoutSeconds = 60;

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

        private static readonly HashSet<string> TextureFileExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png",
            ".jpg",
            ".jpeg",
            ".bmp",
            ".tga",
            ".dds",
            ".ktx",
            ".ktx2",
            ".webp",
            ".rawtex"
        };

        public static string Build(
            string exportRoot,
            string indexPath = null,
            bool skipFileIndex = false,
            bool skipSidecarScan = false,
            bool skipJsonDocuments = false,
            string sourceIndexPath = null,
            string sourceGame = null)
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
            var preservedBakeCache = LoadExistingAnimationBakeCacheRows(dbPath);
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }

            var totalWatch = Stopwatch.StartNew();
            Logger.Info($"Building SQLite library index: {dbPath}");
            var resolvedSourceGame = FirstNonEmpty(sourceGame, ReadExistingAssetLibrarySourceGame(root), string.Empty);
            var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();
            Execute(connection, "PRAGMA journal_mode = WAL;");
            Execute(connection, "PRAGMA synchronous = NORMAL;");

            long RunCountStage(string name, Func<long> work)
            {
                var watch = Stopwatch.StartNew();
                var count = work();
                Logger.Info($"SQLite index stage '{name}' finished in {watch.Elapsed.TotalSeconds:F1}s; rows={count}");
                return count;
            }

            void RunStage(string name, Action work)
            {
                var watch = Stopwatch.StartNew();
                work();
                Logger.Info($"SQLite index stage '{name}' finished in {watch.Elapsed.TotalSeconds:F1}s");
            }

            RunStage("create schema", () => CreateSchema(connection));

            using var transaction = connection.BeginTransaction();
            InsertMetadata(connection, transaction, "schemaVersion", "1");
            InsertMetadata(connection, transaction, "libraryKind", "AssetLibrary");
            InsertMetadata(connection, transaction, "sourceTool", "AnimeStudio");
            InsertMetadata(connection, transaction, "sourceGame", resolvedSourceGame);
            InsertMetadata(connection, transaction, "capabilities", "{\"models\":false,\"animations\":false,\"animationPreviewComposer\":null}");
            InsertMetadata(connection, transaction, "root", root);
            InsertMetadata(connection, transaction, "createdUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            InsertMetadata(connection, transaction, "rule", "索引要全，导出要精。SQLite v1 indexes exported Library/AudioLibrary artifacts; raw Unity source graph indexing will extend this schema later.");

            var assetCatalogRows = RunCountStage("asset catalog", () => ImportAssetCatalog(connection, transaction, root));
            counts["textureAssets"] = RunCountStage("texture assets", () => ImportTextureAssets(connection, transaction, root));
            counts["assets"] = assetCatalogRows + counts["textureAssets"];
            counts["assetCatalog"] = assetCatalogRows;
            counts["textureLinks"] = RunCountStage("texture links", () => ImportTextureLinks(connection, transaction, root));
            counts["materialSidecars"] = RunCountStage("material sidecars", () => ImportMaterialSidecars(connection, transaction, root));
            counts["modelValidation"] = RunCountStage("model validation", () => ImportModelValidation(connection, transaction, root));
            counts["modelBindingPaths"] = RunCountStage("model binding paths", () => ImportModelBindingPaths(connection, transaction, root));
            counts["unityAssets"] = 0;
            counts["unityRelations"] = 0;
            counts["animationBindings"] = 0;
            counts["animationBindingPaths"] = 0;
            RunStage("unity relations", () => ImportUnityRelations(connection, transaction, root, counts));
            counts["modelAnimationCandidates"] = RunCountStage("model animation candidates sidecar", () => ImportModelAnimationCandidates(connection, transaction, root));
            counts["explicitSourceModelAnimationCandidates"] = RunCountStage("model animation candidates source index", () => ImportExplicitModelAnimationCandidatesFromSourceIndex(connection, transaction, root, sourceIndexPath));
            counts["modelAnimationCandidates"] += counts["explicitSourceModelAnimationCandidates"];
            counts["exportManifest"] = RunCountStage("export manifest", () => ImportExportManifest(connection, transaction, root));
            counts["jsonDocuments"] = skipJsonDocuments ? 0 : RunCountStage("json documents", () => ImportJsonDocuments(connection, transaction, root));
            counts["files"] = skipFileIndex ? 0 : RunCountStage("files", () => ImportFiles(connection, transaction, root, dbPath));
            counts["animationBakeCachePreserved"] = RunCountStage("animation bake cache preserved", () => ImportPreservedAnimationBakeCache(connection, transaction, preservedBakeCache));
            counts["filesSkipped"] = skipFileIndex ? 1 : 0;
            counts["sidecarScanSkipped"] = skipSidecarScan ? 1 : 0;
            counts["jsonDocumentsSkipped"] = skipJsonDocuments ? 1 : 0;
            if (skipFileIndex)
            {
                Logger.Info("SQLite index stage 'files' skipped by --skip_sqlite_file_index.");
            }
            if (skipJsonDocuments)
            {
                Logger.Info("SQLite index stage 'json documents' skipped by --skip_sqlite_json_documents.");
            }

            RunStage("commit imports", () => transaction.Commit());
            RunStage("create sql indexes", () => CreateIndexes(connection));
            counts["modelAnimationCandidateModelSummary"] = RunCountStage("model animation candidate summaries", () => BuildModelAnimationCandidateSummaries(connection));
            counts["modelAnimationRelations"] = RunCountStage("unified model animation relations", () => BuildUnifiedModelAnimationRelations(connection));

            var capabilities = BuildAssetLibraryCapabilities(connection);
            RunStage("write capability metadata", () => WriteCapabilityMetadata(connection, capabilities));
            RunStage("write summary", () => WriteSummary(connection, root, dbPath, counts, skipFileIndex, skipSidecarScan, sourceIndexPath));
            RunStage("write asset library manifest", () => WriteUnifiedAssetLibraryManifest(root, resolvedSourceGame, capabilities));
            Logger.Info($"SQLite library index written: {dbPath}; elapsed={totalWatch.Elapsed.TotalSeconds:F1}s");
            return dbPath;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
        }

        private static string ReadExistingAssetLibrarySourceGame(string root)
        {
            var manifestPath = Path.Combine(root, "asset_library.json");
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            try
            {
                return (string)JObject.Parse(File.ReadAllText(manifestPath))["sourceGame"];
            }
            catch (Exception e) when (e is IOException || e is JsonException)
            {
                return null;
            }
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
    object_path TEXT,
    output TEXT,
    format TEXT,
    skeleton_path TEXT,
    skeleton_name TEXT,
    audio_kind TEXT,
    animation_type TEXT,
    skeleton_hash TEXT,
    validation_status TEXT,
    library_role TEXT,
    diagnostic_only INTEGER,
    character_assembly_role TEXT,
    character_assembly_family TEXT,
    character_assembly_candidate_only INTEGER,
    material_status TEXT,
    material_needs_customization_tint INTEGER,
    material_has_base_color_texture INTEGER,
    material_has_normal_texture INTEGER,
    material_image_count INTEGER,
    source_skin_mapping_status TEXT,
    selected_visual_cell_transform_node_status TEXT,
    external_skeleton_context_status TEXT,
    external_skeleton_context_serialized_file TEXT,
    external_skeleton_context_container TEXT,
    external_skeleton_context_name TEXT,
    external_skeleton_context_path_id INTEGER,
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
CREATE TABLE animation_binding_paths (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    animation_binding_id INTEGER NOT NULL,
    binding_path TEXT NOT NULL,
    binding_type TEXT,
    raw_json TEXT,
    FOREIGN KEY(animation_binding_id) REFERENCES animation_bindings(id)
);
CREATE TABLE model_binding_paths (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    model_output TEXT NOT NULL,
    binding_path TEXT NOT NULL,
    binding_type TEXT,
    raw_json TEXT
);
CREATE TABLE model_animation_candidates (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    model_output TEXT NOT NULL,
    animation_output TEXT NOT NULL,
    relation_source TEXT NOT NULL CHECK(relation_source = 'explicit'),
    confidence TEXT,
    score REAL,
    status TEXT,
    needs_validation INTEGER,
    raw_json TEXT NOT NULL
);
CREATE TABLE model_animation_candidate_model_summary (
    model_output TEXT PRIMARY KEY,
    explicit_count INTEGER NOT NULL,
    direct_preview_count INTEGER NOT NULL,
    internal_humanoid_count INTEGER NOT NULL,
    legacy_unity_bake_count INTEGER NOT NULL
);
CREATE TABLE model_animation_candidate_animation_summary (
    animation_output TEXT PRIMARY KEY,
    explicit_model_count INTEGER NOT NULL,
    direct_preview_model_count INTEGER NOT NULL,
    internal_humanoid_model_count INTEGER NOT NULL,
    legacy_unity_bake_model_count INTEGER NOT NULL
);
CREATE TABLE model_animation_relations (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    model_asset_id INTEGER,
    model_name TEXT,
    model_source TEXT,
    model_output TEXT NOT NULL,
    skeleton_path TEXT,
    skeleton_name TEXT,
    skeleton_hash TEXT,
    relation_kind TEXT,
    confidence TEXT,
    animation_count INTEGER,
    raw_json TEXT
);
CREATE TABLE relation_animations (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    relation_id INTEGER NOT NULL,
    animation_asset_id INTEGER,
    name TEXT,
    source TEXT,
    output TEXT NOT NULL,
    status TEXT,
    relation_source TEXT,
    usage_evidence TEXT,
    is_explicit_usage INTEGER,
    is_skeleton_compatible INTEGER,
    validation_status TEXT,
    duration REAL,
    frame_count INTEGER,
    track_count INTEGER,
    track_coverage REAL,
    hierarchy_compatible INTEGER,
    is_container_animation INTEGER,
    is_usable_candidate INTEGER,
    confidence_tier TEXT,
    relationship_kind TEXT,
    recommended_use TEXT,
    evidence_summary TEXT,
    is_deterministic_usage INTEGER,
    is_compatibility_candidate INTEGER,
    raw_json TEXT,
    FOREIGN KEY(relation_id) REFERENCES model_animation_relations(id)
);
CREATE TABLE animation_preview_cache (
    model_output TEXT NOT NULL,
    animation_output TEXT NOT NULL,
    status TEXT NOT NULL,
    gltf_path TEXT,
    validation_path TEXT,
    message TEXT,
    solver_version TEXT,
    diagnostic_status TEXT,
    internal_solved INTEGER,
    muscle_curve_count INTEGER,
    updated_utc TEXT,
    PRIMARY KEY(model_output, animation_output)
);
CREATE TABLE animation_bake_cache (
    model_output TEXT NOT NULL,
    animation_output TEXT NOT NULL,
    status TEXT NOT NULL,
    request_path TEXT,
    result_path TEXT,
    baked_gltf_path TEXT,
    baked_fbx_path TEXT,
    message TEXT,
    bake_mode TEXT,
    updated_utc TEXT,
    PRIMARY KEY(model_output, animation_output)
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
);
CREATE TABLE model_validation (
    asset_id INTEGER PRIMARY KEY,
    output TEXT,
    status TEXT,
    mesh_count INTEGER,
    material_count INTEGER,
    texture_count INTEGER,
    skin_count INTEGER,
    bone_count INTEGER,
    animation_count INTEGER,
    bbox_min_x REAL,
    bbox_min_y REAL,
    bbox_min_z REAL,
    bbox_max_x REAL,
    bbox_max_y REAL,
    bbox_max_z REAL,
    raw_json TEXT
);
CREATE TABLE texture_links (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    asset_id INTEGER,
    texture_output TEXT,
    usage TEXT,
    source TEXT,
    shared TEXT,
    sha256 TEXT,
    size_bytes INTEGER,
    extension TEXT,
    hard_linked INTEGER,
    link_error TEXT,
    raw_json TEXT
);
CREATE TABLE material_sidecars (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    asset_id INTEGER,
    material_output TEXT,
    material_name TEXT,
    raw_json TEXT
);
CREATE TABLE library_reports (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    report_kind TEXT NOT NULL,
    status TEXT,
    created_utc TEXT,
    summary_json TEXT
);");
        }

        private static void CreateIndexes(SqliteConnection connection)
        {
            var statements = new[]
            {
                "CREATE INDEX idx_assets_kind ON assets(kind, resource_kind);",
                "CREATE INDEX idx_assets_name ON assets(name);",
                "CREATE INDEX idx_assets_source ON assets(source, path_id);",
                "CREATE INDEX idx_assets_output ON assets(output);",
                "CREATE INDEX idx_assets_kind_output ON assets(kind, output);",
                "CREATE INDEX idx_assets_library_role ON assets(library_role);",
                "CREATE INDEX idx_assets_character_assembly ON assets(character_assembly_family, character_assembly_role);",
                "CREATE INDEX idx_assets_material_status ON assets(material_status);",
                "CREATE INDEX idx_assets_source_skin_mapping ON assets(source_skin_mapping_status);",
                "CREATE INDEX idx_assets_external_skeleton_context ON assets(external_skeleton_context_status, external_skeleton_context_name);",
                "CREATE INDEX idx_unity_assets_source ON unity_assets(source, path_id);",
                "CREATE INDEX idx_unity_assets_name ON unity_assets(name);",
                "CREATE INDEX idx_unity_relations_from ON unity_relations(from_source, from_path_id);",
                "CREATE INDEX idx_unity_relations_to ON unity_relations(to_source, to_path_id);",
                "CREATE INDEX idx_unity_relations_relation ON unity_relations(relation, confidence);",
                "CREATE INDEX idx_animation_bindings_name ON animation_bindings(animation_name);",
                "CREATE INDEX idx_animation_binding_paths_path ON animation_binding_paths(binding_path);",
                "CREATE INDEX idx_animation_binding_paths_animation ON animation_binding_paths(animation_binding_id);",
                "CREATE INDEX idx_model_binding_paths_model ON model_binding_paths(model_output);",
                "CREATE INDEX idx_model_binding_paths_path ON model_binding_paths(binding_path);",
                "CREATE INDEX idx_model_animation_candidates_model ON model_animation_candidates(model_output, status);",
                "CREATE INDEX idx_model_animation_candidates_animation ON model_animation_candidates(animation_output);",
                "CREATE INDEX idx_model_animation_candidates_confidence ON model_animation_candidates(confidence, relation_source);",
                "CREATE INDEX idx_model_animation_candidates_source_model ON model_animation_candidates(relation_source, model_output, status);",
                "CREATE INDEX idx_model_animation_candidates_source_animation ON model_animation_candidates(relation_source, animation_output);",
                "CREATE INDEX idx_model_animation_candidate_model_summary_count ON model_animation_candidate_model_summary(explicit_count);",
                "CREATE INDEX idx_model_animation_candidate_animation_summary_count ON model_animation_candidate_animation_summary(explicit_model_count);",
                "CREATE INDEX idx_model_animation_relations_model ON model_animation_relations(model_output);",
                "CREATE INDEX idx_relation_animations_relation ON relation_animations(relation_id, is_usable_candidate);",
                "CREATE INDEX idx_relation_animations_output ON relation_animations(output);",
                "CREATE INDEX idx_relation_animations_source ON relation_animations(relation_source, output);",
                "CREATE INDEX idx_model_validation_output ON model_validation(output);",
                "CREATE INDEX idx_model_validation_status ON model_validation(status);",
                "CREATE INDEX idx_texture_links_asset ON texture_links(asset_id);",
                "CREATE INDEX idx_texture_links_shared ON texture_links(shared);",
                "CREATE INDEX idx_texture_links_sha256 ON texture_links(sha256);",
                "CREATE INDEX idx_material_sidecars_asset ON material_sidecars(asset_id);",
                "CREATE INDEX idx_material_sidecars_output ON material_sidecars(material_output);",
                "CREATE INDEX idx_export_manifest_output ON export_manifest(output);",
                "CREATE INDEX idx_files_kind ON files(kind);",
            };

            foreach (var statement in statements)
            {
                var watch = Stopwatch.StartNew();
                Execute(connection, statement);
                Logger.Info($"SQLite index statement finished in {watch.Elapsed.TotalSeconds:F1}s: {statement}");
            }
        }

        private static List<AnimationBakeCacheRow> LoadExistingAnimationBakeCacheRows(string dbPath)
        {
            var rows = new List<AnimationBakeCacheRow>();
            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            {
                return rows;
            }

            try
            {
                using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
                connection.Open();
                if (!HasTable(connection, "animation_bake_cache"))
                {
                    return rows;
                }

                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT model_output, animation_output, status, request_path, result_path, baked_gltf_path, baked_fbx_path, message, bake_mode, updated_utc
FROM animation_bake_cache;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new AnimationBakeCacheRow(
                        ReadNullableString(reader, 0),
                        ReadNullableString(reader, 1),
                        ReadNullableString(reader, 2),
                        ReadNullableString(reader, 3),
                        ReadNullableString(reader, 4),
                        ReadNullableString(reader, 5),
                        ReadNullableString(reader, 6),
                        ReadNullableString(reader, 7),
                        ReadNullableString(reader, 8),
                        ReadNullableString(reader, 9)));
                }

                SqliteConnection.ClearPool(connection);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Unable to preserve existing animation_bake_cache before SQLite rebuild: {ex.Message}");
            }

            return rows
                .Where(x => !string.IsNullOrWhiteSpace(x.ModelOutput) && !string.IsNullOrWhiteSpace(x.AnimationOutput))
                .GroupBy(x => $"{x.ModelOutput}|{x.AnimationOutput}", StringComparer.OrdinalIgnoreCase)
                .Select(x => x.OrderByDescending(row => row.UpdatedUtc, StringComparer.OrdinalIgnoreCase).First())
                .ToList();
        }

        private static long ImportPreservedAnimationBakeCache(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<AnimationBakeCacheRow> rows)
        {
            if (rows == null || rows.Count == 0)
            {
                return 0;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT OR REPLACE INTO animation_bake_cache(model_output, animation_output, status, request_path, result_path, baked_gltf_path, baked_fbx_path, message, bake_mode, updated_utc)
VALUES ($modelOutput, $animationOutput, $status, $requestPath, $resultPath, $bakedGltfPath, $bakedFbxPath, $message, $bakeMode, $updatedUtc);";
            var p = AddParameters(command, "$modelOutput", "$animationOutput", "$status", "$requestPath", "$resultPath", "$bakedGltfPath", "$bakedFbxPath", "$message", "$bakeMode", "$updatedUtc");

            long count = 0;
            foreach (var row in rows)
            {
                Set(p, "$modelOutput", row.ModelOutput);
                Set(p, "$animationOutput", row.AnimationOutput);
                Set(p, "$status", row.Status);
                Set(p, "$requestPath", row.RequestPath);
                Set(p, "$resultPath", row.ResultPath);
                Set(p, "$bakedGltfPath", row.BakedGltfPath);
                Set(p, "$bakedFbxPath", row.BakedFbxPath);
                Set(p, "$message", row.Message);
                Set(p, "$bakeMode", row.BakeMode);
                Set(p, "$updatedUtc", row.UpdatedUtc);
                command.ExecuteNonQuery();
                count++;
            }

            return count;
        }

        private static string ReadNullableString(SqliteDataReader reader, int index)
        {
            return reader.IsDBNull(index) ? null : reader.GetString(index);
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
INSERT INTO assets(kind, resource_kind, name, source_type, container, source, path_id, output, audio_kind, animation_type, skeleton_hash, validation_status, library_role, diagnostic_only, character_assembly_role, character_assembly_family, character_assembly_candidate_only, material_status, material_needs_customization_tint, material_has_base_color_texture, material_has_normal_texture, material_image_count, source_skin_mapping_status, selected_visual_cell_transform_node_status, external_skeleton_context_status, external_skeleton_context_serialized_file, external_skeleton_context_container, external_skeleton_context_name, external_skeleton_context_path_id, raw_json)
VALUES ($kind, $resourceKind, $name, $sourceType, $container, $source, $pathId, $output, $audioKind, $animationType, $skeletonHash, $validationStatus, $libraryRole, $diagnosticOnly, $characterAssemblyRole, $characterAssemblyFamily, $characterAssemblyCandidateOnly, $materialStatus, $materialNeedsCustomizationTint, $materialHasBaseColorTexture, $materialHasNormalTexture, $materialImageCount, $sourceSkinMappingStatus, $selectedVisualCellTransformNodeStatus, $externalSkeletonContextStatus, $externalSkeletonContextSerializedFile, $externalSkeletonContextContainer, $externalSkeletonContextName, $externalSkeletonContextPathId, $rawJson);";
            var p = AddParameters(
                command,
                "$kind",
                "$resourceKind",
                "$name",
                "$sourceType",
                "$container",
                "$source",
                "$pathId",
                "$output",
                "$audioKind",
                "$animationType",
                "$skeletonHash",
                "$validationStatus",
                "$libraryRole",
                "$diagnosticOnly",
                "$characterAssemblyRole",
                "$characterAssemblyFamily",
                "$characterAssemblyCandidateOnly",
                "$materialStatus",
                "$materialNeedsCustomizationTint",
                "$materialHasBaseColorTexture",
                "$materialHasNormalTexture",
                "$materialImageCount",
                "$sourceSkinMappingStatus",
                "$selectedVisualCellTransformNodeStatus",
                "$externalSkeletonContextStatus",
                "$externalSkeletonContextSerializedFile",
                "$externalSkeletonContextContainer",
                "$externalSkeletonContextName",
                "$externalSkeletonContextPathId",
                "$rawJson");

            var rows = ReadJsonLines(path).ToList();
            AttachModelValidation(root, rows);

            long count = 0;
            foreach (var obj in rows)
            {
                EnrichAnimationCatalogFromSidecar(root, obj);
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
                Set(p, "$validationStatus", S(obj, "modelValidationStatus") ?? S(obj["modelValidation"] as JObject, "Status"));
                // 这些字段是浏览器和 SQL 筛选常用入口；关系判断仍以 raw_json 里的 Unity 证据为准。
                Set(p, "$libraryRole", S(obj, "libraryRole"));
                Set(p, "$diagnosticOnly", B(obj, "diagnosticOnly"));
                Set(p, "$characterAssemblyRole", S(obj, "characterAssemblyRole"));
                Set(p, "$characterAssemblyFamily", S(obj, "characterAssemblyFamily"));
                Set(p, "$characterAssemblyCandidateOnly", B(obj, "characterAssemblyCandidateOnly"));
                Set(p, "$materialStatus", S(obj, "materialStatus"));
                Set(p, "$materialNeedsCustomizationTint", B(obj, "materialNeedsCustomizationTint"));
                Set(p, "$materialHasBaseColorTexture", B(obj, "materialHasBaseColorTexture"));
                Set(p, "$materialHasNormalTexture", B(obj, "materialHasNormalTexture"));
                Set(p, "$materialImageCount", I(obj, "materialImageCount"));
                var externalSkeletonContext = obj["externalSkeletonContextCandidate"] as JObject;
                Set(p, "$sourceSkinMappingStatus", S(obj, "sourceSkinMappingStatus"));
                Set(p, "$selectedVisualCellTransformNodeStatus", S(obj, "selectedVisualCellTransformNodeTableStatus"));
                Set(p, "$externalSkeletonContextStatus", S(obj, "externalSkeletonContextCandidateStatus") ?? S(externalSkeletonContext, "status"));
                Set(p, "$externalSkeletonContextSerializedFile", S(externalSkeletonContext, "serializedFile"));
                Set(p, "$externalSkeletonContextContainer", S(externalSkeletonContext, "container"));
                Set(p, "$externalSkeletonContextName", S(externalSkeletonContext, "visualCellGameObjectName"));
                Set(p, "$externalSkeletonContextPathId", I(externalSkeletonContext, "visualCellPathId"));
                Set(p, "$rawJson", obj.ToString(Formatting.None));
                command.ExecuteNonQuery();
                count++;
            }
            return count;
        }

        private static long ImportModelValidation(SqliteConnection connection, SqliteTransaction transaction, string root)
        {
            var validationByOutput = ReadModelValidationByOutput(root);
            if (validationByOutput.Count == 0)
            {
                return 0;
            }

            var assetIdsByOutput = LoadAssetIdsByOutput(connection, root, "Model");
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT OR REPLACE INTO model_validation(asset_id, output, status, mesh_count, material_count, texture_count, skin_count, bone_count, animation_count, bbox_min_x, bbox_min_y, bbox_min_z, bbox_max_x, bbox_max_y, bbox_max_z, raw_json)
VALUES ($assetId, $output, $status, $meshCount, $materialCount, $textureCount, $skinCount, $boneCount, $animationCount, $bboxMinX, $bboxMinY, $bboxMinZ, $bboxMaxX, $bboxMaxY, $bboxMaxZ, $rawJson);";
            var p = AddParameters(
                command,
                "$assetId",
                "$output",
                "$status",
                "$meshCount",
                "$materialCount",
                "$textureCount",
                "$skinCount",
                "$boneCount",
                "$animationCount",
                "$bboxMinX",
                "$bboxMinY",
                "$bboxMinZ",
                "$bboxMaxX",
                "$bboxMaxY",
                "$bboxMaxZ",
                "$rawJson");

            long count = 0;
            foreach (var (output, validation) in validationByOutput)
            {
                if (!assetIdsByOutput.TryGetValue(output, out var assetId))
                {
                    continue;
                }

                var body = validation["Body"] as JObject;
                var bounds = body?["Bounds"] as JObject;
                var min = bounds?["Min"]?.Values<double>().ToArray() ?? Array.Empty<double>();
                var max = bounds?["Max"]?.Values<double>().ToArray() ?? Array.Empty<double>();

                Set(p, "$assetId", assetId);
                Set(p, "$output", output);
                Set(p, "$status", S(validation, "Status"));
                Set(p, "$meshCount", I(validation["Counts"] as JObject, "Meshes") ?? I(body, "PrimitiveCount"));
                Set(p, "$materialCount", I(validation["Counts"] as JObject, "Materials") ?? I(body, "MaterialCount"));
                Set(p, "$textureCount", I(validation["Counts"] as JObject, "Textures") ?? I(body, "TextureCount"));
                Set(p, "$skinCount", I(validation["Counts"] as JObject, "Skins") ?? I(body, "SkinCount"));
                Set(p, "$boneCount", I(body, "SkinJointCount"));
                Set(p, "$animationCount", 0);
                Set(p, "$bboxMinX", min.Length > 0 ? min[0] : null);
                Set(p, "$bboxMinY", min.Length > 1 ? min[1] : null);
                Set(p, "$bboxMinZ", min.Length > 2 ? min[2] : null);
                Set(p, "$bboxMaxX", max.Length > 0 ? max[0] : null);
                Set(p, "$bboxMaxY", max.Length > 1 ? max[1] : null);
                Set(p, "$bboxMaxZ", max.Length > 2 ? max[2] : null);
                Set(p, "$rawJson", validation.ToString(Formatting.None));
                command.ExecuteNonQuery();
                count++;
            }

            return count;
        }

        private static Dictionary<string, long> LoadAssetIdsByOutput(SqliteConnection connection, string root, string kind)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, output FROM assets WHERE kind=$kind AND output IS NOT NULL AND output<>'';";
            command.Parameters.AddWithValue("$kind", kind);

            var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result[NormalizeLibraryOutputForJoin(root, reader.GetString(1))] = reader.GetInt64(0);
            }

            return result;
        }

        private static void EnrichAnimationCatalogFromSidecar(string root, JObject obj)
        {
            if (!string.Equals(S(obj, "kind"), "Animation", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var sidecarPath = ResolveLibraryPath(root, S(obj, "animationAsset"));
            if (string.IsNullOrWhiteSpace(sidecarPath) || !File.Exists(sidecarPath))
            {
                return;
            }

            try
            {
                var sidecar = JObject.Parse(File.ReadAllText(sidecarPath));
                var decoded = sidecar["decoded"] as JObject;
                // assets.raw_json 只放筛选需要的轻量状态，不塞完整 keyframes，避免大库 SQLite 膨胀。
                obj["directGltfAnimationStatus"] = sidecar["directGltfAnimationStatus"];
                obj["directTrsAnimationReady"] = sidecar["directTrsAnimationReady"];
                obj["directWeightsAnimationReady"] = sidecar["directWeightsAnimationReady"];
                obj["needsDirectTrsAnimation"] = sidecar["needsDirectTrsAnimation"];
                obj["deprecatedUnityBakeOnly"] = sidecar["deprecatedUnityBakeOnly"];
                obj["decodedStatus"] = decoded?["status"];
                obj["decodedPlaybackKind"] = decoded?["playbackKind"];
                obj["decoderGapKind"] = decoded?["decoderGapKind"];
                obj["decoderGapNextAction"] = decoded?["decoderGapNextAction"];
                obj["valueDeltaOnlyHumanoid"] = decoded?["decoderInput"]?["payloadSummary"]?["valueDeltaOnlyHumanoid"];
                obj["muscleValueDeltaCount"] = decoded?["decoderInput"]?["muscleValueDeltaCount"];
            }
            catch (Exception e)
            {
                obj["animationSidecarReadError"] = $"{e.GetType().Name}: {e.Message}";
            }
        }

        private static long ImportTextureAssets(SqliteConnection connection, SqliteTransaction transaction, string root)
        {
            var textureRoot = Path.Combine(root, "Textures");
            if (!Directory.Exists(textureRoot))
            {
                return 0;
            }

            var existingOutputs = LoadExistingAssetOutputKeys(connection, root);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO assets(kind, resource_kind, name, source_type, container, source, path_id, output, audio_kind, animation_type, skeleton_hash, raw_json)
VALUES ($kind, $resourceKind, $name, $sourceType, $container, $source, $pathId, $output, $audioKind, $animationType, $skeletonHash, $rawJson);";
            var p = AddParameters(command, "$kind", "$resourceKind", "$name", "$sourceType", "$container", "$source", "$pathId", "$output", "$audioKind", "$animationType", "$skeletonHash", "$rawJson");

            long count = 0;
            foreach (var file in Directory.EnumerateFiles(textureRoot, "*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(file);
                if (!TextureFileExtensions.Contains(extension))
                {
                    continue;
                }

                var relativeOutput = MakeRelative(root, file);
                var outputKey = NormalizeAssetOutputKey(root, relativeOutput);
                if (!existingOutputs.Add(outputKey))
                {
                    continue;
                }

                var textureRelative = MakeRelative(textureRoot, file);
                var bucket = GetTextureBucket(textureRelative);
                var info = new FileInfo(file);
                var raw = new JObject
                {
                    ["kind"] = "Texture",
                    ["resourceKind"] = bucket,
                    ["name"] = Path.GetFileNameWithoutExtension(file) ?? "",
                    ["sourceType"] = "TextureFile",
                    ["source"] = relativeOutput,
                    ["output"] = relativeOutput,
                    ["modelPreview"] = relativeOutput,
                    ["texturePath"] = textureRelative.Replace('\\', '/'),
                    ["extension"] = extension.TrimStart('.').ToLowerInvariant(),
                    ["fileSize"] = info.Length,
                    ["indexedFrom"] = "texture_directory_index",
                    ["status"] = "exported_texture_file"
                };

                Set(p, "$kind", "Texture");
                Set(p, "$resourceKind", bucket);
                Set(p, "$name", Path.GetFileNameWithoutExtension(file) ?? "");
                Set(p, "$sourceType", "TextureFile");
                Set(p, "$container", null);
                Set(p, "$source", relativeOutput);
                Set(p, "$pathId", null);
                Set(p, "$output", relativeOutput);
                Set(p, "$audioKind", null);
                Set(p, "$animationType", null);
                Set(p, "$skeletonHash", null);
                Set(p, "$rawJson", raw.ToString(Formatting.None));
                command.ExecuteNonQuery();
                count++;
            }

            return count;
        }

        private static HashSet<string> LoadExistingAssetOutputKeys(SqliteConnection connection, string root)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT output FROM assets WHERE output IS NOT NULL AND output <> '';";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(NormalizeAssetOutputKey(root, reader.GetString(0)));
            }

            return result;
        }

        private static string GetTextureBucket(string textureRelativePath)
        {
            var parts = (textureRelativePath ?? string.Empty)
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1 ? parts[0] : "Textures";
        }

        private static long ImportTextureLinks(SqliteConnection connection, SqliteTransaction transaction, string root)
        {
            using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = @"
SELECT id, output, name
FROM assets
WHERE kind='Model'
  AND output IS NOT NULL
  AND output <> '';";

            var models = new List<(long Id, string Output, string Name)>();
            using (var reader = select.ExecuteReader())
            {
                while (reader.Read())
                {
                    models.Add((reader.GetInt64(0), ReadNullableString(reader, 1), ReadNullableString(reader, 2)));
                }
            }

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = @"
INSERT INTO texture_links(asset_id, texture_output, usage, source, shared, sha256, size_bytes, extension, hard_linked, link_error, raw_json)
VALUES ($assetId, $textureOutput, $usage, $source, $shared, $sha256, $sizeBytes, $extension, $hardLinked, $linkError, $rawJson);";
            var p = AddParameters(insert, "$assetId", "$textureOutput", "$usage", "$source", "$shared", "$sha256", "$sizeBytes", "$extension", "$hardLinked", "$linkError", "$rawJson");

            long count = 0;
            foreach (var model in models)
            {
                var modelPath = ResolveLibraryPath(root, model.Output);
                if (string.IsNullOrWhiteSpace(modelPath)
                    || !File.Exists(modelPath)
                    || !string.Equals(Path.GetExtension(modelPath), ".gltf", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var link in ReadGltfTextureLinks(root, modelPath, model.Output))
                {
                    Set(p, "$assetId", model.Id);
                    Set(p, "$textureOutput", link.Shared);
                    Set(p, "$usage", link.Usage);
                    Set(p, "$source", link.Source);
                    Set(p, "$shared", link.Shared);
                    Set(p, "$sha256", link.Sha256);
                    Set(p, "$sizeBytes", link.SizeBytes);
                    Set(p, "$extension", link.Extension);
                    Set(p, "$hardLinked", link.HardLinked);
                    Set(p, "$linkError", link.LinkError);
                    Set(p, "$rawJson", link.RawJson.ToString(Formatting.None));
                    insert.ExecuteNonQuery();
                    count++;
                }
            }

            return count;
        }

        private static IEnumerable<TextureLinkRow> ReadGltfTextureLinks(string root, string gltfPath, string modelOutput)
        {
            JObject gltf;
            try
            {
                gltf = JObject.Parse(File.ReadAllText(gltfPath));
            }
            catch (Exception e) when (e is IOException || e is JsonException || e is UnauthorizedAccessException)
            {
                Logger.Warning($"Skipping texture_links for invalid glTF: {gltfPath}: {e.Message}");
                yield break;
            }

            var imageUsage = BuildGltfImageUsageMap(gltf);
            var images = gltf["images"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            for (var imageIndex = 0; imageIndex < images.Length; imageIndex++)
            {
                var image = images[imageIndex];
                var uri = S(image, "uri");
                var usages = imageUsage.TryGetValue(imageIndex, out var usageSet) && usageSet.Count > 0
                    ? usageSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray()
                    : new[] { $"image:{imageIndex}" };
                var usage = string.Join(",", usages);
                var source = $"{NormalizeLibraryOutputForJoin(root, modelOutput)}#{uri ?? $"image:{imageIndex}"}";
                var shared = string.Empty;
                string sha256 = null;
                long? sizeBytes = null;
                string extension = null;
                int? hardLinked = null;
                string linkError = null;
                string texturePath = null;

                if (string.IsNullOrWhiteSpace(uri))
                {
                    linkError = "missing_image_uri";
                }
                else if (IsDataUri(uri))
                {
                    linkError = "embedded_data_uri";
                }
                else if (IsAbsoluteUri(uri))
                {
                    linkError = "absolute_or_remote_uri";
                    shared = uri;
                }
                else
                {
                    texturePath = ResolveGltfUriPath(Path.GetDirectoryName(gltfPath) ?? root, uri);
                    shared = File.Exists(texturePath) ? MakeRelative(root, texturePath) : NormalizeGltfRelativeUri(uri);
                    extension = Path.GetExtension(texturePath).TrimStart('.').ToLowerInvariant();
                    if (File.Exists(texturePath))
                    {
                        var info = new FileInfo(texturePath);
                        sizeBytes = info.Length;
                        sha256 = ComputeSha256(texturePath);
                        hardLinked = IsLikelyHardLinked(info) ? 1 : 0;
                    }
                    else
                    {
                        linkError = "missing_texture_file";
                    }
                }

                var raw = new JObject
                {
                    ["modelOutput"] = NormalizeLibraryOutputForJoin(root, modelOutput),
                    ["gltf"] = MakeRelative(root, gltfPath),
                    ["imageIndex"] = imageIndex,
                    ["uri"] = uri,
                    ["usage"] = usage,
                    ["shared"] = shared,
                    ["sha256"] = sha256,
                    ["sizeBytes"] = sizeBytes,
                    ["extension"] = extension,
                    ["hardLinked"] = hardLinked,
                    ["linkError"] = linkError,
                    ["rule"] = "由 glTF images/textures/materials 的确定性 URI 引用写入；不按文件名猜材质关系。"
                };

                yield return new TextureLinkRow(source, shared, usage, sha256, sizeBytes, extension, hardLinked, linkError, raw);
            }
        }

        private static Dictionary<int, HashSet<string>> BuildGltfImageUsageMap(JObject gltf)
        {
            var result = new Dictionary<int, HashSet<string>>();
            var textures = gltf["textures"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            void Add(int imageIndex, string usage)
            {
                if (imageIndex < 0)
                {
                    return;
                }
                if (!result.TryGetValue(imageIndex, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    result[imageIndex] = set;
                }
                set.Add(usage);
            }

            foreach (var material in gltf["materials"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var materialName = S(material, "name");
                AddTextureUsage(material["pbrMetallicRoughness"]?["baseColorTexture"] as JObject, "baseColorTexture", materialName);
                AddTextureUsage(material["pbrMetallicRoughness"]?["metallicRoughnessTexture"] as JObject, "metallicRoughnessTexture", materialName);
                AddTextureUsage(material["normalTexture"] as JObject, "normalTexture", materialName);
                AddTextureUsage(material["occlusionTexture"] as JObject, "occlusionTexture", materialName);
                AddTextureUsage(material["emissiveTexture"] as JObject, "emissiveTexture", materialName);
            }

            return result;

            void AddTextureUsage(JObject textureInfo, string slot, string materialName)
            {
                var textureIndex = (int?)textureInfo?["index"];
                if (textureIndex == null || textureIndex.Value < 0 || textureIndex.Value >= textures.Length)
                {
                    return;
                }

                var imageIndex = (int?)textures[textureIndex.Value]["source"];
                if (imageIndex == null)
                {
                    return;
                }

                Add(imageIndex.Value, string.IsNullOrWhiteSpace(materialName) ? slot : $"{materialName}:{slot}");
            }
        }

        private static long ImportMaterialSidecars(SqliteConnection connection, SqliteTransaction transaction, string root)
        {
            using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = @"
SELECT id, output
FROM assets
WHERE kind='Model'
  AND output IS NOT NULL
  AND output <> '';";

            var models = new List<(long Id, string Output)>();
            using (var reader = select.ExecuteReader())
            {
                while (reader.Read())
                {
                    models.Add((reader.GetInt64(0), ReadNullableString(reader, 1)));
                }
            }

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = @"
INSERT INTO material_sidecars(asset_id, material_output, material_name, raw_json)
VALUES ($assetId, $materialOutput, $materialName, $rawJson);";
            var p = AddParameters(insert, "$assetId", "$materialOutput", "$materialName", "$rawJson");

            long count = 0;
            foreach (var model in models)
            {
                var modelPath = ResolveLibraryPath(root, model.Output);
                if (string.IsNullOrWhiteSpace(modelPath)
                    || !File.Exists(modelPath)
                    || !string.Equals(Path.GetExtension(modelPath), ".gltf", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var modelDir = Path.GetDirectoryName(modelPath);
                if (string.IsNullOrWhiteSpace(modelDir) || !Directory.Exists(modelDir))
                {
                    continue;
                }

                foreach (var sidecar in ReadGltfMaterialSidecars(root, modelPath, modelDir))
                {
                    Set(p, "$assetId", model.Id);
                    Set(p, "$materialOutput", sidecar.Output);
                    Set(p, "$materialName", sidecar.Name);
                    Set(p, "$rawJson", sidecar.RawJson.ToString(Formatting.None));
                    insert.ExecuteNonQuery();
                    count++;
                }
            }

            return count;
        }

        private static IEnumerable<MaterialSidecarRow> ReadGltfMaterialSidecars(string root, string gltfPath, string modelDir)
        {
            JObject gltf;
            try
            {
                gltf = JObject.Parse(File.ReadAllText(gltfPath));
            }
            catch (Exception e) when (e is IOException || e is JsonException || e is UnauthorizedAccessException)
            {
                Logger.Warning($"Skipping material_sidecars for invalid glTF: {gltfPath}: {e.Message}");
                yield break;
            }

            var materialNames = gltf["materials"]?
                .OfType<JObject>()
                .Select(x => S(x, "name"))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (materialNames.Count == 0)
            {
                yield break;
            }

            foreach (var file in Directory.EnumerateFiles(modelDir, "*.json", SearchOption.TopDirectoryOnly))
            {
                JObject sidecar;
                try
                {
                    sidecar = JObject.Parse(File.ReadAllText(file));
                }
                catch (Exception e) when (e is IOException || e is JsonException || e is UnauthorizedAccessException)
                {
                    Logger.Warning($"Skipping unreadable material sidecar candidate: {file}: {e.Message}");
                    continue;
                }

                var materialName = S(sidecar, "m_Name") ?? S(sidecar, "Name") ?? Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(materialName) || !materialNames.Contains(materialName))
                {
                    continue;
                }

                if (!LooksLikeUnityMaterialSidecar(sidecar))
                {
                    continue;
                }

                var output = MakeRelative(root, file);
                var raw = new JObject
                {
                    ["materialOutput"] = output,
                    ["materialName"] = materialName,
                    ["modelGltf"] = MakeRelative(root, gltfPath),
                    ["unityMaterial"] = sidecar,
                    ["rule"] = "只索引模型 glTF 同目录、名称命中 glTF material、且包含 Unity Material 结构的 JSON；不按任意文件名猜材质关系。"
                };

                yield return new MaterialSidecarRow(output, materialName, raw);
            }
        }

        private static bool LooksLikeUnityMaterialSidecar(JObject sidecar)
        {
            if (sidecar == null)
            {
                return false;
            }

            return sidecar["m_SavedProperties"] is JObject
                   || sidecar["m_Shader"] is JObject
                   || sidecar["unityMaterial"] is JObject;
        }

        private static string ResolveGltfUriPath(string directory, string uri)
        {
            return Path.GetFullPath(Path.Combine(directory, NormalizeGltfRelativeUri(uri).Replace('/', Path.DirectorySeparatorChar)));
        }

        private static string NormalizeGltfRelativeUri(string uri)
        {
            try
            {
                return Uri.UnescapeDataString(uri ?? string.Empty).Replace('\\', '/').TrimStart('/');
            }
            catch (UriFormatException)
            {
                return (uri ?? string.Empty).Replace('\\', '/').TrimStart('/');
            }
        }

        private static bool IsDataUri(string uri)
        {
            return uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAbsoluteUri(string uri)
        {
            return Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && !string.IsNullOrWhiteSpace(parsed.Scheme);
        }

        private static string ComputeSha256(string path)
        {
            using var stream = File.OpenRead(path);
            var hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static bool IsLikelyHardLinked(FileInfo info)
        {
            // .NET 在不同平台上没有统一暴露 hardlink count；这里先记录当前文件是否存在为普通文件。
            // 后续如果导出器真正建立共享硬链接，可在写入阶段显式记录。
            return false;
        }

        private static long ImportModelBindingPaths(SqliteConnection connection, SqliteTransaction transaction, string root)
        {
            var path = Path.Combine(root, "asset_catalog.jsonl");
            if (!File.Exists(path))
            {
                return 0;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO model_binding_paths(model_output, binding_path, binding_type, raw_json)
VALUES ($modelOutput, $bindingPath, $bindingType, $rawJson);";
            var p = AddParameters(command, "$modelOutput", "$bindingPath", "$bindingType", "$rawJson");

            long count = 0;
            foreach (var obj in ReadJsonLines(path))
            {
                if (!string.Equals(S(obj, "kind"), "Model", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var output = S(obj, "output");
                if (string.IsNullOrWhiteSpace(output))
                {
                    continue;
                }

                foreach (var entry in EnumerateModelBindingPaths(obj).GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Select(x => x.First()))
                {
                    Set(p, "$modelOutput", output);
                    Set(p, "$bindingPath", entry.Path);
                    Set(p, "$bindingType", entry.Type);
                    Set(p, "$rawJson", entry.RawJson?.ToString(Formatting.None));
                    command.ExecuteNonQuery();
                    count++;
                }
            }

            return count;
        }

        private static IEnumerable<(string Path, string Type, JToken RawJson)> EnumerateModelBindingPaths(JObject obj)
        {
            foreach (var token in obj["bonePaths"]?.OfType<JValue>() ?? Enumerable.Empty<JValue>())
            {
                var path = NormalizeUnityBindingPath(token.Value?.ToString());
                if (!string.IsNullOrWhiteSpace(path))
                {
                    yield return (path, "Bone", token);
                }
            }

            foreach (var token in obj["nodePaths"]?.OfType<JValue>() ?? Enumerable.Empty<JValue>())
            {
                var path = NormalizeUnityBindingPath(token.Value?.ToString());
                if (!string.IsNullOrWhiteSpace(path))
                {
                    yield return (path, "Node", token);
                }
            }
        }

        private static long ImportModelAnimationCandidates(SqliteConnection connection, SqliteTransaction transaction, string root)
        {
            var path = Path.Combine(root, "model_animations.compact.json");
            if (!File.Exists(path))
            {
                return 0;
            }

            JObject index;
            try
            {
                index = JObject.Parse(File.ReadAllText(path));
            }
            catch (JsonException e)
            {
                Logger.Warning($"Skipping invalid compact model-animation index: {e.Message}");
                return 0;
            }

            var modelOutputs = index["models"]?
                .OfType<JObject>()
                .Select(x => new { Id = S(x, "id"), Output = S(x, "output") })
                .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Output))
                .ToDictionary(x => x.Id, x => x.Output, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var animationOutputs = index["animations"]?
                .OfType<JObject>()
                .Select(x => new { Id = S(x, "id"), Output = S(x, "output") })
                .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Output))
                .ToDictionary(x => x.Id, x => x.Output, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var modelCatalogByOutput = ReadModelCatalogByOutput(root);

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO model_animation_candidates(model_output, animation_output, relation_source, confidence, score, status, needs_validation, raw_json)
VALUES ($modelOutput, $animationOutput, $relationSource, $confidence, $score, $status, $needsValidation, $rawJson);";
            var p = AddParameters(command, "$modelOutput", "$animationOutput", "$relationSource", "$confidence", "$score", "$status", "$needsValidation", "$rawJson");

            long count = 0;
            long skippedNonExplicit = 0;
            foreach (var modelRef in index["modelAnimationRefs"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var modelId = S(modelRef, "modelId");
                if (string.IsNullOrWhiteSpace(modelId) || !modelOutputs.TryGetValue(modelId, out var modelOutput))
                {
                    continue;
                }

                foreach (var candidate in modelRef["candidates"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    var animationId = S(candidate, "animationId") ?? S(candidate, "id");
                    if (string.IsNullOrWhiteSpace(animationId) || !animationOutputs.TryGetValue(animationId, out var animationOutput))
                    {
                        continue;
                    }

                    var relationSource = S(candidate, "relationSource");
                    var confidence = S(candidate, "confidence");
                    if (!IsExplicitModelAnimationRelation(relationSource, confidence))
                    {
                        skippedNonExplicit++;
                        continue;
                    }

                    var rawCandidate = (JObject)candidate.DeepClone();
                    if (modelCatalogByOutput.TryGetValue(NormalizeLibraryOutputForJoin(root, modelOutput), out var modelCatalog))
                    {
                        ApplyModelAnimationGate(rawCandidate, BuildModelAnimationGate(modelCatalog));
                    }
                    else
                    {
                        ApplyModelAnimationGate(rawCandidate, new ModelAnimationGate(
                            false,
                            "blocked",
                            new[] { "model_catalog_missing" },
                            new JObject
                            {
                                ["rule"] = "没有模型 catalog 证据时不能进入动画阶段。",
                                ["modelOutput"] = modelOutput,
                            }));
                    }

                    Set(p, "$modelOutput", modelOutput);
                    Set(p, "$animationOutput", animationOutput);
                    Set(p, "$relationSource", relationSource);
                    Set(p, "$confidence", confidence);
                    Set(p, "$score", D(candidate, "score"));
                    Set(p, "$status", GetExplicitCandidateStatus(rawCandidate));
                    Set(p, "$needsValidation", NeedsExplicitCandidateValidation(rawCandidate) ? 1 : 0);
                    Set(p, "$rawJson", rawCandidate.ToString(Formatting.None));
                    command.ExecuteNonQuery();
                    count++;
                }
            }

            if (skippedNonExplicit > 0)
            {
                Logger.Info($"Skipped {skippedNonExplicit} non-explicit compact model-animation candidate(s); fallback/diagnostic relations are not imported into model_animation_candidates.");
            }

            return count;
        }

        private static bool IsExplicitModelAnimationRelation(string relationSource, string confidence)
        {
            // 默认候选表只接受关系来源本身就是 explicit 的记录。
            // confidence 只能说明这条显式关系的质量，不能单独把 fallback/diagnostic 提升为默认绑定。
            return string.Equals(relationSource, "explicit", StringComparison.OrdinalIgnoreCase);
        }

        private static long ImportExplicitModelAnimationCandidatesFromSourceIndex(SqliteConnection connection, SqliteTransaction transaction, string root, string sourceIndexPath)
        {
            var sourceIndex = string.IsNullOrWhiteSpace(sourceIndexPath)
                ? Path.Combine(root, "unity_source_index.db")
                : Path.GetFullPath(sourceIndexPath);
            var catalogPath = Path.Combine(root, "asset_catalog.jsonl");
            if (!File.Exists(sourceIndex) || !File.Exists(catalogPath))
            {
                return 0;
            }

            var catalog = ReadJsonLines(catalogPath).ToList();
            AttachModelValidation(root, catalog);
            var modelCatalog = catalog
                .Where(x => string.Equals(S(x, "kind"), "Model", StringComparison.OrdinalIgnoreCase))
                .Where(IsPrefabLikeModel)
                .Where(x => !string.IsNullOrWhiteSpace(S(x, "output")))
                .ToList();
            var animationCatalog = catalog
                .Where(x => string.Equals(S(x, "kind"), "Animation", StringComparison.OrdinalIgnoreCase))
                .Where(x => !string.IsNullOrWhiteSpace(S(x, "output")))
                .ToList();

            if (modelCatalog.Count == 0 || animationCatalog.Count == 0)
            {
                // 模型优先阶段常常只导出静态模型。没有动画资产时不加载完整源动画图，
                // 避免小样本重建 SQLite 也扫描几百万条 source_relations。
                return 0;
            }

            var smallLibrary = modelCatalog.Count <= 100 && animationCatalog.Count <= 1000;
            var sourceIndexHasReverseRelationIndex = SourceIndexHasIndex(sourceIndex, "idx_source_relations_to");
            if (smallLibrary)
            {
                var fastCount = TryImportExplicitModelAnimationCandidatesFromSourceIndexFast(
                    connection,
                    transaction,
                    sourceIndex,
                    modelCatalog,
                    animationCatalog);
                if (fastCount > 0)
                {
                    Logger.Info($"SQLite library index used targeted source-index animation import for small Library; rows={fastCount}");
                    return fastCount;
                }

                if (!sourceIndexHasReverseRelationIndex)
                {
                    Logger.Warning("SQLite library index skipped full source animation graph fallback because idx_source_relations_to is missing. 小样本索引只保留已导出的 sidecar/显式候选，避免在超大 source_relations 上全表扫描；如需补齐反查候选，请在源索引副本上补建 idx_source_relations_to。");
                    return 0;
                }
            }

            var graph = SourceAnimationGraph.Load(sourceIndex);
            if (graph.Objects.Count == 0)
            {
                return 0;
            }

            var models = modelCatalog
                .Select(x => new CatalogAsset(
                    graph.KeyFromCatalog(x),
                    S(x, "name"),
                    S(x, "output"),
                    x))
                .Where(x => x.Key.IsValid && !string.IsNullOrWhiteSpace(x.Output))
                .ToList();
            var animations = animationCatalog
                .Select(x => new CatalogAsset(
                    graph.KeyFromCatalog(x),
                    S(x, "name"),
                    S(x, "output"),
                    x))
                .Where(x => x.Key.IsValid && !string.IsNullOrWhiteSpace(x.Output))
                .GroupBy(x => x.Key)
                .ToDictionary(x => x.Key, x => x.First());

            if (models.Count == 0 || animations.Count == 0)
            {
                return 0;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO model_animation_candidates(model_output, animation_output, relation_source, confidence, score, status, needs_validation, raw_json)
VALUES ($modelOutput, $animationOutput, 'explicit', 'explicit_unity_source_index', 100, $status, $needsValidation, $rawJson);";
            var p = AddParameters(command, "$modelOutput", "$animationOutput", "$status", "$needsValidation", "$rawJson");

            long count = 0;
            var processedModels = 0;
            var modelWatch = Stopwatch.StartNew();
            var insertedOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in models)
            {
                var seenAnimationOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var relation in graph.FindAnimationClipsForModel(model.Key))
                {
                    if (!animations.TryGetValue(relation.ClipKey, out var animation))
                    {
                        continue;
                    }

                    if (!seenAnimationOutputs.Add(animation.Output))
                    {
                        continue;
                    }

                    if (!insertedOutputs.Add(model.Output + "|" + animation.Output))
                    {
                        continue;
                    }

                    var raw = BuildExplicitSourceCandidateJson(model.Raw, animation.Raw, relation);
                    Set(p, "$modelOutput", model.Output);
                    Set(p, "$animationOutput", animation.Output);
                    Set(p, "$status", GetExplicitCandidateStatus(raw));
                    Set(p, "$needsValidation", NeedsExplicitCandidateValidation(raw) ? 1 : 0);
                    Set(p, "$rawJson", raw.ToString(Formatting.None));
                    command.ExecuteNonQuery();
                    count++;
                }

                processedModels++;
                if (processedModels % 10000 == 0)
                {
                    Logger.Info($"SQLite source-index candidate expansion processed {processedModels}/{models.Count} model(s) in {modelWatch.Elapsed.TotalSeconds:F1}s; inserted={count}");
                }
            }

            if (count > 0)
            {
                Logger.Info($"SQLite library index added {count} explicit model-animation candidate(s) from unity_source_index.db.");
            }
            if (graph.SkippedStaleOverrideControllers > 0)
            {
                Logger.Warning($"SQLite library index skipped {graph.SkippedStaleOverrideControllers} stale AnimatorOverrideController expansion(s) because unity_source_index.db has baseController relations without animatorOverrideController.overrideSet/clipPair details. Rebuild the source index to recover precise override candidates.");
            }

            return count;
        }

        private static long TryImportExplicitModelAnimationCandidatesFromSourceIndexFast(
            SqliteConnection libraryConnection,
            SqliteTransaction transaction,
            string sourceIndex,
            List<JObject> modelCatalog,
            List<JObject> animationCatalog)
        {
            try
            {
                using var sourceConnection = new SqliteConnection($"Data Source={sourceIndex};Mode=ReadOnly");
                sourceConnection.Open();
                var hasReverseRelationIndex = HasSqliteIndex(sourceConnection, "idx_source_relations_to");
                var hasRelationIndex = HasSqliteIndex(sourceConnection, "idx_source_relations_relation");
                var animations = new Dictionary<SourceKey, CatalogAsset>();
                foreach (var animation in animationCatalog)
                {
                    if (TryResolveSourceIndexObject(sourceConnection, animation, out var key))
                    {
                        key = key.Normalize();
                        if (!animations.ContainsKey(key))
                        {
                            animations[key] = new CatalogAsset(key, S(animation, "name"), S(animation, "output"), animation);
                        }
                    }
                }

                if (animations.Count == 0)
                {
                    return 0;
                }

                using var insert = libraryConnection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = @"
INSERT INTO model_animation_candidates(model_output, animation_output, relation_source, confidence, score, status, needs_validation, raw_json)
VALUES ($modelOutput, $animationOutput, 'explicit', 'explicit_unity_source_index', 100, $status, $needsValidation, $rawJson);";
                var p = AddParameters(insert, "$modelOutput", "$animationOutput", "$status", "$needsValidation", "$rawJson");

                long count = 0;
                long resolvedModels = 0;
                var insertedOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var modelRaw in modelCatalog)
                {
                    if (!TryResolveSourceIndexObject(sourceConnection, modelRaw, out var modelKey))
                    {
                        continue;
                    }
                    resolvedModels++;

                    var model = new CatalogAsset(modelKey, S(modelRaw, "name"), S(modelRaw, "output"), modelRaw);
                    if (!model.Key.IsValid || string.IsNullOrWhiteSpace(model.Output))
                    {
                        continue;
                    }

                    foreach (var relation in LoadTargetedSourceRelations(sourceConnection, model.Key, S(modelRaw, "sourceType"), hasReverseRelationIndex, hasRelationIndex))
                    {
                        if (!animations.TryGetValue(relation.ClipKey.Normalize(), out var animation))
                        {
                            continue;
                        }

                        if (!insertedOutputs.Add(model.Output + "|" + animation.Output))
                        {
                            continue;
                        }

                        var raw = BuildExplicitSourceCandidateJson(model.Raw, animation.Raw, relation);
                        Set(p, "$modelOutput", model.Output);
                        Set(p, "$animationOutput", animation.Output);
                        Set(p, "$status", GetExplicitCandidateStatus(raw));
                        Set(p, "$needsValidation", NeedsExplicitCandidateValidation(raw) ? 1 : 0);
                        Set(p, "$rawJson", raw.ToString(Formatting.None));
                        insert.ExecuteNonQuery();
                        count++;
                    }
                }

                Logger.Info($"Targeted source-index animation import probe: models={resolvedModels}/{modelCatalog.Count}, animations={animations.Count}/{animationCatalog.Count}, rows={count}");
                return count;
            }
            catch (Exception e)
            {
                Logger.Warning($"Targeted source-index animation import failed; falling back to full source graph. {e.GetType().Name}: {e.Message}");
                return 0;
            }
        }

        private static IEnumerable<SourceAnimationRelation> LoadTargetedSourceRelations(SqliteConnection sourceConnection, SourceKey modelKey, string sourceType, bool hasReverseRelationIndex, bool hasRelationIndex)
        {
            if (string.Equals(sourceType, "Animator", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var relation in LoadTargetedAnimatorRelations(sourceConnection, modelKey, "animator.controller.clip", "Model source is an Animator with an explicit RuntimeAnimatorController."))
                {
                    yield return relation;
                }

                yield break;
            }

            if (!hasReverseRelationIndex)
            {
                // GameObject/Prefab -> Animator/Animation 需要“谁指向这个模型”的反查。
                // Endfield 大源索引常为了磁盘和构建耗时省掉 idx_source_relations_to，
                // 此时不能用缺索引 SQL 触发全表扫描；已有 model_animations sidecar 仍会先被导入。
                if (hasRelationIndex)
                {
                    foreach (var relation in LoadTargetedSharedAvatarControllerRelationsFromForwardConfig(sourceConnection, modelKey))
                    {
                        yield return relation;
                    }
                }

                yield break;
            }

            using var command = sourceConnection.CreateCommand();
            command.CommandText = @"
SELECT controllerClip.to_file, controllerClip.to_path_id,
       'gameObject.hierarchy.animator.controller.clip' AS relation_kind,
       controllerClip.raw_json,
       animator.from_name
FROM source_relations animator INDEXED BY idx_source_relations_to
JOIN source_relations controllerRel INDEXED BY idx_source_relations_from
  ON controllerRel.from_file = animator.from_file
 AND controllerRel.from_path_id = animator.from_path_id
 AND controllerRel.relation = 'animator.controller'
JOIN source_objects controller
  ON controller.serialized_file = controllerRel.to_file
 AND controller.path_id = controllerRel.to_path_id
 AND controller.type = 'AnimatorController'
JOIN source_relations controllerClip INDEXED BY idx_source_relations_from
  ON controllerClip.from_file = controller.serialized_file
 AND controllerClip.from_path_id = controller.path_id
 AND controllerClip.relation = 'animatorController.clip'
WHERE animator.to_file = $modelFile
  AND animator.to_path_id = $modelPathId
  AND animator.relation = 'component.gameObject'
  AND animator.from_type = 'Animator'
UNION ALL
SELECT clipRel.to_file, clipRel.to_path_id,
       'gameObject.hierarchy.animation.clip' AS relation_kind,
       NULL AS raw_json,
       animation.from_name
FROM source_relations animation INDEXED BY idx_source_relations_to
JOIN source_relations clipRel INDEXED BY idx_source_relations_from
  ON clipRel.from_file = animation.from_file
 AND clipRel.from_path_id = animation.from_path_id
 AND clipRel.relation = 'animation.clip'
WHERE animation.to_file = $modelFile
  AND animation.to_path_id = $modelPathId
  AND animation.relation = 'component.gameObject'
  AND animation.from_type = 'Animation';";
            command.Parameters.AddWithValue("$modelFile", modelKey.File);
            command.Parameters.AddWithValue("$modelPathId", modelKey.PathId);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var relationKind = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                    var componentName = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                    var reason = relationKind.Contains(".animation.", StringComparison.OrdinalIgnoreCase)
                        ? $"Animation component '{componentName}' explicitly references this clip."
                        : $"Animator '{componentName}' is attached to the exported GameObject hierarchy.";
                    yield return new SourceAnimationRelation(
                        KeyFromRelation(reader, 0, 1).Normalize(),
                        relationKind,
                        reason,
                        TryReadAnimatorControllerContextForCandidate(reader.IsDBNull(3) ? null : reader.GetString(3)));
                }
            }

            foreach (var relation in LoadTargetedSharedAvatarControllerRelations(sourceConnection, modelKey))
            {
                yield return relation;
            }
        }

        private static IEnumerable<SourceAnimationRelation> LoadTargetedSharedAvatarControllerRelations(SqliteConnection sourceConnection, SourceKey modelKey)
        {
            using var command = sourceConnection.CreateCommand();
            command.CommandText = @"
WITH model_configs AS (
    SELECT DISTINCT inbound.from_file, inbound.from_path_id
    FROM source_relations inbound INDEXED BY idx_source_relations_to
    WHERE inbound.relation = 'monoBehaviour.pptr'
      AND inbound.to_file = $modelFile
      AND inbound.to_path_id = $modelPathId
),
config_avatars AS (
    SELECT DISTINCT
        cfg.from_file AS config_file,
        cfg.from_path_id AS config_path_id,
        COALESCE(configObj.name, '') AS config_name,
        avatarRel.to_file AS avatar_file,
        avatarRel.to_path_id AS avatar_path_id,
        COALESCE(avatarObj.name, '') AS avatar_name
    FROM model_configs cfg
    JOIN source_relations avatarRel INDEXED BY idx_source_relations_from
      ON avatarRel.from_file = cfg.from_file
     AND avatarRel.from_path_id = cfg.from_path_id
     AND avatarRel.relation = 'monoBehaviour.pptr'
    JOIN source_objects avatarObj
      ON avatarObj.serialized_file = avatarRel.to_file
     AND avatarObj.path_id = avatarRel.to_path_id
     AND avatarObj.type = 'Avatar'
    LEFT JOIN source_objects configObj
      ON configObj.serialized_file = cfg.from_file
     AND configObj.path_id = cfg.from_path_id
)
SELECT DISTINCT
       controllerClip.to_file,
       controllerClip.to_path_id,
       controllerClip.raw_json,
       COALESCE(animator.name, '') AS animator_name,
       animator.serialized_file AS animator_file,
       animator.path_id AS animator_path_id,
       COALESCE(controller.name, '') AS controller_name,
       controller.serialized_file AS controller_file,
       controller.path_id AS controller_path_id,
       COALESCE(go.name, '') AS animator_go_name,
       go.serialized_file AS animator_go_file,
       go.path_id AS animator_go_path_id,
       config_avatars.config_name,
       config_avatars.config_file,
       config_avatars.config_path_id,
       config_avatars.avatar_name,
       config_avatars.avatar_file,
       config_avatars.avatar_path_id
FROM config_avatars
JOIN source_relations avatarUse INDEXED BY idx_source_relations_to
  ON avatarUse.to_file = config_avatars.avatar_file
 AND avatarUse.to_path_id = config_avatars.avatar_path_id
 AND avatarUse.relation = 'animator.avatar'
JOIN source_objects animator
  ON animator.serialized_file = avatarUse.from_file
 AND animator.path_id = avatarUse.from_path_id
 AND animator.type = 'Animator'
JOIN source_relations controllerRel INDEXED BY idx_source_relations_from
  ON controllerRel.from_file = animator.serialized_file
 AND controllerRel.from_path_id = animator.path_id
 AND controllerRel.relation = 'animator.controller'
JOIN source_objects controller
  ON controller.serialized_file = controllerRel.to_file
 AND controller.path_id = controllerRel.to_path_id
JOIN source_relations controllerClip INDEXED BY idx_source_relations_from
  ON controllerClip.from_file = controller.serialized_file
 AND controllerClip.from_path_id = controller.path_id
 AND controllerClip.relation = 'animatorController.clip'
LEFT JOIN source_relations goRel INDEXED BY idx_source_relations_from
  ON goRel.from_file = animator.serialized_file
 AND goRel.from_path_id = animator.path_id
 AND goRel.relation = 'component.gameObject'
LEFT JOIN source_objects go
  ON go.serialized_file = goRel.to_file
 AND go.path_id = goRel.to_path_id;";
            command.Parameters.AddWithValue("$modelFile", modelKey.File);
            command.Parameters.AddWithValue("$modelPathId", modelKey.PathId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var evidence = new JObject
                {
                    ["bridge"] = "configPrefabAvatarToAnimatorController",
                    ["configName"] = reader.IsDBNull(12) ? null : reader.GetString(12),
                    ["configFile"] = reader.IsDBNull(13) ? null : reader.GetString(13),
                    ["configPathId"] = reader.IsDBNull(14) ? JValue.CreateNull() : new JValue(reader.GetInt64(14)),
                    ["avatarName"] = reader.IsDBNull(15) ? null : reader.GetString(15),
                    ["avatarFile"] = reader.IsDBNull(16) ? null : reader.GetString(16),
                    ["avatarPathId"] = reader.IsDBNull(17) ? JValue.CreateNull() : new JValue(reader.GetInt64(17)),
                    ["animatorName"] = reader.IsDBNull(3) ? null : reader.GetString(3),
                    ["animatorFile"] = reader.IsDBNull(4) ? null : reader.GetString(4),
                    ["animatorPathId"] = reader.IsDBNull(5) ? JValue.CreateNull() : new JValue(reader.GetInt64(5)),
                    ["animatorModelName"] = reader.IsDBNull(9) ? null : reader.GetString(9),
                    ["animatorModelFile"] = reader.IsDBNull(10) ? null : reader.GetString(10),
                    ["animatorModelPathId"] = reader.IsDBNull(11) ? JValue.CreateNull() : new JValue(reader.GetInt64(11)),
                    ["controllerName"] = reader.IsDBNull(6) ? null : reader.GetString(6),
                    ["controllerFile"] = reader.IsDBNull(7) ? null : reader.GetString(7),
                    ["controllerPathId"] = reader.IsDBNull(8) ? JValue.CreateNull() : new JValue(reader.GetInt64(8)),
                };
                yield return new SourceAnimationRelation(
                    KeyFromRelation(reader, 0, 1).Normalize(),
                    "sharedAvatarController",
                    "Model config explicitly points to this prefab and Avatar; an Animator using the same Avatar explicitly references the Controller clip.",
                    TryReadAnimatorControllerContextForCandidate(reader.IsDBNull(2) ? null : reader.GetString(2)),
                    evidence);
            }
        }

        private static IEnumerable<SourceAnimationRelation> LoadTargetedSharedAvatarControllerRelationsFromForwardConfig(SqliteConnection sourceConnection, SourceKey modelKey)
        {
            using var command = sourceConnection.CreateCommand();
            command.CommandText = @"
WITH model_configs AS (
    SELECT DISTINCT modelRel.from_file, modelRel.from_path_id
    FROM source_objects configObj INDEXED BY idx_source_objects_file_path
    JOIN source_relations modelRel INDEXED BY idx_source_relations_from
      ON modelRel.from_file = configObj.serialized_file
     AND modelRel.from_path_id = configObj.path_id
     AND modelRel.relation = 'monoBehaviour.pptr'
     AND modelRel.to_file = $modelFile
     AND modelRel.to_path_id = $modelPathId
    WHERE configObj.type = 'MonoBehaviour'
      AND configObj.serialized_file = $modelFile
),
config_avatars AS (
    SELECT DISTINCT
        cfg.from_file AS config_file,
        cfg.from_path_id AS config_path_id,
        COALESCE(configObj.name, '') AS config_name,
        avatarRel.to_file AS avatar_file,
        avatarRel.to_path_id AS avatar_path_id,
        COALESCE(avatarObj.name, '') AS avatar_name
    FROM model_configs cfg
    JOIN source_relations avatarRel INDEXED BY idx_source_relations_from
      ON avatarRel.from_file = cfg.from_file
     AND avatarRel.from_path_id = cfg.from_path_id
     AND avatarRel.relation = 'monoBehaviour.pptr'
    JOIN source_objects avatarObj
      ON avatarObj.serialized_file = avatarRel.to_file
     AND avatarObj.path_id = avatarRel.to_path_id
     AND avatarObj.type = 'Avatar'
    LEFT JOIN source_objects configObj
      ON configObj.serialized_file = cfg.from_file
     AND configObj.path_id = cfg.from_path_id
)
SELECT DISTINCT
       controllerClip.to_file,
       controllerClip.to_path_id,
       controllerClip.raw_json,
       COALESCE(animator.name, '') AS animator_name,
       animator.serialized_file AS animator_file,
       animator.path_id AS animator_path_id,
       COALESCE(controller.name, '') AS controller_name,
       controller.serialized_file AS controller_file,
       controller.path_id AS controller_path_id,
       COALESCE(go.name, '') AS animator_go_name,
       go.serialized_file AS animator_go_file,
       go.path_id AS animator_go_path_id,
       config_avatars.config_name,
       config_avatars.config_file,
       config_avatars.config_path_id,
       config_avatars.avatar_name,
       config_avatars.avatar_file,
       config_avatars.avatar_path_id
FROM config_avatars
JOIN source_relations avatarUse INDEXED BY idx_source_relations_relation
  ON avatarUse.relation = 'animator.avatar'
 AND avatarUse.to_file = config_avatars.avatar_file
 AND avatarUse.to_path_id = config_avatars.avatar_path_id
JOIN source_objects animator
  ON animator.serialized_file = avatarUse.from_file
 AND animator.path_id = avatarUse.from_path_id
 AND animator.type = 'Animator'
JOIN source_relations controllerRel INDEXED BY idx_source_relations_from
  ON controllerRel.from_file = animator.serialized_file
 AND controllerRel.from_path_id = animator.path_id
 AND controllerRel.relation = 'animator.controller'
JOIN source_objects controller
  ON controller.serialized_file = controllerRel.to_file
 AND controller.path_id = controllerRel.to_path_id
JOIN source_relations controllerClip INDEXED BY idx_source_relations_from
  ON controllerClip.from_file = controller.serialized_file
 AND controllerClip.from_path_id = controller.path_id
 AND controllerClip.relation = 'animatorController.clip'
LEFT JOIN source_relations goRel INDEXED BY idx_source_relations_from
  ON goRel.from_file = animator.serialized_file
 AND goRel.from_path_id = animator.path_id
 AND goRel.relation = 'component.gameObject'
LEFT JOIN source_objects go
  ON go.serialized_file = goRel.to_file
 AND go.path_id = goRel.to_path_id;";
            command.Parameters.AddWithValue("$modelFile", modelKey.File);
            command.Parameters.AddWithValue("$modelPathId", modelKey.PathId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var evidence = new JObject
                {
                    ["bridge"] = "forwardConfigPrefabAvatarToAnimatorController",
                    ["configName"] = reader.IsDBNull(12) ? null : reader.GetString(12),
                    ["configFile"] = reader.IsDBNull(13) ? null : reader.GetString(13),
                    ["configPathId"] = reader.IsDBNull(14) ? JValue.CreateNull() : new JValue(reader.GetInt64(14)),
                    ["avatarName"] = reader.IsDBNull(15) ? null : reader.GetString(15),
                    ["avatarFile"] = reader.IsDBNull(16) ? null : reader.GetString(16),
                    ["avatarPathId"] = reader.IsDBNull(17) ? JValue.CreateNull() : new JValue(reader.GetInt64(17)),
                    ["animatorName"] = reader.IsDBNull(3) ? null : reader.GetString(3),
                    ["animatorFile"] = reader.IsDBNull(4) ? null : reader.GetString(4),
                    ["animatorPathId"] = reader.IsDBNull(5) ? JValue.CreateNull() : new JValue(reader.GetInt64(5)),
                    ["animatorModelName"] = reader.IsDBNull(9) ? null : reader.GetString(9),
                    ["animatorModelFile"] = reader.IsDBNull(10) ? null : reader.GetString(10),
                    ["animatorModelPathId"] = reader.IsDBNull(11) ? JValue.CreateNull() : new JValue(reader.GetInt64(11)),
                    ["controllerName"] = reader.IsDBNull(6) ? null : reader.GetString(6),
                    ["controllerFile"] = reader.IsDBNull(7) ? null : reader.GetString(7),
                    ["controllerPathId"] = reader.IsDBNull(8) ? JValue.CreateNull() : new JValue(reader.GetInt64(8)),
                };
                yield return new SourceAnimationRelation(
                    KeyFromRelation(reader, 0, 1).Normalize(),
                    "sharedAvatarController",
                    "Model config explicitly points to this prefab and Avatar; animator.avatar was scanned by relation index because the source index has no reverse to_file index.",
                    TryReadAnimatorControllerContextForCandidate(reader.IsDBNull(2) ? null : reader.GetString(2)),
                    evidence);
            }
        }

        private static IEnumerable<SourceAnimationRelation> LoadTargetedAnimatorRelations(SqliteConnection sourceConnection, SourceKey animatorKey, string relationKind, string reason)
        {
            using var command = sourceConnection.CreateCommand();
            command.CommandText = @"
SELECT controllerClip.to_file, controllerClip.to_path_id, controllerClip.raw_json
FROM source_relations controllerRel INDEXED BY idx_source_relations_from
JOIN source_objects controller
  ON controller.serialized_file = controllerRel.to_file
 AND controller.path_id = controllerRel.to_path_id
 AND controller.type = 'AnimatorController'
JOIN source_relations controllerClip INDEXED BY idx_source_relations_from
  ON controllerClip.from_file = controller.serialized_file
 AND controllerClip.from_path_id = controller.path_id
 AND controllerClip.relation = 'animatorController.clip'
WHERE controllerRel.from_file = $animatorFile
  AND controllerRel.from_path_id = $animatorPathId
  AND controllerRel.relation = 'animator.controller';";
            command.Parameters.AddWithValue("$animatorFile", animatorKey.File);
            command.Parameters.AddWithValue("$animatorPathId", animatorKey.PathId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                yield return new SourceAnimationRelation(
                    KeyFromRelation(reader, 0, 1).Normalize(),
                    relationKind,
                    reason,
                    TryReadAnimatorControllerContextForCandidate(reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        private static bool TryResolveSourceIndexObject(SqliteConnection sourceConnection, JObject catalogItem, out SourceKey key)
        {
            key = default;
            var pathId = I(catalogItem, "pathId") ?? 0;
            var type = S(catalogItem, "sourceType");
            var name = S(catalogItem, "name");
            if (pathId == 0 || string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            var source = S(catalogItem, "source");
            var serializedFile = ExtractCatalogSerializedFile(source);
            var suffix = BuildSourceIndexPathSuffix(source);
            using var command = sourceConnection.CreateCommand();
            command.CommandText = @"
SELECT serialized_file, path_id
FROM source_objects
WHERE path_id = $pathId
  AND type = $type
  AND name = $name
  AND ($serializedFile = '' OR serialized_file = $serializedFile)
  AND ($suffix = '' OR source_path = $suffix OR source_path LIKE $suffixLike)
LIMIT 2;";
            command.Parameters.AddWithValue("$pathId", pathId);
            command.Parameters.AddWithValue("$type", type ?? string.Empty);
            command.Parameters.AddWithValue("$name", name ?? string.Empty);
            command.Parameters.AddWithValue("$serializedFile", serializedFile ?? string.Empty);
            command.Parameters.AddWithValue("$suffix", suffix ?? string.Empty);
            command.Parameters.AddWithValue("$suffixLike", "%/" + (suffix ?? string.Empty) + "%");

            using var reader = command.ExecuteReader();
            SourceKey? found = null;
            while (reader.Read())
            {
                var candidate = KeyFromRelation(reader, 0, 1);
                if (found.HasValue && !found.Value.Equals(candidate))
                {
                    return false;
                }

                found = candidate;
            }

            if (!found.HasValue)
            {
                return false;
            }

            key = found.Value;
            return key.IsValid;
        }

        private static string BuildSourceIndexPathSuffix(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return string.Empty;
            }

            var normalized = GetCatalogOuterSourcePath(source).Replace('\\', '/').Trim('/');
            var vfsIndex = normalized.LastIndexOf("/VFS/", StringComparison.OrdinalIgnoreCase);
            if (vfsIndex >= 0)
            {
                return normalized[(vfsIndex + "/VFS/".Length)..];
            }

            var parts = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                return parts[^2] + "/" + parts[^1];
            }

            return parts.Length == 1 ? parts[0] : string.Empty;
        }

        private static string GetCatalogOuterSourcePath(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return string.Empty;
            }

            // Catalog source 对 Endfield 这类 VFS 资源会写成：
            // 外层 .blc|内层 .ab|CAB-xxx。source_objects.source_path 只保存外层文件。
            var pipeIndex = source.IndexOf('|');
            return pipeIndex >= 0 ? source[..pipeIndex] : source;
        }

        private static string ExtractCatalogSerializedFile(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return string.Empty;
            }

            var normalized = source.Replace('\\', '/').Trim();
            var pipeIndex = normalized.LastIndexOf('|');
            if (pipeIndex >= 0 && pipeIndex + 1 < normalized.Length)
            {
                return normalized[(pipeIndex + 1)..].Trim();
            }

            return Path.GetFileName(normalized);
        }

        private static JObject TryReadAnimatorControllerContextForCandidate(string rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                return null;
            }

            try
            {
                var details = JObject.Parse(rawJson)["details"] as JObject;
                if (details == null)
                {
                    return null;
                }

                return new JObject
                {
                    ["source"] = details["source"],
                    ["controllerClipIndex"] = details["controllerClipIndex"],
                    ["baseLayerClip"] = details["baseLayerClip"],
                    ["layers"] = details["layers"],
                    ["stateMachineIndex"] = details["stateMachineIndex"],
                    ["stateIndex"] = details["stateIndex"],
                    ["stateName"] = details["stateName"],
                    ["statePath"] = details["statePath"],
                    ["stateFullPath"] = details["stateFullPath"],
                    ["stateSpeed"] = details["stateSpeed"],
                    ["stateCycleOffset"] = details["stateCycleOffset"],
                    ["stateLoop"] = details["stateLoop"],
                    ["stateMirror"] = details["stateMirror"],
                    ["blendTreeIndex"] = details["blendTreeIndex"],
                    ["nodeIndex"] = details["nodeIndex"],
                    ["nodeBlendType"] = details["nodeBlendType"],
                    ["nodeBlendEvent"] = details["nodeBlendEvent"],
                    ["nodeBlendEventY"] = details["nodeBlendEventY"],
                    ["nodeClipId"] = details["nodeClipId"],
                    ["nodeClipIndex"] = details["nodeClipIndex"],
                    ["nodeDuration"] = details["nodeDuration"],
                    ["nodeCycleOffset"] = details["nodeCycleOffset"],
                    ["nodeMirror"] = details["nodeMirror"],
                };
            }
            catch
            {
                return null;
            }
        }

        private static bool IsPrefabLikeModel(JObject obj)
        {
            var role = S(obj, "libraryRole");
            var sourceType = S(obj, "sourceType");
            return string.Equals(role, "PrefabPrimary", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sourceType, "GameObject", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sourceType, "Animator", StringComparison.OrdinalIgnoreCase);
        }

        private static JObject BuildExplicitSourceCandidateJson(JObject model, JObject animation, SourceAnimationRelation relation)
        {
            var modelGate = BuildModelAnimationGate(model);
            var animationGate = BuildAnimationSemanticGate(animation);
            var directGltfAnimationStatus = S(animation, "directGltfAnimationStatus");
            var directGltfAnimationReady = IsDirectGltfAnimationReady(animation);
            var needsDirectTrsAnimation = IsTrue(animation, "needsDirectTrsAnimation");
            var directProductionPath = GetDirectGltfProductionPath(animation);
            var animationNeedsHumanoidSolve = RequiresDirectHumanoidTrsSolve(animation);
            var standaloneBodyBakeReady = IsStandaloneBodyBakeReady(animation);
            var standaloneBodyBakeStatus = S(animation, "standaloneBodyBakeStatus");
            var directGltfNeedsControllerContext = directGltfAnimationReady
                && !standaloneBodyBakeReady
                && string.Equals(standaloneBodyBakeStatus, "requires_animator_controller_context", StringComparison.OrdinalIgnoreCase);
            var directGltfNeedsDirectTrsSolve = directGltfAnimationReady
                && !standaloneBodyBakeReady
                && string.Equals(standaloneBodyBakeStatus, "needs_direct_trs_animation", StringComparison.OrdinalIgnoreCase);
            var hasAnimatorControllerBodyClip = HasAnimatorControllerBodyClip(relation);
            var requiresAnimatorControllerContext = !directGltfAnimationReady && animationNeedsHumanoidSolve && !needsDirectTrsAnimation && !standaloneBodyBakeReady && !hasAnimatorControllerBodyClip;
            var hasDirectTransformPreview = HasDirectTransformPreview(animation);
            var internalSolverMissingReason = GetInternalHumanoidSolverMissingReason(model);
            var experimentalInternalSolverMissingReason = GetExperimentalInternalHumanoidSolverMissingReason(model);
            var modelHasInternalHumanoidSolver = internalSolverMissingReason is null;
            var modelHasExperimentalInternalHumanoidSolver = experimentalInternalSolverMissingReason is null;
            var modelHasProductionUnityBakeAvatar = HasProductionUnityBakeAvatar(model);
            var modelAvatarDiagnostics = BuildModelAvatarDiagnostics(model);
            var directHumanoidTrsRequired = directGltfNeedsDirectTrsSolve
                || (!directGltfAnimationReady && animationNeedsHumanoidSolve && (needsDirectTrsAnimation || standaloneBodyBakeReady || hasAnimatorControllerBodyClip));
            var requiresInternalHumanoidSolve = directHumanoidTrsRequired && (modelHasInternalHumanoidSolver || modelHasExperimentalInternalHumanoidSolver);
            var fullHumanoidBakeBlocked = directHumanoidTrsRequired && !modelHasProductionUnityBakeAvatar;
            var canRequestUnityBake = false;
            var unityBakeAcceleratedReady = directHumanoidTrsRequired && modelHasProductionUnityBakeAvatar && !requiresAnimatorControllerContext;
            var unityBakeAcceleratedBlockedReason = requiresAnimatorControllerContext
                ? "requires_animator_controller_context"
                : directHumanoidTrsRequired && !modelHasProductionUnityBakeAvatar
                    ? GetProductionUnityBakeAvatarMissingReason(model)
                    : null;
            var useDirectTransformPreviewFirst = directHumanoidTrsRequired && hasDirectTransformPreview;
            var missingInternalHumanoidSolver = directHumanoidTrsRequired && !modelHasInternalHumanoidSolver && !modelHasExperimentalInternalHumanoidSolver && !hasDirectTransformPreview;
            var missingProductionAvatar = directHumanoidTrsRequired && !modelHasProductionUnityBakeAvatar;
            var legacyUnityBakeBlockedReason = requiresAnimatorControllerContext
                ? "requires_animator_controller_context"
                : missingProductionAvatar
                    ? GetProductionUnityBakeAvatarMissingReason(model)
                    : null;
            var directAnimationBlockedReason = directGltfAnimationReady
                ? directGltfNeedsControllerContext
                    ? "requires_animator_controller_context"
                    : directGltfNeedsDirectTrsSolve
                        ? "needs_direct_trs_animation"
                        : null
                : requiresAnimatorControllerContext
                    ? "requires_animator_controller_context"
                    : directHumanoidTrsRequired || needsDirectTrsAnimation
                        ? "needs_direct_trs_animation"
                        : null;
            var productionAnimationReady = directGltfAnimationReady && directAnimationBlockedReason == null;
            var nextAction = directGltfAnimationReady
                ? directGltfNeedsControllerContext
                    ? "inspect_animator_controller_context"
                    : directGltfNeedsDirectTrsSolve
                    ? "implement_direct_trs_animation"
                    : "generate_preview_gltf"
                : requiresInternalHumanoidSolve
                ? "generate_preview_gltf"
                : needsDirectTrsAnimation
                ? "implement_direct_trs_animation"
                : requiresAnimatorControllerContext
                ? "inspect_animator_controller_context"
                : missingInternalHumanoidSolver
                ? "inspect_missing_humanoid_avatar"
                : directHumanoidTrsRequired
                    ? "implement_direct_trs_animation"
                : "generate_preview_gltf";
            if (!modelGate.Ready)
            {
                // 动画是第二阶段。模型材质/贴图/skin/来源域没过时，
                // 只保留 Unity 显式关系作诊断，不允许进入默认预览或生产结论。
                directAnimationBlockedReason = "model_not_animation_ready";
                productionAnimationReady = false;
                nextAction = "fix_model_first";
                unityBakeAcceleratedReady = false;
                unityBakeAcceleratedBlockedReason ??= "model_not_animation_ready";
            }
            else if (!animationGate.Ready)
            {
                // 显式引用只能证明 Unity 用过这个 clip；剧情/UI/pose 等上下文动画不能默认升级成可复用动作。
                directAnimationBlockedReason ??= "animation_not_default_candidate";
                productionAnimationReady = false;
                nextAction = "review_animation_context";
                unityBakeAcceleratedReady = false;
                unityBakeAcceleratedBlockedReason ??= "animation_not_default_candidate";
            }
            var sharedAvatarBridge = IsSharedAvatarBridgeRelation(relation);

            return new JObject
            {
                // 大库会产生数百万条候选。候选 raw 只保留后续筛选/预览必须字段，
                // 名称、输出路径、动画类型等重复信息从 assets 表按 output join 读取。
                ["animationAsset"] = S(animation, "animationAsset"),
                ["relation"] = relation.Relation,
                ["relationSource"] = "explicit",
                ["confidence"] = "explicit_unity_source_index",
                ["relationEvidence"] = relation.RelationEvidence,
                ["explicitCandidateRequiresVisualValidation"] = sharedAvatarBridge,
                ["relationReviewHint"] = sharedAvatarBridge ? "sharedAvatarBridgeNeedsVisualValidation" : null,
                ["sharedAvatarBridgeRule"] = sharedAvatarBridge
                    ? new JObject
                    {
                        ["rule"] = "模型经配置 PPtr 指向 Avatar，同一 Avatar 的 Animator 再显式指向 Controller/Clip。它是确定性候选，不是视觉验收结论。",
                        ["modelGateRequired"] = true,
                        ["visualValidationRequired"] = true,
                    }
                    : null,
                ["legacyUnityBakeSupported"] = canRequestUnityBake,
                ["requiresUnityBake"] = canRequestUnityBake,
                ["directGltfAnimationReady"] = directGltfAnimationReady,
                ["directGltfAnimationStatus"] = directGltfAnimationStatus,
                ["directGltfPreviewReady"] = directGltfAnimationReady && modelGate.Ready,
                ["needsDirectTrsAnimation"] = needsDirectTrsAnimation,
                ["deprecatedUnityBakeOnly"] = IsTrue(animation, "deprecatedUnityBakeOnly"),
                ["directHumanoidTrsRequired"] = directHumanoidTrsRequired,
                ["unityBakeAcceleratedReady"] = unityBakeAcceleratedReady,
                ["unityBakeAcceleratedBlockedReason"] = unityBakeAcceleratedBlockedReason,
                ["directAnimationBlocked"] = directAnimationBlockedReason != null,
                ["directAnimationBlockedReason"] = directAnimationBlockedReason,
                ["productionAnimationReady"] = productionAnimationReady,
                ["productionAnimationBlockedReason"] = directAnimationBlockedReason,
                ["fullHumanoidBakeRequired"] = directHumanoidTrsRequired,
                ["productionUnityBakeReady"] = canRequestUnityBake && modelGate.Ready,
                ["productionUnityBakeBlocked"] = !modelGate.Ready || requiresAnimatorControllerContext || missingProductionAvatar,
                ["productionUnityBakeBlockedReason"] = !modelGate.Ready
                    ? "model_not_animation_ready"
                    : legacyUnityBakeBlockedReason,
                ["productionAnimationPath"] = productionAnimationReady
                    ? directProductionPath
                    : !modelGate.Ready
                    ? "ModelNotAnimationReady"
                    : needsDirectTrsAnimation
                    ? "NeedsDirectTrsAnimation"
                    : directGltfNeedsControllerContext
                    ? "NeedsAnimatorControllerContext"
                    : directGltfNeedsDirectTrsSolve
                    ? "NeedsDirectTrsAnimation"
                    : requiresAnimatorControllerContext
                    ? "NeedsAnimatorControllerContext"
                    : directHumanoidTrsRequired
                    ? "NeedsDirectTrsAnimation"
                    : "DirectGltf",
                ["requiresInternalHumanoidSolve"] = requiresInternalHumanoidSolve,
                ["internalHumanoidSolverReady"] = modelHasInternalHumanoidSolver,
                ["experimentalInternalHumanoidSolverReady"] = modelHasExperimentalInternalHumanoidSolver,
                ["experimentalInternalHumanoidSolver"] = directHumanoidTrsRequired && modelHasExperimentalInternalHumanoidSolver,
                ["experimentalInternalHumanoidSolverStatus"] = modelHasExperimentalInternalHumanoidSolver
                    ? (modelHasInternalHumanoidSolver ? "complete_avatar_mapping" : "structural_avatar_fallback")
                    : "missing",
                ["experimentalInternalHumanoidSolverMissingReason"] = experimentalInternalSolverMissingReason,
                ["internalHumanoidSolverProductionReady"] = modelHasInternalHumanoidSolver,
                ["internalHumanoidSolverNotProductionReadyReason"] = !modelHasInternalHumanoidSolver && modelHasExperimentalInternalHumanoidSolver
                    ? internalSolverMissingReason
                    : null,
                ["missingInternalHumanoidSolver"] = missingInternalHumanoidSolver,
                ["missingInternalHumanoidSolverReason"] = missingInternalHumanoidSolver
                    ? experimentalInternalSolverMissingReason ?? internalSolverMissingReason
                    : null,
                ["modelAvatarDiagnostics"] = modelAvatarDiagnostics,
                ["fullHumanoidBakeBlocked"] = requiresAnimatorControllerContext || fullHumanoidBakeBlocked,
                ["fullHumanoidBakeBlockedReason"] = requiresAnimatorControllerContext
                    ? "requires_animator_controller_context"
                    : fullHumanoidBakeBlocked
                        ? GetProductionUnityBakeAvatarMissingReason(model)
                        : null,
                ["standaloneBodyBakeReady"] = standaloneBodyBakeReady,
                ["standaloneBodyBakeStatus"] = standaloneBodyBakeStatus,
                ["standaloneBodyBakeReason"] = S(animation, "standaloneBodyBakeReason"),
                ["standaloneBodyRequiresAnimatorControllerContext"] = directGltfNeedsControllerContext,
                ["standaloneBodyRequiresDirectTrsSolve"] = directGltfNeedsDirectTrsSolve,
                ["directTrsSolveRequiresProductionAvatar"] = directHumanoidTrsRequired && !modelHasProductionUnityBakeAvatar,
                ["directTrsSolveBlockedReason"] = directHumanoidTrsRequired && !modelHasProductionUnityBakeAvatar
                    ? GetProductionUnityBakeAvatarMissingReason(model)
                    : null,
                ["animatorControllerContext"] = relation.AnimatorControllerContext,
                ["animatorControllerBodyClipReady"] = hasAnimatorControllerBodyClip,
                ["partialDirectGltf"] = useDirectTransformPreviewFirst,
                ["partialDirectGltfReason"] = useDirectTransformPreviewFirst
                    ? "Animation has deterministic Transform TRS curves. AnimeStudio writes those curves to glTF first; Humanoid/Muscle curves remain separate diagnostic data until their direct TRS solver passes visual validation."
                    : null,
                ["nextAction"] = nextAction,
                ["modelReadyForAnimation"] = modelGate.Ready,
                ["modelAnimationBlockedReason"] = modelGate.Ready ? null : string.Join(",", modelGate.Reasons),
                ["modelAnimationGate"] = BuildModelAnimationGateJson(modelGate),
                ["defaultAnimationCandidateReady"] = animationGate.Ready,
                ["animationCandidateBlockedReason"] = animationGate.Ready ? null : string.Join(",", animationGate.Reasons),
                ["animationSemanticGate"] = BuildAnimationSemanticGateJson(animationGate),
                ["matchReason"] = relation.Reason,
            };
        }

        private static string GetExplicitCandidateStatus(JObject rawCandidate)
        {
            if (!IsTrue(rawCandidate, "modelReadyForAnimation"))
            {
                return "model_not_animation_ready";
            }

            if (!IsTrue(rawCandidate, "defaultAnimationCandidateReady"))
            {
                return "animation_not_default_candidate";
            }

            return "explicit";
        }

        private static bool NeedsExplicitCandidateValidation(JObject rawCandidate)
        {
            return !IsTrue(rawCandidate, "modelReadyForAnimation")
                || !IsTrue(rawCandidate, "defaultAnimationCandidateReady")
                || IsTrue(rawCandidate, "explicitCandidateRequiresVisualValidation");
        }

        private static bool IsSharedAvatarBridgeRelation(SourceAnimationRelation relation)
        {
            return string.Equals(relation.Relation, "sharedAvatarController", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, JObject> ReadModelCatalogByOutput(string root)
        {
            var catalogPath = Path.Combine(root, "asset_catalog.jsonl");
            if (!File.Exists(catalogPath))
            {
                return new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            }

            var catalog = ReadJsonLines(catalogPath).ToList();
            AttachModelValidation(root, catalog);
            return catalog
                .Where(x => string.Equals(S(x, "kind"), "Model", StringComparison.OrdinalIgnoreCase))
                .Where(x => !string.IsNullOrWhiteSpace(S(x, "output")))
                .GroupBy(x => NormalizeLibraryOutputForJoin(root, S(x, "output")), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        }

        private static void AttachModelValidation(string root, IReadOnlyList<JObject> catalog)
        {
            var validationByOutput = ReadModelValidationByOutput(root);
            if (validationByOutput.Count == 0)
            {
                return;
            }

            foreach (var model in catalog.Where(x => string.Equals(S(x, "kind"), "Model", StringComparison.OrdinalIgnoreCase)))
            {
                var output = NormalizeLibraryOutputForJoin(root, S(model, "output"));
                if (validationByOutput.TryGetValue(output, out var validation))
                {
                    model["modelValidation"] = validation.DeepClone();
                }
            }
        }

        private static Dictionary<string, JObject> ReadModelValidationByOutput(string root)
        {
            var path = Path.Combine(root, "model_validation.json");
            if (!File.Exists(path))
            {
                return new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                var report = JObject.Parse(File.ReadAllText(path).TrimStart('\uFEFF'));
                return report["models"]?
                    .OfType<JObject>()
                    .Where(x => !string.IsNullOrWhiteSpace(S(x, "Path")))
                    .GroupBy(x => NormalizeLibraryOutputForJoin(root, S(x, "Path")), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException e)
            {
                Logger.Warning($"Skipping invalid model_validation.json for animation gate: {e.Message}");
                return new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void ApplyModelAnimationGate(JObject candidate, ModelAnimationGate gate)
        {
            candidate["modelReadyForAnimation"] = gate.Ready;
            candidate["modelAnimationBlockedReason"] = gate.Ready ? null : string.Join(",", gate.Reasons);
            candidate["modelAnimationGate"] = BuildModelAnimationGateJson(gate);
            if (gate.Ready)
            {
                return;
            }

            candidate["directGltfPreviewReady"] = false;
            candidate["productionAnimationReady"] = false;
            candidate["directAnimationBlocked"] = true;
            candidate["directAnimationBlockedReason"] = "model_not_animation_ready";
            candidate["productionAnimationBlockedReason"] = "model_not_animation_ready";
            candidate["productionAnimationPath"] = "ModelNotAnimationReady";
            candidate["productionUnityBakeReady"] = false;
            candidate["productionUnityBakeBlocked"] = true;
            candidate["productionUnityBakeBlockedReason"] = "model_not_animation_ready";
            candidate["unityBakeAcceleratedReady"] = false;
            candidate["unityBakeAcceleratedBlockedReason"] = "model_not_animation_ready";
            candidate["nextAction"] = "fix_model_first";
        }

        private static ModelAnimationGate BuildModelAnimationGate(JObject model)
        {
            var reasons = new List<string>();
            var validation = model?["modelValidation"] as JObject;
            var body = validation?["Body"] as JObject;

            if (validation == null)
            {
                reasons.Add("model_validation_missing");
            }

            var status = S(validation, "Status");
            if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("model_validation_not_ok");
            }

            var bodyStatus = S(body, "ModelBodyStatus");
            if (!string.IsNullOrWhiteSpace(bodyStatus) && !string.Equals(bodyStatus, "ok", StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("model_body_not_ok");
            }

            if ((I(model, "meshCount") ?? I(body, "PrimitiveCount") ?? 0) <= 0)
            {
                reasons.Add("no_mesh");
            }
            if ((I(model, "vertexCount") ?? I(body, "PositionVertexCount") ?? 0) <= 0)
            {
                reasons.Add("no_vertices");
            }
            if ((I(model, "materialCount") ?? I(body, "MaterialCount") ?? 0) <= 0)
            {
                reasons.Add("no_materials");
            }
            if ((I(model, "textureCount") ?? I(body, "TextureCount") ?? 0) <= 0
                && (I(model, "materialImageCount") ?? I(body, "ImageCount") ?? 0) <= 0)
            {
                reasons.Add("no_textures");
            }
            if ((I(model, "materialImageCount") ?? I(body, "ImageCount") ?? 0) <= 0)
            {
                reasons.Add("no_material_images");
            }
            if (IsTrue(model, "materialMissingRendererBinding"))
            {
                reasons.Add("missing_renderer_material_binding");
            }
            if (ModelMaterialNeedsAnimationReview(model))
            {
                reasons.Add("material_customization_tint_not_ready");
            }
            if ((I(model, "unresolvedModelDependencyCount") ?? 0) > 0)
            {
                reasons.Add("unresolved_model_dependencies");
            }
            if ((I(body, "MissingImageCount") ?? 0) > 0)
            {
                reasons.Add("missing_images");
            }
            if ((I(body, "EmptyImageCount") ?? 0) > 0)
            {
                reasons.Add("empty_images");
            }
            if ((I(body, "SkinnedMeshNodeCount") ?? 0) > 0
                && body?["HasCompleteSkinBinding"]?.Type == JTokenType.Boolean
                && !body["HasCompleteSkinBinding"].Value<bool>())
            {
                reasons.Add("incomplete_skin_binding");
            }
            if (IsDiagnosticModelInstance(model))
            {
                reasons.Add("diagnostic_instance_not_default_animation_gate");
            }
            var completenessStatus = S(model, "modelCompletenessStatus");
            if (string.Equals(completenessStatus, "modular_incomplete", StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("modular_character_incomplete");
            }

            var evidence = new JObject
            {
                ["rule"] = "只有模型 Mesh/UV/材质/贴图/skin/bbox 和来源域先过关，才允许进入默认动画预览或生产结论。Unity 显式关系会保留作诊断。",
                ["name"] = S(model, "name"),
                ["output"] = S(model, "output"),
                ["resourceKind"] = S(model, "resourceKind"),
                ["container"] = S(model, "container"),
                ["source"] = S(model, "source"),
                ["modelValidationPresent"] = validation != null,
                ["modelValidationStatus"] = status,
                ["modelBodyStatus"] = bodyStatus,
                ["meshCount"] = I(model, "meshCount") ?? I(body, "PrimitiveCount"),
                ["vertexCount"] = I(model, "vertexCount") ?? I(body, "PositionVertexCount"),
                ["materialCount"] = I(model, "materialCount") ?? I(body, "MaterialCount"),
                ["textureCount"] = I(model, "textureCount") ?? I(body, "TextureCount"),
                ["materialImageCount"] = I(model, "materialImageCount") ?? I(body, "ImageCount"),
                ["materialStatus"] = S(model, "materialStatus"),
                ["materialNeedsCustomizationTint"] = IsTrue(model, "materialNeedsCustomizationTint"),
                ["materialStatusCounts"] = model?["materialStatusCounts"]?.DeepClone(),
                ["modelConversionIssueCount"] = I(model, "modelConversionIssueCount") ?? 0,
                ["modelConversionIssueTypes"] = model?["modelConversionIssueTypes"]?.DeepClone(),
                ["skinCount"] = I(model, "skinCount") ?? I(body, "SkinCount"),
                ["unresolvedModelDependencyCount"] = I(model, "unresolvedModelDependencyCount") ?? 0,
                ["unresolvedModelDependencyTypes"] = model?["unresolvedModelDependencyTypes"]?.DeepClone(),
                ["missingImageCount"] = I(body, "MissingImageCount") ?? 0,
                ["emptyImageCount"] = I(body, "EmptyImageCount") ?? 0,
                ["diagnosticModelInstance"] = IsDiagnosticModelInstance(model),
                ["modelCompletenessStatus"] = completenessStatus,
                ["modelCompletenessMissingRoles"] = model?["modelCompletenessMissingRoles"]?.DeepClone(),
            };

            return new ModelAnimationGate(
                reasons.Count == 0,
                reasons.Count == 0 ? "ready" : "blocked",
                reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                evidence);
        }

        private static bool ModelMaterialNeedsAnimationReview(JObject model)
        {
            if (model == null)
            {
                return false;
            }

            if (IsTrue(model, "materialNeedsCustomizationTint"))
            {
                return true;
            }

            var status = S(model, "materialStatus");
            return string.Equals(status, "needsCustomizationTint", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "needsCustomShaderLayer", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "tintParametersOnly", StringComparison.OrdinalIgnoreCase);
        }

        private static JObject BuildModelAnimationGateJson(ModelAnimationGate gate)
        {
            var evidence = gate.Evidence != null ? (JObject)gate.Evidence.DeepClone() : new JObject();
            return new JObject
            {
                ["status"] = gate.Status,
                ["ready"] = gate.Ready,
                ["reasons"] = new JArray(gate.Reasons),
                ["evidence"] = evidence,
            };
        }

        private static ModelAnimationGate BuildAnimationSemanticGate(JObject animation)
        {
            var reasons = new List<string>();
            if (IsDiagnosticAnimationClip(animation))
            {
                reasons.Add("diagnostic_or_context_animation_not_default_candidate");
            }

            var evidence = new JObject
            {
                ["rule"] = "显式 AnimatorController 引用只说明 Unity 状态机使用了该 clip；剧情/UI/pose/deco 等上下文动画默认只做诊断，不进入生产动画 smoke。",
                ["name"] = S(animation, "name"),
                ["container"] = S(animation, "container"),
                ["source"] = S(animation, "source"),
                ["output"] = S(animation, "output"),
                ["diagnosticAnimationClip"] = IsDiagnosticAnimationClip(animation),
            };

            return new ModelAnimationGate(
                reasons.Count == 0,
                reasons.Count == 0 ? "ready" : "blocked",
                reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                evidence);
        }

        private static JObject BuildAnimationSemanticGateJson(ModelAnimationGate gate)
        {
            var evidence = gate.Evidence != null ? (JObject)gate.Evidence.DeepClone() : new JObject();
            return new JObject
            {
                ["status"] = gate.Status,
                ["ready"] = gate.Ready,
                ["reasons"] = new JArray(gate.Reasons),
                ["evidence"] = evidence,
            };
        }

        private static bool IsDiagnosticModelInstance(JObject model)
        {
            var text = string.Join(
                "/",
                new[] { S(model, "container"), S(model, "source"), S(model, "name"), S(model, "output") }
                    .Where(x => !string.IsNullOrWhiteSpace(x))
            ).Replace('\\', '/').ToLowerInvariant();
            return Regex.IsMatch(
                text,
                @"(^|[/_.\-\s])(?:dialog|timeline|levelseq|ui|uimodel|preview|deco|pose|camera|cutscene|postmodels?|abilityentity|tmpobject|tmp)(?:$|[/_.\-\s0-9])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static bool IsDiagnosticAnimationClip(JObject animation)
        {
            var text = string.Join(
                "/",
                new[] { S(animation, "container"), S(animation, "source"), S(animation, "name"), S(animation, "output") }
                    .Where(x => !string.IsNullOrWhiteSpace(x))
            ).Replace('\\', '/').ToLowerInvariant();
            return Regex.IsMatch(
                text,
                @"(^|[/_.\-\s])(?:cs|cutscene|dialog|timeline|levelseq|ui|uimodel|preview|deco|pose|camera)(?:$|[/_.\-\s0-9])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static JObject BuildModelAvatarDiagnostics(JObject model)
        {
            var avatar = model?["avatar"] as JObject;
            if (avatar == null)
            {
                return new JObject
                {
                    ["status"] = "missing_avatar_object",
                };
            }

            var internalSolverMissingReason = GetInternalHumanoidSolverMissingReason(model);
            var experimentalInternalSolverMissingReason = GetExperimentalInternalHumanoidSolverMissingReason(model);
            var productionAvatarMissingReason = GetProductionUnityBakeAvatarMissingReason(model);
            return new JObject
            {
                ["name"] = S(avatar, "name"),
                ["source"] = S(avatar, "source"),
                ["pathId"] = avatar["pathId"],
                ["hasHumanDescription"] = B(avatar, "hasHumanDescription") == 1,
                ["humanBoneCount"] = I(avatar, "humanBoneCount") ?? 0,
                ["skeletonBoneCount"] = I(avatar, "skeletonBoneCount") ?? 0,
                ["avatarSkeletonNodeCount"] = I(avatar, "avatarSkeletonNodeCount") ?? 0,
                ["hasValidHumanBoneIndex"] = internalSolverMissingReason != "missing_human_bone_index",
                ["internalSolverReady"] = internalSolverMissingReason == null,
                ["internalSolverMissingReason"] = internalSolverMissingReason,
                ["experimentalInternalSolverReady"] = experimentalInternalSolverMissingReason == null,
                ["experimentalInternalSolverMissingReason"] = experimentalInternalSolverMissingReason,
                ["experimentalInternalSolverRule"] = "允许使用 Unity AvatarConstant avatarSkeleton/defaultPose 和 glTF 目标骨架做结构 fallback 生成实验 glTF TRS；这不是生产 Avatar/HumanBoneIndex 完整验收。",
                ["productionAvatarReady"] = productionAvatarMissingReason == null,
                ["productionAvatarMissingReason"] = productionAvatarMissingReason,
            };
        }

        private static bool HasAnimatorControllerBodyClip(SourceAnimationRelation relation)
        {
            // 原神这类 controller 常把同一个状态拆成身体主动作层 + 角色附件/叠加层。
            // 只有源索引明确记录同状态 baseLayerClip 时，才允许把辅助 clip 接到身体主 clip 继续做直接 TRS 研究。
            // 这里不按名称、骨骼数量或目录猜测，避免把普通 root/accessory clip 误升级为可烘焙身体动画。
            var baseLayerClip = relation?.AnimatorControllerContext?["baseLayerClip"] as JObject;
            var clip = baseLayerClip?["clip"] as JObject;
            return (long?)clip?["pathId"] != null;
        }

        private static bool IsStandaloneBodyBakeReady(JObject animation)
        {
            var token = animation?["standaloneBodyBakeReady"];
            if (token == null || token.Type == JTokenType.Null)
            {
                // 旧库没有这个字段时保持兼容；重新导出/重建索引后才启用严格门禁。
                return true;
            }

            return token.Type == JTokenType.Boolean
                ? token.Value<bool>()
                : bool.TryParse(token.ToString(), out var value) && value;
        }

        private static bool IsDirectGltfAnimationReady(JObject animation)
        {
            if (IsTrue(animation, "directTrsAnimationReady") || IsTrue(animation, "directWeightsAnimationReady"))
            {
                return true;
            }

            var status = S(animation, "directGltfAnimationStatus") ?? string.Empty;
            return status.Equals("direct_trs", StringComparison.OrdinalIgnoreCase)
                || status.Equals("direct_weights", StringComparison.OrdinalIgnoreCase)
                || status.Equals("direct_trs_weights", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetDirectGltfProductionPath(JObject animation)
        {
            if (IsTrue(animation, "directTrsAnimationReady") && IsTrue(animation, "directWeightsAnimationReady"))
            {
                return "DirectGltfTrsWeights";
            }
            if (IsTrue(animation, "directWeightsAnimationReady"))
            {
                return "DirectGltfWeights";
            }
            return "DirectGltfTrs";
        }

        private static bool HasDirectTransformPreview(JObject animation)
        {
            if (!IsTrue(animation, "directTrsAnimationReady"))
            {
                return false;
            }

            var transformBindingCount = I(animation, "transformBindingCount") ?? 0;
            var coreTransformBindingCount = I(animation, "coreTransformBindingCount") ?? 0;
            var type = S(animation, "animationType") ?? string.Empty;
            return transformBindingCount > 0
                && coreTransformBindingCount > 0
                && type.Contains("Transform", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasCompleteInternalHumanoidSolver(JObject model)
        {
            return GetInternalHumanoidSolverMissingReason(model) is null;
        }

        private static bool HasExperimentalInternalHumanoidSolver(JObject model)
        {
            return GetExperimentalInternalHumanoidSolverMissingReason(model) is null;
        }

        private static bool HasProductionUnityBakeAvatar(JObject model)
        {
            return GetProductionUnityBakeAvatarMissingReason(model) is null;
        }

        private static string GetProductionUnityBakeAvatarMissingReason(JObject model)
        {
            var avatar = model?["avatar"] as JObject;
            if (avatar == null)
            {
                return "missing_avatar_object";
            }

            // 生产 Unity bake 只接受真实 Unity prefab/Animator.avatar，或完整
            // HumanDescription.humanBones + skeletonBones。AvatarConstant/internalSolver
            // 虽然来自 Unity 序列化数据，但重新 BuildHumanAvatar 不等于游戏原始
            // UnityEngine.Avatar；原神样本已经证明这条路会出现背手、折腿等错误。
            var humanBones = avatar["humanBones"] as JArray;
            var skeletonBones = avatar["skeletonBones"] as JArray;
            if (humanBones != null && humanBones.Count > 0 && skeletonBones != null && skeletonBones.Count > 0)
            {
                return null;
            }

            var hasHumanDescriptionFlag = B(avatar, "hasHumanDescription") == 1;
            if (humanBones == null || humanBones.Count == 0)
            {
                return HasCompleteAvatarConstantOracle(avatar)
                    ? "avatar_constant_oracle_diagnostic_only"
                    : hasHumanDescriptionFlag
                        ? "empty_human_description_human_bones"
                        : "missing_human_description_human_bones";
            }
            if (skeletonBones == null || skeletonBones.Count == 0)
            {
                return HasCompleteAvatarConstantOracle(avatar)
                    ? "avatar_constant_oracle_diagnostic_only"
                    : hasHumanDescriptionFlag
                        ? "empty_human_description_skeleton_bones"
                        : "missing_human_description_skeleton_bones";
            }

            return null;
        }

        private static string GetInternalHumanoidSolverMissingReason(JObject model)
        {
            var avatar = model?["avatar"] as JObject;
            if (avatar == null)
            {
                return "missing_avatar_object";
            }
            if (avatar["internalSolver"] is not JObject solver || !solver.HasValues)
            {
                return "missing_internal_solver";
            }

            // 旧索引可能只有 preQ/postQ。缺 Unity Avatar 参考姿态时会把手脚解反，不能进入生产预览队列。
            var skeleton = solver["skeleton"] as JObject;
            var nodes = skeleton?["nodes"] as JArray;
            var humanSkeletonPose = skeleton?["humanSkeletonPose"] as JArray;
            var humanBoneIndex = solver["humanBoneIndex"] as JArray;
            var avatarSkeleton = solver["avatarSkeleton"] as JObject;
            var avatarSkeletonNodes = avatarSkeleton?["nodes"] as JArray;
            var avatarSkeletonDefaultPose = avatarSkeleton?["defaultPose"] as JArray;
            if (nodes is null || nodes.Count == 0)
            {
                if (avatarSkeletonNodes is null || avatarSkeletonNodes.Count == 0)
                {
                    return "missing_solver_skeleton_nodes";
                }
                if (avatarSkeletonDefaultPose is null || avatarSkeletonDefaultPose.Count < avatarSkeletonNodes.Count)
                {
                    return "missing_avatar_pose_metadata";
                }
            }
            else if (humanSkeletonPose is null || humanSkeletonPose.Count < nodes.Count)
            {
                return "missing_avatar_pose_metadata";
            }
            if (!HasValidHumanBoneIndex(humanBoneIndex))
            {
                return "missing_human_bone_index";
            }

            return null;
        }

        private static string GetExperimentalInternalHumanoidSolverMissingReason(JObject model)
        {
            var avatar = model?["avatar"] as JObject;
            if (avatar == null)
            {
                return "missing_avatar_object";
            }
            if (avatar["internalSolver"] is not JObject solver || !solver.HasValues)
            {
                return "missing_internal_solver";
            }

            // Endfield 这类 Avatar 常没有有效 humanBoneIndex，但包含
            // AvatarConstant 的 avatarSkeleton/defaultPose。内部求解器可以用
            // glTF 目标骨架 + 结构 fallback 生成实验 TRS 预览；它仍然不能当作
            // 生产级 Avatar 映射完成。
            var skeleton = solver["skeleton"] as JObject;
            var nodes = skeleton?["nodes"] as JArray;
            var humanSkeletonPose = skeleton?["humanSkeletonPose"] as JArray;
            var avatarSkeleton = solver["avatarSkeleton"] as JObject;
            var avatarSkeletonNodes = avatarSkeleton?["nodes"] as JArray;
            var avatarSkeletonDefaultPose = avatarSkeleton?["defaultPose"] as JArray;
            if (nodes is not null && nodes.Count > 0)
            {
                if (humanSkeletonPose is null || humanSkeletonPose.Count < nodes.Count)
                {
                    return "missing_avatar_pose_metadata";
                }

                return null;
            }

            if (avatarSkeletonNodes is null || avatarSkeletonNodes.Count == 0)
            {
                return "missing_solver_skeleton_nodes";
            }
            if (avatarSkeletonDefaultPose is null || avatarSkeletonDefaultPose.Count < avatarSkeletonNodes.Count)
            {
                return "missing_avatar_pose_metadata";
            }

            return null;
        }

        private static bool HasValidHumanBoneIndex(JArray humanBoneIndex)
        {
            if (humanBoneIndex is null || humanBoneIndex.Count == 0)
            {
                return false;
            }

            return humanBoneIndex
                .Values<int?>()
                .Any(x => x.GetValueOrDefault(-1) >= 0);
        }

        private static bool HasCompleteAvatarConstantOracle(JObject avatar)
        {
            return HasCompleteOraclePayload(avatar?["oracle"] as JObject)
                || HasCompleteLegacyAvatarConstant(avatar?["internalSolver"] as JObject);
        }

        private static bool HasCompleteOraclePayload(JObject oracle)
        {
            var humanBoneIndex = oracle?["humanBoneIndex"] as JArray;
            var humanSkeleton = oracle?["humanSkeleton"] as JObject;
            var avatarSkeleton = oracle?["avatarSkeleton"] as JObject;
            var humanNodes = humanSkeleton?["nodes"] as JArray;
            var humanPose = humanSkeleton?["pose"] as JArray;
            var avatarNodes = avatarSkeleton?["nodes"] as JArray;
            var avatarDefaultPose = avatarSkeleton?["defaultPose"] as JArray;
            return HasValidHumanBoneIndex(humanBoneIndex)
                && humanNodes != null && humanNodes.Count > 0
                && humanPose != null && humanPose.Count >= humanNodes.Count
                && avatarNodes != null && avatarNodes.Count > 0
                && avatarDefaultPose != null && avatarDefaultPose.Count >= avatarNodes.Count;
        }

        private static bool HasCompleteLegacyAvatarConstant(JObject solver)
        {
            var humanBoneIndex = solver?["humanBoneIndex"] as JArray;
            var skeleton = solver?["skeleton"] as JObject;
            var nodes = skeleton?["nodes"] as JArray;
            var humanSkeletonPose = skeleton?["humanSkeletonPose"] as JArray;
            var avatarSkeleton = solver?["avatarSkeleton"] as JObject;
            var avatarSkeletonNodes = avatarSkeleton?["nodes"] as JArray;
            var avatarSkeletonDefaultPose = avatarSkeleton?["defaultPose"] as JArray;
            return HasValidHumanBoneIndex(humanBoneIndex)
                && nodes != null && nodes.Count > 0
                && humanSkeletonPose != null && humanSkeletonPose.Count >= nodes.Count
                && avatarSkeletonNodes != null && avatarSkeletonNodes.Count > 0
                && avatarSkeletonDefaultPose != null && avatarSkeletonDefaultPose.Count >= avatarSkeletonNodes.Count;
        }

        private static bool RequiresDirectHumanoidTrsSolve(JObject animation)
        {
            var type = S(animation, "animationType") ?? string.Empty;
            var transformBindingCount = I(animation, "transformBindingCount") ?? 0;
            var humanoidBindingCount = I(animation, "humanoidBindingCount") ?? 0;
            if (transformBindingCount > 0 && humanoidBindingCount == 0 && type.Contains("Transform", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return humanoidBindingCount > 0
                || type.Contains("Humanoid", StringComparison.OrdinalIgnoreCase);
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
                        var bindingId = InsertAnimationBinding(connection, transaction, obj, "unity_relations.jsonl");
                        counts["animationBindingPaths"] += InsertAnimationBindingPaths(connection, transaction, bindingId, obj);
                        counts["animationBindings"]++;
                    }
                }
            }

            var bindingPath = Path.Combine(root, "animation_bindings.jsonl");
            if (File.Exists(bindingPath))
            {
                foreach (var obj in ReadJsonLines(bindingPath))
                {
                    var bindingId = InsertAnimationBinding(connection, transaction, obj, "animation_bindings.jsonl");
                    counts["animationBindingPaths"] += InsertAnimationBindingPaths(connection, transaction, bindingId, obj);
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
                if (ShouldSkipSQLiteArtifact(root, fullPath, fullDbPath))
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

        private static long InsertAnimationBinding(SqliteConnection connection, SqliteTransaction transaction, JObject obj, string sourceFile)
        {
            var animation = obj["animation"] as JObject ?? obj;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO animation_bindings(animation_name, animation_source, animation_path_id, binding_count, has_muscle_clip, source_file, raw_json)
VALUES ($animationName, $animationSource, $animationPathId, $bindingCount, $hasMuscleClip, $sourceFile, $rawJson);
SELECT last_insert_rowid();";
            var p = AddParameters(command, "$animationName", "$animationSource", "$animationPathId", "$bindingCount", "$hasMuscleClip", "$sourceFile", "$rawJson");
            Set(p, "$animationName", S(animation, "name") ?? S(obj, "animationName"));
            Set(p, "$animationSource", S(animation, "source") ?? S(obj, "source"));
            Set(p, "$animationPathId", I(animation, "pathId") ?? I(obj, "pathId"));
            Set(p, "$bindingCount", I(obj, "bindingCount") ?? (obj["bindings"] is JArray bindings ? bindings.Count : null));
            Set(p, "$hasMuscleClip", B(obj, "hasMuscleClip"));
            Set(p, "$sourceFile", sourceFile);
            Set(p, "$rawJson", obj.ToString(Formatting.None));
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private static long InsertAnimationBindingPaths(SqliteConnection connection, SqliteTransaction transaction, long bindingId, JObject obj)
        {
            var paths = EnumerateAnimationBindingPaths(obj)
                .Select(x => new
                {
                    Path = NormalizeUnityBindingPath(x.Path),
                    x.Type,
                    x.RawJson,
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Path))
                .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToArray();
            if (paths.Length == 0)
            {
                return 0;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO animation_binding_paths(animation_binding_id, binding_path, binding_type, raw_json)
VALUES ($animationBindingId, $bindingPath, $bindingType, $rawJson);";
            var p = AddParameters(command, "$animationBindingId", "$bindingPath", "$bindingType", "$rawJson");
            long count = 0;
            foreach (var path in paths)
            {
                Set(p, "$animationBindingId", bindingId);
                Set(p, "$bindingPath", path.Path);
                Set(p, "$bindingType", path.Type);
                Set(p, "$rawJson", path.RawJson?.ToString(Formatting.None));
                command.ExecuteNonQuery();
                count++;
            }
            return count;
        }

        private static long BuildModelAnimationCandidateSummaries(SqliteConnection connection)
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, "DELETE FROM model_animation_candidate_model_summary;", transaction);
            Execute(connection, "DELETE FROM model_animation_candidate_animation_summary;", transaction);

            Execute(connection, @"
INSERT INTO model_animation_candidate_model_summary(
    model_output,
    explicit_count,
    direct_preview_count,
    internal_humanoid_count,
    legacy_unity_bake_count
)
SELECT model_output,
       COUNT(*) AS explicit_count,
       SUM(CASE
           WHEN COALESCE(json_extract(raw_json, '$.nextAction'), '') = 'generate_preview_gltf'
            AND COALESCE(json_extract(raw_json, '$.requiresInternalHumanoidSolve'), 0) = 0
            AND COALESCE(json_extract(raw_json, '$.missingInternalHumanoidSolver'), 0) = 0
           THEN 1 ELSE 0 END) AS direct_preview_count,
       SUM(CASE
           WHEN COALESCE(json_extract(raw_json, '$.nextAction'), '') = 'generate_preview_gltf'
            AND COALESCE(json_extract(raw_json, '$.requiresInternalHumanoidSolve'), 0) = 1
           THEN 1
           WHEN COALESCE(json_extract(raw_json, '$.nextAction'), '') = 'generate_unity_baked_gltf'
            AND COALESCE(json_extract(raw_json, '$.requiresUnityBake'), 0) = 1
           THEN 1
           WHEN COALESCE(json_extract(raw_json, '$.missingInternalHumanoidSolver'), 0) = 1
             OR COALESCE(json_extract(raw_json, '$.nextAction'), '') = 'inspect_missing_humanoid_avatar'
           THEN 1 ELSE 0 END) AS internal_humanoid_count,
       SUM(CASE
           WHEN COALESCE(json_extract(raw_json, '$.legacyUnityBakeSupported'), 0) = 1
           THEN 1 ELSE 0 END) AS legacy_unity_bake_count
FROM model_animation_candidates
WHERE relation_source='explicit'
GROUP BY model_output;", transaction);

            Execute(connection, @"
INSERT INTO model_animation_candidate_animation_summary(
    animation_output,
    explicit_model_count,
    direct_preview_model_count,
    internal_humanoid_model_count,
    legacy_unity_bake_model_count
)
SELECT animation_output,
       COUNT(*) AS explicit_model_count,
       SUM(CASE
           WHEN COALESCE(json_extract(raw_json, '$.nextAction'), '') = 'generate_preview_gltf'
            AND COALESCE(json_extract(raw_json, '$.requiresInternalHumanoidSolve'), 0) = 0
            AND COALESCE(json_extract(raw_json, '$.missingInternalHumanoidSolver'), 0) = 0
           THEN 1 ELSE 0 END) AS direct_preview_model_count,
       SUM(CASE
           WHEN COALESCE(json_extract(raw_json, '$.nextAction'), '') = 'generate_preview_gltf'
            AND COALESCE(json_extract(raw_json, '$.requiresInternalHumanoidSolve'), 0) = 1
           THEN 1
           WHEN COALESCE(json_extract(raw_json, '$.nextAction'), '') = 'generate_unity_baked_gltf'
            AND COALESCE(json_extract(raw_json, '$.requiresUnityBake'), 0) = 1
           THEN 1
           WHEN COALESCE(json_extract(raw_json, '$.missingInternalHumanoidSolver'), 0) = 1
             OR COALESCE(json_extract(raw_json, '$.nextAction'), '') = 'inspect_missing_humanoid_avatar'
           THEN 1 ELSE 0 END) AS internal_humanoid_model_count,
       SUM(CASE
           WHEN COALESCE(json_extract(raw_json, '$.legacyUnityBakeSupported'), 0) = 1
           THEN 1 ELSE 0 END) AS legacy_unity_bake_model_count
FROM model_animation_candidates
WHERE relation_source='explicit'
GROUP BY animation_output;", transaction);

            transaction.Commit();
            return ScalarLong(connection, "SELECT COUNT(*) FROM model_animation_candidate_model_summary;");
        }

        private static long BuildUnifiedModelAnimationRelations(SqliteConnection connection)
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, "DELETE FROM relation_animations;", transaction);
            Execute(connection, "DELETE FROM model_animation_relations;", transaction);
            Execute(connection, "DELETE FROM library_reports WHERE report_kind='asset_library_unified_projection';", transaction);

            Execute(connection, @"
INSERT INTO model_animation_relations(
    model_asset_id,
    model_name,
    model_source,
    model_output,
    skeleton_path,
    skeleton_name,
    skeleton_hash,
    relation_kind,
    confidence,
    animation_count,
    raw_json
)
WITH candidate_flags AS (
    SELECT
        c.*,
        CASE
            WHEN COALESCE(c.status, '') IN ('missing_model', 'missing_animation', 'invalid', 'error', 'model_not_animation_ready', 'animation_not_default_candidate') THEN 0
            WHEN COALESCE(json_extract(c.raw_json, '$.modelReadyForAnimation'), 1) = 0 THEN 0
            WHEN COALESCE(json_extract(c.raw_json, '$.defaultAnimationCandidateReady'), 1) = 0 THEN 0
            WHEN COALESCE(json_extract(c.raw_json, '$.explicitCandidateRequiresVisualValidation'), 0) = 1 THEN 0
            ELSE 1
        END AS usable_candidate,
        CASE
            WHEN COALESCE(c.status, '') = 'model_not_animation_ready'
              OR COALESCE(json_extract(c.raw_json, '$.modelReadyForAnimation'), 1) = 0
            THEN 1 ELSE 0
        END AS model_gate_blocked
    FROM model_animation_candidates c
    WHERE c.relation_source='explicit'
)
SELECT
    MIN(m.id) AS model_asset_id,
    COALESCE(MAX(m.name), json_extract(MIN(c.raw_json), '$.model.name'), c.model_output) AS model_name,
    MAX(m.source) AS model_source,
    c.model_output,
    COALESCE(MAX(m.skeleton_path), '') AS skeleton_path,
    COALESCE(MAX(m.skeleton_name), '') AS skeleton_name,
    COALESCE(MAX(m.skeleton_hash), '') AS skeleton_hash,
    'deterministicUsage' AS relation_kind,
    'explicit' AS confidence,
    SUM(c.usable_candidate) AS animation_count,
    json_object(
        'source', 'model_animation_candidates',
        'relationSource', 'explicit',
        'animationCount', SUM(c.usable_candidate),
        'usableAnimationCount', SUM(c.usable_candidate),
        'totalCandidateCount', COUNT(*),
        'modelGateBlockedCount', SUM(c.model_gate_blocked),
        'animationContextBlockedCount', SUM(CASE WHEN COALESCE(json_extract(c.raw_json, '$.defaultAnimationCandidateReady'), 1) = 0 THEN 1 ELSE 0 END),
        'visualValidationBlockedCount', SUM(CASE WHEN COALESCE(json_extract(c.raw_json, '$.explicitCandidateRequiresVisualValidation'), 0) = 1 THEN 1 ELSE 0 END),
        'rule', 'animation_count only counts candidates that passed the model-first, animation-context, and visual-validation gates; blocked explicit Unity relations stay in relation_animations as diagnostics'
    ) AS raw_json
FROM candidate_flags c
LEFT JOIN assets m ON m.kind='Model' AND m.output=c.model_output
GROUP BY c.model_output;", transaction);

            Execute(connection, @"
INSERT INTO relation_animations(
    relation_id,
    animation_asset_id,
    name,
    source,
    output,
    status,
    relation_source,
    usage_evidence,
    is_explicit_usage,
    is_skeleton_compatible,
    validation_status,
    duration,
    frame_count,
    track_count,
    track_coverage,
    hierarchy_compatible,
    is_container_animation,
    is_usable_candidate,
    confidence_tier,
    relationship_kind,
    recommended_use,
    evidence_summary,
    is_deterministic_usage,
    is_compatibility_candidate,
    raw_json
)
SELECT
    r.id AS relation_id,
    a.id AS animation_asset_id,
    COALESCE(a.name, json_extract(c.raw_json, '$.animation.name'), json_extract(c.raw_json, '$.animationName'), c.animation_output) AS name,
    a.source AS source,
    c.animation_output AS output,
    c.status AS status,
    c.relation_source AS relation_source,
    'explicitUnityRelation' AS usage_evidence,
    1 AS is_explicit_usage,
    1 AS is_skeleton_compatible,
    COALESCE(json_extract(c.raw_json, '$.validationStatus'), '') AS validation_status,
    CAST(COALESCE(json_extract(a.raw_json, '$.duration'), json_extract(a.raw_json, '$.length'), json_extract(c.raw_json, '$.duration'), 0) AS REAL) AS duration,
    CAST(COALESCE(json_extract(a.raw_json, '$.frameCount'), json_extract(c.raw_json, '$.frameCount'), 0) AS INTEGER) AS frame_count,
    CAST(COALESCE(json_extract(a.raw_json, '$.trackCount'), json_extract(a.raw_json, '$.curveCount'), json_extract(c.raw_json, '$.trackCount'), 0) AS INTEGER) AS track_count,
    0 AS track_coverage,
    0 AS hierarchy_compatible,
    0 AS is_container_animation,
    CASE
        WHEN COALESCE(c.status, '') IN ('missing_model', 'missing_animation', 'invalid', 'error', 'model_not_animation_ready', 'animation_not_default_candidate') THEN 0
        WHEN COALESCE(json_extract(c.raw_json, '$.modelReadyForAnimation'), 1) = 0 THEN 0
        WHEN COALESCE(json_extract(c.raw_json, '$.defaultAnimationCandidateReady'), 1) = 0 THEN 0
        WHEN COALESCE(json_extract(c.raw_json, '$.explicitCandidateRequiresVisualValidation'), 0) = 1 THEN 0
        ELSE 1
    END AS is_usable_candidate,
    'ExplicitUnity' AS confidence_tier,
    'deterministicUsage' AS relationship_kind,
    CASE
        WHEN COALESCE(c.status, '') = 'model_not_animation_ready'
          OR COALESCE(json_extract(c.raw_json, '$.modelReadyForAnimation'), 1) = 0
        THEN 'fixModelFirst'
        WHEN COALESCE(c.status, '') = 'animation_not_default_candidate'
          OR COALESCE(json_extract(c.raw_json, '$.defaultAnimationCandidateReady'), 1) = 0
        THEN 'reviewAnimationContext'
        WHEN COALESCE(json_extract(c.raw_json, '$.explicitCandidateRequiresVisualValidation'), 0) = 1
        THEN 'visualValidationRequired'
        ELSE 'defaultTrusted'
    END AS recommended_use,
    CASE
        WHEN COALESCE(c.status, '') = 'model_not_animation_ready'
          OR COALESCE(json_extract(c.raw_json, '$.modelReadyForAnimation'), 1) = 0
        THEN 'explicit Unity relation, but model gate blocked animation stage'
        WHEN COALESCE(c.status, '') = 'animation_not_default_candidate'
          OR COALESCE(json_extract(c.raw_json, '$.defaultAnimationCandidateReady'), 1) = 0
        THEN 'explicit Unity relation, but animation context/semantics are diagnostic-only'
        WHEN COALESCE(json_extract(c.raw_json, '$.explicitCandidateRequiresVisualValidation'), 0) = 1
        THEN 'explicit Unity bridge relation; preview/smoke requires clear visual validation before it becomes usable'
        ELSE 'model_animation_candidates.explicit'
    END AS evidence_summary,
    1 AS is_deterministic_usage,
    0 AS is_compatibility_candidate,
    c.raw_json AS raw_json
FROM model_animation_candidates c
JOIN model_animation_relations r ON r.model_output=c.model_output
LEFT JOIN assets a ON a.kind='Animation' AND a.output=c.animation_output
WHERE c.relation_source='explicit';", transaction);

            var report = new JObject
            {
                ["modelsWithAnimations"] = ScalarLong(connection, "SELECT COUNT(*) FROM model_animation_relations;", transaction),
                ["modelsWithUsableAnimations"] = ScalarLong(connection, "SELECT COUNT(*) FROM model_animation_relations WHERE animation_count > 0;", transaction),
                ["relationAnimations"] = ScalarLong(connection, "SELECT COUNT(*) FROM relation_animations;", transaction),
                ["usableRelationAnimations"] = ScalarLong(connection, "SELECT COUNT(*) FROM relation_animations WHERE is_usable_candidate=1;", transaction),
                ["rule"] = "Unified AssetLibrary v1 projection imports only explicit Unity model-animation candidates; fallback/name/structural matches remain diagnostics."
            };
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO library_reports(report_kind, status, created_utc, summary_json)
VALUES ('asset_library_unified_projection', 'ok', $createdUtc, $summaryJson);";
                command.Parameters.AddWithValue("$createdUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$summaryJson", report.ToString(Formatting.None));
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            return ScalarLong(connection, "SELECT COUNT(*) FROM model_animation_relations;");
        }

        private static JObject BuildAssetLibraryCapabilities(SqliteConnection connection)
        {
            var modelAssets = ScalarLong(connection, "SELECT COUNT(*) FROM assets WHERE kind='Model';");
            var animationAssets = ScalarLong(connection, "SELECT COUNT(*) FROM assets WHERE kind='Animation';");
            var usableRelationAnimations = ScalarLong(connection, "SELECT COUNT(*) FROM relation_animations WHERE is_usable_candidate=1;");
            var capabilities = new JObject
            {
                ["models"] = modelAssets > 0,
                ["animations"] = animationAssets > 0 && usableRelationAnimations > 0,
                // AssetLibrary v1 要求这个字段始终存在。
                // 没有可用动画时明确写 null，避免浏览器或验收脚本靠缺字段猜语义。
                ["animationPreviewComposer"] = null,
            };
            if (capabilities.Value<bool>("animations"))
            {
                capabilities["animationPreviewComposer"] = "AnimeStudio.CLI compose-preview";
            }
            return capabilities;
        }

        private static void WriteCapabilityMetadata(SqliteConnection connection, JObject capabilities)
        {
            using var transaction = connection.BeginTransaction();
            InsertMetadata(connection, transaction, "capabilities", capabilities.ToString(Formatting.None));
            transaction.Commit();
        }

        private static void WriteUnifiedAssetLibraryManifest(string root, string sourceGame, JObject capabilities)
        {
            var manifest = new JObject
            {
                ["schemaVersion"] = 1,
                ["libraryKind"] = "AssetLibrary",
                ["libraryName"] = new DirectoryInfo(root).Name,
                ["sourceTool"] = "AnimeStudio",
                ["sourceGame"] = sourceGame ?? string.Empty,
                ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["index"] = "library_index.db",
                ["capabilities"] = capabilities != null ? (JObject)capabilities.DeepClone() : new JObject()
            };
            File.WriteAllText(Path.Combine(root, "asset_library.json"), manifest.ToString(Formatting.Indented));
        }

        private static bool ShouldSkipSQLiteArtifact(string root, string fullPath, string fullDbPath)
        {
            if (string.Equals(fullPath, fullDbPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullPath, fullDbPath + "-wal", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullPath, fullDbPath + "-shm", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var relative = MakeRelative(root, fullPath).Replace('\\', '/');
            var fileName = Path.GetFileName(fullPath);
            var extension = Path.GetExtension(fileName);
            if (!relative.Contains('/', StringComparison.Ordinal)
                && fileName.StartsWith("library_index", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(extension, ".db", StringComparison.OrdinalIgnoreCase)
                    || fileName.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase)
                    || fileName.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase)
                    || fileName.Contains(".db.", StringComparison.OrdinalIgnoreCase)))
            {
                // 根目录常保留旁路、备份或中断的 SQLite 索引。它们是索引构建产物，
                // 不应作为普通素材文件进入 files 表，否则全量重建会扫描很多十几 GB 的 DB。
                return true;
            }

            return false;
        }

        private static IEnumerable<(string Path, string Type, JToken RawJson)> EnumerateAnimationBindingPaths(JObject obj)
        {
            var animation = obj["animation"] as JObject ?? obj;
            if (animation["transformBindingPaths"] is JArray transformPaths)
            {
                foreach (var path in transformPaths.OfType<JValue>())
                {
                    yield return (path.Value?.ToString(), "Transform", path);
                }
            }

            if (obj["bindings"] is JArray bindings)
            {
                foreach (var binding in bindings.OfType<JObject>())
                {
                    yield return (S(binding, "path"), S(binding, "type"), binding);
                }
            }
        }

        private static string NormalizeUnityBindingPath(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/').Trim('/');
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

        private static void WriteSummary(SqliteConnection connection, string root, string dbPath, Dictionary<string, long> counts, bool skipFileIndex, bool skipSidecarScan, string sourceIndexPath)
        {
            var summaryPath = GetSQLiteSummaryPath(root, dbPath);
            var animationRelationCoverage = BuildAnimationRelationCoverageSummarySafely(connection, root, skipSidecarScan, sourceIndexPath);
            var summary = new JObject
            {
                ["schemaVersion"] = 1,
                ["database"] = dbPath,
                ["root"] = root,
                ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["rule"] = "索引要全，导出要精。SQLite v1 面向已导出的 Library/AudioLibrary 文件；后续会扩展到原始 Unity CAB/Object/PPtr 全量索引。",
                ["fileIndexSkipped"] = skipFileIndex,
                ["sidecarScanSkipped"] = skipSidecarScan,
                ["fileIndexNote"] = skipFileIndex
                    ? "本次重建跳过 files 表，用于快速刷新模型-动画关系、sidecar 能力和源索引健康检查。需要完整文件浏览/审计时重新运行不带 --skip_sqlite_file_index 的 build。"
                    : "本次包含 files 表，适合完整文件浏览和审计；大型素材库会明显增加重建时间。",
                ["counts"] = JObject.FromObject(counts),
                ["animationRelationCoverage"] = animationRelationCoverage
            };
            File.WriteAllText(summaryPath, summary.ToString(Formatting.Indented));

            var readme = UsesDefaultSQLiteIndexPath(root, dbPath)
                ? Path.Combine(root, "SQLITE_INDEX_README.md")
                : Path.Combine(Path.GetDirectoryName(summaryPath) ?? root, Path.GetFileNameWithoutExtension(dbPath) + ".README.md");
            File.WriteAllText(readme,
                "# SQLite Library Index\n\n" +
                "这是 AnimeStudio 的素材库索引数据库。当前 v1 从已经导出的 Library/AudioLibrary 文件构建，目标是让后续浏览、筛选、调试、二次打包不用反复扫描大量 JSONL。\n\n" +
                "核心规则：索引要全，导出要精。进入索引不代表默认导出或推荐使用；导出仍按 Unity 显式关系、结构兼容和严格匹配规则执行。\n\n" +
                "桌面工具默认优先读取 SQLite。高频查询，例如模型列表、动画 binding path 定向匹配、缩略图状态、筛选统计，应尽量走 SQLite 索引；JSON/JSONL 保留给人工排查、兼容旧流程和重新建库。\n\n" +
                "主要表：`assets`、`unity_assets`、`unity_relations`、`animation_bindings`、`animation_binding_paths`、`export_manifest`、`json_documents`、`files`。每条结构化记录都尽量保留 `raw_json`，方便后续无损迁移。\n\n" +
                "`assets` 会把常用筛选字段展开成显式列。永劫 ActorBodyVisualCell 自定义网格的 `library_role`、`diagnostic_only`、`character_assembly_role`、`character_assembly_family`、`material_status`、`source_skin_mapping_status`、`selected_visual_cell_transform_node_status` 和 `external_skeleton_context_*` 只服务浏览、筛选和诊断，不等于 glTF skin/joint 或正式角色装配已经可用。\n\n" +
                "音频说明：当前 AudioLibrary 可以导出原始 `.fsb` 等文件；FMOD/native 转 WAV 作为后续批量转换阶段，不阻塞索引与素材库建设。\n");
        }

        private static JObject BuildAnimationRelationCoverageSummarySafely(SqliteConnection connection, string root, bool skipSidecarScan, string sourceIndexPath)
        {
            try
            {
                return BuildAnimationRelationCoverageSummary(connection, root, skipSidecarScan, sourceIndexPath);
            }
            catch (Exception e)
            {
                Logger.Warning($"SQLite summary animation relation coverage failed: {e.GetType().Name}: {e.Message}");
                return new JObject
                {
                    ["status"] = "error",
                    ["error"] = e.GetType().Name + ": " + e.Message,
                    ["note"] = "SQLite DB 已完成写入；仅动画关系覆盖摘要统计失败。可稍后单独诊断 summary 查询，不应阻塞素材库生产索引。"
                };
            }
        }

        private static string GetSQLiteSummaryPath(string root, string dbPath)
        {
            if (UsesDefaultSQLiteIndexPath(root, dbPath))
            {
                return Path.Combine(root, "sqlite_index_summary.json");
            }

            var directory = Path.GetDirectoryName(dbPath) ?? root;
            var name = Path.GetFileNameWithoutExtension(dbPath);
            return Path.Combine(directory, name + ".summary.json");
        }

        private static bool UsesDefaultSQLiteIndexPath(string root, string dbPath)
        {
            var defaultPath = Path.GetFullPath(Path.Combine(root, "library_index.db"));
            return string.Equals(Path.GetFullPath(dbPath), defaultPath, StringComparison.OrdinalIgnoreCase);
        }

        private static JObject BuildAnimationRelationCoverageSummary(SqliteConnection connection, string root, bool skipSidecarScan, string sourceIndexPath)
        {
            var totalModels = ScalarLong(connection, "SELECT COUNT(*) FROM assets WHERE kind='Model';");
            var totalAnimations = ScalarLong(connection, "SELECT COUNT(*) FROM assets WHERE kind='Animation';");
            var explicitCandidates = ScalarLong(connection, "SELECT COALESCE(SUM(explicit_count), 0) FROM model_animation_candidate_model_summary;");
            var modelsWithExplicit = ScalarLong(connection, "SELECT COUNT(*) FROM model_animation_candidate_model_summary WHERE explicit_count > 0;");
            var animationsWithExplicit = ScalarLong(connection, "SELECT COUNT(*) FROM model_animation_candidate_animation_summary WHERE explicit_model_count > 0;");
            var relationSources = QueryGroupedCounts(connection, @"
SELECT COALESCE(relation_source, '(null)') AS key, COUNT(*) AS count
FROM model_animation_candidates
GROUP BY COALESCE(relation_source, '(null)')
ORDER BY count DESC;");
            var confidence = QueryGroupedCounts(connection, @"
SELECT COALESCE(confidence, '(null)') AS key, COUNT(*) AS count
FROM model_animation_candidates
GROUP BY COALESCE(confidence, '(null)')
ORDER BY count DESC
LIMIT 16;");
            var candidateNextActions = QueryGroupedCounts(connection, @"
SELECT COALESCE(json_extract(raw_json, '$.nextAction'), '(null)') AS key, COUNT(*) AS count
FROM model_animation_candidates
WHERE relation_source='explicit'
GROUP BY COALESCE(json_extract(raw_json, '$.nextAction'), '(null)')
ORDER BY count DESC
LIMIT 16;");
            var missingInternalHumanoidSolverReasons = QueryGroupedCounts(connection, @"
SELECT COALESCE(json_extract(raw_json, '$.missingInternalHumanoidSolverReason'), '(null)') AS key, COUNT(*) AS count
FROM model_animation_candidates
WHERE relation_source='explicit'
  AND COALESCE(json_extract(raw_json, '$.missingInternalHumanoidSolver'), 0)=1
GROUP BY COALESCE(json_extract(raw_json, '$.missingInternalHumanoidSolverReason'), '(null)')
ORDER BY count DESC
LIMIT 16;");
            var missingInternalHumanoidSolverModelReasons = QueryGroupedCounts(connection, @"
SELECT reason AS key, COUNT(*) AS count
FROM (
    SELECT c.model_output,
           COALESCE(json_extract(c.raw_json, '$.missingInternalHumanoidSolverReason'), '(null)') AS reason
    FROM model_animation_candidates c
    WHERE c.relation_source='explicit'
      AND COALESCE(json_extract(c.raw_json, '$.missingInternalHumanoidSolver'), 0)=1
    GROUP BY c.model_output, reason
)
GROUP BY reason
ORDER BY count DESC
LIMIT 16;");
            var candidateProcessingFlags = QueryGroupedCounts(connection, @"
SELECT 'requiresInternalHumanoidSolve' AS key, COALESCE(SUM(internal_humanoid_count), 0) AS count
FROM model_animation_candidate_model_summary
UNION ALL
SELECT 'directPreviewCandidate' AS key, COALESCE(SUM(direct_preview_count), 0) AS count
FROM model_animation_candidate_model_summary
UNION ALL
SELECT 'internalHumanoidPreviewCandidate' AS key, COALESCE(SUM(internal_humanoid_count), 0) AS count
FROM model_animation_candidate_model_summary
UNION ALL
SELECT 'legacyUnityBakeSupportedDiagnostic' AS key, COALESCE(SUM(legacy_unity_bake_count), 0) AS count
FROM model_animation_candidate_model_summary;");
            var linkedAnimationTypes = QueryGroupedCounts(connection, @"
SELECT COALESCE(animation_type, json_extract(raw_json, '$.animationType'), '(null)') AS key, COUNT(*) AS count
FROM model_animation_candidate_animation_summary s
JOIN assets a ON a.output = s.animation_output AND a.kind = 'Animation'
WHERE s.explicit_model_count > 0
GROUP BY COALESCE(animation_type, json_extract(raw_json, '$.animationType'), '(null)')
ORDER BY count DESC
LIMIT 32;");
            var linkedAnimationSignals = QueryGroupedCounts(connection, @"
SELECT 'hasMuscleClip' AS key, COUNT(*) AS count
FROM (
    SELECT a.output, a.raw_json
    FROM model_animation_candidate_animation_summary s
    JOIN assets a ON a.output = s.animation_output AND a.kind = 'Animation'
    WHERE s.explicit_model_count > 0
)
WHERE json_extract(raw_json, '$.hasMuscleClip') = 1
UNION ALL
SELECT 'hasTransformBindings' AS key, COUNT(*) AS count
FROM (
    SELECT a.output, a.raw_json
    FROM model_animation_candidate_animation_summary s
    JOIN assets a ON a.output = s.animation_output AND a.kind = 'Animation'
    WHERE s.explicit_model_count > 0
)
WHERE COALESCE(json_extract(raw_json, '$.transformBindingCount'), 0) > 0
UNION ALL
SELECT 'hasCoreTransformBindings' AS key, COUNT(*) AS count
FROM (
    SELECT a.output, a.raw_json
    FROM model_animation_candidate_animation_summary s
    JOIN assets a ON a.output = s.animation_output AND a.kind = 'Animation'
    WHERE s.explicit_model_count > 0
)
WHERE COALESCE(json_extract(raw_json, '$.coreTransformBindingCount'), 0) > 0
UNION ALL
SELECT 'hasBlendShapeBindings' AS key, COUNT(*) AS count
FROM (
    SELECT a.output, a.raw_json
    FROM model_animation_candidate_animation_summary s
    JOIN assets a ON a.output = s.animation_output AND a.kind = 'Animation'
    WHERE s.explicit_model_count > 0
)
WHERE COALESCE(json_extract(raw_json, '$.trueBlendShapeBindingCount'), 0) > 0
UNION ALL
SELECT 'hasAmbiguousSkinnedMeshRendererFloatBindings' AS key, COUNT(*) AS count
FROM (
    SELECT a.output, a.raw_json
    FROM model_animation_candidate_animation_summary s
    JOIN assets a ON a.output = s.animation_output AND a.kind = 'Animation'
    WHERE s.explicit_model_count > 0
)
WHERE COALESCE(json_extract(raw_json, '$.blendShapeBindingCount'), 0) > 0
UNION ALL
SELECT 'hasRendererMaterialBindings' AS key, COUNT(*) AS count
FROM (
    SELECT a.output, a.raw_json
    FROM model_animation_candidate_animation_summary s
    JOIN assets a ON a.output = s.animation_output AND a.kind = 'Animation'
    WHERE s.explicit_model_count > 0
)
WHERE COALESCE(json_extract(raw_json, '$.rendererMaterialBindingCount'), 0) > 0
UNION ALL
SELECT 'hasRendererPropertyBindings' AS key, COUNT(*) AS count
FROM (
    SELECT a.output, a.raw_json
    FROM model_animation_candidate_animation_summary s
    JOIN assets a ON a.output = s.animation_output AND a.kind = 'Animation'
    WHERE s.explicit_model_count > 0
)
WHERE COALESCE(json_extract(raw_json, '$.rendererPropertyBindingCount'), 0) > 0
UNION ALL
SELECT 'hasActiveStateBindings' AS key, COUNT(*) AS count
FROM (
    SELECT a.output, a.raw_json
    FROM model_animation_candidate_animation_summary s
    JOIN assets a ON a.output = s.animation_output AND a.kind = 'Animation'
    WHERE s.explicit_model_count > 0
)
WHERE COALESCE(json_extract(raw_json, '$.activeStateBindingCount'), 0) > 0
UNION ALL
SELECT 'hasAuxiliaryBindings' AS key, COUNT(*) AS count
FROM (
    SELECT a.output, a.raw_json
    FROM model_animation_candidate_animation_summary s
    JOIN assets a ON a.output = s.animation_output AND a.kind = 'Animation'
    WHERE s.explicit_model_count > 0
)
WHERE COALESCE(json_extract(raw_json, '$.auxiliaryBindingCount'), 0) > 0;");
            var resourceKinds = QueryResourceKindCoverage(connection);
            var perModel = QueryLongList(connection, @"
SELECT explicit_count
FROM model_animation_candidate_model_summary
WHERE explicit_count > 0
ORDER BY explicit_count;");
            var sidecarCapabilities = skipSidecarScan
                ? new JObject
                {
                    ["status"] = "skipped",
                    ["note"] = "本次重建使用 --skip_sqlite_sidecar_scan，未逐个读取动画 sidecar。候选关系和源索引健康检查仍然有效；需要 decoded/directGltfReady 覆盖统计时重新运行不带该参数的 build。"
                }
                : QueryLinkedAnimationSidecarCapabilities(connection, root);
            var avatarProductionGate = BuildAvatarProductionGateSummary(connection);
            var unityBakeProduction = BuildUnityBakeProductionSummary(connection);

            return new JObject
            {
                ["rule"] = "只统计 SQLite 中已经建立的模型-动画关系。默认推荐候选必须来自 relation_source=explicit；结构兼容、骨骼数量、名称和路径只能作为诊断或显式 fallback。",
                ["modelCoverageNote"] = "allModelsIncludingStatic 会把大量静态模型、场景件和暂不处理的 VFX/特效网格放进分母，不能单独当作动画关系失败率。更可靠的验收应结合 resourceKind、显式候选、sidecar decoded 能力和 glTF 预览验证。",
                ["totals"] = new JObject
                {
                    ["models"] = totalModels,
                    ["animations"] = totalAnimations,
                    ["explicitCandidates"] = explicitCandidates,
                    ["modelsWithExplicitCandidates"] = modelsWithExplicit,
                    ["animationsWithExplicitCandidates"] = animationsWithExplicit,
                    ["allModelExplicitCoverage"] = Ratio(modelsWithExplicit, totalModels),
                    ["animationExplicitCoverage"] = Ratio(animationsWithExplicit, totalAnimations)
                },
                ["relationSources"] = relationSources,
                ["confidence"] = confidence,
                ["candidateNextActions"] = candidateNextActions,
                ["missingInternalHumanoidSolverReasons"] = missingInternalHumanoidSolverReasons,
                ["missingInternalHumanoidSolverModelReasons"] = missingInternalHumanoidSolverModelReasons,
                ["candidateProcessingFlags"] = candidateProcessingFlags,
                ["exportedAnimatorControllerDiagnostics"] = BuildExportedAnimatorControllerDiagnostics(connection, sourceIndexPath),
                ["sourceIndexAnimationRelationHealth"] = BuildSourceIndexAnimationRelationHealth(root, sourceIndexPath),
                ["avatarProductionGate"] = avatarProductionGate,
                ["unityBakeProduction"] = unityBakeProduction,
                ["linkedAnimationSidecarCapabilities"] = sidecarCapabilities,
                ["linkedAnimationTypes"] = linkedAnimationTypes,
                ["linkedAnimationSignals"] = linkedAnimationSignals,
                ["modelResourceKinds"] = resourceKinds,
                ["explicitCandidatesPerLinkedModel"] = BuildDistribution(perModel)
            };
        }

        private static JObject BuildExportedAnimatorControllerDiagnostics(SqliteConnection libraryConnection, string sourceIndexPath)
        {
            var result = new JObject
            {
                ["rule"] = "只诊断已导出 Animator 模型自身是否有 Unity 显式 animator.controller。controller 为空时不能用同名 AC_* 或动作名前缀补默认模型-动画关系；这些只能进入人工诊断或显式预览。",
                ["note"] = "这个摘要不生成候选，只解释为什么 model_animation_candidates 可能为 0。"
            };

            var models = new List<ExportedAnimatorModel>();
            using (var command = libraryConnection.CreateCommand())
            {
                command.CommandTimeout = SummaryQueryTimeoutSeconds;
                command.CommandText = @"
SELECT name, source, path_id, output, resource_kind, container
FROM assets
WHERE kind='Model'
  AND source_type='Animator'
ORDER BY name;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    models.Add(new ExportedAnimatorModel(
                        reader.IsDBNull(0) ? "" : reader.GetString(0),
                        reader.IsDBNull(1) ? "" : reader.GetString(1),
                        reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                        reader.IsDBNull(3) ? "" : reader.GetString(3),
                        reader.IsDBNull(4) ? "" : reader.GetString(4),
                        reader.IsDBNull(5) ? "" : reader.GetString(5)));
                }
            }

            result["exportedAnimatorModels"] = models.Count;
            if (models.Count == 0)
            {
                result["status"] = "no_exported_animator_models";
                return result;
            }

            if (string.IsNullOrWhiteSpace(sourceIndexPath) || !File.Exists(sourceIndexPath))
            {
                result["status"] = "source_index_unavailable";
                result["sourceIndex"] = sourceIndexPath;
                return result;
            }

            long resolved = 0;
            long nullController = 0;
            long explicitController = 0;
            long missingOrAmbiguous = 0;
            var samples = new JArray();

            using var sourceConnection = new SqliteConnection($"Data Source={sourceIndexPath};Mode=ReadOnly");
            sourceConnection.Open();
            foreach (var model in models)
            {
                var rows = QuerySourceAnimatorRows(sourceConnection, model).ToArray();
                if (rows.Length == 0)
                {
                    missingOrAmbiguous++;
                    AddAnimatorControllerDiagnosticSample(samples, model, "source_animator_not_found", null, null, null);
                    continue;
                }

                resolved++;
                var hasExplicitController = rows.Any(x => x.ControllerIsNull == false);
                var allNullController = rows.All(x => x.ControllerIsNull == true);
                if (hasExplicitController)
                {
                    explicitController++;
                    AddAnimatorControllerDiagnosticSample(samples, model, "has_explicit_controller", rows[0], false, rows.Length);
                }
                else if (allNullController)
                {
                    nullController++;
                    AddAnimatorControllerDiagnosticSample(samples, model, "animator_controller_null", rows[0], true, rows.Length);
                }
                else
                {
                    missingOrAmbiguous++;
                    AddAnimatorControllerDiagnosticSample(samples, model, "animator_controller_unreadable", rows[0], null, rows.Length);
                }
            }

            result["status"] = "ok";
            result["sourceIndex"] = sourceIndexPath;
            result["resolvedSourceAnimatorModels"] = resolved;
            result["modelsWithNullAnimatorController"] = nullController;
            result["modelsWithExplicitAnimatorController"] = explicitController;
            result["missingOrAmbiguousSourceAnimatorModels"] = missingOrAmbiguous;
            result["samples"] = samples;
            return result;
        }

        private static IEnumerable<SourceAnimatorDiagnosticRow> QuerySourceAnimatorRows(SqliteConnection sourceConnection, ExportedAnimatorModel model)
        {
            using var command = sourceConnection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = @"
SELECT source_path, serialized_file, path_id, name, raw_json
FROM source_objects
WHERE type='Animator'
  AND path_id=$pathId
  AND name=$name
LIMIT 8;";
            command.Parameters.AddWithValue("$pathId", model.PathId);
            command.Parameters.AddWithValue("$name", model.Name ?? string.Empty);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var rawText = reader.IsDBNull(4) ? null : reader.GetString(4);
                JObject raw = null;
                try
                {
                    raw = string.IsNullOrWhiteSpace(rawText) ? null : JObject.Parse(rawText);
                }
                catch (JsonException)
                {
                    raw = null;
                }

                yield return new SourceAnimatorDiagnosticRow(
                    reader.IsDBNull(0) ? "" : reader.GetString(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                    reader.IsDBNull(3) ? "" : reader.GetString(3),
                    ReadOptionalBool(raw?.SelectToken("animator.controller.isNull")),
                    ReadOptionalBool(raw?.SelectToken("animator.avatar.isNull")));
            }
        }

        private static void AddAnimatorControllerDiagnosticSample(
            JArray samples,
            ExportedAnimatorModel model,
            string status,
            SourceAnimatorDiagnosticRow row,
            bool? controllerIsNull,
            int? matchingSourceRows)
        {
            if (samples.Count >= 16)
            {
                return;
            }

            samples.Add(new JObject
            {
                ["name"] = model.Name,
                ["resourceKind"] = model.ResourceKind,
                ["container"] = model.Container,
                ["output"] = model.Output,
                ["source"] = model.Source,
                ["pathId"] = model.PathId,
                ["status"] = status,
                ["matchingSourceRows"] = matchingSourceRows,
                ["sourceIndexSourcePath"] = row?.SourcePath,
                ["sourceIndexSerializedFile"] = row?.SerializedFile,
                ["animatorControllerIsNull"] = controllerIsNull,
                ["animatorAvatarIsNull"] = row?.AvatarIsNull
            });
        }

        private static bool? ReadOptionalBool(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            if (token.Type == JTokenType.Boolean)
            {
                return token.Value<bool>();
            }

            if (token.Type == JTokenType.Integer)
            {
                return token.Value<int>() != 0;
            }

            return bool.TryParse(token.ToString(), out var value) ? value : null;
        }

        private sealed record ExportedAnimatorModel(string Name, string Source, long PathId, string Output, string ResourceKind, string Container);

        private sealed record SourceAnimatorDiagnosticRow(string SourcePath, string SerializedFile, long PathId, string Name, bool? ControllerIsNull, bool? AvatarIsNull);

        private static JObject BuildAvatarProductionGateSummary(SqliteConnection connection)
        {
            var modelAvatarSql = @"
SELECT COUNT(*) AS avatarModels,
       SUM(CASE WHEN COALESCE(json_array_length(json_extract(raw_json, '$.avatar.humanBones')), 0) > 0
                 AND COALESCE(json_array_length(json_extract(raw_json, '$.avatar.skeletonBones')), 0) > 0
                THEN 1 ELSE 0 END) AS productionAvatarModels,
       SUM(CASE WHEN COALESCE(json_extract(raw_json, '$.avatar.hasHumanDescription'), 0)=1 THEN 1 ELSE 0 END) AS humanDescriptionFlagModels,
       SUM(CASE WHEN COALESCE(json_extract(raw_json, '$.avatar.hasHumanDescription'), 0)=1
                 AND (
                   COALESCE(json_array_length(json_extract(raw_json, '$.avatar.humanBones')), 0)=0
                   OR COALESCE(json_array_length(json_extract(raw_json, '$.avatar.skeletonBones')), 0)=0
                 )
                THEN 1 ELSE 0 END) AS emptyHumanDescriptionModels,
       SUM(CASE WHEN json_extract(raw_json, '$.avatar.oracle') IS NOT NULL
                 OR json_extract(raw_json, '$.avatar.internalSolver') IS NOT NULL
                THEN 1 ELSE 0 END) AS avatarConstantOracleModels
FROM assets
WHERE kind='Model'
  AND json_extract(raw_json, '$.avatar') IS NOT NULL;";
            var avatarModels = 0L;
            var productionAvatarModels = 0L;
            var humanDescriptionFlagModels = 0L;
            var emptyHumanDescriptionModels = 0L;
            var avatarConstantOracleModels = 0L;
            using (var command = connection.CreateCommand())
            {
                command.CommandTimeout = SummaryQueryTimeoutSeconds;
                command.CommandText = modelAvatarSql;
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    avatarModels = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                    productionAvatarModels = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                    humanDescriptionFlagModels = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                    emptyHumanDescriptionModels = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
                    avatarConstantOracleModels = reader.IsDBNull(4) ? 0 : reader.GetInt64(4);
                }
            }

            var explicitHumanoidCandidates = ScalarLong(connection, @"
SELECT COUNT(*)
FROM model_animation_candidates
WHERE relation_source='explicit'
  AND COALESCE(json_extract(raw_json, '$.fullHumanoidBakeRequired'), 0)=1;");
            var explicitHumanoidModels = ScalarLong(connection, @"
SELECT COUNT(DISTINCT model_output)
FROM model_animation_candidates
WHERE relation_source='explicit'
  AND COALESCE(json_extract(raw_json, '$.fullHumanoidBakeRequired'), 0)=1;");
            var blockedCandidates = ScalarLong(connection, @"
SELECT COUNT(*)
FROM model_animation_candidates
WHERE relation_source='explicit'
  AND COALESCE(json_extract(raw_json, '$.fullHumanoidBakeRequired'), 0)=1
  AND COALESCE(json_extract(raw_json, '$.productionUnityBakeBlocked'), 0)=1;");
            var blockedModels = ScalarLong(connection, @"
SELECT COUNT(DISTINCT model_output)
FROM model_animation_candidates
WHERE relation_source='explicit'
  AND COALESCE(json_extract(raw_json, '$.fullHumanoidBakeRequired'), 0)=1
  AND COALESCE(json_extract(raw_json, '$.productionUnityBakeBlocked'), 0)=1;");
            var readyCandidates = ScalarLong(connection, @"
SELECT COUNT(*)
FROM model_animation_candidates
WHERE relation_source='explicit'
  AND COALESCE(json_extract(raw_json, '$.fullHumanoidBakeRequired'), 0)=1
  AND COALESCE(json_extract(raw_json, '$.productionUnityBakeReady'), 0)=1;");
            var readyModels = ScalarLong(connection, @"
SELECT COUNT(DISTINCT model_output)
FROM model_animation_candidates
WHERE relation_source='explicit'
  AND COALESCE(json_extract(raw_json, '$.fullHumanoidBakeRequired'), 0)=1
  AND COALESCE(json_extract(raw_json, '$.productionUnityBakeReady'), 0)=1;");
            var blockedReasons = QueryGroupedCounts(connection, @"
SELECT COALESCE(json_extract(raw_json, '$.productionUnityBakeBlockedReason'), '(null)') AS key,
       COUNT(*) AS count
FROM model_animation_candidates
WHERE relation_source='explicit'
  AND COALESCE(json_extract(raw_json, '$.fullHumanoidBakeRequired'), 0)=1
  AND COALESCE(json_extract(raw_json, '$.productionUnityBakeBlocked'), 0)=1
GROUP BY COALESCE(json_extract(raw_json, '$.productionUnityBakeBlockedReason'), '(null)')
ORDER BY count DESC, key COLLATE NOCASE;");

            return new JObject
            {
                ["rule"] = "生产 Unity bake 只接受真实 Unity prefab/Animator.avatar，或完整 HumanDescription.humanBones + skeletonBones。AvatarConstant/internalSolver/oracle 只作为诊断和后续恢复输入，不能单独计入 production ready。",
                ["avatarModels"] = avatarModels,
                ["productionAvatarModels"] = productionAvatarModels,
                ["humanDescriptionModels"] = humanDescriptionFlagModels,
                ["humanDescriptionFlagModels"] = humanDescriptionFlagModels,
                ["completeHumanDescriptionModels"] = productionAvatarModels,
                ["emptyHumanDescriptionModels"] = emptyHumanDescriptionModels,
                ["avatarConstantOracleModels"] = avatarConstantOracleModels,
                ["productionAvatarModelCoverage"] = Ratio(productionAvatarModels, avatarModels),
                ["productionAvatarModelCoveragePercent"] = Percent(productionAvatarModels, avatarModels),
                ["explicitHumanoidCandidates"] = explicitHumanoidCandidates,
                ["explicitHumanoidModels"] = explicitHumanoidModels,
                ["readyCandidates"] = readyCandidates,
                ["readyModels"] = readyModels,
                ["blockedCandidates"] = blockedCandidates,
                ["blockedModels"] = blockedModels,
                ["readyCandidateCoverage"] = Ratio(readyCandidates, explicitHumanoidCandidates),
                ["readyCandidateCoveragePercent"] = Percent(readyCandidates, explicitHumanoidCandidates),
                ["blockedReasonCounts"] = blockedReasons,
            };
        }

        private static JObject BuildSourceIndexAnimationRelationHealth(string root, string sourceIndexPath = null)
        {
            var sourceIndex = string.IsNullOrWhiteSpace(sourceIndexPath)
                ? Path.Combine(root, "unity_source_index.db")
                : Path.GetFullPath(sourceIndexPath);
            if (!File.Exists(sourceIndex))
            {
                return new JObject
                {
                    ["status"] = "missing",
                    ["sourceIndex"] = sourceIndex,
                    ["note"] = "未找到 unity_source_index.db；SQLite Library 只能使用已导出的旧关系，无法从完整 Unity PPtr 图恢复 Animator/Animation 确定性关系。",
                };
            }

            try
            {
                using var sourceConnection = new SqliteConnection($"Data Source={sourceIndex};Mode=ReadOnly");
                sourceConnection.Open();
                var overrideControllerObjects = SourceScalarLong(sourceConnection, "SELECT COUNT(*) FROM source_objects WHERE type='AnimatorOverrideController';");
                var controllerClips = SourceRelationCount(sourceConnection, "animatorController.clip");
                var controllerClipResolvedTargets = SourceRelationResolvedTargetCount(sourceConnection, "animatorController.clip", "AnimationClip");
                var controllerClipMissingTargets = Math.Max(0, controllerClips - controllerClipResolvedTargets);
                var animatorControllers = SourceRelationCount(sourceConnection, "animator.controller");
                var animationClips = SourceRelationCount(sourceConnection, "animation.clip");
                var overrideBase = SourceRelationCount(sourceConnection, "animatorOverrideController.baseController");
                var overrideSet = SourceRelationCount(sourceConnection, "animatorOverrideController.overrideSet");
                var nonEmptyOverrideSet = SourceRelationTargetCountPositive(sourceConnection, "animatorOverrideController.overrideSet");
                var overrideOriginal = SourceRelationCount(sourceConnection, "animatorOverrideController.originalClip");
                var overrideClip = SourceRelationCount(sourceConnection, "animatorOverrideController.overrideClip");
                var overridePair = SourceRelationCount(sourceConnection, "animatorOverrideController.clipPair");
                var features = SourceScalarString(sourceConnection, "SELECT value FROM metadata WHERE key='animationRelationFeatures' LIMIT 1;");
                var staleOverridePairs = overrideControllerObjects > 0
                    && (overrideSet == 0 || (nonEmptyOverrideSet > 0 && overridePair == 0));
                var missingControllerClipTargets = controllerClipMissingTargets > 0;

                return new JObject
                {
                    ["status"] = staleOverridePairs || missingControllerClipTargets ? "warning" : "ok",
                    ["sourceIndex"] = sourceIndex,
                    ["animationRelationFeatures"] = string.IsNullOrWhiteSpace(features) ? null : features,
                    ["relationCounts"] = new JObject
                    {
                        ["animator.controller"] = animatorControllers,
                        ["animatorController.clip"] = controllerClips,
                        ["animation.clip"] = animationClips,
                        ["animatorOverrideController.baseController"] = overrideBase,
                        ["animatorOverrideController.overrideSet"] = overrideSet,
                        ["animatorOverrideController.originalClip"] = overrideOriginal,
                        ["animatorOverrideController.overrideClip"] = overrideClip,
                        ["animatorOverrideController.clipPair"] = overridePair,
                    },
                    ["resolvedTargetCounts"] = new JObject
                    {
                        ["animatorController.clip"] = controllerClipResolvedTargets,
                    },
                    ["missingTargetCounts"] = new JObject
                    {
                        ["animatorController.clip"] = controllerClipMissingTargets,
                    },
                    ["objectCounts"] = new JObject
                    {
                        ["AnimatorOverrideController"] = overrideControllerObjects,
                    },
                    ["nonEmptyOverrideSetCount"] = nonEmptyOverrideSet,
                    ["staleOverridePairIndex"] = staleOverridePairs,
                    ["missingControllerClipTargets"] = missingControllerClipTargets,
                    ["note"] = staleOverridePairs
                        ? "当前源索引存在 AnimatorOverrideController，但缺少当前工具写入的 animatorOverrideController.overrideSet/clipPair 精确标记。旧索引即使有分离的 originalClip/overrideClip，也不能可靠表达 original -> override 或空替换表；Library 重建会跳过这类不完整 OverrideController 的 base controller 粗扩散。请先用当前工具重建 unity_source_index.db，再重建 library_index.db，以恢复精确 override 动画候选。"
                        : missingControllerClipTargets
                            ? "源索引包含 AnimatorController -> AnimationClip 关系，但部分目标没有解析到真实 AnimationClip。全量候选仍会只使用已解析到的目标；请检查缺失 CAB 或重新构建更完整的源索引。"
                        : "源索引包含当前工具可识别的动画关系；候选质量仍需结合 resourceKind、sidecar decoded 和 glTF 预览验证。",
                    ["recommendedAction"] = staleOverridePairs || missingControllerClipTargets
                        ? "先用完整 Unity 源目录重建 unity_source_index.db，再对素材库运行 --build_sqlite_index。不要用骨骼数量或全量模型×动画匹配补这类缺口。"
                        : "可直接使用该源索引重建 library_index.db 或运行预览验证。",
                };
            }
            catch (Exception e)
            {
                return new JObject
                {
                    ["status"] = "error",
                    ["sourceIndex"] = sourceIndex,
                    ["error"] = e.GetType().Name + ": " + e.Message,
                };
            }
        }

        private static JObject BuildUnityBakeProductionSummary(SqliteConnection connection)
        {
            var explicitUnityBakeCandidates = ScalarLong(connection, @"
SELECT COUNT(*)
FROM model_animation_candidates
WHERE relation_source='explicit'
  AND COALESCE(json_extract(raw_json, '$.fullHumanoidBakeRequired'), 0)=1;");
            var uniqueExplicitUnityBakeCandidates = ScalarLong(connection, @"
SELECT COUNT(*)
FROM (
  SELECT DISTINCT model_output, animation_output
  FROM model_animation_candidates
  WHERE relation_source='explicit'
    AND COALESCE(json_extract(raw_json, '$.fullHumanoidBakeRequired'), 0)=1
);");
            var explicitUnityBakeModels = ScalarLong(connection, @"
SELECT COUNT(DISTINCT model_output)
FROM model_animation_candidates
WHERE relation_source='explicit'
  AND COALESCE(json_extract(raw_json, '$.fullHumanoidBakeRequired'), 0)=1;");
            var explicitUnityBakeAnimations = ScalarLong(connection, @"
SELECT COUNT(DISTINCT animation_output)
FROM model_animation_candidates
WHERE relation_source='explicit'
  AND COALESCE(json_extract(raw_json, '$.fullHumanoidBakeRequired'), 0)=1;");
            var bakeReadyExplicitUnityBakeCandidates = ScalarLong(connection, @"
SELECT COUNT(*)
FROM model_animation_candidates c
JOIN assets m ON m.kind='Model' AND m.output=c.model_output
WHERE c.relation_source='explicit'
  AND COALESCE(json_extract(c.raw_json, '$.fullHumanoidBakeRequired'), 0)=1
  AND " + BakeReadyAvatarSql("m") + @";");
            var uniqueBakeReadyExplicitUnityBakeCandidates = ScalarLong(connection, @"
SELECT COUNT(*)
FROM (
  SELECT DISTINCT c.model_output, c.animation_output
  FROM model_animation_candidates c
  JOIN assets m ON m.kind='Model' AND m.output=c.model_output
  WHERE c.relation_source='explicit'
    AND COALESCE(json_extract(c.raw_json, '$.fullHumanoidBakeRequired'), 0)=1
    AND " + BakeReadyAvatarSql("m") + @"
);");

            if (!HasTable(connection, "animation_bake_cache"))
            {
                return new JObject
                {
                    ["status"] = "no_cache_table",
                    ["rule"] = "Unity bake production only counts relation_source=explicit Humanoid/Muscle candidates. Run --bake_animation_previews_from_library to populate animation_bake_cache.",
                    ["explicitUnityBakeCandidates"] = explicitUnityBakeCandidates,
                    ["uniqueExplicitUnityBakeCandidates"] = uniqueExplicitUnityBakeCandidates,
                    ["explicitUnityBakeModels"] = explicitUnityBakeModels,
                    ["explicitUnityBakeAnimations"] = explicitUnityBakeAnimations,
                    ["bakeReadyExplicitUnityBakeCandidates"] = bakeReadyExplicitUnityBakeCandidates,
                    ["uniqueBakeReadyExplicitUnityBakeCandidates"] = uniqueBakeReadyExplicitUnityBakeCandidates,
                    ["bakeReadyExplicitUnityBakeCoverage"] = Ratio(bakeReadyExplicitUnityBakeCandidates, explicitUnityBakeCandidates),
                    ["uniqueBakeReadyExplicitUnityBakeCoverage"] = Ratio(uniqueBakeReadyExplicitUnityBakeCandidates, uniqueExplicitUnityBakeCandidates),
                    ["bakeReadyExplicitUnityBakeCoveragePercent"] = Percent(bakeReadyExplicitUnityBakeCandidates, explicitUnityBakeCandidates),
                    ["uniqueBakeReadyExplicitUnityBakeCoveragePercent"] = Percent(uniqueBakeReadyExplicitUnityBakeCandidates, uniqueExplicitUnityBakeCandidates),
                    ["cachedCandidates"] = 0,
                    ["requestWrittenCandidates"] = 0,
                    ["bakedCandidates"] = 0,
                    ["trustedBakedCandidates"] = 0,
                    ["bakedMissingGltfCandidates"] = 0,
                    ["failedCandidates"] = 0,
                    ["cacheCoverage"] = 0.0,
                    ["bakedCoverage"] = 0.0,
                    ["trustedBakedCoverage"] = 0.0,
                    ["cacheCoveragePercent"] = 0.0,
                    ["bakedCoveragePercent"] = 0.0,
                    ["trustedBakedCoveragePercent"] = 0.0,
                    ["uniqueCachedCandidates"] = 0,
                    ["uniqueRequestWrittenCandidates"] = 0,
                    ["uniqueBakedCandidates"] = 0,
                    ["uniqueTrustedBakedCandidates"] = 0,
                    ["uniqueBakedMissingGltfCandidates"] = 0,
                    ["uniqueNeedsReviewCandidates"] = 0,
                    ["uniqueFailedCandidates"] = 0,
                    ["duplicateCacheRows"] = 0,
                    ["pendingUnityBakeCandidates"] = bakeReadyExplicitUnityBakeCandidates,
                    ["uniquePendingUnityBakeCandidates"] = uniqueBakeReadyExplicitUnityBakeCandidates,
                    ["uniqueCacheCoverage"] = 0.0,
                    ["uniqueTrustedBakedCoverage"] = 0.0,
                    ["uniqueCacheCoveragePercent"] = 0.0,
                    ["uniqueTrustedBakedCoveragePercent"] = 0.0,
                };
            }

            var bakeReadyCacheWhere = BuildBakeReadyCacheWhere("bc");
            var cachedCandidates = ScalarLong(connection, $@"
SELECT COUNT(*)
FROM animation_bake_cache bc
WHERE {bakeReadyCacheWhere};");
            var requestWrittenCandidates = ScalarLong(connection, $@"
SELECT COUNT(*)
FROM animation_bake_cache bc
WHERE bc.status='request_written'
  AND {bakeReadyCacheWhere};");
            var bakedCandidates = ScalarLong(connection, $@"
SELECT COUNT(*)
FROM animation_bake_cache bc
WHERE bc.status='baked'
  AND {bakeReadyCacheWhere};");
            var libraryRoot = GetLibraryRootFromConnection(connection);
            var trustedBakedCandidates = CountTrustedBakedCacheRows(connection, libraryRoot);
            var staticPoseCandidates = CountStaticPoseCacheRows(connection, libraryRoot);
            var needsReviewCandidates = CountNeedsReviewCacheRows(connection, libraryRoot);
            var bakedMissingGltfCandidates = CountBakedMissingGltfCacheRows(connection, libraryRoot);
            var failedCandidates = CountUntrustedFailedCacheRows(connection, libraryRoot);
            var summary = new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Unity bake production only counts relation_source=explicit Humanoid/Muscle candidates whose model has a real Unity prefab/Animator.avatar, complete HumanDescription.humanBones + skeletonBones, or an explicitly imported original Unity Avatar asset. trustedBaked requires status=baked, baked glTF exists, unity_bake_apply_report.json is ok/warning, frameVaryingTracks > 0, avatarTrust.TrustedProductionBake=true, and imported_unity_avatar_asset source when the original request supplied unityAssetPaths.avatarAsset. AvatarConstant/internalSolver oracle is diagnostic only until original Unity Avatar/HumanDescription recovery is proven.",
                ["explicitUnityBakeCandidates"] = explicitUnityBakeCandidates,
                ["uniqueExplicitUnityBakeCandidates"] = uniqueExplicitUnityBakeCandidates,
                ["explicitUnityBakeModels"] = explicitUnityBakeModels,
                ["explicitUnityBakeAnimations"] = explicitUnityBakeAnimations,
                ["bakeReadyExplicitUnityBakeCandidates"] = bakeReadyExplicitUnityBakeCandidates,
                ["uniqueBakeReadyExplicitUnityBakeCandidates"] = uniqueBakeReadyExplicitUnityBakeCandidates,
                ["bakeReadyExplicitUnityBakeCoverage"] = Ratio(bakeReadyExplicitUnityBakeCandidates, explicitUnityBakeCandidates),
                ["uniqueBakeReadyExplicitUnityBakeCoverage"] = Ratio(uniqueBakeReadyExplicitUnityBakeCandidates, uniqueExplicitUnityBakeCandidates),
                ["bakeReadyExplicitUnityBakeCoveragePercent"] = Percent(bakeReadyExplicitUnityBakeCandidates, explicitUnityBakeCandidates),
                ["uniqueBakeReadyExplicitUnityBakeCoveragePercent"] = Percent(uniqueBakeReadyExplicitUnityBakeCandidates, uniqueExplicitUnityBakeCandidates),
                ["cachedCandidates"] = cachedCandidates,
                ["requestWrittenCandidates"] = requestWrittenCandidates,
                ["bakedCandidates"] = bakedCandidates,
                ["trustedBakedCandidates"] = trustedBakedCandidates,
                ["staticPoseCandidates"] = staticPoseCandidates,
                ["needsReviewCandidates"] = needsReviewCandidates,
                ["bakedMissingGltfCandidates"] = bakedMissingGltfCandidates,
                ["failedCandidates"] = failedCandidates,
                ["cacheCoverage"] = Ratio(cachedCandidates, bakeReadyExplicitUnityBakeCandidates),
                ["bakedCoverage"] = Ratio(bakedCandidates, bakeReadyExplicitUnityBakeCandidates),
                ["trustedBakedCoverage"] = Ratio(trustedBakedCandidates, bakeReadyExplicitUnityBakeCandidates),
                ["cacheCoveragePercent"] = Percent(cachedCandidates, bakeReadyExplicitUnityBakeCandidates),
                ["bakedCoveragePercent"] = Percent(bakedCandidates, bakeReadyExplicitUnityBakeCandidates),
                ["trustedBakedCoveragePercent"] = Percent(trustedBakedCandidates, bakeReadyExplicitUnityBakeCandidates),
                ["statusCounts"] = QueryGroupedCounts(connection, $@"
SELECT COALESCE(status, '<null>') AS key, COUNT(*) AS count
FROM animation_bake_cache bc
WHERE {bakeReadyCacheWhere}
GROUP BY COALESCE(status, '<null>')
ORDER BY count DESC;"),
                ["effectiveStatusCounts"] = BuildEffectiveBakeCacheStatusCounts(connection, libraryRoot),
            };
            summary.Merge(BuildUniqueBakeCacheCounts(connection, libraryRoot, bakeReadyExplicitUnityBakeCandidates, uniqueBakeReadyExplicitUnityBakeCandidates));
            return summary;
        }

        private static long CountTrustedBakedCacheRows(SqliteConnection connection, string libraryRoot)
        {
            var count = 0L;
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = $@"
SELECT bc.baked_gltf_path
FROM animation_bake_cache bc
WHERE COALESCE(bc.baked_gltf_path, '')<>''
  AND {BuildBakeReadyCacheWhere("bc")};";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (IsTrustedBakedGltfPath(reader.IsDBNull(0) ? null : reader.GetString(0), libraryRoot))
                {
                    count++;
                }
            }
            return count;
        }

        private static JArray BuildEffectiveBakeCacheStatusCounts(SqliteConnection connection, string libraryRoot)
        {
            var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = $@"
SELECT bc.status, bc.baked_gltf_path, bc.message
FROM animation_bake_cache bc
WHERE {BuildBakeReadyCacheWhere("bc")};";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var status = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var bakedGltfPath = reader.IsDBNull(1) ? null : reader.GetString(1);
                var message = reader.IsDBNull(2) ? null : reader.GetString(2);
                var key = IsTrustedBakedGltfPath(bakedGltfPath, libraryRoot)
                    ? "trusted_baked"
                    : IsStaticPoseBakedGltfPath(bakedGltfPath, libraryRoot)
                        ? "static_pose"
                        : IsNeedsReviewBakedGltfPath(bakedGltfPath, libraryRoot)
                            ? "needs_review"
                            : IsAnimatorControllerContextCacheStatus(status, message)
                                ? "needs_animator_controller_context"
                                : string.Equals(status, "baked", StringComparison.OrdinalIgnoreCase)
                                    ? "untrusted_baked"
                                    : string.IsNullOrWhiteSpace(status) ? "<null>" : status;
                counts[key] = counts.TryGetValue(key, out var current) ? current + 1 : 1;
            }

            return new JArray(counts
                .OrderByDescending(x => x.Value)
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => new JObject
                {
                    ["key"] = x.Key,
                    ["count"] = x.Value,
                }));
        }

        private static long CountUntrustedFailedCacheRows(SqliteConnection connection, string libraryRoot)
        {
            var count = 0L;
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = $@"
SELECT bc.baked_gltf_path
FROM animation_bake_cache bc
WHERE bc.status='failed'
  AND {BuildBakeReadyCacheWhere("bc")};";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var bakedGltfPath = reader.IsDBNull(0) ? null : reader.GetString(0);
                if (!IsTrustedBakedGltfPath(bakedGltfPath, libraryRoot)
                    && !IsStaticPoseBakedGltfPath(bakedGltfPath, libraryRoot)
                    && !IsNeedsReviewBakedGltfPath(bakedGltfPath, libraryRoot))
                {
                    count++;
                }
            }
            return count;
        }

        private static long CountBakedMissingGltfCacheRows(SqliteConnection connection, string libraryRoot)
        {
            var count = 0L;
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = $@"
SELECT bc.baked_gltf_path
FROM animation_bake_cache bc
WHERE bc.status='baked'
  AND {BuildBakeReadyCacheWhere("bc")};";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var bakedGltfPath = reader.IsDBNull(0) ? null : reader.GetString(0);
                if (!IsTrustedBakedGltfPath(bakedGltfPath, libraryRoot)
                    && !IsStaticPoseBakedGltfPath(bakedGltfPath, libraryRoot))
                {
                    count++;
                }
            }
            return count;
        }

        private static long CountStaticPoseCacheRows(SqliteConnection connection, string libraryRoot)
        {
            var count = 0L;
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = $@"
SELECT bc.baked_gltf_path
FROM animation_bake_cache bc
WHERE COALESCE(bc.baked_gltf_path, '')<>''
  AND {BuildBakeReadyCacheWhere("bc")};";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (IsStaticPoseBakedGltfPath(reader.IsDBNull(0) ? null : reader.GetString(0), libraryRoot))
                {
                    count++;
                }
            }
            return count;
        }

        private static long CountNeedsReviewCacheRows(SqliteConnection connection, string libraryRoot)
        {
            var count = 0L;
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = $@"
SELECT bc.baked_gltf_path
FROM animation_bake_cache bc
WHERE COALESCE(bc.baked_gltf_path, '')<>''
  AND {BuildBakeReadyCacheWhere("bc")};";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (IsNeedsReviewBakedGltfPath(reader.IsDBNull(0) ? null : reader.GetString(0), libraryRoot))
                {
                    count++;
                }
            }
            return count;
        }

        private static JObject BuildUniqueBakeCacheCounts(
            SqliteConnection connection,
            string libraryRoot,
            long bakeReadyExplicitUnityBakeCandidates,
            long uniqueBakeReadyExplicitUnityBakeCandidates)
        {
            var groups = new Dictionary<string, UniqueBakeCacheEntry>(StringComparer.OrdinalIgnoreCase);
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = $@"
SELECT bc.model_output, bc.animation_output, bc.status, bc.baked_gltf_path, bc.message
FROM animation_bake_cache bc
WHERE {BuildBakeReadyCacheWhere("bc")};";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var modelOutput = reader.IsDBNull(0) ? null : reader.GetString(0);
                var animationOutput = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (string.IsNullOrWhiteSpace(modelOutput) || string.IsNullOrWhiteSpace(animationOutput))
                {
                    continue;
                }

                var key = CanonicalizeLibraryOutput(modelOutput, libraryRoot)
                    + "|"
                    + CanonicalizeLibraryOutput(animationOutput, libraryRoot);
                if (!groups.TryGetValue(key, out var entry))
                {
                    entry = new UniqueBakeCacheEntry();
                    groups[key] = entry;
                }

                entry.RowCount++;
                var status = reader.IsDBNull(2) ? null : reader.GetString(2);
                var bakedGltf = reader.IsDBNull(3) ? null : reader.GetString(3);
                var message = reader.IsDBNull(4) ? null : reader.GetString(4);
                var hasTrustedBakedGltf = IsTrustedBakedGltfPath(bakedGltf, libraryRoot);
                if (hasTrustedBakedGltf)
                {
                    entry.HasBaked = true;
                    entry.HasTrustedBaked = true;
                }
                else if (IsStaticPoseBakedGltfPath(bakedGltf, libraryRoot))
                {
                    entry.HasStaticPose = true;
                }
                else if (IsNeedsReviewBakedGltfPath(bakedGltf, libraryRoot))
                {
                    entry.HasNeedsReview = true;
                }
                else if (IsAnimatorControllerContextCacheStatus(status, message))
                {
                    entry.HasNeedsAnimatorControllerContext = true;
                }
                else if (string.Equals(status, "request_written", StringComparison.OrdinalIgnoreCase))
                {
                    entry.HasRequestWritten = true;
                }
                else if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    entry.HasFailed = true;
                }
                else if (string.Equals(status, "baked", StringComparison.OrdinalIgnoreCase))
                {
                    entry.HasBaked = true;
                }
            }

            var uniqueCachedCandidates = groups.Count;
            var uniqueTrustedBakedCandidates = groups.Values.Count(x => x.HasTrustedBaked);
            var uniqueBakedCandidates = groups.Values.Count(x => x.HasBaked);
            var uniqueBakedMissingGltfCandidates = groups.Values.Count(x => x.HasBaked && !x.HasTrustedBaked);
            var uniqueStaticPoseCandidates = groups.Values.Count(x => !x.HasTrustedBaked && x.HasStaticPose);
            var uniqueNeedsReviewCandidates = groups.Values.Count(x => !x.HasTrustedBaked && !x.HasStaticPose && x.HasNeedsReview);
            var uniqueAnimatorControllerContextCandidates = groups.Values.Count(x => !x.HasTrustedBaked && !x.HasStaticPose && !x.HasNeedsReview && x.HasNeedsAnimatorControllerContext);
            var uniqueFailedCandidates = groups.Values.Count(x => !x.HasBaked && !x.HasStaticPose && !x.HasNeedsReview && !x.HasNeedsAnimatorControllerContext && x.HasFailed);
            var uniqueRequestWrittenCandidates = groups.Values.Count(x => !x.HasBaked && !x.HasStaticPose && !x.HasNeedsReview && !x.HasNeedsAnimatorControllerContext && !x.HasFailed && x.HasRequestWritten);
            var duplicateCacheRows = groups.Values.Sum(x => Math.Max(0, x.RowCount - 1));
            var terminalDiagnosticCandidates = uniqueStaticPoseCandidates + uniqueNeedsReviewCandidates + uniqueAnimatorControllerContextCandidates;

            return new JObject
            {
                ["uniqueCachedCandidates"] = uniqueCachedCandidates,
                ["uniqueRequestWrittenCandidates"] = uniqueRequestWrittenCandidates,
                ["uniqueBakedCandidates"] = uniqueBakedCandidates,
                ["uniqueTrustedBakedCandidates"] = uniqueTrustedBakedCandidates,
                ["uniqueStaticPoseCandidates"] = uniqueStaticPoseCandidates,
                ["uniqueNeedsReviewCandidates"] = uniqueNeedsReviewCandidates,
                ["uniqueAnimatorControllerContextCandidates"] = uniqueAnimatorControllerContextCandidates,
                ["uniqueBakedMissingGltfCandidates"] = uniqueBakedMissingGltfCandidates,
                ["uniqueFailedCandidates"] = uniqueFailedCandidates,
                ["duplicateCacheRows"] = duplicateCacheRows,
                ["pendingUnityBakeCandidates"] = Math.Max(0, bakeReadyExplicitUnityBakeCandidates - uniqueTrustedBakedCandidates - terminalDiagnosticCandidates),
                ["uniquePendingUnityBakeCandidates"] = Math.Max(0, uniqueBakeReadyExplicitUnityBakeCandidates - uniqueTrustedBakedCandidates - terminalDiagnosticCandidates),
                ["uniqueCacheCoverage"] = Ratio(uniqueCachedCandidates, uniqueBakeReadyExplicitUnityBakeCandidates),
                ["uniqueTrustedBakedCoverage"] = Ratio(uniqueTrustedBakedCandidates, uniqueBakeReadyExplicitUnityBakeCandidates),
                ["uniqueCacheCoveragePercent"] = Percent(uniqueCachedCandidates, uniqueBakeReadyExplicitUnityBakeCandidates),
                ["uniqueTrustedBakedCoveragePercent"] = Percent(uniqueTrustedBakedCandidates, uniqueBakeReadyExplicitUnityBakeCandidates),
            };
        }

        private static bool IsTrustedBakedGltfPath(string bakedGltfPath, string libraryRoot)
        {
            var bakedGltf = ResolveLibraryPath(libraryRoot, bakedGltfPath);
            if (string.IsNullOrWhiteSpace(bakedGltf) || !File.Exists(bakedGltf))
            {
                return false;
            }

            var reportPath = Path.Combine(Path.GetDirectoryName(bakedGltf) ?? string.Empty, "unity_bake_apply_report.json");
            if (!File.Exists(reportPath))
            {
                return false;
            }

            try
            {
                var report = JObject.Parse(File.ReadAllText(reportPath));
                var status = (string)report["status"];
                if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(status, "warning", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return (int?)report["frameVaryingTracks"] > 0
                    && !UsesFirstSampleHumanoidDelta(report)
                    && HasReusableAnimationSolve(report)
                    && IsTrustedAvatarBake(report)
                    && HasTrustedAnimationClipBake(report);
            }
            catch
            {
                return false;
            }
        }

        private static bool HasReusableAnimationSolve(JObject report)
        {
            var solve = report?["animationSolve"] as JObject;
            if (solve == null)
            {
                return true;
            }

            // 新报告里的 animationSolve 是比旧 status/frameVaryingTracks 更严格的门禁。
            // 只要求解器明确说还需要视觉验收或只是诊断，就不能计入 trusted bake。
            return ((bool?)solve["writesReusableGltfTrsCandidate"] ?? false)
                && ((bool?)solve["productionReady"] ?? false)
                && !((bool?)solve["requiresVisualValidation"] ?? false);
        }

        private static bool IsStaticPoseBakedGltfPath(string bakedGltfPath, string libraryRoot)
        {
            var bakedGltf = ResolveLibraryPath(libraryRoot, bakedGltfPath);
            if (string.IsNullOrWhiteSpace(bakedGltf) || !File.Exists(bakedGltf))
            {
                return false;
            }

            var reportPath = Path.Combine(Path.GetDirectoryName(bakedGltf) ?? string.Empty, "unity_bake_apply_report.json");
            if (!File.Exists(reportPath))
            {
                return false;
            }

            try
            {
                var report = JObject.Parse(File.ReadAllText(reportPath));
                return string.Equals((string)report["status"], "static_pose", StringComparison.OrdinalIgnoreCase)
                    && ((int?)report["frameVaryingTracks"] ?? 0) == 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsNeedsReviewBakedGltfPath(string bakedGltfPath, string libraryRoot)
        {
            var bakedGltf = ResolveLibraryPath(libraryRoot, bakedGltfPath);
            if (string.IsNullOrWhiteSpace(bakedGltf) || !File.Exists(bakedGltf))
            {
                return false;
            }

            var reportPath = Path.Combine(Path.GetDirectoryName(bakedGltf) ?? string.Empty, "unity_bake_apply_report.json");
            if (!File.Exists(reportPath))
            {
                return false;
            }

            try
            {
                var report = JObject.Parse(File.ReadAllText(reportPath));
                return string.Equals((string)report["status"], "needs_review", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsAnimatorControllerContextCacheStatus(string status, string message)
        {
            return string.Equals(status, "needs_animator_controller_context", StringComparison.OrdinalIgnoreCase)
                && NeedsAnimatorControllerContext(message);
        }

        private static bool NeedsAnimatorControllerContext(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            return message.IndexOf("isHumanMotion=false", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("AnimatorController context", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("AnimatorController auxiliary", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("non-body layer", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("baseLayerClip", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsTrustedAvatarBake(JObject report)
        {
            var avatarTrust = report["avatarTrust"] as JObject;
            if (avatarTrust == null)
            {
                return false;
            }

            var trusted = (bool?)avatarTrust["TrustedProductionBake"]
                ?? (bool?)avatarTrust["trustedProductionBake"]
                ?? false;
            if (!trusted)
            {
                return false;
            }

            var source = (string)avatarTrust["Source"] ?? (string)avatarTrust["source"];
            if (ReportRequestHasExplicitAvatarAsset(report))
            {
                return string.Equals(source, "imported_unity_avatar_asset", StringComparison.OrdinalIgnoreCase)
                    && ReportHasImportedAvatarAssetProof(report);
            }

            return IsProductionAvatarTrustSource(source);
        }

        private static bool ReportRequestHasExplicitAvatarAsset(JObject report)
        {
            if (!string.IsNullOrWhiteSpace((string)report?["unityBakeRequestedAvatarAsset"]))
            {
                return true;
            }

            var requestPath = (string)report?["request"];
            if (string.IsNullOrWhiteSpace(requestPath) || !File.Exists(requestPath))
            {
                return false;
            }

            try
            {
                var request = JObject.Parse(File.ReadAllText(requestPath));
                return !string.IsNullOrWhiteSpace((string)request["unityAssetPaths"]?["avatarAsset"]);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsProductionAvatarTrustSource(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return false;
            }

            if (source.Contains("internal_solver", StringComparison.OrdinalIgnoreCase)
                || source.Contains("avatar_constant", StringComparison.OrdinalIgnoreCase)
                || source.Contains("oracle", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private static bool ReportHasImportedAvatarAssetProof(JObject report)
        {
            if ((bool?)report?["unityBakeImportedAvatarAssetValid"] ?? false)
            {
                return true;
            }

            return string.Equals((string)report?["unityBakeRigRestPoseSource"], "imported_unity_avatar_asset", StringComparison.OrdinalIgnoreCase)
                && ((bool?)report?["unityBakeRigRestPoseApplied"] ?? false);
        }

        private static bool HasTrustedAnimationClipBake(JObject report)
        {
            if (!ReportRequestHasExplicitAvatarAsset(report))
            {
                return true;
            }

            // 走 ImportedAvatar oracle 的 Humanoid bake，clip 也必须是 Unity 工程内
            // 明确恢复/导入的 AnimationClip asset。旧 Browser 已按这条规则拦截旧缓存；
            // SQLite 摘要也要保持一致，避免把语义不可信的旧 bake 统计成 trusted。
            var source = (string)report?["unityBakeAnimationClipSource"];
            var importedClip = (string)report?["unityBakeImportedAnimationClip"];
            return string.Equals(source, "unityAssetPaths.animationClip", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(importedClip);
        }

        private static bool UsesFirstSampleHumanoidDelta(JObject report)
        {
            return string.Equals(
                (string)report?["humanoidDeltaBase"],
                "first_sample",
                StringComparison.OrdinalIgnoreCase);
        }

        private static string GetLibraryRootFromConnection(SqliteConnection connection)
        {
            try
            {
                return Path.GetDirectoryName(Path.GetFullPath(connection.DataSource)) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string CanonicalizeLibraryOutput(string path, string libraryRoot)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(libraryRoot) && Path.IsPathRooted(path))
            {
                try
                {
                    var fullRoot = Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var fullPath = Path.GetFullPath(path);
                    if (fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        return NormalizeLibraryOutput(Path.GetRelativePath(fullRoot, fullPath));
                    }
                }
                catch
                {
                    return NormalizeLibraryOutput(path);
                }
            }

            return NormalizeLibraryOutput(path);
        }

        private static string NormalizeLibraryOutput(string path)
        {
            return (path ?? string.Empty)
                .Trim()
                .Trim('"')
                .Replace('\\', '/')
                .TrimStart('/')
                .TrimEnd('/');
        }

        private static string BuildBakeReadyCacheWhere(string cacheAlias)
        {
            return $@"
EXISTS (
  SELECT 1
  FROM model_animation_candidates c
  JOIN assets m ON m.kind='Model' AND m.output=c.model_output
  WHERE c.model_output={cacheAlias}.model_output
    AND c.animation_output={cacheAlias}.animation_output
    AND c.relation_source='explicit'
    AND (
      COALESCE(json_extract(c.raw_json, '$.requiresUnityBake'), 0)=1
      OR COALESCE(json_extract(c.raw_json, '$.legacyUnityBakeSupported'), 0)=1
      OR COALESCE(json_extract(c.raw_json, '$.requiresInternalHumanoidSolve'), 0)=1
    )
    AND {BakeReadyAvatarSql("m")}
)";
        }

        private static string BakeReadyAvatarSql(string modelAlias)
        {
            var raw = $"{modelAlias}.raw_json";
            return $@"(
    COALESCE(json_array_length(json_extract({raw}, '$.avatar.humanBones')), 0) > 0
    AND COALESCE(json_array_length(json_extract({raw}, '$.avatar.skeletonBones')), 0) > 0
  )";
        }

        private sealed class UniqueBakeCacheEntry
        {
            public int RowCount { get; set; }
            public bool HasRequestWritten { get; set; }
            public bool HasBaked { get; set; }
            public bool HasTrustedBaked { get; set; }
            public bool HasStaticPose { get; set; }
            public bool HasNeedsReview { get; set; }
            public bool HasNeedsAnimatorControllerContext { get; set; }
            public bool HasFailed { get; set; }
        }

        private static long SourceRelationCount(SqliteConnection connection, string relation)
        {
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = "SELECT COUNT(*) FROM source_relations WHERE relation=$relation;";
            command.Parameters.AddWithValue("$relation", relation);
            return Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }

        private static long SourceRelationTargetCountPositive(SqliteConnection connection, string relation)
        {
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = "SELECT COUNT(*) FROM source_relations WHERE relation=$relation AND COALESCE(target_count, 0) > 0;";
            command.Parameters.AddWithValue("$relation", relation);
            return Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }

        private static long SourceRelationResolvedTargetCount(SqliteConnection connection, string relation, string targetType)
        {
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = @"
SELECT COUNT(*)
FROM source_relations r
JOIN source_objects o
  ON o.serialized_file = r.to_file
 AND o.path_id = r.to_path_id
 AND o.type = $targetType
WHERE r.relation = $relation;";
            command.Parameters.AddWithValue("$relation", relation);
            command.Parameters.AddWithValue("$targetType", targetType);
            return Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }

        private static long SourceScalarLong(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }

        private static string SourceScalarString(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = sql;
            return command.ExecuteScalar() as string;
        }

        private static JArray QueryResourceKindCoverage(SqliteConnection connection)
        {
            var result = new JArray();
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = @"
SELECT COALESCE(a.resource_kind, '(null)') AS resource_kind,
       COUNT(*) AS total,
       SUM(CASE WHEN s.model_output IS NULL THEN 0 ELSE 1 END) AS with_explicit
FROM assets a
LEFT JOIN model_animation_candidate_model_summary s ON s.model_output = a.output AND s.explicit_count > 0
WHERE a.kind='Model'
GROUP BY COALESCE(a.resource_kind, '(null)')
ORDER BY with_explicit DESC, total DESC;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var total = reader.GetInt64(1);
                var linked = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                result.Add(new JObject
                {
                    ["resourceKind"] = reader.GetString(0),
                    ["models"] = total,
                    ["modelsWithExplicitCandidates"] = linked,
                    ["explicitCoverage"] = Ratio(linked, total)
                });
            }
            return result;
        }

        private static JObject QueryLinkedAnimationSidecarCapabilities(SqliteConnection connection, string root)
        {
            var rows = new List<(string Output, string AnimationAsset, long CandidateCount)>();
            using (var command = connection.CreateCommand())
            {
                command.CommandTimeout = SummaryQueryTimeoutSeconds;
                command.CommandText = @"
SELECT a.output,
       json_extract(a.raw_json, '$.animationAsset') AS animation_asset,
       s.explicit_model_count AS candidate_count
FROM model_animation_candidate_animation_summary s
JOIN assets a ON a.output = s.animation_output AND a.kind='Animation'
WHERE s.explicit_model_count > 0;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add((
                        reader.IsDBNull(0) ? null : reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? 0 : reader.GetInt64(2)
                    ));
                }
            }

            var decodedStatus = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var playbackKinds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            long withSidecar = 0;
            long missingSidecar = 0;
            long unreadableSidecar = 0;
            long decodedOkAnimations = 0;
            long directGltfReadyAnimations = 0;
            long requiresInternalHumanoidSolveAnimations = 0;
            long skippedFullCurvesAnimations = 0;
            long decodedOkCandidates = 0;
            long directGltfReadyCandidates = 0;
            long requiresInternalHumanoidSolveCandidates = 0;

            foreach (var row in rows)
            {
                var sidecarPath = ResolveLibraryPath(root, row.AnimationAsset);
                if (string.IsNullOrWhiteSpace(sidecarPath) || !File.Exists(sidecarPath))
                {
                    missingSidecar++;
                    continue;
                }

                withSidecar++;
                if (!TryReadDecodedCapability(sidecarPath, out var capability))
                {
                    unreadableSidecar++;
                    AddCount(decodedStatus, "unreadable");
                    continue;
                }

                var status = string.IsNullOrWhiteSpace(capability.Status) ? "(missing)" : capability.Status;
                AddCount(decodedStatus, status);
                if (!string.IsNullOrWhiteSpace(capability.PlaybackKind))
                {
                    AddCount(playbackKinds, capability.PlaybackKind);
                }

                var decodedOk = string.Equals(capability.Status, "ok", StringComparison.OrdinalIgnoreCase);
                var directCandidate = IsDirectGltfSidecarCandidate(capability);
                var internalHumanoidCandidate = IsInternalHumanoidSidecarCandidate(capability);

                if (decodedOk)
                {
                    decodedOkAnimations++;
                    decodedOkCandidates += row.CandidateCount;
                }
                if (directCandidate)
                {
                    directGltfReadyAnimations++;
                    directGltfReadyCandidates += row.CandidateCount;
                }
                if (internalHumanoidCandidate)
                {
                    requiresInternalHumanoidSolveAnimations++;
                    requiresInternalHumanoidSolveCandidates += row.CandidateCount;
                }
                if (string.Equals(capability.Status, "skipped", StringComparison.OrdinalIgnoreCase))
                {
                    skippedFullCurvesAnimations++;
                }
            }

            return new JObject
            {
                ["rule"] = "只扫描已经被 explicit Unity 关系引用到的动画 sidecar。这里是直接 glTF 生成能力的输入状态，不等于视觉验收通过。",
                ["linkedAnimations"] = rows.Count,
                ["withAnimationAssetSidecar"] = withSidecar,
                ["missingAnimationAssetSidecar"] = missingSidecar,
                ["unreadableAnimationAssetSidecar"] = unreadableSidecar,
                ["decodedOkAnimations"] = decodedOkAnimations,
                ["directGltfReadyAnimations"] = directGltfReadyAnimations,
                ["requiresInternalHumanoidSolveAnimations"] = requiresInternalHumanoidSolveAnimations,
                ["skippedFullCurvesAnimations"] = skippedFullCurvesAnimations,
                ["decodedOkCandidates"] = decodedOkCandidates,
                ["directGltfReadyCandidates"] = directGltfReadyCandidates,
                ["requiresInternalHumanoidSolveCandidates"] = requiresInternalHumanoidSolveCandidates,
                ["inferenceNote"] = "旧 sidecar 没有 directGltfReady/playbackKind 字段时，会用 decoded.curveCounts 与 float category 保守推断：Transform/BlendShape 曲线可直接写 glTF；Animator/Humanoid float 需要内部 Humanoid solver。",
                ["decodedStatus"] = ToCountArray(decodedStatus),
                ["playbackKinds"] = ToCountArray(playbackKinds),
            };
        }

        private static bool IsDirectGltfSidecarCandidate(DecodedCapability capability)
        {
            if (!string.Equals(capability.Status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (capability.DirectGltfReady == true)
            {
                return true;
            }
            if (IsInternalHumanoidSidecarCandidate(capability))
            {
                return false;
            }
            return capability.TransformCurveCount > 0 || capability.HasBlendShapeFloat;
        }

        private static bool IsInternalHumanoidSidecarCandidate(DecodedCapability capability)
        {
            if (!string.Equals(capability.Status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return capability.RequiresInternalHumanoidSolve == true
                || string.Equals(capability.PlaybackKind, "HumanoidMuscleNeedsInternalSolver", StringComparison.OrdinalIgnoreCase)
                || capability.HasHumanoidFloat;
        }

        private static string ResolveLibraryPath(string root, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }
            return Path.IsPathRooted(path)
                ? path
                : Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        }

        private static bool TryReadDecodedCapability(string path, out DecodedCapability capability)
        {
            capability = default;
            try
            {
                using var stream = File.OpenRead(path);
                using var text = new StreamReader(stream);
                using var reader = new JsonTextReader(text);
                while (reader.Read())
                {
                    if (reader.TokenType != JsonToken.PropertyName
                        || !string.Equals((string)reader.Value, "decoded", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!reader.Read() || reader.TokenType != JsonToken.StartObject)
                    {
                        return true;
                    }

                    var decodedDepth = reader.Depth;
                    while (reader.Read())
                    {
                        if (reader.TokenType == JsonToken.EndObject && reader.Depth == decodedDepth)
                        {
                            return true;
                        }
                        if (reader.TokenType != JsonToken.PropertyName)
                        {
                            continue;
                        }

                        var name = (string)reader.Value;
                        if (!reader.Read())
                        {
                            return true;
                        }

                        switch (name)
                        {
                            case "status":
                                capability.Status = reader.Value?.ToString();
                                break;
                            case "playbackKind":
                                capability.PlaybackKind = reader.Value?.ToString();
                                break;
                            case "directGltfReady":
                                capability.DirectGltfReady = ReadBool(reader.Value);
                                break;
                            case "requiresInternalHumanoidSolve":
                                capability.RequiresInternalHumanoidSolve = ReadBool(reader.Value);
                                break;
                            case "curveCounts":
                                ReadCurveCounts(reader, ref capability);
                                break;
                            case "floats":
                                ReadFloatCurveKinds(reader, ref capability);
                                break;
                            default:
                                reader.Skip();
                                break;
                        }
                    }
                    return true;
                }
                return true;
            }
            catch (Exception e) when (e is IOException || e is JsonException || e is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool? ReadBool(object value)
        {
            if (value is bool b)
            {
                return b;
            }
            return bool.TryParse(value?.ToString(), out var parsed) ? parsed : null;
        }

        private static void ReadCurveCounts(JsonTextReader reader, ref DecodedCapability capability)
        {
            if (reader.TokenType != JsonToken.StartObject)
            {
                reader.Skip();
                return;
            }

            var depth = reader.Depth;
            while (reader.Read())
            {
                if (reader.TokenType == JsonToken.EndObject && reader.Depth == depth)
                {
                    return;
                }
                if (reader.TokenType != JsonToken.PropertyName)
                {
                    continue;
                }

                var name = (string)reader.Value;
                if (!reader.Read())
                {
                    return;
                }

                var value = ReadInt(reader.Value);
                switch (name)
                {
                    case "translations":
                    case "rotations":
                    case "scales":
                    case "eulers":
                        capability.TransformCurveCount += value;
                        break;
                }
            }
        }

        private static void ReadFloatCurveKinds(JsonTextReader reader, ref DecodedCapability capability)
        {
            if (reader.TokenType != JsonToken.StartArray)
            {
                reader.Skip();
                return;
            }

            var depth = reader.Depth;
            while (reader.Read())
            {
                if (reader.TokenType == JsonToken.EndArray && reader.Depth == depth)
                {
                    return;
                }
                if (reader.TokenType != JsonToken.StartObject)
                {
                    continue;
                }

                ReadOneFloatCurveKind(reader, ref capability);
            }
        }

        private static void ReadOneFloatCurveKind(JsonTextReader reader, ref DecodedCapability capability)
        {
            var depth = reader.Depth;
            string category = null;
            string classId = null;
            string attribute = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonToken.EndObject && reader.Depth == depth)
                {
                    break;
                }
                if (reader.TokenType != JsonToken.PropertyName)
                {
                    continue;
                }

                var name = (string)reader.Value;
                if (!reader.Read())
                {
                    break;
                }

                switch (name)
                {
                    case "category":
                        category = reader.Value?.ToString();
                        break;
                    case "classID":
                        classId = reader.Value?.ToString();
                        break;
                    case "attribute":
                        attribute = reader.Value?.ToString();
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            if (string.Equals(category, "HumanoidMuscleOrAnimator", StringComparison.OrdinalIgnoreCase)
                || string.Equals(classId, "Animator", StringComparison.OrdinalIgnoreCase))
            {
                capability.HasHumanoidFloat = true;
            }
            if (string.Equals(category, "BlendShape", StringComparison.OrdinalIgnoreCase)
                || (attribute ?? string.Empty).StartsWith("blendShape.", StringComparison.OrdinalIgnoreCase))
            {
                capability.HasBlendShapeFloat = true;
            }
        }

        private static int ReadInt(object value)
        {
            if (value is int i)
            {
                return i;
            }
            if (value is long l)
            {
                return l > int.MaxValue ? int.MaxValue : (int)l;
            }
            return int.TryParse(value?.ToString(), out var parsed) ? parsed : 0;
        }

        private static void AddCount(Dictionary<string, long> counts, string key, long value = 1)
        {
            key = string.IsNullOrWhiteSpace(key) ? "(missing)" : key;
            counts.TryGetValue(key, out var current);
            counts[key] = current + value;
        }

        private static JArray ToCountArray(Dictionary<string, long> counts)
        {
            var result = new JArray();
            foreach (var item in counts.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(new JObject
                {
                    ["key"] = item.Key,
                    ["count"] = item.Value
                });
            }
            return result;
        }

        private struct DecodedCapability
        {
            public string Status { get; set; }
            public string PlaybackKind { get; set; }
            public bool? DirectGltfReady { get; set; }
            public bool? RequiresInternalHumanoidSolve { get; set; }
            public int TransformCurveCount { get; set; }
            public bool HasHumanoidFloat { get; set; }
            public bool HasBlendShapeFloat { get; set; }
        }

        private static JObject BuildDistribution(List<long> values)
        {
            if (values.Count == 0)
            {
                return new JObject
                {
                    ["linkedModelCount"] = 0
                };
            }

            return new JObject
            {
                ["linkedModelCount"] = values.Count,
                ["min"] = values[0],
                ["median"] = Percentile(values, 0.50),
                ["p95"] = Percentile(values, 0.95),
                ["max"] = values[^1],
                ["average"] = Ratio(values.Sum(), values.Count)
            };
        }

        private static JArray QueryGroupedCounts(SqliteConnection connection, string sql)
        {
            var result = new JArray();
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new JObject
                {
                    ["key"] = reader.GetString(0),
                    ["count"] = reader.GetInt64(1)
                });
            }
            return result;
        }

        private static List<long> QueryLongList(SqliteConnection connection, string sql)
        {
            var result = new List<long>();
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(reader.GetInt64(0));
            }
            return result;
        }

        private static long ScalarLong(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = sql;
            return command.ExecuteScalar() is long value ? value : 0;
        }

        private static long ScalarLong(SqliteConnection connection, string sql, SqliteTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = SummaryQueryTimeoutSeconds;
            command.CommandText = sql;
            return command.ExecuteScalar() is long value ? value : 0;
        }

        private static bool HasTable(SqliteConnection connection, string tableName)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
            command.Parameters.AddWithValue("$name", tableName);
            return Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture) > 0;
        }

        private static bool SourceIndexHasIndex(string sourceIndexPath, string indexName)
        {
            if (string.IsNullOrWhiteSpace(sourceIndexPath) || !File.Exists(sourceIndexPath))
            {
                return false;
            }

            using var connection = new SqliteConnection($"Data Source={Path.GetFullPath(sourceIndexPath)};Mode=ReadOnly");
            connection.Open();
            return HasSqliteIndex(connection, indexName);
        }

        private static bool HasSqliteIndex(SqliteConnection connection, string indexName)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=$name;";
            command.Parameters.AddWithValue("$name", indexName);
            return Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture) > 0;
        }

        private static double Ratio(long numerator, long denominator)
        {
            return denominator <= 0 ? 0 : Math.Round((double)numerator / denominator, 6);
        }

        private static double Percent(long numerator, long denominator)
        {
            return denominator <= 0 ? 0 : Math.Round((double)numerator * 100.0 / denominator, 6);
        }

        private static double Percentile(List<long> sortedValues, double percentile)
        {
            if (sortedValues.Count == 0)
            {
                return 0;
            }
            var index = (int)Math.Floor((sortedValues.Count - 1) * percentile);
            if (index < 0)
            {
                index = 0;
            }
            if (index >= sortedValues.Count)
            {
                index = sortedValues.Count - 1;
            }
            return sortedValues[index];
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

        private static void Execute(SqliteConnection connection, string sql, SqliteTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
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

        private static double? D(JObject obj, string name)
        {
            var token = obj?[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            return token.Type == JTokenType.Float
                || token.Type == JTokenType.Integer
                || double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                ? token.Value<double>()
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

        private static bool IsTrue(JObject obj, string name)
        {
            return B(obj, name) == 1;
        }

        private static string MakeRelative(string root, string path)
        {
            return Path.GetRelativePath(root, path).Replace('\\', '/');
        }

        private static string NormalizeLibraryOutputForJoin(string root, string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return string.Empty;
            }

            try
            {
                if (Path.IsPathRooted(output))
                {
                    var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var fullOutput = Path.GetFullPath(output);
                    if (fullOutput.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || fullOutput.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        output = Path.GetRelativePath(fullRoot, fullOutput);
                    }
                }
            }
            catch
            {
                // 路径可能来自旧 catalog 或外部工具。归一化失败时保留原文本做 key，
                // 不因为诊断字段格式问题中断 SQLite 重建。
            }

            return output.Replace('\\', '/').TrimStart('/');
        }

        private static string NormalizeAssetOutputKey(string root, string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return string.Empty;
            }

            var fullPath = Path.IsPathRooted(output)
                ? output
                : Path.Combine(root, output);
            return Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static SourceKey KeyFromRelation(SqliteDataReader reader, int fileOrdinal, int pathIdOrdinal)
        {
            return new SourceKey(reader.IsDBNull(fileOrdinal) ? "" : reader.GetString(fileOrdinal), reader.GetInt64(pathIdOrdinal));
        }

        private readonly record struct SourceKey(string File, long PathId)
        {
            public bool IsValid => !string.IsNullOrWhiteSpace(File) && PathId != 0;

            public SourceKey Normalize()
            {
                return new SourceKey((File ?? string.Empty).Trim().ToLowerInvariant(), PathId);
            }
        }

        private sealed record CatalogAsset(SourceKey Key, string Name, string Output, JObject Raw);
        private sealed record ModelAnimationGate(bool Ready, string Status, string[] Reasons, JObject Evidence);

        private sealed record SourceObject(string Type, string Name);

        private sealed record SourceAnimationRelation(SourceKey ClipKey, string Relation, string Reason, JObject AnimatorControllerContext = null, JObject RelationEvidence = null);

        private readonly record struct CatalogLookupKey(string SourcePath, long PathId, string Type, string Name)
        {
            public bool IsValid => !string.IsNullOrWhiteSpace(SourcePath)
                && PathId != 0
                && !string.IsNullOrWhiteSpace(Type)
                && !string.IsNullOrWhiteSpace(Name);

            public CatalogLookupKey Normalize()
            {
                return new CatalogLookupKey(
                    (SourcePath ?? string.Empty).Replace('\\', '/').Trim().ToLowerInvariant(),
                    PathId,
                    (Type ?? string.Empty).Trim().ToLowerInvariant(),
                    (Name ?? string.Empty).Trim().ToLowerInvariant());
            }
        }

        private sealed class SourceAnimationGraph
        {
            private const int MaxHierarchyDepth = 96;
            private readonly Dictionary<SourceKey, List<SourceKey>> _gameObjectComponents = new();
            private readonly Dictionary<SourceKey, SourceKey> _componentGameObjects = new();
            private readonly Dictionary<SourceKey, List<SourceKey>> _transformChildren = new();
            private readonly Dictionary<SourceKey, SourceKey> _transformParents = new();
            private readonly Dictionary<SourceKey, List<SourceKey>> _animatorControllers = new();
            private readonly Dictionary<SourceKey, List<SourceKey>> _animationClips = new();
            private readonly Dictionary<SourceKey, List<SourceKey>> _controllerClips = new();
            private readonly Dictionary<SourceKey, List<SourceAnimationRelation>> _controllerClipRelations = new();
            private readonly Dictionary<SourceKey, List<SourceKey>> _monoBehaviourPPtrTargets = new();
            private readonly Dictionary<SourceKey, List<SourceKey>> _monoBehaviourPPtrSources = new();
            private readonly Dictionary<SourceKey, List<SourceKey>> _avatarAnimators = new();
            private readonly Dictionary<SourceKey, List<SourceKey>> _overrideBaseControllers = new();
            private readonly Dictionary<SourceKey, List<SourceKey>> _overrideOriginalClips = new();
            private readonly Dictionary<SourceKey, List<SourceKey>> _overrideClips = new();
            private readonly Dictionary<SourceKey, List<OverrideClipPair>> _overrideClipPairs = new();
            private readonly Dictionary<SourceKey, int> _overrideSetCounts = new();
            private readonly Dictionary<SourceKey, SourceKey> _sourceObjectAliases = new();
            private readonly HashSet<SourceKey> _ambiguousSourceObjectAliases = new();
            private readonly Dictionary<CatalogLookupKey, SourceKey> _catalogObjectAliases = new();
            private readonly HashSet<CatalogLookupKey> _ambiguousCatalogObjectAliases = new();
            private readonly Dictionary<SourceKey, List<SourceAnimationRelation>> _hierarchyClipCache = new();
            private long _skippedStaleOverrideControllers;
            private string _sourceRoot;

            public Dictionary<SourceKey, SourceObject> Objects { get; } = new();

            public long SkippedStaleOverrideControllers => _skippedStaleOverrideControllers;

            public static SourceAnimationGraph Load(string sourceIndex)
            {
                var watch = Stopwatch.StartNew();
                var graph = new SourceAnimationGraph();
                using var connection = new SqliteConnection($"Data Source={sourceIndex};Mode=ReadOnly");
                connection.Open();
                graph.LoadMetadata(connection);
                graph.LoadObjects(connection);
                graph.LoadRelations(connection);
                Logger.Info($"SQLite source animation graph loaded in {watch.Elapsed.TotalSeconds:F1}s; objects={graph.Objects.Count}");
                return graph;
            }

            public SourceKey KeyFromCatalog(JObject obj)
            {
                var source = S(obj, "source") ?? S(obj, "sourceFile") ?? string.Empty;
                var pathId = I(obj, "pathId") ?? 0;
                var type = S(obj, "sourceType") ?? string.Empty;
                var name = S(obj, "name") ?? string.Empty;
                if (TryResolveCatalogLookup(source, pathId, type, name, out var catalogKey))
                {
                    return catalogKey;
                }

                foreach (var file in BuildCatalogFileKeys(source))
                {
                    var key = ResolveSourceObjectKey(new SourceKey(file, pathId).Normalize());
                    if (Objects.ContainsKey(key))
                    {
                        return key;
                    }
                }

                return new SourceKey(Path.GetFileName(source), pathId).Normalize();
            }

            public IEnumerable<SourceAnimationRelation> FindAnimationClipsForModel(SourceKey modelKey)
            {
                modelKey = ResolveSourceObjectKey(modelKey.Normalize());
                if (!Objects.TryGetValue(modelKey, out var modelObject))
                {
                    yield break;
                }

                var seen = new HashSet<SourceKey>();
                if (string.Equals(modelObject.Type, "Animator", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var relation in FindAnimatorClips(modelKey))
                    {
                        if (seen.Add(relation.ClipKey))
                        {
                            yield return relation with
                            {
                                Relation = "animator.controller.clip",
                                Reason = "Model source is an Animator with an explicit RuntimeAnimatorController.",
                            };
                        }
                    }
                    yield break;
                }

                foreach (var relation in FindHierarchyAnimationClips(modelKey))
                {
                    if (seen.Add(relation.ClipKey))
                    {
                        yield return relation;
                    }
                }

                foreach (var relation in FindAncestorAnimationClips(modelKey, seen))
                {
                    yield return relation;
                }

                foreach (var relation in FindSharedAvatarControllerClips(modelKey, seen))
                {
                    yield return relation;
                }
            }

            private IEnumerable<SourceAnimationRelation> FindSharedAvatarControllerClips(SourceKey modelKey, HashSet<SourceKey> seen)
            {
                foreach (var config in GetMany(_monoBehaviourPPtrSources, modelKey))
                {
                    Objects.TryGetValue(config, out var configObject);
                    foreach (var avatar in GetMany(_monoBehaviourPPtrTargets, config))
                    {
                        if (!Objects.TryGetValue(avatar, out var avatarObject)
                            || !string.Equals(avatarObject.Type, "Avatar", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        foreach (var animator in GetMany(_avatarAnimators, avatar))
                        {
                            Objects.TryGetValue(animator, out var animatorObject);
                            foreach (var relation in FindAnimatorClips(animator))
                            {
                                if (!seen.Add(relation.ClipKey))
                                {
                                    continue;
                                }

                                yield return relation with
                                {
                                    Relation = "sharedAvatarController",
                                    Reason = "Model config explicitly points to this prefab and Avatar; an Animator using the same Avatar explicitly references the Controller clip.",
                                    RelationEvidence = BuildSharedAvatarEvidence(config, configObject, avatar, avatarObject, animator, animatorObject),
                                };
                            }
                        }
                    }
                }
            }

            private JObject BuildSharedAvatarEvidence(
                SourceKey config,
                SourceObject configObject,
                SourceKey avatar,
                SourceObject avatarObject,
                SourceKey animator,
                SourceObject animatorObject)
            {
                _componentGameObjects.TryGetValue(animator, out var animatorModel);
                Objects.TryGetValue(animatorModel, out var animatorModelObject);
                return new JObject
                {
                    ["bridge"] = "configPrefabAvatarToAnimatorController",
                    ["configName"] = configObject?.Name,
                    ["configFile"] = config.File,
                    ["configPathId"] = config.PathId,
                    ["avatarName"] = avatarObject?.Name,
                    ["avatarFile"] = avatar.File,
                    ["avatarPathId"] = avatar.PathId,
                    ["animatorName"] = animatorObject?.Name,
                    ["animatorFile"] = animator.File,
                    ["animatorPathId"] = animator.PathId,
                    ["animatorModelName"] = animatorModelObject?.Name,
                    ["animatorModelFile"] = animatorModel.File,
                    ["animatorModelPathId"] = animatorModel.PathId,
                };
            }

            private IReadOnlyList<SourceAnimationRelation> FindHierarchyAnimationClips(SourceKey gameObject)
            {
                gameObject = ResolveSourceObjectKey(gameObject.Normalize());
                if (_hierarchyClipCache.TryGetValue(gameObject, out var cached))
                {
                    return cached;
                }

                return BuildHierarchyAnimationClips(gameObject, new HashSet<SourceKey>(), 0);
            }

            private List<SourceAnimationRelation> BuildHierarchyAnimationClips(SourceKey gameObject, HashSet<SourceKey> active, int depth)
            {
                if (_hierarchyClipCache.TryGetValue(gameObject, out var cached))
                {
                    return cached;
                }
                if (depth > MaxHierarchyDepth || !active.Add(gameObject))
                {
                    return new List<SourceAnimationRelation>();
                }

                var result = new List<SourceAnimationRelation>();
                var seen = new HashSet<SourceKey>();
                foreach (var component in GetMany(_gameObjectComponents, gameObject))
                {
                    if (!Objects.TryGetValue(component, out var componentObject))
                    {
                        continue;
                    }

                    if (string.Equals(componentObject.Type, "Animator", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var relation in FindAnimatorClips(component))
                        {
                            AddHierarchyRelation(result, seen, relation with
                            {
                                Relation = "gameObject.hierarchy.animator.controller.clip",
                                Reason = $"Animator '{componentObject.Name}' is attached to the exported GameObject hierarchy.",
                            });
                        }
                    }
                    else if (string.Equals(componentObject.Type, "Animation", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var clip in GetMany(_animationClips, component))
                        {
                            AddHierarchyRelation(result, seen, new SourceAnimationRelation(clip, "gameObject.hierarchy.animation.clip", $"Animation component '{componentObject.Name}' explicitly references this clip."));
                        }
                    }
                    else if (string.Equals(componentObject.Type, "Transform", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var childTransform in GetMany(_transformChildren, component))
                        {
                            if (!_componentGameObjects.TryGetValue(childTransform, out var childGameObject))
                            {
                                continue;
                            }

                            foreach (var relation in BuildHierarchyAnimationClips(childGameObject, active, depth + 1))
                            {
                                AddHierarchyRelation(result, seen, relation);
                            }
                        }
                    }
                }

                active.Remove(gameObject);
                _hierarchyClipCache[gameObject] = result;
                return result;
            }

            private static void AddHierarchyRelation(List<SourceAnimationRelation> result, HashSet<SourceKey> seen, SourceAnimationRelation relation)
            {
                if (relation.ClipKey.IsValid && seen.Add(relation.ClipKey))
                {
                    result.Add(relation);
                }
            }

            private IEnumerable<SourceAnimationRelation> FindAncestorAnimationClips(SourceKey gameObject, HashSet<SourceKey> seen)
            {
                foreach (var transform in GetMany(_gameObjectComponents, gameObject)
                    .Where(x => Objects.TryGetValue(x, out var obj) && string.Equals(obj.Type, "Transform", StringComparison.OrdinalIgnoreCase)))
                {
                    var current = transform;
                    for (var depth = 0; depth < MaxHierarchyDepth && _transformParents.TryGetValue(current, out var parentTransform); depth++)
                    {
                        current = parentTransform;
                        if (!_componentGameObjects.TryGetValue(current, out var parentGameObject))
                        {
                            continue;
                        }

                        foreach (var component in GetMany(_gameObjectComponents, parentGameObject))
                        {
                            if (!Objects.TryGetValue(component, out var componentObject))
                            {
                                continue;
                            }

                            if (string.Equals(componentObject.Type, "Animator", StringComparison.OrdinalIgnoreCase))
                            {
                                foreach (var relation in FindAnimatorClips(component))
                                {
                                    if (seen.Add(relation.ClipKey))
                                    {
                                        yield return relation with
                                        {
                                            Relation = "gameObject.ancestor.animator.controller.clip",
                                            Reason = $"Ancestor Animator '{componentObject.Name}' controls the exported GameObject hierarchy.",
                                        };
                                    }
                                }
                            }
                            else if (string.Equals(componentObject.Type, "Animation", StringComparison.OrdinalIgnoreCase))
                            {
                                foreach (var clip in GetMany(_animationClips, component))
                                {
                                    if (seen.Add(clip))
                                    {
                                        yield return new SourceAnimationRelation(clip, "gameObject.ancestor.animation.clip", $"Ancestor Animation component '{componentObject.Name}' controls the exported GameObject hierarchy.");
                                    }
                                }
                            }
                        }
                    }
                }
            }

            private SourceKey ResolveSourceObjectKey(SourceKey modelKey)
            {
                if (Objects.ContainsKey(modelKey))
                {
                    return modelKey;
                }

                return _sourceObjectAliases.TryGetValue(modelKey, out var resolved)
                    ? resolved
                    : modelKey;
            }

            private bool TryResolveCatalogLookup(string source, long pathId, string type, string name, out SourceKey key)
            {
                foreach (var file in BuildCatalogFileKeys(source))
                {
                    var lookup = new CatalogLookupKey(file, pathId, type, name).Normalize();
                    if (!lookup.IsValid)
                    {
                        continue;
                    }

                    if (_catalogObjectAliases.TryGetValue(lookup, out key) && Objects.ContainsKey(key))
                    {
                        return true;
                    }
                }

                key = default;
                return false;
            }

            private IEnumerable<string> BuildCatalogFileKeys(string source)
            {
                if (string.IsNullOrWhiteSpace(source))
                {
                    yield break;
                }

                var serializedFile = ExtractCatalogSerializedFile(source);
                if (!string.IsNullOrWhiteSpace(serializedFile))
                {
                    yield return serializedFile;
                }

                var normalized = GetCatalogOuterSourcePath(source).Replace('\\', '/');
                if (!string.IsNullOrWhiteSpace(_sourceRoot))
                {
                    var root = _sourceRoot.Replace('\\', '/').TrimEnd('/');
                    if (normalized.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        yield return normalized[(root.Length + 1)..];
                    }
                }

                yield return normalized;
                yield return Path.GetFileName(normalized);
            }

            private IEnumerable<SourceAnimationRelation> FindAnimatorClips(SourceKey animator)
            {
                foreach (var controller in GetMany(_animatorControllers, animator))
                {
                    foreach (var relation in FindControllerClips(controller, new HashSet<SourceKey>()))
                    {
                        yield return relation;
                    }
                }
            }

            private IEnumerable<SourceAnimationRelation> FindControllerClips(SourceKey controller, HashSet<SourceKey> visited)
            {
                if (!visited.Add(controller))
                {
                    yield break;
                }

                var overridePairs = GetMany(_overrideClipPairs, controller).ToArray();
                var baseControllers = GetMany(_overrideBaseControllers, controller).ToArray();
                var overrideOriginalClips = GetMany(_overrideOriginalClips, controller).ToArray();
                var overrideClips = GetMany(_overrideClips, controller).ToArray();
                var hasKnownOverrideSet = _overrideSetCounts.ContainsKey(controller);
                var overrideSetCount = hasKnownOverrideSet ? _overrideSetCounts[controller] : -1;
                var overriddenOriginalClips = overridePairs
                    .Where(x => x.OverrideClip.IsValid)
                    .Select(x => x.OriginalClip)
                    .Where(x => x.IsValid)
                    .ToHashSet();

                var isOverrideController = Objects.TryGetValue(controller, out var controllerObject)
                    && string.Equals(controllerObject.Type, "AnimatorOverrideController", StringComparison.OrdinalIgnoreCase);
                if (isOverrideController && baseControllers.Length > 0 && overridePairs.Length == 0)
                {
                    var hasSeparatedOverrideRefs = overrideOriginalClips.Length > 0 || overrideClips.Length > 0;
                    var hasNonEmptyOverrideSetWithoutPairs = hasKnownOverrideSet && overrideSetCount > 0;
                    if (!hasKnownOverrideSet || hasSeparatedOverrideRefs || hasNonEmptyOverrideSetWithoutPairs)
                    {
                        // 旧 source_index 只有 originalClip/overrideClip 分离列表时，无法证明
                        // original -> override 的一一对应关系。生产候选宁可缺失，也不能猜 pair。
                        _skippedStaleOverrideControllers++;
                        yield break;
                    }
                }

                foreach (var relation in GetControllerClipRelations(controller))
                {
                    yield return relation;
                }

                foreach (var baseController in baseControllers)
                {
                    foreach (var relation in FindControllerClips(baseController, visited))
                    {
                        if (!overriddenOriginalClips.Contains(relation.ClipKey))
                        {
                            yield return relation;
                        }
                    }
                }

                if (overridePairs.Length > 0)
                {
                    foreach (var pair in overridePairs)
                    {
                        var selected = pair.OverrideClip.IsValid ? pair.OverrideClip : pair.OriginalClip;
                        if (selected.IsValid)
                        {
                            yield return new SourceAnimationRelation(selected, "animatorOverrideController.clipPair", "AnimatorOverrideController deterministically selected this override clip.");
                        }
                    }
                }
            }

            private IEnumerable<SourceAnimationRelation> GetControllerClipRelations(SourceKey controller)
            {
                if (_controllerClipRelations.TryGetValue(controller, out var relations))
                {
                    return AddDefaultAdditiveLayerHints(relations);
                }

                return GetMany(_controllerClips, controller)
                    .Select(clip => new SourceAnimationRelation(clip, "animatorController.clip", "AnimatorController explicitly references this AnimationClip."));
            }

            private IEnumerable<SourceAnimationRelation> AddDefaultAdditiveLayerHints(IReadOnlyList<SourceAnimationRelation> relations)
            {
                foreach (var relation in relations)
                {
                    yield return relation with
                    {
                        AnimatorControllerContext = AddDefaultAdditiveLayerHints(relation, relations),
                    };
                }
            }

            private JObject AddDefaultAdditiveLayerHints(SourceAnimationRelation baseRelation, IReadOnlyList<SourceAnimationRelation> relations)
            {
                var context = baseRelation.AnimatorControllerContext;
                if (context == null)
                {
                    return null;
                }

                var baseLayerIndex = ReadFirstLayerInt(context, "layerIndex");
                if (baseLayerIndex != 0)
                {
                    return context;
                }

                var motionStem = ReadMotionStem(context);
                if (string.IsNullOrWhiteSpace(motionStem))
                {
                    return context;
                }

                var additional = new JArray();
                foreach (var relation in relations)
                {
                    if (relation.ClipKey.Equals(baseRelation.ClipKey)
                        || relation.AnimatorControllerContext == null
                        || !IsDefaultAdditiveLayer(relation.AnimatorControllerContext)
                        || !ContextMentionsMotionStem(relation.AnimatorControllerContext, motionStem))
                    {
                        continue;
                    }

                    var layerClip = BuildAdditionalLayerClip(relation);
                    if (layerClip != null)
                    {
                        additional.Add(layerClip);
                    }
                }

                if (additional.Count == 0)
                {
                    return context;
                }

                var copy = (JObject)context.DeepClone();
                copy["additionalLayerClips"] = additional;
                return copy;
            }

            private JObject BuildAdditionalLayerClip(SourceAnimationRelation relation)
            {
                var context = relation.AnimatorControllerContext;
                if (context == null)
                {
                    return null;
                }

                Objects.TryGetValue(relation.ClipKey, out var clipObject);
                return new JObject
                {
                    ["name"] = clipObject?.Name,
                    ["file"] = relation.ClipKey.File,
                    ["pathId"] = relation.ClipKey.PathId,
                    ["layerIndex"] = ReadFirstLayerInt(context, "layerIndex"),
                    ["layerBlendingMode"] = ReadFirstLayerInt(context, "layerBlendingMode"),
                    ["layerDefaultWeight"] = ReadFirstLayerFloat(context, "layerDefaultWeight"),
                    ["layerBodyMask"] = ReadFirstLayerToken(context, "layerBodyMask")?.DeepClone(),
                    ["layerSkeletonMask"] = ReadFirstLayerToken(context, "layerSkeletonMask")?.DeepClone(),
                    ["stateName"] = context["stateName"],
                    ["statePath"] = context["statePath"],
                    ["stateFullPath"] = context["stateFullPath"],
                    ["stateSpeed"] = context["stateSpeed"],
                    ["stateCycleOffset"] = context["stateCycleOffset"],
                    ["stateLoop"] = context["stateLoop"],
                    ["stateMirror"] = context["stateMirror"],
                    ["nodeCycleOffset"] = context["nodeCycleOffset"],
                    ["nodeMirror"] = context["nodeMirror"],
                    ["source"] = context["source"],
                };
            }

            private static bool IsDefaultAdditiveLayer(JObject context)
            {
                return ReadFirstLayerInt(context, "layerIndex") > 0
                    && ReadFirstLayerInt(context, "layerBlendingMode") == 1
                    && ReadFirstLayerFloat(context, "layerDefaultWeight") > 0.0001f;
            }

            private static bool ContextMentionsMotionStem(JObject context, string motionStem)
            {
                if (string.IsNullOrWhiteSpace(motionStem))
                {
                    return false;
                }

                var stateText = string.Join(" ",
                    (string)context?["stateName"],
                    (string)context?["statePath"],
                    (string)context?["stateFullPath"]);
                return stateText.IndexOf(motionStem, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            private static string ReadMotionStem(JObject context)
            {
                var stateText = string.Join(" ",
                    (string)context?["stateName"],
                    (string)context?["statePath"],
                    (string)context?["stateFullPath"]);
                if (string.IsNullOrWhiteSpace(stateText))
                {
                    return null;
                }

                // 只使用跨 Unity 项目常见的动作词元，且只作为同一 AnimatorController 内附加层采样提示。
                // 它不会新增模型-动画绑定关系。
                foreach (var stem in new[] { "Idle", "Walk", "Run", "Sprint", "Jump", "Turn", "Attack", "Skill", "Move", "Bump" })
                {
                    if (stateText.IndexOf(stem, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return stem;
                    }
                }

                return null;
            }

            private static int ReadFirstLayerInt(JObject context, string name)
            {
                var value = ReadFirstLayerToken(context, name);
                return value?.Type == JTokenType.Integer ? value.Value<int>() : 0;
            }

            private static float ReadFirstLayerFloat(JObject context, string name)
            {
                var value = ReadFirstLayerToken(context, name);
                return value?.Type == JTokenType.Float || value?.Type == JTokenType.Integer
                    ? value.Value<float>()
                    : 0f;
            }

            private static JToken ReadFirstLayerToken(JObject context, string name)
            {
                return (context?["layers"] as JArray)?.OfType<JObject>().FirstOrDefault()?[name];
            }

            private void LoadObjects(SqliteConnection connection)
            {
                var watch = Stopwatch.StartNew();
                long count = 0;
                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT source_path, serialized_file, path_id, type, name
FROM source_objects
WHERE type IN ('GameObject','Animator','Animation','AnimatorController','AnimatorOverrideController','AnimationClip','Avatar','MonoBehaviour');";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var sourcePath = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    var serializedFile = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    var pathId = reader.GetInt64(2);
                    var key = new SourceKey(serializedFile, pathId).Normalize();
                    Objects[key] = new SourceObject(reader.GetString(3), reader.IsDBNull(4) ? "" : reader.GetString(4));
                    AddSourceObjectAlias(new SourceKey(sourcePath, pathId), key);
                    AddSourceObjectAlias(new SourceKey(Path.GetFileName(sourcePath), pathId), key);
                    AddCatalogObjectAlias(new CatalogLookupKey(sourcePath, pathId, reader.GetString(3), reader.IsDBNull(4) ? "" : reader.GetString(4)), key);
                    AddCatalogObjectAlias(new CatalogLookupKey(Path.GetFileName(sourcePath), pathId, reader.GetString(3), reader.IsDBNull(4) ? "" : reader.GetString(4)), key);
                    count++;
                }
                Logger.Info($"SQLite source animation graph loaded source_objects in {watch.Elapsed.TotalSeconds:F1}s; rows={count}");
            }

            private void LoadMetadata(SqliteConnection connection)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM metadata WHERE key = 'sourceRoot' LIMIT 1;";
                _sourceRoot = command.ExecuteScalar() as string;
            }

            private void AddSourceObjectAlias(SourceKey alias, SourceKey target)
            {
                alias = alias.Normalize();
                target = target.Normalize();
                if (!alias.IsValid || !target.IsValid || alias.Equals(target))
                {
                    return;
                }

                // 一个 .blk 里可能拆出多个 CAB。只有别名唯一时才用于回连，避免把同 pathId 的不同对象绑错。
                if (_sourceObjectAliases.TryGetValue(alias, out var existing) && !existing.Equals(target))
                {
                    _sourceObjectAliases.Remove(alias);
                    _ambiguousSourceObjectAliases.Add(alias);
                    return;
                }

                if (!_ambiguousSourceObjectAliases.Contains(alias))
                {
                    _sourceObjectAliases[alias] = target;
                }
            }

            private void AddCatalogObjectAlias(CatalogLookupKey alias, SourceKey target)
            {
                alias = alias.Normalize();
                target = target.Normalize();
                if (!alias.IsValid || !target.IsValid)
                {
                    return;
                }

                // Endfield 的一个外层 .blc 会拆出很多 CAB，单靠 .blc + PathID 会撞。
                // 加上 Unity 类型和对象名后仍不唯一时，保持拒绝绑定，避免把动画关系接错。
                if (_catalogObjectAliases.TryGetValue(alias, out var existing) && !existing.Equals(target))
                {
                    _catalogObjectAliases.Remove(alias);
                    _ambiguousCatalogObjectAliases.Add(alias);
                    return;
                }

                if (!_ambiguousCatalogObjectAliases.Contains(alias))
                {
                    _catalogObjectAliases[alias] = target;
                }
            }

            private void LoadRelations(SqliteConnection connection)
            {
                var watch = Stopwatch.StartNew();
                long count = 0;
                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT relation, from_file, from_path_id, to_file, to_path_id, from_type, from_name, raw_json
FROM source_relations
WHERE relation IN (
  'component.gameObject',
  'transform.parent',
  'transform.child',
  'monoBehaviour.pptr',
  'animator.avatar',
  'animator.controller',
  'animation.clip',
  'animatorController.clip',
  'animatorOverrideController.baseController',
  'animatorOverrideController.overrideSet',
  'animatorOverrideController.overrideClip',
  'animatorOverrideController.originalClip'
);";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var relation = reader.GetString(0);
                    var from = KeyFromRelation(reader, 1, 2).Normalize();
                    var to = KeyFromRelation(reader, 3, 4).Normalize();
                    if (!from.IsValid || !to.IsValid)
                    {
                        continue;
                    }

                    switch (relation)
                    {
                        case "component.gameObject":
                            var componentType = reader.IsDBNull(5) ? "" : reader.GetString(5);
                            if (IsAnimationRelevantComponentType(componentType))
                            {
                                Objects[from] = new SourceObject(componentType, reader.IsDBNull(6) ? "" : reader.GetString(6));
                                Add(_gameObjectComponents, to, from);
                                _componentGameObjects[from] = to;
                            }
                            break;
                        case "transform.parent":
                            _transformParents[from] = to;
                            break;
                        case "transform.child":
                            Add(_transformChildren, from, to);
                            break;
                        case "monoBehaviour.pptr":
                            Add(_monoBehaviourPPtrTargets, from, to);
                            Add(_monoBehaviourPPtrSources, to, from);
                            break;
                        case "animator.avatar":
                            Add(_avatarAnimators, to, from);
                            break;
                        case "animator.controller":
                            Add(_animatorControllers, from, to);
                            break;
                        case "animation.clip":
                            Add(_animationClips, from, to);
                            break;
                        case "animatorController.clip":
                            Add(_controllerClips, from, to);
                            Add(_controllerClipRelations, from, new SourceAnimationRelation(
                                to,
                                "animatorController.clip",
                                "AnimatorController explicitly references this AnimationClip.",
                                TryReadAnimatorControllerContext(reader.IsDBNull(7) ? null : reader.GetString(7))));
                            break;
                        case "animatorOverrideController.baseController":
                            Add(_overrideBaseControllers, from, to);
                            break;
                        case "animatorOverrideController.overrideSet":
                            _overrideSetCounts[from] = 0;
                            break;
                        case "animatorOverrideController.overrideClip":
                            Add(_overrideClips, from, to);
                            break;
                        case "animatorOverrideController.originalClip":
                            Add(_overrideOriginalClips, from, to);
                            break;
                    }
                    count++;
                }

                using (var pairCommand = connection.CreateCommand())
                {
                    pairCommand.CommandText = @"
SELECT from_file, from_path_id, raw_json
FROM source_relations
WHERE relation = 'animatorOverrideController.clipPair';";
                    using var pairReader = pairCommand.ExecuteReader();
                    while (pairReader.Read())
                    {
                        var from = KeyFromRelation(pairReader, 0, 1).Normalize();
                        if (!from.IsValid || !TryReadOverrideClipPair(pairReader.IsDBNull(2) ? null : pairReader.GetString(2), out var pair))
                        {
                            continue;
                        }

                        Add(_overrideClipPairs, from, pair);
                        count++;
                    }
                }
                LoadOverrideSetCounts(connection);
                Logger.Info($"SQLite source animation graph loaded source_relations in {watch.Elapsed.TotalSeconds:F1}s; rows={count}");
                if (_overrideBaseControllers.Count > 0 && _overrideClipPairs.Count == 0 && _overrideOriginalClips.Count == 0 && _overrideClips.Count == 0 && _overrideSetCounts.Count == 0)
                {
                    Logger.Warning("SQLite source animation graph has AnimatorOverrideController baseController relations but no animatorOverrideController.overrideSet/clipPair details; stale override controllers will not expand base controller clips.");
                }
            }

            private void LoadOverrideSetCounts(SqliteConnection connection)
            {
                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT from_file, from_path_id, target_count, raw_json
FROM source_relations
WHERE relation = 'animatorOverrideController.overrideSet';";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var from = KeyFromRelation(reader, 0, 1).Normalize();
                    if (!from.IsValid)
                    {
                        continue;
                    }

                    var count = reader.IsDBNull(2) ? TryReadOverrideSetCount(reader.IsDBNull(3) ? null : reader.GetString(3)) : reader.GetInt32(2);
                    _overrideSetCounts[from] = Math.Max(0, count);
                }
            }

            private static IEnumerable<SourceKey> GetMany(Dictionary<SourceKey, List<SourceKey>> map, SourceKey key)
            {
                return map.TryGetValue(key, out var values) ? values : Enumerable.Empty<SourceKey>();
            }

            private static bool IsAnimationRelevantComponentType(string type)
            {
                return string.Equals(type, "Transform", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(type, "Animator", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(type, "Animation", StringComparison.OrdinalIgnoreCase);
            }

            private static void Add(Dictionary<SourceKey, List<SourceKey>> map, SourceKey key, SourceKey value)
            {
                if (!map.TryGetValue(key, out var list))
                {
                    list = new List<SourceKey>();
                    map[key] = list;
                }

                list.Add(value);
            }

            private static IEnumerable<OverrideClipPair> GetMany(Dictionary<SourceKey, List<OverrideClipPair>> map, SourceKey key)
            {
                return map.TryGetValue(key, out var values) ? values : Enumerable.Empty<OverrideClipPair>();
            }

            private static void Add(Dictionary<SourceKey, List<OverrideClipPair>> map, SourceKey key, OverrideClipPair value)
            {
                if (!map.TryGetValue(key, out var list))
                {
                    list = new List<OverrideClipPair>();
                    map[key] = list;
                }

                list.Add(value);
            }

            private static void Add(Dictionary<SourceKey, List<SourceAnimationRelation>> map, SourceKey key, SourceAnimationRelation value)
            {
                if (!map.TryGetValue(key, out var list))
                {
                    list = new List<SourceAnimationRelation>();
                    map[key] = list;
                }

                list.Add(value);
            }

            private static JObject TryReadAnimatorControllerContext(string rawJson)
            {
                if (string.IsNullOrWhiteSpace(rawJson))
                {
                    return null;
                }

                try
                {
                    var details = JObject.Parse(rawJson)["details"] as JObject;
                    if (details == null)
                    {
                        return null;
                    }

                    return new JObject
                    {
                        ["source"] = details["source"],
                        ["controllerClipIndex"] = details["controllerClipIndex"],
                        ["baseLayerClip"] = details["baseLayerClip"],
                        ["layers"] = details["layers"],
                        ["stateMachineIndex"] = details["stateMachineIndex"],
                        ["stateIndex"] = details["stateIndex"],
                        ["stateName"] = details["stateName"],
                        ["statePath"] = details["statePath"],
                        ["stateFullPath"] = details["stateFullPath"],
                        ["stateSpeed"] = details["stateSpeed"],
                        ["stateCycleOffset"] = details["stateCycleOffset"],
                        ["stateLoop"] = details["stateLoop"],
                        ["stateMirror"] = details["stateMirror"],
                        ["blendTreeIndex"] = details["blendTreeIndex"],
                        ["nodeIndex"] = details["nodeIndex"],
                        ["nodeBlendType"] = details["nodeBlendType"],
                        ["nodeBlendEvent"] = details["nodeBlendEvent"],
                        ["nodeBlendEventY"] = details["nodeBlendEventY"],
                        ["nodeClipId"] = details["nodeClipId"],
                        ["nodeClipIndex"] = details["nodeClipIndex"],
                        ["nodeDuration"] = details["nodeDuration"],
                        ["nodeCycleOffset"] = details["nodeCycleOffset"],
                        ["nodeMirror"] = details["nodeMirror"],
                    };
                }
                catch
                {
                    return null;
                }
            }

            private static bool TryReadOverrideClipPair(string rawJson, out OverrideClipPair pair)
            {
                pair = default;
                if (string.IsNullOrWhiteSpace(rawJson))
                {
                    return false;
                }

                try
                {
                    var raw = JObject.Parse(rawJson);
                    var details = raw["details"] as JObject;
                    var original = KeyFromPPtr(details?["original"] as JObject).Normalize();
                    var replacement = KeyFromPPtr(details?["override"] as JObject).Normalize();
                    if (!original.IsValid && !replacement.IsValid)
                    {
                        return false;
                    }

                    pair = new OverrideClipPair(original, replacement);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            private static int TryReadOverrideSetCount(string rawJson)
            {
                if (string.IsNullOrWhiteSpace(rawJson))
                {
                    return 0;
                }

                try
                {
                    var raw = JObject.Parse(rawJson);
                    return (int?)raw["details"]?["count"] ?? 0;
                }
                catch
                {
                    return 0;
                }
            }

            private static SourceKey KeyFromPPtr(JObject ptr)
            {
                return new SourceKey((string)ptr?["file"] ?? string.Empty, (long?)ptr?["pathId"] ?? 0);
            }
        }

        private readonly record struct OverrideClipPair(SourceKey OriginalClip, SourceKey OverrideClip);

        private sealed record AnimationBakeCacheRow(
            string ModelOutput,
            string AnimationOutput,
            string Status,
            string RequestPath,
            string ResultPath,
            string BakedGltfPath,
            string BakedFbxPath,
            string Message,
            string BakeMode,
            string UpdatedUtc);

        private sealed record TextureLinkRow(
            string Source,
            string Shared,
            string Usage,
            string Sha256,
            long? SizeBytes,
            string Extension,
            int? HardLinked,
            string LinkError,
            JObject RawJson);

        private sealed record MaterialSidecarRow(
            string Output,
            string Name,
            JObject RawJson);

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
