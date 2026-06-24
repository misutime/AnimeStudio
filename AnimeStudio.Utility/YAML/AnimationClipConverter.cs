using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ACLLibs;
using SevenZip;

namespace AnimeStudio
{
    public class AnimationClipConverter
    {
        public static readonly Regex UnknownPathRegex = new Regex($@"^{UnknownPathPrefix}[0-9]{{1,10}}$", RegexOptions.Compiled);

        private const string UnknownPathPrefix = "path_";
        private const string MissedPropertyPrefix = "missed_";
        private const string ScriptPropertyPrefix = "script_";
        private const string TypeTreePropertyPrefix = "typetree_";

        private readonly Game game;
        private readonly AnimationClip animationClip;
        private readonly CustomCurveResolver m_customCurveResolver;

        private readonly Dictionary<Vector3Curve, List<Keyframe<Vector3>>> m_translations = new Dictionary<Vector3Curve, List<Keyframe<Vector3>>>();
        private readonly Dictionary<QuaternionCurve, List<Keyframe<Quaternion>>> m_rotations = new Dictionary<QuaternionCurve, List<Keyframe<Quaternion>>>();
        private readonly Dictionary<Vector3Curve, List<Keyframe<Vector3>>> m_scales = new Dictionary<Vector3Curve, List<Keyframe<Vector3>>>();
        private readonly Dictionary<Vector3Curve, List<Keyframe<Vector3>>> m_eulers = new Dictionary<Vector3Curve, List<Keyframe<Vector3>>>();
        private readonly Dictionary<FloatCurve, List<Keyframe<Float>>> m_floats = new Dictionary<FloatCurve, List<Keyframe<Float>>>();
        private readonly Dictionary<PPtrCurve, List<PPtrKeyframe>> m_pptrs = new Dictionary<PPtrCurve, List<PPtrKeyframe>>();

        public List<Vector3Curve> Translations { get; private set; }
        public List<QuaternionCurve> Rotations { get; private set; }
        public List<Vector3Curve> Scales { get; private set; }
        public List<Vector3Curve> Eulers { get; private set; }
        public List<FloatCurve> Floats { get; private set; }
        public List<PPtrCurve> PPtrs { get; private set; }
        public string BindingSource { get; private set; }
        public int BindingCount { get; private set; }

        public AnimationClipConverter(AnimationClip clip)
        {
            game = clip.assetsFile.game;
            animationClip = clip;
            m_customCurveResolver = new CustomCurveResolver(animationClip);
        }

        public static AnimationClipConverter Process(AnimationClip clip)
        {
            var converter = new AnimationClipConverter(clip);
            converter.ProcessInner();
            return converter;
        }
        private void ProcessInner()
        {
            var m_Clip = animationClip.m_MuscleClip.m_Clip;
            var bindings = ResolveBindings(m_Clip);
            var tos = animationClip.FindTOS();

            var streamedFrames = m_Clip.m_StreamedClip.ReadData();
            var lastDenseFrame = m_Clip.m_DenseClip.m_FrameCount / m_Clip.m_DenseClip.m_SampleRate;
            var lastSampleFrame = streamedFrames.Count > 1 ? streamedFrames[streamedFrames.Count - 2].time : 0.0f;
            var lastFrame = Math.Max(lastDenseFrame, lastSampleFrame);

            if (m_Clip.m_ACLClip.IsSet && !game.Type.IsSRGroup())
            {
                var lastACLFrame = ProcessACLClip(m_Clip, bindings, tos);
                lastFrame = Math.Max(lastFrame, lastACLFrame);
                animationClip.m_Compressed = false;
            }
            var lastEndfieldAclFrame = ProcessEndfieldAclCompressedBuffer(bindings, tos);
            lastFrame = Math.Max(lastFrame, lastEndfieldAclFrame);
            var endfieldRootMotionAttributes = new HashSet<uint>();
            var lastEndfieldRootMotionFrame = ProcessEndfieldAclRootMotionBuffer(bindings, tos, endfieldRootMotionAttributes);
            lastFrame = Math.Max(lastFrame, lastEndfieldRootMotionFrame);
            var lastEndfieldFloatFrame = ProcessEndfieldAclFloatBuffer(bindings, tos, endfieldRootMotionAttributes);
            lastFrame = Math.Max(lastFrame, lastEndfieldFloatFrame);
            ProcessStreams(streamedFrames, bindings, tos, m_Clip.m_DenseClip.m_SampleRate);
            ProcessDenses(m_Clip, bindings, tos);
            if (m_Clip.m_ACLClip.IsSet && game.Type.IsSRGroup())
            {
                var lastACLFrame = ProcessACLClip(m_Clip, bindings, tos);
                lastFrame = Math.Max(lastFrame, lastACLFrame);
                animationClip.m_Compressed = false;
            }
            if (m_Clip.m_ConstantClip != null)
            {
                ProcessConstant(m_Clip, bindings, tos, lastFrame);
            }
            CreateCurves();
        }

