using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AnimeStudio.UnityBake
{
    public static class AnimeStudioBakeWorker
    {
        private static string queueDirectory;
        private static string heartbeatPath;
        private static string stopRequestPath;
        private static DateTime lastPollUtc;
        private static bool processing;

        public static void Run()
        {
            queueDirectory = AnimeStudioBakeCli.GetArgumentValue("-animeStudioBakeQueue");
            if (string.IsNullOrWhiteSpace(queueDirectory))
            {
                Debug.LogError("Missing -animeStudioBakeQueue.");
                EditorApplication.Exit(1);
                return;
            }

            Directory.CreateDirectory(queueDirectory);
            heartbeatPath = Path.Combine(queueDirectory, "worker_heartbeat.json");
            stopRequestPath = Path.Combine(queueDirectory, "worker_stop.request");
            SafeDelete(stopRequestPath);
            WriteHeartbeat("ready");
            Debug.Log($"AnimeStudio bake worker started: {queueDirectory}");
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            if (processing)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if ((now - lastPollUtc).TotalMilliseconds < 500)
            {
                return;
            }
            lastPollUtc = now;
            WriteHeartbeat("ready");

            try
            {
                if (File.Exists(stopRequestPath))
                {
                    SafeDelete(stopRequestPath);
                    WriteHeartbeat("stopping");
                    Debug.Log("AnimeStudio bake worker stop requested.");
                    EditorApplication.Exit(0);
                    return;
                }

                var job = Directory.EnumerateFiles(queueDirectory, "*.request.json", SearchOption.TopDirectoryOnly)
                    .OrderBy(File.GetCreationTimeUtc)
                    .FirstOrDefault();
                if (string.IsNullOrWhiteSpace(job))
                {
                    return;
                }

                processing = true;
                ProcessJob(job);
            }
            catch (Exception e)
            {
                Debug.LogError(e);
            }
            finally
            {
                processing = false;
                WriteHeartbeat("ready");
            }
        }

        private static void ProcessJob(string jobPath)
        {
            var activePath = Path.ChangeExtension(jobPath, ".active.json");
            var donePath = Path.ChangeExtension(jobPath, ".done.json");
            var errorPath = Path.ChangeExtension(jobPath, ".error.json");
            SafeDelete(donePath);
            SafeDelete(errorPath);
            File.Move(jobPath, activePath);
            WriteHeartbeat("processing");
            Debug.Log($"AnimeStudio bake worker processing: {activePath}");

            var result = AnimeStudioBakeCli.RunRequest(activePath);
            var statusPath = string.Equals(result.status, "ok", StringComparison.OrdinalIgnoreCase)
                ? donePath
                : errorPath;
            File.WriteAllText(statusPath, JsonUtility.ToJson(result, true));
            SafeDelete(activePath);
            Debug.Log($"AnimeStudio bake worker finished: {statusPath}");
        }

        private static void WriteHeartbeat(string state)
        {
            if (string.IsNullOrWhiteSpace(heartbeatPath))
            {
                return;
            }

            try
            {
                File.WriteAllText(heartbeatPath, JsonUtility.ToJson(new WorkerHeartbeat
                {
                    state = state,
                    updatedAtUtc = DateTime.UtcNow.ToString("O"),
                    unityVersion = Application.unityVersion,
                }, true));
            }
            catch
            {
                // Heartbeat is diagnostic only.
            }
        }

        private static void SafeDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best effort cleanup.
            }
        }

        [Serializable]
        private sealed class WorkerHeartbeat
        {
            public string state;
            public string updatedAtUtc;
            public string unityVersion;
        }
    }
}
