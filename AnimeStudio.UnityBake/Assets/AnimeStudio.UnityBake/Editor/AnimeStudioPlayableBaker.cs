using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Collections;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace AnimeStudio.UnityBake
{
    public static class AnimeStudioPlayableBaker
    {
        private const float PositionEpsilon = 0.00001f;

        public static AnimeStudioBakeResult Bake(AnimeStudioBakeRequest request)
        {
            if (request.unityAssetPaths == null)
            {
                return Error(request, "Request is missing unityAssetPaths.");
            }
            var prefab = string.IsNullOrWhiteSpace(request.unityAssetPaths.modelPrefab)
                ? null
                : AssetDatabase.LoadAssetAtPath<GameObject>(request.unityAssetPaths.modelPrefab);
            var clipLoad = LoadAnimationClip(request);
            var clip = clipLoad != null ? clipLoad.clip : null;
            if (clip == null)
            {
                return Error(request, $"AnimationClip not found or could not be imported: {request.unityAssetPaths.animationClip ?? request.animeStudioAssets?.animation?.anim}");
            }
            var clipRebuildStats = TryRebuildHumanoidRuntimeClipFromEditorCurves(request, ref clip);
            var requestClipFilterMode = ResolveClipFilterMode(request);
            if (request.animeStudioAssets?.animation?.requiresHumanoidBake == true
                && !string.Equals(requestClipFilterMode, "transform_only", StringComparison.OrdinalIgnoreCase)
                && !clip.isHumanMotion)
            {
                // Humanoid/Muscle 动画如果没有被 Unity 导入成 humanMotion，
                // PlayableGraph 只能采样到普通 Transform 曲线，身体主动作会丢失。
                return Error(
                    request,
                    "Humanoid/Muscle production bake requires Unity to import the selected AnimationClip as humanMotion, but imported clip.isHumanMotion=false. This usually means the clip is an AnimatorController auxiliary/non-body layer, or the controller context still needs a deterministic baseLayerClip. Refusing to write a misleading baked glTF.");
            }
            var clipFilterStats = ApplyDiagnosticClipFilter(requestClipFilterMode, ref clip);
            Avatar explicitAvatar;
            try
            {
                explicitAvatar = AnimeStudioGltfSkeletonBuilder.LoadImportedAvatarAsset(request);
            }
            catch (Exception ex)
            {
                return Error(request, ex.Message);
            }

            var instance = CreateBakeTarget(request, prefab);
            if (instance == null)
            {
                return Error(request, "Model prefab was not found and glTF skeleton fallback could not be built.");
            }

            try
            {
                var transforms = instance.GetComponentsInChildren<Transform>(true)
                    .OrderBy(x => GetPath(instance.transform, x), StringComparer.Ordinal)
                    .ToArray();
                var requestedAvatarAsset = AnimeStudioGltfSkeletonBuilder.NormalizeUnityAssetPath(request.unityAssetPaths.avatarAsset);
                var importedAvatarBinding = BuildImportedAvatarBindingReport(requestedAvatarAsset, instance.transform, transforms);
                var runtimeRoot = explicitAvatar != null
                    ? SelectRuntimeRootForImportedAvatar(instance.transform, importedAvatarBinding)
                    : instance.transform;
                var animator = EnsureAnimator(instance.transform, runtimeRoot, explicitAvatar);

                if (clip.isHumanMotion && (animator.avatar == null || !animator.avatar.isValid))
                {
                    return Error(request, "Humanoid clip requires a valid Animator.avatar. Refusing to bake guessed data.");
                }

                var frameRate = Mathf.Max(1, request.frameRate);
                var sampleTimes = BuildSampleTimes(clip.length, frameRate);
                var editorCurveTracks = request.probeMuscles
                    ? SampleEditorCurves(clip, sampleTimes)
                    : new List<EditorCurveTrack>();
                if (request.probeMuscles)
                {
                    AddMissingMuscleCurvesFromDecodedSidecar(editorCurveTracks, request, sampleTimes);
                }
                var tracks = transforms
                    .Where(t => t == runtimeRoot || t.IsChildOf(runtimeRoot))
                    .Select(t => new TrackRecorder(runtimeRoot, t, sampleTimes))
                    .ToArray();
                var enableEditorCurveIkGoalDriver = request.probeMuscles && IsEditorCurveIkGoalDriverEnabled(request);
                var controllerSampling = BuildAnimatorControllerSampling(request, clip, enableEditorCurveIkGoalDriver);
                TryPromoteRecoveredControllerLayerMasks(controllerSampling, runtimeRoot, transforms);
                if (controllerSampling.Controller != null)
                {
                    animator.runtimeAnimatorController = controllerSampling.Controller;
                }
                var editorCurveIkDriver = enableEditorCurveIkGoalDriver
                    ? runtimeRoot.gameObject.AddComponent<AnimeStudioEditorCurveIkGoalDriver>()
                    : null;
                editorCurveIkDriver?.Configure(
                    runtimeRoot,
                    BuildAnimationCurveMap(editorCurveTracks),
                    BuildControllerLayerWeightMap(controllerSampling),
                    ResolveControllerLayerWeightSource(controllerSampling));

                var graph = PlayableGraph.Create("AnimeStudioUnityBake");
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var useLayerMixer = controllerSampling.UseLayerMixer
                    && controllerSampling.LayerStates != null
                    && controllerSampling.LayerStates.Count > 0
                    && controllerSampling.LayerStates.All(x => x.Clip != null);
                var clipPlayable = controllerSampling.Controller == null && !useLayerMixer
                    ? AnimationClipPlayable.Create(graph, clip)
                    : default;
                var controllerPlayable = controllerSampling.Controller != null && !useLayerMixer
                    ? AnimatorControllerPlayable.Create(graph, controllerSampling.Controller)
                    : default;
                var layerMixerPlayable = useLayerMixer
                    ? BuildLayerMixerPlayable(graph, controllerSampling, runtimeRoot, transforms)
                    : default;
                var output = AnimationPlayableOutput.Create(graph, "Animation", animator);
                if (useLayerMixer)
                {
                    output.SetSourcePlayable(layerMixerPlayable);
                }
                else if (controllerSampling.Controller != null)
                {
                    output.SetSourcePlayable(controllerPlayable);
                }
                else
                {
                    output.SetSourcePlayable(clipPlayable);
                }

                AnimationMode.StartAnimationMode();
                var humanoidPoseSamples = new List<HumanoidPoseSample>();
                var sampleBounds = new List<SampleBounds>();
                var poseHandler = request.probeMuscles
                    && animator.avatar != null
                    && animator.avatar.isValid
                    && animator.avatar.isHuman
                        ? new HumanPoseHandler(animator.avatar, runtimeRoot)
                        : null;
                try
                {
                    graph.Play();
                    for (var i = 0; i < sampleTimes.Length; i++)
                    {
                        var time = sampleTimes[i];
                        if (editorCurveIkDriver != null)
                        {
                            editorCurveIkDriver.SampleTime = time;
                        }
                        AnimationMode.BeginSampling();
                        SamplePlayableGraph(graph, controllerPlayable, layerMixerPlayable, controllerSampling, clip, time);
                        AnimationMode.EndSampling();
                        editorCurveIkDriver?.CapturePostEvaluateBones(animator);
                        foreach (var track in tracks)
                        {
                            track.Record(i, time);
                        }
                        // 二骨 IK 是诊断探针，只回答“同一个 goal 是否几何可达”。
                        // 必须在记录 glTF TRS 之后执行，避免诊断求解污染 Unity 原始采样结果。
                        editorCurveIkDriver?.ApplyDirectTwoBoneIkDiagnostic(animator);
                        sampleBounds.Add(ReadSampleBounds(runtimeRoot, time));
                        if (poseHandler != null)
                        {
                            var pose = new HumanPose();
                            poseHandler.GetHumanPose(ref pose);
                            humanoidPoseSamples.Add(new HumanoidPoseSample
                            {
                                time = time,
                                bodyPosition = new Vector3Key(time, pose.bodyPosition),
                                bodyRotation = new QuaternionKey(time, pose.bodyRotation),
                                muscles = pose.muscles != null ? (float[])pose.muscles.Clone() : new float[0],
                            });
                        }
                    }
                }
                finally
                {
                    if (graph.IsValid())
                    {
                        graph.Destroy();
                    }
                    AnimationMode.StopAnimationMode();
                }

                var resultTracks = tracks.Select(x => x.ToResult()).ToList();
                var humanoidBoneDiagnostics = CaptureHumanoidBoneDiagnostics(animator, runtimeRoot, resultTracks);
                var internalAvatarPoseSnapshots = new List<InternalAvatarPoseSnapshot>();
                var internalAvatarPoseTimeline = new List<InternalAvatarPoseTimelineSample>();
                var internalAvatarPoseMuscleProbes = new List<MuscleProbe>();
                var internalAvatarPoseMuscleCombinationProbes = new List<MuscleCombinationProbe>();
                var muscleProbes = request.probeMuscles
                    ? ProbeMuscles(runtimeRoot, animator, transforms.Where(t => t == runtimeRoot || t.IsChildOf(runtimeRoot)).ToArray(), editorCurveTracks, internalAvatarPoseSnapshots, out internalAvatarPoseMuscleProbes)
                    : new List<MuscleProbe>();
                var muscleCombinationProbes = request.probeMuscles
                    ? ProbeMuscleCombinations(runtimeRoot, animator, transforms.Where(t => t == runtimeRoot || t.IsChildOf(runtimeRoot)).ToArray(), internalAvatarPoseSnapshots, out internalAvatarPoseMuscleCombinationProbes)
                    : new List<MuscleCombinationProbe>();
                var editorCurveSetHumanPoseTransformTracks = new List<TransformTrack>();
                var editorCurveHumanPoseDiagnostic = request.probeMuscles
                    ? BuildEditorCurveHumanPoseDiagnostic(editorCurveTracks, sampleTimes)
                    : null;
                var editorCurveIkGoalDriverDiagnostic = editorCurveIkDriver != null
                    ? editorCurveIkDriver.BuildDiagnostic()
                    : null;
                var editorCurveHumanPoseRotations = request.probeMuscles
                    ? ApplyFirstEditorCurveSampleAsHumanPose(runtimeRoot, animator, transforms.Where(t => t == runtimeRoot || t.IsChildOf(runtimeRoot)).ToArray(), editorCurveTracks, sampleTimes, internalAvatarPoseSnapshots, internalAvatarPoseTimeline, out editorCurveSetHumanPoseTransformTracks)
                    : new List<ProbeRotationTrack>();
                var animationModeSampleClipTransformTracks = request.probeMuscles
                    ? CaptureAnimationModeSampleClipTimeline(runtimeRoot, animator, clip, transforms.Where(t => t == runtimeRoot || t.IsChildOf(runtimeRoot)).ToArray(), sampleTimes)
                    : new List<TransformTrack>();
                var rigMetadata = instance.GetComponentInChildren<AnimeStudioBakeRigMetadata>(true);
                var importedAvatarAssetValid = !string.IsNullOrWhiteSpace(requestedAvatarAsset)
                    && animator.avatar != null
                    && animator.avatar.isValid
                    && animator.avatar.isHuman
                    && rigMetadata != null
                    && IsImportedAvatarRestPoseSource(rigMetadata.restPoseSource);
                var controllerLayerDiagnostics = BuildControllerLayerDiagnostics(controllerSampling);
                var controllerParameterDiagnostics = BuildControllerParameterDiagnostics(controllerSampling);
                var controllerRuntimeParameterSummary = BuildControllerRuntimeParameterSummary(controllerParameterDiagnostics, controllerLayerDiagnostics);
                var controllerBlendTreeDiagnostics = BuildControllerBlendTreeDiagnostics(controllerSampling);
                var layerMixerBypassesControllerBlendTrees =
                    controllerSampling.UseLayerMixer &&
                    ControllerLayerMixerBypassesBlendTreeSelection(controllerSampling, controllerBlendTreeDiagnostics);
                var unmaskedOverrideLayers = FindUnmaskedOverrideLayerNames(controllerSampling);
                return new AnimeStudioBakeResult
                {
                    status = "ok",
                    message = "Unity Animator/PlayableGraph bake completed.",
                    modelPrefab = request.unityAssetPaths.modelPrefab,
                    animationClip = clipLoad.assetPath,
                    requestedAnimationClip = AnimeStudioGltfSkeletonBuilder.NormalizeUnityAssetPath(request.unityAssetPaths.animationClip),
                    importedAnimationClip = clipLoad.assetPath,
                    animationClipSource = clipLoad.source,
                    requestedAnimatorController = AnimeStudioGltfSkeletonBuilder.NormalizeUnityAssetPath(request.unityAssetPaths.animatorController),
                    animatorControllerSamplingMode = controllerSampling.Mode,
                    animatorControllerSamplingState = controllerSampling.StateName,
                    animatorControllerSamplingAsset = controllerSampling.AssetPath,
                    animatorControllerSamplingMessage = controllerSampling.Message,
                    animatorControllerAdditionalLayerMaskCount = controllerSampling.AdditionalLayerMaskCount,
                    animatorControllerAdditionalLayerSkeletonMaskEntryCount = controllerSampling.AdditionalLayerSkeletonMaskEntryCount,
                    animatorControllerLayerMasksApplied = controllerSampling.LayerMasksApplied,
                    animatorControllerLayerMaskWarning = controllerSampling.LayerMaskWarning,
                    requestEnableEditorCurveIkGoalDriver = request.enableEditorCurveIkGoalDriver,
                    effectiveEditorCurveIkGoalDriver = enableEditorCurveIkGoalDriver,
                    requestSampleRecoverableSkippedLayersDiagnostic = request.sampleRecoverableSkippedLayersDiagnostic,
                    animatorControllerIkPassEnabledLayerCount = controllerSampling.IkPassEnabledLayerCount,
                    animatorControllerIkPassMessage = controllerSampling.IkPassMessage,
                    animatorControllerIkPassEffectiveForSampling = IsIkPassEffectiveForSampling(controllerSampling),
                    animatorControllerIkPassSamplingMode = ResolveIkPassSamplingMode(controllerSampling),
                    animatorControllerIkPassSamplingWarning = BuildIkPassSamplingWarning(controllerSampling),
                    animatorControllerLayerMixerBypassesControllerBlendTrees = layerMixerBypassesControllerBlendTrees,
                    animatorControllerFidelityWarning = layerMixerBypassesControllerBlendTrees
                        ? "Sampling used AnimationLayerMixerPlayable to apply request skeleton masks, so recovered AnimatorController BlendTree parameter evaluation was bypassed. Treat this as a layer/mask diagnostic until controller parameters, BlendTrees, IK and runtime weights are recovered or equivalently solved."
                        : controllerSampling.UseLayerMixer && controllerBlendTreeDiagnostics.Count > 0
                            ? "Sampling used AnimationLayerMixerPlayable and flattened recovered Simple1D BlendTree default selections into clip playables. This is closer to the recovered controller default state, but runtime parameter changes, complex BlendTrees, additive reference poses, IK and runtime weights are still not proven recovered."
                        : null,
                    animatorControllerUnmaskedOverrideLayerCount = unmaskedOverrideLayers.Length,
                    animatorControllerUnmaskedOverrideLayerNames = unmaskedOverrideLayers,
                    animatorControllerSkippedUntrustedLayerCount = controllerSampling.SkippedUntrustedLayerNames?.Length ?? 0,
                    animatorControllerSkippedUntrustedLayerNames = controllerSampling.SkippedUntrustedLayerNames ?? Array.Empty<string>(),
                    animatorControllerSkippedUntrustedLayerReasons = controllerSampling.SkippedUntrustedLayerReasons ?? Array.Empty<string>(),
                    animatorControllerSkippedUntrustedLayerDiagnostics = controllerSampling.SkippedUntrustedLayerDiagnostics ?? new List<AnimatorControllerSkippedLayerDiagnostic>(),
                    animatorControllerDiagnosticSampledSkippedLayerCount = controllerSampling.DiagnosticSampledSkippedLayerNames?.Length ?? 0,
                    animatorControllerDiagnosticSampledSkippedLayerNames = controllerSampling.DiagnosticSampledSkippedLayerNames ?? Array.Empty<string>(),
                    animatorControllerLayerDiagnostics = controllerLayerDiagnostics,
                    animatorControllerParameterDiagnostics = controllerParameterDiagnostics,
                    animatorControllerRuntimeParameterSummary = controllerRuntimeParameterSummary,
                    animatorControllerBlendTreeDiagnostics = controllerBlendTreeDiagnostics,
                    modelName = prefab != null ? prefab.name : instance.name,
                    clipName = clip.name,
                    clipLength = clip.length,
                    frameRate = frameRate,
                    isHumanMotion = clip.isHumanMotion,
                    avatarName = animator.avatar != null ? animator.avatar.name : null,
                    avatarValid = animator.avatar != null && animator.avatar.isValid,
                    runtimeRootPath = GetPath(instance.transform, runtimeRoot),
                    requestedAvatarAsset = requestedAvatarAsset,
                    importedAvatarAsset = importedAvatarAssetValid ? requestedAvatarAsset : null,
                    importedAvatarAssetValid = importedAvatarAssetValid,
                    importedAvatarBinding = importedAvatarBinding,
                    clipFilterMode = clipFilterStats.mode,
                    clipFilterRemovedTransformCurveCount = clipFilterStats.removedTransformCurveCount,
                    clipFilterRemovedAnimatorCurveCount = clipFilterStats.removedAnimatorCurveCount,
                    clipFilterRemovedObjectReferenceCurveCount = clipFilterStats.removedObjectReferenceCurveCount,
                    requestRebuildEditorCurveClip = request.rebuildEditorCurveClip,
                    requestIgnoreImportedAvatar = request.ignoreImportedAvatar,
                    clipRebuildMode = clipRebuildStats.mode,
                    clipRebuildAttempted = clipRebuildStats.attempted,
                    clipRebuildSucceeded = clipRebuildStats.succeeded,
                    clipRebuildIsHumanMotion = clipRebuildStats.isHumanMotion,
                    clipRebuildCurveCount = clipRebuildStats.curveCount,
                    clipRebuildAssetPath = clipRebuildStats.assetPath,
                    clipRebuildMessage = clipRebuildStats.message,
                    rigRestPoseSource = rigMetadata != null ? rigMetadata.restPoseSource : "prefab_or_gltf_transform_rest_pose",
                    rigRestPoseApplied = rigMetadata != null && rigMetadata.restPoseApplied,
                    sampleCount = sampleTimes.Length,
                    transformCount = transforms.Length,
                    changedTrackCount = resultTracks.Count(x => x.changed),
                    muscleProbeCount = muscleProbes.Count,
                    sampleTimes = sampleTimes,
                    sampleBounds = sampleBounds,
                    tracks = resultTracks,
                    humanoidBoneDiagnostics = humanoidBoneDiagnostics,
                    muscleProbes = muscleProbes,
                    internalAvatarPoseMuscleProbes = internalAvatarPoseMuscleProbes,
                    muscleCombinationProbes = muscleCombinationProbes,
                    internalAvatarPoseMuscleCombinationProbes = internalAvatarPoseMuscleCombinationProbes,
                    muscleNames = request.probeMuscles ? HumanTrait.MuscleName : null,
                    humanoidPoseSamples = humanoidPoseSamples,
                    editorCurveTracks = editorCurveTracks,
                    editorCurveHumanPoseDiagnostic = editorCurveHumanPoseDiagnostic,
                    editorCurveIkGoalDriverDiagnostic = editorCurveIkGoalDriverDiagnostic,
                    editorCurveHumanPoseRotations = editorCurveHumanPoseRotations,
                    editorCurveSetHumanPoseTransformTracks = editorCurveSetHumanPoseTransformTracks,
                    animationModeSampleClipTransformTracks = animationModeSampleClipTransformTracks,
                    internalAvatarPoseSnapshots = internalAvatarPoseSnapshots,
                    internalAvatarPoseTimeline = internalAvatarPoseTimeline,
                };
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        private static bool IsEditorCurveIkGoalDriverEnabled(AnimeStudioBakeRequest request)
        {
            if (request?.enableEditorCurveIkGoalDriver == true)
            {
                return true;
            }
            var value = Environment.GetEnvironmentVariable("ANIMESTUDIO_UNITY_BAKE_ENABLE_IK_GOAL_DRIVER");
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<int, float> BuildControllerLayerWeightMap(ControllerSamplingPlan controllerSampling)
        {
            if (controllerSampling?.LayerStates == null || controllerSampling.LayerStates.Count == 0)
            {
                return null;
            }

            // LayerMixer 分支的权重来自我们恢复出的 controller layer 状态，
            // 不能再让 IK 诊断回头读 Animator 的运行时层权重。
            return controllerSampling.LayerStates
                .GroupBy(x => x.LayerIndex)
                .ToDictionary(x => x.Key, x => x.First().Weight);
        }

        private static string ResolveControllerLayerWeightSource(ControllerSamplingPlan controllerSampling)
        {
            if (controllerSampling?.LayerStates == null || controllerSampling.LayerStates.Count == 0)
            {
                return "animator.GetLayerWeight";
            }

            return controllerSampling.UseLayerMixer
                ? "controllerSampling.LayerStates.Weight via AnimationLayerMixerPlayable"
                : "controllerSampling.LayerStates.Weight via AnimatorControllerPlayable";
        }

        private static ControllerSamplingPlan BuildAnimatorControllerSampling(AnimeStudioBakeRequest request, AnimationClip clip, bool enableIkPassDiagnostic)
        {
            var controllerPath = AnimeStudioGltfSkeletonBuilder.NormalizeUnityAssetPath(request?.unityAssetPaths?.animatorController);
            if (!string.IsNullOrWhiteSpace(controllerPath))
            {
                var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(controllerPath);
                if (controller != null)
                {
                    var ikPass = enableIkPassDiagnostic ? CreateIkPassControllerCopy(controller) : new IkPassControllerCopy { Controller = controller };
                    var recoveredController = IsRecoveredImportedAnimatorController(controllerPath);
                    var stateName = ResolveProvidedRuntimeControllerStateName(request, controllerPath);
                    var recoveryMetadata = recoveredController
                        ? LoadControllerRecoveryMetadata(controllerPath)
                        : null;
                    var skippedUntrustedLayers = Array.Empty<string>();
                    var skippedUntrustedLayerReasons = Array.Empty<string>();
                    var skippedUntrustedLayerDiagnostics = new List<AnimatorControllerSkippedLayerDiagnostic>();
                    var diagnosticSampledSkippedLayerNames = Array.Empty<string>();
                    var layerStates = recoveredController
                        ? BuildRecoveredControllerLayerMixerStates(
                            ikPass.Controller ?? controller,
                            request,
                            clip,
                            stateName,
                            recoveryMetadata,
                            out skippedUntrustedLayers,
                            out skippedUntrustedLayerReasons,
                            out skippedUntrustedLayerDiagnostics,
                            out diagnosticSampledSkippedLayerNames)
                        : null;
                    var usesMaskedLayerMixer = layerStates != null
                        && layerStates.Count > 1
                        && layerStates.All(x => x.Clip != null);
                    if (!usesMaskedLayerMixer && recoveredController)
                    {
                        layerStates = BuildRecoveredControllerLayerStates(ikPass.Controller ?? controller, request, stateName);
                    }
                    var additionalLayerMaskCount = CountRequestAdditionalLayerMasks(request);
                    var additionalLayerSkeletonMaskEntryCount = CountRequestAdditionalLayerSkeletonMaskEntries(request);
                    if (usesMaskedLayerMixer)
                    {
                        additionalLayerMaskCount = CountLayerStateMasks(layerStates);
                        additionalLayerSkeletonMaskEntryCount = CountLayerStateSkeletonMaskEntries(layerStates);
                    }
                    var layerMaskWarning = additionalLayerMaskCount > 0 && !usesMaskedLayerMixer
                        ? "Recovered ImportedAnimatorController sampling can set layer state and weight, but request-layer AvatarMask/skeleton-mask data is not applied to the provided controller asset in this path. Treat this as controller-context diagnostic until masked sampling is restored."
                        : null;
                    return new ControllerSamplingPlan
                    {
                        Controller = ikPass.Controller ?? controller,
                        Mode = "provided_runtime_controller",
                        AssetPath = controllerPath,
                        StateName = stateName,
                        RecoveryMetadata = recoveryMetadata,
                        LayerStates = layerStates,
                        UseLayerMixer = usesMaskedLayerMixer,
                        LayerMasksApplied = additionalLayerMaskCount == 0,
                        AdditionalLayerMaskCount = additionalLayerMaskCount,
                        AdditionalLayerSkeletonMaskEntryCount = additionalLayerSkeletonMaskEntryCount,
                        LayerMaskWarning = layerMaskWarning,
                        IkPassEnabledLayerCount = ikPass.EnabledLayerCount,
                        IkPassMessage = ikPass.Message,
                        SkippedUntrustedLayerNames = skippedUntrustedLayers,
                        SkippedUntrustedLayerReasons = skippedUntrustedLayerReasons,
                        SkippedUntrustedLayerDiagnostics = skippedUntrustedLayerDiagnostics,
                        DiagnosticSampledSkippedLayerNames = diagnosticSampledSkippedLayerNames,
                        Message = AppendSentence(AppendSentence(AppendSentence(recoveredController
                            ? "Sampling through recovered ImportedAnimatorController asset; flattened recovered state names are preferred over original nested state paths."
                            : "Sampling through request RuntimeAnimatorController asset.",
                            usesMaskedLayerMixer
                                ? $"Recovered controller context is sampled through AnimationLayerMixerPlayable ({layerStates.Count} layer(s)) so request skeleton masks can be applied."
                            : layerStates != null && layerStates.Count > 0
                                ? $"Recovered controller layer states are sampled explicitly ({layerStates.Count} layer(s)); layer weights are taken from the recovered controller asset."
                                : null),
                            skippedUntrustedLayers.Length > 0
                                ? $"Skipped {skippedUntrustedLayers.Length} recovered controller layer(s) that had no deterministic recovery metadata or layer mask context."
                                : null),
                            AppendSentence(layerMaskWarning, ikPass.Message)),
                    };
                }

                return new ControllerSamplingPlan
                {
                    Mode = "controller_asset_missing",
                    AssetPath = controllerPath,
                    StateName = ResolveAnimatorStateName(request),
                    Message = "Request supplied animatorController, but Unity could not load it.",
                };
            }

            var context = request?.animeStudioAssets?.animation?.animatorControllerContext;
            if (context == null || string.IsNullOrWhiteSpace(context.source))
            {
                return new ControllerSamplingPlan
                {
                    Mode = "clip_playable",
                    StateName = clip != null ? clip.name : null,
                    Message = "No AnimatorController context was supplied; sampling raw AnimationClipPlayable.",
                };
            }

            var generated = CreateSingleStateController(request, clip, context, enableIkPassDiagnostic);
            return new ControllerSamplingPlan
            {
                Controller = generated.Controller,
                Mode = generated.Controller != null && generated.AdditionalLayerCount > 0
                    ? "generated_multi_layer_controller"
                    : generated.Controller != null
                        ? "generated_single_state_controller"
                        : "controller_generation_failed",
                AssetPath = generated.AssetPath,
                StateName = generated.StateName,
                LayerStates = generated.LayerStates,
                UseLayerMixer = generated.AdditionalLayerCount > 0,
                AdditionalLayerMaskCount = generated.AdditionalLayerMaskCount,
                AdditionalLayerSkeletonMaskEntryCount = generated.AdditionalLayerSkeletonMaskEntryCount,
                LayerMasksApplied = generated.AdditionalLayerMaskCount == 0,
                LayerMaskWarning = generated.LayerMaskWarning,
                IkPassEnabledLayerCount = generated.IkPassEnabledLayerCount,
                IkPassMessage = generated.IkPassMessage,
                Message = generated.AdditionalLayerCount > 0
                    ? AppendSentence(generated.Message + " Sampling uses AnimationLayerMixerPlayable so layer weights/additive flags are explicit.", generated.IkPassMessage)
                    : AppendSentence(generated.Message, generated.IkPassMessage),
            };
        }

        private static bool IsImportedAvatarRestPoseSource(string source)
        {
            return !string.IsNullOrWhiteSpace(source)
                && source.StartsWith("imported_unity_avatar_asset", StringComparison.OrdinalIgnoreCase);
        }

        private static GeneratedControllerResult CreateSingleStateController(
            AnimeStudioBakeRequest request,
            AnimationClip clip,
            AnimeStudioAnimatorControllerContext context,
            bool enableIkPassDiagnostic)
        {
            if (clip == null)
            {
                return new GeneratedControllerResult { Message = "Cannot generate AnimatorController because clip is null." };
            }

            var folder = "Assets/AnimeStudioBake/GeneratedAnimatorControllers";
            Directory.CreateDirectory(folder);
            var stateName = ResolveGeneratedControllerStateName(request, clip);
            if (string.IsNullOrWhiteSpace(stateName))
            {
                stateName = clip.name;
            }

            var path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{SanitizeAssetName(clip.name)}_{SanitizeAssetName(stateName)}.controller");
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            if (controller == null || controller.layers == null || controller.layers.Length == 0)
            {
                return new GeneratedControllerResult
                {
                    AssetPath = path,
                    StateName = stateName,
                    Message = "Unity failed to create a temporary AnimatorController asset.",
                };
            }

            var layer = controller.layers[0];
            layer.name = string.IsNullOrWhiteSpace(FirstPathSegment(context.stateFullPath)) ? "Base Layer" : SanitizeAnimatorName(FirstPathSegment(context.stateFullPath));
            layer.iKPass = enableIkPassDiagnostic;
            controller.layers = new[] { layer };
            var stateMachine = controller.layers[0].stateMachine;
            var state = stateMachine.AddState(stateName);
            state.motion = clip;
            state.speed = Mathf.Approximately(context.stateSpeed, 0f) ? 1f : context.stateSpeed;
            state.cycleOffset = context.stateCycleOffset + context.nodeCycleOffset;
            state.mirror = context.stateMirror || context.nodeMirror;
            state.writeDefaultValues = true;
            stateMachine.defaultState = state;

            var layerStates = new List<ControllerLayerState>
            {
                new ControllerLayerState
                {
                    LayerIndex = 0,
                    LayerName = layer.name,
                    StateName = stateName,
                    ClipName = clip.name,
                    Clip = clip,
                    Weight = 1f,
                    Speed = state.speed,
                    CycleOffset = state.cycleOffset,
                    IsAdditive = false,
                    RequestLayerContext = true,
                    MaskApplied = true,
                },
            };
            var addedLayerCount = 0;
            var additionalLayerMaskCount = 0;
            var additionalLayerSkeletonMaskEntryCount = 0;
            var missingLayerClips = new List<string>();
            foreach (var additional in context.additionalLayerClips ?? Array.Empty<AnimeStudioAnimatorControllerLayerClip>())
            {
                var additionalPath = AnimeStudioGltfSkeletonBuilder.NormalizeUnityAssetPath(additional.unityAssetPath);
                var additionalClip = string.IsNullOrWhiteSpace(additionalPath)
                    ? null
                    : AssetDatabase.LoadAssetAtPath<AnimationClip>(additionalPath);
                if (additionalClip == null)
                {
                    missingLayerClips.Add(string.IsNullOrWhiteSpace(additional.name) ? $"layer{additional.layerIndex}" : additional.name);
                    continue;
                }

                var layerName = SanitizeAnimatorName(FirstNonEmpty(FirstPathSegment(additional.stateFullPath), $"Layer{additional.layerIndex}"));
                controller.AddLayer(layerName);
                var layers = controller.layers;
                var actualLayerIndex = layers.Length - 1;
                var additionalLayer = layers[actualLayerIndex];
                additionalLayer.defaultWeight = Mathf.Max(0f, additional.layerDefaultWeight);
                additionalLayer.iKPass = enableIkPassDiagnostic;
                additionalLayer.blendingMode = additional.layerBlendingMode == 1
                    ? AnimatorLayerBlendingMode.Additive
                    : AnimatorLayerBlendingMode.Override;
                if (HasHumanPoseMask(additional.layerBodyMask) || HasSkeletonMask(additional.layerSkeletonMask))
                {
                    additionalLayerMaskCount++;
                    additionalLayerSkeletonMaskEntryCount += additional.layerSkeletonMask?.nonZeroCount ?? 0;
                }
                var additionalStateName = SanitizeAnimatorName(ResolveAdditionalLayerStateName(additional, additionalClip));
                var additionalState = additionalLayer.stateMachine.AddState(additionalStateName);
                additionalState.motion = additionalClip;
                additionalState.speed = Mathf.Approximately(additional.stateSpeed, 0f) ? 1f : additional.stateSpeed;
                additionalState.cycleOffset = additional.stateCycleOffset + additional.nodeCycleOffset;
                additionalState.mirror = additional.stateMirror || additional.nodeMirror;
                additionalState.writeDefaultValues = true;
                additionalLayer.stateMachine.defaultState = additionalState;
                layers[actualLayerIndex] = additionalLayer;
                controller.layers = layers;
                layerStates.Add(new ControllerLayerState
                {
                    LayerIndex = actualLayerIndex,
                    LayerName = additionalLayer.name,
                    StateName = additionalStateName,
                    ClipName = additionalClip.name,
                    Clip = additionalClip,
                    Weight = additionalLayer.defaultWeight,
                    Speed = additionalState.speed,
                    CycleOffset = additionalState.cycleOffset,
                    IsAdditive = additionalLayer.blendingMode == AnimatorLayerBlendingMode.Additive,
                    LayerBodyMask = additional.layerBodyMask,
                    LayerSkeletonMask = additional.layerSkeletonMask,
                    RequestLayerContext = true,
                });
                addedLayerCount++;
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            var message = addedLayerCount > 0
                ? $"Generated a multi-layer AnimatorController from deterministic controller context; added {addedLayerCount} default additive layer clip(s). This is diagnostic unless it is later replaced by the original RuntimeAnimatorController asset."
                : "Generated a single-state AnimatorController from deterministic AnimatorController context. This is diagnostic unless it is later replaced by the original RuntimeAnimatorController asset.";
            if (missingLayerClips.Count > 0)
            {
                message += " Missing additional layer clip asset(s): " + string.Join(", ", missingLayerClips) + ".";
            }
            return new GeneratedControllerResult
            {
                Controller = controller,
                AssetPath = path,
                StateName = stateName,
                LayerStates = layerStates,
                AdditionalLayerCount = addedLayerCount,
                AdditionalLayerMaskCount = additionalLayerMaskCount,
                AdditionalLayerSkeletonMaskEntryCount = additionalLayerSkeletonMaskEntryCount,
                IkPassEnabledLayerCount = enableIkPassDiagnostic ? addedLayerCount + 1 : 0,
                IkPassMessage = enableIkPassDiagnostic
                    ? $"Enabled IK pass on {addedLayerCount + 1} generated AnimatorController layer(s) for editor curve limb goal diagnostics."
                    : null,
                Message = message,
            };
        }

        private static IkPassControllerCopy CreateIkPassControllerCopy(RuntimeAnimatorController controller)
        {
            if (controller is not AnimatorController animatorController)
            {
                return new IkPassControllerCopy
                {
                    Controller = controller,
                    Message = "Provided RuntimeAnimatorController is not an AnimatorController asset; IK pass could not be forced for diagnostic limb goal sampling.",
                };
            }

            var copy = UnityEngine.Object.Instantiate(animatorController);
            copy.name = animatorController.name + "_AnimeStudioIkPassDiagnostic";
            var layers = copy.layers ?? Array.Empty<AnimatorControllerLayer>();
            var enabled = 0;
            for (var i = 0; i < layers.Length; i++)
            {
                if (!layers[i].iKPass)
                {
                    layers[i].iKPass = true;
                }
                enabled++;
            }
            copy.layers = layers;
            return new IkPassControllerCopy
            {
                Controller = copy,
                EnabledLayerCount = enabled,
                Message = enabled > 0
                    ? $"Using an in-memory AnimatorController copy with IK pass enabled on {enabled} layer(s) for editor curve limb goal diagnostics."
                    : "Provided AnimatorController has no layers; IK pass was not enabled.",
            };
        }

        private static bool HasHumanPoseMask(AnimeStudioHumanPoseMask mask)
        {
            return mask != null && !mask.isEmpty && (mask.word0 != 0 || mask.word1 != 0 || mask.word2 != 0);
        }

        private static bool HasSkeletonMask(AnimeStudioSkeletonMask mask)
        {
            return mask != null && mask.nonZeroCount > 0;
        }

        private static AnimationLayerMixerPlayable BuildLayerMixerPlayable(
            PlayableGraph graph,
            ControllerSamplingPlan controllerSampling,
            Transform runtimeRoot,
            IReadOnlyList<Transform> runtimeTransforms)
        {
            var layers = controllerSampling.LayerStates;
            var mixer = AnimationLayerMixerPlayable.Create(graph, layers.Count);
            var transformLookup = BuildTransformPathLookup(runtimeRoot, runtimeTransforms);
            var skeletonMaskLayerCount = 0;
            var appliedSkeletonMaskLayerCount = 0;
            var sourceMaskPathCount = 0;
            var resolvedMaskPathCount = 0;
            var unresolvedMaskPathCount = 0;
            var unresolvedExamples = new List<string>();
            for (var i = 0; i < layers.Count; i++)
            {
                var layer = layers[i];
                var playable = AnimationClipPlayable.Create(graph, layer.Clip);
                layer.Playable = playable;
                graph.Connect(playable, 0, mixer, i);
                mixer.SetInputWeight(i, layer.Weight);
                if (layer.IsAdditive)
                {
                    mixer.SetLayerAdditive((uint)i, true);
                }
                if (HasSkeletonMask(layer.LayerSkeletonMask))
                {
                    skeletonMaskLayerCount++;
                    var maskBuild = BuildAvatarMaskFromSkeletonMask(layer.LayerSkeletonMask, transformLookup);
                    sourceMaskPathCount += maskBuild.SourcePathCount;
                    resolvedMaskPathCount += maskBuild.ResolvedPathCount;
                    unresolvedMaskPathCount += maskBuild.UnresolvedPathCount;
                    unresolvedExamples.AddRange(maskBuild.UnresolvedExamples);
                    if (maskBuild.Mask != null && maskBuild.ResolvedPathCount > 0)
                    {
                        layer.AvatarMask = maskBuild.Mask;
                        mixer.SetLayerMaskFromAvatarMask((uint)i, maskBuild.Mask);
                        layer.MaskApplied = true;
                        appliedSkeletonMaskLayerCount++;
                    }
                }
            }

            if (controllerSampling.AdditionalLayerMaskCount > 0)
            {
                var hasUnappliedHumanOnlyMask = layers.Any(x =>
                    x.LayerIndex > 0
                    && HasHumanPoseMask(x.LayerBodyMask)
                    && !IsFullHumanPoseMask(x.LayerBodyMask)
                    && !HasSkeletonMask(x.LayerSkeletonMask));
                controllerSampling.LayerMasksApplied =
                    !hasUnappliedHumanOnlyMask
                    && skeletonMaskLayerCount == appliedSkeletonMaskLayerCount
                    && unresolvedMaskPathCount == 0;
                if (controllerSampling.LayerMasksApplied)
                {
                    var appliedMessage = $" Applied {appliedSkeletonMaskLayerCount}/{skeletonMaskLayerCount} generated layer skeleton mask(s), resolved {resolvedMaskPathCount}/{sourceMaskPathCount} masked transform path(s).";
                    controllerSampling.LayerMaskWarning = null;
                    controllerSampling.Message = AppendSentence(controllerSampling.Message, appliedMessage);
                }
                else
                {
                    var examples = unresolvedExamples.Count > 0
                        ? " Unresolved examples: " + string.Join(", ", unresolvedExamples.Take(6)) + "."
                        : string.Empty;
                    controllerSampling.LayerMaskWarning =
                        $"Detected {controllerSampling.AdditionalLayerMaskCount} additional layer mask(s) ({controllerSampling.AdditionalLayerSkeletonMaskEntryCount} skeleton-mask entries). Applied {appliedSkeletonMaskLayerCount}/{skeletonMaskLayerCount} skeleton mask layer(s), resolved {resolvedMaskPathCount}/{sourceMaskPathCount} masked transform path(s), unresolved {unresolvedMaskPathCount}.{examples} Treat this sample as needs_review until masked AnimatorController sampling is complete.";
                    controllerSampling.Message = AppendSentence(controllerSampling.Message, controllerSampling.LayerMaskWarning);
                }
            }

            return mixer;
        }

        private static void TryPromoteRecoveredControllerLayerMasks(
            ControllerSamplingPlan controllerSampling,
            Transform runtimeRoot,
            IReadOnlyList<Transform> runtimeTransforms)
        {
            if (controllerSampling == null
                || !controllerSampling.UseLayerMixer
                || controllerSampling.RecoveryMetadata == null
                || controllerSampling.Controller is not AnimatorController controller
                || controllerSampling.LayerStates == null
                || controllerSampling.LayerStates.Count == 0)
            {
                return;
            }

            var maskedLayers = controllerSampling.LayerStates
                .Where(x => x.LayerIndex > 0 && HasSkeletonMask(x.LayerSkeletonMask))
                .ToArray();
            if (maskedLayers.Length == 0)
            {
                return;
            }

            var transformLookup = BuildTransformPathLookup(runtimeRoot, runtimeTransforms);
            var controllerLayers = controller.layers;
            var appliedLayerCount = 0;
            var sourceMaskPathCount = 0;
            var resolvedMaskPathCount = 0;
            var unresolvedMaskPathCount = 0;
            var unresolvedExamples = new List<string>();

            foreach (var layer in maskedLayers)
            {
                if (layer.LayerIndex < 0 || layer.LayerIndex >= controllerLayers.Length)
                {
                    unresolvedMaskPathCount++;
                    AddUnresolvedMaskExample(unresolvedExamples, layer.LayerName);
                    continue;
                }

                var maskBuild = BuildAvatarMaskFromSkeletonMask(layer.LayerSkeletonMask, transformLookup);
                sourceMaskPathCount += maskBuild.SourcePathCount;
                resolvedMaskPathCount += maskBuild.ResolvedPathCount;
                unresolvedMaskPathCount += maskBuild.UnresolvedPathCount;
                foreach (var example in maskBuild.UnresolvedExamples)
                {
                    AddUnresolvedMaskExample(unresolvedExamples, example);
                }

                if (maskBuild.Mask == null || maskBuild.ResolvedPathCount <= 0)
                {
                    continue;
                }

                maskBuild.Mask.name = SanitizeAnimatorName(FirstNonEmpty(layer.LayerName, $"Layer{layer.LayerIndex}")) + "_RuntimeMask";
                controllerLayers[layer.LayerIndex].avatarMask = maskBuild.Mask;
                layer.AvatarMask = maskBuild.Mask;
                layer.MaskApplied = true;
                appliedLayerCount++;
            }

            if (appliedLayerCount != maskedLayers.Length || unresolvedMaskPathCount > 0)
            {
                var examples = unresolvedExamples.Count > 0
                    ? " Unresolved examples: " + string.Join(", ", unresolvedExamples.Take(6)) + "."
                    : string.Empty;
                controllerSampling.LayerMaskWarning =
                    $"Recovered controller layer masks could not be promoted to AnimatorControllerPlayable completely. Applied {appliedLayerCount}/{maskedLayers.Length} layer(s), resolved {resolvedMaskPathCount}/{sourceMaskPathCount} masked transform path(s), unresolved {unresolvedMaskPathCount}.{examples}";
                controllerSampling.Message = AppendSentence(controllerSampling.Message, controllerSampling.LayerMaskWarning);
                return;
            }

            controller.layers = controllerLayers;
            controllerSampling.UseLayerMixer = false;
            controllerSampling.LayerMasksApplied = true;
            controllerSampling.LayerMaskWarning = null;
            controllerSampling.Message = AppendSentence(
                controllerSampling.Message,
                $"Promoted {appliedLayerCount} recovered layer mask(s) onto AnimatorControllerLayer.avatarMask at bake time; sampling can use AnimatorControllerPlayable instead of AnimationLayerMixerPlayable.");
        }

        private static void AddUnresolvedMaskExample(List<string> result, string path)
        {
            if (result != null && result.Count < 8 && !string.IsNullOrWhiteSpace(path))
            {
                result.Add(path);
            }
        }

        private static LayerAvatarMaskBuildResult BuildAvatarMaskFromSkeletonMask(
            AnimeStudioSkeletonMask skeletonMask,
            TransformPathLookup transformLookup)
        {
            var result = new LayerAvatarMaskBuildResult();
            if (skeletonMask?.entries == null || transformLookup == null)
            {
                return result;
            }

            var avatarMask = new AvatarMask();
            var added = new HashSet<Transform>();
            foreach (var entry in skeletonMask.entries)
            {
                if (entry == null || entry.weight <= 0.0001f)
                {
                    continue;
                }

                result.SourcePathCount++;
                var path = NormalizeMaskPath(entry.path);
                var transform = string.IsNullOrWhiteSpace(path)
                    ? ResolveMaskTransformByHash(transformLookup, entry.pathHash)
                    : ResolveMaskTransform(transformLookup, path) ?? ResolveMaskTransformByHash(transformLookup, entry.pathHash);
                if (transform == null)
                {
                    result.UnresolvedPathCount++;
                    AddUnresolvedMaskExample(result, !string.IsNullOrWhiteSpace(path)
                        ? path
                        : entry.pathHashHex ?? entry.pathHash.ToString());
                    continue;
                }

                result.ResolvedPathCount++;
                if (added.Add(transform))
                {
                    avatarMask.AddTransformPath(transform, true);
                }
            }

            result.Mask = result.ResolvedPathCount > 0 ? avatarMask : null;
            return result;
        }

        private static TransformPathLookup BuildTransformPathLookup(Transform runtimeRoot, IReadOnlyList<Transform> runtimeTransforms)
        {
            var lookup = new TransformPathLookup();
            if (runtimeRoot == null || runtimeTransforms == null)
            {
                return lookup;
            }

            foreach (var transform in runtimeTransforms)
            {
                if (transform == null || (transform != runtimeRoot && !transform.IsChildOf(runtimeRoot)))
                {
                    continue;
                }

                AddTransformPathLookup(lookup, GetPath(runtimeRoot, transform), transform);
                AddTransformPathLookup(lookup, RelativePath(runtimeRoot, transform), transform);
            }

            return lookup;
        }

        private static void AddTransformPathLookup(TransformPathLookup lookup, string path, Transform transform)
        {
            var normalized = NormalizeMaskPath(path);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            if (!lookup.Exact.ContainsKey(normalized))
            {
                lookup.Exact.Add(normalized, transform);
            }
            lookup.Paths.Add(new TransformPathRecord { Path = normalized, Transform = transform });
            AddTransformHashLookup(lookup, normalized, transform);
            var suffix = normalized;
            var slash = suffix.IndexOf("/", StringComparison.Ordinal);
            while (slash >= 0 && slash + 1 < suffix.Length)
            {
                suffix = suffix[(slash + 1)..];
                AddTransformHashLookup(lookup, suffix, transform);
                slash = suffix.IndexOf("/", StringComparison.Ordinal);
            }
        }

        private static void AddTransformHashLookup(TransformPathLookup lookup, string path, Transform transform)
        {
            var hash = CalculateCrc32Utf8(path);
            if (!lookup.Hash.TryGetValue(hash, out var existing))
            {
                lookup.Hash.Add(hash, transform);
            }
            else if (existing != transform)
            {
                lookup.Hash[hash] = null;
            }
        }

        private static Transform ResolveMaskTransform(TransformPathLookup lookup, string sourcePath)
        {
            if (lookup.Exact.TryGetValue(sourcePath, out var exact))
            {
                return exact;
            }

            Transform matched = null;
            foreach (var candidate in lookup.Paths)
            {
                if (!candidate.Path.EndsWith("/" + sourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (matched != null && matched != candidate.Transform)
                {
                    return null;
                }
                matched = candidate.Transform;
            }

            return matched;
        }

        private static Transform ResolveMaskTransformByHash(TransformPathLookup lookup, uint pathHash)
        {
            return pathHash != 0 && lookup.Hash.TryGetValue(pathHash, out var transform)
                ? transform
                : null;
        }

        private static string NormalizeMaskPath(string path)
        {
            return string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : path.Replace('\\', '/').Trim().Trim('/');
        }

        private static void AddUnresolvedMaskExample(LayerAvatarMaskBuildResult result, string path)
        {
            if (result.UnresolvedExamples.Count < 8 && !string.IsNullOrWhiteSpace(path))
            {
                result.UnresolvedExamples.Add(path);
            }
        }

        private static bool IsFullHumanPoseMask(AnimeStudioHumanPoseMask mask)
        {
            return mask != null
                && !mask.isEmpty
                && mask.word0 == uint.MaxValue
                && mask.word1 == uint.MaxValue
                // Unity HumanPoseMask 目前只使用第三个 word 的低 25 位。
                // Endfield 的“全人形部位”常见编码是 FFFFFFFF/FFFFFFFF/01FFFFFF。
                && (mask.word2 == uint.MaxValue || mask.word2 == 0x01FFFFFFu);
        }

        private static string AppendSentence(string text, string sentence)
        {
            if (string.IsNullOrWhiteSpace(sentence))
            {
                return text;
            }
            return string.IsNullOrWhiteSpace(text) ? sentence.Trim() : text.TrimEnd() + " " + sentence.Trim();
        }

        private static uint CalculateCrc32Utf8(string text)
        {
            const uint poly = 0xEDB88320u;
            var crc = 0xFFFFFFFFu;
            var bytes = System.Text.Encoding.UTF8.GetBytes(text ?? string.Empty);
            for (var i = 0; i < bytes.Length; i++)
            {
                crc ^= bytes[i];
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
                }
            }

            return crc ^ 0xFFFFFFFFu;
        }

        private static void SamplePlayableGraph(
            PlayableGraph graph,
            AnimatorControllerPlayable controllerPlayable,
            AnimationLayerMixerPlayable layerMixerPlayable,
            ControllerSamplingPlan controllerSampling,
            AnimationClip clip,
            float time)
        {
            if (controllerSampling?.UseLayerMixer == true && layerMixerPlayable.IsValid())
            {
                foreach (var layerState in controllerSampling.LayerStates)
                {
                    if (!layerState.Playable.IsValid() || layerState.Clip == null)
                    {
                        continue;
                    }

                    layerState.Playable.SetTime(GetLayerClipTime(layerState, time));
                    layerMixerPlayable.SetInputWeight(layerState.LayerIndex, layerState.Weight);
                }

                if (controllerSampling.IkPassEnabledLayerCount > 0)
                {
                    // AnimationLayerMixerPlayable 走纯 SamplePlayableGraph 时不会稳定触发
                    // Animator IK pass。显式 IK 诊断需要先让 graph 真正 Evaluate，
                    // 再保留 OnAnimatorIK 写入的姿态，避免被 AnimationMode 覆盖。
                    graph.Evaluate(0.0001f);
                    return;
                }
            }
            else if (controllerSampling?.Controller != null && controllerPlayable.IsValid())
            {
                var normalized = clip != null && clip.length > 0f ? time / clip.length : 0f;
                var useRecoveredDefaultStates = controllerSampling.RecoveryMetadata != null
                    && !controllerSampling.UseLayerMixer;
                if (controllerSampling.LayerStates != null && controllerSampling.LayerStates.Count > 0)
                {
                    foreach (var layerState in controllerSampling.LayerStates)
                    {
                        if (!useRecoveredDefaultStates && !string.IsNullOrWhiteSpace(layerState.StateName))
                        {
                            controllerPlayable.Play(layerState.StateName, layerState.LayerIndex, normalized);
                        }
                        controllerPlayable.SetLayerWeight(layerState.LayerIndex, layerState.Weight);
                    }
                }
                else if (!useRecoveredDefaultStates && !string.IsNullOrWhiteSpace(controllerSampling.StateName))
                {
                    controllerPlayable.Play(controllerSampling.StateName, 0, normalized);
                }
                controllerPlayable.SetTime(time);
                // AnimatorControllerPlayable 的 Play/SetTime 会先写入 controller 状态，
                // 手动采样前需要 Evaluate 一次，把状态机时间推进到当前帧。
                // 默认仍用 0 delta 做确定性定点采样。只有显式 IK goal 实验打开时，
                // 才用极小正 delta 触发 IK pass/OnAnimatorIK；该路径目前只用于诊断。
                if (controllerSampling.IkPassEnabledLayerCount > 0)
                {
                    graph.Evaluate(0.0001f);
                    // IK pass 已在 Evaluate 中修改 Animator 姿态；这里不能再调用
                    // AnimationMode.SamplePlayableGraph，否则会用纯 graph 采样覆盖 IK 结果。
                    return;
                }

                graph.Evaluate(0f);
            }

            AnimationMode.SamplePlayableGraph(graph, 0, time);
        }

        private static double GetLayerClipTime(ControllerLayerState layerState, float baseTime)
        {
            var length = layerState.Clip != null ? layerState.Clip.length : 0f;
            if (length <= 0f)
            {
                return 0d;
            }

            var speed = layerState.Speed;
            var normalized = layerState.CycleOffset + (Mathf.Approximately(speed, 0f) ? 0f : baseTime * speed / length);
            normalized = normalized - Mathf.Floor(normalized);
            return normalized * length;
        }

        private static IReadOnlyList<ControllerLayerState> BuildRecoveredControllerLayerMixerStates(
            RuntimeAnimatorController controller,
            AnimeStudioBakeRequest request,
            AnimationClip baseClip,
            string baseStateName,
            AnimatorControllerRecoveryMetadata recoveryMetadata,
            out string[] skippedUntrustedLayerNames,
            out string[] skippedUntrustedLayerReasons,
            out List<AnimatorControllerSkippedLayerDiagnostic> skippedUntrustedLayerDiagnostics,
            out string[] diagnosticSampledSkippedLayerNames)
        {
            var skippedNames = new List<string>();
            var skippedReasons = new List<string>();
            var skippedDiagnostics = new List<AnimatorControllerSkippedLayerDiagnostic>();
            var diagnosticSampledNames = new List<string>();
            skippedUntrustedLayerNames = Array.Empty<string>();
            skippedUntrustedLayerReasons = Array.Empty<string>();
            skippedUntrustedLayerDiagnostics = skippedDiagnostics;
            diagnosticSampledSkippedLayerNames = Array.Empty<string>();
            if (controller is not AnimatorController animatorController)
            {
                return null;
            }

            var context = request?.animeStudioAssets?.animation?.animatorControllerContext;
            if ((context?.additionalLayerClips == null || context.additionalLayerClips.Length == 0) && baseClip == null)
            {
                return null;
            }

            var contextByLayer = (context?.additionalLayerClips ?? Array.Empty<AnimeStudioAnimatorControllerLayerClip>())
                .Where(x => x != null)
                .GroupBy(x => x.layerIndex)
                .ToDictionary(x => x.Key, x => x.First());
            var contextByLayerName = (context?.additionalLayerClips ?? Array.Empty<AnimeStudioAnimatorControllerLayerClip>())
                .Where(x => x != null)
                .Select(x => new { Key = SanitizeAnimatorName(FirstPathSegment(x.stateFullPath)), Value = x })
                .Where(x => !string.IsNullOrWhiteSpace(x.Key))
                .GroupBy(x => x.Key, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.First().Value, StringComparer.Ordinal);
            var metadataByRecoveredLayerName = (recoveryMetadata?.layers ?? Array.Empty<AnimatorControllerRecoveryLayerMetadata>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.recoveredLayerName))
                .GroupBy(x => SanitizeAnimatorName(x.recoveredLayerName), StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
            var metadataByRecoveredLayerIndex = (recoveryMetadata?.layers ?? Array.Empty<AnimatorControllerRecoveryLayerMetadata>())
                .Where(x => x != null && x.recoveredLayerIndex >= 0)
                .GroupBy(x => x.recoveredLayerIndex)
                .ToDictionary(x => x.Key, x => x.First());
            var metadataBySourceLayerIndex = (recoveryMetadata?.layers ?? Array.Empty<AnimatorControllerRecoveryLayerMetadata>())
                .Where(x => x != null)
                .GroupBy(x => x.sourceLayerIndex)
                .ToDictionary(x => x.Key, x => x.First());
            var skippedLayerWarnings = BuildSkippedAdditionalLayerWarnings(context);
            var parameterDefaults = BuildControllerFloatParameterDefaults(animatorController);
            var parameterSources = BuildControllerFloatParameterSources(recoveryMetadata);
            var result = new List<ControllerLayerState>();
            var layers = animatorController.layers ?? Array.Empty<AnimatorControllerLayer>();

            for (var recoveredLayerIndex = 0; recoveredLayerIndex < layers.Length; recoveredLayerIndex++)
            {
                var layer = layers[recoveredLayerIndex];
                var state = layer.stateMachine != null ? layer.stateMachine.defaultState : null;
                var controllerMotionClip = ResolveMotionClipForLayerMixer(state != null ? state.motion : null, parameterDefaults);
                var clip = controllerMotionClip;
                var requestedClipName = recoveredLayerIndex == 0 ? baseClip?.name : null;
                var motionSelectionRule = "controller_default_state_motion";
                var requestedClipOverridesControllerMotion = false;
                if (recoveredLayerIndex == 0 && baseClip != null && !SameAnimationClip(controllerMotionClip, baseClip))
                {
                    // base layer 是身体主动作。恢复 controller 的默认 motion 如果和请求 clip 冲突，
                    // 以显式候选里的请求 clip 为准，避免 run 这类动作被默认 idle 静默覆盖。
                    clip = baseClip;
                    requestedClipOverridesControllerMotion = controllerMotionClip != null;
                    motionSelectionRule = controllerMotionClip == null
                        ? "base_layer_request_clip_fills_missing_controller_motion"
                        : "base_layer_request_clip_overrides_recovered_controller_motion";
                }
                if (clip == null)
                {
                    continue;
                }

                var layerIndex = result.Count;
                // recovered controller 会跳过空层，因此 Unity 资产里的层序号是“恢复后序号”，
                // 不能直接当作原始 sourceLayerIndex。优先用恢复时写出的 recoveredLayerIndex/name 找回原始层。
                metadataByRecoveredLayerIndex.TryGetValue(recoveredLayerIndex, out var layerMetadata);
                if (layerMetadata == null
                    && !string.IsNullOrWhiteSpace(layer.name)
                    && metadataByRecoveredLayerName.TryGetValue(SanitizeAnimatorName(layer.name), out var namedMetadata))
                {
                    layerMetadata = namedMetadata;
                }
                if (layerMetadata == null)
                {
                    metadataBySourceLayerIndex.TryGetValue(recoveredLayerIndex, out layerMetadata);
                }
                var sourceLayerIndex = layerMetadata?.sourceLayerIndex ?? recoveredLayerIndex;

                AnimeStudioAnimatorControllerLayerClip layerContext = null;
                if (layerMetadata != null)
                {
                    contextByLayer.TryGetValue(layerMetadata.sourceLayerIndex, out layerContext);
                }
                if (layerContext == null)
                {
                    contextByLayer.TryGetValue(recoveredLayerIndex, out layerContext);
                }
                if (layerContext == null
                    && !string.IsNullOrWhiteSpace(layer.name)
                    && contextByLayerName.TryGetValue(SanitizeAnimatorName(layer.name), out var namedLayerContext))
                {
                    layerContext = namedLayerContext;
                }

                var isAdditive = layer.blendingMode == AnimatorLayerBlendingMode.Additive;
                if (recoveredLayerIndex > 0 && skippedLayerWarnings.TryGetValue(sourceLayerIndex, out var skippedLayerReason))
                {
                    var skippedName = string.IsNullOrWhiteSpace(layer.name) ? $"Layer{sourceLayerIndex}" : layer.name;
                    var diagnostic = BuildSkippedLayerDiagnostic(
                        layer,
                        state,
                        clip,
                        parameterDefaults,
                        parameterSources,
                        recoveredLayerIndex,
                        sourceLayerIndex,
                        layerMetadata,
                        layerContext,
                        skippedLayerReason);
                    skippedDiagnostics.Add(diagnostic);
                    if (request?.sampleRecoverableSkippedLayersDiagnostic == true && CanSampleRecoverableSkippedLayerDiagnostic(diagnostic))
                    {
                        diagnosticSampledNames.Add(skippedName);
                    }
                    else
                    {
                        skippedNames.Add(skippedName);
                        skippedReasons.Add($"{skippedName}: {skippedLayerReason}");
                        continue;
                    }
                }

                var diagnosticSampledSkippedLayer = diagnosticSampledNames.Contains(
                    string.IsNullOrWhiteSpace(layer.name) ? $"Layer{sourceLayerIndex}" : layer.name,
                    StringComparer.Ordinal);
                if (!diagnosticSampledSkippedLayer && recoveredLayerIndex > 0 && skippedLayerWarnings.ContainsKey(sourceLayerIndex))
                {
                    continue;
                }

                var hasDeterministicLayerContext = recoveredLayerIndex == 0 || layerContext != null || layerMetadata != null;
                if (recoveredLayerIndex > 0 && !hasDeterministicLayerContext)
                {
                    var skippedName = string.IsNullOrWhiteSpace(layer.name) ? $"Layer{recoveredLayerIndex}" : layer.name;
                    var skippedReason = isAdditive
                        ? $"{skippedName}: recovered additive layer has no metadata/context; skipped instead of assuming weight/mask semantics."
                        : $"{skippedName}: recovered override layer has no metadata/context; skipped instead of applying an unmasked full-body override.";
                    skippedNames.Add(skippedName);
                    skippedReasons.Add(skippedReason);
                    skippedDiagnostics.Add(BuildSkippedLayerDiagnostic(
                        layer,
                        state,
                        clip,
                        parameterDefaults,
                        parameterSources,
                        recoveredLayerIndex,
                        sourceLayerIndex,
                        layerMetadata,
                        layerContext,
                        skippedReason));
                    continue;
                }

                result.Add(new ControllerLayerState
                {
                    // AnimationLayerMixerPlayable 的输入槽必须连续；原始层用 layerName 和报告诊断保留语义。
                    LayerIndex = layerIndex,
                    SourceLayerIndex = sourceLayerIndex,
                    LayerName = layer.name,
                    StateName = recoveredLayerIndex == 0 ? FirstNonEmpty(baseStateName, state != null ? state.name : null) : state != null ? state.name : null,
                    ClipName = clip.name,
                    RequestedClipName = requestedClipName,
                    ControllerMotionName = controllerMotionClip != null ? controllerMotionClip.name : null,
                    RequestedClipOverridesControllerMotion = requestedClipOverridesControllerMotion,
                    MotionSelectionRule = motionSelectionRule,
                    Clip = clip,
                    SourceStateMachineIndex = layerMetadata?.sourceStateMachineIndex ?? -1,
                    StateMachineMotionSetIndex = layerMetadata?.stateMachineMotionSetIndex ?? 0,
                    LayerBinding = layerMetadata?.binding ?? 0,
                    Weight = layerIndex == 0 ? 1f : Mathf.Max(0f, layer.defaultWeight),
                    Speed = state == null || Mathf.Approximately(state.speed, 0f) ? 1f : state.speed,
                    CycleOffset = state != null ? state.cycleOffset : 0f,
                    IsAdditive = isAdditive,
                    IkPass = layerMetadata?.iKPass ?? layer.iKPass,
                    IkOnFeet = layerMetadata?.iKOnFeet ?? (state != null && state.iKOnFeet),
                    SyncedLayerAffectsTiming = layerMetadata?.syncedLayerAffectsTiming ?? false,
                    LayerBodyMask = layerContext?.layerBodyMask ?? layerMetadata?.bodyMask,
                    LayerSkeletonMask = layerContext?.layerSkeletonMask ?? layerMetadata?.skeletonMask,
                    RequestLayerContext = layerContext != null || layerMetadata != null,
                    DiagnosticSampledSkippedLayer = diagnosticSampledSkippedLayer,
                    MaskApplied = layerIndex == 0,
                });
            }

            skippedUntrustedLayerNames = skippedNames.ToArray();
            skippedUntrustedLayerReasons = skippedReasons.ToArray();
            skippedUntrustedLayerDiagnostics = skippedDiagnostics;
            diagnosticSampledSkippedLayerNames = diagnosticSampledNames.ToArray();
            return result.Count > 0 ? result : null;
        }

        private static bool CanSampleRecoverableSkippedLayerDiagnostic(AnimatorControllerSkippedLayerDiagnostic diagnostic)
        {
            return diagnostic != null
                && !string.IsNullOrWhiteSpace(diagnostic.recoverableMotionName)
                && diagnostic.hasLayerMetadata
                && diagnostic.hasSkeletonMask
                && diagnostic.skeletonMaskEntryCount > 0;
        }

        private static AnimatorControllerSkippedLayerDiagnostic BuildSkippedLayerDiagnostic(
            AnimatorControllerLayer layer,
            AnimatorState state,
            AnimationClip recoverableClip,
            IReadOnlyDictionary<string, float> parameterDefaults,
            IReadOnlyDictionary<string, string> parameterSources,
            int recoveredLayerIndex,
            int sourceLayerIndex,
            AnimatorControllerRecoveryLayerMetadata layerMetadata,
            AnimeStudioAnimatorControllerLayerClip layerContext,
            string reason)
        {
            var motion = state != null ? state.motion : null;
            var tree = motion as BlendTree;
            var selected = default(ChildMotion);
            var defaultValue = 0f;
            var defaultSource = "not_applicable";
            var selectionRule = "not_applicable";
            if (tree != null)
            {
                var parameter = tree.blendParameter;
                defaultValue = TryGetControllerFloatParameterDefault(parameterDefaults, parameter, out var value)
                    ? value
                    : 0f;
                defaultSource = ResolveControllerFloatParameterSource(parameterSources, parameter, TryGetControllerFloatParameterDefault(parameterDefaults, parameter, out _));
                selected = ResolveSimple1DSelectedChild(tree.children ?? Array.Empty<ChildMotion>(), defaultValue);
                selectionRule = tree.blendType == BlendTreeType.Simple1D
                    ? "nearest_threshold_from_recovered_default"
                    : "unsupported_complex_blendtree_diagnostic";
            }

            var bodyMask = layerContext?.layerBodyMask ?? layerMetadata?.bodyMask;
            var skeletonMask = layerContext?.layerSkeletonMask ?? layerMetadata?.skeletonMask;
            return new AnimatorControllerSkippedLayerDiagnostic
            {
                recoveredLayerIndex = recoveredLayerIndex,
                sourceLayerIndex = sourceLayerIndex,
                layerName = layer != null ? layer.name : null,
                stateName = state != null ? state.name : null,
                reason = reason,
                blendingMode = layer != null ? layer.blendingMode.ToString() : null,
                defaultWeight = layer != null ? layer.defaultWeight : 0f,
                isAdditive = layer != null && layer.blendingMode == AnimatorLayerBlendingMode.Additive,
                iKPass = layerMetadata?.iKPass ?? (layer != null && layer.iKPass),
                iKOnFeet = layerMetadata?.iKOnFeet ?? (state != null && state.iKOnFeet),
                hasLayerMetadata = layerMetadata != null,
                hasRequestLayerContext = layerContext != null,
                hasHumanPoseMask = HasHumanPoseMask(bodyMask),
                hasSkeletonMask = HasSkeletonMask(skeletonMask),
                skeletonMaskEntryCount = skeletonMask?.nonZeroCount ?? 0,
                recoverableMotionName = recoverableClip != null ? recoverableClip.name : null,
                hasBlendTree = tree != null,
                blendTreeName = tree != null ? tree.name : null,
                blendType = tree != null ? tree.blendType.ToString() : null,
                blendParameter = tree != null ? tree.blendParameter : null,
                defaultParameterValue = defaultValue,
                defaultParameterSource = defaultSource,
                selectedChildMotionName = selected.motion != null ? selected.motion.name : null,
                selectedChildThreshold = selected.motion != null ? selected.threshold : 0f,
                selectedChildSelectionRule = selectionRule,
                rule = "diagnostic_only: this layer was not sampled because its controller/runtime semantics are not trusted enough for production; fields describe what the recovered controller could still resolve.",
            };
        }

        private static Dictionary<int, string> BuildSkippedAdditionalLayerWarnings(AnimeStudioAnimatorControllerContext context)
        {
            var result = new Dictionary<int, string>();
            foreach (var warning in context?.additionalLayerContextWarnings ?? Array.Empty<AnimeStudioAnimatorControllerLayerWarning>())
            {
                if (warning == null || warning.layerIndex < 0)
                {
                    continue;
                }

                var reason = FirstNonEmpty(
                    warning.reason,
                    warning.rule,
                    "additional layer requires AnimatorController BlendTree parameters or runtime context");
                result[warning.layerIndex] = reason;
            }

            return result;
        }

        private static AnimatorControllerRecoveryMetadata LoadControllerRecoveryMetadata(string controllerPath)
        {
            var normalized = AnimeStudioGltfSkeletonBuilder.NormalizeUnityAssetPath(controllerPath);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            var metadataPath = normalized + ".animeStudioControllerRecovery.json";
            if (!File.Exists(metadataPath))
            {
                return null;
            }

            try
            {
                return JsonUtility.FromJson<AnimatorControllerRecoveryMetadata>(File.ReadAllText(metadataPath));
            }
            catch (Exception e)
            {
                Debug.LogWarning("Failed to read AnimeStudio AnimatorController recovery metadata: " + metadataPath + "\n" + e);
                return null;
            }
        }

        private static Dictionary<string, float> BuildControllerFloatParameterDefaults(AnimatorController controller)
        {
            return (controller?.parameters ?? Array.Empty<AnimatorControllerParameter>())
                .GroupBy(x => x.name, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.First().defaultFloat, StringComparer.Ordinal);
        }

        private static Dictionary<string, string> BuildControllerFloatParameterSources(AnimatorControllerRecoveryMetadata recoveryMetadata)
        {
            return (recoveryMetadata?.parameters ?? Array.Empty<AnimatorControllerRecoveryParameterMetadata>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.name))
                .GroupBy(x => x.name, StringComparer.Ordinal)
                .ToDictionary(
                    x => x.Key,
                    x => string.IsNullOrWhiteSpace(x.First().source) ? "unknown" : x.First().source,
                    StringComparer.Ordinal);
        }

        private static bool TryGetControllerFloatParameterDefault(IReadOnlyDictionary<string, float> parameterDefaults, string parameter, out float value)
        {
            value = 0f;
            return !string.IsNullOrWhiteSpace(parameter)
                && parameterDefaults != null
                && parameterDefaults.TryGetValue(parameter, out value);
        }

        private static string ResolveControllerFloatParameterSource(IReadOnlyDictionary<string, string> parameterSources, string parameter, bool hasControllerParameter)
        {
            if (string.IsNullOrWhiteSpace(parameter))
            {
                return "missing_blend_parameter";
            }
            if (parameterSources != null && parameterSources.TryGetValue(parameter, out var source) && !string.IsNullOrWhiteSpace(source))
            {
                return source;
            }
            // 统一使用 recovery request 里的来源名。旧报告曾写 controllerParameterDefault，
            // 会把同一类 controller 默认值证据拆成两个统计口径。
            return hasControllerParameter ? "controllerDefaultValue" : "missingParameterFallbackZero";
        }

        private static AnimationClip ResolveMotionClipForLayerMixer(Motion motion, IReadOnlyDictionary<string, float> parameterDefaults)
        {
            if (motion is AnimationClip clip)
            {
                return clip;
            }
            if (motion is not BlendTree tree)
            {
                return null;
            }

            var children = tree.children ?? Array.Empty<ChildMotion>();
            var parameter = tree.blendParameter;
            var defaultValue = !string.IsNullOrWhiteSpace(parameter) && parameterDefaults.TryGetValue(parameter, out var value)
                ? value
                : 0f;
            var selected = ResolveSimple1DSelectedChild(children, defaultValue);
            return ResolveMotionClipForLayerMixer(selected.motion, parameterDefaults);
        }

        private static IReadOnlyList<ControllerLayerState> BuildRecoveredControllerLayerStates(
            RuntimeAnimatorController controller,
            AnimeStudioBakeRequest request,
            string baseStateName)
        {
            if (controller is not AnimatorController animatorController)
            {
                return null;
            }

            var contextByLayer = (request?.animeStudioAssets?.animation?.animatorControllerContext?.additionalLayerClips
                    ?? Array.Empty<AnimeStudioAnimatorControllerLayerClip>())
                .Where(x => x != null)
                .GroupBy(x => x.layerIndex)
                .ToDictionary(x => x.Key, x => x.First());
            var contextByLayerName = (request?.animeStudioAssets?.animation?.animatorControllerContext?.additionalLayerClips
                    ?? Array.Empty<AnimeStudioAnimatorControllerLayerClip>())
                .Where(x => x != null)
                .Select(x => new { Key = SanitizeAnimatorName(FirstPathSegment(x.stateFullPath)), Value = x })
                .Where(x => !string.IsNullOrWhiteSpace(x.Key))
                .GroupBy(x => x.Key, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.First().Value, StringComparer.Ordinal);
            var result = new List<ControllerLayerState>();
            var layers = animatorController.layers ?? Array.Empty<AnimatorControllerLayer>();
            for (var i = 0; i < layers.Length; i++)
            {
                var layer = layers[i];
                var state = layer.stateMachine != null ? layer.stateMachine.defaultState : null;
                var motion = state != null ? state.motion : null;
                contextByLayer.TryGetValue(i, out var layerContext);
                if (layerContext == null
                    && !string.IsNullOrWhiteSpace(layer.name)
                    && contextByLayerName.TryGetValue(SanitizeAnimatorName(layer.name), out var namedLayerContext))
                {
                    // recovered controller 会跳过空层，原始 layerIndex 会压缩；用 layer 名称对齐 request 中的 mask 上下文。
                    layerContext = namedLayerContext;
                }
                result.Add(new ControllerLayerState
                {
                    LayerIndex = i,
                    SourceLayerIndex = i,
                    LayerName = layer.name,
                    StateName = i == 0 ? FirstNonEmpty(baseStateName, state != null ? state.name : null) : state != null ? state.name : null,
                    ClipName = motion != null ? motion.name : null,
                    SourceStateMachineIndex = -1,
                    Weight = i == 0 ? 1f : Mathf.Max(0f, layer.defaultWeight),
                    Speed = state == null || Mathf.Approximately(state.speed, 0f) ? 1f : state.speed,
                    CycleOffset = state != null ? state.cycleOffset : 0f,
                    IsAdditive = layer.blendingMode == AnimatorLayerBlendingMode.Additive,
                    IkPass = layer.iKPass,
                    IkOnFeet = state != null && state.iKOnFeet,
                    LayerBodyMask = layerContext?.layerBodyMask,
                    LayerSkeletonMask = layerContext?.layerSkeletonMask,
                    RequestLayerContext = layerContext != null,
                    MaskApplied = false,
                });
            }

            return result;
        }

        private static int CountRequestAdditionalLayerMasks(AnimeStudioBakeRequest request)
        {
            var layers = request?.animeStudioAssets?.animation?.animatorControllerContext?.additionalLayerClips
                ?? Array.Empty<AnimeStudioAnimatorControllerLayerClip>();
            return layers.Count(x => HasHumanPoseMask(x?.layerBodyMask) || HasSkeletonMask(x?.layerSkeletonMask));
        }

        private static int CountRequestAdditionalLayerSkeletonMaskEntries(AnimeStudioBakeRequest request)
        {
            var layers = request?.animeStudioAssets?.animation?.animatorControllerContext?.additionalLayerClips
                ?? Array.Empty<AnimeStudioAnimatorControllerLayerClip>();
            return layers.Sum(x => x?.layerSkeletonMask?.nonZeroCount ?? 0);
        }

        private static int CountLayerStateMasks(IReadOnlyList<ControllerLayerState> layers)
        {
            return (layers ?? Array.Empty<ControllerLayerState>())
                .Count(x => HasHumanPoseMask(x?.LayerBodyMask) || HasSkeletonMask(x?.LayerSkeletonMask));
        }

        private static int CountLayerStateSkeletonMaskEntries(IReadOnlyList<ControllerLayerState> layers)
        {
            return (layers ?? Array.Empty<ControllerLayerState>())
                .Sum(x => x?.LayerSkeletonMask?.nonZeroCount ?? 0);
        }

        private static List<AnimatorControllerLayerDiagnostic> BuildControllerLayerDiagnostics(ControllerSamplingPlan plan)
        {
            var result = new List<AnimatorControllerLayerDiagnostic>();
            if (plan?.LayerStates == null)
            {
                return result;
            }

            var ikPassEffective = IsIkPassEffectiveForSampling(plan);
            var ikPassSamplingMode = ResolveIkPassSamplingMode(plan);
            foreach (var layer in plan.LayerStates)
            {
                result.Add(new AnimatorControllerLayerDiagnostic
                {
                    layerIndex = layer.LayerIndex,
                    sourceLayerIndex = layer.SourceLayerIndex,
                    layerName = layer.LayerName,
                    stateName = layer.StateName,
                    motionName = layer.ClipName,
                    requestedClipName = layer.RequestedClipName,
                    controllerMotionName = layer.ControllerMotionName,
                    requestedClipOverridesControllerMotion = layer.RequestedClipOverridesControllerMotion,
                    motionSelectionRule = layer.MotionSelectionRule,
                    sourceStateMachineIndex = layer.SourceStateMachineIndex,
                    stateMachineMotionSetIndex = layer.StateMachineMotionSetIndex,
                    layerBinding = layer.LayerBinding,
                    weight = layer.Weight,
                    isAdditive = layer.IsAdditive,
                    iKPass = layer.IkPass,
                    effectiveIkPassForDiagnostic = ikPassEffective,
                    ikPassSamplingMode = ikPassSamplingMode,
                    iKOnFeet = layer.IkOnFeet,
                    syncedLayerAffectsTiming = layer.SyncedLayerAffectsTiming,
                    hasRequestLayerContext = layer.RequestLayerContext,
                    diagnosticSampledSkippedLayer = layer.DiagnosticSampledSkippedLayer,
                    hasHumanPoseMask = HasHumanPoseMask(layer.LayerBodyMask),
                    hasSkeletonMask = HasSkeletonMask(layer.LayerSkeletonMask),
                    skeletonMaskEntryCount = layer.LayerSkeletonMask?.nonZeroCount ?? 0,
                    maskApplied = layer.MaskApplied,
                    rule = "diagnostic_only: records the AnimatorController layer plan used by the Unity bake worker; it does not prove animation correctness.",
                });
            }

            return result;
        }

        private static bool IsIkPassEffectiveForSampling(ControllerSamplingPlan plan)
        {
            return plan != null
                && plan.IkPassEnabledLayerCount > 0
                && !plan.UseLayerMixer;
        }

        private static string ResolveIkPassSamplingMode(ControllerSamplingPlan plan)
        {
            if (plan == null || plan.IkPassEnabledLayerCount <= 0)
            {
                return "disabled";
            }

            return plan.UseLayerMixer
                ? "animation_layer_mixer_diagnostic_unproven"
                : "animator_controller_playable";
        }

        private static string BuildIkPassSamplingWarning(ControllerSamplingPlan plan)
        {
            if (plan == null || plan.IkPassEnabledLayerCount <= 0 || !plan.UseLayerMixer)
            {
                return null;
            }

            return "IK pass was enabled on an in-memory AnimatorController copy, but this bake sampled recovered clips through AnimationLayerMixerPlayable so layer masks could be applied. Treat OnAnimatorIK target/readback evidence as diagnostic only; it does not prove Unity Humanoid IK modified the final skeleton pose.";
        }

        private static List<AnimatorControllerParameterDiagnostic> BuildControllerParameterDiagnostics(ControllerSamplingPlan plan)
        {
            var result = new List<AnimatorControllerParameterDiagnostic>();
            if (plan?.Controller is not AnimatorController controller)
            {
                return result;
            }
            var parameterSources = BuildControllerFloatParameterSources(plan.RecoveryMetadata);

            foreach (var parameter in controller.parameters ?? Array.Empty<AnimatorControllerParameter>())
            {
                result.Add(new AnimatorControllerParameterDiagnostic
                {
                    name = parameter.name,
                    type = parameter.type.ToString(),
                    defaultFloat = parameter.defaultFloat,
                    defaultInt = parameter.defaultInt,
                    defaultBool = parameter.defaultBool,
                    defaultSource = ResolveControllerFloatParameterSource(parameterSources, parameter.name, true),
                    runtimeRoleHint = BuildControllerParameterRoleHint(parameter.name),
                    possibleRuntimeWeight = IsPossibleRuntimeWeightParameter(parameter.name),
                    possibleLayerWeight = IsPossibleLayerWeightParameter(parameter.name),
                    possibleIkWeight = IsPossibleIkWeightParameter(parameter.name),
                    possibleLookAtWeight = IsPossibleLookAtWeightParameter(parameter.name),
                    defaultNonZero = !Mathf.Approximately(parameter.defaultFloat, 0f),
                    rule = "diagnostic_only: records recovered AnimatorController parameter defaults. Runtime-updated game parameters are not proven recovered.",
                });
            }

            return result
                .OrderBy(x => x.name, StringComparer.Ordinal)
                .ToList();
        }

        private static AnimatorControllerRuntimeParameterSummary BuildControllerRuntimeParameterSummary(
            IReadOnlyList<AnimatorControllerParameterDiagnostic> parameters,
            IReadOnlyList<AnimatorControllerLayerDiagnostic> layers)
        {
            var candidates = (parameters ?? Array.Empty<AnimatorControllerParameterDiagnostic>())
                .Where(x => x != null && x.possibleRuntimeWeight)
                .OrderByDescending(x => x.defaultNonZero)
                .ThenBy(x => x.name, StringComparer.Ordinal)
                .ToArray();
            var layerHints = BuildControllerLayerRuntimeParameterHints(layers, candidates);
            return new AnimatorControllerRuntimeParameterSummary
            {
                totalParameterCount = parameters?.Count ?? 0,
                possibleRuntimeWeightCount = candidates.Length,
                possibleLayerWeightCount = candidates.Count(x => x.possibleLayerWeight),
                possibleIkWeightCount = candidates.Count(x => x.possibleIkWeight),
                possibleLookAtWeightCount = candidates.Count(x => x.possibleLookAtWeight),
                nonZeroCandidateCount = candidates.Count(x => x.defaultNonZero),
                candidateNames = candidates.Select(x => x.name).ToArray(),
                examples = candidates
                    .Take(16)
                    .Select(x => new AnimatorControllerRuntimeParameterCandidate
                    {
                        name = x.name,
                        defaultFloat = x.defaultFloat,
                        runtimeRoleHint = x.runtimeRoleHint,
                        defaultSource = x.defaultSource,
                    })
                    .ToArray(),
                layerParameterHints = layerHints,
                layerParameterHintCount = layerHints.Length,
                zeroDefaultLayerWeightHintCount = layerHints.Count(x => x.hasZeroDefaultWeightCandidate),
                rule = "diagnostic_only: parameter names suggest possible runtime layer/IK/look-at/weight gates. They are not applied to sampling until a deterministic AnimatorController parameter binding or runtime value source is recovered.",
            };
        }

        private static AnimatorControllerLayerRuntimeParameterHint[] BuildControllerLayerRuntimeParameterHints(
            IReadOnlyList<AnimatorControllerLayerDiagnostic> layers,
            IReadOnlyList<AnimatorControllerParameterDiagnostic> candidates)
        {
            if (layers == null || candidates == null || candidates.Count == 0)
            {
                return Array.Empty<AnimatorControllerLayerRuntimeParameterHint>();
            }

            var result = new List<AnimatorControllerLayerRuntimeParameterHint>();
            foreach (var layer in layers.Where(x => x != null && x.layerIndex > 0))
            {
                var matches = candidates
                    .Where(x => x.possibleRuntimeWeight && IsPossibleLayerParameterForLayer(layer.layerName, x.name))
                    .OrderBy(x => x.name, StringComparer.Ordinal)
                    .Take(8)
                    .ToArray();
                if (matches.Length == 0)
                {
                    continue;
                }

                result.Add(new AnimatorControllerLayerRuntimeParameterHint
                {
                    layerIndex = layer.layerIndex,
                    sourceLayerIndex = layer.sourceLayerIndex,
                    layerName = layer.layerName,
                    sampledLayerWeight = layer.weight,
                    candidateParameterNames = matches.Select(x => x.name).ToArray(),
                    candidates = matches
                        .Select(x => new AnimatorControllerRuntimeParameterCandidate
                        {
                            name = x.name,
                            defaultFloat = x.defaultFloat,
                            runtimeRoleHint = x.runtimeRoleHint,
                            defaultSource = x.defaultSource,
                        })
                        .ToArray(),
                    hasZeroDefaultWeightCandidate = matches.Any(x => x.possibleLayerWeight && Mathf.Approximately(x.defaultFloat, 0f)),
                    rule = "diagnostic_only: layer name and parameter name are structurally similar, so this parameter may gate runtime layer/IK/additive weight. It is not applied to sampling without a deterministic binding or runtime value source.",
                });
            }

            return result.ToArray();
        }

        private static bool IsPossibleLayerParameterForLayer(string layerName, string parameterName)
        {
            var layerKey = NormalizeAnimatorParameterKey(layerName);
            var parameterKey = NormalizeAnimatorParameterKey(parameterName);
            if (string.IsNullOrWhiteSpace(layerKey) || string.IsNullOrWhiteSpace(parameterKey))
            {
                return false;
            }

            var layerBase = StripAnimatorLayerSuffix(layerKey);
            var parameterBase = StripAnimatorLayerSuffix(parameterKey);
            return parameterBase == layerKey
                || parameterBase == layerBase
                || parameterKey == layerBase + "weight"
                || parameterKey == layerBase + "sweight"
                || parameterKey == layerBase + "ik"
                || parameterKey.StartsWith(layerBase, StringComparison.Ordinal);
        }

        private static string StripAnimatorLayerSuffix(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            foreach (var suffix in new[] { "addlayer", "layer", "add", "weight", "sweight" })
            {
                if (value.EndsWith(suffix, StringComparison.Ordinal) && value.Length > suffix.Length)
                {
                    return value.Substring(0, value.Length - suffix.Length);
                }
            }

            return value;
        }

        private static string BuildControllerParameterRoleHint(string name)
        {
            var roles = new List<string>();
            if (IsPossibleLayerWeightParameter(name))
            {
                roles.Add("layer_or_additive_weight");
            }
            if (IsPossibleIkWeightParameter(name))
            {
                roles.Add("ik_or_cloth_ik_gate");
            }
            if (IsPossibleLookAtWeightParameter(name))
            {
                roles.Add("look_at_gate");
            }
            if (ContainsParameterToken(name, "speed") || ContainsParameterToken(name, "time") || ContainsParameterToken(name, "cycle"))
            {
                roles.Add("runtime_time_or_speed");
            }
            return roles.Count == 0 ? "none" : string.Join(",", roles);
        }

        private static bool IsPossibleRuntimeWeightParameter(string name)
        {
            return IsPossibleLayerWeightParameter(name)
                || IsPossibleIkWeightParameter(name)
                || IsPossibleLookAtWeightParameter(name)
                || ContainsParameterToken(name, "weight")
                || ContainsParameterToken(name, "sweight")
                || ContainsParameterToken(name, "speed")
                || ContainsParameterToken(name, "time")
                || ContainsParameterToken(name, "cycle");
        }

        private static bool IsPossibleLayerWeightParameter(string name)
        {
            return ContainsParameterToken(name, "layer")
                || ContainsParameterToken(name, "addweight")
                || ContainsParameterToken(name, "base")
                || ContainsParameterToken(name, "sweight");
        }

        private static bool IsPossibleIkWeightParameter(string name)
        {
            return ContainsParameterToken(name, "ik");
        }

        private static bool IsPossibleLookAtWeightParameter(string name)
        {
            return ContainsParameterToken(name, "lookat")
                || ContainsParameterToken(name, "look");
        }

        private static bool ContainsParameterToken(string name, string token)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(token))
            {
                return false;
            }
            return NormalizeAnimatorParameterKey(name).IndexOf(NormalizeAnimatorParameterKey(token), StringComparison.Ordinal) >= 0;
        }

        private static string NormalizeAnimatorParameterKey(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(name.Length);
            foreach (var ch in name)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    builder.Append(char.ToLowerInvariant(ch));
                }
            }

            return builder.ToString();
        }

        private static List<AnimatorControllerBlendTreeDiagnostic> BuildControllerBlendTreeDiagnostics(ControllerSamplingPlan plan)
        {
            var result = new List<AnimatorControllerBlendTreeDiagnostic>();
            if (plan?.Controller is not AnimatorController controller)
            {
                return result;
            }

            var parameterDefaults = (controller.parameters ?? Array.Empty<AnimatorControllerParameter>())
                .GroupBy(x => x.name, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.First().defaultFloat, StringComparer.Ordinal);
            var parameterSources = BuildControllerFloatParameterSources(plan.RecoveryMetadata);
            var layers = controller.layers ?? Array.Empty<AnimatorControllerLayer>();
            for (var i = 0; i < layers.Length; i++)
            {
                var layer = layers[i];
                var state = layer.stateMachine != null ? layer.stateMachine.defaultState : null;
                if (state?.motion == null)
                {
                    continue;
                }

                AddBlendTreeDiagnostics(
                    result,
                    state.motion,
                    parameterDefaults,
                    parameterSources,
                    i,
                    layer.name,
                    state.name);
            }

            return result;
        }

        private static bool ControllerLayerMixerBypassesBlendTreeSelection(
            ControllerSamplingPlan plan,
            IReadOnlyList<AnimatorControllerBlendTreeDiagnostic> blendTreeDiagnostics)
        {
            if (plan?.UseLayerMixer != true || blendTreeDiagnostics == null || blendTreeDiagnostics.Count == 0)
            {
                return false;
            }

            var mixedClipNames = new HashSet<string>(
                (plan.LayerStates ?? Array.Empty<ControllerLayerState>())
                .Select(x => x.ClipName)
                .Where(x => !string.IsNullOrWhiteSpace(x)),
                StringComparer.Ordinal);
            var skippedLayerNames = new HashSet<string>(
                plan.SkippedUntrustedLayerNames ?? Array.Empty<string>(),
                StringComparer.Ordinal);
            return blendTreeDiagnostics.Any(x =>
                !skippedLayerNames.Contains(x.layerName ?? string.Empty) &&
                !string.IsNullOrWhiteSpace(x.selectedChildMotionName) &&
                !mixedClipNames.Contains(x.selectedChildMotionName));
        }

        private static string[] FindUnmaskedOverrideLayerNames(ControllerSamplingPlan plan)
        {
            if (plan?.UseLayerMixer != true || plan.LayerStates == null)
            {
                return Array.Empty<string>();
            }

            return plan.LayerStates
                .Where(x =>
                    x.LayerIndex > 0 &&
                    !x.IsAdditive &&
                    !HasHumanPoseMask(x.LayerBodyMask) &&
                    !HasSkeletonMask(x.LayerSkeletonMask))
                .Select(x => string.IsNullOrWhiteSpace(x.LayerName) ? $"Layer{x.LayerIndex}" : x.LayerName)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        private static void AddBlendTreeDiagnostics(
            List<AnimatorControllerBlendTreeDiagnostic> result,
            Motion motion,
            IReadOnlyDictionary<string, float> parameterDefaults,
            IReadOnlyDictionary<string, string> parameterSources,
            int layerIndex,
            string layerName,
            string stateName)
        {
            if (motion is not BlendTree tree)
            {
                return;
            }

            var children = tree.children ?? Array.Empty<ChildMotion>();
            var parameter = tree.blendParameter;
            var hasParameterDefault = TryGetControllerFloatParameterDefault(parameterDefaults, parameter, out var value);
            var defaultValue = hasParameterDefault ? value : 0f;
            var defaultSource = ResolveControllerFloatParameterSource(parameterSources, parameter, hasParameterDefault);
            var childDiagnostics = children
                .Select((child, index) => new AnimatorControllerBlendTreeChildDiagnostic
                {
                    index = index,
                    motionName = child.motion != null ? child.motion.name : null,
                    threshold = child.threshold,
                })
                .ToArray();
            var selected = ResolveSimple1DSelectedChild(children, defaultValue);
            result.Add(new AnimatorControllerBlendTreeDiagnostic
            {
                layerIndex = layerIndex,
                layerName = layerName,
                stateName = stateName,
                blendTreeName = tree.name,
                blendType = tree.blendType.ToString(),
                blendParameter = parameter,
                defaultParameterValue = defaultValue,
                defaultParameterSource = defaultSource,
                childCount = children.Length,
                selectedChildMotionName = selected.motion != null ? selected.motion.name : null,
                selectedChildThreshold = selected.motion != null ? selected.threshold : 0f,
                selectedChildSelectionRule = tree.blendType == BlendTreeType.Simple1D
                    ? "nearest_threshold_from_recovered_default"
                    : "unsupported_complex_blendtree_diagnostic",
                children = childDiagnostics,
                rule = "diagnostic_only: Simple1D selectedChild is inferred from recovered parameter default and nearest threshold. Complex BlendTrees and runtime parameter changes are not proven recovered.",
            });

            foreach (var child in children)
            {
                AddBlendTreeDiagnostics(
                    result,
                    child.motion,
                    parameterDefaults,
                    parameterSources,
                    layerIndex,
                    layerName,
                    stateName);
            }
        }

        private static ChildMotion ResolveSimple1DSelectedChild(ChildMotion[] children, float value)
        {
            var result = default(ChildMotion);
            var bestDistance = float.PositiveInfinity;
            foreach (var child in children ?? Array.Empty<ChildMotion>())
            {
                if (child.motion == null)
                {
                    continue;
                }

                var distance = Math.Abs(child.threshold - value);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    result = child;
                }
            }

            return result;
        }

        private static string ResolveAnimatorStateName(AnimeStudioBakeRequest request)
        {
            var context = request?.animeStudioAssets?.animation?.animatorControllerContext;
            return FirstNonEmpty(
                context?.stateFullPath,
                context?.statePath,
                context?.stateName,
                request?.animeStudioAssets?.animation?.name);
        }

        private static string ResolveProvidedRuntimeControllerStateName(AnimeStudioBakeRequest request, string controllerPath)
        {
            var context = request?.animeStudioAssets?.animation?.animatorControllerContext;
            if (IsRecoveredImportedAnimatorController(controllerPath))
            {
                // 当前恢复出来的 controller 第一版只重建默认层/默认状态，
                // state 是用原始 fullPath 扁平写入的；Unity 会把点号规整成下划线。
                var recoveredStateName = FirstNonEmpty(
                    context?.stateFullPath,
                    context?.statePath,
                    context?.stateName,
                    request?.animeStudioAssets?.animation?.name);
                return SanitizeAnimatorName(recoveredStateName);
            }

            return ResolveAnimatorStateName(request);
        }

        private static bool IsRecoveredImportedAnimatorController(string controllerPath)
        {
            return !string.IsNullOrWhiteSpace(controllerPath)
                && controllerPath.Replace('\\', '/').IndexOf("/ImportedAnimatorController/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ResolveGeneratedControllerStateName(AnimeStudioBakeRequest request, AnimationClip clip)
        {
            var context = request?.animeStudioAssets?.animation?.animatorControllerContext;
            return FirstNonEmpty(
                context?.stateName,
                LastPathSegment(context?.statePath),
                LastPathSegment(context?.stateFullPath),
                clip?.name,
                request?.animeStudioAssets?.animation?.name);
        }

        private static string ResolveAdditionalLayerStateName(
            AnimeStudioAnimatorControllerLayerClip additional,
            AnimationClip clip)
        {
            return FirstNonEmpty(
                additional?.stateName,
                LastPathSegment(additional?.statePath),
                LastPathSegment(additional?.stateFullPath),
                clip?.name,
                additional?.name,
                "Additional");
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        }

        private static bool SameAnimationClip(AnimationClip left, AnimationClip right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }
            if (left == null || right == null)
            {
                return false;
            }

            return string.Equals(left.name, right.name, StringComparison.Ordinal);
        }

        private static string FirstPathSegment(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var slash = path.IndexOf('/');
            var dot = path.IndexOf('.');
            var cut = slash > 0 && dot > 0 ? Math.Min(slash, dot) : Math.Max(slash, dot);
            return cut > 0 ? path.Substring(0, cut) : path;
        }

        private static string LastPathSegment(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var slash = path.LastIndexOf('/');
            var dot = path.LastIndexOf('.');
            var cut = Math.Max(slash, dot);
            return cut >= 0 && cut < path.Length - 1 ? path.Substring(cut + 1) : path;
        }

        private static string SanitizeAnimatorName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "State";
            }

            var chars = value.Select(ch => ch == '.' || ch == '/' || ch == '\\' ? '_' : ch).ToArray();
            return new string(chars);
        }

        private static string SanitizeAssetName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "AnimatorController";
            }

            var invalid = Path.GetInvalidFileNameChars();
            var chars = value.Select(ch => invalid.Contains(ch) || ch == '/' || ch == '\\' ? '_' : ch).ToArray();
            return new string(chars);
        }

        private static ImportedAvatarBindingReport BuildImportedAvatarBindingReport(
            string avatarAsset,
            Transform root,
            Transform[] transforms)
        {
            if (string.IsNullOrWhiteSpace(avatarAsset))
            {
                return new ImportedAvatarBindingReport
                {
                    status = "not_requested",
                    message = "No imported Avatar asset was supplied.",
                    avatarAsset = avatarAsset,
                    sampleMissingAvatarPaths = Array.Empty<string>(),
                    sampleExtraTransformPaths = Array.Empty<string>(),
                };
            }

            var avatarPaths = ReadImportedAvatarTosPaths(avatarAsset);
            if (avatarPaths.Count == 0)
            {
                return new ImportedAvatarBindingReport
                {
                    status = "unknown",
                    message = "Imported Avatar asset was supplied, but m_TOS paths could not be read from the Unity text asset.",
                    avatarAsset = avatarAsset,
                    sampleMissingAvatarPaths = Array.Empty<string>(),
                    sampleExtraTransformPaths = Array.Empty<string>(),
                };
            }

            var candidates = BuildTransformPathCandidates(root, transforms);
            var best = candidates
                .Select(x => new
                {
                    Normalization = x.Key,
                    Paths = x.Value,
                    Matched = avatarPaths.Count(x.Value.Contains),
                })
                .OrderByDescending(x => x.Matched)
                .ThenBy(x => x.Normalization, StringComparer.Ordinal)
                .FirstOrDefault();
            var transformPaths = best?.Paths ?? new HashSet<string>(StringComparer.Ordinal);
            var pathNormalization = best?.Normalization ?? "none";
            var matched = avatarPaths.Count(transformPaths.Contains);
            var missing = avatarPaths
                .Where(x => !transformPaths.Contains(x))
                .Take(32)
                .ToArray();
            var extra = transformPaths
                .Where(x => !avatarPaths.Contains(x))
                .Take(32)
                .ToArray();
            var ratio = avatarPaths.Count == 0 ? 0f : matched / (float)avatarPaths.Count;
            var status = ratio >= 0.95f ? "ok" : ratio >= 0.75f ? "partial" : "mismatch";
            return new ImportedAvatarBindingReport
            {
                status = status,
                message = $"Imported Avatar m_TOS path coverage against current bake target is {matched}/{avatarPaths.Count}.",
                avatarAsset = avatarAsset,
                avatarPathCount = avatarPaths.Count,
                transformPathCount = transformPaths.Count,
                matchedPathCount = matched,
                matchedPathRatio = ratio,
                pathNormalization = pathNormalization,
                sampleMissingAvatarPaths = missing,
                sampleExtraTransformPaths = extra,
            };
        }

        private static Transform SelectRuntimeRootForImportedAvatar(
            Transform root,
            ImportedAvatarBindingReport binding)
        {
            if (root == null
                || binding == null
                || !string.Equals(binding.status, "ok", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(binding.pathNormalization, "strip_first_gltf_root", StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }

            var avatarPaths = ReadImportedAvatarTosPaths(binding.avatarAsset);
            if (avatarPaths.Count == 0)
            {
                return root;
            }

            Transform best = null;
            var bestMatched = 0;
            for (var i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                var paths = new HashSet<string>(
                    child.GetComponentsInChildren<Transform>(true)
                        .Select(x => RelativePath(child, x))
                        .Where(x => !string.IsNullOrWhiteSpace(x)),
                    StringComparer.Ordinal);
                var matched = avatarPaths.Count(paths.Contains);
                if (matched > bestMatched)
                {
                    bestMatched = matched;
                    best = child;
                }
            }

            return best != null && bestMatched / (float)avatarPaths.Count >= 0.95f
                ? best
                : root;
        }

        private static Animator EnsureAnimator(Transform originalRoot, Transform runtimeRoot, Avatar explicitAvatar)
        {
            runtimeRoot ??= originalRoot;
            var animator = runtimeRoot.GetComponent<Animator>();
            if (animator == null)
            {
                animator = runtimeRoot.gameObject.AddComponent<Animator>();
            }
            animator.applyRootMotion = true;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            if (explicitAvatar != null)
            {
                // request 显式指定 Avatar asset 时，它是强约束。
                // 即使目标来自 prefab，也要用这个原始 Unity Avatar，避免静默走到别的 Avatar。
                animator.avatar = explicitAvatar;
            }
            else if (animator.avatar == null && originalRoot != null)
            {
                var existing = originalRoot.GetComponent<Animator>();
                if (existing != null)
                {
                    animator.avatar = existing.avatar;
                }
            }

            return animator;
        }

        private static Dictionary<string, HashSet<string>> BuildTransformPathCandidates(Transform root, Transform[] transforms)
        {
            var raw = new HashSet<string>(StringComparer.Ordinal);
            var stripFirst = new HashSet<string>(StringComparer.Ordinal);
            foreach (var transform in transforms.Where(x => x != null))
            {
                var path = RelativePath(root, transform);
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                raw.Add(path);
                var slash = path.IndexOf('/');
                if (slash > 0 && slash < path.Length - 1)
                {
                    stripFirst.Add(path[(slash + 1)..]);
                }
            }

            return new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
            {
                ["relative_to_bake_root"] = raw,
                ["strip_first_gltf_root"] = stripFirst,
            };
        }

        private static HashSet<string> ReadImportedAvatarTosPaths(string avatarAsset)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            var normalized = AnimeStudioGltfSkeletonBuilder.NormalizeUnityAssetPath(avatarAsset);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return result;
            }

            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, normalized));
            if (!File.Exists(fullPath))
            {
                return result;
            }

            var inTos = false;
            foreach (var rawLine in File.ReadLines(fullPath))
            {
                var line = rawLine.TrimEnd();
                if (!inTos)
                {
                    if (line.Trim() == "m_TOS:")
                    {
                        inTos = true;
                    }
                    continue;
                }

                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }
                var colon = trimmed.IndexOf(':');
                if (colon <= 0)
                {
                    break;
                }
                if (!trimmed.Take(colon).All(char.IsDigit))
                {
                    break;
                }

                var path = trimmed[(colon + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    result.Add(path.Replace('\\', '/'));
                }
            }

            return result;
        }

        private static string RelativePath(Transform root, Transform current)
        {
            var full = GetPath(root, current);
            if (string.Equals(full, root.name, StringComparison.Ordinal))
            {
                return string.Empty;
            }
            var prefix = root.name + "/";
            return full.StartsWith(prefix, StringComparison.Ordinal)
                ? full[prefix.Length..]
                : full;
        }

        private static ClipRebuildStats TryRebuildHumanoidRuntimeClipFromEditorCurves(AnimeStudioBakeRequest request, ref AnimationClip clip)
        {
            var mode = request != null && request.rebuildEditorCurveClip
                ? "request"
                : Environment.GetEnvironmentVariable("ANIMESTUDIO_UNITY_BAKE_REBUILD_EDITOR_CURVE_CLIP");
            if (string.IsNullOrWhiteSpace(mode) || mode == "0" || mode.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                return new ClipRebuildStats { mode = "disabled", message = "Editor-curve clip rebuild is disabled." };
            }
            if (clip == null)
            {
                return new ClipRebuildStats { mode = mode, attempted = true, message = "Source clip is null." };
            }

            var bindings = AnimationUtility.GetCurveBindings(clip);
            var animatorCurveCount = bindings.Count(x => x.type == typeof(Animator));
            if (animatorCurveCount == 0)
            {
                return new ClipRebuildStats
                {
                    mode = mode,
                    attempted = true,
                    curveCount = bindings.Length,
                    message = "Source clip has no Animator/Humanoid editor curves to rebuild.",
                };
            }

            var folder = "Assets/AnimeStudioBake/RebuiltHumanoidClips";
            Directory.CreateDirectory(folder);
            var path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{clip.name}_rebuilt_from_editor_curves.anim");
            var rebuilt = new AnimationClip
            {
                name = clip.name + "_rebuilt_from_editor_curves",
                frameRate = clip.frameRate > 0 ? clip.frameRate : 60f,
                legacy = false,
                wrapMode = clip.wrapMode,
            };
            AnimationUtility.SetAnimationClipSettings(rebuilt, AnimationUtility.GetAnimationClipSettings(clip));

            foreach (var binding in bindings)
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null)
                {
                    continue;
                }
                AnimationUtility.SetEditorCurve(rebuilt, binding, CloneCurve(curve));
            }

            var events = AnimationUtility.GetAnimationEvents(clip);
            if (events != null && events.Length > 0)
            {
                AnimationUtility.SetAnimationEvents(rebuilt, events);
            }

            // 实验路径：让 Unity 重新序列化一份 clip，验证 Animator editor curves
            // 能否被 Unity 导入/编译成真正可采样的 Humanoid runtime 数据。
            AssetDatabase.CreateAsset(rebuilt, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();
            var loaded = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            var stats = new ClipRebuildStats
            {
                mode = mode,
                attempted = true,
                curveCount = bindings.Length,
                assetPath = path,
                isHumanMotion = loaded != null && loaded.isHumanMotion,
            };
            if (loaded == null)
            {
                stats.message = "Unity failed to load rebuilt AnimationClip asset.";
                return stats;
            }

            if (loaded.isHumanMotion)
            {
                clip = loaded;
                stats.succeeded = true;
                stats.message = "Rebuilt editor-curve AnimationClip is humanMotion and will be sampled by PlayableGraph.";
            }
            else
            {
                stats.message = "Rebuilt editor-curve AnimationClip was created, but Unity still reports isHumanMotion=false; keeping original clip.";
            }

            return stats;
        }

        private static AnimationCurve CloneCurve(AnimationCurve curve)
        {
            return new AnimationCurve(curve.keys)
            {
                preWrapMode = curve.preWrapMode,
                postWrapMode = curve.postWrapMode,
            };
        }

        private static List<HumanoidBoneDiagnostic> CaptureHumanoidBoneDiagnostics(
            Animator animator,
            Transform runtimeRoot,
            IReadOnlyList<TransformTrack> tracks)
        {
            var result = new List<HumanoidBoneDiagnostic>();
            if (animator == null || runtimeRoot == null || animator.avatar == null || !animator.avatar.isValid || !animator.avatar.isHuman)
            {
                return result;
            }

            var byPath = (tracks ?? Array.Empty<TransformTrack>())
                .Where(x => !string.IsNullOrWhiteSpace(x.path))
                .GroupBy(x => x.path, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

            foreach (var humanBone in CoreHumanBodyBones)
            {
                var transform = animator.GetBoneTransform(humanBone);
                var path = transform != null ? GetPath(runtimeRoot, transform) : null;
                byPath.TryGetValue(path ?? string.Empty, out var track);
                var rotations = track?.rotations ?? Array.Empty<QuaternionKey>();
                var translations = track?.translations ?? Array.Empty<Vector3Key>();
                var midIndex = rotations.Length > 0 ? rotations.Length / 2 : 0;

                // 诊断只关心 Unity Humanoid 实际映射和采样姿态，不影响导出的 TRS。
                result.Add(new HumanoidBoneDiagnostic
                {
                    humanBone = humanBone.ToString(),
                    hasTransform = transform != null,
                    hasTrack = track != null,
                    path = path,
                    transformName = transform != null ? transform.name : null,
                    restTranslation = track != null ? track.restTranslation : default,
                    restRotation = track != null ? track.restRotation : default,
                    firstTranslation = translations.Length > 0 ? translations[0] : default,
                    firstRotation = rotations.Length > 0 ? rotations[0] : default,
                    midTranslation = translations.Length > midIndex ? translations[midIndex] : default,
                    midRotation = rotations.Length > midIndex ? rotations[midIndex] : default,
                    maxTranslationDeltaFromFirst = MaxTranslationDeltaFromFirst(translations),
                    maxRotationDeltaFromFirst = MaxRotationDeltaFromFirst(rotations),
                });
            }

            return result;
        }

        private static float MaxTranslationDeltaFromFirst(IReadOnlyList<Vector3Key> values)
        {
            if (values == null || values.Count == 0)
            {
                return 0f;
            }

            var first = new Vector3(values[0].x, values[0].y, values[0].z);
            var max = 0f;
            for (var i = 1; i < values.Count; i++)
            {
                var current = new Vector3(values[i].x, values[i].y, values[i].z);
                max = Mathf.Max(max, Vector3.Distance(first, current));
            }

            return max;
        }

        private static float MaxRotationDeltaFromFirst(IReadOnlyList<QuaternionKey> values)
        {
            if (values == null || values.Count == 0)
            {
                return 0f;
            }

            var first = new Quaternion(values[0].x, values[0].y, values[0].z, values[0].w);
            var max = 0f;
            for (var i = 1; i < values.Count; i++)
            {
                var current = new Quaternion(values[i].x, values[i].y, values[i].z, values[i].w);
                max = Mathf.Max(max, Quaternion.Angle(first, current));
            }

            return max;
        }

        private static readonly HumanBodyBones[] CoreHumanBodyBones =
        {
            HumanBodyBones.Hips,
            HumanBodyBones.Spine,
            HumanBodyBones.Chest,
            HumanBodyBones.UpperChest,
            HumanBodyBones.Neck,
            HumanBodyBones.Head,
            HumanBodyBones.LeftShoulder,
            HumanBodyBones.RightShoulder,
            HumanBodyBones.LeftUpperArm,
            HumanBodyBones.RightUpperArm,
            HumanBodyBones.LeftLowerArm,
            HumanBodyBones.RightLowerArm,
            HumanBodyBones.LeftHand,
            HumanBodyBones.RightHand,
            HumanBodyBones.LeftUpperLeg,
            HumanBodyBones.RightUpperLeg,
            HumanBodyBones.LeftLowerLeg,
            HumanBodyBones.RightLowerLeg,
            HumanBodyBones.LeftFoot,
            HumanBodyBones.RightFoot,
            HumanBodyBones.LeftToes,
            HumanBodyBones.RightToes,
        };

        private static GameObject CreateBakeTarget(AnimeStudioBakeRequest request, GameObject prefab)
        {
            if (prefab != null)
            {
                var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                if (instance == null)
                {
                    instance = UnityEngine.Object.Instantiate(prefab);
                }
                instance.name = prefab.name + "_AnimeStudioBake";
                return instance;
            }

            var fallback = AnimeStudioGltfSkeletonBuilder.BuildFromRequest(request);
            if (fallback != null)
            {
                fallback.name += "_AnimeStudioBake";
            }
            return fallback;
        }

        private static AnimationClipLoadResult LoadAnimationClip(AnimeStudioBakeRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.unityAssetPaths.animationClip))
            {
                var assetPath = AnimeStudioGltfSkeletonBuilder.NormalizeUnityAssetPath(request.unityAssetPaths.animationClip);
                // 显式 AnimationClip asset 是 Unity 确定性来源，不能因为当前 AssetDatabase
                // 还没刷新就静默回退到 sidecar 导入路径。
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                AssetDatabase.Refresh();
                var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
                if (existing != null)
                {
                    return new AnimationClipLoadResult
                    {
                        clip = existing,
                        assetPath = assetPath,
                        source = "unityAssetPaths.animationClip",
                    };
                }

                throw new InvalidOperationException(
                    "Request explicitly specified a Unity AnimationClip asset, but Unity could not load it as AnimationClip after import/refresh. " +
                    $"The bake must fail instead of falling back to AnimeStudio sidecar animation: {assetPath}");
            }

            var sourceAnim = request.animeStudioAssets?.animation?.anim;
            if (string.IsNullOrWhiteSpace(sourceAnim) || !File.Exists(sourceAnim))
            {
                return null;
            }

            var targetFolder = "Assets/AnimeStudioBake/Imported";
            Directory.CreateDirectory(targetFolder);
            var targetName = Path.GetFileName(sourceAnim);
            var targetPath = $"{targetFolder}/{targetName}".Replace('\\', '/');
            File.Copy(sourceAnim, targetPath, overwrite: true);
            AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();
            var imported = AssetDatabase.LoadAssetAtPath<AnimationClip>(targetPath);
            return imported == null
                ? null
                : new AnimationClipLoadResult
                {
                    clip = imported,
                    assetPath = targetPath,
                    source = "animeStudioAssets.animation.anim",
                };
        }

        private static ClipFilterStats ApplyDiagnosticClipFilter(string requestMode, ref AnimationClip clip)
        {
            var mode = requestMode;
            if (string.IsNullOrWhiteSpace(mode))
            {
                mode = Environment.GetEnvironmentVariable("ANIMESTUDIO_UNITY_BAKE_CLIP_FILTER");
            }
            if (string.IsNullOrWhiteSpace(mode))
            {
                mode = "original";
            }
            mode = mode.Trim().ToLowerInvariant();
            if (mode != "humanoid_only" && mode != "humanoid_muscles_only" && mode != "transform_only")
            {
                return new ClipFilterStats { mode = "original" };
            }

            var copy = UnityEngine.Object.Instantiate(clip);
            copy.name = clip.name + "_" + mode;
            var stats = new ClipFilterStats { mode = mode };
            foreach (var binding in AnimationUtility.GetCurveBindings(copy))
            {
                var remove = mode == "transform_only"
                    ? binding.type != typeof(Transform)
                    : binding.type == typeof(Transform) || (mode == "humanoid_muscles_only" && IsHumanoidGoalCurve(binding));
                if (!remove)
                {
                    continue;
                }

                if (binding.type == typeof(Transform))
                {
                    stats.removedTransformCurveCount++;
                }
                else if (binding.type == typeof(Animator))
                {
                    stats.removedAnimatorCurveCount++;
                }

                AnimationUtility.SetEditorCurve(copy, binding, null);
            }

            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(copy))
            {
                if (mode == "transform_only")
                {
                    AnimationUtility.SetObjectReferenceCurve(copy, binding, null);
                    stats.removedObjectReferenceCurveCount++;
                }
            }

            // 诊断用：写成临时资产并强制重新导入，确保 Unity 重新编译 Humanoid m_MuscleClip。
            // 只改 AnimeStudioBake/FilteredClips 下的派生文件，不碰导入的原始 clip。
            var folder = "Assets/AnimeStudioBake/FilteredClips";
            Directory.CreateDirectory(folder);
            var path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{clip.name}_{mode}.anim");
            AssetDatabase.CreateAsset(copy, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();
            clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path) ?? copy;
            return stats;
        }

        private static string ResolveClipFilterMode(AnimeStudioBakeRequest request)
        {
            var mode = request != null ? request.clipFilterMode : null;
            if (string.IsNullOrWhiteSpace(mode))
            {
                return null;
            }
            mode = mode.Trim().ToLowerInvariant();
            return mode == "humanoid_only" || mode == "humanoid_muscles_only" || mode == "transform_only"
                ? mode
                : null;
        }

        private static bool IsHumanoidGoalCurve(EditorCurveBinding binding)
        {
            if (binding.type != typeof(Animator) || string.IsNullOrWhiteSpace(binding.propertyName))
            {
                return false;
            }

            var property = binding.propertyName;
            return property.StartsWith("MotionT.", StringComparison.Ordinal)
                || property.StartsWith("MotionQ.", StringComparison.Ordinal)
                || property.StartsWith("RootT.", StringComparison.Ordinal)
                || property.StartsWith("RootQ.", StringComparison.Ordinal)
                || property.StartsWith("LeftFootT.", StringComparison.Ordinal)
                || property.StartsWith("LeftFootQ.", StringComparison.Ordinal)
                || property.StartsWith("RightFootT.", StringComparison.Ordinal)
                || property.StartsWith("RightFootQ.", StringComparison.Ordinal)
                || property.StartsWith("LeftHandT.", StringComparison.Ordinal)
                || property.StartsWith("LeftHandQ.", StringComparison.Ordinal)
                || property.StartsWith("RightHandT.", StringComparison.Ordinal)
                || property.StartsWith("RightHandQ.", StringComparison.Ordinal);
        }

        private sealed class ClipFilterStats
        {
            public string mode;
            public int removedTransformCurveCount;
            public int removedAnimatorCurveCount;
            public int removedObjectReferenceCurveCount;
        }

        private sealed class ClipRebuildStats
        {
            public string mode;
            public bool attempted;
            public bool succeeded;
            public bool isHumanMotion;
            public int curveCount;
            public string assetPath;
            public string message;
        }

        private sealed class AnimationClipLoadResult
        {
            public AnimationClip clip;
            public string assetPath;
            public string source;
        }

        private static AnimeStudioBakeResult Error(AnimeStudioBakeRequest request, string message)
        {
            return new AnimeStudioBakeResult
            {
                status = "error",
                message = message,
                modelPrefab = request?.unityAssetPaths?.modelPrefab,
                animationClip = request?.unityAssetPaths?.animationClip,
                requestedAnimationClip = AnimeStudioGltfSkeletonBuilder.NormalizeUnityAssetPath(request?.unityAssetPaths?.animationClip),
            };
        }

        private static float[] BuildSampleTimes(float length, int frameRate)
        {
            if (length <= 0)
            {
                return new[] { 0f };
            }
            var count = Mathf.Max(2, Mathf.CeilToInt(length * frameRate) + 1);
            var times = new float[count];
            for (var i = 0; i < count; i++)
            {
                times[i] = Mathf.Min(length, i / (float)frameRate);
            }
            times[count - 1] = length;
            return times;
        }

        private static SampleBounds ReadSampleBounds(Transform root, float time)
        {
            var renderers = root == null
                ? Array.Empty<Renderer>()
                : root.GetComponentsInChildren<Renderer>(true)
                    .Where(x => x != null && x.enabled)
                    .ToArray();
            var hasBounds = false;
            var bounds = new Bounds();
            foreach (var renderer in renderers)
            {
                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return new SampleBounds
            {
                time = time,
                hasRenderer = hasBounds,
                rendererCount = renderers.Length,
                center = new Vector3Key(time, hasBounds ? bounds.center : Vector3.zero),
                size = new Vector3Key(time, hasBounds ? bounds.size : Vector3.zero),
            };
        }

        private static List<EditorCurveTrack> SampleEditorCurves(AnimationClip clip, float[] sampleTimes)
        {
            var result = new List<EditorCurveTrack>();
            if (clip == null || sampleTimes == null)
            {
                return result;
            }

            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null)
                {
                    continue;
                }

                // 这里保留 Unity Editor 自己看到的曲线值，用来和 AnimeStudio 解码值、PlayableGraph 后的 HumanPose 做三方对照。
                result.Add(new EditorCurveTrack
                {
                    path = binding.path,
                    type = binding.type != null ? binding.type.FullName : null,
                    propertyName = binding.propertyName,
                    source = "UnityAnimationUtility",
                    values = sampleTimes
                        .Select(time => new FloatKey(time, curve.Evaluate(time)))
                        .ToArray(),
                });
            }
            return result;
        }

        private static void AddMissingMuscleCurvesFromDecodedSidecar(
            List<EditorCurveTrack> tracks,
            AnimeStudioBakeRequest request,
            float[] sampleTimes)
        {
            if (tracks == null || request?.animeStudioAssets?.animation == null || sampleTimes == null || sampleTimes.Length == 0)
            {
                return;
            }

            var path = request.animeStudioAssets.animation.animationAsset;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return;
            }

            AnimationAssetSidecar sidecar;
            try
            {
                sidecar = JsonUtility.FromJson<AnimationAssetSidecar>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AnimeStudio decoded sidecar could not be parsed for Humanoid probe: {path}; {ex.GetType().Name}: {ex.Message}");
                return;
            }

            var floats = sidecar?.decoded?.floats;
            if (floats == null || floats.Length == 0)
            {
                return;
            }

            var muscleNames = new HashSet<string>(HumanTrait.MuscleName ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var existing = new HashSet<string>(
                tracks
                    .Where(x => !string.IsNullOrWhiteSpace(x?.propertyName))
                    .Select(x => x.propertyName),
                StringComparer.OrdinalIgnoreCase);

            var added = 0;
            foreach (var curve in floats)
            {
                var attribute = curve?.attribute;
                if (string.IsNullOrWhiteSpace(attribute) || !muscleNames.Contains(attribute) || existing.Contains(attribute))
                {
                    continue;
                }
                if (curve.keyframes == null || curve.keyframes.Length == 0)
                {
                    continue;
                }

                // 有些 Unity 导入出来的 .anim 不暴露 EditorCurve，但 AnimeStudio sidecar 已经保留了
                // Unity 序列化空间的 muscle 曲线。这里仅用于 oracle 诊断，帮助更多角色进入同一套黑盒标定。
                tracks.Add(new EditorCurveTrack
                {
                    path = curve.path,
                    type = "AnimeStudio.DecodedHumanoidMuscle",
                    propertyName = attribute,
                    source = "AnimeStudioDecodedSidecar",
                    values = sampleTimes
                        .Select(time => new FloatKey(time, SampleDecodedCurve(curve.keyframes, time)))
                        .ToArray(),
                });
                existing.Add(attribute);
                added++;
            }

            if (added > 0)
            {
                Debug.Log($"AnimeStudio Humanoid probe added {added} missing muscle curve(s) from decoded sidecar: {path}");
            }
        }

        private static float SampleDecodedCurve(FloatKey[] keys, float time)
        {
            if (keys == null || keys.Length == 0)
            {
                return 0f;
            }

            var ordered = keys.OrderBy(x => x.time).ToArray();
            if (time <= ordered[0].time)
            {
                return ordered[0].value;
            }
            if (time >= ordered[^1].time)
            {
                return ordered[^1].value;
            }
            for (var i = 1; i < ordered.Length; i++)
            {
                if (time > ordered[i].time)
                {
                    continue;
                }

                var a = ordered[i - 1];
                var b = ordered[i];
                var span = b.time - a.time;
                var t = span <= 0f ? 0f : (time - a.time) / span;
                return a.value + (b.value - a.value) * t;
            }
            return ordered[^1].value;
        }

        private static List<ProbeRotationTrack> ApplyFirstEditorCurveSampleAsHumanPose(
            Transform root,
            Animator animator,
            Transform[] transforms,
            List<EditorCurveTrack> editorCurves,
            float[] sampleTimes,
            List<InternalAvatarPoseSnapshot> internalAvatarPoseSnapshots,
            List<InternalAvatarPoseTimelineSample> internalAvatarPoseTimeline,
            out List<TransformTrack> setHumanPoseTransformTracks)
        {
            var result = new List<ProbeRotationTrack>();
            setHumanPoseTransformTracks = new List<TransformTrack>();
            if (root == null || animator == null || animator.avatar == null || !animator.avatar.isValid || !animator.avatar.isHuman)
            {
                return result;
            }

            var curveValues = editorCurves?
                .Where(x => x?.values != null && x.values.Length > 0 && !string.IsNullOrWhiteSpace(x.propertyName))
                .GroupBy(x => x.propertyName)
                .ToDictionary(x => x.Key, x => x.First().values[0].value, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var curveMap = BuildEditorCurveMap(editorCurves);

            animator.Rebind();
            animator.Update(0f);

            var baseRotations = transforms.ToDictionary(t => t, t => t.localRotation);
            var handler = new HumanPoseHandler(animator.avatar, root);
            var avatarPoseHandler = CreateJointPathPoseHandler(animator.avatar, root, transforms, internalAvatarPoseSnapshots, out var avatarPoseLength, out var avatarPoseJointPaths);
            CaptureTransformLocalPoseSnapshot(root, transforms, avatarPoseJointPaths, "unityTransformLocalPoseBeforeEditorCurveSetHumanPose", internalAvatarPoseSnapshots);
            var pose = new HumanPose();
            handler.GetHumanPose(ref pose);
            if (pose.muscles == null || pose.muscles.Length == 0)
            {
                return result;
            }

            var names = HumanTrait.MuscleName;
            for (var i = 0; i < pose.muscles.Length && i < names.Length; i++)
            {
                if (curveValues.TryGetValue(names[i], out var value))
                {
                    pose.muscles[i] = value;
                }
            }
            ApplyEditorCurveBodyPose(pose, curveMap, 0f);

            CaptureInternalAvatarPose(avatarPoseHandler, pose, avatarPoseLength, avatarPoseJointPaths, "beforeEditorCurveSetHumanPose", internalAvatarPoseSnapshots);
            CaptureZeroMuscleInternalAvatarPose(avatarPoseHandler, pose, avatarPoseLength, avatarPoseJointPaths, internalAvatarPoseSnapshots);

            // 这是纯诊断：验证 AnimationCurve 的 muscle 值是否可以直接作为 HumanPose 输入。
            handler.SetHumanPose(ref pose);
            CaptureTransformLocalPoseSnapshot(root, transforms, avatarPoseJointPaths, "unityTransformLocalPoseAfterEditorCurveSetHumanPose", internalAvatarPoseSnapshots);
            CaptureInternalAvatarPose(avatarPoseHandler, pose, avatarPoseLength, avatarPoseJointPaths, "afterEditorCurveSetHumanPose", internalAvatarPoseSnapshots);
            CaptureEditorCurveInternalAvatarPoseTimeline(avatarPoseHandler, pose, avatarPoseLength, avatarPoseJointPaths, editorCurves, sampleTimes, internalAvatarPoseTimeline);
            setHumanPoseTransformTracks = CaptureEditorCurveSetHumanPoseTransformTimeline(root, animator, handler, transforms, pose, editorCurves, sampleTimes);
            foreach (var transform in transforms)
            {
                var before = baseRotations[transform];
                var after = transform.localRotation;
                if (Quaternion.Angle(before, after) <= 0.01f)
                {
                    continue;
                }

                result.Add(new ProbeRotationTrack
                {
                    path = GetPath(root, transform),
                    name = transform.name,
                    baseRotation = new QuaternionKey(0f, before),
                    rotation = new QuaternionKey(0f, after),
                });
            }

            animator.Rebind();
            animator.Update(0f);
            return result;
        }

        private static List<TransformTrack> CaptureEditorCurveSetHumanPoseTransformTimeline(
            Transform root,
            Animator animator,
            HumanPoseHandler handler,
            Transform[] transforms,
            HumanPose basePose,
            List<EditorCurveTrack> editorCurves,
            float[] sampleTimes)
        {
            var result = new List<TransformTrack>();
            if (root == null || animator == null || handler == null || transforms == null || sampleTimes == null || sampleTimes.Length == 0)
            {
                return result;
            }

            var muscleCurves = BuildEditorCurveMap(editorCurves);
            var names = HumanTrait.MuscleName;
            var recorders = transforms
                .Select(t => new TrackRecorder(root, t, sampleTimes))
                .ToArray();

            // 诊断用：直接把 decoded/editor muscle 曲线写入 HumanPoseHandler，
            // 再记录真实 Transform localRotation，用来区分 InternalAvatarPose 与实际骨骼输出。
            for (var i = 0; i < sampleTimes.Length; i++)
            {
                var time = sampleTimes[i];
                var pose = new HumanPose
                {
                    bodyPosition = basePose.bodyPosition,
                    bodyRotation = basePose.bodyRotation,
                    muscles = basePose.muscles != null ? (float[])basePose.muscles.Clone() : null,
                };
                if (pose.muscles != null)
                {
                    for (var muscleIndex = 0; muscleIndex < pose.muscles.Length && muscleIndex < names.Length; muscleIndex++)
                    {
                        if (muscleCurves.TryGetValue(names[muscleIndex], out var curve))
                        {
                            pose.muscles[muscleIndex] = SampleEditorCurve(curve, time);
                        }
                    }
                }
                ApplyEditorCurveBodyPose(pose, muscleCurves, time);

                handler.SetHumanPose(ref pose);
                foreach (var recorder in recorders)
                {
                    recorder.Record(i, time);
                }
            }

            animator.Rebind();
            animator.Update(0f);
            return recorders.Select(x => x.ToResult()).ToList();
        }

        private static List<TransformTrack> CaptureAnimationModeSampleClipTimeline(
            Transform root,
            Animator animator,
            AnimationClip clip,
            Transform[] transforms,
            float[] sampleTimes)
        {
            var result = new List<TransformTrack>();
            if (root == null || animator == null || clip == null || transforms == null || sampleTimes == null || sampleTimes.Length == 0)
            {
                return result;
            }

            animator.Rebind();
            animator.Update(0f);
            var recorders = transforms
                .Select(t => new TrackRecorder(root, t, sampleTimes))
                .ToArray();

            // 诊断用：让 Unity Editor 自己按 AnimationClip editor curves 采样 GameObject。
            // 这条路不解释 muscle/IK 曲线，只用于判断 Unity Editor 原生 SampleAnimationClip
            // 是否能比 PlayableGraph/HumanPose 诊断更接近真实 clip 语义。
            AnimationMode.StartAnimationMode();
            try
            {
                for (var i = 0; i < sampleTimes.Length; i++)
                {
                    var time = sampleTimes[i];
                    animator.Rebind();
                    animator.Update(0f);
                    AnimationMode.BeginSampling();
                    AnimationMode.SampleAnimationClip(root.gameObject, clip, time);
                    AnimationMode.EndSampling();
                    foreach (var recorder in recorders)
                    {
                        recorder.Record(i, time);
                    }
                }
            }
            finally
            {
                AnimationMode.StopAnimationMode();
                animator.Rebind();
                animator.Update(0f);
            }

            return recorders.Select(x => x.ToResult()).ToList();
        }

        private static void CaptureZeroMuscleInternalAvatarPose(
            HumanPoseHandler avatarPoseHandler,
            HumanPose basePose,
            int avatarPoseLength,
            string[] avatarPoseJointPaths,
            List<InternalAvatarPoseSnapshot> internalAvatarPoseSnapshots)
        {
            if (basePose.muscles == null || basePose.muscles.Length == 0)
            {
                return;
            }

            var zeroMuscles = new float[basePose.muscles.Length];
            // 这两个快照只用于反推 Unity 的静态 Humanoid 基准姿态：
            // 一个保留当前 body pose，一个把 body pose 归零，方便区分 root/body 与每骨骼 local offset。
            CaptureInternalAvatarPose(avatarPoseHandler, new HumanPose
            {
                bodyPosition = basePose.bodyPosition,
                bodyRotation = basePose.bodyRotation,
                muscles = zeroMuscles,
            }, avatarPoseLength, avatarPoseJointPaths, "zeroMuscleBodyPose", internalAvatarPoseSnapshots);

            CaptureInternalAvatarPose(avatarPoseHandler, new HumanPose
            {
                bodyPosition = Vector3.zero,
                bodyRotation = Quaternion.identity,
                muscles = zeroMuscles,
            }, avatarPoseLength, avatarPoseJointPaths, "zeroMuscleIdentityBodyPose", internalAvatarPoseSnapshots);
        }

        private static void CaptureEditorCurveInternalAvatarPoseTimeline(
            HumanPoseHandler handler,
            HumanPose basePose,
            int avatarPoseLength,
            string[] jointPaths,
            List<EditorCurveTrack> editorCurves,
            float[] sampleTimes,
            List<InternalAvatarPoseTimelineSample> timeline)
        {
            if (handler == null || timeline == null || avatarPoseLength <= 0 || sampleTimes == null || sampleTimes.Length == 0)
            {
                return;
            }

            var muscleCurves = BuildEditorCurveMap(editorCurves);
            var names = HumanTrait.MuscleName;

            foreach (var time in sampleTimes)
            {
                var pose = new HumanPose
                {
                    bodyPosition = basePose.bodyPosition,
                    bodyRotation = basePose.bodyRotation,
                    muscles = basePose.muscles != null ? (float[])basePose.muscles.Clone() : null,
                };
                if (pose.muscles != null)
                {
                    for (var i = 0; i < pose.muscles.Length && i < names.Length; i++)
                    {
                        if (muscleCurves.TryGetValue(names[i], out var curve))
                        {
                            pose.muscles[i] = SampleEditorCurve(curve, time);
                        }
                    }
                }
                ApplyEditorCurveBodyPose(pose, muscleCurves, time);

                CaptureInternalAvatarPoseTimelineSample(handler, pose, avatarPoseLength, jointPaths, time, timeline);
            }
        }

        private static Dictionary<string, EditorCurveTrack> BuildEditorCurveMap(List<EditorCurveTrack> editorCurves)
        {
            return editorCurves?
                .Where(x => x?.values != null && x.values.Length > 0 && !string.IsNullOrWhiteSpace(x.propertyName))
                .GroupBy(x => x.propertyName)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, EditorCurveTrack>(StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, AnimationCurve> BuildAnimationCurveMap(List<EditorCurveTrack> editorCurves)
        {
            var result = new Dictionary<string, AnimationCurve>(StringComparer.OrdinalIgnoreCase);
            foreach (var curve in editorCurves ?? Enumerable.Empty<EditorCurveTrack>())
            {
                if (curve?.values == null || curve.values.Length == 0 || string.IsNullOrWhiteSpace(curve.propertyName))
                {
                    continue;
                }
                if (result.ContainsKey(curve.propertyName))
                {
                    continue;
                }
                result[curve.propertyName] = new AnimationCurve(curve.values
                    .Select(x => new Keyframe(x.time, x.value))
                    .ToArray());
            }
            return result;
        }

        private static EditorCurveHumanPoseDiagnostic BuildEditorCurveHumanPoseDiagnostic(List<EditorCurveTrack> editorCurves, float[] sampleTimes)
        {
            var curveMap = BuildEditorCurveMap(editorCurves);
            var result = new EditorCurveHumanPoseDiagnostic
            {
                rule = "diagnostic_only: HumanTrait muscle curves are written to HumanPose.muscles; dynamic RootT/RootQ are preferred as HumanPose bodyPosition/bodyRotation when present; limb IK goal and TDOF curves are counted but not solved.",
                motionTCurveCount = CountCurvesByPrefix(curveMap, "MotionT."),
                dynamicMotionTCurveCount = CountDynamicCurvesByPrefix(curveMap, "MotionT."),
                motionQCurveCount = CountCurvesByPrefix(curveMap, "MotionQ."),
                dynamicMotionQCurveCount = CountDynamicCurvesByPrefix(curveMap, "MotionQ."),
                rootTCurveCount = CountCurvesByPrefix(curveMap, "RootT."),
                dynamicRootTCurveCount = CountDynamicCurvesByPrefix(curveMap, "RootT."),
                rootQCurveCount = CountCurvesByPrefix(curveMap, "RootQ."),
                dynamicRootQCurveCount = CountDynamicCurvesByPrefix(curveMap, "RootQ."),
                limbGoalCurveCount =
                    CountCurvesByPrefix(curveMap, "LeftFootT.") + CountCurvesByPrefix(curveMap, "LeftFootQ.")
                    + CountCurvesByPrefix(curveMap, "RightFootT.") + CountCurvesByPrefix(curveMap, "RightFootQ.")
                    + CountCurvesByPrefix(curveMap, "LeftHandT.") + CountCurvesByPrefix(curveMap, "LeftHandQ.")
                    + CountCurvesByPrefix(curveMap, "RightHandT.") + CountCurvesByPrefix(curveMap, "RightHandQ."),
                dynamicLimbGoalCurveCount =
                    CountDynamicCurvesByPrefix(curveMap, "LeftFootT.") + CountDynamicCurvesByPrefix(curveMap, "LeftFootQ.")
                    + CountDynamicCurvesByPrefix(curveMap, "RightFootT.") + CountDynamicCurvesByPrefix(curveMap, "RightFootQ.")
                    + CountDynamicCurvesByPrefix(curveMap, "LeftHandT.") + CountDynamicCurvesByPrefix(curveMap, "LeftHandQ.")
                    + CountDynamicCurvesByPrefix(curveMap, "RightHandT.") + CountDynamicCurvesByPrefix(curveMap, "RightHandQ."),
                tdofCurveCount = curveMap.Keys.Count(x => x.StartsWith("TDOF.", StringComparison.OrdinalIgnoreCase) || x.IndexOf("TDOF", StringComparison.OrdinalIgnoreCase) >= 0),
                dynamicTdofCurveCount = curveMap
                    .Where(x => x.Key.StartsWith("TDOF.", StringComparison.OrdinalIgnoreCase) || x.Key.IndexOf("TDOF", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Count(x => IsDynamicCurve(x.Value)),
            };
            result.tdofVector3Summaries = BuildTdofVector3Summaries(curveMap, sampleTimes);

            var muscleNames = new HashSet<string>(HumanTrait.MuscleName ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var muscleCurves = curveMap.Where(x => muscleNames.Contains(x.Key)).ToArray();
            result.muscleCurveCount = muscleCurves.Length;
            result.dynamicMuscleCurveCount = muscleCurves.Count(x => IsDynamicCurve(x.Value));
            result.bodyPositionCurveSource = SelectBodyCurveSource(curveMap, "RootT.", "MotionT.", "x", "y", "z");
            result.bodyRotationCurveSource = SelectBodyCurveSource(curveMap, "RootQ.", "MotionQ.", "x", "y", "z", "w");
            result.bodyPositionApplied = !string.IsNullOrWhiteSpace(result.bodyPositionCurveSource);
            result.bodyRotationApplied = !string.IsNullOrWhiteSpace(result.bodyRotationCurveSource);
            if (result.dynamicLimbGoalCurveCount > 0)
            {
                result.warning = "Detected dynamic limb IK goal curves. HumanPose.SetHumanPose does not consume these goal curves here, so this output is diagnostic and cannot be production-accepted by visual motion alone.";
            }
            return result;
        }

        private static List<EditorCurveVector3Summary> BuildTdofVector3Summaries(Dictionary<string, EditorCurveTrack> curveMap, float[] sampleTimes)
        {
            var groups = new Dictionary<string, List<KeyValuePair<string, EditorCurveTrack>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in curveMap ?? new Dictionary<string, EditorCurveTrack>())
            {
                if (string.IsNullOrWhiteSpace(item.Key)
                    || item.Key.IndexOf("TDOF", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                var dot = item.Key.LastIndexOf('.');
                if (dot <= 0 || dot >= item.Key.Length - 1)
                {
                    continue;
                }
                var component = item.Key.Substring(dot + 1);
                if (!string.Equals(component, "x", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(component, "y", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(component, "z", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var prefix = item.Key.Substring(0, dot);
                if (!groups.TryGetValue(prefix, out var list))
                {
                    list = new List<KeyValuePair<string, EditorCurveTrack>>();
                    groups[prefix] = list;
                }
                list.Add(item);
            }

            var times = sampleTimes != null && sampleTimes.Length > 0
                ? sampleTimes
                : new[] { 0f };
            var result = new List<EditorCurveVector3Summary>();
            foreach (var group in groups.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                var axes = group.Value.ToDictionary(x => x.Key.Substring(x.Key.LastIndexOf('.') + 1), x => x.Value, StringComparer.OrdinalIgnoreCase);
                var summary = new EditorCurveVector3Summary
                {
                    prefix = group.Key,
                    properties = group.Value
                        .Select(x => x.Key)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    hasX = axes.ContainsKey("x"),
                    hasY = axes.ContainsKey("y"),
                    hasZ = axes.ContainsKey("z"),
                    curveCount = group.Value.Count,
                    dynamicCurveCount = group.Value.Count(x => IsDynamicCurve(x.Value)),
                    minLength = float.MaxValue,
                    maxLength = float.MinValue,
                };
                summary.hasAllAxes = summary.hasX && summary.hasY && summary.hasZ;
                Vector3 first = default;
                for (var i = 0; i < times.Length; i++)
                {
                    var value = new Vector3(
                        axes.TryGetValue("x", out var x) ? SampleEditorCurve(x, times[i]) : 0f,
                        axes.TryGetValue("y", out var y) ? SampleEditorCurve(y, times[i]) : 0f,
                        axes.TryGetValue("z", out var z) ? SampleEditorCurve(z, times[i]) : 0f);
                    if (i == 0)
                    {
                        first = value;
                        summary.firstValue = ToVector3Value(value);
                    }
                    if (i == times.Length / 2)
                    {
                        summary.midValue = ToVector3Value(value);
                    }
                    if (i == times.Length - 1)
                    {
                        summary.lastValue = ToVector3Value(value);
                    }
                    summary.sampleCount++;
                    summary.valueRange.Include(value);
                    var length = value.magnitude;
                    summary.minLength = Mathf.Min(summary.minLength, length);
                    summary.maxLength = Mathf.Max(summary.maxLength, length);
                    summary.maxDeltaFromFirst = Mathf.Max(summary.maxDeltaFromFirst, (value - first).magnitude);
                }
                summary.valueRangeSpan = CalculateVector3RangeSpan(summary.valueRange);
                summary.dynamicVector = summary.maxDeltaFromFirst > PositionEpsilon || summary.dynamicCurveCount > 0;
                result.Add(summary);
            }
            return result;
        }

        private static Vector3Value ToVector3Value(Vector3 value)
        {
            return new Vector3Value { x = value.x, y = value.y, z = value.z };
        }

        private static float CalculateVector3RangeSpan(Vector3Range range)
        {
            if (range == null || range.minX == float.MaxValue)
            {
                return 0f;
            }
            var dx = range.maxX - range.minX;
            var dy = range.maxY - range.minY;
            var dz = range.maxZ - range.minZ;
            return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static string SelectBodyCurveSource(Dictionary<string, EditorCurveTrack> curveMap, string preferredPrefix, string fallbackPrefix, params string[] components)
        {
            if (HasCurveComponents(curveMap, preferredPrefix, components) && CountDynamicCurvesByPrefix(curveMap, preferredPrefix) > 0)
            {
                return preferredPrefix.TrimEnd('.');
            }
            return HasCurveComponents(curveMap, fallbackPrefix, components)
                ? fallbackPrefix.TrimEnd('.')
                : null;
        }

        private static bool HasCurveComponents(Dictionary<string, EditorCurveTrack> curveMap, string prefix, params string[] components)
        {
            return curveMap != null
                && components != null
                && components.All(component => curveMap.ContainsKey(prefix + component));
        }

        private static int CountCurvesByPrefix(Dictionary<string, EditorCurveTrack> curveMap, string prefix)
        {
            return curveMap?.Keys.Count(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ?? 0;
        }

        private static int CountDynamicCurvesByPrefix(Dictionary<string, EditorCurveTrack> curveMap, string prefix)
        {
            return curveMap?
                .Where(x => x.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Count(x => IsDynamicCurve(x.Value)) ?? 0;
        }

        private static bool IsDynamicCurve(EditorCurveTrack curve)
        {
            if (curve?.values == null || curve.values.Length < 2)
            {
                return false;
            }
            var min = curve.values[0].value;
            var max = min;
            foreach (var key in curve.values)
            {
                min = Mathf.Min(min, key.value);
                max = Mathf.Max(max, key.value);
            }
            return max - min > 0.00001f;
        }

        private static void ApplyEditorCurveBodyPose(HumanPose pose, Dictionary<string, EditorCurveTrack> curveMap, float time)
        {
            if (curveMap == null)
            {
                return;
            }
            if (TrySampleVector3Curve(curveMap, "RootT.", time, out var rootPosition)
                || TrySampleVector3Curve(curveMap, "MotionT.", time, out rootPosition))
            {
                pose.bodyPosition = rootPosition;
            }
            if (TrySampleQuaternionCurve(curveMap, "RootQ.", time, out var rootRotation)
                || TrySampleQuaternionCurve(curveMap, "MotionQ.", time, out rootRotation))
            {
                pose.bodyRotation = rootRotation;
            }
        }

        private static bool TrySampleVector3Curve(Dictionary<string, EditorCurveTrack> curveMap, string prefix, float time, out Vector3 value)
        {
            value = default;
            if (!curveMap.TryGetValue(prefix + "x", out var x)
                || !curveMap.TryGetValue(prefix + "y", out var y)
                || !curveMap.TryGetValue(prefix + "z", out var z))
            {
                return false;
            }
            value = new Vector3(
                SampleEditorCurve(x, time),
                SampleEditorCurve(y, time),
                SampleEditorCurve(z, time));
            return true;
        }

        private static bool TrySampleQuaternionCurve(Dictionary<string, EditorCurveTrack> curveMap, string prefix, float time, out Quaternion value)
        {
            value = default;
            if (!curveMap.TryGetValue(prefix + "x", out var x)
                || !curveMap.TryGetValue(prefix + "y", out var y)
                || !curveMap.TryGetValue(prefix + "z", out var z)
                || !curveMap.TryGetValue(prefix + "w", out var w))
            {
                return false;
            }
            value = new Quaternion(
                SampleEditorCurve(x, time),
                SampleEditorCurve(y, time),
                SampleEditorCurve(z, time),
                SampleEditorCurve(w, time));
            var length = Mathf.Sqrt(value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w);
            if (length <= 0.00001f)
            {
                value = Quaternion.identity;
            }
            else
            {
                value = new Quaternion(value.x / length, value.y / length, value.z / length, value.w / length);
            }
            return true;
        }

        private static float SampleEditorCurve(EditorCurveTrack curve, float time)
        {
            if (curve?.values == null || curve.values.Length == 0)
            {
                return 0f;
            }
            var keys = curve.values;
            if (time <= keys[0].time)
            {
                return keys[0].value;
            }
            if (time >= keys[^1].time)
            {
                return keys[^1].value;
            }
            for (var i = 1; i < keys.Length; i++)
            {
                if (time > keys[i].time)
                {
                    continue;
                }
                var a = keys[i - 1];
                var b = keys[i];
                var span = b.time - a.time;
                var t = span <= 0f ? 0f : (time - a.time) / span;
                return a.value + (b.value - a.value) * t;
            }
            return keys[^1].value;
        }

        private static float[] BuildMuscleProbeValues(string muscleName, List<EditorCurveTrack> editorCurves)
        {
            var values = new List<float>();
            foreach (var value in new[] { -2f, -1.5f, -1f, -0.5f, 0f, 0.5f, 1f, 1.5f, 2f })
            {
                AddProbeValue(values, value);
            }

            foreach (var curve in editorCurves ?? Enumerable.Empty<EditorCurveTrack>())
            {
                if (!string.Equals(curve?.propertyName, muscleName, StringComparison.OrdinalIgnoreCase) ||
                    curve.values == null ||
                    curve.values.Length == 0)
                {
                    continue;
                }

                var minValue = curve.values.Min(x => x.value);
                var maxValue = curve.values.Max(x => x.value);
                AddProbeValue(values, minValue);
                AddProbeValue(values, maxValue);
                AddProbeValue(values, Mathf.Floor(minValue * 2f) / 2f);
                AddProbeValue(values, Mathf.Ceil(maxValue * 2f) / 2f);
            }

            return values.OrderBy(x => x).ToArray();
        }

        private static void AddProbeValue(List<float> values, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return;
            }

            if (values.Any(x => Mathf.Abs(x - value) <= 0.0001f))
            {
                return;
            }

            values.Add(value);
        }

        private static HumanPoseHandler CreateJointPathPoseHandler(
            Avatar avatar,
            Transform root,
            Transform[] transforms,
            List<InternalAvatarPoseSnapshot> snapshots,
            out int avatarPoseLength,
            out string[] jointPaths)
        {
            avatarPoseLength = 0;
            jointPaths = Array.Empty<string>();
            try
            {
                var rootPath = GetPath(root, root);
                jointPaths = transforms
                    .Select(x => GetPath(root, x))
                    .Select(x => x == rootPath ? string.Empty : x.StartsWith(rootPath + "/", StringComparison.Ordinal) ? x[(rootPath.Length + 1)..] : x)
                    .ToArray();
                avatarPoseLength = jointPaths.Length * 7;
                return new HumanPoseHandler(avatar, jointPaths);
            }
            catch (Exception ex)
            {
                snapshots?.Add(new InternalAvatarPoseSnapshot
                {
                    label = "createJointPathPoseHandler",
                    status = "error",
                    error = ex.GetType().Name + ": " + ex.Message,
                    values = Array.Empty<float>(),
                });
                return null;
            }
        }

        private static void CaptureInternalAvatarPose(
            HumanPoseHandler handler,
            HumanPose pose,
            int avatarPoseLength,
            string[] jointPaths,
            string label,
            List<InternalAvatarPoseSnapshot> snapshots)
        {
            if (handler == null || snapshots == null || avatarPoseLength <= 0)
            {
                return;
            }

            // jointPaths 构造的 InternalAvatarPose 是每个 joint 7 个 float：T.xyz + Q.xyzw。
            // 这正好对应后续离线 solver 需要写入 glTF 节点的本地 TRS。
            var array = new NativeArray<float>(avatarPoseLength, Allocator.Temp, NativeArrayOptions.ClearMemory);
            try
            {
                var localPose = new HumanPose
                {
                    bodyPosition = pose.bodyPosition,
                    bodyRotation = pose.bodyRotation,
                    muscles = pose.muscles != null ? (float[])pose.muscles.Clone() : null,
                };
                // jointPaths 版 HumanPoseHandler 不绑定 Transform 根，只能写 internal pose；
                // 用 SetHumanPose 会触发 Unity “未绑定 avatar skeleton root”的警告。
                handler.SetInternalHumanPose(ref localPose);
                handler.GetInternalAvatarPose(array);
                var values = array.ToArray();
                snapshots.Add(new InternalAvatarPoseSnapshot
                {
                    label = label,
                    status = "ok",
                    requestedLength = avatarPoseLength,
                    valueCount = avatarPoseLength,
                    nonZeroCount = values.Count(x => Math.Abs(x) > 0.000001f),
                    jointPaths = jointPaths ?? Array.Empty<string>(),
                    values = values,
                });
            }
            catch (Exception ex)
            {
                snapshots.Add(new InternalAvatarPoseSnapshot
                {
                    label = label,
                    status = "error",
                    requestedLength = avatarPoseLength,
                    valueCount = 0,
                    nonZeroCount = 0,
                    error = ex.GetType().Name + ": " + ex.Message,
                    jointPaths = jointPaths ?? Array.Empty<string>(),
                    values = Array.Empty<float>(),
                });
            }
            finally
            {
                if (array.IsCreated)
                {
                    array.Dispose();
                }
            }
        }

        private static void CaptureTransformLocalPoseSnapshot(
            Transform root,
            Transform[] transforms,
            string[] jointPaths,
            string label,
            List<InternalAvatarPoseSnapshot> snapshots)
        {
            if (root == null || transforms == null || snapshots == null)
            {
                return;
            }

            // 诊断用：用和 InternalAvatarPose 一样的 T.xyz + Q.xyzw 布局记录 Unity Transform.local TR。
            // 这样离线 compare 可以直接判断问题来自 HumanPoseHandler 内部空间，还是来自普通 Transform 本地姿态。
            var values = new float[transforms.Length * 7];
            for (var i = 0; i < transforms.Length; i++)
            {
                var t = transforms[i];
                if (t == null)
                {
                    continue;
                }

                var offset = i * 7;
                var p = t.localPosition;
                var q = t.localRotation;
                values[offset + 0] = p.x;
                values[offset + 1] = p.y;
                values[offset + 2] = p.z;
                values[offset + 3] = q.x;
                values[offset + 4] = q.y;
                values[offset + 5] = q.z;
                values[offset + 6] = q.w;
            }

            snapshots.Add(new InternalAvatarPoseSnapshot
            {
                label = label,
                status = "ok",
                requestedLength = values.Length,
                valueCount = values.Length,
                nonZeroCount = values.Count(x => Math.Abs(x) > 0.000001f),
                jointPaths = jointPaths ?? transforms.Select(x => x == null ? string.Empty : GetPath(root, x)).ToArray(),
                values = values,
            });
        }

        private static void CaptureInternalAvatarPoseTimelineSample(
            HumanPoseHandler handler,
            HumanPose pose,
            int avatarPoseLength,
            string[] jointPaths,
            float time,
            List<InternalAvatarPoseTimelineSample> timeline)
        {
            if (handler == null || timeline == null || avatarPoseLength <= 0)
            {
                return;
            }

            // 多时间点 oracle 只用于诊断离线 Humanoid solver，不参与正式素材导出。
            var array = new NativeArray<float>(avatarPoseLength, Allocator.Temp, NativeArrayOptions.ClearMemory);
            try
            {
                var localPose = new HumanPose
                {
                    bodyPosition = pose.bodyPosition,
                    bodyRotation = pose.bodyRotation,
                    muscles = pose.muscles != null ? (float[])pose.muscles.Clone() : null,
                };
                // jointPaths 版 HumanPoseHandler 不绑定 Transform 根，只能写 internal pose。
                handler.SetInternalHumanPose(ref localPose);
                handler.GetInternalAvatarPose(array);
                var values = array.ToArray();
                timeline.Add(new InternalAvatarPoseTimelineSample
                {
                    time = time,
                    status = "ok",
                    requestedLength = avatarPoseLength,
                    valueCount = avatarPoseLength,
                    nonZeroCount = values.Count(x => Math.Abs(x) > 0.000001f),
                    jointPaths = jointPaths ?? Array.Empty<string>(),
                    values = values,
                });
            }
            catch (Exception ex)
            {
                timeline.Add(new InternalAvatarPoseTimelineSample
                {
                    time = time,
                    status = "error",
                    requestedLength = avatarPoseLength,
                    valueCount = 0,
                    nonZeroCount = 0,
                    error = ex.GetType().Name + ": " + ex.Message,
                    jointPaths = jointPaths ?? Array.Empty<string>(),
                    values = Array.Empty<float>(),
                });
            }
            finally
            {
                if (array.IsCreated)
                {
                    array.Dispose();
                }
            }
        }

        private static string GetPath(Transform root, Transform current)
        {
            if (root == current)
            {
                return root.name;
            }
            var names = new Stack<string>();
            var t = current;
            while (t != null)
            {
                names.Push(t.name);
                if (t == root)
                {
                    break;
                }
                t = t.parent;
            }
            return string.Join("/", names.ToArray());
        }

        private static List<MuscleProbe> ProbeMuscles(
            Transform root,
            Animator animator,
            Transform[] transforms,
            List<EditorCurveTrack> editorCurves,
            List<InternalAvatarPoseSnapshot> snapshots,
            out List<MuscleProbe> internalAvatarPoseProbes)
        {
            var result = new List<MuscleProbe>();
            internalAvatarPoseProbes = new List<MuscleProbe>();
            if (animator == null || animator.avatar == null || !animator.avatar.isValid || !animator.avatar.isHuman)
            {
                return result;
            }

            // Probe 要测 Avatar 静态求解关系，不能沿用前面 bake 循环留下的最后一帧动画姿态。
            animator.Rebind();
            animator.Update(0f);

            var handler = new HumanPoseHandler(animator.avatar, root);
            var avatarPoseHandler = CreateJointPathPoseHandler(animator.avatar, root, transforms, snapshots, out var avatarPoseLength, out var avatarPoseJointPaths);
            var basePose = new HumanPose();
            handler.GetHumanPose(ref basePose);
            if (basePose.muscles == null || basePose.muscles.Length == 0)
            {
                return result;
            }

            // Unity 写回 HumanPose 时会做 Avatar 规范化；基准也必须用写回后的姿态，否则每个 muscle 都会混进一批假变化。
            handler.SetHumanPose(ref basePose);
            var baseRotations = transforms.ToDictionary(t => t, t => t.localRotation);
            var baseTranslations = transforms.ToDictionary(t => t, t => t.localPosition);
            var baseInternalAvatarPose = ReadInternalAvatarPoseValues(avatarPoseHandler, basePose, avatarPoseLength);
            for (var muscleIndex = 0; muscleIndex < basePose.muscles.Length; muscleIndex++)
            {
                var muscleName = muscleIndex >= 0 && muscleIndex < HumanTrait.MuscleName.Length
                    ? HumanTrait.MuscleName[muscleIndex]
                    : $"muscle_{muscleIndex}";
                // 真实 Humanoid 曲线可能超过 [-1, 1]。probeMuscles 只服务 Unity oracle 诊断，
                // 这里叠加当前动画的实际极值，避免离线查表被边界夹住后误判为公式问题。
                var probeValues = BuildMuscleProbeValues(muscleName, editorCurves);
                foreach (var value in probeValues)
                {
                    handler.SetHumanPose(ref basePose);
                    var pose = new HumanPose
                    {
                        bodyPosition = basePose.bodyPosition,
                        bodyRotation = basePose.bodyRotation,
                        muscles = (float[])basePose.muscles.Clone(),
                    };
                    pose.muscles[muscleIndex] = value;
                    handler.SetHumanPose(ref pose);

                    var probe = new MuscleProbe
                    {
                        muscleIndex = muscleIndex,
                        muscleName = muscleName,
                        baseValue = basePose.muscles[muscleIndex],
                        value = value,
                    };
                    var internalProbe = new MuscleProbe
                    {
                        muscleIndex = muscleIndex,
                        muscleName = probe.muscleName,
                        baseValue = probe.baseValue,
                        value = probe.value,
                    };

                    foreach (var transform in transforms)
                    {
                        var rotation = transform.localRotation;
                        if (Quaternion.Dot(rotation, baseRotations[transform]) >= 1f - 0.00001f)
                        {
                            continue;
                        }

                        probe.rotations.Add(new ProbeRotationTrack
                        {
                            path = GetPath(root, transform),
                            name = transform.name,
                            baseRotation = new QuaternionKey(0f, baseRotations[transform]),
                            rotation = new QuaternionKey(0f, rotation),
                        });
                    }

                    AddInternalAvatarPoseProbeRotations(internalProbe, avatarPoseJointPaths, baseInternalAvatarPose, ReadInternalAvatarPoseValues(avatarPoseHandler, pose, avatarPoseLength));
                    internalProbe.changedTrackCount = internalProbe.rotations.Count;
                    if (internalProbe.changedTrackCount > 0)
                    {
                        internalAvatarPoseProbes.Add(internalProbe);
                    }

                    probe.changedTrackCount = probe.rotations.Count;
                    if (probe.changedTrackCount > 0)
                    {
                        result.Add(probe);
                    }
                }
            }

            handler.SetHumanPose(ref basePose);
            return result;
        }

        private static List<MuscleCombinationProbe> ProbeMuscleCombinations(
            Transform root,
            Animator animator,
            Transform[] transforms,
            List<InternalAvatarPoseSnapshot> snapshots,
            out List<MuscleCombinationProbe> internalAvatarPoseProbes)
        {
            var result = new List<MuscleCombinationProbe>();
            internalAvatarPoseProbes = new List<MuscleCombinationProbe>();
            if (animator == null || animator.avatar == null || !animator.avatar.isValid || !animator.avatar.isHuman)
            {
                return result;
            }

            animator.Rebind();
            animator.Update(0f);

            var handler = new HumanPoseHandler(animator.avatar, root);
            var avatarPoseHandler = CreateJointPathPoseHandler(animator.avatar, root, transforms, snapshots, out var avatarPoseLength, out var avatarPoseJointPaths);
            var basePose = new HumanPose();
            handler.GetHumanPose(ref basePose);
            if (basePose.muscles == null || basePose.muscles.Length == 0)
            {
                return result;
            }

            handler.SetHumanPose(ref basePose);
            var baseRotations = transforms.ToDictionary(t => t, t => t.localRotation);
            var baseTranslations = transforms.ToDictionary(t => t, t => t.localPosition);
            var baseInternalAvatarPose = ReadInternalAvatarPoseValues(avatarPoseHandler, basePose, avatarPoseLength);
            var muscleIndexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < HumanTrait.MuscleName.Length; i++)
            {
                muscleIndexByName[HumanTrait.MuscleName[i]] = i;
            }

            foreach (var spec in BuildMuscleCombinationSpecs())
            {
                var values = new List<MuscleProbeValue>();
                var missing = false;
                foreach (var item in spec.Muscles)
                {
                    if (!muscleIndexByName.TryGetValue(item.MuscleName, out var muscleIndex) ||
                        muscleIndex < 0 ||
                        muscleIndex >= basePose.muscles.Length)
                    {
                        missing = true;
                        break;
                    }

                    values.Add(new MuscleProbeValue
                    {
                        muscleIndex = muscleIndex,
                        muscleName = item.MuscleName,
                        baseValue = basePose.muscles[muscleIndex],
                        value = item.Value,
                    });
                }

                if (missing || values.Count == 0)
                {
                    continue;
                }

                handler.SetHumanPose(ref basePose);
                var pose = new HumanPose
                {
                    bodyPosition = basePose.bodyPosition,
                    bodyRotation = basePose.bodyRotation,
                    muscles = (float[])basePose.muscles.Clone(),
                };
                foreach (var item in values)
                {
                    pose.muscles[item.muscleIndex] = item.value;
                }
                handler.SetHumanPose(ref pose);

                var probe = new MuscleCombinationProbe
                {
                    probeName = spec.Name,
                    muscles = values.ToArray(),
                };
                var internalProbe = new MuscleCombinationProbe
                {
                    probeName = spec.Name,
                    muscles = values.ToArray(),
                };

                foreach (var transform in transforms)
                {
                    var rotation = transform.localRotation;
                    if (Quaternion.Dot(rotation, baseRotations[transform]) < 1f - 0.00001f)
                    {
                        probe.rotations.Add(new ProbeRotationTrack
                        {
                            path = GetPath(root, transform),
                            name = transform.name,
                            baseRotation = new QuaternionKey(0f, baseRotations[transform]),
                            rotation = new QuaternionKey(0f, rotation),
                        });
                    }

                    var translation = transform.localPosition;
                    if ((translation - baseTranslations[transform]).sqrMagnitude <= PositionEpsilon)
                    {
                        continue;
                    }

                    probe.translations.Add(new ProbeTranslationTrack
                    {
                        path = GetPath(root, transform),
                        name = transform.name,
                        baseTranslation = new Vector3Key(0f, baseTranslations[transform]),
                        translation = new Vector3Key(0f, translation),
                    });
                }

                AddInternalAvatarPoseCombinationProbeTracks(internalProbe, avatarPoseJointPaths, baseInternalAvatarPose, ReadInternalAvatarPoseValues(avatarPoseHandler, pose, avatarPoseLength));
                internalProbe.changedTrackCount = internalProbe.rotations.Count + internalProbe.translations.Count;
                if (internalProbe.changedTrackCount > 0)
                {
                    internalAvatarPoseProbes.Add(internalProbe);
                }

                probe.changedTrackCount = probe.rotations.Count + probe.translations.Count;
                if (probe.changedTrackCount > 0)
                {
                    result.Add(probe);
                }
            }

            handler.SetHumanPose(ref basePose);
            return result;
        }

        private static MuscleCombinationSpec[] BuildMuscleCombinationSpecs()
        {
            var specs = new List<MuscleCombinationSpec>();
            foreach (var side in new[] { "Left", "Right" })
            {
                foreach (var sign in new[] { 0.5f, -0.5f })
                {
                    var suffix = sign > 0 ? "pos" : "neg";
                    specs.Add(new MuscleCombinationSpec(
                        $"{side}_arm_swing_twist_{suffix}",
                        new MuscleValueSpec($"{side} Arm Down-Up", sign),
                        new MuscleValueSpec($"{side} Arm Front-Back", sign),
                        new MuscleValueSpec($"{side} Arm Twist In-Out", sign)));
                    specs.Add(new MuscleCombinationSpec(
                        $"{side}_arm_twist_forearm_stretch_{suffix}",
                        new MuscleValueSpec($"{side} Arm Twist In-Out", sign),
                        new MuscleValueSpec($"{side} Forearm Stretch", sign)));
                    specs.Add(new MuscleCombinationSpec(
                        $"{side}_leg_swing_twist_{suffix}",
                        new MuscleValueSpec($"{side} Upper Leg Front-Back", sign),
                        new MuscleValueSpec($"{side} Upper Leg In-Out", sign),
                        new MuscleValueSpec($"{side} Upper Leg Twist In-Out", sign)));
                    specs.Add(new MuscleCombinationSpec(
                        $"{side}_leg_twist_stretch_{suffix}",
                        new MuscleValueSpec($"{side} Upper Leg Twist In-Out", sign),
                        new MuscleValueSpec($"{side} Lower Leg Stretch", sign)));
                    specs.Add(new MuscleCombinationSpec(
                        $"{side}_foot_twist_chain_{suffix}",
                        new MuscleValueSpec($"{side} Lower Leg Twist In-Out", sign),
                        new MuscleValueSpec($"{side} Foot Twist In-Out", sign),
                        new MuscleValueSpec($"{side} Foot Up-Down", sign)));
                }
            }
            return specs.ToArray();
        }

        private static float[] ReadInternalAvatarPoseValues(HumanPoseHandler handler, HumanPose pose, int avatarPoseLength)
        {
            if (handler == null || avatarPoseLength <= 0)
            {
                return Array.Empty<float>();
            }

            var array = new NativeArray<float>(avatarPoseLength, Allocator.Temp, NativeArrayOptions.ClearMemory);
            try
            {
                var localPose = new HumanPose
                {
                    bodyPosition = pose.bodyPosition,
                    bodyRotation = pose.bodyRotation,
                    muscles = pose.muscles != null ? (float[])pose.muscles.Clone() : null,
                };
                // jointPaths 版 HumanPoseHandler 不绑定 Transform 根，只能写 internal pose。
                handler.SetInternalHumanPose(ref localPose);
                handler.GetInternalAvatarPose(array);
                return array.ToArray();
            }
            catch
            {
                return Array.Empty<float>();
            }
            finally
            {
                if (array.IsCreated)
                {
                    array.Dispose();
                }
            }
        }

        private static void AddInternalAvatarPoseProbeRotations(MuscleProbe probe, string[] jointPaths, float[] baseValues, float[] values)
        {
            if (probe == null || jointPaths == null || baseValues == null || values == null)
            {
                return;
            }

            var count = Math.Min(jointPaths.Length, Math.Min(baseValues.Length, values.Length) / 7);
            for (var i = 0; i < count; i++)
            {
                var offset = i * 7;
                var before = new Quaternion(baseValues[offset + 3], baseValues[offset + 4], baseValues[offset + 5], baseValues[offset + 6]);
                var after = new Quaternion(values[offset + 3], values[offset + 4], values[offset + 5], values[offset + 6]);
                if (Quaternion.Dot(before, after) >= 1f - 0.00001f)
                {
                    continue;
                }

                var path = jointPaths[i] ?? string.Empty;
                probe.rotations.Add(new ProbeRotationTrack
                {
                    path = path,
                    name = string.IsNullOrWhiteSpace(path) ? string.Empty : path.Split('/').LastOrDefault() ?? path,
                    baseRotation = new QuaternionKey(0f, before),
                    rotation = new QuaternionKey(0f, after),
                });
            }
        }

        private static void AddInternalAvatarPoseCombinationProbeTracks(MuscleCombinationProbe probe, string[] jointPaths, float[] baseValues, float[] values)
        {
            if (probe == null || jointPaths == null || baseValues == null || values == null)
            {
                return;
            }

            var count = Math.Min(jointPaths.Length, Math.Min(baseValues.Length, values.Length) / 7);
            for (var i = 0; i < count; i++)
            {
                var offset = i * 7;
                var beforeTranslation = new Vector3(baseValues[offset], baseValues[offset + 1], baseValues[offset + 2]);
                var afterTranslation = new Vector3(values[offset], values[offset + 1], values[offset + 2]);
                var path = jointPaths[i] ?? string.Empty;
                var name = string.IsNullOrWhiteSpace(path) ? string.Empty : path.Split('/').LastOrDefault() ?? path;
                if ((afterTranslation - beforeTranslation).sqrMagnitude > PositionEpsilon)
                {
                    probe.translations.Add(new ProbeTranslationTrack
                    {
                        path = path,
                        name = name,
                        baseTranslation = new Vector3Key(0f, beforeTranslation),
                        translation = new Vector3Key(0f, afterTranslation),
                    });
                }

                var before = new Quaternion(baseValues[offset + 3], baseValues[offset + 4], baseValues[offset + 5], baseValues[offset + 6]);
                var after = new Quaternion(values[offset + 3], values[offset + 4], values[offset + 5], values[offset + 6]);
                if (Quaternion.Dot(before, after) >= 1f - 0.00001f)
                {
                    continue;
                }

                probe.rotations.Add(new ProbeRotationTrack
                {
                    path = path,
                    name = name,
                    baseRotation = new QuaternionKey(0f, before),
                    rotation = new QuaternionKey(0f, after),
                });
            }
        }

        private sealed class MuscleCombinationSpec
        {
            public MuscleCombinationSpec(string name, params MuscleValueSpec[] muscles)
            {
                Name = name;
                Muscles = muscles ?? Array.Empty<MuscleValueSpec>();
            }

            public string Name { get; }
            public MuscleValueSpec[] Muscles { get; }
        }

        private sealed class MuscleValueSpec
        {
            public MuscleValueSpec(string muscleName, float value)
            {
                MuscleName = muscleName;
                Value = value;
            }

            public string MuscleName { get; }
            public float Value { get; }
        }

        private sealed class ControllerSamplingPlan
        {
            public RuntimeAnimatorController Controller { get; set; }
            public string Mode { get; set; }
            public string AssetPath { get; set; }
            public string StateName { get; set; }
            public AnimatorControllerRecoveryMetadata RecoveryMetadata { get; set; }
            public IReadOnlyList<ControllerLayerState> LayerStates { get; set; }
            public bool UseLayerMixer { get; set; }
            public int AdditionalLayerMaskCount { get; set; }
            public int AdditionalLayerSkeletonMaskEntryCount { get; set; }
            public bool LayerMasksApplied { get; set; }
            public string LayerMaskWarning { get; set; }
            public int IkPassEnabledLayerCount { get; set; }
            public string IkPassMessage { get; set; }
            public string[] SkippedUntrustedLayerNames { get; set; }
            public string[] SkippedUntrustedLayerReasons { get; set; }
            public List<AnimatorControllerSkippedLayerDiagnostic> SkippedUntrustedLayerDiagnostics { get; set; }
            public string[] DiagnosticSampledSkippedLayerNames { get; set; }
            public string Message { get; set; }
        }

        private sealed class GeneratedControllerResult
        {
            public RuntimeAnimatorController Controller { get; set; }
            public string AssetPath { get; set; }
            public string StateName { get; set; }
            public IReadOnlyList<ControllerLayerState> LayerStates { get; set; }
            public int AdditionalLayerCount { get; set; }
            public int AdditionalLayerMaskCount { get; set; }
            public int AdditionalLayerSkeletonMaskEntryCount { get; set; }
            public string LayerMaskWarning { get; set; }
            public int IkPassEnabledLayerCount { get; set; }
            public string IkPassMessage { get; set; }
            public string Message { get; set; }
        }

        private sealed class IkPassControllerCopy
        {
            public RuntimeAnimatorController Controller { get; set; }
            public int EnabledLayerCount { get; set; }
            public string Message { get; set; }
        }

        private sealed class ControllerLayerState
        {
            public int LayerIndex { get; set; }
            public int SourceLayerIndex { get; set; }
            public string LayerName { get; set; }
            public string StateName { get; set; }
            public string ClipName { get; set; }
            public string RequestedClipName { get; set; }
            public string ControllerMotionName { get; set; }
            public bool RequestedClipOverridesControllerMotion { get; set; }
            public string MotionSelectionRule { get; set; }
            public AnimationClip Clip { get; set; }
            public int SourceStateMachineIndex { get; set; }
            public int StateMachineMotionSetIndex { get; set; }
            public long LayerBinding { get; set; }
            public float Weight { get; set; }
            public float Speed { get; set; }
            public float CycleOffset { get; set; }
            public bool IsAdditive { get; set; }
            public bool IkPass { get; set; }
            public bool IkOnFeet { get; set; }
            public bool SyncedLayerAffectsTiming { get; set; }
            public AnimeStudioHumanPoseMask LayerBodyMask { get; set; }
            public AnimeStudioSkeletonMask LayerSkeletonMask { get; set; }
            public bool RequestLayerContext { get; set; }
            public bool DiagnosticSampledSkippedLayer { get; set; }
            public bool MaskApplied { get; set; }
            public AvatarMask AvatarMask { get; set; }
            public AnimationClipPlayable Playable { get; set; }
        }

        private sealed class TransformPathLookup
        {
            public Dictionary<string, Transform> Exact { get; } = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<uint, Transform> Hash { get; } = new Dictionary<uint, Transform>();
            public List<TransformPathRecord> Paths { get; } = new List<TransformPathRecord>();
        }

        private sealed class TransformPathRecord
        {
            public string Path { get; set; }
            public Transform Transform { get; set; }
        }

        private sealed class LayerAvatarMaskBuildResult
        {
            public AvatarMask Mask { get; set; }
            public int SourcePathCount { get; set; }
            public int ResolvedPathCount { get; set; }
            public int UnresolvedPathCount { get; set; }
            public List<string> UnresolvedExamples { get; } = new List<string>();
        }

        [Serializable]
        private sealed class AnimationAssetSidecar
        {
            public DecodedAnimationSidecar decoded;
        }

        [Serializable]
        private sealed class DecodedAnimationSidecar
        {
            public DecodedFloatCurve[] floats;
        }

        [Serializable]
        private sealed class DecodedFloatCurve
        {
            public string path;
            public string attribute;
            public FloatKey[] keyframes;
        }

        private sealed class TrackRecorder
        {
            private const float PositionEpsilon = 0.00001f;
            private const float RotationEpsilon = 0.00001f;
            private const float ScaleEpsilon = 0.00001f;

            private readonly Transform root;
            private readonly Transform transform;
            private readonly float[] sampleTimes;
            private readonly Vector3[] translations;
            private readonly Quaternion[] rotations;
            private readonly Vector3[] scales;
            private readonly string path;
            private readonly Vector3 restTranslation;
            private readonly Quaternion restRotation;
            private readonly Vector3 restScale;

            public TrackRecorder(Transform root, Transform transform, float[] sampleTimes)
            {
                this.root = root;
                this.transform = transform;
                this.sampleTimes = sampleTimes;
                path = GetPath(root, transform);
                restTranslation = transform.localPosition;
                restRotation = transform.localRotation;
                restScale = transform.localScale;
                translations = new Vector3[sampleTimes.Length];
                rotations = new Quaternion[sampleTimes.Length];
                scales = new Vector3[sampleTimes.Length];
            }

            public void Record(int index, float time)
            {
                translations[index] = transform.localPosition;
                rotations[index] = transform.localRotation;
                scales[index] = transform.localScale;
            }

            public TransformTrack ToResult()
            {
                var changed = HasChanged();
                var track = new TransformTrack
                {
                    path = path,
                    name = transform.name,
                    changed = changed,
                    restTranslation = new Vector3Key(0f, restTranslation),
                    restRotation = new QuaternionKey(0f, restRotation),
                    restScale = new Vector3Key(0f, restScale),
                    translations = new Vector3Key[translations.Length],
                    rotations = new QuaternionKey[rotations.Length],
                    scales = new Vector3Key[scales.Length],
                };
                for (var i = 0; i < translations.Length; i++)
                {
                    var time = sampleTimes.Length <= i ? 0f : sampleTimes[i];
                    track.translations[i] = new Vector3Key(time, translations[i]);
                    track.rotations[i] = new QuaternionKey(time, rotations[i]);
                    track.scales[i] = new Vector3Key(time, scales[i]);
                }
                return track;
            }

            private bool HasChanged()
            {
                for (var i = 0; i < translations.Length; i++)
                {
                    if ((translations[i] - restTranslation).sqrMagnitude > PositionEpsilon) return true;
                    if (Quaternion.Dot(rotations[i], restRotation) < 1f - RotationEpsilon) return true;
                    if ((scales[i] - restScale).sqrMagnitude > ScaleEpsilon) return true;
                }

                for (var i = 1; i < translations.Length; i++)
                {
                    if ((translations[i] - translations[0]).sqrMagnitude > PositionEpsilon) return true;
                    if (Quaternion.Dot(rotations[i], rotations[0]) < 1f - RotationEpsilon) return true;
                    if ((scales[i] - scales[0]).sqrMagnitude > ScaleEpsilon) return true;
                }
                return false;
            }
        }
    }
}
