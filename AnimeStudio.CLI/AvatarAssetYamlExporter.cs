using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AnimeStudio.CLI
{
    internal static class AvatarAssetYamlExporter
    {
        public static void WriteAvatarAsset(Avatar avatar, string outputPath)
        {
            if (avatar == null)
            {
                throw new ArgumentNullException(nameof(avatar));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            var text = ConvertAvatarToYaml(avatar);
            File.WriteAllText(outputPath, text, Encoding.UTF8);
            WriteMetaIfMissing(outputPath);
        }

        public static string ConvertAvatarToYaml(Avatar avatar)
        {
            var sb = new StringBuilder(128 * 1024);
            using var writer = new StringWriter(sb);
            var yaml = new YAMLWriter();
            var document = new YAMLDocument();
            var root = document.CreateMappingRoot();
            root.Tag = ((int)ClassIDType.Avatar).ToString();
            root.Anchor = ((int)ClassIDType.Avatar * 100000).ToString();
            root.Add(ClassIDType.Avatar.ToString(), ExportAvatar(avatar));
            yaml.AddDocument(document);
            yaml.Write(writer);
            return sb.ToString();
        }

        private static YAMLMappingNode ExportAvatar(Avatar avatar)
        {
            var node = new YAMLMappingNode();
            node.Add("m_Name", avatar.m_Name ?? "");
            node.Add("m_AvatarSize", avatar.m_AvatarSize);
            node.Add("m_Avatar", ExportAvatarConstant(avatar.m_Avatar, avatar.version));
            node.Add("m_TOS", avatar.m_TOS?.ExportYAML() ?? new YAMLMappingNode());
            if (avatar.version[0] >= 2019 && avatar.m_HumanDescription != null)
            {
                node.Add("m_HumanDescription", ExportHumanDescription(avatar.m_HumanDescription));
            }

            return node;
        }

        private static YAMLMappingNode ExportAvatarConstant(AvatarConstant constant, int[] version)
        {
            var node = new YAMLMappingNode();
            node.AddSerializedVersion(version[0] > 4 || (version[0] == 4 && version[1] > 3) ? 3 : 1);
            node.Add("m_AvatarSkeleton", ExportData(ExportSkeleton(constant.m_AvatarSkeleton, version)));
            node.Add("m_AvatarSkeletonPose", ExportData(ExportSkeletonPose(constant.m_AvatarSkeletonPose, version)));
            node.Add("m_DefaultPose", ExportData(ExportSkeletonPose(constant.m_DefaultPose ?? constant.m_AvatarSkeletonPose, version)));
            node.Add("m_SkeletonNameIDArray", (constant.m_SkeletonNameIDArray ?? constant.m_AvatarSkeleton?.m_ID ?? Array.Empty<uint>()).ExportYAML(true));
            node.Add("m_Human", ExportData(ExportHuman(constant.m_Human, version)));
            node.Add("m_HumanSkeletonIndexArray", (constant.m_HumanSkeletonIndexArray ?? Array.Empty<int>()).ExportYAML(true));
            node.Add("m_HumanSkeletonReverseIndexArray", (constant.m_HumanSkeletonReverseIndexArray ?? BuildReverseIndex(constant)).ExportYAML(true));
            node.Add("m_RootMotionBoneIndex", constant.m_RootMotionBoneIndex);
            node.Add("m_RootMotionBoneX", ExportXForm(constant.m_RootMotionBoneX, version));
            node.Add("m_RootMotionSkeleton", ExportData(ExportSkeleton(constant.m_RootMotionSkeleton, version)));
            node.Add("m_RootMotionSkeletonPose", ExportData(ExportSkeletonPose(constant.m_RootMotionSkeletonPose, version)));
            node.Add("m_RootMotionSkeletonIndexArray", (constant.m_RootMotionSkeletonIndexArray ?? Array.Empty<int>()).ExportYAML(true));
            return node;
        }

        private static YAMLMappingNode ExportData(YAMLNode data)
        {
            var node = new YAMLMappingNode();
            node.Add("data", data ?? new YAMLMappingNode());
            return node;
        }

        private static int[] BuildReverseIndex(AvatarConstant constant)
        {
            var nodeCount = constant.m_AvatarSkeleton?.m_Node?.Count ?? 0;
            var result = Enumerable.Repeat(-1, nodeCount).ToArray();
            var forward = constant.m_HumanSkeletonIndexArray ?? Array.Empty<int>();
            for (var i = 0; i < forward.Length; i++)
            {
                var value = forward[i];
                if (value >= 0 && value < result.Length)
                {
                    result[value] = i;
                }
            }

            return result;
        }

        private static YAMLMappingNode ExportSkeleton(Skeleton skeleton, int[] version)
        {
            var node = new YAMLMappingNode();
            node.Add("m_Node", ExportSequence(skeleton?.m_Node, x => ExportNode(x)));
            node.Add("m_ID", (skeleton?.m_ID ?? Array.Empty<uint>()).ExportYAML(true));
            node.Add("m_AxesArray", ExportSequence(skeleton?.m_AxesArray, x => ExportAxes(x, version)));
            return node;
        }

        private static YAMLMappingNode ExportNode(Node value)
        {
            var node = new YAMLMappingNode();
            node.Add("m_ParentId", value.m_ParentId);
            node.Add("m_AxesId", value.m_AxesId);
            return node;
        }

        private static YAMLMappingNode ExportAxes(Axes axes, int[] version)
        {
            var node = new YAMLMappingNode();
            node.Add("m_PreQ", ExportVector4(axes.m_PreQ, version));
            node.Add("m_PostQ", ExportVector4(axes.m_PostQ, version));
            node.Add("m_Sgn", ExportVectorLike(axes.m_Sgn, version));
            node.Add("m_Limit", ExportLimit(axes.m_Limit, version));
            node.Add("m_Length", axes.m_Length);
            node.Add("m_Type", axes.m_Type);
            return node;
        }

        private static YAMLMappingNode ExportLimit(Limit limit, int[] version)
        {
            var node = new YAMLMappingNode();
            node.Add("m_Min", ExportVectorLike(limit?.m_Min, version));
            node.Add("m_Max", ExportVectorLike(limit?.m_Max, version));
            return node;
        }

        private static YAMLMappingNode ExportSkeletonPose(SkeletonPose pose, int[] version)
        {
            var node = new YAMLMappingNode();
            node.Add("m_X", ExportSequence(pose?.m_X, x => ExportXForm(x, version)));
            return node;
        }

        private static YAMLMappingNode ExportHuman(Human human, int[] version)
        {
            var node = new YAMLMappingNode();
            node.AddSerializedVersion(version[0] > 5 || (version[0] == 5 && version[1] >= 6) ? 2 : 1);
            node.Add("m_RootX", ExportXForm(human.m_RootX, version));
            node.Add("m_Skeleton", ExportData(ExportSkeleton(human.m_Skeleton, version)));
            node.Add("m_SkeletonPose", ExportData(ExportSkeletonPose(human.m_SkeletonPose, version)));
            node.Add("m_LeftHand", ExportData(ExportHand(human.m_LeftHand)));
            node.Add("m_RightHand", ExportData(ExportHand(human.m_RightHand)));
            node.Add("m_Handles", ExportSequence(human.m_Handles, x => ExportHandle(x, version)));
            node.Add("m_ColliderArray", ExportSequence(human.m_ColliderArray, x => ExportCollider(x, version)));
            node.Add("m_HumanBoneIndex", (human.m_HumanBoneIndex ?? Array.Empty<int>()).ExportYAML(true));
            node.Add("m_HumanBoneMass", ExportFloatSequence(human.m_HumanBoneMass));
            node.Add("m_ColliderIndex", (human.m_ColliderIndex ?? Array.Empty<int>()).ExportYAML(true));
            node.Add("m_Scale", human.m_Scale);
            node.Add("m_ArmTwist", human.m_ArmTwist);
            node.Add("m_ForeArmTwist", human.m_ForeArmTwist);
            node.Add("m_UpperLegTwist", human.m_UpperLegTwist);
            node.Add("m_LegTwist", human.m_LegTwist);
            node.Add("m_ArmStretch", human.m_ArmStretch);
            node.Add("m_LegStretch", human.m_LegStretch);
            node.Add("m_FeetSpacing", human.m_FeetSpacing);
            node.Add("m_HasLeftHand", human.m_HasLeftHand);
            node.Add("m_HasRightHand", human.m_HasRightHand);
            node.Add("m_HasTDoF", human.m_HasTDoF);
            return node;
        }

        private static YAMLMappingNode ExportHand(Hand hand)
        {
            var node = new YAMLMappingNode();
            node.Add("m_HandBoneIndex", (hand?.m_HandBoneIndex ?? Array.Empty<int>()).ExportYAML(true));
            return node;
        }

        private static YAMLMappingNode ExportHandle(Handle handle, int[] version)
        {
            var node = new YAMLMappingNode();
            node.Add("m_X", ExportXForm(handle.m_X, version));
            node.Add("m_ParentHumanIndex", handle.m_ParentHumanIndex);
            node.Add("m_ID", handle.m_ID);
            return node;
        }

        private static YAMLMappingNode ExportCollider(Collider collider, int[] version)
        {
            var node = new YAMLMappingNode();
            node.Add("m_X", ExportXForm(collider.m_X, version));
            node.Add("m_Type", collider.m_Type);
            node.Add("m_XMotionType", collider.m_XMotionType);
            node.Add("m_YMotionType", collider.m_YMotionType);
            node.Add("m_ZMotionType", collider.m_ZMotionType);
            node.Add("m_MinLimitX", collider.m_MinLimitX);
            node.Add("m_MaxLimitX", collider.m_MaxLimitX);
            node.Add("m_MaxLimitY", collider.m_MaxLimitY);
            node.Add("m_MaxLimitZ", collider.m_MaxLimitZ);
            return node;
        }

        private static YAMLMappingNode ExportHumanDescription(HumanDescription description)
        {
            var node = new YAMLMappingNode();
            node.AddSerializedVersion(3);
            node.Add("m_Human", ExportSequence(description.m_Human, ExportHumanBone));
            node.Add("m_Skeleton", ExportSequence(description.m_Skeleton, ExportSkeletonBone));
            node.Add("m_ArmTwist", description.m_ArmTwist);
            node.Add("m_ForeArmTwist", description.m_ForeArmTwist);
            node.Add("m_UpperLegTwist", description.m_UpperLegTwist);
            node.Add("m_LegTwist", description.m_LegTwist);
            node.Add("m_ArmStretch", description.m_ArmStretch);
            node.Add("m_LegStretch", description.m_LegStretch);
            node.Add("m_FeetSpacing", description.m_FeetSpacing);
            node.Add("m_GlobalScale", description.m_GlobalScale);
            node.Add("m_RootMotionBoneName", description.m_RootMotionBoneName ?? "");
            node.Add("m_HasTranslationDoF", description.m_HasTranslationDoF);
            node.Add("m_HasExtraRoot", description.m_HasExtraRoot);
            node.Add("m_SkeletonHasParents", description.m_SkeletonHasParents);
            return node;
        }

        private static YAMLMappingNode ExportHumanBone(HumanBone bone)
        {
            var node = new YAMLMappingNode();
            node.Add("m_BoneName", bone.m_BoneName ?? "");
            node.Add("m_HumanName", bone.m_HumanName ?? "");
            node.Add("m_Limit", ExportSkeletonBoneLimit(bone.m_Limit));
            return node;
        }

        private static YAMLMappingNode ExportSkeletonBone(SkeletonBone bone)
        {
            var node = new YAMLMappingNode();
            node.Add("m_Name", bone.m_Name ?? "");
            node.Add("m_ParentName", bone.m_ParentName ?? "");
            node.Add("m_Position", bone.m_Position.ExportYAML(null));
            node.Add("m_Rotation", bone.m_Rotation.ExportYAML(null));
            node.Add("m_Scale", bone.m_Scale.ExportYAML(null));
            return node;
        }

        private static YAMLMappingNode ExportSkeletonBoneLimit(SkeletonBoneLimit limit)
        {
            var node = new YAMLMappingNode();
            node.Add("m_Min", limit.m_Min.ExportYAML(null));
            node.Add("m_Max", limit.m_Max.ExportYAML(null));
            node.Add("m_Value", limit.m_Value.ExportYAML(null));
            node.Add("m_Length", limit.m_Length);
            node.Add("m_Modified", limit.m_Modified);
            return node;
        }

        private static YAMLMappingNode ExportXForm(XForm xform, int[] version)
        {
            var node = new YAMLMappingNode();
            node.Add("t", xform.t.ExportYAML(version));
            node.Add("q", xform.q.ExportYAML(version));
            node.Add("s", xform.s.ExportYAML(version));
            return node;
        }

        private static YAMLMappingNode ExportVector4(Vector4 value, int[] version)
        {
            var node = new YAMLMappingNode { Style = MappingStyle.Flow };
            node.Add("x", value.X);
            node.Add("y", value.Y);
            node.Add("z", value.Z);
            node.Add("w", value.W);
            return node;
        }

        private static YAMLNode ExportVectorLike(object value, int[] version)
        {
            return value switch
            {
                Vector3 v3 => v3.ExportYAML(version),
                Vector4 v4 => ExportVector4(v4, version),
                null => Vector3.Zero.ExportYAML(version),
                _ => new YAMLMappingNode(),
            };
        }

        private static YAMLSequenceNode ExportFloatSequence(IReadOnlyList<float> values)
        {
            var node = new YAMLSequenceNode(SequenceStyle.Block);
            if (values == null)
            {
                return node;
            }

            foreach (var value in values)
            {
                node.Add(value);
            }

            return node;
        }

        private static YAMLSequenceNode ExportSequence<T>(IEnumerable<T> values, Func<T, YAMLNode> convert)
        {
            var node = new YAMLSequenceNode(SequenceStyle.Block);
            if (values == null)
            {
                return node;
            }

            foreach (var value in values)
            {
                node.Add(convert(value));
            }

            return node;
        }

        private static void WriteMetaIfMissing(string assetPath)
        {
            var metaPath = assetPath + ".meta";
            if (File.Exists(metaPath))
            {
                return;
            }

            var guid = Guid.NewGuid().ToString("N");
            File.WriteAllText(
                metaPath,
                "fileFormatVersion: 2\n"
                + $"guid: {guid}\n"
                + "NativeFormatImporter:\n"
                + "  externalObjects: {}\n"
                + "  mainObjectFileID: 0\n"
                + "  userData: \n"
                + "  assetBundleName: \n"
                + "  assetBundleVariant: \n",
                Encoding.UTF8);
        }
    }
}
