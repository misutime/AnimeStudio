using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace AnimeStudio.CLI
{
    internal static class UnityBakeAcceleratedRunner
    {
        public static bool RunOnce(string requestPath, string unityProject, string unityEditor, string logPath)
        {
            if (!UnityBakeAcceleratedWorkerVerifier.Check(unityProject, unityEditor))
            {
                return false;
            }
            if (string.IsNullOrWhiteSpace(requestPath) || !File.Exists(requestPath))
            {
                Logger.Error("--run_unity_bake_accelerated requires a request JSON file.");
                return false;
            }
            if (string.IsNullOrWhiteSpace(unityEditor) || !File.Exists(unityEditor))
            {
                Logger.Error("--unity_editor is required when running UnityBakeAccelerated.");
                return false;
            }

            logPath = string.IsNullOrWhiteSpace(logPath)
                ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(requestPath)) ?? Directory.GetCurrentDirectory(), "unity_bake_accelerated.log")
                : logPath;

            var args = new[]
            {
                "-batchmode",
                "-quit",
                "-projectPath", Quote(unityProject),
                "-executeMethod", "AnimeStudio.UnityBake.AnimeStudioUnityBakeAcceleratedWorker.RunOnce",
                "-animeStudioAcceleratedBakeRequest", Quote(requestPath),
                "-logFile", Quote(logPath),
            };

            Logger.Info("Running UnityBakeAccelerated request...");
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = unityEditor,
                Arguments = string.Join(" ", args),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(stdout)) Logger.Info(stdout.Trim());
            if (!string.IsNullOrWhiteSpace(stderr)) Logger.Warning(stderr.Trim());
            Logger.Info("UnityBakeAccelerated log: " + logPath);
            if (process.ExitCode != 0)
            {
                Logger.Error("UnityBakeAccelerated failed with exit code " + process.ExitCode + ".");
                return false;
            }

            return true;
        }

        public static bool Queue(string requestPath, string unityProject, string unityEditor, string queuePath, string logPath)
        {
            if (!UnityBakeAcceleratedWorkerVerifier.Check(unityProject, unityEditor))
            {
                return false;
            }
            if (string.IsNullOrWhiteSpace(requestPath) || !File.Exists(requestPath))
            {
                Logger.Error("--run_unity_bake_accelerated requires a request JSON file.");
                return false;
            }
            if (string.IsNullOrWhiteSpace(queuePath))
            {
                Logger.Error("--unity_bake_accelerated_worker_queue is required for queued UnityBakeAccelerated runs.");
                return false;
            }

            Directory.CreateDirectory(queuePath);
            logPath = string.IsNullOrWhiteSpace(logPath)
                ? Path.Combine(queuePath, "unity_bake_accelerated_worker.log")
                : logPath;

            if (!EnsureWorker(queuePath, unityProject, unityEditor, logPath))
            {
                return false;
            }

            var jobId = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}";
            var pendingPath = Path.Combine(queuePath, jobId + ".accelerated.request.tmp");
            var jobPath = Path.Combine(queuePath, jobId + ".accelerated.request.json");
            var donePath = Path.Combine(queuePath, jobId + ".accelerated.done.json");
            var errorPath = Path.Combine(queuePath, jobId + ".accelerated.error.json");
            File.Copy(requestPath, pendingPath, overwrite: true);
            File.Move(pendingPath, jobPath);
            Logger.Info("Queued UnityBakeAccelerated job: " + jobPath);

            var timeoutAt = DateTime.UtcNow.AddMinutes(10);
            while (DateTime.UtcNow < timeoutAt)
            {
                if (File.Exists(donePath))
                {
                    Logger.Info("UnityBakeAccelerated worker completed job: " + donePath);
                    return true;
                }
                if (File.Exists(errorPath))
                {
                    Logger.Error("UnityBakeAccelerated worker failed job: " + errorPath);
                    return false;
                }

                Thread.Sleep(300);
            }

            Logger.Error("UnityBakeAccelerated worker timed out. Queue: " + queuePath);
            return false;
        }

        private static bool EnsureWorker(string queuePath, string unityProject, string unityEditor, string logPath)
        {
            var heartbeat = Path.Combine(queuePath, "accelerated_worker_heartbeat.json");
            if (IsFresh(heartbeat, TimeSpan.FromSeconds(30)))
            {
                Logger.Info("Using existing UnityBakeAccelerated worker: " + queuePath);
                return true;
            }

            var args = new[]
            {
                "-batchmode",
                "-projectPath", Quote(unityProject),
                "-executeMethod", "AnimeStudio.UnityBake.AnimeStudioUnityBakeAcceleratedWorker.Run",
                "-animeStudioBakeAcceleratedQueue", Quote(queuePath),
                "-logFile", Quote(logPath),
            };

            Logger.Info("Starting UnityBakeAccelerated worker...");
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = unityEditor,
                Arguments = string.Join(" ", args),
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true,
            });

            var timeoutAt = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < timeoutAt)
            {
                if (IsFresh(heartbeat, TimeSpan.FromSeconds(30)))
                {
                    Logger.Info("UnityBakeAccelerated worker is ready: " + queuePath);
                    return true;
                }
                if (process != null && process.HasExited)
                {
                    Logger.Error($"UnityBakeAccelerated worker exited before heartbeat. ExitCode={process.ExitCode}; Log={logPath}");
                    return false;
                }
                Thread.Sleep(1000);
            }

            Logger.Error("UnityBakeAccelerated worker did not become ready. Log: " + logPath);
            return false;
        }

        private static bool IsFresh(string path, TimeSpan maxAge)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) <= maxAge;
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
