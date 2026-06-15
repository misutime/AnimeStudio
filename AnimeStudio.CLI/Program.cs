using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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
                Studio.IncludeStaticMeshes = o.IncludeStaticMeshes;
                Studio.IncludeVfx = o.IncludeVfx;
                CliExportOptions.IncludeVfx = o.IncludeVfx;

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

                if (o.ExportFbxFromGltf != null)
                {
                    if (o.BakedFbxOutput == null || string.IsNullOrWhiteSpace(o.BakedFbxOutput.FullName))
                    {
                        Logger.Error("--export_fbx_from_gltf requires --baked_fbx_output.");
                        return;
                    }

                    BlenderFbxExporter.Export(
                        o.ExportFbxFromGltf.FullName,
                        o.BakedFbxOutput.FullName,
                        o.Blender?.FullName
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

                if (o.GeneratePreviewFromLibrary != null)
                {
                    PreviewGltfGenerator.GenerateFromLibrary(
                        o.GeneratePreviewFromLibrary.FullName,
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

                if (o.PackModelAnimationsFromLibrary != null)
                {
                    PreviewGltfGenerator.GeneratePackFromLibrary(
                        o.PackModelAnimationsFromLibrary.FullName,
                        o.GameName,
                        o.PreviewModel,
                        o.PackAnimations,
                        o.PackOutput?.FullName,
                        o.PackLimit,
                        o.PreviewSourceRoot?.FullName,
                        o.IndexPath?.FullName
                    );
                    return;
                }

                if (o.ValidateAnimationPreviewsFromLibrary != null)
                {
                    PreviewGltfGenerator.ValidatePreviewBatchFromLibrary(
                        o.ValidateAnimationPreviewsFromLibrary.FullName,
                        o.GameName,
                        o.PreviewModel,
                        o.PackAnimations ?? o.PreviewAnimation,
                        o.PreviewValidationOutput?.FullName ?? o.PackOutput?.FullName,
                        o.PreviewValidationLimit,
                        o.PreviewValidationKind.ToString(),
                        o.PreviewValidationForce,
                        o.PreviewSourceRoot?.FullName,
                        o.IndexPath?.FullName
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
                        o.UnityBakeAvatarAsset,
                        o.UnityBakeOutput?.FullName,
                        o.UnityBakeFps,
                        o.UnityProbeMuscles,
                        o.RunUnityBake,
                        o.UnityBakeWorkerQueue?.FullName,
                        o.BakedGltfOutput?.FullName,
                        o.BakedFbxOutput?.FullName,
                        o.Blender?.FullName
                    );
                    return;
                }

                if (o.GenerateUnityBakeRequestFromLibrary != null)
                {
                    UnityBakeRequestGenerator.GenerateFromLibrary(
                        o.GenerateUnityBakeRequestFromLibrary.FullName,
                        o.PreviewModel,
                        o.PreviewAnimation,
                        o.PreviewOutput?.FullName,
                        o.PreviewSourceRoot?.FullName,
                        o.UnityProject?.FullName,
                        o.UnityEditor?.FullName,
                        o.UnityBakeModelPrefab,
                        o.UnityBakeAnimationClip,
                        o.UnityBakeAvatarAsset,
                        o.UnityBakeOutput?.FullName,
                        o.UnityBakeFps,
                        o.UnityProbeMuscles,
                        o.RunUnityBake,
                        o.UnityBakeWorkerQueue?.FullName,
                        o.BakedGltfOutput?.FullName,
                        o.BakedFbxOutput?.FullName,
                        o.Blender?.FullName,
                        o.IndexPath?.FullName
                    );
                    return;
                }

                if (o.BakeAnimationPreviewsFromLibrary != null)
                {
                    UnityBakeRequestGenerator.GenerateBatchFromLibrary(
                        o.BakeAnimationPreviewsFromLibrary.FullName,
                        o.PreviewModel,
                        o.PackAnimations ?? o.PreviewAnimation,
                        o.PreviewValidationOutput?.FullName ?? o.PackOutput?.FullName ?? o.PreviewOutput?.FullName,
                        o.PreviewSourceRoot?.FullName,
                        o.UnityProject?.FullName,
                        o.UnityEditor?.FullName,
                        o.UnityBakeModelPrefab,
                        o.UnityBakeAnimationClip,
                        o.UnityBakeAvatarAsset,
                        o.UnityBakeFps,
                        o.RunUnityBake,
                        o.UnityBakeWorkerQueue?.FullName,
                        o.BakedFbxOutput?.FullName,
                        o.Blender?.FullName,
                        o.PreviewValidationLimit,
                        o.IndexPath?.FullName,
                        o.PreviewValidationForce
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

                if (o.CompareUnityBakeResult != null)
                {
                    UnityBakeComparisonReporter.Compare(
                        o.CompareUnityBakeResult.FullName,
                        o.CompareGltf?.FullName,
                        o.CompareOutput?.FullName
                    );
                    return;
                }

                if (o.GenerateSkeletonGuide != null)
                {
                    BlenderSkeletonGuideGenerator.Generate(
                        o.GenerateSkeletonGuide.FullName,
                        o.PreviewOutput?.FullName,
                        o.SkeletonGuideCatalog?.FullName,
                        o.Blender?.FullName
                    );
                    return;
                }

                if (o.GenerateAssembledPreviewGltf != null)
                {
                    ModularPreviewAssembler.Generate(
                        o.GenerateAssembledPreviewGltf.FullName,
                        o.GameName,
                        o.PreviewModel,
                        o.PreviewAnimation,
                        o.PreviewOutput?.FullName,
                        o.PreviewSourceRoot?.FullName,
                        o.AssemblyModules
                    );
                    return;
                }

                if (o.RebuildLibraryIndexes != null)
                {
                    if (o.RequireFreshSourceAnimationRelations)
                    {
                        var strictSourceIndex = Path.Combine(o.RebuildLibraryIndexes.FullName, "unity_source_index.db");
                        SQLiteSourceIndexBuilder.WriteAnimationRelationHealthReport(strictSourceIndex, requireHealthy: true);
                    }

                    Studio.RebuildLibraryIndexes(o.RebuildLibraryIndexes.FullName);
                    return;
                }

                if (o.MigrateLibraryRelativePaths != null)
                {
                    LibraryRelativePathMigrator.Migrate(o.MigrateLibraryRelativePaths.FullName, rebuildSqlite: true);
                    return;
                }

                if (o.BuildSqliteIndex != null)
                {
                    if (o.RequireFreshSourceAnimationRelations)
                    {
                        var strictSourceIndex = o.SourceIndex?.FullName ?? Path.Combine(o.BuildSqliteIndex.FullName, "unity_source_index.db");
                        SQLiteSourceIndexBuilder.WriteAnimationRelationHealthReport(strictSourceIndex, requireHealthy: true);
                    }

                    SQLiteLibraryIndexBuilder.Build(o.BuildSqliteIndex.FullName, o.IndexPath?.FullName, o.SkipSqliteFileIndex, o.SkipSqliteSidecarScan, o.SkipSqliteJsonDocuments, o.SourceIndex?.FullName);
                    TryAutoRecoverImportedAvatarAssets(o, o.BuildSqliteIndex.FullName, null, null);
                    return;
                }

                if (o.VerifySourceIndex != null)
                {
                    SQLiteSourceIndexBuilder.WriteAnimationRelationHealthReport(o.VerifySourceIndex.FullName, requireHealthy: o.RequireFreshSourceAnimationRelations);
                    return;
                }

                if (o.ExportAvatarOracle != null)
                {
                    AvatarOracleExporter.Export(
                        o.ExportAvatarOracle.FullName,
                        o.PreviewModel,
                        o.PreviewOutput?.FullName
                    );
                    return;
                }

                if (o.RecoverImportedAvatarAssets != null)
                {
                    var unitySettings = ResolveUnityBakeSettings(o, o.RecoverImportedAvatarAssets.FullName);
                    if (string.IsNullOrWhiteSpace(unitySettings.UnityProject))
                    {
                        Logger.Error("--recover_imported_avatar_assets requires a Unity bake project. Configure it once in Browser Unity settings, write .as_browser_cache\\unity_bake_settings.json, set ANIMESTUDIO_UNITY_BAKE_PROJECT, or pass --unity_project.");
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(o.GameName))
                    {
                        Logger.Error("--recover_imported_avatar_assets requires --game.");
                        Console.WriteLine(GameManager.SupportedGames());
                        return;
                    }

                    var recoveryGame = GameManager.GetGame(o.GameName);
                    if (recoveryGame == null)
                    {
                        Logger.Error("Invalid Game !!");
                        Console.WriteLine(GameManager.SupportedGames());
                        return;
                    }

                    if (recoveryGame is UnityCNGame recoveryUnityCNGame)
                    {
                        UnityCN.SetKey(recoveryUnityCNGame.Key);
                        Logger.Info($"[UnityCN] Selected Key is {recoveryUnityCNGame.Key.Name} - {recoveryUnityCNGame.Key.Key}");
                    }

                    Studio.Game = recoveryGame;
                    AvatarAssetRecoveryExporter.Recover(
                        o.RecoverImportedAvatarAssets.FullName,
                        unitySettings.UnityProject,
                        unitySettings.UnityEditor,
                        recoveryGame,
                        o.UnityVersion,
                        o.PreviewSourceRoot?.FullName,
                        o.PreviewModel,
                        o.AvatarRecoveryLimit,
                        o.AvatarRecoveryForce,
                        o.RunUnityBake || !string.IsNullOrWhiteSpace(unitySettings.UnityEditor),
                        o.IndexPath?.FullName
                    );
                    return;
                }

                if (o.Input == null || o.Output == null)
                {
                    Logger.Error("input_path and output_path are required for export. Use --convert_model_textures, --generate_preview_gltf, --pack_model_animations, --generate_unity_bake_request, --apply_unity_bake_result, --recover_imported_avatar_assets, --generate_skeleton_guide, --rebuild_library_indexes, --migrate_library_relative_paths, --build_sqlite_index, --verify_source_index, --export_avatar_oracle, or --build_source_sqlite_index for post-export commands.");
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
                CliExportOptions.ExportFullDecodedAnimationCurves = o.ExportFullDecodedAnimationCurves;
                CliExportOptions.ModelSource = o.ModelSource;
                CliExportOptions.TextureMode = o.TextureMode;

                if (o.DumpUnityBlockChunks)
                {
                    UnityBlockChunkDumper.Dump(o.Input.FullName, o.Output.FullName, game);
                    return;
                }
                CliExportOptions.OutputRoot = o.Output.FullName;
                Logger.FileLogging = Settings.Default.enableFileLogging;
                AssetsHelper.Minimal = Settings.Default.minimalAssetMap;
                AssetsHelper.SetUnityVersion(o.UnityVersion);
                o.Output.Create();
                ProfileLogger.Initialize(o.Output.FullName, o.ProfileLog);

                TypeFlags.SetTypes(JsonConvert.DeserializeObject<Dictionary<ClassIDType, (bool, bool)>>(Settings.Default.types));
                TypeFlags.SetType(ClassIDType.Texture2DArray, true, true);

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
                        catch
                        {
                            Logger.Error($"{typeStr} has invalid format, skipping...");
                            continue;
                        }
                    }

                    classTypeFilter = classTypeFilterList.ToArray();

                    if (ClassIDType.GameObject.CanExport() || ClassIDType.Animator.CanExport())
                    {
                        TypeFlags.SetType(ClassIDType.Texture2D, true, exportTexture2D);
                        TypeFlags.SetType(ClassIDType.Texture2DArray, true, false);
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
                if (o.BuildSourceSqliteIndex)
                {
                    ConfigureSourceIndexTypes();
                }
                WarnForLikelyGenshinPathMismatch(o.Input.FullName, game);
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

                if (o.BuildSourceSqliteIndex)
                {
                    SQLiteSourceIndexBuilder.Build(
                        o.Input.FullName,
                        o.Output.FullName,
                        game,
                        o.UnityVersion,
                        Math.Max(1, o.BatchFiles),
                        o.IndexPath?.FullName
                    );
                    return;
                }

                Logger.Info("Scanning for files...");
                var allFiles = o.Input.Attributes.HasFlag(FileAttributes.Directory)
                    ? Directory.GetFiles(o.Input.FullName, "*.*", SearchOption.AllDirectories)
                    : new string[] { o.Input.FullName };
                var files = allFiles
                    .Where(x => SQLiteSourceIndexBuilder.IsLikelyUnityLoadableFile(x, game))
                    .OrderBy(SafeFileLength)
                    .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var skippedNonUnityFiles = allFiles.Length - files.Length;
                if (skippedNonUnityFiles > 0)
                {
                    Logger.Info($"Skipped {skippedNonUnityFiles} sidecar/non-Unity file(s) before Library load.");
                }
                var dependencyMapFiles = files;
                var inputBaseFolder = o.Input.Attributes.HasFlag(FileAttributes.Directory)
                    ? o.Input.FullName
                    : Path.GetDirectoryName(o.Input.FullName);
                if (!o.SourceFileFilter.IsNullOrEmpty())
                {
                    files = FilterSourceFilesForTargetedExport(files, inputBaseFolder, o.SourceFileFilter);
                    Logger.Info($"--source_files selected {files.Length} Unity source file(s) for this targeted export. Default full Library exports should leave this unset.");
                    if (files.Length == 0)
                    {
                        Logger.Warning("--source_files did not match any Unity-loadable input files.");
                        return;
                    }
                }
                var containerExcludeFilters = GetContainerExcludeFilters(o.WorkMode, o.Profile3D, o.ContainerExcludeFilter);
                var nameExcludeFilters = GetNameExcludeFilters(o.WorkMode, o.Profile3D, o.NameExcludeFilter);
                if (o.WorkMode != WorkMode.Export && !containerExcludeFilters.IsNullOrEmpty())
                {
                    Logger.Info("3D path excludes will be applied to export candidates only; source files stay complete to preserve Unity PPtr dependencies.");
                }
                if (o.WorkMode != WorkMode.Export && !o.ContainerFilter.IsNullOrEmpty())
                {
                    Logger.Info("--containers will filter export candidates only; source files stay complete to preserve Unity PPtr dependencies.");
                }
                Logger.Info($"Found {files.Length} files");
                if (files.Length == 0)
                {
                    Logger.Warning("No files left after applying filters.");
                    return;
                }
                var sourceIndexPath = SQLiteSourceIndexRuntime.ResolveIndexPath(
                    o.SourceIndex?.FullName,
                    o.Input.FullName,
                    o.Output.FullName
                );
                SQLiteSourceIndexRuntime.LoadResult sourceIndexResult = null;
                if (string.IsNullOrWhiteSpace(sourceIndexPath)
                    && o.WorkMode == WorkMode.Library
                    && o.MapOp.Equals(MapOpType.None))
                {
                    Logger.Info("No SQLite source index was supplied or found. Building unity_source_index.db in the output directory before Library export.");
                    sourceIndexPath = SQLiteSourceIndexBuilder.Build(
                        o.Input.FullName,
                        o.Output.FullName,
                        game,
                        o.UnityVersion,
                        Math.Max(1, o.BatchFiles)
                    );
                }

                if (!string.IsNullOrWhiteSpace(sourceIndexPath))
                {
                    if (o.RequireFreshSourceAnimationRelations && o.WorkMode == WorkMode.Library)
                    {
                        SQLiteSourceIndexBuilder.WriteAnimationRelationHealthReport(sourceIndexPath, requireHealthy: true);
                    }

                    sourceIndexResult = SQLiteSourceIndexRuntime.LoadIntoAssetsHelper(
                        sourceIndexPath,
                        o.Input.FullName,
                        game.Name
                    );
                    SQLiteSourceIndexRuntime.WriteUsageReport(o.Output.FullName, sourceIndexResult);
                    if (sourceIndexResult != null)
                    {
                        assetsManager.ResolveDependencies = true;
                    }
                }
                else if (o.WorkMode == WorkMode.Library || o.WorkMode == WorkMode.AudioLibrary)
                {
                    Logger.Warning("No SQLite source index was supplied or found. Legacy CAB/Asset maps are only available through explicit --map_op compatibility commands.");
                }

                var mapName = ResolveMapName(o.MapName, game, inputBaseFolder);
                Logger.Info($"Using map name {mapName}");
                var needsModelDependencies = !o.InspectUnityFiles && (ClassIDType.GameObject.CanExport() || ClassIDType.Animator.CanExport());
                var cabMapSourceFiles = needsModelDependencies ? dependencyMapFiles : files;
                var expectedCabMapFileCount = cabMapSourceFiles.Length;

                if (sourceIndexResult == null
                    && o.WorkMode == WorkMode.Library
                    && o.MapOp.Equals(MapOpType.None)
                    && needsModelDependencies)
                {
                    throw new InvalidOperationException("Library export requires a SQLite Unity source index. Build it with --build_source_sqlite_index, pass --source_index, or let the default Library command auto-build unity_source_index.db in the output directory.");
                }

                if (sourceIndexResult != null && (o.MapOp.HasFlag(MapOpType.CABMap) || o.MapOp.HasFlag(MapOpType.Both)))
                {
                    Logger.Warning("SQLite source index is active; skipping legacy CABMap build/load. Use --source_index as the dependency source for full exports.");
                }

                if (sourceIndexResult == null && o.MapOp.HasFlag(MapOpType.CABMap))
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
                if (sourceIndexResult == null && o.MapOp.HasFlag(MapOpType.Both))
                {
                    Task.Run(() => AssetsHelper.BuildBoth(cabMapSourceFiles, mapName, inputBaseFolder, game, o.Output.FullName, o.MapType, classTypeFilter, o.NameFilter, o.ContainerFilter)).Wait();
                }
                if (sourceIndexResult != null && o.MapOp.HasFlag(MapOpType.AssetMap))
                {
                    Logger.Warning("Legacy AssetMap is still an explicit compatibility command. SQLite source index remains the preferred dependency source.");
                }
                if (sourceIndexResult == null && needsModelDependencies && o.MapOp.Equals(MapOpType.None))
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
                    var inspectReports = new List<object>();

                    var path = Path.GetDirectoryName(Path.GetFullPath(files[0]));
                    ImportHelper.MergeSplitAssets(path);
                    var toReadFile = ImportHelper.ProcessingSplitFiles(files.ToList());

                    var fileList = new List<string>(toReadFile);
                    foreach (var batch in ChunkFiles(fileList, GetEffectiveBatchSize(o.WorkMode)))
                    {
                        var largestBatchFile = batch
                            .Select(x => new { Path = x, Bytes = SafeFileLength(x) })
                            .OrderByDescending(x => x.Bytes)
                            .FirstOrDefault();
                        var batchBytes = batch.Sum(SafeFileLength);
                        using (StartLibraryLoadHeartbeat(batch.Count, batchBytes, largestBatchFile?.Path, largestBatchFile?.Bytes ?? 0, assetsManager))
                        using (ProfileLogger.Measure("load_batch", new Dictionary<string, object>
                        {
                            ["batchSize"] = batch.Count,
                            ["batchBytes"] = batchBytes,
                            ["largestFile"] = largestBatchFile?.Path,
                            ["largestFileBytes"] = largestBatchFile?.Bytes ?? 0,
                            ["firstFile"] = batch.FirstOrDefault(),
                        }))
                        {
                            assetsManager.LoadFiles(batch.ToArray());
                        }
                        if (o.InspectUnityFiles)
                        {
                            inspectReports.AddRange(BuildUnityFileInspect(assetsManager, batch));
                            assetsManager.Clear();
                            continue;
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
                                    o.PathIdFilter,
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
                            if (o.WorkMode == WorkMode.Library && Studio.IncludeVfx)
                            {
                                using (ProfileLogger.Measure("vfx_texture_preview_cache_batch", new Dictionary<string, object>
                                {
                                    ["exportableCount"] = exportableAssets.Count,
                                    ["firstFile"] = batch.FirstOrDefault(),
                                }))
                                {
                                    Studio.ExportVfxTexturePreviewCache(o.Output.FullName);
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
                    if (o.InspectUnityFiles)
                    {
                        WriteUnityFileInspect(o.Output.FullName, inspectReports);
                    }
                    else if (o.WorkMode == WorkMode.Library)
                    {
                        if (o.SkipSqliteIndex)
                        {
                            Logger.Info("Skipping library SQLite index build because --skip_sqlite_index is set.");
                        }
                        GenerateLibraryIndexes(o.Output.FullName, skipSqliteIndex: o.SkipSqliteIndex);
                        if (!o.SkipSqliteIndex)
                        {
                            TryAutoRecoverImportedAvatarAssets(o, o.Output.FullName, game, inputBaseFolder);
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
                Console.Error.WriteLine(e);
                Environment.ExitCode = 1;
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

        private static string[] FilterSourceFilesForTargetedExport(
            string[] files,
            string inputBaseFolder,
            string[] sourceFileFilters
        )
        {
            var inputRoot = Path.GetFullPath(inputBaseFolder ?? string.Empty).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            );
            var wanted = sourceFileFilters
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(NormalizeSourceFileFilter)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (wanted.Count == 0)
            {
                return files;
            }

            return files
                .Where(x => wanted.Contains(NormalizeSourceFileForMatch(x, inputRoot)))
                .ToArray();
        }

        private static string NormalizeSourceFileFilter(string path)
        {
            return path
                .Trim()
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace('\\', '/');
        }

        private static string NormalizeSourceFileForMatch(string path, string inputRoot)
        {
            var fullPath = Path.GetFullPath(path);
            var relative = fullPath;
            if (!string.IsNullOrWhiteSpace(inputRoot)
                && fullPath.StartsWith(inputRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                relative = fullPath[(inputRoot.Length + 1)..];
            }

            return NormalizeSourceFileFilter(relative);
        }

        private static void WarnForLikelyGenshinPathMismatch(string inputPath, Game game)
        {
            var fullPath = Path.GetFullPath(inputPath);
            var normalized = fullPath.Replace('\\', '/');
            var looksLikeGenshinInstall =
                normalized.Contains("Genshin Impact Game", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("YuanShen_Data", StringComparison.OrdinalIgnoreCase);

            if (!looksLikeGenshinInstall || game.Type.IsGIGroup())
            {
                return;
            }

            var yuanShenDataIndex = normalized.IndexOf("YuanShen_Data", StringComparison.OrdinalIgnoreCase);
            var blocksPath = yuanShenDataIndex >= 0
                ? Path.Combine(
                    fullPath[..(yuanShenDataIndex + "YuanShen_Data".Length)],
                    "StreamingAssets",
                    "AssetBundles",
                    "blocks"
                )
                : Path.Combine(fullPath, "YuanShen_Data", "StreamingAssets", "AssetBundles", "blocks");

            Logger.Warning(
                "Input path looks like a Genshin Impact install, but --game is not a GI profile. " +
                "For Genshin Library export, use --game GI and the AssetBundles blocks folder, for example: " +
                blocksPath
            );
        }

        private static IEnumerable<object> BuildUnityFileInspect(AssetsManager assetsManager, IReadOnlyCollection<string> batch)
        {
            foreach (var assetsFile in assetsManager.assetsFileList)
            {
                var rawTypeCounts = assetsFile.m_Objects
                    .GroupBy(x => Enum.IsDefined(typeof(ClassIDType), x.classID) ? ((ClassIDType)x.classID).ToString() : $"Unknown_{x.classID}")
                    .OrderByDescending(x => x.Count())
                    .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.Count());
                var parsedTypeCounts = assetsFile.Objects
                    .GroupBy(x => x.type.ToString())
                    .OrderByDescending(x => x.Count())
                    .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.Count());
                var sampleNames = assetsFile.Objects
                    .Where(x => x is NamedObject && !string.IsNullOrWhiteSpace(x.Name))
                    .GroupBy(x => x.type.ToString())
                    .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        x => x.Key,
                        x => x
                            .Select(y => y.Name)
                            .Where(y => !string.IsNullOrWhiteSpace(y))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Take(30)
                            .ToArray()
                    );
                var animatorControllers = assetsFile.Objects
                    .OfType<AnimatorController>()
                    .Select(BuildAnimatorControllerInspect)
                    .ToArray();
                var avatars = assetsFile.Objects
                    .OfType<Avatar>()
                    .Select(BuildAvatarInspect)
                    .ToArray();

                yield return new
                {
                    source = assetsFile.originalPath ?? assetsFile.fullName,
                    file = assetsFile.fileName,
                    unityVersion = assetsFile.unityVersion,
                    platform = assetsFile.m_TargetPlatform.ToString(),
                    rawObjectCount = assetsFile.m_Objects.Count,
                    parsedObjectCount = assetsFile.Objects.Count,
                    rawTypeCounts,
                    parsedTypeCounts,
                    sampleNames,
                    avatars,
                    animatorControllers,
                    batch = batch.ToArray(),
                };
            }
        }

        private static object BuildAvatarInspect(Avatar avatar)
        {
            return new
            {
                name = avatar.Name,
                pathId = avatar.m_PathID,
                avatarSize = avatar.m_AvatarSize,
                hasHumanDescription = avatar.m_HumanDescription != null,
                humanDescriptionReadRule = avatar.m_HumanDescriptionReadRule,
                humanDescriptionBytesRemainingBeforeRead = avatar.m_HumanDescriptionBytesRemainingBeforeRead,
                humanBoneCount = avatar.m_HumanDescription?.m_Human?.Count ?? 0,
                skeletonBoneCount = avatar.m_HumanDescription?.m_Skeleton?.Count ?? 0,
                avatarSkeletonNodeCount = avatar.m_Avatar?.m_AvatarSkeleton?.m_Node?.Count ?? 0,
                avatarSkeletonPoseCount = avatar.m_Avatar?.m_AvatarSkeletonPose?.m_X?.Length ?? 0,
                avatarDefaultPoseCount = avatar.m_Avatar?.m_DefaultPose?.m_X?.Length ?? 0,
                humanSkeletonNodeCount = avatar.m_Avatar?.m_Human?.m_Skeleton?.m_Node?.Count ?? 0,
                humanSkeletonPoseCount = avatar.m_Avatar?.m_Human?.m_SkeletonPose?.m_X?.Length ?? 0,
                humanBoneIndexCount = avatar.m_Avatar?.m_Human?.m_HumanBoneIndex?.Length ?? 0,
                tosCount = avatar.m_TOS?.Count ?? 0,
            };
        }

        private static object BuildAnimatorControllerInspect(AnimatorController controller)
        {
            var stateMachines = controller.m_Controller?.m_StateMachineArray ?? new List<StateMachineConstant>();
            return new
            {
                name = controller.Name,
                pathId = controller.m_PathID,
                tosCount = controller.m_TOS?.Count ?? 0,
                clips = controller.m_AnimationClips?.Select((x, index) => new
                {
                    index,
                    fileId = x.m_FileID,
                    pathId = x.m_PathID,
                }).ToArray() ?? Array.Empty<object>(),
                layers = controller.m_Controller?.m_LayerArray?.Select((x, index) => new
                {
                    index,
                    stateMachineIndex = x.m_StateMachineIndex,
                    stateMachineMotionSetIndex = x.m_StateMachineMotionSetIndex,
                }).ToArray() ?? Array.Empty<object>(),
                stateMachines = stateMachines.Select((machine, machineIndex) => new
                {
                    machineIndex,
                    defaultState = machine.m_DefaultState,
                    motionSetCount = machine.m_MotionSetCount,
                    states = machine.m_StateConstantArray?.Select((state, stateIndex) => new
                    {
                        stateIndex,
                        nameId = state.m_NameID,
                        name = TryGetTos(controller, state.m_NameID),
                        pathId = state.m_PathID,
                        fullPathId = state.m_FullPathID,
                        fullPath = TryGetTos(controller, state.m_FullPathID),
                        speed = state.m_Speed,
                        cycleOffset = state.m_CycleOffset,
                        loop = state.m_Loop,
                        mirror = state.m_Mirror,
                        blendTreeIndexArray = state.m_BlendTreeConstantIndexArray,
                        blendTrees = state.m_BlendTreeConstantArray?.Select((tree, treeIndex) => new
                        {
                            treeIndex,
                            nodes = tree.m_NodeArray?.Select((node, nodeIndex) => new
                            {
                                nodeIndex,
                                blendType = node.m_BlendType,
                                blendEventId = node.m_BlendEventID,
                                blendEvent = TryGetTos(controller, node.m_BlendEventID),
                                blendEventYId = node.m_BlendEventYID,
                                blendEventY = TryGetTos(controller, node.m_BlendEventYID),
                                childIndices = node.m_ChildIndices,
                                childThresholds = node.m_ChildThresholdArray,
                                clipId = node.m_ClipID,
                                clip = TryGetTos(controller, node.m_ClipID),
                                clipIndex = node.m_ClipIndex,
                                duration = node.m_Duration,
                                cycleOffset = node.m_CycleOffset,
                                mirror = node.m_Mirror,
                            }).ToArray() ?? Array.Empty<object>(),
                        }).ToArray() ?? Array.Empty<object>(),
                    }).ToArray() ?? Array.Empty<object>(),
                }).ToArray(),
            };
        }

        private static string TryGetTos(AnimatorController controller, uint id)
        {
            return controller.m_TOS != null && controller.m_TOS.TryGetValue(id, out var value)
                ? value
                : null;
        }

        private static void WriteUnityFileInspect(string outputFolder, IReadOnlyCollection<object> reports)
        {
            Directory.CreateDirectory(outputFolder);
            var output = Path.Combine(outputFolder, "unity_file_inspect.json");
            var summary = new
            {
                generatedAt = DateTime.UtcNow,
                fileCount = reports.Count,
                files = reports,
            };
            File.WriteAllText(output, JsonConvert.SerializeObject(summary, Formatting.Indented));
            Logger.Info($"Wrote Unity file inspect report: {output}");
        }

        private static Regex[] GetContainerExcludeFilters(
            WorkMode workMode,
            Model3DProfile profile3D,
            Regex[] userExcludeFilters
        )
        {
            if (workMode == WorkMode.Export || workMode == WorkMode.AudioLibrary)
            {
                return userExcludeFilters ?? Array.Empty<Regex>();
            }

            var filters = new List<Regex>();
            filters.Add(
                new Regex(
                    @"(^|[\\/])((ui|uiassets?|uiprefabs?|userinterface|emoji|emojis|sounds?|audio|videos?|camera)([\\/]|\.|$)|[^\\/]*[_\-.](ui|emoji)([_\-.]|[\\/]|$))",
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

        private static string NormalizeFilterPath(string path, string baseFolder = null)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            var filterPath = path;
            if (!string.IsNullOrWhiteSpace(baseFolder))
            {
                try
                {
                    filterPath = Path.GetRelativePath(baseFolder, path);
                    if (filterPath.StartsWith("..", StringComparison.Ordinal))
                    {
                        filterPath = path;
                    }
                }
                catch
                {
                    filterPath = path;
                }
            }

            return filterPath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        }

        private static Regex[] GetNameExcludeFilters(WorkMode workMode, Model3DProfile profile3D, Regex[] userExcludeFilters)
        {
            if (workMode == WorkMode.Export || workMode == WorkMode.AudioLibrary)
            {
                return userExcludeFilters ?? Array.Empty<Regex>();
            }

            var filters = new List<Regex>();

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

            if (workMode == WorkMode.AudioLibrary)
            {
                TypeFlags.SetType(ClassIDType.AnimationClip, false, false);
                TypeFlags.SetType(ClassIDType.Animator, false, false);
                TypeFlags.SetType(ClassIDType.GameObject, false, false);
                TypeFlags.SetType(ClassIDType.Transform, false, false);
                TypeFlags.SetType(ClassIDType.MeshFilter, false, false);
                TypeFlags.SetType(ClassIDType.MeshRenderer, false, false);
                TypeFlags.SetType(ClassIDType.SkinnedMeshRenderer, false, false);
                TypeFlags.SetType(ClassIDType.Mesh, false, false);
                TypeFlags.SetType(ClassIDType.Material, false, false);
                TypeFlags.SetType(ClassIDType.Texture2D, false, false);
                TypeFlags.SetType(ClassIDType.Texture2DArray, false, false);
                TypeFlags.SetType(ClassIDType.AudioClip, true, true);
                TypeFlags.SetType(ClassIDType.VideoClip, false, false);
                TypeFlags.SetType(ClassIDType.MovieTexture, false, false);
                TypeFlags.SetType(ClassIDType.Sprite, false, false);
                TypeFlags.SetType(ClassIDType.SpriteAtlas, false, false);
                TypeFlags.SetType(ClassIDType.Font, false, false);
                TypeFlags.SetType(ClassIDType.TextAsset, false, false);
                TypeFlags.SetType(ClassIDType.MonoBehaviour, false, false);
                TypeFlags.SetType(ClassIDType.MiHoYoBinData, false, false);
                TypeFlags.SetType(ClassIDType.Shader, false, false);
                return;
            }

            TypeFlags.SetType(ClassIDType.GameObject, true, workMode == WorkMode.SplitObjects || workMode == WorkMode.Library);
            TypeFlags.SetType(ClassIDType.Animator, true, workMode == WorkMode.Animator || workMode == WorkMode.Library);
            TypeFlags.SetType(ClassIDType.Transform, true, false);
            TypeFlags.SetType(ClassIDType.MeshFilter, true, false);
            TypeFlags.SetType(ClassIDType.MeshRenderer, true, false);
            TypeFlags.SetType(ClassIDType.SkinnedMeshRenderer, true, false);
            TypeFlags.SetType(ClassIDType.Mesh, true, workMode == WorkMode.Library && Studio.IncludeStaticMeshes);
            TypeFlags.SetType(ClassIDType.Material, true, false);
            TypeFlags.SetType(ClassIDType.Texture2D, true, workMode == WorkMode.Library);
            TypeFlags.SetType(ClassIDType.Texture2DArray, true, workMode == WorkMode.Library);
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

        private static void ConfigureSourceIndexTypes()
        {
            TypeFlags.SetTypes(new Dictionary<ClassIDType, (bool, bool)>());
            foreach (ClassIDType type in Enum.GetValues(typeof(ClassIDType)))
            {
                TypeFlags.SetType(type, false, false);
            }

            var parseTypes = new[]
            {
                ClassIDType.Animation,
                ClassIDType.AnimationClip,
                ClassIDType.Animator,
                ClassIDType.AnimatorController,
                ClassIDType.AnimatorOverrideController,
                ClassIDType.AssetBundle,
                ClassIDType.AudioClip,
                ClassIDType.Avatar,
                ClassIDType.GameObject,
                ClassIDType.IndexObject,
                ClassIDType.Material,
                ClassIDType.Mesh,
                ClassIDType.MeshFilter,
                ClassIDType.MeshRenderer,
                ClassIDType.MonoBehaviour,
                ClassIDType.MonoScript,
                ClassIDType.RectTransform,
                ClassIDType.ResourceManager,
                ClassIDType.Shader,
                ClassIDType.SkinnedMeshRenderer,
                ClassIDType.Sprite,
                ClassIDType.SpriteAtlas,
                ClassIDType.TextAsset,
                ClassIDType.Texture2D,
                ClassIDType.Texture2DArray,
                ClassIDType.Transform,
            };

            foreach (var type in parseTypes)
            {
                TypeFlags.SetType(type, true, false);
            }
        }

        private static int GetEffectiveBatchSize(WorkMode workMode)
        {
            return workMode == WorkMode.Export ? 1 : Math.Max(1, Studio.BatchFiles);
        }

        private static long SafeFileLength(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }

        private static void TryAutoRecoverImportedAvatarAssets(Options o, string libraryRoot, Game game, string sourceRoot)
        {
            if (o == null || string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
            {
                return;
            }

            var unitySettings = ResolveUnityBakeSettings(o, libraryRoot);
            if (string.IsNullOrWhiteSpace(unitySettings.UnityProject))
            {
                Logger.Info("Imported Avatar asset auto recovery skipped because no Unity bake project is configured. Configure Browser Unity settings, .as_browser_cache\\unity_bake_settings.json, ANIMESTUDIO_UNITY_BAKE_PROJECT, or --unity_project to enable the default recovery step.");
                return;
            }

            var effectiveGame = game;
            if (effectiveGame == null)
            {
                if (string.IsNullOrWhiteSpace(o.GameName))
                {
                    Logger.Warning("Imported Avatar asset auto recovery skipped because --game is missing.");
                    return;
                }

                effectiveGame = GameManager.GetGame(o.GameName);
            }

            if (effectiveGame == null)
            {
                Logger.Warning("Imported Avatar asset auto recovery skipped because the game profile is invalid.");
                return;
            }

            try
            {
                AvatarAssetRecoveryExporter.Recover(
                    libraryRoot,
                    unitySettings.UnityProject,
                    unitySettings.UnityEditor,
                    effectiveGame,
                    o.UnityVersion,
                    o.PreviewSourceRoot?.FullName ?? sourceRoot,
                    selector: null,
                    limit: 0,
                    force: false,
                    runProbe: !string.IsNullOrWhiteSpace(unitySettings.UnityEditor),
                    explicitIndexPath: o.IndexPath?.FullName);
            }
            catch (Exception e)
            {
                Logger.Warning($"Imported Avatar asset auto recovery failed; Library export remains usable but Humanoid bake may still need Avatar metadata. {e.GetType().Name}: {e.Message}");
            }
        }

        private static UnityBakeSettings ResolveUnityBakeSettings(Options o, string libraryRoot)
        {
            var local = LoadUnityBakeSettings(string.IsNullOrWhiteSpace(libraryRoot)
                ? null
                : Path.Combine(libraryRoot, ".as_browser_cache", "unity_bake_settings.json"));
            var global = LoadUnityBakeSettings(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AnimeStudio",
                "LibraryBrowser",
                "settings.json"));

            var unityProject = FirstNotEmpty(
                o?.UnityProject?.FullName,
                local.UnityProject,
                global.UnityProject,
                Environment.GetEnvironmentVariable("ANIMESTUDIO_UNITY_BAKE_PROJECT"),
                FindDefaultUnityBakeProject());
            if (!IsValidUnityBakeProject(unityProject))
            {
                unityProject = null;
            }

            var unityEditor = NormalizeUnityEditorPath(FirstNotEmpty(
                o?.UnityEditor?.FullName,
                local.UnityEditor,
                global.UnityEditor,
                Environment.GetEnvironmentVariable("ANIMESTUDIO_UNITY_EDITOR"),
                FindDefaultUnityEditor()));

            return new UnityBakeSettings(unityProject, unityEditor);
        }

        private static UnityBakeSettings LoadUnityBakeSettings(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return UnityBakeSettings.Empty;
            }

            try
            {
                var json = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
                return new UnityBakeSettings(
                    (string)json["unityProject"],
                    NormalizeUnityEditorPath((string)json["unityEditor"]));
            }
            catch
            {
                return UnityBakeSettings.Empty;
            }
        }

        private static string FirstNotEmpty(params string[] values)
        {
            return values?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        }

        private static string NormalizeUnityEditorPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var path = value.Trim().Trim('"');
            if (File.Exists(path))
            {
                return Path.GetFullPath(path);
            }

            if (Directory.Exists(path))
            {
                var direct = Path.Combine(path, "Unity.exe");
                if (File.Exists(direct))
                {
                    return Path.GetFullPath(direct);
                }

                var editor = Path.Combine(path, "Editor", "Unity.exe");
                if (File.Exists(editor))
                {
                    return Path.GetFullPath(editor);
                }
            }

            return null;
        }

        private static string FindDefaultUnityEditor()
        {
            var hubRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Unity",
                "Hub",
                "Editor");
            if (Directory.Exists(hubRoot))
            {
                var hubEditor = Directory.EnumerateDirectories(hubRoot)
                    .Select(dir => new
                    {
                        Path = Path.Combine(dir, "Editor", "Unity.exe"),
                        Modified = Directory.GetLastWriteTimeUtc(dir),
                    })
                    .Where(x => File.Exists(x.Path))
                    .OrderByDescending(x => x.Modified)
                    .Select(x => x.Path)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(hubEditor))
                {
                    return hubEditor;
                }
            }

            var legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Unity",
                "Editor",
                "Unity.exe");
            return File.Exists(legacy) ? legacy : null;
        }

        private static string FindDefaultUnityBakeProject()
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AnimeStudioUnityProject"),
                @"D:\misutime\AnimeStudioUnityProject",
            };
            return candidates.FirstOrDefault(IsValidUnityBakeProject);
        }

        private static bool IsValidUnityBakeProject(string path)
        {
            return !string.IsNullOrWhiteSpace(path)
                && Directory.Exists(path)
                && File.Exists(Path.Combine(path, "ProjectSettings", "ProjectSettings.asset"))
                && File.Exists(Path.Combine(path, "Assets", "AnimeStudio.UnityBake", "Editor", "AnimeStudioBakeCli.cs"));
        }

        private readonly struct UnityBakeSettings
        {
            public static readonly UnityBakeSettings Empty = new(null, null);

            public UnityBakeSettings(string unityProject, string unityEditor)
            {
                UnityProject = unityProject;
                UnityEditor = unityEditor;
            }

            public string UnityProject { get; }
            public string UnityEditor { get; }
        }

        private static IDisposable StartLibraryLoadHeartbeat(
            int batchFileCount,
            long batchBytes,
            string largestFile,
            long largestFileBytes,
            AssetsManager manager)
        {
            return new LibraryLoadHeartbeat(batchFileCount, batchBytes, largestFile, largestFileBytes, manager);
        }

        private sealed class LibraryLoadHeartbeat : IDisposable
        {
            private readonly CancellationTokenSource cancellation = new();
            private readonly Task task;
            private readonly DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
            private readonly int batchFileCount;
            private readonly long batchBytes;
            private readonly string largestFile;
            private readonly long largestFileBytes;
            private readonly AssetsManager manager;

            public LibraryLoadHeartbeat(
                int batchFileCount,
                long batchBytes,
                string largestFile,
                long largestFileBytes,
                AssetsManager manager)
            {
                this.batchFileCount = batchFileCount;
                this.batchBytes = batchBytes;
                this.largestFile = largestFile;
                this.largestFileBytes = largestFileBytes;
                this.manager = manager;
                task = Task.Run(Run);
            }

            private async Task Run()
            {
                while (!cancellation.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), cancellation.Token).ConfigureAwait(false);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }

                    if (cancellation.IsCancellationRequested)
                    {
                        break;
                    }

                    var elapsedMs = (DateTimeOffset.UtcNow - startedUtc).TotalMilliseconds;
                    var data = new Dictionary<string, object>
                    {
                        ["batchFileCount"] = batchFileCount,
                        ["batchBytes"] = batchBytes,
                        ["largestFile"] = largestFile,
                        ["largestFileBytes"] = largestFileBytes,
                        ["loadedAssetFiles"] = manager.assetsFileList.Count,
                        ["elapsedMs"] = elapsedMs,
                    };
                    ProfileLogger.Event("load_batch_heartbeat", data);
                    Logger.Info($"Library load heartbeat: {batchFileCount} file(s), loaded {manager.assetsFileList.Count} asset file(s), elapsed {elapsedMs / 1000:0}s, largest {Path.GetFileName(largestFile)}.");
                }
            }

            public void Dispose()
            {
                cancellation.Cancel();
                try
                {
                    task.Wait(TimeSpan.FromSeconds(2));
                }
                catch
                {
                    // Heartbeat is diagnostic only.
                }
                cancellation.Dispose();
            }
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
