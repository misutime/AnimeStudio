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

namespace AnimeStudio.CLI
{
    internal static class CliExportOptions
    {
        public static float? FbxScaleFactor { get; set; }
        public static int? FbxBoneSize { get; set; }
        public static FbxAnimationMode FbxAnimationMode { get; set; } = FbxAnimationMode.Skip;
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

        public static bool ExportAudioClip(AssetItem item, string exportPath)
        {
            var m_AudioClip = (AudioClip)item.Asset;
            if (m_AudioClip.m_AudioData.Size == 0)
                return false;
            var converter = new AudioClipConverter(m_AudioClip);
            if (Properties.Settings.Default.convertAudio && converter.IsSupport)
            {
                if (!TryExportFile(exportPath, item, ".wav", out var exportFullPath))
                    return false;
                var buffer = converter.ConvertToWav();
                if (buffer == null)
                    return false;
                File.WriteAllBytes(exportFullPath, buffer);
            }
            else
            {
                if (
                    !TryExportFile(
                        exportPath,
                        item,
                        converter.GetExtensionName(),
                        out var exportFullPath
                    )
                )
                    return false;
                m_AudioClip.m_AudioData.WriteData(exportFullPath);
            }
            return true;
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
            AppendAssetCatalog(item, exportFullPath, "Animation");
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
                Logger.Info($"GameObject {m_GameObject.m_Name} has no Transform, skipping...");
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
                Logger.Info($"GameObject {gameObject.m_Name} has no mesh, skipping...");
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
                exportAllNodes = Properties.Settings.Default.exportAllNodes,
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
                skeletonHash = bonePaths.Length == 0 ? null : HashText(string.Join("\n", bonePaths)),
                avatar = GetModelAvatarInfo(source),
            };
            AppendCatalogEntry(entry);
        }

        private static object GetModelAvatarInfo(object source)
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

            return new
            {
                name = avatar.m_Name,
                source = avatar.assetsFile?.originalPath ?? avatar.assetsFile?.fileName,
                pathId = avatar.m_PathID,
                hasHumanDescription = avatar.m_HumanDescription != null,
                humanBoneCount = avatar.m_HumanDescription?.m_Human?.Count ?? 0,
                skeletonBoneCount = avatar.m_HumanDescription?.m_Skeleton?.Count ?? 0,
                avatarSkeletonNodeCount = avatar.m_Avatar?.m_AvatarSkeleton?.m_Node?.Count ?? 0,
            };
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

        private static void AppendAssetCatalog(AssetItem item, string outputPath, string kind)
        {
            if (string.IsNullOrWhiteSpace(CliExportOptions.OutputRoot))
            {
                return;
            }

            var animationInfo = item.Asset is AnimationClip infoClip ? AnalyzeAnimationClip(infoClip) : null;
            var entry = new
            {
                kind,
                resourceKind = InferResourceKind(item.Text, item.Container, item.SourceFile?.originalPath ?? item.SourceFile?.fileName),
                exportedAt = DateTime.UtcNow.ToString("O"),
                name = item.Text,
                sourceType = item.TypeString,
                container = item.Container,
                source = item.SourceFile?.originalPath ?? item.SourceFile?.fileName,
                pathId = item.m_PathID,
                output = outputPath,
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

            if (Regex.IsMatch(text, @"(^|/)character/(pc|player)|(^|/)characters?(/|$)|(^|/)(pc|player)(/|$)"))
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
