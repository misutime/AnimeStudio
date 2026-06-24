using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AnimeStudio.CLI
{
    internal static class ModelAnimationCandidateLister
    {
        public static string ListFromLibrary(
            string libraryRoot,
            string modelSelector,
            string animationSelector,
            string outputDirectory,
            int limit,
            string indexPathOverride = null)
        {
            if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
            {
                Logger.Error($"Library root not found: {libraryRoot}");
                return null;
            }
            if (string.IsNullOrWhiteSpace(modelSelector))
            {
                Logger.Error("--list_model_animations_from_library requires --preview_model. 只按已选模型列确定性动画候选，避免全库海量列表误导。");
                return null;
            }

            var dbPath = string.IsNullOrWhiteSpace(indexPathOverride)
                ? Path.Combine(libraryRoot, "library_index.db")
                : indexPathOverride;
            if (!File.Exists(dbPath))
            {
                Logger.Error($"library_index.db not found: {dbPath}. Rebuild the Library export or run --build_sqlite_index.");
                return null;
            }

            limit = limit <= 0 ? 100 : limit;
            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();

            var modelAssets = LoadMatchingAssets(connection, "Model", modelSelector, hardLimit: 64);
            if (modelAssets.Count == 0)
            {
                Logger.Error($"No model matched selector: {modelSelector}");
                return null;
            }

            var animationOutputs = string.IsNullOrWhiteSpace(animationSelector)
                ? null
                : LoadMatchingAssets(connection, "Animation", animationSelector, hardLimit: 2048)
                    .Select(x => x.Output)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var rows = LoadCandidates(connection, modelAssets.Select(x => x.Output).ToArray(), animationOutputs, limit);
            var sourceDiagnostics = rows.Count == 0
                ? LoadSourceRelationDiagnostics(libraryRoot, modelAssets)
                : new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            var models = BuildModelPayload(modelAssets, rows, libraryRoot, sourceDiagnostics).ToList();
            var usableCandidateCount = rows.Count(IsCandidateModelReady);
            var modelGateBlockedCount = rows.Count - usableCandidateCount;
            var payload = new JObject
            {
                ["status"] = "ok",
                ["libraryRoot"] = Path.GetFullPath(libraryRoot),
                ["indexPath"] = Path.GetFullPath(dbPath),
                ["modelSelector"] = modelSelector,
                ["animationSelector"] = animationSelector ?? string.Empty,
                ["limit"] = limit,
                ["matchedModelCount"] = modelAssets.Count,
                ["candidateCount"] = rows.Count,
                ["usableCandidateCount"] = usableCandidateCount,
                ["modelGateBlockedCount"] = modelGateBlockedCount,
                ["rule"] = "Only relation_source=explicit SQLite candidates are listed. This command reads deterministic Unity Animator/Animation/Controller/PPtr relations and does not infer model-animation matches by name, path, or skeleton count.",
                ["modelFirstRule"] = "Explicit relations whose model gate is blocked are diagnostics only. Preview, pack, and standalone animation commands must require modelReadyForAnimation=true.",
                ["zeroCandidateDiagnosticRule"] = rows.Count == 0
                    ? "When a source index is available, sourceRelationDiagnostic explains why no explicit candidate exists. It is diagnostic only and never promotes name/path/skeleton matches into default bindings."
                    : null,
                ["models"] = new JArray(models),
            };

            var outputRoot = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(Path.GetFullPath(libraryRoot), "AnimationCandidateLists", SafeName(modelSelector))
                : outputDirectory;
            Directory.CreateDirectory(outputRoot);
            var reportPath = Path.Combine(outputRoot, "model_animation_candidates.json");
            File.WriteAllText(reportPath, payload.ToString(Formatting.Indented));

            Logger.Info($"Model animation candidate list: {reportPath}");
            foreach (var model in models.Take(8))
            {
                Logger.Info($"- {model["name"]}: {model["usableCandidateCount"]}/{model["candidateCount"]} animation-ready deterministic candidate(s)");
            }

            return reportPath;
        }

        private static List<AssetRow> LoadMatchingAssets(SqliteConnection connection, string kind, string selector, int hardLimit)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT name, output, raw_json
FROM assets
WHERE kind=$kind
ORDER BY name COLLATE NOCASE, output COLLATE NOCASE;";
            command.Parameters.AddWithValue("$kind", kind);

            var result = new List<AssetRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = new AssetRow(
                    ReadString(reader, 0),
                    ReadString(reader, 1),
                    ReadObject(reader, 2));
                if (!Matches(selector, row.Name, row.Output))
                {
                    continue;
                }

                result.Add(row);
                if (result.Count >= hardLimit)
                {
                    break;
                }
            }

            return result;
        }

        private static List<CandidateRow> LoadCandidates(
            SqliteConnection connection,
            string[] modelOutputs,
            HashSet<string> animationOutputs,
            int limit)
        {
            if (modelOutputs.Length == 0)
            {
                return new List<CandidateRow>();
            }

            using var command = connection.CreateCommand();
            var modelParams = new List<string>();
            for (var i = 0; i < modelOutputs.Length; i++)
            {
                var name = "$model" + i;
                modelParams.Add(name);
                command.Parameters.AddWithValue(name, modelOutputs[i]);
            }

            command.CommandText = $@"
SELECT c.model_output, c.animation_output, c.confidence, c.score, c.status, c.needs_validation, c.raw_json, a.name, a.raw_json
FROM model_animation_candidates c
JOIN assets a ON a.kind='Animation' AND a.output=c.animation_output
WHERE c.relation_source='explicit'
  AND c.model_output IN ({string.Join(",", modelParams)})
ORDER BY
  COALESCE(json_extract(c.raw_json, '$.productionAnimationReady'), 0) DESC,
  COALESCE(json_extract(c.raw_json, '$.directGltfAnimationReady'), 0) DESC,
  c.score DESC,
  a.name COLLATE NOCASE
LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", Math.Max(limit * Math.Max(modelOutputs.Length, 1) * 4, limit));

            var result = new List<CandidateRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var animationOutput = ReadString(reader, 1);
                if (animationOutputs != null && !animationOutputs.Contains(animationOutput))
                {
                    continue;
                }

                result.Add(new CandidateRow(
                    ReadString(reader, 0),
                    animationOutput,
                    ReadString(reader, 2),
                    reader.IsDBNull(3) ? 0 : reader.GetDouble(3),
                    ReadString(reader, 4),
                    !reader.IsDBNull(5) && reader.GetInt32(5) != 0,
                    ReadObject(reader, 6),
                    ReadString(reader, 7),
                    ReadObject(reader, 8)));
                if (result.Count >= limit)
                {
                    break;
                }
            }

            return result;
        }

        private static IEnumerable<JObject> BuildModelPayload(
            List<AssetRow> models,
            List<CandidateRow> candidates,
            string libraryRoot,
            Dictionary<string, JObject> sourceDiagnostics)
        {
            var byModel = candidates
                .GroupBy(x => x.ModelOutput, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);

            foreach (var model in models)
            {
                byModel.TryGetValue(model.Output, out var modelCandidates);
                modelCandidates ??= new List<CandidateRow>();
                var usableCandidateCount = modelCandidates.Count(IsCandidateModelReady);
                var item = new JObject
                {
                    ["name"] = model.Name,
                    ["output"] = model.Output,
                    ["gltf"] = ResolveLibraryPath(libraryRoot, model.Output),
                    ["sourceType"] = (string)model.Raw["sourceType"],
                    ["materialStatus"] = (string)model.Raw["materialStatus"],
                    ["materialImageCount"] = model.Raw["materialImageCount"],
                    ["boneCount"] = model.Raw["boneCount"],
                    ["candidateCount"] = modelCandidates.Count,
                    ["usableCandidateCount"] = usableCandidateCount,
                    ["modelGateBlockedCount"] = modelCandidates.Count - usableCandidateCount,
                    ["candidates"] = new JArray(modelCandidates.Select(x => ToCandidateJson(x, libraryRoot))),
                };
                if (sourceDiagnostics.TryGetValue(model.Output, out var diagnostic))
                {
                    item["sourceRelationDiagnostic"] = diagnostic;
                }
                yield return item;
            }
        }

        private static Dictionary<string, JObject> LoadSourceRelationDiagnostics(string libraryRoot, List<AssetRow> models)
        {
            var result = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            var sourceIndex = ResolveSourceIndexPath(libraryRoot);
            if (string.IsNullOrWhiteSpace(sourceIndex) || !File.Exists(sourceIndex))
            {
                foreach (var model in models)
                {
                    result[model.Output] = new JObject
                    {
                        ["status"] = "no_source_index",
                        ["message"] = "未找到 unity_source_index.db，无法解释 0 候选原因。请用完整源目录重建源索引或传入带 source_index_usage.json 的 Library。",
                        ["rule"] = "诊断只解释 Unity 原始关系，不新增模型-动画绑定。"
                    };
                }
                return result;
            }

            var pathIds = models
                .Select(x => ReadLong(x.Raw, "pathId"))
                .Where(x => x.HasValue)
                .Select(x => x.Value)
                .Distinct()
                .ToArray();
            if (pathIds.Length == 0)
            {
                foreach (var model in models)
                {
                    result[model.Output] = BuildMissingSourceObjectDiagnostic(sourceIndex, model, "catalog_missing_path_id");
                }
                return result;
            }

            var objectsByPathId = new Dictionary<long, List<SourceObjectRow>>();
            try
            {
                using var sourceConnection = new SqliteConnection($"Data Source={sourceIndex};Mode=ReadOnly");
                sourceConnection.Open();
                LoadSourceObjectsForCatalogModels(sourceConnection, models, objectsByPathId);
                var missingPathIds = pathIds
                    .Where(x => !objectsByPathId.ContainsKey(x))
                    .ToArray();
                if (missingPathIds.Length > 0)
                {
                    LoadSourceObjects(sourceConnection, missingPathIds, objectsByPathId);
                }

                foreach (var model in models)
                {
                    var pathId = ReadLong(model.Raw, "pathId");
                    if (!pathId.HasValue)
                    {
                        result[model.Output] = BuildMissingSourceObjectDiagnostic(sourceIndex, model, "catalog_missing_path_id");
                        continue;
                    }

                    var sourceObject = ChooseSourceObject(objectsByPathId, pathId.Value, S(model.Raw, "source"), S(model.Raw, "sourceType"), model.Name);
                    if (sourceObject == null)
                    {
                        result[model.Output] = BuildMissingSourceObjectDiagnostic(sourceIndex, model, "source_object_not_found");
                        continue;
                    }

                    result[model.Output] = BuildSourceRelationDiagnostic(sourceConnection, sourceIndex, model, sourceObject);
                }
            }
            catch (Exception e) when (e is SqliteException || e is IOException || e is UnauthorizedAccessException)
            {
                foreach (var model in models)
                {
                    result[model.Output] = new JObject
                    {
                        ["status"] = "source_index_read_failed",
                        ["sourceIndex"] = sourceIndex,
                        ["message"] = $"{e.GetType().Name}: {e.Message}",
                        ["rule"] = "诊断失败不代表可以猜测绑定；默认候选仍必须来自显式 Unity 关系。"
                    };
                }
            }

            return result;
        }

        private static void LoadSourceObjectsForCatalogModels(
            SqliteConnection sourceConnection,
            List<AssetRow> models,
            Dictionary<long, List<SourceObjectRow>> objectsByPathId)
        {
            foreach (var model in models)
            {
                var pathId = ReadLong(model.Raw, "pathId");
                if (!pathId.HasValue)
                {
                    continue;
                }

                foreach (var sourcePath in BuildCatalogSourcePathKeys(S(model.Raw, "source")))
                {
                    using var command = sourceConnection.CreateCommand();
                    command.CommandText = @"
SELECT source_path, serialized_file, path_id, type, name
FROM source_objects INDEXED BY idx_source_objects_source_path
WHERE source_path=$sourcePath AND path_id=$pathId
LIMIT 16;";
                    command.Parameters.AddWithValue("$sourcePath", sourcePath);
                    command.Parameters.AddWithValue("$pathId", pathId.Value);
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        AddSourceObject(objectsByPathId, new SourceObjectRow(
                            ReadString(reader, 0),
                            ReadString(reader, 1),
                            reader.GetInt64(2),
                            ReadString(reader, 3),
                            ReadString(reader, 4)));
                    }
                    if (objectsByPathId.ContainsKey(pathId.Value))
                    {
                        break;
                    }
                }
            }
        }

        private static void LoadSourceObjects(
            SqliteConnection sourceConnection,
            long[] pathIds,
            Dictionary<long, List<SourceObjectRow>> objectsByPathId)
        {
            using var command = sourceConnection.CreateCommand();
            var names = new List<string>();
            for (var i = 0; i < pathIds.Length; i++)
            {
                var name = "$pid" + i.ToString(CultureInfo.InvariantCulture);
                names.Add(name);
                command.Parameters.AddWithValue(name, pathIds[i]);
            }

            command.CommandText = $@"
SELECT source_path, serialized_file, path_id, type, name
FROM source_objects
WHERE path_id IN ({string.Join(",", names)});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = new SourceObjectRow(
                    ReadString(reader, 0),
                    ReadString(reader, 1),
                    reader.GetInt64(2),
                    ReadString(reader, 3),
                    ReadString(reader, 4));
                AddSourceObject(objectsByPathId, row);
            }
        }

        private static void AddSourceObject(Dictionary<long, List<SourceObjectRow>> objectsByPathId, SourceObjectRow row)
        {
            if (!objectsByPathId.TryGetValue(row.PathId, out var list))
            {
                list = new List<SourceObjectRow>();
                objectsByPathId[row.PathId] = list;
            }
            if (!list.Any(x => string.Equals(x.SerializedFile, row.SerializedFile, StringComparison.OrdinalIgnoreCase)
                && x.PathId == row.PathId))
            {
                list.Add(row);
            }
        }

        private static IEnumerable<string> BuildCatalogSourcePathKeys(string source)
        {
            var normalized = NormalizePathForSuffix(source);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in BuildCatalogSourcePathKeysCore(normalized))
            {
                if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
                {
                    yield return value;
                }
            }
        }

        private static IEnumerable<string> BuildCatalogSourcePathKeysCore(string normalizedSource)
        {
            yield return normalizedSource;
            var parts = normalizedSource.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                yield break;
            }

            // 源索引通常保存相对 VFS 路径，例如 7064D8E2/7064D8E2.blc。
            // 这里按后缀逐级尝试，优先走 source_path 索引，避免只按 PathID 扫全表。
            var start = Math.Max(0, parts.Length - 6);
            for (var i = parts.Length - 2; i >= start; i--)
            {
                yield return string.Join("/", parts.Skip(i));
            }
            yield return parts[^1];
        }


        private static JObject BuildSourceRelationDiagnostic(
            SqliteConnection sourceConnection,
            string sourceIndex,
            AssetRow model,
            SourceObjectRow sourceObject)
        {
            var relations = LoadRelationsFromSourceObject(sourceConnection, sourceObject);
            var relationCounts = relations
                .GroupBy(x => x.Relation, StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
            var componentDiagnostics = LoadComponentDiagnostics(sourceConnection, sourceObject);
            var hasController = relationCounts.ContainsKey("animator.controller")
                || relationCounts.ContainsKey("animation.clip")
                || relationCounts.ContainsKey("animatorOverrideController.baseController");
            var hasAvatar = relationCounts.ContainsKey("animator.avatar");
            var nameTokenControllerHints = LoadNameTokenControllerHints(sourceConnection, model, sourceObject);
            var status = hasController ? "has_explicit_animation_relation_not_imported" : "no_explicit_animation_relation";
            var message = hasController
                ? "源索引能看到显式动画关系，但当前 Library 候选表没有导入；应重建 library_index.db 或检查导入逻辑。"
                : hasAvatar
                    ? "源索引只看到 Animator Avatar，没有 RuntimeAnimatorController/AnimationClip 显式关系；不能用名称、目录或同 Avatar 关系补默认候选。"
                    : "源索引没有看到可用于默认绑定的 Animator/Animation 显式动画关系。";

            return new JObject
            {
                ["status"] = status,
                ["message"] = message,
                ["sourceIndex"] = sourceIndex,
                ["sourceObject"] = new JObject
                {
                    ["sourcePath"] = sourceObject.SourcePath,
                    ["serializedFile"] = sourceObject.SerializedFile,
                    ["pathId"] = sourceObject.PathId,
                    ["type"] = sourceObject.Type,
                    ["name"] = sourceObject.Name
                },
                ["relationCounts"] = JObject.FromObject(relationCounts),
                ["explicitRelations"] = new JArray(relations.Take(24).Select(x => x.ToJson())),
                ["componentDiagnostics"] = componentDiagnostics,
                ["nameTokenControllerHints"] = nameTokenControllerHints,
                ["nextAction"] = hasController
                    ? "rebuild_library_index_or_fix_candidate_import"
                    : "keep_zero_candidates_until_a_real_controller_clip_pptr_or_profile_rule_is_found",
                ["rule"] = "这里只做 0 候选诊断；不会因为同名、同目录、同 Avatar 或骨架兼容而新增默认模型-动画绑定。"
            };
        }

        private static JObject LoadComponentDiagnostics(SqliteConnection sourceConnection, SourceObjectRow sourceObject)
        {
            var root = ResolveGameObjectForComponentDiagnostics(sourceConnection, sourceObject);
            if (root == null)
            {
                return new JObject
                {
                    ["status"] = "no_gameobject_context",
                    ["message"] = "源对象无法定位到 GameObject 组件上下文，只能检查对象自身出边。",
                    ["rule"] = "脚本 PPtr 只解释为什么没有默认候选，不能升级为模型-动画绑定。"
                };
            }

            var components = LoadGameObjectComponents(sourceConnection, root);
            var componentItems = new JArray();
            var scriptNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var targetTypeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var component in components.Take(48))
            {
                var item = new JObject
                {
                    ["type"] = component.Type,
                    ["name"] = component.Name,
                    ["file"] = component.SerializedFile,
                    ["pathId"] = component.PathId
                };

                if (string.Equals(component.Type, "MonoBehaviour", StringComparison.OrdinalIgnoreCase))
                {
                    var scriptName = LoadMonoBehaviourScriptName(sourceConnection, component);
                    item["scriptName"] = scriptName;
                    if (!string.IsNullOrWhiteSpace(scriptName))
                    {
                        scriptNames.Add(scriptName);
                    }

                    var pptrs = LoadPPtrSamples(sourceConnection, component, 12);
                    item["monoBehaviourPPtrCount"] = pptrs.TotalCount;
                    item["monoBehaviourPPtrSamples"] = pptrs.Samples;
                    foreach (var count in pptrs.TargetTypeCounts)
                    {
                        targetTypeCounts[count.Key] = targetTypeCounts.TryGetValue(count.Key, out var old)
                            ? old + count.Value
                            : count.Value;
                    }
                }
                else if (string.Equals(component.Type, "Animator", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(component.Type, "Animation", StringComparison.OrdinalIgnoreCase))
                {
                    var componentRelations = LoadRelationsFromSourceObject(sourceConnection, component)
                        .Take(24)
                        .Select(x => x.ToJson());
                    item["animationComponentRelations"] = new JArray(componentRelations);
                }

                componentItems.Add(item);
            }

            return new JObject
            {
                ["status"] = "diagnostic_only",
                ["rootGameObject"] = new JObject
                {
                    ["sourcePath"] = root.SourcePath,
                    ["serializedFile"] = root.SerializedFile,
                    ["pathId"] = root.PathId,
                    ["type"] = root.Type,
                    ["name"] = root.Name
                },
                ["componentCount"] = components.Count,
                ["sampledComponentCount"] = componentItems.Count,
                ["scriptNames"] = new JArray(scriptNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                ["monoBehaviourPPtrTargetTypeCounts"] = JObject.FromObject(targetTypeCounts.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Value)),
                ["components"] = componentItems,
                ["rule"] = "Endfield 这类 postmodel 可能只有 Avatar 和脚本组件。脚本 PPtr 可用于定位下一步研究方向，但在没有明确 Controller/Clip/角色配置闭环前，不能进入默认候选。"
            };
        }

        private static JObject LoadNameTokenControllerHints(SqliteConnection sourceConnection, AssetRow model, SourceObjectRow sourceObject)
        {
            var tokens = BuildDiagnosticNameTokens(model, sourceObject).ToArray();
            if (tokens.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "no_stable_name_token",
                    ["rule"] = "没有稳定角色 token 时不做名字线索诊断；默认候选仍只能来自 Unity 显式关系。"
                };
            }

            var controllers = LoadControllersForTokens(sourceConnection, tokens);
            var items = new JArray();
            foreach (var controller in controllers.Take(24))
            {
                var incomingCounts = LoadIncomingRelationCounts(sourceConnection, controller);
                items.Add(new JObject
                {
                    ["token"] = controller.Token,
                    ["sourcePath"] = controller.SourcePath,
                    ["serializedFile"] = controller.SerializedFile,
                    ["pathId"] = controller.PathId,
                    ["type"] = controller.Type,
                    ["name"] = controller.Name,
                    ["incomingReferenceCount"] = incomingCounts.Values.Sum(),
                    ["incomingRelationCounts"] = JObject.FromObject(incomingCounts),
                    ["clipCount"] = CountControllerClips(sourceConnection, controller),
                    ["sampleClips"] = LoadControllerClipSamples(sourceConnection, controller, 12),
                    ["relationStatus"] = incomingCounts.Count == 0
                        ? "orphan_controller_no_model_reference"
                        : "controller_has_incoming_references_but_not_from_selected_model"
                });
            }

            return new JObject
            {
                ["status"] = controllers.Count == 0
                    ? "no_matching_controller_hint"
                    : "diagnostic_only_name_token_hint",
                ["tokens"] = new JArray(tokens),
                ["matchingControllerCount"] = controllers.Count,
                ["controllers"] = items,
                ["message"] = controllers.Count == 0
                    ? "未发现与模型 token 同名段匹配的 AnimatorController；继续寻找真实 PPtr/config 闭环。"
                    : "发现同 token controller 线索，但名字相似不能证明模型使用该 controller；只有找到 Animator.controller、脚本 PPtr 或配置闭环后才能生成默认候选。",
                ["rule"] = "这个字段只帮助排查 Endfield 这类 postmodel 缺 controller 桥的问题；它不会把名称、目录或角色 token 提升为模型-动画绑定。"
            };
        }

        private static IEnumerable<string> BuildDiagnosticNameTokens(AssetRow model, SourceObjectRow sourceObject)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var text in new[]
            {
                model?.Name,
                model?.Output,
                S(model?.Raw, "container"),
                S(model?.Raw, "objectPath"),
                sourceObject?.Name,
                sourceObject?.SourcePath,
            })
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                foreach (Match match in Regex.Matches(text, @"(?i)(?:npc_)?chr_[0-9]{4}_[a-z0-9]+"))
                {
                    AddNameToken(result, match.Value);
                }

                var normalized = text.Replace('\\', '/');
                foreach (var part in normalized.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    foreach (var suffix in new[] { "_postmodel", "_model", "_prefab" })
                    {
                        var index = part.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
                        if (index > 0)
                        {
                            AddNameToken(result, part[..index]);
                        }
                    }
                }
            }

            return result
                .Where(x => x.Length >= 8)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Take(12);
        }

        private static void AddNameToken(HashSet<string> tokens, string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            var normalized = token.Trim().Trim('_').ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            tokens.Add(normalized);
            if (normalized.StartsWith("npc_chr_", StringComparison.OrdinalIgnoreCase))
            {
                tokens.Add(normalized["npc_".Length..]);
            }
        }

        private static List<ControllerHintRow> LoadControllersForTokens(SqliteConnection sourceConnection, string[] tokens)
        {
            var result = new List<ControllerHintRow>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in tokens.Take(12))
            {
                using var command = sourceConnection.CreateCommand();
                command.CommandText = @"
SELECT source_path, serialized_file, path_id, type, name
FROM source_objects
WHERE type IN ('AnimatorController','AnimatorOverrideController')
  AND (
      name=$token
      OR name=$ac
      OR name LIKE $acPrefix
      OR name LIKE $tokenPrefix
  )
ORDER BY type COLLATE NOCASE, name COLLATE NOCASE
LIMIT 32;";
                command.Parameters.AddWithValue("$token", token);
                command.Parameters.AddWithValue("$ac", "AC_" + token);
                command.Parameters.AddWithValue("$acPrefix", "AC_" + token + "_%");
                command.Parameters.AddWithValue("$tokenPrefix", token + "_%");
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var row = new ControllerHintRow(
                        token,
                        ReadString(reader, 0),
                        ReadString(reader, 1),
                        reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                        ReadString(reader, 3),
                        ReadString(reader, 4));
                    var key = row.SerializedFile + ":" + row.PathId.ToString(CultureInfo.InvariantCulture);
                    if (seen.Add(key))
                    {
                        result.Add(row);
                    }
                }
            }
            return result;
        }

        private static Dictionary<string, int> LoadIncomingRelationCounts(SqliteConnection sourceConnection, ControllerHintRow controller)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using var command = sourceConnection.CreateCommand();
            command.CommandText = @"
