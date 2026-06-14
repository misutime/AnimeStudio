using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AnimeStudio.CLI
{
    internal static class UnityBakeComparisonReporter
    {
        public static void Compare(string requestOrResultPath, string gltfPath, string outputPath)
        {
            if (string.IsNullOrWhiteSpace(requestOrResultPath) || !File.Exists(requestOrResultPath))
            {
                Logger.Error($"Unity bake request/result not found: {requestOrResultPath}");
                return;
            }
            if (string.IsNullOrWhiteSpace(gltfPath) || !File.Exists(gltfPath))
            {
                Logger.Error("--compare_gltf must point to an internally solved glTF preview.");
                return;
            }

            var input = JObject.Parse(File.ReadAllText(requestOrResultPath));
            var requestPath = IsRequest(input) ? requestOrResultPath : TryFindSiblingRequest(requestOrResultPath);
            var resultPath = IsRequest(input) ? (string)input["outputJson"] : requestOrResultPath;
            if (string.IsNullOrWhiteSpace(resultPath) || !File.Exists(resultPath))
            {
                Logger.Error($"Unity bake result not found: {resultPath}");
                return;
            }

            var result = JObject.Parse(File.ReadAllText(resultPath));
            if (!string.Equals((string)result["status"], "ok", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Error($"Unity bake result is not ok: {(string)result["message"]}");
                return;
            }
            var request = !string.IsNullOrWhiteSpace(requestPath) && File.Exists(requestPath)
                ? JObject.Parse(File.ReadAllText(requestPath))
                : null;

            var gltf = JObject.Parse(File.ReadAllText(gltfPath));
            var gltfDir = Path.GetDirectoryName(Path.GetFullPath(gltfPath)) ?? Directory.GetCurrentDirectory();
            var nodes = gltf["nodes"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var nodePathToIndex = BuildNodePathIndex(nodes);
            var rotationChannels = LoadRotationChannels(gltf, gltfDir);
            var animationAsset = ResolveDecodedAnimationAsset(request, result, gltfPath);

            var rows = new List<TrackCompareRow>();
            var missingTracks = new List<string>();
            foreach (var track in result["tracks"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                if ((bool?)track["changed"] != true)
                {
                    continue;
                }

                var unityPath = (string)track["path"];
                var gltfNodePath = NormalizeBakePath(unityPath);
                if (!nodePathToIndex.TryGetValue(gltfNodePath, out var nodeIndex) ||
                    !rotationChannels.TryGetValue(nodeIndex, out var internalTrack))
                {
                    missingTracks.Add(unityPath);
                    continue;
                }

                var unityKeys = ReadUnityRotationKeys(track["rotations"]);
                if (unityKeys.Length == 0 || internalTrack.Times.Length == 0)
                {
                    missingTracks.Add(unityPath);
                    continue;
                }

                var errors = new List<float>();
                foreach (var key in unityKeys)
                {
                    var unityGltfRotation = UnityToGltfRotation(key.Rotation);
                    var internalRotation = SampleRotation(internalTrack, key.Time);
                    errors.Add(QuaternionAngleDegrees(unityGltfRotation, internalRotation));
                }

                rows.Add(new TrackCompareRow(
                    unityPath,
                    gltfNodePath,
                    nodeIndex,
                    unityKeys.Length,
                    errors.Count == 0 ? 0 : errors.Max(),
                    errors.Count == 0 ? 0 : errors.Average(),
                    IsBodyBone(gltfNodePath)
                ));
            }

            var ordered = rows
                .OrderByDescending(x => x.MaxRotationErrorDegrees)
                .ThenBy(x => x.GltfPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var bodyRows = ordered.Where(x => x.BodyBone).ToArray();
            var directRotationComparison = new JObject
            {
                ["status"] = rows.Count == 0 ? "not_available" : (missingTracks.Count == 0 ? "ok" : "warning"),
                ["source"] = "unity_baked_track_to_gltf_rotation",
                ["matchedTrackCount"] = rows.Count,
                ["matchedBodyTrackCount"] = bodyRows.Length,
                ["maxDegrees"] = ordered.Length == 0 ? 0 : ordered[0].MaxRotationErrorDegrees,
                ["avgTrackMaxDegrees"] = ordered.Length == 0 ? 0 : ordered.Average(x => x.MaxRotationErrorDegrees),
                ["bodyGroupError"] = BuildBodyGroupErrorSummary(bodyRows),
                ["note"] = "Fallback for older Unity oracle results that do not contain internalAvatarPoseTimeline. It compares Unity baked local rotation tracks directly against glTF rotation channels.",
            };

            outputPath = string.IsNullOrWhiteSpace(outputPath)
                ? Path.Combine(gltfDir, "unity_bake_compare_report.json")
                : outputPath;
            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            var muscleDecodeComparison = BuildMuscleDecodeComparison(result, animationAsset);
            var editorCurveDecodeComparison = BuildEditorCurveDecodeComparison(result, animationAsset);
            var editorCurveHumanPoseRotationComparison = BuildEditorCurveHumanPoseRotationComparison(result);
            var internalAvatarPoseComparison = BuildInternalAvatarPoseComparison(result);
            var internalAvatarPoseToGltfComparison = BuildInternalAvatarPoseToGltfComparison(result, nodePathToIndex, rotationChannels);
            var playableBakeToEditorCurveInternalPoseComparison = BuildPlayableBakeToEditorCurveInternalPoseComparison(result);
            var setHumanPoseTransformToInternalAvatarPoseComparison = BuildSetHumanPoseTransformToInternalAvatarPoseComparison(result);
            var internalAvatarPoseTimelineToGltfComparison = BuildInternalAvatarPoseTimelineToGltfComparison(result, nodePathToIndex, rotationChannels);
            var unityTransformLocalPoseReferenceSummary = BuildUnityTransformLocalPoseReferenceSummary(result, nodes, nodePathToIndex);
            var avatarInternalSolverMetadataSummary = BuildAvatarInternalSolverMetadataSummary(request);
            var zeroMusclePoseCandidateComparison = BuildZeroMusclePoseCandidateComparison(request, result, nodes, nodePathToIndex);
            var zeroMuscleResidualCorrectionSummary = BuildZeroMuscleResidualCorrectionSummary(request, result, nodes, nodePathToIndex);
            var zeroMuscleShortProductSearchSummary = BuildZeroMuscleShortProductSearchSummary(request, result, nodes, nodePathToIndex);
            var zeroBaseSourceCandidateGateSummary = BuildZeroBaseSourceCandidateGateSummary(zeroMuscleShortProductSearchSummary);
            var avatarInternalSolverParameterSummary = BuildAvatarInternalSolverParameterSummary(request, zeroMuscleShortProductSearchSummary);
            var internalAvatarPoseTranslationTimelineSummary = BuildInternalAvatarPoseTranslationTimelineSummary(request, result, nodes, nodePathToIndex);
            var decodedDirectRotationToInternalAvatarPoseComparison = BuildDecodedDirectRotationToInternalAvatarPoseComparison(result, animationAsset);
            var singleMuscleProbeSolverComparison = BuildSingleMuscleProbeSolverComparison(request, result);
            var internalHumanoidSolverVariantComparison = BuildInternalHumanoidSolverVariantComparison(request, result, animationAsset, nodes, nodePathToIndex, zeroBaseSourceCandidateGateSummary, singleMuscleProbeSolverComparison);

            var report = new JObject
            {
                ["generatedAt"] = DateTime.UtcNow.ToString("O"),
                ["status"] = missingTracks.Count == 0 ? "ok" : "warning",
                ["rule"] = "Diagnostic only: Unity PlayableGraph bake is used as an oracle to measure the internal Humanoid/Muscle glTF solver. This report must not be treated as a production export dependency.",
                ["unityBakeRequest"] = requestPath,
                ["unityBakeResult"] = resultPath,
                ["compareGltf"] = gltfPath,
                ["decodedAnimationAsset"] = animationAsset,
                ["clipName"] = result["clipName"],
                ["frameRate"] = result["frameRate"],
                ["sampleCount"] = result["sampleCount"],
                ["changedTrackCount"] = result["changedTrackCount"],
                ["matchedRotationTrackCount"] = rows.Count,
                ["missingRotationTrackCount"] = missingTracks.Count,
                ["missingRotationTracks"] = new JArray(missingTracks.Take(128)),
                ["rotationError"] = new JObject
                {
                    ["maxDegrees"] = ordered.Length == 0 ? 0 : ordered[0].MaxRotationErrorDegrees,
                    ["avgTrackMaxDegrees"] = ordered.Length == 0 ? 0 : ordered.Average(x => x.MaxRotationErrorDegrees),
                    ["bodyMaxDegrees"] = bodyRows.Length == 0 ? 0 : bodyRows.Max(x => x.MaxRotationErrorDegrees),
                    ["bodyAvgTrackMaxDegrees"] = bodyRows.Length == 0 ? 0 : bodyRows.Average(x => x.MaxRotationErrorDegrees),
                    ["note"] = "Angles compare Unity local rotations after Unity->glTF basis conversion against the internal glTF animation rotation channels at matching sample times.",
                },
                ["humanoidSolverDiagnosis"] = BuildHumanoidSolverDiagnosis(
                    editorCurveHumanPoseRotationComparison,
                    internalAvatarPoseComparison,
                    setHumanPoseTransformToInternalAvatarPoseComparison,
                    playableBakeToEditorCurveInternalPoseComparison,
                    internalAvatarPoseTimelineToGltfComparison,
                    internalHumanoidSolverVariantComparison,
                    singleMuscleProbeSolverComparison,
                    directRotationComparison),
                ["muscleDecodeComparison"] = muscleDecodeComparison,
                ["editorCurveDecodeComparison"] = editorCurveDecodeComparison,
                ["editorCurveHumanPoseRotationComparison"] = editorCurveHumanPoseRotationComparison,
                ["internalAvatarPoseComparison"] = internalAvatarPoseComparison,
                ["internalAvatarPoseToGltfComparison"] = internalAvatarPoseToGltfComparison,
                ["playableBakeToEditorCurveInternalPoseComparison"] = playableBakeToEditorCurveInternalPoseComparison,
                ["setHumanPoseTransformToInternalAvatarPoseComparison"] = setHumanPoseTransformToInternalAvatarPoseComparison,
                ["internalAvatarPoseTimelineToGltfComparison"] = internalAvatarPoseTimelineToGltfComparison,
                ["unityTransformLocalPoseReferenceSummary"] = unityTransformLocalPoseReferenceSummary,
                ["avatarInternalSolverMetadataSummary"] = avatarInternalSolverMetadataSummary,
                ["zeroMusclePoseCandidateComparison"] = zeroMusclePoseCandidateComparison,
                ["zeroMuscleResidualCorrectionSummary"] = zeroMuscleResidualCorrectionSummary,
                ["zeroMuscleShortProductSearchSummary"] = zeroMuscleShortProductSearchSummary,
                ["zeroBaseSourceCandidateGateSummary"] = zeroBaseSourceCandidateGateSummary,
                ["avatarInternalSolverParameterSummary"] = avatarInternalSolverParameterSummary,
                ["internalAvatarPoseTranslationTimelineSummary"] = internalAvatarPoseTranslationTimelineSummary,
                ["decodedDirectRotationToInternalAvatarPoseComparison"] = decodedDirectRotationToInternalAvatarPoseComparison,
                ["internalHumanoidSolverVariantComparison"] = internalHumanoidSolverVariantComparison,
                ["singleMuscleProbeSolverComparison"] = singleMuscleProbeSolverComparison,
                ["topRotationErrors"] = ToJsonRows(ordered.Take(64)),
                ["topBodyRotationErrors"] = ToJsonRows(bodyRows.Take(64)),
            };

            File.WriteAllText(outputPath, JsonConvert.SerializeObject(report, Formatting.Indented));
            Logger.Info($"Unity bake comparison report: {outputPath}");
        }

        private static JObject BuildHumanoidSolverDiagnosis(
            JObject editorCurveHumanPoseRotationComparison,
            JObject internalAvatarPoseComparison,
            JObject setHumanPoseTransformToInternalAvatarPoseComparison,
            JObject playableBakeToEditorCurveInternalPoseComparison,
            JObject internalAvatarPoseTimelineToGltfComparison,
            JObject internalHumanoidSolverVariantComparison,
            JObject singleMuscleProbeSolverComparison,
            JObject directRotationComparison)
        {
            var editorCurveMax = JsonFloat(editorCurveHumanPoseRotationComparison, "maxDegrees");
            var internalAvatarMax = JsonFloat(internalAvatarPoseComparison, "maxDegrees");
            var setHumanPoseTransformMax = JsonFloat(setHumanPoseTransformToInternalAvatarPoseComparison, "maxDegrees");
            var hasEditorCurveHumanPose = IsStatusAvailable(editorCurveHumanPoseRotationComparison);
            var hasInternalAvatarPose = IsStatusAvailable(internalAvatarPoseComparison);
            var hasSetHumanPoseTransform = IsStatusAvailable(setHumanPoseTransformToInternalAvatarPoseComparison);
            var timelineMatchedBodyTrackCount = JsonInt(internalAvatarPoseTimelineToGltfComparison, "matchedBodyTrackCount");
            var hasInternalAvatarPoseTimeline = timelineMatchedBodyTrackCount > 0;
            var useDirectFallback = timelineMatchedBodyTrackCount <= 0 && JsonInt(directRotationComparison, "matchedBodyTrackCount") > 0;
            var comparisonForDiagnosis = useDirectFallback ? directRotationComparison : internalAvatarPoseTimelineToGltfComparison;
            var timelineToGltfMax = JsonFloat(comparisonForDiagnosis, "maxDegrees");
            var timelineToGltfAvgTrackMax = JsonFloat(comparisonForDiagnosis, "avgTrackMaxDegrees");
            var playableCurveMax = JsonFloat(playableBakeToEditorCurveInternalPoseComparison, "maxDegrees");
            var matchedBodyTrackCount = JsonInt(comparisonForDiagnosis, "matchedBodyTrackCount");
            var gltfComparisonUsable = matchedBodyTrackCount > 0;

            // 真正用于反推生产 solver 的是 Unity InternalAvatarPose 时间线。
            // editorCurve / Playable bake 可能包含 root motion 或完整播放器语义差异，只作为辅助证据。
            var oracleReliable = (hasInternalAvatarPoseTimeline || (hasInternalAvatarPose && internalAvatarMax <= 0.5f)) &&
                (!hasInternalAvatarPose || internalAvatarMax <= 0.5f) &&
                (!hasSetHumanPoseTransform || setHumanPoseTransformMax <= 1.0f);

            var residual = internalHumanoidSolverVariantComparison?["currentResidualStability"] as JObject;
            var transformDelta = internalHumanoidSolverVariantComparison?["transformLocalDeltaSolverComparison"] as JObject;
            var unitySpaceConsistency = singleMuscleProbeSolverComparison?["transformVsInternalAvatarPoseProbeSummary"] as JObject;
            var singleMuscleDeltaCorrection = singleMuscleProbeSolverComparison?["singleMuscleDeltaCorrectionSummary"] as JObject;
            var singleMuscleDeltaAxis = singleMuscleProbeSolverComparison?["singleMuscleDeltaAxisSummary"] as JObject;
            var singleMuscleMultiValueLinearity = singleMuscleProbeSolverComparison?["singleMuscleMultiValueLinearitySummary"] as JObject;
            var singleMuscleSignedAxisTilt = singleMuscleDeltaAxis?["signedAxisTiltSummary"] as JObject;
            var armLegPattern = residual?["armLegCorrectionPattern"] as JObject;
            var left = residual?["leftCorrection"] as JObject;
            var right = residual?["rightCorrection"] as JObject;
            var residualUsable = string.Equals((string)residual?["status"], "ok", StringComparison.OrdinalIgnoreCase)
                && (left != null || right != null);
            var leftResidualMax = JsonFloat(left, "maxResidualDegrees");
            var rightResidualMax = JsonFloat(right, "maxResidualDegrees");
            var leftResidualAvg = JsonFloat(left, "avgTrackMaxResidualDegrees");
            var rightResidualAvg = JsonFloat(right, "avgTrackMaxResidualDegrees");
            var bestResidualMax = Math.Min(PositiveOrInfinity(leftResidualMax), PositiveOrInfinity(rightResidualMax));
            var bestResidualAvg = Math.Min(PositiveOrInfinity(leftResidualAvg), PositiveOrInfinity(rightResidualAvg));
            if (float.IsPositiveInfinity(bestResidualMax))
            {
                bestResidualMax = 0f;
            }
            if (float.IsPositiveInfinity(bestResidualAvg))
            {
                bestResidualAvg = 0f;
            }
            var residualMeaning = !residualUsable
                ? "当前 Unity oracle 缺少可用于首帧常量校正的时间线数据；只能先看直接旋转误差和最坏身体分组。"
                : bestResidualAvg <= 2f && bestResidualMax <= 5f
                    ? "首帧按骨骼补一个常量偏移后，时间线残差已经很低，通常表示主要问题是稳定的 Avatar/rest pose 到 glTF rest pose 偏移。"
                    : bestResidualAvg > 5f || bestResidualMax > 15f
                        ? "首帧按骨骼补一个常量偏移后仍然残留较大误差，通常表示动态 muscle 公式还不对，而不只是 rest pose 常量偏移。"
                        : "首帧常量偏移能解释大部分误差，但仍有少量动态残差；需要跨模型和动作复验后再迁入生产 solver。";

            var worstGroups = BuildWorstBodyGroups(comparisonForDiagnosis?["bodyGroupError"] as JObject);
            var diagnosisStatus = !gltfComparisonUsable
                ? "no_gltf_tracks"
                : !oracleReliable
                ? "oracle_unstable"
                : timelineToGltfMax <= 5f
                    ? "ok"
                : timelineToGltfMax <= 15f
                    ? "needs_review"
                    : "solver_formula_wrong";
            var errorKind = !gltfComparisonUsable
                ? "no_comparable_gltf_rotation_tracks"
                : IsTransformDeltaMismatch(transformDelta)
                ? "delta_space_formula_mismatch"
                : residualUsable && (bestResidualAvg > 5f || bestResidualMax > 15f)
                ? "dynamic_formula_error"
                : timelineToGltfMax > 5f && residualUsable
                    ? "mostly_stable_rest_offset"
                    : timelineToGltfMax > 5f
                    ? "large_error_residual_not_available"
                    : "within_tolerance";

            var armLegNextAction = (string)armLegPattern?["nextAction"];
            var unitySpacesAgree = string.Equals((string)unitySpaceConsistency?["status"], "ok", StringComparison.OrdinalIgnoreCase)
                && JsonFloat(unitySpaceConsistency, "maxDegrees") <= 1f;
            var nextAction = !string.IsNullOrWhiteSpace(armLegNextAction) && residualUsable && timelineToGltfMax > 5f
                ? armLegNextAction
                : !gltfComparisonUsable
                ? "先修正 glTF 节点路径/动画 channel 与 Unity internalAvatarPoseTimeline 的匹配，再判断公式误差。"
                : useDirectFallback
                ? "当前使用旧 Unity baked track 直接对比作为 fallback；可以先据此定位最坏身体分组，同时建议后续重跑带 internalAvatarPoseTimeline 的 Unity oracle。"
                : !oracleReliable
                ? "先刷新或重跑 Unity oracle，再比较求解公式。"
                : errorKind == "dynamic_formula_error"
                    ? "不要把当前内部 Humanoid 求解标为生产可用；继续用多模型、多动画 oracle 对比反推 muscle 到骨骼局部旋转公式，重点看最坏身体分组。"
                : errorKind == "delta_space_formula_mismatch"
                    ? unitySpacesAgree
                        ? "Unity Transform.local 与 InternalAvatarPose 的单 muscle delta 已基本一致；下一步集中修 AnimeStudio 离线 muscle 轴映射、乘法顺序和左右镜像规则，而不是继续怀疑 glTF rest 或 Unity 内部空间差异。"
                        : "当前 Transform.local delta 与离线 solver delta 明显不一致；下一步应反推 Avatar 轴系 muscle delta 到骨骼 local delta 的固定变换，而不是只迁入 rest anchor。"
                    : timelineToGltfMax > 5f
                        ? "优先检查 Avatar/rest pose 到 glTF node rest pose 的稳定偏移映射。"
                        : "当前样本已接近可接受，但仍需要跨模型和动作复验。";

            return new JObject
            {
                ["status"] = diagnosisStatus,
                ["rule"] = "Diagnostic summary only. It condenses Unity oracle comparisons so solver formula changes can be judged across many clips without reading the full report.",
                ["oracleReliable"] = oracleReliable,
                ["oracleEvidence"] = new JObject
                {
                    ["editorCurveHumanPoseMaxDegrees"] = editorCurveMax,
                    ["internalAvatarPoseMaxDegrees"] = internalAvatarMax,
                    ["setHumanPoseTransformMaxDegrees"] = setHumanPoseTransformMax,
                    ["playableBakeToEditorCurveMaxDegrees"] = playableCurveMax,
                    ["internalAvatarPoseTimelineMatchedBodyTrackCount"] = timelineMatchedBodyTrackCount,
                    ["transformVsInternalAvatarPoseProbe"] = SummarizeUnitySpaceConsistency(unitySpaceConsistency),
                    ["singleMuscleDeltaCorrection"] = SummarizeSingleMuscleDeltaCorrection(singleMuscleDeltaCorrection),
                    ["singleMuscleDeltaAxis"] = SummarizeSingleMuscleDeltaAxis(singleMuscleDeltaAxis),
                    ["singleMuscleMultiValueLinearity"] = SummarizeSingleMuscleMultiValueLinearity(singleMuscleMultiValueLinearity),
                    ["singleMuscleSignedAxisTilt"] = SummarizeSingleMuscleSignedAxisTilt(singleMuscleSignedAxisTilt),
                    ["usedForReliability"] = "internalAvatarPoseTimeline availability, plus internalAvatarPoseMaxDegrees / setHumanPoseTransformMaxDegrees when available",
                    ["note"] = "InternalAvatarPose 时间线可用时，说明 Unity oracle 可以用于反推公式；editorCurve / Playable bake 可能包含 root motion 或完整播放器语义差异，只作为辅助证据。",
                },
                ["currentGltfError"] = new JObject
                {
                    ["gltfComparisonUsable"] = gltfComparisonUsable,
                    ["comparisonSource"] = useDirectFallback ? "direct_baked_track_fallback" : "internal_avatar_pose_timeline",
                    ["matchedBodyTrackCount"] = matchedBodyTrackCount,
                    ["maxDegrees"] = timelineToGltfMax,
                    ["avgTrackMaxDegrees"] = timelineToGltfAvgTrackMax,
                    ["errorKind"] = errorKind,
                    ["worstBodyGroups"] = worstGroups,
                },
                ["constantResidualAfterFirstFrameCorrection"] = new JObject
                {
                    ["residualUsable"] = residualUsable,
                    ["bestMaxResidualDegrees"] = bestResidualMax,
                    ["bestAvgTrackMaxResidualDegrees"] = bestResidualAvg,
                    ["meaning"] = residualMeaning,
                },
                ["restOffsetCandidateReadiness"] = BuildRestOffsetCandidateReadinessSummary(residual),
                ["armLegCorrectionPattern"] = SummarizeArmLegPatternForDiagnosis(armLegPattern),
                ["transformLocalDeltaSolver"] = SummarizeTransformLocalDeltaForDiagnosis(transformDelta),
                ["nextAction"] = nextAction,
            };
        }

        private static JObject SummarizeSingleMuscleMultiValueLinearity(JObject summary)
        {
            if (!string.Equals((string)summary?["status"], "ok", StringComparison.OrdinalIgnoreCase))
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["meaning"] = "当前报告没有多幅度 single-muscle probe；需要用新版 Unity helper 重跑带 probeMuscles 的 oracle。",
                };
            }

            var mostlyLinearRate = JsonFloat(summary, "mostlyLinearRate");
            var limitExplainedRate = JsonFloat(summary, "limitExplainedRate");
            var focusLimitExplainedRate = JsonFloat(summary, "focusLimitExplainedRate");
            var focusMaxAxisSpread = JsonFloat(summary, "focusMaxAxisSpreadDegrees");
            var maxSlopeError = JsonFloat(summary, "maxRelativeSlopeError");
            var maxAxisSpread = JsonFloat(summary, "maxAxisSpreadDegrees");
            return new JObject
            {
                ["status"] = "ok",
                ["rowCount"] = JsonInt(summary, "rowCount"),
                ["multiValueRowCount"] = JsonInt(summary, "multiValueRowCount"),
                ["mostlyLinearRate"] = mostlyLinearRate,
                ["limitExplainedRate"] = limitExplainedRate,
                ["focusLimitExplainedRate"] = focusLimitExplainedRate,
                ["focusMaxAxisSpreadDegrees"] = focusMaxAxisSpread,
                ["maxRelativeSlopeError"] = maxSlopeError,
                ["maxAxisSpreadDegrees"] = maxAxisSpread,
                ["limitScalingLikely"] = MathF.Max(limitExplainedRate, focusLimitExplainedRate) >= 0.55f,
                ["axisStableEnoughForFormulaProbe"] = focusMaxAxisSpread > 0f ? focusMaxAxisSpread <= 5f : maxAxisSpread <= 5f,
                ["meaning"] = MathF.Max(limitExplainedRate, focusLimitExplainedRate) >= 0.55f
                    ? "多幅度 probe 显示多数 muscle 的角度幅度可由 AvatarConstant limitMin/limitMax 分段解释；剩余重点应追 source/target 轴、左右镜像和 swing/twist 空间。"
                    : "多幅度 probe 还不能稳定由 AvatarConstant limit 解释；迁入生产公式前需要继续检查曲线基准值、轴映射或 Unity probe 输入。",
            };
        }

        private static JObject SummarizeSingleMuscleSignedAxisTilt(JObject summary)
        {
            if (!string.Equals((string)summary?["status"], "ok", StringComparison.OrdinalIgnoreCase))
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["meaning"] = "当前报告没有 signed-axis tilt 诊断；需要重跑新版 compare 报告。",
                };
            }

            var maxTilt = JsonFloat(summary, "maxAxisTiltDegrees");
            var maxUnitySpread = JsonFloat(summary, "maxUnityAxisSpreadDegrees");
            return new JObject
            {
                ["status"] = "ok",
                ["groupCount"] = JsonInt(summary, "groupCount"),
                ["maxAxisTiltDegrees"] = maxTilt,
                ["avgAxisTiltDegrees"] = JsonFloat(summary, "avgAxisTiltDegrees"),
                ["maxUnityAxisSpreadDegrees"] = maxUnitySpread,
                ["avgUnityAxisSpreadDegrees"] = JsonFloat(summary, "avgUnityAxisSpreadDegrees"),
                ["unityAxisStable"] = maxUnitySpread <= 1f,
                ["needsTiltedAxisFormula"] = maxTilt > 10f && maxUnitySpread <= 1f,
                ["meaning"] = maxTilt > 10f && maxUnitySpread <= 1f
                    ? "Unity 单 muscle 真实轴在 +1/-1 probe 中稳定，但当前 solver 轴有明显倾斜偏差；下一步应反推 Unity 的倾斜轴定义和 swing/twist 组合，而不是继续做全局符号或角度缩放。"
                    : maxUnitySpread > 1f
                        ? "Unity 单 muscle 真实轴在 +1/-1 probe 中不够稳定，需先检查 probe/base pose 或该 muscle 是否属于 stretch/translation 特例。"
                        : "当前 signed-axis tilt 较小；可优先检查其它误差来源。",
            };
        }

        private static JObject SummarizeSingleMuscleDeltaAxis(JObject summary)
        {
            if (!string.Equals((string)summary?["status"], "ok", StringComparison.OrdinalIgnoreCase))
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["meaning"] = "当前报告没有单 muscle delta axis 诊断；需要重跑带 probeMuscles 的 Unity oracle 和 compare。",
                };
            }

            var avgRatio = JsonFloat(summary, "avgAngleRatio");
            var maxAxis = JsonFloat(summary, "maxAxisErrorDegrees");
            var avgAxis = JsonFloat(summary, "avgAxisErrorDegrees");
            var magnitudeLooksClose = avgRatio >= 0.9f && avgRatio <= 1.1f;
            return new JObject
            {
                ["status"] = "ok",
                ["rowCount"] = JsonInt(summary, "rowCount"),
                ["maxAxisErrorDegrees"] = maxAxis,
                ["avgAxisErrorDegrees"] = avgAxis,
                ["avgAngleRatio"] = avgRatio,
                ["magnitudeLooksClose"] = magnitudeLooksClose,
                ["axisBasisLikelyWrong"] = maxAxis > 10f && magnitudeLooksClose,
                ["meaning"] = maxAxis > 10f && magnitudeLooksClose
                    ? "单 muscle 的旋转角度幅度大体接近 Unity，但旋转轴方向偏差明显；优先追 Avatar/local 轴基、左右镜像和 pre/postQ 乘法空间。"
                    : maxAxis <= 10f && !magnitudeLooksClose
                        ? "单 muscle 的旋转轴方向大体接近 Unity，但角度比例偏差明显；优先追 muscle limit、sign 或 twist/stretch 权重。"
                        : "单 muscle 的旋转轴和角度幅度都仍需继续拆分；不要只凭完整 timeline 误差迁入生产公式。",
            };
        }

        private static JObject SummarizeSingleMuscleDeltaCorrection(JObject summary)
        {
            if (!string.Equals((string)summary?["status"], "ok", StringComparison.OrdinalIgnoreCase))
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["meaning"] = "当前报告没有单 muscle delta correction 诊断；需要重跑带 probeMuscles 的 Unity oracle 和 compare。",
                };
            }

            var maxConsistency = JsonFloat(summary, "maxCorrectionConsistencyByHumanBoneDegrees");
            var maxCurrent = JsonFloat(summary, "maxCurrentErrorDegrees");
            return new JObject
            {
                ["status"] = "ok",
                ["rowCount"] = JsonInt(summary, "rowCount"),
                ["maxCurrentErrorDegrees"] = maxCurrent,
                ["avgCurrentErrorDegrees"] = JsonFloat(summary, "avgCurrentErrorDegrees"),
                ["maxCorrectionConsistencyByHumanBoneDegrees"] = maxConsistency,
                ["fixedCorrectionLikely"] = maxConsistency <= 5f && maxCurrent > 5f,
                ["meaning"] = maxConsistency <= 5f
                    ? "单 muscle 的缺口接近稳定 correction，后续可以尝试把该 correction 解释为 Avatar/rest/preQ/postQ 的确定性空间变换。"
                    : "同一骨骼内不同 muscle/value 需要的 correction 差异很大，说明不能只补一个固定 rest offset；应继续修 muscle 轴映射、乘法顺序或左右镜像规则。",
            };
        }

        private static bool IsTransformDeltaMismatch(JObject delta)
        {
            if (!string.Equals((string)delta?["status"], "ok", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var best = MinPositive(
                JsonFloat(delta["leftDelta"] as JObject, "maxDegrees"),
                JsonFloat(delta["rightDelta"] as JObject, "maxDegrees"),
                JsonFloat(delta["crossLeftToRightDelta"] as JObject, "maxDegrees"),
                JsonFloat(delta["crossRightToLeftDelta"] as JObject, "maxDegrees"));
            return best > 15f;
        }

        private static JObject SummarizeUnitySpaceConsistency(JObject summary)
        {
            if (!string.Equals((string)summary?["status"], "ok", StringComparison.OrdinalIgnoreCase))
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["rule"] = "Unity single-muscle Transform.local vs InternalAvatarPose delta comparison was not available in this oracle result.",
                };
            }

            var max = JsonFloat(summary, "maxDegrees");
            var avg = JsonFloat(summary, "avgTrackMaxDegrees");
            return new JObject
            {
                ["status"] = "ok",
                ["matchedTrackCount"] = JsonInt(summary, "matchedTrackCount"),
                ["maxDegrees"] = max,
                ["avgTrackMaxDegrees"] = avg,
                ["spacesAgree"] = max <= 1f,
                ["meaning"] = max <= 1f
                    ? "Unity Transform.localRotation 与 HumanPoseHandler.GetInternalAvatarPose 的单 muscle delta 基本一致；离线 solver 应直接复现这套本地 TRS delta。"
                    : "Unity Transform.localRotation 与 InternalAvatarPose 的单 muscle delta 仍有明显差异；需要先确认 Unity oracle 绑定路径或 Avatar pose handler 构造。",
            };
        }

        private static JObject SummarizeTransformLocalDeltaForDiagnosis(JObject delta)
        {
            if (!string.Equals((string)delta?["status"], "ok", StringComparison.OrdinalIgnoreCase))
            {
                return new JObject
                {
                    ["status"] = "not_available",
                };
            }

            var leftMax = JsonFloat(delta["leftDelta"] as JObject, "maxDegrees");
            var rightMax = JsonFloat(delta["rightDelta"] as JObject, "maxDegrees");
            var crossLeftRightMax = JsonFloat(delta["crossLeftToRightDelta"] as JObject, "maxDegrees");
            var crossRightLeftMax = JsonFloat(delta["crossRightToLeftDelta"] as JObject, "maxDegrees");
            var bestMax = MinPositive(leftMax, rightMax, crossLeftRightMax, crossRightLeftMax);
            return new JObject
            {
                ["status"] = "ok",
                ["bestMaxDegrees"] = bestMax,
                ["leftDeltaMaxDegrees"] = leftMax,
                ["rightDeltaMaxDegrees"] = rightMax,
                ["crossLeftToRightDeltaMaxDegrees"] = crossLeftRightMax,
                ["crossRightToLeftDeltaMaxDegrees"] = crossRightLeftMax,
                ["interpretation"] = bestMax <= 5f
                    ? "current_delta_formula_matches_unity"
                    : bestMax <= 15f
                        ? "current_delta_formula_needs_review"
                        : "current_delta_formula_wrong",
            };
        }

        private static JObject SummarizeArmLegPatternForDiagnosis(JObject pattern)
        {
            if (!string.Equals((string)pattern?["status"], "ok", StringComparison.OrdinalIgnoreCase))
            {
                return new JObject
                {
                    ["status"] = "not_available",
                };
            }

            return new JObject
            {
                ["status"] = "ok",
                ["armInterpretation"] = (string)pattern["arm"]?["interpretation"],
                ["legInterpretation"] = (string)pattern["leg"]?["interpretation"],
                ["armWorstNearestCandidatePair"] = (string)pattern["arm"]?["worstNearestCandidatePair"],
                ["legWorstNearestCandidatePair"] = (string)pattern["leg"]?["worstNearestCandidatePair"],
                ["armWorstCrossSideMirroredCandidatePair"] = (string)pattern["arm"]?["worstCrossSideMirroredCandidatePair"],
                ["legWorstCrossSideMirroredCandidatePair"] = (string)pattern["leg"]?["worstCrossSideMirroredCandidatePair"],
                ["armMaxNearestCandidateGapDegrees"] = (float?)pattern["arm"]?["maxNearestCandidateGapDegrees"] ?? 0f,
                ["legMaxNearestCandidateGapDegrees"] = (float?)pattern["leg"]?["maxNearestCandidateGapDegrees"] ?? 0f,
                ["armMaxCrossSideMirroredCandidateGapDegrees"] = (float?)pattern["arm"]?["maxCrossSideMirroredCandidateGapDegrees"] ?? 0f,
                ["legMaxCrossSideMirroredCandidateGapDegrees"] = (float?)pattern["leg"]?["maxCrossSideMirroredCandidateGapDegrees"] ?? 0f,
                ["armCrossSideMirroredCandidateReadyPairCount"] = (int?)pattern["arm"]?["crossSideMirroredCandidateReadyPairCount"] ?? 0,
                ["legCrossSideMirroredCandidateReadyPairCount"] = (int?)pattern["leg"]?["crossSideMirroredCandidateReadyPairCount"] ?? 0,
                ["armParentChainNearestPairCount"] = (int?)pattern["arm"]?["parentChainNearestPairCount"] ?? 0,
                ["legParentChainNearestPairCount"] = (int?)pattern["leg"]?["parentChainNearestPairCount"] ?? 0,
                ["nextAction"] = (string)pattern["nextAction"],
            };
        }

        private static JObject BuildRestOffsetCandidateReadinessSummary(JObject residual)
        {
            if (!string.Equals((string)residual?["status"], "ok", StringComparison.OrdinalIgnoreCase))
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["rule"] = "No residual candidate fit data is available.",
                };
            }

            var left = residual["leftCorrection"]?["candidateFitAll"] as JObject;
            var right = residual["rightCorrection"]?["candidateFitAll"] as JObject;
            var leftBest = SelectBestFullCoverageCandidate(left);
            var rightBest = SelectBestFullCoverageCandidate(right);
            var groupRows = new JArray();
            foreach (var row in SelectBestGroupCandidates(left, "left").Concat(SelectBestGroupCandidates(right, "right")))
            {
                groupRows.Add(row);
            }

            var hasGoodGlobal = IsGoodRestCandidate(leftBest) || IsGoodRestCandidate(rightBest);
            var weakGroups = groupRows
                .OfType<JObject>()
                .Where(x => !IsGoodRestCandidate(x["candidate"] as JObject))
                .Select(x => $"{(string)x["side"]}:{(string)x["bodyGroup"]}")
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new JObject
            {
                ["status"] = hasGoodGlobal
                    ? "global_candidate_ready_for_more_validation"
                    : weakGroups.Length == 0 && groupRows.Count > 0
                        ? "body_group_candidates_need_cross_model_validation"
                        : "no_production_candidate_yet",
                ["rule"] = "Diagnostic only: checks whether stored rest/avatar/preQ/postQ candidates explain the learned stable offset well enough to consider a production solver change. Do not migrate a candidate unless it is stable across models and clips.",
                ["thresholds"] = new JObject
                {
                    ["avgGapDegrees"] = 3.0,
                    ["maxGapDegrees"] = 10.0,
                },
                ["bestGlobalCandidates"] = new JObject
                {
                    ["left"] = leftBest,
                    ["right"] = rightBest,
                },
                ["bestByBodyGroup"] = groupRows,
                ["weakBodyGroups"] = new JArray(weakGroups),
            };
        }

        private static JObject SelectBestFullCoverageCandidate(JObject fit)
        {
            return (fit?["bestFullCoverageCandidates"] as JArray)?
                .OfType<JObject>()
                .OrderBy(x => (float?)x["avgGapDegrees"] ?? float.MaxValue)
                .ThenBy(x => (float?)x["maxGapDegrees"] ?? float.MaxValue)
                .FirstOrDefault();
        }

        private static IEnumerable<JObject> SelectBestGroupCandidates(JObject fit, string side)
        {
            foreach (var group in (fit?["bestByBodyGroup"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var candidate = (group["topCandidates"] as JArray)?
                    .OfType<JObject>()
                    .OrderBy(x => (float?)x["avgGapDegrees"] ?? float.MaxValue)
                    .ThenBy(x => (float?)x["maxGapDegrees"] ?? float.MaxValue)
                    .FirstOrDefault();
                if (candidate == null)
                {
                    continue;
                }

                yield return new JObject
                {
                    ["side"] = side,
                    ["bodyGroup"] = (string)group["bodyGroup"],
                    ["trackCount"] = (int?)group["trackCount"] ?? 0,
                    ["candidate"] = candidate,
                    ["ready"] = IsGoodRestCandidate(candidate),
                };
            }
        }

        private static bool IsGoodRestCandidate(JObject candidate)
        {
            if (candidate == null)
            {
                return false;
            }

            return ((float?)candidate["avgGapDegrees"] ?? float.MaxValue) <= 3f &&
                ((float?)candidate["maxGapDegrees"] ?? float.MaxValue) <= 10f;
        }

        private static JArray BuildWorstBodyGroups(JObject bodyGroupError)
        {
            var rows = new List<JObject>();
            if (bodyGroupError == null)
            {
                return new JArray();
            }

            foreach (var property in bodyGroupError.Properties())
            {
                if (property.Value is not JObject group)
                {
                    continue;
                }

                rows.Add(new JObject
                {
                    ["group"] = property.Name,
                    ["maxDegrees"] = JsonFloat(group, "maxDegrees"),
                    ["avgTrackMaxDegrees"] = JsonFloat(group, "avgTrackMaxDegrees"),
                    ["worstPath"] = (string)group["worstPath"],
                });
            }

            return new JArray(rows
                .OrderByDescending(x => (float?)x["maxDegrees"] ?? 0f)
                .ThenBy(x => (string)x["group"], StringComparer.OrdinalIgnoreCase));
        }

        private static bool IsStatusAvailable(JObject value)
        {
            var status = (string)value?["status"];
            return !string.IsNullOrWhiteSpace(status) &&
                !string.Equals(status, "not_available", StringComparison.OrdinalIgnoreCase);
        }

        private static float JsonFloat(JObject value, string propertyName)
        {
            return (float?)value?[propertyName] ?? 0f;
        }

        private static float? JsonOptionalFloat(JObject value, string propertyName)
        {
            return (float?)value?[propertyName];
        }

        private static float BodyGroupJsonFloat(JObject value, string bodyGroup, string propertyName)
        {
            return JsonFloat(value?["bodyGroupError"]?[bodyGroup] as JObject, propertyName);
        }

        private static int JsonInt(JObject value, string propertyName)
        {
            return (int?)value?[propertyName] ?? 0;
        }

        private static float PositiveOrInfinity(float value)
        {
            return value > 0f ? value : float.PositiveInfinity;
        }

        private static float MinPositive(params float[] values)
        {
            var best = values
                .Where(x => x > 0f)
                .DefaultIfEmpty(0f)
                .Min();
            return best;
        }

        private static bool IsRequest(JObject value) => value["animeStudioAssets"] != null && value["outputJson"] != null;

        private static string ResolveDecodedAnimationAsset(JObject request, JObject unityResult, string gltfPath)
        {
            var requestAsset = (string)request?["animeStudioAssets"]?["animation"]?["animationAsset"];
            var clipName = (string)unityResult?["clipName"] ?? Path.GetFileNameWithoutExtension(requestAsset ?? string.Empty);
            var previewAsset = FindPreviewAnimationAsset(gltfPath, clipName);
            if (!string.IsNullOrWhiteSpace(previewAsset))
            {
                return previewAsset;
            }
            return !string.IsNullOrWhiteSpace(requestAsset) && File.Exists(requestAsset) ? requestAsset : null;
        }

        private static string FindPreviewAnimationAsset(string gltfPath, string clipName)
        {
            if (string.IsNullOrWhiteSpace(gltfPath) || string.IsNullOrWhiteSpace(clipName))
            {
                return null;
            }

            var current = Directory.Exists(gltfPath)
                ? new DirectoryInfo(gltfPath)
                : new FileInfo(gltfPath).Directory;
            for (var depth = 0; current != null && depth < 8; depth++, current = current.Parent)
            {
                var animationsDir = Path.Combine(current.FullName, "Animations");
                if (!Directory.Exists(animationsDir))
                {
                    continue;
                }

                var exact = Directory.EnumerateFiles(animationsDir, $"{clipName}.animation_asset.json", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(exact))
                {
                    return exact;
                }

                var safe = SafeFileName(clipName);
                if (!string.Equals(safe, clipName, StringComparison.Ordinal))
                {
                    var safeMatch = Directory.EnumerateFiles(animationsDir, $"{safe}.animation_asset.json", SearchOption.AllDirectories)
                        .FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(safeMatch))
                    {
                        return safeMatch;
                    }
                }
            }
            return null;
        }

        private static string SafeFileName(string value)
        {
            var result = value ?? string.Empty;
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                result = result.Replace(c, '_');
            }
            return result;
        }

        private static JObject BuildMuscleDecodeComparison(JObject unityResult, string animationAsset)
        {
            var samples = unityResult?["humanoidPoseSamples"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var muscleNames = unityResult?["muscleNames"]?.Values<string>().ToArray() ?? Array.Empty<string>();
            if (samples.Length == 0 || muscleNames.Length == 0 || string.IsNullOrWhiteSpace(animationAsset) || !File.Exists(animationAsset))
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["sampleCount"] = samples.Length,
                    ["muscleNameCount"] = muscleNames.Length,
                    ["animationAsset"] = animationAsset,
                };
            }

            var asset = JObject.Parse(File.ReadAllText(animationAsset));
            var curves = LoadDecodedFloatCurves(asset["decoded"]?["floats"] as JArray);
            var rows = new List<MuscleCompareRow>();
            for (var muscleIndex = 0; muscleIndex < muscleNames.Length; muscleIndex++)
            {
                var name = muscleNames[muscleIndex];
                if (string.IsNullOrWhiteSpace(name) || !curves.TryGetValue(name, out var curve) || curve.Count == 0)
                {
                    continue;
                }

                var errors = new List<float>();
                foreach (var sample in samples)
                {
                    var muscles = sample["muscles"] as JArray;
                    if (muscles == null || muscleIndex >= muscles.Count)
                    {
                        continue;
                    }
                    var time = (float?)sample["time"] ?? 0f;
                    var unityValue = (float?)muscles[muscleIndex] ?? 0f;
                    var decodedValue = SampleFloatCurve(curve, time);
                    errors.Add(Math.Abs(unityValue - decodedValue));
                }

                if (errors.Count > 0)
                {
                    rows.Add(new MuscleCompareRow(name, muscleIndex, errors.Count, errors.Max(), errors.Average()));
                }
            }

            var ordered = rows
                .OrderByDescending(x => x.MaxAbsError)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new JObject
            {
                ["status"] = ordered.Length == 0 ? "no_matching_decoded_curves" : "ok",
                ["rule"] = "Diagnostic only: compares Unity HumanPose.muscles sampled during PlayableGraph bake against AnimeStudio decoded muscle float curves. Large errors point to a decoder/reference-pose issue before skeleton TRS solving.",
                ["animationAsset"] = animationAsset,
                ["sampleCount"] = samples.Length,
                ["decodedMuscleCurveCount"] = curves.Count,
                ["matchedMuscleCurveCount"] = ordered.Length,
                ["maxAbsError"] = ordered.Length == 0 ? 0 : ordered[0].MaxAbsError,
                ["avgCurveMaxAbsError"] = ordered.Length == 0 ? 0 : ordered.Average(x => x.MaxAbsError),
                ["topErrors"] = ToJsonRows(ordered.Take(64)),
            };
        }

        private static JObject BuildEditorCurveDecodeComparison(JObject unityResult, string animationAsset)
        {
            var editorCurves = LoadUnityEditorCurveTracks(unityResult?["editorCurveTracks"] as JArray);
            if (editorCurves.Count == 0 || string.IsNullOrWhiteSpace(animationAsset) || !File.Exists(animationAsset))
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["unityEditorCurveCount"] = editorCurves.Count,
                    ["animationAsset"] = animationAsset,
                };
            }

            var asset = JObject.Parse(File.ReadAllText(animationAsset));
            var decodedCurves = LoadDecodedFloatCurves(asset["decoded"]?["floats"] as JArray);
            var rows = new List<MuscleCompareRow>();
            foreach (var pair in editorCurves)
            {
                if (!decodedCurves.TryGetValue(pair.Key, out var decoded) || decoded.Count == 0)
                {
                    continue;
                }

                var errors = new List<float>();
                foreach (var key in pair.Value)
                {
                    errors.Add(Math.Abs(key.Value - SampleFloatCurve(decoded, key.Time)));
                }
                if (errors.Count > 0)
                {
                    rows.Add(new MuscleCompareRow(pair.Key, -1, errors.Count, errors.Max(), errors.Average()));
                }
            }

            var ordered = rows
                .OrderByDescending(x => x.MaxAbsError)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new JObject
            {
                ["status"] = ordered.Length == 0 ? "no_matching_curves" : "ok",
                ["rule"] = "Diagnostic only: compares Unity AnimationUtility.GetEditorCurve values against AnimeStudio decoded float curves. Near-zero errors mean the serialized curve decoder is aligned; remaining pose errors belong to Avatar/HumanPose solving.",
                ["animationAsset"] = animationAsset,
                ["unityEditorCurveCount"] = editorCurves.Count,
                ["decodedFloatCurveCount"] = decodedCurves.Count,
                ["matchedCurveCount"] = ordered.Length,
                ["maxAbsError"] = ordered.Length == 0 ? 0 : ordered[0].MaxAbsError,
                ["avgCurveMaxAbsError"] = ordered.Length == 0 ? 0 : ordered.Average(x => x.MaxAbsError),
                ["topErrors"] = ToJsonRows(ordered.Take(64)),
            };
        }

        private static JObject BuildEditorCurveHumanPoseRotationComparison(JObject unityResult)
        {
            var playableTracks = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var track in unityResult?["tracks"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var path = (string)track["path"];
                var rotations = track["rotations"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
                if (string.IsNullOrWhiteSpace(path) || rotations.Length == 0)
                {
                    continue;
                }
                var first = rotations[0];
                playableTracks[path] = Normalize(new[]
                {
                    (float?)first["x"] ?? 0f,
                    (float?)first["y"] ?? 0f,
                    (float?)first["z"] ?? 0f,
                    (float?)first["w"] ?? 1f,
                });
            }

            var rows = new List<TrackCompareRow>();
            var missing = new List<string>();
            foreach (var track in unityResult?["editorCurveHumanPoseRotations"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var path = (string)track["path"];
                var rotation = track["rotation"] as JObject;
                if (string.IsNullOrWhiteSpace(path) || rotation == null)
                {
                    continue;
                }

                if (!playableTracks.TryGetValue(path, out var playableRotation))
                {
                    missing.Add(path);
                    continue;
                }

                var humanPoseRotation = Normalize(new[]
                {
                    (float?)rotation["x"] ?? 0f,
                    (float?)rotation["y"] ?? 0f,
                    (float?)rotation["z"] ?? 0f,
                    (float?)rotation["w"] ?? 1f,
                });
                rows.Add(new TrackCompareRow(
                    path,
                    path,
                    -1,
                    1,
                    QuaternionAngleDegrees(humanPoseRotation, playableRotation),
                    QuaternionAngleDegrees(humanPoseRotation, playableRotation),
                    IsBodyBone(path)
                ));
            }

            var ordered = rows
                .OrderByDescending(x => x.MaxRotationErrorDegrees)
                .ThenBy(x => x.UnityPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new JObject
            {
                ["status"] = ordered.Length == 0 ? "not_available" : missing.Count == 0 ? "ok" : "warning",
                ["rule"] = "Diagnostic only: compares Unity rotations produced by writing AnimationUtility editor-curve muscle values into HumanPoseHandler.SetHumanPose against the first PlayableGraph-baked frame. Low arm/leg errors prove the decoded muscle values are suitable input and remaining internal glTF errors belong to the Avatar/HumanPose-to-skeleton solver.",
                ["matchedTrackCount"] = ordered.Length,
                ["missingTrackCount"] = missing.Count,
                ["missingTracks"] = new JArray(missing.Take(128)),
                ["maxDegrees"] = ordered.Length == 0 ? 0 : ordered[0].MaxRotationErrorDegrees,
                ["avgTrackMaxDegrees"] = ordered.Length == 0 ? 0 : ordered.Average(x => x.MaxRotationErrorDegrees),
                ["bodyGroupError"] = BuildBodyGroupErrorSummary(ordered),
                ["topErrors"] = ToJsonRows(ordered.Take(64)),
            };
        }

        private static JObject BuildInternalAvatarPoseComparison(JObject unityResult)
        {
            var targetRotations = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var track in unityResult?["editorCurveHumanPoseRotations"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var path = NormalizeBakePath((string)track["path"]);
                var rotation = track["rotation"] as JObject;
                if (string.IsNullOrWhiteSpace(path) || rotation == null)
                {
                    continue;
                }

                targetRotations[path] = ReadQuaternion(rotation);
            }

            var snapshot = unityResult?["internalAvatarPoseSnapshots"]?.OfType<JObject>()
                .FirstOrDefault(x =>
                    string.Equals((string)x["label"], "afterEditorCurveSetHumanPose", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals((string)x["status"], "ok", StringComparison.OrdinalIgnoreCase));
            if (snapshot == null)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["snapshotCount"] = unityResult?["internalAvatarPoseSnapshots"]?.OfType<JObject>().Count() ?? 0,
                };
            }

            var jointPaths = snapshot["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
            var values = snapshot["values"]?.Values<float>().ToArray() ?? Array.Empty<float>();
            var rowCount = Math.Min(jointPaths.Length, values.Length / 7);
            var internalRotations = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < rowCount; i++)
            {
                var path = jointPaths[i] ?? string.Empty;
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var offset = i * 7;
                internalRotations[path] = Normalize(new[]
                {
                    values[offset + 3],
                    values[offset + 4],
                    values[offset + 5],
                    values[offset + 6],
                });
            }

            var rows = new List<TrackCompareRow>();
            var missing = new List<string>();
            foreach (var pair in targetRotations)
            {
                if (!TryGetRotationByPath(internalRotations, pair.Key, out var internalPath, out var internalRotation))
                {
                    missing.Add(pair.Key);
                    continue;
                }

                var error = QuaternionAngleDegrees(internalRotation, pair.Value);
                rows.Add(new TrackCompareRow(pair.Key, internalPath, -1, 1, error, error, IsBodyBone(pair.Key)));
            }

            var ordered = rows
                .OrderByDescending(x => x.MaxRotationErrorDegrees)
                .ThenBy(x => x.UnityPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new JObject
            {
                ["status"] = ordered.Length == 0 ? "not_available" : missing.Count == 0 ? "ok" : "warning",
                ["rule"] = "Diagnostic only: compares HumanPoseHandler.GetInternalAvatarPose joint rotations (T.xyz + Q.xyzw rows in jointPaths order) against the local rotations produced by SetHumanPose from Unity editor-curve muscles. Near-zero error proves Unity exposes the exact target joint pose we need to reproduce offline.",
                ["snapshotLabel"] = (string)snapshot["label"],
                ["requestedLength"] = (int?)snapshot["requestedLength"] ?? 0,
                ["valueCount"] = values.Length,
                ["jointPathCount"] = jointPaths.Length,
                ["poseRowCount"] = rowCount,
                ["matchedTrackCount"] = ordered.Length,
                ["missingTrackCount"] = missing.Count,
                ["missingTracks"] = new JArray(missing.Take(128)),
                ["maxDegrees"] = ordered.Length == 0 ? 0 : ordered[0].MaxRotationErrorDegrees,
                ["avgTrackMaxDegrees"] = ordered.Length == 0 ? 0 : ordered.Average(x => x.MaxRotationErrorDegrees),
                ["topErrors"] = ToJsonRows(ordered.Take(64)),
            };
        }

        private static JObject BuildInternalAvatarPoseToGltfComparison(
            JObject unityResult,
            Dictionary<string, int> nodePathToIndex,
            Dictionary<int, GltfRotationTrack> rotationChannels)
        {
            var snapshot = unityResult?["internalAvatarPoseSnapshots"]?.OfType<JObject>()
                .FirstOrDefault(x =>
                    string.Equals((string)x["label"], "afterEditorCurveSetHumanPose", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals((string)x["status"], "ok", StringComparison.OrdinalIgnoreCase));
            if (snapshot == null)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["snapshotCount"] = unityResult?["internalAvatarPoseSnapshots"]?.OfType<JObject>().Count() ?? 0,
                    ["gltfRotationChannelCount"] = rotationChannels?.Count ?? 0,
                };
            }

            var jointPaths = snapshot["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
            var values = snapshot["values"]?.Values<float>().ToArray() ?? Array.Empty<float>();
            var rowCount = Math.Min(jointPaths.Length, values.Length / 7);
            var sampleTime = ResolveFirstEditorCurveSampleTime(unityResult);
            var rows = new List<TrackCompareRow>();
            var missingNodes = new List<string>();
            var missingChannels = new List<string>();

            for (var i = 0; i < rowCount; i++)
            {
                var path = NormalizeBakePath(jointPaths[i] ?? string.Empty);
                if (string.IsNullOrWhiteSpace(path) || !IsBodyBone(path))
                {
                    continue;
                }

                if (!TryGetNodeByPath(nodePathToIndex, path, out var gltfPath, out var nodeIndex))
                {
                    missingNodes.Add(path);
                    continue;
                }
                if (!rotationChannels.TryGetValue(nodeIndex, out var gltfTrack))
                {
                    missingChannels.Add(path);
                    continue;
                }

                var offset = i * 7;
                var unityRotation = Normalize(new[]
                {
                    values[offset + 3],
                    values[offset + 4],
                    values[offset + 5],
                    values[offset + 6],
                });
                var unityGltfRotation = UnityToGltfRotation(unityRotation);
                var gltfRotation = SampleRotation(gltfTrack, sampleTime);
                var error = QuaternionAngleDegrees(unityGltfRotation, gltfRotation);
                rows.Add(new TrackCompareRow(path, gltfPath, nodeIndex, 1, error, error, true));
            }

            var ordered = rows
                .OrderByDescending(x => x.MaxRotationErrorDegrees)
                .ThenBy(x => x.UnityPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new JObject
            {
                ["status"] = ordered.Length == 0 ? "not_available" : missingNodes.Count == 0 && missingChannels.Count == 0 ? "ok" : "warning",
                ["rule"] = "Diagnostic only: compares Unity HumanPoseHandler.GetInternalAvatarPose local joint rotations against the current AnimeStudio glTF internal solver output at the first editor-curve sample time. Large leg/arm errors are direct evidence for Avatar/HumanPose solver formula mismatch.",
                ["snapshotLabel"] = (string)snapshot["label"],
                ["sampleTime"] = sampleTime,
                ["jointPathCount"] = jointPaths.Length,
                ["poseRowCount"] = rowCount,
                ["gltfRotationChannelCount"] = rotationChannels?.Count ?? 0,
                ["matchedBodyTrackCount"] = ordered.Length,
                ["missingBodyNodeCount"] = missingNodes.Count,
                ["missingBodyChannelCount"] = missingChannels.Count,
                ["missingBodyNodes"] = new JArray(missingNodes.Take(128)),
                ["missingBodyChannels"] = new JArray(missingChannels.Take(128)),
                ["maxDegrees"] = ordered.Length == 0 ? 0 : ordered[0].MaxRotationErrorDegrees,
                ["avgTrackMaxDegrees"] = ordered.Length == 0 ? 0 : ordered.Average(x => x.MaxRotationErrorDegrees),
                ["bodyGroupError"] = BuildBodyGroupErrorSummary(ordered),
                ["topErrors"] = ToJsonRows(ordered.Take(64)),
            };
        }

        private static JObject BuildPlayableBakeToEditorCurveInternalPoseComparison(JObject unityResult)
        {
            return BuildUnityRotationTracksToInternalPoseComparison(
                unityResult,
                unityResult?["tracks"] as JArray,
                "playable",
                "Diagnostic only: compares Unity PlayableGraph-baked local rotations against the joint rotations produced by writing editor-curve muscle values into HumanPoseHandler.GetInternalAvatarPose. High errors here mean the oracle is missing root/body/motion or other Humanoid playback state before the offline solver formula is judged.");
        }

        private static JObject BuildSetHumanPoseTransformToInternalAvatarPoseComparison(JObject unityResult)
        {
            return BuildUnityRotationTracksToInternalPoseComparison(
                unityResult,
                unityResult?["editorCurveSetHumanPoseTransformTracks"] as JArray,
                "setHumanPoseTransform",
                "Diagnostic only: compares real Transform.localRotation after HumanPoseHandler.SetHumanPose(editor-curve muscles) against HumanPoseHandler.GetInternalAvatarPose for the same sampled muscles. This isolates whether InternalAvatarPose itself differs from the transform pose Unity applies.");
        }

        private static JObject BuildUnityRotationTracksToInternalPoseComparison(
            JObject unityResult,
            JArray sourceTrackArray,
            string sourceName,
            string rule)
        {
            var samples = unityResult?["internalAvatarPoseTimeline"]?.OfType<JObject>()
                .Where(x => string.Equals((string)x["status"], "ok", StringComparison.OrdinalIgnoreCase))
                .ToArray() ?? Array.Empty<JObject>();
            var sourceTracks = LoadUnityPlayableRotationTracks(sourceTrackArray);
            if (samples.Length == 0 || sourceTracks.Count == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["timelineSampleCount"] = samples.Length,
                    [$"{sourceName}RotationTrackCount"] = sourceTracks.Count,
                };
            }

            var errorsByPath = new Dictionary<string, TimelineTrackAccumulator>(StringComparer.OrdinalIgnoreCase);
            var missingSourceTracks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var usedSampleCount = 0;

            foreach (var sample in samples)
            {
                var time = (float?)sample["time"] ?? 0f;
                var jointPaths = sample["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
                var values = sample["values"]?.Values<float>().ToArray() ?? Array.Empty<float>();
                var rowCount = Math.Min(jointPaths.Length, values.Length / 7);
                var usedThisSample = false;

                for (var i = 0; i < rowCount; i++)
                {
                    var path = NormalizeBakePath(jointPaths[i] ?? string.Empty);
                    if (string.IsNullOrWhiteSpace(path) || !IsBodyBone(path))
                    {
                        continue;
                    }

                    if (!TryGetPlayableRotationTrack(sourceTracks, path, out var sourcePath, out var sourceKeys))
                    {
                        missingSourceTracks.Add(path);
                        continue;
                    }

                    var offset = i * 7;
                    var internalRotation = Normalize(new[]
                    {
                        values[offset + 3],
                        values[offset + 4],
                        values[offset + 5],
                        values[offset + 6],
                    });
                    var sourceRotation = SampleUnityRotation(sourceKeys, time);
                    var error = QuaternionAngleDegrees(internalRotation, sourceRotation);

                    if (!errorsByPath.TryGetValue(path, out var accumulator))
                    {
                        accumulator = new TimelineTrackAccumulator(path, sourcePath, -1);
                        errorsByPath[path] = accumulator;
                    }
                    accumulator.Add(error);
                    usedThisSample = true;
                }

                if (usedThisSample)
                {
                    usedSampleCount++;
                }
            }

            var rows = errorsByPath.Values
                .Select(x => x.ToRow())
                .OrderByDescending(x => x.MaxRotationErrorDegrees)
                .ThenBy(x => x.UnityPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new JObject
            {
                ["status"] = rows.Length == 0 ? "not_available" : missingSourceTracks.Count == 0 ? "ok" : "warning",
                ["rule"] = rule,
                ["timelineSampleCount"] = samples.Length,
                ["usedTimelineSampleCount"] = usedSampleCount,
                [$"{sourceName}RotationTrackCount"] = sourceTracks.Count,
                ["matchedBodyTrackCount"] = rows.Length,
                [$"missing{char.ToUpperInvariant(sourceName[0])}{sourceName.Substring(1)}TrackCount"] = missingSourceTracks.Count,
                [$"missing{char.ToUpperInvariant(sourceName[0])}{sourceName.Substring(1)}Tracks"] = new JArray(missingSourceTracks.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(128)),
                ["maxDegrees"] = rows.Length == 0 ? 0 : rows[0].MaxRotationErrorDegrees,
                ["avgTrackMaxDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.MaxRotationErrorDegrees),
                ["avgTrackAvgDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.AvgRotationErrorDegrees),
                ["bodyGroupError"] = BuildBodyGroupErrorSummary(rows),
                ["topErrors"] = ToJsonRows(rows.Take(64)),
            };
        }

        private static JObject BuildInternalAvatarPoseTimelineToGltfComparison(
            JObject unityResult,
            Dictionary<string, int> nodePathToIndex,
            Dictionary<int, GltfRotationTrack> rotationChannels)
        {
            var samples = unityResult?["internalAvatarPoseTimeline"]?.OfType<JObject>()
                .Where(x => string.Equals((string)x["status"], "ok", StringComparison.OrdinalIgnoreCase))
                .ToArray() ?? Array.Empty<JObject>();
            if (samples.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["timelineSampleCount"] = unityResult?["internalAvatarPoseTimeline"]?.OfType<JObject>().Count() ?? 0,
                    ["gltfRotationChannelCount"] = rotationChannels?.Count ?? 0,
                };
            }

            var errorsByPath = new Dictionary<string, TimelineTrackAccumulator>(StringComparer.OrdinalIgnoreCase);
            var missingNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var missingChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var usedSampleCount = 0;

            foreach (var sample in samples)
            {
                var time = (float?)sample["time"] ?? 0f;
                var jointPaths = sample["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
                var values = sample["values"]?.Values<float>().ToArray() ?? Array.Empty<float>();
                var rowCount = Math.Min(jointPaths.Length, values.Length / 7);
                var usedThisSample = false;

                for (var i = 0; i < rowCount; i++)
                {
                    var path = NormalizeBakePath(jointPaths[i] ?? string.Empty);
                    if (string.IsNullOrWhiteSpace(path) || !IsBodyBone(path))
                    {
                        continue;
                    }

                    if (!TryGetNodeByPath(nodePathToIndex, path, out var gltfPath, out var nodeIndex))
                    {
                        missingNodes.Add(path);
                        continue;
                    }
                    if (!rotationChannels.TryGetValue(nodeIndex, out var gltfTrack))
                    {
                        missingChannels.Add(path);
                        continue;
                    }

                    var offset = i * 7;
                    var unityRotation = Normalize(new[]
                    {
                        values[offset + 3],
                        values[offset + 4],
                        values[offset + 5],
                        values[offset + 6],
                    });
                    var unityGltfRotation = UnityToGltfRotation(unityRotation);
                    var gltfRotation = SampleRotation(gltfTrack, time);
                    var error = QuaternionAngleDegrees(unityGltfRotation, gltfRotation);

                    if (!errorsByPath.TryGetValue(path, out var accumulator))
                    {
                        accumulator = new TimelineTrackAccumulator(path, gltfPath, nodeIndex);
                        errorsByPath[path] = accumulator;
                    }
                    accumulator.Add(error);
                    usedThisSample = true;
                }

                if (usedThisSample)
                {
                    usedSampleCount++;
                }
            }

            var rows = errorsByPath.Values
                .Select(x => x.ToRow())
                .OrderByDescending(x => x.MaxRotationErrorDegrees)
                .ThenBy(x => x.UnityPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new JObject
            {
                ["status"] = rows.Length == 0 ? "not_available" : missingNodes.Count == 0 && missingChannels.Count == 0 ? "ok" : "warning",
                ["rule"] = "Diagnostic only: compares multi-time Unity HumanPoseHandler.GetInternalAvatarPose local rotations against AnimeStudio glTF internal solver channels. This is the main oracle for measuring Humanoid/Muscle formula quality without using Unity as a production bake step.",
                ["timelineSampleCount"] = samples.Length,
                ["usedTimelineSampleCount"] = usedSampleCount,
                ["gltfRotationChannelCount"] = rotationChannels?.Count ?? 0,
                ["matchedBodyTrackCount"] = rows.Length,
                ["missingBodyNodeCount"] = missingNodes.Count,
                ["missingBodyChannelCount"] = missingChannels.Count,
                ["missingBodyNodes"] = new JArray(missingNodes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(128)),
                ["missingBodyChannels"] = new JArray(missingChannels.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(128)),
                ["maxDegrees"] = rows.Length == 0 ? 0 : rows[0].MaxRotationErrorDegrees,
                ["avgTrackMaxDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.MaxRotationErrorDegrees),
                ["avgTrackAvgDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.AvgRotationErrorDegrees),
                ["bodyGroupError"] = BuildBodyGroupErrorSummary(rows),
                ["topErrors"] = ToJsonRows(rows.Take(64)),
            };
        }

        private static JObject BuildZeroMusclePoseCandidateComparison(
            JObject request,
            JObject unityResult,
            JObject[] gltfNodes,
            Dictionary<string, int> nodePathToIndex)
        {
            var solver = request?["animeStudioAssets"]?["model"]?["avatar"]?["internalSolver"] as JObject;
            var humanBoneIndex = solver?["humanBoneIndex"] as JArray;
            var skeleton = solver?["skeleton"] as JObject;
            var solverNodes = skeleton?["nodes"] as JArray;
            var solverAxes = skeleton?["axes"] as JArray;
            var snapshots = unityResult?["internalAvatarPoseSnapshots"]?.OfType<JObject>()
                .Where(x => string.Equals((string)x["status"], "ok", StringComparison.OrdinalIgnoreCase)
                    && (((string)x["label"])?.StartsWith("zeroMuscle", StringComparison.OrdinalIgnoreCase) == true))
                .ToArray() ?? Array.Empty<JObject>();

            if (solver == null || humanBoneIndex == null || solverNodes == null || solverAxes == null || snapshots.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["hasInternalSolver"] = solver != null,
                    ["zeroMuscleSnapshotCount"] = snapshots.Length,
                };
            }

            var snapshotReports = new JArray();
            foreach (var snapshot in snapshots)
            {
                var label = (string)snapshot["label"];
                var jointPaths = snapshot["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
                var values = snapshot["values"]?.Values<float>().ToArray() ?? Array.Empty<float>();
                var candidates = new Dictionary<string, Dictionary<string, TimelineTrackAccumulator>>(StringComparer.OrdinalIgnoreCase);
                var missingTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var target in BuildCurrentSolverTargets())
                {
                    if (!TryGetSolverTarget(solverNodes, solverAxes, humanBoneIndex, target, out var targetPath, out var axis) ||
                        !TryFindJointIndex(jointPaths, targetPath, out var jointIndex))
                    {
                        missingTargets.Add(target.HumanBone);
                        continue;
                    }

                    var offset = jointIndex * 7;
                    if (offset + 6 >= values.Length)
                    {
                        missingTargets.Add(target.HumanBone);
                        continue;
                    }

                    var unityRotation = Normalize(new[]
                    {
                        values[offset + 3],
                        values[offset + 4],
                        values[offset + 5],
                        values[offset + 6],
                    });

                    var zeroSolved = Normalize(Multiply(Normalize(axis.PreQ), Inverse(Normalize(axis.PostQ))));
                    AddZeroMuscleCandidate(candidates, "axis_zeroSolved_pre_inversePost", targetPath, unityRotation, zeroSolved);
                    AddZeroMuscleCandidate(candidates, "gltfRest", targetPath, unityRotation,
                        TryGetGltfRestRotation(gltfNodes, nodePathToIndex, targetPath, out var gltfRest) ? gltfRest : null);
                    AddZeroMuscleCandidate(candidates, "gltfParentRest", targetPath, unityRotation,
                        TryGetGltfParentRestRotation(gltfNodes, nodePathToIndex, targetPath, out var parentRest) ? parentRest : null);
                    AddZeroMuscleCandidate(candidates, "humanSkeletonPose", targetPath, unityRotation,
                        ResolveVariantPose(solver, solverNodes, humanBoneIndex, target.HumanBone, targetPath, "humanSkeletonPose", gltfNodes, nodePathToIndex));
                    AddZeroMuscleCandidate(candidates, "humanSkeletonLocalPose", targetPath, unityRotation,
                        ResolveVariantPose(solver, solverNodes, humanBoneIndex, target.HumanBone, targetPath, "humanSkeletonLocalPose", gltfNodes, nodePathToIndex));
                    AddZeroMuscleCandidate(candidates, "avatarDefaultPoseBySameIndex", targetPath, unityRotation,
                        ResolveVariantPose(solver, solverNodes, humanBoneIndex, target.HumanBone, targetPath, "avatarDefaultPoseBySameIndex", gltfNodes, nodePathToIndex));
                    AddZeroMuscleCandidate(candidates, "avatarDefaultLocalPoseBySameIndex", targetPath, unityRotation,
                        ResolveVariantPose(solver, solverNodes, humanBoneIndex, target.HumanBone, targetPath, "avatarDefaultLocalPoseBySameIndex", gltfNodes, nodePathToIndex));
                    AddZeroMuscleCandidate(candidates, "avatarSkeletonPoseByHumanSkeletonIndex", targetPath, unityRotation,
                        ResolveVariantPose(solver, solverNodes, humanBoneIndex, target.HumanBone, targetPath, "avatarSkeletonPoseByHumanSkeletonIndex", gltfNodes, nodePathToIndex));
                    AddZeroMuscleCandidate(candidates, "avatarSkeletonLocalPoseByHumanSkeletonIndex", targetPath, unityRotation,
                        ResolveVariantPose(solver, solverNodes, humanBoneIndex, target.HumanBone, targetPath, "avatarSkeletonLocalPoseByHumanSkeletonIndex", gltfNodes, nodePathToIndex));
                    AddZeroMuscleCandidate(candidates, "avatarSkeletonDefaultPoseByHumanSkeletonIndex", targetPath, unityRotation,
                        ResolveVariantPose(solver, solverNodes, humanBoneIndex, target.HumanBone, targetPath, "avatarDefaultPoseByHumanSkeletonIndex", gltfNodes, nodePathToIndex));
                    AddZeroMuscleCandidate(candidates, "avatarSkeletonDefaultLocalPoseByHumanSkeletonIndex", targetPath, unityRotation,
                        ResolveVariantPose(solver, solverNodes, humanBoneIndex, target.HumanBone, targetPath, "avatarDefaultLocalPoseByHumanSkeletonIndex", gltfNodes, nodePathToIndex));
                }

                var candidateReports = new JArray();
                foreach (var pair in candidates.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var rows = pair.Value.Values
                        .Select(x => x.ToRow())
                        .OrderByDescending(x => x.MaxRotationErrorDegrees)
                        .ThenBy(x => x.UnityPath, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    candidateReports.Add(new JObject
                    {
                        ["name"] = pair.Key,
                        ["matchedTrackCount"] = rows.Length,
                        ["maxDegrees"] = rows.Length == 0 ? 0 : rows[0].MaxRotationErrorDegrees,
                        ["avgTrackMaxDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.MaxRotationErrorDegrees),
                        ["bodyGroupError"] = BuildBodyGroupErrorSummary(rows),
                        ["topErrors"] = ToJsonRows(rows.Take(24)),
                    });
                }

                snapshotReports.Add(new JObject
                {
                    ["label"] = label,
                    ["candidateCount"] = candidateReports.Count,
                    ["missingTargetCount"] = missingTargets.Count,
                    ["missingTargets"] = new JArray(missingTargets.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(64)),
                    ["candidates"] = candidateReports,
                });
            }

            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: compares Unity zero-muscle HumanPoseHandler.GetInternalAvatarPose local rotations against exported Avatar/rest candidates. This identifies the deterministic rest offset needed by the offline Humanoid solver.",
                ["snapshotCount"] = snapshotReports.Count,
                ["snapshots"] = snapshotReports,
            };
        }

        private static void AddZeroMuscleCandidate(
            Dictionary<string, Dictionary<string, TimelineTrackAccumulator>> candidates,
            string name,
            string path,
            float[] unityRotation,
            float[] candidateRotation)
        {
            if (candidateRotation == null)
            {
                return;
            }

            if (!candidates.TryGetValue(name, out var tracks))
            {
                tracks = new Dictionary<string, TimelineTrackAccumulator>(StringComparer.OrdinalIgnoreCase);
                candidates[name] = tracks;
            }
            if (!tracks.TryGetValue(path, out var accumulator))
            {
                accumulator = new TimelineTrackAccumulator(path, path, -1);
                tracks[path] = accumulator;
            }
            accumulator.Add(QuaternionAngleDegrees(unityRotation, candidateRotation));
        }

        private static JObject BuildZeroMuscleResidualCorrectionSummary(
            JObject request,
            JObject unityResult,
            JObject[] gltfNodes,
            Dictionary<string, int> nodePathToIndex)
        {
            var solver = request?["animeStudioAssets"]?["model"]?["avatar"]?["internalSolver"] as JObject;
            var humanBoneIndex = solver?["humanBoneIndex"] as JArray;
            var skeleton = solver?["skeleton"] as JObject;
            var solverNodes = skeleton?["nodes"] as JArray;
            var solverAxes = skeleton?["axes"] as JArray;
            var snapshots = unityResult?["internalAvatarPoseSnapshots"]?.OfType<JObject>()
                .Where(x => string.Equals((string)x["status"], "ok", StringComparison.OrdinalIgnoreCase)
                    && (((string)x["label"])?.StartsWith("zeroMuscle", StringComparison.OrdinalIgnoreCase) == true))
                .ToArray() ?? Array.Empty<JObject>();

            if (solver == null || humanBoneIndex == null || solverNodes == null || solverAxes == null || snapshots.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["hasInternalSolver"] = solver != null,
                    ["zeroMuscleSnapshotCount"] = snapshots.Length,
                };
            }

            var gltfParents = BuildChildToParent(gltfNodes ?? Array.Empty<JObject>());
            var snapshotReports = new JArray();
            foreach (var snapshot in snapshots)
            {
                var label = (string)snapshot["label"];
                var jointPaths = snapshot["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
                var values = snapshot["values"]?.Values<float>().ToArray() ?? Array.Empty<float>();
                var rows = new JArray();
                var missingTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var target in BuildCurrentSolverTargets())
                {
                    if (!TryGetSolverTarget(solverNodes, solverAxes, humanBoneIndex, target, out var targetPath, out var axis) ||
                        !TryFindJointIndex(jointPaths, targetPath, out var jointIndex))
                    {
                        missingTargets.Add(target.HumanBone);
                        continue;
                    }

                    var offset = jointIndex * 7;
                    if (offset + 6 >= values.Length)
                    {
                        missingTargets.Add(target.HumanBone);
                        continue;
                    }

                    var unityZero = Normalize(new[]
                    {
                        values[offset + 3],
                        values[offset + 4],
                        values[offset + 5],
                        values[offset + 6],
                    });
                    var currentZero = Normalize(Multiply(Normalize(axis.PreQ), Inverse(Normalize(axis.PostQ))));
                    var leftCorrection = Normalize(Multiply(unityZero, Inverse(currentZero)));
                    var rightCorrection = Normalize(Multiply(Inverse(currentZero), unityZero));
                    var candidates = BuildResidualCorrectionCandidates(targetPath, axis, solver, solverNodes, humanBoneIndex, target.HumanBone, gltfNodes, gltfParents, nodePathToIndex);
                    var nearestLeft = FindNearestCorrectionCandidate(leftCorrection, candidates);
                    var nearestRight = FindNearestCorrectionCandidate(rightCorrection, candidates);

                    rows.Add(new JObject
                    {
                        ["humanBone"] = target.HumanBone,
                        ["path"] = targetPath,
                        ["bodyGroup"] = ClassifyBodyGroup(targetPath),
                        ["currentZeroErrorDegrees"] = QuaternionAngleDegrees(unityZero, currentZero),
                        ["leftCorrectionAngleDegrees"] = QuaternionAngleDegrees(IdentityQuaternion, leftCorrection),
                        ["nearestLeftCorrectionSource"] = nearestLeft.Name,
                        ["nearestLeftCorrectionSourceErrorDegrees"] = nearestLeft.AngleDegrees,
                        ["rightCorrectionAngleDegrees"] = QuaternionAngleDegrees(IdentityQuaternion, rightCorrection),
                        ["nearestRightCorrectionSource"] = nearestRight.Name,
                        ["nearestRightCorrectionSourceErrorDegrees"] = nearestRight.AngleDegrees,
                    });
                }

                var orderedRows = new JArray(rows
                    .OfType<JObject>()
                    .OrderByDescending(x => (float?)x["currentZeroErrorDegrees"] ?? 0f)
                    .ThenBy(x => (string)x["path"], StringComparer.OrdinalIgnoreCase));
                snapshotReports.Add(new JObject
                {
                    ["label"] = label,
                    ["matchedTrackCount"] = orderedRows.Count,
                    ["missingTargetCount"] = missingTargets.Count,
                    ["missingTargets"] = new JArray(missingTargets.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(64)),
                    ["maxCurrentZeroErrorDegrees"] = orderedRows.Count == 0 ? 0 : orderedRows.OfType<JObject>().Max(x => (float?)x["currentZeroErrorDegrees"] ?? 0f),
                    ["avgCurrentZeroErrorDegrees"] = orderedRows.Count == 0 ? 0 : orderedRows.OfType<JObject>().Average(x => (float?)x["currentZeroErrorDegrees"] ?? 0f),
                    ["leftCorrectionSourceVotes"] = BuildCorrectionSourceVotes(orderedRows, "nearestLeftCorrectionSource"),
                    ["rightCorrectionSourceVotes"] = BuildCorrectionSourceVotes(orderedRows, "nearestRightCorrectionSource"),
                    ["topRows"] = new JArray(orderedRows.OfType<JObject>().Take(64)),
                });
            }

            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: compares Unity zero-muscle InternalAvatarPose against the current offline zero formula preQ * inverse(postQ). Per-bone correction rows help identify whether the missing static offset is explainable by exported Avatar/rest pose metadata before changing production solver math.",
                ["snapshotCount"] = snapshotReports.Count,
                ["snapshots"] = snapshotReports,
            };
        }

        private static JArray BuildCorrectionSourceVotes(JArray rows, string propertyName)
        {
            return new JArray(rows
                .OfType<JObject>()
                .Select(x => (string)x[propertyName])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(x => new JObject
                {
                    ["source"] = x.Key,
                    ["count"] = x.Count(),
                })
                .OrderByDescending(x => (int)x["count"])
                .ThenBy(x => (string)x["source"], StringComparer.OrdinalIgnoreCase));
        }

        private static JObject BuildAvatarInternalSolverMetadataSummary(JObject request)
        {
            var solver = request?["animeStudioAssets"]?["model"]?["avatar"]?["internalSolver"] as JObject;
            if (solver == null)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["reason"] = "internal_solver_missing",
                };
            }

            var skeleton = solver["skeleton"] as JObject;
            var human = solver["human"] as JObject;
            var leftHand = human?["leftHandBoneIndex"] as JArray;
            var rightHand = human?["rightHandBoneIndex"] as JArray;
            var humanBoneMass = human?["humanBoneMass"] as JArray;
            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: records whether the Unity AvatarConstant.m_Human metadata needed for Humanoid formula research is present in the request. Missing fields mean the model Avatar metadata should be refreshed before treating solver negatives as final.",
                ["hasHumanBlock"] = human != null,
                ["hasLeftHand"] = (bool?)human?["hasLeftHand"] ?? false,
                ["hasRightHand"] = (bool?)human?["hasRightHand"] ?? false,
                ["leftHandBoneIndexCount"] = leftHand?.Count ?? 0,
                ["rightHandBoneIndexCount"] = rightHand?.Count ?? 0,
                ["humanBoneMassCount"] = humanBoneMass?.Count ?? 0,
                ["humanBoneIndexCount"] = (solver["humanBoneIndex"] as JArray)?.Count ?? 0,
                ["humanSkeletonNodeCount"] = (int?)skeleton?["nodeCount"] ?? 0,
                ["humanSkeletonAxesCount"] = (int?)skeleton?["axesCount"] ?? 0,
                ["humanSkeletonPoseCount"] = (skeleton?["humanSkeletonPose"] as JArray)?.Count ?? 0,
                ["avatarDefaultPoseCount"] = (skeleton?["avatarDefaultPose"] as JArray)?.Count ?? 0,
                ["hasHumanBoneLimits"] = solver["humanBoneLimits"] is JObject limits && limits.HasValues,
            };
        }

        private static JObject BuildAvatarInternalSolverParameterSummary(JObject request, JObject zeroMuscleShortProductSearchSummary)
        {
            var solver = request?["animeStudioAssets"]?["model"]?["avatar"]?["internalSolver"] as JObject;
            var human = solver?["human"] as JObject;
            var humanBoneMass = human?["humanBoneMass"] as JArray;
            if (solver == null || humanBoneMass == null)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["hasInternalSolver"] = solver != null,
                    ["hasHumanBlock"] = human != null,
                    ["humanBoneMassCount"] = humanBoneMass?.Count ?? 0,
                };
            }

            var zeroRows = LoadFirstZeroMuscleRowsByHumanBone(zeroMuscleShortProductSearchSummary);
            var rows = new JArray();
            foreach (var target in BuildCurrentSolverTargets())
            {
                if (!HumanBoneIndexes.TryGetValue(target.HumanBone, out var humanIndex))
                {
                    continue;
                }

                var mass = humanIndex >= 0 && humanIndex < humanBoneMass.Count
                    ? (float?)humanBoneMass[humanIndex] ?? 0f
                    : 0f;
                zeroRows.TryGetValue(target.HumanBone, out var zeroRow);
                rows.Add(new JObject
                {
                    ["humanBone"] = target.HumanBone,
                    ["bodyGroup"] = ClassifyBodyGroup(target.HumanBone),
                    ["humanBoneIndex"] = humanIndex,
                    ["humanBoneMass"] = mass,
                    ["twistRole"] = ResolveHumanBoneTwistRole(target.HumanBone),
                    ["armTwist"] = ReadSolverFloat(solver, "twist", "arm"),
                    ["foreArmTwist"] = ReadSolverFloat(solver, "twist", "foreArm"),
                    ["upperLegTwist"] = ReadSolverFloat(solver, "twist", "upperLeg"),
                    ["legTwist"] = ReadSolverFloat(solver, "twist", "leg"),
                    ["armStretch"] = ReadSolverFloat(solver, "twist", "armStretch"),
                    ["legStretch"] = ReadSolverFloat(solver, "twist", "legStretch"),
                    ["zeroCurrentErrorDegrees"] = zeroRow?["currentZeroErrorDegrees"] ?? JValue.CreateNull(),
                    ["zeroBestShortFormulaErrorDegrees"] = zeroRow?["bestUnityZeroFormulaErrorDegrees"] ?? JValue.CreateNull(),
                    ["zeroBestShortFormula"] = zeroRow?["bestUnityZeroFormula"] ?? JValue.CreateNull(),
                    ["zeroBestHandContextErrorDegrees"] = zeroRow?["bestHandContextUnityZeroFormulaErrorDegrees"] ?? JValue.CreateNull(),
                    ["zeroBestHandContextFormula"] = zeroRow?["bestHandContextUnityZeroFormula"] ?? JValue.CreateNull(),
                });
            }

            var distalRows = rows
                .OfType<JObject>()
                .Where(x => IsDistalParameterFocus((string)x["humanBone"]))
                .OrderByDescending(x => (float?)x["zeroCurrentErrorDegrees"] ?? 0f)
                .ThenBy(x => (string)x["humanBone"], StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new JObject
            {
                ["status"] = rows.Count == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: correlates AvatarConstant.m_HumanBoneMass and twist/stretch parameters with zero-muscle base errors. It does not prove a production formula by itself; use it to decide which Unity parameters should enter the next oracle formula search.",
                ["humanBoneMassCount"] = humanBoneMass.Count,
                ["hasZeroMuscleRows"] = zeroRows.Count > 0,
                ["twist"] = new JObject
                {
                    ["arm"] = ReadSolverFloat(solver, "twist", "arm"),
                    ["foreArm"] = ReadSolverFloat(solver, "twist", "foreArm"),
                    ["upperLeg"] = ReadSolverFloat(solver, "twist", "upperLeg"),
                    ["leg"] = ReadSolverFloat(solver, "twist", "leg"),
                    ["armStretch"] = ReadSolverFloat(solver, "twist", "armStretch"),
                    ["legStretch"] = ReadSolverFloat(solver, "twist", "legStretch"),
                    ["feetSpacing"] = ReadSolverFloat(solver, "twist", "feetSpacing"),
                },
                ["distalFocusRows"] = new JArray(distalRows),
                ["leftRightSymmetryRows"] = BuildAvatarParameterSymmetryRows(rows),
                ["rows"] = rows,
            };
        }

        private static JArray BuildAvatarParameterSymmetryRows(JArray rows)
        {
            var byBone = rows?
                .OfType<JObject>()
                .ToDictionary(x => (string)x["humanBone"] ?? string.Empty, x => x, StringComparer.OrdinalIgnoreCase) ??
                new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            var pairs = new[]
            {
                ("LowerArm", "LeftLowerArm", "RightLowerArm"),
                ("Hand", "LeftHand", "RightHand"),
                ("LowerLeg", "LeftLowerLeg", "RightLowerLeg"),
                ("Foot", "LeftFoot", "RightFoot"),
                ("UpperArm", "LeftUpperArm", "RightUpperArm"),
                ("UpperLeg", "LeftUpperLeg", "RightUpperLeg"),
            };
            return new JArray(pairs.Select(pair =>
            {
                byBone.TryGetValue(pair.Item2, out var left);
                byBone.TryGetValue(pair.Item3, out var right);
                var leftMass = JsonFloat(left, "humanBoneMass");
                var rightMass = JsonFloat(right, "humanBoneMass");
                var leftError = JsonFloat(left, "zeroCurrentErrorDegrees");
                var rightError = JsonFloat(right, "zeroCurrentErrorDegrees");
                return new JObject
                {
                    ["pair"] = pair.Item1,
                    ["leftHumanBone"] = pair.Item2,
                    ["rightHumanBone"] = pair.Item3,
                    ["leftMass"] = left == null ? null : JToken.FromObject(leftMass),
                    ["rightMass"] = right == null ? null : JToken.FromObject(rightMass),
                    ["massDelta"] = left == null || right == null ? null : JToken.FromObject(Math.Abs(leftMass - rightMass)),
                    ["leftZeroCurrentErrorDegrees"] = left == null ? null : JToken.FromObject(leftError),
                    ["rightZeroCurrentErrorDegrees"] = right == null ? null : JToken.FromObject(rightError),
                    ["zeroErrorDeltaDegrees"] = left == null || right == null ? null : JToken.FromObject(Math.Abs(leftError - rightError)),
                    ["sameTwistRole"] = string.Equals((string)left?["twistRole"], (string)right?["twistRole"], StringComparison.OrdinalIgnoreCase),
                    ["leftTwistRole"] = left?["twistRole"] ?? JValue.CreateNull(),
                    ["rightTwistRole"] = right?["twistRole"] ?? JValue.CreateNull(),
                };
            }));
        }

        private static Dictionary<string, JObject> LoadFirstZeroMuscleRowsByHumanBone(JObject summary)
        {
            var result = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            var snapshot = summary?["snapshots"]?.OfType<JObject>().FirstOrDefault();
            foreach (var row in snapshot?["topRows"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var humanBone = (string)row["humanBone"];
                if (!string.IsNullOrWhiteSpace(humanBone) && !result.ContainsKey(humanBone))
                {
                    result[humanBone] = row;
                }
            }
            return result;
        }

        private static JToken ReadSolverFloat(JObject solver, string group, string propertyName)
        {
            var value = (float?)solver?[group]?[propertyName];
            return value.HasValue ? JToken.FromObject(value.Value) : JValue.CreateNull();
        }

        private static string ResolveHumanBoneTwistRole(string humanBone)
        {
            if (string.IsNullOrWhiteSpace(humanBone))
            {
                return null;
            }

            var roles = new List<string>();
            foreach (var family in new[] { "arm", "foreArm", "upperLeg", "leg" })
            {
                if (IsTwistParentTarget(humanBone, family))
                {
                    roles.Add(family + ":parent");
                }
                if (IsTwistChildTarget(humanBone, family))
                {
                    roles.Add(family + ":child");
                }
            }
            return roles.Count == 0 ? null : string.Join(",", roles);
        }

        private static bool IsDistalParameterFocus(string humanBone) =>
            string.Equals(humanBone, "LeftLowerArm", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(humanBone, "RightLowerArm", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(humanBone, "LeftLowerLeg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(humanBone, "RightLowerLeg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(humanBone, "LeftHand", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(humanBone, "RightHand", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(humanBone, "LeftFoot", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(humanBone, "RightFoot", StringComparison.OrdinalIgnoreCase);

        private static JObject BuildZeroMuscleShortProductSearchSummary(
            JObject request,
            JObject unityResult,
            JObject[] gltfNodes,
            Dictionary<string, int> nodePathToIndex)
        {
            var solver = request?["animeStudioAssets"]?["model"]?["avatar"]?["internalSolver"] as JObject;
            var humanBoneIndex = solver?["humanBoneIndex"] as JArray;
            var skeleton = solver?["skeleton"] as JObject;
            var solverNodes = skeleton?["nodes"] as JArray;
            var solverAxes = skeleton?["axes"] as JArray;
            var snapshots = unityResult?["internalAvatarPoseSnapshots"]?.OfType<JObject>()
                .Where(x => string.Equals((string)x["status"], "ok", StringComparison.OrdinalIgnoreCase)
                    && (((string)x["label"])?.StartsWith("zeroMuscle", StringComparison.OrdinalIgnoreCase) == true))
                .ToArray() ?? Array.Empty<JObject>();

            if (solver == null || humanBoneIndex == null || solverNodes == null || solverAxes == null || snapshots.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["hasInternalSolver"] = solver != null,
                    ["zeroMuscleSnapshotCount"] = snapshots.Length,
                };
            }

            var gltfParents = BuildChildToParent(gltfNodes ?? Array.Empty<JObject>());
            var snapshotReports = new JArray();
            foreach (var snapshot in snapshots)
            {
                var label = (string)snapshot["label"];
                var jointPaths = snapshot["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
                var values = snapshot["values"]?.Values<float>().ToArray() ?? Array.Empty<float>();
                var rows = new JArray();
                var missingTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var target in BuildCurrentSolverTargets())
                {
                    if (!TryGetSolverTarget(solverNodes, solverAxes, humanBoneIndex, target, out var targetPath, out var axis) ||
                        !TryFindJointIndex(jointPaths, targetPath, out var jointIndex))
                    {
                        missingTargets.Add(target.HumanBone);
                        continue;
                    }

                    var offset = jointIndex * 7;
                    if (offset + 6 >= values.Length)
                    {
                        missingTargets.Add(target.HumanBone);
                        continue;
                    }

                    var unityZero = Normalize(new[]
                    {
                        values[offset + 3],
                        values[offset + 4],
                        values[offset + 5],
                        values[offset + 6],
                    });
                    var currentZero = Normalize(Multiply(Normalize(axis.PreQ), Inverse(Normalize(axis.PostQ))));
                    var leftCorrection = Normalize(Multiply(unityZero, Inverse(currentZero)));
                    var rightCorrection = Normalize(Multiply(Inverse(currentZero), unityZero));
                    var candidates = BuildResidualCorrectionCandidates(targetPath, axis, solver, solverNodes, humanBoneIndex, target.HumanBone, gltfNodes, gltfParents, nodePathToIndex);
                    var shortProducts = BuildShortProductCorrectionCandidates(candidates);
                    var handContextCandidates = candidates
                        .Where(x => IsHandContextCandidateName(x.Key))
                        .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
                    var crossSideCandidates = candidates
                        .Where(x => IsCrossSideCandidateName(x.Key))
                        .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
                    var weightedParameterCandidates = BuildWeightedParameterCorrectionCandidates(candidates, solver, currentZero, target.HumanBone);
                    var bestUnityZero = FindNearestCorrectionCandidate(unityZero, shortProducts);
                    var bestHandUnityZero = FindNearestCorrectionCandidate(unityZero, handContextCandidates);
                    var bestCrossSideUnityZero = FindNearestCorrectionCandidate(unityZero, crossSideCandidates);
                    var bestWeightedUnityZero = FindNearestCorrectionCandidate(unityZero, weightedParameterCandidates);
                    var bestLeft = FindNearestCorrectionCandidate(leftCorrection, shortProducts);
                    var bestHandLeft = FindNearestCorrectionCandidate(leftCorrection, handContextCandidates);
                    var bestCrossSideLeft = FindNearestCorrectionCandidate(leftCorrection, crossSideCandidates);
                    var bestWeightedLeft = FindNearestCorrectionCandidate(leftCorrection, weightedParameterCandidates);
                    var bestRight = FindNearestCorrectionCandidate(rightCorrection, shortProducts);
                    var bestHandRight = FindNearestCorrectionCandidate(rightCorrection, handContextCandidates);
                    var bestCrossSideRight = FindNearestCorrectionCandidate(rightCorrection, crossSideCandidates);
                    var bestWeightedRight = FindNearestCorrectionCandidate(rightCorrection, weightedParameterCandidates);

                    rows.Add(new JObject
                    {
                        ["humanBone"] = target.HumanBone,
                        ["path"] = targetPath,
                        ["bodyGroup"] = ClassifyBodyGroup(targetPath),
                        ["currentZeroErrorDegrees"] = QuaternionAngleDegrees(unityZero, currentZero),
                        ["shortProductCandidateCount"] = shortProducts.Count,
                        ["handContextCandidateCount"] = handContextCandidates.Count,
                        ["crossSideCandidateCount"] = crossSideCandidates.Count,
                        ["weightedParameterCandidateCount"] = weightedParameterCandidates.Count,
                        ["bestUnityZeroFormula"] = bestUnityZero.Name,
                        ["bestUnityZeroFormulaErrorDegrees"] = bestUnityZero.AngleDegrees,
                        ["bestHandContextUnityZeroFormula"] = bestHandUnityZero.Name,
                        ["bestHandContextUnityZeroFormulaErrorDegrees"] = handContextCandidates.Count == 0 ? null : JToken.FromObject(bestHandUnityZero.AngleDegrees),
                        ["bestCrossSideUnityZeroFormula"] = bestCrossSideUnityZero.Name,
                        ["bestCrossSideUnityZeroFormulaErrorDegrees"] = crossSideCandidates.Count == 0 ? null : JToken.FromObject(bestCrossSideUnityZero.AngleDegrees),
                        ["bestWeightedParameterUnityZeroFormula"] = bestWeightedUnityZero.Name,
                        ["bestWeightedParameterUnityZeroFormulaErrorDegrees"] = weightedParameterCandidates.Count == 0 ? null : JToken.FromObject(bestWeightedUnityZero.AngleDegrees),
                        ["bestLeftCorrectionFormula"] = bestLeft.Name,
                        ["bestLeftCorrectionFormulaErrorDegrees"] = bestLeft.AngleDegrees,
                        ["bestHandContextLeftCorrectionFormula"] = bestHandLeft.Name,
                        ["bestHandContextLeftCorrectionFormulaErrorDegrees"] = handContextCandidates.Count == 0 ? null : JToken.FromObject(bestHandLeft.AngleDegrees),
                        ["bestCrossSideLeftCorrectionFormula"] = bestCrossSideLeft.Name,
                        ["bestCrossSideLeftCorrectionFormulaErrorDegrees"] = crossSideCandidates.Count == 0 ? null : JToken.FromObject(bestCrossSideLeft.AngleDegrees),
                        ["bestWeightedParameterLeftCorrectionFormula"] = bestWeightedLeft.Name,
                        ["bestWeightedParameterLeftCorrectionFormulaErrorDegrees"] = weightedParameterCandidates.Count == 0 ? null : JToken.FromObject(bestWeightedLeft.AngleDegrees),
                        ["bestRightCorrectionFormula"] = bestRight.Name,
                        ["bestRightCorrectionFormulaErrorDegrees"] = bestRight.AngleDegrees,
                        ["bestHandContextRightCorrectionFormula"] = bestHandRight.Name,
                        ["bestHandContextRightCorrectionFormulaErrorDegrees"] = handContextCandidates.Count == 0 ? null : JToken.FromObject(bestHandRight.AngleDegrees),
                        ["bestCrossSideRightCorrectionFormula"] = bestCrossSideRight.Name,
                        ["bestCrossSideRightCorrectionFormulaErrorDegrees"] = crossSideCandidates.Count == 0 ? null : JToken.FromObject(bestCrossSideRight.AngleDegrees),
                        ["bestWeightedParameterRightCorrectionFormula"] = bestWeightedRight.Name,
                        ["bestWeightedParameterRightCorrectionFormulaErrorDegrees"] = weightedParameterCandidates.Count == 0 ? null : JToken.FromObject(bestWeightedRight.AngleDegrees),
                    });
                }

                var orderedRows = new JArray(rows
                    .OfType<JObject>()
                    .OrderByDescending(x => (float?)x["currentZeroErrorDegrees"] ?? 0f)
                    .ThenBy(x => (string)x["path"], StringComparer.OrdinalIgnoreCase));
                snapshotReports.Add(new JObject
                {
                    ["label"] = label,
                    ["matchedTrackCount"] = orderedRows.Count,
                    ["missingTargetCount"] = missingTargets.Count,
                    ["missingTargets"] = new JArray(missingTargets.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(64)),
                    ["bestUnityZeroFormulaVotes"] = BuildCorrectionSourceVotes(orderedRows, "bestUnityZeroFormula"),
                    ["bestLeftCorrectionFormulaVotes"] = BuildCorrectionSourceVotes(orderedRows, "bestLeftCorrectionFormula"),
                    ["bestRightCorrectionFormulaVotes"] = BuildCorrectionSourceVotes(orderedRows, "bestRightCorrectionFormula"),
                    ["topRows"] = new JArray(orderedRows.OfType<JObject>().Take(64)),
                });
            }

            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: brute-forces short quaternion products from exported Avatar/rest/preQ/postQ candidates to see whether Unity zero-muscle base pose can already be reconstructed from deterministic metadata. HandContext and cross-side mirror candidates are reported separately and are not included in the cubic short-product search. A high LowerArm error here means the exported metadata or formula basis is still incomplete; do not use Unity zero oracle directly in production.",
                ["snapshotCount"] = snapshotReports.Count,
                ["snapshots"] = snapshotReports,
            };
        }

        private static JObject BuildZeroBaseSourceCandidateGateSummary(JObject zeroMuscleShortProductSearchSummary)
        {
            var firstSnapshot = zeroMuscleShortProductSearchSummary?["snapshots"]?.OfType<JObject>().FirstOrDefault();
            var rows = new JArray((firstSnapshot?["topRows"] as JArray)?.OfType<JObject>()
                .Select(BuildZeroBaseSourceCandidateGateRow)
                .Where(x => x != null)
                .OrderByDescending(x => JsonFloat(x, "improvementDegrees"))
                .ThenByDescending(x => JsonFloat(x, "currentZeroErrorDegrees"))
                .ThenBy(x => (string)x["humanBone"], StringComparer.OrdinalIgnoreCase)
                ?? Enumerable.Empty<JObject>());
            var readyRows = rows
                .OfType<JObject>()
                .Where(x => (bool?)x["beatsCurrent"] == true && JsonFloat(x, "bestFormulaErrorDegrees") <= 10f)
                .ToArray();
            return new JObject
            {
                ["status"] = rows.Count == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: gates zero-muscle Unity base pose source candidates from deterministic short products. This compares Unity zero base directly, not animation residuals; candidates still need full timeline and cross-model validation before production.",
                ["rowCount"] = rows.Count,
                ["readyCandidateCount"] = readyRows.Length,
                ["maxImprovementDegrees"] = rows.OfType<JObject>().Select(x => JsonFloat(x, "improvementDegrees")).DefaultIfEmpty(0f).Max(),
                ["migrationStatus"] = readyRows.Length > 0
                    ? "source_candidates_need_timeline_and_cross_model_gate"
                    : "not_ready_no_zero_base_source_candidate",
                ["rows"] = rows,
            };
        }

        private static JObject BuildZeroBaseSourceCandidateGateRow(JObject row)
        {
            if (row == null)
            {
                return null;
            }

            var formula = (string)row["bestUnityZeroFormula"];
            var error = JsonFloat(row, "bestUnityZeroFormulaErrorDegrees");
            var formulaSource = "shortProduct";
            var crossSideFormula = (string)row["bestCrossSideUnityZeroFormula"];
            var crossSideError = JsonOptionalFloat(row, "bestCrossSideUnityZeroFormulaErrorDegrees");
            if (!string.IsNullOrWhiteSpace(crossSideFormula) &&
                crossSideError.HasValue &&
                crossSideError.Value < error)
            {
                formula = crossSideFormula;
                error = crossSideError.Value;
                formulaSource = "crossSideDirect";
            }
            else if (!string.IsNullOrWhiteSpace(formula) &&
                formula.Contains("crossSide", StringComparison.OrdinalIgnoreCase))
            {
                formulaSource = "crossSideShortProduct";
            }
            var weightedFormula = (string)row["bestWeightedParameterUnityZeroFormula"];
            var weightedError = JsonOptionalFloat(row, "bestWeightedParameterUnityZeroFormulaErrorDegrees");
            if (!string.IsNullOrWhiteSpace(weightedFormula) &&
                weightedError.HasValue &&
                weightedError.Value < error)
            {
                formula = weightedFormula;
                error = weightedError.Value;
                formulaSource = "weightedParameter";
            }
            var current = JsonFloat(row, "currentZeroErrorDegrees");
            return new JObject
            {
                ["humanBone"] = row["humanBone"],
                ["bodyGroup"] = row["bodyGroup"],
                ["path"] = row["path"],
                ["formulaName"] = formula,
                ["formulaSource"] = formulaSource,
                ["formulaFamily"] = NormalizeZeroBaseSourceFormulaFamily(formula),
                ["currentZeroErrorDegrees"] = current,
                ["bestFormulaErrorDegrees"] = error,
                ["improvementDegrees"] = Math.Max(0f, current - error),
                ["beatsCurrent"] = error < current - 0.5f,
                ["shortProductCandidateCount"] = row["shortProductCandidateCount"],
                ["crossSideCandidateCount"] = row["crossSideCandidateCount"],
                ["weightedParameterCandidateCount"] = row["weightedParameterCandidateCount"],
                ["nextFormulaWork"] = error <= 10f
                    ? "validate_zero_base_source_formula_on_full_timeline"
                    : "expand_avatar_constant_zero_base_source_candidates",
            };
        }

        private static string NormalizeZeroBaseSourceFormulaFamily(string formula)
        {
            if (string.IsNullOrWhiteSpace(formula))
            {
                return "none";
            }

            var name = formula;
            while (name.StartsWith("inverse(", StringComparison.OrdinalIgnoreCase) && name.EndsWith(")", StringComparison.Ordinal))
            {
                name = name.Substring("inverse(".Length, name.Length - "inverse(".Length - 1);
            }

            if (name.Contains("localRest", StringComparison.OrdinalIgnoreCase))
            {
                return "localRest";
            }
            if (name.Contains("parentRest", StringComparison.OrdinalIgnoreCase))
            {
                return "parentRest";
            }
            if (name.Contains("rest", StringComparison.OrdinalIgnoreCase))
            {
                return "rest";
            }
            if (name.Contains("avatarDefault", StringComparison.OrdinalIgnoreCase))
            {
                return "avatarDefaultPose";
            }
            if (name.Contains("humanSkeleton", StringComparison.OrdinalIgnoreCase))
            {
                return "humanSkeletonPose";
            }
            if (name.Contains("avatarSkeleton", StringComparison.OrdinalIgnoreCase))
            {
                return "avatarSkeletonPose";
            }
            if (name.Contains("weightedParameter", StringComparison.OrdinalIgnoreCase))
            {
                return "weightedParameter";
            }
            if (name.Contains("preQ", StringComparison.OrdinalIgnoreCase) || name.Contains("postQ", StringComparison.OrdinalIgnoreCase))
            {
                return "prePostQ";
            }
            return name;
        }

        private static Dictionary<string, float[]> BuildWeightedParameterCorrectionCandidates(
            Dictionary<string, float[]> candidates,
            JObject solver,
            float[] currentZero,
            string humanBone)
        {
            var result = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            if (candidates == null ||
                solver == null ||
                currentZero == null ||
                string.IsNullOrWhiteSpace(humanBone) ||
                !IsWeightedParameterTarget(humanBone))
            {
                return result;
            }

            var weights = BuildHumanParameterWeights(solver, humanBone);
            if (weights.Length == 0)
            {
                return result;
            }

            var baseCandidates = candidates
                .Where(x => IsWeightedParameterBaseCandidate(x.Key))
                .Select(x => new KeyValuePair<string, float[]>(x.Key, Normalize(x.Value)))
                .Take(24)
                .ToArray();
            var inverseZero = Inverse(currentZero);
            foreach (var candidate in baseCandidates)
            {
                foreach (var weight in weights)
                {
                    // 诊断用途：Unity 的 Human 参数可能不是固定 offset，
                    // 这里只检查“参数作为旋转权重”是否能解释 zero base。
                    var weighted = Nlerp(IdentityQuaternion, candidate.Value, weight.Value);
                    var name = $"weightedParameter({weight.Name},{candidate.Key})";
                    AddNamedCorrectionCandidate(result, name, weighted);
                    AddNamedCorrectionCandidate(result, name + "*inverse(preQ*inverse(postQ))", Normalize(Multiply(weighted, inverseZero)));
                    AddNamedCorrectionCandidate(result, "inverse(preQ*inverse(postQ))*" + name, Normalize(Multiply(inverseZero, weighted)));
                }
            }

            return result;
        }

        private static HumanParameterWeight[] BuildHumanParameterWeights(JObject solver, string humanBone)
        {
            var result = new List<HumanParameterWeight>();
            var mass = TryReadHumanBoneMass(solver, humanBone);
            AddHumanParameterWeight(result, "humanBoneMass", mass);
            AddHumanParameterWeight(result, "1-humanBoneMass", mass.HasValue ? 1f - mass.Value : null);

            var twistGroup = ResolveTwistParameterGroup(humanBone);
            if (!string.IsNullOrWhiteSpace(twistGroup))
            {
                var twist = JsonOptionalFloat(solver?["twist"] as JObject, twistGroup);
                AddHumanParameterWeight(result, "twist." + twistGroup, twist);
                AddHumanParameterWeight(result, "1-twist." + twistGroup, twist.HasValue ? 1f - twist.Value : null);
            }

            var stretchGroup = ResolveStretchParameterName(humanBone);
            if (!string.IsNullOrWhiteSpace(stretchGroup))
            {
                var stretch = JsonOptionalFloat(solver?["twist"] as JObject, stretchGroup);
                AddHumanParameterWeight(result, "twist." + stretchGroup, stretch);
                AddHumanParameterWeight(result, "1-twist." + stretchGroup, stretch.HasValue ? 1f - stretch.Value : null);
            }

            return result
                .Where(x => Math.Abs(x.Value) > 0.000001f)
                .GroupBy(x => x.Name + "=" + x.Value.ToString("G9", System.Globalization.CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .Take(8)
                .ToArray();
        }

        private static bool IsWeightedParameterTarget(string humanBone) =>
            string.Equals(humanBone, "LeftLowerArm", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(humanBone, "RightLowerArm", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(humanBone, "LeftLowerLeg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(humanBone, "RightLowerLeg", StringComparison.OrdinalIgnoreCase);

        private static float? TryReadHumanBoneMass(JObject solver, string humanBone)
        {
            var humanBoneMass = solver?["human"]?["humanBoneMass"] as JArray;
            if (humanBoneMass == null ||
                string.IsNullOrWhiteSpace(humanBone) ||
                !HumanBoneIndexes.TryGetValue(humanBone, out var humanIndex) ||
                humanIndex < 0 ||
                humanIndex >= humanBoneMass.Count)
            {
                return null;
            }

            return (float?)humanBoneMass[humanIndex];
        }

        private static void AddHumanParameterWeight(List<HumanParameterWeight> result, string name, float? value)
        {
            if (result == null || string.IsNullOrWhiteSpace(name) || !value.HasValue)
            {
                return;
            }

            result.Add(new HumanParameterWeight(name, value.Value));
            result.Add(new HumanParameterWeight("-" + name, -value.Value));
        }

        private static string ResolveTwistParameterGroup(string humanBone)
        {
            if (string.IsNullOrWhiteSpace(humanBone))
            {
                return null;
            }
            if (humanBone.Contains("LowerArm", StringComparison.OrdinalIgnoreCase) ||
                humanBone.Contains("Hand", StringComparison.OrdinalIgnoreCase))
            {
                return "foreArm";
            }
            if (humanBone.Contains("UpperArm", StringComparison.OrdinalIgnoreCase))
            {
                return "arm";
            }
            if (humanBone.Contains("LowerLeg", StringComparison.OrdinalIgnoreCase) ||
                humanBone.Contains("Foot", StringComparison.OrdinalIgnoreCase))
            {
                return "leg";
            }
            if (humanBone.Contains("UpperLeg", StringComparison.OrdinalIgnoreCase))
            {
                return "upperLeg";
            }
            return null;
        }

        private static string ResolveStretchParameterName(string humanBone)
        {
            if (string.IsNullOrWhiteSpace(humanBone))
            {
                return null;
            }
            if (humanBone.Contains("Arm", StringComparison.OrdinalIgnoreCase) ||
                humanBone.Contains("Hand", StringComparison.OrdinalIgnoreCase))
            {
                return "armStretch";
            }
            if (humanBone.Contains("Leg", StringComparison.OrdinalIgnoreCase) ||
                humanBone.Contains("Foot", StringComparison.OrdinalIgnoreCase))
            {
                return "legStretch";
            }
            return null;
        }

        private static bool IsWeightedParameterBaseCandidate(string name)
        {
            if (string.IsNullOrWhiteSpace(name) ||
                name.Contains("*", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("inverse(", StringComparison.OrdinalIgnoreCase) ||
                IsHandContextCandidateName(name))
            {
                return false;
            }

            return name.Equals("rest", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("localRest", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("parentRest", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("avatarDefaultPoseBySameIndex", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("avatarDefaultLocalPoseBySameIndex", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("avatarDefaultPoseByHumanSkeletonIndex", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("avatarDefaultLocalPoseByHumanSkeletonIndex", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("humanSkeletonPose", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("humanSkeletonLocalPose", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("avatarSkeletonPoseBySameIndex", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("avatarSkeletonLocalPoseBySameIndex", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("crossSideAvatarDefaultOppositePose", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("crossSideAvatarDefaultMirrorXYOppositePose", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("crossSideAvatarDefaultTargetToOppositePose", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("crossSideAvatarDefaultMirrorXYTargetToOppositePose", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("crossSideAvatarDefaultOppositeToTargetPose", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("crossSideAvatarDefaultMirrorXYOppositeToTargetPose", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, float[]> BuildShortProductCorrectionCandidates(Dictionary<string, float[]> candidates)
        {
            var result = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            var primitives = (candidates ?? new Dictionary<string, float[]>())
                .Where(x => IsShortProductPrimitiveCandidate(x.Key))
                .Select(x => new KeyValuePair<string, float[]>(x.Key, Normalize(x.Value)))
                .ToArray();

            foreach (var primitive in primitives)
            {
                AddNamedCorrectionCandidate(result, primitive.Key, primitive.Value);
            }

            foreach (var a in primitives)
            {
                foreach (var b in primitives)
                {
                    AddNamedCorrectionCandidate(result, $"{a.Key}*{b.Key}", Multiply(a.Value, b.Value));
                }
            }

            foreach (var a in primitives)
            {
                foreach (var b in primitives)
                {
                    foreach (var c in primitives)
                    {
                        AddNamedCorrectionCandidate(result, $"{a.Key}*{b.Key}*{c.Key}", Multiply(Multiply(a.Value, b.Value), c.Value));
                    }
                }
            }

            return result;
        }

        private static bool IsShortProductPrimitiveCandidate(string name)
        {
            if (string.IsNullOrWhiteSpace(name) ||
                name.Contains("*", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("mirrorXY(", StringComparison.OrdinalIgnoreCase) ||
                IsHandContextCandidateName(name) ||
                IsCrossSideCandidateName(name))
            {
                return false;
            }

            var normalized = name.StartsWith("inverse(", StringComparison.OrdinalIgnoreCase) && name.EndsWith(")", StringComparison.Ordinal)
                ? name.Substring("inverse(".Length, name.Length - "inverse(".Length - 1)
                : name;
            return normalized.Equals("identity", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("preQ", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("postQ", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("rest", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("parentRest", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("localRest", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("Pose", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsHandContextCandidateName(string name) =>
            !string.IsNullOrWhiteSpace(name) &&
            name.Contains("HandContext", StringComparison.OrdinalIgnoreCase);

        private static bool IsCrossSideCandidateName(string name) =>
            !string.IsNullOrWhiteSpace(name) &&
            name.StartsWith("crossSide", StringComparison.OrdinalIgnoreCase);

        private static JObject BuildInternalAvatarPoseTranslationTimelineSummary(
            JObject request,
            JObject unityResult,
            JObject[] gltfNodes,
            Dictionary<string, int> nodePathToIndex)
        {
            var solver = request?["animeStudioAssets"]?["model"]?["avatar"]?["internalSolver"] as JObject;
            var humanBoneIndex = solver?["humanBoneIndex"] as JArray;
            var skeleton = solver?["skeleton"] as JObject;
            var solverNodes = skeleton?["nodes"] as JArray;
            var solverAxes = skeleton?["axes"] as JArray;
            var samples = unityResult?["internalAvatarPoseTimeline"]?.OfType<JObject>()
                .Where(x => string.Equals((string)x["status"], "ok", StringComparison.OrdinalIgnoreCase))
                .ToArray() ?? Array.Empty<JObject>();
            if (solver == null || humanBoneIndex == null || solverNodes == null || solverAxes == null || samples.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["hasInternalSolver"] = solver != null,
                    ["timelineSampleCount"] = samples.Length,
                };
            }

            var rows = new JArray();
            var missingTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in BuildCurrentSolverTargets())
            {
                if (!TryGetSolverTarget(solverNodes, solverAxes, humanBoneIndex, target, out var targetPath, out _))
                {
                    missingTargets.Add(target.HumanBone);
                    continue;
                }

                var sampleTranslations = new List<float[]>();
                foreach (var sample in samples)
                {
                    var jointPaths = sample["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
                    if (string.IsNullOrWhiteSpace(targetPath) ||
                        !TryFindJointIndex(jointPaths, targetPath, out var jointIndex))
                    {
                        continue;
                    }

                    var values = sample["values"]?.Values<float>().ToArray() ?? Array.Empty<float>();
                    var offset = jointIndex * 7;
                    if (offset + 2 >= values.Length)
                    {
                        continue;
                    }

                    sampleTranslations.Add(new[] { values[offset], values[offset + 1], values[offset + 2] });
                }

                if (string.IsNullOrWhiteSpace(targetPath) || sampleTranslations.Count == 0)
                {
                    missingTargets.Add(target.HumanBone);
                    continue;
                }

                var rest = TryGetGltfRestTranslation(gltfNodes, nodePathToIndex, targetPath, out var gltfRest)
                    ? gltfRest
                    : new[] { 0f, 0f, 0f };
                var first = sampleTranslations[0];
                var restDistances = sampleTranslations.Select(x => VectorDistance(x, rest)).ToArray();
                var frameDistances = sampleTranslations.Select(x => VectorDistance(x, first)).ToArray();
                rows.Add(new JObject
                {
                    ["humanBone"] = target.HumanBone,
                    ["path"] = targetPath,
                    ["bodyGroup"] = ClassifyBodyGroup(targetPath),
                    ["sampleCount"] = sampleTranslations.Count,
                    ["gltfRestTranslation"] = ToJArray(rest),
                    ["firstUnityTranslation"] = ToJArray(first),
                    ["maxDistanceFromGltfRest"] = restDistances.Length == 0 ? 0 : restDistances.Max(),
                    ["avgDistanceFromGltfRest"] = restDistances.Length == 0 ? 0 : restDistances.Average(),
                    ["maxFrameDeltaFromFirst"] = frameDistances.Length == 0 ? 0 : frameDistances.Max(),
                    ["avgFrameDeltaFromFirst"] = frameDistances.Length == 0 ? 0 : frameDistances.Average(),
                });
            }

            var orderedRows = new JArray(rows
                .OfType<JObject>()
                .OrderByDescending(x => (float?)x["maxFrameDeltaFromFirst"] ?? 0f)
                .ThenByDescending(x => (float?)x["maxDistanceFromGltfRest"] ?? 0f)
                .ThenBy(x => (string)x["path"], StringComparer.OrdinalIgnoreCase));
            var animatedRows = orderedRows
                .OfType<JObject>()
                .Where(x => ((float?)x["maxFrameDeltaFromFirst"] ?? 0f) > 0.0001f)
                .ToArray();

            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: checks Unity InternalAvatarPose T.xyz over time. Non-zero frame deltas mean the internal Humanoid solver may need non-root translation channels, not only rotation channels.",
                ["timelineSampleCount"] = samples.Length,
                ["matchedTrackCount"] = orderedRows.Count,
                ["animatedTranslationTrackCount"] = animatedRows.Length,
                ["maxFrameDeltaFromFirst"] = orderedRows.Count == 0 ? 0 : orderedRows.OfType<JObject>().Max(x => (float?)x["maxFrameDeltaFromFirst"] ?? 0f),
                ["maxDistanceFromGltfRest"] = orderedRows.Count == 0 ? 0 : orderedRows.OfType<JObject>().Max(x => (float?)x["maxDistanceFromGltfRest"] ?? 0f),
                ["missingTargetCount"] = missingTargets.Count,
                ["missingTargets"] = new JArray(missingTargets.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(64)),
                ["topAnimatedTranslations"] = new JArray(animatedRows.Take(64)),
                ["topRestOffsets"] = new JArray(orderedRows.OfType<JObject>().Take(64)),
                };
        }

        private static JObject BuildUnityTransformLocalPoseReferenceSummary(
            JObject unityResult,
            JObject[] gltfNodes,
            Dictionary<string, int> nodePathToIndex)
        {
            var snapshots = unityResult?["internalAvatarPoseSnapshots"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var beforeTransform = FindSnapshot(snapshots, "unityTransformLocalPoseBeforeEditorCurveSetHumanPose");
            var afterTransform = FindSnapshot(snapshots, "unityTransformLocalPoseAfterEditorCurveSetHumanPose");
            if (beforeTransform == null && afterTransform == null)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["rule"] = "Unity oracle result does not contain Transform.local pose snapshots. Re-run Unity bake with a helper version that writes unityTransformLocalPoseBefore/AfterEditorCurveSetHumanPose.",
                };
            }

            var beforeInternal = FindSnapshot(snapshots, "beforeEditorCurveSetHumanPose");
            var afterInternal = FindSnapshot(snapshots, "afterEditorCurveSetHumanPose");
            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: compares Unity Transform.local T/Q snapshots with Unity GetInternalAvatarPose and glTF rest. This helps identify whether the missing Humanoid solver offset comes from regular Transform space or HumanPoseHandler internal avatar space.",
                ["beforeTransformToGltfRest"] = CompareSnapshotToGltfRest(beforeTransform, gltfNodes, nodePathToIndex),
                ["beforeTransformToInternalAvatarPose"] = CompareSnapshotPair(beforeTransform, beforeInternal),
                ["afterTransformToInternalAvatarPose"] = CompareSnapshotPair(afterTransform, afterInternal),
            };
        }

        private static JObject FindSnapshot(JObject[] snapshots, string label)
        {
            return (snapshots ?? Array.Empty<JObject>())
                .FirstOrDefault(x => string.Equals((string)x["label"], label, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals((string)x["status"], "ok", StringComparison.OrdinalIgnoreCase));
        }

        private static JObject CompareSnapshotToGltfRest(JObject snapshot, JObject[] gltfNodes, Dictionary<string, int> nodePathToIndex)
        {
            if (snapshot == null || gltfNodes == null || nodePathToIndex == null)
            {
                return new JObject { ["status"] = "not_available" };
            }

            var rows = new List<SnapshotCompareRow>();
            var jointPaths = snapshot["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
            var values = ReadFloatArrayAll(snapshot["values"] as JArray);
            var rowCount = Math.Min(jointPaths.Length, values.Length / 7);
            for (var i = 0; i < rowCount; i++)
            {
                var path = NormalizeBakePath(jointPaths[i] ?? string.Empty);
                if (!nodePathToIndex.TryGetValue(path, out var nodeIndex) || nodeIndex < 0 || nodeIndex >= gltfNodes.Length)
                {
                    continue;
                }

                var offset = i * 7;
                var unityTranslation = new[] { values[offset + 0], values[offset + 1], values[offset + 2] };
                var unityRotation = Normalize(new[] { values[offset + 3], values[offset + 4], values[offset + 5], values[offset + 6] });
                var gltfTranslation = GltfToUnityPosition(ReadNodeTranslation(gltfNodes[nodeIndex]));
                var gltfRotation = GltfToUnityRotation(ReadNodeRotation(gltfNodes[nodeIndex]));
                rows.Add(new SnapshotCompareRow(
                    path,
                    QuaternionAngleDegrees(unityRotation, gltfRotation),
                    VectorDistance(unityTranslation, gltfTranslation),
                    IsBodyBone(path)));
            }

            return BuildSnapshotCompareSummary(rows, "unity_transform_local_to_gltf_rest");
        }

        private static JObject CompareSnapshotPair(JObject left, JObject right)
        {
            if (left == null || right == null)
            {
                return new JObject { ["status"] = "not_available" };
            }

            var leftPaths = left["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
            var rightPaths = right["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
            var leftValues = ReadFloatArrayAll(left["values"] as JArray);
            var rightValues = ReadFloatArrayAll(right["values"] as JArray);
            var rows = new List<SnapshotCompareRow>();
            for (var i = 0; i < leftPaths.Length && i * 7 + 6 < leftValues.Length; i++)
            {
                var path = NormalizeBakePath(leftPaths[i] ?? string.Empty);
                if (!TryFindJointIndex(rightPaths, path, out var rightIndex) || rightIndex * 7 + 6 >= rightValues.Length)
                {
                    continue;
                }

                var leftOffset = i * 7;
                var rightOffset = rightIndex * 7;
                var leftTranslation = new[] { leftValues[leftOffset + 0], leftValues[leftOffset + 1], leftValues[leftOffset + 2] };
                var rightTranslation = new[] { rightValues[rightOffset + 0], rightValues[rightOffset + 1], rightValues[rightOffset + 2] };
                var leftRotation = Normalize(new[] { leftValues[leftOffset + 3], leftValues[leftOffset + 4], leftValues[leftOffset + 5], leftValues[leftOffset + 6] });
                var rightRotation = Normalize(new[] { rightValues[rightOffset + 3], rightValues[rightOffset + 4], rightValues[rightOffset + 5], rightValues[rightOffset + 6] });
                rows.Add(new SnapshotCompareRow(
                    path,
                    QuaternionAngleDegrees(leftRotation, rightRotation),
                    VectorDistance(leftTranslation, rightTranslation),
                    IsBodyBone(path)));
            }

            return BuildSnapshotCompareSummary(rows, $"{(string)left["label"]}_to_{(string)right["label"]}");
        }

        private static JObject BuildSnapshotCompareSummary(List<SnapshotCompareRow> rows, string source)
        {
            var ordered = (rows ?? new List<SnapshotCompareRow>())
                .OrderByDescending(x => x.RotationDegrees)
                .ThenByDescending(x => x.TranslationDistance)
                .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var body = ordered.Where(x => x.BodyBone).ToArray();
            return new JObject
            {
                ["status"] = ordered.Length == 0 ? "not_available" : "ok",
                ["source"] = source,
                ["matchedJointCount"] = ordered.Length,
                ["matchedBodyJointCount"] = body.Length,
                ["maxRotationDegrees"] = ordered.Length == 0 ? 0 : ordered.Max(x => x.RotationDegrees),
                ["avgRotationDegrees"] = ordered.Length == 0 ? 0 : ordered.Average(x => x.RotationDegrees),
                ["bodyMaxRotationDegrees"] = body.Length == 0 ? 0 : body.Max(x => x.RotationDegrees),
                ["maxTranslationDistance"] = ordered.Length == 0 ? 0 : ordered.Max(x => x.TranslationDistance),
                ["topRotationRows"] = new JArray(ordered.Take(24).Select(x => new JObject
                {
                    ["path"] = x.Path,
                    ["rotationDegrees"] = x.RotationDegrees,
                    ["translationDistance"] = x.TranslationDistance,
                    ["bodyBone"] = x.BodyBone,
                })),
            };
        }

        private static JObject BuildInternalHumanoidSolverVariantComparison(
            JObject request,
            JObject unityResult,
            string animationAsset,
            JObject[] gltfNodes,
            Dictionary<string, int> nodePathToIndex,
            JObject zeroBaseSourceCandidateGateSummary,
            JObject singleMuscleProbeSolverComparison)
        {
            var solver = request?["animeStudioAssets"]?["model"]?["avatar"]?["internalSolver"] as JObject;
            var humanBoneIndex = solver?["humanBoneIndex"] as JArray;
            var skeleton = solver?["skeleton"] as JObject;
            var solverNodes = skeleton?["nodes"] as JArray;
            var solverAxes = skeleton?["axes"] as JArray;
            var samples = unityResult?["internalAvatarPoseTimeline"]?.OfType<JObject>()
                .Where(x => string.Equals((string)x["status"], "ok", StringComparison.OrdinalIgnoreCase))
                .ToArray() ?? Array.Empty<JObject>();
            if (solver == null || humanBoneIndex == null || solverNodes == null || solverAxes == null ||
                samples.Length == 0 || string.IsNullOrWhiteSpace(animationAsset) || !File.Exists(animationAsset))
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["hasInternalSolver"] = solver != null,
                    ["timelineSampleCount"] = samples.Length,
                    ["animationAsset"] = animationAsset,
                };
            }

            var animation = JObject.Parse(File.ReadAllText(animationAsset));
            var curves = LoadDecodedFloatCurves(animation["decoded"]?["floats"] as JArray);
            if (curves.Count == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["reason"] = "decoded_float_curves_empty",
                    ["animationAsset"] = animationAsset,
                    ["timelineSampleCount"] = samples.Length,
                };
            }

            var variants = BuildSolverVariants();
            var variantRows = new JArray();
            foreach (var variant in variants)
            {
                var accumulators = new Dictionary<string, TimelineTrackAccumulator>(StringComparer.OrdinalIgnoreCase);
                var missingTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var target in variant.Targets)
                {
                    if (!TryGetSolverTarget(solverNodes, solverAxes, humanBoneIndex, target, out var targetPath, out var axis))
                    {
                        missingTargets.Add(target.HumanBone);
                        continue;
                    }

                    foreach (var sample in samples)
                    {
                        var time = (float?)sample["time"] ?? 0f;
                        var jointPaths = sample["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
                        var values = sample["values"]?.Values<float>().ToArray() ?? Array.Empty<float>();
                        if (!TryFindJointIndex(jointPaths, targetPath, out var jointIndex))
                        {
                            continue;
                        }
                        var offset = jointIndex * 7;
                        if (offset + 6 >= values.Length)
                        {
                            continue;
                        }

                        var unityRotation = Normalize(new[]
                        {
                            values[offset + 3],
                            values[offset + 4],
                            values[offset + 5],
                            values[offset + 6],
                        });
                        var predicted = BuildVariantSolverRotation(curves, target, axis, time, variant, solver, solverNodes, humanBoneIndex, targetPath, gltfNodes, nodePathToIndex);
                        var error = QuaternionAngleDegrees(unityRotation, predicted);
                        if (!accumulators.TryGetValue(targetPath, out var accumulator))
                        {
                            accumulator = new TimelineTrackAccumulator(targetPath, targetPath, -1);
                            accumulators[targetPath] = accumulator;
                        }
                        accumulator.Add(error);
                    }
                }

                var rows = accumulators.Values
                    .Select(x => x.ToRow())
                    .OrderByDescending(x => x.MaxRotationErrorDegrees)
                    .ThenBy(x => x.UnityPath, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                variantRows.Add(new JObject
                {
                    ["name"] = variant.Name,
                    ["description"] = variant.Description,
                    ["status"] = rows.Length == 0 ? "not_available" : "ok",
                    ["matchedTrackCount"] = rows.Length,
                    ["missingTargetCount"] = missingTargets.Count,
                    ["missingTargets"] = new JArray(missingTargets.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(64)),
                    ["maxDegrees"] = rows.Length == 0 ? 0 : rows[0].MaxRotationErrorDegrees,
                    ["avgTrackMaxDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.MaxRotationErrorDegrees),
                    ["avgTrackAvgDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.AvgRotationErrorDegrees),
                    ["bodyGroupError"] = BuildBodyGroupErrorSummary(rows),
                    ["topErrors"] = ToJsonRows(rows.Take(24)),
                });
            }

            var armTwistTimelineAxisFitSummary = BuildArmTwistTimelineAxisFitSummary(samples, curves, solver, solverNodes, solverAxes, humanBoneIndex);
            var upperArmSwingTimelineFitSummary = BuildUpperArmSwingTimelineFitSummary(samples, curves, solver, solverNodes, solverAxes, humanBoneIndex);
            var upperArmFrontBackPoseAxisTimelineFitSummary = BuildUpperArmFrontBackPoseAxisTimelineFitSummary(samples, curves, solver, solverNodes, solverAxes, humanBoneIndex);
            var armCombinedTimelineFitSummary = BuildArmCombinedTimelineFitSummary(samples, curves, solver, solverNodes, solverAxes, humanBoneIndex);
            var distalStretchTimelineFitSummary = BuildDistalStretchTimelineFitSummary(samples, curves, solver, solverNodes, solverAxes, humanBoneIndex);
            var limbTimelineVariantRankingSummary = BuildLimbTimelineVariantRankingSummary(variantRows);
            var residualCandidateTimelineFitSummary = BuildResidualCandidateTimelineFitSummary(samples, curves, unityResult, solver, solverNodes, solverAxes, humanBoneIndex, gltfNodes, nodePathToIndex);
            var zeroBaseSourceTimelineFitSummary = BuildZeroBaseSourceTimelineFitSummary(samples, curves, zeroBaseSourceCandidateGateSummary, solver, solverNodes, solverAxes, humanBoneIndex, gltfNodes, nodePathToIndex);
            var zeroBaseArmTwistTimelineFitSummary = BuildZeroBaseArmTwistTimelineFitSummary(samples, curves, zeroBaseSourceCandidateGateSummary, armTwistTimelineAxisFitSummary, solver, solverNodes, solverAxes, humanBoneIndex, gltfNodes, nodePathToIndex);
            var zeroBaseForearmPairTimelineFitSummary = BuildZeroBaseForearmPairTimelineFitSummary(samples, curves, zeroBaseSourceCandidateGateSummary, singleMuscleProbeSolverComparison, solver, solverNodes, solverAxes, humanBoneIndex, gltfNodes, nodePathToIndex);
            return new JObject
            {
                ["status"] = variantRows.Count == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: recomputes selected offline Humanoid/Muscle solver formula variants from avatar.internalSolver and decoded curves, then compares them directly against Unity GetInternalAvatarPose timeline. This prevents overfitting by making failed formula ideas visible in the report.",
                ["animationAsset"] = animationAsset,
                ["decodedFloatCurveCount"] = curves.Count,
                ["timelineSampleCount"] = samples.Length,
                ["currentResidualStability"] = BuildSolverResidualStability(samples, curves, solver, solverNodes, solverAxes, humanBoneIndex, gltfNodes, nodePathToIndex),
                ["transformLocalDeltaSolverComparison"] = BuildTransformLocalDeltaSolverComparison(unityResult, curves, solver, solverNodes, solverAxes, humanBoneIndex, gltfNodes, nodePathToIndex),
                ["crossSideZeroBaseDeltaFitSummary"] = BuildCrossSideZeroBaseDeltaFitSummary(samples, curves, solver, solverNodes, solverAxes, humanBoneIndex, gltfNodes, nodePathToIndex),
                ["armTwistTimelineAxisFitSummary"] = armTwistTimelineAxisFitSummary,
                ["handTwistTimelineAxisFitSummary"] = BuildHandTwistTimelineAxisFitSummary(samples, curves, solverNodes, solverAxes, humanBoneIndex),
                ["upperArmSwingTimelineFitSummary"] = upperArmSwingTimelineFitSummary,
                ["upperArmFrontBackPoseAxisTimelineFitSummary"] = upperArmFrontBackPoseAxisTimelineFitSummary,
                ["armCombinedTimelineFitSummary"] = armCombinedTimelineFitSummary,
                ["armAxisSpaceMigrationGateSummary"] = BuildArmAxisSpaceMigrationGateSummary(armTwistTimelineAxisFitSummary, upperArmSwingTimelineFitSummary, armCombinedTimelineFitSummary),
                ["armFormulaTimelineGateSummary"] = BuildArmFormulaTimelineGateSummary(armTwistTimelineAxisFitSummary, upperArmFrontBackPoseAxisTimelineFitSummary),
                ["legFormulaTimelineGateSummary"] = BuildLegFormulaTimelineGateSummary(distalStretchTimelineFitSummary, limbTimelineVariantRankingSummary),
                ["distalStretchTimelineFitSummary"] = distalStretchTimelineFitSummary,
                ["zeroBaseCandidateGateSummary"] = BuildZeroBaseCandidateGateSummary(residualCandidateTimelineFitSummary),
                ["zeroBaseSourceTimelineFitSummary"] = zeroBaseSourceTimelineFitSummary,
                ["zeroBaseArmTwistTimelineFitSummary"] = zeroBaseArmTwistTimelineFitSummary,
                ["zeroBaseForearmPairTimelineFitSummary"] = zeroBaseForearmPairTimelineFitSummary,
                ["residualCandidateTimelineFitSummary"] = residualCandidateTimelineFitSummary,
                ["oracleSingleMuscleTimelineRebuildSummary"] = BuildOracleSingleMuscleTimelineRebuildSummary(samples, curves, unityResult, solverNodes, solverAxes, humanBoneIndex),
                ["partialOracleLowerArmTimelineReplacementSummary"] = BuildPartialOracleLowerArmTimelineReplacementSummary(samples, curves, unityResult, solver, solverNodes, solverAxes, humanBoneIndex, gltfNodes, nodePathToIndex),
                ["limbTimelineVariantRankingSummary"] = limbTimelineVariantRankingSummary,
                ["variants"] = variantRows,
            };
        }

        private static JObject BuildLimbTimelineVariantRankingSummary(JArray variantRows)
        {
            var rows = variantRows?
                .OfType<JObject>()
                .Select(BuildLimbTimelineVariantRankingRow)
                .Where(x => x != null)
                .OrderBy(x => JsonFloat(x, "focusMaxDegrees"))
                .ThenBy(x => JsonFloat(x, "focusAvgTrackMaxDegrees"))
                .ThenBy(x => (string)x["name"], StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? Array.Empty<JObject>();
            if (rows.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["reason"] = "variant_rows_missing",
                    ["rule"] = "Diagnostic only: ranks full-timeline solver variants by Arm/Leg errors so single-muscle hints are not migrated without real animation validation.",
                };
            }

            var current = rows.FirstOrDefault(x => string.Equals((string)x["name"], "current_swing_twist", StringComparison.OrdinalIgnoreCase));
            var currentFocusMax = JsonFloat(current, "focusMaxDegrees");
            var currentFocusAvg = JsonFloat(current, "focusAvgTrackMaxDegrees");
            foreach (var row in rows)
            {
                var focusMax = JsonFloat(row, "focusMaxDegrees");
                var focusAvg = JsonFloat(row, "focusAvgTrackMaxDegrees");
                row["focusMaxImprovementDegrees"] = current == null ? 0f : Math.Max(0f, currentFocusMax - focusMax);
                row["focusAvgImprovementDegrees"] = current == null ? 0f : Math.Max(0f, currentFocusAvg - focusAvg);
                row["beatsCurrentFocus"] = current != null &&
                    (focusMax < currentFocusMax - 0.5f ||
                     (focusMax <= currentFocusMax + 0.5f && focusAvg < currentFocusAvg - 0.5f));
            }

            var best = rows[0];
            var useful = rows
                .Where(x => (bool?)x["beatsCurrentFocus"] == true)
                .OrderByDescending(x => JsonFloat(x, "focusMaxImprovementDegrees"))
                .ThenByDescending(x => JsonFloat(x, "focusAvgImprovementDegrees"))
                .ThenBy(x => JsonFloat(x, "focusMaxDegrees"))
                .Take(16)
                .ToArray();
            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: full Unity GetInternalAvatarPose timeline ranking for Arm/Leg solver variants. A candidate is not production-ready unless it improves Arm and Leg across multiple models/clips without worsening other groups.",
                ["variantCount"] = rows.Length,
                ["current"] = current == null ? null : CloneCompactLimbVariantRow(current),
                ["best"] = CloneCompactLimbVariantRow(best),
                ["bestBeatsCurrent"] = current != null && (bool?)best["beatsCurrentFocus"] == true,
                ["usefulCandidateCount"] = useful.Length,
                ["topUsefulCandidates"] = new JArray(useful.Select(CloneCompactLimbVariantRow)),
                ["topRanked"] = new JArray(rows.Take(24).Select(CloneCompactLimbVariantRow)),
                ["variants"] = new JArray(rows.Select(CloneCompactLimbVariantRow)),
            };
        }

        private static JObject BuildArmAxisSpaceMigrationGateSummary(
            JObject armTwistTimelineAxisFitSummary,
            JObject upperArmSwingTimelineFitSummary,
            JObject armCombinedTimelineFitSummary)
        {
            if (!IsStatusAvailable(armTwistTimelineAxisFitSummary) &&
                !IsStatusAvailable(upperArmSwingTimelineFitSummary) &&
                !IsStatusAvailable(armCombinedTimelineFitSummary))
            {
                return new JObject
                {
                    ["status"] = "not_available",
                };
            }

            const float minimumUsefulImprovementDegrees = 5f;
            const float maximumReadyMaxErrorDegrees = 25f;
            var armTwistCurrent = armTwistTimelineAxisFitSummary?["rankingSummary"]?["current"] as JObject;
            var armTwistBest = armTwistTimelineAxisFitSummary?["rankingSummary"]?["best"] as JObject;
            var upperArmCurrent = FindTimelineVariantRow(upperArmSwingTimelineFitSummary, "current_x_z_y_y_swing_twist");
            var upperArmBest = (upperArmSwingTimelineFitSummary?["variants"] as JArray)?.OfType<JObject>().FirstOrDefault();
            var combinedCurrent = armCombinedTimelineFitSummary?["current"] as JObject;
            var combinedBest = armCombinedTimelineFitSummary?["best"] as JObject;

            var combinedBeatsCurrent = (bool?)armCombinedTimelineFitSummary?["bestBeatsCurrent"] == true;
            var combinedAvgImprovement = JsonFloat(combinedBest, "avgImprovementDegrees");
            var combinedMaxError = JsonFloat(combinedBest, "maxDegrees");
            var ready = combinedBeatsCurrent &&
                combinedAvgImprovement >= minimumUsefulImprovementDegrees &&
                combinedMaxError <= maximumReadyMaxErrorDegrees;
            var reason = ready
                ? "ready_for_solver_experiment"
                : !combinedBeatsCurrent
                    ? "no_full_timeline_candidate_beats_current"
                    : combinedAvgImprovement < minimumUsefulImprovementDegrees
                        ? "candidate_improvement_too_small"
                        : "candidate_error_too_high";

            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: gates arm-axis formula ideas before they can be considered for the production Humanoid solver. A candidate must beat the current full timeline, improve by a useful margin, and keep worst-case error bounded across all arm tracks.",
                ["migrationStatus"] = ready ? "ready_for_solver_experiment" : "not_ready_axis_space_unresolved",
                ["primaryReason"] = reason,
                ["minimumUsefulImprovementDegrees"] = minimumUsefulImprovementDegrees,
                ["maximumReadyMaxErrorDegrees"] = maximumReadyMaxErrorDegrees,
                ["armTwist"] = BuildArmAxisSpaceGateSection(armTwistCurrent, armTwistBest),
                ["upperArmSwing"] = BuildArmAxisSpaceGateSection(upperArmCurrent, upperArmBest),
                ["combinedArm"] = BuildArmAxisSpaceGateSection(combinedCurrent, combinedBest),
                ["topCombinedUseful"] = new JArray((armCombinedTimelineFitSummary?["topUseful"] as JArray)?.OfType<JObject>().Take(8).Select(CloneArmAxisSpaceGateRow) ?? Enumerable.Empty<JObject>()),
                ["topCombinedRanked"] = new JArray((armCombinedTimelineFitSummary?["topRanked"] as JArray)?.OfType<JObject>().Take(8).Select(CloneArmAxisSpaceGateRow) ?? Enumerable.Empty<JObject>()),
            };
        }

        private static JObject BuildArmAxisSpaceGateSection(JObject current, JObject best)
        {
            return new JObject
            {
                ["current"] = CloneArmAxisSpaceGateRow(current),
                ["best"] = CloneArmAxisSpaceGateRow(best),
                ["bestBeatsCurrent"] = best != null && current != null &&
                    (JsonFloat(best, "avgTrackMaxDegrees") < JsonFloat(current, "avgTrackMaxDegrees") - 0.5f ||
                     (JsonFloat(best, "avgTrackMaxDegrees") <= JsonFloat(current, "avgTrackMaxDegrees") + 0.5f &&
                      JsonFloat(best, "maxDegrees") < JsonFloat(current, "maxDegrees") - 0.5f)),
                ["avgImprovementDegrees"] = current == null || best == null ? 0f : Math.Max(0f, JsonFloat(current, "avgTrackMaxDegrees") - JsonFloat(best, "avgTrackMaxDegrees")),
                ["maxImprovementDegrees"] = current == null || best == null ? 0f : Math.Max(0f, JsonFloat(current, "maxDegrees") - JsonFloat(best, "maxDegrees")),
            };
        }

        private static JObject FindTimelineVariantRow(JObject summary, string name)
        {
            return (summary?["variants"] as JArray)?
                .OfType<JObject>()
                .FirstOrDefault(x => string.Equals((string)x["name"], name, StringComparison.OrdinalIgnoreCase));
        }

        private static JObject CloneArmAxisSpaceGateRow(JObject row)
        {
            if (row == null)
            {
                return null;
            }

            var result = new JObject
            {
                ["name"] = (string)row["name"],
                ["maxDegrees"] = JsonFloat(row, "maxDegrees"),
                ["avgTrackMaxDegrees"] = JsonFloat(row, "avgTrackMaxDegrees"),
                ["avgTrackAvgDegrees"] = JsonFloat(row, "avgTrackAvgDegrees"),
                ["avgImprovementDegrees"] = JsonFloat(row, "avgImprovementDegrees"),
                ["maxImprovementDegrees"] = JsonFloat(row, "maxImprovementDegrees"),
                ["beatsCurrent"] = (bool?)row["beatsCurrent"] ?? false,
            };
            CopyStringIfPresent(row, result, "upperArmVariant");
            CopyStringIfPresent(row, result, "armTwistVariant");
            CopyStringIfPresent(row, result, "description");
            return result;
        }

        private static void CopyStringIfPresent(JObject source, JObject target, string propertyName)
        {
            var value = (string)source?[propertyName];
            if (!string.IsNullOrWhiteSpace(value))
            {
                target[propertyName] = value;
            }
        }

        private static JObject BuildArmFormulaTimelineGateSummary(
            JObject armTwistTimelineAxisFitSummary,
            JObject upperArmFrontBackPoseAxisTimelineFitSummary)
        {
            var armTwistRows = armTwistTimelineAxisFitSummary?["rankingSummary"]?["topRanked"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var upperArmRows = (upperArmFrontBackPoseAxisTimelineFitSummary?["variants"] as JArray)?.OfType<JObject>().ToArray()
                ?? upperArmFrontBackPoseAxisTimelineFitSummary?["topRanked"]?.OfType<JObject>().ToArray()
                ?? Array.Empty<JObject>();
            var queueRows = new JArray();

            AddTimelineGateRow(
                queueRows,
                "ArmTwist",
                "Right/Left Arm Twist -> LowerArm",
                "derive_arm_twist_axis_space_from_avatar_pose_or_limit_axis",
                armTwistRows,
                "upper_y_to_x_mirror_xy_right",
                "single-muscle work queue points to source/target axis-space changes; this is the strongest matching full-timeline candidate in Gorou.");
            AddTimelineGateRow(
                queueRows,
                "ArmTwist",
                "Right/Left Arm Twist -> LowerArm",
                "derive_arm_twist_axis_space_from_avatar_pose_or_limit_axis",
                armTwistRows,
                "upper_y_to_x_twist_first_mirror_xy_right",
                "same axis-space idea with twist-first composition.");
            AddTimelineGateRow(
                queueRows,
                "UpperArmFrontBack",
                "Left/Right Arm Front-Back -> UpperArm",
                "derive_upper_arm_swing_axis_tilt_from_avatar_pose",
                upperArmRows,
                "oracle_pattern_frontback__right_delta",
                "oracle-only off-axis pattern previously improved NPC/Gorou; derive this tilt from Avatar/rest metadata before migration.");

            var usefulCount = queueRows.OfType<JObject>().Count(x => (bool?)x["beatsCurrent"] == true);
            var maxImprovement = queueRows.OfType<JObject>()
                .Select(x => JsonFloat(x, "avgImprovementDegrees"))
                .DefaultIfEmpty(0f)
                .Max();
            return new JObject
            {
                ["status"] = queueRows.Count == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: connects the single-muscle arm formula work queue to full-timeline gate rows. A row can guide formula research only; production migration still requires stable improvement across multiple models and no visual regressions.",
                ["queueRowCount"] = queueRows.Count,
                ["beatsCurrentCount"] = usefulCount,
                ["maxAvgImprovementDegrees"] = maxImprovement,
                ["migrationStatus"] = usefulCount == 0
                    ? "not_ready_no_timeline_candidate"
                    : maxImprovement < 2f
                        ? "not_ready_improvement_too_small"
                        : "needs_cross_model_gate",
                ["rows"] = queueRows,
            };
        }

        private static JObject BuildLegFormulaTimelineGateSummary(
            JObject distalStretchTimelineFitSummary,
            JObject limbTimelineVariantRankingSummary)
        {
            var rows = new JArray();

            AddLegBodyGroupGateRow(
                rows,
                "LowerLegDistalStretch",
                "LowerLeg stretch / upper-leg twist pair",
                "derive_lower_leg_stretch_axis_or_zero_base_from_avatar_pose",
                (distalStretchTimelineFitSummary?["variants"] as JArray)?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>(),
                "current_stretch_z_pos_vector_swing_twist",
                "Diagnostic only: tests lower-leg distal stretch/twist variants against Unity timeline Leg bodyGroup. It does not cover the whole foot formula by itself.");

            AddLegBodyGroupGateRow(
                rows,
                "LegFullBodyGroup",
                "UpperLeg + LowerLeg + Foot combined leg timeline",
                "derive_leg_zero_base_and_foot_twist_formula_from_avatar_pose",
                (limbTimelineVariantRankingSummary?["variants"] as JArray)?.OfType<JObject>().ToArray()
                    ?? (limbTimelineVariantRankingSummary?["topRanked"] as JArray)?.OfType<JObject>().ToArray()
                    ?? Array.Empty<JObject>(),
                "current_swing_twist",
                "Diagnostic only: ranks full solver variants by Leg bodyGroup only, so arm failures do not hide whether the leg formula improved.");

            var foundRows = rows.OfType<JObject>().Where(x => (bool?)x["candidateFound"] == true).ToArray();
            var beatsCurrentCount = foundRows.Count(x => (bool?)x["beatsCurrent"] == true);
            var maxAvgImprovement = foundRows.Select(x => JsonFloat(x, "avgImprovementDegrees")).DefaultIfEmpty(0f).Max();
            var maxError = foundRows.Select(x => JsonFloat(x, "maxDegrees")).DefaultIfEmpty(0f).Max();
            return new JObject
            {
                ["status"] = rows.Count == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: gates leg/foot Humanoid formula candidates against Unity GetInternalAvatarPose timeline using Leg bodyGroup metrics. It is evidence for reverse engineering only and must not be treated as a production solver rule.",
                ["queueRowCount"] = rows.Count,
                ["candidateFoundCount"] = foundRows.Length,
                ["beatsCurrentCount"] = beatsCurrentCount,
                ["maxAvgImprovementDegrees"] = maxAvgImprovement,
                ["maxCandidateMaxDegrees"] = maxError,
                ["migrationStatus"] = beatsCurrentCount == 0
                    ? "not_ready_no_leg_timeline_candidate"
                    : maxAvgImprovement < 2f
                        ? "not_ready_improvement_too_small"
                        : "needs_cross_model_gate",
                ["rows"] = rows,
            };
        }

        private static JObject BuildZeroBaseCandidateGateSummary(JObject residualCandidateTimelineFitSummary)
        {
            var sourceRows = residualCandidateTimelineFitSummary?["focusedLimbPerBoneCandidateSummary"]?["rows"]?.OfType<JObject>().ToArray()
                ?? Array.Empty<JObject>();
            var rows = new JArray(sourceRows
                .Select(BuildZeroBaseCandidateGateRow)
                .Where(x => x != null)
                .OrderByDescending(x => JsonFloat(x, "improvementMaxDegrees"))
                .ThenByDescending(x => JsonFloat(x, "currentMaxDegrees"))
                .ThenBy(x => (string)x["humanBone"], StringComparer.OrdinalIgnoreCase));
            var deterministicRows = rows
                .OfType<JObject>()
                .Where(x => (bool?)x["deterministicMetadataCandidate"] == true)
                .ToArray();
            var oracleRows = rows
                .OfType<JObject>()
                .Where(x => string.Equals((string)x["candidateFamily"], "unityZeroBaseOracle", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var readyRows = deterministicRows
                .Where(x => (bool?)x["beatsCurrent"] == true && JsonFloat(x, "bestMaxDegrees") <= 10f)
                .ToArray();
            var oracleOnlyHighImpactRows = oracleRows
                .Where(x => JsonFloat(x, "improvementMaxDegrees") >= 10f)
                .ToArray();
            return new JObject
            {
                ["status"] = rows.Count == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: summarizes per-bone zero/base pose candidates from residualCandidateTimelineFitSummary. Deterministic metadata candidates may guide production solver work; unityZeroBaseOracle only proves the upper bound and must be traced back to Avatar/rest metadata first.",
                ["rowCount"] = rows.Count,
                ["deterministicCandidateCount"] = deterministicRows.Length,
                ["readyDeterministicCandidateCount"] = readyRows.Length,
                ["oracleOnlyHighImpactCount"] = oracleOnlyHighImpactRows.Length,
                ["maxOracleOnlyImprovementDegrees"] = oracleOnlyHighImpactRows.Select(x => JsonFloat(x, "improvementMaxDegrees")).DefaultIfEmpty(0f).Max(),
                ["migrationStatus"] = readyRows.Length > 0
                    ? "deterministic_candidates_need_cross_model_gate"
                    : oracleOnlyHighImpactRows.Length > 0
                        ? "oracle_upper_bound_needs_formula_source"
                        : "not_ready_no_zero_base_candidate",
                ["rows"] = rows,
            };
        }

        private static JObject BuildZeroBaseCandidateGateRow(JObject row)
        {
            if (row == null)
            {
                return null;
            }

            var candidateFamily = (string)row["bestCandidateFamily"] ?? "none";
            var improvementMax = JsonFloat(row, "improvementMaxDegrees");
            var improvementAvg = JsonFloat(row, "improvementAvgDegrees");
            var bestMax = JsonFloat(row, "bestMaxDegrees");
            var bestAvg = JsonFloat(row, "bestAvgDegrees");
            var deterministic = IsDeterministicZeroBaseCandidateFamily(candidateFamily);
            return new JObject
            {
                ["humanBone"] = row["humanBone"],
                ["bodyGroup"] = row["bodyGroup"],
                ["side"] = row["side"],
                ["targetPath"] = row["targetPath"],
                ["candidateName"] = row["bestCandidate"],
                ["candidateFamily"] = candidateFamily,
                ["deterministicMetadataCandidate"] = deterministic,
                ["oracleOnly"] = string.Equals(candidateFamily, "unityZeroBaseOracle", StringComparison.OrdinalIgnoreCase),
                ["bestApplySide"] = row["bestApplySide"],
                ["currentMaxDegrees"] = row["currentMaxDegrees"],
                ["currentAvgDegrees"] = row["currentAvgDegrees"],
                ["bestMaxDegrees"] = bestMax,
                ["bestAvgDegrees"] = bestAvg,
                ["improvementMaxDegrees"] = improvementMax,
                ["improvementAvgDegrees"] = improvementAvg,
                ["beatsCurrent"] = improvementMax >= 0.5f || improvementAvg >= 0.5f,
                ["stillLargeAfterBest"] = row["stillLargeAfterBest"],
                ["nextFormulaWork"] = deterministic
                    ? "verify_deterministic_zero_base_candidate_across_models"
                    : string.Equals(candidateFamily, "unityZeroBaseOracle", StringComparison.OrdinalIgnoreCase)
                        ? "derive_unity_zero_base_from_avatar_rest_metadata"
                        : "find_zero_base_candidate_source",
            };
        }

        private static bool IsDeterministicZeroBaseCandidateFamily(string candidateFamily)
        {
            return string.Equals(candidateFamily, "rest", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidateFamily, "localRest", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidateFamily, "avatarDefaultPose", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidateFamily, "humanSkeletonPose", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidateFamily, "avatarSkeletonPose", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidateFamily, "parentPose", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddLegBodyGroupGateRow(
            JArray rows,
            string family,
            string workItem,
            string nextFormulaWork,
            JObject[] candidateRows,
            string currentName,
            string note)
        {
            if (rows == null)
            {
                return;
            }

            var available = (candidateRows ?? Array.Empty<JObject>())
                .Where(x => x != null && string.Equals((string)x["status"] ?? "ok", "ok", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var current = available.FirstOrDefault(x => string.Equals((string)x["name"], currentName, StringComparison.OrdinalIgnoreCase));
            var currentAvg = ReadLegAvgTrackMaxDegrees(current);
            var currentMax = ReadLegMaxDegrees(current);
            var best = available
                .Where(x => ReadLegAvgTrackMaxDegrees(x) > 0f || ReadLegMaxDegrees(x) > 0f)
                .OrderBy(x => ReadLegAvgTrackMaxDegrees(x))
                .ThenBy(x => ReadLegMaxDegrees(x))
                .ThenBy(x => (string)x["name"], StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            var bestAvg = ReadLegAvgTrackMaxDegrees(best);
            var bestMax = ReadLegMaxDegrees(best);
            var beatsCurrent = current != null && best != null &&
                (bestAvg < currentAvg - 0.5f ||
                 (bestAvg <= currentAvg + 0.5f && bestMax < currentMax - 0.5f));
            if (!beatsCurrent && current != null)
            {
                best = current;
                bestAvg = currentAvg;
                bestMax = currentMax;
            }

            rows.Add(new JObject
            {
                ["family"] = family,
                ["workItem"] = workItem,
                ["nextFormulaWork"] = nextFormulaWork,
                ["candidateName"] = (string)best?["name"],
                ["candidateFound"] = best != null,
                ["currentCandidateName"] = currentName,
                ["currentAvgTrackMaxDegrees"] = current == null ? null : JToken.FromObject(currentAvg),
                ["currentMaxDegrees"] = current == null ? null : JToken.FromObject(currentMax),
                ["avgTrackMaxDegrees"] = best == null ? null : JToken.FromObject(bestAvg),
                ["maxDegrees"] = best == null ? null : JToken.FromObject(bestMax),
                ["avgImprovementDegrees"] = current == null || best == null ? null : JToken.FromObject(Math.Max(0f, currentAvg - bestAvg)),
                ["maxImprovementDegrees"] = current == null || best == null ? null : JToken.FromObject(Math.Max(0f, currentMax - bestMax)),
                ["beatsCurrent"] = beatsCurrent,
                ["note"] = note,
            });
        }

        private static float ReadLegAvgTrackMaxDegrees(JObject row)
        {
            if (row == null)
            {
                return 0f;
            }
            var value = JsonFloat(row, "legAvgTrackMaxDegrees");
            return value > 0f ? value : BodyGroupJsonFloat(row, "Leg", "avgTrackMaxDegrees");
        }

        private static float ReadLegMaxDegrees(JObject row)
        {
            if (row == null)
            {
                return 0f;
            }
            var value = JsonFloat(row, "legMaxDegrees");
            return value > 0f ? value : BodyGroupJsonFloat(row, "Leg", "maxDegrees");
        }

        private static void AddTimelineGateRow(
            JArray rows,
            string family,
            string workItem,
            string nextFormulaWork,
            JObject[] rankedRows,
            string candidateName,
            string note)
        {
            if (rows == null || string.IsNullOrWhiteSpace(candidateName))
            {
                return;
            }

            var candidate = rankedRows?.FirstOrDefault(x => string.Equals((string)x["name"], candidateName, StringComparison.OrdinalIgnoreCase));
            rows.Add(new JObject
            {
                ["family"] = family,
                ["workItem"] = workItem,
                ["nextFormulaWork"] = nextFormulaWork,
                ["candidateName"] = candidateName,
                ["candidateFound"] = candidate != null,
                ["avgTrackMaxDegrees"] = candidate == null ? null : JToken.FromObject(JsonFloat(candidate, "avgTrackMaxDegrees")),
                ["maxDegrees"] = candidate == null ? null : JToken.FromObject(JsonFloat(candidate, "maxDegrees")),
                ["avgImprovementDegrees"] = candidate == null ? null : JToken.FromObject(JsonFloat(candidate, "avgImprovementDegrees")),
                ["maxImprovementDegrees"] = candidate == null ? null : JToken.FromObject(JsonFloat(candidate, "maxImprovementDegrees")),
                ["beatsCurrent"] = (bool?)candidate?["beatsCurrent"] ?? false,
                ["note"] = note,
            });
        }

        private static JObject BuildLimbTimelineVariantRankingRow(JObject variant)
        {
            if (variant == null || !string.Equals((string)variant["status"], "ok", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var body = variant["bodyGroupError"] as JObject;
            var arm = body?["Arm"] as JObject;
            var leg = body?["Leg"] as JObject;
            if (arm == null && leg == null)
            {
                return null;
            }

            var armMax = JsonFloat(arm, "maxDegrees");
            var legMax = JsonFloat(leg, "maxDegrees");
            var armAvg = JsonFloat(arm, "avgTrackMaxDegrees");
            var legAvg = JsonFloat(leg, "avgTrackMaxDegrees");
            var focusMax = Math.Max(arm == null ? 0f : armMax, leg == null ? 0f : legMax);
            var focusAvgValues = new List<float>();
            if (arm != null)
            {
                focusAvgValues.Add(armAvg);
            }
            if (leg != null)
            {
                focusAvgValues.Add(legAvg);
            }

            return new JObject
            {
                ["name"] = variant["name"],
                ["description"] = variant["description"],
                ["maxDegrees"] = JsonFloat(variant, "maxDegrees"),
                ["avgTrackMaxDegrees"] = JsonFloat(variant, "avgTrackMaxDegrees"),
                ["focusBodyGroups"] = "Arm,Leg",
                ["focusMaxDegrees"] = focusMax,
                ["focusAvgTrackMaxDegrees"] = focusAvgValues.Count == 0 ? 0f : focusAvgValues.Average(),
                ["armMaxDegrees"] = arm == null ? 0f : armMax,
                ["armAvgTrackMaxDegrees"] = arm == null ? 0f : armAvg,
                ["armWorstPath"] = arm?["worstPath"],
                ["legMaxDegrees"] = leg == null ? 0f : legMax,
                ["legAvgTrackMaxDegrees"] = leg == null ? 0f : legAvg,
                ["legWorstPath"] = leg?["worstPath"],
            };
        }

        private static JObject CloneCompactLimbVariantRow(JObject row)
        {
            if (row == null)
            {
                return null;
            }

            return new JObject
            {
                ["name"] = row["name"],
                ["description"] = row["description"],
                ["focusMaxDegrees"] = row["focusMaxDegrees"],
                ["focusAvgTrackMaxDegrees"] = row["focusAvgTrackMaxDegrees"],
                ["focusMaxImprovementDegrees"] = row["focusMaxImprovementDegrees"],
                ["focusAvgImprovementDegrees"] = row["focusAvgImprovementDegrees"],
                ["beatsCurrentFocus"] = row["beatsCurrentFocus"],
                ["armMaxDegrees"] = row["armMaxDegrees"],
                ["armAvgTrackMaxDegrees"] = row["armAvgTrackMaxDegrees"],
                ["legMaxDegrees"] = row["legMaxDegrees"],
                ["legAvgTrackMaxDegrees"] = row["legAvgTrackMaxDegrees"],
                ["armWorstPath"] = row["armWorstPath"],
                ["legWorstPath"] = row["legWorstPath"],
            };
        }

        private static JObject BuildOracleSingleMuscleTimelineRebuildSummary(
            JObject[] samples,
            Dictionary<string, List<FloatKey>> curves,
            JObject unityResult,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex)
        {
            var singleProbes = unityResult?["internalAvatarPoseMuscleProbes"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            if (singleProbes.Length == 0)
            {
                singleProbes = unityResult?["muscleProbes"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            }

            if (samples == null || samples.Length == 0 || curves == null || curves.Count == 0 || singleProbes.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["timelineSampleCount"] = samples?.Length ?? 0,
                    ["decodedFloatCurveCount"] = curves?.Count ?? 0,
                    ["singleProbeCount"] = singleProbes.Length,
                    ["rule"] = "Diagnostic only: rebuilds full timeline from Unity single-muscle probe deltas. It is unavailable when timeline, decoded curves, or probes are missing.",
                };
            }

            var probeByMuscle = singleProbes
                .Where(x => !string.IsNullOrWhiteSpace((string)x["muscleName"]))
                .GroupBy(x => (string)x["muscleName"], StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
            var modes = new[] { "leftDelta_append", "leftDelta_prepend", "rightDelta_append", "rightDelta_prepend" };
            var accumulators = modes.ToDictionary(
                x => x,
                _ => new Dictionary<string, TimelineTrackAccumulator>(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
            var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var clamped = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var matchedSampleCount = 0;

            foreach (var target in BuildCurrentSolverTargets())
            {
                if (!TryGetSolverTarget(solverNodes, solverAxes, humanBoneIndex, target, out var targetPath, out _))
                {
                    missing.Add($"target:{target.HumanBone}");
                    continue;
                }

                var attributes = new[] { target.XAttribute, target.YAttribute, target.ZAttribute, target.ExtraZAttribute }
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (attributes.Length == 0)
                {
                    continue;
                }

                foreach (var sample in samples)
                {
                    var time = (float?)sample["time"] ?? 0f;
                    var jointPaths = sample["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
                    var values = sample["values"]?.Values<float>().ToArray() ?? Array.Empty<float>();
                    if (!TryFindJointIndex(jointPaths, targetPath, out var jointIndex))
                    {
                        continue;
                    }

                    var offset = jointIndex * 7;
                    if (offset + 6 >= values.Length)
                    {
                        continue;
                    }

                    var unityRotation = Normalize(new[]
                    {
                        values[offset + 3],
                        values[offset + 4],
                        values[offset + 5],
                        values[offset + 6],
                    });

                    var leftDeltas = new List<float[]>();
                    var rightDeltas = new List<float[]>();
                    float[] baseRotation = null;
                    var ok = true;
                    foreach (var attribute in attributes)
                    {
                        var value = SampleFloatCurve(curves.TryGetValue(attribute, out var curve) ? curve : null, time);
                        if (!TrySampleOracleSingleMuscleDelta(probeByMuscle, attribute, targetPath, value, out var baseQ, out var leftDelta, out var rightDelta, out var wasClamped))
                        {
                            missing.Add($"{targetPath}|{attribute}");
                            ok = false;
                            break;
                        }
                        if (wasClamped)
                        {
                            var key = $"{targetPath}|{attribute}";
                            clamped[key] = clamped.TryGetValue(key, out var count) ? count + 1 : 1;
                        }

                        baseRotation ??= baseQ;
                        leftDeltas.Add(leftDelta);
                        rightDeltas.Add(rightDelta);
                    }

                    if (!ok || baseRotation == null || leftDeltas.Count == 0)
                    {
                        continue;
                    }

                    matchedSampleCount++;
                    foreach (var mode in modes)
                    {
                        var useRight = mode.StartsWith("rightDelta", StringComparison.OrdinalIgnoreCase);
                        var append = mode.EndsWith("_append", StringComparison.OrdinalIgnoreCase);
                        var deltas = useRight ? rightDeltas : leftDeltas;
                        var composed = ComposeDeltas(deltas, Enumerable.Range(0, deltas.Count).ToArray(), append);
                        var predicted = useRight
                            ? Normalize(Multiply(baseRotation, composed))
                            : Normalize(Multiply(composed, baseRotation));
                        var error = QuaternionAngleDegrees(unityRotation, predicted);
                        if (!accumulators[mode].TryGetValue(targetPath, out var accumulator))
                        {
                            accumulator = new TimelineTrackAccumulator(targetPath, targetPath, -1);
                            accumulators[mode][targetPath] = accumulator;
                        }
                        accumulator.Add(error);
                    }
                }
            }

            var modeRows = modes
                .Select(mode =>
                {
                    var rows = accumulators[mode].Values
                        .Select(x => x.ToRow())
                        .OrderByDescending(x => x.MaxRotationErrorDegrees)
                        .ThenBy(x => x.UnityPath, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    return new JObject
                    {
                        ["mode"] = mode,
                        ["matchedTrackCount"] = rows.Length,
                        ["maxDegrees"] = rows.Length == 0 ? 0 : rows[0].MaxRotationErrorDegrees,
                        ["avgTrackMaxDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.MaxRotationErrorDegrees),
                        ["avgTrackAvgDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.AvgRotationErrorDegrees),
                        ["bodyGroupError"] = BuildBodyGroupErrorSummary(rows),
                        ["topErrors"] = ToJsonRows(rows.Take(24)),
                    };
                })
                .OrderBy(x => JsonFloat(x, "maxDegrees"))
                .ThenBy(x => JsonFloat(x, "avgTrackMaxDegrees"))
                .ThenBy(x => (string)x["mode"], StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var best = modeRows.FirstOrDefault();

            return new JObject
            {
                ["status"] = modeRows.Length == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: uses Unity single-muscle probes as an oracle lookup table, interpolates each decoded muscle value per frame, composes those deltas, and compares against Unity InternalAvatarPose timeline. If this is much better than current_swing_twist, the remaining production work is deriving the same single-muscle delta formula from AvatarConstant instead of Unity probes.",
                ["timelineSampleCount"] = samples.Length,
                ["matchedSampleCount"] = matchedSampleCount,
                ["singleProbeCount"] = singleProbes.Length,
                ["missingCount"] = missing.Count,
                ["missing"] = new JArray(missing.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(128)),
                ["clampedSampleCount"] = clamped.Values.DefaultIfEmpty(0).Sum(),
                ["clampedMuscles"] = new JArray(clamped
                    .OrderByDescending(x => x.Value)
                    .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(64)
                    .Select(x => new JObject
                    {
                        ["targetAndAttribute"] = x.Key,
                        ["count"] = x.Value,
                    })),
                ["bestMode"] = (string)best?["mode"],
                ["bestMaxDegrees"] = best == null ? 0 : JsonFloat(best, "maxDegrees"),
                ["bestAvgTrackMaxDegrees"] = best == null ? 0 : JsonFloat(best, "avgTrackMaxDegrees"),
                ["modes"] = new JArray(modeRows),
            };
        }

        private static JObject BuildPartialOracleLowerArmTimelineReplacementSummary(
            JObject[] samples,
            Dictionary<string, List<FloatKey>> curves,
            JObject unityResult,
            JObject solver,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex,
            JObject[] gltfNodes,
            Dictionary<string, int> nodePathToIndex)
        {
            var singleProbes = unityResult?["internalAvatarPoseMuscleProbes"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            if (singleProbes.Length == 0)
            {
                singleProbes = unityResult?["muscleProbes"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            }

            if (samples == null || samples.Length == 0 || curves == null || curves.Count == 0 || singleProbes.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["timelineSampleCount"] = samples?.Length ?? 0,
                    ["decodedFloatCurveCount"] = curves?.Count ?? 0,
                    ["singleProbeCount"] = singleProbes.Length,
                    ["rule"] = "Diagnostic only: partially replaces lower-arm Forearm Stretch and/or Arm Twist with Unity single-muscle oracle deltas. It is unavailable when timeline, decoded curves, or probes are missing.",
                };
            }

            var probeByMuscle = singleProbes
                .Where(x => !string.IsNullOrWhiteSpace((string)x["muscleName"]))
                .GroupBy(x => (string)x["muscleName"], StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
            var replacementModes = BuildPartialOracleLowerArmModes();
            var accumulators = replacementModes.ToDictionary(
                x => x.Name,
                _ => new Dictionary<string, TimelineTrackAccumulator>(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
            var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var clamped = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var matchedSampleCount = 0;

            foreach (var target in BuildCurrentSolverTargets().Where(x => IsLowerArmBone(x.HumanBone)))
            {
                if (!TryGetSolverTarget(solverNodes, solverAxes, humanBoneIndex, target, out var targetPath, out var targetAxis))
                {
                    missing.Add($"target:{target.HumanBone}");
                    continue;
                }

                foreach (var sample in samples)
                {
                    var time = (float?)sample["time"] ?? 0f;
                    var jointPaths = sample["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
                    var values = sample["values"]?.Values<float>().ToArray() ?? Array.Empty<float>();
                    if (!TryFindJointIndex(jointPaths, targetPath, out var jointIndex))
                    {
                        continue;
                    }

                    var offset = jointIndex * 7;
                    if (offset + 6 >= values.Length)
                    {
                        continue;
                    }

                    var unityRotation = Normalize(new[]
                    {
                        values[offset + 3],
                        values[offset + 4],
                        values[offset + 5],
                        values[offset + 6],
                    });

                    matchedSampleCount++;
                    foreach (var mode in replacementModes)
                    {
                        if (!TryBuildPartialOracleLowerArmRotation(
                            probeByMuscle,
                            curves,
                            target,
                            targetAxis,
                            time,
                            mode,
                            solverNodes,
                            solverAxes,
                            humanBoneIndex,
                            targetPath,
                            missing,
                            clamped,
                            out var predicted))
                        {
                            continue;
                        }

                        var error = QuaternionAngleDegrees(unityRotation, predicted);
                        if (!accumulators[mode.Name].TryGetValue(targetPath, out var accumulator))
                        {
                            accumulator = new TimelineTrackAccumulator(targetPath, targetPath, -1);
                            accumulators[mode.Name][targetPath] = accumulator;
                        }
                        accumulator.Add(error);
                    }
                }
            }

            var modeRows = replacementModes
                .Select(mode =>
                {
                    var rows = accumulators[mode.Name].Values
                        .Select(x => x.ToRow())
                        .OrderByDescending(x => x.MaxRotationErrorDegrees)
                        .ThenBy(x => x.UnityPath, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    return new JObject
                    {
                        ["mode"] = mode.Name,
                        ["replacement"] = mode.Replacement,
                        ["deltaMode"] = mode.DeltaMode,
                        ["matchedTrackCount"] = rows.Length,
                        ["maxDegrees"] = rows.Length == 0 ? 0 : rows[0].MaxRotationErrorDegrees,
                        ["avgTrackMaxDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.MaxRotationErrorDegrees),
                        ["avgTrackAvgDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.AvgRotationErrorDegrees),
                        ["topErrors"] = ToJsonRows(rows.Take(8)),
                    };
                })
                .OrderBy(x => JsonFloat(x, "maxDegrees"))
                .ThenBy(x => JsonFloat(x, "avgTrackMaxDegrees"))
                .ThenBy(x => (string)x["mode"], StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var current = modeRows.FirstOrDefault(x => string.Equals((string)x["mode"], "current_lower_arm", StringComparison.OrdinalIgnoreCase));
            var currentMax = JsonFloat(current, "maxDegrees");
            var currentAvg = JsonFloat(current, "avgTrackMaxDegrees");
            foreach (var row in modeRows)
            {
                row["maxImprovementDegrees"] = current == null ? 0f : currentMax - JsonFloat(row, "maxDegrees");
                row["avgImprovementDegrees"] = current == null ? 0f : currentAvg - JsonFloat(row, "avgTrackMaxDegrees");
                row["beatsCurrent"] = current != null &&
                    (JsonFloat(row, "maxDegrees") < currentMax - 0.5f ||
                     (JsonFloat(row, "maxDegrees") <= currentMax + 0.5f &&
                      JsonFloat(row, "avgTrackMaxDegrees") < currentAvg - 0.5f));
            }

            return new JObject
            {
                ["status"] = modeRows.Length == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: isolates lower-arm full-timeline error by replacing Forearm Stretch, Arm Twist, or both with Unity single-muscle oracle deltas while keeping the rest of the current lower-arm formula. This tells which production formula segment should be reverse-engineered next.",
                ["timelineSampleCount"] = samples.Length,
                ["matchedSampleCount"] = matchedSampleCount,
                ["singleProbeCount"] = singleProbes.Length,
                ["missingCount"] = missing.Count,
                ["missing"] = new JArray(missing.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(128)),
                ["clampedSampleCount"] = clamped.Values.DefaultIfEmpty(0).Sum(),
                ["clampedMuscles"] = new JArray(clamped
                    .OrderByDescending(x => x.Value)
                    .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(64)
                    .Select(x => new JObject
                    {
                        ["targetAndAttribute"] = x.Key,
                        ["count"] = x.Value,
                    })),
                ["current"] = current,
                ["best"] = modeRows.FirstOrDefault(),
                ["topUseful"] = new JArray(modeRows.Where(x => (bool?)x["beatsCurrent"] == true).Take(16)),
                ["baseAlignmentSummary"] = BuildPartialOracleLowerArmBaseAlignmentSummary(singleProbes, solver, solverNodes, solverAxes, humanBoneIndex, gltfNodes, nodePathToIndex),
                ["modes"] = new JArray(modeRows),
            };
        }

        private static JObject BuildPartialOracleLowerArmBaseAlignmentSummary(
            JObject[] singleProbes,
            JObject solver,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex,
            JObject[] gltfNodes,
            Dictionary<string, int> nodePathToIndex)
        {
            if (singleProbes == null || singleProbes.Length == 0 || solverNodes == null || solverAxes == null || humanBoneIndex == null)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["singleProbeCount"] = singleProbes?.Length ?? 0,
                    ["rule"] = "Diagnostic only: compares lower-arm Unity single-muscle baseQ against deterministic Avatar/rest/currentZero base candidates.",
                };
            }

            var gltfParents = BuildChildToParent(gltfNodes ?? Array.Empty<JObject>());
            var rows = new List<JObject>();
            foreach (var target in BuildCurrentSolverTargets().Where(x => IsLowerArmBone(x.HumanBone)))
            {
                if (!TryGetSolverTarget(solverNodes, solverAxes, humanBoneIndex, target, out var targetPath, out var targetAxis))
                {
                    continue;
                }

                var baseCandidates = BuildLowerArmBaseAlignmentCandidates(targetPath, targetAxis, solver, solverNodes, humanBoneIndex, target.HumanBone, gltfNodes, gltfParents, nodePathToIndex);
                var reducedBaseCandidates = BuildReducedLowerArmBaseAlignmentCandidates(baseCandidates);
                var templateBaseCandidates = BuildLowerArmBaseTemplateCandidates(baseCandidates);
                var indexSelectionBaseCandidates = BuildLowerArmIndexSelectionBaseCandidates(baseCandidates);
                var avatarLayout = BuildLowerArmAvatarLayoutSummary(targetPath, target.HumanBone, solver, solverNodes, humanBoneIndex, baseCandidates);
                foreach (var muscleName in new[] { target.XAttribute, target.ZAttribute }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    foreach (var probe in singleProbes.Where(x => string.Equals((string)x["muscleName"], muscleName, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (!TryReadProbeRotation(probe, targetPath, out var probePath, out var baseQ, out _))
                        {
                            continue;
                        }

                        var best = baseCandidates
                            .Select(x => new
                            {
                                Name = x.Key,
                                Error = QuaternionAngleDegrees(baseQ, x.Value),
                            })
                            .OrderBy(x => x.Error)
                            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                            .FirstOrDefault();
                        if (best == null)
                        {
                            continue;
                        }
                        var bestReduced = reducedBaseCandidates
                            .Select(x => new
                            {
                                Name = x.Key,
                                Error = QuaternionAngleDegrees(baseQ, x.Value),
                            })
                            .OrderBy(x => x.Error)
                            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                            .FirstOrDefault();
                        var bestTemplate = templateBaseCandidates
                            .Select(x => new
                            {
                                Name = x.Key,
                                Error = QuaternionAngleDegrees(baseQ, x.Value),
                            })
                            .OrderBy(x => x.Error)
                            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                            .FirstOrDefault();
                        var bestIndexSelection = indexSelectionBaseCandidates
                            .Select(x => new
                            {
                                Name = x.Key,
                                Error = QuaternionAngleDegrees(baseQ, x.Value),
                                Expression = BuildLowerArmBaseCandidateExpressionSummary(x.Key),
                            })
                            .OrderBy(x => x.Error)
                            .ThenBy(x => (int?)x.Expression["effectiveFactorCount"] ?? 99)
                            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                            .FirstOrDefault();
                        var templateRows = templateBaseCandidates
                            .Select(x => new JObject
                            {
                                ["candidate"] = x.Key,
                                ["roleSignature"] = ClassifyLowerArmBaseTemplateRoleSignature(x.Key),
                                ["errorDegrees"] = QuaternionAngleDegrees(baseQ, x.Value),
                            })
                            .OrderBy(x => JsonFloat(x, "errorDegrees"))
                            .ThenBy(x => (string)x["candidate"], StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                        var indexSelectionRows = indexSelectionBaseCandidates
                            .Select(x =>
                            {
                                var expression = BuildLowerArmBaseCandidateExpressionSummary(x.Key);
                                return new JObject
                                {
                                    ["candidate"] = x.Key,
                                    ["effectiveRoleSignature"] = (string)expression["effectiveRoleSignature"] ?? "",
                                    ["effectiveFactorSignature"] = (string)expression["effectiveFactorSignature"] ?? "",
                                    ["factorCount"] = (int?)expression["effectiveFactorCount"] ?? 0,
                                    ["errorDegrees"] = QuaternionAngleDegrees(baseQ, x.Value),
                                };
                            })
                            .OrderBy(x => JsonFloat(x, "errorDegrees"))
                            .ThenBy(x => (int?)x["factorCount"] ?? 99)
                            .ThenBy(x => (string)x["candidate"], StringComparer.OrdinalIgnoreCase)
                            .Take(32)
                            .ToArray();

                        rows.Add(new JObject
                        {
                            ["targetHumanBone"] = target.HumanBone,
                            ["targetPath"] = targetPath,
                            ["probePath"] = probePath,
                            ["muscleName"] = muscleName,
                            ["muscleFamily"] = ClassifyMuscleFamily(muscleName),
                            ["bodyGroup"] = ClassifyBodyGroup(targetPath),
                            ["avatarLayout"] = avatarLayout,
                            ["value"] = (float?)probe["value"] ?? 0f,
                            ["baseValue"] = (float?)probe["baseValue"] ?? 0f,
                            ["bestBaseCandidate"] = best.Name,
                            ["bestBaseCandidateFamily"] = ClassifyLowerArmBaseCandidateFamily(best.Name),
                            ["bestBaseCandidateFactorCount"] = CountLowerArmBaseCandidateFactors(best.Name),
                            ["bestBaseCandidatePortability"] = ClassifyLowerArmBaseCandidatePortability(best.Name, best.Error),
                            ["bestBaseCandidateExpression"] = BuildLowerArmBaseCandidateExpressionSummary(best.Name),
                            ["bestBaseErrorDegrees"] = best.Error,
                            ["bestReducedBaseCandidate"] = bestReduced?.Name ?? "",
                            ["bestReducedBaseCandidateFamily"] = ClassifyLowerArmBaseCandidateFamily(bestReduced?.Name),
                            ["bestReducedBaseCandidateFactorCount"] = CountLowerArmBaseCandidateFactors(bestReduced?.Name),
                            ["bestReducedBaseCandidatePortability"] = ClassifyLowerArmBaseCandidatePortability(bestReduced?.Name, bestReduced?.Error ?? 999f),
                            ["bestReducedBaseCandidateExpression"] = BuildLowerArmBaseCandidateExpressionSummary(bestReduced?.Name),
                            ["bestReducedBaseErrorDegrees"] = bestReduced?.Error ?? 0f,
                            ["reducedCandidateCount"] = reducedBaseCandidates.Count,
                            ["bestTemplateBaseCandidate"] = bestTemplate?.Name ?? "",
                            ["bestTemplateBaseCandidateFamily"] = ClassifyLowerArmBaseCandidateFamily(bestTemplate?.Name),
                            ["bestTemplateBaseCandidatePortability"] = ClassifyLowerArmBaseCandidatePortability(bestTemplate?.Name, bestTemplate?.Error ?? 999f),
                            ["bestTemplateEffectiveRoleSignature"] = ClassifyLowerArmBaseTemplateRoleSignature(bestTemplate?.Name),
                            ["bestTemplateBaseErrorDegrees"] = bestTemplate?.Error ?? 0f,
                            ["templateCandidateCount"] = templateBaseCandidates.Count,
                            ["templateBaseCandidates"] = new JArray(templateRows),
                            ["bestIndexSelectionBaseCandidate"] = bestIndexSelection?.Name ?? "",
                            ["bestIndexSelectionBaseCandidateFamily"] = ClassifyLowerArmBaseCandidateFamily(bestIndexSelection?.Name),
                            ["bestIndexSelectionBaseCandidatePortability"] = ClassifyLowerArmBaseCandidatePortability(bestIndexSelection?.Name, bestIndexSelection?.Error ?? 999f),
                            ["bestIndexSelectionBaseCandidateExpression"] = bestIndexSelection?.Expression ?? BuildLowerArmBaseCandidateExpressionSummary(null),
                            ["bestIndexSelectionBaseErrorDegrees"] = bestIndexSelection?.Error ?? 0f,
                            ["indexSelectionCandidateCount"] = indexSelectionBaseCandidates.Count,
                            ["indexSelectionBaseCandidates"] = new JArray(indexSelectionRows),
                            ["currentZeroErrorDegrees"] = baseCandidates.TryGetValue("currentZero", out var currentZero) ? QuaternionAngleDegrees(baseQ, currentZero) : 0f,
                            ["candidateCount"] = baseCandidates.Count,
                        });
                    }
                }
            }

            var groups = rows
                .GroupBy(x => $"{(string)x["targetHumanBone"]}|{(string)x["muscleName"]}", StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var items = group.ToArray();
                    var first = items[0];
                    return new JObject
                    {
                        ["targetHumanBone"] = (string)first["targetHumanBone"],
                        ["muscleName"] = (string)first["muscleName"],
                        ["muscleFamily"] = (string)first["muscleFamily"],
                        ["avatarLayout"] = first["avatarLayout"]?.DeepClone() ?? new JObject(),
                        ["rowCount"] = items.Length,
                        ["bestBaseCandidate"] = (string)items
                            .GroupBy(x => (string)x["bestBaseCandidate"], StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(x => x.Count())
                            .ThenBy(x => x.Average(y => JsonFloat(y, "bestBaseErrorDegrees")))
                            .First().Key,
                        ["bestBaseCandidateFamily"] = (string)items
                            .GroupBy(x => (string)x["bestBaseCandidateFamily"], StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(x => x.Count())
                            .ThenBy(x => x.Average(y => JsonFloat(y, "bestBaseErrorDegrees")))
                            .First().Key,
                        ["bestBaseCandidatePortability"] = (string)items
                            .GroupBy(x => (string)x["bestBaseCandidatePortability"], StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(x => x.Count())
                            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                            .First().Key,
                        ["bestBaseEffectiveFactorSignature"] = (string)items
                            .GroupBy(x => (string)x["bestBaseCandidateExpression"]?["effectiveFactorSignature"], StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(x => x.Count())
                            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                            .First().Key,
                        ["bestBaseEffectiveRoleSignature"] = (string)items
                            .GroupBy(x => (string)x["bestBaseCandidateExpression"]?["effectiveRoleSignature"], StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(x => x.Count())
                            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                            .First().Key,
                        ["maxBestBaseCandidateFactorCount"] = items.Max(x => (int?)x["bestBaseCandidateFactorCount"] ?? 0),
                        ["maxBestBaseErrorDegrees"] = items.Max(x => JsonFloat(x, "bestBaseErrorDegrees")),
                        ["avgBestBaseErrorDegrees"] = items.Average(x => JsonFloat(x, "bestBaseErrorDegrees")),
                        ["bestReducedBaseCandidate"] = (string)items
                            .GroupBy(x => (string)x["bestReducedBaseCandidate"], StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(x => x.Count())
                            .ThenBy(x => x.Average(y => JsonFloat(y, "bestReducedBaseErrorDegrees")))
                            .First().Key,
                        ["bestReducedBaseCandidateFamily"] = (string)items
                            .GroupBy(x => (string)x["bestReducedBaseCandidateFamily"], StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(x => x.Count())
                            .ThenBy(x => x.Average(y => JsonFloat(y, "bestReducedBaseErrorDegrees")))
                            .First().Key,
                        ["bestReducedBaseCandidatePortability"] = (string)items
                            .GroupBy(x => (string)x["bestReducedBaseCandidatePortability"], StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(x => x.Count())
                            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                            .First().Key,
                        ["bestReducedEffectiveFactorSignature"] = (string)items
                            .GroupBy(x => (string)x["bestReducedBaseCandidateExpression"]?["effectiveFactorSignature"], StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(x => x.Count())
                            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                            .First().Key,
                        ["bestReducedEffectiveRoleSignature"] = (string)items
                            .GroupBy(x => (string)x["bestReducedBaseCandidateExpression"]?["effectiveRoleSignature"], StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(x => x.Count())
                            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                            .First().Key,
                        ["maxReducedBaseCandidateFactorCount"] = items.Max(x => (int?)x["bestReducedBaseCandidateFactorCount"] ?? 0),
                        ["maxReducedBaseErrorDegrees"] = items.Max(x => JsonFloat(x, "bestReducedBaseErrorDegrees")),
                        ["avgReducedBaseErrorDegrees"] = items.Average(x => JsonFloat(x, "bestReducedBaseErrorDegrees")),
                        ["bestTemplateBaseCandidate"] = (string)items
                            .GroupBy(x => (string)x["bestTemplateBaseCandidate"], StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(x => x.Count())
                            .ThenBy(x => x.Average(y => JsonFloat(y, "bestTemplateBaseErrorDegrees")))
                            .First().Key,
                        ["bestTemplateBaseCandidateFamily"] = (string)items
                            .GroupBy(x => (string)x["bestTemplateBaseCandidateFamily"], StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(x => x.Count())
                            .ThenBy(x => x.Average(y => JsonFloat(y, "bestTemplateBaseErrorDegrees")))
                            .First().Key,
                        ["bestTemplateBaseCandidatePortability"] = (string)items
                            .GroupBy(x => (string)x["bestTemplateBaseCandidatePortability"], StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(x => x.Count())
                            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                            .First().Key,
                        ["bestTemplateEffectiveRoleSignature"] = (string)items
                            .GroupBy(x => (string)x["bestTemplateEffectiveRoleSignature"], StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(x => x.Count())
                            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                            .First().Key,
                        ["maxTemplateBaseErrorDegrees"] = items.Max(x => JsonFloat(x, "bestTemplateBaseErrorDegrees")),
                        ["avgTemplateBaseErrorDegrees"] = items.Average(x => JsonFloat(x, "bestTemplateBaseErrorDegrees")),
                        ["bestIndexSelectionBaseCandidate"] = (string)items
                            .GroupBy(x => (string)x["bestIndexSelectionBaseCandidate"], StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(x => x.Count())
                            .ThenBy(x => x.Average(y => JsonFloat(y, "bestIndexSelectionBaseErrorDegrees")))
                            .First().Key,
                        ["bestIndexSelectionBaseCandidateFamily"] = (string)items
                            .GroupBy(x => (string)x["bestIndexSelectionBaseCandidateFamily"], StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(x => x.Count())
                            .ThenBy(x => x.Average(y => JsonFloat(y, "bestIndexSelectionBaseErrorDegrees")))
                            .First().Key,
                        ["bestIndexSelectionBaseCandidatePortability"] = (string)items
                            .GroupBy(x => (string)x["bestIndexSelectionBaseCandidatePortability"], StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(x => x.Count())
                            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                            .First().Key,
                        ["bestIndexSelectionEffectiveFactorSignature"] = (string)items
                            .GroupBy(x => (string)x["bestIndexSelectionBaseCandidateExpression"]?["effectiveFactorSignature"], StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(x => x.Count())
                            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                            .First().Key,
                        ["bestIndexSelectionEffectiveRoleSignature"] = (string)items
                            .GroupBy(x => (string)x["bestIndexSelectionBaseCandidateExpression"]?["effectiveRoleSignature"], StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(x => x.Count())
                            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                            .First().Key,
                        ["maxIndexSelectionBaseErrorDegrees"] = items.Max(x => JsonFloat(x, "bestIndexSelectionBaseErrorDegrees")),
                        ["avgIndexSelectionBaseErrorDegrees"] = items.Average(x => JsonFloat(x, "bestIndexSelectionBaseErrorDegrees")),
                        ["maxCurrentZeroErrorDegrees"] = items.Max(x => JsonFloat(x, "currentZeroErrorDegrees")),
                        ["avgCurrentZeroErrorDegrees"] = items.Average(x => JsonFloat(x, "currentZeroErrorDegrees")),
                    };
                })
                .OrderBy(x => JsonFloat(x, "maxBestBaseErrorDegrees"))
                .ThenBy(x => (string)x["targetHumanBone"], StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => (string)x["muscleName"], StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new JObject
            {
                ["status"] = rows.Count == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: tries to explain the Unity single-muscle baseQ used by the perfect lower-arm partial oracle with deterministic Avatar/rest/currentZero base candidates. Low shared errors identify the base space that production formulas should reproduce.",
                ["rowCount"] = rows.Count,
                ["groupCount"] = groups.Length,
                ["maxBestBaseErrorDegrees"] = rows.Count == 0 ? 0f : rows.Max(x => JsonFloat(x, "bestBaseErrorDegrees")),
                ["avgBestBaseErrorDegrees"] = rows.Count == 0 ? 0.0 : rows.Average(x => JsonFloat(x, "bestBaseErrorDegrees")),
                ["maxReducedBaseErrorDegrees"] = rows.Count == 0 ? 0f : rows.Max(x => JsonFloat(x, "bestReducedBaseErrorDegrees")),
                ["avgReducedBaseErrorDegrees"] = rows.Count == 0 ? 0.0 : rows.Average(x => JsonFloat(x, "bestReducedBaseErrorDegrees")),
                ["maxTemplateBaseErrorDegrees"] = rows.Count == 0 ? 0f : rows.Max(x => JsonFloat(x, "bestTemplateBaseErrorDegrees")),
                ["avgTemplateBaseErrorDegrees"] = rows.Count == 0 ? 0.0 : rows.Average(x => JsonFloat(x, "bestTemplateBaseErrorDegrees")),
                ["maxIndexSelectionBaseErrorDegrees"] = rows.Count == 0 ? 0f : rows.Max(x => JsonFloat(x, "bestIndexSelectionBaseErrorDegrees")),
                ["avgIndexSelectionBaseErrorDegrees"] = rows.Count == 0 ? 0.0 : rows.Average(x => JsonFloat(x, "bestIndexSelectionBaseErrorDegrees")),
                ["candidateFamilySummary"] = BuildLowerArmBaseCandidateFamilySummary(rows),
                ["reducedCandidateFamilySummary"] = BuildLowerArmReducedBaseCandidateFamilySummary(rows),
                ["templateCandidateSummary"] = BuildLowerArmTemplateBaseCandidateSummary(rows),
                ["templateCandidateGrid"] = BuildLowerArmTemplateBaseCandidateGrid(rows),
                ["indexSelectionCandidateSummary"] = BuildLowerArmIndexSelectionBaseCandidateSummary(rows),
                ["indexSelectionCandidateGrid"] = BuildLowerArmIndexSelectionBaseCandidateGrid(rows),
                ["groups"] = new JArray(groups),
                ["topRows"] = new JArray(rows
                    .OrderBy(x => JsonFloat(x, "bestBaseErrorDegrees"))
                    .ThenByDescending(x => JsonFloat(x, "currentZeroErrorDegrees"))
                    .Take(64)),
            };
        }

        private static Dictionary<string, float[]> BuildLowerArmBaseAlignmentCandidates(
            string targetPath,
            SolverAxis axis,
            JObject solver,
            JArray solverNodes,
            JArray humanBoneIndex,
            string humanBone,
            JObject[] gltfNodes,
            int?[] gltfParents,
            Dictionary<string, int> nodePathToIndex)
        {
            var currentZero = Normalize(Multiply(Normalize(axis.PreQ), Inverse(Normalize(axis.PostQ))));
            var result = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["currentZero"] = currentZero,
                ["identity"] = IdentityQuaternion,
            };
            var corrections = BuildResidualCorrectionCandidates(targetPath, axis, solver, solverNodes, humanBoneIndex, humanBone, gltfNodes, gltfParents, nodePathToIndex);
            foreach (var pair in BuildShortProductCorrectionCandidates(corrections))
            {
                corrections[pair.Key] = pair.Value;
            }

            foreach (var pair in corrections)
            {
                var value = Normalize(pair.Value);
                AddNamedCorrectionCandidate(result, pair.Key, value);
                AddNamedCorrectionCandidate(result, $"{pair.Key}*currentZero", Normalize(Multiply(value, currentZero)));
                AddNamedCorrectionCandidate(result, $"currentZero*{pair.Key}", Normalize(Multiply(currentZero, value)));
            }
            return result;
        }

        private static Dictionary<string, float[]> BuildReducedLowerArmBaseAlignmentCandidates(Dictionary<string, float[]> baseCandidates)
        {
            var result = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            if (baseCandidates == null ||
                !baseCandidates.TryGetValue("currentZero", out var currentZeroValue))
            {
                return result;
            }

            var currentZero = Normalize(currentZeroValue);
            AddNamedCorrectionCandidate(result, "currentZero", currentZero);

            var primitives = baseCandidates
                         .Where(x => IsReducedLowerArmBasePrimitive(x.Key))
                         .Select(x => new KeyValuePair<string, float[]>(x.Key, Normalize(x.Value)))
                         .ToArray();

            foreach (var pair in primitives)
            {
                AddNamedCorrectionCandidate(result, pair.Key, pair.Value);
                AddNamedCorrectionCandidate(result, $"inverse({pair.Key})", Inverse(pair.Value));
                AddNamedCorrectionCandidate(result, $"currentZero*{pair.Key}", Normalize(Multiply(currentZero, pair.Value)));
                AddNamedCorrectionCandidate(result, $"{pair.Key}*currentZero", Normalize(Multiply(pair.Value, currentZero)));
                AddNamedCorrectionCandidate(result, $"currentZero*inverse({pair.Key})", Normalize(Multiply(currentZero, Inverse(pair.Value))));
                AddNamedCorrectionCandidate(result, $"inverse({pair.Key})*currentZero", Normalize(Multiply(Inverse(pair.Value), currentZero)));
            }

            foreach (var a in primitives)
            {
                foreach (var b in primitives)
                {
                    var productName = $"{a.Key}*{b.Key}";
                    var product = Normalize(Multiply(a.Value, b.Value));
                    var inverseProduct = Inverse(product);
                    AddNamedCorrectionCandidate(result, productName, product);
                    AddNamedCorrectionCandidate(result, $"inverse({productName})", inverseProduct);
                    AddNamedCorrectionCandidate(result, $"currentZero*{productName}", Normalize(Multiply(currentZero, product)));
                    AddNamedCorrectionCandidate(result, $"{productName}*currentZero", Normalize(Multiply(product, currentZero)));
                    AddNamedCorrectionCandidate(result, $"currentZero*inverse({productName})", Normalize(Multiply(currentZero, inverseProduct)));
                    AddNamedCorrectionCandidate(result, $"inverse({productName})*currentZero", Normalize(Multiply(inverseProduct, currentZero)));
                }
            }
            return result;
        }

        private static Dictionary<string, float[]> BuildLowerArmBaseTemplateCandidates(Dictionary<string, float[]> baseCandidates)
        {
            var result = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            if (baseCandidates == null)
            {
                return result;
            }

            // 诊断模板来自 NPC/Gorou 当前共同 oracle 线索，只用于交叉验证。
            // 如果同一模板能跨模型/左右手稳定，再迁入更小的生产候选。
            AddLowerArmBaseTemplateCandidate(
                result,
                baseCandidates,
                "template_npc_left_current_avatarLocal_crossDefaultOppositeToTarget_crossMirrorOppositePose",
                new[]
                {
                    new TemplateFactor("currentZero", false),
                    new TemplateFactor("avatarDefaultLocalPoseBySameIndex", false),
                    new TemplateFactor("crossSideAvatarDefaultOppositeToTargetPose", false),
                    new TemplateFactor("crossSideAvatarDefaultMirrorXYOppositePose", false),
                });
            AddLowerArmBaseTemplateCandidate(
                result,
                baseCandidates,
                "template_npc_right_current_crossMirrorOppositeLocal_crossMirrorTargetToOpposite_avatarPose",
                new[]
                {
                    new TemplateFactor("currentZero", false),
                    new TemplateFactor("crossSideAvatarDefaultMirrorXYOppositeLocalPose", false),
                    new TemplateFactor("crossSideAvatarDefaultMirrorXYTargetToOppositePose", false),
                    new TemplateFactor("avatarDefaultPoseBySameIndex", false),
                });
            AddLowerArmBaseTemplateCandidate(
                result,
                baseCandidates,
                "template_gorou_left_inverseParentPose_inverseParentPose_crossMirrorTargetToOpposite_inverseCurrent",
                new[]
                {
                    new TemplateFactor("avatarDefaultHumanSkeletonIndexParentPose", true),
                    new TemplateFactor("avatarDefaultHumanSkeletonIndexParentPose", true),
                    new TemplateFactor("crossSideAvatarDefaultMirrorXYTargetToOppositePose", false),
                    new TemplateFactor("currentZero", true),
                });
            AddLowerArmBaseTemplateCandidate(
                result,
                baseCandidates,
                "template_gorou_right_current_inverseCrossMirrorOppositeToTarget_avatarSkeletonParentLocal_inverseParentRest",
                new[]
                {
                    new TemplateFactor("currentZero", false),
                    new TemplateFactor("crossSideAvatarDefaultMirrorXYOppositeToTargetPose", true),
                    new TemplateFactor("avatarSkeletonDefaultSameIndexParentLocalPose", false),
                    new TemplateFactor("parentRest", true),
                });
            return result;
        }

        private static Dictionary<string, float[]> BuildLowerArmIndexSelectionBaseCandidates(Dictionary<string, float[]> baseCandidates)
        {
            var result = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            if (baseCandidates == null || !baseCandidates.ContainsKey("currentZero"))
            {
                return result;
            }

            // 诊断用：只替换 Unity AvatarConstant 的索引来源和 pose 字段，
            // 不做任意长乘积搜索，避免把过拟合候选误当成公式。
            var localSources = ExistingCandidateNames(baseCandidates,
                "avatarDefaultLocalPoseBySameIndex",
                "avatarDefaultLocalPoseByHumanSkeletonIndex",
                "avatarSkeletonDefaultLocalPoseBySameIndex",
                "avatarSkeletonDefaultLocalPoseByHumanSkeletonIndex",
                "avatarDefaultSameIndexParentLocalPose",
                "avatarDefaultHumanSkeletonIndexParentLocalPose",
                "avatarSkeletonDefaultSameIndexParentLocalPose",
                "avatarSkeletonDefaultHumanSkeletonIndexParentLocalPose");
            var poseSources = ExistingCandidateNames(baseCandidates,
                "avatarDefaultPoseBySameIndex",
                "avatarDefaultPoseByHumanSkeletonIndex",
                "avatarSkeletonDefaultPoseBySameIndex",
                "avatarSkeletonDefaultPoseByHumanSkeletonIndex",
                "avatarDefaultSameIndexParentPose",
                "avatarDefaultHumanSkeletonIndexParentPose",
                "avatarSkeletonDefaultSameIndexParentPose",
                "avatarSkeletonDefaultHumanSkeletonIndexParentPose");
            var parentSources = ExistingCandidateNames(baseCandidates,
                "avatarDefaultSameIndexParentPose",
                "avatarDefaultHumanSkeletonIndexParentPose",
                "avatarSkeletonDefaultSameIndexParentPose",
                "avatarSkeletonDefaultHumanSkeletonIndexParentPose");
            var parentLocalSources = ExistingCandidateNames(baseCandidates,
                "avatarDefaultSameIndexParentLocalPose",
                "avatarDefaultHumanSkeletonIndexParentLocalPose",
                "avatarSkeletonDefaultSameIndexParentLocalPose",
                "avatarSkeletonDefaultHumanSkeletonIndexParentLocalPose");
            var crossRelations = ExistingCandidateNames(baseCandidates,
                "crossSideAvatarDefaultOppositeToTargetPose",
                "crossSideAvatarDefaultTargetToOppositePose",
                "crossSideAvatarDefaultMirrorXYOppositeToTargetPose",
                "crossSideAvatarDefaultMirrorXYTargetToOppositePose",
                "crossSideAvatarSkeletonDefaultOppositeToTargetPose",
                "crossSideAvatarSkeletonDefaultTargetToOppositePose",
                "crossSideAvatarSkeletonDefaultMirrorXYOppositeToTargetPose",
                "crossSideAvatarSkeletonDefaultMirrorXYTargetToOppositePose");
            var crossPoses = ExistingCandidateNames(baseCandidates,
                "crossSideAvatarDefaultMirrorXYOppositePose",
                "crossSideAvatarDefaultMirrorXYOppositeLocalPose",
                "crossSideAvatarSkeletonDefaultMirrorXYOppositePose",
                "crossSideAvatarSkeletonDefaultMirrorXYOppositeLocalPose");
            var crossLocals = ExistingCandidateNames(baseCandidates,
                "crossSideAvatarDefaultMirrorXYOppositeLocalPose",
                "crossSideAvatarDefaultOppositeLocalPose",
                "crossSideAvatarSkeletonDefaultMirrorXYOppositeLocalPose",
                "crossSideAvatarSkeletonDefaultOppositeLocalPose");

            foreach (var local in localSources)
            {
                foreach (var relation in crossRelations)
                {
                    foreach (var crossPose in crossPoses)
                    {
                        AddLowerArmBaseFormulaCandidate(result, baseCandidates, "currentZero", local, relation, crossPose);
                    }
                }
            }

            foreach (var crossLocal in crossLocals)
            {
                foreach (var relation in crossRelations)
                {
                    foreach (var pose in poseSources)
                    {
                        AddLowerArmBaseFormulaCandidate(result, baseCandidates, "currentZero", crossLocal, relation, pose);
                    }
                }
            }

            foreach (var parent in parentSources)
            {
                foreach (var relation in crossRelations)
                {
                    AddLowerArmBaseFormulaCandidate(result, baseCandidates, $"inverse({parent})", $"inverse({parent})", relation, "inverse(currentZero)");
                }
            }

            foreach (var parentLocal in parentLocalSources)
            {
                foreach (var relation in crossRelations)
                {
                    AddLowerArmBaseFormulaCandidate(result, baseCandidates, "currentZero", $"inverse({relation})", parentLocal, "inverse(parentRest)");
                }
            }

            foreach (var local in localSources)
            {
                foreach (var parentLocal in parentLocalSources)
                {
                    AddLowerArmBaseFormulaCandidate(result, baseCandidates, "inverse(currentZero)", local, "inverse(localRest)", parentLocal);
                }
            }

            foreach (var crossPose in crossPoses)
            {
                AddLowerArmBaseFormulaCandidate(result, baseCandidates, "currentZero", "parentRest", crossPose, "localRest");
            }

            return result;
        }

        private static JObject BuildLowerArmAvatarLayoutSummary(
            string targetPath,
            string humanBone,
            JObject solver,
            JArray solverNodes,
            JArray humanBoneIndex,
            Dictionary<string, float[]> baseCandidates)
        {
            var humanNodeIndex = FindSolverNodeIndexByPath(solverNodes, targetPath);
            var mappedHumanNodeIndex = FindHumanNodeIndex(humanBoneIndex, humanBone);
            var avatarIndex = FindAvatarSkeletonIndex(solver, humanNodeIndex);
            var mappedAvatarIndex = FindAvatarSkeletonIndex(solver, mappedHumanNodeIndex);
            var skeletonNodes = solver?["skeleton"]?["nodes"] as JArray;
            var avatarNodes = solver?["avatarSkeleton"]?["nodes"] as JArray;
            TryGetOppositeHumanBone(humanBone, out var oppositeHumanBone);
            var oppositeHumanNodeIndex = FindHumanNodeIndex(humanBoneIndex, oppositeHumanBone);
            var oppositeAvatarIndex = FindAvatarSkeletonIndex(solver, oppositeHumanNodeIndex);

            return new JObject
            {
                ["humanBone"] = humanBone ?? "",
                ["targetPath"] = targetPath ?? "",
                ["side"] = humanBone?.StartsWith("Left", StringComparison.OrdinalIgnoreCase) == true
                    ? "Left"
                    : humanBone?.StartsWith("Right", StringComparison.OrdinalIgnoreCase) == true ? "Right" : "",
                ["humanNodeIndex"] = humanNodeIndex,
                ["mappedHumanNodeIndex"] = mappedHumanNodeIndex,
                ["sameAsHumanBoneIndex"] = humanNodeIndex >= 0 && humanNodeIndex == mappedHumanNodeIndex,
                ["avatarSkeletonIndex"] = avatarIndex,
                ["mappedAvatarSkeletonIndex"] = mappedAvatarIndex,
                ["sameAsMappedAvatarSkeletonIndex"] = avatarIndex >= 0 && avatarIndex == mappedAvatarIndex,
                ["skeletonParentIndex"] = ReadNodeParentIndex(skeletonNodes, humanNodeIndex),
                ["avatarSkeletonParentIndex"] = ReadNodeParentIndex(avatarNodes, avatarIndex),
                ["oppositeHumanBone"] = oppositeHumanBone ?? "",
                ["oppositeHumanNodeIndex"] = oppositeHumanNodeIndex,
                ["oppositeAvatarSkeletonIndex"] = oppositeAvatarIndex,
                ["oppositeSkeletonParentIndex"] = ReadNodeParentIndex(skeletonNodes, oppositeHumanNodeIndex),
                ["oppositeAvatarSkeletonParentIndex"] = ReadNodeParentIndex(avatarNodes, oppositeAvatarIndex),
                ["humanSkeletonIndexArrayCount"] = (solver?["humanSkeletonIndexArray"] as JArray)?.Count ?? 0,
                ["humanSkeletonReverseIndexArrayCount"] = (solver?["humanSkeletonReverseIndexArray"] as JArray)?.Count ?? 0,
                ["poseCandidateAngles"] = BuildLowerArmAvatarLayoutCandidateAngles(baseCandidates),
            };
        }

        private static JObject BuildLowerArmAvatarLayoutCandidateAngles(Dictionary<string, float[]> baseCandidates)
        {
            return new JObject
            {
                ["avatarDefaultPoseSameVsHumanSkeletonIndex"] = CandidateAngleOrNull(baseCandidates, "avatarDefaultPoseBySameIndex", "avatarDefaultPoseByHumanSkeletonIndex"),
                ["avatarDefaultLocalSameVsHumanSkeletonIndex"] = CandidateAngleOrNull(baseCandidates, "avatarDefaultLocalPoseBySameIndex", "avatarDefaultLocalPoseByHumanSkeletonIndex"),
                ["avatarDefaultParentPoseSameVsHumanSkeletonIndex"] = CandidateAngleOrNull(baseCandidates, "avatarDefaultSameIndexParentPose", "avatarDefaultHumanSkeletonIndexParentPose"),
                ["avatarDefaultParentLocalSameVsHumanSkeletonIndex"] = CandidateAngleOrNull(baseCandidates, "avatarDefaultSameIndexParentLocalPose", "avatarDefaultHumanSkeletonIndexParentLocalPose"),
                ["avatarSkeletonDefaultParentLocalVsAvatarDefaultHumanParentLocal"] = CandidateAngleOrNull(baseCandidates, "avatarSkeletonDefaultSameIndexParentLocalPose", "avatarDefaultHumanSkeletonIndexParentLocalPose"),
                ["localRestVsAvatarDefaultLocalSameIndex"] = CandidateAngleOrNull(baseCandidates, "localRest", "avatarDefaultLocalPoseBySameIndex"),
                ["localRestVsAvatarDefaultParentLocalSameIndex"] = CandidateAngleOrNull(baseCandidates, "localRest", "avatarDefaultSameIndexParentLocalPose"),
                ["parentRestVsAvatarSkeletonDefaultParentLocalSameIndex"] = CandidateAngleOrNull(baseCandidates, "parentRest", "avatarSkeletonDefaultSameIndexParentLocalPose"),
                ["crossMirrorOppositePoseVsCrossDefaultOppositePose"] = CandidateAngleOrNull(baseCandidates, "crossSideAvatarDefaultMirrorXYOppositePose", "crossSideAvatarDefaultOppositePose"),
                ["crossMirrorTargetToOppositeVsOppositeToTarget"] = CandidateAngleOrNull(baseCandidates, "crossSideAvatarDefaultMirrorXYTargetToOppositePose", "crossSideAvatarDefaultMirrorXYOppositeToTargetPose"),
            };
        }

        private static JToken CandidateAngleOrNull(Dictionary<string, float[]> candidates, string left, string right)
        {
            if (candidates == null ||
                string.IsNullOrWhiteSpace(left) ||
                string.IsNullOrWhiteSpace(right) ||
                !candidates.TryGetValue(left, out var leftRotation) ||
                !candidates.TryGetValue(right, out var rightRotation))
            {
                return JValue.CreateNull();
            }
            return QuaternionAngleDegrees(leftRotation, rightRotation);
        }

        private static int ReadNodeParentIndex(JArray nodes, int index)
        {
            return nodes != null && index >= 0 && index < nodes.Count
                ? (int?)nodes[index]?["parentId"] ?? -1
                : -1;
        }

        private static string[] ExistingCandidateNames(Dictionary<string, float[]> baseCandidates, params string[] names)
        {
            if (baseCandidates == null || names == null)
            {
                return Array.Empty<string>();
            }
            return names
                .Where(x => !string.IsNullOrWhiteSpace(x) && baseCandidates.ContainsKey(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static void AddLowerArmBaseFormulaCandidate(
            Dictionary<string, float[]> result,
            Dictionary<string, float[]> baseCandidates,
            params string[] factors)
        {
            if (result == null || baseCandidates == null || factors == null || factors.Length == 0)
            {
                return;
            }

            var value = IdentityQuaternion;
            foreach (var rawFactor in factors)
            {
                if (!TryReadLowerArmBaseFormulaFactor(baseCandidates, rawFactor, out var factor))
                {
                    return;
                }
                value = Normalize(Multiply(value, factor));
            }

            var name = string.Join("*", factors);
            result[name] = Normalize(value);
        }

        private static bool TryReadLowerArmBaseFormulaFactor(Dictionary<string, float[]> baseCandidates, string rawFactor, out float[] value)
        {
            value = null;
            if (baseCandidates == null || string.IsNullOrWhiteSpace(rawFactor))
            {
                return false;
            }

            var factor = rawFactor.Trim();
            var inverted = false;
            if (TryUnwrapFunction(factor, "inverse", out var inner))
            {
                inverted = true;
                factor = inner;
            }

            if (!baseCandidates.TryGetValue(factor, out var raw))
            {
                return false;
            }

            var normalized = Normalize(raw);
            value = inverted ? Inverse(normalized) : normalized;
            return true;
        }

        private static void AddLowerArmBaseTemplateCandidate(
            Dictionary<string, float[]> result,
            Dictionary<string, float[]> baseCandidates,
            string name,
            TemplateFactor[] factors)
        {
            if (result == null ||
                baseCandidates == null ||
                string.IsNullOrWhiteSpace(name) ||
                factors == null ||
                factors.Length == 0)
            {
                return;
            }

            var value = IdentityQuaternion;
            foreach (var factor in factors)
            {
                if (!baseCandidates.TryGetValue(factor.Name, out var raw))
                {
                    return;
                }
                var next = factor.Inverted ? Inverse(raw) : Normalize(raw);
                value = Normalize(Multiply(value, next));
            }
            result[name] = Normalize(value);
        }

        private static JArray BuildLowerArmTemplateBaseCandidateSummary(IEnumerable<JObject> rows)
        {
            return new JArray((rows ?? Enumerable.Empty<JObject>())
                .GroupBy(x => (string)x["bestTemplateBaseCandidate"] ?? "unknown", StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var items = group.ToArray();
                    return new JObject
                    {
                        ["candidate"] = group.Key,
                        ["rowCount"] = items.Length,
                        ["maxTemplateBaseErrorDegrees"] = items.Max(x => JsonFloat(x, "bestTemplateBaseErrorDegrees")),
                        ["avgTemplateBaseErrorDegrees"] = items.Average(x => JsonFloat(x, "bestTemplateBaseErrorDegrees")),
                        ["effectiveRoleSignature"] = (string)items[0]["bestTemplateEffectiveRoleSignature"] ?? "",
                    };
                })
                .OrderBy(x => JsonFloat(x, "maxTemplateBaseErrorDegrees"))
                .ThenBy(x => (string)x["candidate"], StringComparer.OrdinalIgnoreCase));
        }

        private static JArray BuildLowerArmTemplateBaseCandidateGrid(IEnumerable<JObject> rows)
        {
            var flatRows = new List<LowerArmTemplateCandidateGridRow>();
            foreach (var row in rows ?? Enumerable.Empty<JObject>())
            {
                foreach (var template in (row["templateBaseCandidates"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    flatRows.Add(new LowerArmTemplateCandidateGridRow(
                        (string)row["targetHumanBone"] ?? "",
                        (string)row["muscleName"] ?? "",
                        (string)template["candidate"] ?? "",
                        (string)template["roleSignature"] ?? "",
                        JsonFloat(template, "errorDegrees")));
                }
            }

            return new JArray(flatRows
                .GroupBy(x => x.Candidate, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var items = group.ToArray();
                    return new JObject
                    {
                        ["candidate"] = group.Key,
                        ["roleSignature"] = items.Length == 0 ? "" : items[0].RoleSignature,
                        ["rowCount"] = items.Length,
                        ["maxErrorDegrees"] = items.Length == 0 ? 0f : items.Max(x => x.Error),
                        ["avgErrorDegrees"] = items.Length == 0 ? 0.0 : items.Average(x => x.Error),
                        ["bestTargets"] = new JArray(items
                            .OrderBy(x => x.Error)
                            .Take(8)
                            .Select(x => new JObject
                            {
                                ["targetHumanBone"] = x.TargetHumanBone,
                                ["muscleName"] = x.MuscleName,
                                ["errorDegrees"] = x.Error,
                            })),
                        ["worstTargets"] = new JArray(items
                            .OrderByDescending(x => x.Error)
                            .Take(8)
                            .Select(x => new JObject
                            {
                                ["targetHumanBone"] = x.TargetHumanBone,
                                ["muscleName"] = x.MuscleName,
                                ["errorDegrees"] = x.Error,
                            })),
                    };
                })
                .OrderBy(x => JsonFloat(x, "maxErrorDegrees"))
                .ThenBy(x => (string)x["candidate"], StringComparer.OrdinalIgnoreCase));
        }

        private static JArray BuildLowerArmIndexSelectionBaseCandidateSummary(IEnumerable<JObject> rows)
        {
            return new JArray((rows ?? Enumerable.Empty<JObject>())
                .GroupBy(x => (string)x["bestIndexSelectionBaseCandidateExpression"]?["effectiveRoleSignature"] ?? "unknown", StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var items = group.ToArray();
                    return new JObject
                    {
                        ["effectiveRoleSignature"] = group.Key,
                        ["rowCount"] = items.Length,
                        ["maxIndexSelectionBaseErrorDegrees"] = items.Max(x => JsonFloat(x, "bestIndexSelectionBaseErrorDegrees")),
                        ["avgIndexSelectionBaseErrorDegrees"] = items.Average(x => JsonFloat(x, "bestIndexSelectionBaseErrorDegrees")),
                        ["candidateVotes"] = new JArray(items
                            .GroupBy(x => (string)x["bestIndexSelectionBaseCandidate"], StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(x => x.Count())
                            .ThenBy(x => x.Average(y => JsonFloat(y, "bestIndexSelectionBaseErrorDegrees")))
                            .Take(8)
                            .Select(x => new JObject
                            {
                                ["candidate"] = x.Key,
                                ["count"] = x.Count(),
                                ["maxErrorDegrees"] = x.Max(y => JsonFloat(y, "bestIndexSelectionBaseErrorDegrees")),
                                ["avgErrorDegrees"] = x.Average(y => JsonFloat(y, "bestIndexSelectionBaseErrorDegrees")),
                            })),
                    };
                })
                .OrderBy(x => JsonFloat(x, "maxIndexSelectionBaseErrorDegrees"))
                .ThenBy(x => (string)x["effectiveRoleSignature"], StringComparer.OrdinalIgnoreCase));
        }

        private static JArray BuildLowerArmIndexSelectionBaseCandidateGrid(IEnumerable<JObject> rows)
        {
            var flatRows = new List<JObject>();
            foreach (var row in rows ?? Enumerable.Empty<JObject>())
            {
                foreach (var candidate in (row["indexSelectionBaseCandidates"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    flatRows.Add(new JObject
                    {
                        ["targetHumanBone"] = (string)row["targetHumanBone"] ?? "",
                        ["muscleName"] = (string)row["muscleName"] ?? "",
                        ["candidate"] = (string)candidate["candidate"] ?? "",
                        ["effectiveRoleSignature"] = (string)candidate["effectiveRoleSignature"] ?? "",
                        ["effectiveFactorSignature"] = (string)candidate["effectiveFactorSignature"] ?? "",
                        ["factorCount"] = (int?)candidate["factorCount"] ?? 0,
                        ["errorDegrees"] = JsonFloat(candidate, "errorDegrees"),
                    });
                }
            }

            return new JArray(flatRows
                .GroupBy(x => (string)x["effectiveRoleSignature"] ?? "unknown", StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var items = group.ToArray();
                    return new JObject
                    {
                        ["effectiveRoleSignature"] = group.Key,
                        ["rowCount"] = items.Length,
                        ["bestErrorDegrees"] = items.Length == 0 ? 0f : items.Min(x => JsonFloat(x, "errorDegrees")),
                        ["maxErrorDegrees"] = items.Length == 0 ? 0f : items.Max(x => JsonFloat(x, "errorDegrees")),
                        ["avgErrorDegrees"] = items.Length == 0 ? 0.0 : items.Average(x => JsonFloat(x, "errorDegrees")),
                        ["bestCandidates"] = new JArray(items
                            .OrderBy(x => JsonFloat(x, "errorDegrees"))
                            .ThenBy(x => (int?)x["factorCount"] ?? 99)
                            .Take(8)
                            .Select(x => new JObject
                            {
                                ["targetHumanBone"] = (string)x["targetHumanBone"],
                                ["muscleName"] = (string)x["muscleName"],
                                ["candidate"] = (string)x["candidate"],
                                ["errorDegrees"] = JsonFloat(x, "errorDegrees"),
                            })),
                    };
                })
                .OrderBy(x => JsonFloat(x, "maxErrorDegrees"))
                .ThenBy(x => JsonFloat(x, "avgErrorDegrees"))
                .ThenBy(x => (string)x["effectiveRoleSignature"], StringComparer.OrdinalIgnoreCase));
        }

        private static string ClassifyLowerArmBaseTemplateRoleSignature(string templateName)
        {
            return templateName switch
            {
                "template_npc_left_current_avatarLocal_crossDefaultOppositeToTarget_crossMirrorOppositePose" =>
                    "currentZero*avatarDefault.local*crossDefault.oppositeToTarget*crossMirrorDefault.oppositePose",
                "template_npc_right_current_crossMirrorOppositeLocal_crossMirrorTargetToOpposite_avatarPose" =>
                    "currentZero*crossMirrorDefault.oppositeLocal*crossMirrorDefault.targetToOpposite*avatarDefault.pose",
                "template_gorou_left_inverseParentPose_inverseParentPose_crossMirrorTargetToOpposite_inverseCurrent" =>
                    "inverse:avatarDefault.parentPose*inverse:avatarDefault.parentPose*crossMirrorDefault.targetToOpposite*inverse:currentZero",
                "template_gorou_right_current_inverseCrossMirrorOppositeToTarget_avatarSkeletonParentLocal_inverseParentRest" =>
                    "currentZero*inverse:crossMirrorDefault.oppositeToTarget*avatarSkeletonDefault.parentLocal*inverse:parentRest",
                _ => "unknown",
            };
        }

        private static bool IsReducedLowerArmBasePrimitive(string name)
        {
            if (string.IsNullOrWhiteSpace(name) ||
                name.Contains("*", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("inverse(", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("mirrorXY(", StringComparison.OrdinalIgnoreCase) ||
                IsHandContextCandidateName(name))
            {
                return false;
            }

            return name.Equals("avatarDefaultLocalPoseBySameIndex", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("avatarDefaultPoseBySameIndex", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("avatarDefaultPoseByHumanSkeletonIndex", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("avatarDefaultLocalPoseByHumanSkeletonIndex", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("avatarSkeletonDefaultLocalPoseBySameIndex", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("avatarSkeletonDefaultPoseBySameIndex", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("parentRest", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("localRest", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("crossSideAvatarDefaultMirrorXYOppositePose", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("crossSideAvatarDefaultMirrorXYOppositeLocalPose", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("crossSideAvatarDefaultMirrorXYTargetToOppositePose", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("crossSideAvatarDefaultMirrorXYOppositeToTargetPose", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("crossSideAvatarSkeletonDefaultMirrorXYOppositePose", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("crossSideAvatarSkeletonDefaultMirrorXYOppositeLocalPose", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("crossSideAvatarSkeletonDefaultMirrorXYTargetToOppositePose", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("crossSideAvatarSkeletonDefaultMirrorXYOppositeToTargetPose", StringComparison.OrdinalIgnoreCase);
        }

        private static JArray BuildLowerArmBaseCandidateFamilySummary(IEnumerable<JObject> rows)
        {
            return new JArray((rows ?? Enumerable.Empty<JObject>())
                .GroupBy(x => (string)x["bestBaseCandidateFamily"] ?? "unknown", StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var items = group.ToArray();
                    return new JObject
                    {
                        ["candidateFamily"] = group.Key,
                        ["rowCount"] = items.Length,
                        ["maxBestBaseErrorDegrees"] = items.Max(x => JsonFloat(x, "bestBaseErrorDegrees")),
                        ["avgBestBaseErrorDegrees"] = items.Average(x => JsonFloat(x, "bestBaseErrorDegrees")),
                        ["maxFactorCount"] = items.Max(x => (int?)x["bestBaseCandidateFactorCount"] ?? 0),
                        ["portabilityVotes"] = new JArray(items
                            .GroupBy(x => (string)x["bestBaseCandidatePortability"] ?? "unknown", StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(x => x.Count())
                            .Select(x => new JObject
                            {
                                ["value"] = x.Key,
                                ["count"] = x.Count(),
                            })),
                    };
                })
                .OrderBy(x => JsonFloat(x, "maxBestBaseErrorDegrees"))
                .ThenBy(x => (string)x["candidateFamily"], StringComparer.OrdinalIgnoreCase));
        }

        private static JArray BuildLowerArmReducedBaseCandidateFamilySummary(IEnumerable<JObject> rows)
        {
            return new JArray((rows ?? Enumerable.Empty<JObject>())
                .GroupBy(x => (string)x["bestReducedBaseCandidateFamily"] ?? "unknown", StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var items = group.ToArray();
                    return new JObject
                    {
                        ["candidateFamily"] = group.Key,
                        ["rowCount"] = items.Length,
                        ["maxReducedBaseErrorDegrees"] = items.Max(x => JsonFloat(x, "bestReducedBaseErrorDegrees")),
                        ["avgReducedBaseErrorDegrees"] = items.Average(x => JsonFloat(x, "bestReducedBaseErrorDegrees")),
                        ["maxFactorCount"] = items.Max(x => (int?)x["bestReducedBaseCandidateFactorCount"] ?? 0),
                        ["portabilityVotes"] = new JArray(items
                            .GroupBy(x => (string)x["bestReducedBaseCandidatePortability"] ?? "unknown", StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(x => x.Count())
                            .Select(x => new JObject
                            {
                                ["value"] = x.Key,
                                ["count"] = x.Count(),
                            })),
                    };
                })
                .OrderBy(x => JsonFloat(x, "maxReducedBaseErrorDegrees"))
                .ThenBy(x => (string)x["candidateFamily"], StringComparer.OrdinalIgnoreCase));
        }

        private static JObject BuildLowerArmBaseCandidateExpressionSummary(string candidateName)
        {
            var factors = ExpandLowerArmBaseExpression(candidateName, inverted: false)
                .Select(x => new JObject
                {
                    ["name"] = x.Name,
                    ["inverted"] = x.Inverted,
                    ["role"] = ClassifyLowerArmBaseCandidateFactorRole(x.Name),
                })
                .ToArray();
            return new JObject
            {
                ["raw"] = candidateName ?? "",
                ["effectiveFactorCount"] = factors.Length,
                ["effectiveFactorSignature"] = string.Join("*", factors.Select(x => ((bool)x["inverted"] ? "inverse:" : "") + (string)x["name"])),
                ["effectiveRoleSignature"] = string.Join("*", factors.Select(x => ((bool)x["inverted"] ? "inverse:" : "") + (string)x["role"])),
                ["hasCurrentZero"] = factors.Any(x => string.Equals((string)x["name"], "currentZero", StringComparison.OrdinalIgnoreCase)),
                ["factors"] = new JArray(factors),
            };
        }

        private static List<LowerArmBaseExpressionFactor> ExpandLowerArmBaseExpression(string expression, bool inverted)
        {
            var result = new List<LowerArmBaseExpressionFactor>();
            if (string.IsNullOrWhiteSpace(expression))
            {
                return result;
            }

            var trimmed = expression.Trim();
            if (TryUnwrapFunction(trimmed, "inverse", out var inner))
            {
                return ExpandLowerArmBaseExpression(inner, !inverted);
            }

            var parts = SplitTopLevelProduct(trimmed);
            if (parts.Count > 1)
            {
                if (inverted)
                {
                    parts.Reverse();
                }
                foreach (var part in parts)
                {
                    result.AddRange(ExpandLowerArmBaseExpression(part, inverted));
                }
                return result;
            }

            result.Add(new LowerArmBaseExpressionFactor(trimmed, inverted));
            return result;
        }

        private static List<string> SplitTopLevelProduct(string expression)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(expression))
            {
                return result;
            }

            var depth = 0;
            var start = 0;
            for (var i = 0; i < expression.Length; i++)
            {
                var ch = expression[i];
                if (ch == '(')
                {
                    depth++;
                }
                else if (ch == ')' && depth > 0)
                {
                    depth--;
                }
                else if (ch == '*' && depth == 0)
                {
                    result.Add(expression.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            result.Add(expression.Substring(start).Trim());
            return result.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        }

        private static bool TryUnwrapFunction(string expression, string functionName, out string inner)
        {
            inner = null;
            if (string.IsNullOrWhiteSpace(expression) ||
                string.IsNullOrWhiteSpace(functionName))
            {
                return false;
            }

            var prefix = functionName + "(";
            if (!expression.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !expression.EndsWith(")", StringComparison.Ordinal))
            {
                return false;
            }

            var depth = 0;
            for (var i = 0; i < expression.Length; i++)
            {
                if (expression[i] == '(')
                {
                    depth++;
                }
                else if (expression[i] == ')')
                {
                    depth--;
                    if (depth == 0 && i != expression.Length - 1)
                    {
                        return false;
                    }
                }
            }
            inner = expression.Substring(prefix.Length, expression.Length - prefix.Length - 1);
            return true;
        }

        private static string ClassifyLowerArmBaseCandidateFactorRole(string factorName)
        {
            if (string.IsNullOrWhiteSpace(factorName))
            {
                return "unknown";
            }
            if (factorName.Equals("currentZero", StringComparison.OrdinalIgnoreCase))
            {
                return "currentZero";
            }
            if (factorName.Contains("crossSideAvatarDefaultMirrorXY", StringComparison.OrdinalIgnoreCase))
            {
                if (factorName.Contains("OppositeToTarget", StringComparison.OrdinalIgnoreCase))
                {
                    return "crossMirrorDefault.oppositeToTarget";
                }
                if (factorName.Contains("TargetToOpposite", StringComparison.OrdinalIgnoreCase))
                {
                    return "crossMirrorDefault.targetToOpposite";
                }
                if (factorName.Contains("OppositeLocal", StringComparison.OrdinalIgnoreCase))
                {
                    return "crossMirrorDefault.oppositeLocal";
                }
                if (factorName.Contains("OppositePose", StringComparison.OrdinalIgnoreCase))
                {
                    return "crossMirrorDefault.oppositePose";
                }
                return "crossMirrorDefault";
            }
            if (factorName.Contains("crossSideAvatarDefault", StringComparison.OrdinalIgnoreCase))
            {
                if (factorName.Contains("OppositeToTarget", StringComparison.OrdinalIgnoreCase))
                {
                    return "crossDefault.oppositeToTarget";
                }
                if (factorName.Contains("TargetToOpposite", StringComparison.OrdinalIgnoreCase))
                {
                    return "crossDefault.targetToOpposite";
                }
                if (factorName.Contains("OppositeLocal", StringComparison.OrdinalIgnoreCase))
                {
                    return "crossDefault.oppositeLocal";
                }
                if (factorName.Contains("OppositePose", StringComparison.OrdinalIgnoreCase))
                {
                    return "crossDefault.oppositePose";
                }
                return "crossDefault";
            }
            if (factorName.Contains("avatarSkeletonDefault", StringComparison.OrdinalIgnoreCase))
            {
                if (factorName.Contains("ParentLocal", StringComparison.OrdinalIgnoreCase))
                {
                    return "avatarSkeletonDefault.parentLocal";
                }
                if (factorName.Contains("Local", StringComparison.OrdinalIgnoreCase))
                {
                    return "avatarSkeletonDefault.local";
                }
                return "avatarSkeletonDefault.pose";
            }
            if (factorName.Contains("avatarDefault", StringComparison.OrdinalIgnoreCase))
            {
                if (factorName.Contains("ParentPose", StringComparison.OrdinalIgnoreCase))
                {
                    return "avatarDefault.parentPose";
                }
                if (factorName.Contains("Local", StringComparison.OrdinalIgnoreCase))
                {
                    return "avatarDefault.local";
                }
                if (factorName.Contains("HumanSkeletonIndex", StringComparison.OrdinalIgnoreCase))
                {
                    return "avatarDefault.humanSkeletonIndexPose";
                }
                return "avatarDefault.pose";
            }
            if (factorName.Equals("parentRest", StringComparison.OrdinalIgnoreCase))
            {
                return "parentRest";
            }
            if (factorName.Equals("localRest", StringComparison.OrdinalIgnoreCase))
            {
                return "localRest";
            }
            if (factorName.Contains("humanSkeleton", StringComparison.OrdinalIgnoreCase))
            {
                return "humanSkeleton";
            }
            return "other";
        }

        private static string ClassifyLowerArmBaseCandidateFamily(string candidateName)
        {
            if (string.IsNullOrWhiteSpace(candidateName))
            {
                return "unknown";
            }

            var parts = new List<string>();
            if (candidateName.IndexOf("currentZero", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                parts.Add("currentZero");
            }
            if (candidateName.IndexOf("crossSideAvatarDefaultMirrorXY", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                parts.Add("crossSideAvatarDefaultMirrorXY");
            }
            else if (candidateName.IndexOf("crossSideAvatarDefault", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                parts.Add("crossSideAvatarDefault");
            }
            if (candidateName.IndexOf("avatarSkeletonDefault", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                parts.Add("avatarSkeletonDefault");
            }
            else if (candidateName.IndexOf("avatarDefault", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                parts.Add("avatarDefault");
            }
            if (candidateName.IndexOf("humanSkeleton", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                parts.Add("humanSkeleton");
            }
            if (candidateName.IndexOf("parentRest", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                parts.Add("parentRest");
            }
            if (candidateName.IndexOf("localPose", StringComparison.OrdinalIgnoreCase) >= 0 ||
                candidateName.IndexOf("LocalPose", StringComparison.OrdinalIgnoreCase) >= 0 ||
                candidateName.IndexOf("LocalRest", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                parts.Add("local");
            }
            if (candidateName.StartsWith("inverse(", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add("outerInverse");
            }

            return parts.Count == 0 ? "other" : string.Join("+", parts.Distinct(StringComparer.OrdinalIgnoreCase));
        }

        private static int CountLowerArmBaseCandidateFactors(string candidateName)
        {
            if (string.IsNullOrWhiteSpace(candidateName))
            {
                return 0;
            }

            return candidateName.Count(x => x == '*') + 1;
        }

        private static string ClassifyLowerArmBaseCandidatePortability(string candidateName, float errorDegrees)
        {
            var factorCount = CountLowerArmBaseCandidateFactors(candidateName);
            if (errorDegrees > 5f)
            {
                return "not_close_enough";
            }
            if (factorCount <= 2)
            {
                return "short_candidate";
            }
            if (factorCount <= 4)
            {
                return "medium_candidate_needs_reduction";
            }
            return "long_candidate_likely_overfit";
        }

        private static PartialOracleLowerArmMode[] BuildPartialOracleLowerArmModes()
        {
            var modes = new List<PartialOracleLowerArmMode>
            {
                new("current_lower_arm", "current", "current"),
            };
            foreach (var replacement in new[] { "oracle_arm_twist", "oracle_forearm_stretch", "oracle_arm_twist_forearm_stretch" })
            {
                foreach (var deltaMode in new[] { "leftDelta_append", "leftDelta_prepend", "rightDelta_append", "rightDelta_prepend" })
                {
                    modes.Add(new PartialOracleLowerArmMode($"{replacement}_{deltaMode}", replacement, deltaMode));
                }
            }
            return modes.ToArray();
        }

        private static bool TryBuildPartialOracleLowerArmRotation(
            Dictionary<string, JObject[]> probeByMuscle,
            Dictionary<string, List<FloatKey>> curves,
            SolverTarget target,
            SolverAxis targetAxis,
            float time,
            PartialOracleLowerArmMode mode,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex,
            string targetPath,
            HashSet<string> missing,
            Dictionary<string, int> clamped,
            out float[] rotation)
        {
            rotation = null;
            var useOracleStretch = mode.Replacement.IndexOf("forearm_stretch", StringComparison.OrdinalIgnoreCase) >= 0;
            var useOracleTwist = mode.Replacement.IndexOf("arm_twist", StringComparison.OrdinalIgnoreCase) >= 0;
            if (string.Equals(mode.Replacement, "current", StringComparison.OrdinalIgnoreCase))
            {
                rotation = BuildCurrentLowerArmPartialRotation(curves, target, targetAxis, time, true, true, solverNodes, solverAxes, humanBoneIndex);
                return true;
            }

            var oracleLeftDeltas = new List<float[]>();
            var oracleRightDeltas = new List<float[]>();
            float[] oracleBase = null;
            if (useOracleStretch &&
                !TryAddPartialOracleLowerArmDelta(probeByMuscle, curves, target.XAttribute, targetPath, time, oracleLeftDeltas, oracleRightDeltas, ref oracleBase, missing, clamped))
            {
                return false;
            }
            if (useOracleTwist &&
                !TryAddPartialOracleLowerArmDelta(probeByMuscle, curves, target.ZAttribute, targetPath, time, oracleLeftDeltas, oracleRightDeltas, ref oracleBase, missing, clamped))
            {
                return false;
            }

            var baseRotation = useOracleStretch && useOracleTwist && oracleBase != null
                ? oracleBase
                : BuildCurrentLowerArmPartialRotation(curves, target, targetAxis, time, !useOracleStretch, !useOracleTwist, solverNodes, solverAxes, humanBoneIndex);
            var useRight = mode.DeltaMode.StartsWith("rightDelta", StringComparison.OrdinalIgnoreCase);
            var append = mode.DeltaMode.EndsWith("_append", StringComparison.OrdinalIgnoreCase);
            var deltas = useRight ? oracleRightDeltas : oracleLeftDeltas;
            if (deltas.Count == 0)
            {
                rotation = baseRotation;
                return true;
            }

            var composed = ComposeDeltas(deltas, Enumerable.Range(0, deltas.Count).ToArray(), append);
            rotation = useRight
                ? Normalize(Multiply(baseRotation, composed))
                : Normalize(Multiply(composed, baseRotation));
            return true;
        }

        private static bool TryAddPartialOracleLowerArmDelta(
            Dictionary<string, JObject[]> probeByMuscle,
            Dictionary<string, List<FloatKey>> curves,
            string attribute,
            string targetPath,
            float time,
            List<float[]> leftDeltas,
            List<float[]> rightDeltas,
            ref float[] oracleBase,
            HashSet<string> missing,
            Dictionary<string, int> clamped)
        {
            if (string.IsNullOrWhiteSpace(attribute))
            {
                return true;
            }

            var value = SampleFloatCurve(curves.TryGetValue(attribute, out var curve) ? curve : null, time);
            if (!TrySampleOracleSingleMuscleDelta(probeByMuscle, attribute, targetPath, value, out var baseQ, out var leftDelta, out var rightDelta, out var wasClamped))
            {
                missing?.Add($"{targetPath}|{attribute}");
                return false;
            }
            if (wasClamped && clamped != null)
            {
                var key = $"{targetPath}|{attribute}";
                clamped[key] = clamped.TryGetValue(key, out var count) ? count + 1 : 1;
            }

            oracleBase ??= baseQ;
            leftDeltas.Add(leftDelta);
            rightDeltas.Add(rightDelta);
            return true;
        }

        private static float[] BuildCurrentLowerArmPartialRotation(
            Dictionary<string, List<FloatKey>> curves,
            SolverTarget target,
            SolverAxis targetAxis,
            float time,
            bool includeStretch,
            bool includeTwist,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex)
        {
            var angles = new float[3];
            if (includeStretch)
            {
                var stretch = SampleFloatCurve(curves.TryGetValue(target.XAttribute ?? string.Empty, out var stretchCurve) ? stretchCurve : null, time);
                angles[2] += LimitMuscle(stretch, targetAxis, 2);
            }
            if (includeTwist)
            {
                var twist = SampleFloatCurve(curves.TryGetValue(target.ZAttribute ?? string.Empty, out var twistCurve) ? twistCurve : null, time);
                if (MathF.Abs(twist) > 0.0000001f)
                {
                    var sourceAxis = targetAxis;
                    var sourceHumanBone = ResolveArmTwistSourceHumanBone(target.HumanBone, "lower");
                    if (!string.Equals(sourceHumanBone, target.HumanBone, StringComparison.OrdinalIgnoreCase) &&
                        !TryGetHumanBonePathAxis(solverNodes, solverAxes, humanBoneIndex, sourceHumanBone, out _, out sourceAxis))
                    {
                        sourceAxis = targetAxis;
                    }
                    angles[0] += LimitMuscle(twist, sourceAxis, 0);
                }
            }

            var pre = Normalize(targetAxis.PreQ);
            var post = Normalize(targetAxis.PostQ);
            return Normalize(Multiply(Multiply(pre, Multiply(SwingRadiansToQuaternion(angles[1], angles[2]), AxisAngleRadiansToQuaternion(1, 0, 0, angles[0]))), Inverse(post)));
        }

        private static bool TrySampleOracleSingleMuscleDelta(
            Dictionary<string, JObject[]> probeByMuscle,
            string muscleName,
            string targetPath,
            float value,
            out float[] baseRotation,
            out float[] leftDelta,
            out float[] rightDelta,
            out bool clamped)
        {
            baseRotation = null;
            leftDelta = null;
            rightDelta = null;
            clamped = false;
            if (string.IsNullOrWhiteSpace(muscleName) ||
                probeByMuscle == null ||
                !probeByMuscle.TryGetValue(muscleName, out var probes))
            {
                return false;
            }

            var samples = new List<OracleSingleMuscleDeltaSample>();
            foreach (var probe in probes)
            {
                if (!TryReadProbeRotation(probe, targetPath, out _, out var baseQ, out var rotation))
                {
                    continue;
                }

                var sampleValue = (float?)probe["value"] ?? 0f;
                samples.Add(new OracleSingleMuscleDeltaSample(
                    sampleValue,
                    baseQ,
                    Normalize(Multiply(rotation, Inverse(baseQ))),
                    Normalize(Multiply(Inverse(baseQ), rotation))));
            }

            var ordered = samples.OrderBy(x => x.Value).ToArray();
            if (ordered.Length == 0)
            {
                return false;
            }

            if (value <= ordered[0].Value)
            {
                clamped = value < ordered[0].Value;
                baseRotation = ordered[0].BaseRotation;
                leftDelta = ordered[0].LeftDelta;
                rightDelta = ordered[0].RightDelta;
                return true;
            }

            if (value >= ordered[^1].Value)
            {
                clamped = value > ordered[^1].Value;
                baseRotation = ordered[^1].BaseRotation;
                leftDelta = ordered[^1].LeftDelta;
                rightDelta = ordered[^1].RightDelta;
                return true;
            }

            for (var i = 1; i < ordered.Length; i++)
            {
                var a = ordered[i - 1];
                var b = ordered[i];
                if (value > b.Value)
                {
                    continue;
                }

                var span = b.Value - a.Value;
                var t = span <= 0f ? 0f : (value - a.Value) / span;
                baseRotation = a.BaseRotation;
                leftDelta = Nlerp(a.LeftDelta, b.LeftDelta, t);
                rightDelta = Nlerp(a.RightDelta, b.RightDelta, t);
                return true;
            }

            return false;
        }

        private static JObject BuildArmTwistTimelineAxisFitSummary(
            JObject[] samples,
            Dictionary<string, List<FloatKey>> curves,
            JObject solver,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex)
        {
            var armTargets = BuildCurrentSolverTargets()
                .Where(x => string.Equals(x.HumanBone, "LeftLowerArm", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(x.HumanBone, "RightLowerArm", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var variants = BuildArmTwistTimelineVariants();
            var resultRows = new JArray();
            foreach (var variant in variants)
            {
                var accumulators = new Dictionary<string, TimelineTrackAccumulator>(StringComparer.OrdinalIgnoreCase);
                var missingTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var target in armTargets)
                {
                    if (!TryGetSolverTarget(solverNodes, solverAxes, humanBoneIndex, target, out var targetPath, out var targetAxis))
                    {
                        missingTargets.Add(target.HumanBone);
                        continue;
                    }

                    foreach (var sample in samples)
                    {
                        var time = (float?)sample["time"] ?? 0f;
                        var jointPaths = sample["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
                        var values = sample["values"]?.Values<float>().ToArray() ?? Array.Empty<float>();
                        if (!TryFindJointIndex(jointPaths, targetPath, out var jointIndex))
                        {
                            continue;
                        }

                        var offset = jointIndex * 7;
                        if (offset + 6 >= values.Length)
                        {
                            continue;
                        }

                        var unityRotation = Normalize(new[]
                        {
                            values[offset + 3],
                            values[offset + 4],
                            values[offset + 5],
                            values[offset + 6],
                        });
                        if (!TryBuildArmTwistTimelineVariantRotation(
                            curves,
                            target,
                            targetAxis,
                            time,
                            variant,
                            solver,
                            solverNodes,
                            solverAxes,
                            humanBoneIndex,
                            targetPath,
                            out var predicted))
                        {
                            missingTargets.Add(target.HumanBone);
                            continue;
                        }
                        if (!accumulators.TryGetValue(targetPath, out var accumulator))
                        {
                            accumulator = new TimelineTrackAccumulator(targetPath, targetPath, -1);
                            accumulators[targetPath] = accumulator;
                        }
                        accumulator.Add(QuaternionAngleDegrees(unityRotation, predicted));
                    }
                }

                var rows = accumulators.Values
                    .Select(x => x.ToRow())
                    .OrderByDescending(x => x.MaxRotationErrorDegrees)
                    .ThenBy(x => x.UnityPath, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                resultRows.Add(new JObject
                {
                    ["name"] = variant.Name,
                    ["description"] = variant.Description,
                    ["status"] = rows.Length == 0 ? "not_available" : "ok",
                    ["matchedTrackCount"] = rows.Length,
                    ["missingTargets"] = new JArray(missingTargets.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                    ["maxDegrees"] = rows.Length == 0 ? 0 : rows[0].MaxRotationErrorDegrees,
                    ["avgTrackMaxDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.MaxRotationErrorDegrees),
                    ["avgTrackAvgDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.AvgRotationErrorDegrees),
                    ["topErrors"] = ToJsonRows(rows.Take(8)),
                });
            }

            return new JObject
            {
                ["status"] = resultRows.Count == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: evaluates Arm Twist source-axis/target-axis, compose order, and side mirror choices on the full Unity GetInternalAvatarPose timeline. It keeps other forearm muscle inputs in place, because single-muscle probes can select a rule that worsens real animation.",
                ["rankingSummary"] = BuildArmTwistTimelineRankingSummary(resultRows),
                ["variants"] = new JArray(resultRows
                    .OfType<JObject>()
                    .OrderBy(x => (float?)x["avgTrackMaxDegrees"] ?? float.MaxValue)
                    .ThenBy(x => (float?)x["maxDegrees"] ?? float.MaxValue)
                    .ThenBy(x => (string)x["name"], StringComparer.OrdinalIgnoreCase)),
            };
        }

        private static JObject BuildArmTwistTimelineRankingSummary(JArray resultRows)
        {
            var rows = resultRows?
                .OfType<JObject>()
                .Where(x => string.Equals((string)x["status"], "ok", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => (float?)x["avgTrackMaxDegrees"] ?? float.MaxValue)
                .ThenBy(x => (float?)x["maxDegrees"] ?? float.MaxValue)
                .ThenBy(x => (string)x["name"], StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? Array.Empty<JObject>();
            if (rows.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                };
            }

            var current = rows.FirstOrDefault(x => string.Equals((string)x["name"], "current_lower_x_to_x", StringComparison.OrdinalIgnoreCase));
            var currentAvg = (float?)current?["avgTrackMaxDegrees"] ?? float.MaxValue;
            var currentMax = (float?)current?["maxDegrees"] ?? float.MaxValue;
            foreach (var row in rows)
            {
                var avg = (float?)row["avgTrackMaxDegrees"] ?? float.MaxValue;
                var max = (float?)row["maxDegrees"] ?? float.MaxValue;
                row["avgImprovementDegrees"] = current == null ? 0f : Math.Max(0f, currentAvg - avg);
                row["maxImprovementDegrees"] = current == null ? 0f : Math.Max(0f, currentMax - max);
                row["beatsCurrent"] = current != null &&
                    (avg < currentAvg - 0.5f ||
                     (avg <= currentAvg + 0.5f && max < currentMax - 0.5f));
            }

            var best = rows[0];
            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: ranks Arm Twist timeline variants. A candidate is only useful if it beats current on the full Unity timeline and then repeats across multiple models.",
                ["variantCount"] = rows.Length,
                ["current"] = current == null ? null : CloneArmTwistTimelineRow(current),
                ["best"] = CloneArmTwistTimelineRow(best),
                ["bestBeatsCurrent"] = current != null && (bool?)best["beatsCurrent"] == true,
                ["topUseful"] = new JArray(rows
                    .Where(x => (bool?)x["beatsCurrent"] == true)
                    .Take(12)
                    .Select(CloneArmTwistTimelineRow)),
                ["topRanked"] = new JArray(rows.Take(12).Select(CloneArmTwistTimelineRow)),
            };
        }

        private static JObject CloneArmTwistTimelineRow(JObject row)
        {
            return new JObject
            {
                ["name"] = (string)row?["name"],
                ["description"] = (string)row?["description"],
                ["maxDegrees"] = (float?)row?["maxDegrees"] ?? 0f,
                ["avgTrackMaxDegrees"] = (float?)row?["avgTrackMaxDegrees"] ?? 0f,
                ["avgTrackAvgDegrees"] = (float?)row?["avgTrackAvgDegrees"] ?? 0f,
                ["avgImprovementDegrees"] = (float?)row?["avgImprovementDegrees"] ?? 0f,
                ["maxImprovementDegrees"] = (float?)row?["maxImprovementDegrees"] ?? 0f,
                ["beatsCurrent"] = (bool?)row?["beatsCurrent"] ?? false,
            };
        }

        private static JObject BuildTransformLocalDeltaSolverComparison(
            JObject unityResult,
            Dictionary<string, List<FloatKey>> curves,
            JObject solver,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex,
            JObject[] gltfNodes,
            Dictionary<string, int> nodePathToIndex)
        {
            var snapshots = unityResult?["internalAvatarPoseSnapshots"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var before = FindSnapshot(snapshots, "unityTransformLocalPoseBeforeEditorCurveSetHumanPose");
            var after = FindSnapshot(snapshots, "unityTransformLocalPoseAfterEditorCurveSetHumanPose");
            if (before == null || after == null || solver == null || solverNodes == null || solverAxes == null || humanBoneIndex == null)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["hasBeforeTransformSnapshot"] = before != null,
                    ["hasAfterTransformSnapshot"] = after != null,
                    ["hasInternalSolver"] = solver != null,
                };
            }

            var beforePaths = before["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
            var afterPaths = after["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
            var beforeValues = ReadFloatArrayAll(before["values"] as JArray);
            var afterValues = ReadFloatArrayAll(after["values"] as JArray);
            var current = BuildSolverVariants().First(x => string.Equals(x.Name, "current_swing_twist", StringComparison.OrdinalIgnoreCase));
            var zeroCurves = new Dictionary<string, List<FloatKey>>(StringComparer.OrdinalIgnoreCase);
            var time = ResolveFirstEditorCurveSampleTime(unityResult);
            var rows = new List<JObject>();

            foreach (var target in BuildCurrentSolverTargets())
            {
                if (!TryGetSolverTarget(solverNodes, solverAxes, humanBoneIndex, target, out var targetPath, out var axis) ||
                    !TryReadSnapshotRotation(beforePaths, beforeValues, targetPath, out var beforeRotation) ||
                    !TryReadSnapshotRotation(afterPaths, afterValues, targetPath, out var afterRotation))
                {
                    continue;
                }

                var solved = BuildVariantSolverRotation(curves, target, axis, time, current, solver, solverNodes, humanBoneIndex, targetPath, gltfNodes, nodePathToIndex);
                var zeroSolved = BuildVariantSolverRotation(zeroCurves, target, axis, time, current, solver, solverNodes, humanBoneIndex, targetPath, gltfNodes, nodePathToIndex);
                var unityLeftDelta = Normalize(Multiply(afterRotation, Inverse(beforeRotation)));
                var unityRightDelta = Normalize(Multiply(Inverse(beforeRotation), afterRotation));
                var solverLeftDelta = Normalize(Multiply(solved, Inverse(zeroSolved)));
                var solverRightDelta = Normalize(Multiply(Inverse(zeroSolved), solved));
                rows.Add(new JObject
                {
                    ["humanBone"] = target.HumanBone,
                    ["path"] = targetPath,
                    ["bodyGroup"] = ClassifyBodyGroup(targetPath),
                    ["unityLeft_vs_solverLeft_degrees"] = QuaternionAngleDegrees(unityLeftDelta, solverLeftDelta),
                    ["unityRight_vs_solverRight_degrees"] = QuaternionAngleDegrees(unityRightDelta, solverRightDelta),
                    ["unityLeft_vs_solverRight_degrees"] = QuaternionAngleDegrees(unityLeftDelta, solverRightDelta),
                    ["unityRight_vs_solverLeft_degrees"] = QuaternionAngleDegrees(unityRightDelta, solverLeftDelta),
                    ["unityDeltaAngleDegrees"] = QuaternionAngleDegrees(IdentityQuaternion, unityLeftDelta),
                    ["solverDeltaAngleDegrees"] = QuaternionAngleDegrees(IdentityQuaternion, solverLeftDelta),
                });
            }

            var ordered = rows
                .OrderByDescending(x => (float?)x["unityLeft_vs_solverLeft_degrees"] ?? 0f)
                .ThenBy(x => (string)x["path"], StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new JObject
            {
                ["status"] = ordered.Length == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: compares Unity Transform.local delta caused by HumanPoseHandler.SetHumanPose against the current offline muscle solver delta. Low delta error means the dynamic muscle formula is correct and only the rest anchor is wrong; high delta error means the muscle-to-local-TRS formula itself is wrong.",
                ["time"] = time,
                ["matchedTrackCount"] = ordered.Length,
                ["leftDelta"] = BuildDeltaModeSummary(ordered, "unityLeft_vs_solverLeft_degrees"),
                ["rightDelta"] = BuildDeltaModeSummary(ordered, "unityRight_vs_solverRight_degrees"),
                ["crossLeftToRightDelta"] = BuildDeltaModeSummary(ordered, "unityLeft_vs_solverRight_degrees"),
                ["crossRightToLeftDelta"] = BuildDeltaModeSummary(ordered, "unityRight_vs_solverLeft_degrees"),
                ["topRows"] = new JArray(ordered.Take(32)),
            };
        }

        private static JObject BuildCrossSideZeroBaseDeltaFitSummary(
            JObject[] samples,
            Dictionary<string, List<FloatKey>> curves,
            JObject solver,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex,
            JObject[] gltfNodes,
            Dictionary<string, int> nodePathToIndex)
        {
            if (samples == null || samples.Length == 0 || solver == null || solverNodes == null || solverAxes == null || humanBoneIndex == null)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["timelineSampleCount"] = samples?.Length ?? 0,
                    ["hasInternalSolver"] = solver != null,
                };
            }

            var current = BuildSolverVariants().First(x => string.Equals(x.Name, "current_swing_twist", StringComparison.OrdinalIgnoreCase));
            var rows = new List<JObject>();
            foreach (var target in BuildCurrentSolverTargets())
            {
                if (!TryGetSolverTarget(solverNodes, solverAxes, humanBoneIndex, target, out var targetPath, out var axis))
                {
                    continue;
                }

                var baseA = ResolveCrossSideZeroBaseVariantPose(
                    solver,
                    solverNodes,
                    humanBoneIndex,
                    target.HumanBone,
                    targetPath,
                    "inverse(crossSideAvatarDefaultOppositeToTargetPose)*inverse(crossSideAvatarDefaultMirrorXYOppositePose)*avatarDefaultSameIndexParentLocalPose");
                var baseB = ResolveCrossSideZeroBaseVariantPose(
                    solver,
                    solverNodes,
                    humanBoneIndex,
                    target.HumanBone,
                    targetPath,
                    "inverse(crossSideAvatarDefaultTargetToOppositePose)*inverse(crossSideAvatarDefaultMirrorXYTargetToOppositePose)*humanSkeletonParentLocalPose");
                if (baseA == null && baseB == null)
                {
                    continue;
                }

                foreach (var sample in samples)
                {
                    var time = (float?)sample["time"] ?? 0f;
                    var jointPaths = sample["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
                    var values = sample["values"]?.Values<float>().ToArray() ?? Array.Empty<float>();
                    if (!TryFindJointIndex(jointPaths, targetPath, out var jointIndex))
                    {
                        continue;
                    }

                    var offset = jointIndex * 7;
                    if (offset + 6 >= values.Length)
                    {
                        continue;
                    }

                    var unityRotation = Normalize(new[]
                    {
                        values[offset + 3],
                        values[offset + 4],
                        values[offset + 5],
                        values[offset + 6],
                    });
                    var solved = BuildVariantSolverRotation(curves, target, axis, time, current, solver, solverNodes, humanBoneIndex, targetPath, gltfNodes, nodePathToIndex);
                    var currentZero = Normalize(Multiply(Normalize(axis.PreQ), Inverse(Normalize(axis.PostQ))));
                    var solverLeftDelta = Normalize(Multiply(solved, Inverse(currentZero)));
                    var solverRightDelta = Normalize(Multiply(Inverse(currentZero), solved));

                    if (baseA != null)
                    {
                        AddCrossSideZeroBaseDeltaFitRows(rows, target.HumanBone, targetPath, time, "A", unityRotation, baseA, solverLeftDelta, solverRightDelta);
                    }
                    if (baseB != null)
                    {
                        AddCrossSideZeroBaseDeltaFitRows(rows, target.HumanBone, targetPath, time, "B", unityRotation, baseB, solverLeftDelta, solverRightDelta);
                    }
                }
            }

            var ordered = rows
                .OrderByDescending(x => Math.Min(
                    Math.Min(JsonFloat(x, "unityLeft_vs_solverLeft_degrees"), JsonFloat(x, "unityLeft_vs_solverRight_degrees")),
                    Math.Min(JsonFloat(x, "unityRight_vs_solverLeft_degrees"), JsonFloat(x, "unityRight_vs_solverRight_degrees"))))
                .ThenBy(x => (string)x["path"], StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new JObject
            {
                ["status"] = ordered.Length == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: tests whether Unity timeline rotations, when expressed as deltas from the deterministic cross-side zero-base candidates, match the current solver's dynamic delta. Low delta error here means the base candidate is useful and the remaining problem is final composition; high error means the dynamic muscle delta is still in the wrong space.",
                ["rowCount"] = ordered.Length,
                ["baseA_leftDelta"] = BuildDeltaModeSummary(ordered.Where(x => string.Equals((string)x["baseCandidate"], "A", StringComparison.OrdinalIgnoreCase)).ToArray(), "unityLeft_vs_solverLeft_degrees"),
                ["baseA_rightDelta"] = BuildDeltaModeSummary(ordered.Where(x => string.Equals((string)x["baseCandidate"], "A", StringComparison.OrdinalIgnoreCase)).ToArray(), "unityRight_vs_solverRight_degrees"),
                ["baseA_crossLeftToRightDelta"] = BuildDeltaModeSummary(ordered.Where(x => string.Equals((string)x["baseCandidate"], "A", StringComparison.OrdinalIgnoreCase)).ToArray(), "unityLeft_vs_solverRight_degrees"),
                ["baseA_crossRightToLeftDelta"] = BuildDeltaModeSummary(ordered.Where(x => string.Equals((string)x["baseCandidate"], "A", StringComparison.OrdinalIgnoreCase)).ToArray(), "unityRight_vs_solverLeft_degrees"),
                ["baseB_leftDelta"] = BuildDeltaModeSummary(ordered.Where(x => string.Equals((string)x["baseCandidate"], "B", StringComparison.OrdinalIgnoreCase)).ToArray(), "unityLeft_vs_solverLeft_degrees"),
                ["baseB_rightDelta"] = BuildDeltaModeSummary(ordered.Where(x => string.Equals((string)x["baseCandidate"], "B", StringComparison.OrdinalIgnoreCase)).ToArray(), "unityRight_vs_solverRight_degrees"),
                ["baseB_crossLeftToRightDelta"] = BuildDeltaModeSummary(ordered.Where(x => string.Equals((string)x["baseCandidate"], "B", StringComparison.OrdinalIgnoreCase)).ToArray(), "unityLeft_vs_solverRight_degrees"),
                ["baseB_crossRightToLeftDelta"] = BuildDeltaModeSummary(ordered.Where(x => string.Equals((string)x["baseCandidate"], "B", StringComparison.OrdinalIgnoreCase)).ToArray(), "unityRight_vs_solverLeft_degrees"),
                ["topRows"] = new JArray(ordered.Take(64)),
            };
        }

        private static void AddCrossSideZeroBaseDeltaFitRows(
            List<JObject> rows,
            string humanBone,
            string targetPath,
            float time,
            string baseCandidate,
            float[] unityRotation,
            float[] baseRotation,
            float[] solverLeftDelta,
            float[] solverRightDelta)
        {
            var unityLeftDelta = Normalize(Multiply(unityRotation, Inverse(baseRotation)));
            var unityRightDelta = Normalize(Multiply(Inverse(baseRotation), unityRotation));
            rows.Add(new JObject
            {
                ["humanBone"] = humanBone,
                ["path"] = targetPath,
                ["bodyGroup"] = ClassifyBodyGroup(targetPath),
                ["time"] = time,
                ["baseCandidate"] = baseCandidate,
                ["unityLeft_vs_solverLeft_degrees"] = QuaternionAngleDegrees(unityLeftDelta, solverLeftDelta),
                ["unityRight_vs_solverRight_degrees"] = QuaternionAngleDegrees(unityRightDelta, solverRightDelta),
                ["unityLeft_vs_solverRight_degrees"] = QuaternionAngleDegrees(unityLeftDelta, solverRightDelta),
                ["unityRight_vs_solverLeft_degrees"] = QuaternionAngleDegrees(unityRightDelta, solverLeftDelta),
                ["unityLeftDeltaAngleDegrees"] = QuaternionAngleDegrees(IdentityQuaternion, unityLeftDelta),
                ["unityRightDeltaAngleDegrees"] = QuaternionAngleDegrees(IdentityQuaternion, unityRightDelta),
                ["solverDeltaAngleDegrees"] = QuaternionAngleDegrees(IdentityQuaternion, solverLeftDelta),
            });
        }

        private static JObject BuildDeltaModeSummary(JObject[] rows, string field)
        {
            var values = (rows ?? Array.Empty<JObject>())
                .Select(x => (float?)x[field] ?? 0f)
                .ToArray();
            var bodyGroups = (rows ?? Array.Empty<JObject>())
                .GroupBy(x => (string)x["bodyGroup"] ?? "Unknown", StringComparer.OrdinalIgnoreCase)
                .Select(g => new JObject
                {
                    ["bodyGroup"] = g.Key,
                    ["trackCount"] = g.Count(),
                    ["maxDegrees"] = g.Max(x => (float?)x[field] ?? 0f),
                    ["avgDegrees"] = g.Average(x => (float?)x[field] ?? 0f),
                })
                .OrderByDescending(x => (float?)x["maxDegrees"] ?? 0f)
                .ThenBy(x => (string)x["bodyGroup"], StringComparer.OrdinalIgnoreCase);
            return new JObject
            {
                ["field"] = field,
                ["maxDegrees"] = values.Length == 0 ? 0 : values.Max(),
                ["avgDegrees"] = values.Length == 0 ? 0 : values.Average(),
                ["p90Degrees"] = Percentile(values, 0.9f),
                ["bodyGroupError"] = new JArray(bodyGroups),
            };
        }

        private static bool TryReadSnapshotRotation(string[] paths, float[] values, string targetPath, out float[] rotation)
        {
            rotation = null;
            if (!TryFindJointIndex(paths, targetPath, out var index))
            {
                return false;
            }

            var offset = index * 7;
            if (values == null || offset + 6 >= values.Length)
            {
                return false;
            }

            rotation = Normalize(new[]
            {
                values[offset + 3],
                values[offset + 4],
                values[offset + 5],
                values[offset + 6],
            });
            return true;
        }

        private static ArmTwistTimelineVariant[] BuildArmTwistTimelineVariants()
        {
            var variants = new List<ArmTwistTimelineVariant>
            {
                new("current_lower_x_to_x", "Current production rule for Arm Twist: target lower-arm X/twist limit written to lower-arm X/twist.", "lower", 0, 0, "swing_twist", null),
                new("oracle_pattern_arm_twist_local_delta_left", "Diagnostic only: Arm Twist uses the cross-model Unity oracle off-axis pattern as a local left-multiplied delta. Do not migrate to production; derive the same tilt from AvatarConstant first.", "lower", 0, 0, "oracle_pattern_local_delta_left", null),
                new("oracle_pattern_arm_twist_local_delta_right", "Diagnostic only: Arm Twist uses the cross-model Unity oracle off-axis pattern as a local right-multiplied delta. Do not migrate to production; derive the same tilt from AvatarConstant first.", "lower", 0, 0, "oracle_pattern_local_delta_right", null),
                new("pose_axis_arm_twist_avatarDefaultParentLocal_left_delta", "Diagnostic only: Arm Twist uses avatarDefaultSameIndexParentLocalPose to tilt the signed lower-arm twist axis as a local left-multiplied delta.", "lower", 0, 0, "pose_axis_local_delta_left", null, "avatarDefaultSameIndexParentLocalPose"),
                new("pose_axis_arm_twist_avatarDefaultParentToChild_left_delta", "Diagnostic only: Arm Twist uses avatarDefaultSameIndexParentToChildPose to tilt the signed lower-arm twist axis as a local left-multiplied delta.", "lower", 0, 0, "pose_axis_local_delta_left", null, "avatarDefaultSameIndexParentToChildPose"),
                new("pose_axis_arm_twist_inverseAvatarDefaultSameIndex_left_delta", "Diagnostic only: Arm Twist uses inverse(avatarDefaultPoseBySameIndex) to tilt the signed lower-arm twist axis as a local left-multiplied delta.", "lower", 0, 0, "pose_axis_local_delta_left", null, "inverse(avatarDefaultPoseBySameIndex)"),
                new("readiness_arm_twist_left_z_right_x_to_y_swing_twist", "Diagnostic only: readiness-driven Arm Twist probe. Left lower-arm uses source Z/swing -> target Y/swing; right lower-arm uses source X/twist -> target Y/swing; compose=swing_twist.", "lower", 0, 1, "readiness_left_z_right_x_to_y_swing_twist", null),
                new("readiness_arm_twist_left_z_right_x_to_y_twist_swing", "Diagnostic only: readiness-driven Arm Twist probe. Left lower-arm uses source Z/swing -> target Y/swing; right lower-arm uses source X/twist -> target Y/swing; compose=twist_swing.", "lower", 0, 1, "readiness_left_z_right_x_to_y_twist_swing", null),
            };
            foreach (var sourceKind in new[] { "lower", "upper" })
            {
                foreach (var formulaName in new[] { "inversePost_middle_post", "post_middle_inversePost", "zero_preProjectedDelta", "postProjectedDelta_zero" })
                {
                    foreach (var deltaSide in new[] { "left", "right" })
                    {
                        variants.Add(new ArmTwistTimelineVariant(
                            $"single_formula_{deltaSide}_{formulaName}_{sourceKind}_x_to_x",
                            $"Diagnostic only: Arm Twist computes the single-muscle {formulaName} delta from AvatarConstant, then applies its {deltaSide} delta to the lower-arm stretch base. This tests why single-muscle fit is near-zero but full timeline is still wrong.",
                            sourceKind,
                            0,
                            0,
                            $"single_formula_{deltaSide}_{formulaName}",
                            null));
                    }
                }
            }
            foreach (var sourceKind in new[] { "lower", "upper" })
            {
                foreach (var stretchFormula in new[] { "pre_middle_inversePost", "inversePost_middle_post" })
                {
                    foreach (var twistFormula in new[] { "inversePost_middle_post", "post_middle_inversePost" })
                    {
                        foreach (var deltaSide in new[] { "left", "right" })
                        {
                            foreach (var orderMode in new[] { "append", "prepend" })
                            {
                                variants.Add(new ArmTwistTimelineVariant(
                                    $"single_formula_zero_{deltaSide}_{orderMode}_{stretchFormula}__{twistFormula}_{sourceKind}_x_to_x",
                                    $"Diagnostic only: compose Forearm Stretch and Arm Twist as single-muscle {deltaSide} deltas from zero pose, order={orderMode}, stretchFormula={stretchFormula}, twistFormula={twistFormula}. This mirrors the Unity oracle lookup rebuild without using Unity probe values.",
                                    sourceKind,
                                    0,
                                    0,
                                    $"single_formula_zero_{deltaSide}_{orderMode}_{stretchFormula}__{twistFormula}",
                                    null));
                            }
                        }
                    }
                }
            }
            foreach (var sourceKind in new[] { "lower", "upper" })
            {
                foreach (var stretchFormula in new[] { "pre_middle_inversePost", "inversePost_middle_post", "preProjectedDelta_zero" })
                {
                    foreach (var twistFormula in new[] { "inversePost_middle_post", "post_middle_inversePost", "postProjectedDelta_zero", "preProjectedDelta_zero" })
                    {
                        foreach (var outputSide in new[] { "left", "right" })
                        {
                            foreach (var orderMode in new[] { "append", "prepend" })
                            {
                                foreach (var stretchSide in new[] { "left", "right" })
                                {
                                    foreach (var twistSide in new[] { "left", "right" })
                                    {
                                        variants.Add(new ArmTwistTimelineVariant(
                                            $"single_formula_mixed_{outputSide}_{orderMode}_stretch{stretchSide}_twist{twistSide}_{stretchFormula}__{twistFormula}_{sourceKind}_x_to_x",
                                            $"Diagnostic only: compose Forearm Stretch and Arm Twist with independent predicted delta sides. output={outputSide}, order={orderMode}, stretchSide={stretchSide}, twistSide={twistSide}, stretchFormula={stretchFormula}, twistFormula={twistFormula}.",
                                            sourceKind,
                                            0,
                                            0,
                                            $"single_formula_mixed_{outputSide}_{orderMode}_stretch{stretchSide}_twist{twistSide}_{stretchFormula}__{twistFormula}",
                                            null));
                                    }
                                }
                            }
                        }
                    }
                }
            }
            foreach (var sourceKind in new[] { "lower", "upper" })
            {
                for (var sourceAxis = 0; sourceAxis < 3; sourceAxis++)
                {
                    for (var targetAxis = 0; targetAxis < 3; targetAxis++)
                    {
                        foreach (var composeMode in new[] { "swing_twist", "twist_swing" })
                        {
                            foreach (var mirrorMode in new string[] { null, "mirror_xy_left", "mirror_xy_right", "mirror_xy_both" })
                            {
                                var composeSuffix = string.Equals(composeMode, "swing_twist", StringComparison.OrdinalIgnoreCase) ? null : "_twist_first";
                                var mirrorSuffix = string.IsNullOrWhiteSpace(mirrorMode) ? null : $"_{mirrorMode}";
                                var name = $"{sourceKind}_{AvatarAxisShortName(sourceAxis)}_to_{AvatarAxisShortName(targetAxis)}{composeSuffix}{mirrorSuffix}";
                                if (variants.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                                {
                                    continue;
                                }

                                variants.Add(new ArmTwistTimelineVariant(
                                    name,
                                    $"Arm Twist diagnostic: use {sourceKind} arm Avatar {AvatarAxisName(sourceAxis)} limit, write to lower-arm {AvatarAxisName(targetAxis)}, compose={composeMode}, mirror={mirrorMode ?? "none"}.",
                                    sourceKind,
                                    sourceAxis,
                                    targetAxis,
                                    composeMode,
                                    mirrorMode));
                            }
                        }
                    }
                }
            }
            return variants.ToArray();
        }

        private static JObject BuildHandTwistTimelineAxisFitSummary(
            JObject[] samples,
            Dictionary<string, List<FloatKey>> curves,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex)
        {
            var handTargets = BuildCurrentSolverTargets()
                .Where(x => string.Equals(x.HumanBone, "LeftHand", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(x.HumanBone, "RightHand", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var variants = BuildHandTwistTimelineVariants();
            var resultRows = new JArray();
            foreach (var variant in variants)
            {
                var accumulators = new Dictionary<string, TimelineTrackAccumulator>(StringComparer.OrdinalIgnoreCase);
                var missingTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var target in handTargets)
                {
                    if (!TryGetSolverTarget(solverNodes, solverAxes, humanBoneIndex, target, out var targetPath, out var targetAxis))
                    {
                        missingTargets.Add(target.HumanBone);
                        continue;
                    }

                    foreach (var sample in samples)
                    {
                        var time = (float?)sample["time"] ?? 0f;
                        var jointPaths = sample["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
                        var values = sample["values"]?.Values<float>().ToArray() ?? Array.Empty<float>();
                        if (!TryFindJointIndex(jointPaths, targetPath, out var jointIndex))
                        {
                            continue;
                        }

                        var offset = jointIndex * 7;
                        if (offset + 6 >= values.Length)
                        {
                            continue;
                        }

                        var unityRotation = Normalize(new[]
                        {
                            values[offset + 3],
                            values[offset + 4],
                            values[offset + 5],
                            values[offset + 6],
                        });
                        var predicted = BuildHandTwistTimelineVariantRotation(
                            curves,
                            target,
                            targetAxis,
                            time,
                            variant,
                            solverNodes,
                            solverAxes,
                            humanBoneIndex);
                        if (!accumulators.TryGetValue(targetPath, out var accumulator))
                        {
                            accumulator = new TimelineTrackAccumulator(targetPath, targetPath, -1);
                            accumulators[targetPath] = accumulator;
                        }
                        accumulator.Add(QuaternionAngleDegrees(unityRotation, predicted));
                    }
                }

                var rows = accumulators.Values
                    .Select(x => x.ToRow())
                    .OrderByDescending(x => x.MaxRotationErrorDegrees)
                    .ThenBy(x => x.UnityPath, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                resultRows.Add(new JObject
                {
                    ["name"] = variant.Name,
                    ["description"] = variant.Description,
                    ["status"] = rows.Length == 0 ? "not_available" : "ok",
                    ["matchedTrackCount"] = rows.Length,
                    ["missingTargets"] = new JArray(missingTargets.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                    ["maxDegrees"] = rows.Length == 0 ? 0 : rows[0].MaxRotationErrorDegrees,
                    ["avgTrackMaxDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.MaxRotationErrorDegrees),
                    ["avgTrackAvgDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.AvgRotationErrorDegrees),
                    ["topErrors"] = ToJsonRows(rows.Take(8)),
                });
            }

            return new JObject
            {
                ["status"] = resultRows.Count == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: evaluates Forearm Twist source-axis/target-axis choices on Hand transforms over the full Unity GetInternalAvatarPose timeline. This targets the visible wrist/hand flipping that remains after static rest correction.",
                ["variants"] = new JArray(resultRows
                    .OfType<JObject>()
                    .OrderBy(x => (float?)x["avgTrackMaxDegrees"] ?? float.MaxValue)
                    .ThenBy(x => (float?)x["maxDegrees"] ?? float.MaxValue)
                    .ThenBy(x => (string)x["name"], StringComparer.OrdinalIgnoreCase)),
            };
        }

        private static ArmTwistTimelineVariant[] BuildHandTwistTimelineVariants()
        {
            var variants = new List<ArmTwistTimelineVariant>
            {
                new("current_forearm_x_to_x", "Current production rule for Forearm Twist on Hand: source forearm X/twist limit written to hand X/twist.", "forearm", 0, 0),
                new("legacy_hand_x_to_x", "Legacy diagnostic rule: target hand X/twist limit written to hand X/twist. Kept only to compare old previews against the current production rule.", "hand", 0, 0),
            };
            foreach (var sourceKind in new[] { "hand", "forearm" })
            {
                for (var sourceAxis = 0; sourceAxis < 3; sourceAxis++)
                {
                    for (var targetAxis = 0; targetAxis < 3; targetAxis++)
                    {
                        var name = $"{sourceKind}_{AvatarAxisShortName(sourceAxis)}_to_{AvatarAxisShortName(targetAxis)}";
                        if (variants.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }
                        variants.Add(new ArmTwistTimelineVariant(
                            name,
                            $"Forearm Twist diagnostic: use {sourceKind} Avatar {AvatarAxisName(sourceAxis)} limit and write to hand {AvatarAxisName(targetAxis)}.",
                            sourceKind,
                            sourceAxis,
                            targetAxis));
                    }
                }
            }
            return variants.ToArray();
        }

        private static JObject BuildUpperArmSwingTimelineFitSummary(
            JObject[] samples,
            Dictionary<string, List<FloatKey>> curves,
            JObject solver,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex)
        {
            var upperArmTargets = BuildCurrentSolverTargets()
                .Where(x => string.Equals(x.HumanBone, "LeftUpperArm", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(x.HumanBone, "RightUpperArm", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var variants = BuildUpperArmSwingTimelineVariants();
            var resultRows = new JArray();
            foreach (var variant in variants)
            {
                var accumulators = new Dictionary<string, TimelineTrackAccumulator>(StringComparer.OrdinalIgnoreCase);
                var missingTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var target in upperArmTargets)
                {
                    if (!TryGetSolverTarget(solverNodes, solverAxes, humanBoneIndex, target, out var targetPath, out var targetAxis))
                    {
                        missingTargets.Add(target.HumanBone);
                        continue;
                    }

                    foreach (var sample in samples)
                    {
                        var time = (float?)sample["time"] ?? 0f;
                        var jointPaths = sample["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
                        var values = sample["values"]?.Values<float>().ToArray() ?? Array.Empty<float>();
                        if (!TryFindJointIndex(jointPaths, targetPath, out var jointIndex))
                        {
                            continue;
                        }

                        var offset = jointIndex * 7;
                        if (offset + 6 >= values.Length)
                        {
                            continue;
                        }

                        var unityRotation = Normalize(new[]
                        {
                            values[offset + 3],
                            values[offset + 4],
                            values[offset + 5],
                            values[offset + 6],
                        });
                        if (!TryBuildUpperArmSwingTimelineVariantRotation(curves, target, targetAxis, time, variant, solver, solverNodes, humanBoneIndex, targetPath, out var predicted))
                        {
                            missingTargets.Add(target.HumanBone);
                            continue;
                        }
                        if (!accumulators.TryGetValue(targetPath, out var accumulator))
                        {
                            accumulator = new TimelineTrackAccumulator(targetPath, targetPath, -1);
                            accumulators[targetPath] = accumulator;
                        }
                        accumulator.Add(QuaternionAngleDegrees(unityRotation, predicted));
                    }
                }

                var rows = accumulators.Values
                    .Select(x => x.ToRow())
                    .OrderByDescending(x => x.MaxRotationErrorDegrees)
                    .ThenBy(x => x.UnityPath, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                resultRows.Add(new JObject
                {
                    ["name"] = variant.Name,
                    ["description"] = variant.Description,
                    ["status"] = rows.Length == 0 ? "not_available" : "ok",
                    ["matchedTrackCount"] = rows.Length,
                    ["missingTargets"] = new JArray(missingTargets.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                    ["xTargetAxis"] = AvatarAxisName(variant.XTargetAxis),
                    ["yTargetAxis"] = AvatarAxisName(variant.YTargetAxis),
                    ["xSign"] = variant.XSign,
                    ["ySign"] = variant.YSign,
                    ["swingMode"] = variant.SwingMode,
                    ["composeMode"] = variant.ComposeMode,
                    ["maxDegrees"] = rows.Length == 0 ? 0 : rows[0].MaxRotationErrorDegrees,
                    ["avgTrackMaxDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.MaxRotationErrorDegrees),
                    ["avgTrackAvgDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.AvgRotationErrorDegrees),
                    ["topErrors"] = ToJsonRows(rows.Take(8)),
                });
            }

            return new JObject
            {
                ["status"] = resultRows.Count == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: evaluates upper-arm Down-Up / Front-Back muscle axis, sign, and swing composition choices on the full Unity GetInternalAvatarPose timeline. Full timeline ranking is used because one-muscle probes can overfit isolated rotations.",
                ["variants"] = new JArray(resultRows
                    .OfType<JObject>()
                    .OrderBy(x => (float?)x["avgTrackMaxDegrees"] ?? float.MaxValue)
                    .ThenBy(x => (float?)x["maxDegrees"] ?? float.MaxValue)
                    .ThenBy(x => (string)x["name"], StringComparer.OrdinalIgnoreCase)),
            };
        }

        private static UpperArmSwingTimelineVariant[] BuildUpperArmSwingTimelineVariants()
        {
            var variants = new List<UpperArmSwingTimelineVariant>
            {
                new(
                    "current_x_z_y_y_swing_twist",
                    "Current production rule for upper arm: Down-Up writes Avatar Z, Front-Back writes Avatar Y, swing-vector then twist.",
                    2,
                    1,
                    1,
                    1,
                    "vector",
                    "swing_twist"),
                new(
                    "oracle_pattern_frontback_local_delta_left",
                    "Diagnostic only: UpperArm Front-Back uses the cross-model Unity oracle off-axis pattern as a local left-multiplied delta while keeping Down-Up current. Do not migrate to production; derive the same tilt from AvatarConstant first.",
                    2,
                    1,
                    1,
                    1,
                    "oracle_pattern_frontback_local_delta_left",
                    "swing_twist"),
                new(
                    "oracle_pattern_frontback_local_delta_right",
                    "Diagnostic only: UpperArm Front-Back uses the cross-model Unity oracle off-axis pattern as a local right-multiplied delta while keeping Down-Up current. Do not migrate to production; derive the same tilt from AvatarConstant first.",
                    2,
                    1,
                    1,
                    1,
                    "oracle_pattern_frontback_local_delta_right",
                    "swing_twist"),
                new(
                    "pose_axis_frontback_humanSkeletonChildToParent_left_delta",
                    "Diagnostic only: UpperArm Front-Back uses AvatarConstant humanSkeleton child-to-parent pose axis as a local left-multiplied delta while keeping Down-Up current. This is deterministic metadata, but still must pass cross-model full-timeline gates before production.",
                    2,
                    1,
                    1,
                    1,
                    "pose_axis_frontback",
                    "swing_twist",
                    "humanSkeletonChildToParentPose",
                    "left_delta"),
            };

            foreach (var xAxis in new[] { 0, 1, 2 })
            {
                foreach (var yAxis in new[] { 0, 1, 2 })
                {
                    if (xAxis == yAxis)
                    {
                        continue;
                    }
                    foreach (var xSign in new[] { 1, -1 })
                    {
                        foreach (var ySign in new[] { 1, -1 })
                        {
                            foreach (var swingMode in new[] { "vector", "y_then_z", "z_then_y", "ellipse_clamped", "ellipse_scaled" })
                            {
                                foreach (var composeMode in new[] { "swing_twist", "twist_swing" })
                                {
                                    var name = $"x_{AvatarAxisShortName(xAxis)}_{SignName(xSign)}__y_{AvatarAxisShortName(yAxis)}_{SignName(ySign)}__{swingMode}__{composeMode}";
                                    if (variants.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        continue;
                                    }
                                    variants.Add(new UpperArmSwingTimelineVariant(
                                        name,
                                        $"Upper arm diagnostic: Down-Up writes {AvatarAxisName(xAxis)} ({SignName(xSign)}), Front-Back writes {AvatarAxisName(yAxis)} ({SignName(ySign)}), swingMode={swingMode}, composeMode={composeMode}.",
                                        xAxis,
                                        yAxis,
                                        xSign,
                                        ySign,
                                        swingMode,
                                        composeMode));
                                }
                            }
                        }
                    }
                }
            }

            return variants.ToArray();
        }

        private static float[] BuildUpperArmSwingTimelineVariantRotation(
            Dictionary<string, List<FloatKey>> curves,
            SolverTarget target,
            SolverAxis axis,
            float time,
            UpperArmSwingTimelineVariant variant)
        {
            if (variant.SwingMode?.StartsWith("oracle_pattern_frontback_", StringComparison.OrdinalIgnoreCase) == true)
            {
                return BuildOraclePatternUpperArmFrontBackRotation(curves, target, axis, time, variant);
            }

            var angles = new float[3];
            var downUp = SampleFloatCurve(curves.TryGetValue(target.XAttribute ?? string.Empty, out var xCurve) ? xCurve : null, time);
            var frontBack = SampleFloatCurve(curves.TryGetValue(target.YAttribute ?? string.Empty, out var yCurve) ? yCurve : null, time);
            angles[variant.XTargetAxis] += LimitMuscle(downUp * variant.XSign, axis, variant.XTargetAxis);
            angles[variant.YTargetAxis] += LimitMuscle(frontBack * variant.YSign, axis, variant.YTargetAxis);

            var pre = Normalize(axis.PreQ);
            var post = Normalize(axis.PostQ);
            var swingVariant = new SolverVariant(
                variant.Name,
                variant.Description,
                Array.Empty<SolverTarget>(),
                variant.ComposeMode,
                SwingMode: variant.SwingMode == "vector" ? null : variant.SwingMode);
            var swing = BuildSwingQuaternion(angles, axis, swingVariant);
            var twist = AxisAngleRadiansToQuaternion(1, 0, 0, angles[0]);
            var middle = string.Equals(variant.ComposeMode, "twist_swing", StringComparison.OrdinalIgnoreCase)
                ? Multiply(twist, swing)
                : Multiply(swing, twist);
            return Normalize(Multiply(Multiply(pre, middle), Inverse(post)));
        }

        private static bool TryBuildUpperArmSwingTimelineVariantRotation(
            Dictionary<string, List<FloatKey>> curves,
            SolverTarget target,
            SolverAxis axis,
            float time,
            UpperArmSwingTimelineVariant variant,
            JObject solver,
            JArray solverNodes,
            JArray humanBoneIndex,
            string targetPath,
            out float[] rotation)
        {
            rotation = null;
            if (variant.SwingMode?.StartsWith("pose_axis_frontback", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (solver == null ||
                    string.IsNullOrWhiteSpace(variant.FrontBackAxisCandidate) ||
                    string.IsNullOrWhiteSpace(variant.FrontBackApplyMode) ||
                    !TryResolveUpperArmFrontBackPoseAxis(variant.FrontBackAxisCandidate, target, targetPath, solver, solverNodes, humanBoneIndex, out var frontBackAxis))
                {
                    return false;
                }

                rotation = BuildUpperArmFrontBackPoseAxisRotation(curves, target, axis, time, frontBackAxis, variant.FrontBackApplyMode);
                return true;
            }

            rotation = BuildUpperArmSwingTimelineVariantRotation(curves, target, axis, time, variant);
            return true;
        }

        private static float[] BuildOraclePatternUpperArmFrontBackRotation(
            Dictionary<string, List<FloatKey>> curves,
            SolverTarget target,
            SolverAxis axis,
            float time,
            UpperArmSwingTimelineVariant variant)
        {
            var downUp = SampleFloatCurve(curves.TryGetValue(target.XAttribute ?? string.Empty, out var xCurve) ? xCurve : null, time);
            var frontBack = SampleFloatCurve(curves.TryGetValue(target.YAttribute ?? string.Empty, out var yCurve) ? yCurve : null, time);
            var baseAngles = new float[3];
            baseAngles[variant.XTargetAxis] += LimitMuscle(downUp * variant.XSign, axis, variant.XTargetAxis);

            var pre = Normalize(axis.PreQ);
            var post = Normalize(axis.PostQ);
            var baseSwing = BuildSwingQuaternion(baseAngles, axis, new SolverVariant(
                variant.Name,
                variant.Description,
                Array.Empty<SolverTarget>(),
                "swing_twist"));
            var baseRotation = Normalize(Multiply(Multiply(pre, baseSwing), Inverse(post)));
            if (MathF.Abs(frontBack) <= 0.0000001f)
            {
                return baseRotation;
            }

            // 诊断用常量来自 NPC/Gorou Unity oracle：Front-Back 最近轴仍是 -Z，
            // 但左右手分别带稳定的 +/-X 倾斜。这里故意不进生产，只验证缺口形态。
            var isLeft = target.HumanBone.StartsWith("Left", StringComparison.OrdinalIgnoreCase);
            var patternAxis = NormalizeVector3(new[]
            {
                isLeft ? 0.302086f : -0.288938f,
                0f,
                -0.956282f,
            });
            var angle = LimitMuscle(frontBack * variant.YSign, axis, variant.YTargetAxis);
            var delta = AxisAngleRadiansToQuaternion(patternAxis[0], patternAxis[1], patternAxis[2], angle);
            return variant.SwingMode.EndsWith("_right", StringComparison.OrdinalIgnoreCase)
                ? Normalize(Multiply(baseRotation, delta))
                : Normalize(Multiply(delta, baseRotation));
        }

        private static JObject BuildUpperArmFrontBackPoseAxisTimelineFitSummary(
            JObject[] samples,
            Dictionary<string, List<FloatKey>> curves,
            JObject solver,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex)
        {
            var upperArmTargets = BuildCurrentSolverTargets()
                .Where(x => string.Equals(x.HumanBone, "LeftUpperArm", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(x.HumanBone, "RightUpperArm", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var candidateNames = BuildUpperArmFrontBackPoseAxisCandidateNames(upperArmTargets, solver, solverNodes, solverAxes, humanBoneIndex)
                .Concat(new[] { "current_nearest_minus_z", "oracle_pattern_frontback" })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var resultRows = new JArray();
            foreach (var candidateName in candidateNames)
            {
                foreach (var applyMode in new[] { "left_delta", "right_delta" })
                {
                    var accumulators = new Dictionary<string, TimelineTrackAccumulator>(StringComparer.OrdinalIgnoreCase);
                    var missingTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var target in upperArmTargets)
                    {
                        if (!TryGetSolverTarget(solverNodes, solverAxes, humanBoneIndex, target, out var targetPath, out var axis) ||
                            !TryResolveUpperArmFrontBackPoseAxis(candidateName, target, targetPath, solver, solverNodes, humanBoneIndex, out var frontBackAxis))
                        {
                            missingTargets.Add(target.HumanBone);
                            continue;
                        }

                        foreach (var sample in samples)
                        {
                            var time = (float?)sample["time"] ?? 0f;
                            var jointPaths = sample["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
                            var values = sample["values"]?.Values<float>().ToArray() ?? Array.Empty<float>();
                            if (!TryFindJointIndex(jointPaths, targetPath, out var jointIndex))
                            {
                                continue;
                            }

                            var offset = jointIndex * 7;
                            if (offset + 6 >= values.Length)
                            {
                                continue;
                            }

                            var unityRotation = Normalize(new[]
                            {
                                values[offset + 3],
                                values[offset + 4],
                                values[offset + 5],
                                values[offset + 6],
                            });
                            var predicted = BuildUpperArmFrontBackPoseAxisRotation(curves, target, axis, time, frontBackAxis, applyMode);
                            if (!accumulators.TryGetValue(targetPath, out var accumulator))
                            {
                                accumulator = new TimelineTrackAccumulator(targetPath, targetPath, -1);
                                accumulators[targetPath] = accumulator;
                            }
                            accumulator.Add(QuaternionAngleDegrees(unityRotation, predicted));
                        }
                    }

                    var rows = accumulators.Values
                        .Select(x => x.ToRow())
                        .OrderByDescending(x => x.MaxRotationErrorDegrees)
                        .ThenBy(x => x.UnityPath, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    resultRows.Add(new JObject
                    {
                        ["name"] = $"{candidateName}__{applyMode}",
                        ["candidate"] = candidateName,
                        ["applyMode"] = applyMode,
                        ["status"] = rows.Length == 0 ? "not_available" : "ok",
                        ["matchedTrackCount"] = rows.Length,
                        ["missingTargets"] = new JArray(missingTargets.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                        ["maxDegrees"] = rows.Length == 0 ? 0 : rows[0].MaxRotationErrorDegrees,
                        ["avgTrackMaxDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.MaxRotationErrorDegrees),
                        ["avgTrackAvgDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.AvgRotationErrorDegrees),
                        ["topErrors"] = ToJsonRows(rows.Take(8)),
                    });
                }
            }

            var ordered = resultRows
                .OfType<JObject>()
                .Where(x => string.Equals((string)x["status"], "ok", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => (float?)x["avgTrackMaxDegrees"] ?? float.MaxValue)
                .ThenBy(x => (float?)x["maxDegrees"] ?? float.MaxValue)
                .ThenBy(x => (string)x["name"], StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var current = ordered.FirstOrDefault(x => string.Equals((string)x["name"], "current_nearest_minus_z__left_delta", StringComparison.OrdinalIgnoreCase));
            var currentAvg = (float?)current?["avgTrackMaxDegrees"] ?? float.MaxValue;
            foreach (var row in ordered)
            {
                var avg = (float?)row["avgTrackMaxDegrees"] ?? float.MaxValue;
                row["avgImprovementDegrees"] = current == null ? 0f : Math.Max(0f, currentAvg - avg);
                row["beatsCurrent"] = current != null && avg < currentAvg - 0.5f;
            }

            return new JObject
            {
                ["status"] = ordered.Length == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: evaluates whether UpperArm Front-Back off-axis tilt can be explained by AvatarConstant pose/local/parent-child pose candidates. It must not be migrated unless the winning candidate is stable across models and full timelines.",
                ["candidateCount"] = candidateNames.Length,
                ["current"] = current == null ? null : CloneUpperArmFrontBackPoseAxisRow(current),
                ["best"] = ordered.Length == 0 ? null : CloneUpperArmFrontBackPoseAxisRow(ordered[0]),
                ["bestBeatsCurrent"] = ordered.Length > 0 && (bool?)ordered[0]["beatsCurrent"] == true,
                ["topUseful"] = new JArray(ordered.Where(x => (bool?)x["beatsCurrent"] == true).Take(12).Select(CloneUpperArmFrontBackPoseAxisRow)),
                ["topRanked"] = new JArray(ordered.Take(16).Select(CloneUpperArmFrontBackPoseAxisRow)),
                ["variants"] = new JArray(ordered.Select(CloneUpperArmFrontBackPoseAxisRow)),
            };
        }

        private static JObject CloneUpperArmFrontBackPoseAxisRow(JObject row)
        {
            if (row == null)
            {
                return null;
            }

            return new JObject
            {
                ["name"] = (string)row["name"],
                ["candidate"] = (string)row["candidate"],
                ["applyMode"] = (string)row["applyMode"],
                ["maxDegrees"] = JsonFloat(row, "maxDegrees"),
                ["avgTrackMaxDegrees"] = JsonFloat(row, "avgTrackMaxDegrees"),
                ["avgTrackAvgDegrees"] = JsonFloat(row, "avgTrackAvgDegrees"),
                ["avgImprovementDegrees"] = JsonFloat(row, "avgImprovementDegrees"),
                ["beatsCurrent"] = (bool?)row["beatsCurrent"] ?? false,
            };
        }

        private static IEnumerable<string> BuildUpperArmFrontBackPoseAxisCandidateNames(
            IEnumerable<SolverTarget> targets,
            JObject solver,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in targets ?? Enumerable.Empty<SolverTarget>())
            {
                if (!TryGetSolverTarget(solverNodes, solverAxes, humanBoneIndex, target, out var targetPath, out _))
                {
                    continue;
                }
                var candidates = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
                AddAvatarPoseCorrectionCandidates(candidates, targetPath, solver, solverNodes, humanBoneIndex, target.HumanBone);
                foreach (var name in candidates.Keys)
                {
                    names.Add(name);
                }
            }
            return names;
        }

        private static bool TryResolveUpperArmFrontBackPoseAxis(
            string candidateName,
            SolverTarget target,
            string targetPath,
            JObject solver,
            JArray solverNodes,
            JArray humanBoneIndex,
            out float[] axis)
        {
            axis = null;
            if (string.Equals(candidateName, "current_nearest_minus_z", StringComparison.OrdinalIgnoreCase))
            {
                axis = new[] { 0f, 0f, -1f };
                return true;
            }

            if (string.Equals(candidateName, "oracle_pattern_frontback", StringComparison.OrdinalIgnoreCase))
            {
                axis = NormalizeVector3(new[]
                {
                    target.HumanBone.StartsWith("Left", StringComparison.OrdinalIgnoreCase) ? 0.302086f : -0.288938f,
                    0f,
                    -0.956282f,
                });
                return true;
            }

            var candidates = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            AddAvatarPoseCorrectionCandidates(candidates, targetPath, solver, solverNodes, humanBoneIndex, target.HumanBone);
            if (!candidates.TryGetValue(candidateName, out var pose))
            {
                return false;
            }
            axis = RotateVectorByQuaternion(new[] { 0f, 0f, -1f }, pose);
            return true;
        }

        private static float[] BuildUpperArmFrontBackPoseAxisRotation(
            Dictionary<string, List<FloatKey>> curves,
            SolverTarget target,
            SolverAxis axis,
            float time,
            float[] frontBackAxis,
            string applyMode)
        {
            var downUp = SampleFloatCurve(curves.TryGetValue(target.XAttribute ?? string.Empty, out var xCurve) ? xCurve : null, time);
            var frontBack = SampleFloatCurve(curves.TryGetValue(target.YAttribute ?? string.Empty, out var yCurve) ? yCurve : null, time);
            var baseAngles = new float[3];
            baseAngles[2] += LimitMuscle(downUp, axis, 2);
            var pre = Normalize(axis.PreQ);
            var post = Normalize(axis.PostQ);
            var baseRotation = Normalize(Multiply(Multiply(pre, SwingRadiansToQuaternion(baseAngles[1], baseAngles[2])), Inverse(post)));
            if (MathF.Abs(frontBack) <= 0.0000001f)
            {
                return baseRotation;
            }

            var angle = LimitMuscle(frontBack, axis, 1);
            var delta = AxisAngleRadiansToQuaternion(frontBackAxis[0], frontBackAxis[1], frontBackAxis[2], angle);
            return string.Equals(applyMode, "right_delta", StringComparison.OrdinalIgnoreCase)
                ? Normalize(Multiply(baseRotation, delta))
                : Normalize(Multiply(delta, baseRotation));
        }

        private static string SignName(int sign) => sign < 0 ? "neg" : "pos";

        private static (UpperArmSwingTimelineVariant Variant, float AvgTrackMaxDegrees, float MaxDegrees)[] RankUpperArmSwingTimelineVariants(
            JObject[] samples,
            Dictionary<string, List<FloatKey>> curves,
            JObject solver,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex)
        {
            return BuildUpperArmSwingTimelineVariants()
                .Select(variant =>
                {
                    var row = EvaluateUpperArmSwingTimelineVariant(samples, curves, solver, solverNodes, solverAxes, humanBoneIndex, variant);
                    return (
                        Variant: variant,
                        AvgTrackMaxDegrees: (float?)row["avgTrackMaxDegrees"] ?? float.MaxValue,
                        MaxDegrees: (float?)row["maxDegrees"] ?? float.MaxValue);
                })
                .OrderBy(x => x.AvgTrackMaxDegrees)
                .ThenBy(x => x.MaxDegrees)
                .ThenBy(x => x.Variant.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static JObject EvaluateUpperArmSwingTimelineVariant(
            JObject[] samples,
            Dictionary<string, List<FloatKey>> curves,
            JObject solver,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex,
            UpperArmSwingTimelineVariant variant)
        {
            var upperArmTargets = BuildCurrentSolverTargets()
                .Where(x => string.Equals(x.HumanBone, "LeftUpperArm", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(x.HumanBone, "RightUpperArm", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var accumulators = new Dictionary<string, TimelineTrackAccumulator>(StringComparer.OrdinalIgnoreCase);
            var missingTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in upperArmTargets)
            {
                if (!TryGetSolverTarget(solverNodes, solverAxes, humanBoneIndex, target, out var targetPath, out var targetAxis))
                {
                    missingTargets.Add(target.HumanBone);
                    continue;
                }

                foreach (var sample in samples)
                {
                    var time = (float?)sample["time"] ?? 0f;
                    var jointPaths = sample["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
                    var values = sample["values"]?.Values<float>().ToArray() ?? Array.Empty<float>();
                    if (!TryFindJointIndex(jointPaths, targetPath, out var jointIndex))
                    {
                        continue;
                    }

                    var offset = jointIndex * 7;
                    if (offset + 6 >= values.Length)
                    {
                        continue;
                    }

                    var unityRotation = Normalize(new[]
                    {
                        values[offset + 3],
                        values[offset + 4],
                        values[offset + 5],
                        values[offset + 6],
                    });
                    if (!TryBuildUpperArmSwingTimelineVariantRotation(curves, target, targetAxis, time, variant, solver, solverNodes, humanBoneIndex, targetPath, out var predicted))
                    {
                        missingTargets.Add(target.HumanBone);
                        continue;
                    }
                    if (!accumulators.TryGetValue(targetPath, out var accumulator))
                    {
                        accumulator = new TimelineTrackAccumulator(targetPath, targetPath, -1);
                        accumulators[targetPath] = accumulator;
                    }
                    accumulator.Add(QuaternionAngleDegrees(unityRotation, predicted));
                }
            }

            var rows = accumulators.Values
                .Select(x => x.ToRow())
                .OrderByDescending(x => x.MaxRotationErrorDegrees)
                .ThenBy(x => x.UnityPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new JObject
            {
                ["name"] = variant.Name,
                ["description"] = variant.Description,
                ["status"] = rows.Length == 0 ? "not_available" : "ok",
                ["matchedTrackCount"] = rows.Length,
                ["missingTargets"] = new JArray(missingTargets.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                ["xTargetAxis"] = AvatarAxisName(variant.XTargetAxis),
                ["yTargetAxis"] = AvatarAxisName(variant.YTargetAxis),
                ["xSign"] = variant.XSign,
                ["ySign"] = variant.YSign,
                ["swingMode"] = variant.SwingMode,
                ["composeMode"] = variant.ComposeMode,
                ["maxDegrees"] = rows.Length == 0 ? 0 : rows[0].MaxRotationErrorDegrees,
                ["avgTrackMaxDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.MaxRotationErrorDegrees),
                ["avgTrackAvgDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.AvgRotationErrorDegrees),
                ["topErrors"] = ToJsonRows(rows.Take(8)),
            };
        }

        private static (ArmTwistTimelineVariant Variant, float AvgTrackMaxDegrees, float MaxDegrees)[] RankArmTwistTimelineVariants(
            JObject[] samples,
            Dictionary<string, List<FloatKey>> curves,
            JObject solver,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex)
        {
            return BuildArmTwistTimelineVariants()
                .Select(variant =>
                {
                    var row = EvaluateArmTwistTimelineVariant(samples, curves, solver, solverNodes, solverAxes, humanBoneIndex, variant);
                    return (
                        Variant: variant,
                        AvgTrackMaxDegrees: (float?)row["avgTrackMaxDegrees"] ?? float.MaxValue,
                        MaxDegrees: (float?)row["maxDegrees"] ?? float.MaxValue);
                })
                .OrderBy(x => x.AvgTrackMaxDegrees)
                .ThenBy(x => x.MaxDegrees)
                .ThenBy(x => x.Variant.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static JObject EvaluateArmTwistTimelineVariant(
            JObject[] samples,
            Dictionary<string, List<FloatKey>> curves,
            JObject solver,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex,
            ArmTwistTimelineVariant variant)
        {
            var armTargets = BuildCurrentSolverTargets()
                .Where(x => string.Equals(x.HumanBone, "LeftLowerArm", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(x.HumanBone, "RightLowerArm", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var accumulators = new Dictionary<string, TimelineTrackAccumulator>(StringComparer.OrdinalIgnoreCase);
            var missingTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in armTargets)
            {
                if (!TryGetSolverTarget(solverNodes, solverAxes, humanBoneIndex, target, out var targetPath, out var targetAxis))
                {
                    missingTargets.Add(target.HumanBone);
                    continue;
                }

                foreach (var sample in samples)
                {
                    var time = (float?)sample["time"] ?? 0f;
                    var jointPaths = sample["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
                    var values = sample["values"]?.Values<float>().ToArray() ?? Array.Empty<float>();
                    if (!TryFindJointIndex(jointPaths, targetPath, out var jointIndex))
                    {
                        continue;
                    }

                    var offset = jointIndex * 7;
                    if (offset + 6 >= values.Length)
                    {
                        continue;
                    }

                    var unityRotation = Normalize(new[]
                    {
                        values[offset + 3],
                        values[offset + 4],
                        values[offset + 5],
                        values[offset + 6],
                    });
                    if (!TryBuildArmTwistTimelineVariantRotation(
                        curves,
                        target,
                        targetAxis,
                        time,
                        variant,
                        solver,
                        solverNodes,
                        solverAxes,
                        humanBoneIndex,
                        targetPath,
                        out var predicted))
                    {
                        missingTargets.Add(target.HumanBone);
                        continue;
                    }
                    if (!accumulators.TryGetValue(targetPath, out var accumulator))
                    {
                        accumulator = new TimelineTrackAccumulator(targetPath, targetPath, -1);
                        accumulators[targetPath] = accumulator;
                    }
                    accumulator.Add(QuaternionAngleDegrees(unityRotation, predicted));
                }
            }

            var rows = accumulators.Values
                .Select(x => x.ToRow())
                .OrderByDescending(x => x.MaxRotationErrorDegrees)
                .ThenBy(x => x.UnityPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new JObject
            {
                ["name"] = variant.Name,
                ["description"] = variant.Description,
                ["status"] = rows.Length == 0 ? "not_available" : "ok",
                ["matchedTrackCount"] = rows.Length,
                ["missingTargets"] = new JArray(missingTargets.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                ["maxDegrees"] = rows.Length == 0 ? 0 : rows[0].MaxRotationErrorDegrees,
                ["avgTrackMaxDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.MaxRotationErrorDegrees),
                ["avgTrackAvgDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.AvgRotationErrorDegrees),
                ["topErrors"] = ToJsonRows(rows.Take(8)),
            };
        }

        private static JObject BuildArmCombinedTimelineFitSummary(
            JObject[] samples,
            Dictionary<string, List<FloatKey>> curves,
            JObject solver,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex)
        {
            var upperVariants = RankUpperArmSwingTimelineVariants(samples, curves, solver, solverNodes, solverAxes, humanBoneIndex)
                .Take(8)
                .Select(x => x.Variant)
                .Concat(BuildUpperArmSwingTimelineVariants().Where(x => string.Equals(x.Name, "current_x_z_y_y_swing_twist", StringComparison.OrdinalIgnoreCase)))
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToArray();
            var twistVariants = RankArmTwistTimelineVariants(samples, curves, solver, solverNodes, solverAxes, humanBoneIndex)
                .Take(8)
                .Select(x => x.Variant)
                .Concat(BuildArmTwistTimelineVariants().Where(x => string.Equals(x.Name, "current_lower_x_to_x", StringComparison.OrdinalIgnoreCase)))
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToArray();

            if (upperVariants.Length == 0 || twistVariants.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["upperVariantCount"] = upperVariants.Length,
                    ["twistVariantCount"] = twistVariants.Length,
                };
            }

            var rows = new List<JObject>();
            foreach (var upper in upperVariants)
            {
                foreach (var twist in twistVariants)
                {
                    rows.Add(EvaluateArmCombinedTimelineVariant(samples, curves, solver, solverNodes, solverAxes, humanBoneIndex, upper, twist));
                }
            }

            var ordered = rows
                .Where(x => string.Equals((string)x["status"], "ok", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => (float?)x["avgTrackMaxDegrees"] ?? float.MaxValue)
                .ThenBy(x => (float?)x["maxDegrees"] ?? float.MaxValue)
                .ThenBy(x => (string)x["name"], StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (ordered.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["upperVariantCount"] = upperVariants.Length,
                    ["twistVariantCount"] = twistVariants.Length,
                    ["variantCount"] = rows.Count,
                };
            }

            var current = ordered.FirstOrDefault(x => string.Equals((string)x["name"], "current_x_z_y_y_swing_twist__current_lower_x_to_x", StringComparison.OrdinalIgnoreCase));
            var currentAvg = (float?)current?["avgTrackMaxDegrees"] ?? float.MaxValue;
            var currentMax = (float?)current?["maxDegrees"] ?? float.MaxValue;
            foreach (var row in ordered)
            {
                var avg = (float?)row["avgTrackMaxDegrees"] ?? float.MaxValue;
                var max = (float?)row["maxDegrees"] ?? float.MaxValue;
                row["avgImprovementDegrees"] = current == null ? 0f : Math.Max(0f, currentAvg - avg);
                row["maxImprovementDegrees"] = current == null ? 0f : Math.Max(0f, currentMax - max);
                row["beatsCurrent"] = current != null &&
                    (avg < currentAvg - 0.5f ||
                     (avg <= currentAvg + 0.5f && max < currentMax - 0.5f));
            }

            var best = ordered[0];
            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: combines top upper-arm swing timeline variants with top arm-twist timeline variants, then evaluates all arm tracks on Unity GetInternalAvatarPose. This checks whether the Arm problem is coupled instead of a single muscle-axis swap.",
                ["upperVariantCount"] = upperVariants.Length,
                ["twistVariantCount"] = twistVariants.Length,
                ["variantCount"] = ordered.Length,
                ["current"] = current == null ? null : CloneArmCombinedTimelineRow(current),
                ["best"] = CloneArmCombinedTimelineRow(best),
                ["bestBeatsCurrent"] = current != null && (bool?)best["beatsCurrent"] == true,
                ["topUseful"] = new JArray(ordered
                    .Where(x => (bool?)x["beatsCurrent"] == true)
                    .Take(12)
                    .Select(CloneArmCombinedTimelineRow)),
                ["topRanked"] = new JArray(ordered.Take(16).Select(CloneArmCombinedTimelineRow)),
            };
        }

        private static JObject EvaluateArmCombinedTimelineVariant(
            JObject[] samples,
            Dictionary<string, List<FloatKey>> curves,
            JObject solver,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex,
            UpperArmSwingTimelineVariant upperVariant,
            ArmTwistTimelineVariant twistVariant)
        {
            var targets = BuildCurrentSolverTargets()
                .Where(x => string.Equals(x.HumanBone, "LeftUpperArm", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(x.HumanBone, "RightUpperArm", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(x.HumanBone, "LeftLowerArm", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(x.HumanBone, "RightLowerArm", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var accumulators = new Dictionary<string, TimelineTrackAccumulator>(StringComparer.OrdinalIgnoreCase);
            var missingTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in targets)
            {
                if (!TryGetSolverTarget(solverNodes, solverAxes, humanBoneIndex, target, out var targetPath, out var targetAxis))
                {
                    missingTargets.Add(target.HumanBone);
                    continue;
                }

                foreach (var sample in samples)
                {
                    var time = (float?)sample["time"] ?? 0f;
                    var jointPaths = sample["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
                    var values = sample["values"]?.Values<float>().ToArray() ?? Array.Empty<float>();
                    if (!TryFindJointIndex(jointPaths, targetPath, out var jointIndex))
                    {
                        continue;
                    }

                    var offset = jointIndex * 7;
                    if (offset + 6 >= values.Length)
                    {
                        continue;
                    }

                    var unityRotation = Normalize(new[]
                    {
                        values[offset + 3],
                        values[offset + 4],
                        values[offset + 5],
                        values[offset + 6],
                    });
                    float[] predicted;
                    var hasPrediction = IsLowerArmBone(target.HumanBone)
                        ? TryBuildArmTwistTimelineVariantRotation(curves, target, targetAxis, time, twistVariant, solver, solverNodes, solverAxes, humanBoneIndex, targetPath, out predicted)
                        : TryBuildUpperArmSwingTimelineVariantRotation(curves, target, targetAxis, time, upperVariant, solver, solverNodes, humanBoneIndex, targetPath, out predicted);
                    if (!hasPrediction)
                    {
                        missingTargets.Add(target.HumanBone);
                        continue;
                    }
                    if (!accumulators.TryGetValue(targetPath, out var accumulator))
                    {
                        accumulator = new TimelineTrackAccumulator(targetPath, targetPath, -1);
                        accumulators[targetPath] = accumulator;
                    }
                    accumulator.Add(QuaternionAngleDegrees(unityRotation, predicted));
                }
            }

            var rows = accumulators.Values
                .Select(x => x.ToRow())
                .OrderByDescending(x => x.MaxRotationErrorDegrees)
                .ThenBy(x => x.UnityPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new JObject
            {
                ["name"] = $"{upperVariant.Name}__{twistVariant.Name}",
                ["upperArmVariant"] = upperVariant.Name,
                ["armTwistVariant"] = twistVariant.Name,
                ["status"] = rows.Length == 0 ? "not_available" : "ok",
                ["matchedTrackCount"] = rows.Length,
                ["missingTargets"] = new JArray(missingTargets.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                ["maxDegrees"] = rows.Length == 0 ? 0 : rows[0].MaxRotationErrorDegrees,
                ["avgTrackMaxDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.MaxRotationErrorDegrees),
                ["avgTrackAvgDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.AvgRotationErrorDegrees),
                ["topErrors"] = ToJsonRows(rows.Take(8)),
            };
        }

        private static JObject CloneArmCombinedTimelineRow(JObject row)
        {
            return new JObject
            {
                ["name"] = (string)row?["name"],
                ["upperArmVariant"] = (string)row?["upperArmVariant"],
                ["armTwistVariant"] = (string)row?["armTwistVariant"],
                ["maxDegrees"] = (float?)row?["maxDegrees"] ?? 0f,
                ["avgTrackMaxDegrees"] = (float?)row?["avgTrackMaxDegrees"] ?? 0f,
                ["avgTrackAvgDegrees"] = (float?)row?["avgTrackAvgDegrees"] ?? 0f,
                ["avgImprovementDegrees"] = (float?)row?["avgImprovementDegrees"] ?? 0f,
                ["maxImprovementDegrees"] = (float?)row?["maxImprovementDegrees"] ?? 0f,
                ["beatsCurrent"] = (bool?)row?["beatsCurrent"] ?? false,
            };
        }

        private static JObject BuildZeroBaseSourceTimelineFitSummary(
            JObject[] samples,
            Dictionary<string, List<FloatKey>> curves,
            JObject zeroBaseSourceCandidateGateSummary,
            JObject solver,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex,
            JObject[] gltfNodes,
            Dictionary<string, int> nodePathToIndex)
        {
            var sourceRows = zeroBaseSourceCandidateGateSummary?["rows"]?.OfType<JObject>()
                .Where(x => (bool?)x["beatsCurrent"] == true &&
                            !string.IsNullOrWhiteSpace((string)x["humanBone"]) &&
                            !string.IsNullOrWhiteSpace((string)x["formulaName"]))
                .Take(24)
                .ToArray() ?? Array.Empty<JObject>();
            if (samples == null || samples.Length == 0 || sourceRows.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["reason"] = sourceRows.Length == 0 ? "zero_base_source_gate_empty" : "timeline_samples_empty",
                };
            }

            var currentVariant = new SolverVariant(
                "current_swing_twist",
                "Current preview formula.",
                BuildCurrentSolverTargets(),
                "swing_twist");
            var gltfParents = BuildChildToParent(gltfNodes ?? Array.Empty<JObject>());
            var rows = new List<JObject>();
            foreach (var sourceRow in sourceRows)
            {
                var humanBone = (string)sourceRow["humanBone"];
                var formula = (string)sourceRow["formulaName"];
                var target = currentVariant.Targets.FirstOrDefault(x => string.Equals(x.HumanBone, humanBone, StringComparison.OrdinalIgnoreCase));
                if (target == null ||
                    !TryGetSolverTarget(solverNodes, solverAxes, humanBoneIndex, target, out var targetPath, out var axis))
                {
                    continue;
                }

                var currentZero = Normalize(Multiply(Normalize(axis.PreQ), Inverse(Normalize(axis.PostQ))));
                var candidates = BuildResidualCorrectionCandidates(targetPath, axis, solver, solverNodes, humanBoneIndex, target.HumanBone, gltfNodes, gltfParents, nodePathToIndex);
                foreach (var pair in BuildShortProductCorrectionCandidates(candidates))
                {
                    candidates[pair.Key] = pair.Value;
                }
                foreach (var pair in BuildWeightedParameterCorrectionCandidates(candidates, solver, currentZero, target.HumanBone))
                {
                    candidates[pair.Key] = pair.Value;
                }
                if (!candidates.TryGetValue(formula, out var zeroBase))
                {
                    continue;
                }

                var current = new TimelineTrackAccumulator(targetPath, targetPath, -1);
                var perMode = new Dictionary<string, TimelineTrackAccumulator>(StringComparer.OrdinalIgnoreCase);
                foreach (var mode in ZeroBaseSourceTimelineModes)
                {
                    perMode[mode] = new TimelineTrackAccumulator(targetPath, targetPath, -1);
                }

                foreach (var sample in samples)
                {
                    var time = (float?)sample["time"] ?? 0f;
                    var jointPaths = sample["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
                    var values = sample["values"]?.Values<float>().ToArray() ?? Array.Empty<float>();
                    if (!TryFindJointIndex(jointPaths, targetPath, out var jointIndex))
                    {
                        continue;
                    }

                    var offset = jointIndex * 7;
                    if (offset + 6 >= values.Length)
                    {
                        continue;
                    }

                    var unityRotation = Normalize(new[]
                    {
                        values[offset + 3],
                        values[offset + 4],
                        values[offset + 5],
                        values[offset + 6],
                    });
                    var predicted = BuildVariantSolverRotation(curves, target, axis, time, currentVariant, solver, solverNodes, humanBoneIndex, targetPath, gltfNodes, nodePathToIndex);
                    current.Add(QuaternionAngleDegrees(unityRotation, predicted));
                    foreach (var mode in ZeroBaseSourceTimelineModes)
                    {
                        var corrected = ApplyZeroBaseSourceTimelineMode(predicted, currentZero, zeroBase, mode);
                        perMode[mode].Add(QuaternionAngleDegrees(unityRotation, corrected));
                    }
                }

                var currentRow = current.ToRow();
                foreach (var pair in perMode)
                {
                    var metric = pair.Value.ToRow();
                    rows.Add(new JObject
                    {
                        ["name"] = $"{humanBone}|{pair.Key}|{formula}",
                        ["humanBone"] = humanBone,
                        ["bodyGroup"] = ClassifyBodyGroup(targetPath),
                        ["path"] = targetPath,
                        ["formulaName"] = formula,
                        ["formulaSource"] = sourceRow["formulaSource"] ?? JValue.CreateNull(),
                        ["formulaFamily"] = sourceRow["formulaFamily"] ?? JValue.CreateNull(),
                        ["applyMode"] = pair.Key,
                        ["currentMaxDegrees"] = currentRow.MaxRotationErrorDegrees,
                        ["currentAvgDegrees"] = currentRow.AvgRotationErrorDegrees,
                        ["maxDegrees"] = metric.MaxRotationErrorDegrees,
                        ["avgDegrees"] = metric.AvgRotationErrorDegrees,
                        ["improvementMaxDegrees"] = currentRow.MaxRotationErrorDegrees - metric.MaxRotationErrorDegrees,
                        ["improvementAvgDegrees"] = currentRow.AvgRotationErrorDegrees - metric.AvgRotationErrorDegrees,
                        ["beatsCurrent"] = metric.MaxRotationErrorDegrees < currentRow.MaxRotationErrorDegrees - 0.5f ||
                            (metric.MaxRotationErrorDegrees <= currentRow.MaxRotationErrorDegrees + 0.5f &&
                             metric.AvgRotationErrorDegrees < currentRow.AvgRotationErrorDegrees - 0.5f),
                    });
                }
            }

            var ordered = rows
                .OrderBy(x => JsonFloat(x, "maxDegrees"))
                .ThenBy(x => JsonFloat(x, "avgDegrees"))
                .ThenBy(x => (string)x["name"], StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new JObject
            {
                ["status"] = ordered.Length == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: takes the best deterministic zero-base source formula from zeroBaseSourceCandidateGateSummary and tests how that base should combine with the full Humanoid timeline delta. These rows are formula evidence only, not production solver rules.",
                ["sourceRowCount"] = sourceRows.Length,
                ["rowCount"] = ordered.Length,
                ["bestBeatsCurrent"] = ordered.Any(x => (bool?)x["beatsCurrent"] == true),
                ["topUseful"] = new JArray(ordered.Where(x => (bool?)x["beatsCurrent"] == true).Take(16)),
                ["topRanked"] = new JArray(ordered.Take(24)),
                ["rows"] = new JArray(ordered),
            };
        }

        private static readonly string[] ZeroBaseSourceTimelineModes =
        {
            "base_left_delta",
            "left_delta_base",
            "base_right_delta",
            "right_delta_base",
        };

        private static float[] ApplyZeroBaseSourceTimelineMode(float[] predicted, float[] currentZero, float[] zeroBase, string mode)
        {
            var normalizedPredicted = Normalize(predicted);
            var normalizedCurrentZero = Normalize(currentZero);
            var normalizedZeroBase = Normalize(zeroBase);
            var inverseCurrentZero = Inverse(normalizedCurrentZero);
            var leftDelta = Normalize(Multiply(normalizedPredicted, inverseCurrentZero));
            var rightDelta = Normalize(Multiply(inverseCurrentZero, normalizedPredicted));
            return mode switch
            {
                "base_left_delta" => Normalize(Multiply(normalizedZeroBase, leftDelta)),
                "left_delta_base" => Normalize(Multiply(leftDelta, normalizedZeroBase)),
                "base_right_delta" => Normalize(Multiply(normalizedZeroBase, rightDelta)),
                "right_delta_base" => Normalize(Multiply(rightDelta, normalizedZeroBase)),
                _ => normalizedPredicted,
            };
        }

        private static JObject BuildZeroBaseArmTwistTimelineFitSummary(
            JObject[] samples,
            Dictionary<string, List<FloatKey>> curves,
            JObject zeroBaseSourceCandidateGateSummary,
            JObject armTwistTimelineAxisFitSummary,
            JObject solver,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex,
            JObject[] gltfNodes,
            Dictionary<string, int> nodePathToIndex)
        {
            var sourceRows = zeroBaseSourceCandidateGateSummary?["rows"]?.OfType<JObject>()
                .Where(x => (bool?)x["beatsCurrent"] == true &&
                            IsLowerArmBone((string)x["humanBone"]) &&
                            !string.IsNullOrWhiteSpace((string)x["formulaName"]))
                .ToArray() ?? Array.Empty<JObject>();
            var variantNames = SelectArmTwistVariantNamesForZeroBaseFit(armTwistTimelineAxisFitSummary);
            if (samples == null || samples.Length == 0 || sourceRows.Length == 0 || variantNames.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["sourceRowCount"] = sourceRows.Length,
                    ["variantNameCount"] = variantNames.Length,
                };
            }

            var variantsByName = BuildArmTwistTimelineVariants()
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            var variants = variantNames
                .Where(x => variantsByName.ContainsKey(x))
                .Select(x => variantsByName[x])
                .ToArray();
            var targets = BuildCurrentSolverTargets()
                .Where(x => IsLowerArmBone(x.HumanBone))
                .ToDictionary(x => x.HumanBone, x => x, StringComparer.OrdinalIgnoreCase);
            var gltfParents = BuildChildToParent(gltfNodes ?? Array.Empty<JObject>());
            var rows = new List<JObject>();
            foreach (var sourceRow in sourceRows)
            {
                var humanBone = (string)sourceRow["humanBone"];
                var formula = (string)sourceRow["formulaName"];
                if (!targets.TryGetValue(humanBone, out var target) ||
                    !TryGetSolverTarget(solverNodes, solverAxes, humanBoneIndex, target, out var targetPath, out var axis))
                {
                    continue;
                }

                var currentZero = Normalize(Multiply(Normalize(axis.PreQ), Inverse(Normalize(axis.PostQ))));
                var candidates = BuildResidualCorrectionCandidates(targetPath, axis, solver, solverNodes, humanBoneIndex, target.HumanBone, gltfNodes, gltfParents, nodePathToIndex);
                foreach (var pair in BuildShortProductCorrectionCandidates(candidates))
                {
                    candidates[pair.Key] = pair.Value;
                }
                if (!candidates.TryGetValue(formula, out var zeroBase))
                {
                    continue;
                }

                foreach (var variant in variants)
                {
                    var perMode = ZeroBaseSourceTimelineModes.ToDictionary(
                        mode => mode,
                        _ => new TimelineTrackAccumulator(targetPath, targetPath, -1),
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var sample in samples)
                    {
                        var time = (float?)sample["time"] ?? 0f;
                        var jointPaths = sample["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
                        var values = sample["values"]?.Values<float>().ToArray() ?? Array.Empty<float>();
                        if (!TryFindJointIndex(jointPaths, targetPath, out var jointIndex))
                        {
                            continue;
                        }

                        var offset = jointIndex * 7;
                        if (offset + 6 >= values.Length ||
                            !TryBuildArmTwistTimelineVariantRotation(curves, target, axis, time, variant, solver, solverNodes, solverAxes, humanBoneIndex, targetPath, out var predicted))
                        {
                            continue;
                        }

                        var unityRotation = Normalize(new[]
                        {
                            values[offset + 3],
                            values[offset + 4],
                            values[offset + 5],
                            values[offset + 6],
                        });
                        foreach (var mode in ZeroBaseSourceTimelineModes)
                        {
                            var corrected = ApplyZeroBaseSourceTimelineMode(predicted, currentZero, zeroBase, mode);
                            perMode[mode].Add(QuaternionAngleDegrees(unityRotation, corrected));
                        }
                    }

                    foreach (var pair in perMode)
                    {
                        var metric = pair.Value.ToRow();
                        rows.Add(new JObject
                        {
                            ["name"] = $"{humanBone}|{pair.Key}|{variant.Name}",
                            ["humanBone"] = humanBone,
                            ["bodyGroup"] = ClassifyBodyGroup(targetPath),
                            ["path"] = targetPath,
                            ["applyMode"] = pair.Key,
                            ["armTwistVariant"] = variant.Name,
                            ["formulaName"] = formula,
                            ["formulaSource"] = sourceRow["formulaSource"] ?? JValue.CreateNull(),
                            ["formulaFamily"] = sourceRow["formulaFamily"] ?? JValue.CreateNull(),
                            ["maxDegrees"] = metric.MaxRotationErrorDegrees,
                            ["avgDegrees"] = metric.AvgRotationErrorDegrees,
                        });
                    }
                }
            }

            var currentByBone = (zeroBaseSourceTimelineFitSummaryRowsFrom(sourceRows, rows) ?? Array.Empty<JObject>());
            var ordered = rows
                .OrderBy(x => JsonFloat(x, "maxDegrees"))
                .ThenBy(x => JsonFloat(x, "avgDegrees"))
                .ThenBy(x => (string)x["name"], StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var row in ordered)
            {
                var sameBoneCurrent = currentByBone.FirstOrDefault(x =>
                    string.Equals((string)x["humanBone"], (string)row["humanBone"], StringComparison.OrdinalIgnoreCase) &&
                    string.Equals((string)x["applyMode"], (string)row["applyMode"], StringComparison.OrdinalIgnoreCase));
                var currentMax = JsonOptionalFloat(sameBoneCurrent, "maxDegrees");
                row["zeroBaseCurrentMaxDegrees"] = currentMax.HasValue ? JToken.FromObject(currentMax.Value) : JValue.CreateNull();
                row["improvementOverZeroBaseCurrentDegrees"] = currentMax.HasValue ? JToken.FromObject(currentMax.Value - JsonFloat(row, "maxDegrees")) : JValue.CreateNull();
                row["beatsZeroBaseCurrent"] = currentMax.HasValue && JsonFloat(row, "maxDegrees") < currentMax.Value - 0.5f;
            }

            return new JObject
            {
                ["status"] = ordered.Length == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: combines the best deterministic lower-arm zero-base source with ranked Arm Twist timeline variants. This tests whether the remaining forearm error is dynamic Arm Twist delta space rather than the zero anchor.",
                ["sourceRowCount"] = sourceRows.Length,
                ["variantNameCount"] = variantNames.Length,
                ["rowCount"] = ordered.Length,
                ["bestBeatsZeroBaseCurrent"] = ordered.Any(x => (bool?)x["beatsZeroBaseCurrent"] == true),
                ["topUseful"] = new JArray(ordered.Where(x => (bool?)x["beatsZeroBaseCurrent"] == true).Take(24)),
                ["topRanked"] = new JArray(ordered.Take(32)),
                ["rows"] = new JArray(ordered),
            };
        }

        private static string[] SelectArmTwistVariantNamesForZeroBaseFit(JObject armTwistTimelineAxisFitSummary)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "current_lower_x_to_x",
                "oracle_pattern_arm_twist_local_delta_left",
                "oracle_pattern_arm_twist_local_delta_right",
                "pose_axis_arm_twist_avatarDefaultParentLocal_left_delta",
                "pose_axis_arm_twist_avatarDefaultParentToChild_left_delta",
                "pose_axis_arm_twist_inverseAvatarDefaultSameIndex_left_delta",
            };
            foreach (var row in armTwistTimelineAxisFitSummary?["rankingSummary"]?["topRanked"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var name = (string)row["name"];
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
            foreach (var row in armTwistTimelineAxisFitSummary?["rankingSummary"]?["topUseful"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var name = (string)row["name"];
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
            return names.Take(32).ToArray();
        }

        private static JObject[] zeroBaseSourceTimelineFitSummaryRowsFrom(JObject[] sourceRows, List<JObject> rows)
        {
            return rows?
                .Where(x => string.Equals((string)x["armTwistVariant"], "current_lower_x_to_x", StringComparison.OrdinalIgnoreCase))
                .Select(x => new JObject
                {
                    ["humanBone"] = x["humanBone"],
                    ["applyMode"] = x["applyMode"],
                    ["maxDegrees"] = x["maxDegrees"],
                })
                .ToArray() ?? Array.Empty<JObject>();
        }

        private static JObject BuildZeroBaseForearmPairTimelineFitSummary(
            JObject[] samples,
            Dictionary<string, List<FloatKey>> curves,
            JObject zeroBaseSourceCandidateGateSummary,
            JObject singleMuscleProbeSolverComparison,
            JObject solver,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex,
            JObject[] gltfNodes,
            Dictionary<string, int> nodePathToIndex)
        {
            var sourceRows = zeroBaseSourceCandidateGateSummary?["rows"]?.OfType<JObject>()
                .Where(x => (bool?)x["beatsCurrent"] == true &&
                            (IsLowerArmBone((string)x["humanBone"]) || IsLowerLegBone((string)x["humanBone"])) &&
                            !string.IsNullOrWhiteSpace((string)x["formulaName"]))
                .ToArray() ?? Array.Empty<JObject>();
            var pairModes = BuildLimbPairDeltaModes(singleMuscleProbeSolverComparison);
            if (samples == null || samples.Length == 0 || sourceRows.Length == 0 || pairModes.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["sourceRowCount"] = sourceRows.Length,
                    ["pairModeCount"] = pairModes.Length,
                };
            }

            var targets = BuildCurrentSolverTargets()
                .Where(x => IsLowerArmBone(x.HumanBone) || IsLowerLegBone(x.HumanBone))
                .ToDictionary(x => x.HumanBone, x => x, StringComparer.OrdinalIgnoreCase);
            var gltfParents = BuildChildToParent(gltfNodes ?? Array.Empty<JObject>());
            var rows = new List<JObject>();
            foreach (var sourceRow in sourceRows)
            {
                var humanBone = (string)sourceRow["humanBone"];
                var formula = (string)sourceRow["formulaName"];
                if (!targets.TryGetValue(humanBone, out var target) ||
                    !TryGetSolverTarget(solverNodes, solverAxes, humanBoneIndex, target, out var targetPath, out var axis))
                {
                    continue;
                }

                var currentZero = Normalize(Multiply(Normalize(axis.PreQ), Inverse(Normalize(axis.PostQ))));
                var candidates = BuildResidualCorrectionCandidates(targetPath, axis, solver, solverNodes, humanBoneIndex, target.HumanBone, gltfNodes, gltfParents, nodePathToIndex);
                foreach (var pair in BuildShortProductCorrectionCandidates(candidates))
                {
                    candidates[pair.Key] = pair.Value;
                }
                if (!candidates.TryGetValue(formula, out var zeroBase))
                {
                    continue;
                }

                foreach (var pairMode in pairModes)
                {
                    if (!DoesLimbPairModeApply(pairMode, target.HumanBone))
                    {
                        continue;
                    }

                    var accumulators = ZeroBaseSourceTimelineModes.ToDictionary(
                        mode => mode,
                        _ => new TimelineTrackAccumulator(targetPath, targetPath, -1),
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var sample in samples)
                    {
                        var time = (float?)sample["time"] ?? 0f;
                        var jointPaths = sample["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
                        var values = sample["values"]?.Values<float>().ToArray() ?? Array.Empty<float>();
                        if (!TryFindJointIndex(jointPaths, targetPath, out var jointIndex))
                        {
                            continue;
                        }

                        var offset = jointIndex * 7;
                        if (offset + 6 >= values.Length)
                        {
                            continue;
                        }

                        var unityRotation = Normalize(new[]
                        {
                            values[offset + 3],
                            values[offset + 4],
                            values[offset + 5],
                            values[offset + 6],
                        });
                        var dynamicRotation = BuildLimbPairDeltaRotation(curves, target, axis, time, pairMode, solverNodes, solverAxes, humanBoneIndex);
                        foreach (var applyMode in ZeroBaseSourceTimelineModes)
                        {
                            var corrected = ApplyZeroBaseSourceTimelineMode(dynamicRotation, currentZero, zeroBase, applyMode);
                            accumulators[applyMode].Add(QuaternionAngleDegrees(unityRotation, corrected));
                        }
                    }

                    foreach (var pair in accumulators)
                    {
                        var metric = pair.Value.ToRow();
                        rows.Add(new JObject
                        {
                            ["name"] = $"{humanBone}|{pair.Key}|{pairMode.Name}",
                            ["humanBone"] = humanBone,
                            ["bodyGroup"] = ClassifyBodyGroup(targetPath),
                            ["path"] = targetPath,
                            ["applyMode"] = pair.Key,
                            ["pairMode"] = pairMode.Name,
                            ["pairFamily"] = pairMode.TargetFamily,
                            ["stretchFormula"] = pairMode.StretchFormula,
                            ["twistFormula"] = pairMode.TwistFormula,
                            ["stretchSide"] = pairMode.StretchSide,
                            ["twistSide"] = pairMode.TwistSide,
                            ["orderMode"] = pairMode.OrderMode,
                            ["outputSide"] = pairMode.OutputSide,
                            ["formulaName"] = formula,
                            ["formulaSource"] = sourceRow["formulaSource"] ?? JValue.CreateNull(),
                            ["formulaFamily"] = sourceRow["formulaFamily"] ?? JValue.CreateNull(),
                            ["maxDegrees"] = metric.MaxRotationErrorDegrees,
                            ["avgDegrees"] = metric.AvgRotationErrorDegrees,
                        });
                    }
                }
            }

            AddZeroBasePairBaselineMetrics(rows);
            var ordered = rows
                .OrderBy(x => JsonFloat(x, "maxDegrees"))
                .ThenBy(x => JsonFloat(x, "avgDegrees"))
                .ThenBy(x => (string)x["name"], StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new JObject
            {
                ["status"] = ordered.Length == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: keeps the best deterministic lower-arm/lower-leg zero base, then composes distal Stretch and proximal Twist as paired dynamic deltas. This checks whether visible forearm/calf error is caused by pair composition rather than a single muscle curve.",
                ["sourceRowCount"] = sourceRows.Length,
                ["pairModeCount"] = pairModes.Length,
                ["rowCount"] = ordered.Length,
                ["bestBeatsZeroBaseCurrent"] = ordered.Any(x => (bool?)x["beatsZeroBaseCurrent"] == true),
                ["topUseful"] = new JArray(ordered.Where(x => (bool?)x["beatsZeroBaseCurrent"] == true).Take(24)),
                ["topRanked"] = new JArray(ordered.Take(32)),
                ["rows"] = new JArray(ordered),
            };
        }

        private static void AddZeroBasePairBaselineMetrics(List<JObject> rows)
        {
            var baselines = rows
                .Where(x => string.Equals((string)x["pairMode"], "current_pair_delta", StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => $"{(string)x["humanBone"]}|{(string)x["applyMode"]}", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => JsonFloat(x.OrderBy(y => JsonFloat(y, "maxDegrees")).First(), "maxDegrees"), StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                var key = $"{(string)row["humanBone"]}|{(string)row["applyMode"]}";
                if (!baselines.TryGetValue(key, out var baseline))
                {
                    row["zeroBaseCurrentMaxDegrees"] = JValue.CreateNull();
                    row["improvementOverZeroBaseCurrentDegrees"] = JValue.CreateNull();
                    row["beatsZeroBaseCurrent"] = false;
                    continue;
                }

                var max = JsonFloat(row, "maxDegrees");
                row["zeroBaseCurrentMaxDegrees"] = baseline;
                row["improvementOverZeroBaseCurrentDegrees"] = baseline - max;
                row["beatsZeroBaseCurrent"] = max < baseline - 0.5f;
            }
        }

        private static ForearmPairDeltaMode[] BuildLimbPairDeltaModes(JObject singleMuscleProbeSolverComparison)
        {
            var modes = new List<ForearmPairDeltaMode>
            {
                new("current_pair_delta", "pre_middle_inversePost", "pre_middle_inversePost", "left", "left", "append", "right"),
            };
            modes.AddRange(BuildUnityProbeForearmPairDeltaModes(singleMuscleProbeSolverComparison));
            modes.AddRange(BuildUnityProbeLowerLegPairDeltaModes(singleMuscleProbeSolverComparison));
            foreach (var stretchFormula in new[] { "pre_middle_inversePost", "inversePost_middle_post", "preProjectedDelta_zero" })
            {
                foreach (var twistFormula in new[] { "pre_middle_inversePost", "inversePost_middle_post", "post_middle_inversePost", "postProjectedDelta_zero", "preProjectedDelta_zero" })
                {
                    foreach (var stretchSide in new[] { "left", "right" })
                    {
                        foreach (var twistSide in new[] { "left", "right" })
                        {
                            foreach (var orderMode in new[] { "append", "prepend" })
                            {
                                foreach (var outputSide in new[] { "left", "right" })
                                {
                                    modes.Add(new ForearmPairDeltaMode(
                                        $"pair_{outputSide}_{orderMode}_stretch{stretchSide}_twist{twistSide}_{stretchFormula}__{twistFormula}",
                                        stretchFormula,
                                        twistFormula,
                                        stretchSide,
                                        twistSide,
                                        orderMode,
                                        outputSide));
                                }
                            }
                        }
                    }
                }
            }
            return modes
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .Take(192)
                .ToArray();
        }

        private static IEnumerable<ForearmPairDeltaMode> BuildUnityProbeForearmPairDeltaModes(JObject singleMuscleProbeSolverComparison)
        {
            var rows = singleMuscleProbeSolverComparison?["muscleCombinationProbeSolverComparison"]?["unitySingleMuscleCompositionSummary"]?["topRows"]?.OfType<JObject>()
                .Where(x => string.Equals((string)x["probeFamily"], "arm_twist_forearm_stretch", StringComparison.OrdinalIgnoreCase))
                .ToArray() ?? Array.Empty<JObject>();
            foreach (var side in new[] { "Left", "Right" })
            {
                var pos = rows.FirstOrDefault(x =>
                    ((string)x["probeName"])?.StartsWith(side + "_arm_twist_forearm_stretch_pos", StringComparison.OrdinalIgnoreCase) == true);
                var neg = rows.FirstOrDefault(x =>
                    ((string)x["probeName"])?.StartsWith(side + "_arm_twist_forearm_stretch_neg", StringComparison.OrdinalIgnoreCase) == true);
                if (!TryBuildUnityProbeForearmPairMode(side, pos, neg, out var mode))
                {
                    continue;
                }
                yield return mode;
            }
        }

        private static IEnumerable<ForearmPairDeltaMode> BuildUnityProbeLowerLegPairDeltaModes(JObject singleMuscleProbeSolverComparison)
        {
            var rows = singleMuscleProbeSolverComparison?["muscleCombinationProbeSolverComparison"]?["unitySingleMuscleCompositionSummary"]?["topRows"]?.OfType<JObject>()
                .Where(x => string.Equals((string)x["probeFamily"], "leg_twist_stretch", StringComparison.OrdinalIgnoreCase))
                .ToArray() ?? Array.Empty<JObject>();
            foreach (var side in new[] { "Left", "Right" })
            {
                var pos = rows.FirstOrDefault(x =>
                    ((string)x["probeName"])?.StartsWith(side + "_leg_twist_stretch_pos", StringComparison.OrdinalIgnoreCase) == true);
                var neg = rows.FirstOrDefault(x =>
                    ((string)x["probeName"])?.StartsWith(side + "_leg_twist_stretch_neg", StringComparison.OrdinalIgnoreCase) == true);
                if (!TryBuildUnityProbeLowerLegPairMode(side, pos, neg, out var mode))
                {
                    continue;
                }
                yield return mode;
            }
        }

        private static bool TryBuildUnityProbeLowerLegPairMode(string side, JObject positiveRow, JObject negativeRow, out ForearmPairDeltaMode mode)
        {
            mode = null;
            if (string.IsNullOrWhiteSpace(side) || positiveRow == null || negativeRow == null)
            {
                return false;
            }

            if (!TryParseUnityProbeBestMode((string)positiveRow["bestMode"], out var positiveDeltaSide, out var positiveOrder, out var positiveOutputSide) ||
                !TryParseUnityProbeBestMode((string)negativeRow["bestMode"], out var negativeDeltaSide, out var negativeOrder, out var negativeOutputSide))
            {
                return false;
            }

            mode = new ForearmPairDeltaMode(
                $"unity_probe_leg_sign_policy_{side.ToLowerInvariant()}",
                "pre_middle_inversePost",
                "pre_middle_inversePost",
                "unityProbeSign",
                "unityProbeSign",
                "unityProbeSign",
                "unityProbeSign",
                positiveDeltaSide,
                positiveOrder,
                positiveOutputSide,
                negativeDeltaSide,
                negativeOrder,
                negativeOutputSide,
                "leg");
            return true;
        }

        private static bool TryBuildUnityProbeForearmPairMode(string side, JObject positiveRow, JObject negativeRow, out ForearmPairDeltaMode mode)
        {
            mode = null;
            if (string.IsNullOrWhiteSpace(side) || positiveRow == null || negativeRow == null)
            {
                return false;
            }

            if (!TryParseUnityProbeBestMode((string)positiveRow["bestMode"], out var positiveDeltaSide, out var positiveOrder, out var positiveOutputSide) ||
                !TryParseUnityProbeBestMode((string)negativeRow["bestMode"], out var negativeDeltaSide, out var negativeOrder, out var negativeOutputSide))
            {
                return false;
            }

            mode = new ForearmPairDeltaMode(
                $"unity_probe_sign_policy_{side.ToLowerInvariant()}",
                "pre_middle_inversePost",
                "pre_middle_inversePost",
                "unityProbeSign",
                "unityProbeSign",
                "unityProbeSign",
                "unityProbeSign",
                positiveDeltaSide,
                positiveOrder,
                positiveOutputSide,
                negativeDeltaSide,
                negativeOrder,
                negativeOutputSide,
                "arm");
            return true;
        }

        private static bool DoesLimbPairModeApply(ForearmPairDeltaMode mode, string humanBone)
        {
            if (string.IsNullOrWhiteSpace(mode.TargetFamily) ||
                string.Equals(mode.TargetFamily, "any", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(mode.TargetFamily, "arm", StringComparison.OrdinalIgnoreCase))
            {
                return IsLowerArmBone(humanBone);
            }

            if (string.Equals(mode.TargetFamily, "leg", StringComparison.OrdinalIgnoreCase))
            {
                return IsLowerLegBone(humanBone);
            }

            return false;
        }

        private static bool TryParseUnityProbeBestMode(string bestMode, out string deltaSide, out string orderMode, out string outputSide)
        {
            deltaSide = null;
            orderMode = null;
            outputSide = null;
            if (string.IsNullOrWhiteSpace(bestMode))
            {
                return false;
            }

            if (bestMode.StartsWith("leftDelta_", StringComparison.OrdinalIgnoreCase))
            {
                deltaSide = "left";
                outputSide = "left";
                orderMode = bestMode.EndsWith("_prepend", StringComparison.OrdinalIgnoreCase) ? "prepend" : "append";
                return true;
            }
            if (bestMode.StartsWith("rightDelta_", StringComparison.OrdinalIgnoreCase))
            {
                deltaSide = "right";
                outputSide = "right";
                orderMode = bestMode.EndsWith("_prepend", StringComparison.OrdinalIgnoreCase) ? "prepend" : "append";
                return true;
            }
            return false;
        }

        private static float[] BuildLimbPairDeltaRotation(
            Dictionary<string, List<FloatKey>> curves,
            SolverTarget target,
            SolverAxis targetAxis,
            float time,
            ForearmPairDeltaMode mode,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex)
        {
            var zeroRotation = Normalize(Multiply(Normalize(targetAxis.PreQ), Inverse(Normalize(targetAxis.PostQ))));
            var stretch = SampleFloatCurve(curves.TryGetValue(target.XAttribute ?? string.Empty, out var stretchCurve) ? stretchCurve : null, time);
            var twist = SampleFloatCurve(curves.TryGetValue(target.ZAttribute ?? string.Empty, out var twistCurve) ? twistCurve : null, time);
            ResolveForearmPairRuntimePolicy(mode, stretch, out var stretchSide, out var twistSide, out var orderMode, out var outputSide);

            var stretchAngle = LimitMuscle(stretch, targetAxis, 2);
            var stretchDelta = BuildSingleFormulaDelta(targetAxis, 2, stretchAngle, mode.StretchFormula, string.Equals(stretchSide, "right", StringComparison.OrdinalIgnoreCase));

            var sourceHumanBone = ResolveDistalPairTwistSourceHumanBone(target.HumanBone);
            var sourceAxis = targetAxis;
            if (!string.Equals(sourceHumanBone, target.HumanBone, StringComparison.OrdinalIgnoreCase) &&
                !TryGetHumanBonePathAxis(solverNodes, solverAxes, humanBoneIndex, sourceHumanBone, out _, out sourceAxis))
            {
                sourceAxis = targetAxis;
            }

            var twistAngle = LimitMuscle(twist, sourceAxis, 0);
            var twistDelta = BuildSingleFormulaDelta(targetAxis, 0, twistAngle, mode.TwistFormula, string.Equals(twistSide, "right", StringComparison.OrdinalIgnoreCase));
            var deltaOrder = string.Equals(mode.StretchSide, "unityProbeSign", StringComparison.OrdinalIgnoreCase)
                ? new[] { twistDelta, stretchDelta }
                : new[] { stretchDelta, twistDelta };
            var composed = ComposeDeltas(deltaOrder, new[] { 0, 1 }, string.Equals(orderMode, "append", StringComparison.OrdinalIgnoreCase));
            return string.Equals(outputSide, "right", StringComparison.OrdinalIgnoreCase)
                ? Normalize(Multiply(zeroRotation, composed))
                : Normalize(Multiply(composed, zeroRotation));
        }

        private static string ResolveDistalPairTwistSourceHumanBone(string targetHumanBone)
        {
            if (string.Equals(targetHumanBone, "LeftLowerLeg", StringComparison.OrdinalIgnoreCase))
            {
                return "LeftUpperLeg";
            }
            if (string.Equals(targetHumanBone, "RightLowerLeg", StringComparison.OrdinalIgnoreCase))
            {
                return "RightUpperLeg";
            }
            return targetHumanBone;
        }

        private static void ResolveForearmPairRuntimePolicy(
            ForearmPairDeltaMode mode,
            float stretchValue,
            out string stretchSide,
            out string twistSide,
            out string orderMode,
            out string outputSide)
        {
            var usePositive = stretchValue >= 0f;
            if (string.Equals(mode.StretchSide, "unityProbeSign", StringComparison.OrdinalIgnoreCase))
            {
                var deltaSide = usePositive ? mode.PositiveDeltaSide : mode.NegativeDeltaSide;
                stretchSide = deltaSide ?? "left";
                twistSide = deltaSide ?? "left";
                orderMode = (usePositive ? mode.PositiveOrderMode : mode.NegativeOrderMode) ?? "append";
                outputSide = (usePositive ? mode.PositiveOutputSide : mode.NegativeOutputSide) ?? stretchSide;
                return;
            }

            stretchSide = mode.StretchSide;
            twistSide = mode.TwistSide;
            orderMode = mode.OrderMode;
            outputSide = mode.OutputSide;
        }

        private static JObject BuildResidualCandidateTimelineFitSummary(
            JObject[] samples,
            Dictionary<string, List<FloatKey>> curves,
            JObject unityResult,
            JObject solver,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex,
            JObject[] gltfNodes,
            Dictionary<string, int> nodePathToIndex)
        {
            var gltfParents = BuildChildToParent(gltfNodes ?? Array.Empty<JObject>());
            var currentVariant = new SolverVariant(
                "current_swing_twist",
                "Current preview formula.",
                BuildCurrentSolverTargets(),
                "swing_twist");
            var currentByBone = new Dictionary<string, TimelineTrackAccumulator>(StringComparer.OrdinalIgnoreCase);
            var perCandidate = new Dictionary<string, Dictionary<string, TimelineTrackAccumulator>>(StringComparer.OrdinalIgnoreCase);
            var bestLeft = new Dictionary<string, TimelineTrackAccumulator>(StringComparer.OrdinalIgnoreCase);
            var bestRight = new Dictionary<string, TimelineTrackAccumulator>(StringComparer.OrdinalIgnoreCase);
            var chosenLeft = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var chosenRight = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var humanBoneByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var missingTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var target in currentVariant.Targets)
            {
                if (!TryGetSolverTarget(solverNodes, solverAxes, humanBoneIndex, target, out var targetPath, out var axis))
                {
                    missingTargets.Add(target.HumanBone);
                    continue;
                }

                var samplesForTarget = new List<(float[] Unity, float[] Predicted)>();
                foreach (var sample in samples)
                {
                    var time = (float?)sample["time"] ?? 0f;
                    var jointPaths = sample["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
                    var values = sample["values"]?.Values<float>().ToArray() ?? Array.Empty<float>();
                    if (!TryFindJointIndex(jointPaths, targetPath, out var jointIndex))
                    {
                        continue;
                    }

                    var offset = jointIndex * 7;
                    if (offset + 6 >= values.Length)
                    {
                        continue;
                    }

                    var unityRotation = Normalize(new[]
                    {
                        values[offset + 3],
                        values[offset + 4],
                        values[offset + 5],
                        values[offset + 6],
                    });
                    var predicted = BuildVariantSolverRotation(
                        curves,
                        target,
                        axis,
                        time,
                        currentVariant,
                        solver,
                        solverNodes,
                        humanBoneIndex,
                        targetPath,
                        gltfNodes,
                        nodePathToIndex);
                    samplesForTarget.Add((unityRotation, predicted));
                }

                if (samplesForTarget.Count == 0)
                {
                    continue;
                }

                humanBoneByPath[targetPath] = target.HumanBone;
                var currentAccumulator = new TimelineTrackAccumulator(targetPath, targetPath, -1);
                foreach (var sample in samplesForTarget)
                {
                    currentAccumulator.Add(QuaternionAngleDegrees(sample.Unity, sample.Predicted));
                }
                currentByBone[targetPath] = currentAccumulator;

                var candidates = BuildResidualCorrectionCandidates(targetPath, axis, solver, solverNodes, humanBoneIndex, target.HumanBone, gltfNodes, gltfParents, nodePathToIndex);
                AddUnityZeroBaseCorrectionCandidates(candidates, unityResult, targetPath, axis);
                foreach (var pair in candidates)
                {
                    AddResidualCandidateErrors(perCandidate, pair.Key + "|left", targetPath, samplesForTarget, pair.Value, left: true);
                    AddResidualCandidateErrors(perCandidate, pair.Key + "|right", targetPath, samplesForTarget, pair.Value, left: false);
                }

                var bestLeftRow = FindBestResidualCandidate(targetPath, samplesForTarget, candidates, left: true);
                if (bestLeftRow.Accumulator != null)
                {
                    bestLeft[targetPath] = bestLeftRow.Accumulator;
                    chosenLeft[targetPath] = bestLeftRow.Name;
                }
                var bestRightRow = FindBestResidualCandidate(targetPath, samplesForTarget, candidates, left: false);
                if (bestRightRow.Accumulator != null)
                {
                    bestRight[targetPath] = bestRightRow.Accumulator;
                    chosenRight[targetPath] = bestRightRow.Name;
                }
            }

            var candidateRows = new JArray(perCandidate
                .Select(x => BuildResidualCandidateRow(x.Key, x.Value.Values.Select(y => y.ToRow()).ToArray()))
                .Where(x => string.Equals((string)x["status"], "ok", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => (float?)x["avgTrackMaxDegrees"] ?? float.MaxValue)
                .ThenBy(x => (float?)x["maxDegrees"] ?? float.MaxValue)
                .ThenBy(x => (string)x["name"], StringComparer.OrdinalIgnoreCase));

            return new JObject
            {
                ["status"] = candidateRows.Count == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: applies deterministic per-bone correction candidates from glTF rest, parent rest, Avatar default pose, Avatar skeleton pose, and Unity zero-muscle oracle base pose to the current solver timeline. Unity zero base is an oracle-only upper bound: it proves whether base-pose correction is sufficient, but must be replaced by exported Avatar/rest metadata before production.",
                ["missingTargets"] = new JArray(missingTargets.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                ["bestSingleCandidateVariants"] = candidateRows,
                ["currentByBone"] = BuildBestPerBoneResidualCandidateSummary(currentByBone, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
                ["bestPerBoneLeft"] = BuildBestPerBoneResidualCandidateSummary(bestLeft, chosenLeft),
                ["bestPerBoneRight"] = BuildBestPerBoneResidualCandidateSummary(bestRight, chosenRight),
                ["focusedLimbPerBoneCandidateSummary"] = BuildFocusedLimbResidualCandidateSummary(currentByBone, bestLeft, bestRight, chosenLeft, chosenRight, humanBoneByPath),
            };
        }

        private static void AddUnityZeroBaseCorrectionCandidates(
            Dictionary<string, float[]> candidates,
            JObject unityResult,
            string targetPath,
            SolverAxis axis)
        {
            if (candidates == null ||
                !TryReadUnityZeroMuscleRotation(unityResult, targetPath, out var unityZero))
            {
                return;
            }

            var currentZero = Normalize(Multiply(Normalize(axis.PreQ), Inverse(Normalize(axis.PostQ))));
            var leftCorrection = Normalize(Multiply(unityZero, Inverse(currentZero)));
            var rightCorrection = Normalize(Multiply(Inverse(currentZero), unityZero));

            // 这两个候选直接来自 Unity zero-muscle oracle，只能用于判断“base pose 修正是否足够”。
            // 如果它显著变好，下一步必须再把同等修正追溯到 Avatar/rest pose 元数据。
            candidates["unityZeroBase*inverse(preQ*inverse(postQ))"] = leftCorrection;
            candidates["inverse(preQ*inverse(postQ))*unityZeroBase"] = rightCorrection;
        }

        private static bool TryReadUnityZeroMuscleRotation(JObject unityResult, string targetPath, out float[] rotation)
        {
            rotation = null;
            var normalizedTarget = NormalizeBakePath(targetPath);
            foreach (var snapshot in unityResult?["internalAvatarPoseSnapshots"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                if (!string.Equals((string)snapshot["status"], "ok", StringComparison.OrdinalIgnoreCase) ||
                    (((string)snapshot["label"])?.StartsWith("zeroMuscle", StringComparison.OrdinalIgnoreCase) != true))
                {
                    continue;
                }

                var jointPaths = snapshot["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
                var values = snapshot["values"]?.Values<float>().ToArray() ?? Array.Empty<float>();
                for (var i = 0; i < jointPaths.Length; i++)
                {
                    var normalizedPath = NormalizeBakePath(jointPaths[i]);
                    if (!string.Equals(normalizedPath, normalizedTarget, StringComparison.OrdinalIgnoreCase) &&
                        !(normalizedPath?.EndsWith("/" + normalizedTarget, StringComparison.OrdinalIgnoreCase) == true) &&
                        !(normalizedTarget?.EndsWith("/" + normalizedPath, StringComparison.OrdinalIgnoreCase) == true))
                    {
                        continue;
                    }

                    var offset = i * 7;
                    if (offset + 6 >= values.Length)
                    {
                        return false;
                    }

                    rotation = Normalize(new[]
                    {
                        values[offset + 3],
                        values[offset + 4],
                        values[offset + 5],
                        values[offset + 6],
                    });
                    return true;
                }
            }
            return false;
        }

        private static void AddResidualCandidateErrors(
            Dictionary<string, Dictionary<string, TimelineTrackAccumulator>> perCandidate,
            string candidateName,
            string targetPath,
            List<(float[] Unity, float[] Predicted)> samples,
            float[] correction,
            bool left)
        {
            if (!perCandidate.TryGetValue(candidateName, out var tracks))
            {
                tracks = new Dictionary<string, TimelineTrackAccumulator>(StringComparer.OrdinalIgnoreCase);
                perCandidate[candidateName] = tracks;
            }
            if (!tracks.TryGetValue(targetPath, out var accumulator))
            {
                accumulator = new TimelineTrackAccumulator(targetPath, targetPath, -1);
                tracks[targetPath] = accumulator;
            }

            foreach (var sample in samples)
            {
                var predicted = left
                    ? Multiply(correction, sample.Predicted)
                    : Multiply(sample.Predicted, correction);
                accumulator.Add(QuaternionAngleDegrees(sample.Unity, predicted));
            }
        }

        private static (string Name, TimelineTrackAccumulator Accumulator) FindBestResidualCandidate(
            string targetPath,
            List<(float[] Unity, float[] Predicted)> samples,
            Dictionary<string, float[]> candidates,
            bool left)
        {
            string bestName = null;
            TimelineTrackAccumulator best = null;
            foreach (var pair in candidates)
            {
                var accumulator = new TimelineTrackAccumulator(targetPath, targetPath, -1);
                foreach (var sample in samples)
                {
                    var predicted = left
                        ? Multiply(pair.Value, sample.Predicted)
                        : Multiply(sample.Predicted, pair.Value);
                    accumulator.Add(QuaternionAngleDegrees(sample.Unity, predicted));
                }

                if (best == null ||
                    accumulator.ToRow().MaxRotationErrorDegrees < best.ToRow().MaxRotationErrorDegrees ||
                    (Math.Abs(accumulator.ToRow().MaxRotationErrorDegrees - best.ToRow().MaxRotationErrorDegrees) < 0.0001f &&
                     accumulator.ToRow().AvgRotationErrorDegrees < best.ToRow().AvgRotationErrorDegrees))
                {
                    bestName = pair.Key;
                    best = accumulator;
                }
            }

            return (bestName, best);
        }

        private static JObject BuildResidualCandidateRow(string name, TrackCompareRow[] rows) => new()
        {
            ["name"] = name,
            ["status"] = rows.Length == 0 ? "not_available" : "ok",
            ["matchedTrackCount"] = rows.Length,
            ["maxDegrees"] = rows.Length == 0 ? 0 : rows.Max(x => x.MaxRotationErrorDegrees),
            ["avgTrackMaxDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.MaxRotationErrorDegrees),
            ["avgTrackAvgDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.AvgRotationErrorDegrees),
            ["bodyGroupError"] = BuildBodyGroupErrorSummary(rows),
            ["topErrors"] = ToJsonRows(rows
                .OrderByDescending(x => x.MaxRotationErrorDegrees)
                .ThenBy(x => x.UnityPath, StringComparer.OrdinalIgnoreCase)
                .Take(8)),
        };

        private static JObject BuildBestPerBoneResidualCandidateSummary(
            Dictionary<string, TimelineTrackAccumulator> accumulators,
            Dictionary<string, string> chosen)
        {
            var rows = accumulators.Values
                .Select(x => x.ToRow())
                .OrderByDescending(x => x.MaxRotationErrorDegrees)
                .ThenBy(x => x.UnityPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new JObject
            {
                ["status"] = rows.Length == 0 ? "not_available" : "ok",
                ["matchedTrackCount"] = rows.Length,
                ["maxDegrees"] = rows.Length == 0 ? 0 : rows.Max(x => x.MaxRotationErrorDegrees),
                ["avgTrackMaxDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.MaxRotationErrorDegrees),
                ["avgTrackAvgDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.AvgRotationErrorDegrees),
                ["bodyGroupError"] = BuildBodyGroupErrorSummary(rows),
                ["chosenCandidateSummary"] = BuildChosenCandidateSummary(rows, chosen),
                ["chosenCandidates"] = new JObject(chosen
                    .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new JProperty(x.Key, x.Value))),
                ["topErrors"] = ToJsonRows(rows.Take(12)),
            };
        }

        private static JArray BuildChosenCandidateSummary(TrackCompareRow[] rows, Dictionary<string, string> chosen)
        {
            if (rows == null || chosen == null || rows.Length == 0 || chosen.Count == 0)
            {
                return new JArray();
            }

            var rowByPath = rows.ToDictionary(x => x.UnityPath, x => x, StringComparer.OrdinalIgnoreCase);
            var groups = new Dictionary<string, List<TrackCompareRow>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in chosen)
            {
                if (string.IsNullOrWhiteSpace(pair.Value) || !rowByPath.TryGetValue(pair.Key, out var row))
                {
                    continue;
                }

                if (!groups.TryGetValue(pair.Value, out var list))
                {
                    list = new List<TrackCompareRow>();
                    groups[pair.Value] = list;
                }
                list.Add(row);
            }

            return new JArray(groups
                .Select(pair =>
                {
                    var candidateRows = pair.Value
                        .OrderByDescending(x => x.MaxRotationErrorDegrees)
                        .ThenBy(x => x.UnityPath, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    return new JObject
                    {
                        ["candidate"] = pair.Key,
                        ["count"] = candidateRows.Length,
                        ["maxDegrees"] = candidateRows.Length == 0 ? 0 : candidateRows.Max(x => x.MaxRotationErrorDegrees),
                        ["avgTrackMaxDegrees"] = candidateRows.Length == 0 ? 0 : candidateRows.Average(x => x.MaxRotationErrorDegrees),
                        ["bodyGroups"] = new JArray(candidateRows
                            .Select(x => ClassifyBodyGroup(x.UnityPath))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                        ["worstPath"] = candidateRows.Length == 0 ? null : candidateRows[0].UnityPath,
                    };
                })
                .OrderByDescending(x => (int?)x["count"] ?? 0)
                .ThenBy(x => (float?)x["maxDegrees"] ?? 0f)
                .ThenBy(x => (string)x["candidate"], StringComparer.OrdinalIgnoreCase));
        }

        private static JObject BuildFocusedLimbResidualCandidateSummary(
            Dictionary<string, TimelineTrackAccumulator> currentByBone,
            Dictionary<string, TimelineTrackAccumulator> bestLeft,
            Dictionary<string, TimelineTrackAccumulator> bestRight,
            Dictionary<string, string> chosenLeft,
            Dictionary<string, string> chosenRight,
            Dictionary<string, string> humanBoneByPath)
        {
            var rows = new List<JObject>();
            foreach (var pair in currentByBone.OrderBy(x => ResolveHumanBoneForPath(x.Key, humanBoneByPath), StringComparer.OrdinalIgnoreCase))
            {
                var path = pair.Key;
                var humanBone = ResolveHumanBoneForPath(path, humanBoneByPath);
                if (!IsFocusedLimbHumanBone(humanBone))
                {
                    continue;
                }

                var current = pair.Value.ToRow();
                var left = bestLeft.TryGetValue(path, out var leftAccumulator) ? leftAccumulator.ToRow() : null;
                var right = bestRight.TryGetValue(path, out var rightAccumulator) ? rightAccumulator.ToRow() : null;
                var bestSide = ChooseBetterResidualSide(left, right);
                var bestRow = string.Equals(bestSide, "left", StringComparison.OrdinalIgnoreCase) ? left : right;
                var bestCandidate = string.Equals(bestSide, "left", StringComparison.OrdinalIgnoreCase)
                    ? chosenLeft.GetValueOrDefault(path)
                    : chosenRight.GetValueOrDefault(path);

                rows.Add(new JObject
                {
                    ["humanBone"] = humanBone,
                    ["targetPath"] = path,
                    ["side"] = humanBone.StartsWith("Left", StringComparison.OrdinalIgnoreCase) ? "Left" : humanBone.StartsWith("Right", StringComparison.OrdinalIgnoreCase) ? "Right" : "Center",
                    ["bodyGroup"] = ClassifyBodyGroup(path),
                    ["currentMaxDegrees"] = current.MaxRotationErrorDegrees,
                    ["currentAvgDegrees"] = current.AvgRotationErrorDegrees,
                    ["bestApplySide"] = bestSide,
                    ["bestCandidate"] = bestCandidate,
                    ["bestCandidateFamily"] = NormalizeResidualCandidateFamily(bestCandidate),
                    ["bestMaxDegrees"] = bestRow?.MaxRotationErrorDegrees ?? 0f,
                    ["bestAvgDegrees"] = bestRow?.AvgRotationErrorDegrees ?? 0d,
                    ["improvementMaxDegrees"] = current.MaxRotationErrorDegrees - (bestRow?.MaxRotationErrorDegrees ?? current.MaxRotationErrorDegrees),
                    ["improvementAvgDegrees"] = current.AvgRotationErrorDegrees - (bestRow?.AvgRotationErrorDegrees ?? current.AvgRotationErrorDegrees),
                    ["stillLargeAfterBest"] = (bestRow?.MaxRotationErrorDegrees ?? current.MaxRotationErrorDegrees) > 25f,
                });
            }

            var ordered = rows
                .OrderByDescending(x => (float?)x["bestMaxDegrees"] ?? 0f)
                .ThenBy(x => (string)x["humanBone"], StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new JObject
            {
                ["status"] = ordered.Length == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: focuses the residual-candidate oracle on arms and legs. A stable low-error candidate here would justify moving that rest/source-space rule toward production; high error means the Unity formula still needs a different basis, mirror, or parent-space step.",
                ["rowCount"] = ordered.Length,
                ["stillLargeAfterBestCount"] = ordered.Count(x => (bool?)x["stillLargeAfterBest"] == true),
                ["maxBestDegrees"] = ordered.Length == 0 ? 0f : ordered.Max(x => (float?)x["bestMaxDegrees"] ?? 0f),
                ["maxImprovementDegrees"] = ordered.Length == 0 ? 0f : ordered.Max(x => (double?)x["improvementMaxDegrees"] ?? 0d),
                ["byCandidateFamily"] = BuildFocusedResidualCandidateFamilySummary(ordered),
                ["rows"] = new JArray(ordered),
            };
        }

        private static string ResolveHumanBoneForPath(string path, Dictionary<string, string> humanBoneByPath)
        {
            return humanBoneByPath != null && humanBoneByPath.TryGetValue(path ?? string.Empty, out var humanBone)
                ? humanBone
                : path ?? string.Empty;
        }

        private static bool IsFocusedLimbHumanBone(string humanBone)
        {
            return humanBone is not null &&
                (humanBone.Contains("UpperArm", StringComparison.OrdinalIgnoreCase) ||
                 humanBone.Contains("LowerArm", StringComparison.OrdinalIgnoreCase) ||
                 humanBone.Contains("UpperLeg", StringComparison.OrdinalIgnoreCase) ||
                 humanBone.Contains("LowerLeg", StringComparison.OrdinalIgnoreCase) ||
                 humanBone.Contains("Foot", StringComparison.OrdinalIgnoreCase));
        }

        private static string ChooseBetterResidualSide(TrackCompareRow left, TrackCompareRow right)
        {
            if (left == null && right == null)
            {
                return "none";
            }
            if (left == null)
            {
                return "right";
            }
            if (right == null)
            {
                return "left";
            }
            if (left.MaxRotationErrorDegrees < right.MaxRotationErrorDegrees - 0.0001f)
            {
                return "left";
            }
            if (right.MaxRotationErrorDegrees < left.MaxRotationErrorDegrees - 0.0001f)
            {
                return "right";
            }
            return left.AvgRotationErrorDegrees <= right.AvgRotationErrorDegrees ? "left" : "right";
        }

        private static string NormalizeResidualCandidateFamily(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return "none";
            }
            var name = candidate;
            while (name.StartsWith("mirrorXY(", StringComparison.OrdinalIgnoreCase) && name.EndsWith(")", StringComparison.Ordinal))
            {
                name = name.Substring("mirrorXY(".Length, name.Length - "mirrorXY(".Length - 1);
            }
            if (name.Contains("avatarSkeleton", StringComparison.OrdinalIgnoreCase))
            {
                return "avatarSkeletonPose";
            }
            if (name.Contains("avatarDefault", StringComparison.OrdinalIgnoreCase))
            {
                return "avatarDefaultPose";
            }
            if (name.Contains("humanSkeleton", StringComparison.OrdinalIgnoreCase))
            {
                return "humanSkeletonPose";
            }
            if (name.Contains("unityZeroBase", StringComparison.OrdinalIgnoreCase))
            {
                return "unityZeroBaseOracle";
            }
            if (name.Contains("localRest", StringComparison.OrdinalIgnoreCase))
            {
                return "localRest";
            }
            if (name.Contains("parentRest", StringComparison.OrdinalIgnoreCase))
            {
                return "parentRest";
            }
            if (name.Contains("rest", StringComparison.OrdinalIgnoreCase))
            {
                return "rest";
            }
            if (name.Contains("preQ", StringComparison.OrdinalIgnoreCase) || name.Contains("postQ", StringComparison.OrdinalIgnoreCase))
            {
                return "prePostQ";
            }
            return name;
        }

        private static JArray BuildFocusedResidualCandidateFamilySummary(JObject[] rows)
        {
            return new JArray(rows
                .GroupBy(x => (string)x["bestCandidateFamily"] ?? "none", StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var items = group.ToArray();
                    return new JObject
                    {
                        ["candidateFamily"] = group.Key,
                        ["count"] = items.Length,
                        ["maxBestDegrees"] = items.Max(x => (float?)x["bestMaxDegrees"] ?? 0f),
                        ["avgBestDegrees"] = items.Average(x => (float?)x["bestMaxDegrees"] ?? 0f),
                        ["humanBones"] = new JArray(items
                            .Select(x => (string)x["humanBone"])
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                    };
                })
                .OrderByDescending(x => (int?)x["count"] ?? 0)
                .ThenByDescending(x => (float?)x["maxBestDegrees"] ?? 0f)
                .ThenBy(x => (string)x["candidateFamily"], StringComparer.OrdinalIgnoreCase));
        }

        private static JObject BuildDistalStretchTimelineFitSummary(
            JObject[] samples,
            Dictionary<string, List<FloatKey>> curves,
            JObject solver,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex)
        {
            var distalTargets = BuildCurrentSolverTargets()
                .Where(x => string.Equals(x.HumanBone, "LeftLowerArm", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(x.HumanBone, "RightLowerArm", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(x.HumanBone, "LeftLowerLeg", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(x.HumanBone, "RightLowerLeg", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var variants = BuildDistalStretchTimelineVariants();
            var resultRows = new JArray();
            foreach (var variant in variants)
            {
                var accumulators = new Dictionary<string, TimelineTrackAccumulator>(StringComparer.OrdinalIgnoreCase);
                var missingTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var target in distalTargets)
                {
                    if (!TryGetSolverTarget(solverNodes, solverAxes, humanBoneIndex, target, out var targetPath, out var targetAxis))
                    {
                        missingTargets.Add(target.HumanBone);
                        continue;
                    }

                    foreach (var sample in samples)
                    {
                        var time = (float?)sample["time"] ?? 0f;
                        var jointPaths = sample["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
                        var values = sample["values"]?.Values<float>().ToArray() ?? Array.Empty<float>();
                        if (!TryFindJointIndex(jointPaths, targetPath, out var jointIndex))
                        {
                            continue;
                        }

                        var offset = jointIndex * 7;
                        if (offset + 6 >= values.Length)
                        {
                            continue;
                        }

                        var unityRotation = Normalize(new[]
                        {
                            values[offset + 3],
                            values[offset + 4],
                            values[offset + 5],
                            values[offset + 6],
                        });
                        if (!TryBuildDistalStretchTimelineVariantRotation(
                            curves,
                            target,
                            targetAxis,
                            time,
                            variant,
                            solver,
                            solverNodes,
                            humanBoneIndex,
                            targetPath,
                            out var predicted))
                        {
                            missingTargets.Add(target.HumanBone);
                            continue;
                        }
                        if (!accumulators.TryGetValue(targetPath, out var accumulator))
                        {
                            accumulator = new TimelineTrackAccumulator(targetPath, targetPath, -1);
                            accumulators[targetPath] = accumulator;
                        }
                        accumulator.Add(QuaternionAngleDegrees(unityRotation, predicted));
                    }
                }

                var rows = accumulators.Values
                    .Select(x => x.ToRow())
                    .OrderByDescending(x => x.MaxRotationErrorDegrees)
                    .ThenBy(x => x.UnityPath, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                resultRows.Add(new JObject
                {
                    ["name"] = variant.Name,
                    ["description"] = variant.Description,
                    ["status"] = rows.Length == 0 ? "not_available" : "ok",
                    ["matchedTrackCount"] = rows.Length,
                    ["missingTargets"] = new JArray(missingTargets.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                    ["stretchTargetAxis"] = AvatarAxisName(variant.StretchTargetAxis),
                    ["stretchSign"] = variant.StretchSign,
                    ["swingMode"] = variant.SwingMode,
                    ["composeMode"] = variant.ComposeMode,
                    ["poseAxisCandidate"] = variant.PoseAxisCandidate,
                    ["poseApplyMode"] = variant.PoseApplyMode,
                    ["maxDegrees"] = rows.Length == 0 ? 0 : rows[0].MaxRotationErrorDegrees,
                    ["avgTrackMaxDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.MaxRotationErrorDegrees),
                    ["avgTrackAvgDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.AvgRotationErrorDegrees),
                    ["bodyGroupError"] = BuildBodyGroupErrorSummary(rows),
                    ["topErrors"] = ToJsonRows(rows.Take(8)),
                });
            }

            return new JObject
            {
                ["status"] = resultRows.Count == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: evaluates Forearm/Lower-Leg Stretch target axis, sign, and swing composition on the full Unity GetInternalAvatarPose timeline while keeping the current proximal twist rule. This directly targets visible forearm and calf bending errors.",
                ["variants"] = new JArray(resultRows
                    .OfType<JObject>()
                    .OrderBy(x => (float?)x["avgTrackMaxDegrees"] ?? float.MaxValue)
                    .ThenBy(x => (float?)x["maxDegrees"] ?? float.MaxValue)
                    .ThenBy(x => (string)x["name"], StringComparer.OrdinalIgnoreCase)),
            };
        }

        private static DistalStretchTimelineVariant[] BuildDistalStretchTimelineVariants()
        {
            var variants = new List<DistalStretchTimelineVariant>
            {
                new(
                    "current_stretch_z_pos_vector_swing_twist",
                    "Current production rule for forearm/lower-leg stretch: Stretch writes Avatar Z/swing, vector swing then twist.",
                    2,
                    1,
                    "vector",
                    "swing_twist"),
            };
            foreach (var candidateName in new[]
            {
                "avatarDefaultSameIndexParentLocalPose",
                "avatarDefaultHumanSkeletonIndexParentLocalPose",
                "inverse(avatarDefaultPoseByHumanSkeletonIndex)",
                "avatarSkeletonLocalPoseBySameIndex",
            })
            {
                foreach (var applyMode in new[] { "left_delta", "right_delta", "middle_swing_twist", "middle_twist_swing" })
                {
                    variants.Add(new DistalStretchTimelineVariant(
                        $"pose_axis_stretch_{SanitizeVariantName(candidateName)}__{applyMode}",
                        $"Diagnostic only: Forearm/Lower-Leg Stretch uses {candidateName} to tilt the signed -Z stretch axis, apply={applyMode}.",
                        2,
                        1,
                        "pose_axis",
                        "swing_twist",
                        candidateName,
                        applyMode));
                }
            }
            foreach (var stretchAxis in new[] { 0, 1, 2 })
            {
                foreach (var stretchSign in new[] { 1, -1 })
                {
                    foreach (var swingMode in new[] { "vector", "y_then_z", "z_then_y", "ellipse_clamped", "ellipse_scaled" })
                    {
                        foreach (var composeMode in new[] { "swing_twist", "twist_swing" })
                        {
                            var name = $"stretch_{AvatarAxisShortName(stretchAxis)}_{SignName(stretchSign)}__{swingMode}__{composeMode}";
                            if (variants.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                            {
                                continue;
                            }
                            variants.Add(new DistalStretchTimelineVariant(
                                name,
                                $"Distal stretch diagnostic: Stretch writes {AvatarAxisName(stretchAxis)} ({SignName(stretchSign)}), swingMode={swingMode}, composeMode={composeMode}.",
                                stretchAxis,
                                stretchSign,
                                swingMode,
                                composeMode));
                        }
                    }
                }
            }

            return variants.ToArray();
        }

        private static string SanitizeVariantName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "unknown";
            }
            var chars = name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray();
            return new string(chars).Trim('_');
        }

        private static bool TryBuildDistalStretchTimelineVariantRotation(
            Dictionary<string, List<FloatKey>> curves,
            SolverTarget target,
            SolverAxis axis,
            float time,
            DistalStretchTimelineVariant variant,
            JObject solver,
            JArray solverNodes,
            JArray humanBoneIndex,
            string targetPath,
            out float[] rotation)
        {
            rotation = null;
            if (string.Equals(variant.SwingMode, "pose_axis", StringComparison.OrdinalIgnoreCase))
            {
                if (solver == null ||
                    string.IsNullOrWhiteSpace(variant.PoseAxisCandidate) ||
                    !TryResolveDistalStretchPoseAxis(variant.PoseAxisCandidate, target, targetPath, solver, solverNodes, humanBoneIndex, out var stretchAxis))
                {
                    return false;
                }

                rotation = BuildPoseAxisDistalStretchRotation(curves, target, axis, time, variant, stretchAxis);
                return true;
            }

            var angles = new float[3];
            var stretch = SampleFloatCurve(curves.TryGetValue(target.XAttribute ?? string.Empty, out var stretchCurve) ? stretchCurve : null, time);
            angles[variant.StretchTargetAxis] += LimitMuscle(stretch * variant.StretchSign, axis, variant.StretchTargetAxis);

            var twist = SampleFloatCurve(curves.TryGetValue(target.ZAttribute ?? string.Empty, out var twistCurve) ? twistCurve : null, time);
            angles[0] += LimitMuscle(twist, axis, 0);

            var pre = Normalize(axis.PreQ);
            var post = Normalize(axis.PostQ);
            var swingVariant = new SolverVariant(
                variant.Name,
                variant.Description,
                Array.Empty<SolverTarget>(),
                variant.ComposeMode,
                SwingMode: variant.SwingMode == "vector" ? null : variant.SwingMode);
            var swing = BuildSwingQuaternion(angles, axis, swingVariant);
            var twistRotation = AxisAngleRadiansToQuaternion(1, 0, 0, angles[0]);
            var middle = string.Equals(variant.ComposeMode, "twist_swing", StringComparison.OrdinalIgnoreCase)
                ? Multiply(twistRotation, swing)
                : Multiply(swing, twistRotation);
            rotation = Normalize(Multiply(Multiply(pre, middle), Inverse(post)));
            return true;
        }

        private static bool TryResolveDistalStretchPoseAxis(
            string candidateName,
            SolverTarget target,
            string targetPath,
            JObject solver,
            JArray solverNodes,
            JArray humanBoneIndex,
            out float[] axis)
        {
            axis = null;
            var candidates = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            AddAvatarPoseCorrectionCandidates(candidates, targetPath, solver, solverNodes, humanBoneIndex, target.HumanBone);
            if (!candidates.TryGetValue(candidateName, out var pose))
            {
                return false;
            }

            axis = NormalizeVector3(RotateVectorByQuaternion(new[] { 0f, 0f, -1f }, pose));
            return true;
        }

        private static float[] BuildPoseAxisDistalStretchRotation(
            Dictionary<string, List<FloatKey>> curves,
            SolverTarget target,
            SolverAxis axis,
            float time,
            DistalStretchTimelineVariant variant,
            float[] stretchAxis)
        {
            var stretch = SampleFloatCurve(curves.TryGetValue(target.XAttribute ?? string.Empty, out var stretchCurve) ? stretchCurve : null, time);
            var twist = SampleFloatCurve(curves.TryGetValue(target.ZAttribute ?? string.Empty, out var twistCurve) ? twistCurve : null, time);
            var stretchAngle = LimitMuscle(stretch * variant.StretchSign, axis, variant.StretchTargetAxis);
            var twistAngle = LimitMuscle(twist, axis, 0);
            var stretchDelta = AxisAngleRadiansToQuaternion(stretchAxis[0], stretchAxis[1], stretchAxis[2], stretchAngle);
            var twistRotation = AxisAngleRadiansToQuaternion(1, 0, 0, twistAngle);
            var pre = Normalize(axis.PreQ);
            var post = Normalize(axis.PostQ);

            if (string.Equals(variant.PoseApplyMode, "left_delta", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(variant.PoseApplyMode, "right_delta", StringComparison.OrdinalIgnoreCase))
            {
                var baseRotation = Normalize(Multiply(Multiply(pre, twistRotation), Inverse(post)));
                return string.Equals(variant.PoseApplyMode, "right_delta", StringComparison.OrdinalIgnoreCase)
                    ? Normalize(Multiply(baseRotation, stretchDelta))
                    : Normalize(Multiply(stretchDelta, baseRotation));
            }

            var middle = string.Equals(variant.PoseApplyMode, "middle_twist_swing", StringComparison.OrdinalIgnoreCase)
                ? Multiply(twistRotation, stretchDelta)
                : Multiply(stretchDelta, twistRotation);
            return Normalize(Multiply(Multiply(pre, middle), Inverse(post)));
        }

        private static float[] BuildArmTwistTimelineVariantRotation(
            Dictionary<string, List<FloatKey>> curves,
            SolverTarget target,
            SolverAxis targetAxis,
            float time,
            ArmTwistTimelineVariant variant,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex)
        {
            if (variant.ComposeMode?.StartsWith("readiness_left_z_right_x_to_y_", StringComparison.OrdinalIgnoreCase) == true)
            {
                return BuildReadinessArmTwistTimelineRotation(curves, target, targetAxis, time, variant, solverNodes, solverAxes, humanBoneIndex);
            }
            if (variant.ComposeMode?.StartsWith("oracle_pattern_local_delta_", StringComparison.OrdinalIgnoreCase) == true)
            {
                return BuildOraclePatternArmTwistRotation(curves, target, targetAxis, time, variant);
            }
            if (variant.ComposeMode?.StartsWith("single_formula_", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (variant.ComposeMode.StartsWith("single_formula_mixed_", StringComparison.OrdinalIgnoreCase))
                {
                    return BuildMixedSideSingleFormulaArmTwistRotation(curves, target, targetAxis, time, variant, solverNodes, solverAxes, humanBoneIndex);
                }
                if (variant.ComposeMode.StartsWith("single_formula_zero_", StringComparison.OrdinalIgnoreCase))
                {
                    return BuildZeroPoseSingleFormulaArmTwistRotation(curves, target, targetAxis, time, variant, solverNodes, solverAxes, humanBoneIndex);
                }
                return BuildSingleFormulaArmTwistRotation(curves, target, targetAxis, time, variant, solverNodes, solverAxes, humanBoneIndex);
            }

            var angles = new float[3];
            var stretch = SampleFloatCurve(curves.TryGetValue(target.XAttribute ?? string.Empty, out var stretchCurve) ? stretchCurve : null, time);
            angles[2] += LimitMuscle(stretch, targetAxis, 2);

            var twist = SampleFloatCurve(curves.TryGetValue(target.ZAttribute ?? string.Empty, out var twistCurve) ? twistCurve : null, time);
            if (MathF.Abs(twist) > 0.0000001f)
            {
                var sourceHumanBone = ResolveArmTwistSourceHumanBone(target.HumanBone, variant.SourceKind);
                var sourceAxis = targetAxis;
                if (!string.Equals(sourceHumanBone, target.HumanBone, StringComparison.OrdinalIgnoreCase) &&
                    !TryGetHumanBonePathAxis(solverNodes, solverAxes, humanBoneIndex, sourceHumanBone, out _, out sourceAxis))
                {
                    sourceAxis = targetAxis;
                }
                angles[variant.TargetAxis] += LimitMuscle(twist, sourceAxis, variant.SourceAxis);
            }

            var pre = Normalize(targetAxis.PreQ);
            var post = Normalize(targetAxis.PostQ);
            var swing = SwingRadiansToQuaternion(angles[1], angles[2]);
            var twistRotation = AxisAngleRadiansToQuaternion(1, 0, 0, angles[0]);
            var middle = string.Equals(variant.ComposeMode, "twist_swing", StringComparison.OrdinalIgnoreCase)
                ? Multiply(twistRotation, swing)
                : Multiply(swing, twistRotation);
            var rotation = Normalize(Multiply(Multiply(pre, middle), Inverse(post)));
            return ApplyVariantMirror(rotation, target.HumanBone, variant.MirrorMode);
        }

        private static float[] BuildReadinessArmTwistTimelineRotation(
            Dictionary<string, List<FloatKey>> curves,
            SolverTarget target,
            SolverAxis targetAxis,
            float time,
            ArmTwistTimelineVariant variant,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex)
        {
            var angles = new float[3];
            var stretch = SampleFloatCurve(curves.TryGetValue(target.XAttribute ?? string.Empty, out var stretchCurve) ? stretchCurve : null, time);
            angles[2] += LimitMuscle(stretch, targetAxis, 2);

            var twist = SampleFloatCurve(curves.TryGetValue(target.ZAttribute ?? string.Empty, out var twistCurve) ? twistCurve : null, time);
            if (MathF.Abs(twist) > 0.0000001f)
            {
                var isLeft = target.HumanBone.StartsWith("Left", StringComparison.OrdinalIgnoreCase);
                var sourceAxisIndex = isLeft ? 2 : 0;
                var sourceHumanBone = ResolveArmTwistSourceHumanBone(target.HumanBone, variant.SourceKind);
                var sourceAxis = targetAxis;
                if (!string.Equals(sourceHumanBone, target.HumanBone, StringComparison.OrdinalIgnoreCase) &&
                    !TryGetHumanBonePathAxis(solverNodes, solverAxes, humanBoneIndex, sourceHumanBone, out _, out sourceAxis))
                {
                    sourceAxis = targetAxis;
                }
                angles[1] += LimitMuscle(twist, sourceAxis, sourceAxisIndex);
            }

            var pre = Normalize(targetAxis.PreQ);
            var post = Normalize(targetAxis.PostQ);
            var swing = SwingRadiansToQuaternion(angles[1], angles[2]);
            var twistRotation = AxisAngleRadiansToQuaternion(1, 0, 0, angles[0]);
            var middle = variant.ComposeMode.EndsWith("_twist_swing", StringComparison.OrdinalIgnoreCase)
                ? Multiply(twistRotation, swing)
                : Multiply(swing, twistRotation);
            return Normalize(Multiply(Multiply(pre, middle), Inverse(post)));
        }

        private static float[] BuildMixedSideSingleFormulaArmTwistRotation(
            Dictionary<string, List<FloatKey>> curves,
            SolverTarget target,
            SolverAxis targetAxis,
            float time,
            ArmTwistTimelineVariant variant,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex)
        {
            var mode = ParseMixedSideSingleFormulaMode(variant.ComposeMode);
            var zeroRotation = Normalize(Multiply(Normalize(targetAxis.PreQ), Inverse(Normalize(targetAxis.PostQ))));
            var stretch = SampleFloatCurve(curves.TryGetValue(target.XAttribute ?? string.Empty, out var stretchCurve) ? stretchCurve : null, time);
            var twist = SampleFloatCurve(curves.TryGetValue(target.ZAttribute ?? string.Empty, out var twistCurve) ? twistCurve : null, time);

            var stretchAngle = LimitMuscle(stretch, targetAxis, 2);
            var stretchDelta = BuildSingleFormulaDelta(targetAxis, 2, stretchAngle, mode.StretchFormula, mode.UseRightStretchDelta);

            var sourceHumanBone = ResolveArmTwistSourceHumanBone(target.HumanBone, variant.SourceKind);
            var sourceAxis = targetAxis;
            if (!string.Equals(sourceHumanBone, target.HumanBone, StringComparison.OrdinalIgnoreCase) &&
                !TryGetHumanBonePathAxis(solverNodes, solverAxes, humanBoneIndex, sourceHumanBone, out _, out sourceAxis))
            {
                sourceAxis = targetAxis;
            }

            var twistAngle = LimitMuscle(twist, sourceAxis, variant.SourceAxis);
            var twistDelta = BuildSingleFormulaDelta(targetAxis, variant.TargetAxis, twistAngle, mode.TwistFormula, mode.UseRightTwistDelta);
            var composed = ComposeDeltas(new[] { stretchDelta, twistDelta }, new[] { 0, 1 }, mode.Append);
            var rotation = mode.OutputRight
                ? Normalize(Multiply(zeroRotation, composed))
                : Normalize(Multiply(composed, zeroRotation));
            return ApplyVariantMirror(rotation, target.HumanBone, variant.MirrorMode);
        }

        private static (bool OutputRight, bool Append, bool UseRightStretchDelta, bool UseRightTwistDelta, string StretchFormula, string TwistFormula) ParseMixedSideSingleFormulaMode(string composeMode)
        {
            const string prefix = "single_formula_mixed_";
            var body = composeMode?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true
                ? composeMode.Substring(prefix.Length)
                : string.Empty;
            var outputRight = body.StartsWith("right_", StringComparison.OrdinalIgnoreCase);
            body = outputRight || body.StartsWith("left_", StringComparison.OrdinalIgnoreCase)
                ? body.Substring(body.IndexOf('_') + 1)
                : body;
            var append = body.StartsWith("append_", StringComparison.OrdinalIgnoreCase);
            body = append || body.StartsWith("prepend_", StringComparison.OrdinalIgnoreCase)
                ? body.Substring(body.IndexOf('_') + 1)
                : body;
            var useRightStretch = body.StartsWith("stretchright_", StringComparison.OrdinalIgnoreCase);
            if (body.StartsWith("stretchleft_", StringComparison.OrdinalIgnoreCase) ||
                body.StartsWith("stretchright_", StringComparison.OrdinalIgnoreCase))
            {
                body = body.Substring(body.IndexOf('_') + 1);
            }
            var useRightTwist = body.StartsWith("twistright_", StringComparison.OrdinalIgnoreCase);
            if (body.StartsWith("twistleft_", StringComparison.OrdinalIgnoreCase) ||
                body.StartsWith("twistright_", StringComparison.OrdinalIgnoreCase))
            {
                body = body.Substring(body.IndexOf('_') + 1);
            }
            var parts = body.Split(new[] { "__" }, StringSplitOptions.None);
            var stretchFormula = parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0])
                ? parts[0]
                : "pre_middle_inversePost";
            var twistFormula = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])
                ? parts[1]
                : "inversePost_middle_post";
            return (outputRight, append, useRightStretch, useRightTwist, stretchFormula, twistFormula);
        }

        private static float[] BuildZeroPoseSingleFormulaArmTwistRotation(
            Dictionary<string, List<FloatKey>> curves,
            SolverTarget target,
            SolverAxis targetAxis,
            float time,
            ArmTwistTimelineVariant variant,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex)
        {
            var mode = ParseZeroPoseSingleFormulaMode(variant.ComposeMode);
            var zeroRotation = Normalize(Multiply(Normalize(targetAxis.PreQ), Inverse(Normalize(targetAxis.PostQ))));
            var stretch = SampleFloatCurve(curves.TryGetValue(target.XAttribute ?? string.Empty, out var stretchCurve) ? stretchCurve : null, time);
            var twist = SampleFloatCurve(curves.TryGetValue(target.ZAttribute ?? string.Empty, out var twistCurve) ? twistCurve : null, time);

            var stretchAngle = LimitMuscle(stretch, targetAxis, 2);
            var stretchDelta = BuildSingleFormulaDelta(targetAxis, 2, stretchAngle, mode.StretchFormula, mode.UseRightDelta);

            var sourceHumanBone = ResolveArmTwistSourceHumanBone(target.HumanBone, variant.SourceKind);
            var sourceAxis = targetAxis;
            if (!string.Equals(sourceHumanBone, target.HumanBone, StringComparison.OrdinalIgnoreCase) &&
                !TryGetHumanBonePathAxis(solverNodes, solverAxes, humanBoneIndex, sourceHumanBone, out _, out sourceAxis))
            {
                sourceAxis = targetAxis;
            }

            var twistAngle = LimitMuscle(twist, sourceAxis, variant.SourceAxis);
            var twistDelta = BuildSingleFormulaDelta(targetAxis, variant.TargetAxis, twistAngle, mode.TwistFormula, mode.UseRightDelta);
            var composed = ComposeDeltas(new[] { stretchDelta, twistDelta }, new[] { 0, 1 }, mode.Append);
            var rotation = mode.UseRightDelta
                ? Normalize(Multiply(zeroRotation, composed))
                : Normalize(Multiply(composed, zeroRotation));
            return ApplyVariantMirror(rotation, target.HumanBone, variant.MirrorMode);
        }

        private static float[] BuildSingleFormulaDelta(
            SolverAxis targetAxis,
            int targetAvatarAxis,
            float angle,
            string formulaName,
            bool useRightDelta)
        {
            var singleBase = BuildSingleMuscleFormulaCandidateRotation(targetAxis, targetAvatarAxis, 0f, formulaName);
            var singlePredicted = BuildSingleMuscleFormulaCandidateRotation(targetAxis, targetAvatarAxis, angle, formulaName);
            return useRightDelta
                ? Normalize(Multiply(Inverse(singleBase), singlePredicted))
                : Normalize(Multiply(singlePredicted, Inverse(singleBase)));
        }

        private static (bool UseRightDelta, bool Append, string StretchFormula, string TwistFormula) ParseZeroPoseSingleFormulaMode(string composeMode)
        {
            const string prefix = "single_formula_zero_";
            var body = composeMode?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true
                ? composeMode.Substring(prefix.Length)
                : string.Empty;
            var useRight = body.StartsWith("right_", StringComparison.OrdinalIgnoreCase);
            body = useRight || body.StartsWith("left_", StringComparison.OrdinalIgnoreCase)
                ? body.Substring(body.IndexOf('_') + 1)
                : body;
            var append = body.StartsWith("append_", StringComparison.OrdinalIgnoreCase);
            body = append || body.StartsWith("prepend_", StringComparison.OrdinalIgnoreCase)
                ? body.Substring(body.IndexOf('_') + 1)
                : body;
            var parts = body.Split(new[] { "__" }, StringSplitOptions.None);
            var stretchFormula = parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0])
                ? parts[0]
                : "pre_middle_inversePost";
            var twistFormula = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])
                ? parts[1]
                : "inversePost_middle_post";
            return (useRight, append, stretchFormula, twistFormula);
        }

        private static float[] BuildSingleFormulaArmTwistRotation(
            Dictionary<string, List<FloatKey>> curves,
            SolverTarget target,
            SolverAxis targetAxis,
            float time,
            ArmTwistTimelineVariant variant,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex)
        {
            var stretch = SampleFloatCurve(curves.TryGetValue(target.XAttribute ?? string.Empty, out var stretchCurve) ? stretchCurve : null, time);
            var baseAngles = new float[3];
            baseAngles[2] += LimitMuscle(stretch, targetAxis, 2);

            var pre = Normalize(targetAxis.PreQ);
            var post = Normalize(targetAxis.PostQ);
            var baseRotation = Normalize(Multiply(Multiply(pre, SwingRadiansToQuaternion(baseAngles[1], baseAngles[2])), Inverse(post)));
            var twist = SampleFloatCurve(curves.TryGetValue(target.ZAttribute ?? string.Empty, out var twistCurve) ? twistCurve : null, time);
            if (MathF.Abs(twist) <= 0.0000001f)
            {
                return baseRotation;
            }

            var sourceHumanBone = ResolveArmTwistSourceHumanBone(target.HumanBone, variant.SourceKind);
            var sourceAxis = targetAxis;
            if (!string.Equals(sourceHumanBone, target.HumanBone, StringComparison.OrdinalIgnoreCase) &&
                !TryGetHumanBonePathAxis(solverNodes, solverAxes, humanBoneIndex, sourceHumanBone, out _, out sourceAxis))
            {
                sourceAxis = targetAxis;
            }

            var angle = LimitMuscle(twist, sourceAxis, variant.SourceAxis);
            var formulaName = ResolveSingleFormulaName(variant.ComposeMode);
            var singleBase = BuildSingleMuscleFormulaCandidateRotation(targetAxis, variant.TargetAxis, 0f, formulaName);
            var singlePredicted = BuildSingleMuscleFormulaCandidateRotation(targetAxis, variant.TargetAxis, angle, formulaName);
            var useRightDelta = variant.ComposeMode.IndexOf("_right_", StringComparison.OrdinalIgnoreCase) >= 0;
            var delta = useRightDelta
                ? Normalize(Multiply(Inverse(singleBase), singlePredicted))
                : Normalize(Multiply(singlePredicted, Inverse(singleBase)));
            var rotation = useRightDelta
                ? Normalize(Multiply(baseRotation, delta))
                : Normalize(Multiply(delta, baseRotation));
            return ApplyVariantMirror(rotation, target.HumanBone, variant.MirrorMode);
        }

        private static string ResolveSingleFormulaName(string composeMode)
        {
            const string leftPrefix = "single_formula_left_";
            const string rightPrefix = "single_formula_right_";
            if (composeMode?.StartsWith(leftPrefix, StringComparison.OrdinalIgnoreCase) == true)
            {
                return composeMode.Substring(leftPrefix.Length);
            }
            if (composeMode?.StartsWith(rightPrefix, StringComparison.OrdinalIgnoreCase) == true)
            {
                return composeMode.Substring(rightPrefix.Length);
            }
            return "pre_middle_inversePost";
        }

        private static bool TryBuildArmTwistTimelineVariantRotation(
            Dictionary<string, List<FloatKey>> curves,
            SolverTarget target,
            SolverAxis targetAxis,
            float time,
            ArmTwistTimelineVariant variant,
            JObject solver,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex,
            string targetPath,
            out float[] rotation)
        {
            rotation = null;
            if (variant.ComposeMode?.StartsWith("pose_axis_local_delta_", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (solver == null ||
                    string.IsNullOrWhiteSpace(variant.PoseAxisCandidate) ||
                    !TryResolveArmTwistPoseAxis(variant.PoseAxisCandidate, target, targetPath, solver, solverNodes, humanBoneIndex, out var poseAxis))
                {
                    return false;
                }

                rotation = BuildPoseAxisArmTwistRotation(curves, target, targetAxis, time, variant, solverNodes, solverAxes, humanBoneIndex, poseAxis);
                return true;
            }

            rotation = BuildArmTwistTimelineVariantRotation(curves, target, targetAxis, time, variant, solverNodes, solverAxes, humanBoneIndex);
            return true;
        }

        private static bool TryResolveArmTwistPoseAxis(
            string candidateName,
            SolverTarget target,
            string targetPath,
            JObject solver,
            JArray solverNodes,
            JArray humanBoneIndex,
            out float[] axis)
        {
            axis = null;
            var candidates = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            AddAvatarPoseCorrectionCandidates(candidates, targetPath, solver, solverNodes, humanBoneIndex, target.HumanBone);
            if (!candidates.TryGetValue(candidateName, out var pose))
            {
                return false;
            }

            var signedBaseAxis = target.HumanBone.StartsWith("Left", StringComparison.OrdinalIgnoreCase)
                ? new[] { -1f, 0f, 0f }
                : new[] { 1f, 0f, 0f };
            axis = NormalizeVector3(RotateVectorByQuaternion(signedBaseAxis, pose));
            return true;
        }

        private static float[] BuildPoseAxisArmTwistRotation(
            Dictionary<string, List<FloatKey>> curves,
            SolverTarget target,
            SolverAxis targetAxis,
            float time,
            ArmTwistTimelineVariant variant,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex,
            float[] poseAxis)
        {
            var stretch = SampleFloatCurve(curves.TryGetValue(target.XAttribute ?? string.Empty, out var stretchCurve) ? stretchCurve : null, time);
            var baseAngles = new float[3];
            baseAngles[2] += LimitMuscle(stretch, targetAxis, 2);

            var pre = Normalize(targetAxis.PreQ);
            var post = Normalize(targetAxis.PostQ);
            var baseRotation = Normalize(Multiply(Multiply(pre, SwingRadiansToQuaternion(baseAngles[1], baseAngles[2])), Inverse(post)));
            var twist = SampleFloatCurve(curves.TryGetValue(target.ZAttribute ?? string.Empty, out var twistCurve) ? twistCurve : null, time);
            if (MathF.Abs(twist) <= 0.0000001f)
            {
                return baseRotation;
            }

            var sourceHumanBone = ResolveArmTwistSourceHumanBone(target.HumanBone, variant.SourceKind);
            var sourceAxis = targetAxis;
            if (!string.Equals(sourceHumanBone, target.HumanBone, StringComparison.OrdinalIgnoreCase) &&
                !TryGetHumanBonePathAxis(solverNodes, solverAxes, humanBoneIndex, sourceHumanBone, out _, out sourceAxis))
            {
                sourceAxis = targetAxis;
            }

            var angle = LimitMuscle(twist, sourceAxis, variant.SourceAxis);
            var delta = AxisAngleRadiansToQuaternion(poseAxis[0], poseAxis[1], poseAxis[2], angle);
            var rotation = variant.ComposeMode.EndsWith("_right", StringComparison.OrdinalIgnoreCase)
                ? Normalize(Multiply(baseRotation, delta))
                : Normalize(Multiply(delta, baseRotation));
            return ApplyVariantMirror(rotation, target.HumanBone, variant.MirrorMode);
        }

        private static float[] BuildOraclePatternArmTwistRotation(
            Dictionary<string, List<FloatKey>> curves,
            SolverTarget target,
            SolverAxis targetAxis,
            float time,
            ArmTwistTimelineVariant variant)
        {
            var stretch = SampleFloatCurve(curves.TryGetValue(target.XAttribute ?? string.Empty, out var stretchCurve) ? stretchCurve : null, time);
            var baseAngles = new float[3];
            baseAngles[2] += LimitMuscle(stretch, targetAxis, 2);

            var pre = Normalize(targetAxis.PreQ);
            var post = Normalize(targetAxis.PostQ);
            var baseRotation = Normalize(Multiply(Multiply(pre, SwingRadiansToQuaternion(baseAngles[1], baseAngles[2])), Inverse(post)));
            var twist = SampleFloatCurve(curves.TryGetValue(target.ZAttribute ?? string.Empty, out var twistCurve) ? twistCurve : null, time);
            if (MathF.Abs(twist) <= 0.0000001f)
            {
                return baseRotation;
            }

            // 诊断用常量来自 NPC/Gorou Unity oracle：Arm Twist 最近轴仍是 Left=-X / Right=+X，
            // 但都带稳定负 Z 倾斜。这里用完整 timeline 验证“倾斜轴”是否是主缺口。
            var isLeft = target.HumanBone.StartsWith("Left", StringComparison.OrdinalIgnoreCase);
            var patternAxis = NormalizeVector3(new[]
            {
                isLeft ? -0.898922f : 0.904207f,
                0f,
                isLeft ? -0.434765f : -0.426220f,
            });
            var angle = LimitMuscle(twist, targetAxis, 0);
            var delta = AxisAngleRadiansToQuaternion(patternAxis[0], patternAxis[1], patternAxis[2], angle);
            var rotation = variant.ComposeMode.EndsWith("_right", StringComparison.OrdinalIgnoreCase)
                ? Normalize(Multiply(baseRotation, delta))
                : Normalize(Multiply(delta, baseRotation));
            return ApplyVariantMirror(rotation, target.HumanBone, variant.MirrorMode);
        }

        private static float[] BuildHandTwistTimelineVariantRotation(
            Dictionary<string, List<FloatKey>> curves,
            SolverTarget target,
            SolverAxis targetAxis,
            float time,
            ArmTwistTimelineVariant variant,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex)
        {
            var angles = new float[3];
            var downUp = SampleFloatCurve(curves.TryGetValue(target.XAttribute ?? string.Empty, out var xCurve) ? xCurve : null, time);
            var inOut = SampleFloatCurve(curves.TryGetValue(target.YAttribute ?? string.Empty, out var yCurve) ? yCurve : null, time);
            angles[2] += LimitMuscle(downUp, targetAxis, 2);
            angles[1] += LimitMuscle(inOut, targetAxis, 1);

            var twist = SampleFloatCurve(curves.TryGetValue(target.ZAttribute ?? string.Empty, out var twistCurve) ? twistCurve : null, time);
            if (MathF.Abs(twist) > 0.0000001f)
            {
                var sourceHumanBone = ResolveHandTwistSourceHumanBone(target.HumanBone, variant.SourceKind);
                var sourceAxis = targetAxis;
                if (!string.Equals(sourceHumanBone, target.HumanBone, StringComparison.OrdinalIgnoreCase) &&
                    !TryGetHumanBonePathAxis(solverNodes, solverAxes, humanBoneIndex, sourceHumanBone, out _, out sourceAxis))
                {
                    sourceAxis = targetAxis;
                }
                angles[variant.TargetAxis] += LimitMuscle(twist, sourceAxis, variant.SourceAxis);
            }

            var pre = Normalize(targetAxis.PreQ);
            var post = Normalize(targetAxis.PostQ);
            return Normalize(Multiply(Multiply(pre, Multiply(SwingRadiansToQuaternion(angles[1], angles[2]), AxisAngleRadiansToQuaternion(1, 0, 0, angles[0]))), Inverse(post)));
        }

        private static string ResolveArmTwistSourceHumanBone(string targetHumanBone, string sourceKind)
        {
            if (string.Equals(sourceKind, "upper", StringComparison.OrdinalIgnoreCase))
            {
                return targetHumanBone.StartsWith("Left", StringComparison.OrdinalIgnoreCase) ? "LeftUpperArm" : "RightUpperArm";
            }
            return targetHumanBone;
        }

        private static string ResolveHandTwistSourceHumanBone(string targetHumanBone, string sourceKind)
        {
            if (string.Equals(sourceKind, "forearm", StringComparison.OrdinalIgnoreCase))
            {
                return targetHumanBone.StartsWith("Left", StringComparison.OrdinalIgnoreCase) ? "LeftLowerArm" : "RightLowerArm";
            }
            return targetHumanBone;
        }

        private static string AvatarAxisShortName(int axis) => axis switch
        {
            0 => "x",
            1 => "y",
            2 => "z",
            _ => "unknown",
        };

        private static JObject BuildSingleMuscleProbeSolverComparison(JObject request, JObject unityResult)
        {
            var solver = request?["animeStudioAssets"]?["model"]?["avatar"]?["internalSolver"] as JObject;
            var solverNodes = solver?["skeleton"]?["nodes"] as JArray;
            var solverAxes = solver?["skeleton"]?["axes"] as JArray;
            var humanBoneIndex = solver?["humanBoneIndex"] as JArray;
            var probes = unityResult?["muscleProbes"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var internalAvatarPoseProbes = unityResult?["internalAvatarPoseMuscleProbes"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var combinationProbes = unityResult?["muscleCombinationProbes"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var internalAvatarPoseCombinationProbes = unityResult?["internalAvatarPoseMuscleCombinationProbes"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            if (solver == null || solverNodes == null || solverAxes == null || humanBoneIndex == null || probes.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["hasInternalSolver"] = solver != null,
                    ["probeCount"] = probes.Length,
                };
            }

            var probeByNameAndValue = probes
                .Where(x => !string.IsNullOrWhiteSpace((string)x["muscleName"]))
                .GroupBy(x => $"{(string)x["muscleName"]}|{((float?)x["value"] ?? 0f).ToString("R", CultureInfo.InvariantCulture)}", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            var variant = new SolverVariant(
                "current_swing_twist",
                "Current preview formula checked against Unity one-muscle SetHumanPose probes.",
                BuildCurrentSolverTargets(),
                "swing_twist");
            var rows = new List<TrackCompareRow>();
            var correctionRows = new List<SingleMuscleDeltaCorrectionRow>();
            var missingProbes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var missingTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var target in variant.Targets)
            {
                if (!TryGetSolverTarget(solverNodes, solverAxes, humanBoneIndex, target, out var targetPath, out var axis))
                {
                    missingTargets.Add(target.HumanBone);
                    continue;
                }

                foreach (var attribute in new[] { target.XAttribute, target.YAttribute, target.ZAttribute, target.ExtraZAttribute }
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    foreach (var value in new[] { -1f, 1f })
                    {
                        var key = $"{attribute}|{value.ToString("R", CultureInfo.InvariantCulture)}";
                        if (!probeByNameAndValue.TryGetValue(key, out var probe))
                        {
                            missingProbes.Add(key);
                            continue;
                        }
                        if (!TryReadProbeRotation(probe, targetPath, out var probePath, out var unityBaseRotation, out var unityRotation))
                        {
                            missingTargets.Add($"{targetPath}|{attribute}|{value.ToString("R", CultureInfo.InvariantCulture)}");
                            continue;
                        }

                        var baseValue = (float?)probe["baseValue"] ?? 0f;
                        var baseCurves = new Dictionary<string, List<FloatKey>>(StringComparer.OrdinalIgnoreCase)
                        {
                            [attribute] = new List<FloatKey> { new FloatKey(0f, baseValue) },
                        };
                        var curves = new Dictionary<string, List<FloatKey>>(StringComparer.OrdinalIgnoreCase)
                        {
                            [attribute] = new List<FloatKey> { new FloatKey(0f, value) },
                        };
                        var predictedBase = BuildVariantSolverRotation(
                            baseCurves,
                            target,
                            axis,
                            0f,
                            variant,
                            solver,
                            solverNodes,
                            humanBoneIndex,
                            targetPath,
                            gltfNodes: null,
                            nodePathToIndex: null);
                        var predicted = BuildVariantSolverRotation(
                            curves,
                            target,
                            axis,
                            0f,
                            variant,
                            solver,
                            solverNodes,
                            humanBoneIndex,
                            targetPath,
                            gltfNodes: null,
                            nodePathToIndex: null);
                        var predictedDeltaLeft = Normalize(Multiply(predicted, Inverse(predictedBase)));
                        var unityDeltaLeft = Normalize(Multiply(unityRotation, Inverse(unityBaseRotation)));
                        var predictedDeltaRight = Normalize(Multiply(Inverse(predictedBase), predicted));
                        var unityDeltaRight = Normalize(Multiply(Inverse(unityBaseRotation), unityRotation));
                        var leftError = QuaternionAngleDegrees(predictedDeltaLeft, unityDeltaLeft);
                        var rightError = QuaternionAngleDegrees(predictedDeltaRight, unityDeltaRight);
                        var error = MathF.Min(leftError, rightError);
                        var leftCorrection = Normalize(Multiply(unityDeltaLeft, Inverse(predictedDeltaLeft)));
                        var rightCorrection = Normalize(Multiply(Inverse(predictedDeltaRight), unityDeltaRight));
                        var useLeftCorrection = leftError <= rightError;
                        var bestCorrection = useLeftCorrection ? leftCorrection : rightCorrection;
                        correctionRows.Add(new SingleMuscleDeltaCorrectionRow(
                            target.HumanBone,
                            attribute,
                            value,
                            targetPath,
                            probePath,
                            leftError,
                            rightError,
                            useLeftCorrection ? "leftMultiply" : "rightMultiply",
                            useLeftCorrection ? predictedDeltaLeft : predictedDeltaRight,
                            useLeftCorrection ? unityDeltaLeft : unityDeltaRight,
                            axis.PreQ,
                            axis.PostQ,
                            bestCorrection,
                            QuaternionAngleDegrees(IdentityQuaternion, bestCorrection),
                            leftCorrection,
                            QuaternionAngleDegrees(IdentityQuaternion, leftCorrection),
                            rightCorrection,
                            QuaternionAngleDegrees(IdentityQuaternion, rightCorrection)));
                        rows.Add(new TrackCompareRow(
                            $"{target.HumanBone}:{attribute}:{value.ToString("R", CultureInfo.InvariantCulture)}",
                            probePath,
                            -1,
                            1,
                            error,
                            error,
                            IsBodyBone(targetPath)));
                    }
                }
            }

            var ordered = rows
                .OrderByDescending(x => x.MaxRotationErrorDegrees)
                .ThenBy(x => x.UnityPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var selectedCombinationProbes = internalAvatarPoseCombinationProbes.Length == 0 ? combinationProbes : internalAvatarPoseCombinationProbes;
            var selectedSingleProbes = internalAvatarPoseCombinationProbes.Length == 0 ? probes : internalAvatarPoseProbes.Length == 0 ? probes : internalAvatarPoseProbes;
            var comparison = new JObject
            {
                ["status"] = ordered.Length == 0 ? "not_available" : missingProbes.Count == 0 && missingTargets.Count == 0 ? "ok" : "warning",
                ["rule"] = "Diagnostic only: compares delta rotations from the current offline Humanoid formula against Unity HumanPoseHandler single-muscle probes. Each row compares rotation(value) * inverse(rotation(baseValue)), so this isolates one muscle's local effect instead of mixing in the base pose.",
                ["probeCount"] = probes.Length,
                ["matchedProbeCount"] = ordered.Length,
                ["missingProbeCount"] = missingProbes.Count,
                ["missingTargetCount"] = missingTargets.Count,
                ["missingProbes"] = new JArray(missingProbes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(128)),
                ["missingTargets"] = new JArray(missingTargets.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(128)),
                ["maxDegrees"] = ordered.Length == 0 ? 0 : ordered[0].MaxRotationErrorDegrees,
                ["avgTrackMaxDegrees"] = ordered.Length == 0 ? 0 : ordered.Average(x => x.MaxRotationErrorDegrees),
                ["bodyGroupError"] = BuildBodyGroupErrorSummary(ordered),
                ["singleMuscleInfluenceSummary"] = BuildSingleMuscleInfluenceSummary(probes, solverNodes, solverAxes, humanBoneIndex),
                ["singleMuscleAxisFitSummary"] = BuildSingleMuscleAxisFitSummary(probes, solverNodes, solverAxes, humanBoneIndex),
                ["singleMuscleSourceTargetAxisFitSummary"] = BuildSingleMuscleSourceTargetAxisFitSummary(probes, solverNodes, solverAxes, humanBoneIndex),
                ["singleMuscleFormulaCandidateSummary"] = BuildSingleMuscleFormulaCandidateSummary(probes, solverNodes, solverAxes, humanBoneIndex),
                ["singleMuscleFormulaDeltaSideSummary"] = BuildSingleMuscleFormulaDeltaSideSummary(internalAvatarPoseProbes.Length == 0 ? probes : internalAvatarPoseProbes, solverNodes, solverAxes, humanBoneIndex),
                ["forearmStretchScaleProbeSummary"] = BuildForearmStretchScaleProbeSummary(internalAvatarPoseProbes.Length == 0 ? probes : internalAvatarPoseProbes, solver, solverNodes, solverAxes, humanBoneIndex),
                ["singleMuscleProbeBasePoseSummary"] = BuildSingleMuscleProbeBasePoseSummary(internalAvatarPoseProbes.Length == 0 ? probes : internalAvatarPoseProbes, solverNodes, solverAxes, humanBoneIndex),
                ["singleMuscleDeltaCorrectionSummary"] = BuildSingleMuscleDeltaCorrectionSummary(correctionRows),
                ["singleMuscleDeltaAxisSummary"] = BuildSingleMuscleDeltaAxisSummary(correctionRows, solver),
                ["singleMuscleMultiValueLinearitySummary"] = BuildSingleMuscleMultiValueLinearitySummary(internalAvatarPoseProbes.Length == 0 ? probes : internalAvatarPoseProbes, solverNodes, solverAxes, humanBoneIndex),
                ["transformVsInternalAvatarPoseProbeSummary"] = BuildTransformVsInternalAvatarPoseProbeSummary(probes, internalAvatarPoseProbes),
                ["muscleCombinationProbeSolverComparison"] = BuildMuscleCombinationProbeSolverComparison(selectedCombinationProbes, selectedSingleProbes, solver, solverNodes, solverAxes, humanBoneIndex),
                ["topErrors"] = ToJsonRows(ordered.Take(96)),
            };
            comparison["formulaHintSummary"] = BuildSingleMuscleFormulaHintSummary(comparison);
            comparison["formulaReadinessSummary"] = BuildSingleMuscleFormulaReadinessSummary(comparison);
            comparison["armFormulaWorkQueueSummary"] = BuildArmFormulaWorkQueueSummary(comparison);
            return comparison;
        }

        private static JObject BuildMuscleCombinationProbeSolverComparison(
            JObject[] probes,
            JObject[] singleMuscleProbes,
            JObject solver,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex)
        {
            if (probes == null || probes.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["reason"] = "muscle_combination_probes_missing",
                    ["rule"] = "Diagnostic only: compares current offline Humanoid formula against Unity probes where related limb muscles are applied together.",
                };
            }

            var variantRows = BuildMuscleCombinationProbeVariants()
                .Select(variant =>
                {
                    var rows = BuildMuscleCombinationProbeRows(probes, variant, solver, solverNodes, solverAxes, humanBoneIndex, out var missingTargets);
                    var orderedRows = rows
                        .OrderByDescending(x => x.MaxRotationErrorDegrees)
                        .ThenBy(x => x.UnityPath, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    return new
                    {
                        Variant = variant,
                        Rows = orderedRows,
                        MissingTargets = missingTargets,
                    };
                })
                .Where(x => x.Rows.Length > 0)
                .OrderBy(x => x.Rows.Max(row => row.MaxRotationErrorDegrees))
                .ThenBy(x => x.Rows.Average(row => row.MaxRotationErrorDegrees))
                .ThenBy(x => x.Variant.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var current = variantRows.FirstOrDefault(x => string.Equals(x.Variant.Name, "current_swing_twist", StringComparison.OrdinalIgnoreCase)) ?? variantRows.FirstOrDefault();
            var ordered = current?.Rows ?? Array.Empty<TrackCompareRow>();
            var missingTargetsForCurrent = current?.MissingTargets ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var best = variantRows.FirstOrDefault();
            var currentRank = current == null
                ? -1
                : Array.FindIndex(variantRows, x => string.Equals(x.Variant.Name, current.Variant.Name, StringComparison.OrdinalIgnoreCase)) + 1;
            var currentMax = ordered.Length == 0 ? 0 : ordered.Max(x => x.MaxRotationErrorDegrees);
            var bestMax = best?.Rows.Length > 0 ? best.Rows.Max(x => x.MaxRotationErrorDegrees) : 0f;
            return new JObject
            {
                ["status"] = ordered.Length == 0 ? "not_available" : missingTargetsForCurrent.Count == 0 ? "ok" : "warning",
                ["rule"] = "Diagnostic only: Unity applies related limb muscles together on the same base pose; this checks whether offline solver variants compose those muscles the same way. It is not a production binding rule.",
                ["source"] = "Unity internalAvatarPoseMuscleCombinationProbes when available, otherwise Transform.local muscleCombinationProbes.",
                ["probeCount"] = probes.Length,
                ["variantCount"] = variantRows.Length,
                ["bestVariant"] = best?.Variant.Name,
                ["bestVariantMaxDegrees"] = bestMax,
                ["currentVariantRank"] = currentRank,
                ["currentToBestMaxImprovementDegrees"] = Math.Max(0f, currentMax - bestMax),
                ["matchedTrackCount"] = ordered.Length,
                ["missingTargetCount"] = missingTargetsForCurrent.Count,
                ["missingTargets"] = new JArray(missingTargetsForCurrent.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(128)),
                ["maxDegrees"] = ordered.Length == 0 ? 0 : ordered[0].MaxRotationErrorDegrees,
                ["avgTrackMaxDegrees"] = ordered.Length == 0 ? 0 : ordered.Average(x => x.MaxRotationErrorDegrees),
                ["bodyGroupError"] = BuildBodyGroupErrorSummary(ordered),
                ["variantRanking"] = new JArray(variantRows.Select(x => BuildMuscleCombinationVariantSummary(x.Variant, x.Rows, x.MissingTargets)).Take(64)),
                ["probeSummary"] = BuildMuscleCombinationProbeSummary(ordered),
                ["translationSummary"] = BuildMuscleCombinationTranslationSummary(probes),
                ["distalResidualCorrectionSummary"] = BuildMuscleCombinationDistalResidualCorrectionSummary(probes, solver, solverNodes, solverAxes, humanBoneIndex),
                ["unitySingleMuscleCompositionSummary"] = BuildUnitySingleMuscleCompositionSummary(probes, singleMuscleProbes),
                ["topErrors"] = ToJsonRows(ordered.Take(96)),
            };
        }

        private static List<TrackCompareRow> BuildMuscleCombinationProbeRows(
            JObject[] probes,
            SolverVariant variant,
            JObject solver,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex,
            out HashSet<string> missingTargets)
        {
            var rows = new List<TrackCompareRow>();
            missingTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var probe in probes)
            {
                var probeName = (string)probe["probeName"];
                var muscleValues = ReadProbeMuscleValues(probe).ToArray();
                if (muscleValues.Length == 0)
                {
                    continue;
                }

                var muscleNames = new HashSet<string>(muscleValues.Select(x => x.MuscleName), StringComparer.OrdinalIgnoreCase);
                var baseCurves = muscleValues
                    .GroupBy(x => x.MuscleName, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => new List<FloatKey> { new FloatKey(0f, x.First().BaseValue) }, StringComparer.OrdinalIgnoreCase);
                var curves = muscleValues
                    .GroupBy(x => x.MuscleName, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => new List<FloatKey> { new FloatKey(0f, x.First().Value) }, StringComparer.OrdinalIgnoreCase);

                foreach (var target in variant.Targets)
                {
                    var targetAttributes = new[] { target.XAttribute, target.YAttribute, target.ZAttribute, target.ExtraZAttribute }
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (!targetAttributes.Any(x => muscleNames.Contains(x)))
                    {
                        continue;
                    }

                    if (!TryGetSolverTarget(solverNodes, solverAxes, humanBoneIndex, target, out var targetPath, out var axis))
                    {
                        missingTargets.Add($"{probeName}|{target.HumanBone}");
                        continue;
                    }

                    if (!TryReadProbeRotation(probe, targetPath, out var probePath, out var unityBaseRotation, out var unityRotation))
                    {
                        missingTargets.Add($"{probeName}|{targetPath}");
                        continue;
                    }

                    var predictedBase = BuildVariantSolverRotation(
                        baseCurves,
                        target,
                        axis,
                        0f,
                        variant,
                        solver,
                        solverNodes,
                        humanBoneIndex,
                        targetPath,
                        gltfNodes: null,
                        nodePathToIndex: null);
                    var predicted = BuildVariantSolverRotation(
                        curves,
                        target,
                        axis,
                        0f,
                        variant,
                        solver,
                        solverNodes,
                        humanBoneIndex,
                        targetPath,
                        gltfNodes: null,
                        nodePathToIndex: null);

                    var predictedDeltaLeft = Normalize(Multiply(predicted, Inverse(predictedBase)));
                    var unityDeltaLeft = Normalize(Multiply(unityRotation, Inverse(unityBaseRotation)));
                    var predictedDeltaRight = Normalize(Multiply(Inverse(predictedBase), predicted));
                    var unityDeltaRight = Normalize(Multiply(Inverse(unityBaseRotation), unityRotation));
                    var error = MathF.Min(
                        QuaternionAngleDegrees(predictedDeltaLeft, unityDeltaLeft),
                        QuaternionAngleDegrees(predictedDeltaRight, unityDeltaRight));
                    rows.Add(new TrackCompareRow(
                        probePath,
                        string.IsNullOrWhiteSpace(probeName) ? "combination_probe" : probeName,
                        -1,
                        1,
                        error,
                        error,
                        IsBodyBone(targetPath)));
                }
            }

            return rows;
        }

        private static bool IsMuscleCombinationProbeVariant(SolverVariant variant)
        {
            var name = variant?.Name ?? string.Empty;
            return
                string.Equals(name, "current_swing_twist", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "current_twist_swing", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "distal_no_stretch", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "arm_no_twist", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "arm_no_stretch", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "lower_arm_static", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "leg_no_upper_twist", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "leg_no_stretch", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "lower_leg_static", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "distal_limb_static", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "distal_limb_no_proximal_twist", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "distal_limb_no_stretch", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("distal_pair_", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "foot_no_twist", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("twist_split_", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "twist_invert_current", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "leg_twist_parent_source_x", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("arm_twist_target_y_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("swing_", StringComparison.OrdinalIgnoreCase);
        }

        private static SolverVariant[] BuildMuscleCombinationProbeVariants()
        {
            var currentTargets = BuildCurrentSolverTargets();
            var gridScales = new[] { 0f, 0.25f, 0.5f, 0.75f, 1f };
            var grid = new List<SolverVariant>();
            foreach (var twistScale in gridScales)
            {
                foreach (var stretchScale in gridScales)
                {
                    grid.Add(new SolverVariant(
                        $"distal_pair_grid_t{ScaleName(twistScale)}_s{ScaleName(stretchScale)}",
                        $"Diagnostic grid test: scale distal proximal twist to {twistScale.ToString("0.##", CultureInfo.InvariantCulture)} and distal stretch to {stretchScale.ToString("0.##", CultureInfo.InvariantCulture)}.",
                        currentTargets,
                        "swing_twist",
                        TwistMode: $"distal_pair_grid_t{ScaleName(twistScale)}_s{ScaleName(stretchScale)}"));
                }
            }

            return BuildSolverVariants()
                .Where(IsMuscleCombinationProbeVariant)
                .Concat(grid)
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToArray();
        }

        private static string ScaleName(float value)
        {
            return MathF.Round(value * 100f).ToString("0", CultureInfo.InvariantCulture);
        }

        private static JObject BuildMuscleCombinationVariantSummary(SolverVariant variant, TrackCompareRow[] rows, HashSet<string> missingTargets)
        {
            return new JObject
            {
                ["variant"] = variant.Name,
                ["description"] = variant.Description,
                ["matchedTrackCount"] = rows.Length,
                ["missingTargetCount"] = missingTargets?.Count ?? 0,
                ["maxDegrees"] = rows.Length == 0 ? 0 : rows.Max(x => x.MaxRotationErrorDegrees),
                ["avgTrackMaxDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.MaxRotationErrorDegrees),
                ["bodyGroupError"] = BuildBodyGroupErrorSummary(rows),
                ["topProbeErrors"] = new JArray(BuildMuscleCombinationProbeSummary(rows).Take(12)),
            };
        }

        private static JArray BuildMuscleCombinationProbeSummary(TrackCompareRow[] ordered)
        {
            return new JArray((ordered ?? Array.Empty<TrackCompareRow>())
                .GroupBy(x => x.GltfPath, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x.Max(y => y.MaxRotationErrorDegrees))
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => new JObject
                {
                    ["probeName"] = group.Key,
                    ["matchedTrackCount"] = group.Count(),
                    ["maxDegrees"] = group.Max(x => x.MaxRotationErrorDegrees),
                    ["avgDegrees"] = group.Average(x => x.MaxRotationErrorDegrees),
                    ["worstPath"] = group.OrderByDescending(x => x.MaxRotationErrorDegrees).First().UnityPath,
                })
                .Take(64));
        }

        private static JObject BuildMuscleCombinationTranslationSummary(JObject[] probes)
        {
            var rows = new List<JObject>();
            foreach (var probe in probes ?? Array.Empty<JObject>())
            {
                var probeName = (string)probe["probeName"] ?? "combination_probe";
                foreach (var item in probe["translations"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    if (!TryReadVector3Key(item["baseTranslation"], out var baseTranslation) ||
                        !TryReadVector3Key(item["translation"], out var translation))
                    {
                        continue;
                    }

                    var path = (string)item["path"] ?? string.Empty;
                    var distance = VectorDistance(baseTranslation, translation);
                    if (distance <= 0.000001f)
                    {
                        continue;
                    }

                    rows.Add(new JObject
                    {
                        ["probeName"] = probeName,
                        ["path"] = path,
                        ["name"] = (string)item["name"] ?? string.Empty,
                        ["bodyGroup"] = ClassifyBodyGroup(path),
                        ["distance"] = distance,
                        ["baseTranslation"] = ToJArray(baseTranslation),
                        ["translation"] = ToJArray(translation),
                    });
                }
            }

            var ordered = rows
                .OrderByDescending(x => (float?)x["distance"] ?? 0f)
                .ThenBy(x => (string)x["probeName"], StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => (string)x["path"], StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (ordered.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["translationTrackCount"] = 0,
                    ["rule"] = "Diagnostic only: checks whether Unity combination muscle probes changed local translation/InternalAvatarPose T.xyz. Empty means the sampled probe exposed rotation-only changes.",
                };
            }

            var byBodyGroup = ordered
                .GroupBy(x => (string)x["bodyGroup"] ?? "Other", StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var items = group.ToArray();
                    return new JObject
                    {
                        ["bodyGroup"] = group.Key,
                        ["trackCount"] = items.Length,
                        ["maxDistance"] = items.Max(x => (float?)x["distance"] ?? 0f),
                        ["avgDistance"] = items.Average(x => (float?)x["distance"] ?? 0f),
                        ["worstProbeName"] = (string)items.OrderByDescending(x => (float?)x["distance"] ?? 0f).First()["probeName"],
                        ["worstPath"] = (string)items.OrderByDescending(x => (float?)x["distance"] ?? 0f).First()["path"],
                    };
                })
                .OrderByDescending(x => (int?)x["trackCount"] ?? 0)
                .ThenByDescending(x => (float?)x["maxDistance"] ?? 0f)
                .ThenBy(x => (string)x["bodyGroup"], StringComparer.OrdinalIgnoreCase);

            var byProbe = ordered
                .GroupBy(x => (string)x["probeName"] ?? "combination_probe", StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var items = group.ToArray();
                    return new JObject
                    {
                        ["probeName"] = group.Key,
                        ["trackCount"] = items.Length,
                        ["maxDistance"] = items.Max(x => (float?)x["distance"] ?? 0f),
                        ["avgDistance"] = items.Average(x => (float?)x["distance"] ?? 0f),
                        ["worstPath"] = (string)items.OrderByDescending(x => (float?)x["distance"] ?? 0f).First()["path"],
                    };
                })
                .OrderByDescending(x => (float?)x["maxDistance"] ?? 0f)
                .ThenBy(x => (string)x["probeName"], StringComparer.OrdinalIgnoreCase);

            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: non-zero translation in combo probes means the Unity Humanoid result contains local translation/InternalAvatarPose T.xyz changes, so stretch/twist cannot be solved as pure local rotation only.",
                ["probeCount"] = probes?.Length ?? 0,
                ["translationTrackCount"] = ordered.Length,
                ["maxDistance"] = ordered.Max(x => (float?)x["distance"] ?? 0f),
                ["avgDistance"] = ordered.Average(x => (float?)x["distance"] ?? 0f),
                ["bodyGroupSummary"] = new JArray(byBodyGroup),
                ["probeSummary"] = new JArray(byProbe.Take(32)),
                ["topTranslations"] = new JArray(ordered.Take(96)),
            };
        }

        private static JObject BuildMuscleCombinationDistalResidualCorrectionSummary(
            JObject[] probes,
            JObject solver,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex)
        {
            var current = new SolverVariant(
                "current_swing_twist",
                "Current preview formula used only as the baseline for residual diagnostics.",
                BuildCurrentSolverTargets(),
                "swing_twist");
            var rows = new List<MuscleCombinationResidualRow>();
            foreach (var probe in probes ?? Array.Empty<JObject>())
            {
                var probeName = (string)probe["probeName"] ?? "combination_probe";
                var muscleValues = ReadProbeMuscleValues(probe).ToArray();
                if (muscleValues.Length == 0)
                {
                    continue;
                }

                var muscleNames = new HashSet<string>(muscleValues.Select(x => x.MuscleName), StringComparer.OrdinalIgnoreCase);
                var baseCurves = muscleValues
                    .GroupBy(x => x.MuscleName, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => new List<FloatKey> { new FloatKey(0f, x.First().BaseValue) }, StringComparer.OrdinalIgnoreCase);
                var curves = muscleValues
                    .GroupBy(x => x.MuscleName, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => new List<FloatKey> { new FloatKey(0f, x.First().Value) }, StringComparer.OrdinalIgnoreCase);

                foreach (var target in current.Targets)
                {
                    var targetAttributes = new[] { target.XAttribute, target.YAttribute, target.ZAttribute, target.ExtraZAttribute }
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (!targetAttributes.Any(x => muscleNames.Contains(x)) ||
                        !targetAttributes.Any(x => IsDistalPairAttribute(target.HumanBone, x)))
                    {
                        continue;
                    }

                    if (!TryGetSolverTarget(solverNodes, solverAxes, humanBoneIndex, target, out var targetPath, out var axis) ||
                        !TryReadProbeRotation(probe, targetPath, out var probePath, out var unityBaseRotation, out var unityRotation))
                    {
                        continue;
                    }

                    var predictedBase = BuildVariantSolverRotation(
                        baseCurves,
                        target,
                        axis,
                        0f,
                        current,
                        solver,
                        solverNodes,
                        humanBoneIndex,
                        targetPath,
                        gltfNodes: null,
                        nodePathToIndex: null);
                    var predicted = BuildVariantSolverRotation(
                        curves,
                        target,
                        axis,
                        0f,
                        current,
                        solver,
                        solverNodes,
                        humanBoneIndex,
                        targetPath,
                        gltfNodes: null,
                        nodePathToIndex: null);

                    var predictedDeltaLeft = Normalize(Multiply(predicted, Inverse(predictedBase)));
                    var unityDeltaLeft = Normalize(Multiply(unityRotation, Inverse(unityBaseRotation)));
                    var predictedDeltaRight = Normalize(Multiply(Inverse(predictedBase), predicted));
                    var unityDeltaRight = Normalize(Multiply(Inverse(unityBaseRotation), unityRotation));
                    var leftError = QuaternionAngleDegrees(predictedDeltaLeft, unityDeltaLeft);
                    var rightError = QuaternionAngleDegrees(predictedDeltaRight, unityDeltaRight);
                    var leftCorrection = Normalize(Multiply(unityDeltaLeft, Inverse(predictedDeltaLeft)));
                    var rightCorrection = Normalize(Multiply(Inverse(predictedDeltaRight), unityDeltaRight));
                    var useLeft = leftError <= rightError;
                    var activeAttributes = targetAttributes
                        .Where(x => muscleNames.Contains(x))
                        .ToArray();
                    rows.Add(new MuscleCombinationResidualRow(
                        probeName,
                        NormalizeCombinationProbeFamily(probeName),
                        target.HumanBone,
                        targetPath,
                        probePath,
                        activeAttributes,
                        useLeft ? "leftMultiply" : "rightMultiply",
                        MathF.Min(leftError, rightError),
                        leftCorrection,
                        QuaternionAngleDegrees(IdentityQuaternion, leftCorrection),
                        rightCorrection,
                        QuaternionAngleDegrees(IdentityQuaternion, rightCorrection),
                        useLeft ? leftCorrection : rightCorrection,
                        QuaternionAngleDegrees(IdentityQuaternion, useLeft ? leftCorrection : rightCorrection)));
                }
            }

            var ordered = rows
                .OrderByDescending(x => x.CurrentErrorDegrees)
                .ThenBy(x => x.ProbeName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.HumanBone, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (ordered.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["rowCount"] = 0,
                    ["rule"] = "Diagnostic only: learns residual corrections for distal twist/stretch combination probes. Missing rows mean the probe set did not hit distal limb targets.",
                };
            }

            var byHumanBone = ordered
                .GroupBy(x => x.HumanBone, StringComparer.OrdinalIgnoreCase)
                .Select(BuildMuscleCombinationResidualGroup)
                .OrderByDescending(x => (float?)x["maxCurrentErrorDegrees"] ?? 0f)
                .ThenBy(x => (string)x["humanBone"], StringComparer.OrdinalIgnoreCase);
            var byProbeFamily = ordered
                .GroupBy(x => x.ProbeFamily, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var items = group.ToArray();
                    return new JObject
                    {
                        ["probeFamily"] = group.Key,
                        ["rowCount"] = items.Length,
                        ["maxCurrentErrorDegrees"] = items.Max(x => x.CurrentErrorDegrees),
                        ["avgCurrentErrorDegrees"] = items.Average(x => x.CurrentErrorDegrees),
                        ["correctionConsistencyMaxDegrees"] = MaxQuaternionPairGap(items.Select(x => x.BestCorrection)),
                        ["leftMultiplyCount"] = items.Count(x => string.Equals(x.BestApplyMode, "leftMultiply", StringComparison.OrdinalIgnoreCase)),
                        ["rightMultiplyCount"] = items.Count(x => string.Equals(x.BestApplyMode, "rightMultiply", StringComparison.OrdinalIgnoreCase)),
                        ["worstHumanBone"] = items.OrderByDescending(x => x.CurrentErrorDegrees).First().HumanBone,
                    };
                })
                .OrderByDescending(x => (float?)x["maxCurrentErrorDegrees"] ?? 0f)
                .ThenBy(x => (string)x["probeFamily"], StringComparer.OrdinalIgnoreCase);

            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: for each distal twist/stretch combination probe, computes the quaternion correction needed to map the current offline delta to Unity's real delta. Low consistency within a bone/probe family suggests a missing stable space transform; high consistency means the actual composition formula is still wrong.",
                ["rowCount"] = ordered.Length,
                ["maxCurrentErrorDegrees"] = ordered.Max(x => x.CurrentErrorDegrees),
                ["avgCurrentErrorDegrees"] = ordered.Average(x => x.CurrentErrorDegrees),
                ["maxCorrectionConsistencyByHumanBoneDegrees"] = ordered
                    .GroupBy(x => x.HumanBone, StringComparer.OrdinalIgnoreCase)
                    .Select(x => MaxQuaternionPairGap(x.Select(row => row.BestCorrection)))
                    .DefaultIfEmpty(0f)
                    .Max(),
                ["byHumanBone"] = new JArray(byHumanBone),
                ["byProbeFamily"] = new JArray(byProbeFamily),
                ["topRows"] = ToJsonRows(ordered.Take(96)),
            };
        }

        private static JObject BuildMuscleCombinationResidualGroup(IGrouping<string, MuscleCombinationResidualRow> group)
        {
            var items = group.ToArray();
            return new JObject
            {
                ["humanBone"] = group.Key,
                ["bodyGroup"] = ClassifyBodyGroup(items[0].TargetPath),
                ["rowCount"] = items.Length,
                ["maxCurrentErrorDegrees"] = items.Max(x => x.CurrentErrorDegrees),
                ["avgCurrentErrorDegrees"] = items.Average(x => x.CurrentErrorDegrees),
                ["maxCorrectionAngleDegrees"] = items.Max(x => x.BestCorrectionAngleDegrees),
                ["avgCorrectionAngleDegrees"] = items.Average(x => x.BestCorrectionAngleDegrees),
                ["correctionConsistencyMaxDegrees"] = MaxQuaternionPairGap(items.Select(x => x.BestCorrection)),
                ["leftMultiplyCount"] = items.Count(x => string.Equals(x.BestApplyMode, "leftMultiply", StringComparison.OrdinalIgnoreCase)),
                ["rightMultiplyCount"] = items.Count(x => string.Equals(x.BestApplyMode, "rightMultiply", StringComparison.OrdinalIgnoreCase)),
                ["worstProbeName"] = items.OrderByDescending(x => x.CurrentErrorDegrees).First().ProbeName,
                ["worstAttributes"] = new JArray(items.OrderByDescending(x => x.CurrentErrorDegrees).First().Attributes),
            };
        }

        private static string NormalizeCombinationProbeFamily(string probeName)
        {
            var name = probeName ?? "combination_probe";
            name = name.Replace("Left_", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("Right_", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("_pos", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("_neg", string.Empty, StringComparison.OrdinalIgnoreCase);
            return string.IsNullOrWhiteSpace(name) ? "combination_probe" : name;
        }

        private static JObject BuildUnitySingleMuscleCompositionSummary(JObject[] combinationProbes, JObject[] singleMuscleProbes)
        {
            var singleByMuscleAndValue = (singleMuscleProbes ?? Array.Empty<JObject>())
                .Where(x => !string.IsNullOrWhiteSpace((string)x["muscleName"]))
                .GroupBy(x => BuildMuscleValueKey((string)x["muscleName"], (float?)x["value"] ?? 0f), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            var rows = new List<UnityCombinationCompositionRow>();
            var missingSingles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var probe in combinationProbes ?? Array.Empty<JObject>())
            {
                var probeName = (string)probe["probeName"] ?? "combination_probe";
                var muscles = ReadProbeMuscleValues(probe).ToArray();
                if (muscles.Length < 2)
                {
                    continue;
                }

                foreach (var rotationItem in probe["rotations"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    var path = (string)rotationItem["path"];
                    if (string.IsNullOrWhiteSpace(path) ||
                        !TryReadQuaternion(rotationItem["baseRotation"], out var comboBase) ||
                        !TryReadQuaternion(rotationItem["rotation"], out var comboRotation))
                    {
                        continue;
                    }

                    var singleLeftDeltas = new List<float[]>();
                    var singleRightDeltas = new List<float[]>();
                    var muscleNames = new List<string>();
                    var missing = false;
                    foreach (var muscle in muscles)
                    {
                        var key = BuildMuscleValueKey(muscle.MuscleName, muscle.Value);
                        if (!singleByMuscleAndValue.TryGetValue(key, out var singleProbe) ||
                            !TryReadProbeRotation(singleProbe, path, out _, out var singleBase, out var singleRotation))
                        {
                            missingSingles.Add($"{key}|{path}");
                            missing = true;
                            break;
                        }

                        muscleNames.Add(muscle.MuscleName);
                        singleLeftDeltas.Add(Normalize(Multiply(singleRotation, Inverse(singleBase))));
                        singleRightDeltas.Add(Normalize(Multiply(Inverse(singleBase), singleRotation)));
                    }

                    if (missing || singleLeftDeltas.Count < 2)
                    {
                        continue;
                    }

                    var comboLeftDelta = Normalize(Multiply(comboRotation, Inverse(comboBase)));
                    var comboRightDelta = Normalize(Multiply(Inverse(comboBase), comboRotation));
                    var candidates = BuildUnitySingleMuscleCompositionCandidates(singleLeftDeltas, singleRightDeltas, muscleNames, comboLeftDelta, comboRightDelta);
                    var best = candidates
                        .OrderBy(x => x.ErrorDegrees)
                        .ThenBy(x => x.Mode, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(x => x.Order, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();
                    if (best == null)
                    {
                        continue;
                    }

                    rows.Add(new UnityCombinationCompositionRow(
                        probeName,
                        NormalizeCombinationProbeFamily(probeName),
                        path,
                        muscleNames.ToArray(),
                        best.Mode,
                        best.Order,
                        best.ErrorDegrees,
                        best.ComparedTo,
                        QuaternionAngleDegrees(IdentityQuaternion, string.Equals(best.ComparedTo, "comboLeftDelta", StringComparison.OrdinalIgnoreCase) ? comboLeftDelta : comboRightDelta)));
                }
            }

            var ordered = rows
                .OrderByDescending(x => x.BestErrorDegrees)
                .ThenBy(x => x.ProbeName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (ordered.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["rowCount"] = 0,
                    ["missingSingleProbeCount"] = missingSingles.Count,
                    ["missingSingleProbes"] = new JArray(missingSingles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(128)),
                    ["rule"] = "Diagnostic only: tests whether Unity combination muscle deltas can be reconstructed by composing Unity single-muscle deltas on the same bone path.",
                };
            }

            var byProbeFamily = ordered
                .GroupBy(x => x.ProbeFamily, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var items = group.ToArray();
                    return new JObject
                    {
                        ["probeFamily"] = group.Key,
                        ["rowCount"] = items.Length,
                        ["maxBestErrorDegrees"] = items.Max(x => x.BestErrorDegrees),
                        ["avgBestErrorDegrees"] = items.Average(x => x.BestErrorDegrees),
                        ["rowsUnder5Degrees"] = items.Count(x => x.BestErrorDegrees <= 5f),
                        ["rowsUnder10Degrees"] = items.Count(x => x.BestErrorDegrees <= 10f),
                        ["dominantMode"] = items
                            .GroupBy(x => x.BestMode, StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(x => x.Count())
                            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                            .First()
                            .Key,
                        ["worstPath"] = items.OrderByDescending(x => x.BestErrorDegrees).First().Path,
                    };
                })
                .OrderByDescending(x => (float?)x["maxBestErrorDegrees"] ?? 0f)
                .ThenBy(x => (string)x["probeFamily"], StringComparer.OrdinalIgnoreCase);

            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: compares Unity combination probes against all simple products of matching Unity single-muscle deltas. Low error means Unity's combination is mostly composable from single muscles; high error means the Humanoid solver is doing nonlinear limb-chain coupling beyond simple quaternion multiplication.",
                ["rowCount"] = ordered.Length,
                ["missingSingleProbeCount"] = missingSingles.Count,
                ["missingSingleProbes"] = new JArray(missingSingles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(128)),
                ["maxBestErrorDegrees"] = ordered.Max(x => x.BestErrorDegrees),
                ["avgBestErrorDegrees"] = ordered.Average(x => x.BestErrorDegrees),
                ["rowsUnder5Degrees"] = ordered.Count(x => x.BestErrorDegrees <= 5f),
                ["rowsUnder10Degrees"] = ordered.Count(x => x.BestErrorDegrees <= 10f),
                ["byProbeFamily"] = new JArray(byProbeFamily),
                ["topRows"] = ToJsonRows(ordered.Take(96)),
            };
        }

        private static string BuildMuscleValueKey(string muscleName, float value)
        {
            return $"{muscleName}|{value.ToString("R", CultureInfo.InvariantCulture)}";
        }

        private static IEnumerable<UnityCombinationCompositionCandidate> BuildUnitySingleMuscleCompositionCandidates(
            IReadOnlyList<float[]> leftDeltas,
            IReadOnlyList<float[]> rightDeltas,
            IReadOnlyList<string> muscleNames,
            float[] comboLeftDelta,
            float[] comboRightDelta)
        {
            foreach (var order in BuildIndexPermutations(leftDeltas.Count))
            {
                var orderName = string.Join(" -> ", order.Select(i => i >= 0 && i < muscleNames.Count ? muscleNames[i] : i.ToString(CultureInfo.InvariantCulture)));
                var leftAppend = ComposeDeltas(leftDeltas, order, append: true);
                var leftPrepend = ComposeDeltas(leftDeltas, order, append: false);
                var rightAppend = ComposeDeltas(rightDeltas, order, append: true);
                var rightPrepend = ComposeDeltas(rightDeltas, order, append: false);
                yield return new UnityCombinationCompositionCandidate("leftDelta_append", orderName, QuaternionAngleDegrees(leftAppend, comboLeftDelta), "comboLeftDelta");
                yield return new UnityCombinationCompositionCandidate("leftDelta_prepend", orderName, QuaternionAngleDegrees(leftPrepend, comboLeftDelta), "comboLeftDelta");
                yield return new UnityCombinationCompositionCandidate("rightDelta_append", orderName, QuaternionAngleDegrees(rightAppend, comboRightDelta), "comboRightDelta");
                yield return new UnityCombinationCompositionCandidate("rightDelta_prepend", orderName, QuaternionAngleDegrees(rightPrepend, comboRightDelta), "comboRightDelta");
            }
        }

        private static float[] ComposeDeltas(IReadOnlyList<float[]> deltas, IReadOnlyList<int> order, bool append)
        {
            var result = IdentityQuaternion;
            foreach (var index in order)
            {
                if (index < 0 || index >= deltas.Count)
                {
                    continue;
                }

                result = append
                    ? Normalize(Multiply(result, deltas[index]))
                    : Normalize(Multiply(deltas[index], result));
            }
            return result;
        }

        private static IEnumerable<int[]> BuildIndexPermutations(int count)
        {
            if (count <= 0 || count > 4)
            {
                yield break;
            }

            var values = Enumerable.Range(0, count).ToArray();
            foreach (var permutation in BuildIndexPermutations(values, 0))
            {
                yield return permutation;
            }
        }

        private static IEnumerable<int[]> BuildIndexPermutations(int[] values, int start)
        {
            if (start >= values.Length)
            {
                yield return values.ToArray();
                yield break;
            }

            for (var i = start; i < values.Length; i++)
            {
                (values[start], values[i]) = (values[i], values[start]);
                foreach (var item in BuildIndexPermutations(values, start + 1))
                {
                    yield return item;
                }
                (values[start], values[i]) = (values[i], values[start]);
            }
        }

        private static bool TryReadVector3Key(JToken value, out float[] vector)
        {
            vector = null;
            if (value == null)
            {
                return false;
            }

            vector = new[]
            {
                (float?)value["x"] ?? 0f,
                (float?)value["y"] ?? 0f,
                (float?)value["z"] ?? 0f,
            };
            return vector.Length == 3;
        }

        private static IEnumerable<ProbeMuscleValue> ReadProbeMuscleValues(JObject probe)
        {
            foreach (var item in probe?["muscles"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var muscleName = (string)item["muscleName"];
                if (string.IsNullOrWhiteSpace(muscleName))
                {
                    continue;
                }

                yield return new ProbeMuscleValue(
                    (int?)item["muscleIndex"] ?? -1,
                    muscleName,
                    (float?)item["baseValue"] ?? 0f,
                    (float?)item["value"] ?? 0f);
            }
        }

        private static JObject BuildSingleMuscleMultiValueLinearitySummary(
            JObject[] probes,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex)
        {
            if (probes == null || probes.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["reason"] = "single_muscle_probes_missing",
                    ["rule"] = "Diagnostic only: checks whether multiple values for the same Unity muscle form a stable axis and a piecewise-linear angle slope explained by AvatarConstant limits.",
                };
            }

            var samplesByKey = new Dictionary<string, List<MultiValueProbeSample>>(StringComparer.OrdinalIgnoreCase);
            foreach (var probe in probes)
            {
                var muscleName = (string)probe["muscleName"];
                if (string.IsNullOrWhiteSpace(muscleName))
                {
                    continue;
                }

                var muscleIndex = (int?)probe["muscleIndex"] ?? -1;
                var baseValue = (float?)probe["baseValue"] ?? 0f;
                var value = (float?)probe["value"] ?? 0f;
                foreach (var item in probe["rotations"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    var path = (string)item["path"];
                    if (string.IsNullOrWhiteSpace(path) ||
                        !TryReadQuaternion(item["baseRotation"], out var baseRotation) ||
                        !TryReadQuaternion(item["rotation"], out var rotation))
                    {
                        continue;
                    }

                    var offset = value - baseValue;
                    if (MathF.Abs(offset) <= 0.0001f)
                    {
                        continue;
                    }

                    var delta = Normalize(Multiply(rotation, Inverse(baseRotation)));
                    var axisAngle = ToAxisAngle(delta);
                    if (axisAngle.AngleDegrees <= 0.05f)
                    {
                        continue;
                    }

                    var key = $"{muscleName}|{NormalizeBakePath(path)}";
                    if (!samplesByKey.TryGetValue(key, out var samples))
                    {
                        samples = new List<MultiValueProbeSample>();
                        samplesByKey[key] = samples;
                    }
                    samples.Add(new MultiValueProbeSample(
                        muscleIndex,
                        muscleName,
                        path,
                        baseValue,
                        value,
                        offset,
                        axisAngle.Axis,
                        axisAngle.AngleDegrees,
                        axisAngle.AngleDegrees / MathF.Abs(offset)));
                }
            }

            var rows = new List<SingleMuscleMultiValueLinearityRow>();
            foreach (var group in samplesByKey.Values)
            {
                var values = group
                    .Select(x => x.Value)
                    .Distinct()
                    .OrderBy(x => x)
                    .ToArray();
                if (group.Count < 2 || values.Length < 2)
                {
                    continue;
                }

                var path = group[0].Path;
                FindCurrentTargetForProbe(path, group[0].MuscleName, solverNodes, solverAxes, humanBoneIndex, out var humanBone, out var avatarAxis, out var targetAxis);
                ResolveLinearityLimitAxis(
                    group[0].MuscleName,
                    humanBone,
                    avatarAxis,
                    solverNodes,
                    solverAxes,
                    humanBoneIndex,
                    targetAxis,
                    out var targetAvatarAxis,
                    out var limitHumanBone,
                    out var limitAvatarAxis,
                    out var limitAxis);
                var positive = group.Where(x => x.Offset > 0f).ToArray();
                var negative = group.Where(x => x.Offset < 0f).ToArray();
                var positiveSlope = positive.Length == 0 ? 0.0 : positive.Average(x => x.DegreesPerUnit);
                var negativeSlope = negative.Length == 0 ? 0.0 : negative.Average(x => x.DegreesPerUnit);
                var allSlope = group.Average(x => x.DegreesPerUnit);
                var maxRelativeSlopeError = allSlope <= 0.0001
                    ? 0f
                    : group.Max(x => (float)(Math.Abs(x.DegreesPerUnit - allSlope) / allSlope));
                var axisSpread = MaxVectorPairAngle(group.Select(x => x.Axis));
                var maxAngle = group.Max(x => x.AngleDegrees);
                var positiveLimit = limitAxis.HasValue && limitAvatarAxis >= 0
                    ? Math.Abs(limitAxis.Value.LimitMax[limitAvatarAxis]) * 180f / MathF.PI
                    : 0f;
                var negativeLimit = limitAxis.HasValue && limitAvatarAxis >= 0
                    ? Math.Abs(limitAxis.Value.LimitMin[limitAvatarAxis]) * 180f / MathF.PI
                    : 0f;
                var positiveLimitRatio = positiveSlope > 0.0001 && positiveLimit > 0.0001
                    ? (float)(positiveSlope / positiveLimit)
                    : 0f;
                var negativeLimitRatio = negativeSlope > 0.0001 && negativeLimit > 0.0001
                    ? (float)(negativeSlope / negativeLimit)
                    : 0f;
                var positiveExplained = positive.Length == 0 || RatioNearOne(positiveLimitRatio, 0.25f);
                var negativeExplained = negative.Length == 0 || RatioNearOne(negativeLimitRatio, 0.25f);
                rows.Add(new SingleMuscleMultiValueLinearityRow(
                    group[0].MuscleIndex,
                    group[0].MuscleName,
                    humanBone,
                    path,
                    ClassifyBodyGroup(path),
                    group[0].BaseValue,
                    values.Length,
                    group.Count,
                    positive.Length,
                    negative.Length,
                    maxAngle,
                    positiveSlope,
                    negativeSlope,
                    maxRelativeSlopeError,
                    axisSpread,
                    targetAvatarAxis,
                    limitHumanBone,
                    limitAvatarAxis,
                    positiveLimit,
                    negativeLimit,
                    positiveLimitRatio,
                    negativeLimitRatio,
                    axisSpread <= 2f,
                    positiveExplained && negativeExplained && (positive.Length > 0 || negative.Length > 0)));
            }

            var ordered = rows
                .OrderByDescending(x => x.BodyGroup is "Arm" or "Leg" ? 1 : 0)
                .ThenByDescending(x => Math.Max(x.MaxRelativeSlopeError, x.MaxAxisSpreadDegrees / 10f))
                .ThenBy(x => x.MuscleName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (ordered.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["reason"] = "not_enough_distinct_probe_values",
                    ["probeCount"] = probes.Length,
                    ["rule"] = "Diagnostic only: checks whether multiple values for the same Unity muscle form a stable axis and a piecewise-linear angle slope explained by AvatarConstant limits.",
                };
            }

            var multiValue = ordered.Where(x => x.ValueCount >= 3).ToArray();
            var focusRows = ordered
                .Where(x => string.Equals(x.BodyGroup, "Arm", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(x.BodyGroup, "Leg", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var mostlyLinear = ordered.Count(x => x.AxisStable && x.MaxRelativeSlopeError <= 0.1f);
            var limitExplained = ordered.Count(x => x.LimitExplained);
            var focusLimitExplained = focusRows.Count(x => x.LimitExplained);
            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: uses multi-value Unity single-muscle probes to separate angle scaling from axis-space errors. It compares slope angle/abs(value-baseValue) against AvatarConstant limitMin/limitMax in degrees.",
                ["source"] = "Unity internalAvatarPoseMuscleProbes when available, otherwise Transform.local muscleProbes.",
                ["probeCount"] = probes.Length,
                ["rowCount"] = ordered.Length,
                ["multiValueRowCount"] = multiValue.Length,
                ["mostlyLinearCount"] = mostlyLinear,
                ["mostlyLinearRate"] = ordered.Length == 0 ? 0f : (float)mostlyLinear / ordered.Length,
                ["limitExplainedCount"] = limitExplained,
                ["limitExplainedRate"] = ordered.Length == 0 ? 0f : (float)limitExplained / ordered.Length,
                ["focusBodyGroups"] = "Arm,Leg",
                ["focusRowCount"] = focusRows.Length,
                ["focusLimitExplainedCount"] = focusLimitExplained,
                ["focusLimitExplainedRate"] = focusRows.Length == 0 ? 0f : (float)focusLimitExplained / focusRows.Length,
                ["focusMaxAxisSpreadDegrees"] = focusRows.Length == 0 ? 0f : focusRows.Max(x => x.MaxAxisSpreadDegrees),
                ["focusMaxRelativeSlopeError"] = focusRows.Length == 0 ? 0f : focusRows.Max(x => x.MaxRelativeSlopeError),
                ["maxRelativeSlopeError"] = ordered.Max(x => x.MaxRelativeSlopeError),
                ["avgRelativeSlopeError"] = ordered.Average(x => x.MaxRelativeSlopeError),
                ["maxAxisSpreadDegrees"] = ordered.Max(x => x.MaxAxisSpreadDegrees),
                ["avgAxisSpreadDegrees"] = ordered.Average(x => x.MaxAxisSpreadDegrees),
                ["bodyGroupSummary"] = new JArray(ordered
                    .GroupBy(x => x.BodyGroup, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new JObject
                    {
                        ["bodyGroup"] = group.Key,
                        ["rowCount"] = group.Count(),
                        ["mostlyLinearRate"] = (float)group.Count(x => x.AxisStable && x.MaxRelativeSlopeError <= 0.1f) / group.Count(),
                        ["limitExplainedRate"] = (float)group.Count(x => x.LimitExplained) / group.Count(),
                        ["maxRelativeSlopeError"] = group.Max(x => x.MaxRelativeSlopeError),
                        ["maxAxisSpreadDegrees"] = group.Max(x => x.MaxAxisSpreadDegrees),
                    })),
                ["topSlopeOutliers"] = ToJsonRows(ordered
                    .OrderByDescending(x => x.MaxRelativeSlopeError)
                    .ThenByDescending(x => x.MaxAngleDegrees)
                    .Take(48)),
                ["topAxisSpreadOutliers"] = ToJsonRows(ordered
                    .OrderByDescending(x => x.MaxAxisSpreadDegrees)
                    .ThenByDescending(x => x.MaxAngleDegrees)
                    .Take(48)),
                ["topArmLegRows"] = ToJsonRows(ordered
                    .Where(x => x.BodyGroup is "Arm" or "Leg")
                    .Take(96)),
            };
        }

        private static JObject BuildSingleMuscleDeltaAxisSummary(IReadOnlyCollection<SingleMuscleDeltaCorrectionRow> rows, JObject solver)
        {
            var items = (rows ?? Array.Empty<SingleMuscleDeltaCorrectionRow>())
                .Select(row =>
                {
                    var predicted = ToAxisAngle(row.PredictedDelta);
                    var unity = ToAxisAngle(row.UnityDelta);
                    var axisDegrees = AxisAngleDegrees(predicted.Axis, unity.Axis);
                    var ratio = Math.Abs(predicted.AngleDegrees) <= 0.0001f ? 0f : unity.AngleDegrees / predicted.AngleDegrees;
                    return new SingleMuscleDeltaAxisRow(
                        row.HumanBone,
                        row.Attribute,
                        row.Value,
                        row.TargetPath,
                        row.ProbePath,
                        predicted.Axis,
                        predicted.AngleDegrees,
                        unity.Axis,
                        unity.AngleDegrees,
                        axisDegrees,
                        ratio,
                        row.CurrentErrorDegrees,
                        row.BestApplyMode);
                })
                .ToArray();
            if (items.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["rowCount"] = 0,
                    ["rule"] = "Diagnostic only: converts current and Unity single-muscle deltas to axis-angle so axis direction errors can be separated from angle magnitude errors.",
                };
            }

            var byBodyGroup = items
                .GroupBy(x => ClassifyBodyGroup(x.TargetPath), StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var groupItems = group.ToArray();
                    return new JObject
                    {
                        ["bodyGroup"] = group.Key,
                        ["rowCount"] = groupItems.Length,
                        ["maxAxisErrorDegrees"] = groupItems.Max(x => x.AxisErrorDegrees),
                        ["avgAxisErrorDegrees"] = groupItems.Average(x => x.AxisErrorDegrees),
                        ["maxCurrentErrorDegrees"] = groupItems.Max(x => x.CurrentErrorDegrees),
                        ["avgAngleRatio"] = groupItems.Average(x => x.AngleRatio),
                        ["worstAttribute"] = groupItems
                            .OrderByDescending(x => x.AxisErrorDegrees)
                            .ThenBy(x => x.Attribute, StringComparer.OrdinalIgnoreCase)
                            .First()
                            .Attribute,
                    };
                })
                .OrderByDescending(x => (float?)x["maxAxisErrorDegrees"] ?? 0f)
                .ThenBy(x => (string)x["bodyGroup"], StringComparer.OrdinalIgnoreCase);

            var byHumanBone = items
                .GroupBy(x => x.HumanBone, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var groupItems = group.ToArray();
                    return new JObject
                    {
                        ["humanBone"] = group.Key,
                        ["bodyGroup"] = ClassifyBodyGroup(groupItems[0].TargetPath),
                        ["rowCount"] = groupItems.Length,
                        ["maxAxisErrorDegrees"] = groupItems.Max(x => x.AxisErrorDegrees),
                        ["avgAxisErrorDegrees"] = groupItems.Average(x => x.AxisErrorDegrees),
                        ["maxCurrentErrorDegrees"] = groupItems.Max(x => x.CurrentErrorDegrees),
                        ["avgAngleRatio"] = groupItems.Average(x => x.AngleRatio),
                        ["worstAttribute"] = groupItems
                            .OrderByDescending(x => x.AxisErrorDegrees)
                            .ThenBy(x => x.Attribute, StringComparer.OrdinalIgnoreCase)
                            .First()
                            .Attribute,
                    };
                })
                .OrderByDescending(x => (float?)x["maxAxisErrorDegrees"] ?? 0f)
                .ThenBy(x => (string)x["humanBone"], StringComparer.OrdinalIgnoreCase);

            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: converts current and Unity single-muscle deltas to axis-angle. High axis error means the Avatar/muscle axis or mirror rule is wrong; low axis error with bad angle ratio means limit/magnitude mapping is wrong.",
                ["rowCount"] = items.Length,
                ["maxAxisErrorDegrees"] = items.Max(x => x.AxisErrorDegrees),
                ["avgAxisErrorDegrees"] = items.Average(x => x.AxisErrorDegrees),
                ["maxCurrentErrorDegrees"] = items.Max(x => x.CurrentErrorDegrees),
                ["avgAngleRatio"] = items.Average(x => x.AngleRatio),
                ["basisCandidateSummary"] = BuildSingleMuscleAxisBasisCandidateSummary(rows),
                ["avatarAxisProjectionSummary"] = BuildSingleMuscleAvatarAxisProjectionSummary(rows),
                ["signedAxisTiltSummary"] = BuildSingleMuscleSignedAxisTiltSummary(rows, solver),
                ["byBodyGroup"] = new JArray(byBodyGroup),
                ["byHumanBone"] = new JArray(byHumanBone),
                ["topAxisErrors"] = ToJsonRows(items
                    .OrderByDescending(x => x.AxisErrorDegrees)
                    .ThenBy(x => x.HumanBone, StringComparer.OrdinalIgnoreCase)
                    .Take(96)),
            };
        }

        private static JObject BuildSingleMuscleSignedAxisTiltSummary(IEnumerable<SingleMuscleDeltaCorrectionRow> rows, JObject solver)
        {
            var items = rows?.ToArray() ?? Array.Empty<SingleMuscleDeltaCorrectionRow>();
            if (items.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["rowCount"] = 0,
                    ["rule"] = "Diagnostic only: folds +1/-1 single-muscle probes into one signed direction per humanBone+attribute and compares Unity's true axis with the current solver axis.",
                };
            }

            var signedRows = items
                .Select(row =>
                {
                    var predicted = ToAxisAngle(row.PredictedDelta);
                    var unity = ToAxisAngle(row.UnityDelta);
                    var sign = row.Value < 0f ? -1f : 1f;
                    var predictedAxis = NormalizeVector3(new[] { predicted.Axis[0] * sign, predicted.Axis[1] * sign, predicted.Axis[2] * sign });
                    var unityAxis = NormalizeVector3(new[] { unity.Axis[0] * sign, unity.Axis[1] * sign, unity.Axis[2] * sign });
                    return new SingleMuscleSignedAxisSample(
                        row.HumanBone,
                        row.Attribute,
                        row.TargetPath,
                        row.ProbePath,
                        row.Value,
                        predictedAxis,
                        unityAxis,
                        row.TargetPreQ,
                        row.TargetPostQ,
                        predicted.AngleDegrees,
                        unity.AngleDegrees,
                        AxisAngleDegrees(predictedAxis, unityAxis),
                        row.CurrentErrorDegrees);
                })
                .ToArray();

            var groups = signedRows
                .GroupBy(x => $"{x.HumanBone}|{x.Attribute}|{x.TargetPath}", StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var groupItems = group.ToArray();
                    var first = groupItems[0];
                    var predictedAxis = AverageAxis(groupItems.Select(x => x.PredictedAxis));
                    var unityAxis = AverageAxis(groupItems.Select(x => x.UnityAxis));
                    var predictedNearest = FindNearestSignedAvatarAxis(predictedAxis);
                    var unityNearest = FindNearestSignedAvatarAxis(unityAxis);
                    var sourceCandidate = FindNearestAxisSourceCandidate(
                        predictedAxis,
                        unityAxis,
                        predictedNearest.Label,
                        unityNearest.Label,
                        groupItems[0].TargetPreQ,
                        groupItems[0].TargetPostQ,
                        first.HumanBone,
                        first.TargetPath,
                        solver);
                    var unitySpread = MaxVectorPairAngle(groupItems.Select(x => x.UnityAxis));
                    var predictedSpread = MaxVectorPairAngle(groupItems.Select(x => x.PredictedAxis));
                    return new SingleMuscleSignedAxisTiltRow(
                        first.HumanBone,
                        first.Attribute,
                        first.TargetPath,
                        ClassifyBodyGroup(first.TargetPath),
                        groupItems.Length,
                        predictedAxis,
                        unityAxis,
                        predictedNearest.Label,
                        predictedNearest.ErrorDegrees,
                        unityNearest.Label,
                        unityNearest.ErrorDegrees,
                        sourceCandidate.Name,
                        sourceCandidate.ErrorDegrees,
                        AxisAngleDegrees(predictedAxis, unityAxis),
                        predictedSpread,
                        unitySpread,
                        groupItems.Max(x => x.CurrentErrorDegrees),
                        groupItems.Average(x => x.CurrentErrorDegrees),
                        groupItems.Average(x => x.UnityAngleDegrees / MathF.Max(0.0001f, x.PredictedAngleDegrees)));
                })
                .OrderByDescending(x => x.AxisTiltDegrees)
                .ThenByDescending(x => x.MaxCurrentErrorDegrees)
                .ThenBy(x => x.HumanBone, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var byBodyGroup = groups
                .GroupBy(x => x.BodyGroup, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var groupItems = group.ToArray();
                    return new JObject
                    {
                        ["bodyGroup"] = group.Key,
                        ["rowCount"] = groupItems.Length,
                        ["maxAxisTiltDegrees"] = groupItems.Max(x => x.AxisTiltDegrees),
                        ["avgAxisTiltDegrees"] = groupItems.Average(x => x.AxisTiltDegrees),
                        ["maxUnityAxisSpreadDegrees"] = groupItems.Max(x => x.UnityAxisSpreadDegrees),
                        ["avgUnityAxisSpreadDegrees"] = groupItems.Average(x => x.UnityAxisSpreadDegrees),
                        ["avgAngleRatio"] = groupItems.Average(x => x.AvgAngleRatio),
                        ["worstAttribute"] = groupItems
                            .OrderByDescending(x => x.AxisTiltDegrees)
                            .ThenByDescending(x => x.MaxCurrentErrorDegrees)
                            .First()
                            .Attribute,
                    };
                })
                .OrderByDescending(x => (float?)x["maxAxisTiltDegrees"] ?? 0f)
                .ThenBy(x => (string)x["bodyGroup"], StringComparer.OrdinalIgnoreCase);

            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: +1 and -1 probes are sign-folded before averaging. Low unityAxisSpreadDegrees means Unity's true axis is stable for that muscle; high axisTiltDegrees shows how far the current solver axis is tilted away from Unity.",
                ["rowCount"] = signedRows.Length,
                ["groupCount"] = groups.Length,
                ["maxAxisTiltDegrees"] = groups.Length == 0 ? 0f : groups.Max(x => x.AxisTiltDegrees),
                ["avgAxisTiltDegrees"] = groups.Length == 0 ? 0.0 : groups.Average(x => x.AxisTiltDegrees),
                ["maxUnityAxisSpreadDegrees"] = groups.Length == 0 ? 0f : groups.Max(x => x.UnityAxisSpreadDegrees),
                ["avgUnityAxisSpreadDegrees"] = groups.Length == 0 ? 0.0 : groups.Average(x => x.UnityAxisSpreadDegrees),
                ["humanLimitCandidateStatus"] = BuildHumanLimitCandidateStatus(groups, solver),
                ["axisSourceCandidateSummary"] = BuildAxisSourceCandidateSummary(groups),
                ["byBodyGroup"] = new JArray(byBodyGroup),
                ["topAxisTilts"] = ToJsonRows(groups.Take(96)),
            };
        }

        private static JObject BuildHumanLimitCandidateStatus(IEnumerable<SingleMuscleSignedAxisTiltRow> rows, JObject solver)
        {
            var items = rows?.ToArray() ?? Array.Empty<SingleMuscleSignedAxisTiltRow>();
            var humanBoneLimits = solver?["humanBoneLimits"] as JObject;
            if (humanBoneLimits == null || !humanBoneLimits.HasValues)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["groupCount"] = items.Length,
                    ["rule"] = "Diagnostic only: humanBoneLimits are exported by newer AnimeStudio builds from Unity HumanDescription.m_Human[*].m_Limit. Refresh model Avatar metadata before using this evidence.",
                };
            }

            var groupsWithLimit = items.Count(x => TryGetHumanLimitObject(solver, x.HumanBone, out _));
            var bestHumanLimitRows = items
                .Where(x => x.NearestAxisSourceCandidate?.StartsWith("humanLimit_", StringComparison.OrdinalIgnoreCase) == true)
                .ToArray();
            return new JObject
            {
                ["status"] = "ok",
                ["groupCount"] = items.Length,
                ["humanBoneLimitCount"] = humanBoneLimits.Properties().Count(),
                ["groupsWithHumanLimit"] = groupsWithLimit,
                ["bestHumanLimitCandidateGroupCount"] = bestHumanLimitRows.Length,
                ["bestHumanLimitCandidateRate"] = items.Length == 0 ? 0.0 : (double)bestHumanLimitRows.Length / items.Length,
                ["avgBestHumanLimitErrorDegrees"] = bestHumanLimitRows.Length == 0 ? 0.0 : bestHumanLimitRows.Average(x => x.NearestAxisSourceErrorDegrees),
                ["maxBestHumanLimitErrorDegrees"] = bestHumanLimitRows.Length == 0 ? 0f : bestHumanLimitRows.Max(x => x.NearestAxisSourceErrorDegrees),
                ["rule"] = "Diagnostic only: checks whether Unity's true single-muscle axis is closest to HumanDescription per-bone limit vectors. This is evidence for formula work, not a production binding rule.",
            };
        }

        private static JObject BuildAxisSourceCandidateSummary(IEnumerable<SingleMuscleSignedAxisTiltRow> rows)
        {
            var items = rows?.ToArray() ?? Array.Empty<SingleMuscleSignedAxisTiltRow>();
            if (items.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["rowCount"] = 0,
                };
            }

            var byCandidate = items
                .GroupBy(x => x.NearestAxisSourceCandidate, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var groupItems = group.ToArray();
                    return new JObject
                    {
                        ["candidate"] = group.Key,
                        ["rowCount"] = groupItems.Length,
                        ["maxCandidateErrorDegrees"] = groupItems.Max(x => x.NearestAxisSourceErrorDegrees),
                        ["avgCandidateErrorDegrees"] = groupItems.Average(x => x.NearestAxisSourceErrorDegrees),
                        ["maxAxisTiltDegrees"] = groupItems.Max(x => x.AxisTiltDegrees),
                        ["avgAxisTiltDegrees"] = groupItems.Average(x => x.AxisTiltDegrees),
                    };
                })
                .OrderByDescending(x => (int?)x["rowCount"] ?? 0)
                .ThenBy(x => (double?)x["avgCandidateErrorDegrees"] ?? double.MaxValue)
                .ThenBy(x => (string)x["candidate"], StringComparer.OrdinalIgnoreCase);
            var byBodyGroupAndMuscleFamily = items
                .GroupBy(x => $"{x.BodyGroup}\u001f{ClassifyMuscleFamily(x.Attribute)}", StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var groupItems = group.ToArray();
                    var keyParts = group.Key.Split('\u001f');
                    var bodyGroup = keyParts.Length > 0 ? keyParts[0] : "Unknown";
                    var muscleFamily = keyParts.Length > 1 ? keyParts[1] : "Other";
                    var worst = groupItems
                        .OrderByDescending(x => x.NearestAxisSourceErrorDegrees)
                        .ThenByDescending(x => x.AxisTiltDegrees)
                        .ThenBy(x => x.Attribute, StringComparer.OrdinalIgnoreCase)
                        .First();
                    return new JObject
                    {
                        ["bodyGroup"] = bodyGroup,
                        ["muscleFamily"] = muscleFamily,
                        ["rowCount"] = groupItems.Length,
                        ["maxAxisTiltDegrees"] = groupItems.Max(x => x.AxisTiltDegrees),
                        ["avgAxisTiltDegrees"] = groupItems.Average(x => x.AxisTiltDegrees),
                        ["maxCandidateErrorDegrees"] = groupItems.Max(x => x.NearestAxisSourceErrorDegrees),
                        ["avgCandidateErrorDegrees"] = groupItems.Average(x => x.NearestAxisSourceErrorDegrees),
                        ["maxUnityAxisSpreadDegrees"] = groupItems.Max(x => x.UnityAxisSpreadDegrees),
                        ["avgAngleRatio"] = groupItems.Average(x => x.AvgAngleRatio),
                        ["dominantNearestCandidate"] = groupItems
                            .GroupBy(x => x.NearestAxisSourceCandidate, StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(x => x.Count())
                            .ThenBy(x => x.Average(y => y.NearestAxisSourceErrorDegrees))
                            .First()
                            .Key,
                        ["worstHumanBone"] = worst.HumanBone,
                        ["worstAttribute"] = worst.Attribute,
                        ["worstTargetPath"] = worst.TargetPath,
                    };
                })
                .OrderByDescending(x => IsFocusBodyGroup((string)x["bodyGroup"]) ? 1 : 0)
                .ThenByDescending(x => (double?)x["avgCandidateErrorDegrees"] ?? 0.0)
                .ThenByDescending(x => (float?)x["maxAxisTiltDegrees"] ?? 0f)
                .ThenBy(x => (string)x["bodyGroup"], StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => (string)x["muscleFamily"], StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: for each signed-axis group, tests whether Unity's true axis is closest to the current predicted axis, a signed canonical Avatar axis, or that canonical axis transformed by preQ/postQ/zero.",
                ["rowCount"] = items.Length,
                ["candidateCounts"] = new JArray(byCandidate),
                ["poseAxisCandidateStatus"] = BuildPoseAxisCandidateStatus(items),
                ["byBodyGroupAndMuscleFamily"] = new JArray(byBodyGroupAndMuscleFamily),
                ["nextAxisWork"] = BuildNextAxisWorkSummary(byBodyGroupAndMuscleFamily),
                ["topUnexplainedAxes"] = ToJsonRows(items
                    .OrderByDescending(x => x.NearestAxisSourceErrorDegrees)
                    .ThenByDescending(x => x.AxisTiltDegrees)
                    .Take(64)),
            };
        }

        private static JObject BuildPoseAxisCandidateStatus(IReadOnlyCollection<SingleMuscleSignedAxisTiltRow> rows)
        {
            var items = rows?.ToArray() ?? Array.Empty<SingleMuscleSignedAxisTiltRow>();
            var poseRows = items
                .Where(x => IsPoseAxisCandidateName(x.NearestAxisSourceCandidate))
                .ToArray();
            var focusPoseRows = poseRows
                .Where(x => IsFocusBodyGroup(x.BodyGroup))
                .ToArray();
            var armPoseRows = poseRows
                .Where(x => string.Equals(x.BodyGroup, "Arm", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return new JObject
            {
                ["status"] = items.Length == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: counts rows where AvatarConstant pose/local/parent-child pose candidates explain Unity's true single-muscle axis better than simple preQ/postQ candidates.",
                ["rowCount"] = items.Length,
                ["poseCandidateRowCount"] = poseRows.Length,
                ["poseCandidateRate"] = items.Length == 0 ? 0.0 : (double)poseRows.Length / items.Length,
                ["focusPoseCandidateRowCount"] = focusPoseRows.Length,
                ["focusPoseCandidateRate"] = items.Count(IsFocusAxisRow) == 0 ? 0.0 : (double)focusPoseRows.Length / items.Count(IsFocusAxisRow),
                ["armPoseCandidateRowCount"] = armPoseRows.Length,
                ["armPoseCandidateRate"] = items.Count(x => string.Equals(x.BodyGroup, "Arm", StringComparison.OrdinalIgnoreCase)) == 0 ? 0.0 : (double)armPoseRows.Length / items.Count(x => string.Equals(x.BodyGroup, "Arm", StringComparison.OrdinalIgnoreCase)),
                ["avgPoseCandidateErrorDegrees"] = poseRows.Length == 0 ? 0.0 : poseRows.Average(x => x.NearestAxisSourceErrorDegrees),
                ["maxPoseCandidateErrorDegrees"] = poseRows.Length == 0 ? 0f : poseRows.Max(x => x.NearestAxisSourceErrorDegrees),
                ["topPoseCandidateRows"] = ToJsonRows(poseRows
                    .OrderByDescending(x => x.NearestAxisSourceErrorDegrees)
                    .ThenByDescending(x => x.AxisTiltDegrees)
                    .Take(24)),
            };
        }

        private static bool IsFocusAxisRow(SingleMuscleSignedAxisTiltRow row)
        {
            return row != null && IsFocusBodyGroup(row.BodyGroup);
        }

        private static bool IsPoseAxisCandidateName(string candidateName)
        {
            return !string.IsNullOrWhiteSpace(candidateName) &&
                (candidateName.IndexOf("Pose", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 candidateName.IndexOf("ParentToChild", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 candidateName.IndexOf("ChildToParent", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static JObject BuildNextAxisWorkSummary(JObject[] rows)
        {
            var focus = (rows ?? Array.Empty<JObject>())
                .Where(x => IsFocusBodyGroup((string)x["bodyGroup"]))
                .OrderByDescending(x => (double?)x["avgCandidateErrorDegrees"] ?? 0.0)
                .ThenByDescending(x => (float?)x["maxAxisTiltDegrees"] ?? 0f)
                .ToArray();
            if (focus.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                };
            }

            var worst = focus[0];
            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: ranks Arm/Leg muscle families by how poorly existing deterministic axis candidates explain Unity's true single-muscle axis. Use this to decide the next formula probe, not as production evidence.",
                ["worstBodyGroup"] = (string)worst["bodyGroup"],
                ["worstMuscleFamily"] = (string)worst["muscleFamily"],
                ["worstAvgCandidateErrorDegrees"] = (double?)worst["avgCandidateErrorDegrees"] ?? 0.0,
                ["worstMaxAxisTiltDegrees"] = (float?)worst["maxAxisTiltDegrees"] ?? 0f,
                ["worstAttribute"] = (string)worst["worstAttribute"],
                ["recommendedNextProbe"] = BuildNextAxisProbeRecommendation(worst),
                ["topFocusFamilies"] = new JArray(focus.Take(12)),
            };
        }

        private static string BuildNextAxisProbeRecommendation(JObject worst)
        {
            var bodyGroup = (string)worst?["bodyGroup"] ?? "Unknown";
            var muscleFamily = (string)worst?["muscleFamily"] ?? "Other";
            if (string.Equals(muscleFamily, "Twist", StringComparison.OrdinalIgnoreCase))
            {
                return $"{bodyGroup} Twist 仍有最大未解释轴偏差；下一步应用 Unity 单 muscle probe 枚举 source long bone/current bone、target bone、本地/父本地乘法顺序和左右 mirror，先通过完整 timeline 复验再改生产 solver。";
            }

            if (string.Equals(muscleFamily, "Stretch", StringComparison.OrdinalIgnoreCase))
            {
                return $"{bodyGroup} Stretch 可能涉及 Unity TranslateDoF 或非普通旋转 muscle；下一步应单独检查 hasTranslationDoF、stretch 参数和是否需要 translation channel，不要混入普通 swing/twist 公式。";
            }

            if (string.Equals(muscleFamily, "FrontBack", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(muscleFamily, "DownUp", StringComparison.OrdinalIgnoreCase))
            {
                return $"{bodyGroup} swing 轴仍未由现有 preQ/postQ 候选解释；下一步应黑盒测试 Unity 的 swing 轴基和 twist/swing 组合顺序，而不是只加静态 rest offset。";
            }

            return $"{bodyGroup} {muscleFamily} 轴候选仍不稳定；下一步先扩展 Unity probe，而不是迁入生产公式。";
        }

        private static bool IsFocusBodyGroup(string bodyGroup)
        {
            return string.Equals(bodyGroup, "Arm", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(bodyGroup, "Leg", StringComparison.OrdinalIgnoreCase);
        }

        private static string ClassifyMuscleFamily(string attribute)
        {
            if (string.IsNullOrWhiteSpace(attribute))
            {
                return "Other";
            }

            if (attribute.IndexOf("Twist", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Twist";
            }

            if (attribute.IndexOf("Stretch", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Stretch";
            }

            if (attribute.IndexOf("Front-Back", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "FrontBack";
            }

            if (attribute.IndexOf("Down-Up", StringComparison.OrdinalIgnoreCase) >= 0 ||
                attribute.IndexOf("Up-Down", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "DownUp";
            }

            if (attribute.IndexOf("In-Out", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "InOut";
            }

            if (attribute.IndexOf("Close", StringComparison.OrdinalIgnoreCase) >= 0 ||
                attribute.IndexOf("Spread", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Finger";
            }

            return "Other";
        }

        private static (string Name, float ErrorDegrees) FindNearestAxisSourceCandidate(
            float[] predictedAxis,
            float[] unityAxis,
            string predictedNearestLabel,
            string unityNearestLabel,
            float[] preQ,
            float[] postQ,
            string humanBone,
            string targetPath,
            JObject solver)
        {
            var pre = Normalize(preQ);
            var post = Normalize(postQ);
            var zero = Normalize(Multiply(pre, Inverse(post)));
            var predictedNearest = SignedAxisLabelToVector(predictedNearestLabel);
            var unityNearest = SignedAxisLabelToVector(unityNearestLabel);
            var candidates = new List<(string Name, float[] Axis)>
            {
                ("current_predicted_axis", NormalizeVector3(predictedAxis)),
                ("predicted_nearest_axis", predictedNearest),
                ("unity_nearest_axis", unityNearest),
                ("preQ_predicted_nearest_sandwich", RotateVectorByQuaternion(predictedNearest, pre)),
                ("inverse_preQ_predicted_nearest_sandwich", RotateVectorByQuaternion(predictedNearest, Inverse(pre))),
                ("postQ_predicted_nearest_sandwich", RotateVectorByQuaternion(predictedNearest, post)),
                ("inverse_postQ_predicted_nearest_sandwich", RotateVectorByQuaternion(predictedNearest, Inverse(post))),
                ("zero_pre_inverse_post_predicted_nearest_sandwich", RotateVectorByQuaternion(predictedNearest, zero)),
                ("inverse_zero_predicted_nearest_sandwich", RotateVectorByQuaternion(predictedNearest, Inverse(zero))),
                ("preQ_unity_nearest_sandwich", RotateVectorByQuaternion(unityNearest, pre)),
                ("inverse_preQ_unity_nearest_sandwich", RotateVectorByQuaternion(unityNearest, Inverse(pre))),
                ("postQ_unity_nearest_sandwich", RotateVectorByQuaternion(unityNearest, post)),
                ("inverse_postQ_unity_nearest_sandwich", RotateVectorByQuaternion(unityNearest, Inverse(post))),
                ("zero_pre_inverse_post_unity_nearest_sandwich", RotateVectorByQuaternion(unityNearest, zero)),
                ("inverse_zero_unity_nearest_sandwich", RotateVectorByQuaternion(unityNearest, Inverse(zero))),
            };
            candidates.AddRange(BuildPoseAxisSourceCandidates(targetPath, humanBone, predictedNearest, unityNearest, solver));
            candidates.AddRange(BuildHumanLimitAxisSourceCandidates(humanBone, solver));

            var best = candidates
                .Select(x => new
                {
                    x.Name,
                    Error = AxisAngleDegrees(x.Axis, unityAxis),
                })
                .OrderBy(x => x.Error)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .First();
            return (best.Name, best.Error);
        }

        private static IEnumerable<(string Name, float[] Axis)> BuildPoseAxisSourceCandidates(
            string targetPath,
            string humanBone,
            float[] predictedNearest,
            float[] unityNearest,
            JObject solver)
        {
            var poseCandidates = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            var solverNodes = solver?["skeleton"]?["nodes"] as JArray;
            var humanBoneIndex = solver?["humanBoneIndex"] as JArray;
            AddAvatarPoseCorrectionCandidates(poseCandidates, targetPath, solver, solverNodes, humanBoneIndex, humanBone);
            foreach (var pair in poseCandidates)
            {
                // 这里只做 oracle 诊断：检查 Unity 真实轴是否能由 AvatarConstant 的参考姿态、
                // local pose 或父子 pose 空间解释，不能直接当成生产公式。
                yield return ($"{pair.Key}_predicted_nearest_axis", RotateVectorByQuaternion(predictedNearest, pair.Value));
                yield return ($"inverse({pair.Key})_predicted_nearest_axis", RotateVectorByQuaternion(predictedNearest, Inverse(pair.Value)));
                yield return ($"{pair.Key}_unity_nearest_axis", RotateVectorByQuaternion(unityNearest, pair.Value));
                yield return ($"inverse({pair.Key})_unity_nearest_axis", RotateVectorByQuaternion(unityNearest, Inverse(pair.Value)));
            }
        }

        private static IEnumerable<(string Name, float[] Axis)> BuildHumanLimitAxisSourceCandidates(string humanBone, JObject solver)
        {
            if (!TryGetHumanLimitObject(solver, humanBone, out var limit))
            {
                yield break;
            }

            if (TryReadVector3(limit["value"], out var value))
            {
                yield return ("humanLimit_value_axis", value);
                yield return ("humanLimit_inverse_value_axis", NegateVector3(value));
            }
            if (TryReadVector3(limit["min"], out var min))
            {
                yield return ("humanLimit_min_axis", min);
                yield return ("humanLimit_inverse_min_axis", NegateVector3(min));
            }
            if (TryReadVector3(limit["max"], out var max))
            {
                yield return ("humanLimit_max_axis", max);
                yield return ("humanLimit_inverse_max_axis", NegateVector3(max));
            }
            if (TryReadVector3(limit["min"], out var minForRange) && TryReadVector3(limit["max"], out var maxForRange))
            {
                var range = new[]
                {
                    maxForRange[0] - minForRange[0],
                    maxForRange[1] - minForRange[1],
                    maxForRange[2] - minForRange[2],
                };
                var center = new[]
                {
                    maxForRange[0] + minForRange[0],
                    maxForRange[1] + minForRange[1],
                    maxForRange[2] + minForRange[2],
                };
                yield return ("humanLimit_range_axis", range);
                yield return ("humanLimit_inverse_range_axis", NegateVector3(range));
                yield return ("humanLimit_center_axis", center);
                yield return ("humanLimit_inverse_center_axis", NegateVector3(center));
            }
        }

        private static bool TryGetHumanLimitObject(JObject solver, string humanBone, out JObject limit)
        {
            limit = null;
            if (string.IsNullOrWhiteSpace(humanBone))
            {
                return false;
            }

            if (solver?["humanBoneLimits"] is not JObject limits || !limits.HasValues)
            {
                return false;
            }

            var entry = limits[humanBone] as JObject;
            limit = entry?["limit"] as JObject;
            return limit != null && limit.HasValues;
        }

        private static bool TryReadVector3(JToken token, out float[] vector)
        {
            vector = null;
            if (token is not JArray array || array.Count < 3)
            {
                return false;
            }

            vector = new[]
            {
                (float?)array[0] ?? 0f,
                (float?)array[1] ?? 0f,
                (float?)array[2] ?? 0f,
            };
            var length = MathF.Sqrt(vector[0] * vector[0] + vector[1] * vector[1] + vector[2] * vector[2]);
            return length > 0.000001f;
        }

        private static float[] NegateVector3(float[] vector)
        {
            var v = NormalizeVector3(vector);
            return new[] { -v[0], -v[1], -v[2] };
        }

        private static float[] SignedAxisLabelToVector(string label)
        {
            return label switch
            {
                "+X" => new[] { 1f, 0f, 0f },
                "-X" => new[] { -1f, 0f, 0f },
                "+Y" => new[] { 0f, 1f, 0f },
                "-Y" => new[] { 0f, -1f, 0f },
                "+Z" => new[] { 0f, 0f, 1f },
                "-Z" => new[] { 0f, 0f, -1f },
                _ => new[] { 1f, 0f, 0f },
            };
        }

        private static float[] RotateVectorByQuaternion(float[] vector, float[] quaternion)
        {
            var v = NormalizeVector3(vector);
            var q = Normalize(quaternion);
            var x = q[3] * v[0] + q[1] * v[2] - q[2] * v[1];
            var y = q[3] * v[1] - q[0] * v[2] + q[2] * v[0];
            var z = q[3] * v[2] + q[0] * v[1] - q[1] * v[0];
            var w = -(q[0] * v[0] + q[1] * v[1] + q[2] * v[2]);
            return NormalizeVector3(new[]
            {
                w * -q[0] + x * q[3] + y * -q[2] - z * -q[1],
                w * -q[1] - x * -q[2] + y * q[3] + z * -q[0],
                w * -q[2] + x * -q[1] - y * -q[0] + z * q[3],
            });
        }

        private static JObject BuildSingleMuscleAvatarAxisProjectionSummary(IEnumerable<SingleMuscleDeltaCorrectionRow> rows)
        {
            var items = rows?.ToArray() ?? Array.Empty<SingleMuscleDeltaCorrectionRow>();
            if (items.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["rowCount"] = 0,
                    ["rule"] = "Diagnostic only: projects current and Unity single-muscle deltas back toward Avatar middle space, then reports the nearest signed X/Y/Z axis.",
                };
            }

            var projectionRows = new List<SingleMuscleAvatarAxisProjectionRow>();
            foreach (var item in items)
            {
                foreach (var mode in new[]
                {
                    "identity_delta",
                    "inverse_pre_delta_post",
                    "inverse_pre_delta_pre",
                    "post_delta_inverse_post",
                    "inverse_zero_delta_zero",
                })
                {
                    var predictedMiddle = ProjectDeltaToAvatarAxisSpace(item.PredictedDelta, item, mode);
                    var unityMiddle = ProjectDeltaToAvatarAxisSpace(item.UnityDelta, item, mode);
                    var predictedAxis = ToAxisAngle(predictedMiddle);
                    var unityAxis = ToAxisAngle(unityMiddle);
                    var predictedNearest = FindNearestSignedAvatarAxis(predictedAxis.Axis);
                    var unityNearest = FindNearestSignedAvatarAxis(unityAxis.Axis);
                    projectionRows.Add(new SingleMuscleAvatarAxisProjectionRow(
                        mode,
                        item.HumanBone,
                        item.Attribute,
                        item.Value,
                        item.TargetPath,
                        item.ProbePath,
                        predictedNearest.Label,
                        predictedNearest.ErrorDegrees,
                        predictedAxis.AngleDegrees,
                        unityNearest.Label,
                        unityNearest.ErrorDegrees,
                        unityAxis.AngleDegrees,
                        string.Equals(predictedNearest.Label, unityNearest.Label, StringComparison.OrdinalIgnoreCase),
                        AxisAngleDegrees(predictedAxis.Axis, unityAxis.Axis),
                        QuaternionAngleDegrees(predictedMiddle, unityMiddle),
                        item.CurrentErrorDegrees));
                }
            }

            var byMode = projectionRows
                .GroupBy(x => x.ProjectionMode, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var groupItems = group.ToArray();
                    return new JObject
                    {
                        ["projectionMode"] = group.Key,
                        ["rowCount"] = groupItems.Length,
                        ["sameSignedAxisRate"] = groupItems.Average(x => x.SameSignedAxis ? 1.0 : 0.0),
                        ["avgProjectionRotationErrorDegrees"] = groupItems.Average(x => x.ProjectionRotationErrorDegrees),
                        ["maxProjectionRotationErrorDegrees"] = groupItems.Max(x => x.ProjectionRotationErrorDegrees),
                        ["avgAxisErrorDegrees"] = groupItems.Average(x => x.AxisErrorDegrees),
                        ["avgUnityNearestAxisErrorDegrees"] = groupItems.Average(x => x.UnityNearestAxisErrorDegrees),
                        ["unitySignedAxisCounts"] = BuildSignedAxisCounts(groupItems.Select(x => x.UnitySignedAxis)),
                        ["predictedSignedAxisCounts"] = BuildSignedAxisCounts(groupItems.Select(x => x.PredictedSignedAxis)),
                    };
                })
                .OrderByDescending(x => (double?)x["sameSignedAxisRate"] ?? 0.0)
                .ThenBy(x => (double?)x["avgProjectionRotationErrorDegrees"] ?? double.MaxValue)
                .ThenBy(x => (string)x["projectionMode"], StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var bestMode = (string)byMode.FirstOrDefault()?["projectionMode"] ?? "identity_delta";
            var bestRows = projectionRows
                .Where(x => string.Equals(x.ProjectionMode, bestMode, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            var byAttribute = bestRows
                .GroupBy(x => x.Attribute, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var groupItems = group.ToArray();
                    return new JObject
                    {
                        ["attribute"] = group.Key,
                        ["rowCount"] = groupItems.Length,
                        ["sameSignedAxisRate"] = groupItems.Average(x => x.SameSignedAxis ? 1.0 : 0.0),
                        ["unitySignedAxisCounts"] = BuildSignedAxisCounts(groupItems.Select(x => x.UnitySignedAxis)),
                        ["predictedSignedAxisCounts"] = BuildSignedAxisCounts(groupItems.Select(x => x.PredictedSignedAxis)),
                        ["maxCurrentErrorDegrees"] = groupItems.Max(x => x.CurrentErrorDegrees),
                    };
                })
                .OrderBy(x => (double?)x["sameSignedAxisRate"] ?? 0.0)
                .ThenByDescending(x => (float?)x["maxCurrentErrorDegrees"] ?? 0f)
                .ThenBy(x => (string)x["attribute"], StringComparer.OrdinalIgnoreCase);

            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: because solved=preQ*middle*inverse(postQ), this tries several reversible projections of the local delta back toward middle space. The nearest signed axis shows which Avatar axis Unity actually uses for each single-muscle probe.",
                ["rowCount"] = items.Length,
                ["projectionRowCount"] = projectionRows.Count,
                ["bestProjectionMode"] = bestMode,
                ["byProjectionMode"] = new JArray(byMode),
                ["expectedAxisFitSummary"] = BuildSingleMuscleExpectedAxisProjectionSummary(projectionRows),
                ["bestModeByAttribute"] = new JArray(byAttribute.Take(128)),
                ["topMismatches"] = ToJsonRows(bestRows
                    .Where(x => !x.SameSignedAxis)
                    .OrderByDescending(x => x.CurrentErrorDegrees)
                    .ThenByDescending(x => x.AxisErrorDegrees)
                    .Take(96)),
                ["topUnityAxisUncertainty"] = ToJsonRows(bestRows
                    .OrderByDescending(x => x.UnityNearestAxisErrorDegrees)
                    .ThenByDescending(x => x.CurrentErrorDegrees)
                    .Take(64)),
            };
        }

        private static JObject BuildSingleMuscleExpectedAxisProjectionSummary(IEnumerable<SingleMuscleAvatarAxisProjectionRow> rows)
        {
            var items = (rows ?? Enumerable.Empty<SingleMuscleAvatarAxisProjectionRow>())
                .Select(row =>
                {
                    var expectedAxis = CurrentExpectedAvatarAxisForMuscle(row.HumanBone, row.Attribute);
                    if (expectedAxis < 0)
                    {
                        return null;
                    }

                    var foldedUnityAxis = FoldSignedAxisLabelByValue(row.UnitySignedAxis, row.Value);
                    var foldedPredictedAxis = FoldSignedAxisLabelByValue(row.PredictedSignedAxis, row.Value);
                    var expectedLabel = AxisIndexToPositiveLabel(expectedAxis);
                    return new JObject
                    {
                        ["projectionMode"] = row.ProjectionMode,
                        ["humanBone"] = row.HumanBone,
                        ["attribute"] = row.Attribute,
                        ["targetPath"] = row.TargetPath,
                        ["bodyGroup"] = ClassifyBodyGroup(row.TargetPath),
                        ["value"] = row.Value,
                        ["expectedAxis"] = expectedAxis,
                        ["expectedAxisName"] = AvatarAxisName(expectedAxis),
                        ["expectedSignedAxis"] = expectedLabel,
                        ["unitySignedAxisFolded"] = foldedUnityAxis,
                        ["predictedSignedAxisFolded"] = foldedPredictedAxis,
                        ["unityMatchesExpectedAxis"] = string.Equals(foldedUnityAxis, expectedLabel, StringComparison.OrdinalIgnoreCase),
                        ["predictedMatchesExpectedAxis"] = string.Equals(foldedPredictedAxis, expectedLabel, StringComparison.OrdinalIgnoreCase),
                        ["unityExpectedAxisErrorDegrees"] = SignedAxisAngleDegrees(SignedAxisLabelToVector(foldedUnityAxis), AxisIndexToVector(expectedAxis)),
                        ["predictedExpectedAxisErrorDegrees"] = SignedAxisAngleDegrees(SignedAxisLabelToVector(foldedPredictedAxis), AxisIndexToVector(expectedAxis)),
                        ["unityNearestAxisErrorDegrees"] = row.UnityNearestAxisErrorDegrees,
                        ["projectionRotationErrorDegrees"] = row.ProjectionRotationErrorDegrees,
                        ["currentErrorDegrees"] = row.CurrentErrorDegrees,
                    };
                })
                .Where(x => x != null)
                .ToArray();
            if (items.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["rowCount"] = 0,
                };
            }

            var byMode = items
                .GroupBy(x => (string)x["projectionMode"], StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var groupItems = group.ToArray();
                    return new JObject
                    {
                        ["projectionMode"] = group.Key,
                        ["rowCount"] = groupItems.Length,
                        ["unityMatchesExpectedAxisRate"] = groupItems.Average(x => (bool?)x["unityMatchesExpectedAxis"] == true ? 1.0 : 0.0),
                        ["predictedMatchesExpectedAxisRate"] = groupItems.Average(x => (bool?)x["predictedMatchesExpectedAxis"] == true ? 1.0 : 0.0),
                        ["avgUnityExpectedAxisErrorDegrees"] = groupItems.Average(x => (float?)x["unityExpectedAxisErrorDegrees"] ?? 0f),
                        ["maxUnityExpectedAxisErrorDegrees"] = groupItems.Max(x => (float?)x["unityExpectedAxisErrorDegrees"] ?? 0f),
                        ["avgProjectionRotationErrorDegrees"] = groupItems.Average(x => (float?)x["projectionRotationErrorDegrees"] ?? 0f),
                    };
                })
                .OrderByDescending(x => (double?)x["unityMatchesExpectedAxisRate"] ?? 0.0)
                .ThenBy(x => (double?)x["avgUnityExpectedAxisErrorDegrees"] ?? double.MaxValue)
                .ThenBy(x => (double?)x["avgProjectionRotationErrorDegrees"] ?? double.MaxValue)
                .ThenBy(x => (string)x["projectionMode"], StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var bestMode = (string)byMode.FirstOrDefault()?["projectionMode"] ?? "identity_delta";
            var bestRows = items
                .Where(x => string.Equals((string)x["projectionMode"], bestMode, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: folds +1/-1 probes to a positive direction, then checks whether Unity's projected middle-space axis matches the current muscle's expected Avatar axis. If Unity does not match the expected axis, source/target-axis mapping is wrong; if it matches but rotation error stays high, composition or wrapping is wrong.",
                ["rowCount"] = items.Length,
                ["bestProjectionMode"] = bestMode,
                ["byProjectionMode"] = new JArray(byMode),
                ["byBodyGroupAndMuscleFamily"] = BuildExpectedAxisProjectionFamilySummary(bestRows),
                ["signPolicyCandidateSummary"] = BuildExpectedAxisSignPolicySummary(bestRows),
                ["topUnityExpectedAxisMismatches"] = new JArray(bestRows
                    .Where(x => (bool?)x["unityMatchesExpectedAxis"] != true)
                    .OrderByDescending(x => (float?)x["currentErrorDegrees"] ?? 0f)
                    .ThenByDescending(x => (float?)x["unityExpectedAxisErrorDegrees"] ?? 0f)
                    .Take(96)),
            };
        }

        private static JArray BuildExpectedAxisProjectionFamilySummary(JObject[] rows)
        {
            return new JArray((rows ?? Array.Empty<JObject>())
                .GroupBy(x => $"{(string)x["bodyGroup"]}\u001f{ClassifyMuscleFamily((string)x["attribute"])}", StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var items = group.ToArray();
                    var parts = group.Key.Split('\u001f');
                    var worst = items
                        .OrderByDescending(x => (float?)x["unityExpectedAxisErrorDegrees"] ?? 0f)
                        .ThenByDescending(x => (float?)x["currentErrorDegrees"] ?? 0f)
                        .First();
                    return new JObject
                    {
                        ["bodyGroup"] = parts.Length > 0 ? parts[0] : "Unknown",
                        ["muscleFamily"] = parts.Length > 1 ? parts[1] : "Other",
                        ["rowCount"] = items.Length,
                        ["unityMatchesExpectedAxisRate"] = items.Average(x => (bool?)x["unityMatchesExpectedAxis"] == true ? 1.0 : 0.0),
                        ["avgUnityExpectedAxisErrorDegrees"] = items.Average(x => (float?)x["unityExpectedAxisErrorDegrees"] ?? 0f),
                        ["maxUnityExpectedAxisErrorDegrees"] = items.Max(x => (float?)x["unityExpectedAxisErrorDegrees"] ?? 0f),
                        ["worstHumanBone"] = (string)worst["humanBone"],
                        ["worstAttribute"] = (string)worst["attribute"],
                        ["worstUnitySignedAxisFolded"] = (string)worst["unitySignedAxisFolded"],
                        ["worstExpectedSignedAxis"] = (string)worst["expectedSignedAxis"],
                    };
                })
                .OrderByDescending(x => IsFocusBodyGroup((string)x["bodyGroup"]) ? 1 : 0)
                .ThenBy(x => (double?)x["unityMatchesExpectedAxisRate"] ?? 1.0)
                .ThenByDescending(x => (double?)x["avgUnityExpectedAxisErrorDegrees"] ?? 0.0)
                .ThenBy(x => (string)x["bodyGroup"], StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => (string)x["muscleFamily"], StringComparer.OrdinalIgnoreCase));
        }

        private static JObject BuildExpectedAxisSignPolicySummary(JObject[] rows)
        {
            var groups = (rows ?? Array.Empty<JObject>())
                .GroupBy(x => $"{(string)x["humanBone"]}\u001f{(string)x["attribute"]}", StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var items = group.ToArray();
                    var first = items[0];
                    var same = items.Count(x => (bool?)x["unityMatchesExpectedAxis"] == true);
                    var opposite = items.Count(x => Math.Abs(((float?)x["unityExpectedAxisErrorDegrees"] ?? 0f) - 180f) <= 0.001f);
                    var orthogonal = items.Length - same - opposite;
                    var sameRate = items.Length == 0 ? 0.0 : (double)same / items.Length;
                    var oppositeRate = items.Length == 0 ? 0.0 : (double)opposite / items.Length;
                    var policy = oppositeRate >= 0.99
                        ? "invert_axis_sign_candidate"
                        : sameRate >= 0.99
                            ? "keep_current_sign_candidate"
                            : orthogonal > 0
                                ? "target_axis_remap_or_projection_candidate"
                                : "mixed_sign_needs_more_probe";
                    return new JObject
                    {
                        ["humanBone"] = (string)first["humanBone"],
                        ["attribute"] = (string)first["attribute"],
                        ["bodyGroup"] = (string)first["bodyGroup"],
                        ["muscleFamily"] = ClassifyMuscleFamily((string)first["attribute"]),
                        ["sampleCount"] = items.Length,
                        ["sameAxisCount"] = same,
                        ["oppositeAxisCount"] = opposite,
                        ["orthogonalAxisCount"] = orthogonal,
                        ["sameAxisRate"] = sameRate,
                        ["oppositeAxisRate"] = oppositeRate,
                        ["suggestedPolicy"] = policy,
                        ["expectedSignedAxis"] = (string)first["expectedSignedAxis"],
                        ["unitySignedAxisFoldedCounts"] = BuildSignedAxisCounts(items.Select(x => (string)x["unitySignedAxisFolded"])),
                        ["maxCurrentErrorDegrees"] = items.Max(x => (float?)x["currentErrorDegrees"] ?? 0f),
                    };
                })
                .OrderByDescending(x => IsFocusBodyGroup((string)x["bodyGroup"]) ? 1 : 0)
                .ThenByDescending(x => (string)x["suggestedPolicy"] == "invert_axis_sign_candidate" ? 1 : 0)
                .ThenByDescending(x => (string)x["suggestedPolicy"] == "target_axis_remap_or_projection_candidate" ? 1 : 0)
                .ThenByDescending(x => (float?)x["maxCurrentErrorDegrees"] ?? 0f)
                .ThenBy(x => (string)x["humanBone"], StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => (string)x["attribute"], StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new JObject
            {
                ["status"] = groups.Length == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: groups the best projected Unity middle-space axes by humanBone+attribute. Stable oppositeAxisRate means the muscle likely needs a deterministic sign flip in that projected formula; orthogonal rows point to target-axis remap instead.",
                ["rowCount"] = groups.Length,
                ["invertAxisSignCandidateCount"] = groups.Count(x => string.Equals((string)x["suggestedPolicy"], "invert_axis_sign_candidate", StringComparison.OrdinalIgnoreCase)),
                ["targetAxisRemapCandidateCount"] = groups.Count(x => string.Equals((string)x["suggestedPolicy"], "target_axis_remap_or_projection_candidate", StringComparison.OrdinalIgnoreCase)),
                ["topCandidates"] = new JArray(groups.Take(96)),
            };
        }

        private static float[] ProjectDeltaToAvatarAxisSpace(float[] delta, SingleMuscleDeltaCorrectionRow row, string mode)
        {
            var d = Normalize(delta);
            var pre = Normalize(row.TargetPreQ);
            var post = Normalize(row.TargetPostQ);
            var zero = Normalize(Multiply(pre, Inverse(post)));
            return mode switch
            {
                "inverse_pre_delta_post" => Normalize(Multiply(Multiply(Inverse(pre), d), post)),
                "inverse_pre_delta_pre" => Normalize(Multiply(Multiply(Inverse(pre), d), pre)),
                "post_delta_inverse_post" => Normalize(Multiply(Multiply(post, d), Inverse(post))),
                "inverse_zero_delta_zero" => Normalize(Multiply(Multiply(Inverse(zero), d), zero)),
                _ => d,
            };
        }

        private static string FoldSignedAxisLabelByValue(string label, float value)
        {
            if (value >= 0f)
            {
                return string.IsNullOrWhiteSpace(label) ? "+X" : label;
            }
            return FlipSignedAxisLabel(label);
        }

        private static string FlipSignedAxisLabel(string label) => label switch
        {
            "+X" => "-X",
            "-X" => "+X",
            "+Y" => "-Y",
            "-Y" => "+Y",
            "+Z" => "-Z",
            "-Z" => "+Z",
            _ => "+X",
        };

        private static string AxisIndexToPositiveLabel(int axis) => axis switch
        {
            0 => "+X",
            1 => "+Y",
            2 => "+Z",
            _ => "+X",
        };

        private static float[] AxisIndexToVector(int axis) => axis switch
        {
            0 => new[] { 1f, 0f, 0f },
            1 => new[] { 0f, 1f, 0f },
            2 => new[] { 0f, 0f, 1f },
            _ => new[] { 1f, 0f, 0f },
        };

        private static (string Label, float ErrorDegrees) FindNearestSignedAvatarAxis(float[] axis)
        {
            var normalized = NormalizeVector3(axis);
            var candidates = new[]
            {
                ("+X", new[] { 1f, 0f, 0f }),
                ("-X", new[] { -1f, 0f, 0f }),
                ("+Y", new[] { 0f, 1f, 0f }),
                ("-Y", new[] { 0f, -1f, 0f }),
                ("+Z", new[] { 0f, 0f, 1f }),
                ("-Z", new[] { 0f, 0f, -1f }),
            };
            var best = candidates
                .Select(x =>
                {
                    var dot = Math.Clamp(normalized[0] * x.Item2[0] + normalized[1] * x.Item2[1] + normalized[2] * x.Item2[2], -1f, 1f);
                    return new
                    {
                        x.Item1,
                        Error = MathF.Acos(dot) * 180f / MathF.PI,
                    };
                })
                .OrderBy(x => x.Error)
                .ThenBy(x => x.Item1, StringComparer.OrdinalIgnoreCase)
                .First();
            return (best.Item1, best.Error);
        }

        private static float[] AverageAxis(IEnumerable<float[]> axes)
        {
            var sum = new[] { 0f, 0f, 0f };
            var count = 0;
            foreach (var axis in axes ?? Enumerable.Empty<float[]>())
            {
                var normalized = NormalizeVector3(axis);
                sum[0] += normalized[0];
                sum[1] += normalized[1];
                sum[2] += normalized[2];
                count++;
            }
            return count == 0 ? new[] { 1f, 0f, 0f } : NormalizeVector3(sum);
        }

        private static float MaxVectorPairAngle(IEnumerable<float[]> axes)
        {
            var values = (axes ?? Enumerable.Empty<float[]>())
                .Select(NormalizeVector3)
                .ToArray();
            var max = 0f;
            for (var i = 0; i < values.Length; i++)
            {
                for (var j = i + 1; j < values.Length; j++)
                {
                    max = MathF.Max(max, AxisAngleDegrees(values[i], values[j]));
                }
            }
            return max;
        }

        private static JArray BuildSignedAxisCounts(IEnumerable<string> values)
        {
            return new JArray((values ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(group => new JObject
                {
                    ["axis"] = group.Key,
                    ["count"] = group.Count(),
                })
                .OrderByDescending(x => (int?)x["count"] ?? 0)
                .ThenBy(x => (string)x["axis"], StringComparer.OrdinalIgnoreCase));
        }

        private static JObject BuildSingleMuscleAxisBasisCandidateSummary(IEnumerable<SingleMuscleDeltaCorrectionRow> rows)
        {
            var items = rows?.ToArray() ?? Array.Empty<SingleMuscleDeltaCorrectionRow>();
            if (items.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["rowCount"] = 0,
                };
            }

            var candidateRows = new List<AxisBasisCandidateRow>();
            foreach (var item in items)
            {
                foreach (var candidate in BuildAxisBasisCandidates(item))
                {
                    var transformed = ApplyAxisBasisCandidate(item.PredictedDelta, candidate);
                    var rotationError = QuaternionAngleDegrees(transformed, item.UnityDelta);
                    var transformedAxis = ToAxisAngle(transformed);
                    var unityAxis = ToAxisAngle(item.UnityDelta);
                    var axisError = AxisAngleDegrees(transformedAxis.Axis, unityAxis.Axis);
                    candidateRows.Add(new AxisBasisCandidateRow(
                        candidate.Name,
                        item.HumanBone,
                        item.Attribute,
                        item.Value,
                        item.TargetPath,
                        rotationError,
                        axisError));
                }
            }

            var global = candidateRows
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var groupItems = group.ToArray();
                    return new JObject
                    {
                        ["candidate"] = group.Key,
                        ["rowCount"] = groupItems.Length,
                        ["maxRotationErrorDegrees"] = groupItems.Max(x => x.RotationErrorDegrees),
                        ["avgRotationErrorDegrees"] = groupItems.Average(x => x.RotationErrorDegrees),
                        ["maxAxisErrorDegrees"] = groupItems.Max(x => x.AxisErrorDegrees),
                        ["avgAxisErrorDegrees"] = groupItems.Average(x => x.AxisErrorDegrees),
                        ["worstHumanBone"] = groupItems
                            .OrderByDescending(x => x.RotationErrorDegrees)
                            .ThenBy(x => x.HumanBone, StringComparer.OrdinalIgnoreCase)
                            .First()
                            .HumanBone,
                    };
                })
                .OrderBy(x => (float?)x["avgRotationErrorDegrees"] ?? float.MaxValue)
                .ThenBy(x => (float?)x["maxRotationErrorDegrees"] ?? float.MaxValue)
                .ThenBy(x => (string)x["candidate"], StringComparer.OrdinalIgnoreCase);

            var byBodyGroup = candidateRows
                .GroupBy(x => new { BodyGroup = ClassifyBodyGroup(x.TargetPath), x.Name })
                .Select(group =>
                {
                    var groupItems = group.ToArray();
                    return new JObject
                    {
                        ["bodyGroup"] = group.Key.BodyGroup,
                        ["candidate"] = group.Key.Name,
                        ["rowCount"] = groupItems.Length,
                        ["maxRotationErrorDegrees"] = groupItems.Max(x => x.RotationErrorDegrees),
                        ["avgRotationErrorDegrees"] = groupItems.Average(x => x.RotationErrorDegrees),
                        ["maxAxisErrorDegrees"] = groupItems.Max(x => x.AxisErrorDegrees),
                        ["avgAxisErrorDegrees"] = groupItems.Average(x => x.AxisErrorDegrees),
                    };
                })
                .GroupBy(x => (string)x["bodyGroup"], StringComparer.OrdinalIgnoreCase)
                .Select(group => new JObject
                {
                    ["bodyGroup"] = group.Key,
                    ["topCandidates"] = new JArray(group
                        .OrderBy(x => (float?)x["avgRotationErrorDegrees"] ?? float.MaxValue)
                        .ThenBy(x => (float?)x["maxRotationErrorDegrees"] ?? float.MaxValue)
                        .ThenBy(x => (string)x["candidate"], StringComparer.OrdinalIgnoreCase)
                        .Take(8)),
                })
                .OrderBy(x => (string)x["bodyGroup"], StringComparer.OrdinalIgnoreCase);

            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: applies deterministic basis candidates to the current single-muscle delta before comparing to Unity. This tests whether the remaining axis error is explainable by preQ/postQ basis, zero rotation basis, or simple quaternion mirror.",
                ["rowCount"] = items.Length,
                ["candidateCount"] = candidateRows.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                ["topGlobalCandidates"] = new JArray(global.Take(16)),
                ["bestByBodyGroup"] = new JArray(byBodyGroup),
            };
        }

        private static IEnumerable<AxisBasisCandidate> BuildAxisBasisCandidates(SingleMuscleDeltaCorrectionRow row)
        {
            var pre = Normalize(row.TargetPreQ);
            var post = Normalize(row.TargetPostQ);
            var zero = Normalize(Multiply(pre, Inverse(post)));
            yield return new AxisBasisCandidate("identity", IdentityQuaternion, "sandwich", null);
            yield return new AxisBasisCandidate("preQ_sandwich", pre, "sandwich", null);
            yield return new AxisBasisCandidate("inverse_preQ_sandwich", Inverse(pre), "sandwich", null);
            yield return new AxisBasisCandidate("postQ_sandwich", post, "sandwich", null);
            yield return new AxisBasisCandidate("inverse_postQ_sandwich", Inverse(post), "sandwich", null);
            yield return new AxisBasisCandidate("zero_pre_inverse_post_sandwich", zero, "sandwich", null);
            yield return new AxisBasisCandidate("inverse_zero_sandwich", Inverse(zero), "sandwich", null);
            yield return new AxisBasisCandidate("preQ_left", pre, "left", null);
            yield return new AxisBasisCandidate("inverse_preQ_left", Inverse(pre), "left", null);
            yield return new AxisBasisCandidate("postQ_right", post, "right", null);
            yield return new AxisBasisCandidate("inverse_postQ_right", Inverse(post), "right", null);
            foreach (var mirror in new[] { "mirrorX", "mirrorY", "mirrorZ", "mirrorXY", "mirrorXZ", "mirrorYZ", "mirrorXYZ" })
            {
                yield return new AxisBasisCandidate(mirror, IdentityQuaternion, "mirror", mirror);
            }
        }

        private static float[] ApplyAxisBasisCandidate(float[] delta, AxisBasisCandidate candidate)
        {
            var q = Normalize(candidate.Basis);
            var d = Normalize(delta);
            return candidate.Mode switch
            {
                "left" => Normalize(Multiply(q, d)),
                "right" => Normalize(Multiply(d, q)),
                "mirror" => MirrorQuaternion(d, candidate.MirrorName),
                _ => Normalize(Multiply(Multiply(q, d), Inverse(q))),
            };
        }

        private static JObject BuildSingleMuscleDeltaCorrectionSummary(IReadOnlyCollection<SingleMuscleDeltaCorrectionRow> rows)
        {
            var items = rows?.ToArray() ?? Array.Empty<SingleMuscleDeltaCorrectionRow>();
            if (items.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["rowCount"] = 0,
                    ["rule"] = "Diagnostic only: learns the quaternion correction needed to turn the current single-muscle offline delta into Unity's real single-muscle local delta.",
                };
            }

            var byHumanBone = items
                .GroupBy(x => x.HumanBone, StringComparer.OrdinalIgnoreCase)
                .Select(BuildSingleMuscleCorrectionGroup)
                .OrderByDescending(x => (float?)x["maxCurrentErrorDegrees"] ?? 0f)
                .ThenBy(x => (string)x["humanBone"], StringComparer.OrdinalIgnoreCase);
            var byBodyGroup = items
                .GroupBy(x => ClassifyBodyGroup(x.TargetPath), StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var groupItems = group.ToArray();
                    return new JObject
                    {
                        ["bodyGroup"] = group.Key,
                        ["rowCount"] = groupItems.Length,
                        ["maxCurrentErrorDegrees"] = groupItems.Max(x => x.CurrentErrorDegrees),
                        ["avgCurrentErrorDegrees"] = groupItems.Average(x => x.CurrentErrorDegrees),
                        ["maxCorrectionAngleDegrees"] = groupItems.Max(x => x.BestCorrectionAngleDegrees),
                        ["avgCorrectionAngleDegrees"] = groupItems.Average(x => x.BestCorrectionAngleDegrees),
                        ["correctionConsistencyMaxDegrees"] = MaxQuaternionPairGap(groupItems.Select(x => x.BestCorrection)),
                        ["leftMultiplyCount"] = groupItems.Count(x => string.Equals(x.BestApplyMode, "leftMultiply", StringComparison.OrdinalIgnoreCase)),
                        ["rightMultiplyCount"] = groupItems.Count(x => string.Equals(x.BestApplyMode, "rightMultiply", StringComparison.OrdinalIgnoreCase)),
                    };
                })
                .OrderByDescending(x => (float?)x["maxCurrentErrorDegrees"] ?? 0f)
                .ThenBy(x => (string)x["bodyGroup"], StringComparer.OrdinalIgnoreCase);

            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: for each Unity single-muscle probe, computes the correction quaternion that would map the current offline delta to Unity's real local delta. Low correctionConsistencyMaxDegrees inside a bone/group means a fixed missing space transform is plausible; high consistency error means the current axis/muscle formula itself is wrong.",
                ["rowCount"] = items.Length,
                ["maxCurrentErrorDegrees"] = items.Max(x => x.CurrentErrorDegrees),
                ["avgCurrentErrorDegrees"] = items.Average(x => x.CurrentErrorDegrees),
                ["maxCorrectionConsistencyByHumanBoneDegrees"] = items
                    .GroupBy(x => x.HumanBone, StringComparer.OrdinalIgnoreCase)
                    .Select(x => MaxQuaternionPairGap(x.Select(row => row.BestCorrection)))
                    .DefaultIfEmpty(0f)
                    .Max(),
                ["byBodyGroup"] = new JArray(byBodyGroup),
                ["byHumanBone"] = new JArray(byHumanBone),
                ["topRows"] = ToJsonRows(items
                    .OrderByDescending(x => x.CurrentErrorDegrees)
                    .ThenBy(x => x.HumanBone, StringComparer.OrdinalIgnoreCase)
                    .Take(96)),
            };
        }

        private static JObject BuildSingleMuscleCorrectionGroup(IGrouping<string, SingleMuscleDeltaCorrectionRow> group)
        {
            var items = group.ToArray();
            return new JObject
            {
                ["humanBone"] = group.Key,
                ["bodyGroup"] = ClassifyBodyGroup(items[0].TargetPath),
                ["rowCount"] = items.Length,
                ["maxCurrentErrorDegrees"] = items.Max(x => x.CurrentErrorDegrees),
                ["avgCurrentErrorDegrees"] = items.Average(x => x.CurrentErrorDegrees),
                ["maxCorrectionAngleDegrees"] = items.Max(x => x.BestCorrectionAngleDegrees),
                ["avgCorrectionAngleDegrees"] = items.Average(x => x.BestCorrectionAngleDegrees),
                ["correctionConsistencyMaxDegrees"] = MaxQuaternionPairGap(items.Select(x => x.BestCorrection)),
                ["leftMultiplyCount"] = items.Count(x => string.Equals(x.BestApplyMode, "leftMultiply", StringComparison.OrdinalIgnoreCase)),
                ["rightMultiplyCount"] = items.Count(x => string.Equals(x.BestApplyMode, "rightMultiply", StringComparison.OrdinalIgnoreCase)),
                ["worstAttribute"] = items
                    .OrderByDescending(x => x.CurrentErrorDegrees)
                    .ThenBy(x => x.Attribute, StringComparer.OrdinalIgnoreCase)
                    .First()
                    .Attribute,
            };
        }

        private static float MaxQuaternionPairGap(IEnumerable<float[]> quaternions)
        {
            var values = (quaternions ?? Enumerable.Empty<float[]>())
                .Select(Normalize)
                .Where(x => x.Length == 4)
                .ToArray();
            var max = 0f;
            for (var i = 0; i < values.Length; i++)
            {
                for (var j = i + 1; j < values.Length; j++)
                {
                    max = MathF.Max(max, QuaternionAngleDegrees(values[i], values[j]));
                }
            }
            return max;
        }

        private static (float[] Axis, float AngleDegrees) ToAxisAngle(float[] quaternion)
        {
            var q = Normalize(quaternion);
            if (q.Length != 4)
            {
                return (new[] { 1f, 0f, 0f }, 0f);
            }

            if (q[3] < 0f)
            {
                q = new[] { -q[0], -q[1], -q[2], -q[3] };
            }

            var w = Math.Clamp(q[3], -1f, 1f);
            var angle = 2f * MathF.Acos(w);
            var sin = MathF.Sqrt(MathF.Max(0f, 1f - w * w));
            if (sin <= 0.00001f || angle <= 0.00001f)
            {
                return (new[] { 1f, 0f, 0f }, 0f);
            }

            return (new[] { q[0] / sin, q[1] / sin, q[2] / sin }, angle * 180f / MathF.PI);
        }

        private static float AxisAngleDegrees(float[] a, float[] b)
        {
            var na = NormalizeVector3(a);
            var nb = NormalizeVector3(b);
            var dot = Math.Clamp(Math.Abs(na[0] * nb[0] + na[1] * nb[1] + na[2] * nb[2]), 0f, 1f);
            return MathF.Acos(dot) * 180f / MathF.PI;
        }

        private static float SignedAxisAngleDegrees(float[] a, float[] b)
        {
            var na = NormalizeVector3(a);
            var nb = NormalizeVector3(b);
            var dot = Math.Clamp(na[0] * nb[0] + na[1] * nb[1] + na[2] * nb[2], -1f, 1f);
            return MathF.Acos(dot) * 180f / MathF.PI;
        }

        private static float[] NormalizeVector3(float[] value)
        {
            if (value == null || value.Length < 3)
            {
                return new[] { 1f, 0f, 0f };
            }

            var length = MathF.Sqrt(value[0] * value[0] + value[1] * value[1] + value[2] * value[2]);
            if (length <= 0.000001f)
            {
                return new[] { 1f, 0f, 0f };
            }

            return new[] { value[0] / length, value[1] / length, value[2] / length };
        }

        private static JObject BuildTransformVsInternalAvatarPoseProbeSummary(JObject[] transformProbes, JObject[] internalAvatarPoseProbes)
        {
            if (transformProbes == null || transformProbes.Length == 0 || internalAvatarPoseProbes == null || internalAvatarPoseProbes.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["transformProbeCount"] = transformProbes?.Length ?? 0,
                    ["internalAvatarPoseProbeCount"] = internalAvatarPoseProbes?.Length ?? 0,
                    ["rule"] = "Diagnostic only: compares Unity Transform.localRotation single-muscle probes with Unity HumanPoseHandler.GetInternalAvatarPose single-muscle probes. Low error means both Unity-exposed spaces agree and the offline solver should target the same local TRS delta.",
                };
            }

            var internalByKey = internalAvatarPoseProbes
                .Where(x => !string.IsNullOrWhiteSpace((string)x["muscleName"]))
                .GroupBy(x => $"{(string)x["muscleName"]}|{((float?)x["value"] ?? 0f).ToString("R", CultureInfo.InvariantCulture)}", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            var rows = new List<TrackCompareRow>();
            var missingInternalProbe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var missingInternalTrack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var transformProbe in transformProbes)
            {
                var muscleName = (string)transformProbe["muscleName"];
                if (string.IsNullOrWhiteSpace(muscleName))
                {
                    continue;
                }

                var value = (float?)transformProbe["value"] ?? 0f;
                var key = $"{muscleName}|{value.ToString("R", CultureInfo.InvariantCulture)}";
                if (!internalByKey.TryGetValue(key, out var internalProbe))
                {
                    missingInternalProbe.Add(key);
                    continue;
                }

                foreach (var item in transformProbe["rotations"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    var path = (string)item["path"];
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    if (!TryReadProbeRotation(transformProbe, path, out _, out var transformBase, out var transformRotation) ||
                        !TryReadProbeRotation(internalProbe, path, out var internalPath, out var internalBase, out var internalRotation))
                    {
                        missingInternalTrack.Add($"{key}|{path}");
                        continue;
                    }

                    var transformDelta = Normalize(Multiply(transformRotation, Inverse(transformBase)));
                    var internalDelta = Normalize(Multiply(internalRotation, Inverse(internalBase)));
                    var error = QuaternionAngleDegrees(transformDelta, internalDelta);
                    rows.Add(new TrackCompareRow(
                        key,
                        string.IsNullOrWhiteSpace(internalPath) ? path : internalPath,
                        -1,
                        1,
                        error,
                        error,
                        IsBodyBone(path)));
                }
            }

            var ordered = rows
                .OrderByDescending(x => x.MaxRotationErrorDegrees)
                .ThenBy(x => x.UnityPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new JObject
            {
                ["status"] = ordered.Length == 0 ? "not_available" : missingInternalProbe.Count == 0 ? "ok" : "warning",
                ["rule"] = "Diagnostic only: compares Unity Transform.localRotation single-muscle probes with Unity HumanPoseHandler.GetInternalAvatarPose single-muscle probes. Low error means both Unity-exposed spaces agree and the offline solver should target the same local TRS delta.",
                ["transformProbeCount"] = transformProbes.Length,
                ["internalAvatarPoseProbeCount"] = internalAvatarPoseProbes.Length,
                ["matchedTrackCount"] = ordered.Length,
                ["missingInternalProbeCount"] = missingInternalProbe.Count,
                ["missingInternalTrackCount"] = missingInternalTrack.Count,
                ["missingInternalProbes"] = new JArray(missingInternalProbe.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(64)),
                ["missingInternalTracks"] = new JArray(missingInternalTrack.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(64)),
                ["maxDegrees"] = ordered.Length == 0 ? 0 : ordered[0].MaxRotationErrorDegrees,
                ["avgTrackMaxDegrees"] = ordered.Length == 0 ? 0 : ordered.Average(x => x.MaxRotationErrorDegrees),
                ["bodyGroupError"] = BuildBodyGroupErrorSummary(ordered),
                ["topErrors"] = ToJsonRows(ordered.Take(96)),
            };
        }

        private static JObject BuildSingleMuscleFormulaHintSummary(JObject comparison)
        {
            if (!IsStatusAvailable(comparison))
            {
                return new JObject
                {
                    ["status"] = "not_available",
                };
            }

            var currentErrorByKey = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in comparison["topErrors"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var key = (string)row["unityPath"];
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }
                currentErrorByKey[key] = row;
            }

            var sourceTargetRows = comparison["singleMuscleSourceTargetAxisFitSummary"]?["rows"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>();
            var linearityByKey = BuildLinearityRowsByMuscleTarget(comparison["singleMuscleMultiValueLinearitySummary"] as JObject);
            var hintRows = new List<JObject>();
            foreach (var row in sourceTargetRows)
            {
                var muscleName = (string)row["muscleName"];
                var targetHumanBone = (string)row["targetHumanBone"];
                var value = (float?)row["value"] ?? 0f;
                if (string.IsNullOrWhiteSpace(muscleName) || string.IsNullOrWhiteSpace(targetHumanBone))
                {
                    continue;
                }

                var key = $"{targetHumanBone}:{muscleName}:{value.ToString("R", CultureInfo.InvariantCulture)}";
                if (!currentErrorByKey.TryGetValue(key, out var currentError))
                {
                    continue;
                }

                var currentMax = JsonFloat(currentError, "maxRotationErrorDegrees");
                var bestError = JsonFloat(row, "bestErrorDegrees");
                var bodyGroup = ClassifyBodyGroup(targetHumanBone);
                var currentSource = (string)row["currentSourceHumanBone"];
                var bestSource = (string)row["bestSourceHumanBone"];
                var currentTargetAxis = (int?)row["currentTargetAxis"] ?? -1;
                var bestTargetAxis = (int?)row["bestTargetAxis"] ?? -1;
                linearityByKey.TryGetValue($"{muscleName}|{targetHumanBone}", out var linearity);
                var limitExplained = (bool?)linearity?["limitExplained"] == true;
                var axisStable = (bool?)linearity?["axisStable"] == true;
                hintRows.Add(new JObject
                {
                    ["muscleName"] = muscleName,
                    ["value"] = value,
                    ["targetHumanBone"] = targetHumanBone,
                    ["bodyGroup"] = bodyGroup,
                    ["currentErrorDegrees"] = currentMax,
                    ["bestEnumeratedErrorDegrees"] = bestError,
                    ["improvementDegrees"] = Math.Max(0f, currentMax - bestError),
                    ["currentSourceHumanBone"] = currentSource,
                    ["bestSourceHumanBone"] = bestSource,
                    ["sourceChanged"] = !string.Equals(currentSource, bestSource, StringComparison.OrdinalIgnoreCase),
                    ["currentTargetAxis"] = currentTargetAxis,
                    ["currentTargetAxisName"] = AvatarAxisName(currentTargetAxis),
                    ["currentSourceAxis"] = CurrentExpectedAvatarAxisForMuscle(currentSource, muscleName),
                    ["currentSourceAxisName"] = AvatarAxisName(CurrentExpectedAvatarAxisForMuscle(currentSource, muscleName)),
                    ["bestTargetAxis"] = bestTargetAxis,
                    ["bestTargetAxisName"] = AvatarAxisName(bestTargetAxis),
                    ["targetAxisChanged"] = currentTargetAxis != bestTargetAxis,
                    ["bestSourceAxis"] = (int?)row["bestSourceAxis"] ?? -1,
                    ["bestSourceAxisName"] = (string)row["bestSourceAxisName"],
                    ["sourceAxisChanged"] = CurrentExpectedAvatarAxisForMuscle(currentSource, muscleName) != ((int?)row["bestSourceAxis"] ?? -1),
                    ["bestPredictedDeltaDegrees"] = JsonFloat(row, "bestPredictedDeltaDegrees"),
                    ["linearityLimitExplained"] = limitExplained,
                    ["linearityAxisStable"] = axisStable,
                    ["linearityLimitHumanBone"] = (string)linearity?["limitHumanBone"],
                    ["linearityLimitAvatarAxisName"] = (string)linearity?["limitAvatarAxisName"],
                    ["likelyBlocker"] = limitExplained && axisStable && currentMax > 15f
                        ? "axis_space"
                        : !limitExplained && currentMax > 15f
                            ? "limit_or_source_axis"
                            : "none",
                });
            }

            var useful = hintRows
                .Where(x => JsonFloat(x, "currentErrorDegrees") > 15f && JsonFloat(x, "bestEnumeratedErrorDegrees") <= 5f)
                .OrderByDescending(x => JsonFloat(x, "improvementDegrees"))
                .ThenBy(x => (string)x["targetHumanBone"], StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => (string)x["muscleName"], StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var review = hintRows
                .Where(x => JsonFloat(x, "currentErrorDegrees") > 15f && JsonFloat(x, "bestEnumeratedErrorDegrees") > 5f && JsonFloat(x, "bestEnumeratedErrorDegrees") <= 20f)
                .OrderByDescending(x => JsonFloat(x, "improvementDegrees"))
                .ThenBy(x => JsonFloat(x, "bestEnumeratedErrorDegrees"))
                .ThenBy(x => (string)x["targetHumanBone"], StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => (string)x["muscleName"], StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var allSorted = hintRows
                .OrderByDescending(x => JsonFloat(x, "currentErrorDegrees"))
                .ThenBy(x => JsonFloat(x, "bestEnumeratedErrorDegrees"))
                .Take(24)
                .ToArray();
            var axisSpaceBlockers = hintRows
                .Where(x => string.Equals((string)x["likelyBlocker"], "axis_space", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => JsonFloat(x, "currentErrorDegrees"))
                .ThenBy(x => JsonFloat(x, "bestEnumeratedErrorDegrees"))
                .ThenBy(x => (string)x["targetHumanBone"], StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => (string)x["muscleName"], StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new JObject
            {
                ["status"] = hintRows.Count == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: summarizes single-muscle rows where the current formula is wrong but source/target axis enumeration can already explain Unity's SetHumanPose delta. These hints are evidence for the next solver formula, not production binding rules.",
                ["currentErrorThresholdDegrees"] = 15f,
                ["bestCandidateThresholdDegrees"] = 5f,
                ["reviewCandidateThresholdDegrees"] = 20f,
                ["usefulHintCount"] = useful.Length,
                ["reviewHintCount"] = review.Length,
                ["armUsefulHintCount"] = useful.Count(x => string.Equals((string)x["bodyGroup"], "Arm", StringComparison.OrdinalIgnoreCase)),
                ["legUsefulHintCount"] = useful.Count(x => string.Equals((string)x["bodyGroup"], "Leg", StringComparison.OrdinalIgnoreCase)),
                ["armReviewHintCount"] = review.Count(x => string.Equals((string)x["bodyGroup"], "Arm", StringComparison.OrdinalIgnoreCase)),
                ["legReviewHintCount"] = review.Count(x => string.Equals((string)x["bodyGroup"], "Leg", StringComparison.OrdinalIgnoreCase)),
                ["sourceChangedCount"] = useful.Count(x => (bool?)x["sourceChanged"] == true),
                ["sourceAxisChangedCount"] = useful.Count(x => (bool?)x["sourceAxisChanged"] == true),
                ["targetAxisChangedCount"] = useful.Count(x => (bool?)x["targetAxisChanged"] == true),
                ["axisSpaceBlockerCount"] = axisSpaceBlockers.Length,
                ["topUsefulHints"] = new JArray(useful.Take(24)),
                ["topReviewHints"] = new JArray(review.Take(24)),
                ["topAxisSpaceBlockers"] = new JArray(axisSpaceBlockers.Take(32)),
                ["topCurrentErrors"] = new JArray(allSorted),
                ["rows"] = new JArray(hintRows
                    .OrderByDescending(x => JsonFloat(x, "currentErrorDegrees"))
                    .ThenBy(x => JsonFloat(x, "bestEnumeratedErrorDegrees"))
                    .ThenBy(x => (string)x["targetHumanBone"], StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => (string)x["muscleName"], StringComparer.OrdinalIgnoreCase)),
            };
        }

        private static JObject BuildSingleMuscleFormulaReadinessSummary(JObject comparison)
        {
            var hintRows = comparison?["formulaHintSummary"]?["rows"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            if (hintRows.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["reason"] = "formula_hint_rows_missing",
                    ["rule"] = "Diagnostic only: ranks single-muscle rows that are ready for the next deterministic formula probe. It does not change the production Humanoid solver.",
                };
            }

            var signedAxisByKey = (comparison?["singleMuscleDeltaAxisSummary"]?["signedAxisTiltSummary"]?["topAxisTilts"] as JArray)?
                .OfType<JObject>()
                .GroupBy(x => $"{(string)x["humanBone"]}|{(string)x["attribute"]}", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);

            var focusRows = hintRows
                .Where(x => IsFocusBodyGroup((string)x["bodyGroup"]))
                .ToArray();
            var groups = focusRows
                .GroupBy(x => $"{(string)x["targetHumanBone"]}|{(string)x["muscleName"]}", StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var items = group.ToArray();
                    var first = items[0];
                    var targetHumanBone = (string)first["targetHumanBone"];
                    var muscleName = (string)first["muscleName"];
                    signedAxisByKey.TryGetValue($"{targetHumanBone}|{muscleName}", out var axisRow);
                    var sampleCount = items.Length;
                    var limitExplainedRate = sampleCount == 0 ? 0f : (float)items.Count(x => (bool?)x["linearityLimitExplained"] == true) / sampleCount;
                    var axisStableRate = sampleCount == 0 ? 0f : (float)items.Count(x => (bool?)x["linearityAxisStable"] == true) / sampleCount;
                    var minBest = items.Min(x => JsonFloat(x, "bestEnumeratedErrorDegrees"));
                    var maxCurrent = items.Max(x => JsonFloat(x, "currentErrorDegrees"));
                    var avgImprovement = items.Average(x => JsonFloat(x, "improvementDegrees"));
                    var sourceChangedRate = sampleCount == 0 ? 0f : (float)items.Count(x => (bool?)x["sourceChanged"] == true) / sampleCount;
                    var sourceAxisChangedRate = sampleCount == 0 ? 0f : (float)items.Count(x => (bool?)x["sourceAxisChanged"] == true) / sampleCount;
                    var targetAxisChangedRate = sampleCount == 0 ? 0f : (float)items.Count(x => (bool?)x["targetAxisChanged"] == true) / sampleCount;
                    var axisTilt = axisRow == null ? 0f : JsonFloat(axisRow, "axisTiltDegrees");
                    var unityAxisSpread = axisRow == null ? 0f : JsonFloat(axisRow, "unityAxisSpreadDegrees");
                    var nearestAxisSourceError = axisRow == null ? 0f : JsonFloat(axisRow, "nearestAxisSourceErrorDegrees");
                    var readiness = ClassifyFormulaReadiness(
                        minBest,
                        maxCurrent,
                        limitExplainedRate,
                        axisStableRate,
                        unityAxisSpread,
                        nearestAxisSourceError);

                    return new JObject
                    {
                        ["targetHumanBone"] = targetHumanBone,
                        ["muscleName"] = muscleName,
                        ["bodyGroup"] = (string)first["bodyGroup"],
                        ["muscleFamily"] = ClassifyMuscleFamily(muscleName),
                        ["sampleCount"] = sampleCount,
                        ["readiness"] = readiness,
                        ["maxCurrentErrorDegrees"] = maxCurrent,
                        ["minBestEnumeratedErrorDegrees"] = minBest,
                        ["avgImprovementDegrees"] = avgImprovement,
                        ["limitExplainedRate"] = limitExplainedRate,
                        ["axisStableRate"] = axisStableRate,
                        ["sourceChangedRate"] = sourceChangedRate,
                        ["sourceAxisChangedRate"] = sourceAxisChangedRate,
                        ["targetAxisChangedRate"] = targetAxisChangedRate,
                        ["bestSourceHumanBoneVotes"] = BuildPropertyVotes(items, "bestSourceHumanBone"),
                        ["bestSourceAxisVotes"] = BuildPropertyVotes(items, "bestSourceAxisName"),
                        ["bestTargetAxisVotes"] = BuildPropertyVotes(items, "bestTargetAxisName"),
                        ["linearityLimitHumanBoneVotes"] = BuildPropertyVotes(items, "linearityLimitHumanBone"),
                        ["linearityLimitAvatarAxisVotes"] = BuildPropertyVotes(items, "linearityLimitAvatarAxisName"),
                        ["unityNearestAxis"] = (string)axisRow?["unityNearestSignedAxis"],
                        ["predictedNearestAxis"] = (string)axisRow?["predictedNearestSignedAxis"],
                        ["nearestAxisSourceCandidate"] = (string)axisRow?["nearestAxisSourceCandidate"],
                        ["nearestAxisSourceErrorDegrees"] = nearestAxisSourceError,
                        ["axisTiltDegrees"] = axisTilt,
                        ["unityAxisSpreadDegrees"] = unityAxisSpread,
                    };
                })
                .OrderBy(x => ReadinessSortRank((string)x["readiness"]))
                .ThenBy(x => JsonFloat(x, "minBestEnumeratedErrorDegrees"))
                .ThenByDescending(x => JsonFloat(x, "avgImprovementDegrees"))
                .ThenBy(x => (string)x["targetHumanBone"], StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => (string)x["muscleName"], StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new JObject
            {
                ["status"] = groups.Length == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: combines source/target axis enumeration, multi-value limit checks, and signed Unity axis stability. Rows marked ready_for_formula_probe are candidates for the next explicit timeline formula probe, not production solver rules.",
                ["rowCount"] = groups.Length,
                ["readyForFormulaProbeCount"] = groups.Count(x => string.Equals((string)x["readiness"], "ready_for_formula_probe", StringComparison.OrdinalIgnoreCase)),
                ["needsAxisFormulaCount"] = groups.Count(x => string.Equals((string)x["readiness"], "needs_axis_formula_probe", StringComparison.OrdinalIgnoreCase)),
                ["needsMoreProbeCount"] = groups.Count(x => string.Equals((string)x["readiness"], "needs_more_probe_or_metadata", StringComparison.OrdinalIgnoreCase)),
                ["topReadyForFormulaProbe"] = new JArray(groups.Where(x => string.Equals((string)x["readiness"], "ready_for_formula_probe", StringComparison.OrdinalIgnoreCase)).Take(24)),
                ["topNeedsAxisFormulaProbe"] = new JArray(groups.Where(x => string.Equals((string)x["readiness"], "needs_axis_formula_probe", StringComparison.OrdinalIgnoreCase)).Take(24)),
                ["topNeedsMoreProbeOrMetadata"] = new JArray(groups.Where(x => string.Equals((string)x["readiness"], "needs_more_probe_or_metadata", StringComparison.OrdinalIgnoreCase)).Take(24)),
                ["rows"] = new JArray(groups),
            };
        }

        private static string ClassifyFormulaReadiness(
            float minBestEnumeratedErrorDegrees,
            float maxCurrentErrorDegrees,
            float limitExplainedRate,
            float axisStableRate,
            float unityAxisSpreadDegrees,
            float nearestAxisSourceErrorDegrees)
        {
            if (maxCurrentErrorDegrees <= 15f)
            {
                return "current_not_main_blocker";
            }
            if (limitExplainedRate >= 0.99f &&
                axisStableRate >= 0.99f &&
                minBestEnumeratedErrorDegrees <= 5f &&
                unityAxisSpreadDegrees <= 1f)
            {
                return "ready_for_formula_probe";
            }
            if (limitExplainedRate >= 0.75f &&
                axisStableRate >= 0.75f &&
                minBestEnumeratedErrorDegrees <= 20f &&
                nearestAxisSourceErrorDegrees <= 30f)
            {
                return "needs_axis_formula_probe";
            }
            return "needs_more_probe_or_metadata";
        }

        private static int ReadinessSortRank(string readiness)
        {
            return readiness switch
            {
                "ready_for_formula_probe" => 0,
                "needs_axis_formula_probe" => 1,
                "needs_more_probe_or_metadata" => 2,
                "current_not_main_blocker" => 3,
                _ => 4,
            };
        }

        private static JObject BuildArmFormulaWorkQueueSummary(JObject comparison)
        {
            var hintSummary = comparison?["formulaHintSummary"] as JObject;
            if (!IsStatusAvailable(hintSummary))
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["reason"] = "formula_hint_summary_missing",
                };
            }

            var rows = new[]
                {
                    hintSummary["topAxisSpaceBlockers"],
                    hintSummary["topReviewHints"],
                    hintSummary["topUsefulHints"],
                }
                .OfType<JArray>()
                .SelectMany(x => x.OfType<JObject>())
                .Where(x => string.Equals((string)x["bodyGroup"], "Arm", StringComparison.OrdinalIgnoreCase))
                .GroupBy(x =>
                    $"{(string)x["targetHumanBone"]}|{(string)x["muscleName"]}|{((float?)x["value"] ?? 0f).ToString("R", CultureInfo.InvariantCulture)}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToArray();
            if (rows.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["reason"] = "arm_hint_rows_missing",
                };
            }

            var groups = rows
                .GroupBy(x => $"{(string)x["targetHumanBone"]}|{(string)x["muscleName"]}", StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var items = group.ToArray();
                    var targetHumanBone = (string)items[0]["targetHumanBone"];
                    var muscleName = (string)items[0]["muscleName"];
                    var limitExplainedCount = items.Count(x => (bool?)x["linearityLimitExplained"] == true);
                    var axisStableCount = items.Count(x => (bool?)x["linearityAxisStable"] == true);
                    var sourceChangedCount = items.Count(x => (bool?)x["sourceChanged"] == true);
                    var sourceAxisChangedCount = items.Count(x => (bool?)x["sourceAxisChanged"] == true);
                    var targetAxisChangedCount = items.Count(x => (bool?)x["targetAxisChanged"] == true);
                    return new JObject
                    {
                        ["targetHumanBone"] = targetHumanBone,
                        ["muscleName"] = muscleName,
                        ["sampleCount"] = items.Length,
                        ["maxCurrentErrorDegrees"] = items.Max(x => JsonFloat(x, "currentErrorDegrees")),
                        ["minBestEnumeratedErrorDegrees"] = items.Min(x => JsonFloat(x, "bestEnumeratedErrorDegrees")),
                        ["avgImprovementDegrees"] = items.Average(x => JsonFloat(x, "improvementDegrees")),
                        ["limitExplainedRate"] = (float)limitExplainedCount / items.Length,
                        ["axisStableRate"] = (float)axisStableCount / items.Length,
                        ["sourceChangedRate"] = (float)sourceChangedCount / items.Length,
                        ["sourceAxisChangedRate"] = (float)sourceAxisChangedCount / items.Length,
                        ["targetAxisChangedRate"] = (float)targetAxisChangedCount / items.Length,
                        ["bestSourceHumanBoneVotes"] = BuildPropertyVotes(items, "bestSourceHumanBone"),
                        ["bestSourceAxisVotes"] = BuildPropertyVotes(items, "bestSourceAxisName"),
                        ["bestTargetAxisVotes"] = BuildPropertyVotes(items, "bestTargetAxisName"),
                        ["linearityLimitHumanBoneVotes"] = BuildPropertyVotes(items, "linearityLimitHumanBone"),
                        ["linearityLimitAxisVotes"] = BuildPropertyVotes(items, "linearityLimitAvatarAxisName"),
                        ["nextFormulaWork"] = ResolveArmFormulaWork(targetHumanBone, muscleName, limitExplainedCount, axisStableCount, sourceAxisChangedCount, targetAxisChangedCount, items.Length),
                        ["rows"] = new JArray(items
                            .OrderByDescending(x => JsonFloat(x, "currentErrorDegrees"))
                            .ThenBy(x => JsonFloat(x, "bestEnumeratedErrorDegrees"))),
                    };
                })
                .OrderByDescending(x => JsonFloat(x, "maxCurrentErrorDegrees"))
                .ThenBy(x => JsonFloat(x, "minBestEnumeratedErrorDegrees"))
                .ThenBy(x => (string)x["targetHumanBone"], StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => (string)x["muscleName"], StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var armTwistGroups = groups
                .Where(x => ((string)x["muscleName"])?.IndexOf("Arm Twist", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToArray();
            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: reduces single-muscle arm hints to a formula work queue. A group is migration-ready only after the same rule beats current on full timelines across models; this summary does not change production solver math.",
                ["armHintRowCount"] = rows.Length,
                ["groupCount"] = groups.Length,
                ["armTwistGroupCount"] = armTwistGroups.Length,
                ["diagnosticStatus"] = armTwistGroups.Length > 0
                    ? "arm_twist_axis_space_unresolved"
                    : "arm_axis_space_unresolved",
                ["topGroups"] = new JArray(groups.Take(12)),
            };
        }

        private static JArray BuildPropertyVotes(IEnumerable<JObject> rows, string propertyName)
        {
            return new JArray((rows ?? Enumerable.Empty<JObject>())
                .Select(x => (string)x[propertyName])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(x => new JObject
                {
                    ["value"] = x.Key,
                    ["count"] = x.Count(),
                })
                .OrderByDescending(x => (int)x["count"])
                .ThenBy(x => (string)x["value"], StringComparer.OrdinalIgnoreCase));
        }

        private static string ResolveArmFormulaWork(
            string targetHumanBone,
            string muscleName,
            int limitExplainedCount,
            int axisStableCount,
            int sourceAxisChangedCount,
            int targetAxisChangedCount,
            int sampleCount)
        {
            var limitExplained = sampleCount > 0 && limitExplainedCount == sampleCount;
            var axisStable = sampleCount > 0 && axisStableCount == sampleCount;
            var isArmTwist = muscleName?.IndexOf("Arm Twist", StringComparison.OrdinalIgnoreCase) >= 0;
            if (limitExplained && axisStable && isArmTwist)
            {
                return "derive_arm_twist_axis_space_from_avatar_pose_or_limit_axis";
            }
            if (limitExplained && axisStable && sourceAxisChangedCount > 0 &&
                targetHumanBone?.IndexOf("UpperArm", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "derive_upper_arm_swing_axis_tilt_from_avatar_pose";
            }
            if (limitExplained && axisStable && targetAxisChangedCount > 0)
            {
                return "derive_target_axis_tilt_then_full_timeline_gate";
            }
            if (!limitExplained)
            {
                return "recheck_limit_source_or_human_limit_metadata";
            }
            if (!axisStable)
            {
                return "collect_more_single_muscle_probe_values";
            }
            return "full_timeline_gate_before_migration";
        }

        private static Dictionary<string, JObject> BuildLinearityRowsByMuscleTarget(JObject linearitySummary)
        {
            var result = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            if (!string.Equals((string)linearitySummary?["status"], "ok", StringComparison.OrdinalIgnoreCase))
            {
                return result;
            }

            foreach (var row in (linearitySummary["topArmLegRows"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var muscleName = (string)row["muscleName"];
                var humanBone = (string)row["humanBone"];
                if (string.IsNullOrWhiteSpace(muscleName) || string.IsNullOrWhiteSpace(humanBone))
                {
                    continue;
                }

                var key = $"{muscleName}|{humanBone}";
                if (!result.TryGetValue(key, out var existing) ||
                    JsonFloat(row, "maxAngleDegrees") > JsonFloat(existing, "maxAngleDegrees"))
                {
                    result[key] = row;
                }
            }
            return result;
        }

        private static JObject BuildSingleMuscleInfluenceSummary(
            JObject[] probes,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex)
        {
            var expectedByMuscle = BuildExpectedInfluenceByMuscle(solverNodes, solverAxes, humanBoneIndex);
            var rows = new JArray();
            var missingExpectedCount = 0;
            foreach (var probe in probes
                .Where(x => !string.IsNullOrWhiteSpace((string)x["muscleName"]))
                .OrderBy(x => (string)x["muscleName"], StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => (float?)x["value"] ?? 0f))
            {
                var muscleName = (string)probe["muscleName"];
                var value = (float?)probe["value"] ?? 0f;
                var actual = BuildProbeInfluenceRows(probe).ToArray();
                expectedByMuscle.TryGetValue(muscleName, out var expected);
                expected ??= Array.Empty<ExpectedInfluenceTarget>();
                if (expected.Length == 0)
                {
                    missingExpectedCount++;
                }

                var topActual = actual.Take(8).ToArray();
                var matchedExpected = expected
                    .Where(target => topActual.Any(item => PathsReferToSameBone(item.Path, target.Path)))
                    .ToArray();
                var strongestExpected = expected
                    .Select(target => new
                    {
                        Target = target,
                        Actual = actual.FirstOrDefault(item => PathsReferToSameBone(item.Path, target.Path)),
                    })
                    .Where(x => x.Actual != null)
                    .OrderByDescending(x => x.Actual.AngleDegrees)
                    .FirstOrDefault();

                rows.Add(new JObject
                {
                    ["muscleName"] = muscleName,
                    ["value"] = value,
                    ["changedTrackCount"] = actual.Length,
                    ["maxDeltaDegrees"] = actual.Length == 0 ? 0 : actual[0].AngleDegrees,
                    ["bodyGroup"] = actual.Length == 0 ? "Other" : ClassifyBodyGroup(actual[0].Path),
                    ["expectedTargetCount"] = expected.Length,
                    ["expectedTargets"] = ToExpectedInfluenceJson(expected),
                    ["expectedFoundInTop8"] = matchedExpected.Length > 0,
                    ["matchedExpectedTargets"] = ToExpectedInfluenceJson(matchedExpected),
                    ["strongestExpectedDeltaDegrees"] = strongestExpected?.Actual?.AngleDegrees ?? 0f,
                    ["topActualTargets"] = ToProbeInfluenceJson(topActual),
                });
            }

            return new JObject
            {
                ["status"] = probes.Length == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: reports which Transform rotations Unity actually changes when one HumanPose muscle is set to -1 or +1. This is used to find limb/twist target mapping mistakes before changing the production Humanoid solver.",
                ["probeCount"] = probes.Length,
                ["muscleWithNoCurrentExpectedTargetCount"] = missingExpectedCount,
                ["rows"] = rows,
            };
        }

        private static JObject BuildSingleMuscleAxisFitSummary(
            JObject[] probes,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex)
        {
            var expectedByMuscle = BuildExpectedInfluenceByMuscle(solverNodes, solverAxes, humanBoneIndex);
            var rows = new JArray();
            foreach (var probe in probes
                .Where(x => !string.IsNullOrWhiteSpace((string)x["muscleName"]))
                .OrderBy(x => (string)x["muscleName"], StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => (float?)x["value"] ?? 0f))
            {
                var muscleName = (string)probe["muscleName"];
                if (!expectedByMuscle.TryGetValue(muscleName, out var expected) || expected.Length == 0)
                {
                    continue;
                }

                var value = (float?)probe["value"] ?? 0f;
                var baseValue = (float?)probe["baseValue"] ?? 0f;
                foreach (var target in expected)
                {
                    if (!TryGetAxisByPath(solverNodes, solverAxes, target.Path, out var axis) ||
                        !TryReadProbeRotation(probe, target.Path, out var probePath, out var unityBaseRotation, out var unityRotation))
                    {
                        continue;
                    }

                    var candidates = new JArray();
                    var bestAxis = -1;
                    var bestError = float.MaxValue;
                    for (var avatarAxis = 0; avatarAxis < 3; avatarAxis++)
                    {
                        var predictedBase = BuildSingleAxisProbeRotation(axis, avatarAxis, baseValue);
                        var predicted = BuildSingleAxisProbeRotation(axis, avatarAxis, value);
                        var predictedDeltaLeft = Normalize(Multiply(predicted, Inverse(predictedBase)));
                        var unityDeltaLeft = Normalize(Multiply(unityRotation, Inverse(unityBaseRotation)));
                        var predictedDeltaRight = Normalize(Multiply(Inverse(predictedBase), predicted));
                        var unityDeltaRight = Normalize(Multiply(Inverse(unityBaseRotation), unityRotation));
                        var error = MathF.Min(
                            QuaternionAngleDegrees(predictedDeltaLeft, unityDeltaLeft),
                            QuaternionAngleDegrees(predictedDeltaRight, unityDeltaRight));
                        var angle = QuaternionAngleDegrees(predictedDeltaLeft, new[] { 0f, 0f, 0f, 1f });
                        if (error < bestError)
                        {
                            bestError = error;
                            bestAxis = avatarAxis;
                        }
                        candidates.Add(new JObject
                        {
                            ["avatarAxis"] = avatarAxis,
                            ["axisName"] = AvatarAxisName(avatarAxis),
                            ["predictedDeltaDegrees"] = angle,
                            ["errorDegrees"] = error,
                        });
                    }

                    var currentAxis = CurrentExpectedAvatarAxisForMuscle(target.HumanBone, muscleName);
                    rows.Add(new JObject
                    {
                        ["muscleName"] = muscleName,
                        ["value"] = value,
                        ["humanBone"] = target.HumanBone,
                        ["probePath"] = probePath,
                        ["bestAvatarAxis"] = bestAxis,
                        ["bestAxisName"] = AvatarAxisName(bestAxis),
                        ["bestErrorDegrees"] = bestError,
                        ["currentExpectedAxis"] = currentAxis,
                        ["currentExpectedAxisName"] = AvatarAxisName(currentAxis),
                        ["candidates"] = candidates,
                    });
                }
            }

            return new JObject
            {
                ["status"] = rows.Count == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: for each expected single-muscle target, try Avatar X/Y/Z as the destination axis and compare against Unity HumanPoseHandler probes. This exposes per-muscle axis mapping mistakes such as distal twist being sent to a zero-limit axis.",
                ["rows"] = rows,
            };
        }

        private static JObject BuildSingleMuscleSourceTargetAxisFitSummary(
            JObject[] probes,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex)
        {
            var expectedByMuscle = BuildExpectedInfluenceByMuscle(solverNodes, solverAxes, humanBoneIndex);
            var rows = new JArray();
            foreach (var probe in probes
                .Where(x => !string.IsNullOrWhiteSpace((string)x["muscleName"]))
                .OrderBy(x => (string)x["muscleName"], StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => (float?)x["value"] ?? 0f))
            {
                var muscleName = (string)probe["muscleName"];
                if (!expectedByMuscle.TryGetValue(muscleName, out var expected) || expected.Length == 0)
                {
                    continue;
                }

                var value = (float?)probe["value"] ?? 0f;
                var baseValue = (float?)probe["baseValue"] ?? 0f;
                foreach (var target in expected)
                {
                    if (!TryGetAxisByPath(solverNodes, solverAxes, target.Path, out var targetAxis) ||
                        !TryReadProbeRotation(probe, target.Path, out var probePath, out var unityBaseRotation, out var unityRotation))
                    {
                        continue;
                    }

                    var sourceCandidates = BuildSourceBoneCandidates(muscleName, target.HumanBone)
                        .Select(humanBone =>
                        {
                            return TryGetHumanBonePathAxis(solverNodes, solverAxes, humanBoneIndex, humanBone, out var sourcePath, out var axis)
                                ? new SourceAxisCandidate(humanBone, sourcePath, axis)
                                : null;
                        })
                        .Where(x => x != null)
                        .GroupBy(x => x.HumanBone, StringComparer.OrdinalIgnoreCase)
                        .Select(x => x.First())
                        .ToArray();

                    var candidates = new List<SourceTargetAxisFitRow>();
                    foreach (var source in sourceCandidates)
                    {
                        for (var sourceAxis = 0; sourceAxis < 3; sourceAxis++)
                        {
                            for (var targetAvatarAxis = 0; targetAvatarAxis < 3; targetAvatarAxis++)
                            {
                                var predictedBase = BuildSourceTargetAxisProbeRotation(source.Axis, targetAxis, sourceAxis, targetAvatarAxis, baseValue);
                                var predicted = BuildSourceTargetAxisProbeRotation(source.Axis, targetAxis, sourceAxis, targetAvatarAxis, value);
                                var error = CalculateDeltaRotationError(predictedBase, predicted, unityBaseRotation, unityRotation);
                                var angle = QuaternionAngleDegrees(
                                    Normalize(Multiply(predicted, Inverse(predictedBase))),
                                    new[] { 0f, 0f, 0f, 1f });
                                candidates.Add(new SourceTargetAxisFitRow(
                                    source.HumanBone,
                                    source.Path,
                                    sourceAxis,
                                    targetAvatarAxis,
                                    angle,
                                    error));
                            }
                        }
                    }

                    var best = candidates
                        .OrderBy(x => x.ErrorDegrees)
                        .ThenBy(x => x.SourceHumanBone, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(x => x.SourceAxis)
                        .ThenBy(x => x.TargetAxis)
                        .FirstOrDefault();
                    var currentSource = InferSourceHumanBoneForMuscle(muscleName, target.HumanBone);
                    rows.Add(new JObject
                    {
                        ["muscleName"] = muscleName,
                        ["value"] = value,
                        ["targetHumanBone"] = target.HumanBone,
                        ["probePath"] = probePath,
                        ["currentSourceHumanBone"] = currentSource,
                        ["currentTargetAxis"] = CurrentExpectedAvatarAxisForMuscle(target.HumanBone, muscleName),
                        ["currentTargetAxisName"] = AvatarAxisName(CurrentExpectedAvatarAxisForMuscle(target.HumanBone, muscleName)),
                        ["bestSourceHumanBone"] = best?.SourceHumanBone,
                        ["bestSourceAxis"] = best?.SourceAxis ?? -1,
                        ["bestSourceAxisName"] = AvatarAxisName(best?.SourceAxis ?? -1),
                        ["bestTargetAxis"] = best?.TargetAxis ?? -1,
                        ["bestTargetAxisName"] = AvatarAxisName(best?.TargetAxis ?? -1),
                        ["bestPredictedDeltaDegrees"] = best?.PredictedDeltaDegrees ?? 0f,
                        ["bestErrorDegrees"] = best?.ErrorDegrees ?? 0f,
                        ["topCandidates"] = ToSourceTargetAxisFitJson(candidates
                            .OrderBy(x => x.ErrorDegrees)
                            .ThenBy(x => x.SourceHumanBone, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(x => x.SourceAxis)
                            .ThenBy(x => x.TargetAxis)
                            .Take(8)),
                    });
                }
            }

            return new JObject
            {
                ["status"] = rows.Count == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: enumerates source-bone Avatar limits and target-bone Avatar axes for each single-muscle target. This checks whether distal twist uses the source limb limits while writing rotation on the distal child transform.",
                ["rows"] = rows,
            };
        }

        private static JObject BuildSingleMuscleFormulaDeltaSideSummary(
            JObject[] probes,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex)
        {
            var expectedByMuscle = BuildExpectedInfluenceByMuscle(solverNodes, solverAxes, humanBoneIndex);
            var rows = new List<JObject>();
            foreach (var probe in probes
                .Where(x => !string.IsNullOrWhiteSpace((string)x["muscleName"]))
                .OrderBy(x => (string)x["muscleName"], StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => (float?)x["value"] ?? 0f))
            {
                var muscleName = (string)probe["muscleName"];
                if (!IsArmTwistOrStretchMuscle(muscleName) ||
                    !expectedByMuscle.TryGetValue(muscleName, out var expected) ||
                    expected.Length == 0)
                {
                    continue;
                }

                var value = (float?)probe["value"] ?? 0f;
                var baseValue = (float?)probe["baseValue"] ?? 0f;
                foreach (var target in expected)
                {
                    if (!TryGetAxisByPath(solverNodes, solverAxes, target.Path, out var targetAxis) ||
                        !TryReadProbeRotation(probe, target.Path, out var probePath, out var unityBaseRotation, out var unityRotation))
                    {
                        continue;
                    }

                    var unityLeft = Normalize(Multiply(unityRotation, Inverse(unityBaseRotation)));
                    var unityRight = Normalize(Multiply(Inverse(unityBaseRotation), unityRotation));
                    var sourceCandidates = BuildSourceBoneCandidates(muscleName, target.HumanBone)
                        .Select(humanBone => TryGetHumanBonePathAxis(solverNodes, solverAxes, humanBoneIndex, humanBone, out var sourcePath, out var axis)
                            ? new SourceAxisCandidate(humanBone, sourcePath, axis)
                            : null)
                        .Where(x => x != null)
                        .GroupBy(x => x.HumanBone, StringComparer.OrdinalIgnoreCase)
                        .Select(x => x.First())
                        .ToArray();

                    foreach (var source in sourceCandidates)
                    {
                        for (var sourceAxis = 0; sourceAxis < 3; sourceAxis++)
                        {
                            for (var targetAvatarAxis = 0; targetAvatarAxis < 3; targetAvatarAxis++)
                            {
                                var angleBase = LimitMuscle(baseValue, source.Axis, sourceAxis);
                                var angle = LimitMuscle(value, source.Axis, sourceAxis);
                                foreach (var formulaName in BuildSingleMuscleFormulaCandidateNames())
                                {
                                    var predictedBase = BuildSingleMuscleFormulaCandidateRotation(targetAxis, targetAvatarAxis, angleBase, formulaName);
                                    var predicted = BuildSingleMuscleFormulaCandidateRotation(targetAxis, targetAvatarAxis, angle, formulaName);
                                    var predictedLeft = Normalize(Multiply(predicted, Inverse(predictedBase)));
                                    var predictedRight = Normalize(Multiply(Inverse(predictedBase), predicted));
                                    AddFormulaDeltaSideRow(rows, formulaName, muscleName, target, source, sourceAxis, targetAvatarAxis, value, probePath, "left_to_left", predictedLeft, unityLeft);
                                    AddFormulaDeltaSideRow(rows, formulaName, muscleName, target, source, sourceAxis, targetAvatarAxis, value, probePath, "right_to_right", predictedRight, unityRight);
                                    AddFormulaDeltaSideRow(rows, formulaName, muscleName, target, source, sourceAxis, targetAvatarAxis, value, probePath, "left_to_right", predictedLeft, unityRight);
                                    AddFormulaDeltaSideRow(rows, formulaName, muscleName, target, source, sourceAxis, targetAvatarAxis, value, probePath, "right_to_left", predictedRight, unityLeft);
                                }
                            }
                        }
                    }
                }
            }

            if (rows.Count == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["rowCount"] = 0,
                    ["rule"] = "Diagnostic only: no Arm Twist / Forearm Stretch single-muscle probes were available for side-specific formula comparison.",
                };
            }

            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: expands single-muscle formula matching into left/right delta sides instead of taking min(left,right). This prevents a formula from looking correct merely because the opposite delta side matched.",
                ["rowCount"] = rows.Count,
                ["gateSummary"] = BuildSingleMuscleFormulaDeltaSideGateSummary(rows),
                ["bestByMuscle"] = new JArray(rows
                    .GroupBy(x => $"{(string)x["targetHumanBone"]}\u001f{(string)x["muscleName"]}", StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(group =>
                    {
                        var items = group.ToArray();
                        var first = items[0];
                        return new JObject
                        {
                            ["targetHumanBone"] = (string)first["targetHumanBone"],
                            ["muscleName"] = (string)first["muscleName"],
                            ["muscleFamily"] = (string)first["muscleFamily"],
                            ["topCandidates"] = new JArray(BuildFormulaDeltaSideGroups(items).Take(12)),
                        };
                    })),
                ["bestByMuscleFamily"] = new JArray(rows
                    .GroupBy(x => $"{(string)x["bodyGroup"]}\u001f{(string)x["muscleFamily"]}", StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(group =>
                    {
                        var items = group.ToArray();
                        var first = items[0];
                        return new JObject
                        {
                            ["bodyGroup"] = (string)first["bodyGroup"],
                            ["muscleFamily"] = (string)first["muscleFamily"],
                            ["topCandidates"] = new JArray(BuildFormulaDeltaSideGroups(items).Take(12)),
                        };
                    })),
                ["topWorstSamples"] = new JArray(rows
                    .GroupBy(x => $"{(string)x["targetHumanBone"]}\u001f{(string)x["muscleName"]}\u001f{JsonFloat(x, "value").ToString("R", CultureInfo.InvariantCulture)}", StringComparer.OrdinalIgnoreCase)
                    .Select(group => group
                        .OrderBy(x => JsonFloat(x, "errorDegrees"))
                        .ThenBy(x => (string)x["sideMode"], StringComparer.OrdinalIgnoreCase)
                        .First())
                    .OrderByDescending(x => JsonFloat(x, "errorDegrees"))
                    .Take(32)),
            };
        }

        private static JObject BuildSingleMuscleFormulaDeltaSideGateSummary(IReadOnlyCollection<JObject> rows)
        {
            if (rows == null || rows.Count == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["rowCount"] = 0,
                    ["rule"] = "Diagnostic only: no side-specific single-muscle formula rows were available.",
                };
            }

            var gateRows = rows
                .GroupBy(x => $"{(string)x["targetHumanBone"]}\u001f{(string)x["muscleName"]}", StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var items = group.ToArray();
                    var first = items[0];
                    var best = BuildFormulaDeltaSideGroups(items).FirstOrDefault();
                    if (best == null)
                    {
                        return null;
                    }

                    var maxError = JsonFloat(best, "maxErrorDegrees");
                    var avgError = (double?)best["avgErrorDegrees"] ?? double.MaxValue;
                    var candidateStatus = maxError <= 1f
                        ? "ready_for_timeline_formula_test"
                        : maxError <= 8f
                            ? "review_axis_or_value_scaling"
                            : "not_ready";
                    var nextFormulaWork = candidateStatus == "ready_for_timeline_formula_test"
                        ? "validate_single_muscle_delta_on_timeline"
                        : candidateStatus == "review_axis_or_value_scaling"
                            ? "review_axis_side_or_limit_scaling"
                            : "extend_unity_single_muscle_probe";

                    // 这里故意只取每个 muscle 的最佳候选，方便跨模型脚本找“共同稳定公式”。
                    return new JObject
                    {
                        ["targetHumanBone"] = (string)first["targetHumanBone"],
                        ["targetPath"] = (string)first["targetPath"],
                        ["bodyGroup"] = (string)first["bodyGroup"],
                        ["muscleName"] = (string)first["muscleName"],
                        ["muscleFamily"] = (string)first["muscleFamily"],
                        ["candidate"] = (string)best["candidate"],
                        ["formula"] = (string)best["formula"],
                        ["sideMode"] = (string)best["sideMode"],
                        ["sourceHumanBone"] = (string)best["sourceHumanBone"],
                        ["sourceAxisName"] = (string)best["sourceAxisName"],
                        ["targetAxisName"] = (string)best["targetAxisName"],
                        ["rowCount"] = (int?)best["rowCount"] ?? 0,
                        ["maxErrorDegrees"] = maxError,
                        ["avgErrorDegrees"] = avgError,
                        ["p90ErrorDegrees"] = JsonFloat(best, "p90ErrorDegrees"),
                        ["under1DegreeCount"] = (int?)best["under1DegreeCount"] ?? 0,
                        ["under5DegreeCount"] = (int?)best["under5DegreeCount"] ?? 0,
                        ["candidateStatus"] = candidateStatus,
                        ["nextFormulaWork"] = nextFormulaWork,
                    };
                })
                .Where(x => x != null)
                .ToArray();

            return new JObject
            {
                ["status"] = gateRows.Length == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: one best side-specific formula per target muscle. A ready row only means the Unity single-muscle delta is reproduced; it is not a production timeline solver rule.",
                ["rowCount"] = gateRows.Length,
                ["readyCount"] = gateRows.Count(x => string.Equals((string)x["candidateStatus"], "ready_for_timeline_formula_test", StringComparison.OrdinalIgnoreCase)),
                ["reviewCount"] = gateRows.Count(x => string.Equals((string)x["candidateStatus"], "review_axis_or_value_scaling", StringComparison.OrdinalIgnoreCase)),
                ["notReadyCount"] = gateRows.Count(x => string.Equals((string)x["candidateStatus"], "not_ready", StringComparison.OrdinalIgnoreCase)),
                ["rows"] = new JArray(gateRows
                    .OrderBy(x => (string)x["bodyGroup"], StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => (string)x["targetHumanBone"], StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => (string)x["muscleName"], StringComparer.OrdinalIgnoreCase)),
            };
        }

        private static JObject BuildForearmStretchScaleProbeSummary(
            JObject[] probes,
            JObject solver,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex)
        {
            var expectedByMuscle = BuildExpectedInfluenceByMuscle(solverNodes, solverAxes, humanBoneIndex);
            var rows = new List<JObject>();
            var scaleCandidates = BuildForearmStretchScaleCandidates(solver);
            foreach (var probe in probes
                .Where(x => ((string)x["muscleName"])?.IndexOf("Forearm Stretch", StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(x => (string)x["muscleName"], StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => (float?)x["value"] ?? 0f))
            {
                var muscleName = (string)probe["muscleName"];
                if (string.IsNullOrWhiteSpace(muscleName) ||
                    !expectedByMuscle.TryGetValue(muscleName, out var expected) ||
                    expected.Length == 0)
                {
                    continue;
                }

                var value = (float?)probe["value"] ?? 0f;
                var baseValue = (float?)probe["baseValue"] ?? 0f;
                foreach (var target in expected)
                {
                    if (!TryGetAxisByPath(solverNodes, solverAxes, target.Path, out var targetAxis) ||
                        !TryReadProbeRotation(probe, target.Path, out var probePath, out var unityBaseRotation, out var unityRotation))
                    {
                        continue;
                    }

                    var unityLeft = Normalize(Multiply(unityRotation, Inverse(unityBaseRotation)));
                    var unityRight = Normalize(Multiply(Inverse(unityBaseRotation), unityRotation));
                    var sourceCandidates = BuildSourceBoneCandidates(muscleName, target.HumanBone)
                        .Select(humanBone => TryGetHumanBonePathAxis(solverNodes, solverAxes, humanBoneIndex, humanBone, out var sourcePath, out var axis)
                            ? new SourceAxisCandidate(humanBone, sourcePath, axis)
                            : null)
                        .Where(x => x != null)
                        .GroupBy(x => x.HumanBone, StringComparer.OrdinalIgnoreCase)
                        .Select(x => x.First())
                        .ToArray();

                    foreach (var source in sourceCandidates)
                    {
                        for (var sourceAxis = 0; sourceAxis < 3; sourceAxis++)
                        {
                            for (var targetAvatarAxis = 0; targetAvatarAxis < 3; targetAvatarAxis++)
                            {
                                foreach (var scale in scaleCandidates)
                                {
                                    var angleBase = BuildScaledLimitMuscle(baseValue, source.Axis, sourceAxis, scale);
                                    var angle = BuildScaledLimitMuscle(value, source.Axis, sourceAxis, scale);
                                    foreach (var formulaName in BuildSingleMuscleFormulaCandidateNames())
                                    {
                                        var predictedBase = BuildSingleMuscleFormulaCandidateRotation(targetAxis, targetAvatarAxis, angleBase, formulaName);
                                        var predicted = BuildSingleMuscleFormulaCandidateRotation(targetAxis, targetAvatarAxis, angle, formulaName);
                                        var predictedLeft = Normalize(Multiply(predicted, Inverse(predictedBase)));
                                        var predictedRight = Normalize(Multiply(Inverse(predictedBase), predicted));
                                        AddForearmStretchScaleRow(rows, formulaName, muscleName, target, source, sourceAxis, targetAvatarAxis, value, probePath, scale, "left_to_left", predictedLeft, unityLeft);
                                        AddForearmStretchScaleRow(rows, formulaName, muscleName, target, source, sourceAxis, targetAvatarAxis, value, probePath, scale, "right_to_right", predictedRight, unityRight);
                                        AddForearmStretchScaleRow(rows, formulaName, muscleName, target, source, sourceAxis, targetAvatarAxis, value, probePath, scale, "left_to_right", predictedLeft, unityRight);
                                        AddForearmStretchScaleRow(rows, formulaName, muscleName, target, source, sourceAxis, targetAvatarAxis, value, probePath, scale, "right_to_left", predictedRight, unityLeft);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (rows.Count == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["rowCount"] = 0,
                    ["rule"] = "Diagnostic only: no Forearm Stretch single-muscle probes were available for scale comparison.",
                };
            }

            var bestByMuscle = rows
                .GroupBy(x => $"{(string)x["targetHumanBone"]}\u001f{(string)x["muscleName"]}", StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var items = group.ToArray();
                    var first = items[0];
                    var best = BuildFormulaDeltaSideGroups(items).FirstOrDefault();
                    var current = BuildFormulaDeltaSideGroups(items.Where(x => string.Equals((string)x["scaleName"], "current", StringComparison.OrdinalIgnoreCase))).FirstOrDefault();
                    var bestMax = JsonFloat(best, "maxErrorDegrees");
                    var currentMax = current == null ? bestMax : JsonFloat(current, "maxErrorDegrees");
                    return new JObject
                    {
                        ["targetHumanBone"] = (string)first["targetHumanBone"],
                        ["targetPath"] = (string)first["targetPath"],
                        ["bodyGroup"] = (string)first["bodyGroup"],
                        ["muscleName"] = (string)first["muscleName"],
                        ["bestCandidate"] = best,
                        ["currentCandidate"] = current,
                        ["bestBeatsCurrent"] = best != null && current != null && bestMax + 0.001f < currentMax,
                        ["improvementDegrees"] = currentMax - bestMax,
                        ["candidateStatus"] = bestMax <= 1f
                            ? "ready_for_timeline_formula_test"
                            : bestMax <= 8f
                                ? "review_axis_side_or_limit_scaling"
                                : "not_ready",
                        ["topScaleFamilies"] = new JArray(BuildForearmStretchScaleFamilies(items).Take(10)),
                    };
                })
                .ToArray();

            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: enumerates Forearm Stretch value/angle scale candidates, including AvatarConstant twist.armStretch/foreArm parameters. This only tests Unity single-muscle delta matching and must not be migrated directly to production.",
                ["rowCount"] = rows.Count,
                ["scaleCandidates"] = new JArray(scaleCandidates.Select(x => new JObject
                {
                    ["name"] = x.Name,
                    ["mode"] = x.Mode,
                    ["value"] = x.Value,
                })),
                ["bestByMuscle"] = new JArray(bestByMuscle),
            };
        }

        private static ForearmStretchScaleCandidate[] BuildForearmStretchScaleCandidates(JObject solver)
        {
            var armStretch = ReadSolverFloatValue(solver, "twist", "armStretch");
            var foreArm = ReadSolverFloatValue(solver, "twist", "foreArm");
            var values = new List<ForearmStretchScaleCandidate>
            {
                new("current", "angle_scale", 1f),
                new("inverse_current", "angle_scale", -1f),
            };

            AddScaleCandidate(values, "armStretch", "angle_scale", armStretch);
            AddScaleCandidate(values, "inverse_armStretch", "angle_scale", -armStretch);
            AddScaleCandidate(values, "foreArm", "angle_scale", foreArm);
            AddScaleCandidate(values, "inverse_foreArm", "angle_scale", -foreArm);
            AddScaleCandidate(values, "one_minus_armStretch", "angle_scale", 1f - armStretch);
            AddScaleCandidate(values, "one_plus_armStretch", "angle_scale", 1f + armStretch);
            AddScaleCandidate(values, "one_minus_foreArm", "angle_scale", 1f - foreArm);
            AddScaleCandidate(values, "one_plus_foreArm", "angle_scale", 1f + foreArm);
            if (MathF.Abs(armStretch) > 0.000001f)
            {
                AddScaleCandidate(values, "inverse_value_armStretch", "angle_scale", 1f / armStretch);
            }
            if (MathF.Abs(foreArm) > 0.000001f)
            {
                AddScaleCandidate(values, "inverse_value_foreArm", "angle_scale", 1f / foreArm);
            }

            return values
                .GroupBy(x => $"{x.Mode}\u001f{x.Name}", StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToArray();
        }

        private static float ReadSolverFloatValue(JObject solver, string group, string propertyName)
        {
            return (float?)solver?[group]?[propertyName] ?? 0f;
        }

        private static void AddScaleCandidate(List<ForearmStretchScaleCandidate> values, string name, string mode, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return;
            }
            values.Add(new ForearmStretchScaleCandidate(name, mode, value));
        }

        private static float BuildScaledLimitMuscle(float value, SolverAxis axis, int avatarAxis, ForearmStretchScaleCandidate scale)
        {
            var angle = LimitMuscle(value, axis, avatarAxis);
            return string.Equals(scale.Mode, "angle_scale", StringComparison.OrdinalIgnoreCase)
                ? angle * scale.Value
                : LimitMuscle(value * scale.Value, axis, avatarAxis);
        }

        private static void AddForearmStretchScaleRow(
            List<JObject> rows,
            string formulaName,
            string muscleName,
            ExpectedInfluenceTarget target,
            SourceAxisCandidate source,
            int sourceAxis,
            int targetAvatarAxis,
            float value,
            string probePath,
            ForearmStretchScaleCandidate scale,
            string sideMode,
            float[] predictedDelta,
            float[] unityDelta)
        {
            rows.Add(new JObject
            {
                ["candidate"] = $"{formulaName}|scale={scale.Name}:{scale.Value.ToString("R", CultureInfo.InvariantCulture)}|source={source.HumanBone}:{AvatarAxisName(sourceAxis)}|target={AvatarAxisName(targetAvatarAxis)}|side={sideMode}",
                ["formula"] = formulaName,
                ["scaleName"] = scale.Name,
                ["scaleMode"] = scale.Mode,
                ["scaleValue"] = scale.Value,
                ["sideMode"] = sideMode,
                ["muscleName"] = muscleName,
                ["muscleFamily"] = ClassifyMuscleFamily(muscleName),
                ["targetHumanBone"] = target.HumanBone,
                ["targetPath"] = target.Path,
                ["probePath"] = probePath,
                ["bodyGroup"] = ClassifyBodyGroup(target.Path),
                ["sourceHumanBone"] = source.HumanBone,
                ["sourceAxis"] = sourceAxis,
                ["sourceAxisName"] = AvatarAxisName(sourceAxis),
                ["targetAxis"] = targetAvatarAxis,
                ["targetAxisName"] = AvatarAxisName(targetAvatarAxis),
                ["value"] = value,
                ["errorDegrees"] = QuaternionAngleDegrees(predictedDelta, unityDelta),
            });
        }

        private static IEnumerable<JObject> BuildForearmStretchScaleFamilies(IEnumerable<JObject> rows)
        {
            return (rows ?? Enumerable.Empty<JObject>())
                .GroupBy(x => (string)x["scaleName"], StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var best = BuildFormulaDeltaSideGroups(group).FirstOrDefault();
                    return best == null
                        ? null
                        : new JObject
                        {
                            ["scaleName"] = group.Key,
                            ["bestCandidate"] = best,
                        };
                })
                .Where(x => x != null)
                .OrderBy(x => JsonFloat((JObject)x["bestCandidate"], "maxErrorDegrees"))
                .ThenBy(x => (string)x["scaleName"], StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsArmTwistOrStretchMuscle(string muscleName)
        {
            return muscleName?.IndexOf("Arm Twist", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   muscleName?.IndexOf("Forearm Stretch", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void AddFormulaDeltaSideRow(
            List<JObject> rows,
            string formulaName,
            string muscleName,
            ExpectedInfluenceTarget target,
            SourceAxisCandidate source,
            int sourceAxis,
            int targetAvatarAxis,
            float value,
            string probePath,
            string sideMode,
            float[] predictedDelta,
            float[] unityDelta)
        {
            rows.Add(new JObject
            {
                ["candidate"] = $"{formulaName}|source={source.HumanBone}:{AvatarAxisName(sourceAxis)}|target={AvatarAxisName(targetAvatarAxis)}|side={sideMode}",
                ["formula"] = formulaName,
                ["sideMode"] = sideMode,
                ["muscleName"] = muscleName,
                ["muscleFamily"] = ClassifyMuscleFamily(muscleName),
                ["targetHumanBone"] = target.HumanBone,
                ["targetPath"] = target.Path,
                ["probePath"] = probePath,
                ["bodyGroup"] = ClassifyBodyGroup(target.Path),
                ["sourceHumanBone"] = source.HumanBone,
                ["sourceAxis"] = sourceAxis,
                ["sourceAxisName"] = AvatarAxisName(sourceAxis),
                ["targetAxis"] = targetAvatarAxis,
                ["targetAxisName"] = AvatarAxisName(targetAvatarAxis),
                ["value"] = value,
                ["errorDegrees"] = QuaternionAngleDegrees(predictedDelta, unityDelta),
                ["predictedDeltaAxis"] = ToJArray(ToAxisAngle(predictedDelta).Axis),
                ["unityDeltaAxis"] = ToJArray(ToAxisAngle(unityDelta).Axis),
            });
        }

        private static IEnumerable<JObject> BuildFormulaDeltaSideGroups(IEnumerable<JObject> rows)
        {
            return (rows ?? Enumerable.Empty<JObject>())
                .GroupBy(x => (string)x["candidate"], StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var items = group.ToArray();
                    var first = items[0];
                    var errors = items.Select(x => JsonFloat(x, "errorDegrees")).ToArray();
                    return new JObject
                    {
                        ["candidate"] = group.Key,
                        ["formula"] = (string)first["formula"],
                        ["scaleName"] = first["scaleName"],
                        ["scaleMode"] = first["scaleMode"],
                        ["scaleValue"] = first["scaleValue"],
                        ["sideMode"] = (string)first["sideMode"],
                        ["sourceHumanBone"] = (string)first["sourceHumanBone"],
                        ["sourceAxisName"] = (string)first["sourceAxisName"],
                        ["targetAxisName"] = (string)first["targetAxisName"],
                        ["rowCount"] = items.Length,
                        ["maxErrorDegrees"] = errors.Length == 0 ? 0f : errors.Max(),
                        ["avgErrorDegrees"] = errors.Length == 0 ? 0.0 : errors.Average(),
                        ["p90ErrorDegrees"] = Percentile(errors, 0.90f),
                        ["under1DegreeCount"] = items.Count(x => JsonFloat(x, "errorDegrees") <= 1f),
                        ["under5DegreeCount"] = items.Count(x => JsonFloat(x, "errorDegrees") <= 5f),
                    };
                })
                .OrderBy(x => JsonFloat(x, "maxErrorDegrees"))
                .ThenBy(x => (double?)x["avgErrorDegrees"] ?? double.MaxValue)
                .ThenBy(x => (string)x["candidate"], StringComparer.OrdinalIgnoreCase);
        }

        private static JObject BuildSingleMuscleProbeBasePoseSummary(
            JObject[] probes,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex)
        {
            var expectedByMuscle = BuildExpectedInfluenceByMuscle(solverNodes, solverAxes, humanBoneIndex);
            var rows = new List<JObject>();
            foreach (var probe in probes
                .Where(x => !string.IsNullOrWhiteSpace((string)x["muscleName"]))
                .OrderBy(x => (string)x["muscleName"], StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => (float?)x["value"] ?? 0f))
            {
                var muscleName = (string)probe["muscleName"];
                if (!IsArmTwistOrStretchMuscle(muscleName) ||
                    !expectedByMuscle.TryGetValue(muscleName, out var expected) ||
                    expected.Length == 0)
                {
                    continue;
                }

                var value = (float?)probe["value"] ?? 0f;
                var baseValue = (float?)probe["baseValue"] ?? 0f;
                foreach (var target in expected)
                {
                    if (!TryGetAxisByPath(solverNodes, solverAxes, target.Path, out var targetAxis) ||
                        !TryReadProbeRotation(probe, target.Path, out var probePath, out var unityBaseRotation, out _))
                    {
                        continue;
                    }

                    var currentZero = Normalize(Multiply(Normalize(targetAxis.PreQ), Inverse(Normalize(targetAxis.PostQ))));
                    var leftCorrection = Normalize(Multiply(unityBaseRotation, Inverse(currentZero)));
                    var rightCorrection = Normalize(Multiply(Inverse(currentZero), unityBaseRotation));
                    rows.Add(new JObject
                    {
                        ["muscleName"] = muscleName,
                        ["muscleFamily"] = ClassifyMuscleFamily(muscleName),
                        ["targetHumanBone"] = target.HumanBone,
                        ["targetPath"] = target.Path,
                        ["probePath"] = probePath,
                        ["bodyGroup"] = ClassifyBodyGroup(target.Path),
                        ["value"] = value,
                        ["baseValue"] = baseValue,
                        ["baseVsCurrentZeroDegrees"] = QuaternionAngleDegrees(unityBaseRotation, currentZero),
                        ["leftCorrectionAngleDegrees"] = QuaternionAngleDegrees(IdentityQuaternion, leftCorrection),
                        ["rightCorrectionAngleDegrees"] = QuaternionAngleDegrees(IdentityQuaternion, rightCorrection),
                        ["unityBaseQuaternion"] = QuaternionToJObject(unityBaseRotation),
                        ["currentZeroQuaternion"] = QuaternionToJObject(currentZero),
                        ["unityBaseAxis"] = ToJArray(ToAxisAngle(unityBaseRotation).Axis),
                        ["currentZeroAxis"] = ToJArray(ToAxisAngle(currentZero).Axis),
                    });
                }
            }

            if (rows.Count == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["rowCount"] = 0,
                    ["rule"] = "Diagnostic only: no Arm Twist / Forearm Stretch probe base rotations were available.",
                };
            }

            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: compares Unity single-muscle probe baseRotation/baseQ against AnimeStudio current zero pose preQ * inverse(postQ). If baseQ is offset, single-muscle delta formulas can look correct while full timeline composition still starts from the wrong pose.",
                ["rowCount"] = rows.Count,
                ["maxBaseVsCurrentZeroDegrees"] = rows.Max(x => JsonFloat(x, "baseVsCurrentZeroDegrees")),
                ["avgBaseVsCurrentZeroDegrees"] = rows.Average(x => JsonFloat(x, "baseVsCurrentZeroDegrees")),
                ["bestByMuscle"] = new JArray(rows
                    .GroupBy(x => $"{(string)x["targetHumanBone"]}\u001f{(string)x["muscleName"]}", StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(group => BuildProbeBasePoseGroupRow(group))),
                ["byMuscleFamily"] = new JArray(rows
                    .GroupBy(x => $"{(string)x["bodyGroup"]}\u001f{(string)x["muscleFamily"]}", StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(group => BuildProbeBasePoseFamilyRow(group))),
                ["topRows"] = new JArray(rows
                    .OrderByDescending(x => JsonFloat(x, "baseVsCurrentZeroDegrees"))
                    .ThenBy(x => (string)x["targetPath"], StringComparer.OrdinalIgnoreCase)
                    .Take(64)),
            };
        }

        private static JObject BuildProbeBasePoseGroupRow(IGrouping<string, JObject> group)
        {
            var items = group.ToArray();
            var first = items[0];
            var baseRotations = items
                .Select(x => new[]
                {
                    (float?)x["unityBaseQuaternion"]?["x"] ?? 0f,
                    (float?)x["unityBaseQuaternion"]?["y"] ?? 0f,
                    (float?)x["unityBaseQuaternion"]?["z"] ?? 0f,
                    (float?)x["unityBaseQuaternion"]?["w"] ?? 1f,
                })
                .ToArray();
            var maxBaseSpread = 0f;
            for (var i = 0; i < baseRotations.Length; i++)
            {
                for (var j = i + 1; j < baseRotations.Length; j++)
                {
                    maxBaseSpread = MathF.Max(maxBaseSpread, QuaternionAngleDegrees(baseRotations[i], baseRotations[j]));
                }
            }
            return new JObject
            {
                ["targetHumanBone"] = (string)first["targetHumanBone"],
                ["muscleName"] = (string)first["muscleName"],
                ["muscleFamily"] = (string)first["muscleFamily"],
                ["rowCount"] = items.Length,
                ["maxBaseVsCurrentZeroDegrees"] = items.Max(x => JsonFloat(x, "baseVsCurrentZeroDegrees")),
                ["avgBaseVsCurrentZeroDegrees"] = items.Average(x => JsonFloat(x, "baseVsCurrentZeroDegrees")),
                ["maxBaseSpreadDegrees"] = maxBaseSpread,
                ["topRows"] = new JArray(items
                    .OrderByDescending(x => JsonFloat(x, "baseVsCurrentZeroDegrees"))
                    .Take(8)),
            };
        }

        private static JObject BuildProbeBasePoseFamilyRow(IGrouping<string, JObject> group)
        {
            var items = group.ToArray();
            var first = items[0];
            return new JObject
            {
                ["bodyGroup"] = (string)first["bodyGroup"],
                ["muscleFamily"] = (string)first["muscleFamily"],
                ["rowCount"] = items.Length,
                ["maxBaseVsCurrentZeroDegrees"] = items.Max(x => JsonFloat(x, "baseVsCurrentZeroDegrees")),
                ["avgBaseVsCurrentZeroDegrees"] = items.Average(x => JsonFloat(x, "baseVsCurrentZeroDegrees")),
                ["worstTargetHumanBone"] = (string)items
                    .OrderByDescending(x => JsonFloat(x, "baseVsCurrentZeroDegrees"))
                    .First()["targetHumanBone"],
            };
        }

        private static JObject BuildSingleMuscleFormulaCandidateSummary(
            JObject[] probes,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex)
        {
            var expectedByMuscle = BuildExpectedInfluenceByMuscle(solverNodes, solverAxes, humanBoneIndex);
            var rows = new List<SingleMuscleFormulaCandidateRow>();
            foreach (var probe in probes
                .Where(x => !string.IsNullOrWhiteSpace((string)x["muscleName"]))
                .OrderBy(x => (string)x["muscleName"], StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => (float?)x["value"] ?? 0f))
            {
                var muscleName = (string)probe["muscleName"];
                if (!expectedByMuscle.TryGetValue(muscleName, out var expected) || expected.Length == 0)
                {
                    continue;
                }

                var value = (float?)probe["value"] ?? 0f;
                var baseValue = (float?)probe["baseValue"] ?? 0f;
                foreach (var target in expected)
                {
                    if (!TryGetAxisByPath(solverNodes, solverAxes, target.Path, out var targetAxis) ||
                        !TryReadProbeRotation(probe, target.Path, out var probePath, out var unityBaseRotation, out var unityRotation))
                    {
                        continue;
                    }

                    var sourceCandidates = BuildSourceBoneCandidates(muscleName, target.HumanBone)
                        .Select(humanBone => TryGetHumanBonePathAxis(solverNodes, solverAxes, humanBoneIndex, humanBone, out var sourcePath, out var axis)
                            ? new SourceAxisCandidate(humanBone, sourcePath, axis)
                            : null)
                        .Where(x => x != null)
                        .GroupBy(x => x.HumanBone, StringComparer.OrdinalIgnoreCase)
                        .Select(x => x.First())
                        .ToArray();

                    foreach (var source in sourceCandidates)
                    {
                        for (var sourceAxis = 0; sourceAxis < 3; sourceAxis++)
                        {
                            for (var targetAvatarAxis = 0; targetAvatarAxis < 3; targetAvatarAxis++)
                            {
                                var angleBase = LimitMuscle(baseValue, source.Axis, sourceAxis);
                                var angle = LimitMuscle(value, source.Axis, sourceAxis);
                                foreach (var formulaName in BuildSingleMuscleFormulaCandidateNames())
                                {
                                    var predictedBase = BuildSingleMuscleFormulaCandidateRotation(targetAxis, targetAvatarAxis, angleBase, formulaName);
                                    var predicted = BuildSingleMuscleFormulaCandidateRotation(targetAxis, targetAvatarAxis, angle, formulaName);
                                    var error = CalculateDeltaRotationError(predictedBase, predicted, unityBaseRotation, unityRotation);
                                    rows.Add(new SingleMuscleFormulaCandidateRow(
                                        formulaName,
                                        muscleName,
                                        target.HumanBone,
                                        source.HumanBone,
                                        sourceAxis,
                                        targetAvatarAxis,
                                        value,
                                        target.Path,
                                        probePath,
                                        ClassifyBodyGroup(target.Path),
                                        ClassifyMuscleFamily(muscleName),
                                        error));
                                }
                            }
                        }
                    }
                }
            }

            if (rows.Count == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["rowCount"] = 0,
                    ["rule"] = "Diagnostic only: no comparable Unity single-muscle probes were available.",
                };
            }

            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: enumerates deterministic source-limit, target-axis and preQ/postQ composition formulas for Unity single-muscle probes. A production solver change should only use candidates that stay stable across multiple models/clips.",
                ["rowCount"] = rows.Count,
                ["candidateCount"] = rows
                    .Select(x => BuildSingleMuscleFormulaCandidateKey(x, includeAxes: true))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(),
                ["topGlobalCandidates"] = new JArray(BuildSingleMuscleFormulaCandidateGroups(rows, includeAxes: true).Take(24)),
                ["topFormulaFamilies"] = new JArray(BuildSingleMuscleFormulaCandidateGroups(rows, includeAxes: false).Take(24)),
                ["bestByBodyGroup"] = new JArray(rows
                    .GroupBy(x => x.BodyGroup, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new JObject
                    {
                        ["bodyGroup"] = group.Key,
                        ["topCandidates"] = new JArray(BuildSingleMuscleFormulaCandidateGroups(group, includeAxes: true).Take(12)),
                    })),
                ["bestByBodyGroupAndMuscleFamily"] = new JArray(rows
                    .GroupBy(x => $"{x.BodyGroup}\u001f{x.MuscleFamily}", StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(group =>
                    {
                        var first = group.First();
                        return new JObject
                        {
                            ["bodyGroup"] = first.BodyGroup,
                            ["muscleFamily"] = first.MuscleFamily,
                            ["topCandidates"] = new JArray(BuildSingleMuscleFormulaCandidateGroups(group, includeAxes: true).Take(8)),
                        };
                    })),
                ["topWorstSamples"] = new JArray(rows
                    .GroupBy(x => $"{x.MuscleName}\u001f{x.TargetHumanBone}\u001f{x.Value}", StringComparer.OrdinalIgnoreCase)
                    .Select(group => group
                        .OrderBy(x => x.ErrorDegrees)
                        .ThenBy(x => x.FormulaName, StringComparer.OrdinalIgnoreCase)
                        .First())
                    .OrderByDescending(x => x.ErrorDegrees)
                    .ThenBy(x => x.MuscleName, StringComparer.OrdinalIgnoreCase)
                    .Take(32)
                    .Select(ToSingleMuscleFormulaCandidateJson)),
            };
        }

        private static IEnumerable<string> BuildSingleMuscleFormulaCandidateNames()
        {
            yield return "pre_middle_inversePost";
            yield return "zero_middle";
            yield return "middle_zero";
            yield return "zero_postProjectedDelta";
            yield return "postProjectedDelta_zero";
            yield return "preProjectedDelta_zero";
            yield return "zero_preProjectedDelta";
            yield return "post_middle_inversePost";
            yield return "inversePost_middle_post";
            yield return "pre_middle_inversePre";
            yield return "middle";
        }

        private static float[] BuildSingleMuscleFormulaCandidateRotation(SolverAxis axis, int targetAvatarAxis, float angle, string formulaName)
        {
            var middle = targetAvatarAxis switch
            {
                0 => AxisAngleRadiansToQuaternion(1, 0, 0, angle),
                1 => SwingRadiansToQuaternion(angle, 0),
                _ => SwingRadiansToQuaternion(0, angle),
            };
            var pre = Normalize(axis.PreQ);
            var post = Normalize(axis.PostQ);
            var zero = Normalize(Multiply(pre, Inverse(post)));
            return formulaName switch
            {
                "zero_middle" => Normalize(Multiply(zero, middle)),
                "middle_zero" => Normalize(Multiply(middle, zero)),
                "zero_postProjectedDelta" => Normalize(Multiply(zero, Normalize(Multiply(Multiply(Inverse(post), middle), post)))),
                "postProjectedDelta_zero" => Normalize(Multiply(Normalize(Multiply(Multiply(Inverse(post), middle), post)), zero)),
                "preProjectedDelta_zero" => Normalize(Multiply(Normalize(Multiply(Multiply(Inverse(pre), middle), pre)), zero)),
                "zero_preProjectedDelta" => Normalize(Multiply(zero, Normalize(Multiply(Multiply(Inverse(pre), middle), pre)))),
                "post_middle_inversePost" => Normalize(Multiply(Multiply(post, middle), Inverse(post))),
                "inversePost_middle_post" => Normalize(Multiply(Multiply(Inverse(post), middle), post)),
                "pre_middle_inversePre" => Normalize(Multiply(Multiply(pre, middle), Inverse(pre))),
                "middle" => Normalize(middle),
                _ => Normalize(Multiply(Multiply(pre, middle), Inverse(post))),
            };
        }

        private static IEnumerable<JObject> BuildSingleMuscleFormulaCandidateGroups(
            IEnumerable<SingleMuscleFormulaCandidateRow> rows,
            bool includeAxes)
        {
            return (rows ?? Enumerable.Empty<SingleMuscleFormulaCandidateRow>())
                .GroupBy(x => BuildSingleMuscleFormulaCandidateKey(x, includeAxes), StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var items = group.ToArray();
                    var first = items[0];
                    return new JObject
                    {
                        ["candidate"] = group.Key,
                        ["formula"] = first.FormulaName,
                        ["sourceHumanBone"] = includeAxes ? first.SourceHumanBone : null,
                        ["sourceAxis"] = includeAxes ? first.SourceAxis : null,
                        ["sourceAxisName"] = includeAxes ? AvatarAxisName(first.SourceAxis) : null,
                        ["targetAxis"] = includeAxes ? first.TargetAxis : null,
                        ["targetAxisName"] = includeAxes ? AvatarAxisName(first.TargetAxis) : null,
                        ["rowCount"] = items.Length,
                        ["maxErrorDegrees"] = items.Max(x => x.ErrorDegrees),
                        ["avgErrorDegrees"] = items.Average(x => x.ErrorDegrees),
                        ["p90ErrorDegrees"] = Percentile(items.Select(x => x.ErrorDegrees), 0.90f),
                        ["under5DegreeCount"] = items.Count(x => x.ErrorDegrees <= 5f),
                        ["under10DegreeCount"] = items.Count(x => x.ErrorDegrees <= 10f),
                        ["worstMuscle"] = items
                            .OrderByDescending(x => x.ErrorDegrees)
                            .ThenBy(x => x.MuscleName, StringComparer.OrdinalIgnoreCase)
                            .First()
                            .MuscleName,
                        ["worstHumanBone"] = items
                            .OrderByDescending(x => x.ErrorDegrees)
                            .ThenBy(x => x.TargetHumanBone, StringComparer.OrdinalIgnoreCase)
                            .First()
                            .TargetHumanBone,
                    };
                })
                .OrderBy(x => (double?)x["avgErrorDegrees"] ?? double.MaxValue)
                .ThenBy(x => (double?)x["p90ErrorDegrees"] ?? double.MaxValue)
                .ThenBy(x => (double?)x["maxErrorDegrees"] ?? double.MaxValue)
                .ThenBy(x => (string)x["candidate"], StringComparer.OrdinalIgnoreCase);
        }

        private static string BuildSingleMuscleFormulaCandidateKey(SingleMuscleFormulaCandidateRow row, bool includeAxes)
        {
            return includeAxes
                ? $"{row.FormulaName}|source={row.SourceHumanBone}:{AvatarAxisName(row.SourceAxis)}|target={AvatarAxisName(row.TargetAxis)}"
                : row.FormulaName;
        }

        private static JObject ToSingleMuscleFormulaCandidateJson(SingleMuscleFormulaCandidateRow row)
        {
            return new JObject
            {
                ["formula"] = row.FormulaName,
                ["muscleName"] = row.MuscleName,
                ["targetHumanBone"] = row.TargetHumanBone,
                ["sourceHumanBone"] = row.SourceHumanBone,
                ["sourceAxis"] = row.SourceAxis,
                ["sourceAxisName"] = AvatarAxisName(row.SourceAxis),
                ["targetAxis"] = row.TargetAxis,
                ["targetAxisName"] = AvatarAxisName(row.TargetAxis),
                ["value"] = row.Value,
                ["targetPath"] = row.TargetPath,
                ["probePath"] = row.ProbePath,
                ["bodyGroup"] = row.BodyGroup,
                ["muscleFamily"] = row.MuscleFamily,
                ["errorDegrees"] = row.ErrorDegrees,
            };
        }

        private static Dictionary<string, ExpectedInfluenceTarget[]> BuildExpectedInfluenceByMuscle(
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex)
        {
            var map = new Dictionary<string, List<ExpectedInfluenceTarget>>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in BuildCurrentSolverTargets())
            {
                if (!TryGetSolverTarget(solverNodes, solverAxes, humanBoneIndex, target, out var path, out _))
                {
                    continue;
                }

                AddExpectedInfluence(map, target.XAttribute, target.HumanBone, path);
                AddExpectedInfluence(map, target.YAttribute, target.HumanBone, path);
                AddExpectedInfluence(map, target.ZAttribute, target.HumanBone, path);
                AddExpectedInfluence(map, target.ExtraZAttribute, target.HumanBone, path);
            }

            return map.ToDictionary(
                x => x.Key,
                x => x.Value
                    .GroupBy(item => NormalizeBakePath(item.Path) ?? item.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderBy(item => item.HumanBone, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
        }

        private static void AddExpectedInfluence(
            Dictionary<string, List<ExpectedInfluenceTarget>> map,
            string muscleName,
            string humanBone,
            string path)
        {
            if (string.IsNullOrWhiteSpace(muscleName) || string.IsNullOrWhiteSpace(path))
            {
                return;
            }
            if (!map.TryGetValue(muscleName, out var list))
            {
                list = new List<ExpectedInfluenceTarget>();
                map[muscleName] = list;
            }
            list.Add(new ExpectedInfluenceTarget(humanBone, path));
        }

        private static IEnumerable<ProbeInfluenceRow> BuildProbeInfluenceRows(JObject probe)
        {
            foreach (var item in probe?["rotations"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var path = (string)item["path"];
                var baseQ = item["baseRotation"];
                var q = item["rotation"];
                if (string.IsNullOrWhiteSpace(path) || baseQ == null || q == null)
                {
                    continue;
                }

                var baseRotation = Normalize(new[]
                {
                    (float?)baseQ["x"] ?? 0f,
                    (float?)baseQ["y"] ?? 0f,
                    (float?)baseQ["z"] ?? 0f,
                    (float?)baseQ["w"] ?? 1f,
                });
                var rotation = Normalize(new[]
                {
                    (float?)q["x"] ?? 0f,
                    (float?)q["y"] ?? 0f,
                    (float?)q["z"] ?? 0f,
                    (float?)q["w"] ?? 1f,
                });
                var angle = QuaternionAngleDegrees(Normalize(Multiply(rotation, Inverse(baseRotation))), new[] { 0f, 0f, 0f, 1f });
                if (angle <= 0.01f)
                {
                    continue;
                }
                yield return new ProbeInfluenceRow(path, (string)item["name"], angle, ClassifyBodyGroup(path));
            }
        }

        private static bool PathsReferToSameBone(string left, string right)
        {
            var a = NormalizeBakePath(left);
            var b = NormalizeBakePath(right);
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            {
                return false;
            }
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
                   a.EndsWith("/" + b, StringComparison.OrdinalIgnoreCase) ||
                   b.EndsWith("/" + a, StringComparison.OrdinalIgnoreCase);
        }

        private static JArray ToExpectedInfluenceJson(IEnumerable<ExpectedInfluenceTarget> rows)
        {
            var array = new JArray();
            foreach (var row in rows)
            {
                array.Add(new JObject
                {
                    ["humanBone"] = row.HumanBone,
                    ["path"] = row.Path,
                    ["bodyGroup"] = ClassifyBodyGroup(row.Path),
                });
            }
            return array;
        }

        private static JArray ToProbeInfluenceJson(IEnumerable<ProbeInfluenceRow> rows)
        {
            var array = new JArray();
            foreach (var row in rows)
            {
                array.Add(new JObject
                {
                    ["path"] = row.Path,
                    ["name"] = row.Name,
                    ["deltaDegrees"] = row.AngleDegrees,
                    ["bodyGroup"] = row.BodyGroup,
                });
            }
            return array;
        }

        private static JArray ToSourceTargetAxisFitJson(IEnumerable<SourceTargetAxisFitRow> rows)
        {
            var array = new JArray();
            foreach (var row in rows)
            {
                array.Add(new JObject
                {
                    ["sourceHumanBone"] = row.SourceHumanBone,
                    ["sourcePath"] = row.SourcePath,
                    ["sourceAxis"] = row.SourceAxis,
                    ["sourceAxisName"] = AvatarAxisName(row.SourceAxis),
                    ["targetAxis"] = row.TargetAxis,
                    ["targetAxisName"] = AvatarAxisName(row.TargetAxis),
                    ["predictedDeltaDegrees"] = row.PredictedDeltaDegrees,
                    ["errorDegrees"] = row.ErrorDegrees,
                });
            }
            return array;
        }

        private static bool TryGetAxisByPath(JArray solverNodes, JArray solverAxes, string path, out SolverAxis axis)
        {
            axis = default;
            var normalized = NormalizeBakePath(path);
            foreach (var node in solverNodes?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var nodePath = NormalizeBakePath((string)node["path"] ?? (string)node["name"]);
                if (!PathsReferToSameBone(nodePath, normalized))
                {
                    continue;
                }
                var axesId = (int?)node["axesId"] ?? -1;
                if (axesId < 0 || axesId >= solverAxes.Count || solverAxes[axesId] is not JObject axisJson)
                {
                    return false;
                }
                axis = new SolverAxis(
                    ReadFloatArray(axisJson["preQ"] as JArray, 4),
                    ReadFloatArray(axisJson["postQ"] as JArray, 4),
                    ReadFloatArray(axisJson["sign"] as JArray, 3),
                    ReadFloatArray(axisJson["limitMin"] as JArray, 3),
                    ReadFloatArray(axisJson["limitMax"] as JArray, 3));
                return axis.PreQ != null && axis.PostQ != null && axis.Sign != null && axis.LimitMin != null && axis.LimitMax != null;
            }
            return false;
        }

        private static bool TryGetHumanBonePathAxis(
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex,
            string humanBone,
            out string path,
            out SolverAxis axis)
        {
            return TryGetSolverTarget(
                solverNodes,
                solverAxes,
                humanBoneIndex,
                new SolverTarget(humanBone, null, null, null),
                out path,
                out axis);
        }

        private static float[] BuildSingleAxisProbeRotation(SolverAxis axis, int avatarAxis, float value)
        {
            var angles = new float[3];
            angles[avatarAxis] = LimitMuscle(value, axis, avatarAxis);
            var middle = avatarAxis switch
            {
                0 => AxisAngleRadiansToQuaternion(1, 0, 0, angles[0]),
                1 => SwingRadiansToQuaternion(angles[1], 0),
                _ => SwingRadiansToQuaternion(0, angles[2]),
            };
            return Normalize(Multiply(Multiply(Normalize(axis.PreQ), middle), Inverse(Normalize(axis.PostQ))));
        }

        private static float[] BuildSourceTargetAxisProbeRotation(
            SolverAxis sourceAxis,
            SolverAxis targetAxis,
            int sourceAvatarAxis,
            int targetAvatarAxis,
            float value)
        {
            var angle = LimitMuscle(value, sourceAxis, sourceAvatarAxis);
            var angles = new float[3];
            angles[targetAvatarAxis] = angle;
            var middle = targetAvatarAxis switch
            {
                0 => AxisAngleRadiansToQuaternion(1, 0, 0, angles[0]),
                1 => SwingRadiansToQuaternion(angles[1], 0),
                _ => SwingRadiansToQuaternion(0, angles[2]),
            };
            return Normalize(Multiply(Multiply(Normalize(targetAxis.PreQ), middle), Inverse(Normalize(targetAxis.PostQ))));
        }

        private static float CalculateDeltaRotationError(float[] predictedBase, float[] predicted, float[] unityBase, float[] unityRotation)
        {
            var predictedDeltaLeft = Normalize(Multiply(predicted, Inverse(predictedBase)));
            var unityDeltaLeft = Normalize(Multiply(unityRotation, Inverse(unityBase)));
            var predictedDeltaRight = Normalize(Multiply(Inverse(predictedBase), predicted));
            var unityDeltaRight = Normalize(Multiply(Inverse(unityBase), unityRotation));
            return MathF.Min(
                QuaternionAngleDegrees(predictedDeltaLeft, unityDeltaLeft),
                QuaternionAngleDegrees(predictedDeltaRight, unityDeltaRight));
        }

        private static string[] BuildSourceBoneCandidates(string muscleName, string targetHumanBone)
        {
            return new[]
            {
                InferSourceHumanBoneForMuscle(muscleName, targetHumanBone),
                targetHumanBone,
            }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string InferSourceHumanBoneForMuscle(string muscleName, string targetHumanBone)
        {
            if (muscleName.IndexOf("Upper Leg Twist", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return muscleName.StartsWith("Left ", StringComparison.OrdinalIgnoreCase) ? "LeftUpperLeg" : "RightUpperLeg";
            }
            if (muscleName.IndexOf("Lower Leg Twist", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return muscleName.StartsWith("Left ", StringComparison.OrdinalIgnoreCase) ? "LeftLowerLeg" : "RightLowerLeg";
            }
            if (muscleName.IndexOf("Arm Twist", StringComparison.OrdinalIgnoreCase) >= 0 &&
                muscleName.IndexOf("Forearm", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return muscleName.StartsWith("Left ", StringComparison.OrdinalIgnoreCase) ? "LeftUpperArm" : "RightUpperArm";
            }
            if (muscleName.IndexOf("Forearm Twist", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return muscleName.StartsWith("Left ", StringComparison.OrdinalIgnoreCase) ? "LeftLowerArm" : "RightLowerArm";
            }
            return targetHumanBone;
        }

        private static int CurrentExpectedAvatarAxisForMuscle(string humanBone, string muscleName)
        {
            foreach (var target in BuildCurrentSolverTargets())
            {
                if (!string.Equals(target.HumanBone, humanBone, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (string.Equals(target.XAttribute, muscleName, StringComparison.OrdinalIgnoreCase))
                {
                    return 2;
                }
                if (string.Equals(target.YAttribute, muscleName, StringComparison.OrdinalIgnoreCase))
                {
                    return 1;
                }
                if (string.Equals(target.ZAttribute, muscleName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(target.ExtraZAttribute, muscleName, StringComparison.OrdinalIgnoreCase))
                {
                    return 0;
                }
            }
            return -1;
        }

        private static string AvatarAxisName(int axis) => axis switch
        {
            0 => "X/twist",
            1 => "Y/swing",
            2 => "Z/swing",
            _ => "unknown",
        };

        private static bool RatioNearOne(float value, float tolerance)
        {
            return value > 0f && MathF.Abs(value - 1f) <= tolerance;
        }

        private static bool FindCurrentTargetForProbe(
            string probePath,
            string muscleName,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex,
            out string humanBone,
            out int avatarAxis,
            out SolverAxis? axis)
        {
            humanBone = null;
            avatarAxis = -1;
            axis = null;
            if (string.IsNullOrWhiteSpace(probePath) || string.IsNullOrWhiteSpace(muscleName))
            {
                return false;
            }

            foreach (var target in BuildCurrentSolverTargets())
            {
                if (!TryGetSolverTarget(solverNodes, solverAxes, humanBoneIndex, target, out var targetPath, out var targetAxis) ||
                    !PathsReferToSameBone(targetPath, probePath))
                {
                    continue;
                }

                if (string.Equals(target.XAttribute, muscleName, StringComparison.OrdinalIgnoreCase))
                {
                    humanBone = target.HumanBone;
                    avatarAxis = 2;
                    axis = targetAxis;
                    return true;
                }
                if (string.Equals(target.YAttribute, muscleName, StringComparison.OrdinalIgnoreCase))
                {
                    humanBone = target.HumanBone;
                    avatarAxis = 1;
                    axis = targetAxis;
                    return true;
                }
                if (string.Equals(target.ZAttribute, muscleName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(target.ExtraZAttribute, muscleName, StringComparison.OrdinalIgnoreCase))
                {
                    humanBone = target.HumanBone;
                    avatarAxis = 0;
                    axis = targetAxis;
                    return true;
                }
            }

            return false;
        }

        private static void ResolveLinearityLimitAxis(
            string muscleName,
            string targetHumanBone,
            int defaultAvatarAxis,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex,
            SolverAxis? targetAxis,
            out int targetAvatarAxis,
            out string limitHumanBone,
            out int limitAvatarAxis,
            out SolverAxis? limitAxis)
        {
            // 线性诊断必须区分“写到哪个骨骼轴”和“幅度从哪个 Avatar 轴取”。
            // 例如 Forearm Twist 写到 Hand/LowerArm 一侧，但限位来自 Forearm/LowerLeg 的 X/twist。
            targetAvatarAxis = ResolveVariantTargetAvatarAxis(muscleName, targetHumanBone, defaultAvatarAxis, twistMode: null);
            limitHumanBone = ResolveVariantLimitHumanBone(muscleName, targetHumanBone, twistMode: null);
            limitAvatarAxis = ResolveVariantLimitAvatarAxis(muscleName, targetHumanBone, targetAvatarAxis, twistMode: null);
            limitAxis = targetAxis;
            if (!string.IsNullOrWhiteSpace(limitHumanBone) &&
                !string.Equals(limitHumanBone, targetHumanBone, StringComparison.OrdinalIgnoreCase) &&
                TryGetHumanBonePathAxis(solverNodes, solverAxes, humanBoneIndex, limitHumanBone, out _, out var resolvedLimitAxis))
            {
                limitAxis = resolvedLimitAxis;
            }
        }

        private static bool TryReadProbeRotation(JObject probe, string targetPath, out string probePath, out float[] baseRotation, out float[] rotation)
        {
            probePath = null;
            baseRotation = null;
            rotation = null;
            var normalizedTarget = NormalizeBakePath(targetPath);
            foreach (var item in probe?["rotations"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var path = (string)item["path"];
                var normalizedPath = NormalizeBakePath(path);
                if (!string.Equals(normalizedPath, normalizedTarget, StringComparison.OrdinalIgnoreCase) &&
                    !(normalizedPath?.EndsWith("/" + normalizedTarget, StringComparison.OrdinalIgnoreCase) == true) &&
                    !(normalizedTarget?.EndsWith("/" + normalizedPath, StringComparison.OrdinalIgnoreCase) == true))
                {
                    continue;
                }
                var baseQ = item["baseRotation"];
                var q = item["rotation"];
                if (!TryReadQuaternion(baseQ, out baseRotation) || !TryReadQuaternion(q, out rotation))
                {
                    return false;
                }
                probePath = path;
                return true;
            }
            return false;
        }

        private static bool TryReadQuaternion(JToken value, out float[] rotation)
        {
            rotation = null;
            if (value == null)
            {
                return false;
            }

            rotation = Normalize(new[]
            {
                (float?)value["x"] ?? 0f,
                (float?)value["y"] ?? 0f,
                (float?)value["z"] ?? 0f,
                (float?)value["w"] ?? 1f,
            });
            return rotation.Length == 4;
        }

        private static JObject QuaternionToJObject(float[] rotation)
        {
            var q = Normalize(rotation ?? IdentityQuaternion);
            return new JObject
            {
                ["x"] = q.Length > 0 ? q[0] : 0f,
                ["y"] = q.Length > 1 ? q[1] : 0f,
                ["z"] = q.Length > 2 ? q[2] : 0f,
                ["w"] = q.Length > 3 ? q[3] : 1f,
            };
        }

        private static JObject BuildDecodedDirectRotationToInternalAvatarPoseComparison(JObject unityResult, string animationAsset)
        {
            var samples = unityResult?["internalAvatarPoseTimeline"]?.OfType<JObject>()
                .Where(x => string.Equals((string)x["status"], "ok", StringComparison.OrdinalIgnoreCase))
                .ToArray() ?? Array.Empty<JObject>();
            if (samples.Length == 0 || string.IsNullOrWhiteSpace(animationAsset) || !File.Exists(animationAsset))
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["reason"] = samples.Length == 0 ? "internal_avatar_pose_timeline_missing" : "animation_asset_missing",
                    ["animationAsset"] = animationAsset,
                    ["timelineSampleCount"] = samples.Length,
                };
            }

            var animation = JObject.Parse(File.ReadAllText(animationAsset));
            var directRotations = LoadDecodedRotationTracks(animation["decoded"]?["rotations"] as JArray);
            if (directRotations.Count == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["reason"] = "decoded_rotation_tracks_empty",
                    ["animationAsset"] = animationAsset,
                    ["timelineSampleCount"] = samples.Length,
                };
            }

            var accumulators = new Dictionary<string, TimelineTrackAccumulator>(StringComparer.OrdinalIgnoreCase);
            var missingDirectTrack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var usedSampleCount = 0;
            foreach (var sample in samples)
            {
                var time = (float?)sample["time"] ?? 0f;
                var jointPaths = sample["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
                var values = sample["values"]?.Values<float>().ToArray() ?? Array.Empty<float>();
                var used = false;
                for (var i = 0; i < jointPaths.Length; i++)
                {
                    var unityPath = NormalizeBakePath(jointPaths[i]);
                    if (!TryGetDecodedRotationTrack(directRotations, unityPath, out var directPath, out var track))
                    {
                        if (IsBodyBone(unityPath))
                        {
                            missingDirectTrack.Add(unityPath);
                        }
                        continue;
                    }

                    var offset = i * 7;
                    if (offset + 6 >= values.Length)
                    {
                        continue;
                    }

                    var unityRotation = Normalize(new[]
                    {
                        values[offset + 3],
                        values[offset + 4],
                        values[offset + 5],
                        values[offset + 6],
                    });
                    var decodedRotation = SampleDecodedRotation(track, time);
                    var error = QuaternionAngleDegrees(unityRotation, decodedRotation);
                    if (!accumulators.TryGetValue(unityPath, out var accumulator))
                    {
                        accumulator = new TimelineTrackAccumulator(unityPath, directPath, -1);
                        accumulators[unityPath] = accumulator;
                    }
                    accumulator.Add(error);
                    used = true;
                }

                if (used)
                {
                    usedSampleCount++;
                }
            }

            var rows = accumulators.Values
                .Select(x => x.ToRow())
                .OrderByDescending(x => x.MaxRotationErrorDegrees)
                .ThenBy(x => x.UnityPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var bodyRows = rows.Where(x => IsBodyBone(x.UnityPath)).ToArray();
            return new JObject
            {
                ["status"] = rows.Length == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: compares decoded AnimationClip rotation tracks against Unity GetInternalAvatarPose local rotations. Low error means that specific track can be used as deterministic direct TRS instead of the experimental Humanoid muscle solver.",
                ["animationAsset"] = animationAsset,
                ["timelineSampleCount"] = samples.Length,
                ["usedTimelineSampleCount"] = usedSampleCount,
                ["decodedRotationTrackCount"] = directRotations.Count,
                ["matchedTrackCount"] = rows.Length,
                ["matchedBodyTrackCount"] = bodyRows.Length,
                ["missingBodyDirectTrackCount"] = missingDirectTrack.Count,
                ["missingBodyDirectTracks"] = new JArray(missingDirectTrack.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(128)),
                ["maxDegrees"] = rows.Length == 0 ? 0 : rows[0].MaxRotationErrorDegrees,
                ["avgTrackMaxDegrees"] = rows.Length == 0 ? 0 : rows.Average(x => x.MaxRotationErrorDegrees),
                ["bodyMaxDegrees"] = bodyRows.Length == 0 ? 0 : bodyRows.Max(x => x.MaxRotationErrorDegrees),
                ["bodyAvgTrackMaxDegrees"] = bodyRows.Length == 0 ? 0 : bodyRows.Average(x => x.MaxRotationErrorDegrees),
                ["bodyGroupError"] = BuildBodyGroupErrorSummary(rows),
                ["topErrors"] = ToJsonRows(rows.Take(64)),
            };
        }

        private static JObject BuildSolverResidualStability(
            JObject[] samples,
            Dictionary<string, List<FloatKey>> curves,
            JObject solver,
            JArray solverNodes,
            JArray solverAxes,
            JArray humanBoneIndex,
            JObject[] gltfNodes,
            Dictionary<string, int> nodePathToIndex)
        {
            var variant = new SolverVariant(
                "current_swing_twist",
                "Current preview formula residual stability.",
                BuildCurrentSolverTargets(),
                "swing_twist");
            var rows = new List<SolverResidualRow>();
            var gltfParents = BuildChildToParent(gltfNodes ?? Array.Empty<JObject>());
            foreach (var target in variant.Targets)
            {
                if (!TryGetSolverTarget(solverNodes, solverAxes, humanBoneIndex, target, out var targetPath, out var axis))
                {
                    continue;
                }

                var pairs = new List<(float[] Unity, float[] Predicted)>();
                foreach (var sample in samples)
                {
                    var time = (float?)sample["time"] ?? 0f;
                    var jointPaths = sample["jointPaths"]?.Values<string>().ToArray() ?? Array.Empty<string>();
                    var values = sample["values"]?.Values<float>().ToArray() ?? Array.Empty<float>();
                    if (!TryFindJointIndex(jointPaths, targetPath, out var jointIndex))
                    {
                        continue;
                    }
                    var offset = jointIndex * 7;
                    if (offset + 6 >= values.Length)
                    {
                        continue;
                    }

                    var unityRotation = Normalize(new[]
                    {
                        values[offset + 3],
                        values[offset + 4],
                        values[offset + 5],
                        values[offset + 6],
                    });
                    var predicted = BuildVariantSolverRotation(curves, target, axis, time, variant, solver, solverNodes, humanBoneIndex, targetPath, gltfNodes, nodePathToIndex);
                    pairs.Add((unityRotation, predicted));
                }

                if (pairs.Count == 0)
                {
                    continue;
                }

                var first = pairs[0];
                var leftCorrection = Multiply(first.Unity, Inverse(first.Predicted));
                var rightCorrection = Multiply(Inverse(first.Predicted), first.Unity);
                var rawErrors = pairs
                    .Select(x => QuaternionAngleDegrees(x.Unity, x.Predicted))
                    .ToArray();
                var leftErrors = pairs
                    .Select(x => QuaternionAngleDegrees(x.Unity, Multiply(leftCorrection, x.Predicted)))
                    .ToArray();
                var rightErrors = pairs
                    .Select(x => QuaternionAngleDegrees(x.Unity, Multiply(x.Predicted, rightCorrection)))
                    .ToArray();
                var leftDynamicResidualAxis = BuildDynamicResidualAxisSummary(pairs, leftCorrection, left: true);
                var rightDynamicResidualAxis = BuildDynamicResidualAxisSummary(pairs, rightCorrection, left: false);
                var candidates = BuildResidualCorrectionCandidates(targetPath, axis, solver, solverNodes, humanBoneIndex, target.HumanBone, gltfNodes, gltfParents, nodePathToIndex);
                var nearestLeft = FindNearestCorrectionCandidate(leftCorrection, candidates);
                var nearestRight = FindNearestCorrectionCandidate(rightCorrection, candidates);
                var leftCandidateGaps = BuildCorrectionCandidateGaps(leftCorrection, candidates);
                var rightCandidateGaps = BuildCorrectionCandidateGaps(rightCorrection, candidates);

                rows.Add(new SolverResidualRow(
                    targetPath,
                    pairs.Count,
                    rawErrors.Max(),
                    rawErrors.Average(),
                    leftCorrection,
                    QuaternionAngleDegrees(IdentityQuaternion, leftCorrection),
                    leftErrors.Max(),
                    leftErrors.Average(),
                    rightCorrection,
                    QuaternionAngleDegrees(IdentityQuaternion, rightCorrection),
                    rightErrors.Max(),
                    rightErrors.Average(),
                    leftDynamicResidualAxis,
                    rightDynamicResidualAxis,
                    nearestLeft.Name,
                    nearestLeft.AngleDegrees,
                    nearestRight.Name,
                    nearestRight.AngleDegrees,
                    candidates,
                    leftCandidateGaps,
                    rightCandidateGaps));
            }

            var ordered = rows
                .OrderByDescending(x => x.LeftCorrectedMaxDegrees)
                .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var leftTrackRows = ordered
                .Select(x => new TrackCompareRow(x.Path, x.Path, -1, x.SampleCount, x.LeftCorrectedMaxDegrees, x.LeftCorrectedAvgDegrees, IsBodyBone(x.Path)))
                .ToArray();
            var rightTrackRows = ordered
                .Select(x => new TrackCompareRow(x.Path, x.Path, -1, x.SampleCount, x.RightCorrectedMaxDegrees, x.RightCorrectedAvgDegrees, IsBodyBone(x.Path)))
                .OrderByDescending(x => x.MaxRotationErrorDegrees)
                .ThenBy(x => x.UnityPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var pairedLimbCorrectionSymmetry = BuildPairedLimbCorrectionSymmetrySummary(ordered);
            return new JObject
            {
                ["status"] = ordered.Length == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: takes the first-frame residual between Unity internal pose and the current offline solver as a constant per-bone correction, then measures the remaining timeline error. Low corrected error means the solver mostly misses a stable Avatar/rest-pose offset; high corrected error means the dynamic formula is still wrong.",
                ["matchedTrackCount"] = ordered.Length,
                ["forearmCorrectionSymmetry"] = BuildForearmCorrectionSymmetrySummary(ordered),
                ["focusedLimbCorrectionSpace"] = BuildFocusedLimbCorrectionSpaceSummary(ordered),
                ["pairedLimbCorrectionSymmetry"] = pairedLimbCorrectionSymmetry,
                ["armLegCorrectionPattern"] = BuildArmLegCorrectionPatternSummary(pairedLimbCorrectionSymmetry),
                ["leftCorrection"] = new JObject
                {
                    ["maxResidualDegrees"] = leftTrackRows.Length == 0 ? 0 : leftTrackRows.Max(x => x.MaxRotationErrorDegrees),
                    ["avgTrackMaxResidualDegrees"] = leftTrackRows.Length == 0 ? 0 : leftTrackRows.Average(x => x.MaxRotationErrorDegrees),
                    ["bodyGroupResidual"] = BuildBodyGroupErrorSummary(leftTrackRows),
                    ["candidateGap"] = BuildLearnedCorrectionCandidateGapSummary(ordered, left: true),
                    ["candidateFitAll"] = BuildLearnedCorrectionCandidateFitSummary(ordered, left: true),
                },
                ["rightCorrection"] = new JObject
                {
                    ["maxResidualDegrees"] = rightTrackRows.Length == 0 ? 0 : rightTrackRows.Max(x => x.MaxRotationErrorDegrees),
                    ["avgTrackMaxResidualDegrees"] = rightTrackRows.Length == 0 ? 0 : rightTrackRows.Average(x => x.MaxRotationErrorDegrees),
                    ["bodyGroupResidual"] = BuildBodyGroupErrorSummary(rightTrackRows),
                    ["candidateGap"] = BuildLearnedCorrectionCandidateGapSummary(ordered, left: false),
                    ["candidateFitAll"] = BuildLearnedCorrectionCandidateFitSummary(ordered, left: false),
                },
                ["topResiduals"] = ToJsonRows(ordered.Take(32)),
            };
        }

        private static JObject BuildFocusedLimbCorrectionSpaceSummary(SolverResidualRow[] rows)
        {
            var focus = new[]
            {
                ("upperArm", "Arm", " L UpperArm", " R UpperArm"),
                ("forearm", "Arm", " L Forearm", " R Forearm"),
                ("hand", "Arm", " L Hand", " R Hand"),
                ("upperLeg", "Leg", " L Thigh", " R Thigh"),
                ("lowerLeg", "Leg", " L Calf", " R Calf"),
                ("foot", "Leg", " L Foot", " R Foot"),
                ("toe", "Leg", " L Toe0", " R Toe0"),
            };

            var result = new JArray();
            foreach (var item in focus)
            {
                var left = FindResidualRowByToken(rows, item.Item3);
                var right = FindResidualRowByToken(rows, item.Item4);
                if (left == null && right == null)
                {
                    continue;
                }

                result.Add(new JObject
                {
                    ["pair"] = item.Item1,
                    ["group"] = item.Item2,
                    ["status"] = left == null || right == null ? "partial" : "ok",
                    ["left"] = BuildFocusedLimbSideCorrectionSummary(left, "left"),
                    ["right"] = BuildFocusedLimbSideCorrectionSummary(right, "right"),
                    ["leftToRightMirror"] = left == null || right == null ? null : new JObject
                    {
                        ["leftCorrectionBestMirror"] = BuildForearmMirrorCorrectionSummary(left.LeftCorrection, right.LeftCorrection)["bestCandidate"],
                        ["leftCorrectionBestMirrorGapDegrees"] = BuildForearmMirrorCorrectionSummary(left.LeftCorrection, right.LeftCorrection)["bestGapDegrees"],
                        ["rightCorrectionBestMirror"] = BuildForearmMirrorCorrectionSummary(left.RightCorrection, right.RightCorrection)["bestCandidate"],
                        ["rightCorrectionBestMirrorGapDegrees"] = BuildForearmMirrorCorrectionSummary(left.RightCorrection, right.RightCorrection)["bestGapDegrees"],
                    },
                    ["interpretation"] = BuildFocusedLimbPairInterpretation(left, right),
                });
            }

            return new JObject
            {
                ["status"] = result.Count == 0 ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: focuses the learned first-frame correction on arms and legs, then lists the closest deterministic rest/avatar/preQ/postQ candidate per side. It is meant to answer which Unity pose space is missing before changing the production solver.",
                ["thresholds"] = new JObject
                {
                    ["candidateReadyGapDegrees"] = 10.0,
                    ["mirrorReadyGapDegrees"] = 15.0,
                    ["dynamicResidualReadyDegrees"] = 5.0,
                },
                ["pairs"] = result,
            };
        }

        private static JObject BuildFocusedLimbSideCorrectionSummary(SolverResidualRow row, string side)
        {
            if (row == null)
            {
                return new JObject
                {
                    ["status"] = "missing",
                };
            }

            var leftMode = BuildFocusedLimbCorrectionModeSummary(row, left: true);
            var rightMode = BuildFocusedLimbCorrectionModeSummary(row, left: false);
            var leftMax = (float?)leftMode["correctedMaxDegrees"] ?? float.MaxValue;
            var rightMax = (float?)rightMode["correctedMaxDegrees"] ?? float.MaxValue;
            var bestMode = leftMax <= rightMax ? leftMode : rightMode;

            return new JObject
            {
                ["status"] = "ok",
                ["side"] = side,
                ["path"] = row.Path,
                ["rawMaxDegrees"] = row.RawMaxDegrees,
                ["bestApplyMode"] = leftMax <= rightMax ? "leftMultiply" : "rightMultiply",
                ["bestCorrectedMaxDegrees"] = (float?)bestMode["correctedMaxDegrees"] ?? 0f,
                ["bestNearestCandidate"] = (string)bestMode["nearestCandidate"],
                ["bestNearestCandidateClass"] = (string)bestMode["nearestCandidateClass"],
                ["bestNearestCandidateGapDegrees"] = (float?)bestMode["nearestCandidateGapDegrees"] ?? 0f,
                ["leftMultiply"] = leftMode,
                ["rightMultiply"] = rightMode,
            };
        }

        private static JObject BuildFocusedLimbCorrectionModeSummary(SolverResidualRow row, bool left)
        {
            var gaps = left ? row.LeftCorrectionCandidateGaps : row.RightCorrectionCandidateGaps;
            var nearest = left ? row.NearestLeftCorrectionSource : row.NearestRightCorrectionSource;
            var nearestGap = left ? row.NearestLeftCorrectionSourceErrorDegrees : row.NearestRightCorrectionSourceErrorDegrees;
            return new JObject
            {
                ["applyMode"] = left ? "leftMultiply" : "rightMultiply",
                ["learnedCorrectionAngleDegrees"] = left ? row.LeftCorrectionAngleDegrees : row.RightCorrectionAngleDegrees,
                ["correctedMaxDegrees"] = left ? row.LeftCorrectedMaxDegrees : row.RightCorrectedMaxDegrees,
                ["correctedAvgDegrees"] = left ? row.LeftCorrectedAvgDegrees : row.RightCorrectedAvgDegrees,
                ["nearestCandidate"] = nearest,
                ["nearestCandidateClass"] = ClassifyCorrectionCandidate(nearest),
                ["nearestCandidateGapDegrees"] = nearestGap,
                ["dynamicResidualAxis"] = left ? row.LeftDynamicResidualAxis : row.RightDynamicResidualAxis,
                ["topCandidateGaps"] = BuildTopCorrectionCandidateGaps(gaps, 10),
            };
        }

        private static JArray BuildTopCorrectionCandidateGaps(Dictionary<string, float> gaps, int take)
        {
            var result = new JArray();
            if (gaps == null || gaps.Count == 0)
            {
                return result;
            }

            foreach (var pair in gaps
                .OrderBy(x => x.Value)
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, take)))
            {
                result.Add(new JObject
                {
                    ["candidate"] = pair.Key,
                    ["candidateClass"] = ClassifyCorrectionCandidate(pair.Key),
                    ["gapDegrees"] = pair.Value,
                });
            }
            return result;
        }

        private static string BuildFocusedLimbPairInterpretation(SolverResidualRow left, SolverResidualRow right)
        {
            if (left == null || right == null)
            {
                return "partial_tracks";
            }

            var leftGap = Math.Min(left.NearestLeftCorrectionSourceErrorDegrees, left.NearestRightCorrectionSourceErrorDegrees);
            var rightGap = Math.Min(right.NearestLeftCorrectionSourceErrorDegrees, right.NearestRightCorrectionSourceErrorDegrees);
            var mirrorLeft = (float?)BuildForearmMirrorCorrectionSummary(left.LeftCorrection, right.LeftCorrection)["bestGapDegrees"] ?? float.MaxValue;
            var mirrorRight = (float?)BuildForearmMirrorCorrectionSummary(left.RightCorrection, right.RightCorrection)["bestGapDegrees"] ?? float.MaxValue;
            var correctedMax = Math.Max(
                Math.Min(left.LeftCorrectedMaxDegrees, left.RightCorrectedMaxDegrees),
                Math.Min(right.LeftCorrectedMaxDegrees, right.RightCorrectedMaxDegrees));

            if (Math.Max(leftGap, rightGap) <= 10f && correctedMax <= 5f)
            {
                return "stored_candidate_and_static_correction_ready";
            }

            if (Math.Max(leftGap, rightGap) <= 10f)
            {
                return "stored_candidate_close_but_dynamic_formula_unresolved";
            }

            if (Math.Max(mirrorLeft, mirrorRight) <= 15f)
            {
                return "mirror_space_likely_but_source_candidate_missing";
            }

            return "unresolved_space_or_dynamic_formula";
        }

        private static string ClassifyCorrectionCandidate(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "none";
            }

            if (name.StartsWith("mirror", StringComparison.OrdinalIgnoreCase) || name.Contains("::mirror", StringComparison.OrdinalIgnoreCase))
            {
                return "mirror";
            }

            if (name.Equals("identity", StringComparison.OrdinalIgnoreCase))
            {
                return "identity";
            }

            if (IsParentChainCandidate(name))
            {
                return "parentChain";
            }

            if (name.Contains("localRest", StringComparison.OrdinalIgnoreCase))
            {
                return "gltfLocalRest";
            }

            if (name.Contains("parentRest", StringComparison.OrdinalIgnoreCase))
            {
                return "gltfParentRest";
            }

            if (name.Contains("rest", StringComparison.OrdinalIgnoreCase))
            {
                return "gltfRest";
            }

            if (name.Contains("Pose", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("humanSkeleton", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("avatarSkeleton", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("avatarDefault", StringComparison.OrdinalIgnoreCase))
            {
                return "avatarPose";
            }

            if (name.Contains("preQ", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("postQ", StringComparison.OrdinalIgnoreCase))
            {
                return "axisPrePost";
            }

            return "other";
        }

        private static JObject BuildForearmCorrectionSymmetrySummary(SolverResidualRow[] rows)
        {
            var left = FindForearmResidualRow(rows, "Left");
            var right = FindForearmResidualRow(rows, "Right");
            if (left == null || right == null)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["hasLeftForearm"] = left != null,
                    ["hasRightForearm"] = right != null,
                };
            }

            var leftToRightLeftCorrection = QuaternionAngleDegrees(left.LeftCorrection, right.LeftCorrection);
            var leftToRightRightCorrection = QuaternionAngleDegrees(left.RightCorrection, right.RightCorrection);
            var inversePairLeftCorrection = QuaternionAngleDegrees(left.LeftCorrection, Inverse(right.LeftCorrection));
            var inversePairRightCorrection = QuaternionAngleDegrees(left.RightCorrection, Inverse(right.RightCorrection));
            var leftComposeMirrorSummary = BuildForearmMirrorCorrectionSummary(left.LeftCorrection, right.LeftCorrection);
            var rightComposeMirrorSummary = BuildForearmMirrorCorrectionSummary(left.RightCorrection, right.RightCorrection);

            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: compares learned first-frame corrections for left/right forearms. Small same-side, inverse-pair, or mirror gap means both forearms may share one symmetric space transform; large gap means each side likely needs side-specific Avatar/rest-chain handling.",
                ["leftPath"] = left.Path,
                ["rightPath"] = right.Path,
                ["leftCorrectionAngleDegrees"] = left.LeftCorrectionAngleDegrees,
                ["rightCorrectionAngleDegrees"] = right.LeftCorrectionAngleDegrees,
                ["leftCorrectedMaxDegrees"] = left.LeftCorrectedMaxDegrees,
                ["rightCorrectedMaxDegrees"] = right.LeftCorrectedMaxDegrees,
                ["leftNearestCandidate"] = left.NearestLeftCorrectionSource,
                ["rightNearestCandidate"] = right.NearestLeftCorrectionSource,
                ["leftNearestCandidateGapDegrees"] = left.NearestLeftCorrectionSourceErrorDegrees,
                ["rightNearestCandidateGapDegrees"] = right.NearestLeftCorrectionSourceErrorDegrees,
                ["leftVsRightLeftCorrectionDegrees"] = leftToRightLeftCorrection,
                ["leftVsRightRightCorrectionDegrees"] = leftToRightRightCorrection,
                ["leftVsInverseRightLeftCorrectionDegrees"] = inversePairLeftCorrection,
                ["leftVsInverseRightRightCorrectionDegrees"] = inversePairRightCorrection,
                ["leftComposeMirror"] = leftComposeMirrorSummary,
                ["rightComposeMirror"] = rightComposeMirrorSummary,
                ["leftCorrectionQuaternion"] = ToJArray(left.LeftCorrection),
                ["rightCorrectionQuaternion"] = ToJArray(right.LeftCorrection),
            };
        }

        private static JObject BuildDynamicResidualAxisSummary(
            List<(float[] Unity, float[] Predicted)> pairs,
            float[] correction,
            bool left)
        {
            if (pairs == null || pairs.Count == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                };
            }

            var weighted = new[] { 0.0, 0.0, 0.0 };
            var totalAngle = 0.0;
            var maxAngle = -1f;
            float[] maxAxis = { 0f, 0f, 0f };
            foreach (var pair in pairs)
            {
                var corrected = left
                    ? Multiply(correction, pair.Predicted)
                    : Multiply(pair.Predicted, correction);
                var residual = Normalize(Multiply(pair.Unity, Inverse(corrected)));
                var axisAngle = ToAxisAngleDegrees(residual);
                var angle = axisAngle.AngleDegrees;
                if (angle > maxAngle)
                {
                    maxAngle = angle;
                    maxAxis = axisAngle.Axis;
                }

                weighted[0] += axisAngle.Axis[0] * angle;
                weighted[1] += axisAngle.Axis[1] * angle;
                weighted[2] += axisAngle.Axis[2] * angle;
                totalAngle += angle;
            }

            var averageAxis = NormalizeVector3(weighted);
            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: after first-frame constant correction, residual = Unity * inverse(correctedPrediction). Dominant local axis helps decide whether remaining error behaves like twist(X) or swing(Y/Z), instead of another static rest offset.",
                ["sampleCount"] = pairs.Count,
                ["maxResidualDegrees"] = maxAngle < 0 ? 0 : maxAngle,
                ["maxResidualAxis"] = ToJArray(maxAxis),
                ["weightedAverageAxis"] = ToJArray(averageAxis),
                ["dominantAxis"] = DominantAxisName(averageAxis),
                ["axisWeightDegrees"] = totalAngle,
            };
        }

        private static (float[] Axis, float AngleDegrees) ToAxisAngleDegrees(float[] quaternion)
        {
            var q = Normalize(quaternion);
            if (q[3] < 0)
            {
                q = new[] { -q[0], -q[1], -q[2], -q[3] };
            }

            var w = Math.Clamp(q[3], -1f, 1f);
            var angle = (float)(2.0 * Math.Acos(w));
            var sinHalf = MathF.Sin(angle * 0.5f);
            if (Math.Abs(sinHalf) < 0.000001f)
            {
                return (new[] { 0f, 0f, 0f }, 0f);
            }

            return (new[] { q[0] / sinHalf, q[1] / sinHalf, q[2] / sinHalf }, angle * 180f / MathF.PI);
        }

        private static float[] NormalizeVector3(double[] value)
        {
            if (value == null || value.Length < 3)
            {
                return new[] { 0f, 0f, 0f };
            }

            var length = Math.Sqrt(value[0] * value[0] + value[1] * value[1] + value[2] * value[2]);
            if (length < 0.0000001)
            {
                return new[] { 0f, 0f, 0f };
            }

            return new[] { (float)(value[0] / length), (float)(value[1] / length), (float)(value[2] / length) };
        }

        private static string DominantAxisName(float[] axis)
        {
            if (axis == null || axis.Length < 3)
            {
                return "none";
            }

            var x = Math.Abs(axis[0]);
            var y = Math.Abs(axis[1]);
            var z = Math.Abs(axis[2]);
            if (x < 0.0001f && y < 0.0001f && z < 0.0001f)
            {
                return "none";
            }

            if (x >= y && x >= z)
            {
                return axis[0] >= 0 ? "+X" : "-X";
            }
            if (y >= x && y >= z)
            {
                return axis[1] >= 0 ? "+Y" : "-Y";
            }
            return axis[2] >= 0 ? "+Z" : "-Z";
        }

        private static JObject BuildForearmMirrorCorrectionSummary(float[] leftCorrection, float[] rightCorrection)
        {
            var candidates = BuildQuaternionMirrorCandidates(leftCorrection)
                .Select(x => new CorrectionCandidateMatch(x.Key, QuaternionAngleDegrees(x.Value, rightCorrection)))
                .OrderBy(x => x.AngleDegrees)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var best = candidates.FirstOrDefault();
            return new JObject
            {
                ["status"] = best == null ? "not_available" : "ok",
                ["rule"] = "Diagnostic only: applies simple quaternion component mirror patterns to the left correction and measures how close each result is to the right correction. A stable best pattern across models can guide the real Avatar-side transform formula.",
                ["bestCandidate"] = best?.Name,
                ["bestGapDegrees"] = best?.AngleDegrees ?? 0f,
                ["candidates"] = new JArray(candidates.Select(x => new JObject
                {
                    ["candidate"] = x.Name,
                    ["gapDegrees"] = x.AngleDegrees,
                })),
            };
        }

        private static Dictionary<string, float[]> BuildQuaternionMirrorCandidates(float[] source)
        {
            var q = Normalize(source);
            var result = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["same"] = q,
                ["inverse"] = Inverse(q),
            };

            // 这些符号组合只是诊断镜像规律，不代表最终导出公式。
            AddMirrorCandidate(result, "mirrorX", q, -1, 1, 1);
            AddMirrorCandidate(result, "mirrorY", q, 1, -1, 1);
            AddMirrorCandidate(result, "mirrorZ", q, 1, 1, -1);
            AddMirrorCandidate(result, "mirrorXY", q, -1, -1, 1);
            AddMirrorCandidate(result, "mirrorXZ", q, -1, 1, -1);
            AddMirrorCandidate(result, "mirrorYZ", q, 1, -1, -1);
            AddMirrorCandidate(result, "mirrorXYZ", q, -1, -1, -1);
            return result;
        }

        private static void AddMirrorCandidate(Dictionary<string, float[]> result, string name, float[] q, int xSign, int ySign, int zSign)
        {
            var mirrored = Normalize(new[] { q[0] * xSign, q[1] * ySign, q[2] * zSign, q[3] });
            result[name] = mirrored;
            result[$"{name}Inverse"] = Inverse(mirrored);
        }

        private static float[] MirrorQuaternion(float[] rotation, string mirrorName)
        {
            var q = Normalize(rotation);
            return mirrorName switch
            {
                "mirrorX" => Normalize(new[] { -q[0], q[1], q[2], q[3] }),
                "mirrorY" => Normalize(new[] { q[0], -q[1], q[2], q[3] }),
                "mirrorZ" => Normalize(new[] { q[0], q[1], -q[2], q[3] }),
                "mirrorXY" => Normalize(new[] { -q[0], -q[1], q[2], q[3] }),
                "mirrorXZ" => Normalize(new[] { -q[0], q[1], -q[2], q[3] }),
                "mirrorYZ" => Normalize(new[] { q[0], -q[1], -q[2], q[3] }),
                "mirrorXYZ" => Normalize(new[] { -q[0], -q[1], -q[2], q[3] }),
                _ => q,
            };
        }

        private static JArray BuildPairedLimbCorrectionSymmetrySummary(SolverResidualRow[] rows)
        {
            var pairs = new[]
            {
                ("upperArm", " L UpperArm", " R UpperArm"),
                ("forearm", " L Forearm", " R Forearm"),
                ("hand", " L Hand", " R Hand"),
                ("upperLeg", " L Thigh", " R Thigh"),
                ("lowerLeg", " L Calf", " R Calf"),
                ("foot", " L Foot", " R Foot"),
                ("toe", " L Toe0", " R Toe0"),
            };

            var result = new JArray();
            foreach (var pair in pairs)
            {
                var left = FindResidualRowByToken(rows, pair.Item2);
                var right = FindResidualRowByToken(rows, pair.Item3);
                if (left == null || right == null)
                {
                    result.Add(new JObject
                    {
                        ["pair"] = pair.Item1,
                        ["status"] = "not_available",
                        ["hasLeft"] = left != null,
                        ["hasRight"] = right != null,
                    });
                    continue;
                }

                var leftComposeMirrorSummary = BuildForearmMirrorCorrectionSummary(left.LeftCorrection, right.LeftCorrection);
                var rightComposeMirrorSummary = BuildForearmMirrorCorrectionSummary(left.RightCorrection, right.RightCorrection);
                var crossSideMirroredCandidateSummary = BuildCrossSideMirroredCandidateSummary(left, right);
                result.Add(new JObject
                {
                    ["pair"] = pair.Item1,
                    ["status"] = "ok",
                    ["leftPath"] = left.Path,
                    ["rightPath"] = right.Path,
                    ["leftCorrectedMaxDegrees"] = left.LeftCorrectedMaxDegrees,
                    ["rightCorrectedMaxDegrees"] = right.LeftCorrectedMaxDegrees,
                    ["sameSideGapDegrees"] = QuaternionAngleDegrees(left.LeftCorrection, right.LeftCorrection),
                    ["inversePairGapDegrees"] = QuaternionAngleDegrees(left.LeftCorrection, Inverse(right.LeftCorrection)),
                    ["leftComposeBestMirror"] = leftComposeMirrorSummary["bestCandidate"],
                    ["leftComposeBestMirrorGapDegrees"] = leftComposeMirrorSummary["bestGapDegrees"],
                    ["rightComposeBestMirror"] = rightComposeMirrorSummary["bestCandidate"],
                    ["rightComposeBestMirrorGapDegrees"] = rightComposeMirrorSummary["bestGapDegrees"],
                    ["leftNearestCandidate"] = left.NearestLeftCorrectionSource,
                    ["rightNearestCandidate"] = right.NearestLeftCorrectionSource,
                    ["leftNearestCandidateGapDegrees"] = left.NearestLeftCorrectionSourceErrorDegrees,
                    ["rightNearestCandidateGapDegrees"] = right.NearestLeftCorrectionSourceErrorDegrees,
                    ["crossSideMirroredCandidate"] = crossSideMirroredCandidateSummary,
                });
            }

            return result;
        }

        private static JObject BuildCrossSideMirroredCandidateSummary(SolverResidualRow left, SolverResidualRow right)
        {
            if (left == null || right == null)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                };
            }

            var leftModeLeftFromRight = FindBestMirroredCandidate(left.LeftCorrection, right.CorrectionCandidates);
            var leftModeRightFromLeft = FindBestMirroredCandidate(right.LeftCorrection, left.CorrectionCandidates);
            var rightModeLeftFromRight = FindBestMirroredCandidate(left.RightCorrection, right.CorrectionCandidates);
            var rightModeRightFromLeft = FindBestMirroredCandidate(right.RightCorrection, left.CorrectionCandidates);

            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: compares each learned side correction against mirrored correction candidates from the opposite side. Low gap means the missing source may exist on the paired limb but needs side-space mirroring.",
                ["leftCorrectionMode"] = new JObject
                {
                    ["leftFromRight"] = ToJson(leftModeLeftFromRight),
                    ["rightFromLeft"] = ToJson(leftModeRightFromLeft),
                    ["maxBestGapDegrees"] = Math.Max(leftModeLeftFromRight?.AngleDegrees ?? 0f, leftModeRightFromLeft?.AngleDegrees ?? 0f),
                },
                ["rightCorrectionMode"] = new JObject
                {
                    ["leftFromRight"] = ToJson(rightModeLeftFromRight),
                    ["rightFromLeft"] = ToJson(rightModeRightFromLeft),
                    ["maxBestGapDegrees"] = Math.Max(rightModeLeftFromRight?.AngleDegrees ?? 0f, rightModeRightFromLeft?.AngleDegrees ?? 0f),
                },
            };

            static JObject ToJson(CorrectionCandidateMatch match) => new()
            {
                ["candidate"] = match?.Name,
                ["gapDegrees"] = match?.AngleDegrees ?? 0f,
            };
        }

        private static CorrectionCandidateMatch FindBestMirroredCandidate(float[] targetCorrection, Dictionary<string, float[]> sourceCandidates)
        {
            if (targetCorrection == null || sourceCandidates == null || sourceCandidates.Count == 0)
            {
                return null;
            }

            string bestName = null;
            var bestAngle = float.MaxValue;
            foreach (var candidate in sourceCandidates)
            {
                if (string.IsNullOrWhiteSpace(candidate.Key) || candidate.Value == null)
                {
                    continue;
                }

                foreach (var mirrored in BuildQuaternionMirrorCandidates(candidate.Value))
                {
                    var angle = QuaternionAngleDegrees(targetCorrection, mirrored.Value);
                    if (angle < bestAngle)
                    {
                        bestAngle = angle;
                        bestName = $"{candidate.Key}::{mirrored.Key}";
                    }
                }
            }

            return bestName == null ? null : new CorrectionCandidateMatch(bestName, bestAngle);
        }

        private static JObject BuildArmLegCorrectionPatternSummary(JArray pairedRows)
        {
            var rows = pairedRows?.OfType<JObject>()
                .Where(x => string.Equals((string)x["status"], "ok", StringComparison.OrdinalIgnoreCase))
                .ToArray() ?? Array.Empty<JObject>();
            if (rows.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                };
            }

            var armPairs = new HashSet<string>(new[] { "upperArm", "forearm", "hand" }, StringComparer.OrdinalIgnoreCase);
            var legPairs = new HashSet<string>(new[] { "upperLeg", "lowerLeg", "foot", "toe" }, StringComparer.OrdinalIgnoreCase);
            var arm = BuildLimbGroupCorrectionPattern("Arm", rows.Where(x => armPairs.Contains((string)x["pair"])).ToArray());
            var leg = BuildLimbGroupCorrectionPattern("Leg", rows.Where(x => legPairs.Contains((string)x["pair"])).ToArray());

            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: summarizes left/right learned correction symmetry for Arm and Leg pairs. It helps decide whether the missing rest offset looks like a mirror-space issue, a stored metadata candidate, or an unresolved per-bone source.",
                ["thresholds"] = new JObject
                {
                    ["mirrorGapDegrees"] = 15.0,
                    ["nearestCandidateGapDegrees"] = 10.0,
                    ["crossSideMirroredCandidateGapDegrees"] = 10.0,
                },
                ["arm"] = arm,
                ["leg"] = leg,
                ["nextAction"] = BuildArmLegNextAction(arm, leg),
            };
        }

        private static JObject BuildLimbGroupCorrectionPattern(string groupName, JObject[] rows)
        {
            if (rows == null || rows.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["group"] = groupName,
                };
            }

            var mirrorRows = rows
                .Where(x => string.Equals((string)x["leftComposeBestMirror"], "mirrorXY", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals((string)x["rightComposeBestMirror"], "mirrorXY", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var candidateReadyRows = rows
                .Where(x => Math.Max((float?)x["leftNearestCandidateGapDegrees"] ?? float.MaxValue, (float?)x["rightNearestCandidateGapDegrees"] ?? float.MaxValue) <= 10f)
                .ToArray();
            var mirrorReadyRows = rows
                .Where(x => Math.Max((float?)x["leftComposeBestMirrorGapDegrees"] ?? float.MaxValue, (float?)x["rightComposeBestMirrorGapDegrees"] ?? float.MaxValue) <= 15f)
                .ToArray();
            var crossSideReadyRows = rows
                .Where(x => GetBestCrossSideMirroredCandidateGap(x) <= 10f)
                .ToArray();
            var parentChainRows = rows
                .Where(x => IsParentChainCandidate((string)x["leftNearestCandidate"]) || IsParentChainCandidate((string)x["rightNearestCandidate"]))
                .ToArray();
            var worstCandidate = rows
                .OrderByDescending(x => Math.Max((float?)x["leftNearestCandidateGapDegrees"] ?? 0f, (float?)x["rightNearestCandidateGapDegrees"] ?? 0f))
                .First();
            var worstMirror = rows
                .OrderByDescending(x => Math.Max((float?)x["leftComposeBestMirrorGapDegrees"] ?? 0f, (float?)x["rightComposeBestMirrorGapDegrees"] ?? 0f))
                .First();
            var worstCrossSide = rows
                .OrderByDescending(GetBestCrossSideMirroredCandidateGap)
                .First();
            var interpretation = BuildLimbGroupCorrectionInterpretation(rows.Length, candidateReadyRows.Length, mirrorRows.Length, mirrorReadyRows.Length, crossSideReadyRows.Length);

            return new JObject
            {
                ["status"] = "ok",
                ["group"] = groupName,
                ["pairCount"] = rows.Length,
                ["mirrorXYPairCount"] = mirrorRows.Length,
                ["mirrorReadyPairCount"] = mirrorReadyRows.Length,
                ["nearestCandidateReadyPairCount"] = candidateReadyRows.Length,
                ["crossSideMirroredCandidateReadyPairCount"] = crossSideReadyRows.Length,
                ["parentChainNearestPairCount"] = parentChainRows.Length,
                ["maxNearestCandidateGapDegrees"] = rows.Max(x => Math.Max((float?)x["leftNearestCandidateGapDegrees"] ?? 0f, (float?)x["rightNearestCandidateGapDegrees"] ?? 0f)),
                ["maxMirrorGapDegrees"] = rows.Max(x => Math.Max((float?)x["leftComposeBestMirrorGapDegrees"] ?? 0f, (float?)x["rightComposeBestMirrorGapDegrees"] ?? 0f)),
                ["maxCrossSideMirroredCandidateGapDegrees"] = rows.Max(GetBestCrossSideMirroredCandidateGap),
                ["worstNearestCandidatePair"] = (string)worstCandidate["pair"],
                ["worstNearestCandidateLeft"] = (string)worstCandidate["leftNearestCandidate"],
                ["worstNearestCandidateRight"] = (string)worstCandidate["rightNearestCandidate"],
                ["worstMirrorPair"] = (string)worstMirror["pair"],
                ["worstCrossSideMirroredCandidatePair"] = (string)worstCrossSide["pair"],
                ["interpretation"] = interpretation,
                ["pairs"] = new JArray(rows.Select(x => new JObject
                {
                    ["pair"] = (string)x["pair"],
                    ["leftNearestCandidate"] = (string)x["leftNearestCandidate"],
                    ["rightNearestCandidate"] = (string)x["rightNearestCandidate"],
                    ["leftNearestCandidateGapDegrees"] = (float?)x["leftNearestCandidateGapDegrees"] ?? 0f,
                    ["rightNearestCandidateGapDegrees"] = (float?)x["rightNearestCandidateGapDegrees"] ?? 0f,
                    ["leftComposeBestMirror"] = (string)x["leftComposeBestMirror"],
                    ["rightComposeBestMirror"] = (string)x["rightComposeBestMirror"],
                    ["leftComposeBestMirrorGapDegrees"] = (float?)x["leftComposeBestMirrorGapDegrees"] ?? 0f,
                    ["rightComposeBestMirrorGapDegrees"] = (float?)x["rightComposeBestMirrorGapDegrees"] ?? 0f,
                    ["bestCrossSideMirroredCandidateGapDegrees"] = GetBestCrossSideMirroredCandidateGap(x),
                    ["nearestUsesParentChainCandidate"] = IsParentChainCandidate((string)x["leftNearestCandidate"]) || IsParentChainCandidate((string)x["rightNearestCandidate"]),
                })),
            };
        }

        private static bool IsParentChainCandidate(string name)
        {
            return !string.IsNullOrWhiteSpace(name) &&
                (name.Contains("ParentPose", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("ParentLocalPose", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("ParentToChildPose", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("ChildToParentPose", StringComparison.OrdinalIgnoreCase));
        }

        private static string BuildLimbGroupCorrectionInterpretation(int pairCount, int candidateReadyCount, int mirrorPairCount, int mirrorReadyCount, int crossSideReadyCount)
        {
            if (candidateReadyCount == pairCount)
            {
                return "stored_candidate_explains_group";
            }

            if (crossSideReadyCount >= Math.Max(1, pairCount - 1))
            {
                return "cross_side_mirrored_candidate_likely";
            }

            if (mirrorReadyCount >= Math.Max(1, pairCount - 1))
            {
                return "mirror_space_likely_but_candidate_missing";
            }

            return mirrorPairCount >= Math.Max(1, pairCount - 1)
                ? "mirror_space_possible_but_incomplete"
                : "mixed_or_unresolved";
        }

        private static float GetBestCrossSideMirroredCandidateGap(JObject row)
        {
            var cross = row?["crossSideMirroredCandidate"] as JObject;
            if (cross == null)
            {
                return float.MaxValue;
            }

            var leftMode = cross["leftCorrectionMode"] as JObject;
            var rightMode = cross["rightCorrectionMode"] as JObject;
            return Math.Min(
                (float?)leftMode?["maxBestGapDegrees"] ?? float.MaxValue,
                (float?)rightMode?["maxBestGapDegrees"] ?? float.MaxValue);
        }

        private static string BuildArmLegNextAction(JObject arm, JObject leg)
        {
            var armText = (string)arm?["interpretation"] ?? "not_available";
            var legText = (string)leg?["interpretation"] ?? "not_available";
            if (string.Equals(armText, "cross_side_mirrored_candidate_likely", StringComparison.OrdinalIgnoreCase))
            {
                return "Arm 的缺失修正接近对侧镜像候选，下一步可把该候选做成实验 solver 变体并继续用 Unity oracle 验证；验证前不要迁入生产 solver。";
            }

            if (armText.Contains("mirror_space", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(legText, "stored_candidate_explains_group", StringComparison.OrdinalIgnoreCase))
            {
                return "优先追 Arm 的左右镜像 Avatar/rest 空间来源，同时继续拆 Leg 的 upper/lower/foot 分段规则；不要把单一全局 rest 候选迁入生产 solver。";
            }

            if (string.Equals(legText, "stored_candidate_explains_group", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(armText, "stored_candidate_explains_group", StringComparison.OrdinalIgnoreCase))
            {
                return "Leg 已接近可由存量候选解释，下一步集中追 Arm 的缺失空间来源。";
            }

            return "继续扩展 Arm/Leg 的 Unity oracle 和候选元数据；当前摘要不足以证明可迁入生产 solver。";
        }

        private static SolverResidualRow FindForearmResidualRow(IEnumerable<SolverResidualRow> rows, string side)
        {
            var token = string.Equals(side, "Left", StringComparison.OrdinalIgnoreCase)
                ? " L Forearm"
                : " R Forearm";
            return FindResidualRowByToken(rows, token);
        }

        private static SolverResidualRow FindResidualRowByToken(IEnumerable<SolverResidualRow> rows, string token)
        {
            return (rows ?? Array.Empty<SolverResidualRow>())
                .Where(x => x.Path?.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(x => x.Path?.EndsWith(token, StringComparison.OrdinalIgnoreCase) == true ? 0 : 1)
                .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        private static JObject BuildLearnedCorrectionCandidateGapSummary(SolverResidualRow[] rows, bool left)
        {
            var usableRows = (rows ?? Array.Empty<SolverResidualRow>())
                .Where(x => !string.IsNullOrWhiteSpace(left ? x.NearestLeftCorrectionSource : x.NearestRightCorrectionSource))
                .ToArray();
            if (usableRows.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["rule"] = "No nearest correction candidate was available for the learned first-frame correction.",
                };
            }

            var gapRows = usableRows
                .Select(x => new CorrectionGapRow(
                    x.Path,
                    ClassifyBodyGroup(x.Path),
                    left ? x.NearestLeftCorrectionSource : x.NearestRightCorrectionSource,
                    left ? x.NearestLeftCorrectionSourceErrorDegrees : x.NearestRightCorrectionSourceErrorDegrees,
                    left ? x.LeftCorrectionAngleDegrees : x.RightCorrectionAngleDegrees,
                    left ? x.LeftCorrectedMaxDegrees : x.RightCorrectedMaxDegrees,
                    left ? x.LeftCorrectedAvgDegrees : x.RightCorrectedAvgDegrees))
                .OrderByDescending(x => x.CandidateGapDegrees)
                .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var byCandidate = gapRows
                .GroupBy(x => x.Candidate, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var items = g.ToArray();
                    var worst = items.OrderByDescending(x => x.CandidateGapDegrees).First();
                    return new JObject
                    {
                        ["candidate"] = g.Key,
                        ["count"] = items.Length,
                        ["maxGapDegrees"] = items.Max(x => x.CandidateGapDegrees),
                        ["avgGapDegrees"] = items.Average(x => x.CandidateGapDegrees),
                        ["maxCorrectedResidualDegrees"] = items.Max(x => x.CorrectedMaxDegrees),
                        ["bodyGroups"] = new JArray(items
                            .Select(x => x.BodyGroup)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                        ["worstPath"] = worst.Path,
                    };
                })
                .OrderByDescending(x => (float?)x["maxGapDegrees"] ?? 0f)
                .ThenBy(x => (string)x["candidate"], StringComparer.OrdinalIgnoreCase);

            var byBodyGroup = gapRows
                .GroupBy(x => x.BodyGroup, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var items = g.ToArray();
                    var worst = items.OrderByDescending(x => x.CandidateGapDegrees).First();
                    return new JObject
                    {
                        ["bodyGroup"] = g.Key,
                        ["count"] = items.Length,
                        ["maxGapDegrees"] = items.Max(x => x.CandidateGapDegrees),
                        ["avgGapDegrees"] = items.Average(x => x.CandidateGapDegrees),
                        ["maxCorrectedResidualDegrees"] = items.Max(x => x.CorrectedMaxDegrees),
                        ["worstCandidate"] = worst.Candidate,
                        ["worstPath"] = worst.Path,
                    };
                })
                .OrderByDescending(x => (float?)x["maxGapDegrees"] ?? 0f)
                .ThenBy(x => (string)x["bodyGroup"], StringComparer.OrdinalIgnoreCase);

            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: compares the learned first-frame correction against the nearest stored rest/avatar/preQ/postQ correction candidate. Large gap means stored metadata candidates do not explain the stable offset, so a new space transform or source pose is missing.",
                ["trackCount"] = gapRows.Length,
                ["maxGapDegrees"] = gapRows.Max(x => x.CandidateGapDegrees),
                ["avgGapDegrees"] = gapRows.Average(x => x.CandidateGapDegrees),
                ["byCandidate"] = new JArray(byCandidate),
                ["byBodyGroup"] = new JArray(byBodyGroup),
                ["topGaps"] = new JArray(gapRows.Take(24).Select(x => new JObject
                {
                    ["path"] = x.Path,
                    ["bodyGroup"] = x.BodyGroup,
                    ["nearestCandidate"] = x.Candidate,
                    ["candidateGapDegrees"] = x.CandidateGapDegrees,
                    ["learnedCorrectionAngleDegrees"] = x.LearnedCorrectionAngleDegrees,
                    ["correctedMaxResidualDegrees"] = x.CorrectedMaxDegrees,
                    ["correctedAvgResidualDegrees"] = x.CorrectedAvgDegrees,
                })),
            };
        }

        private static Dictionary<string, float> BuildCorrectionCandidateGaps(float[] correction, Dictionary<string, float[]> candidates)
        {
            var result = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            if (correction == null || candidates == null || candidates.Count == 0)
            {
                return result;
            }

            foreach (var pair in candidates)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null)
                {
                    continue;
                }
                result[pair.Key] = QuaternionAngleDegrees(correction, pair.Value);
            }
            return result;
        }

        private static JObject BuildLearnedCorrectionCandidateFitSummary(SolverResidualRow[] rows, bool left)
        {
            var usableRows = (rows ?? Array.Empty<SolverResidualRow>())
                .Where(x => (left ? x.LeftCorrectionCandidateGaps : x.RightCorrectionCandidateGaps)?.Count > 0)
                .ToArray();
            if (usableRows.Length == 0)
            {
                return new JObject
                {
                    ["status"] = "not_available",
                    ["rule"] = "No correction candidate gaps were available.",
                };
            }

            var all = usableRows
                .SelectMany(row => (left ? row.LeftCorrectionCandidateGaps : row.RightCorrectionCandidateGaps)
                    .Select(pair => new
                    {
                        row.Path,
                        BodyGroup = ClassifyBodyGroup(row.Path),
                        Candidate = pair.Key,
                        Gap = pair.Value,
                        CorrectedMax = left ? row.LeftCorrectedMaxDegrees : row.RightCorrectedMaxDegrees,
                    }))
                .ToArray();

            var candidateRows = all
                .GroupBy(x => x.Candidate, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var items = g.ToArray();
                    var worst = items.OrderByDescending(x => x.Gap).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).First();
                    return new JObject
                    {
                        ["candidate"] = g.Key,
                        ["matchedTrackCount"] = items.Length,
                        ["coverage"] = Ratio(items.Length, usableRows.Length),
                        ["maxGapDegrees"] = items.Max(x => x.Gap),
                        ["avgGapDegrees"] = items.Average(x => x.Gap),
                        ["p90GapDegrees"] = Percentile(items.Select(x => x.Gap), 0.90f),
                        ["maxCorrectedResidualDegrees"] = items.Max(x => x.CorrectedMax),
                        ["bodyGroups"] = new JArray(items
                            .Select(x => x.BodyGroup)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                        ["worstBodyGroup"] = worst.BodyGroup,
                        ["worstPath"] = worst.Path,
                    };
                })
                .OrderBy(x => Math.Abs(((float?)x["coverage"] ?? 0f) - 1f) > 0.0001f ? 1 : 0)
                .ThenBy(x => (float?)x["avgGapDegrees"] ?? float.MaxValue)
                .ThenBy(x => (float?)x["maxGapDegrees"] ?? float.MaxValue)
                .ThenBy(x => (string)x["candidate"], StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var bestByBodyGroup = all
                .GroupBy(x => x.BodyGroup, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var groupItems = group.ToArray();
                    var ranked = groupItems
                        .GroupBy(x => x.Candidate, StringComparer.OrdinalIgnoreCase)
                        .Select(g =>
                        {
                            var items = g.ToArray();
                            var worst = items.OrderByDescending(x => x.Gap).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).First();
                            return new JObject
                            {
                                ["candidate"] = g.Key,
                                ["matchedTrackCount"] = items.Length,
                                ["coverageInGroup"] = Ratio(items.Length, groupItems.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()),
                                ["maxGapDegrees"] = items.Max(x => x.Gap),
                                ["avgGapDegrees"] = items.Average(x => x.Gap),
                                ["p90GapDegrees"] = Percentile(items.Select(x => x.Gap), 0.90f),
                                ["worstPath"] = worst.Path,
                            };
                        })
                        .OrderBy(x => Math.Abs(((float?)x["coverageInGroup"] ?? 0f) - 1f) > 0.0001f ? 1 : 0)
                        .ThenBy(x => (float?)x["avgGapDegrees"] ?? float.MaxValue)
                        .ThenBy(x => (float?)x["maxGapDegrees"] ?? float.MaxValue)
                        .ThenBy(x => (string)x["candidate"], StringComparer.OrdinalIgnoreCase)
                        .Take(8);

                    return new JObject
                    {
                        ["bodyGroup"] = group.Key,
                        ["trackCount"] = groupItems.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                        ["topCandidates"] = new JArray(ranked),
                    };
                })
                .OrderBy(x => (string)x["bodyGroup"], StringComparer.OrdinalIgnoreCase);

            return new JObject
            {
                ["status"] = "ok",
                ["rule"] = "Diagnostic only: ranks every deterministic rest/avatar/preQ/postQ correction candidate against the learned first-frame correction on all comparable bones. A production formula should have broad coverage and low avg/max gap across multiple models.",
                ["trackCount"] = usableRows.Length,
                ["candidateCount"] = candidateRows.Length,
                ["topCandidates"] = new JArray(candidateRows.Take(24)),
                ["bestFullCoverageCandidates"] = new JArray(candidateRows
                    .Where(x => Math.Abs(((float?)x["coverage"] ?? 0f) - 1f) <= 0.0001f)
                    .Take(12)),
                ["bestByBodyGroup"] = new JArray(bestByBodyGroup),
            };
        }

        private static double Ratio(long numerator, long denominator)
        {
            return denominator <= 0 ? 0 : Math.Round((double)numerator / denominator, 6);
        }

        private static float Percentile(IEnumerable<float> values, float percentile)
        {
            var ordered = (values ?? Array.Empty<float>())
                .OrderBy(x => x)
                .ToArray();
            if (ordered.Length == 0)
            {
                return 0;
            }
            var clamped = Math.Clamp(percentile, 0f, 1f);
            var index = (int)MathF.Ceiling((ordered.Length - 1) * clamped);
            return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
        }

        private static string TryFindSiblingRequest(string resultPath)
        {
            var directory = Path.GetDirectoryName(resultPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return null;
            }
            var sibling = Path.Combine(directory, "unity_bake_request.json");
            return File.Exists(sibling) ? sibling : null;
        }

        private static SolverVariant[] BuildSolverVariants()
        {
            var current = BuildCurrentSolverTargets();
            return new[]
            {
                new SolverVariant("current_swing_twist", "Current preview formula: attribute X/Y/Z mapped to Avatar Z/Y/X, pre * swing(YZ) * twist(X) * inverse(post), distal twist mapping.", current, "swing_twist"),
                new SolverVariant("attribute_xyz_swing_twist", "Diagnostic axis-order test: map attribute X/Y/Z directly to Avatar X/Y/Z, then use swing(YZ) * twist(X).", current, "swing_twist", AxisOrder: "attribute_xyz"),
                new SolverVariant("attribute_xzy_swing_twist", "Diagnostic axis-order test: map attribute X/Y/Z to Avatar X/Z/Y.", current, "swing_twist", AxisOrder: "attribute_xzy"),
                new SolverVariant("attribute_yxz_swing_twist", "Diagnostic axis-order test: map attribute X/Y/Z to Avatar Y/X/Z.", current, "swing_twist", AxisOrder: "attribute_yxz"),
                new SolverVariant("attribute_yzx_swing_twist", "Diagnostic axis-order test: map attribute X/Y/Z to Avatar Y/Z/X.", current, "swing_twist", AxisOrder: "attribute_yzx"),
                new SolverVariant("attribute_zxy_swing_twist", "Diagnostic axis-order test: map attribute X/Y/Z to Avatar Z/X/Y.", current, "swing_twist", AxisOrder: "attribute_zxy"),
                new SolverVariant("current_twist_swing", "Same target mapping, but twist(X) before swing(YZ). A consistently worse result means compose order is not the main issue.", current, "twist_swing"),
                new SolverVariant("current_euler_zyx", "Same target mapping, but treat Avatar axes as ZYX Euler. Useful to rule out simple Euler interpretation.", current, "euler_zyx"),
                new SolverVariant("current_mirror_xy_left", "Diagnostic mirror test: apply quaternion mirrorXY to left-side limb outputs after the current formula. This checks whether left Avatar space is reflected relative to Unity.", current, "swing_twist", MirrorMode: "mirror_xy_left"),
                new SolverVariant("current_mirror_xy_right", "Diagnostic mirror test: apply quaternion mirrorXY to right-side limb outputs after the current formula. This checks whether right Avatar space is reflected relative to Unity.", current, "swing_twist", MirrorMode: "mirror_xy_right"),
                new SolverVariant("current_mirror_xy_both", "Diagnostic mirror test: apply quaternion mirrorXY to both left and right limb outputs after the current formula. This is a broad smoke test, not a production rule.", current, "swing_twist", MirrorMode: "mirror_xy_both"),
                new SolverVariant("same_bone_twist", "Move arm/leg twist back to the same human bone instead of the distal child. Diagnostic only.", BuildSameBoneTwistTargets(), "swing_twist"),
                new SolverVariant("distal_no_stretch", "Keep distal twist but remove forearm/lower-leg stretch input. Diagnostic only.", BuildDistalNoStretchTargets(), "swing_twist"),
                new SolverVariant("arm_no_twist", "Diagnostic curve ablation: remove Arm Twist from lower-arm bones while keeping forearm stretch.", BuildArmNoTwistTargets(), "swing_twist"),
                new SolverVariant("arm_no_stretch", "Diagnostic curve ablation: remove Forearm Stretch from lower-arm bones while keeping Arm Twist.", BuildArmNoStretchTargets(), "swing_twist"),
                new SolverVariant("lower_arm_static", "Diagnostic curve ablation: remove both Forearm Stretch and Arm Twist from lower-arm bones.", BuildLowerArmStaticTargets(), "swing_twist"),
                new SolverVariant("upper_arm_no_swing", "Diagnostic curve ablation: remove upper-arm Down-Up and Front-Back swing curves but keep distal arm tracks.", BuildUpperArmNoSwingTargets(), "swing_twist"),
                new SolverVariant("leg_no_upper_twist", "Diagnostic curve ablation: remove Upper Leg Twist from lower-leg bones while keeping lower-leg stretch.", BuildLegNoUpperTwistTargets(), "swing_twist"),
                new SolverVariant("leg_no_stretch", "Diagnostic curve ablation: remove Lower Leg Stretch from lower-leg bones while keeping Upper Leg Twist.", BuildLegNoStretchTargets(), "swing_twist"),
                new SolverVariant("lower_leg_static", "Diagnostic curve ablation: remove both Lower Leg Stretch and Upper Leg Twist from lower-leg bones.", BuildLowerLegStaticTargets(), "swing_twist"),
                new SolverVariant("distal_limb_static", "Diagnostic curve ablation: remove both twist+stretch pairs from lower-arm and lower-leg bones.", BuildDistalLimbStaticTargets(), "swing_twist"),
                new SolverVariant("distal_limb_no_proximal_twist", "Diagnostic curve ablation: remove Arm Twist and Upper Leg Twist from distal child bones while keeping stretch.", BuildDistalLimbNoProximalTwistTargets(), "swing_twist"),
                new SolverVariant("distal_limb_no_stretch", "Diagnostic curve ablation: remove Forearm/Lower Leg Stretch from distal child bones while keeping proximal twist.", BuildDistalLimbNoStretchTargets(), "swing_twist"),
                new SolverVariant("distal_pair_scale_25", "Diagnostic scale test: keep lower-arm/lower-leg twist+stretch pairs but scale both to 25%.", current, "swing_twist", TwistMode: "distal_pair_scale_25"),
                new SolverVariant("distal_pair_scale_50", "Diagnostic scale test: keep lower-arm/lower-leg twist+stretch pairs but scale both to 50%.", current, "swing_twist", TwistMode: "distal_pair_scale_50"),
                new SolverVariant("distal_pair_scale_75", "Diagnostic scale test: keep lower-arm/lower-leg twist+stretch pairs but scale both to 75%.", current, "swing_twist", TwistMode: "distal_pair_scale_75"),
                new SolverVariant("distal_pair_invert_twist", "Diagnostic sign test: invert proximal twist written to lower-arm/lower-leg while keeping stretch.", current, "swing_twist", TwistMode: "distal_pair_invert_twist"),
                new SolverVariant("distal_pair_invert_stretch", "Diagnostic sign test: invert forearm/lower-leg stretch while keeping proximal twist.", current, "swing_twist", TwistMode: "distal_pair_invert_stretch"),
                new SolverVariant("distal_pair_invert_both", "Diagnostic sign test: invert both proximal twist and stretch on lower-arm/lower-leg.", current, "swing_twist", TwistMode: "distal_pair_invert_both"),
                new SolverVariant("upper_leg_no_swing", "Diagnostic curve ablation: remove upper-leg Front-Back and In-Out swing curves but keep distal leg tracks.", BuildUpperLegNoSwingTargets(), "swing_twist"),
                new SolverVariant("foot_no_twist", "Diagnostic curve ablation: remove foot and lower-leg twist from feet while keeping Foot Up-Down.", BuildFootNoTwistTargets(), "swing_twist"),
                new SolverVariant("avatar_default_left", "Diagnostic pose-space test: avatar default pose by same skeleton index * current formula.", current, "swing_twist", "avatarDefaultPoseBySameIndex", "left"),
                new SolverVariant("avatar_default_right", "Diagnostic pose-space test: current formula * avatar default pose by same skeleton index.", current, "swing_twist", "avatarDefaultPoseBySameIndex", "right"),
                new SolverVariant("avatar_default_inverse_left", "Diagnostic pose-space test: inverse(avatar default pose by same skeleton index) * current formula.", current, "swing_twist", "avatarDefaultPoseBySameIndex", "inverse_left"),
                new SolverVariant("avatar_default_inverse_right", "Diagnostic pose-space test: current formula * inverse(avatar default pose by same skeleton index).", current, "swing_twist", "avatarDefaultPoseBySameIndex", "inverse_right"),
                new SolverVariant("avatar_default_sandwich", "Diagnostic pose-space test: avatar default pose * current formula * inverse(default pose).", current, "swing_twist", "avatarDefaultPoseBySameIndex", "sandwich"),
                new SolverVariant("avatar_default_inverse_sandwich", "Diagnostic pose-space test: inverse(default pose) * current formula * default pose.", current, "swing_twist", "avatarDefaultPoseBySameIndex", "inverse_sandwich"),
                new SolverVariant("avatar_default_zero_delta_left", "Diagnostic rest-anchor test: avatar default pose * inverse(zero-muscle formula) * current formula.", current, "swing_twist", "avatarDefaultPoseBySameIndex", "zero_delta_left"),
                new SolverVariant("avatar_default_zero_delta_right", "Diagnostic rest-anchor test: current formula * inverse(zero-muscle formula) * avatar default pose.", current, "swing_twist", "avatarDefaultPoseBySameIndex", "zero_delta_right"),
                new SolverVariant("zero_base_cross_side_avatar_default_a", "Diagnostic zero-base test from NPC/Gorou gate: cross-side avatar default opposite/target pose product replaces current zero base. Evidence only, not production.", current, "swing_twist", "zeroBaseCrossSideAvatarDefaultA", "zero_delta_left"),
                new SolverVariant("zero_base_cross_side_avatar_default_a_right", "Diagnostic zero-base test: same cross-side formula as A, but compose on the right side of current delta.", current, "swing_twist", "zeroBaseCrossSideAvatarDefaultA", "zero_delta_right"),
                new SolverVariant("zero_base_cross_side_avatar_default_a_sandwich", "Diagnostic zero-base test: same cross-side formula as A, but applied as a sandwich around current delta.", current, "swing_twist", "zeroBaseCrossSideAvatarDefaultA", "zero_delta_sandwich"),
                new SolverVariant("zero_base_cross_side_avatar_default_b", "Diagnostic zero-base test from NPC/Gorou gate: cross-side avatar default target/opposite pose product replaces current zero base. Evidence only, not production.", current, "swing_twist", "zeroBaseCrossSideAvatarDefaultB", "zero_delta_left"),
                new SolverVariant("zero_base_cross_side_avatar_default_b_right", "Diagnostic zero-base test: same cross-side formula as B, but compose on the right side of current delta.", current, "swing_twist", "zeroBaseCrossSideAvatarDefaultB", "zero_delta_right"),
                new SolverVariant("zero_base_cross_side_avatar_default_b_sandwich", "Diagnostic zero-base test: same cross-side formula as B, but applied as a sandwich around current delta.", current, "swing_twist", "zeroBaseCrossSideAvatarDefaultB", "zero_delta_sandwich"),
                new SolverVariant("human_skeleton_pose_left", "Diagnostic pose-space test: human skeleton pose * current formula.", current, "swing_twist", "humanSkeletonPose", "left"),
                new SolverVariant("human_skeleton_pose_inverse_left", "Diagnostic pose-space test: inverse(human skeleton pose) * current formula.", current, "swing_twist", "humanSkeletonPose", "inverse_left"),
                new SolverVariant("human_skeleton_pose_zero_delta_left", "Diagnostic rest-anchor test: human skeleton pose * inverse(zero-muscle formula) * current formula.", current, "swing_twist", "humanSkeletonPose", "zero_delta_left"),
                new SolverVariant("human_skeleton_local_pose_zero_delta_left", "Diagnostic rest-anchor test: parent-local human skeleton pose * inverse(zero-muscle formula) * current formula.", current, "swing_twist", "humanSkeletonLocalPose", "zero_delta_left"),
                new SolverVariant("avatar_skeleton_pose_left", "Diagnostic pose-space test: avatar skeleton pose mapped through humanSkeletonIndexArray * current formula.", current, "swing_twist", "avatarSkeletonPoseByHumanSkeletonIndex", "left"),
                new SolverVariant("avatar_skeleton_pose_inverse_left", "Diagnostic pose-space test: inverse(mapped avatar skeleton pose) * current formula.", current, "swing_twist", "avatarSkeletonPoseByHumanSkeletonIndex", "inverse_left"),
                new SolverVariant("avatar_skeleton_pose_zero_delta_left", "Diagnostic rest-anchor test: mapped avatar skeleton pose * inverse(zero-muscle formula) * current formula.", current, "swing_twist", "avatarSkeletonPoseByHumanSkeletonIndex", "zero_delta_left"),
                new SolverVariant("avatar_skeleton_local_pose_zero_delta_left", "Diagnostic rest-anchor test: parent-local mapped avatar skeleton pose * inverse(zero-muscle formula) * current formula.", current, "swing_twist", "avatarSkeletonLocalPoseByHumanSkeletonIndex", "zero_delta_left"),
                new SolverVariant("avatar_default_local_pose_zero_delta_left", "Diagnostic rest-anchor test: parent-local mapped avatar default pose * inverse(zero-muscle formula) * current formula.", current, "swing_twist", "avatarDefaultLocalPoseByHumanSkeletonIndex", "zero_delta_left"),
                new SolverVariant("gltf_rest_left", "Diagnostic rest-space test: glTF node rest rotation * current formula.", current, "swing_twist", "gltfRest", "left"),
                new SolverVariant("gltf_rest_right", "Diagnostic rest-space test: current formula * glTF node rest rotation.", current, "swing_twist", "gltfRest", "right"),
                new SolverVariant("gltf_rest_inverse_left", "Diagnostic rest-space test: inverse(glTF node rest rotation) * current formula.", current, "swing_twist", "gltfRest", "inverse_left"),
                new SolverVariant("gltf_rest_inverse_right", "Diagnostic rest-space test: current formula * inverse(glTF node rest rotation).", current, "swing_twist", "gltfRest", "inverse_right"),
                new SolverVariant("gltf_rest_sandwich", "Diagnostic rest-space test: glTF rest * current formula * inverse(rest).", current, "swing_twist", "gltfRest", "sandwich"),
                new SolverVariant("gltf_rest_zero_delta_left", "Diagnostic rest-anchor test: glTF rest * inverse(zero-muscle formula) * current formula.", current, "swing_twist", "gltfRest", "zero_delta_left"),
                new SolverVariant("gltf_rest_zero_delta_right", "Diagnostic rest-anchor test: current formula * inverse(zero-muscle formula) * glTF rest.", current, "swing_twist", "gltfRest", "zero_delta_right"),
                new SolverVariant("gltf_rest_zero_delta_sandwich", "Diagnostic rest-anchor test: glTF rest * inverse(zero-muscle formula) * current formula * inverse(glTF rest).", current, "swing_twist", "gltfRest", "zero_delta_sandwich"),
                new SolverVariant("gltf_parent_rest_left", "Diagnostic rest-space test: parent glTF rest rotation * current formula.", current, "swing_twist", "gltfParentRest", "left"),
                new SolverVariant("gltf_parent_rest_inverse_left", "Diagnostic rest-space test: inverse(parent glTF rest rotation) * current formula.", current, "swing_twist", "gltfParentRest", "inverse_left"),
                new SolverVariant("twist_split_50_50", "Diagnostic twist distribution: split limb twist muscles 50/50 between parent and child bones instead of sending all twist to the distal child.", BuildSplitTwistTargets(), "swing_twist", TwistMode: "split_50_50"),
                new SolverVariant("twist_split_unity_child_param", "Diagnostic twist distribution: use Avatar twist values as child share, parent receives the remaining twist.", BuildSplitTwistTargets(), "swing_twist", TwistMode: "split_unity_child_param"),
                new SolverVariant("twist_split_unity_parent_param", "Diagnostic twist distribution: use Avatar twist values as parent share, child receives the remaining twist.", BuildSplitTwistTargets(), "swing_twist", TwistMode: "split_unity_parent_param"),
                new SolverVariant("twist_invert_current", "Diagnostic twist direction: invert all limb twist muscle values while keeping the current distal mapping.", current, "swing_twist", TwistMode: "invert_all_twist"),
                new SolverVariant("twist_split_arm_unity_child_param", "Diagnostic twist distribution: only split Arm Twist with Avatar arm twist as child share; other twist families stay current.", BuildSplitTwistTargets(), "swing_twist", TwistMode: "split_arm_unity_child_param"),
                new SolverVariant("twist_split_arm_50_50", "Diagnostic twist distribution: only split Arm Twist 50/50 between upper arm and lower arm; other twist families stay current.", BuildSplitTwistTargets(), "swing_twist", TwistMode: "split_arm_50_50"),
                new SolverVariant("twist_split_upperLeg_unity_child_param", "Diagnostic twist distribution: only split Upper Leg Twist with Avatar upperLeg twist as child share; other twist families stay current.", BuildSplitTwistTargets(), "swing_twist", TwistMode: "split_upperLeg_unity_child_param"),
                new SolverVariant("twist_split_leg_unity_child_param", "Diagnostic twist distribution: only split Lower Leg Twist with Avatar leg twist as child share; other twist families stay current.", BuildSplitTwistTargets(), "swing_twist", TwistMode: "split_leg_unity_child_param"),
                new SolverVariant("twist_split_foreArm_unity_child_param", "Diagnostic twist distribution: only split Forearm Twist with Avatar foreArm twist as child share; other twist families stay current.", BuildSplitTwistTargets(), "swing_twist", TwistMode: "split_foreArm_unity_child_param"),
                new SolverVariant("twist_split_50_50_invert", "Diagnostic twist direction/distribution: split limb twist 50/50 and invert twist sign.", BuildSplitTwistTargets(), "swing_twist", TwistMode: "split_50_50_invert"),
                new SolverVariant("twist_split_unity_child_param_invert", "Diagnostic twist direction/distribution: use Avatar child twist share and invert twist sign.", BuildSplitTwistTargets(), "swing_twist", TwistMode: "split_unity_child_param_invert"),
                new SolverVariant("twist_split_unity_parent_param_invert", "Diagnostic twist direction/distribution: use Avatar parent twist share and invert twist sign.", BuildSplitTwistTargets(), "swing_twist", TwistMode: "split_unity_parent_param_invert"),
                new SolverVariant("leg_twist_parent_source_x", "Diagnostic hint test: upper-leg twist still writes to lower-leg X/twist, but uses the upper-leg X/twist limit axis as the muscle source.", current, "swing_twist", TwistMode: "leg_twist_parent_source_x"),
                new SolverVariant("arm_twist_target_y_current_source", "Diagnostic hint test: arm twist writes to lower-arm Y/swing while keeping lower-arm X/twist as the limit source.", current, "swing_twist", TwistMode: "arm_twist_target_y_current_source"),
                new SolverVariant("arm_twist_target_y_parent_source", "Diagnostic hint test: arm twist writes to lower-arm Y/swing and uses upper-arm X/twist as the limit source.", current, "swing_twist", TwistMode: "arm_twist_target_y_parent_source"),
                new SolverVariant("arm_twist_target_y_side_source", "Diagnostic hint test from cross-model single-muscle oracle: Arm Twist writes to lower-arm Y; left uses lower-arm Z/swing limit, right uses lower-arm X/twist limit.", current, "swing_twist", TwistMode: "arm_twist_target_y_side_source"),
                new SolverVariant("sign_policy_common_inverts", "Diagnostic sign policy from NPC/Gorou single-muscle middle-space oracle: invert only humanBone+attribute pairs that were stable opposite-axis candidates across both models.", current, "swing_twist", TwistMode: "sign_policy_common_inverts"),
                new SolverVariant("post_projected_delta_left", "Diagnostic projection test from expectedAxisFitSummary: treat middle as post * delta * inverse(post), then apply zeroPose * delta.", current, "post_projected_delta_left"),
                new SolverVariant("post_projected_delta_right", "Diagnostic projection test from expectedAxisFitSummary: treat middle as post * delta * inverse(post), then apply delta * zeroPose.", current, "post_projected_delta_right"),
                new SolverVariant("swing_y_then_z", "Diagnostic swing test: apply Avatar Y and Z swing axes as separate rotations, Y then Z.", current, "swing_twist", SwingMode: "y_then_z"),
                new SolverVariant("swing_z_then_y", "Diagnostic swing test: apply Avatar Z and Y swing axes as separate rotations, Z then Y.", current, "swing_twist", SwingMode: "z_then_y"),
                new SolverVariant("swing_ellipse_clamped", "Diagnostic swing test: clamp Y/Z by an ellipse before converting the swing vector to an axis-angle quaternion.", current, "swing_twist", SwingMode: "ellipse_clamped"),
                new SolverVariant("swing_ellipse_scaled", "Diagnostic swing test: scale Y/Z through the Avatar ellipse radius before converting the swing vector to axis-angle.", current, "swing_twist", SwingMode: "ellipse_scaled"),
            };
        }

        private static SolverTarget[] BuildCurrentSolverTargets() => new[]
        {
            new SolverTarget("Spine", "Spine Front-Back", "Spine Left-Right", "Spine Twist Left-Right"),
            new SolverTarget("Chest", "Chest Front-Back", "Chest Left-Right", "Chest Twist Left-Right"),
            new SolverTarget("UpperChest", "UpperChest Front-Back", "UpperChest Left-Right", "UpperChest Twist Left-Right"),
            new SolverTarget("Neck", "Neck Nod Down-Up", "Neck Tilt Left-Right", "Neck Turn Left-Right"),
            new SolverTarget("Head", "Head Nod Down-Up", "Head Tilt Left-Right", "Head Turn Left-Right"),
            new SolverTarget("LeftUpperLeg", "Left Upper Leg Front-Back", "Left Upper Leg In-Out", null),
            new SolverTarget("RightUpperLeg", "Right Upper Leg Front-Back", "Right Upper Leg In-Out", null),
            new SolverTarget("LeftLowerLeg", "Left Lower Leg Stretch", null, "Left Upper Leg Twist In-Out"),
            new SolverTarget("RightLowerLeg", "Right Lower Leg Stretch", null, "Right Upper Leg Twist In-Out"),
            new SolverTarget("LeftFoot", "Left Foot Up-Down", null, "Left Foot Twist In-Out", "Left Lower Leg Twist In-Out"),
            new SolverTarget("RightFoot", "Right Foot Up-Down", null, "Right Foot Twist In-Out", "Right Lower Leg Twist In-Out"),
            new SolverTarget("LeftToes", "Left Toes Up-Down", null, null),
            new SolverTarget("RightToes", "Right Toes Up-Down", null, null),
            new SolverTarget("LeftShoulder", "Left Shoulder Down-Up", "Left Shoulder Front-Back", null),
            new SolverTarget("RightShoulder", "Right Shoulder Down-Up", "Right Shoulder Front-Back", null),
            new SolverTarget("LeftUpperArm", "Left Arm Down-Up", "Left Arm Front-Back", null),
            new SolverTarget("RightUpperArm", "Right Arm Down-Up", "Right Arm Front-Back", null),
            new SolverTarget("LeftLowerArm", "Left Forearm Stretch", null, "Left Arm Twist In-Out"),
            new SolverTarget("RightLowerArm", "Right Forearm Stretch", null, "Right Arm Twist In-Out"),
            new SolverTarget("LeftHand", "Left Hand Down-Up", "Left Hand In-Out", "Left Forearm Twist In-Out"),
            new SolverTarget("RightHand", "Right Hand Down-Up", "Right Hand In-Out", "Right Forearm Twist In-Out"),
            new SolverTarget("Jaw", "Jaw Close", "Jaw Left-Right", null),
        };

        private static SolverTarget[] BuildSameBoneTwistTargets() => BuildCurrentSolverTargets()
            .Select(x => x.HumanBone switch
            {
                "LeftUpperLeg" => x with { ZAttribute = "Left Upper Leg Twist In-Out" },
                "RightUpperLeg" => x with { ZAttribute = "Right Upper Leg Twist In-Out" },
                "LeftLowerLeg" => x with { ZAttribute = "Left Lower Leg Twist In-Out" },
                "RightLowerLeg" => x with { ZAttribute = "Right Lower Leg Twist In-Out" },
                "LeftFoot" => x with { ExtraZAttribute = null },
                "RightFoot" => x with { ExtraZAttribute = null },
                "LeftUpperArm" => x with { ZAttribute = "Left Arm Twist In-Out" },
                "RightUpperArm" => x with { ZAttribute = "Right Arm Twist In-Out" },
                "LeftLowerArm" => x with { ZAttribute = "Left Forearm Twist In-Out" },
                "RightLowerArm" => x with { ZAttribute = "Right Forearm Twist In-Out" },
                "LeftHand" => x with { ZAttribute = null },
                "RightHand" => x with { ZAttribute = null },
                _ => x,
            })
            .ToArray();

        private static SolverTarget[] BuildDistalNoStretchTargets() => BuildCurrentSolverTargets()
            .Select(x => x.HumanBone switch
            {
                "LeftLowerLeg" => x with { XAttribute = null },
                "RightLowerLeg" => x with { XAttribute = null },
                "LeftLowerArm" => x with { XAttribute = null },
                "RightLowerArm" => x with { XAttribute = null },
                _ => x,
            })
            .ToArray();

        private static SolverTarget[] BuildArmNoTwistTargets() => BuildCurrentSolverTargets()
            .Select(x => x.HumanBone switch
            {
                "LeftLowerArm" => x with { ZAttribute = null },
                "RightLowerArm" => x with { ZAttribute = null },
                _ => x,
            })
            .ToArray();

        private static SolverTarget[] BuildArmNoStretchTargets() => BuildCurrentSolverTargets()
            .Select(x => x.HumanBone switch
            {
                "LeftLowerArm" => x with { XAttribute = null },
                "RightLowerArm" => x with { XAttribute = null },
                _ => x,
            })
            .ToArray();

        private static SolverTarget[] BuildLowerArmStaticTargets() => BuildCurrentSolverTargets()
            .Select(x => x.HumanBone switch
            {
                "LeftLowerArm" => x with { XAttribute = null, ZAttribute = null },
                "RightLowerArm" => x with { XAttribute = null, ZAttribute = null },
                _ => x,
            })
            .ToArray();

        private static SolverTarget[] BuildUpperArmNoSwingTargets() => BuildCurrentSolverTargets()
            .Select(x => x.HumanBone switch
            {
                "LeftUpperArm" => x with { XAttribute = null, YAttribute = null },
                "RightUpperArm" => x with { XAttribute = null, YAttribute = null },
                _ => x,
            })
            .ToArray();

        private static SolverTarget[] BuildLegNoUpperTwistTargets() => BuildCurrentSolverTargets()
            .Select(x => x.HumanBone switch
            {
                "LeftLowerLeg" => x with { ZAttribute = null },
                "RightLowerLeg" => x with { ZAttribute = null },
                _ => x,
            })
            .ToArray();

        private static SolverTarget[] BuildLegNoStretchTargets() => BuildCurrentSolverTargets()
            .Select(x => x.HumanBone switch
            {
                "LeftLowerLeg" => x with { XAttribute = null },
                "RightLowerLeg" => x with { XAttribute = null },
                _ => x,
            })
            .ToArray();

        private static SolverTarget[] BuildLowerLegStaticTargets() => BuildCurrentSolverTargets()
            .Select(x => x.HumanBone switch
            {
                "LeftLowerLeg" => x with { XAttribute = null, ZAttribute = null },
                "RightLowerLeg" => x with { XAttribute = null, ZAttribute = null },
                _ => x,
            })
            .ToArray();

        private static SolverTarget[] BuildDistalLimbStaticTargets() => BuildCurrentSolverTargets()
            .Select(x => x.HumanBone switch
            {
                "LeftLowerArm" => x with { XAttribute = null, ZAttribute = null },
                "RightLowerArm" => x with { XAttribute = null, ZAttribute = null },
                "LeftLowerLeg" => x with { XAttribute = null, ZAttribute = null },
                "RightLowerLeg" => x with { XAttribute = null, ZAttribute = null },
                _ => x,
            })
            .ToArray();

        private static SolverTarget[] BuildDistalLimbNoProximalTwistTargets() => BuildCurrentSolverTargets()
            .Select(x => x.HumanBone switch
            {
                "LeftLowerArm" => x with { ZAttribute = null },
                "RightLowerArm" => x with { ZAttribute = null },
                "LeftLowerLeg" => x with { ZAttribute = null },
                "RightLowerLeg" => x with { ZAttribute = null },
                _ => x,
            })
            .ToArray();

        private static SolverTarget[] BuildDistalLimbNoStretchTargets() => BuildCurrentSolverTargets()
            .Select(x => x.HumanBone switch
            {
                "LeftLowerArm" => x with { XAttribute = null },
                "RightLowerArm" => x with { XAttribute = null },
                "LeftLowerLeg" => x with { XAttribute = null },
                "RightLowerLeg" => x with { XAttribute = null },
                _ => x,
            })
            .ToArray();

        private static SolverTarget[] BuildUpperLegNoSwingTargets() => BuildCurrentSolverTargets()
            .Select(x => x.HumanBone switch
            {
                "LeftUpperLeg" => x with { XAttribute = null, YAttribute = null },
                "RightUpperLeg" => x with { XAttribute = null, YAttribute = null },
                _ => x,
            })
            .ToArray();

        private static SolverTarget[] BuildFootNoTwistTargets() => BuildCurrentSolverTargets()
            .Select(x => x.HumanBone switch
            {
                "LeftFoot" => x with { ZAttribute = null, ExtraZAttribute = null },
                "RightFoot" => x with { ZAttribute = null, ExtraZAttribute = null },
                _ => x,
            })
            .ToArray();

        private static SolverTarget[] BuildSplitTwistTargets() => BuildCurrentSolverTargets()
            .Select(x => x.HumanBone switch
            {
                "LeftUpperLeg" => x with { ZAttribute = "Left Upper Leg Twist In-Out" },
                "RightUpperLeg" => x with { ZAttribute = "Right Upper Leg Twist In-Out" },
                "LeftLowerLeg" => x with { ZAttribute = "Left Upper Leg Twist In-Out", ExtraZAttribute = "Left Lower Leg Twist In-Out" },
                "RightLowerLeg" => x with { ZAttribute = "Right Upper Leg Twist In-Out", ExtraZAttribute = "Right Lower Leg Twist In-Out" },
                "LeftUpperArm" => x with { ZAttribute = "Left Arm Twist In-Out" },
                "RightUpperArm" => x with { ZAttribute = "Right Arm Twist In-Out" },
                "LeftLowerArm" => x with { ZAttribute = "Left Arm Twist In-Out", ExtraZAttribute = "Left Forearm Twist In-Out" },
                "RightLowerArm" => x with { ZAttribute = "Right Arm Twist In-Out", ExtraZAttribute = "Right Forearm Twist In-Out" },
                _ => x,
            })
            .ToArray();

        private static bool TryGetSolverTarget(JArray solverNodes, JArray solverAxes, JArray humanBoneIndex, SolverTarget target, out string path, out SolverAxis axis)
        {
            path = null;
            axis = default;
            if (!HumanBoneIndexes.TryGetValue(target.HumanBone, out var humanIndex) ||
                humanIndex < 0 || humanIndex >= humanBoneIndex.Count)
            {
                return false;
            }

            var nodeIndex = (int?)humanBoneIndex[humanIndex] ?? -1;
            if (nodeIndex < 0 || nodeIndex >= solverNodes.Count || solverNodes[nodeIndex] is not JObject node)
            {
                return false;
            }

            path = (string)node["path"];
            if (string.IsNullOrWhiteSpace(path))
            {
                path = (string)node["name"];
            }
            var axesId = (int?)node["axesId"] ?? -1;
            if (string.IsNullOrWhiteSpace(path) || axesId < 0 || axesId >= solverAxes.Count || solverAxes[axesId] is not JObject axisJson)
            {
                return false;
            }

            axis = new SolverAxis(
                ReadFloatArray(axisJson["preQ"] as JArray, 4),
                ReadFloatArray(axisJson["postQ"] as JArray, 4),
                ReadFloatArray(axisJson["sign"] as JArray, 3),
                ReadFloatArray(axisJson["limitMin"] as JArray, 3),
                ReadFloatArray(axisJson["limitMax"] as JArray, 3));
            return axis.PreQ != null && axis.PostQ != null && axis.Sign != null && axis.LimitMin != null && axis.LimitMax != null;
        }

        private static bool TryFindJointIndex(string[] jointPaths, string path, out int index)
        {
            index = Array.FindIndex(jointPaths ?? Array.Empty<string>(), x => string.Equals(NormalizeBakePath(x), NormalizeBakePath(path), StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                return true;
            }

            var normalized = NormalizeBakePath(path);
            var suffix = "/" + normalized;
            var matches = (jointPaths ?? Array.Empty<string>())
                .Select((value, i) => new { Value = NormalizeBakePath(value), Index = i })
                .Where(x => x.Value != null && x.Value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            if (matches.Length == 1)
            {
                index = matches[0].Index;
                return true;
            }

            index = -1;
            return false;
        }

        private static float[] BuildVariantSolverRotation(
            Dictionary<string, List<FloatKey>> curves,
            SolverTarget target,
            SolverAxis axis,
            float time,
            SolverVariant variant,
            JObject solver,
            JArray solverNodes,
            JArray humanBoneIndex,
            string targetPath,
            JObject[] gltfNodes,
            Dictionary<string, int> nodePathToIndex)
        {
            var angles = new float[3];
            var axisOrder = GetVariantAxisOrder(variant.AxisOrder);
            AddVariantMuscleAngle(angles, curves, target.XAttribute, time, target.HumanBone, axis, axisOrder[0], variant, solver, solverNodes, humanBoneIndex);
            AddVariantMuscleAngle(angles, curves, target.YAttribute, time, target.HumanBone, axis, axisOrder[1], variant, solver, solverNodes, humanBoneIndex);
            AddVariantMuscleAngle(angles, curves, target.ZAttribute, time, target.HumanBone, axis, axisOrder[2], variant, solver, solverNodes, humanBoneIndex);
            AddVariantMuscleAngle(angles, curves, target.ExtraZAttribute, time, target.HumanBone, axis, axisOrder[2], variant, solver, solverNodes, humanBoneIndex);

            var pre = Normalize(axis.PreQ);
            var post = Normalize(axis.PostQ);
            var middle = variant.ComposeMode switch
            {
                "twist_swing" => Multiply(AxisAngleRadiansToQuaternion(1, 0, 0, angles[0]), BuildSwingQuaternion(angles, axis, variant)),
                "euler_zyx" => Multiply(Multiply(AxisAngleRadiansToQuaternion(0, 0, 1, angles[2]), AxisAngleRadiansToQuaternion(0, 1, 0, angles[1])), AxisAngleRadiansToQuaternion(1, 0, 0, angles[0])),
                _ => Multiply(BuildSwingQuaternion(angles, axis, variant), AxisAngleRadiansToQuaternion(1, 0, 0, angles[0])),
            };
            var zeroSolved = Normalize(Multiply(pre, Inverse(post)));
            var solved = BuildVariantSolvedRotation(pre, post, middle, zeroSolved, variant.ComposeMode);
            var pose = ResolveVariantPose(solver, solverNodes, humanBoneIndex, target.HumanBone, targetPath, variant.PoseSource, gltfNodes, nodePathToIndex);
            var posed = ApplyVariantPose(solved, pose, variant.PoseApplyMode, zeroSolved);
            return ApplyVariantMirror(posed, target.HumanBone, variant.MirrorMode);
        }

        private static float[] BuildVariantSolvedRotation(float[] pre, float[] post, float[] middle, float[] zeroSolved, string composeMode)
        {
            if (string.Equals(composeMode, "post_projected_delta_left", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(composeMode, "post_projected_delta_right", StringComparison.OrdinalIgnoreCase))
            {
                // expectedAxisFitSummary 的最佳投影是 post * delta * inverse(post)。
                // 这里把 middle 反解成 local delta，再分别测试 zero 左乘/右乘。
                var delta = Normalize(Multiply(Multiply(Inverse(post), middle), post));
                return string.Equals(composeMode, "post_projected_delta_left", StringComparison.OrdinalIgnoreCase)
                    ? Normalize(Multiply(zeroSolved, delta))
                    : Normalize(Multiply(delta, zeroSolved));
            }

            return Normalize(Multiply(Multiply(pre, middle), Inverse(post)));
        }

        private static void AddVariantMuscleAngle(
            float[] angles,
            Dictionary<string, List<FloatKey>> curves,
            string attribute,
            float time,
            string targetHumanBone,
            SolverAxis targetAxis,
            int defaultAvatarAxis,
            SolverVariant variant,
            JObject solver,
            JArray solverNodes,
            JArray humanBoneIndex)
        {
            if (string.IsNullOrWhiteSpace(attribute))
            {
                return;
            }

            var value = SampleFloatCurve(curves.TryGetValue(attribute, out var keys) ? keys : null, time) *
                ResolveTwistScale(targetHumanBone, attribute, variant.TwistMode, solver) *
                ResolveDiagnosticDistalPairScale(targetHumanBone, attribute, variant.TwistMode) *
                ResolveDiagnosticSignPolicyScale(targetHumanBone, attribute, variant.TwistMode);
            if (MathF.Abs(value) <= 0.0000001f)
            {
                return;
            }

            var targetAvatarAxis = ResolveVariantTargetAvatarAxis(attribute, targetHumanBone, defaultAvatarAxis, variant.TwistMode);
            var limitHumanBone = ResolveVariantLimitHumanBone(attribute, targetHumanBone, variant.TwistMode);
            var limitAvatarAxis = ResolveVariantLimitAvatarAxis(attribute, targetHumanBone, targetAvatarAxis, variant.TwistMode);
            var limitAxis = targetAxis;
            var solverAxes = solver?["skeleton"]?["axes"] as JArray;
            if (!string.Equals(limitHumanBone, targetHumanBone, StringComparison.OrdinalIgnoreCase) &&
                !TryGetHumanBonePathAxis(solverNodes, solverAxes, humanBoneIndex, limitHumanBone, out _, out limitAxis))
            {
                limitAxis = targetAxis;
            }

            angles[targetAvatarAxis] += LimitMuscle(value, limitAxis, limitAvatarAxis);
        }

        private static float ResolveDiagnosticSignPolicyScale(string humanBone, string attribute, string twistMode)
        {
            if (!string.Equals(twistMode, "sign_policy_common_inverts", StringComparison.OrdinalIgnoreCase))
            {
                return 1f;
            }

            // 这些条目来自 NPC_Male Alert01AS 与 Gorou Standby 的 Unity 单 muscle oracle 交集。
            // 这里只作为完整 timeline 诊断，不代表生产 solver 已确认采用。
            return IsCommonSignPolicyInvertCandidate(humanBone, attribute) ? -1f : 1f;
        }

        private static float ResolveDiagnosticDistalPairScale(string humanBone, string attribute, string twistMode)
        {
            if (!IsDistalPairAttribute(humanBone, attribute))
            {
                return 1f;
            }

            if (TryParseDistalPairGridScale(twistMode, out var twistScale, out var stretchScale))
            {
                return IsDistalProximalTwistAttribute(humanBone, attribute) ? twistScale : stretchScale;
            }

            return twistMode switch
            {
                "distal_pair_scale_25" => 0.25f,
                "distal_pair_scale_50" => 0.5f,
                "distal_pair_scale_75" => 0.75f,
                "distal_pair_invert_twist" => IsDistalProximalTwistAttribute(humanBone, attribute) ? -1f : 1f,
                "distal_pair_invert_stretch" => IsDistalStretchAttribute(humanBone, attribute) ? -1f : 1f,
                "distal_pair_invert_both" => -1f,
                _ => 1f,
            };
        }

        private static bool TryParseDistalPairGridScale(string twistMode, out float twistScale, out float stretchScale)
        {
            twistScale = 1f;
            stretchScale = 1f;
            const string prefix = "distal_pair_grid_t";
            if (string.IsNullOrWhiteSpace(twistMode) ||
                !twistMode.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var marker = twistMode.IndexOf("_s", prefix.Length, StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
            {
                return false;
            }

            if (!float.TryParse(twistMode[prefix.Length..marker], NumberStyles.Float, CultureInfo.InvariantCulture, out var twistPercent) ||
                !float.TryParse(twistMode[(marker + 2)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var stretchPercent))
            {
                return false;
            }

            twistScale = twistPercent / 100f;
            stretchScale = stretchPercent / 100f;
            return true;
        }

        private static bool IsDistalPairAttribute(string humanBone, string attribute)
        {
            return IsDistalProximalTwistAttribute(humanBone, attribute) ||
                IsDistalStretchAttribute(humanBone, attribute);
        }

        private static bool IsDistalProximalTwistAttribute(string humanBone, string attribute)
        {
            return
                IsHumanBoneAttribute(humanBone, attribute, "LeftLowerArm", "Left Arm Twist In-Out") ||
                IsHumanBoneAttribute(humanBone, attribute, "RightLowerArm", "Right Arm Twist In-Out") ||
                IsHumanBoneAttribute(humanBone, attribute, "LeftLowerLeg", "Left Upper Leg Twist In-Out") ||
                IsHumanBoneAttribute(humanBone, attribute, "RightLowerLeg", "Right Upper Leg Twist In-Out");
        }

        private static bool IsDistalStretchAttribute(string humanBone, string attribute)
        {
            return
                IsHumanBoneAttribute(humanBone, attribute, "LeftLowerArm", "Left Forearm Stretch") ||
                IsHumanBoneAttribute(humanBone, attribute, "RightLowerArm", "Right Forearm Stretch") ||
                IsHumanBoneAttribute(humanBone, attribute, "LeftLowerLeg", "Left Lower Leg Stretch") ||
                IsHumanBoneAttribute(humanBone, attribute, "RightLowerLeg", "Right Lower Leg Stretch");
        }

        private static bool IsCommonSignPolicyInvertCandidate(string humanBone, string attribute)
        {
            return
                IsHumanBoneAttribute(humanBone, attribute, "RightLowerArm", "Right Arm Twist In-Out") ||
                IsHumanBoneAttribute(humanBone, attribute, "LeftUpperArm", "Left Arm Down-Up") ||
                IsHumanBoneAttribute(humanBone, attribute, "RightLowerLeg", "Right Upper Leg Twist In-Out") ||
                IsHumanBoneAttribute(humanBone, attribute, "RightUpperLeg", "Right Upper Leg In-Out") ||
                IsHumanBoneAttribute(humanBone, attribute, "LeftLowerArm", "Left Forearm Stretch") ||
                IsHumanBoneAttribute(humanBone, attribute, "RightFoot", "Right Lower Leg Twist In-Out") ||
                IsHumanBoneAttribute(humanBone, attribute, "RightLowerLeg", "Right Lower Leg Stretch") ||
                IsHumanBoneAttribute(humanBone, attribute, "RightShoulder", "Right Shoulder Down-Up");
        }

        private static bool IsHumanBoneAttribute(string humanBone, string attribute, string expectedHumanBone, string expectedAttribute)
        {
            return string.Equals(humanBone, expectedHumanBone, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(attribute, expectedAttribute, StringComparison.OrdinalIgnoreCase);
        }

        private static int ResolveVariantTargetAvatarAxis(string attribute, string targetHumanBone, int defaultAvatarAxis, string twistMode)
        {
            if (IsArmTwistTargetYMode(twistMode) &&
                IsLowerArmBone(targetHumanBone) &&
                attribute?.IndexOf("Arm Twist", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 1;
            }
            if (attribute?.IndexOf("Foot Twist", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 1;
            }
            return defaultAvatarAxis;
        }

        private static int ResolveVariantLimitAvatarAxis(string attribute, string targetHumanBone, int targetAvatarAxis, string twistMode)
        {
            if (string.Equals(twistMode, "leg_twist_parent_source_x", StringComparison.OrdinalIgnoreCase) &&
                IsLowerLegBone(targetHumanBone) &&
                attribute?.IndexOf("Upper Leg Twist", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 0;
            }
            if (IsArmTwistTargetYMode(twistMode) &&
                IsLowerArmBone(targetHumanBone) &&
                attribute?.IndexOf("Arm Twist", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (string.Equals(twistMode, "arm_twist_target_y_side_source", StringComparison.OrdinalIgnoreCase))
                {
                    return targetHumanBone.StartsWith("Left", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
                }
                return 0;
            }
            if (attribute?.IndexOf("Lower Leg Twist", StringComparison.OrdinalIgnoreCase) >= 0 ||
                attribute?.IndexOf("Forearm Twist", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 0;
            }
            return targetAvatarAxis;
        }

        private static string ResolveVariantLimitHumanBone(string attribute, string targetHumanBone, string twistMode)
        {
            if (string.Equals(twistMode, "leg_twist_parent_source_x", StringComparison.OrdinalIgnoreCase) &&
                IsLowerLegBone(targetHumanBone) &&
                attribute?.IndexOf("Upper Leg Twist", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return targetHumanBone.StartsWith("Left", StringComparison.OrdinalIgnoreCase) ? "LeftUpperLeg" : "RightUpperLeg";
            }
            if (string.Equals(twistMode, "arm_twist_target_y_parent_source", StringComparison.OrdinalIgnoreCase) &&
                IsLowerArmBone(targetHumanBone) &&
                attribute?.IndexOf("Arm Twist", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return targetHumanBone.StartsWith("Left", StringComparison.OrdinalIgnoreCase) ? "LeftUpperArm" : "RightUpperArm";
            }
            if (attribute?.IndexOf("Lower Leg Twist", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return attribute.StartsWith("Left ", StringComparison.OrdinalIgnoreCase) ? "LeftLowerLeg" : "RightLowerLeg";
            }
            if (attribute?.IndexOf("Upper Leg Twist", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return attribute.StartsWith("Left ", StringComparison.OrdinalIgnoreCase) ? "LeftUpperLeg" : "RightUpperLeg";
            }
            if (attribute?.IndexOf("Forearm Twist", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return attribute.StartsWith("Left ", StringComparison.OrdinalIgnoreCase) ? "LeftLowerArm" : "RightLowerArm";
            }
            return targetHumanBone;
        }

        private static bool IsArmTwistTargetYMode(string twistMode)
        {
            return string.Equals(twistMode, "arm_twist_target_y_current_source", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(twistMode, "arm_twist_target_y_parent_source", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(twistMode, "arm_twist_target_y_side_source", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLowerLegBone(string humanBone)
        {
            return string.Equals(humanBone, "LeftLowerLeg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(humanBone, "RightLowerLeg", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLowerArmBone(string humanBone)
        {
            return string.Equals(humanBone, "LeftLowerArm", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(humanBone, "RightLowerArm", StringComparison.OrdinalIgnoreCase);
        }

        private static float[] ApplyVariantMirror(float[] rotation, string humanBone, string mirrorMode)
        {
            if (string.IsNullOrWhiteSpace(mirrorMode))
            {
                return rotation;
            }

            var isLeft = humanBone?.StartsWith("Left", StringComparison.OrdinalIgnoreCase) == true;
            var isRight = humanBone?.StartsWith("Right", StringComparison.OrdinalIgnoreCase) == true;
            var shouldMirror = mirrorMode switch
            {
                "mirror_xy_left" => isLeft,
                "mirror_xy_right" => isRight,
                "mirror_xy_both" => isLeft || isRight,
                _ => false,
            };
            if (!shouldMirror)
            {
                return rotation;
            }

            return MirrorQuaternion(rotation, "mirrorXY");
        }

        private static float[] BuildSwingQuaternion(float[] angles, SolverAxis axis, SolverVariant variant)
        {
            var y = angles[1];
            var z = angles[2];
            return variant.SwingMode switch
            {
                "y_then_z" => Multiply(AxisAngleRadiansToQuaternion(0, 1, 0, y), AxisAngleRadiansToQuaternion(0, 0, 1, z)),
                "z_then_y" => Multiply(AxisAngleRadiansToQuaternion(0, 0, 1, z), AxisAngleRadiansToQuaternion(0, 1, 0, y)),
                "ellipse_clamped" => SwingRadiansToQuaternionClamped(y, z, axis),
                "ellipse_scaled" => SwingRadiansToQuaternionScaled(y, z, axis),
                _ => SwingRadiansToQuaternion(y, z),
            };
        }

        private static int[] GetVariantAxisOrder(string axisOrder)
        {
            return axisOrder switch
            {
                "attribute_xyz" => new[] { 0, 1, 2 },
                "attribute_xzy" => new[] { 0, 2, 1 },
                "attribute_yxz" => new[] { 1, 0, 2 },
                "attribute_yzx" => new[] { 1, 2, 0 },
                "attribute_zxy" => new[] { 2, 0, 1 },
                _ => new[] { 2, 1, 0 },
            };
        }

        private static float[] SwingRadiansToQuaternionClamped(float y, float z, SolverAxis axis)
        {
            var normalized = SwingEllipseNormalizedRadius(y, z, axis);
            if (normalized > 1f)
            {
                y /= normalized;
                z /= normalized;
            }
            return SwingRadiansToQuaternion(y, z);
        }

        private static float[] SwingRadiansToQuaternionScaled(float y, float z, SolverAxis axis)
        {
            var normalized = SwingEllipseNormalizedRadius(y, z, axis);
            if (normalized < 0.0000001f)
            {
                return SwingRadiansToQuaternion(y, z);
            }

            var radius = MathF.Min(1f, normalized);
            return SwingRadiansToQuaternion(y / normalized * radius, z / normalized * radius);
        }

        private static float SwingEllipseNormalizedRadius(float y, float z, SolverAxis axis)
        {
            var yLimit = MathF.Max(MathF.Abs(axis.LimitMin.Length > 1 ? axis.LimitMin[1] : 0f), MathF.Abs(axis.LimitMax.Length > 1 ? axis.LimitMax[1] : 0f));
            var zLimit = MathF.Max(MathF.Abs(axis.LimitMin.Length > 2 ? axis.LimitMin[2] : 0f), MathF.Abs(axis.LimitMax.Length > 2 ? axis.LimitMax[2] : 0f));
            if (yLimit < 0.0000001f || zLimit < 0.0000001f)
            {
                return 0f;
            }
            return MathF.Sqrt((y / yLimit) * (y / yLimit) + (z / zLimit) * (z / zLimit));
        }

        private static float SampleTwistAwareZValue(
            Dictionary<string, List<FloatKey>> curves,
            SolverTarget target,
            float time,
            SolverVariant variant,
            JObject solver)
        {
            var z = SampleFloatCurve(curves.TryGetValue(target.ZAttribute ?? string.Empty, out var zCurve) ? zCurve : null, time)
                * ResolveTwistScale(target.HumanBone, target.ZAttribute, variant.TwistMode, solver);
            var extra = SampleFloatCurve(curves.TryGetValue(target.ExtraZAttribute ?? string.Empty, out var extraCurve) ? extraCurve : null, time)
                * ResolveTwistScale(target.HumanBone, target.ExtraZAttribute, variant.TwistMode, solver);
            return z + extra;
        }

        private static float ResolveTwistScale(string humanBone, string attribute, string twistMode, JObject solver)
        {
            if (string.IsNullOrWhiteSpace(attribute) || string.IsNullOrWhiteSpace(twistMode))
            {
                return 1f;
            }

            if (!TryResolveTwistFamily(attribute, out var family))
            {
                return 1f;
            }

            var invert = twistMode.EndsWith("_invert", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(twistMode, "invert_all_twist", StringComparison.OrdinalIgnoreCase);
            var normalizedMode = invert ? twistMode.Replace("_invert", "", StringComparison.OrdinalIgnoreCase) : twistMode;
            var splitFamily = ResolveSplitTwistFamily(normalizedMode, out var familyMode);
            if (!string.IsNullOrWhiteSpace(splitFamily) &&
                !string.Equals(splitFamily, family, StringComparison.OrdinalIgnoreCase))
            {
                return 1f;
            }
            var effectiveMode = string.IsNullOrWhiteSpace(familyMode) ? normalizedMode : familyMode;

            var childShare = effectiveMode switch
            {
                "split_50_50" => 0.5f,
                "split_unity_child_param" => ReadTwistParameter(solver, family, 0.5f),
                "split_unity_parent_param" => 1f - ReadTwistParameter(solver, family, 0.5f),
                _ => 1f,
            };
            childShare = Math.Clamp(childShare, 0f, 1f);
            var parentShare = 1f - childShare;
            var scale = IsTwistParentTarget(humanBone, family) ? parentShare :
                IsTwistChildTarget(humanBone, family) ? childShare :
                1f;
            return invert ? -scale : scale;
        }

        private static string ResolveSplitTwistFamily(string twistMode, out string familyMode)
        {
            familyMode = null;
            if (string.IsNullOrWhiteSpace(twistMode) ||
                !twistMode.StartsWith("split_", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            foreach (var family in new[] { "upperLeg", "foreArm", "arm", "leg" })
            {
                var prefix = $"split_{family}_";
                if (twistMode.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    familyMode = "split_" + twistMode.Substring(prefix.Length);
                    return family;
                }
            }

            return null;
        }

        private static bool TryResolveTwistFamily(string attribute, out string family)
        {
            family = null;
            if (attribute.IndexOf("Upper Leg Twist", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                family = "upperLeg";
                return true;
            }
            if (attribute.IndexOf("Lower Leg Twist", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                family = "leg";
                return true;
            }
            if (attribute.IndexOf("Arm Twist", StringComparison.OrdinalIgnoreCase) >= 0 &&
                attribute.IndexOf("Forearm", StringComparison.OrdinalIgnoreCase) < 0)
            {
                family = "arm";
                return true;
            }
            if (attribute.IndexOf("Forearm Twist", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                family = "foreArm";
                return true;
            }
            return false;
        }

        private static bool IsTwistParentTarget(string humanBone, string family) => family switch
        {
            "upperLeg" => humanBone == "LeftUpperLeg" || humanBone == "RightUpperLeg",
            "leg" => humanBone == "LeftLowerLeg" || humanBone == "RightLowerLeg",
            "arm" => humanBone == "LeftUpperArm" || humanBone == "RightUpperArm",
            "foreArm" => humanBone == "LeftLowerArm" || humanBone == "RightLowerArm",
            _ => false,
        };

        private static bool IsTwistChildTarget(string humanBone, string family) => family switch
        {
            "upperLeg" => humanBone == "LeftLowerLeg" || humanBone == "RightLowerLeg",
            "leg" => humanBone == "LeftFoot" || humanBone == "RightFoot",
            "arm" => humanBone == "LeftLowerArm" || humanBone == "RightLowerArm",
            "foreArm" => humanBone == "LeftHand" || humanBone == "RightHand",
            _ => false,
        };

        private static float ReadTwistParameter(JObject solver, string family, float fallback)
        {
            var value = (float?)solver?["twist"]?[family];
            if (!value.HasValue)
            {
                return fallback;
            }
            return Math.Clamp(value.Value, 0f, 1f);
        }

        private static float[] ResolveVariantPose(
            JObject solver,
            JArray solverNodes,
            JArray humanBoneIndex,
            string humanBone,
            string targetPath,
            string poseSource,
            JObject[] gltfNodes,
            Dictionary<string, int> nodePathToIndex)
        {
            if (string.IsNullOrWhiteSpace(poseSource))
            {
                return null;
            }

            if (string.Equals(poseSource, "gltfRest", StringComparison.OrdinalIgnoreCase))
            {
                return TryGetGltfRestRotation(gltfNodes, nodePathToIndex, targetPath, out var rest) ? rest : null;
            }
            if (string.Equals(poseSource, "gltfParentRest", StringComparison.OrdinalIgnoreCase))
            {
                return TryGetGltfParentRestRotation(gltfNodes, nodePathToIndex, targetPath, out var parentRest) ? parentRest : null;
            }
            if (solver == null)
            {
                return null;
            }

            var humanNodeIndex = FindSolverNodeIndexByPath(solverNodes, targetPath);
            return poseSource switch
            {
                "humanSkeletonPose" => ReadPoseRotation(solver["skeleton"]?["humanSkeletonPose"] as JArray, humanNodeIndex),
                "humanSkeletonLocalPose" => ReadLocalPoseRotation(
                    solver["skeleton"]?["humanSkeletonPose"] as JArray,
                    solver["skeleton"]?["nodes"] as JArray,
                    humanNodeIndex),
                "avatarDefaultPoseBySameIndex" => ReadPoseRotation(solver["skeleton"]?["avatarDefaultPose"] as JArray, humanNodeIndex),
                "avatarDefaultLocalPoseBySameIndex" => ReadLocalPoseRotation(
                    solver["skeleton"]?["avatarDefaultPose"] as JArray,
                    solver["skeleton"]?["nodes"] as JArray,
                    humanNodeIndex),
                "avatarSkeletonPoseBySameIndex" => ReadPoseRotation(solver["avatarSkeleton"]?["pose"] as JArray, humanNodeIndex),
                "avatarSkeletonLocalPoseBySameIndex" => ReadLocalPoseRotation(
                    solver["avatarSkeleton"]?["pose"] as JArray,
                    solver["avatarSkeleton"]?["nodes"] as JArray,
                    humanNodeIndex),
                "avatarSkeletonDefaultPoseBySameIndex" => ReadPoseRotation(solver["avatarSkeleton"]?["defaultPose"] as JArray, humanNodeIndex),
                "avatarSkeletonDefaultLocalPoseBySameIndex" => ReadLocalPoseRotation(
                    solver["avatarSkeleton"]?["defaultPose"] as JArray,
                    solver["avatarSkeleton"]?["nodes"] as JArray,
                    humanNodeIndex),
                "avatarDefaultPoseByHumanSkeletonIndex" => ReadPoseRotation(solver["avatarSkeleton"]?["defaultPose"] as JArray, FindAvatarSkeletonIndex(solver, humanNodeIndex)),
                "avatarDefaultLocalPoseByHumanSkeletonIndex" => ReadLocalPoseRotation(
                    solver["avatarSkeleton"]?["defaultPose"] as JArray,
                    solver["avatarSkeleton"]?["nodes"] as JArray,
                    FindAvatarSkeletonIndex(solver, humanNodeIndex)),
                "avatarSkeletonPoseByHumanSkeletonIndex" => ReadPoseRotation(solver["avatarSkeleton"]?["pose"] as JArray, FindAvatarSkeletonIndex(solver, humanNodeIndex)),
                "avatarSkeletonLocalPoseByHumanSkeletonIndex" => ReadLocalPoseRotation(
                    solver["avatarSkeleton"]?["pose"] as JArray,
                    solver["avatarSkeleton"]?["nodes"] as JArray,
                    FindAvatarSkeletonIndex(solver, humanNodeIndex)),
                "humanSkeletonPoseByHumanBoneIndex" => ReadPoseRotation(solver["skeleton"]?["humanSkeletonPose"] as JArray, FindHumanNodeIndex(humanBoneIndex, humanBone)),
                "zeroBaseCrossSideAvatarDefaultA" => ResolveCrossSideZeroBaseVariantPose(
                    solver,
                    solverNodes,
                    humanBoneIndex,
                    humanBone,
                    targetPath,
                    "inverse(crossSideAvatarDefaultOppositeToTargetPose)*inverse(crossSideAvatarDefaultMirrorXYOppositePose)*avatarDefaultSameIndexParentLocalPose"),
                "zeroBaseCrossSideAvatarDefaultB" => ResolveCrossSideZeroBaseVariantPose(
                    solver,
                    solverNodes,
                    humanBoneIndex,
                    humanBone,
                    targetPath,
                    "inverse(crossSideAvatarDefaultTargetToOppositePose)*inverse(crossSideAvatarDefaultMirrorXYTargetToOppositePose)*humanSkeletonParentLocalPose"),
                _ => null,
            };
        }

        private static float[] ResolveCrossSideZeroBaseVariantPose(
            JObject solver,
            JArray solverNodes,
            JArray humanBoneIndex,
            string humanBone,
            string targetPath,
            string formula)
        {
            if (solver == null ||
                solverNodes == null ||
                string.IsNullOrWhiteSpace(humanBone) ||
                string.IsNullOrWhiteSpace(targetPath) ||
                string.IsNullOrWhiteSpace(formula))
            {
                return null;
            }

            var candidates = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            AddAvatarPoseCorrectionCandidates(candidates, targetPath, solver, solverNodes, humanBoneIndex, humanBone);
            AddCrossSidePoseCorrectionCandidates(candidates, targetPath, solver, solverNodes, humanBoneIndex, humanBone);
            return TryEvaluateCorrectionFormula(candidates, formula, out var rotation) ? rotation : null;
        }

        private static bool TryEvaluateCorrectionFormula(Dictionary<string, float[]> candidates, string formula, out float[] rotation)
        {
            rotation = null;
            if (candidates == null || string.IsNullOrWhiteSpace(formula))
            {
                return false;
            }

            var result = IdentityQuaternion;
            foreach (var rawTerm in formula.Split('*', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var term = rawTerm;
                var invert = false;
                if (term.StartsWith("inverse(", StringComparison.OrdinalIgnoreCase) &&
                    term.EndsWith(")", StringComparison.Ordinal))
                {
                    invert = true;
                    term = term.Substring("inverse(".Length, term.Length - "inverse(".Length - 1);
                }

                if (!candidates.TryGetValue(term, out var value))
                {
                    return false;
                }

                var normalized = Normalize(value);
                result = Normalize(Multiply(result, invert ? Inverse(normalized) : normalized));
            }

            rotation = result;
            return true;
        }

        private static bool TryGetGltfRestRotation(
            JObject[] gltfNodes,
            Dictionary<string, int> nodePathToIndex,
            string targetPath,
            out float[] rotation)
        {
            rotation = null;
            if (!TryGetNodeByPath(nodePathToIndex, targetPath, out _, out var nodeIndex) ||
                gltfNodes == null ||
                nodeIndex < 0 ||
                nodeIndex >= gltfNodes.Length)
            {
                return false;
            }

            rotation = GltfToUnityRotation(ReadNodeRotation(gltfNodes[nodeIndex]));
            return true;
        }

        private static bool TryGetGltfParentRestRotation(
            JObject[] gltfNodes,
            Dictionary<string, int> nodePathToIndex,
            string targetPath,
            out float[] rotation)
        {
            rotation = null;
            if (!TryGetNodeByPath(nodePathToIndex, targetPath, out _, out var nodeIndex) ||
                gltfNodes == null ||
                nodeIndex < 0 ||
                nodeIndex >= gltfNodes.Length)
            {
                return false;
            }

            var parents = BuildChildToParent(gltfNodes);
            var parentIndex = nodeIndex < parents.Length ? parents[nodeIndex] : null;
            if (!parentIndex.HasValue || parentIndex.Value < 0 || parentIndex.Value >= gltfNodes.Length)
            {
                return false;
            }

            rotation = GltfToUnityRotation(ReadNodeRotation(gltfNodes[parentIndex.Value]));
            return true;
        }

        private static int FindHumanNodeIndex(JArray humanBoneIndex, string humanBone)
        {
            if (!Enum.TryParse<BoneType>(humanBone, out var boneType) || humanBoneIndex == null)
            {
                return -1;
            }

            var humanIndex = (int)boneType;
            return humanIndex >= 0 && humanIndex < humanBoneIndex.Count
                ? (int?)humanBoneIndex[humanIndex] ?? -1
                : -1;
        }

        private static float[] ApplyVariantPose(float[] solved, float[] pose, string mode, float[] zeroSolved)
        {
            if (pose == null || string.IsNullOrWhiteSpace(mode))
            {
                return solved;
            }

            var normalizedPose = Normalize(pose);
            var normalizedZero = Normalize(zeroSolved ?? IdentityQuaternion);
            return mode switch
            {
                "left" => Normalize(Multiply(normalizedPose, solved)),
                "right" => Normalize(Multiply(solved, normalizedPose)),
                "inverse_left" => Normalize(Multiply(Inverse(normalizedPose), solved)),
                "inverse_right" => Normalize(Multiply(solved, Inverse(normalizedPose))),
                "sandwich" => Normalize(Multiply(Multiply(normalizedPose, solved), Inverse(normalizedPose))),
                "inverse_sandwich" => Normalize(Multiply(Multiply(Inverse(normalizedPose), solved), normalizedPose)),
                "zero_delta_left" => Normalize(Multiply(Multiply(normalizedPose, Inverse(normalizedZero)), solved)),
                "zero_delta_right" => Normalize(Multiply(Multiply(solved, Inverse(normalizedZero)), normalizedPose)),
                "zero_delta_sandwich" => Normalize(Multiply(Multiply(normalizedPose, Multiply(Inverse(normalizedZero), solved)), Inverse(normalizedPose))),
                _ => solved,
            };
        }

        private static float LimitMuscle(float value, SolverAxis axis, int avatarAxis)
        {
            if (avatarAxis < 0 || avatarAxis >= axis.LimitMin.Length || avatarAxis >= axis.LimitMax.Length || avatarAxis >= axis.Sign.Length)
            {
                return 0;
            }
            var range = value >= 0 ? axis.LimitMax[avatarAxis] : -axis.LimitMin[avatarAxis];
            return value * range * axis.Sign[avatarAxis];
        }

        private static float[] SwingRadiansToQuaternion(float y, float z)
        {
            var angle = Math.Sqrt(y * y + z * z);
            if (angle < 0.0000001)
            {
                return new[] { 0f, 0f, 0f, 1f };
            }
            return AxisAngleRadiansToQuaternion(0, (float)(y / angle), (float)(z / angle), (float)angle);
        }

        private static float[] AxisAngleRadiansToQuaternion(float x, float y, float z, float angle)
        {
            var half = angle * 0.5f;
            var sin = MathF.Sin(half);
            return Normalize(new[] { x * sin, y * sin, z * sin, MathF.Cos(half) });
        }

        private static readonly float[] IdentityQuaternion = { 0f, 0f, 0f, 1f };

        private static Dictionary<string, float[]> BuildResidualCorrectionCandidates(
            string targetPath,
            SolverAxis axis,
            JObject solver,
            JArray solverNodes,
            JArray humanBoneIndex,
            string humanBone,
            JObject[] gltfNodes,
            int?[] gltfParents,
            Dictionary<string, int> nodePathToIndex)
        {
            var result = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["identity"] = IdentityQuaternion,
                ["preQ"] = Normalize(axis.PreQ),
                ["inverse(preQ)"] = Inverse(axis.PreQ),
                ["postQ"] = Normalize(axis.PostQ),
                ["inverse(postQ)"] = Inverse(axis.PostQ),
                ["preQ*inverse(postQ)"] = Multiply(axis.PreQ, Inverse(axis.PostQ)),
                ["postQ*inverse(preQ)"] = Multiply(axis.PostQ, Inverse(axis.PreQ)),
            };

            AddAvatarPoseCorrectionCandidates(result, targetPath, solver, solverNodes, humanBoneIndex, humanBone);
            AddHandContextCorrectionCandidates(result, targetPath, solver, solverNodes, humanBoneIndex, humanBone);
            AddCrossSidePoseCorrectionCandidates(result, targetPath, solver, solverNodes, humanBoneIndex, humanBone);
            AddZeroDeltaCorrectionCandidates(result, "preQ*inverse(postQ)");

            if (TryGetNodeByPath(nodePathToIndex, targetPath, out _, out var nodeIndex) &&
                gltfNodes != null &&
                nodeIndex >= 0 &&
                nodeIndex < gltfNodes.Length)
            {
                var rest = GltfToUnityRotation(ReadNodeRotation(gltfNodes[nodeIndex]));
                result["rest"] = rest;
                result["inverse(rest)"] = Inverse(rest);
                result["rest*inverse(preQ*inverse(postQ))"] = Normalize(Multiply(rest, Inverse(Multiply(axis.PreQ, Inverse(axis.PostQ)))));
                result["inverse(preQ*inverse(postQ))*rest"] = Normalize(Multiply(Inverse(Multiply(axis.PreQ, Inverse(axis.PostQ))), rest));

                var parentIndex = gltfParents != null && nodeIndex < gltfParents.Length ? gltfParents[nodeIndex] : null;
                if (parentIndex.HasValue && parentIndex.Value >= 0 && parentIndex.Value < gltfNodes.Length)
                {
                    var parentRest = GltfToUnityRotation(ReadNodeRotation(gltfNodes[parentIndex.Value]));
                    var localRest = Normalize(Multiply(Inverse(parentRest), rest));
                    var parentToChildRest = Normalize(Multiply(parentRest, Inverse(rest)));
                    var childToParentRest = Normalize(Multiply(rest, Inverse(parentRest)));
                    var zero = Normalize(Multiply(axis.PreQ, Inverse(axis.PostQ)));
                    var inverseZero = Inverse(zero);
                    result["parentRest"] = parentRest;
                    result["inverse(parentRest)"] = Inverse(parentRest);
                    result["localRest"] = localRest;
                    result["inverse(localRest)"] = Inverse(localRest);
                    result["parentRest*inverse(rest)"] = parentToChildRest;
                    result["rest*inverse(parentRest)"] = childToParentRest;
                    result["parentRest*inverse(preQ*inverse(postQ))"] = Normalize(Multiply(parentRest, inverseZero));
                    result["inverse(preQ*inverse(postQ))*parentRest"] = Normalize(Multiply(inverseZero, parentRest));
                    result["localRest*inverse(preQ*inverse(postQ))"] = Normalize(Multiply(localRest, inverseZero));
                    result["inverse(preQ*inverse(postQ))*localRest"] = Normalize(Multiply(inverseZero, localRest));
                    result["parentRest*inverse(rest)*inverse(preQ*inverse(postQ))"] = Normalize(Multiply(parentToChildRest, inverseZero));
                    result["inverse(preQ*inverse(postQ))*parentRest*inverse(rest)"] = Normalize(Multiply(inverseZero, parentToChildRest));
                    result["rest*inverse(parentRest)*inverse(preQ*inverse(postQ))"] = Normalize(Multiply(childToParentRest, inverseZero));
                    result["inverse(preQ*inverse(postQ))*rest*inverse(parentRest)"] = Normalize(Multiply(inverseZero, childToParentRest));
                }
            }

            AddCrossSideRestCorrectionCandidates(result, solverNodes, humanBoneIndex, humanBone, gltfNodes, gltfParents, nodePathToIndex);
            AddZeroDeltaCorrectionCandidates(result, "preQ*inverse(postQ)");
            AddMirrorCorrectionCandidates(result, "mirrorXY");
            return result;
        }

        private static void AddCrossSideRestCorrectionCandidates(
            Dictionary<string, float[]> result,
            JArray solverNodes,
            JArray humanBoneIndex,
            string humanBone,
            JObject[] gltfNodes,
            int?[] gltfParents,
            Dictionary<string, int> nodePathToIndex)
        {
            if (result == null ||
                gltfNodes == null ||
                string.IsNullOrWhiteSpace(humanBone) ||
                !TryGetOppositeHumanBone(humanBone, out var oppositeHumanBone))
            {
                return;
            }

            var oppositeNodeIndex = TryGetHumanBoneNodeIndex(humanBoneIndex, oppositeHumanBone);
            var oppositePath = oppositeNodeIndex >= 0 && oppositeNodeIndex < (solverNodes?.Count ?? 0)
                ? (string)solverNodes[oppositeNodeIndex]?["path"]
                : null;
            if (string.IsNullOrWhiteSpace(oppositePath) ||
                !TryGetNodeByPath(nodePathToIndex, oppositePath, out _, out var oppositeGltfIndex) ||
                oppositeGltfIndex < 0 ||
                oppositeGltfIndex >= gltfNodes.Length)
            {
                return;
            }

            var oppositeRest = GltfToUnityRotation(ReadNodeRotation(gltfNodes[oppositeGltfIndex]));
            AddNamedCorrectionCandidate(result, "crossSideOppositeRest", oppositeRest);
            AddNamedCorrectionCandidate(result, "crossSideMirrorXYOppositeRest", MirrorQuaternion(oppositeRest, "mirrorXY"));

            var parentIndex = gltfParents != null && oppositeGltfIndex < gltfParents.Length ? gltfParents[oppositeGltfIndex] : null;
            if (!parentIndex.HasValue || parentIndex.Value < 0 || parentIndex.Value >= gltfNodes.Length)
            {
                return;
            }

            var parentRest = GltfToUnityRotation(ReadNodeRotation(gltfNodes[parentIndex.Value]));
            var localRest = Normalize(Multiply(Inverse(parentRest), oppositeRest));
            var parentToChildRest = Normalize(Multiply(parentRest, Inverse(oppositeRest)));
            var childToParentRest = Normalize(Multiply(oppositeRest, Inverse(parentRest)));
            AddNamedCorrectionCandidate(result, "crossSideOppositeLocalRest", localRest);
            AddNamedCorrectionCandidate(result, "crossSideMirrorXYOppositeLocalRest", MirrorQuaternion(localRest, "mirrorXY"));
            AddNamedCorrectionCandidate(result, "crossSideOppositeParentToChildRest", parentToChildRest);
            AddNamedCorrectionCandidate(result, "crossSideMirrorXYOppositeParentToChildRest", MirrorQuaternion(parentToChildRest, "mirrorXY"));
            AddNamedCorrectionCandidate(result, "crossSideOppositeChildToParentRest", childToParentRest);
            AddNamedCorrectionCandidate(result, "crossSideMirrorXYOppositeChildToParentRest", MirrorQuaternion(childToParentRest, "mirrorXY"));
        }

        private static void AddCrossSidePoseCorrectionCandidates(
            Dictionary<string, float[]> result,
            string targetPath,
            JObject solver,
            JArray solverNodes,
            JArray humanBoneIndex,
            string humanBone)
        {
            if (result == null ||
                solver == null ||
                solverNodes == null ||
                string.IsNullOrWhiteSpace(humanBone) ||
                !TryGetOppositeHumanBone(humanBone, out var oppositeHumanBone))
            {
                return;
            }

            var targetNodeIndex = FindSolverNodeIndexByPath(solverNodes, targetPath);
            var oppositeNodeIndex = TryGetHumanBoneNodeIndex(humanBoneIndex, oppositeHumanBone);
            if (targetNodeIndex < 0 || oppositeNodeIndex < 0)
            {
                return;
            }

            AddCrossSidePoseCandidates(result, "crossSideHumanSkeleton", solver["skeleton"]?["humanSkeletonPose"] as JArray, solver["skeleton"]?["nodes"] as JArray, targetNodeIndex, oppositeNodeIndex);
            AddCrossSidePoseCandidates(result, "crossSideAvatarDefault", solver["skeleton"]?["avatarDefaultPose"] as JArray, solver["skeleton"]?["nodes"] as JArray, targetNodeIndex, oppositeNodeIndex);
            AddCrossSidePoseCandidates(result, "crossSideAvatarSkeleton", solver["avatarSkeleton"]?["pose"] as JArray, solver["avatarSkeleton"]?["nodes"] as JArray, targetNodeIndex, oppositeNodeIndex);
            AddCrossSidePoseCandidates(result, "crossSideAvatarSkeletonDefault", solver["avatarSkeleton"]?["defaultPose"] as JArray, solver["avatarSkeleton"]?["nodes"] as JArray, targetNodeIndex, oppositeNodeIndex);
        }

        private static void AddCrossSidePoseCandidates(
            Dictionary<string, float[]> result,
            string prefix,
            JArray poses,
            JArray nodes,
            int targetNodeIndex,
            int oppositeNodeIndex)
        {
            if (result == null ||
                string.IsNullOrWhiteSpace(prefix) ||
                poses == null ||
                nodes == null ||
                targetNodeIndex < 0 ||
                oppositeNodeIndex < 0 ||
                targetNodeIndex >= poses.Count ||
                oppositeNodeIndex >= poses.Count ||
                targetNodeIndex >= nodes.Count ||
                oppositeNodeIndex >= nodes.Count)
            {
                return;
            }

            var targetPose = ReadPoseRotation(poses, targetNodeIndex);
            var oppositePose = ReadPoseRotation(poses, oppositeNodeIndex);
            if (targetPose == null || oppositePose == null)
            {
                return;
            }

            // 只用于诊断：前臂/小腿常表现出左右镜像空间缺口，
            // 这里检查对侧 Avatar/rest pose 是否能解释 Unity zero base。
            AddNamedCorrectionCandidate(result, prefix + "OppositePose", oppositePose);
            AddNamedCorrectionCandidate(result, prefix + "MirrorXYOppositePose", MirrorQuaternion(oppositePose, "mirrorXY"));
            var oppositeLocal = ReadLocalPoseRotation(poses, nodes, oppositeNodeIndex);
            if (oppositeLocal != null)
            {
                AddNamedCorrectionCandidate(result, prefix + "OppositeLocalPose", oppositeLocal);
                AddNamedCorrectionCandidate(result, prefix + "MirrorXYOppositeLocalPose", MirrorQuaternion(oppositeLocal, "mirrorXY"));
            }
            AddNamedCorrectionCandidate(result, prefix + "TargetToOppositePose", Normalize(Multiply(targetPose, Inverse(oppositePose))));
            AddNamedCorrectionCandidate(result, prefix + "MirrorXYTargetToOppositePose", MirrorQuaternion(Normalize(Multiply(targetPose, Inverse(oppositePose))), "mirrorXY"));
            AddNamedCorrectionCandidate(result, prefix + "OppositeToTargetPose", Normalize(Multiply(oppositePose, Inverse(targetPose))));
            AddNamedCorrectionCandidate(result, prefix + "MirrorXYOppositeToTargetPose", MirrorQuaternion(Normalize(Multiply(oppositePose, Inverse(targetPose))), "mirrorXY"));
        }

        private static bool TryGetOppositeHumanBone(string humanBone, out string oppositeHumanBone)
        {
            oppositeHumanBone = null;
            if (string.IsNullOrWhiteSpace(humanBone))
            {
                return false;
            }

            if (humanBone.StartsWith("Left", StringComparison.OrdinalIgnoreCase))
            {
                oppositeHumanBone = "Right" + humanBone["Left".Length..];
                return true;
            }
            if (humanBone.StartsWith("Right", StringComparison.OrdinalIgnoreCase))
            {
                oppositeHumanBone = "Left" + humanBone["Right".Length..];
                return true;
            }
            return false;
        }

        private static void AddHandContextCorrectionCandidates(
            Dictionary<string, float[]> result,
            string targetPath,
            JObject solver,
            JArray solverNodes,
            JArray humanBoneIndex,
            string humanBone)
        {
            if (result == null ||
                solver == null ||
                solverNodes == null ||
                humanBoneIndex == null ||
                string.IsNullOrWhiteSpace(humanBone) ||
                !humanBone.Contains("Arm", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var side = humanBone.StartsWith("Left", StringComparison.OrdinalIgnoreCase)
                ? "left"
                : humanBone.StartsWith("Right", StringComparison.OrdinalIgnoreCase)
                    ? "right"
                    : null;
            if (side == null)
            {
                return;
            }

            var targetNodeIndex = FindSolverNodeIndexByPath(solverNodes, targetPath);
            var handBoneName = string.Equals(side, "left", StringComparison.OrdinalIgnoreCase) ? "LeftHand" : "RightHand";
            var handNodeIndex = TryGetHumanBoneNodeIndex(humanBoneIndex, handBoneName);
            AddHandContextPoseCandidates(result, solver, solverNodes, targetNodeIndex, handNodeIndex, side + "HandContextHumanBone");

            var handIndexes = solver["human"]?[$"{side}HandBoneIndex"] as JArray;
            var validHandIndexes = handIndexes?
                .Values<int>()
                .Where(x => x >= 0)
                .Distinct()
                .Take(32)
                .ToArray() ?? Array.Empty<int>();
            if (validHandIndexes.Length == 0)
            {
                return;
            }

            AddHandContextPoseCandidates(result, solver, solverNodes, targetNodeIndex, validHandIndexes[0], side + "HandContextFirstIndex");
            AddHandContextPoseCandidates(result, solver, solverNodes, targetNodeIndex, validHandIndexes[^1], side + "HandContextLastIndex");

            // 中间手指节点能帮忙判断 Unity 是否用整条 hand chain 的稳定基准，而不是只看手腕。
            // 只取一个中位节点，避免候选数量在全量诊断时膨胀。
            AddHandContextPoseCandidates(result, solver, solverNodes, targetNodeIndex, validHandIndexes[validHandIndexes.Length / 2], side + "HandContextMiddleIndex");
        }

        private static int TryGetHumanBoneNodeIndex(JArray humanBoneIndex, string humanBone)
        {
            if (humanBoneIndex == null ||
                string.IsNullOrWhiteSpace(humanBone) ||
                !HumanBoneIndexes.TryGetValue(humanBone, out var humanIndex) ||
                humanIndex < 0 ||
                humanIndex >= humanBoneIndex.Count)
            {
                return -1;
            }

            return (int?)humanBoneIndex[humanIndex] ?? -1;
        }

        private static void AddHandContextPoseCandidates(
            Dictionary<string, float[]> result,
            JObject solver,
            JArray solverNodes,
            int targetNodeIndex,
            int handNodeIndex,
            string prefix)
        {
            if (result == null ||
                solver == null ||
                solverNodes == null ||
                targetNodeIndex < 0 ||
                handNodeIndex < 0)
            {
                return;
            }

            AddHandContextPoseCandidates(result, prefix + "HumanSkeleton", solver["skeleton"]?["humanSkeletonPose"] as JArray, solverNodes, targetNodeIndex, handNodeIndex);
            AddHandContextPoseCandidates(result, prefix + "AvatarDefault", solver["skeleton"]?["avatarDefaultPose"] as JArray, solverNodes, targetNodeIndex, handNodeIndex);
            AddHandContextPoseCandidates(result, prefix + "AvatarSkeleton", solver["avatarSkeleton"]?["pose"] as JArray, solver["avatarSkeleton"]?["nodes"] as JArray, targetNodeIndex, handNodeIndex);
            AddHandContextPoseCandidates(result, prefix + "AvatarSkeletonDefault", solver["avatarSkeleton"]?["defaultPose"] as JArray, solver["avatarSkeleton"]?["nodes"] as JArray, targetNodeIndex, handNodeIndex);
        }

        private static void AddHandContextPoseCandidates(
            Dictionary<string, float[]> result,
            string prefix,
            JArray poses,
            JArray nodes,
            int targetNodeIndex,
            int handNodeIndex)
        {
            if (result == null ||
                string.IsNullOrWhiteSpace(prefix) ||
                poses == null ||
                nodes == null ||
                targetNodeIndex < 0 ||
                handNodeIndex < 0 ||
                targetNodeIndex >= poses.Count ||
                handNodeIndex >= poses.Count ||
                targetNodeIndex >= nodes.Count ||
                handNodeIndex >= nodes.Count)
            {
                return;
            }

            var targetPose = ReadPoseRotation(poses, targetNodeIndex);
            var handPose = ReadPoseRotation(poses, handNodeIndex);
            if (targetPose == null || handPose == null)
            {
                return;
            }

            // 这些候选只用于 oracle 诊断：m_HandBoneIndex 是 Unity AvatarConstant 的确定性手链索引。
            // 如果它能解释前臂 zero base，再把稳定公式迁入内部 Humanoid solver。
            AddNamedCorrectionCandidate(result, prefix + "Pose", handPose);
            AddNamedCorrectionCandidate(result, prefix + "LocalPose", ReadLocalPoseRotation(poses, nodes, handNodeIndex));
            AddNamedCorrectionCandidate(result, prefix + "TargetToHandPose", Normalize(Multiply(targetPose, Inverse(handPose))));
            AddNamedCorrectionCandidate(result, prefix + "HandToTargetPose", Normalize(Multiply(handPose, Inverse(targetPose))));
        }

        private static void AddMirrorCorrectionCandidates(Dictionary<string, float[]> result, string mirrorName)
        {
            if (result == null || string.IsNullOrWhiteSpace(mirrorName))
            {
                return;
            }

            var sources = result
                .Where(x => !x.Key.StartsWith(mirrorName + "(", StringComparison.OrdinalIgnoreCase))
                .Select(x => new KeyValuePair<string, float[]>(x.Key, Normalize(x.Value)))
                .ToArray();
            foreach (var pair in sources)
            {
                result[$"{mirrorName}({pair.Key})"] = MirrorQuaternion(pair.Value, mirrorName);
            }
        }

        private static void AddZeroDeltaCorrectionCandidates(Dictionary<string, float[]> result, string zeroName)
        {
            if (result == null ||
                string.IsNullOrWhiteSpace(zeroName) ||
                !result.TryGetValue(zeroName, out var zero))
            {
                return;
            }

            var normalizedZero = Normalize(zero);
            var inverseZero = Inverse(normalizedZero);
            var sources = result
                .Where(x => IsPoseLikeCorrectionCandidate(x.Key))
                .Select(x => new KeyValuePair<string, float[]>(x.Key, Normalize(x.Value)))
                .ToArray();

            // 如果某个导出的 Avatar/rest pose 就是 Unity zero-muscle 本地姿态，
            // 那离线公式需要的固定校正应当是 pose * inverse(currentZero) 或 inverse(currentZero) * pose。
            foreach (var pair in sources)
            {
                result[$"{pair.Key}*inverse({zeroName})"] = Normalize(Multiply(pair.Value, inverseZero));
                result[$"inverse({zeroName})*{pair.Key}"] = Normalize(Multiply(inverseZero, pair.Value));
            }
        }

        private static bool IsPoseLikeCorrectionCandidate(string name)
        {
            if (string.IsNullOrWhiteSpace(name) ||
                name.StartsWith("inverse(", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("*", StringComparison.OrdinalIgnoreCase) ||
                IsCrossSideCandidateName(name))
            {
                return false;
            }
            return name.Equals("rest", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("parentRest", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Pose", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddAvatarPoseCorrectionCandidates(
            Dictionary<string, float[]> result,
            string targetPath,
            JObject solver,
            JArray solverNodes,
            JArray humanBoneIndex,
            string humanBone)
        {
            if (result == null || solver == null || solverNodes == null)
            {
                return;
            }

            var humanNodeIndex = FindSolverNodeIndexByPath(solverNodes, targetPath);
            if (humanNodeIndex >= 0)
            {
                AddPoseCandidate(result, "humanSkeletonPose", solver["skeleton"]?["humanSkeletonPose"] as JArray, humanNodeIndex);
                AddLocalPoseCandidate(result, "humanSkeletonLocalPose", solver["skeleton"]?["humanSkeletonPose"] as JArray, solver["skeleton"]?["nodes"] as JArray, humanNodeIndex);
                AddPoseChainCandidates(result, "humanSkeleton", solver["skeleton"]?["humanSkeletonPose"] as JArray, solver["skeleton"]?["nodes"] as JArray, humanNodeIndex);
                AddPoseCandidate(result, "avatarDefaultPoseBySameIndex", solver["skeleton"]?["avatarDefaultPose"] as JArray, humanNodeIndex);
                AddLocalPoseCandidate(result, "avatarDefaultLocalPoseBySameIndex", solver["skeleton"]?["avatarDefaultPose"] as JArray, solver["skeleton"]?["nodes"] as JArray, humanNodeIndex);
                AddPoseChainCandidates(result, "avatarDefaultSameIndex", solver["skeleton"]?["avatarDefaultPose"] as JArray, solver["skeleton"]?["nodes"] as JArray, humanNodeIndex);
                AddPoseCandidate(result, "avatarSkeletonPoseBySameIndex", solver["avatarSkeleton"]?["pose"] as JArray, humanNodeIndex);
                AddLocalPoseCandidate(result, "avatarSkeletonLocalPoseBySameIndex", solver["avatarSkeleton"]?["pose"] as JArray, solver["avatarSkeleton"]?["nodes"] as JArray, humanNodeIndex);
                AddPoseChainCandidates(result, "avatarSkeletonSameIndex", solver["avatarSkeleton"]?["pose"] as JArray, solver["avatarSkeleton"]?["nodes"] as JArray, humanNodeIndex);
                AddPoseCandidate(result, "avatarSkeletonDefaultPoseBySameIndex", solver["avatarSkeleton"]?["defaultPose"] as JArray, humanNodeIndex);
                AddLocalPoseCandidate(result, "avatarSkeletonDefaultLocalPoseBySameIndex", solver["avatarSkeleton"]?["defaultPose"] as JArray, solver["avatarSkeleton"]?["nodes"] as JArray, humanNodeIndex);
                AddPoseChainCandidates(result, "avatarSkeletonDefaultSameIndex", solver["avatarSkeleton"]?["defaultPose"] as JArray, solver["avatarSkeleton"]?["nodes"] as JArray, humanNodeIndex);
            }

            var avatarIndex = FindAvatarSkeletonIndex(solver, humanNodeIndex);
            if (avatarIndex >= 0)
            {
                AddPoseCandidate(result, "avatarDefaultPoseByHumanSkeletonIndex", solver["avatarSkeleton"]?["defaultPose"] as JArray, avatarIndex);
                AddLocalPoseCandidate(result, "avatarDefaultLocalPoseByHumanSkeletonIndex", solver["avatarSkeleton"]?["defaultPose"] as JArray, solver["avatarSkeleton"]?["nodes"] as JArray, avatarIndex);
                AddPoseChainCandidates(result, "avatarDefaultHumanSkeletonIndex", solver["avatarSkeleton"]?["defaultPose"] as JArray, solver["avatarSkeleton"]?["nodes"] as JArray, avatarIndex);
                AddPoseCandidate(result, "avatarSkeletonPoseByHumanSkeletonIndex", solver["avatarSkeleton"]?["pose"] as JArray, avatarIndex);
                AddLocalPoseCandidate(result, "avatarSkeletonLocalPoseByHumanSkeletonIndex", solver["avatarSkeleton"]?["pose"] as JArray, solver["avatarSkeleton"]?["nodes"] as JArray, avatarIndex);
                AddPoseChainCandidates(result, "avatarSkeletonHumanSkeletonIndex", solver["avatarSkeleton"]?["pose"] as JArray, solver["avatarSkeleton"]?["nodes"] as JArray, avatarIndex);
            }

            if (!Enum.TryParse<BoneType>(humanBone, out var boneType) || humanBoneIndex == null)
            {
                return;
            }

            var humanIndex = (int)boneType;
            if (humanIndex < 0 || humanIndex >= humanBoneIndex.Count)
            {
                return;
            }

            var mappedHumanNodeIndex = (int?)humanBoneIndex[humanIndex] ?? -1;
            if (mappedHumanNodeIndex >= 0 && mappedHumanNodeIndex != humanNodeIndex)
            {
                AddPoseCandidate(result, "humanSkeletonPoseByHumanBoneIndex", solver["skeleton"]?["humanSkeletonPose"] as JArray, mappedHumanNodeIndex);
                AddLocalPoseCandidate(result, "humanSkeletonLocalPoseByHumanBoneIndex", solver["skeleton"]?["humanSkeletonPose"] as JArray, solver["skeleton"]?["nodes"] as JArray, mappedHumanNodeIndex);
                AddPoseChainCandidates(result, "humanSkeletonHumanBoneIndex", solver["skeleton"]?["humanSkeletonPose"] as JArray, solver["skeleton"]?["nodes"] as JArray, mappedHumanNodeIndex);
            }
        }

        private static void AddPoseChainCandidates(Dictionary<string, float[]> result, string prefix, JArray poses, JArray nodes, int index)
        {
            if (result == null || string.IsNullOrWhiteSpace(prefix) || poses == null || nodes == null || index < 0 || index >= nodes.Count)
            {
                return;
            }

            var child = ReadPoseRotation(poses, index);
            if (child == null)
            {
                return;
            }

            var parentIndex = (int?)nodes[index]?["parentId"] ?? -1;
            var parent = ReadPoseRotation(poses, parentIndex);
            if (parent == null)
            {
                return;
            }

            // forearm/lowerLeg 这类远端骨骼经常暴露父子空间缺口。
            // 这些候选只进诊断排名，用来判断 Unity 内部公式是否依赖父 pose 或父子 delta。
            AddNamedCorrectionCandidate(result, prefix + "ParentPose", parent);
            AddNamedCorrectionCandidate(result, prefix + "ParentLocalPose", ReadLocalPoseRotation(poses, nodes, parentIndex));
            AddNamedCorrectionCandidate(result, prefix + "ParentToChildPose", Normalize(Multiply(parent, Inverse(child))));
            AddNamedCorrectionCandidate(result, prefix + "ChildToParentPose", Normalize(Multiply(child, Inverse(parent))));
        }

        private static void AddNamedCorrectionCandidate(Dictionary<string, float[]> result, string name, float[] rotation)
        {
            if (result == null || string.IsNullOrWhiteSpace(name) || rotation == null)
            {
                return;
            }

            var normalized = Normalize(rotation);
            result[name] = normalized;
            result["inverse(" + name + ")"] = Inverse(normalized);
        }

        private static void AddLocalPoseCandidate(Dictionary<string, float[]> result, string name, JArray poses, JArray nodes, int index)
        {
            var rotation = ReadLocalPoseRotation(poses, nodes, index);
            if (rotation == null)
            {
                return;
            }

            result[name] = rotation;
            result["inverse(" + name + ")"] = Inverse(rotation);
        }

        private static int FindSolverNodeIndexByPath(JArray solverNodes, string targetPath)
        {
            if (solverNodes == null || string.IsNullOrWhiteSpace(targetPath))
            {
                return -1;
            }

            for (var i = 0; i < solverNodes.Count; i++)
            {
                if (solverNodes[i] is not JObject node)
                {
                    continue;
                }

                var path = (string)node["path"];
                if (string.Equals(NormalizeBakePath(path), NormalizeBakePath(targetPath), StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            return -1;
        }

        private static int FindAvatarSkeletonIndex(JObject solver, int humanNodeIndex)
        {
            if (solver == null || humanNodeIndex < 0)
            {
                return -1;
            }

            var forward = solver["humanSkeletonIndexArray"] as JArray;
            if (forward != null)
            {
                for (var avatarIndex = 0; avatarIndex < forward.Count; avatarIndex++)
                {
                    if (((int?)forward[avatarIndex] ?? -1) == humanNodeIndex)
                    {
                        return avatarIndex;
                    }
                }
            }

            var reverse = solver["humanSkeletonReverseIndexArray"] as JArray;
            if (reverse != null && humanNodeIndex < reverse.Count)
            {
                return (int?)reverse[humanNodeIndex] ?? -1;
            }

            return -1;
        }

        private static void AddPoseCandidate(Dictionary<string, float[]> result, string name, JArray poses, int index)
        {
            var rotation = ReadPoseRotation(poses, index);
            if (rotation == null)
            {
                return;
            }

            result[name] = rotation;
            result["inverse(" + name + ")"] = Inverse(rotation);
        }

        private static float[] ReadPoseRotation(JArray poses, int index)
        {
            if (poses == null || index < 0 || index >= poses.Count || poses[index] is not JObject pose)
            {
                return null;
            }

            return Normalize(ReadFloatArray(pose["q"] as JArray, 4));
        }

        private static float[] ReadLocalPoseRotation(JArray poses, JArray nodes, int index)
        {
            var child = ReadPoseRotation(poses, index);
            if (child == null)
            {
                return null;
            }

            var parentIndex = (int?)nodes?[index]?["parentId"] ?? -1;
            var parent = ReadPoseRotation(poses, parentIndex);
            return parent == null
                ? child
                : Normalize(Multiply(Inverse(parent), child));
        }

        private static CorrectionCandidateMatch FindNearestCorrectionCandidate(float[] correction, Dictionary<string, float[]> candidates)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return new CorrectionCandidateMatch(null, 0);
            }

            var bestName = "";
            var bestAngle = float.MaxValue;
            foreach (var pair in candidates)
            {
                var angle = QuaternionAngleDegrees(correction, pair.Value);
                if (angle >= bestAngle)
                {
                    continue;
                }
                bestName = pair.Key;
                bestAngle = angle;
            }
            return new CorrectionCandidateMatch(bestName, bestAngle);
        }

        private static Dictionary<int, GltfRotationTrack> LoadRotationChannels(JObject gltf, string gltfDir)
        {
            var animations = gltf["animations"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            if (animations.Length == 0)
            {
                return new Dictionary<int, GltfRotationTrack>();
            }

            var buffers = LoadBuffers(gltf, gltfDir);
            var result = new Dictionary<int, GltfRotationTrack>();
            foreach (var animation in animations)
            {
                var samplers = animation["samplers"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
                foreach (var channel in animation["channels"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    if (!string.Equals((string)channel["target"]?["path"], "rotation", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var node = (int?)channel["target"]?["node"] ?? -1;
                    var samplerIndex = (int?)channel["sampler"] ?? -1;
                    if (node < 0 || samplerIndex < 0 || samplerIndex >= samplers.Length || result.ContainsKey(node))
                    {
                        continue;
                    }

                    var sampler = samplers[samplerIndex];
                    var times = ReadAccessorFloats(gltf, buffers, (int?)sampler["input"] ?? -1)
                        .Select(x => x.Length > 0 ? x[0] : 0f)
                        .ToArray();
                    var rotations = ReadAccessorFloats(gltf, buffers, (int?)sampler["output"] ?? -1)
                        .Where(x => x.Length >= 4)
                        .Select(x => Normalize(new[] { x[0], x[1], x[2], x[3] }))
                        .ToArray();
                    if (times.Length > 0 && times.Length == rotations.Length)
                    {
                        result[node] = new GltfRotationTrack(times, rotations);
                    }
                }
            }
            return result;
        }

        private static byte[][] LoadBuffers(JObject gltf, string gltfDir)
        {
            var buffers = gltf["buffers"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var result = new byte[buffers.Length][];
            for (var i = 0; i < buffers.Length; i++)
            {
                var uri = (string)buffers[i]["uri"];
                if (string.IsNullOrWhiteSpace(uri))
                {
                    throw new InvalidOperationException("Only file-buffer/data-uri glTF is supported by the compare diagnostic.");
                }
                if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    var comma = uri.IndexOf(',');
                    result[i] = Convert.FromBase64String(uri[(comma + 1)..]);
                    continue;
                }
                result[i] = File.ReadAllBytes(Path.Combine(gltfDir, uri.Replace('/', Path.DirectorySeparatorChar)));
            }
            return result;
        }

        private static float[][] ReadAccessorFloats(JObject gltf, byte[][] buffers, int accessorIndex)
        {
            var accessors = gltf["accessors"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var views = gltf["bufferViews"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            if (accessorIndex < 0 || accessorIndex >= accessors.Length)
            {
                return Array.Empty<float[]>();
            }

            var accessor = accessors[accessorIndex];
            if ((int?)accessor["componentType"] != 5126)
            {
                throw new InvalidOperationException("Compare diagnostic currently supports FLOAT glTF accessors only.");
            }

            var viewIndex = (int?)accessor["bufferView"] ?? -1;
            if (viewIndex < 0 || viewIndex >= views.Length)
            {
                return Array.Empty<float[]>();
            }

            var type = (string)accessor["type"] ?? "SCALAR";
            var componentCount = type switch
            {
                "SCALAR" => 1,
                "VEC2" => 2,
                "VEC3" => 3,
                "VEC4" => 4,
                _ => throw new InvalidOperationException($"Unsupported accessor type: {type}"),
            };
            var count = (int?)accessor["count"] ?? 0;
            var view = views[viewIndex];
            var bufferIndex = (int?)view["buffer"] ?? 0;
            var stride = (int?)view["byteStride"] ?? componentCount * 4;
            var baseOffset = ((int?)view["byteOffset"] ?? 0) + ((int?)accessor["byteOffset"] ?? 0);
            var buffer = buffers[bufferIndex];
            var rows = new float[count][];
            for (var row = 0; row < count; row++)
            {
                rows[row] = new float[componentCount];
                var offset = baseOffset + row * stride;
                for (var c = 0; c < componentCount; c++)
                {
                    rows[row][c] = BitConverter.ToSingle(buffer, offset + c * 4);
                }
            }
            return rows;
        }

        private static UnityRotationKey[] ReadUnityRotationKeys(JToken values) =>
            values?.OfType<JObject>()
                .Select(x => new UnityRotationKey(
                    (float?)x["time"] ?? 0f,
                    Normalize(new[] { (float)x["x"], (float)x["y"], (float)x["z"], (float)x["w"] })
                ))
                .ToArray() ?? Array.Empty<UnityRotationKey>();

        private static Dictionary<string, UnityRotationKey[]> LoadUnityPlayableRotationTracks(JArray tracks)
        {
            var result = new Dictionary<string, UnityRotationKey[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var track in tracks?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                if ((bool?)track["changed"] != true)
                {
                    continue;
                }

                var path = NormalizeBakePath((string)track["path"]);
                var rotations = ReadUnityRotationKeys(track["rotations"]);
                if (string.IsNullOrWhiteSpace(path) || rotations.Length == 0)
                {
                    continue;
                }

                result[path] = rotations
                    .OrderBy(x => x.Time)
                    .ToArray();
            }
            return result;
        }

        private static bool TryGetPlayableRotationTrack(
            Dictionary<string, UnityRotationKey[]> tracks,
            string path,
            out string matchedPath,
            out UnityRotationKey[] keys)
        {
            var normalized = NormalizeBakePath(path);
            if (tracks.TryGetValue(normalized, out keys))
            {
                matchedPath = normalized;
                return true;
            }

            var suffix = "/" + normalized;
            var matches = tracks
                .Where(x => x.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                    || normalized.EndsWith("/" + x.Key, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            if (matches.Length == 1)
            {
                matchedPath = matches[0].Key;
                keys = matches[0].Value;
                return true;
            }

            matchedPath = null;
            keys = null;
            return false;
        }

        private static float[] SampleUnityRotation(UnityRotationKey[] keys, float time)
        {
            if (keys == null || keys.Length == 0)
            {
                return new[] { 0f, 0f, 0f, 1f };
            }
            if (keys.Length == 1 || time <= keys[0].Time)
            {
                return keys[0].Rotation;
            }
            var last = keys.Length - 1;
            if (time >= keys[last].Time)
            {
                return keys[last].Rotation;
            }
            for (var i = 1; i < keys.Length; i++)
            {
                if (keys[i].Time < time)
                {
                    continue;
                }
                var t0 = keys[i - 1].Time;
                var t1 = keys[i].Time;
                var factor = Math.Abs(t1 - t0) < 0.000001f ? 0f : (time - t0) / (t1 - t0);
                return Nlerp(keys[i - 1].Rotation, keys[i].Rotation, factor);
            }
            return keys[last].Rotation;
        }

        private static float[] ReadQuaternion(JObject value) => Normalize(new[]
        {
            (float?)value["x"] ?? 0f,
            (float?)value["y"] ?? 0f,
            (float?)value["z"] ?? 0f,
            (float?)value["w"] ?? 1f,
        });

        private static bool TryGetRotationByPath(
            Dictionary<string, float[]> rotations,
            string path,
            out string matchedPath,
            out float[] rotation)
        {
            var normalized = NormalizeBakePath(path);
            if (rotations.TryGetValue(normalized, out rotation))
            {
                matchedPath = normalized;
                return true;
            }

            var suffix = "/" + normalized;
            var matches = rotations
                .Where(x => x.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            if (matches.Length == 1)
            {
                matchedPath = matches[0].Key;
                rotation = matches[0].Value;
                return true;
            }

            matchedPath = null;
            rotation = null;
            return false;
        }

        private static Dictionary<string, List<RotationKey>> LoadDecodedRotationTracks(JArray tracks)
        {
            var result = new Dictionary<string, List<RotationKey>>(StringComparer.OrdinalIgnoreCase);
            foreach (var track in tracks?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var path = NormalizeBakePath((string)track["path"]);
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var keys = track["keyframes"]?
                    .OfType<JObject>()
                    .Select(x => new RotationKey((float?)x["time"] ?? 0f, ReadQuaternionArray(x["value"] as JArray)))
                    .Where(x => x.Rotation != null)
                    .OrderBy(x => x.Time)
                    .ToList() ?? new List<RotationKey>();
                if (keys.Count > 0)
                {
                    result[path] = keys;
                }
            }
            return result;
        }

        private static bool TryGetDecodedRotationTrack(
            Dictionary<string, List<RotationKey>> tracks,
            string path,
            out string matchedPath,
            out List<RotationKey> track)
        {
            var normalized = NormalizeBakePath(path);
            if (tracks.TryGetValue(normalized, out track))
            {
                matchedPath = normalized;
                return true;
            }

            var suffix = "/" + normalized;
            var matches = tracks
                .Where(x =>
                    x.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                    normalized.EndsWith("/" + x.Key, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            if (matches.Length == 1)
            {
                matchedPath = matches[0].Key;
                track = matches[0].Value;
                return true;
            }

            matchedPath = null;
            track = null;
            return false;
        }

        private static float[] SampleDecodedRotation(List<RotationKey> keys, float time)
        {
            if (keys == null || keys.Count == 0)
            {
                return IdentityQuaternion;
            }
            if (keys.Count == 1 || time <= keys[0].Time)
            {
                return keys[0].Rotation;
            }
            var last = keys.Count - 1;
            if (time >= keys[last].Time)
            {
                return keys[last].Rotation;
            }

            for (var i = 1; i < keys.Count; i++)
            {
                if (keys[i].Time < time)
                {
                    continue;
                }
                var a = keys[i - 1];
                var b = keys[i];
                var span = b.Time - a.Time;
                var t = span <= 0 ? 0 : (time - a.Time) / span;
                return Nlerp(a.Rotation, b.Rotation, t);
            }
            return keys[last].Rotation;
        }

        private static float[] ReadQuaternionArray(JArray value)
        {
            if (value == null || value.Count < 4)
            {
                return null;
            }

            return Normalize(new[]
            {
                (float?)value[0] ?? 0f,
                (float?)value[1] ?? 0f,
                (float?)value[2] ?? 0f,
                (float?)value[3] ?? 1f,
            });
        }

        private static bool TryGetNodeByPath(
            Dictionary<string, int> nodePathToIndex,
            string path,
            out string matchedPath,
            out int nodeIndex)
        {
            var normalized = NormalizeBakePath(path);
            if (nodePathToIndex != null && nodePathToIndex.TryGetValue(normalized, out nodeIndex))
            {
                matchedPath = normalized;
                return true;
            }

            var suffix = "/" + normalized;
            var matches = (nodePathToIndex ?? new Dictionary<string, int>())
                .Where(x => x.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            if (matches.Length == 1)
            {
                matchedPath = matches[0].Key;
                nodeIndex = matches[0].Value;
                return true;
            }

            matchedPath = null;
            nodeIndex = -1;
            return false;
        }

        private static Dictionary<string, int> BuildNodePathIndex(JObject[] nodes)
        {
            var parents = BuildChildToParent(nodes);
            var paths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < nodes.Length; i++)
            {
                paths[BuildNodePath(nodes, i, parents)] = i;
            }
            return paths;
        }

        private static int?[] BuildChildToParent(JObject[] nodes)
        {
            var parents = new int?[nodes.Length];
            for (var i = 0; i < nodes.Length; i++)
            {
                foreach (var child in nodes[i]["children"]?.Values<int>() ?? Enumerable.Empty<int>())
                {
                    if (child >= 0 && child < parents.Length)
                    {
                        parents[child] = i;
                    }
                }
            }
            return parents;
        }

        private static string BuildNodePath(JObject[] nodes, int index, int?[] parents)
        {
            var stack = new Stack<string>();
            int? current = index;
            while (current.HasValue)
            {
                stack.Push((string)nodes[current.Value]["name"] ?? $"node_{current.Value}");
                current = parents[current.Value];
            }
            return string.Join("/", stack);
        }

        private static string NormalizeBakePath(string bakePath)
        {
            if (string.IsNullOrWhiteSpace(bakePath))
            {
                return bakePath;
            }
            var parts = bakePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (parts.Count > 0 && parts[0].EndsWith("_AnimeStudioBake", StringComparison.OrdinalIgnoreCase))
            {
                var bakedRoot = parts[0];
                parts.RemoveAt(0);
                if (parts.Count == 0)
                {
                    return bakedRoot[..^"_AnimeStudioBake".Length];
                }
            }
            return string.Join("/", parts);
        }

        private static float[] UnityToGltfRotation(float[] value) => Normalize(new[] { value[0], -value[1], -value[2], value[3] });

        private static float[] GltfToUnityRotation(float[] value) => Normalize(new[] { value[0], -value[1], -value[2], value[3] });

        private static float[] GltfToUnityPosition(float[] value) => new[] { -value[0], value[1], value[2] };

        private static float VectorDistance(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length < 3 || b.Length < 3)
            {
                return 0f;
            }
            var dx = a[0] - b[0];
            var dy = a[1] - b[1];
            var dz = a[2] - b[2];
            return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static JArray ToJArray(float[] values)
        {
            var array = new JArray();
            foreach (var value in values ?? Array.Empty<float>())
            {
                array.Add(value);
            }
            return array;
        }

        private static bool TryGetGltfRestTranslation(
            JObject[] gltfNodes,
            Dictionary<string, int> nodePathToIndex,
            string targetPath,
            out float[] translation)
        {
            translation = null;
            if (!TryGetNodeByPath(nodePathToIndex, targetPath, out _, out var nodeIndex) ||
                gltfNodes == null ||
                nodeIndex < 0 ||
                nodeIndex >= gltfNodes.Length)
            {
                return false;
            }

            translation = GltfToUnityPosition(ReadNodeTranslation(gltfNodes[nodeIndex]));
            return true;
        }

        private static float[] ReadNodeTranslation(JObject node)
        {
            var value = node?["translation"] as JArray;
            if (value == null || value.Count < 3)
            {
                return new[] { 0f, 0f, 0f };
            }
            return new[]
            {
                (float?)value[0] ?? 0f,
                (float?)value[1] ?? 0f,
                (float?)value[2] ?? 0f,
            };
        }

        private static float[] ReadNodeRotation(JObject node)
        {
            var value = node?["rotation"] as JArray;
            if (value == null || value.Count < 4)
            {
                return new[] { 0f, 0f, 0f, 1f };
            }
            return Normalize(new[]
            {
                (float?)value[0] ?? 0f,
                (float?)value[1] ?? 0f,
                (float?)value[2] ?? 0f,
                (float?)value[3] ?? 1f,
            });
        }

        private static float ResolveFirstEditorCurveSampleTime(JObject unityResult)
        {
            foreach (var track in unityResult?["editorCurveTracks"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var first = track["values"]?.OfType<JObject>().FirstOrDefault();
                if (first != null)
                {
                    return (float?)first["time"] ?? 0f;
                }
            }
            return 0f;
        }

        private static float[] SampleRotation(GltfRotationTrack track, float time)
        {
            if (track.Times.Length == 1 || time <= track.Times[0])
            {
                return track.Rotations[0];
            }
            var last = track.Times.Length - 1;
            if (time >= track.Times[last])
            {
                return track.Rotations[last];
            }
            for (var i = 1; i < track.Times.Length; i++)
            {
                if (track.Times[i] < time)
                {
                    continue;
                }
                var t0 = track.Times[i - 1];
                var t1 = track.Times[i];
                var factor = Math.Abs(t1 - t0) < 0.000001f ? 0f : (time - t0) / (t1 - t0);
                return Nlerp(track.Rotations[i - 1], track.Rotations[i], factor);
            }
            return track.Rotations[last];
        }

        private static float[] Nlerp(float[] a, float[] b, float t)
        {
            var dot = Dot(a, b);
            var sign = dot < 0 ? -1f : 1f;
            return Normalize(new[]
            {
                a[0] + (b[0] * sign - a[0]) * t,
                a[1] + (b[1] * sign - a[1]) * t,
                a[2] + (b[2] * sign - a[2]) * t,
                a[3] + (b[3] * sign - a[3]) * t,
            });
        }

        private static float[] Multiply(float[] a, float[] b)
        {
            return Normalize(new[]
            {
                a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
                a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
                a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3],
                a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2],
            });
        }

        private static float[] Inverse(float[] value)
        {
            var q = Normalize(value);
            return new[] { -q[0], -q[1], -q[2], q[3] };
        }

        private static float QuaternionAngleDegrees(float[] a, float[] b)
        {
            var dot = Math.Clamp(Math.Abs(Dot(Normalize(a), Normalize(b))), 0f, 1f);
            return (float)(2.0 * Math.Acos(dot) * 180.0 / Math.PI);
        }

        private static float Dot(float[] a, float[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3];

        private static float[] Normalize(float[] value)
        {
            var length = Math.Sqrt(Dot(value, value));
            if (length < 0.0000001)
            {
                return new[] { 0f, 0f, 0f, 1f };
            }
            return new[] { (float)(value[0] / length), (float)(value[1] / length), (float)(value[2] / length), (float)(value[3] / length) };
        }

        private static float[] ReadFloatArray(JArray values, int count)
        {
            if (values == null || values.Count < count)
            {
                return null;
            }
            var result = new float[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = (float?)values[i] ?? 0f;
            }
            return result;
        }

        private static float[] ReadFloatArrayAll(JArray values)
        {
            return values?.Select(x => (float?)x ?? 0f).ToArray() ?? Array.Empty<float>();
        }

        private static bool IsBodyBone(string path)
        {
            var name = path ?? string.Empty;
            return Contains(name, "Pelvis") ||
                   Contains(name, "Spine") ||
                   Contains(name, "Thigh") ||
                   Contains(name, "Calf") ||
                   Contains(name, "Foot") ||
                   Contains(name, "Toe") ||
                   Contains(name, "Clavicle") ||
                   Contains(name, "UpperArm") ||
                   Contains(name, "Forearm") ||
                   Contains(name, "Hand") ||
                   Contains(name, "Neck") ||
                   Contains(name, "Head");
        }

        private static bool Contains(string value, string text) => value.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;

        private static JObject BuildBodyGroupErrorSummary(IEnumerable<TrackCompareRow> rows)
        {
            var result = new JObject();
            foreach (var group in rows
                .GroupBy(x => ClassifyBodyGroup(x.UnityPath))
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                var items = group.ToArray();
                if (items.Length == 0)
                {
                    continue;
                }

                result[group.Key] = new JObject
                {
                    ["count"] = items.Length,
                    ["maxDegrees"] = items.Max(x => x.MaxRotationErrorDegrees),
                    ["avgTrackMaxDegrees"] = items.Average(x => x.MaxRotationErrorDegrees),
                    ["worstPath"] = items
                        .OrderByDescending(x => x.MaxRotationErrorDegrees)
                        .ThenBy(x => x.UnityPath, StringComparer.OrdinalIgnoreCase)
                        .First()
                        .UnityPath,
                };
            }
            return result;
        }

        private static string ClassifyBodyGroup(string path)
        {
            var name = path ?? string.Empty;
            if (Contains(name, "Thigh") || Contains(name, "Calf") || Contains(name, "Foot") || Contains(name, "Toe") ||
                Contains(name, "UpperLeg") || Contains(name, "LowerLeg") || Contains(name, "Toes"))
            {
                return "Leg";
            }
            if (Contains(name, "Finger") || Contains(name, "Hand") || Contains(name, "DMZ") || Contains(name, "Weapon"))
            {
                return "Hand";
            }
            if (Contains(name, "Clavicle") || Contains(name, "UpperArm") || Contains(name, "Forearm") ||
                Contains(name, "Shoulder") || Contains(name, "LowerArm"))
            {
                return "Arm";
            }
            if (Contains(name, "Spine") || Contains(name, "Pelvis") || Contains(name, "Neck") || Contains(name, "Head"))
            {
                return "SpineHead";
            }
            return "Other";
        }

        private static JArray ToJsonRows(IEnumerable<TrackCompareRow> rows)
        {
            var array = new JArray();
            foreach (var row in rows)
            {
                array.Add(new JObject
                {
                    ["unityPath"] = row.UnityPath,
                    ["gltfPath"] = row.GltfPath,
                    ["node"] = row.NodeIndex,
                    ["sampleCount"] = row.SampleCount,
                    ["maxRotationErrorDegrees"] = row.MaxRotationErrorDegrees,
                    ["avgRotationErrorDegrees"] = row.AvgRotationErrorDegrees,
                    ["bodyBone"] = row.BodyBone,
                });
            }
            return array;
        }

        private static Dictionary<string, List<FloatKey>> LoadDecodedFloatCurves(JArray floats)
        {
            var result = new Dictionary<string, List<FloatKey>>(StringComparer.OrdinalIgnoreCase);
            foreach (var curve in floats?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var attribute = (string)curve["attribute"];
                if (string.IsNullOrWhiteSpace(attribute))
                {
                    continue;
                }

                var keys = curve["keyframes"]?.OfType<JObject>()
                    .Select(x => new FloatKey((float?)x["time"] ?? 0f, (float?)x["value"] ?? 0f))
                    .OrderBy(x => x.Time)
                    .ToList() ?? new List<FloatKey>();
                if (keys.Count > 0)
                {
                    result[attribute] = keys;
                }
            }
            return result;
        }

        private static Dictionary<string, List<FloatKey>> LoadUnityEditorCurveTracks(JArray tracks)
        {
            var result = new Dictionary<string, List<FloatKey>>(StringComparer.OrdinalIgnoreCase);
            foreach (var track in tracks?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var propertyName = (string)track["propertyName"];
                if (string.IsNullOrWhiteSpace(propertyName))
                {
                    continue;
                }

                var keys = track["values"]?.OfType<JObject>()
                    .Select(x => new FloatKey((float?)x["time"] ?? 0f, (float?)x["value"] ?? 0f))
                    .OrderBy(x => x.Time)
                    .ToList() ?? new List<FloatKey>();
                if (keys.Count > 0)
                {
                    result[propertyName] = keys;
                }
            }
            return result;
        }

        private static float SampleFloatCurve(List<FloatKey> keys, float time)
        {
            if (keys == null || keys.Count == 0)
            {
                return 0f;
            }
            if (time <= keys[0].Time)
            {
                return keys[0].Value;
            }
            if (time >= keys[^1].Time)
            {
                return keys[^1].Value;
            }
            for (var i = 1; i < keys.Count; i++)
            {
                if (time > keys[i].Time)
                {
                    continue;
                }
                var a = keys[i - 1];
                var b = keys[i];
                var factor = Math.Abs(b.Time - a.Time) < 0.000001f ? 0f : (time - a.Time) / (b.Time - a.Time);
                return a.Value + (b.Value - a.Value) * factor;
            }
            return keys[^1].Value;
        }

        private static JArray ToJsonRows(IEnumerable<MuscleCompareRow> rows)
        {
            var array = new JArray();
            foreach (var row in rows)
            {
                array.Add(new JObject
                {
                    ["name"] = row.Name,
                    ["muscleIndex"] = row.MuscleIndex,
                    ["sampleCount"] = row.SampleCount,
                    ["maxAbsError"] = row.MaxAbsError,
                    ["avgAbsError"] = row.AvgAbsError,
                });
            }
            return array;
        }

        private static JArray ToJsonRows(IEnumerable<SolverResidualRow> rows)
        {
            var array = new JArray();
            foreach (var row in rows)
            {
                array.Add(new JObject
                {
                    ["path"] = row.Path,
                    ["sampleCount"] = row.SampleCount,
                    ["rawMaxDegrees"] = row.RawMaxDegrees,
                    ["rawAvgDegrees"] = row.RawAvgDegrees,
                    ["leftCorrectionQuaternion"] = ToJArray(row.LeftCorrection),
                    ["leftCorrectionAngleDegrees"] = row.LeftCorrectionAngleDegrees,
                    ["leftCorrectedMaxDegrees"] = row.LeftCorrectedMaxDegrees,
                    ["leftCorrectedAvgDegrees"] = row.LeftCorrectedAvgDegrees,
                    ["leftDynamicResidualAxis"] = row.LeftDynamicResidualAxis,
                    ["nearestLeftCorrectionSource"] = row.NearestLeftCorrectionSource,
                    ["nearestLeftCorrectionSourceErrorDegrees"] = row.NearestLeftCorrectionSourceErrorDegrees,
                    ["rightCorrectionQuaternion"] = ToJArray(row.RightCorrection),
                    ["rightCorrectionAngleDegrees"] = row.RightCorrectionAngleDegrees,
                    ["rightCorrectedMaxDegrees"] = row.RightCorrectedMaxDegrees,
                    ["rightCorrectedAvgDegrees"] = row.RightCorrectedAvgDegrees,
                    ["rightDynamicResidualAxis"] = row.RightDynamicResidualAxis,
                    ["nearestRightCorrectionSource"] = row.NearestRightCorrectionSource,
                    ["nearestRightCorrectionSourceErrorDegrees"] = row.NearestRightCorrectionSourceErrorDegrees,
                    ["bodyGroup"] = ClassifyBodyGroup(row.Path),
                });
            }
            return array;
        }

        private static JArray ToJsonRows(IEnumerable<SingleMuscleDeltaCorrectionRow> rows)
        {
            var array = new JArray();
            foreach (var row in rows)
            {
                array.Add(new JObject
                {
                    ["humanBone"] = row.HumanBone,
                    ["attribute"] = row.Attribute,
                    ["value"] = row.Value,
                    ["targetPath"] = row.TargetPath,
                    ["probePath"] = row.ProbePath,
                    ["leftErrorDegrees"] = row.LeftErrorDegrees,
                    ["rightErrorDegrees"] = row.RightErrorDegrees,
                    ["currentErrorDegrees"] = row.CurrentErrorDegrees,
                    ["bestApplyMode"] = row.BestApplyMode,
                    ["bestCorrectionQuaternion"] = ToJArray(row.BestCorrection),
                    ["bestCorrectionAngleDegrees"] = row.BestCorrectionAngleDegrees,
                    ["leftCorrectionQuaternion"] = ToJArray(row.LeftCorrection),
                    ["leftCorrectionAngleDegrees"] = row.LeftCorrectionAngleDegrees,
                    ["rightCorrectionQuaternion"] = ToJArray(row.RightCorrection),
                    ["rightCorrectionAngleDegrees"] = row.RightCorrectionAngleDegrees,
                    ["bodyGroup"] = ClassifyBodyGroup(row.TargetPath),
                });
            }
            return array;
        }

        private static JArray ToJsonRows(IEnumerable<MuscleCombinationResidualRow> rows)
        {
            var array = new JArray();
            foreach (var row in rows ?? Enumerable.Empty<MuscleCombinationResidualRow>())
            {
                array.Add(new JObject
                {
                    ["probeName"] = row.ProbeName,
                    ["probeFamily"] = row.ProbeFamily,
                    ["humanBone"] = row.HumanBone,
                    ["targetPath"] = row.TargetPath,
                    ["probePath"] = row.ProbePath,
                    ["bodyGroup"] = ClassifyBodyGroup(row.TargetPath),
                    ["attributes"] = new JArray(row.Attributes ?? Array.Empty<string>()),
                    ["bestApplyMode"] = row.BestApplyMode,
                    ["currentErrorDegrees"] = row.CurrentErrorDegrees,
                    ["leftCorrectionQuaternion"] = ToJArray(row.LeftCorrection),
                    ["leftCorrectionAngleDegrees"] = row.LeftCorrectionAngleDegrees,
                    ["rightCorrectionQuaternion"] = ToJArray(row.RightCorrection),
                    ["rightCorrectionAngleDegrees"] = row.RightCorrectionAngleDegrees,
                    ["bestCorrectionQuaternion"] = ToJArray(row.BestCorrection),
                    ["bestCorrectionAngleDegrees"] = row.BestCorrectionAngleDegrees,
                });
            }
            return array;
        }

        private static JArray ToJsonRows(IEnumerable<UnityCombinationCompositionRow> rows)
        {
            var array = new JArray();
            foreach (var row in rows ?? Enumerable.Empty<UnityCombinationCompositionRow>())
            {
                array.Add(new JObject
                {
                    ["probeName"] = row.ProbeName,
                    ["probeFamily"] = row.ProbeFamily,
                    ["path"] = row.Path,
                    ["bodyGroup"] = ClassifyBodyGroup(row.Path),
                    ["muscles"] = new JArray(row.MuscleNames ?? Array.Empty<string>()),
                    ["bestMode"] = row.BestMode,
                    ["bestOrder"] = row.BestOrder,
                    ["bestErrorDegrees"] = row.BestErrorDegrees,
                    ["comparedTo"] = row.ComparedTo,
                    ["comboDeltaAngleDegrees"] = row.ComboDeltaAngleDegrees,
                });
            }
            return array;
        }

        private static JArray ToJsonRows(IEnumerable<SingleMuscleDeltaAxisRow> rows)
        {
            var array = new JArray();
            foreach (var row in rows)
            {
                array.Add(new JObject
                {
                    ["humanBone"] = row.HumanBone,
                    ["attribute"] = row.Attribute,
                    ["value"] = row.Value,
                    ["targetPath"] = row.TargetPath,
                    ["probePath"] = row.ProbePath,
                    ["predictedAxis"] = ToJArray(row.PredictedAxis),
                    ["predictedAngleDegrees"] = row.PredictedAngleDegrees,
                    ["unityAxis"] = ToJArray(row.UnityAxis),
                    ["unityAngleDegrees"] = row.UnityAngleDegrees,
                    ["axisErrorDegrees"] = row.AxisErrorDegrees,
                    ["angleRatio"] = row.AngleRatio,
                    ["currentErrorDegrees"] = row.CurrentErrorDegrees,
                    ["bestApplyMode"] = row.BestApplyMode,
                    ["bodyGroup"] = ClassifyBodyGroup(row.TargetPath),
                });
            }
            return array;
        }

        private static JArray ToJsonRows(IEnumerable<SingleMuscleAvatarAxisProjectionRow> rows)
        {
            var array = new JArray();
            foreach (var row in rows)
            {
                array.Add(new JObject
                {
                    ["projectionMode"] = row.ProjectionMode,
                    ["humanBone"] = row.HumanBone,
                    ["attribute"] = row.Attribute,
                    ["value"] = row.Value,
                    ["targetPath"] = row.TargetPath,
                    ["probePath"] = row.ProbePath,
                    ["predictedSignedAxis"] = row.PredictedSignedAxis,
                    ["predictedNearestAxisErrorDegrees"] = row.PredictedNearestAxisErrorDegrees,
                    ["predictedAngleDegrees"] = row.PredictedAngleDegrees,
                    ["unitySignedAxis"] = row.UnitySignedAxis,
                    ["unityNearestAxisErrorDegrees"] = row.UnityNearestAxisErrorDegrees,
                    ["unityAngleDegrees"] = row.UnityAngleDegrees,
                    ["sameSignedAxis"] = row.SameSignedAxis,
                    ["axisErrorDegrees"] = row.AxisErrorDegrees,
                    ["projectionRotationErrorDegrees"] = row.ProjectionRotationErrorDegrees,
                    ["currentErrorDegrees"] = row.CurrentErrorDegrees,
                    ["bodyGroup"] = ClassifyBodyGroup(row.TargetPath),
                });
            }
            return array;
        }

        private static JArray ToJsonRows(IEnumerable<SingleMuscleSignedAxisTiltRow> rows)
        {
            var array = new JArray();
            foreach (var row in rows)
            {
                array.Add(new JObject
                {
                    ["humanBone"] = row.HumanBone,
                    ["attribute"] = row.Attribute,
                    ["targetPath"] = row.TargetPath,
                    ["bodyGroup"] = row.BodyGroup,
                    ["sampleCount"] = row.SampleCount,
                    ["predictedAxis"] = ToJArray(row.PredictedAxis),
                    ["unityAxis"] = ToJArray(row.UnityAxis),
                    ["predictedNearestSignedAxis"] = row.PredictedNearestSignedAxis,
                    ["predictedNearestAxisErrorDegrees"] = row.PredictedNearestAxisErrorDegrees,
                    ["unityNearestSignedAxis"] = row.UnityNearestSignedAxis,
                    ["unityNearestAxisErrorDegrees"] = row.UnityNearestAxisErrorDegrees,
                    ["nearestAxisSourceCandidate"] = row.NearestAxisSourceCandidate,
                    ["nearestAxisSourceErrorDegrees"] = row.NearestAxisSourceErrorDegrees,
                    ["axisTiltDegrees"] = row.AxisTiltDegrees,
                    ["predictedAxisSpreadDegrees"] = row.PredictedAxisSpreadDegrees,
                    ["unityAxisSpreadDegrees"] = row.UnityAxisSpreadDegrees,
                    ["maxCurrentErrorDegrees"] = row.MaxCurrentErrorDegrees,
                    ["avgCurrentErrorDegrees"] = row.AvgCurrentErrorDegrees,
                    ["avgAngleRatio"] = row.AvgAngleRatio,
                });
            }
            return array;
        }

        private static JArray ToJsonRows(IEnumerable<SingleMuscleMultiValueLinearityRow> rows)
        {
            var array = new JArray();
            foreach (var row in rows)
            {
                array.Add(new JObject
                {
                    ["muscleIndex"] = row.MuscleIndex,
                    ["muscleName"] = row.MuscleName,
                    ["humanBone"] = row.HumanBone,
                    ["targetPath"] = row.TargetPath,
                    ["bodyGroup"] = row.BodyGroup,
                    ["baseValue"] = row.BaseValue,
                    ["valueCount"] = row.ValueCount,
                    ["sampleCount"] = row.SampleCount,
                    ["positiveSampleCount"] = row.PositiveSampleCount,
                    ["negativeSampleCount"] = row.NegativeSampleCount,
                    ["maxAngleDegrees"] = row.MaxAngleDegrees,
                    ["positiveMeanSlopeDegreesPerUnit"] = row.PositiveMeanSlopeDegreesPerUnit,
                    ["negativeMeanSlopeDegreesPerUnit"] = row.NegativeMeanSlopeDegreesPerUnit,
                    ["maxRelativeSlopeError"] = row.MaxRelativeSlopeError,
                    ["maxAxisSpreadDegrees"] = row.MaxAxisSpreadDegrees,
                    ["currentAvatarAxis"] = row.CurrentAvatarAxis,
                    ["currentAvatarAxisName"] = AvatarAxisName(row.CurrentAvatarAxis),
                    ["limitHumanBone"] = row.LimitHumanBone,
                    ["limitAvatarAxis"] = row.LimitAvatarAxis,
                    ["limitAvatarAxisName"] = AvatarAxisName(row.LimitAvatarAxis),
                    ["positiveLimitDegrees"] = row.PositiveLimitDegrees,
                    ["negativeLimitDegrees"] = row.NegativeLimitDegrees,
                    ["positiveLimitRatio"] = row.PositiveLimitRatio,
                    ["negativeLimitRatio"] = row.NegativeLimitRatio,
                    ["axisStable"] = row.AxisStable,
                    ["limitExplained"] = row.LimitExplained,
                });
            }
            return array;
        }

        private sealed record GltfRotationTrack(float[] Times, float[][] Rotations);
        private sealed record UnityRotationKey(float Time, float[] Rotation);
        private sealed record RotationKey(float Time, float[] Rotation);
        private sealed record FloatKey(float Time, float Value);
        private sealed record LowerArmBaseExpressionFactor(string Name, bool Inverted);
        private sealed record TemplateFactor(string Name, bool Inverted);
        private sealed record LowerArmTemplateCandidateGridRow(
            string TargetHumanBone,
            string MuscleName,
            string Candidate,
            string RoleSignature,
            float Error);
        private sealed record MuscleCompareRow(string Name, int MuscleIndex, int SampleCount, float MaxAbsError, double AvgAbsError);
        private sealed record SolverVariant(
            string Name,
            string Description,
            SolverTarget[] Targets,
            string ComposeMode,
            string PoseSource = null,
            string PoseApplyMode = null,
            string TwistMode = null,
            string SwingMode = null,
            string AxisOrder = null,
            string MirrorMode = null);
        private sealed record SolverTarget(string HumanBone, string XAttribute, string YAttribute, string ZAttribute, string ExtraZAttribute = null);
        private sealed record ExpectedInfluenceTarget(string HumanBone, string Path);
        private sealed record ProbeInfluenceRow(string Path, string Name, float AngleDegrees, string BodyGroup);
        private sealed record MultiValueProbeSample(
            int MuscleIndex,
            string MuscleName,
            string Path,
            float BaseValue,
            float Value,
            float Offset,
            float[] Axis,
            float AngleDegrees,
            float DegreesPerUnit);
        private sealed record ProbeMuscleValue(
            int MuscleIndex,
            string MuscleName,
            float BaseValue,
            float Value);
        private sealed record OracleSingleMuscleDeltaSample(
            float Value,
            float[] BaseRotation,
            float[] LeftDelta,
            float[] RightDelta);
        private sealed record PartialOracleLowerArmMode(
            string Name,
            string Replacement,
            string DeltaMode);
        private sealed record SingleMuscleMultiValueLinearityRow(
            int MuscleIndex,
            string MuscleName,
            string HumanBone,
            string TargetPath,
            string BodyGroup,
            float BaseValue,
            int ValueCount,
            int SampleCount,
            int PositiveSampleCount,
            int NegativeSampleCount,
            float MaxAngleDegrees,
            double PositiveMeanSlopeDegreesPerUnit,
            double NegativeMeanSlopeDegreesPerUnit,
            float MaxRelativeSlopeError,
            float MaxAxisSpreadDegrees,
            int CurrentAvatarAxis,
            string LimitHumanBone,
            int LimitAvatarAxis,
            float PositiveLimitDegrees,
            float NegativeLimitDegrees,
            float PositiveLimitRatio,
            float NegativeLimitRatio,
            bool AxisStable,
            bool LimitExplained);
        private sealed record SourceAxisCandidate(string HumanBone, string Path, SolverAxis Axis);
        private sealed record ForearmStretchScaleCandidate(string Name, string Mode, float Value);
        private sealed record HumanParameterWeight(string Name, float Value);
        private sealed record ForearmPairDeltaMode(
            string Name,
            string StretchFormula,
            string TwistFormula,
            string StretchSide,
            string TwistSide,
            string OrderMode,
            string OutputSide,
            string PositiveDeltaSide = null,
            string PositiveOrderMode = null,
            string PositiveOutputSide = null,
            string NegativeDeltaSide = null,
            string NegativeOrderMode = null,
            string NegativeOutputSide = null,
            string TargetFamily = "any");
        private sealed record SourceTargetAxisFitRow(
            string SourceHumanBone,
            string SourcePath,
            int SourceAxis,
            int TargetAxis,
            float PredictedDeltaDegrees,
            float ErrorDegrees);
        private sealed record SingleMuscleFormulaCandidateRow(
            string FormulaName,
            string MuscleName,
            string TargetHumanBone,
            string SourceHumanBone,
            int SourceAxis,
            int TargetAxis,
            float Value,
            string TargetPath,
            string ProbePath,
            string BodyGroup,
            string MuscleFamily,
            float ErrorDegrees);
        private sealed record ArmTwistTimelineVariant(
            string Name,
            string Description,
            string SourceKind,
            int SourceAxis,
            int TargetAxis,
            string ComposeMode = "swing_twist",
            string MirrorMode = null,
            string PoseAxisCandidate = null);
        private sealed record UpperArmSwingTimelineVariant(
            string Name,
            string Description,
            int XTargetAxis,
            int YTargetAxis,
            int XSign,
            int YSign,
            string SwingMode,
            string ComposeMode,
            string FrontBackAxisCandidate = null,
            string FrontBackApplyMode = null);
        private sealed record DistalStretchTimelineVariant(
            string Name,
            string Description,
            int StretchTargetAxis,
            int StretchSign,
            string SwingMode,
            string ComposeMode,
            string PoseAxisCandidate = null,
            string PoseApplyMode = null);
        private readonly record struct SolverAxis(float[] PreQ, float[] PostQ, float[] Sign, float[] LimitMin, float[] LimitMax);
        private sealed record SolverResidualRow(
            string Path,
            int SampleCount,
            float RawMaxDegrees,
            double RawAvgDegrees,
            float[] LeftCorrection,
            float LeftCorrectionAngleDegrees,
            float LeftCorrectedMaxDegrees,
            double LeftCorrectedAvgDegrees,
            float[] RightCorrection,
            float RightCorrectionAngleDegrees,
            float RightCorrectedMaxDegrees,
            double RightCorrectedAvgDegrees,
            JObject LeftDynamicResidualAxis,
            JObject RightDynamicResidualAxis,
            string NearestLeftCorrectionSource,
            float NearestLeftCorrectionSourceErrorDegrees,
            string NearestRightCorrectionSource,
            float NearestRightCorrectionSourceErrorDegrees,
            Dictionary<string, float[]> CorrectionCandidates,
            Dictionary<string, float> LeftCorrectionCandidateGaps,
            Dictionary<string, float> RightCorrectionCandidateGaps);
        private sealed record CorrectionGapRow(
            string Path,
            string BodyGroup,
            string Candidate,
            float CandidateGapDegrees,
            float LearnedCorrectionAngleDegrees,
            float CorrectedMaxDegrees,
            double CorrectedAvgDegrees);
        private sealed record CorrectionCandidateMatch(string Name, float AngleDegrees);
        private sealed record SingleMuscleDeltaCorrectionRow(
            string HumanBone,
            string Attribute,
            float Value,
            string TargetPath,
            string ProbePath,
            float LeftErrorDegrees,
            float RightErrorDegrees,
            string BestApplyMode,
            float[] PredictedDelta,
            float[] UnityDelta,
            float[] TargetPreQ,
            float[] TargetPostQ,
            float[] BestCorrection,
            float BestCorrectionAngleDegrees,
            float[] LeftCorrection,
            float LeftCorrectionAngleDegrees,
            float[] RightCorrection,
            float RightCorrectionAngleDegrees)
        {
            public float CurrentErrorDegrees => MathF.Min(LeftErrorDegrees, RightErrorDegrees);
        }
        private sealed record MuscleCombinationResidualRow(
            string ProbeName,
            string ProbeFamily,
            string HumanBone,
            string TargetPath,
            string ProbePath,
            string[] Attributes,
            string BestApplyMode,
            float CurrentErrorDegrees,
            float[] LeftCorrection,
            float LeftCorrectionAngleDegrees,
            float[] RightCorrection,
            float RightCorrectionAngleDegrees,
            float[] BestCorrection,
            float BestCorrectionAngleDegrees);
        private sealed record UnityCombinationCompositionRow(
            string ProbeName,
            string ProbeFamily,
            string Path,
            string[] MuscleNames,
            string BestMode,
            string BestOrder,
            float BestErrorDegrees,
            string ComparedTo,
            float ComboDeltaAngleDegrees);
        private sealed record UnityCombinationCompositionCandidate(
            string Mode,
            string Order,
            float ErrorDegrees,
            string ComparedTo);
        private sealed record SingleMuscleDeltaAxisRow(
            string HumanBone,
            string Attribute,
            float Value,
            string TargetPath,
            string ProbePath,
            float[] PredictedAxis,
            float PredictedAngleDegrees,
            float[] UnityAxis,
            float UnityAngleDegrees,
            float AxisErrorDegrees,
            float AngleRatio,
            float CurrentErrorDegrees,
            string BestApplyMode);
        private sealed record SingleMuscleSignedAxisSample(
            string HumanBone,
            string Attribute,
            string TargetPath,
            string ProbePath,
            float Value,
            float[] PredictedAxis,
            float[] UnityAxis,
            float[] TargetPreQ,
            float[] TargetPostQ,
            float PredictedAngleDegrees,
            float UnityAngleDegrees,
            float AxisTiltDegrees,
            float CurrentErrorDegrees);
        private sealed record SingleMuscleSignedAxisTiltRow(
            string HumanBone,
            string Attribute,
            string TargetPath,
            string BodyGroup,
            int SampleCount,
            float[] PredictedAxis,
            float[] UnityAxis,
            string PredictedNearestSignedAxis,
            float PredictedNearestAxisErrorDegrees,
            string UnityNearestSignedAxis,
            float UnityNearestAxisErrorDegrees,
            string NearestAxisSourceCandidate,
            float NearestAxisSourceErrorDegrees,
            float AxisTiltDegrees,
            float PredictedAxisSpreadDegrees,
            float UnityAxisSpreadDegrees,
            float MaxCurrentErrorDegrees,
            double AvgCurrentErrorDegrees,
            double AvgAngleRatio);
        private sealed record SingleMuscleAvatarAxisProjectionRow(
            string ProjectionMode,
            string HumanBone,
            string Attribute,
            float Value,
            string TargetPath,
            string ProbePath,
            string PredictedSignedAxis,
            float PredictedNearestAxisErrorDegrees,
            float PredictedAngleDegrees,
            string UnitySignedAxis,
            float UnityNearestAxisErrorDegrees,
            float UnityAngleDegrees,
            bool SameSignedAxis,
            float AxisErrorDegrees,
            float ProjectionRotationErrorDegrees,
            float CurrentErrorDegrees);
        private sealed record AxisBasisCandidate(string Name, float[] Basis, string Mode, string MirrorName);
        private sealed record AxisBasisCandidateRow(
            string Name,
            string HumanBone,
            string Attribute,
            float Value,
            string TargetPath,
            float RotationErrorDegrees,
            float AxisErrorDegrees);
        private sealed record TrackCompareRow(
            string UnityPath,
            string GltfPath,
            int NodeIndex,
            int SampleCount,
            float MaxRotationErrorDegrees,
            double AvgRotationErrorDegrees,
            bool BodyBone);
        private sealed record SnapshotCompareRow(
            string Path,
            float RotationDegrees,
            float TranslationDistance,
            bool BodyBone);

        private static readonly Dictionary<string, int> HumanBoneIndexes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Hips"] = 0,
            ["LeftUpperLeg"] = 1,
            ["RightUpperLeg"] = 2,
            ["LeftLowerLeg"] = 3,
            ["RightLowerLeg"] = 4,
            ["LeftFoot"] = 5,
            ["RightFoot"] = 6,
            ["Spine"] = 7,
            ["Chest"] = 8,
            ["UpperChest"] = 9,
            ["Neck"] = 10,
            ["Head"] = 11,
            ["LeftShoulder"] = 12,
            ["RightShoulder"] = 13,
            ["LeftUpperArm"] = 14,
            ["RightUpperArm"] = 15,
            ["LeftLowerArm"] = 16,
            ["RightLowerArm"] = 17,
            ["LeftHand"] = 18,
            ["RightHand"] = 19,
            ["LeftToes"] = 20,
            ["RightToes"] = 21,
            ["LeftEye"] = 22,
            ["RightEye"] = 23,
            ["Jaw"] = 24,
        };

        private sealed class TimelineTrackAccumulator
        {
            private readonly List<float> errors = new();

            public TimelineTrackAccumulator(string unityPath, string gltfPath, int nodeIndex)
            {
                UnityPath = unityPath;
                GltfPath = gltfPath;
                NodeIndex = nodeIndex;
            }

            private string UnityPath { get; }
            private string GltfPath { get; }
            private int NodeIndex { get; }

            public void Add(float error) => errors.Add(error);

            public TrackCompareRow ToRow() => new(
                UnityPath,
                GltfPath,
                NodeIndex,
                errors.Count,
                errors.Count == 0 ? 0 : errors.Max(),
                errors.Count == 0 ? 0 : errors.Average(),
                true);
        }
    }
}
