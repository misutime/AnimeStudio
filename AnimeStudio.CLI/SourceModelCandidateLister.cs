using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AnimeStudio.CLI
{
    internal static class SourceModelCandidateLister
    {
        public static string List(string sourceIndexPath, string outputDirectory, string selector, int limit, bool includeStaticRendererCandidates)
        {
            if (string.IsNullOrWhiteSpace(sourceIndexPath) || !File.Exists(sourceIndexPath))
            {
                Logger.Error($"Unity source index not found: {sourceIndexPath}");
                return null;
            }

            limit = limit <= 0 ? 200 : limit;
            var outputRoot = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sourceIndexPath)) ?? Environment.CurrentDirectory, "SourceModelCandidates")
                : Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(outputRoot);

            SQLitePCL.Batteries_V2.Init();
            using var connection = new SqliteConnection($"Data Source={Path.GetFullPath(sourceIndexPath)};Mode=ReadOnly");
            connection.Open();
            var availableIndexes = LoadAvailableIndexNames(connection);
            WarnMissingPerformanceIndexes(availableIndexes);

            var rows = new List<CandidateRow>();
            var selectorQuery = BuildSelectorQuery(selector);
            var scanLimit = selectorQuery.IsTargeted
                // 精确模型诊断要保持轻量。大游戏里同名 GameObject 可能有几百个变体，
                // 但用户传了具体名字或 PathID 时，不能再按全库扫描的倍率扩张。
                ? (selectorQuery.ExactPathId != 0 ? 1 : Math.Clamp(limit, 20, 200))
                : Math.Clamp(limit * 20, 500, 5000);
            if (selectorQuery.IsTargeted)
            {
                var includePathContainerScan = !HasTargetedObjectMatch(connection, selectorQuery);
                // 显式名字查询用于大源索引诊断。只查可能命中的 Animator/SkinnedRenderer，
                // 避免为了找一个真实主模型先扫描全库候选。
                rows.AddRange(LoadTargetedAnimatorCandidates(connection, scanLimit, selectorQuery, availableIndexes));
                rows.AddRange(LoadTargetedSkinnedRendererCandidates(connection, scanLimit, selectorQuery, availableIndexes));
                // 很多 Unity 导入模型的可读名字只出现在 AssetBundle container 路径，
                // 根 GameObject 未必直接挂 Renderer。定向报告要补这一层，避免真实可导出的
                // prefab/raw fbx 在候选报告里显示为 0。
                rows.AddRange(LoadTargetedContainerPrimaryCandidates(connection, scanLimit, selectorQuery, includePathContainerScan, availableIndexes));
                rows.AddRange(LoadTargetedRawContainerMeshCandidates(connection, scanLimit, selectorQuery, includePathContainerScan, availableIndexes));
            }
            else
            {
                rows.AddRange(LoadContainerPrimaryCandidates(connection, scanLimit, availableIndexes));
                rows.AddRange(LoadRawContainerMeshCandidates(connection, scanLimit));
                rows.AddRange(LoadTexturedRendererContainerCandidates(connection, scanLimit, availableIndexes));
                rows.AddRange(LoadAnimatorCandidates(connection, scanLimit, availableIndexes));
                rows.AddRange(LoadSkinnedRendererCandidates(connection, scanLimit, availableIndexes));
                // 静态 Renderer 全库 join 比角色/prefab 线索重，只有显式打开静态模型扩展时才扫描。
                // 这样既能找场景/道具第一阶段样本，又不会让默认角色诊断变慢。
                if (includeStaticRendererCandidates)
                {
                    rows.AddRange(LoadStaticRendererCandidates(connection, scanLimit, availableIndexes));
                }
            }
            rows = FillContainerPaths(connection, rows, availableIndexes);

            var filtered = rows
                .Where(x => MatchesSelector(selector, x))
                .Select(x => x with { ExcludeHint = BuildExcludeHint(x), Score = Score(x) })
                .GroupBy(BuildCandidateDedupeKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderBy(x => x.ExcludeHint.Length > 0 ? 1 : 0)
                    .ThenByDescending(x => x.Score)
                    .First())
                .OrderBy(x => x.ExcludeHint.Length > 0 ? 1 : 0)
                .ThenByDescending(x => x.Score)
                .ThenBy(x => x.Kind, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();
            var usableForModelSmokeCount = filtered.Count(x => string.IsNullOrWhiteSpace(x.ExcludeHint));
            var excludeSummary = new JArray(filtered
                .GroupBy(x => string.IsNullOrWhiteSpace(x.ExcludeHint) ? "none" : x.ExcludeHint, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x.Count())
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => new JObject
                {
                    ["excludeHint"] = x.Key,
                    ["count"] = x.Count(),
                }));

            var payload = new JObject
            {
                ["schemaVersion"] = 1,
                ["sourceIndex"] = Path.GetFullPath(sourceIndexPath),
                ["outputRoot"] = outputRoot,
                ["selector"] = selector ?? string.Empty,
                ["selectorQueryMode"] = selectorQuery.Mode,
                ["selectorQueryToken"] = selectorQuery.LikeToken,
                ["selectorExactName"] = selectorQuery.ExactName,
                ["selectorExactPathId"] = selectorQuery.ExactPathId,
                ["limit"] = limit,
                ["includeStaticRendererCandidates"] = includeStaticRendererCandidates,
                ["candidateCount"] = filtered.Count,
                ["usableForModelSmokeCount"] = usableForModelSmokeCount,
                ["animationGateDecision"] = usableForModelSmokeCount > 0
                    ? "Some candidates have no source-index excludeHint, but they still require actual model export and validation before animation."
                    : "No candidate in this report is ready for model smoke. Do not enter animation smoke from these samples; pick a different source scope or fix model/material/texture recovery first.",
                ["excludeSummary"] = excludeSummary,
                ["rule"] = "This report lists source-index model candidates from deterministic Unity Animator/Renderer/Mesh/Material relations. It is for selecting model-first smoke samples and does not create model-animation bindings.",
                ["modelFirstRule"] = "Only candidates whose exported model later passes mesh/material/texture/skin validation may enter animation work.",
                ["staticCandidateNote"] = includeStaticRendererCandidates
                    ? "StaticRendererModel scanning is enabled by --include_static_meshes. These rows are first-stage model leads only; export and validate Mesh/UV/material/texture/bbox before using them as smoke evidence."
                    : "StaticRendererModel scanning is intentionally not enabled by default because full-game MeshRenderer/MeshFilter joins can be expensive. Pass --include_static_meshes with --list_source_model_candidates when selecting environment/building/prop smoke samples.",
                ["texturedRendererContainerNote"] = "TexturedRendererContainer candidates are found by walking deterministic Material -> Texture links back to Renderer and AssetBundle containerPreload. They are model-first leads only; source-part, VFX, UI, cutscene, or config containers still need review before export.",
                ["targetedHierarchyNote"] = "SkinnedRendererHierarchyModel deep Transform recursion is intentionally not part of the default targeted source-index report because large Endfield prefabs can make it too slow. Use the fast source report to pick leads, then rely on actual Library export, model_validation, glTF validator and screenshots for model-first acceptance.",
                ["rawContainerModelNote"] = "RawContainerModel candidates are deterministic AssetBundle container groups such as imported .fbx files with Mesh/Avatar but no prefab Renderer/Material chain. They are source parts or diagnostics, not model-smoke passes until a prefab/renderer/material binding is found.",
                ["candidates"] = new JArray(filtered.Select(ToJson)),
            };

            var jsonPath = Path.Combine(outputRoot, "source_model_candidates.json");
            var csvPath = Path.Combine(outputRoot, "source_model_candidates.csv");
            File.WriteAllText(jsonPath, payload.ToString(Formatting.Indented));
            File.WriteAllText(csvPath, ToCsv(filtered), Encoding.UTF8);

            Logger.Info($"Source model candidate report: {jsonPath}");
            Logger.Info($"Source model candidate CSV: {csvPath}");
            Logger.Info($"Usable model-smoke candidates without source excludeHint: {usableForModelSmokeCount}/{filtered.Count}");
            if (usableForModelSmokeCount == 0)
            {
                Logger.Warning("No listed source candidate is ready for model smoke. Keep these samples as diagnostics; do not use them as animation smoke evidence.");
            }
            foreach (var row in filtered.Take(12))
            {
                var hint = string.IsNullOrWhiteSpace(row.ExcludeHint) ? string.Empty : $" [{row.ExcludeHint}]";
                Logger.Info($"- {row.Kind}: {row.Name} mesh={row.MeshCount} material={row.ResolvedMaterialCount}/{row.MaterialCount} textured={row.TexturedMaterialCount} texRefs={row.MaterialTextureRefCount} score={row.Score}{hint}");
            }

            return jsonPath;
        }

        private static HashSet<string> LoadAvailableIndexNames(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='index';";
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    result.Add(name);
                }
            }

            return result;
        }

        private static void WarnMissingPerformanceIndexes(HashSet<string> availableIndexes)
        {
            var missing = new[]
                {
                    "idx_source_relations_from",
                    "idx_source_relations_to",
                    "idx_source_relations_relation",
                    "idx_source_relations_container_relation",
                }
                .Where(index => availableIndexes == null || !availableIndexes.Contains(index))
                .ToArray();
            if (missing.Length == 0)
            {
                return;
            }

            Logger.Warning($"Source model candidate lister is using a source index without relation performance index(es): {string.Join(", ", missing)}. Query may be slower; rebuild the source index for production use.");
        }

        private static string ApplyOptionalIndexHints(string sql, HashSet<string> availableIndexes)
        {
            if (string.IsNullOrWhiteSpace(sql))
            {
                return sql;
            }

            foreach (var index in new[]
                     {
                         "idx_source_relations_from",
                         "idx_source_relations_to",
                         "idx_source_relations_relation",
                         "idx_source_relations_container_relation",
                     })
            {
                if (availableIndexes != null && availableIndexes.Contains(index))
                {
                    continue;
                }

                // 旧的诊断库可能在创建 SQL 索引前中断。去掉强制索引提示，
                // 让 SQLite 自己选计划，避免模型候选报告直接失败。
                sql = sql.Replace($" INDEXED BY {index}", string.Empty, StringComparison.OrdinalIgnoreCase);
            }

            return sql;
        }

        private static List<CandidateRow> LoadAnimatorCandidates(SqliteConnection connection, int limit, HashSet<string> availableIndexes)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT go.name, go.source_path, go.serialized_file, go.path_id,
       '' AS container_path,
       COUNT(DISTINCT animator.path_id) AS animator_count,
       COUNT(DISTINCT avatar.to_file || ':' || avatar.to_path_id) AS avatar_count,
       COUNT(DISTINCT controller.to_file || ':' || controller.to_path_id) AS controller_count
