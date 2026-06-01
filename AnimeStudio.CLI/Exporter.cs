using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        public static string OutputRoot { get; set; }

        public static bool ExportAnimations => FbxAnimationMode != FbxAnimationMode.Skip;
        public static bool CollectAnimations => FbxAnimationMode == FbxAnimationMode.Auto;
    }

    internal static class Exporter
    {
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
            if (!TryExportFile(exportPath, item, ".shader", out var exportFullPath))
                return false;
            var m_Shader = (Shader)item.Asset;
            var str = m_Shader.Convert();
            File.WriteAllText(exportFullPath, str);
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
                game = Studio.Game,
                collectAnimations = CliExportOptions.CollectAnimations,
                exportAnimations = CliExportOptions.ExportAnimations,
                exportMaterials = Properties.Settings.Default.exportMaterials,
                materials = new HashSet<Material>(),
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
                game = Studio.Game,
                collectAnimations = CliExportOptions.CollectAnimations,
                exportAnimations = CliExportOptions.ExportAnimations,
                exportMaterials = Properties.Settings.Default.exportMaterials,
                materials = new HashSet<Material>(),
                useAnimatorHierarchy =
                    Properties.Settings.Default.exportSkins
                    || CliExportOptions.ExportAnimations
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
            using (ProfileLogger.Measure("model_write", GetImportedProfileData(convert, exportPath, source)))
            {
                switch (CliExportOptions.ModelFormat)
                {
                    case ModelExportFormat.Fbx:
                        ExportFbxModel(convert, exportPath);
                        break;
                    case ModelExportFormat.Glb:
                        ExportGltfModel(convert, Path.ChangeExtension(exportPath, ".glb"), true);
                        break;
                    default:
                        ExportGltfModel(convert, Path.ChangeExtension(exportPath, ".gltf"), false);
                        break;
                }
            }
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
                exportAnimations = CliExportOptions.ExportAnimations,
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
                exportSkins = Properties.Settings.Default.exportSkins || CliExportOptions.ExportAnimations,
                exportAnimations = CliExportOptions.ExportAnimations,
                textureDirectory = GetSharedTextureDirectory(exportPath),
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
