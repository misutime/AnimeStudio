using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AnimeStudio.UnityBake
{
    public static class AnimeStudioUnityBakeAcceleratedWorker
    {
        private static string queueDirectory;
        private static string heartbeatPath;
        private static string stopRequestPath;
        private static DateTime lastPollUtc;
        private static bool processing;

        public static void Run()
        {
            queueDirectory = AnimeStudioBakeCli.GetArgumentValue("-animeStudioBakeAcceleratedQueue");
            if (string.IsNullOrWhiteSpace(queueDirectory))
            {
                Debug.LogError("Missing -animeStudioBakeAcceleratedQueue.");
                EditorApplication.Exit(1);
                return;
            }

            Directory.CreateDirectory(queueDirectory);
            heartbeatPath = Path.Combine(queueDirectory, "accelerated_worker_heartbeat.json");
            stopRequestPath = Path.Combine(queueDirectory, "accelerated_worker_stop.request");
            SafeDelete(stopRequestPath);
            WriteHeartbeat("ready");
            Debug.Log("AnimeStudio UnityBakeAccelerated worker started: " + queueDirectory);
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        public static void RunOnce()
        {
            var requestPath = AnimeStudioBakeCli.GetArgumentValue("-animeStudioAcceleratedBakeRequest");
            var result = RunRequest(requestPath);
            if (!string.Equals(result.status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogError(result.message);
                EditorApplication.Exit(1);
                return;
            }

            EditorApplication.Exit(0);
        }

        public static void SmokeTest()
        {
            if (!AnimeStudioUnityBakeAcceleratedSolver.CanRun(out var message))
            {
                Debug.LogError(message);
                EditorApplication.Exit(1);
                return;
            }

            Debug.Log(message);
            Debug.Log("UnityBakeAccelerated worker smoke test passed.");
            EditorApplication.Exit(0);
        }

        internal static UnityBakeAcceleratedResult RunRequest(string requestPath)
        {
            UnityBakeAcceleratedRequest request = null;
            try
            {
                if (string.IsNullOrWhiteSpace(requestPath) || !File.Exists(requestPath))
                {
                    throw new FileNotFoundException("Missing accelerated request file.", requestPath);
                }

                request = JsonUtility.FromJson<UnityBakeAcceleratedRequest>(File.ReadAllText(requestPath));
                var result = AnimeStudioUnityBakeAcceleratedSolver.Solve(request);
                WriteResult(request, result);
                return result;
            }
            catch (Exception ex)
            {
                var result = new UnityBakeAcceleratedResult
                {
                    status = "error",
                    message = ex.GetType().Name + ": " + ex.Message,
                    unityVersion = Application.unityVersion,
                    writesLibraryIndex = false,
                    writesModelAnimations = false,
                };
                WriteResult(request, result, requestPath);
                return result;
            }
        }

        private static void Tick()
        {
            if (processing)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if ((now - lastPollUtc).TotalMilliseconds < 200)
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
                    EditorApplication.Exit(0);
                    return;
                }

                var job = Directory.EnumerateFiles(queueDirectory, "*.accelerated.request.json", SearchOption.TopDirectoryOnly)
                    .OrderBy(File.GetCreationTimeUtc)
                    .FirstOrDefault();
                if (string.IsNullOrWhiteSpace(job))
                {
                    return;
                }

                processing = true;
                ProcessJob(job);
            }
            catch (Exception ex)
            {
                Debug.LogError(ex);
            }
            finally
            {
                processing = false;
                WriteHeartbeat("ready");
            }
        }

        private static void ProcessJob(string jobPath)
        {
            var activePath = jobPath.Replace(".accelerated.request.json", ".accelerated.active.json");
            var donePath = jobPath.Replace(".accelerated.request.json", ".accelerated.done.json");
            var errorPath = jobPath.Replace(".accelerated.request.json", ".accelerated.error.json");
            SafeDelete(donePath);
            SafeDelete(errorPath);
            File.Move(jobPath, activePath);
            WriteHeartbeat("processing");

            var result = RunRequest(activePath);
            var statusPath = string.Equals(result.status, "ok", StringComparison.OrdinalIgnoreCase)
                ? donePath
                : errorPath;
            File.WriteAllText(statusPath, JsonUtility.ToJson(result, true));
            SafeDelete(activePath);
        }

        private static void WriteResult(UnityBakeAcceleratedRequest request, UnityBakeAcceleratedResult result, string fallbackRequestPath = null)
        {
            var output = request != null && !string.IsNullOrWhiteSpace(request.outputJson)
                ? request.outputJson
                : Path.Combine(Path.GetDirectoryName(fallbackRequestPath ?? string.Empty) ?? Directory.GetCurrentDirectory(), "unity_bake_accelerated_result.json");
            var directory = Path.GetDirectoryName(output);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(output, JsonUtility.ToJson(result, true));
            Debug.Log("AnimeStudio UnityBakeAccelerated result: " + output);
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
                    helperMarker = AnimeStudioUnityBakeAcceleratedSolver.HelperMarker,
                }, true));
            }
            catch
            {
                // 心跳只用于外部进程判断 worker 是否可用。
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
                // 临时文件清理失败不应盖住真正的求解结果。
            }
        }

        [Serializable]
        private sealed class WorkerHeartbeat
        {
            public string state;
            public string updatedAtUtc;
            public string unityVersion;
            public string helperMarker;
        }
    }
}
