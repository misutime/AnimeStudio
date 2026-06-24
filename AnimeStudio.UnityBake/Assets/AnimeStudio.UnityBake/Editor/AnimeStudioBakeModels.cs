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
        // 显式诊断：用 editor curve 的 Hand/Foot IK goal 驱动 Animator IK pass。
        // 只用于 UnityOracle/求解器对照，结果必须继续走视觉和生产门禁。
        public bool enableEditorCurveIkGoalDriver;
        // 显式诊断：把 normally skipped 但能解析出 motion/mask 的 recovered controller layer
        // 放进采样计划做对照。它只用于定位 layer/BlendTree 语义，不能作为生产通过证据。
        public bool sampleRecoverableSkippedLayersDiagnostic;
        public bool rebuildEditorCurveClip;
        public bool ignoreImportedAvatar;
        // 诊断采样模式。transform_only 表示只采样主 clip 的 Transform 曲线，
        // Humanoid/Muscle 曲线暂不进入结果，避免异常 muscle 覆盖可用 TRS。
        public string clipFilterMode;
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
        public string animatorController;
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
        public AnimeStudioAnimatorControllerContext animatorControllerContext;
    }

    [Serializable]
    public sealed class AnimeStudioAnimatorControllerContext
    {
        public string source;
        public int controllerClipIndex;
        public int stateMachineIndex;
        public int stateIndex;
        public string stateName;
        public string statePath;
        public string stateFullPath;
        public float stateSpeed = 1f;
        public float stateCycleOffset;
        public bool stateLoop;
        public bool stateMirror;
        public int blendTreeIndex;
        public int nodeIndex;
        public int nodeBlendType;
        public int nodeClipId;
        public int nodeClipIndex;
        public float nodeDuration = 1f;
        public float nodeCycleOffset;
        public bool nodeMirror;
        public AnimeStudioAnimatorControllerLayerClip[] additionalLayerClips;
        public AnimeStudioAnimatorControllerLayerWarning[] additionalLayerContextWarnings;
    }

    [Serializable]
    public sealed class AnimeStudioAnimatorControllerLayerClip
    {
        public string name;
        public string file;
        public long pathId;
        public string unityAssetPath;
        public int layerIndex;
        public int layerBlendingMode;
        public float layerDefaultWeight = 1f;
        public AnimeStudioHumanPoseMask layerBodyMask;
        public AnimeStudioSkeletonMask layerSkeletonMask;
        public string stateName;
        public string statePath;
        public string stateFullPath;
        public float stateSpeed = 1f;
        public float stateCycleOffset;
        public bool stateLoop;
        public bool stateMirror;
        public float nodeCycleOffset;
        public bool nodeMirror;
    }

    [Serializable]
    public sealed class AnimeStudioAnimatorControllerLayerWarning
    {
        public int layerIndex;
        public int clipCount;
        public string reason;
        public string rule;
    }

    [Serializable]
    public sealed class AnimeStudioHumanPoseMask
    {
        public uint word0;
        public uint word1;
        public uint word2;
        public bool isEmpty;
        public string[] rawHex;
    }

    [Serializable]
    public sealed class AnimeStudioSkeletonMask
    {
        public int count;
        public int nonZeroCount;
        public AnimeStudioSkeletonMaskEntry[] entries;
    }

    [Serializable]
    public sealed class AnimeStudioSkeletonMaskEntry
    {
        public uint pathHash;
        public string pathHashHex;
        public string path;
        public float weight;
    }

    [Serializable]
    public sealed class AnimeStudioBakeResult
    {
        public int version = 1;
        public int helperVersion = 4;
        public string status;
        public string message;
        public string modelPrefab;
        public string animationClip;
        public string requestedAnimationClip;
        public string importedAnimationClip;
        public string animationClipSource;
        public string requestedAnimatorController;
        public string animatorControllerSamplingMode;
        public string animatorControllerSamplingState;
        public string animatorControllerSamplingAsset;
        public string animatorControllerSamplingMessage;
        public int animatorControllerAdditionalLayerMaskCount;
        public int animatorControllerAdditionalLayerSkeletonMaskEntryCount;
        public bool animatorControllerLayerMasksApplied;
        public string animatorControllerLayerMaskWarning;
        public bool requestEnableEditorCurveIkGoalDriver;
        public bool effectiveEditorCurveIkGoalDriver;
        public bool requestSampleRecoverableSkippedLayersDiagnostic;
        public int animatorControllerIkPassEnabledLayerCount;
        public string animatorControllerIkPassMessage;
        public bool animatorControllerIkPassEffectiveForSampling;
        public string animatorControllerIkPassSamplingMode;
        public string animatorControllerIkPassSamplingWarning;
        public bool animatorControllerLayerMixerBypassesControllerBlendTrees;
        public string animatorControllerFidelityWarning;
        public int animatorControllerUnmaskedOverrideLayerCount;
        public string[] animatorControllerUnmaskedOverrideLayerNames;
        public int animatorControllerSkippedUntrustedLayerCount;
        public string[] animatorControllerSkippedUntrustedLayerNames;
        public string[] animatorControllerSkippedUntrustedLayerReasons;
        public List<AnimatorControllerSkippedLayerDiagnostic> animatorControllerSkippedUntrustedLayerDiagnostics = new List<AnimatorControllerSkippedLayerDiagnostic>();
        public int animatorControllerDiagnosticSampledSkippedLayerCount;
        public string[] animatorControllerDiagnosticSampledSkippedLayerNames;
        public List<AnimatorControllerLayerDiagnostic> animatorControllerLayerDiagnostics = new List<AnimatorControllerLayerDiagnostic>();
        public List<AnimatorControllerParameterDiagnostic> animatorControllerParameterDiagnostics = new List<AnimatorControllerParameterDiagnostic>();
        public AnimatorControllerRuntimeParameterSummary animatorControllerRuntimeParameterSummary;
        public List<AnimatorControllerBlendTreeDiagnostic> animatorControllerBlendTreeDiagnostics = new List<AnimatorControllerBlendTreeDiagnostic>();
        public string modelName;
        public string clipName;
        public float clipLength;
        public float frameRate;
        public bool isHumanMotion;
        public string avatarName;
        public bool avatarValid;
        public string runtimeRootPath;
        public string requestedAvatarAsset;
        public string importedAvatarAsset;
        public bool importedAvatarAssetValid;
        public ImportedAvatarBindingReport importedAvatarBinding;
        public string clipFilterMode;
        public int clipFilterRemovedTransformCurveCount;
        public int clipFilterRemovedAnimatorCurveCount;
        public int clipFilterRemovedObjectReferenceCurveCount;
        public bool requestRebuildEditorCurveClip;
        public bool requestIgnoreImportedAvatar;
        public string clipRebuildMode;
        public bool clipRebuildAttempted;
        public bool clipRebuildSucceeded;
        public bool clipRebuildIsHumanMotion;
        public int clipRebuildCurveCount;
        public string clipRebuildAssetPath;
        public string clipRebuildMessage;
        public string rigRestPoseSource;
        public bool rigRestPoseApplied;
        public int sampleCount;
        public int transformCount;
        public int changedTrackCount;
        public int muscleProbeCount;
        public float[] sampleTimes;
        public List<SampleBounds> sampleBounds = new List<SampleBounds>();
        public List<TransformTrack> tracks = new List<TransformTrack>();
        public List<HumanoidBoneDiagnostic> humanoidBoneDiagnostics = new List<HumanoidBoneDiagnostic>();
        public List<MuscleProbe> muscleProbes = new List<MuscleProbe>();
        public List<MuscleProbe> internalAvatarPoseMuscleProbes = new List<MuscleProbe>();
        public List<MuscleCombinationProbe> muscleCombinationProbes = new List<MuscleCombinationProbe>();
        public List<MuscleCombinationProbe> internalAvatarPoseMuscleCombinationProbes = new List<MuscleCombinationProbe>();
        public string[] muscleNames;
        public List<HumanoidPoseSample> humanoidPoseSamples = new List<HumanoidPoseSample>();
        public List<EditorCurveTrack> editorCurveTracks = new List<EditorCurveTrack>();
        public EditorCurveHumanPoseDiagnostic editorCurveHumanPoseDiagnostic;
        public EditorCurveIkGoalDriverDiagnostic editorCurveIkGoalDriverDiagnostic;
        public List<ProbeRotationTrack> editorCurveHumanPoseRotations = new List<ProbeRotationTrack>();
        public List<TransformTrack> editorCurveSetHumanPoseTransformTracks = new List<TransformTrack>();
        public List<TransformTrack> animationModeSampleClipTransformTracks = new List<TransformTrack>();
        public List<InternalAvatarPoseSnapshot> internalAvatarPoseSnapshots = new List<InternalAvatarPoseSnapshot>();
        public List<InternalAvatarPoseTimelineSample> internalAvatarPoseTimeline = new List<InternalAvatarPoseTimelineSample>();
    }

    [Serializable]
    public sealed class EditorCurveHumanPoseDiagnostic
    {
        public string rule;
        public string bodyPositionCurveSource;
        public string bodyRotationCurveSource;
        public bool bodyPositionApplied;
        public bool bodyRotationApplied;
        public int muscleCurveCount;
        public int dynamicMuscleCurveCount;
        public int motionTCurveCount;
        public int dynamicMotionTCurveCount;
        public int motionQCurveCount;
        public int dynamicMotionQCurveCount;
        public int rootTCurveCount;
        public int dynamicRootTCurveCount;
        public int rootQCurveCount;
        public int dynamicRootQCurveCount;
        public int limbGoalCurveCount;
        public int dynamicLimbGoalCurveCount;
        public int tdofCurveCount;
        public int dynamicTdofCurveCount;
        public List<EditorCurveVector3Summary> tdofVector3Summaries = new List<EditorCurveVector3Summary>();
        public string warning;
    }

    [Serializable]
    public sealed class EditorCurveVector3Summary
    {
        public string prefix;
        public string[] properties;
        public bool hasX;
        public bool hasY;
        public bool hasZ;
        public bool hasAllAxes;
        public int curveCount;
        public int dynamicCurveCount;
        public int sampleCount;
        public Vector3Range valueRange = new Vector3Range();
        public float valueRangeSpan;
        public Vector3Value firstValue;
        public Vector3Value midValue;
        public Vector3Value lastValue;
        public float minLength;
        public float maxLength;
        public float maxDeltaFromFirst;
        public bool dynamicVector;
    }

    [Serializable]
    public sealed class AnimatorControllerLayerDiagnostic
    {
        public int layerIndex;
        public int sourceLayerIndex;
        public string layerName;
        public string stateName;
        public string motionName;
        public string requestedClipName;
        public string controllerMotionName;
        public bool requestedClipOverridesControllerMotion;
        public string motionSelectionRule;
        public int sourceStateMachineIndex;
        public int stateMachineMotionSetIndex;
        public long layerBinding;
        public float weight;
        public bool isAdditive;
        public bool iKPass;
        public bool effectiveIkPassForDiagnostic;
        public string ikPassSamplingMode;
        public bool iKOnFeet;
        public bool syncedLayerAffectsTiming;
        public bool hasRequestLayerContext;
        public bool diagnosticSampledSkippedLayer;
        public bool hasHumanPoseMask;
        public bool hasSkeletonMask;
        public int skeletonMaskEntryCount;
        public bool maskApplied;
        public string rule;
    }

    [Serializable]
    public sealed class AnimatorControllerSkippedLayerDiagnostic
    {
        public int recoveredLayerIndex;
        public int sourceLayerIndex;
        public string layerName;
        public string stateName;
        public string reason;
        public string blendingMode;
        public float defaultWeight;
        public bool isAdditive;
        public bool iKPass;
        public bool iKOnFeet;
        public bool hasLayerMetadata;
        public bool hasRequestLayerContext;
        public bool hasHumanPoseMask;
        public bool hasSkeletonMask;
        public int skeletonMaskEntryCount;
        public string recoverableMotionName;
        public bool hasBlendTree;
        public string blendTreeName;
        public string blendType;
        public string blendParameter;
        public float defaultParameterValue;
        public string defaultParameterSource;
        public string selectedChildMotionName;
        public float selectedChildThreshold;
        public string selectedChildSelectionRule;
        public string rule;
    }

    [Serializable]
    public sealed class AnimatorControllerParameterDiagnostic
    {
        public string name;
        public string type;
        public float defaultFloat;
        public int defaultInt;
        public bool defaultBool;
        public string defaultSource;
        public string runtimeRoleHint;
        public bool possibleRuntimeWeight;
        public bool possibleLayerWeight;
        public bool possibleIkWeight;
        public bool possibleLookAtWeight;
        public bool defaultNonZero;
        public string rule;
    }

    [Serializable]
    public sealed class AnimatorControllerRuntimeParameterSummary
    {
        public int totalParameterCount;
        public int possibleRuntimeWeightCount;
        public int possibleLayerWeightCount;
        public int possibleIkWeightCount;
        public int possibleLookAtWeightCount;
        public int nonZeroCandidateCount;
        public string[] candidateNames;
        public AnimatorControllerRuntimeParameterCandidate[] examples;
        public AnimatorControllerLayerRuntimeParameterHint[] layerParameterHints;
        public int layerParameterHintCount;
        public int zeroDefaultLayerWeightHintCount;
        public string rule;
    }

    [Serializable]
    public sealed class AnimatorControllerRuntimeParameterCandidate
    {
        public string name;
        public float defaultFloat;
        public string runtimeRoleHint;
        public string defaultSource;
    }

    [Serializable]
    public sealed class AnimatorControllerLayerRuntimeParameterHint
    {
        public int layerIndex;
        public int sourceLayerIndex;
        public string layerName;
        public float sampledLayerWeight;
        public string[] candidateParameterNames;
        public AnimatorControllerRuntimeParameterCandidate[] candidates;
        public bool hasZeroDefaultWeightCandidate;
        public string rule;
    }

    [Serializable]
    public sealed class AnimatorControllerBlendTreeDiagnostic
    {
        public int layerIndex;
        public string layerName;
        public string stateName;
        public string blendTreeName;
        public string blendType;
        public string blendParameter;
        public float defaultParameterValue;
        public string defaultParameterSource;
        public int childCount;
        public string selectedChildMotionName;
        public float selectedChildThreshold;
        public string selectedChildSelectionRule;
        public AnimatorControllerBlendTreeChildDiagnostic[] children;
        public string rule;
    }

    [Serializable]
    public sealed class AnimatorControllerBlendTreeChildDiagnostic
    {
        public int index;
        public string motionName;
        public float threshold;
    }

    public sealed class AnimeStudioBakeRigMetadata : MonoBehaviour
    {
        public string restPoseSource;
        public bool restPoseApplied;
    }

    [Serializable]
    public sealed class ImportedAvatarBindingReport
    {
        public string status;
        public string message;
        public string avatarAsset;
        public int avatarPathCount;
        public int transformPathCount;
        public int matchedPathCount;
        public float matchedPathRatio;
        public string pathNormalization;
        public string[] sampleMissingAvatarPaths;
        public string[] sampleExtraTransformPaths;
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
    public sealed class HumanoidBoneDiagnostic
    {
        public string humanBone;
        public bool hasTransform;
        public bool hasTrack;
        public string path;
        public string transformName;
        public Vector3Key restTranslation;
        public QuaternionKey restRotation;
        public Vector3Key firstTranslation;
        public QuaternionKey firstRotation;
        public Vector3Key midTranslation;
        public QuaternionKey midRotation;
        public float maxTranslationDeltaFromFirst;
        public float maxRotationDeltaFromFirst;
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

}
