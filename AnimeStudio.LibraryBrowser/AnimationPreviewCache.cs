using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeStudio.LibraryBrowser
{
    internal sealed class AnimationPreviewCache
    {
        private const string PreviewCacheVersion = "fast-preview-v2-unity-to-gltf";
        private readonly string _root;
        private readonly string _cacheRoot;

        public AnimationPreviewCache(string root)
        {
            _root = root;
            _cacheRoot = Path.Combine(root, ".as_browser_cache", "animation_previews");
            Directory.CreateDirectory(_cacheRoot);
        }

        public AnimationPreviewStatus GetStatus(LibraryModelItem model, LibraryAnimationCandidate animation)
        {
            var directory = GetPreviewDirectory(model, animation);
            var statePath = Path.Combine(directory, "preview_state.json");
            if (!File.Exists(statePath))
            {
                return new AnimationPreviewStatus("未生成", null, null, null);
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(statePath));
                var root = document.RootElement;
                return new AnimationPreviewStatus(
                    ReadString(root, "status") ?? "未知",
                    ReadString(root, "gltf"),
                    ReadString(root, "validation"),
                    ReadString(root, "message"));
            }
            catch
            {
                return new AnimationPreviewStatus("状态损坏", null, null, statePath);
            }
        }

        public async Task<AnimationPreviewStatus> EnsureAsync(
            LibraryModelItem model,
            LibraryAnimationCandidate animation,
            CancellationToken cancellationToken,
            Action<string> progress = null)
        {
            var current = GetStatus(model, animation);
            if (string.Equals(current.Status, "可播放", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(current.GltfPath)
                && File.Exists(current.GltfPath))
            {
                return current;
            }

            var directory = GetPreviewDirectory(model, animation);
            Directory.CreateDirectory(directory);
            WriteState(directory, "生成中", null, null, null);

            var cli = ResolveCliLauncher();
            if (cli == null)
            {
                var status = new AnimationPreviewStatus("失败", null, null, "没有找到 AnimeStudio.CLI，无法生成动画预览。");
                WriteState(directory, status.Status, status.GltfPath, status.ValidationPath, status.Message);
                return status;
            }

            var output = Path.Combine(directory, "output");
            var args = cli.BuildArguments(_root, model.OutputPath, animation.BestPath, output);
            var startedAt = DateTime.UtcNow;
            var startInfo = new ProcessStartInfo
            {
                FileName = cli.FileName,
                WorkingDirectory = FindWorkspaceRoot(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            progress?.Invoke("启动 CLI 预览导出");
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                var status = new AnimationPreviewStatus("失败", null, null, "启动 AnimeStudio.CLI 失败。");
                WriteState(directory, status.Status, status.GltfPath, status.ValidationPath, status.Message);
                return status;
            }

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null)
                {
                    return;
                }

                lock (stdout)
                {
                    stdout.AppendLine(e.Data);
                }

                progress?.Invoke(BuildProgressMessage(startedAt, e.Data));
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null)
                {
                    return;
                }

                lock (stderr)
                {
                    stderr.AppendLine(e.Data);
                }

                progress?.Invoke(BuildProgressMessage(startedAt, e.Data));
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                var status = new AnimationPreviewStatus("已取消", null, null, "用户取消了动画预览生成。");
                WriteState(directory, status.Status, status.GltfPath, status.ValidationPath, status.Message);
                return status;
            }

            var validation = Path.Combine(output, "preview_validation.json");
            var gltf = File.Exists(validation)
                ? ReadString(JsonDocument.Parse(File.ReadAllText(validation)).RootElement, "gltf")
                : Directory.Exists(output)
                    ? Directory.EnumerateFiles(output, "*.gltf", SearchOption.AllDirectories).FirstOrDefault()
                    : null;

            var playable = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(gltf) && File.Exists(gltf);
            var finalStatus = playable ? "可播放" : "失败";
            var message = playable ? null : BuildFailureMessage(process.ExitCode, stdout.ToString(), stderr.ToString());
            WriteState(directory, finalStatus, gltf, File.Exists(validation) ? validation : null, message);
            return new AnimationPreviewStatus(finalStatus, gltf, File.Exists(validation) ? validation : null, message);
        }

        private static string BuildProgressMessage(DateTime startedAt, string line)
        {
            var elapsed = DateTime.UtcNow - startedAt;
            line = string.IsNullOrWhiteSpace(line) ? "处理中" : line.Trim();
            if (line.Length > 160)
            {
                line = line[..160] + "...";
            }

            return $"动画预览生成中 {elapsed:mm\\:ss} | {line}";
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // 进程可能刚好退出；这里只负责尽力清理预览子进程。
            }
        }

        private string GetPreviewDirectory(LibraryModelItem model, LibraryAnimationCandidate animation)
        {
            // 预览生成规则变化时提高版本号，避免复用旧坐标转换错误的缓存。
            var raw = $"{PreviewCacheVersion}|{model.StableKey}|{animation.Name}|{animation.BestPath}|{animation.Source}";
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
            return Path.Combine(_cacheRoot, model.StableKey, hash);
        }

        private void WriteState(string directory, string status, string gltf, string validation, string message)
        {
            Directory.CreateDirectory(directory);
            var state = new
            {
                updatedAt = DateTime.UtcNow.ToString("O"),
                version = PreviewCacheVersion,
                status,
                gltf,
                validation,
                message,
            };
            File.WriteAllText(
                Path.Combine(directory, "preview_state.json"),
                JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static string BuildFailureMessage(int exitCode, string stdout, string stderr)
        {
            var text = string.Join(Environment.NewLine, new[] { stdout, stderr }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (text.Length > 4000)
            {
                text = text[..4000];
            }
            return $"CLI exit code {exitCode}{Environment.NewLine}{text}";
        }

        private static CliLauncher ResolveCliLauncher()
        {
            var workspace = FindWorkspaceRoot();
            var siblingExe = Path.Combine(AppContext.BaseDirectory, "AnimeStudio.CLI.exe");
            if (File.Exists(siblingExe))
            {
                return BuildExeLauncher(siblingExe);
            }

            var builtExe = Path.Combine(workspace, "AnimeStudio.CLI", "bin", "Debug", "net9.0-windows", "AnimeStudio.CLI.exe");
            if (File.Exists(builtExe))
            {
                return BuildExeLauncher(builtExe);
            }

            return null;
        }

        private static CliLauncher BuildExeLauncher(string exe)
        {
            return new CliLauncher(exe, (index, model, animation, output) => new[]
            {
                "--generate_preview_from_library", index,
                "--game", "Normal",
                "--preview_model", model,
                "--preview_animation", animation,
                "--preview_output", output,
            });
        }

        private static string FindWorkspaceRoot()
        {
            var dir = AppContext.BaseDirectory;
            while (!string.IsNullOrWhiteSpace(dir))
            {
                if (File.Exists(Path.Combine(dir, "AnimeStudio.sln"))
                    || File.Exists(Path.Combine(dir, "AnimeStudio.CLI", "AnimeStudio.CLI.csproj")))
                {
                    return dir;
                }

                dir = Directory.GetParent(dir)?.FullName;
            }

            return Directory.GetCurrentDirectory();
        }

        private static string ReadString(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
        }

        private sealed record CliLauncher(string FileName, Func<string, string, string, string, string[]> BuildArguments);
    }

    internal sealed record AnimationPreviewStatus(string Status, string GltfPath, string ValidationPath, string Message);
}
