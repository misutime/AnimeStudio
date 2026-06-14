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
            string unityAvatarAsset,
            string unityBakeOutput,
            int frameRate,
            bool probeMuscles,
            bool runUnityBake,
            string unityBakeWorkerQueue = null,
            string bakedGltfOutput = null,
            string bakedFbxOutput = null,
            string blender = null
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
                unityAvatarAsset,
                unityBakeOutput,
                frameRate,
                probeMuscles,
                runUnityBake,
                unityBakeWorkerQueue,
                bakedGltfOutput,
                bakedFbxOutput,
                blender);
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
            string unityAvatarAsset,
            string unityBakeOutput,
            int frameRate,
            bool probeMuscles,
            bool runUnityBake,
            string unityBakeWorkerQueue = null,
            string bakedGltfOutput = null,
            string bakedFbxOutput = null,
            string blender = null,
            string indexPath = null
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
            var importedAvatarAssets = DiscoverImportedAvatarAssets(unityProject);
            var selection = SelectExplicitCandidateFromLibraryDb(
                dbPath,
                modelSelector,
                animationSelector,
                allowSuppliedAvatarAsset: !string.IsNullOrWhiteSpace(unityAvatarAsset),
                importedAvatarAssets);
            if (selection == null)
            {
                Logger.Error("No explicit model-animation candidate matched the SQLite Unity bake request selectors.");
                return;
            }

            var effectiveUnityAvatarAsset = ResolveUnityAvatarAssetForSelection(selection, unityAvatarAsset, importedAvatarAssets).AssetPath;
            ResolveSelectionLibraryPaths(selection, libraryRoot);
            GenerateSelection(
                dbPath,
                selection,
                outputDirectory,
                sourceRootOverride,
                unityProject,
                unityEditor,
                unityModelPrefab,
                unityAnimationClip,
                effectiveUnityAvatarAsset,
                unityBakeOutput,
                frameRate,
                probeMuscles,
                runUnityBake,
                unityBakeWorkerQueue,
                bakedGltfOutput,
                bakedFbxOutput,
                blender);
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
            string unityAvatarAsset,
            int frameRate,
            bool runUnityBake,
            string unityBakeWorkerQueue = null,
            string bakedFbxOutput = null,
            string blender = null,
            int limit = 10,
            string indexPath = null,
            bool force = false
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
                WriteSummaryOnlyBakeBatchReport(dbPath, libraryRoot, output, runUnityBake, force);
                TryCompactBakeCache(dbPath, libraryRoot);
                TryWriteBakeCacheSummary(dbPath, output, libraryRoot, fullScan: true, unityProject);
                Logger.Info("Unity bake batch limit is 0; wrote reports only and skipped request generation.");
                return;
            }

            limit = Math.Max(1, limit);
            if (!ValidateSuppliedUnityAvatarAssetScope(modelSelector, unityAvatarAsset))
            {
                return;
            }
            var importedAvatarAssets = DiscoverImportedAvatarAssets(unityProject);

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
                        Logger.Info($"Unity bake batch has no pending candidates because {cachedSelections.Length} matching candidate(s) in this batch window are already processed by trusted bake, static_pose, or needs_review. Use --preview_validation_force to rebuild them.");
                        return;
                    }
                }

                Logger.Error("No explicit Humanoid/Muscle model-animation candidate matched the Unity bake batch selectors.");
                return;
            }
            if (!ValidateSuppliedUnityAvatarAssetSelections(unityAvatarAsset, selections))
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

                var requestPath = GenerateSelection(
                    dbPath,
                    selection,
                    itemOutput,
                    sourceRootOverride,
                    unityProject,
                    unityEditor,
                    unityModelPrefab,
                    unityAnimationClip,
                    effectiveUnityAvatarAsset,
                    null,
                    frameRate,
                    probeMuscles: false,
                    runUnityBake,
                    unityBakeWorkerQueue,
                    bakedGltfOutput: null,
                    bakedFbxOutput: itemFbx,
                    blender);

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
                        ? (bakedOk ? "baked" : staticPose ? "static_pose" : needsReview ? "needs_review" : "failed")
                        : (requestWritten ? "request_written" : "failed"),
                    ["model"] = modelName,
                    ["animation"] = animationName,
                    ["modelOutput"] = (string)selection.Model["model"]?["output"],
                    ["animationOutput"] = (string)selection.Animation["output"],
                    ["relationSource"] = (string)selection.Animation["relationSource"],
                    ["confidence"] = (string)selection.Animation["confidence"],
                    ["unityAvatarAsset"] = effectiveUnityAvatarAsset,
                    ["avatarSource"] = avatarResolution.Source,
                    ["avatarMatchKey"] = avatarResolution.MatchKey,
                    ["request"] = requestPath,
                    ["result"] = resultPath,
                    ["bakedGltf"] = bakedGltf,
                    ["bakedFbx"] = itemFbx,
                    ["applyStatus"] = bakeApply.Status,
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
                ["rule"] = "Only relation_source=explicit Humanoid/Muscle candidates are selected. Matching candidates in this batch window were already processed by trusted bake, static_pose, or needs_review and skipped.",
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

        private static void WriteSummaryOnlyBakeBatchReport(
            string dbPath,
            string libraryRoot,
            string output,
            bool runUnityBake,
            bool force)
        {
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
                ["items"] = new JArray(),
            };
            var reportPath = Path.Combine(output, "unity_bake_batch_report.json");
            File.WriteAllText(reportPath, JsonConvert.SerializeObject(report, Formatting.Indented));
            Logger.Info($"Unity bake batch report: {reportPath}");
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

        private static string GenerateSelection(
            string indexPath,
            PreviewSelection selection,
            string outputDirectory,
            string sourceRootOverride,
            string unityProject,
            string unityEditor,
            string unityModelPrefab,
            string unityAnimationClip,
            string unityAvatarAsset,
            string unityBakeOutput,
            int frameRate,
            bool probeMuscles,
            bool runUnityBake,
            string unityBakeWorkerQueue,
            string bakedGltfOutput,
            string bakedFbxOutput,
            string blender
        )
        {
            var model = selection.Model["model"] as JObject;
            var animation = selection.Animation;
            var modelName = (string)model?["name"];
            var animationName = (string)animation?["name"];
            if (string.IsNullOrWhiteSpace(modelName) || string.IsNullOrWhiteSpace(animationName))
            {
                Logger.Error("Selected Unity bake request entry is missing model or animation name.");
                return null;
            }

            var requiresHumanoidBake = (bool?)animation?["requiresHumanoidBake"] ?? false;
            var avatar = PrepareAvatarForUnityBake(model?["avatar"] as JObject, requiresHumanoidBake, unityModelPrefab, unityAvatarAsset);
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

            var request = new
            {
                version = 1,
                generatedAt = DateTime.UtcNow.ToString("O"),
                rule = "Production path: Unity Animator/Avatar samples Humanoid/Muscle motion and AnimeStudio writes the sampled target skeleton TRS back into glTF. Internal Humanoid formula solving is experimental and not used here.",
                sourceIndex = Path.GetFullPath(indexPath),
                sourceRootOverride,
                frameRate = Math.Max(1, frameRate),
                // 诊断开关：让 Unity 输出单 muscle 对骨骼的真实影响，用来标定内部 Humanoid 求解器。
                probeMuscles,
                outputJson = resultPath,
                logJson = Path.Combine(output, "unity_bake_report.json"),
                unityProject,
                unityAssetPaths = new
                {
                    // 这两个路径必须是 Unity 工程里的 Assets/... 路径；Unity helper 不按游戏名猜资源。
                    modelPrefab = unityModelPrefab,
                    animationClip = unityAnimationClip,
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
                        relation = (string)animation?["relation"],
                        relationSource = (string)animation?["relationSource"],
                        confidence = (string)animation?["confidence"],
                        score = (int?)animation?["score"] ?? 0,
                    },
                },
                validation = new
                {
                    expected = "Unity helper should output non-zero changed tracks on core body bones. If Humanoid clip is used without Animator.avatar, the bake must fail instead of producing guessed data.",
                    nextStep = "After unity_bake_result.json is produced, AnimeStudio can merge sampled TRS into glTF/GLB preview or animation pack.",
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
            CreateTempUnityBakeAvatarReadyModelTable(connection);
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
    COALESCE(json_extract(c.raw_json, '$.requiresUnityBake'), 0) = 1
    OR COALESCE(json_extract(c.raw_json, '$.legacyUnityBakeSupported'), 0) = 1
    OR COALESCE(json_extract(c.raw_json, '$.requiresInternalHumanoidSolve'), 0) = 1
    OR COALESCE(json_extract(c.raw_json, '$.fullHumanoidBakeRequired'), 0) = 1
    OR COALESCE(json_extract(c.raw_json, '$.productionUnityBakeReady'), 0) = 1
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
                animation["relation"] = (string)relation["relation"] ?? (string)relation["relationSource"] ?? "library.sqlite.candidate";
                animation["relationSource"] = (string)relation["relationSource"] ?? "explicit";
                animation["confidence"] = (string)relation["confidence"] ?? "explicit_unity_source_index";
                animation["score"] = (int?)relation["score"] ?? 100;
                animation["candidate"] = relation;
                animation["requiresHumanoidBake"] = true;
                ResolveBatchCandidatePaths(model, animation, libraryRoot);
                if (!allowSuppliedAvatarAsset
                    && !ModelHasBakeReadyAvatar(model)
                    && string.IsNullOrWhiteSpace(ResolveUnityAvatarAsset(model, importedAvatarAssets)))
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
    updated_utc TEXT,
    PRIMARY KEY(model_output, animation_output)
);";
            command.ExecuteNonQuery();
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
                        && !string.Equals(x.Status, "needs_review", StringComparison.OrdinalIgnoreCase))
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

                var importedAvatarAssets = DiscoverImportedAvatarAssets(unityProject);
                var importedAvatarAssetFileCount = importedAvatarAssets.Values
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
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
                var uniqueCounts = BuildUniqueBakeCacheCounts(connection, libraryRoot, bakeReadyExplicitUnityBakeCandidates, uniqueBakeReadyExplicitUnityBakeCandidates);
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
                    ["importedAvatarAssetKeyCount"] = importedAvatarAssets.Count,
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
                    ["cacheCoverage"] = Ratio(cachedCandidates, bakeReadyExplicitUnityBakeCandidates),
                    ["bakedCoverage"] = Ratio(bakedCandidates, bakeReadyExplicitUnityBakeCandidates),
                    ["trustedBakedCoverage"] = Ratio(trustedBakedCandidates, bakeReadyExplicitUnityBakeCandidates),
                    ["cacheCoveragePercent"] = Percent(cachedCandidates, bakeReadyExplicitUnityBakeCandidates),
                    ["bakedCoveragePercent"] = Percent(bakedCandidates, bakeReadyExplicitUnityBakeCandidates),
                    ["trustedBakedCoveragePercent"] = Percent(trustedBakedCandidates, bakeReadyExplicitUnityBakeCandidates),
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
                    && IsTrustedAvatarBake(report);
            }
            catch
            {
                return false;
            }
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
            return !string.Equals(source, "avatar_constant_oracle_unity_validated", StringComparison.OrdinalIgnoreCase);
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
SELECT bc.model_output, bc.animation_output, bc.status, bc.baked_gltf_path
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
            var uniqueFailedCandidates = groups.Values.Count(x => !x.HasBaked && !x.HasStaticPose && !x.HasNeedsReview && x.HasFailed);
            var uniqueRequestWrittenCandidates = groups.Values.Count(x => !x.HasBaked && !x.HasStaticPose && !x.HasNeedsReview && !x.HasFailed && x.HasRequestWritten);
            var duplicateCacheRows = groups.Values.Sum(x => Math.Max(0, x.RowCount - 1));
            var terminalDiagnosticCandidates = uniqueStaticPoseCandidates + uniqueNeedsReviewCandidates;

            return new JObject
            {
                ["uniqueCachedCandidates"] = uniqueCachedCandidates,
                ["uniqueRequestWrittenCandidates"] = uniqueRequestWrittenCandidates,
                ["uniqueBakedCandidates"] = uniqueBakedCandidates,
                ["uniqueTrustedBakedCandidates"] = uniqueTrustedBakedCandidates,
                ["uniqueStaticPoseCandidates"] = uniqueStaticPoseCandidates,
                ["uniqueNeedsReviewCandidates"] = uniqueNeedsReviewCandidates,
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

        private static IReadOnlyDictionary<string, string> DiscoverImportedAvatarAssets(string unityProject)
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

            foreach (var path in Directory.EnumerateFiles(directory, "*.asset", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var unityPath = Path.GetRelativePath(unityProject, path).Replace('\\', '/');
                if (!unityPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
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

            return result;
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

        private static void CreateTempUnityBakeAvatarReadyModelTable(SqliteConnection connection)
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
                create.CommandText = @"
CREATE TEMP TABLE temp_unity_bake_avatar_ready_models AS
SELECT m.output AS output
FROM assets m
WHERE m.kind='Model'
  AND (
    " + BakeReadyAvatarSql("m") + @"
    OR " + ImportedAvatarAssetMatchSql("m") + @"
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
     , CASE WHEN " + ImportedAvatarAssetMatchSql("m") + @" THEN 1 ELSE 0 END AS imported_avatar_ready
FROM model_animation_candidates c
JOIN assets m ON m.kind='Model' AND m.output=c.model_output
WHERE c.relation_source='explicit'
  AND (
    COALESCE(json_extract(c.raw_json, '$.requiresUnityBake'), 0)=1
    OR COALESCE(json_extract(c.raw_json, '$.legacyUnityBakeSupported'), 0)=1
    OR COALESCE(json_extract(c.raw_json, '$.requiresInternalHumanoidSolve'), 0)=1
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

        private static string ImportedAvatarAssetMatchSql(string modelAlias)
        {
            var raw = $"{modelAlias}.raw_json";
            return $@"EXISTS (
    SELECT 1
    FROM temp_imported_avatar_asset_keys importedAvatar
    WHERE importedAvatar.key = COALESCE(json_extract({raw}, '$.avatar.name'), '')
       OR importedAvatar.key = COALESCE({modelAlias}.name, '')
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

            return new AvatarAssetResolution(null, "model_human_description_or_prefab", null);
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

        private static IEnumerable<string> BuildUnityAvatarLookupKeys(JObject model)
        {
            var avatarName = (string)model?["avatar"]?["name"];
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
      COALESCE(json_extract(c.raw_json, '$.requiresUnityBake'), 0)=1
      OR COALESCE(json_extract(c.raw_json, '$.legacyUnityBakeSupported'), 0)=1
      OR COALESCE(json_extract(c.raw_json, '$.requiresInternalHumanoidSolve'), 0)=1
    )
    AND {BakeReadyAvatarSql("m")}
)";
        }

        private static void UpsertBakeCache(SqliteConnection connection, SqliteTransaction transaction, JObject item, string libraryRoot)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO animation_bake_cache(model_output, animation_output, status, request_path, result_path, baked_gltf_path, baked_fbx_path, message, bake_mode, updated_utc)
VALUES ($modelOutput, $animationOutput, $status, $requestPath, $resultPath, $bakedGltfPath, $bakedFbxPath, $message, $bakeMode, $updatedUtc)
ON CONFLICT(model_output, animation_output) DO UPDATE SET
    status=CASE
        WHEN animation_bake_cache.status IN ('baked', 'static_pose', 'needs_review') AND excluded.status NOT IN ('baked', 'static_pose', 'needs_review') THEN animation_bake_cache.status
        ELSE excluded.status
    END,
    request_path=COALESCE(excluded.request_path, animation_bake_cache.request_path),
    result_path=CASE
        WHEN animation_bake_cache.status IN ('baked', 'static_pose', 'needs_review') AND excluded.status NOT IN ('baked', 'static_pose', 'needs_review') THEN animation_bake_cache.result_path
        ELSE excluded.result_path
    END,
    baked_gltf_path=CASE
        WHEN animation_bake_cache.status IN ('baked', 'static_pose', 'needs_review') AND excluded.status NOT IN ('baked', 'static_pose', 'needs_review') THEN animation_bake_cache.baked_gltf_path
        ELSE excluded.baked_gltf_path
    END,
    baked_fbx_path=CASE
        WHEN animation_bake_cache.status IN ('baked', 'static_pose', 'needs_review') AND excluded.status NOT IN ('baked', 'static_pose', 'needs_review') THEN animation_bake_cache.baked_fbx_path
        ELSE excluded.baked_fbx_path
    END,
    message=CASE
        WHEN animation_bake_cache.status IN ('baked', 'static_pose', 'needs_review') AND excluded.status NOT IN ('baked', 'static_pose', 'needs_review') THEN animation_bake_cache.message
        ELSE excluded.message
    END,
    bake_mode=COALESCE(excluded.bake_mode, animation_bake_cache.bake_mode),
    updated_utc=excluded.updated_utc;";
            command.Parameters.AddWithValue("$modelOutput", CanonicalizeLibraryOutput((string)item["modelOutput"], libraryRoot));
            command.Parameters.AddWithValue("$animationOutput", CanonicalizeLibraryOutput((string)item["animationOutput"], libraryRoot));
            command.Parameters.AddWithValue("$status", (string)item["status"] ?? "failed");
            command.Parameters.AddWithValue("$requestPath", DbValue((string)item["request"]));
            command.Parameters.AddWithValue("$resultPath", DbValue((string)item["result"]));
            command.Parameters.AddWithValue("$bakedGltfPath", DbValue((string)item["bakedGltf"]));
            command.Parameters.AddWithValue("$bakedFbxPath", DbValue((string)item["bakedFbx"]));
            command.Parameters.AddWithValue("$message", DbValue((string)item["message"]));
            command.Parameters.AddWithValue("$bakeMode", "UnityBakeToGltf");
            command.Parameters.AddWithValue("$updatedUtc", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        private static object DbValue(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
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
SELECT model_output, animation_output, baked_gltf_path
FROM animation_bake_cache
WHERE status IN ('baked', 'static_pose', 'needs_review')
  AND COALESCE(model_output, '')<>''
  AND COALESCE(animation_output, '')<>'';";
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    var bakedGltf = reader.IsDBNull(2) ? null : reader.GetString(2);
                    // static_pose / needs_review 是已经确认的终态诊断结果，默认批量续跑时也应跳过；
                    // 需要重新验证时用 --preview_validation_force 显式重烘焙。
                    if (!IsTrustedBakedGltfPath(bakedGltf, libraryRoot)
                        && !IsStaticPoseBakedGltfPath(bakedGltf, libraryRoot)
                        && !IsNeedsReviewBakedGltfPath(bakedGltf, libraryRoot))
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
            if (!Directory.Exists(bakedPreviewDir))
            {
                return new BatchBakeApplyInfo(null, null, null, 0, 0, 0, 0);
            }

            // UnityBakeResultApplier 会复制原始模型 glTF，再写出带动画名的新 glTF。
            // 批量报告必须记录 apply report 里的最终输出，不能随手拿目录里的第一个 glTF。
            var applyReport = Path.Combine(bakedPreviewDir, "unity_bake_apply_report.json");
            if (File.Exists(applyReport))
            {
                try
                {
                    var report = JObject.Parse(File.ReadAllText(applyReport));
                    var outputGltf = (string)report["outputGltf"];
                    if (!string.IsNullOrWhiteSpace(outputGltf) && File.Exists(outputGltf))
                    {
                        return new BatchBakeApplyInfo(
                            outputGltf,
                            (string)report["status"],
                            (string)report["message"],
                            (int?)report["frameVaryingTracks"] ?? 0,
                            (int?)report["frameVaryingChannels"] ?? 0,
                            (int?)report["firstPoseChangedTracks"] ?? 0,
                            (int?)report["coreBodyFirstPoseChangedTracks"] ?? 0);
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
            return new BatchBakeApplyInfo(fallback, string.IsNullOrWhiteSpace(fallback) ? null : "unknown", null, 0, 0, 0, 0);
        }

        private static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";

        private sealed record PreviewSelection(JObject Model, JObject Animation);

        private sealed record AvatarAssetResolution(string AssetPath, string Source, string MatchKey);

        private sealed record BatchBakeApplyInfo(
            string BakedGltfPath,
            string Status,
            string Message,
            int FrameVaryingTracks,
            int FrameVaryingChannels,
            int FirstPoseChangedTracks,
            int CoreBodyFirstPoseChangedTracks);
    }
}
