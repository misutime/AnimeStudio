using System;
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
}
