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
        private readonly HashSet<string> _favoriteModelKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _favoriteAnimationKeys = new(StringComparer.OrdinalIgnoreCase);

        public LibraryCurationStore(string root)
        {
            var browserDir = Path.Combine(root, ".animestudio_browser");
            Directory.CreateDirectory(browserDir);
            _path = Path.Combine(browserDir, "curation_marks.jsonl");
            Load();
        }

        public bool IsIgnored(LibraryModelItem item) => _ignoredKeys.Contains(item.StableKey);

        public bool IsFavoriteModel(LibraryModelItem item) => item != null && _favoriteModelKeys.Contains(item.StableKey);

        public bool IsFavoriteAnimation(LibraryAnimationCandidate animation) => _favoriteAnimationKeys.Contains(GetAnimationKey(animation));

        public bool IsFavoriteAnimation(LibraryAnimationUsage usage) => usage != null && IsFavoriteAnimation(usage.Animation);

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

        public void SetFavoriteModel(LibraryModelItem item, bool favorite)
        {
            if (item == null)
            {
                return;
            }

            SetMark(_favoriteModelKeys, item.StableKey, favorite);
            Rewrite();
        }

        public void SetFavoriteAnimation(LibraryAnimationCandidate animation, bool favorite)
        {
            var key = GetAnimationKey(animation);
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            SetMark(_favoriteAnimationKeys, key, favorite);
            Rewrite();
        }

        public void SetFavoriteAnimation(LibraryAnimationUsage usage, bool favorite)
        {
            if (usage != null)
            {
                SetFavoriteAnimation(usage.Animation, favorite);
            }
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
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                switch (action?.ToLowerInvariant())
                {
                    case "ignore":
                        _ignoredKeys.Add(key);
                        break;
                    case "favorite_model":
                        _favoriteModelKeys.Add(key);
                        break;
                    case "favorite_animation":
                        _favoriteAnimationKeys.Add(key);
                        break;
                }
            }
        }

        private void Rewrite()
        {
            using var writer = new StreamWriter(_path, false);
            foreach (var key in _ignoredKeys)
            {
                WriteRow(writer, "ignore", key);
            }
            foreach (var key in _favoriteModelKeys)
            {
                WriteRow(writer, "favorite_model", key);
            }
            foreach (var key in _favoriteAnimationKeys)
            {
                WriteRow(writer, "favorite_animation", key);
            }
        }

        private static void SetMark(HashSet<string> set, string key, bool marked)
        {
            if (marked)
            {
                set.Add(key);
            }
            else
            {
                set.Remove(key);
            }
        }

        private static void WriteRow(StreamWriter writer, string action, string key)
        {
            var row = new
            {
                action,
                key,
                markedAtUtc = DateTimeOffset.UtcNow.ToString("O")
            };
            writer.WriteLine(JsonSerializer.Serialize(row));
        }

        private static string GetAnimationKey(LibraryAnimationCandidate animation)
        {
            if (animation == null)
            {
                return "";
            }

            return !string.IsNullOrWhiteSpace(animation.BestPath)
                ? Path.GetFullPath(animation.BestPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                : animation.Name ?? "";
        }
    }
}
