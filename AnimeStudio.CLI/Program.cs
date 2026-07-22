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
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SharpGLTF.Schema2;
using static AnimeStudio.CLI.Studio;

namespace AnimeStudio.CLI 
{
    public class Program
    {
        public static void Main(string[] args)
        {
            if (args.Length > 0 && string.Equals(args[0], "compose-preview", StringComparison.OrdinalIgnoreCase))
            {
                Environment.ExitCode = ComposePreview(args.Skip(1).ToArray());
                return;
            }

            CommandLine.Init(args);
        }

        private static int ComposePreview(string[] args)
        {
            Logger.Default = new ConsoleLogger();
            var options = ParseCommandArgs(args);
            var libraryRoot = GetOption(options, "library-root");
            var model = GetOption(options, "model");
            var animation = GetOption(options, "animation");
            var output = GetOption(options, "output");
            var report = GetOption(options, "report");
            var game = FirstNotEmpty(GetOption(options, "game"), ReadManifestString(libraryRoot, "sourceGame"), "Normal");

            try
            {
                if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
                {
                    return WriteComposeReport(report, "error", null, output, "Library root not found.", 2);
                }
                if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(animation))
                {
                    return WriteComposeReport(report, "error", null, output, "--model and --animation are required.", 2);
                }
                if (string.IsNullOrWhiteSpace(output))
                {
                    return WriteComposeReport(report, "error", null, output, "--output is required.", 2);
                }

                var outputFullPath = Path.GetFullPath(output);
                var outputExtension = Path.GetExtension(outputFullPath);
                var writesExplicitFile = !string.IsNullOrWhiteSpace(outputExtension);
                if (writesExplicitFile
                    && !string.Equals(outputExtension, ".gltf", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(outputExtension, ".glb", StringComparison.OrdinalIgnoreCase))
                {
                    return WriteComposeReport(report, "error", null, outputFullPath, "compose-preview output must be .gltf, .glb, or a directory.", 2);
                }

                var outputDirectory = !writesExplicitFile
                    ? outputFullPath
                    : Path.GetDirectoryName(outputFullPath) ?? Directory.GetCurrentDirectory();

                var generated = PreviewGltfGenerator.GenerateFromLibrary(
                    Path.GetFullPath(libraryRoot),
                    game,
                    model,
                    animation,
                    outputDirectory,
                    sourceRootOverride: null);

                if (string.IsNullOrWhiteSpace(generated) || !File.Exists(generated))
                {
                    return WriteComposeReport(report, "error", generated, outputFullPath, "Preview generation failed.", 1);
                }

                var finalOutput = generated;
                if (writesExplicitFile)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outputFullPath) ?? Directory.GetCurrentDirectory());
                    if (string.Equals(outputExtension, ".glb", StringComparison.OrdinalIgnoreCase))
                    {
                        ModelRoot.Load(generated).SaveGLB(outputFullPath);
                    }
                    else
                    {
                        File.Copy(generated, outputFullPath, overwrite: true);
                    }
                    finalOutput = outputFullPath;
                }

