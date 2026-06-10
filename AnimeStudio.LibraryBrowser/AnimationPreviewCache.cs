using System;
using System.Collections.Generic;
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
        private const string PreviewCacheVersion = "fast-preview-v4-unity-ue-to-gltf";
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
                var status = ReadString(root, "status") ?? "未知";
                var gltf = ReadString(root, "gltf");
                var validation = ReadString(root, "validation");
                var message = ReadString(root, "message");
                if (NeedsUnityBake(animation)
                    && string.Equals(status, "可播放", StringComparison.OrdinalIgnoreCase)
                    && !IsUnityBakedGltf(gltf))
                {
                    status = "局部预览";
                    message ??= "这是旧的快速预览结果，只包含普通 Transform 曲线；Humanoid/Muscle 身体动作仍需 Unity 烘焙。";
                }

                return new AnimationPreviewStatus(
                    status,
                    gltf,
                    validation,
                    message);
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

        public async Task<AnimationPreviewStatus> EnsureUnityBakeAsync(
            LibraryModelItem model,
            LibraryAnimationCandidate animation,
            CancellationToken cancellationToken,
            Action<string> progress = null)
        {
            var directory = GetPreviewDirectory(model, animation);
            var current = GetStatus(model, animation);
            if (string.Equals(current.Status, "可播放", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(current.GltfPath)
                && File.Exists(current.GltfPath)
                && IsUnityBakedGltf(current.GltfPath))
            {
                return current;
            }

            var config = LibraryBrowserSettings.LoadEffective(_root);
            var configError = config.ValidateUnityBake();
            if (!string.IsNullOrWhiteSpace(configError))
            {
                var status = new AnimationPreviewStatus("需配置 Unity", null, null, configError);
                WriteState(directory, status.Status, status.GltfPath, status.ValidationPath, status.Message);
                return status;
            }

            var modelAnimations = Path.Combine(_root, "model_animations.json");
            if (!File.Exists(modelAnimations))
            {
                var status = new AnimationPreviewStatus("烘焙失败", null, null, "没有找到 model_animations.json，无法生成 Unity 烘焙请求。");
                WriteState(directory, status.Status, status.GltfPath, status.ValidationPath, status.Message);
                return status;
            }

            Directory.CreateDirectory(directory);
            WriteState(directory, "Unity 烘焙中", null, null, null);

            var cli = ResolveCliLauncher();
            if (cli == null)
            {
                var status = new AnimationPreviewStatus("烘焙失败", null, null, "没有找到 AnimeStudio.CLI，无法生成 Unity 烘焙。");
                WriteState(directory, status.Status, status.GltfPath, status.ValidationPath, status.Message);
                return status;
            }

            var output = Path.Combine(directory, "unity_bake");
            Directory.CreateDirectory(output);
            var bakedGltf = Path.Combine(
                output,
                "BakedPreview",
                $"{SafeFileName(model.Name)}__{SafeFileName(animation.Name)}.gltf");
            var args = cli.BuildUnityBakeArguments(
                modelAnimations,
                model.OutputPath,
                animation.BestPath,
                output,
                config.UnityProject,
                config.UnityEditor,
                bakedGltf);

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

            progress?.Invoke("启动 Unity Humanoid 烘焙");
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                var status = new AnimationPreviewStatus("烘焙失败", null, null, "启动 AnimeStudio.CLI 失败。");
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
                var status = new AnimationPreviewStatus("已取消", null, null, "用户取消了 Unity 烘焙。");
                WriteState(directory, status.Status, status.GltfPath, status.ValidationPath, status.Message);
                return status;
            }

            var report = Path.Combine(Path.GetDirectoryName(bakedGltf) ?? output, "unity_bake_apply_report.json");
            var baked = process.ExitCode == 0
                && File.Exists(bakedGltf)
                && HasTrustedUnityBakeReport(report)
                && IsUnityBakedGltf(bakedGltf);
            var finalStatus = baked ? "可播放" : "烘焙失败";
            var message = baked ? null : BuildUnityBakeFailureMessage(process.ExitCode, report, stdout.ToString(), stderr.ToString());
            WriteState(directory, finalStatus, baked ? bakedGltf : null, File.Exists(report) ? report : null, message);
            return new AnimationPreviewStatus(finalStatus, baked ? bakedGltf : null, File.Exists(report) ? report : null, message);
        }

        public async Task<AnimationPreviewStatus> EnsureUnrealAsync(
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
            WriteState(directory, "UE预览生成中", null, null, null);

            var launcher = ResolveUnrealExporterLauncher();
            if (launcher == null)
            {
                var status = new AnimationPreviewStatus("失败", null, null, "没有找到 UnrealExporter，无法生成 UE 动画预览。");
                WriteState(directory, status.Status, status.GltfPath, status.ValidationPath, status.Message);
                return status;
            }

            var output = Path.Combine(directory, "preview.glb");
            var validation = Path.Combine(directory, "preview_validation.json");
            var startInfo = new ProcessStartInfo
            {
                FileName = launcher.FileName,
                WorkingDirectory = launcher.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in launcher.BuildArguments(model.OutputPath, animation.BestPath, output, validation))
            {
                startInfo.ArgumentList.Add(arg);
            }

            var startedAt = DateTime.UtcNow;
            progress?.Invoke("启动 UE 动画预览导出");
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                var status = new AnimationPreviewStatus("失败", null, null, "启动 UnrealExporter 失败。");
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
                var status = new AnimationPreviewStatus("已取消", null, null, "用户取消了 UE 动画预览生成。");
                WriteState(directory, status.Status, status.GltfPath, status.ValidationPath, status.Message);
                return status;
            }

            var playable = process.ExitCode == 0 && File.Exists(output) && HasTrustedPreviewReport(validation);
            var finalStatus = playable ? "可播放" : "失败";
            var message = playable ? null : BuildPreviewFailureMessage(validation, process.ExitCode, stdout.ToString(), stderr.ToString());
            WriteState(directory, finalStatus, playable ? output : null, File.Exists(validation) ? validation : null, message);
            return new AnimationPreviewStatus(finalStatus, playable ? output : null, File.Exists(validation) ? validation : null, message);
        }

        private static bool NeedsUnityBake(LibraryAnimationCandidate animation)
        {
            return animation?.RequiresHumanoidBake == true
                || string.Equals(animation?.Capability, "HumanoidBodyBakeReady", StringComparison.OrdinalIgnoreCase);
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

        private static string SafeFileName(string value)
        {
            var text = string.IsNullOrWhiteSpace(value) ? "animation_preview" : value.Trim();
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                text = text.Replace(c, '_');
            }

            return text.Length > 120 ? text[..120] : text;
        }

        private static bool HasTrustedUnityBakeReport(string reportPath)
        {
            if (string.IsNullOrWhiteSpace(reportPath) || !File.Exists(reportPath))
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
                var status = ReadString(document.RootElement, "status");
                return string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "warning", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool HasTrustedPreviewReport(string reportPath)
        {
            if (string.IsNullOrWhiteSpace(reportPath) || !File.Exists(reportPath))
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
                var status = ReadString(document.RootElement, "status");
                var gltf = ReadString(document.RootElement, "gltf");
                return (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(status, "warning", StringComparison.OrdinalIgnoreCase))
                    && !string.IsNullOrWhiteSpace(gltf)
                    && File.Exists(gltf);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsUnityBakedGltf(string gltfPath)
        {
            if (string.IsNullOrWhiteSpace(gltfPath) || !File.Exists(gltfPath))
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(gltfPath));
                foreach (var animation in document.RootElement.GetProperty("animations").EnumerateArray())
                {
                    if (animation.TryGetProperty("extras", out var extras)
                        && extras.TryGetProperty("animeStudioUnityBake", out _))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
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

        private static string BuildPreviewFailureMessage(string reportPath, int exitCode, string stdout, string stderr)
        {
            var reportSummary = "";
            if (!string.IsNullOrWhiteSpace(reportPath) && File.Exists(reportPath))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
                    var status = ReadString(document.RootElement, "status") ?? "";
                    var error = ReadString(document.RootElement, "error") ?? "";
                    var gltf = ReadString(document.RootElement, "gltf") ?? "";
                    reportSummary =
                        $"preview_validation.json: status={EmptyAsUnknown(status)}, gltf={EmptyAsNone(gltf)}" +
                        (string.IsNullOrWhiteSpace(error) ? "" : $"{Environment.NewLine}error={error}");
                }
                catch
                {
                    reportSummary = "preview_validation.json 存在，但读取失败。";
                }
            }
            else
            {
                reportSummary = "没有生成 preview_validation.json。";
            }

            var text = string.Join(Environment.NewLine, new[] { reportSummary, stdout, stderr }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (text.Length > 4000)
            {
                text = text[..4000];
            }

            if (text.Contains("Selected preview entry source files no longer exist", StringComparison.OrdinalIgnoreCase))
            {
                return "UE 动画预览没有生成可信的 glTF。"
                    + Environment.NewLine
                    + "当前失败来自旧版预览命令或旧缓存：它尝试重新读取原始 .uasset 源文件。"
                    + Environment.NewLine
                    + "请使用已刷新后的 Browser/UnrealExporter 重新生成预览；新版路径应只依赖素材库里的模型 GLB 和 .ueanim。"
                    + Environment.NewLine
                    + $"CLI exit code {exitCode}"
                    + Environment.NewLine
                    + text;
            }

            return "UE 动画预览没有生成可信的 glTF。"
                + Environment.NewLine
                + $"CLI exit code {exitCode}"
                + Environment.NewLine
                + text;
        }

        private static string EmptyAsUnknown(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
        }

        private static string EmptyAsNone(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
        }

        private static string BuildUnityBakeFailureMessage(int exitCode, string reportPath, string stdout, string stderr)
        {
            var report = "";
            if (!string.IsNullOrWhiteSpace(reportPath) && File.Exists(reportPath))
            {
                try
                {
                    report = File.ReadAllText(reportPath);
                    if (report.Length > 2000)
                    {
                        report = report[..2000];
                    }
                }
                catch
                {
                    report = "";
                }
            }

            var text = string.Join(Environment.NewLine, new[] { report, stdout, stderr }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (text.Length > 4000)
            {
                text = text[..4000];
            }

            return "Unity 烘焙没有生成可信的 baked glTF。需要同时看到 unity_bake_apply_report.json 和 glTF extras.animeStudioUnityBake。"
                + Environment.NewLine
                + $"CLI exit code {exitCode}"
                + Environment.NewLine
                + text;
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

        private static ExternalLauncher ResolveUnrealExporterLauncher()
        {
            var roots = CandidateUnrealExporterRoots();
            foreach (var root in roots)
            {
                var exe = Path.Combine(root, "UnrealExporter", "bin", "Debug", "net8.0", "UnrealExporter.exe");
                if (File.Exists(exe))
                {
                    return new ExternalLauncher(
                        exe,
                        root,
                        (model, animation, output, report) => new[]
                        {
                            "--preview-ue-animation",
                            "--model", model,
                            "--animation", animation,
                            "--output", output,
                            "--report", report,
                        });
                }

                var project = Path.Combine(root, "UnrealExporter", "UnrealExporter.csproj");
                if (File.Exists(project))
                {
                    return new ExternalLauncher(
                        "dotnet",
                        root,
                        (model, animation, output, report) => new[]
                        {
                            "run",
                            "--project", project,
                            "--no-build",
                            "--",
                            "--preview-ue-animation",
                            "--model", model,
                            "--animation", animation,
                            "--output", output,
                            "--report", report,
                        });
                }
            }

            return null;
        }

        private static string[] CandidateUnrealExporterRoots()
        {
            var result = new List<string>();
            var workspace = FindWorkspaceRoot();
            var parent = Directory.GetParent(workspace)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent))
            {
                result.Add(Path.Combine(parent, "UnrealExporter"));
            }

            result.Add(Path.Combine(workspace, "UnrealExporter"));
            result.Add(@"D:\misutime\UnrealExporter");
            return result
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static CliLauncher BuildExeLauncher(string exe)
        {
            return new CliLauncher(
                exe,
                (index, model, animation, output) => new[]
                {
                    "--generate_preview_from_library", index,
                    "--game", "Normal",
                    "--preview_model", model,
                    "--preview_animation", animation,
                    "--preview_output", output,
                },
                (modelAnimations, model, animation, output, unityProject, unityEditor, bakedGltf) => new[]
                {
                    "--generate_unity_bake_request_from_library", Path.GetDirectoryName(modelAnimations) ?? Directory.GetCurrentDirectory(),
                    "--preview_model", model,
                    "--preview_animation", animation,
                    "--preview_output", output,
                    "--unity_project", unityProject,
                    "--unity_editor", unityEditor,
                    "--unity_bake_worker_queue", Path.Combine(Path.GetDirectoryName(modelAnimations) ?? Directory.GetCurrentDirectory(), ".as_browser_cache", "unity_bake_worker"),
                    "--baked_gltf_output", bakedGltf,
                    "--run_unity_bake",
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

        private sealed record CliLauncher(
            string FileName,
            Func<string, string, string, string, string[]> BuildArguments,
            Func<string, string, string, string, string, string, string, string[]> BuildUnityBakeArguments);

        private sealed record ExternalLauncher(
            string FileName,
            string WorkingDirectory,
            Func<string, string, string, string, string[]> BuildArguments);

    }

    internal sealed record AnimationPreviewStatus(string Status, string GltfPath, string ValidationPath, string Message);
}
