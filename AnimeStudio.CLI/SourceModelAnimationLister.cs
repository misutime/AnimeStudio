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
    internal static class SourceModelAnimationLister
    {
        private static bool warnedMissingContainerReverseIndex;

        public static string List(string sourceIndexPath, string outputDirectory, string modelSelector, string animationSelector, int limit)
        {
            if (string.IsNullOrWhiteSpace(sourceIndexPath) || !File.Exists(sourceIndexPath))
            {
                Logger.Error($"Unity source index not found: {sourceIndexPath}");
                return null;
            }

            if (string.IsNullOrWhiteSpace(modelSelector))
            {
                Logger.Error("--list_source_model_animations requires --preview_model. 这个入口只列已选模型的 Unity 显式动画关系，避免全库组合搜索。");
                return null;
            }

            limit = limit <= 0 ? 200 : limit;
            var outputRoot = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sourceIndexPath)) ?? Environment.CurrentDirectory, "SourceModelAnimations")
                : Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(outputRoot);

            SQLitePCL.Batteries_V2.Init();
            using var connection = new SqliteConnection($"Data Source={Path.GetFullPath(sourceIndexPath)};Mode=ReadOnly");
            connection.Open();
            var availableIndexes = LoadAvailableIndexNames(connection);
            WarnMissingPerformanceIndexes(availableIndexes);

            var modelScanLimit = Math.Clamp(limit, 20, 500);
            var preFilterLimit = BuildPreFilterLimit(limit, animationSelector);
            var rows = new List<CandidateRow>();
            var selectedModels = LoadModelsForAnimationQuery(connection, modelSelector, modelScanLimit);
            var hasReverseRelationIndex = availableIndexes.Contains("idx_source_relations_to");
            var canScanSharedAvatarForward =
                availableIndexes.Contains("idx_source_relations_from") &&
                availableIndexes.Contains("idx_source_relations_relation");
            foreach (var model in selectedModels)
            {
                rows.AddRange(LoadAnimatorControllerClips(connection, model));
                rows.AddRange(LoadLegacyAnimationClips(connection, model));
                if (hasReverseRelationIndex)
                {
                    rows.AddRange(LoadSharedAvatarControllerClips(connection, model, Math.Max(1, preFilterLimit - rows.Count)));
                }
                else if (canScanSharedAvatarForward)
                {
                    rows.AddRange(LoadSharedAvatarControllerClipsFromForwardConfig(connection, model, Math.Max(1, preFilterLimit - rows.Count)));
                }

                if (rows.Count >= preFilterLimit)
                {
                    break;
                }
            }

            rows = FillModelContainerPaths(connection, rows);

            var filtered = rows
                .Where(x => MatchesModelSelector(modelSelector, x))
                .Where(x => MatchesAnimationSelector(animationSelector, x))
                .Select(x => x with { ReviewHint = BuildReviewHint(x) })
                .GroupBy(x => string.Join("\n",
                    x.ModelSerializedFile ?? string.Empty,
                    x.ModelPathId.ToString(CultureInfo.InvariantCulture),
                    x.ControllerSerializedFile ?? string.Empty,
                    x.ControllerPathId.ToString(CultureInfo.InvariantCulture),
                    x.AnimationSerializedFile ?? string.Empty,
                    x.AnimationPathId.ToString(CultureInfo.InvariantCulture),
                    x.RelationKind ?? string.Empty), StringComparer.Ordinal)
                .Select(x => x.First())
                .OrderBy(x => string.IsNullOrWhiteSpace(x.ReviewHint) ? 0 : 1)
                .ThenBy(x => x.ModelName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.RelationKind, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.AnimationName, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();

            var models = filtered
                .GroupBy(x => x.ModelSerializedFile + "\n" + x.ModelPathId.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal)
                .Select(g => new ModelRow(
                    g.First().ModelName,
                    g.First().ModelSourcePath,
                    g.First().ModelSerializedFile,
                    g.First().ModelPathId,
                    g.First().ModelContainerPath))
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.SerializedFile, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var animatorDiagnostics = LoadAnimatorDiagnosticsForSelector(connection, modelSelector, Math.Min(limit, 80));
            var monoBehaviourPPtrDiagnostics = hasReverseRelationIndex
                ? LoadMonoBehaviourPPtrDiagnosticsForSelector(connection, modelSelector, Math.Min(limit, 80))
                : new List<MonoBehaviourPPtrDiagnosticRow>();
            if (!hasReverseRelationIndex)
            {
                if (canScanSharedAvatarForward)
                {
                    Logger.Warning("idx_source_relations_to is missing. Using conservative forward-config sharedAvatarController scan; MonoBehaviour reverse diagnostics remain skipped.");
                }
                else
                {
                    Logger.Warning("Skipping sharedAvatarController and MonoBehaviour PPtr reverse diagnostics because idx_source_relations_to is missing. Build that index on a copy or rebuild the source index before using broad Avatar bridge queries.");
                }
            }

            var payload = new JObject
            {
                ["schemaVersion"] = 1,
                ["sourceIndex"] = Path.GetFullPath(sourceIndexPath),
                ["outputRoot"] = outputRoot,
                ["modelSelector"] = modelSelector,
                ["animationSelector"] = animationSelector ?? string.Empty,
                ["limit"] = limit,
                ["selectedModelCount"] = selectedModels.Count,
                ["matchedModelCount"] = models.Count,
                ["candidateCount"] = filtered.Count,
                ["sourceIndexPerformance"] = new JObject
                {
                    ["hasRelationFromIndex"] = availableIndexes.Contains("idx_source_relations_from"),
                    ["hasRelationToIndex"] = hasReverseRelationIndex,
                    ["preFilterLimit"] = preFilterLimit,
                    ["sharedAvatarBridgeScanned"] = hasReverseRelationIndex || canScanSharedAvatarForward,
                    ["sharedAvatarBridgeScanMode"] = hasReverseRelationIndex
                        ? "reverse_index"
                        : canScanSharedAvatarForward
                            ? "forward_config_plus_relation_scan"
                            : "skipped_missing_indexes",
                    ["monoBehaviourReverseDiagnosticsScanned"] = hasReverseRelationIndex,
                    ["note"] = hasReverseRelationIndex
                        ? "Reverse relation index is available; shared Avatar bridge and MonoBehaviour PPtr diagnostics were scanned."
                        : canScanSharedAvatarForward
                            ? "Reverse relation index is missing; shared Avatar bridge used a conservative forward config scan plus the small animator.avatar relation set. MonoBehaviour reverse diagnostics were still skipped."
                            : "Reverse relation index is missing; shared Avatar bridge and MonoBehaviour PPtr diagnostics were skipped to avoid full source_relations scans on large source indexes.",
                },
                ["rule"] = "This report lists deterministic Unity source-index model-animation references only. It does not export animations and does not mark them playable.",
                ["modelFirstRule"] = "The selected model must pass mesh/material/texture/uv/skin/bbox validation before any row here may be used for animation preview or production validation.",
                ["relationRule"] = "Rows come from explicit Unity references such as Animator.controller -> AnimatorController.clip, legacy Animation.clip, or a conservative model config -> Avatar <- Animator.controller bridge. Names, folders, skeleton counts, and containers are review hints only.",
                ["sharedAvatarBridgeRule"] = "sharedAvatarController rows require a MonoBehaviour config that points to the selected prefab and an Avatar, plus an Animator that explicitly uses the same Avatar and controller clips. They are deterministic candidates, not visual proof; model gate, TRS export and screenshots must still pass.",
                ["animatorDiagnosticRule"] = "Diagnostics list matching GameObjects and their Animator/Controller/Clip state. They explain why explicit candidates may be zero, but they do not create or imply a model-animation binding.",
                ["monoBehaviourPPtrDiagnosticRule"] = "Diagnostics list MonoBehaviour PPtr fields that point at the selected model, plus sibling PPtr fields from the same MonoBehaviour. These are deterministic config clues only; they do not become playable animation candidates unless a model-first gate and explicit animation relation are also proven.",
                ["models"] = new JArray(models.Select(ToJson)),
                ["animatorDiagnostics"] = new JArray(animatorDiagnostics.Select(ToJson)),
                ["monoBehaviourPPtrDiagnostics"] = new JArray(monoBehaviourPPtrDiagnostics.Select(ToJson)),
                ["candidates"] = new JArray(filtered.Select(ToJson)),
            };

            var jsonPath = Path.Combine(outputRoot, "source_model_animation_candidates.json");
            var csvPath = Path.Combine(outputRoot, "source_model_animation_candidates.csv");
            File.WriteAllText(jsonPath, payload.ToString(Formatting.Indented));
            File.WriteAllText(csvPath, ToCsv(filtered), Encoding.UTF8);

            Logger.Info($"Source model-animation report: {jsonPath}");
            Logger.Info($"Source model-animation CSV: {csvPath}");
            foreach (var row in filtered.Take(12))
            {
                var hint = string.IsNullOrWhiteSpace(row.ReviewHint) ? string.Empty : $" [{row.ReviewHint}]";
                Logger.Info($"- {row.ModelName} -> {row.AnimationName} via {row.RelationKind}{hint}");
            }
            if (filtered.Count == 0 && animatorDiagnostics.Count > 0)
            {
                foreach (var row in animatorDiagnostics.Take(8))
                {
                    Logger.Info($"- diagnostic {row.ModelName}#{row.ModelPathId}: animator={row.AnimatorName ?? string.Empty} avatar={row.HasAvatar} controllers={row.ControllerCount} clips={row.ControllerClipCount} reason={row.Reason}");
                }
            }
            if (filtered.Count == 0 && monoBehaviourPPtrDiagnostics.Count > 0)
            {
                foreach (var row in monoBehaviourPPtrDiagnostics.Take(8))
                {
                    Logger.Info($"- mono pptr diagnostic {row.MonoBehaviourName ?? string.Empty}#{row.MonoBehaviourPathId}: {row.ReferenceFieldPath} -> {row.TargetType}/{row.TargetName}#{row.TargetPathId}");
                }
            }

            return jsonPath;
        }

        private static int BuildPreFilterLimit(int limit, string animationSelector)
        {
            if (string.IsNullOrWhiteSpace(animationSelector))
            {
                return limit;
            }

            // animationSelector 是正则，不能安全下推成 SQL LIKE。先扩大预筛数量，
            // 再做严格正则筛选，避免 dialog/cutscene 候选在 ORDER BY+LIMIT 前段挤掉真实动作。
            return Math.Clamp(limit * 8, limit, 500);
        }

        private static List<ModelRow> LoadMatchingModels(SqliteConnection connection, string selector, int limit)
        {
            var directPathId = long.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPathId);
            var likeToken = directPathId ? string.Empty : ExtractLongestLiteralToken(selector);
            var sqlLimit = Math.Clamp(limit * 50, 500, 20000);

            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT go.name, go.source_path, go.serialized_file, go.path_id
