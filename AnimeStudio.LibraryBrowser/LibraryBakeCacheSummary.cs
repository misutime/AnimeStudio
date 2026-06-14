using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace AnimeStudio.LibraryBrowser
{
    internal sealed class LibraryBakeCacheSummary
    {
        public static LibraryBakeCacheSummary Empty { get; } = new(false, "", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "");

        public LibraryBakeCacheSummary(
            bool exists,
            string summaryPath,
            long explicitUnityBakeCandidates,
            long effectiveBakeReadyCandidates,
            double effectiveCoveragePercent,
            long cachedCandidates,
            long trustedBakedCandidates,
            long staticPoseCandidates,
            long needsReviewCandidates,
            long untrustedBakedCandidates,
            double cacheCoveragePercent,
            double trustedBakedCoveragePercent,
            string generatedAt)
        {
            Exists = exists;
            SummaryPath = summaryPath ?? "";
            ExplicitUnityBakeCandidates = explicitUnityBakeCandidates;
            EffectiveBakeReadyCandidates = effectiveBakeReadyCandidates;
            EffectiveCoveragePercent = effectiveCoveragePercent;
            CachedCandidates = cachedCandidates;
            TrustedBakedCandidates = trustedBakedCandidates;
            StaticPoseCandidates = staticPoseCandidates;
            NeedsReviewCandidates = needsReviewCandidates;
            UntrustedBakedCandidates = untrustedBakedCandidates;
            CacheCoveragePercent = cacheCoveragePercent;
            TrustedBakedCoveragePercent = trustedBakedCoveragePercent;
            GeneratedAt = generatedAt ?? "";
        }

        public bool Exists { get; }
        public string SummaryPath { get; }
        public long ExplicitUnityBakeCandidates { get; }
        public long EffectiveBakeReadyCandidates { get; }
        public double EffectiveCoveragePercent { get; }
        public long CachedCandidates { get; }
        public long TrustedBakedCandidates { get; }
        public long StaticPoseCandidates { get; }
        public long NeedsReviewCandidates { get; }
        public long UntrustedBakedCandidates { get; }
        public double CacheCoveragePercent { get; }
        public double TrustedBakedCoveragePercent { get; }
        public string GeneratedAt { get; }

        public string ShortLabel()
        {
            if (!Exists)
            {
                return "Unity烘焙摘要: 未生成";
            }

            return $"Unity烘焙oracle {FormatPercent(EffectiveCoveragePercent)}，可信 {TrustedBakedCandidates}";
        }

        public string DetailText()
        {
            if (!Exists)
            {
                return "Unity烘焙摘要: 未生成。可用 CLI 批量烘焙或 --preview_validation_limit 0 刷新全局统计。" + Environment.NewLine;
            }

            return
                $"Unity烘焙摘要: {SummaryPath}{Environment.NewLine}" +
                $"Unity烘焙摘要时间: {EmptyAsUnknown(GeneratedAt)}{Environment.NewLine}" +
                $"显式Humanoid/Muscle候选: {ExplicitUnityBakeCandidates:N0}{Environment.NewLine}" +
                $"有效Avatar oracle候选: {EffectiveBakeReadyCandidates:N0} ({FormatPercent(EffectiveCoveragePercent)}){Environment.NewLine}" +
                $"已缓存烘焙记录: {CachedCandidates:N0} ({FormatPercent(CacheCoveragePercent)}){Environment.NewLine}" +
                $"可信baked glTF: {TrustedBakedCandidates:N0} ({FormatPercent(TrustedBakedCoveragePercent)}){Environment.NewLine}" +
                $"静态姿态/需复查/不可信: {StaticPoseCandidates:N0} / {NeedsReviewCandidates:N0} / {UntrustedBakedCandidates:N0}{Environment.NewLine}";
        }

        public static LibraryBakeCacheSummary Load(string libraryRoot)
        {
            if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
            {
                return Empty;
            }

            var path = Path.Combine(libraryRoot, "animation_bake_cache_summary.json");
            if (!File.Exists(path))
            {
                return Empty;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                return new LibraryBakeCacheSummary(
                    true,
                    path,
                    ReadInt64(root, "explicitUnityBakeCandidates"),
                    ReadInt64(root, "effectiveBakeReadyExplicitUnityBakeCandidates"),
                    ReadDouble(root, "effectiveBakeReadyExplicitUnityBakeCoveragePercent"),
                    ReadInt64(root, "cachedCandidates"),
                    ReadInt64(root, "trustedBakedCandidates"),
                    ReadInt64(root, "staticPoseCandidates"),
                    ReadInt64(root, "needsReviewCandidates", "uniqueNeedsReviewCandidates"),
                    ReadInt64(root, "untrustedBakedCandidates"),
                    ReadDouble(root, "cacheCoveragePercent"),
                    ReadDouble(root, "trustedBakedCoveragePercent"),
                    ReadString(root, "generatedAt"));
            }
            catch
            {
                return Empty;
            }
        }

        private static string EmptyAsUnknown(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
        }

        private static string FormatPercent(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture) + "%";
        }

        private static string ReadString(JsonElement element, string property)
        {
            return element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? ""
                    : "";
        }

        private static long ReadInt64(JsonElement element, string property, string fallbackProperty = null)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            {
                if (string.IsNullOrWhiteSpace(fallbackProperty) || !element.TryGetProperty(fallbackProperty, out value))
                {
                    return 0;
                }
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            {
                return number;
            }

            return value.ValueKind == JsonValueKind.String
                && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
                    ? number
                    : 0;
        }

        private static double ReadDouble(JsonElement element, string property)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            {
                return 0;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            {
                return number;
            }

            return value.ValueKind == JsonValueKind.String
                && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                    ? number
                    : 0;
        }
    }
}