        private AnimationClipBindingConstant ResolveBindings(Clip clip)
        {
            var directBindings = animationClip.m_ClipBindingConstant;
            if ((directBindings?.genericBindings?.Count ?? 0) > 0)
            {
                BindingSource = "AnimationClip.m_ClipBindingConstant";
                BindingCount = directBindings.genericBindings.Count;
                return directBindings;
            }

            if ((clip?.m_Binding?.m_ValueArray?.Count ?? 0) > 0)
            {
                // 有些 Unity 版本会把曲线 binding 存在 MuscleClip 的 ValueArray 中。
                // 解码流数据前必须先还原 GenericBinding，否则有效动画会被误判为空。
                var convertedBindings = clip.ConvertValueArrayToGenericBinding();
                convertedBindings.pptrCurveMapping = directBindings?.pptrCurveMapping ?? new List<PPtr<Object>>();
                BindingSource = "MuscleClip.m_Clip.m_Binding.ConvertValueArrayToGenericBinding";
                BindingCount = convertedBindings.genericBindings?.Count ?? 0;
                return convertedBindings;
            }

            BindingSource = "Missing";
            BindingCount = 0;
            return new AnimationClipBindingConstant
            {
                genericBindings = new List<GenericBinding>(),
                pptrCurveMapping = directBindings?.pptrCurveMapping ?? new List<PPtr<Object>>(),
            };
        }

        private void CreateCurves()
        {
            m_translations.AsEnumerable().ToList().ForEach(x => x.Key.curve.m_Curve.AddRange(x.Value));
            Translations = m_translations.Keys.ToList();
            m_rotations.AsEnumerable().ToList().ForEach(x => x.Key.curve.m_Curve.AddRange(x.Value));
            Rotations = m_rotations.Keys.ToList();
            m_scales.AsEnumerable().ToList().ForEach(x => x.Key.curve.m_Curve.AddRange(x.Value));
            Scales = m_scales.Keys.ToList();
            m_eulers.AsEnumerable().ToList().ForEach(x => x.Key.curve.m_Curve.AddRange(x.Value));
            Eulers = m_eulers.Keys.ToList();
            m_floats.AsEnumerable().ToList().ForEach(x => x.Key.curve.m_Curve.AddRange(x.Value));
            Floats = m_floats.Keys.ToList();
            m_pptrs.AsEnumerable().ToList().ForEach(x => x.Key.curve.AddRange(x.Value));
            PPtrs = m_pptrs.Keys.ToList();
        }