FROM source_objects go
WHERE go.type = 'GameObject'
  AND (
      $pathIdFilter = 1 AND go.path_id = $pathId
      OR $pathIdFilter = 0 AND (
          $likeToken = ''
          OR go.name LIKE $like
          OR go.source_path LIKE $like
          OR go.serialized_file LIKE $like
      )
  )
ORDER BY go.name COLLATE NOCASE, go.source_path COLLATE NOCASE
LIMIT $sqlLimit;";
            command.Parameters.AddWithValue("$pathIdFilter", directPathId ? 1 : 0);
            command.Parameters.AddWithValue("$pathId", directPathId ? parsedPathId : 0);
            command.Parameters.AddWithValue("$likeToken", likeToken ?? string.Empty);
            command.Parameters.AddWithValue("$like", "%" + (likeToken ?? string.Empty) + "%");
            command.Parameters.AddWithValue("$sqlLimit", sqlLimit);

            var result = new List<ModelRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = new ModelRow(
                    ReadString(reader, 0),
                    ReadString(reader, 1),
                    ReadString(reader, 2),
                    ReadLong(reader, 3),
                    string.Empty);
                if (MatchesModelSelector(selector, row))
                {
                    result.Add(row);
                    if (result.Count >= limit)
                    {
                        break;
                    }
                }
            }

            return result
                .Select(x => x with { ContainerPath = LoadContainerPath(connection, x.SerializedFile, x.PathId) })
                .ToList();
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
                }
                .Where(index => availableIndexes == null || !availableIndexes.Contains(index))
                .ToArray();
            if (missing.Length == 0)
            {
                return;
            }

            Logger.Warning($"Source model-animation lister is using a source index without relation performance index(es): {string.Join(", ", missing)}. Query may be slower; run --ensure_source_index_query_indexes on a copy or rebuild the source index when enough disk/time is available.");
        }

        private static string ApplyOptionalIndexHints(SqliteConnection connection, string sql)
        {
            return ApplyOptionalIndexHints(sql, LoadAvailableIndexNames(connection));
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
                     })
            {
                if (availableIndexes != null && availableIndexes.Contains(index))
                {
                    continue;
                }

                // 旧的大源索引可能只补了一部分查询索引。去掉强制 hint 后查询可能变慢，
                // 但至少能继续做精确模型关系诊断，不会因为缺索引直接失败。
                sql = sql.Replace($" INDEXED BY {index}", string.Empty, StringComparison.OrdinalIgnoreCase);
            }

            return sql;
        }

        private static List<ModelRow> LoadModelsForAnimationQuery(SqliteConnection connection, string selector, int limit)
        {
            // 已选模型的动画关系查询必须从模型出发，不能为了找动画扫完整 Animator 表。
            // 这样既符合“模型先通过验收，再查确定性关系”的阶段规则，也能在 Endfield 这类大索引上保持交互速度。
            var models = LoadMatchingModels(connection, selector, limit);

            return models
                .GroupBy(x => (x.SerializedFile ?? string.Empty) + "\n" + x.PathId.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal)
                .Select(x => x.First())
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.SourcePath, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();
        }

        private static List<ModelRow> LoadMatchingModelsByExactName(SqliteConnection connection, string name, int limit)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return new List<ModelRow>();
            }

            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT go.name, go.source_path, go.serialized_file, go.path_id
