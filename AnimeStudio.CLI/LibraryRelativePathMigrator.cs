using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AnimeStudio.CLI
{
    internal static class LibraryRelativePathMigrator
    {
        private static readonly string[] KnownAssetRoots =
        {
            "Models",
            "Animations",
            "Textures",
            "Materials",
            "VFX",
            "Audio",
            "Previews",
            "BakedPreview",
            ".diagnostics",
        };

        private static readonly HashSet<string> PathFieldNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "output",
            "outputPath",
            "assetPath",
            "modelPreview",
            "animationAsset",
            "catalog",
            "compactIndex",
            "skeletonIndex",
            "animationBindings",
            "relationGraph",
            "relationSummary",
            "gltf",
            "sourceGltf",
            "outputGltf",
            "request",
            "result",
            "sourceRequest",
            "sourceResult",
            "path",
        };

        private static readonly string[] JsonFileNames =
        {
            "asset_summary.json",
            "export_run_summary.json",
            "model_validation.json",
            "model_animations.json",
            "model_animations.compact.json",
            "unity_relation_summary.json",
            "character_assemblies.json",
            "skeletons.json",
            "texture_library.json",
            "vfx_library.json",
            "sqlite_index_summary.json",
        };

        private static readonly string[] JsonLineFileNames =
        {
            "asset_catalog.jsonl",
            "animation_bindings.jsonl",
            "unity_relations.jsonl",
            "export_manifest.jsonl",
        };

        public static MigrationSummary Migrate(string libraryRoot, bool rebuildSqlite = true)
        {
            libraryRoot = Path.GetFullPath(libraryRoot);
            if (!Directory.Exists(libraryRoot))
            {
                throw new DirectoryNotFoundException($"Library root not found: {libraryRoot}");
            }

            var summary = new MigrationSummary { LibraryRoot = libraryRoot };
            foreach (var fileName in JsonFileNames)
            {
                MigrateJsonFile(Path.Combine(libraryRoot, fileName), libraryRoot, summary);
            }

            foreach (var fileName in JsonLineFileNames)
            {
                MigrateJsonLinesFile(Path.Combine(libraryRoot, fileName), libraryRoot, summary);
            }

            MigrateVfxJsonFiles(libraryRoot, summary);

            if (rebuildSqlite)
            {
                SQLiteLibraryIndexBuilder.Build(libraryRoot);
                summary.SqliteRebuilt = true;
            }

            WriteSummary(libraryRoot, summary);
            Logger.Info($"Relative path migration finished: {summary.ChangedFiles} file(s), {summary.ChangedValues} path value(s).");
            return summary;
        }

        public static void NormalizeIndexesBeforeSqlite(string libraryRoot)
        {
            Migrate(libraryRoot, rebuildSqlite: false);
        }

        public static string ResolveLibraryPath(string libraryRoot, string value)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(libraryRoot))
            {
                return value;
            }

            if (Path.IsPathRooted(value))
            {
                if (File.Exists(value) || Directory.Exists(value))
                {
                    return value;
                }

                return TryRelocateByKnownRoot(libraryRoot, value) ?? value;
            }

            var candidate = Path.Combine(libraryRoot, value.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                return candidate;
            }

            return value;
        }

        private static void MigrateJsonFile(string path, string libraryRoot, MigrationSummary summary)
        {
            if (!File.Exists(path))
            {
                return;
            }

            JToken token;
            try
            {
                token = JToken.Parse(File.ReadAllText(path));
            }
            catch (JsonException e)
            {
                Logger.Warning($"Skipping invalid JSON during relative path migration: {path}. {e.Message}");
                return;
            }

            var changed = RewriteToken(token, libraryRoot);
            if (changed <= 0)
            {
                summary.ScannedFiles++;
                return;
            }

            File.WriteAllText(path, token.ToString(Formatting.Indented), Encoding.UTF8);
            summary.ScannedFiles++;
            summary.ChangedFiles++;
            summary.ChangedValues += changed;
        }

        private static void MigrateJsonLinesFile(string path, string libraryRoot, MigrationSummary summary)
        {
            if (!File.Exists(path))
            {
                return;
            }

            var changedValues = 0;
            var changedLines = 0;
            var lines = new List<string>();
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    lines.Add(line);
                    continue;
                }

                try
                {
                    var obj = JObject.Parse(line);
                    var changed = RewriteToken(obj, libraryRoot);
                    changedValues += changed;
                    if (changed > 0)
                    {
                        changedLines++;
                    }
                    lines.Add(obj.ToString(Formatting.None));
                }
                catch (JsonException)
                {
                    lines.Add(line);
                }
            }

            summary.ScannedFiles++;
            if (changedValues <= 0)
            {
                return;
            }

            File.WriteAllLines(path, lines, Encoding.UTF8);
            summary.ChangedFiles++;
            summary.ChangedValues += changedValues;
            summary.ChangedJsonLines += changedLines;
        }

        private static void MigrateVfxJsonFiles(string libraryRoot, MigrationSummary summary)
        {
            var vfxRoot = Path.Combine(libraryRoot, "VFX");
            if (!Directory.Exists(vfxRoot))
            {
                return;
            }

            foreach (var path in Directory.EnumerateFiles(vfxRoot, "vfx.json", SearchOption.AllDirectories))
            {
                MigrateJsonFile(path, libraryRoot, summary);
            }
        }

        private static int RewriteToken(JToken token, string libraryRoot)
        {
            var changed = 0;
            if (token is JObject obj)
            {
                foreach (var property in obj.Properties().ToArray())
                {
                    if (property.Value is JValue value && value.Type == JTokenType.String)
                    {
                        var text = (string)value.Value;
                        if (PathFieldNames.Contains(property.Name)
                            && TryMakeLibraryRelative(libraryRoot, text, out var relative))
                        {
                            property.Value = relative;
                            changed++;
                        }
                        continue;
                    }

                    changed += RewriteToken(property.Value, libraryRoot);
                }
            }
            else if (token is JArray array)
            {
                foreach (var item in array)
                {
                    changed += RewriteToken(item, libraryRoot);
                }
            }

            return changed;
        }

        private static bool TryMakeLibraryRelative(string libraryRoot, string value, out string relative)
        {
            relative = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var normalizedValue = value.Replace('\\', '/');
            var normalizedRoot = Path.GetFullPath(libraryRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace('\\', '/');

            if (normalizedValue.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                relative = normalizedValue[(normalizedRoot.Length + 1)..];
                return IsLibraryInternalRelative(relative);
            }

            foreach (var rootName in KnownAssetRoots)
            {
                var marker = "/" + rootName + "/";
                var index = normalizedValue.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    continue;
                }

                var candidate = normalizedValue[(index + 1)..];
                if (!IsLibraryInternalRelative(candidate))
                {
                    continue;
                }

                relative = candidate;
                return true;
            }

            var fileName = Path.GetFileName(normalizedValue);
            if (!string.IsNullOrWhiteSpace(fileName)
                && File.Exists(Path.Combine(libraryRoot, fileName))
                && normalizedValue.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase))
            {
                relative = fileName;
                return true;
            }

            return false;
        }

        private static string TryRelocateByKnownRoot(string libraryRoot, string value)
        {
            var normalizedValue = value.Replace('\\', '/');
            foreach (var rootName in KnownAssetRoots)
            {
                var marker = "/" + rootName + "/";
                var index = normalizedValue.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    continue;
                }

                var relative = normalizedValue[(index + 1)..];
                var candidate = Path.Combine(libraryRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate) || Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static bool IsLibraryInternalRelative(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
            {
                return false;
            }

            if (value.Contains("..", StringComparison.Ordinal))
            {
                return false;
            }

            return KnownAssetRoots.Any(root => value.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                || value is "asset_catalog.jsonl"
                    or "model_animations.json"
                    or "model_animations.compact.json"
                    or "animation_bindings.jsonl"
                    or "unity_relations.jsonl"
                    or "unity_relation_summary.json"
                    or "skeletons.json";
        }

        private static void WriteSummary(string libraryRoot, MigrationSummary summary)
        {
            var path = Path.Combine(libraryRoot, "relative_path_migration_summary.json");
            File.WriteAllText(path, JsonConvert.SerializeObject(summary, Formatting.Indented), Encoding.UTF8);
        }

        internal sealed class MigrationSummary
        {
            public string LibraryRoot { get; set; }
            public int ScannedFiles { get; set; }
            public int ChangedFiles { get; set; }
            public int ChangedValues { get; set; }
            public int ChangedJsonLines { get; set; }
            public bool SqliteRebuilt { get; set; }
        }
    }
}
