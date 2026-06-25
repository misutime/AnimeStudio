using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AnimeStudio.CLI
{
    internal static class EndfieldCabLocator
    {
        public static string BuildLocationIndex(string inputPath, string outputDirectory, Game game, Regex[] innerFileFilters, int innerFileLimit)
        {
            if (game == null || !game.Type.IsArknightsEndfieldGroup())
            {
                Logger.Error("--build_endfield_cab_location_index only supports Arknights Endfield.");
                return null;
            }
            if (string.IsNullOrWhiteSpace(inputPath) || (!File.Exists(inputPath) && !Directory.Exists(inputPath)))
            {
                Logger.Error($"Input path not found: {inputPath}");
                return null;
            }

            var outputRoot = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(Environment.CurrentDirectory, "EndfieldCabLocationIndex")
                : Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(outputRoot);

            var blockFiles = EnumerateBlockFiles(inputPath)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var filter = BuildInnerFileFilter(innerFileFilters);
            var locations = new JArray();
            var blockReports = new JArray();
            var errors = new JArray();
            var scannedUnityBundleCount = 0;
            var skippedUnityBundleCount = 0;

            foreach (var blockFile in blockFiles)
            {
                var result = EndfieldVfsBlockFile.ListCabFiles(
                    blockFile,
                    game,
                    filter,
                    Math.Max(0, innerFileLimit));
                scannedUnityBundleCount += result.ScannedUnityBundleCount;
                skippedUnityBundleCount += result.SkippedUnityBundleCount;
                foreach (var location in result.Locations)
                {
                    locations.Add(ToJson(inputPath, location));
                }
                foreach (var error in result.Errors.Take(16))
                {
                    errors.Add(new JObject
                    {
                        ["blockList"] = MakeRelativeOrSelf(inputPath, blockFile),
                        ["chunkFile"] = error.ChunkFileName,
                        ["unityBundleFile"] = error.UnityBundleFile,
                        ["message"] = error.Message,
                    });
                }

                blockReports.Add(new JObject
                {
                    ["blockList"] = MakeRelativeOrSelf(inputPath, blockFile),
                    ["scannedUnityBundleCount"] = result.ScannedUnityBundleCount,
                    ["skippedUnityBundleCount"] = result.SkippedUnityBundleCount,
                    ["locationCount"] = result.Locations.Count,
                    ["errorCount"] = result.Errors.Count,
                });
            }

            var cabCount = locations
                .OfType<JObject>()
                .Select(x => (string)x["cabName"])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var report = new JObject
            {
                ["schemaVersion"] = 1,
                ["kind"] = "endfield_cab_location_index",
                ["inputPath"] = Path.GetFullPath(inputPath),
                ["outputRoot"] = outputRoot,
                ["game"] = game.Name,
                ["cabCount"] = cabCount,
                ["locationCount"] = locations.Count,
                ["blockFileCount"] = blockFiles.Length,
                ["scannedUnityBundleCount"] = scannedUnityBundleCount,
                ["skippedUnityBundleCount"] = skippedUnityBundleCount,
                ["innerFileLimit"] = Math.Max(0, innerFileLimit),
                ["innerFileFilterCount"] = innerFileFilters?.Length ?? 0,
                ["locations"] = locations,
                ["blocks"] = blockReports,
                ["errors"] = errors,
                ["rule"] = "只读建立 Endfield CAB -> VFS inner bundle 位置索引，用于后续模型第一阶段材质/贴图依赖闭包查询；不解析 Unity 对象，不创建模型-动画关系。",
            };

            var reportPath = Path.Combine(outputRoot, "endfield_cab_location_index.json");
            File.WriteAllText(reportPath, report.ToString(Formatting.Indented));
            Logger.Info($"Endfield CAB location index: {reportPath}");
            Logger.Info($"Indexed {cabCount} CAB(s), locations={locations.Count}, scanned Unity bundle files={scannedUnityBundleCount}.");
            return reportPath;
        }

        public static string Locate(string inputPath, string outputDirectory, Game game, string[] cabNames, Regex[] innerFileFilters, int innerFileLimit)
        {
            if (game == null || !game.Type.IsArknightsEndfieldGroup())
            {
                Logger.Error("--locate_endfield_cabs only supports Arknights Endfield.");
                return null;
            }
            if (string.IsNullOrWhiteSpace(inputPath) || (!File.Exists(inputPath) && !Directory.Exists(inputPath)))
            {
                Logger.Error($"Input path not found: {inputPath}");
                return null;
            }

            var targets = NormalizeCabNames(cabNames);
            if (targets.Count == 0)
            {
                Logger.Error("--locate_endfield_cabs requires at least one CAB name.");
                return null;
            }

            var outputRoot = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(Environment.CurrentDirectory, "EndfieldCabLocator")
                : Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(outputRoot);

            var blockFiles = EnumerateBlockFiles(inputPath)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var filter = BuildInnerFileFilter(innerFileFilters);
            var blockReports = new JArray();
            var locations = new JArray();
            var errors = new JArray();
            var scannedUnityBundleCount = 0;
            var skippedUnityBundleCount = 0;

            foreach (var blockFile in blockFiles)
            {
                var result = EndfieldVfsBlockFile.LocateCabFiles(
                    blockFile,
                    game,
                    targets,
                    filter,
                    Math.Max(0, innerFileLimit));
                scannedUnityBundleCount += result.ScannedUnityBundleCount;
                skippedUnityBundleCount += result.SkippedUnityBundleCount;
                foreach (var location in result.Locations)
                {
                    locations.Add(ToJson(inputPath, location));
                }
                foreach (var error in result.Errors.Take(16))
                {
                    errors.Add(new JObject
                    {
                        ["blockList"] = MakeRelativeOrSelf(inputPath, blockFile),
                        ["chunkFile"] = error.ChunkFileName,
                        ["unityBundleFile"] = error.UnityBundleFile,
                        ["message"] = error.Message,
                    });
                }

                blockReports.Add(new JObject
                {
                    ["blockList"] = MakeRelativeOrSelf(inputPath, blockFile),
                    ["scannedUnityBundleCount"] = result.ScannedUnityBundleCount,
                    ["skippedUnityBundleCount"] = result.SkippedUnityBundleCount,
                    ["locationCount"] = result.Locations.Count,
                    ["errorCount"] = result.Errors.Count,
                });
            }

            var foundTargets = locations
                .OfType<JObject>()
                .Select(x => (string)x["cabName"])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missingTargets = targets
                .Where(x => !foundTargets.Contains(x))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var report = new JObject
            {
                ["schemaVersion"] = 1,
                ["inputPath"] = Path.GetFullPath(inputPath),
                ["outputRoot"] = outputRoot,
                ["game"] = game.Name,
                ["targetCabCount"] = targets.Count,
                ["targets"] = new JArray(targets.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                ["foundTargetCount"] = foundTargets.Count,
                ["missingTargets"] = new JArray(missingTargets),
                ["blockFileCount"] = blockFiles.Length,
                ["scannedUnityBundleCount"] = scannedUnityBundleCount,
                ["skippedUnityBundleCount"] = skippedUnityBundleCount,
                ["innerFileLimit"] = Math.Max(0, innerFileLimit),
                ["innerFileFilterCount"] = innerFileFilters?.Length ?? 0,
                ["locations"] = locations,
                ["blocks"] = blockReports,
                ["errors"] = errors,
                ["rule"] = "只读扫描 Endfield VFS 内部 UnityFS bundle 目录来定位 CAB 物理位置；不解析 Unity 对象，也不创建模型-动画关系。",
            };

            var reportPath = Path.Combine(outputRoot, "endfield_cab_locations.json");
            File.WriteAllText(reportPath, report.ToString(Formatting.Indented));
            Logger.Info($"Endfield CAB location report: {reportPath}");
            Logger.Info($"Located {foundTargets.Count}/{targets.Count} CAB target(s), locations={locations.Count}, scanned Unity bundle files={scannedUnityBundleCount}.");
            if (missingTargets.Length > 0)
            {
                Logger.Warning($"Missing CAB target(s): {string.Join(", ", missingTargets.Take(8))}{(missingTargets.Length > 8 ? " ..." : string.Empty)}");
            }

            return reportPath;
        }

        public static string LocateMissingSourceCabs(
            string inputPath,
            string outputDirectory,
            Game game,
            string sourceIndexPath,
            Regex[] innerFileFilters,
            int innerFileLimit,
            string cabLocationIndexPath,
            int cabLimit)
        {
            if (game == null || !game.Type.IsArknightsEndfieldGroup())
            {
                Logger.Error("--locate_endfield_missing_source_cabs only supports Arknights Endfield.");
                return null;
            }
            if (string.IsNullOrWhiteSpace(inputPath) || (!File.Exists(inputPath) && !Directory.Exists(inputPath)))
            {
                Logger.Error($"Input path not found: {inputPath}");
                return null;
            }
            if (string.IsNullOrWhiteSpace(sourceIndexPath) || !File.Exists(sourceIndexPath))
            {
                Logger.Error($"Unity source index not found: {sourceIndexPath}");
                return null;
            }

            cabLimit = cabLimit <= 0 ? 200 : cabLimit;
            var outputRoot = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(Environment.CurrentDirectory, "EndfieldMissingSourceCabClosure")
                : Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(outputRoot);

            var missingRows = LoadMissingSourceCabRows(sourceIndexPath);
            var selectedTargets = missingRows
                .GroupBy(x => x.CabName, StringComparer.OrdinalIgnoreCase)
                .Select(group => new MissingCabTarget(
                    group.Key,
                    group.Sum(x => x.RelationCount),
                    group.Sum(x => x.DistinctReferrerCount),
                    group.Select(x => x.Relation).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
                    group.OrderByDescending(x => x.RelationCount).First()))
                .OrderByDescending(x => x.RelationCount)
                .ThenByDescending(x => x.DistinctReferrerCount)
                .ThenBy(x => x.CabName, StringComparer.OrdinalIgnoreCase)
                .Take(cabLimit)
                .ToArray();

            if (selectedTargets.Length == 0)
            {
                Logger.Warning("No missing source relation target CABs were found in the source index.");
            }

            var targetSet = selectedTargets.Select(x => x.CabName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var blockReports = new JArray();
            var locations = new JArray();
            var errors = new JArray();
            var scannedUnityBundleCount = 0;
            var skippedUnityBundleCount = 0;
            var usedCabLocationIndex = !string.IsNullOrWhiteSpace(cabLocationIndexPath);
            var blockFileCount = 0;
            if (usedCabLocationIndex)
            {
                IReadOnlyList<JObject> indexedLocations;
                try
                {
                    indexedLocations = LoadLocationsFromIndex(cabLocationIndexPath, targetSet);
                }
                catch (Exception e) when (e is IOException || e is InvalidDataException || e is JsonException || e is ArgumentException)
                {
                    Logger.Error($"Failed to read Endfield CAB location index: {e.Message}");
                    return null;
                }

                foreach (var location in indexedLocations)
                {
                    locations.Add(location);
                }
            }
            else
            {
                var blockFiles = EnumerateBlockFiles(inputPath)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                blockFileCount = blockFiles.Length;
                var filter = BuildInnerFileFilter(innerFileFilters);

                foreach (var blockFile in blockFiles)
                {
                    var result = EndfieldVfsBlockFile.LocateCabFiles(
                        blockFile,
                        game,
                        targetSet,
                        filter,
                        Math.Max(0, innerFileLimit));
                    scannedUnityBundleCount += result.ScannedUnityBundleCount;
                    skippedUnityBundleCount += result.SkippedUnityBundleCount;
                    foreach (var location in result.Locations)
                    {
                        locations.Add(ToJson(inputPath, location));
                    }
                    foreach (var error in result.Errors.Take(16))
                    {
                        errors.Add(new JObject
                        {
                            ["blockList"] = MakeRelativeOrSelf(inputPath, blockFile),
                            ["chunkFile"] = error.ChunkFileName,
                            ["unityBundleFile"] = error.UnityBundleFile,
                            ["message"] = error.Message,
                        });
                    }

                    blockReports.Add(new JObject
                    {
                        ["blockList"] = MakeRelativeOrSelf(inputPath, blockFile),
                        ["scannedUnityBundleCount"] = result.ScannedUnityBundleCount,
                        ["skippedUnityBundleCount"] = result.SkippedUnityBundleCount,
                        ["locationCount"] = result.Locations.Count,
                        ["errorCount"] = result.Errors.Count,
                    });
                }
            }

            var foundTargets = locations
                .OfType<JObject>()
                .Select(x => (string)x["cabName"])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missingTargets = selectedTargets
                .Where(x => !foundTargets.Contains(x.CabName))
                .Select(x => x.CabName)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var locatedUnityBundleFiles = locations
                .OfType<JObject>()
                .Select(x => (string)x["unityBundleFile"])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var endfieldVfsFilesRegex = BuildEndfieldVfsFilesRegex(locatedUnityBundleFiles);
            var relationBuckets = BuildRelationBuckets(selectedTargets, foundTargets);
            var dependencyDomainBuckets = BuildDependencyDomainBuckets(selectedTargets, foundTargets);

            var report = new JObject
            {
                ["schemaVersion"] = 1,
                ["inputPath"] = Path.GetFullPath(inputPath),
                ["outputRoot"] = outputRoot,
                ["game"] = game.Name,
                ["sourceIndex"] = Path.GetFullPath(sourceIndexPath),
                ["sourceMissingCabCount"] = missingRows.Select(x => x.CabName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                ["selectedCabLimit"] = cabLimit,
                ["selectedCabCount"] = selectedTargets.Length,
                ["selectedTargets"] = new JArray(selectedTargets.Select(ToJson)),
                ["relationBuckets"] = relationBuckets,
                ["dependencyDomainBuckets"] = dependencyDomainBuckets,
                ["cabLocationIndex"] = usedCabLocationIndex ? Path.GetFullPath(cabLocationIndexPath) : string.Empty,
                ["usedCabLocationIndex"] = usedCabLocationIndex,
                ["foundTargetCount"] = foundTargets.Count,
                ["missingTargets"] = new JArray(missingTargets),
                ["locatedUnityBundleFileCount"] = locatedUnityBundleFiles.Length,
                ["locatedUnityBundleFiles"] = new JArray(locatedUnityBundleFiles),
                ["endfieldVfsFilesRegexForLocatedMissingBundles"] = endfieldVfsFilesRegex,
                ["diagnosticRebuildHint"] = string.IsNullOrWhiteSpace(endfieldVfsFilesRegex)
                    ? "未定位到缺失 CAB 所在 inner bundle。请检查源索引缺失样本或扩大扫描范围。"
                    : "做小范围源索引复现时，把原始 root bundle 正则和本字段正则一起传给 --endfield_vfs_files；它只补缺失依赖包，不替代 root bundle 选择。",
                ["blockFileCount"] = blockFileCount,
                ["scannedUnityBundleCount"] = scannedUnityBundleCount,
                ["skippedUnityBundleCount"] = skippedUnityBundleCount,
                ["innerFileLimit"] = Math.Max(0, innerFileLimit),
                ["innerFileFilterCount"] = innerFileFilters?.Length ?? 0,
                ["locations"] = locations,
                ["blocks"] = blockReports,
                ["errors"] = errors,
                ["rule"] = "源关系闭包诊断：只从现有源索引缺失的 Mesh/Renderer/Material/Texture/AnimationClip 等确定性 PPtr 关系里提取 CAB，再只读定位物理 inner bundle。它不创建材质或动画关系、不导出模型，也不能把缺 Mesh/缺材质模型或缺动画上下文样本升级为 smoke。",
            };

            var reportPath = Path.Combine(outputRoot, "endfield_missing_source_cab_closure.json");
            File.WriteAllText(reportPath, report.ToString(Formatting.Indented));
            Logger.Info($"Endfield missing source CAB closure report: {reportPath}");
            Logger.Info($"Located {foundTargets.Count}/{selectedTargets.Length} missing source CAB target(s), bundleFiles={locatedUnityBundleFiles.Length}, scanned Unity bundle files={scannedUnityBundleCount}{(usedCabLocationIndex ? " (using CAB location index)" : string.Empty)}.");
            if (!string.IsNullOrWhiteSpace(endfieldVfsFilesRegex))
            {
                Logger.Info($"Suggested --endfield_vfs_files regex for located missing bundles: {endfieldVfsFilesRegex}");
            }
            if (missingTargets.Length > 0)
            {
                Logger.Warning($"Still missing CAB target(s): {string.Join(", ", missingTargets.Take(8))}{(missingTargets.Length > 8 ? " ..." : string.Empty)}");
            }

            return reportPath;
        }

        private static JArray BuildRelationBuckets(IEnumerable<MissingCabTarget> targets, ISet<string> foundTargets)
        {
            var buckets = (targets ?? Array.Empty<MissingCabTarget>())
                .SelectMany(target => (target.Relations ?? Array.Empty<string>())
                    .Where(relation => !string.IsNullOrWhiteSpace(relation))
                    .Select(relation => new { Relation = relation, Target = target }))
                .GroupBy(x => x.Relation, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase);

            var result = new JArray();
            foreach (var bucket in buckets)
            {
                var bucketTargets = bucket.Select(x => x.Target).ToArray();
                var foundCabCount = bucketTargets.Count(x => foundTargets != null && foundTargets.Contains(x.CabName));
                result.Add(new JObject
                {
                    ["relation"] = bucket.Key,
                    ["selectedCabCount"] = bucketTargets.Length,
                    ["foundCabCount"] = foundCabCount,
                    ["missingCabCount"] = bucketTargets.Length - foundCabCount,
                    ["relationCount"] = bucketTargets.Sum(x => x.RelationCount),
                    ["distinctReferrerCount"] = bucketTargets.Sum(x => x.DistinctReferrerCount),
                    ["sampleCab"] = bucketTargets.OrderByDescending(x => x.RelationCount).FirstOrDefault()?.CabName ?? string.Empty,
                });
            }

            return result;
        }

        private static JArray BuildDependencyDomainBuckets(IEnumerable<MissingCabTarget> targets, ISet<string> foundTargets)
        {
            var domainRules = new[]
            {
                new
                {
                    Domain = "model",
                    Relations = new HashSet<string>(new[] { "meshFilter.mesh", "skinnedMeshRenderer.mesh", "animator.avatar" }, StringComparer.OrdinalIgnoreCase),
                    Note = "模型第一阶段闭包：MeshFilter/SkinnedMeshRenderer 指向的 Mesh 或 Animator 指向的 Avatar 目标缺失时，不能进入动画 smoke。",
                },
                new
                {
                    Domain = "material",
                    Relations = new HashSet<string>(new[] { "renderer.material", "material.texture" }, StringComparer.OrdinalIgnoreCase),
                    Note = "模型第一阶段闭包：Renderer 材质或 Material 贴图目标缺失时，不能把样本当作材质完整。",
                },
                new
                {
                    Domain = "animationClip",
                    Relations = new HashSet<string>(new[] { "animatorController.clip", "animation.clip", "animatorOverrideController.originalClip", "animatorOverrideController.overrideClip" }, StringComparer.OrdinalIgnoreCase),
                    Note = "动画第二阶段闭包：Controller/Animation/OverrideController 的 AnimationClip 目标缺失时，不能进入动画候选验收。",
                },
            };

            var targetArray = (targets ?? Array.Empty<MissingCabTarget>()).ToArray();
            var result = new JArray();
            foreach (var rule in domainRules)
            {
                var domainTargets = targetArray
                    .Where(target => (target.Relations ?? Array.Empty<string>()).Any(rule.Relations.Contains))
                    .ToArray();
                var foundCabCount = domainTargets.Count(x => foundTargets != null && foundTargets.Contains(x.CabName));
                result.Add(new JObject
                {
                    ["domain"] = rule.Domain,
                    ["selectedCabCount"] = domainTargets.Length,
                    ["foundCabCount"] = foundCabCount,
                    ["missingCabCount"] = domainTargets.Length - foundCabCount,
                    ["relationCount"] = domainTargets.Sum(x => x.RelationCount),
                    ["distinctReferrerCount"] = domainTargets.Sum(x => x.DistinctReferrerCount),
                    ["relations"] = new JArray(rule.Relations.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                    ["note"] = rule.Note,
                });
            }

            return result;
        }

        public static SourceCabClosureFilter BuildSourceCabClosureInnerFileFilter(string reportPath, IEnumerable<string> domains = null)
        {
            if (string.IsNullOrWhiteSpace(reportPath) || !File.Exists(reportPath))
            {
                throw new FileNotFoundException($"Endfield missing source CAB closure report not found: {reportPath}", reportPath);
            }

            var report = JObject.Parse(File.ReadAllText(reportPath));
            var allowedCabs = BuildClosureCabFilter(report, domains);
            var bundleFiles = ReadClosureBundleFiles(report, allowedCabs)
                .Select(NormalizeClosureBundlePath)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var exact = new HashSet<string>(bundleFiles, StringComparer.OrdinalIgnoreCase);
            var withoutDataPrefix = bundleFiles
                .Where(x => x.StartsWith("Data/Bundles/Windows/", StringComparison.OrdinalIgnoreCase))
                .Select(x => x["Data/Bundles/Windows/".Length..])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            bool Filter(string innerFile)
            {
                var normalized = NormalizeClosureBundlePath(innerFile);
                return exact.Contains(normalized)
                    || withoutDataPrefix.Contains(normalized);
            }

            return new SourceCabClosureFilter(
                Filter,
                Path.GetFullPath(reportPath),
                bundleFiles,
                allowedCabs == null ? (int?)report["selectedCabCount"] ?? 0 : allowedCabs.Count,
                allowedCabs == null ? (int?)report["foundTargetCount"] ?? 0 : CountFoundClosureCabs(report, allowedCabs),
                (int?)report["sourceMissingCabCount"] ?? 0);
        }

        private static HashSet<string> BuildClosureCabFilter(JObject report, IEnumerable<string> domains)
        {
            var normalizedDomains = (domains ?? Array.Empty<string>())
                .SelectMany(x => (x ?? string.Empty).Split(new[] { ',', ';', '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (normalizedDomains.Length == 0)
            {
                return null;
            }

            var allowedRelations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var domain in normalizedDomains)
            {
                foreach (var relation in GetClosureDomainRelations(domain))
                {
                    allowedRelations.Add(relation);
                }
            }

            var allowedCabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in report?["selectedTargets"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var cabName = (string)target["cabName"];
                if (string.IsNullOrWhiteSpace(cabName))
                {
                    continue;
                }

                var relations = target["relations"]?.Values<string>() ?? Enumerable.Empty<string>();
                if (relations.Any(allowedRelations.Contains))
                {
                    allowedCabs.Add(cabName);
                }
            }

            return allowedCabs;
        }

        private static IEnumerable<string> GetClosureDomainRelations(string domain)
        {
            if (string.Equals(domain, "model", StringComparison.OrdinalIgnoreCase))
            {
                return new[] { "meshFilter.mesh", "skinnedMeshRenderer.mesh", "animator.avatar" };
            }
            if (string.Equals(domain, "material", StringComparison.OrdinalIgnoreCase))
            {
                return new[] { "renderer.material", "material.texture" };
            }
            if (string.Equals(domain, "animationClip", StringComparison.OrdinalIgnoreCase)
                || string.Equals(domain, "animation", StringComparison.OrdinalIgnoreCase))
            {
                return new[] { "animatorController.clip", "animation.clip", "animatorOverrideController.originalClip", "animatorOverrideController.overrideClip" };
            }

            throw new ArgumentException($"Unknown Endfield source CAB closure domain '{domain}'. Use model, material, or animationClip.");
        }

        private static int CountFoundClosureCabs(JObject report, ISet<string> allowedCabs)
        {
            if (allowedCabs == null)
            {
                return (int?)report?["foundTargetCount"] ?? 0;
            }

            return report?["locations"]?
                .OfType<JObject>()
                .Select(x => (string)x["cabName"])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(allowedCabs.Contains) ?? 0;
        }

        public static string LocateStrings(string inputPath, string outputDirectory, Game game, string[] searchStrings, Regex[] innerFileFilters)
        {
            if (game == null || !game.Type.IsArknightsEndfieldGroup())
            {
                Logger.Error("--locate_endfield_strings only supports Arknights Endfield.");
                return null;
            }
            if (string.IsNullOrWhiteSpace(inputPath) || (!File.Exists(inputPath) && !Directory.Exists(inputPath)))
            {
                Logger.Error($"Input path not found: {inputPath}");
                return null;
            }

            var targets = NormalizeSearchStrings(searchStrings);
            if (targets.Count == 0)
            {
                Logger.Error("--locate_endfield_strings requires at least one non-empty ASCII string.");
                return null;
            }

            var outputRoot = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(Environment.CurrentDirectory, "EndfieldStringLocator")
                : Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(outputRoot);

            var filter = BuildInnerFileFilter(innerFileFilters);
            var blockFiles = EnumerateBlockFiles(inputPath)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var blockReports = new JArray();
            var locations = new JArray();
            var errors = new JArray();
            var scannedChunkCount = 0;

            foreach (var blockFile in blockFiles)
            {
                IReadOnlyList<EndfieldVfsBlockFile.VfsFileEntry> entries;
                try
                {
                    entries = EndfieldVfsBlockFile.ListFileEntries(blockFile, game.Type);
                }
                catch (Exception e) when (e is IOException || e is InvalidDataException || e is EndOfStreamException || e is ArgumentOutOfRangeException || e is NotSupportedException)
                {
                    errors.Add(new JObject
                    {
                        ["blockList"] = MakeRelativeOrSelf(inputPath, blockFile),
                        ["message"] = e.GetType().Name + ": " + e.Message,
                    });
                    continue;
                }

                var blockDir = Path.GetDirectoryName(blockFile) ?? string.Empty;
                var hitCount = 0;
                var chunkGroups = entries
                    .GroupBy(x => x.ChunkFileName, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase);
                foreach (var group in chunkGroups)
                {
                    var chunkPath = Path.Combine(blockDir, group.Key);
                    if (!File.Exists(chunkPath))
                    {
                        continue;
                    }

                    scannedChunkCount++;
                    byte[] chunkBytes;
                    try
                    {
                        // 这是诊断入口：一次读一个 chunk，换取最直白的 offset 对照。
                        chunkBytes = File.ReadAllBytes(chunkPath);
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        errors.Add(new JObject
                        {
                            ["blockList"] = MakeRelativeOrSelf(inputPath, blockFile),
                            ["chunkFile"] = group.Key,
                            ["message"] = e.GetType().Name + ": " + e.Message,
                        });
                        continue;
                    }

                    var groupEntries = group.OrderBy(x => x.Offset).ToArray();
                    foreach (var target in targets)
                    {
                        foreach (var offset in FindAsciiOffsets(chunkBytes, target))
                        {
                            var owner = FindOwner(groupEntries, offset, filter);
                            if (owner == null)
                            {
                                continue;
                            }

                            hitCount++;
                            locations.Add(new JObject
                            {
                                ["searchString"] = target.Text,
                                ["blockList"] = MakeRelativeOrSelf(inputPath, blockFile),
                                ["chunkFile"] = group.Key,
                                ["chunkOffset"] = offset,
                                ["innerFile"] = owner.FileName,
                                ["innerFileOffset"] = offset - owner.Offset,
                                ["innerFileLength"] = owner.Length,
                                ["innerBlockType"] = owner.BlockType,
                                ["innerBlockTypeId"] = owner.BlockTypeId,
                                ["innerUseEncrypt"] = owner.UseEncrypt,
                            });
                        }
                    }
                }

                blockReports.Add(new JObject
                {
                    ["blockList"] = MakeRelativeOrSelf(inputPath, blockFile),
                    ["vfsFileCount"] = entries.Count,
                    ["hitCount"] = hitCount,
                });
            }

            var foundTargets = locations
                .OfType<JObject>()
                .Select(x => (string)x["searchString"])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.Ordinal);
            var missingTargets = targets
                .Select(x => x.Text)
                .Where(x => !foundTargets.Contains(x))
                .ToArray();

            var report = new JObject
            {
                ["schemaVersion"] = 1,
                ["inputPath"] = Path.GetFullPath(inputPath),
                ["outputRoot"] = outputRoot,
                ["game"] = game.Name,
                ["targetCount"] = targets.Count,
                ["targets"] = new JArray(targets.Select(x => x.Text)),
                ["foundTargetCount"] = foundTargets.Count,
                ["missingTargets"] = new JArray(missingTargets),
                ["blockFileCount"] = blockFiles.Length,
                ["scannedChunkCount"] = scannedChunkCount,
                ["innerFileFilterCount"] = innerFileFilters?.Length ?? 0,
                ["locations"] = locations,
                ["targetSummaries"] = BuildStringTargetSummaries(locations),
                ["innerFileSummaries"] = BuildStringInnerFileSummaries(locations),
                ["blocks"] = blockReports,
                ["errors"] = errors,
                ["rule"] = "只读扫描 Endfield VFS chunk 明文字节，并用 VFS metadata 把命中 offset 映射回 inner 文件；用于定位缺失 CAB/manifest 关系，不参与默认导出绑定。",
            };

            var reportPath = Path.Combine(outputRoot, "endfield_string_locations.json");
            File.WriteAllText(reportPath, report.ToString(Formatting.Indented));
            Logger.Info($"Endfield string location report: {reportPath}");
            Logger.Info($"Located {foundTargets.Count}/{targets.Count} string target(s), hits={locations.Count}, scanned chunks={scannedChunkCount}.");
            if (missingTargets.Length > 0)
            {
                Logger.Warning($"Missing string target(s): {string.Join(", ", missingTargets.Take(8))}{(missingTargets.Length > 8 ? " ..." : string.Empty)}");
            }

            return reportPath;
        }

        private static IReadOnlyList<MissingCabRow> LoadMissingSourceCabRows(string sourceIndexPath)
        {
            SQLitePCL.Batteries_V2.Init();
            var rows = new List<MissingCabRow>();
            using var connection = new SqliteConnection($"Data Source={Path.GetFullPath(sourceIndexPath)};Mode=ReadOnly");
            connection.Open();
            rows.AddRange(LoadMissingSourceCabRows(connection, "meshFilter.mesh", "Mesh"));
            rows.AddRange(LoadMissingSourceCabRows(connection, "skinnedMeshRenderer.mesh", "Mesh"));
            rows.AddRange(LoadMissingSourceCabRows(connection, "animator.avatar", "Avatar"));
            rows.AddRange(LoadMissingSourceCabRows(connection, "renderer.material", "Material"));
            rows.AddRange(LoadMissingSourceCabRows(connection, "material.texture", "UnityTexture"));
            rows.AddRange(LoadMissingSourceCabRows(connection, "animatorController.clip", "AnimationClip"));
            rows.AddRange(LoadMissingSourceCabRows(connection, "animation.clip", "AnimationClip"));
            rows.AddRange(LoadMissingSourceCabRows(connection, "animatorOverrideController.originalClip", "AnimationClip"));
            rows.AddRange(LoadMissingSourceCabRows(connection, "animatorOverrideController.overrideClip", "AnimationClip"));
            return rows;
        }

        private static IReadOnlyList<JObject> LoadLocationsFromIndex(string indexPath, ISet<string> targetCabNames)
        {
            if (string.IsNullOrWhiteSpace(indexPath) || !File.Exists(indexPath))
            {
                throw new FileNotFoundException($"Endfield CAB location index not found: {indexPath}", indexPath);
            }

            var report = JObject.Parse(File.ReadAllText(indexPath));
            var locations = new List<JObject>();
            foreach (var location in report["locations"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var cabName = (string)location["cabName"];
                if (string.IsNullOrWhiteSpace(cabName)
                    || targetCabNames == null
                    || !targetCabNames.Contains(cabName))
                {
                    continue;
                }

                locations.Add(new JObject(location));
            }

            return locations;
        }

        private static IReadOnlyList<MissingCabRow> LoadMissingSourceCabRows(SqliteConnection connection, string relation, string targetType)
        {
            var targetTypeFilter = targetType == "UnityTexture"
                ? UnityTextureTypeSql("target")
                : targetType == "Texture2D"
                    ? "target.type IN ('Texture2D', 'Texture2DArray')"
                    : "target.type = $targetType";
            using var command = connection.CreateCommand();
            command.CommandText = $@"
SELECT
    r.to_file,
    r.to_path_id,
    r.to_type_hint,
    COUNT(*) AS relation_count,
    COUNT(DISTINCT r.from_file || ':' || r.from_path_id) AS distinct_referrer_count,
    MIN(r.from_source) AS sample_from_source,
    MIN(r.from_file) AS sample_from_file,
    MIN(r.from_type) AS sample_from_type,
    MIN(r.from_name) AS sample_from_name,
    MIN(r.from_path_id) AS sample_from_path_id,
    MIN(e.file_name) AS sample_external_file_name,
    MIN(e.path_name) AS sample_external_path_name,
    MIN(e.guid) AS sample_external_guid
FROM source_relations r
LEFT JOIN source_objects target
  ON target.serialized_file = r.to_file COLLATE NOCASE
 AND target.path_id = r.to_path_id
 AND {targetTypeFilter}
LEFT JOIN source_externals e
  ON e.serialized_file = r.from_file COLLATE NOCASE
 AND e.file_id = r.to_file_id
WHERE r.relation = $relation
  AND target.id IS NULL
GROUP BY r.to_file, r.to_path_id, r.to_type_hint
ORDER BY relation_count DESC, distinct_referrer_count DESC;";
            command.Parameters.AddWithValue("$relation", relation);
            command.Parameters.AddWithValue("$targetType", targetType);
            var rows = new List<MissingCabRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var targetFile = ReadString(reader, "to_file");
                var externalFile = ReadString(reader, "sample_external_file_name");
                var externalPath = ReadString(reader, "sample_external_path_name");
                var cabName = ExtractCabName(targetFile, externalFile, externalPath);
                if (string.IsNullOrWhiteSpace(cabName))
                {
                    continue;
                }

                rows.Add(new MissingCabRow(
                    relation,
                    cabName,
                    targetFile,
                    ReadInt64(reader, "to_path_id"),
                    ReadString(reader, "to_type_hint"),
                    ReadInt64(reader, "relation_count"),
                    ReadInt64(reader, "distinct_referrer_count"),
                    ReadString(reader, "sample_from_source"),
                    ReadString(reader, "sample_from_file"),
                    ReadString(reader, "sample_from_type"),
                    ReadString(reader, "sample_from_name"),
                    ReadInt64(reader, "sample_from_path_id"),
                    externalFile,
                    externalPath,
                    ReadString(reader, "sample_external_guid")));
            }

            return rows;
        }

        private static string UnityTextureTypeSql(string alias)
        {
            // 材质贴图槽引用 Unity Texture 基类，Cubemap/Texture3D 也属于已解析目标。
            return $"{alias}.type IN ('Texture2D', 'Texture2DArray', 'Texture3D', 'Cubemap')";
        }

        private static IEnumerable<string> ReadClosureBundleFiles(JObject report, ISet<string> allowedCabs = null)
        {
            if (allowedCabs == null)
            {
                foreach (var value in report?["locatedUnityBundleFiles"]?.Values<string>() ?? Array.Empty<string>())
                {
                    yield return value;
                }
            }

            foreach (var location in report?["locations"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var cabName = (string)location["cabName"];
                if (allowedCabs != null && (string.IsNullOrWhiteSpace(cabName) || !allowedCabs.Contains(cabName)))
                {
                    continue;
                }

                var bundle = (string)location["unityBundleFile"];
                if (!string.IsNullOrWhiteSpace(bundle))
                {
                    yield return bundle;
                }
            }
        }

        private static string NormalizeClosureBundlePath(string path)
        {
            return (path ?? string.Empty)
                .Trim()
                .Trim('"')
                .TrimStart('/', '\\')
                .Replace('\\', '/');
        }

        private static HashSet<string> NormalizeCabNames(IEnumerable<string> cabNames)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in cabNames ?? Array.Empty<string>())
            {
                foreach (var item in raw.Split(new[] { ',', ';', '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var text = item.Trim().Trim('"');
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    result.Add(Path.GetFileName(text));
                }
            }

            return result;
        }

        private static string ExtractCabName(params string[] values)
        {
            foreach (var value in values ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var normalized = value.Replace('\\', '/');
                var match = Regex.Match(normalized, @"CAB-[A-Za-z0-9_\-]+", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return match.Value;
                }
            }

            return string.Empty;
        }

        private static string BuildEndfieldVfsFilesRegex(IEnumerable<string> unityBundleFiles)
        {
            var files = (unityBundleFiles ?? Array.Empty<string>())
                .Select(x => (x ?? string.Empty).Replace('\\', '/').Trim('/'))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (files.Length == 0)
            {
                return string.Empty;
            }

            var groups = files
                .Select(x => new
                {
                    Path = x,
                    Directory = Path.GetDirectoryName(x)?.Replace('\\', '/') ?? string.Empty,
                    FileName = Path.GetFileName(x),
                })
                .GroupBy(x => x.Directory, StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var parts = new List<string>();
            foreach (var group in groups)
            {
                var names = group.Select(x => x.FileName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                var hashes = names
                    .Select(x => Regex.Match(x, @"^([0-9a-fA-F]{32})\.ab$", RegexOptions.IgnoreCase))
                    .ToArray();
                if (hashes.Length == names.Length && hashes.All(x => x.Success))
                {
                    var prefix = string.IsNullOrWhiteSpace(group.Key) ? string.Empty : Regex.Escape(group.Key).Replace("\\/", "/") + "/";
                    parts.Add(prefix + "(" + string.Join("|", hashes.Select(x => x.Groups[1].Value)) + @")\.ab$");
                    continue;
                }

                parts.AddRange(group.Select(x => Regex.Escape(x.Path).Replace("\\/", "/") + "$"));
            }

            return parts.Count == 1 ? parts[0] : "(" + string.Join("|", parts) + ")";
        }

        private static JObject ToJson(MissingCabTarget target)
        {
            var sample = target.Sample;
            return new JObject
            {
                ["cabName"] = target.CabName,
                ["relationCount"] = target.RelationCount,
                ["distinctReferrerCount"] = target.DistinctReferrerCount,
                ["relations"] = new JArray(target.Relations),
                ["sampleTarget"] = new JObject
                {
                    ["serializedFile"] = sample.TargetFile,
                    ["pathId"] = sample.TargetPathId,
                    ["typeHint"] = sample.TargetTypeHint,
                },
                ["sampleReferrer"] = new JObject
                {
                    ["source"] = sample.SampleFromSource,
                    ["serializedFile"] = sample.SampleFromFile,
                    ["type"] = sample.SampleFromType,
                    ["name"] = sample.SampleFromName,
                    ["pathId"] = sample.SampleFromPathId,
                },
                ["sampleExternal"] = new JObject
                {
                    ["fileName"] = sample.SampleExternalFileName,
                    ["pathName"] = sample.SampleExternalPathName,
                    ["guid"] = sample.SampleExternalGuid,
                },
            };
        }

        private static string ReadString(SqliteDataReader reader, string column)
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
        }

        private static long ReadInt64(SqliteDataReader reader, string column)
        {
            var ordinal = reader.GetOrdinal(column);
            if (reader.IsDBNull(ordinal))
            {
                return 0;
            }

            return reader.GetInt64(ordinal);
        }

        private static List<SearchBytes> NormalizeSearchStrings(IEnumerable<string> searchStrings)
        {
            var result = new List<SearchBytes>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in searchStrings ?? Array.Empty<string>())
            {
                foreach (var item in raw.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var text = item.Trim().Trim('"');
                    if (string.IsNullOrWhiteSpace(text) || !seen.Add(text))
                    {
                        continue;
                    }

                    result.Add(new SearchBytes(text, Encoding.ASCII.GetBytes(text)));
                }
            }

            return result;
        }

        private static IReadOnlyList<long> FindAsciiOffsets(byte[] data, SearchBytes target)
        {
            var offsets = new List<long>();
            if (data == null || data.Length == 0 || target.Bytes.Length == 0 || target.Bytes.Length > data.Length)
            {
                return offsets;
            }

            var span = data.AsSpan();
            var needle = target.Bytes.AsSpan();
            var start = 0;
            while (start <= data.Length - target.Bytes.Length)
            {
                var relative = span[start..].IndexOf(needle);
                if (relative < 0)
                {
                    break;
                }

                var offset = start + relative;
                offsets.Add(offset);
                start = offset + 1;
            }

            return offsets;
        }

        private static EndfieldVfsBlockFile.VfsFileEntry FindOwner(
            IReadOnlyList<EndfieldVfsBlockFile.VfsFileEntry> entries,
            long chunkOffset,
            Func<string, bool> innerFileFilter)
        {
            foreach (var entry in entries)
            {
                if (innerFileFilter != null && !innerFileFilter(entry.FileName ?? string.Empty))
                {
                    continue;
                }
                if (chunkOffset >= entry.Offset && chunkOffset < entry.Offset + entry.Length)
                {
                    return entry;
                }
            }

            return null;
        }

        private static JArray BuildStringTargetSummaries(JArray locations)
        {
            var summaries = new JArray();
            foreach (var group in locations
                .OfType<JObject>()
                .GroupBy(x => (string)x["searchString"] ?? string.Empty, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                var innerFiles = group
                    .Select(x => (string)x["innerFile"] ?? string.Empty)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                summaries.Add(new JObject
                {
                    ["searchString"] = group.Key,
                    ["hitCount"] = group.Count(),
                    ["innerFileCount"] = innerFiles.Length,
                    ["innerFiles"] = new JArray(innerFiles.Take(64)),
                });
            }

            return summaries;
        }

        private static JArray BuildStringInnerFileSummaries(JArray locations)
        {
            var summaries = new JArray();
            foreach (var group in locations
                .OfType<JObject>()
                .GroupBy(x => (string)x["innerFile"] ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(group.Key))
                {
                    continue;
                }

                var targets = group
                    .Select(x => (string)x["searchString"] ?? string.Empty)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToArray();

                summaries.Add(new JObject
                {
                    ["innerFile"] = group.Key,
                    ["hitCount"] = group.Count(),
                    ["targetCount"] = targets.Length,
                    ["targets"] = new JArray(targets),
                });
            }

            return summaries;
        }

        private static IEnumerable<string> EnumerateBlockFiles(string inputPath)
        {
            if (File.Exists(inputPath))
            {
                yield return Path.GetFullPath(inputPath);
                yield break;
            }

            foreach (var file in Directory.EnumerateFiles(inputPath, "*.blc", SearchOption.AllDirectories))
            {
                yield return Path.GetFullPath(file);
            }
        }

        private static Func<string, bool> BuildInnerFileFilter(Regex[] filters)
        {
            if (filters == null || filters.Length == 0)
            {
                return null;
            }

            return path => filters.Any(filter => filter.IsMatch(path ?? string.Empty));
        }

        private static JObject ToJson(string inputPath, EndfieldVfsBlockFile.CabLocation location)
        {
            return new JObject
            {
                ["cabName"] = location.CabName,
                ["blockList"] = MakeRelativeOrSelf(inputPath, location.BlockListPath),
                ["chunkFile"] = location.ChunkFileName,
                ["unityBundleFile"] = location.UnityBundleFile,
                ["unityBundleLength"] = location.UnityBundleLength,
                ["bundleInnerPath"] = location.BundleInnerPath,
                ["bundleInnerLength"] = location.BundleInnerLength,
            };
        }

        private static string MakeRelativeOrSelf(string root, string path)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                var basePath = Directory.Exists(root)
                    ? Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    : Path.GetDirectoryName(Path.GetFullPath(root));
                if (!string.IsNullOrWhiteSpace(basePath))
                {
                    var relative = Path.GetRelativePath(basePath, fullPath);
                    if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                    {
                        return relative.Replace('\\', '/');
                    }
                }
            }
            catch
            {
                // 诊断报告路径归一化失败时保留原路径。
            }

            return path?.Replace('\\', '/') ?? string.Empty;
        }

        private sealed class SearchBytes
        {
            public SearchBytes(string text, byte[] bytes)
            {
                Text = text;
                Bytes = bytes ?? Array.Empty<byte>();
            }

            public string Text { get; }
            public byte[] Bytes { get; }
        }

        private sealed class MissingCabRow
        {
            public MissingCabRow(
                string relation,
                string cabName,
                string targetFile,
                long targetPathId,
                string targetTypeHint,
                long relationCount,
                long distinctReferrerCount,
                string sampleFromSource,
                string sampleFromFile,
                string sampleFromType,
                string sampleFromName,
                long sampleFromPathId,
                string sampleExternalFileName,
                string sampleExternalPathName,
                string sampleExternalGuid)
            {
                Relation = relation;
                CabName = cabName;
                TargetFile = targetFile;
                TargetPathId = targetPathId;
                TargetTypeHint = targetTypeHint;
                RelationCount = relationCount;
                DistinctReferrerCount = distinctReferrerCount;
                SampleFromSource = sampleFromSource;
                SampleFromFile = sampleFromFile;
                SampleFromType = sampleFromType;
                SampleFromName = sampleFromName;
                SampleFromPathId = sampleFromPathId;
                SampleExternalFileName = sampleExternalFileName;
                SampleExternalPathName = sampleExternalPathName;
                SampleExternalGuid = sampleExternalGuid;
            }

            public string Relation { get; }
            public string CabName { get; }
            public string TargetFile { get; }
            public long TargetPathId { get; }
            public string TargetTypeHint { get; }
            public long RelationCount { get; }
            public long DistinctReferrerCount { get; }
            public string SampleFromSource { get; }
            public string SampleFromFile { get; }
            public string SampleFromType { get; }
            public string SampleFromName { get; }
            public long SampleFromPathId { get; }
            public string SampleExternalFileName { get; }
            public string SampleExternalPathName { get; }
            public string SampleExternalGuid { get; }
        }

        private sealed class MissingCabTarget
        {
            public MissingCabTarget(string cabName, long relationCount, long distinctReferrerCount, string[] relations, MissingCabRow sample)
            {
                CabName = cabName;
                RelationCount = relationCount;
                DistinctReferrerCount = distinctReferrerCount;
                Relations = relations ?? Array.Empty<string>();
                Sample = sample;
            }

            public string CabName { get; }
            public long RelationCount { get; }
            public long DistinctReferrerCount { get; }
            public string[] Relations { get; }
            public MissingCabRow Sample { get; }
        }

        public sealed class SourceCabClosureFilter
        {
            public SourceCabClosureFilter(
                Func<string, bool> filter,
                string reportPath,
                string[] bundleFiles,
                int selectedCabCount,
                int foundTargetCount,
                int sourceMissingCabCount)
            {
                Filter = filter ?? (_ => false);
                ReportPath = reportPath ?? string.Empty;
                BundleFiles = bundleFiles ?? Array.Empty<string>();
                SelectedCabCount = selectedCabCount;
                FoundTargetCount = foundTargetCount;
                SourceMissingCabCount = sourceMissingCabCount;
            }

            public Func<string, bool> Filter { get; }
            public string ReportPath { get; }
            public string[] BundleFiles { get; }
            public int SelectedCabCount { get; }
            public int FoundTargetCount { get; }
            public int SourceMissingCabCount { get; }
        }
    }
}
