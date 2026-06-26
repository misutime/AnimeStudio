using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
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
            // 这个入口常用于大源索引排查；阶段日志能在超时时留下最后完成的位置。
            var timingPath = Path.Combine(outputRoot, "source_model_animation_profile.jsonl");
            File.WriteAllText(timingPath, string.Empty);

            SQLitePCL.Batteries_V2.Init();
            using var connection = new SqliteConnection($"Data Source={Path.GetFullPath(sourceIndexPath)};Mode=ReadOnly");
            connection.Open();
            var availableIndexes = MeasureStage(timingPath, "load_available_indexes", null, () => LoadAvailableIndexNames(connection));
            WarnMissingPerformanceIndexes(availableIndexes);

            var modelScanLimit = Math.Clamp(limit, 20, 500);
            var preFilterLimit = BuildPreFilterLimit(limit, animationSelector);
            var rows = new List<CandidateRow>();
            var selectedModels = MeasureStage(timingPath, "load_selected_models", null, () => LoadModelsForAnimationQuery(connection, modelSelector, modelScanLimit));
            var hasReverseRelationIndex = availableIndexes.Contains("idx_source_relations_to");
            var canScanSharedAvatarForward =
                availableIndexes.Contains("idx_source_relations_from") &&
                availableIndexes.Contains("idx_source_relations_relation");
            foreach (var model in selectedModels)
            {
                rows.AddRange(MeasureStage(timingPath, "load_animator_controller_clips", StageModelData(model), () => LoadAnimatorControllerClips(connection, model)));
                rows.AddRange(MeasureStage(timingPath, "load_legacy_animation_clips", StageModelData(model), () => LoadLegacyAnimationClips(connection, model)));
                if (hasReverseRelationIndex)
                {
                    rows.AddRange(MeasureStage(timingPath, "load_shared_avatar_controller_clips", StageModelData(model), () => LoadSharedAvatarControllerClips(connection, model, Math.Max(1, preFilterLimit - rows.Count))));
                }
                else if (canScanSharedAvatarForward)
                {
                    rows.AddRange(MeasureStage(timingPath, "load_shared_avatar_controller_clips_forward", StageModelData(model), () => LoadSharedAvatarControllerClipsFromForwardConfig(connection, model, Math.Max(1, preFilterLimit - rows.Count))));
                }

                if (rows.Count >= preFilterLimit)
                {
                    break;
                }
            }

            rows = MeasureStage(timingPath, "fill_model_container_paths", null, () => FillModelContainerPaths(connection, rows));

            var filtered = MeasureStage(timingPath, "filter_candidates", null, () => rows
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
                .ToList());

            var models = MeasureStage(timingPath, "build_candidate_models", null, () => filtered
                .GroupBy(x => x.ModelSerializedFile + "\n" + x.ModelPathId.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal)
                .Select(g => new ModelRow(
                    g.First().ModelName,
                    g.First().ModelSourcePath,
                    g.First().ModelSerializedFile,
                    g.First().ModelPathId,
                    g.First().ModelContainerPath))
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.SerializedFile, StringComparer.OrdinalIgnoreCase)
                .ToList());

            var animatorDiagnostics = MeasureStage(timingPath, "load_animator_diagnostics", null, () => LoadAnimatorDiagnosticsForModels(connection, selectedModels, Math.Min(limit, 80)));
            var monoBehaviourPPtrDiagnostics = hasReverseRelationIndex
                ? MeasureStage(timingPath, "load_mono_behaviour_pptr_diagnostics", null, () => LoadMonoBehaviourPPtrDiagnosticsForModels(connection, selectedModels, Math.Min(limit, 80)))
                : new List<MonoBehaviourPPtrDiagnosticRow>();
            var scriptAnimationComponentDiagnostics = MeasureStage(timingPath, "load_script_animation_component_diagnostics", null, () => LoadScriptAnimationComponentDiagnosticsForModels(connection, selectedModels, Math.Min(limit, 80)));
            var avatarTosClipDiagnostics = MeasureStage(timingPath, "load_avatar_tos_clip_diagnostics", null, () => LoadAvatarTosClipDiagnosticsForModels(
                connection,
                selectedModels,
                animationSelector,
                Math.Min(limit, 80)));
            var modelVisibilityDiagnostics = MeasureStage(timingPath, "load_model_visibility_diagnostics", null, () => LoadModelVisibilityDiagnosticsForModels(connection, selectedModels, Math.Min(limit, 80)));
            var modelAvatarCompatibilityDiagnostics = MeasureStage(timingPath, "load_model_avatar_compatibility_diagnostics", null, () => LoadModelAvatarCompatibilityDiagnosticsForModels(
                connection,
                selectedModels,
                modelVisibilityDiagnostics,
                Math.Min(limit, 80)));
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
                ["sourceModelAnimationProfile"] = timingPath,
                ["modelSelector"] = modelSelector,
                ["animationSelector"] = animationSelector ?? string.Empty,
                ["limit"] = limit,
                ["selectedModelCount"] = selectedModels.Count,
                ["matchedModelCount"] = models.Count,
                ["candidateModelCount"] = models.Count,
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
                ["scriptAnimationComponentDiagnosticRule"] = "Diagnostics list AnimationClip PPtr fields on MonoBehaviour components attached to the selected GameObject and its direct children. Each row also includes a bounded subtree visibility summary for local context. These rows are script-animation clues, not default model-animation candidates; deeper or broader scans must stay explicit and bounded.",
                ["avatarTosClipDiagnosticRule"] = "Diagnostics list AnimationClip binding pathHash coverage against the selected model's explicit Animator.avatar -> Avatar.m_TOS table. This is structural evidence for Naraka hash-only path recovery only; it is not a default model-animation relation and must not bypass Animator/Controller context, model validation, TRS export, or visual review.",
                ["modelVisibilityDiagnosticRule"] = "Diagnostics count Renderer/Animator components under the selected GameObject hierarchy. They only explain whether a selected source object looks like a visible model root; real model readiness still requires exported glTF, material/texture/skin/bbox validation, and visual review.",
                ["modelAvatarCompatibilityDiagnosticRule"] = "Diagnostics compare selected model Transform evidence against Avatar TOS/oracle paths. This is structural compatibility only: it must not create default model-animation relations, bypass explicit Animator/Controller context, or mark Humanoid/Muscle animation playable.",
                ["selectedModels"] = new JArray(selectedModels.Select(ToJson)),
                ["modelVisibilityDiagnostics"] = new JArray(modelVisibilityDiagnostics.Select(ToJson)),
                ["modelAvatarCompatibilityDiagnostics"] = new JArray(modelAvatarCompatibilityDiagnostics.Select(ToJson)),
                ["models"] = new JArray(models.Select(ToJson)),
                ["animatorDiagnostics"] = new JArray(animatorDiagnostics.Select(ToJson)),
                ["monoBehaviourPPtrDiagnostics"] = new JArray(monoBehaviourPPtrDiagnostics.Select(ToJson)),
                ["scriptAnimationComponentDiagnosticSummary"] = BuildScriptAnimationComponentDiagnosticSummary(scriptAnimationComponentDiagnostics),
                ["scriptAnimationComponentDiagnostics"] = new JArray(scriptAnimationComponentDiagnostics.Select(ToJson)),
                ["avatarTosClipDiagnosticSummary"] = BuildAvatarTosClipDiagnosticSummary(avatarTosClipDiagnostics),
                ["avatarTosClipDiagnostics"] = new JArray(avatarTosClipDiagnostics.Select(ToJson)),
                ["modelAvatarCompatibilityDiagnosticSummary"] = BuildModelAvatarCompatibilityDiagnosticSummary(modelAvatarCompatibilityDiagnostics),
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
            if (filtered.Count == 0 && avatarTosClipDiagnostics.Count > 0)
            {
                foreach (var row in avatarTosClipDiagnostics.Take(8))
                {
                    Logger.Info($"- avatar TOS diagnostic {row.ModelName}#{row.ModelPathId}: avatar={row.AvatarName} clip={row.AnimationName} coverage={Ratio(row.ResolvedPathHashCount, row.UniqueHashOnlyPathHashCount):0.######}");
                }
            }
            if (filtered.Count == 0 && modelAvatarCompatibilityDiagnostics.Count > 0)
            {
                foreach (var row in modelAvatarCompatibilityDiagnostics.Take(8))
                {
                    Logger.Info($"- model/avatar compatibility diagnostic {row.ModelName}#{row.ModelPathId}: avatar={row.AvatarName ?? string.Empty} coverage={row.CoverageRatio:0.######} matched={row.MatchedAvatarPathCount}/{row.ComparableAvatarPathCount} reason={row.Reason}");
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

        private static T MeasureStage<T>(string timingPath, string stage, IDictionary<string, object> data, Func<T> action)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var result = action();
                WriteStageTiming(timingPath, stage, stopwatch.Elapsed, true, data, null);
                return result;
            }
            catch (Exception ex)
            {
                WriteStageTiming(timingPath, stage, stopwatch.Elapsed, false, data, ex.Message);
                throw;
            }
        }

        private static Dictionary<string, object> StageModelData(ModelRow model)
        {
            return new Dictionary<string, object>
            {
                ["modelName"] = model.Name ?? string.Empty,
                ["modelSerializedFile"] = model.SerializedFile ?? string.Empty,
                ["modelPathId"] = model.PathId,
            };
        }

        private static void WriteStageTiming(string timingPath, string stage, TimeSpan elapsed, bool success, IDictionary<string, object> data, string error)
        {
            if (string.IsNullOrWhiteSpace(timingPath))
            {
                return;
            }

            var payload = new Dictionary<string, object>
            {
                ["timestamp"] = DateTime.UtcNow.ToString("O"),
                ["stage"] = stage,
                ["elapsedMs"] = Math.Round(elapsed.TotalMilliseconds, 3),
                ["success"] = success,
            };
            if (!string.IsNullOrWhiteSpace(error))
            {
                payload["error"] = error;
            }
            if (data != null)
            {
                foreach (var pair in data)
                {
                    payload[pair.Key] = pair.Value;
                }
            }

            File.AppendAllText(timingPath, JsonConvert.SerializeObject(payload) + Environment.NewLine);
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

        private static List<AnimatorDiagnosticRow> LoadAnimatorDiagnosticsForModels(SqliteConnection connection, IReadOnlyList<ModelRow> models, int limit)
        {
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

        private static List<MonoBehaviourPPtrDiagnosticRow> LoadMonoBehaviourPPtrDiagnosticsForModels(SqliteConnection connection, IReadOnlyList<ModelRow> models, int limit)
        {
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

        private static List<AvatarTosClipDiagnosticRow> LoadAvatarTosClipDiagnosticsForModels(
            SqliteConnection connection,
            IReadOnlyList<ModelRow> models,
            string animationSelector,
            int limit)
        {
            var result = new List<AvatarTosClipDiagnosticRow>();
            foreach (var model in models ?? Array.Empty<ModelRow>())
            {
                result.AddRange(LoadAvatarTosClipDiagnosticsForModel(connection, model, animationSelector, Math.Max(1, limit - result.Count)));
                if (result.Count >= limit)
                {
                    break;
                }
            }

            return result
                .OrderByDescending(x => Ratio(x.ResolvedPathHashCount, x.UniqueHashOnlyPathHashCount))
                .ThenByDescending(x => x.UniqueHashOnlyPathHashCount)
                .ThenBy(x => x.AnimationName, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();
        }

        private static List<ModelVisibilityDiagnosticRow> LoadModelVisibilityDiagnosticsForModels(SqliteConnection connection, IReadOnlyList<ModelRow> models, int limit)
        {
            var result = new List<ModelVisibilityDiagnosticRow>();
            foreach (var model in models ?? Array.Empty<ModelRow>())
            {
                result.Add(LoadModelVisibilityDiagnostic(connection, model));
                if (result.Count >= limit)
                {
                    break;
                }
            }

            return result;
        }

        private static List<ModelAvatarCompatibilityDiagnosticRow> LoadModelAvatarCompatibilityDiagnosticsForModels(
            SqliteConnection connection,
            IReadOnlyList<ModelRow> models,
            IReadOnlyList<ModelVisibilityDiagnosticRow> visibilityRows,
            int limit)
        {
            var avatars = LoadAvatarPathSets(connection);
            var visibilityByModel = (visibilityRows ?? Array.Empty<ModelVisibilityDiagnosticRow>())
                .GroupBy(x => BuildObjectKey(x.ModelSerializedFile, x.ModelPathId), StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
            var result = new List<ModelAvatarCompatibilityDiagnosticRow>();

            foreach (var model in models ?? Array.Empty<ModelRow>())
            {
                visibilityByModel.TryGetValue(BuildObjectKey(model.SerializedFile, model.PathId), out var visibility);
                var evidence = LoadModelTransformEvidence(connection, model, maxTransforms: 2048);
                var rows = BuildModelAvatarCompatibilityRows(model, visibility, evidence, avatars, perModelLimit: 6);
                result.AddRange(rows);
                if (result.Count >= limit)
                {
                    break;
                }
            }

            return result
                .OrderByDescending(x => x.CoverageRatio)
                .ThenByDescending(x => x.MatchedAvatarPathCount)
                .ThenByDescending(x => x.ModelPathCount)
                .ThenBy(x => x.ModelName, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();
        }

        private static List<ModelAvatarCompatibilityDiagnosticRow> BuildModelAvatarCompatibilityRows(
            ModelRow model,
            ModelVisibilityDiagnosticRow visibility,
            ModelTransformEvidence evidence,
            IReadOnlyList<AvatarPathSet> avatars,
            int perModelLimit)
        {
            var visibleRendererCount = (visibility?.MeshRendererCount ?? 0) + (visibility?.SkinnedMeshRendererCount ?? 0);
            var modelComparablePaths = BuildComparablePathVariants(evidence.Paths);
            if (modelComparablePaths.Count == 0)
            {
                return new List<ModelAvatarCompatibilityDiagnosticRow>
                {
                    new ModelAvatarCompatibilityDiagnosticRow(
                        model.Name,
                        model.SourcePath,
                        model.SerializedFile,
                        model.PathId,
                        model.ContainerPath,
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        0,
                        evidence.Paths.Count,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        visibleRendererCount,
                        evidence.HierarchyPathCount,
                        evidence.TransformNodePathCount,
                        "no_comparable_model_transform_paths",
                        BuildSampleArray(evidence.Paths, 12),
                        new JArray(),
                        new JArray())
                };
            }

            var rows = new List<ModelAvatarCompatibilityDiagnosticRow>();
            foreach (var avatar in avatars)
            {
                var comparableAvatarPaths = avatar.Paths
                    .Select(NormalizePathForCompare)
                    .Where(x => CountPathSegments(x) >= 2)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (comparableAvatarPaths.Count == 0)
                {
                    continue;
                }
                if (comparableAvatarPaths.Count < 20 && avatar.HumanBoneCount < 20)
                {
                    continue;
                }

                var matched = comparableAvatarPaths
                    .Where(modelComparablePaths.Contains)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (matched.Count == 0)
                {
                    continue;
                }

                var coverage = Ratio(matched.Count, comparableAvatarPaths.Count);
                if (matched.Count < 10 && coverage < 0.5)
                {
                    continue;
                }

                var unmatched = comparableAvatarPaths
                    .Where(x => !modelComparablePaths.Contains(x))
                    .Take(12)
                    .ToList();
                rows.Add(new ModelAvatarCompatibilityDiagnosticRow(
                    model.Name,
                    model.SourcePath,
                    model.SerializedFile,
                    model.PathId,
                    model.ContainerPath,
                    avatar.Name,
                    avatar.SourcePath,
                    avatar.SerializedFile,
                    avatar.PathId,
                    evidence.Paths.Count,
                    modelComparablePaths.Count,
                    comparableAvatarPaths.Count,
                    matched.Count,
                    coverage,
                    avatar.HumanBoneCount,
                    avatar.TosPathCount,
                    visibleRendererCount,
                    evidence.HierarchyPathCount,
                    evidence.TransformNodePathCount,
                    coverage >= 0.8 ? "high_structural_path_overlap_diagnostic_only" : "partial_structural_path_overlap_diagnostic_only",
                    BuildSampleArray(evidence.Paths, 12),
                    BuildSampleArray(matched, 12),
                    BuildSampleArray(unmatched, 12)));
            }

            if (rows.Count == 0)
            {
                rows.Add(new ModelAvatarCompatibilityDiagnosticRow(
                    model.Name,
                    model.SourcePath,
                    model.SerializedFile,
                    model.PathId,
                    model.ContainerPath,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    0,
                    evidence.Paths.Count,
                    modelComparablePaths.Count,
                    0,
                    0,
                    0,
                    0,
                    0,
                    visibleRendererCount,
                    evidence.HierarchyPathCount,
                    evidence.TransformNodePathCount,
                    "no_avatar_path_overlap",
                    BuildSampleArray(evidence.Paths, 12),
                    new JArray(),
                    new JArray()));
            }

            return rows
                .OrderByDescending(x => x.CoverageRatio)
                .ThenByDescending(x => x.MatchedAvatarPathCount)
                .ThenBy(x => x.AvatarName, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, perModelLimit))
                .ToList();
        }

        private static ModelVisibilityDiagnosticRow LoadModelVisibilityDiagnostic(SqliteConnection connection, ModelRow model)
        {
            // 大 CTE 在 Naraka 全量索引上容易被 SQLite 规划成慢查询；这里按 Unity 显式层级拆成几条小查询。
            var rootTransform = LoadRootTransform(connection, model);
            var hierarchyGameObjects = LoadModelAndDirectChildGameObjects(connection, model, rootTransform);
            var components = new List<ComponentRow>();
            foreach (var gameObject in hierarchyGameObjects)
            {
                components.AddRange(LoadComponentsForGameObject(connection, gameObject));
            }

            var animatorComponents = components
                .Where(x => string.Equals(x.Type, "Animator", StringComparison.Ordinal))
                .ToList();

            return new ModelVisibilityDiagnosticRow(
                model.Name,
                model.SourcePath,
                model.SerializedFile,
                model.PathId,
                model.ContainerPath,
                hierarchyGameObjects.Count,
                rootTransform == null ? 0 : 1,
                components.Count(x => string.Equals(x.Type, "MeshRenderer", StringComparison.Ordinal)),
                components.Count(x => string.Equals(x.Type, "SkinnedMeshRenderer", StringComparison.Ordinal)),
                components.Count(x => string.Equals(x.Type, "MeshFilter", StringComparison.Ordinal)),
                animatorComponents.Count,
                components.Count(x => string.Equals(x.Type, "Animation", StringComparison.Ordinal)),
                CountDistinctComponentTargets(connection, animatorComponents, "animator.avatar"),
                CountDistinctComponentTargets(connection, animatorComponents, "animator.controller"));
        }

        private static List<ScriptAnimationComponentDiagnosticRow> LoadScriptAnimationComponentDiagnosticsForModels(SqliteConnection connection, IReadOnlyList<ModelRow> models, int limit)
        {
            var result = new List<ScriptAnimationComponentDiagnosticRow>();
            foreach (var model in models ?? Array.Empty<ModelRow>())
            {
                result.AddRange(LoadScriptAnimationComponentDiagnosticsForModel(connection, model, Math.Max(1, limit - result.Count)));
                if (result.Count >= limit)
                {
                    break;
                }
            }

            return result
                .GroupBy(x => string.Join("\n",
                    x.ModelSerializedFile ?? string.Empty,
                    x.ModelPathId.ToString(CultureInfo.InvariantCulture),
                    x.GameObjectSerializedFile ?? string.Empty,
                    x.GameObjectPathId.ToString(CultureInfo.InvariantCulture),
                    x.MonoBehaviourSerializedFile ?? string.Empty,
                    x.MonoBehaviourPathId.ToString(CultureInfo.InvariantCulture),
                    x.ReferenceFieldPath ?? string.Empty,
                    x.AnimationSerializedFile ?? string.Empty,
                    x.AnimationPathId.ToString(CultureInfo.InvariantCulture)), StringComparer.Ordinal)
                .Select(x => x.First())
                .OrderBy(x => x.GameObjectDepth)
                .ThenBy(x => x.GameObjectName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.ScriptName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.AnimationName, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();
        }

        private static List<ScriptAnimationComponentDiagnosticRow> LoadScriptAnimationComponentDiagnosticsForModel(SqliteConnection connection, ModelRow model, int limit)
        {
            var result = new List<ScriptAnimationComponentDiagnosticRow>();
            var rootTransform = LoadRootTransform(connection, model);
            var gameObjects = LoadModelAndDirectChildGameObjects(connection, model, rootTransform);
            foreach (var gameObject in gameObjects)
            {
                var gameObjectName = LoadSourceObjectName(connection, gameObject);
                var gameObjectDepth = gameObject.PathId == model.PathId && string.Equals(gameObject.SerializedFile, model.SerializedFile, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                var components = LoadComponentsForGameObject(connection, gameObject);
                var monoBehaviourComponents = components
                    .Where(x => string.Equals(x.Type, "MonoBehaviour", StringComparison.Ordinal))
                    .ToList();
                if (monoBehaviourComponents.Count == 0)
                {
                    continue;
                }

                var visibleRendererCount = components.Count(x => string.Equals(x.Type, "MeshRenderer", StringComparison.Ordinal) || string.Equals(x.Type, "SkinnedMeshRenderer", StringComparison.Ordinal));
                var animatorCount = components.Count(x => string.Equals(x.Type, "Animator", StringComparison.Ordinal));
                var rectTransformCount = components.Count(x => string.Equals(x.Type, "RectTransform", StringComparison.Ordinal));
                var subtreeVisibility = LoadScriptAnimationSubtreeVisibility(connection, gameObject, maxDepth: 4, maxGameObjects: 160);
                foreach (var component in monoBehaviourComponents)
                {
                    result.AddRange(LoadScriptAnimationClipRefsForMonoBehaviour(
                        connection,
                        model,
                        gameObject,
                        gameObjectName,
                        gameObjectDepth,
                        visibleRendererCount,
                        animatorCount,
                        rectTransformCount,
                        subtreeVisibility,
                        component,
                        Math.Max(1, limit - result.Count)));
                    if (result.Count >= limit)
                    {
                        return result;
                    }
                }
            }

            return result;
        }

        private static List<ScriptAnimationComponentDiagnosticRow> LoadScriptAnimationClipRefsForMonoBehaviour(
            SqliteConnection connection,
            ModelRow model,
            SourceObjectKey gameObject,
            string gameObjectName,
            int gameObjectDepth,
            int visibleRendererCount,
            int animatorCount,
            int rectTransformCount,
            ScriptAnimationSubtreeVisibility subtreeVisibility,
            ComponentRow monoBehaviour,
            int limit)
        {
            using var command = connection.CreateCommand();
            command.CommandText = ApplyOptionalIndexHints(connection, @"
SELECT
    COALESCE(mono.name, '') AS mono_name,
    COALESCE(mono.source_path, '') AS mono_source_path,
    COALESCE(script.name, '') AS script_name,
    COALESCE(json_extract(pptr.raw_json, '$.details.path'), '') AS field_path,
    COALESCE(json_extract(pptr.raw_json, '$.details.field'), '') AS field_name,
    clip.name AS clip_name,
    clip.source_path AS clip_source_path,
    clip.serialized_file AS clip_file,
    clip.path_id AS clip_path_id
FROM source_relations pptr INDEXED BY idx_source_relations_from
JOIN source_objects clip
  ON clip.serialized_file = pptr.to_file
 AND clip.path_id = pptr.to_path_id
 AND clip.type = 'AnimationClip'
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
WHERE pptr.from_file = $monoFile
  AND pptr.from_path_id = $monoPathId
  AND pptr.relation = 'monoBehaviour.pptr'
ORDER BY script_name COLLATE NOCASE, field_path COLLATE NOCASE, clip_name COLLATE NOCASE
LIMIT $limit;");
            command.Parameters.AddWithValue("$monoFile", monoBehaviour.SerializedFile ?? string.Empty);
            command.Parameters.AddWithValue("$monoPathId", monoBehaviour.PathId);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 128));

            var rows = new List<ScriptAnimationComponentDiagnosticRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new ScriptAnimationComponentDiagnosticRow(
                    model.Name,
                    model.SourcePath,
                    model.SerializedFile,
                    model.PathId,
                    model.ContainerPath,
                    gameObject.SerializedFile,
                    gameObject.PathId,
                    gameObjectName,
                    gameObjectDepth,
                    visibleRendererCount,
                    animatorCount,
                    rectTransformCount,
                    subtreeVisibility?.GameObjectCount ?? 0,
                    subtreeVisibility?.MeshRendererCount ?? 0,
                    subtreeVisibility?.SkinnedMeshRendererCount ?? 0,
                    subtreeVisibility?.AnimatorCount ?? 0,
                    subtreeVisibility?.RectTransformCount ?? 0,
                    subtreeVisibility?.MaxDepth ?? 0,
                    subtreeVisibility?.Truncated ?? false,
                    subtreeVisibility?.VisibleGameObjectSamples ?? new List<string>(),
                    monoBehaviour.SerializedFile,
                    monoBehaviour.PathId,
                    ReadString(reader, 0),
                    ReadString(reader, 1),
                    ReadString(reader, 2),
                    ReadString(reader, 3),
                    ReadString(reader, 4),
                    ReadString(reader, 5),
                    ReadString(reader, 6),
                    ReadString(reader, 7),
                    ReadLong(reader, 8)));
            }

            return rows;
        }

        private static List<SourceObjectKey> LoadModelAndDirectChildGameObjects(SqliteConnection connection, ModelRow model, SourceObjectKey rootTransform)
        {
            var hierarchyGameObjects = new List<SourceObjectKey>
            {
                new SourceObjectKey(model.SerializedFile ?? string.Empty, model.PathId),
            };
            if (rootTransform != null)
            {
                hierarchyGameObjects.AddRange(LoadDirectChildGameObjects(connection, rootTransform));
            }

            return hierarchyGameObjects
                .GroupBy(x => (x.SerializedFile ?? string.Empty) + "\n" + x.PathId.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal)
                .Select(x => x.First())
                .ToList();
        }

        private static ScriptAnimationSubtreeVisibility LoadScriptAnimationSubtreeVisibility(SqliteConnection connection, SourceObjectKey gameObject, int maxDepth, int maxGameObjects)
        {
            var rootTransform = LoadTransformForGameObject(connection, gameObject);
            if (rootTransform == null)
            {
                var rootComponents = LoadComponentsForGameObject(connection, gameObject);
                return BuildScriptAnimationSubtreeVisibility(
                    connection,
                    new List<SourceObjectKey> { gameObject },
                    new Dictionary<string, List<ComponentRow>>(StringComparer.Ordinal)
                    {
                        [BuildObjectKey(gameObject.SerializedFile, gameObject.PathId)] = rootComponents,
                    },
                    maxDepth: 0,
                    truncated: false);
            }

            var gameObjects = new List<SourceObjectKey>();
            var componentsByGameObject = new Dictionary<string, List<ComponentRow>>(StringComparer.Ordinal);
            var visitedTransforms = new HashSet<SourceObjectKey>();
            var queue = new Queue<(SourceObjectKey Transform, int Depth)>();
            var observedMaxDepth = 0;
            var truncated = false;
            queue.Enqueue((rootTransform, 0));

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!visitedTransforms.Add(current.Transform))
                {
                    continue;
                }

                observedMaxDepth = Math.Max(observedMaxDepth, current.Depth);
                var currentGameObject = LoadGameObjectForTransform(connection, current.Transform);
                if (currentGameObject != null)
                {
                    var key = BuildObjectKey(currentGameObject.SerializedFile, currentGameObject.PathId);
                    if (!componentsByGameObject.ContainsKey(key))
                    {
                        gameObjects.Add(currentGameObject);
                        componentsByGameObject[key] = LoadComponentsForGameObject(connection, currentGameObject);
                    }
                }

                if (gameObjects.Count >= maxGameObjects)
                {
                    truncated = true;
                    break;
                }

                if (current.Depth >= maxDepth)
                {
                    continue;
                }

                foreach (var child in LoadChildTransforms(connection, current.Transform))
                {
                    queue.Enqueue((child, current.Depth + 1));
                }
            }

            return BuildScriptAnimationSubtreeVisibility(connection, gameObjects, componentsByGameObject, observedMaxDepth, truncated);
        }

        private static ScriptAnimationSubtreeVisibility BuildScriptAnimationSubtreeVisibility(
            SqliteConnection connection,
            IReadOnlyList<SourceObjectKey> gameObjects,
            IReadOnlyDictionary<string, List<ComponentRow>> componentsByGameObject,
            int maxDepth,
            bool truncated)
        {
            var meshRendererCount = 0;
            var skinnedMeshRendererCount = 0;
            var animatorCount = 0;
            var rectTransformCount = 0;
            var visibleSamples = new List<string>();

            foreach (var gameObject in gameObjects ?? Array.Empty<SourceObjectKey>())
            {
                var key = BuildObjectKey(gameObject.SerializedFile, gameObject.PathId);
                if (!componentsByGameObject.TryGetValue(key, out var components))
                {
                    components = LoadComponentsForGameObject(connection, gameObject);
                }

                var hasVisibleRenderer = false;
                foreach (var component in components)
                {
                    if (string.Equals(component.Type, "MeshRenderer", StringComparison.Ordinal))
                    {
                        meshRendererCount++;
                        hasVisibleRenderer = true;
                    }
                    else if (string.Equals(component.Type, "SkinnedMeshRenderer", StringComparison.Ordinal))
                    {
                        skinnedMeshRendererCount++;
                        hasVisibleRenderer = true;
                    }
                    else if (string.Equals(component.Type, "Animator", StringComparison.Ordinal))
                    {
                        animatorCount++;
                    }
                    else if (string.Equals(component.Type, "RectTransform", StringComparison.Ordinal))
                    {
                        rectTransformCount++;
                    }
                }

                if (hasVisibleRenderer && visibleSamples.Count < 8)
                {
                    var name = LoadSourceObjectName(connection, gameObject);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        visibleSamples.Add(name);
                    }
                }
            }

            return new ScriptAnimationSubtreeVisibility(
                gameObjects?.Count ?? 0,
                meshRendererCount,
                skinnedMeshRendererCount,
                animatorCount,
                rectTransformCount,
                maxDepth,
                truncated,
                visibleSamples);
        }

        private static ModelTransformEvidence LoadModelTransformEvidence(SqliteConnection connection, ModelRow model, int maxTransforms)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hierarchyPathCount = 0;
            var transformNodePathCount = 0;
            var rootTransform = LoadRootTransform(connection, model);
            if (rootTransform != null)
            {
                foreach (var path in LoadHierarchyTransformPaths(connection, model, rootTransform, maxTransforms))
                {
                    if (!string.IsNullOrWhiteSpace(path) && paths.Add(path))
                    {
                        hierarchyPathCount++;
                    }
                }
            }

            // Naraka 的 ActorBodyVisualCell 会显式列出 transformNodes.data。
            // 这只能作为骨骼/节点覆盖诊断，不能单独证明 Avatar 或动画绑定。
            foreach (var transform in LoadMonoBehaviourTransformNodeRefs(connection, model, maxTransforms))
            {
                var path = BuildTransformPath(connection, transform, maxDepth: 64);
                if (!string.IsNullOrWhiteSpace(path) && paths.Add(path))
                {
                    transformNodePathCount++;
                }
            }

            return new ModelTransformEvidence(
                paths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                hierarchyPathCount,
                transformNodePathCount);
        }

        private static List<string> LoadHierarchyTransformPaths(SqliteConnection connection, ModelRow model, SourceObjectKey rootTransform, int maxTransforms)
        {
            var result = new List<string>();
            var visited = new HashSet<SourceObjectKey>();
            var queue = new Queue<(SourceObjectKey Transform, string Path, int Depth)>();
            var rootName = LoadGameObjectNameForTransform(connection, rootTransform);
            if (string.IsNullOrWhiteSpace(rootName))
            {
                rootName = model.Name ?? string.Empty;
            }

            queue.Enqueue((rootTransform, rootName, 0));
            while (queue.Count > 0 && result.Count < maxTransforms)
            {
                var current = queue.Dequeue();
                if (!visited.Add(current.Transform))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(current.Path))
                {
                    result.Add(current.Path);
                }

                if (current.Depth >= 64)
                {
                    continue;
                }

                foreach (var child in LoadChildTransforms(connection, current.Transform))
                {
                    var childName = LoadGameObjectNameForTransform(connection, child);
                    var childPath = string.IsNullOrWhiteSpace(childName)
                        ? current.Path
                        : CombineUnityPath(current.Path, childName);
                    queue.Enqueue((child, childPath, current.Depth + 1));
                }
            }

            return result;
        }

        private static List<SourceObjectKey> LoadMonoBehaviourTransformNodeRefs(SqliteConnection connection, ModelRow model, int maxTransforms)
        {
            var result = new List<SourceObjectKey>();
            foreach (var component in LoadComponentsForGameObject(connection, new SourceObjectKey(model.SerializedFile ?? string.Empty, model.PathId)))
            {
                if (!string.Equals(component.Type, "MonoBehaviour", StringComparison.Ordinal))
                {
                    continue;
                }

                using var command = connection.CreateCommand();
                command.CommandText = ApplyOptionalIndexHints(connection, @"
SELECT DISTINCT rel.to_file, rel.to_path_id
FROM source_relations rel INDEXED BY idx_source_relations_from
WHERE rel.from_file = $componentFile
  AND rel.from_path_id = $componentPathId
  AND rel.relation = 'monoBehaviour.pptr'
  AND json_extract(rel.raw_json, '$.details.path') = 'transformNodes.data'
LIMIT $limit;");
                command.Parameters.AddWithValue("$componentFile", component.SerializedFile ?? string.Empty);
                command.Parameters.AddWithValue("$componentPathId", component.PathId);
                command.Parameters.AddWithValue("$limit", Math.Clamp(maxTransforms - result.Count, 1, maxTransforms));

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new SourceObjectKey(ReadString(reader, 0), ReadLong(reader, 1)));
                    if (result.Count >= maxTransforms)
                    {
                        return result
                            .GroupBy(x => BuildObjectKey(x.SerializedFile, x.PathId), StringComparer.Ordinal)
                            .Select(x => x.First())
                            .ToList();
                    }
                }
            }

            return result
                .GroupBy(x => BuildObjectKey(x.SerializedFile, x.PathId), StringComparer.Ordinal)
                .Select(x => x.First())
                .ToList();
        }

        private static string BuildTransformPath(SqliteConnection connection, SourceObjectKey transform, int maxDepth)
        {
            var names = new List<string>();
            var visited = new HashSet<SourceObjectKey>();
            var current = transform;
            for (var depth = 0; depth < maxDepth && current != null && visited.Add(current); depth++)
            {
                var name = LoadGameObjectNameForTransform(connection, current);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }

                current = LoadParentTransform(connection, current);
            }

            names.Reverse();
            return string.Join("/", names);
        }

        private static string LoadGameObjectNameForTransform(SqliteConnection connection, SourceObjectKey transform)
        {
            using var command = connection.CreateCommand();
            command.CommandText = ApplyOptionalIndexHints(connection, @"
SELECT COALESCE(go.name, '')
FROM source_relations rel INDEXED BY idx_source_relations_from
JOIN source_objects go
  ON go.serialized_file = rel.to_file
 AND go.path_id = rel.to_path_id
 AND go.type = 'GameObject'
WHERE rel.from_file = $transformFile
  AND rel.from_path_id = $transformPathId
  AND rel.relation = 'component.gameObject'
LIMIT 1;");
            command.Parameters.AddWithValue("$transformFile", transform.SerializedFile ?? string.Empty);
            command.Parameters.AddWithValue("$transformPathId", transform.PathId);
            return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static SourceObjectKey LoadGameObjectForTransform(SqliteConnection connection, SourceObjectKey transform)
        {
            using var command = connection.CreateCommand();
            command.CommandText = ApplyOptionalIndexHints(connection, @"
SELECT go.serialized_file, go.path_id
FROM source_relations rel INDEXED BY idx_source_relations_from
JOIN source_objects go
  ON go.serialized_file = rel.to_file
 AND go.path_id = rel.to_path_id
 AND go.type = 'GameObject'
WHERE rel.from_file = $transformFile
  AND rel.from_path_id = $transformPathId
  AND rel.relation = 'component.gameObject'
LIMIT 1;");
            command.Parameters.AddWithValue("$transformFile", transform.SerializedFile ?? string.Empty);
            command.Parameters.AddWithValue("$transformPathId", transform.PathId);

            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new SourceObjectKey(ReadString(reader, 0), ReadLong(reader, 1))
                : null;
        }

        private static SourceObjectKey LoadTransformForGameObject(SqliteConnection connection, SourceObjectKey gameObject)
        {
            using var command = connection.CreateCommand();
            command.CommandText = ApplyOptionalIndexHints(connection, @"
SELECT comp.serialized_file, comp.path_id
FROM source_relations rel INDEXED BY idx_source_relations_from
JOIN source_objects comp
  ON comp.serialized_file = rel.to_file
 AND comp.path_id = rel.to_path_id
 AND comp.type IN ('Transform', 'RectTransform')
WHERE rel.from_file = $gameObjectFile
  AND rel.from_path_id = $gameObjectPathId
  AND rel.relation = 'gameObject.component'
ORDER BY CASE WHEN comp.type = 'Transform' THEN 0 ELSE 1 END
LIMIT 1;");
            command.Parameters.AddWithValue("$gameObjectFile", gameObject.SerializedFile ?? string.Empty);
            command.Parameters.AddWithValue("$gameObjectPathId", gameObject.PathId);

            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new SourceObjectKey(ReadString(reader, 0), ReadLong(reader, 1))
                : null;
        }

        private static string LoadSourceObjectName(SqliteConnection connection, SourceObjectKey sourceObject)
        {
            using var command = connection.CreateCommand();
            command.CommandText = ApplyOptionalIndexHints(connection, @"
SELECT COALESCE(name, '')
FROM source_objects
WHERE serialized_file = $file
  AND path_id = $pathId
LIMIT 1;");
            command.Parameters.AddWithValue("$file", sourceObject.SerializedFile ?? string.Empty);
            command.Parameters.AddWithValue("$pathId", sourceObject.PathId);
            return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static SourceObjectKey LoadParentTransform(SqliteConnection connection, SourceObjectKey transform)
        {
            using var command = connection.CreateCommand();
            command.CommandText = ApplyOptionalIndexHints(connection, @"
SELECT rel.from_file, rel.from_path_id
FROM source_relations rel INDEXED BY idx_source_relations_to
WHERE rel.to_file = $transformFile
  AND rel.to_path_id = $transformPathId
  AND rel.relation = 'transform.child'
LIMIT 1;");
            command.Parameters.AddWithValue("$transformFile", transform.SerializedFile ?? string.Empty);
            command.Parameters.AddWithValue("$transformPathId", transform.PathId);

            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new SourceObjectKey(ReadString(reader, 0), ReadLong(reader, 1))
                : null;
        }

        private static List<SourceObjectKey> LoadChildTransforms(SqliteConnection connection, SourceObjectKey transform)
        {
            using var command = connection.CreateCommand();
            command.CommandText = ApplyOptionalIndexHints(connection, @"
SELECT DISTINCT rel.to_file, rel.to_path_id
FROM source_relations rel INDEXED BY idx_source_relations_from
WHERE rel.from_file = $transformFile
  AND rel.from_path_id = $transformPathId
  AND rel.relation = 'transform.child'
ORDER BY rel.to_path_id;");
            command.Parameters.AddWithValue("$transformFile", transform.SerializedFile ?? string.Empty);
            command.Parameters.AddWithValue("$transformPathId", transform.PathId);

            var result = new List<SourceObjectKey>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new SourceObjectKey(ReadString(reader, 0), ReadLong(reader, 1)));
            }

            return result;
        }

        private static SourceObjectKey LoadRootTransform(SqliteConnection connection, ModelRow model)
        {
            using var command = connection.CreateCommand();
            command.CommandText = ApplyOptionalIndexHints(connection, @"
SELECT comp.serialized_file, comp.path_id
FROM source_relations rel INDEXED BY idx_source_relations_from
JOIN source_objects comp
  ON comp.serialized_file = rel.to_file
 AND comp.path_id = rel.to_path_id
 AND comp.type = 'Transform'
WHERE rel.from_file = $modelFile
  AND rel.from_path_id = $modelPathId
  AND rel.relation = 'gameObject.component'
LIMIT 1;");
            command.Parameters.AddWithValue("$modelFile", model.SerializedFile ?? string.Empty);
            command.Parameters.AddWithValue("$modelPathId", model.PathId);

            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new SourceObjectKey(ReadString(reader, 0), ReadLong(reader, 1))
                : null;
        }

        private static List<SourceObjectKey> LoadDirectChildGameObjects(SqliteConnection connection, SourceObjectKey rootTransform)
        {
            using var command = connection.CreateCommand();
            command.CommandText = ApplyOptionalIndexHints(connection, @"
SELECT DISTINCT goRel.to_file, goRel.to_path_id
FROM source_relations child INDEXED BY idx_source_relations_from
JOIN source_relations goRel INDEXED BY idx_source_relations_from
  ON goRel.from_file = child.to_file
 AND goRel.from_path_id = child.to_path_id
 AND goRel.relation = 'component.gameObject'
WHERE child.from_file = $transformFile
  AND child.from_path_id = $transformPathId
  AND child.relation = 'transform.child';");
            command.Parameters.AddWithValue("$transformFile", rootTransform.SerializedFile ?? string.Empty);
            command.Parameters.AddWithValue("$transformPathId", rootTransform.PathId);

            var rows = new List<SourceObjectKey>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new SourceObjectKey(ReadString(reader, 0), ReadLong(reader, 1)));
            }

            return rows;
        }

        private static List<ComponentRow> LoadComponentsForGameObject(SqliteConnection connection, SourceObjectKey gameObject)
        {
            using var command = connection.CreateCommand();
            command.CommandText = ApplyOptionalIndexHints(connection, @"
SELECT comp.type, comp.serialized_file, comp.path_id
FROM source_relations rel INDEXED BY idx_source_relations_from
JOIN source_objects comp
  ON comp.serialized_file = rel.to_file
 AND comp.path_id = rel.to_path_id
WHERE rel.from_file = $gameObjectFile
  AND rel.from_path_id = $gameObjectPathId
  AND rel.relation = 'gameObject.component';");
            command.Parameters.AddWithValue("$gameObjectFile", gameObject.SerializedFile ?? string.Empty);
            command.Parameters.AddWithValue("$gameObjectPathId", gameObject.PathId);

            var rows = new List<ComponentRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new ComponentRow(ReadString(reader, 0), ReadString(reader, 1), ReadLong(reader, 2)));
            }

            return rows;
        }

        private static long CountDistinctComponentTargets(SqliteConnection connection, IReadOnlyList<ComponentRow> components, string relation)
        {
            var targets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var component in components ?? Array.Empty<ComponentRow>())
            {
                using var command = connection.CreateCommand();
                command.CommandText = ApplyOptionalIndexHints(connection, @"
SELECT DISTINCT rel.to_file, rel.to_path_id
FROM source_relations rel INDEXED BY idx_source_relations_from
WHERE rel.from_file = $componentFile
  AND rel.from_path_id = $componentPathId
  AND rel.relation = $relation;");
                command.Parameters.AddWithValue("$componentFile", component.SerializedFile ?? string.Empty);
                command.Parameters.AddWithValue("$componentPathId", component.PathId);
                command.Parameters.AddWithValue("$relation", relation ?? string.Empty);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    targets.Add(ReadString(reader, 0) + "\n" + ReadLong(reader, 1).ToString(CultureInfo.InvariantCulture));
                }
            }

            return targets.Count;
        }

        private static List<AvatarTosClipDiagnosticRow> LoadAvatarTosClipDiagnosticsForModel(
            SqliteConnection connection,
            ModelRow model,
            string animationSelector,
            int limit)
        {
            var avatars = LoadAnimatorAvatarTosRows(connection, model);
            if (avatars.Count == 0)
            {
                return new List<AvatarTosClipDiagnosticRow>();
            }

            var clipRows = LoadHashOnlyAnimationBindingRows(connection, animationSelector, Math.Clamp(limit * 50, 500, 10000));
            var result = new List<AvatarTosClipDiagnosticRow>();
            foreach (var avatar in avatars)
            {
                foreach (var clip in clipRows)
                {
                    if (!MatchesAnimationSelector(animationSelector, clip.AnimationName))
                    {
                        continue;
                    }

                    var resolved = clip.HashOnlyPathHashes.Count(hash => avatar.HashToPath.ContainsKey(hash));
                    if (resolved == 0)
                    {
                        continue;
                    }

                    result.Add(new AvatarTosClipDiagnosticRow(
                        model.Name,
                        model.SourcePath,
                        model.SerializedFile,
                        model.PathId,
                        model.ContainerPath,
                        avatar.AnimatorName,
                        avatar.AnimatorSerializedFile,
                        avatar.AnimatorPathId,
                        avatar.AvatarName,
                        avatar.AvatarSourcePath,
                        avatar.AvatarSerializedFile,
                        avatar.AvatarPathId,
                        clip.AnimationName,
                        clip.AnimationSourcePath,
                        clip.AnimationSerializedFile,
                        clip.AnimationPathId,
                        clip.BindingCount,
                        clip.HashOnlyPathHashes.Count,
                        resolved,
                        clip.ZeroPathHashBindingCount,
                        BuildResolvedPathSamples(clip.HashOnlyPathHashes, avatar.HashToPath, 8),
                        BuildUnresolvedHashSamples(clip.HashOnlyPathHashes, avatar.HashToPath, 8)));
                }
            }

            return result
                .OrderByDescending(x => Ratio(x.ResolvedPathHashCount, x.UniqueHashOnlyPathHashCount))
                .ThenByDescending(x => x.UniqueHashOnlyPathHashCount)
                .ThenBy(x => x.AnimationName, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();
        }

        private static List<AnimatorAvatarTosRow> LoadAnimatorAvatarTosRows(SqliteConnection connection, ModelRow model)
        {
            using var command = connection.CreateCommand();
            command.CommandText = ApplyOptionalIndexHints(connection, @"
SELECT DISTINCT
    animator.name,
    animator.serialized_file,
    animator.path_id,
    avatar.name,
    avatar.source_path,
    avatar.serialized_file,
    avatar.path_id,
    avatar.raw_json
FROM source_relations goRel INDEXED BY idx_source_relations_from
JOIN source_objects animator
  ON animator.serialized_file = goRel.to_file
 AND animator.path_id = goRel.to_path_id
 AND animator.type = 'Animator'
JOIN source_relations avatarRel INDEXED BY idx_source_relations_from
  ON avatarRel.from_file = animator.serialized_file
 AND avatarRel.from_path_id = animator.path_id
 AND avatarRel.relation = 'animator.avatar'
JOIN source_objects avatar
  ON avatar.serialized_file = avatarRel.to_file
 AND avatar.path_id = avatarRel.to_path_id
 AND avatar.type = 'Avatar'
WHERE goRel.from_file = $modelFile
  AND goRel.from_path_id = $modelPathId
  AND goRel.relation = 'gameObject.component'
ORDER BY avatar.name COLLATE NOCASE;");
            command.Parameters.AddWithValue("$modelFile", model.SerializedFile ?? string.Empty);
            command.Parameters.AddWithValue("$modelPathId", model.PathId);

            var result = new List<AnimatorAvatarTosRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var hashToPath = ExtractAvatarTos(ReadString(reader, 7));
                if (hashToPath.Count == 0)
                {
                    continue;
                }

                result.Add(new AnimatorAvatarTosRow(
                    ReadString(reader, 0),
                    ReadString(reader, 1),
                    ReadLong(reader, 2),
                    ReadString(reader, 3),
                    ReadString(reader, 4),
                    ReadString(reader, 5),
                    ReadLong(reader, 6),
                    hashToPath));
            }

            return result;
        }

        private static List<HashOnlyAnimationBindingRow> LoadHashOnlyAnimationBindingRows(SqliteConnection connection, string animationSelector, int sqlLimit)
        {
            if (!TableExists(connection, "source_animation_bindings"))
            {
                return new List<HashOnlyAnimationBindingRow>();
            }

            var likeToken = ExtractLongestLiteralToken(animationSelector);
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT animation_name, animation_source, animation_file, animation_path_id, binding_count, raw_json
FROM source_animation_bindings
WHERE $likeToken = '' OR animation_name LIKE $like
ORDER BY COALESCE(binding_count, 0) DESC, animation_name COLLATE NOCASE
LIMIT $limit;";
            command.Parameters.AddWithValue("$likeToken", likeToken ?? string.Empty);
            command.Parameters.AddWithValue("$like", "%" + (likeToken ?? string.Empty) + "%");
            command.Parameters.AddWithValue("$limit", Math.Clamp(sqlLimit, 1, 10000));

            var result = new List<HashOnlyAnimationBindingRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var animationName = ReadString(reader, 0);
                if (!MatchesAnimationSelector(animationSelector, animationName))
                {
                    continue;
                }

                var hashes = ExtractHashOnlyBindingPathHashes(ReadString(reader, 5), out var zeroPathHashBindingCount);
                if (hashes.Count == 0)
                {
                    continue;
                }

                result.Add(new HashOnlyAnimationBindingRow(
                    animationName,
                    ReadString(reader, 1),
                    ReadString(reader, 2),
                    ReadLong(reader, 3),
                    ReadLong(reader, 4),
                    hashes,
                    zeroPathHashBindingCount));
            }

            return result;
        }

        private static Dictionary<uint, string> ExtractAvatarTos(string rawJson)
        {
            var result = new Dictionary<uint, string>();
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                return result;
            }

            JObject raw;
            try
            {
                raw = JObject.Parse(rawJson);
            }
            catch (JsonException)
            {
                return result;
            }

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

                result.TryAdd(hash, path);
            }

            return result;
        }

        private static List<AvatarPathSet> LoadAvatarPathSets(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT name, source_path, serialized_file, path_id, raw_json
FROM source_objects
WHERE type = 'Avatar'
ORDER BY name COLLATE NOCASE, path_id
LIMIT 5000;";

            var result = new List<AvatarPathSet>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var paths = ExtractAvatarStructuralPaths(ReadString(reader, 4), out var humanBoneCount, out var tosPathCount);
                if (paths.Count == 0)
                {
                    continue;
                }

                result.Add(new AvatarPathSet(
                    ReadString(reader, 0),
                    ReadString(reader, 1),
                    ReadString(reader, 2),
                    ReadLong(reader, 3),
                    paths,
                    humanBoneCount,
                    tosPathCount));
            }

            return result;
        }

        private static HashSet<string> ExtractAvatarStructuralPaths(string rawJson, out int humanBoneCount, out int tosPathCount)
        {
            humanBoneCount = 0;
            tosPathCount = 0;
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                return result;
            }

            JObject raw;
            try
            {
                raw = JObject.Parse(rawJson);
            }
            catch (JsonException)
            {
                return result;
            }

            humanBoneCount = ReadIntToken(raw.SelectToken("avatar.humanBoneCount"));
            var tos = raw.SelectToken("avatar.tos") as JArray;
            tosPathCount = CountPathItems(tos);
            AddPathItems(result, tos);
            AddPathItems(result, raw.SelectToken("avatar.oracle.skeleton.nodes") as JArray);
            AddPathItems(result, raw.SelectToken("avatar.oracle.humanSkeleton.nodes") as JArray);
            AddPathItems(result, raw.SelectToken("avatar.skeleton.nodes") as JArray);
            AddPathItems(result, raw.SelectToken("avatar.humanSkeleton.nodes") as JArray);
            return result;
        }

        private static void AddPathItems(HashSet<string> paths, JArray items)
        {
            if (items == null)
            {
                return;
            }

            foreach (var item in items.OfType<JObject>())
            {
                var path = NormalizePathForCompare(item["path"]?.ToString());
                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(path);
                }
            }
        }

        private static int CountPathItems(JArray items)
        {
            if (items == null)
            {
                return 0;
            }

            return items
                .OfType<JObject>()
                .Count(x => !string.IsNullOrWhiteSpace(x["path"]?.ToString()));
        }

        private static int ReadIntToken(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return 0;
            }

            if (token.Type == JTokenType.Integer)
            {
                return token.Value<int>();
            }

            return int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : 0;
        }

        private static HashSet<string> BuildComparablePathVariants(IEnumerable<string> paths)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths ?? Array.Empty<string>())
            {
                var normalized = NormalizePathForCompare(path);
                var parts = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    continue;
                }

                for (var i = 0; i <= parts.Length - 2; i++)
                {
                    result.Add(string.Join("/", parts.Skip(i)));
                }
            }

            return result;
        }

        private static string NormalizePathForCompare(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Replace('\\', '/').Trim();
            while (normalized.Contains("//", StringComparison.Ordinal))
            {
                normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
            }

            return normalized.Trim('/');
        }

        private static int CountPathSegments(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return 0;
            }

            return value.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        private static string CombineUnityPath(string parent, string child)
        {
            parent = NormalizePathForCompare(parent);
            child = NormalizePathForCompare(child);
            if (string.IsNullOrWhiteSpace(parent))
            {
                return child;
            }

            return string.IsNullOrWhiteSpace(child) ? parent : parent + "/" + child;
        }

        private static JArray BuildSampleArray(IEnumerable<string> values, int limit)
        {
            return new JArray((values ?? Array.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(0, limit)));
        }

        private static HashSet<uint> ExtractHashOnlyBindingPathHashes(string rawJson, out long zeroPathHashBindingCount)
        {
            zeroPathHashBindingCount = 0;
            var result = new HashSet<uint>();
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                return result;
            }

            JObject raw;
            try
            {
                raw = JObject.Parse(rawJson);
            }
            catch (JsonException)
            {
                return result;
            }

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

        private static JArray BuildResolvedPathSamples(HashSet<uint> hashes, IReadOnlyDictionary<uint, string> hashToPath, int limit)
        {
            return new JArray(hashes
                .OrderBy(x => x)
                .Where(hash => hashToPath.ContainsKey(hash))
                .Take(Math.Max(0, limit))
                .Select(hash => new JObject
                {
                    ["pathHash"] = hash,
                    ["pathHashHex"] = $"0x{hash:X8}",
                    ["path"] = hashToPath[hash],
                }));
        }

        private static JArray BuildUnresolvedHashSamples(HashSet<uint> hashes, IReadOnlyDictionary<uint, string> hashToPath, int limit)
        {
            return new JArray(hashes
                .OrderBy(x => x)
                .Where(hash => !hashToPath.ContainsKey(hash))
                .Take(Math.Max(0, limit))
                .Select(hash => new JObject
                {
                    ["pathHash"] = hash,
                    ["pathHashHex"] = $"0x{hash:X8}",
                }));
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

        private static bool TableExists(SqliteConnection connection, string tableName)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
            command.Parameters.AddWithValue("$name", tableName);
            return Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture) > 0;
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
  ON goRel.to_file = animator.serialized_file
 AND goRel.to_path_id = animator.path_id
 AND goRel.relation = 'gameObject.component'
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
  AND goRel.from_file = $modelFile
  AND goRel.from_path_id = $modelPathId
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
  ON goRel.to_file = animation.serialized_file
 AND goRel.to_path_id = animation.path_id
 AND goRel.relation = 'gameObject.component'
