using System;
using System.IO;

namespace AnimeStudio.CLI
{
    internal static class UnityBakeAcceleratedWorkerVerifier
    {
        private const string RequiredUnityMajor = "6000.";
        private static readonly string[] RequiredFiles =
        {
            "AnimeStudioBakeCli.cs",
            "AnimeStudioUnityBakeAcceleratedModels.cs",
            "AnimeStudioUnityBakeAcceleratedSolver.cs",
            "AnimeStudioUnityBakeAcceleratedWorker.cs",
        };

        public static bool Check(string unityProject, string unityEditor)
        {
            if (string.IsNullOrWhiteSpace(unityProject) || !Directory.Exists(unityProject))
            {
                Logger.Error("--check_unity_bake_accelerated_worker requires --unity_project pointing to AnimeStudioUnityBakeWorker.");
                return false;
            }

            var projectVersion = ReadProjectUnityVersion(unityProject);
            if (string.IsNullOrWhiteSpace(projectVersion))
            {
                Logger.Error("Unity worker ProjectSettings/ProjectVersion.txt is missing or unreadable: " + unityProject);
                return false;
            }
            if (!projectVersion.StartsWith(RequiredUnityMajor, StringComparison.Ordinal))
            {
                Logger.Error($"UnityBakeAccelerated requires Unity 6 / 6000.x worker project. Project version is {projectVersion}.");
                return false;
            }

            if (!string.IsNullOrWhiteSpace(unityEditor))
            {
                if (!File.Exists(unityEditor))
                {
                    Logger.Error("--unity_editor does not exist: " + unityEditor);
                    return false;
                }
                if (!unityEditor.Replace('\\', '/').Contains("/6000.", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Error("--unity_editor must point to a Unity 6 / 6000.x editor for UnityBakeAccelerated: " + unityEditor);
                    return false;
                }
            }

            var helperRoot = Path.Combine(unityProject, "Assets", "AnimeStudio.UnityBake", "Editor");
            foreach (var file in RequiredFiles)
            {
                var path = Path.Combine(helperRoot, file);
                if (!File.Exists(path))
                {
                    Logger.Error("UnityBakeAccelerated helper file is missing: " + path);
                    return false;
                }
            }

            var cliText = File.ReadAllText(Path.Combine(helperRoot, "AnimeStudioBakeCli.cs"));
            var solverText = File.ReadAllText(Path.Combine(helperRoot, "AnimeStudioUnityBakeAcceleratedSolver.cs"));
            var workerText = File.ReadAllText(Path.Combine(helperRoot, "AnimeStudioUnityBakeAcceleratedWorker.cs"));
            if (!cliText.Contains("-animeStudioAcceleratedBakeRequest", StringComparison.Ordinal)
                || !solverText.Contains("UnityBakeAcceleratedWorkerV1", StringComparison.Ordinal)
                || !solverText.Contains("SetInternalHumanPose", StringComparison.Ordinal)
                || !workerText.Contains("-animeStudioBakeAcceleratedQueue", StringComparison.Ordinal)
                || !workerText.Contains("*.accelerated.request.json", StringComparison.Ordinal))
            {
                Logger.Error("UnityBakeAccelerated helper marker check failed. Sync AnimeStudio.UnityBake into the worker project.");
                return false;
            }

            Logger.Info("UnityBakeAccelerated worker project check passed.");
            Logger.Info("Unity project: " + unityProject);
            Logger.Info("Unity project version: " + projectVersion);
            if (!string.IsNullOrWhiteSpace(unityEditor))
            {
                Logger.Info("Unity editor: " + unityEditor);
            }
            Logger.Info("Helper root: " + helperRoot);
            return true;
        }

        private static string ReadProjectUnityVersion(string unityProject)
        {
            var versionPath = Path.Combine(unityProject, "ProjectSettings", "ProjectVersion.txt");
            if (!File.Exists(versionPath))
            {
                return null;
            }

            foreach (var line in File.ReadLines(versionPath))
            {
                const string prefix = "m_EditorVersion:";
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return line[prefix.Length..].Trim();
                }
            }

            return null;
        }
    }
}