FROM source_objects animator
JOIN source_relations goRel INDEXED BY idx_source_relations_from
  ON goRel.from_file = animator.serialized_file
 AND goRel.from_path_id = animator.path_id
 AND goRel.relation = 'component.gameObject'
JOIN source_objects go
  ON go.serialized_file = goRel.to_file
 AND go.path_id = goRel.to_path_id
LEFT JOIN source_relations avatar INDEXED BY idx_source_relations_from
  ON avatar.from_file = animator.serialized_file
 AND avatar.from_path_id = animator.path_id
 AND avatar.relation = 'animator.avatar'
LEFT JOIN source_relations controller INDEXED BY idx_source_relations_from
  ON controller.from_file = animator.serialized_file
 AND controller.from_path_id = animator.path_id
 AND controller.relation = 'animator.controller'
WHERE animator.type = 'Animator'
GROUP BY go.serialized_file, go.path_id
HAVING avatar_count > 0 OR controller_count > 0
ORDER BY controller_count DESC, avatar_count DESC, go.name COLLATE NOCASE
LIMIT $limit;";
            command.CommandText = ApplyOptionalIndexHints(command.CommandText, availableIndexes);
            command.Parameters.AddWithValue("$limit", limit);
            var result = new List<CandidateRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new CandidateRow(
                    "AnimatorModel",
                    ReadString(reader, 0),
                    ReadString(reader, 1),
                    ReadString(reader, 2),
                    ReadLong(reader, 3),
                    ReadString(reader, 4),
                    ReadLong(reader, 5),
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    ReadLong(reader, 6),
                    ReadLong(reader, 7),
                    0,
                    0,
                    string.Empty,
                    string.Empty,
                    0));
            }

            return result;
        }

        private static List<CandidateRow> LoadTargetedAnimatorCandidates(SqliteConnection connection, int limit, SelectorQuery selectorQuery, HashSet<string> availableIndexes)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
WITH matched_go AS (
    SELECT go.name, go.source_path, go.serialized_file, go.path_id
    FROM source_objects go
    WHERE go.type = 'GameObject'
      AND (($exactPathId != 0 AND go.path_id = $exactPathId)
           OR ($exactPathId = 0 AND go.name = $exactName))
    LIMIT $limit
)
SELECT go.name, go.source_path, go.serialized_file, go.path_id,
       '' AS container_path,
       COUNT(DISTINCT animator.path_id) AS animator_count,
       COUNT(DISTINCT avatar.to_file || ':' || avatar.to_path_id) AS avatar_count,
       COUNT(DISTINCT controller.to_file || ':' || controller.to_path_id) AS controller_count
FROM matched_go go
JOIN source_relations goRel INDEXED BY idx_source_relations_to
  ON goRel.to_file = go.serialized_file
 AND goRel.to_path_id = go.path_id
 AND goRel.relation = 'component.gameObject'
JOIN source_objects animator
  ON animator.serialized_file = goRel.from_file
 AND animator.path_id = goRel.from_path_id
 AND animator.type = 'Animator'
LEFT JOIN source_relations avatar INDEXED BY idx_source_relations_from
  ON avatar.from_file = animator.serialized_file
 AND avatar.from_path_id = animator.path_id
 AND avatar.relation = 'animator.avatar'
LEFT JOIN source_relations controller INDEXED BY idx_source_relations_from
  ON controller.from_file = animator.serialized_file
 AND controller.from_path_id = animator.path_id
 AND controller.relation = 'animator.controller'
GROUP BY go.serialized_file, go.path_id
HAVING avatar_count > 0 OR controller_count > 0
ORDER BY controller_count DESC, avatar_count DESC, go.name COLLATE NOCASE
LIMIT $limit;";
            command.CommandText = ApplyOptionalIndexHints(command.CommandText, availableIndexes);
            command.Parameters.AddWithValue("$exactName", selectorQuery.ExactName ?? string.Empty);
            command.Parameters.AddWithValue("$exactPathId", selectorQuery.ExactPathId);
            command.Parameters.AddWithValue("$limit", limit);
            var result = new List<CandidateRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new CandidateRow(
                    "AnimatorModel",
                    ReadString(reader, 0),
                    ReadString(reader, 1),
                    ReadString(reader, 2),
                    ReadLong(reader, 3),
                    ReadString(reader, 4),
                    ReadLong(reader, 5),
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    ReadLong(reader, 6),
                    ReadLong(reader, 7),
                    0,
                    0,
                    string.Empty,
                    string.Empty,
                    0));
            }

            return result;
        }

        private static List<CandidateRow> LoadContainerPrimaryCandidates(SqliteConnection connection, int limit, HashSet<string> availableIndexes)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
WITH container_roots AS (
    SELECT DISTINCT
           json_extract(rootRel.raw_json, '$.details.container') AS container_path,
           go.name,
           go.source_path,
           go.serialized_file,
           go.path_id
    FROM source_relations rootRel
    JOIN source_objects go
      ON go.serialized_file = rootRel.to_file
     AND go.path_id = rootRel.to_path_id
    WHERE rootRel.relation IN ('assetBundle.containerAsset', 'resourceManager.container')
      AND go.type = 'GameObject'
      AND COALESCE(json_extract(rootRel.raw_json, '$.details.container'), '') <> ''
),
            preload_counts AS (
    SELECT json_extract(preload.raw_json, '$.details.container') AS container_path,
           COUNT(DISTINCT CASE WHEN obj.type = 'Animator' THEN obj.serialized_file || ':' || obj.path_id END) AS animator_count,
           COUNT(DISTINCT CASE
             WHEN obj.type = 'Animator'
              AND COALESCE(json_extract(obj.raw_json, '$.animator.hasTransformHierarchy'), 1) = 0
              AND COALESCE(json_extract(obj.raw_json, '$.animator.avatar.isNull'), 1) = 1
             THEN obj.serialized_file || ':' || obj.path_id END) AS optimized_animator_without_avatar_count,
           COUNT(DISTINCT CASE WHEN obj.type = 'SkinnedMeshRenderer' THEN obj.serialized_file || ':' || obj.path_id END) AS renderer_count,
           COUNT(DISTINCT CASE WHEN obj.type = 'Mesh' THEN obj.serialized_file || ':' || obj.path_id END) AS mesh_count,
           COUNT(DISTINCT CASE WHEN obj.type = 'Avatar' THEN obj.serialized_file || ':' || obj.path_id END) AS avatar_count,
           COUNT(DISTINCT CASE WHEN obj.type = 'AnimatorController' THEN obj.serialized_file || ':' || obj.path_id END) AS controller_count
    FROM source_relations preload
    LEFT JOIN source_objects obj
      ON obj.serialized_file = preload.to_file
     AND obj.path_id = preload.to_path_id
    WHERE preload.relation = 'assetBundle.containerPreload'
    GROUP BY json_extract(preload.raw_json, '$.details.container')
),
renderer_material_counts AS (
    SELECT json_extract(preload.raw_json, '$.details.container') AS container_path,
           COUNT(DISTINCT material.to_file || ':' || material.to_path_id) AS material_count,
           COUNT(DISTINCT resolvedMaterial.serialized_file || ':' || resolvedMaterial.path_id) AS resolved_material_count,
           COUNT(DISTINCT CASE
             WHEN textureRel.to_path_id IS NOT NULL
             THEN resolvedMaterial.serialized_file || ':' || resolvedMaterial.path_id END) AS textured_material_count,
           COUNT(DISTINCT CASE
             WHEN textureRel.to_path_id IS NOT NULL
             THEN textureRel.to_file || ':' || textureRel.to_path_id END) AS material_texture_ref_count
    FROM source_relations preload
    JOIN source_objects renderer
      ON renderer.serialized_file = preload.to_file
     AND renderer.path_id = preload.to_path_id
     AND renderer.type = 'SkinnedMeshRenderer'
    LEFT JOIN source_relations material INDEXED BY idx_source_relations_from
      ON material.from_file = renderer.serialized_file
     AND material.from_path_id = renderer.path_id
     AND material.relation = 'renderer.material'
    LEFT JOIN source_objects resolvedMaterial
      ON resolvedMaterial.serialized_file = material.to_file
     AND resolvedMaterial.path_id = material.to_path_id
     AND resolvedMaterial.type = 'Material'
    LEFT JOIN source_relations textureRel INDEXED BY idx_source_relations_from
      ON textureRel.from_file = resolvedMaterial.serialized_file
     AND textureRel.from_path_id = resolvedMaterial.path_id
     AND textureRel.relation = 'material.texture'
    WHERE preload.relation = 'assetBundle.containerPreload'
    GROUP BY json_extract(preload.raw_json, '$.details.container')
)
SELECT root.name, root.source_path, root.serialized_file, root.path_id, root.container_path,
       COALESCE(counts.animator_count, 0) AS animator_count,
       COALESCE(counts.renderer_count, 0) AS renderer_count,
       COALESCE(counts.mesh_count, 0) AS mesh_count,
       COALESCE(materials.material_count, 0) AS material_count,
       COALESCE(materials.resolved_material_count, 0) AS resolved_material_count,
       COALESCE(materials.textured_material_count, 0) AS textured_material_count,
       COALESCE(materials.material_texture_ref_count, 0) AS material_texture_ref_count,
       COALESCE(counts.avatar_count, 0) AS avatar_count,
       COALESCE(counts.controller_count, 0) AS controller_count,
       COALESCE(counts.optimized_animator_without_avatar_count, 0) AS optimized_animator_without_avatar_count
