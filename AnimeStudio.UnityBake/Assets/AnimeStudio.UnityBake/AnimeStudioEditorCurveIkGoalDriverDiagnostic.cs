using System;
using System.Collections.Generic;
using UnityEngine;

namespace AnimeStudio.UnityBake
{
    [Serializable]
    public sealed class EditorCurveIkGoalDriverDiagnostic
    {
        public string rule;
        public bool enabled;
        public string weightRule;
        public string goalSpaceRule;
        public string hintRule;
        public string directTwoBoneIkRule;
        public bool requiresRuntimeWeightVerification;
        public int explicitIkGoalWeightCurveCount;
        public bool hasExplicitIkGoalWeightCurves;
        public bool geometryHintDriverEnabled;
        public bool directTwoBoneIkDiagnosticEnabled;
        public string layerWeightSource;
        public string boneDistanceRule;
        public int callCount;
        public int appliedGoalCount;
        public int directTwoBoneIkAttemptCount;
        public int directTwoBoneIkSolveCount;
        public int directTwoBoneIkMissingChainCount;
        public int directTwoBoneIkWeightSkippedCount;
        public float maxDirectTwoBoneIkDistanceToGoal;
        public float maxDirectTwoBoneIkImprovement;
        public float maxDirectTwoBoneIkTargetDistanceFromUpper;
        public float maxDirectTwoBoneIkChainLength;
        public float maxDirectTwoBoneIkReachShortfall;
        public float minDirectTwoBoneIkAvatarHumanScale = float.MaxValue;
        public float maxDirectTwoBoneIkAvatarHumanScale = float.MinValue;
        public float minDirectTwoBoneIkReachFitScale = float.MaxValue;
        public float maxDirectTwoBoneIkReachFitScale = float.MinValue;
        public float maxDirectTwoBoneIkReachFitScaleOffsetFromCurrentGoal;
        public int recordedSampleCount;
        public float lastSampleTime;
        public int[] layerIndexes;
        public List<EditorCurveIkGoalDriverGoalLayerSummary> goalLayerSummaries = new List<EditorCurveIkGoalDriverGoalLayerSummary>();
        public List<EditorCurveIkGoalDriverFinalGoalSummary> finalGoalSummaries = new List<EditorCurveIkGoalDriverFinalGoalSummary>();
        public List<EditorCurveIkGoalDriverSample> samples = new List<EditorCurveIkGoalDriverSample>();
    }

    [Serializable]
    public sealed class EditorCurveIkGoalDriverGoalLayerSummary
    {
        public string goal;
        public int layerIndex;
        public int sampleCount;
        public bool hasPosition;
        public bool hasRotation;
        public string layerWeightSource;
        public float minLayerWeight = float.MaxValue;
        public float maxLayerWeight = float.MinValue;
        public string bonePath;
        public string hint;
        public string hintPath;
        public bool hasPreIkBone;
        public bool hasPostIkBone;
        public bool hasIkReadback;
        public bool hasHint;
        public bool hasIkHintReadback;
        public Vector3Range localPosition = new Vector3Range();
        public Vector3Range worldPosition = new Vector3Range();
        public Vector3Range ikReadbackWorldPosition = new Vector3Range();
        public Vector3Range hintWorldPosition = new Vector3Range();
        public Vector3Range ikHintReadbackWorldPosition = new Vector3Range();
        public Vector3Range preIkBoneWorldPosition = new Vector3Range();
        public Vector3Range postIkBoneWorldPosition = new Vector3Range();
        public float minIkReadbackPositionWeight = float.MaxValue;
        public float maxIkReadbackPositionWeight = float.MinValue;
        public float minIkReadbackRotationWeight = float.MaxValue;
        public float maxIkReadbackRotationWeight = float.MinValue;
        public float minIkReadbackDistanceToGoal = float.MaxValue;
        public float maxIkReadbackDistanceToGoal = float.MinValue;
        public float minIkHintReadbackWeight = float.MaxValue;
        public float maxIkHintReadbackWeight = float.MinValue;
        public float minIkHintReadbackDistanceToHint = float.MaxValue;
        public float maxIkHintReadbackDistanceToHint = float.MinValue;
        public float minPreIkDistanceToGoal = float.MaxValue;
        public float maxPreIkDistanceToGoal = float.MinValue;
        public float minPostIkDistanceToGoal = float.MaxValue;
        public float maxPostIkDistanceToGoal = float.MinValue;
    }

