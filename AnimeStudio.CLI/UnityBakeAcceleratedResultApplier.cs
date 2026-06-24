using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AnimeStudio.CLI
{
    internal static class UnityBakeAcceleratedResultApplier
    {
        public static string Apply(string requestOrResultPath, string outputGltfPath)
        {
            if (string.IsNullOrWhiteSpace(requestOrResultPath) || !File.Exists(requestOrResultPath))
            {
                Logger.Error($"UnityBakeAccelerated request/result not found: {requestOrResultPath}");
                return null;
            }

            var input = JObject.Parse(File.ReadAllText(requestOrResultPath));
            var requestPath = IsAcceleratedRequest(input)
                ? requestOrResultPath
                : TryFindSiblingRequest(requestOrResultPath);
            if (string.IsNullOrWhiteSpace(requestPath) || !File.Exists(requestPath))
            {
                Logger.Error("Unable to find unity_bake_accelerated_request.json next to the result.");
                return null;
            }

            var request = JObject.Parse(File.ReadAllText(requestPath));
            var resultPath = IsAcceleratedRequest(input)
                ? (string)request["outputJson"]
                : requestOrResultPath;
            if (string.IsNullOrWhiteSpace(resultPath) || !File.Exists(resultPath))
            {
                Logger.Error($"UnityBakeAccelerated result not found: {resultPath}");
                return null;
            }

            var result = JObject.Parse(File.ReadAllText(resultPath));
            if (!string.Equals((string)result["status"], "ok", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Error($"UnityBakeAccelerated result is not ok: {(string)result["message"]}");
                return null;
            }

            var sourceGltf = (string)request["animeStudioRequest"]?["model"]?["gltf"];
            if (string.IsNullOrWhiteSpace(sourceGltf) || !File.Exists(sourceGltf))
            {
                Logger.Error($"Source glTF not found in accelerated request: {sourceGltf}");
                return null;
            }

            var clip = result["clips"]?.OfType<JObject>().FirstOrDefault();
            if (clip == null)
            {
                Logger.Error("UnityBakeAccelerated result has no clip.");
                return null;
            }

            var clipName = (string)clip["clipName"] ?? "UnityBakeAccelerated";
            outputGltfPath = ResolveOutputPath(requestPath, outputGltfPath, sourceGltf, clipName);
            if (string.Equals(Path.GetFullPath(sourceGltf), Path.GetFullPath(outputGltfPath), StringComparison.OrdinalIgnoreCase))
            {
                Logger.Error("UnityBakeAccelerated apply requires a separate output glTF path, to avoid modifying the clean model asset in place.");
                return null;
            }

            var modelGate = ModelLibraryValidator.ValidateSingleModelForAnimationGate(sourceGltf);
            if ((bool?)modelGate?["animationGate"]?["ready"] != true)
            {
                var gateOutputDir = Path.GetDirectoryName(Path.GetFullPath(outputGltfPath)) ?? Directory.GetCurrentDirectory();
                var failureMessage = "UnityBakeAccelerated apply blocked by model-first gate: source model glTF is not animation-ready. Fix Mesh/UV/material/texture/skin/bbox first, then generate animation output.";
                WriteModelGateFailureReport(gateOutputDir, sourceGltf, outputGltfPath, requestPath, resultPath, clipName, failureMessage, modelGate);
                Logger.Error(failureMessage);
                return null;
            }

            CopyModelFolder(sourceGltf, outputGltfPath);
            // 每次 apply 都从干净模型重新写动画，避免重复执行命令时把同一个 clip 追加多遍。
            File.Copy(sourceGltf, outputGltfPath, overwrite: true);

            var outputDir = Path.GetDirectoryName(outputGltfPath) ?? Directory.GetCurrentDirectory();
            var gltf = JObject.Parse(File.ReadAllText(outputGltfPath));
            var bufferFile = ResolveMainBufferFile(gltf, outputDir);
            if (string.IsNullOrWhiteSpace(bufferFile) || !File.Exists(bufferFile))
            {
                Logger.Error("Only file-buffer glTF is currently supported for UnityBakeAccelerated apply.");
                return null;
            }

            var bufferBytes = File.ReadAllBytes(bufferFile).ToList();
            var nodes = gltf["nodes"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var modelName = (string)request["animeStudioRequest"]?["model"]?["name"];
            var nodePathToIndex = BuildNodePathIndex(gltf, nodes, modelName);
            var jointPaths = result["jointPaths"]?.Values<string>().ToArray()
                ?? request["jointPaths"]?.Values<string>().ToArray()
                ?? Array.Empty<string>();
            var restValues = result["restValues"]?.Values<float>().ToArray();
            var samples = clip["samples"]?.OfType<JObject>()
                .Where(x => string.Equals((string)x["status"], "ok", StringComparison.OrdinalIgnoreCase))
                .ToArray() ?? Array.Empty<JObject>();
            if (jointPaths.Length == 0 || samples.Length == 0)
            {
                Logger.Error("UnityBakeAccelerated result has no usable joint paths or samples.");
                return null;
            }

            var animation = new JObject
            {
                ["name"] = clipName,
                ["samplers"] = new JArray(),
                ["channels"] = new JArray(),
                ["extras"] = new JObject
                {
                    ["animeStudioUnityBakeAccelerated"] = new JObject
                    {
                        ["sourceRequest"] = requestPath,
                        ["sourceResult"] = resultPath,
                        ["avatar"] = result["avatarName"],
                        ["avatarValid"] = result["avatarValid"],
                        ["avatarHuman"] = result["avatarHuman"],
                        ["sampleCount"] = samples.Length,
                        ["valueLayout"] = result["valueLayout"],
                        ["coordinateConversion"] = "Unity internal avatar local T is written as rest-relative glTF delta; local Q is written as absolute Unity->glTF rotation.",
                        ["translationDeltaBase"] = restValues != null && restValues.Length >= jointPaths.Length * 7 ? "unity_zero_body_zero_muscle_pose" : "first_sample_fallback",
                        ["rotationWriteMode"] = "absolute_internal_avatar_local_rotation",
                    }
                }
            };

            var times = samples.Select(x => (float?)x["time"] ?? 0f).ToArray();
            var timeAccessor = AppendAccessor(gltf, bufferBytes, times, "SCALAR", 5126, new[] { times.Min() }, new[] { times.Max() });
            var writtenTracks = 0;
            var skippedPaths = new JArray();
            var coreChangedTracks = 0;
            var restChangedTracks = 0;
            var frameVaryingTracks = 0;
            var coreRestChangedTracks = 0;
            var coreFrameVaryingTracks = 0;
            var maxFrameTranslationDelta = 0f;
            var maxFrameRotationDeltaDegrees = 0f;
            var maxRestTranslationDelta = 0f;
            var maxRestRotationDeltaDegrees = 0f;
            var requestMuscleStats = MeasureRequestMuscleFrameDelta(request);
            var resultJointStats = MeasureResultJointFrameDelta(samples, jointPaths.Length);
            var unsupportedHumanPoseCurves = request["animeStudioRequest"]?["sampling"]?["unsupportedHumanPoseCurveSummary"] as JObject;
            var hasUnsupportedHumanPoseCurves = ((int?)unsupportedHumanPoseCurves?["curveCount"] ?? 0) > 0;

            for (var jointIndex = 0; jointIndex < jointPaths.Length; jointIndex++)
            {
                var jointPath = NormalizeResultJointPath(jointPaths[jointIndex], request, modelName);
                if (!nodePathToIndex.TryGetValue(jointPath, out var nodeIndex))
                {
                    skippedPaths.Add(jointPaths[jointIndex]);
                    continue;
                }

                var unityRows = samples
                    .Select(x => ReadJointRow(x["values"]?.Values<float>().ToArray(), jointIndex))
                    .Where(x => x != null)
                    .ToArray();
                if (unityRows.Length != samples.Length)
                {
                    continue;
                }

                var unityRest = ReadJointRow(restValues, jointIndex) ?? unityRows[0];
                var node = nodes[nodeIndex];
                var gltfRestT = ReadNodeTranslation(node);
                var gltfRestQ = ReadNodeRotation(node);

                var translations = unityRows
                    .Select(x => ApplyRestRelativeTranslation(x, unityRest, gltfRestT))
                    .ToArray();
                var rotations = MakeQuaternionsContinuous(unityRows
                    .Select(x => ApplyRestRelativeRotation(x, unityRest, gltfRestQ))
                    .ToArray());

                var translationFrameDelta = MaxTranslationDelta(translations);
                var rotationFrameDelta = MaxRotationDelta(rotations);
                var translationRestDelta = MaxTranslationRestDelta(translations, gltfRestT);
                var rotationRestDelta = MaxRotationRestDelta(rotations, gltfRestQ);
                maxFrameTranslationDelta = Math.Max(maxFrameTranslationDelta, translationFrameDelta);
                maxFrameRotationDeltaDegrees = Math.Max(maxFrameRotationDeltaDegrees, rotationFrameDelta);
                maxRestTranslationDelta = Math.Max(maxRestTranslationDelta, translationRestDelta);
                maxRestRotationDeltaDegrees = Math.Max(maxRestRotationDeltaDegrees, rotationRestDelta);

                // 帧间变化用“可感知”阈值，避免把 Unity/float 抖动当作有效动画。
                // 相对 rest 的变化阈值更低，因为短定格姿态可能第一帧已经是目标姿态。
                var translationFrameVarying = translationFrameDelta > 0.0005f;
                var rotationFrameVarying = rotationFrameDelta > 0.01f;
                var translationRestChanged = translationRestDelta > 0.00001f;
                var rotationRestChanged = rotationRestDelta > 0.0001f;
                var pathIsCore = Exporter.IsCoreAnimationPath(jointPath);
                // InternalAvatarPose 的绝对本地旋转会让一些手指、lookat、辅助/twist joint
                // 出现“相对 rest 有偏移但帧间不动”的静态差异。它们不能覆盖模型原始 rest，
                // 否则会把非主体骨骼写成错误姿态。主体骨骼仍允许 pose-only/首帧姿态类变化。
                var translationVarying = translationFrameVarying || (pathIsCore && translationRestChanged);
                var rotationVarying = rotationFrameVarying || (pathIsCore && rotationRestChanged);
                if (!translationVarying && !rotationVarying)
                {
                    continue;
                }

                if (translationRestChanged || rotationRestChanged)
                {
                    restChangedTracks++;
                    if (pathIsCore)
                    {
                        coreRestChangedTracks++;
                    }
                }
                if (translationFrameVarying || rotationFrameVarying)
                {
                    frameVaryingTracks++;
                    if (pathIsCore)
                    {
                        coreFrameVaryingTracks++;
                    }
                }

                if (translationVarying)
                {
                    AddChannel(gltf, animation, bufferBytes, timeAccessor, nodeIndex, "translation", translations, "VEC3");
                }
                if (rotationVarying)
                {
                    AddChannel(gltf, animation, bufferBytes, timeAccessor, nodeIndex, "rotation", rotations, "VEC4");
                }

                writtenTracks++;
                if (pathIsCore)
                {
                    coreChangedTracks++;
                }
            }

            if (writtenTracks == 0)
            {
                var emptyReportPath = Path.Combine(outputDir, "unity_bake_accelerated_apply_report.json");
                var emptyReport = new JObject
                {
                    ["status"] = "error",
                    ["message"] = requestMuscleStats.FrameVaryingCount > 0 && resultJointStats.FrameVaryingCount == 0
                        ? "Request muscle curves vary, but UnityBakeAccelerated produced no changed joint TRS. Check poseSolveMethod, muscle order, or Humanoid curve semantics."
                        : "UnityBakeAccelerated apply found no changed tracks to write.",
                    ["sourceGltf"] = sourceGltf,
                    ["outputGltf"] = outputGltfPath,
                    ["sourceRequest"] = requestPath,
                    ["sourceResult"] = resultPath,
                    ["clipName"] = clipName,
                    ["poseSolveMethod"] = result["setPoseMethod"] ?? request["poseSolveMethod"],
                    ["sampleCount"] = samples.Length,
                    ["jointPathCount"] = jointPaths.Length,
                    ["writtenTracks"] = writtenTracks,
                    ["requestMuscleFrameVaryingCount"] = requestMuscleStats.FrameVaryingCount,
                    ["requestMaxMuscleDelta"] = requestMuscleStats.MaxDelta,
                    ["resultJointFrameVaryingCount"] = resultJointStats.FrameVaryingCount,
                    ["resultMaxJointTranslationFrameDelta"] = resultJointStats.MaxTranslationDelta,
                    ["resultMaxJointRotationFrameDeltaDegrees"] = resultJointStats.MaxRotationDeltaDegrees,
                    ["skippedPathCount"] = skippedPaths.Count,
                    ["skippedPathsPreview"] = new JArray(skippedPaths.Take(64)),
                };
                File.WriteAllText(emptyReportPath, emptyReport.ToString(Formatting.Indented));
                Logger.Info($"UnityBakeAccelerated apply report: {emptyReportPath}");
                Logger.Error("UnityBakeAccelerated apply found no changed tracks to write.");
                return null;
            }

            var animations = gltf["animations"] as JArray;
            if (animations == null)
            {
                animations = new JArray();
                gltf["animations"] = animations;
            }
            animations.Add(animation);

            RemoveEmptyTopLevelArrays(gltf);
            Pad4(bufferBytes);
            ((JObject)gltf["buffers"]![0])["byteLength"] = bufferBytes.Count;
            File.WriteAllBytes(bufferFile, bufferBytes.ToArray());
            File.WriteAllText(outputGltfPath, gltf.ToString(Formatting.Indented));
            var standaloneAnimationGltf = WriteSkinlessAnimationGltf(
                gltf,
                animation,
                outputGltfPath,
                bufferFile,
                clipName,
                requestPath,
                resultPath);

            var status = coreFrameVaryingTracks > 0
                ? "ok"
                : coreRestChangedTracks > 0 ? "needs_review" : "needs_review";
            var message = coreFrameVaryingTracks > 0
                ? "UnityBakeAccelerated result was written to glTF TRS animation channels."
                : requestMuscleStats.FrameVaryingCount > 0 && resultJointStats.FrameVaryingCount == 0
                    ? "Request muscle curves vary, but UnityBakeAccelerated produced no frame-varying joint TRS. Check poseSolveMethod, muscle order, or Humanoid curve semantics."
                    : "Animation was written as a rest-pose delta or non-core change only; visual validation is required before treating it as reusable animation.";
            if (hasUnsupportedHumanPoseCurves)
            {
                status = "needs_review_unsupported_humanoid_curves";
                message = "UnityBakeAccelerated wrote diagnostic glTF TRS channels, but the source clip contains limb IK/TDOF Humanoid curves that HumanPoseHandler.SetHumanPose/SetInternalHumanPose does not consume. Use full Unity AnimationClip/PlayableGraph sampling before marking this animation reusable.";
            }

            var report = new JObject
            {
                ["status"] = status,
                ["message"] = message,
                ["sourceGltf"] = sourceGltf,
                ["outputGltf"] = outputGltfPath,
                ["sourceRequest"] = requestPath,
                ["sourceResult"] = resultPath,
                ["clipName"] = clipName,
                ["standaloneAnimationGltf"] = standaloneAnimationGltf,
                ["poseSolveMethod"] = result["setPoseMethod"] ?? request["poseSolveMethod"],
                ["sampleCount"] = samples.Length,
                ["jointPathCount"] = jointPaths.Length,
                ["writtenTracks"] = writtenTracks,
                ["coreChangedTracks"] = coreChangedTracks,
                ["restChangedTracks"] = restChangedTracks,
                ["frameVaryingTracks"] = frameVaryingTracks,
                ["coreRestChangedTracks"] = coreRestChangedTracks,
                ["coreFrameVaryingTracks"] = coreFrameVaryingTracks,
                ["maxFrameTranslationDelta"] = maxFrameTranslationDelta,
                ["maxFrameRotationDeltaDegrees"] = maxFrameRotationDeltaDegrees,
                ["maxRestTranslationDelta"] = maxRestTranslationDelta,
                ["maxRestRotationDeltaDegrees"] = maxRestRotationDeltaDegrees,
                ["requestMuscleFrameVaryingCount"] = requestMuscleStats.FrameVaryingCount,
                ["requestMaxMuscleDelta"] = requestMuscleStats.MaxDelta,
                ["resultJointFrameVaryingCount"] = resultJointStats.FrameVaryingCount,
                ["resultMaxJointTranslationFrameDelta"] = resultJointStats.MaxTranslationDelta,
                ["resultMaxJointRotationFrameDeltaDegrees"] = resultJointStats.MaxRotationDeltaDegrees,
                ["unsupportedHumanPoseCurveSummary"] = unsupportedHumanPoseCurves,
                ["motionClassificationRule"] = "restChangedTracks 表示采样姿态相对 glTF rest pose 有变化；frameVaryingTracks 表示动画帧之间有变化。短定格动作可能只有 restChanged，循环动作通常还应有 frameVarying。",
                ["skippedPathCount"] = skippedPaths.Count,
                ["skippedPathsPreview"] = new JArray(skippedPaths.Take(64)),
            };
            File.WriteAllText(Path.Combine(outputDir, "unity_bake_accelerated_apply_report.json"), report.ToString(Formatting.Indented));
            Logger.Info($"UnityBakeAccelerated glTF: {outputGltfPath}");
            Logger.Info($"UnityBakeAccelerated apply report: {Path.Combine(outputDir, "unity_bake_accelerated_apply_report.json")}");
            return outputGltfPath;
        }

        private static void WriteModelGateFailureReport(
            string outputDir,
            string sourceGltf,
            string outputGltfPath,
            string requestPath,
            string resultPath,
            string clipName,
            string message,
            JObject modelGate)
        {
            Directory.CreateDirectory(outputDir);
            DeleteStaleOutput(outputDir, outputGltfPath);
            var report = new JObject
            {
                ["generatedAt"] = DateTime.UtcNow.ToString("O"),
                ["status"] = "blocked",
                ["reason"] = "model_not_animation_ready",
                ["message"] = message,
                ["sourceGltf"] = sourceGltf,
                ["outputGltf"] = outputGltfPath,
                ["sourceRequest"] = requestPath,
                ["sourceResult"] = resultPath,
                ["clipName"] = clipName,
                ["modelGate"] = modelGate,
                ["rule"] = "模型 glTF 单独通过 Mesh/UV/材质/贴图/skin/bbox 静态验收后，才允许 UnityBakeAccelerated 写回可播放 glTF；白模、缺材质贴图或 skin 异常只保留诊断报告。",
            };
            var reportPath = Path.Combine(outputDir, "unity_bake_accelerated_apply_report.json");
            File.WriteAllText(reportPath, report.ToString(Formatting.Indented));
            Logger.Info($"UnityBakeAccelerated apply report: {reportPath}");
        }

        private static void DeleteStaleOutput(string outputDir, string outputGltfPath)
        {
            if (string.IsNullOrWhiteSpace(outputDir) || string.IsNullOrWhiteSpace(outputGltfPath))
            {
                return;
            }

            var fullOutputDir = Path.GetFullPath(outputDir);
            var fullGltfPath = Path.GetFullPath(outputGltfPath);
            if (!fullGltfPath.StartsWith(fullOutputDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetDirectoryName(fullGltfPath), fullOutputDir, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (File.Exists(fullGltfPath))
            {
                File.Delete(fullGltfPath);
            }
        }

        private static bool IsAcceleratedRequest(JObject json)
        {
            return string.Equals((string)json?["mode"], "UnityBakeAccelerated", StringComparison.OrdinalIgnoreCase)
                && json?["clips"] is JArray
                && json?["outputJson"] != null
                && (json?["avatarAsset"] != null || json?["avatar"] != null);
        }

        private static string TryFindSiblingRequest(string resultPath)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(resultPath));
            if (string.IsNullOrWhiteSpace(dir))
            {
                return null;
            }
            var exact = Path.Combine(dir, "unity_bake_accelerated_request.json");
            return File.Exists(exact)
                ? exact
                : Directory.EnumerateFiles(dir, "*accelerated*request*.json", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
        }

        private static string ResolveOutputPath(string requestPath, string outputGltfPath, string sourceGltf, string clipName)
        {
            if (!string.IsNullOrWhiteSpace(outputGltfPath))
            {
                return outputGltfPath;
            }
            var requestDir = Path.GetDirectoryName(Path.GetFullPath(requestPath)) ?? Directory.GetCurrentDirectory();
            var sourceName = Path.GetFileNameWithoutExtension(sourceGltf);
            return Path.Combine(requestDir, "AcceleratedPreview", $"{sourceName}__{SafeName(clipName)}.gltf");
        }

        private static void CopyModelFolder(string sourceGltf, string outputGltf)
        {
            var sourceDir = Path.GetDirectoryName(Path.GetFullPath(sourceGltf)) ?? Directory.GetCurrentDirectory();
            var outputDir = Path.GetDirectoryName(Path.GetFullPath(outputGltf)) ?? Directory.GetCurrentDirectory();
            Directory.CreateDirectory(outputDir);
            if (string.Equals(sourceDir, outputDir, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, file);
                var target = Path.Combine(outputDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target) ?? outputDir);
                File.Copy(file, target, overwrite: true);
            }
        }

        private static string WriteSkinlessAnimationGltf(
            JObject solvedModelGltf,
            JObject animation,
            string outputGltfPath,
            string bufferFile,
            string clipName,
            string requestPath,
            string resultPath)
        {
            var outputDir = Path.GetDirectoryName(Path.GetFullPath(outputGltfPath)) ?? Directory.GetCurrentDirectory();
            var standaloneDir = Path.Combine(outputDir, "StandaloneAnimation");
            Directory.CreateDirectory(standaloneDir);

            var standalonePath = Path.Combine(standaloneDir, $"{SafeName(clipName)}.gltf");
            var standaloneBuffer = Path.Combine(standaloneDir, Path.GetFileName(bufferFile));
            File.Copy(bufferFile, standaloneBuffer, overwrite: true);

            var gltf = new JObject
            {
                ["asset"] = solvedModelGltf["asset"]?.DeepClone() ?? new JObject
                {
                    ["version"] = "2.0",
                    ["generator"] = "AnimeStudio",
                },
                ["scene"] = solvedModelGltf["scene"]?.DeepClone() ?? 0,
                ["scenes"] = solvedModelGltf["scenes"]?.DeepClone() ?? new JArray(),
                ["nodes"] = solvedModelGltf["nodes"]?.DeepClone() ?? new JArray(),
                ["accessors"] = solvedModelGltf["accessors"]?.DeepClone() ?? new JArray(),
                ["bufferViews"] = solvedModelGltf["bufferViews"]?.DeepClone() ?? new JArray(),
                ["buffers"] = new JArray
                {
                    new JObject
                    {
                        ["uri"] = Path.GetFileName(standaloneBuffer).Replace('\\', '/'),
                        ["byteLength"] = ((JObject)solvedModelGltf["buffers"]![0])["byteLength"],
                    }
                },
                ["animations"] = new JArray(animation.DeepClone()),
                ["extras"] = new JObject
                {
                    ["animeStudioAnimationAsset"] = new JObject
                    {
                        ["skinless"] = true,
                        ["meshStripped"] = true,
                        ["materialStripped"] = true,
                        ["solvePath"] = "UnityBakeAccelerated",
                        ["sourceRequest"] = requestPath,
                        ["sourceResult"] = resultPath,
                    },
                },
            };

            foreach (var node in gltf["nodes"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                node.Remove("mesh");
                node.Remove("skin");
                node.Remove("weights");
            }

            foreach (var key in new[] { "meshes", "skins", "materials", "textures", "images", "samplers" })
            {
                gltf.Remove(key);
            }

            RemoveEmptyTopLevelArrays(gltf);
            File.WriteAllText(standalonePath, gltf.ToString(Formatting.Indented));
            return standalonePath;
        }

        private static string ResolveMainBufferFile(JObject gltf, string outputDir)
        {
            var uri = (string)gltf["buffers"]?.FirstOrDefault()?["uri"];
            if (string.IsNullOrWhiteSpace(uri) || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return Path.GetFullPath(Path.Combine(outputDir, uri.Replace('/', Path.DirectorySeparatorChar)));
        }

        private static void RemoveEmptyTopLevelArrays(JObject gltf)
        {
            foreach (var key in new[] { "images", "materials", "textures" })
            {
                if (gltf[key] is JArray array && array.Count == 0)
                {
                    gltf.Remove(key);
                }
            }
        }

        private static Dictionary<string, int> BuildNodePathIndex(JObject gltf, JObject[] nodes, string modelName)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            var rootIndices = gltf["scenes"]?.FirstOrDefault()?["nodes"]?.Values<int>().ToArray();
            if (rootIndices == null || rootIndices.Length == 0)
            {
                rootIndices = Enumerable.Range(0, nodes.Length)
                    .Where(i => !nodes.Any(n => n["children"]?.Values<int>().Contains(i) == true))
                    .ToArray();
            }

            var visited = new HashSet<int>();
            foreach (var root in rootIndices)
            {
                AddNodePath(nodes, root, "", modelName, result, visited);
            }
            return result;
        }

        private static void AddNodePath(JObject[] nodes, int index, string parentPath, string modelName, Dictionary<string, int> result, HashSet<int> visited)
        {
            if (index < 0 || index >= nodes.Length || !visited.Add(index))
            {
                return;
            }
            var name = ((string)nodes[index]["name"])?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "Node_" + index.ToString(CultureInfo.InvariantCulture);
            }
            var path = string.IsNullOrWhiteSpace(parentPath) ? name : parentPath + "/" + name;
            var normalized = NormalizeAcceleratedJointPath(path, modelName);
            if (!result.ContainsKey(normalized))
            {
                result[normalized] = index;
            }
            foreach (var child in nodes[index]["children"]?.Values<int>() ?? Enumerable.Empty<int>())
            {
                AddNodePath(nodes, child, path, modelName, result, visited);
            }
        }

        private static string NormalizeAcceleratedJointPath(string path, string modelName)
        {
            path = NormalizePath(path);
            if (!string.IsNullOrWhiteSpace(modelName)
                && path.StartsWith(modelName + "/", StringComparison.Ordinal))
            {
                return path[(modelName.Length + 1)..];
            }
            var rootIndex = path.IndexOf("/Root/", StringComparison.Ordinal);
            if (rootIndex >= 0)
            {
                return path[(rootIndex + 1)..];
            }
            return string.Equals(path, modelName, StringComparison.Ordinal) ? string.Empty : path;
        }

        private static string NormalizePath(string path) => (path ?? string.Empty).Replace('\\', '/').Trim('/');

        private static string NormalizeResultJointPath(string path, JObject request, string modelName)
        {
            path = NormalizePath(path);
            var avatarRoot = GetRequestAvatarRootName(request);
            if (!string.IsNullOrWhiteSpace(avatarRoot))
            {
                if (string.Equals(path, avatarRoot, StringComparison.Ordinal))
                {
                    path = string.Empty;
                }
                else if (path.StartsWith(avatarRoot + "/", StringComparison.Ordinal))
                {
                    path = path[(avatarRoot.Length + 1)..];
                }
            }

            return NormalizeAcceleratedJointPath(path, modelName);
        }

        private static string GetRequestAvatarRootName(JObject request)
        {
            var avatar = request?["avatar"] as JObject;
            var roots = avatar?["skeletonBones"]?.OfType<JObject>()
                .Where(x => string.IsNullOrWhiteSpace((string)x["parentName"]))
                .Select(x => NormalizePath((string)x["name"]))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<string>();
            return roots.Length == 1 ? roots[0] : null;
        }

        private static float[] ReadJointRow(float[] values, int jointIndex)
        {
            var offset = jointIndex * 7;
            if (values == null || offset < 0 || offset + 6 >= values.Length)
            {
                return null;
            }
            return new[]
            {
                values[offset + 0],
                values[offset + 1],
                values[offset + 2],
                values[offset + 3],
                values[offset + 4],
                values[offset + 5],
                values[offset + 6],
            };
        }

        private static float[] ReadNodeTranslation(JObject node)
        {
            var value = node?["translation"]?.Values<float>().ToArray();
            return value != null && value.Length >= 3 ? new[] { value[0], value[1], value[2] } : new[] { 0f, 0f, 0f };
        }

        private static float[] ReadNodeRotation(JObject node)
        {
            var value = node?["rotation"]?.Values<float>().ToArray();
            return value != null && value.Length >= 4 ? NormalizeQuaternion(new[] { value[0], value[1], value[2], value[3] }) : new[] { 0f, 0f, 0f, 1f };
        }

        private static float[] ApplyRestRelativeTranslation(float[] unityRow, float[] unityRest, float[] gltfRest)
        {
            var delta = UnityToGltfPosition(new[]
            {
                unityRow[0] - unityRest[0],
                unityRow[1] - unityRest[1],
                unityRow[2] - unityRest[2],
            });
            return new[] { gltfRest[0] + delta[0], gltfRest[1] + delta[1], gltfRest[2] + delta[2] };
        }

        private static float[] ApplyRestRelativeRotation(float[] unityRow, float[] unityRest, float[] gltfRest)
        {
            // HumanPoseHandler.GetInternalAvatarPose(avatar, jointPaths) 返回的 joint rotation
            // 已经是目标 joint 的 Unity 本地旋转，不是需要再相对 zero pose 叠到 glTF rest 上的 delta。
            // 旧 rest-relative 写法会让上臂/前臂/大腿/小腿多出稳定的大角度偏差。
            var current = NormalizeQuaternion(new[] { unityRow[3], unityRow[4], unityRow[5], unityRow[6] });
            return UnityToGltfRotation(current);
        }

        private static float[] UnityToGltfPosition(float[] value) => new[] { -value[0], value[1], value[2] };

        private static float[] UnityToGltfRotation(float[] value) => new[] { value[0], -value[1], -value[2], value[3] };

        private static float[][] MakeQuaternionsContinuous(float[][] values)
        {
            for (var i = 1; i < values.Length; i++)
            {
                if (Dot(values[i - 1], values[i]) < 0f)
                {
                    values[i] = new[] { -values[i][0], -values[i][1], -values[i][2], -values[i][3] };
                }
            }
            return values;
        }

        private static void AddChannel(JObject gltf, JObject animation, List<byte> bufferBytes, int timeAccessor, int nodeIndex, string path, float[][] values, string type)
        {
            var outputAccessor = AppendAccessor(gltf, bufferBytes, values.SelectMany(x => x).ToArray(), type, 5126, null, null);
            var samplers = (JArray)animation["samplers"];
            var channels = (JArray)animation["channels"];
            var samplerIndex = samplers.Count;
            samplers.Add(new JObject
            {
                ["input"] = timeAccessor,
                ["interpolation"] = "LINEAR",
                ["output"] = outputAccessor,
            });
            channels.Add(new JObject
            {
                ["sampler"] = samplerIndex,
                ["target"] = new JObject
                {
                    ["node"] = nodeIndex,
                    ["path"] = path,
                }
            });
        }

        private static int AppendAccessor(JObject gltf, List<byte> bufferBytes, float[] values, string type, int componentType, float[] min, float[] max)
        {
            Pad4(bufferBytes);
            var byteOffset = bufferBytes.Count;
            foreach (var value in values)
            {
                bufferBytes.AddRange(BitConverter.GetBytes(value));
            }

            var bufferViews = gltf["bufferViews"] as JArray;
            if (bufferViews == null)
            {
                bufferViews = new JArray();
                gltf["bufferViews"] = bufferViews;
            }
            var accessors = gltf["accessors"] as JArray;
            if (accessors == null)
            {
                accessors = new JArray();
                gltf["accessors"] = accessors;
            }

            var bufferViewIndex = bufferViews.Count;
            bufferViews.Add(new JObject
            {
                ["buffer"] = 0,
                ["byteOffset"] = byteOffset,
                ["byteLength"] = values.Length * 4,
            });

            var elementSize = type switch
            {
                "SCALAR" => 1,
                "VEC3" => 3,
                "VEC4" => 4,
                _ => 1,
            };
            var accessor = new JObject
            {
                ["bufferView"] = bufferViewIndex,
                ["byteOffset"] = 0,
                ["componentType"] = componentType,
                ["count"] = values.Length / elementSize,
                ["type"] = type,
            };
            if (min != null)
            {
                accessor["min"] = new JArray(min);
            }
            if (max != null)
            {
                accessor["max"] = new JArray(max);
            }
            var accessorIndex = accessors.Count;
            accessors.Add(accessor);
            return accessorIndex;
        }

        private static void Pad4(List<byte> bytes)
        {
            while (bytes.Count % 4 != 0)
            {
                bytes.Add(0);
            }
        }

        private static float MaxTranslationDelta(float[][] values)
        {
            if (values.Length < 2) return 0f;
            var first = values[0];
            return values.Skip(1).Select(x => TranslationDelta(first, x)).DefaultIfEmpty(0f).Max();
        }

        private static float MaxTranslationRestDelta(float[][] values, float[] rest)
        {
            return values.Select(x => TranslationDelta(x, rest)).DefaultIfEmpty(0f).Max();
        }

        private static float TranslationDelta(float[] a, float[] b)
        {
            var x = a[0] - b[0];
            var y = a[1] - b[1];
            var z = a[2] - b[2];
            return (float)Math.Sqrt(x * x + y * y + z * z);
        }

        private static float MaxRotationDelta(float[][] values)
        {
            if (values.Length < 2) return 0f;
            var first = values[0];
            return values.Skip(1).Select(x => RotationDeltaDegrees(first, x)).DefaultIfEmpty(0f).Max();
        }

        private static float MaxRotationRestDelta(float[][] values, float[] rest)
        {
            return values.Select(x => RotationDeltaDegrees(x, rest)).DefaultIfEmpty(0f).Max();
        }

        private readonly struct FrameDeltaStats
        {
            public FrameDeltaStats(int frameVaryingCount, float maxTranslationDelta, float maxRotationDeltaDegrees, float maxDelta)
            {
                FrameVaryingCount = frameVaryingCount;
                MaxTranslationDelta = maxTranslationDelta;
                MaxRotationDeltaDegrees = maxRotationDeltaDegrees;
                MaxDelta = maxDelta;
            }

            public int FrameVaryingCount { get; }
            public float MaxTranslationDelta { get; }
            public float MaxRotationDeltaDegrees { get; }
            public float MaxDelta { get; }
        }

        private static FrameDeltaStats MeasureRequestMuscleFrameDelta(JObject request)
        {
            var samples = request?["clips"]?.OfType<JObject>().FirstOrDefault()?["samples"]?.OfType<JObject>().ToArray()
                ?? Array.Empty<JObject>();
            if (samples.Length < 2)
            {
                return new FrameDeltaStats(0, 0, 0, 0);
            }

            var first = samples[0]["muscles"]?.Values<float>().ToArray() ?? Array.Empty<float>();
            if (first.Length == 0)
            {
                return new FrameDeltaStats(0, 0, 0, 0);
            }

            var varying = 0;
            var maxDelta = 0f;
            for (var muscleIndex = 0; muscleIndex < first.Length; muscleIndex++)
            {
                var values = samples
                    .Select(x => x["muscles"]?.Values<float>().ElementAtOrDefault(muscleIndex) ?? 0f)
                    .ToArray();
                var delta = values.Max() - values.Min();
                if (Math.Abs(delta) > 0.00001f)
                {
                    varying++;
                }
                maxDelta = Math.Max(maxDelta, Math.Abs(delta));
            }

            return new FrameDeltaStats(varying, 0, 0, maxDelta);
        }

        private static FrameDeltaStats MeasureResultJointFrameDelta(JObject[] samples, int jointCount)
        {
            if (samples == null || samples.Length < 2 || jointCount <= 0)
            {
                return new FrameDeltaStats(0, 0, 0, 0);
            }

            var varying = 0;
            var maxTranslation = 0f;
            var maxRotation = 0f;
            for (var jointIndex = 0; jointIndex < jointCount; jointIndex++)
            {
                var rows = samples
                    .Select(x => ReadJointRow(x["values"]?.Values<float>().ToArray(), jointIndex))
                    .Where(x => x != null)
                    .ToArray();
                if (rows.Length != samples.Length)
                {
                    continue;
                }

                var translations = rows.Select(x => new[] { x[0], x[1], x[2] }).ToArray();
                var rotations = MakeQuaternionsContinuous(rows.Select(x => new[] { x[3], x[4], x[5], x[6] }).ToArray());
                var translationDelta = MaxTranslationDelta(translations);
                var rotationDelta = MaxRotationDelta(rotations);
                if (translationDelta > 0.0005f || rotationDelta > 0.01f)
                {
                    varying++;
                }
                maxTranslation = Math.Max(maxTranslation, translationDelta);
                maxRotation = Math.Max(maxRotation, rotationDelta);
            }

            return new FrameDeltaStats(varying, maxTranslation, maxRotation, 0);
        }

        private static float RotationDeltaDegrees(float[] a, float[] b)
        {
            var dot = Math.Abs(DotNormalizedDouble(a, b));
            if (1.0 - dot <= 0.000000000001)
            {
                return 0f;
            }
            var clamped = Math.Min(1.0, Math.Max(-1.0, dot));
            return (float)(2.0 * Math.Acos(clamped) * 180.0 / Math.PI);
        }

        private static double DotNormalizedDouble(float[] a, float[] b)
        {
            var al = Math.Sqrt(a[0] * (double)a[0] + a[1] * (double)a[1] + a[2] * (double)a[2] + a[3] * (double)a[3]);
            var bl = Math.Sqrt(b[0] * (double)b[0] + b[1] * (double)b[1] + b[2] * (double)b[2] + b[3] * (double)b[3]);
            if (al <= 0.000001 || bl <= 0.000001)
            {
                return 1.0;
            }

            return (a[0] / al) * (b[0] / bl)
                + (a[1] / al) * (b[1] / bl)
                + (a[2] / al) * (b[2] / bl)
                + (a[3] / al) * (b[3] / bl);
        }

        private static float[] NormalizeQuaternion(float[] q)
        {
            var length = Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
            return length <= 0.000001
                ? new[] { 0f, 0f, 0f, 1f }
                : new[] { (float)(q[0] / length), (float)(q[1] / length), (float)(q[2] / length), (float)(q[3] / length) };
        }

        private static float[] Inverse(float[] q) => new[] { -q[0], -q[1], -q[2], q[3] };

        private static float[] Multiply(float[] a, float[] b)
        {
            return new[]
            {
                a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
                a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
                a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3],
                a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2],
            };
        }

        private static float Dot(float[] a, float[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3];

        private static string SafeName(string value)
        {
            var name = string.IsNullOrWhiteSpace(value) ? "UnityBakeAccelerated" : value;
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }
    }
}