JOIN source_relations clipRel INDEXED BY idx_source_relations_from
  ON clipRel.from_file = animation.serialized_file
 AND clipRel.from_path_id = animation.path_id
 AND clipRel.relation = 'animation.clip'
JOIN source_objects clip
  ON clip.serialized_file = clipRel.to_file
 AND clip.path_id = clipRel.to_path_id
WHERE animation.type = 'Animation'
  AND goRel.from_file = $modelFile
  AND goRel.from_path_id = $modelPathId
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

        private static bool MatchesAnimationSelector(string selector, string animationName)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                return true;
            }

            return Regex.IsMatch(animationName ?? string.Empty, selector, RegexOptions.IgnoreCase);
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

        private static JObject ToJson(ScriptAnimationComponentDiagnosticRow row)
        {
            return new JObject
            {
                ["deterministic"] = true,
                ["diagnosticOnly"] = true,
                ["notDefaultModelAnimationRelation"] = true,
                ["relationKind"] = "monoBehaviour.componentAnimationClipPPtr",
                ["modelReadyForAnimation"] = false,
                ["modelReadyForAnimationReason"] = "This row only proves a local script field points to an AnimationClip. It does not prove the selected model is valid or that the script clip drives the visible model.",
                ["model"] = new JObject
                {
                    ["name"] = row.ModelName,
                    ["sourcePath"] = row.ModelSourcePath,
                    ["serializedFile"] = row.ModelSerializedFile,
                    ["pathId"] = row.ModelPathId,
                    ["containerPath"] = row.ModelContainerPath,
                },
                ["gameObject"] = new JObject
                {
                    ["name"] = row.GameObjectName,
                    ["serializedFile"] = row.GameObjectSerializedFile,
                    ["pathId"] = row.GameObjectPathId,
                    ["depthFromSelectedModel"] = row.GameObjectDepth,
                    ["visibleRendererCount"] = row.VisibleRendererCount,
                    ["animatorCount"] = row.AnimatorCount,
                    ["rectTransformCount"] = row.RectTransformCount,
                    ["subtree"] = new JObject
                    {
                        ["rule"] = "该子树统计包含当前脚本节点和受限深度后代，只用于判断脚本动画线索附近是否有可见对象；它不证明 AnimationClip 会驱动这些 Renderer。",
                        ["gameObjectCount"] = row.SubtreeGameObjectCount,
                        ["visibleRendererCount"] = row.SubtreeMeshRendererCount + row.SubtreeSkinnedMeshRendererCount,
                        ["meshRendererCount"] = row.SubtreeMeshRendererCount,
                        ["skinnedMeshRendererCount"] = row.SubtreeSkinnedMeshRendererCount,
                        ["animatorCount"] = row.SubtreeAnimatorCount,
                        ["rectTransformCount"] = row.SubtreeRectTransformCount,
                        ["maxDepth"] = row.SubtreeMaxDepth,
                        ["truncated"] = row.SubtreeTruncated,
                        ["visibleGameObjectSamples"] = new JArray(row.SubtreeVisibleGameObjectSamples ?? Array.Empty<string>()),
                    },
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
                    ["simpleAnimationRole"] = GetSimpleAnimationRole(row.ScriptName, row.ReferenceFieldPath),
                    ["animation"] = new JObject
                    {
                        ["name"] = row.AnimationName,
                        ["sourcePath"] = row.AnimationSourcePath,
                        ["serializedFile"] = row.AnimationSerializedFile,
                        ["pathId"] = row.AnimationPathId,
                    },
                },
                ["rule"] = "This diagnostic is intentionally limited to MonoBehaviour components on the selected GameObject and direct children. The bounded subtree summary is context only; it does not promote script semantics to production animation bindings.",
            };
        }

        private static JObject BuildScriptAnimationComponentDiagnosticSummary(IReadOnlyList<ScriptAnimationComponentDiagnosticRow> rows)
        {
            rows ??= Array.Empty<ScriptAnimationComponentDiagnosticRow>();
            var topScripts = rows
                .GroupBy(x => string.IsNullOrWhiteSpace(x.ScriptName) ? "<unknown>" : x.ScriptName, StringComparer.OrdinalIgnoreCase)
                .Select(x => new JObject
                {
                    ["scriptName"] = x.Key,
                    ["rowCount"] = x.Count(),
                    ["clipCount"] = x.Select(y => y.AnimationSerializedFile + ":" + y.AnimationPathId.ToString(CultureInfo.InvariantCulture)).Distinct(StringComparer.Ordinal).Count(),
                    ["gameObjectCount"] = x.Select(y => y.GameObjectSerializedFile + ":" + y.GameObjectPathId.ToString(CultureInfo.InvariantCulture)).Distinct(StringComparer.Ordinal).Count(),
                })
                .OrderByDescending(x => x["rowCount"]?.Value<int>() ?? 0)
                .ThenBy(x => x["scriptName"]?.ToString(), StringComparer.OrdinalIgnoreCase)
                .Take(12);
            var topFields = rows
                .GroupBy(x => string.IsNullOrWhiteSpace(x.ReferenceFieldPath) ? "<unknown>" : x.ReferenceFieldPath, StringComparer.OrdinalIgnoreCase)
                .Select(x => new JObject
                {
                    ["fieldPath"] = x.Key,
                    ["rowCount"] = x.Count(),
                })
                .OrderByDescending(x => x["rowCount"]?.Value<int>() ?? 0)
                .ThenBy(x => x["fieldPath"]?.ToString(), StringComparer.OrdinalIgnoreCase)
                .Take(12);
            var animationSamples = rows
                .Select(x => x.AnimationName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Take(16);

            var subtreeVisibleRows = rows.Count(x => x.SubtreeMeshRendererCount + x.SubtreeSkinnedMeshRendererCount > 0);
            return new JObject
            {
                ["status"] = rows.Count == 0 ? "empty" : "diagnosticOnly",
                ["rowCount"] = rows.Count,
                ["diagnosticOnly"] = true,
                ["notDefaultModelAnimationRelation"] = true,
                ["defaultCandidateCount"] = 0,
                ["productionReadiness"] = "blocked",
                ["blockedProductionRequirements"] = new JArray(
                    "scriptSemantics",
                    "validatedModelGltf",
                    "animationTrsExport",
                    "visualReview"),
                ["localVisibleRendererRows"] = rows.Count(x => x.VisibleRendererCount > 0),
                ["localAnimatorRows"] = rows.Count(x => x.AnimatorCount > 0),
                ["subtreeVisibleRendererRows"] = subtreeVisibleRows,
                ["subtreeSkinnedRendererRows"] = rows.Count(x => x.SubtreeSkinnedMeshRendererCount > 0),
                ["subtreeAnimatorRows"] = rows.Count(x => x.SubtreeAnimatorCount > 0),
                ["subtreeTruncatedRows"] = rows.Count(x => x.SubtreeTruncated),
                ["maxSubtreeDepth"] = rows.Count == 0 ? 0 : rows.Max(x => x.SubtreeMaxDepth),
                ["scriptNameCounts"] = new JArray(topScripts),
                ["fieldPathCounts"] = new JArray(topFields),
                ["animationNameSamples"] = new JArray(animationSamples.Select(x => new JValue(x))),
                ["simpleAnimationSemanticSummary"] = BuildSimpleAnimationSemanticSummary(rows),
                ["rule"] = "MonoBehaviour -> AnimationClip PPtr rows are explicit script-field evidence only. They remain diagnosticOnly/notDefaultModelAnimationRelation until script semantics, model validation, TRS export and visual review are proven.",
            };
        }

        private static JObject BuildSimpleAnimationSemanticSummary(IReadOnlyList<ScriptAnimationComponentDiagnosticRow> rows)
        {
            rows ??= Array.Empty<ScriptAnimationComponentDiagnosticRow>();
            // 只解释 Unity SimpleAnimation 的稳定字段角色，不在这里推断运行时播放状态。
            var simpleRows = rows
                .Where(x => string.Equals(x.ScriptName, "SimpleAnimation", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var knownRows = simpleRows
                .Where(x => !string.IsNullOrWhiteSpace(GetSimpleAnimationRole(x.ScriptName, x.ReferenceFieldPath)))
                .ToList();
            var groupedByComponentClip = knownRows
                .GroupBy(x => string.Join("\n",
                    x.MonoBehaviourSerializedFile ?? string.Empty,
                    x.MonoBehaviourPathId.ToString(CultureInfo.InvariantCulture),
                    x.AnimationSerializedFile ?? string.Empty,
                    x.AnimationPathId.ToString(CultureInfo.InvariantCulture)), StringComparer.Ordinal)
                .ToList();
            var pairedRows = groupedByComponentClip
                .Where(x =>
                    x.Any(y => string.Equals(GetSimpleAnimationRole(y.ScriptName, y.ReferenceFieldPath), "defaultClip", StringComparison.Ordinal)) &&
                    x.Any(y => string.Equals(GetSimpleAnimationRole(y.ScriptName, y.ReferenceFieldPath), "stateClip", StringComparison.Ordinal)))
                .ToList();
            var defaultOnlyRows = groupedByComponentClip
                .Where(x =>
                    x.Any(y => string.Equals(GetSimpleAnimationRole(y.ScriptName, y.ReferenceFieldPath), "defaultClip", StringComparison.Ordinal)) &&
                    !x.Any(y => string.Equals(GetSimpleAnimationRole(y.ScriptName, y.ReferenceFieldPath), "stateClip", StringComparison.Ordinal)))
                .ToList();
            var stateOnlyRows = groupedByComponentClip
                .Where(x =>
                    !x.Any(y => string.Equals(GetSimpleAnimationRole(y.ScriptName, y.ReferenceFieldPath), "defaultClip", StringComparison.Ordinal)) &&
                    x.Any(y => string.Equals(GetSimpleAnimationRole(y.ScriptName, y.ReferenceFieldPath), "stateClip", StringComparison.Ordinal)))
                .ToList();

            return new JObject
            {
                ["status"] = simpleRows.Count == 0 ? "empty" : "diagnosticOnly",
                ["diagnosticOnly"] = true,
                ["notDefaultModelAnimationRelation"] = true,
                ["defaultCandidateCount"] = 0,
                ["simpleAnimationRows"] = simpleRows.Count,
                ["knownFieldRows"] = knownRows.Count,
                ["componentCount"] = simpleRows
                    .Select(x => x.MonoBehaviourSerializedFile + ":" + x.MonoBehaviourPathId.ToString(CultureInfo.InvariantCulture))
                    .Distinct(StringComparer.Ordinal)
                    .Count(),
                ["defaultClipRows"] = simpleRows.Count(x => string.Equals(GetSimpleAnimationRole(x.ScriptName, x.ReferenceFieldPath), "defaultClip", StringComparison.Ordinal)),
                ["stateClipRows"] = simpleRows.Count(x => string.Equals(GetSimpleAnimationRole(x.ScriptName, x.ReferenceFieldPath), "stateClip", StringComparison.Ordinal)),
                ["pairedDefaultStateClipRows"] = pairedRows.Count,
                ["defaultOnlyRows"] = defaultOnlyRows.Count,
                ["stateOnlyRows"] = stateOnlyRows.Count,
                ["unresolvedFieldRows"] = simpleRows.Count - knownRows.Count,
                ["pairedClipSamples"] = new JArray(BuildSimpleAnimationPairedClipSamples(pairedRows)),
                ["productionReadiness"] = "blocked",
                ["blockedProductionRequirements"] = new JArray(
                    "scriptRuntimeSemantics",
                    "selectedStateOrPlayCall",
                    "validatedModelGltf",
                    "animationTrsExport",
                    "visualReview"),
                ["rule"] = "SimpleAnimation public source shows m_Clip is the default clip and m_States.data.clip are state clips played through an Animator-backed PlayableGraph. This summary only explains script-field semantics; it does not prove which state is selected at runtime or create a production model-animation binding.",
            };
        }

        private static IEnumerable<JObject> BuildSimpleAnimationPairedClipSamples(IEnumerable<IGrouping<string, ScriptAnimationComponentDiagnosticRow>> pairedRows)
        {
            return (pairedRows ?? Array.Empty<IGrouping<string, ScriptAnimationComponentDiagnosticRow>>())
                .Select(group =>
                {
                    var first = group
                        .OrderBy(x => x.AnimationName, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(x => x.MonoBehaviourPathId)
                        .First();
                    var roles = group
                        .Select(x => GetSimpleAnimationRole(x.ScriptName, x.ReferenceFieldPath))
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(x => x, StringComparer.Ordinal)
                        .ToArray();
                    return new JObject
                    {
                        ["modelName"] = first.ModelName,
                        ["modelSerializedFile"] = first.ModelSerializedFile,
                        ["modelPathId"] = first.ModelPathId,
                        ["modelPathIdString"] = first.ModelPathId.ToString(CultureInfo.InvariantCulture),
                        ["gameObjectName"] = first.GameObjectName,
                        ["gameObjectSerializedFile"] = first.GameObjectSerializedFile,
                        ["gameObjectPathId"] = first.GameObjectPathId,
                        ["gameObjectPathIdString"] = first.GameObjectPathId.ToString(CultureInfo.InvariantCulture),
                        ["monoBehaviourSerializedFile"] = first.MonoBehaviourSerializedFile,
                        ["monoBehaviourPathId"] = first.MonoBehaviourPathId,
                        ["monoBehaviourPathIdString"] = first.MonoBehaviourPathId.ToString(CultureInfo.InvariantCulture),
                        ["clipName"] = first.AnimationName,
                        ["clipSourcePath"] = first.AnimationSourcePath,
                        ["clipSerializedFile"] = first.AnimationSerializedFile,
                        ["clipPathId"] = first.AnimationPathId,
                        ["clipPathIdString"] = first.AnimationPathId.ToString(CultureInfo.InvariantCulture),
                        ["roles"] = new JArray(roles.Select(x => new JValue(x))),
                        ["diagnosticOnly"] = true,
                        ["notDefaultModelAnimationRelation"] = true,
                    };
                })
                .OrderBy(x => x["clipName"]?.ToString(), StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x["monoBehaviourPathId"]?.Value<long>() ?? 0)
                .Take(8);
        }

        private static string GetSimpleAnimationRole(string scriptName, string fieldPath)
        {
            if (!string.Equals(scriptName, "SimpleAnimation", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return fieldPath switch
            {
                "m_Clip" => "defaultClip",
                "m_States.data.clip" => "stateClip",
                _ => string.Empty,
            };
        }

        // 这些摘要只方便 smoke 和审计读取诊断边界，不把结构线索升级成默认动画候选。
        private static JObject BuildAvatarTosClipDiagnosticSummary(IReadOnlyList<AvatarTosClipDiagnosticRow> rows)
        {
            rows ??= Array.Empty<AvatarTosClipDiagnosticRow>();
            var topAvatars = rows
                .GroupBy(x => string.IsNullOrWhiteSpace(x.AvatarName) ? "<unknown>" : x.AvatarName, StringComparer.OrdinalIgnoreCase)
                .Select(x => new JObject
                {
                    ["avatarName"] = x.Key,
                    ["rowCount"] = x.Count(),
                    ["clipCount"] = x.Select(y => y.AnimationSerializedFile + ":" + y.AnimationPathId.ToString(CultureInfo.InvariantCulture)).Distinct(StringComparer.Ordinal).Count(),
                    ["maxCoverageRatio"] = x.Max(y => Ratio(y.ResolvedPathHashCount, y.UniqueHashOnlyPathHashCount)),
                })
                .OrderByDescending(x => x["rowCount"]?.Value<int>() ?? 0)
                .ThenByDescending(x => x["maxCoverageRatio"]?.Value<double>() ?? 0)
                .ThenBy(x => x["avatarName"]?.ToString(), StringComparer.OrdinalIgnoreCase)
                .Take(12);
            var animationSamples = rows
                .Select(x => x.AnimationName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Take(16);

            var maxCoverage = rows.Count == 0
                ? 0.0
                : rows.Max(x => Ratio(x.ResolvedPathHashCount, x.UniqueHashOnlyPathHashCount));
            return new JObject
            {
                ["status"] = rows.Count == 0 ? "empty" : "diagnosticOnly",
                ["rowCount"] = rows.Count,
                ["diagnosticOnly"] = true,
                ["manualReviewRequired"] = rows.Count > 0,
                ["notDefaultModelAnimationRelation"] = true,
                ["defaultCandidateCount"] = 0,
                ["modelReadyForAnimation"] = false,
                ["productionReadiness"] = "blocked",
                ["blockedProductionRequirements"] = new JArray(
                    "explicitAnimatorControllerOrAnimationClipRelation",
                    "validatedModelGltf",
                    "animationTrsExport",
                    "visualReview"),
                ["distinctModelCount"] = rows.Select(x => x.ModelSerializedFile + ":" + x.ModelPathId.ToString(CultureInfo.InvariantCulture)).Distinct(StringComparer.Ordinal).Count(),
                ["distinctAvatarCount"] = rows.Select(x => x.AvatarSerializedFile + ":" + x.AvatarPathId.ToString(CultureInfo.InvariantCulture)).Distinct(StringComparer.Ordinal).Count(),
                ["distinctClipCount"] = rows.Select(x => x.AnimationSerializedFile + ":" + x.AnimationPathId.ToString(CultureInfo.InvariantCulture)).Distinct(StringComparer.Ordinal).Count(),
                ["uniqueHashOnlyPathHashCount"] = rows.Sum(x => x.UniqueHashOnlyPathHashCount),
                ["resolvedPathHashCount"] = rows.Sum(x => x.ResolvedPathHashCount),
                ["zeroPathHashBindingCount"] = rows.Sum(x => x.ZeroPathHashBindingCount),
                ["maxCoverageRatio"] = maxCoverage,
                ["fullCoverageRows"] = rows.Count(x => x.UniqueHashOnlyPathHashCount > 0 && x.ResolvedPathHashCount == x.UniqueHashOnlyPathHashCount),
                ["unresolvedHashRows"] = rows.Count(x => x.UniqueHashOnlyPathHashCount > x.ResolvedPathHashCount),
                ["avatarNameCounts"] = new JArray(topAvatars),
                ["animationNameSamples"] = new JArray(animationSamples.Select(x => new JValue(x))),
                ["rule"] = "Animator.avatar -> Avatar.m_TOS rows only prove hash-only binding path recovery evidence. They remain diagnosticOnly/notDefaultModelAnimationRelation until explicit Animator/Controller context, model validation, TRS export and visual review are proven.",
            };
        }

        private static JObject BuildModelAvatarCompatibilityDiagnosticSummary(IReadOnlyList<ModelAvatarCompatibilityDiagnosticRow> rows)
        {
            rows ??= Array.Empty<ModelAvatarCompatibilityDiagnosticRow>();
            var reasonCounts = rows
                .GroupBy(x => string.IsNullOrWhiteSpace(x.Reason) ? "<unknown>" : x.Reason, StringComparer.OrdinalIgnoreCase)
                .Select(x => new JObject
                {
                    ["reason"] = x.Key,
                    ["rowCount"] = x.Count(),
                })
                .OrderByDescending(x => x["rowCount"]?.Value<int>() ?? 0)
                .ThenBy(x => x["reason"]?.ToString(), StringComparer.OrdinalIgnoreCase)
                .Take(12);
            var topAvatars = rows
                .GroupBy(x => string.IsNullOrWhiteSpace(x.AvatarName) ? "<unknown>" : x.AvatarName, StringComparer.OrdinalIgnoreCase)
                .Select(x => new JObject
                {
                    ["avatarName"] = x.Key,
                    ["rowCount"] = x.Count(),
                    ["maxCoverageRatio"] = x.Max(y => y.CoverageRatio),
                    ["maxMatchedAvatarPathCount"] = x.Max(y => y.MatchedAvatarPathCount),
                })
                .OrderByDescending(x => x["maxCoverageRatio"]?.Value<double>() ?? 0)
                .ThenByDescending(x => x["maxMatchedAvatarPathCount"]?.Value<int>() ?? 0)
                .ThenBy(x => x["avatarName"]?.ToString(), StringComparer.OrdinalIgnoreCase)
                .Take(12);
            var modelSamples = rows
                .Select(x => x.ModelName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Take(16);

            return new JObject
            {
                ["status"] = rows.Count == 0 ? "empty" : "diagnosticOnly",
                ["rowCount"] = rows.Count,
                ["diagnosticOnly"] = true,
                ["manualReviewRequired"] = rows.Count > 0,
                ["notDefaultModelAnimationRelation"] = true,
                ["defaultCandidateCount"] = 0,
                ["modelReadyForAnimation"] = false,
                ["productionReadiness"] = "blocked",
                ["blockedProductionRequirements"] = new JArray(
                    "explicitAnimationClipRelation",
                    "validatedModelGltf",
                    "animationTrsExport",
                    "visualReview"),
                ["distinctModelCount"] = rows.Select(x => x.ModelSerializedFile + ":" + x.ModelPathId.ToString(CultureInfo.InvariantCulture)).Distinct(StringComparer.Ordinal).Count(),
                ["distinctAvatarCount"] = rows.Select(x => x.AvatarSerializedFile + ":" + x.AvatarPathId.ToString(CultureInfo.InvariantCulture)).Distinct(StringComparer.Ordinal).Count(),
                ["visibleRendererRows"] = rows.Count(x => x.VisibleRendererCount > 0),
                ["highOverlapRows"] = rows.Count(x => x.CoverageRatio >= 0.9),
                ["fullOverlapRows"] = rows.Count(x => x.ComparableAvatarPathCount > 0 && x.MatchedAvatarPathCount == x.ComparableAvatarPathCount),
                ["maxCoverageRatio"] = rows.Count == 0 ? 0.0 : rows.Max(x => x.CoverageRatio),
                ["maxMatchedAvatarPathCount"] = rows.Count == 0 ? 0 : rows.Max(x => x.MatchedAvatarPathCount),
                ["maxComparableAvatarPathCount"] = rows.Count == 0 ? 0 : rows.Max(x => x.ComparableAvatarPathCount),
                ["avatarNameCounts"] = new JArray(topAvatars),
                ["reasonCounts"] = new JArray(reasonCounts),
                ["modelNameSamples"] = new JArray(modelSamples.Select(x => new JValue(x))),
                ["rule"] = "Model/Avatar structural path overlap is a bounded source-index diagnostic. High overlap can guide a manual solver/oracle probe, but it must not create default model-animation relations without Unity explicit context, model validation, TRS export and visual review.",
            };
        }

        private static JObject ToJson(AvatarTosClipDiagnosticRow row)
        {
            return new JObject
            {
                ["deterministic"] = true,
                ["diagnosticOnly"] = true,
                ["manualReviewRequired"] = true,
                ["notDefaultModelAnimationRelation"] = true,
                ["relationKind"] = "animator.avatar_tos_pathHash_coverage",
                ["relationSource"] = "structural_source_index",
                ["modelReadyForAnimation"] = false,
                ["modelReadyForAnimationReason"] = "This diagnostic cannot prove model quality or animation semantics. Export and validate the model before any forced preview.",
                ["coverage"] = new JObject
                {
                    ["uniqueHashOnlyPathHashCount"] = row.UniqueHashOnlyPathHashCount,
                    ["resolvedPathHashCount"] = row.ResolvedPathHashCount,
                    ["coverageRatio"] = Ratio(row.ResolvedPathHashCount, row.UniqueHashOnlyPathHashCount),
                    ["zeroPathHashBindingCount"] = row.ZeroPathHashBindingCount,
                    ["resolvedPathSamples"] = row.ResolvedPathSamples ?? new JArray(),
                    ["unresolvedHashSamples"] = row.UnresolvedHashSamples ?? new JArray(),
                },
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
                },
                ["avatar"] = new JObject
                {
                    ["name"] = row.AvatarName,
                    ["sourcePath"] = row.AvatarSourcePath,
                    ["serializedFile"] = row.AvatarSerializedFile,
                    ["pathId"] = row.AvatarPathId,
                },
                ["animation"] = new JObject
                {
                    ["name"] = row.AnimationName,
                    ["sourcePath"] = row.AnimationSourcePath,
                    ["serializedFile"] = row.AnimationSerializedFile,
                    ["pathId"] = row.AnimationPathId,
                    ["bindingCount"] = row.BindingCount,
                },
                ["rule"] = "Animator.avatar -> Avatar.m_TOS can resolve hash-only AnimationClip paths, but this is structural compatibility only. It must not be written as a default model-animation relation without Animator/Controller context, model validation, TRS export, and visual review.",
            };
        }

        private static JObject ToJson(ModelVisibilityDiagnosticRow row)
        {
            var visibleRendererCount = row.MeshRendererCount + row.SkinnedMeshRendererCount;
            return new JObject
            {
                ["deterministic"] = true,
                ["diagnosticOnly"] = true,
                ["visibleModelLike"] = visibleRendererCount > 0,
                ["visibleRendererCount"] = visibleRendererCount,
                ["model"] = new JObject
                {
                    ["name"] = row.ModelName,
                    ["sourcePath"] = row.ModelSourcePath,
                    ["serializedFile"] = row.ModelSerializedFile,
                    ["pathId"] = row.ModelPathId,
                    ["containerPath"] = row.ModelContainerPath,
                },
                ["hierarchy"] = new JObject
                {
                    ["gameObjectCount"] = row.HierarchyGameObjectCount,
                    ["transformCount"] = row.TransformCount,
                    ["meshRendererCount"] = row.MeshRendererCount,
                    ["skinnedMeshRendererCount"] = row.SkinnedMeshRendererCount,
                    ["meshFilterCount"] = row.MeshFilterCount,
                    ["animatorCount"] = row.AnimatorCount,
                    ["animationComponentCount"] = row.AnimationComponentCount,
                    ["animatorAvatarReferenceCount"] = row.AnimatorAvatarReferenceCount,
                    ["animatorControllerReferenceCount"] = row.AnimatorControllerReferenceCount,
                },
                ["modelReadyForAnimation"] = false,
                ["modelReadyForAnimationReason"] = visibleRendererCount <= 0
                    ? "Selected GameObject hierarchy has no MeshRenderer/SkinnedMeshRenderer in the source index; treat it as skeleton/config diagnostic until a visible model export passes validation."
                    : "Visible renderer components exist, but this source-index diagnostic still cannot prove glTF mesh/material/texture/skin/bbox quality.",
                ["rule"] = "This diagnostic only checks source-index hierarchy components. It cannot replace model_validation.json, glTF validator, material/texture checks, or visual screenshots.",
            };
        }

        private static JObject ToJson(ModelAvatarCompatibilityDiagnosticRow row)
        {
            return new JObject
            {
                ["deterministic"] = true,
                ["diagnosticOnly"] = true,
                ["manualReviewRequired"] = true,
                ["notDefaultModelAnimationRelation"] = true,
                ["relationKind"] = "model_avatar_structural_path_overlap",
                ["relationSource"] = "structural_source_index",
                ["reason"] = row.Reason,
                ["modelReadyForAnimation"] = false,
                ["modelReadyForAnimationReason"] = "This diagnostic only compares source-index Transform paths with Avatar TOS/oracle paths. It cannot replace explicit Animator/Controller relation, exported glTF validation, TRS export, or visual review.",
                ["coverage"] = new JObject
                {
                    ["modelPathCount"] = row.ModelPathCount,
                    ["modelComparablePathVariantCount"] = row.ModelComparablePathVariantCount,
                    ["comparableAvatarPathCount"] = row.ComparableAvatarPathCount,
                    ["matchedAvatarPathCount"] = row.MatchedAvatarPathCount,
                    ["coverageRatio"] = row.CoverageRatio,
                    ["avatarHumanBoneCount"] = row.AvatarHumanBoneCount,
                    ["avatarTosPathCount"] = row.AvatarTosPathCount,
                    ["visibleRendererCount"] = row.VisibleRendererCount,
                    ["hierarchyPathCount"] = row.HierarchyPathCount,
                    ["transformNodePathCount"] = row.TransformNodePathCount,
                    ["modelPathSamples"] = row.ModelPathSamples ?? new JArray(),
                    ["matchedAvatarPathSamples"] = row.MatchedAvatarPathSamples ?? new JArray(),
                    ["unmatchedAvatarPathSamples"] = row.UnmatchedAvatarPathSamples ?? new JArray(),
                },
                ["model"] = new JObject
                {
                    ["name"] = row.ModelName,
                    ["sourcePath"] = row.ModelSourcePath,
                    ["serializedFile"] = row.ModelSerializedFile,
                    ["pathId"] = row.ModelPathId,
                    ["containerPath"] = row.ModelContainerPath,
                },
                ["avatar"] = new JObject
                {
                    ["name"] = row.AvatarName,
                    ["sourcePath"] = row.AvatarSourcePath,
                    ["serializedFile"] = row.AvatarSerializedFile,
                    ["pathId"] = row.AvatarPathId,
                },
                ["rule"] = "High overlap can guide a manual Naraka Humanoid solver/oracle probe, but it must not be written into model_animations.json as a recommended relation unless Unity explicit context or a reviewed profile rule later proves the binding.",
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

        private static double Ratio(long part, long total)
        {
            return total <= 0 ? 0.0 : Math.Round((double)part / total, 6);
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

        private sealed record ScriptAnimationComponentDiagnosticRow(
            string ModelName,
            string ModelSourcePath,
            string ModelSerializedFile,
            long ModelPathId,
            string ModelContainerPath,
            string GameObjectSerializedFile,
            long GameObjectPathId,
            string GameObjectName,
            int GameObjectDepth,
            int VisibleRendererCount,
            int AnimatorCount,
            int RectTransformCount,
            int SubtreeGameObjectCount,
            int SubtreeMeshRendererCount,
            int SubtreeSkinnedMeshRendererCount,
            int SubtreeAnimatorCount,
            int SubtreeRectTransformCount,
            int SubtreeMaxDepth,
            bool SubtreeTruncated,
            IReadOnlyList<string> SubtreeVisibleGameObjectSamples,
            string MonoBehaviourSerializedFile,
            long MonoBehaviourPathId,
            string MonoBehaviourName,
            string MonoBehaviourSourcePath,
            string ScriptName,
            string ReferenceFieldPath,
            string ReferenceFieldName,
            string AnimationName,
            string AnimationSourcePath,
            string AnimationSerializedFile,
            long AnimationPathId);

        private sealed record ScriptAnimationSubtreeVisibility(
            int GameObjectCount,
            int MeshRendererCount,
            int SkinnedMeshRendererCount,
            int AnimatorCount,
            int RectTransformCount,
            int MaxDepth,
            bool Truncated,
            IReadOnlyList<string> VisibleGameObjectSamples);

        private sealed record ModelVisibilityDiagnosticRow(
            string ModelName,
            string ModelSourcePath,
            string ModelSerializedFile,
            long ModelPathId,
            string ModelContainerPath,
            long HierarchyGameObjectCount,
            long TransformCount,
            long MeshRendererCount,
            long SkinnedMeshRendererCount,
            long MeshFilterCount,
            long AnimatorCount,
            long AnimationComponentCount,
            long AnimatorAvatarReferenceCount,
            long AnimatorControllerReferenceCount);

        private sealed record ModelAvatarCompatibilityDiagnosticRow(
            string ModelName,
            string ModelSourcePath,
            string ModelSerializedFile,
            long ModelPathId,
            string ModelContainerPath,
            string AvatarName,
            string AvatarSourcePath,
            string AvatarSerializedFile,
            long AvatarPathId,
            int ModelPathCount,
            int ModelComparablePathVariantCount,
            int ComparableAvatarPathCount,
            int MatchedAvatarPathCount,
            double CoverageRatio,
            int AvatarHumanBoneCount,
            int AvatarTosPathCount,
            long VisibleRendererCount,
            int HierarchyPathCount,
            int TransformNodePathCount,
            string Reason,
            JArray ModelPathSamples,
            JArray MatchedAvatarPathSamples,
            JArray UnmatchedAvatarPathSamples);

        private sealed record ModelTransformEvidence(
            List<string> Paths,
            int HierarchyPathCount,
            int TransformNodePathCount);

        private sealed record AvatarPathSet(
            string Name,
            string SourcePath,
            string SerializedFile,
            long PathId,
            HashSet<string> Paths,
            int HumanBoneCount,
            int TosPathCount);

        private sealed record SourceObjectKey(
            string SerializedFile,
            long PathId);

        private sealed record ComponentRow(
            string Type,
            string SerializedFile,
            long PathId);

        private sealed record AnimatorAvatarTosRow(
            string AnimatorName,
            string AnimatorSerializedFile,
            long AnimatorPathId,
            string AvatarName,
            string AvatarSourcePath,
            string AvatarSerializedFile,
            long AvatarPathId,
            Dictionary<uint, string> HashToPath);

        private sealed record HashOnlyAnimationBindingRow(
            string AnimationName,
            string AnimationSourcePath,
            string AnimationSerializedFile,
            long AnimationPathId,
            long BindingCount,
            HashSet<uint> HashOnlyPathHashes,
            long ZeroPathHashBindingCount);

        private sealed record AvatarTosClipDiagnosticRow(
            string ModelName,
            string ModelSourcePath,
            string ModelSerializedFile,
            long ModelPathId,
            string ModelContainerPath,
            string AnimatorName,
            string AnimatorSerializedFile,
            long AnimatorPathId,
            string AvatarName,
            string AvatarSourcePath,
            string AvatarSerializedFile,
            long AvatarPathId,
            string AnimationName,
            string AnimationSourcePath,
            string AnimationSerializedFile,
            long AnimationPathId,
            long BindingCount,
            int UniqueHashOnlyPathHashCount,
            int ResolvedPathHashCount,
            long ZeroPathHashBindingCount,
            JArray ResolvedPathSamples,
            JArray UnresolvedHashSamples);
    }
}
