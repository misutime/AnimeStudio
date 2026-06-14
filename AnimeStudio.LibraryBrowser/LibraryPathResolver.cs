using System;
using System.IO;

namespace AnimeStudio.LibraryBrowser
{
    internal static class LibraryPathResolver
    {
        private static readonly string[] AssetRoots =
        {
            "Models",
            "Animations",
            "Textures",
            "VFX",
        };

        public static string ResolveExistingFileOrDirectory(string libraryRoot, string path)
        {
            return ResolveExistingPath(libraryRoot, path, expectDirectory: null);
        }

        public static string ResolveExistingFile(string libraryRoot, string path)
        {
            return ResolveExistingPath(libraryRoot, path, expectDirectory: false);
        }

        public static string ResolveExistingDirectory(string libraryRoot, string path)
        {
            return ResolveExistingPath(libraryRoot, path, expectDirectory: true);
        }

        private static string ResolveExistingPath(string libraryRoot, string path, bool? expectDirectory)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            if (!Path.IsPathRooted(path) && !string.IsNullOrWhiteSpace(libraryRoot))
            {
                var fromRoot = TryFullPath(Path.Combine(libraryRoot, path));
                if (ExistsAsExpected(fromRoot, expectDirectory))
                {
                    return fromRoot;
                }

                var relocatedFromRoot = RelocateFromKnownAssetRoot(libraryRoot, path);
                if (ExistsAsExpected(relocatedFromRoot, expectDirectory))
                {
                    return relocatedFromRoot;
                }

                return path;
            }

            var direct = TryFullPath(path);
            if (ExistsAsExpected(direct, expectDirectory))
            {
                return direct;
            }

            var relocated = RelocateFromKnownAssetRoot(libraryRoot, path);
            if (ExistsAsExpected(relocated, expectDirectory))
            {
                return relocated;
            }

            return path;
        }

        private static string RelocateFromKnownAssetRoot(string libraryRoot, string path)
        {
            if (string.IsNullOrWhiteSpace(libraryRoot) || string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            foreach (var assetRoot in AssetRoots)
            {
                var marker = Path.DirectorySeparatorChar + assetRoot + Path.DirectorySeparatorChar;
                var index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    continue;
                }

                var relative = normalized[(index + 1)..];
                return Path.Combine(libraryRoot, relative);
            }

            return path;
        }

        private static bool ExistsAsExpected(string path, bool? expectDirectory)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            return expectDirectory switch
            {
                true => Directory.Exists(path),
                false => File.Exists(path),
                _ => File.Exists(path) || Directory.Exists(path),
            };
        }

        private static string TryFullPath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }
    }
}