        private void ProcessStreams(List<StreamedClip.StreamedFrame> streamFrames, AnimationClipBindingConstant bindings, Dictionary<uint, string> tos, float sampleRate)
        {
            var curveValues = new float[4];
            var inSlopeValues = new float[4];
            var outSlopeValues = new float[4];
            var interval = 1.0f / sampleRate;

            // first (index [0]) stream frame is for slope calculation for the first real frame (index [1])
            // last one (index [count - 1]) is +Infinity
            // it is made for slope processing, but we don't need them
            for (var frameIndex = 1; frameIndex < streamFrames.Count - 1; frameIndex++)
            {
                var frame = streamFrames[frameIndex];
                for (var curveIndex = 0; curveIndex < frame.keyList.Count;)
                {
                    var curve = frame.keyList[curveIndex];
                    var index = curve.index;
                    if (!game.Type.IsSRGroup())
                        index += (int)animationClip.m_MuscleClip.m_Clip.m_ACLClip.CurveCount;
                    var binding = bindings.FindBinding(index);

                    var path = GetCurvePath(tos, binding.path);
                    if (binding.typeID == ClassIDType.Transform)
                    {
                        GetPreviousFrame(streamFrames, curve.index, frameIndex, out var prevFrameIndex, out var prevCurveIndex);
                        var dimension = binding.GetDimension();
                        for (int key = 0; key < dimension; key++)
                        {
                            var keyCurve = frame.keyList[curveIndex];
                            var prevFrame = streamFrames[prevFrameIndex];
                            var prevKeyCurve = prevFrame.keyList[prevCurveIndex + key];
                            var deltaTime = frame.time - prevFrame.time;
                            curveValues[key] = keyCurve.value;
                            inSlopeValues[key] = prevKeyCurve.CalculateNextInSlope(deltaTime, keyCurve);
                            outSlopeValues[key] = keyCurve.outSlope;
                            curveIndex = GetNextCurve(frame, curveIndex);
                        }

                        AddTransformCurve(frame.time, binding.attribute, curveValues, inSlopeValues, outSlopeValues, 0, path);
                    }
                    else if ((BindingCustomType)binding.customType == BindingCustomType.None)
                    {
                        AddDefaultCurve(binding, path, frame.time, frame.keyList[curveIndex].value);
                        curveIndex = GetNextCurve(frame, curveIndex);
                    }
                    else
                    {
                        AddCustomCurve(bindings, binding, path, frame.time, frame.keyList[curveIndex].value);
                        curveIndex = GetNextCurve(frame, curveIndex);
                    }
                }
            }
        }

        private void ProcessDenses(Clip clip, AnimationClipBindingConstant bindings, Dictionary<uint, string> tos)
        {
            var dense = clip.m_DenseClip;
            var streamCount = clip.m_StreamedClip.curveCount;
            var slopeValues = new float[4]; // no slopes - 0 values
            for (var frameIndex = 0; frameIndex < dense.m_FrameCount; frameIndex++)
            {
                var time = frameIndex / dense.m_SampleRate;
                var frameOffset = frameIndex * (int)dense.m_CurveCount;
                for (var curveIndex = 0; curveIndex < dense.m_CurveCount;)
                {
                    var index = (int)streamCount + curveIndex;
                    if (!game.Type.IsSRGroup())
                        index += (int)clip.m_ACLClip.CurveCount;
                    var binding = bindings.FindBinding(index);
                    var path = GetCurvePath(tos, binding.path);
                    var framePosition = frameOffset + curveIndex;
                    if (binding.typeID == ClassIDType.Transform)
                    {
                        AddTransformCurve(time, binding.attribute, dense.m_SampleArray, slopeValues, slopeValues, framePosition, path);
                        curveIndex += binding.GetDimension();
                    }
                    else if ((BindingCustomType)binding.customType == BindingCustomType.None)
                    {
                        AddDefaultCurve(binding, path, time, dense.m_SampleArray[framePosition]);
                        curveIndex++;
                    }
                    else
                    {
                        AddCustomCurve(bindings, binding, path, time, dense.m_SampleArray[framePosition]);
                        curveIndex++;
                    }
                }
            }
        }
        private float ProcessACLClip(Clip clip, AnimationClipBindingConstant bindings, Dictionary<uint, string> tos)
        {
            var acl = clip.m_ACLClip;
            acl.Process(game, out var values, out var times);
            float[] slopeValues = new float[4]; // no slopes - 0 values

            int frameCount = times.Length;
            if (frameCount == 0)
            {
                return 0.0f;
            }

            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                float time = times[frameIndex];
                int frameOffset = frameIndex * (int)acl.CurveCount;
                for (int curveIndex = 0; curveIndex < acl.CurveCount;)
                {
                    var index = curveIndex;
                    if (game.Type.IsSRGroup())
                        index += (int)(clip.m_DenseClip.m_CurveCount + clip.m_StreamedClip.curveCount);
                    GenericBinding binding = bindings.FindBinding(index);
                    string path = GetCurvePath(tos, binding.path);
                    int framePosition = frameOffset + curveIndex;
                    if (binding.typeID == ClassIDType.Transform)
                    {
                        AddTransformCurve(time, binding.attribute, values, slopeValues, slopeValues, framePosition, path);
                        curveIndex += binding.GetDimension();
                    }
                    else if ((BindingCustomType)binding.customType == BindingCustomType.None)
                    {
                        AddDefaultCurve(binding, path, time, values[framePosition]);
                        curveIndex++;
                    }
                    else
                    {
                        AddCustomCurve(bindings, binding, path, time, values[framePosition]);
                        curveIndex++;
                    }
                }
            }