FROM source_objects go
WHERE go.type = 'GameObject'
  AND go.name = $name
ORDER BY go.source_path COLLATE NOCASE, go.path_id
LIMIT $limit;";
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

            var result = new List<ModelRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new ModelRow(
                    ReadString(reader, 0),
                    ReadString(reader, 1),
                    ReadString(reader, 2),
                    ReadLong(reader, 3),
                    string.Empty));
            }

            return result
                .Select(x => x with { ContainerPath = LoadContainerPath(connection, x.SerializedFile, x.PathId) })
                .ToList();
        }

        private static List<AnimatorDiagnosticRow> LoadAnimatorDiagnosticsForSelector(SqliteConnection connection, string selector, int limit)
        {
            var models = LoadMatchingModels(connection, selector, Math.Min(limit, 80));

            var distinctModels = models
                .GroupBy(x => (x.SerializedFile ?? string.Empty) + "\n" + x.PathId.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal)
                .Select(x => x.First())
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.SourcePath, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();

            var rows = new List<AnimatorDiagnosticRow>();
            foreach (var model in distinctModels)
            {
                var animators = LoadAnimatorsForModel(connection, model);
                if (animators.Count == 0)
                {
                    rows.Add(new AnimatorDiagnosticRow(
                        model.Name,
                        model.SourcePath,
                        model.SerializedFile,
                        model.PathId,
                        model.ContainerPath,
                        string.Empty,
                        string.Empty,
                        0,
                        false,
                        0,
                        string.Empty,
                        0,
                        "no_animator_component"));
                    continue;
                }

                foreach (var animator in animators)
                {
                    var summary = LoadAnimatorControllerSummary(connection, animator);
                    var reason = summary.ControllerCount <= 0
                        ? "animator_without_controller"
                        : summary.ControllerClipCount <= 0
                            ? "controller_without_clip"
                            : "has_explicit_controller_clips";
                    rows.Add(new AnimatorDiagnosticRow(
                        model.Name,
                        model.SourcePath,
                        model.SerializedFile,
                        model.PathId,
                        model.ContainerPath,
                        animator.Name,
                        animator.SerializedFile,
                        animator.PathId,
                        summary.HasAvatar,
                        summary.ControllerCount,
                        summary.ControllerNames,
                        summary.ControllerClipCount,
                        reason));
                }
            }

            return rows;
        }

        private static List<MonoBehaviourPPtrDiagnosticRow> LoadMonoBehaviourPPtrDiagnosticsForSelector(SqliteConnection connection, string selector, int limit)
        {
            var models = LoadMatchingModels(connection, selector, Math.Min(limit, 80));

            var distinctModels = models
                .GroupBy(x => (x.SerializedFile ?? string.Empty) + "\n" + x.PathId.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal)
                .Select(x => x.First())
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.SourcePath, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();

            var rows = new List<MonoBehaviourPPtrDiagnosticRow>();
            foreach (var model in distinctModels)
            {
                rows.AddRange(LoadMonoBehaviourPPtrDiagnosticsForModel(connection, model, Math.Max(1, limit)));
                if (rows.Count >= limit)
                {
                    break;
                }
            }

            return rows
                .GroupBy(x => string.Join("\n",
                    x.ModelSerializedFile ?? string.Empty,
                    x.ModelPathId.ToString(CultureInfo.InvariantCulture),
                    x.MonoBehaviourSerializedFile ?? string.Empty,
                    x.MonoBehaviourPathId.ToString(CultureInfo.InvariantCulture),
                    x.ReferenceFieldPath ?? string.Empty,
                    x.TargetSerializedFile ?? string.Empty,
                    x.TargetPathId.ToString(CultureInfo.InvariantCulture)), StringComparer.Ordinal)
                .Select(x => x.First())
                .OrderBy(x => x.MonoBehaviourName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.ReferenceFieldPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.TargetName, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();
        }

        private static List<MonoBehaviourPPtrDiagnosticRow> LoadMonoBehaviourPPtrDiagnosticsForModel(SqliteConnection connection, ModelRow model, int limit)
        {
            using var command = connection.CreateCommand();
            command.CommandText = ApplyOptionalIndexHints(connection, @"
WITH mono_sources AS (
    SELECT DISTINCT inbound.from_file, inbound.from_path_id
    FROM source_relations inbound INDEXED BY idx_source_relations_to
    WHERE inbound.relation = 'monoBehaviour.pptr'
      AND inbound.to_file = $modelFile
      AND inbound.to_path_id = $modelPathId
)
SELECT DISTINCT
    pptr.from_file,
    pptr.from_path_id,
    COALESCE(mono.name, '') AS mono_name,
    COALESCE(mono.source_path, '') AS mono_source_path,
    COALESCE(script.name, '') AS script_name,
    COALESCE(json_extract(pptr.raw_json, '$.details.path'), '') AS field_path,
    COALESCE(json_extract(pptr.raw_json, '$.details.field'), '') AS field_name,
    pptr.to_file,
    pptr.to_path_id,
    COALESCE(target.type, pptr.to_type_hint, '') AS target_type,
    COALESCE(target.name, '') AS target_name,
    COALESCE(target.source_path, '') AS target_source_path,
    CASE WHEN pptr.to_file = $modelFile AND pptr.to_path_id = $modelPathId THEN 1 ELSE 0 END AS is_selected_model
FROM mono_sources src
JOIN source_relations pptr INDEXED BY idx_source_relations_from
  ON pptr.from_file = src.from_file
 AND pptr.from_path_id = src.from_path_id
 AND pptr.relation = 'monoBehaviour.pptr'
LEFT JOIN source_objects mono
  ON mono.serialized_file = pptr.from_file
 AND mono.path_id = pptr.from_path_id
LEFT JOIN source_relations scriptRel INDEXED BY idx_source_relations_from
  ON scriptRel.from_file = pptr.from_file
 AND scriptRel.from_path_id = pptr.from_path_id
 AND scriptRel.relation = 'monoBehaviour.script'
LEFT JOIN source_objects script
  ON script.serialized_file = scriptRel.to_file
 AND script.path_id = scriptRel.to_path_id
LEFT JOIN source_objects target
  ON target.serialized_file = pptr.to_file
 AND target.path_id = pptr.to_path_id
ORDER BY mono_name COLLATE NOCASE, field_path COLLATE NOCASE, target_name COLLATE NOCASE
LIMIT $limit;");
            command.Parameters.AddWithValue("$modelFile", model.SerializedFile ?? string.Empty);
            command.Parameters.AddWithValue("$modelPathId", model.PathId);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit * 8, 16, 800));

            var rows = new List<MonoBehaviourPPtrDiagnosticRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new MonoBehaviourPPtrDiagnosticRow(
                    model.Name,
                    model.SourcePath,
                    model.SerializedFile,
                    model.PathId,
                    model.ContainerPath,
                    ReadString(reader, 0),
                    ReadLong(reader, 1),
                    ReadString(reader, 2),
                    ReadString(reader, 3),
                    ReadString(reader, 4),
                    ReadString(reader, 5),
                    ReadString(reader, 6),
                    ReadString(reader, 7),
                    ReadLong(reader, 8),
                    ReadString(reader, 9),
                    ReadString(reader, 10),
                    ReadString(reader, 11),
                    ReadLong(reader, 12) != 0));
            }

            return rows;
        }

        private static List<AnimatorComponentRow> LoadAnimatorsForModel(SqliteConnection connection, ModelRow model)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT DISTINCT animator.name, animator.serialized_file, animator.path_id
