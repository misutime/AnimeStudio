using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AnimeStudio.LibraryBrowser
{
    internal sealed class RecentLibraryStore
    {
        private const int MaxRecentCount = 12;
        private readonly string _settingsPath;

        public RecentLibraryStore()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var settingsDir = Path.Combine(appData, "AnimeStudio", "LibraryBrowser");
            Directory.CreateDirectory(settingsDir);
            _settingsPath = Path.Combine(settingsDir, "recent_libraries.json");
        }

        public IReadOnlyList<string> Load()
        {
            if (!File.Exists(_settingsPath))
            {
                return Array.Empty<string>();
            }

            try
            {
                var paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_settingsPath)) ?? new List<string>();
                return paths
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(NormalizePath)
                    .Where(Directory.Exists)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(MaxRecentCount)
                    .ToList();
            }
            catch
            {
                // 最近列表只是启动便捷入口，文件损坏时直接清空，避免影响主功能。
                return Array.Empty<string>();
            }
        }

        public void Add(string path)
        {
            var normalized = NormalizePath(path);
            var paths = Load()
                .Where(x => !string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase))
                .Prepend(normalized)
                .Take(MaxRecentCount)
                .ToList();

            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(paths, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }

        public void Remove(string path)
        {
            var normalized = NormalizePath(path);
            var paths = Load()
                .Where(x => !string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase))
                .ToList();

            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(paths, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }

        private static string NormalizePath(string path)
        {
            return Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