            return times[frameCount - 1];
        }

        private float ProcessEndfieldAclCompressedBuffer(AnimationClipBindingConstant bindings, Dictionary<uint, string> tos)
        {
            var buffer = animationClip.m_AclCompressedBuffer;
            if (buffer == null || buffer.TransformBufferData.IsNullOrEmpty())
            {
                return 0.0f;
            }

            if (!EndfieldACL.TryGetInfo(buffer.TransformBufferData, out var info)
                || info.NumTracks == 0
                || info.NumSamples == 0
                || info.OutputFloatCount == 0)
            {
                return 0.0f;
            }

            var transformBindings = bindings.genericBindings?
                .Where(x => x.typeID == ClassIDType.Transform)
                .ToList() ?? new List<GenericBinding>();
            var positionBindings = transformBindings.Where(x => x.attribute == 1).ToList();
            var rotationBindings = transformBindings.Where(x => x.attribute == 2).ToList();
            var scaleBindingPaths = transformBindings
                .Where(x => x.attribute == 3)
                .Select(x => x.path)
                .ToHashSet();

            // Endfield 的 transform ACL 是一条 track 对应一个 Transform。
            // 多数 clip 的 position/rotation 顺序一致；少数 clip 只有 rotation 覆盖完整 track。
            // 因此用覆盖完整 track 的 binding 作为路径主键，避免被缺失或异序的 position 列表带偏。
            var trackBindings = rotationBindings.Count == info.NumTracks
                ? rotationBindings
                : positionBindings.Count == info.NumTracks
                    ? positionBindings
                    : null;
            if (trackBindings == null)
            {
                return 0.0f;
            }

            var values = new float[info.OutputFloatCount];
            var slopeValues = new float[4];
            var previousRotations = new float[info.NumTracks * 4];
            var sampleRate = info.SampleRate > 0.0f ? info.SampleRate : animationClip.m_SampleRate;
            var lastTime = 0.0f;

            for (var frameIndex = 0; frameIndex < info.NumSamples; frameIndex++)
            {
                var time = sampleRate > 0.0f ? frameIndex / sampleRate : 0.0f;
                if (!EndfieldACL.TryDecompressSample(buffer.TransformBufferData, time, values, out _))
                {
                    return lastTime;
                }

                lastTime = time;
                for (var trackIndex = 0; trackIndex < info.NumTracks; trackIndex++)
                {
                    var baseOffset = trackIndex * 12;
                    KeepQuaternionContinuous(values, baseOffset, previousRotations, trackIndex, frameIndex == 0);
                    var trackPathHash = trackBindings[trackIndex].path;
                    var trackPath = GetCurvePath(tos, trackPathHash);
                    AddTransformCurve(time, 1, values, slopeValues, slopeValues, baseOffset + 4, trackPath);
                    AddTransformCurve(time, 2, values, slopeValues, slopeValues, baseOffset + 0, trackPath);

                    if (scaleBindingPaths.Contains(trackPathHash))
                    {
                        AddTransformCurve(time, 3, values, slopeValues, slopeValues, baseOffset + 8, trackPath);
                    }
                }
            }

            animationClip.m_Compressed = false;
            return lastTime;
        }

