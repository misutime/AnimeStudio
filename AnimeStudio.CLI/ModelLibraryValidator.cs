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
                rule = "This report validates model, texture, material, and skin structure only. Animation correctness is checked separately after the static asset path is trusted.",
                totals = new
                {
                    models = reports.Length,
                    ok = reports.Count(x => x.Status == "ok"),
                    warning = reports.Count(x => x.Status == "warning"),
                    error = reports.Count(x => x.Status == "error"),
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

                ValidateImages(images, directory, notes);
                ValidateTextures(textures, images.Length, notes);
                ValidateMaterials(materials, textures.Length, notes);
                ValidateMeshes(meshes, materials.Length, accessors.Length, notes);
                ValidateSkins(skins, nodes.Length, accessors, notes);
                ValidateMeshSkinNodes(nodes, meshes, skins.Length, notes);

                if (meshes.Length == 0)
                {
                    notes.Add("No mesh was written.");
                }
                if (materials.Length > 0 && textures.Length == 0)
                {
                    notes.Add("Materials exist but no standard glTF textures are referenced.");
                }

                var status = notes.Count == 0 ? "ok" : "warning";
                return new ModelReport
                {
                    Status = status,
                    Path = gltfPath,
                    Counts = new ModelCounts(nodes.Length, meshes.Length, materials.Length, textures.Length, images.Length, skins.Length),
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
                    Notes = new[] { ex.Message },
                };
            }
        }

        private static JObject[] ArrayOf(JObject gltf, string name)
        {
            return gltf[name]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
        }

        private static void ValidateImages(JObject[] images, string directory, List<string> notes)
        {
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
                    notes.Add($"image[{i}] file is missing: {uri}");
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

        private static void ValidateMeshes(JObject[] meshes, int materialCount, int accessorCount, List<string> notes)
        {
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

                    var material = (int?)primitive["material"];
                    if (material != null && (material < 0 || material >= materialCount))
                    {
                        notes.Add($"mesh[{meshIndex}].primitive[{primitiveIndex}] points to invalid material {material}.");
                    }
                }
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
            public string[] Notes { get; set; }
        }

        private sealed record ModelCounts(int Nodes, int Meshes, int Materials, int Textures, int Images, int Skins);
    }
}