FROM container_roots root
LEFT JOIN preload_counts counts
  ON counts.container_path = root.container_path
LEFT JOIN renderer_material_counts materials
  ON materials.container_path = root.container_path
WHERE COALESCE(counts.renderer_count, 0) > 0
   OR COALESCE(counts.animator_count, 0) > 0
   OR COALESCE(counts.mesh_count, 0) > 0
ORDER BY counts.renderer_count DESC, counts.mesh_count DESC, counts.animator_count DESC, root.name COLLATE NOCASE
LIMIT $limit;";
            command.CommandText = ApplyOptionalIndexHints(command.CommandText, availableIndexes);
            command.Parameters.AddWithValue("$limit", limit);
            var result = new List<CandidateRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new CandidateRow(
                    "ContainerPrimaryModel",
                    ReadString(reader, 0),
                    ReadString(reader, 1),
                    ReadString(reader, 2),
                    ReadLong(reader, 3),
                    ReadString(reader, 4),
                    ReadLong(reader, 5),
                    ReadLong(reader, 6),
                    ReadLong(reader, 7),
                    ReadLong(reader, 8),
                    ReadLong(reader, 9),
                    ReadLong(reader, 10),
                    ReadLong(reader, 11),
                    ReadLong(reader, 12),
                    ReadLong(reader, 13),
                    0,
                    ReadLong(reader, 14),
                    string.Empty,
                    string.Empty,
                    0));
            }

            return result;
        }

        private static bool HasTargetedObjectMatch(SqliteConnection connection, SelectorQuery selectorQuery)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT 1
FROM source_objects
WHERE (($exactPathId != 0 AND path_id = $exactPathId)
       OR ($exactPathId = 0 AND name = $exactName))
LIMIT 1;";
            command.Parameters.AddWithValue("$exactName", selectorQuery.ExactName ?? string.Empty);
            command.Parameters.AddWithValue("$exactPathId", selectorQuery.ExactPathId);
            return command.ExecuteScalar() != null;
        }

        private static List<CandidateRow> LoadTargetedContainerPrimaryCandidates(SqliteConnection connection, int limit, SelectorQuery selectorQuery, bool includePathContainerScan, HashSet<string> availableIndexes)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
WITH matched_containers AS (
    SELECT DISTINCT json_extract(rel.raw_json, '$.details.container') AS container_path
    FROM source_objects obj
    JOIN source_relations rel INDEXED BY idx_source_relations_to
      ON rel.to_file = obj.serialized_file
     AND rel.to_path_id = obj.path_id
     AND rel.relation IN ('assetBundle.containerAsset', 'assetBundle.containerPreload', 'resourceManager.container')
    WHERE COALESCE(json_extract(rel.raw_json, '$.details.container'), '') <> ''
      AND (($exactPathId != 0 AND obj.path_id = $exactPathId)
           OR ($exactPathId = 0 AND obj.name = $exactName))
    LIMIT $containerLimit
),
__PATH_CONTAINER_CTE__,
selected_containers AS (
    SELECT container_path FROM matched_containers
    UNION
    SELECT container_path FROM path_containers
),
container_roots AS (
    SELECT DISTINCT
           json_extract(rootRel.raw_json, '$.details.container') AS container_path,
           go.name,
           go.source_path,
           go.serialized_file,
           go.path_id
    FROM selected_containers selected
    JOIN source_relations rootRel INDEXED BY idx_source_relations_container_relation
      ON rootRel.relation IN ('assetBundle.containerAsset', 'resourceManager.container')
     AND json_extract(rootRel.raw_json, '$.details.container') = selected.container_path
    JOIN source_objects go
      ON go.serialized_file = rootRel.to_file
     AND go.path_id = rootRel.to_path_id
    WHERE go.type = 'GameObject'
),
preload_counts AS (
    SELECT selected.container_path,
           COUNT(DISTINCT CASE WHEN obj.type = 'Animator' THEN obj.serialized_file || ':' || obj.path_id END) AS animator_count,
           COUNT(DISTINCT CASE
             WHEN obj.type = 'Animator'
              AND COALESCE(json_extract(obj.raw_json, '$.animator.hasTransformHierarchy'), 1) = 0
              AND COALESCE(json_extract(obj.raw_json, '$.animator.avatar.isNull'), 1) = 1
             THEN obj.serialized_file || ':' || obj.path_id END) AS optimized_animator_without_avatar_count,
           COUNT(DISTINCT CASE WHEN obj.type = 'SkinnedMeshRenderer' THEN obj.serialized_file || ':' || obj.path_id END) AS renderer_count,
           COUNT(DISTINCT CASE WHEN obj.type = 'Mesh' THEN obj.serialized_file || ':' || obj.path_id END) AS mesh_count,
           COUNT(DISTINCT CASE WHEN obj.type = 'Avatar' THEN obj.serialized_file || ':' || obj.path_id END) AS avatar_count,
           COUNT(DISTINCT CASE WHEN obj.type = 'AnimatorController' THEN obj.serialized_file || ':' || obj.path_id END) AS controller_count
    FROM selected_containers selected
    JOIN source_relations preload INDEXED BY idx_source_relations_container_relation
      ON preload.relation = 'assetBundle.containerPreload'
     AND json_extract(preload.raw_json, '$.details.container') = selected.container_path
    LEFT JOIN source_objects obj
      ON obj.serialized_file = preload.to_file
     AND obj.path_id = preload.to_path_id
    GROUP BY selected.container_path
),
renderer_material_counts AS (
    SELECT selected.container_path,
           COUNT(DISTINCT material.to_file || ':' || material.to_path_id) AS material_count,
           COUNT(DISTINCT resolvedMaterial.serialized_file || ':' || resolvedMaterial.path_id) AS resolved_material_count,
           COUNT(DISTINCT CASE
             WHEN textureRel.to_path_id IS NOT NULL
             THEN resolvedMaterial.serialized_file || ':' || resolvedMaterial.path_id END) AS textured_material_count,
           COUNT(DISTINCT CASE
             WHEN textureRel.to_path_id IS NOT NULL
             THEN textureRel.to_file || ':' || textureRel.to_path_id END) AS material_texture_ref_count
    FROM selected_containers selected
    JOIN source_relations preload INDEXED BY idx_source_relations_container_relation
      ON preload.relation = 'assetBundle.containerPreload'
     AND json_extract(preload.raw_json, '$.details.container') = selected.container_path
    JOIN source_objects renderer
      ON renderer.serialized_file = preload.to_file
     AND renderer.path_id = preload.to_path_id
     AND renderer.type = 'SkinnedMeshRenderer'
    LEFT JOIN source_relations material INDEXED BY idx_source_relations_from
      ON material.from_file = renderer.serialized_file
     AND material.from_path_id = renderer.path_id
     AND material.relation = 'renderer.material'
    LEFT JOIN source_objects resolvedMaterial
      ON resolvedMaterial.serialized_file = material.to_file
     AND resolvedMaterial.path_id = material.to_path_id
     AND resolvedMaterial.type = 'Material'
    LEFT JOIN source_relations textureRel INDEXED BY idx_source_relations_from
      ON textureRel.from_file = resolvedMaterial.serialized_file
     AND textureRel.from_path_id = resolvedMaterial.path_id
     AND textureRel.relation = 'material.texture'
    GROUP BY selected.container_path
)
SELECT root.name, root.source_path, root.serialized_file, root.path_id, root.container_path,
       COALESCE(counts.animator_count, 0) AS animator_count,
       COALESCE(counts.renderer_count, 0) AS renderer_count,
       COALESCE(counts.mesh_count, 0) AS mesh_count,
       COALESCE(materials.material_count, 0) AS material_count,
       COALESCE(materials.resolved_material_count, 0) AS resolved_material_count,
       COALESCE(materials.textured_material_count, 0) AS textured_material_count,
       COALESCE(materials.material_texture_ref_count, 0) AS material_texture_ref_count,
       COALESCE(counts.avatar_count, 0) AS avatar_count,
       COALESCE(counts.controller_count, 0) AS controller_count,
       COALESCE(counts.optimized_animator_without_avatar_count, 0) AS optimized_animator_without_avatar_count
FROM container_roots root
LEFT JOIN preload_counts counts
  ON counts.container_path = root.container_path
LEFT JOIN renderer_material_counts materials
  ON materials.container_path = root.container_path
WHERE COALESCE(counts.renderer_count, 0) > 0
   OR COALESCE(counts.animator_count, 0) > 0
   OR COALESCE(counts.mesh_count, 0) > 0
