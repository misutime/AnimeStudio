using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AnimeStudio.CLI
{
    internal static class TexturePostProcessor
    {
        private static readonly JsonSerializerOptions WriteJsonOptions = new()
        {
            WriteIndented = true,
        };

        public static void ConvertModelTextures(
            string gltfPath,
            string assetRoot,
            string outputDirectory,
            ImageFormat imageFormat,
            bool updateGltfTextureRefs
        )
        {
            gltfPath = Path.GetFullPath(gltfPath);
            if (!File.Exists(gltfPath))
            {
                Logger.Error($"glTF file not found: {gltfPath}");
                return;
            }

            var gltf = JsonNode.Parse(File.ReadAllText(gltfPath))?.AsObject();
            if (gltf == null)
            {
                Logger.Error($"Invalid glTF json: {gltfPath}");
                return;
            }

            assetRoot = string.IsNullOrWhiteSpace(assetRoot)
                ? InferAssetRoot(gltfPath)
                : Path.GetFullPath(assetRoot);
            if (string.IsNullOrWhiteSpace(assetRoot))
            {
                Logger.Error("Unable to infer texture asset root. Pass --texture_asset_root pointing at the previous export root.");
                return;
            }

            var sourceTextureDirectory = Path.Combine(assetRoot, "Textures", "_ModelDependencies");
            if (!Directory.Exists(sourceTextureDirectory))
            {
                Logger.Error($"Raw texture directory not found: {sourceTextureDirectory}");
                return;
            }

            outputDirectory = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(Path.GetDirectoryName(gltfPath) ?? Directory.GetCurrentDirectory(), "Textures")
                : Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(outputDirectory);

            var textureRefs = FindUnityTextureRefs(gltf).ToList();
            if (textureRefs.Count == 0)
            {
                Logger.Info("No materials[].extras.unityTextures entries found.");
                return;
            }

            var converted = 0;
            var failed = 0;
            var textureIndexByPath = BuildExistingTextureIndexMap(gltf, gltfPath);
            foreach (var textureRef in textureRefs)
            {
                if (string.IsNullOrWhiteSpace(textureRef.ExportName))
                {
                    failed++;
                    continue;
                }

                if (!TryResolveRawTexture(sourceTextureDirectory, textureRef.ExportName, out var rawPath, out var metadataPath))
                {
                    Logger.Warning($"Raw texture not found: {textureRef.ExportName}");
                    failed++;
                    continue;
                }

                if (!TryConvertRawTexture(rawPath, metadataPath, outputDirectory, imageFormat, out var imagePath))
                {
                    Logger.Warning($"Unable to convert raw texture: {rawPath}");
                    failed++;
                    continue;
                }

                converted++;
                if (updateGltfTextureRefs)
                {
                    var textureIndex = GetOrAddGltfTexture(gltf, gltfPath, imagePath, textureIndexByPath);
                    ApplyTextureReference(textureRef.Material, textureRef.Slot, textureIndex);
                }
            }

            if (updateGltfTextureRefs && converted > 0)
            {
                File.WriteAllText(gltfPath, gltf.ToJsonString(WriteJsonOptions));
            }

            Logger.Info($"Converted {converted} texture(s) to {outputDirectory}. Failed: {failed}.");
            if (updateGltfTextureRefs && converted > 0)
            {
                Logger.Info($"Updated glTF texture references: {gltfPath}");
            }
        }

        private static IEnumerable<TextureRef> FindUnityTextureRefs(JsonObject gltf)
        {
            var materials = gltf["materials"] as JsonArray;
            if (materials == null)
            {
                yield break;
            }

            foreach (var materialNode in materials)
            {
                if (materialNode is not JsonObject material)
                {
                    continue;
                }

                var unityTextures = material["extras"]?["unityTextures"] as JsonArray;
                if (unityTextures == null)
                {
                    continue;
                }

                foreach (var textureNode in unityTextures)
                {
                    if (textureNode is not JsonObject texture)
                    {
                        continue;
                    }

                    yield return new TextureRef(
                        material,
                        texture["slot"]?.GetValue<int>() ?? -1,
                        texture["exportName"]?.GetValue<string>()
                    );
                }
            }
        }

        private static bool TryResolveRawTexture(string textureDirectory, string exportName, out string rawPath, out string metadataPath)
        {
            var safeName = GetSafeTextureFileName(Path.GetFileNameWithoutExtension(exportName));
            rawPath = Path.Combine(textureDirectory, safeName + ".rawtex");
            metadataPath = rawPath + ".json";
            if (File.Exists(rawPath) && File.Exists(metadataPath))
            {
                return true;
            }

            var directPath = Path.Combine(textureDirectory, Path.GetFileName(exportName));
            if (File.Exists(directPath) && File.Exists(directPath + ".json"))
            {
                rawPath = directPath;
                metadataPath = directPath + ".json";
                return true;
            }

            rawPath = Directory.EnumerateFiles(textureDirectory, "*.rawtex", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(x => string.Equals(Path.GetFileNameWithoutExtension(x), safeName, StringComparison.OrdinalIgnoreCase));
            metadataPath = rawPath == null ? null : rawPath + ".json";
            return rawPath != null && File.Exists(metadataPath);
        }

        private static bool TryConvertRawTexture(string rawPath, string metadataPath, string outputDirectory, ImageFormat imageFormat, out string imagePath)
        {
            imagePath = null;
            var metadata = JsonNode.Parse(File.ReadAllText(metadataPath))?.AsObject();
            if (metadata == null)
            {
                return false;
            }

            var formatText = metadata["format"]?.GetValue<string>();
            if (!Enum.TryParse<TextureFormat>(formatText, ignoreCase: true, out var textureFormat))
            {
                return false;
            }

            var width = metadata["width"]?.GetValue<int>() ?? 0;
            var height = metadata["height"]?.GetValue<int>() ?? 0;
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            var version = ParseUnityVersion(metadata["unityVersion"]?.GetValue<string>());
            var platform = ParsePlatform(metadata["platform"]?.GetValue<string>());
            var rawData = File.ReadAllBytes(rawPath);
            using var stream = Texture2DExtensions.ConvertRawTextureToStream(
                rawData,
                width,
                height,
                textureFormat,
                imageFormat,
                flip: true,
                version,
                platform
            );
            if (stream == null)
            {
                return false;
            }

            var outputName = GetSafeTextureFileName(Path.GetFileNameWithoutExtension(rawPath)) + GetImageExtension(imageFormat);
            imagePath = Path.Combine(outputDirectory, outputName);
            File.WriteAllBytes(imagePath, stream.ToArray());
            return true;
        }

        private static int GetOrAddGltfTexture(JsonObject gltf, string gltfPath, string imagePath, Dictionary<string, int> textureIndexByPath)
        {
            if (textureIndexByPath.TryGetValue(imagePath, out var existing))
            {
                return existing;
            }

            var images = EnsureArray(gltf, "images");
            var textures = EnsureArray(gltf, "textures");
            var imageIndex = images.Count;
            images.Add(new JsonObject
            {
                ["uri"] = ToUri(Path.GetRelativePath(Path.GetDirectoryName(gltfPath) ?? Directory.GetCurrentDirectory(), imagePath)),
            });

            var textureIndex = textures.Count;
            textures.Add(new JsonObject
            {
                ["source"] = imageIndex,
            });
            textureIndexByPath[imagePath] = textureIndex;
            return textureIndex;
        }

        private static Dictionary<string, int> BuildExistingTextureIndexMap(JsonObject gltf, string gltfPath)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var images = gltf["images"] as JsonArray;
            var textures = gltf["textures"] as JsonArray;
            if (images == null || textures == null)
            {
                return result;
            }

            var gltfDirectory = Path.GetDirectoryName(gltfPath) ?? Directory.GetCurrentDirectory();
            for (var textureIndex = 0; textureIndex < textures.Count; textureIndex++)
            {
                if (textures[textureIndex] is not JsonObject texture)
                {
                    continue;
                }

                var sourceIndex = texture["source"]?.GetValue<int>() ?? -1;
                if (sourceIndex < 0 || sourceIndex >= images.Count || images[sourceIndex] is not JsonObject image)
                {
                    continue;
                }

                var uri = image["uri"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(uri) || Uri.TryCreate(uri, UriKind.Absolute, out _))
                {
                    continue;
                }

                var imagePath = Path.GetFullPath(Path.Combine(gltfDirectory, uri.Replace('/', Path.DirectorySeparatorChar)));
                result.TryAdd(imagePath, textureIndex);
            }
            return result;
        }

        private static void ApplyTextureReference(JsonObject material, int slot, int textureIndex)
        {
            if (slot == 0)
            {
                var pbr = material["pbrMetallicRoughness"] as JsonObject;
                if (pbr == null)
                {
                    pbr = new JsonObject();
                    material["pbrMetallicRoughness"] = pbr;
                }
                pbr["baseColorTexture"] = new JsonObject { ["index"] = textureIndex };
            }
            else if (slot == 1 || slot == 3)
            {
                material["normalTexture"] = new JsonObject { ["index"] = textureIndex };
            }
            else if (slot == 5)
            {
                material["emissiveTexture"] = new JsonObject { ["index"] = textureIndex };
            }
        }

        private static JsonArray EnsureArray(JsonObject obj, string name)
        {
            if (obj[name] is JsonArray array)
            {
                return array;
            }

            array = new JsonArray();
            obj[name] = array;
            return array;
        }

        private static string InferAssetRoot(string gltfPath)
        {
            var directory = new DirectoryInfo(Path.GetDirectoryName(gltfPath) ?? Directory.GetCurrentDirectory());
            while (directory != null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "Textures", "_ModelDependencies")))
                {
                    return directory.FullName;
                }
                directory = directory.Parent;
            }
            return null;
        }

        private static int[] ParseUnityVersion(string unityVersion)
        {
            if (string.IsNullOrWhiteSpace(unityVersion))
            {
                return new[] { 2020, 1, 0 };
            }

            var parts = unityVersion
                .Split(new[] { '.', 'f', 'p', 'b' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => int.TryParse(x, out var value) ? value : 0)
                .ToArray();
            if (parts.Length >= 2)
            {
                return parts;
            }
            return new[] { 2020, 1, 0 };
        }

        private static BuildTarget ParsePlatform(string platform)
        {
            return Enum.TryParse<BuildTarget>(platform, ignoreCase: true, out var buildTarget)
                ? buildTarget
                : BuildTarget.NoTarget;
        }

        private static string GetImageExtension(ImageFormat imageFormat)
        {
            return imageFormat switch
            {
                ImageFormat.Jpeg => ".jpg",
                ImageFormat.Bmp => ".bmp",
                ImageFormat.Tga => ".tga",
                ImageFormat.Hdr => ".hdr",
                _ => ".png",
            };
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
            name = Regex.Replace(name, @"\s+", "_");
            foreach (var c in new[] { '#', '?', '%', '[', ']' })
            {
                name = name.Replace(c, '_');
            }
            return name.Length > 100 ? name.Substring(0, 67) + "_" + name.Substring(name.Length - 32) : name;
        }

        private sealed record TextureRef(JsonObject Material, int Slot, string ExportName);
    }
}
