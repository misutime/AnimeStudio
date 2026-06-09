using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeStudio.LibraryBrowser
{
    internal sealed class UnityBakeWorkerManager
    {
        private const string WorkerEntryPoint = "AnimeStudio.UnityBake.AnimeStudioBakeWorker.Run";

        public static string GetQueuePath(string libraryRoot)
        {
            return string.IsNullOrWhiteSpace(libraryRoot)
                ? null
                : Path.Combine(libraryRoot, ".as_browser_cache", "unity_bake_worker");
        }

        public static UnityBakeWorkerStatus GetStatus(string libraryRoot)
        {
            var queue = GetQueuePath(libraryRoot);
            if (string.IsNullOrWhiteSpace(queue))
            {
                return new UnityBakeWorkerStatus(false, "未选择素材库", null, null, null, null);
            }

            var heartbeat = Path.Combine(queue, "worker_heartbeat.json");
            if (!File.Exists(heartbeat))
            {
                return new UnityBakeWorkerStatus(false, "未启动", queue, heartbeat, null, null);
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(heartbeat));
                var root = document.RootElement;
                var state = ReadString(root, "state") ?? "unknown";
                var unityVersion = ReadString(root, "unityVersion");
                var updatedAt = File.GetLastWriteTimeUtc(heartbeat);
                var age = DateTime.UtcNow - updatedAt;
                var running = age <= TimeSpan.FromSeconds(30)
                    && !string.Equals(state, "stopping", StringComparison.OrdinalIgnoreCase);
                var label = running
                    ? $"运行中 ({state}, {age.TotalSeconds:0}s)"
                    : $"未响应 ({state}, {age.TotalSeconds:0}s)";
                return new UnityBakeWorkerStatus(running, label, queue, heartbeat, unityVersion, updatedAt);
            }
            catch
            {
                return new UnityBakeWorkerStatus(false, "heartbeat 损坏", queue, heartbeat, null, File.GetLastWriteTimeUtc(heartbeat));
            }
        }

        public static async Task<UnityBakeWorkerOperationResult> StartAsync(
            string libraryRoot,
            LibraryBrowserSettings settings,
            IProgress<string> progress,
            CancellationToken cancellationToken)
        {
            var queue = GetQueuePath(libraryRoot);
            if (string.IsNullOrWhiteSpace(queue))
            {
                return UnityBakeWorkerOperationResult.Fail("请先选择素材库。");
            }

            var configError = settings?.ValidateUnityBake();
            if (!string.IsNullOrWhiteSpace(configError))
            {
                return UnityBakeWorkerOperationResult.Fail(configError);
            }

            var status = GetStatus(libraryRoot);
            if (status.IsRunning)
            {
                return UnityBakeWorkerOperationResult.Ok("Unity Bake Worker 已经在运行。");
            }

            Directory.CreateDirectory(queue);
            TryDelete(Path.Combine(queue, "worker_stop.request"));
            var workerLog = Path.Combine(queue, "unity_bake_worker.log");
            var args = new[]
            {
                "-batchmode",
                "-projectPath", Quote(settings.UnityProject),
                "-executeMethod", WorkerEntryPoint,
                "-animeStudioBakeQueue", Quote(queue),
                "-logFile", Quote(workerLog),
            };

            progress?.Report("正在启动 Unity Bake Worker...");
            Process.Start(new ProcessStartInfo
            {
                FileName = settings.UnityEditor,
                Arguments = string.Join(" ", args),
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                status = GetStatus(libraryRoot);
                progress?.Report($"Unity Worker 状态: {status.Label}");
                if (status.IsRunning)
                {
                    return UnityBakeWorkerOperationResult.Ok("Unity Bake Worker 已启动。");
                }
            }

            return UnityBakeWorkerOperationResult.Fail("Unity Bake Worker 启动超时。请查看日志：" + workerLog);
        }

        public static async Task<UnityBakeWorkerOperationResult> StopAsync(
            string libraryRoot,
            IProgress<string> progress,
            CancellationToken cancellationToken)
        {
            var queue = GetQueuePath(libraryRoot);
            if (string.IsNullOrWhiteSpace(queue))
            {
                return UnityBakeWorkerOperationResult.Fail("请先选择素材库。");
            }

            Directory.CreateDirectory(queue);
            File.WriteAllText(Path.Combine(queue, "worker_stop.request"), DateTime.UtcNow.ToString("O"));
            progress?.Report("已请求 Unity Bake Worker 停止...");

            var deadline = DateTime.UtcNow.AddSeconds(12);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                var status = GetStatus(libraryRoot);
                progress?.Report($"Unity Worker 状态: {status.Label}");
                if (!status.IsRunning)
                {
                    return UnityBakeWorkerOperationResult.Ok("Unity Bake Worker 已停止。");
                }
            }

            progress?.Report("优雅停止未生效，尝试关闭当前队列对应的旧版 Unity worker...");
            KillMatchingUnityWorker(queue);

            await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
            return GetStatus(libraryRoot).IsRunning
                ? UnityBakeWorkerOperationResult.Fail("已写入停止请求并尝试关闭旧版 worker，但 heartbeat 仍然新鲜。请稍后重试或查看任务管理器。")
                : UnityBakeWorkerOperationResult.Ok("Unity Bake Worker 已停止。");
        }

        public static async Task<UnityBakeWorkerOperationResult> RestartAsync(
            string libraryRoot,
            LibraryBrowserSettings settings,
            IProgress<string> progress,
            CancellationToken cancellationToken)
        {
            var stop = await StopAsync(libraryRoot, progress, cancellationToken).ConfigureAwait(false);
            if (!stop.Success)
            {
                return stop;
            }

            return await StartAsync(libraryRoot, settings, progress, cancellationToken).ConfigureAwait(false);
        }

        private static string ReadString(JsonElement obj, string name)
        {
            return obj.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static void TryDelete(string path)
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

        private static void KillMatchingUnityWorker(string queuePath)
        {
            try
            {
                var escapedQueue = EscapePowerShellSingleQuoted(queuePath);
                var escapedEntry = EscapePowerShellSingleQuoted(WorkerEntryPoint);
                var command =
                    "$queue='" + escapedQueue + "'; " +
                    "$entry='" + escapedEntry + "'; " +
                    "Get-CimInstance Win32_Process -Filter \"Name='Unity.exe'\" | " +
                    "Where-Object { $_.CommandLine -and $_.CommandLine.Contains($entry) -and $_.CommandLine.Contains($queue) } | " +
                    "ForEach-Object { Stop-Process -Id $_.ProcessId -Force }";
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    ArgumentList =
                    {
                        "-NoProfile",
                        "-ExecutionPolicy",
                        "Bypass",
                        "-Command",
                        command,
                    },
                });
                process?.WaitForExit(8000);
            }
            catch
            {
                // The stop file remains as the safe path; forced cleanup is best effort.
            }
        }

        private static string EscapePowerShellSingleQuoted(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }
    }

    internal sealed record UnityBakeWorkerStatus(
        bool IsRunning,
        string Label,
        string QueuePath,
        string HeartbeatPath,
        string UnityVersion,
        DateTime? HeartbeatUtc);

    internal sealed record UnityBakeWorkerOperationResult(bool Success, string Message)
    {
        public static UnityBakeWorkerOperationResult Ok(string message) => new(true, message);
        public static UnityBakeWorkerOperationResult Fail(string message) => new(false, message);
    }
}