ORDER BY counts.renderer_count DESC, counts.mesh_count DESC, counts.animator_count DESC, root.name COLLATE NOCASE
LIMIT $limit;";
            command.CommandText = command.CommandText.Replace("__PATH_CONTAINER_CTE__", BuildPathContainerCte(includePathContainerScan), StringComparison.Ordinal);
            command.CommandText = ApplyOptionalIndexHints(command.CommandText, availableIndexes);
            command.Parameters.AddWithValue("$exactName", selectorQuery.ExactName ?? string.Empty);
            command.Parameters.AddWithValue("$exactPathId", selectorQuery.ExactPathId);
            command.Parameters.AddWithValue("$includePathContainers", includePathContainerScan ? 1 : 0);
            command.Parameters.AddWithValue("$containerLimit", Math.Max(limit * 4, 20));
            command.Parameters.AddWithValue("$limit", limit);
            var result = new List<CandidateRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new CandidateRow(
                    "ContainerPrimaryModel",
                    ReadString(reader, 0),
                    ReadString(reader, 1),
                    ReadString(reader, 2),
                    ReadLong(reader, 3),
                    ReadString(reader, 4),
                    ReadLong(reader, 5),
                    ReadLong(reader, 6),
                    ReadLong(reader, 7),
                    ReadLong(reader, 8),
                    ReadLong(reader, 9),
                    ReadLong(reader, 10),
                    ReadLong(reader, 11),
                    ReadLong(reader, 12),
                    ReadLong(reader, 13),
                    0,
                    ReadLong(reader, 14),
                    string.Empty,
                    string.Empty,
                    0));
            }

            return result;
        }

        private static string BuildPathContainerCte(bool includePathContainerScan)
        {
            if (!includePathContainerScan)
            {
                return "path_containers AS (SELECT NULL AS container_path WHERE 0)";
            }

            return @"
path_containers AS (
    SELECT DISTINCT json_extract(rel.raw_json, '$.details.container') AS container_path
    FROM source_relations rel INDEXED BY idx_source_relations_relation
    WHERE rel.relation IN ('assetBundle.containerAsset', 'assetBundle.containerPreload', 'resourceManager.container')
      AND COALESCE(json_extract(rel.raw_json, '$.details.container'), '') LIKE '%' || $exactName || '%'
    LIMIT $containerLimit
)";
        }

        private static List<CandidateRow> LoadRawContainerMeshCandidates(SqliteConnection connection, int limit)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
WITH raw_container AS (
    SELECT json_extract(rel.raw_json, '$.details.container') AS container_path,
           obj.source_path,
           obj.serialized_file,
           obj.path_id,
           obj.type,
           obj.name
    FROM source_relations rel
    JOIN source_objects obj
      ON obj.serialized_file = rel.to_file
     AND obj.path_id = rel.to_path_id
    WHERE rel.relation IN ('assetBundle.containerAsset', 'assetBundle.containerPreload', 'resourceManager.container')
      AND COALESCE(json_extract(rel.raw_json, '$.details.container'), '') <> ''
      AND obj.type IN ('Mesh', 'Avatar')
),
raw_group AS (
    SELECT container_path,
           MIN(source_path) AS source_path,
           MIN(serialized_file) AS serialized_file,
           MIN(path_id) AS path_id,
           COUNT(DISTINCT CASE WHEN type = 'Mesh' THEN serialized_file || ':' || path_id END) AS mesh_count,
           COUNT(DISTINCT CASE WHEN type = 'Avatar' THEN serialized_file || ':' || path_id END) AS avatar_count,
           GROUP_CONCAT(DISTINCT name) AS sample_names
    FROM raw_container
    GROUP BY container_path
)
SELECT COALESCE(NULLIF(substr(container_path, length(rtrim(container_path, replace(container_path, '/', ''))) + 1), ''), container_path) AS name,
       source_path,
       serialized_file,
       path_id,
       container_path,
       mesh_count,
       avatar_count,
       COALESCE(sample_names, '') AS sample_names
FROM raw_group
WHERE mesh_count > 0
ORDER BY avatar_count DESC, mesh_count DESC, container_path COLLATE NOCASE
LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", limit);
            var result = new List<CandidateRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new CandidateRow(
                    "RawContainerModel",
                    ReadString(reader, 0),
                    ReadString(reader, 1),
                    ReadString(reader, 2),
                    ReadLong(reader, 3),
                    ReadString(reader, 4),
                    0,
                    0,
                    ReadLong(reader, 5),
                    0,
                    0,
                    0,
                    0,
                    ReadLong(reader, 6),
                    0,
                    0,
                    0,
                    string.Empty,
                    ReadString(reader, 7),
                    0));
            }

            return result;
        }

        private static List<CandidateRow> LoadTargetedRawContainerMeshCandidates(SqliteConnection connection, int limit, SelectorQuery selectorQuery, bool includePathContainerScan, HashSet<string> availableIndexes)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
WITH matched_containers AS (
    SELECT DISTINCT json_extract(rel.raw_json, '$.details.container') AS container_path
    FROM source_objects obj
    JOIN source_relations rel INDEXED BY idx_source_relations_to
      ON rel.to_file = obj.serialized_file
     AND rel.to_path_id = obj.path_id
     AND rel.relation IN ('assetBundle.containerAsset', 'assetBundle.containerPreload', 'resourceManager.container')
    WHERE COALESCE(json_extract(rel.raw_json, '$.details.container'), '') <> ''
      AND (($exactPathId != 0 AND obj.path_id = $exactPathId)
           OR ($exactPathId = 0 AND obj.name = $exactName))
    LIMIT $containerLimit
),
__PATH_CONTAINER_CTE__,
selected_containers AS (
    SELECT container_path FROM matched_containers
    UNION
    SELECT container_path FROM path_containers
),
raw_container AS (
    SELECT selected.container_path,
           obj.source_path,
           obj.serialized_file,
           obj.path_id,
           obj.type,
           obj.name
    FROM selected_containers selected
    JOIN source_relations rel INDEXED BY idx_source_relations_container_relation
      ON rel.relation IN ('assetBundle.containerAsset', 'assetBundle.containerPreload', 'resourceManager.container')
     AND json_extract(rel.raw_json, '$.details.container') = selected.container_path
    JOIN source_objects obj
      ON obj.serialized_file = rel.to_file
     AND obj.path_id = rel.to_path_id
    WHERE obj.type IN ('Mesh', 'Avatar')
),
raw_group AS (
    SELECT container_path,
           MIN(source_path) AS source_path,
           MIN(serialized_file) AS serialized_file,
           MIN(path_id) AS path_id,
           COUNT(DISTINCT CASE WHEN type = 'Mesh' THEN serialized_file || ':' || path_id END) AS mesh_count,
           COUNT(DISTINCT CASE WHEN type = 'Avatar' THEN serialized_file || ':' || path_id END) AS avatar_count,
           GROUP_CONCAT(DISTINCT name) AS sample_names
    FROM raw_container
    GROUP BY container_path
)
SELECT COALESCE(NULLIF(substr(container_path, length(rtrim(container_path, replace(container_path, '/', ''))) + 1), ''), container_path) AS name,
       source_path,
       serialized_file,
       path_id,
       container_path,
       mesh_count,
       avatar_count,
       COALESCE(sample_names, '') AS sample_names
FROM raw_group
WHERE mesh_count > 0
ORDER BY avatar_count DESC, mesh_count DESC, container_path COLLATE NOCASE
LIMIT $limit;";
            command.CommandText = command.CommandText.Replace("__PATH_CONTAINER_CTE__", BuildPathContainerCte(includePathContainerScan), StringComparison.Ordinal);
            command.CommandText = ApplyOptionalIndexHints(command.CommandText, availableIndexes);
            command.Parameters.AddWithValue("$exactName", selectorQuery.ExactName ?? string.Empty);
            command.Parameters.AddWithValue("$exactPathId", selectorQuery.ExactPathId);
            command.Parameters.AddWithValue("$includePathContainers", includePathContainerScan ? 1 : 0);
            command.Parameters.AddWithValue("$containerLimit", Math.Max(limit * 4, 20));
            command.Parameters.AddWithValue("$limit", limit);
            var result = new List<CandidateRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new CandidateRow(
                    "RawContainerModel",
                    ReadString(reader, 0),
                    ReadString(reader, 1),
                    ReadString(reader, 2),
                    ReadLong(reader, 3),
                    ReadString(reader, 4),
                    0,
                    0,
                    ReadLong(reader, 5),
                    0,
                    0,
                    0,
                    0,
                    ReadLong(reader, 6),
                    0,
                    0,
                    0,
                    string.Empty,
                    ReadString(reader, 7),
                    0));
            }

            return result;
        }

        private static List<CandidateRow> LoadTexturedRendererContainerCandidates(SqliteConnection connection, int limit, HashSet<string> availableIndexes)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
