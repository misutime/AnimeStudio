using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeStudio.LibraryBrowser
{
    internal sealed class ThumbnailCache : IDisposable
    {
        private const string ThumbnailCacheVersion = "v2_textured";
        private readonly string _thumbnailDir;
        private readonly string _failedDir;
        private readonly string _f3dPath;
        private readonly SemaphoreSlim _f3dSlots;
        private readonly GltfThumbnailRenderPool _renderPool;
        private readonly ConcurrentDictionary<string, Task<string>> _runningTasks = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        public ThumbnailCache(string root, int maxConcurrency)
        {
            var browserDir = Path.Combine(root, ".animestudio_browser");
            _thumbnailDir = Path.Combine(browserDir, "thumbnails", ThumbnailCacheVersion);
            _failedDir = Path.Combine(browserDir, "thumbnail_failed", ThumbnailCacheVersion);
            Directory.CreateDirectory(_thumbnailDir);
            Directory.CreateDirectory(_failedDir);
            _f3dPath = FindF3d();
            _f3dSlots = new SemaphoreSlim(Math.Max(1, Math.Min(2, maxConcurrency)));
            _renderPool = new GltfThumbnailRenderPool(maxConcurrency);
        }

        public bool HasF3d => !string.IsNullOrWhiteSpace(_f3dPath) && File.Exists(_f3dPath);

        public bool HasPersistentRenderer => _renderPool != null;

        public void StopRenderWorkers()
        {
            _renderPool.Dispose();
        }

        public string GetPath(LibraryModelItem item) => Path.Combine(_thumbnailDir, item.StableKey + ".png");

        public string GetFailurePath(LibraryModelItem item) => Path.Combine(_failedDir, item.StableKey + ".txt");

        public bool IsCached(LibraryModelItem item) => File.Exists(GetPath(item));

        public bool IsFailed(LibraryModelItem item) => File.Exists(GetFailurePath(item));

        public bool TryLoadExisting(LibraryModelItem item, out Image image)
        {
            var path = GetPath(item);
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                using var loaded = Image.FromStream(stream);
                image = new Bitmap(loaded);
                return true;
            }

            image = null;
            return false;
        }

        public Task<string> EnsureAsync(LibraryModelItem item, CancellationToken cancellationToken)
        {
            return _runningTasks.GetOrAdd(item.StableKey, _ => RenderAsync(item, cancellationToken));
        }

        private async Task<string> RenderAsync(LibraryModelItem item, CancellationToken cancellationToken)
        {
            var output = GetPath(item);
            if (File.Exists(output))
            {
                return output;
            }

            if (item.IsVfx && string.IsNullOrWhiteSpace(item.ThumbnailSourcePath))
            {
                try
                {
                    VfxThumbnailRenderer.RenderToFile(item, output);
                    _runningTasks.TryRemove(item.StableKey, out _);
                    return File.Exists(output) ? output : null;
                }
                catch (Exception ex)
                {
                    WriteFailure(item, "vfx procedural thumbnail failed: " + ex.Message);
                    _runningTasks.TryRemove(item.StableKey, out _);
                    return null;
                }
            }

            var result = await _renderPool.RenderAsync(item, output, cancellationToken).ConfigureAwait(false);
            if (result.Success && File.Exists(output))
            {
                _runningTasks.TryRemove(item.StableKey, out _);
                return output;
            }

            if (item.IsVfx)
            {
                try
                {
                    VfxThumbnailRenderer.RenderToFile(item, output);
                    _runningTasks.TryRemove(item.StableKey, out _);
                    return File.Exists(output) ? output : null;
                }
                catch (Exception ex)
                {
                    WriteFailure(item, "vfx fallback thumbnail failed: " + ex.Message);
                    _runningTasks.TryRemove(item.StableKey, out _);
                    return null;
                }
            }

            if (!HasF3d || string.IsNullOrWhiteSpace(item.ThumbnailSourcePath))
            {
                WriteFailure(item, "persistent renderer failed: " + result.Error);
                _runningTasks.TryRemove(item.StableKey, out _);
                return null;
            }

            var fallback = await RenderWithF3dAsync(item, output, "persistent renderer failed: " + result.Error, cancellationToken).ConfigureAwait(false);
            _runningTasks.TryRemove(item.StableKey, out _);
            return fallback;
        }

        private async Task<string> RenderWithF3dAsync(LibraryModelItem item, string output, string reason, CancellationToken cancellationToken)
        {
            await _f3dSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (File.Exists(output))
                {
                    return output;
                }

                var temp = output + ".tmp.png";
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = _f3dPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                // f3d 只作为 fallback。默认路径使用常驻 OpenGL renderer，避免每个模型都启动进程。
                startInfo.ArgumentList.Add("--no-config");
                startInfo.ArgumentList.Add("--resolution");
                startInfo.ArgumentList.Add("256,256");
                startInfo.ArgumentList.Add("--background-color");
                startInfo.ArgumentList.Add("#2f3438");
                startInfo.ArgumentList.Add("--camera-orthographic");
                startInfo.ArgumentList.Add("--camera-azimuth-angle");
                startInfo.ArgumentList.Add("35");
                startInfo.ArgumentList.Add("--camera-elevation-angle");
                startInfo.ArgumentList.Add("25");
                startInfo.ArgumentList.Add("--output");
                startInfo.ArgumentList.Add(temp);
                startInfo.ArgumentList.Add("--input");
                startInfo.ArgumentList.Add(item.ThumbnailSourcePath);

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    WriteFailure(item, reason + Environment.NewLine + "f3d process start failed");
                    return null;
                }

                var exited = await WaitForExitAsync(process, TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
                if (!exited)
                {
                    TryKill(process);
                    WriteFailure(item, reason + Environment.NewLine + "f3d timeout");
                    return null;
                }

                if (process.ExitCode != 0 || !File.Exists(temp))
                {
                    var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                    WriteFailure(item, reason + Environment.NewLine + (string.IsNullOrWhiteSpace(error) ? $"f3d exitCode={process.ExitCode}" : error));
                    return null;
                }

                File.Move(temp, output, true);
                return output;
            }
            finally
            {
                _f3dSlots.Release();
            }
        }

        private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var waitTask = process.WaitForExitAsync(cancellationToken);
            var doneTask = await Task.WhenAny(waitTask, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
            return doneTask == waitTask;
        }

        private void WriteFailure(LibraryModelItem item, string message)
        {
            var path = GetFailurePath(item);
            File.WriteAllText(path, $"{item.OutputPath}{Environment.NewLine}{message}");
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch
            {
                // 进程可能已经自然退出，这里只做清理，不影响浏览器继续工作。
            }
        }

        private static string FindF3d()
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var candidate = Path.Combine(programFiles, "F3D", "bin", "f3d.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir))
                {
                    continue;
                }

                candidate = Path.Combine(dir.Trim(), "f3d.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _renderPool.Dispose();
            _f3dSlots.Dispose();
        }
    }
}