                return WriteComposeReport(report, "ok", generated, finalOutput, "Preview generated.", 0);
            }
            catch (Exception e)
            {
                return WriteComposeReport(report, "error", null, output, e.GetType().Name + ": " + e.Message, 1);
            }
        }

        private static Dictionary<string, string> ParseCommandArgs(string[] args)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < args.Length; i++)
            {
                var key = args[i];
                if (!key.StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                var name = key[2..];
                var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[++i]
                    : "true";
                result[name] = value;
            }

            return result;
        }

        private static string GetOption(Dictionary<string, string> options, string name)
        {
            return options.TryGetValue(name, out var value) ? value : null;
        }

        private static string ReadManifestString(string libraryRoot, string propertyName)
        {
            if (string.IsNullOrWhiteSpace(libraryRoot))
            {
                return null;
            }

            var manifestPath = Path.Combine(libraryRoot, "asset_library.json");
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            try
            {
                return (string)JObject.Parse(File.ReadAllText(manifestPath))[propertyName];
            }
            catch
            {
                return null;
            }
        }

        private static int WriteComposeReport(string reportPath, string status, string generated, string output, string message, int exitCode)
        {
            var payload = new JObject
            {
                ["status"] = status,
                ["generated"] = generated,
                ["output"] = output,
                ["message"] = message,
                ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O")
            };

            if (!string.IsNullOrWhiteSpace(reportPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath)) ?? Directory.GetCurrentDirectory());
                File.WriteAllText(reportPath, payload.ToString(Formatting.Indented));
            }

            if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Info(message);
            }
            else
            {
                Logger.Error(message);
            }

            return exitCode;
        }

        public static void Run(Options o)
        {
            try
            {
                Logger.Default = new ConsoleLogger();
                Logger.Flags = o.LoggerFlags.Aggregate((e, x) => e |= x);
                Studio.IncludeStaticMeshes = o.IncludeStaticMeshes;
                Studio.IncludeVfx = o.IncludeVfx;
                CliExportOptions.IncludeVfx = o.IncludeVfx;

                if (o.SyncUnityBakeAcceleratedWorker)
                {
                    if (!UnityBakeAcceleratedWorkerVerifier.SyncAndCheck(o.UnityProject?.FullName, o.UnityEditor?.FullName, runSmokeTest: true))
                    {
                        Environment.ExitCode = 1;
                    }
                    return;
                }

                if (o.CheckUnityBakeAcceleratedWorker)
                {
                    if (!UnityBakeAcceleratedWorkerVerifier.Check(o.UnityProject?.FullName, o.UnityEditor?.FullName, runSmokeTest: true))
                    {
                        Environment.ExitCode = 1;
                    }
                    return;
                }

                if (o.RunUnityBakeAccelerated != null)
                {
                    var ok = string.IsNullOrWhiteSpace(o.UnityBakeAcceleratedWorkerQueue?.FullName)
                        ? UnityBakeAcceleratedRunner.RunOnce(
                            o.RunUnityBakeAccelerated.FullName,
                            o.UnityProject?.FullName,
                            o.UnityEditor?.FullName,
                            o.UnityBakeOutput?.FullName)
                        : UnityBakeAcceleratedRunner.Queue(
                            o.RunUnityBakeAccelerated.FullName,
                            o.UnityProject?.FullName,
                            o.UnityEditor?.FullName,
                            o.UnityBakeAcceleratedWorkerQueue.FullName,
                            o.UnityBakeOutput?.FullName);
                    if (!ok)
                    {
                        Environment.ExitCode = 1;
                    }
                    return;
                }

                if (o.ApplyUnityBakeAcceleratedResult != null)
                {
                    var bakedGltf = UnityBakeAcceleratedResultApplier.Apply(
                        o.ApplyUnityBakeAcceleratedResult.FullName,
                        o.BakedGltfOutput?.FullName);
                    if (string.IsNullOrWhiteSpace(bakedGltf))
                    {
                        Environment.ExitCode = 1;
                    }
                    return;
                }

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
                        o.Blender?.FullName,
                        o.FbxSkeletonOnly
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

                if (o.ListModelAnimationsFromLibrary != null)
                {
                    ModelAnimationCandidateLister.ListFromLibrary(
                        o.ListModelAnimationsFromLibrary.FullName,
                        o.PreviewModel,
                        o.PackAnimations ?? o.PreviewAnimation,
                        o.PreviewOutput?.FullName,
                        o.PackLimit,
                        o.IndexPath?.FullName
                    );
                    return;
                }

                if (o.ExportAnimationGltfFromLibrary != null)
                {
                    var animationGltfPath = PreviewGltfGenerator.ExportStandaloneAnimationFromLibrary(
                        o.ExportAnimationGltfFromLibrary.FullName,
                        o.GameName,
                        o.PreviewModel,
                        o.PreviewAnimation,
                        o.PreviewOutput?.FullName,
                        o.PreviewForceInternalHumanoidSolve
                    );
                    if (string.IsNullOrWhiteSpace(animationGltfPath))
                    {
                        Environment.ExitCode = 1;
                    }
                    return;
                }

                if (o.ExportAnimationGltfFromFiles != null)
                {
                    var animationGltfPath = PreviewGltfGenerator.ExportStandaloneAnimationFromFiles(
                        o.ExportAnimationGltfFromFiles.FullName,
                        o.PreviewAnimation,
                        o.PreviewOutput?.FullName,
                        o.PreviewForceInternalHumanoidSolve,
                        o.PackAnimations,
                        o.SourceIndex?.FullName,
                        o.PreviewAvatar
                    );
                    if (string.IsNullOrWhiteSpace(animationGltfPath))
                    {
                        Environment.ExitCode = 1;
                    }
                    return;
                }

                if (o.MergeAnimationGltf != null)
                {
                    var mergedGltfPath = PreviewGltfGenerator.MergeStandaloneAnimationGltf(
                        o.MergeAnimationGltf.FullName,
                        o.PreviewAnimation,
                        o.PreviewOutput?.FullName
                    );
                    if (string.IsNullOrWhiteSpace(mergedGltfPath))
                    {
                        Environment.ExitCode = 1;
                    }
                    return;
                }

                if (o.ApplyVerifiedAnimationPreview != null)
                {
                    var libraryRoot = o.PreviewOutput?.FullName ?? o.Output?.FullName;
                    if (string.IsNullOrWhiteSpace(libraryRoot))
                    {
                        throw new InvalidOperationException("--apply_verified_animation_preview requires --preview_output <LibraryRoot>.");
                    }

                    VerifiedAnimationPreviewImporter.Apply(
                        libraryRoot,
                        o.ApplyVerifiedAnimationPreview.FullName,
                        o.VerifiedAnimationRenderReport?.FullName,
                        o.VerifiedAnimationSourceReport?.FullName,
                        o.SourceIndex?.FullName,
                        o.GameName);
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
                    WarnUnityBakeDeprecated();
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
                        o.UnityBakeAnimatorController,
                        o.UnityBakeAvatarAsset,
                        o.UnityBakeOutput?.FullName,
                        o.UnityBakeFps,
                        o.UnityProbeMuscles,
                        o.UnityBakeEnableIkGoalDriver,
                        o.UnityBakeRebuildEditorCurveClip,
                        o.UnityBakeIgnoreImportedAvatar,
                        o.UnityBakeClipFilterMode,
                        o.RunUnityBake,
                        o.UnityBakeWorkerQueue?.FullName,
                        o.BakedGltfOutput?.FullName,
                        o.BakedFbxOutput?.FullName,
                        o.Blender?.FullName,
                        o.UnityBakeSampleRecoverableSkippedLayersDiagnostic
                    );
                    return;
                }

                if (o.GenerateUnityBakeRequestFromLibrary != null)
                {
                    WarnUnityBakeDeprecated();
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
                        o.UnityBakeAnimatorController,
                        o.UnityBakeAvatarAsset,
                        o.UnityBakeOutput?.FullName,
                        o.UnityBakeFps,
                        o.UnityProbeMuscles,
                        o.UnityBakeEnableIkGoalDriver,
                        o.UnityBakeRebuildEditorCurveClip,
                        o.UnityBakeIgnoreImportedAvatar,
                        o.UnityBakeClipFilterMode,
                        o.RunUnityBake,
                        o.UnityBakeWorkerQueue?.FullName,
                        o.BakedGltfOutput?.FullName,
                        o.BakedFbxOutput?.FullName,
                        o.Blender?.FullName,
                        o.IndexPath?.FullName,
                        o.UnityBakeSampleRecoverableSkippedLayersDiagnostic
                    );
                    return;
                }

                if (o.BakeAnimationPreviewsFromLibrary != null)
                {
                    WarnUnityBakeDeprecated();
                    TryPrepareAnimatorControllerBakeContext(o, o.BakeAnimationPreviewsFromLibrary.FullName);
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
                        o.UnityBakeAnimatorController,
                        o.UnityBakeAvatarAsset,
                        o.UnityBakeFps,
                        o.RunUnityBake,
                        o.UnityBakeClipFilterMode,
                        o.UnityBakeWorkerQueue?.FullName,
                        o.BakedFbxOutput?.FullName,
                        o.Blender?.FullName,
                        o.PreviewValidationLimit,
                        o.IndexPath?.FullName,
                        o.PreviewValidationForce,
                        o.UnityProbeMuscles,
                        o.UnityBakeEnableIkGoalDriver,
                        o.UnityBakeRebuildEditorCurveClip,
                        o.UnityBakeSampleRecoverableSkippedLayersDiagnostic
                    );
                    return;
                }

                if (o.GenerateUnityBakeAcceleratedRequestFromLibrary != null)
                {
                    // Endfield 这类 Humanoid/Muscle 动画经常依赖 AnimatorController 的
                    // state、BlendTree、layer 和实际 Unity AnimationClip asset。加速求解前
                    // 也必须先刷新这些确定性上下文，避免把单个 clip 误当完整动作。
                    TryPrepareAnimatorControllerBakeContext(o, o.GenerateUnityBakeAcceleratedRequestFromLibrary.FullName);
                    var requestPath = UnityBakeRequestGenerator.GenerateAcceleratedFromLibrary(
                        o.GenerateUnityBakeAcceleratedRequestFromLibrary.FullName,
                        o.PreviewModel,
                        o.PackAnimations ?? o.PreviewAnimation,
                        o.PreviewOutput?.FullName,
                        o.UnityProject?.FullName,
                        o.UnityEditor?.FullName,
                        o.UnityBakeAnimatorController,
                        o.UnityBakeAvatarAsset,
                        o.UnityBakeOutput?.FullName,
                        o.UnityBakeFps,
                        o.RunUnityBake,
                        o.UnityBakeClipFilterMode,
                        o.UnityBakeAllowGeneratedControllerDiagnostic,
                        o.UnityBakeAcceleratedWorkerQueue?.FullName,
                        o.IndexPath?.FullName,
                        o.UnityBakeSampleRecoverableSkippedLayersDiagnostic
                    );
                    if (string.IsNullOrWhiteSpace(requestPath))
                    {
                        Environment.ExitCode = 1;
                    }
                    return;
                }

                if (o.ApplyUnityBakeResult != null)
                {
                    WarnUnityBakeDeprecated();
                    var bakedGltf = UnityBakeResultApplier.Apply(
                        o.ApplyUnityBakeResult.FullName,
                        o.BakedGltfOutput?.FullName
                    );
                    if (!string.IsNullOrWhiteSpace(o.BakedFbxOutput?.FullName))
                    {
                        BlenderFbxExporter.Export(bakedGltf, o.BakedFbxOutput.FullName, o.Blender?.FullName, o.FbxSkeletonOnly);
                    }
                    return;
                }

                if (o.CompareUnityBakeResult != null)
                {
                    WarnUnityBakeDeprecated();
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
                        var strictSourceIndex = o.SourceIndex?.FullName ?? Path.Combine(o.RebuildLibraryIndexes.FullName, "unity_source_index.db");
                        SQLiteSourceIndexBuilder.WriteAnimationRelationHealthReport(strictSourceIndex, requireHealthy: true);
                    }

                    Studio.RebuildLibraryIndexes(o.RebuildLibraryIndexes.FullName, o.GameName, o.SourceIndex?.FullName);
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

                    SQLiteLibraryIndexBuilder.Build(o.BuildSqliteIndex.FullName, o.IndexPath?.FullName, o.SkipSqliteFileIndex, o.SkipSqliteSidecarScan, o.SkipSqliteJsonDocuments, o.SourceIndex?.FullName, o.GameName);
                    TryAutoRecoverUnityBakeAssets(o, o.BuildSqliteIndex.FullName, null, null);
                    return;
                }

                if (o.VerifySourceIndex != null)
                {
                    SQLiteSourceIndexBuilder.WriteAnimationRelationHealthReport(o.VerifySourceIndex.FullName, requireHealthy: o.RequireFreshSourceAnimationRelations);
                    return;
                }

                if (o.EnsureSourceIndexQueryIndexes != null)
                {
                    SQLiteSourceIndexBuilder.EnsureQueryIndexes(o.EnsureSourceIndexQueryIndexes.FullName);
                    return;
                }

                if (o.RefreshSimpleAnimationTypeTree != null)
                {
                    SQLiteSourceIndexBuilder.RefreshSimpleAnimationTypeTreeMetadata(
                        o.RefreshSimpleAnimationTypeTree.FullName,
                        o.PreviewSourceRoot?.FullName,
                        o.PreviewOutput?.FullName,
                        o.SourceFileFilter,
                        o.SourceCandidateLimit,
                        o.UnityVersion);
                    return;
                }

                if (o.ListSourceModelCandidates != null)
                {
                    SourceModelCandidateLister.List(
                        o.ListSourceModelCandidates.FullName,
                        o.PreviewOutput?.FullName ?? o.Output?.FullName,
                        ResolveSourceModelCandidateSelector(o),
                        o.SourceCandidateLimit,
                        o.IncludeStaticMeshes,
                        o.PreviewSourceRoot?.FullName,
                        o.GameName);
                    return;
                }

                if (o.ListSourceModelAnimations != null)
                {
                    SourceModelAnimationLister.List(
                        o.ListSourceModelAnimations.FullName,
                        o.PreviewOutput?.FullName,
                        o.PreviewModel,
                        o.PackAnimations ?? o.PreviewAnimation,
                        o.SourceCandidateLimit);
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

                if (o.ExportNarakaAvatarMeshPlan != null)
                {
                    NarakaAvatarMeshExportPlanner.WritePlan(
                        o.ExportNarakaAvatarMeshPlan.FullName,
                        o.PreviewModel ?? o.NameFilter?.FirstOrDefault()?.ToString(),
                        o.PreviewOutput?.FullName,
                        o.PreviewSourceRoot?.FullName);
                    return;
                }

                if (o.ExportAvatarMeshDataGltf != null)
                {
                    AvatarMeshDataGltfExporter.Export(
                        o.ExportAvatarMeshDataGltf.FullName,
                        o.PreviewOutput?.FullName,
                        o.SourceIndex?.FullName,
                        o.NarakaAvatarMeshExternalSkeletonSkinDiagnostic,
                        o.NarakaAvatarMeshFaceRuntimeSkinDiagnostic,
                        o.NarakaAvatarMeshRendererBonesSkinDiagnostic);
                    return;
                }

                if (o.RecoverImportedAvatarAssets != null)
                {
                    WarnUnityBakeDeprecated();
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
                        o.IndexPath?.FullName,
                        o.SourceIndex?.FullName
                    );
                    return;
                }

                if (o.RecoverImportedAnimationClips != null)
                {
                    WarnUnityBakeDeprecated();
                    var unitySettings = ResolveUnityBakeSettings(o, o.RecoverImportedAnimationClips.FullName);
                    if (string.IsNullOrWhiteSpace(unitySettings.UnityProject))
                    {
                        Logger.Error("--recover_imported_animation_clips requires a Unity bake project. Configure it once in Browser Unity settings, write .as_browser_cache\\unity_bake_settings.json, set ANIMESTUDIO_UNITY_BAKE_PROJECT, or pass --unity_project.");
                        return;
                    }

                    AnimationClipAssetRecoveryExporter.Recover(
                        o.RecoverImportedAnimationClips.FullName,
                        unitySettings.UnityProject,
                        o.PreviewModel,
                        o.PreviewAnimation,
                        o.AvatarRecoveryLimit,
                        o.AvatarRecoveryForce,
                        o.IndexPath?.FullName);
                    return;
                }

                if (o.RecoverImportedAnimatorControllers != null)
                {
                    WarnUnityBakeDeprecated();
                    var unitySettings = ResolveUnityBakeSettings(o, o.RecoverImportedAnimatorControllers.FullName);
                    if (o.RunUnityBake && string.IsNullOrWhiteSpace(unitySettings.UnityProject))
                    {
                        Logger.Error("--recover_imported_animator_controllers with --run_unity_bake requires a Unity bake project. Configure it once in Browser Unity settings, write .as_browser_cache\\unity_bake_settings.json, set ANIMESTUDIO_UNITY_BAKE_PROJECT, or pass --unity_project.");
                        return;
                    }
                    if (o.UnityFileInspect == null || !File.Exists(o.UnityFileInspect.FullName))
                    {
                        Logger.Error("--recover_imported_animator_controllers requires --unity_file_inspect pointing to unity_file_inspect.json.");
                        return;
                    }
                    if (string.IsNullOrWhiteSpace(unitySettings.UnityProject))
                    {
                        Logger.Info("--recover_imported_animator_controllers is running in request-only diagnostic mode because no valid Unity bake project is configured.");
                    }

                    AnimatorControllerAssetRecoveryExporter.Recover(
                        o.RecoverImportedAnimatorControllers.FullName,
                        unitySettings.UnityProject,
                        unitySettings.UnityEditor,
                        o.UnityFileInspect.FullName,
                        o.PreviewModel,
                        o.PreviewAnimation,
                        o.AvatarRecoveryLimit,
                        o.AvatarRecoveryForce,
                        o.RunUnityBake,
                        o.IndexPath?.FullName,
                        o.SourceIndex?.FullName,
                        o.AnimatorControllerClipLibrary?.Select(x => x.FullName));
                    return;
                }

                if (o.RefreshAnimatorControllerContexts != null)
                {
                    if (o.UnityFileInspect == null || !File.Exists(o.UnityFileInspect.FullName))
                    {
                        Logger.Error("--refresh_animator_controller_contexts requires --unity_file_inspect pointing to unity_file_inspect.json.");
                        return;
                    }

                    AnimatorControllerContextRefresher.Refresh(
                        o.RefreshAnimatorControllerContexts.FullName,
                        o.UnityFileInspect.FullName,
                        o.SourceIndex?.FullName,
                        o.IndexPath?.FullName,
                        o.PreviewModel,
                        o.PreviewAnimation);
                    return;
                }

                if (o.Input == null || o.Output == null)
                {
                    Logger.Error("input_path and output_path are required for export. Use --convert_model_textures, --generate_preview_gltf, --pack_model_animations, --generate_unity_bake_request, --apply_unity_bake_result, --recover_imported_avatar_assets, --recover_imported_animation_clips, --generate_skeleton_guide, --rebuild_library_indexes, --migrate_library_relative_paths, --build_sqlite_index, --verify_source_index, --ensure_source_index_query_indexes, --list_source_model_candidates, --list_source_model_animations, --locate_source_cabs, --locate_endfield_cabs, --locate_endfield_missing_source_cabs, --build_endfield_cab_location_index, --locate_endfield_strings, --inspect_endfield_manifest_deps, --export_avatar_oracle, --export_naraka_avatar_mesh_plan, --export_avatar_mesh_data_gltf, --probe_source_input, or --build_source_sqlite_index for post-export commands.");
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

                if (o.ProbeSourceInput)
                {
                    UnitySourceInputProbe.WriteReport(o.Input.FullName, o.Output.FullName, game);
                    return;
                }

                if (RejectKnownIncompleteModelLibraryExport(o, game))
                {
                    return;
                }

                if (RejectUnsupportedOdinContainerLibraryExport(o, game))
                {
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
                if (o.LocateSourceCabs != null && o.LocateSourceCabs.Length > 0)
                {
                    SourceCabLocator.Locate(
                        o.Input.FullName,
                        o.Output.FullName,
                        game,
                        o.LocateSourceCabs);
                    return;
                }
                if (o.LocateEndfieldStrings != null && o.LocateEndfieldStrings.Length > 0)
                {
                    EndfieldCabLocator.LocateStrings(
                        o.Input.FullName,
                        o.Output.FullName,
                        game,
                        o.LocateEndfieldStrings,
                        o.EndfieldVfsFileFilter);
                    return;
                }
                if (o.LocateEndfieldCabs != null && o.LocateEndfieldCabs.Length > 0)
                {
                    EndfieldCabLocator.Locate(
                        o.Input.FullName,
                        o.Output.FullName,
                        game,
                        o.LocateEndfieldCabs,
                        o.EndfieldVfsFileFilter,
                        o.EndfieldVfsFileLimit);
                    return;
                }
                if (o.BuildEndfieldCabLocationIndex)
                {
                    EndfieldCabLocator.BuildLocationIndex(
                        o.Input.FullName,
                        o.Output.FullName,
                        game,
                        o.EndfieldVfsFileFilter,
                        o.EndfieldVfsFileLimit);
                    return;
                }
                if (o.LocateEndfieldMissingSourceCabs != null)
                {
                    EndfieldCabLocator.LocateMissingSourceCabs(
                        o.Input.FullName,
                        o.Output.FullName,
                        game,
                        o.LocateEndfieldMissingSourceCabs.FullName,
                        o.EndfieldVfsFileFilter,
                        o.EndfieldVfsFileLimit,
                        o.EndfieldCabLocationIndex?.FullName,
                        o.SourceCandidateLimit);
                    return;
                }
                if (o.InspectEndfieldManifestDeps != null && o.InspectEndfieldManifestDeps.Length > 0)
                {
                    EndfieldManifestDependencyInspector.Inspect(
                        o.Input.FullName,
                        o.Output.FullName,
                        game,
                        o.InspectEndfieldManifestDeps);
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
                ConfigureWorkModeTypes(o.WorkMode, o.FbxAnimationMode, o.AnimationPackage);
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

                var endfieldVfsInnerFileFilter = BuildEndfieldVfsInnerFileFilter(o.EndfieldVfsFileFilter);
                var endfieldVfsInnerFileLimit = Math.Max(0, o.EndfieldVfsFileLimit);
                if (o.EndfieldManifestDeps != null)
                {
                    if (!game.Type.IsArknightsEndfieldGroup())
                    {
                        Logger.Error("--endfield_manifest_deps only supports Arknights Endfield.");
                        return;
                    }
                    if (o.EndfieldVfsFileFilter == null || o.EndfieldVfsFileFilter.Length == 0)
                    {
                        Logger.Error("--endfield_manifest_deps requires --endfield_vfs_files to select root bundle(s).");
                        return;
                    }

                    EndfieldManifestDependencyInspector.DependencyExpandedFilter manifestFilter;
                    try
                    {
                        manifestFilter = EndfieldManifestDependencyInspector.BuildDependencyExpandedInnerFileFilter(
                            o.EndfieldManifestDeps.FullName,
                            o.EndfieldVfsFileFilter);
                    }
                    catch (Exception e) when (e is IOException || e is InvalidDataException || e is ArgumentException)
                    {
                        Logger.Error($"Failed to parse Endfield manifest dependency closure: {e.Message}");
                        return;
                    }

                    if (manifestFilter.RootBundles.Length == 0)
                    {
                        Logger.Error("--endfield_manifest_deps did not find any manifest root bundle matched by --endfield_vfs_files.");
                        return;
                    }

                    endfieldVfsInnerFileFilter = manifestFilter.Filter;
                    endfieldVfsInnerFileLimit = 0;
                    Logger.Warning("--endfield_manifest_deps is diagnostic source-index dependency mode. It expands selected root bundle(s) to root + direct Endfield manifest dependencies and must be validated before production full Library use.");
                    Logger.Info($"Endfield manifest direct deps selected roots={manifestFilter.RootBundles.Length}, selectedBundles={manifestFilter.ClosureBundles.Length}, manifestBundles={manifestFilter.ManifestBundleCount}, encoding={manifestFilter.InputEncoding}.");
                    Logger.Info($"Endfield manifest direct deps root sample: {string.Join(", ", manifestFilter.RootBundles.Take(8))}{(manifestFilter.RootBundles.Length > 8 ? " ..." : string.Empty)}");
                }
                if (!o.EndfieldSourceCabClosure.IsNullOrEmpty())
                {
                    if (!game.Type.IsArknightsEndfieldGroup())
                    {
                        Logger.Error("--endfield_source_cab_closure only supports Arknights Endfield.");
                        return;
                    }

                    var closureFilters = new List<EndfieldCabLocator.SourceCabClosureFilter>();
                    foreach (var report in o.EndfieldSourceCabClosure)
                    {
                        EndfieldCabLocator.SourceCabClosureFilter closureFilter;
                        try
                        {
                            closureFilter = EndfieldCabLocator.BuildSourceCabClosureInnerFileFilter(
                                report.FullName,
                                o.EndfieldSourceCabClosureDomains);
                        }
                        catch (Exception e) when (e is IOException || e is InvalidDataException || e is JsonException || e is ArgumentException)
                        {
                            Logger.Error($"Failed to read Endfield source CAB closure report: {e.Message}");
                            return;
                        }

                        if (closureFilter.BundleFiles.Length == 0)
                        {
                            Logger.Error($"--endfield_source_cab_closure did not contain any locatedUnityBundleFiles: {report.FullName}");
                            return;
                        }

                        closureFilters.Add(closureFilter);
                    }

                    var previousFilter = endfieldVfsInnerFileFilter;
                    endfieldVfsInnerFileFilter = innerFile =>
                        (previousFilter != null && previousFilter(innerFile))
                        || closureFilters.Any(closure => closure.Filter(innerFile));
                    endfieldVfsInnerFileLimit = 0;
                    Logger.Warning("--endfield_source_cab_closure is diagnostic source-index closure mode. It includes missing Mesh/Material/Texture CAB bundles located from an existing source index report; validate the rebuilt model itself before animation smoke.");
                    var closureBundles = closureFilters
                        .SelectMany(x => x.BundleFiles)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    Logger.Info($"Endfield source CAB closure reports={closureFilters.Count}, selectedBundles={closureBundles.Length}, selectedCabs={closureFilters.Sum(x => x.SelectedCabCount)}, foundCabs={closureFilters.Sum(x => x.FoundTargetCount)}.");
                    if (!o.EndfieldSourceCabClosureDomains.IsNullOrEmpty())
                    {
                        Logger.Info($"Endfield source CAB closure domains: {string.Join(", ", o.EndfieldSourceCabClosureDomains)}");
                    }
                    if (o.EndfieldSourceCabClosureIncludeAutoRoots)
                    {
                        Logger.Warning("--endfield_source_cab_closure_include_auto_roots may expand Endfield VFS root/context coverage into a very large diagnostic source index. Prefer explicit --endfield_vfs_files roots for model-first smoke reproduction when a candidate root bundle is known.");
                    }
                    Logger.Info($"Endfield source CAB closure bundle sample: {string.Join(", ", closureBundles.Take(8))}{(closureBundles.Length > 8 ? " ..." : string.Empty)}");
                }
                if (endfieldVfsInnerFileFilter != null || endfieldVfsInnerFileLimit > 0 || !o.EndfieldSourceCabClosure.IsNullOrEmpty())
                {
                    Logger.Warning("--endfield_vfs_files / --endfield_vfs_file_limit is diagnostic only. It narrows .blc inner UnityFS files for smoke tests and must not be used for production full Library source indexes.");
                }
                if (o.EndfieldVfsKeepSameLengthSupplemental)
                {
                    Logger.Warning("--endfield_vfs_keep_same_length_supplemental is a slow Endfield VFS full-closure diagnostic mode. Use it to chase unresolved material/CAB targets, not as the default production source-index path.");
                }

                assetsManager.Silent = o.Silent;
                assetsManager.Game = game;
                assetsManager.SpecifyUnityVersion = o.UnityVersion;
                assetsManager.EndfieldVfsInnerFileFilter = endfieldVfsInnerFileFilter;
                assetsManager.EndfieldVfsInnerFileLimit = endfieldVfsInnerFileLimit;
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
                        o.IndexPath?.FullName,
                        endfieldVfsInnerFileFilter,
                        endfieldVfsInnerFileLimit,
                        o.EndfieldVfsKeepSameLengthSupplemental,
                        o.EndfieldSourceCabClosureIncludeAutoRoots,
                        o.SourceFileFilter
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
                        Math.Max(1, o.BatchFiles),
                        null,
                        endfieldVfsInnerFileFilter,
                        endfieldVfsInnerFileLimit,
                        o.EndfieldVfsKeepSameLengthSupplemental,
                        o.EndfieldSourceCabClosureIncludeAutoRoots,
                        o.SourceFileFilter
                    );
                }

                ExpandTargetedAnimatorControllerBaseClipPathIds(o, sourceIndexPath);

                var usesTargetedSourceIndexClosure = false;
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
                    TryApplyEndfieldVfsSourceIndexInnerFilter(o, game, inputBaseFolder, sourceIndexPath, files, assetsManager);
                    if (TryApplyTargetedSourceIndexSourceFileClosure(o, game, inputBaseFolder, sourceIndexPath, ref files))
                    {
                        // 已经用完整源索引算出本次目标需要的物理 source file。
                        // 再跑通用 CAB external 闭包会把共享 common 包全部拖进来，定向烟测会退化成大范围加载。
                        assetsManager.ResolveDependencies = false;
                        usesTargetedSourceIndexClosure = true;
                    }
                    else if (ShouldSkipDependencyClosureForTargetedIndependentExport(o))
                    {
                        // JSON/Raw/Dump 只读目标对象本体；定向诊断时继续解析外部 CAB 会把 Naraka 这类大包拖成全依赖加载。
                        // Convert 只允许材质、贴图、脚本等可独立导出的类型走这里；模型类仍保持完整依赖逻辑。
                        assetsManager.ResolveDependencies = false;
                        Logger.Info("Targeted independent Export detected; skipping broad CAB dependency closure for standalone diagnostic assets.");
                        ProfileLogger.Event("targeted_independent_export_skip_dependency_closure", new Dictionary<string, object>
                        {
                            ["exportType"] = o.AssetExportType.ToString(),
                            ["sourceFileFilterCount"] = o.SourceFileFilter?.Length ?? 0,
                            ["pathIdFilterCount"] = o.PathIdFilter?.Length ?? 0,
                        });
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
                        bool cabMapLoaded;
                        using (ProfileLogger.Measure("load_cab_map", new Dictionary<string, object> { ["mapName"] = mapName }))
                        {
                            cabMapLoaded = AssetsHelper.LoadCABMapInternal(mapName);
                        }
                        if (cabMapLoaded)
                        {
                            // 显式兼容路径：刚建好的 CABMap 也要立刻加载，
                            // 后续 LoadFiles 才能按 Unity external CAB 关系补齐依赖闭包。
                            assetsManager.ResolveDependencies = true;
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
                    var effectiveBatchSize = usesTargetedSourceIndexClosure
                        ? Math.Max(1, fileList.Count)
                        : GetEffectiveBatchSize(o.WorkMode);
                    if (usesTargetedSourceIndexClosure)
                    {
                        // 定向闭包里的目标文件和外部 CAB 必须同批加载。
                        // 否则批次导出会先写模型、后加载贴图/材质依赖，造成假缺失。
                        Logger.Info($"Targeted source-index closure will load {fileList.Count} source file(s) in one batch so Unity PPtr dependencies are available before export.");
                    }
                    foreach (var batch in ChunkFiles(fileList, effectiveBatchSize))
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
                            inspectReports.AddRange(BuildUnityFileInspect(assetsManager, batch, o.PathIdFilter, o.SourceObjectKeyFilter));
                            assetsManager.Clear();
                            continue;
                        }
                        if (assetsManager.assetsFileList.Count > 0)
                        {
                            Studio.RepairRendererMeshReferencesFromSourceIndex(sourceIndexPath);
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
                                    o.SourceObjectKeyFilter,
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
                        GenerateLibraryIndexes(
                            o.Output.FullName,
                            skipSqliteIndex: o.SkipSqliteIndex,
                            sourceGame: game?.Name,
                            sourceIndexPath: sourceIndexPath);
                        if (!o.SkipSqliteIndex)
                        {
                            TryRefreshAnimatorControllerContextsAfterTargetedExport(o, o.Output.FullName, sourceIndexPath);
                            TryAutoRecoverUnityBakeAssets(o, o.Output.FullName, game, inputBaseFolder);
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

        private static bool RejectKnownIncompleteModelLibraryExport(Options o, Game game)
        {
            if (game?.Type.IsArknightsEndfieldGroup() != true || o.WorkMode != WorkMode.Library)
            {
                return false;
            }

            if (IsAllowedKnownIncompleteModelDiagnostic(o))
            {
                return false;
            }

            const string reason = "knownIncompleteModelExport";
            var message =
                "拒绝导出 Arknights Endfield 默认 Library：当前项目已知该游戏默认模型导出仍不能稳定产出完整可用模型。"
                + "最近烟测显示 cutscene 包模型全部为 warning、withTextures=0，并存在材质/贴图 CAB 闭包和本机 VFS chunk 缺失问题。"
                + "为避免跑完整导出后才发现模型不可用，请先使用诊断命令补齐源索引、CAB、材质贴图闭包，"
                + "并用真实角色样本验证 model_validation=ok 后再恢复默认导出。";
            Logger.Error(message);
            TryWriteExportRefusalReport(o.Output?.FullName, game, reason, message);
            Environment.ExitCode = 2;
            return true;
        }

        private static bool IsAllowedKnownIncompleteModelDiagnostic(Options o)
        {
            // 这些命令只做源数据诊断或索引建设，不产出默认可用模型库。
            return o.InspectUnityFiles
                || o.BuildSourceSqliteIndex
                || o.DumpUnityBlockChunks
                || (o.LocateSourceCabs != null && o.LocateSourceCabs.Length > 0)
                || (o.LocateEndfieldStrings != null && o.LocateEndfieldStrings.Length > 0)
                || (o.LocateEndfieldCabs != null && o.LocateEndfieldCabs.Length > 0)
                || o.BuildEndfieldCabLocationIndex
                || o.LocateEndfieldMissingSourceCabs != null
                || (o.InspectEndfieldManifestDeps != null && o.InspectEndfieldManifestDeps.Length > 0);
        }

        private static bool RejectUnsupportedOdinContainerLibraryExport(Options o, Game game)
        {
            if (o?.WorkMode != WorkMode.Library || IsAllowedKnownIncompleteModelDiagnostic(o))
            {
                return false;
            }

            if (o.Input == null || !Directory.Exists(o.Input.FullName))
            {
                return false;
            }

            var probe = ProbeOdinContainerInput(o.Input.FullName);
            if (probe.OdinHeaderFileCount <= 0 || probe.LoadableUnityLikeHeaderFileCount > 0)
            {
                return false;
            }

            const string reason = "unsupportedOdinContainer";
            var message =
                "拒绝导出默认 Library：当前输入主要资源使用 SGEngine/Odin 自定义资源容器，"
                + $"检测到 Odin 文件 {probe.OdinHeaderFileCount} 个，但没有检测到可直接加载的 UnityFS/UnityWeb/UnityRaw/Naraka bundle 头。"
                + "现有读取器只能解析内置少量 Unity assets，不能解出真实角色、场景、材质和贴图；"
                + "如果继续全量导出，会生成空素材库或误导性的内置资源结果。"
                + "请先实现 Odin 容器解码，再重建 unity_source_index.db 并重新跑模型候选烟测。";
            Logger.Error(message);
            TryWriteExportRefusalReport(o.Output?.FullName, game, reason, message);
            Environment.ExitCode = 2;
            return true;
        }

        private static OdinContainerProbe ProbeOdinContainerInput(string inputRoot)
        {
            var result = new OdinContainerProbe();
            foreach (var file in Directory.EnumerateFiles(inputRoot, "*", SearchOption.AllDirectories))
            {
                if (!TryReadFirstBytes(file, 8, out var header))
                {
                    continue;
                }

                if (IsOdinHeader(header))
                {
                    result.OdinHeaderFileCount++;
                }

                if (IsUnityLikeLoadableHeader(header))
                {
                    result.LoadableUnityLikeHeaderFileCount++;
                }
            }

            return result;
        }

        private static bool TryReadFirstBytes(string path, int count, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            try
            {
                using var stream = File.OpenRead(path);
                if (stream.Length <= 0)
                {
                    return false;
                }

                bytes = new byte[Math.Min(count, stream.Length)];
                var read = stream.Read(bytes, 0, bytes.Length);
                if (read == bytes.Length)
                {
                    return true;
                }

                Array.Resize(ref bytes, read);
                return read > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsOdinHeader(byte[] header)
        {
            return header.Length >= 8
                && header[0] == 0x4F
                && header[1] == 0x64
                && header[2] == 0x69
                && header[3] == 0x6E
                && header[4] == 0
                && header[5] == 0
                && header[6] == 0
                && header[7] == 0;
        }

        private static bool IsUnityLikeLoadableHeader(byte[] header)
        {
            if (header.Length < 7)
            {
                return false;
            }

            var text = Encoding.ASCII.GetString(header, 0, Math.Min(header.Length, 8));
            return text.StartsWith("UnityFS", StringComparison.Ordinal)
                || text.StartsWith("UnityWeb", StringComparison.Ordinal)
                || text.StartsWith("UnityRaw", StringComparison.Ordinal)
                || IsNarakaBundleHeader(header);
        }

        private static bool IsNarakaBundleHeader(byte[] header)
        {
            return header.Length >= 7
                && header[0] == 0x15
                && header[1] == 0x1E
                && header[2] == 0x1C
                && header[3] == 0x0D
                && header[4] == 0x0D
                && header[5] == 0x23
                && header[6] == 0x21;
        }

        private sealed class OdinContainerProbe
        {
            public int OdinHeaderFileCount { get; set; }
            public int LoadableUnityLikeHeaderFileCount { get; set; }
        }

        private static void TryWriteExportRefusalReport(string outputRoot, Game game, string reason, string message)
        {
            if (string.IsNullOrWhiteSpace(outputRoot))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(outputRoot);
                var report = new JObject
                {
                    ["status"] = "refused",
                    ["reason"] = reason,
                    ["game"] = game?.Name ?? "Unknown",
                    ["gameType"] = game?.Type.ToString() ?? "Unknown",
                    ["message"] = message,
                    ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["allowedDiagnostics"] = new JArray(
                        "--probe_source_input",
                        "--inspect_unity_files",
                        "--build_source_sqlite_index",
                        "--list_source_model_candidates",
                        "--locate_endfield_cabs",
                        "--locate_endfield_missing_source_cabs",
                        "--build_endfield_cab_location_index",
                        "--locate_endfield_strings",
                        "--inspect_endfield_manifest_deps")
                };
                File.WriteAllText(
                    Path.Combine(outputRoot, "export_refusal.json"),
                    report.ToString(Formatting.Indented));
            }
            catch (Exception ex)
            {
                Logger.Warning($"Unable to write export_refusal.json: {ex.Message}");
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

        private static Func<string, bool> BuildEndfieldVfsInnerFileFilter(Regex[] filters)
        {
            if (filters.IsNullOrEmpty())
            {
                return null;
            }

            return fileName =>
            {
                var normalized = NormalizeSourceFileFilter(fileName);
                return filters.Any(filter => filter.IsMatch(normalized));
            };
        }

        private static void ExpandTargetedAnimatorControllerBaseClipPathIds(Options o, string sourceIndexPath)
        {
            if (o == null
                || o.WorkMode != WorkMode.Library
                || (o.PathIdFilter.IsNullOrEmpty() && o.SourceObjectKeyFilter.IsNullOrEmpty()))
            {
                return;
            }

            var selected = (o.PathIdFilter ?? Array.Empty<long>()).ToHashSet();
            var selectedKeys = ParseSourceObjectKeys(o.SourceObjectKeyFilter);
            var added = new HashSet<long>();
            var addedKeys = new HashSet<SourceObjectKey>();
            var sources = new JArray();

            AddRendererGameObjectDependenciesFromSourceIndex(sourceIndexPath, selected, selectedKeys, added, addedKeys, sources);
            AddContainerRendererGameObjectDependenciesFromSourceIndex(sourceIndexPath, selectedKeys, addedKeys, sources);
            AddBaseClipDependenciesFromInspect(o.UnityFileInspect?.FullName, selected, added, sources);
            AddBaseClipDependenciesFromSourceIndex(sourceIndexPath, selected, added, sources);
            if (o.IncludeAnimatorControllerClipClosure)
            {
                AddControllerClipClosureFromSourceIndex(sourceIndexPath, selected, added, sources);
            }
            if (added.Count == 0 && addedKeys.Count == 0)
            {
                return;
            }

            if (!o.PathIdFilter.IsNullOrEmpty())
            {
                o.PathIdFilter = selected.Concat(added).Distinct().ToArray();
            }
            if (addedKeys.Count > 0)
            {
                o.SourceObjectKeyFilter = selectedKeys
                    .Concat(addedKeys)
                    .Select(FormatSourceObjectKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            var reason = o.IncludeAnimatorControllerClipClosure
                ? "AnimatorController baseLayerClip/controller clip closure"
                : "Renderer GameObject / AnimatorController baseLayerClip dependency";
            Logger.Info($"Expanded targeted object filters with {added.Count + addedKeys.Count} deterministic {reason} item(s): {string.Join(", ", added.Take(12))}{(added.Count > 12 ? ", ..." : "")}");
            TryWritePathIdExpansionReport(o.Output?.FullName, selected, selectedKeys, added, addedKeys, sources);
        }

        private static void AddRendererGameObjectDependenciesFromSourceIndex(
            string sourceIndexPath,
            HashSet<long> selected,
            HashSet<SourceObjectKey> selectedKeys,
            HashSet<long> added,
            HashSet<SourceObjectKey> addedKeys,
            JArray sources)
        {
            if (string.IsNullOrWhiteSpace(sourceIndexPath)
                || !File.Exists(sourceIndexPath)
                || ((selected == null || selected.Count == 0)
                    && (selectedKeys == null || selectedKeys.Count == 0)))
            {
                return;
            }

            try
            {
                SQLitePCL.Batteries_V2.Init();
                using var connection = new SqliteConnection($"Data Source={sourceIndexPath};Mode=ReadOnly");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
WITH renderer_game_objects AS (
    SELECT from_file, from_path_id, from_type, from_name, to_file, to_path_id, raw_json
    FROM source_relations INDEXED BY idx_source_relations_relation
    WHERE relation='component.gameObject'
      AND from_type IN ('SkinnedMeshRenderer', 'MeshRenderer')
      AND to_path_id IS NOT NULL
)
SELECT r.from_file,
       r.from_path_id,
       r.from_type,
       r.from_name,
       r.to_file,
       r.to_path_id,
       r.raw_json,
       COALESCE(smr_mesh.to_file, mf_mesh.to_file) AS mesh_file,
       COALESCE(smr_mesh.to_path_id, mf_mesh.to_path_id) AS mesh_path_id
FROM renderer_game_objects r
LEFT JOIN source_relations smr_mesh
  ON r.from_type='SkinnedMeshRenderer'
 AND smr_mesh.relation='skinnedMeshRenderer.mesh'
 AND smr_mesh.from_file=r.from_file COLLATE NOCASE
 AND smr_mesh.from_path_id=r.from_path_id
LEFT JOIN source_relations mf_go
  ON r.from_type='MeshRenderer'
 AND mf_go.relation='component.gameObject'
 AND mf_go.from_type='MeshFilter'
 AND mf_go.to_file=r.to_file COLLATE NOCASE
 AND mf_go.to_path_id=r.to_path_id
LEFT JOIN source_relations mf_mesh
  ON mf_mesh.relation='meshFilter.mesh'
 AND mf_mesh.from_file=mf_go.from_file COLLATE NOCASE
 AND mf_mesh.from_path_id=mf_go.from_path_id;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var rendererFile = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    var rendererPathId = reader.GetInt64(1);
                    var rendererKey = new SourceObjectKey(NormalizeSourceObjectKeyFile(rendererFile), rendererPathId);
                    var exactKeyMatched = selectedKeys != null && selectedKeys.Contains(rendererKey);
                    var pathIdMatched = selected != null && selected.Count > 0 && selected.Contains(rendererPathId);
                    if (!exactKeyMatched && !pathIdMatched)
                    {
                        continue;
                    }

                    var gameObjectFile = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                    var gameObjectPathId = reader.GetInt64(5);
                    if (gameObjectPathId == 0)
                    {
                        continue;
                    }

                    var gameObjectKey = new SourceObjectKey(NormalizeSourceObjectKeyFile(gameObjectFile), gameObjectPathId);
                    if (exactKeyMatched)
                    {
                        if (selectedKeys.Contains(gameObjectKey) || !addedKeys.Add(gameObjectKey))
                        {
                            continue;
                        }
                    }
                    else if (selected.Contains(gameObjectPathId) || !added.Add(gameObjectPathId))
                    {
                        continue;
                    }

                    sources.Add(new JObject
                    {
                        ["source"] = "unity_source_index.source_relations.component.gameObject",
                        ["selectedRendererFile"] = rendererFile,
                        ["selectedRendererPathId"] = rendererPathId,
                        ["selectedRendererType"] = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                        ["selectedRendererName"] = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                        ["gameObjectFile"] = gameObjectFile,
                        ["gameObjectPathId"] = gameObjectPathId,
                        ["meshFile"] = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                        ["meshPathId"] = reader.IsDBNull(8) ? 0 : reader.GetInt64(8),
                        ["selectionMode"] = exactKeyMatched ? "sourceObjectKey" : "pathId",
                        ["rule"] = "定向 Library 导出选中 Renderer 组件时，追加 Unity 显式 component.gameObject 目标，让导出阶段仍以 GameObject/PrefabPrimary 作为模型入口。",
                    });
                }
            }
            catch (Exception e)
            {
                Logger.Warning($"Unable to expand --path_ids from Renderer component.gameObject source-index relations: {e.GetType().Name}: {e.Message}");
            }
        }

        private static void AddContainerRendererGameObjectDependenciesFromSourceIndex(
            string sourceIndexPath,
            HashSet<SourceObjectKey> selectedKeys,
            HashSet<SourceObjectKey> addedKeys,
            JArray sources)
        {
            if (string.IsNullOrWhiteSpace(sourceIndexPath)
                || !File.Exists(sourceIndexPath)
                || selectedKeys == null
                || selectedKeys.Count == 0)
            {
                return;
            }

            try
            {
                SQLitePCL.Batteries_V2.Init();
                using var connection = new SqliteConnection($"Data Source={sourceIndexPath};Mode=ReadOnly");
                connection.Open();
                var containers = SelectSourceObjectContainers(connection, selectedKeys);
                if (containers.Count == 0)
                {
                    return;
                }

                using var command = connection.CreateCommand();
                command.CommandText = @"
WITH container_objects AS (
    SELECT to_file, to_path_id
    FROM source_relations INDEXED BY idx_source_relations_container_relation
    WHERE relation='assetBundle.containerPreload'
      AND json_extract(raw_json, '$.details.container')=$container
),
renderers AS (
    SELECT o.serialized_file AS renderer_file, o.path_id AS renderer_path_id, o.type AS renderer_type, o.name AS renderer_name
    FROM source_objects o
    INNER JOIN container_objects c
      ON c.to_file=o.serialized_file COLLATE NOCASE
     AND c.to_path_id=o.path_id
    WHERE o.type IN ('SkinnedMeshRenderer', 'MeshRenderer')
)
SELECT r.renderer_file,
       r.renderer_path_id,
       r.renderer_type,
       r.renderer_name,
       go.to_file,
       go.to_path_id,
       COALESCE(smr_mesh.to_file, mf_mesh.to_file) AS mesh_file,
       COALESCE(smr_mesh.to_path_id, mf_mesh.to_path_id) AS mesh_path_id
FROM renderers r
INNER JOIN source_relations go
  ON go.from_file=r.renderer_file COLLATE NOCASE
 AND go.from_path_id=r.renderer_path_id
 AND go.relation='component.gameObject'
LEFT JOIN source_relations smr_mesh
  ON r.renderer_type='SkinnedMeshRenderer'
 AND smr_mesh.relation='skinnedMeshRenderer.mesh'
 AND smr_mesh.from_file=r.renderer_file COLLATE NOCASE
 AND smr_mesh.from_path_id=r.renderer_path_id
LEFT JOIN source_relations mf_go
  ON r.renderer_type='MeshRenderer'
 AND mf_go.relation='component.gameObject'
 AND mf_go.from_type='MeshFilter'
 AND mf_go.to_file=go.to_file COLLATE NOCASE
 AND mf_go.to_path_id=go.to_path_id
LEFT JOIN source_relations mf_mesh
  ON mf_mesh.relation='meshFilter.mesh'
 AND mf_mesh.from_file=mf_go.from_file COLLATE NOCASE
 AND mf_mesh.from_path_id=mf_go.from_path_id
WHERE go.to_file <> ''
  AND go.to_path_id IS NOT NULL
  AND COALESCE(smr_mesh.to_path_id, mf_mesh.to_path_id) IS NOT NULL;";
                var containerParam = command.Parameters.Add("$container", SqliteType.Text);
                var addedCount = 0;
                const int maxAddedPerRun = 512;
                foreach (var container in containers)
                {
                    containerParam.Value = container;
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        if (addedCount >= maxAddedPerRun)
                        {
                            Logger.Warning($"Targeted container renderer GameObject expansion reached {maxAddedPerRun} item(s); remaining renderer owners stay diagnostic for this run.");
                            return;
                        }

                        var gameObjectFile = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                        var gameObjectPathId = reader.IsDBNull(5) ? 0 : reader.GetInt64(5);
                        if (string.IsNullOrWhiteSpace(gameObjectFile) || gameObjectPathId == 0)
                        {
                            continue;
                        }

                        var gameObjectKey = new SourceObjectKey(NormalizeSourceObjectKeyFile(gameObjectFile), gameObjectPathId);
                        if (selectedKeys.Contains(gameObjectKey) || !addedKeys.Add(gameObjectKey))
                        {
                            continue;
                        }

                        addedCount++;
                        sources.Add(new JObject
                        {
                            ["source"] = "unity_source_index.source_relations.assetBundle.containerPreload.component.gameObject",
                            ["container"] = container,
                            ["rendererFile"] = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                            ["rendererPathId"] = reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                            ["rendererType"] = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                            ["rendererName"] = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                            ["gameObjectFile"] = gameObjectFile,
                            ["gameObjectPathId"] = gameObjectPathId,
                            ["meshFile"] = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                            ["meshPathId"] = reader.IsDBNull(7) ? 0 : reader.GetInt64(7),
                            ["selectionMode"] = "sourceObjectKeyContainerPreload",
                            ["rule"] = "定向 Library 导出选中 AssetBundle container 主对象时，追加同一 Unity container preload 中 Renderer 所属 GameObject，避免 prefab 根对象无直接 Mesh 时导出 0 个模型。",
                        });
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Warning($"Unable to expand targeted AssetBundle container Renderer GameObject dependencies from source index: {e.GetType().Name}: {e.Message}");
            }
        }

        private static HashSet<string> SelectSourceObjectContainers(
            SqliteConnection connection,
            HashSet<SourceObjectKey> selectedKeys)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT r.raw_json
FROM source_relations r
INNER JOIN source_objects o
  ON o.serialized_file=r.to_file COLLATE NOCASE
 AND o.path_id=r.to_path_id
WHERE r.to_file=$file COLLATE NOCASE
  AND r.to_path_id=$pathId
  AND r.relation='assetBundle.containerAsset'
  AND o.type IN ('GameObject', 'Animator')
  AND lower(coalesce(json_extract(r.raw_json, '$.details.container'), '')) LIKE '%.prefab';";
            var fileParam = command.Parameters.Add("$file", SqliteType.Text);
            var pathParam = command.Parameters.Add("$pathId", SqliteType.Integer);
            foreach (var key in selectedKeys)
            {
                fileParam.Value = key.File;
                pathParam.Value = key.PathId;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var container = ExtractContainerPath(reader.IsDBNull(0) ? null : reader.GetString(0));
                    if (!string.IsNullOrWhiteSpace(container))
                    {
                        result.Add(container);
                    }
                }
            }

            using var preloadCommand = connection.CreateCommand();
            preloadCommand.CommandText = @"
SELECT r.raw_json
FROM source_relations r
INNER JOIN source_objects o
  ON o.serialized_file=r.to_file COLLATE NOCASE
 AND o.path_id=r.to_path_id
WHERE r.to_file=$file COLLATE NOCASE
  AND r.to_path_id=$pathId
  AND r.relation='assetBundle.containerPreload'
  AND o.type IN ('GameObject', 'Animator', 'SkinnedMeshRenderer', 'MeshRenderer')
  AND lower(coalesce(json_extract(r.raw_json, '$.details.container'), '')) LIKE '%.prefab';";
            var preloadFileParam = preloadCommand.Parameters.Add("$file", SqliteType.Text);
            var preloadPathParam = preloadCommand.Parameters.Add("$pathId", SqliteType.Integer);
            foreach (var key in selectedKeys)
            {
                preloadFileParam.Value = key.File;
                preloadPathParam.Value = key.PathId;
                using var reader = preloadCommand.ExecuteReader();
                while (reader.Read())
                {
                    var container = ExtractContainerPath(reader.IsDBNull(0) ? null : reader.GetString(0));
                    if (!string.IsNullOrWhiteSpace(container))
                    {
                        result.Add(container);
                    }
                }
            }

            return result;
        }

        private static void TryRefreshAnimatorControllerContextsAfterTargetedExport(
            Options o,
            string outputRoot,
            string sourceIndexPath)
        {
            if (o == null
                || o.WorkMode != WorkMode.Library
                || o.PathIdFilter.IsNullOrEmpty()
                || string.IsNullOrWhiteSpace(outputRoot)
                || string.IsNullOrWhiteSpace(o.UnityFileInspect?.FullName)
                || !File.Exists(o.UnityFileInspect.FullName)
                || string.IsNullOrWhiteSpace(sourceIndexPath)
                || !File.Exists(sourceIndexPath))
            {
                return;
            }

            try
            {
                AnimatorControllerContextRefresher.Refresh(
                    outputRoot,
                    o.UnityFileInspect.FullName,
                    sourceIndexPath,
                    indexPath: null,
                    modelSelector: o.PreviewModel,
                    animationSelector: o.PreviewAnimation);
            }
            catch (Exception e)
            {
                Logger.Warning($"Unable to auto-refresh AnimatorController context after targeted Library export: {e.GetType().Name}: {e.Message}");
            }
        }

        private static void AddBaseClipDependenciesFromInspect(
            string unityFileInspectPath,
            HashSet<long> selected,
            HashSet<long> added,
            JArray sources)
        {
            if (string.IsNullOrWhiteSpace(unityFileInspectPath) || !File.Exists(unityFileInspectPath))
            {
                return;
            }

            Dictionary<long, long[]> dependencies;
            try
            {
                dependencies = AnimatorControllerContextRefresher.FindBaseLayerClipDependencies(unityFileInspectPath, selected);
            }
            catch (Exception e)
            {
                Logger.Warning($"Unable to expand --path_ids from unity_file_inspect AnimatorController context: {e.GetType().Name}: {e.Message}");
                return;
            }

            foreach (var item in dependencies.OrderBy(x => x.Key))
            {
                foreach (var basePathId in item.Value)
                {
                    if (selected.Contains(basePathId) || !added.Add(basePathId))
                    {
                        continue;
                    }

                    sources.Add(new JObject
                    {
                        ["source"] = "unity_file_inspect.animatorControllerContext.baseLayerClip",
                        ["selectedClipPathId"] = item.Key,
                        ["baseLayerClipPathId"] = basePathId,
                    });
                }
            }
        }

        private static void AddBaseClipDependenciesFromSourceIndex(
            string sourceIndexPath,
            HashSet<long> selected,
            HashSet<long> added,
            JArray sources)
        {
            if (string.IsNullOrWhiteSpace(sourceIndexPath) || !File.Exists(sourceIndexPath))
            {
                return;
            }

            try
            {
                SQLitePCL.Batteries_V2.Init();
                using var connection = new SqliteConnection($"Data Source={sourceIndexPath};Mode=ReadOnly");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT to_path_id, raw_json
FROM source_relations INDEXED BY idx_source_relations_relation
WHERE relation='animatorController.clip';";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(0))
                    {
                        continue;
                    }

                    var selectedClipPathId = reader.GetInt64(0);
                    if (!selected.Contains(selectedClipPathId) || reader.IsDBNull(1))
                    {
                        continue;
                    }

                    JObject raw;
                    try
                    {
                        raw = JObject.Parse(reader.GetString(1));
                    }
                    catch
                    {
                        continue;
                    }

                    var basePathId = ReadBaseLayerClipPathId(raw["details"]?["baseLayerClip"]);
                    if (basePathId == null
                        || basePathId.Value == selectedClipPathId
                        || selected.Contains(basePathId.Value)
                        || !added.Add(basePathId.Value))
                    {
                        continue;
                    }

                    sources.Add(new JObject
                    {
                        ["source"] = "unity_source_index.source_relations.animatorController.clip.baseLayerClip",
                        ["controllerPathId"] = (long?)raw["from"]?["pathId"],
                        ["controllerName"] = (string)raw["from"]?["name"],
                        ["selectedClipPathId"] = selectedClipPathId,
                        ["baseLayerClipPathId"] = basePathId.Value,
                    });
                }
            }
            catch (Exception e)
            {
                Logger.Warning($"Unable to expand --path_ids from unity_source_index AnimatorController relations: {e.GetType().Name}: {e.Message}");
            }
        }

        private static void AddControllerClipClosureFromSourceIndex(
            string sourceIndexPath,
            HashSet<long> selected,
            HashSet<long> added,
            JArray sources)
        {
            if (string.IsNullOrWhiteSpace(sourceIndexPath) || !File.Exists(sourceIndexPath))
            {
                Logger.Warning("--include_animator_controller_clip_closure was requested but no unity_source_index.db is available.");
                return;
            }

            try
            {
                SQLitePCL.Batteries_V2.Init();
                using var connection = new SqliteConnection($"Data Source={sourceIndexPath};Mode=ReadOnly");
                connection.Open();
                var controllers = FindControllersForSelectedClips(connection, selected.Concat(added).ToHashSet());
                if (controllers.Count == 0)
                {
                    return;
                }

                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT from_path_id, from_name, to_path_id, raw_json
FROM source_relations INDEXED BY idx_source_relations_relation
WHERE relation='animatorController.clip'
  AND from_path_id=$controllerPathId
  AND to_path_id IS NOT NULL;";
                var controllerParameter = command.CreateParameter();
                controllerParameter.ParameterName = "$controllerPathId";
                command.Parameters.Add(controllerParameter);

                foreach (var controller in controllers.OrderBy(x => x.Key))
                {
                    controllerParameter.Value = controller.Key;
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        if (reader.IsDBNull(2))
                        {
                            continue;
                        }

                        var clipPathId = reader.GetInt64(2);
                        if (selected.Contains(clipPathId) || !added.Add(clipPathId))
                        {
                            continue;
                        }

                        var details = TryParseRawJson(reader.IsDBNull(3) ? null : reader.GetString(3))?["details"];
                        sources.Add(new JObject
                        {
                            ["source"] = "unity_source_index.source_relations.animatorController.clip.closure",
                            ["controllerPathId"] = controller.Key,
                            ["controllerName"] = controller.Value ?? string.Empty,
                            ["clipPathId"] = clipPathId,
                            ["controllerClipIndex"] = (int?)details?["controllerClipIndex"],
                            ["stateName"] = (string)details?["stateName"],
                            ["statePath"] = (string)details?["statePath"],
                            ["rule"] = "显式诊断闭包：同一个 AnimatorController.m_AnimationClips 引用到的 AnimationClip 一起导出，用于恢复 Unity worker 所需的 controller clip 依赖；不新增模型-动画推荐关系。",
                        });
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Warning($"Unable to expand --path_ids from AnimatorController clip closure: {e.GetType().Name}: {e.Message}");
            }
        }

        private static Dictionary<long, string> FindControllersForSelectedClips(SqliteConnection connection, HashSet<long> selected)
        {
            var result = new Dictionary<long, string>();
            if (selected == null || selected.Count == 0)
            {
                return result;
            }

            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT DISTINCT from_path_id, from_name
FROM source_relations INDEXED BY idx_source_relations_relation
WHERE relation='animatorController.clip'
  AND to_path_id=$clipPathId
  AND from_path_id IS NOT NULL;";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$clipPathId";
            command.Parameters.Add(parameter);

            foreach (var clipPathId in selected)
            {
                parameter.Value = clipPathId;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(0))
                    {
                        continue;
                    }

                    var controllerPathId = reader.GetInt64(0);
                    var controllerName = reader.IsDBNull(1) ? null : reader.GetString(1);
                    result[controllerPathId] = controllerName;
                }
            }

            return result;
        }

        private static JObject TryParseRawJson(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            try
            {
                return JObject.Parse(raw);
            }
            catch
            {
                return null;
            }
        }

        private static long? ReadBaseLayerClipPathId(JToken baseLayerClip)
        {
            if (baseLayerClip is not JObject baseObject)
            {
                return null;
            }

            return (long?)baseObject["clip"]?["pathId"]
                ?? (long?)baseObject["pathId"];
        }

        private static void TryWritePathIdExpansionReport(
            string outputRoot,
            HashSet<long> selected,
            HashSet<SourceObjectKey> selectedKeys,
            HashSet<long> added,
            HashSet<SourceObjectKey> addedKeys,
            JArray sources)
        {
            if (string.IsNullOrWhiteSpace(outputRoot))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(outputRoot);
                var report = new JObject
                {
                    ["status"] = "ok",
                    ["rule"] = "定向 Library 导出会把 Renderer->GameObject、AnimatorController baseLayerClip 或显式请求的 controller clip 闭包加入目标对象筛选。SerializedFile:PathID 是优先格式，可避免不同 CAB 复用 PathID 时误选对象。这里只补导资产，不新增模型-动画绑定关系。",
                    ["selectedPathIds"] = new JArray(selected.OrderBy(x => x)),
                    ["selectedSourceObjectKeys"] = new JArray((selectedKeys ?? new HashSet<SourceObjectKey>()).OrderBy(FormatSourceObjectKey).Select(FormatSourceObjectKey)),
                    ["addedPathIds"] = new JArray(added.OrderBy(x => x)),
                    ["addedSourceObjectKeys"] = new JArray((addedKeys ?? new HashSet<SourceObjectKey>()).OrderBy(FormatSourceObjectKey).Select(FormatSourceObjectKey)),
                    ["sources"] = sources,
                };
                File.WriteAllText(
                    Path.Combine(outputRoot, "path_id_dependency_expansion.json"),
                    report.ToString(Formatting.Indented));
            }
            catch (Exception e)
            {
                Logger.Warning($"Unable to write path_id_dependency_expansion.json: {e.GetType().Name}: {e.Message}");
            }
        }

        private readonly record struct SourceObjectKey(string File, long PathId);

        private static HashSet<SourceObjectKey> ParseSourceObjectKeys(string[] filters)
        {
            var result = new HashSet<SourceObjectKey>();
            if (filters.IsNullOrEmpty())
            {
                return result;
            }

            foreach (var filter in filters)
            {
                if (TryParseSourceObjectKey(filter, out var key))
                {
                    result.Add(key);
                }
            }

            return result;
        }

        private static bool TryParseSourceObjectKey(string value, out SourceObjectKey key)
        {
            key = default;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var trimmed = value.Trim();
            var separator = trimmed.LastIndexOf(':');
            if (separator < 0)
            {
                separator = trimmed.LastIndexOf('#');
            }
            if (separator <= 0 || separator >= trimmed.Length - 1)
            {
                return false;
            }

            var file = NormalizeSourceObjectKeyFile(trimmed[..separator]);
            var pathIdText = trimmed[(separator + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(file)
                || !long.TryParse(pathIdText, out var pathId))
            {
                return false;
            }

            key = new SourceObjectKey(file, pathId);
            return true;
        }

        private static string NormalizeSourceObjectKeyFile(string file)
        {
            return (file ?? string.Empty).Trim().Replace('\\', '/').ToLowerInvariant();
        }

        private static string FormatSourceObjectKey(SourceObjectKey key)
        {
            return $"{NormalizeSourceObjectKeyFile(key.File)}:{key.PathId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        }

        private static bool ShouldSkipDependencyClosureForTargetedIndependentExport(Options o)
        {
            if (o == null
                || o.WorkMode != WorkMode.Export
                || o.SourceFileFilter.IsNullOrEmpty()
                || o.PathIdFilter.IsNullOrEmpty())
            {
                return false;
            }

            return o.AssetExportType is ExportType.JSON or ExportType.Raw or ExportType.Dump
                || IsTargetedIndependentConvertExport(o);
        }

        private static bool IsTargetedIndependentConvertExport(Options o)
        {
            if (o?.AssetExportType != ExportType.Convert || o.TypeFilter.IsNullOrEmpty())
            {
                return false;
            }

            return o.TypeFilter.All(x =>
            {
                var typeName = (x ?? string.Empty).Split(':')[0].Trim();
                if (!Enum.TryParse<ClassIDType>(typeName, ignoreCase: true, out var type))
                {
                    return false;
                }

                // 这些类型的 Convert 写出不需要同时加载模型层级。
                // 如果材质引用了外部贴图，贴图对象必须像 Naraka 计划那样用显式 path_id 单独导出。
                return type is ClassIDType.Texture2D
                    or ClassIDType.Texture2DArray
                    or ClassIDType.AudioClip
                    or ClassIDType.Shader
                    or ClassIDType.TextAsset
                    or ClassIDType.MonoBehaviour
                    or ClassIDType.Material
                    or ClassIDType.MiHoYoBinData
                    or ClassIDType.Font
                    or ClassIDType.MovieTexture
                    or ClassIDType.VideoClip
                    or ClassIDType.Sprite;
            });
        }

        private static bool TryApplyTargetedSourceIndexSourceFileClosure(
            Options o,
            Game game,
            string inputBaseFolder,
            string sourceIndexPath,
            ref string[] files)
        {
            if (o == null
                || o.WorkMode != WorkMode.Library
                || o.SourceFileFilter.IsNullOrEmpty()
                || string.IsNullOrWhiteSpace(sourceIndexPath)
                || !File.Exists(sourceIndexPath)
                || files.IsNullOrEmpty()
                || game?.Type.IsArknightsEndfieldGroup() == true
                || (o.NameFilter.IsNullOrEmpty()
                    && o.ContainerFilter.IsNullOrEmpty()
                    && o.PathIdFilter.IsNullOrEmpty()
                    && o.SourceObjectKeyFilter.IsNullOrEmpty()))
            {
                return false;
            }

            var inputRoot = Path.GetFullPath(inputBaseFolder ?? string.Empty).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var selectedSources = files
                .Select(path => NormalizeSourceFileForMatch(path, inputRoot))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (selectedSources.Count == 0)
            {
                return false;
            }

            try
            {
                var closure = ResolveTargetedSourceFileClosureFromSourceIndex(
                    sourceIndexPath,
                    selectedSources,
                    o.NameFilter,
                    o.ContainerFilter,
                    o.PathIdFilter,
                    o.SourceObjectKeyFilter);
                if (closure.Count == 0)
                {
                    return false;
                }

                foreach (var source in selectedSources)
                {
                    closure.Add(source);
                }

                var resolvedFiles = closure
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Select(path => Path.GetFullPath(Path.Combine(inputRoot, path.Replace('/', Path.DirectorySeparatorChar))))
                    .Where(File.Exists)
                    .ToArray();
                if (resolvedFiles.Length != closure.Count)
                {
                    Logger.Warning($"Targeted source-index source file closure resolved {closure.Count} source path(s), but only {resolvedFiles.Length} file(s) exist on disk. Falling back to normal CAB dependency closure.");
                    return false;
                }

                files = resolvedFiles;
                Logger.Info($"Source-index targeted source file closure selected {files.Length} physical source file(s) from {selectedSources.Count} explicit --source_files entry(ies). Broad CAB external closure is skipped for this targeted export.");
                ProfileLogger.Event("source_index_targeted_source_file_closure", new Dictionary<string, object>
                {
                    ["explicitSourceFileCount"] = selectedSources.Count,
                    ["sourceObjectKeyFilterCount"] = o.SourceObjectKeyFilter?.Length ?? 0,
                    ["resolvedSourceFileCount"] = files.Length,
                    ["sourceIndex"] = sourceIndexPath,
                });
                return true;
            }
            catch (Exception e) when (e is IOException || e is SqliteException || e is InvalidDataException)
            {
                Logger.Warning($"Unable to derive targeted source file closure from source index; falling back to normal CAB dependency closure. {e.GetType().Name}: {e.Message}");
                return false;
            }
        }

        private static HashSet<string> ResolveTargetedSourceFileClosureFromSourceIndex(
            string sourceIndexPath,
            HashSet<string> selectedSources,
            Regex[] nameFilters,
            Regex[] containerFilters,
            long[] pathIdFilters,
            string[] sourceObjectKeyFilters)
        {
            SQLitePCL.Batteries_V2.Init();
            using var connection = new SqliteConnection($"Data Source={Path.GetFullPath(sourceIndexPath)};Mode=ReadOnly");
            connection.Open();

            var cabToSource = LoadCabPhysicalSourceMap(connection);
            var sourceToCabs = cabToSource
                .GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);
            var seedCabs = selectedSources
                .Where(sourceToCabs.ContainsKey)
                .SelectMany(source => sourceToCabs[source])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (seedCabs.Count == 0)
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var selectedObjects = SelectTargetObjectsFromSourceIndex(
                connection,
                seedCabs,
                nameFilters,
                pathIdFilters,
                ParseSourceObjectKeys(sourceObjectKeyFilters));
            var selectedContainers = SelectTargetContainersFromSourceIndex(
                connection,
                seedCabs,
                selectedObjects,
                containerFilters);
            var objectClosure = SelectObjectsFromContainers(connection, selectedContainers);
            foreach (var obj in selectedObjects)
            {
                objectClosure.Add(obj);
            }

            var cabClosure = objectClosure
                .Select(x => x.File)
                .Where(IsLikelyCabName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            ExpandObjectRelationClosure(connection, objectClosure, cabClosure);

            var missingCabs = cabClosure
                .Where(IsLikelyCabName)
                .Where(cab => !cabToSource.ContainsKey(cab))
                .Take(16)
                .ToArray();
            if (missingCabs.Length > 0)
            {
                throw new InvalidDataException($"source index CAB closure has target CAB(s) without physical source mapping: {string.Join(", ", missingCabs)}");
            }

            return cabClosure
                .Where(cabToSource.ContainsKey)
                .Select(cab => cabToSource[cab])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, string> LoadCabPhysicalSourceMap(SqliteConnection connection)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT file_name, source_path
FROM serialized_files
WHERE file_name IS NOT NULL AND file_name <> ''
  AND source_path IS NOT NULL AND source_path <> '';";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var cab = reader.GetString(0);
                if (result.ContainsKey(cab))
                {
                    continue;
                }

                result[cab] = NormalizeIndexedPhysicalSourcePath(reader.GetString(1));
            }

            return result;
        }

        private static HashSet<SourceObjectKey> SelectTargetObjectsFromSourceIndex(
            SqliteConnection connection,
            HashSet<string> seedCabs,
            Regex[] nameFilters,
            long[] pathIdFilters,
            HashSet<SourceObjectKey> sourceObjectKeys)
        {
            var result = new HashSet<SourceObjectKey>();
            var pathIds = (pathIdFilters ?? Array.Empty<long>()).ToHashSet();
            sourceObjectKeys ??= new HashSet<SourceObjectKey>();
            if (nameFilters.IsNullOrEmpty() && pathIds.Count == 0 && sourceObjectKeys.Count == 0)
            {
                return result;
            }

            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT path_id, name
FROM source_objects
WHERE serialized_file=$cab COLLATE NOCASE
  AND type <> 'AssetBundle';";
            var cabParam = command.Parameters.Add("$cab", SqliteType.Text);
            foreach (var cab in seedCabs)
            {
                cabParam.Value = cab;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var pathId = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                    var name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    var nameMatched = !nameFilters.IsNullOrEmpty() && nameFilters.Any(filter => filter.IsMatch(name ?? string.Empty));
                    var pathIdMatched = pathIds.Count > 0 && pathIds.Contains(pathId);
                    var sourceObjectKeyMatched = sourceObjectKeys.Contains(new SourceObjectKey(NormalizeSourceObjectKeyFile(cab), pathId));
                    if (nameMatched || pathIdMatched || sourceObjectKeyMatched)
                    {
                        result.Add(new SourceObjectKey(cab, pathId));
                    }
                }
            }

            return result;
        }

        private static HashSet<string> SelectTargetContainersFromSourceIndex(
            SqliteConnection connection,
            HashSet<string> seedCabs,
            HashSet<SourceObjectKey> selectedObjects,
            Regex[] containerFilters)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!containerFilters.IsNullOrEmpty())
            {
                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT raw_json
FROM source_relations
WHERE relation='assetBundle.containerPreload'
  AND from_file=$cab COLLATE NOCASE;";
                var cabParam = command.Parameters.Add("$cab", SqliteType.Text);
                foreach (var cab in seedCabs)
                {
                    cabParam.Value = cab;
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var container = ExtractContainerPath(reader.IsDBNull(0) ? null : reader.GetString(0));
                        if (!string.IsNullOrWhiteSpace(container)
                            && containerFilters.Any(filter => filter.IsMatch(container)))
                        {
                            result.Add(container);
                        }
                    }
                }
            }

            if (selectedObjects.Count > 0)
            {
                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT raw_json
FROM source_relations
WHERE to_path_id=$pathId
  AND relation='assetBundle.containerAsset'
  AND to_file=$file COLLATE NOCASE;";
                var pathParam = command.Parameters.Add("$pathId", SqliteType.Integer);
                var fileParam = command.Parameters.Add("$file", SqliteType.Text);
                foreach (var obj in selectedObjects)
                {
                    pathParam.Value = obj.PathId;
                    fileParam.Value = obj.File;
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var container = ExtractContainerPath(reader.IsDBNull(0) ? null : reader.GetString(0));
                        if (!string.IsNullOrWhiteSpace(container))
                        {
                            result.Add(container);
                        }
                    }
                }
            }

            return result;
        }

        private static HashSet<SourceObjectKey> SelectObjectsFromContainers(
            SqliteConnection connection,
            HashSet<string> containers)
        {
            var result = new HashSet<SourceObjectKey>();
            if (containers.Count == 0)
            {
                return result;
            }

            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT to_file, to_path_id
FROM source_relations
WHERE json_extract(raw_json, '$.details.container')=$container
  AND relation='assetBundle.containerPreload';";
            var containerParam = command.Parameters.Add("$container", SqliteType.Text);
            foreach (var container in containers)
            {
                containerParam.Value = container;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var file = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    var pathId = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                    if (!string.IsNullOrWhiteSpace(file) && pathId != 0)
                    {
                        result.Add(new SourceObjectKey(file, pathId));
                    }
                }
            }

            return result;
        }

        private static void ExpandObjectRelationClosure(
            SqliteConnection connection,
            HashSet<SourceObjectKey> objectClosure,
            HashSet<string> cabClosure)
        {
            const int maxObjects = 50000;
            var queue = new Queue<SourceObjectKey>(objectClosure);
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT to_file, to_path_id
FROM source_relations
WHERE from_path_id=$pathId
  AND from_file=$file COLLATE NOCASE
  AND to_file <> '';";
            var pathParam = command.Parameters.Add("$pathId", SqliteType.Integer);
            var fileParam = command.Parameters.Add("$file", SqliteType.Text);

            while (queue.Count > 0)
            {
                if (objectClosure.Count > maxObjects)
                {
                    throw new InvalidDataException($"targeted source file closure exceeded {maxObjects} related objects; falling back to broad dependency closure.");
                }

                var current = queue.Dequeue();
                cabClosure.Add(current.File);
                pathParam.Value = current.PathId;
                fileParam.Value = current.File;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var toFile = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    var toPathId = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                    if (string.IsNullOrWhiteSpace(toFile))
                    {
                        continue;
                    }

                    if (IsLikelyCabName(toFile))
                    {
                        cabClosure.Add(toFile);
                    }
                    if (toPathId == 0)
                    {
                        continue;
                    }

                    var next = new SourceObjectKey(toFile, toPathId);
                    if (objectClosure.Add(next))
                    {
                        queue.Enqueue(next);
                    }
                }
            }
        }

        private static string ExtractContainerPath(string rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                return string.Empty;
            }

            try
            {
                return (string)JObject.Parse(rawJson)["details"]?["container"] ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool IsLikelyCabName(string fileName)
        {
            return !string.IsNullOrWhiteSpace(fileName)
                && fileName.StartsWith("cab-", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeIndexedPhysicalSourcePath(string sourcePath)
        {
            var normalized = NormalizeSourceFileFilter(sourcePath ?? string.Empty);
            var separator = normalized.IndexOf('|');
            return separator >= 0 ? normalized[..separator] : normalized;
        }

        private static void TryApplyEndfieldVfsSourceIndexInnerFilter(
            Options o,
            Game game,
            string inputBaseFolder,
            string sourceIndexPath,
            string[] files,
            AssetsManager assetsManager)
        {
            if (game == null
                || !game.Type.IsArknightsEndfieldGroup()
                || string.IsNullOrWhiteSpace(sourceIndexPath)
                || !File.Exists(sourceIndexPath)
                || !o.EndfieldVfsFileFilter.IsNullOrEmpty()
                || !o.EndfieldSourceCabClosure.IsNullOrEmpty()
                || o.EndfieldVfsFileLimit > 0
                || (o.NameFilter.IsNullOrEmpty() && o.PathIdFilter.IsNullOrEmpty()))
            {
                return;
            }

            var endfieldSources = files
                .Where(path => SQLiteSourceIndexBuilder.IsLikelyUnityLoadableFile(path, game)
                    && string.Equals(Path.GetExtension(path), ".blc", StringComparison.OrdinalIgnoreCase))
                .Select(path => NormalizeSourceFileForMatch(path, inputBaseFolder))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (endfieldSources.Count == 0)
            {
                return;
            }

            try
            {
                var innerFiles = ResolveEndfieldVfsInnerFilesFromSourceIndex(
                    sourceIndexPath,
                    endfieldSources,
                    o.NameFilter,
                    o.PathIdFilter);
                if (innerFiles.Count == 0)
                {
                    return;
                }

                assetsManager.EndfieldVfsInnerFileFilter = BuildExactEndfieldVfsInnerFileFilter(innerFiles);
                assetsManager.EndfieldVfsInnerFileLimit = 0;
                assetsManager.EndfieldVfsInnerFileFilterIsDiagnostic = false;
                Logger.Info($"Endfield VFS source-index target filter selected {innerFiles.Count} inner UnityFS file(s) from explicit --names/--path_ids and CAB dependency closure.");
            }
            catch (Exception e) when (e is IOException || e is SqliteException || e is InvalidDataException)
            {
                Logger.Warning($"Unable to derive Endfield VFS inner-file filter from source index; falling back to current VFS loading behavior. {e.GetType().Name}: {e.Message}");
            }
        }

        private static HashSet<string> ResolveEndfieldVfsInnerFilesFromSourceIndex(
            string sourceIndexPath,
            HashSet<string> sourcePaths,
            Regex[] nameFilters,
            long[] pathIdFilters)
        {
            SQLitePCL.Batteries_V2.Init();
            using var connection = new SqliteConnection($"Data Source={Path.GetFullPath(sourceIndexPath)};Mode=ReadOnly");
            connection.Open();

            var cabToInnerFile = LoadEndfieldCabInnerFileMap(connection, sourcePaths);
            if (cabToInnerFile.Count == 0)
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var selectedCabs = SelectEndfieldCabsForTargetObjects(connection, sourcePaths, nameFilters, pathIdFilters);
            var includeRelationClosure = HasSqliteIndexWithLeadingColumns(
                connection,
                "source_relations",
                "from_file");
            if (!includeRelationClosure)
            {
                // 主源索引很大时，source_relations 可能只有 from_path_id 前缀索引。
                // 这种情况下按 from_file 做闭包会全表扫描 1.5 亿级关系，定向导出会像卡死。
                // 先保留已索引的 source_externals 闭包，并在日志里明确这是保守降级。
                Logger.Warning("Endfield VFS source-index target filter skips source_relations CAB closure because no leading from_file index exists; using indexed source_externals closure only. Build a source_relations(from_file) query index or pass explicit closure reports if this targeted model misses dependencies.");
                ProfileLogger.Event("endfield_vfs_target_filter_relation_closure_skipped", new Dictionary<string, object>
                {
                    ["reason"] = "missing_source_relations_from_file_index",
                    ["selectedCabCount"] = selectedCabs.Count
                });
            }

            ExpandEndfieldCabDependencyClosure(connection, selectedCabs, cabToInnerFile, includeRelationClosure);

            return selectedCabs
                .Where(cabToInnerFile.ContainsKey)
                .Select(cab => cabToInnerFile[cab])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, string> LoadEndfieldCabInnerFileMap(SqliteConnection connection, HashSet<string> sourcePaths)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT source_path, serialized_file, name
FROM source_objects
WHERE type='AssetBundle'
  AND name <> '';";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var source = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                if (!MatchesEndfieldIndexedSource(source, sourcePaths))
                {
                    continue;
                }

                var cab = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var innerFile = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                if (!string.IsNullOrWhiteSpace(cab) && !string.IsNullOrWhiteSpace(innerFile))
                {
                    result[cab] = NormalizeSourceFileFilter(innerFile);
                }
            }

            return result;
        }

        private static HashSet<string> SelectEndfieldCabsForTargetObjects(
            SqliteConnection connection,
            HashSet<string> sourcePaths,
            Regex[] nameFilters,
            long[] pathIdFilters)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pathIds = (pathIdFilters ?? Array.Empty<long>()).ToHashSet();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT source_path, serialized_file, name, path_id
FROM source_objects
WHERE type <> 'AssetBundle';";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var source = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                if (!MatchesEndfieldIndexedSource(source, sourcePaths))
                {
                    continue;
                }

                var name = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                var pathId = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
                var nameMatched = !nameFilters.IsNullOrEmpty() && nameFilters.Any(filter => filter.IsMatch(name ?? string.Empty));
                var pathIdMatched = pathIds.Count > 0 && pathIds.Contains(pathId);
                if (!nameMatched && !pathIdMatched)
                {
                    continue;
                }

                var cab = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                if (!string.IsNullOrWhiteSpace(cab))
                {
                    result.Add(cab);
                }
            }

            return result;
        }

        private static void ExpandEndfieldCabDependencyClosure(
            SqliteConnection connection,
            HashSet<string> selectedCabs,
            Dictionary<string, string> cabToInnerFile,
            bool includeRelationClosure)
        {
            var queue = new Queue<string>(selectedCabs);
            using var externalCommand = connection.CreateCommand();
            externalCommand.CommandText = @"
SELECT file_name
FROM source_externals
WHERE serialized_file=$cab
  AND file_name <> '';";
            var externalCab = externalCommand.Parameters.Add("$cab", SqliteType.Text);

            using var relationCommand = includeRelationClosure ? connection.CreateCommand() : null;
            SqliteParameter relationCab = null;
            if (relationCommand != null)
            {
                relationCommand.CommandText = @"
SELECT to_file
FROM source_relations
WHERE from_file=$cab
  AND to_file <> '';";
                relationCab = relationCommand.Parameters.Add("$cab", SqliteType.Text);
            }

            while (queue.Count > 0)
            {
                var cab = queue.Dequeue();

                externalCab.Value = cab;
                using (var reader = externalCommand.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        AddCabIfKnown(reader.IsDBNull(0) ? null : reader.GetString(0));
                    }
                }

                if (relationCommand == null || relationCab == null)
                {
                    continue;
                }

                relationCab.Value = cab;
                using (var reader = relationCommand.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        AddCabIfKnown(reader.IsDBNull(0) ? null : reader.GetString(0));
                    }
                }
            }

            void AddCabIfKnown(string candidate)
            {
                if (string.IsNullOrWhiteSpace(candidate)
                    || !cabToInnerFile.ContainsKey(candidate)
                    || !selectedCabs.Add(candidate))
                {
                    return;
                }

                queue.Enqueue(candidate);
            }
        }

        private static bool HasSqliteIndexWithLeadingColumns(
            SqliteConnection connection,
            string tableName,
            params string[] columns)
        {
            if (columns == null || columns.Length == 0)
            {
                return false;
            }

            using var indexCommand = connection.CreateCommand();
            indexCommand.CommandText = $"PRAGMA index_list('{tableName.Replace("'", "''")}');";
            using var indexReader = indexCommand.ExecuteReader();
            var indexNames = new List<string>();
            while (indexReader.Read())
            {
                if (!indexReader.IsDBNull(1))
                {
                    indexNames.Add(indexReader.GetString(1));
                }
            }

            foreach (var indexName in indexNames)
            {
                using var infoCommand = connection.CreateCommand();
                infoCommand.CommandText = $"PRAGMA index_info('{indexName.Replace("'", "''")}');";
                using var infoReader = infoCommand.ExecuteReader();
                var indexColumns = new List<string>();
                while (infoReader.Read())
                {
                    if (!infoReader.IsDBNull(2))
                    {
                        indexColumns.Add(infoReader.GetString(2));
                    }
                }

                if (indexColumns.Count < columns.Length)
                {
                    continue;
                }

                var matches = true;
                for (var i = 0; i < columns.Length; i++)
                {
                    if (!string.Equals(indexColumns[i], columns[i], StringComparison.OrdinalIgnoreCase))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    return true;
                }
            }

            return false;
        }

        private static Func<string, bool> BuildExactEndfieldVfsInnerFileFilter(IEnumerable<string> innerFiles)
        {
            var selected = innerFiles
                .Select(NormalizeSourceFileFilter)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selectedByFileName = selected
                .GroupBy(GetNormalizedFileName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(path => "/" + path).ToArray(),
                    StringComparer.OrdinalIgnoreCase);

            return fileName =>
            {
                var normalized = NormalizeSourceFileFilter(fileName);
                if (selected.Contains(normalized))
                {
                    return true;
                }

                // 这里会被 Endfield VFS 每个内部文件调用。先按文件名定位少量候选，
                // 再保留旧的后缀兼容，避免全量 selected.Any 带来的成倍扫描。
                var leaf = GetNormalizedFileName(normalized);
                return selectedByFileName.TryGetValue(leaf, out var suffixes)
                    && suffixes.Any(suffix => normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            };
        }

        private static bool MatchesEndfieldIndexedSource(string sourcePath, HashSet<string> sourcePaths)
        {
            if (sourcePaths == null || sourcePaths.Count == 0)
            {
                return false;
            }

            var normalized = NormalizeSourceFileFilter(sourcePath ?? string.Empty);
            if (sourcePaths.Contains(normalized))
            {
                return true;
            }

            var vfsRelative = NormalizeEndfieldVfsRelativeSource(normalized);
            if (!string.IsNullOrWhiteSpace(vfsRelative) && sourcePaths.Contains(vfsRelative))
            {
                return true;
            }

            // Endfield 源索引里的对象来源是 “外层.blc|内部.ab|CAB”。
            // 定向导出时 files 列表只有外层 .blc；这里按外层段匹配，才能从
            // --source_index + --names 精确反推出目标 inner UnityFS 文件。
            var pipe = normalized.IndexOf('|');
            if (pipe > 0)
            {
                var outerSource = normalized[..pipe];
                if (sourcePaths.Contains(outerSource))
                {
                    return true;
                }

                var outerVfsRelative = NormalizeEndfieldVfsRelativeSource(outerSource);
                if (!string.IsNullOrWhiteSpace(outerVfsRelative) && sourcePaths.Contains(outerVfsRelative))
                {
                    return true;
                }
            }

            return false;
        }

        private static string NormalizeEndfieldVfsRelativeSource(string sourcePath)
        {
            var normalized = NormalizeSourceFileFilter(sourcePath ?? string.Empty);
            var marker = "/VFS/";
            var index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return string.Empty;
            }

            return normalized[(index + marker.Length)..].Trim('/');
        }

        private static string NormalizeSourceFileFilter(string path)
        {
            return path
                .Trim()
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace('\\', '/');
        }

        private static string GetNormalizedFileName(string normalizedPath)
        {
            normalizedPath ??= string.Empty;
            var index = normalizedPath.LastIndexOf('/');
            return index >= 0 ? normalizedPath[(index + 1)..] : normalizedPath;
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

        private static IEnumerable<object> BuildUnityFileInspect(
            AssetsManager assetsManager,
            IReadOnlyCollection<string> batch,
            long[] pathIdFilters,
            string[] sourceObjectKeyFilters)
        {
            var selectedPathIds = pathIdFilters.IsNullOrEmpty()
                ? new HashSet<long>()
                : pathIdFilters.ToHashSet();
            var selectedSourceObjectKeys = ParseSourceObjectKeys(sourceObjectKeyFilters);
            var hasTargetFilter = selectedPathIds.Count > 0 || selectedSourceObjectKeys.Count > 0;

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
                var selectedRenderers = hasTargetFilter
                    ? assetsFile.Objects
                        .OfType<Renderer>()
                        .Where(x => IsSelectedRendererForInspect(x, selectedPathIds, selectedSourceObjectKeys))
                        .Select(BuildRendererInspect)
                        .ToArray()
                    : Array.Empty<object>();

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
                    selectedRenderers,
                    batch = batch.ToArray(),
                };
            }
        }

        private static bool IsSelectedRendererForInspect(
            Renderer renderer,
            HashSet<long> selectedPathIds,
            HashSet<SourceObjectKey> selectedSourceObjectKeys)
        {
            if (renderer == null)
            {
                return false;
            }

            var rendererFile = NormalizeSourceObjectKeyFile(renderer.assetsFile?.fileName);
            var gameObjectPathId = renderer.m_GameObject?.m_PathID ?? 0;
            if (selectedPathIds.Contains(renderer.m_PathID) || selectedPathIds.Contains(gameObjectPathId))
            {
                return true;
            }

            if (selectedSourceObjectKeys.Count == 0)
            {
                return false;
            }

            return selectedSourceObjectKeys.Contains(new SourceObjectKey(rendererFile, renderer.m_PathID))
                || selectedSourceObjectKeys.Contains(new SourceObjectKey(rendererFile, gameObjectPathId));
        }

        private static object BuildRendererInspect(Renderer renderer)
        {
            var materials = renderer.m_Materials ?? new List<PPtr<Material>>();
            return new
            {
                type = renderer.type.ToString(),
                name = renderer.Name,
                file = renderer.assetsFile?.fileName,
                pathId = renderer.m_PathID,
                gameObject = DescribePPtrForInspect(renderer.assetsFile, renderer.m_GameObject, "GameObject"),
                enabled = renderer.m_Enabled,
                materialSlotCount = materials.Count,
                materials = materials.Select((x, index) => DescribePPtrForInspect(renderer.assetsFile, x, "Material", index)).ToArray(),
                skinnedMesh = renderer is SkinnedMeshRenderer skinned
                    ? DescribePPtrForInspect(renderer.assetsFile, skinned.m_Mesh, "Mesh")
                    : null,
                meshRendererMeshFilterHint = renderer is MeshRenderer
                    ? "MeshRenderer mesh lives on sibling MeshFilter; inspect source_index component.gameObject relations for MeshFilter.mesh."
                    : null,
                rootBone = renderer is SkinnedMeshRenderer skinnedRoot
                    ? DescribePPtrForInspect(renderer.assetsFile, skinnedRoot.m_RootBone, "Transform")
                    : null,
                boneCount = renderer is SkinnedMeshRenderer skinnedBones
                    ? skinnedBones.m_Bones?.Count ?? 0
                    : 0,
            };
        }

        private static object DescribePPtrForInspect<T>(SerializedFile sourceFile, PPtr<T> ptr, string typeHint, int? slot = null)
            where T : AnimeStudio.Object
        {
            if (ptr == null)
            {
                return null;
            }

            ptr.TryGet(out T resolved);
            return new
            {
                slot,
                fileId = ptr.m_FileID,
                pathId = ptr.m_PathID,
                typeHint,
                targetFile = ResolvePPtrTargetFileName(sourceFile, ptr.m_FileID),
                resolved = resolved != null,
                resolvedType = resolved?.type.ToString(),
                resolvedName = resolved?.Name,
            };
        }

        private static string ResolvePPtrTargetFileName(SerializedFile sourceFile, int fileId)
        {
            if (sourceFile == null || fileId == 0)
            {
                return sourceFile?.fileName;
            }

            var externalIndex = fileId - 1;
            if (externalIndex >= 0 && externalIndex < sourceFile.m_Externals.Count)
            {
                return sourceFile.m_Externals[externalIndex].fileName;
            }

            return null;
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
            var parseBytesRead = controller.reader?.Position - controller.reader?.byteStart;
            var parseBytesLeft = controller.reader?.BytesLeft();
            return new
            {
                name = controller.Name,
                pathId = controller.m_PathID,
                byteSize = controller.byteSize,
                parseBytesRead,
                parseBytesLeft,
                parseStatus = parseBytesLeft == 0
                    ? "exact"
                    : parseBytesLeft > 0
                        ? "underread"
                        : "overread",
                typeTreeDumpPreview = TryBuildTypeTreeDumpPreview(controller, 50000),
                tosCount = controller.m_TOS?.Count ?? 0,
                tosSamples = controller.m_TOS?
                    .OrderBy(x => x.Value, StringComparer.Ordinal)
                    .Take(1000)
                    .Select(x => new
                    {
                        hash = x.Key,
                        hashHex = $"0x{x.Key:X8}",
                        path = x.Value,
                    })
                    .ToArray() ?? Array.Empty<object>(),
                controllerValueDefaults = DescribeControllerValueDefaultsForInspect(controller),
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
                    binding = x.m_Binding,
                    blendingMode = x.m_LayerBlendingMode,
                    defaultWeight = x.m_DefaultWeight,
                    iKPass = x.m_IKPass,
                    syncedLayerAffectsTiming = x.m_SyncedLayerAffectsTiming,
                    bodyMask = DescribeHumanPoseMaskForInspect(x.m_BodyMask),
                    skeletonMask = DescribeSkeletonMaskForInspect(x.m_SkeletonMask, controller.m_TOS),
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
                        iKOnFeet = state.m_IKOnFeet,
                        loop = state.m_Loop,
                        mirror = state.m_Mirror,
                        transitions = DescribeTransitionsForInspect(state.m_TransitionConstantArray, controller.m_TOS),
                        stateParameters = DescribeStateParametersForInspect(state.m_StateParameterConstantArray, controller.m_TOS),
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
                                blend1dChildThresholds = node.m_Blend1dData?.m_ChildThresholdArray,
                                directChildBlendEventIds = node.m_BlendDirectData?.m_ChildBlendEventIDArray,
                                directChildBlendEvents = DescribeTosArrayForInspect(node.m_BlendDirectData?.m_ChildBlendEventIDArray, controller.m_TOS),
                                directChildPoseTimeEventIds = node.m_BlendDirectData?.m_ChildPoseTimeEventIDArray,
                                directChildPoseTimeEvents = DescribeTosArrayForInspect(node.m_BlendDirectData?.m_ChildPoseTimeEventIDArray, controller.m_TOS),
                                directNormalizedBlendValues = node.m_BlendDirectData?.m_NormalizedBlendValues,
                                directUsePoseTimeValues = node.m_BlendDirectData?.m_UsePoseTimeValues,
                                sequenceChildBlendEventIds = node.m_BlendSequenceData?.m_ChildBlendEventIDArray,
                                sequenceChildBlendEvents = DescribeTosArrayForInspect(node.m_BlendSequenceData?.m_ChildBlendEventIDArray, controller.m_TOS),
                                sequenceChildPoseTimeEventIds = node.m_BlendSequenceData?.m_ChildPoseTimeEventIDArray,
                                sequenceChildPoseTimeEvents = DescribeTosArrayForInspect(node.m_BlendSequenceData?.m_ChildPoseTimeEventIDArray, controller.m_TOS),
                                sequenceNormalizedBlendValues = node.m_BlendSequenceData?.m_NormalizedBlendValues,
                                sequenceUsePoseTimeValues = node.m_BlendSequenceData?.m_UsePoseTimeValues,
                                sequenceChildSpeed = node.m_BlendSequenceData?.m_ChildSpeed,
                                sequenceChildLodThreshold = node.m_BlendSequenceData?.m_ChildLodThreshold,
                                sequenceChildAbilityThreshold = node.m_BlendSequenceData?.m_ChildAbilityThreshold,
                                sequenceChildCullingMode = node.m_BlendSequenceData?.m_ChildCullingMode,
                                clipId = node.m_ClipID,
                                clip = TryGetTos(controller, node.m_ClipID),
                                clipSlot = TryGetAnimatorControllerClipSlot(controller, node.m_ClipID),
                                clipPPtr = DescribeAnimatorControllerClipSlot(controller, node.m_ClipID),
                                clipIndex = node.m_ClipIndex,
                                duration = node.m_Duration,
                                cycleOffset = node.m_CycleOffset,
                                mirror = node.m_Mirror,
                            }).ToArray() ?? Array.Empty<object>(),
                        }).ToArray() ?? Array.Empty<object>(),
                    }).ToArray() ?? Array.Empty<object>(),
                    anyStateTransitions = DescribeTransitionsForInspect(machine.m_AnyStateTransitionConstantArray, controller.m_TOS),
                    selectorStates = machine.m_SelectorStateConstantArray?.Select((selector, selectorIndex) => new
                    {
                        selectorIndex,
                        fullPathId = selector.m_FullPathID,
                        fullPath = TryGetTos(controller, selector.m_FullPathID),
                        isEntry = selector.m_isEntry,
                        transitions = selector.m_TransitionConstantArray?.Select((transition, transitionIndex) => new
                        {
                            transitionIndex,
                            destination = transition.m_Destination,
                            conditions = DescribeConditionsForInspect(transition.m_ConditionConstantArray, controller.m_TOS),
                        }).ToArray() ?? Array.Empty<object>(),
                    }).ToArray() ?? Array.Empty<object>(),
                }).ToArray(),
            };
        }

        private static JObject DescribeControllerValueDefaultsForInspect(AnimatorController controller)
        {
            var values = controller?.m_Controller?.m_Values?.m_ValueArray ?? new List<ValueConstant>();
            var defaults = controller?.m_Controller?.m_DefaultValues;
            var items = new JArray();
            foreach (var value in values.Take(4096))
            {
                var resolvedDefault = ResolveControllerDefaultValue(defaults, value);
                var item = new JObject
                {
                    ["id"] = value.m_ID,
                    ["idHex"] = $"0x{value.m_ID:X8}",
                    ["name"] = TryGetTos(controller, value.m_ID),
                    ["typeId"] = value.m_TypeID,
                    ["typeIdHex"] = $"0x{value.m_TypeID:X8}",
                    ["type"] = value.m_Type,
                    ["typeName"] = DescribeAnimatorControllerParameterType(value.m_Type),
                    ["index"] = value.m_Index,
                    ["defaultCandidates"] = DescribeControllerDefaultCandidates(defaults, value.m_Index),
                    ["rule"] = "diagnostic_only: resolvedDefault* records the AnimatorController serialized parameter default. It is deterministic controller data, but runtime-updated game parameters are still not recovered.",
                };
                if (resolvedDefault != null)
                {
                    item["resolvedDefaultKind"] = resolvedDefault.Kind;
                    item["resolvedDefaultValue"] = JToken.FromObject(resolvedDefault.Value);
                    item["resolvedDefaultSource"] = resolvedDefault.Source;
                }
                items.Add(item);
            }

            return new JObject
            {
                ["valueCount"] = values.Count,
                ["emittedValueCount"] = items.Count,
                ["truncated"] = values.Count > items.Count,
                ["defaultBoolCount"] = defaults?.m_BoolValues?.Length ?? 0,
                ["defaultIntCount"] = defaults?.m_IntValues?.Length ?? 0,
                ["defaultFloatCount"] = defaults?.m_FloatValues?.Length ?? 0,
                ["defaultPositionCount"] = defaults?.m_PositionValues?.Length ?? 0,
                ["defaultQuaternionCount"] = defaults?.m_QuaternionValues?.Length ?? 0,
                ["defaultScaleCount"] = defaults?.m_ScaleValues?.Length ?? 0,
                ["values"] = items,
            };
        }

        private static JObject DescribeControllerDefaultCandidates(ValueArray defaults, uint index)
        {
            var result = new JObject();
            var i = unchecked((int)index);
            if (defaults == null || i < 0)
            {
                return result;
            }

            if (defaults.m_BoolValues != null && i < defaults.m_BoolValues.Length)
            {
                result["bool"] = defaults.m_BoolValues[i];
            }
            if (defaults.m_IntValues != null && i < defaults.m_IntValues.Length)
            {
                result["int"] = defaults.m_IntValues[i];
            }
            if (defaults.m_FloatValues != null && i < defaults.m_FloatValues.Length)
            {
                result["float"] = defaults.m_FloatValues[i];
            }
            if (defaults.m_PositionValues != null && i < defaults.m_PositionValues.Length)
            {
                var v = defaults.m_PositionValues[i];
                result["position"] = new JArray(v.X, v.Y, v.Z);
            }
            if (defaults.m_QuaternionValues != null && i < defaults.m_QuaternionValues.Length)
            {
                var q = defaults.m_QuaternionValues[i];
                result["quaternion"] = new JArray(q.X, q.Y, q.Z, q.W);
            }
            if (defaults.m_ScaleValues != null && i < defaults.m_ScaleValues.Length)
            {
                var v = defaults.m_ScaleValues[i];
                result["scale"] = new JArray(v.X, v.Y, v.Z);
            }

            return result;
        }

        private static ControllerDefaultValue ResolveControllerDefaultValue(ValueArray defaults, ValueConstant value)
        {
            if (defaults == null || value == null)
            {
                return null;
            }

            var i = unchecked((int)value.m_Index);
            if (i < 0)
            {
                return null;
            }

            // Unity AnimatorController 参数枚举：Float=1, Int=3, Bool=4, Trigger=9。
            // 这里只解析明确的默认值来源；运行时脚本改写的参数仍需要额外证据。
            switch (value.m_Type)
            {
                case 1 when defaults.m_FloatValues != null && i < defaults.m_FloatValues.Length:
                    return new ControllerDefaultValue("float", defaults.m_FloatValues[i], "m_DefaultValues.m_FloatValues");
                case 3 when defaults.m_IntValues != null && i < defaults.m_IntValues.Length:
                    return new ControllerDefaultValue("int", defaults.m_IntValues[i], "m_DefaultValues.m_IntValues");
                case 4 when defaults.m_BoolValues != null && i < defaults.m_BoolValues.Length:
                    return new ControllerDefaultValue("bool", defaults.m_BoolValues[i], "m_DefaultValues.m_BoolValues");
                case 9 when defaults.m_BoolValues != null && i < defaults.m_BoolValues.Length:
                    return new ControllerDefaultValue("trigger", defaults.m_BoolValues[i], "m_DefaultValues.m_BoolValues");
                default:
                    return null;
            }
        }

        private static string DescribeAnimatorControllerParameterType(uint type)
        {
            return type switch
            {
                1 => "Float",
                3 => "Int",
                4 => "Bool",
                9 => "Trigger",
                _ => "Unknown",
            };
        }

        private sealed record ControllerDefaultValue(string Kind, object Value, string Source);

        private static JObject DescribeHumanPoseMaskForInspect(HumanPoseMask mask)
        {
            if (mask == null)
            {
                return null;
            }

            return new JObject
            {
                ["word0"] = mask.word0,
                ["word1"] = mask.word1,
                ["word2"] = mask.word2,
                ["isEmpty"] = mask.word0 == 0 && mask.word1 == 0 && mask.word2 == 0,
                ["rawHex"] = new JArray(
                    $"0x{mask.word0:X8}",
                    $"0x{mask.word1:X8}",
                    $"0x{mask.word2:X8}"),
            };
        }

        private static JObject DescribeSkeletonMaskForInspect(SkeletonMask mask, IReadOnlyDictionary<uint, string> tos)
        {
            if (mask?.m_Data == null)
            {
                return null;
            }

            return new JObject
            {
                ["count"] = mask.m_Data.Count,
                ["nonZeroCount"] = mask.m_Data.Count(x => Math.Abs(x.m_Weight) > 0.0001f),
                ["entries"] = new JArray(mask.m_Data.Select(x => new JObject
                {
                    ["pathHash"] = x.m_PathHash,
                    ["pathHashHex"] = $"0x{x.m_PathHash:X8}",
                    ["path"] = tos != null && tos.TryGetValue(x.m_PathHash, out var path) ? path : null,
                    ["weight"] = x.m_Weight,
                })),
            };
        }

        private static object[] DescribeStateParametersForInspect(StateParameterConstant[] parameters, IReadOnlyDictionary<uint, string> tos)
        {
            return parameters?
                .Select(x => new
                {
                    nameId = x.m_NameID,
                    nameIdHex = $"0x{unchecked((uint)x.m_NameID):X8}",
                    name = tos != null && tos.TryGetValue(unchecked((uint)x.m_NameID), out var name) ? name : null,
                    value = x.m_Value,
                })
                .ToArray()
                ?? Array.Empty<object>();
        }

        private static object[] DescribeTosArrayForInspect(IEnumerable<uint> ids, IReadOnlyDictionary<uint, string> tos)
        {
            return ids?
                .Select((id, index) => new
                {
                    index,
                    id,
                    idHex = $"0x{id:X8}",
                    name = TryGetTos(tos, id),
                })
                .ToArray()
                ?? Array.Empty<object>();
        }

        private static object[] DescribeTransitionsForInspect(IEnumerable<TransitionConstant> transitions, IReadOnlyDictionary<uint, string> tos)
        {
            return transitions?
                .Select((transition, transitionIndex) => new
                {
                    transitionIndex,
                    destinationState = transition.m_DestinationState,
                    fullPathId = transition.m_FullPathID,
                    fullPath = TryGetTos(tos, transition.m_FullPathID),
                    id = transition.m_ID,
                    userId = transition.m_UserID,
                    duration = transition.m_TransitionDuration,
                    offset = transition.m_TransitionOffset,
                    exitTime = transition.m_ExitTime,
                    hasExitTime = transition.m_HasExitTime,
                    hasFixedDuration = transition.m_HasFixedDuration,
                    interruptionSource = transition.m_InterruptionSource,
                    orderedInterruption = transition.m_OrderedInterruption,
                    canTransitionToSelf = transition.m_CanTransitionToSelf,
                    conditions = DescribeConditionsForInspect(transition.m_ConditionConstantArray, tos),
                })
                .ToArray()
                ?? Array.Empty<object>();
        }

        private static object[] DescribeConditionsForInspect(IEnumerable<ConditionConstant> conditions, IReadOnlyDictionary<uint, string> tos)
        {
            return conditions?
                .Select((condition, conditionIndex) => new
                {
                    conditionIndex,
                    mode = condition.m_ConditionMode,
                    modeName = DescribeAnimatorConditionMode(condition.m_ConditionMode),
                    eventId = condition.m_EventID,
                    eventIdHex = $"0x{condition.m_EventID:X8}",
                    eventName = TryGetTos(tos, condition.m_EventID),
                    threshold = condition.m_EventThreshold,
                    exitTime = condition.m_ExitTime,
                })
                .ToArray()
                ?? Array.Empty<object>();
        }

        private static string DescribeAnimatorConditionMode(uint mode)
        {
            return mode switch
            {
                1 => "If",
                2 => "IfNot",
                3 => "Greater",
                4 => "Less",
                6 => "Equals",
                7 => "NotEqual",
                _ => "Unknown",
            };
        }

        private static string TryGetTos(IReadOnlyDictionary<uint, string> tos, uint id)
        {
            return tos != null && tos.TryGetValue(id, out var value)
                ? value
                : null;
        }

        private static string TryBuildTypeTreeDumpPreview(AnimeStudio.Object obj, int maxChars)
        {
            if (obj?.reader == null || obj.serializedType?.m_Type == null)
            {
                return null;
            }

            var pos = obj.reader.Position;
            try
            {
                obj.reader.Reset();
                var dump = obj.Dump();
                if (string.IsNullOrWhiteSpace(dump))
                {
                    return null;
                }

                return dump.Length <= maxChars
                    ? dump
                    : dump[..maxChars] + "\n...<truncated>";
            }
            catch (Exception e)
            {
                return $"{e.GetType().Name}: {e.Message}";
            }
            finally
            {
                obj.reader.Position = pos;
            }
        }

        private static int? TryGetAnimatorControllerClipSlot(AnimatorController controller, uint clipId)
        {
            var clips = controller.m_AnimationClips ?? new List<PPtr<AnimationClip>>();
            if (clipId <= int.MaxValue)
            {
                var index = unchecked((int)clipId);
                if (index >= 0 && index < clips.Count)
                {
                    return index;
                }
            }

            return null;
        }

        private static object DescribeAnimatorControllerClipSlot(AnimatorController controller, uint clipId)
        {
            var slot = TryGetAnimatorControllerClipSlot(controller, clipId);
            if (slot == null)
            {
                return null;
            }

            var ptr = controller.m_AnimationClips[slot.Value];
            return new
            {
                index = slot.Value,
                fileId = ptr.m_FileID,
                pathId = ptr.m_PathID,
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

        private static void ConfigureWorkModeTypes(WorkMode workMode, FbxAnimationMode animationMode, AnimationPackageMode animationPackage)
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
            TypeFlags.SetType(ClassIDType.LODGroup, true, false);
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

            var explicitAnimationExport =
                animationMode != FbxAnimationMode.Skip
                || animationPackage != AnimationPackageMode.Skip;
            if (workMode == WorkMode.Library || explicitAnimationExport)
            {
                // 默认 Library 只识别动画资源边界，不把 AnimationClip 写进正式素材库。
                TypeFlags.SetType(ClassIDType.AnimationClip, true, explicitAnimationExport);
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
                ClassIDType.LODGroup,
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

        private static string ResolveSourceModelCandidateSelector(Options o)
        {
            if (!string.IsNullOrWhiteSpace(o?.PreviewModel))
            {
                return o.PreviewModel;
            }

            var nameFilters = o?.NameFilter?
                .Where(x => x != null)
                .Select(x => x.ToString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();
            if (nameFilters != null && nameFilters.Length > 0)
            {
                return nameFilters.Length == 1
                    ? nameFilters[0]
                    : string.Join("|", nameFilters.Select(x => "(" + x + ")"));
            }

            var containerFilters = o?.ContainerFilter?
                .Where(x => x != null)
                .Select(x => x.ToString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();
            if (containerFilters != null && containerFilters.Length > 0)
            {
                return containerFilters.Length == 1
                    ? containerFilters[0]
                    : string.Join("|", containerFilters.Select(x => "(" + x + ")"));
            }

            return null;
        }

        private static void TryAutoRecoverUnityBakeAssets(Options o, string libraryRoot, Game game, string sourceRoot)
        {
            Logger.Info("Legacy game-specific Unity bake asset auto recovery is deprecated and skipped by default. Production animation export must end as glTF TRS/weights; UnityBakeAccelerated remains allowed when it is deterministic, automated, high-throughput, and visually validated.");
        }

        private static void WarnUnityBakeDeprecated()
        {
            Logger.Warning("External Unity-project bake workflow is deprecated. Treat generated Unity project/plugin/helper bake requests/results as diagnostic legacy output only; production animation assets must be directly reusable glTF TRS/weights data generated by AnimeStudio.");
        }

        private static void TryPrepareAnimatorControllerBakeContext(Options o, string libraryRoot)
        {
            if (o == null || string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
            {
                return;
            }

            if (o.UnityFileInspect == null || !File.Exists(o.UnityFileInspect.FullName))
            {
                return;
            }

            var unitySettings = ResolveUnityBakeSettings(o, libraryRoot);
            if (string.IsNullOrWhiteSpace(unitySettings.UnityProject))
            {
                Logger.Info("AnimatorController context preparation skipped because no Unity bake project is configured.");
                return;
            }

            try
            {
                // 这一步只使用 unity_file_inspect.json 中的 AnimatorController state/blend-tree
                // 和 unity_source_index.db 的 Animator.controller 显式关系，不按名字猜动画。
                AnimatorControllerContextRefresher.Refresh(
                    libraryRoot,
                    o.UnityFileInspect.FullName,
                    o.SourceIndex?.FullName,
                    o.IndexPath?.FullName,
                    o.PreviewModel,
                    o.PackAnimations ?? o.PreviewAnimation);

                // 如果选中 clip 被切到了同状态 baseLayerClip，这里把实际要采样的
                // 原始 Unity AnimationClip asset 放进 bake 工程，保证 helper 能得到 isHumanMotion=true。
                AnimationClipAssetRecoveryExporter.Recover(
                    libraryRoot,
                    unitySettings.UnityProject,
                    o.PreviewModel,
                    o.PackAnimations ?? o.PreviewAnimation,
                    // AnimatorController 诊断需要同状态的 additional layer clips；
                    // 这里不能被 preview limit 截掉，否则生成的 controller 上下文会缺层。
                    limit: 0,
                    force: false,
                    explicitIndexPath: o.IndexPath?.FullName);
            }
            catch (Exception e)
            {
                var message = $"AnimatorController context preparation failed. {e.GetType().Name}: {e.Message}";
                Logger.Error(message);
                throw new InvalidOperationException(message, e);
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
                    explicitIndexPath: o.IndexPath?.FullName,
                    sourceIndexPath: o.SourceIndex?.FullName);
            }
            catch (Exception e)
            {
                Logger.Warning($"Imported Avatar asset auto recovery failed; Library export remains usable but Humanoid bake may still need Avatar metadata. {e.GetType().Name}: {e.Message}");
            }
        }

        private static void TryAutoRecoverImportedAnimationClips(Options o, string libraryRoot)
        {
            if (o == null || string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
            {
                return;
            }

            var unitySettings = ResolveUnityBakeSettings(o, libraryRoot);
            if (string.IsNullOrWhiteSpace(unitySettings.UnityProject))
            {
                Logger.Info("Imported AnimationClip auto recovery skipped because no Unity bake project is configured. Configure Browser Unity settings, .as_browser_cache\\unity_bake_settings.json, ANIMESTUDIO_UNITY_BAKE_PROJECT, or --unity_project to enable the default recovery step.");
                return;
            }

            try
            {
                AnimationClipAssetRecoveryExporter.Recover(
                    libraryRoot,
                    unitySettings.UnityProject,
                    modelSelector: null,
                    animationSelector: null,
                    limit: 0,
                    force: false,
                    explicitIndexPath: o.IndexPath?.FullName);
            }
            catch (Exception e)
            {
                Logger.Warning($"Imported AnimationClip auto recovery failed; Library export remains usable but Humanoid bake may still need imported AnimationClip assets. {e.GetType().Name}: {e.Message}");
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