FROM source_relations rel INDEXED BY idx_source_relations_from
JOIN source_objects animator
  ON animator.serialized_file = rel.to_file
 AND animator.path_id = rel.to_path_id
WHERE rel.from_file = $modelFile
  AND rel.from_path_id = $modelPathId
  AND rel.relation = 'gameObject.component'
  AND animator.type = 'Animator'
ORDER BY animator.name COLLATE NOCASE, animator.path_id;";
            command.Parameters.AddWithValue("$modelFile", model.SerializedFile ?? string.Empty);
            command.Parameters.AddWithValue("$modelPathId", model.PathId);

            var rows = new List<AnimatorComponentRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new AnimatorComponentRow(
                    ReadString(reader, 0),
                    ReadString(reader, 1),
                    ReadLong(reader, 2)));
            }

            return rows;
        }

        private static AnimatorControllerSummary LoadAnimatorControllerSummary(SqliteConnection connection, AnimatorComponentRow animator)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT
  COALESCE(MAX(CASE WHEN avatarRel.relation IS NULL THEN 0 ELSE 1 END), 0) AS has_avatar,
  COUNT(DISTINCT controllerRel.to_file || ':' || controllerRel.to_path_id) AS controller_count,
  COALESCE(GROUP_CONCAT(DISTINCT controller.name), '') AS controller_names,
  COUNT(DISTINCT controllerClip.to_file || ':' || controllerClip.to_path_id) AS controller_clip_count
FROM source_objects animator
LEFT JOIN source_relations avatarRel INDEXED BY idx_source_relations_from
  ON avatarRel.from_file = animator.serialized_file
 AND avatarRel.from_path_id = animator.path_id
 AND avatarRel.relation = 'animator.avatar'
LEFT JOIN source_relations controllerRel INDEXED BY idx_source_relations_from
  ON controllerRel.from_file = animator.serialized_file
 AND controllerRel.from_path_id = animator.path_id
 AND controllerRel.relation = 'animator.controller'
LEFT JOIN source_objects controller
  ON controller.serialized_file = controllerRel.to_file
 AND controller.path_id = controllerRel.to_path_id
LEFT JOIN source_relations controllerClip INDEXED BY idx_source_relations_from
  ON controllerClip.from_file = controller.serialized_file
 AND controllerClip.from_path_id = controller.path_id
 AND controllerClip.relation = 'animatorController.clip'
