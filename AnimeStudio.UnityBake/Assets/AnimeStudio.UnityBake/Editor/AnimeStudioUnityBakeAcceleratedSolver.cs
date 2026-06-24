using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace AnimeStudio.UnityBake
{
    public static class AnimeStudioUnityBakeAcceleratedSolver
    {
        public const string HelperMarker = "UnityBakeAcceleratedWorkerV1";
        private static readonly MethodInfo SetInternalHumanPoseMethod = FindSetInternalHumanPoseMethod();

        private static MethodInfo FindSetInternalHumanPoseMethod()
        {
            var humanPoseRef = typeof(HumanPose).MakeByRefType();
            return typeof(HumanPoseHandler)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(x =>
                {
                    if (!string.Equals(x.Name, "SetInternalHumanPose", StringComparison.Ordinal))
                    {
                        return false;
                    }

                    var parameters = x.GetParameters();
                    return parameters.Length == 1 && parameters[0].ParameterType == humanPoseRef;
                });
        }

        public static bool CanRun(out string message)
        {
            if (!Application.unityVersion.StartsWith("6000.", StringComparison.Ordinal))
            {
                message = "UnityBakeAccelerated requires Unity 6 / 6000.x. Current Unity version: " + Application.unityVersion;
                return false;
            }
            if (SetInternalHumanPoseMethod == null)
            {
                message = "HumanPoseHandler.SetInternalHumanPose is not available.";
                return false;
            }

            message = "UnityBakeAccelerated runtime is available.";
            return true;
        }

        public static UnityBakeAcceleratedResult Solve(UnityBakeAcceleratedRequest request)
        {
            var result = new UnityBakeAcceleratedResult
            {
                status = "error",
                unityVersion = Application.unityVersion,
                animationSolvePath = "UnityBakeAccelerated",
                outputAnimationData = "UnityDerivedJointLocalTRS",
                requiresUnityToSolve = true,
                finalGltfAnimationRequired = true,
                valueLayout = "perJoint:T.xyz,Q.xyzw",
                avatarAsset = request?.avatarAsset,
                avatarKey = request?.avatarKey,
                avatarSource = "unknown",
                jointPaths = request?.jointPaths ?? Array.Empty<string>(),
                muscleNames = HumanTrait.MuscleName ?? Array.Empty<string>(),
                setPoseMethod = ResolvePoseSolveMethod(request),
                writesLibraryIndex = false,
                writesModelAnimations = false,
            };
            // 先记录 solver 自身吞吐，后面才能判断几万模型/几十万动画是否跑得动。
            var totalTimer = Stopwatch.StartNew();
            var solveTimer = new Stopwatch();
            var avatarRoot = default(GameObject);

            try
            {
                ValidateUnity6();
                ValidateRequest(request);
                if (SetInternalHumanPoseMethod == null)
                {
                    throw new MissingMethodException("HumanPoseHandler.SetInternalHumanPose is required for UnityBakeAccelerated.");
                }

                var avatar = LoadAvatar(request, out avatarRoot, out var avatarSource);
                result.avatarName = avatar != null ? avatar.name : null;
                result.avatarSource = avatarSource;
                result.avatarValid = avatar != null && avatar.isValid;
                result.avatarHuman = avatar != null && avatar.isHuman;
                if (avatar == null)
                {
                    throw new InvalidOperationException("No valid Avatar source was provided. Use avatarAsset or request avatar with complete humanBones and skeletonBones.");
                }
                if (!avatar.isValid || !avatar.isHuman)
                {
                    throw new InvalidOperationException($"Avatar is not a valid Humanoid Avatar. source={avatarSource}, avatar={avatar.name}, isValid={avatar.isValid}, isHuman={avatar.isHuman}");
                }

                var jointPaths = request.jointPaths ?? Array.Empty<string>();
                result.jointCount = jointPaths.Length;
                result.clipCount = request.clips?.Count ?? 0;

                var handler = new HumanPoseHandler(avatar, jointPaths);
                var poseSolveMethod = ResolvePoseSolveMethod(request);
                result.setPoseMethod = poseSolveMethod;
                var poseValues = new NativeArray<float>(jointPaths.Length * 7, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                try
                {
                    solveTimer.Start();
                    var restSample = SolveSample(handler, poseValues, new UnityBakeAcceleratedPoseSample
                    {
                        time = 0,
                        bodyPosition = new Vector3Value { x = 0, y = 0, z = 0 },
                        bodyRotation = new QuaternionValue { x = 0, y = 0, z = 0, w = 1 },
                        muscles = new float[HumanTrait.MuscleName != null ? HumanTrait.MuscleName.Length : 95],
                    }, poseSolveMethod);
                    if (string.Equals(restSample.status, "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        result.restValues = restSample.values ?? Array.Empty<float>();
                        result.restValueCount = result.restValues.Length;
                    }
                    foreach (var clip in request.clips ?? new List<UnityBakeAcceleratedClipRequest>())
                    {
                        var clipResult = SolveClip(handler, poseValues, clip, poseSolveMethod);
                        result.sampleCount += clipResult.sampleCount;
                        result.clips.Add(clipResult);
                    }
                    solveTimer.Stop();
                }
                finally
                {
                    if (poseValues.IsCreated)
                    {
                        poseValues.Dispose();
                    }
                    if (avatarRoot != null)
                    {
                        UnityEngine.Object.DestroyImmediate(avatarRoot);
                        avatarRoot = null;
                    }
                }

                result.status = "ok";
                result.message = "UnityBakeAccelerated solved with Unity 6 HumanPoseHandler internal avatar pose.";
                FillTiming(result, totalTimer, solveTimer.Elapsed);
                return result;
            }
            catch (Exception ex)
            {
                if (avatarRoot != null)
                {
                    UnityEngine.Object.DestroyImmediate(avatarRoot);
                }
                result.status = "error";
                result.message = ex.GetType().Name + ": " + ex.Message;
                FillTiming(result, totalTimer, solveTimer.Elapsed);
                return result;
            }
        }

        private static UnityBakeAcceleratedClipResult SolveClip(
            HumanPoseHandler handler,
            NativeArray<float> poseValues,
            UnityBakeAcceleratedClipRequest clip,
            string poseSolveMethod)
        {
            var clipResult = new UnityBakeAcceleratedClipResult
            {
                clipKey = clip?.clipKey,
                clipName = clip?.clipName,
                frameRate = clip != null && clip.frameRate > 0 ? clip.frameRate : 30,
                sampleCount = clip?.samples?.Count ?? 0,
            };
            var timer = Stopwatch.StartNew();

            foreach (var sample in clip?.samples ?? new List<UnityBakeAcceleratedPoseSample>())
            {
                clipResult.samples.Add(SolveSample(handler, poseValues, sample, poseSolveMethod));
            }

            timer.Stop();
            clipResult.solveMilliseconds = (float)timer.Elapsed.TotalMilliseconds;
            clipResult.samplesPerSecond = clipResult.solveMilliseconds > 0 && clipResult.sampleCount > 0
                ? clipResult.sampleCount / (clipResult.solveMilliseconds / 1000f)
                : 0;
            return clipResult;
        }

        private static UnityBakeAcceleratedPoseResult SolveSample(
            HumanPoseHandler handler,
            NativeArray<float> poseValues,
            UnityBakeAcceleratedPoseSample sample,
            string poseSolveMethod)
        {
            var sampleResult = new UnityBakeAcceleratedPoseResult
            {
                time = sample != null ? sample.time : 0,
                status = "error",
                valueCount = poseValues.Length,
                values = Array.Empty<float>(),
            };

            try
            {
                var pose = new HumanPose
                {
                    bodyPosition = sample != null ? sample.bodyPosition.ToVector3() : Vector3.zero,
                    bodyRotation = NormalizeOrIdentity(sample != null ? sample.bodyRotation.ToQuaternion() : Quaternion.identity),
                    muscles = NormalizeMuscleArray(sample?.muscles),
                };

                // SetInternalHumanPose 适合 internal pose 对照；SetHumanPose 用来确认公开 HumanPose.muscles 语义。
                // 两条路都只输出目标骨架 TRS，最终仍由 CLI 写成 glTF 动画。
                if (string.Equals(poseSolveMethod, "HumanPoseHandler.SetHumanPose", StringComparison.Ordinal))
                {
                    handler.SetHumanPose(ref pose);
                }
                else
                {
                    SetInternalHumanPoseMethod.Invoke(handler, new object[] { pose });
                }
                handler.GetInternalAvatarPose(poseValues);
                sampleResult.values = poseValues.ToArray();
                sampleResult.valueCount = sampleResult.values.Length;
                sampleResult.status = "ok";
                return sampleResult;
            }
            catch (Exception ex)
            {
                sampleResult.error = ex.GetType().Name + ": " + ex.Message;
                return sampleResult;
            }
        }

        private static string ResolvePoseSolveMethod(UnityBakeAcceleratedRequest request)
        {
            var value = request?.poseSolveMethod;
            return string.Equals(value, "SetHumanPose", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "HumanPoseHandler.SetHumanPose", StringComparison.OrdinalIgnoreCase)
                ? "HumanPoseHandler.SetHumanPose"
                : "HumanPoseHandler.SetInternalHumanPose";
        }

        private static void ValidateUnity6()
        {
            if (!CanRun(out var message))
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void ValidateRequest(UnityBakeAcceleratedRequest request)
        {
            if (request == null)
            {
                throw new InvalidOperationException("Request is empty.");
            }
            if (!string.Equals(request.mode, "UnityBakeAccelerated", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Request mode must be UnityBakeAccelerated.");
            }
            if (string.IsNullOrWhiteSpace(request.avatarAsset) && !HasRequestHumanDescriptionAvatar(request.avatar))
            {
                throw new InvalidOperationException("Request must provide avatarAsset or avatar with complete humanBones and skeletonBones.");
            }
            if (request.jointPaths == null || request.jointPaths.Length == 0)
            {
                throw new InvalidOperationException("Request must provide jointPaths.");
            }
            if (request.clips == null || request.clips.Count == 0)
            {
                throw new InvalidOperationException("Request must provide at least one clip.");
            }
        }

        private static Avatar LoadAvatar(UnityBakeAcceleratedRequest request, out GameObject avatarRoot, out string avatarSource)
        {
            avatarRoot = null;
            avatarSource = null;

            if (!string.IsNullOrWhiteSpace(request.avatarAsset))
            {
                var normalized = request.avatarAsset.Replace('\\', '/').Trim();
                var avatar = AssetDatabase.LoadAssetAtPath<Avatar>(normalized);
                avatarSource = "avatarAsset";
                if (avatar == null)
                {
                    throw new InvalidOperationException("Avatar asset not found: " + normalized);
                }
                return avatar;
            }

            // 只有完整 HumanDescription 才能临时 BuildHumanAvatar；缺 humanBones 时不能按名字猜。
            if (!HasRequestHumanDescriptionAvatar(request.avatar))
            {
                return null;
            }

            avatarRoot = BuildAvatarRoot(request.avatar);
            var description = BuildHumanDescription(request.avatar);
            var built = AvatarBuilder.BuildHumanAvatar(avatarRoot, description);
            built.name = string.IsNullOrWhiteSpace(request.avatar.name)
                ? "UnityBakeAcceleratedRequestAvatar"
                : request.avatar.name;
            avatarSource = "request_human_description";
            return built;
        }

        private static bool HasRequestHumanDescriptionAvatar(AnimeStudioAvatarAsset avatar)
        {
            return avatar?.humanBones != null
                && avatar.humanBones.Length > 0
                && avatar.skeletonBones != null
                && avatar.skeletonBones.Length > 0;
        }

        private static GameObject BuildAvatarRoot(AnimeStudioAvatarAsset avatar)
        {
            var root = new GameObject(string.IsNullOrWhiteSpace(avatar.name) ? "UnityBakeAcceleratedAvatarRoot" : avatar.name + "_Root");
            var transforms = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);

            foreach (var bone in avatar.skeletonBones)
            {
                if (bone == null || string.IsNullOrWhiteSpace(bone.name) || transforms.ContainsKey(bone.name))
                {
                    continue;
                }

                var go = new GameObject(bone.name);
                var t = go.transform;
                t.localPosition = bone.position.ToVector3();
                t.localRotation = NormalizeOrIdentity(bone.rotation.ToQuaternion());
                var scale = bone.scale.ToVector3();
                t.localScale = scale == Vector3.zero ? Vector3.one : scale;
                transforms[bone.name] = t;
            }

            foreach (var bone in avatar.skeletonBones)
            {
                if (bone == null || string.IsNullOrWhiteSpace(bone.name) || !transforms.TryGetValue(bone.name, out var t))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(bone.parentName) && transforms.TryGetValue(bone.parentName, out var parent))
                {
                    t.SetParent(parent, false);
                }
                else
                {
                    t.SetParent(root.transform, false);
                }
            }

            return root;
        }

        private static HumanDescription BuildHumanDescription(AnimeStudioAvatarAsset avatar)
        {
            var human = avatar.humanBones
                .Select(ParseHumanBone)
                .Where(x => !string.IsNullOrWhiteSpace(x.humanName) && !string.IsNullOrWhiteSpace(x.boneName))
                .ToArray();
            if (human.Length == 0)
            {
                throw new InvalidOperationException("Request avatar humanBones did not contain any Human:Bone mapping.");
            }

            var skeleton = avatar.skeletonBones
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.name))
                .Select(x => new SkeletonBone
                {
                    name = x.name,
                    position = x.position.ToVector3(),
                    rotation = NormalizeOrIdentity(x.rotation.ToQuaternion()),
                    scale = x.scale.ToVector3() == Vector3.zero ? Vector3.one : x.scale.ToVector3(),
                })
                .ToArray();
            var settings = avatar.humanDescription;
            return new HumanDescription
            {
                human = human,
                skeleton = skeleton,
                armStretch = settings != null ? settings.armStretch : 0.05f,
                legStretch = settings != null ? settings.legStretch : 0.05f,
                upperArmTwist = settings != null ? settings.armTwist : 0.5f,
                lowerArmTwist = settings != null ? settings.foreArmTwist : 0.5f,
                upperLegTwist = settings != null ? settings.upperLegTwist : 0.5f,
                lowerLegTwist = settings != null ? settings.legTwist : 0.5f,
                feetSpacing = settings != null ? settings.feetSpacing : 0,
                hasTranslationDoF = settings != null && settings.hasTranslationDoF,
            };
        }

        private static HumanBone ParseHumanBone(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return default;
            }

            var index = value.IndexOf(':');
            if (index <= 0 || index >= value.Length - 1)
            {
                return default;
            }

            return new HumanBone
            {
                humanName = value[..index],
                boneName = value[(index + 1)..],
                limit = new HumanLimit { useDefaultValues = true },
            };
        }

        private static float[] NormalizeMuscleArray(float[] muscles)
        {
            var names = HumanTrait.MuscleName ?? Array.Empty<string>();
            var result = new float[names.Length];
            if (muscles == null)
            {
                return result;
            }

            var count = Math.Min(result.Length, muscles.Length);
            Array.Copy(muscles, result, count);
            return result;
        }

        private static Quaternion NormalizeOrIdentity(Quaternion q)
        {
            var length = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (length <= 0.000001f)
            {
                return Quaternion.identity;
            }

            return new Quaternion(q.x / length, q.y / length, q.z / length, q.w / length);
        }

        private static void FillTiming(UnityBakeAcceleratedResult result, Stopwatch totalTimer, TimeSpan solveElapsed)
        {
            totalTimer.Stop();
            result.elapsedMilliseconds = (float)totalTimer.Elapsed.TotalMilliseconds;
            result.solveMilliseconds = (float)solveElapsed.TotalMilliseconds;
            result.samplesPerSecond = result.solveMilliseconds > 0 && result.sampleCount > 0
                ? result.sampleCount / (result.solveMilliseconds / 1000f)
                : 0;
        }
    }
}
