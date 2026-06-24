using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AnimeStudio.CLI
{
    internal static class ModelLibraryValidator
    {
        public static JObject ValidateSingleModelForAnimationGate(string gltfPath)
        {
            var report = ValidateModel(gltfPath);
            var body = JObject.FromObject(report.Body ?? new ModelBodyReport());
            var result = JObject.FromObject(report);
            result["animationGate"] = new JObject
            {
                ["ready"] = string.Equals(report.Status, "ok", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(report.Body?.ModelBodyStatus, "ok", StringComparison.OrdinalIgnoreCase),
                ["status"] = string.Equals(report.Status, "ok", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(report.Body?.ModelBodyStatus, "ok", StringComparison.OrdinalIgnoreCase)
                        ? "ready"
                        : "blocked",
                ["reasons"] = BuildAnimationGateReasons(report, body),
                ["rule"] = "模型 glTF 单独通过 Mesh/UV/材质/贴图/skin/bbox 静态验收后，才允许进入动画导出、合成或生产 smoke。",
            };
            return result;
        }

        public static void Generate(string savePath)
        {
            if (string.IsNullOrWhiteSpace(savePath))
            {
                return;
            }

            var modelsRoot = Path.Combine(savePath, "Models");
            var modelFiles = Directory.Exists(modelsRoot)
                ? Directory.EnumerateFiles(modelsRoot, "*.gltf", SearchOption.AllDirectories).OrderBy(x => x).ToArray()
                : Array.Empty<string>();

            var reports = modelFiles.Select(ValidateModel).ToArray();
            var summary = new
            {
                generatedAt = DateTime.UtcNow.ToString("O"),
                rule = "本报告只验证模型本体、贴图、材质和 skin 结构。动画必须在模型、UV、贴图、骨骼和 bbox 可信之后再单独验收。",
                totals = new
                {
                    models = reports.Length,
                    ok = reports.Count(x => x.Status == "ok"),
                    warning = reports.Count(x => x.Status == "warning"),
                    error = reports.Count(x => x.Status == "error"),
                    modelBodyOk = reports.Count(x => x.Body?.ModelBodyStatus == "ok"),
                    modelBodyWarning = reports.Count(x => x.Body?.ModelBodyStatus == "warning"),
                    modelBodyError = reports.Count(x => x.Body?.ModelBodyStatus == "error"),
                    withSkin = reports.Count(x => x.Counts.Skins > 0),
                    withTextures = reports.Count(x => x.Counts.Images > 0),
                },
                models = reports,
            };

            File.WriteAllText(
                Path.Combine(savePath, "model_validation.json"),
                JsonConvert.SerializeObject(summary, Newtonsoft.Json.Formatting.Indented)
            );
        }

        private static JArray BuildAnimationGateReasons(ModelReport report, JObject body)
        {
            var reasons = new List<string>();
            if (!string.Equals(report?.Status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("model_validation_not_ok");
            }
            if (!string.Equals(report?.Body?.ModelBodyStatus, "ok", StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("model_body_not_ok");
            }
            if ((int?)body["PrimitiveCount"] <= 0)
            {
                reasons.Add("no_mesh");
            }
            if ((long?)body["PositionVertexCount"] <= 0)
            {
                reasons.Add("no_vertices");
            }
            if ((int?)body["MaterialCount"] <= 0)
            {
                reasons.Add("no_materials");
            }
            if ((int?)body["PrimitiveCount"] > 0
                && (int?)body["PrimitivesWithBaseColorTexture"] * 2 < (int?)body["PrimitiveCount"])
            {
                reasons.Add("insufficient_base_color_texture_coverage");
            }
            if ((int?)body["TextureCount"] <= 0)
            {
                reasons.Add("no_textures");
            }
            if ((int?)body["ImageCount"] <= 0)
            {
                reasons.Add("no_material_images");
            }
            if ((int?)body["MissingImageCount"] > 0)
            {
                reasons.Add("missing_images");
            }
            if ((int?)body["EmptyImageCount"] > 0)
            {
                reasons.Add("empty_images");
            }
            if ((int?)body["SkinnedMeshNodeCount"] > 0
                && body["HasCompleteSkinBinding"]?.Type == JTokenType.Boolean
                && !body["HasCompleteSkinBinding"].Value<bool>())
            {
                reasons.Add("incomplete_skin_binding");
            }

            return new JArray(reasons.Distinct(StringComparer.OrdinalIgnoreCase));
        }

        private static ModelReport ValidateModel(string gltfPath)
        {
            var notes = new List<string>();
            try
            {
                var gltf = JObject.Parse(File.ReadAllText(gltfPath));
                var directory = Path.GetDirectoryName(Path.GetFullPath(gltfPath)) ?? Directory.GetCurrentDirectory();
                var nodes = ArrayOf(gltf, "nodes");
                var meshes = ArrayOf(gltf, "meshes");
                var materials = ArrayOf(gltf, "materials");
                var textures = ArrayOf(gltf, "textures");
                var images = ArrayOf(gltf, "images");
                var skins = ArrayOf(gltf, "skins");
                var accessors = ArrayOf(gltf, "accessors");
                var buffers = ArrayOf(gltf, "buffers");

                ValidateImages(images, directory, notes, out var missingImages, out var emptyImages);
                ValidateBuffers(buffers, directory, notes, out var missingBuffers, out var emptyBuffers);
                ValidateTextures(textures, images.Length, notes);
                ValidateMaterials(materials, textures.Length, notes);
                ValidateMeshes(meshes, materials.Length, accessors, notes);
                ValidateSkins(skins, nodes.Length, accessors, notes);
                ValidateMeshSkinNodes(nodes, meshes, skins.Length, notes);
                var body = BuildModelBodyReport(
                    nodes,
                    meshes,
                    materials,
                    textures,
                    images,
                    skins,
                    accessors,
                    missingImages,
                    emptyImages,
                    missingBuffers,
                    emptyBuffers);

                if (meshes.Length == 0)
                {
                    notes.Add("No mesh was written.");
                }
                if (meshes.Length > 0 && materials.Length == 0)
                {
                    notes.Add("Model has visible mesh primitives but no glTF materials; it is not ready as a reusable textured asset.");
                }
                if (materials.Length > 0 && textures.Length == 0)
                {
                    notes.Add("Materials exist but no standard glTF textures are referenced.");
                }
                if (textures.Length > 0 && images.Length == 0)
                {
                    notes.Add("Textures exist but no glTF images are referenced.");
                }

                var status = body.ModelBodyStatus == "error"
                    ? "error"
                    : notes.Count == 0 && body.ModelBodyStatus == "ok" ? "ok" : "warning";
                return new ModelReport
                {
                    Status = status,
                    Path = gltfPath,
                    Counts = new ModelCounts(nodes.Length, meshes.Length, materials.Length, textures.Length, images.Length, skins.Length),
                    Body = body,
                    Notes = notes.ToArray(),
                };
            }
            catch (Exception ex)
            {
                return new ModelReport
                {
                    Status = "error",
                    Path = gltfPath,
                    Counts = new ModelCounts(0, 0, 0, 0, 0, 0),
                    Body = new ModelBodyReport
                    {
                        ModelBodyStatus = "error",
                        Evidence = new[] { "glTF 文件无法解析，模型本体验证失败。" },
                    },
                    Notes = new[] { ex.Message },
                };
            }
        }

        private static JObject[] ArrayOf(JObject gltf, string name)
        {
            return gltf[name]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
        }

        private static void ValidateImages(JObject[] images, string directory, List<string> notes, out int missingImages, out int emptyImages)
        {
            missingImages = 0;
            emptyImages = 0;
            for (var i = 0; i < images.Length; i++)
            {
                var uri = (string)images[i]["uri"];
                if (string.IsNullOrWhiteSpace(uri))
                {
                    notes.Add($"image[{i}] has no uri.");
                    continue;
                }

                if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var path = Path.Combine(directory, uri.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                {
                    missingImages++;
                    notes.Add($"image[{i}] file is missing: {uri}");
                    continue;
                }

                if (new FileInfo(path).Length == 0)
                {
                    emptyImages++;
                    notes.Add($"image[{i}] file is empty: {uri}");
                }
            }
        }

        private static void ValidateBuffers(JObject[] buffers, string directory, List<string> notes, out int missingBuffers, out int emptyBuffers)
        {
            missingBuffers = 0;
            emptyBuffers = 0;
            for (var i = 0; i < buffers.Length; i++)
            {
                var uri = (string)buffers[i]["uri"];
                if (string.IsNullOrWhiteSpace(uri) || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var path = Path.Combine(directory, uri.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                {
                    missingBuffers++;
                    notes.Add($"buffer[{i}] file is missing: {uri}");
                    continue;
                }

                if (new FileInfo(path).Length == 0)
                {
                    emptyBuffers++;
                    notes.Add($"buffer[{i}] file is empty: {uri}");
                }
            }
        }

        private static void ValidateTextures(JObject[] textures, int imageCount, List<string> notes)
        {
            for (var i = 0; i < textures.Length; i++)
            {
                var source = (int?)textures[i]["source"];
                if (source == null || source < 0 || source >= imageCount)
                {
                    notes.Add($"texture[{i}] points to invalid image source {source}.");
                }
            }
        }

        private static void ValidateMaterials(JObject[] materials, int textureCount, List<string> notes)
        {
            for (var i = 0; i < materials.Length; i++)
            {
                CheckMaterialTexture(materials[i], textureCount, notes, $"material[{i}].pbrMetallicRoughness.baseColorTexture", "pbrMetallicRoughness", "baseColorTexture");
                CheckMaterialTexture(materials[i], textureCount, notes, $"material[{i}].normalTexture", null, "normalTexture");
                CheckMaterialTexture(materials[i], textureCount, notes, $"material[{i}].emissiveTexture", null, "emissiveTexture");
            }
        }

        private static void CheckMaterialTexture(JObject material, int textureCount, List<string> notes, string label, string parentName, string textureName)
        {
            var parent = string.IsNullOrWhiteSpace(parentName) ? material : material[parentName] as JObject;
            var texture = parent?[textureName] as JObject;
            if (texture == null)
            {
                return;
            }

            var index = (int?)texture["index"];
            if (index == null || index < 0 || index >= textureCount)
            {
                notes.Add($"{label} points to invalid texture index {index}.");
            }
        }

        private static void ValidateMeshes(JObject[] meshes, int materialCount, JObject[] accessors, List<string> notes)
        {
            var accessorCount = accessors.Length;
            for (var meshIndex = 0; meshIndex < meshes.Length; meshIndex++)
            {
                var primitives = meshes[meshIndex]["primitives"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
                if (primitives.Length == 0)
                {
                    notes.Add($"mesh[{meshIndex}] has no primitives.");
                }

                for (var primitiveIndex = 0; primitiveIndex < primitives.Length; primitiveIndex++)
                {
                    var primitive = primitives[primitiveIndex];
                    var attributes = primitive["attributes"] as JObject;
                    CheckAccessor(attributes, "POSITION", accessorCount, notes, $"mesh[{meshIndex}].primitive[{primitiveIndex}].POSITION");
                    CheckAccessor(attributes, "NORMAL", accessorCount, notes, $"mesh[{meshIndex}].primitive[{primitiveIndex}].NORMAL");
                    CheckAccessor(attributes, "TEXCOORD_0", accessorCount, notes, $"mesh[{meshIndex}].primitive[{primitiveIndex}].TEXCOORD_0", required: false);
                    CheckAccessor(attributes, "JOINTS_0", accessorCount, notes, $"mesh[{meshIndex}].primitive[{primitiveIndex}].JOINTS_0", required: false);
                    CheckAccessor(attributes, "WEIGHTS_0", accessorCount, notes, $"mesh[{meshIndex}].primitive[{primitiveIndex}].WEIGHTS_0", required: false);
                    ValidatePrimitiveAccessorCounts(attributes, accessors, notes, $"mesh[{meshIndex}].primitive[{primitiveIndex}]");

                    var material = (int?)primitive["material"];
                    if (material != null && (material < 0 || material >= materialCount))
                    {
                        notes.Add($"mesh[{meshIndex}].primitive[{primitiveIndex}] points to invalid material {material}.");
                    }
                }
            }
        }

        private static void ValidatePrimitiveAccessorCounts(JObject attributes, JObject[] accessors, List<string> notes, string label)
        {
            var position = GetAccessorIndex(attributes, "POSITION", accessors.Length);
            var normal = GetAccessorIndex(attributes, "NORMAL", accessors.Length);
            var uv0 = GetAccessorIndex(attributes, "TEXCOORD_0", accessors.Length);
            var joints = GetAccessorIndex(attributes, "JOINTS_0", accessors.Length);
            var weights = GetAccessorIndex(attributes, "WEIGHTS_0", accessors.Length);
            var vertexCount = GetAccessorCount(accessors, position);

            CompareAccessorCount(accessors, normal, vertexCount, notes, $"{label}.NORMAL");
            CompareAccessorCount(accessors, uv0, vertexCount, notes, $"{label}.TEXCOORD_0");
            CompareAccessorCount(accessors, joints, vertexCount, notes, $"{label}.JOINTS_0");
            CompareAccessorCount(accessors, weights, vertexCount, notes, $"{label}.WEIGHTS_0");

            if (joints.HasValue != weights.HasValue)
            {
                notes.Add($"{label} has only one of JOINTS_0/WEIGHTS_0.");
            }
        }

        private static void CompareAccessorCount(JObject[] accessors, int? accessorIndex, int? vertexCount, List<string> notes, string label)
        {
            if (!accessorIndex.HasValue || !vertexCount.HasValue)
            {
                return;
            }

            var count = GetAccessorCount(accessors, accessorIndex);
            if (count.HasValue && count.Value != vertexCount.Value)
            {
                notes.Add($"{label} count {count.Value} does not match POSITION count {vertexCount.Value}.");
            }
        }

        private static void CheckAccessor(JObject attributes, string name, int accessorCount, List<string> notes, string label, bool required = true)
        {
            var value = (int?)attributes?[name];
            if (value == null)
            {
                if (required)
                {
                    notes.Add($"{label} is missing.");
                }
                return;
            }

            if (value < 0 || value >= accessorCount)
            {
                notes.Add($"{label} points to invalid accessor {value}.");
            }
        }

        private static void ValidateSkins(JObject[] skins, int nodeCount, JObject[] accessors, List<string> notes)
        {
            for (var i = 0; i < skins.Length; i++)
            {
                var joints = skins[i]["joints"]?.Select(x => (int?)x).ToArray() ?? Array.Empty<int?>();
                if (joints.Length == 0)
                {
                    notes.Add($"skin[{i}] has no joints.");
                }

                foreach (var joint in joints)
                {
                    if (joint == null || joint < 0 || joint >= nodeCount)
                    {
                        notes.Add($"skin[{i}] has invalid joint node {joint}.");
                    }
                }

                var inverseBindMatrices = (int?)skins[i]["inverseBindMatrices"];
                if (inverseBindMatrices == null || inverseBindMatrices < 0 || inverseBindMatrices >= accessors.Length)
                {
                    notes.Add($"skin[{i}] has invalid inverseBindMatrices accessor {inverseBindMatrices}.");
                    continue;
                }

                var matrixCount = (int?)accessors[inverseBindMatrices.Value]["count"] ?? 0;
                if (matrixCount != joints.Length)
                {
                    notes.Add($"skin[{i}] inverseBindMatrices count {matrixCount} does not match joints count {joints.Length}.");
                }
            }
        }

        private static ModelBodyReport BuildModelBodyReport(
            JObject[] nodes,
            JObject[] meshes,
            JObject[] materials,
            JObject[] textures,
            JObject[] images,
            JObject[] skins,
            JObject[] accessors,
            int missingImages,
            int emptyImages,
            int missingBuffers,
            int emptyBuffers)
        {
            var evidence = new List<string>();
            var primitiveCount = 0;
            var withPosition = 0;
            var withNormal = 0;
            var withUv0 = 0;
            var withMaterial = 0;
            var withBaseColorTexture = 0;
            var withJoints = 0;
            var withWeights = 0;
            var positionVertexCount = 0L;
            var bounds = ModelBounds.Empty();

            foreach (var mesh in meshes)
            {
                foreach (var primitive in mesh["primitives"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    primitiveCount++;
                    var attributes = primitive["attributes"] as JObject;
                    var position = GetAccessorIndex(attributes, "POSITION", accessors.Length);
                    var normal = GetAccessorIndex(attributes, "NORMAL", accessors.Length);
                    var uv0 = GetAccessorIndex(attributes, "TEXCOORD_0", accessors.Length);
                    var joints = GetAccessorIndex(attributes, "JOINTS_0", accessors.Length);
                    var weights = GetAccessorIndex(attributes, "WEIGHTS_0", accessors.Length);

                    if (position.HasValue)
                    {
                        withPosition++;
                        positionVertexCount += GetAccessorCount(accessors, position) ?? 0;
                        bounds.Include(accessors[position.Value]);
                    }
                    if (normal.HasValue)
                    {
                        withNormal++;
                    }
                    if (uv0.HasValue)
                    {
                        withUv0++;
                    }
                    if ((int?)primitive["material"] != null)
                    {
                        withMaterial++;
                    }
                    if (PrimitiveHasBaseColorTexture(primitive, materials, textures.Length))
                    {
                        withBaseColorTexture++;
                    }
                    if (joints.HasValue)
                    {
                        withJoints++;
                    }
                    if (weights.HasValue)
                    {
                        withWeights++;
                    }
                }
            }

            var meshNodes = nodes.Count(x => (int?)x["mesh"] != null);
            var skinnedMeshNodes = nodes.Count(x => (int?)x["mesh"] != null && (int?)x["skin"] != null);
            var skinJointCount = skins.Sum(x => x["joints"]?.Count() ?? 0);
            var hasSkinAttributes = withJoints > 0 || withWeights > 0;
            var hasCompleteSkinBinding = skins.Length > 0 && skinnedMeshNodes > 0 && withJoints == withWeights && withJoints > 0;

            if (primitiveCount == 0)
            {
                evidence.Add("没有可用 primitive。");
            }
            if (withPosition != primitiveCount)
            {
                evidence.Add($"POSITION 覆盖 {withPosition}/{primitiveCount} 个 primitive。");
            }
            if (withNormal != primitiveCount)
            {
                evidence.Add($"NORMAL 覆盖 {withNormal}/{primitiveCount} 个 primitive。");
            }
            if (withUv0 != primitiveCount)
            {
                evidence.Add($"UV0 覆盖 {withUv0}/{primitiveCount} 个 primitive。");
            }
            if (primitiveCount > 0 && withMaterial != primitiveCount)
            {
                evidence.Add($"材质槽覆盖 {withMaterial}/{primitiveCount} 个 primitive。");
            }
            if (primitiveCount > 0 && withBaseColorTexture * 2 < primitiveCount)
            {
                evidence.Add($"基础贴图覆盖 {withBaseColorTexture}/{primitiveCount} 个 primitive；主要可见面仍可能是默认色或 mask/tint 未复原状态。");
            }
            // 模型阶段必须先确认材质和贴图链路；白模不能再被当作模型本体 OK。
            if (primitiveCount > 0 && materials.Length == 0)
            {
                evidence.Add("模型有可见几何，但没有写出 glTF 材质。");
            }
            if (materials.Length > 0 && textures.Length == 0)
            {
                evidence.Add("材质存在，但没有标准 glTF 贴图引用。");
            }
            if (textures.Length > 0 && images.Length == 0)
            {
                evidence.Add("贴图对象存在，但没有标准 glTF 图片引用。");
            }
            if (hasSkinAttributes && !hasCompleteSkinBinding)
            {
                evidence.Add("primitive 含 JOINTS/WEIGHTS，但 glTF skin 或 mesh node skin 绑定不完整。");
            }
            if (!bounds.IsValid)
            {
                evidence.Add("POSITION accessor 没有有效 min/max，bbox 无法验收。");
            }
            if (missingImages > 0 || emptyImages > 0 || missingBuffers > 0 || emptyBuffers > 0)
            {
                evidence.Add($"外部文件异常：missingImages={missingImages}, emptyImages={emptyImages}, missingBuffers={missingBuffers}, emptyBuffers={emptyBuffers}。");
            }

            var hasError = primitiveCount == 0
                || withPosition != primitiveCount
                || withNormal != primitiveCount
                || !bounds.IsValid
                || missingImages > 0
                || emptyImages > 0
                || missingBuffers > 0
                || emptyBuffers > 0;
            var hasWarning = evidence.Count > 0;

            return new ModelBodyReport
            {
                ModelBodyStatus = hasError ? "error" : hasWarning ? "warning" : "ok",
                PrimitiveCount = primitiveCount,
                PositionVertexCount = positionVertexCount,
                PrimitivesWithPosition = withPosition,
                PrimitivesWithNormal = withNormal,
                PrimitivesWithUv0 = withUv0,
                PrimitivesWithMaterial = withMaterial,
                PrimitivesWithBaseColorTexture = withBaseColorTexture,
                PrimitivesWithJoints0 = withJoints,
                PrimitivesWithWeights0 = withWeights,
                MeshNodeCount = meshNodes,
                SkinnedMeshNodeCount = skinnedMeshNodes,
                SkinCount = skins.Length,
                SkinJointCount = skinJointCount,
                HasCompleteSkinBinding = hasCompleteSkinBinding,
                MaterialCount = materials.Length,
                TextureCount = textures.Length,
                ImageCount = images.Length,
                MissingImageCount = missingImages,
                EmptyImageCount = emptyImages,
                MissingBufferCount = missingBuffers,
                EmptyBufferCount = emptyBuffers,
                Bounds = bounds.ToReport(),
                Evidence = evidence.ToArray(),
            };
        }

        private static int? GetAccessorIndex(JObject attributes, string name, int accessorCount)
        {
            var value = (int?)attributes?[name];
            return value.HasValue && value.Value >= 0 && value.Value < accessorCount
                ? value.Value
                : null;
        }

        private static bool PrimitiveHasBaseColorTexture(JObject primitive, JObject[] materials, int textureCount)
        {
            var materialIndex = (int?)primitive?["material"];
            if (!materialIndex.HasValue || materialIndex.Value < 0 || materialIndex.Value >= materials.Length)
            {
                return false;
            }

            var textureIndex = (int?)materials[materialIndex.Value]?["pbrMetallicRoughness"]?["baseColorTexture"]?["index"];
            return textureIndex.HasValue && textureIndex.Value >= 0 && textureIndex.Value < textureCount;
        }

        private static int? GetAccessorCount(JObject[] accessors, int? accessorIndex)
        {
            return accessorIndex.HasValue && accessorIndex.Value >= 0 && accessorIndex.Value < accessors.Length
                ? (int?)accessors[accessorIndex.Value]["count"]
                : null;
        }

        private static void ValidateMeshSkinNodes(JObject[] nodes, JObject[] meshes, int skinCount, List<string> notes)
        {
            for (var i = 0; i < nodes.Length; i++)
            {
                var mesh = (int?)nodes[i]["mesh"];
                if (mesh == null)
                {
                    continue;
                }

                if (mesh < 0 || mesh >= meshes.Length)
                {
                    notes.Add($"node[{i}] points to invalid mesh {mesh}.");
                }

                var skin = (int?)nodes[i]["skin"];
                if (skin != null && (skin < 0 || skin >= skinCount))
                {
                    notes.Add($"node[{i}] points to invalid skin {skin}.");
                }
            }
        }

        private sealed class ModelReport
        {
            public string Status { get; set; }
            public string Path { get; set; }
            public ModelCounts Counts { get; set; }
            public ModelBodyReport Body { get; set; }
            public string[] Notes { get; set; }
        }

        private sealed record ModelCounts(int Nodes, int Meshes, int Materials, int Textures, int Images, int Skins);

        private sealed class ModelBodyReport
        {
            public string ModelBodyStatus { get; set; }
            public int PrimitiveCount { get; set; }
            public long PositionVertexCount { get; set; }
            public int PrimitivesWithPosition { get; set; }
            public int PrimitivesWithNormal { get; set; }
            public int PrimitivesWithUv0 { get; set; }
            public int PrimitivesWithMaterial { get; set; }
            public int PrimitivesWithBaseColorTexture { get; set; }
            public int PrimitivesWithJoints0 { get; set; }
            public int PrimitivesWithWeights0 { get; set; }
            public int MeshNodeCount { get; set; }
            public int SkinnedMeshNodeCount { get; set; }
            public int SkinCount { get; set; }
            public int SkinJointCount { get; set; }
            public bool HasCompleteSkinBinding { get; set; }
            public int MaterialCount { get; set; }
            public int TextureCount { get; set; }
            public int ImageCount { get; set; }
            public int MissingImageCount { get; set; }
            public int EmptyImageCount { get; set; }
            public int MissingBufferCount { get; set; }
            public int EmptyBufferCount { get; set; }
            public BoundsReport Bounds { get; set; }
            public string[] Evidence { get; set; }
        }

        private sealed class BoundsReport
        {
            public bool IsValid { get; set; }
            public float[] Min { get; set; }
            public float[] Max { get; set; }
            public float[] Size { get; set; }
        }

        private sealed class ModelBounds
        {
            private readonly float[] _min = { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity };
            private readonly float[] _max = { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };

            public bool IsValid { get; private set; }

            public static ModelBounds Empty()
            {
                return new ModelBounds();
            }

            public void Include(JObject accessor)
            {
                var min = accessor["min"]?.Values<float>().ToArray();
                var max = accessor["max"]?.Values<float>().ToArray();
                if (min == null || min.Length < 3 || max == null || max.Length < 3)
                {
                    return;
                }

                for (var i = 0; i < 3; i++)
                {
                    if (!float.IsFinite(min[i]) || !float.IsFinite(max[i]) || min[i] > max[i])
                    {
                        return;
                    }
                }

                for (var i = 0; i < 3; i++)
                {
                    _min[i] = Math.Min(_min[i], min[i]);
                    _max[i] = Math.Max(_max[i], max[i]);
                }
                IsValid = true;
            }

            public BoundsReport ToReport()
            {
                if (!IsValid)
                {
                    return new BoundsReport
                    {
                        IsValid = false,
                        Min = Array.Empty<float>(),
                        Max = Array.Empty<float>(),
                        Size = Array.Empty<float>(),
                    };
                }

                return new BoundsReport
                {
                    IsValid = true,
                    Min = _min.ToArray(),
                    Max = _max.ToArray(),
                    Size = new[] { _max[0] - _min[0], _max[1] - _min[1], _max[2] - _min[2] },
                };
            }
        }
    }
}