WHERE animator.serialized_file = $animatorFile
  AND animator.path_id = $animatorPathId;";
            command.Parameters.AddWithValue("$animatorFile", animator.SerializedFile ?? string.Empty);
            command.Parameters.AddWithValue("$animatorPathId", animator.PathId);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return new AnimatorControllerSummary(false, 0, string.Empty, 0);
            }

            return new AnimatorControllerSummary(
                ReadLong(reader, 0) != 0,
                (int)ReadLong(reader, 1),
                ReadString(reader, 2),
                (int)ReadLong(reader, 3));
        }

        private static string LoadContainerPath(SqliteConnection connection, string serializedFile, long pathId)
        {
            // container 只做诊断提示，按已命中的模型逐条查，避免在大库 GameObject 扫描阶段做昂贵 join。
            if (!LoadAvailableIndexNames(connection).Contains("idx_source_relations_to"))
            {
                if (!warnedMissingContainerReverseIndex)
                {
                    Logger.Warning("Skipping source model container-path lookup because idx_source_relations_to is missing. Container path is diagnostic only; model-animation relations still use explicit Unity references.");
                    warnedMissingContainerReverseIndex = true;
                }

                return string.Empty;
            }

            using var command = connection.CreateCommand();
            command.CommandText = ApplyOptionalIndexHints(connection, @"
SELECT COALESCE(GROUP_CONCAT(DISTINCT json_extract(raw_json, '$.details.container')), '')
FROM source_relations INDEXED BY idx_source_relations_to
WHERE to_file = $file
  AND to_path_id = $pathId
  AND relation IN ('assetBundle.containerAsset', 'assetBundle.containerPreload', 'resourceManager.container');");
            command.Parameters.AddWithValue("$file", serializedFile ?? string.Empty);
            command.Parameters.AddWithValue("$pathId", pathId);
            return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static List<CandidateRow> FillModelContainerPaths(SqliteConnection connection, List<CandidateRow> rows)
        {
            if (rows.Count == 0)
            {
                return rows;
            }

            // 快速 selector 查询不做全库 container join，命中后再补容器路径。
            // 这个字段只用于模型优先筛选和诊断，不参与默认动画绑定。
            var cache = new Dictionary<string, string>(StringComparer.Ordinal);
            var result = new List<CandidateRow>(rows.Count);
            foreach (var row in rows)
            {
                if (!string.IsNullOrWhiteSpace(row.ModelContainerPath))
                {
                    result.Add(row);
                    continue;
                }

                var key = (row.ModelSerializedFile ?? string.Empty) + "\n" + row.ModelPathId.ToString(CultureInfo.InvariantCulture);
                if (!cache.TryGetValue(key, out var containerPath))
                {
                    containerPath = LoadContainerPath(connection, row.ModelSerializedFile, row.ModelPathId);
                    cache[key] = containerPath;
                }

                result.Add(row with { ModelContainerPath = containerPath });
            }

            return result;
        }

        private static List<CandidateRow> LoadAnimatorControllerClips(SqliteConnection connection, ModelRow model)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT animator.name, animator.serialized_file, animator.path_id,
       controller.name, controller.serialized_file, controller.path_id,
       clip.name, clip.source_path, clip.serialized_file, clip.path_id,
       controllerClip.relation
FROM source_objects animator
JOIN source_relations goRel INDEXED BY idx_source_relations_from
  ON goRel.from_file = animator.serialized_file
 AND goRel.from_path_id = animator.path_id
 AND goRel.relation = 'component.gameObject'
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
JOIN source_objects clip
  ON clip.serialized_file = controllerClip.to_file
 AND clip.path_id = controllerClip.to_path_id
WHERE animator.type = 'Animator'
  AND goRel.to_file = $modelFile
  AND goRel.to_path_id = $modelPathId
ORDER BY clip.name COLLATE NOCASE;";
            command.Parameters.AddWithValue("$modelFile", model.SerializedFile);
            command.Parameters.AddWithValue("$modelPathId", model.PathId);

            var result = new List<CandidateRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new CandidateRow(
                    model.Name,
                    model.SourcePath,
                    model.SerializedFile,
                    model.PathId,
                    model.ContainerPath,
                    ReadString(reader, 0),
                    ReadString(reader, 1),
                    ReadLong(reader, 2),
                    ReadString(reader, 3),
                    ReadString(reader, 4),
                    ReadLong(reader, 5),
                    ReadString(reader, 6),
                    ReadString(reader, 7),
                    ReadString(reader, 8),
                    ReadLong(reader, 9),
                    ReadString(reader, 10),
                    string.Empty,
                    string.Empty));
            }

            return result;
        }

        private static List<CandidateRow> LoadSharedAvatarControllerClips(SqliteConnection connection, ModelRow model, int limit)
        {
            // Endfield 的正式角色模型常通过 SkeletalMorphAvatarDataSO 之类配置保存：
            // config -> prefabWithRendererHelper(模型) 与 config -> avatar(Avatar)。
            // 动画 controller 则挂在使用同 Avatar 的运行实例上。这里只按这条确定性 PPtr/Avatar 链桥接，
            // 不按角色名、目录或动画名前缀猜。
            using var command = connection.CreateCommand();
            command.CommandText = ApplyOptionalIndexHints(connection, @"
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
       animator.name, animator.serialized_file, animator.path_id,
       controller.name, controller.serialized_file, controller.path_id,
       clip.name, clip.source_path, clip.serialized_file, clip.path_id,
       go.name AS animator_go_name,
       go.serialized_file AS animator_go_file,
       go.path_id AS animator_go_path_id,
       config_avatars.config_name,
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
JOIN source_objects clip
  ON clip.serialized_file = controllerClip.to_file
 AND clip.path_id = controllerClip.to_path_id
LEFT JOIN source_relations goRel INDEXED BY idx_source_relations_from
  ON goRel.from_file = animator.serialized_file
 AND goRel.from_path_id = animator.path_id
 AND goRel.relation = 'component.gameObject'
LEFT JOIN source_objects go
  ON go.serialized_file = goRel.to_file
 AND go.path_id = goRel.to_path_id
ORDER BY controller.name COLLATE NOCASE, clip.name COLLATE NOCASE
LIMIT $limit;");
            command.Parameters.AddWithValue("$modelFile", model.SerializedFile);
            command.Parameters.AddWithValue("$modelPathId", model.PathId);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

            var result = new List<CandidateRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var animatorGoName = ReadString(reader, 10);
                var animatorGoFile = ReadString(reader, 11);
                var animatorGoPathId = ReadLong(reader, 12);
                var configName = ReadString(reader, 13);
                var avatarName = ReadString(reader, 14);
                var avatarFile = ReadString(reader, 15);
                var avatarPathId = ReadLong(reader, 16);
                var evidence = $"config={configName}; avatar={avatarName}#{avatarPathId}; avatarFile={avatarFile}; animatorModel={animatorGoName}#{animatorGoPathId}; animatorModelFile={animatorGoFile}";

                result.Add(new CandidateRow(
                    model.Name,
                    model.SourcePath,
                    model.SerializedFile,
                    model.PathId,
                    model.ContainerPath,
                    ReadString(reader, 0),
                    ReadString(reader, 1),
                    ReadLong(reader, 2),
                    ReadString(reader, 3),
                    ReadString(reader, 4),
                    ReadLong(reader, 5),
                    ReadString(reader, 6),
                    ReadString(reader, 7),
                    ReadString(reader, 8),
                    ReadLong(reader, 9),
                    "sharedAvatarController",
                    evidence,
                    string.Empty));
            }

            return result;
        }

        private static List<CandidateRow> LoadSharedAvatarControllerClipsFromForwardConfig(SqliteConnection connection, ModelRow model, int limit)
        {
            // 大型源索引可能缺少 to_file/to_path_id 反查索引，不能从“谁指向模型”做全库反查。
            // 这里只在选中模型同一个 SerializedFile 内找 MonoBehaviour 配置，并要求它同时 PPtr 到该模型和 Avatar。
            // 然后通过很小的 animator.avatar 关系集合找使用同 Avatar 的 Animator；全程仍是 Unity 显式引用。
            using var command = connection.CreateCommand();
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
       animator.name, animator.serialized_file, animator.path_id,
       controller.name, controller.serialized_file, controller.path_id,
       clip.name, clip.source_path, clip.serialized_file, clip.path_id,
       go.name AS animator_go_name,
       go.serialized_file AS animator_go_file,
       go.path_id AS animator_go_path_id,
       config_avatars.config_name,
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
JOIN source_objects clip
  ON clip.serialized_file = controllerClip.to_file
 AND clip.path_id = controllerClip.to_path_id
LEFT JOIN source_relations goRel INDEXED BY idx_source_relations_from
  ON goRel.from_file = animator.serialized_file
 AND goRel.from_path_id = animator.path_id
 AND goRel.relation = 'component.gameObject'
LEFT JOIN source_objects go
  ON go.serialized_file = goRel.to_file
 AND go.path_id = goRel.to_path_id
ORDER BY controller.name COLLATE NOCASE, clip.name COLLATE NOCASE
LIMIT $limit;";
            command.Parameters.AddWithValue("$modelFile", model.SerializedFile);
            command.Parameters.AddWithValue("$modelPathId", model.PathId);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

            var result = new List<CandidateRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var animatorGoName = ReadString(reader, 10);
                var animatorGoFile = ReadString(reader, 11);
                var animatorGoPathId = ReadLong(reader, 12);
                var configName = ReadString(reader, 13);
                var avatarName = ReadString(reader, 14);
                var avatarFile = ReadString(reader, 15);
                var avatarPathId = ReadLong(reader, 16);
                var evidence = $"config={configName}; avatar={avatarName}#{avatarPathId}; avatarFile={avatarFile}; animatorModel={animatorGoName}#{animatorGoPathId}; animatorModelFile={animatorGoFile}; bridgeScan=forwardConfig";

                result.Add(new CandidateRow(
                    model.Name,
                    model.SourcePath,
                    model.SerializedFile,
                    model.PathId,
                    model.ContainerPath,
                    ReadString(reader, 0),
                    ReadString(reader, 1),
                    ReadLong(reader, 2),
                    ReadString(reader, 3),
                    ReadString(reader, 4),
                    ReadLong(reader, 5),
                    ReadString(reader, 6),
                    ReadString(reader, 7),
                    ReadString(reader, 8),
                    ReadLong(reader, 9),
                    "sharedAvatarController",
                    evidence,
                    string.Empty));
            }

            return result;
        }

        private static List<CandidateRow> LoadAnimatorControllerClipsForSelector(SqliteConnection connection, string selector, int limit)
        {
            var directPathId = long.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPathId);
            var likeToken = directPathId ? string.Empty : ExtractLongestLiteralToken(selector);

            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT go.name, go.source_path, go.serialized_file, go.path_id,
       animator.name, animator.serialized_file, animator.path_id,
       controller.name, controller.serialized_file, controller.path_id,
       clip.name, clip.source_path, clip.serialized_file, clip.path_id,
       controllerClip.relation
