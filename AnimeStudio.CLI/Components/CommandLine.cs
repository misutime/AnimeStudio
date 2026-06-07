using System;
using System.IO;
using System.Linq;
using System.CommandLine;
using System.CommandLine.Binding;
using System.CommandLine.Parsing;
using System.Text.RegularExpressions;
using System.Collections.Generic;

namespace AnimeStudio.CLI
{
    public enum WorkMode
    {
        Export,
        SplitObjects,
        Animator,
        Library,
        AudioLibrary
    }

    public enum FbxAnimationMode
    {
        Skip,
        Auto,
        All
    }

    public enum ModelExportFormat
    {
        Gltf,
        Glb,
        Fbx
    }

    public enum Model3DProfile
    {
        Core,
        All
    }

    public enum ModelSourceMode
    {
        PrefabPrimary,
        PrefabAndParts,
        RawPartsOnly
    }

    public enum AnimationPackageMode
    {
        Separate,
        Embedded,
        Both
    }

    public static class CommandLine
    {
        public static void Init(string[] args)
        {
            var rootCommand = RegisterOptions();
            rootCommand.Invoke(args);
        }
        public static RootCommand RegisterOptions()
        {
            var optionsBinder = new OptionsBinder();
            var rootCommand = new RootCommand()
            {
                optionsBinder.Silent,
                optionsBinder.LoggerFlags,
                optionsBinder.TypeFilter,
                optionsBinder.NameFilter,
                optionsBinder.ContainerFilter,
                optionsBinder.NameExcludeFilter,
                optionsBinder.ContainerExcludeFilter,
                optionsBinder.WorkMode,
                optionsBinder.GameName,
                optionsBinder.MapOp,
                optionsBinder.MapType,
                optionsBinder.MapName,
                optionsBinder.UnityVersion,
                optionsBinder.GroupAssetsType,
                optionsBinder.AssetExportType,
                optionsBinder.Key,
                optionsBinder.AIFile,
                optionsBinder.AIVersion,
                optionsBinder.ModelRootsOnly,
                optionsBinder.FbxScaleFactor,
                optionsBinder.FbxBoneSize,
                optionsBinder.FbxExportAllNodes,
                optionsBinder.FbxAnimationMode,
                optionsBinder.HumanoidBakeSolver,
                optionsBinder.ModelFormat,
                optionsBinder.AnimationPackage,
                optionsBinder.TextureMode,
                optionsBinder.Profile3D,
                optionsBinder.ModelSource,
                optionsBinder.IncludeShaders,
                optionsBinder.MaxExportTasks,
                optionsBinder.BatchFiles,
                optionsBinder.ModelGcInterval,
                optionsBinder.ProfileLog,
                optionsBinder.ConvertModelTextures,
                optionsBinder.ConvertTextureAssetRoot,
                optionsBinder.ConvertTextureOutput,
                optionsBinder.ConvertTextureFormat,
                optionsBinder.UpdateGltfTextureRefs,
                optionsBinder.GeneratePreviewGltf,
                optionsBinder.GeneratePreviewFromLibrary,
                optionsBinder.GenerateAssembledPreviewGltf,
                optionsBinder.PreviewModel,
                optionsBinder.PreviewAnimation,
                optionsBinder.PreviewOutput,
                optionsBinder.PreviewSourceRoot,
                optionsBinder.AssemblyModules,
                optionsBinder.PackModelAnimations,
                optionsBinder.PackAnimations,
                optionsBinder.PackOutput,
                optionsBinder.PackLimit,
                optionsBinder.GenerateUnityBakeRequest,
                optionsBinder.UnityProject,
                optionsBinder.UnityEditor,
                optionsBinder.UnityBakeModelPrefab,
                optionsBinder.UnityBakeAnimationClip,
                optionsBinder.UnityBakeOutput,
                optionsBinder.UnityBakeFps,
                optionsBinder.RunUnityBake,
                optionsBinder.ApplyUnityBakeResult,
                optionsBinder.BakedGltfOutput,
                optionsBinder.BakedFbxOutput,
                optionsBinder.GenerateSkeletonGuide,
                optionsBinder.SkeletonGuideCatalog,
                optionsBinder.RebuildLibraryIndexes,
                optionsBinder.BuildSqliteIndex,
                optionsBinder.BuildSourceSqliteIndex,
                optionsBinder.SourceIndex,
                optionsBinder.IndexPath,
                optionsBinder.InspectUnityFiles,
                optionsBinder.Blender,
                optionsBinder.DummyDllFolder,
                optionsBinder.Input,
                optionsBinder.Output
            };

            rootCommand.SetHandler(Program.Run, optionsBinder);

            return rootCommand;
        }
    }
    public class Options
    {
        public bool Silent { get; set; }
        public LoggerEvent[] LoggerFlags { get; set; }
        public string[] TypeFilter { get; set; }
        public Regex[] NameFilter { get; set; }
        public Regex[] ContainerFilter { get; set; }
        public Regex[] NameExcludeFilter { get; set; }
        public Regex[] ContainerExcludeFilter { get; set; }
        public WorkMode WorkMode { get; set; }
        public string GameName { get; set; }
        public MapOpType MapOp { get; set; }
        public ExportListType MapType { get; set; }
        public string MapName { get; set; }
        public string UnityVersion { get; set; }
        public AssetGroupOption GroupAssetsType { get; set; }
        public ExportType AssetExportType { get; set; }
        public byte Key { get; set; }
        public FileInfo AIFile { get; set; }
        public string AIVersion { get; set; }
        public bool ModelRootsOnly { get; set; }
        public float? FbxScaleFactor { get; set; }
        public int? FbxBoneSize { get; set; }
        public bool FbxExportAllNodes { get; set; }
        public FbxAnimationMode FbxAnimationMode { get; set; }
        public string HumanoidBakeSolver { get; set; }
        public ModelExportFormat ModelFormat { get; set; }
        public AnimationPackageMode AnimationPackage { get; set; }
        public AnimeStudio.TextureExportMode TextureMode { get; set; }
        public Model3DProfile Profile3D { get; set; }
        public ModelSourceMode ModelSource { get; set; }
        public bool IncludeShaders { get; set; }
        public int MaxExportTasks { get; set; }
        public int BatchFiles { get; set; }
        public int ModelGcInterval { get; set; }
        public string ProfileLog { get; set; }
        public FileInfo ConvertModelTextures { get; set; }
        public DirectoryInfo ConvertTextureAssetRoot { get; set; }
        public DirectoryInfo ConvertTextureOutput { get; set; }
        public AnimeStudio.ImageFormat ConvertTextureFormat { get; set; }
        public bool UpdateGltfTextureRefs { get; set; }
        public FileInfo GeneratePreviewGltf { get; set; }
        public DirectoryInfo GeneratePreviewFromLibrary { get; set; }
        public FileInfo GenerateAssembledPreviewGltf { get; set; }
        public string PreviewModel { get; set; }
        public string PreviewAnimation { get; set; }
        public DirectoryInfo PreviewOutput { get; set; }
        public DirectoryInfo PreviewSourceRoot { get; set; }
        public string AssemblyModules { get; set; }
        public FileInfo PackModelAnimations { get; set; }
        public string PackAnimations { get; set; }
        public DirectoryInfo PackOutput { get; set; }
        public int PackLimit { get; set; }
        public FileInfo GenerateUnityBakeRequest { get; set; }
        public DirectoryInfo UnityProject { get; set; }
        public FileInfo UnityEditor { get; set; }
        public string UnityBakeModelPrefab { get; set; }
        public string UnityBakeAnimationClip { get; set; }
        public FileInfo UnityBakeOutput { get; set; }
        public int UnityBakeFps { get; set; }
        public bool RunUnityBake { get; set; }
        public FileInfo ApplyUnityBakeResult { get; set; }
        public FileInfo BakedGltfOutput { get; set; }
        public FileInfo BakedFbxOutput { get; set; }
        public FileInfo GenerateSkeletonGuide { get; set; }
        public FileInfo SkeletonGuideCatalog { get; set; }
        public DirectoryInfo RebuildLibraryIndexes { get; set; }
        public DirectoryInfo BuildSqliteIndex { get; set; }
        public bool BuildSourceSqliteIndex { get; set; }
        public FileInfo SourceIndex { get; set; }
        public FileInfo IndexPath { get; set; }
        public bool InspectUnityFiles { get; set; }
        public FileInfo Blender { get; set; }
        public DirectoryInfo DummyDllFolder { get; set; }
        public FileInfo Input { get; set; }
        public DirectoryInfo Output { get; set; }
    }

