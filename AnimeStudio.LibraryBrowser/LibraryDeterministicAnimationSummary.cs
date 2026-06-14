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
            true,
            "",
            "",
            "",
            "",
            "",
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            "",
            0,
            0,
            0,
            "",
            false,
            "",
            "");

        public LibraryDeterministicAnimationSummary(
            bool exists,
            string summaryPath,
            string libraryIndex,
            bool usesDefaultLibraryIndex,
            string mode,
            string gateStatus,
            string sourceIndexStatus,
            string candidateTableSchemaStatus,
            string candidateTableSchemaNote,
            long candidates,
            long explicitCandidates,
            long nonExplicitCandidates,
            long modelsWithExplicitCandidates,
            long animationsWithExplicitCandidates,
            long explicitUnityBakeCandidates,
            long effectiveBakeReadyCandidates,
            double effectiveBakeReadyCoveragePercent,
            long importedAvatarAssetFileCount,
            long importedAvatarAssetKeyCount,
            long importedAvatarAssetBakeReadyCandidates,
            string importedAvatarProbeStatus,
            long importedAvatarProbeTotalAssets,
            long importedAvatarProbeValidHumanAvatars,
            long importedAvatarProbeInvalidAssets,
            string importedAvatarProbeError,
            bool cacheSummaryPresent,
            string sqliteSummaryError,
            string generatedAt)
        {
            Exists = exists;
            SummaryPath = summaryPath ?? "";
            LibraryIndex = libraryIndex ?? "";
            UsesDefaultLibraryIndex = usesDefaultLibraryIndex;
            Mode = mode ?? "";
            GateStatus = gateStatus ?? "";
            SourceIndexStatus = sourceIndexStatus ?? "";
            CandidateTableSchemaStatus = candidateTableSchemaStatus ?? "";
            CandidateTableSchemaNote = candidateTableSchemaNote ?? "";
            Candidates = candidates;
            ExplicitCandidates = explicitCandidates;
            NonExplicitCandidates = nonExplicitCandidates;
            ModelsWithExplicitCandidates = modelsWithExplicitCandidates;
            AnimationsWithExplicitCandidates = animationsWithExplicitCandidates;
            ExplicitUnityBakeCandidates = explicitUnityBakeCandidates;
            EffectiveBakeReadyCandidates = effectiveBakeReadyCandidates;
            EffectiveBakeReadyCoveragePercent = effectiveBakeReadyCoveragePercent;
            ImportedAvatarAssetFileCount = importedAvatarAssetFileCount;
            ImportedAvatarAssetKeyCount = importedAvatarAssetKeyCount;
            ImportedAvatarAssetBakeReadyCandidates = importedAvatarAssetBakeReadyCandidates;
            ImportedAvatarProbeStatus = importedAvatarProbeStatus ?? "";
            ImportedAvatarProbeTotalAssets = importedAvatarProbeTotalAssets;
            ImportedAvatarProbeValidHumanAvatars = importedAvatarProbeValidHumanAvatars;
            ImportedAvatarProbeInvalidAssets = importedAvatarProbeInvalidAssets;
            ImportedAvatarProbeError = importedAvatarProbeError ?? "";
            CacheSummaryPresent = cacheSummaryPresent;
            SqliteSummaryError = sqliteSummaryError ?? "";
            GeneratedAt = generatedAt ?? "";
        }

        public bool Exists { get; }
        public string SummaryPath { get; }
        public string LibraryIndex { get; }
        public bool UsesDefaultLibraryIndex { get; }
        public string Mode { get; }
        public string GateStatus { get; }
        public string SourceIndexStatus { get; }
        public string CandidateTableSchemaStatus { get; }
        public string CandidateTableSchemaNote { get; }
        public long Candidates { get; }
        public long ExplicitCandidates { get; }
        public long NonExplicitCandidates { get; }
        public long ModelsWithExplicitCandidates { get; }
        public long AnimationsWithExplicitCandidates { get; }
        public long ExplicitUnityBakeCandidates { get; }
        public long EffectiveBakeReadyCandidates { get; }
        public double EffectiveBakeReadyCoveragePercent { get; }
        public long ImportedAvatarAssetFileCount { get; }
        public long ImportedAvatarAssetKeyCount { get; }
        public long ImportedAvatarAssetBakeReadyCandidates { get; }
        public string ImportedAvatarProbeStatus { get; }
        public long ImportedAvatarProbeTotalAssets { get; }
        public long ImportedAvatarProbeValidHumanAvatars { get; }
        public long ImportedAvatarProbeInvalidAssets { get; }
        public string ImportedAvatarProbeError { get; }
        public bool CacheSummaryPresent { get; }
        public string SqliteSummaryError { get; }
        public string GeneratedAt { get; }

        public string ShortLabel()
        {
            if (!Exists)
            {
                return "动画关系门禁: 未生成";
            }

            if (!UsesDefaultLibraryIndex)
            {
                return "动画关系门禁: 旁路报告";
            }

            if (IsFastSummary)
            {
                var cacheText = CacheSummaryPresent ? "cache OK" : "cache 缺失";
                var avatarText = ImportedAvatarProbeTotalAssets > 0
                    ? $"Avatar {ImportedAvatarProbeValidHumanAvatars:N0}/{ImportedAvatarProbeTotalAssets:N0}有效"
                    : $"Avatar {ImportedAvatarAssetFileCount:N0}";
                var bakeText = EffectiveBakeReadyCandidates > 0
                    ? $"，oracle {FormatPercent(EffectiveBakeReadyCoveragePercent)}"
                    : "";
                return $"动画快速摘要，{avatarText}，{cacheText}{bakeText}";
            }

            if (string.Equals(GateStatus, "ok", StringComparison.OrdinalIgnoreCase))
            {
                var bakeText = EffectiveBakeReadyCandidates > 0
                    ? $"，oracle {FormatPercent(EffectiveBakeReadyCoveragePercent)}"
                    : "";
                return $"动画关系门禁 OK，显式 {ExplicitCandidates:N0}{bakeText}";
            }

            if (!string.Equals(CandidateTableSchemaStatus, "ok", StringComparison.OrdinalIgnoreCase)
                && NonExplicitCandidates == 0)
            {
                return $"动画关系门禁 {EmptyAsUnknown(GateStatus)}，schema需重建";
            }

            return $"动画关系门禁 {EmptyAsUnknown(GateStatus)}，非显式 {NonExplicitCandidates:N0}";
        }

        public string DetailText()
        {
            if (!Exists)
            {
                return "动画关系门禁: 未生成。可运行 Measure-DeterministicAnimationCoverage.ps1 -GateOnly -FailOnWarning 生成快速门禁报告。" + Environment.NewLine;
            }

            if (IsFastSummary)
            {
                return
                    $"动画快速摘要: {SummaryPath}{Environment.NewLine}" +
                    $"动画快速摘要索引: {EmptyAsUnknown(LibraryIndex)}{FormatIndexScopeNote()}{Environment.NewLine}" +
                    $"动画快速摘要时间: {EmptyAsUnknown(GeneratedAt)}{Environment.NewLine}" +
                    $"根目录bake cache: {(CacheSummaryPresent ? "已读取" : "未读取")}{Environment.NewLine}" +
                    $"导入Avatar asset文件/key: {ImportedAvatarAssetFileCount:N0} / {ImportedAvatarAssetKeyCount:N0}{Environment.NewLine}" +
                    FormatImportedAvatarProbeText() +
                    $"有效Avatar oracle候选: {EffectiveBakeReadyCandidates:N0} ({FormatPercent(EffectiveBakeReadyCoveragePercent)}){Environment.NewLine}" +
                    $"导入Avatar oracle候选: {ImportedAvatarAssetBakeReadyCandidates:N0}{Environment.NewLine}" +
                    FormatFastSummaryWarningText() +
                    "FastSummary 只用于阶段性确认 cache / ImportedAvatar 状态，不替代 GateOnly 或 SummaryOnly 的深度关系门禁。" +
                    Environment.NewLine;
            }

            return
                $"动画关系门禁: {SummaryPath}{Environment.NewLine}" +
                $"动画关系门禁索引: {EmptyAsUnknown(LibraryIndex)}{FormatIndexScopeNote()}{Environment.NewLine}" +
                $"动画关系门禁时间: {EmptyAsUnknown(GeneratedAt)}{Environment.NewLine}" +
                $"动画关系门禁模式: {EmptyAsUnknown(Mode)}{Environment.NewLine}" +
                $"动画关系门禁状态: {EmptyAsUnknown(GateStatus)}，源索引: {EmptyAsUnknown(SourceIndexStatus)}{Environment.NewLine}" +
                $"候选表约束: {EmptyAsUnknown(CandidateTableSchemaStatus)}{FormatSchemaNote()}{Environment.NewLine}" +
                $"默认候选: {Candidates:N0}，显式: {ExplicitCandidates:N0}，非显式: {NonExplicitCandidates:N0}{Environment.NewLine}" +
                $"有显式动画的模型/动画: {ModelsWithExplicitCandidates:N0} / {AnimationsWithExplicitCandidates:N0}{Environment.NewLine}" +
                FormatUnityBakeOracleText();
        }

        public static LibraryDeterministicAnimationSummary Load(string libraryRoot)
        {
            var defaultIndex = string.IsNullOrWhiteSpace(libraryRoot)
                ? ""
                : Path.GetFullPath(Path.Combine(libraryRoot, "library_index.db"));
            var path = FindLatestReport(libraryRoot, defaultIndex);
            if (string.IsNullOrWhiteSpace(path))
            {
                return Empty;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                var mode = ReadString(root, "mode");
                var isFastSummary = string.Equals(mode, "fastSummary", StringComparison.OrdinalIgnoreCase);
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
                var candidateSchema = root.TryGetProperty("candidateTableSchema", out var candidateSchemaElement)
                    && candidateSchemaElement.ValueKind == JsonValueKind.Object
                        ? candidateSchemaElement
                        : default;
                var bake = root.TryGetProperty("unityBakeProduction", out var bakeElement)
                    && bakeElement.ValueKind == JsonValueKind.Object
                        ? bakeElement
                        : default;
                if (isFastSummary
                    && root.TryGetProperty("animationBakeCacheSummary", out var fastBakeElement)
                    && fastBakeElement.ValueKind == JsonValueKind.Object)
                {
                    bake = fastBakeElement;
                }
                var importedAvatarReadiness = bake.ValueKind == JsonValueKind.Object
                    && bake.TryGetProperty("importedAvatarAssetReadiness", out var importedAvatarReadinessElement)
                    && importedAvatarReadinessElement.ValueKind == JsonValueKind.Object
                        ? importedAvatarReadinessElement
                        : default;
                var libraryIndex = ReadString(root, "libraryIndex");
                var usesDefaultIndex = string.IsNullOrWhiteSpace(libraryIndex)
                    || PathsEqual(libraryIndex, defaultIndex);
                return new LibraryDeterministicAnimationSummary(
                    true,
                    path,
                    libraryIndex,
                    usesDefaultIndex,
                    mode,
                    ReadString(gate, "status"),
                    ReadString(sourceHealth, "status"),
                    ReadString(candidateSchema, "status"),
                    ReadString(candidateSchema, "note"),
                    ReadInt64(totals, "candidates"),
                    ReadInt64(totals, "explicitCandidates"),
                    ReadInt64(totals, "nonExplicitCandidates"),
                    ReadInt64(totals, "modelsWithExplicitCandidates"),
                    ReadInt64(totals, "animationsWithExplicitCandidates"),
                    ReadInt64(bake, "explicitUnityBakeCandidates"),
                    ReadInt64(bake, "effectiveBakeReadyExplicitUnityBakeCandidates", "bakeReadyExplicitUnityBakeCandidates"),
                    ReadDouble(bake, "effectiveBakeReadyExplicitUnityBakeCoveragePercent", "bakeReadyExplicitUnityBakeCoveragePercent"),
                    isFastSummary
                        ? ReadInt64(root, "importedAvatarAssetCount")
                        : ReadInt64(importedAvatarReadiness, "fileCount"),
                    ReadInt64(bake, "importedAvatarAssetKeyCount") != 0
                        ? ReadInt64(bake, "importedAvatarAssetKeyCount")
                        : ReadInt64(importedAvatarReadiness, "keyCount"),
                    ReadInt64(bake, "uniqueImportedAvatarAssetBakeReadyExplicitUnityBakeCandidates", "importedAvatarAssetBakeReadyExplicitUnityBakeCandidates"),
                    isFastSummary ? ReadString(root, "importedAvatarProbeStatus") : "",
                    isFastSummary ? ReadInt64(root, "importedAvatarProbeTotalAssets") : 0,
                    isFastSummary ? ReadInt64(root, "importedAvatarProbeValidHumanAvatars") : 0,
                    isFastSummary ? ReadInt64(root, "importedAvatarProbeInvalidAssets") : 0,
                    isFastSummary ? ReadString(root, "importedAvatarProbeError") : "",
                    isFastSummary && ReadBool(root, "cacheSummaryPresent"),
                    ReadString(root, "sqliteSummaryError"),
                    ReadString(root, "generatedAt"));
            }
            catch
            {
                return Empty;
            }
        }

        private static string FindLatestReport(string libraryRoot, string defaultIndex)
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
            if (candidates.Length == 0)
            {
                return "";
            }

            var defaultReport = candidates.FirstOrDefault(x => ReportUsesDefaultIndex(x.FullName, defaultIndex));
            return defaultReport?.FullName ?? candidates[0].FullName;
        }

        private static string EmptyAsUnknown(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
        }

        private string FormatSchemaNote()
        {
            if (string.IsNullOrWhiteSpace(CandidateTableSchemaNote))
            {
                return "";
            }

            return $"，{CandidateTableSchemaNote}";
        }

        private string FormatIndexScopeNote()
        {
            return UsesDefaultLibraryIndex
                ? ""
                : "，旁路报告，不代表当前正式 library_index.db";
        }

        private string FormatUnityBakeOracleText()
        {
            if (ExplicitUnityBakeCandidates <= 0)
            {
                return "";
            }

            return
                $"显式Humanoid/Muscle候选: {ExplicitUnityBakeCandidates:N0}{Environment.NewLine}" +
                $"有效Avatar oracle候选: {EffectiveBakeReadyCandidates:N0} ({FormatPercent(EffectiveBakeReadyCoveragePercent)}){Environment.NewLine}" +
                $"导入Avatar asset文件/key: {ImportedAvatarAssetFileCount:N0} / {ImportedAvatarAssetKeyCount:N0}{Environment.NewLine}" +
                $"导入Avatar oracle候选: {ImportedAvatarAssetBakeReadyCandidates:N0}{Environment.NewLine}";
        }

        private string FormatImportedAvatarProbeText()
        {
            if (ImportedAvatarProbeTotalAssets <= 0 && string.IsNullOrWhiteSpace(ImportedAvatarProbeStatus))
            {
                return "导入Avatar asset有效性: 未验证" + Environment.NewLine;
            }

            var text =
                $"导入Avatar asset有效性: {EmptyAsUnknown(ImportedAvatarProbeStatus)}，有效 {ImportedAvatarProbeValidHumanAvatars:N0}/{ImportedAvatarProbeTotalAssets:N0}，无效 {ImportedAvatarProbeInvalidAssets:N0}{Environment.NewLine}";
            if (!string.IsNullOrWhiteSpace(ImportedAvatarProbeError))
            {
                text += $"导入Avatar asset验证读取错误: {ImportedAvatarProbeError}{Environment.NewLine}";
            }

            return text;
        }

        private bool IsFastSummary => string.Equals(Mode, "fastSummary", StringComparison.OrdinalIgnoreCase);

        private string FormatFastSummaryWarningText()
        {
            return string.IsNullOrWhiteSpace(SqliteSummaryError)
                ? ""
                : $"SQLite摘要读取提示: {SqliteSummaryError}{Environment.NewLine}";
        }

        private static string FormatPercent(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture) + "%";
        }

        private static bool ReportUsesDefaultIndex(string reportPath, string defaultIndex)
        {
            if (string.IsNullOrWhiteSpace(reportPath) || string.IsNullOrWhiteSpace(defaultIndex))
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
                var libraryIndex = ReadString(document.RootElement, "libraryIndex");
                return string.IsNullOrWhiteSpace(libraryIndex)
                    || PathsEqual(libraryIndex, defaultIndex);
            }
            catch
            {
                return false;
            }
        }

        private static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            try
            {
                return string.Equals(
                    Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
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
            return ReadInt64(element, property, null);
        }

        private static long ReadInt64(JsonElement element, string property, string fallbackProperty)
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
            return ReadDouble(element, property, null);
        }

        private static double ReadDouble(JsonElement element, string property, string fallbackProperty)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            {
                if (string.IsNullOrWhiteSpace(fallbackProperty) || !element.TryGetProperty(fallbackProperty, out value))
                {
                    return 0;
                }
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

        private static bool ReadBool(JsonElement element, string property)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            {
                return false;
            }

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(value.GetString(), out var result) && result,
                _ => false,
            };
        }
    }
}