        private float ProcessEndfieldAclRootMotionBuffer(
            AnimationClipBindingConstant bindings,
            Dictionary<uint, string> tos,
            HashSet<uint> handledAnimatorAttributes)
        {
            var buffer = animationClip.m_AclCompressedBuffer;
            if (buffer == null || buffer.RootMotionBufferData.IsNullOrEmpty())
            {
                return 0.0f;
            }

            if (!EndfieldACL.TryGetInfo(buffer.RootMotionBufferData, out var info)
                || info.NumTracks == 0
                || info.NumSamples == 0
                || info.OutputFloatCount == 0)
            {
                return 0.0f;
            }

            // Endfield 的 RootMotionBufferData 目前在样本中是 Humanoid 前 28 条标量：
            // MotionT/Q、RootT/Q、LeftFootT/Q、RightFootT/Q。这里按 attribute 顺序严格匹配，
            // 只在轨道数和 binding 数完全一致时写入，避免把 root 载荷错套到手部或其它 muscle。
            var rootBindings = bindings.genericBindings?
                .Where(x => x.typeID == ClassIDType.Animator
                    && (BindingCustomType)x.customType == BindingCustomType.AnimatorMuscle
                    && x.attribute < info.NumTracks)
                .OrderBy(x => x.attribute)
                .ToList() ?? new List<GenericBinding>();
            if (rootBindings.Count != info.NumTracks || info.OutputFloatCount < info.NumTracks)
            {
                return 0.0f;
            }

            var values = new float[info.OutputFloatCount];
            var sampleRate = info.SampleRate > 0.0f ? info.SampleRate : animationClip.m_SampleRate;
            var lastTime = 0.0f;
            for (var frameIndex = 0; frameIndex < info.NumSamples; frameIndex++)
            {
                var time = sampleRate > 0.0f ? frameIndex / sampleRate : 0.0f;
                if (!EndfieldACL.TryDecompressSample(buffer.RootMotionBufferData, time, values, out _))
                {
                    return lastTime;
                }

                lastTime = time;
                for (var trackIndex = 0; trackIndex < info.NumTracks; trackIndex++)
                {
                    var binding = rootBindings[trackIndex];
                    var path = GetCurvePath(tos, binding.path);
                    AddCustomCurve(bindings, binding, path, time, values[trackIndex]);
                    handledAnimatorAttributes?.Add(binding.attribute);
                }
            }

            animationClip.m_Compressed = false;
            return lastTime;
        }

        private float ProcessEndfieldAclFloatBuffer(
            AnimationClipBindingConstant bindings,
            Dictionary<uint, string> tos,
            HashSet<uint> skippedAnimatorAttributes)
        {
            var buffer = animationClip.m_AclCompressedBuffer;
            if (buffer == null || buffer.FloatBufferData.IsNullOrEmpty())
            {
                return 0.0f;
            }

            if (!EndfieldACL.TryGetInfo(buffer.FloatBufferData, out var info)
                || info.NumTracks == 0
                || info.NumSamples == 0
                || info.OutputFloatCount == 0)
            {
                return 0.0f;
            }

            var animatorBindings = bindings.genericBindings?
                .Where(x => x.typeID == ClassIDType.Animator)
                .ToList() ?? new List<GenericBinding>();
            if (animatorBindings.Count != info.NumTracks)
            {
                return 0.0f;
            }

            var values = new float[info.OutputFloatCount];
            var sampleRate = info.SampleRate > 0.0f ? info.SampleRate : animationClip.m_SampleRate;
            var lastTime = 0.0f;
            for (var frameIndex = 0; frameIndex < info.NumSamples; frameIndex++)
            {
                var time = sampleRate > 0.0f ? frameIndex / sampleRate : 0.0f;
                if (!EndfieldACL.TryDecompressSample(buffer.FloatBufferData, time, values, out _))
                {
                    return lastTime;
                }

                lastTime = time;
                for (var trackIndex = 0; trackIndex < info.NumTracks; trackIndex++)
                {
                    var binding = animatorBindings[trackIndex];
                    if (skippedAnimatorAttributes?.Contains(binding.attribute) == true)
                    {
                        continue;
                    }

                    var path = GetCurvePath(tos, binding.path);
                    AddCustomCurve(bindings, binding, path, time, values[trackIndex]);
                }
            }

            animationClip.m_Compressed = false;
            return lastTime;
        }

