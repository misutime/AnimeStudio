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
        public static bool IncludeVfx { get; set; }
        public static AnimeStudio.TextureExportMode TextureMode { get; set; } = AnimeStudio.TextureExportMode.Raw;
        public static string OutputRoot { get; set; }
        public static bool ExportFullDecodedAnimationCurves { get; set; }

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
        private static readonly object CatalogWriteLock = new object();
        private static readonly object StaticMeshTextureWriteLock = new object();
        private static readonly object StaticMeshMaterialBindingCacheLock = new object();
        private static readonly Dictionary<Material, ImportedMaterial> SharedMaterialCache = new Dictionary<Material, ImportedMaterial>();
        private static readonly Dictionary<Texture2D, ImportedTexture> SharedTextureCache = new Dictionary<Texture2D, ImportedTexture>();
        private static readonly Dictionary<string, List<ImportedVertex>> SharedMeshVertexCache = new Dictionary<string, List<ImportedVertex>>();
        private static readonly Queue<string> SharedMeshVertexCacheOrder = new Queue<string>();
        private static string StaticMeshMaterialBindingCacheIndexPath;
        private static Dictionary<string, List<StaticMeshMaterialReference>> StaticMeshMaterialBindingCache;

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

            var profileData = GetModelProfileData(item, gltfPath);
            using (ProfileLogger.Measure("static_mesh_gltf_export", profileData))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(gltfPath));
                var binName = BuildSafeGltfBufferFileName(item.Text, item.m_PathID);
                var binPath = Path.Combine(Path.GetDirectoryName(gltfPath), binName);
                var bufferViews = new JArray();
                var accessors = new JArray();
                var primitives = new JArray();
                StaticMeshMaterialBinding materialBinding;
                using (ProfileLogger.Measure("static_mesh_material_binding", profileData))
                {
                    materialBinding = ResolveStaticMeshMaterialBinding(item, gltfPath);
                }

                long bufferLength;
                using (ProfileLogger.Measure("static_mesh_bin_write", profileData))
                using (var stream = File.Create(binPath))
                {
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
                    bufferLength = stream.Length;
                }

                using (ProfileLogger.Measure("static_mesh_json_write", profileData))
                {
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
                            ["byteLength"] = bufferLength,
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
                }
                using (ProfileLogger.Measure("static_mesh_normalize", profileData))
                {
                    NormalizeGltfForViewerCompatibility(gltfPath);
                }
                using (ProfileLogger.Measure("static_mesh_readme", profileData))
                {
                    WriteStaticMeshReadme(gltfPath, item, mesh, materialBinding);
                }
                using (ProfileLogger.Measure("static_mesh_catalog_append", profileData))
                {
                    AppendStaticMeshAssetCatalog(item, gltfPath, materialBinding);
                }
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
            var refs = GetStaticMeshMaterialReferences(indexPath, meshFile, item.m_PathID);
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

        internal static void PreloadStaticMeshMaterialBindingCache()
        {
            var indexPath = SQLiteSourceIndexRuntime.CurrentLoadResult?.DatabasePath;
            if (string.IsNullOrWhiteSpace(indexPath) || !File.Exists(indexPath))
            {
                return;
            }

            EnsureStaticMeshMaterialBindingCache(indexPath);
        }

        private static List<StaticMeshMaterialReference> GetStaticMeshMaterialReferences(string indexPath, string meshFile, long meshPathId)
        {
            var cache = EnsureStaticMeshMaterialBindingCache(indexPath);
            if (cache != null && cache.TryGetValue(BuildStaticMeshMaterialCacheKey(meshFile, meshPathId), out var refs))
            {
                return refs;
            }

            return QueryStaticMeshMaterialReferences(indexPath, meshFile, meshPathId);
        }

        private static Dictionary<string, List<StaticMeshMaterialReference>> EnsureStaticMeshMaterialBindingCache(string indexPath)
        {
            lock (StaticMeshMaterialBindingCacheLock)
            {
                if (StaticMeshMaterialBindingCache != null
                    && string.Equals(StaticMeshMaterialBindingCacheIndexPath, indexPath, StringComparison.OrdinalIgnoreCase))
                {
                    return StaticMeshMaterialBindingCache;
                }

                using (ProfileLogger.Measure("static_mesh_material_binding_cache_build", new Dictionary<string, object>
                {
                    ["sourceIndex"] = indexPath,
                }))
                {
                    StaticMeshMaterialBindingCache = QueryAllStaticMeshMaterialReferences(indexPath)
                        .GroupBy(x => BuildStaticMeshMaterialCacheKey(x.MeshFile, x.MeshPathId), StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            x => x.Key,
                            x => x.OrderBy(y => y.RendererFile, StringComparer.OrdinalIgnoreCase)
                                .ThenBy(y => y.RendererPathId)
                                .ThenBy(y => y.RelationId)
                                .ToList(),
                            StringComparer.OrdinalIgnoreCase);
                    StaticMeshMaterialBindingCacheIndexPath = indexPath;
                    Logger.Info($"Cached StaticMesh Renderer->Material bindings for {StaticMeshMaterialBindingCache.Count} Mesh asset(s).");
                    return StaticMeshMaterialBindingCache;
                }
            }
        }

        private static string BuildStaticMeshMaterialCacheKey(string meshFile, long meshPathId)
        {
            return $"{meshFile ?? string.Empty}#{meshPathId}";
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
                        MeshFile = meshFile ?? string.Empty,
                        MeshPathId = meshPathId,
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

        private static List<StaticMeshMaterialReference> QueryAllStaticMeshMaterialReferences(string indexPath)
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
    SELECT r.to_file AS mesh_file,
           r.to_path_id AS mesh_path_id,
           r.from_file,
           r.from_path_id,
           r.from_type
    FROM source_relations r
    WHERE r.relation = 'skinnedMeshRenderer.mesh'
),
mesh_filters AS (
    SELECT mf.to_file AS mesh_file,
           mf.to_path_id AS mesh_path_id,
           mf.from_file AS mesh_filter_file,
           mf.from_path_id AS mesh_filter_path_id
    FROM source_relations mf
    WHERE mf.relation = 'meshFilter.mesh'
),
game_objects AS (
    SELECT mf.mesh_file,
           mf.mesh_path_id,
           go.to_file AS go_file,
           go.to_path_id AS go_path_id
    FROM mesh_filters mf
    JOIN source_relations go ON go.from_file = mf.mesh_filter_file AND go.from_path_id = mf.mesh_filter_path_id
    WHERE go.relation = 'component.gameObject'
),
static_renderers AS (
    SELECT go.mesh_file,
           go.mesh_path_id,
           r.from_file,
           r.from_path_id,
           r.from_type
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
       r.mesh_file,
       r.mesh_path_id,
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
ORDER BY r.mesh_file, r.mesh_path_id, r.from_file, r.from_path_id, mat.id;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    refs.Add(new StaticMeshMaterialReference
                    {
                        RelationId = reader.GetInt64(0),
                        MeshFile = reader.GetString(1),
                        MeshPathId = reader.GetInt64(2),
                        RendererFile = reader.GetString(3),
                        RendererPathId = reader.GetInt64(4),
                        RendererType = reader.GetString(5),
                        MaterialFile = reader.GetString(6),
                        MaterialPathId = reader.GetInt64(7),
                        MaterialName = reader.GetString(8),
                    });
                }
            }
            catch (Exception e) when (e is IOException || e is SqliteException || e is InvalidDataException)
            {
                Logger.Warning($"Unable to pre-cache StaticMesh material bindings from SQLite source index: {e.Message}");
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
            var hasBaseColorTexture = false;
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
                        hasBaseColorTexture = true;
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
            ProtectPreviewBaseColorFactor(pbr, diffuse, hasBaseColorTexture, gltfMaterial);
            if (ShouldExportPreviewBlend(material, diffuse, hasBaseColorTexture))
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
            lock (StaticMeshTextureWriteLock)
            {
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

        private static string BuildSafeGltfBufferFileName(string sourceName, long stableId)
        {
            var name = GetSafeAsciiFileStem(sourceName);
            return $"{name}_{stableId:X}.bin";
        }

        private static string GetSafeAsciiFileStem(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "buffer";
            }

            var builder = new StringBuilder();
            foreach (var ch in value)
            {
                if ((ch >= 'a' && ch <= 'z')
                    || (ch >= 'A' && ch <= 'Z')
                    || (ch >= '0' && ch <= '9')
                    || ch == '_' || ch == '-' || ch == '.')
                {
                    builder.Append(ch);
                }
                else if (char.IsWhiteSpace(ch))
                {
                    builder.Append('_');
                }
            }

            var result = builder.ToString().Trim('.', '_', '-');
            if (string.IsNullOrWhiteSpace(result))
            {
                result = "buffer";
            }
            if (result.Length > 64)
            {
                result = result.Substring(0, 64).Trim('.', '_', '-');
            }
            return result;
        }

        private static bool IsViewerSafeGltfUri(string uri)
        {
            foreach (var ch in uri)
            {
                if (ch > 127 || char.IsWhiteSpace(ch) || ch == '(' || ch == ')' || ch == '[' || ch == ']')
                {
                    return false;
                }
            }
            return true;
        }

        private static void ProtectPreviewBaseColorFactor(JObject pbr, Color original, bool hasBaseColorTexture, JObject material)
        {
            if (!hasBaseColorTexture)
            {
                return;
            }

            var protectedFactor = BuildProtectedPreviewBaseColorFactor(original, out var reason);
            if (protectedFactor == null)
            {
                return;
            }

            PreserveOriginalBaseColorFactor(material, original, reason);
            pbr["baseColorFactor"] = protectedFactor;
        }

        private static JArray BuildProtectedPreviewBaseColorFactor(Color original, out string reason)
        {
            reason = null;
            var rgbNearlyBlack = original.R <= 0.02f && original.G <= 0.02f && original.B <= 0.02f;
            var transparent = original.A <= 0.001f;
            if (!rgbNearlyBlack && !transparent)
            {
                return null;
            }

            reason = transparent
                ? "Unity material color alpha is 0, but the material has a base color texture. The glTF preview keeps the texture visible and preserves the original factor in extras."
                : "Unity material color is near black, but the material has a base color texture. The glTF preview keeps the texture visible and preserves the original factor in extras.";
            return new JArray(1.0, 1.0, 1.0, 1.0);
        }

        private static void PreserveOriginalBaseColorFactor(JObject material, Color original, string reason)
        {
            var extras = material["extras"] as JObject;
            if (extras == null)
            {
                extras = new JObject();
                material["extras"] = extras;
            }

            var anime = extras["animeStudioMaterial"] as JObject;
            if (anime == null)
            {
                anime = new JObject();
                extras["animeStudioMaterial"] = anime;
            }

            anime["originalBaseColorFactor"] = new JArray(original.R, original.G, original.B, original.A);
            anime["previewBaseColorFactorProtected"] = true;
            anime["previewBaseColorFactorReason"] = reason;
        }

        private static Color? ReadColorFactor(JArray factor)
        {
            if (factor == null || factor.Count < 4)
            {
                return null;
            }

            return new Color(
                (float)factor[0],
                (float)factor[1],
                (float)factor[2],
                (float)factor[3]);
        }

        private static bool ShouldExportPreviewBlend(Material material, Color diffuse, bool hasBaseColorTexture)
        {
            if (hasBaseColorTexture && diffuse.A <= 0.001f)
            {
                return false;
            }

            return diffuse.A < 0.999f || GetUnityFloat(material, "_Surface", 0.0f) > 0.5f;
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
            public string MeshFile { get; init; }
            public long MeshPathId { get; init; }
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
            ProfileLogger.Event("animation_export_start", GetAnimationProfileData(item, exportFullPath));
            string str;
            using (ProfileLogger.Measure("animation_convert_yaml", GetAnimationProfileData(item, exportFullPath)))
            {
                str = m_AnimationClip.Convert();
            }
            if (string.IsNullOrEmpty(str))
                return false;
            using (ProfileLogger.Measure("animation_write_yaml", GetAnimationProfileData(item, exportFullPath, str.Length)))
            {
                File.WriteAllText(exportFullPath, str);
            }
            string animationAssetPath;
            using (ProfileLogger.Measure("animation_write_sidecar", GetAnimationProfileData(item, exportFullPath)))
            {
                animationAssetPath = WriteAnimationAssetJson(item, m_AnimationClip, exportFullPath);
            }
            AppendAssetCatalog(item, exportFullPath, "Animation", animationAssetPath);
            ProfileLogger.Event("animation_export_done", GetAnimationProfileData(item, exportFullPath));
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
            if (!binary)
            {
                NormalizeGltfForViewerCompatibility(exportPath);
            }
        }

        private static void NormalizeGltfForViewerCompatibility(string gltfPath)
        {
            if (string.IsNullOrWhiteSpace(gltfPath)
                || !File.Exists(gltfPath)
                || !string.Equals(Path.GetExtension(gltfPath), ".gltf", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                var gltf = JObject.Parse(File.ReadAllText(gltfPath));
                var changed = false;

                changed |= NormalizeGltfBufferUris(gltfPath, gltf);
                changed |= ProtectGltfPreviewMaterials(gltf);

                if (changed)
                {
                    File.WriteAllText(gltfPath, gltf.ToString(Formatting.Indented));
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"glTF viewer compatibility normalize failed: {gltfPath}. {ex.Message}");
            }
        }

        private static bool NormalizeGltfBufferUris(string gltfPath, JObject gltf)
        {
            // f3d/VTK 对 glTF URI 比较严格，空格、括号和中文文件名会直接导致模型加载失败。
            // 这里把外部 bin 统一挪成 ASCII 稳定名，glTF 里只引用这个安全文件名。
            var buffers = gltf["buffers"] as JArray;
            if (buffers == null || buffers.Count == 0)
            {
                return false;
            }

            var changed = false;
            var directory = Path.GetDirectoryName(gltfPath);
            for (var i = 0; i < buffers.Count; i++)
            {
                if (buffers[i] is not JObject buffer)
                {
                    continue;
                }

                var uri = (string)buffer["uri"];
                if (string.IsNullOrWhiteSpace(uri) || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var decodedUri = Uri.UnescapeDataString(uri);
                if (IsViewerSafeGltfUri(uri))
                {
                    continue;
                }

                var sourcePath = Path.GetFullPath(Path.Combine(directory, decodedUri.Replace('/', Path.DirectorySeparatorChar)));
                if (!File.Exists(sourcePath))
                {
                    Logger.Warning($"glTF buffer uri normalize skipped, file not found: {sourcePath}");
                    continue;
                }

                var newName = BuildSafeGltfBufferFileName(Path.GetFileNameWithoutExtension(gltfPath), i);
                var targetPath = Path.Combine(directory, newName);
                if (!string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(sourcePath, targetPath, true);
                    File.Delete(sourcePath);
                }

                buffer["uri"] = newName;
                changed = true;
            }

            return changed;
        }

        private static bool ProtectGltfPreviewMaterials(JObject gltf)
        {
            // Unity 自定义 shader 里的 _Color/_BaseColor 不一定是最终 PBR 颜色。
            // 如果已有 base color 贴图，黑色或 alpha=0 的颜色因子会让 f3d 里整模变黑/透明，所以预览层保护为可见。
            var materials = gltf["materials"] as JArray;
            if (materials == null)
            {
                return false;
            }

            var changed = false;
            foreach (var material in materials.OfType<JObject>())
            {
                var pbr = material["pbrMetallicRoughness"] as JObject;
                if (pbr == null)
                {
                    continue;
                }

                var factor = ReadColorFactor(pbr["baseColorFactor"] as JArray);
                if (factor == null)
                {
                    continue;
                }

                var hasBaseColorTexture = pbr["baseColorTexture"] != null;
                if (!hasBaseColorTexture)
                {
                    continue;
                }

                var protectedFactor = BuildProtectedPreviewBaseColorFactor(factor.Value, out var reason);
                if (protectedFactor == null)
                {
                    continue;
                }

                PreserveOriginalBaseColorFactor(material, factor.Value, reason);
                pbr["baseColorFactor"] = protectedFactor;
                if (string.Equals((string)material["alphaMode"], "BLEND", StringComparison.OrdinalIgnoreCase)
                    && factor.Value.A <= 0.001f)
                {
                    material.Remove("alphaMode");
                }
                changed = true;
            }

            return changed;
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
            var embeddedAnimationCount = CountWrittenGltfAnimations(outputPath);
            var importedAnimationListCount = imported.AnimationList?.Count ?? 0;
            var conversionIssues = imported is ModelConverter modelConverter
                ? modelConverter.ConversionIssues ?? new List<ModelConversionIssue>()
                : new List<ModelConversionIssue>();
            var conversionIssueTypes = conversionIssues
                .GroupBy(x => x.Kind ?? "unknown", StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x.Count())
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
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
                unresolvedModelDependencyCount = conversionIssues.Count,
                unresolvedModelDependencyTypes = conversionIssueTypes,
                unresolvedModelDependencies = conversionIssues.Take(64).ToArray(),
                unresolvedModelDependenciesTruncated = conversionIssues.Count > 64,
                animationCount = embeddedAnimationCount,
                embeddedAnimationCount,
                importedAnimationListCount,
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

        private static int CountWrittenGltfAnimations(string outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath)
                || !string.Equals(Path.GetExtension(outputPath), ".gltf", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(outputPath))
            {
                return 0;
            }

            try
            {
                // catalog 的 animationCount 表示实际写进 glTF 的内嵌动画数量。
                // 模型可用动画候选必须看 model_animations / SQLite 关系表，不能用导入阶段的临时 AnimationList 代替。
                var gltf = JObject.Parse(File.ReadAllText(outputPath));
                return gltf["animations"] is JArray animations ? animations.Count : 0;
            }
            catch (Exception e) when (e is IOException || e is JsonException || e is UnauthorizedAccessException)
            {
                Logger.Warning($"Unable to count written glTF animations for catalog: {outputPath}. {e.GetType().Name}: {e.Message}");
                return 0;
            }
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
            var humanBoneDetails = BuildHumanBoneDetails(avatar.m_HumanDescription);
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
            var internalSolver = BuildAvatarInternalSolverInfo(avatar);

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
                humanBoneDetails,
                skeletonBones = skeletonBonePose,
                internalSolver,
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

        private static object BuildAvatarInternalSolverInfo(Avatar avatar)
        {
            var human = avatar?.m_Avatar?.m_Human;
            var skeleton = human?.m_Skeleton;
            if (human == null || skeleton?.m_Node == null || skeleton.m_AxesArray == null || human.m_HumanBoneIndex == null)
            {
                return null;
            }

            var avatarSkeleton = avatar.m_Avatar?.m_AvatarSkeleton;

            // 这些是 Humanoid/Muscle 离线求解必需的 Unity Avatar 内部轴系数据。
            // 只保存通用序列化结构，不写游戏私有规则。
            return new
            {
                version = 1,
                source = "Unity AvatarConstant.m_Human",
                rule = "Used by AnimeStudio internal Humanoid/Muscle to target skeleton TRS solver. Unity bake may compare against this data but is not the production dependency.",
                humanBoneIndex = human.m_HumanBoneIndex,
                scale = human.m_Scale,
                hasTranslationDoF = human.m_HasTDoF,
                root = ToJsonXForm(human.m_RootX),
                human = new
                {
                    // AvatarConstant.m_Human 里的左右手索引和 bone mass 是 Unity 原始求解上下文。
                    // 先完整保留，后续用 oracle 判断它们是否参与手臂/前臂 zero base 或 twist 公式。
                    hasLeftHand = human.m_HasLeftHand,
                    hasRightHand = human.m_HasRightHand,
                    leftHandBoneIndex = human.m_LeftHand?.m_HandBoneIndex ?? Array.Empty<int>(),
                    rightHandBoneIndex = human.m_RightHand?.m_HandBoneIndex ?? Array.Empty<int>(),
                    humanBoneMass = human.m_HumanBoneMass ?? Array.Empty<float>(),
                },
                twist = new
                {
                    arm = human.m_ArmTwist,
                    foreArm = human.m_ForeArmTwist,
                    upperLeg = human.m_UpperLegTwist,
                    leg = human.m_LegTwist,
                    armStretch = human.m_ArmStretch,
                    legStretch = human.m_LegStretch,
                    feetSpacing = human.m_FeetSpacing,
                },
                humanBoneLimits = BuildHumanBoneLimitsByName(avatar.m_HumanDescription),
                humanSkeletonIndexArray = avatar.m_Avatar?.m_HumanSkeletonIndexArray ?? Array.Empty<int>(),
                humanSkeletonReverseIndexArray = avatar.m_Avatar?.m_HumanSkeletonReverseIndexArray ?? Array.Empty<int>(),
                skeleton = new
                {
                    nodeCount = skeleton.m_Node.Count,
                    axesCount = skeleton.m_AxesArray.Count,
                    humanSkeletonPoseCount = human.m_SkeletonPose?.m_X?.Length ?? 0,
                    avatarDefaultPoseCount = avatar.m_Avatar?.m_DefaultPose?.m_X?.Length ?? 0,
                    nodes = skeleton.m_Node.Select((node, index) =>
                    {
                        var path = TryGetAvatarSkeletonPath(avatar, skeleton, index);
                        return new
                        {
                            index,
                            parentId = node.m_ParentId,
                            axesId = node.m_AxesId,
                            // Humanoid/Muscle 离线求解必须知道 Unity Avatar 节点对应哪个 glTF 节点。
                            // 这里使用 Avatar 自带的 TOS 路径表，不按骨骼数量或游戏命名猜。
                            path,
                            name = GetLastPathSegment(path),
                        };
                    }).ToArray(),
                    axes = skeleton.m_AxesArray.Select((axes, index) => new
                    {
                        index,
                        preQ = ToJsonVector4(axes.m_PreQ),
                        postQ = ToJsonVector4(axes.m_PostQ),
                        sign = ToJsonVector3Or4As3(axes.m_Sgn),
                        limitMin = ToJsonVector3Or4As3(axes.m_Limit?.m_Min),
                        limitMax = ToJsonVector3Or4As3(axes.m_Limit?.m_Max),
                        length = axes.m_Length,
                        type = axes.m_Type,
                    }).ToArray(),
                    // Unity Humanoid/Muscle 求解不只有 axes，还依赖 Avatar 序列化里的参考姿态。
                    // 这些 pose 全部来自 AvatarConstant，不按骨骼名或游戏规则猜，后续内部 solver
                    // 用它们和 Unity bake oracle 对齐，避免手脚反折时只能凭截图调公式。
                    humanSkeletonPose = ToJsonXFormArray(human.m_SkeletonPose?.m_X, skeleton.m_Node.Count),
                    avatarDefaultPose = ToJsonXFormArray(avatar.m_Avatar?.m_DefaultPose?.m_X, avatarSkeleton?.m_Node?.Count ?? 0),
                },
                avatarSkeleton = avatarSkeleton == null ? null : new
                {
                    nodeCount = avatarSkeleton.m_Node?.Count ?? 0,
                    axesCount = avatarSkeleton.m_AxesArray?.Count ?? 0,
                    poseCount = avatar.m_Avatar?.m_AvatarSkeletonPose?.m_X?.Length ?? 0,
                    defaultPoseCount = avatar.m_Avatar?.m_DefaultPose?.m_X?.Length ?? 0,
                    nodes = avatarSkeleton.m_Node?.Select((node, index) =>
                    {
                        var path = TryGetAvatarSkeletonPath(avatar, avatarSkeleton, index);
                        return new
                        {
                            index,
                            parentId = node.m_ParentId,
                            axesId = node.m_AxesId,
                            path,
                            name = GetLastPathSegment(path),
                        };
                    }).ToArray() ?? Array.Empty<object>(),
                    pose = ToJsonXFormArray(avatar.m_Avatar?.m_AvatarSkeletonPose?.m_X, avatarSkeleton.m_Node?.Count ?? 0),
                    defaultPose = ToJsonXFormArray(avatar.m_Avatar?.m_DefaultPose?.m_X, avatarSkeleton.m_Node?.Count ?? 0),
                },
                rootMotion = new
                {
                    boneIndex = avatar.m_Avatar?.m_RootMotionBoneIndex ?? -1,
                    boneX = ToJsonXForm(avatar.m_Avatar?.m_RootMotionBoneX ?? XForm.Zero),
                    skeletonNodeCount = avatar.m_Avatar?.m_RootMotionSkeleton?.m_Node?.Count ?? 0,
                    skeletonPoseCount = avatar.m_Avatar?.m_RootMotionSkeletonPose?.m_X?.Length ?? 0,
                    skeletonIndexArray = avatar.m_Avatar?.m_RootMotionSkeletonIndexArray ?? Array.Empty<int>(),
                },
            };
        }

        private static object[] BuildHumanBoneDetails(HumanDescription humanDescription)
        {
            if (humanDescription?.m_Human == null || humanDescription.m_Human.Count == 0)
            {
                return Array.Empty<object>();
            }

            return humanDescription.m_Human
                .Where(x => !string.IsNullOrWhiteSpace(x.m_HumanName) || !string.IsNullOrWhiteSpace(x.m_BoneName))
                .Select(x => new
                {
                    humanName = x.m_HumanName,
                    boneName = x.m_BoneName,
                    // HumanDescription 里的 per-bone limit 是 Unity Avatar 复建和 muscle 公式诊断的原始输入。
                    // 保留原字段，不按游戏或骨骼名称推断含义。
                    limit = ToJsonSkeletonBoneLimit(x.m_Limit),
                })
                .ToArray();
        }

        private static object BuildHumanBoneLimitsByName(HumanDescription humanDescription)
        {
            if (humanDescription?.m_Human == null || humanDescription.m_Human.Count == 0)
            {
                return null;
            }

            var result = new JObject();
            foreach (var bone in humanDescription.m_Human
                .Where(x => !string.IsNullOrWhiteSpace(x.m_HumanName))
                .OrderBy(x => x.m_HumanName, StringComparer.OrdinalIgnoreCase))
            {
                result[bone.m_HumanName] = JObject.FromObject(new
                {
                    boneName = bone.m_BoneName,
                    limit = ToJsonSkeletonBoneLimit(bone.m_Limit),
                });
            }

            return result.HasValues ? result : null;
        }

        private static object ToJsonSkeletonBoneLimit(SkeletonBoneLimit limit)
        {
            if (limit == null)
            {
                return null;
            }

            return new
            {
                min = ToJsonVector3(limit.m_Min),
                max = ToJsonVector3(limit.m_Max),
                value = ToJsonVector3(limit.m_Value),
                length = limit.m_Length,
                modified = limit.m_Modified,
            };
        }

        private static string TryGetAvatarSkeletonPath(Avatar avatar, Skeleton skeleton, int index)
        {
            if (avatar?.m_TOS == null || skeleton?.m_ID == null || index < 0 || index >= skeleton.m_ID.Length)
            {
                return null;
            }

            return avatar.m_TOS.TryGetValue(skeleton.m_ID[index], out var path) && !string.IsNullOrWhiteSpace(path)
                ? path.Replace('\\', '/')
                : null;
        }

        private static string GetLastPathSegment(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            path = path.Replace('\\', '/').Trim('/');
            var separator = path.LastIndexOf('/');
            return separator >= 0 ? path[(separator + 1)..] : path;
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
            var endfieldAclDebugDirectory = WriteEndfieldAclDebugPayloads(clip, animOutputPath);
            var bindingAssets = bindings
                .Select((binding, index) => BuildAnimationBindingAsset(binding, index, tos))
                .ToArray();
            var muscleBindings = bindingAssets
                .Where(x => string.Equals((string)x["category"], "HumanoidMuscle", StringComparison.OrdinalIgnoreCase))
                .Take(256)
                .ToArray();

            var decodedCurves = BuildDecodedAnimationCurves(clip, animationInfo);
            var productionStatus = BuildDirectAnimationProductionStatus(animationInfo, decodedCurves);

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
                hasMuscleClip = animationInfo.hasMuscleClip,
                directTrsAnimationReady = productionStatus.DirectTrsAnimationReady,
                directWeightsAnimationReady = productionStatus.DirectWeightsAnimationReady,
                directGltfAnimationStatus = productionStatus.DirectGltfAnimationStatus,
                needsDirectTrsAnimation = productionStatus.NeedsDirectTrsAnimation,
                deprecatedUnityBakeOnly = productionStatus.DeprecatedUnityBakeOnly,
                legacyStandaloneBodyBakeField = true,
                standaloneBodyBakeReady = animationInfo.standaloneBodyBakeReady,
                standaloneBodyBakeStatus = animationInfo.standaloneBodyBakeStatus,
                standaloneBodyBakeReason = animationInfo.standaloneBodyBakeReason,
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
                decoded = decodedCurves,
                endfieldAclDebugDirectory,
                bindings = bindingAssets.Take(1024).ToArray(),
                truncatedBindings = bindings.Count > 1024,
            };
        }

        private static string WriteEndfieldAclDebugPayloads(AnimationClip clip, string animOutputPath)
        {
            var buffer = clip?.m_AclCompressedBuffer;
            if (buffer == null || !CliExportOptions.ExportFullDecodedAnimationCurves || string.IsNullOrWhiteSpace(animOutputPath))
            {
                return null;
            }

            var debugDir = Path.Combine(
                Path.GetDirectoryName(animOutputPath) ?? Directory.GetCurrentDirectory(),
                $"{Path.GetFileNameWithoutExtension(animOutputPath)}.endfield_acl"
            );
            Directory.CreateDirectory(debugDir);

            WriteBytesIfPresent(Path.Combine(debugDir, "transform_buffer.bin"), buffer.TransformBufferData);
            WriteBytesIfPresent(Path.Combine(debugDir, "root_motion_buffer.bin"), buffer.RootMotionBufferData);
            WriteBytesIfPresent(Path.Combine(debugDir, "float_buffer.bin"), buffer.FloatBufferData);
            WriteBytesIfPresent(Path.Combine(debugDir, "transform_subtrack_masks.bin"), buffer.TransformSubTrackMasks);
            WriteBytesIfPresent(Path.Combine(debugDir, "transform_subtrack_constant_masks.bin"), buffer.TransformSubTrackConstantMasks);
            WriteUInt16Array(Path.Combine(debugDir, "default_indices.u16le"), buffer.m_DefaultIndexs);
            WriteUInt16Array(Path.Combine(debugDir, "constant_indices.u16le"), buffer.m_ConstantIndexs);
            WriteFloatArray(Path.Combine(debugDir, "constant_values.f32le"), buffer.m_ConstantValues);

            var manifest = new
            {
                rule = "诊断输出：Endfield AnimationClip.m_AclCompressedBuffer 原始载荷。不要直接把 transform_buffer.bin 传给现有 acl.dll；需要先解析 Endfield/Unity 扩展头。",
                version = buffer.Version,
                outputTrackCount = buffer.OutputTrackCount,
                rootTrackCount = buffer.RootTrackCount,
                rootPosIndex = buffer.RootPosIndex,
                rootRotIndex = buffer.RootRotIndex,
                rootScaleIndex = buffer.RootScaleIndex,
                floatCurveCount = buffer.FloatCurveCount,
                files = new[]
                {
                    "transform_buffer.bin",
                    "root_motion_buffer.bin",
                    "float_buffer.bin",
                    "transform_subtrack_masks.bin",
                    "transform_subtrack_constant_masks.bin",
                    "default_indices.u16le",
                    "constant_indices.u16le",
                    "constant_values.f32le",
                },
            };
            File.WriteAllText(Path.Combine(debugDir, "manifest.json"), JsonConvert.SerializeObject(manifest, Formatting.Indented));
            return debugDir;
        }

        private static void WriteBytesIfPresent(string path, byte[] values)
        {
            File.WriteAllBytes(path, values ?? Array.Empty<byte>());
        }

        private static void WriteUInt16Array(string path, IReadOnlyList<ushort> values)
        {
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);
            foreach (var value in values ?? Array.Empty<ushort>())
            {
                writer.Write(value);
            }
        }

        private static void WriteFloatArray(string path, IReadOnlyList<float> values)
        {
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);
            foreach (var value in values ?? Array.Empty<float>())
            {
                writer.Write(value);
            }
        }

        private static object BuildDecodedAnimationCurves(AnimationClip clip, AnimationClipInfo animationInfo)
        {
            var hasHumanoidMuscle = animationInfo.humanoidBindingCount > 0 || clip.m_MuscleClip != null;
            var hasTransformTrs =
                animationInfo.transformBindingCount > 0
                || (clip.m_PositionCurves?.Count ?? 0) > 0
                || (clip.m_RotationCurves?.Count ?? 0) > 0
                || (clip.m_ScaleCurves?.Count ?? 0) > 0;
            var playbackKind = hasHumanoidMuscle
                ? "HumanoidMuscleNeedsInternalSolver"
                : hasTransformTrs
                    ? "TransformTrsDirect"
                    : "NonTrsOrMetadataOnly";
            var decoderInput = BuildAnimationDecoderInputDiagnostics(clip);

            if (!CliExportOptions.ExportFullDecodedAnimationCurves && Studio.WorkMode == WorkMode.Library)
            {
                return new
                {
                    status = "skipped",
                    coordinateSpace = "UnitySerialized",
                    playbackKind,
                    directGltfReady = false,
                    requiresInternalHumanoidSolve = hasHumanoidMuscle,
                    note = "Full decoded keyframes are skipped by default during full Library export to keep large games resumable and memory-stable. Use targeted preview/export and AnimeStudio's internal solver path for playable animation output.",
                    decoderInput,
                    curveCounts = new
                    {
                        translations = clip.m_PositionCurves?.Count ?? 0,
                        rotations = clip.m_RotationCurves?.Count ?? 0,
                        scales = clip.m_ScaleCurves?.Count ?? 0,
                        eulers = clip.m_EulerCurves?.Count ?? 0,
                        floats = clip.m_FloatCurves?.Count ?? 0,
                        pptrs = clip.m_PPtrCurves?.Count ?? 0,
                        transformBindings = animationInfo.transformBindingCount,
                        humanoidBindings = animationInfo.humanoidBindingCount,
                        blendShapeBindings = animationInfo.blendShapeBindingCount,
                    },
                };
            }

            try
            {
                if (!clip.m_Legacy && clip.m_MuscleClip != null)
                {
                    var decoded = AnimationClipConverter.Process(clip);
                    var decodedHasTransformTrs =
                        (decoded.Translations?.Count ?? 0) > 0
                        || (decoded.Rotations?.Count ?? 0) > 0
                        || (decoded.Scales?.Count ?? 0) > 0
                        || (decoded.Eulers?.Count ?? 0) > 0;
                    var keyframeCount =
                        CountVector3Keyframes(decoded.Translations)
                        + CountQuaternionKeyframes(decoded.Rotations)
                        + CountVector3Keyframes(decoded.Scales)
                        + CountVector3Keyframes(decoded.Eulers)
                        + CountFloatKeyframes(decoded.Floats)
                        + CountPPtrKeyframes(decoded.PPtrs);
                    // 不能把“解码器跑完但没有任何曲线”标成 ok。否则后续预览会误以为动画可用。
                    var hasDecodedKeyframes = keyframeCount > 0;
                    var isEmptyHumanoidClip = !hasDecodedKeyframes && IsEmptyHumanoidClipPayload(clip, animationInfo);
                    var decoderGapKind = hasDecodedKeyframes
                        ? null
                        : isEmptyHumanoidClip
                            ? "empty_humanoid_clip"
                            : ClassifyAnimationDecoderGap(clip, animationInfo);
                    var decodedDirectTrsReady = hasDecodedKeyframes && decodedHasTransformTrs;
                    var decodedHasHumanoidFloats = hasHumanoidMuscle && HasDecodedBodyHumanoidMuscleFloats(decoded.Floats);
                    var decodedHasAnimatorAuxFloats = hasHumanoidMuscle
                        && (decoded.Floats?.Count ?? 0) > 0
                        && !decodedHasHumanoidFloats;
                    var requiresInternalHumanoidSolve = decodedHasHumanoidFloats || (hasHumanoidMuscle && !decodedDirectTrsReady);
                    var resolvedPlaybackKind = decodedHasHumanoidFloats && decodedHasTransformTrs
                        ? "MixedHumanoidMuscleAndTransformTrs"
                        : decodedHasHumanoidFloats
                            ? "HumanoidMuscleNeedsInternalSolver"
                            : decodedHasAnimatorAuxFloats && decodedHasTransformTrs
                                ? "MixedRootMotionOrAnimatorAuxAndTransformTrs"
                            : decodedDirectTrsReady
                                ? hasHumanoidMuscle ? "HumanoidDecodedTransformTrsDirect" : "TransformTrsDirect"
                        : playbackKind;
                    return new
                    {
                        status = hasDecodedKeyframes ? "ok" : isEmptyHumanoidClip ? "empty_humanoid_clip" : "no_decoded_keyframes",
                        coordinateSpace = "UnitySerialized",
                        playbackKind = resolvedPlaybackKind,
                        directGltfReady = decodedDirectTrsReady,
                        requiresInternalHumanoidSolve,
                        bindingSource = decoded.BindingSource,
                        decodedBindingCount = decoded.BindingCount,
                        decoderGapKind,
                        decoderGapNextAction = GetAnimationDecoderGapNextAction(decoderGapKind),
                        decoderInput,
                        note = hasDecodedKeyframes
                                ? decodedDirectTrsReady
                                    ? decodedHasHumanoidFloats
                                        ? "Decoded both Transform TRS and Humanoid/Muscle float curves from sampled payload. Transform TRS may cover auxiliary nodes; body playback must use AnimeStudio's internal Humanoid/Muscle solver before production validation. m_ValueArrayDelta remains diagnostic layout evidence."
                                        : decodedHasAnimatorAuxFloats
                                            ? "Decoded Transform TRS plus Animator root-motion/auxiliary float curves. No body Humanoid muscle curves were found, so this clip should not be treated as a Humanoid body solver requirement."
                                    : "Decoded into direct Unity Transform TRS curves from ACL/streamed/dense/constant payload. This is ready for AnimeStudio glTF TRS export; m_ValueArrayDelta remains diagnostic unless no sampled payload exists."
                                : "Decoded from Unity ACL/streamed/dense/constant clip data, but no Transform TRS channels were produced."
                            : isEmptyHumanoidClip
                                ? "Unity Humanoid/Muscle clip has duration metadata but no serialized curve payload, binding, ACL, streamed, dense, constant, delta, or reference-pose data. Treat it as an empty/sync/marker clip, not a playable body animation."
                            : GetAnimationDecoderGapNote(decoderGapKind),
                        curveCounts = new
                        {
                            translations = decoded.Translations?.Count ?? 0,
                            rotations = decoded.Rotations?.Count ?? 0,
                            scales = decoded.Scales?.Count ?? 0,
                            eulers = decoded.Eulers?.Count ?? 0,
                            floats = decoded.Floats?.Count ?? 0,
                            pptrs = decoded.PPtrs?.Count ?? 0,
                            keyframes = keyframeCount,
                        },
                        translations = ToVector3CurveAssets(decoded.Translations),
                        rotations = ToQuaternionCurveAssets(decoded.Rotations),
                        scales = ToVector3CurveAssets(decoded.Scales),
                        eulers = ToVector3CurveAssets(decoded.Eulers),
                        floats = ToFloatCurveAssets(decoded.Floats),
                        pptrs = ToPPtrCurveAssets(decoded.PPtrs),
                    };
                }

                var legacyKeyframeCount =
                    CountVector3Keyframes(clip.m_PositionCurves)
                    + CountQuaternionKeyframes(clip.m_RotationCurves)
                    + CountVector3Keyframes(clip.m_ScaleCurves)
                    + CountVector3Keyframes(clip.m_EulerCurves)
                    + CountFloatKeyframes(clip.m_FloatCurves)
                    + CountPPtrKeyframes(clip.m_PPtrCurves);
                var hasLegacyKeyframes = legacyKeyframeCount > 0;
                var legacyDecoderGapKind = hasLegacyKeyframes ? null : ClassifyAnimationDecoderGap(clip, animationInfo);
                return new
                {
                    status = hasLegacyKeyframes ? "ok" : "no_decoded_keyframes",
                    coordinateSpace = "UnitySerialized",
                    playbackKind,
                    directGltfReady = hasLegacyKeyframes && !hasHumanoidMuscle && hasTransformTrs,
                    requiresInternalHumanoidSolve = hasHumanoidMuscle,
                    decoderGapKind = legacyDecoderGapKind,
                    decoderGapNextAction = GetAnimationDecoderGapNextAction(legacyDecoderGapKind),
                    decoderInput,
                    note = hasLegacyKeyframes
                        ? "Decoded from legacy AnimationClip curve containers."
                        : GetAnimationDecoderGapNote(legacyDecoderGapKind),
                    curveCounts = new
                    {
                        translations = clip.m_PositionCurves?.Count ?? 0,
                        rotations = clip.m_RotationCurves?.Count ?? 0,
                        scales = clip.m_ScaleCurves?.Count ?? 0,
                        eulers = clip.m_EulerCurves?.Count ?? 0,
                        floats = clip.m_FloatCurves?.Count ?? 0,
                        pptrs = clip.m_PPtrCurves?.Count ?? 0,
                        keyframes = legacyKeyframeCount,
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
                    playbackKind,
                    directGltfReady = false,
                    requiresInternalHumanoidSolve = hasHumanoidMuscle,
                    decoderGapKind = "decoder_exception",
                    decoderGapNextAction = "fix_decoder_exception",
                    error = ex.Message,
                    decoderInput,
                    note = "The raw .anim file and binding metadata were still exported; only decoded JSON curves failed.",
                };
            }
        }

        private static string ClassifyAnimationDecoderGap(AnimationClip clip, AnimationClipInfo animationInfo)
        {
            var muscleClip = clip?.m_MuscleClip;
            var innerClip = muscleClip?.m_Clip;
            var aclDense = innerClip?.m_DenseClip as ACLDenseClip;
            var hasHumanoidMuscle = (animationInfo?.humanoidBindingCount ?? 0) > 0 || muscleClip != null;
            var endfieldAclBuffer = clip?.m_AclCompressedBuffer;
            var hasBindings = (clip?.m_ClipBindingConstant?.genericBindings?.Count ?? 0) > 0
                || (innerClip?.m_Binding?.m_ValueArray?.Count ?? 0) > 0;
            var hasStreamCurves = (innerClip?.m_StreamedClip?.curveCount ?? 0) > 0;
            var hasDenseCurves = (innerClip?.m_DenseClip?.m_CurveCount ?? 0) > 0
                || (innerClip?.m_DenseClip?.m_SampleArray?.Length ?? 0) > 0;
            var hasAclClip = innerClip?.m_ACLClip?.IsSet ?? false;
            var hasConstant = (innerClip?.m_ConstantClip?.data?.Length ?? 0) > 0;
            var hasValueDelta = (muscleClip?.m_ValueArrayDelta?.Count ?? 0) > 0;
            var hasReferencePose = (muscleClip?.m_ValueArrayReferencePose?.Length ?? 0) > 0;
            var hasAclDenseBytes = (aclDense?.m_ACLArray?.Length ?? 0) > 0;
            var hasEndfieldAclBuffer = (endfieldAclBuffer?.TransformBufferData?.Length ?? 0) > 0
                || (endfieldAclBuffer?.RootMotionBufferData?.Length ?? 0) > 0
                || (endfieldAclBuffer?.FloatBufferData?.Length ?? 0) > 0;

            if (hasEndfieldAclBuffer
                && !hasStreamCurves
                && !hasDenseCurves
                && !hasAclClip
                && !hasConstant
                && !hasAclDenseBytes)
            {
                return "endfield_acl_compressed_buffer_decode_gap";
            }

            if (hasHumanoidMuscle
                && hasValueDelta
                && !hasStreamCurves
                && !hasDenseCurves
                && !hasAclClip
                && !hasConstant
                && !hasAclDenseBytes)
            {
                return "humanoid_muscle_value_delta_only";
            }

            if (aclDense != null
                && (aclDense.m_FrameCount > 0 || aclDense.m_ACLType != 0)
                && (aclDense.m_CurveCount == 0)
                && (aclDense.m_ACLArray?.Length ?? 0) == 0)
            {
                return "empty_acl_dense_with_frame_metadata";
            }

            if (aclDense != null
                && (aclDense.m_ACLArray?.Length ?? 0) > 0
                && (aclDense.m_SampleArray?.Length ?? 0) == 0)
            {
                return "acl_dense_decode_gap";
            }

            if (hasBindings
                && !hasStreamCurves
                && !hasDenseCurves
                && !hasAclClip
                && !hasConstant
                && !hasValueDelta
                && !hasReferencePose)
            {
                return "binding_only_no_curve_payload";
            }

            if (!hasBindings
                && !hasStreamCurves
                && !hasDenseCurves
                && !hasAclClip
                && !hasConstant
                && !hasValueDelta
                && !hasReferencePose)
            {
                return "no_curve_payload";
            }

            return "unknown_decoder_gap";
        }

        private static string GetAnimationDecoderGapNextAction(string decoderGapKind)
        {
            return decoderGapKind switch
            {
                "humanoid_muscle_value_delta_only" => "implement_humanoid_muscle_delta_to_trs_solver",
                "endfield_acl_compressed_buffer_decode_gap" => "decode_endfield_acl_compressed_buffer_to_trs_curves",
                "acl_dense_decode_gap" => "fix_acl_dense_decompression",
                "empty_acl_dense_with_frame_metadata" => "inspect_muscle_delta_or_animator_controller_context",
                "binding_only_no_curve_payload" => "inspect_animator_controller_context_or_missing_curve_payload",
                "no_curve_payload" => "treat_as_empty_or_marker_clip",
                "empty_humanoid_clip" => "treat_as_empty_or_marker_clip",
                "decoder_exception" => "fix_decoder_exception",
                null => null,
                _ => "inspect_animation_decoder_payload",
            };
        }

        private static string GetAnimationDecoderGapNote(string decoderGapKind)
        {
            return decoderGapKind switch
            {
                "endfield_acl_compressed_buffer_decode_gap" =>
                    "Unity clip has Endfield AnimationClip.m_AclCompressedBuffer payload. m_ValueArrayDelta matches binding scalar count and should be treated as per-curve start/stop delta, while the real sampled TRS data must be decoded from the ACL compressed buffers.",
                "humanoid_muscle_value_delta_only" =>
                    "Unity clip has Humanoid/Muscle bindings and m_ValueArrayDelta data, but no streamed/dense/ACL/constant keyframe samples. This is a direct Humanoid/Muscle delta -> target skeleton TRS solver gap; do not synthesize playable keyframes from ValueDelta start/stop pairs.",
                "acl_dense_decode_gap" =>
                    "Unity clip contains ACLDenseClip bytes but AnimeStudio did not decode sample values. Fix ACLDenseClip decompression before treating the animation as playable.",
                "empty_acl_dense_with_frame_metadata" =>
                    "Unity clip reports ACLDenseClip frame metadata but has zero curve count and no ACL bytes. Treat frame count as timing metadata only; inspect Humanoid/Muscle delta data or AnimatorController context.",
                "binding_only_no_curve_payload" =>
                    "Unity clip has binding metadata but no serialized curve payload in streamed, dense, ACL, constant, delta, or reference-pose containers. It may require AnimatorController context or may be a marker/auxiliary clip.",
                "no_curve_payload" =>
                    "AnimationClip exported, but AnimeStudio found no serialized keyframe payload. Keep this as empty/marker or data-gap until stronger evidence appears.",
                _ =>
                    "Unity clip has Humanoid/Muscle or compressed animation data, but AnimeStudio decoded no keyframes. Keep this as a decoder gap; do not treat it as playable.",
            };
        }

        private static object BuildEndfieldAclCompressedBufferDiagnostics(AnimClipAclCompressedBuffer buffer)
        {
            if (buffer == null)
            {
                return null;
            }

            return new
            {
                rule = "Endfield 扩展动画载荷。ValueDelta 是 binding 标量的起止 delta；真正逐帧 TRS 应优先从这里的 ACL buffer 解出。",
                version = buffer.Version,
                outputTrackCount = buffer.OutputTrackCount,
                rootTrackCount = buffer.RootTrackCount,
                rootPosIndex = buffer.RootPosIndex,
                rootRotIndex = buffer.RootRotIndex,
                rootScaleIndex = buffer.RootScaleIndex,
                floatCurveCount = buffer.FloatCurveCount,
                transformBuffer = BuildByteArrayPayloadSummary(buffer.TransformBufferData, 24),
                rootMotionBuffer = BuildByteArrayPayloadSummary(buffer.RootMotionBufferData, 24),
                floatBuffer = BuildByteArrayPayloadSummary(buffer.FloatBufferData, 24),
                transformSubTrackMasks = BuildByteArrayPayloadSummary(buffer.TransformSubTrackMasks, 32),
                transformSubTrackConstantMasks = BuildByteArrayPayloadSummary(buffer.TransformSubTrackConstantMasks, 32),
                defaultIndices = BuildUInt16ArraySummary(buffer.m_DefaultIndexs, 64),
                constantIndices = BuildUInt16ArraySummary(buffer.m_ConstantIndexs, 64),
                constantValues = BuildFloatArraySummary(buffer.m_ConstantValues ?? Array.Empty<float>(), 64),
                decodeProbe = new
                {
                    skipped = true,
                    reason = "不要把 Endfield m_AclCompressedBuffer 直接传给现有 acl.dll。实测 TransformBufferData 不是该 DLL 预期的裸 ACL clip，in-process native probe 会导致访问冲突；必须先解析 Endfield/Unity 扩展头和轨道表。",
                },
            };
        }

        private static object BuildByteArrayPayloadSummary(byte[] values, int sampleCount)
        {
            values ??= Array.Empty<byte>();
            return new
            {
                count = values.Length,
                magicLe = values.Length >= 4
                    ? $"0x{BitConverter.ToUInt32(values, 0):X8}"
                    : null,
                sample = values.Take(Math.Max(0, sampleCount)).ToArray(),
            };
        }

        private static object BuildUInt16ArraySummary(IReadOnlyList<ushort> values, int sampleCount)
        {
            values ??= Array.Empty<ushort>();
            return new
            {
                count = values.Count,
                min = values.Count == 0 ? (ushort?)null : values.Min(),
                max = values.Count == 0 ? (ushort?)null : values.Max(),
                sample = values.Take(Math.Max(0, sampleCount)).ToArray(),
            };
        }

        private static object BuildAnimationDecoderInputDiagnostics(AnimationClip clip)
        {
            var muscleClip = clip?.m_MuscleClip;
            var innerClip = muscleClip?.m_Clip;
            var aclDense = innerClip?.m_DenseClip as ACLDenseClip;
            var denseFrameCount = innerClip?.m_DenseClip?.m_FrameCount ?? 0;
            var denseCurveCount = innerClip?.m_DenseClip?.m_CurveCount ?? 0;
            var denseSampleCount = innerClip?.m_DenseClip?.m_SampleArray?.Length ?? 0;
            var aclDenseBytes = aclDense?.m_ACLArray?.Length ?? 0;
            var endfieldAclBuffer = clip?.m_AclCompressedBuffer;
            var streamedCurveCount = innerClip?.m_StreamedClip?.curveCount ?? 0;
            var constantValueCount = innerClip?.m_ConstantClip?.data?.Length ?? 0;
            var aclCurveCount = innerClip?.m_ACLClip?.CurveCount ?? 0;
            var muscleValueDeltaCount = muscleClip?.m_ValueArrayDelta?.Count ?? 0;
            var muscleValueReferencePoseCount = muscleClip?.m_ValueArrayReferencePose?.Length ?? 0;
            return new
            {
                clipBindingCount = clip?.m_ClipBindingConstant?.genericBindings?.Count ?? 0,
                pptrCurveMappingCount = clip?.m_ClipBindingConstant?.pptrCurveMapping?.Count ?? 0,
                muscleClipPresent = muscleClip != null,
                valueBindingCount = innerClip?.m_Binding?.m_ValueArray?.Count ?? 0,
                streamedCurveCount,
                streamedDataCount = innerClip?.m_StreamedClip?.data?.Length ?? 0,
                streamedFrameCount = TryCountStreamedFrames(innerClip?.m_StreamedClip),
                denseClipType = innerClip?.m_DenseClip?.GetType().Name,
                denseCurveCount,
                denseFrameCount,
                denseSampleCount,
                denseExpectedSampleCount = denseFrameCount > 0 && denseCurveCount > 0
                    ? denseFrameCount * (long)denseCurveCount
                    : 0,
                aclDense = aclDense == null
                    ? null
                    : new
                    {
                        aclType = aclDense.m_ACLType,
                        aclArrayBytes = aclDenseBytes,
                        positionFactor = aclDense.m_PositionFactor,
                        eulerFactor = aclDense.m_EulerFactor,
                        scaleFactor = aclDense.m_ScaleFactor,
                        floatFactor = aclDense.m_FloatFactor,
                        positionCurveValues = aclDense.m_nPositionCurves,
                        rotationCurveValues = aclDense.m_nRotationCurves,
                        eulerCurveValues = aclDense.m_nEulerCurves,
                        scaleCurveValues = aclDense.m_nScaleCurves,
                        genericCurveValues = aclDense.m_nGenericCurves,
                        payloadKind = aclDenseBytes > 0
                            ? "acl_dense_bytes"
                            : denseCurveCount > 0 || denseSampleCount > 0
                                ? "dense_samples"
                                : "frame_metadata_only",
                    },
                constantValueCount,
                aclIsSet = innerClip?.m_ACLClip?.IsSet ?? false,
                aclCurveCount,
                endfieldAclCompressedBuffer = BuildEndfieldAclCompressedBufferDiagnostics(endfieldAclBuffer),
                muscleClipSize = clip?.m_MuscleClipSize ?? 0,
                streamDataPath = clip?.m_StreamData?.path,
                streamDataOffset = clip?.m_StreamData?.offset ?? 0,
                streamDataSize = clip?.m_StreamData?.size ?? 0,
                muscleValueDeltaCount,
                muscleValueReferencePoseCount,
                payloadSummary = new
                {
                    hasStreamCurves = streamedCurveCount > 0,
                    hasDenseSamples = denseCurveCount > 0 || denseSampleCount > 0,
                    hasAclClip = innerClip?.m_ACLClip?.IsSet ?? false,
                    hasAclDenseBytes = aclDenseBytes > 0,
                    hasEndfieldAclCompressedBuffer = endfieldAclBuffer != null
                        && ((endfieldAclBuffer.TransformBufferData?.Length ?? 0) > 0
                            || (endfieldAclBuffer.RootMotionBufferData?.Length ?? 0) > 0
                            || (endfieldAclBuffer.FloatBufferData?.Length ?? 0) > 0),
                    hasConstantValues = constantValueCount > 0,
                    hasMuscleValueDelta = muscleValueDeltaCount > 0,
                    hasMuscleReferencePose = muscleValueReferencePoseCount > 0,
                    valueDeltaOnlyHumanoid = muscleClip != null
                        && muscleValueDeltaCount > 0
                        && streamedCurveCount == 0
                        && denseCurveCount == 0
                        && denseSampleCount == 0
                        && aclCurveCount == 0
                        && aclDenseBytes == 0
                        && !(((endfieldAclBuffer?.TransformBufferData?.Length ?? 0) > 0)
                            || ((endfieldAclBuffer?.RootMotionBufferData?.Length ?? 0) > 0)
                            || ((endfieldAclBuffer?.FloatBufferData?.Length ?? 0) > 0))
                        && constantValueCount == 0,
                    rule = "此摘要只说明 Unity 序列化数据载荷位置。ValueDelta-only 不是可播放关键帧；Endfield ACL buffer 存在时，必须优先解析 ACL buffer，而不是把 ValueDelta 当时间采样。",
                },
                humanoidMuscle = BuildHumanoidMuscleDecoderDiagnostics(clip, muscleClip),
            };
        }

        private static object BuildHumanoidMuscleDecoderDiagnostics(AnimationClip clip, ClipMuscleConstant muscleClip)
        {
            if (muscleClip == null)
            {
                return null;
            }

            var valueDeltas = muscleClip.m_ValueArrayDelta ?? new List<ValueDelta>();
            var referencePose = muscleClip.m_ValueArrayReferencePose ?? Array.Empty<float>();
            var indexArray = muscleClip.m_IndexArray ?? Array.Empty<int>();

            return new
            {
                rule = "诊断字段：记录 Unity Humanoid/Muscle 原始输入，帮助后续直接求解为 glTF TRS；它本身不是可播放证明。",
                time = new
                {
                    start = muscleClip.m_StartTime,
                    stop = muscleClip.m_StopTime,
                    duration = Math.Max(0, muscleClip.m_StopTime - muscleClip.m_StartTime),
                    loopTime = muscleClip.m_LoopTime,
                },
                rootMotion = new
                {
                    startX = ToJsonXForm(muscleClip.m_StartX),
                    stopX = ToJsonXForm(muscleClip.m_StopX),
                    leftFootStartX = ToJsonXForm(muscleClip.m_LeftFootStartX),
                    rightFootStartX = ToJsonXForm(muscleClip.m_RightFootStartX),
                    averageSpeed = ToJsonVector3(muscleClip.m_AverageSpeed),
                    averageAngularSpeed = muscleClip.m_AverageAngularSpeed,
                    orientationOffsetY = muscleClip.m_OrientationOffsetY,
                },
                indexArray = BuildIntArraySummary(indexArray, 64),
                valueArrayDelta = BuildValueDeltaSummary(valueDeltas, 64),
                valueArrayDeltaLayout = BuildValueDeltaLayoutDiagnostics(
                    muscleClip,
                    valueDeltas,
                    indexArray,
                    clip?.m_ClipBindingConstant?.genericBindings),
                valueArrayReferencePose = BuildFloatArraySummary(referencePose, 64),
                bindingDeltaMap = BuildHumanoidBindingDeltaMap(
                    clip?.m_ClipBindingConstant?.genericBindings,
                    indexArray,
                    valueDeltas,
                    192),
                deltaPose = BuildHumanPoseSummary(muscleClip.m_DeltaPose),
            };
        }

        private static object BuildValueDeltaLayoutDiagnostics(
            ClipMuscleConstant muscleClip,
            IReadOnlyList<ValueDelta> valueDeltas,
            IReadOnlyList<int> indexArray,
            IReadOnlyList<GenericBinding> bindings)
        {
            valueDeltas ??= Array.Empty<ValueDelta>();
            indexArray ??= Array.Empty<int>();
            bindings ??= Array.Empty<GenericBinding>();
            var clip = muscleClip?.m_Clip;
            var frameCount = clip?.m_DenseClip?.m_FrameCount ?? 0;
            var dofCount = muscleClip?.m_DeltaPose?.m_DoFArray?.Length ?? 0;
            var tdofCount = muscleClip?.m_DeltaPose?.m_TDoFArray?.Length ?? 0;
            var transformScalarCount = bindings.Where(x => x?.typeID == ClassIDType.Transform).Sum(GetBindingScalarDimension);
            var animatorScalarCount = bindings.Where(x => x?.typeID == ClassIDType.Animator).Sum(GetBindingScalarDimension);
            var totalScalarCount = bindings.Sum(GetBindingScalarDimension);
            var validIndexValues = indexArray.Where(x => x >= 0 && x < valueDeltas.Count).Distinct().OrderBy(x => x).ToArray();
            var firstIndexedValue = validIndexValues.Length == 0 ? valueDeltas.Count : validIndexValues.Min();
            var prefixCount = Math.Clamp(firstIndexedValue, 0, valueDeltas.Count);
            var prefix = valueDeltas.Take(prefixCount).ToArray();
            var candidateRows = new List<object>();

            // 这里只做布局探测，不把任何候选解释升级为可播放曲线。
            // Endfield/GI 风格 MuscleClip 常把 Root/Motion 索引放在尾部，前缀可能是按帧压缩的人体数据。
            AddValueDeltaLayoutCandidate(candidateRows, "denseFrameCount", prefix, frameCount);
            AddValueDeltaLayoutCandidate(candidateRows, "denseFrameCountMinusOne", prefix, frameCount - 1);
            AddValueDeltaLayoutCandidate(candidateRows, "denseFrameCountPlusOne", prefix, frameCount + 1);
            AddValueDeltaLayoutCandidate(candidateRows, "humanDoFCount", prefix, dofCount);
            AddValueDeltaLayoutCandidate(candidateRows, "humanTDoFCount", prefix, tdofCount);
            AddValueDeltaLayoutCandidate(candidateRows, "humanTDoFVectorScalarCount", prefix, tdofCount * 3);
            AddValueDeltaLayoutCandidate(candidateRows, "humanoidDoFPlusTDoFScalarCount", prefix, dofCount + tdofCount * 3);

            return new
            {
                rule = "诊断字段：尝试判断 m_ValueArrayDelta 前缀是不是按帧/按 DoF 打包的人体数据。候选只用于求解器研究，不能直接当作 glTF 关键帧。",
                valueDeltaCount = valueDeltas.Count,
                indexedTailStart = prefixCount,
                indexedTailCount = valueDeltas.Count - prefixCount,
                validIndexCount = validIndexValues.Length,
                denseFrameCount = frameCount,
                denseSampleRate = clip?.m_DenseClip?.m_SampleRate ?? 0,
                denseBeginTime = clip?.m_DenseClip?.m_BeginTime ?? 0,
                duration = Math.Max(0, (muscleClip?.m_StopTime ?? 0) - (muscleClip?.m_StartTime ?? 0)),
                humanDoFCount = dofCount,
                humanTDoFCount = tdofCount,
                bindingScalarLayout = new
                {
                    rule = "优先检查 ValueDelta 是否按 AnimationClip binding 标量一一对应。Endfield 样本中 prefix 对应 Transform 标量，indexed tail 对应 Animator/Root 标量；这不是逐帧矩阵。",
                    bindingCount = bindings.Count,
                    transformBindingCount = bindings.Count(x => x?.typeID == ClassIDType.Transform),
                    animatorBindingCount = bindings.Count(x => x?.typeID == ClassIDType.Animator),
                    transformScalarCount,
                    animatorScalarCount,
                    totalScalarCount,
                    valueDeltaMatchesTotalScalarCount = valueDeltas.Count == totalScalarCount,
                    prefixMatchesTransformScalarCount = prefixCount == transformScalarCount,
                    indexedTailMatchesAnimatorScalarCount = valueDeltas.Count - prefixCount == animatorScalarCount,
                },
                prefixSummary = BuildValueDeltaSummary(prefix, 32),
                indexedTailSample = validIndexValues
                    .Take(32)
                    .Select(x => new
                    {
                        index = x,
                        start = valueDeltas[x].m_Start,
                        stop = valueDeltas[x].m_Stop,
                        changed = Math.Abs(valueDeltas[x].m_Stop - valueDeltas[x].m_Start) > 1e-6f,
                    })
                    .ToArray(),
                candidates = candidateRows.ToArray(),
            };
        }

        private static void AddValueDeltaLayoutCandidate(List<object> rows, string name, IReadOnlyList<ValueDelta> values, int unit)
        {
            if (rows == null || values == null || unit <= 0)
            {
                return;
            }

            var fullRows = values.Count / unit;
            var remainder = values.Count % unit;
            var firstRows = new List<object>();
            var previewRows = Math.Min(fullRows, 4);
            for (var row = 0; row < previewRows; row++)
            {
                var slice = values.Skip(row * unit).Take(unit).ToArray();
                firstRows.Add(new
                {
                    row,
                    changedPairCount = slice.Count(x => Math.Abs(x.m_Stop - x.m_Start) > 1e-6f),
                    nonZeroPairCount = slice.Count(x => Math.Abs(x.m_Start) > 1e-6f || Math.Abs(x.m_Stop) > 1e-6f),
                    maxAbs = slice.Length == 0
                        ? 0
                        : slice.SelectMany(x => new[] { Math.Abs(x.m_Start), Math.Abs(x.m_Stop) }).Max(),
                    firstValues = ToBoundedValueDeltaArray(slice, Math.Min(unit, 8)),
                });
            }

            rows.Add(new
            {
                name,
                unit,
                fullRows,
                remainder,
                exact = remainder == 0,
                score = BuildValueDeltaLayoutScore(fullRows, remainder),
                channelStats = remainder == 0 && fullRows > 1 && unit <= 128
                    ? BuildValueDeltaChannelStats(values, unit, fullRows, 32)
                    : Array.Empty<object>(),
                firstRows = firstRows.ToArray(),
            });
        }

        private static object[] BuildValueDeltaChannelStats(IReadOnlyList<ValueDelta> values, int unit, int rows, int maxChannels)
        {
            var result = new List<object>();
            var channelCount = Math.Min(unit, Math.Max(0, maxChannels));
            for (var channel = 0; channel < channelCount; channel++)
            {
                var starts = new List<float>();
                var stops = new List<float>();
                for (var row = 0; row < rows; row++)
                {
                    var index = row * unit + channel;
                    if (index < 0 || index >= values.Count)
                    {
                        continue;
                    }
                    starts.Add(values[index].m_Start);
                    stops.Add(values[index].m_Stop);
                }

                var all = starts.Concat(stops).Where(float.IsFinite).ToArray();
                var first = starts.Count > 0 ? starts[0] : 0;
                var last = stops.Count > 0 ? stops[^1] : first;
                result.Add(new
                {
                    channel,
                    rowCount = starts.Count,
                    first,
                    last,
                    changedAcrossRows = starts.Count > 1 && starts.Zip(starts.Skip(1), (a, b) => Math.Abs(a - b) > 1e-6f).Any(x => x),
                    min = all.Length == 0 ? (float?)null : all.Min(),
                    max = all.Length == 0 ? (float?)null : all.Max(),
                    maxAbs = all.Length == 0 ? (float?)null : all.Max(x => Math.Abs(x)),
                    firstValues = starts.Take(8).ToArray(),
                });
            }
            return result.ToArray();
        }

        private static string BuildValueDeltaLayoutScore(int fullRows, int remainder)
        {
            if (fullRows <= 0)
            {
                return "not_applicable";
            }
            if (remainder == 0)
            {
                return "exact_division";
            }
            return "has_remainder";
        }

        private static int GetBindingScalarDimension(GenericBinding binding)
        {
            if (binding == null)
            {
                return 0;
            }

            if (binding.typeID != ClassIDType.Transform)
            {
                return 1;
            }

            return binding.attribute switch
            {
                1 => 3, // localPosition
                2 => 4, // localRotation quaternion
                3 => 3, // localScale
                4 => 3, // localEuler
                _ => 1,
            };
        }

        private static object BuildHumanoidBindingDeltaMap(
            IReadOnlyList<GenericBinding> bindings,
            IReadOnlyList<int> indexArray,
            IReadOnlyList<ValueDelta> valueDeltas,
            int maxItems)
        {
            bindings ??= Array.Empty<GenericBinding>();
            indexArray ??= Array.Empty<int>();
            valueDeltas ??= Array.Empty<ValueDelta>();

            var rows = new List<object>();
            var mappedCount = 0;
            var changedCount = 0;
            foreach (var binding in bindings)
            {
                if (binding == null || (BindingCustomType)binding.customType != BindingCustomType.AnimatorMuscle)
                {
                    continue;
                }

                var attribute = unchecked((int)binding.attribute);
                var valueDeltaIndex = attribute >= 0 && attribute < indexArray.Count
                    ? indexArray[attribute]
                    : -1;
                var hasValueDelta = valueDeltaIndex >= 0 && valueDeltaIndex < valueDeltas.Count;
                ValueDelta delta = hasValueDelta ? valueDeltas[valueDeltaIndex] : null;
                var changed = delta != null && Math.Abs(delta.m_Stop - delta.m_Start) > 1e-6f;
                if (hasValueDelta)
                {
                    mappedCount++;
                }
                if (changed)
                {
                    changedCount++;
                }

                if (rows.Count < Math.Max(0, maxItems))
                {
                    rows.Add(new
                    {
                        attribute,
                        attributeName = binding.GetHumanoidMuscle().ToAttributeString(),
                        valueDeltaIndex,
                        hasValueDelta,
                        changed,
                        start = delta?.m_Start,
                        stop = delta?.m_Stop,
                    });
                }
            }

            return new
            {
                rule = "按 GenericBinding.attribute 读取 m_IndexArray，再指向 m_ValueArrayDelta。这里只记录确定性映射，不把它当成完整关键帧。",
                humanoidBindingCount = bindings.Count(x => x != null && (BindingCustomType)x.customType == BindingCustomType.AnimatorMuscle),
                mappedCount,
                changedMappedCount = changedCount,
                truncated = mappedCount > rows.Count,
                sample = rows.ToArray(),
            };
        }

        private static object BuildHumanPoseSummary(HumanPose pose)
        {
            if (pose == null)
            {
                return null;
            }

            return new
            {
                rootX = ToJsonXForm(pose.m_RootX),
                doFArray = BuildFloatArraySummary(pose.m_DoFArray ?? Array.Empty<float>(), 64),
                tDoFArrayCount = pose.m_TDoFArray?.Length ?? 0,
                tDoFArraySample = (pose.m_TDoFArray ?? Array.Empty<Vector3>())
                    .Take(16)
                    .Select(ToJsonVector3)
                    .ToArray(),
                goalCount = pose.m_GoalArray?.Count ?? 0,
                hasLeftHandPose = pose.m_LeftHandPose != null,
                hasRightHandPose = pose.m_RightHandPose != null,
            };
        }

        private static object BuildIntArraySummary(IReadOnlyList<int> values, int sampleCount)
        {
            values ??= Array.Empty<int>();
            var validValues = values.Where(x => x >= 0).ToArray();
            return new
            {
                count = values.Count,
                validCount = validValues.Length,
                invalidCount = values.Count - validValues.Length,
                distinctValidCount = validValues.Distinct().Count(),
                min = values.Count == 0 ? (int?)null : values.Min(),
                max = values.Count == 0 ? (int?)null : values.Max(),
                sample = values.Take(Math.Max(0, sampleCount)).ToArray(),
            };
        }

        private static object BuildFloatArraySummary(IReadOnlyList<float> values, int sampleCount)
        {
            values ??= Array.Empty<float>();
            var finiteValues = values.Where(float.IsFinite).ToArray();
            return new
            {
                count = values.Count,
                finiteCount = finiteValues.Length,
                nonZeroCount = values.Count(x => Math.Abs(x) > 1e-6f),
                min = finiteValues.Length == 0 ? (float?)null : finiteValues.Min(),
                max = finiteValues.Length == 0 ? (float?)null : finiteValues.Max(),
                maxAbs = finiteValues.Length == 0 ? (float?)null : finiteValues.Max(x => Math.Abs(x)),
                sample = values.Take(Math.Max(0, sampleCount)).ToArray(),
            };
        }

        private static object BuildValueDeltaSummary(IReadOnlyList<ValueDelta> values, int sampleCount)
        {
            values ??= Array.Empty<ValueDelta>();
            var finiteNumbers = values
                .SelectMany(x => new[] { x.m_Start, x.m_Stop })
                .Where(float.IsFinite)
                .ToArray();
            return new
            {
                count = values.Count,
                changedPairCount = values.Count(x => Math.Abs(x.m_Stop - x.m_Start) > 1e-6f),
                nonZeroPairCount = values.Count(x => Math.Abs(x.m_Start) > 1e-6f || Math.Abs(x.m_Stop) > 1e-6f),
                min = finiteNumbers.Length == 0 ? (float?)null : finiteNumbers.Min(),
                max = finiteNumbers.Length == 0 ? (float?)null : finiteNumbers.Max(),
                maxAbs = finiteNumbers.Length == 0 ? (float?)null : finiteNumbers.Max(x => Math.Abs(x)),
                sample = ToBoundedValueDeltaArray(values, sampleCount),
            };
        }

        private static int? TryCountStreamedFrames(StreamedClip streamedClip)
        {
            if (streamedClip?.data == null || streamedClip.data.Length == 0)
            {
                return 0;
            }

            try
            {
                return streamedClip.ReadData()?.Count ?? 0;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsEmptyHumanoidClipPayload(AnimationClip clip, AnimationClipInfo animationInfo)
        {
            var muscleClip = clip?.m_MuscleClip;
            var innerClip = muscleClip?.m_Clip;
            if (muscleClip == null)
            {
                return false;
            }

            var directCurveCount = GetAnimationCurveCount(clip);
            var hasAnyPayload =
                directCurveCount > 0
                || (clip.m_ClipBindingConstant?.genericBindings?.Count ?? 0) > 0
                || (innerClip?.m_Binding?.m_ValueArray?.Count ?? 0) > 0
                || (innerClip?.m_StreamedClip?.curveCount ?? 0) > 0
                || (innerClip?.m_DenseClip?.m_CurveCount ?? 0) > 0
                || (innerClip?.m_DenseClip?.m_SampleArray?.Length ?? 0) > 0
                || (innerClip?.m_ConstantClip?.data?.Length ?? 0) > 0
                || (innerClip?.m_ACLClip?.IsSet ?? false)
                || (muscleClip.m_ValueArrayDelta?.Count ?? 0) > 0
                || (muscleClip.m_ValueArrayReferencePose?.Length ?? 0) > 0;
            if (hasAnyPayload)
            {
                return false;
            }

            return (animationInfo?.transformBindingCount ?? 0) == 0
                && (animationInfo?.humanoidBindingCount ?? 0) == 0
                && (animationInfo?.trueBlendShapeBindingCount ?? 0) == 0;
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
                    category = ClassifyFloatCurveCategory(x),
                    keyframes = x.curve?.m_Curve?.Select(ToFloatKeyframeAsset).ToArray() ?? Array.Empty<object>(),
                })
                .Cast<object>()
                .ToArray() ?? Array.Empty<object>();
        }

        private static string ClassifyFloatCurveCategory(FloatCurve curve)
        {
            var attribute = curve?.attribute ?? string.Empty;
            if (curve?.classID == ClassIDType.Animator)
            {
                return "HumanoidMuscleOrAnimator";
            }
            if (attribute.StartsWith("blendShape.", StringComparison.OrdinalIgnoreCase))
            {
                return "BlendShape";
            }
            if (attribute.StartsWith("material.", StringComparison.OrdinalIgnoreCase))
            {
                return "RendererMaterial";
            }
            if (string.Equals(attribute, "m_IsActive", StringComparison.OrdinalIgnoreCase))
            {
                return "ActiveState";
            }
            if (curve?.classID == ClassIDType.SkinnedMeshRenderer)
            {
                return "SkinnedMeshRendererProperty";
            }
            return "Float";
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
                        // 这些是 Humanoid 曲线还原的原始 Unity 数据。
                        // 默认只写有限长度，方便诊断 decoder 和 Unity HumanPose 的差异，不参与默认匹配。
                        indexArray = ToBoundedJsonArray(muscleClip.m_IndexArray, 512),
                        valueArrayDeltaCount = muscleClip.m_ValueArrayDelta?.Count ?? 0,
                        valueArrayDelta = ToBoundedValueDeltaArray(muscleClip.m_ValueArrayDelta, 512),
                        valueArrayReferencePoseCount = muscleClip.m_ValueArrayReferencePose?.Length ?? 0,
                        valueArrayReferencePose = ToBoundedJsonArray(muscleClip.m_ValueArrayReferencePose, 512),
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

        private static int[] ToBoundedJsonArray(int[] values, int maxCount)
        {
            return values == null
                ? Array.Empty<int>()
                : values.Take(Math.Max(0, maxCount)).ToArray();
        }

        private static float[] ToBoundedJsonArray(float[] values, int maxCount)
        {
            return values == null
                ? Array.Empty<float>()
                : values.Take(Math.Max(0, maxCount)).ToArray();
        }

        private static object[] ToBoundedValueDeltaArray(IReadOnlyList<ValueDelta> values, int maxCount)
        {
            return values == null
                ? Array.Empty<object>()
                : values
                    .Take(Math.Max(0, maxCount))
                    .Select(x => new
                    {
                        start = x.m_Start,
                        stop = x.m_Stop,
                    })
                    .Cast<object>()
                    .ToArray();
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
            if ((BindingCustomType)binding.customType == BindingCustomType.BlendShape)
            {
                return "BlendShape";
            }
            if ((BindingCustomType)binding.customType == BindingCustomType.RendererMaterial)
            {
                return "RendererMaterial";
            }
            if (IsRendererPropertyBinding(binding))
            {
                return "RendererProperty";
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

            if (string.Equals(category, "BlendShape", StringComparison.OrdinalIgnoreCase))
            {
                return $"blendShape_{binding.attribute}";
            }

            if (string.Equals(category, "RendererMaterial", StringComparison.OrdinalIgnoreCase))
            {
                return $"rendererMaterialAttribute_{binding.attribute}";
            }

            if (string.Equals(category, "RendererProperty", StringComparison.OrdinalIgnoreCase))
            {
                return $"rendererPropertyAttribute_{binding.attribute}";
            }

            return $"attribute_{binding.attribute}";
        }

        private static object ToJsonVector3(Vector3 value)
        {
            return new[] { value.X, value.Y, value.Z };
        }

        private static object ToJsonVector4(Vector4 value)
        {
            return new[] { value.X, value.Y, value.Z, value.W };
        }

        private static object ToJsonVector3Or4As3(object value)
        {
            return value switch
            {
                Vector3 v => new[] { v.X, v.Y, v.Z },
                Vector4 v => new[] { v.X, v.Y, v.Z },
                _ => null,
            };
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

        private static object[] ToJsonXFormArray(XForm[] values, int expectedCount)
        {
            if (values == null || values.Length == 0)
            {
                return Array.Empty<object>();
            }

            return values
                .Take(expectedCount > 0 ? Math.Min(values.Length, expectedCount) : values.Length)
                .Select(ToJsonXForm)
                .ToArray();
        }

        private static void AppendAssetCatalog(AssetItem item, string outputPath, string kind, string animationAssetPath = null)
        {
            if (string.IsNullOrWhiteSpace(CliExportOptions.OutputRoot))
            {
                return;
            }

            var animationInfo = item.Asset is AnimationClip infoClip ? AnalyzeAnimationClip(infoClip) : null;
            var animationStatus = BuildDirectAnimationProductionStatusFromSidecar(animationInfo, animationAssetPath);
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
                directTrsAnimationReady = animationStatus?.DirectTrsAnimationReady,
                directWeightsAnimationReady = animationStatus?.DirectWeightsAnimationReady,
                directGltfAnimationStatus = animationStatus?.DirectGltfAnimationStatus,
                needsDirectTrsAnimation = animationStatus?.NeedsDirectTrsAnimation,
                deprecatedUnityBakeOnly = animationStatus?.DeprecatedUnityBakeOnly,
                legacyStandaloneBodyBakeField = animationInfo != null,
                standaloneBodyBakeReady = animationInfo?.standaloneBodyBakeReady,
                standaloneBodyBakeStatus = animationInfo?.standaloneBodyBakeStatus,
                standaloneBodyBakeReason = animationInfo?.standaloneBodyBakeReason,
                transformBindingCount = animationInfo?.transformBindingCount,
                coreTransformBindingCount = animationInfo?.coreTransformBindingCount,
                humanoidBindingCount = animationInfo?.humanoidBindingCount,
                blendShapeBindingCount = animationInfo?.blendShapeBindingCount,
                trueBlendShapeBindingCount = animationInfo?.trueBlendShapeBindingCount,
                rendererMaterialBindingCount = animationInfo?.rendererMaterialBindingCount,
                rendererPropertyBindingCount = animationInfo?.rendererPropertyBindingCount,
                activeStateBindingCount = animationInfo?.activeStateBindingCount,
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

        private static bool? IsDirectTrsAnimationReady(AnimationClipInfo animationInfo)
        {
            if (animationInfo == null)
            {
                return null;
            }

            return animationInfo.transformBindingCount > 0
                && (animationInfo.coreTransformBindingCount > 0 || !animationInfo.hasMuscleClip);
        }

        private static bool? IsDirectWeightsAnimationReady(AnimationClipInfo animationInfo)
        {
            return animationInfo == null
                ? null
                : animationInfo.trueBlendShapeBindingCount > 0;
        }

        private static bool? NeedsDirectTrsAnimation(AnimationClipInfo animationInfo)
        {
            if (animationInfo == null)
            {
                return null;
            }

            return (animationInfo.hasMuscleClip || animationInfo.humanoidBindingCount > 0)
                && IsDirectTrsAnimationReady(animationInfo) != true;
        }

        private static bool? IsDeprecatedUnityBakeOnly(AnimationClipInfo animationInfo)
        {
            return animationInfo == null
                ? null
                : NeedsDirectTrsAnimation(animationInfo) == true;
        }

        private static string GetDirectGltfAnimationStatus(AnimationClipInfo animationInfo)
        {
            if (animationInfo == null)
            {
                return null;
            }

            if (IsDirectTrsAnimationReady(animationInfo) == true)
            {
                return "direct_trs";
            }

            if (IsDirectWeightsAnimationReady(animationInfo) == true)
            {
                return "direct_weights";
            }

            if (NeedsDirectTrsAnimation(animationInfo) == true)
            {
                return "needs_direct_trs_animation";
            }

            return "non_trs_animation";
        }

        private static DirectAnimationProductionStatus BuildDirectAnimationProductionStatusFromSidecar(AnimationClipInfo animationInfo, string animationAssetPath)
        {
            if (animationInfo == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(animationAssetPath) && File.Exists(animationAssetPath))
            {
                try
                {
                    var sidecar = JObject.Parse(File.ReadAllText(animationAssetPath));
                    return BuildDirectAnimationProductionStatus(animationInfo, sidecar["decoded"]);
                }
                catch (Exception e)
                {
                    Logger.Warning($"Unable to read animation sidecar production status: {animationAssetPath}. {e.GetType().Name}: {e.Message}");
                }
            }

            return BuildDirectAnimationProductionStatus(animationInfo, null);
        }

        private static DirectAnimationProductionStatus BuildDirectAnimationProductionStatus(AnimationClipInfo animationInfo, object decodedCurves)
        {
            if (animationInfo == null)
            {
                return null;
            }

            var decoded = ToJObject(decodedCurves);
            var decodedOk = string.Equals((string)decoded?["status"], "ok", StringComparison.OrdinalIgnoreCase);
            var decodedDirectReady = (bool?)decoded?["directGltfReady"] == true;
            var decodedRequiresInternalHumanoidSolve = (bool?)decoded?["requiresInternalHumanoidSolve"] == true;
            var hasTrsKeyframes = HasDecodedTrsKeyframes(decoded);
            var hasWeightKeyframes = JInt(decoded?["curveCounts"]?["floats"]) > 0;

            // binding 只能证明曲线目标可能存在；生产 glTF 必须真的解出 keyframe。
            // Endfield 的 Humanoid/Muscle clip 可以从 ACL payload 直接解出目标骨骼 TRS，
            // 此时旧的原始 binding 分类不能再把它卡成 needs_direct_trs_animation。
            var directTrsReady = decodedOk
                && decodedDirectReady
                && hasTrsKeyframes
                && !decodedRequiresInternalHumanoidSolve;
            var directWeightsReady = decodedOk
                && decodedDirectReady
                && hasWeightKeyframes
                && IsDirectWeightsAnimationReady(animationInfo) == true;
            var needsDirectTrs = !directTrsReady
                && !directWeightsReady
                && (animationInfo.transformBindingCount > 0
                    || animationInfo.hasMuscleClip
                    || animationInfo.humanoidBindingCount > 0);

            var status = directTrsReady && directWeightsReady
                ? "direct_trs_weights"
                : directTrsReady
                    ? "direct_trs"
                    : directWeightsReady
                        ? "direct_weights"
                        : needsDirectTrs
                            ? "needs_direct_trs_animation"
                            : "non_trs_animation";

            return new DirectAnimationProductionStatus(
                directTrsReady,
                directWeightsReady,
                status,
                needsDirectTrs,
                needsDirectTrs);
        }

        private static JObject ToJObject(object value)
        {
            if (value == null)
            {
                return null;
            }
            if (value is JObject obj)
            {
                return obj;
            }
            if (value is JToken token)
            {
                return token as JObject;
            }
            return JObject.FromObject(value);
        }

        private static bool HasDecodedTrsKeyframes(JObject decoded)
        {
            var counts = decoded?["curveCounts"];
            return JInt(counts?["translations"]) > 0
                || JInt(counts?["rotations"]) > 0
                || JInt(counts?["scales"]) > 0
                || JInt(counts?["eulers"]) > 0;
        }

        private static int JInt(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return 0;
            }

            return token.Type == JTokenType.Integer
                ? token.Value<int>()
                : int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : 0;
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
            var trueBlendShapeBindingCount = bindings.Count(IsBlendShapeBinding)
                + (clip.m_FloatCurves?.Count(IsBlendShapeFloatCurve) ?? 0);
            var rendererMaterialBindingCount = bindings.Count(IsRendererMaterialBinding)
                + (clip.m_FloatCurves?.Count(IsRendererMaterialFloatCurve) ?? 0);
            var activeStateBindingCount = clip.m_FloatCurves?.Count(IsActiveStateFloatCurve) ?? 0;
            var rendererPropertyBindingCount = bindings.Count(IsRendererPropertyBinding)
                + (clip.m_FloatCurves?.Count(IsRendererPropertyFloatCurve) ?? 0);
            var blendShapeBindingCount = bindings.Count(x => x.typeID == ClassIDType.SkinnedMeshRenderer)
                + (clip.m_FloatCurves?.Count(x => x.classID == ClassIDType.SkinnedMeshRenderer) ?? 0);
            var humanoidBindingCount = bindings.Count(x => x.typeID == ClassIDType.Animator);
            var rootMotionHumanoidBindingCount = bindings.Count(IsRootMotionHumanoidBinding);
            var unknownBindingCount = bindings.Count(x => x.typeID != ClassIDType.Transform && x.typeID != ClassIDType.SkinnedMeshRenderer && x.typeID != ClassIDType.Animator);
            var hasMuscleClip = clip.m_MuscleClip != null;
            var notes = new List<string>();

            string animationType;
            if (humanoidBindingCount > 0)
            {
                animationType = transformBindingCount > 0 ? "MixedHumanoidTransform" : "HumanoidMuscleAnimation";
                if (coreTransformCount >= 3)
                {
                    notes.Add("AnimationClip 同时包含 Humanoid 和直接 Transform 绑定；生产 glTF 应优先使用直接 TRS，纯 Humanoid/Muscle 求解只能作诊断。");
                }
                else if (transformBindingCount > 0)
                {
                    notes.Add("AnimationClip 同时包含 Humanoid 和直接 Transform 绑定，但直接 Transform 主要是辅助骨、修正骨或附件；主体动作仍需要 Humanoid/AnimatorController 求解。");
                }
                else
                {
                    notes.Add("AnimationClip 只有 Humanoid/Muscle 绑定，缺少可直接写入 glTF 的 Transform TRS；不能作为生产动画导出。");
                }
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
            else if (trueBlendShapeBindingCount > 0 && transformBindingCount == 0)
            {
                animationType = "BlendShapeAnimation";
            }
            else if (rendererMaterialBindingCount > 0 && transformBindingCount == 0)
            {
                animationType = "MaterialAnimation";
                notes.Add("Renderer material curves are present; they are not glTF morph target weights.");
            }
            else if (activeStateBindingCount > 0 && transformBindingCount == 0)
            {
                animationType = "ActiveStateAnimation";
                notes.Add("GameObject active-state curves are present; glTF playback needs a separate visibility mapping.");
            }
            else if (rendererPropertyBindingCount > 0 && transformBindingCount == 0)
            {
                animationType = "RendererPropertyAnimation";
                notes.Add("Renderer or SkinnedMeshRenderer property curves are present; inspect before treating them as playable glTF animation.");
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

            var standalone = ClassifyStandaloneBodyBakeReadiness(
                hasMuscleClip,
                humanoidBindingCount,
                rootMotionHumanoidBindingCount,
                coreTransformCount);
            if (!standalone.ready && (hasMuscleClip || humanoidBindingCount > 0))
            {
                notes.Add(standalone.reason);
            }

            return new AnimationClipInfo(
                animationType,
                hasMuscleClip,
                standalone.ready,
                standalone.status,
                standalone.reason,
                transformBindingCount,
                coreTransformCount,
                humanoidBindingCount,
                blendShapeBindingCount,
                trueBlendShapeBindingCount,
                rendererMaterialBindingCount,
                rendererPropertyBindingCount,
                activeStateBindingCount,
                auxiliaryCount,
                unknownBindingCount,
                transformPaths.Take(64).ToArray(),
                notes.ToArray()
            );
        }

        private static (bool ready, string status, string reason) ClassifyStandaloneBodyBakeReadiness(
            bool hasMuscleClip,
            int humanoidBindingCount,
            int rootMotionHumanoidBindingCount,
            int coreTransformBindingCount)
        {
            if (coreTransformBindingCount >= 3)
            {
                // 有些 Unity Humanoid clip 同时带 MuscleClip 外壳和完整 Transform 曲线。
                // 只要核心身体骨骼已有直接 TRS，就不应该被旧 Humanoid/root-motion 判定误拦住。
                return (
                    true,
                    "direct_transform_body_trs",
                    "AnimationClip has direct Transform TRS coverage on core body bones; Humanoid/Muscle data is diagnostic and does not block standalone glTF TRS export.");
            }

            if (!hasMuscleClip && humanoidBindingCount == 0)
            {
                return (true, "not_humanoid_body_bake", null);
            }

            if (humanoidBindingCount > rootMotionHumanoidBindingCount)
            {
                return (
                    false,
                    "needs_direct_trs_animation",
                    "AnimationClip exposes Humanoid/Muscle data. Production export must use decoded direct glTF TRS/weights; the old standalone Unity bake readiness flag is deprecated.");
            }

            // 这类 clip 仍然是 Unity 显式引用，但它自己只携带 root motion
            // 或附件曲线。强行当完整身体动作 bake 会生成入地、静态或语义错误的 glTF。
            return (
                false,
                "requires_animator_controller_context",
                "AnimationClip only exposes root-motion Humanoid bindings or auxiliary Transform bindings; sample it through AnimatorController/layer/blend context before treating it as a standalone body animation.");
        }

        private static bool IsRootMotionHumanoidBinding(GenericBinding binding)
        {
            if (binding == null || binding.typeID != ClassIDType.Animator)
            {
                return false;
            }
            try
            {
                var attribute = binding.GetHumanoidMuscle().ToAttributeString();
                return attribute.StartsWith("MotionT.", StringComparison.OrdinalIgnoreCase)
                    || attribute.StartsWith("MotionQ.", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return binding.attribute >= 0 && binding.attribute <= 6;
            }
        }

        private static bool HasDecodedBodyHumanoidMuscleFloats(IReadOnlyCollection<FloatCurve> curves)
        {
            return curves?.Any(IsDecodedBodyHumanoidMuscleFloat) == true;
        }

        private static bool IsDecodedBodyHumanoidMuscleFloat(FloatCurve curve)
        {
            if (curve == null || curve.classID != ClassIDType.Animator)
            {
                return false;
            }

            var attribute = curve.attribute ?? string.Empty;
            if (attribute.StartsWith("RootT.", StringComparison.OrdinalIgnoreCase)
                || attribute.StartsWith("RootQ.", StringComparison.OrdinalIgnoreCase)
                || attribute.StartsWith("MotionT.", StringComparison.OrdinalIgnoreCase)
                || attribute.StartsWith("MotionQ.", StringComparison.OrdinalIgnoreCase)
                || attribute.StartsWith("typetree_", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Unity HumanTrait muscle 名称通常是可读的 body/finger 属性，
            // 例如 "Spine Front-Back" 或 "Left Index 1 Stretched"。
            // 不能把 Root/Motion 或未知 typetree hash 当成身体 muscle。
            return !string.IsNullOrWhiteSpace(attribute);
        }

        private static bool IsBlendShapeBinding(GenericBinding binding)
        {
            return binding != null && (BindingCustomType)binding.customType == BindingCustomType.BlendShape;
        }

        private static bool IsRendererMaterialBinding(GenericBinding binding)
        {
            return binding != null && (BindingCustomType)binding.customType == BindingCustomType.RendererMaterial;
        }

        private static bool IsRendererPropertyBinding(GenericBinding binding)
        {
            if (binding == null)
            {
                return false;
            }
            var customType = (BindingCustomType)binding.customType;
            return customType == BindingCustomType.Renderer
                || customType == BindingCustomType.RendererShadows
                || customType == BindingCustomType.SpriteRenderer
                || customType == BindingCustomType.LineRenderer
                || customType == BindingCustomType.TrailRenderer
                || (binding.typeID == ClassIDType.SkinnedMeshRenderer && customType != BindingCustomType.BlendShape && customType != BindingCustomType.RendererMaterial);
        }

        private static bool IsBlendShapeFloatCurve(FloatCurve curve)
        {
            return (curve?.attribute ?? string.Empty).StartsWith("blendShape.", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRendererMaterialFloatCurve(FloatCurve curve)
        {
            return (curve?.attribute ?? string.Empty).StartsWith("material.", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsActiveStateFloatCurve(FloatCurve curve)
        {
            return string.Equals(curve?.attribute, "m_IsActive", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRendererPropertyFloatCurve(FloatCurve curve)
        {
            return curve?.classID == ClassIDType.SkinnedMeshRenderer
                && !IsBlendShapeFloatCurve(curve)
                && !IsRendererMaterialFloatCurve(curve);
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
            var text = NormalizeAnimationPathLeaf(path);
            if (IsAuxiliaryAnimationPath(path) || IsTwistOrHelperAnimationPath(path))
            {
                return false;
            }
            if (IsAttachmentAnimationPath(path))
            {
                return false;
            }

            // 这里宁可保守：只有叶子名明确是主体骨，才算直接身体 TRS 覆盖。
            // `*_UpperArm_tz_plus`、手指、面部点、衣摆等不能把 Humanoid clip 升级成可复用身体动画。
            if (IsCentralBodyBoneLeaf(text))
            {
                return true;
            }

            return IsSideBodyBoneLeaf(text);
        }

        private static bool IsCentralBodyBoneLeaf(string text)
        {
            return text == "pelvis"
                || text == "hips"
                || text == "hip"
                || text.EndsWith("pelvis", StringComparison.OrdinalIgnoreCase)
                || text.EndsWith("hips", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(text, @"(?:^|bip\d*)spine\d*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                || text.EndsWith("chest", StringComparison.OrdinalIgnoreCase)
                || text.EndsWith("upperchest", StringComparison.OrdinalIgnoreCase)
                || text.EndsWith("neck", StringComparison.OrdinalIgnoreCase)
                || text.EndsWith("head", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSideBodyBoneLeaf(string text)
        {
            var sidePrefixes = new[] { "l", "r", "left", "right" };
            var boneNames = new[]
            {
                "clavicle", "shoulder", "upperarm", "arm", "forearm", "lowerarm", "hand",
                "thigh", "upperleg", "calf", "lowerleg", "foot", "toe", "toes"
            };

            foreach (var side in sidePrefixes)
            {
                foreach (var bone in boneNames)
                {
                    if (text.EndsWith(side + bone, StringComparison.OrdinalIgnoreCase)
                        || text.EndsWith(bone + side, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsAuxiliaryAnimationPath(string path)
        {
            var text = NormalizeAnimationPathLeaf(path);
            return text.Contains("point") || text.Contains("socket") || text.Contains("attach");
        }

        private static bool IsTwistOrHelperAnimationPath(string path)
        {
            var text = NormalizeAnimationPathLeaf(path);
            return text.Contains("twist")
                || text.Contains("helper")
                || IsAxisCorrectiveHelperPath(text);
        }

        private static bool IsAxisCorrectiveHelperPath(string normalizedLeaf)
        {
            // Endfield 常见 `Bip001_L_UpperArm_tz_plus` / `*_ty_minus` 是轴向修正辅助骨。
            // 它们名字里带 upperArm/thigh/calf，但不是主体骨骼 TRS，不能算作身体动画覆盖。
            return Regex.IsMatch(
                normalizedLeaf ?? string.Empty,
                @"(?:tx|ty|tz)(?:plus|minus)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static bool IsAttachmentAnimationPath(string path)
        {
            var text = NormalizeAnimationPathLeaf(path);
            return text.Contains("hair")
                || text.Contains("face")
                || text.Contains("ear")
                || text.Contains("eye")
                || text.Contains("tooth")
                || text.Contains("breast")
                || text.Contains("bag")
                || text.Contains("cloth")
                || text.Contains("skirt")
                || text.Contains("cape")
                || text.Contains("tail")
                || text.Contains("ribbon")
                || text.Contains("weapon")
                || text.Contains("bow")
                || text.Contains("arrow")
                || text.Contains("sleeve")
                || text.Contains("dress")
                || text.Contains("ornament")
                || text.Contains("accessory");
        }

        internal static string NormalizeAnimationPath(string path)
        {
            return Regex.Replace((path ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9]+", string.Empty);
        }

        private static string NormalizeAnimationPathLeaf(string path)
        {
            var leaf = (path ?? string.Empty)
                .Replace('\\', '/')
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault() ?? string.Empty;
            return NormalizeAnimationPath(leaf);
        }

        private sealed record AnimationClipInfo(
            string animationType,
            bool hasMuscleClip,
            bool standaloneBodyBakeReady,
            string standaloneBodyBakeStatus,
            string standaloneBodyBakeReason,
            int transformBindingCount,
            int coreTransformBindingCount,
            int humanoidBindingCount,
            int blendShapeBindingCount,
            int trueBlendShapeBindingCount,
            int rendererMaterialBindingCount,
            int rendererPropertyBindingCount,
            int activeStateBindingCount,
            int auxiliaryBindingCount,
            int unknownBindingCount,
            string[] transformBindingPaths,
            string[] classificationNotes
        );

        private sealed record DirectAnimationProductionStatus(
            bool DirectTrsAnimationReady,
            bool DirectWeightsAnimationReady,
            string DirectGltfAnimationStatus,
            bool NeedsDirectTrsAnimation,
            bool DeprecatedUnityBakeOnly
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
            lock (CatalogWriteLock)
            {
                File.AppendAllText(catalogPath, JsonConvert.SerializeObject(entry) + Environment.NewLine);
            }
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
            var signalText = string.Join(
                "/",
                new[] { container, Path.GetFileNameWithoutExtension(source), name }.Where(x => !string.IsNullOrWhiteSpace(x))
            ).Replace('\\', '/').ToLowerInvariant();

            static bool HasToken(string value, string alternatives)
            {
                return Regex.IsMatch(
                    value,
                    $@"(^|[/_.\-\s])(?:{alternatives})(?:$|[/_.\-\s0-9])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                );
            }

            if (CliExportOptions.IncludeVfx
                && Regex.IsMatch(signalText, @"(^|[/_.\-\s])(?:vfx|fx|effect|effects|particle|particles|trail|trails|slash|impact|hitfx|explosion|smoke|fire|flame|spark|sparks|beam|laser|aura|buff|debuff|projectile|spell|skill)(?:$|[/_.\-\s0-9])"))
            {
                return "VFX";
            }
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
            if (HasToken(text, "npc|npcs"))
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
            // enemy/monster/mob 是跨游戏通用素材语义，归 Unit；不按具体游戏私有前缀猜。
            if (HasToken(text, "enemy|enemies|monster|monsters|mob|mobs"))
            {
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

        private static Dictionary<string, object> GetAnimationProfileData(
            AssetItem item,
            string exportPath,
            int? yamlLength = null
        )
        {
            var clip = item.Asset as AnimationClip;
            var data = new Dictionary<string, object>
            {
                ["type"] = item.TypeString,
                ["name"] = item.Text,
                ["container"] = item.Container,
                ["source"] = item.SourceFile?.originalPath ?? item.SourceFile?.fileName,
                ["pathId"] = item.m_PathID,
                ["output"] = exportPath,
                ["sampleRate"] = clip?.m_SampleRate ?? 0,
                ["legacy"] = clip?.m_Legacy ?? false,
                ["compressed"] = clip?.m_Compressed ?? false,
                ["genericBindingCount"] = clip?.m_ClipBindingConstant?.genericBindings?.Count ?? 0,
                ["positionCurveCount"] = clip?.m_PositionCurves?.Count ?? 0,
                ["rotationCurveCount"] = clip?.m_RotationCurves?.Count ?? 0,
                ["scaleCurveCount"] = clip?.m_ScaleCurves?.Count ?? 0,
                ["floatCurveCount"] = clip?.m_FloatCurves?.Count ?? 0,
                ["pptrCurveCount"] = clip?.m_PPtrCurves?.Count ?? 0,
                ["hasMuscleClip"] = clip?.m_MuscleClip != null,
            };
            if (yamlLength.HasValue)
            {
                data["yamlLength"] = yamlLength.Value;
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
                ["meshWithBoneListCount"] = imported.MeshList?.Count(x => x.BoneList?.Count > 0) ?? 0,
                ["meshWithVertexWeightsCount"] = imported.MeshList?.Count(HasVertexWeights) ?? 0,
                ["weightedMeshWithoutBoneListCount"] = imported.MeshList?.Count(x => HasVertexWeights(x) && (x.BoneList == null || x.BoneList.Count == 0)) ?? 0,
                ["importedBoneTotal"] = imported.MeshList?.Sum(x => x.BoneList?.Count ?? 0) ?? 0,
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

        private static bool HasVertexWeights(ImportedMesh mesh)
        {
            return mesh?.VertexList?.Any(x => x.Weights?.Any(weight => weight > 0.0001f) == true) == true;
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
            // Windows 允许空格、#、?、[] 这类字符出现在文件名里，但 glTF 查看器常会把本地路径转成 URI。
            // # 会被当成 fragment，空格也可能在命令行/URI 转换中踩坑；素材库文件名保守替换，原名保留在元数据里。
            str = Path.GetInvalidFileNameChars()
                .Aggregate(str, (current, c) => current.Replace(c, '_'));
            str = Regex.Replace(str, @"\s+", "_");
            foreach (var c in GetUriSensitiveFileNameChars())
            {
                str = str.Replace(c, '_');
            }
            return str;
        }

        private static char[] GetUriSensitiveFileNameChars()
        {
            return new[] { '#', '?', '%', '[', ']' };
        }
    }
}
