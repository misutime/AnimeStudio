using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
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
        public static WorkMode WorkMode { get; set; } = WorkMode.Export;
        public static FbxAnimationMode FbxAnimationMode { get; set; } = FbxAnimationMode.Skip;
        public static int MaxExportTasks { get; set; } = 1;
        public static int BatchFiles { get; set; } = 2;
        public static bool ModelRootsOnly { get; set; }
        private static int _exportsSinceCollect;
        private static readonly Regex[] ModelRootExcludePatterns =
        {
            new Regex(@"^Cs_", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"_Convert$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"(?:^|_)Vo$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"(?:^|_)(Col|Collider|Collision)$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"Collider|Collision", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"(?:^|_)ShadowMesh$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        };

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

            var matches = exportableAssets
                .Where(x =>
                {
                    var isMatchRegex =
                        nameFilters.IsNullOrEmpty() || nameFilters.Any(y => y.IsMatch(x.Text));
                    var isFilteredType =
                        typeFilters.IsNullOrEmpty() || typeFilters.Contains(x.Type);
                    var isContainerMatch =
                        containerFilters.IsNullOrEmpty()
                        || containerFilters.Any(y => y.IsMatch(x.Container));
                    var isNameExcluded =
                        !nameExcludeFilters.IsNullOrEmpty()
                        && nameExcludeFilters.Any(y => y.IsMatch(x.Text ?? string.Empty));
                    var isContainerExcluded =
                        !containerExcludeFilters.IsNullOrEmpty()
                        && containerExcludeFilters.Any(y => y.IsMatch(GetFilterableContainerText(x)));
                    return isMatchRegex
                        && isFilteredType
                        && isContainerMatch
                        && !isNameExcluded
                        && !isContainerExcluded;
                })
                .ToArray();
            if (ModelRootsOnly)
            {
                matches = FilterModelRootsOnly(matches, containerMainAssets);
                matches = FilterUsefulModelRoots(matches);
            }
            exportableAssets.Clear();
            exportableAssets.AddRange(matches);
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
                    if (x.Asset is not GameObject)
                    {
                        return true;
                    }

                    var exclude = ModelRootExcludePatterns.Any(y => y.IsMatch(x.Text ?? string.Empty));
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
                        $"--model_roots_only skipped {skippedModels} GameObject model(s) because no AssetBundle/ResourceManager main GameObject entries were found."
                    );
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
            int toExportCount = toExportAssets.Count;
            int exportedCount = 0;
            foreach (var asset in toExportAssets)
            {
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
                        if (string.IsNullOrEmpty(asset.SourceFile.originalPath))
                        {
                            exportPath = Path.Combine(
                                savePath,
                                asset.SourceFile.fileName + "_export"
                            );
                        }
                        else
                        {
                            exportPath = Path.Combine(
                                savePath,
                                Path.GetFileName(asset.SourceFile.originalPath) + "_export",
                                asset.SourceFile.fileName
                            );
                        }
                        break;
                    default:
                        exportPath = savePath;
                        break;
                }
                exportPath += Path.DirectorySeparatorChar;
                Logger.Info(
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
                            }
                            break;
                        case ExportType.Dump:
                            if (ExportDumpFile(asset, exportPath))
                            {
                                exportedCount++;
                            }
                            break;
                        case ExportType.Convert:
                            if (ExportConvertFile(asset, exportPath))
                            {
                                exportedCount++;
                            }
                            break;
                        case ExportType.JSON:
                            if (ExportJSONFile(asset, exportPath))
                            {
                                exportedCount++;
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

        public static void ExportCurrentAssets(
            string savePath,
            AssetGroupOption assetGroupOption,
            ExportType exportType
        )
        {
            switch (WorkMode)
            {
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
        }

        private static List<AssetItem> GetAnimationListForMode()
        {
            return FbxAnimationMode == FbxAnimationMode.All
                ? exportableAssets.Where(x => x.Asset is AnimationClip).ToList()
                : null;
        }

        private static void ExportModelAssets(
            string savePath,
            List<AssetItem> models,
            AssetGroupOption assetGroupOption,
            List<AssetItem> animations
        )
        {
            var exportedCount = 0;
            var toExportCount = models.Count;
            Logger.Info($"Export mode {WorkMode} using max export tasks {MaxExportTasks}.");

            foreach (var asset in models)
            {
                var exportPath = GetExportPath(savePath, assetGroupOption, asset);
                Logger.Info($"[{exportedCount}/{toExportCount}] Exporting {asset.TypeString}: {asset.Text}");
                try
                {
                    var exported = asset.Asset switch
                    {
                        GameObject => ExportGameObject(asset, exportPath, animations),
                        Animator => ExportAnimator(asset, exportPath, animations),
                        _ => false,
                    };

                    if (exported)
                    {
                        exportedCount++;
                        AppendExportManifest(savePath, asset, exportPath);
                    }
                    CollectAfterModelExport();
                }
                catch (Exception ex)
                {
                    Logger.Error(
                        $"Export {asset.Type}:{asset.Text} error\r\n{ex.Message}\r\n{ex.StackTrace}"
                    );
                    CollectAfterModelExport();
                }
            }

            Logger.Info($"Finished exporting {exportedCount}/{toExportCount} model asset(s).");
        }

        private static void CollectAfterModelExport()
        {
            _exportsSinceCollect++;
            if (_exportsSinceCollect < 4)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: false);
                return;
            }

            _exportsSinceCollect = 0;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
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
            File.AppendAllText(manifestPath, JsonConvert.SerializeObject(entry) + Environment.NewLine);
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
                AssetGroupOption.BySource when string.IsNullOrEmpty(asset.SourceFile.originalPath) =>
                    Path.Combine(savePath, asset.SourceFile.fileName + "_export") + Path.DirectorySeparatorChar,
                AssetGroupOption.BySource =>
                    Path.Combine(
                        savePath,
                        Path.GetFileName(asset.SourceFile.originalPath) + "_export",
                        asset.SourceFile.fileName
                    ) + Path.DirectorySeparatorChar,
                _ => savePath + Path.DirectorySeparatorChar,
            };
        }

        private static string GetLibraryExportPath(string savePath, AssetItem asset)
        {
            var libraryRoot = GetLibraryRoot(asset);
            var subPath = GetLibrarySubPath(asset);
            return string.IsNullOrEmpty(subPath)
                ? Path.Combine(savePath, libraryRoot)
                : Path.Combine(savePath, libraryRoot, subPath);
        }

        private static string GetLibraryRoot(AssetItem asset)
        {
            return asset.Type switch
            {
                ClassIDType.GameObject or ClassIDType.Animator => "Models",
                ClassIDType.Texture2D or ClassIDType.Sprite => "Textures",
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
            if (!string.IsNullOrWhiteSpace(asset.Container) && !int.TryParse(asset.Container, out _))
            {
                var container = Path.HasExtension(asset.Container)
                    ? Path.GetDirectoryName(asset.Container)
                    : asset.Container;
                return string.IsNullOrWhiteSpace(container) ? string.Empty : container;
            }

            return GetAssetNameCategory(asset.Text);
        }

        private static string GetAssetNameCategory(string name)
        {
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
