using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AnimeStudio.UnityBake
{
    public static class AnimeStudioGltfSkeletonBuilder
    {
        public static GameObject BuildFromRequest(AnimeStudioBakeRequest request)
        {
            var model = request.animeStudioAssets?.model;
            if (model == null || string.IsNullOrWhiteSpace(model.gltf) || !File.Exists(model.gltf))
            {
                return null;
            }

            var gltf = JsonUtility.FromJson<GltfRoot>(File.ReadAllText(model.gltf));
            if (gltf?.nodes == null || gltf.nodes.Length == 0)
            {
                return null;
            }

            var root = new GameObject(string.IsNullOrWhiteSpace(model.name) ? "AnimeStudioSkeleton" : model.name);
            var transforms = new Transform[gltf.nodes.Length];
            for (var i = 0; i < gltf.nodes.Length; i++)
            {
                var node = gltf.nodes[i];
                var go = new GameObject(string.IsNullOrWhiteSpace(node.name) ? $"node_{i}" : node.name);
                transforms[i] = go.transform;
                ApplyLocalTransform(go.transform, node);
            }

            var hasParent = new bool[gltf.nodes.Length];
            for (var i = 0; i < gltf.nodes.Length; i++)
            {
                var children = gltf.nodes[i].children ?? Array.Empty<int>();
                foreach (var child in children)
                {
                    if (child < 0 || child >= transforms.Length)
                    {
                        continue;
                    }
                    transforms[child].SetParent(transforms[i], false);
                    hasParent[child] = true;
                }
            }

            for (var i = 0; i < transforms.Length; i++)
            {
                if (!hasParent[i])
                {
                    transforms[i].SetParent(root.transform, false);
                }
            }

            var importedAvatar = LoadImportedAvatar(request);
            var rigRestPoseSource = importedAvatar != null
                ? "imported_unity_avatar_asset"
                : ApplyAvatarOraclePoseToTransforms(root, model.avatar);
            var metadata = root.AddComponent<AnimeStudioBakeRigMetadata>();
            metadata.restPoseSource = rigRestPoseSource;
            metadata.restPoseApplied = !string.IsNullOrWhiteSpace(rigRestPoseSource);

            var animator = root.AddComponent<Animator>();
            animator.avatar = importedAvatar != null ? importedAvatar : BuildAvatar(root, model.avatar);
            animator.applyRootMotion = true;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            return root;
        }

        private static Avatar LoadImportedAvatar(AnimeStudioBakeRequest request)
        {
            var path = request?.unityAssetPaths?.avatarAsset;
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var normalized = path.Replace('\\', '/');
            var avatar = AssetDatabase.LoadAssetAtPath<Avatar>(normalized);
            if (avatar == null)
            {
                Debug.LogError($"Imported Avatar asset not found: {normalized}");
                return null;
            }
            if (!avatar.isValid || !avatar.isHuman)
            {
                Debug.LogError($"Imported Avatar asset is not a valid Humanoid Avatar: {normalized}, isValid={avatar.isValid}, isHuman={avatar.isHuman}");
                return null;
            }

            return avatar;
        }

        private static Avatar BuildAvatar(GameObject root, AnimeStudioAvatarAsset avatarAsset)
        {
            if (avatarAsset?.humanBones == null || avatarAsset.humanBones.Length == 0)
            {
                return BuildAvatarFromOracle(root, avatarAsset);
            }

            var humanBones = new List<HumanBone>();
            foreach (var entry in avatarAsset.humanBones)
            {
                if (string.IsNullOrWhiteSpace(entry))
                {
                    continue;
                }
                var index = entry.IndexOf(':');
                if (index <= 0 || index >= entry.Length - 1)
                {
                    continue;
                }
                humanBones.Add(new HumanBone
                {
                    humanName = entry[..index],
                    boneName = entry[(index + 1)..],
                    limit = new HumanLimit { useDefaultValues = true },
                });
            }

            var skeleton = BuildSkeletonBones(root, avatarAsset);
            var settings = avatarAsset.humanDescription;

            var description = new HumanDescription
            {
                human = humanBones.ToArray(),
                skeleton = skeleton.ToArray(),
                armStretch = settings != null ? settings.armStretch : 0.05f,
                legStretch = settings != null ? settings.legStretch : 0.05f,
                upperArmTwist = settings != null ? settings.armTwist : 0.5f,
                lowerArmTwist = settings != null ? settings.foreArmTwist : 0.5f,
                upperLegTwist = settings != null ? settings.upperLegTwist : 0.5f,
                lowerLegTwist = settings != null ? settings.legTwist : 0.5f,
                feetSpacing = settings != null ? settings.feetSpacing : 0,
                hasTranslationDoF = settings != null && settings.hasTranslationDoF,
            };
            var avatar = AvatarBuilder.BuildHumanAvatar(root, description);
            avatar.name = string.IsNullOrWhiteSpace(avatarAsset.name) ? root.name + "Avatar" : avatarAsset.name;
            return avatar;
        }

        private static Avatar BuildAvatarFromOracle(GameObject root, AnimeStudioAvatarAsset avatarAsset)
        {
            var oracle = avatarAsset?.oracle;
            if (root == null || oracle == null || oracle.humanBoneIndex == null || oracle.humanSkeleton?.nodes == null)
            {
                return null;
            }

            var nodeNames = BuildOracleNodeNames(oracle.humanSkeleton);
            var humanBones = new List<HumanBone>();
            var used = new HashSet<string>();
            for (var i = 0; i < oracle.humanBoneIndex.Length && i < AvatarConstantHumanBoneNames.Length; i++)
            {
                var nodeIndex = oracle.humanBoneIndex[i];
                if (nodeIndex < 0 || nodeIndex >= nodeNames.Length)
                {
                    continue;
                }

                var humanName = AvatarConstantHumanBoneNames[i];
                var boneName = nodeNames[nodeIndex];
                if (string.IsNullOrWhiteSpace(boneName) || FindChildByName(root.transform, boneName) == null || !used.Add(boneName))
                {
                    continue;
                }

                humanBones.Add(new HumanBone
                {
                    humanName = humanName,
                    boneName = boneName,
                    limit = BuildHumanLimitFromOracle(oracle, nodeIndex),
                });
            }

            if (humanBones.Count == 0)
            {
                return null;
            }

            var skeleton = BuildSkeletonBonesFromOracle(root, avatarAsset);
            var human = oracle.human;
            var description = new HumanDescription
            {
                human = humanBones.ToArray(),
                skeleton = skeleton.ToArray(),
                armStretch = human != null ? human.armStretch : 0.05f,
                legStretch = human != null ? human.legStretch : 0.05f,
                upperArmTwist = human != null ? human.armTwist : 0.5f,
                lowerArmTwist = human != null ? human.foreArmTwist : 0.5f,
                upperLegTwist = human != null ? human.upperLegTwist : 0.5f,
                lowerLegTwist = human != null ? human.legTwist : 0.5f,
                feetSpacing = human != null ? human.feetSpacing : 0,
                hasTranslationDoF = human != null && human.hasTDoF,
            };

            var avatar = AvatarBuilder.BuildHumanAvatar(root, description);
            avatar.name = string.IsNullOrWhiteSpace(avatarAsset.name) ? root.name + "Avatar" : avatarAsset.name;
            if (!avatar.isValid || !avatar.isHuman)
            {
                Debug.LogError($"AvatarConstant oracle did not produce a valid Humanoid Avatar. avatar={avatar.name}, isValid={avatar.isValid}, isHuman={avatar.isHuman}");
            }
            return avatar;
        }

        private static HumanLimit BuildHumanLimitFromOracle(AvatarOracleData oracle, int humanNodeIndex)
        {
            if (UseDefaultHumanLimitsForOracle())
            {
                return new HumanLimit { useDefaultValues = true };
            }

            var node = oracle?.humanSkeleton?.nodes != null
                && humanNodeIndex >= 0
                && humanNodeIndex < oracle.humanSkeleton.nodes.Length
                    ? oracle.humanSkeleton.nodes[humanNodeIndex]
                    : null;
            var axes = oracle?.humanSkeleton?.axes;
            if (node == null || axes == null || node.axesId < 0 || node.axesId >= axes.Length)
            {
                return new HumanLimit { useDefaultValues = true };
            }

            var axis = axes[node.axesId];
            if (axis?.limitMin == null || axis.limitMax == null || axis.limitMin.Length < 3 || axis.limitMax.Length < 3)
            {
                return new HumanLimit { useDefaultValues = true };
            }

            // AvatarConstant 里的 limit 是弧度；AvatarBuilder 的 HumanLimit 使用角度。
            // 这里只把 Unity 原始关节限制交回 Unity，不尝试自己解 muscle 公式。
            return new HumanLimit
            {
                useDefaultValues = false,
                min = RadiansToDegrees(axis.limitMin),
                max = RadiansToDegrees(axis.limitMax),
                center = Vector3.zero,
                axisLength = axis.length > 0 ? axis.length : 0,
            };
        }

        private static Vector3 RadiansToDegrees(float[] value)
        {
            return new Vector3(
                value[0] * Mathf.Rad2Deg,
                value[1] * Mathf.Rad2Deg,
                value[2] * Mathf.Rad2Deg);
        }

        private static List<SkeletonBone> BuildSkeletonBones(GameObject root, AnimeStudioAvatarAsset avatarAsset)
        {
            if (avatarAsset?.skeletonBones != null && avatarAsset.skeletonBones.Length > 0)
            {
                return avatarAsset.skeletonBones
                    .Where(x => !string.IsNullOrWhiteSpace(x.name))
                    .Select(x => new SkeletonBone
                    {
                        name = x.name,
                        position = x.position.ToVector3(),
                        rotation = x.rotation.ToQuaternion(),
                        scale = x.scale.ToVector3(),
                    })
                    .ToList();
            }

            var skeleton = UseInternalSolverSkeletonPose()
                ? BuildSkeletonBonesFromInternalSolver(avatarAsset)
                : new List<SkeletonBone>();
            if (skeleton.Count > 0)
            {
                // 旧库常没有 HumanDescription.skeletonBones。诊断 bake 可以用 AvatarConstant
                // 的 defaultPose 做复建实验，但它不是 HumanDescription.skeletonBones，
                // 不能默认启用，更不能在 CLI 侧标成生产可信。
                AppendMissingTransformSkeletonBones(root, skeleton);
                return skeleton;
            }

            skeleton = new List<SkeletonBone>();
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                skeleton.Add(new SkeletonBone
                {
                    name = transform.name,
                    position = transform.localPosition,
                    rotation = transform.localRotation,
                    scale = transform.localScale,
                });
            }
            return skeleton;
        }

        private static string[] BuildOracleNodeNames(AvatarOracleSkeleton skeleton)
        {
            var nodes = skeleton?.nodes;
            if (nodes == null)
            {
                return Array.Empty<string>();
            }

            var names = new string[nodes.Length];
            var used = new HashSet<string>();
            for (var i = 0; i < nodes.Length; i++)
            {
                var raw = !string.IsNullOrWhiteSpace(nodes[i]?.path)
                    ? LastPathPart(nodes[i].path)
                    : nodes[i]?.name;
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

        private static List<SkeletonBone> BuildSkeletonBonesFromOracle(GameObject root, AnimeStudioAvatarAsset avatarAsset)
        {
            var skeleton = BuildSkeletonBonesFromOracleData(avatarAsset?.oracle);
            if (skeleton.Count > 0)
            {
                // AvatarConstant 里的 defaultPose 才是 Unity 原始 Avatar 解算参考。
                // glTF 当前 rest pose 只能用于补外层包装节点，不能拿来替代 Humanoid Avatar 参考姿态。
                AppendMissingTransformSkeletonBones(root, skeleton);
                return skeleton;
            }

            if (string.Equals(OraclePoseSourceMode(), "gltf_rest", StringComparison.OrdinalIgnoreCase))
            {
                return BuildCurrentTransformSkeletonBones(root);
            }

            skeleton = BuildSkeletonBonesFromInternalSolver(avatarAsset);
            if (skeleton.Count > 0)
            {
                AppendMissingTransformSkeletonBones(root, skeleton);
                return skeleton;
            }

            return BuildSkeletonBones(root, avatarAsset);
        }

        private static List<SkeletonBone> BuildSkeletonBonesFromOracleData(AvatarOracleData oracle)
        {
            if (oracle == null)
            {
                return new List<SkeletonBone>();
            }

            var mode = OraclePoseSourceMode();
            if (string.Equals(mode, "gltf_rest", StringComparison.OrdinalIgnoreCase))
            {
                return new List<SkeletonBone>();
            }
            if (string.Equals(mode, "human_pose", StringComparison.OrdinalIgnoreCase))
            {
                var humanPose = BuildSkeletonBonesFromOracleSkeleton(oracle.humanSkeleton, useDefaultPose: false);
                if (humanPose.Count > 0)
                {
                    return humanPose;
                }
            }
            if (string.Equals(mode, "avatar_pose", StringComparison.OrdinalIgnoreCase))
            {
                var avatarPose = BuildSkeletonBonesFromOracleSkeleton(oracle.avatarSkeleton, useDefaultPose: false);
                if (avatarPose.Count > 0)
                {
                    return avatarPose;
                }
            }

            var skeleton = BuildSkeletonBonesFromOracleSkeleton(oracle.avatarSkeleton, useDefaultPose: true);
            return skeleton.Count > 0
                ? skeleton
                : BuildSkeletonBonesFromOracleSkeleton(oracle.humanSkeleton, useDefaultPose: false);
        }

        private static List<SkeletonBone> BuildSkeletonBonesFromOracleSkeleton(AvatarOracleSkeleton skeleton, bool useDefaultPose)
        {
            var nodes = skeleton?.nodes;
            var pose = useDefaultPose ? skeleton?.defaultPose : skeleton?.pose;
            if (nodes == null || pose == null || nodes.Length == 0 || pose.Length < nodes.Length)
            {
                return new List<SkeletonBone>();
            }

            var result = new List<SkeletonBone>();
            for (var i = 0; i < nodes.Length; i++)
            {
                var node = nodes[i];
                var nodeName = !string.IsNullOrWhiteSpace(node?.name)
                    ? node.name
                    : LastPathPart(node?.path);
                if (string.IsNullOrWhiteSpace(nodeName))
                {
                    continue;
                }

                var xform = pose[i];
                result.Add(new SkeletonBone
                {
                    name = nodeName,
                    position = ToVector3(xform.t, Vector3.zero),
                    rotation = ToQuaternion(xform.q, Quaternion.identity),
                    scale = ToOracleScale(xform.s),
                });
            }

            return result;
        }

        private static Transform FindChildByName(Transform root, string name)
        {
            if (root == null || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (string.Equals(transform.name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return transform;
                }
            }

            return null;
        }

        private static List<SkeletonBone> BuildSkeletonBonesFromInternalSolver(AnimeStudioAvatarAsset avatarAsset)
        {
            var solver = avatarAsset?.internalSolver;
            var skeleton = solver?.avatarSkeleton;
            var nodes = skeleton?.nodes;
            var pose = skeleton?.defaultPose;
            if (nodes == null || pose == null || nodes.Length == 0 || pose.Length < nodes.Length)
            {
                skeleton = solver?.skeleton;
                nodes = skeleton?.nodes;
                pose = skeleton?.humanSkeletonPose;
            }
            if (nodes == null || pose == null || nodes.Length == 0 || pose.Length < nodes.Length)
            {
                return new List<SkeletonBone>();
            }

            // 原神等项目常没有 HumanDescription.skeletonBones，但 AvatarConstant 里有 Unity 原始 pose。
            // 优先用完整 avatarSkeleton.defaultPose；缺失时才退到 humanSkeletonPose。
            var result = new List<SkeletonBone>();
            for (var i = 0; i < nodes.Length; i++)
            {
                var node = nodes[i];
                var nodeName = !string.IsNullOrWhiteSpace(node?.name)
                    ? node.name
                    : LastPathPart(node?.path);
                if (string.IsNullOrWhiteSpace(nodeName))
                {
                    continue;
                }

                var xform = pose[i];
                result.Add(new SkeletonBone
                {
                    name = nodeName,
                    position = ToVector3(xform.t, Vector3.zero),
                    rotation = ToQuaternion(xform.q, Quaternion.identity),
                    scale = ToOracleScale(xform.s),
                });
            }

            return result;
        }

        private static string ApplyAvatarOraclePoseToTransforms(GameObject root, AnimeStudioAvatarAsset avatarAsset)
        {
            if (root == null)
            {
                return null;
            }
            if (string.Equals(OraclePoseSourceMode(), "gltf_rest", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var skeleton = BuildSkeletonBonesFromOracleData(avatarAsset?.oracle);
            var source = skeleton.Count > 0 ? "avatar_constant_oracle_default_pose" : null;
            if (skeleton.Count > 0 && !string.Equals(OraclePoseSourceMode(), "avatar_default", StringComparison.OrdinalIgnoreCase))
            {
                source = "avatar_constant_oracle_" + OraclePoseSourceMode();
            }
            if (skeleton.Count == 0)
            {
                skeleton = BuildSkeletonBonesFromInternalSolver(avatarAsset);
                source = skeleton.Count > 0 ? "avatar_internal_solver_default_pose" : null;
            }
            if (skeleton.Count == 0)
            {
                return null;
            }

            var byName = skeleton
                .Where(x => !string.IsNullOrWhiteSpace(x.name))
                .GroupBy(x => x.name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (string.IsNullOrWhiteSpace(transform.name) || !byName.TryGetValue(transform.name, out var bone))
                {
                    continue;
                }

                // Unity bake 采样记录的是 Transform 当前 local TRS。
                // 当 Avatar 使用 AvatarConstant defaultPose 构建时，临时采样骨架也必须切到同一套 rest pose，
                // 否则后续把采样姿态相对 rest 的 delta 写回 glTF 会把手脚方向带反。
                transform.localPosition = bone.position;
                transform.localRotation = bone.rotation;
                transform.localScale = bone.scale;
            }

            return source;
        }

        private static List<SkeletonBone> BuildCurrentTransformSkeletonBones(GameObject root)
        {
            var result = new List<SkeletonBone>();
            if (root == null)
            {
                return result;
            }

            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                result.Add(new SkeletonBone
                {
                    name = transform.name,
                    position = transform.localPosition,
                    rotation = transform.localRotation,
                    scale = transform.localScale,
                });
            }

            return result;
        }

        private static bool UseInternalSolverSkeletonPose()
        {
            var value = Environment.GetEnvironmentVariable("ANIMESTUDIO_USE_INTERNAL_SOLVER_SKELETON_POSE");
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static Vector3 ToOracleScale(float[] value)
        {
            // 诊断用：原神 AvatarConstant 里存在少量负 scale。
            // 负 scale 可能让 Unity 临时 Avatar 的左右肢体轴向被镜像，默认不改，开关开启时只验证这个根因。
            return ForceUnitScaleForOraclePose()
                ? Vector3.one
                : ToVector3(value, Vector3.one);
        }

        private static bool ForceUnitScaleForOraclePose()
        {
            var value = Environment.GetEnvironmentVariable("ANIMESTUDIO_AVATAR_ORACLE_FORCE_UNIT_SCALE");
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static bool UseDefaultHumanLimitsForOracle()
        {
            var value = Environment.GetEnvironmentVariable("ANIMESTUDIO_AVATAR_ORACLE_DEFAULT_LIMITS");
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static string OraclePoseSourceMode()
        {
            var value = Environment.GetEnvironmentVariable("ANIMESTUDIO_AVATAR_ORACLE_POSE_SOURCE");
            if (string.Equals(value, "human_pose", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "avatar_pose", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "gltf_rest", StringComparison.OrdinalIgnoreCase))
            {
                return value.Trim().ToLowerInvariant();
            }

            return "avatar_default";
        }

        private static string LastPathPart(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }
            var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? null : parts[^1];
        }

        // AvatarConstant.m_Human.m_HumanBoneIndex 使用 Unity Humanoid 内部顺序，
        // 不能直接用 HumanTrait.BoneName 的显示顺序。
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

        private static void AppendMissingTransformSkeletonBones(GameObject root, List<SkeletonBone> skeleton)
        {
            var names = new HashSet<string>(skeleton.Select(x => x.name).Where(x => !string.IsNullOrWhiteSpace(x)));
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (string.IsNullOrWhiteSpace(transform.name) || names.Contains(transform.name))
                {
                    continue;
                }

                // AvatarBuilder 要求 HumanBone 祖先链也在 Skeleton 里。外层 prefab/glTF 包装节点
                // 没有 AvatarConstant pose，就保留 glTF 重建出的本地 TRS。
                skeleton.Add(new SkeletonBone
                {
                    name = transform.name,
                    position = transform.localPosition,
                    rotation = transform.localRotation,
                    scale = transform.localScale,
                });
                names.Add(transform.name);
            }
        }

        private static void ApplyLocalTransform(Transform transform, GltfNode node)
        {
            if (node.translation != null && node.translation.Length >= 3)
            {
                transform.localPosition = GltfToUnityPosition(node.translation);
            }
            if (node.rotation != null && node.rotation.Length >= 4)
            {
                transform.localRotation = GltfToUnityRotation(node.rotation);
            }
            if (node.scale != null && node.scale.Length >= 3)
            {
                transform.localScale = new Vector3(node.scale[0], node.scale[1], node.scale[2]);
            }
        }

        private static Vector3 GltfToUnityPosition(float[] value)
        {
            return new Vector3(-value[0], value[1], value[2]);
        }

        private static Quaternion GltfToUnityRotation(float[] value)
        {
            return new Quaternion(value[0], -value[1], -value[2], value[3]);
        }

        [Serializable]
        private sealed class GltfRoot
        {
            public GltfNode[] nodes;
        }

        [Serializable]
        private sealed class GltfNode
        {
            public string name;
            public int[] children;
            public float[] translation;
            public float[] rotation;
            public float[] scale;
        }
    }
}
