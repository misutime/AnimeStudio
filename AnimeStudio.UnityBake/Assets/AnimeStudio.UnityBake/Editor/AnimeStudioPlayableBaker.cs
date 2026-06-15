using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Collections;
using UnityEditor;
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
            if (request.animeStudioAssets?.animation?.requiresHumanoidBake == true && !clip.isHumanMotion)
            {
                // Humanoid/Muscle 动画如果没有被 Unity 导入成 humanMotion，
                // PlayableGraph 只能采样到普通 Transform 曲线，身体主动作会丢失。
                return Error(
                    request,
                    "Humanoid/Muscle production bake requires Unity to import the selected AnimationClip as humanMotion, but imported clip.isHumanMotion=false. This usually means the clip is an AnimatorController auxiliary/non-body layer, or the controller context still needs a deterministic baseLayerClip. Refusing to write a misleading baked glTF.");
            }
            var clipFilterStats = ApplyDiagnosticClipFilter(ref clip);
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
                var animator = instance.GetComponentInChildren<Animator>();
                if (animator == null)
                {
                    animator = instance.AddComponent<Animator>();
                }
                animator.applyRootMotion = true;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                if (explicitAvatar != null)
                {
                    // request 显式指定 Avatar asset 时，它是强约束。
                    // 即使目标来自 prefab，也要用这个原始 Unity Avatar，避免静默走到别的 Avatar。
                    animator.avatar = explicitAvatar;
                }

                if (clip.isHumanMotion && (animator.avatar == null || !animator.avatar.isValid))
                {
                    return Error(request, "Humanoid clip requires a valid Animator.avatar. Refusing to bake guessed data.");
                }

                var transforms = instance.GetComponentsInChildren<Transform>(true)
                    .OrderBy(x => GetPath(instance.transform, x), StringComparer.Ordinal)
                    .ToArray();
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
                    .Select(t => new TrackRecorder(instance.transform, t, sampleTimes))
                    .ToArray();

                var graph = PlayableGraph.Create("AnimeStudioUnityBake");
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimationClipPlayable.Create(graph, clip);
                var output = AnimationPlayableOutput.Create(graph, "Animation", animator);
                output.SetSourcePlayable(playable);

                AnimationMode.StartAnimationMode();
                var humanoidPoseSamples = new List<HumanoidPoseSample>();
                var sampleBounds = new List<SampleBounds>();
                var poseHandler = request.probeMuscles
                    && animator.avatar != null
                    && animator.avatar.isValid
                    && animator.avatar.isHuman
                        ? new HumanPoseHandler(animator.avatar, instance.transform)
                        : null;
                try
                {
                    graph.Play();
                    for (var i = 0; i < sampleTimes.Length; i++)
                    {
                        var time = sampleTimes[i];
                        AnimationMode.BeginSampling();
                        AnimationMode.SamplePlayableGraph(graph, 0, time);
                        AnimationMode.EndSampling();
                        foreach (var track in tracks)
                        {
                            track.Record(i, time);
                        }
                        sampleBounds.Add(ReadSampleBounds(instance.transform, time));
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
                var internalAvatarPoseSnapshots = new List<InternalAvatarPoseSnapshot>();
                var internalAvatarPoseTimeline = new List<InternalAvatarPoseTimelineSample>();
                var internalAvatarPoseMuscleProbes = new List<MuscleProbe>();
                var internalAvatarPoseMuscleCombinationProbes = new List<MuscleCombinationProbe>();
                var muscleProbes = request.probeMuscles
                    ? ProbeMuscles(instance.transform, animator, transforms, editorCurveTracks, internalAvatarPoseSnapshots, out internalAvatarPoseMuscleProbes)
                    : new List<MuscleProbe>();
                var muscleCombinationProbes = request.probeMuscles
                    ? ProbeMuscleCombinations(instance.transform, animator, transforms, internalAvatarPoseSnapshots, out internalAvatarPoseMuscleCombinationProbes)
                    : new List<MuscleCombinationProbe>();
                var editorCurveSetHumanPoseTransformTracks = new List<TransformTrack>();
                var editorCurveHumanPoseRotations = request.probeMuscles
                    ? ApplyFirstEditorCurveSampleAsHumanPose(instance.transform, animator, transforms, editorCurveTracks, sampleTimes, internalAvatarPoseSnapshots, internalAvatarPoseTimeline, out editorCurveSetHumanPoseTransformTracks)
                    : new List<ProbeRotationTrack>();
                var rigMetadata = instance.GetComponentInChildren<AnimeStudioBakeRigMetadata>(true);
                var requestedAvatarAsset = AnimeStudioGltfSkeletonBuilder.NormalizeUnityAssetPath(request.unityAssetPaths.avatarAsset);
                var importedAvatarAssetValid = !string.IsNullOrWhiteSpace(requestedAvatarAsset)
                    && animator.avatar != null
                    && animator.avatar.isValid
                    && animator.avatar.isHuman
                    && rigMetadata != null
                    && string.Equals(rigMetadata.restPoseSource, "imported_unity_avatar_asset", StringComparison.Ordinal);
                return new AnimeStudioBakeResult
                {
                    status = "ok",
                    message = "Unity Animator/PlayableGraph bake completed.",
                    modelPrefab = request.unityAssetPaths.modelPrefab,
                    animationClip = clipLoad.assetPath,
                    requestedAnimationClip = AnimeStudioGltfSkeletonBuilder.NormalizeUnityAssetPath(request.unityAssetPaths.animationClip),
                    importedAnimationClip = clipLoad.assetPath,
                    animationClipSource = clipLoad.source,
                    modelName = prefab != null ? prefab.name : instance.name,
                    clipName = clip.name,
                    clipLength = clip.length,
                    frameRate = frameRate,
                    isHumanMotion = clip.isHumanMotion,
                    avatarName = animator.avatar != null ? animator.avatar.name : null,
                    avatarValid = animator.avatar != null && animator.avatar.isValid,
                    requestedAvatarAsset = requestedAvatarAsset,
                    importedAvatarAsset = importedAvatarAssetValid ? requestedAvatarAsset : null,
                    importedAvatarAssetValid = importedAvatarAssetValid,
                    clipFilterMode = clipFilterStats.mode,
                    clipFilterRemovedTransformCurveCount = clipFilterStats.removedTransformCurveCount,
                    clipFilterRemovedAnimatorCurveCount = clipFilterStats.removedAnimatorCurveCount,
                    clipFilterRemovedObjectReferenceCurveCount = clipFilterStats.removedObjectReferenceCurveCount,
                    rigRestPoseSource = rigMetadata != null ? rigMetadata.restPoseSource : "prefab_or_gltf_transform_rest_pose",
                    rigRestPoseApplied = rigMetadata != null && rigMetadata.restPoseApplied,
                    sampleCount = sampleTimes.Length,
                    transformCount = transforms.Length,
                    changedTrackCount = resultTracks.Count(x => x.changed),
                    muscleProbeCount = muscleProbes.Count,
                    sampleTimes = sampleTimes,
                    sampleBounds = sampleBounds,
                    tracks = resultTracks,
                    muscleProbes = muscleProbes,
                    internalAvatarPoseMuscleProbes = internalAvatarPoseMuscleProbes,
                    muscleCombinationProbes = muscleCombinationProbes,
                    internalAvatarPoseMuscleCombinationProbes = internalAvatarPoseMuscleCombinationProbes,
                    muscleNames = request.probeMuscles ? HumanTrait.MuscleName : null,
                    humanoidPoseSamples = humanoidPoseSamples,
                    editorCurveTracks = editorCurveTracks,
                    editorCurveHumanPoseRotations = editorCurveHumanPoseRotations,
                    editorCurveSetHumanPoseTransformTracks = editorCurveSetHumanPoseTransformTracks,
                    internalAvatarPoseSnapshots = internalAvatarPoseSnapshots,
                    internalAvatarPoseTimeline = internalAvatarPoseTimeline,
                };
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

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

        private static ClipFilterStats ApplyDiagnosticClipFilter(ref AnimationClip clip)
        {
            var mode = Environment.GetEnvironmentVariable("ANIMESTUDIO_UNITY_BAKE_CLIP_FILTER");
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

            // 诊断用：只改临时拷贝，不碰导入资产。默认 original 完全不走这里。
            clip = copy;
            return stats;
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

            var muscleCurves = editorCurves?
                .Where(x => x?.values != null && x.values.Length > 0 && !string.IsNullOrWhiteSpace(x.propertyName))
                .GroupBy(x => x.propertyName)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, EditorCurveTrack>(StringComparer.OrdinalIgnoreCase);
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

            var muscleCurves = editorCurves?
                .Where(x => x?.values != null && x.values.Length > 0 && !string.IsNullOrWhiteSpace(x.propertyName))
                .GroupBy(x => x.propertyName)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, EditorCurveTrack>(StringComparer.OrdinalIgnoreCase);
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

                CaptureInternalAvatarPoseTimelineSample(handler, pose, avatarPoseLength, jointPaths, time, timeline);
            }
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
                handler.SetHumanPose(ref localPose);
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
                handler.SetHumanPose(ref localPose);
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
                handler.SetHumanPose(ref localPose);
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
