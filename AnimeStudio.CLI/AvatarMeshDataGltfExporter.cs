using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AnimeStudio.CLI
{
    internal static class AvatarMeshDataGltfExporter
    {
        public static string Export(string jsonPath, string outputFolder)
        {
            if (!string.IsNullOrWhiteSpace(jsonPath) && Directory.Exists(jsonPath))
            {
                return ExportDirectory(jsonPath, outputFolder);
            }

            if (string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath))
            {
                throw new FileNotFoundException("AvatarMeshDataAsset TypeTree JSON not found.", jsonPath);
            }

            outputFolder = string.IsNullOrWhiteSpace(outputFolder)
                ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(jsonPath)) ?? Directory.GetCurrentDirectory(), "AvatarMeshDataGltf")
                : outputFolder;
            Directory.CreateDirectory(outputFolder);

            var sourceJson = JObject.Parse(File.ReadAllText(jsonPath));
            var mesh = ReadMesh(sourceJson, jsonPath);
            var safeName = FixFileName(mesh.MeshName);
            var binName = safeName + ".bin";
            var gltfName = safeName + ".gltf";
            var reportName = safeName + ".avatar_mesh_data_report.json";
            var binPath = Path.Combine(outputFolder, binName);
            var gltfPath = Path.Combine(outputFolder, gltfName);
            var reportPath = Path.Combine(outputFolder, reportName);

            var bufferViews = new JArray();
            var accessors = new JArray();
            var attributes = new JObject();
            using var stream = File.Create(binPath);

            var indexView = WriteUIntBufferView(stream, bufferViews, mesh.Indices.Select(x => checked((uint)x)), 34963);
            accessors.Add(new JObject
            {
                ["bufferView"] = indexView,
                ["componentType"] = 5125,
                ["count"] = mesh.Indices.Count,
                ["type"] = "SCALAR"
            });
            var indexAccessor = accessors.Count - 1;

            var positionView = WriteFloatBufferView(stream, bufferViews, mesh.Positions.SelectMany(ToFloatArray3), 34962);
            accessors.Add(new JObject
            {
                ["bufferView"] = positionView,
                ["componentType"] = 5126,
                ["count"] = mesh.Positions.Count,
                ["type"] = "VEC3",
                ["min"] = new JArray(mesh.Min.X, mesh.Min.Y, mesh.Min.Z),
                ["max"] = new JArray(mesh.Max.X, mesh.Max.Y, mesh.Max.Z)
            });
            attributes["POSITION"] = accessors.Count - 1;

            if (mesh.Normals.Count == mesh.Positions.Count)
            {
                var normalView = WriteFloatBufferView(stream, bufferViews, mesh.Normals.SelectMany(ToFloatArray3), 34962);
                accessors.Add(new JObject
                {
                    ["bufferView"] = normalView,
                    ["componentType"] = 5126,
                    ["count"] = mesh.Normals.Count,
                    ["type"] = "VEC3"
                });
                attributes["NORMAL"] = accessors.Count - 1;
            }

            if (mesh.Tangents.Count == mesh.Positions.Count)
            {
                var tangentView = WriteFloatBufferView(stream, bufferViews, mesh.Tangents.SelectMany(ToFloatArray4), 34962);
                accessors.Add(new JObject
                {
                    ["bufferView"] = tangentView,
                    ["componentType"] = 5126,
                    ["count"] = mesh.Tangents.Count,
                    ["type"] = "VEC4"
                });
                attributes["TANGENT"] = accessors.Count - 1;
            }

            if (mesh.Uvs.Count == mesh.Positions.Count)
            {
                var uvView = WriteFloatBufferView(stream, bufferViews, mesh.Uvs.SelectMany(ToFloatArray2), 34962);
                accessors.Add(new JObject
                {
                    ["bufferView"] = uvView,
                    ["componentType"] = 5126,
                    ["count"] = mesh.Uvs.Count,
                    ["type"] = "VEC2"
                });
                attributes["TEXCOORD_0"] = accessors.Count - 1;
            }

            var gltf = new JObject
            {
                ["asset"] = new JObject
                {
                    ["version"] = "2.0",
                    ["generator"] = "AnimeStudio AvatarMeshDataAsset diagnostic exporter"
                },
                ["scene"] = 0,
                ["scenes"] = new JArray(new JObject { ["nodes"] = new JArray(0) }),
                ["nodes"] = new JArray(new JObject { ["name"] = mesh.MeshName, ["mesh"] = 0 }),
                ["meshes"] = new JArray(new JObject
                {
                    ["name"] = mesh.MeshName,
                    ["primitives"] = new JArray(new JObject
                    {
                        ["attributes"] = attributes,
                        ["indices"] = indexAccessor,
                        ["material"] = 0,
                        ["mode"] = 4
                    }),
                    ["extras"] = new JObject
                    {
                        ["source"] = Path.GetFullPath(jsonPath),
                        ["sourceType"] = "AvatarMeshDataAsset",
                        ["diagnosticOnly"] = true,
                        ["unityCoordinateSystemPreserved"] = true,
                        ["note"] = "Naraka 自定义 MonoBehaviour 网格诊断导出；尚未绑定 prefab 模块关系、材质或骨骼。"
                    }
                }),
                ["materials"] = new JArray(new JObject
                {
                    ["name"] = "avatar_mesh_data_diagnostic_gray",
                    ["doubleSided"] = true,
                    ["pbrMetallicRoughness"] = new JObject
                    {
                        ["baseColorFactor"] = new JArray(0.55, 0.55, 0.55, 1.0),
                        ["metallicFactor"] = 0.0,
                        ["roughnessFactor"] = 0.85
                    }
                }),
                ["buffers"] = new JArray(new JObject
                {
                    ["uri"] = Uri.EscapeDataString(binName),
                    ["byteLength"] = stream.Length
                }),
                ["bufferViews"] = bufferViews,
                ["accessors"] = accessors
            };

            File.WriteAllText(gltfPath, gltf.ToString(Formatting.Indented));
            var report = new JObject
            {
                ["status"] = "ok",
                ["kind"] = "AvatarMeshDataAssetGltf",
                ["sourceJson"] = Path.GetFullPath(jsonPath),
                ["output"] = gltfPath,
                ["meshName"] = mesh.MeshName,
                ["vertexCount"] = mesh.Positions.Count,
                ["indexCount"] = mesh.Indices.Count,
                ["triangleCount"] = mesh.Indices.Count / 3,
                ["hasNormals"] = mesh.Normals.Count == mesh.Positions.Count,
                ["hasTangents"] = mesh.Tangents.Count == mesh.Positions.Count,
                ["hasUv0"] = mesh.Uvs.Count == mesh.Positions.Count,
                ["bboxMin"] = new JArray(mesh.Min.X, mesh.Min.Y, mesh.Min.Z),
                ["bboxMax"] = new JArray(mesh.Max.X, mesh.Max.Y, mesh.Max.Z),
                ["warnings"] = new JArray(mesh.Warnings),
                ["rule"] = "只从 AvatarMeshDataAsset TypeTree JSON 里的确定性字段生成静态诊断 glTF；不猜 prefab 模块关系、材质或骨骼。"
            };
            File.WriteAllText(reportPath, report.ToString(Formatting.Indented));
            Logger.Info($"Exported AvatarMeshDataAsset diagnostic glTF: {gltfPath}");
            Logger.Info($"Wrote AvatarMeshDataAsset diagnostic report: {reportPath}");
            return gltfPath;
        }

        private static string ExportDirectory(string jsonFolder, string outputFolder)
        {
            var jsonFiles = Directory.GetFiles(jsonFolder, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (jsonFiles.Length == 0)
            {
                throw new FileNotFoundException("AvatarMeshDataAsset TypeTree JSON folder has no *.json files.", jsonFolder);
            }

            outputFolder = string.IsNullOrWhiteSpace(outputFolder)
                ? Path.Combine(Path.GetFullPath(jsonFolder), "AvatarMeshDataGltf")
                : outputFolder;
            Directory.CreateDirectory(outputFolder);

            var meshes = jsonFiles
                .Select(file => ReadMesh(JObject.Parse(File.ReadAllText(file)), file))
                .ToList();
            var safeName = FixFileName(Path.GetFileName(Path.GetFullPath(jsonFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            var binName = safeName + ".bin";
            var gltfName = safeName + ".gltf";
            var reportName = safeName + ".avatar_mesh_data_report.json";
            var binPath = Path.Combine(outputFolder, binName);
            var gltfPath = Path.Combine(outputFolder, gltfName);
            var reportPath = Path.Combine(outputFolder, reportName);

            var bufferViews = new JArray();
            var accessors = new JArray();
            var gltfMeshes = new JArray();
            var nodes = new JArray();
            using var stream = File.Create(binPath);
            for (var i = 0; i < meshes.Count; i++)
            {
                gltfMeshes.Add(WriteMeshObject(stream, bufferViews, accessors, meshes[i], Path.GetFullPath(jsonFiles[i])));
                nodes.Add(new JObject
                {
                    ["name"] = meshes[i].MeshName,
                    ["mesh"] = i
                });
            }

            var totalBounds = CalculateBounds(meshes);
            var gltf = new JObject
            {
                ["asset"] = new JObject
                {
                    ["version"] = "2.0",
                    ["generator"] = "AnimeStudio AvatarMeshDataAsset diagnostic set exporter"
                },
                ["scene"] = 0,
                ["scenes"] = new JArray(new JObject { ["nodes"] = new JArray(Enumerable.Range(0, nodes.Count)) }),
                ["nodes"] = nodes,
                ["meshes"] = gltfMeshes,
                ["materials"] = new JArray(CreateDiagnosticMaterial()),
                ["buffers"] = new JArray(new JObject
                {
                    ["uri"] = Uri.EscapeDataString(binName),
                    ["byteLength"] = stream.Length
                }),
                ["bufferViews"] = bufferViews,
                ["accessors"] = accessors,
                ["extras"] = new JObject
                {
                    ["sourceDirectory"] = Path.GetFullPath(jsonFolder),
                    ["sourceType"] = "AvatarMeshDataAssetSet",
                    ["diagnosticOnly"] = true,
                    ["unityCoordinateSystemPreserved"] = true,
                    ["note"] = "Naraka 自定义 MonoBehaviour 网格集合诊断导出；可能包含 LOD 变体，尚未绑定 prefab 模块关系、材质槽或骨骼 skin。"
                }
            };

            File.WriteAllText(gltfPath, gltf.ToString(Formatting.Indented));
            var report = new JObject
            {
                ["status"] = "ok",
                ["kind"] = "AvatarMeshDataAssetGltfSet",
                ["sourceDirectory"] = Path.GetFullPath(jsonFolder),
                ["output"] = gltfPath,
                ["meshCount"] = meshes.Count,
                ["vertexCount"] = meshes.Sum(x => x.Positions.Count),
                ["indexCount"] = meshes.Sum(x => x.Indices.Count),
                ["triangleCount"] = meshes.Sum(x => x.Indices.Count / 3),
                ["containsLodVariants"] = meshes.Any(x => Regex.IsMatch(x.MeshName ?? string.Empty, @"_L\d+_", RegexOptions.IgnoreCase)),
                ["bboxMin"] = new JArray(totalBounds.Min.X, totalBounds.Min.Y, totalBounds.Min.Z),
                ["bboxMax"] = new JArray(totalBounds.Max.X, totalBounds.Max.Y, totalBounds.Max.Z),
                ["meshes"] = new JArray(meshes.Select((mesh, index) => ToReportJson(mesh, jsonFiles[index]))),
                ["rule"] = "只从目录内 AvatarMeshDataAsset TypeTree JSON 的确定性字段生成静态诊断 glTF；不猜 prefab 模块关系、Renderer 材质槽、骨骼或 skin。"
            };
            File.WriteAllText(reportPath, report.ToString(Formatting.Indented));
            Logger.Info($"Exported AvatarMeshDataAsset diagnostic glTF set: {gltfPath}");
            Logger.Info($"Wrote AvatarMeshDataAsset diagnostic report: {reportPath}");
            return gltfPath;
        }

        private static JObject WriteMeshObject(Stream stream, JArray bufferViews, JArray accessors, AvatarMeshData mesh, string source)
        {
            var attributes = new JObject();
            var indexView = WriteUIntBufferView(stream, bufferViews, mesh.Indices.Select(x => checked((uint)x)), 34963);
            accessors.Add(new JObject
            {
                ["bufferView"] = indexView,
                ["componentType"] = 5125,
                ["count"] = mesh.Indices.Count,
                ["type"] = "SCALAR"
            });
            var indexAccessor = accessors.Count - 1;

            var positionView = WriteFloatBufferView(stream, bufferViews, mesh.Positions.SelectMany(ToFloatArray3), 34962);
            accessors.Add(new JObject
            {
                ["bufferView"] = positionView,
                ["componentType"] = 5126,
                ["count"] = mesh.Positions.Count,
                ["type"] = "VEC3",
                ["min"] = new JArray(mesh.Min.X, mesh.Min.Y, mesh.Min.Z),
                ["max"] = new JArray(mesh.Max.X, mesh.Max.Y, mesh.Max.Z)
            });
            attributes["POSITION"] = accessors.Count - 1;

            if (mesh.Normals.Count == mesh.Positions.Count)
            {
                var normalView = WriteFloatBufferView(stream, bufferViews, mesh.Normals.SelectMany(ToFloatArray3), 34962);
                accessors.Add(new JObject
                {
                    ["bufferView"] = normalView,
                    ["componentType"] = 5126,
                    ["count"] = mesh.Normals.Count,
                    ["type"] = "VEC3"
                });
                attributes["NORMAL"] = accessors.Count - 1;
            }

            if (mesh.Tangents.Count == mesh.Positions.Count)
            {
                var tangentView = WriteFloatBufferView(stream, bufferViews, mesh.Tangents.SelectMany(ToFloatArray4), 34962);
                accessors.Add(new JObject
                {
                    ["bufferView"] = tangentView,
                    ["componentType"] = 5126,
                    ["count"] = mesh.Tangents.Count,
                    ["type"] = "VEC4"
                });
                attributes["TANGENT"] = accessors.Count - 1;
            }

            if (mesh.Uvs.Count == mesh.Positions.Count)
            {
                var uvView = WriteFloatBufferView(stream, bufferViews, mesh.Uvs.SelectMany(ToFloatArray2), 34962);
                accessors.Add(new JObject
                {
                    ["bufferView"] = uvView,
                    ["componentType"] = 5126,
                    ["count"] = mesh.Uvs.Count,
                    ["type"] = "VEC2"
                });
                attributes["TEXCOORD_0"] = accessors.Count - 1;
            }

            return new JObject
            {
                ["name"] = mesh.MeshName,
                ["primitives"] = new JArray(new JObject
                {
                    ["attributes"] = attributes,
                    ["indices"] = indexAccessor,
                    ["material"] = 0,
                    ["mode"] = 4
                }),
                ["extras"] = new JObject
                {
                    ["source"] = source,
                    ["sourceType"] = "AvatarMeshDataAsset",
                    ["diagnosticOnly"] = true,
                    ["unityCoordinateSystemPreserved"] = true,
                    ["note"] = "Naraka 自定义 MonoBehaviour 网格诊断导出；尚未绑定 prefab 模块关系、材质或骨骼。"
                }
            };
        }

        private static JObject CreateDiagnosticMaterial()
        {
            return new JObject
            {
                ["name"] = "avatar_mesh_data_diagnostic_gray",
                ["doubleSided"] = true,
                ["pbrMetallicRoughness"] = new JObject
                {
                    ["baseColorFactor"] = new JArray(0.55, 0.55, 0.55, 1.0),
                    ["metallicFactor"] = 0.0,
                    ["roughnessFactor"] = 0.85
                }
            };
        }

        private static JObject ToReportJson(AvatarMeshData mesh, string sourceJson)
        {
            return new JObject
            {
                ["sourceJson"] = Path.GetFullPath(sourceJson),
                ["meshName"] = mesh.MeshName,
                ["vertexCount"] = mesh.Positions.Count,
                ["indexCount"] = mesh.Indices.Count,
                ["triangleCount"] = mesh.Indices.Count / 3,
                ["hasNormals"] = mesh.Normals.Count == mesh.Positions.Count,
                ["hasTangents"] = mesh.Tangents.Count == mesh.Positions.Count,
                ["hasUv0"] = mesh.Uvs.Count == mesh.Positions.Count,
                ["bboxMin"] = new JArray(mesh.Min.X, mesh.Min.Y, mesh.Min.Z),
                ["bboxMax"] = new JArray(mesh.Max.X, mesh.Max.Y, mesh.Max.Z),
                ["warnings"] = new JArray(mesh.Warnings)
            };
        }

        private static (Vector3Value Min, Vector3Value Max) CalculateBounds(IEnumerable<AvatarMeshData> meshes)
        {
            var list = meshes.ToList();
            return (
                new Vector3Value(
                    list.Min(x => x.Min.X),
                    list.Min(x => x.Min.Y),
                    list.Min(x => x.Min.Z)),
                new Vector3Value(
                    list.Max(x => x.Max.X),
                    list.Max(x => x.Max.Y),
                    list.Max(x => x.Max.Z)));
        }

        private static AvatarMeshData ReadMesh(JObject json, string source)
        {
            var meshName = (string)json["m_MeshName"];
            if (string.IsNullOrWhiteSpace(meshName))
            {
                meshName = (string)json["m_Name"] ?? Path.GetFileNameWithoutExtension(source);
            }

            var vertices = json["m_VertexData"] as JArray;
            var indices = json["m_Indices"] as JArray;
            if (vertices == null || vertices.Count == 0)
            {
                throw new InvalidDataException("AvatarMeshDataAsset JSON missing m_VertexData.");
            }
            if (indices == null || indices.Count == 0)
            {
                throw new InvalidDataException("AvatarMeshDataAsset JSON missing m_Indices.");
            }

            var result = new AvatarMeshData(meshName);
            foreach (var vertex in vertices.OfType<JObject>())
            {
                result.Positions.Add(ReadVector3(vertex["position"], "position"));
                if (vertex["normal"] != null)
                {
                    result.Normals.Add(ReadVector3(vertex["normal"], "normal"));
                }
                if (vertex["tangent"] != null)
                {
                    result.Tangents.Add(ReadVector4(vertex["tangent"], "tangent"));
                }
            }

            foreach (var token in indices)
            {
                var index = checked((int)token.Value<long>());
                if (index < 0 || index >= result.Positions.Count)
                {
                    throw new InvalidDataException($"AvatarMeshDataAsset index out of range: {index}, vertexCount={result.Positions.Count}.");
                }
                result.Indices.Add(index);
            }

            if (result.Indices.Count % 3 != 0)
            {
                result.Warnings.Add("indexCountNotMultipleOf3");
            }

            result.Uvs.AddRange(ReadUv0(json, result.Positions.Count));
            if (result.Uvs.Count != 0 && result.Uvs.Count != result.Positions.Count)
            {
                result.Warnings.Add("uvCountMismatch");
            }
            if (result.Normals.Count != 0 && result.Normals.Count != result.Positions.Count)
            {
                result.Warnings.Add("normalCountMismatch");
            }
            if (result.Tangents.Count != 0 && result.Tangents.Count != result.Positions.Count)
            {
                result.Warnings.Add("tangentCountMismatch");
            }

            result.CalculateBounds();
            return result;
        }

        private static IEnumerable<Vector2Value> ReadUv0(JObject json, int vertexCount)
        {
            var hairUvSet = json["m_HairUVSetData"] as JArray;
            if (hairUvSet != null && hairUvSet.Count == vertexCount)
            {
                foreach (var item in hairUvSet.OfType<JObject>())
                {
                    var uv = item["uv0"];
                    if (uv == null)
                    {
                        yield break;
                    }
                    yield return new Vector2Value(ReadFloat(uv, "x"), 1f - ReadFloat(uv, "y"));
                }
                yield break;
            }

            var uvData = json["m_UVData"] as JArray;
            if (uvData != null && uvData.Count == vertexCount)
            {
                foreach (var item in uvData)
                {
                    yield return new Vector2Value(ReadFloat(item, "x"), 1f - ReadFloat(item, "y"));
                }
            }
        }

        private static Vector3Value ReadVector3(JToken token, string field)
        {
            if (token == null)
            {
                throw new InvalidDataException($"AvatarMeshDataAsset vertex missing {field}.");
            }
            return new Vector3Value(ReadFloat(token, "x"), ReadFloat(token, "y"), ReadFloat(token, "z"));
        }

        private static Vector4Value ReadVector4(JToken token, string field)
        {
            if (token == null)
            {
                throw new InvalidDataException($"AvatarMeshDataAsset vertex missing {field}.");
            }
            return new Vector4Value(ReadFloat(token, "x"), ReadFloat(token, "y"), ReadFloat(token, "z"), ReadFloat(token, "w"));
        }

        private static float ReadFloat(JToken token, string name)
        {
            var value = token?[name];
            if (value == null)
            {
                throw new InvalidDataException($"AvatarMeshDataAsset field missing float {name}.");
            }
            return value.Value<float>();
        }

        private static int WriteFloatBufferView(Stream stream, JArray bufferViews, IEnumerable<float> values, int target)
        {
            Align4(stream);
            var offset = stream.Length;
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            foreach (var value in values)
            {
                writer.Write(value);
            }
            writer.Flush();
            return AddBufferView(bufferViews, offset, stream.Length - offset, target);
        }

        private static int WriteUIntBufferView(Stream stream, JArray bufferViews, IEnumerable<uint> values, int target)
        {
            Align4(stream);
            var offset = stream.Length;
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            foreach (var value in values)
            {
                writer.Write(value);
            }
            writer.Flush();
            return AddBufferView(bufferViews, offset, stream.Length - offset, target);
        }

        private static int AddBufferView(JArray bufferViews, long offset, long length, int target)
        {
            bufferViews.Add(new JObject
            {
                ["buffer"] = 0,
                ["byteOffset"] = offset,
                ["byteLength"] = length,
                ["target"] = target
            });
            return bufferViews.Count - 1;
        }

        private static void Align4(Stream stream)
        {
            while (stream.Length % 4 != 0)
            {
                stream.WriteByte(0);
            }
        }

        private static IEnumerable<float> ToFloatArray2(Vector2Value value)
        {
            yield return value.X;
            yield return value.Y;
        }

        private static IEnumerable<float> ToFloatArray3(Vector3Value value)
        {
            yield return value.X;
            yield return value.Y;
            yield return value.Z;
        }

        private static IEnumerable<float> ToFloatArray4(Vector4Value value)
        {
            yield return value.X;
            yield return value.Y;
            yield return value.Z;
            yield return value.W;
        }

        private static string FixFileName(string value)
        {
            value = string.IsNullOrWhiteSpace(value) ? "avatar_mesh_data" : value.Trim();
            value = Path.GetInvalidFileNameChars().Aggregate(value, (current, c) => current.Replace(c, '_'));
            value = Regex.Replace(value, @"\s+", "_");
            return value.Length == 0 ? "avatar_mesh_data" : value;
        }

        private sealed class AvatarMeshData
        {
            public AvatarMeshData(string meshName)
            {
                MeshName = meshName;
            }

            public string MeshName { get; }
            public List<int> Indices { get; } = new();
            public List<Vector3Value> Positions { get; } = new();
            public List<Vector3Value> Normals { get; } = new();
            public List<Vector4Value> Tangents { get; } = new();
            public List<Vector2Value> Uvs { get; } = new();
            public List<string> Warnings { get; } = new();
            public Vector3Value Min { get; private set; }
            public Vector3Value Max { get; private set; }

            public void CalculateBounds()
            {
                Min = new Vector3Value(
                    Positions.Min(x => x.X),
                    Positions.Min(x => x.Y),
                    Positions.Min(x => x.Z));
                Max = new Vector3Value(
                    Positions.Max(x => x.X),
                    Positions.Max(x => x.Y),
                    Positions.Max(x => x.Z));
            }
        }

        private readonly record struct Vector2Value(float X, float Y);
        private readonly record struct Vector3Value(float X, float Y, float Z);
        private readonly record struct Vector4Value(float X, float Y, float Z, float W);
    }
}