    [Serializable]
    public sealed class EditorCurveIkGoalDriverFinalGoalSummary
    {
        public string goal;
        public int sampleCount;
        public int minDominantLayerIndex = int.MaxValue;
        public int maxDominantLayerIndex = int.MinValue;
        public float minDominantLayerWeight = float.MaxValue;
        public float maxDominantLayerWeight = float.MinValue;
        public string dominantLayerWeightSource;
        public string bonePath;
        public bool hasPreIkBone;
        public bool hasPostIkBone;
        public Vector3Range dominantWorldPosition = new Vector3Range();
        public Vector3Range preIkBoneWorldPosition = new Vector3Range();
        public Vector3Range postIkBoneWorldPosition = new Vector3Range();
        public float minPreIkDistanceToDominantGoal = float.MaxValue;
        public float maxPreIkDistanceToDominantGoal = float.MinValue;
        public float minPostIkDistanceToDominantGoal = float.MaxValue;
        public float maxPostIkDistanceToDominantGoal = float.MinValue;
        public float minIkBoneMoveDistance = float.MaxValue;
        public float maxIkBoneMoveDistance = float.MinValue;
        public float minIkDistanceImprovement = float.MaxValue;
        public float maxIkDistanceImprovement = float.MinValue;
        public float maxIkDistanceRegression = float.MinValue;
        public int directTwoBoneIkSolveCount;
        public int directTwoBoneIkMissingChainCount;
        public int directTwoBoneIkWeightSkippedCount;
        public float minDirectTwoBoneIkPreDistanceToGoal = float.MaxValue;
        public float maxDirectTwoBoneIkPreDistanceToGoal = float.MinValue;
        public float minDirectTwoBoneIkPostDistanceToGoal = float.MaxValue;
        public float maxDirectTwoBoneIkPostDistanceToGoal = float.MinValue;
        public float minDirectTwoBoneIkImprovement = float.MaxValue;
        public float maxDirectTwoBoneIkImprovement = float.MinValue;
        public float minDirectTwoBoneIkUpperToLowerLength = float.MaxValue;
        public float maxDirectTwoBoneIkUpperToLowerLength = float.MinValue;
        public float minDirectTwoBoneIkLowerToEndLength = float.MaxValue;
        public float maxDirectTwoBoneIkLowerToEndLength = float.MinValue;
        public float minDirectTwoBoneIkChainLength = float.MaxValue;
        public float maxDirectTwoBoneIkChainLength = float.MinValue;
        public float minDirectTwoBoneIkTargetDistanceFromUpper = float.MaxValue;
        public float maxDirectTwoBoneIkTargetDistanceFromUpper = float.MinValue;
        public float minDirectTwoBoneIkReachShortfall = float.MaxValue;
        public float maxDirectTwoBoneIkReachShortfall = float.MinValue;
        public float maxDirectTwoBoneIkInsideMinReachAmount = float.MinValue;
        public float minDirectTwoBoneIkAvatarHumanScale = float.MaxValue;
        public float maxDirectTwoBoneIkAvatarHumanScale = float.MinValue;
        public float minDirectTwoBoneIkReachFitScale = float.MaxValue;
        public float maxDirectTwoBoneIkReachFitScale = float.MinValue;
        public float minDirectTwoBoneIkReachFitScaleTargetDistanceFromUpper = float.MaxValue;
        public float maxDirectTwoBoneIkReachFitScaleTargetDistanceFromUpper = float.MinValue;
        public float maxDirectTwoBoneIkReachFitScaleOffsetFromCurrentGoal = float.MinValue;
        public float minLayerTargetSpread = float.MaxValue;
        public float maxLayerTargetSpread = float.MinValue;
        public int closestDescendantSampleCount;
        public string closestDescendantLastPath;
        public float minClosestDescendantDistanceToDominantGoal = float.MaxValue;
        public float maxClosestDescendantDistanceToDominantGoal = float.MinValue;
        public List<EditorCurveIkGoalDriverGoalSpaceCandidateSummary> goalSpaceCandidates = new List<EditorCurveIkGoalDriverGoalSpaceCandidateSummary>();
        public List<EditorCurveIkGoalDriverBoneTargetCandidateSummary> boneTargetCandidates = new List<EditorCurveIkGoalDriverBoneTargetCandidateSummary>();
    }