WITH textured_material AS (
    SELECT DISTINCT from_file AS material_file,
           from_path_id AS material_path
    FROM source_relations INDEXED BY idx_source_relations_relation
    WHERE relation = 'material.texture'
),
textured_renderer AS (
    SELECT DISTINCT material.from_file AS renderer_file,
           material.from_path_id AS renderer_path,
           material.to_file AS material_file,
           material.to_path_id AS material_path
    FROM source_relations material INDEXED BY idx_source_relations_relation
    JOIN textured_material textured
      ON textured.material_file = material.to_file
     AND textured.material_path = material.to_path_id
    WHERE material.relation = 'renderer.material'
),
renderer_go AS (
    SELECT textured.renderer_file,
           textured.renderer_path,
           textured.material_file,
           textured.material_path,
           renderer.type AS renderer_type,
           go.name AS go_name,
           go.source_path AS go_source,
           go.serialized_file AS go_file,
           go.path_id AS go_path
    FROM textured_renderer textured
    JOIN source_objects renderer
      ON renderer.serialized_file = textured.renderer_file
     AND renderer.path_id = textured.renderer_path
    LEFT JOIN source_relations goRel INDEXED BY idx_source_relations_from
      ON goRel.from_file = textured.renderer_file
     AND goRel.from_path_id = textured.renderer_path
     AND goRel.relation = 'component.gameObject'
    LEFT JOIN source_objects go
      ON go.serialized_file = goRel.to_file
     AND go.path_id = goRel.to_path_id
    WHERE renderer.type IN ('SkinnedMeshRenderer', 'MeshRenderer')
),
renderer_container AS (
    SELECT renderer_go.*,
           json_extract(container.raw_json, '$.details.container') AS container_path
    FROM renderer_go
    LEFT JOIN source_relations container INDEXED BY idx_source_relations_to
      ON container.to_file = renderer_go.renderer_file
     AND container.to_path_id = renderer_go.renderer_path
     AND container.relation = 'assetBundle.containerPreload'
)
SELECT COALESCE(container_path, '') AS container_path,
       COALESCE(NULLIF(MIN(go_name), ''), COALESCE(container_path, 'TexturedRendererContainer')) AS name,
       COALESCE(NULLIF(MIN(go_source), ''), COALESCE(container_path, '')) AS source_path,
       COALESCE(MIN(go_file), MIN(renderer_file), '') AS serialized_file,
       COALESCE(MIN(go_path), MIN(renderer_path), 0) AS path_id,
       COUNT(DISTINCT renderer_file || ':' || renderer_path) AS renderer_count,
       COUNT(DISTINCT CASE WHEN renderer_type = 'SkinnedMeshRenderer' THEN renderer_file || ':' || renderer_path END) AS skinned_renderer_count,
       COUNT(DISTINCT CASE WHEN renderer_type = 'MeshRenderer' THEN renderer_file || ':' || renderer_path END) AS mesh_renderer_count,
       COUNT(DISTINCT material_file || ':' || material_path) AS textured_material_count,
       COUNT(DISTINCT textureRel.to_file || ':' || textureRel.to_path_id) AS material_texture_ref_count,
       GROUP_CONCAT(DISTINCT go_name) AS sample_names
FROM renderer_container
LEFT JOIN source_relations textureRel INDEXED BY idx_source_relations_from
  ON textureRel.from_file = renderer_container.material_file
 AND textureRel.from_path_id = renderer_container.material_path
 AND textureRel.relation = 'material.texture'
GROUP BY COALESCE(container_path, '')
HAVING renderer_count > 0
ORDER BY skinned_renderer_count DESC,
         renderer_count DESC,
         textured_material_count DESC,
         material_texture_ref_count DESC,
         container_path COLLATE NOCASE
LIMIT $limit;";
            command.CommandText = ApplyOptionalIndexHints(command.CommandText, availableIndexes);
            command.Parameters.AddWithValue("$limit", limit);
            var result = new List<CandidateRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var rendererCount = ReadLong(reader, 5);
                var texturedMaterialCount = ReadLong(reader, 8);
                var textureRefCount = ReadLong(reader, 9);
                result.Add(new CandidateRow(
                    "TexturedRendererContainer",
                    ReadString(reader, 1),
                    ReadString(reader, 2),
                    ReadString(reader, 3),
                    ReadLong(reader, 4),
                    ReadString(reader, 0),
                    0,
                    rendererCount,
                    rendererCount,
                    texturedMaterialCount,
                    texturedMaterialCount,
                    texturedMaterialCount,
                    textureRefCount,
                    0,
                    0,
                    0,
                    0,
                    string.Empty,
                    ReadString(reader, 10),
                    0));
            }

            return result;
        }

        private static List<CandidateRow> LoadSkinnedRendererCandidates(SqliteConnection connection, int limit, HashSet<string> availableIndexes)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT go.name, go.source_path, go.serialized_file, go.path_id,
       '' AS container_path,
       COUNT(DISTINCT renderer.path_id) AS renderer_count,
       COUNT(DISTINCT mesh.to_file || ':' || mesh.to_path_id) AS mesh_count,
       COUNT(DISTINCT material.to_file || ':' || material.to_path_id) AS material_count,
       COUNT(DISTINCT resolvedMaterial.serialized_file || ':' || resolvedMaterial.path_id) AS resolved_material_count,
       COUNT(DISTINCT CASE
         WHEN textureRel.to_path_id IS NOT NULL
         THEN resolvedMaterial.serialized_file || ':' || resolvedMaterial.path_id END) AS textured_material_count,
       COUNT(DISTINCT CASE
         WHEN textureRel.to_path_id IS NOT NULL
         THEN textureRel.to_file || ':' || textureRel.to_path_id END) AS material_texture_ref_count,
       COUNT(DISTINCT rootBone.to_file || ':' || rootBone.to_path_id) AS root_bone_count
FROM source_objects renderer
JOIN source_relations goRel INDEXED BY idx_source_relations_from
  ON goRel.from_file = renderer.serialized_file
 AND goRel.from_path_id = renderer.path_id
 AND goRel.relation = 'component.gameObject'
JOIN source_objects go
  ON go.serialized_file = goRel.to_file
 AND go.path_id = goRel.to_path_id
LEFT JOIN source_relations mesh INDEXED BY idx_source_relations_from
  ON mesh.from_file = renderer.serialized_file
 AND mesh.from_path_id = renderer.path_id
 AND mesh.relation = 'skinnedMeshRenderer.mesh'
LEFT JOIN source_relations material INDEXED BY idx_source_relations_from
  ON material.from_file = renderer.serialized_file
 AND material.from_path_id = renderer.path_id
 AND material.relation = 'renderer.material'
LEFT JOIN source_objects resolvedMaterial
  ON resolvedMaterial.serialized_file = material.to_file
 AND resolvedMaterial.path_id = material.to_path_id
 AND resolvedMaterial.type = 'Material'
LEFT JOIN source_relations textureRel INDEXED BY idx_source_relations_from
  ON textureRel.from_file = resolvedMaterial.serialized_file
 AND textureRel.from_path_id = resolvedMaterial.path_id
 AND textureRel.relation = 'material.texture'
LEFT JOIN source_relations rootBone INDEXED BY idx_source_relations_from
  ON rootBone.from_file = renderer.serialized_file
 AND rootBone.from_path_id = renderer.path_id
 AND rootBone.relation = 'skinnedMeshRenderer.rootBone'
WHERE renderer.type = 'SkinnedMeshRenderer'
GROUP BY go.serialized_file, go.path_id
HAVING mesh_count > 0
ORDER BY material_count DESC, renderer_count DESC, go.name COLLATE NOCASE
LIMIT $limit;";
            command.CommandText = ApplyOptionalIndexHints(command.CommandText, availableIndexes);
            command.Parameters.AddWithValue("$limit", limit);
            var result = new List<CandidateRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new CandidateRow(
                    "SkinnedRendererModel",
                    ReadString(reader, 0),
                    ReadString(reader, 1),
                    ReadString(reader, 2),
                    ReadLong(reader, 3),
                    ReadString(reader, 4),
                    0,
                    ReadLong(reader, 5),
                    ReadLong(reader, 6),
                    ReadLong(reader, 7),
                    ReadLong(reader, 8),
                    ReadLong(reader, 9),
                    ReadLong(reader, 10),
                    0,
                    0,
                    ReadLong(reader, 11),
                    0,
                    string.Empty,
                    string.Empty,
                    0));
            }

            return result;
        }

        private static List<CandidateRow> LoadTargetedSkinnedRendererCandidates(SqliteConnection connection, int limit, SelectorQuery selectorQuery, HashSet<string> availableIndexes)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
WITH matched_go AS (
    SELECT go.name, go.source_path, go.serialized_file, go.path_id
    FROM source_objects go
    WHERE go.type = 'GameObject'
      AND (($exactPathId != 0 AND go.path_id = $exactPathId)
           OR ($exactPathId = 0 AND go.name = $exactName))
    LIMIT $limit
)
SELECT go.name, go.source_path, go.serialized_file, go.path_id,
       '' AS container_path,
       COUNT(DISTINCT renderer.path_id) AS renderer_count,
       COUNT(DISTINCT mesh.to_file || ':' || mesh.to_path_id) AS mesh_count,
       COUNT(DISTINCT material.to_file || ':' || material.to_path_id) AS material_count,
       COUNT(DISTINCT resolvedMaterial.serialized_file || ':' || resolvedMaterial.path_id) AS resolved_material_count,
       COUNT(DISTINCT CASE
         WHEN textureRel.to_path_id IS NOT NULL
         THEN resolvedMaterial.serialized_file || ':' || resolvedMaterial.path_id END) AS textured_material_count,
       COUNT(DISTINCT CASE
         WHEN textureRel.to_path_id IS NOT NULL
         THEN textureRel.to_file || ':' || textureRel.to_path_id END) AS material_texture_ref_count,
       COUNT(DISTINCT rootBone.to_file || ':' || rootBone.to_path_id) AS root_bone_count
FROM matched_go go
JOIN source_relations goRel INDEXED BY idx_source_relations_to
  ON goRel.to_file = go.serialized_file
 AND goRel.to_path_id = go.path_id
 AND goRel.relation = 'component.gameObject'
JOIN source_objects renderer
  ON renderer.serialized_file = goRel.from_file
 AND renderer.path_id = goRel.from_path_id
 AND renderer.type = 'SkinnedMeshRenderer'
LEFT JOIN source_relations mesh INDEXED BY idx_source_relations_from
  ON mesh.from_file = renderer.serialized_file
 AND mesh.from_path_id = renderer.path_id
 AND mesh.relation = 'skinnedMeshRenderer.mesh'
LEFT JOIN source_relations material INDEXED BY idx_source_relations_from
  ON material.from_file = renderer.serialized_file
 AND material.from_path_id = renderer.path_id
 AND material.relation = 'renderer.material'
LEFT JOIN source_objects resolvedMaterial
  ON resolvedMaterial.serialized_file = material.to_file
 AND resolvedMaterial.path_id = material.to_path_id
 AND resolvedMaterial.type = 'Material'
LEFT JOIN source_relations textureRel INDEXED BY idx_source_relations_from
  ON textureRel.from_file = resolvedMaterial.serialized_file
 AND textureRel.from_path_id = resolvedMaterial.path_id
 AND textureRel.relation = 'material.texture'
