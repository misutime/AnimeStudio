using System;
using System.Collections.Generic;
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
                avatarAsset = request?.avatarAsset,
                avatarKey = request?.avatarKey,
                jointPaths = request?.jointPaths ?? Array.Empty<string>(),
                muscleNames = HumanTrait.MuscleName ?? Array.Empty<string>(),
                setPoseMethod = "HumanPoseHandler.SetInternalHumanPose",
                writesLibraryIndex = false,
                writesModelAnimations = false,
            };

            try
            {
                ValidateUnity6();
                ValidateRequest(request);
                if (SetInternalHumanPoseMethod == null)
                {
                    throw new MissingMethodException("HumanPoseHandler.SetInternalHumanPose is required for UnityBakeAccelerated.");
                }

                var avatar = AssetDatabase.LoadAssetAtPath<Avatar>(request.avatarAsset);
                result.avatarName = avatar != null ? avatar.name : null;
                result.avatarValid = avatar != null && avatar.isValid;
                result.avatarHuman = avatar != null && avatar.isHuman;
                if (avatar == null)
                {
                    throw new InvalidOperationException("Avatar asset not found: " + request.avatarAsset);
                }
                if (!avatar.isValid || !avatar.isHuman)
                {
                    throw new InvalidOperationException("Avatar asset is not a valid Humanoid Avatar: " + request.avatarAsset);
                }

                var jointPaths = request.jointPaths ?? Array.Empty<string>();
                result.jointCount = jointPaths.Length;
                result.clipCount = request.clips?.Count ?? 0;

                var handler = new HumanPoseHandler(avatar, jointPaths);
                var poseValues = new NativeArray<float>(jointPaths.Length * 7, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                try
                {
                    foreach (var clip in request.clips ?? new List<UnityBakeAcceleratedClipRequest>())
                    {
                        var clipResult = SolveClip(handler, poseValues, clip);
                        result.sampleCount += clipResult.sampleCount;
                        result.clips.Add(clipResult);
                    }
                }
                finally
                {
                    if (poseValues.IsCreated)
                    {
                        poseValues.Dispose();
                    }
                }

                result.status = "ok";
                result.message = "UnityBakeAccelerated solved with Unity 6 HumanPoseHandler internal avatar pose.";
                return result;
            }
            catch (Exception ex)
            {
                result.status = "error";
                result.message = ex.GetType().Name + ": " + ex.Message;
                return result;
            }
        }

        private static UnityBakeAcceleratedClipResult SolveClip(
            HumanPoseHandler handler,
            NativeArray<float> poseValues,
            UnityBakeAcceleratedClipRequest clip)
        {
            var clipResult = new UnityBakeAcceleratedClipResult
            {
                clipKey = clip?.clipKey,
                clipName = clip?.clipName,
                frameRate = clip != null && clip.frameRate > 0 ? clip.frameRate : 30,
                sampleCount = clip?.samples?.Count ?? 0,
            };

            foreach (var sample in clip?.samples ?? new List<UnityBakeAcceleratedPoseSample>())
            {
                clipResult.samples.Add(SolveSample(handler, poseValues, sample));
            }

            return clipResult;
        }

        private static UnityBakeAcceleratedPoseResult SolveSample(
            HumanPoseHandler handler,
            NativeArray<float> poseValues,
            UnityBakeAcceleratedPoseSample sample)
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

                SetInternalHumanPoseMethod.Invoke(handler, new object[] { pose });
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
            if (string.IsNullOrWhiteSpace(request.avatarAsset))
            {
                throw new InvalidOperationException("Request must provide avatarAsset.");
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
    }
}