        private static void KeepQuaternionContinuous(float[] values, int baseOffset, float[] previousRotations, int trackIndex, bool isFirstFrame)
        {
            var previousOffset = trackIndex * 4;
            if (!isFirstFrame)
            {
                var dot = values[baseOffset + 0] * previousRotations[previousOffset + 0]
                    + values[baseOffset + 1] * previousRotations[previousOffset + 1]
                    + values[baseOffset + 2] * previousRotations[previousOffset + 2]
                    + values[baseOffset + 3] * previousRotations[previousOffset + 3];
                if (dot < 0.0f)
                {
                    values[baseOffset + 0] = -values[baseOffset + 0];
                    values[baseOffset + 1] = -values[baseOffset + 1];
                    values[baseOffset + 2] = -values[baseOffset + 2];
                    values[baseOffset + 3] = -values[baseOffset + 3];
                }
            }

            previousRotations[previousOffset + 0] = values[baseOffset + 0];
            previousRotations[previousOffset + 1] = values[baseOffset + 1];
            previousRotations[previousOffset + 2] = values[baseOffset + 2];
            previousRotations[previousOffset + 3] = values[baseOffset + 3];
        }
        private void ProcessConstant(Clip clip, AnimationClipBindingConstant bindings, Dictionary<uint, string> tos, float lastFrame)
        {
            var constant = clip.m_ConstantClip;
            var streamCount = clip.m_StreamedClip.curveCount;
            var denseCount = clip.m_DenseClip.m_CurveCount;
            var slopeValues = new float[4]; // no slopes - 0 values

            // only first and last frames
            var time = 0.0f;
            for (var i = 0; i < 2; i++, time += lastFrame)
            {
                for (var curveIndex = 0; curveIndex < constant.data.Length;)
                {
                    var index = (int)(streamCount + denseCount + curveIndex);
                    if (clip.m_ACLClip.IsSet)
                        index += (int)clip.m_ACLClip.CurveCount;
                    GenericBinding binding = bindings.FindBinding(index);
                    string path = GetCurvePath(tos, binding.path);
                    if (binding.typeID == ClassIDType.Transform)
                    {
                        AddTransformCurve(time, binding.attribute, constant.data, slopeValues, slopeValues, curveIndex, path);
                        curveIndex += binding.GetDimension();
                    }
                    else if ((BindingCustomType)binding.customType == BindingCustomType.None)
                    {
                        AddDefaultCurve(binding, path, time, constant.data[curveIndex]);
                        curveIndex++;
                    }
                    else
                    {
                        AddCustomCurve(bindings, binding, path, time, constant.data[curveIndex]);
                        curveIndex++;
                    }
                }
            }
        }

        private void AddCustomCurve(AnimationClipBindingConstant bindings, GenericBinding binding, string path, float time, float value)
        {
            switch ((BindingCustomType)binding.customType)
            {
                case BindingCustomType.AnimatorMuscle:
                    AddAnimatorMuscleCurve(binding, time, value);
                    break;
                case BindingCustomType.None:
                    // Endfield 的 ACL FloatBufferData 里会混入普通 Animator 标量。
                    // 这类曲线不是 Humanoid muscle，先按普通属性保留下来，避免阻断整条动画导出。
                    AddDefaultCurve(binding, path, time, value);
                    break;
                default:
                    string attribute = m_customCurveResolver.ToAttributeName((BindingCustomType)binding.customType, binding.attribute, path);
                    if (binding.isPPtrCurve == 0x01)
                    {
                        PPtrCurve curve = new PPtrCurve(path, attribute, binding.typeID, binding.script.Cast<MonoScript>());
                        AddPPtrKeyframe(curve, bindings, time, (int)value);
                    }
                    else
                    {
                        FloatCurve curve = new FloatCurve(path, attribute, binding.typeID, binding.script.Cast<MonoScript>());
                        AddFloatKeyframe(curve, time, value);
                    }
                    break;
            }
        }

