using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AnimeStudio.UnityBake
{
    public static class AnimeStudioAvatarOracleProbe
    {
        public static void Run(string oraclePath, string outputJson)
        {
            if (string.IsNullOrWhiteSpace(outputJson))
            {
                outputJson = Path.Combine(Path.GetDirectoryName(oraclePath) ?? Directory.GetCurrentDirectory(), "avatar_oracle_probe.json");
            }

            var report = Probe(oraclePath);
            var directory = Path.GetDirectoryName(outputJson);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(outputJson, JsonUtility.ToJson(report, true));
            Debug.Log("AnimeStudio Avatar oracle probe report: " + outputJson);
            if (!string.Equals(report.status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogError(report.message);
                EditorApplication.Exit(1);
                return;
            }

            EditorApplication.Exit(0);
        }

        private static AvatarOracleProbeReport Probe(string oraclePath)
        {
            var report = new AvatarOracleProbeReport
            {
                oraclePath = oraclePath,
            };

            if (string.IsNullOrWhiteSpace(oraclePath) || !File.Exists(oraclePath))
            {
                report.status = "error";
                report.message = "Avatar oracle path is missing or does not exist.";
                return report;
            }

            try
            {
                var package = JsonUtility.FromJson<AvatarOraclePackage>(File.ReadAllText(oraclePath));
                if (package == null || package.oracle == null)
                {
                    report.status = "error";
                    report.message = "Unable to parse Avatar oracle JSON.";
                    return report;
                }

                report.avatarName = package.avatar != null ? package.avatar.name : null;
                report.pathId = package.avatar != null ? package.avatar.pathId : 0;
                report.hasHumanDescription = package.avatar != null && package.avatar.hasHumanDescription;
                report.humanBoneIndexCount = Count(package.oracle.humanBoneIndex);
                report.humanBoneMappedCount = CountMapped(package.oracle.humanBoneIndex);
                report.humanSkeletonNodeCount = package.oracle.humanSkeleton != null ? package.oracle.humanSkeleton.nodeCount : 0;
                report.humanSkeletonPoseCount = Count(package.oracle.humanSkeleton != null ? package.oracle.humanSkeleton.pose : null);
                report.avatarSkeletonNodeCount = package.oracle.avatarSkeleton != null ? package.oracle.avatarSkeleton.nodeCount : 0;
                report.avatarDefaultPoseCount = Count(package.oracle.avatarSkeleton != null ? package.oracle.avatarSkeleton.defaultPose : null);
                report.rootMotionBoneIndex = package.oracle.rootMotion != null ? package.oracle.rootMotion.boneIndex : -1;
                report.sampleHumanPaths = SamplePaths(package.oracle.humanSkeleton);
                report.sampleAvatarPaths = SamplePaths(package.oracle.avatarSkeleton);
                report.problems = Validate(report);
                TryBuildUnityAvatar(package, report);
                report.status = report.problems.Count == 0 ? "ok" : "error";
                report.message = report.status == "ok"
                    ? "AvatarConstant oracle JSON can be consumed by Unity Editor."
                    : "AvatarConstant oracle JSON is incomplete; see problems.";
                return report;
            }
            catch (Exception ex)
            {
                report.status = "error";
                report.message = ex.ToString();
                return report;
            }
        }

        private static List<string> Validate(AvatarOracleProbeReport report)
        {
            var problems = new List<string>();
            if (report.humanBoneIndexCount <= 0)
            {
                problems.Add("Missing humanBoneIndex.");
            }

            if (report.humanSkeletonNodeCount <= 0)
            {
                problems.Add("Missing humanSkeleton nodes.");
            }

            if (report.humanSkeletonPoseCount < report.humanSkeletonNodeCount)
            {
                problems.Add("humanSkeleton pose count is smaller than node count.");
            }

            if (report.avatarSkeletonNodeCount <= 0)
            {
                problems.Add("Missing avatarSkeleton nodes.");
            }

            if (report.avatarDefaultPoseCount < report.avatarSkeletonNodeCount)
            {
                problems.Add("avatarSkeleton defaultPose count is smaller than node count.");
            }

            return problems;
        }

        private static void TryBuildUnityAvatar(AvatarOraclePackage package, AvatarOracleProbeReport report)
        {
            if (package == null || package.oracle == null || package.oracle.humanSkeleton == null)
            {
                return;
            }

            GameObject root = null;
            Avatar avatar = null;
            try
            {
                root = BuildSkeletonObject(package.avatar != null ? package.avatar.name : "AvatarOracle", package.oracle.humanSkeleton);
                var nodeNames = BuildNodeNames(package.oracle.humanSkeleton);
                var humanBones = BuildHumanBones(package.oracle, nodeNames);
                var skeletonBones = BuildSkeletonBonesFromTransforms(root);
                report.generatedHumanBoneCount = humanBones.Length;
                report.generatedSkeletonBoneCount = skeletonBones.Length;

                if (root == null || humanBones.Length == 0 || skeletonBones.Length == 0)
                {
                    report.avatarBuildMessage = "Skipped AvatarBuilder because generated skeleton or human bones are empty.";
                    return;
                }

                var human = package.oracle.human;
                var description = new HumanDescription
                {
                    human = humanBones,
                    skeleton = skeletonBones,
                    armStretch = human != null ? human.armStretch : 0.05f,
                    legStretch = human != null ? human.legStretch : 0.05f,
                    upperArmTwist = human != null ? human.armTwist : 0.5f,
                    lowerArmTwist = human != null ? human.foreArmTwist : 0.5f,
                    upperLegTwist = human != null ? human.upperLegTwist : 0.5f,
                    lowerLegTwist = human != null ? human.legTwist : 0.5f,
                    feetSpacing = human != null ? human.feetSpacing : 0,
                    hasTranslationDoF = human != null && human.hasTDoF,
                };

                avatar = AvatarBuilder.BuildHumanAvatar(root, description);
                if (avatar != null)
                {
                    avatar.name = (package.avatar != null && !string.IsNullOrWhiteSpace(package.avatar.name))
                        ? package.avatar.name + "_OracleRebuild"
                        : "AvatarOracleRebuild";
                }

                report.avatarBuildAttempted = true;
                report.avatarValid = avatar != null && avatar.isValid;
                report.avatarHuman = avatar != null && avatar.isHuman;
                report.avatarBuildMessage = avatar == null
                    ? "AvatarBuilder returned null."
                    : $"AvatarBuilder returned avatar. isValid={avatar.isValid}, isHuman={avatar.isHuman}.";
                if (!report.avatarValid || !report.avatarHuman)
                {
                    report.problems.Add(report.avatarBuildMessage);
                }
            }
            catch (Exception ex)
            {
                report.avatarBuildAttempted = true;
                report.avatarBuildMessage = ex.ToString();
                report.problems.Add("AvatarBuilder failed: " + ex.Message);
            }
            finally
            {
                if (avatar != null)
                {
                    UnityEngine.Object.DestroyImmediate(avatar);
                }
                if (root != null)
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }
            }
        }

        private static GameObject BuildSkeletonObject(string avatarName, AvatarOracleSkeleton skeleton)
        {
            if (skeleton == null || skeleton.nodes == null || skeleton.nodes.Length == 0)
            {
                return null;
            }

            var root = new GameObject(string.IsNullOrWhiteSpace(avatarName) ? "AvatarOracleSkeleton" : avatarName + "_OracleSkeleton");
            var nodeNames = BuildNodeNames(skeleton);
            var transforms = new Transform[skeleton.nodes.Length];
            for (var i = 0; i < skeleton.nodes.Length; i++)
            {
                var pose = skeleton.pose != null && i < skeleton.pose.Length ? skeleton.pose[i] : null;
                var go = new GameObject(nodeNames[i]);
                transforms[i] = go.transform;
                ApplyPose(go.transform, pose);
            }

            for (var i = 0; i < skeleton.nodes.Length; i++)
            {
                var node = skeleton.nodes[i];
                var parentId = node != null ? node.parentId : -1;
                transforms[i].SetParent(parentId >= 0 && parentId < transforms.Length ? transforms[parentId] : root.transform, false);
            }

            return root;
        }

        private static string[] BuildNodeNames(AvatarOracleSkeleton skeleton)
        {
            var names = new string[skeleton.nodes.Length];
            var used = new HashSet<string>();
            for (var i = 0; i < skeleton.nodes.Length; i++)
            {
                var raw = LastPathPart(skeleton.nodes[i] != null ? skeleton.nodes[i].path : null);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    raw = "node_" + i;
                }

                var name = raw;
                var suffix = 1;
                while (!used.Add(name))
                {
                    name = raw + "_" + suffix;
                    suffix++;
                }

                names[i] = name;
            }

            return names;
        }

        private static HumanBone[] BuildHumanBones(AvatarOracleData oracle, string[] nodeNames)
        {
            var result = new List<HumanBone>();
            var names = new HashSet<string>();
            var humanNames = AvatarConstantHumanBoneNames;
            var nodes = oracle.humanSkeleton != null ? oracle.humanSkeleton.nodes : null;
            if (oracle.humanBoneIndex == null || nodes == null || nodeNames == null)
            {
                return Array.Empty<HumanBone>();
            }

            for (var i = 0; i < oracle.humanBoneIndex.Length && i < humanNames.Length; i++)
            {
                var nodeIndex = oracle.humanBoneIndex[i];
                if (nodeIndex < 0 || nodeIndex >= nodes.Length)
                {
                    continue;
                }

                var boneName = nodeNames[nodeIndex];
                if (string.IsNullOrWhiteSpace(boneName) || !names.Add(boneName))
                {
                    continue;
                }

                result.Add(new HumanBone
                {
                    humanName = humanNames[i],
                    boneName = boneName,
                    limit = new HumanLimit { useDefaultValues = true },
                });
            }

            return result.ToArray();
        }

        private static SkeletonBone[] BuildSkeletonBonesFromTransforms(GameObject root)
        {
            if (root == null)
            {
                return Array.Empty<SkeletonBone>();
            }

            var result = new List<SkeletonBone>();
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (string.IsNullOrWhiteSpace(transform.name))
                {
                    continue;
                }

                result.Add(new SkeletonBone
                {
                    name = transform.name,
                    position = transform.localPosition,
                    rotation = transform.localRotation,
                    scale = transform.localScale,
                });
            }

            return result.ToArray();
        }

        private static void ApplyPose(Transform transform, AvatarOraclePose pose)
        {
            if (pose == null)
            {
                return;
            }

            transform.localPosition = ToVector3(pose.t, Vector3.zero);
            transform.localRotation = ToQuaternion(pose.q, Quaternion.identity);
            transform.localScale = ToVector3(pose.s, Vector3.one);
        }

        private static Vector3 ToVector3(float[] value, Vector3 fallback)
        {
            return value != null && value.Length >= 3
                ? new Vector3(value[0], value[1], value[2])
                : fallback;
        }

        private static Quaternion ToQuaternion(float[] value, Quaternion fallback)
        {
            return value != null && value.Length >= 4
                ? new Quaternion(value[0], value[1], value[2], value[3])
                : fallback;
        }

        private static string LastPathPart(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? null : parts.Last();
        }

        // AvatarConstant.m_Human.m_HumanBoneIndex 使用 Unity Humanoid 内部顺序。
        // 它和 HumanTrait.BoneName 的显示数组顺序不同，不能混用。
        private static readonly string[] AvatarConstantHumanBoneNames =
        {
            "Hips",
            "LeftUpperLeg",
            "RightUpperLeg",
            "LeftLowerLeg",
            "RightLowerLeg",
            "LeftFoot",
            "RightFoot",
            "Spine",
            "Chest",
            "UpperChest",
            "Neck",
            "Head",
            "LeftShoulder",
            "RightShoulder",
            "LeftUpperArm",
            "RightUpperArm",
            "LeftLowerArm",
            "RightLowerArm",
            "LeftHand",
            "RightHand",
            "LeftToes",
            "RightToes",
            "LeftEye",
            "RightEye",
            "Jaw",
        };

        private static int Count(Array array)
        {
            return array == null ? 0 : array.Length;
        }

        private static int CountMapped(int[] values)
        {
            if (values == null)
            {
                return 0;
            }

            var count = 0;
            foreach (var value in values)
            {
                if (value >= 0)
                {
                    count++;
                }
            }

            return count;
        }

        private static string[] SamplePaths(AvatarOracleSkeleton skeleton)
        {
            if (skeleton == null || skeleton.nodes == null)
            {
                return Array.Empty<string>();
            }

            var paths = new List<string>();
            foreach (var node in skeleton.nodes)
            {
                if (node == null || string.IsNullOrWhiteSpace(node.path))
                {
                    continue;
                }

                paths.Add(node.path);
                if (paths.Count >= 8)
                {
                    break;
                }
            }

            return paths.ToArray();
        }
    }

    [Serializable]
    public sealed class AvatarOracleProbeReport
    {
        public string status;
        public string message;
        public string oraclePath;
        public string avatarName;
        public long pathId;
        public bool hasHumanDescription;
        public int humanBoneIndexCount;
        public int humanBoneMappedCount;
        public int humanSkeletonNodeCount;
        public int humanSkeletonPoseCount;
        public int avatarSkeletonNodeCount;
        public int avatarDefaultPoseCount;
        public int rootMotionBoneIndex;
        public bool avatarBuildAttempted;
        public bool avatarValid;
        public bool avatarHuman;
        public int generatedHumanBoneCount;
        public int generatedSkeletonBoneCount;
        public string avatarBuildMessage;
        public string[] sampleHumanPaths;
        public string[] sampleAvatarPaths;
        public List<string> problems = new List<string>();
    }

    [Serializable]
    public sealed class AvatarOraclePackage
    {
        public int version;
        public string status;
        public AvatarOracleAvatarInfo avatar;
        public AvatarOracleData oracle;
    }

    [Serializable]
    public sealed class AvatarOracleAvatarInfo
    {
        public string name;
        public long pathId;
        public bool hasHumanDescription;
    }

    [Serializable]
    public sealed class AvatarOracleData
    {
        public int version;
        public string source;
        public int[] humanBoneIndex;
        public float[] humanBoneMass;
        public AvatarOracleHumanSettings human;
        public AvatarOracleSkeleton humanSkeleton;
        public AvatarOracleSkeleton avatarSkeleton;
        public AvatarOracleRootMotion rootMotion;
    }

    [Serializable]
    public sealed class AvatarOracleHumanSettings
    {
        public float scale;
        public float armTwist;
        public float foreArmTwist;
        public float upperLegTwist;
        public float legTwist;
        public float armStretch;
        public float legStretch;
        public float feetSpacing;
        public bool hasLeftHand;
        public bool hasRightHand;
        public bool hasTDoF;
    }

    [Serializable]
    public sealed class AvatarOracleSkeleton
    {
        public int nodeCount;
        public int axesCount;
        public AvatarOracleNode[] nodes;
        public AvatarOracleAxis[] axes;
        public AvatarOraclePose[] pose;
        public AvatarOraclePose[] defaultPose;
    }

    [Serializable]
    public sealed class AvatarOracleAxis
    {
        public int index;
        public float[] preQ;
        public float[] postQ;
        public float[] sign;
        public float[] limitMin;
        public float[] limitMax;
        public float length;
        public int type;
    }

    [Serializable]
    public sealed class AvatarOracleNode
    {
        public int index;
        public int parentId;
        public int axesId;
        public string path;
        public string name;
    }

    [Serializable]
    public sealed class AvatarOraclePose
    {
        public float[] t;
        public float[] q;
        public float[] s;
    }

    [Serializable]
    public sealed class AvatarOracleRootMotion
    {
        public int boneIndex;
        public AvatarOraclePose boneX;
        public int[] skeletonIndexArray;
        public AvatarOraclePose skeletonPose;
    }
}
