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
    internal static class UnityBakeResultApplier
    {
        public static string Apply(string requestOrResultPath, string outputGltfPath)
        {
            if (string.IsNullOrWhiteSpace(requestOrResultPath) || !File.Exists(requestOrResultPath))
            {
                Logger.Error($"Unity bake request/result not found: {requestOrResultPath}");
                return null;
            }

            var input = JObject.Parse(File.ReadAllText(requestOrResultPath));
            var requestPath = IsRequest(input) ? requestOrResultPath : TryFindSiblingRequest(requestOrResultPath);
            if (requestPath == null || !File.Exists(requestPath))
            {
                Logger.Error("Unable to find unity_bake_request.json. Apply needs the request so it can locate the source glTF.");
                return null;
            }

            var request = JObject.Parse(File.ReadAllText(requestPath));
            var resultPath = IsRequest(input)
                ? (string)request["outputJson"]
                : requestOrResultPath;
            if (string.IsNullOrWhiteSpace(resultPath) || !File.Exists(resultPath))
            {
                Logger.Error($"Unity bake result not found: {resultPath}");
                return null;
            }

            var result = JObject.Parse(File.ReadAllText(resultPath));

            var sourceGltf = (string)request["animeStudioAssets"]?["model"]?["gltf"];
            if (string.IsNullOrWhiteSpace(sourceGltf) || !File.Exists(sourceGltf))
            {
                Logger.Error($"Source glTF not found in request: {sourceGltf}");
                return null;
            }

            var clipName = (string)result["clipName"] ?? (string)request["animeStudioAssets"]?["animation"]?["name"] ?? "UnityBaked";
            outputGltfPath = ResolveOutputPath(requestPath, outputGltfPath, sourceGltf, (string)request["animeStudioAssets"]?["model"]?["name"], clipName);
            var outputDir = Path.GetDirectoryName(outputGltfPath);
            if (!string.Equals((string)result["status"], "ok", StringComparison.OrdinalIgnoreCase))
            {
                var failureMessage = $"Unity bake result is not ok: {(string)result["message"]}";
                WriteFailureReport(outputDir, sourceGltf, outputGltfPath, requestPath, resultPath, clipName, failureMessage, result);
                Logger.Error(failureMessage);
                return null;
            }

            var modelGate = ModelLibraryValidator.ValidateSingleModelForAnimationGate(sourceGltf);
            if ((bool?)modelGate?["animationGate"]?["ready"] != true)
            {
                var failureMessage = "Unity bake apply blocked by model-first gate: source model glTF is not animation-ready. Fix Mesh/UV/material/texture/skin/bbox first, then generate animation output.";
                WriteModelGateFailureReport(outputDir, sourceGltf, outputGltfPath, requestPath, resultPath, clipName, failureMessage, result, modelGate);
                Logger.Error(failureMessage);
                return null;
            }

            var requiresHumanoidBake = (bool?)request["animeStudioAssets"]?["animation"]?["requiresHumanoidBake"] ?? false;
            var unityClipIsHumanMotion = (bool?)result["isHumanMotion"] ?? false;
            var clipFilterMode = ResolveClipFilterMode(request, result);
            var isTransformOnlyClipFilter = string.Equals(clipFilterMode, "transform_only", StringComparison.OrdinalIgnoreCase);
            if (requiresHumanoidBake && !isTransformOnlyClipFilter && !unityClipIsHumanMotion)
            {
                var failureMessage = "Unity bake rejected this Humanoid/Muscle animation because Unity reported isHumanMotion=false for the imported clip. The current .anim input was not sampled as a real Humanoid/Muscle body animation, so writing a baked glTF would preserve a misleading static/default pose.";
                WriteFailureReport(outputDir, sourceGltf, outputGltfPath, requestPath, resultPath, clipName, failureMessage, result);
                Logger.Error(failureMessage);
                return null;
            }

            CopyModelFolder(sourceGltf, outputGltfPath);

            var gltf = JObject.Parse(File.ReadAllText(outputGltfPath));
            var bufferFile = ResolveMainBufferFile(gltf, outputDir);
            if (bufferFile == null)
            {
                Logger.Error("Only file-buffer glTF is currently supported for Unity bake apply.");
                return null;
            }

            var bufferBytes = File.ReadAllBytes(bufferFile).ToList();
            var nodes = gltf["nodes"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var hiddenLodMeshes = HideNonPrimaryLodMeshes(nodes);
            var nodePathToIndex = BuildNodePathIndex(nodes);
            var selectedTracks = SelectUnityBakeTrackSource(result, requiresHumanoidBake, nodes);
            var animation = new JObject
            {
                ["name"] = clipName,
                ["samplers"] = new JArray(),
                ["channels"] = new JArray(),
                ["extras"] = new JObject
                {
                    ["animeStudioUnityBake"] = new JObject
                    {
                        ["sourceRequest"] = requestPath,
                        ["sourceResult"] = resultPath,
                        ["avatar"] = result["avatarName"],
                        ["avatarValid"] = result["avatarValid"],
                        ["rigRestPoseSource"] = result["rigRestPoseSource"],
                        ["rigRestPoseApplied"] = result["rigRestPoseApplied"],
                        ["sampleCount"] = result["sampleCount"],
                        ["changedTrackCount"] = result["changedTrackCount"],
                        ["coordinateConversion"] = "Unity baked local TRS is applied as a delta, then converted back to glTF basis.",
                        ["trackSource"] = selectedTracks.Source,
                        ["trackSourceReason"] = selectedTracks.Reason,
                        ["humanoidDeltaBase"] = "rest_pose",
                        ["rotationApplyMode"] = RotationApplyMode,
                    }
                }
            };

            var writtenTracks = 0;
            var frameVaryingTracks = 0;
            var frameVaryingChannels = 0;
            var coreBodyFrameVaryingTracks = 0;
            var firstPoseChangedTracks = 0;
            var coreBodyFirstPoseChangedTracks = 0;
            var unityRestPoseTracks = 0;
            var humanoidDeltaBase = ResolveHumanoidDeltaBase(request);
            var useFirstSampleAsBase = string.Equals(humanoidDeltaBase, "first_sample", StringComparison.OrdinalIgnoreCase);
            var trackMotionReports = new List<TrackMotionReport>();
            var skippedTracks = new List<string>();
            var suppressedHumanoidRootRotationTracks = new List<string>();
            foreach (var track in selectedTracks.Tracks)
            {
                if ((bool?)track["changed"] != true)
                {
                    continue;
                }

                var path = NormalizeBakePath((string)track["path"], nodes);
                if (!nodePathToIndex.TryGetValue(path, out var nodeIndex))
                {
                    skippedTracks.Add((string)track["path"]);
                    continue;
                }

                var times = track["rotations"]?.OfType<JObject>().Select(x => (float)x["time"]).ToArray() ?? Array.Empty<float>();
                if (times.Length == 0)
                {
                    continue;
                }

                var node = nodes[nodeIndex];
                var restTranslation = ReadNodeTranslation(node);
                var restRotation = ReadNodeRotation(node);
                var restScale = ReadNodeScale(node);
                float[] unityRestTranslation;
                float[] unityRestRotation;
                float[] unityRestScale;
                var hasUnityRestPose =
                    TryReadVector3Key(track["restTranslation"], out unityRestTranslation) &
                    TryReadQuaternionKey(track["restRotation"], out unityRestRotation) &
                    TryReadVector3Key(track["restScale"], out unityRestScale);
                if (hasUnityRestPose)
                {
                    unityRestPoseTracks++;
                }

                var rawTranslations = ReadVec3(track["translations"]);
                var rawRotations = ReadVec4(track["rotations"]);
                var rawScales = ReadVec3(track["scales"]);
                var firstPose = BuildFirstPoseMotionReport(
                    (string)track["path"],
                    path,
                    rawTranslations,
                    rawRotations,
                    rawScales,
                    hasUnityRestPose ? unityRestTranslation : GltfToUnityPosition(restTranslation),
                    hasUnityRestPose ? unityRestRotation : GltfToUnityRotation(restRotation),
                    hasUnityRestPose ? unityRestScale : restScale);
                if (firstPose.FirstPoseChanged)
                {
                    firstPoseChangedTracks++;
                    if (IsCoreBodyBonePath(path))
                    {
                        coreBodyFirstPoseChangedTracks++;
                    }
                }
                var translations = ApplyRestRelativeTranslations(
                    rawTranslations,
                    hasUnityRestPose ? unityRestTranslation : GltfToUnityPosition(restTranslation),
                    restTranslation,
                    useFirstSampleAsBase);
                var rotations = ApplyRestRelativeRotations(
                    rawRotations,
                    hasUnityRestPose ? unityRestRotation : GltfToUnityRotation(restRotation),
                    restRotation,
                    useFirstSampleAsBase);
                if (requiresHumanoidBake && IsHumanoidSkeletonRootContainer(nodeIndex, nodes))
                {
                    // Humanoid bake 会把 body/root orientation 落到骨架容器根（常见如 Bip001）。
                    // 这个节点不是实际人体骨骼；直接写入 rotation 会把整个人横向翻倒。
                    // 这里保留 translation/scale，只把容器根 rotation 固定为 glTF rest。
                    rotations = rotations.Select(_ => (float[])restRotation.Clone()).ToArray();
                    suppressedHumanoidRootRotationTracks.Add(path);
                }
                var scales = ApplyRestRelativeScales(
                    rawScales,
                    hasUnityRestPose ? unityRestScale : restScale,
                    restScale,
                    useFirstSampleAsBase);
                var motion = BuildTrackMotionReport((string)track["path"], path, translations, rotations, scales);
                if (motion.FrameVarying)
                {
                    frameVaryingTracks++;
                    if (IsCoreBodyBonePath(path))
                    {
                        coreBodyFrameVaryingTracks++;
                    }
                }
                frameVaryingChannels += motion.FrameVaryingChannelCount;
                trackMotionReports.Add(motion with
                {
                    FirstPoseChanged = firstPose.FirstPoseChanged,
                    FirstPoseTranslationDelta = firstPose.FirstPoseTranslationDelta,
                    FirstPoseRotationDelta = firstPose.FirstPoseRotationDelta,
                    FirstPoseScaleDelta = firstPose.FirstPoseScaleDelta,
                });

                WriteChannel(gltf, animation, bufferBytes, nodeIndex, "translation", times, translations);
                WriteChannel(gltf, animation, bufferBytes, nodeIndex, "rotation", times, rotations);
                WriteChannel(gltf, animation, bufferBytes, nodeIndex, "scale", times, scales);
                writtenTracks++;
            }

            if (writtenTracks == 0)
            {
                Logger.Error("No baked tracks matched glTF nodes; inspect path mapping before trusting this output.");
                return null;
            }

            if (requiresHumanoidBake
                && coreBodyFrameVaryingTracks == 0
                && IsStandaloneIncompleteHumanoidClip(request, out var incompleteReason))
            {
                var failureMessage =
                    "Unity bake rejected this Humanoid/Muscle animation because the selected AnimationClip does not contain standalone body-driving motion. "
                    + incompleteReason
                    + " This clip is still a deterministic Unity reference, but it must be sampled through its AnimatorController/blend/layer context or combined with the body-driving clip; baking it alone would produce a misleading pose/root/accessory-only glTF.";
                WriteFailureReport(outputDir, sourceGltf, outputGltfPath, requestPath, resultPath, clipName, failureMessage, result);
                Logger.Error(failureMessage);
                return null;
            }

            // 预览文件只保留当前烘焙动画，避免 F3D/Blender 默认播放旧的表情或辅助动画，
            // 让用户打开文件时看到的就是这次选择的模型 + 动作。
            gltf["animations"] = new JArray(animation);
            ((JArray)(gltf["buffers"]))[0]["byteLength"] = bufferBytes.Count;
            RemoveEmptyTopLevelArrays(gltf);
            File.WriteAllBytes(bufferFile, bufferBytes.ToArray());
            File.WriteAllText(outputGltfPath, JsonConvert.SerializeObject(gltf, Formatting.Indented));
            var skinReport = BuildSkinAnimationReport(gltf);
            var avatarTrust = BuildAvatarTrustReport(request, result);
            var humanoidRuntimeSampling = BuildHumanoidRuntimeSamplingReport(result);
            var legacyUnityBakePolicyOverride = (bool?)request["validation"]?["legacyUnityBakePolicyOverride"] == true;
            var legacyUnityBakePolicyOverrideReason = (string)request["validation"]?["legacyUnityBakePolicyOverrideReason"];
            var diagnosticRebuildEditorCurveClip =
                (bool?)request["rebuildEditorCurveClip"] == true ||
                (bool?)result["requestRebuildEditorCurveClip"] == true ||
                (bool?)result["clipRebuildAttempted"] == true;
            var diagnosticIgnoreImportedAvatar =
                (bool?)request["ignoreImportedAvatar"] == true ||
                (bool?)result["requestIgnoreImportedAvatar"] == true;
            var status = skippedTracks.Count == 0 && skinReport.InvalidTargetCount == 0
                ? (frameVaryingTracks > 0 ? "ok" : "static_pose")
                : "warning";
            var message = status == "static_pose"
                ? "Unity bake wrote tracks that differ from rest pose, but no written channel changes over sampled frames. Treat this as a static pose diagnostic, not a playable animation."
                : null;
            if (requiresHumanoidBake
                && status == "static_pose"
                && humanoidRuntimeSampling.HasOnlyConstantEditorCurves)
            {
                message = "Unity bake wrote a static pose, and Unity AnimationUtility reported editor curves that are all constant over the sampled timeline. Treat this clip as a pose/base/additive layer or controller-context fragment, not as a complete playable body animation.";
            }
            if (requiresHumanoidBake
                && status == "static_pose"
                && humanoidRuntimeSampling.HasEditorCurvesButNoRuntimePoseMotion)
            {
                message = humanoidRuntimeSampling.Message;
            }
            if (requiresHumanoidBake && status == "ok" && coreBodyFrameVaryingTracks == 0)
            {
                status = "needs_review";
                message = coreBodyFirstPoseChangedTracks > 0
                    ? "Unity bake produced a static or pose-only Humanoid result: core body bones differ from rest pose in the first frame, but do not change over time. The baked glTF preserves this as a rest-pose-relative pose, but it still needs visual review because it is not a frame-varying body animation."
                    : unityClipIsHumanMotion
                    ? "Unity bake only produced frame-varying root or auxiliary motion; no core body bone changed over time. Treat this as a Humanoid bake diagnostic until a body-driving clip is confirmed."
                    : "Unity bake only produced frame-varying root or auxiliary motion; no core body bone changed over time. Unity also reported isHumanMotion=false for the imported clip, so the current .anim input was not sampled as a real Humanoid/Muscle clip.";
            }
            if (string.Equals(humanoidDeltaBase, "first_sample", StringComparison.OrdinalIgnoreCase))
            {
                status = "needs_review";
                message = string.IsNullOrWhiteSpace(message)
                    ? "Humanoid bake was applied with first-sample delta as a diagnostic anti-twist path. This prevents obvious flipped output, but it can erase meaningful first-frame poses such as idle, aim, or pose-only clips, so it is not a trusted production animation export."
                    : message;
            }
            if (!avatarTrust.TrustedProductionBake && (status == "ok" || status == "warning"))
            {
                status = "needs_review";
                message = avatarTrust.Message;
            }
            if (requiresHumanoidBake
                && (status == "ok" || status == "warning")
                && humanoidRuntimeSampling.HasEditorCurvesButNoRuntimePoseMotion)
            {
                status = "needs_review";
                message = humanoidRuntimeSampling.Message;
            }
            if (legacyUnityBakePolicyOverride && (status == "ok" || status == "warning"))
            {
                status = "needs_review";
                message = IsUnityDerivedPlayableGraphCandidate(
                        requiresHumanoidBake,
                        unityClipIsHumanMotion,
                        avatarTrust,
                        writtenTracks,
                        frameVaryingTracks,
                        coreBodyFrameVaryingTracks,
                        skinReport.InvalidTargetCount,
                        diagnosticRebuildEditorCurveClip,
                        diagnosticIgnoreImportedAvatar,
                        isTransformOnlyClipFilter)
                    ? "UnityDerivedPlayableGraph wrote ordinary glTF TRS channels with a trusted Avatar and frame-varying core body tracks. It is an automated Unity-derived solver output, not the old manual Unity project bake, but it still requires clear visual validation before production reuse."
                    : "This Unity bake was allowed only as an explicit UnityOracle/PlayableGraph diagnostic after the legacy Unity-bake-deprecated standalone flag blocked the clip. It must pass visual validation before being treated as reusable glTF animation.";
                message += string.IsNullOrWhiteSpace(legacyUnityBakePolicyOverrideReason) ? string.Empty : " Block reason: " + legacyUnityBakePolicyOverrideReason;
            }
            if (isTransformOnlyClipFilter
                && (status == "ok" || status == "warning" || status == "needs_review"))
            {
                status = "needs_review";
                var transformOnlyMessage = "This bake used clipFilterMode=transform_only: AnimeStudio sampled the deterministic Transform/controller layer result and deliberately excluded the main clip Humanoid/Muscle curves because they are not trusted for this candidate. Treat the output as a controller-context diagnostic until direct layer/additive TRS composition and visual validation pass.";
                message = string.IsNullOrWhiteSpace(message)
                    ? transformOnlyMessage
                    : message + " " + transformOnlyMessage;
            }
            var animatorControllerSamplingMode = (string)result["animatorControllerSamplingMode"];
            var animatorControllerAdditionalLayerMaskCount = (int?)result["animatorControllerAdditionalLayerMaskCount"] ?? 0;
            var animatorControllerLayerMasksApplied = (bool?)result["animatorControllerLayerMasksApplied"] ?? animatorControllerAdditionalLayerMaskCount == 0;
            var animatorControllerLayerMaskWarning = (string)result["animatorControllerLayerMaskWarning"];
            var animatorControllerLayerMixerBypassesControllerBlendTrees = (bool?)result["animatorControllerLayerMixerBypassesControllerBlendTrees"] ?? false;
            var animatorControllerIkPassEffectiveForSampling = (bool?)result["animatorControllerIkPassEffectiveForSampling"] ?? false;
            var animatorControllerIkPassSamplingMode = (string)result["animatorControllerIkPassSamplingMode"];
            var animatorControllerIkPassSamplingWarning = (string)result["animatorControllerIkPassSamplingWarning"];
            var animatorControllerUnmaskedOverrideLayerCount = (int?)result["animatorControllerUnmaskedOverrideLayerCount"] ?? 0;
            var animatorControllerSkippedUntrustedLayerCount = (int?)result["animatorControllerSkippedUntrustedLayerCount"] ?? 0;
            var animatorControllerDiagnosticSampledSkippedLayerCount = (int?)result["animatorControllerDiagnosticSampledSkippedLayerCount"] ?? 0;
            var selectedControllerLayerClipNeedsBaseContext = SelectedControllerLayerClipNeedsBaseContext(request);
            if (requiresHumanoidBake && NeedsAnimatorControllerContextSampling(request, result))
            {
                status = "needs_animator_controller_context";
                var requestedControllerAsset = (string)request?["unityAssetPaths"]?["animatorController"];
                message = !string.IsNullOrWhiteSpace(requestedControllerAsset)
                    ? "This Humanoid clip was selected through AnimatorController context, and the request supplied a RuntimeAnimatorController asset, but Unity could not load it. Restore/import the original AnimatorController asset before treating this glTF as a reusable body animation."
                    : "This Humanoid clip was selected through AnimatorController context, but the request did not include a RuntimeAnimatorController asset, so Unity sampled the raw AnimationClip only. Restore and sample the AnimatorController state/layer/blend context before treating this glTF as a reusable body animation.";
            }
            if (requiresHumanoidBake
                && IsGeneratedAnimatorControllerSamplingMode(animatorControllerSamplingMode))
            {
                status = "needs_animator_controller_context";
                var generatedControllerMessage = string.Equals(animatorControllerSamplingMode, "generated_multi_layer_controller", StringComparison.OrdinalIgnoreCase)
                    ? "Unity bake sampled a generated multi-layer AnimatorController reconstructed from deterministic controller context. This is useful for controller-layer diagnostics, but it is not the original RuntimeAnimatorController asset and must not be treated as a reusable animation until the original AnimatorController state/layer/blend context or an equivalent solver is restored."
                    : "Unity bake sampled a generated single-state AnimatorController reconstructed from deterministic controller context. This can make a single Humanoid clip visibly move, but it can also produce semantically wrong poses such as raised/twisted hands on an idle clip; restore the original AnimatorController state/layer/blend context or an equivalent solver before reuse.";
                message = string.IsNullOrWhiteSpace(message)
                    ? generatedControllerMessage
                    : message + " " + generatedControllerMessage;
            }
            if (requiresHumanoidBake
                && animatorControllerAdditionalLayerMaskCount > 0
                && !animatorControllerLayerMasksApplied)
            {
                status = "needs_review";
                message = string.IsNullOrWhiteSpace(animatorControllerLayerMaskWarning)
                    ? "AnimatorController context contains masked additional layer(s), but this generated-controller diagnostic bake did not apply the serialized layer masks. Treat the glTF as a context recovery diagnostic, not a reusable animation."
                    : animatorControllerLayerMaskWarning;
            }
            if (requiresHumanoidBake
                && animatorControllerSkippedUntrustedLayerCount > 0
                && (status == "ok" || status == "warning" || status == "needs_review"))
            {
                status = "needs_review";
                var skippedLayerMessage = "Recovered AnimatorController sampling skipped layer(s) that had no deterministic recovery metadata or mask context. This avoids applying untrusted full-body/additive layers, but it also proves the controller context is still incomplete; treat this as diagnostic until original layer semantics are recovered.";
                message = string.IsNullOrWhiteSpace(message)
                    ? skippedLayerMessage
                    : message + " " + skippedLayerMessage;
            }
            if (requiresHumanoidBake
                && animatorControllerDiagnosticSampledSkippedLayerCount > 0
                && (status == "ok" || status == "warning" || status == "needs_review"))
            {
                status = "needs_review";
                var diagnosticSampledLayerMessage = "This bake explicitly sampled recoverable AnimatorController layer(s) that are normally skipped because their runtime layer context is not fully trusted. Treat this output as a layer/BlendTree diagnostic comparison, not as production reusable animation.";
                message = string.IsNullOrWhiteSpace(message)
                    ? diagnosticSampledLayerMessage
                    : message + " " + diagnosticSampledLayerMessage;
            }
            if (requiresHumanoidBake
                && selectedControllerLayerClipNeedsBaseContext
                && (status == "ok" || status == "warning" || status == "needs_review"))
            {
                status = "needs_review";
                var layerClipMessage = "The selected AnimationClip comes from a non-base AnimatorController layer, and the candidate does not contain a deterministic baseLayerClip from the same controller state or the controller base layer default state. Sampling the recovered controller can help diagnose masks/layers, but this clip still requires full base-layer/controller runtime context before it can be treated as a reusable body animation.";
                message = string.IsNullOrWhiteSpace(message)
                    ? layerClipMessage
                    : message + " " + layerClipMessage;
            }
            if ((diagnosticRebuildEditorCurveClip || diagnosticIgnoreImportedAvatar) && (status == "ok" || status == "warning"))
            {
                status = "needs_review";
                message = diagnosticIgnoreImportedAvatar
                    ? "This bake used a diagnostic rebuilt Avatar instead of the supplied imported Avatar. It may be useful for root-cause analysis, but it is not trusted production animation output."
                    : "This bake rebuilt a Humanoid AnimationClip from Unity editor curves before sampling. It proves the recovered editor curves can drive Unity Humanoid, but it is still a diagnostic path until the original clip/runtime payload issue is resolved.";
            }
            if (selectedTracks.UsesEditorCurveSetHumanPose && (status == "ok" || status == "warning" || status == "needs_review"))
            {
                status = "needs_review";
                var editorCurveSolverMessage = "This glTF was written from editorCurveSetHumanPoseTransformTracks: Unity runtime PlayableGraph did not drive a frame-varying Humanoid pose, so AnimeStudio directly sampled Unity HumanPoseHandler.SetHumanPose with the recovered editor muscle curves. This is a useful solver diagnostic, but it currently ignores limb IK/TDOF/controller runtime details and must not be treated as production animation until visual validation passes.";
                message = string.IsNullOrWhiteSpace(message)
                    ? editorCurveSolverMessage
                    : message + " " + editorCurveSolverMessage;
            }
            if (selectedTracks.UsesAnimationModeSampleClip && (status == "ok" || status == "warning" || status == "needs_review"))
            {
                status = "needs_review";
                var animationModeMessage = "This glTF was written from AnimationMode.SampleAnimationClip diagnostic tracks: Unity Editor can sample frame-varying transforms for this clip, but this path bypasses the real runtime AnimatorController/layer/IK semantics and must not be treated as production animation until clear visual validation passes.";
                message = string.IsNullOrWhiteSpace(message)
                    ? animationModeMessage
                    : message + " " + animationModeMessage;
            }
            var recoverableSkippedLayerSummary = BuildAnimatorControllerRecoverableSkippedLayerSummary(result);
            var requestUnsupportedHumanPoseCurves =
                request["animeStudioAssets"]?["animation"]?["unsupportedHumanPoseCurveSummary"] as JObject;
            var animatorControllerRuntimeParameterSummary = result["animatorControllerRuntimeParameterSummary"] as JObject;
            var animationSolve = BuildUnityBakeAnimationSolveReport(
                status,
                requiresHumanoidBake,
                unityClipIsHumanMotion,
                avatarTrust,
                writtenTracks,
                frameVaryingTracks,
                coreBodyFrameVaryingTracks,
                skinReport.InvalidTargetCount,
                diagnosticRebuildEditorCurveClip,
                diagnosticIgnoreImportedAvatar,
                isTransformOnlyClipFilter,
                legacyUnityBakePolicyOverride,
                legacyUnityBakePolicyOverrideReason,
                animatorControllerSamplingMode,
                animatorControllerAdditionalLayerMaskCount,
                animatorControllerLayerMasksApplied,
                animatorControllerLayerMixerBypassesControllerBlendTrees,
                animatorControllerUnmaskedOverrideLayerCount,
                animatorControllerSkippedUntrustedLayerCount,
                animatorControllerDiagnosticSampledSkippedLayerCount,
                selectedControllerLayerClipNeedsBaseContext,
                humanoidRuntimeSampling,
                requestUnsupportedHumanPoseCurves,
                result["editorCurveHumanPoseDiagnostic"] as JObject,
                result["editorCurveIkGoalDriverDiagnostic"] as JObject,
                animatorControllerRuntimeParameterSummary,
                selectedTracks.UsesEditorCurveSetHumanPose,
                selectedTracks.UsesAnimationModeSampleClip);
            animationSolve["animatorControllerRecoverableSkippedLayerSummary"] = recoverableSkippedLayerSummary;
            animationSolve["animatorControllerRuntimeParameterSummary"] = animatorControllerRuntimeParameterSummary;
            var reportPath = Path.Combine(outputDir, "unity_bake_apply_report.json");
            var previewStatus = BuildAnimationPreviewStatusMetadata(
                status,
                message,
                animationSolve,
                reportPath,
                outputGltfPath);
            AttachAnimationPreviewStatus(gltf, previewStatus);
            File.WriteAllText(outputGltfPath, JsonConvert.SerializeObject(gltf, Formatting.Indented));

            var report = new
            {
                generatedAt = DateTime.UtcNow.ToString("O"),
                status,
                message,
                animationSolve,
                sourceGltf,
                outputGltf = outputGltfPath,
                request = requestPath,
                result = resultPath,
                clipName,
                unityClipIsHumanMotion,
                unitySampleBounds = result["sampleBounds"],
                writtenTracks,
                frameVaryingTracks,
                frameVaryingChannels,
                coreBodyFrameVaryingTracks,
                firstPoseChangedTracks,
                coreBodyFirstPoseChangedTracks,
                unityRestPoseTracks,
                humanoidDeltaBase,
                humanoidFirstSampleDeltaApplied = false,
                unityBakeHelperVersion = (int?)result["helperVersion"] ?? 1,
                unityBakeRigRestPoseSource = (string)result["rigRestPoseSource"],
                unityBakeRigRestPoseApplied = (bool?)result["rigRestPoseApplied"] ?? false,
                unityBakeRequestedAvatarAsset = (string)result["requestedAvatarAsset"],
                unityBakeImportedAvatarAsset = (string)result["importedAvatarAsset"],
                unityBakeImportedAvatarAssetValid = (bool?)result["importedAvatarAssetValid"] ?? false,
                unityBakeImportedAvatarBinding = result["importedAvatarBinding"],
                unityBakeRequestedAnimationClip = (string)result["requestedAnimationClip"],
                unityBakeImportedAnimationClip = (string)result["importedAnimationClip"],
                unityBakeAnimationClipSource = (string)result["animationClipSource"],
                unityBakeClipFilterMode = clipFilterMode,
                unityBakeTrackSource = selectedTracks.Source,
                unityBakeTrackSourceReason = selectedTracks.Reason,
                unityBakeRuntimeTrackSummary = selectedTracks.RuntimeSummary,
                unityBakeEditorCurveSetHumanPoseTrackSummary = selectedTracks.EditorCurveSetHumanPoseSummary,
                unityBakeAnimationModeSampleClipTrackSummary = selectedTracks.AnimationModeSampleClipSummary,
                unityBakeEditorCurveHumanPoseDiagnostic = result["editorCurveHumanPoseDiagnostic"],
                unityBakeEditorCurveIkGoalDriverDiagnostic = result["editorCurveIkGoalDriverDiagnostic"],
                unityBakeEditorCurveCategorySummary = humanoidRuntimeSampling.EditorCurveCategorySummary,
                unityBakeClipFilterRemovedTransformCurveCount = (int?)result["clipFilterRemovedTransformCurveCount"] ?? 0,
                unityBakeClipFilterRemovedAnimatorCurveCount = (int?)result["clipFilterRemovedAnimatorCurveCount"] ?? 0,
                unityBakeClipFilterRemovedObjectReferenceCurveCount = (int?)result["clipFilterRemovedObjectReferenceCurveCount"] ?? 0,
                unityBakeRequestedAnimatorController = (string)result["requestedAnimatorController"],
                unityBakeAnimatorControllerSamplingMode = animatorControllerSamplingMode,
                unityBakeAnimatorControllerSamplingState = (string)result["animatorControllerSamplingState"],
                unityBakeAnimatorControllerSamplingAsset = (string)result["animatorControllerSamplingAsset"],
                unityBakeAnimatorControllerSamplingMessage = (string)result["animatorControllerSamplingMessage"],
                unityBakeAnimatorControllerAdditionalLayerMaskCount = animatorControllerAdditionalLayerMaskCount,
                unityBakeAnimatorControllerAdditionalLayerSkeletonMaskEntryCount = (int?)result["animatorControllerAdditionalLayerSkeletonMaskEntryCount"] ?? 0,
                unityBakeAnimatorControllerLayerMasksApplied = animatorControllerLayerMasksApplied,
                unityBakeAnimatorControllerLayerMaskWarning = animatorControllerLayerMaskWarning,
                unityBakeRequestEnableEditorCurveIkGoalDriver = (bool?)result["requestEnableEditorCurveIkGoalDriver"] ?? (bool?)request["enableEditorCurveIkGoalDriver"] ?? false,
                unityBakeEffectiveEditorCurveIkGoalDriver = (bool?)result["effectiveEditorCurveIkGoalDriver"] ?? false,
                unityBakeRequestSampleRecoverableSkippedLayersDiagnostic = (bool?)result["requestSampleRecoverableSkippedLayersDiagnostic"] ?? (bool?)request["sampleRecoverableSkippedLayersDiagnostic"] ?? false,
                unityBakeAnimatorControllerIkPassEnabledLayerCount = (int?)result["animatorControllerIkPassEnabledLayerCount"] ?? 0,
                unityBakeAnimatorControllerIkPassMessage = (string)result["animatorControllerIkPassMessage"],
                unityBakeAnimatorControllerIkPassEffectiveForSampling = animatorControllerIkPassEffectiveForSampling,
                unityBakeAnimatorControllerIkPassSamplingMode = animatorControllerIkPassSamplingMode,
                unityBakeAnimatorControllerIkPassSamplingWarning = animatorControllerIkPassSamplingWarning,
                unityBakeAnimatorControllerLayerMixerBypassesControllerBlendTrees = animatorControllerLayerMixerBypassesControllerBlendTrees,
                unityBakeAnimatorControllerFidelityWarning = (string)result["animatorControllerFidelityWarning"],
                unityBakeAnimatorControllerUnmaskedOverrideLayerCount = animatorControllerUnmaskedOverrideLayerCount,
                unityBakeAnimatorControllerUnmaskedOverrideLayerNames = result["animatorControllerUnmaskedOverrideLayerNames"],
                unityBakeAnimatorControllerSkippedUntrustedLayerCount = animatorControllerSkippedUntrustedLayerCount,
                unityBakeAnimatorControllerSkippedUntrustedLayerNames = result["animatorControllerSkippedUntrustedLayerNames"],
                unityBakeAnimatorControllerSkippedUntrustedLayerReasons = result["animatorControllerSkippedUntrustedLayerReasons"],
                unityBakeAnimatorControllerSkippedUntrustedLayerDiagnostics = result["animatorControllerSkippedUntrustedLayerDiagnostics"],
                unityBakeAnimatorControllerDiagnosticSampledSkippedLayerCount = animatorControllerDiagnosticSampledSkippedLayerCount,
                unityBakeAnimatorControllerDiagnosticSampledSkippedLayerNames = result["animatorControllerDiagnosticSampledSkippedLayerNames"],
                unityBakeAnimatorControllerRecoverableSkippedLayerSummary = recoverableSkippedLayerSummary,
                unityBakeAnimatorControllerLayerDiagnostics = result["animatorControllerLayerDiagnostics"],
                unityBakeAnimatorControllerParameterDiagnostics = result["animatorControllerParameterDiagnostics"],
                unityBakeAnimatorControllerRuntimeParameterSummary = animatorControllerRuntimeParameterSummary,
                unityBakeAnimatorControllerBlendTreeDiagnostics = result["animatorControllerBlendTreeDiagnostics"],
                unityBakeDiagnostics = new
                {
                    rebuildEditorCurveClip = diagnosticRebuildEditorCurveClip,
                    ignoreImportedAvatar = diagnosticIgnoreImportedAvatar,
                    requestRebuildEditorCurveClip = (bool?)result["requestRebuildEditorCurveClip"] ?? (bool?)request["rebuildEditorCurveClip"] ?? false,
                    requestIgnoreImportedAvatar = (bool?)result["requestIgnoreImportedAvatar"] ?? (bool?)request["ignoreImportedAvatar"] ?? false,
                    clipFilterMode,
                    clipFilterRemovedTransformCurveCount = (int?)result["clipFilterRemovedTransformCurveCount"] ?? 0,
                    clipFilterRemovedAnimatorCurveCount = (int?)result["clipFilterRemovedAnimatorCurveCount"] ?? 0,
                    clipFilterRemovedObjectReferenceCurveCount = (int?)result["clipFilterRemovedObjectReferenceCurveCount"] ?? 0,
                    clipRebuildMode = (string)result["clipRebuildMode"],
                    clipRebuildAttempted = (bool?)result["clipRebuildAttempted"] ?? false,
                    clipRebuildSucceeded = (bool?)result["clipRebuildSucceeded"] ?? false,
                    clipRebuildIsHumanMotion = (bool?)result["clipRebuildIsHumanMotion"] ?? false,
                    clipRebuildCurveCount = (int?)result["clipRebuildCurveCount"] ?? 0,
                    clipRebuildAssetPath = (string)result["clipRebuildAssetPath"],
                    clipRebuildMessage = (string)result["clipRebuildMessage"],
                },
                legacyUnityBakePolicyOverride,
                legacyUnityBakePolicyOverrideReason,
                hiddenNonPrimaryLodMeshCount = hiddenLodMeshes.Count,
                hiddenNonPrimaryLodMeshNodes = hiddenLodMeshes.Take(128).ToArray(),
                suppressedHumanoidRootRotationTrackCount = suppressedHumanoidRootRotationTracks.Count,
                suppressedHumanoidRootRotationTracks = suppressedHumanoidRootRotationTracks.Take(32).ToArray(),
                staticPoseTracks = Math.Max(0, writtenTracks - frameVaryingTracks),
                avatarTrust,
                humanoidRuntimeSampling,
                trackMotionSample = trackMotionReports
                    .OrderByDescending(x => x.MaxRotationDelta)
                    .ThenByDescending(x => x.MaxTranslationDelta)
                    .ThenByDescending(x => x.MaxScaleDelta)
                    .Take(64)
                    .ToArray(),
                skippedTrackCount = skippedTracks.Count,
                skippedTracks = skippedTracks.Take(128).ToArray(),
                skinAnimation = skinReport,
            };
            File.WriteAllText(reportPath, JsonConvert.SerializeObject(report, Formatting.Indented));
            WriteAnimationPreviewStatusReadme(outputDir, previewStatus, reportPath, outputGltfPath);
            Logger.Info($"Baked glTF preview: {outputGltfPath}");
            Logger.Info($"Baked apply report: {reportPath}");
            return outputGltfPath;
        }

        private static List<HiddenLodMeshReport> HideNonPrimaryLodMeshes(JObject[] nodes)
        {
            var result = new List<HiddenLodMeshReport>();
            if (nodes.Length == 0)
            {
                return result;
            }

            var groups = nodes
                .Select((node, index) => new
                {
                    Node = node,
                    Index = index,
                    Name = (string)node["name"] ?? string.Empty,
                    Mesh = (int?)node["mesh"],
                    Lod = TryParseLodNode((string)node["name"], out var lodInfo) ? lodInfo : null,
                })
                .Where(x => x.Mesh.HasValue && x.Lod != null)
                .GroupBy(x => x.Lod.BaseName, StringComparer.OrdinalIgnoreCase);

            foreach (var group in groups)
            {
                var ordered = group
                    .OrderBy(x => x.Lod.Lod)
                    .ThenBy(x => x.Index)
                    .ToArray();
                if (ordered.Length <= 1)
                {
                    continue;
                }

                var selected = ordered[0];
                foreach (var item in ordered.Skip(1))
                {
                    var node = item.Node;
                    var mesh = item.Mesh.Value;
                    var extras = node["extras"] as JObject;
                    if (extras == null)
                    {
                        extras = new JObject();
                        node["extras"] = extras;
                    }
                    extras["animeStudioLod"] = new JObject
                    {
                        ["hiddenByAnimeStudio"] = true,
                        ["reason"] = "non_primary_lod_preview",
                        ["group"] = item.Lod.BaseName,
                        ["selectedNode"] = selected.Name,
                        ["selectedLod"] = selected.Lod.Lod,
                        ["hiddenLod"] = item.Lod.Lod,
                        ["hiddenMesh"] = mesh,
                    };
                    node.Remove("mesh");
                    node.Remove("skin");
                    result.Add(new HiddenLodMeshReport(
                        Node: item.Index,
                        Name: item.Name,
                        Mesh: mesh,
                        Group: item.Lod.BaseName,
                        HiddenLod: item.Lod.Lod,
                        SelectedNode: selected.Name,
                        SelectedLod: selected.Lod.Lod));
                }
            }

            return result;
        }

        private static bool TryParseLodNode(string name, out LodNodeInfo info)
        {
            info = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            var match = Regex.Match(name, @"^(?<base>.+?)(?:[_\-. ]lod(?<lod>\d+))$", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return false;
            }

            if (!int.TryParse(match.Groups["lod"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lod))
            {
                return false;
            }

            info = new LodNodeInfo(match.Groups["base"].Value, lod);
            return true;
        }

        private static void WriteFailureReport(
            string outputDir,
            string sourceGltf,
            string outputGltfPath,
            string requestPath,
            string resultPath,
            string clipName,
            string message,
            JObject result)
        {
            if (string.IsNullOrWhiteSpace(outputDir))
            {
                return;
            }

            Directory.CreateDirectory(outputDir);
            DeleteStaleFailedGltf(outputDir, outputGltfPath);
            var report = new
            {
                generatedAt = DateTime.UtcNow.ToString("O"),
                status = "failed",
                message,
                sourceGltf,
                outputGltf = outputGltfPath,
                request = requestPath,
                result = resultPath,
                clipName,
                unityClipIsHumanMotion = (bool?)result?["isHumanMotion"] ?? false,
                unityBakeHelperVersion = (int?)result?["helperVersion"] ?? 1,
                unityBakeRequestedAvatarAsset = (string)result?["requestedAvatarAsset"],
                unityBakeImportedAvatarAsset = (string)result?["importedAvatarAsset"],
                unityBakeImportedAvatarAssetValid = (bool?)result?["importedAvatarAssetValid"] ?? false,
                unityBakeRequestedAnimationClip = (string)result?["requestedAnimationClip"],
                unityBakeImportedAnimationClip = (string)result?["importedAnimationClip"],
                unityBakeAnimationClipSource = (string)result?["animationClipSource"],
                unityBakeClipFilterMode = ResolveClipFilterMode(requestPath, result),
            };
            var reportPath = Path.Combine(outputDir, "unity_bake_apply_report.json");
            File.WriteAllText(reportPath, JsonConvert.SerializeObject(report, Formatting.Indented));
            WriteAnimationPreviewStatusReadme(
                outputDir,
                BuildFailurePreviewStatusMetadata("failed", message, reportPath, outputGltfPath),
                reportPath,
                outputGltfPath);
            Logger.Info($"Baked apply report: {reportPath}");
        }

        private static void WriteModelGateFailureReport(
            string outputDir,
            string sourceGltf,
            string outputGltfPath,
            string requestPath,
            string resultPath,
            string clipName,
            string message,
            JObject result,
            JObject modelGate)
        {
            if (string.IsNullOrWhiteSpace(outputDir))
            {
                return;
            }

            Directory.CreateDirectory(outputDir);
            DeleteStaleFailedGltf(outputDir, outputGltfPath);
            var report = new JObject
            {
                ["generatedAt"] = DateTime.UtcNow.ToString("O"),
                ["status"] = "blocked",
                ["reason"] = "model_not_animation_ready",
                ["message"] = message,
                ["sourceGltf"] = sourceGltf,
                ["outputGltf"] = outputGltfPath,
                ["request"] = requestPath,
                ["result"] = resultPath,
                ["clipName"] = clipName,
                ["unityClipIsHumanMotion"] = (bool?)result?["isHumanMotion"] ?? false,
                ["unityBakeHelperVersion"] = (int?)result?["helperVersion"] ?? 1,
                ["modelGate"] = modelGate,
                ["rule"] = "模型 glTF 单独通过 Mesh/UV/材质/贴图/skin/bbox 静态验收后，才允许 Unity bake 写回可播放 glTF；白模、缺材质贴图或 skin 异常只保留诊断报告。",
            };
            var reportPath = Path.Combine(outputDir, "unity_bake_apply_report.json");
            File.WriteAllText(reportPath, report.ToString(Formatting.Indented));
            WriteAnimationPreviewStatusReadme(
                outputDir,
                BuildFailurePreviewStatusMetadata("blocked", message, reportPath, outputGltfPath, "model_not_animation_ready"),
                reportPath,
                outputGltfPath);
            Logger.Info($"Baked apply report: {reportPath}");
        }

        private static JObject BuildAnimationPreviewStatusMetadata(
            string status,
            string message,
            JObject animationSolve,
            string reportPath,
            string outputGltfPath)
        {
            var productionStatus = (string)animationSolve?["productionStatus"] ?? "unknown";
            var writesReusable = (bool?)animationSolve?["writesReusableGltfTrsCandidate"] ?? false;
            var productionReady = (bool?)animationSolve?["productionReady"] ?? false;
            var blockedReasons = animationSolve?["reusableCandidateBlockedReasons"] as JArray ?? new JArray();
            return new JObject
            {
                ["schema"] = "AnimeStudio.AnimationPreviewStatus.v1",
                ["status"] = status ?? "unknown",
                ["productionStatus"] = productionStatus,
                ["productionReady"] = productionReady,
                ["writesReusableGltfTrsCandidate"] = writesReusable,
                ["diagnosticOnly"] = !writesReusable || !productionReady,
                ["message"] = message ?? string.Empty,
                ["report"] = string.IsNullOrWhiteSpace(reportPath)
                    ? "unity_bake_apply_report.json"
                    : Path.GetFileName(reportPath),
                ["output"] = string.IsNullOrWhiteSpace(outputGltfPath)
                    ? string.Empty
                    : Path.GetFileName(outputGltfPath),
                ["blockedReasons"] = new JArray(blockedReasons),
                ["rule"] = writesReusable && productionReady
                    ? "This preview is a reusable glTF TRS candidate only because the apply report passed production gates."
                    : "This preview is diagnostic until the apply report, deterministic relation, model-first gate, solver coverage, and clear visual validation pass.",
            };
        }

        private static JObject BuildFailurePreviewStatusMetadata(
            string status,
            string message,
            string reportPath,
            string outputGltfPath,
            string reason = null)
        {
            var blockedReasons = new JArray();
            if (!string.IsNullOrWhiteSpace(reason))
            {
                blockedReasons.Add(reason);
            }
            return new JObject
            {
                ["schema"] = "AnimeStudio.AnimationPreviewStatus.v1",
                ["status"] = status ?? "failed",
                ["productionStatus"] = reason ?? status ?? "failed",
                ["productionReady"] = false,
                ["writesReusableGltfTrsCandidate"] = false,
                ["diagnosticOnly"] = true,
                ["message"] = message ?? string.Empty,
                ["report"] = string.IsNullOrWhiteSpace(reportPath)
                    ? "unity_bake_apply_report.json"
                    : Path.GetFileName(reportPath),
                ["output"] = string.IsNullOrWhiteSpace(outputGltfPath)
                    ? string.Empty
                    : Path.GetFileName(outputGltfPath),
                ["blockedReasons"] = blockedReasons,
                ["rule"] = "No reusable animation preview was written. Read the apply report before retrying or using this candidate.",
            };
        }

        private static void AttachAnimationPreviewStatus(JObject gltf, JObject previewStatus)
        {
            if (gltf == null || previewStatus == null)
            {
                return;
            }

            var asset = gltf["asset"] as JObject;
            if (asset == null)
            {
                asset = new JObject
                {
                    ["version"] = "2.0",
                };
                gltf["asset"] = asset;
            }
            var extras = asset["extras"] as JObject;
            if (extras == null)
            {
                extras = new JObject();
                asset["extras"] = extras;
            }
            // 浏览器和后处理可以直接从 glTF 读这个状态，避免只凭文件可播放误判。
            extras["animeStudioAnimationPreview"] = previewStatus.DeepClone();
        }

        private static void WriteAnimationPreviewStatusReadme(
            string outputDir,
            JObject previewStatus,
            string reportPath,
            string outputGltfPath)
        {
            if (string.IsNullOrWhiteSpace(outputDir) || previewStatus == null)
            {
                return;
            }

            Directory.CreateDirectory(outputDir);
            var status = (string)previewStatus["status"] ?? "unknown";
            var productionStatus = (string)previewStatus["productionStatus"] ?? "unknown";
            var productionReady = (bool?)previewStatus["productionReady"] ?? false;
            var writesReusable = (bool?)previewStatus["writesReusableGltfTrsCandidate"] ?? false;
            var diagnosticOnly = (bool?)previewStatus["diagnosticOnly"] ?? true;
            var blockedReasons = previewStatus["blockedReasons"]?
                .OfType<JValue>()
                .Select(x => (string)x)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? Array.Empty<string>();

            var lines = new List<string>
            {
                "# Animation Preview Status",
                string.Empty,
                $"Status: `{status}`",
                $"Production status: `{productionStatus}`",
                $"Production ready: `{productionReady.ToString().ToLowerInvariant()}`",
                $"Reusable glTF TRS candidate: `{writesReusable.ToString().ToLowerInvariant()}`",
                $"Diagnostic only: `{diagnosticOnly.ToString().ToLowerInvariant()}`",
                string.Empty,
                "这份文件是给人看的动画预览状态入口。glTF 能打开、能播放、有 TRS channel，只说明管线跑通；是否可复用以 `unity_bake_apply_report.json` 和清晰截图验收为准。",
                string.Empty,
                $"Preview glTF: `{Path.GetFileName(outputGltfPath)}`",
                $"Apply report: `{Path.GetFileName(reportPath)}`",
            };
            if (!string.IsNullOrWhiteSpace((string)previewStatus["message"]))
            {
                lines.Add(string.Empty);
                lines.Add("Message:");
                lines.Add((string)previewStatus["message"]);
            }
            if (blockedReasons.Length > 0)
            {
                lines.Add(string.Empty);
                lines.Add("Blocked reasons:");
                foreach (var reason in blockedReasons)
                {
                    lines.Add($"- `{reason}`");
                }
            }

            File.WriteAllText(
                Path.Combine(outputDir, "ANIMATION_PREVIEW_STATUS.md"),
                string.Join(Environment.NewLine, lines) + Environment.NewLine);
        }

        private static void DeleteStaleFailedGltf(string outputDir, string outputGltfPath)
        {
            if (string.IsNullOrWhiteSpace(outputDir) || string.IsNullOrWhiteSpace(outputGltfPath))
            {
                return;
            }

            var fullOutputDir = Path.GetFullPath(outputDir);
            var fullGltfPath = Path.GetFullPath(outputGltfPath);
            if (!fullGltfPath.StartsWith(fullOutputDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetDirectoryName(fullGltfPath), fullOutputDir, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (File.Exists(fullGltfPath))
            {
                File.Delete(fullGltfPath);
            }
        }

        private static bool IsStandaloneIncompleteHumanoidClip(JObject request, out string reason)
        {
            reason = null;
            var animationAsset = (string)request["animeStudioAssets"]?["animation"]?["animationAsset"];
            if (string.IsNullOrWhiteSpace(animationAsset) || !File.Exists(animationAsset))
            {
                return false;
            }

            JObject sidecar;
            try
            {
                sidecar = JObject.Parse(File.ReadAllText(animationAsset));
            }
            catch
            {
                return false;
            }

            var hasMuscleClip = (bool?)sidecar["humanoid"]?["hasMuscleClip"] ?? false;
            var muscleBindingCount = (int?)sidecar["humanoid"]?["muscleBindingCount"] ?? 0;
            if (!hasMuscleClip || muscleBindingCount > 7)
            {
                return false;
            }

            var bodyTransformBindingCount = sidecar["bindings"]?
                .OfType<JObject>()
                .Count(IsCoreBodyTransformBinding) ?? 0;
            if (bodyTransformBindingCount > 0)
            {
                return false;
            }

            reason = $"Animation sidecar reports only {muscleBindingCount} Humanoid/root curve(s) and no core body Transform bindings.";
            return true;
        }

        private static bool IsCoreBodyTransformBinding(JObject binding)
        {
            if (!string.Equals((string)binding["typeID"], "Transform", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var path = ((string)binding["path"])?.Replace('\\', '/').Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var last = path.Split('/').LastOrDefault()?.Trim();
            if (string.IsNullOrWhiteSpace(last))
            {
                return false;
            }

            // 只把真正人体主骨骼算作 standalone 身体驱动。
            // 附件路径可能挂在 Head/Spine/Thigh 下，最后一级通常带 + 或附件名，
            // 不能因此误判为完整身体动画。
            var normalized = last.Replace("_", " ").Replace("-", " ");
            var coreNames = new[]
            {
                "Bip001 Pelvis",
                "Bip001 Spine",
                "Bip001 Spine1",
                "Bip001 Spine2",
                "Bip001 Neck",
                "Bip001 Head",
                "Bip001 L Clavicle",
                "Bip001 R Clavicle",
                "Bip001 L UpperArm",
                "Bip001 R UpperArm",
                "Bip001 L Forearm",
                "Bip001 R Forearm",
                "Bip001 L Hand",
                "Bip001 R Hand",
                "Bip001 L Thigh",
                "Bip001 R Thigh",
                "Bip001 L Calf",
                "Bip001 R Calf",
                "Bip001 L Foot",
                "Bip001 R Foot",
            };
            return coreNames.Any(x => string.Equals(normalized, x, StringComparison.OrdinalIgnoreCase));
        }

        private static AvatarTrustReport BuildAvatarTrustReport(JObject request, JObject result)
        {
            var requestedAvatarAsset = (string)request["unityAssetPaths"]?["avatarAsset"];
            if (!string.IsNullOrWhiteSpace(requestedAvatarAsset) && IsImportedAvatarAssetResultValid(result))
            {
                if (!IsImportedAvatarBindingOk(result, out var bindingMessage))
                {
                    return new AvatarTrustReport(
                        TrustedProductionBake: false,
                        Source: "imported_unity_avatar_asset_binding_mismatch",
                        Message: bindingMessage);
                }

                return new AvatarTrustReport(
                    TrustedProductionBake: true,
                    Source: "imported_unity_avatar_asset",
                    Message: null);
            }
            if (!string.IsNullOrWhiteSpace(requestedAvatarAsset))
            {
                return new AvatarTrustReport(
                    TrustedProductionBake: false,
                    Source: "imported_unity_avatar_asset_invalid",
                    Message: "Unity bake request explicitly supplied unityAssetPaths.avatarAsset, but the Unity result did not report a valid Humanoid Avatar. The result must not fall back to HumanDescription, AvatarConstant, internalSolver, or glTF rest pose trust.");
            }

            var avatar = request["animeStudioAssets"]?["model"]?["avatar"] as JObject;
            if (avatar == null)
            {
                return new AvatarTrustReport(
                    TrustedProductionBake: false,
                    Source: "missing_avatar",
                    Message: "Unity bake request did not contain Avatar metadata, so the baked glTF must be treated as diagnostic output.");
            }

            var skeletonBoneCount = avatar["skeletonBones"] is JArray skeletonBones ? skeletonBones.Count : 0;
            var humanBonesSource = (string)avatar["humanBonesSource"];
            if (skeletonBoneCount > 0)
            {
                return new AvatarTrustReport(
                    TrustedProductionBake: true,
                    Source: "human_description_skeleton_bones",
                    Message: null);
            }

            var requestedModelPrefab = (string)request["unityAssetPaths"]?["modelPrefab"];
            if (!string.IsNullOrWhiteSpace(requestedModelPrefab) && ((bool?)result?["avatarValid"] ?? false))
            {
                return new AvatarTrustReport(
                    TrustedProductionBake: true,
                    Source: "unity_prefab_original_animator_avatar",
                    Message: null);
            }

            var rigRestPoseSource = (string)result?["rigRestPoseSource"];
            var rigRestPoseApplied = (bool?)result?["rigRestPoseApplied"] ?? false;
            if (IsImportedAvatarRestPoseSource(rigRestPoseSource)
                && rigRestPoseApplied
                && ((bool?)result?["avatarValid"] ?? false))
            {
                return new AvatarTrustReport(
                    TrustedProductionBake: true,
                    Source: "imported_unity_avatar_asset",
                    Message: null);
            }

            if (HasCompleteAvatarOracle(avatar) && ((bool?)result?["avatarValid"] ?? false))
            {
                return new AvatarTrustReport(
                    TrustedProductionBake: false,
                    Source: string.IsNullOrWhiteSpace(rigRestPoseSource) ? "avatar_constant_oracle_diagnostic" : rigRestPoseSource,
                    Message: "Unity bake used AvatarConstant/internalSolver metadata to rebuild a temporary Avatar. This is deterministic Unity source metadata, but it is not the original UnityEngine.Avatar or complete HumanDescription.skeletonBones; Genshin samples show this path can produce wrong arms/legs, so it is diagnostic only and must not be counted as production bake.");
            }

            if (string.Equals(humanBonesSource, "avatar.internalSolver.humanBoneIndex", StringComparison.OrdinalIgnoreCase))
            {
                return new AvatarTrustReport(
                    TrustedProductionBake: false,
                    Source: "internal_solver_derived_human_bones",
                    Message: "Unity bake used HumanBone mapping reconstructed from Avatar internalSolver metadata. Without original Unity prefab Avatar or HumanDescription.skeletonBones, the helper has to build a temporary Avatar; this is diagnostic only and must not be accepted as production animation.");
            }

            return new AvatarTrustReport(
                TrustedProductionBake: false,
                Source: string.IsNullOrWhiteSpace(humanBonesSource) ? "missing_human_description_skeleton_bones" : humanBonesSource,
                Message: "Unity bake did not include original HumanDescription.skeletonBones. Treat this baked glTF as needing visual validation or metadata refresh before production use.");
        }

        private static HumanoidRuntimeSamplingReport BuildHumanoidRuntimeSamplingReport(JObject result)
        {
            var editorCurves = result["editorCurveTracks"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var editorCurveCategorySummary = BuildEditorCurveCategorySummary(editorCurves);
            var dynamicEditorCurves = editorCurves.Count(IsDynamicEditorCurveTrack);
            var animatorEditorCurves = editorCurves
                .Where(x => string.Equals((string)x["type"], "UnityEngine.Animator", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var dynamicAnimatorEditorCurves = animatorEditorCurves.Count(IsDynamicEditorCurveTrack);
            var hasOnlyConstantEditorCurves =
                editorCurves.Length > 0 &&
                dynamicEditorCurves == 0;

            var poseSamples = result["humanoidPoseSamples"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var (muscleCount, varyingMuscles, maxMuscleDelta) = CountVaryingHumanPoseMuscles(poseSamples);
            var maxBodyPositionDelta = MaxHumanPoseVectorDelta(poseSamples, "bodyPosition", "x", "y", "z");
            var maxBodyRotationDelta = MaxHumanPoseRotationDelta(poseSamples);
            var hasRuntimePoseMotion =
                varyingMuscles > 0 ||
                maxBodyPositionDelta > 0.00001f ||
                maxBodyRotationDelta > 0.00001f;
            var hasEditorCurvesButNoRuntimePoseMotion =
                dynamicAnimatorEditorCurves > 0 &&
                poseSamples.Length > 1 &&
                !hasRuntimePoseMotion;
            var status = hasEditorCurvesButNoRuntimePoseMotion
                ? "runtime_pose_not_driven"
                : hasOnlyConstantEditorCurves
                ? "constant_editor_curves"
                : poseSamples.Length == 0
                ? "not_probed"
                : "ok";
            var message = hasEditorCurvesButNoRuntimePoseMotion
                ? "Unity AnimationUtility can see dynamic Humanoid/Animator editor curves, but HumanPose sampling did not produce any frame-varying muscle/body pose. This usually means the recovered .anim exposes editor curves while the runtime m_MuscleClip payload is empty or not driving Humanoid playback; treat this bake as diagnostic, not a trusted glTF animation."
                : hasOnlyConstantEditorCurves
                ? "Unity AnimationUtility can see editor curves, but all sampled values are constant. This is a pose/base/additive layer or controller-context fragment unless another deterministic controller state proves it composes into a dynamic action."
                : null;

            return new HumanoidRuntimeSamplingReport(
                EditorCurveTrackCount: editorCurves.Length,
                AnimatorEditorCurveTrackCount: animatorEditorCurves.Length,
                DynamicEditorCurveTrackCount: dynamicEditorCurves,
                DynamicAnimatorEditorCurveTrackCount: dynamicAnimatorEditorCurves,
                HumanoidPoseSampleCount: poseSamples.Length,
                HumanoidMuscleCount: muscleCount,
                VaryingHumanoidMuscleCount: varyingMuscles,
                MaxHumanoidMuscleDelta: maxMuscleDelta,
                MaxBodyPositionDelta: maxBodyPositionDelta,
                MaxBodyRotationDelta: maxBodyRotationDelta,
                HasEditorCurvesButNoRuntimePoseMotion: hasEditorCurvesButNoRuntimePoseMotion,
                HasOnlyConstantEditorCurves: hasOnlyConstantEditorCurves,
                EditorCurveCategorySummary: editorCurveCategorySummary,
                Status: status,
                Message: message);
        }

        private static UnityBakeTrackSourceSelection SelectUnityBakeTrackSource(JObject result, bool requiresHumanoidBake, JObject[] nodes)
        {
            var runtimeTracks = result["tracks"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var editorCurveSetHumanPoseTracks = result["editorCurveSetHumanPoseTransformTracks"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var animationModeSampleClipTracks = result["animationModeSampleClipTransformTracks"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var runtimeSummary = SummarizeBakeTracks(runtimeTracks, nodes);
            var editorCurveSummary = SummarizeBakeTracks(editorCurveSetHumanPoseTracks, nodes);
            var animationModeSummary = SummarizeBakeTracks(animationModeSampleClipTracks, nodes);
            var canUseAnimationModeSampleClip =
                requiresHumanoidBake &&
                runtimeSummary.FrameVaryingCoreBodyTrackCount == 0 &&
                animationModeSummary.FrameVaryingCoreBodyTrackCount > 0;
            if (canUseAnimationModeSampleClip)
            {
                return new UnityBakeTrackSourceSelection(
                    Tracks: animationModeSampleClipTracks,
                    Source: "animation_mode_sample_clip_diagnostic",
                    Reason: "Runtime PlayableGraph tracks were static, but Unity Editor AnimationMode.SampleAnimationClip produced frame-varying core body tracks.",
                    UsesEditorCurveSetHumanPose: false,
                    UsesAnimationModeSampleClip: true,
                    RuntimeSummary: runtimeSummary.ToJson(),
                    EditorCurveSetHumanPoseSummary: editorCurveSummary.ToJson(),
                    AnimationModeSampleClipSummary: animationModeSummary.ToJson());
            }
            var canUseEditorCurveSetHumanPose =
                requiresHumanoidBake &&
                runtimeSummary.FrameVaryingCoreBodyTrackCount == 0 &&
                editorCurveSummary.FrameVaryingCoreBodyTrackCount > 0;
            if (canUseEditorCurveSetHumanPose)
            {
                return new UnityBakeTrackSourceSelection(
                    Tracks: editorCurveSetHumanPoseTracks,
                    Source: "editor_curve_set_human_pose_diagnostic",
                    Reason: "Runtime PlayableGraph tracks were static, but direct HumanPoseHandler.SetHumanPose sampling from editor muscle curves produced frame-varying core body tracks.",
                    UsesEditorCurveSetHumanPose: true,
                    UsesAnimationModeSampleClip: false,
                    RuntimeSummary: runtimeSummary.ToJson(),
                    EditorCurveSetHumanPoseSummary: editorCurveSummary.ToJson(),
                    AnimationModeSampleClipSummary: animationModeSummary.ToJson());
            }

            return new UnityBakeTrackSourceSelection(
                Tracks: runtimeTracks,
                Source: "runtime_playable_graph",
                Reason: "Using Unity runtime PlayableGraph/Animator tracks.",
                UsesEditorCurveSetHumanPose: false,
                UsesAnimationModeSampleClip: false,
                RuntimeSummary: runtimeSummary.ToJson(),
                EditorCurveSetHumanPoseSummary: editorCurveSummary.ToJson(),
                AnimationModeSampleClipSummary: animationModeSummary.ToJson());
        }

        private static BakeTrackSourceSummary SummarizeBakeTracks(JObject[] tracks, JObject[] nodes)
        {
            var nodePathToIndex = BuildNodePathIndex(nodes);
            var changed = 0;
            var matched = 0;
            var frameVarying = 0;
            var frameVaryingCoreBody = 0;
            foreach (var track in tracks ?? Array.Empty<JObject>())
            {
                if ((bool?)track["changed"] != true)
                {
                    continue;
                }

                changed++;
                var path = NormalizeBakePath((string)track["path"], nodes);
                if (!nodePathToIndex.ContainsKey(path))
                {
                    continue;
                }

                matched++;
                if (!IsFrameVaryingRawBakeTrack(track))
                {
                    continue;
                }

                frameVarying++;
                if (IsCoreBodyBonePath(path))
                {
                    frameVaryingCoreBody++;
                }
            }

            return new BakeTrackSourceSummary(
                TotalTrackCount: tracks?.Length ?? 0,
                ChangedTrackCount: changed,
                MatchedTrackCount: matched,
                FrameVaryingTrackCount: frameVarying,
                FrameVaryingCoreBodyTrackCount: frameVaryingCoreBody);
        }

        private static bool IsFrameVaryingRawBakeTrack(JObject track)
        {
            return MaxRawVectorDelta(track["translations"] as JArray, "x", "y", "z") > 0.00001f
                || MaxRawVectorDelta(track["scales"] as JArray, "x", "y", "z") > 0.00001f
                || MaxRawVectorDelta(track["rotations"] as JArray, "x", "y", "z", "w") > 0.00001f;
        }

        private static float MaxRawVectorDelta(JArray samples, params string[] fields)
        {
            var items = samples?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            if (items.Length < 2)
            {
                return 0f;
            }

            var first = items[0];
            var max = 0f;
            foreach (var sample in items.Skip(1))
            {
                foreach (var field in fields)
                {
                    var delta = Math.Abs(((float?)sample[field] ?? 0f) - ((float?)first[field] ?? 0f));
                    if (delta > max)
                    {
                        max = delta;
                    }
                }
            }

            return max;
        }

        private static JObject BuildAnimatorControllerRecoverableSkippedLayerSummary(JObject result)
        {
            var skippedDiagnostics = result?["animatorControllerSkippedUntrustedLayerDiagnostics"] as JArray ?? new JArray();
            var blendTreeDiagnostics = result?["animatorControllerBlendTreeDiagnostics"] as JArray ?? new JArray();
            var sampledLayerNames = new HashSet<string>(
                (result?["animatorControllerDiagnosticSampledSkippedLayerNames"] as JArray)
                    ?.Values<string>()
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    ?? Enumerable.Empty<string>(),
                StringComparer.Ordinal);
            var recoverableLayers = new JArray();
            var layerNames = new HashSet<string>(StringComparer.Ordinal);
            var parameterNames = new HashSet<string>(StringComparer.Ordinal);
            var recoverableCount = 0;
            var simple1DDefaultSelectedCount = 0;
            var diagnosticSampledRecoverableCount = 0;
            var fallbackZeroBlendParameterCount = 0;
            var parameterSources = new HashSet<string>(StringComparer.Ordinal);

            foreach (var item in skippedDiagnostics.OfType<JObject>())
            {
                var layerName = (string)item["layerName"] ?? string.Empty;
                var recoverableMotion = (string)item["recoverableMotionName"];
                var selectedMotion = (string)item["selectedChildMotionName"];
                var isRecoverable = !string.IsNullOrWhiteSpace(recoverableMotion)
                    || !string.IsNullOrWhiteSpace(selectedMotion);
                if (!isRecoverable)
                {
                    continue;
                }

                recoverableCount++;
                if (!string.IsNullOrWhiteSpace(layerName))
                {
                    layerNames.Add(layerName);
                }

                var sampled = sampledLayerNames.Contains(layerName);
                if (sampled)
                {
                    diagnosticSampledRecoverableCount++;
                }

                var blendParameter = (string)item["blendParameter"];
                if (!string.IsNullOrWhiteSpace(blendParameter))
                {
                    parameterNames.Add(blendParameter);
                }

                var defaultValue = (float?)item["defaultParameterValue"] ?? 0f;
                var defaultParameterSource = (string)item["defaultParameterSource"];
                if (!string.IsNullOrWhiteSpace(defaultParameterSource))
                {
                    parameterSources.Add(defaultParameterSource);
                }
                if (string.Equals(defaultParameterSource, "blendEventFallbackZero", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(defaultParameterSource, "missingParameterFallbackZero", StringComparison.OrdinalIgnoreCase))
                {
                    fallbackZeroBlendParameterCount++;
                }

                var selectedThreshold = (float?)item["selectedChildThreshold"] ?? 0f;
                var isSimple1DDefaultSelection =
                    string.Equals((string)item["blendType"], "Simple1D", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(selectedMotion)
                    && (string.IsNullOrWhiteSpace(recoverableMotion)
                        || string.Equals(recoverableMotion, selectedMotion, StringComparison.Ordinal))
                    && Math.Abs(defaultValue - selectedThreshold) <= 0.0001f;
                if (isSimple1DDefaultSelection)
                {
                    simple1DDefaultSelectedCount++;
                }

                recoverableLayers.Add(new JObject
                {
                    ["layerName"] = layerName,
                    ["sourceLayerIndex"] = (int?)item["sourceLayerIndex"] ?? -1,
                    ["stateName"] = (string)item["stateName"],
                    ["sampledDiagnostic"] = sampled,
                    ["isAdditive"] = (bool?)item["isAdditive"] ?? false,
                    ["defaultWeight"] = (float?)item["defaultWeight"] ?? 0f,
                    ["hasSkeletonMask"] = (bool?)item["hasSkeletonMask"] ?? false,
                    ["skeletonMaskEntryCount"] = (int?)item["skeletonMaskEntryCount"] ?? 0,
                    ["hasBlendTree"] = (bool?)item["hasBlendTree"] ?? false,
                    ["blendType"] = (string)item["blendType"],
                    ["blendParameter"] = blendParameter,
                    ["defaultParameterValue"] = defaultValue,
                    ["defaultParameterSource"] = defaultParameterSource,
                    ["selectedChildMotionName"] = selectedMotion,
                    ["selectedChildThreshold"] = selectedThreshold,
                    ["selectedChildSelectionRule"] = (string)item["selectedChildSelectionRule"],
                    ["recoverableMotionName"] = recoverableMotion,
                    ["simple1DDefaultSelection"] = isSimple1DDefaultSelection,
                });
            }

            return new JObject
            {
                ["recoverableSkippedLayerCount"] = recoverableCount,
                ["diagnosticSampledRecoverableSkippedLayerCount"] = diagnosticSampledRecoverableCount,
                ["simple1DDefaultSelectedLayerCount"] = simple1DDefaultSelectedCount,
                ["fallbackZeroBlendParameterLayerCount"] = fallbackZeroBlendParameterCount,
                ["blendTreeDiagnosticCount"] = blendTreeDiagnostics.Count,
                ["layerNames"] = new JArray(layerNames.OrderBy(x => x, StringComparer.Ordinal)),
                ["blendParameterNames"] = new JArray(parameterNames.OrderBy(x => x, StringComparer.Ordinal)),
                ["defaultParameterSources"] = new JArray(parameterSources.OrderBy(x => x, StringComparer.Ordinal)),
                ["layers"] = recoverableLayers,
                ["rule"] = "diagnostic_only: summarizes normally skipped AnimatorController layers that still have recovered motion or Simple1D default BlendTree evidence. It helps triage controller context quality, but does not make the bake production reusable.",
            };
        }

        private static JObject BuildUnityBakeAnimationSolveReport(
            string status,
            bool requiresHumanoidBake,
            bool unityClipIsHumanMotion,
            AvatarTrustReport avatarTrust,
            int writtenTracks,
            int frameVaryingTracks,
            int coreBodyFrameVaryingTracks,
            int invalidSkinTargetCount,
            bool diagnosticRebuildEditorCurveClip,
            bool diagnosticIgnoreImportedAvatar,
            bool isTransformOnlyClipFilter,
            bool legacyUnityBakePolicyOverride,
            string legacyUnityBakePolicyOverrideReason,
            string animatorControllerSamplingMode,
            int animatorControllerAdditionalLayerMaskCount,
            bool animatorControllerLayerMasksApplied,
            bool animatorControllerLayerMixerBypassesControllerBlendTrees,
            int animatorControllerUnmaskedOverrideLayerCount,
            int animatorControllerSkippedUntrustedLayerCount,
            int animatorControllerDiagnosticSampledSkippedLayerCount,
            bool selectedControllerLayerClipNeedsBaseContext,
            HumanoidRuntimeSamplingReport humanoidRuntimeSampling,
            JObject requestUnsupportedHumanPoseCurves,
            JObject editorCurveHumanPoseDiagnostic,
            JObject editorCurveIkGoalDriverDiagnostic,
            JObject animatorControllerRuntimeParameterSummary,
            bool usesEditorCurveSetHumanPose,
            bool usesAnimationModeSampleClip)
        {
            var isUnityDerivedPlayableGraphCandidate = IsUnityDerivedPlayableGraphCandidate(
                requiresHumanoidBake,
                unityClipIsHumanMotion,
                avatarTrust,
                writtenTracks,
                frameVaryingTracks,
                coreBodyFrameVaryingTracks,
                invalidSkinTargetCount,
                diagnosticRebuildEditorCurveClip,
                diagnosticIgnoreImportedAvatar,
                isTransformOnlyClipFilter);
            var generatedControllerDiagnostic = IsGeneratedAnimatorControllerSamplingMode(animatorControllerSamplingMode);
            var missingAnimatorControllerAsset = IsAnimatorControllerMissingSamplingMode(animatorControllerSamplingMode);
            var needsAnimatorControllerContext = string.Equals(status, "needs_animator_controller_context", StringComparison.OrdinalIgnoreCase);
            var hasUnappliedAnimatorControllerLayerMasks = animatorControllerAdditionalLayerMaskCount > 0 && !animatorControllerLayerMasksApplied;
            var dynamicLimbGoalCurveCount =
                (int?)editorCurveHumanPoseDiagnostic?["dynamicLimbGoalCurveCount"]
                ?? (int?)requestUnsupportedHumanPoseCurves?["dynamicLimbGoalCurveCount"]
                ?? 0;
            var hasDynamicLimbGoalCurves = dynamicLimbGoalCurveCount > 0;
            var ikGoalDriverCallCount = (int?)editorCurveIkGoalDriverDiagnostic?["callCount"] ?? 0;
            var ikGoalDriverAppliedGoalCount = (int?)editorCurveIkGoalDriverDiagnostic?["appliedGoalCount"] ?? 0;
            var usedIkGoalDriverDiagnostic = hasDynamicLimbGoalCurves && ikGoalDriverCallCount > 0 && ikGoalDriverAppliedGoalCount > 0;
            var ikGoalDriverVerification = BuildIkGoalDriverVerificationSummary(
                editorCurveIkGoalDriverDiagnostic,
                hasDynamicLimbGoalCurves,
                usedIkGoalDriverDiagnostic);
            var hasZeroDefaultRuntimeLayerGate =
                ((int?)animatorControllerRuntimeParameterSummary?["zeroDefaultLayerWeightHintCount"] ?? 0) > 0;
            var visualValidationRequired =
                requiresHumanoidBake ||
                legacyUnityBakePolicyOverride ||
                isUnityDerivedPlayableGraphCandidate ||
                string.Equals(status, "needs_review", StringComparison.OrdinalIgnoreCase);
            var writesReusableCandidate = isUnityDerivedPlayableGraphCandidate
                && !generatedControllerDiagnostic
                && !missingAnimatorControllerAsset
                && !needsAnimatorControllerContext
                && !hasUnappliedAnimatorControllerLayerMasks
                && !animatorControllerLayerMixerBypassesControllerBlendTrees
                && animatorControllerUnmaskedOverrideLayerCount == 0
                && animatorControllerSkippedUntrustedLayerCount == 0
                && animatorControllerDiagnosticSampledSkippedLayerCount == 0
                && !selectedControllerLayerClipNeedsBaseContext
                && !visualValidationRequired
                && !usesEditorCurveSetHumanPose
                && !usesAnimationModeSampleClip
                && !hasZeroDefaultRuntimeLayerGate
                && !hasDynamicLimbGoalCurves;
            var productionReady =
                string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) &&
                !visualValidationRequired;
            var blockedReasons = new JArray();
            if (!requiresHumanoidBake)
            {
                blockedReasons.Add("not_humanoid_bake");
            }
            if (!unityClipIsHumanMotion)
            {
                blockedReasons.Add("unity_clip_is_not_human_motion");
            }
            if (!avatarTrust.TrustedProductionBake)
            {
                blockedReasons.Add("avatar_not_trusted_for_production_bake");
            }
            if (writtenTracks <= 0)
            {
                blockedReasons.Add("no_written_gltf_trs_tracks");
            }
            if (frameVaryingTracks <= 0)
            {
                blockedReasons.Add("no_frame_varying_tracks");
            }
            if (coreBodyFrameVaryingTracks <= 0)
            {
                blockedReasons.Add("no_frame_varying_core_body_tracks");
            }
            if (invalidSkinTargetCount > 0)
            {
                blockedReasons.Add("animation_targets_include_nodes_outside_skin");
            }
            if (diagnosticRebuildEditorCurveClip)
            {
                blockedReasons.Add("diagnostic_rebuilt_editor_curve_clip");
            }
            if (diagnosticIgnoreImportedAvatar)
            {
                blockedReasons.Add("diagnostic_ignored_imported_avatar");
            }
            if (isTransformOnlyClipFilter)
            {
                blockedReasons.Add("transform_only_clip_filter");
            }
            if (generatedControllerDiagnostic)
            {
                blockedReasons.Add("generated_animator_controller_context");
            }
            if (missingAnimatorControllerAsset)
            {
                blockedReasons.Add("animator_controller_asset_missing");
            }
            if (needsAnimatorControllerContext)
            {
                blockedReasons.Add("needs_animator_controller_context");
            }
            if (visualValidationRequired)
            {
                blockedReasons.Add("requires_clear_visual_validation");
            }
            if (hasUnappliedAnimatorControllerLayerMasks)
            {
                blockedReasons.Add("animator_controller_layer_masks_not_applied");
            }
            if (animatorControllerLayerMixerBypassesControllerBlendTrees)
            {
                blockedReasons.Add("animator_controller_blend_tree_parameters_bypassed_by_layer_mixer");
            }
            if (animatorControllerUnmaskedOverrideLayerCount > 0)
            {
                blockedReasons.Add("animator_controller_unmasked_override_layers");
            }
            if (animatorControllerSkippedUntrustedLayerCount > 0)
            {
                blockedReasons.Add("animator_controller_untrusted_layers_skipped");
            }
            if (animatorControllerDiagnosticSampledSkippedLayerCount > 0)
            {
                blockedReasons.Add("animator_controller_recoverable_skipped_layers_sampled_diagnostic");
            }
            if (hasZeroDefaultRuntimeLayerGate)
            {
                blockedReasons.Add("animator_controller_runtime_layer_weight_gate_unresolved");
            }
            if (selectedControllerLayerClipNeedsBaseContext)
            {
                blockedReasons.Add("selected_controller_layer_clip_missing_base_layer_context");
            }
            if (hasDynamicLimbGoalCurves && usedIkGoalDriverDiagnostic)
            {
                blockedReasons.Add("limb_ik_goal_driver_diagnostic_unverified");
            }
            else if (hasDynamicLimbGoalCurves)
            {
                blockedReasons.Add("dynamic_limb_goal_curves_not_solved");
            }
            if (humanoidRuntimeSampling.HasOnlyConstantEditorCurves)
            {
                blockedReasons.Add("pose_layer_constant_editor_curves");
            }
            if (humanoidRuntimeSampling.HasEditorCurvesButNoRuntimePoseMotion)
            {
                blockedReasons.Add("editor_curves_not_driving_runtime_pose");
            }
            if (usesEditorCurveSetHumanPose)
            {
                blockedReasons.Add("editor_curve_set_human_pose_diagnostic");
            }
            if (usesAnimationModeSampleClip)
            {
                blockedReasons.Add("animation_mode_sample_clip_diagnostic");
            }

            return new JObject
            {
                ["path"] = requiresHumanoidBake
                    ? usesEditorCurveSetHumanPose
                        ? "UnityEditorCurveHumanPoseDiagnostic"
                    : usesAnimationModeSampleClip
                        ? "UnityAnimationModeSampleClipDiagnostic"
                    : isUnityDerivedPlayableGraphCandidate
                        ? "UnityDerivedPlayableGraph"
                        : "UnityOraclePlayableGraph"
                    : "UnityTransformBake",
                ["writesGltfTrs"] = writtenTracks > 0,
                ["writesReusableGltfTrsCandidate"] = writesReusableCandidate,
                ["reusableCandidateBlockedReasons"] = blockedReasons,
                ["requiresUnityToReproduce"] = true,
                ["requiresVisualValidation"] = visualValidationRequired,
                ["productionReady"] = productionReady,
                ["productionStatus"] = productionReady
                    ? "production_ready"
                    : usesEditorCurveSetHumanPose
                        ? "diagnostic_editor_curve_human_pose_solver"
                    : usesAnimationModeSampleClip
                        ? "diagnostic_animation_mode_sample_clip"
                    : generatedControllerDiagnostic
                        ? "needs_original_animator_controller_context"
                    : missingAnimatorControllerAsset
                        ? "needs_original_animator_controller_asset"
                    : hasUnappliedAnimatorControllerLayerMasks
                        ? "needs_animator_controller_layer_masks"
                    : animatorControllerLayerMixerBypassesControllerBlendTrees
                        ? "needs_animator_controller_blend_tree_context"
                    : animatorControllerUnmaskedOverrideLayerCount > 0
                        ? "needs_animator_controller_layer_masks"
                    : animatorControllerSkippedUntrustedLayerCount > 0
                        ? "needs_animator_controller_layer_context"
                    : animatorControllerDiagnosticSampledSkippedLayerCount > 0
                        ? "diagnostic_recoverable_skipped_layer_sampling"
                    : selectedControllerLayerClipNeedsBaseContext
                        ? "needs_animator_controller_base_layer_context"
                    : usedIkGoalDriverDiagnostic
                        ? "diagnostic_limb_ik_goal_driver"
                    : hasDynamicLimbGoalCurves
                        ? "needs_limb_ik_goal_solver"
                    : isUnityDerivedPlayableGraphCandidate
                        ? "needs_visual_validation"
                        : string.Equals(status, "needs_animator_controller_context", StringComparison.OrdinalIgnoreCase)
                            ? "needs_animator_controller_context"
                            : "diagnostic_or_needs_review",
                ["avatarTrustSource"] = avatarTrust.Source,
                ["trustedAvatar"] = avatarTrust.TrustedProductionBake,
                ["unityClipIsHumanMotion"] = unityClipIsHumanMotion,
                ["writtenTracks"] = writtenTracks,
                ["frameVaryingTracks"] = frameVaryingTracks,
                ["coreBodyFrameVaryingTracks"] = coreBodyFrameVaryingTracks,
                ["invalidSkinTargetCount"] = invalidSkinTargetCount,
                ["diagnosticRebuildEditorCurveClip"] = diagnosticRebuildEditorCurveClip,
                ["diagnosticIgnoreImportedAvatar"] = diagnosticIgnoreImportedAvatar,
                ["clipFilterTransformOnly"] = isTransformOnlyClipFilter,
                ["legacyUnityBakePolicyOverride"] = legacyUnityBakePolicyOverride,
                ["legacyUnityBakePolicyOverrideReason"] = legacyUnityBakePolicyOverrideReason ?? string.Empty,
                ["animatorControllerSamplingMode"] = animatorControllerSamplingMode ?? string.Empty,
                ["animatorControllerAdditionalLayerMaskCount"] = animatorControllerAdditionalLayerMaskCount,
                ["animatorControllerLayerMasksApplied"] = animatorControllerLayerMasksApplied,
                ["animatorControllerSkippedUntrustedLayerCount"] = animatorControllerSkippedUntrustedLayerCount,
                ["animatorControllerDiagnosticSampledSkippedLayerCount"] = animatorControllerDiagnosticSampledSkippedLayerCount,
                ["selectedControllerLayerClipNeedsBaseContext"] = selectedControllerLayerClipNeedsBaseContext,
                ["usesEditorCurveSetHumanPose"] = usesEditorCurveSetHumanPose,
                ["usesAnimationModeSampleClip"] = usesAnimationModeSampleClip,
                ["hasDynamicLimbGoalCurves"] = hasDynamicLimbGoalCurves,
                ["usedIkGoalDriverDiagnostic"] = usedIkGoalDriverDiagnostic,
                ["dynamicLimbGoalCurveCount"] = dynamicLimbGoalCurveCount,
                ["requestUnsupportedHumanPoseCurveSummary"] = requestUnsupportedHumanPoseCurves ?? new JObject(),
                ["ikGoalDriverDiagnosticEnabled"] = (bool?)editorCurveIkGoalDriverDiagnostic?["enabled"] ?? false,
                ["ikGoalDriverCallCount"] = ikGoalDriverCallCount,
                ["ikGoalDriverAppliedGoalCount"] = ikGoalDriverAppliedGoalCount,
                ["ikGoalDriverVerification"] = ikGoalDriverVerification,
                ["humanoidRuntimeSamplingStatus"] = humanoidRuntimeSampling.Status,
                ["editorCurveTrackCount"] = humanoidRuntimeSampling.EditorCurveTrackCount,
                ["dynamicEditorCurveTrackCount"] = humanoidRuntimeSampling.DynamicEditorCurveTrackCount,
                ["animatorEditorCurveTrackCount"] = humanoidRuntimeSampling.AnimatorEditorCurveTrackCount,
                ["dynamicAnimatorEditorCurveTrackCount"] = humanoidRuntimeSampling.DynamicAnimatorEditorCurveTrackCount,
                ["editorCurveCategorySummary"] = humanoidRuntimeSampling.EditorCurveCategorySummary,
                ["hasOnlyConstantEditorCurves"] = humanoidRuntimeSampling.HasOnlyConstantEditorCurves,
                ["hasEditorCurvesButNoRuntimePoseMotion"] = humanoidRuntimeSampling.HasEditorCurvesButNoRuntimePoseMotion,
                ["rule"] = "Unity-derived results are only accepted as reusable animation candidates after they have been written back as ordinary glTF TRS/weights and pass model-first, relation, track coverage, and clear visual validation gates.",
            };
        }

        private static JObject BuildIkGoalDriverVerificationSummary(
            JObject diagnostic,
            bool hasDynamicLimbGoalCurves,
            bool usedIkGoalDriverDiagnostic)
        {
            var result = new JObject
            {
                ["rule"] = "evidence_only: summarizes Unity OnAnimatorIK goal alignment from helper diagnostics. It does not remove visual validation or production blockers.",
                ["hasDynamicLimbGoalCurves"] = hasDynamicLimbGoalCurves,
                ["driverUsed"] = usedIkGoalDriverDiagnostic,
                ["weightRule"] = (string)diagnostic?["weightRule"] ?? string.Empty,
                ["hasExplicitIkGoalWeightCurves"] = (bool?)diagnostic?["hasExplicitIkGoalWeightCurves"] ?? false,
                ["explicitIkGoalWeightCurveCount"] = (int?)diagnostic?["explicitIkGoalWeightCurveCount"] ?? 0,
                ["status"] = !hasDynamicLimbGoalCurves
                    ? "not_required"
                    : usedIkGoalDriverDiagnostic ? "evidence_available" : "driver_not_used",
            };
            if (!hasDynamicLimbGoalCurves || !usedIkGoalDriverDiagnostic || diagnostic == null)
            {
                return result;
            }

            var summaries = diagnostic["goalLayerSummaries"] as JArray;
            if (summaries == null || summaries.Count == 0)
            {
                result["status"] = "missing_goal_layer_summaries";
                return result;
            }

            var goalRows = new JArray();
            var handMaxPostDistance = 0.0;
            var footMaxPostDistance = 0.0;
            var handSampleCount = 0;
            var footSampleCount = 0;
            var handGoalCount = 0;
            var footGoalCount = 0;
            var missingPostIkBoneCount = 0;
            var missingIkReadbackCount = 0;
            var maxIkReadbackDistanceToGoal = 0.0;
            var minIkReadbackPositionWeight = double.MaxValue;
            var maxIkReadbackPositionWeight = 0.0;
            var maxIkReadbackRotationWeight = 0.0;
            var missingIkHintReadbackCount = 0;
            var maxIkHintReadbackDistanceToHint = 0.0;
            var maxIkHintReadbackWeight = 0.0;
            var layerWeightSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var layerIndexes = new HashSet<int>();

            foreach (var row in summaries.OfType<JObject>())
            {
                var goal = (string)row["goal"] ?? string.Empty;
                var sampleCount = (int?)row["sampleCount"] ?? 0;
                var layerIndex = (int?)row["layerIndex"] ?? -1;
                var maxPostDistance = ReadJsonDouble(row, "maxPostIkDistanceToGoal");
                var maxPreDistance = ReadJsonDouble(row, "maxPreIkDistanceToGoal");
                var hasPostIkBone = (bool?)row["hasPostIkBone"] ?? false;
                var hasIkReadback = (bool?)row["hasIkReadback"] ?? false;
                var readbackDistance = ReadJsonDouble(row, "maxIkReadbackDistanceToGoal");
                var readbackMinPositionWeight = ReadJsonDouble(row, "minIkReadbackPositionWeight");
                var readbackMaxPositionWeight = ReadJsonDouble(row, "maxIkReadbackPositionWeight");
                var readbackMaxRotationWeight = ReadJsonDouble(row, "maxIkReadbackRotationWeight");
                var hasHint = (bool?)row["hasHint"] ?? false;
                var hasIkHintReadback = (bool?)row["hasIkHintReadback"] ?? false;
                var hintReadbackDistance = ReadJsonDouble(row, "maxIkHintReadbackDistanceToHint");
                var hintReadbackWeight = ReadJsonDouble(row, "maxIkHintReadbackWeight");
                var layerWeightSource = (string)row["layerWeightSource"];
                if (!string.IsNullOrWhiteSpace(layerWeightSource))
                {
                    layerWeightSources.Add(layerWeightSource);
                }
                if (layerIndex >= 0)
                {
                    layerIndexes.Add(layerIndex);
                }
                if (!hasPostIkBone)
                {
                    missingPostIkBoneCount++;
                }
                if (!hasIkReadback)
                {
                    missingIkReadbackCount++;
                }
                else
                {
                    maxIkReadbackDistanceToGoal = Math.Max(maxIkReadbackDistanceToGoal, readbackDistance);
                    minIkReadbackPositionWeight = Math.Min(minIkReadbackPositionWeight, readbackMinPositionWeight);
                    maxIkReadbackPositionWeight = Math.Max(maxIkReadbackPositionWeight, readbackMaxPositionWeight);
                    maxIkReadbackRotationWeight = Math.Max(maxIkReadbackRotationWeight, readbackMaxRotationWeight);
                }
                if (hasHint && !hasIkHintReadback)
                {
                    missingIkHintReadbackCount++;
                }
                if (hasIkHintReadback)
                {
                    maxIkHintReadbackDistanceToHint = Math.Max(maxIkHintReadbackDistanceToHint, hintReadbackDistance);
                    maxIkHintReadbackWeight = Math.Max(maxIkHintReadbackWeight, hintReadbackWeight);
                }

                var isHand = goal.IndexOf("Hand", StringComparison.OrdinalIgnoreCase) >= 0;
                var isFoot = goal.IndexOf("Foot", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isHand)
                {
                    handGoalCount++;
                    handSampleCount += sampleCount;
                    handMaxPostDistance = Math.Max(handMaxPostDistance, maxPostDistance);
                }
                if (isFoot)
                {
                    footGoalCount++;
                    footSampleCount += sampleCount;
                    footMaxPostDistance = Math.Max(footMaxPostDistance, maxPostDistance);
                }

                goalRows.Add(new JObject
                {
                    ["goal"] = goal,
                    ["layerIndex"] = layerIndex,
                    ["sampleCount"] = sampleCount,
                    ["hasPosition"] = (bool?)row["hasPosition"] ?? false,
                    ["hasRotation"] = (bool?)row["hasRotation"] ?? false,
                    ["hasPostIkBone"] = hasPostIkBone,
                    ["hasIkReadback"] = hasIkReadback,
                    ["hint"] = (string)row["hint"] ?? string.Empty,
                    ["hintPath"] = (string)row["hintPath"] ?? string.Empty,
                    ["hasHint"] = hasHint,
                    ["hasIkHintReadback"] = hasIkHintReadback,
                    ["maxPreIkDistanceToGoal"] = maxPreDistance,
                    ["maxPostIkDistanceToGoal"] = maxPostDistance,
                    ["maxIkReadbackDistanceToGoal"] = readbackDistance,
                    ["maxIkHintReadbackDistanceToHint"] = hintReadbackDistance,
                    ["minIkReadbackPositionWeight"] = readbackMinPositionWeight,
                    ["maxIkReadbackPositionWeight"] = readbackMaxPositionWeight,
                    ["maxIkReadbackRotationWeight"] = readbackMaxRotationWeight,
                    ["maxIkHintReadbackWeight"] = hintReadbackWeight,
                    ["layerWeightSource"] = layerWeightSource ?? string.Empty,
                });
            }

            var finalGoalRows = new JArray();
            var finalHandMaxPostDistance = 0.0;
            var finalFootMaxPostDistance = 0.0;
            var finalHandSampleCount = 0;
            var finalFootSampleCount = 0;
            var finalHandGoalCount = 0;
            var finalFootGoalCount = 0;
            var finalMissingPostIkBoneCount = 0;
            var maxLayerTargetSpread = 0.0;
            var maxFinalIkBoneMoveDistance = 0.0;
            var maxFinalIkDistanceImprovement = 0.0;
            var maxFinalIkDistanceRegression = 0.0;
            var directTwoBoneIkSolveCount = 0;
            var directTwoBoneIkMissingChainCount = 0;
            var directTwoBoneIkWeightSkippedCount = 0;
            var maxDirectTwoBoneIkPostDistanceToGoal = 0.0;
            var maxDirectTwoBoneIkImprovement = 0.0;
            var maxDirectTwoBoneIkTargetDistanceFromUpper = 0.0;
            var maxDirectTwoBoneIkChainLength = 0.0;
            var maxDirectTwoBoneIkReachShortfall = 0.0;
            var maxDirectTwoBoneIkInsideMinReachAmount = 0.0;
            var minDirectTwoBoneIkAvatarHumanScale = double.MaxValue;
            var maxDirectTwoBoneIkAvatarHumanScale = 0.0;
            var minDirectTwoBoneIkReachFitScale = double.MaxValue;
            var maxDirectTwoBoneIkReachFitScale = 0.0;
            var maxDirectTwoBoneIkReachFitScaleOffsetFromCurrentGoal = 0.0;
            var goalSpaceCandidateWorstDistances = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var goalSpaceCandidateSampleCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var finalSummaries = diagnostic["finalGoalSummaries"] as JArray;
            if (finalSummaries != null)
            {
                foreach (var row in finalSummaries.OfType<JObject>())
                {
                    var goal = (string)row["goal"] ?? string.Empty;
                    var sampleCount = (int?)row["sampleCount"] ?? 0;
                    var maxPreDistance = ReadJsonDouble(row, "maxPreIkDistanceToDominantGoal");
                    var maxPostDistance = ReadJsonDouble(row, "maxPostIkDistanceToDominantGoal");
                    var maxMoveDistance = ReadJsonDouble(row, "maxIkBoneMoveDistance");
                    var maxImprovement = ReadJsonDouble(row, "maxIkDistanceImprovement");
                    var maxRegression = ReadJsonDouble(row, "maxIkDistanceRegression");
                    var targetSpread = ReadJsonDouble(row, "maxLayerTargetSpread");
                    var rowDirectSolveCount = (int?)row["directTwoBoneIkSolveCount"] ?? 0;
                    var rowDirectMissingChainCount = (int?)row["directTwoBoneIkMissingChainCount"] ?? 0;
                    var rowDirectWeightSkippedCount = (int?)row["directTwoBoneIkWeightSkippedCount"] ?? 0;
                    var rowDirectMaxPostDistance = ReadJsonDouble(row, "maxDirectTwoBoneIkPostDistanceToGoal");
                    var rowDirectMaxImprovement = ReadJsonDouble(row, "maxDirectTwoBoneIkImprovement");
                    var rowDirectTargetDistanceFromUpper = ReadJsonDouble(row, "maxDirectTwoBoneIkTargetDistanceFromUpper");
                    var rowDirectChainLength = ReadJsonDouble(row, "maxDirectTwoBoneIkChainLength");
                    var rowDirectReachShortfall = ReadJsonDouble(row, "maxDirectTwoBoneIkReachShortfall");
                    var rowDirectInsideMinReachAmount = ReadJsonDouble(row, "maxDirectTwoBoneIkInsideMinReachAmount");
                    var rowMinAvatarHumanScale = ReadJsonDouble(row, "minDirectTwoBoneIkAvatarHumanScale");
                    var rowMaxAvatarHumanScale = ReadJsonDouble(row, "maxDirectTwoBoneIkAvatarHumanScale");
                    var rowMinReachFitScale = ReadJsonDouble(row, "minDirectTwoBoneIkReachFitScale");
                    var rowMaxReachFitScale = ReadJsonDouble(row, "maxDirectTwoBoneIkReachFitScale");
                    var rowReachFitScaleOffset = ReadJsonDouble(row, "maxDirectTwoBoneIkReachFitScaleOffsetFromCurrentGoal");
                    var hasPreIkBone = (bool?)row["hasPreIkBone"] ?? false;
                    var hasPostIkBone = (bool?)row["hasPostIkBone"] ?? false;
                    if (!hasPostIkBone)
                    {
                        finalMissingPostIkBoneCount++;
                    }
                    maxLayerTargetSpread = Math.Max(maxLayerTargetSpread, targetSpread);
                    maxFinalIkBoneMoveDistance = Math.Max(maxFinalIkBoneMoveDistance, maxMoveDistance);
                    maxFinalIkDistanceImprovement = Math.Max(maxFinalIkDistanceImprovement, maxImprovement);
                    maxFinalIkDistanceRegression = Math.Max(maxFinalIkDistanceRegression, maxRegression);
                    directTwoBoneIkSolveCount += rowDirectSolveCount;
                    directTwoBoneIkMissingChainCount += rowDirectMissingChainCount;
                    directTwoBoneIkWeightSkippedCount += rowDirectWeightSkippedCount;
                    maxDirectTwoBoneIkPostDistanceToGoal = Math.Max(maxDirectTwoBoneIkPostDistanceToGoal, rowDirectMaxPostDistance);
                    maxDirectTwoBoneIkImprovement = Math.Max(maxDirectTwoBoneIkImprovement, rowDirectMaxImprovement);
                    maxDirectTwoBoneIkTargetDistanceFromUpper = Math.Max(maxDirectTwoBoneIkTargetDistanceFromUpper, rowDirectTargetDistanceFromUpper);
                    maxDirectTwoBoneIkChainLength = Math.Max(maxDirectTwoBoneIkChainLength, rowDirectChainLength);
                    maxDirectTwoBoneIkReachShortfall = Math.Max(maxDirectTwoBoneIkReachShortfall, rowDirectReachShortfall);
                    maxDirectTwoBoneIkInsideMinReachAmount = Math.Max(maxDirectTwoBoneIkInsideMinReachAmount, rowDirectInsideMinReachAmount);
                    if (rowMinAvatarHumanScale > 0.0)
                    {
                        minDirectTwoBoneIkAvatarHumanScale = Math.Min(minDirectTwoBoneIkAvatarHumanScale, rowMinAvatarHumanScale);
                    }
                    maxDirectTwoBoneIkAvatarHumanScale = Math.Max(maxDirectTwoBoneIkAvatarHumanScale, rowMaxAvatarHumanScale);
                    if (rowMinReachFitScale > 0.0)
                    {
                        minDirectTwoBoneIkReachFitScale = Math.Min(minDirectTwoBoneIkReachFitScale, rowMinReachFitScale);
                    }
                    maxDirectTwoBoneIkReachFitScale = Math.Max(maxDirectTwoBoneIkReachFitScale, rowMaxReachFitScale);
                    maxDirectTwoBoneIkReachFitScaleOffsetFromCurrentGoal = Math.Max(maxDirectTwoBoneIkReachFitScaleOffsetFromCurrentGoal, rowReachFitScaleOffset);

                    var isHand = goal.IndexOf("Hand", StringComparison.OrdinalIgnoreCase) >= 0;
                    var isFoot = goal.IndexOf("Foot", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (isHand)
                    {
                        finalHandGoalCount++;
                        finalHandSampleCount += sampleCount;
                        finalHandMaxPostDistance = Math.Max(finalHandMaxPostDistance, maxPostDistance);
                    }
                    if (isFoot)
                    {
                        finalFootGoalCount++;
                        finalFootSampleCount += sampleCount;
                        finalFootMaxPostDistance = Math.Max(finalFootMaxPostDistance, maxPostDistance);
                    }

                    var candidateRows = new JArray();
                    var bestCandidateName = string.Empty;
                    var bestCandidateDistance = double.MaxValue;
                    var bestReachableCandidateName = string.Empty;
                    var bestReachableCandidateDistance = double.MaxValue;
                    var bestReachableCandidateShortfall = double.MaxValue;
                    if (row["goalSpaceCandidates"] is JArray candidateSummaries)
                    {
                        foreach (var candidate in candidateSummaries.OfType<JObject>())
                        {
                            var candidateName = (string)candidate["name"] ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(candidateName))
                            {
                                continue;
                            }

                            var candidateSampleCount = (int?)candidate["sampleCount"] ?? 0;
                            var candidateMaxDistance = ReadJsonDouble(candidate, "maxPostIkDistance");
                            var candidateMaxReachShortfall = ReadJsonDouble(candidate, "maxReachShortfall");
                            if (!goalSpaceCandidateWorstDistances.TryGetValue(candidateName, out var currentWorst)
                                || candidateMaxDistance > currentWorst)
                            {
                                goalSpaceCandidateWorstDistances[candidateName] = candidateMaxDistance;
                            }
                            goalSpaceCandidateSampleCounts.TryGetValue(candidateName, out var currentSampleCount);
                            goalSpaceCandidateSampleCounts[candidateName] = currentSampleCount + candidateSampleCount;
                            if (candidateMaxDistance < bestCandidateDistance)
                            {
                                bestCandidateDistance = candidateMaxDistance;
                                bestCandidateName = candidateName;
                            }
                            if (candidateMaxReachShortfall <= 0.0005 && candidateMaxDistance < bestReachableCandidateDistance)
                            {
                                bestReachableCandidateDistance = candidateMaxDistance;
                                bestReachableCandidateShortfall = candidateMaxReachShortfall;
                                bestReachableCandidateName = candidateName;
                            }

                            candidateRows.Add(new JObject
                            {
                                ["name"] = candidateName,
                                ["sampleCount"] = candidateSampleCount,
                                ["maxPostIkDistance"] = candidateMaxDistance,
                                ["minTargetDistanceFromUpper"] = ReadJsonDouble(candidate, "minTargetDistanceFromUpper"),
                                ["maxTargetDistanceFromUpper"] = ReadJsonDouble(candidate, "maxTargetDistanceFromUpper"),
                                ["minReachShortfall"] = ReadJsonDouble(candidate, "minReachShortfall"),
                                ["maxReachShortfall"] = candidateMaxReachShortfall,
                                ["maxInsideMinReachAmount"] = ReadJsonDouble(candidate, "maxInsideMinReachAmount"),
                            });
                        }
                    }
                    if (bestReachableCandidateDistance == double.MaxValue)
                    {
                        bestReachableCandidateDistance = 0.0;
                        bestReachableCandidateShortfall = 0.0;
                    }

                    var boneCandidateRows = new JArray();
                    var bestAnyBoneCandidateName = string.Empty;
                    var bestAnyBoneCandidatePath = string.Empty;
                    var bestAnyBoneCandidateDistance = double.MaxValue;
                    var bestAnyBoneCandidateCoverage = 0.0;
                    var bestStableBoneCandidateName = string.Empty;
                    var bestStableBoneCandidatePath = string.Empty;
                    var bestStableBoneCandidateDistance = double.MaxValue;
                    var bestStableBoneCandidateCoverage = 0.0;
                    const double stableBoneTargetCoverageThreshold = 0.8;
                    if (row["boneTargetCandidates"] is JArray boneCandidateSummaries)
                    {
                        foreach (var candidate in boneCandidateSummaries.OfType<JObject>())
                        {
                            var candidateName = (string)candidate["name"] ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(candidateName))
                            {
                                continue;
                            }

                            var candidatePath = (string)candidate["path"] ?? string.Empty;
                            var candidateSampleCount = (int?)candidate["sampleCount"] ?? 0;
                            var candidateMaxDistance = ReadJsonDouble(candidate, "maxDistanceToDominantGoal");
                            var localOffsetRange = candidate["localOffsetToDominantGoal"] as JObject;
                            var localOffsetSpan = CalculateVectorRangeSpan(localOffsetRange);
                            var candidateCoverage = sampleCount > 0
                                ? Math.Min(1.0, Math.Max(0.0, (double)candidateSampleCount / sampleCount))
                                : 0.0;
                            if (candidateMaxDistance < bestAnyBoneCandidateDistance)
                            {
                                bestAnyBoneCandidateDistance = candidateMaxDistance;
                                bestAnyBoneCandidateName = candidateName;
                                bestAnyBoneCandidatePath = candidatePath;
                                bestAnyBoneCandidateCoverage = candidateCoverage;
                            }
                            if (candidateCoverage >= stableBoneTargetCoverageThreshold
                                && candidateMaxDistance < bestStableBoneCandidateDistance)
                            {
                                bestStableBoneCandidateDistance = candidateMaxDistance;
                                bestStableBoneCandidateName = candidateName;
                                bestStableBoneCandidatePath = candidatePath;
                                bestStableBoneCandidateCoverage = candidateCoverage;
                            }

                            boneCandidateRows.Add(new JObject
                            {
                                ["name"] = candidateName,
                                ["path"] = candidatePath,
                                ["sampleCount"] = candidateSampleCount,
                                ["coverage"] = candidateCoverage,
                                ["localOffsetToDominantGoal"] = localOffsetRange ?? new JObject(),
                                ["localOffsetSpan"] = localOffsetSpan,
                                ["minLocalOffsetLength"] = ReadJsonDouble(candidate, "minLocalOffsetLength"),
                                ["maxLocalOffsetLength"] = ReadJsonDouble(candidate, "maxLocalOffsetLength"),
                                ["maxDistanceToDominantGoal"] = candidateMaxDistance,
                            });
                        }
                    }
                    if (bestStableBoneCandidateDistance == double.MaxValue)
                    {
                        bestStableBoneCandidateDistance = 0.0;
                    }

                    finalGoalRows.Add(new JObject
                    {
                        ["goal"] = goal,
                        ["sampleCount"] = sampleCount,
                        ["hasPreIkBone"] = hasPreIkBone,
                        ["hasPostIkBone"] = hasPostIkBone,
                        ["minDominantLayerIndex"] = (int?)row["minDominantLayerIndex"] ?? -1,
                        ["maxDominantLayerIndex"] = (int?)row["maxDominantLayerIndex"] ?? -1,
                        ["minDominantLayerWeight"] = ReadJsonDouble(row, "minDominantLayerWeight"),
                        ["maxDominantLayerWeight"] = ReadJsonDouble(row, "maxDominantLayerWeight"),
                        ["dominantLayerWeightSource"] = (string)row["dominantLayerWeightSource"] ?? string.Empty,
                        ["maxPreIkDistanceToDominantGoal"] = maxPreDistance,
                        ["maxPostIkDistanceToDominantGoal"] = maxPostDistance,
                        ["maxIkBoneMoveDistance"] = maxMoveDistance,
                        ["maxIkDistanceImprovement"] = maxImprovement,
                        ["maxIkDistanceRegression"] = maxRegression,
                        ["directTwoBoneIkSolveCount"] = rowDirectSolveCount,
                        ["directTwoBoneIkMissingChainCount"] = rowDirectMissingChainCount,
                        ["directTwoBoneIkWeightSkippedCount"] = rowDirectWeightSkippedCount,
                        ["maxDirectTwoBoneIkPostDistanceToGoal"] = rowDirectMaxPostDistance,
                        ["maxDirectTwoBoneIkImprovement"] = rowDirectMaxImprovement,
                        ["minDirectTwoBoneIkUpperToLowerLength"] = ReadJsonDouble(row, "minDirectTwoBoneIkUpperToLowerLength"),
                        ["maxDirectTwoBoneIkUpperToLowerLength"] = ReadJsonDouble(row, "maxDirectTwoBoneIkUpperToLowerLength"),
                        ["minDirectTwoBoneIkLowerToEndLength"] = ReadJsonDouble(row, "minDirectTwoBoneIkLowerToEndLength"),
                        ["maxDirectTwoBoneIkLowerToEndLength"] = ReadJsonDouble(row, "maxDirectTwoBoneIkLowerToEndLength"),
                        ["minDirectTwoBoneIkChainLength"] = ReadJsonDouble(row, "minDirectTwoBoneIkChainLength"),
                        ["maxDirectTwoBoneIkChainLength"] = rowDirectChainLength,
                        ["minDirectTwoBoneIkTargetDistanceFromUpper"] = ReadJsonDouble(row, "minDirectTwoBoneIkTargetDistanceFromUpper"),
                        ["maxDirectTwoBoneIkTargetDistanceFromUpper"] = rowDirectTargetDistanceFromUpper,
                        ["minDirectTwoBoneIkReachShortfall"] = ReadJsonDouble(row, "minDirectTwoBoneIkReachShortfall"),
                        ["maxDirectTwoBoneIkReachShortfall"] = rowDirectReachShortfall,
                        ["maxDirectTwoBoneIkInsideMinReachAmount"] = rowDirectInsideMinReachAmount,
                        ["minDirectTwoBoneIkAvatarHumanScale"] = rowMinAvatarHumanScale,
                        ["maxDirectTwoBoneIkAvatarHumanScale"] = rowMaxAvatarHumanScale,
                        ["minDirectTwoBoneIkReachFitScale"] = rowMinReachFitScale,
                        ["maxDirectTwoBoneIkReachFitScale"] = rowMaxReachFitScale,
                        ["minDirectTwoBoneIkReachFitScaleTargetDistanceFromUpper"] = ReadJsonDouble(row, "minDirectTwoBoneIkReachFitScaleTargetDistanceFromUpper"),
                        ["maxDirectTwoBoneIkReachFitScaleTargetDistanceFromUpper"] = ReadJsonDouble(row, "maxDirectTwoBoneIkReachFitScaleTargetDistanceFromUpper"),
                        ["maxDirectTwoBoneIkReachFitScaleOffsetFromCurrentGoal"] = rowReachFitScaleOffset,
                        ["maxLayerTargetSpread"] = targetSpread,
                        ["closestDescendantSampleCount"] = (int?)row["closestDescendantSampleCount"] ?? 0,
                        ["closestDescendantLastPath"] = (string)row["closestDescendantLastPath"] ?? string.Empty,
                        ["minClosestDescendantDistanceToDominantGoal"] = ReadJsonDouble(row, "minClosestDescendantDistanceToDominantGoal"),
                        ["maxClosestDescendantDistanceToDominantGoal"] = ReadJsonDouble(row, "maxClosestDescendantDistanceToDominantGoal"),
                        ["bestGoalSpaceCandidate"] = bestCandidateName,
                        ["bestGoalSpaceCandidateMaxPostIkDistance"] = bestCandidateDistance == double.MaxValue ? 0.0 : bestCandidateDistance,
                        ["bestReachableGoalSpaceCandidate"] = bestReachableCandidateName,
                        ["bestReachableGoalSpaceCandidateMaxPostIkDistance"] = bestReachableCandidateDistance,
                        ["bestReachableGoalSpaceCandidateMaxReachShortfall"] = bestReachableCandidateShortfall,
                        ["goalSpaceCandidates"] = candidateRows,
                        ["bestBoneTargetCandidateRule"] = "bestStable requires candidate coverage >= 80% of samples; bestAny is diagnostic only and may represent a transient closest descendant.",
                        ["bestAnyBoneTargetCandidate"] = bestAnyBoneCandidateName,
                        ["bestAnyBoneTargetCandidatePath"] = bestAnyBoneCandidatePath,
                        ["bestAnyBoneTargetCandidateCoverage"] = bestAnyBoneCandidateCoverage,
                        ["bestAnyBoneTargetCandidateMaxDistanceToDominantGoal"] = bestAnyBoneCandidateDistance == double.MaxValue ? 0.0 : bestAnyBoneCandidateDistance,
                        ["bestStableBoneTargetCandidate"] = bestStableBoneCandidateName,
                        ["bestStableBoneTargetCandidatePath"] = bestStableBoneCandidatePath,
                        ["bestStableBoneTargetCandidateCoverage"] = bestStableBoneCandidateCoverage,
                        ["bestStableBoneTargetCandidateMaxDistanceToDominantGoal"] = bestStableBoneCandidateDistance,
                        // 兼容旧报告消费者：旧字段现在指向稳定候选，避免少数帧 closest descendant 被误读为整段动画的目标点。
                        ["bestBoneTargetCandidate"] = bestStableBoneCandidateName,
                        ["bestBoneTargetCandidatePath"] = bestStableBoneCandidatePath,
                        ["bestBoneTargetCandidateMaxDistanceToDominantGoal"] = bestStableBoneCandidateDistance,
                        ["boneTargetCandidates"] = boneCandidateRows,
                    });
                }
            }

            var hasFinalGoalEvidence = finalGoalRows.Count > 0;
            var hasHandEvidence = hasFinalGoalEvidence
                ? finalHandGoalCount > 0 && finalHandSampleCount > 0
                : handGoalCount > 0 && handSampleCount > 0;
            var hasFootEvidence = hasFinalGoalEvidence
                ? finalFootGoalCount > 0 && finalFootSampleCount > 0
                : footGoalCount > 0 && footSampleCount > 0;
            var statusHandMaxPostDistance = hasFinalGoalEvidence ? finalHandMaxPostDistance : handMaxPostDistance;
            var statusFootMaxPostDistance = hasFinalGoalEvidence ? finalFootMaxPostDistance : footMaxPostDistance;
            var statusMissingPostIkBoneCount = hasFinalGoalEvidence ? finalMissingPostIkBoneCount : missingPostIkBoneCount;
            result["status"] =
                statusMissingPostIkBoneCount > 0 ? "incomplete_goal_bone_evidence" :
                hasHandEvidence && statusHandMaxPostDistance <= 0.03 && hasFootEvidence && statusFootMaxPostDistance <= 0.15 ? "hand_and_foot_goal_alignment_evidence" :
                hasHandEvidence && statusHandMaxPostDistance <= 0.03 ? "hand_goal_alignment_evidence_only" :
                "goal_alignment_needs_review";
            result["statusBasis"] = hasFinalGoalEvidence
                ? "final_dominant_goal"
                : "per_layer_goal";
            result["handGoalCount"] = handGoalCount;
            result["footGoalCount"] = footGoalCount;
            result["handSampleCount"] = handSampleCount;
            result["footSampleCount"] = footSampleCount;
            result["maxHandPostIkDistanceToGoal"] = handMaxPostDistance;
            result["maxFootPostIkDistanceToGoal"] = footMaxPostDistance;
            result["missingIkReadbackCount"] = missingIkReadbackCount;
            result["maxIkReadbackDistanceToGoal"] = maxIkReadbackDistanceToGoal;
            result["minIkReadbackPositionWeight"] = minIkReadbackPositionWeight == double.MaxValue ? 0.0 : minIkReadbackPositionWeight;
            result["maxIkReadbackPositionWeight"] = maxIkReadbackPositionWeight;
            result["maxIkReadbackRotationWeight"] = maxIkReadbackRotationWeight;
            result["missingIkHintReadbackCount"] = missingIkHintReadbackCount;
            result["maxIkHintReadbackDistanceToHint"] = maxIkHintReadbackDistanceToHint;
            result["maxIkHintReadbackWeight"] = maxIkHintReadbackWeight;
            result["finalGoalEvidenceAvailable"] = hasFinalGoalEvidence;
            result["finalHandGoalCount"] = finalHandGoalCount;
            result["finalFootGoalCount"] = finalFootGoalCount;
            result["finalHandSampleCount"] = finalHandSampleCount;
            result["finalFootSampleCount"] = finalFootSampleCount;
            result["maxHandFinalPostIkDistanceToDominantGoal"] = finalHandMaxPostDistance;
            result["maxFootFinalPostIkDistanceToDominantGoal"] = finalFootMaxPostDistance;
            result["maxLayerTargetSpread"] = maxLayerTargetSpread;
            result["maxFinalIkBoneMoveDistance"] = maxFinalIkBoneMoveDistance;
            result["maxFinalIkDistanceImprovement"] = maxFinalIkDistanceImprovement;
            result["maxFinalIkDistanceRegression"] = maxFinalIkDistanceRegression;
            result["directTwoBoneIkDiagnosticEnabled"] = (bool?)diagnostic["directTwoBoneIkDiagnosticEnabled"] ?? false;
            result["directTwoBoneIkAttemptCount"] = (int?)diagnostic["directTwoBoneIkAttemptCount"] ?? 0;
            result["directTwoBoneIkSolveCount"] = directTwoBoneIkSolveCount;
            result["directTwoBoneIkMissingChainCount"] = directTwoBoneIkMissingChainCount;
            result["directTwoBoneIkWeightSkippedCount"] = directTwoBoneIkWeightSkippedCount;
            result["maxDirectTwoBoneIkPostDistanceToGoal"] = maxDirectTwoBoneIkPostDistanceToGoal;
            result["maxDirectTwoBoneIkImprovement"] = maxDirectTwoBoneIkImprovement;
            result["maxDirectTwoBoneIkTargetDistanceFromUpper"] = maxDirectTwoBoneIkTargetDistanceFromUpper;
            result["maxDirectTwoBoneIkChainLength"] = maxDirectTwoBoneIkChainLength;
            result["maxDirectTwoBoneIkReachShortfall"] = maxDirectTwoBoneIkReachShortfall;
            result["maxDirectTwoBoneIkInsideMinReachAmount"] = maxDirectTwoBoneIkInsideMinReachAmount;
            result["minDirectTwoBoneIkAvatarHumanScale"] = minDirectTwoBoneIkAvatarHumanScale == double.MaxValue ? 0.0 : minDirectTwoBoneIkAvatarHumanScale;
            result["maxDirectTwoBoneIkAvatarHumanScale"] = maxDirectTwoBoneIkAvatarHumanScale;
            result["minDirectTwoBoneIkReachFitScale"] = minDirectTwoBoneIkReachFitScale == double.MaxValue ? 0.0 : minDirectTwoBoneIkReachFitScale;
            result["maxDirectTwoBoneIkReachFitScale"] = maxDirectTwoBoneIkReachFitScale;
            result["maxDirectTwoBoneIkReachFitScaleOffsetFromCurrentGoal"] = maxDirectTwoBoneIkReachFitScaleOffsetFromCurrentGoal;
            result["missingPostIkBoneCount"] = missingPostIkBoneCount;
            result["finalMissingPostIkBoneCount"] = finalMissingPostIkBoneCount;
            result["layerIndexes"] = new JArray(layerIndexes.OrderBy(x => x));
            result["layerWeightSources"] = new JArray(layerWeightSources.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            result["goals"] = goalRows;
            result["finalGoals"] = finalGoalRows;
            result["goalSpaceCandidateSummary"] = new JArray(
                goalSpaceCandidateWorstDistances
                    .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new JObject
                    {
                        ["name"] = x.Key,
                        ["sampleCount"] = goalSpaceCandidateSampleCounts.TryGetValue(x.Key, out var count) ? count : 0,
                        ["maxPostIkDistanceAcrossGoals"] = x.Value,
                    }));
            result["thresholds"] = new JObject
            {
                ["handMaxPostIkDistanceStrongEvidence"] = 0.03,
                ["footMaxPostIkDistanceLooseEvidence"] = 0.15,
                ["stableBoneTargetCandidateCoverage"] = 0.8,
                ["note"] = "These thresholds only classify diagnostic evidence. They do not approve production reuse without visual validation.",
            };
            return result;
        }

        private static double ReadJsonDouble(JObject row, string name)
        {
            var token = row?[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return 0.0;
            }

            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
            {
                return token.Value<double>();
            }

            return double.TryParse((string)token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : 0.0;
        }

        private static double CalculateVectorRangeSpan(JObject range)
        {
            if (range == null)
            {
                return 0.0;
            }

            var dx = ReadJsonDouble(range, "maxX") - ReadJsonDouble(range, "minX");
            var dy = ReadJsonDouble(range, "maxY") - ReadJsonDouble(range, "minY");
            var dz = ReadJsonDouble(range, "maxZ") - ReadJsonDouble(range, "minZ");
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static bool IsGeneratedAnimatorControllerSamplingMode(string samplingMode)
        {
            return string.Equals(samplingMode, "generated_single_state_controller", StringComparison.OrdinalIgnoreCase)
                || string.Equals(samplingMode, "generated_multi_layer_controller", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAnimatorControllerMissingSamplingMode(string samplingMode)
        {
            return string.Equals(samplingMode, "controller_asset_missing", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsUnityDerivedPlayableGraphCandidate(
            bool requiresHumanoidBake,
            bool unityClipIsHumanMotion,
            AvatarTrustReport avatarTrust,
            int writtenTracks,
            int frameVaryingTracks,
            int coreBodyFrameVaryingTracks,
            int invalidSkinTargetCount,
            bool diagnosticRebuildEditorCurveClip,
            bool diagnosticIgnoreImportedAvatar,
            bool isTransformOnlyClipFilter)
        {
            return requiresHumanoidBake
                && unityClipIsHumanMotion
                && avatarTrust.TrustedProductionBake
                && writtenTracks > 0
                && frameVaryingTracks > 0
                && coreBodyFrameVaryingTracks > 0
                && invalidSkinTargetCount == 0
                && !diagnosticRebuildEditorCurveClip
                && !diagnosticIgnoreImportedAvatar
                && !isTransformOnlyClipFilter;
        }

        private static bool IsDynamicEditorCurveTrack(JObject track)
        {
            return MaxEditorCurveDelta(track["values"] as JArray) > 0.00001f;
        }

        private static float MaxEditorCurveDelta(JArray values)
        {
            var numbers = values?
                .Select(ReadCurveSampleValue)
                .Where(x => x.HasValue)
                .Select(x => x.Value)
                .ToArray() ?? Array.Empty<float>();
            if (numbers.Length < 2)
            {
                return 0f;
            }

            return numbers.Max() - numbers.Min();
        }

        private static float? ReadCurveSampleValue(JToken token)
        {
            if (token == null)
            {
                return null;
            }
            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
            {
                return (float)token;
            }
            if (token is JObject obj && obj["value"] != null)
            {
                return (float)obj["value"];
            }
            return null;
        }

        private static JObject BuildEditorCurveCategorySummary(JObject[] editorCurves)
        {
            var categories = new Dictionary<string, CurveCategoryCounter>(StringComparer.OrdinalIgnoreCase);
            foreach (var curve in editorCurves ?? Array.Empty<JObject>())
            {
                var category = ClassifyEditorCurve(curve);
                if (!categories.TryGetValue(category, out var counter))
                {
                    counter = new CurveCategoryCounter(category);
                    categories[category] = counter;
                }

                var propertyName = (string)curve["propertyName"] ?? string.Empty;
                var path = (string)curve["path"] ?? string.Empty;
                var delta = Math.Abs(MaxEditorCurveDelta(curve["values"] as JArray));
                counter.Add(propertyName, path, delta);
            }

            return new JObject
            {
                ["rule"] = "diagnostic_only: categories are derived from Unity editor curve property/path/type names to explain why runtime HumanPose sampling differs from editor-visible curves; they do not create model-animation bindings.",
                ["categories"] = new JObject(categories
                    .OrderByDescending(x => x.Value.DynamicCount)
                    .ThenByDescending(x => x.Value.Count)
                    .Select(x => new JProperty(x.Key, x.Value.ToJson())))
            };
        }

        private static string ClassifyEditorCurve(JObject curve)
        {
            var propertyName = ((string)curve?["propertyName"] ?? string.Empty).Trim();
            var path = ((string)curve?["path"] ?? string.Empty).Trim();
            var type = ((string)curve?["type"] ?? string.Empty).Trim();
            if (IsAnyPrefix(propertyName, "RootT.", "RootQ.", "MotionT.", "MotionQ."))
            {
                return "humanoidRootBody";
            }
            if (IsAnyPrefix(
                    propertyName,
                    "LeftFootT.", "LeftFootQ.",
                    "RightFootT.", "RightFootQ.",
                    "LeftHandT.", "LeftHandQ.",
                    "RightHandT.", "RightHandQ."))
            {
                return "humanoidLimbGoal";
            }
            if (propertyName.StartsWith("TDOF.", StringComparison.OrdinalIgnoreCase)
                || propertyName.IndexOf("TDOF", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "humanoidTdof";
            }
            if (propertyName.StartsWith("m_LocalPosition.", StringComparison.OrdinalIgnoreCase)
                || propertyName.StartsWith("m_LocalRotation.", StringComparison.OrdinalIgnoreCase)
                || propertyName.StartsWith("m_LocalScale.", StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(path)
                    ? "transformCurveNoPath"
                    : "transformCurve";
            }
            if (propertyName.StartsWith("blendShape.", StringComparison.OrdinalIgnoreCase))
            {
                return "blendShape";
            }
            if (string.Equals(type, "UnityEngine.Animator", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(path))
            {
                return "otherAnimatorCurve";
            }
            if (propertyName.StartsWith("typetree_", StringComparison.OrdinalIgnoreCase))
            {
                return "typetreeCurve";
            }
            return string.IsNullOrWhiteSpace(type) ? "unknown" : "other_" + SafeJsonKey(type);
        }

        private static bool IsAnyPrefix(string value, params string[] prefixes)
        {
            return prefixes != null && prefixes.Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        private static string SafeJsonKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "unknown";
            }

            return Regex.Replace(value, "[^A-Za-z0-9_]+", "_").Trim('_');
        }

        private static (int MuscleCount, int VaryingMuscleCount, float MaxMuscleDelta) CountVaryingHumanPoseMuscles(JObject[] poseSamples)
        {
            if (poseSamples.Length < 2)
            {
                return (0, 0, 0f);
            }

            var first = ReadHumanPoseMuscles(poseSamples[0]).ToArray();
            if (first.Length == 0)
            {
                return (0, 0, 0f);
            }

            var varying = 0;
            var maxDelta = 0f;
            for (var muscleIndex = 0; muscleIndex < first.Length; muscleIndex++)
            {
                var muscleMaxDelta = 0f;
                foreach (var sample in poseSamples.Skip(1))
                {
                    var values = ReadHumanPoseMuscles(sample).ToArray();
                    if (muscleIndex >= values.Length)
                    {
                        continue;
                    }
                    muscleMaxDelta = Math.Max(muscleMaxDelta, Math.Abs(values[muscleIndex] - first[muscleIndex]));
                }
                if (muscleMaxDelta > 0.00001f)
                {
                    varying++;
                }
                maxDelta = Math.Max(maxDelta, muscleMaxDelta);
            }

            return (first.Length, varying, maxDelta);
        }

        private static IEnumerable<float> ReadHumanPoseMuscles(JObject sample)
        {
            foreach (var item in sample["muscles"]?.Children() ?? Enumerable.Empty<JToken>())
            {
                if (item.Type == JTokenType.Float || item.Type == JTokenType.Integer)
                {
                    yield return (float)item;
                }
                else if (item is JObject obj && obj["value"] != null)
                {
                    yield return (float)obj["value"];
                }
            }
        }

        private static float MaxHumanPoseVectorDelta(JObject[] poseSamples, string propertyName, params string[] keys)
        {
            if (poseSamples.Length < 2)
            {
                return 0f;
            }

            var first = ReadHumanPoseVector(poseSamples[0][propertyName] as JObject, keys);
            var maxDelta = 0f;
            foreach (var sample in poseSamples.Skip(1))
            {
                var current = ReadHumanPoseVector(sample[propertyName] as JObject, keys);
                var sum = 0f;
                for (var i = 0; i < Math.Min(first.Length, current.Length); i++)
                {
                    var delta = current[i] - first[i];
                    sum += delta * delta;
                }
                maxDelta = Math.Max(maxDelta, (float)Math.Sqrt(sum));
            }

            return maxDelta;
        }

        private static float MaxHumanPoseRotationDelta(JObject[] poseSamples)
        {
            if (poseSamples.Length < 2)
            {
                return 0f;
            }

            var first = NormalizeQuaternion(ReadHumanPoseVector(poseSamples[0]["bodyRotation"] as JObject, "x", "y", "z", "w"));
            var maxDelta = 0f;
            foreach (var sample in poseSamples.Skip(1))
            {
                var current = NormalizeQuaternion(ReadHumanPoseVector(sample["bodyRotation"] as JObject, "x", "y", "z", "w"));
                var dot = Math.Abs(
                    first[0] * current[0] +
                    first[1] * current[1] +
                    first[2] * current[2] +
                    first[3] * current[3]);
                maxDelta = Math.Max(maxDelta, 1f - Math.Min(1f, dot));
            }
            return maxDelta;
        }

        private static float[] ReadHumanPoseVector(JObject obj, params string[] keys)
        {
            return keys.Select(key => (float?)obj?[key] ?? 0f).ToArray();
        }

        private static bool HasCompleteAvatarOracle(JObject avatar)
        {
            var oracle = avatar?["oracle"] as JObject;
            if (oracle == null)
            {
                return false;
            }

            var humanBoneIndex = oracle["humanBoneIndex"] as JArray;
            var humanSkeleton = oracle["humanSkeleton"] as JObject;
            var avatarSkeleton = oracle["avatarSkeleton"] as JObject;
            var humanNodes = humanSkeleton?["nodes"] as JArray;
            var humanPose = humanSkeleton?["pose"] as JArray;
            var avatarNodes = avatarSkeleton?["nodes"] as JArray;
            var avatarDefaultPose = avatarSkeleton?["defaultPose"] as JArray;
            return humanBoneIndex != null && humanBoneIndex.Values<int?>().Any(x => x.GetValueOrDefault(-1) >= 0)
                && humanNodes != null && humanNodes.Count > 0
                && humanPose != null && humanPose.Count >= humanNodes.Count
                && avatarNodes != null && avatarNodes.Count > 0
                && avatarDefaultPose != null && avatarDefaultPose.Count >= avatarNodes.Count;
        }

        private static bool IsImportedAvatarAssetResultValid(JObject result)
        {
            if (result == null || !((bool?)result["avatarValid"] ?? false))
            {
                return false;
            }

            if ((bool?)result["importedAvatarAssetValid"] ?? false)
            {
                return true;
            }

            // 兼容已经由新 helper 之前版本生成、但明确记录了导入 Avatar rest pose 的结果。
            return IsImportedAvatarRestPoseSource((string)result["rigRestPoseSource"])
                && ((bool?)result["rigRestPoseApplied"] ?? false);
        }

        private static bool IsImportedAvatarRestPoseSource(string source)
        {
            return !string.IsNullOrWhiteSpace(source)
                && source.StartsWith("imported_unity_avatar_asset", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsImportedAvatarBindingOk(JObject result, out string message)
        {
            message = null;
            var binding = result?["importedAvatarBinding"] as JObject;
            if (binding == null)
            {
                return true;
            }

            var status = (string)binding["status"];
            if (string.IsNullOrWhiteSpace(status)
                || string.Equals(status, "not_requested", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "unknown", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            message = "Imported Unity Avatar asset did not bind cleanly to the current bake target hierarchy: "
                + ((string)binding["message"] ?? status)
                + ". This bake must be treated as diagnostic until the Avatar/root path mapping is fixed.";
            return false;
        }

        private static bool NeedsAnimatorControllerContextSampling(JObject request, JObject result)
        {
            var context = request?["animeStudioAssets"]?["animation"]?["animatorControllerContext"] as JObject;
            if (context == null || context.Count == 0)
            {
                return false;
            }

            var controllerAsset = (string)request?["unityAssetPaths"]?["animatorController"];
            var samplingMode = (string)result?["animatorControllerSamplingMode"];
            if (!string.IsNullOrWhiteSpace(controllerAsset)
                && !IsAnimatorControllerMissingSamplingMode(samplingMode))
            {
                return false;
            }

            if (string.Equals(samplingMode, "provided_runtime_controller", StringComparison.OrdinalIgnoreCase)
                || string.Equals(samplingMode, "generated_single_state_controller", StringComparison.OrdinalIgnoreCase)
                || string.Equals(samplingMode, "generated_multi_layer_controller", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var source = (string)context["source"];
            var stateFullPath = (string)context["stateFullPath"] ?? (string)context["statePath"];
            return !string.IsNullOrWhiteSpace(source) || !string.IsNullOrWhiteSpace(stateFullPath);
        }

        private static bool SelectedControllerLayerClipNeedsBaseContext(JObject request)
        {
            var context = request?["animeStudioAssets"]?["animation"]?["animatorControllerContext"] as JObject;
            if (context == null || context.Count == 0)
            {
                return false;
            }

            // 选中非 base layer 的 clip 时，必须有确定的 baseLayerClip 证据；
            // 证据可以来自同状态 body clip，或同 controller 的 base layer 默认状态。
            // 否则它可能只是 slot、Sequence、蒙太奇或 additive 片段，不能当完整身体动作。
            if (context["baseLayerClip"] is JObject baseLayerClip
                && baseLayerClip["clip"] is JObject)
            {
                return false;
            }

            foreach (var layer in context["layers"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var layerIndex = (int?)layer["layerIndex"] ?? 0;
                if (layerIndex > 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string ResolveClipFilterMode(JObject request, JObject result)
        {
            var mode = (string)request?["clipFilterMode"] ?? (string)request?["validation"]?["clipFilterMode"] ?? (string)result?["clipFilterMode"];
            if (string.IsNullOrWhiteSpace(mode))
            {
                return "original";
            }
            mode = mode.Trim().ToLowerInvariant();
            return mode == "humanoid_only" || mode == "humanoid_muscles_only" || mode == "transform_only"
                ? mode
                : "original";
        }

        private static string ResolveClipFilterMode(string requestPath, JObject result)
        {
            if (!string.IsNullOrWhiteSpace(requestPath) && File.Exists(requestPath))
            {
                try
                {
                    return ResolveClipFilterMode(JObject.Parse(File.ReadAllText(requestPath)), result);
                }
                catch
                {
                    // 失败报告只做附加说明，不能因为读取诊断字段失败遮住原始错误。
                }
            }
            return ResolveClipFilterMode((JObject)null, result);
        }

        private static bool IsRequest(JObject value) => value["animeStudioAssets"] != null && value["outputJson"] != null;

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

        private static string ResolveOutputPath(string requestPath, string outputGltfPath, string sourceGltf, string modelName, string clipName)
        {
            if (!string.IsNullOrWhiteSpace(outputGltfPath))
            {
                return outputGltfPath;
            }
            var root = Path.Combine(Path.GetDirectoryName(requestPath) ?? Directory.GetCurrentDirectory(), "BakedPreview");
            return Path.Combine(root, $"{SafeName(modelName)}__{SafeName(clipName)}.gltf");
        }

        private static void CopyModelFolder(string sourceGltf, string outputGltf)
        {
            var sourceDir = Path.GetDirectoryName(sourceGltf);
            var outputDir = Path.GetDirectoryName(outputGltf);
            if (string.IsNullOrWhiteSpace(sourceDir) || string.IsNullOrWhiteSpace(outputDir))
            {
                throw new InvalidOperationException("Invalid glTF folder.");
            }
            Directory.CreateDirectory(outputDir);
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, file);
                var target = Path.Combine(outputDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, overwrite: true);
            }
            var copiedGltf = Path.Combine(outputDir, Path.GetFileName(sourceGltf));
            if (!string.Equals(copiedGltf, outputGltf, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(copiedGltf, outputGltf, overwrite: true);
            }
        }

        private static string ResolveMainBufferFile(JObject gltf, string outputDir)
        {
            var buffer = gltf["buffers"]?.OfType<JObject>().FirstOrDefault();
            var uri = (string)buffer?["uri"];
            if (string.IsNullOrWhiteSpace(uri) || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return Path.Combine(outputDir, uri.Replace('/', Path.DirectorySeparatorChar));
        }

        private static Dictionary<string, int> BuildNodePathIndex(JObject[] nodes)
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

            var paths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string Build(int index)
            {
                var name = (string)nodes[index]["name"] ?? $"node_{index}";
                return parents[index].HasValue ? $"{Build(parents[index].Value)}/{name}" : name;
            }
            for (var i = 0; i < nodes.Length; i++)
            {
                paths[Build(i)] = i;
            }
            return paths;
        }

        private static SkinAnimationReport BuildSkinAnimationReport(JObject gltf)
        {
            var nodes = gltf["nodes"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var skins = gltf["skins"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var animations = gltf["animations"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var meshSkinNodes = nodes
                .Select((node, index) => new { node, index })
                .Where(x => x.node["mesh"] != null && x.node["skin"] != null)
                .Select(x => new MeshSkinNode(x.index, (string)x.node["name"], (int?)x.node["mesh"] ?? -1, (int?)x.node["skin"] ?? -1))
                .ToArray();
            var joints = skins
                .SelectMany(skin => skin["joints"]?.Values<int>() ?? Enumerable.Empty<int>())
                .Where(x => x >= 0 && x < nodes.Length)
                .Distinct()
                .ToHashSet();
            var targets = animations
                .SelectMany(animation => animation["channels"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                .Select(channel => (int?)channel["target"]?["node"])
                .Where(x => x.HasValue && x.Value >= 0 && x.Value < nodes.Length)
                .Select(x => x.Value)
                .Distinct()
                .ToHashSet();
            var childToParent = BuildChildToParent(nodes);
            var hierarchyParentTargets = targets
                .Where(x => !joints.Contains(x) && HasDescendantJoint(nodes, joints, x))
                .Select(x => new NodeRef(x, BuildNodePath(nodes, x, childToParent)))
                .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var nonJointLeafTargets = targets
                .Where(x => !joints.Contains(x) && !HasDescendantJoint(nodes, joints, x))
                .Select(x => new NodeRef(x, BuildNodePath(nodes, x)))
                .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var auxiliaryNonSkinTargets = nonJointLeafTargets
                .Where(x => IsAuxiliaryNonSkinAnimationTarget(nodes, x.Node, x.Path))
                .ToArray();
            var invalidTargets = nonJointLeafTargets
                .Where(x => !IsAuxiliaryNonSkinAnimationTarget(nodes, x.Node, x.Path))
                .ToArray();
            var jointNotTarget = joints
                .Where(x => !targets.Contains(x))
                .Select(x => new NodeRef(x, BuildNodePath(nodes, x, childToParent)))
                .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .Take(128)
                .ToArray();

            return new SkinAnimationReport(
                Nodes: nodes.Length,
                Skins: skins.Length,
                SkinJoints: joints.Count,
                AnimationTargets: targets.Count,
                TargetNotJointCount: invalidTargets.Length + hierarchyParentTargets.Length + auxiliaryNonSkinTargets.Length,
                HierarchyParentTargetCount: hierarchyParentTargets.Length,
                AuxiliaryNonSkinTargetCount: auxiliaryNonSkinTargets.Length,
                InvalidTargetCount: invalidTargets.Length,
                JointNotTargetCount: joints.Count - targets.Count(x => joints.Contains(x)),
                MeshSkinNodes: meshSkinNodes,
                HierarchyParentTargets: hierarchyParentTargets,
                AuxiliaryNonSkinTargets: auxiliaryNonSkinTargets,
                InvalidTargets: invalidTargets,
                JointNotTargetSample: jointNotTarget,
                Rule: "UnityGLTF-style check: skin.joints come from SkinnedMeshRenderer.bones, and baked animation channels should target that node graph. Non-joint parent targets are valid when they have skinned joint descendants, because glTF node hierarchy propagates their transforms into the skin. Meshless IK/look-at/socket helper targets are reported as auxiliary non-skin targets; they still require visual review, but they do not by themselves prove a skin mapping failure."
            );
        }

        private static bool IsAuxiliaryNonSkinAnimationTarget(JObject[] nodes, int nodeIndex, string path)
        {
            if (nodeIndex < 0 || nodeIndex >= nodes.Length)
            {
                return false;
            }
            if (nodes[nodeIndex]["mesh"] != null || HasDescendantMesh(nodes, nodeIndex))
            {
                return false;
            }

            var name = ((string)nodes[nodeIndex]["name"] ?? string.Empty).Trim();
            var lowerName = name.ToLowerInvariant();
            var lowerPath = (path ?? string.Empty).ToLowerInvariant();
            return lowerName.StartsWith("ik_", StringComparison.OrdinalIgnoreCase)
                || lowerPath.Contains("/ik_", StringComparison.OrdinalIgnoreCase)
                || lowerName.Contains("lookat", StringComparison.OrdinalIgnoreCase)
                || lowerName.Contains("look_at", StringComparison.OrdinalIgnoreCase)
                || lowerName.Contains("footstep", StringComparison.OrdinalIgnoreCase)
                || lowerName.StartsWith("wep_", StringComparison.OrdinalIgnoreCase)
                || lowerName.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase)
                || lowerName.Contains("socket", StringComparison.OrdinalIgnoreCase)
                || lowerName.EndsWith("_socket", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasDescendantMesh(JObject[] nodes, int index)
        {
            foreach (var child in nodes[index]["children"]?.Values<int>() ?? Enumerable.Empty<int>())
            {
                if (child < 0 || child >= nodes.Length)
                {
                    continue;
                }
                if (nodes[child]["mesh"] != null || HasDescendantMesh(nodes, child))
                {
                    return true;
                }
            }
            return false;
        }

        private static string BuildNodePath(JObject[] nodes, int index)
        {
            return BuildNodePath(nodes, index, BuildChildToParent(nodes));
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

        private static bool HasDescendantJoint(JObject[] nodes, HashSet<int> joints, int nodeIndex)
        {
            foreach (var child in nodes[nodeIndex]["children"]?.Values<int>() ?? Enumerable.Empty<int>())
            {
                if (child < 0 || child >= nodes.Length)
                {
                    continue;
                }
                if (joints.Contains(child) || HasDescendantJoint(nodes, joints, child))
                {
                    return true;
                }
            }
            return false;
        }

        private static string NormalizeBakePath(string bakePath, JObject[] nodes)
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
                    return FindSingleRootNodeName(nodes) ?? bakedRoot[..^"_AnimeStudioBake".Length];
                }
            }
            return string.Join("/", parts);
        }

        private static string FindSingleRootNodeName(JObject[] nodes)
        {
            var hasParent = new bool[nodes.Length];
            for (var i = 0; i < nodes.Length; i++)
            {
                foreach (var child in nodes[i]["children"]?.Values<int>() ?? Enumerable.Empty<int>())
                {
                    if (child >= 0 && child < hasParent.Length)
                    {
                        hasParent[child] = true;
                    }
                }
            }

            var roots = nodes
                .Select((node, index) => new { node, index })
                .Where(x => !hasParent[x.index])
                .ToArray();
            return roots.Length == 1
                ? (string)roots[0].node["name"]
                : null;
        }

        private static bool IsCoreBodyBonePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var leaf = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? path;
            var bodyTokens = new[]
            {
                "Pelvis", "Spine", "Neck", "Head", "Clavicle",
                "UpperArm", "Forearm", "Hand",
                "Thigh", "Calf", "Foot", "Toe"
            };
            return bodyTokens.Any(token => leaf.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsHumanoidSkeletonRootContainer(int nodeIndex, JObject[] nodes)
        {
            if (nodeIndex < 0 || nodeIndex >= nodes.Length)
            {
                return false;
            }

            var nodeName = (string)nodes[nodeIndex]["name"] ?? string.Empty;
            if (IsCoreBodyBonePath(nodeName))
            {
                return false;
            }

            foreach (var childIndex in nodes[nodeIndex]["children"]?.Values<int>() ?? Enumerable.Empty<int>())
            {
                if (childIndex < 0 || childIndex >= nodes.Length)
                {
                    continue;
                }

                var childName = (string)nodes[childIndex]["name"] ?? string.Empty;
                if (childName.IndexOf("Pelvis", StringComparison.OrdinalIgnoreCase) >= 0
                    || childName.IndexOf("Hips", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static float[][] ReadVec3(JToken values, Func<float[], float[]> convert = null) =>
            values?.OfType<JObject>()
                .Select(x =>
                {
                    var value = new[] { (float)x["x"], (float)x["y"], (float)x["z"] };
                    return convert == null ? value : convert(value);
                })
                .ToArray() ?? Array.Empty<float[]>();

        private static float[][] ReadVec4(JToken values, Func<float[], float[]> convert = null) =>
            values?.OfType<JObject>()
                .Select(x =>
                {
                    var value = new[] { (float)x["x"], (float)x["y"], (float)x["z"], (float)x["w"] };
                    return convert == null ? value : convert(value);
                })
                .ToArray() ?? Array.Empty<float[]>();

        private static float[][] ApplyRestRelativeTranslations(float[][] unityValues, float[] unityRest, float[] gltfRest, bool useFirstSampleAsBase)
        {
            var result = new float[unityValues.Length][];
            var baseValue = useFirstSampleAsBase && unityValues.Length > 0
                ? unityValues[0]
                : unityRest;
            for (var i = 0; i < unityValues.Length; i++)
            {
                var unityDelta = new[]
                {
                    unityValues[i][0] - baseValue[0],
                    unityValues[i][1] - baseValue[1],
                    unityValues[i][2] - baseValue[2],
                };
                var gltfDelta = UnityToGltfPosition(unityDelta);
                result[i] = new[]
                {
                    gltfRest[0] + gltfDelta[0],
                    gltfRest[1] + gltfDelta[1],
                    gltfRest[2] + gltfDelta[2],
                };
            }
            return result;
        }

        private static string ResolveHumanoidDeltaBase(JObject request)
        {
            var value = (string)request?["validation"]?["humanoidDeltaBase"]
                ?? Environment.GetEnvironmentVariable("ANIMESTUDIO_UNITY_BAKE_DELTA_BASE");
            return string.Equals(value, "first_sample", StringComparison.OrdinalIgnoreCase)
                ? "first_sample"
                : "rest_pose";
        }

        private static float[][] ApplyRestRelativeRotations(float[][] unityValues, float[] unityRest, float[] gltfRest, bool useFirstSampleAsBase)
        {
            var result = new float[unityValues.Length][];
            var baseRotation = useFirstSampleAsBase && unityValues.Length > 0
                ? NormalizeQuaternion(unityValues[0])
                : NormalizeQuaternion(unityRest);
            var inverseBaseRotation = Inverse(baseRotation);
            var mode = RotationApplyMode;
            for (var i = 0; i < unityValues.Length; i++)
            {
                var current = NormalizeQuaternion(unityValues[i]);
                if (string.Equals(mode, "absolute_unity_to_gltf", StringComparison.OrdinalIgnoreCase))
                {
                    result[i] = NormalizeQuaternion(UnityToGltfRotation(current));
                    continue;
                }
                if (string.Equals(mode, "absolute_raw", StringComparison.OrdinalIgnoreCase))
                {
                    result[i] = current;
                    continue;
                }

                // Unity bake 采样到的是 retarget 后的绝对局部姿态。
                // glTF 模型要保留自己的 rest pose，只写 Unity 采样姿态相对 Unity rest pose 的变化。
                // 不能默认拿第一帧当基准，否则会把 idle、aim、pose-only 这类第一帧有效姿态抹掉。
                var unityDelta = string.Equals(mode, "delta_current_inverse_rest", StringComparison.OrdinalIgnoreCase)
                    ? NormalizeQuaternion(Multiply(current, inverseBaseRotation))
                    : NormalizeQuaternion(Multiply(inverseBaseRotation, current));
                var gltfDelta = string.Equals(mode, "delta_no_basis", StringComparison.OrdinalIgnoreCase)
                    ? unityDelta
                    : UnityToGltfRotation(unityDelta);
                result[i] = string.Equals(mode, "delta_pre_multiply", StringComparison.OrdinalIgnoreCase)
                    ? NormalizeQuaternion(Multiply(gltfDelta, gltfRest))
                    : NormalizeQuaternion(Multiply(gltfRest, gltfDelta));
            }
            return result;
        }

        private static string RotationApplyMode =>
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANIMESTUDIO_UNITY_BAKE_ROTATION_MODE"))
                ? "delta_inverse_rest_current"
                : Environment.GetEnvironmentVariable("ANIMESTUDIO_UNITY_BAKE_ROTATION_MODE");

        private static float[][] ApplyRestRelativeScales(float[][] unityValues, float[] unityRestScale, float[] gltfRestScale, bool useFirstSampleAsBase)
        {
            var result = new float[unityValues.Length][];
            var baseScale = useFirstSampleAsBase && unityValues.Length > 0
                ? unityValues[0]
                : unityRestScale;
            for (var i = 0; i < unityValues.Length; i++)
            {
                result[i] = new[]
                {
                    ApplyScaleDelta(unityValues[i][0], baseScale[0], gltfRestScale[0]),
                    ApplyScaleDelta(unityValues[i][1], baseScale[1], gltfRestScale[1]),
                    ApplyScaleDelta(unityValues[i][2], baseScale[2], gltfRestScale[2]),
                };
            }
            return result;
        }

        private static float ApplyScaleDelta(float current, float unityRest, float gltfRest)
        {
            return Math.Abs(unityRest) <= 0.000001f
                ? current
                : gltfRest * (current / unityRest);
        }

        private static float[] ReadNodeTranslation(JObject node)
        {
            var value = node?["translation"]?.Values<float>().ToArray();
            return value != null && value.Length >= 3
                ? new[] { value[0], value[1], value[2] }
                : new[] { 0f, 0f, 0f };
        }

        private static float[] ReadNodeRotation(JObject node)
        {
            var value = node?["rotation"]?.Values<float>().ToArray();
            return value != null && value.Length >= 4
                ? NormalizeQuaternion(new[] { value[0], value[1], value[2], value[3] })
                : new[] { 0f, 0f, 0f, 1f };
        }

        private static float[] ReadNodeScale(JObject node)
        {
            var value = node?["scale"]?.Values<float>().ToArray();
            return value != null && value.Length >= 3
                ? new[] { value[0], value[1], value[2] }
                : new[] { 1f, 1f, 1f };
        }

        private static bool TryReadVector3Key(JToken token, out float[] value)
        {
            value = null;
            if (token == null)
            {
                return false;
            }
            var x = (float?)token["x"];
            var y = (float?)token["y"];
            var z = (float?)token["z"];
            if (!x.HasValue || !y.HasValue || !z.HasValue)
            {
                return false;
            }
            value = new[] { x.Value, y.Value, z.Value };
            return true;
        }

        private static bool TryReadQuaternionKey(JToken token, out float[] value)
        {
            value = null;
            if (token == null)
            {
                return false;
            }
            var x = (float?)token["x"];
            var y = (float?)token["y"];
            var z = (float?)token["z"];
            var w = (float?)token["w"];
            if (!x.HasValue || !y.HasValue || !z.HasValue || !w.HasValue)
            {
                return false;
            }
            value = NormalizeQuaternion(new[] { x.Value, y.Value, z.Value, w.Value });
            return true;
        }

        private static float[] UnityToGltfPosition(float[] value) => new[] { -value[0], value[1], value[2] };

        private static float[] UnityToGltfRotation(float[] value) => new[] { value[0], -value[1], -value[2], value[3] };

        private static float[] GltfToUnityPosition(float[] value) => UnityToGltfPosition(value);

        private static float[] GltfToUnityRotation(float[] value) => UnityToGltfRotation(value);

        private static TrackMotionReport BuildTrackMotionReport(
            string bakePath,
            string gltfPath,
            float[][] translations,
            float[][] rotations,
            float[][] scales)
        {
            var translationDelta = MaxDeltaFromFirst(translations);
            var rotationDelta = MaxRotationDeltaFromFirst(rotations);
            var scaleDelta = MaxDeltaFromFirst(scales);
            var translationVarying = translationDelta > 0.00001f;
            var rotationVarying = rotationDelta > 0.00001f;
            var scaleVarying = scaleDelta > 0.00001f;
            return new TrackMotionReport(
                BakePath: bakePath,
                GltfPath: gltfPath,
                FrameVarying: translationVarying || rotationVarying || scaleVarying,
                FrameVaryingChannelCount:
                    (translationVarying ? 1 : 0) +
                    (rotationVarying ? 1 : 0) +
                    (scaleVarying ? 1 : 0),
                MaxTranslationDelta: translationDelta,
                MaxRotationDelta: rotationDelta,
                MaxScaleDelta: scaleDelta);
        }

        private static FirstPoseMotionReport BuildFirstPoseMotionReport(
            string bakePath,
            string gltfPath,
            float[][] unityTranslations,
            float[][] unityRotations,
            float[][] unityScales,
            float[] unityRestTranslation,
            float[] unityRestRotation,
            float[] unityRestScale)
        {
            var translationDelta = FirstVectorDelta(unityTranslations, unityRestTranslation);
            var rotationDelta = FirstRotationDelta(unityRotations, unityRestRotation);
            var scaleDelta = FirstVectorDelta(unityScales, unityRestScale);
            return new FirstPoseMotionReport(
                BakePath: bakePath,
                GltfPath: gltfPath,
                FirstPoseChanged:
                    translationDelta > 0.00001f ||
                    rotationDelta > 0.00001f ||
                    scaleDelta > 0.00001f,
                FirstPoseTranslationDelta: translationDelta,
                FirstPoseRotationDelta: rotationDelta,
                FirstPoseScaleDelta: scaleDelta);
        }

        private static float FirstVectorDelta(float[][] values, float[] rest)
        {
            if (values == null || values.Length == 0 || values[0] == null || rest == null)
            {
                return 0f;
            }

            var first = values[0];
            var sum = 0f;
            for (var i = 0; i < Math.Min(first.Length, rest.Length); i++)
            {
                var delta = first[i] - rest[i];
                sum += delta * delta;
            }
            return (float)Math.Sqrt(sum);
        }

        private static float FirstRotationDelta(float[][] values, float[] rest)
        {
            if (values == null || values.Length == 0 || values[0] == null || values[0].Length < 4 || rest == null || rest.Length < 4)
            {
                return 0f;
            }

            var first = NormalizeQuaternion(values[0]);
            var normalizedRest = NormalizeQuaternion(rest);
            var dot = Math.Abs(
                first[0] * normalizedRest[0] +
                first[1] * normalizedRest[1] +
                first[2] * normalizedRest[2] +
                first[3] * normalizedRest[3]);
            return 1f - Math.Min(1f, dot);
        }

        private static float MaxDeltaFromFirst(float[][] values)
        {
            if (values == null || values.Length < 2)
            {
                return 0f;
            }

            var first = values[0];
            var max = 0f;
            for (var i = 1; i < values.Length; i++)
            {
                var current = values[i];
                if (current == null || first == null)
                {
                    continue;
                }

                var sum = 0f;
                for (var c = 0; c < Math.Min(first.Length, current.Length); c++)
                {
                    var delta = current[c] - first[c];
                    sum += delta * delta;
                }
                max = Math.Max(max, (float)Math.Sqrt(sum));
            }
            return max;
        }

        private static float MaxRotationDeltaFromFirst(float[][] values)
        {
            if (values == null || values.Length < 2 || values[0] == null || values[0].Length < 4)
            {
                return 0f;
            }

            var first = NormalizeQuaternion(values[0]);
            var max = 0f;
            for (var i = 1; i < values.Length; i++)
            {
                if (values[i] == null || values[i].Length < 4)
                {
                    continue;
                }
                var current = NormalizeQuaternion(values[i]);
                var dot = Math.Abs(
                    first[0] * current[0] +
                    first[1] * current[1] +
                    first[2] * current[2] +
                    first[3] * current[3]);
                max = Math.Max(max, 1f - Math.Min(1f, dot));
            }
            return max;
        }

        private static float[] NormalizeQuaternion(float[] value)
        {
            var length = (float)Math.Sqrt(
                value[0] * value[0] +
                value[1] * value[1] +
                value[2] * value[2] +
                value[3] * value[3]);
            if (length <= 0f)
            {
                return new[] { 0f, 0f, 0f, 1f };
            }
            return new[] { value[0] / length, value[1] / length, value[2] / length, value[3] / length };
        }

        private static float[] Multiply(float[] a, float[] b)
        {
            return new[]
            {
                a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
                a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
                a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3],
                a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2],
            };
        }

        private static float[] Inverse(float[] value)
        {
            var normalized = NormalizeQuaternion(value);
            return new[] { -normalized[0], -normalized[1], -normalized[2], normalized[3] };
        }

        private static void WriteChannel(JObject gltf, JObject animation, List<byte> bufferBytes, int nodeIndex, string path, float[] times, float[][] values)
        {
            if (values.Length != times.Length || values.Length == 0)
            {
                return;
            }
            var inputAccessor = WriteAccessor(gltf, bufferBytes, times.Select(x => new[] { x }).ToArray(), "SCALAR");
            var outputAccessor = WriteAccessor(gltf, bufferBytes, values, path == "rotation" ? "VEC4" : "VEC3");
            var samplers = (JArray)animation["samplers"];
            var channels = (JArray)animation["channels"];
            var samplerIndex = samplers.Count;
            samplers.Add(new JObject
            {
                ["input"] = inputAccessor,
                ["interpolation"] = "LINEAR",
                ["output"] = outputAccessor,
            });
            channels.Add(new JObject
            {
                ["sampler"] = samplerIndex,
                ["target"] = new JObject
                {
                    ["node"] = nodeIndex,
                    ["path"] = path,
                }
            });
        }

        private static int WriteAccessor(JObject gltf, List<byte> bufferBytes, float[][] rows, string type)
        {
            Align(bufferBytes, 4);
            var byteOffset = bufferBytes.Count;
            foreach (var row in rows)
            {
                foreach (var value in row)
                {
                    bufferBytes.AddRange(BitConverter.GetBytes(value));
                }
            }
            var byteLength = bufferBytes.Count - byteOffset;
            var bufferViews = (JArray)(gltf["bufferViews"] ??= new JArray());
            var accessors = (JArray)(gltf["accessors"] ??= new JArray());
            var bufferViewIndex = bufferViews.Count;
            bufferViews.Add(new JObject
            {
                ["buffer"] = 0,
                ["byteOffset"] = byteOffset,
                ["byteLength"] = byteLength,
            });
            var components = rows[0].Length;
            var accessor = new JObject
            {
                ["bufferView"] = bufferViewIndex,
                ["componentType"] = 5126,
                ["count"] = rows.Length,
                ["type"] = type,
                ["min"] = new JArray(Enumerable.Range(0, components).Select(i => rows.Min(x => x[i]))),
                ["max"] = new JArray(Enumerable.Range(0, components).Select(i => rows.Max(x => x[i]))),
            };
            var accessorIndex = accessors.Count;
            accessors.Add(accessor);
            return accessorIndex;
        }

        private static void Align(List<byte> bytes, int alignment)
        {
            while (bytes.Count % alignment != 0)
            {
                bytes.Add(0);
            }
        }

        private static void RemoveEmptyTopLevelArrays(JObject gltf)
        {
            // glTF 顶层空数组不合法。无材质/贴图的灰模应省略这些字段，
            // 而不是写成 images/materials/textures/samplers: []。
            foreach (var key in new[] { "images", "materials", "textures", "samplers" })
            {
                if (gltf[key] is JArray array && array.Count == 0)
                {
                    gltf.Remove(key);
                }
            }
        }

        private static string SafeName(string value)
        {
            var name = string.IsNullOrWhiteSpace(value) ? "baked" : value;
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        private sealed record SkinAnimationReport(
            int Nodes,
            int Skins,
            int SkinJoints,
            int AnimationTargets,
            int TargetNotJointCount,
            int HierarchyParentTargetCount,
            int AuxiliaryNonSkinTargetCount,
            int InvalidTargetCount,
            int JointNotTargetCount,
            MeshSkinNode[] MeshSkinNodes,
            NodeRef[] HierarchyParentTargets,
            NodeRef[] AuxiliaryNonSkinTargets,
            NodeRef[] InvalidTargets,
            NodeRef[] JointNotTargetSample,
            string Rule
        );

        private sealed record MeshSkinNode(int Node, string Name, int Mesh, int Skin);

        private sealed record NodeRef(int Node, string Path);

        private sealed record LodNodeInfo(string BaseName, int Lod);

        private sealed record HiddenLodMeshReport(
            int Node,
            string Name,
            int Mesh,
            string Group,
            int HiddenLod,
            string SelectedNode,
            int SelectedLod);

        private sealed record TrackMotionReport(
            string BakePath,
            string GltfPath,
            bool FrameVarying,
            int FrameVaryingChannelCount,
            float MaxTranslationDelta,
            float MaxRotationDelta,
            float MaxScaleDelta,
            bool FirstPoseChanged = false,
            float FirstPoseTranslationDelta = 0f,
            float FirstPoseRotationDelta = 0f,
            float FirstPoseScaleDelta = 0f);

        private sealed record FirstPoseMotionReport(
            string BakePath,
            string GltfPath,
            bool FirstPoseChanged,
            float FirstPoseTranslationDelta,
            float FirstPoseRotationDelta,
            float FirstPoseScaleDelta);

        private sealed class CurveCategoryCounter
        {
            private readonly List<JObject> _examples = new();

            public CurveCategoryCounter(string name)
            {
                Name = name;
            }

            public string Name { get; }

            public int Count { get; private set; }

            public int DynamicCount { get; private set; }

            public float MaxDelta { get; private set; }

            public void Add(string propertyName, string path, float delta)
            {
                Count++;
                if (delta > 0.00001f)
                {
                    DynamicCount++;
                    MaxDelta = Math.Max(MaxDelta, delta);
                    if (_examples.Count < 8)
                    {
                        _examples.Add(new JObject
                        {
                            ["propertyName"] = propertyName ?? string.Empty,
                            ["path"] = path ?? string.Empty,
                            ["maxDelta"] = delta,
                        });
                    }
                }
            }

            public JObject ToJson()
            {
                return new JObject
                {
                    ["count"] = Count,
                    ["dynamicCount"] = DynamicCount,
                    ["maxDelta"] = MaxDelta,
                    ["examples"] = new JArray(_examples),
                };
            }
        }

        private sealed record AvatarTrustReport(
            bool TrustedProductionBake,
            string Source,
            string Message);

        private sealed record HumanoidRuntimeSamplingReport(
            int EditorCurveTrackCount,
            int AnimatorEditorCurveTrackCount,
            int DynamicEditorCurveTrackCount,
            int DynamicAnimatorEditorCurveTrackCount,
            int HumanoidPoseSampleCount,
            int HumanoidMuscleCount,
            int VaryingHumanoidMuscleCount,
            float MaxHumanoidMuscleDelta,
            float MaxBodyPositionDelta,
            float MaxBodyRotationDelta,
            bool HasEditorCurvesButNoRuntimePoseMotion,
            bool HasOnlyConstantEditorCurves,
            JObject EditorCurveCategorySummary,
            string Status,
            string Message);

        private sealed record UnityBakeTrackSourceSelection(
            JObject[] Tracks,
            string Source,
            string Reason,
            bool UsesEditorCurveSetHumanPose,
            bool UsesAnimationModeSampleClip,
            JObject RuntimeSummary,
            JObject EditorCurveSetHumanPoseSummary,
            JObject AnimationModeSampleClipSummary);

        private sealed record BakeTrackSourceSummary(
            int TotalTrackCount,
            int ChangedTrackCount,
            int MatchedTrackCount,
            int FrameVaryingTrackCount,
            int FrameVaryingCoreBodyTrackCount)
        {
            public JObject ToJson()
            {
                return new JObject
                {
                    ["totalTrackCount"] = TotalTrackCount,
                    ["changedTrackCount"] = ChangedTrackCount,
                    ["matchedTrackCount"] = MatchedTrackCount,
                    ["frameVaryingTrackCount"] = FrameVaryingTrackCount,
                    ["frameVaryingCoreBodyTrackCount"] = FrameVaryingCoreBodyTrackCount,
                };
            }
        }
    }
}
