using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                Studio.ModelRootsOnly = o.ModelRootsOnly;
                Studio.WorkMode = o.WorkMode;
                Studio.FbxAnimationMode = o.FbxAnimationMode;
                Studio.MaxExportTasks = Math.Max(1, o.MaxExportTasks);
                CliExportOptions.FbxScaleFactor = o.FbxScaleFactor;
                CliExportOptions.FbxBoneSize = o.FbxBoneSize;
                CliExportOptions.FbxAnimationMode = o.FbxAnimationMode;
                Logger.Default = new ConsoleLogger();
                Logger.Flags = o.LoggerFlags.Aggregate((e, x) => e |= x);
                Logger.FileLogging = Settings.Default.enableFileLogging;
                AssetsHelper.Minimal = Settings.Default.minimalAssetMap;
                AssetsHelper.SetUnityVersion(o.UnityVersion);

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
                o.Output.Create();

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
                Logger.Info($"Found {files.Length} files");
                var inputBaseFolder = o.Input.Attributes.HasFlag(FileAttributes.Directory)
                    ? o.Input.FullName
                    : Path.GetDirectoryName(o.Input.FullName);
                var needsModelDependencies = ClassIDType.GameObject.CanExport() || ClassIDType.Animator.CanExport();

                if (o.MapOp.HasFlag(MapOpType.CABMap))
                {
                    if (o.MapOp.HasFlag(MapOpType.Load))
                    {
                        AssetsHelper.BuildCABMap(files, o.MapName, inputBaseFolder, game);
                    }
                    else
                    {
                        AssetsHelper.LoadCABMapInternal(o.MapName);
                        assetsManager.ResolveDependencies = true;
                    }
                }
                if (o.MapOp.HasFlag(MapOpType.AssetMap))
                {
                    if (o.MapOp.HasFlag(MapOpType.Load))
                    {
                        files = AssetsHelper.ParseAssetMap(o.MapName, o.MapType, classTypeFilter, o.NameFilter, o.ContainerFilter);
                    }
                    else
                    {
                        Task.Run(() => AssetsHelper.BuildAssetMap(files, o.MapName, game, o.Output.FullName, o.MapType, classTypeFilter, o.NameFilter, o.ContainerFilter)).Wait();
                    }
                }
                if (o.MapOp.HasFlag(MapOpType.Both))
                {
                    Task.Run(() => AssetsHelper.BuildBoth(files, o.MapName, inputBaseFolder, game, o.Output.FullName, o.MapType, classTypeFilter, o.NameFilter, o.ContainerFilter)).Wait();
                }
                if (needsModelDependencies && o.MapOp.Equals(MapOpType.None))
                {
                    var cabMapLoaded = AssetsHelper.LoadCABMapInternal(o.MapName);
                    if (!cabMapLoaded)
                    {
                        Logger.Info("Building CAB dependency map for model materials...");
                        AssetsHelper.BuildCABMap(files, o.MapName, inputBaseFolder, game);
                        cabMapLoaded = AssetsHelper.LoadCABMapInternal(o.MapName);
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
                    foreach (var file in fileList)
                    {
                        assetsManager.LoadFiles(file);
                        if (assetsManager.assetsFileList.Count > 0)
                        {
                            BuildAssetData(classTypeFilter, o.NameFilter, o.ContainerFilter, ref i);
                            ExportCurrentAssets(o.Output.FullName, o.GroupAssetsType, o.AssetExportType);
                        }
                        exportableAssets.Clear();
                        assetsManager.Clear();
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

        private static void ConfigureWorkModeTypes(WorkMode workMode, FbxAnimationMode animationMode)
        {
            if (workMode == WorkMode.Export)
            {
                return;
            }

            TypeFlags.SetType(ClassIDType.GameObject, true, workMode == WorkMode.SplitObjects);
            TypeFlags.SetType(ClassIDType.Animator, true, workMode == WorkMode.Animator);
            TypeFlags.SetType(ClassIDType.Transform, true, false);
            TypeFlags.SetType(ClassIDType.MeshFilter, true, false);
            TypeFlags.SetType(ClassIDType.MeshRenderer, true, false);
            TypeFlags.SetType(ClassIDType.SkinnedMeshRenderer, true, false);
            TypeFlags.SetType(ClassIDType.Mesh, true, false);
            TypeFlags.SetType(ClassIDType.Material, true, false);
            TypeFlags.SetType(ClassIDType.Texture2D, true, false);

            if (animationMode != FbxAnimationMode.Skip)
            {
                TypeFlags.SetType(ClassIDType.AnimationClip, true, true);
                TypeFlags.SetType(ClassIDType.AnimatorController, true, false);
                TypeFlags.SetType(ClassIDType.AnimatorOverrideController, true, false);
                TypeFlags.SetType(ClassIDType.Avatar, true, false);
            }
        }
    }
}
