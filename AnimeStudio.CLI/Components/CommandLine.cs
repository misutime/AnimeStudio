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
        Animator
    }

    public enum FbxAnimationMode
    {
        Skip,
        Auto,
        All
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
                optionsBinder.FbxAnimationMode,
                optionsBinder.MaxExportTasks,
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
        public FbxAnimationMode FbxAnimationMode { get; set; }
        public int MaxExportTasks { get; set; }
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
        public readonly Option<FbxAnimationMode> FbxAnimationMode;
        public readonly Option<int> MaxExportTasks;
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
            WorkMode = new Option<WorkMode>("--mode", "Specify 3D export mode: Export, SplitObjects, or Animator.");
            GameName = new Option<string>("--game", $"Specify Game.") { IsRequired = true };
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
            FbxAnimationMode = new Option<FbxAnimationMode>("--fbx_animation", "Specify FBX animation export mode: Skip, Auto, or All.");
            MaxExportTasks = new Option<int>("--max_export_tasks", "Reserved maximum parallel export tasks for future batch export.");
            DummyDllFolder = new Option<DirectoryInfo>("--dummy_dlls", "Specify DummyDll path.").LegalFilePathsOnly();
            Input = new Argument<FileInfo>("input_path", "Input file/folder.").LegalFilePathsOnly();
            Output = new Argument<DirectoryInfo>("output_path", "Output folder.").LegalFilePathsOnly();

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

            LoggerFlags.SetDefaultValue(new LoggerEvent[] { LoggerEvent.Debug, LoggerEvent.Info, LoggerEvent.Warning, LoggerEvent.Error });
            GroupAssetsType.SetDefaultValue(AssetGroupOption.ByType);
            AssetExportType.SetDefaultValue(ExportType.Convert);
            WorkMode.SetDefaultValue(AnimeStudio.CLI.WorkMode.Export);
            FbxAnimationMode.SetDefaultValue(AnimeStudio.CLI.FbxAnimationMode.Skip);
            MaxExportTasks.SetDefaultValue(1);
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
            FbxAnimationMode = bindingContext.ParseResult.GetValueForOption(FbxAnimationMode),
            MaxExportTasks = bindingContext.ParseResult.GetValueForOption(MaxExportTasks),
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
