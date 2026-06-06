using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace AnimeStudio.CLI
{
    internal static class ProfileLogger
    {
        private static readonly object LockObject = new object();
        private static string _path;
        private static string _summaryPath;
        private static readonly Dictionary<string, StageStats> StageSummaries = new Dictionary<string, StageStats>(StringComparer.OrdinalIgnoreCase);
        private static int _summaryDirtyCount;
        private static bool _processExitRegistered;

        public static bool Enabled => !string.IsNullOrWhiteSpace(_path);

        public static void Initialize(string outputRoot, string profileLog)
        {
            if (string.Equals(profileLog, "off", StringComparison.OrdinalIgnoreCase))
            {
                _path = null;
                return;
            }

            _path = Path.IsPathRooted(profileLog)
                ? profileLog
                : Path.Combine(outputRoot, profileLog);
            _summaryPath = Path.Combine(Path.GetDirectoryName(_path) ?? outputRoot, "profile_summary.json");
            StageSummaries.Clear();
            _summaryDirtyCount = 0;
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            File.WriteAllText(_path, string.Empty);
            File.WriteAllText(_summaryPath, string.Empty);
            if (!_processExitRegistered)
            {
                AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushSummary();
                _processExitRegistered = true;
            }
            Event("profile_start", new Dictionary<string, object>
            {
                ["outputRoot"] = outputRoot,
                ["profileLog"] = _path,
                ["profileSummary"] = _summaryPath,
            });
        }

        public static IDisposable Measure(string stage, IDictionary<string, object> data = null)
        {
            return Enabled ? new ProfileScope(stage, data) : NullScope.Instance;
        }

        public static void Event(string stage, IDictionary<string, object> data = null)
        {
            if (!Enabled)
            {
                return;
            }

            var entry = CreateEntry(stage, data);
            Write(entry);
        }

        private static Dictionary<string, object> CreateEntry(string stage, IDictionary<string, object> data)
        {
            var process = Process.GetCurrentProcess();
            var entry = new Dictionary<string, object>
            {
                ["timestamp"] = DateTime.UtcNow.ToString("O"),
                ["stage"] = stage,
                ["workingSetBytes"] = process.WorkingSet64,
                ["privateMemoryBytes"] = process.PrivateMemorySize64,
                ["managedMemoryBytes"] = GC.GetTotalMemory(false),
                ["gen0"] = GC.CollectionCount(0),
                ["gen1"] = GC.CollectionCount(1),
                ["gen2"] = GC.CollectionCount(2),
            };
            if (data != null)
            {
                foreach (var pair in data)
                {
                    entry[pair.Key] = pair.Value;
                }
            }
            return entry;
        }

        private static void Write(Dictionary<string, object> entry)
        {
            var json = JsonConvert.SerializeObject(entry);
            lock (LockObject)
            {
                File.AppendAllText(_path, json + Environment.NewLine);
                UpdateSummary(entry);
            }
        }

        private static void UpdateSummary(Dictionary<string, object> entry)
        {
            if (string.IsNullOrWhiteSpace(_summaryPath) || !entry.TryGetValue("elapsedMs", out var elapsedValue))
            {
                return;
            }

            var stage = entry.TryGetValue("stage", out var stageValue)
                ? stageValue?.ToString()
                : null;
            if (string.IsNullOrEmpty(stage))
            {
                return;
            }

            var elapsedMs = Convert.ToDouble(elapsedValue);
            if (!StageSummaries.TryGetValue(stage, out var stats))
            {
                stats = new StageStats();
                StageSummaries[stage] = stats;
            }

            stats.Count++;
            stats.TotalElapsedMs += elapsedMs;
            stats.MaxElapsedMs = Math.Max(stats.MaxElapsedMs, elapsedMs);
            stats.PeakWorkingSetBytes = Math.Max(stats.PeakWorkingSetBytes, GetLong(entry, "workingSetBytes"));
            stats.PeakPrivateMemoryBytes = Math.Max(stats.PeakPrivateMemoryBytes, GetLong(entry, "privateMemoryBytes"));
            stats.PeakManagedMemoryBytes = Math.Max(stats.PeakManagedMemoryBytes, GetLong(entry, "managedMemoryBytes"));

            _summaryDirtyCount++;
            if (_summaryDirtyCount >= 100)
            {
                FlushSummaryNoLock();
            }
        }

        public static void FlushSummary()
        {
            lock (LockObject)
            {
                FlushSummaryNoLock();
            }
        }

        private static void FlushSummaryNoLock()
        {
            if (string.IsNullOrWhiteSpace(_summaryPath))
            {
                return;
            }

            var summary = new Dictionary<string, object>
            {
                ["generatedUtc"] = DateTime.UtcNow.ToString("O"),
                ["profileLog"] = _path,
                ["stages"] = StageSummaries
                    .OrderByDescending(x => x.Value.TotalElapsedMs)
                    .ToDictionary(
                        x => x.Key,
                        x => new Dictionary<string, object>
                        {
                            ["count"] = x.Value.Count,
                            ["totalElapsedMs"] = x.Value.TotalElapsedMs,
                            ["averageElapsedMs"] = x.Value.Count > 0 ? x.Value.TotalElapsedMs / x.Value.Count : 0,
                            ["maxElapsedMs"] = x.Value.MaxElapsedMs,
                            ["peakWorkingSetBytes"] = x.Value.PeakWorkingSetBytes,
                            ["peakPrivateMemoryBytes"] = x.Value.PeakPrivateMemoryBytes,
                            ["peakManagedMemoryBytes"] = x.Value.PeakManagedMemoryBytes,
                        }),
            };
            File.WriteAllText(_summaryPath, JsonConvert.SerializeObject(summary, Formatting.Indented));
            _summaryDirtyCount = 0;
        }

        private static long GetLong(Dictionary<string, object> entry, string key)
        {
            return entry.TryGetValue(key, out var value) ? Convert.ToInt64(value) : 0;
        }

        private sealed class ProfileScope : IDisposable
        {
            private readonly string _stage;
            private readonly IDictionary<string, object> _data;
            private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

            public ProfileScope(string stage, IDictionary<string, object> data)
            {
                _stage = stage;
                _data = data;
            }

            public void Dispose()
            {
                _stopwatch.Stop();
                var data = _data == null
                    ? new Dictionary<string, object>()
                    : new Dictionary<string, object>(_data);
                data["elapsedMs"] = _stopwatch.Elapsed.TotalMilliseconds;
                Event(_stage, data);
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new NullScope();
            public void Dispose() { }
        }

        private sealed class StageStats
        {
            public long Count;
            public double TotalElapsedMs;
            public double MaxElapsedMs;
            public long PeakWorkingSetBytes;
            public long PeakPrivateMemoryBytes;
            public long PeakManagedMemoryBytes;
        }
    }
}