LEFT JOIN source_relations rootBone INDEXED BY idx_source_relations_from
  ON rootBone.from_file = renderer.serialized_file
 AND rootBone.from_path_id = renderer.path_id
 AND rootBone.relation = 'skinnedMeshRenderer.rootBone'
GROUP BY go.serialized_file, go.path_id
HAVING mesh_count > 0
ORDER BY material_count DESC, renderer_count DESC, go.name COLLATE NOCASE
LIMIT $limit;";
            command.CommandText = ApplyOptionalIndexHints(command.CommandText, availableIndexes);
            command.Parameters.AddWithValue("$exactName", selectorQuery.ExactName ?? string.Empty);
            command.Parameters.AddWithValue("$exactPathId", selectorQuery.ExactPathId);
            command.Parameters.AddWithValue("$limit", limit);
            var result = new List<CandidateRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new CandidateRow(
                    "SkinnedRendererModel",
                    ReadString(reader, 0),
                    ReadString(reader, 1),
                    ReadString(reader, 2),
                    ReadLong(reader, 3),
                    ReadString(reader, 4),
                    0,
                    ReadLong(reader, 5),
                    ReadLong(reader, 6),
                    ReadLong(reader, 7),
                    ReadLong(reader, 8),
                    ReadLong(reader, 9),
                    ReadLong(reader, 10),
                    0,
                    0,
                    ReadLong(reader, 11),
                    0,
                    string.Empty,
                    string.Empty,
                    0));
            }

            return result;
        }

        private static List<CandidateRow> LoadTargetedSkinnedRendererHierarchyCandidates(SqliteConnection connection, int limit, SelectorQuery selectorQuery, HashSet<string> availableIndexes)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
WITH RECURSIVE matched_go AS (
    SELECT go.name, go.source_path, go.serialized_file, go.path_id
    FROM source_objects go
    WHERE go.type = 'GameObject'
      AND (($exactPathId != 0 AND go.path_id = $exactPathId)
           OR ($exactPathId = 0 AND go.name = $exactName))
    LIMIT $rootLimit
),
root_transform AS (
    SELECT go.name AS root_name,
           go.source_path AS root_source_path,
           go.serialized_file AS root_file,
           go.path_id AS root_path_id,
           tr.serialized_file AS transform_file,
           tr.path_id AS transform_path_id
    FROM matched_go go
    JOIN source_relations comp INDEXED BY idx_source_relations_to
      ON comp.to_file = go.serialized_file
     AND comp.to_path_id = go.path_id
     AND comp.relation = 'component.gameObject'
    JOIN source_objects tr
      ON tr.serialized_file = comp.from_file
     AND tr.path_id = comp.from_path_id
     AND tr.type = 'Transform'
),
tree(root_name, root_source_path, root_file, root_path_id, transform_file, transform_path_id, depth) AS (
    SELECT root_name, root_source_path, root_file, root_path_id, transform_file, transform_path_id, 0
    FROM root_transform
    UNION ALL
    SELECT tree.root_name,
           tree.root_source_path,
           tree.root_file,
           tree.root_path_id,
           child.to_file,
           child.to_path_id,
           tree.depth + 1
    FROM tree
    JOIN source_relations child INDEXED BY idx_source_relations_from
      ON child.from_file = tree.transform_file
     AND child.from_path_id = tree.transform_path_id
     AND child.relation = 'transform.child'
    WHERE tree.depth < 64
),
child_go AS (
    SELECT tree.root_name,
           tree.root_source_path,
           tree.root_file,
           tree.root_path_id,
           go.serialized_file AS go_file,
           go.path_id AS go_path_id
    FROM tree
    JOIN source_relations comp INDEXED BY idx_source_relations_from
      ON comp.from_file = tree.transform_file
     AND comp.from_path_id = tree.transform_path_id
     AND comp.relation = 'component.gameObject'
    JOIN source_objects go
      ON go.serialized_file = comp.to_file
     AND go.path_id = comp.to_path_id
     AND go.type = 'GameObject'
)
SELECT child_go.root_name,
       child_go.root_source_path,
       child_go.root_file,
       child_go.root_path_id,
       '' AS container_path,
       COUNT(DISTINCT renderer.serialized_file || ':' || renderer.path_id) AS renderer_count,
       COUNT(DISTINCT mesh.to_file || ':' || mesh.to_path_id) AS mesh_count,
       COUNT(DISTINCT material.to_file || ':' || material.to_path_id) AS material_count,
       COUNT(DISTINCT resolvedMaterial.serialized_file || ':' || resolvedMaterial.path_id) AS resolved_material_count,
       COUNT(DISTINCT CASE
         WHEN textureRel.to_path_id IS NOT NULL
         THEN resolvedMaterial.serialized_file || ':' || resolvedMaterial.path_id END) AS textured_material_count,
       COUNT(DISTINCT CASE
         WHEN textureRel.to_path_id IS NOT NULL
         THEN textureRel.to_file || ':' || textureRel.to_path_id END) AS material_texture_ref_count,
       COUNT(DISTINCT rootBone.to_file || ':' || rootBone.to_path_id) AS root_bone_count
FROM child_go
JOIN source_relations rgo INDEXED BY idx_source_relations_to
  ON rgo.to_file = child_go.go_file
 AND rgo.to_path_id = child_go.go_path_id
 AND rgo.relation = 'component.gameObject'
JOIN source_objects renderer
  ON renderer.serialized_file = rgo.from_file
 AND renderer.path_id = rgo.from_path_id
 AND renderer.type = 'SkinnedMeshRenderer'
LEFT JOIN source_relations mesh INDEXED BY idx_source_relations_from
  ON mesh.from_file = renderer.serialized_file
 AND mesh.from_path_id = renderer.path_id
 AND mesh.relation = 'skinnedMeshRenderer.mesh'
LEFT JOIN source_relations material INDEXED BY idx_source_relations_from
  ON material.from_file = renderer.serialized_file
 AND material.from_path_id = renderer.path_id
 AND material.relation = 'renderer.material'
LEFT JOIN source_objects resolvedMaterial
  ON resolvedMaterial.serialized_file = material.to_file
 AND resolvedMaterial.path_id = material.to_path_id
 AND resolvedMaterial.type = 'Material'
LEFT JOIN source_relations textureRel INDEXED BY idx_source_relations_from
  ON textureRel.from_file = resolvedMaterial.serialized_file
 AND textureRel.from_path_id = resolvedMaterial.path_id
 AND textureRel.relation = 'material.texture'
LEFT JOIN source_relations rootBone INDEXED BY idx_source_relations_from
  ON rootBone.from_file = renderer.serialized_file
 AND rootBone.from_path_id = renderer.path_id
 AND rootBone.relation = 'skinnedMeshRenderer.rootBone'
GROUP BY child_go.root_file, child_go.root_path_id
HAVING mesh_count > 0
ORDER BY material_count DESC, renderer_count DESC, child_go.root_name COLLATE NOCASE
LIMIT $limit;";
            command.CommandText = ApplyOptionalIndexHints(command.CommandText, availableIndexes);
            command.Parameters.AddWithValue("$exactName", selectorQuery.ExactName ?? string.Empty);
            command.Parameters.AddWithValue("$exactPathId", selectorQuery.ExactPathId);
            command.Parameters.AddWithValue("$rootLimit", 1);
            command.Parameters.AddWithValue("$limit", limit);
            var result = new List<CandidateRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new CandidateRow(
                    "SkinnedRendererHierarchyModel",
                    ReadString(reader, 0),
                    ReadString(reader, 1),
                    ReadString(reader, 2),
                    ReadLong(reader, 3),
                    ReadString(reader, 4),
                    0,
                    ReadLong(reader, 5),
                    ReadLong(reader, 6),
                    ReadLong(reader, 7),
                    ReadLong(reader, 8),
                    ReadLong(reader, 9),
                    ReadLong(reader, 10),
                    0,
                    0,
                    ReadLong(reader, 11),
                    0,
                    string.Empty,
                    string.Empty,
                    0));
            }

            return result;
        }

        private static List<CandidateRow> LoadStaticRendererCandidates(SqliteConnection connection, int limit, HashSet<string> availableIndexes)
        {
            var rendererRows = LoadStaticRendererGameObjects(connection, limit, availableIndexes);
            if (rendererRows.Count == 0)
            {
                return new List<CandidateRow>();
            }

            var meshCountsByGameObject = LoadMeshFilterMeshCountsByGameObject(connection, availableIndexes);
            var materialStatsByRenderer = LoadRendererMaterialStats(connection, availableIndexes);
            var byGameObject = new Dictionary<string, StaticCandidateAccumulator>(StringComparer.Ordinal);
            foreach (var renderer in rendererRows)
            {
                var gameObjectKey = BuildObjectKey(renderer.GameObjectFile, renderer.GameObjectPathId);
                if (!meshCountsByGameObject.TryGetValue(gameObjectKey, out var meshCount) || meshCount <= 0)
                {
                    continue;
                }

                if (!byGameObject.TryGetValue(gameObjectKey, out var accumulator))
                {
                    accumulator = new StaticCandidateAccumulator(
                        renderer.GameObjectName,
                        renderer.GameObjectSourcePath,
                        renderer.GameObjectFile,
                        renderer.GameObjectPathId,
                        meshCount);
                    byGameObject.Add(gameObjectKey, accumulator);
                }

                accumulator.RendererCount++;
                if (materialStatsByRenderer.TryGetValue(BuildObjectKey(renderer.RendererFile, renderer.RendererPathId), out var materialStats))
                {
                    accumulator.MaterialCount += materialStats.MaterialCount;
                    accumulator.ResolvedMaterialCount += materialStats.ResolvedMaterialCount;
                    accumulator.TexturedMaterialCount += materialStats.TexturedMaterialCount;
                    accumulator.MaterialTextureRefCount += materialStats.MaterialTextureRefCount;
                }
            }

            return byGameObject.Values
                .OrderByDescending(x => x.MaterialCount)
                .ThenByDescending(x => x.RendererCount)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(x => new CandidateRow(
                    "StaticRendererModel",
                    x.Name,
                    x.SourcePath,
                    x.SerializedFile,
                    x.PathId,
                    string.Empty,
                    0,
                    x.RendererCount,
                    x.MeshCount,
                    x.MaterialCount,
                    x.ResolvedMaterialCount,
                    x.TexturedMaterialCount,
                    x.MaterialTextureRefCount,
                    0,
                    0,
                    0,
                    0,
                    string.Empty,
                    string.Empty,
                    0))
                .ToList();
        }

        private static List<StaticRendererGameObjectRow> LoadStaticRendererGameObjects(SqliteConnection connection, int limit, HashSet<string> availableIndexes)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT renderer.serialized_file,
       renderer.path_id,
       go.serialized_file,
       go.path_id,
       go.name,
       go.source_path
