using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AnimeStudio.CLI
{
    internal static class UnityBakeResultApplier
    {
        public static string Apply(string requestOrResultPath, string outputGltfPath)
        {
            if (string.IsNullOrWhiteSpace(requestOrResultPath) || !File.Exists(requestOrResultPath))
            {
                Logger.Error($"Unity bake request/result not found: {requestOrResultPath}");
                return null;
            }

            var input = JObject.Parse(File.ReadAllText(requestOrResultPath));
            var requestPath = IsRequest(input) ? requestOrResultPath : TryFindSiblingRequest(requestOrResultPath);
            if (requestPath == null || !File.Exists(requestPath))
            {
                Logger.Error("Unable to find unity_bake_request.json. Apply needs the request so it can locate the source glTF.");
                return null;
            }

            var request = JObject.Parse(File.ReadAllText(requestPath));
            var resultPath = IsRequest(input)
                ? (string)request["outputJson"]
                : requestOrResultPath;
            if (string.IsNullOrWhiteSpace(resultPath) || !File.Exists(resultPath))
            {
                Logger.Error($"Unity bake result not found: {resultPath}");
                return null;
            }

            var result = JObject.Parse(File.ReadAllText(resultPath));
            if (!string.Equals((string)result["status"], "ok", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Error($"Unity bake result is not ok: {(string)result["message"]}");
                return null;
            }

            var sourceGltf = (string)request["animeStudioAssets"]?["model"]?["gltf"];
            if (string.IsNullOrWhiteSpace(sourceGltf) || !File.Exists(sourceGltf))
            {
                Logger.Error($"Source glTF not found in request: {sourceGltf}");
                return null;
            }

            var clipName = (string)result["clipName"] ?? (string)request["animeStudioAssets"]?["animation"]?["name"] ?? "UnityBaked";
            outputGltfPath = ResolveOutputPath(requestPath, outputGltfPath, sourceGltf, (string)request["animeStudioAssets"]?["model"]?["name"], clipName);
            CopyModelFolder(sourceGltf, outputGltfPath);

            var gltf = JObject.Parse(File.ReadAllText(outputGltfPath));
            var outputDir = Path.GetDirectoryName(outputGltfPath);
            var bufferFile = ResolveMainBufferFile(gltf, outputDir);
            if (bufferFile == null)
            {
                Logger.Error("Only file-buffer glTF is currently supported for Unity bake apply.");
                return null;
            }

            var bufferBytes = File.ReadAllBytes(bufferFile).ToList();
            var nodes = gltf["nodes"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var nodePathToIndex = BuildNodePathIndex(nodes);
            var animation = new JObject
            {
                ["name"] = clipName,
                ["samplers"] = new JArray(),
                ["channels"] = new JArray(),
                ["extras"] = new JObject
                {
                    ["animeStudioUnityBake"] = new JObject
                    {
                        ["sourceRequest"] = requestPath,
                        ["sourceResult"] = resultPath,
                        ["avatar"] = result["avatarName"],
                        ["avatarValid"] = result["avatarValid"],
                        ["rigRestPoseSource"] = result["rigRestPoseSource"],
                        ["rigRestPoseApplied"] = result["rigRestPoseApplied"],
                        ["sampleCount"] = result["sampleCount"],
                        ["changedTrackCount"] = result["changedTrackCount"],
                        ["coordinateConversion"] = "Unity baked local TRS is applied as a delta, then converted back to glTF basis.",
                        ["humanoidDeltaBase"] = "rest_pose",
                        ["rotationApplyMode"] = RotationApplyMode,
                    }
                }
            };

            var writtenTracks = 0;
            var frameVaryingTracks = 0;
            var frameVaryingChannels = 0;
            var coreBodyFrameVaryingTracks = 0;
            var firstPoseChangedTracks = 0;
            var coreBodyFirstPoseChangedTracks = 0;
            var unityRestPoseTracks = 0;
            var requiresHumanoidBake = (bool?)request["animeStudioAssets"]?["animation"]?["requiresHumanoidBake"] ?? false;
            var humanoidDeltaBase = "rest_pose";
            var trackMotionReports = new List<TrackMotionReport>();
            var skippedTracks = new List<string>();
            var suppressedHumanoidRootRotationTracks = new List<string>();
            foreach (var track in result["tracks"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                if ((bool?)track["changed"] != true)
                {
                    continue;
                }

                var path = NormalizeBakePath((string)track["path"], nodes);
                if (!nodePathToIndex.TryGetValue(path, out var nodeIndex))
                {
                    skippedTracks.Add((string)track["path"]);
                    continue;
                }

                var times = track["rotations"]?.OfType<JObject>().Select(x => (float)x["time"]).ToArray() ?? Array.Empty<float>();
                if (times.Length == 0)
                {
                    continue;
                }

                var node = nodes[nodeIndex];
                var restTranslation = ReadNodeTranslation(node);
                var restRotation = ReadNodeRotation(node);
                var restScale = ReadNodeScale(node);
                float[] unityRestTranslation;
                float[] unityRestRotation;
                float[] unityRestScale;
                var hasUnityRestPose =
                    TryReadVector3Key(track["restTranslation"], out unityRestTranslation) &
                    TryReadQuaternionKey(track["restRotation"], out unityRestRotation) &
                    TryReadVector3Key(track["restScale"], out unityRestScale);
                if (hasUnityRestPose)
                {
                    unityRestPoseTracks++;
                }

                var rawTranslations = ReadVec3(track["translations"]);
                var rawRotations = ReadVec4(track["rotations"]);
                var rawScales = ReadVec3(track["scales"]);
                var firstPose = BuildFirstPoseMotionReport(
                    (string)track["path"],
                    path,
                    rawTranslations,
                    rawRotations,
                    rawScales,
                    hasUnityRestPose ? unityRestTranslation : GltfToUnityPosition(restTranslation),
                    hasUnityRestPose ? unityRestRotation : GltfToUnityRotation(restRotation),
                    hasUnityRestPose ? unityRestScale : restScale);
                if (firstPose.FirstPoseChanged)
                {
                    firstPoseChangedTracks++;
                    if (IsCoreBodyBonePath(path))
                    {
                        coreBodyFirstPoseChangedTracks++;
                    }
                }
                var translations = ApplyRestRelativeTranslations(
                    rawTranslations,
                    hasUnityRestPose ? unityRestTranslation : GltfToUnityPosition(restTranslation),
                    restTranslation,
                    useFirstSampleAsBase: false);
                var rotations = ApplyRestRelativeRotations(
                    rawRotations,
                    hasUnityRestPose ? unityRestRotation : GltfToUnityRotation(restRotation),
                    restRotation,
                    useFirstSampleAsBase: false);
                if (requiresHumanoidBake && IsHumanoidSkeletonRootContainer(nodeIndex, nodes))
                {
                    // Humanoid bake 会把 body/root orientation 落到骨架容器根（常见如 Bip001）。
                    // 这个节点不是实际人体骨骼；直接写入 rotation 会把整个人横向翻倒。
                    // 这里保留 translation/scale，只把容器根 rotation 固定为 glTF rest。
                    rotations = rotations.Select(_ => (float[])restRotation.Clone()).ToArray();
                    suppressedHumanoidRootRotationTracks.Add(path);
                }
                var scales = ApplyRestRelativeScales(
                    rawScales,
                    hasUnityRestPose ? unityRestScale : restScale,
                    restScale,
                    useFirstSampleAsBase: false);
                var motion = BuildTrackMotionReport((string)track["path"], path, translations, rotations, scales);
                if (motion.FrameVarying)
                {
                    frameVaryingTracks++;
                    if (IsCoreBodyBonePath(path))
                    {
                        coreBodyFrameVaryingTracks++;
                    }
                }
                frameVaryingChannels += motion.FrameVaryingChannelCount;
                trackMotionReports.Add(motion with
                {
                    FirstPoseChanged = firstPose.FirstPoseChanged,
                    FirstPoseTranslationDelta = firstPose.FirstPoseTranslationDelta,
                    FirstPoseRotationDelta = firstPose.FirstPoseRotationDelta,
                    FirstPoseScaleDelta = firstPose.FirstPoseScaleDelta,
                });

                WriteChannel(gltf, animation, bufferBytes, nodeIndex, "translation", times, translations);
                WriteChannel(gltf, animation, bufferBytes, nodeIndex, "rotation", times, rotations);
                WriteChannel(gltf, animation, bufferBytes, nodeIndex, "scale", times, scales);
                writtenTracks++;
            }

            if (writtenTracks == 0)
            {
                Logger.Error("No baked tracks matched glTF nodes; inspect path mapping before trusting this output.");
                return null;
            }

            // 预览文件只保留当前烘焙动画，避免 F3D/Blender 默认播放旧的表情或辅助动画，
            // 让用户打开文件时看到的就是这次选择的模型 + 动作。
            gltf["animations"] = new JArray(animation);
            ((JArray)(gltf["buffers"]))[0]["byteLength"] = bufferBytes.Count;
            File.WriteAllBytes(bufferFile, bufferBytes.ToArray());
            File.WriteAllText(outputGltfPath, JsonConvert.SerializeObject(gltf, Formatting.Indented));
            var skinReport = BuildSkinAnimationReport(gltf);
            var avatarTrust = BuildAvatarTrustReport(request, result);
            var unityClipIsHumanMotion = (bool?)result["isHumanMotion"] ?? false;
            var status = skippedTracks.Count == 0 && skinReport.InvalidTargetCount == 0
                ? (frameVaryingTracks > 0 ? "ok" : "static_pose")
                : "warning";
            var message = status == "static_pose"
                ? "Unity bake wrote tracks that differ from rest pose, but no written channel changes over sampled frames. Treat this as a static pose diagnostic, not a playable animation."
                : null;
            if (requiresHumanoidBake && status == "ok" && coreBodyFrameVaryingTracks == 0)
            {
                status = "needs_review";
                message = coreBodyFirstPoseChangedTracks > 0
                    ? "Unity bake produced a static or pose-only Humanoid result: core body bones differ from rest pose in the first frame, but do not change over time. The baked glTF preserves this as a rest-pose-relative pose, but it still needs visual review because it is not a frame-varying body animation."
                    : unityClipIsHumanMotion
                    ? "Unity bake only produced frame-varying root or auxiliary motion; no core body bone changed over time. Treat this as a Humanoid bake diagnostic until a body-driving clip is confirmed."
                    : "Unity bake only produced frame-varying root or auxiliary motion; no core body bone changed over time. Unity also reported isHumanMotion=false for the imported clip, so the current .anim input was not sampled as a real Humanoid/Muscle clip.";
            }
            if (string.Equals(humanoidDeltaBase, "first_sample", StringComparison.OrdinalIgnoreCase))
            {
                status = "needs_review";
                message = string.IsNullOrWhiteSpace(message)
                    ? "Humanoid bake was applied with first-sample delta as a diagnostic anti-twist path. This prevents obvious flipped output, but it can erase meaningful first-frame poses such as idle, aim, or pose-only clips, so it is not a trusted production animation export."
                    : message;
            }
            if (!avatarTrust.TrustedProductionBake && (status == "ok" || status == "warning"))
            {
                status = "needs_review";
                message = avatarTrust.Message;
            }

            var report = new
            {
                generatedAt = DateTime.UtcNow.ToString("O"),
                status,
                message,
                sourceGltf,
                outputGltf = outputGltfPath,
                request = requestPath,
                result = resultPath,
                clipName,
                unityClipIsHumanMotion,
                unitySampleBounds = result["sampleBounds"],
                writtenTracks,
                frameVaryingTracks,
                frameVaryingChannels,
                coreBodyFrameVaryingTracks,
                firstPoseChangedTracks,
                coreBodyFirstPoseChangedTracks,
                unityRestPoseTracks,
                humanoidDeltaBase,
                humanoidFirstSampleDeltaApplied = false,
                unityBakeHelperVersion = (int?)result["helperVersion"] ?? 1,
                unityBakeRigRestPoseSource = (string)result["rigRestPoseSource"],
                unityBakeRigRestPoseApplied = (bool?)result["rigRestPoseApplied"] ?? false,
                suppressedHumanoidRootRotationTrackCount = suppressedHumanoidRootRotationTracks.Count,
                suppressedHumanoidRootRotationTracks = suppressedHumanoidRootRotationTracks.Take(32).ToArray(),
                staticPoseTracks = Math.Max(0, writtenTracks - frameVaryingTracks),
                avatarTrust,
                trackMotionSample = trackMotionReports
                    .OrderByDescending(x => x.MaxRotationDelta)
                    .ThenByDescending(x => x.MaxTranslationDelta)
                    .ThenByDescending(x => x.MaxScaleDelta)
                    .Take(64)
                    .ToArray(),
                skippedTrackCount = skippedTracks.Count,
                skippedTracks = skippedTracks.Take(128).ToArray(),
                skinAnimation = skinReport,
            };
            var reportPath = Path.Combine(outputDir, "unity_bake_apply_report.json");
            File.WriteAllText(reportPath, JsonConvert.SerializeObject(report, Formatting.Indented));
            Logger.Info($"Baked glTF preview: {outputGltfPath}");
            Logger.Info($"Baked apply report: {reportPath}");
            return outputGltfPath;
        }

        private static AvatarTrustReport BuildAvatarTrustReport(JObject request, JObject result)
        {
            var avatar = request["animeStudioAssets"]?["model"]?["avatar"] as JObject;
            if (avatar == null)
            {
                return new AvatarTrustReport(
                    TrustedProductionBake: false,
                    Source: "missing_avatar",
                    Message: "Unity bake request did not contain Avatar metadata, so the baked glTF must be treated as diagnostic output.");
            }

            var skeletonBoneCount = avatar["skeletonBones"] is JArray skeletonBones ? skeletonBones.Count : 0;
            var humanBonesSource = (string)avatar["humanBonesSource"];
            if (skeletonBoneCount > 0)
            {
                return new AvatarTrustReport(
                    TrustedProductionBake: true,
                    Source: "human_description_skeleton_bones",
                    Message: null);
            }

            var rigRestPoseSource = (string)result?["rigRestPoseSource"];
            var rigRestPoseApplied = (bool?)result?["rigRestPoseApplied"] ?? false;
            if (string.Equals(rigRestPoseSource, "imported_unity_avatar_asset", StringComparison.OrdinalIgnoreCase)
                && rigRestPoseApplied
                && ((bool?)result?["avatarValid"] ?? false))
            {
                return new AvatarTrustReport(
                    TrustedProductionBake: true,
                    Source: "imported_unity_avatar_asset",
                    Message: null);
            }

            if (HasCompleteAvatarOracle(avatar) && ((bool?)result?["avatarValid"] ?? false))
            {
                return new AvatarTrustReport(
                    TrustedProductionBake: false,
                    Source: string.IsNullOrWhiteSpace(rigRestPoseSource) ? "avatar_constant_oracle_diagnostic" : rigRestPoseSource,
                    Message: "Unity bake used AvatarConstant/internalSolver metadata to rebuild a temporary Avatar. This is deterministic Unity source metadata, but it is not the original UnityEngine.Avatar or complete HumanDescription.skeletonBones; Genshin samples show this path can produce wrong arms/legs, so it is diagnostic only and must not be counted as production bake.");
            }

            if (string.Equals(humanBonesSource, "avatar.internalSolver.humanBoneIndex", StringComparison.OrdinalIgnoreCase))
            {
                return new AvatarTrustReport(
                    TrustedProductionBake: false,
                    Source: "internal_solver_derived_human_bones",
                    Message: "Unity bake used HumanBone mapping reconstructed from Avatar internalSolver metadata. Without original Unity prefab Avatar or HumanDescription.skeletonBones, the helper has to build a temporary Avatar; this is diagnostic only and must not be accepted as production animation.");
            }

            return new AvatarTrustReport(
                TrustedProductionBake: false,
                Source: string.IsNullOrWhiteSpace(humanBonesSource) ? "missing_human_description_skeleton_bones" : humanBonesSource,
                Message: "Unity bake did not include original HumanDescription.skeletonBones. Treat this baked glTF as needing visual validation or metadata refresh before production use.");
        }

        private static bool HasCompleteAvatarOracle(JObject avatar)
        {
            var oracle = avatar?["oracle"] as JObject;
            if (oracle == null)
            {
                return false;
            }

            var humanBoneIndex = oracle["humanBoneIndex"] as JArray;
            var humanSkeleton = oracle["humanSkeleton"] as JObject;
            var avatarSkeleton = oracle["avatarSkeleton"] as JObject;
            var humanNodes = humanSkeleton?["nodes"] as JArray;
            var humanPose = humanSkeleton?["pose"] as JArray;
            var avatarNodes = avatarSkeleton?["nodes"] as JArray;
            var avatarDefaultPose = avatarSkeleton?["defaultPose"] as JArray;
            return humanBoneIndex != null && humanBoneIndex.Values<int?>().Any(x => x.GetValueOrDefault(-1) >= 0)
                && humanNodes != null && humanNodes.Count > 0
                && humanPose != null && humanPose.Count >= humanNodes.Count
                && avatarNodes != null && avatarNodes.Count > 0
                && avatarDefaultPose != null && avatarDefaultPose.Count >= avatarNodes.Count;
        }

        private static bool IsRequest(JObject value) => value["animeStudioAssets"] != null && value["outputJson"] != null;

        private static string TryFindSiblingRequest(string resultPath)
        {
            var directory = Path.GetDirectoryName(resultPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return null;
            }
            var sibling = Path.Combine(directory, "unity_bake_request.json");
            return File.Exists(sibling) ? sibling : null;
        }

        private static string ResolveOutputPath(string requestPath, string outputGltfPath, string sourceGltf, string modelName, string clipName)
        {
            if (!string.IsNullOrWhiteSpace(outputGltfPath))
            {
                return outputGltfPath;
            }
            var root = Path.Combine(Path.GetDirectoryName(requestPath) ?? Directory.GetCurrentDirectory(), "BakedPreview");
            return Path.Combine(root, $"{SafeName(modelName)}__{SafeName(clipName)}.gltf");
        }

        private static void CopyModelFolder(string sourceGltf, string outputGltf)
        {
            var sourceDir = Path.GetDirectoryName(sourceGltf);
            var outputDir = Path.GetDirectoryName(outputGltf);
            if (string.IsNullOrWhiteSpace(sourceDir) || string.IsNullOrWhiteSpace(outputDir))
            {
                throw new InvalidOperationException("Invalid glTF folder.");
            }
            Directory.CreateDirectory(outputDir);
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, file);
                var target = Path.Combine(outputDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, overwrite: true);
            }
            var copiedGltf = Path.Combine(outputDir, Path.GetFileName(sourceGltf));
            if (!string.Equals(copiedGltf, outputGltf, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(copiedGltf, outputGltf, overwrite: true);
            }
        }

        private static string ResolveMainBufferFile(JObject gltf, string outputDir)
        {
            var buffer = gltf["buffers"]?.OfType<JObject>().FirstOrDefault();
            var uri = (string)buffer?["uri"];
            if (string.IsNullOrWhiteSpace(uri) || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return Path.Combine(outputDir, uri.Replace('/', Path.DirectorySeparatorChar));
        }

        private static Dictionary<string, int> BuildNodePathIndex(JObject[] nodes)
        {
            var parents = new int?[nodes.Length];
            for (var i = 0; i < nodes.Length; i++)
            {
                foreach (var child in nodes[i]["children"]?.Values<int>() ?? Enumerable.Empty<int>())
                {
                    if (child >= 0 && child < parents.Length)
                    {
                        parents[child] = i;
                    }
                }
            }

            var paths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string Build(int index)
            {
                var name = (string)nodes[index]["name"] ?? $"node_{index}";
                return parents[index].HasValue ? $"{Build(parents[index].Value)}/{name}" : name;
            }
            for (var i = 0; i < nodes.Length; i++)
            {
                paths[Build(i)] = i;
            }
            return paths;
        }

        private static SkinAnimationReport BuildSkinAnimationReport(JObject gltf)
        {
            var nodes = gltf["nodes"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var skins = gltf["skins"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var animations = gltf["animations"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var meshSkinNodes = nodes
                .Select((node, index) => new { node, index })
                .Where(x => x.node["mesh"] != null && x.node["skin"] != null)
                .Select(x => new MeshSkinNode(x.index, (string)x.node["name"], (int?)x.node["mesh"] ?? -1, (int?)x.node["skin"] ?? -1))
                .ToArray();
            var joints = skins
                .SelectMany(skin => skin["joints"]?.Values<int>() ?? Enumerable.Empty<int>())
                .Where(x => x >= 0 && x < nodes.Length)
                .Distinct()
                .ToHashSet();
            var targets = animations
                .SelectMany(animation => animation["channels"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                .Select(channel => (int?)channel["target"]?["node"])
                .Where(x => x.HasValue && x.Value >= 0 && x.Value < nodes.Length)
                .Select(x => x.Value)
                .Distinct()
                .ToHashSet();
            var childToParent = BuildChildToParent(nodes);
            var hierarchyParentTargets = targets
                .Where(x => !joints.Contains(x) && HasDescendantJoint(nodes, joints, x))
                .Select(x => new NodeRef(x, BuildNodePath(nodes, x, childToParent)))
                .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var invalidTargets = targets
                .Where(x => !joints.Contains(x) && !HasDescendantJoint(nodes, joints, x))
                .Select(x => new NodeRef(x, BuildNodePath(nodes, x)))
                .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var jointNotTarget = joints
                .Where(x => !targets.Contains(x))
                .Select(x => new NodeRef(x, BuildNodePath(nodes, x, childToParent)))
                .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .Take(128)
                .ToArray();

            return new SkinAnimationReport(
                Nodes: nodes.Length,
                Skins: skins.Length,
                SkinJoints: joints.Count,
                AnimationTargets: targets.Count,
                TargetNotJointCount: invalidTargets.Length + hierarchyParentTargets.Length,
                HierarchyParentTargetCount: hierarchyParentTargets.Length,
                InvalidTargetCount: invalidTargets.Length,
                JointNotTargetCount: joints.Count - targets.Count(x => joints.Contains(x)),
                MeshSkinNodes: meshSkinNodes,
                HierarchyParentTargets: hierarchyParentTargets,
                InvalidTargets: invalidTargets,
                JointNotTargetSample: jointNotTarget,
                Rule: "UnityGLTF-style check: skin.joints come from SkinnedMeshRenderer.bones, and baked animation channels should target that node graph. Non-joint parent targets are valid when they have skinned joint descendants, because glTF node hierarchy propagates their transforms into the skin."
            );
        }

        private static string BuildNodePath(JObject[] nodes, int index)
        {
            return BuildNodePath(nodes, index, BuildChildToParent(nodes));
        }

        private static string BuildNodePath(JObject[] nodes, int index, int?[] parents)
        {
            var stack = new Stack<string>();
            int? current = index;
            while (current.HasValue)
            {
                stack.Push((string)nodes[current.Value]["name"] ?? $"node_{current.Value}");
                current = parents[current.Value];
            }
            return string.Join("/", stack);
        }

        private static int?[] BuildChildToParent(JObject[] nodes)
        {
            var parents = new int?[nodes.Length];
            for (var i = 0; i < nodes.Length; i++)
            {
                foreach (var child in nodes[i]["children"]?.Values<int>() ?? Enumerable.Empty<int>())
                {
                    if (child >= 0 && child < parents.Length)
                    {
                        parents[child] = i;
                    }
                }
            }
            return parents;
        }

        private static bool HasDescendantJoint(JObject[] nodes, HashSet<int> joints, int nodeIndex)
        {
            foreach (var child in nodes[nodeIndex]["children"]?.Values<int>() ?? Enumerable.Empty<int>())
            {
                if (child < 0 || child >= nodes.Length)
                {
                    continue;
                }
                if (joints.Contains(child) || HasDescendantJoint(nodes, joints, child))
                {
                    return true;
                }
            }
            return false;
        }

        private static string NormalizeBakePath(string bakePath, JObject[] nodes)
        {
            if (string.IsNullOrWhiteSpace(bakePath))
            {
                return bakePath;
            }
            var parts = bakePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (parts.Count > 0 && parts[0].EndsWith("_AnimeStudioBake", StringComparison.OrdinalIgnoreCase))
            {
                var bakedRoot = parts[0];
                parts.RemoveAt(0);
                if (parts.Count == 0)
                {
                    return FindSingleRootNodeName(nodes) ?? bakedRoot[..^"_AnimeStudioBake".Length];
                }
            }
            return string.Join("/", parts);
        }

        private static string FindSingleRootNodeName(JObject[] nodes)
        {
            var hasParent = new bool[nodes.Length];
            for (var i = 0; i < nodes.Length; i++)
            {
                foreach (var child in nodes[i]["children"]?.Values<int>() ?? Enumerable.Empty<int>())
                {
                    if (child >= 0 && child < hasParent.Length)
                    {
                        hasParent[child] = true;
                    }
                }
            }

            var roots = nodes
                .Select((node, index) => new { node, index })
                .Where(x => !hasParent[x.index])
                .ToArray();
            return roots.Length == 1
                ? (string)roots[0].node["name"]
                : null;
        }

        private static bool IsCoreBodyBonePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var leaf = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? path;
            var bodyTokens = new[]
            {
                "Pelvis", "Spine", "Neck", "Head", "Clavicle",
                "UpperArm", "Forearm", "Hand",
                "Thigh", "Calf", "Foot", "Toe"
            };
            return bodyTokens.Any(token => leaf.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsHumanoidSkeletonRootContainer(int nodeIndex, JObject[] nodes)
        {
            if (nodeIndex < 0 || nodeIndex >= nodes.Length)
            {
                return false;
            }

            var nodeName = (string)nodes[nodeIndex]["name"] ?? string.Empty;
            if (IsCoreBodyBonePath(nodeName))
            {
                return false;
            }

            foreach (var childIndex in nodes[nodeIndex]["children"]?.Values<int>() ?? Enumerable.Empty<int>())
            {
                if (childIndex < 0 || childIndex >= nodes.Length)
                {
                    continue;
                }

                var childName = (string)nodes[childIndex]["name"] ?? string.Empty;
                if (childName.IndexOf("Pelvis", StringComparison.OrdinalIgnoreCase) >= 0
                    || childName.IndexOf("Hips", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static float[][] ReadVec3(JToken values, Func<float[], float[]> convert = null) =>
            values?.OfType<JObject>()
                .Select(x =>
                {
                    var value = new[] { (float)x["x"], (float)x["y"], (float)x["z"] };
                    return convert == null ? value : convert(value);
                })
                .ToArray() ?? Array.Empty<float[]>();

        private static float[][] ReadVec4(JToken values, Func<float[], float[]> convert = null) =>
            values?.OfType<JObject>()
                .Select(x =>
                {
                    var value = new[] { (float)x["x"], (float)x["y"], (float)x["z"], (float)x["w"] };
                    return convert == null ? value : convert(value);
                })
                .ToArray() ?? Array.Empty<float[]>();

        private static float[][] ApplyRestRelativeTranslations(float[][] unityValues, float[] unityRest, float[] gltfRest, bool useFirstSampleAsBase)
        {
            var result = new float[unityValues.Length][];
            var baseValue = useFirstSampleAsBase && unityValues.Length > 0
                ? unityValues[0]
                : unityRest;
            for (var i = 0; i < unityValues.Length; i++)
            {
                var unityDelta = new[]
                {
                    unityValues[i][0] - baseValue[0],
                    unityValues[i][1] - baseValue[1],
                    unityValues[i][2] - baseValue[2],
                };
                var gltfDelta = UnityToGltfPosition(unityDelta);
                result[i] = new[]
                {
                    gltfRest[0] + gltfDelta[0],
                    gltfRest[1] + gltfDelta[1],
                    gltfRest[2] + gltfDelta[2],
                };
            }
            return result;
        }

        private static float[][] ApplyRestRelativeRotations(float[][] unityValues, float[] unityRest, float[] gltfRest, bool useFirstSampleAsBase)
        {
            var result = new float[unityValues.Length][];
            var baseRotation = useFirstSampleAsBase && unityValues.Length > 0
                ? NormalizeQuaternion(unityValues[0])
                : NormalizeQuaternion(unityRest);
            var inverseBaseRotation = Inverse(baseRotation);
            var mode = RotationApplyMode;
            for (var i = 0; i < unityValues.Length; i++)
            {
                var current = NormalizeQuaternion(unityValues[i]);
                if (string.Equals(mode, "absolute_unity_to_gltf", StringComparison.OrdinalIgnoreCase))
                {
                    result[i] = NormalizeQuaternion(UnityToGltfRotation(current));
                    continue;
                }
                if (string.Equals(mode, "absolute_raw", StringComparison.OrdinalIgnoreCase))
                {
                    result[i] = current;
                    continue;
                }

                // Unity bake 采样到的是 retarget 后的绝对局部姿态。
                // glTF 模型要保留自己的 rest pose，只写 Unity 采样姿态相对 Unity rest pose 的变化。
                // 不能默认拿第一帧当基准，否则会把 idle、aim、pose-only 这类第一帧有效姿态抹掉。
                var unityDelta = string.Equals(mode, "delta_current_inverse_rest", StringComparison.OrdinalIgnoreCase)
                    ? NormalizeQuaternion(Multiply(current, inverseBaseRotation))
                    : NormalizeQuaternion(Multiply(inverseBaseRotation, current));
                var gltfDelta = string.Equals(mode, "delta_no_basis", StringComparison.OrdinalIgnoreCase)
                    ? unityDelta
                    : UnityToGltfRotation(unityDelta);
                result[i] = string.Equals(mode, "delta_pre_multiply", StringComparison.OrdinalIgnoreCase)
                    ? NormalizeQuaternion(Multiply(gltfDelta, gltfRest))
                    : NormalizeQuaternion(Multiply(gltfRest, gltfDelta));
            }
            return result;
        }

        private static string RotationApplyMode =>
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANIMESTUDIO_UNITY_BAKE_ROTATION_MODE"))
                ? "delta_inverse_rest_current"
                : Environment.GetEnvironmentVariable("ANIMESTUDIO_UNITY_BAKE_ROTATION_MODE");

        private static float[][] ApplyRestRelativeScales(float[][] unityValues, float[] unityRestScale, float[] gltfRestScale, bool useFirstSampleAsBase)
        {
            var result = new float[unityValues.Length][];
            var baseScale = useFirstSampleAsBase && unityValues.Length > 0
                ? unityValues[0]
                : unityRestScale;
            for (var i = 0; i < unityValues.Length; i++)
            {
                result[i] = new[]
                {
                    ApplyScaleDelta(unityValues[i][0], baseScale[0], gltfRestScale[0]),
                    ApplyScaleDelta(unityValues[i][1], baseScale[1], gltfRestScale[1]),
                    ApplyScaleDelta(unityValues[i][2], baseScale[2], gltfRestScale[2]),
                };
            }
            return result;
        }

        private static float ApplyScaleDelta(float current, float unityRest, float gltfRest)
        {
            return Math.Abs(unityRest) <= 0.000001f
                ? current
                : gltfRest * (current / unityRest);
        }

        private static float[] ReadNodeTranslation(JObject node)
        {
            var value = node?["translation"]?.Values<float>().ToArray();
            return value != null && value.Length >= 3
                ? new[] { value[0], value[1], value[2] }
                : new[] { 0f, 0f, 0f };
        }

        private static float[] ReadNodeRotation(JObject node)
        {
            var value = node?["rotation"]?.Values<float>().ToArray();
            return value != null && value.Length >= 4
                ? NormalizeQuaternion(new[] { value[0], value[1], value[2], value[3] })
                : new[] { 0f, 0f, 0f, 1f };
        }

        private static float[] ReadNodeScale(JObject node)
        {
            var value = node?["scale"]?.Values<float>().ToArray();
            return value != null && value.Length >= 3
                ? new[] { value[0], value[1], value[2] }
                : new[] { 1f, 1f, 1f };
        }

        private static bool TryReadVector3Key(JToken token, out float[] value)
        {
            value = null;
            if (token == null)
            {
                return false;
            }
            var x = (float?)token["x"];
            var y = (float?)token["y"];
            var z = (float?)token["z"];
            if (!x.HasValue || !y.HasValue || !z.HasValue)
            {
                return false;
            }
            value = new[] { x.Value, y.Value, z.Value };
            return true;
        }

        private static bool TryReadQuaternionKey(JToken token, out float[] value)
        {
            value = null;
            if (token == null)
            {
                return false;
            }
            var x = (float?)token["x"];
            var y = (float?)token["y"];
            var z = (float?)token["z"];
            var w = (float?)token["w"];
            if (!x.HasValue || !y.HasValue || !z.HasValue || !w.HasValue)
            {
                return false;
            }
            value = NormalizeQuaternion(new[] { x.Value, y.Value, z.Value, w.Value });
            return true;
        }

        private static float[] UnityToGltfPosition(float[] value) => new[] { -value[0], value[1], value[2] };

        private static float[] UnityToGltfRotation(float[] value) => new[] { value[0], -value[1], -value[2], value[3] };

        private static float[] GltfToUnityPosition(float[] value) => UnityToGltfPosition(value);

        private static float[] GltfToUnityRotation(float[] value) => UnityToGltfRotation(value);

        private static TrackMotionReport BuildTrackMotionReport(
            string bakePath,
            string gltfPath,
            float[][] translations,
            float[][] rotations,
            float[][] scales)
        {
            var translationDelta = MaxDeltaFromFirst(translations);
            var rotationDelta = MaxRotationDeltaFromFirst(rotations);
            var scaleDelta = MaxDeltaFromFirst(scales);
            var translationVarying = translationDelta > 0.00001f;
            var rotationVarying = rotationDelta > 0.00001f;
            var scaleVarying = scaleDelta > 0.00001f;
            return new TrackMotionReport(
                BakePath: bakePath,
                GltfPath: gltfPath,
                FrameVarying: translationVarying || rotationVarying || scaleVarying,
                FrameVaryingChannelCount:
                    (translationVarying ? 1 : 0) +
                    (rotationVarying ? 1 : 0) +
                    (scaleVarying ? 1 : 0),
                MaxTranslationDelta: translationDelta,
                MaxRotationDelta: rotationDelta,
                MaxScaleDelta: scaleDelta);
        }

        private static FirstPoseMotionReport BuildFirstPoseMotionReport(
            string bakePath,
            string gltfPath,
            float[][] unityTranslations,
            float[][] unityRotations,
            float[][] unityScales,
            float[] unityRestTranslation,
            float[] unityRestRotation,
            float[] unityRestScale)
        {
            var translationDelta = FirstVectorDelta(unityTranslations, unityRestTranslation);
            var rotationDelta = FirstRotationDelta(unityRotations, unityRestRotation);
            var scaleDelta = FirstVectorDelta(unityScales, unityRestScale);
            return new FirstPoseMotionReport(
                BakePath: bakePath,
                GltfPath: gltfPath,
                FirstPoseChanged:
                    translationDelta > 0.00001f ||
                    rotationDelta > 0.00001f ||
                    scaleDelta > 0.00001f,
                FirstPoseTranslationDelta: translationDelta,
                FirstPoseRotationDelta: rotationDelta,
                FirstPoseScaleDelta: scaleDelta);
        }

        private static float FirstVectorDelta(float[][] values, float[] rest)
        {
            if (values == null || values.Length == 0 || values[0] == null || rest == null)
            {
                return 0f;
            }

            var first = values[0];
            var sum = 0f;
            for (var i = 0; i < Math.Min(first.Length, rest.Length); i++)
            {
                var delta = first[i] - rest[i];
                sum += delta * delta;
            }
            return (float)Math.Sqrt(sum);
        }

        private static float FirstRotationDelta(float[][] values, float[] rest)
        {
            if (values == null || values.Length == 0 || values[0] == null || values[0].Length < 4 || rest == null || rest.Length < 4)
            {
                return 0f;
            }

            var first = NormalizeQuaternion(values[0]);
            var normalizedRest = NormalizeQuaternion(rest);
            var dot = Math.Abs(
                first[0] * normalizedRest[0] +
                first[1] * normalizedRest[1] +
                first[2] * normalizedRest[2] +
                first[3] * normalizedRest[3]);
            return 1f - Math.Min(1f, dot);
        }

        private static float MaxDeltaFromFirst(float[][] values)
        {
            if (values == null || values.Length < 2)
            {
                return 0f;
            }

            var first = values[0];
            var max = 0f;
            for (var i = 1; i < values.Length; i++)
            {
                var current = values[i];
                if (current == null || first == null)
                {
                    continue;
                }

                var sum = 0f;
                for (var c = 0; c < Math.Min(first.Length, current.Length); c++)
                {
                    var delta = current[c] - first[c];
                    sum += delta * delta;
                }
                max = Math.Max(max, (float)Math.Sqrt(sum));
            }
            return max;
        }

        private static float MaxRotationDeltaFromFirst(float[][] values)
        {
            if (values == null || values.Length < 2 || values[0] == null || values[0].Length < 4)
            {
                return 0f;
            }

            var first = NormalizeQuaternion(values[0]);
            var max = 0f;
            for (var i = 1; i < values.Length; i++)
            {
                if (values[i] == null || values[i].Length < 4)
                {
                    continue;
                }
                var current = NormalizeQuaternion(values[i]);
                var dot = Math.Abs(
                    first[0] * current[0] +
                    first[1] * current[1] +
                    first[2] * current[2] +
                    first[3] * current[3]);
                max = Math.Max(max, 1f - Math.Min(1f, dot));
            }
            return max;
        }

        private static float[] NormalizeQuaternion(float[] value)
        {
            var length = (float)Math.Sqrt(
                value[0] * value[0] +
                value[1] * value[1] +
                value[2] * value[2] +
                value[3] * value[3]);
            if (length <= 0f)
            {
                return new[] { 0f, 0f, 0f, 1f };
            }
            return new[] { value[0] / length, value[1] / length, value[2] / length, value[3] / length };
        }

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

        private static float[] Inverse(float[] value)
        {
            var normalized = NormalizeQuaternion(value);
            return new[] { -normalized[0], -normalized[1], -normalized[2], normalized[3] };
        }

        private static void WriteChannel(JObject gltf, JObject animation, List<byte> bufferBytes, int nodeIndex, string path, float[] times, float[][] values)
        {
            if (values.Length != times.Length || values.Length == 0)
            {
                return;
            }
            var inputAccessor = WriteAccessor(gltf, bufferBytes, times.Select(x => new[] { x }).ToArray(), "SCALAR");
            var outputAccessor = WriteAccessor(gltf, bufferBytes, values, path == "rotation" ? "VEC4" : "VEC3");
            var samplers = (JArray)animation["samplers"];
            var channels = (JArray)animation["channels"];
            var samplerIndex = samplers.Count;
            samplers.Add(new JObject
            {
                ["input"] = inputAccessor,
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

        private static int WriteAccessor(JObject gltf, List<byte> bufferBytes, float[][] rows, string type)
        {
            Align(bufferBytes, 4);
            var byteOffset = bufferBytes.Count;
            foreach (var row in rows)
            {
                foreach (var value in row)
                {
                    bufferBytes.AddRange(BitConverter.GetBytes(value));
                }
            }
            var byteLength = bufferBytes.Count - byteOffset;
            var bufferViews = (JArray)(gltf["bufferViews"] ??= new JArray());
            var accessors = (JArray)(gltf["accessors"] ??= new JArray());
            var bufferViewIndex = bufferViews.Count;
            bufferViews.Add(new JObject
            {
                ["buffer"] = 0,
                ["byteOffset"] = byteOffset,
                ["byteLength"] = byteLength,
            });
            var components = rows[0].Length;
            var accessor = new JObject
            {
                ["bufferView"] = bufferViewIndex,
                ["componentType"] = 5126,
                ["count"] = rows.Length,
                ["type"] = type,
                ["min"] = new JArray(Enumerable.Range(0, components).Select(i => rows.Min(x => x[i]))),
                ["max"] = new JArray(Enumerable.Range(0, components).Select(i => rows.Max(x => x[i]))),
            };
            var accessorIndex = accessors.Count;
            accessors.Add(accessor);
            return accessorIndex;
        }

        private static void Align(List<byte> bytes, int alignment)
        {
            while (bytes.Count % alignment != 0)
            {
                bytes.Add(0);
            }
        }

        private static string SafeName(string value)
        {
            var name = string.IsNullOrWhiteSpace(value) ? "baked" : value;
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        private sealed record SkinAnimationReport(
            int Nodes,
            int Skins,
            int SkinJoints,
            int AnimationTargets,
            int TargetNotJointCount,
            int HierarchyParentTargetCount,
            int InvalidTargetCount,
            int JointNotTargetCount,
            MeshSkinNode[] MeshSkinNodes,
            NodeRef[] HierarchyParentTargets,
            NodeRef[] InvalidTargets,
            NodeRef[] JointNotTargetSample,
            string Rule
        );

        private sealed record MeshSkinNode(int Node, string Name, int Mesh, int Skin);

        private sealed record NodeRef(int Node, string Path);

        private sealed record TrackMotionReport(
            string BakePath,
            string GltfPath,
            bool FrameVarying,
            int FrameVaryingChannelCount,
            float MaxTranslationDelta,
            float MaxRotationDelta,
            float MaxScaleDelta,
            bool FirstPoseChanged = false,
            float FirstPoseTranslationDelta = 0f,
            float FirstPoseRotationDelta = 0f,
            float FirstPoseScaleDelta = 0f);

        private sealed record FirstPoseMotionReport(
            string BakePath,
            string GltfPath,
            bool FirstPoseChanged,
            float FirstPoseTranslationDelta,
            float FirstPoseRotationDelta,
            float FirstPoseScaleDelta);

        private sealed record AvatarTrustReport(
            bool TrustedProductionBake,
            string Source,
            string Message);
    }
}
