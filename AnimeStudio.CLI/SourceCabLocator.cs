using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AnimeStudio.CLI
{
    internal static class SourceCabLocator
    {
        public static void Locate(string inputRoot, string outputRoot, Game game, IEnumerable<string> cabNames)
        {
            if (string.IsNullOrWhiteSpace(inputRoot) || !Directory.Exists(inputRoot))
            {
                throw new DirectoryNotFoundException($"输入目录不存在: {inputRoot}");
            }

            var targets = cabNames?
                .Select(NormalizeCabName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (targets.Count == 0)
            {
                throw new ArgumentException("--locate_source_cabs 至少需要一个 CAB 名称。");
            }

            Directory.CreateDirectory(outputRoot);
            var found = new Dictionary<string, List<JObject>>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in targets)
            {
                found[target] = new List<JObject>();
            }

            var files = Directory.GetFiles(inputRoot, "*", SearchOption.AllDirectories);
            var scanned = 0;
            var failed = 0;
            foreach (var file in files)
            {
                scanned++;
                if (scanned % 1000 == 0)
                {
                    Logger.Info($"已扫描 {scanned}/{files.Length} 个源文件，找到 {found.Count(x => x.Value.Count > 0)}/{targets.Count} 个 CAB 目标。");
                }

                try
                {
                    LocateInFile(inputRoot, file, game, targets, found);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    failed++;
                    if (failed <= 5)
                    {
                        Logger.Debug($"定位 CAB 时跳过源文件: {file}. {ex.GetType().Name}: {ex.Message}");
                    }
                }

                if (found.All(x => x.Value.Count > 0))
                {
                    break;
                }
            }

            var missing = targets.Where(x => found[x].Count == 0).OrderBy(x => x).ToArray();
            var sourceFiles = found.Values
                .SelectMany(x => x)
                .Select(x => (string)x["source"])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x)
                .ToArray();

            var report = new JObject
            {
                ["schemaVersion"] = 1,
                ["sourceRoot"] = Path.GetFullPath(inputRoot),
                ["game"] = game?.Name,
                ["requestedCabs"] = new JArray(targets.OrderBy(x => x)),
                ["scannedFileCount"] = scanned,
                ["totalFileCount"] = files.Length,
                ["failedFileCount"] = failed,
                ["stoppedAfterAllFound"] = missing.Length == 0,
                ["sourceFiles"] = new JArray(sourceFiles),
                ["missingCabs"] = new JArray(missing),
                ["locations"] = JObject.FromObject(found),
                ["rule"] = "只通过 Unity Bundle 目录和 SerializedFile 元数据定位 CAB，不导出对象数据。",
            };

            var output = Path.Combine(outputRoot, "source_cab_locations.json");
            File.WriteAllText(output, report.ToString(Formatting.Indented));
            Logger.Info($"已写出源 CAB 定位报告: {output}");
        }

        private static void LocateInFile(
            string inputRoot,
            string file,
            Game game,
            IReadOnlySet<string> targets,
            Dictionary<string, List<JObject>> found)
        {
            using var reader = new FileReader(file).PreProcessing(game);
            if (reader.FileType == FileType.BundleFile)
            {
                // 这里只需要 Bundle 目录里的内部 CAB 名称，避免为了定位依赖而解压大对象流。
                using var bundle = new BundleFile(reader, game, readBlocks: false);
                foreach (var inner in bundle.fileList ?? Enumerable.Empty<StreamFile>())
                {
                    RecordIfTarget(inputRoot, file, inner.fileName, inner.path, targets, found);
                }
                return;
            }

            if (reader.FileType == FileType.AssetsFile)
            {
                var assetsFile = new SerializedFile(reader, new AssetsManager { Game = game, SkipProcess = true });
                RecordIfTarget(inputRoot, file, assetsFile.fileName, assetsFile.fileName, targets, found);
            }
        }

        private static void RecordIfTarget(
            string inputRoot,
            string sourceFile,
            string cabName,
            string innerPath,
            IReadOnlySet<string> targets,
            Dictionary<string, List<JObject>> found)
        {
            cabName = NormalizeCabName(cabName);
            if (!targets.Contains(cabName))
            {
                return;
            }

            var relativeSource = Path.GetRelativePath(inputRoot, sourceFile)
                .Replace(Path.DirectorySeparatorChar, '/');
            found[cabName].Add(new JObject
            {
                ["cab"] = cabName,
                ["source"] = relativeSource,
                ["sourceFullPath"] = Path.GetFullPath(sourceFile),
                ["innerPath"] = innerPath,
            });
        }

        private static string NormalizeCabName(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            value = value.Replace('\\', '/');
            var slash = value.LastIndexOf('/');
            return slash >= 0 ? value[(slash + 1)..] : value;
        }
    }
}