SELECT relation, COUNT(*)
FROM source_relations INDEXED BY idx_source_relations_to
WHERE to_file=$file AND to_path_id=$pathId
GROUP BY relation
ORDER BY relation;";
            command.Parameters.AddWithValue("$file", controller.SerializedFile);
            command.Parameters.AddWithValue("$pathId", controller.PathId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result[ReadString(reader, 0)] = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            }
            return result;
        }

        private static int CountControllerClips(SqliteConnection sourceConnection, ControllerHintRow controller)
        {
            using var command = sourceConnection.CreateCommand();
            command.CommandText = @"
SELECT COUNT(DISTINCT to_file || ':' || to_path_id)
FROM source_relations INDEXED BY idx_source_relations_from
WHERE from_file=$file
  AND from_path_id=$pathId
  AND relation IN ('animatorController.clip','animatorOverrideController.originalClip','animatorOverrideController.overrideClip','animatorOverrideController.clipPair');";
            command.Parameters.AddWithValue("$file", controller.SerializedFile);
            command.Parameters.AddWithValue("$pathId", controller.PathId);
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private static JArray LoadControllerClipSamples(SqliteConnection sourceConnection, ControllerHintRow controller, int limit)
        {
            var result = new JArray();
            using var command = sourceConnection.CreateCommand();
            command.CommandText = @"
SELECT DISTINCT r.relation, r.to_file, r.to_path_id, COALESCE(o.type, r.to_type_hint, ''), COALESCE(o.name, '')
FROM source_relations AS r INDEXED BY idx_source_relations_from
LEFT JOIN source_objects o ON o.serialized_file=r.to_file AND o.path_id=r.to_path_id
WHERE r.from_file=$file
  AND r.from_path_id=$pathId
  AND r.relation IN ('animatorController.clip','animatorOverrideController.originalClip','animatorOverrideController.overrideClip')
ORDER BY COALESCE(o.name, '') COLLATE NOCASE, r.to_path_id
LIMIT $limit;";
            command.Parameters.AddWithValue("$file", controller.SerializedFile);
            command.Parameters.AddWithValue("$pathId", controller.PathId);
            command.Parameters.AddWithValue("$limit", Math.Max(1, limit));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new JObject
                {
                    ["relation"] = ReadString(reader, 0),
                    ["file"] = ReadString(reader, 1),
                    ["pathId"] = reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                    ["type"] = ReadString(reader, 3),
                    ["name"] = ReadString(reader, 4)
                });
            }
            return result;
        }

        private static SourceObjectRow ResolveGameObjectForComponentDiagnostics(SqliteConnection sourceConnection, SourceObjectRow sourceObject)
        {
            if (string.Equals(sourceObject.Type, "GameObject", StringComparison.OrdinalIgnoreCase))
            {
                return sourceObject;
            }

            using var command = sourceConnection.CreateCommand();
            command.CommandText = @"
SELECT to_file, to_path_id
FROM source_relations INDEXED BY idx_source_relations_from
WHERE from_file=$file
  AND from_path_id=$pathId
  AND relation='component.gameObject'
LIMIT 1;";
            command.Parameters.AddWithValue("$file", sourceObject.SerializedFile);
            command.Parameters.AddWithValue("$pathId", sourceObject.PathId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return LoadSourceObject(sourceConnection, ReadString(reader, 0), reader.IsDBNull(1) ? 0 : reader.GetInt64(1));
        }

        private static List<SourceObjectRow> LoadGameObjectComponents(SqliteConnection sourceConnection, SourceObjectRow root)
        {
            var components = new List<SourceObjectRow>();
            using var command = sourceConnection.CreateCommand();
            command.CommandText = @"
SELECT to_file, to_path_id
FROM source_relations INDEXED BY idx_source_relations_from
WHERE from_file=$file
  AND from_path_id=$pathId
  AND relation='gameObject.component'
ORDER BY to_path_id
LIMIT 256;";
            command.Parameters.AddWithValue("$file", root.SerializedFile);
            command.Parameters.AddWithValue("$pathId", root.PathId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var component = LoadSourceObject(sourceConnection, ReadString(reader, 0), reader.IsDBNull(1) ? 0 : reader.GetInt64(1));
                if (component != null)
                {
                    components.Add(component);
                }
            }
            return components;
        }

        private static SourceObjectRow LoadSourceObject(SqliteConnection sourceConnection, string serializedFile, long pathId)
        {
            using var command = sourceConnection.CreateCommand();
            command.CommandText = @"
SELECT source_path, serialized_file, path_id, type, name
FROM source_objects
WHERE serialized_file=$file AND path_id=$pathId
LIMIT 1;";
            command.Parameters.AddWithValue("$file", serializedFile ?? string.Empty);
            command.Parameters.AddWithValue("$pathId", pathId);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new SourceObjectRow(
                    ReadString(reader, 0),
                    ReadString(reader, 1),
                    reader.GetInt64(2),
                    ReadString(reader, 3),
                    ReadString(reader, 4))
                : null;
        }

        private static string LoadMonoBehaviourScriptName(SqliteConnection sourceConnection, SourceObjectRow component)
        {
            using var command = sourceConnection.CreateCommand();
            command.CommandText = @"
SELECT COALESCE(o.name, '')
FROM source_relations AS r INDEXED BY idx_source_relations_from
LEFT JOIN source_objects o ON o.serialized_file=r.to_file AND o.path_id=r.to_path_id
WHERE r.from_file=$file
  AND r.from_path_id=$pathId
  AND r.relation='monoBehaviour.script'
LIMIT 1;";
            command.Parameters.AddWithValue("$file", component.SerializedFile);
            command.Parameters.AddWithValue("$pathId", component.PathId);
            var value = command.ExecuteScalar();
            return value?.ToString() ?? string.Empty;
        }

        private static PPtrSampleResult LoadPPtrSamples(SqliteConnection sourceConnection, SourceObjectRow component, int sampleLimit)
        {
            var samples = new JArray();
            var targetTypeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            using var command = sourceConnection.CreateCommand();
            command.CommandText = @"
SELECT r.to_file, r.to_type_hint, r.to_path_id, COALESCE(o.type, ''), COALESCE(o.name, '')
FROM source_relations AS r INDEXED BY idx_source_relations_from
LEFT JOIN source_objects o ON o.serialized_file=r.to_file AND o.path_id=r.to_path_id
WHERE r.from_file=$file
  AND r.from_path_id=$pathId
  AND r.relation='monoBehaviour.pptr'
ORDER BY COALESCE(o.type, r.to_type_hint), COALESCE(o.name, ''), r.to_path_id
LIMIT $limit;";
            command.Parameters.AddWithValue("$file", component.SerializedFile);
            command.Parameters.AddWithValue("$pathId", component.PathId);
            command.Parameters.AddWithValue("$limit", Math.Max(1, sampleLimit));
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    total++;
                    var type = ReadString(reader, 3);
                    if (string.IsNullOrWhiteSpace(type))
                    {
                        type = ReadString(reader, 1);
                    }
                    if (string.IsNullOrWhiteSpace(type))
                    {
                        type = "Object";
                    }
                    targetTypeCounts[type] = targetTypeCounts.TryGetValue(type, out var old) ? old + 1 : 1;
                    samples.Add(new JObject
                    {
                        ["file"] = ReadString(reader, 0),
                        ["typeHint"] = ReadString(reader, 1),
                        ["type"] = type,
                        ["pathId"] = reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                        ["name"] = ReadString(reader, 4)
                    });
                }
            }

            using var countCommand = sourceConnection.CreateCommand();
            countCommand.CommandText = @"
