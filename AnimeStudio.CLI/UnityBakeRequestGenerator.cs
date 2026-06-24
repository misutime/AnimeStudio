using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AnimeStudio.CLI
{
    internal static class UnityBakeRequestGenerator
    {
        public static void Generate(
            string indexPath,
            string modelSelector,
            string animationSelector,
            string outputDirectory,
            string sourceRootOverride,
            string unityProject,
            string unityEditor,
            string unityModelPrefab,
            string unityAnimationClip,
            string unityAnimatorController,
            string unityAvatarAsset,
            string unityBakeOutput,
            int frameRate,
            bool probeMuscles,
            bool enableIkGoalDriver,
            bool rebuildEditorCurveClip,
            bool ignoreImportedAvatar,
            string clipFilterModeOverride,
            bool runUnityBake,
            string unityBakeWorkerQueue = null,
            string bakedGltfOutput = null,
            string bakedFbxOutput = null,
            string blender = null,
            bool sampleRecoverableSkippedLayersDiagnostic = false
        )
        {
            if (string.IsNullOrWhiteSpace(indexPath) || !File.Exists(indexPath))
            {
                Logger.Error($"model_animations.json not found: {indexPath}");
                return;
            }

            var index = JObject.Parse(File.ReadAllText(indexPath));
            var indexDirectory = Path.GetDirectoryName(Path.GetFullPath(indexPath)) ?? "";
            var selection = SelectPreview(index, modelSelector, animationSelector, indexDirectory);
            if (selection == null)
            {
                Logger.Error("No model/animation candidate matched the Unity bake request selectors.");
                return;
            }

            ResolveSelectionLibraryPaths(selection, Path.GetDirectoryName(Path.GetFullPath(indexPath)) ?? "");
            GenerateSelection(
                indexPath,
                selection,
                outputDirectory,
                sourceRootOverride,
                unityProject,
                unityEditor,
                unityModelPrefab,
                unityAnimationClip,
                unityAnimatorController,
                unityAvatarAsset,
                unityBakeOutput,
                frameRate,
                probeMuscles,
                enableIkGoalDriver,
                rebuildEditorCurveClip,
                ignoreImportedAvatar,
                clipFilterModeOverride,
                runUnityBake,
                unityBakeWorkerQueue,
                bakedGltfOutput,
                bakedFbxOutput,
                blender,
                sampleRecoverableSkippedLayersDiagnostic: sampleRecoverableSkippedLayersDiagnostic);
        }

        public static void GenerateFromLibrary(
            string libraryRoot,
            string modelSelector,
            string animationSelector,
            string outputDirectory,
            string sourceRootOverride,
            string unityProject,
            string unityEditor,
            string unityModelPrefab,
            string unityAnimationClip,
            string unityAnimatorController,
            string unityAvatarAsset,
            string unityBakeOutput,
            int frameRate,
            bool probeMuscles,
            bool enableIkGoalDriver,
            bool rebuildEditorCurveClip,
            bool ignoreImportedAvatar,
            string clipFilterModeOverride,
            bool runUnityBake,
            string unityBakeWorkerQueue = null,
            string bakedGltfOutput = null,
            string bakedFbxOutput = null,
            string blender = null,
            string indexPath = null,
            bool sampleRecoverableSkippedLayersDiagnostic = false
        )
        {
            if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
            {
                Logger.Error($"Library root not found: {libraryRoot}");
                return;
            }

            var dbPath = string.IsNullOrWhiteSpace(indexPath)
                ? Path.Combine(libraryRoot, "library_index.db")
                : indexPath;
            if (!File.Exists(dbPath))
            {
                Logger.Error($"library_index.db not found: {dbPath}. Rebuild the Library export or run --build_sqlite_index.");
                return;
            }
            if (!ValidateSuppliedUnityAvatarAssetScope(modelSelector, unityAvatarAsset))
            {
                return;
            }
            var importedAvatarAssets = DiscoverImportedAvatarAssets(unityProject, libraryRoot);
            var importedAnimationClips = DiscoverImportedAnimationClips(unityProject, libraryRoot);
            var importedAnimatorControllers = DiscoverImportedAnimatorControllers(unityProject, libraryRoot);
            var selection = SelectExplicitCandidateFromLibraryDb(
                dbPath,
                modelSelector,
                animationSelector,
                allowSuppliedAvatarAsset: !string.IsNullOrWhiteSpace(unityAvatarAsset),
                importedAvatarAssets);
            if (selection == null)
            {
                var blockedMessage = TryDescribeBlockedExplicitCandidate(dbPath, modelSelector, animationSelector);
                if (!string.IsNullOrWhiteSpace(blockedMessage))
                {
                    Logger.Error(blockedMessage);
                    return;
                }
                Logger.Error("No explicit model-animation candidate matched the SQLite Unity bake request selectors.");
                return;
            }

            var effectiveUnityAvatarAsset = ResolveUnityAvatarAssetForSelection(selection, unityAvatarAsset, importedAvatarAssets).AssetPath;
            var effectiveUnityAnimationClip = ResolveUnityAnimationClipForSelection(selection, unityAnimationClip, importedAnimationClips).AssetPath;
            var effectiveUnityAnimatorController = ResolveUnityAnimatorControllerForSelection(selection, unityAnimatorController, importedAnimatorControllers).AssetPath;
            ResolveSelectionLibraryPaths(selection, libraryRoot);
            GenerateSelection(
                dbPath,
                selection,
                outputDirectory,
                sourceRootOverride,
                unityProject,
                unityEditor,
                unityModelPrefab,
                effectiveUnityAnimationClip,
                effectiveUnityAnimatorController,
                effectiveUnityAvatarAsset,
                unityBakeOutput,
                frameRate,
                probeMuscles,
                enableIkGoalDriver,
                rebuildEditorCurveClip,
                ignoreImportedAvatar,
                clipFilterModeOverride,
                runUnityBake,
                unityBakeWorkerQueue,
                bakedGltfOutput,
                bakedFbxOutput,
                blender,
                importedAnimationClips,
                sampleRecoverableSkippedLayersDiagnostic: sampleRecoverableSkippedLayersDiagnostic);
        }

        public static string GenerateAcceleratedFromLibrary(
            string libraryRoot,
            string modelSelector,
            string animationSelector,
            string outputDirectory,
            string unityProject,
            string unityEditor,
            string unityAnimatorController,
            string unityAvatarAsset,
            string unityBakeOutput,
            int frameRate,
            bool runUnityBake,
            string clipFilterModeOverride = null,
            bool allowGeneratedControllerDiagnostic = false,
            string unityBakeWorkerQueue = null,
            string indexPath = null,
            bool sampleRecoverableSkippedLayersDiagnostic = false
        )
        {
            if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
            {
                Logger.Error($"Library root not found: {libraryRoot}");
                return null;
            }

            var dbPath = string.IsNullOrWhiteSpace(indexPath)
                ? Path.Combine(libraryRoot, "library_index.db")
                : indexPath;
            if (!File.Exists(dbPath))
            {
                Logger.Error($"library_index.db not found: {dbPath}. Rebuild the Library export or run --build_sqlite_index.");
                return null;
            }
            if (!ValidateSuppliedUnityAvatarAssetScope(modelSelector, unityAvatarAsset))
            {
                return null;
            }

            var importedAvatarAssets = DiscoverImportedAvatarAssets(unityProject, libraryRoot);
            var importedAnimationClips = DiscoverImportedAnimationClips(unityProject, libraryRoot);
            var importedAnimatorControllers = DiscoverImportedAnimatorControllers(unityProject, libraryRoot);
            var selection = SelectExplicitCandidateFromLibraryDb(
                dbPath,
                modelSelector,
                animationSelector,
                allowSuppliedAvatarAsset: !string.IsNullOrWhiteSpace(unityAvatarAsset),
                importedAvatarAssets);
            if (selection == null)
            {
                var blockedMessage = TryDescribeBlockedExplicitCandidate(dbPath, modelSelector, animationSelector);
                Logger.Error(!string.IsNullOrWhiteSpace(blockedMessage)
                    ? blockedMessage
                    : "No explicit Humanoid/Muscle model-animation candidate matched the UnityBakeAccelerated selectors.");
                return null;
            }

            ResolveSelectionLibraryPaths(selection, libraryRoot);
            var model = selection.Model["model"] as JObject;
            var effectiveUnityAvatarAsset = ResolveUnityAvatarAssetForSelection(selection, unityAvatarAsset, importedAvatarAssets).AssetPath;
            var animationClipResolution = ResolveUnityAnimationClipForSelection(selection, null, importedAnimationClips);
            var animatorControllerResolution = ResolveUnityAnimatorControllerForSelection(selection, unityAnimatorController, importedAnimatorControllers);
            var requestAvatar = PrepareAvatarForUnityBake(
                model?["avatar"] as JObject,
                requiresHumanoidBake: true,
                unityModelPrefab: null,
                unityAvatarAsset: effectiveUnityAvatarAsset);
            if (string.IsNullOrWhiteSpace(effectiveUnityAvatarAsset) && !HasUsableHumanoidReferencePose(requestAvatar, null, null))
            {
                Logger.Error("Selected candidate has no recovered Unity Avatar asset and no complete HumanDescription skeletonBones. Run --recover_imported_avatar_assets first, refresh Avatar metadata, or pass --unity_avatar_asset for this exact model scope.");
                return null;
            }

            return GenerateAcceleratedSelection(
                dbPath,
                libraryRoot,
                selection,
                outputDirectory,
                unityProject,
                unityEditor,
                effectiveUnityAvatarAsset,
                animationClipResolution,
                animatorControllerResolution,
                importedAnimationClips,
                requestAvatar,
                unityBakeOutput,
                frameRate,
                runUnityBake,
                clipFilterModeOverride,
                allowGeneratedControllerDiagnostic,
                unityBakeWorkerQueue,
                sampleRecoverableSkippedLayersDiagnostic);
        }

        public static void GenerateBatchFromLibrary(
            string libraryRoot,
            string modelSelector,
            string animationSelector,
            string outputDirectory,
            string sourceRootOverride,
            string unityProject,
            string unityEditor,
            string unityModelPrefab,
            string unityAnimationClip,
            string unityAnimatorController,
            string unityAvatarAsset,
            int frameRate,
            bool runUnityBake,
            string clipFilterModeOverride = null,
            string unityBakeWorkerQueue = null,
            string bakedFbxOutput = null,
            string blender = null,
            int limit = 10,
            string indexPath = null,
            bool force = false,
            bool probeMuscles = false,
            bool enableIkGoalDriver = false,
            bool rebuildEditorCurveClip = false,
            bool sampleRecoverableSkippedLayersDiagnostic = false
        )
        {
            if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
            {
                Logger.Error($"Library root not found: {libraryRoot}");
                return;
            }

            var dbPath = string.IsNullOrWhiteSpace(indexPath)
                ? Path.Combine(libraryRoot, "library_index.db")
                : indexPath;
            if (!File.Exists(dbPath))
            {
                Logger.Error($"library_index.db not found: {dbPath}. Rebuild the Library export or run --build_sqlite_index.");
                return;
            }

            var output = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(libraryRoot, "UnityBakedAnimationPreviews")
                : outputDirectory;
            Directory.CreateDirectory(output);
            if (limit == 0)
            {
                TryCompactBakeCache(dbPath, libraryRoot);
                TryWriteBakeCacheSummary(dbPath, output, libraryRoot, fullScan: true, unityProject);
                WriteSummaryOnlyBakeBatchReport(dbPath, libraryRoot, output, runUnityBake, force);
                Logger.Info("Unity bake batch limit is 0; wrote reports only and skipped request generation.");
                return;
            }

            limit = Math.Max(1, limit);
            if (!ValidateSuppliedUnityAvatarAssetScope(modelSelector, unityAvatarAsset))
            {
                return;
            }
            var importedAvatarAssets = DiscoverImportedAvatarAssets(unityProject, libraryRoot);
            var importedAnimationClips = DiscoverImportedAnimationClips(unityProject, libraryRoot);
            var importedAnimatorControllers = DiscoverImportedAnimatorControllers(unityProject, libraryRoot);

            var selections = SelectExplicitBakeCandidatesFromLibraryDb(
                    dbPath,
                    modelSelector,
                    animationSelector,
                    limit,
                    skipBakedCache: !force,
                    allowSuppliedAvatarAsset: !string.IsNullOrWhiteSpace(unityAvatarAsset),
                    importedAvatarAssets)
                .ToArray();
            if (selections.Length == 0)
            {
                if (!force)
                {
                    var cachedSelections = SelectExplicitBakeCandidatesFromLibraryDb(
                            dbPath,
                            modelSelector,
                            animationSelector,
                            limit,
                            skipBakedCache: false,
                            allowSuppliedAvatarAsset: !string.IsNullOrWhiteSpace(unityAvatarAsset),
                            importedAvatarAssets)
                        .Take(limit)
                        .ToArray();
                    if (cachedSelections.Length > 0)
                    {
                        WriteNoOpBakeBatchReport(dbPath, libraryRoot, output, runUnityBake, cachedSelections.Length);
                        TryCompactBakeCache(dbPath, libraryRoot);
                        TryWriteBakeCacheSummary(dbPath, output, libraryRoot, fullScan: false, unityProject);
                        Logger.Info($"Unity bake batch has no pending candidates because {cachedSelections.Length} matching candidate(s) in this batch window are already processed by trusted bake, static_pose, needs_review, or needs_animator_controller_context. Use --preview_validation_force to rebuild them.");
                        return;
                    }
                }

                if (string.IsNullOrWhiteSpace(unityAvatarAsset))
                {
                    var missingAvatarSelections = SelectExplicitBakeCandidatesFromLibraryDb(
                            dbPath,
                            modelSelector,
                            animationSelector,
                            limit,
                            skipBakedCache: false,
                            allowSuppliedAvatarAsset: true,
                            importedAvatarAssets)
                        .Take(limit)
                        .ToArray();
                    if (missingAvatarSelections.Length > 0)
                    {
                        WriteNoOpMissingAvatarOracleBatchReport(dbPath, libraryRoot, output, runUnityBake, missingAvatarSelections);
                        TryCompactBakeCache(dbPath, libraryRoot);
                        TryWriteBakeCacheSummary(dbPath, output, libraryRoot, fullScan: false, unityProject);
                        Logger.Info($"Unity bake batch has {missingAvatarSelections.Length} explicit Humanoid/Muscle candidate(s), but none has a production Avatar oracle. Recover/import original Unity Avatar assets or complete HumanDescription metadata before baking.");
                        return;
                    }
                }

                var blockedCandidate = TryDescribeBlockedExplicitCandidate(dbPath, modelSelector, animationSelector);
                if (!string.IsNullOrWhiteSpace(blockedCandidate))
                {
                    Logger.Error(blockedCandidate);
                    return;
                }

                Logger.Error("No explicit Humanoid/Muscle model-animation candidate matched the Unity bake batch selectors.");
                return;
            }
            if (!ValidateSuppliedUnityAvatarAssetSelections(unityAvatarAsset, selections))
            {
                return;
            }
            if (!ValidateSuppliedUnityAnimationClipSelections(unityAnimationClip, selections))
            {
                return;
            }

            var items = new JArray();
            var requestsWritten = 0;
            var bakedCompleted = 0;
            foreach (var selection in selections)
            {
                ResolveSelectionLibraryPaths(selection, libraryRoot);
                var modelName = (string)selection.Model["model"]?["name"];
                var animationName = (string)selection.Animation["name"];
                var itemOutput = Path.Combine(output, $"{SafeName(modelName)}__{SafeName(animationName)}");
                var itemFbx = BuildBatchFbxPath(bakedFbxOutput, output, modelName, animationName);
                var avatarResolution = ResolveUnityAvatarAssetForSelection(selection, unityAvatarAsset, importedAvatarAssets);
                var effectiveUnityAvatarAsset = avatarResolution.AssetPath;
                var animationClipResolution = ResolveUnityAnimationClipForSelection(selection, unityAnimationClip, importedAnimationClips);
                var effectiveUnityAnimationClip = animationClipResolution.AssetPath;
                var animatorControllerResolution = ResolveUnityAnimatorControllerForSelection(selection, unityAnimatorController, importedAnimatorControllers);
                var effectiveUnityAnimatorController = animatorControllerResolution.AssetPath;

                var requestPath = GenerateSelection(
                    dbPath,
                    selection,
                    itemOutput,
                    sourceRootOverride,
                    unityProject,
                    unityEditor,
                    unityModelPrefab,
                    effectiveUnityAnimationClip,
                    effectiveUnityAnimatorController,
                    effectiveUnityAvatarAsset,
                    null,
                    frameRate,
                    probeMuscles,
                    enableIkGoalDriver,
                    rebuildEditorCurveClip,
                    ignoreImportedAvatar: false,
                    clipFilterModeOverride,
                    runUnityBake,
                    unityBakeWorkerQueue,
                    bakedGltfOutput: null,
                    bakedFbxOutput: itemFbx,
                    blender,
                    importedAnimationClips,
                    sampleRecoverableSkippedLayersDiagnostic: sampleRecoverableSkippedLayersDiagnostic);

                var resultPath = string.IsNullOrWhiteSpace(requestPath)
                    ? null
                    : Path.Combine(Path.GetDirectoryName(requestPath) ?? itemOutput, "unity_bake_result.json");
                var bakeApply = ReadBatchBakeApplyInfo(itemOutput);
                var bakedGltf = bakeApply.BakedGltfPath;

                var requestWritten = !string.IsNullOrWhiteSpace(requestPath) && File.Exists(requestPath);
                // Unity 烘焙写回可能带 warning（例如非关键 track/报告提示），
                // 只要已经产出 baked glTF，就应该算作批任务完成，细节仍保留在 applyStatus。
                var bakedOk = (string.Equals(bakeApply.Status, "ok", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(bakeApply.Status, "warning", StringComparison.OrdinalIgnoreCase))
                    && !string.IsNullOrWhiteSpace(bakedGltf)
                    && File.Exists(bakedGltf);
                var staticPose = string.Equals(bakeApply.Status, "static_pose", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(bakedGltf)
                    && File.Exists(bakedGltf);
                var needsReview = string.Equals(bakeApply.Status, "needs_review", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(bakedGltf)
                    && File.Exists(bakedGltf);
                var needsAnimatorControllerContext =
                    string.Equals(bakeApply.Status, "needs_animator_controller_context", StringComparison.OrdinalIgnoreCase)
                    || NeedsAnimatorControllerContext(bakeApply.Message);
                if (requestWritten)
                {
                    requestsWritten++;
                }
                if (bakedOk)
                {
                    bakedCompleted++;
                }

                items.Add(new JObject
                {
                    ["status"] = runUnityBake
                        ? (bakedOk ? "baked" : staticPose ? "static_pose" : needsReview ? "needs_review" : needsAnimatorControllerContext ? "needs_animator_controller_context" : "failed")
                        : (requestWritten ? "request_written" : "failed"),
                    ["model"] = modelName,
                    ["animation"] = animationName,
                    ["requestedAnimation"] = (string)selection.Animation["animatorControllerRequestedClip"]?["name"] ?? animationName,
                    ["modelOutput"] = (string)selection.Model["model"]?["output"],
                    ["animationOutput"] = (string)selection.Animation["output"],
                    ["requestedAnimationOutput"] = (string)selection.Animation["animatorControllerRequestedClip"]?["output"],
                    ["actualBakeAnimationOutput"] = (string)selection.Animation["output"],
                    ["relationSource"] = (string)selection.Animation["relationSource"],
                    ["confidence"] = (string)selection.Animation["confidence"],
                    ["unityAvatarAsset"] = effectiveUnityAvatarAsset,
                    ["avatarSource"] = avatarResolution.Source,
                    ["avatarMatchKey"] = avatarResolution.MatchKey,
                    ["unityAnimationClip"] = effectiveUnityAnimationClip,
                    ["animationClipSource"] = animationClipResolution.Source,
                    ["animationClipMatchKey"] = animationClipResolution.MatchKey,
                    ["request"] = requestPath,
                    ["result"] = resultPath,
                    ["bakedGltf"] = bakedGltf,
                    ["bakedFbx"] = itemFbx,
                    ["applyStatus"] = bakeApply.Status,
                    ["animationSolve"] = bakeApply.AnimationSolve?.DeepClone(),
                    ["solvePath"] = bakeApply.SolvePath,
                    ["productionStatus"] = bakeApply.ProductionStatus,
                    ["requiresVisualValidation"] = bakeApply.RequiresVisualValidation,
                    ["writesReusableGltfTrsCandidate"] = bakeApply.WritesReusableGltfTrsCandidate,
                    ["reusableCandidateBlockedReasons"] = bakeApply.ReusableCandidateBlockedReasons?.DeepClone(),
                    ["ikGoalDriverDiagnosticEnabled"] = bakeApply.IkGoalDriverDiagnosticEnabled,
                    ["ikGoalDriverCallCount"] = bakeApply.IkGoalDriverCallCount,
                    ["ikGoalDriverAppliedGoalCount"] = bakeApply.IkGoalDriverAppliedGoalCount,
                    ["sampleRecoverableSkippedLayersDiagnostic"] = bakeApply.SampleRecoverableSkippedLayersDiagnostic,
                    ["animatorControllerDiagnosticSampledSkippedLayerCount"] = bakeApply.AnimatorControllerDiagnosticSampledSkippedLayerCount,
                    ["animatorControllerDiagnosticSampledSkippedLayerNames"] = bakeApply.AnimatorControllerDiagnosticSampledSkippedLayerNames?.DeepClone(),
                    ["animatorControllerRecoverableSkippedLayerSummary"] = bakeApply.AnimatorControllerRecoverableSkippedLayerSummary?.DeepClone(),
                    ["message"] = bakeApply.Message,
                    ["frameVaryingTracks"] = bakeApply.FrameVaryingTracks,
                    ["frameVaryingChannels"] = bakeApply.FrameVaryingChannels,
                    ["firstPoseChangedTracks"] = bakeApply.FirstPoseChangedTracks,
                    ["coreBodyFirstPoseChangedTracks"] = bakeApply.CoreBodyFirstPoseChangedTracks,
                });
            }

            var report = new JObject
            {
                ["generatedAt"] = DateTime.UtcNow.ToString("O"),
                ["libraryRoot"] = libraryRoot,
                ["libraryIndex"] = dbPath,
                ["mode"] = "UnityBakeToGltfProduction",
                ["rule"] = "Only relation_source=explicit Humanoid/Muscle candidates are selected. Unity samples Animator/Avatar and AnimeStudio writes sampled TRS back into glTF.",
                ["requested"] = selections.Length,
                ["requestsWritten"] = requestsWritten,
                ["bakedCompleted"] = bakedCompleted,
                ["skipBakedCache"] = !force,
                ["runUnityBake"] = runUnityBake,
                ["avatarAssetCounts"] = CountReportItemsByString(items, "unityAvatarAsset"),
                ["avatarSourceCounts"] = CountReportItemsByString(items, "avatarSource"),
                ["avatarMatchKeyCounts"] = CountReportItemsByString(items, "avatarMatchKey"),
                ["animationClipAssetCounts"] = CountReportItemsByString(items, "unityAnimationClip"),
                ["animationClipSourceCounts"] = CountReportItemsByString(items, "animationClipSource"),
                ["animationClipMatchKeyCounts"] = CountReportItemsByString(items, "animationClipMatchKey"),
                ["items"] = items,
            };
            var reportPath = Path.Combine(output, "unity_bake_batch_report.json");
            File.WriteAllText(reportPath, JsonConvert.SerializeObject(report, Formatting.Indented));
            Logger.Info($"Unity bake batch report: {reportPath}");
            if (runUnityBake)
            {
                TryWriteBakeCache(dbPath, items, libraryRoot);
                TryCompactBakeCache(dbPath, libraryRoot);
            }
            else
            {
                Logger.Info("Unity bake dry run wrote requests only; animation_bake_cache was not updated.");
            }
            TryWriteBakeCacheSummary(dbPath, output, libraryRoot, fullScan: false, unityProject);
        }

        private static void WriteNoOpBakeBatchReport(
            string dbPath,
            string libraryRoot,
            string output,
            bool runUnityBake,
            int skippedBakedCandidates)
        {
            var report = new JObject
            {
                ["generatedAt"] = DateTime.UtcNow.ToString("O"),
                ["libraryRoot"] = libraryRoot,
                ["libraryIndex"] = dbPath,
                ["mode"] = "UnityBakeToGltfProduction",
                ["status"] = "noop_all_cached",
                ["rule"] = "Only relation_source=explicit Humanoid/Muscle candidates are selected. Matching candidates in this batch window were already processed by trusted bake, static_pose, needs_review, or needs_animator_controller_context and skipped.",
                ["requested"] = 0,
                ["requestsWritten"] = 0,
                ["bakedCompleted"] = 0,
                ["skippedBakedCache"] = true,
                ["skippedBakedCandidates"] = skippedBakedCandidates,
                ["runUnityBake"] = runUnityBake,
                ["items"] = new JArray(),
            };
            var reportPath = Path.Combine(output, "unity_bake_batch_report.json");
            File.WriteAllText(reportPath, JsonConvert.SerializeObject(report, Formatting.Indented));
            Logger.Info($"Unity bake batch report: {reportPath}");
        }

        private static void WriteNoOpMissingAvatarOracleBatchReport(
            string dbPath,
            string libraryRoot,
            string output,
            bool runUnityBake,
            IReadOnlyList<PreviewSelection> missingAvatarSelections)
        {
            var items = new JArray();
            foreach (var selection in missingAvatarSelections ?? Array.Empty<PreviewSelection>())
            {
                var model = selection?.Model?["model"] as JObject;
                var animation = selection?.Animation as JObject;
                var reason = (string)animation?["candidate"]?["productionUnityBakeBlockedReason"]
                    ?? (string)animation?["productionUnityBakeBlockedReason"]
                    ?? "missing_production_avatar_oracle";
                items.Add(new JObject
                {
                    ["status"] = "skipped_missing_avatar_oracle",
                    ["model"] = (string)model?["name"],
                    ["animation"] = (string)animation?["name"],
                    ["modelOutput"] = (string)model?["output"],
                    ["animationOutput"] = (string)animation?["output"],
                    ["relationSource"] = (string)animation?["relationSource"],
                    ["confidence"] = (string)animation?["confidence"],
                    ["avatarSource"] = "missing_production_avatar_oracle",
                    ["avatarMatchKey"] = "",
                    ["message"] = "缺少可信生产 Avatar oracle，未生成 Unity bake request。需要原始 Animator.avatar、完整 HumanDescription，或导入的原始 Unity Avatar asset。原因: " + reason,
                });
            }

            var report = new JObject
            {
                ["generatedAt"] = DateTime.UtcNow.ToString("O"),
                ["libraryRoot"] = libraryRoot,
                ["libraryIndex"] = dbPath,
                ["mode"] = "UnityBakeToGltfProduction",
                ["status"] = "noop_missing_avatar_oracle",
                ["rule"] = "Only relation_source=explicit Humanoid/Muscle candidates with a production Avatar oracle may enter Unity bake. Matching explicit candidates were skipped because no original Animator.avatar, complete HumanDescription, or imported Unity Avatar asset was available.",
                ["requested"] = 0,
                ["requestsWritten"] = 0,
                ["bakedCompleted"] = 0,
                ["skippedMissingAvatarOracle"] = items.Count,
                ["runUnityBake"] = runUnityBake,
                ["items"] = items,
            };
            var reportPath = Path.Combine(output, "unity_bake_batch_report.json");
            File.WriteAllText(reportPath, JsonConvert.SerializeObject(report, Formatting.Indented));
            Logger.Info($"Unity bake batch report: {reportPath}");
        }

        private static void WriteSummaryOnlyBakeBatchReport(
            string dbPath,
            string libraryRoot,
            string output,
            bool runUnityBake,
            bool force)
        {
            var readiness = BuildSummaryOnlyBakeReadiness(libraryRoot, output);
            var report = new JObject
            {
                ["generatedAt"] = DateTime.UtcNow.ToString("O"),
                ["libraryRoot"] = libraryRoot,
                ["libraryIndex"] = dbPath,
                ["mode"] = "UnityBakeToGltfProduction",
                ["status"] = "summary_only",
                ["rule"] = "Only relation_source=explicit Humanoid/Muscle candidates are selected. Limit 0 refreshes bake cache reports without generating Unity bake requests.",
                ["requested"] = 0,
                ["requestsWritten"] = 0,
                ["bakedCompleted"] = 0,
                ["skipBakedCache"] = !force,
                ["runUnityBake"] = runUnityBake,
                ["explicitUnityBakeCandidates"] = (long?)readiness["explicitUnityBakeCandidates"] ?? 0,
                ["bakeReadyExplicitUnityBakeCandidates"] = (long?)readiness["bakeReadyExplicitUnityBakeCandidates"] ?? 0,
                ["importedAvatarAssetBakeReadyExplicitUnityBakeCandidates"] = (long?)readiness["importedAvatarAssetBakeReadyExplicitUnityBakeCandidates"] ?? 0,
                ["effectiveBakeReadyExplicitUnityBakeCandidates"] = (long?)readiness["effectiveBakeReadyExplicitUnityBakeCandidates"] ?? 0,
                ["avatarSourceCounts"] = readiness["avatarSourceCounts"] ?? new JObject(),
                ["summaryError"] = (string)readiness["summaryError"],
                ["items"] = new JArray(),
            };
            var reportPath = Path.Combine(output, "unity_bake_batch_report.json");
            File.WriteAllText(reportPath, JsonConvert.SerializeObject(report, Formatting.Indented));
            Logger.Info($"Unity bake batch report: {reportPath}");
        }

        private static JObject BuildSummaryOnlyBakeReadiness(string libraryRoot, string output)
        {
            var summary = new JObject
            {
                ["explicitUnityBakeCandidates"] = 0,
                ["bakeReadyExplicitUnityBakeCandidates"] = 0,
                ["importedAvatarAssetBakeReadyExplicitUnityBakeCandidates"] = 0,
                ["effectiveBakeReadyExplicitUnityBakeCandidates"] = 0,
                ["avatarSourceCounts"] = new JObject(),
            };
            try
            {
                var summaryPath = ResolveFreshBakeSummaryPath(libraryRoot, output);
                if (string.IsNullOrWhiteSpace(summaryPath))
                {
                    summary["summaryError"] = "animation_bake_cache_summary.json was not found after summary refresh.";
                    return summary;
                }

                var root = JObject.Parse(File.ReadAllText(summaryPath));
                var explicitUnityBakeCandidates = (long?)root["explicitUnityBakeCandidates"] ?? 0;
                var bakeReadyExplicitUnityBakeCandidates = (long?)root["bakeReadyExplicitUnityBakeCandidates"] ?? 0;
                var importedAvatarAssetBakeReadyExplicitUnityBakeCandidates = (long?)root["importedAvatarAssetBakeReadyExplicitUnityBakeCandidates"] ?? 0;
                var effectiveBakeReadyExplicitUnityBakeCandidates = (long?)root["effectiveBakeReadyExplicitUnityBakeCandidates"] ?? 0;
                summary["explicitUnityBakeCandidates"] = explicitUnityBakeCandidates;
                summary["bakeReadyExplicitUnityBakeCandidates"] = bakeReadyExplicitUnityBakeCandidates;
                summary["importedAvatarAssetBakeReadyExplicitUnityBakeCandidates"] = importedAvatarAssetBakeReadyExplicitUnityBakeCandidates;
                summary["effectiveBakeReadyExplicitUnityBakeCandidates"] = effectiveBakeReadyExplicitUnityBakeCandidates;
                summary["avatarSourceCounts"] = new JObject
                {
                    ["model_human_description"] = bakeReadyExplicitUnityBakeCandidates,
                    ["imported_unity_avatar_asset"] = importedAvatarAssetBakeReadyExplicitUnityBakeCandidates,
                };
            }
            catch (Exception ex)
            {
                summary["summaryError"] = ex.Message;
            }

            return summary;
        }

        private static string ResolveFreshBakeSummaryPath(string libraryRoot, string output)
        {
            var candidates = new[]
            {
                Path.Combine(output ?? "", "animation_bake_cache_summary.json"),
                Path.Combine(libraryRoot ?? "", "animation_bake_cache_summary.json"),
            };
            return candidates
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .FirstOrDefault();
        }

        private static JObject CountReportItemsByString(JArray items, string propertyName)
        {
            var counts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var value = ((string)item[propertyName])?.Trim();
                if (string.IsNullOrWhiteSpace(value))
                {
                    value = "(none)";
                }

                counts[value] = counts.TryGetValue(value, out var count) ? count + 1 : 1;
            }

            var result = new JObject();
            foreach (var pair in counts)
            {
                result[pair.Key] = pair.Value;
            }

            return result;
        }

        private static string GenerateAcceleratedSelection(
            string indexPath,
            string libraryRoot,
            PreviewSelection selection,
            string outputDirectory,
            string unityProject,
            string unityEditor,
            string unityAvatarAsset,
            AnimationClipAssetResolution unityAnimationClip,
            AnimatorControllerAssetResolution unityAnimatorController,
            IReadOnlyDictionary<string, string> importedAnimationClips,
            JObject requestAvatar,
            string unityBakeOutput,
            int frameRate,
            bool runUnityBake,
            string clipFilterModeOverride,
            bool allowGeneratedControllerDiagnostic,
            string unityBakeWorkerQueue,
            bool sampleRecoverableSkippedLayersDiagnostic)
        {
            var model = selection.Model["model"] as JObject;
            var animation = selection.Animation;
            var modelName = (string)model?["name"];
            var animationName = (string)animation?["name"];
            if (string.IsNullOrWhiteSpace(modelName) || string.IsNullOrWhiteSpace(animationName))
            {
                Logger.Error("Selected UnityBakeAccelerated entry is missing model or animation name.");
                return null;
            }
            if (!IsExplicitBakeRelation(animation))
            {
                Logger.Error("Selected UnityBakeAccelerated candidate is not a Unity explicit model-animation relation.");
                return null;
            }

            var modelGltf = ResolveSourcePath((string)model?["output"], null);
            var animationAssetPath = ResolveAnimationAssetPath(animation);
            if (string.IsNullOrWhiteSpace(modelGltf) || !File.Exists(modelGltf))
            {
                Logger.Error($"Selected model glTF not found: {modelGltf}");
                return null;
            }
            if (string.IsNullOrWhiteSpace(animationAssetPath) || !File.Exists(animationAssetPath))
            {
                Logger.Error($"Selected animation sidecar not found: {animationAssetPath}. Re-export the clip with --export_full_decoded_animation_curves.");
                return null;
            }

            var output = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(
                    libraryRoot,
                    "UnityBakeAcceleratedRequests",
                    $"{SafeName(modelName)}__{SafeName(animationName)}")
                : outputDirectory;
            Directory.CreateDirectory(output);

            var sidecar = JObject.Parse(File.ReadAllText(animationAssetPath));
            var curves = ReadDecodedFloatCurves(sidecar);
            var effectiveFrameRate = frameRate > 0
                ? frameRate
                : Math.Max(1, (int)Math.Round((double?)sidecar["sampleRate"] ?? 60.0));
            var unsupportedHumanPoseCurves = BuildUnsupportedAcceleratedHumanPoseCurveSummary(curves);
            var requiresFullClipSemantics = (bool?)unsupportedHumanPoseCurves["requiresFullHumanoidClipBake"] == true;
            if (curves.Count == 0 || requiresFullClipSemantics)
            {
                var fallbackReason = curves.Count == 0
                    ? "missing_decoded_float_curves"
                    : "unsupported_limb_goal_or_tdof_curves";
                var fallbackRequest = TryGeneratePlayableGraphFallbackFromAcceleratedSelection(
                    indexPath,
                    libraryRoot,
                    selection,
                    output,
                    unityProject,
                    unityEditor,
                    unityAvatarAsset,
                    unityAnimationClip,
                    unityAnimatorController,
                    importedAnimationClips,
                    unityBakeOutput,
                    effectiveFrameRate,
                    runUnityBake,
                    unityBakeWorkerQueue,
                    clipFilterModeOverride,
                    allowGeneratedControllerDiagnostic,
                    fallbackReason,
                    unsupportedHumanPoseCurves,
                    sampleRecoverableSkippedLayersDiagnostic);
                if (!string.IsNullOrWhiteSpace(fallbackRequest))
                {
                    return fallbackRequest;
                }

                Logger.Error(curves.Count == 0
                    ? "Animation sidecar has no decoded float curves for UnityBakeAccelerated, and UnityOracle/PlayableGraph fallback was unavailable or blocked by safety gates."
                    : "Animation sidecar contains limb goal/TDOF curves that UnityBakeAccelerated fast path does not consume, and UnityOracle/PlayableGraph fallback was unavailable or blocked by safety gates.");
                return null;
            }

            var samples = BuildAcceleratedPoseSamples(curves, sidecar, effectiveFrameRate);
            if (samples.Count == 0)
            {
                Logger.Error("Unable to build UnityBakeAccelerated pose samples from decoded float curves.");
                return null;
            }

            var jointPaths = BuildGltfJointPaths(modelGltf, modelName);
            if (jointPaths.Length == 0)
            {
                Logger.Error("Unable to build UnityBakeAccelerated joint paths from model glTF.");
                return null;
            }
            var unityJointPaths = BuildUnityBakeJointPaths(jointPaths, requestAvatar, unityAvatarAsset);

            var requestPath = Path.Combine(output, "unity_bake_accelerated_request.json");
            var resultPath = string.IsNullOrWhiteSpace(unityBakeOutput)
                ? Path.Combine(output, "unity_bake_accelerated_result.json")
                : unityBakeOutput;

            var request = new JObject
            {
                ["version"] = 1,
                ["mode"] = "UnityBakeAccelerated",
                ["avatarAsset"] = unityAvatarAsset,
                // 没有可导入的 UnityEngine.Avatar asset 时，仍允许使用完整的
                // HumanDescription.humanBones + skeletonBones 让 Unity 6 临时 BuildHumanAvatar。
                // 这不是按名字猜骨骼；数据来自模型索引里的 Unity Avatar/HumanDescription。
                ["avatar"] = requestAvatar,
                ["avatarKey"] = BuildAcceleratedAvatarKey(model, animation),
                ["poseSolveMethod"] = "HumanPoseHandler.SetInternalHumanPose",
                ["outputJson"] = resultPath,
                ["jointPaths"] = new JArray(unityJointPaths),
                ["clips"] = new JArray
                {
                    new JObject
                    {
                        ["clipKey"] = BuildAcceleratedClipKey(animation),
                        ["clipName"] = animationName,
                        ["frameRate"] = effectiveFrameRate,
                        ["samples"] = samples,
                    }
                },
                ["animeStudioRequest"] = new JObject
                {
                    ["generatedAt"] = DateTime.UtcNow.ToString("O"),
                    ["rule"] = "UnityBakeAccelerated 只把已解码 Humanoid/Muscle 曲线求解成目标骨架 TRS；最终仍需写成普通 glTF 动画。",
                    ["libraryRoot"] = libraryRoot,
                    ["libraryIndex"] = indexPath,
                    ["model"] = new JObject
                    {
                        ["name"] = modelName,
                        ["gltf"] = modelGltf,
                        ["pathId"] = model?["pathId"],
                    },
                    ["animation"] = new JObject
                    {
                        ["name"] = animationName,
                        ["animationAsset"] = animationAssetPath,
                        ["pathId"] = animation?["pathId"],
                        ["relation"] = animation?["relation"],
                        ["relationSource"] = animation?["relationSource"],
                        ["confidence"] = animation?["confidence"],
                    },
                    ["sampling"] = new JObject
                    {
                        ["source"] = "animation_asset.decoded.floats",
                        ["bodyPosition"] = "MotionT.xyz fallback RootT.xyz fallback zero",
                        ["bodyRotation"] = "MotionQ.xyzw fallback RootQ.xyzw fallback identity",
                        ["muscles"] = "Humanoid muscle/finger curves only. Limb IK goal curves and TDOF curves are recorded in the report but not consumed by HumanPoseHandler.SetHumanPose/SetInternalHumanPose.",
                        ["unsupportedHumanPoseCurveSummary"] = unsupportedHumanPoseCurves,
                        ["sampleCount"] = samples.Count,
                        ["frameRate"] = effectiveFrameRate,
                        ["gltfJointPaths"] = new JArray(jointPaths),
                        ["unityJointPathPrefix"] = GetHumanDescriptionSkeletonRootName(requestAvatar),
                    },
                },
            };

            File.WriteAllText(requestPath, request.ToString(Formatting.Indented));
            Logger.Info($"UnityBakeAccelerated request: {requestPath}");

            if (runUnityBake)
            {
                var ok = string.IsNullOrWhiteSpace(unityBakeWorkerQueue)
                    ? UnityBakeAcceleratedRunner.RunOnce(requestPath, unityProject, unityEditor, null)
                    : UnityBakeAcceleratedRunner.Queue(requestPath, unityProject, unityEditor, unityBakeWorkerQueue, null);
                if (!ok)
                {
                    Logger.Error("UnityBakeAccelerated run failed.");
                }
            }

            return requestPath;
        }

        private static string TryGeneratePlayableGraphFallbackFromAcceleratedSelection(
            string indexPath,
            string libraryRoot,
            PreviewSelection selection,
            string output,
            string unityProject,
            string unityEditor,
            string unityAvatarAsset,
            AnimationClipAssetResolution unityAnimationClip,
            AnimatorControllerAssetResolution unityAnimatorController,
            IReadOnlyDictionary<string, string> importedAnimationClips,
            string unityBakeOutput,
            int frameRate,
            bool runUnityBake,
            string unityBakeWorkerQueue,
            string clipFilterModeOverride,
            bool allowGeneratedControllerDiagnostic,
            string fallbackReason,
            JObject unsupportedHumanPoseCurves,
            bool sampleRecoverableSkippedLayersDiagnostic)
        {
            if (string.IsNullOrWhiteSpace(unityAnimationClip.AssetPath))
            {
                WriteAcceleratedFallbackReport(output, selection, fallbackReason, null, unityAnimationClip, null, unsupportedHumanPoseCurves, "missing_imported_animation_clip");
                return null;
            }

            var selectedAnimation = selection?.Animation as JObject;
            var requiresControllerContext = HasAnimatorControllerContext(selectedAnimation);
            if (requiresControllerContext
                && string.IsNullOrWhiteSpace(unityAnimatorController?.AssetPath)
                && !allowGeneratedControllerDiagnostic)
            {
                var blockReason =
                    "Selected Humanoid clip has deterministic AnimatorController context, but no exact ImportedAnimatorController/original RuntimeAnimatorController asset was resolved. "
                    + "Default UnityBakeAccelerated fallback refuses to generate a temporary single-state controller because it can produce semantically wrong poses, such as raised or twisted hands on an idle clip. "
                    + "Recover/import the original RuntimeAnimatorController or pass --unity_animator_controller. Use --unity_bake_allow_generated_controller_diagnostic only for clearly marked diagnostic output.";
                WriteAcceleratedFallbackReport(
                    output,
                    selection,
                    fallbackReason,
                    null,
                    unityAnimationClip,
                    unityAnimatorController,
                    unsupportedHumanPoseCurves,
                    "blocked_generated_controller_context",
                    probeUnsupportedHumanoidCurves: false,
                    enableIkGoalDriver: false,
                    allowGeneratedControllerDiagnostic: allowGeneratedControllerDiagnostic,
                    sampleRecoverableSkippedLayersDiagnostic: sampleRecoverableSkippedLayersDiagnostic,
                    blockReason: blockReason);
                Logger.Error(blockReason);
                return null;
            }

            Logger.Warning(
                "UnityBakeAccelerated fast path cannot safely solve this Humanoid clip yet; generating UnityOracle/PlayableGraph fallback request. " +
                $"reason={fallbackReason}; animationClip={unityAnimationClip.AssetPath}");
            var missingDecodedFloatCurves = string.Equals(fallbackReason, "missing_decoded_float_curves", StringComparison.OrdinalIgnoreCase);
            var shouldProbeUnsupportedHumanoidCurves = missingDecodedFloatCurves || RequiresFullHumanoidClipBake(unsupportedHumanPoseCurves);
            var shouldEnableIkGoalDriver = HasHumanoidLimbGoalCurves(unsupportedHumanPoseCurves);
            var requestPath = GenerateSelection(
                indexPath,
                selection,
                output,
                sourceRootOverride: null,
                unityProject,
                unityEditor,
                unityModelPrefab: null,
                unityAnimationClip: unityAnimationClip.AssetPath,
                unityAnimatorController: unityAnimatorController.AssetPath,
                unityAvatarAsset,
                unityBakeOutput,
                frameRate,
                // fast path 已经证明这些曲线不能被 HumanPoseHandler 简单消费，或 sidecar
                // 还没有解出 ACL scalar float curves。fallback 的职责是尽量接近 Unity 原生
                // AnimationClip/PlayableGraph 语义，并补齐 editor curve 分类诊断；只有 fast path
                // 已明确发现手脚 goal 时，才自动启用 IK pass 诊断。
                probeMuscles: shouldProbeUnsupportedHumanoidCurves,
                enableIkGoalDriver: shouldEnableIkGoalDriver,
                rebuildEditorCurveClip: false,
                ignoreImportedAvatar: false,
                clipFilterModeOverride,
                runUnityBake: runUnityBake,
                unityBakeWorkerQueue: unityBakeWorkerQueue,
                bakedGltfOutput: null,
                bakedFbxOutput: null,
                blender: null,
                importedAnimationClips: importedAnimationClips,
                sampleRecoverableSkippedLayersDiagnostic: sampleRecoverableSkippedLayersDiagnostic);
            WriteAcceleratedFallbackReport(
                output,
                selection,
                fallbackReason,
                requestPath,
                unityAnimationClip,
                unityAnimatorController,
                unsupportedHumanPoseCurves,
                string.IsNullOrWhiteSpace(requestPath) ? "failed" : "ok",
                shouldProbeUnsupportedHumanoidCurves,
                shouldEnableIkGoalDriver,
                allowGeneratedControllerDiagnostic,
                sampleRecoverableSkippedLayersDiagnostic);
            return requestPath;
        }

        private static bool RequiresFullHumanoidClipBake(JObject unsupportedHumanPoseCurves)
        {
            return (bool?)unsupportedHumanPoseCurves?["requiresFullHumanoidClipBake"] == true
                || ((int?)unsupportedHumanPoseCurves?["curveCount"] ?? 0) > 0;
        }

        private static bool HasHumanoidLimbGoalCurves(JObject unsupportedHumanPoseCurves)
        {
            return ((int?)unsupportedHumanPoseCurves?["limbGoalCurveCount"] ?? 0) > 0;
        }

        private static bool HasDynamicHumanoidLimbGoalCurves(JObject unsupportedHumanPoseCurves)
        {
            return ((int?)unsupportedHumanPoseCurves?["dynamicLimbGoalCurveCount"] ?? 0) > 0;
        }

        private static bool ShouldAutoEnableIkGoalDriver(JObject animation, string unityAnimatorController, JObject unsupportedHumanPoseCurves)
        {
            if (animation == null
                || string.IsNullOrWhiteSpace(unityAnimatorController)
                || !HasAnimatorControllerContext(animation))
            {
                return false;
            }

            // 只有动态 Hand/Foot goal 才自动打开 IK 诊断。静态 goal 可能只是默认值，
            // 不能因为有字段名就增加 Unity 采样成本或改变诊断路径。
            return HasDynamicHumanoidLimbGoalCurves(unsupportedHumanPoseCurves);
        }

        private static void WriteAcceleratedFallbackReport(
            string output,
            PreviewSelection selection,
            string fallbackReason,
            string requestPath,
            AnimationClipAssetResolution unityAnimationClip,
            AnimatorControllerAssetResolution unityAnimatorController,
            JObject unsupportedHumanPoseCurves,
            string status,
            bool probeUnsupportedHumanoidCurves = false,
            bool enableIkGoalDriver = false,
            bool allowGeneratedControllerDiagnostic = false,
            bool sampleRecoverableSkippedLayersDiagnostic = false,
            string blockReason = null)
        {
            Directory.CreateDirectory(output);
            var model = selection?.Model?["model"] as JObject;
            var animation = selection?.Animation as JObject;
            var report = new JObject
            {
                ["status"] = status,
                ["mode"] = "UnityOraclePlayableGraphFallback",
                ["generatedAt"] = DateTime.UtcNow.ToString("O"),
                ["fallbackReason"] = fallbackReason,
                ["rule"] = "UnityBakeAccelerated fast path only consumes decoded body/muscle float curves. Missing decoded ACL scalar curves or limb goal/TDOF curves must use full Unity AnimationClip/PlayableGraph semantics until AnimeStudio has an equivalent direct solver. The fallback still writes ordinary glTF TRS through AnimeStudio and remains needs visual validation.",
                ["model"] = new JObject
                {
                    ["name"] = (string)model?["name"],
                    ["output"] = (string)model?["output"],
                    ["pathId"] = model?["pathId"],
                },
                ["animation"] = new JObject
                {
                    ["name"] = (string)animation?["name"],
                    ["output"] = (string)animation?["output"],
                    ["pathId"] = animation?["pathId"],
                    ["relation"] = (string)animation?["relation"],
                    ["relationSource"] = (string)animation?["relationSource"],
                    ["confidence"] = (string)animation?["confidence"],
                },
                ["unityAnimationClip"] = unityAnimationClip.AssetPath,
                ["unityAnimationClipSource"] = unityAnimationClip.Source,
                ["unityAnimationClipMatchKey"] = unityAnimationClip.MatchKey,
                ["unityAnimatorController"] = unityAnimatorController?.AssetPath,
                ["unityAnimatorControllerSource"] = unityAnimatorController?.Source,
                ["unityAnimatorControllerMatchKey"] = unityAnimatorController?.MatchKey,
                ["requestPath"] = requestPath,
                ["blockedReason"] = blockReason,
                ["unsupportedHumanPoseCurveSummary"] = unsupportedHumanPoseCurves ?? new JObject(),
                ["fallbackDiagnostics"] = new JObject
                {
                    ["probeUnsupportedHumanoidCurves"] = probeUnsupportedHumanoidCurves,
                    ["enableEditorCurveIkGoalDriver"] = enableIkGoalDriver,
                    ["allowGeneratedControllerDiagnostic"] = allowGeneratedControllerDiagnostic,
                    ["sampleRecoverableSkippedLayersDiagnostic"] = sampleRecoverableSkippedLayersDiagnostic,
                    ["requiresOriginalAnimatorController"] = string.Equals(status, "blocked_generated_controller_context", StringComparison.OrdinalIgnoreCase),
                    ["rule"] = "limb goal/TDOF 已知时自动打开诊断采样；缺 decoded float curves 时也会 probe editor curves 以补齐分类证据，但不会仅凭 probe 结果进入生产。",
                },
                ["productionReady"] = false,
            };
            File.WriteAllText(Path.Combine(output, "unity_bake_accelerated_fallback_report.json"), report.ToString(Formatting.Indented));
        }

        private static Dictionary<string, List<FloatCurveKey>> ReadDecodedFloatCurves(JObject sidecar)
        {
            var result = new Dictionary<string, List<FloatCurveKey>>(StringComparer.OrdinalIgnoreCase);
            foreach (var curve in sidecar?["decoded"]?["floats"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var attribute = ((string)curve["attribute"] ?? (string)curve["attributeName"])?.Trim();
                if (string.IsNullOrWhiteSpace(attribute))
                {
                    continue;
                }

                var keys = curve["keyframes"]?.OfType<JObject>()
                    .Select(x => new FloatCurveKey((float?)x["time"] ?? 0f, (float?)x["value"] ?? 0f))
                    .OrderBy(x => x.Time)
                    .ToList();
                if (keys == null || keys.Count == 0)
                {
                    continue;
                }

                result[attribute] = keys;
                var unityFingerName = ToUnityFingerMuscleName(attribute);
                if (!string.IsNullOrWhiteSpace(unityFingerName) && !result.ContainsKey(unityFingerName))
                {
                    result[unityFingerName] = keys;
                }
            }

            return result;
        }

        private static JObject TryBuildUnsupportedHumanPoseCurveSummaryFromSidecar(JObject animation)
        {
            var animationAssetPath = ResolveAnimationAssetPath(animation);
            if (string.IsNullOrWhiteSpace(animationAssetPath) || !File.Exists(animationAssetPath))
            {
                return null;
            }

            try
            {
                var sidecar = JObject.Parse(File.ReadAllText(animationAssetPath));
                var curves = ReadDecodedFloatCurves(sidecar);
                return curves.Count == 0
                    ? null
                    : BuildUnsupportedAcceleratedHumanPoseCurveSummary(curves);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Unable to inspect animation sidecar for Humanoid unsupported curve summary: {animationAssetPath}; {ex.Message}");
                return null;
            }
        }

        private static string ToUnityFingerMuscleName(string attribute)
        {
            if (string.IsNullOrWhiteSpace(attribute))
            {
                return null;
            }

            var parts = attribute.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
            {
                return null;
            }

            var side = parts[0].Equals("LeftHand", StringComparison.OrdinalIgnoreCase)
                ? "Left"
                : parts[0].Equals("RightHand", StringComparison.OrdinalIgnoreCase)
                    ? "Right"
                    : null;
            if (side == null)
            {
                return null;
            }

            return parts[2].Equals("Spread", StringComparison.OrdinalIgnoreCase)
                ? $"{side} {parts[1]} Spread"
                : $"{side} {parts[1]} {parts[2]}";
        }

        private static JArray BuildAcceleratedPoseSamples(
            Dictionary<string, List<FloatCurveKey>> curves,
            JObject sidecar,
            int frameRate)
        {
            var times = BuildAcceleratedSampleTimes(curves, sidecar, frameRate);
            var muscleNames = BuildUnityHumanTraitMuscleNames();
            var samples = new JArray();
            foreach (var time in times)
            {
                var muscles = new JArray();
                foreach (var muscleName in muscleNames)
                {
                    muscles.Add(SampleDecodedFloat(curves, muscleName, time, 0f));
                }

                var bodyPosition = SampleVector3(curves, "MotionT", time, "RootT");
                var bodyRotation = SampleQuaternion(curves, "MotionQ", time, "RootQ");
                samples.Add(new JObject
                {
                    ["time"] = time,
                    ["bodyPosition"] = new JObject
                    {
                        ["x"] = bodyPosition[0],
                        ["y"] = bodyPosition[1],
                        ["z"] = bodyPosition[2],
                    },
                    ["bodyRotation"] = new JObject
                    {
                        ["x"] = bodyRotation[0],
                        ["y"] = bodyRotation[1],
                        ["z"] = bodyRotation[2],
                        ["w"] = bodyRotation[3],
                    },
                    ["muscles"] = muscles,
                });
            }

            return samples;
        }

        private static float[] BuildAcceleratedSampleTimes(
            Dictionary<string, List<FloatCurveKey>> curves,
            JObject sidecar,
            int frameRate)
        {
            var duration = (float?)sidecar?["duration"]
                ?? curves.Values.SelectMany(x => x).Select(x => x.Time).DefaultIfEmpty(0f).Max();
            frameRate = Math.Max(1, frameRate);
            if (duration <= 0f)
            {
                return new[] { 0f };
            }

            var count = Math.Max(2, (int)Math.Ceiling(duration * frameRate) + 1);
            var times = new float[count];
            for (var i = 0; i < count; i++)
            {
                times[i] = Math.Min(duration, i / (float)frameRate);
            }
            times[count - 1] = duration;
            return times;
        }

        private static string[] BuildUnityHumanTraitMuscleNames()
        {
            var start = (int)AnimeStudio.HumanoidMuscleType.Muscles;
            var end = (int)AnimeStudio.HumanoidMuscleType.TDoFBones;
            var names = new string[Math.Max(0, end - start)];
            for (var i = 0; i < names.Length; i++)
            {
                names[i] = ((AnimeStudio.HumanoidMuscleType)(start + i)).ToAttributeString();
            }
            return names;
        }

        private static JObject BuildUnsupportedAcceleratedHumanPoseCurveSummary(IEnumerable<string> attributes)
        {
            var unsupported = (attributes ?? Enumerable.Empty<string>())
                .Where(IsUnsupportedAcceleratedHumanPoseCurve)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var limbGoalCount = unsupported.Count(IsHumanoidLimbGoalCurve);
            var tDoFCount = unsupported.Count(IsHumanoidTDoFCurve);
            return new JObject
            {
                ["curveCount"] = unsupported.Length,
                ["limbGoalCurveCount"] = limbGoalCount,
                ["tDoFCurveCount"] = tDoFCount,
                ["requiresFullHumanoidClipBake"] = unsupported.Length > 0,
                ["reason"] = unsupported.Length > 0
                    ? "当前 UnityBakeAccelerated 只通过 HumanPoseHandler 写 body/muscles；Left/Right Foot/Hand T/Q 和 *TDOF 曲线不会进入求解，含这些曲线的 clip 只能作为诊断输出，不能标为生产可复用动画。"
                    : "当前 clip 没有发现 HumanPoseHandler 无法消费的 limb IK/TDOF float 曲线。",
                ["preview"] = new JArray(unsupported.Take(64)),
            };
        }

        private static JObject BuildUnsupportedAcceleratedHumanPoseCurveSummary(Dictionary<string, List<FloatCurveKey>> curves)
        {
            var summary = BuildUnsupportedAcceleratedHumanPoseCurveSummary(curves?.Keys);
            var dynamicUnsupported = (curves ?? new Dictionary<string, List<FloatCurveKey>>(StringComparer.OrdinalIgnoreCase))
                .Where(x => IsUnsupportedAcceleratedHumanPoseCurve(x.Key) && IsFloatCurveDynamic(x.Value))
                .Select(x => x.Key)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            summary["dynamicCurveCount"] = dynamicUnsupported.Length;
            summary["dynamicLimbGoalCurveCount"] = dynamicUnsupported.Count(IsHumanoidLimbGoalCurve);
            summary["dynamicTDoFCurveCount"] = dynamicUnsupported.Count(IsHumanoidTDoFCurve);
            summary["dynamicPreview"] = new JArray(dynamicUnsupported.Take(64));
            return summary;
        }

        private static bool IsFloatCurveDynamic(IReadOnlyList<FloatCurveKey> keys)
        {
            if (keys == null || keys.Count < 2)
            {
                return false;
            }

            var first = keys[0].Value;
            return keys.Any(x => Math.Abs(x.Value - first) > 0.00001f);
        }

        private static bool IsUnsupportedAcceleratedHumanPoseCurve(string attribute)
        {
            return IsHumanoidLimbGoalCurve(attribute) || IsHumanoidTDoFCurve(attribute);
        }

        private static bool IsHumanoidLimbGoalCurve(string attribute)
        {
            if (string.IsNullOrWhiteSpace(attribute))
            {
                return false;
            }
            var names = new[]
            {
                "LeftFootT.", "LeftFootQ.",
                "RightFootT.", "RightFootQ.",
                "LeftHandT.", "LeftHandQ.",
                "RightHandT.", "RightHandQ.",
            };
            return names.Any(prefix => attribute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsHumanoidTDoFCurve(string attribute)
        {
            return !string.IsNullOrWhiteSpace(attribute)
                && attribute.IndexOf("TDOF.", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static float[] SampleVector3(
            Dictionary<string, List<FloatCurveKey>> curves,
            string prefix,
            float time,
            string fallbackPrefix = null)
        {
            if (!curves.ContainsKey(prefix + ".x") && !string.IsNullOrWhiteSpace(fallbackPrefix))
            {
                prefix = fallbackPrefix;
            }
            return new[]
            {
                SampleDecodedFloat(curves, prefix + ".x", time, 0f),
                SampleDecodedFloat(curves, prefix + ".y", time, 0f),
                SampleDecodedFloat(curves, prefix + ".z", time, 0f),
            };
        }

        private static float[] SampleQuaternion(
            Dictionary<string, List<FloatCurveKey>> curves,
            string prefix,
            float time,
            string fallbackPrefix = null)
        {
            if (!curves.ContainsKey(prefix + ".w") && !string.IsNullOrWhiteSpace(fallbackPrefix))
            {
                prefix = fallbackPrefix;
            }

            var q = new[]
            {
                SampleDecodedFloat(curves, prefix + ".x", time, 0f),
                SampleDecodedFloat(curves, prefix + ".y", time, 0f),
                SampleDecodedFloat(curves, prefix + ".z", time, 0f),
                SampleDecodedFloat(curves, prefix + ".w", time, 1f),
            };
            var length = Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
            if (length <= 0.000001)
            {
                return new[] { 0f, 0f, 0f, 1f };
            }
            return new[]
            {
                (float)(q[0] / length),
                (float)(q[1] / length),
                (float)(q[2] / length),
                (float)(q[3] / length),
            };
        }

        private static float SampleDecodedFloat(
            Dictionary<string, List<FloatCurveKey>> curves,
            string attribute,
            float time,
            float fallback)
        {
            if (string.IsNullOrWhiteSpace(attribute) || !curves.TryGetValue(attribute, out var keys) || keys.Count == 0)
            {
                return fallback;
            }
            if (time <= keys[0].Time)
            {
                return keys[0].Value;
            }
            if (time >= keys[^1].Time)
            {
                return keys[^1].Value;
            }

            for (var i = 1; i < keys.Count; i++)
            {
                if (time > keys[i].Time)
                {
                    continue;
                }

                var a = keys[i - 1];
                var b = keys[i];
                var span = b.Time - a.Time;
                if (span <= 0.000001f)
                {
                    return b.Value;
                }

                var t = (time - a.Time) / span;
                return a.Value + (b.Value - a.Value) * t;
            }

            return keys[^1].Value;
        }

        private static string[] BuildGltfJointPaths(string gltfPath, string modelName)
        {
            var gltf = JObject.Parse(File.ReadAllText(gltfPath));
            var nodes = gltf["nodes"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            if (nodes.Length == 0)
            {
                return Array.Empty<string>();
            }

            var rootIndices = gltf["scenes"]?.FirstOrDefault()?["nodes"]?.Values<int>().ToArray();
            if (rootIndices == null || rootIndices.Length == 0)
            {
                rootIndices = Enumerable.Range(0, nodes.Length)
                    .Where(i => !nodes.Any(n => n["children"]?.Values<int>().Contains(i) == true))
                    .ToArray();
            }

            var result = new List<string>();
            var visited = new HashSet<int>();
            foreach (var rootIndex in rootIndices)
            {
                AddGltfJointPath(nodes, rootIndex, "", modelName, result, visited);
            }

            return result
                .Where(x => x != null)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        private static string[] BuildUnityBakeJointPaths(string[] gltfJointPaths, JObject requestAvatar, string unityAvatarAsset)
        {
            var rootName = GetHumanDescriptionSkeletonRootName(requestAvatar);
            if (!string.IsNullOrWhiteSpace(unityAvatarAsset) || string.IsNullOrWhiteSpace(rootName))
            {
                return gltfJointPaths;
            }

            // HumanPoseHandler(avatar, jointPaths) 使用 BuildHumanAvatar 时的 Unity 骨架路径。
            // glTF 为了浏览会剥掉模型/Avatar 顶层；这里要补回去，写回 glTF 时再去掉。
            return gltfJointPaths
                .Select(path =>
                {
                    var normalized = NormalizeAcceleratedPathText(path);
                    if (string.IsNullOrWhiteSpace(normalized))
                    {
                        return rootName;
                    }
                    return normalized.StartsWith(rootName + "/", StringComparison.Ordinal)
                        ? normalized
                        : rootName + "/" + normalized;
                })
                .ToArray();
        }

        private static string GetHumanDescriptionSkeletonRootName(JObject requestAvatar)
        {
            var roots = requestAvatar?["skeletonBones"]?.OfType<JObject>()
                .Where(x => string.IsNullOrWhiteSpace((string)x["parentName"]))
                .Select(x => NormalizeAcceleratedPathText((string)x["name"]))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<string>();
            return roots.Length == 1 ? roots[0] : null;
        }

        private static string NormalizeAcceleratedPathText(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/').Trim('/');
        }

        private static void AddGltfJointPath(
            JObject[] nodes,
            int nodeIndex,
            string parentPath,
            string modelName,
            List<string> result,
            HashSet<int> visited)
        {
            if (nodeIndex < 0 || nodeIndex >= nodes.Length || !visited.Add(nodeIndex))
            {
                return;
            }

            var node = nodes[nodeIndex];
            var name = ((string)node["name"])?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "Node_" + nodeIndex.ToString(CultureInfo.InvariantCulture);
            }

            var path = string.IsNullOrWhiteSpace(parentPath) ? name : parentPath + "/" + name;
            result.Add(NormalizeAcceleratedJointPath(path, modelName));

            foreach (var child in node["children"]?.Values<int>() ?? Enumerable.Empty<int>())
            {
                AddGltfJointPath(nodes, child, path, modelName, result, visited);
            }
        }

        private static string NormalizeAcceleratedJointPath(string path, string modelName)
        {
            path = (path ?? string.Empty).Replace('\\', '/').Trim('/');
            if (!string.IsNullOrWhiteSpace(modelName)
                && path.StartsWith(modelName + "/", StringComparison.Ordinal))
            {
                return path[(modelName.Length + 1)..];
            }
            var rootIndex = path.IndexOf("/Root/", StringComparison.Ordinal);
            if (rootIndex >= 0)
            {
                return path[(rootIndex + 1)..];
            }
            return string.Equals(path, modelName, StringComparison.Ordinal) ? string.Empty : path;
        }

        private static string BuildAcceleratedAvatarKey(JObject model, JObject animation)
        {
            return string.Join("|", new[]
            {
                (string)model?["source"],
                Convert.ToString((long?)model?["pathId"], CultureInfo.InvariantCulture),
                (string)model?["avatarName"],
                (string)animation?["confidence"],
            }.Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        private static string BuildAcceleratedClipKey(JObject animation)
        {
            return string.Join("|", new[]
            {
                (string)animation?["source"],
                Convert.ToString((long?)animation?["pathId"], CultureInfo.InvariantCulture),
                (string)animation?["name"],
            }.Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        private static string GenerateSelection(
            string indexPath,
            PreviewSelection selection,
            string outputDirectory,
            string sourceRootOverride,
            string unityProject,
            string unityEditor,
            string unityModelPrefab,
            string unityAnimationClip,
            string unityAnimatorController,
            string unityAvatarAsset,
            string unityBakeOutput,
            int frameRate,
            bool probeMuscles,
            bool enableIkGoalDriver,
            bool rebuildEditorCurveClip,
            bool ignoreImportedAvatar,
            string clipFilterModeOverride,
            bool runUnityBake,
            string unityBakeWorkerQueue,
            string bakedGltfOutput,
            string bakedFbxOutput,
            string blender,
            IReadOnlyDictionary<string, string> importedAnimationClips = null,
            bool sampleRecoverableSkippedLayersDiagnostic = false
        )
        {
            var model = selection.Model["model"] as JObject;
            var animation = AttachAdditionalLayerClipAssetPaths(selection.Animation, importedAnimationClips);
            var modelName = (string)model?["name"];
            var animationName = (string)animation?["name"];
            if (string.IsNullOrWhiteSpace(modelName) || string.IsNullOrWhiteSpace(animationName))
            {
                Logger.Error("Selected Unity bake request entry is missing model or animation name.");
                return null;
            }
            if (!IsExplicitBakeRelation(animation))
            {
                Logger.Error("Selected Unity bake candidate is not a Unity explicit model-animation relation. Production bake refuses structure/name/manual matches.");
                Logger.Error($"Relation source: {(string)animation?["relationSource"] ?? "(none)"}; confidence: {(string)animation?["confidence"] ?? "(none)"}");
                return null;
            }

            var requiresHumanoidBake = ResolveRequiresHumanoidBake(animation);
            var legacyUnityBakePolicyOverride = false;
            string legacyUnityBakePolicyOverrideReason = null;
            if (requiresHumanoidBake && IsStandaloneBodyBakeBlocked(animation, out var standaloneBlockReason))
            {
                var hasExplicitUnityClipSolver =
                    !string.IsNullOrWhiteSpace(unityAnimationClip)
                    && (!string.IsNullOrWhiteSpace(unityAvatarAsset) || !string.IsNullOrWhiteSpace(unityModelPrefab));
                var hasControllerLayerContext = HasAdditionalAnimatorControllerLayerClips(animation);
                if (!hasExplicitUnityClipSolver || (!IsLegacyUnityBakeDeprecationReason(standaloneBlockReason) && !hasControllerLayerContext))
                {
                    Logger.Error(
                        "Selected Humanoid animation is an explicit Unity relation, but it is not a standalone body animation. " +
                        standaloneBlockReason + " Restore/sample the AnimatorController layer/blend context before baking this clip."
                    );
                    return null;
                }

                Logger.Warning(
                    "Selected Humanoid animation is blocked by standalone-body bake checks, but deterministic AnimatorController context is available. " +
                    "Continuing as an explicit UnityOracle/PlayableGraph diagnostic because Unity AnimationClip plus Avatar/Prefab are available; " +
                    "the final deliverable must still be glTF TRS with visual validation."
                );
                legacyUnityBakePolicyOverride = true;
                legacyUnityBakePolicyOverrideReason = standaloneBlockReason;
            }
            var avatar = PrepareAvatarForUnityBake(model?["avatar"] as JObject, requiresHumanoidBake, unityModelPrefab, unityAvatarAsset);
            var clipFilterMode = ResolveUnityBakeClipFilterMode(animation, requiresHumanoidBake, clipFilterModeOverride, unityAnimatorController);
            var requestUnsupportedHumanPoseCurves = requiresHumanoidBake
                ? TryBuildUnsupportedHumanPoseCurveSummaryFromSidecar(animation)
                : null;
            var autoEnabledIkGoalDriver = false;
            string autoEnabledIkGoalDriverReason = null;
            if (!enableIkGoalDriver
                && ShouldAutoEnableIkGoalDriver(animation, unityAnimatorController, requestUnsupportedHumanPoseCurves))
            {
                enableIkGoalDriver = true;
                autoEnabledIkGoalDriver = true;
                autoEnabledIkGoalDriverReason =
                    "Animation sidecar contains dynamic Hand/Foot IK goal curves and a provided AnimatorController is available; enabling IK goal diagnostic so the UnityOracle request does not silently sample a known-incomplete Humanoid pose.";
                Logger.Warning(autoEnabledIkGoalDriverReason);
            }
            var humanBones = avatar?["humanBones"]?.Values<string>()?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray() ?? Array.Empty<string>();
            var hasExplicitUnityAvatar = !string.IsNullOrWhiteSpace(unityModelPrefab)
                || !string.IsNullOrWhiteSpace(unityAvatarAsset);
            if (requiresHumanoidBake && humanBones.Length == 0 && !hasExplicitUnityAvatar)
            {
                Logger.Error(
                    "Selected Humanoid animation requires a production Unity Avatar, but the exported Avatar has no HumanDescription humanBones and no Unity prefab was supplied. " +
                    "Run original Unity Avatar/prefab recovery first, or provide a Unity prefab with the original Animator.avatar. " +
                    "AvatarConstant/internalSolver data is deterministic metadata, but by itself it is only a diagnostic input and must not enter production bake."
                );
                return null;
            }
            if (requiresHumanoidBake && !HasUsableHumanoidReferencePose(avatar, unityModelPrefab, unityAvatarAsset))
            {
                Logger.Error(
                    "Selected Humanoid animation needs Unity Avatar reference pose metadata, but the selected model avatar is incomplete. " +
                    "Provide a Unity prefab with its original Avatar or refresh the model Avatar metadata so HumanDescription skeletonBones are available. " +
                    "Generating this request from derived humanBones plus glTF rest pose would create a misleading Unity oracle."
                );
                return null;
            }

            var output = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(indexPath)) ?? Directory.GetCurrentDirectory(),
                    "UnityBakeRequests",
                    $"{SafeName(modelName)}__{SafeName(animationName)}"
                )
                : outputDirectory;
            Directory.CreateDirectory(output);

            var requestPath = Path.Combine(output, "unity_bake_request.json");
            var resultPath = string.IsNullOrWhiteSpace(unityBakeOutput)
                ? Path.Combine(output, "unity_bake_result.json")
                : unityBakeOutput;
            var logPath = Path.Combine(output, "unity_bake.log");
            // IK goal driver 需要先读取 Unity editor curves，才能拿到 Hand/Foot T/Q。
            // 显式启用 IK 诊断时自动打开 probe，避免 request 写了 enable=true 但实际无曲线可喂。
            var effectiveProbeMuscles = probeMuscles || enableIkGoalDriver;

            var request = new
            {
                version = 1,
                generatedAt = DateTime.UtcNow.ToString("O"),
                rule = "Production path: Unity Animator/Avatar samples Humanoid/Muscle motion and AnimeStudio writes the sampled target skeleton TRS back into glTF. Internal Humanoid formula solving is experimental and not used here.",
                sourceIndex = Path.GetFullPath(indexPath),
                sourceRootOverride,
                frameRate = Math.Max(1, frameRate),
                // 诊断开关：让 Unity 输出单 muscle 对骨骼的真实影响，用来标定内部 Humanoid 求解器。
                probeMuscles = effectiveProbeMuscles,
                // 诊断开关：用 Unity editor curve 中的 Hand/Foot IK goal 驱动 Animator IK pass。
                // 只用于 Endfield Humanoid/Muscle 求解器对照；结果仍必须 needs_review。
                enableEditorCurveIkGoalDriver = enableIkGoalDriver,
                autoEnabledIkGoalDriver,
                autoEnabledIkGoalDriverReason,
                // 诊断开关：把 normally skipped 但能解析出 motion/mask 的 recovered controller layer
                // 放进采样计划做对照。它只能帮助定位 layer/BlendTree 语义，不能升级生产状态。
                sampleRecoverableSkippedLayersDiagnostic,
                // 诊断开关：Endfield 部分恢复 .anim 只有 editor curves 能被 AnimationUtility 看到，
                // runtime m_MuscleClip 不驱动 HumanPose。显式开启后，Unity helper 会从 editor curves
                // 重建一份临时 Humanoid clip 再采样；结果必须保留 needs_review，不能当生产证明。
                rebuildEditorCurveClip,
                // Endfield 这类 MixedHumanoidTransform 如果已有确定的 Transform TRS 和
                // AnimatorController layer/mask 上下文，优先把异常 Humanoid/Muscle 曲线排除出
                // 当前诊断采样，保留 controller 叠层后的目标骨架 TRS。结果仍需视觉验收。
                clipFilterMode,
                // 诊断开关：忽略已传入的 imported Avatar，强制用 request 内 HumanDescription/oracle
                // 重建 Avatar。只用于定位 imported Avatar/root 绑定问题，结果必须标为不可信生产 bake。
                ignoreImportedAvatar,
                outputJson = resultPath,
                logJson = Path.Combine(output, "unity_bake_report.json"),
                unityProject,
                unityAssetPaths = new
                {
                    // 这两个路径必须是 Unity 工程里的 Assets/... 路径；Unity helper 不按游戏名猜资源。
                    modelPrefab = unityModelPrefab,
                    animationClip = unityAnimationClip,
                    // 预留给原始 RuntimeAnimatorController asset。Ambor 这类 BlendTree 叶子 clip
                    // 必须通过 Controller state 采样，不能再把单个 .anim 当完整身体动画。
                    animatorController = NormalizeUnityAssetPath(unityAnimatorController),
                    // 原神等缺少完整 HumanDescription.skeletonBones 的资源，需要显式导入原始 UnityEngine.Avatar。
                    // 默认不填，避免影响 VRising/Freedunk 等普通 Unity 项目的既有路径。
                    avatarAsset = unityAvatarAsset,
                },
                animeStudioAssets = new
                {
                    model = new
                    {
                        name = modelName,
                        gltf = ResolveSourcePath((string)model?["output"], null),
                        source = ResolveSourcePath((string)model?["source"], sourceRootOverride),
                        container = (string)model?["container"],
                        skeletonHash = (string)model?["skeletonHash"],
                        boneCount = (int?)model?["boneCount"] ?? 0,
                        bonePaths = model?["bonePaths"]?.Values<string>()?.Take(512).ToArray() ?? Array.Empty<string>(),
                        avatar,
                    },
                    animation = new
                    {
                        name = animationName,
                        anim = ResolveSourcePath((string)animation?["output"], null),
                        animationAsset = ResolveAnimationAssetPath(animation),
                        source = ResolveSourcePath((string)animation?["source"], sourceRootOverride),
                        container = (string)animation?["container"],
                        animationType = (string)animation?["animationType"],
                        hasMuscleClip = (bool?)animation?["hasMuscleClip"] ?? false,
                        requiresHumanoidBake,
                        unsupportedHumanPoseCurveSummary = requestUnsupportedHumanPoseCurves,
                        autoEnabledIkGoalDriver,
                        autoEnabledIkGoalDriverReason,
                        relation = (string)animation?["relation"],
                        relationSource = (string)animation?["relationSource"],
                        confidence = (string)animation?["confidence"],
                        score = (int?)animation?["score"] ?? 0,
                        animatorControllerContext = animation?["animatorControllerContext"] as JObject,
                        animatorControllerRequestedClip = animation?["animatorControllerRequestedClip"] as JObject,
                    },
                },
                validation = new
                {
                    expected = "Unity helper should output non-zero changed tracks on core body bones. If Humanoid clip is used without Animator.avatar, the bake must fail instead of producing guessed data.",
                    nextStep = "After unity_bake_result.json is produced, AnimeStudio can merge sampled TRS into glTF/GLB preview or animation pack.",
                    clipFilterMode,
                    legacyUnityBakePolicyOverride,
                    legacyUnityBakePolicyOverrideReason,
                },
            };

            File.WriteAllText(requestPath, JsonConvert.SerializeObject(request, Formatting.Indented));
            Logger.Info($"Unity bake request: {requestPath}");

            if (runUnityBake)
            {
                if (RunUnity(requestPath, resultPath, unityProject, unityEditor, logPath, unityBakeWorkerQueue))
                {
                    var bakedGltf = UnityBakeResultApplier.Apply(requestPath, bakedGltfOutput);
                    if (!string.IsNullOrWhiteSpace(bakedFbxOutput))
                    {
                        BlenderFbxExporter.Export(bakedGltf, bakedFbxOutput, blender);
                    }
                }
            }
            return requestPath;
        }

        private static string ResolveUnityBakeClipFilterMode(JObject animation, bool requiresHumanoidBake, string overrideMode, string unityAnimatorController)
        {
            var normalizedOverride = NormalizeUnityBakeClipFilterModeOverride(overrideMode);
            if (normalizedOverride != null)
            {
                return normalizedOverride;
            }

            if (!requiresHumanoidBake || animation == null)
            {
                return null;
            }

            var animationType = (string)animation["animationType"];
            if (!string.Equals(animationType, "MixedHumanoidTransform", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var hasOriginalRuntimeController = !string.IsNullOrWhiteSpace(unityAnimatorController);
            if (hasOriginalRuntimeController)
            {
                return null;
            }

            if (!HasAnimatorControllerContext(animation))
            {
                return null;
            }

            // 这条规则只用于没有原始 RuntimeAnimatorController 的诊断路径：
            // Endfield 的 Pelica idle 已证明 generated single/multi layer controller
            // 可能把 Humanoid/Muscle full-body 采样成抬手、扭腕等语义错误姿态。
            // 默认只保留确定 Transform/controller 层曲线；人工对照可显式传 full。
            return "transform_only";
        }

        private static string NormalizeUnityBakeClipFilterModeOverride(string value)
        {
            if (string.IsNullOrWhiteSpace(value)
                || string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "default", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "full", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "full_clip", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            if (string.Equals(value, "transform_only", StringComparison.OrdinalIgnoreCase))
            {
                return "transform_only";
            }

            Logger.Warning($"Unknown --unity_bake_clip_filter_mode '{value}', falling back to auto.");
            return null;
        }

        private static bool RunUnity(string requestPath, string resultPath, string unityProject, string unityEditor, string logPath, string unityBakeWorkerQueue)
        {
            if (string.IsNullOrWhiteSpace(unityProject) || !Directory.Exists(unityProject))
            {
                Logger.Error("--unity_project is required and must exist when --run_unity_bake is used.");
                return false;
            }
            if (string.IsNullOrWhiteSpace(unityEditor) || !File.Exists(unityEditor))
            {
                Logger.Error("--unity_editor must point to Unity.exe when --run_unity_bake is used.");
                return false;
            }
            var helperError = ValidateUnityBakeHelperVersion(unityProject);
            if (!string.IsNullOrWhiteSpace(helperError))
            {
                Logger.Error(helperError);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(unityBakeWorkerQueue))
            {
                return RunUnityWorker(requestPath, resultPath, unityProject, unityEditor, logPath, unityBakeWorkerQueue);
            }

            var args = new[]
            {
                "-batchmode",
                "-quit",
                "-projectPath", Quote(unityProject),
                "-executeMethod", "AnimeStudio.UnityBake.AnimeStudioBakeCli.Run",
                "-animeStudioBakeRequest", Quote(requestPath),
                "-logFile", Quote(logPath),
            };
            Logger.Info("Running Unity bake helper...");
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = unityEditor,
                Arguments = string.Join(" ", args),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(stdout)) Logger.Info(stdout.Trim());
            if (!string.IsNullOrWhiteSpace(stderr)) Logger.Warning(stderr.Trim());
            Logger.Info($"Unity bake log: {logPath}");
            if (process.ExitCode != 0)
            {
                Logger.Error($"Unity bake helper failed with exit code {process.ExitCode}.");
                return false;
            }
            return true;
        }

        private static string ValidateUnityBakeHelperVersion(string unityProject)
        {
            var helperRoot = Path.Combine(unityProject, "Assets", "AnimeStudio.UnityBake", "Editor");
            var cliPath = Path.Combine(helperRoot, "AnimeStudioBakeCli.cs");
            var modelPath = Path.Combine(helperRoot, "AnimeStudioBakeModels.cs");
            var bakerPath = Path.Combine(helperRoot, "AnimeStudioPlayableBaker.cs");
            var skeletonPath = Path.Combine(helperRoot, "AnimeStudioGltfSkeletonBuilder.cs");
            if (!File.Exists(cliPath) || !File.Exists(modelPath) || !File.Exists(bakerPath) || !File.Exists(skeletonPath))
            {
                return "Unity project does not contain a complete AnimeStudio.UnityBake helper. Copy AnimeStudio.UnityBake\\Assets\\AnimeStudio.UnityBake into the Unity project's Assets directory: " + helperRoot;
            }

            var modelText = File.ReadAllText(modelPath);
            var bakerText = File.ReadAllText(bakerPath);
            var skeletonText = File.ReadAllText(skeletonPath);
            if (!modelText.Contains("importedAvatarAssetValid", StringComparison.Ordinal)
                || !modelText.Contains("importedAnimationClip", StringComparison.Ordinal)
                || !modelText.Contains("animationClipSource", StringComparison.Ordinal)
                || !bakerText.Contains("importedAvatarAssetValid", StringComparison.Ordinal)
                || !bakerText.Contains("AnimationClipLoadResult", StringComparison.Ordinal)
                || !bakerText.Contains("animationClipSource", StringComparison.Ordinal)
                || !bakerText.Contains("LoadImportedAvatarAsset", StringComparison.Ordinal)
                || !bakerText.Contains("clip.isHumanMotion", StringComparison.Ordinal)
                || !bakerText.Contains("isHumanMotion=false", StringComparison.Ordinal)
                || !skeletonText.Contains("request explicitly supplied unityAssetPaths.avatarAsset", StringComparison.OrdinalIgnoreCase))
            {
                return "Unity project has an outdated AnimeStudio.UnityBake helper. Copy AnimeStudio.UnityBake\\Assets\\AnimeStudio.UnityBake into the Unity project's Assets directory so imported Avatar asset proof, AnimationClip source proof, and Humanoid humanMotion guards are written before trusted bake statistics are accepted: " + helperRoot;
            }

            return null;
        }

        private static bool RunUnityWorker(string requestPath, string resultPath, string unityProject, string unityEditor, string logPath, string queuePath)
        {
            Directory.CreateDirectory(queuePath);
            var workerLog = Path.Combine(queuePath, "unity_bake_worker.log");
            if (!EnsureUnityWorker(queuePath, unityProject, unityEditor, workerLog))
            {
                return false;
            }

            var jobId = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}";
            var pendingPath = Path.Combine(queuePath, $"{jobId}.request.tmp");
            var jobPath = Path.Combine(queuePath, $"{jobId}.request.json");
            var donePath = Path.Combine(queuePath, $"{jobId}.request.done.json");
            var errorPath = Path.Combine(queuePath, $"{jobId}.request.error.json");
            if (File.Exists(resultPath))
            {
                TryDelete(resultPath);
            }

            File.Copy(requestPath, pendingPath, overwrite: true);
            File.Move(pendingPath, jobPath);
            Logger.Info($"Queued Unity bake worker job: {jobPath}");

            var timeoutAt = DateTime.UtcNow.AddMinutes(10);
            while (DateTime.UtcNow < timeoutAt)
            {
                if (File.Exists(donePath) && File.Exists(resultPath))
                {
                    Logger.Info($"Unity bake worker completed job: {donePath}");
                    return true;
                }

                if (File.Exists(errorPath))
                {
                    Logger.Error($"Unity bake worker failed job: {errorPath}");
                    TryCopy(errorPath, logPath);
                    return false;
                }

                Thread.Sleep(500);
            }

            Logger.Error($"Unity bake worker timed out. Queue: {queuePath}; worker log: {workerLog}");
            return false;
        }

        private static bool EnsureUnityWorker(string queuePath, string unityProject, string unityEditor, string workerLog)
        {
            var heartbeat = Path.Combine(queuePath, "worker_heartbeat.json");
            if (IsFresh(heartbeat, TimeSpan.FromSeconds(30)))
            {
                Logger.Info($"Using existing Unity bake worker: {queuePath}");
                return true;
            }

            var startLock = Path.Combine(queuePath, "worker_start.lock");
            Process workerProcess = null;
            using (AcquireFileLock(startLock, TimeSpan.FromSeconds(20)))
            {
                if (IsFresh(heartbeat, TimeSpan.FromSeconds(30)))
                {
                    Logger.Info($"Using existing Unity bake worker: {queuePath}");
                    return true;
                }

                var args = new[]
                {
                    "-batchmode",
                    "-projectPath", Quote(unityProject),
                    "-executeMethod", "AnimeStudio.UnityBake.AnimeStudioBakeWorker.Run",
                    "-animeStudioBakeQueue", Quote(queuePath),
                    "-logFile", Quote(workerLog),
                };
                Logger.Info("Starting persistent Unity bake worker...");
                workerProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = unityEditor,
                    Arguments = string.Join(" ", args),
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true,
                });
            }

            var timeoutAt = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < timeoutAt)
            {
                if (IsFresh(heartbeat, TimeSpan.FromSeconds(30)))
                {
                    Logger.Info($"Unity bake worker is ready: {queuePath}");
                    return true;
                }
                if (workerProcess != null && workerProcess.HasExited)
                {
                    Logger.Error($"Unity bake worker exited before heartbeat. ExitCode={workerProcess.ExitCode}; Log={workerLog}");
                    var tail = TryReadLogTail(workerLog, 80);
                    if (!string.IsNullOrWhiteSpace(tail))
                    {
                        Logger.Error("Unity bake worker log tail:\n" + tail);
                    }
                    return false;
                }
                Thread.Sleep(1000);
            }

            Logger.Error($"Unity bake worker did not become ready. Log: {workerLog}");
            var logTail = TryReadLogTail(workerLog, 80);
            if (!string.IsNullOrWhiteSpace(logTail))
            {
                Logger.Error("Unity bake worker log tail:\n" + logTail);
            }
            return false;
        }

        private static IDisposable AcquireFileLock(string path, TimeSpan timeout)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            var timeoutAt = DateTime.UtcNow.Add(timeout);
            while (true)
            {
                try
                {
                    return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException) when (DateTime.UtcNow < timeoutAt)
                {
                    Thread.Sleep(200);
                }
            }
        }

        private static bool IsFresh(string path, TimeSpan maxAge)
        {
            try
            {
                return File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) <= maxAge;
            }
            catch
            {
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best effort cleanup.
            }
        }

        private static void TryCopy(string source, string destination)
        {
            try
            {
                var directory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.Copy(source, destination, overwrite: true);
            }
            catch
            {
                // Diagnostic copy only.
            }
        }

        private static string TryReadLogTail(string path, int maxLines)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                var lines = File.ReadAllLines(path);
                return string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Length - Math.Max(1, maxLines))));
            }
            catch
            {
                return null;
            }
        }

        private static PreviewSelection SelectPreview(JObject index, string modelSelector, string animationSelector, string indexDirectory)
        {
            foreach (var model in index["models"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var modelInfo = model["model"] as JObject;
                if (!Matches(modelSelector, (string)modelInfo?["name"], (string)modelInfo?["output"]))
                {
                    continue;
                }
                var animation = model["candidates"]?
                    .OfType<JObject>()
                    .FirstOrDefault(x => Matches(animationSelector, (string)x["name"], (string)x["output"]));
                if (animation != null)
                {
                    return new PreviewSelection(model, animation);
                }

                var embeddedAnimation = TrySelectAnimationSidecar(indexDirectory, animationSelector);
                if (embeddedAnimation != null)
                {
                    return new PreviewSelection(model, embeddedAnimation);
                }
            }
            return null;
        }

        private static JObject TrySelectAnimationSidecar(string libraryRoot, string animationSelector)
        {
            if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
            {
                return null;
            }

            var animationRoot = Path.Combine(libraryRoot, "Animations");
            if (!Directory.Exists(animationRoot))
            {
                return null;
            }

            foreach (var sidecarPath in Directory.EnumerateFiles(animationRoot, "*.animation_asset.json", SearchOption.AllDirectories))
            {
                JObject sidecar;
                try
                {
                    sidecar = JObject.Parse(File.ReadAllText(sidecarPath));
                }
                catch
                {
                    continue;
                }

                if (!Matches(animationSelector, (string)sidecar["name"], (string)sidecar["output"], sidecarPath))
                {
                    continue;
                }

                return BuildDiagnosticCandidateFromAnimationAsset(sidecar, sidecarPath);
            }

            return null;
        }

        private static JObject BuildDiagnosticCandidateFromAnimationAsset(JObject sidecar, string sidecarPath)
        {
            var humanoid = sidecar["humanoid"] as JObject;
            var hasMuscleClip = (bool?)humanoid?["hasMuscleClip"] == true ||
                string.Equals((string)humanoid?["present"], "true", StringComparison.OrdinalIgnoreCase);
            var animationType = (string)sidecar["animationType"];
            return new JObject
            {
                ["name"] = (string)sidecar["name"],
                ["output"] = (string)sidecar["output"],
                ["animationAsset"] = sidecarPath,
                ["source"] = (string)sidecar["source"],
                ["container"] = (string)sidecar["container"],
                ["animationType"] = animationType,
                ["animationCapability"] = (string)humanoid?["gltfPlaybackStatus"],
                ["hasMuscleClip"] = hasMuscleClip,
                ["requiresHumanoidBake"] = hasMuscleClip || (!string.IsNullOrWhiteSpace(animationType) && animationType.IndexOf("Humanoid", StringComparison.OrdinalIgnoreCase) >= 0),
                ["relation"] = "embedded_or_manual_diagnostic_selection",
                ["relationSource"] = "embedded_sidecar_diagnostic",
                ["confidence"] = "manual_unity_bake_selection",
                ["score"] = 100,
            };
        }

        private static bool IsExplicitBakeRelation(JObject animation)
        {
            return string.Equals((string)animation?["relationSource"], "explicit", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsStandaloneBodyBakeBlocked(JObject animation, out string reason)
        {
            reason = null;
            var candidate = animation?["candidate"] as JObject;
            if (ReadBaseLayerClipPathId(candidate) != null
                && animation?["animatorControllerRequestedClip"] is JObject)
            {
                // 选中的原 clip 可能是 AnimatorController 的附件/叠加层，所以它本身会标记为
                // standaloneBodyBakeReady=false。只要 request 已经切换到同状态的 baseLayerClip，
                // 后续门禁应检查身体主 clip，而不是继续用原辅助 clip 拦截。
                return false;
            }
            if (IsFalse(animation?["standaloneBodyBakeReady"]) || IsFalse(candidate?["standaloneBodyBakeReady"]))
            {
                reason = (string)animation?["standaloneBodyBakeReason"]
                    ?? (string)candidate?["standaloneBodyBakeReason"]
                    ?? (string)candidate?["productionUnityBakeBlockedReason"]
                    ?? "AnimationClip requires AnimatorController context.";
                return true;
            }

            var animationAsset = (string)animation?["animationAsset"];
            if (string.IsNullOrWhiteSpace(animationAsset) || !File.Exists(animationAsset))
            {
                return false;
            }

            try
            {
                var sidecar = JObject.Parse(File.ReadAllText(animationAsset));
                if (IsFalse(sidecar["standaloneBodyBakeReady"]))
                {
                    reason = (string)sidecar["standaloneBodyBakeReason"]
                        ?? "AnimationClip sidecar marks it as requiring AnimatorController context.";
                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static long? ReadBaseLayerClipPathId(JObject candidate)
        {
            // 旧索引里 animatorControllerContext/baseLayerClip/clip 可能是字符串或简单值。
            // 只在结构完整时读取 pathId，避免诊断 request 因 JValue 形态崩溃。
            if (candidate?["animatorControllerContext"] is not JObject context
                || context["baseLayerClip"] is not JObject baseLayerClip
                || baseLayerClip["clip"] is not JObject clip)
            {
                return null;
            }

            return (long?)clip["pathId"];
        }

        private static bool IsLegacyUnityBakeDeprecationReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return false;
            }

            return reason.IndexOf("old standalone Unity bake readiness flag is deprecated", StringComparison.OrdinalIgnoreCase) >= 0
                || reason.IndexOf("Production export must use decoded direct glTF TRS/weights", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool HasAdditionalAnimatorControllerLayerClips(JObject animation)
        {
            return AnimatorControllerContextHasAdditionalLayerClips(animation?["animatorControllerContext"] as JObject)
                || AnimatorControllerContextHasAdditionalLayerClips(animation?["candidate"]?["animatorControllerContext"] as JObject);
        }

        private static bool HasAnimatorControllerContext(JObject animation)
        {
            return animation?["animatorControllerContext"] is JObject
                || animation?["candidate"]?["animatorControllerContext"] is JObject
                || HasAdditionalAnimatorControllerLayerClips(animation);
        }

        private static bool AnimatorControllerContextHasAdditionalLayerClips(JObject context)
        {
            return context?["additionalLayerClips"] is JArray clips && clips.Count > 0;
        }

        private static bool IsFalse(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }
            if (token.Type == JTokenType.Boolean)
            {
                return token.Value<bool>() == false;
            }
            if (token.Type == JTokenType.Integer)
            {
                return token.Value<int>() == 0;
            }
            return bool.TryParse(token.ToString(), out var value) && value == false;
        }

        private static PreviewSelection SelectExplicitCandidateFromLibraryDb(
            string dbPath,
            string modelSelector,
            string animationSelector,
            bool allowSuppliedAvatarAsset = false,
            IReadOnlyDictionary<string, string> importedAvatarAssets = null)
        {
            return SelectExplicitBakeCandidatesFromLibraryDb(
                    dbPath,
                    modelSelector,
                    animationSelector,
                    1,
                    allowSuppliedAvatarAsset: allowSuppliedAvatarAsset,
                    importedAvatarAssets: importedAvatarAssets)
                .FirstOrDefault();
        }

        private static string TryDescribeBlockedExplicitCandidate(string dbPath, string modelSelector, string animationSelector)
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT m.name
     , a.name
     , json_extract(c.raw_json, '$.productionAnimationPath')
     , json_extract(c.raw_json, '$.productionAnimationBlockedReason')
     , json_extract(c.raw_json, '$.productionUnityBakeBlockedReason')
     , json_extract(c.raw_json, '$.fullHumanoidBakeBlockedReason')
     , json_extract(c.raw_json, '$.modelAvatarDiagnostics.productionAvatarMissingReason')
     , json_extract(c.raw_json, '$.standaloneBodyBakeStatus')
     , COALESCE(json_extract(c.raw_json, '$.standaloneBodyRequiresDirectTrsSolve'), 0)
FROM model_animation_candidates c
JOIN assets m ON m.kind='Model' AND m.output=c.model_output
JOIN assets a ON a.kind='Animation' AND a.output=c.animation_output
WHERE c.relation_source='explicit'
  AND (
    json_extract(c.raw_json, '$.productionAnimationPath') IN ('NeedsAnimatorControllerContext', 'NeedsDirectTrsAnimation')
    OR COALESCE(json_extract(c.raw_json, '$.standaloneBodyRequiresDirectTrsSolve'), 0)=1
    OR COALESCE(json_extract(c.raw_json, '$.fullHumanoidBakeRequired'), 0)=1
  )
  AND ($model='' OR m.name LIKE $model OR m.output LIKE $model)
  AND ($animation='' OR a.name LIKE $animation OR a.output LIKE $animation)
LIMIT 1;";
                command.Parameters.AddWithValue("$model", string.IsNullOrWhiteSpace(modelSelector) ? "" : "%" + EscapeLike(modelSelector) + "%");
                command.Parameters.AddWithValue("$animation", string.IsNullOrWhiteSpace(animationSelector) ? "" : "%" + EscapeLike(animationSelector) + "%");
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    return null;
                }

                var model = reader.IsDBNull(0) ? "(unknown model)" : reader.GetString(0);
                var animation = reader.IsDBNull(1) ? "(unknown animation)" : reader.GetString(1);
                var productionPath = reader.IsDBNull(2) ? null : reader.GetString(2);
                var animationReason = reader.IsDBNull(3) ? null : reader.GetString(3);
                var bakeReason = reader.IsDBNull(4) ? null : reader.GetString(4);
                var fullBakeReason = reader.IsDBNull(5) ? null : reader.GetString(5);
                var avatarReason = reader.IsDBNull(6) ? null : reader.GetString(6);
                var standaloneStatus = reader.IsDBNull(7) ? null : reader.GetString(7);
                var requiresDirectSolve = !reader.IsDBNull(8) && reader.GetInt64(8) != 0;
                var reason = FirstNonEmpty(animationReason, bakeReason, fullBakeReason, avatarReason, standaloneStatus, productionPath);
                if (string.Equals(productionPath, "NeedsAnimatorControllerContext", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(animationReason, "requires_animator_controller_context", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(standaloneStatus, "requires_animator_controller_context", StringComparison.OrdinalIgnoreCase))
                {
                    return $"Selected explicit Unity candidate is blocked before bake: {model} + {animation} requires AnimatorController context. Reason: {reason ?? "requires_animator_controller_context"}. Baking this single AnimationClip would produce a misleading root/accessory/static pose result.";
                }

                if (requiresDirectSolve
                    || string.Equals(productionPath, "NeedsDirectTrsAnimation", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(animationReason, "needs_direct_trs_animation", StringComparison.OrdinalIgnoreCase))
                {
                    return $"Selected explicit Unity candidate requires Humanoid/Muscle to target-skeleton TRS solving before it can be production glTF: {model} + {animation}. Avatar context: {avatarReason ?? fullBakeReason ?? bakeReason ?? "unknown"}. Provide/recover the original Unity Avatar or complete HumanDescription before UnityBakeAccelerated can generate a trustworthy request.";
                }

                return $"Selected explicit Unity candidate is blocked before bake: {model} + {animation}. Reason: {reason ?? "unknown"}.";
            }
            catch
            {
                return null;
            }
        }

        private static string EscapeLike(string value)
        {
            return value ?? string.Empty;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }

        private static IEnumerable<PreviewSelection> SelectExplicitBakeCandidatesFromLibraryDb(
            string dbPath,
            string modelSelector,
            string animationSelector,
            int limit,
            bool skipBakedCache = false,
            bool allowSuppliedAvatarAsset = false,
            IReadOnlyDictionary<string, string> importedAvatarAssets = null)
        {
            SQLitePCL.Batteries_V2.Init();
            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();
            var libraryRoot = Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? string.Empty;
            connection.CreateFunction<string, string>(
                "anime_studio_canonical_output",
                path => CanonicalizeLibraryOutput(path, libraryRoot));

            var modelOutputs = SelectMatchingAssetOutputs(connection, "Model", modelSelector, 4096);
            var animationOutputs = SelectMatchingAssetOutputs(connection, "Animation", animationSelector, 8192);
            if (modelOutputs != null && modelOutputs.Count == 0)
            {
                return Array.Empty<PreviewSelection>();
            }
            if (animationOutputs != null && animationOutputs.Count == 0)
            {
                return Array.Empty<PreviewSelection>();
            }
            CreateTempOutputTable(connection, "temp_unity_bake_models", modelOutputs);
            CreateTempOutputTable(connection, "temp_unity_bake_animations", animationOutputs);
            CreateTempImportedAvatarAssetKeys(connection, importedAvatarAssets);
            CreateTempUnityBakeAvatarReadyModelTable(connection, modelOutputs);
            var hasBakeCache = skipBakedCache && BakeCacheTableExists(connection);
            if (hasBakeCache)
            {
                CreateTempProcessedBakeCacheTable(connection, libraryRoot);
            }

            var skipBakedCacheSql = hasBakeCache
                ? @"
  AND NOT EXISTS (
    SELECT 1
    FROM temp_unity_bake_processed_cache bc
    WHERE bc.model_output = anime_studio_canonical_output(c.model_output)
      AND bc.animation_output = anime_studio_canonical_output(c.animation_output)
  )"
                : "";
            var queryLimit = Math.Max(1, limit);
            var diversifyModels = string.IsNullOrWhiteSpace(modelSelector) && string.IsNullOrWhiteSpace(animationSelector);
            if (diversifyModels)
            {
                // 默认批量验收要尽快覆盖不同骨架/模型族，避免一个高分模型的上百个动作长期占满批次。
                // 指定模型或动画时保持精确筛选，不做额外轮转。
                queryLimit = Math.Min(Math.Max(queryLimit * 64, queryLimit), 4096);
            }
            var modelRankLimit = diversifyModels ? 16 : 1;
            var readyModelLimit = diversifyModels
                ? Math.Min(queryLimit, Math.Max(limit * 4, limit))
                : -1;

            using var command = connection.CreateCommand();
            // 下面的名称排序只影响批量预览先后，不参与建立模型-动画关系。
            // 候选本身仍然必须来自 Unity 显式关系，避免把动作名猜测混进素材库绑定结果。
            command.CommandText = @"
WITH ready_models AS (
SELECT m.raw_json AS raw_json
     , m.output AS output
     , m.name AS name
FROM assets m
WHERE m.kind='Model'
  AND ($hasModelFilter = 0 OR m.output IN (SELECT output FROM temp_unity_bake_models))
  AND (
    $hasSuppliedAvatar = 1
    OR m.output IN (SELECT output FROM temp_unity_bake_avatar_ready_models)
  )
ORDER BY m.name COLLATE NOCASE
LIMIT $readyModelLimit
),
candidate_keys AS (
SELECT c.model_output
     , c.animation_output
     , c.score AS candidate_score
     , m.name AS model_name
     , a.name AS animation_name
     , 0 AS dynamic_rank
     , CASE
         WHEN a.name LIKE '%Standby%' OR a.name LIKE '%Idle%' OR a.name LIKE '%Loop%' OR a.name LIKE '%Sync%' THEN 1
         ELSE 0
       END AS static_name_rank
     , CASE
         WHEN a.name LIKE '%Run%' OR a.name LIKE '%Walk%' OR a.name LIKE '%Sprint%' OR a.name LIKE '%Jump%' OR a.name LIKE '%Climb%'
              OR a.name LIKE '%Move%' OR a.name LIKE '%Cycle%' OR a.name LIKE '%Locomotion%' THEN 0
         WHEN a.name LIKE '%Attack%' OR a.name LIKE '%Skill%' OR a.name LIKE '%Dash%' OR a.name LIKE '%Dodge%' OR a.name LIKE '%Hit%'
              OR a.name LIKE '%Cast%' OR a.name LIKE '%Shoot%' OR a.name LIKE '%Aim%' THEN 1
         ELSE 2
       END AS body_motion_name_rank
     , CASE
         WHEN a.name LIKE '%+Mirror%' THEN 1
         ELSE 0
       END AS mirror_name_rank
     , ROW_NUMBER() OVER (
           PARTITION BY c.model_output
           ORDER BY
             CASE
               WHEN a.name LIKE '%Standby%' OR a.name LIKE '%Idle%' OR a.name LIKE '%Loop%' OR a.name LIKE '%Sync%' THEN 1
               ELSE 0
             END ASC,
             CASE
               WHEN a.name LIKE '%Run%' OR a.name LIKE '%Walk%' OR a.name LIKE '%Sprint%' OR a.name LIKE '%Jump%' OR a.name LIKE '%Climb%'
                    OR a.name LIKE '%Move%' OR a.name LIKE '%Cycle%' OR a.name LIKE '%Locomotion%' THEN 0
               WHEN a.name LIKE '%Attack%' OR a.name LIKE '%Skill%' OR a.name LIKE '%Dash%' OR a.name LIKE '%Dodge%' OR a.name LIKE '%Hit%'
                    OR a.name LIKE '%Cast%' OR a.name LIKE '%Shoot%' OR a.name LIKE '%Aim%' THEN 1
               ELSE 2
             END ASC,
             CASE
               WHEN a.name LIKE '%+Mirror%' THEN 1
               ELSE 0
             END ASC,
             c.score DESC,
             COALESCE(json_extract(c.raw_json, '$.missingInternalHumanoidSolver'), 0) ASC,
             a.name COLLATE NOCASE
       ) AS model_rank
FROM ready_models m
JOIN model_animation_candidates c INDEXED BY idx_model_animation_candidates_source_model
  ON c.relation_source='explicit' AND c.model_output=m.output
JOIN assets a ON a.kind='Animation' AND a.output=c.animation_output
WHERE (
    " + HumanoidBakeCandidateSql("c") + @"
  )
  AND ($hasAnimationFilter = 0 OR c.animation_output IN (SELECT output FROM temp_unity_bake_animations))
" + skipBakedCacheSql + @"
)
SELECT m.raw_json AS model_raw_json
     , a.raw_json AS animation_raw_json
     , c.raw_json AS relation_raw_json
     , k.model_output
     , k.animation_output
FROM candidate_keys k
JOIN ready_models m ON m.output=k.model_output
JOIN model_animation_candidates c INDEXED BY idx_model_animation_candidates_source_model
  ON c.relation_source='explicit' AND c.model_output=k.model_output AND c.animation_output=k.animation_output
JOIN assets a ON a.kind='Animation' AND a.output=k.animation_output
WHERE ($diversifyModels = 0 OR k.model_rank <= $modelRankLimit)
ORDER BY
  CASE WHEN $diversifyModels = 1 THEN k.model_name END COLLATE NOCASE,
  CASE WHEN $diversifyModels = 1 THEN k.dynamic_rank END ASC,
  CASE WHEN $diversifyModels = 1 THEN k.static_name_rank END ASC,
  CASE WHEN $diversifyModels = 1 THEN k.body_motion_name_rank END ASC,
  CASE WHEN $diversifyModels = 1 THEN k.mirror_name_rank END ASC,
  CASE WHEN $diversifyModels = 1 THEN k.animation_name END COLLATE NOCASE,
  CASE WHEN $diversifyModels = 0 THEN k.dynamic_rank END ASC,
  CASE WHEN $diversifyModels = 0 THEN k.static_name_rank END ASC,
  CASE WHEN $diversifyModels = 0 THEN k.body_motion_name_rank END ASC,
  CASE WHEN $diversifyModels = 0 THEN k.mirror_name_rank END ASC,
  CASE WHEN $diversifyModels = 0 THEN k.candidate_score END DESC,
  CASE WHEN $diversifyModels = 0 THEN COALESCE(json_extract(c.raw_json, '$.missingInternalHumanoidSolver'), 0) END ASC,
  k.model_name COLLATE NOCASE,
  k.animation_name COLLATE NOCASE
LIMIT $queryLimit;";
            command.Parameters.AddWithValue("$hasModelFilter", modelOutputs == null ? 0 : 1);
            command.Parameters.AddWithValue("$hasAnimationFilter", animationOutputs == null ? 0 : 1);
            command.Parameters.AddWithValue("$queryLimit", queryLimit);
            command.Parameters.AddWithValue("$diversifyModels", diversifyModels ? 1 : 0);
            command.Parameters.AddWithValue("$modelRankLimit", modelRankLimit);
            command.Parameters.AddWithValue("$hasSuppliedAvatar", allowSuppliedAvatarAsset ? 1 : 0);
            command.Parameters.AddWithValue("$readyModelLimit", readyModelLimit);

            var result = new List<PreviewSelection>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var model = JObject.Parse(reader.GetString(0));
                var animation = JObject.Parse(reader.GetString(1));
                var relation = JObject.Parse(reader.GetString(2));
                var modelOutput = reader.GetString(3);
                var animationOutput = reader.GetString(4);

                if (!Matches(modelSelector, (string)model["name"], modelOutput, (string)model["output"]))
                {
                    continue;
                }
                if (!MatchesAny(animationSelector, (string)animation["name"], animationOutput, (string)animation["output"]))
                {
                    continue;
                }

                // 生产 bake 只允许从候选表带出的显式关系进入，避免手工拼错模型和动画。
                // 关系 raw_json 里有 AnimatorController/状态机上下文，request 必须保留下来，
                // 否则会把“状态机里的 clip”误当成普通独立动画。
                MergeExplicitRelationIntoAnimation(animation, relation);
                animation = ResolveAnimatorControllerBaseLayerAnimation(connection, animation, relation, libraryRoot) ?? animation;
                ResolveBatchCandidatePaths(model, animation, libraryRoot);
                if (!allowSuppliedAvatarAsset
                    && !ModelHasBakeReadyAvatar(model)
                    && string.IsNullOrWhiteSpace(ResolveUnityAvatarAsset(model, importedAvatarAssets))
                    && string.IsNullOrWhiteSpace(ResolveCandidateImportedUnityAvatarAssetDetails(animation, importedAvatarAssets).AssetPath)
                    && string.IsNullOrWhiteSpace(ResolveCandidateUnityAvatarAsset(animation)))
                {
                    continue;
                }
                if (!File.Exists((string)model["output"]) || !File.Exists((string)animation["output"]))
                {
                    continue;
                }

                result.Add(new PreviewSelection(
                    new JObject
                    {
                        ["model"] = model,
                        ["candidateCount"] = 1,
                        ["candidates"] = new JArray(animation),
                    },
                    animation));
            }

            return diversifyModels
                ? DiversifySelectionsByModel(result, Math.Max(1, limit))
                : result.Take(Math.Max(1, limit));
        }

        private static JObject ResolveAnimatorControllerBaseLayerAnimation(
            SqliteConnection connection,
            JObject requestedAnimation,
            JObject relation,
            string libraryRoot)
        {
            var animatorControllerContext = relation?["animatorControllerContext"] as JObject;
            var baseLayerClip = animatorControllerContext?["baseLayerClip"] as JObject;
            var baseClip = baseLayerClip?["clip"] as JObject;
            var basePathId = (long?)baseClip?["pathId"];
            if (basePathId == null)
            {
                return null;
            }

            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT output, raw_json
FROM assets
WHERE kind='Animation'
  AND json_extract(raw_json, '$.pathId')=$pathId
ORDER BY name COLLATE NOCASE
LIMIT 1;";
            command.Parameters.AddWithValue("$pathId", basePathId.Value);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            var baseOutput = reader.IsDBNull(0) ? null : reader.GetString(0);
            var baseAnimation = JObject.Parse(reader.GetString(1));
            baseAnimation["relation"] = (string)relation["relation"] ?? (string)relation["relationSource"] ?? "library.sqlite.candidate";
            baseAnimation["relationSource"] = (string)relation["relationSource"] ?? "explicit";
            baseAnimation["confidence"] = (string)relation["confidence"] ?? "explicit_unity_source_index";
            baseAnimation["score"] = (int?)relation["score"] ?? 100;
            baseAnimation["candidate"] = relation;
            baseAnimation["requiresHumanoidBake"] = true;
            baseAnimation["animatorControllerContext"] = relation["animatorControllerContext"]?.DeepClone();
            baseAnimation["animatorControllerRequestedClip"] = new JObject
            {
                ["name"] = requestedAnimation?["name"],
                ["output"] = requestedAnimation?["output"],
                ["pathId"] = requestedAnimation?["pathId"],
                ["reason"] = "Selected clip is an AnimatorController auxiliary layer; baseLayerClip is used as the deterministic body-driving AnimationClip.",
            };
            if (!string.IsNullOrWhiteSpace(baseOutput))
            {
                baseAnimation["output"] = ResolveSourcePath(baseOutput, libraryRoot);
            }

            return baseAnimation;
        }

        private static void MergeExplicitRelationIntoAnimation(JObject animation, JObject relation)
        {
            if (animation == null || relation == null)
            {
                return;
            }

            animation["relation"] = (string)relation["relation"] ?? (string)relation["relationSource"] ?? "library.sqlite.candidate";
            animation["relationSource"] = (string)relation["relationSource"] ?? "explicit";
            animation["confidence"] = (string)relation["confidence"] ?? "explicit_unity_source_index";
            animation["score"] = (int?)relation["score"] ?? 100;
            animation["candidate"] = relation;
            animation["requiresHumanoidBake"] = true;

            foreach (var name in ExplicitRelationFieldsForRequest)
            {
                if (relation[name] != null)
                {
                    animation[name] = relation[name].DeepClone();
                }
            }
        }

        private static readonly string[] ExplicitRelationFieldsForRequest =
        {
            "animationAsset",
            "legacyUnityBakeSupported",
            "requiresUnityBake",
            "directGltfAnimationReady",
            "directGltfAnimationStatus",
            "needsDirectTrsAnimation",
            "deprecatedUnityBakeOnly",
            "directHumanoidTrsRequired",
            "unityBakeAcceleratedReady",
            "unityBakeAcceleratedBlockedReason",
            "directAnimationBlocked",
            "directAnimationBlockedReason",
            "productionAnimationReady",
            "productionAnimationBlockedReason",
            "productionAnimationPath",
            "fullHumanoidBakeRequired",
            "productionUnityBakeReady",
            "productionUnityBakeBlocked",
            "productionUnityBakeBlockedReason",
            "fullHumanoidBakeBlocked",
            "fullHumanoidBakeBlockedReason",
            "standaloneBodyBakeReady",
            "standaloneBodyBakeStatus",
            "standaloneBodyBakeReason",
            "standaloneBodyRequiresAnimatorControllerContext",
            "standaloneBodyRequiresDirectTrsSolve",
            "relationEvidence",
            "animatorControllerContext",
            "animatorControllerBodyClipReady",
            "unityAvatarAsset",
            "unityAvatarMatchKey",
            "productionUnityBakeAvatarSource",
            "importedAvatarAssetValidated",
            "nextAction",
            "matchReason",
        };

        private static IEnumerable<PreviewSelection> DiversifySelectionsByModel(
            IReadOnlyList<PreviewSelection> selections,
            int limit)
        {
            if (selections == null || selections.Count == 0)
            {
                return Array.Empty<PreviewSelection>();
            }

            var queues = selections
                .GroupBy(x => CanonicalizeLibraryOutput((string)x.Model?["model"]?["output"], null), StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => (string)g.First().Model?["model"]?["name"], StringComparer.OrdinalIgnoreCase)
                .Select(g => new Queue<PreviewSelection>(g))
                .ToList();
            var result = new List<PreviewSelection>(Math.Max(1, limit));
            while (result.Count < limit && queues.Count > 0)
            {
                for (var i = 0; i < queues.Count && result.Count < limit;)
                {
                    var queue = queues[i];
                    result.Add(queue.Dequeue());
                    if (queue.Count == 0)
                    {
                        queues.RemoveAt(i);
                    }
                    else
                    {
                        i++;
                    }
                }
            }

            return result;
        }

        private static List<string> SelectMatchingAssetOutputs(SqliteConnection connection, string kind, string selector, int limit)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                return null;
            }

            var result = new List<string>();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT name, output
FROM assets
WHERE kind = $kind
  AND output IS NOT NULL
  AND output <> ''
ORDER BY name COLLATE NOCASE
LIMIT 200000;";
            command.Parameters.AddWithValue("$kind", kind);
            using var reader = command.ExecuteReader();
            while (reader.Read() && result.Count < Math.Max(1, limit))
            {
                var name = reader.IsDBNull(0) ? null : reader.GetString(0);
                var output = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (MatchesAny(selector, name, output))
                {
                    result.Add(output);
                }
            }
            return result;
        }

        private static void TryWriteBakeCache(string dbPath, JArray items, string libraryRoot)
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={dbPath}");
                connection.Open();
                EnsureBakeCacheTable(connection);
                using var transaction = connection.BeginTransaction();
                foreach (var item in items.OfType<JObject>())
                {
                    UpsertBakeCache(connection, transaction, item, libraryRoot);
                }
                transaction.Commit();
                Logger.Info($"SQLite animation_bake_cache updated: {dbPath}; rows={items.Count}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Unable to update animation_bake_cache in {dbPath}: {ex.Message}");
            }
        }

        private static void EnsureBakeCacheTable(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
CREATE TABLE IF NOT EXISTS animation_bake_cache (
    model_output TEXT NOT NULL,
    animation_output TEXT NOT NULL,
    status TEXT NOT NULL,
    request_path TEXT,
    result_path TEXT,
    baked_gltf_path TEXT,
    baked_fbx_path TEXT,
    message TEXT,
    bake_mode TEXT,
    solve_path TEXT,
    production_status TEXT,
    requires_visual_validation INTEGER,
    writes_reusable_gltf_trs_candidate INTEGER,
    animation_solve_json TEXT,
    updated_utc TEXT,
    PRIMARY KEY(model_output, animation_output)
);";
            command.ExecuteNonQuery();
            EnsureBakeCacheColumn(connection, "solve_path", "TEXT");
            EnsureBakeCacheColumn(connection, "production_status", "TEXT");
            EnsureBakeCacheColumn(connection, "requires_visual_validation", "INTEGER");
            EnsureBakeCacheColumn(connection, "writes_reusable_gltf_trs_candidate", "INTEGER");
            EnsureBakeCacheColumn(connection, "animation_solve_json", "TEXT");
        }

        private static void EnsureBakeCacheColumn(SqliteConnection connection, string columnName, string sqlType)
        {
            using var check = connection.CreateCommand();
            check.CommandText = "PRAGMA table_info(animation_bake_cache);";
            using (var reader = check.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
            }

            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE animation_bake_cache ADD COLUMN {columnName} {sqlType};";
            alter.ExecuteNonQuery();
        }

        private static void TryCompactBakeCache(string dbPath, string libraryRoot)
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={dbPath}");
                connection.Open();
                if (!BakeCacheTableExists(connection))
                {
                    return;
                }

                var rows = ReadBakeCacheRows(connection, libraryRoot);
                var missingAssetRowIds = rows
                    .Where(x => !BakeCacheRowAssetsExist(x, libraryRoot)
                        && !string.Equals(x.Status, "baked", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(x.Status, "static_pose", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(x.Status, "needs_review", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(x.Status, "needs_animator_controller_context", StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.RowId)
                    .ToHashSet();
                var requestOnlyRowIds = rows
                    .Where(x => string.Equals(x.Status, "request_written", StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.RowId)
                    .ToHashSet();
                var deleteRowIds = rows
                    .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Where(x => x.Count() > 1)
                    .SelectMany(group =>
                    {
                        var keep = group
                            .OrderByDescending(BakeCacheRowPriority)
                            .ThenByDescending(x => x.IsCanonical)
                            .ThenByDescending(x => x.UpdatedUtc)
                            .ThenByDescending(x => x.RowId)
                            .First();
                        return group
                            .Where(x => x.RowId != keep.RowId)
                            .Select(x => x.RowId);
                    })
                    .Concat(missingAssetRowIds)
                    .Concat(requestOnlyRowIds)
                    .Distinct()
                    .ToArray();
                if (deleteRowIds.Length == 0)
                {
                    return;
                }

                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM animation_bake_cache WHERE rowid=$rowid;";
                var rowIdParameter = command.Parameters.Add("$rowid", SqliteType.Integer);
                foreach (var rowId in deleteRowIds)
                {
                    rowIdParameter.Value = rowId;
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
                Logger.Info($"SQLite animation_bake_cache compacted: deleted={deleteRowIds.Length}, missingAssetRows={missingAssetRowIds.Count}, requestOnlyRows={requestOnlyRowIds.Count}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Unable to compact animation_bake_cache in {dbPath}: {ex.Message}");
            }
        }

        private static bool BakeCacheRowAssetsExist(BakeCacheRow row, string libraryRoot)
        {
            var parts = (row.Key ?? string.Empty).Split('|');
            if (parts.Length != 2)
            {
                return false;
            }

            var modelPath = ResolveLibraryPath(parts[0], libraryRoot);
            var animationPath = ResolveLibraryPath(parts[1], libraryRoot);
            return !string.IsNullOrWhiteSpace(modelPath)
                && !string.IsNullOrWhiteSpace(animationPath)
                && File.Exists(modelPath)
                && File.Exists(animationPath);
        }

        private static List<BakeCacheRow> ReadBakeCacheRows(SqliteConnection connection, string libraryRoot)
        {
            var result = new List<BakeCacheRow>();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT rowid, model_output, animation_output, status, baked_gltf_path, updated_utc
FROM animation_bake_cache;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var modelOutput = reader.IsDBNull(1) ? null : reader.GetString(1);
                var animationOutput = reader.IsDBNull(2) ? null : reader.GetString(2);
                if (string.IsNullOrWhiteSpace(modelOutput) || string.IsNullOrWhiteSpace(animationOutput))
                {
                    continue;
                }

                var canonicalModel = CanonicalizeLibraryOutput(modelOutput, libraryRoot);
                var canonicalAnimation = CanonicalizeLibraryOutput(animationOutput, libraryRoot);
                result.Add(new BakeCacheRow(
                    reader.GetInt64(0),
                    canonicalModel + "|" + canonicalAnimation,
                    string.Equals(NormalizeLibraryOutput(modelOutput), canonicalModel, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(NormalizeLibraryOutput(animationOutput), canonicalAnimation, StringComparison.OrdinalIgnoreCase),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    ParseUtc(reader.IsDBNull(5) ? null : reader.GetString(5))));
            }
            return result;
        }

        private static int BakeCacheRowPriority(BakeCacheRow row)
        {
            if (string.Equals(row.Status, "baked", StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(row.BakedGltfPath) ? 80 : 100;
            }
            if (string.Equals(row.Status, "static_pose", StringComparison.OrdinalIgnoreCase)
                || string.Equals(row.Status, "needs_review", StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(row.BakedGltfPath) ? 70 : 90;
            }
            if (string.Equals(row.Status, "needs_animator_controller_context", StringComparison.OrdinalIgnoreCase))
            {
                return 70;
            }
            if (string.Equals(row.Status, "request_written", StringComparison.OrdinalIgnoreCase))
            {
                return 50;
            }
            if (string.Equals(row.Status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                return 20;
            }
            return 10;
        }

        private static DateTime ParseUtc(string value)
        {
            return DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var result)
                    ? result
                    : DateTime.MinValue;
        }

        private static void TryWriteBakeCacheSummary(string dbPath, string outputDirectory, string libraryRoot, bool fullScan = false, string unityProject = null)
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                connection.Open();
                if (!BakeCacheTableExists(connection))
                {
                    return;
                }

                if (!fullScan)
                {
                    WriteFastBakeCacheSummary(connection, dbPath, outputDirectory, libraryRoot);
                    return;
                }

                var importedAvatarAssets = DiscoverImportedAvatarAssets(unityProject, libraryRoot);
                var importedAvatarAssetTrustedFileCount = importedAvatarAssets.Values
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                var importedAvatarReadiness = BuildImportedAvatarReadinessSummary(unityProject, libraryRoot, importedAvatarAssets);
                var importedAvatarAssetFileCount = (long?)importedAvatarReadiness["fileCount"] ?? importedAvatarAssetTrustedFileCount;
                CreateTempImportedAvatarAssetKeys(connection, importedAvatarAssets);
                CreateTempExplicitUnityBakeCandidateTable(connection);
                var explicitUnityBakeCandidates = ScalarLong(connection, @"
SELECT COUNT(*)
FROM temp_explicit_unity_bake_candidates;");
                var uniqueExplicitUnityBakeCandidates = ScalarLong(connection, @"
SELECT COUNT(*)
FROM (
  SELECT DISTINCT model_output, animation_output
  FROM temp_explicit_unity_bake_candidates
);");
                var bakeReadyExplicitUnityBakeCandidates = ScalarLong(connection, @"
SELECT COUNT(*)
FROM temp_explicit_unity_bake_candidates
WHERE bake_ready_avatar=1;");
                var uniqueBakeReadyExplicitUnityBakeCandidates = ScalarLong(connection, @"
SELECT COUNT(*)
FROM (
  SELECT DISTINCT model_output, animation_output
  FROM temp_explicit_unity_bake_candidates
  WHERE bake_ready_avatar=1
);");
                var importedAvatarAssetBakeReadyExplicitUnityBakeCandidates = ScalarLong(connection, @"
SELECT COUNT(*)
FROM temp_explicit_unity_bake_candidates
WHERE imported_avatar_ready=1;");
                var uniqueImportedAvatarAssetBakeReadyExplicitUnityBakeCandidates = ScalarLong(connection, @"
SELECT COUNT(*)
FROM (
  SELECT DISTINCT model_output, animation_output
  FROM temp_explicit_unity_bake_candidates
  WHERE imported_avatar_ready=1
);");
                var effectiveBakeReadyExplicitUnityBakeCandidates = ScalarLong(connection, @"
SELECT COUNT(*)
FROM temp_explicit_unity_bake_candidates
WHERE bake_ready_avatar=1 OR imported_avatar_ready=1;");
                var uniqueEffectiveBakeReadyExplicitUnityBakeCandidates = ScalarLong(connection, @"
SELECT COUNT(*)
FROM (
  SELECT DISTINCT model_output, animation_output
  FROM temp_explicit_unity_bake_candidates
  WHERE bake_ready_avatar=1 OR imported_avatar_ready=1
);");
                var bakeReadyCacheWhere = BuildBakeReadyCacheWhere("bc");
                var cachedCandidates = ScalarLong(connection, $@"
SELECT COUNT(*)
FROM animation_bake_cache bc
WHERE {bakeReadyCacheWhere};");
                var requestWrittenCandidates = ScalarLong(connection, $@"
SELECT COUNT(*)
FROM animation_bake_cache bc
WHERE bc.status='request_written'
  AND {bakeReadyCacheWhere};");
                var bakedCandidates = ScalarLong(connection, $@"
SELECT COUNT(*)
FROM animation_bake_cache bc
WHERE bc.status='baked'
                AND {bakeReadyCacheWhere};");
                var trustedBakedCandidates = CountTrustedBakedCacheRows(connection, libraryRoot);
                var staticPoseCandidates = CountStaticPoseCacheRows(connection, libraryRoot);
                var untrustedBakedCandidates = CountUntrustedBakedCacheRows(connection, libraryRoot);
                var bakedMissingGltfCandidates = untrustedBakedCandidates;
                var failedCandidates = CountUntrustedFailedCacheRows(connection, libraryRoot);
                var uniqueCounts = BuildUniqueBakeCacheCounts(connection, libraryRoot, effectiveBakeReadyExplicitUnityBakeCandidates, uniqueEffectiveBakeReadyExplicitUnityBakeCandidates);
                var summary = new JObject
                {
                    ["generatedAt"] = DateTime.UtcNow.ToString("O"),
                    ["libraryRoot"] = libraryRoot,
                    ["libraryIndex"] = dbPath,
                    ["rule"] = "Only relation_source=explicit Humanoid/Muscle candidates are counted for Unity bake production.",
                    ["explicitUnityBakeCandidates"] = explicitUnityBakeCandidates,
                    ["uniqueExplicitUnityBakeCandidates"] = uniqueExplicitUnityBakeCandidates,
                    ["bakeReadyExplicitUnityBakeCandidates"] = bakeReadyExplicitUnityBakeCandidates,
                    ["uniqueBakeReadyExplicitUnityBakeCandidates"] = uniqueBakeReadyExplicitUnityBakeCandidates,
                    ["bakeReadyExplicitUnityBakeCoverage"] = Ratio(bakeReadyExplicitUnityBakeCandidates, explicitUnityBakeCandidates),
                    ["uniqueBakeReadyExplicitUnityBakeCoverage"] = Ratio(uniqueBakeReadyExplicitUnityBakeCandidates, uniqueExplicitUnityBakeCandidates),
                    ["bakeReadyExplicitUnityBakeCoveragePercent"] = Percent(bakeReadyExplicitUnityBakeCandidates, explicitUnityBakeCandidates),
                    ["uniqueBakeReadyExplicitUnityBakeCoveragePercent"] = Percent(uniqueBakeReadyExplicitUnityBakeCandidates, uniqueExplicitUnityBakeCandidates),
                    ["importedAvatarAssetFileCount"] = importedAvatarAssetFileCount,
                    ["importedAvatarAssetTrustedFileCount"] = (long?)importedAvatarReadiness["trustedFileCount"] ?? importedAvatarAssetTrustedFileCount,
                    ["importedAvatarAssetKeyCount"] = importedAvatarAssets.Count,
                    ["importedAvatarProbeReportPath"] = (string)importedAvatarReadiness["probeReportPath"],
                    ["importedAvatarProbeFreshness"] = (string)importedAvatarReadiness["probeFreshness"],
                    ["importedAvatarProbeEnforced"] = (bool?)importedAvatarReadiness["probeEnforced"] ?? false,
                    ["importedAvatarProbeValidHumanAvatars"] = (long?)importedAvatarReadiness["probeValidHumanAvatars"] ?? 0,
                    ["importedAvatarProbeInvalidAssets"] = (long?)importedAvatarReadiness["probeInvalidAssets"] ?? 0,
                    ["importedAvatarProbeError"] = (string)importedAvatarReadiness["probeError"],
                    ["importedAvatarAssetBakeReadyExplicitUnityBakeCandidates"] = importedAvatarAssetBakeReadyExplicitUnityBakeCandidates,
                    ["uniqueImportedAvatarAssetBakeReadyExplicitUnityBakeCandidates"] = uniqueImportedAvatarAssetBakeReadyExplicitUnityBakeCandidates,
                    ["importedAvatarAssetBakeReadyExplicitUnityBakeCoverage"] = Ratio(importedAvatarAssetBakeReadyExplicitUnityBakeCandidates, explicitUnityBakeCandidates),
                    ["uniqueImportedAvatarAssetBakeReadyExplicitUnityBakeCoverage"] = Ratio(uniqueImportedAvatarAssetBakeReadyExplicitUnityBakeCandidates, uniqueExplicitUnityBakeCandidates),
                    ["importedAvatarAssetBakeReadyExplicitUnityBakeCoveragePercent"] = Percent(importedAvatarAssetBakeReadyExplicitUnityBakeCandidates, explicitUnityBakeCandidates),
                    ["uniqueImportedAvatarAssetBakeReadyExplicitUnityBakeCoveragePercent"] = Percent(uniqueImportedAvatarAssetBakeReadyExplicitUnityBakeCandidates, uniqueExplicitUnityBakeCandidates),
                    ["effectiveBakeReadyExplicitUnityBakeCandidates"] = effectiveBakeReadyExplicitUnityBakeCandidates,
                    ["uniqueEffectiveBakeReadyExplicitUnityBakeCandidates"] = uniqueEffectiveBakeReadyExplicitUnityBakeCandidates,
                    ["effectiveBakeReadyExplicitUnityBakeCoverage"] = Ratio(effectiveBakeReadyExplicitUnityBakeCandidates, explicitUnityBakeCandidates),
                    ["uniqueEffectiveBakeReadyExplicitUnityBakeCoverage"] = Ratio(uniqueEffectiveBakeReadyExplicitUnityBakeCandidates, uniqueExplicitUnityBakeCandidates),
                    ["effectiveBakeReadyExplicitUnityBakeCoveragePercent"] = Percent(effectiveBakeReadyExplicitUnityBakeCandidates, explicitUnityBakeCandidates),
                    ["uniqueEffectiveBakeReadyExplicitUnityBakeCoveragePercent"] = Percent(uniqueEffectiveBakeReadyExplicitUnityBakeCandidates, uniqueExplicitUnityBakeCandidates),
                    ["cachedCandidates"] = cachedCandidates,
                    ["requestWrittenCandidates"] = requestWrittenCandidates,
                    ["bakedCandidates"] = bakedCandidates,
                    ["trustedBakedCandidates"] = trustedBakedCandidates,
                    ["staticPoseCandidates"] = staticPoseCandidates,
                    ["untrustedBakedCandidates"] = untrustedBakedCandidates,
                    ["bakedMissingGltfCandidates"] = bakedMissingGltfCandidates,
                    ["failedCandidates"] = failedCandidates,
                    ["cacheCoverage"] = Ratio(cachedCandidates, effectiveBakeReadyExplicitUnityBakeCandidates),
                    ["bakedCoverage"] = Ratio(bakedCandidates, effectiveBakeReadyExplicitUnityBakeCandidates),
                    ["trustedBakedCoverage"] = Ratio(trustedBakedCandidates, effectiveBakeReadyExplicitUnityBakeCandidates),
                    ["cacheCoveragePercent"] = Percent(cachedCandidates, effectiveBakeReadyExplicitUnityBakeCandidates),
                    ["bakedCoveragePercent"] = Percent(bakedCandidates, effectiveBakeReadyExplicitUnityBakeCandidates),
                    ["trustedBakedCoveragePercent"] = Percent(trustedBakedCandidates, effectiveBakeReadyExplicitUnityBakeCandidates),
                    ["statusCounts"] = QueryGroupedCounts(connection, $@"
SELECT COALESCE(status, '<null>') AS key, COUNT(*) AS count
FROM animation_bake_cache bc
WHERE {bakeReadyCacheWhere}
GROUP BY COALESCE(status, '<null>')
ORDER BY count DESC;"),
                    ["effectiveStatusCounts"] = BuildEffectiveBakeCacheStatusCounts(connection, libraryRoot),
                };
                summary.Merge(uniqueCounts);

                var outputPath = Path.Combine(outputDirectory, "animation_bake_cache_summary.json");
                File.WriteAllText(outputPath, JsonConvert.SerializeObject(summary, Formatting.Indented));
                Logger.Info($"Animation bake cache summary: {outputPath}");

                var rootPath = Path.Combine(libraryRoot, "animation_bake_cache_summary.json");
                if (!string.Equals(Path.GetFullPath(outputPath), Path.GetFullPath(rootPath), StringComparison.OrdinalIgnoreCase))
                {
                    File.WriteAllText(rootPath, JsonConvert.SerializeObject(summary, Formatting.Indented));
                    Logger.Info($"Animation bake cache summary: {rootPath}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Unable to write animation_bake_cache_summary.json: {ex.Message}");
            }
        }

        private static void WriteFastBakeCacheSummary(SqliteConnection connection, string dbPath, string outputDirectory, string libraryRoot)
        {
            var rows = ReadBakeCacheRows(connection, libraryRoot);
            var statusCounts = rows
                .GroupBy(x => string.IsNullOrWhiteSpace(x.Status) ? "<null>" : x.Status, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x.Count())
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => new JObject
                {
                    ["key"] = x.Key,
                    ["count"] = x.Count(),
                });

            var effectiveCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                var key = EffectiveBakeCacheStatus(row.Status, row.BakedGltfPath, libraryRoot);
                effectiveCounts[key] = effectiveCounts.TryGetValue(key, out var current) ? current + 1 : 1;
            }

            var trustedBakedCandidates = rows.Count(x => IsTrustedBakedGltfPath(x.BakedGltfPath, libraryRoot));
            var staticPoseCandidates = rows.Count(x => IsStaticPoseBakedGltfPath(x.BakedGltfPath, libraryRoot));
            var needsReviewCandidates = rows.Count(x => IsNeedsReviewBakedGltfPath(x.BakedGltfPath, libraryRoot));
            var untrustedBakedCandidates = rows.Count(x => string.Equals(x.Status, "baked", StringComparison.OrdinalIgnoreCase)
                && !IsTrustedBakedGltfPath(x.BakedGltfPath, libraryRoot)
                && !IsStaticPoseBakedGltfPath(x.BakedGltfPath, libraryRoot)
                && !IsNeedsReviewBakedGltfPath(x.BakedGltfPath, libraryRoot));
            var summary = new JObject
            {
                ["generatedAt"] = DateTime.UtcNow.ToString("O"),
                ["libraryRoot"] = libraryRoot,
                ["libraryIndex"] = dbPath,
                ["summaryMode"] = "fast_cache_only",
                ["rule"] = "Fast batch summary reads animation_bake_cache only. Run --bake_animation_previews_from_library with --preview_validation_limit 0 for full relation_source=explicit coverage denominators.",
                ["cachedCandidates"] = rows.Count,
                ["trustedBakedCandidates"] = trustedBakedCandidates,
                ["staticPoseCandidates"] = staticPoseCandidates,
                ["needsReviewCandidates"] = needsReviewCandidates,
                ["untrustedBakedCandidates"] = untrustedBakedCandidates,
                ["statusCounts"] = new JArray(statusCounts),
                ["effectiveStatusCounts"] = new JArray(effectiveCounts
                    .OrderByDescending(x => x.Value)
                    .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new JObject
                    {
                        ["key"] = x.Key,
                        ["count"] = x.Value,
                    })),
            };

            WriteBakeCacheSummaryFiles(summary, outputDirectory, libraryRoot);
        }

        private static void WriteBakeCacheSummaryFiles(JObject summary, string outputDirectory, string libraryRoot)
        {
            var outputPath = Path.Combine(outputDirectory, "animation_bake_cache_summary.json");
            File.WriteAllText(outputPath, JsonConvert.SerializeObject(summary, Formatting.Indented));
            Logger.Info($"Animation bake cache summary: {outputPath}");

            var rootPath = Path.Combine(libraryRoot, "animation_bake_cache_summary.json");
            if (!string.Equals(Path.GetFullPath(outputPath), Path.GetFullPath(rootPath), StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(rootPath, JsonConvert.SerializeObject(summary, Formatting.Indented));
                Logger.Info($"Animation bake cache summary: {rootPath}");
            }
        }

        private static bool BakeCacheTableExists(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='animation_bake_cache';";
            return Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture) > 0;
        }

        private static long ScalarLong(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }

        private static JArray QueryGroupedCounts(SqliteConnection connection, string sql)
        {
            var result = new JArray();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new JObject
                {
                    ["key"] = reader.GetString(0),
                    ["count"] = reader.GetInt64(1),
                });
            }
            return result;
        }

        private static long CountTrustedBakedCacheRows(SqliteConnection connection, string libraryRoot)
        {
            var count = 0L;
            using var command = connection.CreateCommand();
            command.CommandText = $@"
SELECT bc.baked_gltf_path
FROM animation_bake_cache bc
WHERE COALESCE(bc.baked_gltf_path, '')<>''
  AND {BuildBakeReadyCacheWhere("bc")};";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (IsTrustedBakedGltfPath(reader.IsDBNull(0) ? null : reader.GetString(0), libraryRoot))
                {
                    count++;
                }
            }
            return count;
        }

        private static JArray BuildEffectiveBakeCacheStatusCounts(SqliteConnection connection, string libraryRoot)
        {
            var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            using var command = connection.CreateCommand();
            command.CommandText = $@"
SELECT bc.status, bc.baked_gltf_path
FROM animation_bake_cache bc
WHERE {BuildBakeReadyCacheWhere("bc")};";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var status = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var bakedGltfPath = reader.IsDBNull(1) ? null : reader.GetString(1);
                var key = EffectiveBakeCacheStatus(status, bakedGltfPath, libraryRoot);
                counts[key] = counts.TryGetValue(key, out var current) ? current + 1 : 1;
            }

            return new JArray(counts
                .OrderByDescending(x => x.Value)
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => new JObject
                {
                    ["key"] = x.Key,
                    ["count"] = x.Value,
                }));
        }

        private static string EffectiveBakeCacheStatus(string status, string bakedGltfPath, string libraryRoot)
        {
            if (IsTrustedBakedGltfPath(bakedGltfPath, libraryRoot))
            {
                return "trusted_baked";
            }
            if (IsStaticPoseBakedGltfPath(bakedGltfPath, libraryRoot))
            {
                return "static_pose";
            }
            if (IsNeedsReviewBakedGltfPath(bakedGltfPath, libraryRoot))
            {
                return "needs_review";
            }
            if (string.Equals(status, "needs_animator_controller_context", StringComparison.OrdinalIgnoreCase))
            {
                return "needs_animator_controller_context";
            }
            if (string.Equals(status, "baked", StringComparison.OrdinalIgnoreCase))
            {
                return "untrusted_baked";
            }
            return string.IsNullOrWhiteSpace(status) ? "<null>" : status;
        }

        private static long CountUntrustedFailedCacheRows(SqliteConnection connection, string libraryRoot)
        {
            var count = 0L;
            using var command = connection.CreateCommand();
            command.CommandText = $@"
SELECT bc.baked_gltf_path
FROM animation_bake_cache bc
WHERE bc.status='failed'
  AND {BuildBakeReadyCacheWhere("bc")};";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var bakedGltfPath = reader.IsDBNull(0) ? null : reader.GetString(0);
                if (!IsTrustedBakedGltfPath(bakedGltfPath, libraryRoot)
                    && !IsStaticPoseBakedGltfPath(bakedGltfPath, libraryRoot)
                    && !IsNeedsReviewBakedGltfPath(bakedGltfPath, libraryRoot))
                {
                    count++;
                }
            }
            return count;
        }

        private static long CountUntrustedBakedCacheRows(SqliteConnection connection, string libraryRoot)
        {
            var count = 0L;
            using var command = connection.CreateCommand();
            command.CommandText = $@"
SELECT bc.baked_gltf_path
FROM animation_bake_cache bc
WHERE bc.status='baked'
  AND {BuildBakeReadyCacheWhere("bc")};";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var bakedGltfPath = reader.IsDBNull(0) ? null : reader.GetString(0);
                if (!IsTrustedBakedGltfPath(bakedGltfPath, libraryRoot)
                    && !IsStaticPoseBakedGltfPath(bakedGltfPath, libraryRoot)
                    && !IsNeedsReviewBakedGltfPath(bakedGltfPath, libraryRoot))
                {
                    count++;
                }
            }
            return count;
        }

        private static long CountStaticPoseCacheRows(SqliteConnection connection, string libraryRoot)
        {
            var count = 0L;
            using var command = connection.CreateCommand();
            command.CommandText = $@"
SELECT bc.baked_gltf_path
FROM animation_bake_cache bc
WHERE COALESCE(bc.baked_gltf_path, '')<>''
  AND {BuildBakeReadyCacheWhere("bc")};";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (IsStaticPoseBakedGltfPath(reader.IsDBNull(0) ? null : reader.GetString(0), libraryRoot))
                {
                    count++;
                }
            }
            return count;
        }

        private static bool IsTrustedBakedGltfPath(string bakedGltfPath, string libraryRoot)
        {
            var bakedGltf = ResolveLibraryPath(bakedGltfPath, libraryRoot);
            if (string.IsNullOrWhiteSpace(bakedGltf) || !File.Exists(bakedGltf))
            {
                return false;
            }

            var reportPath = Path.Combine(Path.GetDirectoryName(bakedGltf) ?? string.Empty, "unity_bake_apply_report.json");
            if (!File.Exists(reportPath))
            {
                return false;
            }

            try
            {
                var report = JObject.Parse(File.ReadAllText(reportPath));
                var status = (string)report["status"];
                if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(status, "warning", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return (int?)report["frameVaryingTracks"] > 0
                    && !UsesFirstSampleHumanoidDelta(report)
                    && HasReusableAnimationSolve(report)
                    && IsTrustedAvatarBake(report)
                    && HasTrustedAnimationClipBake(report);
            }
            catch
            {
                return false;
            }
        }

        private static bool HasReusableAnimationSolve(JObject report)
        {
            var solve = report?["animationSolve"] as JObject;
            if (solve == null)
            {
                return true;
            }

            // batch 续跑和摘要也要听新版求解报告，不能只凭 glTF 存在和 Avatar 可信。
            return ((bool?)solve["writesReusableGltfTrsCandidate"] ?? false)
                && ((bool?)solve["productionReady"] ?? false)
                && !((bool?)solve["requiresVisualValidation"] ?? false);
        }

        private static bool IsStaticPoseBakedGltfPath(string bakedGltfPath, string libraryRoot)
        {
            var bakedGltf = ResolveLibraryPath(bakedGltfPath, libraryRoot);
            if (string.IsNullOrWhiteSpace(bakedGltf) || !File.Exists(bakedGltf))
            {
                return false;
            }

            var reportPath = Path.Combine(Path.GetDirectoryName(bakedGltf) ?? string.Empty, "unity_bake_apply_report.json");
            if (!File.Exists(reportPath))
            {
                return false;
            }

            try
            {
                var report = JObject.Parse(File.ReadAllText(reportPath));
                return string.Equals((string)report["status"], "static_pose", StringComparison.OrdinalIgnoreCase)
                    && ((int?)report["frameVaryingTracks"] ?? 0) == 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsNeedsReviewBakedGltfPath(string bakedGltfPath, string libraryRoot)
        {
            var bakedGltf = ResolveLibraryPath(bakedGltfPath, libraryRoot);
            if (string.IsNullOrWhiteSpace(bakedGltf) || !File.Exists(bakedGltf))
            {
                return false;
            }

            var reportPath = Path.Combine(Path.GetDirectoryName(bakedGltf) ?? string.Empty, "unity_bake_apply_report.json");
            if (!File.Exists(reportPath))
            {
                return false;
            }

            try
            {
                var report = JObject.Parse(File.ReadAllText(reportPath));
                return string.Equals((string)report["status"], "needs_review", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsTrustedAvatarBake(JObject report)
        {
            var avatarTrust = report["avatarTrust"] as JObject;
            if (avatarTrust == null)
            {
                return false;
            }

            var trusted = (bool?)avatarTrust["TrustedProductionBake"]
                ?? (bool?)avatarTrust["trustedProductionBake"]
                ?? false;
            if (!trusted)
            {
                return false;
            }

            var source = (string)avatarTrust["Source"] ?? (string)avatarTrust["source"];
            if (ReportRequestHasExplicitAvatarAsset(report))
            {
                return string.Equals(source, "imported_unity_avatar_asset", StringComparison.OrdinalIgnoreCase)
                    && ReportHasImportedAvatarAssetProof(report);
            }

            return IsProductionAvatarTrustSource(source);
        }

        private static bool ReportRequestHasExplicitAvatarAsset(JObject report)
        {
            if (!string.IsNullOrWhiteSpace((string)report?["unityBakeRequestedAvatarAsset"]))
            {
                return true;
            }

            var requestPath = (string)report?["request"];
            if (string.IsNullOrWhiteSpace(requestPath) || !File.Exists(requestPath))
            {
                return false;
            }

            try
            {
                var request = JObject.Parse(File.ReadAllText(requestPath));
                return !string.IsNullOrWhiteSpace((string)request["unityAssetPaths"]?["avatarAsset"]);
            }
            catch
            {
                return false;
            }
        }

        private static bool HasTrustedAnimationClipBake(JObject report)
        {
            if (!ReportRequestHasExplicitAvatarAsset(report))
            {
                return true;
            }

            // 走 ImportedAvatar oracle 时，clip 也必须是 Unity 工程内明确导入的原始 AnimationClip。
            // 旧的临时 .anim sidecar 容易把辅助片段当成完整身体动作，只能保留为诊断缓存。
            var source = (string)report?["unityBakeAnimationClipSource"];
            var importedClip = (string)report?["unityBakeImportedAnimationClip"];
            return string.Equals(source, "unityAssetPaths.animationClip", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(importedClip);
        }

        private static bool IsProductionAvatarTrustSource(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return false;
            }

            if (source.Contains("internal_solver", StringComparison.OrdinalIgnoreCase)
                || source.Contains("avatar_constant", StringComparison.OrdinalIgnoreCase)
                || source.Contains("oracle", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private static bool ReportHasImportedAvatarAssetProof(JObject report)
        {
            if ((bool?)report?["unityBakeImportedAvatarAssetValid"] ?? false)
            {
                return true;
            }

            return string.Equals((string)report?["unityBakeRigRestPoseSource"], "imported_unity_avatar_asset", StringComparison.OrdinalIgnoreCase)
                && ((bool?)report?["unityBakeRigRestPoseApplied"] ?? false);
        }

        private static bool UsesFirstSampleHumanoidDelta(JObject report)
        {
            return string.Equals(
                (string)report?["humanoidDeltaBase"],
                "first_sample",
                StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveLibraryPath(string path, string libraryRoot)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return Path.IsPathRooted(path)
                ? path
                : Path.Combine(libraryRoot ?? string.Empty, path);
        }

        private static JObject BuildUniqueBakeCacheCounts(
            SqliteConnection connection,
            string libraryRoot,
            long bakeReadyExplicitUnityBakeCandidates,
            long uniqueBakeReadyExplicitUnityBakeCandidates)
        {
            var groups = new Dictionary<string, UniqueBakeCacheEntry>(StringComparer.OrdinalIgnoreCase);
            using var command = connection.CreateCommand();
            command.CommandText = $@"
SELECT bc.model_output, bc.animation_output, bc.status, bc.baked_gltf_path, bc.message
FROM animation_bake_cache bc
WHERE {BuildBakeReadyCacheWhere("bc")};";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var modelOutput = reader.IsDBNull(0) ? null : reader.GetString(0);
                var animationOutput = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (string.IsNullOrWhiteSpace(modelOutput) || string.IsNullOrWhiteSpace(animationOutput))
                {
                    continue;
                }

                var key = CanonicalizeLibraryOutput(modelOutput, libraryRoot)
                    + "|"
                    + CanonicalizeLibraryOutput(animationOutput, libraryRoot);
                if (!groups.TryGetValue(key, out var entry))
                {
                    entry = new UniqueBakeCacheEntry();
                    groups[key] = entry;
                }

                entry.RowCount++;
                var status = reader.IsDBNull(2) ? null : reader.GetString(2);
                var bakedGltf = reader.IsDBNull(3) ? null : reader.GetString(3);
                var message = reader.IsDBNull(4) ? null : reader.GetString(4);
                var hasTrustedBakedGltf = IsTrustedBakedGltfPath(bakedGltf, libraryRoot);
                if (hasTrustedBakedGltf)
                {
                    entry.HasBaked = true;
                    entry.HasTrustedBaked = true;
                }
                else if (IsStaticPoseBakedGltfPath(bakedGltf, libraryRoot))
                {
                    entry.HasStaticPose = true;
                }
                else if (IsNeedsReviewBakedGltfPath(bakedGltf, libraryRoot))
                {
                    entry.HasNeedsReview = true;
                }
                else if (IsAnimatorControllerContextCacheStatus(status, message))
                {
                    entry.HasNeedsAnimatorControllerContext = true;
                }
                else if (string.Equals(status, "request_written", StringComparison.OrdinalIgnoreCase))
                {
                    entry.HasRequestWritten = true;
                }
                else if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    entry.HasFailed = true;
                }
                else if (string.Equals(status, "baked", StringComparison.OrdinalIgnoreCase))
                {
                    entry.HasBaked = true;
                    entry.HasBakedMissingGltf = true;
                }
            }

            var uniqueCachedCandidates = groups.Count;
            var uniqueTrustedBakedCandidates = groups.Values.Count(x => x.HasTrustedBaked);
            var uniqueBakedCandidates = groups.Values.Count(x => x.HasBaked);
            var uniqueBakedMissingGltfCandidates = groups.Values.Count(x => x.HasBaked && !x.HasTrustedBaked);
            var uniqueStaticPoseCandidates = groups.Values.Count(x => !x.HasTrustedBaked && x.HasStaticPose);
            var uniqueNeedsReviewCandidates = groups.Values.Count(x => !x.HasTrustedBaked && !x.HasStaticPose && x.HasNeedsReview);
            var uniqueAnimatorControllerContextCandidates = groups.Values.Count(x => !x.HasTrustedBaked && !x.HasStaticPose && !x.HasNeedsReview && x.HasNeedsAnimatorControllerContext);
            var uniqueFailedCandidates = groups.Values.Count(x => !x.HasBaked && !x.HasStaticPose && !x.HasNeedsReview && !x.HasNeedsAnimatorControllerContext && x.HasFailed);
            var uniqueRequestWrittenCandidates = groups.Values.Count(x => !x.HasBaked && !x.HasStaticPose && !x.HasNeedsReview && !x.HasNeedsAnimatorControllerContext && !x.HasFailed && x.HasRequestWritten);
            var duplicateCacheRows = groups.Values.Sum(x => Math.Max(0, x.RowCount - 1));
            var terminalDiagnosticCandidates = uniqueStaticPoseCandidates + uniqueNeedsReviewCandidates + uniqueAnimatorControllerContextCandidates;

            return new JObject
            {
                ["uniqueCachedCandidates"] = uniqueCachedCandidates,
                ["uniqueRequestWrittenCandidates"] = uniqueRequestWrittenCandidates,
                ["uniqueBakedCandidates"] = uniqueBakedCandidates,
                ["uniqueTrustedBakedCandidates"] = uniqueTrustedBakedCandidates,
                ["uniqueStaticPoseCandidates"] = uniqueStaticPoseCandidates,
                ["uniqueNeedsReviewCandidates"] = uniqueNeedsReviewCandidates,
                ["uniqueAnimatorControllerContextCandidates"] = uniqueAnimatorControllerContextCandidates,
                ["uniqueBakedMissingGltfCandidates"] = uniqueBakedMissingGltfCandidates,
                ["uniqueFailedCandidates"] = uniqueFailedCandidates,
                ["duplicateCacheRows"] = duplicateCacheRows,
                ["pendingUnityBakeCandidates"] = Math.Max(0, bakeReadyExplicitUnityBakeCandidates - uniqueTrustedBakedCandidates - terminalDiagnosticCandidates),
                ["uniquePendingUnityBakeCandidates"] = Math.Max(0, uniqueBakeReadyExplicitUnityBakeCandidates - uniqueTrustedBakedCandidates - terminalDiagnosticCandidates),
                ["uniqueCacheCoverage"] = Ratio(uniqueCachedCandidates, uniqueBakeReadyExplicitUnityBakeCandidates),
                ["uniqueTrustedBakedCoverage"] = Ratio(uniqueTrustedBakedCandidates, uniqueBakeReadyExplicitUnityBakeCandidates),
                ["uniqueCacheCoveragePercent"] = Percent(uniqueCachedCandidates, uniqueBakeReadyExplicitUnityBakeCandidates),
                ["uniqueTrustedBakedCoveragePercent"] = Percent(uniqueTrustedBakedCandidates, uniqueBakeReadyExplicitUnityBakeCandidates),
            };
        }

        private static double Ratio(long numerator, long denominator)
        {
            return denominator <= 0 ? 0 : Math.Round((double)numerator / denominator, 6);
        }

        private static double Percent(long numerator, long denominator)
        {
            return denominator <= 0 ? 0 : Math.Round((double)numerator * 100.0 / denominator, 6);
        }

        private static IReadOnlyDictionary<string, string> DiscoverImportedAvatarAssets(string unityProject, string libraryRoot = null)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(unityProject))
            {
                return result;
            }

            var directory = Path.Combine(unityProject, "Assets", "AnimeStudioBake", "ImportedAvatar");
            if (!Directory.Exists(directory))
            {
                return result;
            }

            var assetFiles = Directory.EnumerateFiles(directory, "*.asset", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .ToArray();
            var validProbeKeys = LoadFreshImportedAvatarProbeKeys(libraryRoot, assetFiles);
            if (assetFiles.Length > 0 && validProbeKeys == null)
            {
                return result;
            }
            foreach (var file in assetFiles)
            {
                var name = Path.GetFileNameWithoutExtension(file.FullName);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var unityPath = Path.GetRelativePath(unityProject, file.FullName).Replace('\\', '/');
                if (!unityPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (validProbeKeys != null
                    && !validProbeKeys.Contains(name)
                    && !(name.EndsWith("_ModelAvatar", StringComparison.OrdinalIgnoreCase)
                        && validProbeKeys.Contains(name[..^"_ModelAvatar".Length])))
                {
                    continue;
                }

                // 这些 key 来自真实导入的 Unity Avatar asset 文件名，只用于定位生产 bake 的 Avatar oracle。
                result[name] = unityPath;
                if (name.EndsWith("_ModelAvatar", StringComparison.OrdinalIgnoreCase))
                {
                    result[name[..^"_ModelAvatar".Length]] = unityPath;
                }
            }

            AddValidatedUnityAvatarSettingsAliases(result, LoadUnityBakeStringMap(libraryRoot, "unityAvatarAssets"));

            return result;
        }

        private static void AddValidatedUnityAvatarSettingsAliases(
            Dictionary<string, string> target,
            IReadOnlyDictionary<string, string> settingsMap)
        {
            if (target == null || target.Count == 0 || settingsMap == null || settingsMap.Count == 0)
            {
                return;
            }

            var validAssets = target.Values
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(NormalizeUnityAssetPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (validAssets.Count == 0)
            {
                return;
            }

            foreach (var pair in settingsMap)
            {
                var key = pair.Key?.Trim();
                var assetPath = NormalizeUnityAssetPath(pair.Value);
                if (string.IsNullOrWhiteSpace(key)
                    || string.IsNullOrWhiteSpace(assetPath)
                    || !validAssets.Contains(assetPath))
                {
                    continue;
                }

                // Alias 只指向已经通过 ImportedAvatar probe 的真实 Unity Avatar asset。
                target[key] = assetPath;
            }
        }

        private static IReadOnlyDictionary<string, string> DiscoverImportedAnimationClips(string unityProject, string libraryRoot = null)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddStringMap(result, LoadUnityBakeStringMap(libraryRoot, "unityAnimationClips"));
            if (string.IsNullOrWhiteSpace(unityProject))
            {
                return result;
            }

            var directory = Path.Combine(unityProject, "Assets", "AnimeStudioBake", "ImportedAnimationClip");
            if (!Directory.Exists(directory))
            {
                return result;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.anim", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var unityPath = Path.GetRelativePath(unityProject, file).Replace('\\', '/');
                if (!unityPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // ImportedAnimationClip 只用文件名精确匹配当前已经选中的 AnimationClip。
                // 它不参与模型-动画候选生成，也不做 contains 模糊匹配。
                result[name] = unityPath;
                result[Path.GetFileName(file)] = unityPath;
            }

            return result;
        }

        private static IReadOnlyDictionary<string, string> DiscoverImportedAnimatorControllers(string unityProject, string libraryRoot = null)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddStringMap(result, LoadUnityBakeStringMap(libraryRoot, "unityAnimatorControllers"));
            if (string.IsNullOrWhiteSpace(unityProject))
            {
                return result;
            }

            var directory = Path.Combine(unityProject, "Assets", "AnimeStudioBake", "ImportedAnimatorController");
            if (!Directory.Exists(directory))
            {
                return result;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.controller", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var unityPath = Path.GetRelativePath(unityProject, file).Replace('\\', '/');
                if (!unityPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 只登记显式导入目录里的原始/人工恢复 controller，不扫描 GeneratedAnimatorControllers。
                result[name] = unityPath;
                result[Path.GetFileName(file)] = unityPath;
            }

            return result;
        }

        private static IReadOnlyDictionary<string, string> LoadUnityBakeStringMap(string libraryRoot, string propertyName)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var localSettings = string.IsNullOrWhiteSpace(libraryRoot)
                ? null
                : LoadJsonObject(Path.Combine(libraryRoot, ".as_browser_cache", "unity_bake_settings.json"));
            var globalSettings = LoadJsonObject(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AnimeStudio",
                "LibraryBrowser",
                "settings.json"));

            AddStringMap(result, ReadStringMap(globalSettings, propertyName));
            AddStringMap(result, ReadStringMap(localSettings, propertyName));
            return result;
        }

        private static JObject LoadJsonObject(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                return JObject.Parse(File.ReadAllText(path));
            }
            catch
            {
                return null;
            }
        }

        private static IReadOnlyDictionary<string, string> ReadStringMap(JObject node, string propertyName)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (node?[propertyName] is not JObject map)
            {
                return result;
            }

            foreach (var pair in map.Properties())
            {
                var value = (string)pair.Value;
                if (!string.IsNullOrWhiteSpace(pair.Name) && !string.IsNullOrWhiteSpace(value))
                {
                    result[pair.Name.Trim()] = NormalizeUnityAssetPath(value);
                }
            }

            return result;
        }

        private static void AddStringMap(Dictionary<string, string> target, IReadOnlyDictionary<string, string> source)
        {
            if (target == null || source == null)
            {
                return;
            }

            foreach (var pair in source)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                {
                    target[pair.Key.Trim()] = NormalizeUnityAssetPath(pair.Value);
                }
            }
        }

        private static string NormalizeUnityAssetPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var text = value.Trim().Trim('"').Replace('\\', '/');
            var assetsIndex = text.IndexOf("Assets/", StringComparison.OrdinalIgnoreCase);
            return assetsIndex >= 0 ? text[assetsIndex..] : text;
        }

        private static JObject BuildImportedAvatarReadinessSummary(
            string unityProject,
            string libraryRoot,
            IReadOnlyDictionary<string, string> trustedAssets)
        {
            var summary = new JObject
            {
                ["fileCount"] = 0,
                ["trustedFileCount"] = trustedAssets?.Values
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() ?? 0,
                ["keyCount"] = trustedAssets?.Count ?? 0,
                ["probeFreshness"] = "not_run",
                ["probeEnforced"] = false,
                ["probeValidHumanAvatars"] = 0,
                ["probeInvalidAssets"] = 0,
            };
            if (string.IsNullOrWhiteSpace(unityProject))
            {
                return summary;
            }

            var directory = Path.Combine(unityProject, "Assets", "AnimeStudioBake", "ImportedAvatar");
            if (!Directory.Exists(directory))
            {
                return summary;
            }

            var assetFiles = Directory.EnumerateFiles(directory, "*.asset", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .ToArray();
            summary["fileCount"] = assetFiles.Length;
            if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
            {
                return summary;
            }

            FileInfo reportFile;
            try
            {
                reportFile = Directory.EnumerateDirectories(libraryRoot, "ImportedAvatarProbe*", SearchOption.TopDirectoryOnly)
                    .Select(dir => Path.Combine(dir, "imported_avatar_probe_batch.json"))
                    .Where(File.Exists)
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                summary["probeFreshness"] = "error";
                summary["probeError"] = ex.Message;
                return summary;
            }

            if (reportFile == null)
            {
                return summary;
            }

            summary["probeReportPath"] = reportFile.FullName;
            try
            {
                var root = JObject.Parse(File.ReadAllText(reportFile.FullName));
                summary["probeValidHumanAvatars"] = (int?)root["validHumanAvatars"] ?? 0;
                summary["probeInvalidAssets"] = (int?)root["invalidAssets"] ?? 0;
                if ((int?)root["totalAssets"] != assetFiles.Length)
                {
                    summary["probeFreshness"] = "mismatch";
                    return summary;
                }

                var newestAssetTime = assetFiles.Length == 0
                    ? DateTime.MinValue
                    : assetFiles.Max(file => file.LastWriteTimeUtc);
                if (reportFile.LastWriteTimeUtc < newestAssetTime)
                {
                    summary["probeFreshness"] = "stale";
                    return summary;
                }

                summary["probeFreshness"] = "fresh";
                summary["probeEnforced"] = true;
                return summary;
            }
            catch (Exception ex)
            {
                summary["probeFreshness"] = "error";
                summary["probeError"] = ex.Message;
                return summary;
            }
        }

        private static HashSet<string> LoadFreshImportedAvatarProbeKeys(string libraryRoot, FileInfo[] assetFiles)
        {
            if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
            {
                return null;
            }

            FileInfo reportFile;
            try
            {
                reportFile = Directory.EnumerateDirectories(libraryRoot, "ImportedAvatarProbe*", SearchOption.TopDirectoryOnly)
                    .Select(dir => Path.Combine(dir, "imported_avatar_probe_batch.json"))
                    .Where(File.Exists)
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }

            if (reportFile == null)
            {
                return null;
            }

            var newestAssetTime = assetFiles.Length == 0
                ? DateTime.MinValue
                : assetFiles.Max(file => file.LastWriteTimeUtc);
            if (reportFile.LastWriteTimeUtc < newestAssetTime)
            {
                return null;
            }

            try
            {
                var root = JObject.Parse(File.ReadAllText(reportFile.FullName));
                if ((int?)root["totalAssets"] != assetFiles.Length)
                {
                    return null;
                }

                var items = root["items"] as JArray;
                if (items == null)
                {
                    return null;
                }

                var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in items.OfType<JObject>())
                {
                    if (!string.Equals((string)item["status"], "ok", StringComparison.OrdinalIgnoreCase)
                        || !((bool?)item["isValid"] ?? false)
                        || !((bool?)item["isHuman"] ?? false))
                    {
                        continue;
                    }

                    var avatarAssetPath = (string)item["avatarAssetPath"];
                    var name = Path.GetFileNameWithoutExtension((avatarAssetPath ?? "").Replace('/', Path.DirectorySeparatorChar));
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = (string)item["avatarName"];
                    }

                    AddImportedAvatarKey(result, name);
                }

                return result;
            }
            catch
            {
                return null;
            }
        }

        private static void AddImportedAvatarKey(HashSet<string> target, string name)
        {
            if (target == null || string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            target.Add(name);
            if (name.EndsWith("_ModelAvatar", StringComparison.OrdinalIgnoreCase))
            {
                target.Add(name[..^"_ModelAvatar".Length]);
            }
        }

        private static void CreateTempImportedAvatarAssetKeys(
            SqliteConnection connection,
            IReadOnlyDictionary<string, string> importedAvatarAssets)
        {
            using (var drop = connection.CreateCommand())
            {
                drop.CommandText = "DROP TABLE IF EXISTS temp_imported_avatar_asset_keys;";
                drop.ExecuteNonQuery();
            }

            using (var create = connection.CreateCommand())
            {
                create.CommandText = "CREATE TEMP TABLE temp_imported_avatar_asset_keys(key TEXT PRIMARY KEY);";
                create.ExecuteNonQuery();
            }

            if (importedAvatarAssets == null || importedAvatarAssets.Count == 0)
            {
                return;
            }

            using var transaction = connection.BeginTransaction();
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO temp_imported_avatar_asset_keys(key) VALUES ($key);";
            var keyParameter = insert.CreateParameter();
            keyParameter.ParameterName = "$key";
            insert.Parameters.Add(keyParameter);

            foreach (var key in importedAvatarAssets.Keys.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                keyParameter.Value = key.Trim();
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        private static void CreateTempUnityBakeAvatarReadyModelTable(SqliteConnection connection, IReadOnlyCollection<string> modelOutputs = null)
        {
            using (var drop = connection.CreateCommand())
            {
                drop.CommandText = "DROP TABLE IF EXISTS temp_unity_bake_avatar_ready_models;";
                drop.ExecuteNonQuery();
            }

            using (var create = connection.CreateCommand())
            {
                // 先把“有可信 Avatar 来源”的模型收敛成小表，后面的几百万候选只做 output 命中。
                // ImportedAvatar 仍然只用精确 key 匹配，不做 contains 模糊匹配。
                var modelFilterSql = modelOutputs != null
                    ? "AND m.output IN (SELECT output FROM temp_unity_bake_models)"
                    : "";
                create.CommandText = @"
CREATE TEMP TABLE temp_unity_bake_avatar_ready_models AS
SELECT m.output AS output
FROM assets m
WHERE m.kind='Model'
  " + modelFilterSql + @"
  AND (
    " + EffectiveBakeReadyAvatarSql("m") + @"
  );";
                create.ExecuteNonQuery();
            }

            using (var index = connection.CreateCommand())
            {
                index.CommandText = @"
CREATE INDEX temp_unity_bake_avatar_ready_models_output_idx
ON temp_unity_bake_avatar_ready_models(output);";
                index.ExecuteNonQuery();
            }
        }

        private static void CreateTempExplicitUnityBakeCandidateTable(SqliteConnection connection)
        {
            using (var drop = connection.CreateCommand())
            {
                drop.CommandText = "DROP TABLE IF EXISTS temp_explicit_unity_bake_candidates;";
                drop.ExecuteNonQuery();
            }

            using (var create = connection.CreateCommand())
            {
                // 这个表只服务全量摘要统计：候选关系仍来自 SQLite 里的 Unity 显式关系，不在这里新增或猜测绑定。
                create.CommandText = @"
CREATE TEMP TABLE temp_explicit_unity_bake_candidates AS
SELECT c.model_output AS model_output
     , c.animation_output AS animation_output
     , CASE WHEN " + BakeReadyAvatarSql("m") + @" THEN 1 ELSE 0 END AS bake_ready_avatar
     , CASE WHEN " + ImportedAvatarAssetMatchSql("m", "c") + @" THEN 1 ELSE 0 END AS imported_avatar_ready
FROM model_animation_candidates c
JOIN assets m ON m.kind='Model' AND m.output=c.model_output
WHERE c.relation_source='explicit'
  AND (
    " + HumanoidBakeCandidateSql("c") + @"
  );";
                create.ExecuteNonQuery();
            }

            using (var index = connection.CreateCommand())
            {
                index.CommandText = @"
CREATE INDEX temp_explicit_unity_bake_candidates_pair_idx
ON temp_explicit_unity_bake_candidates(model_output, animation_output);";
                index.ExecuteNonQuery();
            }
        }

        private static string ImportedAvatarAssetMatchSql(string modelAlias, string candidateAlias = null)
        {
            var raw = $"{modelAlias}.raw_json";
            var candidateRaw = string.IsNullOrWhiteSpace(candidateAlias) ? null : $"{candidateAlias}.raw_json";
            var candidateChecks = candidateRaw == null
                ? string.Empty
                : $@"
       OR importedAvatar.key = COALESCE(json_extract({candidateRaw}, '$.relationEvidence.avatarName'), '')
       OR importedAvatar.key = COALESCE(json_extract({candidateRaw}, '$.animatorControllerContext.avatarName'), '')
       OR importedAvatar.key = COALESCE(json_extract({candidateRaw}, '$.candidate.relationEvidence.avatarName'), '')
       OR importedAvatar.key = COALESCE(json_extract({candidateRaw}, '$.candidate.animatorControllerContext.avatarName'), '')";
            return $@"EXISTS (
    SELECT 1
    FROM temp_imported_avatar_asset_keys importedAvatar
    WHERE importedAvatar.key = COALESCE(json_extract({raw}, '$.avatar.name'), '')
       OR importedAvatar.key = COALESCE({modelAlias}.name, '')
       {candidateChecks}
  )";
        }

        private static AvatarAssetResolution ResolveUnityAvatarAssetForSelection(
            PreviewSelection selection,
            string suppliedUnityAvatarAsset,
            IReadOnlyDictionary<string, string> importedAvatarAssets)
        {
            if (!string.IsNullOrWhiteSpace(suppliedUnityAvatarAsset))
            {
                return new AvatarAssetResolution(
                    suppliedUnityAvatarAsset,
                    "unityAssetPaths.avatarAsset",
                    "--unity_avatar_asset");
            }

            var resolved = ResolveUnityAvatarAssetDetails(selection?.Model?["model"] as JObject, importedAvatarAssets);
            if (!string.IsNullOrWhiteSpace(resolved.AssetPath))
            {
                return resolved;
            }

            resolved = ResolveCandidateImportedUnityAvatarAssetDetails(selection?.Animation as JObject, importedAvatarAssets);
            if (!string.IsNullOrWhiteSpace(resolved.AssetPath))
            {
                return resolved;
            }

            resolved = ResolveCandidateUnityAvatarAssetDetails(selection?.Animation as JObject);
            if (!string.IsNullOrWhiteSpace(resolved.AssetPath))
            {
                return resolved;
            }

            return new AvatarAssetResolution(null, "model_human_description_or_prefab", null);
        }

        private static AnimationClipAssetResolution ResolveUnityAnimationClipForSelection(
            PreviewSelection selection,
            string suppliedUnityAnimationClip,
            IReadOnlyDictionary<string, string> importedAnimationClips)
        {
            if (!string.IsNullOrWhiteSpace(suppliedUnityAnimationClip))
            {
                return new AnimationClipAssetResolution(
                    NormalizeUnityAssetPath(suppliedUnityAnimationClip),
                    "unityAssetPaths.animationClip",
                    "--unity_animation_clip");
            }

            var resolved = ResolveUnityAnimationClipDetails(selection?.Animation as JObject, importedAnimationClips);
            if (!string.IsNullOrWhiteSpace(resolved.AssetPath))
            {
                return resolved;
            }

            return new AnimationClipAssetResolution(null, "animeStudioAssets.animation.anim", null);
        }

        private static AnimationClipAssetResolution ResolveUnityAnimationClipDetails(
            JObject animation,
            IReadOnlyDictionary<string, string> importedAnimationClips)
        {
            if (animation == null || importedAnimationClips == null || importedAnimationClips.Count == 0)
            {
                return new AnimationClipAssetResolution(null, "animeStudioAssets.animation.anim", null);
            }

            foreach (var key in BuildUnityAnimationClipLookupKeys(animation))
            {
                if (importedAnimationClips.TryGetValue(key, out var value))
                {
                    return new AnimationClipAssetResolution(value, "unityAssetPaths.animationClip", key);
                }
            }

            return new AnimationClipAssetResolution(null, "animeStudioAssets.animation.anim", null);
        }

        private static AnimatorControllerAssetResolution ResolveUnityAnimatorControllerForSelection(
            PreviewSelection selection,
            string suppliedUnityAnimatorController,
            IReadOnlyDictionary<string, string> importedAnimatorControllers)
        {
            if (!string.IsNullOrWhiteSpace(suppliedUnityAnimatorController))
            {
                return new AnimatorControllerAssetResolution(
                    NormalizeUnityAssetPath(suppliedUnityAnimatorController),
                    "unityAssetPaths.animatorController",
                    "--unity_animator_controller");
            }

            if (importedAnimatorControllers == null || importedAnimatorControllers.Count == 0)
            {
                return new AnimatorControllerAssetResolution(null, "generated_or_clip_playable_controller", null);
            }

            foreach (var key in BuildUnityAnimatorControllerLookupKeys(selection))
            {
                if (importedAnimatorControllers.TryGetValue(key, out var value))
                {
                    return new AnimatorControllerAssetResolution(
                        NormalizeUnityAssetPath(value),
                        "unityAssetPaths.animatorController",
                        key);
                }
            }

            return new AnimatorControllerAssetResolution(null, "generated_or_clip_playable_controller", null);
        }

        private static JObject AttachAdditionalLayerClipAssetPaths(
            JObject animation,
            IReadOnlyDictionary<string, string> importedAnimationClips)
        {
            var context = animation?["animatorControllerContext"] as JObject;
            var clips = context?["additionalLayerClips"] as JArray;
            if (clips == null || clips.Count == 0)
            {
                return animation;
            }

            var copy = (JObject)animation.DeepClone();
            var copiedContext = copy["animatorControllerContext"] as JObject;
            var copiedClips = copiedContext?["additionalLayerClips"] as JArray;
            if (copiedClips == null)
            {
                return copy;
            }

            PruneAmbiguousAdditionalLayerClips(copiedContext, copiedClips);
            copiedClips = copiedContext?["additionalLayerClips"] as JArray;
            if (copiedClips == null || copiedClips.Count == 0)
            {
                return copy;
            }

            foreach (var clip in copiedClips.OfType<JObject>())
            {
                if (!string.IsNullOrWhiteSpace((string)clip["unityAssetPath"]))
                {
                    clip["unityAssetPath"] = NormalizeUnityAssetPath((string)clip["unityAssetPath"]);
                    continue;
                }

                if (importedAnimationClips == null || importedAnimationClips.Count == 0)
                {
                    continue;
                }

                foreach (var key in BuildAdditionalLayerClipLookupKeys(clip))
                {
                    if (importedAnimationClips.TryGetValue(key, out var value))
                    {
                        clip["unityAssetPath"] = NormalizeUnityAssetPath(value);
                        clip["unityAssetLookupKey"] = key;
                        break;
                    }
                }
            }

            return copy;
        }

        private static void PruneAmbiguousAdditionalLayerClips(JObject context, JArray clips)
        {
            if (context == null || clips == null || clips.Count == 0)
            {
                return;
            }

            var kept = new JArray();
            var warnings = context["additionalLayerContextWarnings"] as JArray ?? new JArray();
            foreach (var group in clips.OfType<JObject>().GroupBy(ReadAdditionalLayerIndex).OrderBy(x => x.Key))
            {
                var unique = group
                    .Where(x => x != null)
                    .GroupBy(x => FirstNonEmpty(
                        ((long?)x["pathId"])?.ToString(CultureInfo.InvariantCulture),
                        (string)x["unityAssetPath"],
                        (string)x["name"]))
                    .Select(x => x.First())
                    .ToList();

                if (unique.Count == 1)
                {
                    kept.Add(unique[0]);
                    continue;
                }

                warnings.Add(new JObject
                {
                    ["layerIndex"] = group.Key,
                    ["clipCount"] = unique.Count,
                    ["reason"] = "same AnimatorController layer resolves to multiple additional clips; BlendTree parameters, weights, or runtime controller context are required",
                    ["rule"] = "skip ambiguous additional layer in Unity bake request instead of applying several child clips as full-weight layers",
                });
            }

            context["additionalLayerClips"] = kept;
            if (warnings.Count > 0)
            {
                context["additionalLayerContextWarnings"] = warnings;
            }
        }

        private static int ReadAdditionalLayerIndex(JObject clip)
        {
            return (int?)clip?["layerIndex"] ?? -1;
        }

        private static IEnumerable<string> BuildAdditionalLayerClipLookupKeys(JObject clip)
        {
            var name = (string)clip?["name"];
            var pathId = (long?)clip?["pathId"];
            var fileName = string.IsNullOrWhiteSpace(name) ? null : name + ".anim";
            var pathIdText = pathId?.ToString(CultureInfo.InvariantCulture);
            return new[] { name, fileName, pathIdText }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> BuildUnityAnimationClipLookupKeys(JObject animation)
        {
            var name = (string)animation?["name"];
            var output = (string)animation?["output"];
            var animationAsset = (string)animation?["animationAsset"];
            var fileName = string.IsNullOrWhiteSpace(output) ? null : Path.GetFileName(output);
            var stem = string.IsNullOrWhiteSpace(fileName) ? null : Path.GetFileNameWithoutExtension(fileName);
            var sidecarFileName = string.IsNullOrWhiteSpace(animationAsset) ? null : Path.GetFileName(animationAsset);
            var sidecarStem = string.IsNullOrWhiteSpace(sidecarFileName) ? null : Path.GetFileNameWithoutExtension(sidecarFileName);
            if (!string.IsNullOrWhiteSpace(sidecarStem)
                && sidecarStem.EndsWith(".animation_asset", StringComparison.OrdinalIgnoreCase))
            {
                sidecarStem = sidecarStem[..^".animation_asset".Length];
            }

            return new[] { name, stem, fileName, sidecarStem, sidecarFileName }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> BuildUnityAnimatorControllerLookupKeys(PreviewSelection selection)
        {
            var model = selection?.Model?["model"] as JObject;
            var animation = selection?.Animation as JObject;
            var context = animation?["animatorControllerContext"] as JObject;
            var relationEvidence = animation?["relationEvidence"] as JObject
                ?? animation?["candidate"]?["relationEvidence"] as JObject;
            var controllerName = (string)relationEvidence?["controllerName"] ?? (string)context?["controllerName"];
            var controllerFileName = string.IsNullOrWhiteSpace(controllerName) ? null : controllerName + ".controller";
            var controllerPathId = (long?)relationEvidence?["controllerPathId"] ?? (long?)context?["controllerPathId"];
            var controllerPathIdText = controllerPathId?.ToString(CultureInfo.InvariantCulture);
            var modelName = (string)model?["name"];
            var animationName = (string)animation?["name"];

            return new[]
            {
                controllerName,
                controllerFileName,
                controllerPathIdText,
                modelName,
                animationName,
                !string.IsNullOrWhiteSpace(modelName) && !string.IsNullOrWhiteSpace(controllerName)
                    ? modelName + "|" + controllerName
                    : null,
                !string.IsNullOrWhiteSpace(animationName) && !string.IsNullOrWhiteSpace(controllerName)
                    ? animationName + "|" + controllerName
                    : null,
            }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static bool ValidateSuppliedUnityAvatarAssetScope(string modelSelector, string suppliedUnityAvatarAsset)
        {
            if (string.IsNullOrWhiteSpace(suppliedUnityAvatarAsset))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(modelSelector))
            {
                return true;
            }

            Logger.Error(
                "--unity_avatar_asset 是单模型诊断/定向预览入口，不能在未指定 --preview_model 时参与批量选择。"
                + " 多模型生产 bake 请把恢复出的 Avatar 放入 Unity Bake Project/Assets/AnimeStudioBake/ImportedAvatar，"
                + "让工具按模型 Avatar 名/模型名精确匹配。");
            return false;
        }

        private static bool ValidateSuppliedUnityAnimationClipSelections(
            string suppliedUnityAnimationClip,
            IReadOnlyCollection<PreviewSelection> selections)
        {
            if (string.IsNullOrWhiteSpace(suppliedUnityAnimationClip) || selections == null || selections.Count <= 1)
            {
                return true;
            }

            var animationKeys = selections
                .Select(x => (string)x?.Animation?["output"] ?? (string)x?.Animation?["name"] ?? "")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToArray();
            if (animationKeys.Length <= 1)
            {
                return true;
            }

            Logger.Error(
                "--unity_animation_clip 只允许用于单个 AnimationClip 的定向 bake。当前选择命中了多个动画，"
                + "继续执行会把同一个 Unity AnimationClip 强行套到不同动画候选上。"
                + " 请收紧 --preview_animation，或使用 ImportedAnimationClip 目录/配置让每个动画精确匹配自己的原始 clip asset。");
            return false;
        }

        private static bool ValidateSuppliedUnityAvatarAssetSelections(
            string suppliedUnityAvatarAsset,
            IReadOnlyCollection<PreviewSelection> selections)
        {
            if (string.IsNullOrWhiteSpace(suppliedUnityAvatarAsset) || selections == null || selections.Count <= 1)
            {
                return true;
            }

            var modelKeys = selections
                .Select(x =>
                {
                    var model = x?.Model?["model"] as JObject;
                    return (string)model?["output"] ?? (string)model?["name"] ?? "";
                })
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToArray();
            if (modelKeys.Length <= 1)
            {
                return true;
            }

            Logger.Error(
                "--unity_avatar_asset 只允许用于单个模型的定向 bake。当前选择命中了多个模型，"
                + "继续执行会把同一个 Avatar 强行套到不同模型上，违背 Unity 确定性关系规则。"
                + " 请收紧 --preview_model，或使用 ImportedAvatar 目录/配置让每个模型精确匹配自己的 Avatar asset。");
            return false;
        }

        private static string ResolveUnityAvatarAsset(JObject model, IReadOnlyDictionary<string, string> importedAvatarAssets)
        {
            return ResolveUnityAvatarAssetDetails(model, importedAvatarAssets).AssetPath;
        }

        private static AvatarAssetResolution ResolveUnityAvatarAssetDetails(JObject model, IReadOnlyDictionary<string, string> importedAvatarAssets)
        {
            if (model == null || importedAvatarAssets == null || importedAvatarAssets.Count == 0)
            {
                return new AvatarAssetResolution(null, "model_human_description_or_prefab", null);
            }

            foreach (var key in BuildUnityAvatarLookupKeys(model))
            {
                if (importedAvatarAssets.TryGetValue(key, out var value))
                {
                    return new AvatarAssetResolution(value, "unityAssetPaths.avatarAsset", key);
                }
            }

            return new AvatarAssetResolution(null, "model_human_description_or_prefab", null);
        }

        private static string ResolveCandidateUnityAvatarAsset(JObject animation)
        {
            return ResolveCandidateUnityAvatarAssetDetails(animation).AssetPath;
        }

        private static AvatarAssetResolution ResolveCandidateUnityAvatarAssetDetails(JObject animation)
        {
            var assetPath = NormalizeUnityAssetPath(
                (string)animation?["unityAvatarAsset"]
                ?? (string)animation?["unityAssetPaths"]?["avatarAsset"]
                ?? (string)animation?["candidate"]?["unityAvatarAsset"]
                ?? (string)animation?["candidate"]?["unityAssetPaths"]?["avatarAsset"]);
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return new AvatarAssetResolution(null, "model_human_description_or_prefab", null);
            }

            // 这个字段来自已经入库的显式模型-动画候选，只用于当前选中候选的 Avatar 求解器入口。
            // 它不参与新增模型-动画关系，也不允许跨模型批量硬套。
            return new AvatarAssetResolution(assetPath, "candidate.unityAvatarAsset", "explicit_candidate");
        }

        private static AvatarAssetResolution ResolveCandidateImportedUnityAvatarAssetDetails(
            JObject animation,
            IReadOnlyDictionary<string, string> importedAvatarAssets)
        {
            if (animation == null || importedAvatarAssets == null || importedAvatarAssets.Count == 0)
            {
                return new AvatarAssetResolution(null, "model_human_description_or_prefab", null);
            }

            foreach (var key in BuildCandidateUnityAvatarLookupKeys(animation))
            {
                if (importedAvatarAssets.TryGetValue(key, out var value))
                {
                    return new AvatarAssetResolution(value, "unityAssetPaths.avatarAsset", key);
                }
            }

            return new AvatarAssetResolution(null, "model_human_description_or_prefab", null);
        }

        private static IEnumerable<string> BuildCandidateUnityAvatarLookupKeys(JObject animation)
        {
            var relationEvidence = animation?["relationEvidence"] as JObject
                ?? animation?["candidate"]?["relationEvidence"] as JObject;
            var context = animation?["animatorControllerContext"] as JObject
                ?? animation?["candidate"]?["animatorControllerContext"] as JObject;
            return new[]
            {
                (string)relationEvidence?["avatarName"],
                (string)context?["avatarName"],
            }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> BuildUnityAvatarLookupKeys(JObject model)
        {
            var avatarName = (string)(model?["avatar"] as JObject)?["name"];
            var modelName = (string)model?["name"];
            var output = (string)model?["output"];
            var fileName = string.IsNullOrWhiteSpace(output) ? null : Path.GetFileName(output);
            var stem = string.IsNullOrWhiteSpace(fileName) ? null : Path.GetFileNameWithoutExtension(fileName);
            return new[] { avatarName, modelName, stem, fileName }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static bool ModelHasBakeReadyAvatar(JObject model)
        {
            var avatar = model?["avatar"] as JObject;
            var humanBones = avatar?["humanBones"] as JArray;
            var skeletonBones = avatar?["skeletonBones"] as JArray;
            return humanBones != null && humanBones.Count > 0
                && skeletonBones != null && skeletonBones.Count > 0;
        }

        private static string BakeReadyAvatarSql(string modelAlias)
        {
            var raw = $"{modelAlias}.raw_json";
            return $@"(
    COALESCE(json_array_length(json_extract({raw}, '$.avatar.humanBones')), 0) > 0
    AND COALESCE(json_array_length(json_extract({raw}, '$.avatar.skeletonBones')), 0) > 0
  )";
        }

        private static string EffectiveBakeReadyAvatarSql(string modelAlias)
        {
            return $@"(
    {BakeReadyAvatarSql(modelAlias)}
    OR {ImportedAvatarAssetMatchSql(modelAlias)}
    OR {CandidateImportedAvatarAssetReadySql(modelAlias)}
    OR {CandidateUnityAvatarAssetReadySql(modelAlias)}
  )";
        }

        private static string CandidateImportedAvatarAssetReadySql(string modelAlias)
        {
            return $@"EXISTS (
    SELECT 1
    FROM model_animation_candidates avatarCandidate
    JOIN temp_imported_avatar_asset_keys importedAvatar
      ON importedAvatar.key = COALESCE(json_extract(avatarCandidate.raw_json, '$.relationEvidence.avatarName'), '')
      OR importedAvatar.key = COALESCE(json_extract(avatarCandidate.raw_json, '$.animatorControllerContext.avatarName'), '')
      OR importedAvatar.key = COALESCE(json_extract(avatarCandidate.raw_json, '$.candidate.relationEvidence.avatarName'), '')
      OR importedAvatar.key = COALESCE(json_extract(avatarCandidate.raw_json, '$.candidate.animatorControllerContext.avatarName'), '')
    WHERE avatarCandidate.relation_source='explicit'
      AND avatarCandidate.model_output={modelAlias}.output
  )";
        }

        private static string CandidateUnityAvatarAssetReadySql(string modelAlias)
        {
            return $@"EXISTS (
    SELECT 1
    FROM model_animation_candidates avatarCandidate
    WHERE avatarCandidate.relation_source='explicit'
      AND avatarCandidate.model_output={modelAlias}.output
      AND COALESCE(json_extract(avatarCandidate.raw_json, '$.unityAvatarAsset'), '') <> ''
  )";
        }

        private static string HumanoidBakeCandidateSql(string candidateAlias)
        {
            var raw = $"{candidateAlias}.raw_json";
            return $@"(
    COALESCE(json_extract({raw}, '$.requiresUnityBake'), 0)=1
    OR COALESCE(json_extract({raw}, '$.legacyUnityBakeSupported'), 0)=1
    OR COALESCE(json_extract({raw}, '$.requiresInternalHumanoidSolve'), 0)=1
    OR COALESCE(json_extract({raw}, '$.fullHumanoidBakeRequired'), 0)=1
    OR COALESCE(json_extract({raw}, '$.unityBakeAcceleratedReady'), 0)=1
    OR COALESCE(json_extract({raw}, '$.productionUnityBakeReady'), 0)=1
    OR COALESCE(json_extract({raw}, '$.standaloneBodyRequiresDirectTrsSolve'), 0)=1
    OR json_extract({raw}, '$.animatorControllerContext.baseLayerClip.clip.pathId') IS NOT NULL
  )";
        }

        private static string BuildBakeReadyCacheWhere(string cacheAlias)
        {
            return $@"
EXISTS (
  SELECT 1
  FROM model_animation_candidates c
  JOIN assets m ON m.kind='Model' AND m.output=c.model_output
  WHERE c.model_output={cacheAlias}.model_output
    AND c.animation_output={cacheAlias}.animation_output
    AND c.relation_source='explicit'
    AND (
      {HumanoidBakeCandidateSql("c")}
    )
    AND {EffectiveBakeReadyAvatarSql("m")}
)";
        }

        private static void UpsertBakeCache(SqliteConnection connection, SqliteTransaction transaction, JObject item, string libraryRoot)
        {
            var modelOutput = CanonicalizeLibraryOutput((string)item["modelOutput"], libraryRoot);
            var animationOutput = CanonicalizeLibraryOutput((string)item["animationOutput"], libraryRoot);
            UpsertBakeCacheRow(connection, transaction, item, libraryRoot, modelOutput, animationOutput);

            var requestedAnimationOutput = CanonicalizeLibraryOutput((string)item["requestedAnimationOutput"], libraryRoot);
            if (!string.IsNullOrWhiteSpace(requestedAnimationOutput)
                && !string.Equals(requestedAnimationOutput, animationOutput, StringComparison.OrdinalIgnoreCase))
            {
                // AnimatorController 的叠加/辅助 clip 会切到 baseLayerClip 才能得到完整身体动画。
                // 这里额外把同一个可信 bake 结果挂回用户点选的原始候选，Browser 状态才不会显示成未生成；
                // request/report 仍保留 actual/requested 两个路径，避免把关系来源说混。
                UpsertBakeCacheRow(connection, transaction, item, libraryRoot, modelOutput, requestedAnimationOutput);
            }
        }

        private static void UpsertBakeCacheRow(
            SqliteConnection connection,
            SqliteTransaction transaction,
            JObject item,
            string libraryRoot,
            string modelOutput,
            string animationOutput)
        {
            var incomingStatus = (string)item["status"] ?? "failed";
            var incomingMessage = (string)item["message"];
            var preserveExistingTerminal = ShouldPreserveExistingBakeCache(
                connection,
                transaction,
                modelOutput,
                animationOutput,
                libraryRoot,
                incomingStatus,
                incomingMessage);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO animation_bake_cache(model_output, animation_output, status, request_path, result_path, baked_gltf_path, baked_fbx_path, message, bake_mode, solve_path, production_status, requires_visual_validation, writes_reusable_gltf_trs_candidate, animation_solve_json, updated_utc)
VALUES ($modelOutput, $animationOutput, $status, $requestPath, $resultPath, $bakedGltfPath, $bakedFbxPath, $message, $bakeMode, $solvePath, $productionStatus, $requiresVisualValidation, $writesReusableGltfTrsCandidate, $animationSolveJson, $updatedUtc)
ON CONFLICT(model_output, animation_output) DO UPDATE SET
    status=CASE
        WHEN $preserveExistingTerminal=1 AND excluded.status NOT IN ('baked', 'static_pose', 'needs_review') THEN animation_bake_cache.status
        ELSE excluded.status
    END,
    request_path=COALESCE(excluded.request_path, animation_bake_cache.request_path),
    result_path=CASE
        WHEN $preserveExistingTerminal=1 AND excluded.status NOT IN ('baked', 'static_pose', 'needs_review') THEN animation_bake_cache.result_path
        ELSE excluded.result_path
    END,
    baked_gltf_path=CASE
        WHEN $preserveExistingTerminal=1 AND excluded.status NOT IN ('baked', 'static_pose', 'needs_review') THEN animation_bake_cache.baked_gltf_path
        ELSE excluded.baked_gltf_path
    END,
    baked_fbx_path=CASE
        WHEN $preserveExistingTerminal=1 AND excluded.status NOT IN ('baked', 'static_pose', 'needs_review') THEN animation_bake_cache.baked_fbx_path
        ELSE excluded.baked_fbx_path
    END,
    message=CASE
        WHEN $preserveExistingTerminal=1 AND excluded.status NOT IN ('baked', 'static_pose', 'needs_review') THEN animation_bake_cache.message
        ELSE excluded.message
    END,
    bake_mode=COALESCE(excluded.bake_mode, animation_bake_cache.bake_mode),
    solve_path=CASE
        WHEN $preserveExistingTerminal=1 AND excluded.status NOT IN ('baked', 'static_pose', 'needs_review') THEN animation_bake_cache.solve_path
        ELSE excluded.solve_path
    END,
    production_status=CASE
        WHEN $preserveExistingTerminal=1 AND excluded.status NOT IN ('baked', 'static_pose', 'needs_review') THEN animation_bake_cache.production_status
        ELSE excluded.production_status
    END,
    requires_visual_validation=CASE
        WHEN $preserveExistingTerminal=1 AND excluded.status NOT IN ('baked', 'static_pose', 'needs_review') THEN animation_bake_cache.requires_visual_validation
        ELSE excluded.requires_visual_validation
    END,
    writes_reusable_gltf_trs_candidate=CASE
        WHEN $preserveExistingTerminal=1 AND excluded.status NOT IN ('baked', 'static_pose', 'needs_review') THEN animation_bake_cache.writes_reusable_gltf_trs_candidate
        ELSE excluded.writes_reusable_gltf_trs_candidate
    END,
    animation_solve_json=CASE
        WHEN $preserveExistingTerminal=1 AND excluded.status NOT IN ('baked', 'static_pose', 'needs_review') THEN animation_bake_cache.animation_solve_json
        ELSE excluded.animation_solve_json
    END,
    updated_utc=excluded.updated_utc;";
            command.Parameters.AddWithValue("$modelOutput", modelOutput);
            command.Parameters.AddWithValue("$animationOutput", animationOutput);
            command.Parameters.AddWithValue("$status", incomingStatus);
            command.Parameters.AddWithValue("$requestPath", DbValue((string)item["request"]));
            command.Parameters.AddWithValue("$resultPath", DbValue((string)item["result"]));
            command.Parameters.AddWithValue("$bakedGltfPath", DbValue((string)item["bakedGltf"]));
            command.Parameters.AddWithValue("$bakedFbxPath", DbValue((string)item["bakedFbx"]));
            command.Parameters.AddWithValue("$message", DbValue((string)item["message"]));
            command.Parameters.AddWithValue("$bakeMode", "UnityBakeToGltf");
            command.Parameters.AddWithValue("$solvePath", DbValue((string)item["solvePath"]));
            command.Parameters.AddWithValue("$productionStatus", DbValue((string)item["productionStatus"]));
            command.Parameters.AddWithValue("$requiresVisualValidation", NullableBoolDbValue((bool?)item["requiresVisualValidation"]));
            command.Parameters.AddWithValue("$writesReusableGltfTrsCandidate", NullableBoolDbValue((bool?)item["writesReusableGltfTrsCandidate"]));
            command.Parameters.AddWithValue("$animationSolveJson", DbValue((item["animationSolve"] as JObject)?.ToString(Formatting.None)));
            command.Parameters.AddWithValue("$updatedUtc", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$preserveExistingTerminal", preserveExistingTerminal ? 1 : 0);
            command.ExecuteNonQuery();
        }

        private static bool ShouldPreserveExistingBakeCache(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string modelOutput,
            string animationOutput,
            string libraryRoot,
            string incomingStatus,
            string incomingMessage)
        {
            if (IsHardHumanoidBakeFailure(incomingStatus, incomingMessage))
            {
                return false;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
SELECT status, baked_gltf_path
FROM animation_bake_cache
WHERE model_output=$modelOutput AND animation_output=$animationOutput
LIMIT 1;";
            command.Parameters.AddWithValue("$modelOutput", modelOutput);
            command.Parameters.AddWithValue("$animationOutput", animationOutput);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return false;
            }

            var status = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var bakedGltfPath = reader.IsDBNull(1) ? null : reader.GetString(1);
            return string.Equals(status, "baked", StringComparison.OrdinalIgnoreCase)
                && IsTrustedBakedGltfPath(bakedGltfPath, libraryRoot);
        }

        private static bool IsHardHumanoidBakeFailure(string status, string message)
        {
            if (!string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            // 这类失败说明 Unity 没有把 Humanoid/Muscle clip 当 humanMotion 采样。
            // 旧的 needs_review 半跪/静态姿态缓存不能继续保护，否则 Browser 会一直打开错误 glTF。
            return message.IndexOf("isHumanMotion=false", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("not sampled as a real Humanoid/Muscle", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static object DbValue(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
        }

        private static object NullableBoolDbValue(bool? value)
        {
            return value.HasValue ? (object)(value.Value ? 1 : 0) : DBNull.Value;
        }

        private static string CanonicalizeLibraryOutput(string path, string libraryRoot)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            if (string.IsNullOrWhiteSpace(libraryRoot) || !Path.IsPathRooted(path))
            {
                return NormalizeLibraryOutput(path);
            }

            try
            {
                var fullRoot = Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var fullPath = Path.GetFullPath(path);
                if (fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return NormalizeLibraryOutput(Path.GetRelativePath(fullRoot, fullPath));
                }
            }
            catch
            {
                return NormalizeLibraryOutput(path);
            }

            return NormalizeLibraryOutput(path);
        }

        private static string NormalizeLibraryOutput(string path)
        {
            return (path ?? string.Empty)
                .Trim()
                .Trim('"')
                .Replace('\\', '/')
                .TrimStart('/')
                .TrimEnd('/');
        }

        private sealed class UniqueBakeCacheEntry
        {
            public int RowCount { get; set; }
            public bool HasRequestWritten { get; set; }
            public bool HasBaked { get; set; }
            public bool HasTrustedBaked { get; set; }
            public bool HasStaticPose { get; set; }
            public bool HasNeedsReview { get; set; }
            public bool HasNeedsAnimatorControllerContext { get; set; }
            public bool HasBakedMissingGltf { get; set; }
            public bool HasFailed { get; set; }
        }

        private sealed record BakeCacheRow(
            long RowId,
            string Key,
            bool IsCanonical,
            string Status,
            string BakedGltfPath,
            DateTime UpdatedUtc);

        private static void CreateTempProcessedBakeCacheTable(SqliteConnection connection, string libraryRoot)
        {
            using (var create = connection.CreateCommand())
            {
                create.CommandText = @"
CREATE TEMP TABLE IF NOT EXISTS temp_unity_bake_processed_cache(
    model_output TEXT NOT NULL,
    animation_output TEXT NOT NULL,
    PRIMARY KEY(model_output, animation_output)
);
DELETE FROM temp_unity_bake_processed_cache;";
                create.ExecuteNonQuery();
            }

            var rows = new List<(string ModelOutput, string AnimationOutput)>();
            using (var select = connection.CreateCommand())
            {
                select.CommandText = @"
SELECT model_output, animation_output, baked_gltf_path, status, message
FROM animation_bake_cache
WHERE status IN ('baked', 'static_pose', 'needs_review', 'needs_animator_controller_context')
  AND COALESCE(model_output, '')<>''
  AND COALESCE(animation_output, '')<>'';";
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    var bakedGltf = reader.IsDBNull(2) ? null : reader.GetString(2);
                    var status = reader.IsDBNull(3) ? null : reader.GetString(3);
                    var message = reader.IsDBNull(4) ? null : reader.GetString(4);
                    // static_pose / needs_review 是已经确认的终态诊断结果，默认批量续跑时也应跳过；
                    // 需要重新验证时用 --preview_validation_force 显式重烘焙。
                    if (!IsTrustedBakedGltfPath(bakedGltf, libraryRoot)
                        && !IsStaticPoseBakedGltfPath(bakedGltf, libraryRoot)
                        && !IsNeedsReviewBakedGltfPath(bakedGltf, libraryRoot)
                        && !IsAnimatorControllerContextCacheStatus(status, message))
                    {
                        continue;
                    }

                    rows.Add((
                        CanonicalizeLibraryOutput(reader.GetString(0), libraryRoot),
                        CanonicalizeLibraryOutput(reader.GetString(1), libraryRoot)));
                }
            }

            if (rows.Count == 0)
            {
                return;
            }

            using var transaction = connection.BeginTransaction();
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = @"
INSERT OR IGNORE INTO temp_unity_bake_processed_cache(model_output, animation_output)
VALUES ($modelOutput, $animationOutput);";
            var modelParameter = insert.Parameters.Add("$modelOutput", SqliteType.Text);
            var animationParameter = insert.Parameters.Add("$animationOutput", SqliteType.Text);
            foreach (var row in rows)
            {
                modelParameter.Value = row.ModelOutput;
                animationParameter.Value = row.AnimationOutput;
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        private static void CreateTempOutputTable(SqliteConnection connection, string tableName, List<string> outputs)
        {
            using (var create = connection.CreateCommand())
            {
                create.CommandText = $"CREATE TEMP TABLE IF NOT EXISTS {tableName}(output TEXT PRIMARY KEY); DELETE FROM {tableName};";
                create.ExecuteNonQuery();
            }

            if (outputs == null)
            {
                return;
            }

            using var transaction = connection.BeginTransaction();
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"INSERT OR IGNORE INTO {tableName}(output) VALUES ($output);";
            var parameter = insert.Parameters.Add("$output", SqliteType.Text);
            foreach (var output in outputs)
            {
                parameter.Value = output;
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        private static PreviewSelection SelectPreviewFromLibraryDb(string dbPath, string modelSelector, string animationSelector)
        {
            SQLitePCL.Batteries_V2.Init();
            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();

            var model = SelectAssetFromLibraryDb(connection, "Model", modelSelector);
            var animation = SelectAssetFromLibraryDb(connection, "Animation", animationSelector);
            if (model == null || animation == null)
            {
                return null;
            }

            var candidate = new JObject
            {
                ["name"] = animation["name"],
                ["output"] = animation["output"],
                ["animationAsset"] = animation["animationAsset"],
                ["source"] = animation["source"],
                ["container"] = animation["container"],
                ["animationType"] = animation["animationType"],
                ["animationCapability"] = animation["animationCapability"],
                ["hasMuscleClip"] = animation["hasMuscleClip"],
                ["requiresHumanoidBake"] = ResolveRequiresHumanoidBake(animation),
                ["relation"] = "library.sqlite.selection",
                ["relationSource"] = "sqlite",
                ["confidence"] = "manual_unity_bake_selection",
                ["score"] = 100,
            };

            return new PreviewSelection(
                new JObject
                {
                    ["model"] = model,
                    ["candidateCount"] = 1,
                    ["candidates"] = new JArray(candidate),
                },
                candidate);
        }

        private static JObject SelectAssetFromLibraryDb(SqliteConnection connection, string kind, string selector)
        {
            var fileName = string.IsNullOrWhiteSpace(selector) ? string.Empty : Path.GetFileName(selector);
            var stem = string.IsNullOrWhiteSpace(fileName) ? string.Empty : Path.GetFileNameWithoutExtension(fileName);
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT raw_json
FROM assets
WHERE kind = $kind
  AND (
    output = $selector
    OR name = $selector
    OR output LIKE $fileNameSelector
    OR name = $stem
    OR output LIKE $likeSelector
    OR name LIKE $likeSelector
  )
ORDER BY
  CASE
    WHEN output = $selector THEN 0
    WHEN name = $selector THEN 1
    WHEN name = $stem THEN 2
    WHEN output LIKE $fileNameSelector THEN 3
    ELSE 4
  END,
  name COLLATE NOCASE
LIMIT 32;";
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$selector", selector ?? string.Empty);
            command.Parameters.AddWithValue("$fileNameSelector", "%" + fileName);
            command.Parameters.AddWithValue("$stem", stem);
            command.Parameters.AddWithValue("$likeSelector", "%" + (selector ?? string.Empty) + "%");

            var rows = new List<JObject>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(JObject.Parse(reader.GetString(0)));
            }

            if (rows.Count == 0)
            {
                return null;
            }

            return rows.FirstOrDefault(x => Matches(selector, (string)x["name"], (string)x["output"]))
                ?? rows[0];
        }

        private static bool MatchesAny(string selector, params string[] values)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                return true;
            }

            foreach (var item in selector.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Matches(item, values))
                {
                    return true;
                }
            }
            return false;
        }

        private static void ResolveSelectionLibraryPaths(PreviewSelection selection, string libraryRoot)
        {
            if (selection?.Model?["model"] is JObject model)
            {
                ResolvePathProperty(model, libraryRoot, "output");
                ResolvePathProperty(model, libraryRoot, "modelPreview");
            }

            if (selection?.Animation is JObject animation)
            {
                ResolvePathProperty(animation, libraryRoot, "output");
                ResolvePathProperty(animation, libraryRoot, "animationAsset");
            }
        }

        private static void ResolveBatchCandidatePaths(JObject model, JObject animation, string libraryRoot)
        {
            ResolvePathProperty(model, libraryRoot, "output");
            ResolvePathProperty(model, libraryRoot, "modelPreview");
            ResolvePathProperty(animation, libraryRoot, "output");
            ResolvePathProperty(animation, libraryRoot, "animationAsset");
        }

        private static void ResolvePathProperty(JObject obj, string libraryRoot, string propertyName)
        {
            var value = (string)obj[propertyName];
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var resolved = LibraryRelativePathMigrator.ResolveLibraryPath(libraryRoot, value);
            if (!Path.IsPathRooted(resolved) && !string.IsNullOrWhiteSpace(libraryRoot))
            {
                resolved = Path.Combine(libraryRoot, resolved.Replace('/', Path.DirectorySeparatorChar));
            }

            obj[propertyName] = resolved;
        }

        private static bool ResolveRequiresHumanoidBake(JObject animation)
        {
            if ((bool?)animation?["requiresHumanoidBake"] == true)
            {
                return true;
            }

            if ((bool?)animation?["hasMuscleClip"] == true)
            {
                return true;
            }

            var animationType = (string)animation?["animationType"];
            return !string.IsNullOrWhiteSpace(animationType)
                && animationType.IndexOf("Humanoid", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static JObject PrepareAvatarForUnityBake(JObject avatar, bool requiresHumanoidBake, string unityModelPrefab, string unityAvatarAsset)
        {
            if (avatar == null)
            {
                return null;
            }

            var copy = (JObject)avatar.DeepClone();
            EnsureAvatarOracle(copy);
            var humanBones = copy["humanBones"]?.Values<string>()?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray() ?? Array.Empty<string>();
            if (!requiresHumanoidBake || humanBones.Length > 0)
            {
                return copy;
            }

            if (string.IsNullOrWhiteSpace(unityModelPrefab) && string.IsNullOrWhiteSpace(unityAvatarAsset))
            {
                // 没有 HumanDescription 和真实 prefab 时，AvatarConstant 只能做诊断。
                // 这条路会让 Unity 重新 BuildHumanAvatar，不等于游戏原始 Avatar。
                // 生产 bake 已在 GenerateSelection 前置拒绝，避免继续产出看似成功但手臂/腿部错误的 glTF。
                return copy;
            }

            if (!string.IsNullOrWhiteSpace(unityAvatarAsset))
            {
                copy["humanBonesSource"] = "unityAssetPaths.avatarAsset";
                copy["humanBonesNote"] =
                    "HumanDescription humanBones are absent from the exported Avatar summary. The Unity bake helper must use the supplied original UnityEngine.Avatar asset as the production oracle.";
                return copy;
            }

            copy["humanBonesSource"] = "unityPrefab.originalAnimatorAvatar";
            copy["humanBonesNote"] =
                "HumanDescription humanBones are absent from the exported Avatar summary. The Unity bake helper must use the supplied Unity prefab and its original Animator.avatar as the production oracle.";
            return copy;
        }

        private static bool HasUsableHumanoidReferencePose(JObject avatar, string unityModelPrefab, string unityAvatarAsset)
        {
            if (!string.IsNullOrWhiteSpace(unityModelPrefab))
            {
                // 有 Unity 工程内 prefab 时，Unity helper 会优先使用 prefab 自带 Animator.avatar。
                // 这条路才是真正的 Unity Avatar oracle，不依赖我们用 glTF rest 临时拼 HumanDescription。
                return true;
            }
            if (!string.IsNullOrWhiteSpace(unityAvatarAsset))
            {
                // 原始 UnityEngine.Avatar 单独导入时，也由 Unity 自己完成 Humanoid retarget。
                // 这是原神 Avatar oracle 的显式路径，不影响默认项目。
                return true;
            }

            if (avatar == null)
            {
                return false;
            }

            var skeletonBones = avatar["skeletonBones"] as JArray;
            if (skeletonBones != null && skeletonBones.Count > 0)
            {
                return true;
            }

            // AvatarConstant/internalSolver 是 Unity 原始序列化元数据，但不是
            // UnityEngine.Avatar 实例，也不是 HumanDescription.skeletonBones。
            // 用它临时 BuildHumanAvatar 已经在原神样本中证明会出现背手/折腿，
            // 所以只能作为诊断输入，不能作为主线生产 bake 的参考姿态。
            return false;
        }

        private static void EnsureAvatarOracle(JObject avatar)
        {
            if (avatar == null || avatar["oracle"] is JObject)
            {
                return;
            }

            var oracle = BuildOracleFromLegacyInternalSolver(avatar["internalSolver"] as JObject);
            if (oracle == null)
            {
                return;
            }

            avatar["oracle"] = oracle;
            avatar["oracleSource"] = "Unity AvatarConstant via legacy internalSolver field";
        }

        private static JObject BuildOracleFromLegacyInternalSolver(JObject solver)
        {
            if (solver == null || !HasCompleteLegacyAvatarConstant(solver))
            {
                return null;
            }

            var skeleton = solver["skeleton"] as JObject;
            var avatarSkeleton = solver["avatarSkeleton"] as JObject;
            var human = solver["human"] as JObject;
            var twist = solver["twist"] as JObject;
            return new JObject
            {
                ["version"] = 1,
                ["source"] = "Unity AvatarConstant",
                ["sourceField"] = "avatar.internalSolver",
                ["rule"] = "Recovered from legacy AnimeStudio AvatarConstant payload. Unity bake may use this as Avatar oracle after Unity AvatarBuilder validation.",
                ["humanBoneIndex"] = solver["humanBoneIndex"]?.DeepClone(),
                ["humanBoneMass"] = human?["humanBoneMass"]?.DeepClone(),
                ["humanSkeletonIndexArray"] = solver["humanSkeletonIndexArray"]?.DeepClone(),
                ["humanSkeletonReverseIndexArray"] = solver["humanSkeletonReverseIndexArray"]?.DeepClone(),
                ["human"] = new JObject
                {
                    ["scale"] = solver["scale"],
                    ["armTwist"] = twist?["armTwist"],
                    ["foreArmTwist"] = twist?["foreArmTwist"],
                    ["upperLegTwist"] = twist?["upperLegTwist"],
                    ["legTwist"] = twist?["legTwist"],
                    ["armStretch"] = twist?["armStretch"],
                    ["legStretch"] = twist?["legStretch"],
                    ["feetSpacing"] = twist?["feetSpacing"],
                    ["hasLeftHand"] = human?["hasLeftHand"],
                    ["hasRightHand"] = human?["hasRightHand"],
                    ["hasTDoF"] = solver["hasTranslationDoF"],
                },
                ["humanSkeleton"] = new JObject
                {
                    ["nodeCount"] = skeleton?["nodeCount"],
                    ["axesCount"] = skeleton?["axesCount"],
                    ["nodes"] = skeleton?["nodes"]?.DeepClone(),
                    ["axes"] = skeleton?["axes"]?.DeepClone(),
                    ["pose"] = skeleton?["humanSkeletonPose"]?.DeepClone(),
                    ["defaultPose"] = skeleton?["avatarDefaultPose"]?.DeepClone(),
                },
                ["avatarSkeleton"] = new JObject
                {
                    ["nodeCount"] = avatarSkeleton?["nodeCount"],
                    ["axesCount"] = avatarSkeleton?["axesCount"],
                    ["nodes"] = avatarSkeleton?["nodes"]?.DeepClone(),
                    ["axes"] = avatarSkeleton?["axes"]?.DeepClone(),
                    ["pose"] = avatarSkeleton?["pose"]?.DeepClone(),
                    ["defaultPose"] = avatarSkeleton?["defaultPose"]?.DeepClone(),
                },
                ["rootMotion"] = solver["rootMotion"]?.DeepClone(),
            };
        }

        private static bool HasCompleteAvatarOracle(JObject avatar)
        {
            if (avatar == null)
            {
                return false;
            }

            return HasCompleteOraclePayload(avatar["oracle"] as JObject)
                || HasCompleteLegacyAvatarConstant(avatar["internalSolver"] as JObject);
        }

        private static bool HasCompleteOraclePayload(JObject oracle)
        {
            var humanBoneIndex = oracle?["humanBoneIndex"] as JArray;
            var humanSkeleton = oracle?["humanSkeleton"] as JObject;
            var avatarSkeleton = oracle?["avatarSkeleton"] as JObject;
            var humanNodes = humanSkeleton?["nodes"] as JArray;
            var humanPose = humanSkeleton?["pose"] as JArray;
            var avatarNodes = avatarSkeleton?["nodes"] as JArray;
            var avatarDefaultPose = avatarSkeleton?["defaultPose"] as JArray;
            return HasValidHumanBoneIndex(humanBoneIndex)
                && humanNodes != null && humanNodes.Count > 0
                && humanPose != null && humanPose.Count >= humanNodes.Count
                && avatarNodes != null && avatarNodes.Count > 0
                && avatarDefaultPose != null && avatarDefaultPose.Count >= avatarNodes.Count;
        }

        private static bool HasCompleteLegacyAvatarConstant(JObject solver)
        {
            var humanBoneIndex = solver?["humanBoneIndex"] as JArray;
            var skeleton = solver?["skeleton"] as JObject;
            var nodes = skeleton?["nodes"] as JArray;
            var humanSkeletonPose = skeleton?["humanSkeletonPose"] as JArray;
            var avatarSkeleton = solver?["avatarSkeleton"] as JObject;
            var avatarSkeletonNodes = avatarSkeleton?["nodes"] as JArray;
            var avatarSkeletonDefaultPose = avatarSkeleton?["defaultPose"] as JArray;
            return HasValidHumanBoneIndex(humanBoneIndex)
                && nodes != null && nodes.Count > 0
                && humanSkeletonPose != null && humanSkeletonPose.Count >= nodes.Count
                && avatarSkeletonNodes != null && avatarSkeletonNodes.Count > 0
                && avatarSkeletonDefaultPose != null && avatarSkeletonDefaultPose.Count >= avatarSkeletonNodes.Count;
        }

        private static string[] DeriveHumanBonesFromInternalSolver(JObject avatar)
        {
            var solver = avatar?["internalSolver"] as JObject;
            var humanBoneIndex = solver?["humanBoneIndex"] as JArray;
            var nodes = solver?["skeleton"]?["nodes"] as JArray;
            if (nodes == null || nodes.Count == 0)
            {
                // 有些游戏的 AvatarConstant 没有填 m_Human.m_Skeleton，但 m_AvatarSkeleton
                // 保留了可复建 Unity Avatar 的确定性骨架路径和 defaultPose。
                nodes = solver?["avatarSkeleton"]?["nodes"] as JArray;
            }
            if (!HasValidHumanBoneIndex(humanBoneIndex) || nodes == null || nodes.Count == 0)
            {
                return Array.Empty<string>();
            }

            var result = new List<string>();
            var last = (int)AnimeStudio.BoneType.Last;
            for (var i = 0; i < Math.Min(last, humanBoneIndex.Count); i++)
            {
                var nodeIndex = (int?)humanBoneIndex[i] ?? -1;
                if (nodeIndex < 0 || nodeIndex >= nodes.Count || nodes[nodeIndex] is not JObject node)
                {
                    continue;
                }

                var boneName = (string)node["name"];
                if (string.IsNullOrWhiteSpace(boneName))
                {
                    var path = (string)node["path"];
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        boneName = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                    }
                }
                if (string.IsNullOrWhiteSpace(boneName))
                {
                    continue;
                }

                var humanName = ((AnimeStudio.BoneType)i).ToString();
                result.Add($"{humanName}:{boneName}");
            }
            return result.ToArray();
        }

        private static bool HasValidHumanBoneIndex(JArray humanBoneIndex)
        {
            if (humanBoneIndex == null || humanBoneIndex.Count == 0)
            {
                return false;
            }

            return humanBoneIndex
                .Values<int?>()
                .Any(x => x.GetValueOrDefault(-1) >= 0);
        }

        private static bool Matches(string selector, params string[] values)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                return true;
            }
            if (values.Any(x => string.Equals(x, selector, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            var selectorPath = NormalizeSelectorPath(selector);
            var selectorFile = Path.GetFileName(selectorPath);
            var selectorStem = Path.GetFileNameWithoutExtension(selectorPath);
            foreach (var value in values.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                var valuePath = NormalizeSelectorPath(value);
                if (string.Equals(valuePath, selectorPath, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(selectorFile) && string.Equals(Path.GetFileName(valuePath), selectorFile, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(selectorStem) && string.Equals(Path.GetFileNameWithoutExtension(valuePath), selectorStem, StringComparison.OrdinalIgnoreCase))
                    || valuePath.EndsWith(selectorPath, StringComparison.OrdinalIgnoreCase)
                    || selectorPath.EndsWith(valuePath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // Windows 路径里的反斜杠会被 Regex 当成转义符。
            // 用户传入 glTF/anim 路径时，只按路径匹配，避免误命中其它模型。
            if (LooksLikePathSelector(selectorPath))
            {
                return false;
            }

            try
            {
                var regex = new Regex(selector, RegexOptions.IgnoreCase);
                return values.Where(x => !string.IsNullOrWhiteSpace(x)).Any(x => regex.IsMatch(x));
            }
            catch (ArgumentException)
            {
                return values.Where(x => !string.IsNullOrWhiteSpace(x)).Any(x => x.IndexOf(selector, StringComparison.OrdinalIgnoreCase) >= 0);
            }
        }

        private static string NormalizeSelectorPath(string value)
        {
            return (value ?? string.Empty)
                .Trim()
                .Trim('"')
                .Replace('\\', '/')
                .TrimEnd('/');
        }

        private static bool LooksLikePathSelector(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && (value.IndexOf('/') >= 0
                    || value.IndexOf('\\') >= 0
                    || string.Equals(Path.GetExtension(value), ".gltf", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetExtension(value), ".glb", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetExtension(value), ".anim", StringComparison.OrdinalIgnoreCase));
        }

        private static string ResolveAnimationAssetPath(JObject animation)
        {
            var explicitPath = (string)animation?["animationAsset"];
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                return explicitPath;
            }
            var animPath = (string)animation?["output"];
            if (string.IsNullOrWhiteSpace(animPath))
            {
                return null;
            }
            var sidecar = animPath + ".animation_asset.json";
            return File.Exists(sidecar) ? sidecar : null;
        }

        private static string ResolveSourcePath(string indexedSourcePath, string sourceRootOverride)
        {
            if (string.IsNullOrWhiteSpace(indexedSourcePath) || string.IsNullOrWhiteSpace(sourceRootOverride))
            {
                return indexedSourcePath;
            }
            if (!Directory.Exists(sourceRootOverride))
            {
                return indexedSourcePath;
            }

            var normalizedSource = indexedSourcePath.Replace('\\', '/');
            var lowerSource = normalizedSource.ToLowerInvariant();
            foreach (var anchor in new[] { "/streamingassets/", "/assets/", "/graphics/" })
            {
                var index = lowerSource.IndexOf(anchor, StringComparison.Ordinal);
                if (index < 0)
                {
                    continue;
                }
                var relative = normalizedSource[(index + 1)..].Replace('/', Path.DirectorySeparatorChar);
                var candidate = Path.Combine(sourceRootOverride, relative);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            return indexedSourcePath;
        }

        private static string SafeName(string value)
        {
            var name = string.IsNullOrWhiteSpace(value) ? "unity_bake" : value;
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        private static string BuildBatchFbxPath(string bakedFbxOutput, string batchOutput, string modelName, string animationName)
        {
            if (string.IsNullOrWhiteSpace(bakedFbxOutput))
            {
                return null;
            }

            var extension = Path.GetExtension(bakedFbxOutput);
            if (string.Equals(extension, ".fbx", StringComparison.OrdinalIgnoreCase))
            {
                var directory = Path.GetDirectoryName(bakedFbxOutput);
                var stem = Path.GetFileNameWithoutExtension(bakedFbxOutput);
                directory = string.IsNullOrWhiteSpace(directory)
                    ? Path.Combine(batchOutput, "Fbx")
                    : directory;
                return Path.Combine(directory, $"{stem}__{SafeName(modelName)}__{SafeName(animationName)}.fbx");
            }

            return Path.Combine(bakedFbxOutput, $"{SafeName(modelName)}__{SafeName(animationName)}.fbx");
        }

        private static BatchBakeApplyInfo ReadBatchBakeApplyInfo(string itemOutput)
        {
            var bakedPreviewDir = Path.Combine(itemOutput, "BakedPreview");
            if (Directory.Exists(bakedPreviewDir))
            {
                // UnityBakeResultApplier 会复制原始模型 glTF，再写出带动画名的新 glTF。
                // 批量报告必须记录 apply report 里的最终输出，不能随手拿目录里的第一个 glTF。
                var applyReport = Path.Combine(bakedPreviewDir, "unity_bake_apply_report.json");
                if (File.Exists(applyReport))
                {
                    try
                    {
                        var report = JObject.Parse(File.ReadAllText(applyReport));
                        var outputGltf = (string)report["outputGltf"];
                        var status = (string)report["status"];
                        var animationSolve = report["animationSolve"] as JObject;
                        var solvePath = (string)animationSolve?["path"];
                        var productionStatus = (string)animationSolve?["productionStatus"];
                        var requiresVisualValidation = (bool?)animationSolve?["requiresVisualValidation"];
                        var writesReusableGltfTrsCandidate = (bool?)animationSolve?["writesReusableGltfTrsCandidate"];
                        var reusableCandidateBlockedReasons = animationSolve?["reusableCandidateBlockedReasons"] as JArray;
                        var ikDiagnostic = report["unityBakeEditorCurveIkGoalDriverDiagnostic"] as JObject;
                        var ikGoalDriverDiagnosticEnabled =
                            (bool?)animationSolve?["ikGoalDriverDiagnosticEnabled"]
                            ?? (bool?)ikDiagnostic?["enabled"];
                        var ikGoalDriverCallCount =
                            (int?)animationSolve?["ikGoalDriverCallCount"]
                            ?? (int?)ikDiagnostic?["callCount"]
                            ?? 0;
                        var ikGoalDriverAppliedGoalCount =
                            (int?)animationSolve?["ikGoalDriverAppliedGoalCount"]
                            ?? (int?)ikDiagnostic?["appliedGoalCount"]
                            ?? 0;
                        var sampleRecoverableSkippedLayersDiagnostic =
                            (bool?)report["unityBakeRequestSampleRecoverableSkippedLayersDiagnostic"];
                        var animatorControllerDiagnosticSampledSkippedLayerCount =
                            (int?)animationSolve?["animatorControllerDiagnosticSampledSkippedLayerCount"]
                            ?? (int?)report["unityBakeAnimatorControllerDiagnosticSampledSkippedLayerCount"]
                            ?? 0;
                        var animatorControllerDiagnosticSampledSkippedLayerNames =
                            report["unityBakeAnimatorControllerDiagnosticSampledSkippedLayerNames"] as JArray;
                        var animatorControllerRecoverableSkippedLayerSummary =
                            animationSolve?["animatorControllerRecoverableSkippedLayerSummary"] as JObject
                            ?? report["unityBakeAnimatorControllerRecoverableSkippedLayerSummary"] as JObject;
                        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                        {
                            return new BatchBakeApplyInfo(
                                null,
                                status,
                                (string)report["message"],
                                (int?)report["frameVaryingTracks"] ?? 0,
                                (int?)report["frameVaryingChannels"] ?? 0,
                                (int?)report["firstPoseChangedTracks"] ?? 0,
                                (int?)report["coreBodyFirstPoseChangedTracks"] ?? 0,
                                animationSolve,
                                solvePath,
                                productionStatus,
                                requiresVisualValidation,
                                writesReusableGltfTrsCandidate,
                                reusableCandidateBlockedReasons,
                                ikGoalDriverDiagnosticEnabled,
                                ikGoalDriverCallCount,
                                ikGoalDriverAppliedGoalCount,
                                sampleRecoverableSkippedLayersDiagnostic,
                                animatorControllerDiagnosticSampledSkippedLayerCount,
                                animatorControllerDiagnosticSampledSkippedLayerNames,
                                animatorControllerRecoverableSkippedLayerSummary);
                        }
                        if (!string.IsNullOrWhiteSpace(outputGltf) && File.Exists(outputGltf))
                        {
                            return new BatchBakeApplyInfo(
                                outputGltf,
                                status,
                                (string)report["message"],
                                (int?)report["frameVaryingTracks"] ?? 0,
                                (int?)report["frameVaryingChannels"] ?? 0,
                                (int?)report["firstPoseChangedTracks"] ?? 0,
                                (int?)report["coreBodyFirstPoseChangedTracks"] ?? 0,
                                animationSolve,
                                solvePath,
                                productionStatus,
                                requiresVisualValidation,
                                writesReusableGltfTrsCandidate,
                                reusableCandidateBlockedReasons,
                                ikGoalDriverDiagnosticEnabled,
                                ikGoalDriverCallCount,
                                ikGoalDriverAppliedGoalCount,
                                sampleRecoverableSkippedLayersDiagnostic,
                                animatorControllerDiagnosticSampledSkippedLayerCount,
                                animatorControllerDiagnosticSampledSkippedLayerNames,
                                animatorControllerRecoverableSkippedLayerSummary);
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.Warning($"Unable to read Unity bake apply report: {applyReport}; {e.Message}");
                    }
                }

                var fallback = Directory.EnumerateFiles(bakedPreviewDir, "*.gltf", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    return new BatchBakeApplyInfo(fallback, "unknown", null, 0, 0, 0, 0, null, null, null, null, null, null, null, 0, 0, null, 0, null);
                }
            }

            var resultPath = Path.Combine(itemOutput, "unity_bake_result.json");
            if (File.Exists(resultPath))
            {
                try
                {
                    var result = JObject.Parse(File.ReadAllText(resultPath));
                    if (!string.Equals((string)result["status"], "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        return new BatchBakeApplyInfo(
                            null,
                            NeedsAnimatorControllerContext((string)result["message"])
                                ? "needs_animator_controller_context"
                                : "failed",
                            (string)result["message"],
                            0,
                            0,
                            0,
                            0,
                            null,
                                null,
                                null,
                                null,
                                null,
                                null,
                                null,
                                0,
                                0,
                                null,
                                0,
                                null);
                    }
                }
                catch (Exception e)
                {
                    Logger.Warning($"Unable to read Unity bake result: {resultPath}; {e.Message}");
                }
            }

            return new BatchBakeApplyInfo(null, null, null, 0, 0, 0, 0, null, null, null, null, null, null, null, 0, 0, null, 0, null);
        }

        private static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";

        private static bool IsAnimatorControllerContextCacheStatus(string status, string message)
        {
            return string.Equals(status, "needs_animator_controller_context", StringComparison.OrdinalIgnoreCase)
                && NeedsAnimatorControllerContext(message);
        }

        private static bool NeedsAnimatorControllerContext(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            return message.IndexOf("isHumanMotion=false", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("AnimatorController context", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("AnimatorController auxiliary", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("non-body layer", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("baseLayerClip", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private sealed record PreviewSelection(JObject Model, JObject Animation);

        private sealed record AvatarAssetResolution(string AssetPath, string Source, string MatchKey);

        private sealed record AnimationClipAssetResolution(string AssetPath, string Source, string MatchKey);

        private sealed record AnimatorControllerAssetResolution(string AssetPath, string Source, string MatchKey);

        private sealed record FloatCurveKey(float Time, float Value);

        private sealed record BatchBakeApplyInfo(
            string BakedGltfPath,
            string Status,
            string Message,
            int FrameVaryingTracks,
            int FrameVaryingChannels,
            int FirstPoseChangedTracks,
            int CoreBodyFirstPoseChangedTracks,
            JObject AnimationSolve,
            string SolvePath,
            string ProductionStatus,
            bool? RequiresVisualValidation,
            bool? WritesReusableGltfTrsCandidate,
            JArray ReusableCandidateBlockedReasons,
            bool? IkGoalDriverDiagnosticEnabled,
            int IkGoalDriverCallCount,
            int IkGoalDriverAppliedGoalCount,
            bool? SampleRecoverableSkippedLayersDiagnostic,
            int AnimatorControllerDiagnosticSampledSkippedLayerCount,
            JArray AnimatorControllerDiagnosticSampledSkippedLayerNames,
            JObject AnimatorControllerRecoverableSkippedLayerSummary = null);
    }
}
