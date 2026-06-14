using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using AnimeStudio;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AnimeStudio.CLI
{
    internal static class FastPreviewGltfBuilder
    {
        internal const string HumanoidSolverMode = "AnimeStudioInternalHumanoidMusclePreviewV1";
        internal const string HumanoidSolverRestPoseSpace = "UnityAvatarPreSwingTwistInversePostLocalRotationAxisZYX_DistalTwistSourceLimitsUpperLegFootTwistY_KnownLimbRiskDiagnostics";
        internal const string HumanoidSolverCacheVersion = HumanoidSolverMode + ":" + HumanoidSolverRestPoseSpace;

        public static bool TryGenerate(JObject modelEntry, JObject animationEntry, string animationName, string outputDirectory, out string gltfPath, out string message, bool forceInternalHumanoidSolve = false)
        {
            gltfPath = null;
            message = null;

            var model = modelEntry?["model"] as JObject;
            var sourceGltf = (string)model?["output"];
            var animationAsset = (string)animationEntry?["animationAsset"];
            if (string.IsNullOrWhiteSpace(sourceGltf) || !File.Exists(sourceGltf))
            {
                message = "快速预览缺少已导出的模型 glTF。";
                return false;
            }
            if (string.IsNullOrWhiteSpace(animationAsset) || !File.Exists(animationAsset))
            {
                message = "快速预览缺少动画 sidecar JSON。";
                return false;
            }

            var animationJson = JObject.Parse(File.ReadAllText(animationAsset));
            var decoded = animationJson["decoded"] as JObject;
            if (!string.Equals((string)decoded?["status"], "ok", StringComparison.OrdinalIgnoreCase))
            {
                message = $"动画 sidecar 没有可用 decoded 曲线。{BuildDecodedHint(decoded)}";
                return false;
            }

            var candidate = animationEntry?["candidate"] as JObject;
            var partialDirectGltf = (bool?)candidate?["partialDirectGltf"] == true
                || string.Equals((string)candidate?["productionAnimationPath"], "DirectGltfTransformOnly", StringComparison.OrdinalIgnoreCase);

            if (!partialDirectGltf
                && (forceInternalHumanoidSolve
                    || (bool?)decoded["requiresInternalHumanoidSolve"] == true
                    || string.Equals((string)decoded["playbackKind"], "HumanoidMuscleNeedsInternalSolver", StringComparison.OrdinalIgnoreCase)))
            {
                return TryGenerateHumanoidMusclePreview(
                    model,
                    sourceGltf,
                    animationAsset,
                    decoded,
                    animationName,
                    outputDirectory,
                    out gltfPath,
                    out message
                );
            }

            var hasBlendShapeCurves = HasBlendShapeFloatCurves(decoded);
            if ((bool?)decoded["directGltfReady"] == false && !hasBlendShapeCurves && !partialDirectGltf)
            {
                message = $"动画 sidecar 标记为不能直接写入 glTF。{BuildDecodedHint(decoded)}";
                return false;
            }

            var curves = LoadCurves(decoded);
            if (curves.Count == 0 && !hasBlendShapeCurves)
            {
                message = $"decoded 曲线里没有 Transform TRS 或 BlendShape 轨道。{BuildDecodedHint(decoded)}";
                return false;
            }

            var output = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(Path.GetDirectoryName(sourceGltf) ?? Directory.GetCurrentDirectory(), "FastPreview", SafeName(animationName))
                : outputDirectory;
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
            Directory.CreateDirectory(output);

            var sourceDir = Path.GetDirectoryName(Path.GetFullPath(sourceGltf));
            CopyDirectory(sourceDir, output);
            gltfPath = Path.Combine(output, Path.GetFileName(sourceGltf));
            var gltf = JObject.Parse(File.ReadAllText(gltfPath));
            var nodes = gltf["nodes"] as JArray;
            if (nodes == null || nodes.Count == 0)
            {
                message = "模型 glTF 没有 nodes。";
                return false;
            }

            var nodeMap = BuildNodePathMap(gltf, nodes);
            var blendShapeCurves = LoadBlendShapeCurves(decoded, gltf, nodes, nodeMap, out var blendShapeDiagnostic);
            var writer = new AnimationBinaryWriter();
            var samplers = new JArray();
            var channels = new JArray();
            var matched = 0;

            foreach (var curveGroup in curves.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (!TryFindNode(nodeMap, curveGroup.Key, out var nodeIndex))
                {
                    continue;
                }

                foreach (var curve in curveGroup)
                {
                    var samplerIndex = samplers.Count;
                    var timeAccessor = writer.AddAccessor(gltf, curve.Times, "SCALAR");
                    var valueAccessor = writer.AddAccessor(gltf, curve.Values, curve.ComponentCount == 4 ? "VEC4" : "VEC3");
                    samplers.Add(new JObject
                    {
                        ["input"] = timeAccessor,
                        ["interpolation"] = "LINEAR",
                        ["output"] = valueAccessor,
                    });
                    channels.Add(new JObject
                    {
                        ["sampler"] = samplerIndex,
                        ["target"] = new JObject
                        {
                            ["node"] = nodeIndex,
                            ["path"] = curve.TargetPath,
                        },
                    });
                    matched++;
                }
            }
            matched += AddBlendShapeChannels(gltf, writer, samplers, channels, blendShapeCurves);

            if (matched == 0)
            {
                message = "动画曲线没有匹配到 glTF 节点或 morph target。";
                return false;
            }

            writer.Flush(gltf, output, $"{SafeName(animationName)}.fastpreview.bin");
            var animations = new JArray();
            gltf["animations"] = animations;
            animations.Add(new JObject
            {
                ["name"] = animationName,
                ["samplers"] = samplers,
                ["channels"] = channels,
                ["extras"] = new JObject
                {
                    ["animeStudioPreview"] = new JObject
                    {
                        ["mode"] = "FastDecodedSidecar",
                        ["version"] = 2,
                        ["animationAsset"] = animationAsset,
                        ["matchedChannelCount"] = matched,
                        ["curveCount"] = curves.Count,
                        ["blendShape"] = blendShapeDiagnostic,
                        ["coordinateConversion"] = "Unity local TRS converted to glTF basis: position.x=-x, rotation.y/z=-y/-z",
                        ["partialDirectGltf"] = partialDirectGltf,
                        ["partialDirectGltfReason"] = partialDirectGltf
                            ? (string)candidate?["partialDirectGltfReason"] ?? "This preview writes deterministic Transform TRS curves only. Full Humanoid/Muscle body motion still needs Unity bake or a trusted Humanoid solver."
                            : null,
                        ["note"] = partialDirectGltf
                            ? "快速预览只写入 Transform TRS 曲线；完整 Humanoid/Muscle 动作仍需 Unity bake 或可信 Humanoid solver。"
                            : "快速预览直接使用已导出 glTF 和 animation_asset.json decoded 曲线，不重新加载 Unity bundle。",
                    },
                },
            });

            File.WriteAllText(gltfPath, gltf.ToString(Formatting.Indented));
            message = $"快速预览完成: {matched} channel(s)";
            return true;
        }

        private static List<CurveData> LoadCurves(JObject decoded)
        {
            var result = new List<CurveData>();
            AddVector3Curves(result, decoded["translations"] as JArray, "translation", ConvertUnityPositionToGltf);
            AddQuaternionCurves(result, decoded["rotations"] as JArray);
            AddVector3Curves(result, decoded["scales"] as JArray, "scale");
            return result;
        }

        private static bool HasBlendShapeFloatCurves(JObject decoded)
        {
            return decoded?["floats"]?.OfType<JObject>().Any(IsBlendShapeFloatCurve) == true;
        }

        private static List<BlendShapeCurveData> LoadBlendShapeCurves(JObject decoded, JObject gltf, JArray nodes, Dictionary<string, int> nodeMap, out JObject diagnostic)
        {
            var result = new List<BlendShapeCurveData>();
            var items = new JArray();
            diagnostic = new JObject
            {
                ["status"] = "not_present",
                ["mode"] = "DecodedBlendShapeToGltfWeights",
                ["items"] = items,
            };

            foreach (var curve in decoded?["floats"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                if (!IsBlendShapeFloatCurve(curve))
                {
                    continue;
                }

                var path = (string)curve["path"];
                var channelName = NormalizeBlendShapeName((string)curve["attribute"]);
                var item = new JObject
                {
                    ["path"] = path,
                    ["channelName"] = channelName,
                };
                items.Add(item);

                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(channelName))
                {
                    item["status"] = "missing_path_or_channel";
                    continue;
                }
                if (!TryFindNode(nodeMap, path, out var nodeIndex))
                {
                    item["status"] = "missing_gltf_node";
                    continue;
                }
                item["nodeIndex"] = nodeIndex;

                if (!TryGetMorphTargetMap(gltf, nodes, nodeIndex, out var targetMap, out var targetCount))
                {
                    item["status"] = "node_has_no_morph_targets";
                    continue;
                }
                if (!targetMap.TryGetValue(channelName, out var targetIndex))
                {
                    item["status"] = "missing_target_name";
                    item["targetNames"] = new JArray(targetMap.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(32));
                    continue;
                }

                var keys = ReadFloatKeys(curve["keyframes"] as JArray);
                if (keys.Count == 0)
                {
                    item["status"] = "no_keyframes";
                    continue;
                }

                result.Add(new BlendShapeCurveData(nodeIndex, channelName, targetIndex, targetCount, keys));
                item["targetIndex"] = targetIndex;
                item["targetCount"] = targetCount;
                item["keyframes"] = keys.Count;
                item["status"] = "matched";
            }

            diagnostic["matchedCurveCount"] = result.Count;
            diagnostic["status"] = result.Count > 0 ? "ok" : items.Count > 0 ? "no_curves_matched" : "not_present";
            diagnostic["note"] = "只把 category=BlendShape 或 attribute=blendShape.* 且能匹配 glTF extras.targetNames 的曲线写成 weights；材质/显隐/Renderer 属性曲线不进入 morph。";
            return result;
        }

        private static int AddBlendShapeChannels(JObject gltf, AnimationBinaryWriter writer, JArray samplers, JArray channels, List<BlendShapeCurveData> curves)
        {
            var matched = 0;
            foreach (var group in curves.GroupBy(x => x.NodeIndex))
            {
                var first = group.First();
                if (first.TargetCount <= 0)
                {
                    continue;
                }

                var times = group
                    .SelectMany(x => x.Keys.Select(k => k.Time))
                    .Distinct()
                    .OrderBy(x => x)
                    .ToArray();
                if (times.Length == 0)
                {
                    continue;
                }

                var values = new List<float>(times.Length * first.TargetCount);
                foreach (var time in times)
                {
                    var weights = new float[first.TargetCount];
                    foreach (var curve in group)
                    {
                        if (curve.TargetIndex < 0 || curve.TargetIndex >= weights.Length)
                        {
                            continue;
                        }
                        weights[curve.TargetIndex] = SampleFloatKeys(curve.Keys, time) / 100.0f;
                    }
                    values.AddRange(weights);
                }

                var samplerIndex = samplers.Count;
                var timeAccessor = writer.AddAccessor(gltf, times, "SCALAR");
                var valueAccessor = writer.AddAccessor(gltf, values.ToArray(), "SCALAR");
                samplers.Add(new JObject
                {
                    ["input"] = timeAccessor,
                    ["interpolation"] = "LINEAR",
                    ["output"] = valueAccessor,
                });
                channels.Add(new JObject
                {
                    ["sampler"] = samplerIndex,
                    ["target"] = new JObject
                    {
                        ["node"] = first.NodeIndex,
                        ["path"] = "weights",
                    },
                });
                matched++;
            }
            return matched;
        }

        private static bool TryGenerateHumanoidMusclePreview(
            JObject model,
            string sourceGltf,
            string animationAsset,
            JObject decoded,
            string animationName,
            string outputDirectory,
            out string gltfPath,
            out string message)
        {
            gltfPath = null;
            message = null;
            var avatar = model?["avatar"] as JObject;
            if (avatar?["internalSolver"] == null)
            {
                message = "Humanoid/Muscle 预览缺少 model.avatar.internalSolver。请用新导出重建模型索引；不能退回 Unity bake 或骨骼数量猜测。";
                return false;
            }
            if (!HasRequiredHumanoidSolverPoseMetadata(avatar["internalSolver"] as JObject, out var metadataMessage))
            {
                message = metadataMessage;
                return false;
            }

            var output = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(Path.GetDirectoryName(sourceGltf) ?? Directory.GetCurrentDirectory(), "FastPreview", SafeName(animationName))
                : outputDirectory;
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
            Directory.CreateDirectory(output);

            var sourceDir = Path.GetDirectoryName(Path.GetFullPath(sourceGltf));
            CopyDirectory(sourceDir, output);
            gltfPath = Path.Combine(output, Path.GetFileName(sourceGltf));
            var gltf = JObject.Parse(File.ReadAllText(gltfPath));
            var nodes = gltf["nodes"] as JArray;
            if (nodes == null || nodes.Count == 0)
            {
                message = "模型 glTF 没有 nodes。";
                return false;
            }

            var nodeMap = BuildNodePathMap(gltf, nodes);
            var curves = LoadCurves(decoded);
            var humanoidCurves = LoadHumanoidMuscleCurves(avatar, decoded, nodes, nodeMap, out var diagnostic);
            var rootMotionCurves = LoadHumanoidRootMotionCurves(decoded, gltf, nodes, out var rootMotionDiagnostic);
            var blendShapeCurves = LoadBlendShapeCurves(decoded, gltf, nodes, nodeMap, out var blendShapeDiagnostic);
            foreach (var rootCurve in rootMotionCurves)
            {
                curves.RemoveAll(x =>
                    string.Equals(x.Path, rootCurve.Path, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.TargetPath, rootCurve.TargetPath, StringComparison.OrdinalIgnoreCase));
            }
            curves.AddRange(humanoidCurves);
            curves.AddRange(rootMotionCurves);
            if (curves.Count == 0 && blendShapeCurves.Count == 0)
            {
                message = "Humanoid/Muscle 内部求解器没有生成任何 TRS 轨道。" + BuildHumanoidDiagnosticHint(diagnostic);
                return false;
            }

            var writer = new AnimationBinaryWriter();
            var samplers = new JArray();
            var channels = new JArray();
            var matched = 0;
            foreach (var curve in curves)
            {
                if (!TryFindNode(nodeMap, curve.Path, out var nodeIndex))
                {
                    continue;
                }

                var samplerIndex = samplers.Count;
                var timeAccessor = writer.AddAccessor(gltf, curve.Times, "SCALAR");
                var valueAccessor = writer.AddAccessor(gltf, curve.Values, curve.ComponentCount == 4 ? "VEC4" : "VEC3");
                samplers.Add(new JObject
                {
                    ["input"] = timeAccessor,
                    ["interpolation"] = "LINEAR",
                    ["output"] = valueAccessor,
                });
                channels.Add(new JObject
                {
                    ["sampler"] = samplerIndex,
                    ["target"] = new JObject
                    {
                        ["node"] = nodeIndex,
                        ["path"] = curve.TargetPath,
                    },
                });
                matched++;
            }
            matched += AddBlendShapeChannels(gltf, writer, samplers, channels, blendShapeCurves);

            if (matched == 0)
            {
                message = "Humanoid/Muscle 求解轨道或 BlendShape 曲线没有匹配到 glTF 节点。" + BuildHumanoidDiagnosticHint(diagnostic);
                return false;
            }

            writer.Flush(gltf, output, $"{SafeName(animationName)}.humanoidpreview.bin");
            var animations = new JArray();
            gltf["animations"] = animations;
            animations.Add(new JObject
            {
                ["name"] = animationName,
                ["samplers"] = samplers,
                ["channels"] = channels,
                ["extras"] = new JObject
                {
                    ["unityHumanoid"] = new JObject
                    {
                        ["present"] = true,
                        ["baked"] = false,
                        ["internalSolved"] = humanoidCurves.Count > 0,
                        ["solveMode"] = HumanoidSolverMode,
                        ["solverCacheVersion"] = HumanoidSolverCacheVersion,
                        ["solvedTrackCount"] = humanoidCurves.Count,
                        ["solvedKeyframeCount"] = humanoidCurves.Sum(x => x.Times.Length),
                        ["muscleCurveCount"] = (int?)diagnostic["matchingMuscleCurveCount"] ?? 0,
                        ["keyframeCount"] = (int?)diagnostic["matchingMuscleKeyframeCount"] ?? 0,
                        ["attributes"] = diagnostic["matchingMuscleAttributes"] ?? new JArray(),
                        ["diagnostics"] = diagnostic,
                        ["rootMotion"] = rootMotionDiagnostic,
                        ["blendShape"] = blendShapeDiagnostic,
                    },
                    ["animeStudioPreview"] = new JObject
                    {
                        ["mode"] = "InternalHumanoidMuscleSidecar",
                        ["version"] = 2,
                        ["animationAsset"] = animationAsset,
                        ["matchedChannelCount"] = matched,
                        ["curveCount"] = curves.Count,
                        ["decodedTransformCurveCount"] = curves.Count - humanoidCurves.Count - rootMotionCurves.Count,
                        ["internalHumanoidCurveCount"] = humanoidCurves.Count,
                        ["rootMotionCurveCount"] = rootMotionCurves.Count,
                        ["blendShapeCurveCount"] = blendShapeCurves.Count,
                        ["note"] = "实验性内部 Humanoid/Muscle 预览：只使用已导出的 Avatar internalSolver 与 animation_asset decoded muscle 曲线，不调用 Unity bake。",
                    },
                },
            });

            File.WriteAllText(gltfPath, gltf.ToString(Formatting.Indented));
            message = $"Humanoid/Muscle 内部预览完成: {matched} channel(s)";
            return true;
        }

        private static string BuildDecodedHint(JObject decoded)
        {
            if (decoded == null)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            var status = (string)decoded["status"];
            var playbackKind = (string)decoded["playbackKind"];
            var bindingSource = (string)decoded["bindingSource"];
            var note = (string)decoded["note"];
            if (!string.IsNullOrWhiteSpace(status))
            {
                parts.Add($"status={status}");
            }
            if (!string.IsNullOrWhiteSpace(playbackKind))
            {
                parts.Add($"playbackKind={playbackKind}");
            }
            if (!string.IsNullOrWhiteSpace(bindingSource))
            {
                parts.Add($"bindingSource={bindingSource}");
            }
            var decoderInput = decoded["decoderInput"] as JObject;
            if (decoderInput != null)
            {
                var clipBindings = (int?)decoderInput["clipBindingCount"] ?? 0;
                var valueBindings = (int?)decoderInput["valueBindingCount"] ?? 0;
                var aclCurves = (int?)decoderInput["aclCurveCount"] ?? 0;
                var streamedCurves = (int?)decoderInput["streamedCurveCount"] ?? 0;
                var denseCurves = (int?)decoderInput["denseCurveCount"] ?? 0;
                var constantValues = (int?)decoderInput["constantValueCount"] ?? 0;
                parts.Add($"decoderInput=bindings:{clipBindings},valueBindings:{valueBindings},acl:{aclCurves},streamed:{streamedCurves},dense:{denseCurves},constant:{constantValues}");
            }
            if (!string.IsNullOrWhiteSpace(note))
            {
                parts.Add($"note={note}");
            }
            return parts.Count == 0 ? string.Empty : " " + string.Join("; ", parts);
        }

        private static List<CurveData> LoadHumanoidMuscleCurves(
            JObject avatar,
            JObject decoded,
            JArray nodes,
            Dictionary<string, int> nodeMap,
            out JObject diagnostic)
        {
            var result = new List<CurveData>();
            var diagnostics = new JArray();
            var knownRiskTargets = new JArray();
            diagnostic = new JObject
            {
                ["status"] = "experimental_internal_solver",
                ["mode"] = HumanoidSolverMode,
                ["cacheVersion"] = HumanoidSolverCacheVersion,
                ["restPoseSpace"] = HumanoidSolverRestPoseSpace,
                ["targetCount"] = HumanoidTarget.Targets.Length,
                ["targets"] = diagnostics,
                ["knownRiskTargets"] = knownRiskTargets,
            };

            var humanBoneMap = ParseHumanBoneMap(avatar);
            var solver = avatar["internalSolver"] as JObject;
            var humanBoneIndex = solver?["humanBoneIndex"] as JArray;
            var solverSkeleton = solver?["skeleton"] as JObject;
            var solverNodes = solverSkeleton?["nodes"] as JArray;
            var solverAxes = solverSkeleton?["axes"] as JArray;
            var curves = LoadFloatCurves(decoded["floats"] as JArray);
            diagnostic["curveAttributes"] = new JArray(curves.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            var matchingAttributes = SelectMatchingHumanoidMuscleAttributes(curves);
            diagnostic["matchingMuscleAttributes"] = new JArray(matchingAttributes);
            diagnostic["matchingMuscleCurveCount"] = matchingAttributes.Length;
            diagnostic["matchingMuscleKeyframeCount"] = matchingAttributes.Sum(x => curves.TryGetValue(x, out var keys) ? keys.Count : 0);
            if (humanBoneIndex == null || solverNodes == null || solverAxes == null || curves.Count == 0)
            {
                diagnostic["status"] = "missing_solver_input";
                diagnostic["humanBoneCount"] = humanBoneMap.Count;
                diagnostic["muscleCurveCount"] = curves.Count;
                diagnostic["summary"] = BuildHumanoidDiagnosticSummary(diagnostics);
                return result;
            }
            if (matchingAttributes.Length == 0)
            {
                diagnostic["status"] = "no_humanoid_muscle_curves";
                diagnostic["humanBoneCount"] = humanBoneMap.Count;
                diagnostic["muscleCurveCount"] = curves.Count;
                diagnostic["summary"] = BuildHumanoidDiagnosticSummary(diagnostics);
                return result;
            }

            var times = curves.Values
                .SelectMany(x => x.Select(k => k.Time))
                .Distinct()
                .OrderBy(x => x)
                .ToArray();
            diagnostic["sampleTimeCount"] = times.Length;
            if (times.Length == 0)
            {
                diagnostic["status"] = "no_sample_times";
                return result;
            }

            foreach (var target in HumanoidTarget.Targets)
            {
                var item = new JObject
                {
                    ["humanBone"] = target.HumanBone,
                    ["attributes"] = new JArray(new[] { target.XAttribute, target.YAttribute, target.ZAttribute, target.ExtraZAttribute }.Where(x => !string.IsNullOrWhiteSpace(x))),
                };
                diagnostics.Add(item);

                if (!TryGetAvatarAxis(target.HumanBone, humanBoneIndex, solverNodes, solverAxes, out var axis))
                {
                    item["status"] = "missing_avatar_axis";
                    continue;
                }
                item["avatarSkeletonNodeIndex"] = axis.SkeletonNodeIndex;
                item["avatarAxesId"] = axis.AxesId;

                // 优先使用 AvatarConstant.m_Human.m_Skeleton.m_ID -> Avatar.m_TOS 还原出来的 Unity 路径。
                // 没有这个确定性路径时，才使用 HumanDescription 的骨骼名作为补充。
                var targetPath = GetSolverNodePath(solverNodes, axis.SkeletonNodeIndex);
                if (string.IsNullOrWhiteSpace(targetPath)
                    && humanBoneMap.TryGetValue(target.HumanBone, out var mappedBoneName))
                {
                    targetPath = mappedBoneName;
                }

                if (string.IsNullOrWhiteSpace(targetPath))
                {
                    item["status"] = "missing_avatar_node_path";
                    continue;
                }
                item["boneName"] = targetPath;

                if (!TryFindNode(nodeMap, targetPath, out var nodeIndex))
                {
                    item["status"] = "missing_gltf_node";
                    continue;
                }
                item["nodeIndex"] = nodeIndex;

                var hasCurve = HasFloatCurve(curves, target.XAttribute)
                    || HasFloatCurve(curves, target.YAttribute)
                    || HasFloatCurve(curves, target.ZAttribute)
                    || HasFloatCurve(curves, target.ExtraZAttribute);
                if (!hasCurve)
                {
                    item["status"] = "no_matching_muscle_curve";
                    continue;
                }

                var values = new List<float>();
                foreach (var time in times)
                {
                    var rotation = BuildAvatarAxisLocalRotation(curves, target, axis, time, humanBoneIndex, solverNodes, solverAxes);
                    var gltf = ConvertUnityRotationToGltf(new[] { rotation.X, rotation.Y, rotation.Z, rotation.W });
                    values.AddRange(gltf);
                }

                result.Add(new CurveData(targetPath, "rotation", 4, times, values.ToArray()));
                item["status"] = "solved_experimental";
                item["keyframes"] = times.Length;
                if (IsKnownHumanoidLimbFormulaRisk(target, curves, out var riskReason))
                {
                    item["knownRisk"] = "limb_delta_formula_unverified";
                    item["knownRiskReason"] = riskReason;
                    knownRiskTargets.Add(new JObject
                    {
                        ["humanBone"] = target.HumanBone,
                        ["reason"] = riskReason,
                    });
                }
            }

            diagnostic["solvedTrackCount"] = result.Count;
            diagnostic["knownRiskTargetCount"] = knownRiskTargets.Count;
            diagnostic["knownRiskMeaning"] = "这些骨骼命中了已知未完全复现 Unity 的 Humanoid limb delta 公式。glTF channel 已生成，但不能把它当作视觉验收通过；需要继续用 Unity oracle 反推 Forearm/LowerLeg 的成对 muscle delta 空间。";
            diagnostic["status"] = result.Count > 0
                ? (knownRiskTargets.Count > 0 ? "experimental_solved_known_limb_formula_risk" : "experimental_solved")
                : "no_tracks_solved";
            diagnostic["summary"] = BuildHumanoidDiagnosticSummary(diagnostics);
            return result;
        }

        private static bool IsKnownHumanoidLimbFormulaRisk(
            HumanoidTarget target,
            Dictionary<string, List<FloatKey>> curves,
            out string reason)
        {
            reason = null;
            if (target == null || curves == null || curves.Count == 0)
            {
                return false;
            }

            var hasTargetCurve = HasFloatCurve(curves, target.XAttribute)
                || HasFloatCurve(curves, target.ZAttribute)
                || HasFloatCurve(curves, target.ExtraZAttribute);
            if (!hasTargetCurve)
            {
                return false;
            }

            if (string.Equals(target.HumanBone, "LeftLowerArm", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(target.HumanBone, "RightLowerArm", StringComparison.OrdinalIgnoreCase))
            {
                reason = "Forearm Stretch 与 Arm Twist 需要成对 delta 空间公式。NPC/Gorou oracle 显示当前单骨 swing-twist 公式会造成前臂大误差，不能按 solved_experimental 当作视觉通过。";
                return true;
            }

            if (string.Equals(target.HumanBone, "LeftLowerLeg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(target.HumanBone, "RightLowerLeg", StringComparison.OrdinalIgnoreCase))
            {
                reason = "LowerLeg Stretch / UpperLeg Twist 的远端 delta 公式仍未跨模型稳定。当前 glTF channel 只说明内部求解器生成了轨道，不说明腿部姿态已通过 Unity oracle 对齐。";
                return true;
            }

            return false;
        }

        private static string[] SelectMatchingHumanoidMuscleAttributes(Dictionary<string, List<FloatKey>> curves)
        {
            if (curves == null || curves.Count == 0)
            {
                return Array.Empty<string>();
            }

            var expected = HumanoidTarget.Targets
                .SelectMany(x => new[] { x.XAttribute, x.YAttribute, x.ZAttribute, x.ExtraZAttribute })
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return curves.Keys
                .Where(expected.Contains)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static JObject BuildHumanoidDiagnosticSummary(JArray diagnostics)
        {
            var items = diagnostics?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var byStatus = items
                .GroupBy(x => (string)x["status"] ?? "unknown", StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);

            var solved = SelectHumanBonesByStatus(items, "solved_experimental");
            var missingAxis = SelectHumanBonesByStatus(items, "missing_avatar_axis");
            var missingNodePath = SelectHumanBonesByStatus(items, "missing_avatar_node_path");
            var missingGltfNode = SelectHumanBonesByStatus(items, "missing_gltf_node");
            var noMatchingCurve = SelectHumanBonesByStatus(items, "no_matching_muscle_curve");

            return new JObject
            {
                ["targetCount"] = items.Length,
                ["solvedHumanBoneCount"] = solved.Length,
                ["unsolvedHumanBoneCount"] = items.Length - solved.Length,
                ["statusCounts"] = JObject.FromObject(byStatus),
                ["solvedHumanBones"] = new JArray(solved),
                ["missingAvatarAxisHumanBones"] = new JArray(missingAxis),
                ["missingAvatarNodePathHumanBones"] = new JArray(missingNodePath),
                ["missingGltfNodeHumanBones"] = new JArray(missingGltfNode),
                ["noMatchingCurveHumanBones"] = new JArray(noMatchingCurve),
                ["diagnosticMeaning"] = "no_matching_muscle_curve 表示该动画没有驱动这个人体骨；missing_avatar_axis / missing_gltf_node 表示模型 Avatar 或 glTF 节点映射缺口；solved_experimental 才进入当前内部 Humanoid TRS 预览。",
            };
        }

        private static string[] SelectHumanBonesByStatus(IEnumerable<JObject> items, string status)
        {
            return items
                .Where(x => string.Equals((string)x["status"], status, StringComparison.OrdinalIgnoreCase))
                .Select(x => (string)x["humanBone"])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static List<CurveData> LoadHumanoidRootMotionCurves(JObject decoded, JObject gltf, JArray nodes, out JObject diagnostic)
        {
            var result = new List<CurveData>();
            diagnostic = new JObject
            {
                ["status"] = "not_present",
                ["mode"] = "RootT_RootQ_ToSceneRoot",
            };

            var curves = LoadFloatCurves(decoded?["floats"] as JArray);
            if (curves.Count == 0)
            {
                diagnostic["status"] = "no_float_curves";
                return result;
            }

            if (!TryGetPrimarySceneRootPath(gltf, nodes, out var rootPath, out var rootNodeIndex))
            {
                diagnostic["status"] = "missing_scene_root";
                return result;
            }

            diagnostic["targetPath"] = rootPath;
            diagnostic["targetNode"] = rootNodeIndex;

            var hasRootT = curves.ContainsKey("RootT.x") || curves.ContainsKey("RootT.y") || curves.ContainsKey("RootT.z");
            if (hasRootT)
            {
                var times = GetCurveTimes(curves, "RootT.x", "RootT.y", "RootT.z");
                if (times.Length > 0)
                {
                    var first = SampleRootT(curves, times[0]);
                    var values = new List<float>();
                    foreach (var time in times)
                    {
                        var current = SampleRootT(curves, time);
                        var delta = new[] { current[0] - first[0], current[1] - first[1], current[2] - first[2] };
                        values.AddRange(ConvertUnityPositionToGltf(delta));
                    }

                    result.Add(new CurveData(rootPath, "translation", 3, times, values.ToArray()));
                    diagnostic["rootTKeyframes"] = times.Length;
                }
            }

            var hasRootQ = curves.ContainsKey("RootQ.w")
                || curves.ContainsKey("RootQ.x")
                || curves.ContainsKey("RootQ.y")
                || curves.ContainsKey("RootQ.z");
            if (hasRootQ)
            {
                var times = GetCurveTimes(curves, "RootQ.x", "RootQ.y", "RootQ.z", "RootQ.w");
                if (times.Length > 0)
                {
                    var rest = ConvertGltfRotationToUnity(ReadNodeRotation(nodes[rootNodeIndex] as JObject));
                    var first = SampleRootQ(curves, times[0]);
                    var firstInv = Inverse(first);
                    var values = new List<float>();
                    foreach (var time in times)
                    {
                        var current = SampleRootQ(curves, time);
                        var delta = NormalizeQuaternion(Multiply(current, firstInv));
                        var rotation = NormalizeQuaternion(Multiply(delta, rest));
                        values.AddRange(ConvertUnityRotationToGltf(new[] { rotation.X, rotation.Y, rotation.Z, rotation.W }));
                    }

                    result.Add(new CurveData(rootPath, "rotation", 4, times, values.ToArray()));
                    diagnostic["rootQKeyframes"] = times.Length;
                }
            }

            diagnostic["status"] = result.Count > 0 ? "ok" : "no_root_motion_tracks";
            diagnostic["trackCount"] = result.Count;
            diagnostic["note"] = "RootT/RootQ 来自 Unity MuscleClip decoded floats，写到 glTF scene root；第一帧归零，避免把源场景世界偏移写进素材。";
            return result;
        }

        private static bool TryGetPrimarySceneRootPath(JObject gltf, JArray nodes, out string path, out int nodeIndex)
        {
            path = null;
            nodeIndex = -1;
            var sceneIndex = (int?)gltf?["scene"] ?? 0;
            var roots = gltf?["scenes"]?[sceneIndex]?["nodes"]?.Values<int>().ToArray();
            if (roots == null || roots.Length == 0)
            {
                roots = nodes == null ? Array.Empty<int>() : Enumerable.Range(0, nodes.Count).ToArray();
            }

            nodeIndex = roots.FirstOrDefault(x => x >= 0 && nodes != null && x < nodes.Count);
            if (nodes == null || nodeIndex < 0 || nodeIndex >= nodes.Count || nodes[nodeIndex] is not JObject node)
            {
                return false;
            }

            path = (string)node["name"] ?? $"node_{nodeIndex}";
            return !string.IsNullOrWhiteSpace(path);
        }

        private static float[] GetCurveTimes(Dictionary<string, List<FloatKey>> curves, params string[] attributes)
        {
            return attributes
                .Where(x => !string.IsNullOrWhiteSpace(x) && curves.ContainsKey(x))
                .SelectMany(x => curves[x].Select(k => k.Time))
                .Distinct()
                .OrderBy(x => x)
                .ToArray();
        }

        private static float[] SampleRootT(Dictionary<string, List<FloatKey>> curves, float time)
        {
            return new[]
            {
                SampleCurve(curves, "RootT.x", time),
                SampleCurve(curves, "RootT.y", time),
                SampleCurve(curves, "RootT.z", time),
            };
        }

        private static QuaternionData SampleRootQ(Dictionary<string, List<FloatKey>> curves, float time)
        {
            var qx = SampleCurve(curves, "RootQ.x", time);
            var qy = SampleCurve(curves, "RootQ.y", time);
            var qz = SampleCurve(curves, "RootQ.z", time);
            var qw = curves.ContainsKey("RootQ.w") ? SampleCurve(curves, "RootQ.w", time) : 1f;
            return NormalizeQuaternion(new QuaternionData(qx, qy, qz, qw));
        }

        private static string GetSolverNodePath(JArray nodes, int skeletonNodeIndex)
        {
            if (nodes == null || skeletonNodeIndex < 0 || skeletonNodeIndex >= nodes.Count || nodes[skeletonNodeIndex] is not JObject node)
            {
                return null;
            }

            var path = (string)node["path"];
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            var name = (string)node["name"];
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        private static bool HasFloatCurve(Dictionary<string, List<FloatKey>> curves, string attribute)
        {
            return !string.IsNullOrWhiteSpace(attribute) && curves.ContainsKey(attribute);
        }

        private static string BuildHumanoidDiagnosticHint(JObject diagnostic)
        {
            if (diagnostic == null)
            {
                return string.Empty;
            }
            return $" status={(string)diagnostic["status"]}; solvedTrackCount={(int?)diagnostic["solvedTrackCount"] ?? 0}; sampleTimeCount={(int?)diagnostic["sampleTimeCount"] ?? 0}";
        }

        private static bool HasRequiredHumanoidSolverPoseMetadata(JObject solver, out string message)
        {
            message = null;
            var skeleton = solver?["skeleton"] as JObject;
            var nodes = skeleton?["nodes"] as JArray;
            var humanSkeletonPose = skeleton?["humanSkeletonPose"] as JArray;
            if (nodes == null || nodes.Count == 0)
            {
                message = "Humanoid/Muscle 预览缺少 Avatar internalSolver.skeleton.nodes。请刷新模型 Avatar 元数据。";
                return false;
            }

            // HumanPose -> 骨骼 TRS 不是单靠 preQ/postQ 就能稳定还原；Unity Avatar 的参考姿态
            // 是求解零姿态和后续校正的必要输入。旧索引缺这个字段时必须重导/回写，不能继续生成会扭曲的 glTF。
            if (humanSkeletonPose == null || humanSkeletonPose.Count < nodes.Count)
            {
                message = "Humanoid/Muscle 预览缺少完整 Avatar internalSolver.skeleton.humanSkeletonPose。请用当前导出工具刷新模型 Avatar 元数据后再生成预览。";
                return false;
            }

            var humanBoneIndex = solver?["humanBoneIndex"] as JArray;
            if (humanBoneIndex == null || humanBoneIndex.Count == 0)
            {
                message = "Humanoid/Muscle 预览缺少 Avatar internalSolver.humanBoneIndex。请刷新模型 Avatar 元数据。";
                return false;
            }

            return true;
        }

        private static Dictionary<string, string> ParseHumanBoneMap(JObject avatar)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in avatar?["humanBones"]?.Values<string>() ?? Enumerable.Empty<string>())
            {
                var separator = value.IndexOf(':');
                if (separator <= 0 || separator >= value.Length - 1)
                {
                    continue;
                }
                map[value[..separator]] = value[(separator + 1)..];
            }
            return map;
        }

        private static Dictionary<string, List<FloatKey>> LoadFloatCurves(JArray floats)
        {
            var curves = new Dictionary<string, List<FloatKey>>(StringComparer.OrdinalIgnoreCase);
            foreach (var curve in floats?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var attribute = (string)curve["attribute"];
                if (string.IsNullOrWhiteSpace(attribute))
                {
                    continue;
                }
                var keys = curve["keyframes"]?
                    .OfType<JObject>()
                    .Select(x => new FloatKey(F(x["time"]), F(x["value"])))
                    .OrderBy(x => x.Time)
                    .ToList() ?? new List<FloatKey>();
                if (keys.Count > 0)
                {
                    curves[attribute] = keys;
                }
            }
            return curves;
        }

        private static bool TryGetAvatarAxis(string humanBone, JArray humanBoneIndex, JArray nodes, JArray axes, out AvatarAxis axis)
        {
            axis = default;
            if (!Enum.TryParse<BoneType>(humanBone, out var boneType))
            {
                return false;
            }
            var humanIndex = (int)boneType;
            if (humanIndex < 0 || humanIndex >= humanBoneIndex.Count)
            {
                return false;
            }
            var skeletonNodeIndex = (int?)humanBoneIndex[humanIndex] ?? -1;
            if (skeletonNodeIndex < 0 || skeletonNodeIndex >= nodes.Count)
            {
                return false;
            }
            var node = nodes[skeletonNodeIndex] as JObject;
            var axesId = (int?)node?["axesId"] ?? -1;
            if (axesId < 0 || axesId >= axes.Count || axes[axesId] is not JObject axisJson)
            {
                return false;
            }

            axis = new AvatarAxis(
                skeletonNodeIndex,
                axesId,
                ReadFloatArray(axisJson["preQ"] as JArray, 4),
                ReadFloatArray(axisJson["postQ"] as JArray, 4),
                ReadFloatArray(axisJson["sign"] as JArray, 3),
                ReadFloatArray(axisJson["limitMin"] as JArray, 3),
                ReadFloatArray(axisJson["limitMax"] as JArray, 3)
            );
            return axis.PreQ != null && axis.PostQ != null && axis.Sign != null && axis.LimitMin != null && axis.LimitMax != null;
        }

        private static QuaternionData BuildAvatarAxisLocalRotation(
            Dictionary<string, List<FloatKey>> curves,
            HumanoidTarget target,
            AvatarAxis axis,
            float time,
            JArray humanBoneIndex,
            JArray solverNodes,
            JArray solverAxes)
        {
            var angles = new float[3];
            // Unity Avatar 轴系里 X 更接近 twist，Y/Z 更接近 swing。
            // AnimationCurve 的常见顺序是 FrontBack/InOut/Twist，所以这里按 Z/Y/X 放回 Avatar 轴，
            // 避免把屈伸当成 twist 写进小腿、前臂这类长骨。
            AddMuscleAngle(angles, curves, target.XAttribute, time, target.HumanBone, axis, 2, humanBoneIndex, solverNodes, solverAxes);
            AddMuscleAngle(angles, curves, target.YAttribute, time, target.HumanBone, axis, 1, humanBoneIndex, solverNodes, solverAxes);
            AddMuscleAngle(angles, curves, target.ZAttribute, time, target.HumanBone, axis, 0, humanBoneIndex, solverNodes, solverAxes);
            AddMuscleAngle(angles, curves, target.ExtraZAttribute, time, target.HumanBone, axis, 0, humanBoneIndex, solverNodes, solverAxes);

            var pre = ToQuaternion(axis.PreQ);
            var post = ToQuaternion(axis.PostQ);
            // Unity Humanoid muscle 不是普通 XYZ Euler。X 是 twist，Y/Z 合成 swing；
            // 最终写回骨骼本地旋转时使用 Avatar 轴系的 preQ 和 inverse(postQ)。
            var swing = SwingRadiansToQuaternion(angles[1], angles[2]);
            var twist = AxisAngleRadiansToQuaternion(1, 0, 0, angles[0]);
            return NormalizeQuaternion(Multiply(Multiply(Multiply(pre, swing), twist), Inverse(post)));
        }

        private static void AddMuscleAngle(
            float[] angles,
            Dictionary<string, List<FloatKey>> curves,
            string attribute,
            float time,
            string targetHumanBone,
            AvatarAxis targetAxis,
            int defaultAvatarAxis,
            JArray humanBoneIndex,
            JArray solverNodes,
            JArray solverAxes)
        {
            if (string.IsNullOrWhiteSpace(attribute))
            {
                return;
            }

            var value = SampleCurve(curves, attribute, time);
            if (MathF.Abs(value) <= 0.0000001f)
            {
                return;
            }

            var targetAvatarAxis = ResolveTargetAvatarAxis(attribute, defaultAvatarAxis);
            var limitHumanBone = ResolveLimitHumanBone(attribute, targetHumanBone);
            var limitAvatarAxis = ResolveLimitAvatarAxis(attribute, targetAvatarAxis);
            var limitAxis = targetAxis;
            if (!string.Equals(limitHumanBone, targetHumanBone, StringComparison.OrdinalIgnoreCase) &&
                !TryGetAvatarAxis(limitHumanBone, humanBoneIndex, solverNodes, solverAxes, out limitAxis))
            {
                limitAxis = targetAxis;
            }

            angles[targetAvatarAxis] += LimitMuscle(value, limitAxis.LimitMin, limitAxis.LimitMax, limitAxis.Sign, limitAvatarAxis);
        }

        private static int ResolveTargetAvatarAxis(string attribute, int defaultAvatarAxis)
        {
            // Unity probe 显示 Foot Twist 写在 Foot 的 Y/swing 轴；Foot 自身 X 限位常为 0。
            if (attribute.IndexOf("Foot Twist", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 1;
            }
            return defaultAvatarAxis;
        }

        private static int ResolveLimitAvatarAxis(string attribute, int targetAvatarAxis)
        {
            if (attribute.IndexOf("Lower Leg Twist", StringComparison.OrdinalIgnoreCase) >= 0 ||
                attribute.IndexOf("Forearm Twist", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 0;
            }
            return targetAvatarAxis;
        }

        private static string ResolveLimitHumanBone(string attribute, string targetHumanBone)
        {
            if (attribute.IndexOf("Upper Leg Twist", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // UpperLeg Twist 的曲线写到 LowerLeg transform，但角度幅度来自 UpperLeg 的 Avatar X/twist limit。
                return attribute.StartsWith("Left ", StringComparison.OrdinalIgnoreCase) ? "LeftUpperLeg" : "RightUpperLeg";
            }
            if (attribute.IndexOf("Lower Leg Twist", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return attribute.StartsWith("Left ", StringComparison.OrdinalIgnoreCase) ? "LeftLowerLeg" : "RightLowerLeg";
            }
            if (attribute.IndexOf("Forearm Twist", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return attribute.StartsWith("Left ", StringComparison.OrdinalIgnoreCase) ? "LeftLowerArm" : "RightLowerArm";
            }
            return targetHumanBone;
        }

        private static float SampleCurve(Dictionary<string, List<FloatKey>> curves, string attribute, float time)
        {
            if (string.IsNullOrWhiteSpace(attribute) || !curves.TryGetValue(attribute, out var keys) || keys.Count == 0)
            {
                return 0;
            }
            if (time <= keys[0].Time)
            {
                return keys[0].Value;
            }
            if (time >= keys[^1].Time)
            {
                return keys[^1].Value;
            }
            for (var i = 1; i < keys.Count; i++)
            {
                if (time > keys[i].Time)
                {
                    continue;
                }
                var a = keys[i - 1];
                var b = keys[i];
                var span = b.Time - a.Time;
                var t = span <= 0 ? 0 : (time - a.Time) / span;
                return a.Value + (b.Value - a.Value) * t;
            }
            return keys[^1].Value;
        }

        private static float LimitMuscle(float value, float[] min, float[] max, float[] sign, int axis)
        {
            if (axis < 0 || min == null || max == null || sign == null || axis >= min.Length || axis >= max.Length || axis >= sign.Length)
            {
                return 0;
            }
            var range = value >= 0 ? max[axis] : -min[axis];
            return value * range * sign[axis];
        }

        private static void AddVector3Curves(List<CurveData> result, JArray curves, string targetPath, Func<float[], float[]> convert = null)
        {
            foreach (var curve in curves?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var path = (string)curve["path"];
                var keys = ReadKeys(curve["keyframes"] as JArray, 3, convert);
                if (!string.IsNullOrWhiteSpace(path) && keys.Times.Length > 0)
                {
                    result.Add(new CurveData(path, targetPath, 3, keys.Times, keys.Values));
                }
            }
        }

        private static void AddQuaternionCurves(List<CurveData> result, JArray curves)
        {
            foreach (var curve in curves?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var path = (string)curve["path"];
                var keys = ReadKeys(curve["keyframes"] as JArray, 4, ConvertUnityRotationToGltf);
                if (!string.IsNullOrWhiteSpace(path) && keys.Times.Length > 0)
                {
                    result.Add(new CurveData(path, "rotation", 4, keys.Times, keys.Values));
                }
            }
        }

        private static (float[] Times, float[] Values) ReadKeys(JArray keyframes, int componentCount, Func<float[], float[]> convert = null)
        {
            var times = new List<float>();
            var values = new List<float>();
            foreach (var key in keyframes?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var value = key["value"] as JArray;
                if (value == null || value.Count < componentCount)
                {
                    continue;
                }

                times.Add(F(key["time"]));
                var converted = new float[componentCount];
                for (var i = 0; i < componentCount; i++)
                {
                    converted[i] = F(value[i]);
                }

                // animation_asset.json 保存的是 Unity 本地 TRS；glTF 预览需要和模型导出时相同的坐标基转换。
                if (convert != null)
                {
                    converted = convert(converted);
                }

                for (var i = 0; i < componentCount; i++)
                {
                    values.Add(converted[i]);
                }
            }
            return (times.ToArray(), values.ToArray());
        }

        private static float[] ConvertUnityPositionToGltf(float[] value) => new[] { -value[0], value[1], value[2] };

        private static float[] ConvertUnityRotationToGltf(float[] value) => new[] { value[0], -value[1], -value[2], value[3] };

        private static QuaternionData ConvertGltfRotationToUnity(QuaternionData value)
        {
            // glTF 节点里的 rest rotation 已经做过 Unity->glTF 轴转换。
            // Humanoid/Muscle delta 和 Avatar pre/post 仍在 Unity 空间，先转回来再相乘。
            return NormalizeQuaternion(new QuaternionData(value.X, -value.Y, -value.Z, value.W));
        }

        private static bool IsBlendShapeFloatCurve(JObject curve)
        {
            var category = (string)curve?["category"];
            var attribute = (string)curve?["attribute"];
            return string.Equals(category, "BlendShape", StringComparison.OrdinalIgnoreCase)
                || (attribute ?? string.Empty).StartsWith("blendShape.", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeBlendShapeName(string attribute)
        {
            if (string.IsNullOrWhiteSpace(attribute))
            {
                return null;
            }
            const string Prefix = "blendShape.";
            return attribute.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                ? attribute[Prefix.Length..]
                : attribute;
        }

        private static bool TryGetMorphTargetMap(JObject gltf, JArray nodes, int nodeIndex, out Dictionary<string, int> targetMap, out int targetCount)
        {
            targetMap = null;
            targetCount = 0;
            if (nodes == null || nodeIndex < 0 || nodeIndex >= nodes.Count || nodes[nodeIndex] is not JObject node)
            {
                return false;
            }

            var meshIndex = (int?)node["mesh"];
            var meshes = gltf?["meshes"] as JArray;
            if (meshIndex == null || meshes == null || meshIndex.Value < 0 || meshIndex.Value >= meshes.Count || meshes[meshIndex.Value] is not JObject mesh)
            {
                return false;
            }

            var names = mesh["extras"]?["targetNames"]?.Values<string>()?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray() ?? Array.Empty<string>();
            var primitiveTargetCount = mesh["primitives"]?
                .OfType<JObject>()
                .Select(x => (x["targets"] as JArray)?.Count ?? 0)
                .DefaultIfEmpty(0)
                .Max() ?? 0;
            targetCount = Math.Max(names.Length, primitiveTargetCount);
            if (targetCount <= 0 || names.Length == 0)
            {
                return false;
            }

            targetMap = names
                .Select((name, index) => new { name, index })
                .GroupBy(x => x.name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First().index, StringComparer.OrdinalIgnoreCase);
            return targetMap.Count > 0;
        }

        private static List<FloatKey> ReadFloatKeys(JArray keyframes)
        {
            return keyframes?
                .OfType<JObject>()
                .Select(x => new FloatKey(F(x["time"]), F(x["value"])))
                .OrderBy(x => x.Time)
                .ToList() ?? new List<FloatKey>();
        }

        private static float SampleFloatKeys(List<FloatKey> keys, float time)
        {
            if (keys == null || keys.Count == 0)
            {
                return 0.0f;
            }
            if (time <= keys[0].Time)
            {
                return keys[0].Value;
            }
            if (time >= keys[^1].Time)
            {
                return keys[^1].Value;
            }
            for (var i = 1; i < keys.Count; i++)
            {
                if (time > keys[i].Time)
                {
                    continue;
                }
                var a = keys[i - 1];
                var b = keys[i];
                var span = b.Time - a.Time;
                var t = span <= 0 ? 0 : (time - a.Time) / span;
                return a.Value + (b.Value - a.Value) * t;
            }
            return keys[^1].Value;
        }

        private static Dictionary<string, int> BuildNodePathMap(JObject gltf, JArray nodes)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var sceneNodes = gltf["scenes"]?[(int?)gltf["scene"] ?? 0]?["nodes"]?.Values<int>().ToArray()
                ?? Enumerable.Range(0, nodes.Count).ToArray();
            foreach (var root in sceneNodes)
            {
                AddNodePath(nodes, root, "", map);
            }
            return map;
        }

        private static void AddNodePath(JArray nodes, int index, string parentPath, Dictionary<string, int> map)
        {
            if (index < 0 || index >= nodes.Count || nodes[index] is not JObject node)
            {
                return;
            }

            var name = (string)node["name"] ?? $"node_{index}";
            var path = string.IsNullOrWhiteSpace(parentPath) ? name : parentPath + "/" + name;
            map.TryAdd(path, index);
            map.TryAdd(name, index);
            foreach (var child in node["children"]?.Values<int>() ?? Enumerable.Empty<int>())
            {
                AddNodePath(nodes, child, path, map);
            }
        }

        private static bool TryFindNode(Dictionary<string, int> nodeMap, string animationPath, out int nodeIndex)
        {
            if (nodeMap.TryGetValue(animationPath, out nodeIndex))
            {
                return true;
            }

            var match = nodeMap
                .Where(x => x.Key.EndsWith("/" + animationPath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Key.Length)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(match.Key))
            {
                nodeIndex = match.Value;
                return true;
            }

            var leaf = animationPath.Split('/').LastOrDefault();
            return !string.IsNullOrWhiteSpace(leaf) && nodeMap.TryGetValue(leaf, out nodeIndex);
        }

        private static void CopyDirectory(string source, string destination)
        {
            var sourceRoot = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var destinationRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories).ToArray())
            {
                if (IsSameOrChildPath(directory, destinationRoot))
                {
                    continue;
                }
                Directory.CreateDirectory(Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, directory)));
            }
            foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories).ToArray())
            {
                if (IsSameOrChildPath(file, destinationRoot))
                {
                    continue;
                }
                var target = Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target) ?? destinationRoot);
                File.Copy(file, target, overwrite: true);
            }
        }

        private static bool IsSameOrChildPath(string path, string parent)
        {
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(fullPath, fullParent, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(fullParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(fullParent + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static float F(JToken token)
        {
            return token == null ? 0 : token.Value<float>();
        }

        private static float[] ReadFloatArray(JArray array, int minCount)
        {
            return array == null || array.Count < minCount
                ? null
                : array.Take(minCount).Select(F).ToArray();
        }

        private static QuaternionData ReadNodeRotation(JObject node)
        {
            var values = ReadFloatArray(node?["rotation"] as JArray, 4);
            return values == null
                ? new QuaternionData(0, 0, 0, 1)
                : new QuaternionData(values[0], values[1], values[2], values[3]);
        }

        private static QuaternionData ToQuaternion(float[] value)
        {
            return value == null || value.Length < 4
                ? new QuaternionData(0, 0, 0, 1)
                : NormalizeQuaternion(new QuaternionData(value[0], value[1], value[2], value[3]));
        }

        private static QuaternionData SwingRadiansToQuaternion(float y, float z)
        {
            var angle = Math.Sqrt(y * y + z * z);
            if (angle <= 0.000001)
            {
                return new QuaternionData(0, 0, 0, 1);
            }
            return AxisAngleRadiansToQuaternion(0, (float)(y / angle), (float)(z / angle), (float)angle);
        }

        private static QuaternionData AxisAngleRadiansToQuaternion(float x, float y, float z, float angle)
        {
            var half = angle / 2.0;
            var sin = Math.Sin(half);
            return NormalizeQuaternion(new QuaternionData(
                (float)(x * sin),
                (float)(y * sin),
                (float)(z * sin),
                (float)Math.Cos(half)
            ));
        }

        private static QuaternionData Multiply(QuaternionData a, QuaternionData b)
        {
            return new QuaternionData(
                a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
                a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
                a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z
            );
        }

        private static QuaternionData Inverse(QuaternionData q)
        {
            var lengthSq = q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W;
            if (lengthSq <= 0.000001f)
            {
                return new QuaternionData(0, 0, 0, 1);
            }
            var inv = 1.0f / lengthSq;
            return new QuaternionData(-q.X * inv, -q.Y * inv, -q.Z * inv, q.W * inv);
        }

        private static QuaternionData NormalizeQuaternion(QuaternionData q)
        {
            var length = Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
            if (length <= 0.000001)
            {
                return new QuaternionData(0, 0, 0, 1);
            }
            var inv = 1.0 / length;
            return new QuaternionData((float)(q.X * inv), (float)(q.Y * inv), (float)(q.Z * inv), (float)(q.W * inv));
        }

        private static string SafeName(string value)
        {
            var name = string.IsNullOrWhiteSpace(value) ? "preview" : value;
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        private sealed record CurveData(string Path, string TargetPath, int ComponentCount, float[] Times, float[] Values);

        private sealed record BlendShapeCurveData(int NodeIndex, string ChannelName, int TargetIndex, int TargetCount, List<FloatKey> Keys);

        private sealed record FloatKey(float Time, float Value);

        private readonly record struct QuaternionData(float X, float Y, float Z, float W);

        private readonly record struct AvatarAxis(
            int SkeletonNodeIndex,
            int AxesId,
            float[] PreQ,
            float[] PostQ,
            float[] Sign,
            float[] LimitMin,
            float[] LimitMax);

        private sealed record HumanoidTarget(
            string HumanBone,
            string XAttribute,
            string YAttribute,
            string ZAttribute,
            string ExtraZAttribute = null)
        {
            public static readonly HumanoidTarget[] Targets =
            {
                new("Spine", "Spine Front-Back", "Spine Left-Right", "Spine Twist Left-Right"),
                new("Chest", "Chest Front-Back", "Chest Left-Right", "Chest Twist Left-Right"),
                new("UpperChest", "UpperChest Front-Back", "UpperChest Left-Right", "UpperChest Twist Left-Right"),
                new("Neck", "Neck Nod Down-Up", "Neck Tilt Left-Right", "Neck Turn Left-Right"),
                new("Head", "Head Nod Down-Up", "Head Tilt Left-Right", "Head Turn Left-Right"),
                new("LeftUpperLeg", "Left Upper Leg Front-Back", "Left Upper Leg In-Out", null),
                new("RightUpperLeg", "Right Upper Leg Front-Back", "Right Upper Leg In-Out", null),
                // Unity Humanoid 会把长骨 twist 写到下一节本地旋转：上腿->小腿，小腿->脚。
                new("LeftLowerLeg", "Left Lower Leg Stretch", null, "Left Upper Leg Twist In-Out"),
                new("RightLowerLeg", "Right Lower Leg Stretch", null, "Right Upper Leg Twist In-Out"),
                new("LeftFoot", "Left Foot Up-Down", null, "Left Foot Twist In-Out", "Left Lower Leg Twist In-Out"),
                new("RightFoot", "Right Foot Up-Down", null, "Right Foot Twist In-Out", "Right Lower Leg Twist In-Out"),
                new("LeftToes", "Left Toes Up-Down", null, null),
                new("RightToes", "Right Toes Up-Down", null, null),
                new("LeftShoulder", "Left Shoulder Down-Up", "Left Shoulder Front-Back", null),
                new("RightShoulder", "Right Shoulder Down-Up", "Right Shoulder Front-Back", null),
                new("LeftUpperArm", "Left Arm Down-Up", "Left Arm Front-Back", null),
                new("RightUpperArm", "Right Arm Down-Up", "Right Arm Front-Back", null),
                // 手臂 twist 同样写到远端：上臂->前臂，前臂->手。
                new("LeftLowerArm", "Left Forearm Stretch", null, "Left Arm Twist In-Out"),
                new("RightLowerArm", "Right Forearm Stretch", null, "Right Arm Twist In-Out"),
                new("LeftHand", "Left Hand Down-Up", "Left Hand In-Out", "Left Forearm Twist In-Out"),
                new("RightHand", "Right Hand Down-Up", "Right Hand In-Out", "Right Forearm Twist In-Out"),
                new("Jaw", "Jaw Close", "Jaw Left-Right", null),
            };
        }

        private sealed class AnimationBinaryWriter
        {
            private readonly List<byte> _bytes = new();

            public int AddAccessor(JObject gltf, float[] values, string type)
            {
                Align();
                var offset = _bytes.Count;
                foreach (var value in values)
                {
                    _bytes.AddRange(BitConverter.GetBytes(value));
                }

                var bufferViewIndex = AddBufferView(gltf, offset, values.Length * sizeof(float));
                var accessor = new JObject
                {
                    ["bufferView"] = bufferViewIndex,
                    ["componentType"] = 5126,
                    ["count"] = type == "SCALAR" ? values.Length : values.Length / (type == "VEC4" ? 4 : 3),
                    ["type"] = type,
                };
                if (type == "SCALAR" && values.Length > 0)
                {
                    accessor["min"] = new JArray(values.Min());
                    accessor["max"] = new JArray(values.Max());
                }

                var accessors = EnsureArray(gltf, "accessors");
                accessors.Add(accessor);
                return accessors.Count - 1;
            }

            public void Flush(JObject gltf, string directory, string fileName)
            {
                Align();
                File.WriteAllBytes(Path.Combine(directory, fileName), _bytes.ToArray());
                var buffers = EnsureArray(gltf, "buffers");
                buffers.Add(new JObject
                {
                    ["uri"] = fileName,
                    ["byteLength"] = _bytes.Count,
                });
                gltf["extras"] ??= new JObject();
            }

            private int AddBufferView(JObject gltf, int offset, int length)
            {
                var bufferViews = EnsureArray(gltf, "bufferViews");
                var bufferIndex = EnsureArray(gltf, "buffers").Count;
                bufferViews.Add(new JObject
                {
                    ["buffer"] = bufferIndex,
                    ["byteOffset"] = offset,
                    ["byteLength"] = length,
                });
                return bufferViews.Count - 1;
            }

            private void Align()
            {
                while (_bytes.Count % 4 != 0)
                {
                    _bytes.Add(0);
                }
            }

            private static JArray EnsureArray(JObject obj, string name)
            {
                if (obj[name] is not JArray array)
                {
                    array = new JArray();
                    obj[name] = array;
                }
                return array;
            }
        }
    }
}