FROM source_objects renderer
JOIN source_relations goRel INDEXED BY idx_source_relations_from
  ON goRel.from_file = renderer.serialized_file
 AND goRel.from_path_id = renderer.path_id
 AND goRel.relation = 'component.gameObject'
JOIN source_objects go
  ON go.serialized_file = goRel.to_file
 AND go.path_id = goRel.to_path_id
WHERE renderer.type = 'MeshRenderer'
ORDER BY go.name COLLATE NOCASE, go.source_path COLLATE NOCASE
LIMIT $limit;";
            command.CommandText = ApplyOptionalIndexHints(command.CommandText, availableIndexes);
            command.Parameters.AddWithValue("$limit", limit);
            var result = new List<StaticRendererGameObjectRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new StaticRendererGameObjectRow(
                    ReadString(reader, 0),
                    ReadLong(reader, 1),
                    ReadString(reader, 2),
                    ReadLong(reader, 3),
                    ReadString(reader, 4),
                    ReadString(reader, 5)));
            }

            return result;
        }

        private static Dictionary<string, long> LoadMeshFilterMeshCountsByGameObject(SqliteConnection connection, HashSet<string> availableIndexes)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT goRel.to_file,
       goRel.to_path_id,
       COUNT(DISTINCT mesh.to_file || ':' || mesh.to_path_id) AS mesh_count
FROM source_objects meshFilter
JOIN source_relations goRel INDEXED BY idx_source_relations_from
  ON goRel.from_file = meshFilter.serialized_file
 AND goRel.from_path_id = meshFilter.path_id
 AND goRel.relation = 'component.gameObject'
JOIN source_relations mesh INDEXED BY idx_source_relations_from
  ON mesh.from_file = meshFilter.serialized_file
 AND mesh.from_path_id = meshFilter.path_id
 AND mesh.relation = 'meshFilter.mesh'
WHERE meshFilter.type = 'MeshFilter'
GROUP BY goRel.to_file, goRel.to_path_id;";
            command.CommandText = ApplyOptionalIndexHints(command.CommandText, availableIndexes);
            var result = new Dictionary<string, long>(StringComparer.Ordinal);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result[BuildObjectKey(ReadString(reader, 0), ReadLong(reader, 1))] = ReadLong(reader, 2);
            }

            return result;
        }

        private static Dictionary<string, StaticRendererMaterialStats> LoadRendererMaterialStats(SqliteConnection connection, HashSet<string> availableIndexes)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT material.from_file,
       material.from_path_id,
       COUNT(DISTINCT material.to_file || ':' || material.to_path_id) AS material_count,
       COUNT(DISTINCT resolvedMaterial.serialized_file || ':' || resolvedMaterial.path_id) AS resolved_material_count,
       COUNT(DISTINCT CASE
         WHEN textureRel.to_path_id IS NOT NULL
         THEN resolvedMaterial.serialized_file || ':' || resolvedMaterial.path_id END) AS textured_material_count,
       COUNT(DISTINCT CASE
         WHEN textureRel.to_path_id IS NOT NULL
         THEN textureRel.to_file || ':' || textureRel.to_path_id END) AS material_texture_ref_count
FROM source_relations material INDEXED BY idx_source_relations_relation
LEFT JOIN source_objects resolvedMaterial
  ON resolvedMaterial.serialized_file = material.to_file
 AND resolvedMaterial.path_id = material.to_path_id
 AND resolvedMaterial.type = 'Material'
LEFT JOIN source_relations textureRel INDEXED BY idx_source_relations_from
  ON textureRel.from_file = resolvedMaterial.serialized_file
 AND textureRel.from_path_id = resolvedMaterial.path_id
 AND textureRel.relation = 'material.texture'
WHERE material.relation = 'renderer.material'
GROUP BY material.from_file, material.from_path_id;";
            command.CommandText = ApplyOptionalIndexHints(command.CommandText, availableIndexes);
            var result = new Dictionary<string, StaticRendererMaterialStats>(StringComparer.Ordinal);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result[BuildObjectKey(ReadString(reader, 0), ReadLong(reader, 1))] = new StaticRendererMaterialStats(
                    ReadLong(reader, 2),
                    ReadLong(reader, 3),
                    ReadLong(reader, 4),
                    ReadLong(reader, 5));
            }

            return result;
        }

        private static long Score(CandidateRow row)
        {
            var score = 0L;
            score += row.ControllerCount * 100;
            score += row.AvatarCount * 60;
            score += row.MeshCount * 20;
            score += row.TexturedMaterialCount * 35;
            score += row.MaterialTextureRefCount * 8;
            score += row.ResolvedMaterialCount * 15;
            score += row.MaterialCount * 3;
            score += row.RendererCount * 5;
            score += row.RootBoneCount * 5;
            if (row.MeshCount <= 0 && row.RendererCount <= 0)
            {
                score -= 1000;
            }
            score -= row.OptimizedAnimatorWithoutAvatarCount * 80;
            if (row.Kind == "SkinnedRendererModel")
            {
                score += 30;
            }
            else if (row.Kind == "SkinnedRendererHierarchyModel")
            {
                score += 150;
            }
            else if (row.Kind == "AnimatorModel")
            {
                score += 120;
            }
            else if (row.Kind == "ContainerPrimaryModel")
            {
                score += 180;
            }
            else if (row.Kind == "RawContainerModel")
            {
                score += 40;
            }
            else if (row.Kind == "TexturedRendererContainer")
            {
                score += 70;
            }
            else if (row.Kind == "StaticRendererModel")
            {
                score += 10;
            }

            return score;
        }

        private static string BuildExcludeHint(CandidateRow row)
        {
            var text = string.Join("/", row.Kind, row.Name, row.SourcePath, row.SerializedFile, row.ContainerPath).ToLowerInvariant();
            var hits = new List<string>();
            AddIfContains(text, hits, "dialog");
            AddIfContains(text, hits, "timeline");
            AddIfContains(text, hits, "cutscene");
            AddIfContains(text, hits, "postmodel");
            AddIfContains(text, hits, "uimodel");
            AddIfContains(text, hits, "skeletalmorph");
            AddIfContains(text, hits, "gamedata");
            AddIfContains(text, hits, "abilityentity");
            AddIfContains(text, hits, "tmpobject");
            AddIfContains(text, hits, "_tmp");
            AddIfContains(text, hits, "/tmp");
            AddIfContains(text, hits, "att_widget");
            AddIfContains(text, hits, "widget");
            AddIfContains(text, hits, "cine");
            AddIfContains(text, hits, "_lod");
            AddIfContains(text, hits, "ui");
            AddIfContains(text, hits, "preview");
            AddIfContains(text, hits, "deco");
            AddIfContains(text, hits, "levelseq");
            AddIfContains(text, hits, "pose");
            AddIfContains(text, hits, "camera");
            if (text.Contains("initialassets/intro", StringComparison.OrdinalIgnoreCase)
                || text.Contains("/intro/", StringComparison.OrdinalIgnoreCase))
            {
                hits.Add("introOnly");
            }
            if (text.Contains("vfx", StringComparison.OrdinalIgnoreCase)
                || text.Contains("/fx", StringComparison.OrdinalIgnoreCase)
                || text.Contains("effects/", StringComparison.OrdinalIgnoreCase)
                || text.Contains("effect/", StringComparison.OrdinalIgnoreCase))
            {
                hits.Add("vfxOrEffect");
            }
            if (row.Kind == "TexturedRendererContainer")
            {
                hits.Add("containerLeadNeedsExportValidation");
            }
            if (row.Kind == "RawContainerModel")
            {
                hits.Add("sourcePart");
                hits.Add("rawModelNeedsPrefabOrRendererBinding");
            }
            if (row.Kind == "StaticRendererModel" && string.IsNullOrWhiteSpace(row.ContainerPath))
            {
                hits.Add("staticRendererNeedsContainerReview");
            }
            if (row.MeshCount <= 0 && row.RendererCount <= 0)
            {
                hits.Add("noVisibleMesh");
            }
            if (row.MaterialCount <= 0)
            {
                hits.Add("noMaterial");
            }
            if (row.MaterialCount > 0 && row.ResolvedMaterialCount == 0)
            {
                hits.Add("missingResolvedMaterial");
            }
            if (row.ResolvedMaterialCount > 0 && row.TexturedMaterialCount == 0)
            {
                hits.Add("missingMaterialTexture");
            }
            if (row.OptimizedAnimatorWithoutAvatarCount > 0)
            {
                hits.Add("optimizedAnimatorMissingAvatar");
            }
            return string.Join(",", hits.Distinct(StringComparer.OrdinalIgnoreCase));
        }

        private static List<CandidateRow> FillContainerPaths(SqliteConnection connection, List<CandidateRow> rows, HashSet<string> availableIndexes)
        {
            if (rows.Count == 0)
            {
                return rows;
            }

            // container/preload 只做模型优先筛选和来源诊断。先加载候选再逐条补，
            // 避免在 SkinnedRenderer 全库扫描时把庞大的 containerPreload 表 join 进去。
            var cache = new Dictionary<string, string>(StringComparer.Ordinal);
            var result = new List<CandidateRow>(rows.Count);
            foreach (var row in rows)
            {
                if (!string.IsNullOrWhiteSpace(row.ContainerPath))
                {
                    result.Add(row);
                    continue;
                }

                var key = (row.SerializedFile ?? string.Empty) + "\n" + row.PathId.ToString(CultureInfo.InvariantCulture);
                if (!cache.TryGetValue(key, out var containerPath))
                {
                    containerPath = LoadContainerPath(connection, row.SerializedFile, row.PathId, availableIndexes);
                    cache[key] = containerPath;
                }

                result.Add(row with { ContainerPath = containerPath });
            }

            return result;
        }

        private static string BuildCandidateDedupeKey(CandidateRow row)
        {
            return string.Join("\n",
                row.Kind ?? string.Empty,
                row.SerializedFile ?? string.Empty,
                row.PathId.ToString(CultureInfo.InvariantCulture),
                row.ContainerPath ?? string.Empty,
                row.Name ?? string.Empty);
        }

        private static string LoadContainerPath(SqliteConnection connection, string serializedFile, long pathId, HashSet<string> availableIndexes)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT COALESCE(GROUP_CONCAT(DISTINCT json_extract(raw_json, '$.details.container')), '')
