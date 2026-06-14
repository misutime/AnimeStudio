using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AnimeStudio.UnityBake
{
    public static class AnimeStudioImportedAvatarProbe
    {
        public static void Run(string avatarAssetPath, string outputJson)
        {
            if (string.IsNullOrWhiteSpace(outputJson))
            {
                outputJson = Path.Combine(Directory.GetCurrentDirectory(), "imported_avatar_probe.json");
            }

            var report = Probe(avatarAssetPath);
            var directory = Path.GetDirectoryName(outputJson);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(outputJson, JsonUtility.ToJson(report, true));
            Debug.Log("AnimeStudio imported Avatar probe report: " + outputJson);
            if (!string.Equals(report.status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogError(report.message);
                EditorApplication.Exit(1);
                return;
            }

            EditorApplication.Exit(0);
        }

        public static void RunDirectory(string avatarAssetRoot, string outputJson)
        {
            if (string.IsNullOrWhiteSpace(outputJson))
            {
                outputJson = Path.Combine(Directory.GetCurrentDirectory(), "imported_avatar_probe_batch.json");
            }

            var report = ProbeDirectory(avatarAssetRoot);
            var directory = Path.GetDirectoryName(outputJson);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(outputJson, JsonUtility.ToJson(report, true));
            Debug.Log("AnimeStudio imported Avatar batch probe report: " + outputJson);
            if (!string.Equals(report.status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogError(report.message);
                EditorApplication.Exit(1);
                return;
            }

            EditorApplication.Exit(0);
        }

        private static ImportedAvatarProbeReport Probe(string avatarAssetPath)
        {
            var report = new ImportedAvatarProbeReport
            {
                avatarAssetPath = avatarAssetPath,
            };

            if (string.IsNullOrWhiteSpace(avatarAssetPath))
            {
                report.status = "error";
                report.message = "Avatar asset path is empty.";
                return report;
            }

            if (!avatarAssetPath.Replace('\\', '/').StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                report.status = "error";
                report.message = "Avatar asset path must be a Unity project relative Assets/... path.";
                return report;
            }

            var avatar = AssetDatabase.LoadAssetAtPath<Avatar>(avatarAssetPath);
            if (avatar == null)
            {
                report.status = "error";
                report.message = "AssetDatabase could not load a UnityEngine.Avatar from the path.";
                return report;
            }

            report.avatarName = avatar.name;
            report.isValid = avatar.isValid;
            report.isHuman = avatar.isHuman;
            report.status = avatar.isValid && avatar.isHuman ? "ok" : "error";
            report.message = report.status == "ok"
                ? "Imported Avatar asset is a valid human Avatar."
                : "Imported Avatar asset loaded, but Unity does not consider it a valid human Avatar.";
            return report;
        }

        private static ImportedAvatarProbeBatchReport ProbeDirectory(string avatarAssetRoot)
        {
            var report = new ImportedAvatarProbeBatchReport
            {
                avatarAssetRoot = avatarAssetRoot,
                generatedAt = DateTime.UtcNow.ToString("o"),
            };

            var root = NormalizeAssetPath(avatarAssetRoot);
            if (string.IsNullOrWhiteSpace(root))
            {
                root = "Assets/AnimeStudioBake/ImportedAvatar";
            }

            if (!root.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                report.status = "error";
                report.message = "Avatar asset root must be a Unity project relative Assets/... path.";
                return report;
            }

            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var fullRoot = Path.GetFullPath(Path.Combine(projectRoot, root.Replace('/', Path.DirectorySeparatorChar)));
            if (!Directory.Exists(fullRoot))
            {
                report.status = "error";
                report.message = "Imported Avatar directory does not exist: " + root;
                return report;
            }

            foreach (var file in Directory.GetFiles(fullRoot, "*.asset", SearchOption.AllDirectories))
            {
                var assetPath = ToUnityAssetPath(projectRoot, file);
                var item = Probe(assetPath);
                report.items.Add(item);
                report.totalAssets++;
                if (string.Equals(item.status, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    report.validHumanAvatars++;
                }
                else
                {
                    report.invalidAssets++;
                }
            }

            report.avatarAssetRoot = root;
            report.status = report.invalidAssets == 0 ? "ok" : "error";
            report.message = report.status == "ok"
                ? "All imported Avatar assets are valid human Avatars."
                : "Some imported Avatar assets are missing, invalid, or not human Avatars.";
            return report;
        }

        private static string NormalizeAssetPath(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "" : value.Trim().Replace('\\', '/');
        }

        private static string ToUnityAssetPath(string projectRoot, string file)
        {
            var fullProjectRoot = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullFile = Path.GetFullPath(file);
            if (!fullFile.StartsWith(fullProjectRoot, StringComparison.OrdinalIgnoreCase))
            {
                return fullFile.Replace('\\', '/');
            }

            var relative = fullFile.Substring(fullProjectRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return relative.Replace('\\', '/');
        }
    }

    [Serializable]
    public sealed class ImportedAvatarProbeReport
    {
        public string status;
        public string message;
        public string avatarAssetPath;
        public string avatarName;
        public bool isValid;
        public bool isHuman;
    }

    [Serializable]
    public sealed class ImportedAvatarProbeBatchReport
    {
        public string status;
        public string message;
        public string generatedAt;
        public string avatarAssetRoot;
        public int totalAssets;
        public int validHumanAvatars;
        public int invalidAssets;
        public List<ImportedAvatarProbeReport> items = new List<ImportedAvatarProbeReport>();
    }
}