FROM source_objects animator INDEXED BY idx_source_objects_type
JOIN source_relations goRel INDEXED BY idx_source_relations_from
  ON goRel.from_file = animator.serialized_file
 AND goRel.from_path_id = animator.path_id
 AND goRel.relation = 'component.gameObject'
JOIN source_objects go
  ON go.serialized_file = goRel.to_file
 AND go.path_id = goRel.to_path_id
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
JOIN source_objects clip
  ON clip.serialized_file = controllerClip.to_file
 AND clip.path_id = controllerClip.to_path_id
WHERE animator.type = 'Animator'
  AND (
      $pathIdFilter = 1 AND go.path_id = $pathId
      OR $pathIdFilter = 0 AND (
          $likeToken = ''
          OR go.name LIKE $like
          OR go.source_path LIKE $like
          OR go.serialized_file LIKE $like
      )
  )
ORDER BY go.name COLLATE NOCASE, clip.name COLLATE NOCASE
LIMIT $limit;";
            command.Parameters.AddWithValue("$pathIdFilter", directPathId ? 1 : 0);
            command.Parameters.AddWithValue("$pathId", directPathId ? parsedPathId : 0);
            command.Parameters.AddWithValue("$likeToken", likeToken ?? string.Empty);
            command.Parameters.AddWithValue("$like", "%" + (likeToken ?? string.Empty) + "%");
            command.Parameters.AddWithValue("$limit", limit);

            var result = new List<CandidateRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new CandidateRow(
                    ReadString(reader, 0),
                    ReadString(reader, 1),
                    ReadString(reader, 2),
                    ReadLong(reader, 3),
                    string.Empty,
                    ReadString(reader, 4),
                    ReadString(reader, 5),
                    ReadLong(reader, 6),
                    ReadString(reader, 7),
                    ReadString(reader, 8),
                    ReadLong(reader, 9),
                    ReadString(reader, 10),
                    ReadString(reader, 11),
                    ReadString(reader, 12),
                    ReadLong(reader, 13),
                    ReadString(reader, 14),
                    string.Empty,
                    string.Empty));
            }

            return result;
        }

        private static List<CandidateRow> LoadLegacyAnimationClips(SqliteConnection connection, ModelRow model)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT animation.name, animation.serialized_file, animation.path_id,
       clip.name, clip.source_path, clip.serialized_file, clip.path_id,
       clipRel.relation
FROM source_objects animation
JOIN source_relations goRel INDEXED BY idx_source_relations_from
  ON goRel.from_file = animation.serialized_file
 AND goRel.from_path_id = animation.path_id
 AND goRel.relation = 'component.gameObject'
JOIN source_relations clipRel INDEXED BY idx_source_relations_from
  ON clipRel.from_file = animation.serialized_file
 AND clipRel.from_path_id = animation.path_id
 AND clipRel.relation = 'animation.clip'
JOIN source_objects clip
  ON clip.serialized_file = clipRel.to_file
 AND clip.path_id = clipRel.to_path_id
WHERE animation.type = 'Animation'
  AND goRel.to_file = $modelFile
  AND goRel.to_path_id = $modelPathId
ORDER BY clip.name COLLATE NOCASE;";
            command.Parameters.AddWithValue("$modelFile", model.SerializedFile);
            command.Parameters.AddWithValue("$modelPathId", model.PathId);

            var result = new List<CandidateRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new CandidateRow(
                    model.Name,
                    model.SourcePath,
                    model.SerializedFile,
                    model.PathId,
                    model.ContainerPath,
                    ReadString(reader, 0),
                    ReadString(reader, 1),
                    ReadLong(reader, 2),
                    string.Empty,
                    string.Empty,
                    0,
                    ReadString(reader, 3),
                    ReadString(reader, 4),
                    ReadString(reader, 5),
                    ReadLong(reader, 6),
                    ReadString(reader, 7),
                    string.Empty,
                    string.Empty));
            }

            return result;
        }

        private static List<CandidateRow> LoadLegacyAnimationClipsForSelector(SqliteConnection connection, string selector, int limit)
        {
            var directPathId = long.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPathId);
            var likeToken = directPathId ? string.Empty : ExtractLongestLiteralToken(selector);

            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT go.name, go.source_path, go.serialized_file, go.path_id,
       animation.name, animation.serialized_file, animation.path_id,
       clip.name, clip.source_path, clip.serialized_file, clip.path_id,
       clipRel.relation
FROM source_objects animation INDEXED BY idx_source_objects_type
JOIN source_relations goRel INDEXED BY idx_source_relations_from
  ON goRel.from_file = animation.serialized_file
 AND goRel.from_path_id = animation.path_id
 AND goRel.relation = 'component.gameObject'
JOIN source_objects go
  ON go.serialized_file = goRel.to_file
 AND go.path_id = goRel.to_path_id
JOIN source_relations clipRel INDEXED BY idx_source_relations_from
  ON clipRel.from_file = animation.serialized_file
 AND clipRel.from_path_id = animation.path_id
 AND clipRel.relation = 'animation.clip'
JOIN source_objects clip
  ON clip.serialized_file = clipRel.to_file
 AND clip.path_id = clipRel.to_path_id
WHERE animation.type = 'Animation'
  AND (
      $pathIdFilter = 1 AND go.path_id = $pathId
      OR $pathIdFilter = 0 AND (
          $likeToken = ''
          OR go.name LIKE $like
          OR go.source_path LIKE $like
          OR go.serialized_file LIKE $like
      )
  )