    [Serializable]
    public sealed class EditorCurveIkGoalDriverGoalSpaceCandidateSummary
    {
        public string name;
        public int sampleCount;
        public Vector3Range worldPosition = new Vector3Range();
        public float minPostIkDistance = float.MaxValue;
        public float maxPostIkDistance = float.MinValue;
        public float minTargetDistanceFromUpper = float.MaxValue;
        public float maxTargetDistanceFromUpper = float.MinValue;
        public float minReachShortfall = float.MaxValue;
        public float maxReachShortfall = float.MinValue;
        public float maxInsideMinReachAmount = float.MinValue;
    }

    [Serializable]
    public sealed class EditorCurveIkGoalDriverBoneTargetCandidateSummary
    {
        public string name;
        public string path;
        public int sampleCount;
        public Vector3Range worldPosition = new Vector3Range();
        public Vector3Range localOffsetToDominantGoal = new Vector3Range();
        public float minLocalOffsetLength = float.MaxValue;
        public float maxLocalOffsetLength = float.MinValue;
        public float minDistanceToDominantGoal = float.MaxValue;
        public float maxDistanceToDominantGoal = float.MinValue;
    }

    [Serializable]
    public sealed class EditorCurveIkGoalDriverSample
    {
        public float time;
        public int layerIndex;
        public string goal;
        public bool hasPosition;
        public bool hasRotation;
        public string layerWeightSource;
        public float layerWeight;
        public float positionWeight;
        public float rotationWeight;
        public string bonePath;
        public string hint;
        public string hintPath;
        public bool hasPreIkBone;
        public bool hasPostIkBone;
        public bool hasIkReadback;
        public bool hasHint;
        public bool hasIkHintReadback;
        public Vector3Key localPosition;
        public QuaternionKey localRotation;
        public Vector3Key worldPosition;
        public QuaternionKey worldRotation;
        public Vector3Key ikReadbackWorldPosition;
        public Vector3Key hintWorldPosition;
        public Vector3Key ikHintReadbackWorldPosition;
        public Vector3Key preIkBoneWorldPosition;
        public Vector3Key postIkBoneWorldPosition;
        public float ikReadbackPositionWeight;
        public float ikReadbackRotationWeight;
        public float ikReadbackDistanceToGoal;
        public float ikHintReadbackWeight;
        public float ikHintReadbackDistanceToHint;
        public float preIkDistanceToGoal;
        public float postIkDistanceToGoal;
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

    [Serializable]
    public sealed class Vector3Range
    {
        public float minX = float.MaxValue;
        public float minY = float.MaxValue;
        public float minZ = float.MaxValue;
        public float maxX = float.MinValue;
        public float maxY = float.MinValue;
        public float maxZ = float.MinValue;

        public void Include(Vector3 value)
        {
            minX = Math.Min(minX, value.x);
            minY = Math.Min(minY, value.y);
            minZ = Math.Min(minZ, value.z);
            maxX = Math.Max(maxX, value.x);
            maxY = Math.Max(maxY, value.y);
            maxZ = Math.Max(maxZ, value.z);
        }
    }
}
