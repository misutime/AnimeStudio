using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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

            var type = Properties.Settings.Default.convertType;
            var extension = "." + type.ToString().ToLower();
            var exportedLayers = new List<object>();
            var layerIndex = 0;

            foreach (var layer in textureArray.TextureList)
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
                mipCount = textureArray.m_MipCount,
                dataSize = textureArray.m_DataSize,
                streamPath = textureArray.m_StreamData?.path,
                streamOffset = textureArray.m_StreamData?.offset,
                streamSize = textureArray.m_StreamData?.size,
                layers = exportedLayers,
                note = "Texture2DArray is exported as independent layer images for texture/terrain/material libraries; it is not embedded into glTF PBR materials by default.",
            };
            File.WriteAllText(metadataPath, JsonConvert.SerializeObject(metadata, Formatting.Indented));

            AppendAssetCatalog(item, exportFolder, "Texture2DArray");
            return exportedLayers.Count > 0;
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
            if (Regex.IsMatch(text, @"(^|/)character/npc|(^|/)npc(/|$)"))
            {
                return "NPC";
            }
            if (Regex.IsMatch(text, @"(^|/)stage|court|scene|map"))
            {
                return "Stage";
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
            if (str.Length >= 260)
                return Path.GetRandomFileName();
            return Path.GetInvalidFileNameChars()
                .Aggregate(str, (current, c) => current.Replace(c, '_'));
        }
    }
}
