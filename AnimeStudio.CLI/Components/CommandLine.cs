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

    public enum PreviewValidationKind
    {
        All,
        Direct,
        InternalHumanoid
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
                optionsBinder.SourceFileFilter,
                optionsBinder.PathIdFilter,
                optionsBinder.IncludeAnimatorControllerClipClosure,
                optionsBinder.EndfieldVfsFileFilter,
                optionsBinder.EndfieldVfsFileLimit,
                optionsBinder.EndfieldVfsKeepSameLengthSupplemental,
                optionsBinder.EndfieldManifestDeps,
                optionsBinder.EndfieldSourceCabClosure,
                optionsBinder.EndfieldSourceCabClosureDomains,
                optionsBinder.EndfieldSourceCabClosureIncludeAutoRoots,
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
                optionsBinder.ExportFullDecodedAnimationCurves,
                optionsBinder.TextureMode,
                optionsBinder.Profile3D,
                optionsBinder.ModelSource,
                optionsBinder.IncludeStaticMeshes,
                optionsBinder.IncludeVfx,
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
                optionsBinder.ListModelAnimationsFromLibrary,
                optionsBinder.ExportAnimationGltfFromLibrary,
                optionsBinder.ExportAnimationGltfFromFiles,
                optionsBinder.MergeAnimationGltf,
                optionsBinder.ValidateAnimationPreviewsFromLibrary,
                optionsBinder.GenerateAssembledPreviewGltf,
                optionsBinder.PreviewModel,
                optionsBinder.PreviewAnimation,
                optionsBinder.PreviewOutput,
                optionsBinder.PreviewSourceRoot,
                optionsBinder.PreviewForceInternalHumanoidSolve,
                optionsBinder.PreviewAvatar,
                optionsBinder.AssemblyModules,
                optionsBinder.PackModelAnimations,
                optionsBinder.PackModelAnimationsFromLibrary,
                optionsBinder.PackAnimations,
                optionsBinder.PackOutput,
                optionsBinder.PackLimit,
                optionsBinder.PreviewValidationLimit,
                optionsBinder.PreviewValidationOutput,
                optionsBinder.PreviewValidationKind,
                optionsBinder.PreviewValidationForce,
                optionsBinder.GenerateUnityBakeRequest,
                optionsBinder.GenerateUnityBakeRequestFromLibrary,
                optionsBinder.BakeAnimationPreviewsFromLibrary,
                optionsBinder.GenerateUnityBakeAcceleratedRequestFromLibrary,
                optionsBinder.CheckUnityBakeAcceleratedWorker,
                optionsBinder.SyncUnityBakeAcceleratedWorker,
                optionsBinder.RunUnityBakeAccelerated,
                optionsBinder.UnityBakeAcceleratedWorkerQueue,
                optionsBinder.ApplyUnityBakeAcceleratedResult,
                optionsBinder.UnityProject,
                optionsBinder.UnityEditor,
                optionsBinder.UnityBakeModelPrefab,
                optionsBinder.UnityBakeAnimationClip,
                optionsBinder.UnityBakeAnimatorController,
                optionsBinder.UnityBakeAvatarAsset,
                optionsBinder.UnityBakeOutput,
                optionsBinder.UnityBakeFps,
                optionsBinder.UnityProbeMuscles,
                optionsBinder.UnityBakeEnableIkGoalDriver,
                optionsBinder.UnityBakeSampleRecoverableSkippedLayersDiagnostic,
                optionsBinder.UnityBakeRebuildEditorCurveClip,
                optionsBinder.UnityBakeIgnoreImportedAvatar,
                optionsBinder.UnityBakeClipFilterMode,
                optionsBinder.UnityBakeAllowGeneratedControllerDiagnostic,
                optionsBinder.RunUnityBake,
                optionsBinder.UnityBakeWorkerQueue,
                optionsBinder.ApplyUnityBakeResult,
                optionsBinder.CompareUnityBakeResult,
                optionsBinder.CompareGltf,
                optionsBinder.CompareOutput,
                optionsBinder.ExportFbxFromGltf,
                optionsBinder.FbxSkeletonOnly,
                optionsBinder.BakedGltfOutput,
                optionsBinder.BakedFbxOutput,
                optionsBinder.GenerateSkeletonGuide,
                optionsBinder.SkeletonGuideCatalog,
                optionsBinder.RebuildLibraryIndexes,
                optionsBinder.MigrateLibraryRelativePaths,
                optionsBinder.BuildSqliteIndex,
                optionsBinder.ProbeSourceInput,
                optionsBinder.BuildSourceSqliteIndex,
                optionsBinder.VerifySourceIndex,
                optionsBinder.EnsureSourceIndexQueryIndexes,
                optionsBinder.ListSourceModelCandidates,
                optionsBinder.ListSourceModelAnimations,
                optionsBinder.LocateSourceCabs,
                optionsBinder.LocateEndfieldStrings,
                optionsBinder.LocateEndfieldCabs,
                optionsBinder.LocateEndfieldMissingSourceCabs,
                optionsBinder.BuildEndfieldCabLocationIndex,
                optionsBinder.EndfieldCabLocationIndex,
                optionsBinder.InspectEndfieldManifestDeps,
                optionsBinder.ExportAvatarOracle,
                optionsBinder.ExportNarakaAvatarMeshPlan,
                optionsBinder.ExportAvatarMeshDataGltf,
                optionsBinder.NarakaAvatarMeshExternalSkeletonSkinDiagnostic,
                optionsBinder.NarakaAvatarMeshFaceRuntimeSkinDiagnostic,
                optionsBinder.NarakaAvatarMeshRendererBonesSkinDiagnostic,
                optionsBinder.RecoverImportedAvatarAssets,
                optionsBinder.RecoverImportedAnimationClips,
                optionsBinder.RecoverImportedAnimatorControllers,
                optionsBinder.AnimatorControllerClipLibrary,
                optionsBinder.RefreshAnimatorControllerContexts,
                optionsBinder.UnityFileInspect,
                optionsBinder.AvatarRecoveryLimit,
                optionsBinder.AvatarRecoveryForce,
                optionsBinder.DumpUnityBlockChunks,
                optionsBinder.RequireFreshSourceAnimationRelations,
                optionsBinder.SourceCandidateLimit,
                optionsBinder.SourceIndex,
                optionsBinder.IndexPath,
                optionsBinder.SkipSqliteIndex,
                optionsBinder.SkipSqliteFileIndex,
                optionsBinder.SkipSqliteSidecarScan,
                optionsBinder.SkipSqliteJsonDocuments,
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
        public string[] SourceFileFilter { get; set; }
        public long[] PathIdFilter { get; set; }
        public bool IncludeAnimatorControllerClipClosure { get; set; }
        public Regex[] EndfieldVfsFileFilter { get; set; }
        public int EndfieldVfsFileLimit { get; set; }
        public bool EndfieldVfsKeepSameLengthSupplemental { get; set; }
        public FileInfo EndfieldManifestDeps { get; set; }
        public FileInfo[] EndfieldSourceCabClosure { get; set; }
        public string[] EndfieldSourceCabClosureDomains { get; set; }
        public bool EndfieldSourceCabClosureIncludeAutoRoots { get; set; }
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
        public bool ExportFullDecodedAnimationCurves { get; set; }
        public AnimeStudio.TextureExportMode TextureMode { get; set; }
        public Model3DProfile Profile3D { get; set; }
        public ModelSourceMode ModelSource { get; set; }
        public bool IncludeStaticMeshes { get; set; }
        public bool IncludeVfx { get; set; }
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
        public DirectoryInfo ListModelAnimationsFromLibrary { get; set; }
        public DirectoryInfo ExportAnimationGltfFromLibrary { get; set; }
        public FileInfo ExportAnimationGltfFromFiles { get; set; }
        public FileInfo MergeAnimationGltf { get; set; }
        public DirectoryInfo ValidateAnimationPreviewsFromLibrary { get; set; }
        public FileInfo GenerateAssembledPreviewGltf { get; set; }
        public string PreviewModel { get; set; }
        public string PreviewAnimation { get; set; }
        public DirectoryInfo PreviewOutput { get; set; }
        public DirectoryInfo PreviewSourceRoot { get; set; }
        public bool PreviewForceInternalHumanoidSolve { get; set; }
        public string PreviewAvatar { get; set; }
        public string AssemblyModules { get; set; }
        public FileInfo PackModelAnimations { get; set; }
        public DirectoryInfo PackModelAnimationsFromLibrary { get; set; }
        public string PackAnimations { get; set; }
        public DirectoryInfo PackOutput { get; set; }
        public int PackLimit { get; set; }
        public int PreviewValidationLimit { get; set; }
        public DirectoryInfo PreviewValidationOutput { get; set; }
        public PreviewValidationKind PreviewValidationKind { get; set; }
        public bool PreviewValidationForce { get; set; }
        public FileInfo GenerateUnityBakeRequest { get; set; }
        public DirectoryInfo GenerateUnityBakeRequestFromLibrary { get; set; }
        public DirectoryInfo BakeAnimationPreviewsFromLibrary { get; set; }
        public DirectoryInfo GenerateUnityBakeAcceleratedRequestFromLibrary { get; set; }
        public bool CheckUnityBakeAcceleratedWorker { get; set; }
        public bool SyncUnityBakeAcceleratedWorker { get; set; }
        public FileInfo RunUnityBakeAccelerated { get; set; }
        public DirectoryInfo UnityBakeAcceleratedWorkerQueue { get; set; }
        public FileInfo ApplyUnityBakeAcceleratedResult { get; set; }
        public DirectoryInfo UnityProject { get; set; }
        public FileInfo UnityEditor { get; set; }
        public string UnityBakeModelPrefab { get; set; }
        public string UnityBakeAnimationClip { get; set; }
        public string UnityBakeAnimatorController { get; set; }
        public string UnityBakeAvatarAsset { get; set; }
        public FileInfo UnityBakeOutput { get; set; }
        public int UnityBakeFps { get; set; }
        public bool UnityProbeMuscles { get; set; }
        public bool UnityBakeEnableIkGoalDriver { get; set; }
        public bool UnityBakeSampleRecoverableSkippedLayersDiagnostic { get; set; }
        public bool UnityBakeRebuildEditorCurveClip { get; set; }
        public bool UnityBakeIgnoreImportedAvatar { get; set; }
        public string UnityBakeClipFilterMode { get; set; }
        public bool UnityBakeAllowGeneratedControllerDiagnostic { get; set; }
        public bool RunUnityBake { get; set; }
        public DirectoryInfo UnityBakeWorkerQueue { get; set; }
        public FileInfo ApplyUnityBakeResult { get; set; }
        public FileInfo CompareUnityBakeResult { get; set; }
        public FileInfo CompareGltf { get; set; }
        public FileInfo CompareOutput { get; set; }
        public FileInfo ExportFbxFromGltf { get; set; }
        public bool FbxSkeletonOnly { get; set; }
        public FileInfo BakedGltfOutput { get; set; }
        public FileInfo BakedFbxOutput { get; set; }
        public FileInfo GenerateSkeletonGuide { get; set; }
        public FileInfo SkeletonGuideCatalog { get; set; }
        public DirectoryInfo RebuildLibraryIndexes { get; set; }
        public DirectoryInfo MigrateLibraryRelativePaths { get; set; }
        public DirectoryInfo BuildSqliteIndex { get; set; }
        public bool ProbeSourceInput { get; set; }
        public bool BuildSourceSqliteIndex { get; set; }
        public FileInfo VerifySourceIndex { get; set; }
        public FileInfo EnsureSourceIndexQueryIndexes { get; set; }
        public FileInfo ListSourceModelCandidates { get; set; }
        public FileInfo ListSourceModelAnimations { get; set; }
        public string[] LocateSourceCabs { get; set; }
        public string[] LocateEndfieldStrings { get; set; }
        public string[] LocateEndfieldCabs { get; set; }
        public FileInfo LocateEndfieldMissingSourceCabs { get; set; }
        public bool BuildEndfieldCabLocationIndex { get; set; }
        public FileInfo EndfieldCabLocationIndex { get; set; }
        public string[] InspectEndfieldManifestDeps { get; set; }
        public FileInfo ExportAvatarOracle { get; set; }
        public FileInfo ExportNarakaAvatarMeshPlan { get; set; }
        public FileInfo ExportAvatarMeshDataGltf { get; set; }
        public bool NarakaAvatarMeshExternalSkeletonSkinDiagnostic { get; set; }
        public bool NarakaAvatarMeshFaceRuntimeSkinDiagnostic { get; set; }
        public bool NarakaAvatarMeshRendererBonesSkinDiagnostic { get; set; }
        public DirectoryInfo RecoverImportedAvatarAssets { get; set; }
        public DirectoryInfo RecoverImportedAnimationClips { get; set; }
        public DirectoryInfo RecoverImportedAnimatorControllers { get; set; }
        public DirectoryInfo[] AnimatorControllerClipLibrary { get; set; }
        public DirectoryInfo RefreshAnimatorControllerContexts { get; set; }
        public FileInfo UnityFileInspect { get; set; }
        public int AvatarRecoveryLimit { get; set; }
        public bool AvatarRecoveryForce { get; set; }
        public bool DumpUnityBlockChunks { get; set; }
        public bool RequireFreshSourceAnimationRelations { get; set; }
        public int SourceCandidateLimit { get; set; }
        public FileInfo SourceIndex { get; set; }
        public FileInfo IndexPath { get; set; }
        public bool SkipSqliteIndex { get; set; }
        public bool SkipSqliteFileIndex { get; set; }
        public bool SkipSqliteSidecarScan { get; set; }
        public bool SkipSqliteJsonDocuments { get; set; }
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
        public readonly Option<string[]> SourceFileFilter;
        public readonly Option<long[]> PathIdFilter;
        public readonly Option<bool> IncludeAnimatorControllerClipClosure;
        public readonly Option<Regex[]> EndfieldVfsFileFilter;
        public readonly Option<int> EndfieldVfsFileLimit;
        public readonly Option<bool> EndfieldVfsKeepSameLengthSupplemental;
        public readonly Option<FileInfo> EndfieldManifestDeps;
        public readonly Option<FileInfo[]> EndfieldSourceCabClosure;
        public readonly Option<string[]> EndfieldSourceCabClosureDomains;
        public readonly Option<bool> EndfieldSourceCabClosureIncludeAutoRoots;
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
        public readonly Option<bool> ExportFullDecodedAnimationCurves;
        public readonly Option<AnimeStudio.TextureExportMode> TextureMode;
        public readonly Option<Model3DProfile> Profile3D;
        public readonly Option<ModelSourceMode> ModelSource;
        public readonly Option<bool> IncludeStaticMeshes;
        public readonly Option<bool> IncludeVfx;
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
        public readonly Option<DirectoryInfo> ListModelAnimationsFromLibrary;
        public readonly Option<DirectoryInfo> ExportAnimationGltfFromLibrary;
        public readonly Option<FileInfo> ExportAnimationGltfFromFiles;
        public readonly Option<FileInfo> MergeAnimationGltf;
        public readonly Option<DirectoryInfo> ValidateAnimationPreviewsFromLibrary;
        public readonly Option<FileInfo> GenerateAssembledPreviewGltf;
        public readonly Option<string> PreviewModel;
        public readonly Option<string> PreviewAnimation;
        public readonly Option<DirectoryInfo> PreviewOutput;
        public readonly Option<DirectoryInfo> PreviewSourceRoot;
        public readonly Option<bool> PreviewForceInternalHumanoidSolve;
        public readonly Option<string> PreviewAvatar;
        public readonly Option<string> AssemblyModules;
        public readonly Option<FileInfo> PackModelAnimations;
        public readonly Option<DirectoryInfo> PackModelAnimationsFromLibrary;
        public readonly Option<string> PackAnimations;
        public readonly Option<DirectoryInfo> PackOutput;
        public readonly Option<int> PackLimit;
        public readonly Option<int> PreviewValidationLimit;
        public readonly Option<DirectoryInfo> PreviewValidationOutput;
        public readonly Option<PreviewValidationKind> PreviewValidationKind;
        public readonly Option<bool> PreviewValidationForce;
        public readonly Option<FileInfo> GenerateUnityBakeRequest;
        public readonly Option<DirectoryInfo> GenerateUnityBakeRequestFromLibrary;
        public readonly Option<DirectoryInfo> BakeAnimationPreviewsFromLibrary;
        public readonly Option<DirectoryInfo> GenerateUnityBakeAcceleratedRequestFromLibrary;
        public readonly Option<bool> CheckUnityBakeAcceleratedWorker;
        public readonly Option<bool> SyncUnityBakeAcceleratedWorker;
        public readonly Option<FileInfo> RunUnityBakeAccelerated;
        public readonly Option<DirectoryInfo> UnityBakeAcceleratedWorkerQueue;
        public readonly Option<FileInfo> ApplyUnityBakeAcceleratedResult;
        public readonly Option<DirectoryInfo> UnityProject;
        public readonly Option<FileInfo> UnityEditor;
        public readonly Option<string> UnityBakeModelPrefab;
        public readonly Option<string> UnityBakeAnimationClip;
        public readonly Option<string> UnityBakeAnimatorController;
        public readonly Option<string> UnityBakeAvatarAsset;
        public readonly Option<FileInfo> UnityBakeOutput;
        public readonly Option<int> UnityBakeFps;
        public readonly Option<bool> UnityProbeMuscles;
        public readonly Option<bool> UnityBakeEnableIkGoalDriver;
        public readonly Option<bool> UnityBakeSampleRecoverableSkippedLayersDiagnostic;
        public readonly Option<bool> UnityBakeRebuildEditorCurveClip;
        public readonly Option<bool> UnityBakeIgnoreImportedAvatar;
        public readonly Option<string> UnityBakeClipFilterMode;
        public readonly Option<bool> UnityBakeAllowGeneratedControllerDiagnostic;
        public readonly Option<bool> RunUnityBake;
        public readonly Option<DirectoryInfo> UnityBakeWorkerQueue;
        public readonly Option<FileInfo> ApplyUnityBakeResult;
        public readonly Option<FileInfo> CompareUnityBakeResult;
        public readonly Option<FileInfo> CompareGltf;
        public readonly Option<FileInfo> CompareOutput;
        public readonly Option<FileInfo> ExportFbxFromGltf;
        public readonly Option<bool> FbxSkeletonOnly;
        public readonly Option<FileInfo> BakedGltfOutput;
        public readonly Option<FileInfo> BakedFbxOutput;
        public readonly Option<FileInfo> GenerateSkeletonGuide;
        public readonly Option<FileInfo> SkeletonGuideCatalog;
        public readonly Option<DirectoryInfo> RebuildLibraryIndexes;
        public readonly Option<DirectoryInfo> MigrateLibraryRelativePaths;
        public readonly Option<DirectoryInfo> BuildSqliteIndex;
        public readonly Option<bool> ProbeSourceInput;
        public readonly Option<bool> BuildSourceSqliteIndex;
        public readonly Option<FileInfo> VerifySourceIndex;
        public readonly Option<FileInfo> EnsureSourceIndexQueryIndexes;
        public readonly Option<FileInfo> ListSourceModelCandidates;
        public readonly Option<FileInfo> ListSourceModelAnimations;
        public readonly Option<string[]> LocateSourceCabs;
        public readonly Option<string[]> LocateEndfieldStrings;
        public readonly Option<string[]> LocateEndfieldCabs;
        public readonly Option<FileInfo> LocateEndfieldMissingSourceCabs;
        public readonly Option<bool> BuildEndfieldCabLocationIndex;
        public readonly Option<FileInfo> EndfieldCabLocationIndex;
        public readonly Option<string[]> InspectEndfieldManifestDeps;
        public readonly Option<FileInfo> ExportAvatarOracle;
        public readonly Option<FileInfo> ExportNarakaAvatarMeshPlan;
        public readonly Option<FileInfo> ExportAvatarMeshDataGltf;
        public readonly Option<bool> NarakaAvatarMeshExternalSkeletonSkinDiagnostic;
        public readonly Option<bool> NarakaAvatarMeshFaceRuntimeSkinDiagnostic;
        public readonly Option<bool> NarakaAvatarMeshRendererBonesSkinDiagnostic;
        public readonly Option<DirectoryInfo> RecoverImportedAvatarAssets;
        public readonly Option<DirectoryInfo> RecoverImportedAnimationClips;
        public readonly Option<DirectoryInfo> RecoverImportedAnimatorControllers;
        public readonly Option<DirectoryInfo[]> AnimatorControllerClipLibrary;
        public readonly Option<DirectoryInfo> RefreshAnimatorControllerContexts;
        public readonly Option<FileInfo> UnityFileInspect;
        public readonly Option<int> AvatarRecoveryLimit;
        public readonly Option<bool> AvatarRecoveryForce;
        public readonly Option<bool> DumpUnityBlockChunks;
        public readonly Option<bool> RequireFreshSourceAnimationRelations;
        public readonly Option<int> SourceCandidateLimit;
        public readonly Option<FileInfo> SourceIndex;
        public readonly Option<FileInfo> IndexPath;
        public readonly Option<bool> SkipSqliteIndex;
        public readonly Option<bool> SkipSqliteFileIndex;
        public readonly Option<bool> SkipSqliteSidecarScan;
        public readonly Option<bool> SkipSqliteJsonDocuments;
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
            SourceFileFilter = new Option<string[]>("--source_files", "Explicit source file paths, relative to the input root, to load for targeted refresh/debug exports. Default Library exports should leave this unset.") { AllowMultipleArgumentsPerToken = true };
            PathIdFilter = new Option<long[]>("--path_ids", "Explicit Unity object PathID values to export for targeted refresh/debug exports. Default Library exports should leave this unset.") { AllowMultipleArgumentsPerToken = true };
            IncludeAnimatorControllerClipClosure = new Option<bool>("--include_animator_controller_clip_closure", "Diagnostic only: when --path_ids selects AnimationClip assets, also export every AnimationClip referenced by the same AnimatorController(s). This repairs controller recovery clip closure but may export many clips.");
            EndfieldVfsFileFilter = new Option<Regex[]>("--endfield_vfs_files", ParseRegexOption, false, "Diagnostic only: regex filter for UnityFS files inside Arknights Endfield .blc VFS groups. Leave unset for full Library source indexing.") { AllowMultipleArgumentsPerToken = true };
            EndfieldVfsFileLimit = new Option<int>("--endfield_vfs_file_limit", "Diagnostic only: maximum number of UnityFS files to expose from each Arknights Endfield .blc VFS group. 0 means no limit.");
            EndfieldVfsKeepSameLengthSupplemental = new Option<bool>("--endfield_vfs_keep_same_length_supplemental", "Diagnostic/full-closure mode: keep same-named same-length StreamingAssets inner UnityFS files in addition to Persistent/VFS. Slower and not the default production source-index path.");
            EndfieldManifestDeps = new Option<FileInfo>("--endfield_manifest_deps", "Diagnostic: Endfield manifest file used with --endfield_vfs_files to expand selected root bundle(s) to their manifest dependency closure for source-index smoke builds.").LegalFilePathsOnly();
            EndfieldSourceCabClosure = new Option<FileInfo[]>("--endfield_source_cab_closure", "Diagnostic: include locatedUnityBundleFiles from one or more endfield_missing_source_cab_closure.json reports in the Endfield VFS inner-file filter for source-index smoke builds. Combine with --endfield_vfs_files for root bundle(s).") { AllowMultipleArgumentsPerToken = true }.LegalFilePathsOnly();
            EndfieldSourceCabClosureDomains = new Option<string[]>("--endfield_source_cab_closure_domains", "Diagnostic: restrict --endfield_source_cab_closure to dependency domains: model, material, animationClip. Leave unset to include every located bundle.") { AllowMultipleArgumentsPerToken = true };
            EndfieldSourceCabClosureIncludeAutoRoots = new Option<bool>("--endfield_source_cab_closure_include_auto_roots", "Diagnostic: when building an Endfield source index with CAB closure reports, keep the default VFS root/context selection and add closure bundles on top.");
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
            ExportFullDecodedAnimationCurves = new Option<bool>("--export_full_decoded_animation_curves", "Export full decoded AnimationClip keyframes into animation sidecar JSON. Disabled by default for full Library performance; enable for targeted direct glTF preview and internal solver validation.");
            TextureMode = new Option<AnimeStudio.TextureExportMode>("--texture_mode", "Specify model texture export mode: Raw, Png, or Reference.");
            Profile3D = new Option<Model3DProfile>("--profile_3d", "Specify 3D export profile: Core filters non-core models; All keeps all model candidates except basic hygiene filters.");
            ModelSource = new Option<ModelSourceMode>("--model_source", "Specify Library model source mode: PrefabPrimary exports prefab/Animator models by default and indexes raw fbx parts; PrefabAndParts exports both; RawPartsOnly exports only raw fbx/source parts.");
            IncludeStaticMeshes = new Option<bool>("--include_static_meshes", "Include standalone/static Mesh assets as browsable Library models. Also enables StaticRendererModel rows for --list_source_model_candidates. Disabled by default so Library focuses on prefab/Animator models.");
            IncludeVfx = new Option<bool>("--include_vfx", "Include VFX metadata, mesh-VFX classification, and VFX preview cache in Library export. Disabled by default.");
            IncludeShaders = new Option<bool>("--include_shaders", "Include shaders in Library mode as experimental safe raw archives.");
            MaxExportTasks = new Option<int>("--max_export_tasks", "Maximum parallel export tasks for independent texture/audio/material assets and StaticMeshPrimary glTF export. Core prefab/Animator model export remains serial. Use 1 for fully serial export.");
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
            ListModelAnimationsFromLibrary = new Option<DirectoryInfo>("--list_model_animations_from_library", "List deterministic relation_source=explicit animation candidates for one selected Library model. Use --preview_model and optional --preview_animation.").LegalFilePathsOnly();
            ExportAnimationGltfFromLibrary = new Option<DirectoryInfo>("--export_animation_gltf_from_library", "Export a skinless standalone glTF animation asset from a deterministic library_index.db model-animation candidate.").LegalFilePathsOnly();
            ExportAnimationGltfFromFiles = new Option<FileInfo>("--export_animation_gltf_from_files", "Export a skinless standalone glTF animation asset from a model glTF and an explicit animation sidecar passed with --preview_animation. This does not create a model-animation recommendation.").LegalFilePathsOnly();
            MergeAnimationGltf = new Option<FileInfo>("--merge_animation_gltf", "Merge a clean model glTF with a previously exported skinless standalone animation glTF. Pass the model glTF here and the animation glTF with --preview_animation.").LegalFilePathsOnly();
            ValidateAnimationPreviewsFromLibrary = new Option<DirectoryInfo>("--validate_animation_previews_from_library", "Generate and validate a limited batch of deterministic SQLite model-animation preview glTFs, then write animation_preview_cache.").LegalFilePathsOnly();
            GenerateAssembledPreviewGltf = new Option<FileInfo>("--generate_assembled_preview_gltf", "Generate a playable preview glTF and non-destructively add compatible modular character parts such as face, hair, or accessories when their Unity joints can be remapped.").LegalFilePathsOnly();
            PreviewModel = new Option<string>("--preview_model", "Model name, output path, or regex used with preview commands.");
            PreviewAnimation = new Option<string>("--preview_animation", "Animation name, output path, or regex used with preview commands.");
            PreviewOutput = new Option<DirectoryInfo>("--preview_output", "Output folder for preview commands. Defaults to Previews/<model>__<animation> next to the index or Library root.");
            PreviewSourceRoot = new Option<DirectoryInfo>("--preview_source_root", "Full Unity game/source root used by preview and animation-pack commands to resolve dependencies instead of reusing possibly incomplete indexed sample paths.").LegalFilePathsOnly();
            PreviewForceInternalHumanoidSolve = new Option<bool>("--preview_force_internal_humanoid_solve", "Force file-based preview/standalone animation export to use AnimeStudio's internal Humanoid/Muscle solver. Diagnostic validation only; it does not create model-animation recommendations.");
            PreviewAvatar = new Option<string>("--preview_avatar", "Explicit Avatar selector for diagnostic manual preview. With --source_index it can inject Avatar.m_TOS for hash-only TRS path mapping, or Avatar oracle for --preview_force_internal_humanoid_solve; it does not create default model-animation relations.");
            AssemblyModules = new Option<string>("--assembly_modules", "Comma-separated module roles or selectors for --generate_assembled_preview_gltf. Defaults to Face,Hair,Accessory.");
            PackModelAnimations = new Option<FileInfo>("--pack_model_animations", "Generate a reusable animation asset pack from model_animations.json by exporting one model with multiple selected animations.").LegalFilePathsOnly();
            PackModelAnimationsFromLibrary = new Option<DirectoryInfo>("--pack_model_animations_from_library", "Generate a reusable animation asset pack directly from library_index.db in a Library root.").LegalFilePathsOnly();
            PackAnimations = new Option<string>("--pack_animations", "Comma-separated animation names or regexes used with animation-pack commands. If omitted, top candidates are used.");
            PackOutput = new Option<DirectoryInfo>("--pack_output", "Output folder for animation-pack commands.");
            PackLimit = new Option<int>("--pack_limit", "Maximum number of animations to pack when --pack_animations is omitted.");
            PreviewValidationLimit = new Option<int>("--preview_validation_limit", "Maximum number of SQLite deterministic preview candidates to validate in one batch.");
            PreviewValidationOutput = new Option<DirectoryInfo>("--preview_validation_output", "Output folder for --validate_animation_previews_from_library. Defaults to AnimationPreviewValidation under the Library root.");
            PreviewValidationKind = new Option<PreviewValidationKind>("--preview_validation_kind", "Filter preview validation candidates: All, Direct, or InternalHumanoid. This only filters existing relation_source=explicit candidates.");
            PreviewValidationForce = new Option<bool>("--preview_validation_force", "Re-run matching deterministic preview candidates even when animation_preview_cache already has a result. Use for visual spot checks after solver or decoder changes.");
            GenerateUnityBakeRequest = new Option<FileInfo>("--generate_unity_bake_request", "Deprecated diagnostic only: generate a Unity Editor bake request from model_animations.json for legacy comparison.").LegalFilePathsOnly();
            GenerateUnityBakeRequestFromLibrary = new Option<DirectoryInfo>("--generate_unity_bake_request_from_library", "Deprecated diagnostic only: generate one Unity bake request from an explicit model-animation candidate in library_index.db.").LegalFilePathsOnly();
            BakeAnimationPreviewsFromLibrary = new Option<DirectoryInfo>("--bake_animation_previews_from_library", "Deprecated diagnostic only: batch legacy Unity bake previews for comparison, not production animation support.").LegalFilePathsOnly();
            GenerateUnityBakeAcceleratedRequestFromLibrary = new Option<DirectoryInfo>("--generate_unity_bake_accelerated_request_from_library", "Experimental: generate one UnityBakeAccelerated request from an explicit Library Humanoid/Muscle candidate and decoded animation sidecar.").LegalFilePathsOnly();
            CheckUnityBakeAcceleratedWorker = new Option<bool>("--check_unity_bake_accelerated_worker", "Experimental: check a preconfigured Unity 6 AnimeStudioUnityBakeWorker project and UnityBakeAccelerated helper markers.");
            SyncUnityBakeAcceleratedWorker = new Option<bool>("--sync_unity_bake_accelerated_worker", "Experimental: copy the repo AnimeStudio.UnityBake helper into the UnityBakeAccelerated worker project, then run the worker check.");
            RunUnityBakeAccelerated = new Option<FileInfo>("--run_unity_bake_accelerated", "Experimental: run a standalone UnityBakeAccelerated request JSON. This does not read or write Library indexes.").LegalFilePathsOnly();
            UnityBakeAcceleratedWorkerQueue = new Option<DirectoryInfo>("--unity_bake_accelerated_worker_queue", "Experimental: queue directory for a persistent UnityBakeAccelerated worker. Only used with --run_unity_bake_accelerated.").LegalFilePathsOnly();
            ApplyUnityBakeAcceleratedResult = new Option<FileInfo>("--apply_unity_bake_accelerated_result", "Experimental: apply a UnityBakeAccelerated request/result to the source glTF and write ordinary glTF TRS animation channels.").LegalFilePathsOnly();
            UnityProject = new Option<DirectoryInfo>("--unity_project", "Unity project that contains the AnimeStudio.UnityBake helper scripts. Required when --run_unity_bake is used.").LegalFilePathsOnly();
            UnityEditor = new Option<FileInfo>("--unity_editor", "Unity.exe path used with --run_unity_bake. If omitted, only the request JSON is written.").LegalFilePathsOnly();
            UnityBakeModelPrefab = new Option<string>("--unity_model_prefab", "Unity project asset path for the prepared model prefab, for example Assets/AnimeStudioBake/Input/Bill.prefab.");
            UnityBakeAnimationClip = new Option<string>("--unity_animation_clip", "Unity project asset path for the prepared AnimationClip, for example Assets/AnimeStudioBake/Input/NORMALMOVE_STAND_01.anim.");
            UnityBakeAnimatorController = new Option<string>("--unity_animator_controller", "Unity project asset path for the original RuntimeAnimatorController asset. Diagnostic/prototype path for sampling the real AnimatorController instead of a generated single-state controller.");
            UnityBakeAvatarAsset = new Option<string>("--unity_avatar_asset", "Unity project asset path for the original imported UnityEngine.Avatar asset. Explicit Genshin oracle path; omitted by default so normal Unity projects keep their existing prefab/HumanDescription flow.");
            UnityBakeOutput = new Option<FileInfo>("--unity_bake_output", "Output JSON path for Unity baked TRS animation. Defaults to unity_bake_result.json next to the request.");
            UnityBakeFps = new Option<int>("--unity_bake_fps", "Frame rate used by the Unity bake helper.");
            UnityProbeMuscles = new Option<bool>("--unity_probe_muscles", "Diagnostic only: ask the Unity bake helper to sample every Humanoid muscle at -1/+1 so AnimeStudio can compare Unity's real Avatar solve against the internal solver.");
            UnityBakeEnableIkGoalDriver = new Option<bool>("--unity_bake_enable_ik_goal_driver", "Diagnostic only: enable editor-curve Hand/Foot IK goal driver during Unity bake. Results remain needs_review until visual validation and goal space/weight semantics are proven.");
            UnityBakeSampleRecoverableSkippedLayersDiagnostic = new Option<bool>("--unity_bake_sample_recoverable_skipped_layers_diagnostic", "Diagnostic only: sample recovered AnimatorController layers that would normally be skipped but have a resolvable motion and mask metadata. Results remain needs_review until runtime layer semantics are proven.");
            UnityBakeRebuildEditorCurveClip = new Option<bool>("--unity_bake_rebuild_editor_curve_clip", "Diagnostic only: rebuild a Humanoid AnimationClip from Unity editor curves before sampling, useful when recovered .anim has editor curves but an empty runtime m_MuscleClip.");
            UnityBakeIgnoreImportedAvatar = new Option<bool>("--unity_bake_ignore_imported_avatar", "Diagnostic only: ignore a supplied imported Avatar asset and rebuild Avatar from request HumanDescription/oracle data. Results must stay needs_review.");
            UnityBakeClipFilterMode = new Option<string>("--unity_bake_clip_filter_mode", "Diagnostic only: override Unity bake clip filter mode. Use auto/default for existing safety gates, full/none to sample the full AnimationClip, or transform_only to keep only deterministic Transform/controller layer curves.");
            UnityBakeAllowGeneratedControllerDiagnostic = new Option<bool>("--unity_bake_allow_generated_controller_diagnostic", "Diagnostic only: allow Unity bake fallback to generate a temporary AnimatorController when no exact ImportedAnimatorController/original RuntimeAnimatorController is available. Default blocks this because it can create semantically wrong poses.");
            RunUnityBake = new Option<bool>("--run_unity_bake", "Run Unity Editor batchmode immediately after writing the bake request.");
            UnityBakeWorkerQueue = new Option<DirectoryInfo>("--unity_bake_worker_queue", "Queue directory for a persistent Unity bake worker. When set with --run_unity_bake, CLI reuses a warm Unity worker instead of launching Unity for each request.").LegalFilePathsOnly();
            ApplyUnityBakeResult = new Option<FileInfo>("--apply_unity_bake_result", "Apply unity_bake_result.json or unity_bake_request.json to the source glTF and write a baked playable preview glTF.").LegalFilePathsOnly();
            CompareUnityBakeResult = new Option<FileInfo>("--compare_unity_bake_result", "Diagnostic: compare a Unity bake request/result against an internally solved glTF animation and write per-bone TRS error report.").LegalFilePathsOnly();
            CompareGltf = new Option<FileInfo>("--compare_gltf", "Internally solved glTF used by --compare_unity_bake_result.").LegalFilePathsOnly();
            CompareOutput = new Option<FileInfo>("--compare_output", "Output JSON report for --compare_unity_bake_result. Defaults to unity_bake_compare_report.json next to the compared glTF.").LegalFilePathsOnly();
            ExportFbxFromGltf = new Option<FileInfo>("--export_fbx_from_gltf", "Convert an existing baked glTF/GLB preview to FBX through Blender for Unity/DCC comparison. Requires --baked_fbx_output.");
            FbxSkeletonOnly = new Option<bool>("--fbx_skeleton_only", "When converting glTF/GLB to FBX through Blender, export only armature/skeleton and animation takes, without mesh skin.");
            BakedGltfOutput = new Option<FileInfo>("--baked_gltf_output", "Output glTF path for --apply_unity_bake_result. Defaults to BakedPreview/<model>__<clip>.gltf next to the bake request/result.");
            BakedFbxOutput = new Option<FileInfo>("--baked_fbx_output", "Optional compatibility FBX output path. AnimeStudio first writes the baked glTF, then asks Blender to export FBX for DCC comparison.");
            GenerateSkeletonGuide = new Option<FileInfo>("--generate_skeleton_guide", "Generate a non-destructive Blender CoreHumanoid skeleton guide from an exported FBX/glTF/GLB. Uses asset_catalog.jsonl Unity Avatar relations when available.").LegalFilePathsOnly();
            SkeletonGuideCatalog = new Option<FileInfo>("--skeleton_guide_catalog", "Optional asset_catalog.jsonl used by --generate_skeleton_guide. If omitted, AnimeStudio walks up from the FBX path.").LegalFilePathsOnly();
            RebuildLibraryIndexes = new Option<DirectoryInfo>("--rebuild_library_indexes", "Rebuild summary, validation, skeleton, model-animation, and compact indexes from a previous Library export. If unity_source_index.db exists beside the Library, deterministic Unity Animator/Animation/Controller relations are restored without full re-export.").LegalFilePathsOnly();
            MigrateLibraryRelativePaths = new Option<DirectoryInfo>("--migrate_library_relative_paths", "Convert an existing exported Library index to portable root-relative internal asset paths and rebuild library_index.db. Unity source paths are preserved.").LegalFilePathsOnly();
            BuildSqliteIndex = new Option<DirectoryInfo>("--build_sqlite_index", "Rebuild the reusable SQLite index from a previous Library or AudioLibrary export. For Library roots, pass --source_index to use an external fresh source index; otherwise unity_source_index.db beside the Library is used to restore deterministic model-animation candidates.").LegalFilePathsOnly();
            ProbeSourceInput = new Option<bool>("--probe_source_input", "Diagnostic: quickly inspect input_path file headers and write source_input_probe.json. Useful before Naraka source-index builds to distinguish loadable StreamingAssets bundles from external .pak/AES unpacking tasks.");
            BuildSourceSqliteIndex = new Option<bool>("--build_source_sqlite_index", "Build a reusable SQLite source index directly from a full Unity game/source folder. Requires input_path, output_path, and --game.");
            VerifySourceIndex = new Option<FileInfo>("--verify_source_index", "Inspect an existing unity_source_index.db and write an animation relation health report without rebuilding it.").LegalFilePathsOnly();
            EnsureSourceIndexQueryIndexes = new Option<FileInfo>("--ensure_source_index_query_indexes", "Create or rebuild query indexes on an existing unity_source_index.db. Useful for old large indexes before candidate or animation relation scans.").LegalFilePathsOnly();
            ListSourceModelCandidates = new Option<FileInfo>("--list_source_model_candidates", "List model-first smoke candidates from unity_source_index.db using deterministic Animator/Renderer/Mesh/Material relations. Does not export assets or create animation bindings.").LegalFilePathsOnly();
            ListSourceModelAnimations = new Option<FileInfo>("--list_source_model_animations", "List deterministic source-index animation references for one selected model. Requires --preview_model; does not prove model quality or animation playability.").LegalFilePathsOnly();
            LocateSourceCabs = new Option<string[]>("--locate_source_cabs", "Diagnostic: locate Unity CAB serialized files inside a normal Unity source folder by reading bundle directory metadata only. Pass one or more CAB names; writes source_cab_locations.json.") { AllowMultipleArgumentsPerToken = true };
            LocateEndfieldStrings = new Option<string[]>("--locate_endfield_strings", "Diagnostic: locate ASCII strings inside Arknights Endfield VFS chunk files and map hits back to inner VFS files. Pass one or more strings; writes endfield_string_locations.json.") { AllowMultipleArgumentsPerToken = true };
            LocateEndfieldCabs = new Option<string[]>("--locate_endfield_cabs", "Diagnostic: locate CAB files inside Arknights Endfield VFS inner UnityFS bundles. Pass one or more CAB names; writes endfield_cab_locations.json to output_path.") { AllowMultipleArgumentsPerToken = true };
            LocateEndfieldMissingSourceCabs = new Option<FileInfo>("--locate_endfield_missing_source_cabs", "Diagnostic: read missing Mesh/Material/Texture/AnimationClip target CABs from a unity_source_index.db, locate them in Arknights Endfield VFS inner bundles, and write a source-index closure report. Use --source_candidate_limit to cap CAB targets.").LegalFilePathsOnly();
            BuildEndfieldCabLocationIndex = new Option<bool>("--build_endfield_cab_location_index", "Diagnostic: scan Arknights Endfield VFS once and write endfield_cab_location_index.json for fast missing Mesh/Material/Texture CAB closure lookups.");
            EndfieldCabLocationIndex = new Option<FileInfo>("--endfield_cab_location_index", "Existing endfield_cab_location_index.json used by --locate_endfield_missing_source_cabs to avoid rescanning all VFS bundles.").LegalFilePathsOnly();
            InspectEndfieldManifestDeps = new Option<string[]>("--inspect_endfield_manifest_deps", "Diagnostic: parse an Arknights Endfield bundle manifest and list bundle dependency paths for one or more main/*.ab or initial/*.ab bundle paths. input_path is the manifest file; output_path receives endfield_manifest_dependencies.json.") { AllowMultipleArgumentsPerToken = true };
            ExportAvatarOracle = new Option<FileInfo>("--export_avatar_oracle", "Export one AvatarConstant oracle JSON from unity_source_index.db. Use --preview_model to select Avatar name/pathId/source and --preview_output for the output folder.").LegalFilePathsOnly();
            ExportNarakaAvatarMeshPlan = new Option<FileInfo>("--export_naraka_avatar_mesh_plan", "Diagnostic: build a deterministic Naraka ActorBodyVisualCell custom mesh export plan from unity_source_index.db. Use --preview_model for GameObject name or PathID, --preview_source_root for the full Unity source root, and --preview_output for plan files.").LegalFilePathsOnly();
            ExportAvatarMeshDataGltf = new Option<FileInfo>("--export_avatar_mesh_data_gltf", "Diagnostic: convert one AvatarMeshDataAsset TypeTree JSON file, or a folder of JSON files, into static glTF. Use --preview_output for the output folder. Optional --source_index records renderer material references for Naraka ActorBodyVisualCell folders, but does not bake materials.").LegalFilePathsOnly();
            NarakaAvatarMeshExternalSkeletonSkinDiagnostic = new Option<bool>("--naraka_avatar_mesh_external_skeleton_skin_diagnostic", "Diagnostic only: for Naraka ActorBodyVisualCell folder exports, write glTF JOINTS_0/WEIGHTS_0 from AvatarBoneWeights using the best external transformNodes candidate. Default is off because the mapping still needs visual and bind-pose validation.");
            NarakaAvatarMeshFaceRuntimeSkinDiagnostic = new Option<bool>("--naraka_avatar_mesh_face_runtime_skin_diagnostic", "Diagnostic only: for Naraka face ActorBodyVisualCell folder exports, write glTF JOINTS_0/WEIGHTS_0 from AvatarBoneWeights using AvatarFaceRuntime -> AvatarFaceData.m_AvatarBones. Default is off because bind-pose space and visual correctness still need validation.");
            NarakaAvatarMeshRendererBonesSkinDiagnostic = new Option<bool>("--naraka_avatar_mesh_renderer_bones_skin_diagnostic", "Diagnostic only: for Naraka ActorBodyVisualCell folder exports, write per-part glTF skins from SkinnedMeshRenderer.m_Bones and AvatarMeshDataAsset.m_AnimSkinData/m_BindPoses. Default is off because bind-pose space and visual correctness still need validation.");
            RecoverImportedAvatarAssets = new Option<DirectoryInfo>("--recover_imported_avatar_assets", "Recover missing original UnityEngine.Avatar assets from a Library root into the Unity bake project ImportedAvatar folder. Uses library_index.db avatar source/pathId, not guessed skeleton data.").LegalFilePathsOnly();
            RecoverImportedAnimationClips = new Option<DirectoryInfo>("--recover_imported_animation_clips", "Recover deterministic Humanoid/Muscle AnimationClip .anim assets from a Library root into the Unity bake project ImportedAnimationClip folder. Uses explicit SQLite candidates and AnimatorController baseLayerClip context; no name or bone-count guessing.").LegalFilePathsOnly();
            RecoverImportedAnimatorControllers = new Option<DirectoryInfo>("--recover_imported_animator_controllers", "Experimental: rebuild default-state RuntimeAnimatorController assets into the Unity bake project ImportedAnimatorController folder from unity_file_inspect.json and explicit Library candidates. This is diagnostic until full state/transition semantics are recovered.").LegalFilePathsOnly();
            AnimatorControllerClipLibrary = new Option<DirectoryInfo[]>("--animator_controller_clip_library", "Diagnostic only: extra Library roots that contain AnimationClip closure assets/settings used by --recover_imported_animator_controllers. Does not create model-animation relations.") { AllowMultipleArgumentsPerToken = true }.LegalFilePathsOnly();
            RefreshAnimatorControllerContexts = new Option<DirectoryInfo>("--refresh_animator_controller_contexts", "Refresh library_index.db candidates that need AnimatorController context by importing baseLayerClip evidence from unity_file_inspect.json. Uses explicit Animator.controller relations from unity_source_index.db; no name or bone-count guessing.").LegalFilePathsOnly();
            UnityFileInspect = new Option<FileInfo>("--unity_file_inspect", "unity_file_inspect.json produced by --inspect_unity_files. Used by --refresh_animator_controller_contexts to recover AnimatorController state/layer baseLayerClip context.").LegalFilePathsOnly();
            AvatarRecoveryLimit = new Option<int>("--avatar_recovery_limit", "Maximum number of unique missing Avatar assets to recover. 0 means all selected missing avatars.");
            AvatarRecoveryForce = new Option<bool>("--avatar_recovery_force", "Rewrite imported Avatar .asset files even when they already exist.");
            DumpUnityBlockChunks = new Option<bool>("--dump_unity_block_chunks", "Diagnostic only: decrypt a block container such as .blk and write its inner Unity/MHY chunks for Unity Editor load probes.");
            RequireFreshSourceAnimationRelations = new Option<bool>("--require_fresh_source_animation_relations", "Fail source-index verification or SQLite Library rebuild when AnimatorOverrideController overrideSet/clipPair markers are missing. Use this for production deterministic animation builds.");
            SourceCandidateLimit = new Option<int>("--source_candidate_limit", "Maximum source model candidates to list for --list_source_model_candidates. Default 200.");
            SourceIndex = new Option<FileInfo>("--source_index", "SQLite Unity source index used by Library export or --build_sqlite_index for dependency resolution. Prefer unity_source_index.db over legacy CAB maps for full exports.").LegalFilePathsOnly();
            IndexPath = new Option<FileInfo>("--index_path", "Output SQLite database path for --build_sqlite_index. Defaults to library_index.db in the export root.");
            SkipSqliteIndex = new Option<bool>("--skip_sqlite_index", "Skip building library_index.db after a Library export. Use only for temporary preview/export jobs that do not need a reusable SQLite library index.");
            SkipSqliteFileIndex = new Option<bool>("--skip_sqlite_file_index", "When rebuilding library_index.db, skip the large files table. Use this to quickly refresh model-animation candidates, source-index health, and preview validation stats on very large libraries.");
            SkipSqliteSidecarScan = new Option<bool>("--skip_sqlite_sidecar_scan", "When rebuilding library_index.db, skip the animation sidecar capability scan in sqlite_index_summary.json. Candidate relations are still imported.");
            SkipSqliteJsonDocuments = new Option<bool>("--skip_sqlite_json_documents", "When rebuilding library_index.db, skip copying large JSON summary documents into the json_documents table. Structured index tables are still imported.");
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
            EndfieldVfsFileFilter.AddValidator(FilterValidator);
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
            MaxExportTasks.SetDefaultValue(Math.Max(1, Math.Min(8, Environment.ProcessorCount / 2)));
            BatchFiles.SetDefaultValue(32);
            ModelGcInterval.SetDefaultValue(0);
            ProfileLog.SetDefaultValue("export_profile.jsonl");
            EndfieldVfsFileLimit.SetDefaultValue(0);
            ConvertTextureFormat.SetDefaultValue(AnimeStudio.ImageFormat.Png);
            UpdateGltfTextureRefs.SetDefaultValue(true);
            PackLimit.SetDefaultValue(5);
            PreviewValidationLimit.SetDefaultValue(10);
            PreviewValidationKind.SetDefaultValue(AnimeStudio.CLI.PreviewValidationKind.All);
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
            SourceFileFilter = bindingContext.ParseResult.GetValueForOption(SourceFileFilter),
            PathIdFilter = bindingContext.ParseResult.GetValueForOption(PathIdFilter),
            IncludeAnimatorControllerClipClosure = bindingContext.ParseResult.GetValueForOption(IncludeAnimatorControllerClipClosure),
            EndfieldVfsFileFilter = bindingContext.ParseResult.GetValueForOption(EndfieldVfsFileFilter),
            EndfieldVfsFileLimit = bindingContext.ParseResult.GetValueForOption(EndfieldVfsFileLimit),
            EndfieldVfsKeepSameLengthSupplemental = bindingContext.ParseResult.GetValueForOption(EndfieldVfsKeepSameLengthSupplemental),
            EndfieldManifestDeps = bindingContext.ParseResult.GetValueForOption(EndfieldManifestDeps),
            EndfieldSourceCabClosure = bindingContext.ParseResult.GetValueForOption(EndfieldSourceCabClosure),
            EndfieldSourceCabClosureDomains = bindingContext.ParseResult.GetValueForOption(EndfieldSourceCabClosureDomains),
            EndfieldSourceCabClosureIncludeAutoRoots = bindingContext.ParseResult.GetValueForOption(EndfieldSourceCabClosureIncludeAutoRoots),
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
                ExportFullDecodedAnimationCurves = bindingContext.ParseResult.GetValueForOption(ExportFullDecodedAnimationCurves),
                TextureMode = bindingContext.ParseResult.GetValueForOption(TextureMode),
                Profile3D = bindingContext.ParseResult.GetValueForOption(Profile3D),
                ModelSource = bindingContext.ParseResult.GetValueForOption(ModelSource),
                IncludeStaticMeshes = bindingContext.ParseResult.GetValueForOption(IncludeStaticMeshes),
                IncludeVfx = bindingContext.ParseResult.GetValueForOption(IncludeVfx),
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
                ListModelAnimationsFromLibrary = bindingContext.ParseResult.GetValueForOption(ListModelAnimationsFromLibrary),
                ExportAnimationGltfFromLibrary = bindingContext.ParseResult.GetValueForOption(ExportAnimationGltfFromLibrary),
                ExportAnimationGltfFromFiles = bindingContext.ParseResult.GetValueForOption(ExportAnimationGltfFromFiles),
                MergeAnimationGltf = bindingContext.ParseResult.GetValueForOption(MergeAnimationGltf),
                ValidateAnimationPreviewsFromLibrary = bindingContext.ParseResult.GetValueForOption(ValidateAnimationPreviewsFromLibrary),
                GenerateAssembledPreviewGltf = bindingContext.ParseResult.GetValueForOption(GenerateAssembledPreviewGltf),
                PreviewModel = bindingContext.ParseResult.GetValueForOption(PreviewModel),
                PreviewAnimation = bindingContext.ParseResult.GetValueForOption(PreviewAnimation),
                PreviewOutput = bindingContext.ParseResult.GetValueForOption(PreviewOutput),
                PreviewSourceRoot = bindingContext.ParseResult.GetValueForOption(PreviewSourceRoot),
                PreviewForceInternalHumanoidSolve = bindingContext.ParseResult.GetValueForOption(PreviewForceInternalHumanoidSolve),
                PreviewAvatar = bindingContext.ParseResult.GetValueForOption(PreviewAvatar),
                AssemblyModules = bindingContext.ParseResult.GetValueForOption(AssemblyModules),
                PackModelAnimations = bindingContext.ParseResult.GetValueForOption(PackModelAnimations),
                PackModelAnimationsFromLibrary = bindingContext.ParseResult.GetValueForOption(PackModelAnimationsFromLibrary),
                PackAnimations = bindingContext.ParseResult.GetValueForOption(PackAnimations),
                PackOutput = bindingContext.ParseResult.GetValueForOption(PackOutput),
                PackLimit = bindingContext.ParseResult.GetValueForOption(PackLimit),
                PreviewValidationLimit = bindingContext.ParseResult.GetValueForOption(PreviewValidationLimit),
                PreviewValidationOutput = bindingContext.ParseResult.GetValueForOption(PreviewValidationOutput),
                PreviewValidationKind = bindingContext.ParseResult.GetValueForOption(PreviewValidationKind),
                PreviewValidationForce = bindingContext.ParseResult.GetValueForOption(PreviewValidationForce),
                GenerateUnityBakeRequest = bindingContext.ParseResult.GetValueForOption(GenerateUnityBakeRequest),
                GenerateUnityBakeRequestFromLibrary = bindingContext.ParseResult.GetValueForOption(GenerateUnityBakeRequestFromLibrary),
                BakeAnimationPreviewsFromLibrary = bindingContext.ParseResult.GetValueForOption(BakeAnimationPreviewsFromLibrary),
                GenerateUnityBakeAcceleratedRequestFromLibrary = bindingContext.ParseResult.GetValueForOption(GenerateUnityBakeAcceleratedRequestFromLibrary),
                CheckUnityBakeAcceleratedWorker = bindingContext.ParseResult.GetValueForOption(CheckUnityBakeAcceleratedWorker),
                SyncUnityBakeAcceleratedWorker = bindingContext.ParseResult.GetValueForOption(SyncUnityBakeAcceleratedWorker),
                RunUnityBakeAccelerated = bindingContext.ParseResult.GetValueForOption(RunUnityBakeAccelerated),
                UnityBakeAcceleratedWorkerQueue = bindingContext.ParseResult.GetValueForOption(UnityBakeAcceleratedWorkerQueue),
                ApplyUnityBakeAcceleratedResult = bindingContext.ParseResult.GetValueForOption(ApplyUnityBakeAcceleratedResult),
                UnityProject = bindingContext.ParseResult.GetValueForOption(UnityProject),
                UnityEditor = bindingContext.ParseResult.GetValueForOption(UnityEditor),
                UnityBakeModelPrefab = bindingContext.ParseResult.GetValueForOption(UnityBakeModelPrefab),
                UnityBakeAnimationClip = bindingContext.ParseResult.GetValueForOption(UnityBakeAnimationClip),
                UnityBakeAnimatorController = bindingContext.ParseResult.GetValueForOption(UnityBakeAnimatorController),
                UnityBakeAvatarAsset = bindingContext.ParseResult.GetValueForOption(UnityBakeAvatarAsset),
                UnityBakeOutput = bindingContext.ParseResult.GetValueForOption(UnityBakeOutput),
                UnityBakeFps = bindingContext.ParseResult.GetValueForOption(UnityBakeFps),
                UnityProbeMuscles = bindingContext.ParseResult.GetValueForOption(UnityProbeMuscles),
                UnityBakeEnableIkGoalDriver = bindingContext.ParseResult.GetValueForOption(UnityBakeEnableIkGoalDriver),
                UnityBakeSampleRecoverableSkippedLayersDiagnostic = bindingContext.ParseResult.GetValueForOption(UnityBakeSampleRecoverableSkippedLayersDiagnostic),
                UnityBakeRebuildEditorCurveClip = bindingContext.ParseResult.GetValueForOption(UnityBakeRebuildEditorCurveClip),
                UnityBakeIgnoreImportedAvatar = bindingContext.ParseResult.GetValueForOption(UnityBakeIgnoreImportedAvatar),
                UnityBakeClipFilterMode = bindingContext.ParseResult.GetValueForOption(UnityBakeClipFilterMode),
                UnityBakeAllowGeneratedControllerDiagnostic = bindingContext.ParseResult.GetValueForOption(UnityBakeAllowGeneratedControllerDiagnostic),
                RunUnityBake = bindingContext.ParseResult.GetValueForOption(RunUnityBake),
                UnityBakeWorkerQueue = bindingContext.ParseResult.GetValueForOption(UnityBakeWorkerQueue),
                ApplyUnityBakeResult = bindingContext.ParseResult.GetValueForOption(ApplyUnityBakeResult),
                CompareUnityBakeResult = bindingContext.ParseResult.GetValueForOption(CompareUnityBakeResult),
                CompareGltf = bindingContext.ParseResult.GetValueForOption(CompareGltf),
                CompareOutput = bindingContext.ParseResult.GetValueForOption(CompareOutput),
                ExportFbxFromGltf = bindingContext.ParseResult.GetValueForOption(ExportFbxFromGltf),
                FbxSkeletonOnly = bindingContext.ParseResult.GetValueForOption(FbxSkeletonOnly),
                BakedGltfOutput = bindingContext.ParseResult.GetValueForOption(BakedGltfOutput),
                BakedFbxOutput = bindingContext.ParseResult.GetValueForOption(BakedFbxOutput),
                GenerateSkeletonGuide = bindingContext.ParseResult.GetValueForOption(GenerateSkeletonGuide),
                SkeletonGuideCatalog = bindingContext.ParseResult.GetValueForOption(SkeletonGuideCatalog),
                RebuildLibraryIndexes = bindingContext.ParseResult.GetValueForOption(RebuildLibraryIndexes),
                MigrateLibraryRelativePaths = bindingContext.ParseResult.GetValueForOption(MigrateLibraryRelativePaths),
                BuildSqliteIndex = bindingContext.ParseResult.GetValueForOption(BuildSqliteIndex),
                ProbeSourceInput = bindingContext.ParseResult.GetValueForOption(ProbeSourceInput),
                BuildSourceSqliteIndex = bindingContext.ParseResult.GetValueForOption(BuildSourceSqliteIndex),
                VerifySourceIndex = bindingContext.ParseResult.GetValueForOption(VerifySourceIndex),
                EnsureSourceIndexQueryIndexes = bindingContext.ParseResult.GetValueForOption(EnsureSourceIndexQueryIndexes),
                ListSourceModelCandidates = bindingContext.ParseResult.GetValueForOption(ListSourceModelCandidates),
                ListSourceModelAnimations = bindingContext.ParseResult.GetValueForOption(ListSourceModelAnimations),
                LocateSourceCabs = bindingContext.ParseResult.GetValueForOption(LocateSourceCabs),
                LocateEndfieldStrings = bindingContext.ParseResult.GetValueForOption(LocateEndfieldStrings),
                LocateEndfieldCabs = bindingContext.ParseResult.GetValueForOption(LocateEndfieldCabs),
                LocateEndfieldMissingSourceCabs = bindingContext.ParseResult.GetValueForOption(LocateEndfieldMissingSourceCabs),
                BuildEndfieldCabLocationIndex = bindingContext.ParseResult.GetValueForOption(BuildEndfieldCabLocationIndex),
                EndfieldCabLocationIndex = bindingContext.ParseResult.GetValueForOption(EndfieldCabLocationIndex),
                InspectEndfieldManifestDeps = bindingContext.ParseResult.GetValueForOption(InspectEndfieldManifestDeps),
                ExportAvatarOracle = bindingContext.ParseResult.GetValueForOption(ExportAvatarOracle),
                ExportNarakaAvatarMeshPlan = bindingContext.ParseResult.GetValueForOption(ExportNarakaAvatarMeshPlan),
                ExportAvatarMeshDataGltf = bindingContext.ParseResult.GetValueForOption(ExportAvatarMeshDataGltf),
                NarakaAvatarMeshExternalSkeletonSkinDiagnostic = bindingContext.ParseResult.GetValueForOption(NarakaAvatarMeshExternalSkeletonSkinDiagnostic),
                NarakaAvatarMeshFaceRuntimeSkinDiagnostic = bindingContext.ParseResult.GetValueForOption(NarakaAvatarMeshFaceRuntimeSkinDiagnostic),
                NarakaAvatarMeshRendererBonesSkinDiagnostic = bindingContext.ParseResult.GetValueForOption(NarakaAvatarMeshRendererBonesSkinDiagnostic),
                RecoverImportedAvatarAssets = bindingContext.ParseResult.GetValueForOption(RecoverImportedAvatarAssets),
                RecoverImportedAnimationClips = bindingContext.ParseResult.GetValueForOption(RecoverImportedAnimationClips),
                RecoverImportedAnimatorControllers = bindingContext.ParseResult.GetValueForOption(RecoverImportedAnimatorControllers),
                AnimatorControllerClipLibrary = bindingContext.ParseResult.GetValueForOption(AnimatorControllerClipLibrary),
                RefreshAnimatorControllerContexts = bindingContext.ParseResult.GetValueForOption(RefreshAnimatorControllerContexts),
                UnityFileInspect = bindingContext.ParseResult.GetValueForOption(UnityFileInspect),
                AvatarRecoveryLimit = bindingContext.ParseResult.GetValueForOption(AvatarRecoveryLimit),
                AvatarRecoveryForce = bindingContext.ParseResult.GetValueForOption(AvatarRecoveryForce),
                DumpUnityBlockChunks = bindingContext.ParseResult.GetValueForOption(DumpUnityBlockChunks),
                RequireFreshSourceAnimationRelations = bindingContext.ParseResult.GetValueForOption(RequireFreshSourceAnimationRelations),
                SourceCandidateLimit = bindingContext.ParseResult.GetValueForOption(SourceCandidateLimit),
                SourceIndex = bindingContext.ParseResult.GetValueForOption(SourceIndex),
                IndexPath = bindingContext.ParseResult.GetValueForOption(IndexPath),
                SkipSqliteIndex = bindingContext.ParseResult.GetValueForOption(SkipSqliteIndex),
                SkipSqliteFileIndex = bindingContext.ParseResult.GetValueForOption(SkipSqliteFileIndex),
                SkipSqliteSidecarScan = bindingContext.ParseResult.GetValueForOption(SkipSqliteSidecarScan),
                SkipSqliteJsonDocuments = bindingContext.ParseResult.GetValueForOption(SkipSqliteJsonDocuments),
                InspectUnityFiles = bindingContext.ParseResult.GetValueForOption(InspectUnityFiles),
                Blender = bindingContext.ParseResult.GetValueForOption(Blender),
                DummyDllFolder = bindingContext.ParseResult.GetValueForOption(DummyDllFolder),
                Input = bindingContext.ParseResult.GetValueForArgument(Input),
                Output = bindingContext.ParseResult.GetValueForArgument(Output)
            };

        private static Regex[] ParseRegexOption(ArgumentResult result)
        {
            var items = new List<Regex>();
            foreach (var token in result.Tokens)
            {
                var value = token.Value;
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
                    items.Add(new Regex(value, RegexOptions.IgnoreCase));
                }
            }

            return items.ToArray();
        }
    }
}