        private void AddTransformCurve(float time, uint transType, float[] curveValues,
            float[] inSlopeValues, float[] outSlopeValues, int offset, string path)
        {
            if (!HasTransformCurveValues(transType, curveValues, inSlopeValues, outSlopeValues, offset))
            {
                return;
            }

            switch (transType)
            {
                case 1:
                    {
                        var curve = new Vector3Curve(path);
                        if (!m_translations.TryGetValue(curve, out List<Keyframe<Vector3>> transCurve))
                        {
                            transCurve = new List<Keyframe<Vector3>>();
                            m_translations.Add(curve, transCurve);
                        }

                        float x = curveValues[offset + 0];
                        float y = curveValues[offset + 1];
                        float z = curveValues[offset + 2];

                        float inX = inSlopeValues[0];
                        float inY = inSlopeValues[1];
                        float inZ = inSlopeValues[2];

                        float outX = outSlopeValues[0];
                        float outY = outSlopeValues[1];
                        float outZ = outSlopeValues[2];

                        Vector3 value = new Vector3(x, y, z);
                        Vector3 inSlope = new Vector3(inX, inY, inZ);
                        Vector3 outSlope = new Vector3(outX, outY, outZ);
                        Keyframe<Vector3> transKey = new Keyframe<Vector3>(time, value, inSlope, outSlope, AnimationClipExtensions.DefaultVector3Weight);
                        transCurve.Add(transKey);
                    }
                    break;
                case 2:
                    {
                        var curve = new QuaternionCurve(path);
                        if (!m_rotations.TryGetValue(curve, out List<Keyframe<Quaternion>> rotCurve))
                        {
                            rotCurve = new List<Keyframe<Quaternion>>();
                            m_rotations.Add(curve, rotCurve);
                        }

                        float x = curveValues[offset + 0];
                        float y = curveValues[offset + 1];
                        float z = curveValues[offset + 2];
                        float w = curveValues[offset + 3];

                        float inX = inSlopeValues[0];
                        float inY = inSlopeValues[1];
                        float inZ = inSlopeValues[2];
                        float inW = inSlopeValues[3];

                        float outX = outSlopeValues[0];
                        float outY = outSlopeValues[1];
                        float outZ = outSlopeValues[2];
                        float outW = outSlopeValues[3];

                        Quaternion value = new Quaternion(x, y, z, w);
                        Quaternion inSlope = new Quaternion(inX, inY, inZ, inW);
                        Quaternion outSlope = new Quaternion(outX, outY, outZ, outW);
                        Keyframe<Quaternion> rotKey = new Keyframe<Quaternion>(time, value, inSlope, outSlope, AnimationClipExtensions.DefaultQuaternionWeight);
                        rotCurve.Add(rotKey);
                    }
                    break;
                case 3:
                    {
                        var curve = new Vector3Curve(path);
                        if (!m_scales.TryGetValue(curve, out List<Keyframe<Vector3>> scaleCurve))
                        {
                            scaleCurve = new List<Keyframe<Vector3>>();
                            m_scales.Add(curve, scaleCurve);
                        }

                        float x = curveValues[offset + 0];
                        float y = curveValues[offset + 1];
                        float z = curveValues[offset + 2];

                        float inX = inSlopeValues[0];
                        float inY = inSlopeValues[1];
                        float inZ = inSlopeValues[2];

                        float outX = outSlopeValues[0];
                        float outY = outSlopeValues[1];
                        float outZ = outSlopeValues[2];

                        Vector3 value = new Vector3(x, y, z);
                        Vector3 inSlope = new Vector3(inX, inY, inZ);
                        Vector3 outSlope = new Vector3(outX, outY, outZ);
                        Keyframe<Vector3> scaleKey = new Keyframe<Vector3>(time, value, inSlope, outSlope, AnimationClipExtensions.DefaultVector3Weight);
                        scaleCurve.Add(scaleKey);
                    }
                    break;
                case 4:
                    {
                        var curve = new Vector3Curve(path);
                        if (!m_eulers.TryGetValue(curve, out List<Keyframe<Vector3>> eulerCurve))
                        {
                            eulerCurve = new List<Keyframe<Vector3>>();
                            m_eulers.Add(curve, eulerCurve);
                        }

                        float x = curveValues[offset + 0];
                        float y = curveValues[offset + 1];
                        float z = curveValues[offset + 2];

                        float inX = inSlopeValues[0];
                        float inY = inSlopeValues[1];
                        float inZ = inSlopeValues[2];

                        float outX = outSlopeValues[0];
                        float outY = outSlopeValues[1];
                        float outZ = outSlopeValues[2];

                        Vector3 value = new Vector3(x, y, z);
                        Vector3 inSlope = new Vector3(inX, inY, inZ);
                        Vector3 outSlope = new Vector3(outX, outY, outZ);
                        Keyframe<Vector3> eulerKey = new Keyframe<Vector3>(time, value, inSlope, outSlope, AnimationClipExtensions.DefaultVector3Weight);
                        eulerCurve.Add(eulerKey);
                    }
                    break;
                default:
                    throw new NotImplementedException(transType.ToString());
            }
        }

        private static bool HasTransformCurveValues(uint transType, float[] curveValues, float[] inSlopeValues, float[] outSlopeValues, int offset)
        {
            var dimension = transType == 2 ? 4 : transType is 1 or 3 or 4 ? 3 : 0;
            if (dimension == 0)
            {
                return true;
            }

            return curveValues != null
                && inSlopeValues != null
                && outSlopeValues != null
                && offset >= 0
                && offset + dimension <= curveValues.Length
                && dimension <= inSlopeValues.Length
                && dimension <= outSlopeValues.Length;
        }

