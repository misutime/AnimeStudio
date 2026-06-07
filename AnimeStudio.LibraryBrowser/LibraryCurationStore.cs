using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AnimeStudio.LibraryBrowser
{
    internal sealed class LibraryCurationStore
    {
        private readonly string _path;
        private readonly HashSet<string> _ignoredKeys = new(StringComparer.OrdinalIgnoreCase);

        public LibraryCurationStore(string root)
        {
            var browserDir = Path.Combine(root, ".animestudio_browser");
            Directory.CreateDirectory(browserDir);
            _path = Path.Combine(browserDir, "curation_marks.jsonl");
            Load();
        }

        public bool IsIgnored(LibraryModelItem item) => _ignoredKeys.Contains(item.StableKey);

        public void SetIgnored(LibraryModelItem item, bool ignored)
        {
            if (ignored)
            {
                _ignoredKeys.Add(item.StableKey);
            }
            else
            {
                _ignoredKeys.Remove(item.StableKey);
            }

            Rewrite();
        }

        private void Load()
        {
            if (!File.Exists(_path))
            {
                return;
            }

            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                var obj = document.RootElement;
                var action = obj.TryGetProperty("action", out var actionProperty) ? actionProperty.GetString() : null;
                var key = obj.TryGetProperty("key", out var keyProperty) ? keyProperty.GetString() : null;
                if (!string.IsNullOrWhiteSpace(key) && string.Equals(action, "ignore", StringComparison.OrdinalIgnoreCase))
                {
                    _ignoredKeys.Add(key);
                }
            }
        }

        private void Rewrite()
        {
            using var writer = new StreamWriter(_path, false);
            foreach (var key in _ignoredKeys)
            {
                var row = new
                {
                    action = "ignore",
                    key,
                    markedAtUtc = DateTimeOffset.UtcNow.ToString("O")
                };
                writer.WriteLine(JsonSerializer.Serialize(row));
            }
        }
    }
}
