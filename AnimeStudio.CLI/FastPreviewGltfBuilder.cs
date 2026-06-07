using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AnimeStudio.CLI
{
    internal static class FastPreviewGltfBuilder
    {
        public static bool TryGenerate(JObject modelEntry, JObject animationEntry, string animationName, string outputDirectory, out string gltfPath, out string message)
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
                message = "动画 sidecar 没有可用 decoded 曲线。";
                return false;
            }

            var curves = LoadCurves(decoded);
            if (curves.Count == 0)
            {
                message = "decoded 曲线里没有 Transform TRS 轨道。";
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

            if (matched == 0)
            {
                message = "动画曲线没有匹配到 glTF 节点。";
                return false;
            }

            writer.Flush(gltf, output, $"{SafeName(animationName)}.fastpreview.bin");
            var animations = gltf["animations"] as JArray;
            if (animations == null)
            {
                animations = new JArray();
                gltf["animations"] = animations;
            }
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
                        ["coordinateConversion"] = "Unity local TRS converted to glTF basis: position.x=-x, rotation.y/z=-y/-z",
                        ["note"] = "快速预览直接使用已导出 glTF 和 animation_asset.json decoded TRS 曲线，不重新加载 Unity bundle。",
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
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
            }
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target) ?? destination);
                File.Copy(file, target, overwrite: true);
            }
        }

        private static float F(JToken token)
        {
            return token == null ? 0 : token.Value<float>();
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
