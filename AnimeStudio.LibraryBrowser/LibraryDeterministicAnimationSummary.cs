using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AnimeStudio.LibraryBrowser
{
    internal sealed class LibraryDeterministicAnimationSummary
    {
        public static LibraryDeterministicAnimationSummary Empty { get; } = new(
            false,
            "",
            "",
            "",
            "",
            0,
            0,
            0,
            0,
            0,
            "");

        public LibraryDeterministicAnimationSummary(
            bool exists,
            string summaryPath,
            string mode,
            string gateStatus,
            string sourceIndexStatus,
            long candidates,
            long explicitCandidates,
            long nonExplicitCandidates,
            long modelsWithExplicitCandidates,
            long animationsWithExplicitCandidates,
            string generatedAt)
        {
            Exists = exists;
            SummaryPath = summaryPath ?? "";
            Mode = mode ?? "";
            GateStatus = gateStatus ?? "";
            SourceIndexStatus = sourceIndexStatus ?? "";
            Candidates = candidates;
            ExplicitCandidates = explicitCandidates;
            NonExplicitCandidates = nonExplicitCandidates;
            ModelsWithExplicitCandidates = modelsWithExplicitCandidates;
            AnimationsWithExplicitCandidates = animationsWithExplicitCandidates;
            GeneratedAt = generatedAt ?? "";
        }

        public bool Exists { get; }
        public string SummaryPath { get; }
        public string Mode { get; }
        public string GateStatus { get; }
        public string SourceIndexStatus { get; }
        public long Candidates { get; }
        public long ExplicitCandidates { get; }
        public long NonExplicitCandidates { get; }
        public long ModelsWithExplicitCandidates { get; }
        public long AnimationsWithExplicitCandidates { get; }
        public string GeneratedAt { get; }

        public string ShortLabel()
        {
            if (!Exists)
            {
                return "动画关系门禁: 未生成";
            }

            if (string.Equals(GateStatus, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return $"动画关系门禁 OK，显式 {ExplicitCandidates:N0}";
            }

            return $"动画关系门禁 {EmptyAsUnknown(GateStatus)}，非显式 {NonExplicitCandidates:N0}";
        }

        public string DetailText()
        {
            if (!Exists)
            {
                return "动画关系门禁: 未生成。可运行 Measure-DeterministicAnimationCoverage.ps1 -GateOnly -FailOnWarning 生成快速门禁报告。" + Environment.NewLine;
            }

            return
                $"动画关系门禁: {SummaryPath}{Environment.NewLine}" +
                $"动画关系门禁时间: {EmptyAsUnknown(GeneratedAt)}{Environment.NewLine}" +
                $"动画关系门禁模式: {EmptyAsUnknown(Mode)}{Environment.NewLine}" +
                $"动画关系门禁状态: {EmptyAsUnknown(GateStatus)}，源索引: {EmptyAsUnknown(SourceIndexStatus)}{Environment.NewLine}" +
                $"默认候选: {Candidates:N0}，显式: {ExplicitCandidates:N0}，非显式: {NonExplicitCandidates:N0}{Environment.NewLine}" +
                $"有显式动画的模型/动画: {ModelsWithExplicitCandidates:N0} / {AnimationsWithExplicitCandidates:N0}{Environment.NewLine}";
        }

        public static LibraryDeterministicAnimationSummary Load(string libraryRoot)
        {
            var path = FindLatestReport(libraryRoot);
            if (string.IsNullOrWhiteSpace(path))
            {
                return Empty;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                var totals = root.TryGetProperty("totals", out var totalsElement) && totalsElement.ValueKind == JsonValueKind.Object
                    ? totalsElement
                    : default;
                var gate = root.TryGetProperty("gate", out var gateElement) && gateElement.ValueKind == JsonValueKind.Object
                    ? gateElement
                    : default;
                var sourceHealth = root.TryGetProperty("sourceIndexAnimationRelationHealth", out var sourceHealthElement)
                    && sourceHealthElement.ValueKind == JsonValueKind.Object
                        ? sourceHealthElement
                        : default;
                return new LibraryDeterministicAnimationSummary(
                    true,
                    path,
                    ReadString(root, "mode"),
                    ReadString(gate, "status"),
                    ReadString(sourceHealth, "status"),
                    ReadInt64(totals, "candidates"),
                    ReadInt64(totals, "explicitCandidates"),
                    ReadInt64(totals, "nonExplicitCandidates"),
                    ReadInt64(totals, "modelsWithExplicitCandidates"),
                    ReadInt64(totals, "animationsWithExplicitCandidates"),
                    ReadString(root, "generatedAt"));
            }
            catch
            {
                return Empty;
            }
        }

        private static string FindLatestReport(string libraryRoot)
        {
            if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
            {
                return "";
            }

            var direct = Path.Combine(libraryRoot, "deterministic_animation_coverage.json");
            var candidates = Directory.EnumerateDirectories(libraryRoot, "AnimationRelationDiagnostics*", SearchOption.TopDirectoryOnly)
                .Select(x => Path.Combine(x, "deterministic_animation_coverage.json"))
                .Concat(new[] { direct })
                .Where(File.Exists)
                .Select(x => new FileInfo(x))
                .OrderByDescending(x => x.LastWriteTimeUtc)
                .ToArray();
            return candidates.Length == 0 ? "" : candidates[0].FullName;
        }

        private static string EmptyAsUnknown(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
        }

        private static string ReadString(JsonElement element, string property)
        {
            return element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? ""
                    : "";
        }

        private static long ReadInt64(JsonElement element, string property)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            {
                return 0;
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
    }
}
