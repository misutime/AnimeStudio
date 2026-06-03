using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace AnimeStudio.UnityBake
{
    public static class AnimeStudioPlayableBaker
    {
        public static AnimeStudioBakeResult Bake(AnimeStudioBakeRequest request)
        {
            if (request.unityAssetPaths == null)
            {
                return Error(request, "Request is missing unityAssetPaths.");
            }
            var prefab = string.IsNullOrWhiteSpace(request.unityAssetPaths.modelPrefab)
                ? null
                : AssetDatabase.LoadAssetAtPath<GameObject>(request.unityAssetPaths.modelPrefab);
            var clip = LoadAnimationClip(request);
            if (clip == null)
            {
                return Error(request, $"AnimationClip not found or could not be imported: {request.unityAssetPaths.animationClip}");
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

                if (clip.isHumanMotion && (animator.avatar == null || !animator.avatar.isValid))
                {
                    return Error(request, "Humanoid clip requires a valid Animator.avatar. Refusing to bake guessed data.");
                }

                var transforms = instance.GetComponentsInChildren<Transform>(true)
                    .OrderBy(x => GetPath(instance.transform, x), StringComparer.Ordinal)
                    .ToArray();
                var frameRate = Mathf.Max(1, request.frameRate);
                var sampleTimes = BuildSampleTimes(clip.length, frameRate);
                var tracks = transforms
                    .Select(t => new TrackRecorder(instance.transform, t, sampleTimes))
                    .ToArray();

                var graph = PlayableGraph.Create("AnimeStudioUnityBake");
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimationClipPlayable.Create(graph, clip);
                var output = AnimationPlayableOutput.Create(graph, "Animation", animator);
                output.SetSourcePlayable(playable);

                AnimationMode.StartAnimationMode();
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
                return new AnimeStudioBakeResult
                {
                    status = "ok",
                    message = "Unity Animator/PlayableGraph bake completed.",
                    modelPrefab = request.unityAssetPaths.modelPrefab,
                    animationClip = request.unityAssetPaths.animationClip,
                    modelName = prefab != null ? prefab.name : instance.name,
                    clipName = clip.name,
                    clipLength = clip.length,
                    frameRate = frameRate,
                    isHumanMotion = clip.isHumanMotion,
                    avatarName = animator.avatar != null ? animator.avatar.name : null,
                    avatarValid = animator.avatar != null && animator.avatar.isValid,
                    sampleCount = sampleTimes.Length,
                    transformCount = transforms.Length,
                    changedTrackCount = resultTracks.Count(x => x.changed),
                    sampleTimes = sampleTimes,
                    tracks = resultTracks,
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

        private static AnimationClip LoadAnimationClip(AnimeStudioBakeRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.unityAssetPaths.animationClip))
            {
                var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(request.unityAssetPaths.animationClip);
                if (existing != null)
                {
                    return existing;
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
            return AssetDatabase.LoadAssetAtPath<AnimationClip>(targetPath);
        }

        private static AnimeStudioBakeResult Error(AnimeStudioBakeRequest request, string message)
        {
            return new AnimeStudioBakeResult
            {
                status = "error",
                message = message,
                modelPrefab = request?.unityAssetPaths?.modelPrefab,
                animationClip = request?.unityAssetPaths?.animationClip,
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

            public TrackRecorder(Transform root, Transform transform, float[] sampleTimes)
            {
                this.root = root;
                this.transform = transform;
                this.sampleTimes = sampleTimes;
                path = GetPath(root, transform);
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
