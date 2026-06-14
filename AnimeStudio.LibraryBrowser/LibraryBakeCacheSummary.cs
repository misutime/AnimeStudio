using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AnimeStudio.LibraryBrowser
{
    internal sealed class LibraryBakeCacheSummary
    {
        public static LibraryBakeCacheSummary Empty { get; } = new(false, "", 0, 0, 0, 0, 0, 0, "", false, 0, 0, 0, 0, 0, 0, 0, "");

        public LibraryBakeCacheSummary(
            bool exists,
            string summaryPath,
            long explicitUnityBakeCandidates,
            long effectiveBakeReadyCandidates,
            double effectiveCoveragePercent,
            long importedAvatarAssetFileCount,
            long importedAvatarTrustedFileCount,
            long importedAvatarAssetKeyCount,
            string importedAvatarProbeFreshness,
            bool importedAvatarProbeEnforced,
            long cachedCandidates,
            long trustedBakedCandidates,
            long staticPoseCandidates,
            long needsReviewCandidates,
            long untrustedBakedCandidates,
            double cacheCoveragePercent,
            double trustedBakedCoveragePercent,
            string generatedAt,
            LatestBatchReport latestBatchReport = null)
        {
            Exists = exists;
            SummaryPath = summaryPath ?? "";
            ExplicitUnityBakeCandidates = explicitUnityBakeCandidates;
            EffectiveBakeReadyCandidates = effectiveBakeReadyCandidates;
            EffectiveCoveragePercent = effectiveCoveragePercent;
            ImportedAvatarAssetFileCount = importedAvatarAssetFileCount;
            ImportedAvatarTrustedFileCount = importedAvatarTrustedFileCount;
            ImportedAvatarAssetKeyCount = importedAvatarAssetKeyCount;
            ImportedAvatarProbeFreshness = importedAvatarProbeFreshness ?? "";
            ImportedAvatarProbeEnforced = importedAvatarProbeEnforced;
            CachedCandidates = cachedCandidates;
            TrustedBakedCandidates = trustedBakedCandidates;
            StaticPoseCandidates = staticPoseCandidates;
            NeedsReviewCandidates = needsReviewCandidates;
            UntrustedBakedCandidates = untrustedBakedCandidates;
            CacheCoveragePercent = cacheCoveragePercent;
            TrustedBakedCoveragePercent = trustedBakedCoveragePercent;
            GeneratedAt = generatedAt ?? "";
            LatestBatch = latestBatchReport ?? LatestBatchReport.Empty;
        }

        public bool Exists { get; }
        public string SummaryPath { get; }
        public long ExplicitUnityBakeCandidates { get; }
        public long EffectiveBakeReadyCandidates { get; }
        public double EffectiveCoveragePercent { get; }
        public long ImportedAvatarAssetFileCount { get; }
        public long ImportedAvatarTrustedFileCount { get; }
        public long ImportedAvatarAssetKeyCount { get; }
        public string ImportedAvatarProbeFreshness { get; }
        public bool ImportedAvatarProbeEnforced { get; }
        public long CachedCandidates { get; }
        public long TrustedBakedCandidates { get; }
        public long StaticPoseCandidates { get; }
        public long NeedsReviewCandidates { get; }
        public long UntrustedBakedCandidates { get; }
        public double CacheCoveragePercent { get; }
        public double TrustedBakedCoveragePercent { get; }
        public string GeneratedAt { get; }
        public LatestBatchReport LatestBatch { get; }

        public string ShortLabel()
        {
            if (!Exists)
            {
                if (LatestBatch.Exists && LatestBatch.SkippedMissingAvatarOracle > 0)
                {
                    return $"Unity烘焙摘要: 未生成，最近缺Avatar {LatestBatch.SkippedMissingAvatarOracle}";
                }

                return "Unity烘焙摘要: 未生成";
            }

            if (LatestBatch.Exists && LatestBatch.SkippedMissingAvatarOracle > 0)
            {
                return $"Unity烘焙oracle {FormatPercent(EffectiveCoveragePercent)}，可信 {TrustedBakedCandidates}，最近缺Avatar {LatestBatch.SkippedMissingAvatarOracle}";
            }

            return $"Unity烘焙oracle {FormatPercent(EffectiveCoveragePercent)}，可信 {TrustedBakedCandidates}";
        }

        public string DetailText()
        {
            if (!Exists)
            {
                return "Unity烘焙摘要: 未生成。可用 CLI 批量烘焙或 --preview_validation_limit 0 刷新全局统计。" + Environment.NewLine
                    + LatestBatchDetailText();
            }

            return
                $"Unity烘焙摘要: {SummaryPath}{Environment.NewLine}" +
                $"Unity烘焙摘要时间: {EmptyAsUnknown(GeneratedAt)}{Environment.NewLine}" +
                $"显式Humanoid/Muscle候选: {ExplicitUnityBakeCandidates:N0}{Environment.NewLine}" +
                $"有效Avatar oracle候选: {EffectiveBakeReadyCandidates:N0} ({FormatPercent(EffectiveCoveragePercent)}){Environment.NewLine}" +
                $"导入Avatar asset文件/key: {ImportedAvatarAssetFileCount:N0} / {ImportedAvatarAssetKeyCount:N0}{FormatImportedAvatarTrustText()}{Environment.NewLine}" +
                $"已缓存烘焙记录: {CachedCandidates:N0} ({FormatPercent(CacheCoveragePercent)}){Environment.NewLine}" +
                $"可信baked glTF: {TrustedBakedCandidates:N0} ({FormatPercent(TrustedBakedCoveragePercent)}){Environment.NewLine}" +
                $"静态姿态/需复查/不可信: {StaticPoseCandidates:N0} / {NeedsReviewCandidates:N0} / {UntrustedBakedCandidates:N0}{Environment.NewLine}" +
                LatestBatchDetailText();
        }

        public static LibraryBakeCacheSummary Load(string libraryRoot)
        {
            if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
            {
                return Empty;
            }

            var latestBatchReport = LoadLatestBatchReport(libraryRoot);
            var path = Path.Combine(libraryRoot, "animation_bake_cache_summary.json");
            if (!File.Exists(path))
            {
                return new LibraryBakeCacheSummary(false, "", 0, 0, 0, 0, 0, 0, "", false, 0, 0, 0, 0, 0, 0, 0, "", latestBatchReport);
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
                    ReadInt64(root, "importedAvatarAssetFileCount"),
                    ReadInt64(root, "importedAvatarAssetTrustedFileCount"),
                    ReadInt64(root, "importedAvatarAssetKeyCount"),
                    ReadString(root, "importedAvatarProbeFreshness"),
                    ReadBool(root, "importedAvatarProbeEnforced"),
                    ReadInt64(root, "cachedCandidates"),
                    ReadInt64(root, "trustedBakedCandidates"),
                    ReadInt64(root, "staticPoseCandidates"),
                    ReadInt64(root, "needsReviewCandidates", "uniqueNeedsReviewCandidates"),
                    ReadInt64(root, "untrustedBakedCandidates"),
                    ReadDouble(root, "cacheCoveragePercent"),
                    ReadDouble(root, "trustedBakedCoveragePercent"),
                    ReadString(root, "generatedAt"),
                    latestBatchReport);
            }
            catch
            {
                return new LibraryBakeCacheSummary(false, path, 0, 0, 0, 0, 0, 0, "", false, 0, 0, 0, 0, 0, 0, 0, "", latestBatchReport);
            }
        }

        private string FormatImportedAvatarTrustText()
        {
            var parts = "";
            if (ImportedAvatarTrustedFileCount > 0)
            {
                parts += $"，可信文件 {ImportedAvatarTrustedFileCount:N0}";
            }
            if (!string.IsNullOrWhiteSpace(ImportedAvatarProbeFreshness))
            {
                parts += $"，probe {ImportedAvatarProbeFreshness}";
            }
            if (ImportedAvatarProbeEnforced)
            {
                parts += "，已强制验证";
            }
            return parts;
        }

        private string LatestBatchDetailText()
        {
            if (!LatestBatch.Exists)
            {
                return "";
            }

            return
                $"最近Browser批量烘焙报告: {LatestBatch.ReportPath}{Environment.NewLine}" +
                $"最近Browser批量烘焙: {EmptyAsUnknown(LatestBatch.Label)}，完成 {EmptyAsUnknown(LatestBatch.CompletedAtUtc)}，成功/失败/待处理/已烘焙/缺Avatar {LatestBatch.SuccessCount:N0} / {LatestBatch.FailureCount:N0} / {LatestBatch.PendingCount:N0} / {LatestBatch.SkippedAlreadyBaked:N0} / {LatestBatch.SkippedMissingAvatarOracle:N0}{Environment.NewLine}";
        }

        private static LatestBatchReport LoadLatestBatchReport(string libraryRoot)
        {
            try
            {
                var directory = Path.Combine(libraryRoot, ".as_browser_cache", "unity_bake_batch_reports");
                if (!Directory.Exists(directory))
                {
                    return LatestBatchReport.Empty;
                }

                var reportPath = Directory.GetFiles(directory, "*.json")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (string.IsNullOrWhiteSpace(reportPath))
                {
                    return LatestBatchReport.Empty;
                }

                using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
                var root = document.RootElement;
                return new LatestBatchReport(
                    true,
                    reportPath,
                    ReadString(root, "Label", "label"),
                    ReadString(root, "CompletedAtUtc", "completedAtUtc"),
                    ReadInt64(root, "SuccessCount", "successCount"),
                    ReadInt64(root, "FailureCount", "failureCount"),
                    ReadInt64(root, "PendingCount", "pendingCount"),
                    ReadInt64(root, "SkippedAlreadyBaked", "skippedAlreadyBaked"),
                    ReadInt64(root, "SkippedMissingAvatarOracle", "skippedMissingAvatarOracle"));
            }
            catch
            {
                return LatestBatchReport.Empty;
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
            return ReadString(element, property, null);
        }

        private static string ReadString(JsonElement element, string property, string fallbackProperty)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            {
                if (string.IsNullOrWhiteSpace(fallbackProperty) || !element.TryGetProperty(fallbackProperty, out value))
                {
                    return "";
                }
            }

            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
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

        private static bool ReadBool(JsonElement element, string property)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            {
                return false;
            }

            if (value.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            return value.ValueKind == JsonValueKind.String
                && bool.TryParse(value.GetString(), out var parsed)
                && parsed;
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

        internal sealed class LatestBatchReport
        {
            public static LatestBatchReport Empty { get; } = new(false, "", "", "", 0, 0, 0, 0, 0);

            public LatestBatchReport(
                bool exists,
                string reportPath,
                string label,
                string completedAtUtc,
                long successCount,
                long failureCount,
                long pendingCount,
                long skippedAlreadyBaked,
                long skippedMissingAvatarOracle)
            {
                Exists = exists;
                ReportPath = reportPath ?? "";
                Label = label ?? "";
                CompletedAtUtc = completedAtUtc ?? "";
                SuccessCount = successCount;
                FailureCount = failureCount;
                PendingCount = pendingCount;
                SkippedAlreadyBaked = skippedAlreadyBaked;
                SkippedMissingAvatarOracle = skippedMissingAvatarOracle;
            }

            public bool Exists { get; }
            public string ReportPath { get; }
            public string Label { get; }
            public string CompletedAtUtc { get; }
            public long SuccessCount { get; }
            public long FailureCount { get; }
            public long PendingCount { get; }
            public long SkippedAlreadyBaked { get; }
            public long SkippedMissingAvatarOracle { get; }
        }
    }
}
