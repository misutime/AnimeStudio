using System;
using System.Diagnostics;
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

        public static bool Check(string unityProject, string unityEditor, bool runSmokeTest = false)
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
            else if (runSmokeTest)
            {
                Logger.Error("--unity_editor is required to run the UnityBakeAccelerated smoke test.");
                return false;
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
            if (runSmokeTest && !RunSmokeTest(unityProject, unityEditor))
            {
                return false;
            }
            return true;
        }

        public static bool SyncAndCheck(string unityProject, string unityEditor, bool runSmokeTest = false)
        {
            if (!Sync(unityProject))
            {
                return false;
            }

            return Check(unityProject, unityEditor, runSmokeTest);
        }

        private static bool Sync(string unityProject)
        {
            if (string.IsNullOrWhiteSpace(unityProject) || !Directory.Exists(unityProject))
            {
                Logger.Error("--sync_unity_bake_accelerated_worker requires --unity_project pointing to AnimeStudioUnityBakeWorker.");
                return false;
            }

            var sourceRoot = FindRepoHelperRoot();
            if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            {
                Logger.Error("Unable to find repo helper root: AnimeStudio.UnityBake\\Assets\\AnimeStudio.UnityBake. Run this command from the AnimeStudio repository.");
                return false;
            }

            var targetRoot = Path.Combine(unityProject, "Assets", "AnimeStudio.UnityBake");
            CopyDirectory(sourceRoot, targetRoot);
            Logger.Info("UnityBakeAccelerated helper synced.");
            Logger.Info("Source helper: " + sourceRoot);
            Logger.Info("Target helper: " + targetRoot);
            return true;
        }

        private static string FindRepoHelperRoot()
        {
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                var current = string.IsNullOrWhiteSpace(start) ? null : Path.GetFullPath(start);
                for (var i = 0; i < 8 && !string.IsNullOrWhiteSpace(current); i++)
                {
                    var candidate = Path.Combine(current, "AnimeStudio.UnityBake", "Assets", "AnimeStudio.UnityBake");
                    if (Directory.Exists(candidate))
                    {
                        return candidate;
                    }

                    current = Directory.GetParent(current)?.FullName;
                }
            }

            return null;
        }

        private static void CopyDirectory(string sourceRoot, string targetRoot)
        {
            foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceRoot, directory);
                Directory.CreateDirectory(Path.Combine(targetRoot, relative));
            }

            Directory.CreateDirectory(targetRoot);
            foreach (var sourceFile in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceRoot, sourceFile);
                var targetFile = Path.Combine(targetRoot, relative);
                var targetDirectory = Path.GetDirectoryName(targetFile);
                if (!string.IsNullOrWhiteSpace(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }

                // 只覆盖 helper 文件，不删除 Unity 工程里其他资产，避免误伤用户调试内容。
                File.Copy(sourceFile, targetFile, overwrite: true);
            }
        }

        private static bool RunSmokeTest(string unityProject, string unityEditor)
        {
            // check 命令要真正确认 Unity 侧 helper 能编译和调用 native Humanoid 入口。
            var logDir = Path.Combine(unityProject, "Logs");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "unity_bake_accelerated_smoke.log");
            var args = string.Join(" ", new[]
            {
                "-batchmode",
                "-quit",
                "-projectPath", Quote(unityProject),
                "-executeMethod", "AnimeStudio.UnityBake.AnimeStudioUnityBakeAcceleratedWorker.SmokeTest",
                "-logFile", Quote(logPath),
            });

            Logger.Info("Running UnityBakeAccelerated smoke test...");
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = unityEditor,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (process == null)
            {
                Logger.Error("Unable to start Unity editor for UnityBakeAccelerated smoke test.");
                return false;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(stdout)) Logger.Info(stdout.Trim());
            if (!string.IsNullOrWhiteSpace(stderr)) Logger.Warning(stderr.Trim());
            Logger.Info("UnityBakeAccelerated smoke log: " + logPath);
            if (process.ExitCode != 0)
            {
                Logger.Error("UnityBakeAccelerated smoke test failed with exit code " + process.ExitCode + ".");
                return false;
            }

            Logger.Info("UnityBakeAccelerated smoke test passed.");
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

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