SELECT COUNT(*)
FROM source_relations INDEXED BY idx_source_relations_from
WHERE from_file=$file
  AND from_path_id=$pathId
  AND relation='monoBehaviour.pptr';";
            countCommand.Parameters.AddWithValue("$file", component.SerializedFile);
            countCommand.Parameters.AddWithValue("$pathId", component.PathId);
            total = Convert.ToInt64(countCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
            return new PPtrSampleResult(total, samples, targetTypeCounts);
        }

        private static List<SourceRelationRow> LoadRelationsFromSourceObject(SqliteConnection sourceConnection, SourceObjectRow sourceObject)
        {
            var result = new List<SourceRelationRow>();
            using var command = sourceConnection.CreateCommand();
            command.CommandText = @"
SELECT relation, confidence, from_type, from_name, from_path_id, to_file, to_type_hint, to_path_id, target_count
FROM source_relations INDEXED BY idx_source_relations_from
WHERE from_file=$file AND from_path_id=$pathId
ORDER BY relation, to_type_hint, to_path_id
LIMIT 128;";
            command.Parameters.AddWithValue("$file", sourceObject.SerializedFile);
            command.Parameters.AddWithValue("$pathId", sourceObject.PathId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new SourceRelationRow(
                    ReadString(reader, 0),
                    ReadString(reader, 1),
                    ReadString(reader, 2),
                    ReadString(reader, 3),
                    reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                    ReadString(reader, 5),
                    ReadString(reader, 6),
                    reader.IsDBNull(7) ? 0 : reader.GetInt64(7),
                    reader.IsDBNull(8) ? null : reader.GetInt64(8)));
            }
            return result;
        }

        private static JObject BuildMissingSourceObjectDiagnostic(string sourceIndex, AssetRow model, string status)
        {
            return new JObject
            {
                ["status"] = status,
                ["sourceIndex"] = sourceIndex,
                ["catalogSource"] = S(model.Raw, "source"),
                ["catalogPathId"] = ReadLong(model.Raw, "pathId"),
                ["catalogSourceType"] = S(model.Raw, "sourceType"),
                ["message"] = "Library 模型条目无法在源索引中定位到对应 Unity 对象；应检查源索引是否来自同一完整源目录。",
                ["rule"] = "定位不到源对象时必须保持 0 候选，不能退回名称或骨架猜测。"
            };
        }

        private static SourceObjectRow ChooseSourceObject(
            Dictionary<long, List<SourceObjectRow>> objectsByPathId,
            long pathId,
            string catalogSource,
            string catalogType,
            string catalogName)
        {
            if (!objectsByPathId.TryGetValue(pathId, out var list) || list.Count == 0)
            {
                return null;
            }

            var normalizedSource = NormalizePathForSuffix(catalogSource);
            return list
                .OrderByDescending(x => PathSuffixMatches(normalizedSource, x.SourcePath))
                .ThenByDescending(x => string.Equals(x.Type, catalogType, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x => string.Equals(x.Name, catalogName, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
        }

        private static bool PathSuffixMatches(string normalizedFullPath, string sourcePath)
        {
            var normalizedSource = NormalizePathForSuffix(sourcePath);
            return !string.IsNullOrWhiteSpace(normalizedFullPath)
                && !string.IsNullOrWhiteSpace(normalizedSource)
                && normalizedFullPath.EndsWith(normalizedSource, StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveSourceIndexPath(string libraryRoot)
        {
            var summaryPath = Path.Combine(libraryRoot, "sqlite_index_summary.json");
            var summarySourceIndex = TryReadSourceIndex(summaryPath, "animationRelationCoverage.sourceIndexAnimationRelationHealth.sourceIndex")
                ?? TryReadSourceIndex(summaryPath, "animationRelationCoverage.exportedAnimatorControllerDiagnostics.sourceIndex");
            if (!string.IsNullOrWhiteSpace(summarySourceIndex) && File.Exists(summarySourceIndex))
            {
                return Path.GetFullPath(summarySourceIndex);
            }

            var usagePath = Path.Combine(libraryRoot, "source_index_usage.json");
            var usageSourceIndex = TryReadSourceIndex(usagePath, "sourceIndex");
            if (!string.IsNullOrWhiteSpace(usageSourceIndex) && File.Exists(usageSourceIndex))
            {
                return Path.GetFullPath(usageSourceIndex);
            }

            var local = Path.Combine(libraryRoot, "unity_source_index.db");
            return File.Exists(local) ? Path.GetFullPath(local) : null;
        }

        private static string TryReadSourceIndex(string jsonPath, string dottedPath)
        {
            if (!File.Exists(jsonPath))
            {
                return null;
            }

            try
            {
                JToken token = JObject.Parse(File.ReadAllText(jsonPath));
                foreach (var part in dottedPath.Split('.'))
                {
                    token = token?[part];
                    if (token == null)
                    {
                        return null;
                    }
                }
                var value = token.Type == JTokenType.Null ? null : token.ToString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            catch (Exception e) when (e is IOException || e is JsonException || e is UnauthorizedAccessException)
            {
                // 读取失败只影响诊断说明，不能影响候选列表本身。
                return null;
            }
        }

        private static JObject ToCandidateJson(CandidateRow row, string libraryRoot)
        {
            var candidate = row.Raw;
            var context = candidate["animatorControllerContext"] as JObject;
            var modelReady = IsCandidateModelReady(row);
            return new JObject
            {
                ["animationName"] = row.AnimationName,
                ["animationOutput"] = row.AnimationOutput,
                ["animationAsset"] = (string)candidate["animationAsset"],
                ["animationPath"] = ResolveLibraryPath(libraryRoot, row.AnimationOutput),
                ["confidence"] = (string)candidate["confidence"] ?? row.Confidence,
                ["score"] = row.Score,
                ["status"] = row.Status,
                ["needsValidation"] = row.NeedsValidation,
                ["modelReadyForAnimation"] = modelReady,
                ["modelAnimationBlockedReason"] = candidate["modelAnimationBlockedReason"],
                ["modelAnimationGate"] = candidate["modelAnimationGate"],
                ["relation"] = (string)candidate["relation"],
                ["relationSource"] = (string)candidate["relationSource"] ?? "explicit",
                ["productionAnimationReady"] = candidate["productionAnimationReady"],
                ["productionAnimationPath"] = candidate["productionAnimationPath"],
                ["productionAnimationBlockedReason"] = candidate["productionAnimationBlockedReason"],
                ["directGltfPreviewReady"] = candidate["directGltfPreviewReady"],
                ["directGltfAnimationReady"] = candidate["directGltfAnimationReady"],
                ["directGltfAnimationStatus"] = candidate["directGltfAnimationStatus"],
                ["directAnimationBlocked"] = candidate["directAnimationBlocked"],
                ["directAnimationBlockedReason"] = candidate["directAnimationBlockedReason"],
                ["requiresInternalHumanoidSolve"] = candidate["requiresInternalHumanoidSolve"],
                ["directHumanoidTrsRequired"] = candidate["directHumanoidTrsRequired"],
                ["unityBakeAcceleratedReady"] = candidate["unityBakeAcceleratedReady"],
                ["unityBakeAcceleratedBlockedReason"] = candidate["unityBakeAcceleratedBlockedReason"],
                ["requiresUnityBake"] = candidate["requiresUnityBake"],
                ["legacyUnityBakeSupported"] = candidate["legacyUnityBakeSupported"],
                ["standaloneBodyBakeReady"] = candidate["standaloneBodyBakeReady"],
                ["standaloneBodyBakeStatus"] = candidate["standaloneBodyBakeStatus"],
                ["standaloneBodyRequiresAnimatorControllerContext"] = candidate["standaloneBodyRequiresAnimatorControllerContext"],
                ["standaloneBodyRequiresDirectTrsSolve"] = candidate["standaloneBodyRequiresDirectTrsSolve"],
                ["nextAction"] = candidate["nextAction"],
                ["controllerState"] = context == null ? null : new JObject
                {
                    ["source"] = (string)context["source"],
                    ["stateFullPath"] = (string)context["stateFullPath"],
                    ["stateName"] = (string)context["stateName"],
                    ["stateLoop"] = context["stateLoop"],
                    ["baseLayerClip"] = context["baseLayerClip"],
                },
                ["recommendedCommands"] = new JObject
                {
                    ["exportStandaloneAnimation"] = $"--export_animation_gltf_from_library \"{libraryRoot}\" --preview_model \"{row.ModelOutput}\" --preview_animation \"{row.AnimationName}\"",
                    ["mergeStandaloneAnimation"] = "--merge_animation_gltf <model.gltf> --preview_animation <animation.gltf>",
                },
            };
        }

        private static bool IsCandidateModelReady(CandidateRow row)
        {
            if (string.Equals(row.Status, "model_not_animation_ready", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var ready = ReadNullableBool(row.Raw?["modelReadyForAnimation"])
                ?? ReadNullableBool(row.Raw?["modelAnimationGate"]?["ready"]);
            return ready == true;
        }

        private static bool? ReadNullableBool(JToken token)
        {
            return token?.Type switch
            {
                JTokenType.Boolean => token.Value<bool>(),
                JTokenType.Integer => token.Value<int>() != 0,
                JTokenType.String when bool.TryParse(token.ToString(), out var value) => value,
                JTokenType.String when int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value != 0,
                _ => null,
            };
        }

        private static bool Matches(string selector, string name, string output)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                return true;
            }

            foreach (var part in selector.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()))
            {
                if (string.IsNullOrWhiteSpace(part))
                {
                    continue;
                }
                if (string.Equals(part, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(part, output, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(name) && name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0)
                    || (!string.IsNullOrWhiteSpace(output) && output.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return true;
                }

                try
                {
                    if ((!string.IsNullOrWhiteSpace(name) && Regex.IsMatch(name, part, RegexOptions.IgnoreCase))
                        || (!string.IsNullOrWhiteSpace(output) && Regex.IsMatch(output, part, RegexOptions.IgnoreCase)))
                    {
                        return true;
                    }
                }
                catch (ArgumentException)
                {
                    // 非法正则按普通文本处理即可，不把查询命令变成失败点。
                }
            }

            return false;
        }

        private static JObject ReadObject(SqliteDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal))
            {
                return new JObject();
            }

            try
            {
                return JObject.Parse(reader.GetString(ordinal));
            }
            catch
            {
                return new JObject();
            }
        }

        private static string ReadString(SqliteDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
        }

        private static string S(JObject obj, string name)
        {
            return obj?[name]?.Type == JTokenType.Null ? null : obj?[name]?.ToString();
        }

        private static long? ReadLong(JObject obj, string name)
        {
            var token = obj?[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }
            if (token.Type == JTokenType.Integer)
            {
                return token.Value<long>();
            }
            return long.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }

        private static string NormalizePathForSuffix(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/').Trim();
        }

        private static string ResolveLibraryPath(string libraryRoot, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return string.Empty;
            }

            return Path.Combine(Path.GetFullPath(libraryRoot), relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static string SafeName(string value)
        {
            var text = string.IsNullOrWhiteSpace(value) ? "Model" : value;
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                text = text.Replace(c, '_');
            }
            return text.Length > 120 ? text[..120] : text;
        }

        private sealed record AssetRow(string Name, string Output, JObject Raw);

        private sealed record SourceObjectRow(string SourcePath, string SerializedFile, long PathId, string Type, string Name);

        private sealed record PPtrSampleResult(long TotalCount, JArray Samples, Dictionary<string, int> TargetTypeCounts);

        private sealed record ControllerHintRow(
            string Token,
            string SourcePath,
            string SerializedFile,
            long PathId,
            string Type,
            string Name);

        private sealed record SourceRelationRow(
            string Relation,
            string Confidence,
            string FromType,
            string FromName,
            long FromPathId,
            string ToFile,
            string ToTypeHint,
            long ToPathId,
            long? TargetCount)
        {
            public JObject ToJson()
            {
                return new JObject
                {
                    ["relation"] = Relation,
                    ["confidence"] = Confidence,
                    ["fromType"] = FromType,
                    ["fromName"] = FromName,
                    ["fromPathId"] = FromPathId,
                    ["toFile"] = ToFile,
                    ["toTypeHint"] = ToTypeHint,
                    ["toPathId"] = ToPathId,
                    ["targetCount"] = TargetCount
                };
            }
        }

        private sealed record CandidateRow(
            string ModelOutput,
            string AnimationOutput,
            string Confidence,
            double Score,
            string Status,
            bool NeedsValidation,
            JObject Raw,
            string AnimationName,
            JObject AnimationRaw);
    }
}