    public class OptionsBinder : BinderBase<Options>
    {
        public readonly Option<bool> Silent;
        public readonly Option<LoggerEvent[]> LoggerFlags;
        public readonly Option<string[]> TypeFilter;
        public readonly Option<Regex[]> NameFilter;
        public readonly Option<Regex[]> ContainerFilter;
        public readonly Option<Regex[]> NameExcludeFilter;
        public readonly Option<Regex[]> ContainerExcludeFilter;
        public readonly Option<WorkMode> WorkMode;
        public readonly Option<string> GameName;
        public readonly Option<MapOpType> MapOp;
        public readonly Option<ExportListType> MapType;
        public readonly Option<string> MapName;
        public readonly Option<string> UnityVersion;
        public readonly Option<AssetGroupOption> GroupAssetsType;
        public readonly Option<ExportType> AssetExportType;
        public readonly Option<byte> Key;
        public readonly Option<FileInfo> AIFile;
        public readonly Option<string> AIVersion;
        public readonly Option<bool> ModelRootsOnly;
        public readonly Option<float?> FbxScaleFactor;
        public readonly Option<int?> FbxBoneSize;
        public readonly Option<bool> FbxExportAllNodes;
        public readonly Option<FbxAnimationMode> FbxAnimationMode;
        public readonly Option<string> HumanoidBakeSolver;
        public readonly Option<ModelExportFormat> ModelFormat;
        public readonly Option<AnimationPackageMode> AnimationPackage;
        public readonly Option<AnimeStudio.TextureExportMode> TextureMode;
        public readonly Option<Model3DProfile> Profile3D;
        public readonly Option<ModelSourceMode> ModelSource;
        public readonly Option<bool> IncludeShaders;
        public readonly Option<int> MaxExportTasks;
        public readonly Option<int> BatchFiles;
        public readonly Option<int> ModelGcInterval;
        public readonly Option<string> ProfileLog;
        public readonly Option<FileInfo> ConvertModelTextures;
        public readonly Option<DirectoryInfo> ConvertTextureAssetRoot;
        public readonly Option<DirectoryInfo> ConvertTextureOutput;
        public readonly Option<AnimeStudio.ImageFormat> ConvertTextureFormat;
        public readonly Option<bool> UpdateGltfTextureRefs;
        public readonly Option<FileInfo> GeneratePreviewGltf;
        public readonly Option<DirectoryInfo> GeneratePreviewFromLibrary;
        public readonly Option<FileInfo> GenerateAssembledPreviewGltf;
        public readonly Option<string> PreviewModel;
        public readonly Option<string> PreviewAnimation;
        public readonly Option<DirectoryInfo> PreviewOutput;
        public readonly Option<DirectoryInfo> PreviewSourceRoot;
        public readonly Option<string> AssemblyModules;
        public readonly Option<FileInfo> PackModelAnimations;
        public readonly Option<string> PackAnimations;
        public readonly Option<DirectoryInfo> PackOutput;
        public readonly Option<int> PackLimit;
        public readonly Option<FileInfo> GenerateUnityBakeRequest;
        public readonly Option<DirectoryInfo> UnityProject;
        public readonly Option<FileInfo> UnityEditor;
        public readonly Option<string> UnityBakeModelPrefab;
        public readonly Option<string> UnityBakeAnimationClip;
        public readonly Option<FileInfo> UnityBakeOutput;
        public readonly Option<int> UnityBakeFps;
        public readonly Option<bool> RunUnityBake;
        public readonly Option<FileInfo> ApplyUnityBakeResult;
        public readonly Option<FileInfo> BakedGltfOutput;
        public readonly Option<FileInfo> BakedFbxOutput;
        public readonly Option<FileInfo> GenerateSkeletonGuide;
        public readonly Option<FileInfo> SkeletonGuideCatalog;
        public readonly Option<DirectoryInfo> RebuildLibraryIndexes;
        public readonly Option<DirectoryInfo> BuildSqliteIndex;
        public readonly Option<bool> BuildSourceSqliteIndex;
        public readonly Option<FileInfo> SourceIndex;
        public readonly Option<FileInfo> IndexPath;
        public readonly Option<bool> InspectUnityFiles;
        public readonly Option<FileInfo> Blender;
        public readonly Option<DirectoryInfo> DummyDllFolder;
        public readonly Argument<FileInfo> Input;
        public readonly Argument<DirectoryInfo> Output;

