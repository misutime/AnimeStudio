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
        public bool probeMuscles;
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
        // 可选：UnityRipper/uTinyRipper 恢复出的原始 Avatar 资产。
        // 有它时优先交给 Unity 自己的 Humanoid oracle，不再用 HumanDescription 猜一个 Avatar。
        public string avatarAsset;
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
        public AnimeStudioAvatarSkeletonBone[] skeletonBones;
        public AvatarOracleData oracle;
        public AnimeStudioAvatarInternalSolver internalSolver;
        public AnimeStudioHumanDescriptionSettings humanDescription;
    }

    [Serializable]
    public sealed class AnimeStudioAvatarInternalSolver
    {
        public AnimeStudioAvatarInternalSkeleton skeleton;
        public AnimeStudioAvatarInternalSkeleton avatarSkeleton;
    }

    [Serializable]
    public sealed class AnimeStudioAvatarInternalSkeleton
    {
        public AnimeStudioAvatarInternalNode[] nodes;
        public AnimeStudioAvatarPose[] pose;
        public AnimeStudioAvatarPose[] defaultPose;
        public AnimeStudioAvatarPose[] humanSkeletonPose;
        public AnimeStudioAvatarPose[] avatarDefaultPose;
    }

    [Serializable]
    public sealed class AnimeStudioAvatarInternalNode
    {
        public int index;
        public int parentId;
        public string path;
        public string name;
    }

    [Serializable]
    public sealed class AnimeStudioAvatarPose
    {
        public float[] t;
        public float[] q;
        public float[] s;
    }

    [Serializable]
    public sealed class AnimeStudioAvatarSkeletonBone
    {
        public string name;
        public string parentName;
        public Vector3Value position;
        public QuaternionValue rotation;
        public Vector3Value scale;
    }

    [Serializable]
    public sealed class AnimeStudioHumanDescriptionSettings
    {
        public float armTwist;
        public float foreArmTwist;
        public float upperLegTwist;
        public float legTwist;
        public float armStretch;
        public float legStretch;
        public float feetSpacing;
        public float globalScale;
        public string rootMotionBoneName;
        public bool hasTranslationDoF;
        public bool hasExtraRoot;
        public bool skeletonHasParents;
    }

    [Serializable]
    public struct Vector3Value
    {
        public float x;
        public float y;
        public float z;
        public Vector3 ToVector3() => new Vector3(x, y, z);
    }

    [Serializable]
    public struct QuaternionValue
    {
        public float x;
        public float y;
        public float z;
        public float w;
        public Quaternion ToQuaternion() => new Quaternion(x, y, z, w);
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
        public int helperVersion = 2;
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
        public string requestedAvatarAsset;
        public string importedAvatarAsset;
        public bool importedAvatarAssetValid;
        public string clipFilterMode;
        public int clipFilterRemovedTransformCurveCount;
        public int clipFilterRemovedAnimatorCurveCount;
        public int clipFilterRemovedObjectReferenceCurveCount;
        public string rigRestPoseSource;
        public bool rigRestPoseApplied;
        public int sampleCount;
        public int transformCount;
        public int changedTrackCount;
        public int muscleProbeCount;
        public float[] sampleTimes;
        public List<SampleBounds> sampleBounds = new List<SampleBounds>();
        public List<TransformTrack> tracks = new List<TransformTrack>();
        public List<MuscleProbe> muscleProbes = new List<MuscleProbe>();
        public List<MuscleProbe> internalAvatarPoseMuscleProbes = new List<MuscleProbe>();
        public List<MuscleCombinationProbe> muscleCombinationProbes = new List<MuscleCombinationProbe>();
        public List<MuscleCombinationProbe> internalAvatarPoseMuscleCombinationProbes = new List<MuscleCombinationProbe>();
        public string[] muscleNames;
        public List<HumanoidPoseSample> humanoidPoseSamples = new List<HumanoidPoseSample>();
        public List<EditorCurveTrack> editorCurveTracks = new List<EditorCurveTrack>();
        public List<ProbeRotationTrack> editorCurveHumanPoseRotations = new List<ProbeRotationTrack>();
        public List<TransformTrack> editorCurveSetHumanPoseTransformTracks = new List<TransformTrack>();
        public List<InternalAvatarPoseSnapshot> internalAvatarPoseSnapshots = new List<InternalAvatarPoseSnapshot>();
        public List<InternalAvatarPoseTimelineSample> internalAvatarPoseTimeline = new List<InternalAvatarPoseTimelineSample>();
    }

    public sealed class AnimeStudioBakeRigMetadata : MonoBehaviour
    {
        public string restPoseSource;
        public bool restPoseApplied;
    }

    [Serializable]
    public sealed class SampleBounds
    {
        public float time;
        public Vector3Key center;
        public Vector3Key size;
        public bool hasRenderer;
        public int rendererCount;
    }

    [Serializable]
    public sealed class TransformTrack
    {
        public string path;
        public string name;
        public bool changed;
        public Vector3Key restTranslation;
        public QuaternionKey restRotation;
        public Vector3Key restScale;
        public Vector3Key[] translations;
        public QuaternionKey[] rotations;
        public Vector3Key[] scales;
    }

    [Serializable]
    public sealed class MuscleProbe
    {
        public int muscleIndex;
        public string muscleName;
        public float baseValue;
        public float value;
        public int changedTrackCount;
        public List<ProbeRotationTrack> rotations = new List<ProbeRotationTrack>();
    }

    [Serializable]
    public sealed class MuscleCombinationProbe
    {
        public string probeName;
        public MuscleProbeValue[] muscles;
        public int changedTrackCount;
        public List<ProbeRotationTrack> rotations = new List<ProbeRotationTrack>();
        public List<ProbeTranslationTrack> translations = new List<ProbeTranslationTrack>();
    }

    [Serializable]
    public sealed class MuscleProbeValue
    {
        public int muscleIndex;
        public string muscleName;
        public float baseValue;
        public float value;
    }

    [Serializable]
    public sealed class ProbeRotationTrack
    {
        public string path;
        public string name;
        public QuaternionKey baseRotation;
        public QuaternionKey rotation;
    }

    [Serializable]
    public sealed class ProbeTranslationTrack
    {
        public string path;
        public string name;
        public Vector3Key baseTranslation;
        public Vector3Key translation;
    }

    [Serializable]
    public sealed class HumanoidPoseSample
    {
        public float time;
        public Vector3Key bodyPosition;
        public QuaternionKey bodyRotation;
        public float[] muscles;
    }

    [Serializable]
    public sealed class EditorCurveTrack
    {
        public string path;
        public string type;
        public string propertyName;
        public string source;
        public FloatKey[] values;
    }

    [Serializable]
    public sealed class InternalAvatarPoseSnapshot
    {
        public string label;
        public string status;
        public int requestedLength;
        public int valueCount;
        public int nonZeroCount;
        public string error;
        public string[] jointPaths;
        public float[] values;
    }

    [Serializable]
    public sealed class InternalAvatarPoseTimelineSample
    {
        public float time;
        public string status;
        public int requestedLength;
        public int valueCount;
        public int nonZeroCount;
        public string error;
        public string[] jointPaths;
        public float[] values;
    }

    [Serializable]
    public struct FloatKey
    {
        public float time;
        public float value;

        public FloatKey(float time, float value)
        {
            this.time = time;
            this.value = value;
        }
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
