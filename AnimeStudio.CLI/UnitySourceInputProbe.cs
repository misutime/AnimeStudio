using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AnimeStudio.CLI
{
    internal static class UnitySourceInputProbe
    {
        private const int SampleLimit = 12;
        private static readonly byte[] NarakaHeader = { 0x15, 0x1E, 0x1C, 0x0D, 0x0D, 0x23, 0x21 };

        public static string WriteReport(string sourceRoot, string outputRoot, Game game)
        {
            if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            {
                throw new DirectoryNotFoundException($"Source input folder not found: {sourceRoot}");
            }

            Directory.CreateDirectory(outputRoot);
            var report = Probe(sourceRoot, game);
            var reportPath = Path.Combine(outputRoot, "source_input_probe.json");
            File.WriteAllText(reportPath, report.ToString(Formatting.Indented), new UTF8Encoding(false));
            Logger.Info($"Wrote source input probe: {reportPath}");
            return reportPath;
        }

        private static JObject Probe(string sourceRoot, Game game)
        {
            var files = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories);
            var sourceFileCount = 0;
            var totalBytes = 0L;
            var unityFsHeaderFileCount = 0;
            var unityWebHeaderFileCount = 0;
            var unityRawHeaderFileCount = 0;
            var narakaHeaderFileCount = 0;
            var narakaLoadableCandidateCount = 0;
            var pakFileCount = 0;
            var pakBytes = 0L;
            var noExtensionFileCount = 0;
            var headerSizeOffsetCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var extensionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var pakSamples = new List<string>();
            var unityHeaderSamples = new List<string>();
            var narakaHeaderSamples = new List<string>();

            foreach (var file in files)
            {
                sourceFileCount++;
                var length = SafeLength(file);
                totalBytes += length;

                var extension = Path.GetExtension(file);
                var extensionKey = string.IsNullOrEmpty(extension) ? "(no extension)" : extension.ToLowerInvariant();
                Increment(extensionCounts, extensionKey);
                if (string.IsNullOrEmpty(extension))
                {
                    noExtensionFileCount++;
                }

                if (string.Equals(extension, ".pak", StringComparison.OrdinalIgnoreCase))
                {
                    pakFileCount++;
                    pakBytes += length;
                    AddSample(pakSamples, MakeRelative(sourceRoot, file));
                }

                if (!TryReadHeader(file, out var header))
                {
                    continue;
                }

                var headerText = Encoding.ASCII.GetString(header, 0, Math.Min(header.Length, 16));
                if (headerText.StartsWith("UnityFS", StringComparison.Ordinal))
                {
                    unityFsHeaderFileCount++;
                    AddSample(unityHeaderSamples, MakeRelative(sourceRoot, file));
                }
                else if (headerText.StartsWith("UnityWeb", StringComparison.Ordinal))
                {
                    unityWebHeaderFileCount++;
                    AddSample(unityHeaderSamples, MakeRelative(sourceRoot, file));
                }
                else if (headerText.StartsWith("UnityRaw", StringComparison.Ordinal))
                {
                    unityRawHeaderFileCount++;
                    AddSample(unityHeaderSamples, MakeRelative(sourceRoot, file));
                }

                if (TryReadNarakaBundleHeader(file, header, out var sizeOffset))
                {
                    narakaHeaderFileCount++;
                    var offsetText = FormatOffset(sizeOffset);
                    Increment(headerSizeOffsetCounts, offsetText);
                    AddSample(narakaHeaderSamples, MakeRelative(sourceRoot, file) + "|" + offsetText);
                    if (IsKnownNarakaLoadableOffset(sizeOffset, header))
                    {
                        narakaLoadableCandidateCount++;
                    }
                }
            }

            var loadableHeaderFileCount = unityFsHeaderFileCount
                + unityWebHeaderFileCount
                + unityRawHeaderFileCount
                + narakaLoadableCandidateCount;

            var recommendation = BuildRecommendation(game, pakFileCount, narakaHeaderFileCount, narakaLoadableCandidateCount, loadableHeaderFileCount);

            return new JObject
            {
                ["kind"] = "UnitySourceInputProbe",
                ["sourceRoot"] = sourceRoot,
                ["game"] = game?.Name,
                ["sourceFileCount"] = sourceFileCount,
                ["totalBytes"] = totalBytes,
                ["totalSize"] = FormatBytes(totalBytes),
                ["noExtensionFileCount"] = noExtensionFileCount,
                ["loadableHeaderFileCount"] = loadableHeaderFileCount,
                ["unityFsHeaderFileCount"] = unityFsHeaderFileCount,
                ["unityWebHeaderFileCount"] = unityWebHeaderFileCount,
                ["unityRawHeaderFileCount"] = unityRawHeaderFileCount,
                ["narakaHeaderFileCount"] = narakaHeaderFileCount,
                ["narakaLoadableCandidateCount"] = narakaLoadableCandidateCount,
                ["pakFileCount"] = pakFileCount,
                ["pakBytes"] = pakBytes,
                ["pakSize"] = FormatBytes(pakBytes),
                ["externalPakAesKeyNeededForThisInput"] = pakFileCount > 0 && loadableHeaderFileCount == 0,
                ["headerSizeOffsetCounts"] = JObject.FromObject(headerSizeOffsetCounts.OrderByDescending(x => x.Value).ToDictionary(x => x.Key, x => x.Value)),
                ["extensionCounts"] = JObject.FromObject(extensionCounts.OrderByDescending(x => x.Value).Take(24).ToDictionary(x => x.Key, x => x.Value)),
                ["pakSamples"] = new JArray(pakSamples),
                ["unityHeaderSamples"] = new JArray(unityHeaderSamples),
                ["narakaHeaderSamples"] = new JArray(narakaHeaderSamples),
                ["recommendation"] = recommendation,
                ["rule"] = "该探针只检查输入文件形态，不解包 .pak，不验证社区 AES key，也不修改素材库。Naraka 替代头表示当前 StreamingAssets 可走 AnimeStudio Naraka bundle header fix 主线；外部 .pak/AES 只能作为独立预处理诊断。",
            };
        }

        private static JObject BuildRecommendation(Game game, int pakFileCount, int narakaHeaderFileCount, int narakaLoadableCandidateCount, int loadableHeaderFileCount)
        {
            var status = "unknown";
            var nextStep = "Build unity_source_index.db with --build_source_sqlite_index after confirming --game.";
            var notes = new JArray();

            if (game?.Type.IsNaraka() == true && narakaHeaderFileCount > 0)
            {
                status = "naraka_streaming_assets_loadable";
                nextStep = "Use the current StreamingAssets folder with --game Naraka; QuickBMS/AES is not part of this input path.";
                notes.Add("检测到 Naraka 替代 UnityFS 文件头，当前工具会在 BundleFile 中按 Naraka profile 修正 header 和 4KB block 对齐。");
                if (narakaLoadableCandidateCount < narakaHeaderFileCount)
                {
                    notes.Add("部分 Naraka header offset 不在已知快速规则内；若后续加载失败，应补 header fix 规则，而不是先套 .pak AES。");
                }
            }
            else if (loadableHeaderFileCount > 0)
            {
                status = "unity_bundles_loadable";
                nextStep = "Use this folder directly as Unity source input.";
            }
            else if (pakFileCount > 0)
            {
                status = "external_pak_preprocess_needed";
                nextStep = "This input looks like external .pak archives; unpack/decrypt outside the default Library export, then point AnimeStudio at extracted Unity bundles.";
                notes.Add("探针不会验证用户提供的 AES key；key 是否匹配只能由外部 .pak 解包工具和本地包版本证明。");
            }

            return new JObject
            {
                ["status"] = status,
                ["nextStep"] = nextStep,
                ["notes"] = notes,
            };
        }

        private static bool TryReadHeader(string path, out byte[] header)
        {
            header = Array.Empty<byte>();
            try
            {
                using var stream = File.OpenRead(path);
                if (stream.Length <= 0)
                {
                    return false;
                }

                header = new byte[Math.Min(64, stream.Length)];
                var read = stream.Read(header, 0, header.Length);
                if (read == header.Length)
                {
                    return true;
                }

                Array.Resize(ref header, read);
                return read > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsNarakaHeader(byte[] header)
        {
            return header.Length >= NarakaHeader.Length
                && NarakaHeader.Select((x, i) => header[i] == x).All(x => x);
        }

        private static bool TryReadNarakaBundleHeader(string path, byte[] header, out long sizeOffset)
        {
            sizeOffset = 0;
            if (!IsNarakaHeader(header))
            {
                return false;
            }

            try
            {
                using var stream = File.OpenRead(path);
                if (stream.Length < 32)
                {
                    return false;
                }

                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
                reader.ReadBytes(8);
                ReadUInt32BigEndian(reader);
                SkipNullTerminatedString(reader, stream.Length);
                SkipNullTerminatedString(reader, stream.Length);
                if (stream.Position + 20 > stream.Length)
                {
                    return false;
                }

                var size = ReadInt64BigEndian(reader);
                sizeOffset = size - stream.Length;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsKnownNarakaLoadableOffset(long sizeOffset, byte[] header)
        {
            if (sizeOffset == 0x14 || sizeOffset == 0x16 || sizeOffset == 0x1A || sizeOffset == 0x1E)
            {
                return true;
            }

            if (sizeOffset >= 0 || header.Length < 32)
            {
                return false;
            }

            // 负 offset 表示 header.size 小于真实文件长度，尾部可能有补丁/附加数据。
            // BundleFile 会再按字段特征确认；快速探针只能把它标成 Naraka 候选，不能替代正式加载验证。
            return true;
        }

        private static uint ReadUInt32BigEndian(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(4);
            if (bytes.Length != 4)
            {
                return 0;
            }

            return ((uint)bytes[0] << 24)
                | ((uint)bytes[1] << 16)
                | ((uint)bytes[2] << 8)
                | bytes[3];
        }

        private static long ReadInt64BigEndian(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(8);
            if (bytes.Length != 8)
            {
                return 0;
            }

            ulong value = ((ulong)bytes[0] << 56)
                | ((ulong)bytes[1] << 48)
                | ((ulong)bytes[2] << 40)
                | ((ulong)bytes[3] << 32)
                | ((ulong)bytes[4] << 24)
                | ((ulong)bytes[5] << 16)
                | ((ulong)bytes[6] << 8)
                | bytes[7];
            return unchecked((long)value);
        }

        private static void SkipNullTerminatedString(BinaryReader reader, long streamLength)
        {
            while (reader.BaseStream.Position < streamLength && reader.ReadByte() != 0)
            {
            }
        }

        private static void Increment(IDictionary<string, int> values, string key)
        {
            values.TryGetValue(key, out var count);
            values[key] = count + 1;
        }

        private static void AddSample(ICollection<string> samples, string value)
        {
            if (samples.Count < SampleLimit)
            {
                samples.Add(value);
            }
        }

        private static long SafeLength(string path)
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

        private static string MakeRelative(string root, string path)
        {
            return Path.GetRelativePath(root, path).Replace('\\', '/');
        }

        private static string FormatOffset(long offset)
        {
            return offset == long.MinValue
                ? "unknown"
                : offset < 0 ? $"-0x{Math.Abs(offset):X}" : $"0x{offset:X}";
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return unit == 0 ? $"{bytes} {units[unit]}" : $"{value:0.##} {units[unit]}";
        }
    }
}
