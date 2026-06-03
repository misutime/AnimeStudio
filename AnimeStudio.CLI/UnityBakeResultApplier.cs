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
        public static void Apply(string requestOrResultPath, string outputGltfPath)
        {
            if (string.IsNullOrWhiteSpace(requestOrResultPath) || !File.Exists(requestOrResultPath))
            {
                Logger.Error($"Unity bake request/result not found: {requestOrResultPath}");
                return;
            }

            var input = JObject.Parse(File.ReadAllText(requestOrResultPath));
            var requestPath = IsRequest(input) ? requestOrResultPath : TryFindSiblingRequest(requestOrResultPath);
            if (requestPath == null || !File.Exists(requestPath))
            {
                Logger.Error("Unable to find unity_bake_request.json. Apply needs the request so it can locate the source glTF.");
                return;
            }

            var request = JObject.Parse(File.ReadAllText(requestPath));
            var resultPath = IsRequest(input)
                ? (string)request["outputJson"]
                : requestOrResultPath;
            if (string.IsNullOrWhiteSpace(resultPath) || !File.Exists(resultPath))
            {
                Logger.Error($"Unity bake result not found: {resultPath}");
                return;
            }

            var result = JObject.Parse(File.ReadAllText(resultPath));
            if (!string.Equals((string)result["status"], "ok", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Error($"Unity bake result is not ok: {(string)result["message"]}");
                return;
            }

            var sourceGltf = (string)request["animeStudioAssets"]?["model"]?["gltf"];
            if (string.IsNullOrWhiteSpace(sourceGltf) || !File.Exists(sourceGltf))
            {
                Logger.Error($"Source glTF not found in request: {sourceGltf}");
                return;
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
                return;
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
                        ["sampleCount"] = result["sampleCount"],
                        ["changedTrackCount"] = result["changedTrackCount"],
                    }
                }
            };

            var writtenTracks = 0;
            var skippedTracks = new List<string>();
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

                WriteChannel(gltf, animation, bufferBytes, nodeIndex, "translation", times, ReadVec3(track["translations"]));
                WriteChannel(gltf, animation, bufferBytes, nodeIndex, "rotation", times, ReadVec4(track["rotations"]));
                WriteChannel(gltf, animation, bufferBytes, nodeIndex, "scale", times, ReadVec3(track["scales"]));
                writtenTracks++;
            }

            if (writtenTracks == 0)
            {
                Logger.Error("No baked tracks matched glTF nodes; inspect path mapping before trusting this output.");
                return;
            }

            // 预览文件只保留当前烘焙动画，避免 F3D/Blender 默认播放旧的表情或辅助动画，
            // 让用户打开文件时看到的就是这次选择的模型 + 动作。
            gltf["animations"] = new JArray(animation);
            ((JArray)(gltf["buffers"]))[0]["byteLength"] = bufferBytes.Count;
            File.WriteAllBytes(bufferFile, bufferBytes.ToArray());
            File.WriteAllText(outputGltfPath, JsonConvert.SerializeObject(gltf, Formatting.Indented));
            var skinReport = BuildSkinAnimationReport(gltf);

            var report = new
            {
                generatedAt = DateTime.UtcNow.ToString("O"),
                status = skippedTracks.Count == 0 && skinReport.TargetNotJointCount == 0 ? "ok" : "warning",
                sourceGltf,
                outputGltf = outputGltfPath,
                request = requestPath,
                result = resultPath,
                clipName,
                writtenTracks,
                skippedTrackCount = skippedTracks.Count,
                skippedTracks = skippedTracks.Take(128).ToArray(),
                skinAnimation = skinReport,
            };
            var reportPath = Path.Combine(outputDir, "unity_bake_apply_report.json");
            File.WriteAllText(reportPath, JsonConvert.SerializeObject(report, Formatting.Indented));
            Logger.Info($"Baked glTF preview: {outputGltfPath}");
            Logger.Info($"Baked apply report: {reportPath}");
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
            var targetNotJoint = targets
                .Where(x => !joints.Contains(x))
                .Select(x => new NodeRef(x, BuildNodePath(nodes, x)))
                .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var jointNotTarget = joints
                .Where(x => !targets.Contains(x))
                .Select(x => new NodeRef(x, BuildNodePath(nodes, x)))
                .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .Take(128)
                .ToArray();

            return new SkinAnimationReport(
                Nodes: nodes.Length,
                Skins: skins.Length,
                SkinJoints: joints.Count,
                AnimationTargets: targets.Count,
                TargetNotJointCount: targetNotJoint.Length,
                JointNotTargetCount: joints.Count - targets.Count(x => joints.Contains(x)),
                MeshSkinNodes: meshSkinNodes,
                TargetNotJoint: targetNotJoint,
                JointNotTargetSample: jointNotTarget,
                Rule: "UnityGLTF-style check: skin.joints should be exported from SkinnedMeshRenderer.bones, and baked animation channels should target the same bone node graph. Non-joint parent targets can still affect child joints through hierarchy, but they are reported for inspection."
            );
        }

        private static string BuildNodePath(JObject[] nodes, int index)
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

            var stack = new Stack<string>();
            int? current = index;
            while (current.HasValue)
            {
                stack.Push((string)nodes[current.Value]["name"] ?? $"node_{current.Value}");
                current = parents[current.Value];
            }
            return string.Join("/", stack);
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
                parts.RemoveAt(0);
            }
            return string.Join("/", parts);
        }

        private static float[][] ReadVec3(JToken values) =>
            values?.OfType<JObject>().Select(x => new[] { (float)x["x"], (float)x["y"], (float)x["z"] }).ToArray() ?? Array.Empty<float[]>();

        private static float[][] ReadVec4(JToken values) =>
            values?.OfType<JObject>().Select(x => new[] { (float)x["x"], (float)x["y"], (float)x["z"], (float)x["w"] }).ToArray() ?? Array.Empty<float[]>();

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
            int JointNotTargetCount,
            MeshSkinNode[] MeshSkinNodes,
            NodeRef[] TargetNotJoint,
            NodeRef[] JointNotTargetSample,
            string Rule
        );

        private sealed record MeshSkinNode(int Node, string Name, int Mesh, int Skin);

        private sealed record NodeRef(int Node, string Path);
    }
}
