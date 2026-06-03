using System;
using System.Collections.Generic;
using UnityEngine;

namespace AnimeStudio.UnityBake
{
    [Serializable]
    public sealed class AnimeStudioBakeRequest
    {
        public int version;
        public int frameRate = 30;
        public string outputJson;
        public string logJson;
        public UnityAssetPaths unityAssetPaths;
        public AnimeStudioAssets animeStudioAssets;
    }

    [Serializable]
    public sealed class UnityAssetPaths
    {
        public string modelPrefab;
        public string animationClip;
    }

    [Serializable]
    public sealed class AnimeStudioAssets
    {
        public AnimeStudioModelAsset model;
        public AnimeStudioAnimationAsset animation;
    }

    [Serializable]
    public sealed class AnimeStudioModelAsset
    {
        public string name;
        public string gltf;
        public string source;
        public string container;
        public string skeletonHash;
        public string[] bonePaths;
        public AnimeStudioAvatarAsset avatar;
    }

    [Serializable]
    public sealed class AnimeStudioAvatarAsset
    {
        public string name;
        public bool hasHumanDescription;
        public string[] humanBones;
    }

    [Serializable]
    public sealed class AnimeStudioAnimationAsset
    {
        public string name;
        public string anim;
        public string animationAsset;
        public string source;
        public string container;
        public string animationType;
        public bool hasMuscleClip;
        public bool requiresHumanoidBake;
    }

    [Serializable]
    public sealed class AnimeStudioBakeResult
    {
        public int version = 1;
        public string status;
        public string message;
        public string modelPrefab;
        public string animationClip;
        public string modelName;
        public string clipName;
        public float clipLength;
        public float frameRate;
        public bool isHumanMotion;
        public string avatarName;
        public bool avatarValid;
        public int sampleCount;
        public int transformCount;
        public int changedTrackCount;
        public float[] sampleTimes;
        public List<TransformTrack> tracks = new List<TransformTrack>();
    }

    [Serializable]
    public sealed class TransformTrack
    {
        public string path;
        public string name;
        public bool changed;
        public Vector3Key[] translations;
        public QuaternionKey[] rotations;
        public Vector3Key[] scales;
    }

    [Serializable]
    public struct Vector3Key
    {
        public float time;
        public float x;
        public float y;
        public float z;

        public Vector3Key(float time, Vector3 value)
        {
            this.time = time;
            x = value.x;
            y = value.y;
            z = value.z;
        }
    }

    [Serializable]
    public struct QuaternionKey
    {
        public float time;
        public float x;
        public float y;
        public float z;
        public float w;

        public QuaternionKey(float time, Quaternion value)
        {
            this.time = time;
            x = value.x;
            y = value.y;
            z = value.z;
            w = value.w;
        }
    }
}
