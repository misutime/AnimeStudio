using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace AnimeStudio.LibraryBrowser
{
    internal sealed class LibraryBrowserSettings
    {
        private const string LocalSettingsRelativePath = ".as_browser_cache\\unity_bake_settings.json";

        public string UnityProject { get; init; }
        public string UnityEditor { get; init; }
        public IReadOnlyDictionary<string, string> UnityAvatarAssets { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string GlobalSettingsPath
        {
            get
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appData, "AnimeStudio", "LibraryBrowser", "settings.json");
            }
        }

        public static string LocalSettingsPath(string root)
        {
            return string.IsNullOrWhiteSpace(root)
                ? LocalSettingsRelativePath
                : Path.Combine(root, LocalSettingsRelativePath);
        }

        public static LibraryBrowserSettings LoadEffective(string root)
        {
            var local = LoadFromFile(LocalSettingsPath(root));
            var global = LoadGlobal();

            var unityProject = FirstNotEmpty(
                local?.UnityProject,
                global?.UnityProject,
                FindDefaultUnityBakeProject(),
                Environment.GetEnvironmentVariable("ANIMESTUDIO_UNITY_BAKE_PROJECT"));
            var unityEditor = NormalizeUnityEditorPath(FirstNotEmpty(
                local?.UnityEditor,
                global?.UnityEditor,
                FindDefaultUnityEditor(),
                Environment.GetEnvironmentVariable("ANIMESTUDIO_UNITY_EDITOR")));

            return new LibraryBrowserSettings
            {
                UnityProject = unityProject,
                UnityEditor = unityEditor,
                UnityAvatarAssets = MergeAvatarAssets(
                    DiscoverImportedAvatarAssets(unityProject),
                    global?.UnityAvatarAssets,
                    local?.UnityAvatarAssets),
            };
        }

        public static LibraryBrowserSettings LoadGlobal()
        {
            return LoadFromFile(GlobalSettingsPath) ?? new LibraryBrowserSettings();
        }

        public static void SaveGlobal(LibraryBrowserSettings settings)
        {
            var path = GlobalSettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var node = new JsonObject
            {
                ["unityProject"] = settings?.UnityProject ?? string.Empty,
                ["unityEditor"] = NormalizeUnityEditorPath(settings?.UnityEditor) ?? string.Empty,
            };
            WriteStringMap(node, "unityAvatarAssets", settings?.UnityAvatarAssets);
            File.WriteAllText(path, node.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
            }));
        }

        public string ResolveUnityAvatarAsset(params string[] modelKeys)
        {
            return ResolveUnityAvatarAssetDetails(modelKeys).AssetPath;
        }

        public UnityAvatarAssetResolution ResolveUnityAvatarAssetDetails(params string[] modelKeys)
        {
            if (UnityAvatarAssets == null || UnityAvatarAssets.Count == 0)
            {
                return new UnityAvatarAssetResolution(null, null);
            }

            foreach (var key in BuildLookupKeys(modelKeys))
            {
                if (UnityAvatarAssets.TryGetValue(key, out var value))
                {
                    return new UnityAvatarAssetResolution(NormalizeUnityAssetPath(value), key);
                }
            }

            return new UnityAvatarAssetResolution(null, null);
        }

        public string ValidateUnityBake()
        {
            var localPath = LocalSettingsPath(null);
            var globalPath = GlobalSettingsPath;
            if (string.IsNullOrWhiteSpace(UnityProject) || !Directory.Exists(UnityProject))
            {
                return "需要配置 UnityBakeProject。Unity Editor 只是 Unity.exe，UnityBakeProject 是用于执行 AnimeStudio.UnityBake helper 的 Unity 工程目录。请点击 LibraryBrowser 顶部的“Unity设置”同时配置 Editor 和 Bake Project，或在全局配置 "
                    + globalPath
                    + " 写入 unityProject，或在素材库根目录写入 "
                    + localPath
                    + " 覆盖全局配置。";
            }

            var assetsPath = Path.Combine(UnityProject, "Assets");
            var helperPath = Path.Combine(assetsPath, "AnimeStudio.UnityBake", "Editor", "AnimeStudioBakeCli.cs");
            if (!Directory.Exists(assetsPath) || !File.Exists(helperPath))
            {
                return "UnityBakeProject 里没有安装 AnimeStudio.UnityBake helper。请把仓库里的 AnimeStudio.UnityBake\\Assets\\AnimeStudio.UnityBake 复制到 Bake 工程的 Assets 目录下。当前期望入口脚本："
                    + helperPath;
            }

            if (string.IsNullOrWhiteSpace(UnityEditor) || !File.Exists(UnityEditor))
            {
                return "需要配置 Unity Editor。请点击 LibraryBrowser 顶部的“Unity设置”选择 Unity.exe，或在全局配置 "
                    + globalPath
                    + " 写入 unityEditor，或在素材库根目录写入 "
                    + localPath
                    + " 覆盖全局配置。Unity Hub 默认安装目录会自动检测。";
            }

            return null;
        }

        public static string NormalizeUnityEditorPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var path = value.Trim().Trim('"');
            if (File.Exists(path))
            {
                return path;
            }

            if (Directory.Exists(path))
            {
                var direct = Path.Combine(path, "Unity.exe");
                if (File.Exists(direct))
                {
                    return direct;
                }

                var editor = Path.Combine(path, "Editor", "Unity.exe");
                if (File.Exists(editor))
                {
                    return editor;
                }
            }

            return path;
        }

        public static string FindDefaultUnityEditor()
        {
            var hubRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Unity",
                "Hub",
                "Editor");

            if (!Directory.Exists(hubRoot))
            {
                return null;
            }

            var exact = Path.Combine(hubRoot, "6000.4.5f1", "Editor", "Unity.exe");
            if (File.Exists(exact))
            {
                return exact;
            }

            return Directory.EnumerateDirectories(hubRoot)
                .Select(dir => new
                {
                    Path = Path.Combine(dir, "Editor", "Unity.exe"),
                    Name = Path.GetFileName(dir),
                    Modified = Directory.GetLastWriteTimeUtc(dir),
                })
                .Where(x => File.Exists(x.Path))
                .OrderByDescending(x => ParseVersionWeight(x.Name))
                .ThenByDescending(x => x.Modified)
                .Select(x => x.Path)
                .FirstOrDefault();
        }

        private static string FindDefaultUnityBakeProject()
        {
            var candidates = new[]
            {
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AnimeStudioUnityProject"),
                @"D:\misutime\AnimeStudioUnityProject",
            };

            return candidates
                .Where(IsValidUnityBakeProject)
                .FirstOrDefault();
        }

        private static bool IsValidUnityBakeProject(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return false;
            }

            return File.Exists(Path.Combine(path, "ProjectSettings", "ProjectSettings.asset"))
                && File.Exists(Path.Combine(path, "Assets", "AnimeStudio.UnityBake", "Editor", "AnimeStudioBakeCli.cs"));
        }

        private static LibraryBrowserSettings LoadFromFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                return new LibraryBrowserSettings
                {
                    UnityProject = ReadNodeString(node, "unityProject"),
                    UnityEditor = NormalizeUnityEditorPath(ReadNodeString(node, "unityEditor")),
                    UnityAvatarAssets = ReadStringMap(node, "unityAvatarAssets"),
                };
            }
            catch
            {
                return null;
            }
        }

        private static string ReadNodeString(JsonObject node, string name)
        {
            return node != null && node.TryGetPropertyValue(name, out var value)
                ? value?.GetValue<string>()
                : null;
        }

        private static string FirstNotEmpty(params string[] values)
        {
            return values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        }

        private string NormalizeUnityAssetPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var path = value.Trim().Trim('"').Replace('\\', '/');
            if (path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            if (Path.IsPathRooted(value) && !string.IsNullOrWhiteSpace(UnityProject))
            {
                var assetsRoot = Path.GetFullPath(Path.Combine(UnityProject, "Assets"));
                var fullPath = Path.GetFullPath(value);
                if (fullPath.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase))
                {
                    var relative = Path.GetRelativePath(UnityProject, fullPath).Replace('\\', '/');
                    return relative.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ? relative : null;
                }
            }

            return path;
        }

        private static IEnumerable<string> BuildLookupKeys(params string[] values)
        {
            return values
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .SelectMany(x => new[]
                {
                    x,
                    Path.GetFileNameWithoutExtension(x),
                    Path.GetFileName(x),
                })
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static IReadOnlyDictionary<string, string> DiscoverImportedAvatarAssets(string unityProject)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(unityProject))
            {
                return result;
            }

            var directory = Path.Combine(unityProject, "Assets", "AnimeStudioBake", "ImportedAvatar");
            if (!Directory.Exists(directory))
            {
                return result;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*.asset", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var unityPath = Path.GetRelativePath(unityProject, path).Replace('\\', '/');
                if (!unityPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 这些 key 都来自真实导入的 Avatar asset 文件名，只做查找便利，不生成模型-动画关系。
                result[name] = unityPath;
                if (name.EndsWith("_ModelAvatar", StringComparison.OrdinalIgnoreCase))
                {
                    result[name[..^"_ModelAvatar".Length]] = unityPath;
                }
            }

            return result;
        }

        private static IReadOnlyDictionary<string, string> MergeAvatarAssets(
            params IReadOnlyDictionary<string, string>[] maps)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var map in maps)
            {
                AddStringMap(result, map);
            }
            return result;
        }

        private static void AddStringMap(Dictionary<string, string> target, IReadOnlyDictionary<string, string> source)
        {
            if (source == null)
            {
                return;
            }

            foreach (var pair in source)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                {
                    target[pair.Key.Trim()] = pair.Value.Trim();
                }
            }
        }

        private static IReadOnlyDictionary<string, string> ReadStringMap(JsonObject node, string name)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (node == null
                || !node.TryGetPropertyValue(name, out var value)
                || value is not JsonObject obj)
            {
                return result;
            }

            foreach (var pair in obj)
            {
                var text = pair.Value?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(text))
                {
                    result[pair.Key.Trim()] = text.Trim();
                }
            }

            return result;
        }

        private static void WriteStringMap(JsonObject node, string name, IReadOnlyDictionary<string, string> values)
        {
            if (values == null || values.Count == 0)
            {
                return;
            }

            var obj = new JsonObject();
            foreach (var pair in values.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                {
                    obj[pair.Key] = pair.Value;
                }
            }

            if (obj.Count > 0)
            {
                node[name] = obj;
            }
        }

        private static Version ParseVersionWeight(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new Version(0, 0);
            }

            var parts = value.Split('f')[0].Split('.');
            var numbers = parts
                .Select(part => int.TryParse(part, out var number) ? number : 0)
                .ToArray();
            return numbers.Length switch
            {
                >= 4 => new Version(numbers[0], numbers[1], numbers[2], numbers[3]),
                3 => new Version(numbers[0], numbers[1], numbers[2]),
                2 => new Version(numbers[0], numbers[1]),
                1 => new Version(numbers[0], 0),
                _ => new Version(0, 0),
            };
        }
    }

    internal sealed record UnityAvatarAssetResolution(string AssetPath, string MatchKey);
}
