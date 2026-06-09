using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AnimeStudio.CLI
{
    internal static class PreviewGltfGenerator
    {
        public static string Generate(
            string indexPath,
            string gameName,
            string modelSelector,
            string animationSelector,
            string outputDirectory,
            string sourceRootOverride = null
        )
        {
            if (string.IsNullOrWhiteSpace(gameName))
            {
                Logger.Error("--game is required with --generate_preview_gltf.");
                return null;
            }
            if (string.IsNullOrWhiteSpace(indexPath) || !File.Exists(indexPath))
            {
                Logger.Error($"model_animations.json not found: {indexPath}");
                return null;
            }

            var index = JObject.Parse(File.ReadAllText(indexPath));
            var selection = SelectPreview(index, modelSelector, animationSelector);
            if (selection == null)
            {
                Logger.Error("No model/animation candidate matched the preview selectors.");
                return null;
            }

            ResolveSelectionLibraryPaths(selection, Path.GetDirectoryName(Path.GetFullPath(indexPath)) ?? "");
            return GenerateSelection(indexPath, gameName, selection, outputDirectory, sourceRootOverride);
        }

        public static string GenerateFromLibrary(
            string libraryRoot,
            string gameName,
            string modelSelector,
            string animationSelector,
            string outputDirectory,
            string sourceRootOverride = null
        )
        {
            if (string.IsNullOrWhiteSpace(gameName))
            {
                Logger.Error("--game is required with --generate_preview_from_library.");
                return null;
            }
            if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
            {
                Logger.Error($"Library root not found: {libraryRoot}");
                return null;
            }

            var dbPath = Path.Combine(libraryRoot, "library_index.db");
            if (!File.Exists(dbPath))
            {
                Logger.Error($"library_index.db not found: {dbPath}. Rebuild the Library export or run --build_sqlite_index.");
                return null;
            }

            var selection = SelectPreviewFromLibraryDb(dbPath, modelSelector, animationSelector);
            if (selection == null)
            {
                Logger.Error("No model/animation matched the SQLite preview selectors.");
                return null;
            }

            ResolveSelectionLibraryPaths(selection, libraryRoot);
            return GenerateSelection(dbPath, gameName, selection, outputDirectory, sourceRootOverride);
        }

        private static string GenerateSelection(
            string indexPath,
            string gameName,
            PreviewSelection selection,
            string outputDirectory,
            string sourceRootOverride)
        {
            var modelName = (string)selection.Model["model"]?["name"];
            var animationName = (string)selection.Animation["name"];
            var modelSource = ResolveSourcePath((string)selection.Model["model"]?["source"], sourceRootOverride);
            var animationSource = ResolveSourcePath((string)selection.Animation["source"], sourceRootOverride);
            if (string.IsNullOrWhiteSpace(modelName) || string.IsNullOrWhiteSpace(animationName))
            {
                Logger.Error("Selected preview entry is missing model or animation name.");
                return null;
            }
            if (!File.Exists(modelSource) || !File.Exists(animationSource))
            {
                Logger.Error("Selected preview entry source files no longer exist. Re-run the Library export from an accessible game/source folder.");
                Logger.Error($"Model source: {modelSource}");
                Logger.Error($"Animation source: {animationSource}");
                return null;
            }

            var output = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(indexPath)) ?? Directory.GetCurrentDirectory(),
                    "Previews",
                    $"{SafeName(modelName)}__{SafeName(animationName)}"
                )
                : outputDirectory;
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
            Directory.CreateDirectory(output);

            if (FastPreviewGltfBuilder.TryGenerate(selection.Model, selection.Animation, animationName, output, out var fastGltfPath, out var fastMessage))
            {
                Logger.Info(fastMessage);
                var fastReport = GltfPreviewValidator.Validate(fastGltfPath, modelName, animationName, selection.Model, selection.Animation, Array.Empty<string>());
                var fastReportPath = Path.Combine(output, "preview_validation.json");
                File.WriteAllText(fastReportPath, JsonConvert.SerializeObject(fastReport, Formatting.Indented));
                Logger.Info($"Fast preview glTF: {fastGltfPath}");
                Logger.Info($"Preview validation: {fastReportPath}");
                return fastGltfPath;
            }
            Logger.Info($"Fast preview skipped: {fastMessage}");
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
            Directory.CreateDirectory(output);

            var inputRoot = GetCommonRoot(new[] { Path.GetDirectoryName(modelSource), Path.GetDirectoryName(animationSource) });
            var modelFilter = BuildSourceFilter(modelSource);
            var animationFilter = BuildSourceFilter(animationSource);
            var sourceFilter = $"({modelFilter})|({animationFilter})";
            var names = $"^{Regex.Escape(modelName)}$|^{Regex.Escape(animationName)}$";
            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            {
                Logger.Error("Unable to resolve current CLI executable path.");
                return null;
            }

            Logger.Info($"Generating preview glTF: {modelName} + {animationName}");
            var args = new List<string>
            {
                Quote(inputRoot),
                Quote(output),
                "--game", Quote(gameName),
                "--mode", "Library",
                "--group_assets", "ByLibrary",
                "--profile_3d", "All",
                "--model_format", "Gltf",
                "--texture_mode", "Png",
                "--animation_package", "Both",
                "--fbx_animation", "All",
                "--names", Quote(names),
                "--containers", Quote(sourceFilter),
                "--batch_files", "64",
                "--profile_log", "off",
                "--silent",
            };

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = string.Join(" ", args),
                WorkingDirectory = Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(stdout))
            {
                Logger.Info(stdout.Trim());
            }
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                Logger.Warning(stderr.Trim());
            }
            if (process.ExitCode != 0)
            {
                Logger.Error($"Preview export failed with exit code {process.ExitCode}.");
                return null;
            }

            var gltfPath = Directory
                .EnumerateFiles(output, $"{SafeName(modelName)}.gltf", SearchOption.AllDirectories)
                .FirstOrDefault()
                ?? Directory.EnumerateFiles(output, "*.gltf", SearchOption.AllDirectories).FirstOrDefault();
            if (gltfPath == null)
            {
                Logger.Error($"Preview export did not produce a glTF file in {output}.");
                return null;
            }

            var exportIssues = ExtractExportIssues(stdout, stderr);
            var report = GltfPreviewValidator.Validate(gltfPath, modelName, animationName, selection.Model, selection.Animation, exportIssues);
            var reportPath = Path.Combine(output, "preview_validation.json");
            File.WriteAllText(reportPath, JsonConvert.SerializeObject(report, Formatting.Indented));
            Logger.Info($"Preview glTF: {gltfPath}");
            Logger.Info($"Preview validation: {reportPath}");
            return gltfPath;
        }

        public static void GeneratePack(
            string indexPath,
            string gameName,
            string modelSelector,
            string animationSelectors,
            string outputDirectory,
            int limit,
            string sourceRootOverride = null
        )
        {
            if (string.IsNullOrWhiteSpace(gameName))
            {
                Logger.Error("--game is required with --pack_model_animations.");
                return;
            }
            if (string.IsNullOrWhiteSpace(indexPath) || !File.Exists(indexPath))
            {
                Logger.Error($"model_animations.json not found: {indexPath}");
                return;
            }

            var index = JObject.Parse(File.ReadAllText(indexPath));
            var model = SelectModel(index, modelSelector);
            if (model == null)
            {
                Logger.Error("No model matched --preview_model for --pack_model_animations.");
                return;
            }

            var modelInfo = model["model"] as JObject;
            var modelName = (string)modelInfo?["name"];
            var output = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(indexPath)) ?? Directory.GetCurrentDirectory(),
                    "AnimationPacks",
                    SafeName(modelName ?? "Model")
                )
                : outputDirectory;
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
            Directory.CreateDirectory(output);

            var animations = SelectPackAnimations(model, animationSelectors, Math.Max(1, limit));
            if (animations.Count == 0)
            {
                Logger.Error("No animation candidates matched --pack_animations.");
                return;
            }

            Logger.Info($"Generating animation pack: {modelName} + {animations.Count} animation(s)");
            var results = new List<object>();
            foreach (var animation in animations)
            {
                var animationName = (string)animation["name"];
                var itemOutput = Path.Combine(output, SafeName(animationName ?? "Animation"));
                Generate(indexPath, gameName, modelName, $"^{Regex.Escape(animationName ?? string.Empty)}$", itemOutput, sourceRootOverride);

                var validationPath = Path.Combine(itemOutput, "preview_validation.json");
                JObject validation = null;
                if (File.Exists(validationPath))
                {
                    validation = JObject.Parse(File.ReadAllText(validationPath));
                }
                var gltfPath = validation == null
                    ? Directory.EnumerateFiles(itemOutput, "*.gltf", SearchOption.AllDirectories).FirstOrDefault()
                    : (string)validation["gltf"];
                results.Add(new
                {
                    model = modelName,
                    animation = animationName,
                    output = gltfPath,
                    validation = validationPath,
                    status = (string)validation?["status"] ?? "missing",
                    channels = (int?)validation?["counts"]?["channels"] ?? 0,
                    coreBoneNodes = (int?)validation?["animationCoverage"]?["coreBoneNodeCount"] ?? 0,
                    invalidChannels = (int?)validation?["counts"]?["invalidChannels"] ?? 0,
                    baked = (bool?)validation?["humanoid"]?["baked"] ?? false,
                    bakeMode = (string)validation?["humanoid"]?["bakeMode"],
                    bakedTrackCount = (int?)validation?["humanoid"]?["bakedTrackCount"] ?? 0,
                    bakedKeyframeCount = (int?)validation?["humanoid"]?["bakedKeyframeCount"] ?? 0,
                    exportIssueCount = (int?)validation?["exportIssues"]?["count"] ?? 0,
                    humanoid = validation?["humanoid"],
                    exportIssues = validation?["exportIssues"],
                    notes = validation?["notes"],
                });
            }

            var report = new
            {
                generatedAt = DateTime.UtcNow.ToString("O"),
                rule = "Each item is a reusable playable model+single-animation glTF generated from Unity relation indexes. Use these assets for small-scope validation before creating larger animation bundles.",
                index = indexPath,
                sourceRootOverride,
                model = modelInfo,
                requestedAnimations = animationSelectors,
                count = results.Count,
                animations = results,
            };
            var reportPath = Path.Combine(output, "animation_pack_report.json");
            File.WriteAllText(reportPath, JsonConvert.SerializeObject(report, Formatting.Indented));
            Logger.Info($"Animation pack report: {reportPath}");
        }

        private static JObject SelectModel(JObject index, string modelSelector)
        {
            return index["models"]?
                .OfType<JObject>()
                .FirstOrDefault(model =>
                {
                    var modelInfo = model["model"] as JObject;
                    return Matches(modelSelector, (string)modelInfo?["name"], (string)modelInfo?["output"]);
                });
        }

        private static List<JObject> SelectPackAnimations(JObject model, string animationSelectors, int limit)
        {
            var candidates = model["candidates"]?
                .OfType<JObject>()
                .OrderByDescending(x => (int?)x["score"] ?? 0)
                .ThenBy(x => (string)x["name"], StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<JObject>();
            if (string.IsNullOrWhiteSpace(animationSelectors))
            {
                return candidates.Take(limit).ToList();
            }

            var selectors = animationSelectors
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();
            var selected = new List<JObject>();
            foreach (var selector in selectors)
            {
                selected.AddRange(candidates.Where(x => Matches(selector, (string)x["name"], (string)x["output"])));
            }
            return selected
                .GroupBy(x => (string)x["name"], StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .Take(limit)
                .ToList();
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
                ["animationType"] = animation["animationType"],
                ["animationCapability"] = animation["animationCapability"],
                ["relation"] = "library.sqlite.selection",
                ["relationSource"] = "sqlite",
                ["confidence"] = "manual_preview_selection",
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

        private static string GetCommonRoot(IEnumerable<string> paths)
        {
            var fullPaths = paths
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => Path.GetFullPath(x).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .ToArray();
            if (fullPaths.Length == 0)
            {
                return Directory.GetCurrentDirectory();
            }
            var firstParts = fullPaths[0].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var common = new List<string>();
            for (var i = 0; i < firstParts.Length; i++)
            {
                var part = firstParts[i];
                if (fullPaths.All(x =>
                {
                    var parts = x.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    return parts.Length > i && string.Equals(parts[i], part, StringComparison.OrdinalIgnoreCase);
                }))
                {
                    common.Add(part);
                }
                else
                {
                    break;
                }
            }
            return string.Join(Path.DirectorySeparatorChar, common);
        }

        private static string BuildSourceFilter(string sourcePath)
        {
            var withoutExtension = Path.ChangeExtension(Path.GetFullPath(sourcePath), null)
                .Replace('\\', '/');
            return Regex.Escape(withoutExtension).Replace("/", "[\\\\/]");
        }

        private static string ResolveSourcePath(string indexedSourcePath, string sourceRootOverride)
        {
            if (string.IsNullOrWhiteSpace(sourceRootOverride))
            {
                return indexedSourcePath;
            }
            if (string.IsNullOrWhiteSpace(indexedSourcePath))
            {
                return indexedSourcePath;
            }
            if (!Directory.Exists(sourceRootOverride))
            {
                Logger.Warning($"--preview_source_root does not exist: {sourceRootOverride}");
                return indexedSourcePath;
            }

            var normalizedSource = indexedSourcePath.Replace('\\', '/');
            var lowerSource = normalizedSource.ToLowerInvariant();
            var anchors = new[]
            {
                "/streamingassets/",
                "/assets/",
                "/graphics/",
            };
            foreach (var anchor in anchors)
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

            var fileName = Path.GetFileName(indexedSourcePath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return indexedSourcePath;
            }
            var matches = Directory.EnumerateFiles(sourceRootOverride, fileName, SearchOption.AllDirectories)
                .Take(2)
                .ToArray();
            if (matches.Length == 1)
            {
                return matches[0];
            }
            Logger.Warning(matches.Length == 0
                ? $"Unable to resolve indexed source under --preview_source_root: {indexedSourcePath}"
                : $"Multiple files named {fileName} found under --preview_source_root; keeping indexed source path.");
            return indexedSourcePath;
        }

        private static string[] ExtractExportIssues(params string[] logs)
        {
            return logs
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .SelectMany(x => x.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                .Where(line =>
                    line.IndexOf("Unable to resolve", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("missing", StringComparison.OrdinalIgnoreCase) >= 0 && line.IndexOf("mesh", StringComparison.OrdinalIgnoreCase) >= 0
                )
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(128)
                .ToArray();
        }

        private static string SafeName(string value)
        {
            var name = string.IsNullOrWhiteSpace(value) ? "preview" : value;
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private sealed record PreviewSelection(JObject Model, JObject Animation);
    }

    internal static class GltfPreviewValidator
    {
        public static object Validate(string gltfPath, string expectedModelName, string expectedAnimationName, JObject modelIndex, JObject animationIndex, string[] exportIssues)
        {
            var gltf = JObject.Parse(File.ReadAllText(gltfPath));
            var directory = Path.GetDirectoryName(Path.GetFullPath(gltfPath));
            var buffers = LoadBuffers(gltf, directory);
            var nodes = gltf["nodes"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var animations = gltf["animations"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var skins = gltf["skins"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var meshes = gltf["meshes"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var animation = animations.FirstOrDefault(x => string.Equals((string)x["name"], expectedAnimationName, StringComparison.OrdinalIgnoreCase))
                ?? animations.FirstOrDefault();
            var channels = animation?["channels"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            var humanoid = ReadHumanoidExtras(animation);
            var invalidChannels = channels.Count(x =>
            {
                var nodeIndex = (int?)x["target"]?["node"];
                return nodeIndex == null || nodeIndex < 0 || nodeIndex >= nodes.Length;
            });
            var channelTargets = channels
                .Select(x => new
                {
                    node = (int?)x["target"]?["node"],
                    nodeName = NodeName(nodes, (int?)x["target"]?["node"]),
                    path = (string)x["target"]?["path"],
                })
                .ToArray();
            var coverage = AnalyzeAnimationCoverage(channelTargets.Select(x => (x.nodeName, x.path)));
            var morph = AnalyzeMorphTargets(meshes, channels, animationIndex);
            var skinJointCount = skins.Sum(x => x["joints"]?.Count() ?? 0);
            var visibleTransformChannelCount = CountChannelsAffectingVisibleMeshes(nodes, skins, channels);
            var motion = AnalyzeAnimationMotion(gltf, buffers, nodes, skins, animation, channels);
            var meshNode = nodes
                .Select((node, index) => new { node, index })
                .FirstOrDefault(x => x.node["mesh"] != null && x.node["skin"] != null)
                ?? nodes.Select((node, index) => new { node, index }).FirstOrDefault(x => x.node["mesh"] != null);

            var bbox = meshNode == null
                ? null
                : ComputeBounds(gltf, buffers, nodes, meshNode.index);
            exportIssues ??= Array.Empty<string>();
            var hasExperimentalHumanoidBake = humanoid?.baked == true
                && (humanoid.bakeMode ?? string.Empty).StartsWith("Approximate", StringComparison.OrdinalIgnoreCase);
            var nonCharacterTransformOk = string.Equals(
                    (string)animationIndex?["animationCapability"],
                    "NonCharacterTransformPreviewReady",
                    StringComparison.OrdinalIgnoreCase
                )
                && channels.Length > 0
                && invalidChannels == 0
                && visibleTransformChannelCount > 0
                && motion.visibleMovingChannelCount > 0;
            var morphOk = morph.expected
                && morph.meshTargetCount > 0
                && morph.targetCount > 0
                && morph.weightChannelCount > 0
                && invalidChannels == 0
                && motion.movingChannelCount > 0;
            var structurallyOk = animations.Length > 0
                && channels.Length > 0
                && invalidChannels == 0
                && motion.movingChannelCount > 0
                && (morphOk
                    || nonCharacterTransformOk
                    || (skins.Length > 0
                        && coverage.coreBoneChannelCount > 0
                        && coverage.coreBoneNodeCount >= 3))
                && bbox?.skinnedSizeLooksReasonable != false;
            var status = !structurallyOk || exportIssues.Length > 0
                ? "warning"
                : hasExperimentalHumanoidBake
                    ? "experimental"
                    : "ok";

            return new
            {
                generatedAt = DateTime.UtcNow.ToString("O"),
                status,
                gltf = gltfPath,
                expectedModel = expectedModelName,
                expectedAnimation = expectedAnimationName,
                model = modelIndex?["model"],
                animation = animationIndex,
                counts = new
                {
                    nodes = nodes.Length,
                    meshes = meshes.Length,
                    skins = skins.Length,
                    skinJoints = skinJointCount,
                    animations = animations.Length,
                    channels = channels.Length,
                    invalidChannels,
                    morphTargets = morph.targetCount,
                    weightChannels = morph.weightChannelCount,
                },
                exportIssues = new
                {
                    count = exportIssues.Length,
                    items = exportIssues,
                },
                animationCoverage = coverage,
                morph,
                nonCharacterTransform = new
                {
                    expected = nonCharacterTransformOk,
                    capability = (string)animationIndex?["animationCapability"],
                    channelCount = channels.Length,
                    visibleTransformChannelCount,
                    invalidChannels,
                },
                motion,
                humanoid,
                bounds = bbox,
                channelTargets = channelTargets.Take(128).ToArray(),
                notes = BuildNotes(animations.Length, channels.Length, skins.Length, invalidChannels, coverage, morph, nonCharacterTransformOk, motion, humanoid, bbox, exportIssues),
            };
        }

        private static HumanoidAnimationReport ReadHumanoidExtras(JObject animation)
        {
            var humanoid = animation?["extras"]?["unityHumanoid"] as JObject;
            if (humanoid == null)
            {
                return new HumanoidAnimationReport(false, false, false, null, 0, 0, 0, 0, Array.Empty<string>(), null);
            }

            var attributes = humanoid["attributes"]?.Values<string>()?.ToArray() ?? Array.Empty<string>();
            return new HumanoidAnimationReport(
                true,
                (bool?)humanoid["requiresBake"] ?? false,
                (bool?)humanoid["baked"] ?? false,
                (string)humanoid["bakeMode"],
                (int?)humanoid["bakedTrackCount"] ?? 0,
                (int?)humanoid["bakedKeyframeCount"] ?? 0,
                (int?)humanoid["muscleCurveCount"] ?? 0,
                (int?)humanoid["keyframeCount"] ?? 0,
                attributes.Take(128).ToArray(),
                humanoid["diagnostics"] as JObject
            );
        }

        private static AnimationCoverage AnalyzeAnimationCoverage(IEnumerable<(string nodeName, string path)> channelTargets)
        {
            var targets = channelTargets
                .Where(x => !string.IsNullOrWhiteSpace(x.nodeName))
                .ToArray();
            var core = targets
                .Where(x => IsCoreAnimatedBone(x.nodeName))
                .Select(x => x.nodeName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var twist = targets
                .Where(x => IsTwistOrHelperBone(x.nodeName))
                .Select(x => x.nodeName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var helper = targets
                .Where(x => IsAccessoryPoint(x.nodeName))
                .Select(x => x.nodeName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new AnimationCoverage(
                core.Length,
                targets.Count(x => IsCoreAnimatedBone(x.nodeName)),
                twist.Length,
                helper.Length,
                core,
                twist,
                helper
            );
        }

        private static MorphReport AnalyzeMorphTargets(JObject[] meshes, JObject[] channels, JObject animationIndex)
        {
            var meshTargetCount = 0;
            var targetCount = 0;
            var targetNames = new List<string>();
            foreach (var mesh in meshes)
            {
                var meshHasTargets = false;
                foreach (var primitive in mesh["primitives"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    var targets = primitive["targets"] as JArray;
                    if (targets == null || targets.Count == 0)
                    {
                        continue;
                    }
                    meshHasTargets = true;
                    targetCount += targets.Count;
                }
                if (meshHasTargets)
                {
                    meshTargetCount++;
                    targetNames.AddRange(mesh["extras"]?["targetNames"]?.Values<string>() ?? Enumerable.Empty<string>());
                }
            }

            var weightChannelCount = channels.Count(x => string.Equals((string)x["target"]?["path"], "weights", StringComparison.OrdinalIgnoreCase));
            var expectedBindingCount = (int?)animationIndex?["blendShapeBindingCount"] ?? 0;
            return new MorphReport(
                expectedBindingCount > 0 || weightChannelCount > 0,
                expectedBindingCount,
                meshTargetCount,
                targetCount,
                weightChannelCount,
                targetNames.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(128).ToArray()
            );
        }

        private static bool IsCoreAnimatedBone(string nodeName)
        {
            var name = NormalizeBoneName(nodeName);
            if (IsTwistOrHelperBone(nodeName) || IsAccessoryPoint(nodeName))
            {
                return false;
            }
            return name.Contains("pelvis")
                || name.Contains("spine")
                || name.Contains("neck")
                || name.Contains("head")
                || name.Contains("clavicle")
                || name.Contains("upperarm")
                || name.Contains("forearm")
                || name.Contains("hand")
                || name.Contains("thigh")
                || name.Contains("calf")
                || name.Contains("foot")
                || name.Contains("toe");
        }

        private static bool IsTwistOrHelperBone(string nodeName)
        {
            var name = NormalizeBoneName(nodeName);
            return name.Contains("twist") || name.Contains("helper");
        }

        private static bool IsAccessoryPoint(string nodeName)
        {
            var name = NormalizeBoneName(nodeName);
            return name.Contains("point") || name.Contains("socket") || name.Contains("attach");
        }

        private static string NormalizeBoneName(string nodeName)
        {
            return Regex.Replace((nodeName ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9]+", string.Empty);
        }

        private static List<string> BuildNotes(int animationCount, int channelCount, int skinCount, int invalidChannels, AnimationCoverage coverage, MorphReport morph, bool nonCharacterTransformOk, AnimationMotionReport motion, HumanoidAnimationReport humanoid, BoundsReport bounds, string[] exportIssues)
        {
            var notes = new List<string>();
            if (animationCount == 0) notes.Add("No animation was embedded in the preview glTF.");
            if (channelCount == 0) notes.Add("The embedded animation has no valid glTF channels.");
            if (skinCount == 0 && !nonCharacterTransformOk) notes.Add("No skin was exported for the preview model.");
            if (invalidChannels > 0) notes.Add($"{invalidChannels} animation channel(s) target missing nodes.");
            if (channelCount > 0 && motion.movingChannelCount == 0)
            {
                notes.Add("Animation channels were written, but all sampled outputs are static or zero-duration; treat this as a static pose, not a playable animation.");
            }
            else if (nonCharacterTransformOk && motion.visibleMovingChannelCount == 0)
            {
                notes.Add("Non-character animation channels exist, but none of the moving channels affect visible meshes.");
            }
            if (exportIssues?.Length > 0)
            {
                notes.Add($"{exportIssues.Length} export issue(s) were reported while generating the preview; inspect exportIssues before trusting the asset.");
            }
            if (morph?.expected == true)
            {
                if (morph.meshTargetCount > 0 && morph.weightChannelCount > 0)
                {
                    notes.Add($"BlendShape/morph animation is present: {morph.targetCount} morph target(s), {morph.weightChannelCount} glTF weights channel(s).");
                }
                else
                {
                    notes.Add($"BlendShape bindings were expected ({morph.expectedBindingCount}), but glTF morph targets or weights channels are missing.");
                }
            }
            if (nonCharacterTransformOk)
            {
                notes.Add("Non-character Transform animation channels target exported glTF nodes.");
            }
            if (humanoid?.requiresBake == true)
            {
                notes.Add($"Unity Humanoid/Muscle curves are present ({humanoid.muscleCurveCount} curves, {humanoid.keyframeCount} keyframes); they must be baked to skeleton TRS before the body animation can play in glTF.");
            }
            else if (humanoid?.baked == true)
            {
                notes.Add($"Unity Humanoid/Muscle curves were baked with {humanoid.bakeMode ?? "unknown"} into {humanoid.bakedTrackCount} skeleton track(s), {humanoid.bakedKeyframeCount} keyframes.");
                if ((humanoid.bakeMode ?? string.Empty).StartsWith("Approximate", StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add("This Humanoid/Muscle bake is approximate and experimental; it is useful for debugging channel generation, but it is not accepted as a correct reusable animation asset until visual/solver validation passes.");
                }
                var diagnosticsStatus = (string)humanoid.diagnostics?["status"];
                if (!string.IsNullOrWhiteSpace(diagnosticsStatus))
                {
                    notes.Add($"Humanoid bake diagnostics: {diagnosticsStatus}, mapped {(int?)humanoid.diagnostics?["mappedTargetCount"] ?? 0}/{(int?)humanoid.diagnostics?["targetCount"] ?? 0} target bone(s), sample times {(int?)humanoid.diagnostics?["sampleTimeCount"] ?? 0}.");
                }
            }
            if (coverage.coreBoneChannelCount == 0 && morph?.weightChannelCount == 0 && !nonCharacterTransformOk)
            {
                notes.Add("No core body bone channels were written; the preview animation currently affects only helper/accessory/twist nodes.");
            }
            else if (coverage.coreBoneNodeCount < 3 && !nonCharacterTransformOk)
            {
                notes.Add("Very few core body bones were animated; inspect whether this is an auxiliary clip rather than a body animation.");
            }
            if (bounds?.skinnedSizeLooksReasonable == false) notes.Add("Skinned rest bounds differ greatly from raw mesh bounds; inspect bind poses or joints.");
            return notes;
        }

        private static string NodeName(JObject[] nodes, int? index)
        {
            return index.HasValue && index.Value >= 0 && index.Value < nodes.Length
                ? (string)nodes[index.Value]["name"]
                : null;
        }

        private static int CountChannelsAffectingVisibleMeshes(JObject[] nodes, JObject[] skins, JObject[] channels)
        {
            var skinJoints = skins
                .SelectMany(skin => skin["joints"]?.Values<int>() ?? Enumerable.Empty<int>())
                .ToHashSet();
            var count = 0;
            foreach (var channel in channels)
            {
                var nodeIndex = (int?)channel["target"]?["node"];
                if (nodeIndex == null || nodeIndex < 0 || nodeIndex >= nodes.Length)
                {
                    continue;
                }
                if (skinJoints.Contains(nodeIndex.Value) || NodeOrDescendantHasMesh(nodes, nodeIndex.Value))
                {
                    count++;
                }
            }
            return count;
        }

        private static bool NodeOrDescendantHasMesh(JObject[] nodes, int nodeIndex)
        {
            if (nodeIndex < 0 || nodeIndex >= nodes.Length)
            {
                return false;
            }
            if (nodes[nodeIndex]["mesh"] != null)
            {
                return true;
            }
            foreach (var childIndex in nodes[nodeIndex]["children"]?.Values<int>() ?? Enumerable.Empty<int>())
            {
                if (NodeOrDescendantHasMesh(nodes, childIndex))
                {
                    return true;
                }
            }
            return false;
        }

        private static AnimationMotionReport AnalyzeAnimationMotion(JObject gltf, byte[][] buffers, JObject[] nodes, JObject[] skins, JObject animation, JObject[] channels)
        {
            var samplers = animation?["samplers"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            if (samplers.Length == 0 || channels.Length == 0)
            {
                return new AnimationMotionReport(0, 0, 0, 0, 0, Array.Empty<string>());
            }

            var movingSamplers = new HashSet<int>();
            var maxDuration = 0.0f;
            var maxOutputDelta = 0.0f;
            for (var i = 0; i < samplers.Length; i++)
            {
                var inputAccessor = (int?)samplers[i]["input"];
                var outputAccessor = (int?)samplers[i]["output"];
                if (inputAccessor == null || outputAccessor == null)
                {
                    continue;
                }

                var input = ReadAccessor(gltf, buffers, inputAccessor.Value);
                var output = ReadAccessor(gltf, buffers, outputAccessor.Value);
                var duration = input.Length == 0 ? 0 : input.Max(x => x[0]) - input.Min(x => x[0]);
                var delta = MaxOutputDelta(output);
                maxDuration = Math.Max(maxDuration, duration);
                maxOutputDelta = Math.Max(maxOutputDelta, delta);
                if (duration > 0.0001f && delta > 0.0001f)
                {
                    movingSamplers.Add(i);
                }
            }

            var movingChannelCount = 0;
            var visibleMovingChannelCount = 0;
            var visibleTargets = new List<string>();
            foreach (var channel in channels)
            {
                var samplerIndex = (int?)channel["sampler"];
                if (samplerIndex == null || !movingSamplers.Contains(samplerIndex.Value))
                {
                    continue;
                }

                movingChannelCount++;
                var nodeIndex = (int?)channel["target"]?["node"];
                var nodeName = NodeName(nodes, nodeIndex);
                if (NodeAffectsVisibleMesh(nodes, skins, nodeIndex))
                {
                    visibleMovingChannelCount++;
                    if (!string.IsNullOrWhiteSpace(nodeName))
                    {
                        visibleTargets.Add(nodeName);
                    }
                }
            }

            return new AnimationMotionReport(
                maxDuration,
                maxOutputDelta,
                movingSamplers.Count,
                movingChannelCount,
                visibleMovingChannelCount,
                visibleTargets.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(128).ToArray()
            );
        }

        private static bool NodeAffectsVisibleMesh(JObject[] nodes, JObject[] skins, int? nodeIndex)
        {
            if (nodeIndex == null || nodeIndex < 0 || nodeIndex >= nodes.Length)
            {
                return false;
            }

            var skinJoints = skins
                .SelectMany(skin => skin["joints"]?.Values<int>() ?? Enumerable.Empty<int>())
                .ToHashSet();
            return skinJoints.Contains(nodeIndex.Value) || NodeOrDescendantHasMesh(nodes, nodeIndex.Value);
        }

        private static float MaxOutputDelta(float[][] rows)
        {
            if (rows.Length < 2)
            {
                return 0;
            }

            var first = rows[0];
            var max = 0.0f;
            foreach (var row in rows.Skip(1))
            {
                for (var i = 0; i < Math.Min(first.Length, row.Length); i++)
                {
                    max = Math.Max(max, Math.Abs(row[i] - first[i]));
                }
            }
            return max;
        }

        private static byte[][] LoadBuffers(JObject gltf, string directory)
        {
            return gltf["buffers"]?
                .OfType<JObject>()
                .Select(buffer =>
                {
                    var uri = (string)buffer["uri"];
                    if (string.IsNullOrWhiteSpace(uri) || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        return Array.Empty<byte>();
                    }
                    return File.ReadAllBytes(Path.Combine(directory, uri.Replace('/', Path.DirectorySeparatorChar)));
                })
                .ToArray() ?? Array.Empty<byte[]>();
        }

        private static BoundsReport ComputeBounds(JObject gltf, byte[][] buffers, JObject[] nodes, int meshNodeIndex)
        {
            var meshNode = nodes[meshNodeIndex];
            var meshIndex = (int?)meshNode["mesh"];
            if (meshIndex == null)
            {
                return null;
            }
            var mesh = gltf["meshes"]?[meshIndex.Value] as JObject;
            var primitive = mesh?["primitives"]?.OfType<JObject>().FirstOrDefault(x => x["attributes"]?["POSITION"] != null);
            if (primitive == null)
            {
                return null;
            }
            var positions = ReadAccessor(gltf, buffers, (int)primitive["attributes"]["POSITION"]).Select(x => new Vec3(x[0], x[1], x[2])).ToArray();
            var raw = Bounds.FromPoints(positions);
            Bounds skinned = null;
            var skinIndex = (int?)meshNode["skin"];
            if (skinIndex != null && primitive["attributes"]?["JOINTS_0"] != null && primitive["attributes"]?["WEIGHTS_0"] != null)
            {
                var joints = ReadAccessor(gltf, buffers, (int)primitive["attributes"]["JOINTS_0"]);
                var weights = ReadAccessor(gltf, buffers, (int)primitive["attributes"]["WEIGHTS_0"]);
                var skin = gltf["skins"]?[skinIndex.Value] as JObject;
                var jointNodes = skin?["joints"]?.Select(x => (int)x).ToArray() ?? Array.Empty<int>();
                var inverseBindMatrices = skin?["inverseBindMatrices"] != null
                    ? ReadAccessor(gltf, buffers, (int)skin["inverseBindMatrices"]).Select(Matrix.FromArray).ToArray()
                    : Enumerable.Range(0, jointNodes.Length).Select(_ => Matrix.Identity()).ToArray();
                var worlds = BuildWorldMatrices(nodes);
                var meshWorld = worlds[meshNodeIndex];
                var skinMatrices = jointNodes
                    .Select((node, i) => worlds[node] * inverseBindMatrices[i])
                    .ToArray();
                var skinnedPoints = new Vec3[positions.Length];
                for (var i = 0; i < positions.Length; i++)
                {
                    var p = Vec3.Zero;
                    for (var k = 0; k < Math.Min(4, weights[i].Length); k++)
                    {
                        var weight = weights[i][k];
                        if (weight == 0) continue;
                        var joint = (int)joints[i][k];
                        if (joint < 0 || joint >= skinMatrices.Length) continue;
                        p += skinMatrices[joint].Transform(positions[i]) * weight;
                    }
                    skinnedPoints[i] = meshWorld.Transform(p);
                }
                skinned = Bounds.FromPoints(skinnedPoints);
            }
            var reasonable = skinned == null || raw.maxSize <= 0 || skinned.maxSize / raw.maxSize < 3.0f;
            return new BoundsReport(raw, skinned, reasonable);
        }

        private static Matrix[] BuildWorldMatrices(JObject[] nodes)
        {
            var parents = new int?[nodes.Length];
            for (var i = 0; i < nodes.Length; i++)
            {
                foreach (var child in nodes[i]["children"]?.Select(x => (int)x) ?? Enumerable.Empty<int>())
                {
                    if (child >= 0 && child < parents.Length)
                    {
                        parents[child] = i;
                    }
                }
            }
            var worlds = new Matrix[nodes.Length];
            var done = new bool[nodes.Length];
            Matrix Build(int index)
            {
                if (done[index]) return worlds[index];
                var local = Matrix.FromNode(nodes[index]);
                worlds[index] = parents[index].HasValue ? Build(parents[index].Value) * local : local;
                done[index] = true;
                return worlds[index];
            }
            for (var i = 0; i < nodes.Length; i++) Build(i);
            return worlds;
        }

        private static float[][] ReadAccessor(JObject gltf, byte[][] buffers, int accessorIndex)
        {
            var accessor = (JObject)gltf["accessors"][accessorIndex];
            var view = (JObject)gltf["bufferViews"][(int)accessor["bufferView"]];
            var buffer = buffers[(int)view["buffer"]];
            var offset = ((int?)view["byteOffset"] ?? 0) + ((int?)accessor["byteOffset"] ?? 0);
            var count = (int)accessor["count"];
            var components = ((string)accessor["type"]) switch
            {
                "SCALAR" => 1,
                "VEC2" => 2,
                "VEC3" => 3,
                "VEC4" => 4,
                "MAT4" => 16,
                _ => 1,
            };
            var componentType = (int)accessor["componentType"];
            var bytes = componentType switch
            {
                5126 => 4,
                5125 => 4,
                5123 => 2,
                5121 => 1,
                _ => 4,
            };
            var stride = (int?)view["byteStride"] ?? components * bytes;
            var rows = new float[count][];
            for (var i = 0; i < count; i++)
            {
                rows[i] = new float[components];
                for (var c = 0; c < components; c++)
                {
                    var at = offset + i * stride + c * bytes;
                    rows[i][c] = componentType switch
                    {
                        5126 => BitConverter.ToSingle(buffer, at),
                        5125 => BitConverter.ToUInt32(buffer, at),
                        5123 => BitConverter.ToUInt16(buffer, at),
                        5121 => buffer[at],
                        _ => 0,
                    };
                }
            }
            return rows;
        }

        private sealed record BoundsReport(Bounds raw, Bounds skinnedFinal, bool skinnedSizeLooksReasonable);

        private sealed record AnimationCoverage(
            int coreBoneNodeCount,
            int coreBoneChannelCount,
            int twistOrHelperNodeCount,
            int accessoryPointNodeCount,
            string[] coreBoneNodes,
            string[] twistOrHelperNodes,
            string[] accessoryPointNodes
        );

        private sealed record MorphReport(
            bool expected,
            int expectedBindingCount,
            int meshTargetCount,
            int targetCount,
            int weightChannelCount,
            string[] targetNames
        );

        private sealed record HumanoidAnimationReport(
            bool present,
            bool requiresBake,
            bool baked,
            string bakeMode,
            int bakedTrackCount,
            int bakedKeyframeCount,
            int muscleCurveCount,
            int keyframeCount,
            string[] attributes,
            JObject diagnostics
        );

        private sealed record AnimationMotionReport(
            float maxDuration,
            float maxOutputDelta,
            int movingSamplerCount,
            int movingChannelCount,
            int visibleMovingChannelCount,
            string[] visibleMovingTargets
        );

        private sealed record Bounds(float[] min, float[] max, float[] size)
        {
            public float maxSize => size.Length == 0 ? 0 : size.Max();

            public static Bounds FromPoints(IEnumerable<Vec3> points)
            {
                var min = new[] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity };
                var max = new[] { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };
                foreach (var p in points)
                {
                    min[0] = Math.Min(min[0], p.X); min[1] = Math.Min(min[1], p.Y); min[2] = Math.Min(min[2], p.Z);
                    max[0] = Math.Max(max[0], p.X); max[1] = Math.Max(max[1], p.Y); max[2] = Math.Max(max[2], p.Z);
                }
                return new Bounds(Round(min), Round(max), Round(new[] { max[0] - min[0], max[1] - min[1], max[2] - min[2] }));
            }

            private static float[] Round(float[] values)
            {
                return values.Select(x => float.IsInfinity(x) ? x : float.Parse(x.ToString("0.###", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)).ToArray();
            }
        }

        private readonly struct Vec3
        {
            public static readonly Vec3 Zero = new Vec3(0, 0, 0);
            public readonly float X;
            public readonly float Y;
            public readonly float Z;
            public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }
            public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
            public static Vec3 operator *(Vec3 a, float s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        }

        private readonly struct Matrix
        {
            private readonly float[] m;
            private Matrix(float[] values) { m = values; }
            public static Matrix Identity() => new Matrix(new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 });
            public static Matrix FromArray(float[] values) => new Matrix(values.Take(16).ToArray());
            public static Matrix FromNode(JObject node)
            {
                if (node["matrix"] is JArray matrix)
                {
                    return FromArray(matrix.Select(x => (float)x).ToArray());
                }
                var t = node["translation"]?.Select(x => (float)x).ToArray() ?? new[] { 0f, 0f, 0f };
                var s = node["scale"]?.Select(x => (float)x).ToArray() ?? new[] { 1f, 1f, 1f };
                var q = node["rotation"]?.Select(x => (float)x).ToArray() ?? new[] { 0f, 0f, 0f, 1f };
                var x = q[0]; var y = q[1]; var z = q[2]; var w = q[3];
                var x2 = x + x; var y2 = y + y; var z2 = z + z;
                var xx = x * x2; var xy = x * y2; var xz = x * z2;
                var yy = y * y2; var yz = y * z2; var zz = z * z2;
                var wx = w * x2; var wy = w * y2; var wz = w * z2;
                return new Matrix(new[]
                {
                    (1 - (yy + zz)) * s[0], (xy + wz) * s[0], (xz - wy) * s[0], 0,
                    (xy - wz) * s[1], (1 - (xx + zz)) * s[1], (yz + wx) * s[1], 0,
                    (xz + wy) * s[2], (yz - wx) * s[2], (1 - (xx + yy)) * s[2], 0,
                    t[0], t[1], t[2], 1,
                });
            }
            public Vec3 Transform(Vec3 v) => new Vec3(
                m[0] * v.X + m[4] * v.Y + m[8] * v.Z + m[12],
                m[1] * v.X + m[5] * v.Y + m[9] * v.Z + m[13],
                m[2] * v.X + m[6] * v.Y + m[10] * v.Z + m[14]
            );
            public static Matrix operator *(Matrix a, Matrix b)
            {
                var o = new float[16];
                for (var c = 0; c < 4; c++)
                for (var r = 0; r < 4; r++)
                for (var k = 0; k < 4; k++)
                {
                    o[c * 4 + r] += a.m[k * 4 + r] * b.m[c * 4 + k];
                }
                return new Matrix(o);
            }
        }
    }
}