        public OptionsBinder()
        {
            Silent = new Option<bool>("--silent", "Hide log messages.");
            LoggerFlags = new Option<LoggerEvent[]>("--logger_flags", "Flags to control toggle log events.") { AllowMultipleArgumentsPerToken = true, ArgumentHelpName = "Verbose|Debug|Info|etc.." };
            TypeFilter = new Option<string[]>("--types", "Specify unity class type(s)") { AllowMultipleArgumentsPerToken = true, ArgumentHelpName = "Texture2D|Shader:Parse|Sprite:Both|etc.." };
            NameFilter = new Option<Regex[]>("--names", ParseRegexOption, false, "Specify name regex filter(s).") { AllowMultipleArgumentsPerToken = true };
            ContainerFilter = new Option<Regex[]>("--containers", ParseRegexOption, false, "Specify container regex filter(s).") { AllowMultipleArgumentsPerToken = true };
            NameExcludeFilter = new Option<Regex[]>("--names_exclude", ParseRegexOption, false, "Specify name regex exclude filter(s).") { AllowMultipleArgumentsPerToken = true };
            ContainerExcludeFilter = new Option<Regex[]>("--containers_exclude", ParseRegexOption, false, "Specify container/path regex exclude filter(s).") { AllowMultipleArgumentsPerToken = true };
            WorkMode = new Option<WorkMode>("--mode", "Specify export mode: Library, AudioLibrary, Export, SplitObjects, or Animator.");
            GameName = new Option<string>("--game", $"Specify Game.");
            MapOp = new Option<MapOpType>("--map_op", "Specify which map to build.");
            MapType = new Option<ExportListType>("--map_type", "AssetMap output type.");
            MapName = new Option<string>("--map_name", "Specify AssetMap file name. If omitted, a stable name is generated from game and input path.");
            UnityVersion = new Option<string>("--unity_version", "Specify Unity version.");
            GroupAssetsType = new Option<AssetGroupOption>("--group_assets", "Specify how exported assets should be grouped. ByLibrary writes models, textures, materials, and data into separate library folders.");
            AssetExportType = new Option<ExportType>("--export_type", "Specify how assets should be exported.");
            AIFile = new Option<FileInfo>("--ai_file", "Specify asset_index json file path (to recover GI containers).").LegalFilePathsOnly();
            AIVersion = new Option<string>("--ai_version", "Download and load asset_index for the specified GI version (for example 6.0).");
            ModelRootsOnly = new Option<bool>("--model_roots_only", "Export only top-level model GameObjects and skip child mesh parts when their parent model is also exportable.");
            FbxScaleFactor = new Option<float?>("--fbx_scale_factor", "Override FBX scale factor.");
            FbxBoneSize = new Option<int?>("--fbx_bone_size", "Override FBX bone size.");
            FbxExportAllNodes = new Option<bool>("--fbx_export_all_nodes", "Export every Unity transform/helper node in FBX. Disabled by default for clean library models.");
            FbxAnimationMode = new Option<FbxAnimationMode>("--fbx_animation", "Specify FBX animation export mode: Skip, Auto, or All.");
            HumanoidBakeSolver = new Option<string>("--humanoid_bake_solver", "Experimental Humanoid bake solver variant used for glTF preview/animation bake.");
            ModelFormat = new Option<ModelExportFormat>("--model_format", "Specify model export format: Gltf, Glb, or Fbx.");
            AnimationPackage = new Option<AnimationPackageMode>("--animation_package", "Specify animation packaging: Separate exports clips into the animation library; Embedded writes clips into each model; Both does both.");
            TextureMode = new Option<AnimeStudio.TextureExportMode>("--texture_mode", "Specify model texture export mode: Raw, Png, or Reference.");
            Profile3D = new Option<Model3DProfile>("--profile_3d", "Specify 3D export profile: Core filters non-core models; All keeps all model candidates except basic hygiene filters.");
            ModelSource = new Option<ModelSourceMode>("--model_source", "Specify Library model source mode: PrefabPrimary exports prefab/Animator models by default and indexes raw fbx parts; PrefabAndParts exports both; RawPartsOnly exports only raw fbx/source parts.");
            IncludeShaders = new Option<bool>("--include_shaders", "Include shaders in Library mode as experimental safe raw archives.");
            MaxExportTasks = new Option<int>("--max_export_tasks", "Reserved maximum parallel export tasks for future batch export.");
            BatchFiles = new Option<int>("--batch_files", "Number of source files to load per export batch. Higher values reduce repeated dependency loads but use more memory.");
            ModelGcInterval = new Option<int>("--model_gc_interval", "Run a light non-blocking GC after this many exported model candidates in 3D modes. Default 0 disables model-level GC; batch cleanup still performs full cleanup.");
            ProfileLog = new Option<string>("--profile_log", "Write JSONL performance profile events to the specified path. Use 'off' to disable.");
            ConvertModelTextures = new Option<FileInfo>("--convert_model_textures", "Convert only the raw textures referenced by a previously exported glTF model. Does not require the original game folder.").LegalFilePathsOnly();
            ConvertTextureAssetRoot = new Option<DirectoryInfo>("--texture_asset_root", "Root of a previous export. If omitted, it is inferred by walking up from the glTF until Textures/_ModelDependencies is found.").LegalFilePathsOnly();
            ConvertTextureOutput = new Option<DirectoryInfo>("--texture_output", "Output folder for converted model textures. Defaults to a Textures folder next to the glTF.").LegalFilePathsOnly();
            ConvertTextureFormat = new Option<AnimeStudio.ImageFormat>("--texture_output_format", "Output image format for --convert_model_textures.");
            UpdateGltfTextureRefs = new Option<bool>("--update_gltf_texture_refs", "Patch the glTF to reference converted standard image textures where possible.");
            GeneratePreviewGltf = new Option<FileInfo>("--generate_preview_gltf", "Generate a playable preview glTF from model_animations.json by re-exporting one model with one selected animation.").LegalFilePathsOnly();
            GeneratePreviewFromLibrary = new Option<DirectoryInfo>("--generate_preview_from_library", "Generate a playable preview glTF by selecting model and animation from library_index.db in a Library root.").LegalFilePathsOnly();
            GenerateAssembledPreviewGltf = new Option<FileInfo>("--generate_assembled_preview_gltf", "Generate a playable preview glTF and non-destructively add compatible modular character parts such as face, hair, or accessories when their Unity joints can be remapped.").LegalFilePathsOnly();
            PreviewModel = new Option<string>("--preview_model", "Model name, output path, or regex used with preview commands.");
            PreviewAnimation = new Option<string>("--preview_animation", "Animation name, output path, or regex used with preview commands.");
            PreviewOutput = new Option<DirectoryInfo>("--preview_output", "Output folder for preview commands. Defaults to Previews/<model>__<animation> next to the index or Library root.");
            PreviewSourceRoot = new Option<DirectoryInfo>("--preview_source_root", "Full Unity game/source root used by preview and animation-pack commands to resolve dependencies instead of reusing possibly incomplete indexed sample paths.").LegalFilePathsOnly();
            AssemblyModules = new Option<string>("--assembly_modules", "Comma-separated module roles or selectors for --generate_assembled_preview_gltf. Defaults to Face,Hair,Accessory.");
            PackModelAnimations = new Option<FileInfo>("--pack_model_animations", "Generate a reusable animation asset pack from model_animations.json by exporting one model with multiple selected animations.").LegalFilePathsOnly();
            PackAnimations = new Option<string>("--pack_animations", "Comma-separated animation names or regexes used with --pack_model_animations. If omitted, top candidates are used.");
            PackOutput = new Option<DirectoryInfo>("--pack_output", "Output folder for --pack_model_animations.");
            PackLimit = new Option<int>("--pack_limit", "Maximum number of animations to pack when --pack_animations is omitted.");
            GenerateUnityBakeRequest = new Option<FileInfo>("--generate_unity_bake_request", "Generate a Unity Editor bake request from model_animations.json. Unity then samples Animator/Avatar through PlayableGraph and writes baked skeleton TRS.").LegalFilePathsOnly();
            UnityProject = new Option<DirectoryInfo>("--unity_project", "Unity project that contains the AnimeStudio.UnityBake helper scripts. Required when --run_unity_bake is used.").LegalFilePathsOnly();
            UnityEditor = new Option<FileInfo>("--unity_editor", "Unity.exe path used with --run_unity_bake. If omitted, only the request JSON is written.").LegalFilePathsOnly();
            UnityBakeModelPrefab = new Option<string>("--unity_model_prefab", "Unity project asset path for the prepared model prefab, for example Assets/AnimeStudioBake/Input/Bill.prefab.");
            UnityBakeAnimationClip = new Option<string>("--unity_animation_clip", "Unity project asset path for the prepared AnimationClip, for example Assets/AnimeStudioBake/Input/NORMALMOVE_STAND_01.anim.");
            UnityBakeOutput = new Option<FileInfo>("--unity_bake_output", "Output JSON path for Unity baked TRS animation. Defaults to unity_bake_result.json next to the request.");
            UnityBakeFps = new Option<int>("--unity_bake_fps", "Frame rate used by the Unity bake helper.");
            RunUnityBake = new Option<bool>("--run_unity_bake", "Run Unity Editor batchmode immediately after writing the bake request.");
            ApplyUnityBakeResult = new Option<FileInfo>("--apply_unity_bake_result", "Apply unity_bake_result.json or unity_bake_request.json to the source glTF and write a baked playable preview glTF.").LegalFilePathsOnly();
            BakedGltfOutput = new Option<FileInfo>("--baked_gltf_output", "Output glTF path for --apply_unity_bake_result. Defaults to BakedPreview/<model>__<clip>.gltf next to the bake request/result.");
            BakedFbxOutput = new Option<FileInfo>("--baked_fbx_output", "Optional compatibility FBX output path. AnimeStudio first writes the baked glTF, then asks Blender to export FBX for DCC comparison.");
            GenerateSkeletonGuide = new Option<FileInfo>("--generate_skeleton_guide", "Generate a non-destructive Blender CoreHumanoid skeleton guide from an exported FBX/glTF/GLB. Uses asset_catalog.jsonl Unity Avatar relations when available.").LegalFilePathsOnly();
            SkeletonGuideCatalog = new Option<FileInfo>("--skeleton_guide_catalog", "Optional asset_catalog.jsonl used by --generate_skeleton_guide. If omitted, AnimeStudio walks up from the FBX path.").LegalFilePathsOnly();
            RebuildLibraryIndexes = new Option<DirectoryInfo>("--rebuild_library_indexes", "Rebuild summary, validation, skeleton, model-animation, and compact indexes from a previous Library export without loading the original Unity game files. Explicit Animator relations require a fresh export; catalog structural links are rebuilt.").LegalFilePathsOnly();
            BuildSqliteIndex = new Option<DirectoryInfo>("--build_sqlite_index", "Rebuild the reusable SQLite index from a previous Library or AudioLibrary export. Default Library export already writes library_index.db.").LegalFilePathsOnly();
            BuildSourceSqliteIndex = new Option<bool>("--build_source_sqlite_index", "Build a reusable SQLite source index directly from a full Unity game/source folder. Requires input_path, output_path, and --game.");
            SourceIndex = new Option<FileInfo>("--source_index", "SQLite Unity source index used by Library export for dependency resolution. Prefer unity_source_index.db over legacy CAB maps for full exports.").LegalFilePathsOnly();
            IndexPath = new Option<FileInfo>("--index_path", "Output SQLite database path for --build_sqlite_index. Defaults to library_index.db in the export root.");
            InspectUnityFiles = new Option<bool>("--inspect_unity_files", "Load Unity files and write unity_file_inspect.json with object type counts and sample names, without exporting assets.");
            Blender = new Option<FileInfo>("--blender", "Blender executable path used for optional glTF-to-FBX compatibility packaging.").LegalFilePathsOnly();
            DummyDllFolder = new Option<DirectoryInfo>("--dummy_dlls", "Specify DummyDll path.").LegalFilePathsOnly();
            Input = new Argument<FileInfo>("input_path", "Input file/folder.").LegalFilePathsOnly();
            Output = new Argument<DirectoryInfo>("output_path", "Output folder.").LegalFilePathsOnly();
            Input.Arity = ArgumentArity.ZeroOrOne;
            Output.Arity = ArgumentArity.ZeroOrOne;

            Key = new Option<byte>("--key", result =>
            {
                return ParseKey(result.Tokens.Single().Value);
            }, false, "XOR key to decrypt MiHoYoBinData.");

            LoggerFlags.AddValidator(FilterValidator);
            TypeFilter.AddValidator(FilterValidator);
            NameFilter.AddValidator(FilterValidator);
            ContainerFilter.AddValidator(FilterValidator);
            NameExcludeFilter.AddValidator(FilterValidator);
            ContainerExcludeFilter.AddValidator(FilterValidator);
            Key.AddValidator(result =>
            {
                var value = result.Tokens.Single().Value;
                try
                {
                    ParseKey(value);
                }
                catch (Exception e)
                {
                    result.ErrorMessage = "Invalid byte value.\n" + e.Message;
                }
            });

            GameName.FromAmong(GameManager.GetGameNames());

            LoggerFlags.SetDefaultValue(new LoggerEvent[] { LoggerEvent.Info, LoggerEvent.Warning, LoggerEvent.Error });
            GroupAssetsType.SetDefaultValue(AssetGroupOption.ByLibrary);
            AssetExportType.SetDefaultValue(ExportType.Convert);
            WorkMode.SetDefaultValue(AnimeStudio.CLI.WorkMode.Library);
            FbxAnimationMode.SetDefaultValue(AnimeStudio.CLI.FbxAnimationMode.Skip);
            HumanoidBakeSolver.SetDefaultValue("AvatarPreEulerPost");
            ModelFormat.SetDefaultValue(AnimeStudio.CLI.ModelExportFormat.Gltf);
            AnimationPackage.SetDefaultValue(AnimeStudio.CLI.AnimationPackageMode.Separate);
            TextureMode.SetDefaultValue(AnimeStudio.TextureExportMode.Png);
            Profile3D.SetDefaultValue(AnimeStudio.CLI.Model3DProfile.All);
            ModelSource.SetDefaultValue(AnimeStudio.CLI.ModelSourceMode.PrefabPrimary);
            MaxExportTasks.SetDefaultValue(1);
            BatchFiles.SetDefaultValue(16);
            ModelGcInterval.SetDefaultValue(0);
            ProfileLog.SetDefaultValue("export_profile.jsonl");
            ConvertTextureFormat.SetDefaultValue(AnimeStudio.ImageFormat.Png);
            UpdateGltfTextureRefs.SetDefaultValue(true);
            PackLimit.SetDefaultValue(5);
            UnityBakeFps.SetDefaultValue(30);
            MapOp.SetDefaultValue(MapOpType.None);
            MapType.SetDefaultValue(ExportListType.XML);
        }
        
