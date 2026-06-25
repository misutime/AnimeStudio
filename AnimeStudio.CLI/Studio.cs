using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using static AnimeStudio.CLI.Exporter;

namespace AnimeStudio.CLI
{
    [Flags]
    public enum MapOpType
    {
        None,
        Load,
        CABMap,
        AssetMap = 4,
        Both = 8,
        All = Both | Load,
    }

    internal static class Studio
    {
        public static Game Game;
        public static bool SkipContainer = false;
        public static AssetsManager assetsManager = new AssetsManager()
        {
            ResolveDependencies = false,
        };
        public static AssemblyLoader assemblyLoader = new AssemblyLoader();
        public static List<AssetItem> exportableAssets = new List<AssetItem>();
        private static readonly HashSet<string> VfxTexturePreviewCatalogKeys = new(StringComparer.OrdinalIgnoreCase);
        public static WorkMode WorkMode { get; set; } = WorkMode.Export;
        public static FbxAnimationMode FbxAnimationMode { get; set; } = FbxAnimationMode.Skip;
        public static int MaxExportTasks { get; set; } = Math.Max(1, Math.Min(8, Environment.ProcessorCount / 2));
        public static int BatchFiles { get; set; } = 32;
        public static int ModelGcInterval { get; set; } = 0;
        public static bool IncludeStaticMeshes { get; set; }
        public static bool IncludeVfx { get; set; }
        public static bool IncludeShaders { get; set; }
        public static bool ModelRootsOnly { get; set; }
        private static int _exportsSinceCollect;
        private static ExportRunStats _exportRunStats = new ExportRunStats();
        private static readonly object ExportManifestWriteLock = new object();
        private static string _rendererBoundStaticMeshIndexPath;
        private static HashSet<string> _rendererBoundStaticMeshKeys;
        private static readonly HashSet<string> WeaponSemanticTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "axe",
            "bomb",
            "bottle",
            "bow",
            "crossbow",
            "dagger",
            "gun",
            "mace",
            "pistol",
            "railgun",
            "shield",
            "sickle",
            "spear",
            "spell",
            "sword",
            "unarmed",
            "wand",
            "whip",
        };
        private static readonly HashSet<string> GenericSemanticStopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "animation",
            "animationclip",
            "asset",
            "assets",
            "character",
            "contentarchives",
            "data",
            "export",
            "gltf",
            "models",
            "prefab",
            "standard",
            "streamingassets",
        };
        private const long FullStructuralAnimationPairLimit = 100_000;
        private static readonly Regex[] ModelRootExcludePatterns =
        {
            new Regex(@"(?:^|_)(Col|Collider|Collision)$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"(?:^|[_\-\s])(JNT|Joint|Bone|Socket|Attach|Locator|Point|Empty)(?:$|[_\-\s0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        };
        private static readonly Regex DeprecatedLibraryPathPattern = new Regex(
            @"(?:^|[\\/])(obsolete|deprecated)(?:[\\/]|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );
        private static readonly Regex LowValueBrowsableModelPattern = new Regex(
            @"(?:^|[\\/])(?:[^\\/]*PLACEHOLDER[^\\/]*|[^\\/]*(?:^|[_\-\s])Dummy(?:$|[_\-\s0-9])[^\\/]*|[^\\/]*AimPreview[^\\/]*|[^\\/]*ArenaBlock_(?:Invalid|ValidPlace|ValidRemove)[^\\/]*|[^\\/]*(?:^|[_\-\s])(Camera|Light|Audio)(?:$|[_\-\s0-9])[^\\/]*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );
        private static readonly Regex Non3DLibraryRootPattern = new Regex(
            @"(?:^|[\\/])(?:[^\\/]*(?:Canvas|Dialog|Popup|Toast|Panel|Button|PayPlat|WebShow|Loading(?:Fade)?|Fade|CNPayPlat|UIRoot)[^\\/]*|(?:Image|RawImage|Text|Label|Mask|Scrollbar|ScrollView|Dropdown|InputField|Toggle|Slider|Camera)(?:$|[_\-\s0-9])[^\\/]*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );
        private static readonly Regex StaticMeshLibraryPathPattern = new Regex(
            @"(?:^|[\\/])(environment|terrain|landscape|levelbuild|levelbuildelements|building|buildings|structure|structures|prop|props|object|objects|item|items|weapon|weapons|nature|rock|rocks|tree|trees|vegetation|foliage|world|locations|rooms|pieces|map|stage|scene|scenery|decor|decoration)(?:[\\/]|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );
        private static readonly Regex StaticMeshRejectPathPattern = new Regex(
            @"(?:^|[\\/])(?:debug|collider|collision|physics|navmesh|occlusion|occluder|bounds?|proxy|placeholder|dummy|socket|locator|attach|joint|jnt|bone)(?:[\\/._\-\s]|$)|(?:^|[_\-\s])(col|collider|collision|navmesh|occluder|dummy|socket|locator|jnt|joint|bone)(?:$|[_\-\s0-9])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );
        private static readonly Regex UnityDefaultPrimitivePathPattern = new Regex(
            @"(?:^|[\\/])unity default resources(?:[\\/]|$).*(?:^|[\\/])(Cube|Sphere|Capsule|Cylinder|Plane|Quad)(?:\.[^\\/]+)?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );
        private static readonly Regex VfxLibrarySignalPattern = new Regex(
            @"(^|[/_.\-\s])(?:vfx|fx|effect|effects|particle|particles|trail|trails|slash|impact|hitfx|explosion|smoke|fire|flame|spark|sparks|beam|laser|aura|buff|debuff|projectile|spell|skill)(?:$|[/_.\-\s0-9])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );

        public static Dictionary<ulong, string> Paths { get; set; } =
            new Dictionary<ulong, string>();
        public static List<string> PathStrings { get; set; } = new List<string>();
        public static List<string> VOStrings { get; set; } = new List<string>();
        public static List<string> EventStrings { get; set; } = new List<string>();

        public static int ExtractFolder(string path, string savePath)
        {
            int extractedCount = 0;
            var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories);
            for (int i = 0; i < files.Length; i++)
            {
                var file = files[i];
                var fileOriPath = Path.GetDirectoryName(file);
                var fileSavePath = fileOriPath.Replace(path, savePath);
                extractedCount += ExtractFile(file, fileSavePath);
            }
            return extractedCount;
        }

        public static int ExtractFile(string[] fileNames, string savePath)
        {
            int extractedCount = 0;
            for (var i = 0; i < fileNames.Length; i++)
            {
                var fileName = fileNames[i];
                extractedCount += ExtractFile(fileName, savePath);
            }
            return extractedCount;
        }

        public static int ExtractFile(string fileName, string savePath)
        {
            int extractedCount = 0;
            var reader = new FileReader(fileName);
            reader = reader.PreProcessing(Game);
            if (reader.FileType == FileType.BundleFile)
                extractedCount += ExtractBundleFile(reader, savePath);
            else if (reader.FileType == FileType.WebFile)
                extractedCount += ExtractWebDataFile(reader, savePath);
            else if (reader.FileType == FileType.BlkFile)
                extractedCount += ExtractBlkFile(reader, savePath);
            else if (reader.FileType == FileType.BlockFile)
                extractedCount += ExtractBlockFile(reader, savePath);
            else
                reader.Dispose();
            return extractedCount;
        }

        private static int ExtractBundleFile(FileReader reader, string savePath)
        {
            Logger.Info($"Decompressing {reader.FileName} ...");
            try
            {
                var bundleFile = new BundleFile(reader, Game);
                reader.Dispose();
                if (bundleFile.fileList.Count > 0)
                {
                    var extractPath = Path.Combine(savePath, reader.FileName + "_unpacked");
                    return ExtractStreamFile(extractPath, bundleFile.fileList);
                }
            }
            catch (InvalidCastException)
            {
                Logger.Error(
                    $"Game type mismatch, Expected {nameof(Mr0k)} but got {Game.Name} ({Game.GetType().Name}) !!"
                );
            }
            return 0;
        }

        private static int ExtractWebDataFile(FileReader reader, string savePath)
        {
            Logger.Info($"Decompressing {reader.FileName} ...");
            var webFile = new WebFile(reader);
            reader.Dispose();
            if (webFile.fileList.Count > 0)
            {
                var extractPath = Path.Combine(savePath, reader.FileName + "_unpacked");
                return ExtractStreamFile(extractPath, webFile.fileList);
            }
            return 0;
        }

        private static int ExtractBlkFile(FileReader reader, string savePath)
        {
            int total = 0;
            Logger.Info($"Decompressing {reader.FileName} ...");
            try
            {
                using var stream = BlkUtils.Decrypt(reader, (Blk)Game);
                do
                {
                    stream.Offset = stream.AbsolutePosition;
                    var dummyPath = Path.Combine(
                        reader.FullPath,
                        stream.AbsolutePosition.ToString("X8")
                    );
                    var subReader = new FileReader(dummyPath, stream, true);
                    var subSavePath = Path.Combine(savePath, reader.FileName + "_unpacked");
                    switch (subReader.FileType)
                    {
                        case FileType.BundleFile:
                            total += ExtractBundleFile(subReader, subSavePath);
                            break;
                        case FileType.MhyFile:
                            total += ExtractMhyFile(subReader, subSavePath);
                            break;
                    }
                } while (stream.Remaining > 0);
            }
            catch (InvalidCastException)
            {
                Logger.Error(
                    $"Game type mismatch, Expected {nameof(Blk)} but got {Game.Name} ({Game.GetType().Name}) !!"
                );
            }
            return total;
        }

        private static int ExtractBlockFile(FileReader reader, string savePath)
        {
            int total = 0;
            Logger.Info($"Decompressing {reader.FileName} ...");
            using var stream = new OffsetStream(reader.BaseStream, 0);
            do
            {
                stream.Offset = stream.AbsolutePosition;
                var subSavePath = Path.Combine(savePath, reader.FileName + "_unpacked");
                var dummyPath = Path.Combine(
                    reader.FullPath,
                    stream.AbsolutePosition.ToString("X8")
                );
                var subReader = new FileReader(dummyPath, stream, true);
                total += ExtractBundleFile(subReader, subSavePath);
            } while (stream.Remaining > 0);
            return total;
        }

        private static int ExtractMhyFile(FileReader reader, string savePath)
        {
            Logger.Info($"Decompressing {reader.FileName} ...");
            try
            {
                var mhy0File = new MhyFile(reader, (Mhy)Game);
                reader.Dispose();
                if (mhy0File.fileList.Count > 0)
                {
                    var extractPath = Path.Combine(savePath, reader.FileName + "_unpacked");
                    return ExtractStreamFile(extractPath, mhy0File.fileList);
                }
            }
            catch (InvalidCastException)
            {
                Logger.Error(
                    $"Game type mismatch, Expected {nameof(Mhy)} but got {Game.Name} ({Game.GetType().Name}) !!"
                );
            }
            return 0;
        }

        private static int ExtractStreamFile(string extractPath, List<StreamFile> fileList)
        {
            int extractedCount = 0;
            foreach (var file in fileList)
            {
                var filePath = Path.Combine(extractPath, file.path);
                var fileDirectory = Path.GetDirectoryName(filePath);
                if (!Directory.Exists(fileDirectory))
                {
                    Directory.CreateDirectory(fileDirectory);
                }
                if (!File.Exists(filePath))
                {
                    using (var fileStream = File.Create(filePath))
                    {
                        file.stream.CopyTo(fileStream);
                    }
                    extractedCount += 1;
                }
                file.stream.Dispose();
            }
            return extractedCount;
        }

        public static void UpdateContainers()
        {
            if (exportableAssets.Count > 0)
            {
                Logger.Info("Updating Containers...");
                foreach (var asset in exportableAssets)
                {
                    if (int.TryParse(asset.Container, out var value))
                    {
                        var last = unchecked((uint)value);
                        var name = Path.GetFileNameWithoutExtension(asset.SourceFile.originalPath);
                        if (uint.TryParse(name, out var id))
                        {
                            var path = ResourceIndex.GetContainer(id, last);
                            if (!string.IsNullOrEmpty(path))
                            {
                                asset.Container = path;
                                if (asset.Type == ClassIDType.MiHoYoBinData)
                                {
                                    asset.Text = Path.GetFileNameWithoutExtension(path);
                                }
                            }
                        }
                    }
                }
                Logger.Info("Updated !!");
            }
        }

        public static void BuildAssetData(
            ClassIDType[] typeFilters,
            Regex[] nameFilters,
            Regex[] containerFilters,
            long[] pathIdFilters,
            Regex[] nameExcludeFilters,
            Regex[] containerExcludeFilters,
            ref int i
        )
        {
            var objectAssetItemDic = new Dictionary<Object, AssetItem>();
            var mihoyoBinDataNames = new List<(PPtr<Object>, string)>();
            var containers = new List<(PPtr<Object>, string)>();
            var containerMainAssets = new HashSet<Object>();
            foreach (var assetsFile in assetsManager.assetsFileList)
            {
                foreach (var asset in assetsFile.Objects)
                {
                    ProcessAssetData(
                        asset,
                        objectAssetItemDic,
                        mihoyoBinDataNames,
                        containers,
                        containerMainAssets,
                        ref i
                    );
                }
            }
            foreach ((var pptr, var name) in mihoyoBinDataNames)
            {
                if (pptr.TryGet<MiHoYoBinData>(out var obj))
                {
                    var assetItem = objectAssetItemDic[obj];
                    if (
                        int.TryParse(
                            name,
                            NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture,
                            out var hash
                        )
                    )
                    {
                        assetItem.Text = name;
                        assetItem.Container = hash.ToString();
                    }
                    else
                        assetItem.Text = $"BinFile #{assetItem.m_PathID}";
                }
            }
            if (!SkipContainer)
            {
                foreach ((var pptr, var container) in containers)
                {
                    if (pptr.TryGet(out var obj))
                    {
                        objectAssetItemDic[obj].Container = container;
                    }
                }
                containers.Clear();
                if (Game.Type.IsGISubGroup())
                {
                    UpdateContainers();
                }
            }

            var preFilterCounts = CountAssetItemsByType(exportableAssets);
            var pathIdFilterSet = pathIdFilters.IsNullOrEmpty()
                ? null
                : pathIdFilters.ToHashSet();
            var matches = exportableAssets
                .Where(x =>
                {
                    var isMatchRegex =
                        nameFilters.IsNullOrEmpty() || nameFilters.Any(y => y.IsMatch(x.Text));
                    var isFilteredType =
                        typeFilters.IsNullOrEmpty() || typeFilters.Contains(x.Type);
                    var isContainerMatch =
                        containerFilters.IsNullOrEmpty()
                        || containerFilters.Any(y => y.IsMatch(GetFilterableContainerText(x)));
                    var isPathIdExactMatch = pathIdFilterSet != null && pathIdFilterSet.Contains(x.m_PathID);
                    var isPathIdMatch =
                        pathIdFilterSet == null || isPathIdExactMatch;
                    var isNameExcluded =
                        !nameExcludeFilters.IsNullOrEmpty()
                        && nameExcludeFilters.Any(y => y.IsMatch(x.Text ?? string.Empty));
                    var isContainerExcluded =
                        !containerExcludeFilters.IsNullOrEmpty()
                        && containerExcludeFilters.Any(y => y.IsMatch(GetFilterableContainerText(x)));
                    // 显式 --path_ids 是定向诊断/刷新入口，已经精确到 Unity 对象。
                    // 默认 3D 路径/名字排除会过滤 UI、cutscene 等路径；精确 PathID 不能被这些默认规则误杀。
                    if (isPathIdExactMatch)
                    {
                        return isFilteredType;
                    }

                    return isMatchRegex
                        && isFilteredType
                        && isContainerMatch
                        && isPathIdMatch
                        && !isNameExcluded
                        && !isContainerExcluded;
                })
                .ToArray();
            var regexFilteredCounts = CountAssetItemsByType(matches);
            if (ModelRootsOnly)
            {
                matches = FilterModelRootsOnly(matches, containerMainAssets);
                matches = FilterUsefulModelRoots(matches);
            }
            if (WorkMode == WorkMode.Library)
            {
                ProfileLogger.Event("library_candidate_counts", new Dictionary<string, object>
                {
                    ["beforeFilterTotal"] = exportableAssets.Count,
                    ["beforeFilterByType"] = preFilterCounts,
                    ["afterNameContainerFilterTotal"] = regexFilteredCounts.Values.Sum(),
                    ["afterNameContainerFilterByType"] = regexFilteredCounts,
                    ["pathIdFilterCount"] = pathIdFilterSet?.Count ?? 0,
                    ["afterModelRootFilterTotal"] = matches.Length,
                    ["afterModelRootFilterByType"] = CountAssetItemsByType(matches),
                    ["containerMainAssetCount"] = containerMainAssets.Count,
                    ["modelRootsOnly"] = ModelRootsOnly,
                });
            }
            exportableAssets.Clear();
            exportableAssets.AddRange(matches);
        }

        private static Dictionary<string, int> CountAssetItemsByType(IEnumerable<AssetItem> assets)
        {
            return assets
                .GroupBy(x => x.TypeString ?? x.Type.ToString(), StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x.Count())
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
        }

        private static string GetFilterableContainerText(AssetItem asset)
        {
            return string.Join(
                "\n",
                new[]
                {
                    asset.Container,
                    asset.SourceFile?.originalPath,
                    asset.SourceFile?.fileName,
                    asset.Text,
                }.Where(x => !string.IsNullOrWhiteSpace(x))
            );
        }

        private static AssetItem[] FilterUsefulModelRoots(AssetItem[] assets)
        {
            var skipped = 0;
            var filtered = assets
                .Where(x =>
                {
                    if (x.Asset is not GameObject && x.Asset is not Animator)
                    {
                        return true;
                    }

                    var exclude = IsExcludedModelRoot(x);
                    if (exclude)
                    {
                        skipped++;
                    }
                    return !exclude;
                })
                .ToArray();

            if (skipped > 0)
            {
                Logger.Info(
                    $"--model_roots_only skipped {skipped} cutscene/helper/collision model root(s)."
                );
            }

            return filtered;
        }

        private static bool IsExcludedModelRoot(AssetItem asset)
        {
            var filterTexts = new[]
                {
                    asset.Text,
                    asset.Container,
                    asset.SourceFile?.originalPath,
                    asset.SourceFile?.fileName,
                    GetFilterableContainerText(asset),
                }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Replace('\\', '/'));
            return filterTexts.Any(x => ModelRootExcludePatterns.Any(y => y.IsMatch(x)));
        }

        private static AssetItem[] FilterModelRootsOnly(
            AssetItem[] assets,
            HashSet<Object> containerMainAssets
        )
        {
            var containerModelAssets = assets
                .Where(x => x.Asset is GameObject && containerMainAssets.Contains(x.Asset))
                .Select(x => (GameObject)x.Asset)
                .ToHashSet();
            if (containerModelAssets.Count == 0)
            {
                var skippedModels = assets.Count(x => x.Asset is GameObject);
                if (skippedModels > 0)
                {
                    Logger.Warning(
                        $"--model_roots_only found no AssetBundle/ResourceManager main GameObject entries for {skippedModels} GameObject model(s)."
                    );
                }
                if (WorkMode == WorkMode.Library)
                {
                    var hierarchyRoots = FilterHierarchyModelRoots(assets, out var skippedChildren);
                    if (skippedChildren > 0)
                    {
                        Logger.Info($"Library model root fallback kept hierarchy roots and skipped {skippedChildren} child model candidate(s).");
                    }
                    return hierarchyRoots;
                }
                if (skippedModels > 0)
                {
                    Logger.Warning($"--model_roots_only skipped {skippedModels} GameObject model(s).");
                }
            }

            return assets
                .Where(x =>
                    x.Asset is not GameObject gameObject
                    || containerModelAssets.Contains(gameObject)
                )
                .ToArray();
        }

        public static void ProcessAssetData(
            Object asset,
            Dictionary<Object, AssetItem> objectAssetItemDic,
            List<(PPtr<Object>, string)> mihoyoBinDataNames,
            List<(PPtr<Object>, string)> containers,
            HashSet<Object> containerMainAssets,
            ref int i
        )
        {
            var assetItem = new AssetItem(asset);
            objectAssetItemDic.Add(asset, assetItem);
            assetItem.UniqueID = "#" + i++;
            var exportable = false;
            switch (asset)
            {
                case GameObject m_GameObject:
                    exportable = ClassIDType.GameObject.CanExport() && m_GameObject.HasModel();
                    break;
                case Texture2D m_Texture2D:
                    if (!string.IsNullOrEmpty(m_Texture2D.m_StreamData?.path))
                        assetItem.FullSize = asset.byteSize + m_Texture2D.m_StreamData.size;
                    exportable = ClassIDType.Texture2D.CanExport();
                    break;
                case Texture2DArray m_Texture2DArray:
                    if (!string.IsNullOrEmpty(m_Texture2DArray.m_StreamData?.path))
                        assetItem.FullSize = asset.byteSize + m_Texture2DArray.m_StreamData.size;
                    exportable = ClassIDType.Texture2DArray.CanExport();
                    break;
                case AudioClip m_AudioClip:
                    if (!string.IsNullOrEmpty(m_AudioClip.m_Source))
                        assetItem.FullSize = asset.byteSize + m_AudioClip.m_Size;
                    exportable = ClassIDType.AudioClip.CanExport();
                    break;
                case VideoClip m_VideoClip:
                    if (!string.IsNullOrEmpty(m_VideoClip.m_OriginalPath))
                        assetItem.FullSize =
                            asset.byteSize + m_VideoClip.m_ExternalResources.m_Size;
                    exportable = ClassIDType.VideoClip.CanExport();
                    break;
                case MonoBehaviour m_MonoBehaviour:
                    exportable = ClassIDType.MonoBehaviour.CanExport();
                    break;
                case AssetBundle m_AssetBundle:
                    foreach (var m_Container in m_AssetBundle.m_Container)
                    {
                        string container = m_Container.Key;

                        if (
                            ulong.TryParse(container, out var hash)
                            && Paths.TryGetValue(hash, out var path)
                        )
                        {
                            container = path;
                        }
                        else if (hash == 0) //Allows HSR or other games with actual containers to extract byContainer, without needing my JSON, or other external files.
                        {
                            container = m_Container.Key;
                        }
                        else
                        {
                            container = null;
                        }

                        if (m_Container.Value.asset.TryGet(out var mainAsset))
                        {
                            containerMainAssets.Add(mainAsset);
                        }

                        var preloadIndex = m_Container.Value.preloadIndex;
                        var preloadSize = m_Container.Value.preloadSize;
                        var preloadEnd = preloadIndex + preloadSize;
                        for (int k = preloadIndex; k < preloadEnd; k++)
                        {
                            containers.Add((m_AssetBundle.m_PreloadTable[k], container));
                        }
                    }

                    exportable = ClassIDType.AssetBundle.CanExport();
                    break;
                case IndexObject m_IndexObject:
                    foreach (var index in m_IndexObject.AssetMap)
                    {
                        mihoyoBinDataNames.Add((index.Value.Object, index.Key));
                    }

                    exportable = ClassIDType.IndexObject.CanExport();
                    break;
                case ResourceManager m_ResourceManager:
                    foreach (var m_Container in m_ResourceManager.m_Container)
                    {
                        containers.Add((m_Container.Value, m_Container.Key));
                        if (m_Container.Value.TryGet(out var mainAsset))
                        {
                            containerMainAssets.Add(mainAsset);
                        }
                    }

                    exportable = ClassIDType.ResourceManager.CanExport();
                    break;
                case Mesh _ when ClassIDType.Mesh.CanExport():
                case TextAsset _ when ClassIDType.TextAsset.CanExport():
                case AnimationClip _ when ClassIDType.AnimationClip.CanExport():
                case Font _ when ClassIDType.Font.CanExport():
                case MovieTexture _ when ClassIDType.MovieTexture.CanExport():
                case Sprite _ when ClassIDType.Sprite.CanExport():
                case Material _ when ClassIDType.Material.CanExport():
                case MiHoYoBinData _ when ClassIDType.MiHoYoBinData.CanExport():
                case Shader _ when ClassIDType.Shader.CanExport():
                case Animator _ when ClassIDType.Animator.CanExport():
                    exportable = true;
                    break;
            }
            // In a scenario where a specific case doesn't exist, still allows export, without needing a class file for them.
            // Best used when --export_type Raw or Dump.
            if (!exportable && assetItem.Type.CanExport())
            {
                exportable = true;
            }
            if (assetItem.Text == "")
            {
                assetItem.Text = assetItem.TypeString + assetItem.UniqueID;
            }

            if (exportable)
            {
                exportableAssets.Add(assetItem);
            }
        }

        public static void ExportAssets(
            string savePath,
            List<AssetItem> toExportAssets,
            AssetGroupOption assetGroupOption,
            ExportType exportType
        )
        {
            if (TryExportIndependentAssetsParallel(savePath, toExportAssets, assetGroupOption, exportType))
            {
                return;
            }

            int toExportCount = toExportAssets.Count;
            int exportedCount = 0;
            int processedCount = 0;
            foreach (var asset in toExportAssets)
            {
                processedCount++;
                string exportPath;
                switch (assetGroupOption)
                {
                    case AssetGroupOption.ByType: //type name
                        exportPath = Path.Combine(savePath, asset.TypeString);
                        break;
                    case AssetGroupOption.ByContainer: //container path
                        if (!string.IsNullOrEmpty(asset.Container))
                        {
                            exportPath = Path.HasExtension(asset.Container)
                                ? Path.Combine(savePath, Path.GetDirectoryName(asset.Container))
                                : Path.Combine(savePath, asset.Container);
                        }
                        else
                        {
                            exportPath = Path.Combine(savePath, asset.TypeString);
                        }
                        break;
                    case AssetGroupOption.ByLibrary:
                        exportPath = GetLibraryExportPath(savePath, asset);
                        break;
                    case AssetGroupOption.BySource: //source file
                        exportPath = GetSourceExportPath(savePath, asset);
                        break;
                    default:
                        exportPath = savePath;
                        break;
                }
                exportPath += Path.DirectorySeparatorChar;
                Logger.Verbose(
                    $"[{exportedCount}/{toExportCount}] Exporting {asset.TypeString}: {asset.Text}"
                );
                try
                {
                    switch (exportType)
                    {
                        case ExportType.Raw:
                            if (ExportRawFile(asset, exportPath))
                            {
                                exportedCount++;
                                LogAssetExported(asset, toExportCount);
                                AppendExportManifest(savePath, asset, exportPath);
                            }
                            break;
                        case ExportType.Dump:
                            if (ExportDumpFile(asset, exportPath))
                            {
                                exportedCount++;
                                LogAssetExported(asset, toExportCount);
                                AppendExportManifest(savePath, asset, exportPath);
                            }
                            break;
                        case ExportType.Convert:
                            if (ExportConvertFile(asset, exportPath))
                            {
                                exportedCount++;
                                LogAssetExported(asset, toExportCount);
                                AppendExportManifest(savePath, asset, exportPath);
                            }
                            break;
                        case ExportType.JSON:
                            if (ExportJSONFile(asset, exportPath))
                            {
                                exportedCount++;
                                LogAssetExported(asset, toExportCount);
                                AppendExportManifest(savePath, asset, exportPath);
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(
                        $"Export {asset.Type}:{asset.Text} error\r\n{ex.Message}\r\n{ex.StackTrace}"
                    );
                }

                if (processedCount % 500 == 0)
                {
                    Logger.Info(
                        $"Processed {processedCount}/{toExportCount} {asset.TypeString} asset(s); exported {exportedCount}."
                    );
                }
            }

            var statusText =
                exportedCount == 0
                    ? "Nothing exported."
                    : $"Finished exporting {exportedCount} assets.";

            if (toExportCount > exportedCount)
            {
                statusText +=
                    $" {toExportCount - exportedCount} assets skipped (not extractable or files already exist)";
            }

            Logger.Info(statusText);
        }

        private static bool TryExportIndependentAssetsParallel(
            string savePath,
            List<AssetItem> toExportAssets,
            AssetGroupOption assetGroupOption,
            ExportType exportType)
        {
            if (MaxExportTasks <= 1 || toExportAssets.Count < 64)
            {
                return false;
            }

            if (toExportAssets.Any(x => !CanExportAssetIndependently(x, exportType)))
            {
                return false;
            }

            var planned = toExportAssets
                .Select(asset => new PlannedIndependentExport(
                    asset,
                    GetExportPath(savePath, assetGroupOption, asset) + Path.DirectorySeparatorChar,
                    BuildIndependentExportCollisionKey(savePath, assetGroupOption, asset, exportType)))
                .ToList();
            if (planned.GroupBy(x => x.CollisionKey, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
            {
                return false;
            }

            Logger.Info($"Exporting {toExportAssets.Count} independent {toExportAssets[0].TypeString} asset(s) with {MaxExportTasks} task(s).");
            var toExportCount = planned.Count;
            var exportedCount = 0;
            var processedCount = 0;
            var errors = new List<string>();
            var errorsLock = new object();

            Parallel.ForEach(
                planned,
                new ParallelOptions { MaxDegreeOfParallelism = MaxExportTasks },
                plan =>
                {
                    var exported = false;
                    try
                    {
                        exported = ExportAssetByType(plan.Asset, plan.ExportPath, exportType);
                    }
                    catch (Exception ex)
                    {
                        lock (errorsLock)
                        {
                            errors.Add($"Export {plan.Asset.Type}:{plan.Asset.Text} error\r\n{ex.Message}\r\n{ex.StackTrace}");
                        }
                    }

                    if (exported)
                    {
                        Interlocked.Increment(ref exportedCount);
                        AppendExportManifest(savePath, plan.Asset, plan.ExportPath);
                    }

                    var processed = Interlocked.Increment(ref processedCount);
                    if (processed % 500 == 0)
                    {
                        Logger.Info($"Processed {processed}/{toExportCount} {plan.Asset.TypeString} asset(s); exported {Volatile.Read(ref exportedCount)}.");
                    }
                });

            foreach (var error in errors)
            {
                Logger.Error(error);
            }

            var finalExported = Volatile.Read(ref exportedCount);
            var statusText = finalExported == 0
                ? "Nothing exported."
                : $"Finished exporting {finalExported} assets.";
            if (toExportCount > finalExported)
            {
                statusText += $" {toExportCount - finalExported} assets skipped (not extractable or files already exist)";
            }

            Logger.Info(statusText);
            return true;
        }

        private static bool CanExportAssetIndependently(AssetItem asset, ExportType exportType)
        {
            if (asset == null)
            {
                return false;
            }

            if (exportType is ExportType.Raw or ExportType.Dump or ExportType.JSON)
            {
                return asset.Asset is not GameObject and not Animator and not Mesh;
            }

            return asset.Asset is Texture2D
                or Texture2DArray
                or AudioClip
                or Shader
                or TextAsset
                or Material
                or MiHoYoBinData
                or Font
                or MovieTexture
                or VideoClip
                or Sprite;
        }

        private static bool ExportAssetByType(AssetItem asset, string exportPath, ExportType exportType)
        {
            return exportType switch
            {
                ExportType.Raw => ExportRawFile(asset, exportPath),
                ExportType.Dump => ExportDumpFile(asset, exportPath),
                ExportType.Convert => ExportConvertFile(asset, exportPath),
                ExportType.JSON => ExportJSONFile(asset, exportPath),
                _ => false,
            };
        }

        private static string BuildIndependentExportCollisionKey(
            string savePath,
            AssetGroupOption assetGroupOption,
            AssetItem asset,
            ExportType exportType)
        {
            var exportPath = GetExportPath(savePath, assetGroupOption, asset);
            var extension = exportType switch
            {
                ExportType.Raw => ".dat",
                ExportType.Dump => ".txt",
                ExportType.JSON => ".json",
                ExportType.Convert => GetConvertCollisionExtension(asset),
                _ => ".dat",
            };
            return Path.Combine(exportPath, $"{Exporter.FixFileName(asset.Text)}{extension}");
        }

        private static string GetConvertCollisionExtension(AssetItem asset)
        {
            return asset.Type switch
            {
                ClassIDType.Texture2D => Properties.Settings.Default.convertTexture
                    ? "." + Properties.Settings.Default.convertType.ToString().ToLowerInvariant()
                    : ".tex",
                ClassIDType.Texture2DArray => ".texture2darray.folder",
                ClassIDType.AudioClip => ".audioclip",
                ClassIDType.Shader => ".shader.raw",
                ClassIDType.TextAsset => ".txt",
                ClassIDType.MonoBehaviour => ".json",
                ClassIDType.Font => ".ttf",
                ClassIDType.MovieTexture => ".movietexture",
                ClassIDType.VideoClip => ".ogv",
                ClassIDType.Sprite => ".png",
                ClassIDType.AnimationClip => ".anim",
                ClassIDType.MiHoYoBinData => ".dat",
                ClassIDType.Material => ".json",
                _ => ".dat",
            };
        }

        private sealed class PlannedIndependentExport
        {
            public PlannedIndependentExport(AssetItem asset, string exportPath, string collisionKey)
            {
                Asset = asset;
                ExportPath = exportPath;
                CollisionKey = collisionKey;
            }

            public AssetItem Asset { get; }
            public string ExportPath { get; }
            public string CollisionKey { get; }
        }

        private static void LogAssetExported(AssetItem asset, int batchCount)
        {
            if (asset.Type == ClassIDType.AnimationClip && batchCount > 200)
            {
                Logger.Verbose($"Exported {asset.TypeString}: {asset.Text}");
                return;
            }

            Logger.Info($"Exported {asset.TypeString}: {asset.Text}");
        }

        public static void ExportCurrentAssets(
            string savePath,
            AssetGroupOption assetGroupOption,
            ExportType exportType
        )
        {
            switch (WorkMode)
            {
                case WorkMode.Library:
                    ExportAssetLibrary(savePath);
                    break;
                case WorkMode.AudioLibrary:
                    ExportAudioLibrary(savePath);
                    break;
                case WorkMode.SplitObjects:
                    ExportSplitObjects(savePath, assetGroupOption);
                    break;
                case WorkMode.Animator:
                    ExportAnimators(savePath, assetGroupOption);
                    break;
                default:
                    ExportAssets(savePath, exportableAssets, assetGroupOption, exportType);
                    break;
            }
        }

        public static void ExportAssetLibrary(string savePath)
        {
            var models = exportableAssets.Where(x => x.Asset is GameObject or Animator).ToList();
            var animations = exportableAssets.Where(x => x.Asset is AnimationClip).ToList();
            var shaders = exportableAssets.Where(x => x.Asset is Shader).ToList();
            var sourcePartModels = new List<AssetItem>();
            var staticMeshes = IncludeStaticMeshes
                ? CollectLibraryStaticMeshModels(sourcePartModels)
                : new List<AssetItem>();
            var libraryTextures = CollectLibraryTextureAssets(savePath);

            models = FilterLibraryModelSources(models, sourcePartModels);
            models = FilterOptimizedAnimatorMissingAvatarModels(models);
            models.AddRange(staticMeshes);
            models = FilterDeprecatedLibraryAssets(models, "model");
            models = FilterLowValueBrowsableModels(models, sourcePartModels);
            animations = FilterDeprecatedLibraryAssets(animations, "animation");

            Logger.Info(
                $"Exporting asset library: {models.Count} model candidate(s), {sourcePartModels.Count} indexed source part(s), {animations.Count} animation clip(s), {libraryTextures.Count} material/terrain texture asset(s), {shaders.Count} shader(s)."
            );
            AppendModelSourcePartCatalog(savePath, sourcePartModels);

            var modelAnimations = CliExportOptions.ExportEmbeddedAnimations ? animations : null;
            ExportModelAssets(savePath, models, AssetGroupOption.ByLibrary, modelAnimations);
            ExportSeparateAnimationClips(savePath);
            if (libraryTextures.Count > 0)
            {
                ExportAssets(savePath, libraryTextures, AssetGroupOption.ByLibrary, ExportType.Convert);
            }
            if (shaders.Count > 0)
            {
                ExportAssets(savePath, shaders, AssetGroupOption.ByLibrary, ExportType.Convert);
            }
        }

        private static List<AssetItem> FilterOptimizedAnimatorMissingAvatarModels(List<AssetItem> models)
        {
            var kept = new List<AssetItem>(models.Count);
            var skipped = 0;
            var samples = new List<string>();
            foreach (var model in models)
            {
                if (!IsOptimizedAnimatorMissingAvatarModel(model))
                {
                    kept.Add(model);
                    continue;
                }

                skipped++;
                if (samples.Count < 8)
                {
                    samples.Add(model.Text);
                }
            }

            if (skipped > 0)
            {
                Logger.Warning(
                    $"Library skipped {skipped} optimized Animator model root(s) without Avatar; these roots cannot restore the Unity transform hierarchy and stay diagnostic only. Samples: {string.Join(", ", samples)}"
                );
                ProfileLogger.Event("library_skip_optimized_animator_missing_avatar", new Dictionary<string, object>
                {
                    ["skippedCount"] = skipped,
                    ["samples"] = samples,
                    ["rule"] = "hasTransformHierarchy=false but Animator.m_Avatar is null. Do not guess bones or Avatar; keep renderer/static mesh children eligible through explicit mesh paths.",
                });
            }

            return kept;
        }

        private static bool IsOptimizedAnimatorMissingAvatarModel(AssetItem model)
        {
            return TryGetOptimizedAnimatorAvatarBlockReason(model, out var reason)
                && reason == "optimizedAnimatorMissingAvatar";
        }

        private static bool TryGetOptimizedAnimatorAvatarBlockReason(AssetItem model, out string reason)
        {
            reason = null;
            var animator = model?.Asset switch
            {
                Animator directAnimator => directAnimator,
                GameObject gameObject => gameObject.m_Animator,
                _ => null,
            };
            if (animator == null || animator.m_HasTransformHierarchy)
            {
                return false;
            }

            // Unity 优化层级需要 Avatar 才能还原骨骼树。空引用不能硬猜；未解析引用通常说明局部样本没带完整 CAB 依赖。
            if (animator.m_Avatar == null || animator.m_Avatar.IsNull)
            {
                reason = "optimizedAnimatorMissingAvatar";
                return true;
            }
            if (!animator.m_Avatar.TryGet(out _))
            {
                reason = "optimizedAnimatorAvatarUnresolved";
                return true;
            }

            return false;
        }

        private static List<AssetItem> FilterDeprecatedLibraryAssets(List<AssetItem> assets, string label)
        {
            var kept = new List<AssetItem>(assets.Count);
            var skipped = 0;
            foreach (var asset in assets)
            {
                if (IsDeprecatedLibraryAsset(asset))
                {
                    skipped++;
                    continue;
                }

                kept.Add(asset);
            }

            if (skipped > 0)
            {
                Logger.Info($"Library skipped {skipped} deprecated/obsolete {label} asset(s).");
                ProfileLogger.Event("library_deprecated_asset_filter", new Dictionary<string, object>
                {
                    ["assetKind"] = label,
                    ["inputCount"] = assets.Count,
                    ["skippedCount"] = skipped,
                    ["keptCount"] = kept.Count,
                    ["rule"] = "Path segment exactly matches obsolete or deprecated. Source SQLite index remains complete; default Library export omits these browsable assets.",
                });
            }

            return kept;
        }

        private static bool IsDeprecatedLibraryAsset(AssetItem asset)
        {
            return GetDeprecatedFilterTexts(asset)
                .Any(x => DeprecatedLibraryPathPattern.IsMatch(x));
        }

        private static List<AssetItem> FilterLowValueBrowsableModels(List<AssetItem> models, List<AssetItem> sourcePartModels)
        {
            var kept = new List<AssetItem>(models.Count);
            var skipped = 0;
            foreach (var model in models)
            {
                if (IsLowValueBrowsableModel(model))
                {
                    model.LibraryRole = "SourcePart";
                    sourcePartModels.Add(model);
                    skipped++;
                    continue;
                }

                kept.Add(model);
            }

            if (skipped > 0)
            {
                Logger.Info($"Library downgraded {skipped} placeholder/debug preview model(s) to source-part index entries.");
                ProfileLogger.Event("library_low_value_model_filter", new Dictionary<string, object>
                {
                    ["inputCount"] = models.Count,
                    ["skippedCount"] = skipped,
                    ["keptCount"] = kept.Count,
                    ["rule"] = "Only strong low-value browsing signals are downgraded: PLACEHOLDER, Dummy token, AimPreview, ArenaBlock Invalid/ValidPlace/ValidRemove, and cameras/lights/audio helpers. sfx/fx/vfx/spawner/fader, Armature, and mixamorig roots are not globally filtered because they can carry valid mesh/skeleton/effect assets in some Unity projects. Part_* is not filtered by prefix.",
                });
            }

            return kept;
        }

        private static bool IsLowValueBrowsableModel(AssetItem asset)
        {
            return GetLowValueFilterTexts(asset)
                .Any(x =>
                    LowValueBrowsableModelPattern.IsMatch(x)
                    || Non3DLibraryRootPattern.IsMatch(x)
                );
        }

        private static IEnumerable<string> GetDeprecatedFilterTexts(AssetItem asset)
        {
            if (!string.IsNullOrWhiteSpace(asset.Container))
            {
                yield return asset.Container.Replace('\\', '/');
            }
            if (!string.IsNullOrWhiteSpace(asset.SourceFile?.originalPath))
            {
                yield return asset.SourceFile.originalPath.Replace('\\', '/');
            }
        }

        private static IEnumerable<string> GetLowValueFilterTexts(AssetItem asset)
        {
            if (!string.IsNullOrWhiteSpace(asset.Text))
            {
                yield return asset.Text.Replace('\\', '/');
            }
            if (!string.IsNullOrWhiteSpace(asset.Container))
            {
                yield return asset.Container.Replace('\\', '/');
            }
            if (!string.IsNullOrWhiteSpace(asset.SourceFile?.originalPath))
            {
                yield return asset.SourceFile.originalPath.Replace('\\', '/');
            }
            if (!string.IsNullOrWhiteSpace(asset.SourceFile?.fileName))
            {
                yield return asset.SourceFile.fileName.Replace('\\', '/');
            }
        }

        private static List<AssetItem> CollectLibraryTextureAssets(string savePath)
        {
            var textureItems = exportableAssets
                .Where(x => x.Asset is Texture2D or Texture2DArray)
                .GroupBy(x => GetAssetKey((AnimeStudio.Object)x.Asset), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

            if (textureItems.Count == 0)
            {
                return new List<AssetItem>();
            }

            var selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var references = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);

            foreach (var arrayItem in textureItems.Values.Where(x => x.Asset is Texture2DArray))
            {
                var key = GetAssetKey((AnimeStudio.Object)arrayItem.Asset);
                selectedKeys.Add(key);
                AddTextureLibraryReference(references, key, new
                {
                    basis = GetTexture2DArrayUsage(arrayItem),
                    reason = IsDataTexture2DArray(arrayItem)
                        ? "Texture2DArray appears to be a float/HDR/unknown shader or terrain data array; PNG layers are diagnostic previews."
                        : "Texture2DArray is a standalone material/terrain/shader texture library asset.",
                });
            }

            foreach (var materialItem in exportableAssets.Where(x => x.Asset is Material))
            {
                var material = (Material)materialItem.Asset;
                foreach (var texEnv in material.m_SavedProperties?.m_TexEnvs ?? new List<KeyValuePair<string, UnityTexEnv>>())
                {
                    if (texEnv.Value?.m_Texture == null || texEnv.Value.m_Texture.m_PathID == 0)
                    {
                        continue;
                    }

                    if (!texEnv.Value.m_Texture.TryGet<Texture>(out var texture))
                    {
                        continue;
                    }

                    if (texture is not Texture2D and not Texture2DArray)
                    {
                        continue;
                    }

                    var key = GetAssetKey(texture);
                    if (!textureItems.ContainsKey(key))
                    {
                        continue;
                    }

                    selectedKeys.Add(key);
                    AddTextureLibraryReference(references, key, new
                    {
                        basis = "Material.m_SavedProperties.m_TexEnvs",
                        slot = texEnv.Key,
                        material = material.m_Name,
                        materialSource = material.assetsFile?.originalPath ?? material.assetsFile?.fileName,
                        materialPathId = material.m_PathID,
                    });
                }
            }

            var selected = selectedKeys
                .Select(x => textureItems[x])
                .OrderBy(x => x.TypeString, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Container ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Text ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var item in selected)
            {
                item.LibraryRole = item.Asset is Texture2DArray ? "Texture2DArray" : "MaterialLibrary";
            }

            WriteTextureLibraryReport(savePath, selected, references);
            return selected;
        }

        private static void AddTextureLibraryReference(Dictionary<string, List<object>> references, string key, object reference)
        {
            if (!references.TryGetValue(key, out var list))
            {
                list = new List<object>();
                references[key] = list;
            }
            list.Add(reference);
        }

        private static void WriteTextureLibraryReport(string savePath, List<AssetItem> textures, Dictionary<string, List<object>> references)
        {
            if (textures.Count == 0)
            {
                return;
            }

            var report = new JObject
            {
                ["schemaVersion"] = 1,
                ["generatedUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["rule"] = "Default Library exports material-referenced Texture2D assets and all Texture2DArray assets as independent material/terrain texture library resources. Model display dependencies remain in Textures/_ModelDependencies.",
                ["counts"] = JObject.FromObject(new
                {
                    total = textures.Count,
                    texture2D = textures.Count(x => x.Asset is Texture2D),
                    texture2DArray = textures.Count(x => x.Asset is Texture2DArray && !IsDataTexture2DArray(x)),
                    dataTexture2DArray = textures.Count(IsDataTexture2DArray),
                }),
                ["textures"] = JArray.FromObject(textures.Select(x =>
                {
                    var key = GetAssetKey((AnimeStudio.Object)x.Asset);
                    var isDataTextureArray = IsDataTexture2DArray(x);
                    return new
                    {
                        name = x.Text,
                        type = x.TypeString,
                        libraryRole = x.LibraryRole,
                        textureArrayUsage = x.Asset is Texture2DArray ? GetTexture2DArrayUsage(x) : null,
                        isDiagnosticPreview = isDataTextureArray,
                        source = x.SourceFile?.originalPath ?? x.SourceFile?.fileName,
                        container = x.Container,
                        pathId = x.m_PathID,
                        outputRoot = Path.Combine(
                            "Textures",
                            x.LibraryRole == "Texture2DArray"
                                ? GetTexture2DArrayRoot(x)
                                : "MaterialLibrary"),
                        references = references.TryGetValue(key, out var refs) ? refs : new List<object>(),
                    };
                })),
            };

            File.WriteAllText(Path.Combine(savePath, "texture_library.json"), report.ToString(Newtonsoft.Json.Formatting.Indented));
            File.WriteAllText(Path.Combine(savePath, "TEXTURE_LIBRARY.md"),
                "# Texture Library\n\n" +
                "默认 Library 会额外导出材质/地表贴图库：\n\n" +
                "- `Textures/_ModelDependencies`：模型 glTF 直接显示需要的贴图。\n" +
                "- `Textures/MaterialLibrary`：由 Unity `Material.m_SavedProperties.m_TexEnvs` 明确引用的 Texture2D。\n" +
                "- `Textures/Texture2DArray`：Texture2DArray 按 layer 拆出的可视贴图库资源，常用于 terrain、surface、shader/material 混合。\n" +
                "- `Textures/DataTexture2DArray`：float/HDR/未知语义数组贴图，常用于 shader、terrain 或运行时数据采样；PNG 只是诊断预览，看起来像雪花不一定代表导出错误。\n\n" +
                "这些贴图不一定会直接嵌入 glTF PBR 材质；自定义 shader、terrain splat、ColorMask/Tint 或 Texture2DArray 采样方式需要结合 `texture_library.json`、材质 JSON 和后续 shader/customization 管线处理。\n");
        }

        private static bool IsDataTexture2DArray(AssetItem item)
        {
            return item.Asset is Texture2DArray textureArray
                && Exporter.IsDataTexture2DArray(
                    textureArray,
                    item.Text,
                    item.Container,
                    item.SourceFile?.originalPath ?? item.SourceFile?.fileName);
        }

        private static string GetTexture2DArrayRoot(AssetItem item)
        {
            return IsDataTexture2DArray(item) ? "DataTexture2DArray" : "Texture2DArray";
        }

        private static string GetTexture2DArrayUsage(AssetItem item)
        {
            return IsDataTexture2DArray(item) ? "DataTextureArray" : "VisualTextureArray";
        }

        private static List<AssetItem> FilterLibraryModelSources(
            List<AssetItem> models,
            List<AssetItem> sourcePartModels)
        {
            if (CliExportOptions.ModelSource == ModelSourceMode.PrefabAndParts)
            {
                foreach (var model in models)
                {
                    model.LibraryRole = IsRawModelSourcePart(model) ? ClassifySourcePartRole(model) : "PrefabPrimary";
                }
                return models;
            }

            // 默认素材库只展示 prefab/Animator 组合体。raw fbx 仍进索引，避免身体、脸、附件零件污染 Models 浏览目录。
            var rawModels = models.Where(IsRawModelSourcePart).ToList();
            var primaryModels = models.Where(x => !IsRawModelSourcePart(x)).ToList();
            var primaryKeys = primaryModels
                .Select(GetLibraryModelGroupKey)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (CliExportOptions.ModelSource == ModelSourceMode.RawPartsOnly)
            {
                foreach (var raw in rawModels)
                {
                    raw.LibraryRole = ClassifySourcePartRole(raw);
                }
                sourcePartModels.AddRange(primaryModels.Select(x =>
                {
                    x.LibraryRole = "PrefabPrimary";
                    return x;
                }));
                Logger.Info($"--model_source RawPartsOnly selected {rawModels.Count} raw/source model part(s).");
                return rawModels;
            }

            foreach (var primary in primaryModels)
            {
                primary.LibraryRole = "PrefabPrimary";
            }

            var exportModels = new List<AssetItem>(primaryModels);
            foreach (var raw in rawModels)
            {
                var key = GetLibraryModelGroupKey(raw);
                if (!string.IsNullOrWhiteSpace(key) && primaryKeys.Contains(key))
                {
                    raw.LibraryRole = ClassifySourcePartRole(raw);
                    sourcePartModels.Add(raw);
                }
                else
                {
                    raw.LibraryRole = "RawUnreferenced";
                    exportModels.Add(raw);
                }
            }

            if (sourcePartModels.Count > 0)
            {
                Logger.Info($"--model_source PrefabPrimary indexed {sourcePartModels.Count} raw/source model part(s) without exporting them as browsable Models.");
            }
            return exportModels;
        }

        private static bool IsRawModelSourcePart(AssetItem asset)
        {
            var text = GetFilterableContainerText(asset).Replace('\\', '/').ToLowerInvariant();
            return Regex.IsMatch(text, @"(^|/)fbx(/|$)|\.fbx($|/)");
        }

        private static string ClassifySourcePartRole(AssetItem asset)
        {
            var text = string.Join("/", asset.Text, asset.Container, asset.SourceFile?.originalPath, asset.SourceFile?.fileName)
                .Replace('\\', '/')
                .ToLowerInvariant();
            if (Regex.IsMatch(text, @"face|eye|brow|mouth"))
            {
                return "AttachmentSource";
            }
            if (IsRawModelSourcePart(asset))
            {
                return "RawModel";
            }
            return "SourcePart";
        }

        private static string GetLibraryModelGroupKey(AssetItem asset)
        {
            var path = asset.Container;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = asset.SourceFile?.originalPath ?? asset.SourceFile?.fileName ?? asset.Text;
            }
            path = (path ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
            var fbxIndex = path.IndexOf("/fbx/", StringComparison.OrdinalIgnoreCase);
            if (fbxIndex >= 0)
            {
                return path.Substring(0, fbxIndex).Trim('/');
            }
            if (path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetDirectoryName(path)?.Replace('\\', '/').Trim('/');
            }
            if (Path.HasExtension(path))
            {
                return Path.GetDirectoryName(path)?.Replace('\\', '/').Trim('/');
            }
            return path.Trim('/');
        }

        private static void AppendModelSourcePartCatalog(string savePath, List<AssetItem> sourcePartModels)
        {
            if (sourcePartModels.Count == 0 || string.IsNullOrWhiteSpace(CliExportOptions.OutputRoot))
            {
                return;
            }

            var catalogPath = Path.Combine(CliExportOptions.OutputRoot, "asset_catalog.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(catalogPath));
            foreach (var item in sourcePartModels)
            {
                var entry = new
                {
                    kind = "ModelSourcePart",
                    libraryRole = item.LibraryRole,
                    resourceKind = InferLibraryResourceKind(item.Text, item.Container, item.SourceFile?.originalPath ?? item.SourceFile?.fileName),
                    exportedAt = DateTime.UtcNow.ToString("O"),
                    name = item.Text,
                    sourceType = item.TypeString,
                    container = item.Container,
                    source = item.SourceFile?.originalPath ?? item.SourceFile?.fileName,
                    pathId = item.m_PathID,
                    output = (string)null,
                    exportPolicy = CliExportOptions.ModelSource.ToString(),
                    modelGroupKey = GetLibraryModelGroupKey(item),
                };
                File.AppendAllText(catalogPath, JsonConvert.SerializeObject(entry) + Environment.NewLine);
            }
        }

        private static List<AssetItem> CollectLibraryStaticMeshModels(List<AssetItem> sourcePartModels)
        {
            var meshes = exportableAssets.Where(x => x.Asset is Mesh).ToList();
            if (meshes.Count == 0)
            {
                return new List<AssetItem>();
            }

            var selected = new List<AssetItem>();
            var downgraded = 0;
            var rendererBoundMeshKeys = GetRendererBoundStaticMeshKeys();
            foreach (var item in meshes)
            {
                if (IsLibraryStaticMeshPrimary(item, rendererBoundMeshKeys, out var reason))
                {
                    EnsureStaticMeshDisplayName(item);
                    item.LibraryRole = "StaticMeshPrimary";
                    selected.Add(item);
                    continue;
                }

                if (ShouldIndexStaticMeshSourcePart(reason))
                {
                    item.LibraryRole = "StaticMeshSource";
                    sourcePartModels.Add(item);
                    downgraded++;
                }
            }

            EnsureStaticMeshDuplicateNamesAreStable(selected);

            if (selected.Count > 0 || downgraded > 0)
            {
                Logger.Info($"Library static Mesh pipeline selected {selected.Count} container-backed static mesh asset(s) and indexed {downgraded} non-browsable mesh source part(s).");
                ProfileLogger.Event("library_static_mesh_filter", new Dictionary<string, object>
                {
                    ["inputCount"] = meshes.Count,
                    ["selectedCount"] = selected.Count,
                    ["sourcePartCount"] = downgraded,
                    ["rendererBoundMeshCount"] = rendererBoundMeshKeys.Count,
                    ["rule"] = "Promote Mesh assets with enough triangle data and strong Unity static-library signals. Signals include explicit container/preload paths, semantic source bundle paths, and SQLite-proven MeshFilter/SkinnedMeshRenderer -> Renderer usage. Renderer material binding improves quality, but missing materials are exported as tagged gray models instead of being dropped.",
                });
            }

            return selected;
        }

        private static bool ShouldIndexStaticMeshSourcePart(string reason)
        {
            return reason switch
            {
                "source_static_mesh" or "container_static_mesh" or "renderer_bound_static_mesh" => true,
                _ => false,
            };
        }

        private static bool IsLibraryStaticMeshPrimary(AssetItem item, HashSet<string> rendererBoundMeshKeys, out string reason)
        {
            reason = "unknown";
            if (item.Asset is not Mesh mesh)
            {
                reason = "not_mesh";
                return false;
            }

            if (mesh.m_VertexCount < 24 || mesh.m_Vertices == null || mesh.m_Vertices.Length == 0 || mesh.m_Indices == null || mesh.m_Indices.Count < 3)
            {
                reason = "too_small_or_empty";
                return false;
            }

            var container = (item.Container ?? string.Empty).Trim();
            var text = string.Join("/", item.Text, container, item.SourceFile?.originalPath, item.SourceFile?.fileName)
                .Replace('\\', '/');
            if (DeprecatedLibraryPathPattern.IsMatch(text))
            {
                reason = "deprecated";
                return false;
            }
            if (StaticMeshRejectPathPattern.IsMatch(text))
            {
                reason = "low_value_static_mesh";
                return false;
            }
            if (UnityDefaultPrimitivePathPattern.IsMatch(text))
            {
                reason = "unity_default_primitive";
                return false;
            }

            var hasContainer = !string.IsNullOrWhiteSpace(container) && !int.TryParse(container, out _);
            var hasStaticSignal = StaticMeshLibraryPathPattern.IsMatch(text);
            var hasRendererUsage = rendererBoundMeshKeys.Contains(GetSourceObjectKey(item));
            if (!hasStaticSignal && !hasRendererUsage)
            {
                reason = "no_static_library_path_signal";
                return false;
            }

            reason = hasRendererUsage
                ? "renderer_bound_static_mesh"
                : hasContainer ? "container_static_mesh" : "source_static_mesh";
            return true;
        }

        private static HashSet<string> GetRendererBoundStaticMeshKeys()
        {
            var indexPath = SQLiteSourceIndexRuntime.CurrentLoadResult?.DatabasePath;
            if (string.IsNullOrWhiteSpace(indexPath) || !File.Exists(indexPath))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            if (
                _rendererBoundStaticMeshKeys != null
                && string.Equals(_rendererBoundStaticMeshIndexPath, indexPath, StringComparison.OrdinalIgnoreCase)
            )
            {
                return _rendererBoundStaticMeshKeys;
            }

            _rendererBoundStaticMeshIndexPath = indexPath;
            _rendererBoundStaticMeshKeys = QueryRendererBoundStaticMeshKeys(indexPath);
            if (_rendererBoundStaticMeshKeys.Count > 0)
            {
                Logger.Info($"SQLite source index identified {_rendererBoundStaticMeshKeys.Count} renderer-used Mesh asset(s) for StaticMeshPrimary promotion.");
            }
            return _rendererBoundStaticMeshKeys;
        }

        private static HashSet<string> QueryRendererBoundStaticMeshKeys(string indexPath)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                SQLitePCL.Batteries_V2.Init();
                using var connection = new SqliteConnection($"Data Source={indexPath};Mode=ReadOnly");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
WITH direct_meshes AS (
    SELECT mesh.to_file AS mesh_file, mesh.to_path_id AS mesh_path_id
    FROM source_relations mesh
    WHERE mesh.relation = 'skinnedMeshRenderer.mesh'
),
mesh_filters AS (
    SELECT mf.from_file AS mf_file, mf.from_path_id AS mf_path_id, mf.to_file AS mesh_file, mf.to_path_id AS mesh_path_id
    FROM source_relations mf
    WHERE mf.relation = 'meshFilter.mesh'
),
mesh_game_objects AS (
    SELECT mf.mesh_file, mf.mesh_path_id, go.to_file AS go_file, go.to_path_id AS go_path_id
    FROM mesh_filters mf
    JOIN source_relations go
      ON go.relation = 'component.gameObject'
     AND go.from_file = mf.mf_file
     AND go.from_path_id = mf.mf_path_id
),
static_meshes AS (
    SELECT mgo.mesh_file, mgo.mesh_path_id
    FROM mesh_game_objects mgo
    JOIN source_relations renderer
      ON renderer.relation = 'component.gameObject'
     AND renderer.to_file = mgo.go_file
     AND renderer.to_path_id = mgo.go_path_id
     AND renderer.from_type IN ('MeshRenderer', 'SkinnedMeshRenderer')
)
SELECT DISTINCT mesh_file, mesh_path_id FROM direct_meshes
UNION
SELECT DISTINCT mesh_file, mesh_path_id FROM static_meshes;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(GetSourceObjectKey(reader.GetString(0), reader.GetInt64(1)));
                }
            }
            catch (Exception e) when (e is IOException || e is SqliteException || e is InvalidDataException)
            {
                Logger.Warning($"Unable to query renderer/material-bound StaticMesh keys from SQLite source index: {e.Message}");
            }

            return result;
        }

        private static string GetSourceObjectKey(AssetItem item)
        {
            if (item?.Asset is AnimeStudio.Object obj)
            {
                return GetSourceObjectKey(obj.assetsFile?.fileName ?? item.SourceFile?.fileName ?? string.Empty, item.m_PathID);
            }
            return GetSourceObjectKey(item?.SourceFile?.fileName ?? string.Empty, item?.m_PathID ?? 0);
        }

        private static string GetSourceObjectKey(string serializedFile, long pathId)
        {
            return $"{serializedFile ?? string.Empty}\u001f{pathId}";
        }

        private static void EnsureStaticMeshDuplicateNamesAreStable(List<AssetItem> selected)
        {
            foreach (var group in selected
                .GroupBy(
                    item => $"{GetStaticMeshLibrarySubPath(item)}\u001f{item.Text ?? string.Empty}",
                    StringComparer.OrdinalIgnoreCase
                )
                .Where(group => group.Count() > 1))
            {
                foreach (var item in group)
                {
                    item.Text = $"{item.Text}_{GetShortPathId(item.m_PathID)}";
                }
            }
        }

        private static void EnsureStaticMeshDisplayName(AssetItem item)
        {
            if (!IsAutoGeneratedAssetName(item.Text, item.TypeString))
            {
                return;
            }

            var source = item.SourceFile?.originalPath ?? item.SourceFile?.fileName ?? "source";
            var stem = Path.GetFileNameWithoutExtension(source);
            if (string.IsNullOrWhiteSpace(stem))
            {
                stem = "static_mesh";
            }

            var pathId = unchecked((ulong)item.m_PathID).ToString("X16", CultureInfo.InvariantCulture);
            item.Text = $"{stem}_Mesh_{pathId}";
        }

        private static string GetShortPathId(long pathId)
        {
            return unchecked((ulong)pathId).ToString("X16", CultureInfo.InvariantCulture)[^8..];
        }

        private static bool IsAutoGeneratedAssetName(string name, string typeString)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return true;
            }

            return Regex.IsMatch(
                name.Trim(),
                $"^{Regex.Escape(typeString ?? string.Empty)}#\\d+$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            );
        }

        private static string InferLibraryResourceKind(string name, string container, string source)
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

            if (IncludeVfx && VfxLibrarySignalPattern.IsMatch(signalText))
            {
                return "VFX";
            }
            if (Regex.IsMatch(text, @"(^|/)character/(pc|player)|(^|/)characters?(/|$)|(^|/)characterprefabs?(/|$)|(^|/)(pc|player)(/|$)"))
            {
                return "Character";
            }
            if (Regex.IsMatch(text, @"(^|/)actor_visual_parts?(/|$)"))
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
            if (HasToken(text, "soldier|warrior|worker|settler|archer|slinger|skirmisher|spearman|pikeman|axeman|swordsman|maceman|legionary|hastatus|hoplite|phalangite|conscript|raider|warlord|disciple|cavalry|horseman|javelin(?:eer)?|clubthrower|peltast"))
            {
                return "Unit";
            }
            if (HasToken(text, "horse|deer|bear|camel|elephant|mammoth|boar|wolf|dog|cat|bird|fish"))
            {
                return "Animal";
            }
            if (HasToken(text, "tank|ship|boat|submarine|carrier|frigate|corvette|helicopter|fighter|aircraft|artillery|cannon|chariot|trireme|bireme|dromon|catapult|mangonel|ballista|onager|siegetower|ram"))
            {
                return "Vehicle";
            }
            if (Regex.IsMatch(text, @"(^|/)(accessor|accessories|hat|hats|hair|weapon|weapons|shield|shields)(/|$)"))
            {
                return "Accessory";
            }
            if (Regex.IsMatch(text, @"(^|/)stage|court|scene|map"))
            {
                return "Stage";
            }
            if (Regex.IsMatch(text, @"(^|/)(building|buildings|structure|structures|wall|walls|floor|floors|roof|roofs|house|houses|castle|pieces)(/|$)|(^|/)gameelements/pieces(/|$)"))
            {
                return "Buildings";
            }
            if (HasToken(text, "building|buildings|structure|structures|wall|walls|floor|floors|roof|roofs|house|houses|castle|tower|gate|bridge|temple|shrine|stupa|church|cathedral|mosque|palace|fort|fortress|barracks|stable|market|farm|mill|lumbermill|mine|quarry|harbor|port|dock|scaffold|ruin|ruins|hovel|hut|amphitheater|ampitheater|oracle"))
            {
                return "Buildings";
            }
            if (Regex.IsMatch(text, @"(^|/)(terrain|landscape|surface|ground|levelbuild|levelbuildelements|environment|world|locations|rooms|nature|vegetation|foliage|tree|trees|rock|rocks)(/|$)"))
            {
                return "Environment";
            }
            if (HasToken(text, "terrain|landscape|surface|ground|environment|world|nature|vegetation|foliage|tree|trees|rock|rocks|stone|stones|cliff|mountain|hill|water|river|lake|ocean|sea|beach|grass|bush|plant|flower|forest|wood|woods|road|path"))
            {
                return "Environment";
            }
            if (Regex.IsMatch(text, @"(^|/)ball|basketball"))
            {
                return "Ball";
            }
            if (Regex.IsMatch(text, @"(^|/)trophy|prop|props|object|item|weapon"))
            {
                return "Prop";
            }
            if (HasToken(text, "prop|props|object|item|trophy|crate|box|barrel|chair|table|bench|door|cart|chest|sign|banner|flag|lamp|lantern|torch|statue|vase|jar|pot|bottle|book|scroll|coin"))
            {
                return "Prop";
            }
            if (Regex.IsMatch(text, @"(^|/)animation|\.anim$"))
            {
                return "Animation";
            }
            return "Unknown";
        }

        private static void ExportSeparateAnimationClips(string savePath)
        {
            var animations = exportableAssets.Where(x => x.Asset is AnimationClip).ToList();
            if (CliExportOptions.ExportSeparateAnimations && animations.Count > 0)
            {
                ExportAssets(savePath, animations, AssetGroupOption.ByLibrary, ExportType.Convert);
            }
            else if (animations.Count > 0)
            {
                Logger.Info("Animation clips were parsed but not written because --animation_package is Embedded.");
            }
        }

        public static void ExportSplitObjects(string savePath, AssetGroupOption assetGroupOption)
        {
            var animations = GetAnimationListForMode();
            var models = exportableAssets.Where(x => x.Asset is GameObject).ToList();
            ExportModelAssets(savePath, models, assetGroupOption, animations);
        }

        public static void ExportAnimators(string savePath, AssetGroupOption assetGroupOption)
        {
            var animations = GetAnimationListForMode();
            var animators = exportableAssets.Where(x => x.Asset is Animator).ToList();
            ExportModelAssets(savePath, animators, assetGroupOption, animations);
            ExportSeparateAnimationClips(savePath);
            GenerateLibraryIndexes(savePath);
        }

        private static List<AssetItem> GetAnimationListForMode()
        {
            return CliExportOptions.ExportEmbeddedAnimations && FbxAnimationMode == FbxAnimationMode.All
                ? exportableAssets.Where(x => x.Asset is AnimationClip).ToList()
                : null;
        }

        private sealed class ExportRunStats
        {
            private const int MaxSamples = 64;

            public DateTime? StartedAtUtc { get; set; }
            public DateTime? FinishedAtUtc { get; set; }
            public string Mode { get; set; }
            public int ModelCandidateCount { get; set; }
            public int AnimationCandidateCount { get; set; }
            public int ExportedModels { get; set; }
            public int SkippedModels { get; private set; }
            public int FailedModels { get; private set; }
            public Dictionary<string, int> SkippedByReason { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, int> FailedByReason { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<object> SkippedSamples { get; } = new();
            public List<object> FailedSamples { get; } = new();

            public void RecordSkippedModel(string reason, AssetItem asset)
            {
                SkippedModels++;
                Increment(SkippedByReason, reason);
                AddSample(SkippedSamples, reason, asset, null);
            }

            public void RecordFailedModel(string reason, AssetItem asset, Exception exception)
            {
                FailedModels++;
                Increment(FailedByReason, reason);
                AddSample(FailedSamples, reason, asset, exception);
            }

            private static void Increment(Dictionary<string, int> map, string key)
            {
                key = string.IsNullOrWhiteSpace(key) ? "unknown" : key;
                map.TryGetValue(key, out var count);
                map[key] = count + 1;
            }

            private static void AddSample(List<object> samples, string reason, AssetItem asset, Exception exception)
            {
                if (samples.Count >= MaxSamples)
                {
                    return;
                }

                samples.Add(new
                {
                    reason,
                    type = asset?.TypeString,
                    name = asset?.Text,
                    source = asset?.SourceFile?.originalPath ?? asset?.SourceFile?.fullName ?? asset?.SourceFile?.fileName,
                    pathId = asset?.m_PathID,
                    exception = exception?.GetType().Name,
                    message = exception?.Message,
                });
            }
        }

        private sealed class VfxLibraryEntry
        {
            public string Name { get; init; }
            public string SourceType { get; init; }
            public string Source { get; init; }
            public string SerializedFile { get; init; }
            public long PathId { get; init; }
            public string ResourceKind { get; init; } = "VFX";
            public string Category { get; init; }
            public string Confidence { get; init; }
            public string OutputDirectory { get; set; }
            public string ModelPreview { get; set; }
            public int OccurrenceCount { get; set; } = 1;
            public List<object> SourceSamples { get; init; } = new();
            public List<string> Signals { get; init; } = new();
            public List<string> ComponentKeys { get; init; } = new();
            public List<object> Components { get; init; } = new();
            public List<object> MaterialRefs { get; init; } = new();
            public List<object> TextureRefs { get; init; } = new();
            public List<object> TexturePreviews { get; init; } = new();
            public int TexturePreviewResolveMisses { get; set; }
            public int TexturePreviewWriteFailures { get; set; }
            public List<object> MeshRefs { get; init; } = new();
            public JObject PreviewHints { get; set; } = new();
            public List<string> Notes { get; init; } = new();
        }

        private static void GenerateVfxLibrary(string savePath)
        {
            if (string.IsNullOrWhiteSpace(savePath))
            {
                return;
            }

            using var profile = ProfileLogger.Measure("generate_vfx_library", new Dictionary<string, object>
            {
                ["savePath"] = savePath,
                ["sourceIndex"] = SQLiteSourceIndexRuntime.CurrentLoadResult?.DatabasePath,
            });

            var entries = new List<VfxLibraryEntry>();
            entries.AddRange(QuerySourceIndexVfxEntries());
            entries.AddRange(CollectCatalogBackedVfxEntries(savePath, entries));
            AttachVfxModelPreviews(savePath, entries);
            entries = entries
                .GroupBy(GetVfxLogicalKey, StringComparer.OrdinalIgnoreCase)
                .Select(MergeVfxEntries)
                .OrderBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var catalogPath = Path.Combine(savePath, "asset_catalog.jsonl");
            RewriteCatalogWithoutKind(catalogPath, "VFX");
            if (entries.Count == 0)
            {
                return;
            }

            var vfxRoot = Path.Combine(savePath, "VFX");
            Directory.CreateDirectory(vfxRoot);

            var exported = 0;
            var categoryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var exportedTextureCatalog = LoadExportedTextureCatalog(catalogPath);
            foreach (var entry in entries)
            {
                entry.OutputDirectory = GetVfxOutputDirectory(savePath, entry);
                Directory.CreateDirectory(entry.OutputDirectory);
                AttachVfxTexturePreviewFiles(entry, exportedTextureCatalog);
                WriteVfxEntryFiles(entry);

                IncrementCount(categoryCounts, entry.Category);
                File.AppendAllText(catalogPath, JsonConvert.SerializeObject(BuildVfxCatalogEntry(entry)) + Environment.NewLine);
                exported++;
            }

            WriteVfxLibrarySummary(vfxRoot, entries, categoryCounts);
            Logger.Info($"Generated VFX library metadata: {entries.Count} VFX candidate(s), {exported} new catalog entrie(s).");
        }

        private static IEnumerable<VfxLibraryEntry> QuerySourceIndexVfxEntries()
        {
            var indexPath = SQLiteSourceIndexRuntime.CurrentLoadResult?.DatabasePath;
            if (string.IsNullOrWhiteSpace(indexPath) || !File.Exists(indexPath))
            {
                yield break;
            }

            List<VfxLibraryEntry> result;
            try
            {
                result = QuerySourceIndexVfxEntries(indexPath);
            }
            catch (Exception e) when (e is IOException || e is SqliteException || e is InvalidDataException)
            {
                Logger.Warning($"Unable to query VFX candidates from SQLite source index: {e.Message}");
                yield break;
            }

            foreach (var entry in result)
            {
                yield return entry;
            }
        }

        private static List<VfxLibraryEntry> QuerySourceIndexVfxEntries(string indexPath)
        {
            SQLitePCL.Batteries_V2.Init();
            using var connection = new SqliteConnection($"Data Source={indexPath};Mode=ReadOnly");
            connection.Open();
            var componentClassIds = GetVfxComponentClassIds();
            var componentClassIdList = string.Join(",", componentClassIds.Select(x => ((int)x).ToString(CultureInfo.InvariantCulture)));
            var entries = new Dictionary<string, VfxLibraryEntry>(StringComparer.OrdinalIgnoreCase);

            using (var command = connection.CreateCommand())
            {
                command.CommandText = $@"
SELECT
    go.serialized_file,
    go.source_path,
    go.path_id,
    go.name,
    comp.type,
    comp.class_id,
    comp.path_id,
    comp.name,
    comp.source_path
FROM source_relations r
JOIN source_objects go
  ON go.serialized_file = r.from_file
 AND go.path_id = r.from_path_id
JOIN source_objects comp
  ON comp.serialized_file = r.to_file
 AND comp.path_id = r.to_path_id
WHERE r.relation = 'gameObject.component'
  AND comp.class_id IN ({componentClassIdList});";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var serializedFile = ReadString(reader, 0);
                    var source = ReadString(reader, 1);
                    var pathId = ReadInt64(reader, 2);
                    var name = FirstNonEmpty(ReadString(reader, 3), Path.GetFileNameWithoutExtension(source), $"VFX_{GetShortPathId(pathId)}");
                    var componentType = ReadString(reader, 4);
                    var componentClassId = ReadInt64(reader, 5);
                    var componentPathId = ReadInt64(reader, 6);
                    var componentName = ReadString(reader, 7);
                    var componentSource = ReadString(reader, 8);
                    var key = GetSourceObjectKey(serializedFile, pathId);
                    if (!entries.TryGetValue(key, out var entry))
                    {
                        entry = new VfxLibraryEntry
                        {
                            Name = name,
                            SourceType = "GameObject",
                            Source = source,
                            SerializedFile = serializedFile,
                            PathId = pathId,
                            Category = ClassifyVfxCategory(name, source, componentType),
                            Confidence = "explicit_unity_vfx_component",
                            Signals = new List<string> { "Unity GameObject has VFX component" },
                            Notes = new List<string>
                            {
                                "Unity ParticleSystem/VisualEffect/LineRenderer/TrailRenderer component was detected from the source SQLite index.",
                                "This v1 VFX Library preserves structure and component evidence. ParticleSystem module parameters are not fully decoded yet, so JSON is metadata/diagnostic, not a standalone particle runtime file.",
                            },
                        };
                        entries[key] = entry;
                    }

                    entry.Components.Add(new
                    {
                        type = componentType,
                        classId = componentClassId,
                        name = componentName,
                        source = componentSource,
                        pathId = componentPathId,
                    });
                    entry.ComponentKeys.Add(GetSourceObjectKey(serializedFile, componentPathId));
                    entry.Signals.Add($"component:{componentType}");
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = $@"
SELECT serialized_file, source_path, path_id, type, class_id, name
FROM source_objects
WHERE class_id IN ({componentClassIdList});";
                var ownedComponentKeys = entries.Values
                    .SelectMany(x => x.ComponentKeys)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var serializedFile = ReadString(reader, 0);
                    var source = ReadString(reader, 1);
                    var pathId = ReadInt64(reader, 2);
                    var type = ReadString(reader, 3);
                    var name = FirstNonEmpty(ReadString(reader, 5), type, $"VFX_{GetShortPathId(pathId)}");
                    var key = GetSourceObjectKey(serializedFile, pathId);
                    if (entries.ContainsKey(key) || ownedComponentKeys.Contains(key))
                    {
                        continue;
                    }

                    entries[key] = new VfxLibraryEntry
                    {
                        Name = name,
                        SourceType = type,
                        Source = source,
                        SerializedFile = serializedFile,
                        PathId = pathId,
                        Category = ClassifyVfxCategory(name, source, type),
                        Confidence = "explicit_unity_vfx_object",
                        Signals = new List<string> { $"object:{type}" },
                        Components = new List<object>
                        {
                            new
                            {
                                type,
                                classId = ReadInt64(reader, 4),
                                name,
                                source,
                                pathId,
                            },
                        },
                        Notes = new List<string>
                        {
                            "Unity VFX-related object was detected from the source SQLite index without a resolved GameObject owner.",
                            "This entry is kept because default Library is a complete browsable asset library; unresolved ownership is reported instead of silently dropping the asset.",
                        },
                    };
                }
            }

            AttachVfxRendererRelations(connection, entries);
            AttachVfxTextureRelations(connection, entries);
            AttachVfxMetadata(connection, entries);
            return entries.Values.ToList();
        }

        private static void AttachVfxRendererRelations(SqliteConnection connection, Dictionary<string, VfxLibraryEntry> entries)
        {
            if (entries.Count == 0)
            {
                return;
            }

            var componentOwners = new Dictionary<string, List<VfxLibraryEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries.Values)
            {
                foreach (var componentKey in entry.ComponentKeys.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!componentOwners.TryGetValue(componentKey, out var owners))
                    {
                        owners = new List<VfxLibraryEntry>();
                        componentOwners[componentKey] = owners;
                    }
                    owners.Add(entry);
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT
    r.from_file,
    r.from_path_id,
    r.relation,
    r.to_file,
    r.to_path_id,
    r.to_type_hint,
    target.type,
    target.name,
    target.source_path
FROM source_relations r
LEFT JOIN source_objects target
  ON target.serialized_file = r.to_file
 AND target.path_id = r.to_path_id
WHERE r.relation IN ('renderer.material', 'skinnedMeshRenderer.mesh', 'meshFilter.mesh');";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var componentKey = GetSourceObjectKey(ReadString(reader, 0), ReadInt64(reader, 1));
                    if (!componentOwners.TryGetValue(componentKey, out var owners))
                    {
                        continue;
                    }

                    var relation = ReadString(reader, 2);
                    var reference = new
                    {
                        relation,
                        file = ReadString(reader, 3),
                        pathId = ReadInt64(reader, 4),
                        typeHint = ReadString(reader, 5),
                        type = ReadString(reader, 6),
                        name = ReadString(reader, 7),
                        source = ReadString(reader, 8),
                    };

                    foreach (var owner in owners)
                    {
                        if (string.Equals(relation, "renderer.material", StringComparison.OrdinalIgnoreCase))
                        {
                            owner.MaterialRefs.Add(reference);
                            owner.Signals.Add("renderer.material");
                        }
                        else
                        {
                            owner.MeshRefs.Add(reference);
                            owner.Signals.Add(relation);
                        }
                    }
                }
            }

            foreach (var entry in entries.Values)
            {
                if (entry.MaterialRefs.Count == 0
                    && entry.Components.Any(x => x.ToString().IndexOf("Renderer", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    entry.Notes.Add("No renderer.material relation was found for this VFX entry. Rebuild unity_source_index.db with a version that supports VFX renderer lightweight relations, or treat this as material-unresolved metadata.");
                }
            }
        }

        private static void AttachVfxTextureRelations(SqliteConnection connection, Dictionary<string, VfxLibraryEntry> entries)
        {
            if (entries.Count == 0)
            {
                return;
            }

            var componentOwners = new Dictionary<string, List<VfxLibraryEntry>>(StringComparer.OrdinalIgnoreCase);
            var materialOwners = new Dictionary<string, List<VfxLibraryEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries.Values)
            {
                foreach (var componentKey in entry.ComponentKeys.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    AddOwner(componentOwners, componentKey, entry);
                }

                foreach (var materialRef in entry.MaterialRefs)
                {
                    var material = JObject.FromObject(materialRef);
                    var materialKey = GetSourceObjectKey((string)material["file"] ?? string.Empty, (long?)material["pathId"] ?? 0);
                    AddOwner(materialOwners, materialKey, entry);
                }
            }

            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT
    r.from_file,
    r.from_path_id,
    r.relation,
    r.to_file,
    r.to_path_id,
    r.to_type_hint,
    target.type,
    target.name,
    target.source_path,
    r.raw_json
FROM source_relations r
LEFT JOIN source_objects target
  ON target.serialized_file = r.to_file
 AND target.path_id = r.to_path_id
WHERE r.relation IN ('material.texture', 'vfx.texture');";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var fromKey = GetSourceObjectKey(ReadString(reader, 0), ReadInt64(reader, 1));
                var relation = ReadString(reader, 2);
                var owners = string.Equals(relation, "material.texture", StringComparison.OrdinalIgnoreCase)
                    ? materialOwners.TryGetValue(fromKey, out var materialEntryOwners) ? materialEntryOwners : null
                    : componentOwners.TryGetValue(fromKey, out var componentEntryOwners) ? componentEntryOwners : null;
                if (owners == null || owners.Count == 0)
                {
                    continue;
                }

                var raw = TryParseJObject(ReadString(reader, 9));
                var reference = new
                {
                    relation,
                    file = ReadString(reader, 3),
                    pathId = ReadInt64(reader, 4),
                    typeHint = ReadString(reader, 5),
                    type = ReadString(reader, 6),
                    name = ReadString(reader, 7),
                    source = ReadString(reader, 8),
                    slot = (string)raw?["details"]?["slot"],
                    path = (string)raw?["details"]?["path"],
                };

                foreach (var owner in owners)
                {
                    AddDistinctObjects(owner.TextureRefs, new[] { reference }, GetReferenceLogicalKey);
                    owner.Signals.Add(relation);
                }
            }

            foreach (var entry in entries.Values)
            {
                if (entry.TextureRefs.Count > 0)
                {
                    entry.Notes.Add("Texture references were resolved from VFX renderer materials or direct VFX texture PPtrs. Browser previews may use them as visual evidence; full shader/atlas sampling is still approximate.");
                }
            }

            static void AddOwner(Dictionary<string, List<VfxLibraryEntry>> map, string key, VfxLibraryEntry entry)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    return;
                }
                if (!map.TryGetValue(key, out var owners))
                {
                    owners = new List<VfxLibraryEntry>();
                    map[key] = owners;
                }
                owners.Add(entry);
            }

            static JObject TryParseJObject(string rawJson)
            {
                if (string.IsNullOrWhiteSpace(rawJson))
                {
                    return null;
                }
                try
                {
                    return JObject.Parse(rawJson);
                }
                catch
                {
                    return null;
                }
            }
        }

        private static void AttachVfxMetadata(SqliteConnection connection, Dictionary<string, VfxLibraryEntry> entries)
        {
            if (entries.Count == 0)
            {
                return;
            }

            var componentOwners = new Dictionary<string, List<VfxLibraryEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries.Values)
            {
                foreach (var componentKey in entry.ComponentKeys.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!componentOwners.TryGetValue(componentKey, out var owners))
                    {
                        owners = new List<VfxLibraryEntry>();
                        componentOwners[componentKey] = owners;
                    }
                    owners.Add(entry);
                }
            }

            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT from_file, from_path_id, raw_json
FROM source_relations
WHERE relation = 'vfx.metadata';";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var componentKey = GetSourceObjectKey(ReadString(reader, 0), ReadInt64(reader, 1));
                if (!componentOwners.TryGetValue(componentKey, out var owners))
                {
                    continue;
                }

                var rawJson = ReadString(reader, 2);
                if (string.IsNullOrWhiteSpace(rawJson))
                {
                    continue;
                }

                JObject raw;
                try
                {
                    raw = JObject.Parse(rawJson);
                }
                catch
                {
                    continue;
                }

                var hints = raw["details"]?["hints"] as JObject;
                if (hints == null || !hints.Properties().Any())
                {
                    continue;
                }

                foreach (var owner in owners)
                {
                    MergeVfxPreviewHints(owner.PreviewHints, hints);
                    owner.Signals.Add("vfx.metadata");
                }
            }

            foreach (var entry in entries.Values)
            {
                if (entry.PreviewHints.Properties().Any())
                {
                    entry.Notes.Add("ParticleSystem/Renderer preview hints were decoded from Unity TypeTree. They are used for approximate non-Unity-runtime preview and may be partial across Unity versions.");
                }
            }
        }

        private static void MergeVfxPreviewHints(JObject target, JObject source)
        {
            foreach (var property in source.Properties())
            {
                if (!target.ContainsKey(property.Name))
                {
                    target[property.Name] = property.Value.DeepClone();
                    continue;
                }

                if (!JToken.DeepEquals(target[property.Name], property.Value))
                {
                    var arrayName = property.Name + "#variants";
                    var array = target[arrayName] as JArray;
                    if (array == null)
                    {
                        array = new JArray(target[property.Name]?.DeepClone());
                        target[arrayName] = array;
                    }
                    if (!array.Any(x => JToken.DeepEquals(x, property.Value)))
                    {
                        array.Add(property.Value.DeepClone());
                    }
                }
            }
        }

        private static IEnumerable<VfxLibraryEntry> CollectCatalogBackedVfxEntries(string savePath, IEnumerable<VfxLibraryEntry> existingEntries)
        {
            var catalogPath = Path.Combine(savePath, "asset_catalog.jsonl");
            if (!File.Exists(catalogPath))
            {
                yield break;
            }

            var knownKeys = existingEntries.Select(GetVfxEntryKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var obj in LoadCatalogEntries(catalogPath))
            {
                if (!string.Equals((string)obj["kind"], "Model", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals((string)obj["resourceKind"], "VFX", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var source = (string)obj["source"];
                var pathId = (long?)obj["pathId"] ?? 0;
                var key = GetSourceObjectKey((string)obj["sourceFile"] ?? string.Empty, pathId);
                if (!knownKeys.Add(key))
                {
                    continue;
                }

                yield return new VfxLibraryEntry
                {
                    Name = FirstNonEmpty((string)obj["name"], Path.GetFileNameWithoutExtension((string)obj["output"]), $"VFX_{GetShortPathId(pathId)}"),
                    SourceType = (string)obj["sourceType"] ?? "Model",
                    Source = source,
                    SerializedFile = (string)obj["sourceFile"] ?? string.Empty,
                    PathId = pathId,
                    Category = ClassifyVfxCategory((string)obj["name"], (string)obj["container"], (string)obj["source"]),
                    Confidence = "mesh_vfx_name_signal",
                    ModelPreview = (string)obj["output"],
                    Signals = new List<string> { "exported model resourceKind=VFX" },
                    Notes = new List<string>
                    {
                        "This is a mesh/material VFX asset exported through the normal model pipeline.",
                        "It may be a slash, projectile, impact, beam, trail mesh, or shader-driven visual effect. Runtime shader/particle behavior may need a dedicated previewer.",
                    },
                };
            }
        }

        private static void AttachVfxModelPreviews(string savePath, IEnumerable<VfxLibraryEntry> entries)
        {
            var catalogPath = Path.Combine(savePath, "asset_catalog.jsonl");
            if (!File.Exists(catalogPath))
            {
                return;
            }

            var modelOutputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var obj in LoadCatalogEntries(catalogPath))
            {
                if (!string.Equals((string)obj["kind"], "Model", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var output = (string)obj["output"];
                if (string.IsNullOrWhiteSpace(output) || !File.Exists(output))
                {
                    continue;
                }

                var key = GetSourceObjectKey((string)obj["sourceFile"] ?? string.Empty, (long?)obj["pathId"] ?? 0);
                if (!string.IsNullOrWhiteSpace(key) && !modelOutputs.ContainsKey(key))
                {
                    modelOutputs[key] = output;
                }
            }

            if (modelOutputs.Count == 0)
            {
                return;
            }

            foreach (var entry in entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.ModelPreview))
                {
                    continue;
                }

                foreach (var meshRef in entry.MeshRefs)
                {
                    var refObj = JObject.FromObject(meshRef);
                    var key = GetSourceObjectKey((string)refObj["file"] ?? string.Empty, (long?)refObj["pathId"] ?? 0);
                    if (modelOutputs.TryGetValue(key, out var output))
                    {
                        entry.ModelPreview = output;
                        entry.Signals.Add("mesh.preview");
                        break;
                    }
                }
            }
        }

        private static Dictionary<string, JObject> LoadExportedTextureCatalog(string catalogPath)
        {
            var result = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(catalogPath))
            {
                return result;
            }

            foreach (var obj in LoadCatalogEntries(catalogPath))
            {
                var kind = (string)obj["kind"] ?? string.Empty;
                var sourceType = (string)obj["sourceType"] ?? string.Empty;
                if (!kind.Contains("Texture", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(sourceType, nameof(Texture2D), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var output = (string)obj["output"];
                if (string.IsNullOrWhiteSpace(output) || !File.Exists(output))
                {
                    continue;
                }

                var pathId = (long?)obj["pathId"] ?? 0;
                foreach (var key in GetPossibleSourceObjectKeys((string)obj["source"], pathId))
                {
                    result.TryAdd(key, obj);
                }
            }

            return result;
        }

        private static void AttachVfxTexturePreviewFiles(VfxLibraryEntry entry, Dictionary<string, JObject> exportedTextureCatalog)
        {
            if (entry.TextureRefs.Count == 0)
            {
                return;
            }

            var textureDirectory = Path.Combine(entry.OutputDirectory, "Textures");
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var textureRef in entry.TextureRefs)
            {
                var refObj = JObject.FromObject(textureRef);
                var file = (string)refObj["file"] ?? string.Empty;
                var pathId = (long?)refObj["pathId"] ?? 0;
                if (pathId == 0)
                {
                    continue;
                }

                string texturePath = null;
                JObject catalog = null;
                foreach (var key in GetPossibleSourceObjectKeys(file, pathId))
                {
                    if (exportedTextureCatalog.TryGetValue(key, out catalog))
                    {
                        texturePath = (string)catalog["output"];
                        break;
                    }
                }

                var source = "asset_catalog";
                if (string.IsNullOrWhiteSpace(texturePath) || !File.Exists(texturePath))
                {
                    var texture = FindLoadedTexture2D(file, pathId);
                    if (texture == null)
                    {
                        entry.TexturePreviewResolveMisses++;
                        continue;
                    }

                    Directory.CreateDirectory(textureDirectory);
                    texturePath = Path.Combine(textureDirectory, BuildVfxTexturePreviewFileName(texture, pathId));
                    source = "loaded_texture2d";
                    if (!File.Exists(texturePath) && !TryWriteVfxTexturePreview(texture, texturePath, entry, refObj))
                    {
                        entry.TexturePreviewWriteFailures++;
                        continue;
                    }
                }

                if (!File.Exists(texturePath) || !written.Add(texturePath))
                {
                    continue;
                }

                entry.TexturePreviews.Add(new
                {
                    file,
                    pathId,
                    name = (string)refObj["name"] ?? (string)catalog?["name"] ?? Path.GetFileNameWithoutExtension(texturePath),
                    slot = (string)refObj["slot"],
                    relation = (string)refObj["relation"],
                    source,
                    output = texturePath,
                    relativePath = Path.GetRelativePath(entry.OutputDirectory, texturePath).Replace('\\', '/'),
                });
            }

            if (entry.TexturePreviews.Count > 0)
            {
                entry.Signals.Add("texture.preview");
                entry.Notes.Add("Texture preview PNGs were attached for Browser billboard/mesh-particle approximation. This improves browsing, but does not fully reproduce Unity shader flipbook, tint, UV scroll, or VFX Graph behavior.");
            }
            else
            {
                entry.Notes.Add("Texture references exist, but no preview PNG could be resolved in the current export. Re-export with complete dependency input/source_index if Browser needs texture-backed VFX thumbnails.");
            }
        }

        public static void ExportVfxTexturePreviewCache(string savePath)
        {
            var indexPath = SQLiteSourceIndexRuntime.CurrentLoadResult?.DatabasePath;
            if (string.IsNullOrWhiteSpace(savePath)
                || string.IsNullOrWhiteSpace(indexPath)
                || !File.Exists(indexPath)
                || exportableAssets.Count == 0)
            {
                return;
            }

            var textureRefs = new List<(string File, long PathId, string Type, string Name, string Source)>();
            try
            {
                SQLitePCL.Batteries_V2.Init();
                using var connection = new SqliteConnection($"Data Source={indexPath};Mode=ReadOnly");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT DISTINCT
    r.to_file,
    r.to_path_id,
    COALESCE(target.type, r.to_type_hint, ''),
    COALESCE(target.name, ''),
    COALESCE(target.source_path, '')
FROM source_relations r
LEFT JOIN source_objects target
  ON target.serialized_file = r.to_file
 AND target.path_id = r.to_path_id
WHERE r.relation IN ('material.texture', 'vfx.texture')
  AND (target.type = 'Texture2D' OR r.to_type_hint LIKE '%Texture%');";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var file = ReadString(reader, 0);
                    var pathId = ReadInt64(reader, 1);
                    if (!string.IsNullOrWhiteSpace(file) && pathId != 0)
                    {
                        textureRefs.Add((
                            file,
                            pathId,
                            ReadString(reader, 2),
                            ReadString(reader, 3),
                            ReadString(reader, 4)));
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is SqliteException || e is InvalidDataException)
            {
                Logger.Debug($"Unable to query VFX texture preview cache refs: {e.Message}");
                return;
            }

            if (textureRefs.Count == 0)
            {
                return;
            }

            var exported = 0;
            var previewRoot = Path.Combine(savePath, "VFX", "_TexturePreviews");
            foreach (var textureRef in textureRefs)
            {
                var texture = FindLoadedTexture2D(textureRef.File, textureRef.PathId);
                if (texture == null)
                {
                    continue;
                }

                var sourceKey = SanitizeRelativePath(Path.GetFileName(textureRef.File));
                if (string.IsNullOrWhiteSpace(sourceKey))
                {
                    sourceKey = "UnknownSource";
                }

                var textureDirectory = Path.Combine(previewRoot, sourceKey);
                Directory.CreateDirectory(textureDirectory);
                var texturePath = Path.Combine(textureDirectory, BuildVfxTexturePreviewFileName(texture, textureRef.PathId));
                var catalogKey = GetSourceObjectKey(textureRef.File, textureRef.PathId);
                if (!File.Exists(texturePath))
                {
                    var tempEntry = new VfxLibraryEntry
                    {
                        Name = "VFX texture preview cache",
                        Source = textureRef.Source,
                        SerializedFile = textureRef.File,
                        PathId = textureRef.PathId,
                    };
                    var refObj = new JObject
                    {
                        ["relation"] = "vfx.texture.cache",
                    };
                    if (!TryWriteVfxTexturePreview(texture, texturePath, tempEntry, refObj))
                    {
                        continue;
                    }
                }

                if (VfxTexturePreviewCatalogKeys.Add(catalogKey))
                {
                    var catalogPath = Path.Combine(savePath, "asset_catalog.jsonl");
                    var entry = new
                    {
                        kind = "VfxTexturePreviewTexture",
                        resourceKind = "VFX",
                        exportedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                        name = FirstNonEmpty(texture.m_Name, textureRef.Name, $"Texture2D_{GetShortPathId(textureRef.PathId)}"),
                        sourceType = nameof(Texture2D),
                        source = textureRef.File,
                        actualSource = texture.assetsFile?.originalPath ?? texture.assetsFile?.fullName ?? texture.assetsFile?.fileName,
                        pathId = textureRef.PathId,
                        output = texturePath,
                        usage = "vfx_texture_preview_cache",
                    };
                    File.AppendAllText(catalogPath, JsonConvert.SerializeObject(entry) + Environment.NewLine, Encoding.UTF8);
                    exported++;
                }
            }

            if (exported > 0)
            {
                Logger.Info($"Cached {exported} VFX texture preview image(s) for Browser approximation.");
            }
        }

        private static IEnumerable<string> GetPossibleSourceObjectKeys(string source, long pathId)
        {
            if (pathId == 0)
            {
                yield break;
            }

            source ??= string.Empty;
            yield return GetSourceObjectKey(source, pathId);

            var fileName = Path.GetFileName(source);
            if (!string.IsNullOrWhiteSpace(fileName) && !string.Equals(fileName, source, StringComparison.OrdinalIgnoreCase))
            {
                yield return GetSourceObjectKey(fileName, pathId);
            }
        }

        private static Texture2D FindLoadedTexture2D(string sourceFile, long pathId)
        {
            var pathOnlyMatches = new HashSet<Texture2D>();

            foreach (var item in exportableAssets)
            {
                if (item.Asset is not Texture2D texture || item.m_PathID != pathId)
                {
                    continue;
                }

                if (SourceFileMatches(sourceFile, texture.assetsFile?.fileName, texture.assetsFile?.fullName, texture.assetsFile?.originalPath, item.SourceFile?.fileName, item.SourceFile?.fullName, item.SourceFile?.originalPath))
                {
                    return texture;
                }

                pathOnlyMatches.Add(texture);
            }

            foreach (var assetsFile in assetsManager.assetsFileList)
            {
                if (assetsFile.ObjectsDic.TryGetValue(pathId, out var obj) && obj is Texture2D texture)
                {
                    if (SourceFileMatches(sourceFile, assetsFile.fileName, assetsFile.fullName, assetsFile.originalPath))
                    {
                        return texture;
                    }

                    pathOnlyMatches.Add(texture);
                }
            }

            // AssetBundle outer file names and inner serialized names are not always identical.
            // PathIDs are normally unique enough within the currently loaded dependency closure;
            // use them as a conservative fallback only when there is exactly one candidate.
            return pathOnlyMatches.Count == 1 ? pathOnlyMatches.First() : null;
        }

        private static bool SourceFileMatches(string sourceFile, params string[] candidates)
        {
            sourceFile ??= string.Empty;
            var sourceFileName = Path.GetFileName(sourceFile);
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                var candidateFileName = Path.GetFileName(candidate);
                if (string.Equals(candidate, sourceFile, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(candidateFileName) && string.Equals(candidateFileName, sourceFile, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(sourceFileName) && string.Equals(candidateFileName, sourceFileName, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(sourceFileName) && candidate.Contains(sourceFileName, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(candidateFileName) && sourceFile.Contains(candidateFileName, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }

        private static string BuildVfxTexturePreviewFileName(Texture2D texture, long pathId)
        {
            var name = SanitizeRelativePath(texture.m_Name ?? "Texture");
            if (name.Length > 72)
            {
                name = name.Substring(0, 72);
            }
            return $"{name}_{GetShortPathId(pathId)}.png";
        }

        private static bool TryWriteVfxTexturePreview(Texture2D texture, string texturePath, VfxLibraryEntry entry, JObject textureRef)
        {
            try
            {
                var profileData = new Dictionary<string, object>
                {
                    ["vfx"] = entry.Name,
                    ["texture"] = texture.m_Name,
                    ["source"] = texture.assetsFile?.originalPath ?? texture.assetsFile?.fileName,
                    ["pathId"] = texture.m_PathID,
                    ["output"] = texturePath,
                    ["usage"] = "vfx_texture_preview",
                    ["relation"] = (string)textureRef["relation"],
                };
                using (ProfileLogger.Measure("vfx_texture_preview", profileData))
                using (var stream = texture.ConvertToStream(Properties.Settings.Default.convertType, true, ProfileLogger.Measure, profileData))
                {
                    if (stream == null)
                    {
                        return false;
                    }
                    using var file = File.OpenWrite(texturePath);
                    stream.WriteTo(file);
                    return true;
                }
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is NotSupportedException)
            {
                entry.Notes.Add($"Unable to export VFX texture preview `{texture.m_Name}` ({texture.m_PathID}): {e.GetType().Name}: {e.Message}");
                return false;
            }
        }

        private static VfxLibraryEntry MergeVfxEntries(IGrouping<string, VfxLibraryEntry> group)
        {
            var entries = group.ToList();
            var first = entries.First();
            first.OccurrenceCount = entries.Sum(x => Math.Max(1, x.OccurrenceCount));
            foreach (var entry in entries.Skip(1))
            {
                AddDistinct(first.Signals, entry.Signals);
                AddDistinct(first.ComponentKeys, entry.ComponentKeys);
                AddDistinctObjects(first.Components, entry.Components, GetObjectLogicalKey);
                AddDistinctObjects(first.MaterialRefs, entry.MaterialRefs, GetReferenceLogicalKey);
                AddDistinctObjects(first.TextureRefs, entry.TextureRefs, GetReferenceLogicalKey);
                AddDistinctObjects(first.TexturePreviews, entry.TexturePreviews, GetReferenceLogicalKey);
                AddDistinctObjects(first.MeshRefs, entry.MeshRefs, GetReferenceLogicalKey);
                first.TexturePreviewResolveMisses += entry.TexturePreviewResolveMisses;
                first.TexturePreviewWriteFailures += entry.TexturePreviewWriteFailures;
                MergeVfxPreviewHints(first.PreviewHints, entry.PreviewHints);
                AddDistinct(first.Notes, entry.Notes);
                if (string.IsNullOrWhiteSpace(first.ModelPreview) && !string.IsNullOrWhiteSpace(entry.ModelPreview))
                {
                    first.ModelPreview = entry.ModelPreview;
                }
            }

            foreach (var sample in entries
                .Select(x => new
                {
                    x.SourceType,
                    x.Source,
                    x.SerializedFile,
                    x.PathId,
                })
                .GroupBy(x => $"{x.SerializedFile}|{x.PathId}", StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .Take(16))
            {
                first.SourceSamples.Add(sample);
            }

            if (entries.Count > 1)
            {
                first.Signals.Add("deduplicated.logical_vfx");
                first.Notes.Add($"Merged {entries.Count} Unity VFX instance/component entries into one logical VFX asset. occurrenceCount keeps the original instance count.");
            }

            return first;
        }

        private static string GetVfxLogicalKey(VfxLibraryEntry entry)
        {
            var componentTypes = entry.Components
                .Select(GetObjectLogicalKey)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
            var materials = entry.MaterialRefs
                .Select(GetReferenceLogicalKey)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
            var textures = entry.TextureRefs
                .Select(GetReferenceLogicalKey)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
            var meshes = entry.MeshRefs
                .Select(GetReferenceLogicalKey)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

            var name = NormalizeVfxName(entry.Name);
            return string.Join("|", new[]
            {
                name,
                entry.Category ?? string.Empty,
                string.Join(",", componentTypes),
                string.Join(",", materials),
                string.Join(",", textures),
                string.Join(",", meshes),
                entry.ModelPreview ?? string.Empty,
            });
        }

        private static string NormalizeVfxName(string value)
        {
            value = string.IsNullOrWhiteSpace(value) ? "vfx" : value.Trim();
            value = Regex.Replace(value, @"\s*\(\d+\)$", "", RegexOptions.CultureInvariant);
            value = Regex.Replace(value, @"[_\-\s]*(?:instance|clone)\d*$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return value.ToLowerInvariant();
        }

        private static string GetObjectLogicalKey(object value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            var obj = JObject.FromObject(value);
            return string.Join(":", new[]
            {
                ((string)obj["type"] ?? string.Empty).ToLowerInvariant(),
                ((string)obj["name"] ?? string.Empty).ToLowerInvariant(),
            });
        }

        private static string GetReferenceLogicalKey(object value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            var obj = JObject.FromObject(value);
            var name = ((string)obj["name"] ?? string.Empty).Trim().ToLowerInvariant();
            var type = ((string)obj["type"] ?? (string)obj["typeHint"] ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(name))
            {
                return $"{type}:{name}";
            }

            return GetSourceObjectKey((string)obj["file"] ?? string.Empty, (long?)obj["pathId"] ?? 0);
        }

        private static void AddDistinct(List<string> target, IEnumerable<string> values)
        {
            var known = target.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var value in values.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                if (known.Add(value))
                {
                    target.Add(value);
                }
            }
        }

        private static void AddDistinctObjects(List<object> target, IEnumerable<object> values, Func<object, string> keySelector)
        {
            var known = target.Select(keySelector).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var value in values)
            {
                var key = keySelector(value);
                if (known.Add(key))
                {
                    target.Add(value);
                }
            }
        }

        private static HashSet<string> LoadExistingCatalogKeys(string catalogPath, string kind)
        {
            if (!File.Exists(catalogPath))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            return LoadCatalogEntries(catalogPath)
                .Where(x => string.Equals((string)x["kind"], kind, StringComparison.OrdinalIgnoreCase))
                .Select(x => GetSourceObjectKey((string)x["sourceFile"] ?? string.Empty, (long?)x["pathId"] ?? 0))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static void RewriteCatalogWithoutKind(string catalogPath, string kind)
        {
            if (!File.Exists(catalogPath))
            {
                return;
            }

            var tempPath = catalogPath + ".tmp";
            using (var writer = new StreamWriter(tempPath, false, Encoding.UTF8))
            {
                foreach (var line in File.ReadLines(catalogPath))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        var obj = JObject.Parse(line);
                        if (string.Equals((string)obj["kind"], kind, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        // Preserve malformed catalog lines; other readers already skip them defensively.
                    }

                    writer.WriteLine(line);
                }
            }

            File.Copy(tempPath, catalogPath, true);
            File.Delete(tempPath);
        }

        private static JObject BuildVfxCatalogEntry(VfxLibraryEntry entry)
        {
            return new JObject
            {
                ["kind"] = "VFX",
                ["resourceKind"] = "VFX",
                ["vfxCategory"] = entry.Category,
                ["name"] = entry.Name,
                ["sourceType"] = entry.SourceType,
                ["container"] = null,
                ["source"] = entry.Source,
                ["sourceFile"] = entry.SerializedFile,
                ["pathId"] = entry.PathId,
                ["output"] = entry.OutputDirectory,
                ["modelPreview"] = entry.ModelPreview,
                ["confidence"] = entry.Confidence,
                ["componentCount"] = entry.Components.Count,
                ["materialRefCount"] = entry.MaterialRefs.Count,
                ["textureRefCount"] = entry.TextureRefs.Count,
                ["texturePreviewCount"] = entry.TexturePreviews.Count,
                ["texturePreviewResolveMisses"] = entry.TexturePreviewResolveMisses,
                ["texturePreviewWriteFailures"] = entry.TexturePreviewWriteFailures,
                ["texturePreviews"] = JArray.FromObject(entry.TexturePreviews),
                ["meshRefCount"] = entry.MeshRefs.Count,
                ["occurrenceCount"] = entry.OccurrenceCount,
                ["signals"] = JArray.FromObject(entry.Signals.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()),
                ["previewHints"] = entry.PreviewHints,
                ["status"] = entry.TexturePreviews.Count > 0
                    ? "texture_backed_approx_particle_preview"
                    : "metadata_only_particle_runtime_not_baked",
            };
        }

        private static void WriteVfxEntryFiles(VfxLibraryEntry entry)
        {
            var json = new JObject
            {
                ["schemaVersion"] = 1,
                ["generatedUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["name"] = entry.Name,
                ["resourceKind"] = entry.ResourceKind,
                ["category"] = entry.Category,
                ["confidence"] = entry.Confidence,
                ["sourceType"] = entry.SourceType,
                ["source"] = entry.Source,
                ["sourceFile"] = entry.SerializedFile,
                ["pathId"] = entry.PathId,
                ["modelPreview"] = entry.ModelPreview,
                ["signals"] = JArray.FromObject(entry.Signals.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()),
                ["components"] = JArray.FromObject(entry.Components),
                ["materials"] = JArray.FromObject(entry.MaterialRefs),
                ["textures"] = JArray.FromObject(entry.TextureRefs),
                ["texturePreviews"] = JArray.FromObject(entry.TexturePreviews),
                ["texturePreviewResolveMisses"] = entry.TexturePreviewResolveMisses,
                ["texturePreviewWriteFailures"] = entry.TexturePreviewWriteFailures,
                ["meshes"] = JArray.FromObject(entry.MeshRefs),
                ["previewHints"] = entry.PreviewHints,
                ["occurrenceCount"] = entry.OccurrenceCount,
                ["sourceSamples"] = JArray.FromObject(entry.SourceSamples),
                ["notes"] = JArray.FromObject(entry.Notes),
                ["particleSystemStatus"] = entry.PreviewHints.Properties().Any()
                    ? "Unity ParticleSystem/Renderer preview hints are decoded for approximate non-runtime preview. Shader/VFX Graph/event binding is still not fully baked."
                    : "Unity component metadata is indexed. Full ParticleSystem module simulation/export is not decoded in this version.",
                ["libraryRule"] = "Default Library keeps likely useful VFX assets and reports incomplete runtime behavior instead of dropping them.",
            };
            File.WriteAllText(Path.Combine(entry.OutputDirectory, "vfx.json"), json.ToString(Newtonsoft.Json.Formatting.Indented), Encoding.UTF8);

            var sb = new StringBuilder();
            sb.AppendLine("# VFX Report");
            sb.AppendLine();
            sb.AppendLine($"- Name: `{entry.Name}`");
            sb.AppendLine($"- Category: `{entry.Category}`");
            sb.AppendLine($"- Confidence: `{entry.Confidence}`");
            sb.AppendLine($"- Source type: `{entry.SourceType}`");
            sb.AppendLine($"- Source: `{entry.Source}`");
            sb.AppendLine($"- PathID: `{entry.PathId}`");
            if (!string.IsNullOrWhiteSpace(entry.ModelPreview))
            {
                sb.AppendLine($"- Mesh preview: `{entry.ModelPreview}`");
            }
            sb.AppendLine($"- Material refs: `{entry.MaterialRefs.Count}`");
            sb.AppendLine($"- Texture refs: `{entry.TextureRefs.Count}`");
            sb.AppendLine($"- Texture preview files: `{entry.TexturePreviews.Count}`");
            sb.AppendLine($"- Texture preview resolve misses: `{entry.TexturePreviewResolveMisses}`");
            sb.AppendLine($"- Texture preview write failures: `{entry.TexturePreviewWriteFailures}`");
            sb.AppendLine($"- Mesh refs: `{entry.MeshRefs.Count}`");
            sb.AppendLine($"- Occurrences: `{entry.OccurrenceCount}`");
            if (entry.TexturePreviews.Count > 0)
            {
                sb.AppendLine("- Preview status: `texture_backed_approx_particle_preview`");
            }
            sb.AppendLine();
            sb.AppendLine("## Components / Signals");
            foreach (var signal in entry.Signals.Distinct(StringComparer.OrdinalIgnoreCase).Take(64))
            {
                sb.AppendLine($"- `{signal}`");
            }
            sb.AppendLine();
            sb.AppendLine("## Notes");
            foreach (var note in entry.Notes)
            {
                sb.AppendLine($"- {note}");
            }
            File.WriteAllText(Path.Combine(entry.OutputDirectory, "VFX_REPORT.md"), sb.ToString(), Encoding.UTF8);
        }

        private static void WriteVfxLibrarySummary(string vfxRoot, IReadOnlyCollection<VfxLibraryEntry> entries, Dictionary<string, int> categoryCounts)
        {
            var summary = new JObject
            {
                ["schemaVersion"] = 1,
                ["generatedUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["count"] = entries.Count,
                ["countsByCategory"] = JObject.FromObject(categoryCounts.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value)),
                ["rule"] = "VFX Library is a metadata-first Unity effect library. It exports ParticleSystem/VisualEffect/LineRenderer/TrailRenderer evidence and mesh VFX references; full runtime particle playback is a later preview/runtime task.",
                ["entries"] = JArray.FromObject(entries.Select(x => new
                {
                    x.Name,
                    x.Category,
                    x.Confidence,
                    x.SourceType,
                    x.Source,
                    x.PathId,
                    x.OutputDirectory,
                    x.ModelPreview,
                    componentCount = x.Components.Count,
                    materialRefCount = x.MaterialRefs.Count,
                    textureRefCount = x.TextureRefs.Count,
                    texturePreviewCount = x.TexturePreviews.Count,
                    meshRefCount = x.MeshRefs.Count,
                    occurrenceCount = x.OccurrenceCount,
                    signals = x.Signals.Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToArray(),
                })),
            };
            File.WriteAllText(Path.Combine(vfxRoot, "vfx_library.json"), summary.ToString(Newtonsoft.Json.Formatting.Indented), Encoding.UTF8);

            var sb = new StringBuilder();
            sb.AppendLine("# VFX Library");
            sb.AppendLine();
            sb.AppendLine("这份目录收集 Unity 特效相关素材：`ParticleSystem`、`ParticleSystemRenderer`、`LineRenderer`、`TrailRenderer`、`VisualEffect`、GPU Particle/VFX 对象，以及通过命名/路径识别出的 mesh 型特效。");
            sb.AppendLine();
            sb.AppendLine("当前版本是 metadata-first 管线：先把特效对象、组件证据、mesh 预览和限制记录下来。Unity 粒子模块参数、shader 动画、事件触发和运行时绑定不会被伪装成已完整还原。");
            sb.AppendLine();
            sb.AppendLine("## 分类统计");
            foreach (var pair in categoryCounts.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"- `{pair.Key}`: {pair.Value}");
            }
            sb.AppendLine();
            sb.AppendLine("## 使用规则");
            sb.AppendLine();
            sb.AppendLine("- `vfx.json`：机器可读的 Unity 特效元数据。");
            sb.AppendLine("- `VFX_REPORT.md`：人工查看时的说明。");
            sb.AppendLine("- `modelPreview`：如果该特效同时有 mesh/glTF 预览，会指向已导出的模型。");
            sb.AppendLine("- `texturePreviews`：从已导出贴图或当前加载 Texture2D 解码出的近似预览贴图，Browser 会优先使用它画 billboard/mesh 粒子。");
            sb.AppendLine("- 看到 `texture_backed_approx_particle_preview` 表示它已经有贴图辅助预览，但仍不是完整 Unity 粒子运行时。");
            sb.AppendLine("- 看到 `metadata_only_particle_runtime_not_baked` 表示它是 Unity 粒子/特效组件，但还不是独立可播放粒子文件。");
            File.WriteAllText(Path.Combine(vfxRoot, "VFX_LIBRARY.md"), sb.ToString(), Encoding.UTF8);
        }

        private static string GetVfxOutputDirectory(string savePath, VfxLibraryEntry entry)
        {
            var category = string.IsNullOrWhiteSpace(entry.Category) ? "Other" : entry.Category;
            var name = FirstNonEmpty(entry.Name, entry.SourceType, $"VFX_{GetShortPathId(entry.PathId)}");
            var folder = $"{name}_{GetShortPathId(entry.PathId)}";
            return Path.Combine(savePath, "VFX", SanitizeRelativePath(category), SanitizeRelativePath(folder));
        }

        private static string GetVfxEntryKey(VfxLibraryEntry entry)
        {
            return GetSourceObjectKey(entry.SerializedFile ?? string.Empty, entry.PathId);
        }

        private static string ClassifyVfxCategory(params string[] values)
        {
            var text = string.Join("/", values.Where(x => !string.IsNullOrWhiteSpace(x))).ToLowerInvariant();
            if (Regex.IsMatch(text, @"trail|line"))
            {
                return "Trail";
            }
            if (Regex.IsMatch(text, @"projectile|bullet|missile|arrow|fireball|bolt"))
            {
                return "Projectile";
            }
            if (Regex.IsMatch(text, @"explosion|impact|hit|burst|shockwave"))
            {
                return "Impact";
            }
            if (Regex.IsMatch(text, @"aura|buff|debuff|zone|field|ring|circle"))
            {
                return "Aura";
            }
            if (Regex.IsMatch(text, @"fire|flame|smoke|fog|spark|dust|water|ice|lightning|beam|laser"))
            {
                return "Elemental";
            }
            if (Regex.IsMatch(text, @"slash|skill|spell|cast|attack"))
            {
                return "Skill";
            }
            return "Other";
        }

        private static IReadOnlyCollection<ClassIDType> GetVfxComponentClassIds()
        {
            return new[]
            {
                ClassIDType.ParticleSystem,
                ClassIDType.ParticleSystemRenderer,
                ClassIDType.ParticleSystemForceField,
                ClassIDType.LineRenderer,
                ClassIDType.TrailRenderer,
                ClassIDType.VFXRenderer,
                ClassIDType.VFXManager,
                ClassIDType.GPUParticleSystemRenderer,
                ClassIDType.GPUParticleSystemAsset,
                ClassIDType.ParticleSystemCurve3D,
                ClassIDType.VisualEffectSubgraph,
                ClassIDType.VisualEffectSubgraphOperator,
                ClassIDType.VisualEffectSubgraphBlock,
                ClassIDType.VisualEffectAsset,
                ClassIDType.VisualEffectResource,
                ClassIDType.VisualEffectObject,
                ClassIDType.VisualEffect,
            };
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
        }

        private static string ReadString(SqliteDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
        }

        private static long ReadInt64(SqliteDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? 0 : reader.GetInt64(ordinal);
        }

        private static void IncrementCount(Dictionary<string, int> map, string key)
        {
            key = string.IsNullOrWhiteSpace(key) ? "Unknown" : key;
            map.TryGetValue(key, out var count);
            map[key] = count + 1;
        }

        public static void GenerateLibraryIndexes(string savePath, bool skipSqliteIndex = false, string sourceGame = null, string sourceIndexPath = null)
        {
            if (IncludeVfx)
            {
                NormalizePathPollutedModelResourceKinds(savePath);
                GenerateVfxLibrary(savePath);
            }

            var catalogPath = Path.Combine(savePath, "asset_catalog.jsonl");
            if (!File.Exists(catalogPath))
            {
                return;
            }

            using (ProfileLogger.Measure("generate_library_indexes", new Dictionary<string, object>
            {
                ["catalogPath"] = catalogPath,
            }))
            {
                List<JObject> entries;
                using (ProfileLogger.Measure("library_index_load_catalog", new Dictionary<string, object>
                {
                    ["catalogPath"] = catalogPath,
                }))
                {
                    entries = LoadCatalogEntries(catalogPath);
                }

                using (ProfileLogger.Measure("library_index_write_summary", new Dictionary<string, object>
                {
                    ["entryCount"] = entries.Count,
                }))
                {
                    WriteAssetSummary(savePath, catalogPath, entries);
                    WriteExportRunSummary(savePath);
                }

                using (ProfileLogger.Measure("library_index_validate_models", new Dictionary<string, object>
                {
                    ["entryCount"] = entries.Count,
                }))
                {
                    ModelLibraryValidator.Generate(savePath);
                }

                var models = entries.Where(x => (string)x["kind"] == "Model").ToList();
                if (ApplyModelValidationSummaryToCatalogEntries(savePath, catalogPath, entries, models))
                {
                    entries = LoadCatalogEntries(catalogPath);
                    models = entries.Where(x => (string)x["kind"] == "Model").ToList();
                }
                var animations = entries.Where(x => (string)x["kind"] == "Animation").ToList();
                ExplicitAnimationLinks structuralLinks;
                using (ProfileLogger.Measure("library_index_build_animation_links", new Dictionary<string, object>
                {
                    ["modelCount"] = models.Count,
                    ["animationCount"] = animations.Count,
                    ["pairCount"] = (long)models.Count * animations.Count,
                    ["pairLimit"] = FullStructuralAnimationPairLimit,
                }))
                {
                    structuralLinks = BuildStructuralUnityAnimationLinksForLibrary(models, animations);
                }

                using (ProfileLogger.Measure("library_index_write_animation_indexes", new Dictionary<string, object>
                {
                    ["modelCount"] = models.Count,
                    ["animationCount"] = animations.Count,
                }))
                {
                    WriteAnimationIndexes(savePath, catalogPath, models, animations, structuralLinks);
                }
                using (ProfileLogger.Measure("library_index_normalize_relative_paths", new Dictionary<string, object>
                {
                    ["savePath"] = savePath,
                }))
                {
                    LibraryRelativePathMigrator.NormalizeIndexesBeforeSqlite(savePath);
                }
                if (skipSqliteIndex)
                {
                    Logger.Info("Skipped library_index.db build for this Library export.");
                }
                else
                {
                    BuildDefaultLibrarySqliteIndex(savePath, sourceGame, sourceIndexPath);
                }
                Logger.Info($"Generated library indexes once after export: {models.Count} model(s), {animations.Count} animation(s).");
            }
        }

        public static void RebuildLibraryIndexes(string savePath, string sourceGame = null)
        {
            var catalogPath = Path.Combine(savePath, "asset_catalog.jsonl");
            if (!File.Exists(catalogPath))
            {
                throw new FileNotFoundException("asset_catalog.jsonl was not found. Rebuild requires a previous Library export.", catalogPath);
            }

            EnsureSourceIndexLoadedForRebuild(savePath);
            if (IncludeVfx)
            {
                NormalizePathPollutedModelResourceKinds(savePath);
                GenerateVfxLibrary(savePath);
            }
            var entries = LoadCatalogEntries(catalogPath);
            WriteAssetSummary(savePath, catalogPath, entries);
            ModelLibraryValidator.Generate(savePath);

            var models = entries.Where(x => (string)x["kind"] == "Model").ToList();
            if (ApplyModelValidationSummaryToCatalogEntries(savePath, catalogPath, entries, models))
            {
                entries = LoadCatalogEntries(catalogPath);
                models = entries.Where(x => (string)x["kind"] == "Model").ToList();
            }
            var animations = entries.Where(x => (string)x["kind"] == "Animation").ToList();
            var structuralLinks = BuildStructuralUnityAnimationLinksForLibrary(models, animations);
            WriteAnimationIndexes(savePath, catalogPath, models, animations, structuralLinks);
            LibraryRelativePathMigrator.NormalizeIndexesBeforeSqlite(savePath);
            BuildDefaultLibrarySqliteIndex(savePath, sourceGame);
            Logger.Info($"Rebuilt library indexes from catalog: {models.Count} model(s), {animations.Count} animation(s). If unity_source_index.db exists beside the Library, SQLite rebuild restores deterministic Animator/Animation/Controller/PPtr candidates without full re-export.");
        }

        private static void NormalizePathPollutedModelResourceKinds(string savePath)
        {
            var catalogPath = Path.Combine(savePath, "asset_catalog.jsonl");
            if (!File.Exists(catalogPath))
            {
                return;
            }

            var changed = 0;
            var checkedModels = 0;
            var tempPath = catalogPath + ".resourcekind.tmp";
            using (var writer = new StreamWriter(tempPath, false, Encoding.UTF8))
            {
                foreach (var line in File.ReadLines(catalogPath))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        writer.WriteLine(line);
                        continue;
                    }

                    JObject obj;
                    try
                    {
                        obj = JObject.Parse(line);
                    }
                    catch
                    {
                        writer.WriteLine(line);
                        continue;
                    }

                    if (string.Equals((string)obj["kind"], "Model", StringComparison.OrdinalIgnoreCase)
                        && string.Equals((string)obj["resourceKind"], "VFX", StringComparison.OrdinalIgnoreCase))
                    {
                        checkedModels++;
                        var corrected = InferLibraryResourceKind(
                            (string)obj["name"],
                            (string)obj["container"],
                            (string)obj["source"]);
                        if (!string.Equals(corrected, "VFX", StringComparison.OrdinalIgnoreCase))
                        {
                            obj["resourceKind"] = corrected;
                            obj["resourceKindRepair"] = JObject.FromObject(new
                            {
                                reason = "recomputedAfterVfxPathSignalFix",
                                oldResourceKind = "VFX",
                                newResourceKind = corrected,
                                rule = "模型分类重新计算时，VFX 关键词只使用 container、资源名和源文件名，不使用完整绝对安装路径，避免 Genshin Impact Game 这类路径污染。",
                            });
                            changed++;
                        }
                    }

                    writer.WriteLine(JsonConvert.SerializeObject(obj, Newtonsoft.Json.Formatting.None));
                }
            }

            if (changed == 0)
            {
                File.Delete(tempPath);
                return;
            }

            File.Replace(tempPath, catalogPath, null);
            Logger.Info($"Normalized {changed}/{checkedModels} path-polluted model resourceKind value(s) in asset_catalog.jsonl before rebuilding Library indexes.");
            ProfileLogger.Event("library_catalog_resource_kind_normalized", new Dictionary<string, object>
            {
                ["checkedModelVfxCount"] = checkedModels,
                ["changedCount"] = changed,
                ["rule"] = "Only kind=Model entries previously marked resourceKind=VFX are recomputed. Real mesh VFX names still remain VFX.",
            });
        }

        private static void EnsureSourceIndexLoadedForRebuild(string savePath)
        {
            if (SQLiteSourceIndexRuntime.CurrentLoadResult != null || string.IsNullOrWhiteSpace(savePath))
            {
                return;
            }

            var sourceIndexPath = Path.Combine(savePath, "unity_source_index.db");
            if (!File.Exists(sourceIndexPath))
            {
                Logger.Warning("No unity_source_index.db was found beside the Library. Rebuild will keep catalog-backed metadata only; Unity component VFX and dependency relations may be incomplete.");
                return;
            }

            try
            {
                SQLiteSourceIndexRuntime.LoadIntoAssetsHelper(sourceIndexPath, null, null);
                SQLiteSourceIndexRuntime.WriteUsageReport(savePath, SQLiteSourceIndexRuntime.CurrentLoadResult);
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is SqliteException)
            {
                Logger.Warning($"Unable to load unity_source_index.db for rebuild. Rebuild will keep catalog-backed metadata only. {e.GetType().Name}: {e.Message}");
            }
        }

        private static void BuildDefaultLibrarySqliteIndex(string savePath, string sourceGame = null, string sourceIndexPath = null)
        {
            using (ProfileLogger.Measure("library_index_build_sqlite", new Dictionary<string, object>
            {
                ["savePath"] = savePath,
            }))
            {
                try
                {
                    SQLiteLibraryIndexBuilder.Build(
                        savePath,
                        sourceIndexPath: sourceIndexPath,
                        sourceGame: FirstNonEmpty(sourceGame, Game?.Name));
                }
                catch (Exception e)
                {
                    Logger.Warning($"SQLite library index failed; exported files remain usable. {e.GetType().Name}: {e.Message}");
                    ProfileLogger.Event("library_index_build_sqlite_failed", new Dictionary<string, object>
                    {
                        ["exception"] = e.GetType().FullName,
                        ["message"] = e.Message,
                    });
                }
            }
        }

        private static List<JObject> LoadCatalogEntries(string catalogPath)
        {
            var libraryRoot = Path.GetDirectoryName(Path.GetFullPath(catalogPath)) ?? "";
            return File.ReadLines(catalogPath)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x =>
                {
                    try
                    {
                        var obj = JObject.Parse(x);
                        ResolveCatalogInternalPaths(obj, libraryRoot);
                        return obj;
                    }
                    catch
                    {
                        return null;
                    }
                })
                .Where(x => x != null)
                .ToList();
        }

        private static void ResolveCatalogInternalPaths(JObject obj, string libraryRoot)
        {
            foreach (var propertyName in new[] { "output", "modelPreview", "animationAsset" })
            {
                var value = (string)obj[propertyName];
                if (!string.IsNullOrWhiteSpace(value))
                {
                    obj[propertyName] = LibraryRelativePathMigrator.ResolveLibraryPath(libraryRoot, value);
                }
            }
        }

        private static void WriteAssetSummary(string savePath, string catalogPath, List<JObject> entries)
        {
            var summary = new
            {
                generatedAt = DateTime.UtcNow.ToString("O"),
                catalog = catalogPath,
                totalsByKind = entries
                    .GroupBy(x => (string)x["kind"] ?? "Unknown")
                    .OrderBy(x => x.Key)
                    .ToDictionary(x => x.Key, x => x.Count()),
                totalsByResourceKind = entries
                    .GroupBy(x => (string)x["resourceKind"] ?? "Unknown")
                    .OrderBy(x => x.Key)
                    .ToDictionary(x => x.Key, x => x.Count()),
                modelStats = new
                {
                    count = entries.Count(x => (string)x["kind"] == "Model"),
                    skinned = entries.Count(x => (string)x["kind"] == "Model" && ((int?)x["boneCount"] ?? 0) > 0),
                    withTextures = entries.Count(x => (string)x["kind"] == "Model" && ((int?)x["textureCount"] ?? 0) > 0),
                    withMorphs = entries.Count(x => (string)x["kind"] == "Model" && ((int?)x["morphCount"] ?? 0) > 0),
                },
            };
            File.WriteAllText(
                Path.Combine(savePath, "asset_summary.json"),
                JsonConvert.SerializeObject(summary, Newtonsoft.Json.Formatting.Indented)
            );
        }

        private static void WriteExportRunSummary(string savePath)
        {
            if (_exportRunStats == null)
            {
                return;
            }

            var parseFailures = assetsManager.ObjectParseFailureCounts
                .OrderByDescending(x => x.Value)
                .ThenBy(x => x.Key.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key.ToString(), x => x.Value);
            var summary = new
            {
                generatedAt = DateTime.UtcNow.ToString("O"),
                startedAt = _exportRunStats.StartedAtUtc?.ToString("O"),
                finishedAt = _exportRunStats.FinishedAtUtc?.ToString("O"),
                mode = _exportRunStats.Mode,
                candidates = new
                {
                    models = _exportRunStats.ModelCandidateCount,
                    animations = _exportRunStats.AnimationCandidateCount,
                },
                results = new
                {
                    exportedModels = _exportRunStats.ExportedModels,
                    skippedModels = _exportRunStats.SkippedModels,
                    failedModels = _exportRunStats.FailedModels,
                },
                skippedByReason = _exportRunStats.SkippedByReason
                    .OrderByDescending(x => x.Value)
                    .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.Value),
                failedByReason = _exportRunStats.FailedByReason
                    .OrderByDescending(x => x.Value)
                    .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.Value),
                objectParseFailures = new
                {
                    total = parseFailures.Values.Sum(),
                    byType = parseFailures,
                    note = "Recoverable Unity object parse failures are skipped or kept as lightweight placeholders. They are not treated as usable exported assets.",
                },
                samples = new
                {
                    skipped = _exportRunStats.SkippedSamples,
                    failed = _exportRunStats.FailedSamples,
                },
            };
            File.WriteAllText(
                Path.Combine(savePath, "export_run_summary.json"),
                JsonConvert.SerializeObject(summary, Newtonsoft.Json.Formatting.Indented)
            );
        }

        private static void WriteAnimationIndexes(
            string savePath,
            string catalogPath,
            List<JObject> models,
            List<JObject> animations,
            ExplicitAnimationLinks explicitAnimationLinks)
        {
            using (ProfileLogger.Measure("library_index_write_skeletons", new Dictionary<string, object>
            {
                ["modelCount"] = models.Count,
                ["animationCount"] = animations.Count,
                ["matchingDeferred"] = explicitAnimationLinks.MatchingDeferred,
            }))
            {
                GenerateSkeletonLibraryIndex(savePath, models, explicitAnimationLinks);
            }

            var bindingsPath = Path.Combine(savePath, "animation_bindings.jsonl");
            using (ProfileLogger.Measure("library_index_write_animation_bindings", new Dictionary<string, object>
            {
                ["animationCount"] = animations.Count,
                ["matchingDeferred"] = explicitAnimationLinks.MatchingDeferred,
            }))
            {
                using var writer = new StreamWriter(bindingsPath, false);
                foreach (var animation in animations)
                {
                    var animationKey = GetCatalogKey(animation);
                    explicitAnimationLinks.ByAnimation.TryGetValue(animationKey, out var linkedModels);
                    var candidates = (linkedModels ?? new List<ExplicitModelAnimationLink>())
                        .OrderBy(x => x.ModelName, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (candidates.Count == 0)
                    {
                        var unbound = new
                        {
                            animation = BuildBindingAsset(animation),
                            candidateCount = 0,
                            candidates = Array.Empty<object>(),
                            note = explicitAnimationLinks.MatchingDeferred
                                ? explicitAnimationLinks.DeferredReason
                                : "No exported model has an explicit Unity Animator/Animation reference to this clip in the current loaded files.",
                        };
                        writer.WriteLine(JsonConvert.SerializeObject(unbound));
                        continue;
                    }

                    var binding = new
                    {
                        animation = BuildBindingAsset(animation),
                        candidateCount = candidates.Count,
                        candidates = candidates.Take(16).Select(link => new
                        {
                            name = link.ModelName,
                            resourceKind = link.ModelResourceKind,
                            output = link.ModelOutput,
                            skeletonHash = link.ModelSkeletonHash,
                            boneCount = link.ModelBoneCount,
                            meshCount = link.ModelMeshCount,
                            textureCount = link.ModelTextureCount,
                            relationSource = link.Source,
                            score = link.Score,
                            confidence = link.Confidence,
                            relation = link.Relation,
                            matchReasons = link.Reasons,
                            matchedBindingPaths = link.MatchedBindingPaths,
                            unmatchedBindingPaths = link.UnmatchedBindingPaths,
                            requiresHumanoidBake = false,
                            legacyUnityBakeSupported = link.RequiresHumanoidBake,
                            requiresInternalHumanoidSolve = link.RequiresInternalHumanoidSolve,
                        }).ToArray(),
                    };
                    writer.WriteLine(JsonConvert.SerializeObject(binding));
                }
            }

            if (explicitAnimationLinks.MatchingDeferred)
            {
                var deferred = new
                {
                    generatedAt = DateTime.UtcNow.ToString("O"),
                    catalog = catalogPath,
                    rule = "Full verbose model-animation candidates were intentionally deferred for this large Library export. Models and animations remain clean and standalone; use model_animations.compact.json, animation_bindings.jsonl, skeletons.json, and targeted preview/pack commands.",
                    relationRule = "Default model-animation candidates only use deterministic Unity Animator/Animation references. Skeleton/binding compatibility is kept for diagnostics and targeted preview, not emitted as a default binding relation.",
                    matchingDeferred = true,
                    deferredReason = explicitAnimationLinks.DeferredReason,
                    pairCount = explicitAnimationLinks.PairCount,
                    pairLimit = explicitAnimationLinks.PairLimit,
                    modelCount = models.Count,
                    animationCount = animations.Count,
                    compactIndex = Path.Combine(savePath, "model_animations.compact.json"),
                    skeletonIndex = Path.Combine(savePath, "skeletons.json"),
                    animationBindings = bindingsPath,
                };
                File.WriteAllText(
                    Path.Combine(savePath, "model_animations.json"),
                    JsonConvert.SerializeObject(deferred, Newtonsoft.Json.Formatting.Indented)
                );
            }
            else
            {
                using (ProfileLogger.Measure("library_index_write_model_animations_verbose", new Dictionary<string, object>
                {
                    ["modelCount"] = models.Count,
                    ["animationCount"] = animations.Count,
                }))
                {
                    var modelAnimations = new
                    {
                        generatedAt = DateTime.UtcNow.ToString("O"),
                        catalog = catalogPath,
                        rule = "Models stay clean by default. Animation candidates are indexed here; preview or bundle commands should explicitly write selected animations into glTF/GLB.",
                        relationRule = "Default candidates come from explicit Unity Animator/Animation references. Unity AnimationClip binding paths, skeleton compatibility, path/name/resourceKind signals are diagnostics only unless an explicit fallback option enables them.",
                        capabilityRule = "animationCapability only describes the next safe processing path. It is not visual proof; trusted playback requires generated glTF animation channels and validation reports.",
                        capabilitySummary = BuildAnimationCapabilitySummary(models, animations),
                        relationGraph = Path.Combine(savePath, "unity_relations.jsonl"),
                        relationSummary = Path.Combine(savePath, "unity_relation_summary.json"),
                        skeletonIndex = Path.Combine(savePath, "skeletons.json"),
                        models = models
                            .OrderBy(x => (string)x["resourceKind"])
                            .ThenBy(x => (string)x["name"])
                            .Select(model =>
                            {
                                var modelKey = GetCatalogKey(model);
                                explicitAnimationLinks.ByModel.TryGetValue(modelKey, out var linkedAnimations);
                                var candidates = (linkedAnimations ?? new List<ExplicitModelAnimationLink>())
                                    .Select(BuildExplicitModelAnimationCandidate)
                                    .OrderByDescending(x => x.score)
                                    .ThenBy(x => x.name)
                                    .ToArray();

                                return new
                                {
                                    model = BuildModelBindingAsset(model),
                                    candidateCount = candidates.Length,
                                    usableCandidateCount = candidates.Count(x => x.modelReadyForAnimation),
                                    modelReadyForAnimation = BuildModelAnimationGate(model).Ready,
                                    modelAnimationGate = BuildModelAnimationGateJson(BuildModelAnimationGate(model)),
                                    embeddedAnimationCount = (int?)model["animationCount"] ?? 0,
                                    candidates,
                                    notes = candidates.Length == 0
                                        ? new[] { "No explicit Unity Animator/Animation clip reference was found for this exported model in the current loaded files." }
                                        : Array.Empty<string>(),
                                };
                            })
                            .ToArray(),
                    };
                    File.WriteAllText(
                        Path.Combine(savePath, "model_animations.json"),
                        JsonConvert.SerializeObject(modelAnimations, Newtonsoft.Json.Formatting.Indented)
                    );
                }
            }

            using (ProfileLogger.Measure("library_index_write_model_animations_compact", new Dictionary<string, object>
            {
                ["modelCount"] = models.Count,
                ["animationCount"] = animations.Count,
                ["matchingDeferred"] = explicitAnimationLinks.MatchingDeferred,
            }))
            {
                GenerateCompactModelAnimationIndex(savePath, catalogPath, models, animations, explicitAnimationLinks);
            }

            using (ProfileLogger.Measure("library_index_write_character_assemblies", new Dictionary<string, object>
            {
                ["modelCount"] = models.Count,
            }))
            {
                CharacterAssemblyIndexGenerator.Generate(savePath, models);
            }

            using (ProfileLogger.Measure("library_index_write_asset_readmes", new Dictionary<string, object>
            {
                ["modelCount"] = models.Count,
            }))
            {
                AssetReadmeGenerator.Generate(savePath);
            }
        }

        private static bool ApplyModelValidationSummaryToCatalogEntries(string savePath, string catalogPath, IReadOnlyList<JObject> entries, IReadOnlyList<JObject> models)
        {
            var validationByOutput = ReadModelValidationByOutputForAnimationGate(savePath);
            if (validationByOutput.Count == 0)
            {
                return false;
            }

            var changed = false;
            foreach (var model in models)
            {
                var output = NormalizeLibraryOutputForAnimationGate(savePath, JsonText(model, "output"));
                if (validationByOutput.TryGetValue(output, out var validation))
                {
                    changed |= SetCatalogValue(model, "modelValidation", validation.DeepClone());
                    changed |= ApplyModelValidationMaterialSummary(model, validation);
                    changed |= ApplyModelResourceKindOutputFallback(model);
                }
            }

            if (!changed)
            {
                return false;
            }

            RewriteCatalogEntries(catalogPath, entries);
            Logger.Info($"Updated asset_catalog.jsonl with model validation summary for {models.Count} model row(s).");
            return true;
        }

        private static bool ApplyModelResourceKindOutputFallback(JObject model)
        {
            var current = JsonText(model, "resourceKind");
            if (!IsUnknownResourceKind(current))
            {
                return false;
            }

            var output = JsonText(model, "output");
            if (string.IsNullOrWhiteSpace(output))
            {
                return false;
            }

            // 只用 Library 相对 output 做兜底，不读取绝对安装路径，避免路径中游戏名或目录名污染分类。
            var inferred = InferLibraryResourceKind(JsonText(model, "name"), JsonText(model, "container"), output);
            if (IsUnknownResourceKind(inferred))
            {
                return false;
            }

            var changed = SetCatalogValue(model, "resourceKind", inferred);
            changed |= SetCatalogValue(model, "resourceKindEvidence", JObject.FromObject(new
            {
                basis = "libraryRelativeOutputFallback",
                oldResourceKind = current,
                output,
                rule = "仅当原 resourceKind 为 Unknown 时，使用 Library 相对输出路径里的通用素材语义补判；不按游戏私有角色名前缀猜。"
            }));
            return changed;
        }

        private static bool ApplyModelValidationMaterialSummary(JObject model, JObject validation)
        {
            var changed = false;
            var body = validation?["Body"] as JObject;
            var missingPrimitives = body?["MissingMaterialPrimitives"]?
                .Values<string>()
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<string>();
            var primitiveCount = JsonLong(body, "PrimitiveCount") ?? 0;
            var primitivesWithMaterial = JsonLong(body, "PrimitivesWithMaterial") ?? primitiveCount;
            var primitivesWithBaseColorTexture = JsonLong(body, "PrimitivesWithBaseColorTexture") ?? 0;
            var missingMaterialCount = missingPrimitives.Length > 0
                ? missingPrimitives.Length
                : Math.Max(0, primitiveCount - primitivesWithMaterial);

            changed |= SetCatalogValue(model, "modelValidationStatus", JsonText(validation, "Status"));
            changed |= SetCatalogValue(model, "modelBodyStatus", JsonText(body, "ModelBodyStatus"));
            changed |= SetCatalogValue(model, "materialPrimitiveCount", primitiveCount);
            changed |= SetCatalogValue(model, "materialPrimitivesWithMaterial", primitivesWithMaterial);
            changed |= SetCatalogValue(model, "materialPrimitivesWithBaseColorTexture", primitivesWithBaseColorTexture);
            changed |= SetCatalogValue(model, "materialMissingRendererBinding", missingMaterialCount > 0 || CatalogMaterialStatusImpliesMissingRendererBinding(model));
            changed |= SetCatalogValue(model, "materialMissingRendererPrimitiveCount", missingMaterialCount);
            changed |= SetCatalogValue(model, "materialMissingRendererPrimitives", new JArray(missingPrimitives.Take(64)));
            changed |= SetCatalogValue(model, "materialMissingRendererPrimitivesTruncated", missingPrimitives.Length > 64);
            if (JsonLong(body, "ImageCount") is long imageCount)
            {
                changed |= SetCatalogValue(model, "materialImageCount", imageCount);
            }

            return changed;
        }

        private static bool CatalogMaterialStatusImpliesMissingRendererBinding(JObject model)
        {
            static bool IsMissingStatus(string status)
            {
                return string.Equals(status, "missingRendererMaterial", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "needsRendererBinding", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "rendererMaterialUnresolved", StringComparison.OrdinalIgnoreCase);
            }

            if (IsMissingStatus(JsonText(model, "materialStatus")))
            {
                return true;
            }

            var counts = model?["materialStatusCounts"] as JObject;
            return counts?.Properties().Any(x => IsMissingStatus(x.Name) && x.Value.Value<int>() > 0) == true;
        }

        private static bool SetCatalogValue(JObject obj, string name, object value)
        {
            var token = value switch
            {
                null => JValue.CreateNull(),
                JToken existingToken => existingToken,
                _ => JToken.FromObject(value),
            };

            if (JToken.DeepEquals(obj[name], token))
            {
                return false;
            }

            obj[name] = token;
            return true;
        }

        private static void RewriteCatalogEntries(string catalogPath, IEnumerable<JObject> entries)
        {
            var tempPath = catalogPath + ".validation.tmp";
            using (var writer = new StreamWriter(tempPath, false, Encoding.UTF8))
            {
                foreach (var entry in entries)
                {
                    writer.WriteLine(entry.ToString(Newtonsoft.Json.Formatting.None));
                }
            }

            File.Copy(tempPath, catalogPath, true);
            File.Delete(tempPath);
        }

        private static Dictionary<string, JObject> ReadModelValidationByOutputForAnimationGate(string savePath)
        {
            var path = Path.Combine(savePath, "model_validation.json");
            if (!File.Exists(path))
            {
                return new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                var report = JObject.Parse(File.ReadAllText(path).TrimStart('\uFEFF'));
                return report["models"]?
                    .OfType<JObject>()
                    .Where(x => !string.IsNullOrWhiteSpace(JsonText(x, "Path")))
                    .GroupBy(x => NormalizeLibraryOutputForAnimationGate(savePath, JsonText(x, "Path")), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException e)
            {
                Logger.Warning($"Skipping invalid model_validation.json for model-first animation gate: {e.Message}");
                return new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static ModelAnimationGate BuildModelAnimationGate(JObject model)
        {
            var reasons = new List<string>();
            var validation = model?["modelValidation"] as JObject;
            var body = validation?["Body"] as JObject;

            if (validation == null)
            {
                reasons.Add("model_validation_missing");
            }

            var status = JsonText(validation, "Status");
            if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("model_validation_not_ok");
            }

            var bodyStatus = JsonText(body, "ModelBodyStatus");
            if (!string.IsNullOrWhiteSpace(bodyStatus) && !string.Equals(bodyStatus, "ok", StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("model_body_not_ok");
            }

            if ((JsonLong(model, "meshCount") ?? JsonLong(body, "PrimitiveCount") ?? 0) <= 0)
            {
                reasons.Add("no_mesh");
            }
            if ((JsonLong(model, "vertexCount") ?? JsonLong(body, "PositionVertexCount") ?? 0) <= 0)
            {
                reasons.Add("no_vertices");
            }
            if ((JsonLong(model, "materialCount") ?? JsonLong(body, "MaterialCount") ?? 0) <= 0)
            {
                reasons.Add("no_materials");
            }
            if ((JsonLong(model, "textureCount") ?? JsonLong(body, "TextureCount") ?? 0) <= 0
                && (JsonLong(model, "materialImageCount") ?? JsonLong(body, "ImageCount") ?? 0) <= 0)
            {
                reasons.Add("no_textures");
            }
            if ((JsonLong(model, "materialImageCount") ?? JsonLong(body, "ImageCount") ?? 0) <= 0)
            {
                reasons.Add("no_material_images");
            }
            if (JsonBool(model, "materialMissingRendererBinding"))
            {
                reasons.Add("missing_renderer_material_binding");
            }
            if ((JsonLong(model, "unresolvedModelDependencyCount") ?? 0) > 0)
            {
                reasons.Add("unresolved_model_dependencies");
            }
            if ((JsonLong(body, "MissingImageCount") ?? 0) > 0)
            {
                reasons.Add("missing_images");
            }
            if ((JsonLong(body, "EmptyImageCount") ?? 0) > 0)
            {
                reasons.Add("empty_images");
            }
            if ((JsonLong(body, "SkinnedMeshNodeCount") ?? 0) > 0
                && body?["HasCompleteSkinBinding"]?.Type == JTokenType.Boolean
                && !body["HasCompleteSkinBinding"].Value<bool>())
            {
                reasons.Add("incomplete_skin_binding");
            }
            if (IsDiagnosticModelInstance(model))
            {
                reasons.Add("diagnostic_instance_not_default_animation_gate");
            }

            var evidence = new JObject
            {
                ["rule"] = "只有模型 Mesh/UV/材质/贴图/skin/bbox 和来源域先过关，才允许进入默认动画预览或生产结论。Unity 显式关系会保留作诊断。",
                ["name"] = JsonText(model, "name"),
                ["output"] = JsonText(model, "output"),
                ["resourceKind"] = JsonText(model, "resourceKind"),
                ["container"] = JsonText(model, "container"),
                ["source"] = JsonText(model, "source"),
                ["modelValidationPresent"] = validation != null,
                ["modelValidationStatus"] = status,
                ["modelBodyStatus"] = bodyStatus,
                ["meshCount"] = JsonLong(model, "meshCount") ?? JsonLong(body, "PrimitiveCount"),
                ["vertexCount"] = JsonLong(model, "vertexCount") ?? JsonLong(body, "PositionVertexCount"),
                ["materialCount"] = JsonLong(model, "materialCount") ?? JsonLong(body, "MaterialCount"),
                ["textureCount"] = JsonLong(model, "textureCount") ?? JsonLong(body, "TextureCount"),
                ["materialImageCount"] = JsonLong(model, "materialImageCount") ?? JsonLong(body, "ImageCount"),
                ["skinCount"] = JsonLong(model, "skinCount") ?? JsonLong(body, "SkinCount"),
                ["unresolvedModelDependencyCount"] = JsonLong(model, "unresolvedModelDependencyCount") ?? 0,
                ["unresolvedModelDependencyTypes"] = model?["unresolvedModelDependencyTypes"]?.DeepClone(),
                ["missingImageCount"] = JsonLong(body, "MissingImageCount") ?? 0,
                ["emptyImageCount"] = JsonLong(body, "EmptyImageCount") ?? 0,
            };

            var distinctReasons = reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return new ModelAnimationGate(
                distinctReasons.Length == 0,
                distinctReasons.Length == 0 ? "ready" : "blocked",
                distinctReasons,
                evidence);
        }

        private static JObject BuildModelAnimationGateJson(ModelAnimationGate gate)
        {
            return new JObject
            {
                ["status"] = gate.Status,
                ["ready"] = gate.Ready,
                ["reasons"] = new JArray(gate.Reasons ?? Array.Empty<string>()),
                ["evidence"] = gate.Evidence != null ? (JObject)gate.Evidence.DeepClone() : new JObject(),
            };
        }

        private static bool IsDiagnosticModelInstance(JObject model)
        {
            var text = string.Join(
                "/",
                new[] { JsonText(model, "container"), JsonText(model, "source"), JsonText(model, "name"), JsonText(model, "output") }
                    .Where(x => !string.IsNullOrWhiteSpace(x))
            ).Replace('\\', '/').ToLowerInvariant();
            return Regex.IsMatch(
                text,
                @"(^|[/_.\-\s])(?:dialog|timeline|levelseq|ui|uimodel|preview|deco|pose|camera|cutscene|postmodel|abilityentity|tmpobject|tmp)(?:$|[/_.\-\s0-9])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string NormalizeLibraryOutputForAnimationGate(string root, string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return string.Empty;
            }

            try
            {
                if (Path.IsPathRooted(output))
                {
                    var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var fullOutput = Path.GetFullPath(output);
                    if (fullOutput.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || fullOutput.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        output = Path.GetRelativePath(fullRoot, fullOutput);
                    }
                }
            }
            catch
            {
                // 旧索引或外部工具可能写入非标准路径。归一化失败时保留原文本做 key。
            }

            return output.Replace('\\', '/').TrimStart('/');
        }

        private static string JsonText(JObject obj, string name)
        {
            return obj?[name]?.Type == JTokenType.Null ? null : obj?[name]?.ToString();
        }

        private static long? JsonLong(JObject obj, string name)
        {
            var token = obj?[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            return token.Type == JTokenType.Integer || long.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                ? token.Value<long>()
                : null;
        }

        private static bool JsonBool(JObject obj, string name)
        {
            var token = obj?[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }

            return token.Type == JTokenType.Boolean
                ? token.Value<bool>()
                : bool.TryParse(token.ToString(), out var value) && value;
        }

        public static void ExportAudioLibrary(string savePath)
        {
            var audioClips = exportableAssets.Where(x => x.Asset is AudioClip).ToList();
            Logger.Info($"Exporting audio library: {audioClips.Count} audio clip(s).");
            ExportAssets(savePath, audioClips, AssetGroupOption.ByLibrary, ExportType.Convert);
            WriteAudioLibraryReadme(savePath, audioClips);
        }

        private static void WriteAudioLibraryReadme(string savePath, IReadOnlyCollection<AssetItem> audioClips)
        {
            var counts = GetAudioLibraryCounts(savePath, audioClips);

            var sb = new StringBuilder();
            sb.AppendLine("# Audio Library");
            sb.AppendLine();
            sb.AppendLine("这份目录是独立音频素材库，不属于默认 3D Library。3D Library 默认只导出模型、贴图、骨骼、动画和必要材质信息；音效需要显式使用 `--mode AudioLibrary`。");
            sb.AppendLine();
            sb.AppendLine("## 目录分类");
            sb.AppendLine();
            sb.AppendLine("- `Audio/SFX/`：短音效，例如脚步、命中、按钮、技能、环境点缀等。");
            sb.AppendLine("- `Audio/Music/`：较长音乐、BGM、主题音乐。");
            sb.AppendLine("- `Audio/Voice/`：语音、对白、角色台词。");
            sb.AppendLine("- `Audio/Other/`：无法可靠判断的音频。");
            sb.AppendLine();
            sb.AppendLine("分类优先依据 Unity container/source/name 中的通用语义，再用音频时长兜底；这是素材库浏览规则，不改变原始 Unity 资源。");
            sb.AppendLine();
            sb.AppendLine("## 本次统计");
            sb.AppendLine();
            foreach (var pair in counts)
            {
                sb.AppendLine($"- `{pair.Key}`: {pair.Value}");
            }
            if (counts.Count == 0)
            {
                sb.AppendLine("- 未导出可用音频。");
            }
            sb.AppendLine();
            sb.AppendLine("每个导出的音频旁边会生成 `.audio.json`，记录 Unity 名称、路径、长度、声道、采样率、压缩格式、分类和导出文件。机器索引用 `asset_catalog.jsonl`。");
            File.WriteAllText(Path.Combine(savePath, "AUDIO_LIBRARY_README.md"), sb.ToString(), Encoding.UTF8);
        }

        private static Dictionary<string, int> GetAudioLibraryCounts(string savePath, IReadOnlyCollection<AssetItem> audioClips)
        {
            var catalogPath = Path.Combine(savePath, "asset_catalog.jsonl");
            if (File.Exists(catalogPath))
            {
                return File.ReadLines(catalogPath)
                    .Select(line =>
                    {
                        try
                        {
                            return JObject.Parse(line);
                        }
                        catch
                        {
                            return null;
                        }
                    })
                    .Where(x => x?["kind"]?.ToString() == "Audio")
                    .GroupBy(x => x["output"]?.ToString() ?? $"{x["name"]}|{x["pathId"]}")
                    .Select(x => x.Last())
                    .GroupBy(x => x["audioKind"]?.ToString() ?? x["resourceKind"]?.ToString() ?? "Other")
                    .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.Count());
            }

            return audioClips
                .Where(x => x.Asset is AudioClip)
                .GroupBy(x => Exporter.ClassifyAudioClip((AudioClip)x.Asset, x.Text, x.Container, x.SourceFile?.originalPath ?? x.SourceFile?.fileName))
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Count());
        }

        private static void GenerateCompactModelAnimationIndex(
            string savePath,
            string catalogPath,
            List<JObject> models,
            List<JObject> animations,
            ExplicitAnimationLinks explicitAnimationLinks)
        {
            var orderedModels = models
                .OrderBy(x => (string)x["resourceKind"], StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => (string)x["name"], StringComparer.OrdinalIgnoreCase)
                .ThenBy(GetCatalogKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var orderedAnimations = animations
                .OrderBy(x => (string)x["resourceKind"], StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => (string)x["name"], StringComparer.OrdinalIgnoreCase)
                .ThenBy(GetCatalogKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var modelIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var animationIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var modelIdsByIndex = new string[orderedModels.Length];
            var animationIdsByIndex = new string[orderedAnimations.Length];
            for (var i = 0; i < orderedModels.Length; i++)
            {
                var id = $"m{i}";
                modelIdsByIndex[i] = id;
                var key = GetCatalogKey(orderedModels[i]);
                if (!modelIds.ContainsKey(key))
                {
                    modelIds[key] = id;
                }
            }
            for (var i = 0; i < orderedAnimations.Length; i++)
            {
                var id = $"a{i}";
                animationIdsByIndex[i] = id;
                var key = GetCatalogKey(orderedAnimations[i]);
                if (!animationIds.ContainsKey(key))
                {
                    animationIds[key] = id;
                }
            }

            var compactModels = orderedModels.Select((model, index) => new
            {
                modelAnimationGate = BuildModelAnimationGateJson(BuildModelAnimationGate(model)),
                modelReadyForAnimation = BuildModelAnimationGate(model).Ready,
                id = modelIdsByIndex[index],
                name = (string)model["name"],
                resourceKind = (string)model["resourceKind"],
                output = (string)model["output"],
                source = (string)model["source"],
                container = (string)model["container"],
                skeletonHash = (string)model["skeletonHash"],
                skeletonLibraryId = (string)model["skeleton"]?["libraryId"],
                animationTarget = ClassifyModelAnimationTarget(model),
                boneCount = (int?)model["boneCount"] ?? 0,
                nodeCount = (int?)model["nodeCount"] ?? 0,
                meshCount = (int?)model["meshCount"] ?? 0,
                textureCount = (int?)model["textureCount"] ?? 0,
                morphCount = (int?)model["morphCount"] ?? 0,
                nodePaths = model["nodePaths"]?.ToObject<string[]>()?.Take(128).ToArray(),
                nodePathsTruncated = (bool?)model["nodePathsTruncated"] ?? false,
                meshPaths = model["meshPaths"]?.ToObject<string[]>()?.Take(128).ToArray(),
                meshPathsTruncated = (bool?)model["meshPathsTruncated"] ?? false,
                avatar = BuildCompactAvatarSummary(model["avatar"] as JObject),
            }).ToArray();

            var compactAnimations = orderedAnimations.Select((animation, index) => new
            {
                id = animationIdsByIndex[index],
                name = (string)animation["name"],
                resourceKind = (string)animation["resourceKind"],
                output = (string)animation["output"],
                animationAsset = (string)animation["animationAsset"],
                source = (string)animation["source"],
                container = (string)animation["container"],
                sampleRate = (float?)animation["sampleRate"],
                duration = (float?)animation["duration"],
                curveCount = (int?)animation["curveCount"] ?? 0,
                eventCount = (int?)animation["eventCount"] ?? 0,
                legacy = (bool?)animation["legacy"],
                animationType = (string)animation["animationType"],
                animationCapability = ClassifyAnimationCapability(animation, null),
                hasMuscleClip = (bool?)animation["hasMuscleClip"],
                coreTransformBindingCount = (int?)animation["coreTransformBindingCount"],
                humanoidBindingCount = (int?)animation["humanoidBindingCount"],
                blendShapeBindingCount = (int?)animation["blendShapeBindingCount"],
                trueBlendShapeBindingCount = (int?)animation["trueBlendShapeBindingCount"],
                rendererMaterialBindingCount = (int?)animation["rendererMaterialBindingCount"],
                rendererPropertyBindingCount = (int?)animation["rendererPropertyBindingCount"],
                activeStateBindingCount = (int?)animation["activeStateBindingCount"],
                auxiliaryBindingCount = (int?)animation["auxiliaryBindingCount"],
                classificationNotes = animation["classificationNotes"]?.ToObject<string[]>(),
            }).ToArray();

            var modelAnimationRefs = orderedModels.Select((model, index) =>
            {
                var modelKey = GetCatalogKey(model);
                explicitAnimationLinks.ByModel.TryGetValue(modelKey, out var links);
                var candidates = (links ?? new List<ExplicitModelAnimationLink>())
                    .OrderByDescending(x => x.Score)
                    .ThenBy(x => (string)x.Animation["name"], StringComparer.OrdinalIgnoreCase)
                    .Where(x => animationIds.ContainsKey(GetCatalogKey(x.Animation)))
                    .Select(x => new
                    {
                        animationId = animationIds[GetCatalogKey(x.Animation)],
                        score = x.Score,
                        confidence = x.Confidence,
                        relation = x.Relation,
                        relationSource = x.Source,
                        modelReadyForAnimation = x.ModelReadyForAnimation,
                        modelAnimationBlockedReason = x.ModelAnimationBlockedReason,
                        modelAnimationGate = BuildModelAnimationGateJson(x.ModelAnimationGate),
                        directGltfPreviewReady = x.ModelReadyForAnimation,
                        productionAnimationReady = false,
                        productionAnimationBlockedReason = x.ModelReadyForAnimation ? null : "model_not_animation_ready",
                        requiresHumanoidBake = false,
                        legacyUnityBakeSupported = x.RequiresHumanoidBake,
                        requiresInternalHumanoidSolve = x.RequiresInternalHumanoidSolve,
                        animationCapability = ClassifyAnimationCapability(x.Animation, x),
                        nextAction = x.ModelReadyForAnimation ? GetAnimationNextAction(x.Animation, x) : "fix_model_first",
                        matchReasons = x.Reasons,
                        matchedBindingPaths = TruncateArray(x.MatchedBindingPaths, 16),
                        matchedVisibleMeshBindingPaths = TruncateArray(x.MatchedVisibleMeshBindingPaths, 16),
                        unmatchedBindingPaths = TruncateArray(x.UnmatchedBindingPaths, 16),
                    })
                    .ToArray();

                return new
                {
                    modelId = modelIdsByIndex[index],
                    candidateCount = candidates.Length,
                    usableCandidateCount = candidates.Count(x => x.modelReadyForAnimation),
                    modelReadyForAnimation = BuildModelAnimationGate(model).Ready,
                    modelAnimationGate = BuildModelAnimationGateJson(BuildModelAnimationGate(model)),
                    candidates,
                };
            }).ToArray();

            var compact = new
            {
                generatedAt = DateTime.UtcNow.ToString("O"),
                version = 1,
                catalog = catalogPath,
                rule = "Normalized compact model-animation index for tools and browsing. Full verbose objects stay in model_animations.json for compatibility and diagnostics.",
                relationRule = "Default candidates are deterministic Unity Animator/Animation references. Structural compatibility data is diagnostic and is not a default binding relation.",
                capabilityRule = "animationCapability only describes the next safe processing path. It is not visual proof; trusted playback requires generated glTF animation channels and validation reports.",
                capabilitySummary = BuildAnimationCapabilitySummary(models, animations),
                relationGraph = Path.Combine(savePath, "unity_relations.jsonl"),
                relationSummary = Path.Combine(savePath, "unity_relation_summary.json"),
                skeletonIndex = Path.Combine(savePath, "skeletons.json"),
                models = compactModels,
                animations = compactAnimations,
                modelAnimationRefs,
            };
            File.WriteAllText(
                Path.Combine(savePath, "model_animations.compact.json"),
                JsonConvert.SerializeObject(compact, Newtonsoft.Json.Formatting.None)
            );
        }

        private static object BuildCompactAvatarSummary(JObject avatar)
        {
            if (avatar == null)
            {
                return null;
            }

            return new
            {
                name = (string)avatar["name"],
                hasHumanDescription = (bool?)avatar["hasHumanDescription"] ?? false,
                humanBoneCount = avatar["humanBones"] is JArray humanBones ? humanBones.Count : 0,
                skeletonBoneCount = avatar["skeletonBones"] is JArray skeletonBones ? skeletonBones.Count : 0,
                humanDescriptionHash = (string)avatar["humanDescriptionHash"],
            };
        }

        private static string[] TruncateArray(string[] values, int maxCount)
        {
            if (values == null || values.Length <= maxCount)
            {
                return values;
            }
            return values.Take(maxCount).ToArray();
        }

        private static void GenerateSkeletonLibraryIndex(string savePath, List<JObject> models, ExplicitAnimationLinks animationLinks)
        {
            var skeletonGroups = models
                .Select(model => new
                {
                    Model = model,
                    Skeleton = model["skeleton"] as JObject,
                    SkeletonId = (string)model["skeleton"]?["libraryId"] ?? (string)model["skeletonHash"],
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.SkeletonId))
                .GroupBy(x => x.SkeletonId, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x.Count())
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var groupModels = group.Select(x => x.Model).ToArray();
                    var browsableModels = groupModels
                        .Where(x => ((int?)x["meshCount"] ?? 0) > 0)
                        .ToArray();
                    var sourceSkeletonModels = groupModels
                        .Where(x => ((int?)x["meshCount"] ?? 0) == 0)
                        .ToArray();
                    var visibleModels = browsableModels.Length > 0 ? browsableModels : groupModels;
                    var firstSkeleton = group.Select(x => x.Skeleton).FirstOrDefault(x => x != null);
                    var candidateLinks = groupModels
                        .SelectMany(model =>
                        {
                            var modelKey = GetCatalogKey(model);
                            return animationLinks.ByModel.TryGetValue(modelKey, out var links)
                                ? links
                                : Enumerable.Empty<ExplicitModelAnimationLink>();
                        })
                        .GroupBy(x => GetCatalogKey(x.Animation), StringComparer.OrdinalIgnoreCase)
                        .Select(x => x
                            .OrderByDescending(link => link.Score)
                            .ThenBy(link => link.ModelName, StringComparer.OrdinalIgnoreCase)
                            .First())
                        .OrderByDescending(x => x.Score)
                        .ThenBy(x => (string)x.Animation["name"], StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    return new
                    {
                        skeletonId = group.Key,
                        relationBasis = (string)firstSkeleton?["relationBasis"],
                        fingerprintVersion = (int?)firstSkeleton?["fingerprintVersion"] ?? 1,
                        hashes = new
                        {
                            namePathHash = (string)firstSkeleton?["namePathHash"],
                            hierarchyHash = (string)firstSkeleton?["hierarchyHash"],
                            bindPoseHash = (string)firstSkeleton?["bindPoseHash"],
                            avatarHumanHash = (string)firstSkeleton?["avatarHumanHash"],
                            avatarSkeletonNameHash = (string)firstSkeleton?["avatarSkeletonNameHash"],
                        },
                        modelCount = browsableModels.Length,
                        sourceSkeletonCount = sourceSkeletonModels.Length,
                        totalIndexedModelLikeCount = groupModels.Length,
                        resourceKinds = visibleModels
                            .Select(x => (string)x["resourceKind"] ?? "Unknown")
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                            .ToArray(),
                        models = visibleModels
                            .OrderBy(x => (string)x["name"], StringComparer.OrdinalIgnoreCase)
                            .Select(model => new
                            {
                                name = (string)model["name"],
                                resourceKind = (string)model["resourceKind"],
                                output = (string)model["output"],
                                source = (string)model["source"],
                                container = (string)model["container"],
                                boneCount = (int?)model["boneCount"] ?? 0,
                                meshCount = (int?)model["meshCount"] ?? 0,
                                textureCount = (int?)model["textureCount"] ?? 0,
                                avatar = model["avatar"] is JObject avatar ? new
                                {
                                    name = (string)avatar["name"],
                                    humanBoneCount = (int?)avatar["humanBoneCount"] ?? 0,
                                    skeletonBoneCount = (int?)avatar["skeletonBoneCount"] ?? 0,
                                } : null,
                            })
                            .ToArray(),
                        animationCandidateCount = candidateLinks.Length,
                        animationCandidates = candidateLinks
                            .Take(512)
                            .Select(link => new
                            {
                                name = (string)link.Animation["name"],
                                resourceKind = (string)link.Animation["resourceKind"],
                                output = (string)link.Animation["output"],
                                animationAsset = (string)link.Animation["animationAsset"],
                                source = (string)link.Animation["source"],
                                container = (string)link.Animation["container"],
                                animationType = (string)link.Animation["animationType"],
                                score = link.Score,
                                confidence = link.Confidence,
                                relation = link.Relation,
                                requiresHumanoidBake = false,
                                legacyUnityBakeSupported = link.RequiresHumanoidBake,
                                requiresInternalHumanoidSolve = link.RequiresInternalHumanoidSolve,
                                matchedModel = link.ModelName,
                            })
                            .ToArray(),
                    };
                })
                .ToArray();

            var index = new
            {
                generatedAt = DateTime.UtcNow.ToString("O"),
                rule = "Skeleton groups are generated from Unity-derived skeleton fingerprints. They are the preferred bridge between clean model library assets and reusable animation assets.",
                skeletonCount = skeletonGroups.Length,
                skeletons = skeletonGroups,
            };
            File.WriteAllText(
                Path.Combine(savePath, "skeletons.json"),
                JsonConvert.SerializeObject(index, Newtonsoft.Json.Formatting.Indented)
            );
        }

        private static ExplicitAnimationLinks BuildExplicitUnityAnimationLinks(List<JObject> models, List<JObject> animations)
        {
            var links = new ExplicitAnimationLinks();
            var modelEntries = models
                .GroupBy(GetCatalogKey)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            var animationEntries = animations
                .GroupBy(GetCatalogKey)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            var modelItems = exportableAssets
                .Where(x => x?.Asset is GameObject or Animator)
                .Where(x => modelEntries.ContainsKey(GetAssetKey(x.Asset)))
                .ToList();

            foreach (var item in modelItems)
            {
                var modelKey = GetAssetKey(item.Asset);
                if (!modelEntries.TryGetValue(modelKey, out var modelEntry))
                {
                    continue;
                }

                foreach (var relation in CollectExplicitAnimationRelations(item.Asset))
                {
                    var animationKey = GetAssetKey(relation.Clip);
                    if (!animationEntries.TryGetValue(animationKey, out var animationEntry))
                    {
                        continue;
                    }

                    var link = BuildExplicitLink(modelEntry, animationEntry, relation);
                    links.Add(modelKey, animationKey, link);
                }
            }

            return links;
        }

        private static ExplicitAnimationLinks BuildStructuralUnityAnimationLinks(List<JObject> models, List<JObject> animations)
        {
            var links = BuildExplicitUnityAnimationLinks(models, animations);
            return links;
        }

        private static ExplicitAnimationLinks BuildStructuralUnityAnimationLinksForLibrary(List<JObject> models, List<JObject> animations)
        {
            // 显式 Animator/Animation 引用是 Unity 已经给出的关系，成本低、可信度高。
            // 即使大库跳过结构型全矩阵匹配，也不能把这部分真实绑定一起跳过。
            var links = BuildExplicitUnityAnimationLinks(models, animations);
            var pairCount = (long)models.Count * animations.Count;
            if (pairCount > FullStructuralAnimationPairLimit)
            {
                Logger.Warning(
                    $"Skipped structural model-animation matching for Library index: {models.Count} model(s) x {animations.Count} animation(s) = {pairCount} pair(s), limit {FullStructuralAnimationPairLimit}. Kept {links.ByModel.Count} model(s) with explicit Unity animation reference(s). Default Library candidates must come from deterministic Unity references, not bone-count or skeleton-compatible fallback."
                );
                links.MatchingDeferred = true;
                links.DeferredReason = $"Large Library index has {pairCount} model-animation pairs, above limit {FullStructuralAnimationPairLimit}. Explicit Unity Animator/Animation references were kept; structural skeleton/binding fallback is not emitted as a default model-animation relation.";
                links.PairCount = pairCount;
                links.PairLimit = FullStructuralAnimationPairLimit;
                return links;
            }

            return links;
        }

        private static IEnumerable<ExplicitAnimationRelation> CollectExplicitAnimationRelations(AnimeStudio.Object modelAsset)
        {
            var gameObject = modelAsset switch
            {
                Animator animator when animator.m_GameObject.TryGet(out var owner) => owner,
                GameObject go => go,
                _ => null,
            };

            if (modelAsset is Animator directAnimator)
            {
                foreach (var relation in CollectAnimatorClipRelations(directAnimator, "animator.controller"))
                {
                    yield return relation;
                }
            }

            if (gameObject == null)
            {
                yield break;
            }

            foreach (var owner in EnumerateGameObjectHierarchy(gameObject))
            {
                foreach (var componentPtr in owner.m_Components ?? Enumerable.Empty<PPtr<Component>>())
                {
                    if (componentPtr.TryGet<Animator>(out var animator))
                    {
                        var relationName = owner == gameObject
                            ? "gameObject.animator.controller"
                            : "childGameObject.animator.controller";
                        foreach (var relation in CollectAnimatorClipRelations(animator, relationName))
                        {
                            yield return relation;
                        }
                    }

                    if (componentPtr.TryGet<Animation>(out var animation))
                    {
                        var relationName = owner == gameObject
                            ? "animation.clip"
                            : "childGameObject.animation.clip";
                        foreach (var clipPtr in animation.m_Animations ?? Enumerable.Empty<PPtr<AnimationClip>>())
                        {
                            if (clipPtr.TryGet(out var clip))
                            {
                                yield return new ExplicitAnimationRelation
                                {
                                    Clip = clip,
                                    Relation = relationName,
                                    Reasons = new[] { "Unity Animation component explicitly references this AnimationClip." },
                                };
                            }
                        }
                    }
                }
            }
        }

        private static IEnumerable<GameObject> EnumerateGameObjectHierarchy(GameObject root)
        {
            if (root == null)
            {
                yield break;
            }

            yield return root;
            var transform = root.m_Transform;
            foreach (var childPtr in transform?.m_Children ?? Enumerable.Empty<PPtr<Transform>>())
            {
                if (childPtr.TryGet(out var childTransform) && childTransform.m_GameObject.TryGet(out var child))
                {
                    foreach (var nested in EnumerateGameObjectHierarchy(child))
                    {
                        yield return nested;
                    }
                }
            }
        }

        private static IEnumerable<ExplicitAnimationRelation> CollectAnimatorClipRelations(Animator animator, string relationName)
        {
            if (!animator.m_Controller.TryGet<RuntimeAnimatorController>(out var controller))
            {
                yield break;
            }

            foreach (var clip in CollectRuntimeControllerClips(controller))
            {
                yield return new ExplicitAnimationRelation
                {
                    Clip = clip,
                    Relation = relationName,
                    Reasons = new[] { "Unity AnimatorController explicitly references this AnimationClip." },
                };
            }
        }

        private static IEnumerable<AnimationClip> CollectRuntimeControllerClips(RuntimeAnimatorController controller)
        {
            switch (controller)
            {
                case AnimatorController animatorController:
                    foreach (var clipPtr in animatorController.m_AnimationClips ?? Enumerable.Empty<PPtr<AnimationClip>>())
                    {
                        if (clipPtr.TryGet(out var clip))
                        {
                            yield return clip;
                        }
                    }
                    break;
                case AnimatorOverrideController overrideController:
                    var overriddenOriginalClips = new HashSet<AnimationClip>();
                    var selectedOverrideClips = new List<AnimationClip>();
                    foreach (var pair in overrideController.m_Clips ?? Enumerable.Empty<AnimationClipOverride>())
                    {
                        var hasOriginal = pair.m_OriginalClip.TryGet(out var originalClip);
                        var hasOverride = pair.m_OverrideClip.TryGet(out var overrideClip);
                        if (hasOverride)
                        {
                            if (hasOriginal)
                            {
                                overriddenOriginalClips.Add(originalClip);
                            }
                            selectedOverrideClips.Add(overrideClip);
                        }
                        else if (hasOriginal)
                        {
                            selectedOverrideClips.Add(originalClip);
                        }
                    }

                    if (overrideController.m_Controller.TryGet<RuntimeAnimatorController>(out var baseController))
                    {
                        foreach (var clip in CollectRuntimeControllerClips(baseController))
                        {
                            // OverrideController 的原始 clip 被替换后不应再作为该模型的默认候选。
                            if (!overriddenOriginalClips.Contains(clip))
                            {
                                yield return clip;
                            }
                        }
                    }

                    foreach (var clip in selectedOverrideClips.Distinct())
                    {
                        yield return clip;
                    }
                    break;
            }
        }

        private static ExplicitModelAnimationLink BuildExplicitLink(JObject model, JObject animation, ExplicitAnimationRelation relation)
        {
            var usesNodeBindingPaths = ShouldUseNodeBindingPathsForAnimationMatch(model);
            var animationPaths = animation["transformBindingPaths"]?.ToObject<string[]>() ?? Array.Empty<string>();
            var match = AnalyzeStructuralAnimationMatch(GetModelAnimationBindingPaths(model), animationPaths, usesNodeBindingPaths);
            var matchedPaths = match.MatchedPaths?.Length > 0 ? match.MatchedPaths : null;
            var matchedVisibleMeshPaths = matchedPaths != null
                ? GetMatchedVisibleMeshBindingPaths(model, matchedPaths)
                : null;
            var unmatchedPaths = match.UnmatchedPaths?.Length > 0 ? match.UnmatchedPaths : null;
            var reasons = relation.Reasons ?? Array.Empty<string>();
            var requiresInternalHumanoidSolve = IsHumanoidCharacterTarget(model) && IsHumanoidAnimationAsset(animation);
            var modelGate = BuildModelAnimationGate(model);
            if (match.MatchedPathCount > 0)
            {
                reasons = reasons
                    .Concat(new[]
                    {
                        $"Explicit Unity reference also matches {match.MatchedPathCount} AnimationClip binding path(s) to exported model node/bone paths.",
                    })
                    .ToArray();
            }

            return new ExplicitModelAnimationLink
            {
                ModelName = (string)model["name"],
                ModelResourceKind = (string)model["resourceKind"],
                ModelOutput = (string)model["output"],
                ModelSkeletonHash = (string)model["skeletonHash"],
                ModelBoneCount = (int?)model["boneCount"] ?? 0,
                ModelMeshCount = (int?)model["meshCount"] ?? 0,
                ModelTextureCount = (int?)model["textureCount"] ?? 0,
                ModelReadyForAnimation = modelGate.Ready,
                ModelAnimationBlockedReason = modelGate.Ready ? null : string.Join(",", modelGate.Reasons),
                ModelAnimationGate = modelGate,
                Animation = animation,
                Relation = relation.Relation,
                Source = "explicit",
                Score = 100,
                Confidence = "explicit_unity_reference",
                Reasons = reasons,
                MatchedBindingPaths = matchedPaths,
                MatchedVisibleMeshBindingPaths = matchedVisibleMeshPaths,
                UnmatchedBindingPaths = unmatchedPaths,
                RequiresHumanoidBake = requiresInternalHumanoidSolve,
                RequiresInternalHumanoidSolve = requiresInternalHumanoidSolve,
            };
        }

        private static object BuildBindingAsset(JObject entry)
        {
            return new
            {
                name = (string)entry["name"],
                resourceKind = (string)entry["resourceKind"],
                output = (string)entry["output"],
                animationAsset = (string)entry["animationAsset"],
                source = (string)entry["source"],
                sampleRate = (float?)entry["sampleRate"],
                duration = (float?)entry["duration"],
                curveCount = (int?)entry["curveCount"] ?? 0,
                animationType = (string)entry["animationType"],
                animationCapability = ClassifyAnimationCapability(entry, null),
                hasMuscleClip = (bool?)entry["hasMuscleClip"],
                coreTransformBindingCount = (int?)entry["coreTransformBindingCount"],
                humanoidBindingCount = (int?)entry["humanoidBindingCount"],
                blendShapeBindingCount = (int?)entry["blendShapeBindingCount"],
                trueBlendShapeBindingCount = (int?)entry["trueBlendShapeBindingCount"],
                rendererMaterialBindingCount = (int?)entry["rendererMaterialBindingCount"],
                rendererPropertyBindingCount = (int?)entry["rendererPropertyBindingCount"],
                activeStateBindingCount = (int?)entry["activeStateBindingCount"],
                auxiliaryBindingCount = (int?)entry["auxiliaryBindingCount"],
                transformBindingPaths = TruncateArray(entry["transformBindingPaths"]?.ToObject<string[]>(), 64),
                transformBindingPathsTruncated = (entry["transformBindingPaths"] as JArray)?.Count > 64,
                classificationNotes = entry["classificationNotes"]?.ToObject<string[]>(),
            };
        }

        private static object BuildModelBindingAsset(JObject entry)
        {
            return new
            {
                name = (string)entry["name"],
                resourceKind = (string)entry["resourceKind"],
                output = (string)entry["output"],
                source = (string)entry["source"],
                container = (string)entry["container"],
                skeletonHash = (string)entry["skeletonHash"],
                skeleton = entry["skeleton"],
                boneCount = (int?)entry["boneCount"] ?? 0,
                meshCount = (int?)entry["meshCount"] ?? 0,
                textureCount = (int?)entry["textureCount"] ?? 0,
                animationCount = (int?)entry["animationCount"] ?? 0,
                bonePaths = TruncateArray(entry["bonePaths"]?.ToObject<string[]>(), 64),
                bonePathsTruncated = (entry["bonePaths"] as JArray)?.Count > 64,
                nodePaths = TruncateArray(entry["nodePaths"]?.ToObject<string[]>(), 96),
                nodePathsTruncated = (entry["nodePaths"] as JArray)?.Count > 96,
                meshPaths = TruncateArray(entry["meshPaths"]?.ToObject<string[]>(), 64),
                meshPathsTruncated = (entry["meshPaths"] as JArray)?.Count > 64,
                avatar = entry["avatar"],
            };
        }

        private static ModelAnimationCandidate BuildExplicitModelAnimationCandidate(ExplicitModelAnimationLink link)
        {
            var animation = link.Animation;
            return new ModelAnimationCandidate
            {
                name = (string)animation["name"],
                resourceKind = (string)animation["resourceKind"],
                output = (string)animation["output"],
                animationAsset = (string)animation["animationAsset"],
                source = (string)animation["source"],
                container = (string)animation["container"],
                sampleRate = (float?)animation["sampleRate"],
                duration = (float?)animation["duration"],
                curveCount = (int?)animation["curveCount"] ?? 0,
                eventCount = (int?)animation["eventCount"] ?? 0,
                legacy = (bool?)animation["legacy"],
                animationType = (string)animation["animationType"],
                animationCapability = ClassifyAnimationCapability(animation, link),
                hasMuscleClip = (bool?)animation["hasMuscleClip"],
                coreTransformBindingCount = (int?)animation["coreTransformBindingCount"],
                humanoidBindingCount = (int?)animation["humanoidBindingCount"],
                blendShapeBindingCount = (int?)animation["blendShapeBindingCount"],
                trueBlendShapeBindingCount = (int?)animation["trueBlendShapeBindingCount"],
                rendererMaterialBindingCount = (int?)animation["rendererMaterialBindingCount"],
                rendererPropertyBindingCount = (int?)animation["rendererPropertyBindingCount"],
                activeStateBindingCount = (int?)animation["activeStateBindingCount"],
                auxiliaryBindingCount = (int?)animation["auxiliaryBindingCount"],
                classificationNotes = animation["classificationNotes"]?.ToObject<string[]>(),
                confidence = link.Confidence,
                relation = link.Relation,
                relationSource = link.Source,
                score = link.Score,
                matchReasons = link.Reasons,
                semanticModelTags = link.SemanticModelTags,
                semanticAnimationTags = link.SemanticAnimationTags,
                semanticSharedTags = link.SemanticSharedTags,
                semanticRejectedTags = link.SemanticRejectedTags,
                matchedBindingPaths = TruncateArray(link.MatchedBindingPaths, 32),
                matchedBindingPathsTruncated = link.MatchedBindingPaths?.Length > 32,
                matchedVisibleMeshBindingPaths = TruncateArray(link.MatchedVisibleMeshBindingPaths, 32),
                matchedVisibleMeshBindingPathsTruncated = link.MatchedVisibleMeshBindingPaths?.Length > 32,
                unmatchedBindingPaths = TruncateArray(link.UnmatchedBindingPaths, 32),
                unmatchedBindingPathsTruncated = link.UnmatchedBindingPaths?.Length > 32,
                requiresHumanoidBake = false,
                legacyUnityBakeSupported = link.RequiresHumanoidBake,
                requiresInternalHumanoidSolve = link.RequiresInternalHumanoidSolve,
                modelReadyForAnimation = link.ModelReadyForAnimation,
                modelAnimationBlockedReason = link.ModelAnimationBlockedReason,
                modelAnimationGate = BuildModelAnimationGateJson(link.ModelAnimationGate),
                directGltfPreviewReady = link.ModelReadyForAnimation,
                productionAnimationReady = false,
                productionAnimationBlockedReason = link.ModelReadyForAnimation ? null : "model_not_animation_ready",
                verification = new
                {
                    status = link.Confidence,
                    channelCount = (int?)null,
                    note = BuildAnimationCapabilityNote(animation, link),
                },
                nextAction = link.ModelReadyForAnimation ? GetAnimationNextAction(animation, link) : "fix_model_first",
            };
        }

        private static object BuildAnimationCapabilitySummary(List<JObject> models, List<JObject> animations)
        {
            var animationCapabilities = animations
                .GroupBy(x => ClassifyAnimationCapability(x, null), StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
            var modelCapabilities = models
                .GroupBy(ClassifyModelAnimationTarget, StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
            return new
            {
                animationCapabilities,
                modelCapabilities,
                rule = "Humanoid body motion, Transform body motion, BlendShape/face motion, non-character Transform motion, and legacy/material/event motion are separate implementation paths.",
            };
        }

        private static string ClassifyModelAnimationTarget(JObject model)
        {
            var resourceKind = (string)model["resourceKind"] ?? string.Empty;
            var avatar = model["avatar"] as JObject;
            if (avatar != null && ((int?)avatar["humanBoneCount"] ?? 0) > 0)
            {
                return "HumanoidCharacterTarget";
            }
            if (((int?)model["boneCount"] ?? 0) > 0)
            {
                var hasAvatarContext = avatar != null && ((bool?)avatar["hasHumanDescription"] ?? false);
                return string.Equals(resourceKind, "Character", StringComparison.OrdinalIgnoreCase)
                    || (hasAvatarContext && LooksLikeHumanCharacterSkeleton(model))
                    ? "GenericCharacterSkeletonTarget"
                    : "NonCharacterSkeletonTarget";
            }
            return string.Equals(resourceKind, "Character", StringComparison.OrdinalIgnoreCase)
                ? "CharacterStaticTarget"
                : "NonCharacterStaticTarget";
        }

        private static string ClassifyAnimationCapability(JObject animation, ExplicitModelAnimationLink link)
        {
            var animationType = (string)animation["animationType"] ?? "UnknownAnimation";
            var resourceKind = (string)animation["resourceKind"] ?? string.Empty;
            var legacy = (bool?)animation["legacy"] ?? false;
            var trueBlendShapeCount = (int?)animation["trueBlendShapeBindingCount"] ?? 0;
            var rendererMaterialCount = (int?)animation["rendererMaterialBindingCount"] ?? 0;
            var rendererPropertyCount = (int?)animation["rendererPropertyBindingCount"] ?? 0;
            var activeStateCount = (int?)animation["activeStateBindingCount"] ?? 0;
            var coreTransformCount = (int?)animation["coreTransformBindingCount"] ?? 0;
            var humanoidBindingCount = (int?)animation["humanoidBindingCount"] ?? 0;
            var transformBindingCount = (int?)animation["transformBindingCount"] ?? 0;
            var curveCount = (int?)animation["curveCount"] ?? 0;
            var hasMuscleClip = (bool?)animation["hasMuscleClip"] ?? false;
            var duration = (float?)animation["duration"];
            var isCharacter = string.Equals(resourceKind, "Character", StringComparison.OrdinalIgnoreCase);
            var isHumanoidLike = hasMuscleClip || humanoidBindingCount > 0;
            var isTransformAnimation = string.Equals(animationType, "TransformAnimation", StringComparison.OrdinalIgnoreCase);
            var isAuxiliaryAnimation = string.Equals(animationType, "AuxiliaryAnimation", StringComparison.OrdinalIgnoreCase);
            var isTransformBodyAnimation = string.Equals(animationType, "TransformBodyAnimation", StringComparison.OrdinalIgnoreCase);
            var isMixedHumanoidTransform = string.Equals(animationType, "MixedHumanoidTransform", StringComparison.OrdinalIgnoreCase);
            var isTransformLike = isTransformAnimation || isAuxiliaryAnimation || isTransformBodyAnimation || isMixedHumanoidTransform;

            if (trueBlendShapeCount > 0)
            {
                return legacy ? "BlendShapeLegacyNotImplemented" : "BlendShapePreviewReady";
            }
            if (rendererMaterialCount > 0)
            {
                return "MaterialAnimationNotMapped";
            }
            if (activeStateCount > 0)
            {
                return "ActiveStateAnimationNotMapped";
            }
            if (rendererPropertyCount > 0)
            {
                return "RendererPropertyAnimationNotMapped";
            }
            if (legacy)
            {
                return "LegacyNotPlayableYet";
            }
            if (duration.HasValue && duration.Value <= 0.0001f && isTransformLike)
            {
                return "StaticPoseOnly";
            }
            if (hasMuscleClip
                && humanoidBindingCount == 0
                && transformBindingCount == 0
                && curveCount == 0
                && trueBlendShapeCount == 0
                && rendererMaterialCount == 0
                && rendererPropertyCount == 0
                && activeStateCount == 0)
            {
                return "EmptyHumanoidClip";
            }

            if (!isCharacter
                && link?.MatchedBindingPaths != null
                && link.MatchedBindingPaths.Length > 0
                && link.MatchedVisibleMeshBindingPaths != null
                && link.MatchedVisibleMeshBindingPaths.Length > 0
                && isTransformLike)
            {
                return "NonCharacterTransformPreviewReady";
            }

            if (!isCharacter && (isTransformAnimation || isAuxiliaryAnimation))
            {
                return "NonCharacterTransformNeedsMapping";
            }

            if (isMixedHumanoidTransform
                && coreTransformCount >= 3
                && link?.MatchedBindingPaths != null
                && link.MatchedBindingPaths.Length >= 4)
            {
                // Endfield 这类 clip 会同时带 Humanoid/Muscle 和直接 Transform TRS。
                // 有确定模型绑定时，先把可直接播放的 TRS 写进 glTF；Muscle 求解另行诊断，
                // 不能让未验收的 Humanoid solver 覆盖已经解出的骨骼曲线。
                return "TransformBodyPreviewReady";
            }
            if (isTransformBodyAnimation && coreTransformCount >= 3)
            {
                return isCharacter
                    ? "TransformBodyPreviewReady"
                    : "NonCharacterTransformNeedsMapping";
            }
            if (link?.RequiresHumanoidBake == true || (isCharacter && isHumanoidLike))
            {
                return "HumanoidBodyNeedsInternalSolver";
            }
            if (isAuxiliaryAnimation || isTransformAnimation)
            {
                return isCharacter
                    ? "AuxiliaryTransformNeedsMapping"
                    : "NonCharacterTransformNeedsMapping";
            }

            return "UnknownNeedsInspection";
        }

        private static bool ShouldUseNodeBindingPathsForAnimationMatch(JObject model)
        {
            var modelResourceKind = (string)model["resourceKind"] ?? string.Empty;
            return GetModelBonePaths(model).Length == 0
                || (!IsHumanoidCharacterTarget(model)
                    && !string.Equals(modelResourceKind, "Character", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsHumanoidCharacterTarget(JObject model)
        {
            return string.Equals(ClassifyModelAnimationTarget(model), "HumanoidCharacterTarget", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsGenericCharacterSkeletonTarget(JObject model)
        {
            return string.Equals(ClassifyModelAnimationTarget(model), "GenericCharacterSkeletonTarget", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeHumanCharacterSkeleton(JObject model)
        {
            var keys = GetModelBonePaths(model)
                .SelectMany(GetComparableBonePathKeys)
                .Select(NormalizeAnimationPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            static bool HasAny(HashSet<string> values, params string[] names)
            {
                return names.Any(name => values.Contains(NormalizeAnimationPath(name)));
            }

            var hasSpine = HasAny(keys, "spine", "spine_jnt", "bip001 spine", "bip001 spine1");
            var hasHead = HasAny(keys, "head", "head_jnt", "bip001 head");
            var hasLeftArm = HasAny(keys, "left_shoulder_jnt", "left_elbow_jnt", "left_wrist_jnt", "bip001 l upperarm", "bip001 l forearm", "bip001 l hand");
            var hasRightArm = HasAny(keys, "right_shoulder_jnt", "right_elbow_jnt", "right_wrist_jnt", "bip001 r upperarm", "bip001 r forearm", "bip001 r hand");
            var hasLeftLeg = HasAny(keys, "left_hip_jnt", "left_knee_jnt", "left_ankle_jnt", "bip001 l thigh", "bip001 l calf", "bip001 l foot");
            var hasRightLeg = HasAny(keys, "right_hip_jnt", "right_knee_jnt", "right_ankle_jnt", "bip001 r thigh", "bip001 r calf", "bip001 r foot");

            return hasSpine && hasHead && hasLeftArm && hasRightArm && hasLeftLeg && hasRightLeg;
        }

        private static bool IsHumanoidAnimationAsset(JObject animation)
        {
            var hasMuscleClip = (bool?)animation["hasMuscleClip"] ?? false;
            var humanoidBindingCount = (int?)animation["humanoidBindingCount"] ?? 0;
            var animationType = (string)animation["animationType"] ?? string.Empty;
            return hasMuscleClip
                || humanoidBindingCount > 0
                || animationType.IndexOf("Humanoid", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string GetAnimationNextAction(JObject animation, ExplicitModelAnimationLink link)
        {
            return ClassifyAnimationCapability(animation, link) switch
            {
                "HumanoidBodyNeedsInternalSolver" => "generate_preview_gltf",
                "TransformBodyPreviewReady" => "generate_preview_gltf",
                "BlendShapePreviewReady" => "generate_preview_gltf",
                "NonCharacterTransformPreviewReady" => "generate_preview_gltf",
                "StaticPoseOnly" => "treat_as_static_model",
                "EmptyHumanoidClip" => "treat_as_empty_animation_marker",
                "MaterialAnimationNotMapped" => "implement_material_animation_mapping",
                "ActiveStateAnimationNotMapped" => "implement_visibility_animation_mapping",
                "RendererPropertyAnimationNotMapped" => "inspect_renderer_property_animation",
                "BlendShapeLegacyNotImplemented" => "implement_blendshape_animation_export",
                "LegacyNotPlayableYet" => "implement_legacy_clip_sampling",
                "NonCharacterTransformNeedsMapping" => "implement_non_character_transform_mapping",
                "AuxiliaryTransformNeedsMapping" => "inspect_auxiliary_transform_targets",
                _ => "inspect_animation_bindings",
            };
        }

        private static string BuildAnimationCapabilityNote(JObject animation, ExplicitModelAnimationLink link)
        {
            return ClassifyAnimationCapability(animation, link) switch
            {
                "HumanoidBodyNeedsInternalSolver" => "Humanoid/Avatar body motion needs AnimeStudio's internal Humanoid/Muscle to skeleton TRS solver before direct glTF playback can be trusted.",
                "TransformBodyPreviewReady" => "Transform body bindings look playable without Humanoid retargeting, but the preview glTF report must still validate target channels.",
                "BlendShapePreviewReady" => "BlendShape/morph animation can be written as glTF morph targets and weights channels; preview validation must confirm morph target and weights channel counts.",
                "NonCharacterTransformPreviewReady" => "Non-character Transform animation has Unity binding paths matched to exported glTF nodes; generate a preview glTF and validate channel targets.",
                "StaticPoseOnly" => "This Transform clip has no effective duration; keep it as a static pose/static model signal instead of exposing it as playable animation.",
                "EmptyHumanoidClip" => "This Humanoid/Muscle clip carries timing/marker metadata but no serialized curve payload or bindings; keep the explicit Unity relation, but do not expose it as playable body animation.",
                "MaterialAnimationNotMapped" => "Renderer material curves are captured, but direct glTF material animation mapping is not implemented yet.",
                "ActiveStateAnimationNotMapped" => "Unity active-state curves are captured, but glTF visibility mapping is not implemented yet.",
                "RendererPropertyAnimationNotMapped" => "Renderer property curves are captured, but they are not BlendShape weights and need separate inspection.",
                "BlendShapeLegacyNotImplemented" => "This looks like legacy face/blendshape animation. It needs legacy clip sampling before morph target channel export.",
                "LegacyNotPlayableYet" => "This clip is legacy; Unity Playables cannot use it directly in the current helper path.",
                "NonCharacterTransformNeedsMapping" => "This is non-character or low-core Transform animation. It needs original prefab/node path mapping before glTF playback can be trusted.",
                "AuxiliaryTransformNeedsMapping" => "Bindings mainly target helper/socket/auxiliary nodes; inspect targets before exposing as body animation.",
                _ => "Animation type is not mapped to a trusted export path yet.",
            };
        }

        private static string[] GetModelBonePaths(JObject model)
        {
            return model["bonePaths"]?.ToObject<string[]>()?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? Array.Empty<string>();
        }

        private static string[] GetModelNodePaths(JObject model)
        {
            return model["nodePaths"]?.ToObject<string[]>()?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? Array.Empty<string>();
        }

        private static string[] GetModelAnimationBindingPaths(JObject model)
        {
            var bonePaths = GetModelBonePaths(model);
            var resourceKind = (string)model["resourceKind"] ?? string.Empty;
            if (!string.Equals(resourceKind, "Character", StringComparison.OrdinalIgnoreCase))
            {
                return bonePaths
                    .Concat(GetModelNodePaths(model))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            if (bonePaths.Length > 0)
            {
                return bonePaths;
            }

            return GetModelNodePaths(model);
        }

        private static string[] GetModelMeshPaths(JObject model)
        {
            return model["meshPaths"]?.ToObject<string[]>()?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? Array.Empty<string>();
        }

        private static string[] GetMatchedVisibleMeshBindingPaths(JObject model, string[] matchedBindingPaths)
        {
            var meshPaths = GetModelMeshPaths(model);
            if (meshPaths.Length == 0 || matchedBindingPaths == null || matchedBindingPaths.Length == 0)
            {
                return Array.Empty<string>();
            }

            return matchedBindingPaths
                .Where(bindingPath => meshPaths.Any(meshPath => BindingPathAffectsMesh(bindingPath, meshPath)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static AssetItem[] FilterHierarchyModelRoots(AssetItem[] assets, out int skippedChildren)
        {
            skippedChildren = 0;
            var modelRoots = assets
                .Where(x => x.Asset is GameObject)
                .Select(x => (GameObject)x.Asset)
                .ToHashSet();
            if (modelRoots.Count == 0)
            {
                return assets;
            }

            var filtered = new List<AssetItem>(assets.Length);
            foreach (var asset in assets)
            {
                if (asset.Asset is not GameObject gameObject)
                {
                    filtered.Add(asset);
                    continue;
                }

                if (HasAncestorInCandidateSet(gameObject, modelRoots))
                {
                    skippedChildren++;
                    continue;
                }

                filtered.Add(asset);
            }

            return filtered.ToArray();
        }

        private static bool HasAncestorInCandidateSet(GameObject gameObject, HashSet<GameObject> candidates)
        {
            var transform = gameObject.m_Transform;
            var visited = new HashSet<long>();
            while (transform?.m_Father != null && !transform.m_Father.IsNull)
            {
                if (!transform.m_Father.TryGet(out var parent))
                {
                    return false;
                }

                if (!visited.Add(parent.m_PathID))
                {
                    return false;
                }

                if (parent.m_GameObject.TryGet(out var parentGameObject) && candidates.Contains(parentGameObject))
                {
                    return true;
                }

                transform = parent;
            }

            return false;
        }

        private static bool BindingPathAffectsMesh(string bindingPath, string meshPath)
        {
            var bindingKeys = GetComparableBonePathKeys(bindingPath).ToArray();
            var meshKeys = GetComparableBonePathKeys(meshPath).ToArray();
            foreach (var bindingKey in bindingKeys)
            {
                foreach (var meshKey in meshKeys)
                {
                    if (string.Equals(meshKey, bindingKey, StringComparison.OrdinalIgnoreCase)
                        || meshKey.StartsWith(bindingKey + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static StructuralAnimationMatch AnalyzeStructuralAnimationMatch(string[] modelBindingPaths, string[] animationPaths, bool allowNodePathMatch)
        {
            var modelKeys = modelBindingPaths
                .SelectMany(GetComparableBonePathKeys)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var normalizedAnimationPaths = animationPaths
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var matched = new List<string>();
            var unmatched = new List<string>();
            foreach (var path in normalizedAnimationPaths)
            {
                var keys = GetComparableBonePathKeys(path);
                if (keys.Any(modelKeys.Contains))
                {
                    matched.Add(path);
                }
                else
                {
                    unmatched.Add(path);
                }
            }

            var coreMatched = matched.Count(IsCoreAnimationPath);
            var score = allowNodePathMatch
                ? Math.Min(80, 50 + matched.Count * 10)
                : Math.Min(95, coreMatched * 10 + matched.Count * 2);
            var isCandidate = allowNodePathMatch
                ? matched.Count > 0
                : matched.Count >= 4 && coreMatched >= 2;
            return new StructuralAnimationMatch(
                isCandidate,
                score,
                matched.Count,
                coreMatched,
                matched.Take(64).ToArray(),
                unmatched.Take(32).ToArray()
            );
        }

        private static HumanoidAnimationMatch AnalyzeHumanoidAnimationMatch(JObject model, JObject animation)
        {
            var avatar = model["avatar"] as JObject;
            var hasHumanAvatar = avatar != null && ((bool?)avatar["hasHumanDescription"] ?? false);
            var humanoidBindingCount = (int?)animation["humanoidBindingCount"] ?? 0;
            var hasMuscleClip = (bool?)animation["hasMuscleClip"] ?? false;
            var isCandidate = hasHumanAvatar && (humanoidBindingCount > 0 || hasMuscleClip);
            var score = isCandidate ? Math.Min(80, 50 + Math.Min(30, humanoidBindingCount / 5)) : 0;
            return new HumanoidAnimationMatch(
                isCandidate,
                score,
                (string)avatar?["name"],
                humanoidBindingCount
            );
        }

        private static bool IsStructuralBindingResourceCompatible(JObject model, JObject animation)
        {
            var modelResourceKind = ((string)model["resourceKind"] ?? string.Empty).Trim();
            var modelAnimationTarget = ClassifyModelAnimationTarget(model);
            return IsStructuralBindingResourceCompatible(modelAnimationTarget, modelResourceKind, animation);
        }

        private static bool IsStructuralBindingResourceCompatible(string modelAnimationTarget, string modelResourceKind, JObject animation)
        {
            var animationResourceKind = ((string)animation["resourceKind"] ?? string.Empty).Trim();
            if (string.Equals(modelAnimationTarget, "HumanoidCharacterTarget", StringComparison.OrdinalIgnoreCase)
                && IsHumanoidAnimationAsset(animation))
            {
                return true;
            }
            if (string.Equals(modelAnimationTarget, "GenericCharacterSkeletonTarget", StringComparison.OrdinalIgnoreCase)
                && IsTransformBodyAnimationAsset(animation))
            {
                return true;
            }

            if (IsUnknownResourceKind(modelResourceKind) || IsUnknownResourceKind(animationResourceKind))
            {
                return false;
            }

            return string.Equals(modelResourceKind, animationResourceKind, StringComparison.OrdinalIgnoreCase);
        }

        private static AnimationSemanticMatch AnalyzeAnimationSemanticMatch(JObject model, JObject animation, bool requireStrictAttachmentSemantics)
        {
            var modelTags = ExtractAnimationSemanticTags(model, includeModelHierarchy: true);
            var animationTags = ExtractAnimationSemanticTags(animation, includeModelHierarchy: false);
            var modelWeaponTags = modelTags.Where(IsWeaponSemanticTag).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var animationWeaponTags = animationTags.Where(IsWeaponSemanticTag).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sharedWeaponTags = modelWeaponTags
                .Where(modelTag => animationWeaponTags.Any(animationTag => AreCompatibleWeaponTags(modelTag, animationTag)))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var rejectedTags = new List<string>();
            var reasons = new List<string>();

            if (modelWeaponTags.Count > 0 && animationWeaponTags.Count > 0 && sharedWeaponTags.Length == 0)
            {
                foreach (var tag in modelWeaponTags.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    rejectedTags.Add("model:" + tag);
                }
                foreach (var tag in animationWeaponTags.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    rejectedTags.Add("animation:" + tag);
                }
                reasons.Add("Rejected: model attachment/weapon tags and AnimationClip action tags conflict.");
                return new AnimationSemanticMatch(
                    false,
                    0,
                    "semantic_rejected_attachment_conflict",
                    reasons.ToArray(),
                    modelTags,
                    animationTags,
                    Array.Empty<string>(),
                    rejectedTags.ToArray()
                );
            }

            if (requireStrictAttachmentSemantics && modelWeaponTags.Count > 0 && animationWeaponTags.Count > 0)
            {
                reasons.Add($"Semantic weapon/action tags agree: {string.Join(", ", sharedWeaponTags)}.");
                return new AnimationSemanticMatch(
                    true,
                    12,
                    "structural_unity_binding_semantic_attachment",
                    reasons.ToArray(),
                    modelTags,
                    animationTags,
                    sharedWeaponTags,
                    Array.Empty<string>()
                );
            }

            var modelFamilyTags = ExtractFamilySemanticTags(model);
            var animationFamilyTags = ExtractFamilySemanticTags(animation);
            var sharedFamilyTags = modelFamilyTags
                .Intersect(animationFamilyTags, StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (sharedFamilyTags.Length > 0)
            {
                reasons.Add($"Semantic family tags agree: {string.Join(", ", sharedFamilyTags)}.");
                return new AnimationSemanticMatch(
                    true,
                    8,
                    "structural_unity_binding_semantic_family",
                    reasons.ToArray(),
                    modelTags,
                    animationTags,
                    sharedFamilyTags,
                    Array.Empty<string>()
                );
            }

            if (requireStrictAttachmentSemantics && modelWeaponTags.Count > 0 && animationWeaponTags.Count == 0)
            {
                reasons.Add("Accepted: model has an attachment/weapon, but AnimationClip has no weapon-specific semantic tag.");
                return new AnimationSemanticMatch(
                    true,
                    2,
                    "structural_unity_binding_semantic_neutral_action",
                    reasons.ToArray(),
                    modelTags,
                    animationTags,
                    Array.Empty<string>(),
                    Array.Empty<string>()
                );
            }

            if (requireStrictAttachmentSemantics && modelWeaponTags.Count == 0 && animationWeaponTags.Count > 0)
            {
                rejectedTags.AddRange(animationWeaponTags.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Select(x => "animation:" + x));
                reasons.Add("Rejected: AnimationClip is weapon/action-specific, but the model catalog has no matching attachment tag.");
                return new AnimationSemanticMatch(
                    false,
                    0,
                    "semantic_rejected_unmatched_animation_weapon",
                    reasons.ToArray(),
                    modelTags,
                    animationTags,
                    Array.Empty<string>(),
                    rejectedTags.ToArray()
                );
            }

            reasons.Add("Accepted: no conflicting attachment/action semantic tags were found.");
            return new AnimationSemanticMatch(
                true,
                0,
                "structural_unity_binding_semantic_neutral",
                reasons.ToArray(),
                modelTags,
                animationTags,
                Array.Empty<string>(),
                Array.Empty<string>()
            );
        }

        private static string[] ExtractAnimationSemanticTags(JObject entry, bool includeModelHierarchy)
        {
            var text = BuildSemanticText(entry, includeModelHierarchy);
            var tags = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            AddSemanticTagIfMatch(text, tags, "crossbow", @"\bcross\s*bow\d*\b|\bcrossbow\d*\b");
            AddSemanticTagIfMatch(text, tags, "bow", @"\b2h\s*bow\d*\b|\b1h\s*bow\d*\b|\bbow\d*\b");
            AddSemanticTagIfMatch(text, tags, "bomb", @"\b[12]h\s*bomb\b|\bbomb\b");
            AddSemanticTagIfMatch(text, tags, "bottle", @"\b[12]h\s*bottle\b|\bbottle\b|\bdrink\s*bottle\b");
            AddSemanticTagIfMatch(text, tags, "pistol", @"\bpistol\b");
            AddSemanticTagIfMatch(text, tags, "railgun", @"\brail\s*gun\b|\brailgun\b");
            AddSemanticTagIfMatch(text, tags, "gun", @"\bgun\b|\brifle\b|\bbullet\s*gun\b|\bzap\s*gun\b|\bflame\s*gun\b");
            AddSemanticTagIfMatch(text, tags, "sword", @"\b2w\s*sword\b|\b1h\s*sword\b|\bsword\b|\btwin\s*blade\b|\btwinblade\b");
            AddSemanticTagIfMatch(text, tags, "spear", @"\bspear\b|\bthrowing\s*spear\b");
            AddSemanticTagIfMatch(text, tags, "mace", @"\bmace\b");
            AddSemanticTagIfMatch(text, tags, "dagger", @"\bdagger\b");
            AddSemanticTagIfMatch(text, tags, "axe", @"\baxe\b");
            AddSemanticTagIfMatch(text, tags, "whip", @"\bwhip\b");
            AddSemanticTagIfMatch(text, tags, "sickle", @"\bsickle\b");
            AddSemanticTagIfMatch(text, tags, "wand", @"\bwand\b");
            AddSemanticTagIfMatch(text, tags, "shield", @"\bshield\b");
            AddSemanticTagIfMatch(text, tags, "unarmed", @"\bunarmed\b");
            AddSemanticTagIfMatch(text, tags, "spell", @"\bspell\b|\bchaos\s*bolt\b|\bchaosbolt\b|\bspectral\s*blast\b|\bbeam\b");
            return tags.ToArray();
        }

        private static string[] ExtractFamilySemanticTags(JObject entry)
        {
            var text = BuildSemanticText(entry, includeModelHierarchy: false);
            var tags = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in Regex.Matches(text, @"\b(?:hyb_)?(?:npc|pc)?[a-z][a-z0-9]{3,}\b", RegexOptions.IgnoreCase))
            {
                var token = match.Value.Trim('_').ToLowerInvariant();
                if (token.Length >= 5
                    && !IsWeaponSemanticTag(token)
                    && !GenericSemanticStopWords.Contains(token))
                {
                    tags.Add(token);
                }
            }
            return tags.Take(16).ToArray();
        }

        private static string BuildSemanticText(JObject entry, bool includeModelHierarchy)
        {
            var parts = new List<string>
            {
                (string)entry["name"],
                (string)entry["resourceKind"],
                (string)entry["sourceType"],
                (string)entry["container"],
                (string)entry["source"],
                (string)entry["output"],
                (string)entry["animationAsset"],
            };
            if (includeModelHierarchy)
            {
                parts.AddRange(entry["meshPaths"]?.ToObject<string[]>() ?? Array.Empty<string>());
                parts.AddRange((entry["nodePaths"]?.ToObject<string[]>() ?? Array.Empty<string>())
                    .Where(x => Regex.IsMatch(x ?? string.Empty, "weapon|bow|crossbow|bomb|bottle|gun|sword|spear|mace|dagger|axe|whip|sickle|wand|shield|unarmed", RegexOptions.IgnoreCase)));
            }
            else
            {
                parts.AddRange(entry["transformBindingPaths"]?.ToObject<string[]>() ?? Array.Empty<string>());
            }

            return Regex.Replace(
                string.Join(" ", parts.Where(x => !string.IsNullOrWhiteSpace(x))),
                @"(?<=[a-z])(?=[A-Z])|[_\-/\\.:]+",
                " "
            ).ToLowerInvariant();
        }

        private static void AddSemanticTagIfMatch(string text, ISet<string> tags, string tag, string pattern)
        {
            if (Regex.IsMatch(text ?? string.Empty, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                tags.Add(tag);
            }
        }

        private static bool IsWeaponSemanticTag(string tag)
        {
            return WeaponSemanticTags.Contains(tag ?? string.Empty);
        }

        private static bool AreCompatibleWeaponTags(string modelTag, string animationTag)
        {
            if (string.Equals(modelTag, animationTag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if ((string.Equals(modelTag, "crossbow", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(modelTag, "bow", StringComparison.OrdinalIgnoreCase))
                && (string.Equals(animationTag, "crossbow", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(animationTag, "bow", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            if ((string.Equals(modelTag, "pistol", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(modelTag, "railgun", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(modelTag, "gun", StringComparison.OrdinalIgnoreCase))
                && (string.Equals(animationTag, "pistol", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(animationTag, "railgun", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(animationTag, "gun", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            return false;
        }

        private static bool IsTransformBodyAnimationAsset(JObject animation)
        {
            var animationType = (string)animation["animationType"] ?? string.Empty;
            var coreTransformBindingCount = (int?)animation["coreTransformBindingCount"] ?? 0;
            return coreTransformBindingCount >= 3
                && (string.Equals(animationType, "TransformBodyAnimation", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(animationType, "MixedHumanoidTransform", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(animationType, "TransformAnimation", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsUnknownResourceKind(string resourceKind)
        {
            return string.IsNullOrWhiteSpace(resourceKind)
                || string.Equals(resourceKind, "Unknown", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> GetComparableBonePathKeys(string path)
        {
            var parts = (path ?? string.Empty)
                .Replace('\\', '/')
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeAnimationPath)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();
            for (var i = 0; i < parts.Length; i++)
            {
                yield return string.Join("/", parts.Skip(i));
            }
        }

        private static string GetCatalogKey(JObject entry)
        {
            return BuildAssetKey((string)entry["source"], (long?)entry["pathId"] ?? 0);
        }

        private static string GetAssetKey(AnimeStudio.Object asset)
        {
            return BuildAssetKey(asset?.assetsFile?.originalPath ?? asset?.assetsFile?.fileName, asset?.m_PathID ?? 0);
        }

        private static string BuildAssetKey(string source, long pathId)
        {
            return $"{NormalizeSourcePath(source)}#{pathId}";
        }

        private static string NormalizeSourcePath(string source)
        {
            return (source ?? string.Empty)
                .Replace('\\', '/')
                .Trim()
                .ToLowerInvariant();
        }

        private sealed class ModelAnimationCandidate
        {
            public string name { get; set; }
            public string resourceKind { get; set; }
            public string output { get; set; }
            public string animationAsset { get; set; }
            public string source { get; set; }
            public string container { get; set; }
            public float? sampleRate { get; set; }
            public float? duration { get; set; }
            public int curveCount { get; set; }
            public int eventCount { get; set; }
            public bool? legacy { get; set; }
            public string animationType { get; set; }
            public string animationCapability { get; set; }
            public bool? hasMuscleClip { get; set; }
            public int? coreTransformBindingCount { get; set; }
            public int? humanoidBindingCount { get; set; }
            public int? blendShapeBindingCount { get; set; }
            public int? trueBlendShapeBindingCount { get; set; }
            public int? rendererMaterialBindingCount { get; set; }
            public int? rendererPropertyBindingCount { get; set; }
            public int? activeStateBindingCount { get; set; }
            public int? auxiliaryBindingCount { get; set; }
            public string[] classificationNotes { get; set; }
            public string confidence { get; set; }
            public string relation { get; set; }
            public string relationSource { get; set; }
            public int score { get; set; }
            public string[] matchReasons { get; set; }
            public string[] semanticModelTags { get; set; }
            public string[] semanticAnimationTags { get; set; }
            public string[] semanticSharedTags { get; set; }
            public string[] semanticRejectedTags { get; set; }
            public string[] matchedBindingPaths { get; set; }
            public bool? matchedBindingPathsTruncated { get; set; }
            public string[] matchedVisibleMeshBindingPaths { get; set; }
            public bool? matchedVisibleMeshBindingPathsTruncated { get; set; }
            public string[] unmatchedBindingPaths { get; set; }
            public bool? unmatchedBindingPathsTruncated { get; set; }
            public bool requiresHumanoidBake { get; set; }
            public bool legacyUnityBakeSupported { get; set; }
            public bool requiresInternalHumanoidSolve { get; set; }
            public bool modelReadyForAnimation { get; set; }
            public string modelAnimationBlockedReason { get; set; }
            public JObject modelAnimationGate { get; set; }
            public bool directGltfPreviewReady { get; set; }
            public bool productionAnimationReady { get; set; }
            public string productionAnimationBlockedReason { get; set; }
            public object verification { get; set; }
            public string nextAction { get; set; }
        }

        private sealed class ExplicitAnimationLinks
        {
            public Dictionary<string, List<ExplicitModelAnimationLink>> ByModel { get; } = new Dictionary<string, List<ExplicitModelAnimationLink>>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, List<ExplicitModelAnimationLink>> ByAnimation { get; } = new Dictionary<string, List<ExplicitModelAnimationLink>>(StringComparer.OrdinalIgnoreCase);
            public bool MatchingDeferred { get; set; }
            public string DeferredReason { get; set; }
            public long PairCount { get; set; }
            public long PairLimit { get; set; }
            private readonly HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public void Add(string modelKey, string animationKey, ExplicitModelAnimationLink link)
            {
                var uniqueKey = $"{modelKey}|{animationKey}|{link.Relation}";
                if (!seen.Add(uniqueKey))
                {
                    return;
                }

                AddTo(ByModel, modelKey, link);
                AddTo(ByAnimation, animationKey, link);
            }

            public bool ContainsModelAnimation(string modelKey, string animationKey)
            {
                return ByModel.TryGetValue(modelKey, out var links)
                    && links.Any(x => string.Equals(GetCatalogKey(x.Animation), animationKey, StringComparison.OrdinalIgnoreCase));
            }

            private static void AddTo(Dictionary<string, List<ExplicitModelAnimationLink>> map, string key, ExplicitModelAnimationLink link)
            {
                if (!map.TryGetValue(key, out var links))
                {
                    links = new List<ExplicitModelAnimationLink>();
                    map[key] = links;
                }
                links.Add(link);
            }
        }

        private sealed class ExplicitModelAnimationLink
        {
            public string ModelName { get; set; }
            public string ModelResourceKind { get; set; }
            public string ModelOutput { get; set; }
            public string ModelSkeletonHash { get; set; }
            public int ModelBoneCount { get; set; }
            public int ModelMeshCount { get; set; }
            public int ModelTextureCount { get; set; }
            public bool ModelReadyForAnimation { get; set; }
            public string ModelAnimationBlockedReason { get; set; }
            public ModelAnimationGate ModelAnimationGate { get; set; }
            public JObject Animation { get; set; }
            public string Confidence { get; set; }
            public string Relation { get; set; }
            public string Source { get; set; }
            public int Score { get; set; }
            public string[] Reasons { get; set; }
            public string[] MatchedBindingPaths { get; set; }
            public string[] MatchedVisibleMeshBindingPaths { get; set; }
            public string[] UnmatchedBindingPaths { get; set; }
            public bool RequiresHumanoidBake { get; set; }
            public bool RequiresInternalHumanoidSolve { get; set; }
            public string[] SemanticModelTags { get; set; }
            public string[] SemanticAnimationTags { get; set; }
            public string[] SemanticSharedTags { get; set; }
            public string[] SemanticRejectedTags { get; set; }
        }

        private sealed record ModelAnimationGate(bool Ready, string Status, string[] Reasons, JObject Evidence);

        private sealed class ExplicitAnimationRelation
        {
            public AnimationClip Clip { get; set; }
            public string Relation { get; set; }
            public string[] Reasons { get; set; }
        }

        private sealed record StructuralAnimationMatch(
            bool IsCandidate,
            int Score,
            int MatchedPathCount,
            int CoreMatchedPathCount,
            string[] MatchedPaths,
            string[] UnmatchedPaths
        );

        private sealed record HumanoidAnimationMatch(
            bool IsCandidate,
            int Score,
            string AvatarName,
            int HumanoidBindingCount
        );

        private sealed record AnimationSemanticMatch(
            bool IsCandidate,
            int ScoreBonus,
            string Confidence,
            string[] Reasons,
            string[] ModelTags,
            string[] AnimationTags,
            string[] SharedTags,
            string[] RejectedTags
        );

        private static void ExportModelAssets(
            string savePath,
            List<AssetItem> models,
            AssetGroupOption assetGroupOption,
            List<AssetItem> animations
        )
        {
            var reuseLibraryStats = WorkMode == WorkMode.Library && _exportRunStats?.StartedAtUtc != null;
            if (!reuseLibraryStats)
            {
                _exportRunStats = new ExportRunStats
                {
                    StartedAtUtc = DateTime.UtcNow,
                    Mode = WorkMode.ToString(),
                };
            }
            else
            {
                _exportRunStats.Mode = WorkMode.ToString();
            }

            _exportRunStats.ModelCandidateCount += models.Count;
            _exportRunStats.AnimationCandidateCount += animations?.Count ?? 0;

            var batchStartedAt = DateTime.UtcNow;
            if (_exportRunStats.StartedAtUtc == null || batchStartedAt < _exportRunStats.StartedAtUtc)
            {
                _exportRunStats.StartedAtUtc = batchStartedAt;
            }

            var exportedCount = 0;
            var processedCount = 0;
            var skippedCount = 0;
            var toExportCount = models.Count;
            Logger.Info($"Export mode {WorkMode} using max export tasks {MaxExportTasks}.");
            if (models.Any(IsParallelStaticMeshAsset))
            {
                PreloadStaticMeshMaterialBindingCache();
            }

            for (var modelIndex = 0; modelIndex < models.Count;)
            {
                if (MaxExportTasks > 1 && IsParallelStaticMeshAsset(models[modelIndex]))
                {
                    var start = modelIndex;
                    while (modelIndex < models.Count && IsParallelStaticMeshAsset(models[modelIndex]))
                    {
                        modelIndex++;
                    }

                    var count = modelIndex - start;
                    Logger.Info($"Exporting {count} StaticMeshPrimary candidate(s) with {MaxExportTasks} task(s).");
                    var results = new ModelExportResult[count];
                    Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = MaxExportTasks }, offset =>
                    {
                        var asset = models[start + offset];
                        var exportPath = GetExportPath(savePath, assetGroupOption, asset);
                        results[offset] = ExportSingleModelAsset(asset, exportPath, animations);
                    });

                    for (var offset = 0; offset < results.Length; offset++)
                    {
                        processedCount++;
                        ApplyModelExportResult(
                            savePath,
                            results[offset],
                            processedCount,
                            toExportCount,
                            ref exportedCount,
                            ref skippedCount);
                    }
                    continue;
                }

                var asset = models[modelIndex];
                var exportPath = GetExportPath(savePath, assetGroupOption, asset);
                var result = ExportSingleModelAsset(asset, exportPath, animations);
                modelIndex++;
                processedCount++;
                ApplyModelExportResult(
                    savePath,
                    result,
                    processedCount,
                    toExportCount,
                    ref exportedCount,
                    ref skippedCount);
            }


            _exportRunStats.FinishedAtUtc = DateTime.UtcNow;
            Logger.Info($"Finished exporting {exportedCount}/{toExportCount} model asset(s); skipped {skippedCount} candidate(s).");
            WriteExportRunSummary(savePath);
        }

        private static bool IsParallelStaticMeshAsset(AssetItem asset)
        {
            return asset?.Asset is Mesh && asset.LibraryRole == "StaticMeshPrimary";
        }

        private static ModelExportResult ExportSingleModelAsset(
            AssetItem asset,
            string exportPath,
            List<AssetItem> animations)
        {
            Logger.Verbose($"Exporting {asset.TypeString}: {asset.Text}");
            if (TryGetOptimizedAnimatorAvatarBlockReason(asset, out var avatarBlockReason))
            {
                Logger.Warning(
                    $"Skipping {asset.TypeString}:{asset.Text}: {avatarBlockReason}. Optimized Unity transform hierarchy cannot be restored without a resolved Avatar; keep static mesh children as diagnostics or load the full source dependency set."
                );
                ProfileLogger.Event("library_skip_optimized_animator_avatar_block", new Dictionary<string, object>
                {
                    ["name"] = asset.Text,
                    ["type"] = asset.TypeString,
                    ["reason"] = avatarBlockReason,
                    ["source"] = asset.SourceFile?.originalPath ?? asset.SourceFile?.fileName,
                    ["pathId"] = asset.m_PathID,
                });
                return new ModelExportResult(asset, exportPath, false, null);
            }

            try
            {
                var exported = asset.Asset switch
                {
                    GameObject => ExportGameObject(asset, exportPath, animations),
                    Animator => ExportAnimator(asset, exportPath, animations),
                    Mesh when asset.LibraryRole == "StaticMeshPrimary" => ExportStaticMeshGltf(asset, exportPath),
                    _ => false,
                };
                return new ModelExportResult(asset, exportPath, exported, null);
            }
            catch (Exception ex)
            {
                return new ModelExportResult(asset, exportPath, false, ex);
            }
        }

        private static void ApplyModelExportResult(
            string savePath,
            ModelExportResult result,
            int processedCount,
            int toExportCount,
            ref int exportedCount,
            ref int skippedCount)
        {
            if (result.Exception != null)
            {
                _exportRunStats.RecordFailedModel("export_exception", result.Asset, result.Exception);
                Logger.Error(
                    $"Export {result.Asset.Type}:{result.Asset.Text} error\r\n{result.Exception.Message}\r\n{result.Exception.StackTrace}"
                );
                skippedCount++;
                CollectAfterModelExport();
                LogModelExportProgress(processedCount, toExportCount, exportedCount, skippedCount);
                return;
            }

            if (result.Exported)
            {
                exportedCount++;
                _exportRunStats.ExportedModels++;
                var exportMessage = $"[{processedCount}/{toExportCount}] Exported {result.Asset.TypeString}: {result.Asset.Text}";
                if (processedCount <= 20 || processedCount % 100 == 0 || processedCount == toExportCount)
                {
                    Logger.Info(exportMessage);
                }
                else
                {
                    Logger.Verbose(exportMessage);
                }
                AppendExportManifest(savePath, result.Asset, result.ExportPath);
            }
            else
            {
                skippedCount++;
                _exportRunStats.RecordSkippedModel("export_returned_false", result.Asset);
            }
            CollectAfterModelExport();
            LogModelExportProgress(processedCount, toExportCount, exportedCount, skippedCount);
        }

        private static void LogModelExportProgress(int processedCount, int toExportCount, int exportedCount, int skippedCount)
        {
            if (processedCount % 100 == 0)
            {
                Logger.Info(
                    $"Processed {processedCount}/{toExportCount} model candidate(s); exported {exportedCount}, skipped {skippedCount}."
                );
            }
        }

        private sealed class ModelExportResult
        {
            public ModelExportResult(AssetItem asset, string exportPath, bool exported, Exception exception)
            {
                Asset = asset;
                ExportPath = exportPath;
                Exported = exported;
                Exception = exception;
            }

            public AssetItem Asset { get; }
            public string ExportPath { get; }
            public bool Exported { get; }
            public Exception Exception { get; }
        }

        private static void CollectAfterModelExport()
        {
            _exportsSinceCollect++;
            if (ModelGcInterval <= 0)
            {
                return;
            }

            if (_exportsSinceCollect < ModelGcInterval)
            {
                return;
            }

            _exportsSinceCollect = 0;
            using (ProfileLogger.Measure("model_gc", new Dictionary<string, object>
            {
                ["interval"] = ModelGcInterval,
            }))
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: false);
            }
        }

        private static void AppendExportManifest(string savePath, AssetItem asset, string exportPath)
        {
            var manifestPath = Path.Combine(savePath, "export_manifest.jsonl");
            var entry = new
            {
                exportedAt = DateTime.UtcNow.ToString("O"),
                mode = WorkMode.ToString(),
                type = asset.TypeString,
                name = asset.Text,
                container = asset.Container,
                source = asset.SourceFile?.originalPath ?? asset.SourceFile?.fileName,
                pathId = asset.m_PathID,
                output = exportPath,
            };
            lock (ExportManifestWriteLock)
            {
                File.AppendAllText(manifestPath, JsonConvert.SerializeObject(entry) + Environment.NewLine);
            }
        }

        private static string GetExportPath(
            string savePath,
            AssetGroupOption assetGroupOption,
            AssetItem asset
        )
        {
            return assetGroupOption switch
            {
                AssetGroupOption.ByType =>
                    Path.Combine(savePath, asset.TypeString) + Path.DirectorySeparatorChar,
                AssetGroupOption.ByContainer when !string.IsNullOrEmpty(asset.Container) =>
                    (
                        Path.HasExtension(asset.Container)
                            ? Path.Combine(savePath, Path.GetDirectoryName(asset.Container))
                            : Path.Combine(savePath, asset.Container)
                    ) + Path.DirectorySeparatorChar,
                AssetGroupOption.ByContainer =>
                    Path.Combine(savePath, asset.TypeString) + Path.DirectorySeparatorChar,
                AssetGroupOption.ByLibrary =>
                    GetLibraryExportPath(savePath, asset) + Path.DirectorySeparatorChar,
                AssetGroupOption.BySource =>
                    GetSourceExportPath(savePath, asset) + Path.DirectorySeparatorChar,
                _ => savePath + Path.DirectorySeparatorChar,
            };
        }

        private static string GetSourceExportPath(string savePath, AssetItem asset)
        {
            // 只清洗输出目录名，source 原始路径仍写进 manifest/报告，方便回查 Unity CAB。
            var sourceFileName = Exporter.FixFileName(asset.SourceFile?.fileName ?? "source");
            if (string.IsNullOrEmpty(asset.SourceFile?.originalPath))
            {
                return Path.Combine(savePath, sourceFileName + "_export");
            }

            var sourceFolderName = Exporter.FixFileName(Path.GetFileName(asset.SourceFile.originalPath));
            return Path.Combine(savePath, sourceFolderName + "_export", sourceFileName);
        }

        private static string GetLibraryExportPath(string savePath, AssetItem asset)
        {
            var libraryRoot = GetLibraryRoot(asset);
            var subPath = SanitizeRelativePath(GetLibrarySubPath(asset));
            return string.IsNullOrEmpty(subPath)
                ? Path.Combine(savePath, libraryRoot)
                : Path.Combine(savePath, libraryRoot, subPath);
        }

        private static string GetLibraryRoot(AssetItem asset)
        {
            return asset.Type switch
            {
                ClassIDType.GameObject or ClassIDType.Animator => "Models",
                ClassIDType.Mesh when asset.LibraryRole == "StaticMeshPrimary" => "Models",
                ClassIDType.AnimationClip => "Animations",
                ClassIDType.AudioClip => "Audio",
                ClassIDType.Shader => "Shaders",
                ClassIDType.Texture2D or ClassIDType.Texture2DArray or ClassIDType.Sprite => "Textures",
                ClassIDType.Material => "Materials",
                ClassIDType.Mesh => "Meshes",
                ClassIDType.MiHoYoBinData
                or ClassIDType.TextAsset
                or ClassIDType.MonoBehaviour => "Data",
                _ => asset.TypeString,
            };
        }

        private static string GetLibrarySubPath(AssetItem asset)
        {
            if (asset.Asset is AudioClip audioClip)
            {
                return GetAudioLibrarySubPath(asset, audioClip);
            }

            if (asset.Type == ClassIDType.Texture2DArray || asset.LibraryRole == "Texture2DArray")
            {
                var textureArraySubPath = GetLibrarySubPathWithoutRole(asset);
                var textureArrayRoot = GetTexture2DArrayRoot(asset);
                return string.IsNullOrWhiteSpace(textureArraySubPath)
                    ? textureArrayRoot
                    : Path.Combine(textureArrayRoot, textureArraySubPath);
            }

            if (asset.Type == ClassIDType.Texture2D && asset.LibraryRole == "MaterialLibrary")
            {
                var textureSubPath = GetLibrarySubPathWithoutRole(asset);
                return string.IsNullOrWhiteSpace(textureSubPath)
                    ? "MaterialLibrary"
                    : Path.Combine("MaterialLibrary", textureSubPath);
            }

            if (asset.LibraryRole == "RawUnreferenced")
            {
                var rawSubPath = GetLibrarySubPathWithoutRole(asset);
                return string.IsNullOrWhiteSpace(rawSubPath)
                    ? "RawUnreferenced"
                    : Path.Combine("RawUnreferenced", rawSubPath);
            }

            if (asset.Type == ClassIDType.Mesh && asset.LibraryRole == "StaticMeshPrimary")
            {
                var kind = InferLibraryResourceKind(asset.Text, asset.Container, asset.SourceFile?.originalPath ?? asset.SourceFile?.fileName);
                var meshSubPath = GetStaticMeshLibrarySubPath(asset);
                return string.IsNullOrWhiteSpace(meshSubPath)
                    ? kind
                    : Path.Combine(kind, meshSubPath);
            }

            return GetLibrarySubPathWithoutRole(asset);
        }

        private static string GetStaticMeshLibrarySubPath(AssetItem asset)
        {
            if (!string.IsNullOrWhiteSpace(asset.Container) && !int.TryParse(asset.Container, out _))
            {
                return GetLibrarySubPathWithoutRole(asset);
            }

            var source = asset.SourceFile?.originalPath ?? asset.SourceFile?.fileName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(source))
            {
                return GetAssetNameCategory(asset.Text);
            }

            var sourcePath = GetRelativeStaticMeshSourcePath(source);
            if (Path.HasExtension(sourcePath))
            {
                sourcePath = Path.GetDirectoryName(sourcePath);
            }

            return string.IsNullOrWhiteSpace(sourcePath)
                ? GetAssetNameCategory(asset.Text)
                : sourcePath;
        }

        private static string GetRelativeStaticMeshSourcePath(string source)
        {
            var normalized = (source ?? string.Empty)
                .Replace('\\', '/')
                .Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            var markers = new[]
            {
                "/AssetBundles/",
                "/StreamingAssets/",
                "/Resources/",
                "/ContentArchives/",
                "/Data/",
            };
            foreach (var marker in markers)
            {
                var index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    return normalized.Substring(index + 1).Replace('/', Path.DirectorySeparatorChar);
                }
            }

            var dataIndex = normalized.LastIndexOf("_Data/", StringComparison.OrdinalIgnoreCase);
            if (dataIndex >= 0)
            {
                return normalized.Substring(dataIndex + "_Data/".Length).Replace('/', Path.DirectorySeparatorChar);
            }

            return Path.GetFileName(normalized);
        }

        private static string GetLibrarySubPathWithoutRole(AssetItem asset)
        {
            if (!string.IsNullOrWhiteSpace(asset.Container) && !int.TryParse(asset.Container, out _))
            {
                var container = Path.HasExtension(asset.Container)
                    ? Path.GetDirectoryName(asset.Container)
                    : asset.Container;
                return string.IsNullOrWhiteSpace(container) ? string.Empty : container;
            }

            return GetAssetNameCategory(asset.Text);
        }

        private static string GetAudioLibrarySubPath(AssetItem asset, AudioClip audioClip)
        {
            var category = Exporter.ClassifyAudioClip(audioClip, asset.Text, asset.Container, asset.SourceFile?.originalPath ?? asset.SourceFile?.fileName);
            var sourcePath = !string.IsNullOrWhiteSpace(asset.Container) && !int.TryParse(asset.Container, out _)
                ? asset.Container
                : asset.SourceFile?.originalPath ?? asset.SourceFile?.fileName ?? string.Empty;
            var container = Path.HasExtension(sourcePath) ? Path.GetDirectoryName(sourcePath) : sourcePath;
            if (string.IsNullOrWhiteSpace(container))
            {
                return category;
            }

            return Path.Combine(category, SanitizeRelativePath(container));
        }

        private static string SanitizeRelativePath(string path)
        {
            var parts = path
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => Exporter.FixFileName(Regex.Replace(x, @"[<>:""/\\|?*]+", "_")))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();
            return parts.Length == 0 ? string.Empty : Path.Combine(parts);
        }

        private static string GetAssetNameCategory(string name)
        {
            name = Exporter.FixFileName(name ?? string.Empty);
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var match = Regex.Match(name, @"^[A-Za-z0-9]+");
            return match.Success ? match.Value : string.Empty;
        }

        public static void ExportAssetsMap(
            string savePath,
            List<AssetEntry> toExportAssets,
            string exportListName,
            ExportListType exportListType
        )
        {
            string filename;
            switch (exportListType)
            {
                case ExportListType.XML:
                    filename = Path.Combine(savePath, $"{exportListName}.xml");
                    var settings = new XmlWriterSettings() { Indent = true };
                    using (XmlWriter writer = XmlWriter.Create(filename, settings))
                    {
                        writer.WriteStartDocument();
                        writer.WriteStartElement("Assets");
                        writer.WriteAttributeString("filename", filename);
                        writer.WriteAttributeString("createdAt", DateTime.UtcNow.ToString("s"));
                        foreach (var asset in toExportAssets)
                        {
                            writer.WriteStartElement("Asset");
                            writer.WriteElementString("Name", asset.Name);
                            writer.WriteElementString("Container", asset.Container);
                            writer.WriteStartElement("Type");
                            writer.WriteAttributeString("id", ((int)asset.Type).ToString());
                            writer.WriteValue(asset.Type.ToString());
                            writer.WriteEndElement();
                            writer.WriteElementString("PathID", asset.PathID.ToString());
                            writer.WriteElementString("Source", asset.Source);
                            writer.WriteEndElement();
                        }
                        writer.WriteEndElement();
                        writer.WriteEndDocument();
                    }
                    break;
                case ExportListType.JSON:
                    filename = Path.Combine(savePath, $"{exportListName}.json");
                    using (StreamWriter file = File.CreateText(filename))
                    {
                        JsonSerializer serializer = new JsonSerializer()
                        {
                            Formatting = Newtonsoft.Json.Formatting.Indented,
                        };
                        serializer.Converters.Add(new StringEnumConverter());
                        serializer.Serialize(file, toExportAssets);
                    }
                    break;
            }

            var statusText = $"Finished exporting asset list with {toExportAssets.Count()} items.";

            Logger.Info(statusText);

            Logger.Info($"AssetMap build successfully !!");
        }

        public static TypeTree MonoBehaviourToTypeTree(MonoBehaviour m_MonoBehaviour)
        {
            return m_MonoBehaviour.ConvertToTypeTree(assemblyLoader);
        }
    }
}
