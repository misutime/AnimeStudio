using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;

namespace AnimeStudio.CLI
{
    internal static class ProfileLogger
    {
        private static readonly object LockObject = new object();
        private static string _path;

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
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            File.WriteAllText(_path, string.Empty);
            Event("profile_start", new Dictionary<string, object>
            {
                ["outputRoot"] = outputRoot,
                ["profileLog"] = _path,
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
            }
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
    }
}
