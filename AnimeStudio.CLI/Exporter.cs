using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace AnimeStudio.CLI
{
    internal static class CliExportOptions
    {
        public static float? FbxScaleFactor { get; set; }
        public static int? FbxBoneSize { get; set; }
        public static bool FbxExportAllNodes { get; set; }
        public static FbxAnimationMode FbxAnimationMode { get; set; } = FbxAnimationMode.Skip;
        public static string HumanoidBakeSolver { get; set; } = "AvatarPreEulerPost";
        public static ModelExportFormat ModelFormat { get; set; } = ModelExportFormat.Gltf;
        public static AnimationPackageMode AnimationPackage { get; set; } = AnimationPackageMode.Separate;
        public static ModelSourceMode ModelSource { get; set; } = ModelSourceMode.PrefabPrimary;
        public static AnimeStudio.TextureExportMode TextureMode { get; set; } = AnimeStudio.TextureExportMode.Raw;
        public static string OutputRoot { get; set; }

        public static bool ExportEmbeddedAnimations =>
            FbxAnimationMode != FbxAnimationMode.Skip
            && AnimationPackage != AnimationPackageMode.Separate;
        public static bool CollectAnimations =>
            FbxAnimationMode == FbxAnimationMode.Auto
            && AnimationPackage != AnimationPackageMode.Separate;
        public static bool ExportSeparateAnimations =>
            AnimationPackage == AnimationPackageMode.Separate
            || AnimationPackage == AnimationPackageMode.Both;
    }

    internal static class Exporter
    {
        private static readonly Dictionary<Material, ImportedMaterial> SharedMaterialCache = new Dictionary<Material, ImportedMaterial>();
        private static readonly Dictionary<Texture2D, ImportedTexture> SharedTextureCache = new Dictionary<Texture2D, ImportedTexture>();
        private static readonly Dictionary<string, List<ImportedVertex>> SharedMeshVertexCache = new Dictionary<string, List<ImportedVertex>>();
        private static readonly Queue<string> SharedMeshVertexCacheOrder = new Queue<string>();

        public static bool ExportTexture2D(AssetItem item, string exportPath)
        {
            var m_Texture2D = (Texture2D)item.Asset;
            if (Properties.Settings.Default.convertTexture)
            {
                var type = Properties.Settings.Default.convertType;
                if (
                    !TryExportFile(
                        exportPath,
                        item,
                        "." + type.ToString().ToLower(),
                        out var exportFullPath
                    )
                )
                    return false;
                var image = m_Texture2D.ConvertToImage(true);
                if (image == null)
                    return false;
                using (image)
                {
                    using (var file = File.OpenWrite(exportFullPath))
                    {
                        image.WriteToStream(file, type);
                    }
                    return true;
                }
            }
            else
            {
                if (!TryExportFile(exportPath, item, ".tex", out var exportFullPath))
                    return false;
                m_Texture2D.image_data.WriteData(exportFullPath);
                return true;
            }
        }

        public static bool ExportTexture2DArray(AssetItem item, string exportPath)
        {
            var textureArray = (Texture2DArray)item.Asset;
            if (!TryExportFolder(exportPath, item, out var exportFolder))
                return false;

            Directory.CreateDirectory(exportFolder);

            var isDataArray = IsDataTexture2DArray(
                textureArray,
                item.Text,
                item.Container,
                item.SourceFile?.originalPath ?? item.SourceFile?.fileName);
            var textureArrayUsage = isDataArray ? "DataTextureArray" : "VisualTextureArray";
            var type = Properties.Settings.Default.convertType;
            var extension = "." + type.ToString().ToLower();
            var exportedLayers = new List<object>();
            var layerIndex = 0;

            if (isDataArray)
            {
                for (var index = 0; index < textureArray.m_Depth; index++)
                {
                    exportedLayers.Add(new
                    {
                        index,
                        name = $"_{index + 1:000}",
                        output = (string)null,
                        status = "metadataOnlyDataArray",
                        textureFormat = textureArray.m_Format.ToTextureFormat().ToString(),
                        note = "PNG preview skipped by default for float/HDR/unknown data Texture2DArray.",
                    });
                }
            }

            foreach (var layer in isDataArray ? Enumerable.Empty<Texture2D>() : textureArray.TextureList)
            {
                layerIndex++;
                var layerName = FixFileName(layer.m_Name);
                var layerPath = Path.Combine(exportFolder, $"{layerName}{extension}");
                if (File.Exists(layerPath) && !Properties.Settings.Default.allowDuplicates)
                {
                    continue;
                }

                if (File.Exists(layerPath))
                {
                    for (var duplicate = 0; ; duplicate++)
                    {
                        var candidate = Path.Combine(exportFolder, $"{layerName} ({duplicate}){extension}");
                        if (!File.Exists(candidate))
                        {
                            layerPath = candidate;
                            break;
                        }
                    }
                }

                using var image = layer.ConvertToImage(true);
                if (image == null)
                {
                    exportedLayers.Add(new
                    {
                        index = layerIndex - 1,
                        name = layer.m_Name,
                        output = (string)null,
                        status = "unsupportedTextureFormat",
                        textureFormat = layer.m_TextureFormat.ToString(),
                    });
                    continue;
                }

                using (var file = File.OpenWrite(layerPath))
                {
                    image.WriteToStream(file, type);
                }

                exportedLayers.Add(new
                {
                    index = layerIndex - 1,
                    name = layer.m_Name,
                    output = layerPath,
                    status = "exported",
                    textureFormat = layer.m_TextureFormat.ToString(),
                });
            }

            var metadataPath = Path.Combine(exportFolder, $"{FixFileName(item.Text)}.texture2darray.json");
            var metadata = new
            {
                kind = "Texture2DArray",
                name = textureArray.m_Name,
                sourceType = item.TypeString,
                source = item.SourceFile?.originalPath ?? item.SourceFile?.fileName,
                container = item.Container,
                pathId = item.m_PathID,
                width = textureArray.m_Width,
                height = textureArray.m_Height,
                depth = textureArray.m_Depth,
                graphicsFormat = textureArray.m_Format.ToString(),
                mappedTextureFormat = textureArray.m_Format.ToTextureFormat().ToString(),
                textureArrayUsage,
                isDiagnosticPreview = isDataArray,
                previewNote = isDataArray
                    ? "This Texture2DArray appears to be a float/HDR/unknown shader or terrain data array. Exported PNG layers are diagnostic previews and may look noisy; use the metadata/raw Unity reference for material or terrain reconstruction."
                    : null,
                mipCount = textureArray.m_MipCount,
                dataSize = textureArray.m_DataSize,
                streamPath = textureArray.m_StreamData?.path,
                streamOffset = textureArray.m_StreamData?.offset,
                streamSize = textureArray.m_StreamData?.size,
                layers = exportedLayers,
                note = isDataArray
                    ? "Data Texture2DArray is exported as independent diagnostic layer images plus metadata. It is not a normal glTF PBR image and may require shader/terrain/customization logic."
                    : "Texture2DArray is exported as independent layer images for texture/terrain/material libraries; it is not embedded into glTF PBR materials by default.",
            };
            File.WriteAllText(metadataPath, JsonConvert.SerializeObject(metadata, Formatting.Indented));

            AppendAssetCatalog(item, exportFolder, isDataArray ? "DataTexture2DArray" : "Texture2DArray");
            return exportedLayers.Count > 0;
        }

        internal static bool IsDataTexture2DArray(Texture2DArray textureArray, string name, string container, string source)
        {
            var text = string.Join(
                    "/",
                    new[] { name, container, source }
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x.Replace('\\', '/')))
                .ToLowerInvariant();

            if (Regex.IsMatch(
                    text,
                    @"(^|[/_.\-\s])(albedo|basecolor|base_color|diffuse|color|alpha|normal|metallic|roughness|mask|emissive|atlas|palette)(?:$|[/_.\-\s0-9])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return false;
            }

            var graphicsFormat = textureArray.m_Format.ToString();
            var mappedTextureFormat = textureArray.m_Format.ToTextureFormat().ToString();
            var formatText = $"{graphicsFormat}/{mappedTextureFormat}";
            if (Regex.IsMatch(
                    formatText,
                    @"SFloat|UFloat|RGBAFloat|RGFloat|RFloat|Half|BC6H|UInt|Int",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return true;
            }

            var hasHumanReadableName = !string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(container);
            return !hasHumanReadableName && textureArray.m_Format.ToTextureFormat() == 0;
        }

        public static bool ExportAudioClip(AssetItem item, string exportPath)
        {
            var m_AudioClip = (AudioClip)item.Asset;
            if (m_AudioClip.m_AudioData.Size == 0)
                return false;
            var converter = new AudioClipConverter(m_AudioClip);
            string exportFullPath;
            var convertedToWav = false;
            if (Properties.Settings.Default.convertAudio && converter.IsSupport)
            {
                byte[] buffer = null;
                try
                {
                    buffer = converter.ConvertToWav();
                }
                catch (Exception ex)
                {
                    Logger.Warning($"AudioClip {item.Text} cannot be converted to wav; exporting original audio data instead. {ex.Message}");
                }

                if (buffer != null)
                {
                    if (!TryExportFile(exportPath, item, ".wav", out exportFullPath))
                        return false;
                    File.WriteAllBytes(exportFullPath, buffer);
                    convertedToWav = true;
                }
                else
                {
                    if (
                        !TryExportFile(
                            exportPath,
                            item,
                            converter.GetExtensionName(),
                            out exportFullPath
                        )
                    )
                        return false;
                    m_AudioClip.m_AudioData.WriteData(exportFullPath);
                }
            }
            else
            {
                if (
                    !TryExportFile(
                        exportPath,
                        item,
                        converter.GetExtensionName(),
                        out exportFullPath
                    )
                )
                    return false;
                m_AudioClip.m_AudioData.WriteData(exportFullPath);
            }
            WriteAudioMetadata(item, m_AudioClip, exportFullPath, converter, convertedToWav);
            AppendAssetCatalog(item, exportFullPath, "Audio");
            return true;
        }

        internal static string ClassifyAudioClip(AudioClip clip, string name, string container, string source)
        {
            var text = $"{name} {container} {source}".ToLowerInvariant();
            if (Regex.IsMatch(text, @"(^|[^a-z0-9])(voice|vo|dialog|dialogue|speech|talk|line|npcvoice|character[_-]?voice)([^a-z0-9]|$)"))
            {
                return "Voice";
            }
            if (Regex.IsMatch(text, @"(^|[^a-z0-9])(bgm|music|mus|theme|ost|song|loop|ambience|ambient)([^a-z0-9]|$)"))
            {
                return "Music";
            }
            if (Regex.IsMatch(text, @"(^|[^a-z0-9])(sfx|se|sound|sounds|audio|effect|fx|hit|step|foot|jump|dash|shoot|shot|button|click|impact|skill|attack|ui)([^a-z0-9]|$)"))
            {
                return "SFX";
            }
            if (clip != null && clip.m_Length > 0 && clip.m_Length <= 15f)
            {
                return "SFX";
            }
            if (clip != null && clip.m_Length >= 45f)
            {
                return "Music";
            }
            return "Other";
        }

        private static void WriteAudioMetadata(
            AssetItem item,
            AudioClip audioClip,
            string exportFullPath,
            AudioClipConverter converter,
            bool convertedToWav
        )
        {
            var source = item.SourceFile?.originalPath ?? item.SourceFile?.fileName;
            var metadata = new
            {
                kind = "Audio",
                resourceKind = ClassifyAudioClip(audioClip, item.Text, item.Container, source),
                name = item.Text,
                unityName = audioClip.m_Name,
                container = item.Container,
                source,
                pathId = item.m_PathID,
                output = exportFullPath,
                exportedFile = Path.GetFileName(exportFullPath),
                convertedToWav,
                originalExtension = converter.GetExtensionName(),
                length = audioClip.m_Length,
                channels = audioClip.m_Channels,
                frequency = audioClip.m_Frequency,
                bitsPerSample = audioClip.m_BitsPerSample,
                compressionFormat = audioClip.m_CompressionFormat.ToString(),
                loadType = audioClip.m_LoadType,
                preloadAudioData = audioClip.m_PreloadAudioData,
                loadInBackground = audioClip.m_LoadInBackground,
                dataSize = audioClip.m_AudioData.Size,
                note = "AudioClip is intentionally exported through AudioLibrary or explicit type export; it is not part of the default 3D Library.",
            };
            File.WriteAllText(exportFullPath + ".audio.json", JsonConvert.SerializeObject(metadata, Formatting.Indented));
        }

        public static bool ExportShader(AssetItem item, string exportPath)
        {
            if (!TryExportFile(exportPath, item, ".shader.raw", out var exportFullPath))
                return false;
            var m_Shader = (Shader)item.Asset;
            File.WriteAllBytes(exportFullPath, m_Shader.GetRawData());
            var metadata = new
            {
                name = m_Shader.Name,
                unityName = m_Shader.m_Name,
                source = item.SourceFile?.originalPath ?? item.SourceFile?.fileName,
                container = item.Container,
                pathId = item.m_PathID,
                unityVersion = m_Shader.version == null ? null : string.Join(".", m_Shader.version),
                platforms = m_Shader.platforms?.Select(x => x.ToString()).ToArray(),
                hasParsedForm = m_Shader.m_ParsedForm != null,
                hasCompressedBlob = m_Shader.compressedBlob?.Length > 0,
                compressedBlobSize = m_Shader.compressedBlob?.Length ?? 0,
                rawDataFile = Path.GetFileName(exportFullPath),
                note = "Safe shader archive. Native shader disassembly is intentionally not run during default library export.",
            };
            File.WriteAllText(exportFullPath + ".json", JsonConvert.SerializeObject(metadata, Formatting.Indented));
            AppendAssetCatalog(item, exportFullPath, "Shader");
            return true;
        }

        public static bool ExportTextAsset(AssetItem item, string exportPath)
        {
            var m_TextAsset = (TextAsset)(item.Asset);
            var extension = ".txt";
            if (Properties.Settings.Default.restoreExtensionName)
            {
                if (!string.IsNullOrEmpty(item.Container))
                {
                    extension = Path.GetExtension(item.Container);
                }
            }
            if (!TryExportFile(exportPath, item, extension, out var exportFullPath))
                return false;
            File.WriteAllBytes(exportFullPath, m_TextAsset.m_Script);
            return true;
        }

        public static bool ExportMonoBehaviour(AssetItem item, string exportPath)
        {
            var option = new Options();
            var m_MonoBehaviour = (MonoBehaviour)item.Asset;

            string folderPattern =
                $@"(?:Assets|UI|IconRole|Data|Scenes|OriginalResRepos|Comic|Weapon)(?:/[^\s"",]+)*";
            string filePattern =
                $@"(?:Assets|UI|IconRole|Data|Scenes|OriginalResRepos|Comic|Weapon)/[^\s"",]+?\.(?:.*)";
            string voPattern = @"(?:VO|Breath|Tips)_[^""\s;]+";
            string eventPattern =
                @"(?:Ev|Play|Stop|StateGroup|State|VO|SFX)_[a-zA-Z0-9/_-\{\}]{2,}";

            var folderRegex = new Regex(folderPattern, RegexOptions.IgnoreCase);
            var fileRegex = new Regex(filePattern, RegexOptions.IgnoreCase);
            var voRegex = new Regex(voPattern, RegexOptions.IgnoreCase);
            var eventRegex = new Regex(eventPattern, RegexOptions.IgnoreCase);

            if (Properties.Settings.Default.scrapeMonos)
            {
                var s = m_MonoBehaviour.GetRawData();
                var cleanedBytes = new List<byte>(s.Length);
                for (int i = 0; i < s.Length; i++)
                {
                    if (s[i] == 0x00)
                    {
                        bool precededByNull = (i > 0) && (s[i - 1] == 0x00);
                        bool followedByNull = (i < s.Length - 1) && (s[i + 1] == 0x00);

                        if (precededByNull || followedByNull)
                        {
                            cleanedBytes.Add(s[i]);
                        }
                    }
                    else
                    {
                        cleanedBytes.Add(s[i]);
                    }
                }
                var s_cleaned = cleanedBytes.ToArray();

                var idx = Search(s_cleaned, 0);

                while (idx != -1)
                {
                    try
                    {
                        int len = BinaryPrimitives.ReadInt32LittleEndian(s_cleaned.AsSpan(idx - 4));
                        string str = Encoding.UTF8.GetString(s_cleaned.AsSpan(idx, len));

                        foreach (Match match in folderRegex.Matches(str))
                        {
                            Studio.PathStrings.Add(match.Value.Trim());
                        }

                        foreach (Match match in fileRegex.Matches(str))
                        {
                            string subMatch = match.Value.Trim();

                            if (subMatch.StartsWith("UI"))
                                subMatch = $"Assets/NapResources/{subMatch}";
                            else if (subMatch.StartsWith("IconRole"))
                                subMatch =
                                    $"Assets/NapResources/UI/Sprite/A1DynamicLoad/{subMatch}";
                            else if (subMatch.StartsWith("Data"))
                                subMatch = $"Assets/NapResources/{subMatch}";

                            Studio.PathStrings.Add(subMatch);
                        }

                        foreach (Match match in voRegex.Matches(str))
                        {
                            Studio.VOStrings.Add(match.Value.Trim());
                        }
                        foreach (Match match in eventRegex.Matches(str))
                        {
                            Studio.EventStrings.Add(match.Value.Trim());
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error processing MonoBehaviour segment: {ex.Message}");
                    }

                    idx = Search(s_cleaned, idx + 4);
                }
            }
            else
            {
                if (!TryExportFile(exportPath, item, ".json", out var exportFullPath))
                    return false;
                var type = m_MonoBehaviour.ToType();
                if (type == null)
                {
                    var m_Type = Studio.MonoBehaviourToTypeTree(m_MonoBehaviour);
                    type = m_MonoBehaviour.ToType(m_Type);
                }
                var str = JsonConvert.SerializeObject(type, Formatting.Indented);
                File.WriteAllText(exportFullPath, str);
            }

            return true;
        }

        private static int Search(byte[] bytes, int startIndex)
        {
            string[] keys =
            {
                "Assets",
                "UI",
                "IconRole",
                "Data",
                "Scenes",
                "State_",
                "VO_",
                "Play_",
                "Stop_",
                "SFX_",
            };
            foreach (var key in keys)
            {
                int idx = bytes.Search(key, startIndex);
                if (idx != -1)
                    return idx;
            }
            return -1;
        }

        public static bool ExportMiHoYoBinData(AssetItem item, string exportPath)
        {
            string exportFullPath;
            if (item.Asset is MiHoYoBinData m_MiHoYoBinData)
            {
                switch (m_MiHoYoBinData.Type)
                {
                    case MiHoYoBinDataType.JSON:

                        if (!TryExportFile(exportPath, item, ".json", out exportFullPath))
                            return false;
                        var json = m_MiHoYoBinData.Dump() as string;
                        if (json.Length != 0)
                        {
                            File.WriteAllText(exportFullPath, json);
                            return true;
                        }
                        break;
                    case MiHoYoBinDataType.Bytes:
                        var extension = ".bin";
                        if (Properties.Settings.Default.restoreExtensionName)
                        {
                            if (!string.IsNullOrEmpty(item.Container))
                            {
                                extension = Path.GetExtension(item.Container);
                            }
                        }
                        if (!TryExportFile(exportPath, item, extension, out exportFullPath))
                            return false;
                        var bytes = m_MiHoYoBinData.Dump() as byte[];
                        if (!bytes.IsNullOrEmpty())
                        {
                            File.WriteAllBytes(exportFullPath, bytes);
                            return true;
                        }
                        break;
                }
            }
            return false;
        }

        public static bool ExportFont(AssetItem item, string exportPath)
        {
            var m_Font = (Font)item.Asset;
            if (m_Font.m_FontData != null)
            {
                var extension = ".ttf";
                if (
                    m_Font.m_FontData[0] == 79
                    && m_Font.m_FontData[1] == 84
                    && m_Font.m_FontData[2] == 84
                    && m_Font.m_FontData[3] == 79
                )
                {
                    extension = ".otf";
                }
                if (!TryExportFile(exportPath, item, extension, out var exportFullPath))
                    return false;
                File.WriteAllBytes(exportFullPath, m_Font.m_FontData);
                return true;
            }
            return false;
        }

        public static bool ExportMesh(AssetItem item, string exportPath)
        {
            var m_Mesh = (Mesh)item.Asset;
            if (m_Mesh.m_VertexCount <= 0)
                return false;
            if (!TryExportFile(exportPath, item, ".obj", out var exportFullPath))
                return false;
            var sb = new StringBuilder();
            sb.AppendLine("g " + m_Mesh.m_Name);
            #region Vertices
            if (m_Mesh.m_Vertices == null || m_Mesh.m_Vertices.Length == 0)
            {
                return false;
            }
            int c = 3;
            if (m_Mesh.m_Vertices.Length == m_Mesh.m_VertexCount * 4)
            {
                c = 4;
            }
            for (int v = 0; v < m_Mesh.m_VertexCount; v++)
            {
                sb.AppendFormat(
                    "v {0} {1} {2}\r\n",
                    -m_Mesh.m_Vertices[v * c],
                    m_Mesh.m_Vertices[v * c + 1],
                    m_Mesh.m_Vertices[v * c + 2]
                );
            }
            #endregion

            #region UV
            if (m_Mesh.m_UV0?.Length > 0)
            {
                c = 4;
                if (m_Mesh.m_UV0.Length == m_Mesh.m_VertexCount * 2)
                {
                    c = 2;
                }
                else if (m_Mesh.m_UV0.Length == m_Mesh.m_VertexCount * 3)
                {
                    c = 3;
                }
                for (int v = 0; v < m_Mesh.m_VertexCount; v++)
                {
                    sb.AppendFormat("vt {0} {1}\r\n", m_Mesh.m_UV0[v * c], m_Mesh.m_UV0[v * c + 1]);
                }
            }
            #endregion

            #region Normals
            if (m_Mesh.m_Normals?.Length > 0)
            {
                if (m_Mesh.m_Normals.Length == m_Mesh.m_VertexCount * 3)
                {
                    c = 3;
                }
                else if (m_Mesh.m_Normals.Length == m_Mesh.m_VertexCount * 4)
                {
                    c = 4;
                }
                for (int v = 0; v < m_Mesh.m_VertexCount; v++)
                {
                    sb.AppendFormat(
                        "vn {0} {1} {2}\r\n",
                        -m_Mesh.m_Normals[v * c],
                        m_Mesh.m_Normals[v * c + 1],
                        m_Mesh.m_Normals[v * c + 2]
                    );
                }
            }
            #endregion

            #region Face
            int sum = 0;
            for (var i = 0; i < m_Mesh.m_SubMeshes.Count; i++)
            {
                sb.AppendLine($"g {m_Mesh.m_Name}_{i}");
                int indexCount = (int)m_Mesh.m_SubMeshes[i].indexCount;
                var end = sum + indexCount / 3;
                for (int f = sum; f < end; f++)
                {
                    sb.AppendFormat(
                        "f {0}/{0}/{0} {1}/{1}/{1} {2}/{2}/{2}\r\n",
                        m_Mesh.m_Indices[f * 3 + 2] + 1,
                        m_Mesh.m_Indices[f * 3 + 1] + 1,
                        m_Mesh.m_Indices[f * 3] + 1
                    );
                }
                sum = end;
            }
            #endregion

            sb.Replace("NaN", "0");
            File.WriteAllText(exportFullPath, sb.ToString());
            return true;
        }

        public static bool ExportStaticMeshGltf(AssetItem item, string exportPath)
        {
            var mesh = (Mesh)item.Asset;
            if (!IsExportableStaticMesh(mesh))
            {
                return false;
            }

            if (!TryExportFile(exportPath, item, ".gltf", out var gltfPath))
            {
                return false;
            }

            using (ProfileLogger.Measure("static_mesh_gltf_export", GetModelProfileData(item, gltfPath)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(gltfPath));
                var binPath = Path.ChangeExtension(gltfPath, ".bin");
                var binName = Path.GetFileName(binPath);
                var bufferViews = new JArray();
                var accessors = new JArray();
                var primitives = new JArray();
                var materialBinding = ResolveStaticMeshMaterialBinding(item, gltfPath);
                using var stream = File.Create(binPath);

                var positionAccessor = WriteFloatAccessor(
                    stream,
                    bufferViews,
                    accessors,
                    BuildStaticMeshPositions(mesh),
                    "VEC3",
                    34962,
                    CalculateVec3Min(mesh.m_Vertices, mesh.m_VertexCount, true),
                    CalculateVec3Max(mesh.m_Vertices, mesh.m_VertexCount, true)
                );

                int? normalAccessor = null;
                if (mesh.m_Normals != null && mesh.m_Normals.Length >= mesh.m_VertexCount * 3)
                {
                    normalAccessor = WriteFloatAccessor(
                        stream,
                        bufferViews,
                        accessors,
                        BuildStaticMeshNormals(mesh),
                        "VEC3",
                        34962
                    );
                }

                int? uvAccessor = null;
                var uv0 = mesh.GetUV(0);
                if (uv0 != null && uv0.Length >= mesh.m_VertexCount * 2)
                {
                    uvAccessor = WriteFloatAccessor(
                        stream,
                        bufferViews,
                        accessors,
                        BuildStaticMeshUv(uv0, mesh.m_VertexCount),
                        "VEC2",
                        34962
                    );
                }

                var cursor = 0;
                for (var subMeshIndex = 0; subMeshIndex < mesh.m_SubMeshes.Count; subMeshIndex++)
                {
                    var subMesh = mesh.m_SubMeshes[subMeshIndex];
                    var indexCount = (int)Math.Min(subMesh.indexCount, Math.Max(0, mesh.m_Indices.Count - cursor));
                    if (indexCount < 3)
                    {
                        cursor += indexCount;
                        continue;
                    }

                    var indices = BuildStaticMeshIndices(mesh.m_Indices, cursor, indexCount);
                    cursor += indexCount;
                    if (indices.Length < 3)
                    {
                        continue;
                    }

                    var indexAccessor = WriteUIntAccessor(stream, bufferViews, accessors, indices, 34963);
                    var attributes = new JObject
                    {
                        ["POSITION"] = positionAccessor,
                    };
                    if (normalAccessor.HasValue)
                    {
                        attributes["NORMAL"] = normalAccessor.Value;
                    }
                    if (uvAccessor.HasValue)
                    {
                        attributes["TEXCOORD_0"] = uvAccessor.Value;
                    }

                    primitives.Add(new JObject
                    {
                        ["attributes"] = attributes,
                        ["indices"] = indexAccessor,
                        ["mode"] = 4,
                        ["material"] = materialBinding.GetMaterialIndexForSubMesh(subMeshIndex),
                        ["extras"] = new JObject
                        {
                            ["unitySubMesh"] = subMeshIndex,
                            ["sourceIndexCount"] = indexCount,
                        },
                    });
                }

                if (primitives.Count == 0)
                {
                    return false;
                }

                AlignStream4(stream);
                var images = new JArray();
                var textures = new JArray();
                var materials = BuildStaticMeshGltfMaterials(item, gltfPath, materialBinding, images, textures);

                var gltf = new JObject
                {
                    ["asset"] = new JObject
                    {
                        ["version"] = "2.0",
                        ["generator"] = "AnimeStudio StaticMesh Library",
                    },
                    ["scenes"] = new JArray(new JObject { ["nodes"] = new JArray(0) }),
                    ["scene"] = 0,
                    ["nodes"] = new JArray(new JObject
                    {
                        ["name"] = FixFileName(item.Text),
                        ["mesh"] = 0,
                        ["extras"] = new JObject
                        {
                            ["unityContainer"] = item.Container,
                            ["source"] = item.SourceFile?.originalPath ?? item.SourceFile?.fileName,
                            ["pathId"] = item.m_PathID,
                            ["libraryRole"] = item.LibraryRole,
                        },
                    }),
                    ["meshes"] = new JArray(new JObject
                    {
                        ["name"] = mesh.m_Name,
                        ["primitives"] = primitives,
                    }),
                    ["materials"] = materials,
                    ["buffers"] = new JArray(new JObject
                    {
                        ["uri"] = binName,
                        ["byteLength"] = stream.Length,
                    }),
                    ["bufferViews"] = bufferViews,
                    ["accessors"] = accessors,
                };
                if (images.Count > 0)
                {
                    gltf["images"] = images;
                }
                if (textures.Count > 0)
                {
                    gltf["textures"] = textures;
                }

                File.WriteAllText(gltfPath, gltf.ToString(Formatting.Indented));
                WriteStaticMeshReadme(gltfPath, item, mesh, materialBinding);
                AppendStaticMeshAssetCatalog(item, gltfPath, materialBinding);
            }

            return true;
        }

        private static bool IsExportableStaticMesh(Mesh mesh)
        {
            return mesh != null
                && mesh.m_VertexCount > 0
                && mesh.m_Vertices != null
                && mesh.m_Vertices.Length >= mesh.m_VertexCount * 3
                && mesh.m_Indices != null
                && mesh.m_Indices.Count >= 3
                && mesh.m_SubMeshes != null
                && mesh.m_SubMeshes.Count > 0;
        }

        private static float[] BuildStaticMeshPositions(Mesh mesh)
        {
            var stride = mesh.m_Vertices.Length >= mesh.m_VertexCount * 4 ? 4 : 3;
            var values = new float[mesh.m_VertexCount * 3];
            for (var i = 0; i < mesh.m_VertexCount; i++)
            {
                values[i * 3] = SanitizeFloat(-mesh.m_Vertices[i * stride]);
                values[i * 3 + 1] = SanitizeFloat(mesh.m_Vertices[i * stride + 1]);
                values[i * 3 + 2] = SanitizeFloat(mesh.m_Vertices[i * stride + 2]);
            }
            return values;
        }

        private static float[] BuildStaticMeshNormals(Mesh mesh)
        {
            var stride = mesh.m_Normals.Length >= mesh.m_VertexCount * 4 ? 4 : 3;
            var values = new float[mesh.m_VertexCount * 3];
            for (var i = 0; i < mesh.m_VertexCount; i++)
            {
                values[i * 3] = SanitizeFloat(-mesh.m_Normals[i * stride]);
                values[i * 3 + 1] = SanitizeFloat(mesh.m_Normals[i * stride + 1]);
                values[i * 3 + 2] = SanitizeFloat(mesh.m_Normals[i * stride + 2]);
            }
            return values;
        }

        private static float[] BuildStaticMeshUv(float[] uv, int vertexCount)
        {
            var stride = uv.Length >= vertexCount * 4 ? 4 : uv.Length >= vertexCount * 3 ? 3 : 2;
            var values = new float[vertexCount * 2];
            for (var i = 0; i < vertexCount; i++)
            {
                values[i * 2] = SanitizeFloat(uv[i * stride]);
                values[i * 2 + 1] = SanitizeFloat(1.0f - uv[i * stride + 1]);
            }
            return values;
        }

        private static uint[] BuildStaticMeshIndices(List<uint> source, int start, int count)
        {
            var triangleCount = count / 3;
            var values = new uint[triangleCount * 3];
            for (var i = 0; i < triangleCount; i++)
            {
                var src = start + i * 3;
                var dst = i * 3;
                values[dst] = source[src + 2];
                values[dst + 1] = source[src + 1];
                values[dst + 2] = source[src];
            }
            return values;
        }

        private static int WriteFloatAccessor(Stream stream, JArray bufferViews, JArray accessors, float[] values, string type, int target, float[] min = null, float[] max = null)
        {
            AlignStream4(stream);
            var offset = stream.Position;
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                foreach (var value in values)
                {
                    writer.Write(SanitizeFloat(value));
                }
            }
            return AddAccessor(bufferViews, accessors, offset, values.Length * sizeof(float), target, 5126, type, GetElementCount(type, values.Length), min, max);
        }

        private static int WriteUIntAccessor(Stream stream, JArray bufferViews, JArray accessors, uint[] values, int target)
        {
            AlignStream4(stream);
            var offset = stream.Position;
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                foreach (var value in values)
                {
                    writer.Write(value);
                }
            }
            return AddAccessor(bufferViews, accessors, offset, values.Length * sizeof(uint), target, 5125, "SCALAR", values.Length);
        }

        private static int AddAccessor(JArray bufferViews, JArray accessors, long offset, int byteLength, int target, int componentType, string type, int count, float[] min = null, float[] max = null)
        {
            var bufferViewIndex = bufferViews.Count;
            bufferViews.Add(new JObject
            {
                ["buffer"] = 0,
                ["byteOffset"] = offset,
                ["byteLength"] = byteLength,
                ["target"] = target,
            });
            var accessor = new JObject
            {
                ["bufferView"] = bufferViewIndex,
                ["componentType"] = componentType,
                ["count"] = count,
                ["type"] = type,
            };
            if (min != null)
            {
                accessor["min"] = new JArray(min.Select(x => SanitizeFloat(x)));
            }
            if (max != null)
            {
                accessor["max"] = new JArray(max.Select(x => SanitizeFloat(x)));
            }
            var accessorIndex = accessors.Count;
            accessors.Add(accessor);
            return accessorIndex;
        }

        private static int GetElementCount(string type, int scalarCount)
        {
            return type switch
            {
                "VEC2" => scalarCount / 2,
                "VEC3" => scalarCount / 3,
                "VEC4" => scalarCount / 4,
                _ => scalarCount,
            };
        }

        private static void AlignStream4(Stream stream)
        {
            while ((stream.Position & 3) != 0)
            {
                stream.WriteByte(0);
            }
        }

        private static float SanitizeFloat(float value)
        {
            return float.IsFinite(value) ? value : 0f;
        }

        private static float[] CalculateVec3Min(float[] values, int vertexCount, bool negateX)
        {
            return CalculateVec3MinMax(values, vertexCount, negateX, true);
        }

        private static float[] CalculateVec3Max(float[] values, int vertexCount, bool negateX)
        {
            return CalculateVec3MinMax(values, vertexCount, negateX, false);
        }

        private static float[] CalculateVec3MinMax(float[] values, int vertexCount, bool negateX, bool findMin)
        {
            var stride = values.Length >= vertexCount * 4 ? 4 : 3;
            var result = new[]
            {
                findMin ? float.PositiveInfinity : float.NegativeInfinity,
                findMin ? float.PositiveInfinity : float.NegativeInfinity,
                findMin ? float.PositiveInfinity : float.NegativeInfinity,
            };
            for (var i = 0; i < vertexCount; i++)
            {
                var x = SanitizeFloat(values[i * stride] * (negateX ? -1f : 1f));
                var y = SanitizeFloat(values[i * stride + 1]);
                var z = SanitizeFloat(values[i * stride + 2]);
                if (findMin)
                {
                    result[0] = Math.Min(result[0], x);
                    result[1] = Math.Min(result[1], y);
                    result[2] = Math.Min(result[2], z);
                }
                else
                {
                    result[0] = Math.Max(result[0], x);
                    result[1] = Math.Max(result[1], y);
                    result[2] = Math.Max(result[2], z);
                }
            }
            return result.Select(SanitizeFloat).ToArray();
        }

        private static StaticMeshMaterialBinding ResolveStaticMeshMaterialBinding(AssetItem item, string gltfPath)
        {
            var binding = new StaticMeshMaterialBinding
            {
                SameContainerMaterials = FindSameContainerMaterials(item).ToArray(),
            };

            var indexPath = SQLiteSourceIndexRuntime.CurrentLoadResult?.DatabasePath;
            if (string.IsNullOrWhiteSpace(indexPath) || !File.Exists(indexPath))
            {
                binding.Status = binding.SameContainerMaterials.Length > 0 ? "needsRendererBinding" : "missingRendererMaterial";
                binding.Notes.Add("No active SQLite source index is available during StaticMesh export; only same-container Material candidates can be reported.");
                return binding;
            }

            var meshObject = (AnimeStudio.Object)item.Asset;
            var meshFile = meshObject.assetsFile?.fileName ?? item.SourceFile?.fileName ?? string.Empty;
            var refs = QueryStaticMeshMaterialReferences(indexPath, meshFile, item.m_PathID);
            binding.RendererBindings = refs
                .GroupBy(x => $"{x.RendererFile}:{x.RendererPathId}", StringComparer.OrdinalIgnoreCase)
                .Select(x => new StaticMeshRendererBindingInfo
                {
                    RendererFile = x.First().RendererFile,
                    RendererPathId = x.First().RendererPathId,
                    RendererType = x.First().RendererType,
                    MaterialCount = x.Count(),
                    Materials = x.Select(y => new StaticMeshMaterialRefInfo
                    {
                        Name = y.MaterialName,
                        File = y.MaterialFile,
                        PathId = y.MaterialPathId,
                    }).ToList(),
                })
                .ToList();

            if (refs.Count == 0)
            {
                binding.Status = binding.SameContainerMaterials.Length > 0 ? "needsRendererBinding" : "missingRendererMaterial";
                binding.Notes.Add(binding.SameContainerMaterials.Length > 0
                    ? "No Renderer->Material binding was found for this Mesh in the SQLite source index; same-container materials are listed as candidates only."
                    : "No Renderer->Material binding or same-container Material candidate was found for this direct Mesh.");
                return binding;
            }

            var grouped = refs
                .GroupBy(x => $"{x.RendererFile}:{x.RendererPathId}", StringComparer.OrdinalIgnoreCase)
                .Select(x => new
                {
                    RendererKey = x.Key,
                    RendererFile = x.First().RendererFile,
                    RendererPathId = x.First().RendererPathId,
                    RendererType = x.First().RendererType,
                    Materials = x.OrderBy(y => y.RelationId).ToList(),
                    ResolvedCount = x.Count(y => TryGetLoadedObject(item, y.MaterialFile, y.MaterialPathId, out Material _)),
                })
                .OrderByDescending(x => x.ResolvedCount)
                .ThenByDescending(x => x.Materials.Count)
                .ToList();
            var selected = grouped.FirstOrDefault();
            if (selected == null || selected.ResolvedCount == 0)
            {
                binding.Status = "rendererMaterialUnresolved";
                binding.Notes.Add("Renderer->Material relations were found in SQLite, but none of the Material objects could be resolved in the currently loaded dependency closure.");
                return binding;
            }

            binding.SelectedRenderer = new StaticMeshRendererBindingInfo
            {
                RendererFile = selected.RendererFile,
                RendererPathId = selected.RendererPathId,
                RendererType = selected.RendererType,
                MaterialCount = selected.Materials.Count,
                Materials = selected.Materials.Select(y => new StaticMeshMaterialRefInfo
                {
                    Name = y.MaterialName,
                    File = y.MaterialFile,
                    PathId = y.MaterialPathId,
                }).ToList(),
            };
            foreach (var materialRef in selected.Materials)
            {
                if (!TryGetLoadedObject(item, materialRef.MaterialFile, materialRef.MaterialPathId, out Material material))
                {
                    continue;
                }

                binding.Materials.Add(new StaticMeshResolvedMaterial
                {
                    Material = material,
                    Name = string.IsNullOrWhiteSpace(material.m_Name) ? materialRef.MaterialName : material.m_Name,
                    File = materialRef.MaterialFile,
                    PathId = materialRef.MaterialPathId,
                });
            }

            binding.Status = binding.Materials.Count > 0 ? "boundRendererMaterial" : "rendererMaterialUnresolved";
            binding.Notes.Add("Material binding was resolved from Unity source index relation chain: Mesh -> MeshFilter/SkinnedMeshRenderer -> GameObject/Renderer -> Material.");
            if (binding.RendererBindings.Count > 1)
            {
                binding.Notes.Add("This Mesh is referenced by multiple Renderer material sets; AnimeStudio selected the first resolvable set for browsable StaticMesh glTF and records alternatives in extras.");
            }
            return binding;
        }

        private static List<StaticMeshMaterialReference> QueryStaticMeshMaterialReferences(string indexPath, string meshFile, long meshPathId)
        {
            var refs = new List<StaticMeshMaterialReference>();
            try
            {
                SQLitePCL.Batteries_V2.Init();
                using var connection = new SqliteConnection($"Data Source={indexPath};Mode=ReadOnly");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
WITH direct_renderers AS (
    SELECT r.from_file, r.from_path_id, r.from_type
    FROM source_relations r
    WHERE r.relation = 'skinnedMeshRenderer.mesh'
      AND r.to_file = $meshFile
      AND r.to_path_id = $meshPathId
),
mesh_filters AS (
    SELECT mf.from_file, mf.from_path_id
    FROM source_relations mf
    WHERE mf.relation = 'meshFilter.mesh'
      AND mf.to_file = $meshFile
      AND mf.to_path_id = $meshPathId
),
game_objects AS (
    SELECT go.to_file AS go_file, go.to_path_id AS go_path_id
    FROM source_relations go
    JOIN mesh_filters mf ON go.from_file = mf.from_file AND go.from_path_id = mf.from_path_id
    WHERE go.relation = 'component.gameObject'
),
static_renderers AS (
    SELECT r.from_file, r.from_path_id, r.from_type
    FROM source_relations r
    JOIN game_objects go ON r.to_file = go.go_file AND r.to_path_id = go.go_path_id
    WHERE r.relation = 'component.gameObject'
      AND r.from_type IN ('MeshRenderer', 'SkinnedMeshRenderer')
),
renderers AS (
    SELECT * FROM direct_renderers
    UNION
    SELECT * FROM static_renderers
)
SELECT mat.id,
       r.from_file,
       r.from_path_id,
       r.from_type,
       mat.to_file,
       mat.to_path_id,
       COALESCE(mo.name, '') AS material_name
FROM renderers r
JOIN source_relations mat ON mat.from_file = r.from_file AND mat.from_path_id = r.from_path_id
LEFT JOIN source_objects mo ON mo.serialized_file = mat.to_file AND mo.path_id = mat.to_path_id
WHERE mat.relation = 'renderer.material'
ORDER BY r.from_file, r.from_path_id, mat.id;";
                command.Parameters.AddWithValue("$meshFile", meshFile ?? string.Empty);
                command.Parameters.AddWithValue("$meshPathId", meshPathId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    refs.Add(new StaticMeshMaterialReference
                    {
                        RelationId = reader.GetInt64(0),
                        RendererFile = reader.GetString(1),
                        RendererPathId = reader.GetInt64(2),
                        RendererType = reader.GetString(3),
                        MaterialFile = reader.GetString(4),
                        MaterialPathId = reader.GetInt64(5),
                        MaterialName = reader.GetString(6),
                    });
                }
            }
            catch (Exception e) when (e is IOException || e is SqliteException || e is InvalidDataException)
            {
                Logger.Warning($"Unable to query StaticMesh material bindings from SQLite source index: {e.Message}");
            }

            return refs;
        }

        private static bool TryGetLoadedObject<T>(AssetItem sourceItem, string serializedFile, long pathId, out T result)
            where T : AnimeStudio.Object
        {
            result = null;
            if (pathId == 0 || sourceItem?.Asset is not AnimeStudio.Object sourceObject)
            {
                return false;
            }

            var assetsManager = sourceObject.assetsFile?.assetsManager;
            if (assetsManager?.assetsFileList == null)
            {
                return false;
            }

            foreach (var assetsFile in assetsManager.assetsFileList)
            {
                if (!string.Equals(assetsFile.fileName ?? string.Empty, serializedFile ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (assetsFile.ObjectsDic.TryGetValue(pathId, out var obj) && obj is T typed)
                {
                    result = typed;
                    return true;
                }
            }

            return false;
        }

        private static JArray BuildStaticMeshGltfMaterials(
            AssetItem item,
            string gltfPath,
            StaticMeshMaterialBinding binding,
            JArray images,
            JArray textures)
        {
            if (binding.Materials.Count == 0)
            {
                return new JArray(BuildStaticMeshDefaultMaterial(binding));
            }

            var materials = new JArray();
            foreach (var materialInfo in binding.Materials)
            {
                materials.Add(BuildStaticMeshGltfMaterial(item, gltfPath, materialInfo, binding, images, textures));
            }
            return materials;
        }

        private static JObject BuildStaticMeshDefaultMaterial(StaticMeshMaterialBinding binding)
        {
            return new JObject
            {
                ["name"] = "StaticMesh_Default",
                ["pbrMetallicRoughness"] = new JObject
                {
                    ["baseColorFactor"] = new JArray(0.8, 0.8, 0.8, 1.0),
                    ["metallicFactor"] = 0.0,
                    ["roughnessFactor"] = 0.6,
                },
                ["extras"] = new JObject
                {
                    ["animeStudioMaterial"] = binding.ToJson(),
                },
            };
        }

        private static JObject BuildStaticMeshGltfMaterial(
            AssetItem item,
            string gltfPath,
            StaticMeshResolvedMaterial materialInfo,
            StaticMeshMaterialBinding binding,
            JArray images,
            JArray textures)
        {
            var material = materialInfo.Material;
            var diffuse = GetStaticMeshMaterialColor(material);
            var pbr = new JObject
            {
                ["baseColorFactor"] = new JArray(diffuse.R, diffuse.G, diffuse.B, diffuse.A),
                ["metallicFactor"] = GetUnityFloat(material, "_Metallic", 0.0f),
                ["roughnessFactor"] = 1.0f - Math.Clamp(GetUnityFloat(material, "_Glossiness", 0.2f), 0.0f, 1.0f),
            };
            JObject normalTexture = null;
            var textureExtras = new JArray();

            foreach (var texEnv in material.m_SavedProperties?.m_TexEnvs ?? new List<KeyValuePair<string, UnityTexEnv>>())
            {
                if (texEnv.Value?.m_Texture == null || !texEnv.Value.m_Texture.TryGet<Texture2D>(out var texture))
                {
                    continue;
                }

                var destination = GetStaticMeshTextureDestination(texEnv.Key);
                var textureInfo = new JObject
                {
                    ["slot"] = texEnv.Key,
                    ["dest"] = destination,
                    ["texture"] = texture.m_Name,
                    ["source"] = texture.assetsFile?.originalPath ?? texture.assetsFile?.fileName,
                    ["pathId"] = texture.m_PathID,
                };
                if (TryExportStaticMeshTexture(gltfPath, texture, out var textureUri, out var exportName))
                {
                    var imageIndex = images.Count;
                    images.Add(new JObject
                    {
                        ["uri"] = textureUri,
                        ["name"] = texture.m_Name,
                    });
                    var textureIndex = textures.Count;
                    textures.Add(new JObject
                    {
                        ["source"] = imageIndex,
                        ["name"] = texture.m_Name,
                    });
                    textureInfo["uri"] = textureUri;
                    textureInfo["exportName"] = exportName;
                    textureInfo["gltfTextureIndex"] = textureIndex;

                    if (destination == 0 && pbr["baseColorTexture"] == null)
                    {
                        pbr["baseColorTexture"] = new JObject { ["index"] = textureIndex };
                    }
                    else if ((destination == 1 || destination == 3) && normalTexture == null)
                    {
                        normalTexture = new JObject { ["index"] = textureIndex };
                    }
                }
                else
                {
                    textureInfo["status"] = "textureExportUnavailable";
                }
                textureExtras.Add(textureInfo);
            }

            var gltfMaterial = new JObject
            {
                ["name"] = string.IsNullOrWhiteSpace(materialInfo.Name) ? "StaticMesh_Material" : materialInfo.Name,
                ["pbrMetallicRoughness"] = pbr,
                ["extras"] = new JObject
                {
                    ["animeStudioMaterial"] = binding.ToJson(materialInfo, textureExtras),
                },
            };
            if (normalTexture != null)
            {
                gltfMaterial["normalTexture"] = normalTexture;
            }
            if (diffuse.A < 0.999f || GetUnityFloat(material, "_Surface", 0.0f) > 0.5f)
            {
                gltfMaterial["alphaMode"] = "BLEND";
            }

            return gltfMaterial;
        }

        private static bool TryExportStaticMeshTexture(string gltfPath, Texture2D texture, out string textureUri, out string exportName)
        {
            textureUri = null;
            exportName = null;
            if (texture == null || CliExportOptions.TextureMode == AnimeStudio.TextureExportMode.Raw)
            {
                return false;
            }

            var textureDirectory = GetSharedTextureDirectory(gltfPath)
                ?? Path.Combine(Path.GetDirectoryName(gltfPath), "Textures");
            Directory.CreateDirectory(textureDirectory);

            var extension = "." + Properties.Settings.Default.convertType.ToString().ToLowerInvariant();
            exportName = GetStaticMeshTextureExportName(texture, extension);
            var texturePath = Path.Combine(textureDirectory, exportName);
            if (!File.Exists(texturePath))
            {
                var profileData = new Dictionary<string, object>
                {
                    ["texture"] = texture.m_Name,
                    ["source"] = texture.assetsFile?.fullName,
                    ["pathId"] = texture.m_PathID,
                    ["exportName"] = exportName,
                    ["textureMode"] = CliExportOptions.TextureMode.ToString(),
                    ["textureFormat"] = texture.m_TextureFormat.ToString(),
                    ["width"] = texture.m_Width,
                    ["height"] = texture.m_Height,
                    ["imageFormat"] = Properties.Settings.Default.convertType.ToString(),
                    ["usage"] = "static_mesh_renderer_material",
                };
                using (ProfileLogger.Measure("static_mesh_texture", profileData))
                using (var stream = texture.ConvertToStream(Properties.Settings.Default.convertType, true, ProfileLogger.Measure, profileData))
                {
                    if (stream == null)
                    {
                        return false;
                    }
                    using var file = File.OpenWrite(texturePath);
                    stream.WriteTo(file);
                }
            }

            textureUri = Path.GetRelativePath(Path.GetDirectoryName(gltfPath), texturePath).Replace('\\', '/');
            return true;
        }

        private static string GetStaticMeshTextureExportName(Texture2D texture, string extension)
        {
            var rawName = GetSafeTextureFileName(texture.m_Name ?? "Texture");
            if (rawName.Length > 100)
            {
                rawName = rawName.Substring(0, 67) + "_" + rawName.Substring(rawName.Length - 32);
            }
            var sourceFile = GetSafeTextureFileName(texture.assetsFile?.fileName ?? "source");
            return $"{rawName}_{sourceFile}_{texture.m_PathID}{extension}";
        }

        private static Color GetStaticMeshMaterialColor(Material material)
        {
            var result = new Color(0.8f, 0.8f, 0.8f, 1);
            var hasColor = false;
            foreach (var color in material.m_SavedProperties?.m_Colors ?? new List<KeyValuePair<string, Color>>())
            {
                if (string.Equals(color.Key, "_Color", StringComparison.OrdinalIgnoreCase))
                {
                    result = color.Value;
                    hasColor = true;
                }
                else if (!hasColor && string.Equals(color.Key, "_BaseColor", StringComparison.OrdinalIgnoreCase))
                {
                    result = color.Value;
                }
            }
            return result;
        }

        private static float GetUnityFloat(Material material, string name, float fallback)
        {
            foreach (var value in material.m_SavedProperties?.m_Floats ?? new List<KeyValuePair<string, float>>())
            {
                if (string.Equals(value.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return value.Value;
                }
            }
            return fallback;
        }

        private static int GetStaticMeshTextureDestination(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return -1;
            }
            if (
                key == "_MainTex"
                || key.Contains("Diffuse")
                || key.Contains("Albedo")
                || key.Contains("BaseMap")
                || key.Contains("BaseColor")
            )
            {
                return 0;
            }
            if (key == "_BumpMap" || key.Contains("Normal"))
            {
                return 1;
            }
            if (key.Contains("Specular"))
            {
                return 2;
            }
            if (key.Contains("Emission") || key.Contains("Emissive"))
            {
                return 5;
            }
            if (key.Contains("Reflect"))
            {
                return 6;
            }
            return -1;
        }

        private static IEnumerable<object> FindSameContainerMaterials(AssetItem item)
        {
            if (string.IsNullOrWhiteSpace(item.Container))
            {
                yield break;
            }

            foreach (var materialItem in Studio.exportableAssets.Where(x =>
                         x.Asset is Material
                         && string.Equals((x.Container ?? string.Empty).Trim(), item.Container.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                var material = (Material)materialItem.Asset;
                yield return new
                {
                    name = material.m_Name,
                    source = material.assetsFile?.originalPath ?? material.assetsFile?.fileName,
                    pathId = material.m_PathID,
                    textureSlots = material.m_SavedProperties?.m_TexEnvs?
                        .Where(x => x.Value?.m_Texture != null && x.Value.m_Texture.m_PathID != 0)
                        .Select(x => x.Key)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .ToArray() ?? Array.Empty<string>(),
                };
            }
        }

        private static void WriteStaticMeshReadme(string gltfPath, AssetItem item, Mesh mesh, StaticMeshMaterialBinding materialBinding)
        {
            var readmePath = Path.Combine(Path.GetDirectoryName(gltfPath), $"{FixFileName(item.Text)}.ASSET_README.md");
            var sb = new StringBuilder();
            sb.AppendLine("# Static Mesh Asset");
            sb.AppendLine();
            sb.AppendLine("这份 glTF 来自 Unity 的直接 `Mesh` 资产，而不是 prefab/renderer 根对象。");
            sb.AppendLine();
            sb.AppendLine($"- 名称: `{item.Text}`");
            sb.AppendLine($"- Unity Container: `{item.Container}`");
            sb.AppendLine($"- Source: `{item.SourceFile?.originalPath ?? item.SourceFile?.fileName}`");
            sb.AppendLine($"- PathID: `{item.m_PathID}`");
            sb.AppendLine($"- Vertex Count: `{mesh.m_VertexCount}`");
            sb.AppendLine($"- SubMesh Count: `{mesh.m_SubMeshes?.Count ?? 0}`");
            sb.AppendLine($"- Material Binding: `{materialBinding.Status}`");
            sb.AppendLine();
            foreach (var note in materialBinding.Notes)
            {
                sb.AppendLine($"- {note}");
            }
            if (materialBinding.SelectedRenderer != null)
            {
                sb.AppendLine();
                sb.AppendLine("已通过 Unity Renderer 关系绑定材质：");
                sb.AppendLine($"- Renderer: `{materialBinding.SelectedRenderer.RendererType}` `{materialBinding.SelectedRenderer.RendererFile}:{materialBinding.SelectedRenderer.RendererPathId}`");
                foreach (var material in materialBinding.Materials)
                {
                    sb.AppendLine($"- Material: `{material.Name}` `{material.File}:{material.PathId}`");
                }
            }
            else if (materialBinding.SameContainerMaterials.Length > 0)
            {
                sb.AppendLine("同容器内找到了 Material 候选，但裸 Mesh 没有 Renderer 的 submesh-material 绑定，当前仅记录候选关系：");
                foreach (var material in materialBinding.SameContainerMaterials)
                {
                    sb.AppendLine($"- `{JsonConvert.SerializeObject(material)}`");
                }
            }
            else
            {
                sb.AppendLine("没有找到同容器 Material 候选。该素材可能需要 prefab/renderer、terrain/shader 配置或人工材质修补。");
            }
            File.WriteAllText(readmePath, sb.ToString());
        }

        private static void AppendStaticMeshAssetCatalog(AssetItem item, string outputPath, StaticMeshMaterialBinding materialBinding)
        {
            if (string.IsNullOrWhiteSpace(CliExportOptions.OutputRoot))
            {
                return;
            }

            var mesh = (Mesh)item.Asset;
            var materialSummary = BuildExportedModelMaterialSummary(outputPath);
            AppendCatalogEntry(new
            {
                kind = "Model",
                libraryRole = item.LibraryRole,
                resourceKind = InferResourceKind(item.Text, item.Container, item.SourceFile?.originalPath ?? item.SourceFile?.fileName),
                exportedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                name = item.Text,
                sourceType = item.TypeString,
                container = item.Container,
                source = item.SourceFile?.originalPath ?? item.SourceFile?.fileName,
                pathId = item.m_PathID,
                output = outputPath,
                format = "Gltf",
                modelSource = CliExportOptions.ModelSource.ToString(),
                textureMode = CliExportOptions.TextureMode.ToString(),
                animationPackage = CliExportOptions.AnimationPackage.ToString(),
                meshCount = 1,
                vertexCount = mesh.m_VertexCount,
                subMeshCount = mesh.m_SubMeshes?.Count ?? 0,
                indexCount = mesh.m_Indices?.Count ?? 0,
                materialBindingStatus = materialBinding.Status,
                materialStatus = materialSummary?.status ?? materialBinding.Status,
                materialStatusCounts = materialSummary?.statusCounts,
                materialNeedsCustomizationTint = materialSummary?.needsCustomizationTint ?? false,
                materialMissingRendererBinding = materialSummary?.missingRendererBinding
                    ?? !string.Equals(materialBinding.Status, "boundRendererMaterial", StringComparison.OrdinalIgnoreCase),
                materialHasBaseColorTexture = materialSummary?.hasBaseColorTexture ?? false,
                materialHasNormalTexture = materialSummary?.hasNormalTexture ?? false,
                materialImageCount = materialSummary?.imageCount ?? 0,
                materialCount = materialBinding.Materials.Count,
                selectedRenderer = materialBinding.SelectedRenderer,
                rendererBindings = materialBinding.RendererBindings,
                sameContainerMaterials = materialBinding.SameContainerMaterials,
            });
        }

        private sealed class StaticMeshMaterialBinding
        {
            public string Status { get; set; } = "missingRendererMaterial";
            public List<string> Notes { get; } = new List<string>();
            public object[] SameContainerMaterials { get; set; } = Array.Empty<object>();
            public List<StaticMeshRendererBindingInfo> RendererBindings { get; set; } = new List<StaticMeshRendererBindingInfo>();
            public StaticMeshRendererBindingInfo SelectedRenderer { get; set; }
            public List<StaticMeshResolvedMaterial> Materials { get; } = new List<StaticMeshResolvedMaterial>();

            public int GetMaterialIndexForSubMesh(int subMeshIndex)
            {
                if (Materials.Count == 0)
                {
                    return 0;
                }
                return Math.Min(subMeshIndex, Materials.Count - 1);
            }

            public JObject ToJson(StaticMeshResolvedMaterial material = null, JArray textureExtras = null)
            {
                var json = new JObject
                {
                    ["workflow"] = "StaticMeshContainer",
                    ["status"] = Status,
                    ["notes"] = new JArray(Notes),
                    ["selectedRenderer"] = SelectedRenderer != null ? JObject.FromObject(SelectedRenderer) : null,
                    ["rendererBindings"] = JArray.FromObject(RendererBindings),
                    ["sameContainerMaterials"] = JArray.FromObject(SameContainerMaterials),
                };
                if (material != null)
                {
                    json["material"] = new JObject
                    {
                        ["name"] = material.Name,
                        ["file"] = material.File,
                        ["pathId"] = material.PathId,
                    };
                }
                if (textureExtras != null)
                {
                    json["unityTextures"] = textureExtras;
                }
                return json;
            }
        }

        private sealed class StaticMeshResolvedMaterial
        {
            public Material Material { get; init; }
            public string Name { get; init; }
            public string File { get; init; }
            public long PathId { get; init; }
        }

        private sealed class StaticMeshMaterialReference
        {
            public long RelationId { get; init; }
            public string RendererFile { get; init; }
            public long RendererPathId { get; init; }
            public string RendererType { get; init; }
            public string MaterialFile { get; init; }
            public long MaterialPathId { get; init; }
            public string MaterialName { get; init; }
        }

        private sealed class StaticMeshRendererBindingInfo
        {
            public string RendererFile { get; init; }
            public long RendererPathId { get; init; }
            public string RendererType { get; init; }
            public int MaterialCount { get; init; }
            public List<StaticMeshMaterialRefInfo> Materials { get; init; } = new List<StaticMeshMaterialRefInfo>();
        }

        private sealed class StaticMeshMaterialRefInfo
        {
            public string Name { get; init; }
            public string File { get; init; }
            public long PathId { get; init; }
        }

        public static bool ExportVideoClip(AssetItem item, string exportPath)
        {
            var m_VideoClip = (VideoClip)item.Asset;
            if (m_VideoClip.m_ExternalResources.m_Size > 0)
            {
                if (
                    !TryExportFile(
                        exportPath,
                        item,
                        Path.GetExtension(m_VideoClip.m_OriginalPath),
                        out var exportFullPath
                    )
                )
                    return false;
                m_VideoClip.m_VideoData.WriteData(exportFullPath);
                return true;
            }
            return false;
        }

        public static bool ExportMovieTexture(AssetItem item, string exportPath)
        {
            var m_MovieTexture = (MovieTexture)item.Asset;
            if (!TryExportFile(exportPath, item, ".ogv", out var exportFullPath))
                return false;
            File.WriteAllBytes(exportFullPath, m_MovieTexture.m_MovieData);
            return true;
        }

        public static bool ExportSprite(AssetItem item, string exportPath)
        {
            var type = Properties.Settings.Default.convertType;
            if (
                !TryExportFile(
                    exportPath,
                    item,
                    "." + type.ToString().ToLower(),
                    out var exportFullPath
                )
            )
                return false;
            var image = ((Sprite)item.Asset).GetImage();
            if (image != null)
            {
                using (image)
                {
                    using (var file = File.OpenWrite(exportFullPath))
                    {
                        image.WriteToStream(file, type);
                    }
                    return true;
                }
            }
            return false;
        }

        public static bool ExportRawFile(AssetItem item, string exportPath)
        {
            if (!TryExportFile(exportPath, item, ".dat", out var exportFullPath))
                return false;
            File.WriteAllBytes(exportFullPath, item.Asset.GetRawData());
            return true;
        }

        private static bool TryExportFile(
            string dir,
            AssetItem item,
            string extension,
            out string fullPath
        )
        {
            var fileName = FixFileName(item.Text);
            fullPath = Path.Combine(dir, $"{fileName}{extension}");
            if (!File.Exists(fullPath))
            {
                Directory.CreateDirectory(dir);
                return true;
            }
            if (Properties.Settings.Default.allowDuplicates)
            {
                for (int i = 0; ; i++)
                {
                    fullPath = Path.Combine(dir, $"{fileName} ({i}){extension}");
                    if (!File.Exists(fullPath))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool TryExportFolder(string dir, AssetItem item, out string fullPath)
        {
            var fileName = FixFileName(item.Text);
            fullPath = Path.Combine(dir, fileName);
            if (!Directory.Exists(fullPath))
            {
                return true;
            }
            if (Properties.Settings.Default.allowDuplicates)
            {
                for (int i = 0; ; i++)
                {
                    fullPath = Path.Combine(dir, $"{fileName} ({i})");
                    if (!Directory.Exists(fullPath))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public static bool ExportAnimationClip(AssetItem item, string exportPath)
        {
            if (!TryExportFile(exportPath, item, ".anim", out var exportFullPath))
                return false;
            var m_AnimationClip = (AnimationClip)item.Asset;
            var str = m_AnimationClip.Convert();
            if (string.IsNullOrEmpty(str))
                return false;
            File.WriteAllText(exportFullPath, str);
            var animationAssetPath = WriteAnimationAssetJson(item, m_AnimationClip, exportFullPath);
            AppendAssetCatalog(item, exportFullPath, "Animation", animationAssetPath);
            return true;
        }

        public static bool ExportAnimator(
            AssetItem item,
            string exportPath,
            List<AssetItem> animationList = null
        )
        {
            if (!TryExportFolder(exportPath, item, out var exportFullPath))
                return false;
            var exportFilePath = Path.Combine(exportFullPath, FixFileName(item.Text) + GetModelExtension());

            var m_Animator = (Animator)item.Asset;
            var options = new ModelConverter.Options()
            {
                imageFormat = Properties.Settings.Default.convertType,
                textureMode = CliExportOptions.TextureMode,
                game = Studio.Game,
                collectAnimations = CliExportOptions.CollectAnimations,
                exportAnimations = CliExportOptions.ExportEmbeddedAnimations,
                preferBakedHumanoidBodyAnimation = animationList != null,
                humanoidBakeSolver = CliExportOptions.HumanoidBakeSolver,
                exportMaterials = Properties.Settings.Default.exportMaterials,
                materials = new HashSet<Material>(),
                materialCache = SharedMaterialCache,
                textureCache = SharedTextureCache,
                meshVertexCache = SharedMeshVertexCache,
                meshVertexCacheOrder = SharedMeshVertexCacheOrder,
                profileMeasure = ProfileLogger.Measure,
                useAnimatorHierarchy = true,
                uvs = JsonConvert.DeserializeObject<Dictionary<string, (bool, int)>>(
                    Properties.Settings.Default.uvs
                ),
                texs = JsonConvert.DeserializeObject<Dictionary<string, int>>(
                    Properties.Settings.Default.texs
                ),
            };
            options.textureDataExists = (_, exportName) =>
                ExportedTextureExists(exportFilePath, exportName);
            ModelConverter convert;
            using (ProfileLogger.Measure("model_convert", GetModelProfileData(item, exportFilePath)))
            {
                convert =
                    animationList != null
                        ? new ModelConverter(
                            m_Animator,
                            options,
                            animationList.Select(x => (AnimationClip)x.Asset).ToArray()
                        )
                        : new ModelConverter(m_Animator, options);
            }
            if (convert.MeshList.Count == 0)
            {
                Logger.Verbose($"Animator {item.Text} has no mesh, skipping...");
                return false;
            }
            if (options.exportMaterials)
            {
                var materialExportPath = exportFullPath;
                Directory.CreateDirectory(materialExportPath);
                using (ProfileLogger.Measure("material_json_export", GetModelProfileData(item, exportFilePath, options.materials.Count)))
                {
                    foreach (var material in options.materials)
                    {
                        var matItem = new AssetItem(material);
                        ExportJSONFile(matItem, materialExportPath);
                    }
                }
            }
            ExportFbx(convert, exportFilePath, item);
            return true;
        }

        public static bool ExportGameObject(
            AssetItem item,
            string exportPath,
            List<AssetItem> animationList = null
        )
        {
            if (!TryExportFolder(exportPath, item, out var exportFullPath))
                return false;

            var m_GameObject = (GameObject)item.Asset;
            if (m_GameObject.m_Transform == null)
            {
                Logger.Verbose($"GameObject {m_GameObject.m_Name} has no Transform, skipping...");
                return false;
            }
            return ExportGameObject(
                m_GameObject,
                exportFullPath + Path.DirectorySeparatorChar,
                animationList
            );
        }

        public static bool ExportGameObject(
            GameObject gameObject,
            string exportPath,
            List<AssetItem> animationList = null
        )
        {
            var exportFullPath = exportPath + FixFileName(gameObject.m_Name) + GetModelExtension();
            var options = new ModelConverter.Options()
            {
                imageFormat = Properties.Settings.Default.convertType,
                textureMode = CliExportOptions.TextureMode,
                game = Studio.Game,
                collectAnimations = CliExportOptions.CollectAnimations,
                exportAnimations = CliExportOptions.ExportEmbeddedAnimations,
                preferBakedHumanoidBodyAnimation = animationList != null,
                humanoidBakeSolver = CliExportOptions.HumanoidBakeSolver,
                exportMaterials = Properties.Settings.Default.exportMaterials,
                materials = new HashSet<Material>(),
                materialCache = SharedMaterialCache,
                textureCache = SharedTextureCache,
                meshVertexCache = SharedMeshVertexCache,
                meshVertexCacheOrder = SharedMeshVertexCacheOrder,
                profileMeasure = ProfileLogger.Measure,
                useAnimatorHierarchy =
                    Properties.Settings.Default.exportSkins
                    || Studio.WorkMode == WorkMode.Library
                    || CliExportOptions.ExportEmbeddedAnimations
                    || CliExportOptions.CollectAnimations,
                uvs = JsonConvert.DeserializeObject<Dictionary<string, (bool, int)>>(
                    Properties.Settings.Default.uvs
                ),
                texs = JsonConvert.DeserializeObject<Dictionary<string, int>>(
                    Properties.Settings.Default.texs
                ),
            };
            options.textureDataExists = (_, exportName) =>
                ExportedTextureExists(exportFullPath, exportName);
            ModelConverter convert;
            using (ProfileLogger.Measure("model_convert", GetGameObjectProfileData(gameObject, exportFullPath)))
            {
                convert =
                    animationList != null
                        ? new ModelConverter(
                            gameObject,
                            options,
                            animationList.Select(x => (AnimationClip)x.Asset).ToArray()
                        )
                        : new ModelConverter(gameObject, options);
            }

            if (convert.MeshList.Count == 0)
            {
                Logger.Verbose($"GameObject {gameObject.m_Name} has no mesh, skipping...");
                return false;
            }
            if (options.exportMaterials && convert.MaterialList.Count == 0)
            {
                Logger.Warning($"GameObject {gameObject.m_Name} has no resolved materials, skipping FBX export.");
                return false;
            }
            if (options.exportMaterials)
            {
                using (ProfileLogger.Measure("material_json_export", GetGameObjectProfileData(gameObject, exportFullPath, options.materials.Count)))
                {
                    foreach (var material in options.materials)
                    {
                        var matItem = new AssetItem(material);
                        ExportJSONFile(matItem, exportPath);
                    }
                }
            }
            ExportFbx(convert, exportFullPath, gameObject);
            return true;
        }

        private static void ExportFbx(IImported convert, string exportPath, object source)
        {
            var outputPath = exportPath;
            using (ProfileLogger.Measure("model_write", GetImportedProfileData(convert, exportPath, source)))
            {
                switch (CliExportOptions.ModelFormat)
                {
                    case ModelExportFormat.Fbx:
                        ExportFbxModel(convert, exportPath);
                        break;
                    case ModelExportFormat.Glb:
                        outputPath = Path.ChangeExtension(exportPath, ".glb");
                        ExportGltfModel(convert, outputPath, true);
                        break;
                    default:
                        outputPath = Path.ChangeExtension(exportPath, ".gltf");
                        ExportGltfModel(convert, outputPath, false);
                        break;
                }
            }
            AppendModelAssetCatalog(convert, outputPath, source);
        }

        private static string GetModelExtension()
        {
            return CliExportOptions.ModelFormat switch
            {
                ModelExportFormat.Fbx => ".fbx",
                ModelExportFormat.Glb => ".glb",
                _ => ".gltf",
            };
        }

        private static void ExportFbxModel(IImported convert, string exportPath)
        {
            var exportOptions = new Fbx.ExportOptions()
            {
                eulerFilter = Properties.Settings.Default.eulerFilter,
                filterPrecision = (float)Properties.Settings.Default.filterPrecision,
                exportAllNodes = CliExportOptions.FbxExportAllNodes,
                exportSkins = Properties.Settings.Default.exportSkins,
                exportAnimations = CliExportOptions.ExportEmbeddedAnimations,
                exportBlendShape = Properties.Settings.Default.exportBlendShape,
                castToBone = Properties.Settings.Default.castToBone,
                boneSize = CliExportOptions.FbxBoneSize ?? (int)Properties.Settings.Default.boneSize,
                scaleFactor = CliExportOptions.FbxScaleFactor ?? (float)Properties.Settings.Default.scaleFactor,
                fbxVersion = Properties.Settings.Default.fbxVersion,
                fbxFormat = Properties.Settings.Default.fbxFormat,
                textureDirectory = GetSharedTextureDirectory(exportPath),
                localTextureDirectoryName = ".",
            };
            ModelExporter.ExportFbx(exportPath, convert, exportOptions);
        }

        private static void ExportGltfModel(IImported convert, string exportPath, bool binary)
        {
            var exportOptions = new Gltf.ExportOptions()
            {
                binary = binary,
                exportSkins = Properties.Settings.Default.exportSkins
                    || Studio.WorkMode == WorkMode.Library
                    || CliExportOptions.ExportEmbeddedAnimations,
                exportAnimations = CliExportOptions.ExportEmbeddedAnimations,
                textureDirectory = GetSharedTextureDirectory(exportPath),
                localTextureDirectoryName = "Textures",
                profileMeasure = ProfileLogger.Measure,
            };
            ModelExporter.ExportGltf(exportPath, convert, exportOptions);
        }

        private static string GetSharedTextureDirectory(string exportPath)
        {
            if (!string.IsNullOrWhiteSpace(CliExportOptions.OutputRoot))
            {
                var outputRoot = Path.GetFullPath(CliExportOptions.OutputRoot).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar
                );
                var fullExportPath = Path.GetFullPath(exportPath);
                if (
                    fullExportPath.StartsWith(
                        outputRoot + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase
                    )
                    || string.Equals(fullExportPath, outputRoot, StringComparison.OrdinalIgnoreCase)
                )
                {
                    return Path.Combine(outputRoot, "Textures", "_ModelDependencies");
                }
            }

            var fullPath = Path.GetFullPath(
                exportPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            );
            var marker = $"{Path.DirectorySeparatorChar}Models{Path.DirectorySeparatorChar}";
            var index = fullPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return null;
            }

            var libraryRoot = fullPath.Substring(0, index);
            return Path.Combine(libraryRoot, "Textures", "_ModelDependencies");
        }

        private static bool ExportedTextureExists(string exportPath, string exportName)
        {
            var textureDirectory = GetSharedTextureDirectory(exportPath);
            if (string.IsNullOrWhiteSpace(textureDirectory))
            {
                textureDirectory = Path.GetDirectoryName(
                    Path.GetFullPath(
                        exportPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    )
                );
            }

            if (string.IsNullOrWhiteSpace(textureDirectory))
            {
                return false;
            }

            var rawName = Path.GetFileNameWithoutExtension(exportName);
            var safeRaw = GetSafeTextureFileName(rawName);
            if (safeRaw.Length > 100)
            {
                safeRaw = safeRaw.Substring(0, 67) + "_" + safeRaw.Substring(safeRaw.Length - 32);
            }
            return File.Exists(Path.Combine(textureDirectory, $"{safeRaw}.png"));
        }

        private static string GetSafeTextureFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        private static void AppendModelAssetCatalog(IImported imported, string outputPath, object source)
        {
            if (string.IsNullOrWhiteSpace(CliExportOptions.OutputRoot))
            {
                return;
            }

            var sourceInfo = GetSourceInfo(source);
            var bonePaths = imported.MeshList?
                .SelectMany(x => x.BoneList ?? Enumerable.Empty<ImportedBone>())
                .Select(x => x.Path)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<string>();
            var nodePaths = CollectFramePaths(imported.RootFrame);
            var meshPaths = imported.MeshList?
                .Select(x => x.Path)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<string>();

            var avatarInfo = GetModelAvatarInfo(source);
            var skeletonInfo = BuildSkeletonInfo(imported, bonePaths, avatarInfo);
            var skeletonValidation = BuildHumanoidSkeletonValidation(imported, avatarInfo);
            var materialSummary = BuildExportedModelMaterialSummary(outputPath);
            var entry = new
            {
                kind = "Model",
                libraryRole = source is AssetItem sourceItem ? sourceItem.LibraryRole ?? "PrefabPrimary" : "PrefabPrimary",
                resourceKind = InferResourceKind(sourceInfo.name, sourceInfo.container, sourceInfo.source),
                exportedAt = DateTime.UtcNow.ToString("O"),
                name = sourceInfo.name,
                sourceType = sourceInfo.type,
                container = sourceInfo.container,
                source = sourceInfo.source,
                pathId = sourceInfo.pathId,
                output = outputPath,
                format = CliExportOptions.ModelFormat.ToString(),
                modelSource = CliExportOptions.ModelSource.ToString(),
                textureMode = CliExportOptions.TextureMode.ToString(),
                animationPackage = CliExportOptions.AnimationPackage.ToString(),
                nodeCount = CountFrames(imported.RootFrame),
                meshCount = imported.MeshList?.Count ?? 0,
                vertexCount = imported.MeshList?.Sum(x => x.VertexList?.Count ?? 0) ?? 0,
                materialCount = imported.MaterialList?.Count ?? 0,
                textureCount = imported.TextureList?.Count ?? 0,
                materialStatus = materialSummary?.status,
                materialStatusCounts = materialSummary?.statusCounts,
                materialNeedsCustomizationTint = materialSummary?.needsCustomizationTint,
                materialMissingRendererBinding = materialSummary?.missingRendererBinding,
                materialHasBaseColorTexture = materialSummary?.hasBaseColorTexture,
                materialHasNormalTexture = materialSummary?.hasNormalTexture,
                materialImageCount = materialSummary?.imageCount,
                animationCount = imported.AnimationList?.Count ?? 0,
                morphCount = imported.MorphList?.Count ?? 0,
                boneCount = bonePaths.Length,
                bonePaths = bonePaths.Take(512).ToArray(),
                bonePathsTruncated = bonePaths.Length > 512,
                nodePaths = nodePaths.Take(1024).ToArray(),
                nodePathsTruncated = nodePaths.Length > 1024,
                meshPaths = meshPaths.Take(512).ToArray(),
                meshPathsTruncated = meshPaths.Length > 512,
                skeletonHash = (string)skeletonInfo?["libraryId"],
                skeleton = skeletonInfo,
                skeletonValidation,
                avatar = avatarInfo,
            };
            AppendCatalogEntry(entry);
        }

        private static ModelMaterialCatalogSummary BuildExportedModelMaterialSummary(string outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath)
                || !string.Equals(Path.GetExtension(outputPath), ".gltf", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(outputPath))
            {
                return null;
            }

            try
            {
                var gltf = JObject.Parse(File.ReadAllText(outputPath));
                var materials = gltf["materials"] as JArray ?? new JArray();
                var statusCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var hasBaseColorTexture = false;
                var hasNormalTexture = false;
                var needsCustomizationTint = false;
                var missingRendererBinding = false;

                foreach (var materialToken in materials.OfType<JObject>())
                {
                    var pbr = materialToken["pbrMetallicRoughness"] as JObject;
                    hasBaseColorTexture |= pbr?["baseColorTexture"] != null;
                    hasNormalTexture |= materialToken["normalTexture"] != null;

                    var anime = materialToken["extras"]?["animeStudioMaterial"] as JObject;
                    var status = anime?["status"]?.ToString();
                    if (string.IsNullOrWhiteSpace(status))
                    {
                        status = pbr?["baseColorTexture"] != null || materialToken["normalTexture"] != null
                            ? "standardGltfMaterial"
                            : "unclassifiedMaterial";
                    }

                    statusCounts.TryGetValue(status, out var count);
                    statusCounts[status] = count + 1;
                    needsCustomizationTint |= string.Equals(status, "needsCustomizationTint", StringComparison.OrdinalIgnoreCase)
                        || (bool?)anime?["needsCustomizationTint"] == true;
                    missingRendererBinding |= string.Equals(status, "missingRendererMaterial", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(status, "needsRendererBinding", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(status, "rendererMaterialUnresolved", StringComparison.OrdinalIgnoreCase);
                }

                var statusSummary = statusCounts.Count == 0
                    ? "noMaterial"
                    : statusCounts.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).First().Key;
                return new ModelMaterialCatalogSummary
                {
                    status = statusSummary,
                    statusCounts = statusCounts
                        .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase),
                    needsCustomizationTint = needsCustomizationTint,
                    missingRendererBinding = missingRendererBinding,
                    hasBaseColorTexture = hasBaseColorTexture,
                    hasNormalTexture = hasNormalTexture,
                    imageCount = gltf["images"] is JArray images ? images.Count : 0,
                };
            }
            catch (Exception ex)
            {
                Logger.Verbose($"Unable to summarize glTF material state for catalog: {outputPath}. {ex.Message}");
                return new ModelMaterialCatalogSummary
                {
                    status = "materialSummaryFailed",
                    statusCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["materialSummaryFailed"] = 1,
                    },
                };
            }
        }

        private sealed class ModelMaterialCatalogSummary
        {
            public string status { get; init; }
            public Dictionary<string, int> statusCounts { get; init; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public bool needsCustomizationTint { get; init; }
            public bool missingRendererBinding { get; init; }
            public bool hasBaseColorTexture { get; init; }
            public bool hasNormalTexture { get; init; }
            public int imageCount { get; init; }
        }

        private static JObject GetModelAvatarInfo(object source)
        {
            var modelAsset = source is AssetItem item ? item.Asset : source as AnimeStudio.Object;
            var animator = modelAsset switch
            {
                Animator direct => direct,
                GameObject gameObject => FindAnimatorInHierarchy(gameObject),
                _ => null,
            };
            if (animator == null || !animator.m_Avatar.TryGet(out var avatar))
            {
                return null;
            }

            var humanBones = avatar.m_HumanDescription?.m_Human?
                .Where(x => !string.IsNullOrWhiteSpace(x.m_HumanName) && !string.IsNullOrWhiteSpace(x.m_BoneName))
                .Select(x => $"{x.m_HumanName}:{x.m_BoneName}")
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? Array.Empty<string>();
            var skeletonBones = avatar.m_HumanDescription?.m_Skeleton?
                .Where(x => !string.IsNullOrWhiteSpace(x.m_Name))
                .Select(x => string.IsNullOrWhiteSpace(x.m_ParentName) ? x.m_Name : $"{x.m_ParentName}/{x.m_Name}")
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? Array.Empty<string>();
            var skeletonBonePose = avatar.m_HumanDescription?.m_Skeleton?
                .Where(x => !string.IsNullOrWhiteSpace(x.m_Name))
                .Select(x => new
                {
                    name = x.m_Name,
                    parentName = x.m_ParentName,
                    position = new { x = x.m_Position.X, y = x.m_Position.Y, z = x.m_Position.Z },
                    rotation = new { x = x.m_Rotation.X, y = x.m_Rotation.Y, z = x.m_Rotation.Z, w = x.m_Rotation.W },
                    scale = new { x = x.m_Scale.X, y = x.m_Scale.Y, z = x.m_Scale.Z },
                })
                .Take(256)
                .ToArray() ?? Array.Empty<object>();
            var humanDescription = avatar.m_HumanDescription;

            return JObject.FromObject(new
            {
                name = avatar.m_Name,
                source = avatar.assetsFile?.originalPath ?? avatar.assetsFile?.fileName,
                pathId = avatar.m_PathID,
                hasHumanDescription = avatar.m_HumanDescription != null,
                humanBoneCount = avatar.m_HumanDescription?.m_Human?.Count ?? 0,
                skeletonBoneCount = avatar.m_HumanDescription?.m_Skeleton?.Count ?? 0,
                avatarSkeletonNodeCount = avatar.m_Avatar?.m_AvatarSkeleton?.m_Node?.Count ?? 0,
                humanBoneMapHash = humanBones.Length == 0 ? null : HashText(string.Join("\n", humanBones)),
                skeletonBoneNameHash = skeletonBones.Length == 0 ? null : HashText(string.Join("\n", skeletonBones)),
                humanBones = humanBones.Take(128).ToArray(),
                skeletonBones = skeletonBonePose,
                humanDescription = humanDescription == null ? null : new
                {
                    armTwist = humanDescription.m_ArmTwist,
                    foreArmTwist = humanDescription.m_ForeArmTwist,
                    upperLegTwist = humanDescription.m_UpperLegTwist,
                    legTwist = humanDescription.m_LegTwist,
                    armStretch = humanDescription.m_ArmStretch,
                    legStretch = humanDescription.m_LegStretch,
                    feetSpacing = humanDescription.m_FeetSpacing,
                    globalScale = humanDescription.m_GlobalScale,
                    rootMotionBoneName = humanDescription.m_RootMotionBoneName,
                    hasTranslationDoF = humanDescription.m_HasTranslationDoF,
                    hasExtraRoot = humanDescription.m_HasExtraRoot,
                    skeletonHasParents = humanDescription.m_SkeletonHasParents,
                },
            });
        }

        private static JObject BuildSkeletonInfo(IImported imported, string[] bonePaths, JObject avatarInfo)
        {
            var namePathHash = bonePaths.Length == 0 ? null : HashText(string.Join("\n", bonePaths));
            var hierarchySignature = imported.RootFrame == null ? null : BuildFrameHierarchySignature(imported.RootFrame);
            var hierarchyHash = string.IsNullOrWhiteSpace(hierarchySignature) ? null : HashText(hierarchySignature);
            var bindPoseSignature = BuildBindPoseSignature(imported);
            var bindPoseHash = string.IsNullOrWhiteSpace(bindPoseSignature) ? null : HashText(bindPoseSignature);
            var avatarHumanHash = (string)avatarInfo?["humanBoneMapHash"];
            var avatarSkeletonNameHash = (string)avatarInfo?["skeletonBoneNameHash"];
            var librarySeed = avatarHumanHash
                ?? (namePathHash != null && bindPoseHash != null ? $"{namePathHash}:{bindPoseHash}" : null)
                ?? namePathHash
                ?? hierarchyHash;
            if (librarySeed == null)
            {
                return null;
            }

            return JObject.FromObject(new
            {
                libraryId = HashText(librarySeed),
                namePathHash,
                hierarchyHash,
                bindPoseHash,
                avatarHumanHash,
                avatarSkeletonNameHash,
                boneCount = bonePaths.Length,
                nodeCount = CountFrames(imported.RootFrame),
                fingerprintVersion = 1,
                relationBasis = avatarHumanHash != null
                    ? "AvatarHumanDescription"
                    : bindPoseHash != null
                    ? "BonePathsAndBindPose"
                    : "BonePathsOrHierarchy",
            });
        }

        private static JObject BuildHumanoidSkeletonValidation(IImported imported, JObject avatarInfo)
        {
            var humanBoneMap = ParseAvatarHumanBoneMap(avatarInfo);
            if (humanBoneMap.Count == 0)
            {
                return JObject.FromObject(new
                {
                    status = "not_applicable",
                    rule = "Humanoid skeleton validation only applies when Unity Avatar HumanDescription is available. Static meshes and generic non-humanoid models are not treated as failed humanoid assets.",
                    hasAvatarHumanDescription = false,
                    mappedHumanBoneCount = 0,
                });
            }

            var frameByName = new Dictionary<string, ImportedFrame>(StringComparer.OrdinalIgnoreCase);
            CollectFrames(imported.RootFrame, frameByName);

            var requiredHumanBones = new[]
            {
                "Hips",
                "Spine",
                "Chest",
                "Neck",
                "Head",
                "LeftShoulder",
                "LeftUpperArm",
                "LeftLowerArm",
                "LeftHand",
                "RightShoulder",
                "RightUpperArm",
                "RightLowerArm",
                "RightHand",
                "LeftUpperLeg",
                "LeftLowerLeg",
                "LeftFoot",
                "RightUpperLeg",
                "RightLowerLeg",
                "RightFoot",
            };
            var optionalHumanBones = new[]
            {
                "UpperChest",
                "LeftToes",
                "RightToes",
                "Left Thumb Proximal",
                "Left Index Proximal",
                "Left Middle Proximal",
                "Left Ring Proximal",
                "Left Little Proximal",
                "Right Thumb Proximal",
                "Right Index Proximal",
                "Right Middle Proximal",
                "Right Ring Proximal",
                "Right Little Proximal",
            };
            var chains = new (string Name, string[] HumanBones)[]
            {
                ("spine", new[] { "Hips", "Spine", "Chest", "Neck", "Head" }),
                ("leftArm", new[] { "LeftShoulder", "LeftUpperArm", "LeftLowerArm", "LeftHand" }),
                ("rightArm", new[] { "RightShoulder", "RightUpperArm", "RightLowerArm", "RightHand" }),
                ("leftLeg", new[] { "Hips", "LeftUpperLeg", "LeftLowerLeg", "LeftFoot" }),
                ("rightLeg", new[] { "Hips", "RightUpperLeg", "RightLowerLeg", "RightFoot" }),
            };

            var missingHumanBones = requiredHumanBones
                .Where(x => !humanBoneMap.ContainsKey(x))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var missingFrames = requiredHumanBones
                .Where(x => humanBoneMap.TryGetValue(x, out var boneName) && !frameByName.ContainsKey(boneName))
                .Select(x => $"{x}:{humanBoneMap[x]}")
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var optionalPresent = optionalHumanBones
                .Count(x => humanBoneMap.TryGetValue(x, out var boneName) && frameByName.ContainsKey(boneName));
            var chainResults = chains
                .Select(x => BuildHumanoidChainValidation(x.Name, x.HumanBones, humanBoneMap, frameByName))
                .ToArray();
            var validChainCount = chainResults.Count(x => (bool)x["ok"]);
            var status = missingHumanBones.Length == 0 && missingFrames.Length == 0 && validChainCount == chains.Length
                ? "ok"
                : missingHumanBones.Length <= 2 && missingFrames.Length <= 2 && validChainCount >= chains.Length - 1
                    ? "warning"
                    : "failed";

            return JObject.FromObject(new
            {
                status,
                rule = "Humanoid skeleton validation derived from Unity Avatar HumanDescription plus exported frame hierarchy. This validates reusable human bone structure before animation binding.",
                hasAvatarHumanDescription = humanBoneMap.Count > 0,
                mappedHumanBoneCount = humanBoneMap.Count,
                requiredHumanBoneCount = requiredHumanBones.Length,
                requiredPresentCount = requiredHumanBones.Length - missingHumanBones.Length,
                requiredFramePresentCount = requiredHumanBones.Length - missingFrames.Length,
                optionalPresentCount = optionalPresent,
                chainCount = chains.Length,
                validChainCount,
                missingHumanBones,
                missingFrames,
                chains = chainResults,
            });
        }

        private static Dictionary<string, string> ParseAvatarHumanBoneMap(JObject avatarInfo)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var humanBones = avatarInfo?["humanBones"]?.Values<string>() ?? Enumerable.Empty<string>();
            foreach (var item in humanBones)
            {
                var separator = item.IndexOf(':');
                if (separator <= 0 || separator >= item.Length - 1)
                {
                    continue;
                }

                var humanBone = item.Substring(0, separator);
                var skeletonBone = item.Substring(separator + 1);
                result[humanBone] = skeletonBone;
            }

            return result;
        }

        private static JObject BuildHumanoidChainValidation(
            string name,
            string[] humanBones,
            Dictionary<string, string> humanBoneMap,
            Dictionary<string, ImportedFrame> frameByName)
        {
            var mappedBones = humanBones
                .Select(x => humanBoneMap.TryGetValue(x, out var boneName) ? $"{x}:{boneName}" : $"{x}:<missing>")
                .ToArray();
            var missing = humanBones
                .Where(x => !humanBoneMap.TryGetValue(x, out var boneName) || !frameByName.ContainsKey(boneName))
                .ToArray();
            var brokenLinks = new List<string>();
            for (var i = 1; i < humanBones.Length; i++)
            {
                if (!humanBoneMap.TryGetValue(humanBones[i - 1], out var parentBoneName)
                    || !humanBoneMap.TryGetValue(humanBones[i], out var childBoneName)
                    || !frameByName.TryGetValue(parentBoneName, out var parentFrame)
                    || !frameByName.TryGetValue(childBoneName, out var childFrame))
                {
                    continue;
                }

                if (!IsDescendantOf(childFrame, parentFrame))
                {
                    brokenLinks.Add($"{humanBones[i - 1]}->{humanBones[i]}");
                }
            }

            return JObject.FromObject(new
            {
                name,
                ok = missing.Length == 0 && brokenLinks.Count == 0,
                mappedBones,
                missing,
                brokenLinks,
            });
        }

        private static void CollectFrames(ImportedFrame frame, Dictionary<string, ImportedFrame> frameByName)
        {
            if (frame == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(frame.Name) && !frameByName.ContainsKey(frame.Name))
            {
                frameByName.Add(frame.Name, frame);
            }

            for (var i = 0; i < frame.Count; i++)
            {
                CollectFrames(frame[i], frameByName);
            }
        }

        private static bool IsDescendantOf(ImportedFrame child, ImportedFrame ancestor)
        {
            var current = child?.Parent;
            while (current != null)
            {
                if (ReferenceEquals(current, ancestor))
                {
                    return true;
                }

                current = current.Parent;
            }

            return false;
        }

        private static string BuildFrameHierarchySignature(ImportedFrame frame)
        {
            if (frame == null)
            {
                return null;
            }

            var childSignatures = new List<string>();
            for (var i = 0; i < frame.Count; i++)
            {
                childSignatures.Add(BuildFrameHierarchySignature(frame[i]));
            }
            childSignatures.Sort(StringComparer.Ordinal);
            return $"{NormalizeSkeletonName(frame.Name)}({string.Join(",", childSignatures)})";
        }

        private static string BuildBindPoseSignature(IImported imported)
        {
            var bones = imported.MeshList?
                .SelectMany(x => x.BoneList ?? Enumerable.Empty<ImportedBone>())
                .Where(x => !string.IsNullOrWhiteSpace(x.Path))
                .GroupBy(x => x.Path, StringComparer.Ordinal)
                .Select(x => $"{x.Key}:{QuantizeMatrix(x.First().Matrix)}")
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<string>();
            return bones.Length == 0 ? null : string.Join("\n", bones);
        }

        private static string QuantizeMatrix(Matrix4x4 matrix)
        {
            var values = new string[16];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = Math.Round(matrix[i], 4).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
            }
            return string.Join(",", values);
        }

        private static string NormalizeSkeletonName(string name)
        {
            return Regex.Replace((name ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9]+", string.Empty);
        }

        private static Animator FindAnimatorInHierarchy(GameObject root)
        {
            if (root == null)
            {
                return null;
            }

            foreach (var componentPtr in root.m_Components ?? Enumerable.Empty<PPtr<Component>>())
            {
                if (componentPtr.TryGet<Animator>(out var animator))
                {
                    return animator;
                }
            }

            foreach (var childPtr in root.m_Transform?.m_Children ?? Enumerable.Empty<PPtr<Transform>>())
            {
                if (childPtr.TryGet(out var childTransform) && childTransform.m_GameObject.TryGet(out var child))
                {
                    var nested = FindAnimatorInHierarchy(child);
                    if (nested != null)
                    {
                        return nested;
                    }
                }
            }
            return null;
        }

        private static string WriteAnimationAssetJson(AssetItem item, AnimationClip clip, string animOutputPath)
        {
            if (clip == null || string.IsNullOrWhiteSpace(animOutputPath))
            {
                return null;
            }

            var sidecarPath = Path.Combine(
                Path.GetDirectoryName(animOutputPath),
                $"{Path.GetFileNameWithoutExtension(animOutputPath)}.animation_asset.json"
            );
            var animationInfo = AnalyzeAnimationClip(clip);
            var asset = BuildAnimationAssetJson(item, clip, animOutputPath, animationInfo);
            File.WriteAllText(sidecarPath, JsonConvert.SerializeObject(asset, Formatting.Indented));
            return sidecarPath;
        }

        private static object BuildAnimationAssetJson(
            AssetItem item,
            AnimationClip clip,
            string animOutputPath,
            AnimationClipInfo animationInfo
        )
        {
            var bindings = clip.m_ClipBindingConstant?.genericBindings ?? new List<GenericBinding>();
            var tos = clip.FindTOS();
            var bindingAssets = bindings
                .Select((binding, index) => BuildAnimationBindingAsset(binding, index, tos))
                .ToArray();
            var muscleBindings = bindingAssets
                .Where(x => string.Equals((string)x["category"], "HumanoidMuscle", StringComparison.OrdinalIgnoreCase))
                .Take(256)
                .ToArray();

            return new
            {
                kind = "AnimationAsset",
                schemaVersion = 1,
                exportedAt = DateTime.UtcNow.ToString("O"),
                name = item.Text,
                sourceType = item.TypeString,
                container = item.Container,
                source = item.SourceFile?.originalPath ?? item.SourceFile?.fileName,
                pathId = item.m_PathID,
                output = animOutputPath,
                sampleRate = clip.m_SampleRate,
                duration = GetAnimationDuration(clip),
                legacy = clip.m_Legacy,
                wrapMode = clip.m_WrapMode,
                animationType = animationInfo.animationType,
                classificationNotes = animationInfo.classificationNotes,
                bindingSummary = new
                {
                    genericBindingCount = bindings.Count,
                    transformBindingCount = animationInfo.transformBindingCount,
                    coreTransformBindingCount = animationInfo.coreTransformBindingCount,
                    humanoidBindingCount = animationInfo.humanoidBindingCount,
                    blendShapeBindingCount = animationInfo.blendShapeBindingCount,
                    auxiliaryBindingCount = animationInfo.auxiliaryBindingCount,
                    unknownBindingCount = animationInfo.unknownBindingCount,
                    pptrCurveCount = clip.m_PPtrCurves?.Count ?? 0,
                    eventCount = clip.m_Events?.Count ?? 0,
                },
                curveContainers = new
                {
                    rotationCurves = clip.m_RotationCurves?.Count ?? 0,
                    compressedRotationCurves = clip.m_CompressedRotationCurves?.Count ?? 0,
                    eulerCurves = clip.m_EulerCurves?.Count ?? 0,
                    positionCurves = clip.m_PositionCurves?.Count ?? 0,
                    scaleCurves = clip.m_ScaleCurves?.Count ?? 0,
                    floatCurves = clip.m_FloatCurves?.Count ?? 0,
                    pptrCurves = clip.m_PPtrCurves?.Count ?? 0,
                },
                transformBindingPaths = animationInfo.transformBindingPaths,
                humanoid = BuildHumanoidAnimationAsset(clip, animationInfo, muscleBindings),
                decoded = BuildDecodedAnimationCurves(clip),
                bindings = bindingAssets.Take(1024).ToArray(),
                truncatedBindings = bindings.Count > 1024,
            };
        }

        private static object BuildDecodedAnimationCurves(AnimationClip clip)
        {
            try
            {
                if (!clip.m_Legacy && clip.m_MuscleClip != null)
                {
                    var decoded = AnimationClipConverter.Process(clip);
                    return new
                    {
                        status = "ok",
                        coordinateSpace = "UnitySerialized",
                        note = "Decoded from Unity ACL/streamed/dense/constant clip data before model-space export conversion.",
                        curveCounts = new
                        {
                            translations = decoded.Translations?.Count ?? 0,
                            rotations = decoded.Rotations?.Count ?? 0,
                            scales = decoded.Scales?.Count ?? 0,
                            eulers = decoded.Eulers?.Count ?? 0,
                            floats = decoded.Floats?.Count ?? 0,
                            pptrs = decoded.PPtrs?.Count ?? 0,
                            keyframes =
                                CountVector3Keyframes(decoded.Translations)
                                + CountQuaternionKeyframes(decoded.Rotations)
                                + CountVector3Keyframes(decoded.Scales)
                                + CountVector3Keyframes(decoded.Eulers)
                                + CountFloatKeyframes(decoded.Floats)
                                + CountPPtrKeyframes(decoded.PPtrs),
                        },
                        translations = ToVector3CurveAssets(decoded.Translations),
                        rotations = ToQuaternionCurveAssets(decoded.Rotations),
                        scales = ToVector3CurveAssets(decoded.Scales),
                        eulers = ToVector3CurveAssets(decoded.Eulers),
                        floats = ToFloatCurveAssets(decoded.Floats),
                        pptrs = ToPPtrCurveAssets(decoded.PPtrs),
                    };
                }

                return new
                {
                    status = "ok",
                    coordinateSpace = "UnitySerialized",
                    note = "Decoded from legacy AnimationClip curve containers.",
                    curveCounts = new
                    {
                        translations = clip.m_PositionCurves?.Count ?? 0,
                        rotations = clip.m_RotationCurves?.Count ?? 0,
                        scales = clip.m_ScaleCurves?.Count ?? 0,
                        eulers = clip.m_EulerCurves?.Count ?? 0,
                        floats = clip.m_FloatCurves?.Count ?? 0,
                        pptrs = clip.m_PPtrCurves?.Count ?? 0,
                        keyframes =
                            CountVector3Keyframes(clip.m_PositionCurves)
                            + CountQuaternionKeyframes(clip.m_RotationCurves)
                            + CountVector3Keyframes(clip.m_ScaleCurves)
                            + CountVector3Keyframes(clip.m_EulerCurves)
                            + CountFloatKeyframes(clip.m_FloatCurves)
                            + CountPPtrKeyframes(clip.m_PPtrCurves),
                    },
                    translations = ToVector3CurveAssets(clip.m_PositionCurves),
                    rotations = ToQuaternionCurveAssets(clip.m_RotationCurves),
                    scales = ToVector3CurveAssets(clip.m_ScaleCurves),
                    eulers = ToVector3CurveAssets(clip.m_EulerCurves),
                    floats = ToFloatCurveAssets(clip.m_FloatCurves),
                    pptrs = ToPPtrCurveAssets(clip.m_PPtrCurves),
                };
            }
            catch (Exception ex)
            {
                return new
                {
                    status = "error",
                    coordinateSpace = "UnitySerialized",
                    error = ex.Message,
                    note = "The raw .anim file and binding metadata were still exported; only decoded JSON curves failed.",
                };
            }
        }

        private static object[] ToVector3CurveAssets(IEnumerable<Vector3Curve> curves)
        {
            return curves?
                .OrderBy(x => x.path, StringComparer.OrdinalIgnoreCase)
                .Select(x => new
                {
                    path = x.path,
                    keyframes = x.curve?.m_Curve?.Select(ToVector3KeyframeAsset).ToArray() ?? Array.Empty<object>(),
                })
                .Cast<object>()
                .ToArray() ?? Array.Empty<object>();
        }

        private static object[] ToQuaternionCurveAssets(IEnumerable<QuaternionCurve> curves)
        {
            return curves?
                .OrderBy(x => x.path, StringComparer.OrdinalIgnoreCase)
                .Select(x => new
                {
                    path = x.path,
                    keyframes = x.curve?.m_Curve?.Select(ToQuaternionKeyframeAsset).ToArray() ?? Array.Empty<object>(),
                })
                .Cast<object>()
                .ToArray() ?? Array.Empty<object>();
        }

        private static object[] ToFloatCurveAssets(IEnumerable<FloatCurve> curves)
        {
            return curves?
                .OrderBy(x => x.path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.attribute, StringComparer.OrdinalIgnoreCase)
                .Select(x => new
                {
                    path = x.path,
                    attribute = x.attribute,
                    classID = x.classID.ToString(),
                    flags = x.flags,
                    category = x.classID == ClassIDType.Animator ? "HumanoidMuscleOrAnimator" :
                        x.classID == ClassIDType.SkinnedMeshRenderer ? "BlendShapeOrRenderer" : "Float",
                    keyframes = x.curve?.m_Curve?.Select(ToFloatKeyframeAsset).ToArray() ?? Array.Empty<object>(),
                })
                .Cast<object>()
                .ToArray() ?? Array.Empty<object>();
        }

        private static object[] ToPPtrCurveAssets(IEnumerable<PPtrCurve> curves)
        {
            return curves?
                .OrderBy(x => x.path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.attribute, StringComparer.OrdinalIgnoreCase)
                .Select(x => new
                {
                    path = x.path,
                    attribute = x.attribute,
                    classID = ((ClassIDType)x.classID).ToString(),
                    flags = x.flags,
                    keyframes = x.curve?.Select(x => new
                    {
                        time = x.time,
                        value = new
                        {
                            fileId = x.value?.m_FileID,
                            pathId = x.value?.m_PathID,
                            name = x.value?.Name,
                        },
                    }).ToArray() ?? Array.Empty<object>(),
                })
                .Cast<object>()
                .ToArray() ?? Array.Empty<object>();
        }

        private static object ToVector3KeyframeAsset(Keyframe<Vector3> keyframe)
        {
            return new
            {
                time = keyframe.time,
                value = ToJsonVector3(keyframe.value),
                inSlope = ToJsonVector3(keyframe.inSlope),
                outSlope = ToJsonVector3(keyframe.outSlope),
                weightedMode = keyframe.weightedMode,
            };
        }

        private static object ToQuaternionKeyframeAsset(Keyframe<Quaternion> keyframe)
        {
            return new
            {
                time = keyframe.time,
                value = ToJsonQuaternion(keyframe.value),
                inSlope = ToJsonQuaternion(keyframe.inSlope),
                outSlope = ToJsonQuaternion(keyframe.outSlope),
                weightedMode = keyframe.weightedMode,
            };
        }

        private static object ToFloatKeyframeAsset(Keyframe<Float> keyframe)
        {
            return new
            {
                time = keyframe.time,
                value = keyframe.value.Value,
                inSlope = keyframe.inSlope.Value,
                outSlope = keyframe.outSlope.Value,
                weightedMode = keyframe.weightedMode,
            };
        }

        private static int CountVector3Keyframes(IEnumerable<Vector3Curve> curves)
        {
            return curves?.Sum(x => x.curve?.m_Curve?.Count ?? 0) ?? 0;
        }

        private static int CountQuaternionKeyframes(IEnumerable<QuaternionCurve> curves)
        {
            return curves?.Sum(x => x.curve?.m_Curve?.Count ?? 0) ?? 0;
        }

        private static int CountFloatKeyframes(IEnumerable<FloatCurve> curves)
        {
            return curves?.Sum(x => x.curve?.m_Curve?.Count ?? 0) ?? 0;
        }

        private static int CountPPtrKeyframes(IEnumerable<PPtrCurve> curves)
        {
            return curves?.Sum(x => x.curve?.Count ?? 0) ?? 0;
        }

        private static JObject BuildAnimationBindingAsset(GenericBinding binding, int index, Dictionary<uint, string> tos)
        {
            var path = ResolveAnimationBindingPath(binding, tos);
            var category = ResolveAnimationBindingCategory(binding);
            var attributeName = ResolveAnimationBindingAttribute(binding, category);

            return JObject.FromObject(new
            {
                index,
                pathHash = binding.path,
                path,
                typeID = binding.typeID.ToString(),
                customType = binding.customType,
                customTypeName = Enum.IsDefined(typeof(BindingCustomType), binding.customType)
                    ? ((BindingCustomType)binding.customType).ToString()
                    : null,
                attribute = binding.attribute,
                attributeName,
                category,
                isPPtrCurve = binding.isPPtrCurve != 0,
                isIntCurve = binding.isIntCurve != 0,
            });
        }

        private static object BuildHumanoidAnimationAsset(
            AnimationClip clip,
            AnimationClipInfo animationInfo,
            JObject[] muscleBindings
        )
        {
            var muscleClip = clip.m_MuscleClip;
            return new
            {
                present = animationInfo.humanoidBindingCount > 0 || muscleClip != null,
                hasMuscleClip = muscleClip != null,
                reusableAssetStatus = animationInfo.humanoidBindingCount > 0 || muscleClip != null
                    ? "UnityHumanoidOrMuscleDataCaptured"
                    : "NotHumanoid",
                gltfPlaybackStatus = animationInfo.humanoidBindingCount > 0 || muscleClip != null
                    ? "RequiresHumanoidSolverOrBake"
                    : "DirectTransformIfBindingsExist",
                muscleBindingCount = muscleBindings.Length,
                muscleBindings = muscleBindings,
                muscleClip = muscleClip == null
                    ? null
                    : new
                    {
                        startTime = muscleClip.m_StartTime,
                        stopTime = muscleClip.m_StopTime,
                        duration = Math.Max(0, muscleClip.m_StopTime - muscleClip.m_StartTime),
                        averageSpeed = ToJsonVector3(muscleClip.m_AverageSpeed),
                        averageAngularSpeed = muscleClip.m_AverageAngularSpeed,
                        orientationOffsetY = muscleClip.m_OrientationOffsetY,
                        level = muscleClip.m_Level,
                        cycleOffset = muscleClip.m_CycleOffset,
                        startX = ToJsonXForm(muscleClip.m_StartX),
                        stopX = ToJsonXForm(muscleClip.m_StopX),
                        leftFootStartX = ToJsonXForm(muscleClip.m_LeftFootStartX),
                        rightFootStartX = ToJsonXForm(muscleClip.m_RightFootStartX),
                        motionStartX = ToJsonXForm(muscleClip.m_MotionStartX),
                        motionStopX = ToJsonXForm(muscleClip.m_MotionStopX),
                        indexArrayCount = muscleClip.m_IndexArray?.Length ?? 0,
                        valueArrayDeltaCount = muscleClip.m_ValueArrayDelta?.Count ?? 0,
                        valueArrayReferencePoseCount = muscleClip.m_ValueArrayReferencePose?.Length ?? 0,
                        flags = new
                        {
                            mirror = muscleClip.m_Mirror,
                            loopTime = muscleClip.m_LoopTime,
                            loopBlend = muscleClip.m_LoopBlend,
                            loopBlendOrientation = muscleClip.m_LoopBlendOrientation,
                            loopBlendPositionY = muscleClip.m_LoopBlendPositionY,
                            loopBlendPositionXZ = muscleClip.m_LoopBlendPositionXZ,
                            startAtOrigin = muscleClip.m_StartAtOrigin,
                            keepOriginalOrientation = muscleClip.m_KeepOriginalOrientation,
                            keepOriginalPositionY = muscleClip.m_KeepOriginalPositionY,
                            keepOriginalPositionXZ = muscleClip.m_KeepOriginalPositionXZ,
                            heightFromFeet = muscleClip.m_HeightFromFeet,
                            reducedDeltaValue = muscleClip.m_ReducedDeltaValue,
                        },
                    },
            };
        }

        private static string ResolveAnimationBindingCategory(GenericBinding binding)
        {
            if (binding == null)
            {
                return "Unknown";
            }
            if ((BindingCustomType)binding.customType == BindingCustomType.AnimatorMuscle)
            {
                return "HumanoidMuscle";
            }
            if ((BindingCustomType)binding.customType == BindingCustomType.BlendShape
                || binding.typeID == ClassIDType.SkinnedMeshRenderer)
            {
                return "BlendShapeOrRenderer";
            }
            if (binding.typeID == ClassIDType.Transform)
            {
                return "Transform";
            }
            if (binding.isPPtrCurve != 0)
            {
                return "PPtr";
            }
            return "Unknown";
        }

        private static string ResolveAnimationBindingAttribute(GenericBinding binding, string category)
        {
            if (binding == null)
            {
                return null;
            }
            try
            {
                if (string.Equals(category, "HumanoidMuscle", StringComparison.OrdinalIgnoreCase))
                {
                    return binding.GetHumanoidMuscle().ToAttributeString();
                }
            }
            catch
            {
                return $"HumanoidMuscle_{binding.attribute}";
            }

            if (binding.typeID == ClassIDType.Transform)
            {
                return binding.attribute switch
                {
                    1 => "localPosition",
                    2 => "localRotation",
                    3 => "localScale",
                    4 => "localEulerAngles",
                    _ => $"TransformAttribute_{binding.attribute}",
                };
            }

            if (string.Equals(category, "BlendShapeOrRenderer", StringComparison.OrdinalIgnoreCase))
            {
                return ((BindingCustomType)binding.customType) == BindingCustomType.BlendShape
                    ? $"blendShape_{binding.attribute}"
                    : $"rendererOrSkinnedMeshAttribute_{binding.attribute}";
            }

            return $"attribute_{binding.attribute}";
        }

        private static object ToJsonVector3(Vector3 value)
        {
            return new[] { value.X, value.Y, value.Z };
        }

        private static object ToJsonQuaternion(Quaternion value)
        {
            return new[] { value.X, value.Y, value.Z, value.W };
        }

        private static object ToJsonXForm(XForm value)
        {
            return new
            {
                t = ToJsonVector3(value.t),
                q = ToJsonQuaternion(value.q),
                s = ToJsonVector3(value.s),
            };
        }

        private static void AppendAssetCatalog(AssetItem item, string outputPath, string kind, string animationAssetPath = null)
        {
            if (string.IsNullOrWhiteSpace(CliExportOptions.OutputRoot))
            {
                return;
            }

            var animationInfo = item.Asset is AnimationClip infoClip ? AnalyzeAnimationClip(infoClip) : null;
            var audioClip = item.Asset as AudioClip;
            var audioKind = audioClip == null
                ? null
                : ClassifyAudioClip(audioClip, item.Text, item.Container, item.SourceFile?.originalPath ?? item.SourceFile?.fileName);
            var entry = new
            {
                kind,
                resourceKind = audioKind ?? InferResourceKind(item.Text, item.Container, item.SourceFile?.originalPath ?? item.SourceFile?.fileName),
                exportedAt = DateTime.UtcNow.ToString("O"),
                name = item.Text,
                sourceType = item.TypeString,
                container = item.Container,
                source = item.SourceFile?.originalPath ?? item.SourceFile?.fileName,
                pathId = item.m_PathID,
                output = outputPath,
                animationAsset = animationAssetPath,
                sampleRate = item.Asset is AnimationClip clip ? clip.m_SampleRate : (float?)null,
                duration = item.Asset is AnimationClip durationClip ? GetAnimationDuration(durationClip) : (float?)null,
                curveCount = item.Asset is AnimationClip curveClip ? GetAnimationCurveCount(curveClip) : (int?)null,
                eventCount = item.Asset is AnimationClip eventClip ? eventClip.m_Events?.Count ?? 0 : (int?)null,
                legacy = item.Asset is AnimationClip legacyClip ? legacyClip.m_Legacy : (bool?)null,
                animationType = animationInfo?.animationType,
                hasMuscleClip = animationInfo?.hasMuscleClip,
                transformBindingCount = animationInfo?.transformBindingCount,
                coreTransformBindingCount = animationInfo?.coreTransformBindingCount,
                humanoidBindingCount = animationInfo?.humanoidBindingCount,
                blendShapeBindingCount = animationInfo?.blendShapeBindingCount,
                auxiliaryBindingCount = animationInfo?.auxiliaryBindingCount,
                unknownBindingCount = animationInfo?.unknownBindingCount,
                transformBindingPaths = animationInfo?.transformBindingPaths,
                classificationNotes = animationInfo?.classificationNotes,
                audioKind,
                audioLength = audioClip?.m_Length,
                audioChannels = audioClip?.m_Channels,
                audioFrequency = audioClip?.m_Frequency,
                audioBitsPerSample = audioClip?.m_BitsPerSample,
                audioCompressionFormat = audioClip?.m_CompressionFormat.ToString(),
                audioDataSize = audioClip?.m_AudioData.Size,
            };
            AppendCatalogEntry(entry);
        }

        private static AnimationClipInfo AnalyzeAnimationClip(AnimationClip clip)
        {
            var bindings = clip.m_ClipBindingConstant?.genericBindings ?? new List<GenericBinding>();
            var tos = clip.FindTOS();
            var transformPaths = bindings
                .Where(x => x.typeID == ClassIDType.Transform)
                .Select(x => ResolveAnimationBindingPath(x, tos))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var coreTransformCount = transformPaths.Count(IsCoreAnimationPath);
            var auxiliaryCount = transformPaths.Count(x => IsAuxiliaryAnimationPath(x) || IsTwistOrHelperAnimationPath(x));
            var transformBindingCount = bindings.Count(x => x.typeID == ClassIDType.Transform)
                + (clip.m_RotationCurves?.Count ?? 0)
                + (clip.m_PositionCurves?.Count ?? 0)
                + (clip.m_ScaleCurves?.Count ?? 0)
                + (clip.m_EulerCurves?.Count ?? 0);
            var blendShapeBindingCount = bindings.Count(x => x.typeID == ClassIDType.SkinnedMeshRenderer);
            var humanoidBindingCount = bindings.Count(x => x.typeID == ClassIDType.Animator);
            var unknownBindingCount = bindings.Count(x => x.typeID != ClassIDType.Transform && x.typeID != ClassIDType.SkinnedMeshRenderer && x.typeID != ClassIDType.Animator);
            var hasMuscleClip = clip.m_MuscleClip != null;
            var notes = new List<string>();

            string animationType;
            if (humanoidBindingCount > 0)
            {
                animationType = transformBindingCount > 0 ? "MixedHumanoidTransform" : "HumanoidMuscleAnimation";
                notes.Add("Animator/humanoid bindings are present; current glTF export may need humanoid/muscle baking for full body motion.");
            }
            else if (coreTransformCount >= 3)
            {
                animationType = "TransformBodyAnimation";
            }
            else if (transformBindingCount > 0 && coreTransformCount == 0 && auxiliaryCount > 0)
            {
                animationType = "AuxiliaryAnimation";
                notes.Add("Transform bindings mainly target helper, socket, point, or twist nodes.");
            }
            else if (blendShapeBindingCount > 0 && transformBindingCount == 0)
            {
                animationType = "BlendShapeAnimation";
            }
            else if (transformBindingCount > 0)
            {
                animationType = "TransformAnimation";
                notes.Add("Transform bindings exist but core body bone coverage is low.");
            }
            else if (hasMuscleClip)
            {
                animationType = "HumanoidMuscleAnimation";
                notes.Add("Muscle clip data exists but no direct transform body bindings were classified.");
            }
            else
            {
                animationType = "UnknownAnimation";
            }

            return new AnimationClipInfo(
                animationType,
                hasMuscleClip,
                transformBindingCount,
                coreTransformCount,
                humanoidBindingCount,
                blendShapeBindingCount,
                auxiliaryCount,
                unknownBindingCount,
                transformPaths.Take(64).ToArray(),
                notes.ToArray()
            );
        }

        private static string ResolveAnimationBindingPath(GenericBinding binding, Dictionary<uint, string> tos)
        {
            if (binding == null)
            {
                return null;
            }
            return tos != null && tos.TryGetValue(binding.path, out var path)
                ? path
                : null;
        }

        internal static bool IsCoreAnimationPath(string path)
        {
            var text = NormalizeAnimationPath(path);
            if (IsAuxiliaryAnimationPath(path) || IsTwistOrHelperAnimationPath(path))
            {
                return false;
            }
            return text.Contains("pelvis")
                || text.Contains("spine")
                || text.Contains("neck")
                || text.Contains("head")
                || text.Contains("clavicle")
                || text.Contains("upperarm")
                || text.Contains("forearm")
                || text.Contains("hand")
                || text.Contains("thigh")
                || text.Contains("calf")
                || text.Contains("foot")
                || text.Contains("toe");
        }

        private static bool IsAuxiliaryAnimationPath(string path)
        {
            var text = NormalizeAnimationPath(path);
            return text.Contains("point") || text.Contains("socket") || text.Contains("attach");
        }

        private static bool IsTwistOrHelperAnimationPath(string path)
        {
            var text = NormalizeAnimationPath(path);
            return text.Contains("twist") || text.Contains("helper");
        }

        internal static string NormalizeAnimationPath(string path)
        {
            return Regex.Replace((path ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9]+", string.Empty);
        }

        private sealed record AnimationClipInfo(
            string animationType,
            bool hasMuscleClip,
            int transformBindingCount,
            int coreTransformBindingCount,
            int humanoidBindingCount,
            int blendShapeBindingCount,
            int auxiliaryBindingCount,
            int unknownBindingCount,
            string[] transformBindingPaths,
            string[] classificationNotes
        );

        private static float? GetAnimationDuration(AnimationClip clip)
        {
            if (clip?.m_MuscleClip != null)
            {
                return Math.Max(0, clip.m_MuscleClip.m_StopTime - clip.m_MuscleClip.m_StartTime);
            }
            return null;
        }

        private static int GetAnimationCurveCount(AnimationClip clip)
        {
            if (clip == null)
            {
                return 0;
            }
            return (clip.m_RotationCurves?.Count ?? 0)
                + (clip.m_CompressedRotationCurves?.Count ?? 0)
                + (clip.m_EulerCurves?.Count ?? 0)
                + (clip.m_PositionCurves?.Count ?? 0)
                + (clip.m_ScaleCurves?.Count ?? 0)
                + (clip.m_FloatCurves?.Count ?? 0)
                + (clip.m_PPtrCurves?.Count ?? 0);
        }

        private static void AppendCatalogEntry(object entry)
        {
            var catalogPath = Path.Combine(CliExportOptions.OutputRoot, "asset_catalog.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(catalogPath));
            File.AppendAllText(catalogPath, JsonConvert.SerializeObject(entry) + Environment.NewLine);
        }

        private static (string type, string name, string container, string source, long pathId) GetSourceInfo(object source)
        {
            return source switch
            {
                AssetItem item => (
                    item.TypeString,
                    item.Text,
                    item.Container,
                    item.SourceFile?.originalPath ?? item.SourceFile?.fileName,
                    item.m_PathID
                ),
                GameObject gameObject => (
                    nameof(GameObject),
                    gameObject.m_Name,
                    null,
                    gameObject.assetsFile?.originalPath ?? gameObject.assetsFile?.fileName,
                    gameObject.m_PathID
                ),
                _ => (source?.GetType().Name, source?.ToString(), null, null, 0),
            };
        }

        private static string InferResourceKind(string name, string container, string source)
        {
            var text = string.Join(
                "/",
                new[] { container, source, name }.Where(x => !string.IsNullOrWhiteSpace(x))
            ).Replace('\\', '/').ToLowerInvariant();

            if (Regex.IsMatch(text, @"(^|/)character/(pc|player)|(^|/)characters?(/|$)|(^|/)characterprefabs?(/|$)|(^|/)(pc|player)(/|$)"))
            {
                return "Character";
            }
            if (Regex.IsMatch(text, @"(^|/)avatars?(/|$)|(^|/)bodies(/|$)|(^|/)costumes?(/|$)"))
            {
                return "Avatar";
            }
            if (Regex.IsMatch(text, @"(^|/)character/npc|(^|/)npc(/|$)"))
            {
                return "NPC";
            }
            if (Regex.IsMatch(text, @"(^|/)units?(/|$)"))
            {
                if (Regex.IsMatch(text, @"(^|/)vehicles?(/|$)|tank|ship|boat|submarine|carrier|frigate|corvette|helicopter|fighter|aircraft|artiller|cannon"))
                {
                    return "Vehicle";
                }
                if (Regex.IsMatch(text, @"(^|/)animals?(/|$)|horse|deer|bear|camel|elephant|mammoth"))
                {
                    return "Animal";
                }
                return "Unit";
            }
            if (Regex.IsMatch(text, @"(^|/)(accessor|accessories|hat|hats|hair|weapon|weapons|shield|shields)(/|$)"))
            {
                return "Accessory";
            }
            if (Regex.IsMatch(text, @"(^|/)stage|court|scene|map"))
            {
                return "Stage";
            }
            if (Regex.IsMatch(text, @"(^|/)(terrain|landscape|surface|ground|levelbuild|levelbuildelements|environment|world|locations|rooms|nature|vegetation|foliage|tree|trees|rock|rocks)(/|$)"))
            {
                return "Environment";
            }
            if (Regex.IsMatch(text, @"(^|/)(building|buildings|structure|structures|wall|walls|floor|floors|roof|roofs|house|houses|castle|pieces)(/|$)|(^|/)gameelements/pieces(/|$)"))
            {
                return "Buildings";
            }
            if (Regex.IsMatch(text, @"(^|/)ball|basketball"))
            {
                return "Ball";
            }
            if (Regex.IsMatch(text, @"(^|/)trophy|prop|props|object|item|weapon"))
            {
                return "Prop";
            }
            if (Regex.IsMatch(text, @"(^|/)shader|\.shader$"))
            {
                return "Shader";
            }
            if (Regex.IsMatch(text, @"(^|/)animation|\.anim$"))
            {
                return "Animation";
            }
            return "Unknown";
        }

        private static int CountFrames(ImportedFrame frame)
        {
            if (frame == null)
            {
                return 0;
            }

            var count = 1;
            for (var i = 0; i < frame.Count; i++)
            {
                count += CountFrames(frame[i]);
            }
            return count;
        }

        private static string[] CollectFramePaths(ImportedFrame frame)
        {
            if (frame == null)
            {
                return Array.Empty<string>();
            }

            var paths = new List<string>();
            CollectFramePaths(frame, paths);
            return paths
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();
        }

        private static void CollectFramePaths(ImportedFrame frame, List<string> paths)
        {
            if (frame == null)
            {
                return;
            }

            paths.Add(frame.Path);
            for (var i = 0; i < frame.Count; i++)
            {
                CollectFramePaths(frame[i], paths);
            }
        }

        private static string HashText(string text)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty));
            return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
        }

        private static Dictionary<string, object> GetModelProfileData(
            AssetItem item,
            string exportPath,
            int? materialCount = null
        )
        {
            var data = new Dictionary<string, object>
            {
                ["type"] = item.TypeString,
                ["name"] = item.Text,
                ["container"] = item.Container,
                ["source"] = item.SourceFile?.originalPath ?? item.SourceFile?.fileName,
                ["pathId"] = item.m_PathID,
                ["output"] = exportPath,
                ["format"] = CliExportOptions.ModelFormat.ToString(),
                ["textureMode"] = CliExportOptions.TextureMode.ToString(),
                ["animationPackage"] = CliExportOptions.AnimationPackage.ToString(),
            };
            if (materialCount.HasValue)
            {
                data["materialCount"] = materialCount.Value;
            }
            return data;
        }

        private static Dictionary<string, object> GetGameObjectProfileData(
            GameObject gameObject,
            string exportPath,
            int? materialCount = null
        )
        {
            var data = new Dictionary<string, object>
            {
                ["type"] = nameof(GameObject),
                ["name"] = gameObject.m_Name,
                ["source"] = gameObject.assetsFile?.originalPath ?? gameObject.assetsFile?.fileName,
                ["pathId"] = gameObject.m_PathID,
                ["output"] = exportPath,
                ["format"] = CliExportOptions.ModelFormat.ToString(),
                ["textureMode"] = CliExportOptions.TextureMode.ToString(),
                ["animationPackage"] = CliExportOptions.AnimationPackage.ToString(),
            };
            if (materialCount.HasValue)
            {
                data["materialCount"] = materialCount.Value;
            }
            return data;
        }

        private static Dictionary<string, object> GetImportedProfileData(
            IImported imported,
            string exportPath,
            object source
        )
        {
            var data = new Dictionary<string, object>
            {
                ["output"] = exportPath,
                ["format"] = CliExportOptions.ModelFormat.ToString(),
                ["textureMode"] = CliExportOptions.TextureMode.ToString(),
                ["animationPackage"] = CliExportOptions.AnimationPackage.ToString(),
                ["meshCount"] = imported.MeshList?.Count ?? 0,
                ["materialCount"] = imported.MaterialList?.Count ?? 0,
                ["textureCount"] = imported.TextureList?.Count ?? 0,
                ["animationCount"] = imported.AnimationList?.Count ?? 0,
            };
            switch (source)
            {
                case AssetItem item:
                    data["type"] = item.TypeString;
                    data["name"] = item.Text;
                    data["container"] = item.Container;
                    data["source"] = item.SourceFile?.originalPath ?? item.SourceFile?.fileName;
                    data["pathId"] = item.m_PathID;
                    break;
                case GameObject gameObject:
                    data["type"] = nameof(GameObject);
                    data["name"] = gameObject.m_Name;
                    data["source"] = gameObject.assetsFile?.originalPath ?? gameObject.assetsFile?.fileName;
                    data["pathId"] = gameObject.m_PathID;
                    break;
            }
            return data;
        }

        public static bool ExportDumpFile(AssetItem item, string exportPath)
        {
            if (!TryExportFile(exportPath, item, ".txt", out var exportFullPath))
                return false;
            var str = item.Asset.Dump();
            if (str != null)
            {
                File.WriteAllText(exportFullPath, str);
                return true;
            }
            return false;
        }

        public static bool ExportConvertFile(AssetItem item, string exportPath)
        {
            switch (item.Type)
            {
                case ClassIDType.GameObject:
                    return ExportGameObject(item, exportPath);
                case ClassIDType.Texture2D:
                    return ExportTexture2D(item, exportPath);
                case ClassIDType.Texture2DArray:
                    return ExportTexture2DArray(item, exportPath);
                case ClassIDType.AudioClip:
                    return ExportAudioClip(item, exportPath);
                case ClassIDType.Shader:
                    return ExportShader(item, exportPath);
                case ClassIDType.TextAsset:
                    return ExportTextAsset(item, exportPath);
                case ClassIDType.MonoBehaviour:
                    return ExportMonoBehaviour(item, exportPath);
                case ClassIDType.Font:
                    return ExportFont(item, exportPath);
                case ClassIDType.Mesh:
                    return ExportMesh(item, exportPath);
                case ClassIDType.VideoClip:
                    return ExportVideoClip(item, exportPath);
                case ClassIDType.MovieTexture:
                    return ExportMovieTexture(item, exportPath);
                case ClassIDType.Sprite:
                    return ExportSprite(item, exportPath);
                case ClassIDType.Animator:
                    return ExportAnimator(item, exportPath);
                case ClassIDType.AnimationClip:
                    return ExportAnimationClip(item, exportPath);
                case ClassIDType.MiHoYoBinData:
                    return ExportMiHoYoBinData(item, exportPath);
                case ClassIDType.Material:
                    return ExportJSONFile(item, exportPath);
                default:
                    return ExportRawFile(item, exportPath);
            }
        }

        public static bool ExportJSONFile(AssetItem item, string exportPath)
        {
            if (!TryExportFile(exportPath, item, ".json", out var exportFullPath))
                return false;

            var settings = new JsonSerializerSettings();
            settings.Converters.Add(new StringEnumConverter());
            var str = JsonConvert.SerializeObject(item.Asset, Formatting.Indented, settings);
            File.WriteAllText(exportFullPath, str);
            return true;
        }

        public static string FixFileName(string str)
        {
            str = (str ?? string.Empty).Trim().TrimEnd('.');
            if (str.Length == 0)
                return "_";
            if (str.Length >= 260)
                return Path.GetRandomFileName();
            return Path.GetInvalidFileNameChars()
                .Aggregate(str, (current, c) => current.Replace(c, '_'));
        }
    }
}
