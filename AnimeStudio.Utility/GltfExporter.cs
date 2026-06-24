using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

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
            public Func<string, IDictionary<string, object>, IDisposable> profileMeasure;
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
        private const int ArrayBufferTarget = 34962;
        private const int ElementArrayBufferTarget = 34963;
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
        private readonly Dictionary<int, string> _textureImagePathMap = new Dictionary<int, string>();
        private readonly Dictionary<int, int> _opaquePreviewTextureMap = new Dictionary<int, int>();
        private readonly Dictionary<string, int> _skinMap = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, int>> _morphTargetMap = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _morphTargetCountMap = new Dictionary<string, int>(StringComparer.Ordinal);
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
            WriteMaterialReport(gltf);
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
                ["POSITION"] = WriteVec3Accessor(mesh.VertexList.Select(x => x.Vertex), true, ArrayBufferTarget),
            };

            if (mesh.hasNormal)
            {
                attributes["NORMAL"] = WriteVec3Accessor(mesh.VertexList.Select(x => x.Normal), false, ArrayBufferTarget);
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
                    }), ArrayBufferTarget);
                }
            }

            if (mesh.hasTangent)
            {
                attributes["TANGENT"] = WriteVec4Accessor(mesh.VertexList.Select(x => x.Tangent), ArrayBufferTarget);
            }

            var protectVertexColorAlpha = false;
            if (mesh.hasColor)
            {
                // 有些 Unity 自定义 shader 会把顶点色 alpha 当业务遮罩，不是真透明。
                // glTF 查看器会直接乘 alpha；仅当所有子材质都确认是不透明预览时，保护可见性。
                protectVertexColorAlpha = ShouldProtectVertexColorAlpha(mesh);
                attributes["COLOR_0"] = WriteColorAccessor(mesh.VertexList.Select(x => x.Color), protectVertexColorAlpha, ArrayBufferTarget);
            }

            if (mesh.BoneList?.Count > 0 && mesh.VertexList.Any(x => x.BoneIndices != null && x.Weights != null))
            {
                attributes["JOINTS_0"] = WriteJointsAccessor(mesh.VertexList, ArrayBufferTarget);
                attributes["WEIGHTS_0"] = WriteWeightsAccessor(mesh.VertexList, ArrayBufferTarget);
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
                    ["indices"] = WriteUIntAccessor(indices, ElementArrayBufferTarget),
                    ["mode"] = 4,
                };
                AddMorphTargets(mesh, primitive);

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
            if (protectVertexColorAlpha)
            {
                gltfMesh["extras"] = new Dictionary<string, object>
                {
                    ["animeStudio"] = new Dictionary<string, object>
                    {
                        ["previewVertexColorAlphaProtected"] = true,
                        ["previewVertexColorAlphaReason"] = "Unity 不透明材质的顶点色 alpha 可能是 shader 遮罩；glTF 预览写为 1 以避免通用查看器误判透明。",
                    },
                };
            }
            AddMorphTargetNames(mesh, gltfMesh);
            var meshIndex = _meshes.Count;
            _meshes.Add(gltfMesh);
            _meshMap[mesh.Path] = meshIndex;
            return meshIndex;
        }

        private void AddMorphTargets(ImportedMesh mesh, Dictionary<string, object> primitive)
        {
            var morph = FindMorph(mesh.Path);
            if (morph?.Channels == null || morph.Channels.Count == 0)
            {
                return;
            }

            var targets = new List<Dictionary<string, object>>();
            var channelMap = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var channel in morph.Channels)
            {
                var keyframe = channel.KeyframeList?.LastOrDefault(x => x?.VertexList != null && x.VertexList.Count > 0);
                if (keyframe == null)
                {
                    continue;
                }

                var positions = new Vector3[mesh.VertexList.Count];
                var normals = keyframe.hasNormals ? new Vector3[mesh.VertexList.Count] : null;
                var tangents = keyframe.hasTangents ? new Vector3[mesh.VertexList.Count] : null;
                foreach (var morphVertex in keyframe.VertexList)
                {
                    var index = (int)morphVertex.Index;
                    if (index < 0 || index >= mesh.VertexList.Count || morphVertex.Vertex == null)
                    {
                        continue;
                    }

                    var baseVertex = mesh.VertexList[index];
                    positions[index] = morphVertex.Vertex.Vertex - baseVertex.Vertex;
                    if (normals != null)
                    {
                        normals[index] = morphVertex.Vertex.Normal - baseVertex.Normal;
                    }
                    if (tangents != null)
                    {
                        var tangent = morphVertex.Vertex.Tangent - baseVertex.Tangent;
                        tangents[index] = new Vector3(tangent.X, tangent.Y, tangent.Z);
                    }
                }

                var target = new Dictionary<string, object>
                {
                    // morph target 的 POSITION 也是 glTF 规范里的 position accessor，需要 min/max。
                    // F3D 这类严格查看器会因为缺少 bounds 直接拒绝加载场景。
                    ["POSITION"] = WriteVec3Accessor(positions, true, ArrayBufferTarget),
                };
                if (normals != null)
                {
                    target["NORMAL"] = WriteVec3Accessor(normals, false, ArrayBufferTarget);
                }
                if (tangents != null)
                {
                    target["TANGENT"] = WriteVec3Accessor(tangents, false, ArrayBufferTarget);
                }

                var channelName = channel.Name ?? $"morph_{targets.Count}";
                channelMap[channelName] = targets.Count;
                targets.Add(target);
            }

            if (targets.Count == 0)
            {
                return;
            }

            primitive["targets"] = targets;
            _morphTargetMap[mesh.Path] = channelMap;
            _morphTargetCountMap[mesh.Path] = targets.Count;
        }

        private void AddMorphTargetNames(ImportedMesh mesh, Dictionary<string, object> gltfMesh)
        {
            var morph = FindMorph(mesh.Path);
            if (morph?.Channels == null || !_morphTargetMap.TryGetValue(mesh.Path, out var targetMap) || targetMap.Count == 0)
            {
                return;
            }

            var names = targetMap
                .OrderBy(x => x.Value)
                .Select(x => x.Key)
                .ToArray();
            gltfMesh["weights"] = names.Select(_ => 0.0f).ToArray();
            if (!gltfMesh.TryGetValue("extras", out var extrasValue)
                || extrasValue is not Dictionary<string, object> extras)
            {
                extras = new Dictionary<string, object>();
                gltfMesh["extras"] = extras;
            }
            extras["targetNames"] = names;
            extras["unityMorph"] = new Dictionary<string, object>
            {
                ["path"] = morph.Path,
                ["channelCount"] = names.Length,
                ["note"] = "glTF morph targets store one shape target per Unity BlendShape channel; multi-frame Unity blend shape channels currently use the last shape frame as the target.",
            };
        }

        private ImportedMorph FindMorph(string path)
        {
            return _imported.MorphList?.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.Ordinal));
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

                    AddAnimationChannel(track.Translations, "translation", nodeIndex, samplers, channels, (values, bounds) => WriteVec3Accessor(values, bounds));
                    AddAnimationChannel(track.Rotations, "rotation", nodeIndex, samplers, channels, WriteQuatAccessor);
                    AddAnimationChannel(track.Scalings, "scale", nodeIndex, samplers, channels, (values, bounds) => WriteVec3Accessor(values, bounds));
                }
                AddBlendShapeAnimationChannels(animation, samplers, channels);

                if (channels.Count > 0)
                {
                    var animationEntry = new Dictionary<string, object>
                    {
                        ["name"] = animation.Name ?? "Animation",
                        ["samplers"] = samplers,
                        ["channels"] = channels,
                    };
                    AddAnimationExtras(animationEntry, animation);
                    _animations.Add(animationEntry);
                }
                else if (animation.HumanoidMuscles?.Count > 0)
                {
                    var animationEntry = new Dictionary<string, object>
                    {
                        ["name"] = animation.Name ?? "Animation",
                        ["samplers"] = samplers,
                        ["channels"] = channels,
                    };
                    AddAnimationExtras(animationEntry, animation);
                    _animations.Add(animationEntry);
                }
            }
        }

        private static void AddAnimationExtras(Dictionary<string, object> animationEntry, ImportedKeyframedAnimation animation)
        {
            if (animation.HumanoidMuscles == null || animation.HumanoidMuscles.Count == 0)
            {
                return;
            }

            animationEntry["extras"] = new Dictionary<string, object>
            {
                ["unityHumanoid"] = new Dictionary<string, object>
                {
                    ["requiresBake"] = !animation.HumanoidMusclesBaked,
                    ["baked"] = animation.HumanoidMusclesBaked,
                    ["bakeMode"] = animation.HumanoidBakeMode,
                    ["bakedTrackCount"] = animation.HumanoidBakedTrackCount,
                    ["bakedKeyframeCount"] = animation.HumanoidBakedKeyframeCount,
                    ["muscleCurveCount"] = animation.HumanoidMuscles.Count,
                    ["keyframeCount"] = animation.HumanoidMuscles.Sum(x => x.Keyframes?.Count ?? 0),
                    ["attributes"] = animation.HumanoidMuscles
                        .Select(x => x.Attribute)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .Take(256)
                        .ToArray(),
                    ["diagnostics"] = animation.HumanoidBakeDiagnostics == null ? null : new Dictionary<string, object>
                    {
                        ["status"] = animation.HumanoidBakeDiagnostics.Status,
                        ["mode"] = animation.HumanoidBakeDiagnostics.Mode,
                        ["solver"] = animation.HumanoidBakeDiagnostics.Solver,
                        ["humanBoneCount"] = animation.HumanoidBakeDiagnostics.HumanBoneCount,
                        ["targetCount"] = animation.HumanoidBakeDiagnostics.TargetCount,
                        ["mappedTargetCount"] = animation.HumanoidBakeDiagnostics.MappedTargetCount,
                        ["missingTargetCount"] = animation.HumanoidBakeDiagnostics.MissingTargetCount,
                        ["sampleTimeCount"] = animation.HumanoidBakeDiagnostics.SampleTimeCount,
                        ["notes"] = animation.HumanoidBakeDiagnostics.Notes ?? Array.Empty<string>(),
                        ["targets"] = animation.HumanoidBakeDiagnostics.Targets
                            .Select(x => new Dictionary<string, object>
                            {
                                ["humanBone"] = x.HumanBone,
                                ["skeletonPath"] = x.SkeletonPath,
                                ["hasFrame"] = x.HasFrame,
                                ["hasCurves"] = x.HasCurves,
                                ["attributes"] = x.Attributes ?? Array.Empty<string>(),
                                ["status"] = x.Status,
                                ["humanBoneIndex"] = x.HumanBoneIndex,
                                ["avatarSkeletonNodeIndex"] = x.AvatarSkeletonNodeIndex,
                                ["avatarAxesId"] = x.AvatarAxesId,
                                ["avatarPreQ"] = x.AvatarPreQ,
                                ["avatarPostQ"] = x.AvatarPostQ,
                                ["avatarSgn"] = x.AvatarSgn,
                                ["avatarLimitMin"] = x.AvatarLimitMin,
                                ["avatarLimitMax"] = x.AvatarLimitMax,
                            })
                            .ToArray(),
                    },
                },
            };
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

        private void AddBlendShapeAnimationChannels(
            ImportedKeyframedAnimation animation,
            List<Dictionary<string, object>> samplers,
            List<Dictionary<string, object>> channels)
        {
            var blendShapeTracks = animation.TrackList?
                .Where(x => x.BlendShape?.Keyframes != null && x.BlendShape.Keyframes.Count > 0)
                .GroupBy(x => x.Path, StringComparer.Ordinal)
                .ToArray();
            if (blendShapeTracks == null || blendShapeTracks.Length == 0)
            {
                return;
            }

            foreach (var group in blendShapeTracks)
            {
                if (!_pathNodeMap.TryGetValue(group.Key, out var nodeIndex)
                    || !_morphTargetMap.TryGetValue(group.Key, out var targetMap)
                    || !_morphTargetCountMap.TryGetValue(group.Key, out var targetCount)
                    || targetCount <= 0)
                {
                    continue;
                }

                var usableTracks = group
                    .Where(x => !string.IsNullOrWhiteSpace(x.BlendShape?.ChannelName)
                        && targetMap.ContainsKey(x.BlendShape.ChannelName))
                    .ToArray();
                if (usableTracks.Length == 0)
                {
                    continue;
                }

                var times = usableTracks
                    .SelectMany(x => x.BlendShape.Keyframes.Select(k => k.time))
                    .Distinct()
                    .OrderBy(x => x)
                    .ToArray();
                if (times.Length == 0)
                {
                    continue;
                }

                var weights = new List<float>(times.Length * targetCount);
                foreach (var time in times)
                {
                    var values = new float[targetCount];
                    foreach (var track in usableTracks)
                    {
                        var targetIndex = targetMap[track.BlendShape.ChannelName];
                        values[targetIndex] = EvaluateFloatCurve(track.BlendShape.Keyframes, time) / 100.0f;
                    }
                    weights.AddRange(values);
                }

                var input = WriteFloatAccessor(times, true);
                var output = WriteFloatAccessor(weights, false);
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
                        ["path"] = "weights",
                    },
                });
            }
        }

        private static float EvaluateFloatCurve(List<ImportedKeyframe<float>> keys, float time)
        {
            if (keys == null || keys.Count == 0)
            {
                return 0.0f;
            }

            var ordered = keys.OrderBy(x => x.time).ToArray();
            if (time <= ordered[0].time)
            {
                return ordered[0].value;
            }
            for (var i = 1; i < ordered.Length; i++)
            {
                if (time > ordered[i].time)
                {
                    continue;
                }

                var previous = ordered[i - 1];
                var next = ordered[i];
                var span = next.time - previous.time;
                if (span <= 0.000001f)
                {
                    return next.value;
                }

                var t = (time - previous.time) / span;
                return previous.value + (next.value - previous.value) * t;
            }

            return ordered[^1].value;
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
            var unityTextures = new List<Dictionary<string, object>>();
            var baseColorTextureIndex = -1;
            var colorMaskTextureIndex = -1;
            var maskTextureIndex = -1;

            foreach (var textureRef in material.Textures ?? Enumerable.Empty<ImportedMaterialTexture>())
            {
                var texture = ImportedHelpers.FindTexture(textureRef.Name, _imported.TextureList);
                if (texture == null)
                {
                    continue;
                }
                unityTextures.Add(BuildUnityTextureExtra(textureRef, texture));
                var textureIndex = GetTextureIndex(texture);
                if (textureIndex < 0)
                {
                    continue;
                }
                if (textureRef.Dest == 0)
                {
                    pbr["baseColorTexture"] = new Dictionary<string, object> { ["index"] = textureIndex };
                    baseColorTextureIndex = textureIndex;
                }
                else if (textureRef.Dest == 1 || textureRef.Dest == 3)
                {
                    normalTexture ??= new Dictionary<string, object> { ["index"] = textureIndex };
                }
                else if (IsColorMaskSlot(textureRef.Slot))
                {
                    colorMaskTextureIndex = textureIndex;
                }
                else if (IsMaskSlot(textureRef.Slot))
                {
                    maskTextureIndex = textureIndex;
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
            ApplyUnityMaterialState(material, gltfMaterial);
            NormalizePbrBaseColorAlpha(material, pbr, gltfMaterial);
            if (!gltfMaterial.ContainsKey("alphaMode") && (material.Diffuse.A < 0.999f || material.Transparency > 0))
            {
                gltfMaterial["alphaMode"] = "BLEND";
            }
            if (unityTextures.Count > 0 || material.UnityFloats?.Count > 0 || material.UnityColors?.Count > 0)
            {
                var extras = EnsureMaterialExtras(gltfMaterial);
                extras["unityTextures"] = unityTextures;
                extras["unityMaterial"] = BuildUnityMaterialExtra(material, unityTextures);
            }
            ApplyColorMaskTintPipeline(
                material,
                pbr,
                gltfMaterial,
                baseColorTextureIndex,
                colorMaskTextureIndex,
                maskTextureIndex);
            ProtectOpaquePreviewBaseColorTextureAlpha(material, pbr, gltfMaterial);

            var index = _materials.Count;
            _materials.Add(gltfMaterial);
            _materialMap[name] = index;
            return index;
        }

        private bool ShouldProtectVertexColorAlpha(ImportedMesh mesh)
        {
            if (mesh?.SubmeshList == null || mesh.SubmeshList.Count == 0)
            {
                return false;
            }

            var hasMaterial = false;
            foreach (var submesh in mesh.SubmeshList)
            {
                var material = ImportedHelpers.FindMaterial(submesh.Material, _imported.MaterialList);
                if (material == null)
                {
                    return false;
                }
                hasMaterial = true;
                if (!IsOpaquePreviewMaterial(material))
                {
                    return false;
                }
            }

            return hasMaterial;
        }

        private void ApplyColorMaskTintPipeline(
            ImportedMaterial material,
            Dictionary<string, object> pbr,
            Dictionary<string, object> gltfMaterial,
            int baseColorTextureIndex,
            int colorMaskTextureIndex,
            int maskTextureIndex)
        {
            if (baseColorTextureIndex < 0)
            {
                return;
            }

            var tintColors = FindTintColors(material).ToList();
            var maskSlots = new List<Dictionary<string, object>>();
            if (colorMaskTextureIndex >= 0)
            {
                maskSlots.Add(new Dictionary<string, object>
                {
                    ["slot"] = "_ColorMask",
                    ["texture"] = colorMaskTextureIndex,
                    ["usage"] = "tint-region-mask",
                });
            }
            if (maskTextureIndex >= 0 && maskTextureIndex != colorMaskTextureIndex)
            {
                maskSlots.Add(new Dictionary<string, object>
                {
                    ["slot"] = "_MaskMap",
                    ["texture"] = maskTextureIndex,
                    ["usage"] = "shader-mask",
                });
            }

            if (maskSlots.Count == 0 && tintColors.Count == 0)
            {
                return;
            }

            var status = "indexed";
            var notes = new List<string>();
            if (maskSlots.Count > 0 && tintColors.Count == 0)
            {
                status = "needsCustomizationTint";
                notes.Add("Color/mask textures exist, but no usable tint color was found on the Unity Material. Runtime customization data may be stored outside the material.");
            }
            else if (maskSlots.Count > 0 && TryBakeColorMaskPreview(material, baseColorTextureIndex, colorMaskTextureIndex, tintColors[0].color, out var bakedTextureIndex))
            {
                pbr["baseColorTexture"] = new Dictionary<string, object> { ["index"] = bakedTextureIndex };
                status = "bakedPreview";
                notes.Add("Generated a conservative preview baseColor texture from the first tint color and the color mask red channel.");
            }
            else if (tintColors.Count > 0)
            {
                status = "tintParametersOnly";
                notes.Add("Tint colors were found, but no color mask preview texture could be baked.");
            }

            var extras = EnsureMaterialExtras(gltfMaterial);
            extras["animeStudioMaterial"] = new Dictionary<string, object>
            {
                ["workflow"] = "ColorMaskTint",
                ["status"] = status,
                ["baseColorTexture"] = baseColorTextureIndex,
                ["maskTextures"] = maskSlots,
                ["tintColors"] = tintColors.Select(x => new Dictionary<string, object>
                {
                    ["name"] = x.name,
                    ["color"] = new[] { x.color.R, x.color.G, x.color.B, x.color.A },
                }).ToArray(),
                ["notes"] = notes,
            };
        }

        private static Dictionary<string, object> EnsureMaterialExtras(Dictionary<string, object> gltfMaterial)
        {
            if (gltfMaterial.TryGetValue("extras", out var value)
                && value is Dictionary<string, object> extras)
            {
                return extras;
            }

            extras = new Dictionary<string, object>();
            gltfMaterial["extras"] = extras;
            return extras;
        }

        private bool TryBakeColorMaskPreview(
            ImportedMaterial material,
            int baseColorTextureIndex,
            int colorMaskTextureIndex,
            Color tint,
            out int bakedTextureIndex)
        {
            bakedTextureIndex = -1;
            if (colorMaskTextureIndex < 0
                || !_textureImagePathMap.TryGetValue(baseColorTextureIndex, out var basePath)
                || !_textureImagePathMap.TryGetValue(colorMaskTextureIndex, out var maskPath)
                || !File.Exists(basePath)
                || !File.Exists(maskPath))
            {
                return false;
            }

            try
            {
                using var baseImage = Image.Load<Rgba32>(basePath);
                using var maskImage = Image.Load<Rgba32>(maskPath);
                if (baseImage.Width != maskImage.Width || baseImage.Height != maskImage.Height)
                {
                    return false;
                }

                var tintR = Clamp01(tint.R);
                var tintG = Clamp01(tint.G);
                var tintB = Clamp01(tint.B);
                for (var y = 0; y < baseImage.Height; y++)
                {
                    var baseRow = baseImage.DangerousGetPixelRowMemory(y).Span;
                    var maskRow = maskImage.DangerousGetPixelRowMemory(y).Span;
                    for (var x = 0; x < baseRow.Length; x++)
                    {
                        var mask = maskRow[x].R / 255f;
                        var pixel = baseRow[x];
                        pixel.R = (byte)Math.Round(Lerp(pixel.R, pixel.R * tintR, mask));
                        pixel.G = (byte)Math.Round(Lerp(pixel.G, pixel.G * tintG, mask));
                        pixel.B = (byte)Math.Round(Lerp(pixel.B, pixel.B * tintB, mask));
                        baseRow[x] = pixel;
                    }
                }

                var bakedName = GetSafeTextureFileName($"{material.Name}_colormask_preview.png");
                var bakedPath = Path.Combine(_localTextureDirectory ?? _directory, bakedName);
                Directory.CreateDirectory(Path.GetDirectoryName(bakedPath) ?? _directory);
                baseImage.SaveAsPng(bakedPath);
                bakedTextureIndex = AddImageTexture(bakedPath);
                return bakedTextureIndex >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<(string name, Color color)> FindTintColors(ImportedMaterial material)
        {
            if (material.UnityColors == null)
            {
                yield break;
            }

            foreach (var color in material.UnityColors)
            {
                if (!IsTintColorName(color.Key) || IsNeutralTint(color.Value))
                {
                    continue;
                }
                yield return (color.Key, color.Value);
            }
        }

        private static bool IsTintColorName(string name)
        {
            return !string.IsNullOrWhiteSpace(name)
                && (name.IndexOf("Tint", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Skin", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Primary", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Secondary", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Cloth", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Dye", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsNeutralTint(Color color)
        {
            return (Math.Abs(color.R - 1.0f) < 0.0001f
                    && Math.Abs(color.G - 1.0f) < 0.0001f
                    && Math.Abs(color.B - 1.0f) < 0.0001f)
                || (Math.Abs(color.R) < 0.0001f
                    && Math.Abs(color.G) < 0.0001f
                    && Math.Abs(color.B) < 0.0001f
                    && Math.Abs(color.A) < 0.0001f);
        }

        private static bool IsColorMaskSlot(string slot)
        {
            return string.Equals(slot, "_ColorMask", StringComparison.OrdinalIgnoreCase)
                || string.Equals(slot, "_ColorMaskMap", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsMaskSlot(string slot)
        {
            return string.Equals(slot, "_MaskMap", StringComparison.OrdinalIgnoreCase)
                || string.Equals(slot, "_Masks", StringComparison.OrdinalIgnoreCase);
        }

        private static float Clamp01(float value) => Math.Max(0.0f, Math.Min(1.0f, value));

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private static void NormalizePbrBaseColorAlpha(
            ImportedMaterial material,
            Dictionary<string, object> pbr,
            Dictionary<string, object> gltfMaterial)
        {
            if (!pbr.ContainsKey("baseColorTexture"))
            {
                return;
            }

            if (pbr["baseColorFactor"] is not float[] color || color.Length < 4)
            {
                return;
            }

            if (color[3] > 0.0001f)
            {
                return;
            }

            if (IsUnityTransparentMaterial(material, gltfMaterial))
            {
                return;
            }

            // Unity 的不透明/裁剪材质里，基础色 alpha 为 0 经常只是 shader 控制值。
            // glTF 会把它直接乘到贴图上，导致贴图材质整体不可见，所以这里保留贴图 alpha 自己决定可见性。
            color[3] = 1.0f;
            pbr["baseColorFactor"] = color;
        }

        private void ProtectOpaquePreviewBaseColorTextureAlpha(
            ImportedMaterial material,
            Dictionary<string, object> pbr,
            Dictionary<string, object> gltfMaterial)
        {
            if (!IsOpaquePreviewMaterial(material, gltfMaterial))
            {
                return;
            }

            if (!TryGetTextureIndex(pbr, "baseColorTexture", out var baseColorTextureIndex))
            {
                return;
            }

            var protectedTextureIndex = GetOpaquePreviewTextureIndex(baseColorTextureIndex);
            if (protectedTextureIndex < 0 || protectedTextureIndex == baseColorTextureIndex)
            {
                return;
            }

            pbr["baseColorTexture"] = new Dictionary<string, object> { ["index"] = protectedTextureIndex };
            AddAnimeStudioMaterialNote(
                gltfMaterial,
                "previewTextureAlphaProtected",
                "Unity 材质未声明透明/裁剪，基础贴图 alpha 按预览保护为不透明；原始贴图和 Unity 槽位仍保存在 extras.unityMaterial。");
        }

        private int GetOpaquePreviewTextureIndex(int sourceTextureIndex)
        {
            if (_opaquePreviewTextureMap.TryGetValue(sourceTextureIndex, out var existing))
            {
                return existing;
            }

            var protectedIndex = CreateOpaquePreviewTexture(sourceTextureIndex);
            _opaquePreviewTextureMap[sourceTextureIndex] = protectedIndex;
            return protectedIndex;
        }

        private int CreateOpaquePreviewTexture(int sourceTextureIndex)
        {
            if (!_textureImagePathMap.TryGetValue(sourceTextureIndex, out var sourcePath)
                || !File.Exists(sourcePath)
                || !Path.GetExtension(sourcePath).Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                return sourceTextureIndex;
            }

            try
            {
                using var image = Image.Load<Rgba32>(sourcePath);
                var changed = false;
                for (var y = 0; y < image.Height; y++)
                {
                    var row = image.DangerousGetPixelRowMemory(y).Span;
                    for (var x = 0; x < row.Length; x++)
                    {
                        if (row[x].A == byte.MaxValue)
                        {
                            continue;
                        }
                        row[x].A = byte.MaxValue;
                        changed = true;
                    }
                }

                if (!changed)
                {
                    return sourceTextureIndex;
                }

                var fileName = Path.GetFileNameWithoutExtension(sourcePath) + "_opaque_preview.png";
                var previewPath = Path.Combine(Path.GetDirectoryName(sourcePath) ?? _directory, GetSafeTextureFileName(fileName));
                image.SaveAsPng(previewPath);
                return AddImageTexture(previewPath);
            }
            catch
            {
                return sourceTextureIndex;
            }
        }

        private static bool TryGetTextureIndex(Dictionary<string, object> pbr, string textureName, out int textureIndex)
        {
            textureIndex = -1;
            if (!pbr.TryGetValue(textureName, out var value)
                || value is not Dictionary<string, object> textureInfo
                || !textureInfo.TryGetValue("index", out var indexValue))
            {
                return false;
            }

            if (indexValue is int intIndex)
            {
                textureIndex = intIndex;
                return true;
            }

            if (indexValue is long longIndex && longIndex >= 0 && longIndex <= int.MaxValue)
            {
                textureIndex = (int)longIndex;
                return true;
            }

            return false;
        }

        private static void AddAnimeStudioMaterialNote(
            Dictionary<string, object> gltfMaterial,
            string flagName,
            string note)
        {
            var extras = EnsureMaterialExtras(gltfMaterial);
            if (!extras.TryGetValue("animeStudioMaterial", out var animeValue)
                || animeValue is not Dictionary<string, object> anime)
            {
                anime = new Dictionary<string, object>();
                extras["animeStudioMaterial"] = anime;
            }

            anime[flagName] = true;
            if (anime.TryGetValue("notes", out var notesValue) && notesValue is List<string> notes)
            {
                notes.Add(note);
            }
            else if (notesValue is string[] noteArray)
            {
                anime["notes"] = noteArray.Concat(new[] { note }).ToArray();
            }
            else
            {
                anime["notes"] = new[] { note };
            }
        }

        private static bool IsUnityTransparentMaterial(ImportedMaterial material, Dictionary<string, object> gltfMaterial)
        {
            if (gltfMaterial.TryGetValue("alphaMode", out var alphaMode)
                && string.Equals(alphaMode as string, "BLEND", StringComparison.Ordinal))
            {
                return true;
            }

            return GetUnityFloat(material, "_SurfaceType") > 0.5f
                || GetUnityFloat(material, "_BlendMode") > 0.5f
                || (TryGetUnityFloat(material, "_SrcBlend", out var srcBlend) && srcBlend != 1.0f)
                || (TryGetUnityFloat(material, "_DstBlend", out var dstBlend) && dstBlend != 0.0f);
        }

        private static bool IsOpaquePreviewMaterial(ImportedMaterial material, Dictionary<string, object> gltfMaterial = null)
        {
            if (material == null)
            {
                return false;
            }
            if (gltfMaterial != null
                && gltfMaterial.TryGetValue("alphaMode", out var alphaMode)
                && alphaMode is string alphaModeText
                && !string.IsNullOrWhiteSpace(alphaModeText))
            {
                return false;
            }
            if (material.Diffuse.A < 0.999f || material.Transparency > 0.0f)
            {
                return false;
            }
            if (IsUnityTransparentMaterial(material, gltfMaterial ?? new Dictionary<string, object>()))
            {
                return false;
            }

            return !HasUnityAlphaClip(material);
        }

        private static bool HasUnityAlphaClip(ImportedMaterial material)
        {
            return GetUnityFloat(material, "_AlphaCutoffEnable") > 0.5f
                || GetUnityFloat(material, "_AlphaToMask") > 0.5f
                || GetUnityFloat(material, "_UseOpacityMap") > 0.5f
                || GetUnityFloat(material, "_TransparentDepthPrepassEnable") > 0.5f
                || GetUnityFloat(material, "_TransparentDepthPostpassEnable") > 0.5f;
        }

        private static Dictionary<string, object> BuildUnityTextureExtra(ImportedMaterialTexture textureRef, ImportedTexture texture)
        {
            return new Dictionary<string, object>
            {
                ["slotName"] = textureRef.Slot ?? "",
                ["dest"] = textureRef.Dest,
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
                ["scale"] = new[] { textureRef.Scale.X, textureRef.Scale.Y },
                ["offset"] = new[] { textureRef.Offset.X, textureRef.Offset.Y },
            };
        }

        private static Dictionary<string, object> BuildUnityMaterialExtra(
            ImportedMaterial material,
            List<Dictionary<string, object>> unityTextures)
        {
            var extra = new Dictionary<string, object>();
            if (unityTextures.Count > 0)
            {
                extra["textures"] = unityTextures;
            }
            if (material.UnityFloats?.Count > 0)
            {
                extra["floats"] = material.UnityFloats;
            }
            if (material.UnityColors?.Count > 0)
            {
                extra["colors"] = material.UnityColors.ToDictionary(
                    x => x.Key,
                    x => new[] { x.Value.R, x.Value.G, x.Value.B, x.Value.A });
            }
            return extra;
        }

        private static void ApplyUnityMaterialState(ImportedMaterial material, Dictionary<string, object> gltfMaterial)
        {
            if (GetUnityFloat(material, "_DoubleSidedEnable") > 0.5f || GetUnityFloat(material, "_CullMode") == 0.0f)
            {
                gltfMaterial["doubleSided"] = true;
            }

            var surfaceType = GetUnityFloat(material, "_SurfaceType");
            if (surfaceType > 0.5f)
            {
                gltfMaterial["alphaMode"] = "BLEND";
                return;
            }

            if (HasUnityAlphaClip(material))
            {
                gltfMaterial["alphaMode"] = "MASK";
                var cutoff = GetUnityFloat(material, "_Cutoff");
                if (cutoff > 0.0f && cutoff < 1.0f)
                {
                    gltfMaterial["alphaCutoff"] = cutoff;
                }
            }
        }

        private static float GetUnityFloat(ImportedMaterial material, string name)
        {
            return material.UnityFloats != null && material.UnityFloats.TryGetValue(name, out var value)
                ? value
                : 0.0f;
        }

        private static bool TryGetUnityFloat(ImportedMaterial material, string name, out float value)
        {
            if (material.UnityFloats != null && material.UnityFloats.TryGetValue(name, out value))
            {
                return true;
            }

            value = 0.0f;
            return false;
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
            _textureImagePathMap[textureIndex] = imagePath;
            return textureIndex;
        }

        private int AddImageTexture(string imagePath)
        {
            if (!File.Exists(imagePath) || !IsGltfSupportedImagePath(imagePath))
            {
                return -1;
            }

            var fullPath = Path.GetFullPath(imagePath);
            foreach (var existing in _textureImagePathMap)
            {
                if (string.Equals(Path.GetFullPath(existing.Value), fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return existing.Key;
                }
            }

            var imageIndex = _images.Count;
            _images.Add(new Dictionary<string, object>
            {
                ["uri"] = ToUri(Path.GetRelativePath(_directory, fullPath)),
            });

            var textureIndex = _textures.Count;
            _textures.Add(new Dictionary<string, object> { ["source"] = imageIndex });
            _textureImagePathMap[textureIndex] = fullPath;
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
                var writeData = GetTextureProfileData(texture, path);
                writeData["bytes"] = texture.Data.Length;
                using (Measure("model_texture_write", writeData))
                {
                    File.WriteAllBytes(path, texture.Data);
                    WriteTextureMetadata(path, texture);
                }
            }
            if (!File.Exists(path))
            {
                return null;
            }

            return CreateLocalTextureLink(path, Path.GetFileName(path), texture);
        }

        private string CreateLocalTextureLink(string sharedPath, string fileName, ImportedTexture texture)
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

            var linkData = GetTextureProfileData(texture, linkPath);
            linkData["sharedPath"] = sharedPath;
            using (Measure("model_texture_link", linkData))
            {
                if (CreateHardLink(linkPath, sharedPath, IntPtr.Zero))
                {
                    linkData["linkMode"] = "HardLink";
                }
                else
                {
                    File.Copy(sharedPath, linkPath, false);
                    linkData["linkMode"] = "Copy";
                }
            }
            return linkPath;
        }

        private IDisposable Measure(string stage, IDictionary<string, object> data)
        {
            return _options.profileMeasure?.Invoke(stage, data);
        }

        private static Dictionary<string, object> GetTextureProfileData(ImportedTexture texture, string path)
        {
            return new Dictionary<string, object>
            {
                ["texture"] = texture.Name,
                ["exportName"] = texture.ExportName ?? texture.Name,
                ["path"] = path,
                ["textureFormat"] = texture.SourceTextureFormat,
                ["width"] = texture.Width,
                ["height"] = texture.Height,
                ["source"] = texture.SourceAssetPath,
                ["pathId"] = texture.SourcePathId,
            };
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

        private int WriteVec2Accessor(IEnumerable<Vector2> values, int bufferViewTarget = 0)
        {
            var list = values.ToList();
            var offset = BeginWrite();
            foreach (var value in list)
            {
                WriteFinite(value.X);
                WriteFinite(value.Y);
            }
            return AddAccessor(offset, list.Count * 8, Float, "VEC2", list.Count, target: bufferViewTarget);
        }

        private int WriteVec3Accessor(IEnumerable<Vector3> values, bool bounds, int bufferViewTarget = 0)
        {
            var list = values.ToList();
            var offset = BeginWrite();
            foreach (var value in list)
            {
                WriteFinite(value.X);
                WriteFinite(value.Y);
                WriteFinite(value.Z);
            }
            var min = bounds ? FiniteVec3Bounds(list, isMin: true) : null;
            var max = bounds ? FiniteVec3Bounds(list, isMin: false) : null;
            return AddAccessor(offset, list.Count * 12, Float, "VEC3", list.Count, min, max, bufferViewTarget);
        }

        private int WriteQuatAccessor(IEnumerable<Quaternion> values, bool bounds)
        {
            var list = values.ToList();
            var offset = BeginWrite();
            foreach (var value in list)
            {
                WriteFinite(value.X);
                WriteFinite(value.Y);
                WriteFinite(value.Z);
                WriteFinite(value.W);
            }
            return AddAccessor(offset, list.Count * 16, Float, "VEC4", list.Count);
        }

        private int WriteVec4Accessor(IEnumerable<Vector4> values, int bufferViewTarget = 0)
        {
            var list = values.ToList();
            var offset = BeginWrite();
            foreach (var value in list)
            {
                WriteFinite(value.X);
                WriteFinite(value.Y);
                WriteFinite(value.Z);
                WriteFinite(value.W);
            }
            return AddAccessor(offset, list.Count * 16, Float, "VEC4", list.Count, target: bufferViewTarget);
        }

        private int WriteColorAccessor(IEnumerable<Color> values, bool forceOpaqueAlpha = false, int bufferViewTarget = 0)
        {
            var list = values.ToList();
            var offset = BeginWrite();
            foreach (var value in list)
            {
                WriteFinite(value.R);
                WriteFinite(value.G);
                WriteFinite(value.B);
                WriteFinite(forceOpaqueAlpha ? 1.0f : value.A);
            }
            return AddAccessor(offset, list.Count * 16, Float, "VEC4", list.Count, target: bufferViewTarget);
        }

        private int WriteJointsAccessor(IEnumerable<ImportedVertex> vertices, int bufferViewTarget = 0)
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
            return AddAccessor(offset, list.Count * 8, UShort, "VEC4", list.Count, target: bufferViewTarget);
        }

        private int WriteWeightsAccessor(IEnumerable<ImportedVertex> vertices, int bufferViewTarget = 0)
        {
            var list = vertices.ToList();
            var offset = BeginWrite();
            foreach (var vertex in list)
            {
                var weights = new float[4];
                var sum = 0.0f;
                for (var i = 0; i < 4; i++)
                {
                    var value = vertex.Weights != null && i < vertex.Weights.Length ? vertex.Weights[i] : 0;
                    if (!float.IsFinite(value) || value < 0)
                    {
                        value = 0;
                    }
                    weights[i] = value;
                    sum += value;
                }

                // glTF 要求 skin 权重和为 1。Unity 原始权重常有很小浮点误差，导出前统一修正，避免第三方校验器误报。
                if (sum > 0.000001f)
                {
                    var inv = 1.0f / sum;
                    for (var i = 0; i < 4; i++)
                    {
                        weights[i] *= inv;
                    }
                }
                else
                {
                    weights[0] = 1.0f;
                }

                for (var i = 0; i < 4; i++)
                {
                    WriteFinite(weights[i]);
                }
            }
            return AddAccessor(offset, list.Count * 16, Float, "VEC4", list.Count, target: bufferViewTarget);
        }

        private int WriteUIntAccessor(IEnumerable<uint> values, int bufferViewTarget = 0)
        {
            var list = values.ToList();
            var offset = BeginWrite();
            foreach (var value in list)
            {
                _writer.Write(value);
            }
            return AddAccessor(offset, list.Count * 4, UInt, "SCALAR", list.Count, target: bufferViewTarget);
        }

        private int WriteFloatAccessor(IEnumerable<float> values, bool bounds)
        {
            var list = values.ToList();
            var offset = BeginWrite();
            foreach (var value in list)
            {
                WriteFinite(value);
            }
            var finite = list.Where(IsFinite).ToList();
            var min = bounds && finite.Count > 0 ? new[] { finite.Min() } : null;
            var max = bounds && finite.Count > 0 ? new[] { finite.Max() } : null;
            return AddAccessor(offset, list.Count * 4, Float, "SCALAR", list.Count, min, max);
        }

        private int WriteMatrixAccessor(IEnumerable<Matrix4x4> values)
        {
            var list = values.ToList();
            var offset = BeginWrite();
            foreach (var matrix in list)
            {
                // Unity bind poses arrive in the importer orientation; glTF MAT4 accessors need the transposed column-major form.
                for (var column = 0; column < 4; column++)
                {
                    for (var row = 0; row < 4; row++)
                    {
                        WriteFinite(matrix[column, row]);
                    }
                }
            }
            return AddAccessor(offset, list.Count * 64, Float, "MAT4", list.Count);
        }

        private int BeginWrite()
        {
            Align(4);
            return (int)_bin.Position;
        }

        private void WriteFinite(float value)
        {
            _writer.Write(IsFinite(value) ? value : 0.0f);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static float[] FiniteVec3Bounds(List<Vector3> values, bool isMin)
        {
            if (values.Count == 0)
            {
                return null;
            }

            var x = values.Select(v => v.X).Where(IsFinite).ToList();
            var y = values.Select(v => v.Y).Where(IsFinite).ToList();
            var z = values.Select(v => v.Z).Where(IsFinite).ToList();
            if (x.Count == 0 || y.Count == 0 || z.Count == 0)
            {
                return null;
            }

            return isMin
                ? new[] { x.Min(), y.Min(), z.Min() }
                : new[] { x.Max(), y.Max(), z.Max() };
        }

        private int AddAccessor(int offset, int length, int componentType, string type, int count, float[] min = null, float[] max = null, int target = 0)
        {
            var view = new Dictionary<string, object>
            {
                ["buffer"] = 0,
                ["byteOffset"] = offset,
                ["byteLength"] = length,
            };
            if (target > 0)
            {
                view["target"] = target;
            }
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
            var document = new Dictionary<string, object>
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

            // glTF 的可选顶层数组为空时不要写出。
            // 写成 [] 会被严格 validator 判为 EMPTY_ENTITY。
            if (_materials.Count > 0)
            {
                document["materials"] = _materials;
            }
            if (_skins.Count > 0)
            {
                document["skins"] = _skins;
            }

            // glTF 不要求没有贴图时写 images/textures: []。
            // 空实体会让严格 validator 报错；无贴图应由模型质量报告标 warning。
            if (_images.Count > 0)
            {
                document["images"] = _images;
            }
            if (_textures.Count > 0)
            {
                document["textures"] = _textures;
            }

            // glTF 不要求没有动画时写 animations: []。
            // 部分严格查看器/validator 会把空实体当错误，直接导致场景加载失败。
            if (_animations.Count > 0)
            {
                document["animations"] = _animations;
            }

            return document;
        }

        private void WriteJson(string path, Dictionary<string, object> gltf)
        {
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(SanitizeJsonValue(gltf), jsonOptions));
        }

        private void WriteMaterialReport(Dictionary<string, object> gltf)
        {
            if (!_materials.Any())
            {
                return;
            }

            var path = Path.Combine(_directory, "MATERIAL_REPORT.md");
            var sb = new StringBuilder();
            sb.AppendLine("# 材质说明");
            sb.AppendLine();
            sb.AppendLine($"模型: `{Path.GetFileName(_path)}`");
            sb.AppendLine();
            sb.AppendLine("这份说明面向人工查看。机器可读的完整信息仍保存在 glTF `materials[].extras.unityMaterial` 和 `materials[].extras.animeStudioMaterial`。");
            sb.AppendLine();

            for (var i = 0; i < _materials.Count; i++)
            {
                var material = _materials[i];
                var name = GetString(material, "name") ?? $"Material_{i}";
                sb.AppendLine($"## {i}. {name}");
                sb.AppendLine();
                AppendPbrSummary(sb, material);
                AppendAnimeStudioMaterialSummary(sb, material);
                AppendUnityMaterialSummary(sb, material);
                sb.AppendLine();
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        private static void AppendPbrSummary(StringBuilder sb, Dictionary<string, object> material)
        {
            if (!TryGetDictionary(material, "pbrMetallicRoughness", out var pbr))
            {
                return;
            }

            if (TryGetDictionary(pbr, "baseColorTexture", out var baseColorTexture)
                && TryGetInt(baseColorTexture, "index", out var baseColorTextureIndex))
            {
                sb.AppendLine($"- 基础贴图: texture #{baseColorTextureIndex}");
            }
            if (TryGetFloatArray(pbr, "baseColorFactor", out var color) && color.Length >= 4)
            {
                sb.AppendLine($"- 基础颜色: `{color[0]:0.###}, {color[1]:0.###}, {color[2]:0.###}, {color[3]:0.###}`");
            }
            if (GetString(material, "alphaMode") is { } alphaMode)
            {
                sb.AppendLine($"- 透明模式: `{alphaMode}`");
            }
            if (TryGetBool(material, "doubleSided", out var doubleSided) && doubleSided)
            {
                sb.AppendLine("- 双面渲染: 是");
            }
        }

        private static void AppendAnimeStudioMaterialSummary(StringBuilder sb, Dictionary<string, object> material)
        {
            if (!TryGetDictionary(material, "extras", out var extras)
                || !TryGetDictionary(extras, "animeStudioMaterial", out var animeStudioMaterial))
            {
                return;
            }

            var workflow = GetString(animeStudioMaterial, "workflow");
            var status = GetString(animeStudioMaterial, "status");
            if (!string.IsNullOrWhiteSpace(workflow))
            {
                sb.AppendLine($"- AnimeStudio 材质流程: `{workflow}`");
            }
            if (!string.IsNullOrWhiteSpace(status))
            {
                sb.AppendLine($"- 状态: `{status}`");
                if (string.Equals(status, "needsCustomizationTint", StringComparison.Ordinal))
                {
                    sb.AppendLine("- 说明: 发现基础贴图和 mask，但 Unity Material 里没有可直接使用的染色颜色。该模型可能依赖运行时 customization/tint 配置，当前颜色需要后续解析配置或人工修补。");
                    sb.AppendLine("- 建议: 保留当前贴图和 mask；如果需要最终配色，继续查找角色 customization 配置或手动根据 mask 烘焙 base color。");
                }
                else if (string.Equals(status, "bakedPreview", StringComparison.Ordinal))
                {
                    sb.AppendLine("- 说明: 已根据可用 tint 参数生成预览用 base color。");
                }
                else if (string.Equals(status, "tintParametersOnly", StringComparison.Ordinal))
                {
                    sb.AppendLine("- 说明: 找到了 tint 参数，但没有成功生成预览贴图。");
                }
            }
            if (TryGetBool(animeStudioMaterial, "previewTextureAlphaProtected", out var previewTextureAlphaProtected)
                && previewTextureAlphaProtected)
            {
                sb.AppendLine("- 预览保护: 不透明 Unity 材质的基础贴图 alpha 已保护为可见，原始贴图信息保留在 glTF extras。");
            }

            if (TryGetEnumerable(animeStudioMaterial, "maskTextures", out var masks))
            {
                var any = false;
                foreach (var item in masks)
                {
                    if (item is not Dictionary<string, object> mask)
                    {
                        continue;
                    }
                    if (!any)
                    {
                        sb.AppendLine("- Mask 贴图:");
                        any = true;
                    }
                    var slot = GetString(mask, "slot") ?? "unknown";
                    var usage = GetString(mask, "usage") ?? "unknown";
                    var texture = TryGetInt(mask, "texture", out var textureIndex) ? textureIndex.ToString() : "?";
                    sb.AppendLine($"  - `{slot}` -> texture #{texture}, 用途 `{usage}`");
                }
            }

            if (TryGetEnumerable(animeStudioMaterial, "notes", out var notes))
            {
                foreach (var note in notes.OfType<string>())
                {
                    sb.AppendLine($"- 备注: {note}");
                }
            }
        }

        private static void AppendUnityMaterialSummary(StringBuilder sb, Dictionary<string, object> material)
        {
            if (!TryGetDictionary(material, "extras", out var extras)
                || !TryGetDictionary(extras, "unityMaterial", out var unityMaterial))
            {
                return;
            }

            if (TryGetEnumerable(unityMaterial, "textures", out var textures))
            {
                var slots = textures
                    .OfType<Dictionary<string, object>>()
                    .Select(x => GetString(x, "slotName"))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (slots.Length > 0)
                {
                    sb.AppendLine($"- Unity 贴图槽: {string.Join(", ", slots.Select(x => $"`{x}`"))}");
                }
            }
        }

        private static string GetString(Dictionary<string, object> dictionary, string key)
        {
            return dictionary.TryGetValue(key, out var value) ? value as string : null;
        }

        private static bool TryGetDictionary(Dictionary<string, object> dictionary, string key, out Dictionary<string, object> value)
        {
            if (dictionary.TryGetValue(key, out var raw) && raw is Dictionary<string, object> typed)
            {
                value = typed;
                return true;
            }

            value = null;
            return false;
        }

        private static bool TryGetEnumerable(Dictionary<string, object> dictionary, string key, out IEnumerable<object> value)
        {
            if (dictionary.TryGetValue(key, out var raw) && raw is IEnumerable<object> typed)
            {
                value = typed;
                return true;
            }

            value = null;
            return false;
        }

        private static bool TryGetInt(Dictionary<string, object> dictionary, string key, out int value)
        {
            if (dictionary.TryGetValue(key, out var raw))
            {
                switch (raw)
                {
                    case int intValue:
                        value = intValue;
                        return true;
                    case long longValue:
                        value = (int)longValue;
                        return true;
                }
            }

            value = 0;
            return false;
        }

        private static bool TryGetBool(Dictionary<string, object> dictionary, string key, out bool value)
        {
            if (dictionary.TryGetValue(key, out var raw) && raw is bool boolValue)
            {
                value = boolValue;
                return true;
            }

            value = false;
            return false;
        }

        private static bool TryGetFloatArray(Dictionary<string, object> dictionary, string key, out float[] value)
        {
            if (dictionary.TryGetValue(key, out var raw) && raw is float[] floatArray)
            {
                value = floatArray;
                return true;
            }

            value = null;
            return false;
        }

        private void WriteGlb(Dictionary<string, object> gltf)
        {
            var json = JsonSerializer.Serialize(SanitizeJsonValue(gltf), new JsonSerializerOptions { WriteIndented = false });
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

        private static object SanitizeJsonValue(object value)
        {
            switch (value)
            {
                case null:
                case string:
                case bool:
                case int:
                case long:
                case uint:
                case ulong:
                    return value;
                case float f:
                    return IsFinite(f) ? f : 0.0f;
                case double d:
                    return double.IsNaN(d) || double.IsInfinity(d) ? 0.0d : d;
                case Dictionary<string, object> dictionary:
                    return dictionary.ToDictionary(kv => kv.Key, kv => SanitizeJsonValue(kv.Value));
                case IDictionary dictionary:
                    // Unity 原始材质/诊断数据可能带 Dictionary<string, float>。
                    // glTF JSON 不允许 NaN/Infinity，这里统一清洗所有字典值，避免单个素材写出失败。
                    var result = new Dictionary<string, object>();
                    foreach (var item in dictionary)
                    {
                        if (item is DictionaryEntry entry)
                        {
                            result[Convert.ToString(entry.Key) ?? string.Empty] = SanitizeJsonValue(entry.Value);
                            continue;
                        }

                        var type = item.GetType();
                        var keyProperty = type.GetProperty("Key");
                        var valueProperty = type.GetProperty("Value");
                        if (keyProperty != null && valueProperty != null)
                        {
                            result[Convert.ToString(keyProperty.GetValue(item)) ?? string.Empty] = SanitizeJsonValue(valueProperty.GetValue(item));
                        }
                    }
                    return result;
                case IEnumerable<object> objects:
                    return objects.Select(SanitizeJsonValue).ToArray();
                case IEnumerable enumerable:
                    return enumerable.Cast<object>().Select(SanitizeJsonValue).ToArray();
                default:
                    return value;
            }
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
            // 文件系统允许空格和这些字符，但 glTF image.uri 会按 URI 语义解析。
            // 尤其 # 会被当成 fragment；空格也可能在查看器路径转换中踩坑。
            name = Regex.Replace(name, @"\s+", "_");
            foreach (var c in new[] { '#', '?', '%', '[', ']' })
            {
                name = name.Replace(c, '_');
            }
            return name.Length > 100 ? name.Substring(0, 67) + "_" + name.Substring(name.Length - 32) : name;
        }
    }
}