ORDER BY go.name COLLATE NOCASE, clip.name COLLATE NOCASE
LIMIT $limit;";
            command.Parameters.AddWithValue("$pathIdFilter", directPathId ? 1 : 0);
            command.Parameters.AddWithValue("$pathId", directPathId ? parsedPathId : 0);
            command.Parameters.AddWithValue("$likeToken", likeToken ?? string.Empty);
            command.Parameters.AddWithValue("$like", "%" + (likeToken ?? string.Empty) + "%");
            command.Parameters.AddWithValue("$limit", limit);

            var result = new List<CandidateRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new CandidateRow(
                    ReadString(reader, 0),
                    ReadString(reader, 1),
                    ReadString(reader, 2),
                    ReadLong(reader, 3),
                    string.Empty,
                    ReadString(reader, 4),
                    ReadString(reader, 5),
                    ReadLong(reader, 6),
                    string.Empty,
                    string.Empty,
                    0,
                    ReadString(reader, 7),
                    ReadString(reader, 8),
                    ReadString(reader, 9),
                    ReadLong(reader, 10),
                    ReadString(reader, 11),
                    string.Empty,
                    string.Empty));
            }

            return result;
        }

        private static bool MatchesModelSelector(string selector, ModelRow row)
        {
            if (long.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pathId))
            {
                return row.PathId == pathId;
            }

            return Regex.IsMatch(row.Name ?? string.Empty, selector, RegexOptions.IgnoreCase)
                || Regex.IsMatch(row.SourcePath ?? string.Empty, selector, RegexOptions.IgnoreCase)
                || Regex.IsMatch(row.SerializedFile ?? string.Empty, selector, RegexOptions.IgnoreCase)
                || Regex.IsMatch(row.ContainerPath ?? string.Empty, selector, RegexOptions.IgnoreCase);
        }

        private static bool MatchesModelSelector(string selector, CandidateRow row)
        {
            if (long.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pathId))
            {
                return row.ModelPathId == pathId;
            }

            return Regex.IsMatch(row.ModelName ?? string.Empty, selector, RegexOptions.IgnoreCase)
                || Regex.IsMatch(row.ModelSourcePath ?? string.Empty, selector, RegexOptions.IgnoreCase)
                || Regex.IsMatch(row.ModelSerializedFile ?? string.Empty, selector, RegexOptions.IgnoreCase)
                || Regex.IsMatch(row.ModelContainerPath ?? string.Empty, selector, RegexOptions.IgnoreCase);
        }

        private static bool MatchesAnimationSelector(string selector, CandidateRow row)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                return true;
            }

            return Regex.IsMatch(row.AnimationName ?? string.Empty, selector, RegexOptions.IgnoreCase)
                || Regex.IsMatch(row.AnimationSourcePath ?? string.Empty, selector, RegexOptions.IgnoreCase)
                || Regex.IsMatch(row.AnimationSerializedFile ?? string.Empty, selector, RegexOptions.IgnoreCase)
                || Regex.IsMatch(row.ControllerName ?? string.Empty, selector, RegexOptions.IgnoreCase)
                || Regex.IsMatch(row.RelationKind ?? string.Empty, selector, RegexOptions.IgnoreCase);
        }

        private static string BuildReviewHint(CandidateRow row)
        {
            var text = string.Join("/", row.ModelName, row.ModelSourcePath, row.ModelSerializedFile, row.ModelContainerPath, row.ControllerName, row.AnimationName, row.AnimationSourcePath, row.AnimationSerializedFile).ToLowerInvariant();
            var hits = new List<string>();
            AddIfContains(text, hits, "dialog");
            AddIfContains(text, hits, "timeline");
            AddIfContains(text, hits, "levelseq");
            AddIfContains(text, hits, "cutscene");
            AddIfContains(text, hits, "_cs_");
            AddIfContains(text, hits, "cs_");
            AddIfContains(text, hits, "postmodel");
            AddIfContains(text, hits, "uimodel");
            AddIfContains(text, hits, "abilityentity");
            AddIfContains(text, hits, "tmpobject");
            AddIfContains(text, hits, "_tmp");
            AddIfContains(text, hits, "/tmp");
            AddIfContains(text, hits, "att_widget");
            AddIfContains(text, hits, "widget");
            AddIfContains(text, hits, "cine");
            AddIfContains(text, hits, "ui");
            AddIfContains(text, hits, "preview");
            AddIfContains(text, hits, "deco");
            AddIfContains(text, hits, "pose");
            AddIfContains(text, hits, "camera");
            if (string.Equals(row.RelationKind, "sharedAvatarController", StringComparison.OrdinalIgnoreCase))
            {
                hits.Add("sharedAvatarBridgeNeedsVisualValidation");
                var modelToken = ExtractActorToken(row.ModelName);
                var controllerToken = ExtractActorToken(row.ControllerName);
                if (LooksActorSpecific(row.AnimationName)
                    && !ActorTokenMatches(row.AnimationName, modelToken)
                    && !ActorTokenMatches(row.AnimationName, controllerToken)
                    && !ContainsGenericHumanToken(row.AnimationName))
                {
                    hits.Add("animationNameDiffersFromModelOrController");
                }
            }
            return string.Join(",", hits.Distinct(StringComparer.OrdinalIgnoreCase));
        }

        private static bool LooksActorSpecific(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.StartsWith("A_actor_", StringComparison.OrdinalIgnoreCase);
        }

        private static string ExtractActorToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var match = Regex.Match(value, @"(?:P|A|AC|SK)_(?:npc_)?(?:human_)?(?:actor|npc)_([A-Za-z0-9]+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static bool ActorTokenMatches(string value, string actorToken)
        {
            return !string.IsNullOrWhiteSpace(actorToken)
                && !string.IsNullOrWhiteSpace(value)
                && value.IndexOf(actorToken, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ContainsGenericHumanToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.IndexOf("_girl_", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("_boy_", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("_male_", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("_female_", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("_common_", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void AddIfContains(string text, List<string> hits, string token)
        {
            if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(token);
            }
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

        private static JObject ToJson(ModelRow row)
        {
            return new JObject
            {
                ["name"] = row.Name,
                ["sourcePath"] = row.SourcePath,
                ["serializedFile"] = row.SerializedFile,
                ["pathId"] = row.PathId,
                ["containerPath"] = row.ContainerPath,
            };
        }

        private static JObject ToJson(CandidateRow row)
        {
            return new JObject
            {
                ["deterministic"] = true,
                ["relationSource"] = "explicit_source_index",
                ["relationKind"] = row.RelationKind,
                ["relationEvidence"] = row.RelationEvidence,
                ["reviewHint"] = row.ReviewHint,
                ["modelReadyForAnimation"] = false,
                ["modelReadyForAnimationReason"] = "This source-index report cannot prove model quality. Export and validate the model first.",
                ["model"] = new JObject
                {
                    ["name"] = row.ModelName,
                    ["sourcePath"] = row.ModelSourcePath,
                    ["serializedFile"] = row.ModelSerializedFile,
                    ["pathId"] = row.ModelPathId,
                    ["containerPath"] = row.ModelContainerPath,
                },
                ["animatorOrAnimation"] = new JObject
                {
                    ["name"] = row.AnimatorName,
                    ["serializedFile"] = row.AnimatorSerializedFile,
                    ["pathId"] = row.AnimatorPathId,
                },
                ["controller"] = new JObject
                {
                    ["name"] = row.ControllerName,
                    ["serializedFile"] = row.ControllerSerializedFile,
                    ["pathId"] = row.ControllerPathId,
                },
                ["animation"] = new JObject
                {
                    ["name"] = row.AnimationName,
                    ["sourcePath"] = row.AnimationSourcePath,
                    ["serializedFile"] = row.AnimationSerializedFile,
                    ["pathId"] = row.AnimationPathId,
                },
            };
        }

        private static JObject ToJson(AnimatorDiagnosticRow row)
        {
            return new JObject
            {
                ["deterministic"] = true,
                ["diagnosticOnly"] = true,
                ["reason"] = row.Reason,
                ["model"] = new JObject
                {
                    ["name"] = row.ModelName,
                    ["sourcePath"] = row.ModelSourcePath,
                    ["serializedFile"] = row.ModelSerializedFile,
                    ["pathId"] = row.ModelPathId,
                    ["containerPath"] = row.ModelContainerPath,
                },
                ["animator"] = new JObject
                {
                    ["name"] = row.AnimatorName,
                    ["serializedFile"] = row.AnimatorSerializedFile,
                    ["pathId"] = row.AnimatorPathId,
                    ["hasAvatarReference"] = row.HasAvatar,
                    ["controllerCount"] = row.ControllerCount,
                    ["controllerNames"] = row.ControllerNames,
                    ["controllerClipCount"] = row.ControllerClipCount,
                },
                ["rule"] = "This row explains source-index state only. A same-name Animator or Avatar without AnimatorController clips is not a playable animation relation.",
            };
        }

        private static JObject ToJson(MonoBehaviourPPtrDiagnosticRow row)
        {
            return new JObject
            {
                ["deterministic"] = true,
                ["diagnosticOnly"] = true,
                ["relationKind"] = "monoBehaviour.pptr",
                ["model"] = new JObject
                {
                    ["name"] = row.ModelName,
                    ["sourcePath"] = row.ModelSourcePath,
                    ["serializedFile"] = row.ModelSerializedFile,
                    ["pathId"] = row.ModelPathId,
                    ["containerPath"] = row.ModelContainerPath,
                },
                ["monoBehaviour"] = new JObject
                {
                    ["name"] = row.MonoBehaviourName,
                    ["sourcePath"] = row.MonoBehaviourSourcePath,
                    ["serializedFile"] = row.MonoBehaviourSerializedFile,
                    ["pathId"] = row.MonoBehaviourPathId,
                    ["scriptName"] = row.ScriptName,
                },
                ["reference"] = new JObject
                {
                    ["path"] = row.ReferenceFieldPath,
                    ["field"] = row.ReferenceFieldName,
                    ["isSelectedModel"] = row.IsSelectedModel,
                    ["target"] = new JObject
                    {
                        ["type"] = row.TargetType,
                        ["name"] = row.TargetName,
                        ["sourcePath"] = row.TargetSourcePath,
                        ["serializedFile"] = row.TargetSerializedFile,
                        ["pathId"] = row.TargetPathId,
                    },
                },
                ["rule"] = "MonoBehaviour PPtr fields are deterministic config clues. They may prove model/avatar/config adjacency, but they are not animation candidates until an explicit animation relation and model-first validation also pass.",
            };
        }

        private static string ToCsv(List<CandidateRow> rows)
        {
            var builder = new StringBuilder();
            builder.AppendLine("modelName,modelSourcePath,modelSerializedFile,modelPathId,modelContainerPath,animatorOrAnimationName,animatorOrAnimationFile,animatorOrAnimationPathId,controllerName,controllerFile,controllerPathId,animationName,animationSourcePath,animationSerializedFile,animationPathId,relationKind,relationEvidence,reviewHint,deterministic,modelReadyForAnimation");
            foreach (var row in rows)
            {
                builder.AppendLine(string.Join(",",
                    Csv(row.ModelName),
                    Csv(row.ModelSourcePath),
                    Csv(row.ModelSerializedFile),
                    row.ModelPathId.ToString(CultureInfo.InvariantCulture),
                    Csv(row.ModelContainerPath),
                    Csv(row.AnimatorName),
                    Csv(row.AnimatorSerializedFile),
                    row.AnimatorPathId.ToString(CultureInfo.InvariantCulture),
                    Csv(row.ControllerName),
                    Csv(row.ControllerSerializedFile),
                    row.ControllerPathId.ToString(CultureInfo.InvariantCulture),
                    Csv(row.AnimationName),
                    Csv(row.AnimationSourcePath),
                    Csv(row.AnimationSerializedFile),
                    row.AnimationPathId.ToString(CultureInfo.InvariantCulture),
                    Csv(row.RelationKind),
                    Csv(row.RelationEvidence),
                    Csv(row.ReviewHint),
                    "true",
                    "false"));
            }

            return builder.ToString();
        }

        private static string Csv(string value)
        {
            value ??= string.Empty;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static string ReadString(SqliteDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
        }

        private static long ReadLong(SqliteDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }

        private sealed record ModelRow(
            string Name,
            string SourcePath,
            string SerializedFile,
            long PathId,
            string ContainerPath);

        private sealed record CandidateRow(
            string ModelName,
            string ModelSourcePath,
            string ModelSerializedFile,
            long ModelPathId,
            string ModelContainerPath,
            string AnimatorName,
            string AnimatorSerializedFile,
            long AnimatorPathId,
            string ControllerName,
            string ControllerSerializedFile,
            long ControllerPathId,
            string AnimationName,
            string AnimationSourcePath,
            string AnimationSerializedFile,
            long AnimationPathId,
            string RelationKind,
            string RelationEvidence,
            string ReviewHint);

        private sealed record AnimatorComponentRow(
            string Name,
            string SerializedFile,
            long PathId);

        private sealed record AnimatorControllerSummary(
            bool HasAvatar,
            int ControllerCount,
            string ControllerNames,
            int ControllerClipCount);

        private sealed record AnimatorDiagnosticRow(
            string ModelName,
            string ModelSourcePath,
            string ModelSerializedFile,
            long ModelPathId,
            string ModelContainerPath,
            string AnimatorName,
            string AnimatorSerializedFile,
            long AnimatorPathId,
            bool HasAvatar,
            int ControllerCount,
            string ControllerNames,
            int ControllerClipCount,
            string Reason);

        private sealed record MonoBehaviourPPtrDiagnosticRow(
            string ModelName,
            string ModelSourcePath,
            string ModelSerializedFile,
            long ModelPathId,
            string ModelContainerPath,
            string MonoBehaviourSerializedFile,
            long MonoBehaviourPathId,
            string MonoBehaviourName,
            string MonoBehaviourSourcePath,
            string ScriptName,
            string ReferenceFieldPath,
            string ReferenceFieldName,
            string TargetSerializedFile,
            long TargetPathId,
            string TargetType,
            string TargetName,
            string TargetSourcePath,
            bool IsSelectedModel);
    }
}
