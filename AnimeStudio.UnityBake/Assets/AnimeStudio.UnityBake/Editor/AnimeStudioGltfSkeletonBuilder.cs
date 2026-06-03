using System;
using System.Collections.Generic;
using System.IO;
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

            var animator = root.AddComponent<Animator>();
            animator.avatar = BuildAvatar(root, model.avatar);
            animator.applyRootMotion = true;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            return root;
        }

        private static Avatar BuildAvatar(GameObject root, AnimeStudioAvatarAsset avatarAsset)
        {
            if (avatarAsset?.humanBones == null || avatarAsset.humanBones.Length == 0)
            {
                return null;
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

            var skeleton = new List<SkeletonBone>();
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

            var description = new HumanDescription
            {
                human = humanBones.ToArray(),
                skeleton = skeleton.ToArray(),
                armStretch = 0.05f,
                legStretch = 0.05f,
                upperArmTwist = 0.5f,
                lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f,
                lowerLegTwist = 0.5f,
                feetSpacing = 0,
                hasTranslationDoF = false,
            };
            var avatar = AvatarBuilder.BuildHumanAvatar(root, description);
            avatar.name = string.IsNullOrWhiteSpace(avatarAsset.name) ? root.name + "Avatar" : avatarAsset.name;
            return avatar;
        }

        private static void ApplyLocalTransform(Transform transform, GltfNode node)
        {
            if (node.translation != null && node.translation.Length >= 3)
            {
                transform.localPosition = new Vector3(node.translation[0], node.translation[1], node.translation[2]);
            }
            if (node.rotation != null && node.rotation.Length >= 4)
            {
                transform.localRotation = new Quaternion(node.rotation[0], node.rotation[1], node.rotation[2], node.rotation[3]);
            }
            if (node.scale != null && node.scale.Length >= 3)
            {
                transform.localScale = new Vector3(node.scale[0], node.scale[1], node.scale[2]);
            }
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
