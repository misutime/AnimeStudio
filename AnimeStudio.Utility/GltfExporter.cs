using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AnimeStudio
{
    public static class Gltf
    {
        public sealed class ExportOptions
        {
            public string textureDirectory;
            public bool binary;
            public bool exportSkins = true;
            public bool exportAnimations = true;
            public string localTextureDirectoryName;
        }

        public static class Exporter
        {
            public static void Export(string path, IImported imported, ExportOptions options)
            {
                new GltfExporter(path, imported, options).Export();
            }
        }
    }

    internal sealed class GltfExporter
    {
        private const int Float = 5126;
        private const int UInt = 5125;
        private const int UShort = 5123;
        private readonly string _path;
        private readonly string _directory;
        private readonly string _binName;
        private readonly string _localTextureDirectory;
        private readonly IImported _imported;
        private readonly Gltf.ExportOptions _options;
        private readonly MemoryStream _bin = new MemoryStream();
        private readonly BinaryWriter _writer;
        private readonly Dictionary<ImportedFrame, int> _nodeMap = new Dictionary<ImportedFrame, int>();
        private readonly Dictionary<string, int> _pathNodeMap = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _meshMap = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _materialMap = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _textureMap = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _skinMap = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<Dictionary<string, object>> _nodes = new List<Dictionary<string, object>>();
        private readonly List<Dictionary<string, object>> _meshes = new List<Dictionary<string, object>>();
        private readonly List<Dictionary<string, object>> _materials = new List<Dictionary<string, object>>();
        private readonly List<Dictionary<string, object>> _images = new List<Dictionary<string, object>>();
        private readonly List<Dictionary<string, object>> _textures = new List<Dictionary<string, object>>();
        private readonly List<Dictionary<string, object>> _skins = new List<Dictionary<string, object>>();
        private readonly List<Dictionary<string, object>> _animations = new List<Dictionary<string, object>>();
        private readonly List<Dictionary<string, object>> _bufferViews = new List<Dictionary<string, object>>();
        private readonly List<Dictionary<string, object>> _accessors = new List<Dictionary<string, object>>();

        public GltfExporter(string path, IImported imported, Gltf.ExportOptions options)
        {
            _path = path;
            _directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory();
            _binName = Path.GetFileNameWithoutExtension(path) + ".bin";
            _imported = imported;
            _options = options ?? new Gltf.ExportOptions();
            _localTextureDirectory = string.IsNullOrWhiteSpace(_options.localTextureDirectoryName)
                ? null
                : Path.Combine(_directory, _options.localTextureDirectoryName);
            _writer = new BinaryWriter(_bin, Encoding.UTF8, leaveOpen: true);
        }

        public void Export()
        {
            Directory.CreateDirectory(_directory);
            BuildNodeTree(_imported.RootFrame, null);
            BuildMeshesAndSkins();
            BuildAnimations();
            Align(4);

            var gltf = BuildDocument();
            if (_options.binary || Path.GetExtension(_path).Equals(".glb", StringComparison.OrdinalIgnoreCase))
            {
                WriteGlb(gltf);
            }
            else
            {
                File.WriteAllBytes(Path.Combine(_directory, _binName), _bin.ToArray());
                WriteJson(_path, gltf);
            }
        }

        private int BuildNodeTree(ImportedFrame frame, List<int> rootNodes)
        {
            var node = new Dictionary<string, object>
            {
                ["name"] = frame.Name ?? "Node",
            };
            if (frame.LocalPosition != Vector3.Zero)
            {
                node["translation"] = new[] { frame.LocalPosition.X, frame.LocalPosition.Y, frame.LocalPosition.Z };
            }
            if (frame.LocalRotation != Quaternion.Zero)
            {
                node["rotation"] = new[] { frame.LocalRotation.X, frame.LocalRotation.Y, frame.LocalRotation.Z, frame.LocalRotation.W };
            }
            if (frame.LocalScale != Vector3.One)
            {
                node["scale"] = new[] { frame.LocalScale.X, frame.LocalScale.Y, frame.LocalScale.Z };
            }

            var index = _nodes.Count;
            _nodes.Add(node);
            _nodeMap[frame] = index;
            _pathNodeMap[frame.Path] = index;

            if (rootNodes != null)
            {
                rootNodes.Add(index);
            }

            var children = new List<int>();
            for (var i = 0; i < frame.Count; i++)
            {
                children.Add(BuildNodeTree(frame[i], null));
            }
            if (children.Count > 0)
            {
                node["children"] = children;
            }
            return index;
        }

        private void BuildMeshesAndSkins()
        {
            if (_imported.MeshList == null)
            {
                return;
            }

            foreach (var mesh in _imported.MeshList)
            {
                if (!_pathNodeMap.TryGetValue(mesh.Path, out var nodeIndex))
                {
                    continue;
                }

                var meshIndex = BuildMesh(mesh);
                _nodes[nodeIndex]["mesh"] = meshIndex;

                if (_options.exportSkins && mesh.BoneList?.Count > 0)
                {
                    var skinIndex = BuildSkin(mesh);
                    if (skinIndex >= 0)
                    {
                        _nodes[nodeIndex]["skin"] = skinIndex;
                    }
                }
            }
        }

        private int BuildMesh(ImportedMesh mesh)
        {
            if (_meshMap.TryGetValue(mesh.Path, out var existing))
            {
                return existing;
            }

            var vertexCount = mesh.VertexList.Count;
            var attributes = new Dictionary<string, object>
            {
                ["POSITION"] = WriteVec3Accessor(mesh.VertexList.Select(x => x.Vertex), true),
            };

            if (mesh.hasNormal)
            {
                attributes["NORMAL"] = WriteVec3Accessor(mesh.VertexList.Select(x => x.Normal), false);
            }

            for (var uv = 0; uv < mesh.hasUV?.Length; uv++)
            {
                if (mesh.hasUV[uv])
                {
                    attributes[$"TEXCOORD_{uv}"] = WriteVec2Accessor(mesh.VertexList.Select(x =>
                    {
                        var value = x.UV?[uv];
                        return value == null || value.Length < 2
                            ? new Vector2()
                            : new Vector2(value[0], 1.0f - value[1]);
                    }));
                }
            }

            if (mesh.hasTangent)
            {
                attributes["TANGENT"] = WriteVec4Accessor(mesh.VertexList.Select(x => x.Tangent));
            }

            if (mesh.hasColor)
            {
                attributes["COLOR_0"] = WriteColorAccessor(mesh.VertexList.Select(x => x.Color));
            }

            if (mesh.BoneList?.Count > 0 && mesh.VertexList.Any(x => x.BoneIndices != null && x.Weights != null))
            {
                attributes["JOINTS_0"] = WriteJointsAccessor(mesh.VertexList);
                attributes["WEIGHTS_0"] = WriteWeightsAccessor(mesh.VertexList);
            }

            var primitives = new List<Dictionary<string, object>>();
            foreach (var submesh in mesh.SubmeshList)
            {
                var indices = new List<uint>();
                foreach (var face in submesh.FaceList)
                {
                    if (face.VertexIndices == null || face.VertexIndices.Length < 3)
                    {
                        continue;
                    }
                    indices.Add((uint)(face.VertexIndices[0] + submesh.BaseVertex));
                    indices.Add((uint)(face.VertexIndices[1] + submesh.BaseVertex));
                    indices.Add((uint)(face.VertexIndices[2] + submesh.BaseVertex));
                }

                if (indices.Count == 0)
                {
                    continue;
                }

                var primitive = new Dictionary<string, object>
                {
                    ["attributes"] = attributes,
                    ["indices"] = WriteUIntAccessor(indices),
                    ["mode"] = 4,
                };

                var material = GetMaterialIndex(submesh.Material);
                if (material >= 0)
                {
                    primitive["material"] = material;
                }
                primitives.Add(primitive);
            }

            var gltfMesh = new Dictionary<string, object>
            {
                ["name"] = Path.GetFileName(mesh.Path),
                ["primitives"] = primitives,
            };
            var meshIndex = _meshes.Count;
            _meshes.Add(gltfMesh);
            _meshMap[mesh.Path] = meshIndex;
            return meshIndex;
        }

        private int BuildSkin(ImportedMesh mesh)
        {
            if (_skinMap.TryGetValue(mesh.Path, out var existing))
            {
                return existing;
            }

            var joints = new List<int>();
            var matrices = new List<Matrix4x4>();
            foreach (var bone in mesh.BoneList)
            {
                if (bone.Path == null || !_pathNodeMap.TryGetValue(bone.Path, out var jointNode))
                {
                    continue;
                }
                joints.Add(jointNode);
                matrices.Add(bone.Matrix);
            }

            if (joints.Count == 0)
            {
                return -1;
            }

            var skin = new Dictionary<string, object>
            {
                ["joints"] = joints,
                ["inverseBindMatrices"] = WriteMatrixAccessor(matrices),
            };
            var index = _skins.Count;
            _skins.Add(skin);
            _skinMap[mesh.Path] = index;
            return index;
        }

        private void BuildAnimations()
        {
            if (!_options.exportAnimations || _imported.AnimationList == null)
            {
                return;
            }

            foreach (var animation in _imported.AnimationList)
            {
                var samplers = new List<Dictionary<string, object>>();
                var channels = new List<Dictionary<string, object>>();
                foreach (var track in animation.TrackList)
                {
                    if (track.Path == null || !_pathNodeMap.TryGetValue(track.Path, out var nodeIndex))
                    {
                        continue;
                    }

                    AddAnimationChannel(track.Translations, "translation", nodeIndex, samplers, channels, WriteVec3Accessor);
                    AddAnimationChannel(track.Rotations, "rotation", nodeIndex, samplers, channels, WriteQuatAccessor);
                    AddAnimationChannel(track.Scalings, "scale", nodeIndex, samplers, channels, WriteVec3Accessor);
                }

                if (channels.Count > 0)
                {
                    _animations.Add(new Dictionary<string, object>
                    {
                        ["name"] = animation.Name ?? "Animation",
                        ["samplers"] = samplers,
                        ["channels"] = channels,
                    });
                }
            }
        }

        private void AddAnimationChannel<T>(
            List<ImportedKeyframe<T>> keys,
            string path,
            int nodeIndex,
            List<Dictionary<string, object>> samplers,
            List<Dictionary<string, object>> channels,
            Func<IEnumerable<T>, bool, int> outputWriter)
        {
            if (keys == null || keys.Count == 0)
            {
                return;
            }

            var input = WriteFloatAccessor(keys.Select(x => x.time), true);
            var output = outputWriter(keys.Select(x => x.value), false);
            var samplerIndex = samplers.Count;
            samplers.Add(new Dictionary<string, object>
            {
                ["input"] = input,
                ["output"] = output,
                ["interpolation"] = "LINEAR",
            });
            channels.Add(new Dictionary<string, object>
            {
                ["sampler"] = samplerIndex,
                ["target"] = new Dictionary<string, object>
                {
                    ["node"] = nodeIndex,
                    ["path"] = path,
                },
            });
        }

        private int GetMaterialIndex(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return -1;
            }
            if (_materialMap.TryGetValue(name, out var existing))
            {
                return existing;
            }

            var material = ImportedHelpers.FindMaterial(name, _imported.MaterialList);
            if (material == null)
            {
                return -1;
            }

            var pbr = new Dictionary<string, object>
            {
                ["baseColorFactor"] = new[] { material.Diffuse.R, material.Diffuse.G, material.Diffuse.B, material.Diffuse.A },
                ["metallicFactor"] = 0.0f,
                ["roughnessFactor"] = 0.8f,
            };
            Dictionary<string, object> normalTexture = null;
            List<Dictionary<string, object>> nonStandardTextures = null;

            foreach (var textureRef in material.Textures ?? Enumerable.Empty<ImportedMaterialTexture>())
            {
                var texture = ImportedHelpers.FindTexture(textureRef.Name, _imported.TextureList);
                if (texture == null)
                {
                    continue;
                }
                if (texture.IsReferenceOnly || !IsGltfSupportedImageName(texture.ExportName ?? texture.Name))
                {
                    nonStandardTextures ??= new List<Dictionary<string, object>>();
                    nonStandardTextures.Add(new Dictionary<string, object>
                    {
                        ["slot"] = textureRef.Dest,
                        ["name"] = texture.Name,
                        ["exportName"] = texture.ExportName ?? texture.Name,
                        ["format"] = texture.SourceTextureFormat,
                        ["width"] = texture.Width,
                        ["height"] = texture.Height,
                        ["mipCount"] = texture.MipCount,
                        ["sourcePathId"] = texture.SourcePathId,
                        ["sourceAssetPath"] = texture.SourceAssetPath,
                        ["sourceFileName"] = texture.SourceFileName,
                        ["unityVersion"] = texture.UnityVersion,
                        ["platform"] = texture.Platform,
                        ["rawDataSize"] = texture.RawDataSize,
                        ["referenceOnly"] = texture.IsReferenceOnly,
                    });
                }
                var textureIndex = GetTextureIndex(texture);
                if (textureIndex < 0)
                {
                    continue;
                }
                if (textureRef.Dest == 0)
                {
                    pbr["baseColorTexture"] = new Dictionary<string, object> { ["index"] = textureIndex };
                }
                else if (textureRef.Dest == 1 || textureRef.Dest == 3)
                {
                    normalTexture ??= new Dictionary<string, object> { ["index"] = textureIndex };
                }
            }

            var gltfMaterial = new Dictionary<string, object>
            {
                ["name"] = material.Name,
                ["pbrMetallicRoughness"] = pbr,
            };
            if (normalTexture != null)
            {
                gltfMaterial["normalTexture"] = normalTexture;
            }
            if (material.Diffuse.A < 0.999f || material.Transparency > 0)
            {
                gltfMaterial["alphaMode"] = "BLEND";
            }
            if (nonStandardTextures?.Count > 0)
            {
                gltfMaterial["extras"] = new Dictionary<string, object>
                {
                    ["unityTextures"] = nonStandardTextures,
                };
            }

            var index = _materials.Count;
            _materials.Add(gltfMaterial);
            _materialMap[name] = index;
            return index;
        }

        private int GetTextureIndex(ImportedTexture texture)
        {
            var exportName = texture.ExportName ?? texture.Name;
            if (_textureMap.TryGetValue(exportName, out var existing))
            {
                return existing;
            }

            var imagePath = ExportTexture(texture);
            if (imagePath == null)
            {
                return -1;
            }
            if (!IsGltfSupportedImagePath(imagePath))
            {
                return -1;
            }

            var imageIndex = _images.Count;
            _images.Add(new Dictionary<string, object>
            {
                ["uri"] = ToUri(Path.GetRelativePath(_directory, imagePath)),
            });

            var textureIndex = _textures.Count;
            _textures.Add(new Dictionary<string, object> { ["source"] = imageIndex });
            _textureMap[exportName] = textureIndex;
            return textureIndex;
        }

        private string ExportTexture(ImportedTexture texture)
        {
            if (texture.IsReferenceOnly)
            {
                return null;
            }

            var textureDirectory = string.IsNullOrWhiteSpace(_options.textureDirectory)
                ? _directory
                : _options.textureDirectory;
            Directory.CreateDirectory(textureDirectory);

            var name = GetSafeTextureFileName(Path.GetFileNameWithoutExtension(texture.ExportName ?? texture.Name));
            var ext = Path.GetExtension(texture.ExportName ?? texture.Name);
            if (string.IsNullOrEmpty(ext))
            {
                ext = ".png";
            }
            var path = Path.Combine(textureDirectory, $"{name}{ext.ToLowerInvariant()}");
            if (!File.Exists(path) && texture.Data != null)
            {
                File.WriteAllBytes(path, texture.Data);
                WriteTextureMetadata(path, texture);
            }
            if (!File.Exists(path))
            {
                return null;
            }

            return CreateLocalTextureLink(path, Path.GetFileName(path));
        }

        private string CreateLocalTextureLink(string sharedPath, string fileName)
        {
            if (_localTextureDirectory == null)
            {
                return sharedPath;
            }

            Directory.CreateDirectory(_localTextureDirectory);
            var linkPath = Path.Combine(_localTextureDirectory, fileName);
            if (string.Equals(Path.GetFullPath(sharedPath), Path.GetFullPath(linkPath), StringComparison.OrdinalIgnoreCase))
            {
                return sharedPath;
            }

            if (File.Exists(linkPath))
            {
                return linkPath;
            }

            if (!CreateHardLink(linkPath, sharedPath, IntPtr.Zero))
            {
                File.Copy(sharedPath, linkPath, false);
            }
            return linkPath;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        private static bool IsGltfSupportedImagePath(string path)
        {
            return IsGltfSupportedImageName(Path.GetFileName(path));
        }

        private static bool IsGltfSupportedImageName(string name)
        {
            var ext = Path.GetExtension(name);
            return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".ktx2", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
        }

        private static void WriteTextureMetadata(string path, ImportedTexture texture)
        {
            if (string.IsNullOrWhiteSpace(texture.SourceTextureFormat))
            {
                return;
            }

            var metadata = new Dictionary<string, object>
            {
                ["name"] = texture.Name,
                ["exportName"] = texture.ExportName ?? texture.Name,
                ["format"] = texture.SourceTextureFormat,
                ["width"] = texture.Width,
                ["height"] = texture.Height,
                ["mipCount"] = texture.MipCount,
                ["sourcePathId"] = texture.SourcePathId,
                ["sourceAssetPath"] = texture.SourceAssetPath,
                ["sourceFileName"] = texture.SourceFileName,
                ["unityVersion"] = texture.UnityVersion,
                ["platform"] = texture.Platform,
                ["rawDataSize"] = texture.RawDataSize,
                ["dataFile"] = Path.GetFileName(path),
            };
            File.WriteAllText(path + ".json", JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        }

        private int WriteVec2Accessor(IEnumerable<Vector2> values)
        {
            var list = values.ToList();
            var offset = BeginWrite();
            foreach (var value in list)
            {
                _writer.Write(value.X);
                _writer.Write(value.Y);
            }
            return AddAccessor(offset, list.Count * 8, Float, "VEC2", list.Count);
        }

        private int WriteVec3Accessor(IEnumerable<Vector3> values, bool bounds)
        {
            var list = values.ToList();
            var offset = BeginWrite();
            foreach (var value in list)
            {
                _writer.Write(value.X);
                _writer.Write(value.Y);
                _writer.Write(value.Z);
            }
            var minMax = bounds && list.Count > 0
                ? (min: new[] { list.Min(x => x.X), list.Min(x => x.Y), list.Min(x => x.Z) },
                   max: new[] { list.Max(x => x.X), list.Max(x => x.Y), list.Max(x => x.Z) })
                : default;
            return AddAccessor(offset, list.Count * 12, Float, "VEC3", list.Count, minMax.min, minMax.max);
        }

        private int WriteQuatAccessor(IEnumerable<Quaternion> values, bool bounds)
        {
            var list = values.ToList();
            var offset = BeginWrite();
            foreach (var value in list)
            {
                _writer.Write(value.X);
                _writer.Write(value.Y);
                _writer.Write(value.Z);
                _writer.Write(value.W);
            }
            return AddAccessor(offset, list.Count * 16, Float, "VEC4", list.Count);
        }

        private int WriteVec4Accessor(IEnumerable<Vector4> values)
        {
            var list = values.ToList();
            var offset = BeginWrite();
            foreach (var value in list)
            {
                _writer.Write(value.X);
                _writer.Write(value.Y);
                _writer.Write(value.Z);
                _writer.Write(value.W);
            }
            return AddAccessor(offset, list.Count * 16, Float, "VEC4", list.Count);
        }

        private int WriteColorAccessor(IEnumerable<Color> values)
        {
            var list = values.ToList();
            var offset = BeginWrite();
            foreach (var value in list)
            {
                _writer.Write(value.R);
                _writer.Write(value.G);
                _writer.Write(value.B);
                _writer.Write(value.A);
            }
            return AddAccessor(offset, list.Count * 16, Float, "VEC4", list.Count);
        }

        private int WriteJointsAccessor(IEnumerable<ImportedVertex> vertices)
        {
            var list = vertices.ToList();
            var offset = BeginWrite();
            foreach (var vertex in list)
            {
                for (var i = 0; i < 4; i++)
                {
                    var value = vertex.BoneIndices != null && i < vertex.BoneIndices.Length ? vertex.BoneIndices[i] : 0;
                    _writer.Write((ushort)Math.Clamp(value, 0, ushort.MaxValue));
                }
            }
            return AddAccessor(offset, list.Count * 8, UShort, "VEC4", list.Count);
        }

        private int WriteWeightsAccessor(IEnumerable<ImportedVertex> vertices)
        {
            var list = vertices.ToList();
            var offset = BeginWrite();
            foreach (var vertex in list)
            {
                for (var i = 0; i < 4; i++)
                {
                    var value = vertex.Weights != null && i < vertex.Weights.Length ? vertex.Weights[i] : 0;
                    _writer.Write(value);
                }
            }
            return AddAccessor(offset, list.Count * 16, Float, "VEC4", list.Count);
        }

        private int WriteUIntAccessor(IEnumerable<uint> values)
        {
            var list = values.ToList();
            var offset = BeginWrite();
            foreach (var value in list)
            {
                _writer.Write(value);
            }
            return AddAccessor(offset, list.Count * 4, UInt, "SCALAR", list.Count);
        }

        private int WriteFloatAccessor(IEnumerable<float> values, bool bounds)
        {
            var list = values.ToList();
            var offset = BeginWrite();
            foreach (var value in list)
            {
                _writer.Write(value);
            }
            var min = bounds && list.Count > 0 ? new[] { list.Min() } : null;
            var max = bounds && list.Count > 0 ? new[] { list.Max() } : null;
            return AddAccessor(offset, list.Count * 4, Float, "SCALAR", list.Count, min, max);
        }

        private int WriteMatrixAccessor(IEnumerable<Matrix4x4> values)
        {
            var list = values.ToList();
            var offset = BeginWrite();
            foreach (var matrix in list)
            {
                for (var i = 0; i < 16; i++)
                {
                    _writer.Write(matrix[i]);
                }
            }
            return AddAccessor(offset, list.Count * 64, Float, "MAT4", list.Count);
        }

        private int BeginWrite()
        {
            Align(4);
            return (int)_bin.Position;
        }

        private int AddAccessor(int offset, int length, int componentType, string type, int count, float[] min = null, float[] max = null)
        {
            var view = new Dictionary<string, object>
            {
                ["buffer"] = 0,
                ["byteOffset"] = offset,
                ["byteLength"] = length,
            };
            var viewIndex = _bufferViews.Count;
            _bufferViews.Add(view);

            var accessor = new Dictionary<string, object>
            {
                ["bufferView"] = viewIndex,
                ["componentType"] = componentType,
                ["count"] = count,
                ["type"] = type,
            };
            if (min != null)
            {
                accessor["min"] = min;
            }
            if (max != null)
            {
                accessor["max"] = max;
            }

            var accessorIndex = _accessors.Count;
            _accessors.Add(accessor);
            return accessorIndex;
        }

        private Dictionary<string, object> BuildDocument()
        {
            return new Dictionary<string, object>
            {
                ["asset"] = new Dictionary<string, object>
                {
                    ["version"] = "2.0",
                    ["generator"] = "AnimeStudio CLI glTF exporter",
                },
                ["scene"] = 0,
                ["scenes"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["nodes"] = new[] { _nodeMap[_imported.RootFrame] },
                    },
                },
                ["nodes"] = _nodes,
                ["meshes"] = _meshes,
                ["materials"] = _materials,
                ["images"] = _images,
                ["textures"] = _textures,
                ["skins"] = _skins,
                ["animations"] = _animations,
                ["buffers"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["uri"] = _options.binary ? null : _binName,
                        ["byteLength"] = (int)_bin.Length,
                    }.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value),
                },
                ["bufferViews"] = _bufferViews,
                ["accessors"] = _accessors,
            };
        }

        private void WriteJson(string path, Dictionary<string, object> gltf)
        {
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(gltf, jsonOptions));
        }

        private void WriteGlb(Dictionary<string, object> gltf)
        {
            var json = JsonSerializer.Serialize(gltf, new JsonSerializerOptions { WriteIndented = false });
            var jsonBytes = Encoding.UTF8.GetBytes(json);
            var binBytes = _bin.ToArray();
            var jsonLength = AlignLength(jsonBytes.Length, 4);
            var binLength = AlignLength(binBytes.Length, 4);
            using var fs = File.Create(_path);
            using var bw = new BinaryWriter(fs);
            bw.Write(0x46546C67);
            bw.Write(2);
            bw.Write(12 + 8 + jsonLength + 8 + binLength);
            bw.Write(jsonLength);
            bw.Write(0x4E4F534A);
            bw.Write(jsonBytes);
            for (var i = jsonBytes.Length; i < jsonLength; i++) bw.Write((byte)0x20);
            bw.Write(binLength);
            bw.Write(0x004E4942);
            bw.Write(binBytes);
            for (var i = binBytes.Length; i < binLength; i++) bw.Write((byte)0);
        }

        private void Align(int alignment)
        {
            while ((_bin.Position % alignment) != 0)
            {
                _writer.Write((byte)0);
            }
        }

        private static int AlignLength(int value, int alignment)
        {
            return (value + alignment - 1) / alignment * alignment;
        }

        private static string ToUri(string path)
        {
            return path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        }

        private static string GetSafeTextureFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name.Length > 100 ? name.Substring(0, 67) + "_" + name.Substring(name.Length - 32) : name;
        }
    }
}
