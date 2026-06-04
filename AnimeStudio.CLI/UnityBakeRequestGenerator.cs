using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

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
                if (RunUnity(requestPath, unityProject, unityEditor, logPath))
                {
                    var bakedGltf = UnityBakeResultApplier.Apply(requestPath, bakedGltfOutput);
                    if (!string.IsNullOrWhiteSpace(bakedFbxOutput))
                    {
                        BlenderFbxExporter.Export(bakedGltf, bakedFbxOutput, blender);
                    }
                }
            }
        }

        private static bool RunUnity(string requestPath, string unityProject, string unityEditor, string logPath)
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