        public byte ParseKey(string value)
        {
            if (value.StartsWith("0x"))
            {
                value = value[2..];
                return Convert.ToByte(value, 0x10);
            }
            else
            {
                return byte.Parse(value);
            }
        }

        public void FilterValidator(OptionResult result)
        {
            var values = result.Tokens.Select(x => x.Value).ToArray();
            foreach (var val in values)
            {
                if (string.IsNullOrWhiteSpace(val))
                {
                    result.ErrorMessage = "Empty string.";
                    return;
                }

                try
                {
                    Regex.Match("", val, RegexOptions.IgnoreCase);
                }
                catch (ArgumentException e)
                {
                    result.ErrorMessage = "Invalid Regex.\n" + e.Message;
                    return;
                }
            }
        }

        protected override Options GetBoundValue(BindingContext bindingContext) =>
        new()
        {
            Silent = bindingContext.ParseResult.GetValueForOption(Silent),
            LoggerFlags = bindingContext.ParseResult.GetValueForOption(LoggerFlags),
            TypeFilter = bindingContext.ParseResult.GetValueForOption(TypeFilter),
            NameFilter = bindingContext.ParseResult.GetValueForOption(NameFilter),
            ContainerFilter = bindingContext.ParseResult.GetValueForOption(ContainerFilter),
            NameExcludeFilter = bindingContext.ParseResult.GetValueForOption(NameExcludeFilter),
            ContainerExcludeFilter = bindingContext.ParseResult.GetValueForOption(ContainerExcludeFilter),
            WorkMode = bindingContext.ParseResult.GetValueForOption(WorkMode),
            GameName = bindingContext.ParseResult.GetValueForOption(GameName),
            MapOp = bindingContext.ParseResult.GetValueForOption(MapOp),
            MapType = bindingContext.ParseResult.GetValueForOption(MapType),
            MapName = bindingContext.ParseResult.GetValueForOption(MapName),
            UnityVersion = bindingContext.ParseResult.GetValueForOption(UnityVersion),
            GroupAssetsType = bindingContext.ParseResult.GetValueForOption(GroupAssetsType),
            AssetExportType = bindingContext.ParseResult.GetValueForOption(AssetExportType),
            Key = bindingContext.ParseResult.GetValueForOption(Key),
            AIFile = bindingContext.ParseResult.GetValueForOption(AIFile),
            AIVersion = bindingContext.ParseResult.GetValueForOption(AIVersion),
            ModelRootsOnly = bindingContext.ParseResult.GetValueForOption(ModelRootsOnly),
                FbxScaleFactor = bindingContext.ParseResult.GetValueForOption(FbxScaleFactor),
                FbxBoneSize = bindingContext.ParseResult.GetValueForOption(FbxBoneSize),
                FbxExportAllNodes = bindingContext.ParseResult.GetValueForOption(FbxExportAllNodes),
                FbxAnimationMode = bindingContext.ParseResult.GetValueForOption(FbxAnimationMode),
                HumanoidBakeSolver = bindingContext.ParseResult.GetValueForOption(HumanoidBakeSolver),
                ModelFormat = bindingContext.ParseResult.GetValueForOption(ModelFormat),
                AnimationPackage = bindingContext.ParseResult.GetValueForOption(AnimationPackage),
                TextureMode = bindingContext.ParseResult.GetValueForOption(TextureMode),
                Profile3D = bindingContext.ParseResult.GetValueForOption(Profile3D),
                ModelSource = bindingContext.ParseResult.GetValueForOption(ModelSource),
                IncludeShaders = bindingContext.ParseResult.GetValueForOption(IncludeShaders),
                MaxExportTasks = bindingContext.ParseResult.GetValueForOption(MaxExportTasks),
                BatchFiles = bindingContext.ParseResult.GetValueForOption(BatchFiles),
                ModelGcInterval = bindingContext.ParseResult.GetValueForOption(ModelGcInterval),
                ProfileLog = bindingContext.ParseResult.GetValueForOption(ProfileLog),
                ConvertModelTextures = bindingContext.ParseResult.GetValueForOption(ConvertModelTextures),
                ConvertTextureAssetRoot = bindingContext.ParseResult.GetValueForOption(ConvertTextureAssetRoot),
                ConvertTextureOutput = bindingContext.ParseResult.GetValueForOption(ConvertTextureOutput),
                ConvertTextureFormat = bindingContext.ParseResult.GetValueForOption(ConvertTextureFormat),
                UpdateGltfTextureRefs = bindingContext.ParseResult.GetValueForOption(UpdateGltfTextureRefs),
                GeneratePreviewGltf = bindingContext.ParseResult.GetValueForOption(GeneratePreviewGltf),
                GeneratePreviewFromLibrary = bindingContext.ParseResult.GetValueForOption(GeneratePreviewFromLibrary),
                GenerateAssembledPreviewGltf = bindingContext.ParseResult.GetValueForOption(GenerateAssembledPreviewGltf),
                PreviewModel = bindingContext.ParseResult.GetValueForOption(PreviewModel),
                PreviewAnimation = bindingContext.ParseResult.GetValueForOption(PreviewAnimation),
                PreviewOutput = bindingContext.ParseResult.GetValueForOption(PreviewOutput),
                PreviewSourceRoot = bindingContext.ParseResult.GetValueForOption(PreviewSourceRoot),
                AssemblyModules = bindingContext.ParseResult.GetValueForOption(AssemblyModules),
                PackModelAnimations = bindingContext.ParseResult.GetValueForOption(PackModelAnimations),
                PackAnimations = bindingContext.ParseResult.GetValueForOption(PackAnimations),
                PackOutput = bindingContext.ParseResult.GetValueForOption(PackOutput),
                PackLimit = bindingContext.ParseResult.GetValueForOption(PackLimit),
                GenerateUnityBakeRequest = bindingContext.ParseResult.GetValueForOption(GenerateUnityBakeRequest),
                UnityProject = bindingContext.ParseResult.GetValueForOption(UnityProject),
                UnityEditor = bindingContext.ParseResult.GetValueForOption(UnityEditor),
                UnityBakeModelPrefab = bindingContext.ParseResult.GetValueForOption(UnityBakeModelPrefab),
                UnityBakeAnimationClip = bindingContext.ParseResult.GetValueForOption(UnityBakeAnimationClip),
                UnityBakeOutput = bindingContext.ParseResult.GetValueForOption(UnityBakeOutput),
                UnityBakeFps = bindingContext.ParseResult.GetValueForOption(UnityBakeFps),
                RunUnityBake = bindingContext.ParseResult.GetValueForOption(RunUnityBake),
                ApplyUnityBakeResult = bindingContext.ParseResult.GetValueForOption(ApplyUnityBakeResult),
                BakedGltfOutput = bindingContext.ParseResult.GetValueForOption(BakedGltfOutput),
                BakedFbxOutput = bindingContext.ParseResult.GetValueForOption(BakedFbxOutput),
                GenerateSkeletonGuide = bindingContext.ParseResult.GetValueForOption(GenerateSkeletonGuide),
                SkeletonGuideCatalog = bindingContext.ParseResult.GetValueForOption(SkeletonGuideCatalog),
                RebuildLibraryIndexes = bindingContext.ParseResult.GetValueForOption(RebuildLibraryIndexes),
                BuildSqliteIndex = bindingContext.ParseResult.GetValueForOption(BuildSqliteIndex),
                BuildSourceSqliteIndex = bindingContext.ParseResult.GetValueForOption(BuildSourceSqliteIndex),
                SourceIndex = bindingContext.ParseResult.GetValueForOption(SourceIndex),
                IndexPath = bindingContext.ParseResult.GetValueForOption(IndexPath),
                InspectUnityFiles = bindingContext.ParseResult.GetValueForOption(InspectUnityFiles),
                Blender = bindingContext.ParseResult.GetValueForOption(Blender),
                DummyDllFolder = bindingContext.ParseResult.GetValueForOption(DummyDllFolder),
                Input = bindingContext.ParseResult.GetValueForArgument(Input),
                Output = bindingContext.ParseResult.GetValueForArgument(Output)
            };

        private static Regex[] ParseRegexOption(ArgumentResult result)
        {
            var items = new List<Regex>();
            var value = result.Tokens.Single().Value;
            if (File.Exists(value))
            {
                var lines = File.ReadLines(value);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        items.Add(new Regex(line, RegexOptions.IgnoreCase));
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }
                }
            }
            else
            {
                items.AddRange(result.Tokens.Select(x => new Regex(x.Value, RegexOptions.IgnoreCase)).ToArray());
            }

            return items.ToArray();
        }
    }
}