FROM source_relations INDEXED BY idx_source_relations_to
WHERE to_file = $file
  AND to_path_id = $pathId
  AND relation IN ('assetBundle.containerAsset', 'assetBundle.containerPreload', 'resourceManager.container');";
            command.CommandText = ApplyOptionalIndexHints(command.CommandText, availableIndexes);
            command.Parameters.AddWithValue("$file", serializedFile ?? string.Empty);
            command.Parameters.AddWithValue("$pathId", pathId);
            return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static void AddIfContains(string text, List<string> hits, string token)
        {
            if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(token);
            }
        }

        private static SelectorQuery BuildSelectorQuery(string selector)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                return new SelectorQuery(false, "broad", string.Empty, string.Empty, 0);
            }

            var trimmed = selector.Trim();
            if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exactPathId) && exactPathId != 0)
            {
                return new SelectorQuery(true, "targeted_exact_path_id", trimmed, string.Empty, exactPathId);
            }

            var likeToken = ExtractLongestLiteralToken(selector);
            if (likeToken.Length < 4)
            {
                return new SelectorQuery(false, "broad", string.Empty, string.Empty, 0);
            }

            // 只有简单对象名才下推到 SQL 精确查找。复杂正则/路径继续用旧流程，
            // 避免 alternation、分组或路径筛选被单个名字误缩窄。
            var simpleLiteral = Regex.IsMatch(trimmed, @"^[A-Za-z0-9_.-]+$");
            return simpleLiteral
                ? new SelectorQuery(true, "targeted_exact_name", likeToken, trimmed, 0)
                : new SelectorQuery(false, "broad_regex", string.Empty, string.Empty, 0);
        }

        private static string ExtractLongestLiteralToken(string selector)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                return string.Empty;
            }

            var matches = Regex.Matches(selector, @"[A-Za-z0-9_]{4,}");
            return matches
                .Select(x => x.Value)
                .OrderByDescending(x => x.Length)
                .FirstOrDefault() ?? string.Empty;
        }

        private static bool MatchesSelector(string selector, CandidateRow row)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                return true;
            }

            if (long.TryParse(selector.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pathId))
            {
                return row.PathId == pathId;
            }

            return Regex.IsMatch(row.Name ?? string.Empty, selector, RegexOptions.IgnoreCase)
                || Regex.IsMatch(row.SourcePath ?? string.Empty, selector, RegexOptions.IgnoreCase)
                || Regex.IsMatch(row.SerializedFile ?? string.Empty, selector, RegexOptions.IgnoreCase)
                || Regex.IsMatch(row.ContainerPath ?? string.Empty, selector, RegexOptions.IgnoreCase)
                || Regex.IsMatch(row.SampleNames ?? string.Empty, selector, RegexOptions.IgnoreCase)
                || Regex.IsMatch(row.Kind ?? string.Empty, selector, RegexOptions.IgnoreCase);
        }

        private static JObject ToJson(CandidateRow row)
        {
            return new JObject
            {
                ["kind"] = row.Kind,
                ["name"] = row.Name,
                ["sourcePath"] = row.SourcePath,
                ["serializedFile"] = row.SerializedFile,
                ["containerPath"] = row.ContainerPath,
                ["pathId"] = row.PathId,
                ["score"] = row.Score,
                ["excludeHint"] = row.ExcludeHint,
                ["animatorCount"] = row.AnimatorCount,
                ["rendererCount"] = row.RendererCount,
                ["meshCount"] = row.MeshCount,
                ["materialCount"] = row.MaterialCount,
                ["resolvedMaterialCount"] = row.ResolvedMaterialCount,
                ["texturedMaterialCount"] = row.TexturedMaterialCount,
                ["materialTextureRefCount"] = row.MaterialTextureRefCount,
                ["optimizedAnimatorWithoutAvatarCount"] = row.OptimizedAnimatorWithoutAvatarCount,
                ["avatarCount"] = row.AvatarCount,
                ["controllerCount"] = row.ControllerCount,
                ["rootBoneCount"] = row.RootBoneCount,
                ["sampleNames"] = row.SampleNames,
            };
        }

        private static string ToCsv(List<CandidateRow> rows)
        {
            var builder = new StringBuilder();
            builder.AppendLine("kind,name,sourcePath,serializedFile,containerPath,pathId,score,excludeHint,animatorCount,rendererCount,meshCount,materialCount,resolvedMaterialCount,texturedMaterialCount,materialTextureRefCount,optimizedAnimatorWithoutAvatarCount,avatarCount,controllerCount,rootBoneCount,sampleNames");
            foreach (var row in rows)
            {
                builder.AppendLine(string.Join(",",
                    Csv(row.Kind),
                    Csv(row.Name),
                    Csv(row.SourcePath),
                    Csv(row.SerializedFile),
                    Csv(row.ContainerPath),
                    row.PathId.ToString(CultureInfo.InvariantCulture),
                    row.Score.ToString(CultureInfo.InvariantCulture),
                    Csv(row.ExcludeHint),
                    row.AnimatorCount.ToString(CultureInfo.InvariantCulture),
                    row.RendererCount.ToString(CultureInfo.InvariantCulture),
                    row.MeshCount.ToString(CultureInfo.InvariantCulture),
                    row.MaterialCount.ToString(CultureInfo.InvariantCulture),
                    row.ResolvedMaterialCount.ToString(CultureInfo.InvariantCulture),
                    row.TexturedMaterialCount.ToString(CultureInfo.InvariantCulture),
                    row.MaterialTextureRefCount.ToString(CultureInfo.InvariantCulture),
                    row.OptimizedAnimatorWithoutAvatarCount.ToString(CultureInfo.InvariantCulture),
                    row.AvatarCount.ToString(CultureInfo.InvariantCulture),
                    row.ControllerCount.ToString(CultureInfo.InvariantCulture),
                    row.RootBoneCount.ToString(CultureInfo.InvariantCulture),
                    Csv(row.SampleNames)));
            }

            return builder.ToString();
        }

        private static string Csv(string value)
        {
            value ??= string.Empty;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static string BuildObjectKey(string serializedFile, long pathId)
        {
            return (serializedFile ?? string.Empty) + "\n" + pathId.ToString(CultureInfo.InvariantCulture);
        }

        private static string ReadString(SqliteDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
        }

        private static long ReadLong(SqliteDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }

        private sealed record StaticRendererGameObjectRow(
            string RendererFile,
            long RendererPathId,
            string GameObjectFile,
            long GameObjectPathId,
            string GameObjectName,
            string GameObjectSourcePath);

        private sealed record StaticRendererMaterialStats(
            long MaterialCount,
            long ResolvedMaterialCount,
            long TexturedMaterialCount,
            long MaterialTextureRefCount);

        private sealed class StaticCandidateAccumulator
        {
            public StaticCandidateAccumulator(string name, string sourcePath, string serializedFile, long pathId, long meshCount)
            {
                Name = name;
                SourcePath = sourcePath;
                SerializedFile = serializedFile;
                PathId = pathId;
                MeshCount = meshCount;
            }

            public string Name { get; }
            public string SourcePath { get; }
            public string SerializedFile { get; }
            public long PathId { get; }
            public long MeshCount { get; }
            public long RendererCount { get; set; }
            public long MaterialCount { get; set; }
            public long ResolvedMaterialCount { get; set; }
            public long TexturedMaterialCount { get; set; }
            public long MaterialTextureRefCount { get; set; }
        }

        private sealed record SelectorQuery(
            bool IsTargeted,
            string Mode,
            string LikeToken,
            string ExactName,
            long ExactPathId);

        private sealed record CandidateRow(
            string Kind,
            string Name,
            string SourcePath,
            string SerializedFile,
            long PathId,
            string ContainerPath,
            long AnimatorCount,
            long RendererCount,
            long MeshCount,
            long MaterialCount,
            long ResolvedMaterialCount,
            long TexturedMaterialCount,
            long MaterialTextureRefCount,
            long AvatarCount,
            long ControllerCount,
            long RootBoneCount,
            long OptimizedAnimatorWithoutAvatarCount,
            string ExcludeHint,
            string SampleNames,
            long Score);
    }
}
