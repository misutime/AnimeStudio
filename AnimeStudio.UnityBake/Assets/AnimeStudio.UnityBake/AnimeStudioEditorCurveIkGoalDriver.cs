using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AnimeStudio.UnityBake
{
    [ExecuteAlways]
    public sealed class AnimeStudioEditorCurveIkGoalDriver : MonoBehaviour
    {
        private const int MaxRecordedSamples = 64;

        private Transform root;
        private Dictionary<string, AnimationCurve> curves;
        private Dictionary<int, float> explicitLayerWeights;
        private string layerWeightSource;
        private readonly HashSet<int> layerIndexes = new HashSet<int>();
        private readonly List<EditorCurveIkGoalDriverSample> samples = new List<EditorCurveIkGoalDriverSample>();
        private readonly Dictionary<string, EditorCurveIkGoalDriverGoalLayerSummary> goalLayerSummaries = new Dictionary<string, EditorCurveIkGoalDriverGoalLayerSummary>();
        private readonly Dictionary<string, EditorCurveIkGoalDriverFinalGoalSummary> finalGoalSummaries = new Dictionary<string, EditorCurveIkGoalDriverFinalGoalSummary>();
        private readonly Dictionary<string, IkGoalFrameTarget> frameTargets = new Dictionary<string, IkGoalFrameTarget>();
        private float frameTargetSampleTime = float.NaN;
        private int callCount;
        private int appliedGoalCount;
        private int directTwoBoneIkAttemptCount;
        private int directTwoBoneIkSolveCount;
        private int directTwoBoneIkMissingChainCount;
        private int directTwoBoneIkWeightSkippedCount;
        private float maxDirectTwoBoneIkDistanceToGoal;
        private float maxDirectTwoBoneIkImprovement;
        private float maxDirectTwoBoneIkTargetDistanceFromUpper;
        private float maxDirectTwoBoneIkChainLength;
        private float maxDirectTwoBoneIkReachShortfall;
        private float minDirectTwoBoneIkAvatarHumanScale = float.MaxValue;
        private float maxDirectTwoBoneIkAvatarHumanScale = float.MinValue;
        private float minDirectTwoBoneIkReachFitScale = float.MaxValue;
        private float maxDirectTwoBoneIkReachFitScale = float.MinValue;
        private float maxDirectTwoBoneIkReachFitScaleOffsetFromCurrentGoal;

        public float SampleTime { get; set; }

        public void Configure(
            Transform runtimeRoot,
            Dictionary<string, AnimationCurve> editorCurves,
            Dictionary<int, float> controllerLayerWeights = null,
            string controllerLayerWeightSource = null)
        {
            root = runtimeRoot;
            curves = editorCurves ?? new Dictionary<string, AnimationCurve>();
            explicitLayerWeights = controllerLayerWeights;
            layerWeightSource = string.IsNullOrWhiteSpace(controllerLayerWeightSource)
                ? "animator.GetLayerWeight"
                : controllerLayerWeightSource;
        }

        private void OnAnimatorIK(int layerIndex)
        {
            callCount++;
            layerIndexes.Add(layerIndex);
            var animator = GetComponent<Animator>();
            if (animator == null || root == null || curves == null || curves.Count == 0)
            {
                return;
            }

            // Unity 的手脚 IK goal 曲线跟随 Humanoid body 保存；
            // SetIKPosition/Rotation 需要 world space，所以这里用同一帧 RootT/RootQ 转换。
            var bodyPosition = TrySampleVector3Curve("RootT.", out var rootPosition)
                || TrySampleVector3Curve("MotionT.", out rootPosition)
                    ? rootPosition
                    : Vector3.zero;
            var bodyRotation = TrySampleQuaternionCurve("RootQ.", out var rootRotation)
                || TrySampleQuaternionCurve("MotionQ.", out rootRotation)
                    ? rootRotation
                    : Quaternion.identity;
            var bodyWorldPosition = root.TransformPoint(bodyPosition);
            var bodyWorldRotation = root.rotation * bodyRotation;
            // Unity 文档说明 Humanoid IK goal 跟随 Body Transform 保存。
            // RootT/RootQ 和 Animator.bodyPosition/bodyRotation 不一定完全等价，所以先只作为诊断候选比较。
            var animatorBodyWorldPosition = animator.bodyPosition;
            var animatorBodyWorldRotation = animator.bodyRotation;

            ApplyGoal(animator, AvatarIKGoal.LeftFoot, "LeftFoot", layerIndex, bodyPosition, bodyRotation, bodyWorldPosition, bodyWorldRotation, animatorBodyWorldPosition, animatorBodyWorldRotation);
            ApplyGoal(animator, AvatarIKGoal.RightFoot, "RightFoot", layerIndex, bodyPosition, bodyRotation, bodyWorldPosition, bodyWorldRotation, animatorBodyWorldPosition, animatorBodyWorldRotation);
            ApplyGoal(animator, AvatarIKGoal.LeftHand, "LeftHand", layerIndex, bodyPosition, bodyRotation, bodyWorldPosition, bodyWorldRotation, animatorBodyWorldPosition, animatorBodyWorldRotation);
            ApplyGoal(animator, AvatarIKGoal.RightHand, "RightHand", layerIndex, bodyPosition, bodyRotation, bodyWorldPosition, bodyWorldRotation, animatorBodyWorldPosition, animatorBodyWorldRotation);
        }

        public EditorCurveIkGoalDriverDiagnostic BuildDiagnostic()
        {
            return new EditorCurveIkGoalDriverDiagnostic
            {
                rule = "diagnostic_only: records whether OnAnimatorIK ran and which editor-curve IK goals were written; it does not prove the goal space or weights are correct.",
                weightRule = CountExplicitIkGoalWeightCurves() > 0
                    ? "diagnostic_only: explicit-looking Hand/Foot IK weight curves were detected, but their Unity runtime semantics are not yet proven; recovered controller layer weight is still used for this diagnostic."
                    : "diagnostic_only: no explicit Hand/Foot IK goal weight curves were detected; recovered controller layer weight is used when available, otherwise Animator.GetLayerWeight is used. This can over-apply goals that the game runtime would gate by IK weight or parameters.",
                goalSpaceRule = "diagnostic_only: goal T/Q curves are interpreted as body-local offsets and converted through RootT/RootQ or MotionT/MotionQ into world-space Animator IK targets. animator_body_* candidates compare Unity Animator.bodyPosition/bodyRotation as the Humanoid Body Transform source. axis_basis_scan_* candidates exhaustively test signed axis-basis interpretations for reachability only; they never change the written IK target or production status.",
                hintRule = "diagnostic_only: when a goal is written, the current frame elbow/knee joint position is also written as Animator IK hint. The hint is derived from Unity's sampled skeleton pose, not from names or game-specific constants.",
                directTwoBoneIkRule = "diagnostic_only: after Unity sampling, a simple two-bone CCD solver can move Humanoid upper/lower/end limb chains toward the same Hand/Foot goal. It is evidence for goal-space semantics only and does not approve production reuse.",
                boneDistanceRule = "diagnostic_only: pre/post IK bone distances compare the interpreted world goal with Unity's humanoid hand/foot bone transforms; they help triage goal space and weight issues but do not approve production reuse.",
                requiresRuntimeWeightVerification = true,
                explicitIkGoalWeightCurveCount = CountExplicitIkGoalWeightCurves(),
                hasExplicitIkGoalWeightCurves = CountExplicitIkGoalWeightCurves() > 0,
                geometryHintDriverEnabled = true,
                directTwoBoneIkDiagnosticEnabled = directTwoBoneIkAttemptCount > 0,
                layerWeightSource = layerWeightSource ?? "animator.GetLayerWeight",
                enabled = true,
                callCount = callCount,
                appliedGoalCount = appliedGoalCount,
                directTwoBoneIkAttemptCount = directTwoBoneIkAttemptCount,
                directTwoBoneIkSolveCount = directTwoBoneIkSolveCount,
                directTwoBoneIkMissingChainCount = directTwoBoneIkMissingChainCount,
                directTwoBoneIkWeightSkippedCount = directTwoBoneIkWeightSkippedCount,
                maxDirectTwoBoneIkDistanceToGoal = maxDirectTwoBoneIkDistanceToGoal,
                maxDirectTwoBoneIkImprovement = maxDirectTwoBoneIkImprovement,
                maxDirectTwoBoneIkTargetDistanceFromUpper = maxDirectTwoBoneIkTargetDistanceFromUpper,
                maxDirectTwoBoneIkChainLength = maxDirectTwoBoneIkChainLength,
                maxDirectTwoBoneIkReachShortfall = maxDirectTwoBoneIkReachShortfall,
                minDirectTwoBoneIkAvatarHumanScale = minDirectTwoBoneIkAvatarHumanScale == float.MaxValue ? 0f : minDirectTwoBoneIkAvatarHumanScale,
                maxDirectTwoBoneIkAvatarHumanScale = maxDirectTwoBoneIkAvatarHumanScale == float.MinValue ? 0f : maxDirectTwoBoneIkAvatarHumanScale,
                minDirectTwoBoneIkReachFitScale = minDirectTwoBoneIkReachFitScale == float.MaxValue ? 0f : minDirectTwoBoneIkReachFitScale,
                maxDirectTwoBoneIkReachFitScale = maxDirectTwoBoneIkReachFitScale == float.MinValue ? 0f : maxDirectTwoBoneIkReachFitScale,
                maxDirectTwoBoneIkReachFitScaleOffsetFromCurrentGoal = maxDirectTwoBoneIkReachFitScaleOffsetFromCurrentGoal,
                recordedSampleCount = samples.Count,
                lastSampleTime = SampleTime,
                layerIndexes = layerIndexes.OrderBy(x => x).ToArray(),
                goalLayerSummaries = goalLayerSummaries.Values
                    .OrderBy(x => x.layerIndex)
                    .ThenBy(x => x.goal)
                    .ToList(),
                finalGoalSummaries = finalGoalSummaries.Values
                    .OrderBy(x => x.goal)
                    .ToList(),
                samples = samples.ToList(),
            };
        }

        public void CapturePostEvaluateBones(Animator animator)
        {
            if (animator == null || frameTargets.Count == 0)
            {
                return;
            }

            foreach (var target in frameTargets.Values)
            {
                if (!target.hasPosition
                    || !goalLayerSummaries.TryGetValue(target.key, out var summary)
                    || !TryGetGoalBone(animator, target.goal, out var bone, out var bonePath))
                {
                    continue;
                }

                var postPosition = bone.position;
                var distance = Vector3.Distance(postPosition, target.worldPosition);
                summary.hasPostIkBone = true;
                if (string.IsNullOrWhiteSpace(summary.bonePath))
                {
                    summary.bonePath = bonePath;
                }
                summary.postIkBoneWorldPosition.Include(postPosition);
                summary.minPostIkDistanceToGoal = Mathf.Min(summary.minPostIkDistanceToGoal, distance);
                summary.maxPostIkDistanceToGoal = Mathf.Max(summary.maxPostIkDistanceToGoal, distance);

                var sample = target.sample;
                if (sample != null)
                {
                    sample.hasPostIkBone = true;
                    if (string.IsNullOrWhiteSpace(sample.bonePath))
                    {
                        sample.bonePath = bonePath;
                    }
                    sample.postIkBoneWorldPosition = new Vector3Key(SampleTime, postPosition);
                    sample.postIkDistanceToGoal = distance;
                }
            }

            RecordFinalGoalAlignment(animator);
        }

        public void ApplyDirectTwoBoneIkDiagnostic(Animator animator)
        {
            if (animator == null || frameTargets.Count == 0)
            {
                return;
            }

            foreach (var group in frameTargets.Values
                .Where(x => x.hasPosition)
                .GroupBy(x => x.goal))
            {
                var dominant = group
                    .OrderByDescending(x => x.layerWeight)
                    .ThenByDescending(x => x.layerIndex)
                    .FirstOrDefault();
                if (dominant == null)
                {
                    continue;
                }

                directTwoBoneIkAttemptCount++;
                var summary = GetFinalGoalSummary(dominant.goal);
                var weight = Mathf.Clamp01(dominant.layerWeight);
                if (weight <= 0.0001f)
                {
                    directTwoBoneIkWeightSkippedCount++;
                    summary.directTwoBoneIkWeightSkippedCount++;
                    continue;
                }

                if (!TryGetTwoBoneChain(animator, dominant.goal, out var upper, out var lower, out var end))
                {
                    directTwoBoneIkMissingChainCount++;
                    summary.directTwoBoneIkMissingChainCount++;
                    continue;
                }

                var upperToLowerLength = Vector3.Distance(upper.position, lower.position);
                var lowerToEndLength = Vector3.Distance(lower.position, end.position);
                var chainLength = upperToLowerLength + lowerToEndLength;
                var minReach = Mathf.Abs(upperToLowerLength - lowerToEndLength);
                var targetDistanceFromUpper = Vector3.Distance(upper.position, dominant.worldPosition);
                var reachShortfall = Mathf.Max(0f, targetDistanceFromUpper - chainLength);
                var insideMinReachAmount = Mathf.Max(0f, minReach - targetDistanceFromUpper);
                RecordDirectTwoBoneIkReach(summary, upperToLowerLength, lowerToEndLength, chainLength, targetDistanceFromUpper, reachShortfall, insideMinReachAmount);
                RecordDirectTwoBoneIkGoalScale(summary, dominant, upper.position, chainLength);

                var preDistance = Vector3.Distance(end.position, dominant.worldPosition);
                SolveTwoBoneCcd(upper, lower, end, dominant.worldPosition, weight);
                if (dominant.hasRotation)
                {
                    end.rotation = Quaternion.Slerp(end.rotation, dominant.worldRotation, weight);
                }

                var postDistance = Vector3.Distance(end.position, dominant.worldPosition);
                var improvement = preDistance - postDistance;
                directTwoBoneIkSolveCount++;
                maxDirectTwoBoneIkDistanceToGoal = Mathf.Max(maxDirectTwoBoneIkDistanceToGoal, postDistance);
                maxDirectTwoBoneIkImprovement = Mathf.Max(maxDirectTwoBoneIkImprovement, improvement);
                maxDirectTwoBoneIkTargetDistanceFromUpper = Mathf.Max(maxDirectTwoBoneIkTargetDistanceFromUpper, targetDistanceFromUpper);
                maxDirectTwoBoneIkChainLength = Mathf.Max(maxDirectTwoBoneIkChainLength, chainLength);
                maxDirectTwoBoneIkReachShortfall = Mathf.Max(maxDirectTwoBoneIkReachShortfall, reachShortfall);

                summary.directTwoBoneIkSolveCount++;
                summary.minDirectTwoBoneIkPreDistanceToGoal = Mathf.Min(summary.minDirectTwoBoneIkPreDistanceToGoal, preDistance);
                summary.maxDirectTwoBoneIkPreDistanceToGoal = Mathf.Max(summary.maxDirectTwoBoneIkPreDistanceToGoal, preDistance);
                summary.minDirectTwoBoneIkPostDistanceToGoal = Mathf.Min(summary.minDirectTwoBoneIkPostDistanceToGoal, postDistance);
                summary.maxDirectTwoBoneIkPostDistanceToGoal = Mathf.Max(summary.maxDirectTwoBoneIkPostDistanceToGoal, postDistance);
                summary.minDirectTwoBoneIkImprovement = Mathf.Min(summary.minDirectTwoBoneIkImprovement, improvement);
                summary.maxDirectTwoBoneIkImprovement = Mathf.Max(summary.maxDirectTwoBoneIkImprovement, improvement);
            }
        }

        private static void RecordDirectTwoBoneIkReach(
            EditorCurveIkGoalDriverFinalGoalSummary summary,
            float upperToLowerLength,
            float lowerToEndLength,
            float chainLength,
            float targetDistanceFromUpper,
            float reachShortfall,
            float insideMinReachAmount)
        {
            if (summary == null)
            {
                return;
            }

            summary.minDirectTwoBoneIkUpperToLowerLength = Mathf.Min(summary.minDirectTwoBoneIkUpperToLowerLength, upperToLowerLength);
            summary.maxDirectTwoBoneIkUpperToLowerLength = Mathf.Max(summary.maxDirectTwoBoneIkUpperToLowerLength, upperToLowerLength);
            summary.minDirectTwoBoneIkLowerToEndLength = Mathf.Min(summary.minDirectTwoBoneIkLowerToEndLength, lowerToEndLength);
            summary.maxDirectTwoBoneIkLowerToEndLength = Mathf.Max(summary.maxDirectTwoBoneIkLowerToEndLength, lowerToEndLength);
            summary.minDirectTwoBoneIkChainLength = Mathf.Min(summary.minDirectTwoBoneIkChainLength, chainLength);
            summary.maxDirectTwoBoneIkChainLength = Mathf.Max(summary.maxDirectTwoBoneIkChainLength, chainLength);
            summary.minDirectTwoBoneIkTargetDistanceFromUpper = Mathf.Min(summary.minDirectTwoBoneIkTargetDistanceFromUpper, targetDistanceFromUpper);
            summary.maxDirectTwoBoneIkTargetDistanceFromUpper = Mathf.Max(summary.maxDirectTwoBoneIkTargetDistanceFromUpper, targetDistanceFromUpper);
            summary.minDirectTwoBoneIkReachShortfall = Mathf.Min(summary.minDirectTwoBoneIkReachShortfall, reachShortfall);
            summary.maxDirectTwoBoneIkReachShortfall = Mathf.Max(summary.maxDirectTwoBoneIkReachShortfall, reachShortfall);
            summary.maxDirectTwoBoneIkInsideMinReachAmount = Mathf.Max(summary.maxDirectTwoBoneIkInsideMinReachAmount, insideMinReachAmount);
        }

        private void RecordDirectTwoBoneIkGoalScale(
            EditorCurveIkGoalDriverFinalGoalSummary summary,
            IkGoalFrameTarget dominant,
            Vector3 upperWorldPosition,
            float chainLength)
        {
            if (summary == null || dominant == null || !dominant.hasPosition)
            {
                return;
            }

            summary.minDirectTwoBoneIkAvatarHumanScale = Mathf.Min(summary.minDirectTwoBoneIkAvatarHumanScale, dominant.avatarHumanScale);
            summary.maxDirectTwoBoneIkAvatarHumanScale = Mathf.Max(summary.maxDirectTwoBoneIkAvatarHumanScale, dominant.avatarHumanScale);
            minDirectTwoBoneIkAvatarHumanScale = Mathf.Min(minDirectTwoBoneIkAvatarHumanScale, dominant.avatarHumanScale);
            maxDirectTwoBoneIkAvatarHumanScale = Mathf.Max(maxDirectTwoBoneIkAvatarHumanScale, dominant.avatarHumanScale);

            var localOffsetWorld = dominant.bodyWorldRotation * dominant.localPosition;
            if (!TrySolveScaleToReach(
                    dominant.bodyWorldPosition,
                    localOffsetWorld,
                    upperWorldPosition,
                    chainLength,
                    out var fitScale,
                    out var fitWorldPosition))
            {
                return;
            }

            var fitDistanceFromUpper = Vector3.Distance(upperWorldPosition, fitWorldPosition);
            var offsetFromCurrentGoal = Vector3.Distance(fitWorldPosition, dominant.worldPosition);
            summary.minDirectTwoBoneIkReachFitScale = Mathf.Min(summary.minDirectTwoBoneIkReachFitScale, fitScale);
            summary.maxDirectTwoBoneIkReachFitScale = Mathf.Max(summary.maxDirectTwoBoneIkReachFitScale, fitScale);
            summary.minDirectTwoBoneIkReachFitScaleTargetDistanceFromUpper = Mathf.Min(summary.minDirectTwoBoneIkReachFitScaleTargetDistanceFromUpper, fitDistanceFromUpper);
            summary.maxDirectTwoBoneIkReachFitScaleTargetDistanceFromUpper = Mathf.Max(summary.maxDirectTwoBoneIkReachFitScaleTargetDistanceFromUpper, fitDistanceFromUpper);
            summary.maxDirectTwoBoneIkReachFitScaleOffsetFromCurrentGoal = Mathf.Max(summary.maxDirectTwoBoneIkReachFitScaleOffsetFromCurrentGoal, offsetFromCurrentGoal);
            minDirectTwoBoneIkReachFitScale = Mathf.Min(minDirectTwoBoneIkReachFitScale, fitScale);
            maxDirectTwoBoneIkReachFitScale = Mathf.Max(maxDirectTwoBoneIkReachFitScale, fitScale);
            maxDirectTwoBoneIkReachFitScaleOffsetFromCurrentGoal = Mathf.Max(maxDirectTwoBoneIkReachFitScaleOffsetFromCurrentGoal, offsetFromCurrentGoal);
        }

        private void ApplyGoal(
            Animator animator,
            AvatarIKGoal goal,
            string prefix,
            int layerIndex,
            Vector3 bodyPosition,
            Quaternion bodyRotation,
            Vector3 bodyWorldPosition,
            Quaternion bodyWorldRotation,
            Vector3 animatorBodyWorldPosition,
            Quaternion animatorBodyWorldRotation)
        {
            var hasPosition = TrySampleVector3Curve(prefix + "T.", out var localPosition);
            var hasRotation = TrySampleQuaternionCurve(prefix + "Q.", out var localRotation);
            if (!hasPosition && !hasRotation)
            {
                return;
            }

            var worldPosition = bodyWorldPosition + bodyWorldRotation * localPosition;
            var worldRotation = bodyWorldRotation * localRotation;
            var avatarHumanScale = ReadAvatarHumanScale(animator);
            var goalSpaceCandidates = BuildGoalSpaceCandidates(localPosition, bodyPosition, bodyRotation, bodyWorldPosition, bodyWorldRotation, animatorBodyWorldPosition, animatorBodyWorldRotation, worldPosition, avatarHumanScale);
            var layerWeight = ReadLayerWeight(animator, layerIndex, out var currentLayerWeightSource);
            var hasBone = TryGetGoalBone(animator, goal, out var bone, out var bonePath);
            var preIkBoneWorldPosition = hasBone ? bone.position : default;
            var preIkDistanceToGoal = hasPosition && hasBone
                ? Vector3.Distance(preIkBoneWorldPosition, worldPosition)
                : 0f;
            var hasHint = TryGetGoalHint(animator, goal, out var hint, out var hintTransform, out var hintPath);
            var hintWorldPosition = hasHint ? hintTransform.position : default;
            // 目前 Endfield 样本没有暴露单独 IK 权重曲线。诊断阶段使用 Animator
            // 当前层权重，比固定 1 更接近 controller 语义；结果仍由 needs_review 阻断。
            if (hasPosition)
            {
                animator.SetIKPositionWeight(goal, layerWeight);
                animator.SetIKPosition(goal, worldPosition);
                if (hasHint)
                {
                    animator.SetIKHintPositionWeight(hint, layerWeight);
                    animator.SetIKHintPosition(hint, hintWorldPosition);
                }
            }
            if (hasRotation)
            {
                animator.SetIKRotationWeight(goal, layerWeight);
                animator.SetIKRotation(goal, worldRotation);
            }
            var readback = ReadIkGoalReadback(animator, goal, hasPosition, hasRotation, worldPosition);
            var hintReadback = ReadIkHintReadback(animator, hasPosition && hasHint, hint, hintWorldPosition);
            appliedGoalCount++;
            RecordSummary(goal, layerIndex, hasPosition, hasRotation, layerWeight, currentLayerWeightSource, localPosition, worldPosition, readback, hasBone, bonePath, preIkBoneWorldPosition, preIkDistanceToGoal, hasHint, hint, hintPath, hintWorldPosition, hintReadback);
            var sample = RecordSample(goal, layerIndex, hasPosition, hasRotation, layerWeight, currentLayerWeightSource, localPosition, localRotation, worldPosition, worldRotation, readback, hasBone, bonePath, preIkBoneWorldPosition, preIkDistanceToGoal, hasHint, hint, hintPath, hintWorldPosition, hintReadback);
            RecordFrameTarget(goal, layerIndex, hasPosition, localPosition, bodyWorldPosition, bodyWorldRotation, avatarHumanScale, worldPosition, goalSpaceCandidates, layerWeight, currentLayerWeightSource, hasBone, preIkBoneWorldPosition, preIkDistanceToGoal, sample);
        }

        private static float ReadAvatarHumanScale(Animator animator)
        {
            if (animator == null || animator.avatar == null)
            {
                return 1f;
            }

            try
            {
                var property = typeof(Avatar).GetProperty(
                    "humanScale",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
                var raw = property != null ? property.GetValue(animator.avatar, null) : null;
                if (raw is float scale && scale > 0.00001f && !float.IsNaN(scale) && !float.IsInfinity(scale))
                {
                    return scale;
                }
            }
            catch
            {
                // 不同 Unity 版本是否暴露 humanScale 不稳定；缺失时保持 1.0，只记录诊断证据。
            }

            return 1f;
        }

        private float ReadLayerWeight(Animator animator, int layerIndex, out string source)
        {
            if (explicitLayerWeights != null && explicitLayerWeights.TryGetValue(layerIndex, out var explicitWeight))
            {
                source = layerWeightSource ?? "controllerSampling.LayerStates.Weight";
                return Mathf.Clamp01(explicitWeight);
            }
            if (layerIndex == 0)
            {
                source = "animator.GetLayerWeight.baseLayerDefault";
                return 1f;
            }
            if (animator == null || layerIndex < 0 || layerIndex >= animator.layerCount)
            {
                source = "animator.GetLayerWeight.unavailableDefault";
                return 1f;
            }

            source = "animator.GetLayerWeight";
            return Mathf.Clamp01(animator.GetLayerWeight(layerIndex));
        }

        private void RecordSummary(
            AvatarIKGoal goal,
            int layerIndex,
            bool hasPosition,
            bool hasRotation,
            float layerWeight,
            string currentLayerWeightSource,
            Vector3 localPosition,
            Vector3 worldPosition,
            IkGoalReadback readback,
            bool hasBone,
            string bonePath,
            Vector3 preIkBoneWorldPosition,
            float preIkDistanceToGoal,
            bool hasHint,
            AvatarIKHint hint,
            string hintPath,
            Vector3 hintWorldPosition,
            IkHintReadback hintReadback)
        {
            var key = layerIndex + ":" + goal;
            if (!goalLayerSummaries.TryGetValue(key, out var summary))
            {
                summary = new EditorCurveIkGoalDriverGoalLayerSummary
                {
                    goal = goal.ToString(),
                    layerIndex = layerIndex,
                };
                goalLayerSummaries[key] = summary;
            }

            // samples 只保留少量明细，summary 汇总完整时间线，方便后续判断权重和 goal 空间。
            summary.sampleCount++;
            summary.hasPosition |= hasPosition;
            summary.hasRotation |= hasRotation;
            summary.layerWeightSource = currentLayerWeightSource;
            summary.minLayerWeight = Mathf.Min(summary.minLayerWeight, layerWeight);
            summary.maxLayerWeight = Mathf.Max(summary.maxLayerWeight, layerWeight);
            if (hasPosition)
            {
                summary.localPosition.Include(localPosition);
                summary.worldPosition.Include(worldPosition);
            }
            if (readback.hasReadback)
            {
                summary.hasIkReadback = true;
                summary.ikReadbackWorldPosition.Include(readback.position);
                summary.minIkReadbackPositionWeight = Mathf.Min(summary.minIkReadbackPositionWeight, readback.positionWeight);
                summary.maxIkReadbackPositionWeight = Mathf.Max(summary.maxIkReadbackPositionWeight, readback.positionWeight);
                summary.minIkReadbackRotationWeight = Mathf.Min(summary.minIkReadbackRotationWeight, readback.rotationWeight);
                summary.maxIkReadbackRotationWeight = Mathf.Max(summary.maxIkReadbackRotationWeight, readback.rotationWeight);
                summary.minIkReadbackDistanceToGoal = Mathf.Min(summary.minIkReadbackDistanceToGoal, readback.distanceToGoal);
                summary.maxIkReadbackDistanceToGoal = Mathf.Max(summary.maxIkReadbackDistanceToGoal, readback.distanceToGoal);
            }
            if (hasPosition && hasBone)
            {
                summary.hasPreIkBone = true;
                summary.bonePath = bonePath;
                summary.preIkBoneWorldPosition.Include(preIkBoneWorldPosition);
                summary.minPreIkDistanceToGoal = Mathf.Min(summary.minPreIkDistanceToGoal, preIkDistanceToGoal);
                summary.maxPreIkDistanceToGoal = Mathf.Max(summary.maxPreIkDistanceToGoal, preIkDistanceToGoal);
            }
            if (hasPosition && hasHint)
            {
                summary.hasHint = true;
                summary.hint = hint.ToString();
                summary.hintPath = hintPath;
                summary.hintWorldPosition.Include(hintWorldPosition);
            }
            if (hintReadback.hasReadback)
            {
                summary.hasIkHintReadback = true;
                summary.ikHintReadbackWorldPosition.Include(hintReadback.position);
                summary.minIkHintReadbackWeight = Mathf.Min(summary.minIkHintReadbackWeight, hintReadback.weight);
                summary.maxIkHintReadbackWeight = Mathf.Max(summary.maxIkHintReadbackWeight, hintReadback.weight);
                summary.minIkHintReadbackDistanceToHint = Mathf.Min(summary.minIkHintReadbackDistanceToHint, hintReadback.distanceToHint);
                summary.maxIkHintReadbackDistanceToHint = Mathf.Max(summary.maxIkHintReadbackDistanceToHint, hintReadback.distanceToHint);
            }
        }

        private EditorCurveIkGoalDriverSample RecordSample(
            AvatarIKGoal goal,
            int layerIndex,
            bool hasPosition,
            bool hasRotation,
            float layerWeight,
            string currentLayerWeightSource,
            Vector3 localPosition,
            Quaternion localRotation,
            Vector3 worldPosition,
            Quaternion worldRotation,
            IkGoalReadback readback,
            bool hasBone,
            string bonePath,
            Vector3 preIkBoneWorldPosition,
            float preIkDistanceToGoal,
            bool hasHint,
            AvatarIKHint hint,
            string hintPath,
            Vector3 hintWorldPosition,
            IkHintReadback hintReadback)
        {
            if (samples.Count >= MaxRecordedSamples)
            {
                return null;
            }

            var sample = new EditorCurveIkGoalDriverSample
            {
                time = SampleTime,
                layerIndex = layerIndex,
                goal = goal.ToString(),
                hasPosition = hasPosition,
                hasRotation = hasRotation,
                layerWeightSource = currentLayerWeightSource,
                layerWeight = layerWeight,
                positionWeight = hasPosition ? layerWeight : 0f,
                rotationWeight = hasRotation ? layerWeight : 0f,
                bonePath = bonePath,
                hint = hasHint ? hint.ToString() : string.Empty,
                hintPath = hintPath,
                hasPreIkBone = hasPosition && hasBone,
                hasHint = hasPosition && hasHint,
                localPosition = new Vector3Key(SampleTime, localPosition),
                localRotation = new QuaternionKey(SampleTime, localRotation),
                worldPosition = new Vector3Key(SampleTime, worldPosition),
                worldRotation = new QuaternionKey(SampleTime, worldRotation),
                hasIkReadback = readback.hasReadback,
                hasIkHintReadback = hintReadback.hasReadback,
                ikReadbackWorldPosition = readback.hasReadback
                    ? new Vector3Key(SampleTime, readback.position)
                    : default,
                hintWorldPosition = hasPosition && hasHint
                    ? new Vector3Key(SampleTime, hintWorldPosition)
                    : default,
                ikHintReadbackWorldPosition = hintReadback.hasReadback
                    ? new Vector3Key(SampleTime, hintReadback.position)
                    : default,
                ikReadbackPositionWeight = readback.hasReadback ? readback.positionWeight : 0f,
                ikReadbackRotationWeight = readback.hasReadback ? readback.rotationWeight : 0f,
                ikReadbackDistanceToGoal = readback.hasReadback ? readback.distanceToGoal : 0f,
                ikHintReadbackWeight = hintReadback.hasReadback ? hintReadback.weight : 0f,
                ikHintReadbackDistanceToHint = hintReadback.hasReadback ? hintReadback.distanceToHint : 0f,
                preIkBoneWorldPosition = hasPosition && hasBone
                    ? new Vector3Key(SampleTime, preIkBoneWorldPosition)
                    : default,
                preIkDistanceToGoal = hasPosition && hasBone ? preIkDistanceToGoal : 0f,
            };
            samples.Add(sample);
            return sample;
        }

        private static IkGoalReadback ReadIkGoalReadback(
            Animator animator,
            AvatarIKGoal goal,
            bool hasPosition,
            bool hasRotation,
            Vector3 worldPosition)
        {
            if (animator == null)
            {
                return default;
            }

            var readback = new IkGoalReadback
            {
                hasReadback = true,
                position = hasPosition ? animator.GetIKPosition(goal) : default,
                positionWeight = hasPosition ? animator.GetIKPositionWeight(goal) : 0f,
                rotationWeight = hasRotation ? animator.GetIKRotationWeight(goal) : 0f,
            };
            readback.distanceToGoal = hasPosition
                ? Vector3.Distance(readback.position, worldPosition)
                : 0f;
            return readback;
        }

        private static IkHintReadback ReadIkHintReadback(
            Animator animator,
            bool hasHint,
            AvatarIKHint hint,
            Vector3 hintWorldPosition)
        {
            if (animator == null || !hasHint)
            {
                return default;
            }

            var readback = new IkHintReadback
            {
                hasReadback = true,
                position = animator.GetIKHintPosition(hint),
                weight = animator.GetIKHintPositionWeight(hint),
            };
            readback.distanceToHint = Vector3.Distance(readback.position, hintWorldPosition);
            return readback;
        }

        private void RecordFrameTarget(
            AvatarIKGoal goal,
            int layerIndex,
            bool hasPosition,
            Vector3 localPosition,
            Vector3 bodyWorldPosition,
            Quaternion bodyWorldRotation,
            float avatarHumanScale,
            Vector3 worldPosition,
            IReadOnlyList<GoalSpaceCandidateTarget> goalSpaceCandidates,
            float layerWeight,
            string layerWeightSource,
            bool hasPreIkBone,
            Vector3 preIkBoneWorldPosition,
            float preIkDistanceToGoal,
            EditorCurveIkGoalDriverSample sample)
        {
            if (!Mathf.Approximately(frameTargetSampleTime, SampleTime))
            {
                frameTargets.Clear();
                frameTargetSampleTime = SampleTime;
            }

            var key = layerIndex + ":" + goal;
            frameTargets[key] = new IkGoalFrameTarget
            {
                key = key,
                goal = goal,
                layerIndex = layerIndex,
                hasPosition = hasPosition,
                localPosition = localPosition,
                bodyWorldPosition = bodyWorldPosition,
                bodyWorldRotation = bodyWorldRotation,
                avatarHumanScale = avatarHumanScale,
                worldPosition = worldPosition,
                hasRotation = sample != null && sample.hasRotation,
                worldRotation = sample != null ? new Quaternion(
                    sample.worldRotation.x,
                    sample.worldRotation.y,
                    sample.worldRotation.z,
                    sample.worldRotation.w) : Quaternion.identity,
                goalSpaceCandidates = goalSpaceCandidates?.ToArray() ?? System.Array.Empty<GoalSpaceCandidateTarget>(),
                layerWeight = layerWeight,
                layerWeightSource = layerWeightSource,
                hasPreIkBone = hasPosition && hasPreIkBone,
                preIkBoneWorldPosition = preIkBoneWorldPosition,
                preIkDistanceToGoal = preIkDistanceToGoal,
                sample = sample,
            };
        }

        private void RecordFinalGoalAlignment(Animator animator)
        {
            foreach (var group in frameTargets.Values
                .Where(x => x.hasPosition)
                .GroupBy(x => x.goal))
            {
                var targets = group.ToArray();
                if (targets.Length == 0)
                {
                    continue;
                }

                // 多个 Animator layer 会在同一帧对同一个 hand/foot 写目标。
                // 最终骨骼姿态只能和混合后的结果对齐；逐层距离只能作为辅助证据。
                // 这里先用当前恢复出的最高 layer weight 目标作为“主导目标”诊断。
                var dominant = targets
                    .OrderByDescending(x => x.layerWeight)
                    .ThenByDescending(x => x.layerIndex)
                    .First();
                var summary = GetFinalGoalSummary(dominant.goal);
                summary.sampleCount++;
                summary.minDominantLayerIndex = Mathf.Min(summary.minDominantLayerIndex, dominant.layerIndex);
                summary.maxDominantLayerIndex = Mathf.Max(summary.maxDominantLayerIndex, dominant.layerIndex);
                summary.minDominantLayerWeight = Mathf.Min(summary.minDominantLayerWeight, dominant.layerWeight);
                summary.maxDominantLayerWeight = Mathf.Max(summary.maxDominantLayerWeight, dominant.layerWeight);
                summary.dominantLayerWeightSource = dominant.layerWeightSource;
                summary.dominantWorldPosition.Include(dominant.worldPosition);

                var spread = CalculateTargetSpread(targets);
                summary.minLayerTargetSpread = Mathf.Min(summary.minLayerTargetSpread, spread);
                summary.maxLayerTargetSpread = Mathf.Max(summary.maxLayerTargetSpread, spread);

                if (!TryGetGoalBone(animator, dominant.goal, out var bone, out var bonePath))
                {
                    continue;
                }

                var postPosition = bone.position;
                var distance = Vector3.Distance(postPosition, dominant.worldPosition);
                summary.hasPostIkBone = true;
                if (string.IsNullOrWhiteSpace(summary.bonePath))
                {
                    summary.bonePath = bonePath;
                }
                if (dominant.hasPreIkBone)
                {
                    var moveDistance = Vector3.Distance(dominant.preIkBoneWorldPosition, postPosition);
                    var improvement = dominant.preIkDistanceToGoal - distance;
                    summary.hasPreIkBone = true;
                    summary.preIkBoneWorldPosition.Include(dominant.preIkBoneWorldPosition);
                    summary.minPreIkDistanceToDominantGoal = Mathf.Min(summary.minPreIkDistanceToDominantGoal, dominant.preIkDistanceToGoal);
                    summary.maxPreIkDistanceToDominantGoal = Mathf.Max(summary.maxPreIkDistanceToDominantGoal, dominant.preIkDistanceToGoal);
                    summary.minIkBoneMoveDistance = Mathf.Min(summary.minIkBoneMoveDistance, moveDistance);
                    summary.maxIkBoneMoveDistance = Mathf.Max(summary.maxIkBoneMoveDistance, moveDistance);
                    summary.minIkDistanceImprovement = Mathf.Min(summary.minIkDistanceImprovement, improvement);
                    summary.maxIkDistanceImprovement = Mathf.Max(summary.maxIkDistanceImprovement, improvement);
                    summary.maxIkDistanceRegression = Mathf.Max(summary.maxIkDistanceRegression, Mathf.Max(0f, -improvement));
                }
                summary.postIkBoneWorldPosition.Include(postPosition);
                summary.minPostIkDistanceToDominantGoal = Mathf.Min(summary.minPostIkDistanceToDominantGoal, distance);
                summary.maxPostIkDistanceToDominantGoal = Mathf.Max(summary.maxPostIkDistanceToDominantGoal, distance);
                RecordGoalSpaceCandidateDistances(animator, summary, dominant, postPosition);
                RecordClosestDescendantDistance(summary, dominant, bone);
                RecordBoneTargetCandidateDistances(animator, summary, dominant, bone);
            }
        }

        private EditorCurveIkGoalDriverFinalGoalSummary GetFinalGoalSummary(AvatarIKGoal goal)
        {
            var key = goal.ToString();
            if (!finalGoalSummaries.TryGetValue(key, out var summary))
            {
                summary = new EditorCurveIkGoalDriverFinalGoalSummary
                {
                    goal = key,
                };
                finalGoalSummaries[key] = summary;
            }

            return summary;
        }

        private GoalSpaceCandidateTarget[] BuildGoalSpaceCandidates(
            Vector3 localPosition,
            Vector3 bodyPosition,
            Quaternion bodyRotation,
            Vector3 bodyWorldPosition,
            Quaternion bodyWorldRotation,
            Vector3 animatorBodyWorldPosition,
            Quaternion animatorBodyWorldRotation,
            Vector3 currentWorldPosition,
            float avatarHumanScale)
        {
            // 这些候选只用于诊断，不改变实际写给 Animator IK 的 current_body_local_root_tr。
            // 目标是区分“goal T 是 body-local / root-local / world-like”这类空间解释问题。
            var rootPosition = root != null ? root.position : Vector3.zero;
            var rootRotation = root != null ? root.rotation : Quaternion.identity;
            var safeHumanScale = avatarHumanScale > 0.00001f ? avatarHumanScale : 1f;
            var result = new List<GoalSpaceCandidateTarget>
            {
                new GoalSpaceCandidateTarget("current_body_local_root_tr", currentWorldPosition),
                new GoalSpaceCandidateTarget("body_local_avatar_human_scale", bodyWorldPosition + bodyWorldRotation * (localPosition * safeHumanScale)),
                new GoalSpaceCandidateTarget("body_local_inverse_avatar_human_scale", bodyWorldPosition + bodyWorldRotation * (localPosition / safeHumanScale)),
                new GoalSpaceCandidateTarget("root_transform_point", root != null ? root.TransformPoint(localPosition) : localPosition),
                new GoalSpaceCandidateTarget("root_rotation_offset", rootPosition + rootRotation * localPosition),
                new GoalSpaceCandidateTarget("body_position_root_rotation", bodyWorldPosition + rootRotation * localPosition),
                new GoalSpaceCandidateTarget("body_rotation_root_origin", rootPosition + bodyWorldRotation * localPosition),
                new GoalSpaceCandidateTarget("body_position_no_rotation", bodyWorldPosition + localPosition),
                new GoalSpaceCandidateTarget("animator_body_local", animatorBodyWorldPosition + animatorBodyWorldRotation * localPosition),
                new GoalSpaceCandidateTarget("animator_body_local_avatar_human_scale", animatorBodyWorldPosition + animatorBodyWorldRotation * (localPosition * safeHumanScale)),
                new GoalSpaceCandidateTarget("animator_body_local_inverse_avatar_human_scale", animatorBodyWorldPosition + animatorBodyWorldRotation * (localPosition / safeHumanScale)),
                new GoalSpaceCandidateTarget("animator_body_position_root_rotation", animatorBodyWorldPosition + rootRotation * localPosition),
                new GoalSpaceCandidateTarget("animator_body_position_no_rotation", animatorBodyWorldPosition + localPosition),
                new GoalSpaceCandidateTarget("raw_world", localPosition),
            };
            result.AddRange(BuildAxisBasisScanCandidates(localPosition, bodyWorldPosition, bodyWorldRotation));
            return result.ToArray();
        }

        private static IEnumerable<GoalSpaceCandidateTarget> BuildAxisBasisScanCandidates(
            Vector3 localPosition,
            Vector3 bodyWorldPosition,
            Quaternion bodyWorldRotation)
        {
            // 这里只做穷举诊断，回答“是否存在稳定轴基能让目标点变可达”。
            // 不把任何候选反写到 IK target，避免把坐标猜测伪装成修复。
            var permutations = new[]
            {
                new[] { 0, 1, 2 },
                new[] { 0, 2, 1 },
                new[] { 1, 0, 2 },
                new[] { 1, 2, 0 },
                new[] { 2, 0, 1 },
                new[] { 2, 1, 0 },
            };
            foreach (var permutation in permutations)
            {
                foreach (var sx in AxisSigns())
                foreach (var sy in AxisSigns())
                foreach (var sz in AxisSigns())
                {
                    var signs = new[] { sx, sy, sz };
                    var mapped = new Vector3(
                        signs[0] * GetAxis(localPosition, permutation[0]),
                        signs[1] * GetAxis(localPosition, permutation[1]),
                        signs[2] * GetAxis(localPosition, permutation[2]));
                    var handedness = AxisBasisDeterminant(permutation, signs) >= 0 ? "proper" : "mirror";
                    var name = "axis_basis_scan_"
                        + handedness
                        + "_"
                        + AxisName(permutation[0], signs[0])
                        + "_"
                        + AxisName(permutation[1], signs[1])
                        + "_"
                        + AxisName(permutation[2], signs[2]);
                    yield return new GoalSpaceCandidateTarget(name, bodyWorldPosition + bodyWorldRotation * mapped);
                }
            }
        }

        private int CountExplicitIkGoalWeightCurves()
        {
            if (curves == null || curves.Count == 0)
            {
                return 0;
            }

            return curves.Keys.Count(IsExplicitIkGoalWeightCurveName);
        }

        private static bool IsExplicitIkGoalWeightCurveName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            var normalized = name.Replace(" ", string.Empty);
            var hasGoalName =
                normalized.IndexOf("LeftFoot", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("RightFoot", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("LeftHand", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("RightHand", System.StringComparison.OrdinalIgnoreCase) >= 0;
            if (!hasGoalName)
            {
                return false;
            }

            return normalized.EndsWith("T.w", System.StringComparison.OrdinalIgnoreCase)
                || normalized.IndexOf("IKWeight", System.StringComparison.OrdinalIgnoreCase) >= 0
                || normalized.IndexOf("GoalWeight", System.StringComparison.OrdinalIgnoreCase) >= 0
                || normalized.IndexOf("PositionWeight", System.StringComparison.OrdinalIgnoreCase) >= 0
                || normalized.IndexOf("RotationWeight", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int[] AxisSigns()
        {
            return new[] { -1, 1 };
        }

        private static float GetAxis(Vector3 value, int axis)
        {
            switch (axis)
            {
                case 0:
                    return value.x;
                case 1:
                    return value.y;
                case 2:
                    return value.z;
                default:
                    return 0f;
            }
        }

        private static string AxisName(int axis, int sign)
        {
            var prefix = sign < 0 ? "n" : "p";
            switch (axis)
            {
                case 0:
                    return prefix + "x";
                case 1:
                    return prefix + "y";
                case 2:
                    return prefix + "z";
                default:
                    return prefix + "unknown";
            }
        }

        private static int AxisBasisDeterminant(int[] permutation, int[] signs)
        {
            var inversions = 0;
            for (var i = 0; i < permutation.Length; i++)
            {
                for (var j = i + 1; j < permutation.Length; j++)
                {
                    if (permutation[i] > permutation[j])
                    {
                        inversions++;
                    }
                }
            }

            var permutationSign = inversions % 2 == 0 ? 1 : -1;
            return permutationSign * signs[0] * signs[1] * signs[2];
        }

        private static void RecordGoalSpaceCandidateDistances(
            Animator animator,
            EditorCurveIkGoalDriverFinalGoalSummary summary,
            IkGoalFrameTarget dominant,
            Vector3 postPosition)
        {
            if (summary == null || dominant?.goalSpaceCandidates == null)
            {
                return;
            }

            var hasChain = TryGetTwoBoneChain(animator, dominant.goal, out var upper, out var lower, out var end);
            var upperPosition = hasChain ? upper.position : Vector3.zero;
            var upperToLowerLength = hasChain ? Vector3.Distance(upper.position, lower.position) : 0f;
            var lowerToEndLength = hasChain ? Vector3.Distance(lower.position, end.position) : 0f;
            var chainLength = upperToLowerLength + lowerToEndLength;
            var minReach = Mathf.Abs(upperToLowerLength - lowerToEndLength);
            foreach (var candidate in dominant.goalSpaceCandidates)
            {
                if (string.IsNullOrWhiteSpace(candidate.name))
                {
                    continue;
                }

                var row = GetGoalSpaceCandidateSummary(summary, candidate.name);
                var distance = Vector3.Distance(postPosition, candidate.worldPosition);
                row.sampleCount++;
                row.worldPosition.Include(candidate.worldPosition);
                row.minPostIkDistance = Mathf.Min(row.minPostIkDistance, distance);
                row.maxPostIkDistance = Mathf.Max(row.maxPostIkDistance, distance);
                if (hasChain)
                {
                    var targetDistanceFromUpper = Vector3.Distance(upperPosition, candidate.worldPosition);
                    var reachShortfall = Mathf.Max(0f, targetDistanceFromUpper - chainLength);
                    var insideMinReachAmount = Mathf.Max(0f, minReach - targetDistanceFromUpper);
                    row.minTargetDistanceFromUpper = Mathf.Min(row.minTargetDistanceFromUpper, targetDistanceFromUpper);
                    row.maxTargetDistanceFromUpper = Mathf.Max(row.maxTargetDistanceFromUpper, targetDistanceFromUpper);
                    row.minReachShortfall = Mathf.Min(row.minReachShortfall, reachShortfall);
                    row.maxReachShortfall = Mathf.Max(row.maxReachShortfall, reachShortfall);
                    row.maxInsideMinReachAmount = Mathf.Max(row.maxInsideMinReachAmount, insideMinReachAmount);
                }
            }
        }

        private static EditorCurveIkGoalDriverGoalSpaceCandidateSummary GetGoalSpaceCandidateSummary(
            EditorCurveIkGoalDriverFinalGoalSummary summary,
            string name)
        {
            var existing = summary.goalSpaceCandidates.FirstOrDefault(x => string.Equals(x.name, name, System.StringComparison.Ordinal));
            if (existing != null)
            {
                return existing;
            }

            var created = new EditorCurveIkGoalDriverGoalSpaceCandidateSummary
            {
                name = name,
            };
            summary.goalSpaceCandidates.Add(created);
            return created;
        }

        private void RecordClosestDescendantDistance(
            EditorCurveIkGoalDriverFinalGoalSummary summary,
            IkGoalFrameTarget dominant,
            Transform goalBone)
        {
            if (summary == null || dominant == null || goalBone == null)
            {
                return;
            }

            var closest = FindClosestDescendant(goalBone, dominant.worldPosition);
            if (closest == null)
            {
                return;
            }

            var distance = Vector3.Distance(closest.position, dominant.worldPosition);
            summary.closestDescendantSampleCount++;
            summary.closestDescendantLastPath = BuildPath(root, closest);
            summary.minClosestDescendantDistanceToDominantGoal = Mathf.Min(summary.minClosestDescendantDistanceToDominantGoal, distance);
            summary.maxClosestDescendantDistanceToDominantGoal = Mathf.Max(summary.maxClosestDescendantDistanceToDominantGoal, distance);
        }

        private void RecordBoneTargetCandidateDistances(
            Animator animator,
            EditorCurveIkGoalDriverFinalGoalSummary summary,
            IkGoalFrameTarget dominant,
            Transform goalBone)
        {
            if (animator == null || summary == null || dominant == null)
            {
                return;
            }

            foreach (var candidate in BuildBoneTargetCandidates(animator, dominant.goal, goalBone, dominant.worldPosition))
            {
                var row = GetBoneTargetCandidateSummary(summary, candidate.name, candidate.path);
                var distance = Vector3.Distance(candidate.worldPosition, dominant.worldPosition);
                row.sampleCount++;
                row.worldPosition.Include(candidate.worldPosition);
                row.localOffsetToDominantGoal.Include(candidate.localOffsetToGoal);
                row.minLocalOffsetLength = Mathf.Min(row.minLocalOffsetLength, candidate.localOffsetLength);
                row.maxLocalOffsetLength = Mathf.Max(row.maxLocalOffsetLength, candidate.localOffsetLength);
                row.minDistanceToDominantGoal = Mathf.Min(row.minDistanceToDominantGoal, distance);
                row.maxDistanceToDominantGoal = Mathf.Max(row.maxDistanceToDominantGoal, distance);
            }
        }

        private IEnumerable<BoneTargetCandidate> BuildBoneTargetCandidates(
            Animator animator,
            AvatarIKGoal goal,
            Transform goalBone,
            Vector3 worldPosition)
        {
            foreach (var humanBone in RelatedHumanBones(goal))
            {
                var transform = animator.GetBoneTransform(humanBone);
                if (transform == null)
                {
                    continue;
                }

                yield return new BoneTargetCandidate(
                    "human:" + humanBone,
                    BuildPath(root, transform),
                    transform.position,
                    ToLocalOffset(transform, worldPosition));
            }

            if (goalBone == null)
            {
                yield break;
            }

            var closest = FindClosestDescendant(goalBone, worldPosition);
            if (closest != null)
            {
                yield return new BoneTargetCandidate(
                    "closest_descendant_of_goal_bone",
                    BuildPath(root, closest),
                    closest.position,
                    ToLocalOffset(closest, worldPosition));
            }
        }

        private static Vector3 ToLocalOffset(Transform transform, Vector3 worldPosition)
        {
            if (transform == null)
            {
                return Vector3.zero;
            }

            // 目标点相对候选骨骼的本地偏移能帮助区分“真正的末端点偏移”
            // 和“少数帧碰巧最近”。这里只记录证据，不反向修正动画。
            return Quaternion.Inverse(transform.rotation) * (worldPosition - transform.position);
        }

        private static HumanBodyBones[] RelatedHumanBones(AvatarIKGoal goal)
        {
            switch (goal)
            {
                case AvatarIKGoal.LeftHand:
                    return new[] { HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand };
                case AvatarIKGoal.RightHand:
                    return new[] { HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand };
                case AvatarIKGoal.LeftFoot:
                    return new[] { HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes };
                case AvatarIKGoal.RightFoot:
                    return new[] { HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, HumanBodyBones.RightToes };
                default:
                    return System.Array.Empty<HumanBodyBones>();
            }
        }

        private static Transform FindClosestDescendant(Transform rootTransform, Vector3 worldPosition)
        {
            Transform best = null;
            var bestDistance = float.MaxValue;
            foreach (var transform in rootTransform.GetComponentsInChildren<Transform>(true))
            {
                var distance = Vector3.Distance(transform.position, worldPosition);
                if (distance < bestDistance)
                {
                    best = transform;
                    bestDistance = distance;
                }
            }

            return best;
        }

        private static EditorCurveIkGoalDriverBoneTargetCandidateSummary GetBoneTargetCandidateSummary(
            EditorCurveIkGoalDriverFinalGoalSummary summary,
            string name,
            string path)
        {
            var existing = summary.boneTargetCandidates.FirstOrDefault(x =>
                string.Equals(x.name, name, System.StringComparison.Ordinal) &&
                string.Equals(x.path, path, System.StringComparison.Ordinal));
            if (existing != null)
            {
                return existing;
            }

            var created = new EditorCurveIkGoalDriverBoneTargetCandidateSummary
            {
                name = name,
                path = path,
            };
            summary.boneTargetCandidates.Add(created);
            return created;
        }

        private static float CalculateTargetSpread(IReadOnlyList<IkGoalFrameTarget> targets)
        {
            var max = 0f;
            for (var i = 0; i < targets.Count; i++)
            {
                for (var j = i + 1; j < targets.Count; j++)
                {
                    max = Mathf.Max(max, Vector3.Distance(targets[i].worldPosition, targets[j].worldPosition));
                }
            }

            return max;
        }

        private static bool TrySolveScaleToReach(
            Vector3 bodyWorldPosition,
            Vector3 localOffsetWorld,
            Vector3 upperWorldPosition,
            float chainLength,
            out float fitScale,
            out Vector3 fitWorldPosition)
        {
            fitScale = 1f;
            fitWorldPosition = bodyWorldPosition + localOffsetWorld;
            if (chainLength <= 0.00001f || localOffsetWorld.sqrMagnitude <= 0.0000001f)
            {
                return false;
            }

            // 解 |body + offset * scale - upper| = chainLength。
            // 若同一批 hand/foot 的 scale 很接近，说明问题更像 body-local 尺度解释；
            // 若每个 limb 差异很大，则应继续查 endpoint、权重或 controller 语义。
            var bodyToUpper = bodyWorldPosition - upperWorldPosition;
            var a = Vector3.Dot(localOffsetWorld, localOffsetWorld);
            var b = 2f * Vector3.Dot(bodyToUpper, localOffsetWorld);
            var c = Vector3.Dot(bodyToUpper, bodyToUpper) - chainLength * chainLength;
            var discriminant = b * b - 4f * a * c;
            if (a <= 0.0000001f || discriminant < 0f)
            {
                return false;
            }

            var rootDiscriminant = Mathf.Sqrt(discriminant);
            var inv = 1f / (2f * a);
            var s0 = (-b - rootDiscriminant) * inv;
            var s1 = (-b + rootDiscriminant) * inv;
            if (!TryPickScale(s0, s1, out fitScale))
            {
                return false;
            }

            fitWorldPosition = bodyWorldPosition + localOffsetWorld * fitScale;
            return true;
        }

        private static bool TryPickScale(float a, float b, out float picked)
        {
            picked = 1f;
            var hasA = a >= 0f && !float.IsNaN(a) && !float.IsInfinity(a);
            var hasB = b >= 0f && !float.IsNaN(b) && !float.IsInfinity(b);
            if (!hasA && !hasB)
            {
                return false;
            }
            if (hasA && hasB)
            {
                picked = Mathf.Abs(a - 1f) <= Mathf.Abs(b - 1f) ? a : b;
                return true;
            }
            picked = hasA ? a : b;
            return true;
        }

        private bool TrySampleVector3Curve(string prefix, out Vector3 value)
        {
            value = default;
            if (!curves.TryGetValue(prefix + "x", out var x)
                || !curves.TryGetValue(prefix + "y", out var y)
                || !curves.TryGetValue(prefix + "z", out var z))
            {
                return false;
            }
            value = new Vector3(x.Evaluate(SampleTime), y.Evaluate(SampleTime), z.Evaluate(SampleTime));
            return true;
        }

        private bool TrySampleQuaternionCurve(string prefix, out Quaternion value)
        {
            value = default;
            if (!curves.TryGetValue(prefix + "x", out var x)
                || !curves.TryGetValue(prefix + "y", out var y)
                || !curves.TryGetValue(prefix + "z", out var z)
                || !curves.TryGetValue(prefix + "w", out var w))
            {
                return false;
            }
            value = new Quaternion(x.Evaluate(SampleTime), y.Evaluate(SampleTime), z.Evaluate(SampleTime), w.Evaluate(SampleTime));
            var length = Mathf.Sqrt(value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w);
            value = length <= 0.00001f
                ? Quaternion.identity
                : new Quaternion(value.x / length, value.y / length, value.z / length, value.w / length);
            return true;
        }

        private bool TryGetGoalBone(Animator animator, AvatarIKGoal goal, out Transform bone, out string bonePath)
        {
            bone = null;
            bonePath = null;
            if (animator == null)
            {
                return false;
            }

            var humanBone = ToHumanBodyBone(goal);
            if (humanBone == HumanBodyBones.LastBone)
            {
                return false;
            }

            bone = animator.GetBoneTransform(humanBone);
            if (bone == null)
            {
                return false;
            }

            bonePath = BuildPath(root, bone);
            return true;
        }

        private bool TryGetGoalHint(
            Animator animator,
            AvatarIKGoal goal,
            out AvatarIKHint hint,
            out Transform hintTransform,
            out string hintPath)
        {
            hint = AvatarIKHint.LeftKnee;
            hintTransform = null;
            hintPath = null;
            if (animator == null)
            {
                return false;
            }

            var humanBone = HumanBodyBones.LastBone;
            switch (goal)
            {
                case AvatarIKGoal.LeftFoot:
                    hint = AvatarIKHint.LeftKnee;
                    humanBone = HumanBodyBones.LeftLowerLeg;
                    break;
                case AvatarIKGoal.RightFoot:
                    hint = AvatarIKHint.RightKnee;
                    humanBone = HumanBodyBones.RightLowerLeg;
                    break;
                case AvatarIKGoal.LeftHand:
                    hint = AvatarIKHint.LeftElbow;
                    humanBone = HumanBodyBones.LeftLowerArm;
                    break;
                case AvatarIKGoal.RightHand:
                    hint = AvatarIKHint.RightElbow;
                    humanBone = HumanBodyBones.RightLowerArm;
                    break;
                default:
                    return false;
            }

            hintTransform = animator.GetBoneTransform(humanBone);
            if (hintTransform == null)
            {
                return false;
            }

            hintPath = BuildPath(root, hintTransform);
            return true;
        }

        private static bool TryGetTwoBoneChain(
            Animator animator,
            AvatarIKGoal goal,
            out Transform upper,
            out Transform lower,
            out Transform end)
        {
            upper = null;
            lower = null;
            end = null;
            if (animator == null)
            {
                return false;
            }

            HumanBodyBones upperBone;
            HumanBodyBones lowerBone;
            HumanBodyBones endBone;
            switch (goal)
            {
                case AvatarIKGoal.LeftHand:
                    upperBone = HumanBodyBones.LeftUpperArm;
                    lowerBone = HumanBodyBones.LeftLowerArm;
                    endBone = HumanBodyBones.LeftHand;
                    break;
                case AvatarIKGoal.RightHand:
                    upperBone = HumanBodyBones.RightUpperArm;
                    lowerBone = HumanBodyBones.RightLowerArm;
                    endBone = HumanBodyBones.RightHand;
                    break;
                case AvatarIKGoal.LeftFoot:
                    upperBone = HumanBodyBones.LeftUpperLeg;
                    lowerBone = HumanBodyBones.LeftLowerLeg;
                    endBone = HumanBodyBones.LeftFoot;
                    break;
                case AvatarIKGoal.RightFoot:
                    upperBone = HumanBodyBones.RightUpperLeg;
                    lowerBone = HumanBodyBones.RightLowerLeg;
                    endBone = HumanBodyBones.RightFoot;
                    break;
                default:
                    return false;
            }

            upper = animator.GetBoneTransform(upperBone);
            lower = animator.GetBoneTransform(lowerBone);
            end = animator.GetBoneTransform(endBone);
            return upper != null && lower != null && end != null;
        }

        private static void SolveTwoBoneCcd(
            Transform upper,
            Transform lower,
            Transform end,
            Vector3 target,
            float weight)
        {
            // 诊断用 CCD：只旋转上/下两段骨骼，不改骨长和层级。
            // 这样能直接判断 Hand/Foot goal 空间是否能被目标骨架追上。
            const int maxIterations = 8;
            const float stopDistance = 0.0005f;
            for (var i = 0; i < maxIterations; i++)
            {
                RotateJointTowardTarget(lower, end, target, weight);
                RotateJointTowardTarget(upper, end, target, weight);
                if (Vector3.Distance(end.position, target) <= stopDistance)
                {
                    break;
                }
            }
        }

        private static void RotateJointTowardTarget(
            Transform joint,
            Transform end,
            Vector3 target,
            float weight)
        {
            if (joint == null || end == null)
            {
                return;
            }

            var current = end.position - joint.position;
            var desired = target - joint.position;
            if (current.sqrMagnitude <= 0.0000001f || desired.sqrMagnitude <= 0.0000001f)
            {
                return;
            }

            var delta = Quaternion.FromToRotation(current, desired);
            if (weight < 0.999f)
            {
                delta = Quaternion.Slerp(Quaternion.identity, delta, weight);
            }
            joint.rotation = delta * joint.rotation;
        }

        private static HumanBodyBones ToHumanBodyBone(AvatarIKGoal goal)
        {
            switch (goal)
            {
                case AvatarIKGoal.LeftFoot:
                    return HumanBodyBones.LeftFoot;
                case AvatarIKGoal.RightFoot:
                    return HumanBodyBones.RightFoot;
                case AvatarIKGoal.LeftHand:
                    return HumanBodyBones.LeftHand;
                case AvatarIKGoal.RightHand:
                    return HumanBodyBones.RightHand;
                default:
                    return HumanBodyBones.LastBone;
            }
        }

        private static string BuildPath(Transform root, Transform transform)
        {
            if (root == null || transform == null)
            {
                return string.Empty;
            }
            if (root == transform)
            {
                return string.Empty;
            }

            var names = new List<string>();
            var current = transform;
            while (current != null && current != root)
            {
                names.Add(current.name);
                current = current.parent;
            }
            names.Reverse();
            return string.Join("/", names);
        }

        private sealed class IkGoalFrameTarget
        {
            public string key;
            public AvatarIKGoal goal;
            public int layerIndex;
            public bool hasPosition;
            public Vector3 localPosition;
            public Vector3 bodyWorldPosition;
            public Quaternion bodyWorldRotation;
            public float avatarHumanScale;
            public Vector3 worldPosition;
            public bool hasRotation;
            public Quaternion worldRotation;
            public GoalSpaceCandidateTarget[] goalSpaceCandidates;
            public float layerWeight;
            public string layerWeightSource;
            public bool hasPreIkBone;
            public Vector3 preIkBoneWorldPosition;
            public float preIkDistanceToGoal;
            public EditorCurveIkGoalDriverSample sample;
        }

        private sealed class GoalSpaceCandidateTarget
        {
            public GoalSpaceCandidateTarget(string name, Vector3 worldPosition)
            {
                this.name = name;
                this.worldPosition = worldPosition;
            }

            public string name;
            public Vector3 worldPosition;
        }

        private sealed class BoneTargetCandidate
        {
            public BoneTargetCandidate(string name, string path, Vector3 worldPosition, Vector3 localOffsetToGoal)
            {
                this.name = name;
                this.path = path;
                this.worldPosition = worldPosition;
                this.localOffsetToGoal = localOffsetToGoal;
                localOffsetLength = localOffsetToGoal.magnitude;
            }

            public string name;
            public string path;
            public Vector3 worldPosition;
            public Vector3 localOffsetToGoal;
            public float localOffsetLength;
        }

        private struct IkGoalReadback
        {
            public bool hasReadback;
            public Vector3 position;
            public float positionWeight;
            public float rotationWeight;
            public float distanceToGoal;
        }

        private struct IkHintReadback
        {
            public bool hasReadback;
            public Vector3 position;
            public float weight;
            public float distanceToHint;
        }
    }
}
