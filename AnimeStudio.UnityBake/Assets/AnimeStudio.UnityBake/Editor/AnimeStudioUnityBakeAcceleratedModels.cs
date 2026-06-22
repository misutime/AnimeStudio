using System;
using System.Collections.Generic;

namespace AnimeStudio.UnityBake
{
    [Serializable]
    public sealed class UnityBakeAcceleratedRequest
    {
        public int version = 1;
        public string mode = "UnityBakeAccelerated";
        public string avatarAsset;
        public string avatarKey;
        public string outputJson;
        public string[] jointPaths;
        public List<UnityBakeAcceleratedClipRequest> clips = new List<UnityBakeAcceleratedClipRequest>();
    }

    [Serializable]
    public sealed class UnityBakeAcceleratedClipRequest
    {
        public string clipKey;
        public string clipName;
        public float frameRate = 30;
        public List<UnityBakeAcceleratedPoseSample> samples = new List<UnityBakeAcceleratedPoseSample>();
    }

    [Serializable]
    public sealed class UnityBakeAcceleratedPoseSample
    {
        public float time;
        public Vector3Value bodyPosition;
        public QuaternionValue bodyRotation;
        public float[] muscles;
    }

    [Serializable]
    public sealed class UnityBakeAcceleratedResult
    {
        public int version = 1;
        public string mode = "UnityBakeAccelerated";
        public string status;
        public string message;
        public string unityVersion;
        public string avatarAsset;
        public string avatarName;
        public string avatarKey;
        public bool avatarValid;
        public bool avatarHuman;
        public string setPoseMethod;
        public int jointCount;
        public string[] jointPaths;
        public string[] muscleNames;
        public int clipCount;
        public int sampleCount;
        public bool writesLibraryIndex;
        public bool writesModelAnimations;
        public List<UnityBakeAcceleratedClipResult> clips = new List<UnityBakeAcceleratedClipResult>();
    }

    [Serializable]
    public sealed class UnityBakeAcceleratedClipResult
    {
        public string clipKey;
        public string clipName;
        public float frameRate;
        public int sampleCount;
        public List<UnityBakeAcceleratedPoseResult> samples = new List<UnityBakeAcceleratedPoseResult>();
    }

    [Serializable]
    public sealed class UnityBakeAcceleratedPoseResult
    {
        public float time;
        public string status;
        public string error;
        public int valueCount;
        public float[] values;
    }
}
