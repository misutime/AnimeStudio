using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            string unityBakeOutput,
            int frameRate,
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
            var selection = SelectPreview(index, modelSelector, animationSelector);
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
                unityBakeOutput,
                frameRate,
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
            string unityBakeOutput,
            int frameRate,
            bool runUnityBake,
            string unityBakeWorkerQueue = null,
            string bakedGltfOutput = null,
            string bakedFbxOutput = null,
            string blender = null
        )
        {
            if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
            {
                Logger.Error($"Library root not found: {libraryRoot}");
                return;
            }

            var dbPath = Path.Combine(libraryRoot, "library_index.db");
            if (!File.Exists(dbPath))
            {
                Logger.Error($"library_index.db not found: {dbPath}. Rebuild the Library export or run --build_sqlite_index.");
                return;
            }

            var selection = SelectPreviewFromLibraryDb(dbPath, modelSelector, animationSelector);
            if (selection == null)
            {
                Logger.Error("No model/animation matched the SQLite Unity bake request selectors.");
                return;
            }

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
                unityBakeOutput,
                frameRate,
                runUnityBake,
                unityBakeWorkerQueue,
                bakedGltfOutput,
                bakedFbxOutput,
                blender);
        }

        private static void GenerateSelection(
            string indexPath,
            PreviewSelection selection,
            string outputDirectory,
            string sourceRootOverride,
            string unityProject,
            string unityEditor,
            string unityModelPrefab,
            string unityAnimationClip,
            string unityBakeOutput,
            int frameRate,
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
                return;
            }

            var requiresHumanoidBake = (bool?)animation?["requiresHumanoidBake"] ?? false;
            var avatar = model?["avatar"] as JObject;
            var humanBones = avatar?["humanBones"]?.Values<string>()?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray() ?? Array.Empty<string>();
            if (requiresHumanoidBake && humanBones.Length == 0)
            {
                Logger.Error(
                    "Selected Humanoid animation requires Unity Avatar humanBones, but model_animations.json only contains an Avatar summary. " +
                    "Re-export the sample with the current CLI so asset_catalog.jsonl/model_animations.json keeps full Unity Avatar HumanDescription data."
                );
                return;
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
                rule = "Humanoid/Muscle clips must be baked by Unity Animator/Avatar sampling. AnimeStudio keeps Unity relation indexes; Unity Editor writes skeleton TRS samples.",
                sourceIndex = Path.GetFullPath(indexPath),
                sourceRootOverride,
                frameRate = Math.Max(1, frameRate),
                outputJson = resultPath,
                logJson = Path.Combine(output, "unity_bake_report.json"),
                unityProject,
                unityAssetPaths = new
                {
                    // 这两个路径必须是 Unity 工程里的 Assets/... 路径；Unity helper 不按游戏名猜资源。
                    modelPrefab = unityModelPrefab,
                    animationClip = unityAnimationClip,
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
                Process.Start(new ProcessStartInfo
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
                Thread.Sleep(1000);
            }

            Logger.Error($"Unity bake worker did not become ready. Log: {workerLog}");
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

        private static PreviewSelection SelectPreview(JObject index, string modelSelector, string animationSelector)
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
            }
            return null;
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

        private static void ResolvePathProperty(JObject obj, string libraryRoot, string propertyName)
        {
            var value = (string)obj[propertyName];
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            obj[propertyName] = LibraryRelativePathMigrator.ResolveLibraryPath(libraryRoot, value);
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

        private static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";

        private sealed record PreviewSelection(JObject Model, JObject Animation);
    }
}
