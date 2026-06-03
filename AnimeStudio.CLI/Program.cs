using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AnimeStudio.CLI.Properties;
using Newtonsoft.Json;
using static AnimeStudio.CLI.Studio;

namespace AnimeStudio.CLI 
{
    public class Program
    {
        public static void Main(string[] args) => CommandLine.Init(args);

        public static void Run(Options o)
        {
            try
            {
                Logger.Default = new ConsoleLogger();
                Logger.Flags = o.LoggerFlags.Aggregate((e, x) => e |= x);

                if (o.ConvertModelTextures != null)
                {
                    TexturePostProcessor.ConvertModelTextures(
                        o.ConvertModelTextures.FullName,
                        o.ConvertTextureAssetRoot?.FullName,
                        o.ConvertTextureOutput?.FullName,
                        o.ConvertTextureFormat,
                        o.UpdateGltfTextureRefs
                    );
                    return;
                }

                if (o.GeneratePreviewGltf != null)
                {
                    PreviewGltfGenerator.Generate(
                        o.GeneratePreviewGltf.FullName,
                        o.GameName,
                        o.PreviewModel,
                        o.PreviewAnimation,
                        o.PreviewOutput?.FullName,
                        o.PreviewSourceRoot?.FullName
                    );
                    return;
                }

                if (o.PackModelAnimations != null)
                {
                    PreviewGltfGenerator.GeneratePack(
                        o.PackModelAnimations.FullName,
                        o.GameName,
                        o.PreviewModel,
                        o.PackAnimations,
                        o.PackOutput?.FullName,
                        o.PackLimit,
                        o.PreviewSourceRoot?.FullName
                    );
                    return;
                }

                if (o.GenerateUnityBakeRequest != null)
                {
                    UnityBakeRequestGenerator.Generate(
                        o.GenerateUnityBakeRequest.FullName,
                        o.PreviewModel,
                        o.PreviewAnimation,
                        o.PreviewOutput?.FullName,
                        o.PreviewSourceRoot?.FullName,
                        o.UnityProject?.FullName,
                        o.UnityEditor?.FullName,
                        o.UnityBakeModelPrefab,
                        o.UnityBakeAnimationClip,
                        o.UnityBakeOutput?.FullName,
                        o.UnityBakeFps,
                        o.RunUnityBake,
                        o.BakedGltfOutput?.FullName,
                        o.BakedFbxOutput?.FullName,
                        o.Blender?.FullName
                    );
                    return;
                }

                if (o.ApplyUnityBakeResult != null)
                {
                    var bakedGltf = UnityBakeResultApplier.Apply(
                        o.ApplyUnityBakeResult.FullName,
                        o.BakedGltfOutput?.FullName
                    );
                    if (!string.IsNullOrWhiteSpace(o.BakedFbxOutput?.FullName))
                    {
                        BlenderFbxExporter.Export(bakedGltf, o.BakedFbxOutput.FullName, o.Blender?.FullName);
                    }
                    return;
                }

                if (o.Input == null || o.Output == null)
                {
                    Logger.Error("input_path and output_path are required for export. Use --convert_model_textures, --generate_preview_gltf, --pack_model_animations, --generate_unity_bake_request, or --apply_unity_bake_result for post-export commands.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(o.GameName))
                {
                    Logger.Error("--game is required for export.");
                    Console.WriteLine(GameManager.SupportedGames());
                    return;
                }

                var game = GameManager.GetGame(o.GameName);

                // See https://github.com/Eleiyas/Z3-Asset-Map 
                var paths = File.Exists("./Maps/Z3-AssetIndex-Eleiyas.json")
                    ? JsonConvert.DeserializeObject<Dictionary<ulong, string>>(File.ReadAllText("./Maps/Z3-AssetIndex-Eleiyas.json"))
                    : new Dictionary<ulong, string>();

                Studio.Paths = paths;
                AssetsHelper.Paths = paths;

                if (game == null)
                {
                    Console.WriteLine("Invalid Game !!");
                    Console.WriteLine(GameManager.SupportedGames());
                    return;
                }

                if (game is UnityCNGame unityCNGame)
                {
                    UnityCN.SetKey(unityCNGame.Key);
                    Logger.Info($"[UnityCN] Selected Key is {unityCNGame.Key.Name} - {unityCNGame.Key.Key}");
                }

                Studio.Game = game;
                Studio.ModelRootsOnly = o.ModelRootsOnly || o.WorkMode == WorkMode.Library;
                Studio.WorkMode = o.WorkMode;
                Studio.FbxAnimationMode = o.FbxAnimationMode;
                Studio.IncludeShaders = o.IncludeShaders;
                Studio.MaxExportTasks = Math.Max(1, o.MaxExportTasks);
                Studio.BatchFiles = Math.Max(1, o.BatchFiles);
                Studio.ModelGcInterval = Math.Max(0, o.ModelGcInterval);
                CliExportOptions.FbxScaleFactor = o.FbxScaleFactor;
                CliExportOptions.FbxBoneSize = o.FbxBoneSize;
                CliExportOptions.FbxExportAllNodes = o.FbxExportAllNodes;
                CliExportOptions.FbxAnimationMode = o.FbxAnimationMode;
                CliExportOptions.HumanoidBakeSolver = o.HumanoidBakeSolver;
                CliExportOptions.ModelFormat = o.ModelFormat;
                CliExportOptions.AnimationPackage = o.AnimationPackage;
                CliExportOptions.ModelSource = o.ModelSource;
                CliExportOptions.TextureMode = o.TextureMode;
                CliExportOptions.OutputRoot = o.Output.FullName;
                Logger.FileLogging = Settings.Default.enableFileLogging;
                AssetsHelper.Minimal = Settings.Default.minimalAssetMap;
                AssetsHelper.SetUnityVersion(o.UnityVersion);
                o.Output.Create();
                ProfileLogger.Initialize(o.Output.FullName, o.ProfileLog);

                TypeFlags.SetTypes(JsonConvert.DeserializeObject<Dictionary<ClassIDType, (bool, bool)>>(Settings.Default.types));

                var classTypeFilter = Array.Empty<ClassIDType>();
                if (!o.TypeFilter.IsNullOrEmpty())
                {
                    TypeFlags.SetTypes(new Dictionary<ClassIDType, (bool, bool)>());
                    var exportTexture2D = false;
                    var exportMaterial = false;
                    var classTypeFilterList = new List<ClassIDType>();
                    for (int i = 0; i < o.TypeFilter.Length; i++)
                    {
                        var typeStr = o.TypeFilter[i];
                        var type = ClassIDType.UnknownType;
                        var flag = TypeFlag.Both;
                    
                        try
                        {
                            if (typeStr.Contains(':'))
                            {
                                var param = typeStr.Split(':');
                    
                                flag = (TypeFlag)Enum.Parse(typeof(TypeFlag), param[1], true);
                    
                                typeStr = param[0];
                            }
                    
                            type = (ClassIDType)Enum.Parse(typeof(ClassIDType), typeStr, true);

                            if (type == ClassIDType.Texture2D)
                            {
                                exportTexture2D = flag.HasFlag(TypeFlag.Export);
                            }
                            else if (type == ClassIDType.Material)
                            {
                                exportMaterial = flag.HasFlag(TypeFlag.Export);
                            }
                    
                            TypeFlags.SetType(type, flag.HasFlag(TypeFlag.Parse), flag.HasFlag(TypeFlag.Export));
                    
                            classTypeFilterList.Add(type);
                        }
                        catch(Exception e)
                        {
                            Logger.Error($"{typeStr} has invalid format, skipping...");
                            continue;
                        }
                    }

                    classTypeFilter = classTypeFilterList.ToArray();

                    if (ClassIDType.GameObject.CanExport() || ClassIDType.Animator.CanExport())
                    {
                        TypeFlags.SetType(ClassIDType.Texture2D, true, exportTexture2D);
                        if (Settings.Default.exportMaterials)
                        {
                            TypeFlags.SetType(ClassIDType.Material, true, exportMaterial);
                        }
                        if (ClassIDType.Animator.CanExport())
                        {
                            TypeFlags.SetType(ClassIDType.GameObject, true, false);
                        }
                    }
                }
                ConfigureWorkModeTypes(o.WorkMode, o.FbxAnimationMode);
                if (o.WorkMode != WorkMode.Export)
                {
                    classTypeFilter = Array.Empty<ClassIDType>();
                }

                if (o.GroupAssetsType == AssetGroupOption.ByContainer || o.ModelRootsOnly)
                {
                    TypeFlags.SetType(ClassIDType.AssetBundle, true, false);
                    TypeFlags.SetType(ClassIDType.ResourceManager, true, false);
                }

                assetsManager.Silent = o.Silent;
                assetsManager.Game = game;
                assetsManager.SpecifyUnityVersion = o.UnityVersion;
                if (o.Key != default)
                {
                    MiHoYoBinData.Encrypted = true;
                    MiHoYoBinData.Key = o.Key;
                }

                if (game.Type.IsGISubGroup() && o.AIFile != null)
                {
                    ResourceIndex.FromFile(o.AIFile.FullName);
                }
                else if (game.Type.IsGISubGroup() && !string.IsNullOrWhiteSpace(o.AIVersion))
                {
                    Logger.Info($"Loading AI v{o.AIVersion}");
                    var aiPath = "";
                    if (Task.Run(() => AIVersionManager.FetchVersions()).Result)
                    {
                        aiPath = Task.Run(() => AIVersionManager.FetchAI(o.AIVersion)).Result;
                    }

                    if (!string.IsNullOrEmpty(aiPath))
                    {
                        ResourceIndex.FromFile(aiPath);
                    }
                    else
                    {
                        Logger.Warning($"Could not load AI v{o.AIVersion}");
                    }
                }

                if (o.DummyDllFolder != null)
                {
                    assemblyLoader.Load(o.DummyDllFolder.FullName);
                }

                Logger.Info("Scanning for files...");
                var files = o.Input.Attributes.HasFlag(FileAttributes.Directory) ? Directory.GetFiles(o.Input.FullName, "*.*", SearchOption.AllDirectories).OrderBy(x => x.Length).ToArray() : new string[] { o.Input.FullName };
                var dependencyMapFiles = files;
                var containerExcludeFilters = GetContainerExcludeFilters(o.WorkMode, o.Profile3D, o.ContainerExcludeFilter);
                var nameExcludeFilters = GetNameExcludeFilters(o.WorkMode, o.Profile3D, o.NameExcludeFilter);
                if (o.WorkMode != WorkMode.Export && !containerExcludeFilters.IsNullOrEmpty())
                {
                    var originalCount = files.Length;
                    files = files.Where(x => !containerExcludeFilters.Any(y => y.IsMatch(x))).ToArray();
                    Logger.Info($"Excluded {originalCount - files.Length} file(s) by 3D path filters.");
                }
                if (o.WorkMode != WorkMode.Export && !o.ContainerFilter.IsNullOrEmpty())
                {
                    var matchedFiles = files
                        .Where(x => o.ContainerFilter.Any(y => y.IsMatch(NormalizeFilterPath(x))))
                        .ToArray();
                    if (matchedFiles.Length > 0 && matchedFiles.Length < files.Length)
                    {
                        Logger.Info($"Prefiltered source files by --containers: {files.Length} -> {matchedFiles.Length}.");
                        files = matchedFiles;
                    }
                }
                Logger.Info($"Found {files.Length} files");
                if (files.Length == 0)
                {
                    Logger.Warning("No files left after applying filters.");
                    return;
                }
                var inputBaseFolder = o.Input.Attributes.HasFlag(FileAttributes.Directory)
                    ? o.Input.FullName
                    : Path.GetDirectoryName(o.Input.FullName);
                var mapName = ResolveMapName(o.MapName, game, inputBaseFolder);
                Logger.Info($"Using map name {mapName}");
                var needsModelDependencies = ClassIDType.GameObject.CanExport() || ClassIDType.Animator.CanExport();
                var cabMapSourceFiles = needsModelDependencies ? dependencyMapFiles : files;
                var expectedCabMapFileCount = cabMapSourceFiles.Length;

                if (o.MapOp.HasFlag(MapOpType.CABMap))
                {
                    if (o.MapOp.HasFlag(MapOpType.Load))
                    {
                        using (ProfileLogger.Measure("build_cab_map", new Dictionary<string, object> { ["fileCount"] = cabMapSourceFiles.Length, ["mapName"] = mapName }))
                        {
                            AssetsHelper.BuildCABMap(cabMapSourceFiles, mapName, inputBaseFolder, game);
                        }
                    }
                    else
                    {
                        bool cabMapLoaded;
                        using (ProfileLogger.Measure("load_cab_map", new Dictionary<string, object> { ["mapName"] = mapName }))
                        {
                            cabMapLoaded = AssetsHelper.LoadCABMapInternal(mapName);
                        }
                        if (cabMapLoaded && needsModelDependencies && !AssetsHelper.IsCABMapCompleteFor(cabMapSourceFiles, inputBaseFolder))
                        {
                            Logger.Warning("CAB dependency map was built from an incomplete source set. Rebuilding from the full input so Unity external references stay resolvable.");
                            using (ProfileLogger.Measure("build_cab_map", new Dictionary<string, object> { ["fileCount"] = expectedCabMapFileCount, ["mapName"] = mapName }))
                            {
                                AssetsHelper.BuildCABMap(cabMapSourceFiles, mapName, inputBaseFolder, game);
                            }
                            using (ProfileLogger.Measure("load_cab_map", new Dictionary<string, object> { ["mapName"] = mapName }))
                            {
                                cabMapLoaded = AssetsHelper.LoadCABMapInternal(mapName);
                            }
                        }
                        if (cabMapLoaded)
                        {
                            assetsManager.ResolveDependencies = true;
                        }
                    }
                }
                if (o.MapOp.HasFlag(MapOpType.AssetMap))
                {
                    if (o.MapOp.HasFlag(MapOpType.Load))
                    {
                        files = AssetsHelper.ParseAssetMap(mapName, o.MapType, classTypeFilter, o.NameFilter, o.ContainerFilter);
                    }
                    else
                    {
                        Task.Run(() => AssetsHelper.BuildAssetMap(files, mapName, game, o.Output.FullName, o.MapType, classTypeFilter, o.NameFilter, o.ContainerFilter)).Wait();
                    }
                }
                if (o.MapOp.HasFlag(MapOpType.Both))
                {
                    Task.Run(() => AssetsHelper.BuildBoth(cabMapSourceFiles, mapName, inputBaseFolder, game, o.Output.FullName, o.MapType, classTypeFilter, o.NameFilter, o.ContainerFilter)).Wait();
                }
                if (needsModelDependencies && o.MapOp.Equals(MapOpType.None))
                {
                    bool cabMapLoaded;
                    using (ProfileLogger.Measure("load_cab_map", new Dictionary<string, object> { ["mapName"] = mapName }))
                    {
                        cabMapLoaded = AssetsHelper.LoadCABMapInternal(mapName);
                    }
                    if (cabMapLoaded && !AssetsHelper.IsCABMapCompleteFor(cabMapSourceFiles, inputBaseFolder))
                    {
                        Logger.Warning("CAB dependency map was built from an incomplete source set. Rebuilding from the full input so Unity external references stay resolvable.");
                        cabMapLoaded = false;
                    }
                    if (!cabMapLoaded)
                    {
                        Logger.Info("Building CAB dependency map for model materials...");
                        using (ProfileLogger.Measure("build_cab_map", new Dictionary<string, object> { ["fileCount"] = expectedCabMapFileCount, ["mapName"] = mapName }))
                        {
                            AssetsHelper.BuildCABMap(cabMapSourceFiles, mapName, inputBaseFolder, game);
                        }
                        using (ProfileLogger.Measure("load_cab_map", new Dictionary<string, object> { ["mapName"] = mapName }))
                        {
                            cabMapLoaded = AssetsHelper.LoadCABMapInternal(mapName);
                        }
                    }
                    if (cabMapLoaded)
                    {
                        assetsManager.ResolveDependencies = true;
                    }
                }
                if (o.MapOp.Equals(MapOpType.None) || o.MapOp.HasFlag(MapOpType.Load))
                {
                    var i = 0;

                    var path = Path.GetDirectoryName(Path.GetFullPath(files[0]));
                    ImportHelper.MergeSplitAssets(path);
                    var toReadFile = ImportHelper.ProcessingSplitFiles(files.ToList());

                    var fileList = new List<string>(toReadFile);
                    foreach (var batch in ChunkFiles(fileList, GetEffectiveBatchSize(o.WorkMode)))
                    {
                        using (ProfileLogger.Measure("load_batch", new Dictionary<string, object>
                        {
                            ["batchSize"] = batch.Count,
                            ["firstFile"] = batch.FirstOrDefault(),
                        }))
                        {
                            assetsManager.LoadFiles(batch.ToArray());
                        }
                        if (assetsManager.assetsFileList.Count > 0)
                        {
                            using (ProfileLogger.Measure("build_asset_data", new Dictionary<string, object>
                            {
                                ["loadedAssetFiles"] = assetsManager.assetsFileList.Count,
                                ["firstFile"] = batch.FirstOrDefault(),
                            }))
                            {
                                BuildAssetData(
                                    classTypeFilter,
                                    o.NameFilter,
                                    o.ContainerFilter,
                                    nameExcludeFilters,
                                    containerExcludeFilters,
                                    ref i
                                );
                            }
                            if (exportableAssets.Count > 0)
                            {
                                using (ProfileLogger.Measure("export_batch", new Dictionary<string, object>
                                {
                                    ["exportableCount"] = exportableAssets.Count,
                                    ["firstFile"] = batch.FirstOrDefault(),
                                }))
                                {
                                    ExportCurrentAssets(o.Output.FullName, o.GroupAssetsType, o.AssetExportType);
                                }
                            }
                        }
                        exportableAssets.Clear();
                        using (ProfileLogger.Measure("clear_batch", new Dictionary<string, object>
                        {
                            ["firstFile"] = batch.FirstOrDefault(),
                        }))
                        {
                            assetsManager.Clear();
                        }
                    }
                }
                if (Properties.Settings.Default.scrapeMonos)
                {
                    File.WriteAllLines("./Maps/PathStrings_Sorted.txt", PathStrings.Distinct().OrderBy(p => p));
                    File.WriteAllLines("./Maps/VOStrings_Sorted.txt", VOStrings.Distinct().OrderBy(p => p));
                    File.WriteAllLines("./Maps/EventStrings_Sorted.txt", EventStrings.Distinct().OrderBy(p => p));
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }

        private static string ResolveMapName(string mapName, Game game, string inputBaseFolder)
        {
            if (!string.IsNullOrWhiteSpace(mapName))
            {
                return mapName;
            }

            var fullPath = Path.GetFullPath(inputBaseFolder).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            );
            var key = $"{game.Type}|{fullPath}".ToLowerInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12].ToLowerInvariant();
            return $"auto_{game.Type}_{hash}";
        }

        private static Regex[] GetContainerExcludeFilters(
            WorkMode workMode,
            Model3DProfile profile3D,
            Regex[] userExcludeFilters
        )
        {
            if (workMode == WorkMode.Export)
            {
                return userExcludeFilters ?? Array.Empty<Regex>();
            }

            var filters = new List<Regex>();
            filters.Add(
                new Regex(
                    @"(^|[\\/])([^\\/]*(ui|emoji)[^\\/]*|sounds?|audio|videos?|camera)([\\/]|\.|$)",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled
                )
            );

            if (workMode == WorkMode.SplitObjects)
            {
                filters.Add(
                    new Regex(
                        @"(^|[\\/])animations?([\\/]|\.|$)",
                        RegexOptions.IgnoreCase | RegexOptions.Compiled
                    )
                );
            }

            if (profile3D == Model3DProfile.Core)
            {
                filters.Add(
                    new Regex(
                        @"(^|[\\/])assets[\\/](outgame[\\/]res[\\/]effect|graphics[\\/]effect|ingame[\\/]prefabs[\\/](managers|datas)|stagetest|graphics[\\/]temp|graphics[\\/]stageoutgame[\\/]playerselect|graphics[\\/]character[\\/]pc[\\/]_common)([\\/]|\.|$)",
                        RegexOptions.IgnoreCase | RegexOptions.Compiled
                    )
                );
                filters.Add(
                    new Regex(
                        @"(^|[\\/])sphere([\\/]|\.|$)",
                        RegexOptions.IgnoreCase | RegexOptions.Compiled
                    )
                );
            }

            if (!userExcludeFilters.IsNullOrEmpty())
            {
                filters.AddRange(userExcludeFilters);
            }

            return filters.ToArray();
        }

        private static string NormalizeFilterPath(string path)
        {
            return path?.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/') ?? string.Empty;
        }

        private static Regex[] GetNameExcludeFilters(WorkMode workMode, Model3DProfile profile3D, Regex[] userExcludeFilters)
        {
            if (workMode == WorkMode.Export)
            {
                return userExcludeFilters ?? Array.Empty<Regex>();
            }

            var filters = new List<Regex>
            {
                new Regex(
                    @"camera|maincam|handycam|uicam",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled
                ),
            };

            if (profile3D == Model3DProfile.Core)
            {
                filters.Add(
                    new Regex(
                        @"(^|[_\-\s])(shadow|dummy)($|[_\-\s])|(?:^|[_\-\s])test$|^sphere$|^groundshadow$",
                        RegexOptions.IgnoreCase | RegexOptions.Compiled
                    )
                );
            }

            if (!userExcludeFilters.IsNullOrEmpty())
            {
                filters.AddRange(userExcludeFilters);
            }

            return filters.ToArray();
        }

        private static void ConfigureWorkModeTypes(WorkMode workMode, FbxAnimationMode animationMode)
        {
            if (workMode == WorkMode.Export)
            {
                return;
            }

            TypeFlags.SetType(ClassIDType.GameObject, true, workMode == WorkMode.SplitObjects || workMode == WorkMode.Library);
            TypeFlags.SetType(ClassIDType.Animator, true, workMode == WorkMode.Animator || workMode == WorkMode.Library);
            TypeFlags.SetType(ClassIDType.Transform, true, false);
            TypeFlags.SetType(ClassIDType.MeshFilter, true, false);
            TypeFlags.SetType(ClassIDType.MeshRenderer, true, false);
            TypeFlags.SetType(ClassIDType.SkinnedMeshRenderer, true, false);
            TypeFlags.SetType(ClassIDType.Mesh, true, false);
            TypeFlags.SetType(ClassIDType.Material, true, false);
            TypeFlags.SetType(ClassIDType.Texture2D, true, false);
            TypeFlags.SetType(ClassIDType.AudioClip, false, false);
            TypeFlags.SetType(ClassIDType.VideoClip, false, false);
            TypeFlags.SetType(ClassIDType.MovieTexture, false, false);
            TypeFlags.SetType(ClassIDType.Sprite, false, false);
            TypeFlags.SetType(ClassIDType.SpriteAtlas, false, false);
            TypeFlags.SetType(ClassIDType.Font, false, false);
            TypeFlags.SetType(ClassIDType.TextAsset, false, false);
            TypeFlags.SetType(ClassIDType.MonoBehaviour, false, false);
            TypeFlags.SetType(ClassIDType.MiHoYoBinData, false, false);
            TypeFlags.SetType(ClassIDType.Shader, false, false);

            if (workMode == WorkMode.Library || animationMode != FbxAnimationMode.Skip)
            {
                TypeFlags.SetType(ClassIDType.AnimationClip, true, true);
                TypeFlags.SetType(ClassIDType.AnimatorController, true, false);
                TypeFlags.SetType(ClassIDType.AnimatorOverrideController, true, false);
                TypeFlags.SetType(ClassIDType.Avatar, true, false);
            }

            if (workMode == WorkMode.Library && Studio.IncludeShaders)
            {
                TypeFlags.SetType(ClassIDType.Shader, true, true);
            }
        }

        private static int GetEffectiveBatchSize(WorkMode workMode)
        {
            return workMode == WorkMode.Export ? 1 : Math.Max(1, Studio.BatchFiles);
        }

        private static IEnumerable<List<string>> ChunkFiles(List<string> files, int size)
        {
            for (var i = 0; i < files.Count; i += size)
            {
                yield return files.Skip(i).Take(size).ToList();
            }
        }
    }
}
