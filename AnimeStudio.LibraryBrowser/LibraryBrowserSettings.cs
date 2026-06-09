using System;
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

            return new LibraryBrowserSettings
            {
                UnityProject = FirstNotEmpty(
                    local?.UnityProject,
                    global?.UnityProject,
                    Environment.GetEnvironmentVariable("ANIMESTUDIO_UNITY_BAKE_PROJECT")),
                UnityEditor = NormalizeUnityEditorPath(FirstNotEmpty(
                    local?.UnityEditor,
                    global?.UnityEditor,
                    FindDefaultUnityEditor(),
                    Environment.GetEnvironmentVariable("ANIMESTUDIO_UNITY_EDITOR"))),
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
            File.WriteAllText(path, node.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
            }));
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
}
