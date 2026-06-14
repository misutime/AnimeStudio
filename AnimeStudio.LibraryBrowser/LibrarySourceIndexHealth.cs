using System;
using System.IO;
using System.Text.Json;

namespace AnimeStudio.LibraryBrowser
{
    internal sealed class LibrarySourceIndexHealth
    {
        public static LibrarySourceIndexHealth Empty { get; } = new("missing", false, "", "", "");

        public LibrarySourceIndexHealth(string status, bool staleOverridePairIndex, string note, string reportPath, string sourceRoot)
        {
            Status = string.IsNullOrWhiteSpace(status) ? "unknown" : status;
            StaleOverridePairIndex = staleOverridePairIndex;
            Note = note ?? "";
            ReportPath = reportPath ?? "";
            SourceRoot = sourceRoot ?? "";
        }

        public string Status { get; }
        public bool StaleOverridePairIndex { get; }
        public string Note { get; }
        public string ReportPath { get; }
        public string SourceRoot { get; }
        public bool HasWarning => StaleOverridePairIndex || string.Equals(Status, "warning", StringComparison.OrdinalIgnoreCase);

        public string ShortLabel()
        {
            if (HasWarning)
            {
                return "源索引动画关系: 需重建";
            }

            return string.Equals(Status, "ok", StringComparison.OrdinalIgnoreCase)
                ? "源索引动画关系: ok"
                : "源索引动画关系: 未知";
        }

        public string DetailText()
        {
            var status = string.IsNullOrWhiteSpace(Status) ? "unknown" : Status;
            var text =
                $"源索引动画关系状态: {status}{Environment.NewLine}" +
                $"OverrideController关系过旧: {(StaleOverridePairIndex ? "是" : "否")}{Environment.NewLine}";
            if (StaleOverridePairIndex)
            {
                text += "处理建议: 当前工具会跳过缺少 clipPair 的 OverrideController 动画候选；请先重建 unity_source_index.db，再重建 library_index.db，不要用旧 original/override 分离关系兜底。"
                    + Environment.NewLine;
            }

            if (!string.IsNullOrWhiteSpace(SourceRoot))
            {
                text += $"源索引记录源目录: {SourceRoot}{Environment.NewLine}";
            }

            if (!string.IsNullOrWhiteSpace(Note))
            {
                text += $"源索引提示: {Note}{Environment.NewLine}";
            }

            if (!string.IsNullOrWhiteSpace(ReportPath))
            {
                text += $"源索引报告: {ReportPath}{Environment.NewLine}";
            }

            return text;
        }

        public static LibrarySourceIndexHealth Load(string libraryRoot)
        {
            if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
            {
                return Empty;
            }

            var sqliteSummary = Path.Combine(libraryRoot, "sqlite_index_summary.json");
            if (File.Exists(sqliteSummary) && TryLoadSqliteSummary(sqliteSummary, out var fromSummary))
            {
                return fromSummary;
            }

            var healthReport = Path.Combine(libraryRoot, "unity_source_index.animation_relation_health.json");
            if (File.Exists(healthReport) && TryLoadHealthReport(healthReport, out var fromReport))
            {
                return fromReport;
            }

            return new LibrarySourceIndexHealth(
                "missing",
                false,
                "没有找到源索引动画关系健康报告。生产重建前建议运行 --verify_source_index。",
                "",
                "");
        }

        private static bool TryLoadHealthReport(string path, out LibrarySourceIndexHealth health)
        {
            health = Empty;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                var relationHealth = TryGetObject(root, "animationRelationHealth");
                if (relationHealth.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                var metadata = TryGetObject(root, "metadata");
                health = new LibrarySourceIndexHealth(
                    ReadString(relationHealth, "status"),
                    ReadBool(relationHealth, "staleOverridePairIndex"),
                    ReadString(relationHealth, "note"),
                    path,
                    metadata.ValueKind == JsonValueKind.Object ? ReadString(metadata, "sourceRoot") : "");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryLoadSqliteSummary(string path, out LibrarySourceIndexHealth health)
        {
            health = Empty;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                var relationHealth = TryGetObject(root, "sourceIndexAnimationRelationHealth");
                if (relationHealth.ValueKind != JsonValueKind.Object)
                {
                    var coverage = TryGetObject(root, "animationRelationCoverage");
                    relationHealth = TryGetObject(coverage, "sourceIndexAnimationRelationHealth");
                }
                if (relationHealth.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                health = new LibrarySourceIndexHealth(
                    ReadString(relationHealth, "status"),
                    ReadBool(relationHealth, "staleOverridePairIndex"),
                    ReadString(relationHealth, "note"),
                    path,
                    ReadString(relationHealth, "sourceIndex"));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static JsonElement TryGetObject(JsonElement element, string property)
        {
            return element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.Object
                    ? value
                    : default;
        }

        private static string ReadString(JsonElement element, string property)
        {
            return element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? ""
                    : "";
        }

        private static bool ReadBool(JsonElement element, string property)
        {
            return element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.True;
        }
    }
}
