using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AnimeStudio.CLI
{
    internal static class EndfieldManifestDependencyInspector
    {
        private const uint ManifestMagic = 0xFF11FF11;
        private const int MaxBundlePathBytes = 320;
        private static readonly string[] BundlePathPrefixes =
        {
            "main/",
            "initial/",
            "audio/",
            "builtin/",
            "shader/",
            "video/",
        };

        public static string Inspect(string manifestPath, string outputDirectory, Game game, string[] queryBundles)
        {
            if (game == null || !game.Type.IsArknightsEndfieldGroup())
            {
                Logger.Error("--inspect_endfield_manifest_deps only supports Arknights Endfield.");
                return null;
            }
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            {
                Logger.Error($"Manifest file not found: {manifestPath}");
                return null;
            }

            var queries = NormalizeQueryBundles(queryBundles);
            if (queries.Count == 0)
            {
                Logger.Error("--inspect_endfield_manifest_deps requires at least one bundle path such as main/4725f1e91f7790c5b9fea875.ab.");
                return null;
            }

            var outputRoot = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(Environment.CurrentDirectory, "EndfieldManifestDeps")
                : Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(outputRoot);

            var bytes = ReadManifestBytes(manifestPath, out var inputEncoding);
            var map = EndfieldManifestDependencyMap.Parse(bytes);
            var results = new JArray();
            foreach (var query in queries.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var normalized = NormalizeBundlePath(query);
                if (map.TryGet(normalized, out var entry))
                {
                    results.Add(new JObject
                    {
                        ["query"] = query,
                        ["bundlePath"] = entry.Path,
                        ["index"] = entry.Index,
                        ["offset"] = entry.Offset,
                        ["dependencyCount"] = entry.DependencyIndices.Length,
                        ["missingDependencyIndexCount"] = entry.MissingDependencyIndexCount,
                        ["dependencies"] = new JArray(entry.Dependencies),
                    });
                }
                else
                {
                    results.Add(new JObject
                    {
                        ["query"] = query,
                        ["bundlePath"] = normalized,
                        ["found"] = false,
                    });
                }
            }

            var report = new JObject
            {
                ["schemaVersion"] = 1,
                ["manifestPath"] = Path.GetFullPath(manifestPath),
                ["outputRoot"] = outputRoot,
                ["game"] = game.Name,
                ["inputEncoding"] = inputEncoding,
                ["manifestByteLength"] = bytes.LongLength,
                ["bundleCount"] = map.Count,
                ["queryCount"] = queries.Count,
                ["results"] = results,
                ["rule"] = "只读解析 Endfield manifest 中的 bundle -> bundle 依赖表，用于追踪模型第一阶段材质/贴图依赖闭包；不解析 Unity 对象，不创建或猜测模型-动画关系。",
            };

            var reportPath = Path.Combine(outputRoot, "endfield_manifest_dependencies.json");
            File.WriteAllText(reportPath, report.ToString(Formatting.Indented));
            Logger.Info($"Endfield manifest dependency report: {reportPath}");
            Logger.Info($"Parsed {map.Count} manifest bundle entries; queried {queries.Count} bundle(s).");
            return reportPath;
        }

        public static DependencyExpandedFilter BuildDependencyExpandedInnerFileFilter(string manifestPath, Regex[] rootFilters)
        {
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            {
                throw new FileNotFoundException($"Endfield manifest file not found: {manifestPath}", manifestPath);
            }
            if (rootFilters == null || rootFilters.Length == 0)
            {
                throw new ArgumentException("--endfield_manifest_deps requires --endfield_vfs_files to select root bundle(s).", nameof(rootFilters));
            }

            var bytes = ReadManifestBytes(manifestPath, out var inputEncoding);
            var map = EndfieldManifestDependencyMap.Parse(bytes);
            var rootBundles = map.Entries
                .Where(entry => MatchesAnyRootFilter(entry.Path, rootFilters))
                .Select(entry => entry.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var closure = ExpandDirectDependencySet(map, rootBundles);
            var exact = closure.ToHashSet(StringComparer.OrdinalIgnoreCase);

            bool Filter(string innerFile)
            {
                var normalized = NormalizeBundlePath(innerFile);
                return exact.Contains(normalized);
            }

            return new DependencyExpandedFilter(
                Filter,
                Path.GetFullPath(manifestPath),
                inputEncoding,
                map.Count,
                rootBundles,
                closure);
        }

        private static byte[] ReadManifestBytes(string manifestPath, out string inputEncoding)
        {
            var raw = File.ReadAllBytes(manifestPath);
            if (LooksLikeDecodedManifest(raw))
            {
                inputEncoding = "decoded";
                return raw;
            }

            try
            {
                using var reader = new FileReader(manifestPath);
                if (reader.FileType == FileType.BrotliFile)
                {
                    using var decompressed = ImportHelper.DecompressBrotli(reader);
                    decompressed.Position = 0;
                    var bytes = decompressed.ReadBytes((int)decompressed.Length);
                    if (!LooksLikeDecodedManifest(bytes))
                    {
                        throw new InvalidDataException("Brotli decompressed data does not look like an Endfield manifest.");
                    }

                    inputEncoding = "brotli";
                    return bytes;
                }
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is ArgumentException || e is NotSupportedException)
            {
                throw new InvalidDataException($"Unable to read Endfield manifest: {e.Message}", e);
            }

            throw new InvalidDataException("Input is neither a decoded Endfield manifest nor a recognized Brotli manifest file.");
        }

        private static bool LooksLikeDecodedManifest(byte[] bytes)
        {
            return bytes != null
                && bytes.Length >= sizeof(uint)
                && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, sizeof(uint))) == ManifestMagic;
        }

        private static HashSet<string> NormalizeQueryBundles(IEnumerable<string> queryBundles)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in queryBundles ?? Array.Empty<string>())
            {
                foreach (var item in raw.Split(new[] { ',', ';', '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var normalized = NormalizeBundlePath(item);
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        result.Add(normalized);
                    }
                }
            }

            return result;
        }

        private static string NormalizeBundlePath(string path)
        {
            var text = (path ?? string.Empty)
                .Trim()
                .Trim('"')
                .TrimStart('/', '\\')
                .Replace('\\', '/');
            const string dataPrefix = "Data/Bundles/Windows/";
            if (text.StartsWith(dataPrefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[dataPrefix.Length..];
            }

            return text;
        }

        private static bool MatchesAnyRootFilter(string bundlePath, Regex[] rootFilters)
        {
            var normalized = NormalizeBundlePath(bundlePath);
            var vfsPath = "Data/Bundles/Windows/" + normalized;
            return rootFilters.Any(filter =>
                filter.IsMatch(normalized)
                || filter.IsMatch(vfsPath)
                || filter.IsMatch("/" + normalized)
                || filter.IsMatch("/" + vfsPath));
        }

        private static string[] ExpandDirectDependencySet(EndfieldManifestDependencyMap map, string[] rootBundles)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in rootBundles ?? Array.Empty<string>())
            {
                var current = NormalizeBundlePath(root);
                if (string.IsNullOrWhiteSpace(current) || !result.Add(current))
                {
                    continue;
                }

                if (!map.TryGet(current, out var entry))
                {
                    continue;
                }

                foreach (var dependency in entry.Dependencies ?? Array.Empty<string>())
                {
                    result.Add(NormalizeBundlePath(dependency));
                }
            }

            return result
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private sealed class EndfieldManifestDependencyMap
        {
            private readonly Dictionary<string, BundleEntry> byPath;

            private EndfieldManifestDependencyMap(IReadOnlyList<BundleEntry> entries)
            {
                Entries = entries ?? Array.Empty<BundleEntry>();
                byPath = Entries
                    .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            }

            public IReadOnlyList<BundleEntry> Entries { get; }
            public int Count => Entries.Count;

            public static EndfieldManifestDependencyMap Parse(byte[] bytes)
            {
                if (!LooksLikeDecodedManifest(bytes))
                {
                    throw new InvalidDataException("Endfield manifest magic mismatch.");
                }

                var entries = ScanBundleEntries(bytes);
                var byIndex = entries.ToDictionary(x => x.Index);
                foreach (var entry in entries)
                {
                    var deps = new List<string>(entry.DependencyIndices.Length);
                    var missing = 0;
                    foreach (var dependencyIndex in entry.DependencyIndices)
                    {
                        if (byIndex.TryGetValue(dependencyIndex, out var dependency))
                        {
                            deps.Add(dependency.Path);
                        }
                        else
                        {
                            missing++;
                        }
                    }

                    entry.Dependencies = deps
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    entry.MissingDependencyIndexCount = missing;
                }

                return new EndfieldManifestDependencyMap(entries);
            }

            public bool TryGet(string path, out BundleEntry entry)
            {
                return byPath.TryGetValue(NormalizeBundlePath(path), out entry);
            }

            private static IReadOnlyList<BundleEntry> ScanBundleEntries(byte[] bytes)
            {
                var entries = new List<BundleEntry>(capacity: 4096);
                var seenOffsets = new HashSet<int>();
                var needles = BundlePathPrefixes
                    .Select(prefix => Encoding.Unicode.GetBytes(prefix))
                    .ToArray();

                for (var offset = 0; offset < bytes.Length - 16; offset += 2)
                {
                    var matched = false;
                    foreach (var needle in needles)
                    {
                        if (StartsWith(bytes, offset, needle))
                        {
                            matched = true;
                            break;
                        }
                    }
                    if (!matched || !seenOffsets.Add(offset))
                    {
                        continue;
                    }

                    if (!TryReadUtf16Path(bytes, offset, out var path, out var afterPath)
                        || !IsLikelyBundlePath(path)
                        || !TryReadDependencyIndices(bytes, afterPath, out var dependencyIndices))
                    {
                        continue;
                    }

                    entries.Add(new BundleEntry(entries.Count, offset, path, dependencyIndices));
                }

                return entries;
            }

            private static bool TryReadUtf16Path(byte[] bytes, int offset, out string path, out int afterPath)
            {
                path = string.Empty;
                afterPath = 0;
                var end = offset;
                var maxEnd = Math.Min(bytes.Length - 1, offset + MaxBundlePathBytes);
                while (end + 1 <= maxEnd)
                {
                    if (bytes[end] == 0 && bytes[end + 1] == 0)
                    {
                        var byteLength = end - offset;
                        if (byteLength <= 0 || (byteLength & 1) != 0)
                        {
                            return false;
                        }

                        path = Encoding.Unicode.GetString(bytes, offset, byteLength);
                        afterPath = end + 2;
                        return true;
                    }
                    end += 2;
                }

                return false;
            }

            private static bool TryReadDependencyIndices(byte[] bytes, int offset, out int[] dependencyIndices)
            {
                dependencyIndices = Array.Empty<int>();
                if (offset < 0 || offset + sizeof(uint) > bytes.Length)
                {
                    return false;
                }

                var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, sizeof(uint)));
                // 当前正式 manifest 中单包依赖数为数百。上限留宽一点，防止误把路径后的其它字段当依赖表。
                if (count > 20000)
                {
                    return false;
                }

                var totalBytes = checked((long)count * sizeof(uint));
                if (offset + sizeof(uint) + totalBytes > bytes.LongLength)
                {
                    return false;
                }

                var result = new int[count];
                var cursor = offset + sizeof(uint);
                for (var i = 0; i < count; i++)
                {
                    var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor, sizeof(uint)));
                    if (value > int.MaxValue)
                    {
                        return false;
                    }

                    result[i] = (int)value;
                    cursor += sizeof(uint);
                }

                dependencyIndices = result;
                return true;
            }

            private static bool StartsWith(byte[] bytes, int offset, byte[] needle)
            {
                if (offset < 0 || needle == null || offset + needle.Length > bytes.Length)
                {
                    return false;
                }

                return bytes.AsSpan(offset, needle.Length).SequenceEqual(needle);
            }

            private static bool IsLikelyBundlePath(string path)
            {
                if (string.IsNullOrWhiteSpace(path)
                    || !path.EndsWith(".ab", StringComparison.OrdinalIgnoreCase)
                    || !BundlePathPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }

                var slash = path.IndexOf('/');
                var name = slash >= 0 ? path[(slash + 1)..^3] : string.Empty;
                return name.Length >= 12
                    && name.Length <= 48
                    && name.All(IsHexLowerOrDigit);
            }

            private static bool IsHexLowerOrDigit(char ch)
            {
                return ch >= '0' && ch <= '9' || ch >= 'a' && ch <= 'f';
            }
        }

        private sealed class BundleEntry
        {
            public BundleEntry(int index, int offset, string path, int[] dependencyIndices)
            {
                Index = index;
                Offset = offset;
                Path = path;
                DependencyIndices = dependencyIndices ?? Array.Empty<int>();
                Dependencies = Array.Empty<string>();
            }

            public int Index { get; }
            public int Offset { get; }
            public string Path { get; }
            public int[] DependencyIndices { get; }
            public string[] Dependencies { get; set; }
            public int MissingDependencyIndexCount { get; set; }
        }

        public sealed class DependencyExpandedFilter
        {
            public DependencyExpandedFilter(
                Func<string, bool> filter,
                string manifestPath,
                string inputEncoding,
                int manifestBundleCount,
                string[] rootBundles,
                string[] closureBundles)
            {
                Filter = filter ?? (_ => false);
                ManifestPath = manifestPath ?? string.Empty;
                InputEncoding = inputEncoding ?? string.Empty;
                ManifestBundleCount = manifestBundleCount;
                RootBundles = rootBundles ?? Array.Empty<string>();
                ClosureBundles = closureBundles ?? Array.Empty<string>();
            }

            public Func<string, bool> Filter { get; }
            public string ManifestPath { get; }
            public string InputEncoding { get; }
            public int ManifestBundleCount { get; }
            public string[] RootBundles { get; }
            public string[] ClosureBundles { get; }
        }
    }
}