        private void AddDefaultCurve(GenericBinding binding, string path, float time, float value)
        {
            switch (binding.typeID)
            {
                case ClassIDType.GameObject:
                    {
                        AddGameObjectCurve(binding, path, time, value);
                    }
                    break;

                case ClassIDType.MonoBehaviour:
                    {
                        AddScriptCurve(binding, path, time, value);
                    }
                    break;

                default:
                    AddEngineCurve(binding, path, time, value);
                    break;
            }
        }

        private void AddGameObjectCurve(GenericBinding binding, string path, float time, float value)
        {
            if (binding.attribute == CRC.CalculateDigestAscii("m_IsActive"))
            {
                FloatCurve curve = new FloatCurve(path, "m_IsActive", ClassIDType.GameObject, new PPtr<MonoScript>(0, 0, null));
                AddFloatKeyframe(curve, time, value);
                return;
            }
            else
            {
                // that means that dev exported animation clip with missing component
                FloatCurve curve = new FloatCurve(path, MissedPropertyPrefix + binding.attribute, ClassIDType.GameObject, new PPtr<MonoScript>(0, 0, null));
                AddFloatKeyframe(curve, time, value);
            }
        }

        private void AddScriptCurve(GenericBinding binding, string path, float time, float value)
        {
#warning TODO:
            FloatCurve curve = new FloatCurve(path, ScriptPropertyPrefix + binding.attribute, ClassIDType.MonoBehaviour, binding.script.Cast<MonoScript>());
            AddFloatKeyframe(curve, time, value);
        }

        private void AddEngineCurve(GenericBinding binding, string path, float time, float value)
        {
#warning TODO:
            FloatCurve curve = new FloatCurve(path, TypeTreePropertyPrefix + binding.attribute, binding.typeID, new PPtr<MonoScript>(0, 0, null));
            AddFloatKeyframe(curve, time, value);
        }

        private void AddAnimatorMuscleCurve(GenericBinding binding, float time, float value)
        {
            FloatCurve curve = new FloatCurve(string.Empty, binding.GetHumanoidMuscle().ToAttributeString(), ClassIDType.Animator, new PPtr<MonoScript>(0, 0, null));
            AddFloatKeyframe(curve, time, value);
        }

        private void AddFloatKeyframe(FloatCurve curve, float time, float value)
        {
            if (!m_floats.TryGetValue(curve, out List<Keyframe<Float>> floatCurve))
            {
                floatCurve = new List<Keyframe<Float>>();
                m_floats.Add(curve, floatCurve);
            }

            Keyframe<Float> floatKey = new Keyframe<Float>(time, value, default, default, AnimationClipExtensions.DefaultFloatWeight);
            floatCurve.Add(floatKey);
        }

        private void AddPPtrKeyframe(PPtrCurve curve, AnimationClipBindingConstant bindings, float time, int index)
        {
            if (!m_pptrs.TryGetValue(curve, out List<PPtrKeyframe> pptrCurve))
            {
                pptrCurve = new List<PPtrKeyframe>();
                m_pptrs.Add(curve, pptrCurve);
                AddPPtrKeyframe(curve, bindings, 0.0f, index - 1);
            }

            PPtr<Object> value = bindings.pptrCurveMapping[index];
            PPtrKeyframe pptrKey = new PPtrKeyframe(time, value);
            pptrCurve.Add(pptrKey);
        }

        private void GetPreviousFrame(List<StreamedClip.StreamedFrame> streamFrames, int curveID, int currentFrame, out int frameIndex, out int curveIndex)
        {
            for (frameIndex = currentFrame - 1; frameIndex >= 0; frameIndex--)
            {
                var frame = streamFrames[frameIndex];
                for (curveIndex = 0; curveIndex < frame.keyList.Count; curveIndex++)
                {
                    var curve = frame.keyList[curveIndex];
                    if (curve.index == curveID)
                    {
                        return;
                    }
                }
            }
            throw new Exception($"There is no curve with index {curveID} in any of previous frames");
        }

        private int GetNextCurve(StreamedClip.StreamedFrame frame, int currentCurve)
        {
            var curve = frame.keyList[currentCurve];
            int i = currentCurve + 1;
            for (; i < frame.keyList.Count; i++)
            {
                if (frame.keyList[i].index != curve.index)
                {
                    return i;
                }
            }
            return i;
        }

        private static string GetCurvePath(Dictionary<uint, string> tos, uint hash)
        {
            if (tos.TryGetValue(hash, out string path))
            {
                return path;
            }
            else
            {
                return UnknownPathPrefix + hash;
            }
        }
        
    }
}
