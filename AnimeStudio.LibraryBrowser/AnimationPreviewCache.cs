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
using Microsoft.Data.Sqlite;

namespace AnimeStudio.LibraryBrowser
{
    internal sealed class AnimationPreviewCache
    {
        private const string PreviewCacheVersion = "fast-preview-v7-ue-library-files";
        private readonly string _root;
        private readonly string _cacheRoot;
        private readonly object _sqliteBakeCacheLock = new();
        private Dictionary<string, AnimationPreviewStatus> _sqliteBakeCache;
        private DateTime _sqliteBakeCacheTimestampUtc;

        public AnimationPreviewCache(string root)
        {
            _root = root;
            _cacheRoot = Path.Combine(root, ".as_browser_cache", "animation_previews");
            Directory.CreateDirectory(_cacheRoot);
        }

        public AnimationPreviewStatus GetStatus(LibraryModelItem model, LibraryAnimationCandidate animation)
        {
            if (animation?.NeedsProductionAvatarRefresh == true)
            {
                return new AnimationPreviewStatus(
                    "需 Avatar 元数据",
                    null,
                    null,
                    "该显式 Unity 动画关系缺少生产 Unity bake 所需的原始 Avatar/HumanDescription；AvatarConstant 只能作为诊断输入，不能当作可信可播放动画。");
            }

            var sqliteBakeStatus = GetSqliteBakeStatus(model, animation);
            var directory = GetPreviewDirectory(model, animation);
            var statePath = Path.Combine(directory, "preview_state.json");
            if (!File.Exists(statePath))
            {
                return sqliteBakeStatus ?? new AnimationPreviewStatus("未生成", null, null, null);
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(statePath));
                var root = document.RootElement;
                var status = ReadString(root, "status") ?? "未知";
                var gltf = ReadString(root, "gltf");
                var validation = ReadString(root, "validation");
                var message = ReadString(root, "message");
                if (!string.Equals(status, "可播放", StringComparison.OrdinalIgnoreCase)
                    && sqliteBakeStatus != null
                    && string.Equals(sqliteBakeStatus.Status, "可播放", StringComparison.OrdinalIgnoreCase))
                {
                    return sqliteBakeStatus;
                }

                if (NeedsUnityBake(animation)
                    && string.Equals(status, "可播放", StringComparison.OrdinalIgnoreCase)
                    && !IsUnityBakedGltf(gltf))
                {
                    if (sqliteBakeStatus != null
                        && string.Equals(sqliteBakeStatus.Status, "可播放", StringComparison.OrdinalIgnoreCase))
                    {
                        return sqliteBakeStatus;
                    }

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
            if (model == null || animation == null)
            {
                return new AnimationPreviewStatus("失败", null, null, "没有选中模型或动画。");
            }

            if (animation?.IsExplicit != true)
            {
                var status = new AnimationPreviewStatus(
                    "拒绝预览",
                    null,
                    null,
                    "当前模型-动画关系不是 Unity 显式关系；结构匹配只能诊断，不能写入默认可播放预览缓存。");
                WriteState(GetPreviewDirectory(model, animation), status.Status, status.GltfPath, status.ValidationPath, status.Message);
                return status;
            }

            if (animation?.NeedsProductionAvatarRefresh == true)
            {
                var status = new AnimationPreviewStatus(
                    "需 Avatar 元数据",
                    null,
                    null,
                    "该显式 Unity 动画关系缺少生产 Unity bake 所需的原始 Avatar/HumanDescription；请先刷新或恢复模型 Avatar 元数据。");
                WriteState(GetPreviewDirectory(model, animation), status.Status, status.GltfPath, status.ValidationPath, status.Message);
                return status;
            }

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
            if (model == null || animation == null)
            {
                return new AnimationPreviewStatus("烘焙失败", null, null, "没有选中模型或动画。");
            }

            var directory = GetPreviewDirectory(model, animation);
            if (animation?.IsExplicit != true)
            {
                var status = new AnimationPreviewStatus(
                    "拒绝烘焙",
                    null,
                    null,
                    "Unity bake 只能处理 Unity 显式关系候选；结构匹配、骨骼兼容或人工拼接不能进入生产烘焙。");
                WriteState(directory, status.Status, status.GltfPath, status.ValidationPath, status.Message);
                return status;
            }

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

            var libraryIndex = Path.Combine(_root, "library_index.db");
            if (!File.Exists(libraryIndex))
            {
                var status = new AnimationPreviewStatus("烘焙失败", null, null, "没有找到 library_index.db，无法按 Unity 显式关系生成 Unity 烘焙。");
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
            var unityAvatarAsset = config.ResolveUnityAvatarAsset(
                model.Name,
                model.OutputPath,
                model.FileName,
                ResolveModelAvatarName(model));
            var args = cli.BuildUnityBakeArguments(
                _root,
                model.OutputPath,
                animation.BestPath,
                output,
                config.UnityProject,
                config.UnityEditor,
                null,
                unityAvatarAsset);

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

            var batchReport = Path.Combine(output, "unity_bake_batch_report.json");
            var bakedGltf = ReadBakedGltfFromBatchReport(batchReport);
            var report = string.IsNullOrWhiteSpace(bakedGltf)
                ? null
                : Path.Combine(Path.GetDirectoryName(bakedGltf) ?? output, "unity_bake_apply_report.json");
            var baked = process.ExitCode == 0
                && !string.IsNullOrWhiteSpace(bakedGltf)
                && File.Exists(bakedGltf)
                && HasTrustedUnityBakeReport(report)
                && IsUnityBakedGltf(bakedGltf);
            var finalStatus = baked ? "可播放" : "烘焙失败";
            var message = baked ? null : BuildUnityBakeFailureMessage(process.ExitCode, report ?? batchReport, stdout.ToString(), stderr.ToString());
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
                || animation?.RequiresUnityBake == true
                || animation?.RequiresInternalHumanoidSolve == true
                || string.Equals(animation?.NextAction, "generate_unity_baked_gltf", StringComparison.OrdinalIgnoreCase)
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
                if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(status, "warning", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                // Unity bake 可能只把模型推到某个定格姿态，但没有任何帧间变化。
                // 这种文件可用于诊断，不能在浏览器里标成“可播放”。
                return TryReadInt(document.RootElement, "frameVaryingTracks", out var frameVaryingTracks)
                    && frameVaryingTracks > 0
                    && HasTrustedAvatarBake(document.RootElement);
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

        private static string ReadBakedGltfFromBatchReport(string reportPath)
        {
            if (string.IsNullOrWhiteSpace(reportPath) || !File.Exists(reportPath))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
                if (!document.RootElement.TryGetProperty("items", out var items)
                    || items.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                foreach (var item in items.EnumerateArray())
                {
                    if (!string.Equals(ReadString(item, "status"), "baked", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var gltf = ReadString(item, "bakedGltf");
                    if (!string.IsNullOrWhiteSpace(gltf) && File.Exists(gltf))
                    {
                        return gltf;
                    }
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private AnimationPreviewStatus GetSqliteBakeStatus(LibraryModelItem model, LibraryAnimationCandidate animation)
        {
            if (model == null || animation == null || string.IsNullOrWhiteSpace(model.OutputPath) || string.IsNullOrWhiteSpace(animation.BestPath))
            {
                return null;
            }

            var cache = LoadSqliteBakeCache();
            return cache.TryGetValue(BuildBakeCacheKey(model.OutputPath, animation.BestPath), out var status)
                ? status
                : null;
        }

        private string ResolveModelAvatarName(LibraryModelItem model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.OutputPath))
            {
                return null;
            }

            var dbPath = Path.Combine(_root, "library_index.db");
            if (!File.Exists(dbPath))
            {
                return null;
            }

            try
            {
                SQLitePCL.Batteries_V2.Init();
                using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT raw_json
FROM assets
WHERE output=$output
LIMIT 1;";
                command.Parameters.AddWithValue("$output", model.OutputPath);
                var rawJson = command.ExecuteScalar() as string;
                if (string.IsNullOrWhiteSpace(rawJson))
                {
                    return null;
                }

                using var document = JsonDocument.Parse(rawJson);
                if (!document.RootElement.TryGetProperty("avatar", out var avatar)
                    || avatar.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var avatarName = ReadString(avatar, "name");
                return string.IsNullOrWhiteSpace(avatarName) ? null : avatarName;
            }
            catch
            {
                return null;
            }
        }

        private Dictionary<string, AnimationPreviewStatus> LoadSqliteBakeCache()
        {
            var dbPath = Path.Combine(_root, "library_index.db");
            var timestamp = File.Exists(dbPath) ? File.GetLastWriteTimeUtc(dbPath) : DateTime.MinValue;
            lock (_sqliteBakeCacheLock)
            {
                if (_sqliteBakeCache != null && _sqliteBakeCacheTimestampUtc == timestamp)
                {
                    return _sqliteBakeCache;
                }

                _sqliteBakeCache = LoadSqliteBakeCacheCore(dbPath);
                _sqliteBakeCacheTimestampUtc = timestamp;
                return _sqliteBakeCache;
            }
        }

        private Dictionary<string, AnimationPreviewStatus> LoadSqliteBakeCacheCore(string dbPath)
        {
            var result = new Dictionary<string, AnimationPreviewStatus>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            {
                return result;
            }

            try
            {
                SQLitePCL.Batteries_V2.Init();
                using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                connection.Open();
                if (!HasTable(connection, "animation_bake_cache"))
                {
                    return result;
                }

                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT model_output, animation_output, status, baked_gltf_path, result_path, message
FROM animation_bake_cache;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var modelOutput = ReadDbString(reader, 0);
                    var animationOutput = ReadDbString(reader, 1);
                    if (string.IsNullOrWhiteSpace(modelOutput) || string.IsNullOrWhiteSpace(animationOutput))
                    {
                        continue;
                    }

                    var status = ReadDbString(reader, 2);
                    var bakedGltf = ResolveLibraryPath(ReadDbString(reader, 3));
                    var resultPath = ResolveLibraryPath(ReadDbString(reader, 4));
                    var message = ReadDbString(reader, 5);
                    var applyReport = ResolveUnityBakeApplyReport(bakedGltf);
                    var applyStatus = ReadUnityBakeApplyStatus(applyReport);
                    var hasTrustedBakedGltf = !string.IsNullOrWhiteSpace(bakedGltf)
                        && File.Exists(bakedGltf)
                        && HasTrustedUnityBakeReport(applyReport)
                        && IsUnityBakedGltf(bakedGltf);
                    var previewStatus = hasTrustedBakedGltf
                        ? new AnimationPreviewStatus("可播放", bakedGltf, applyReport ?? resultPath, message)
                        : new AnimationPreviewStatus(
                            FormatBakeCacheStatus(status, bakedGltf, applyStatus),
                            bakedGltf,
                            applyReport ?? resultPath,
                            message ?? BuildUntrustedBakeCacheMessage(status, bakedGltf, applyReport, applyStatus));

                    var key = BuildBakeCacheKey(modelOutput, animationOutput);
                    result[key] = result.TryGetValue(key, out var existing)
                        ? PreferBakeCacheStatus(existing, previewStatus)
                        : previewStatus;
                }
            }
            catch
            {
                return result;
            }

            return result;
        }

        private string ResolveLibraryPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return Path.IsPathRooted(path)
                ? path
                : Path.Combine(_root, path);
        }

        private string BuildBakeCacheKey(string modelOutput, string animationOutput)
        {
            return NormalizeCachePath(modelOutput) + "|" + NormalizeCachePath(animationOutput);
        }

        private string NormalizeCachePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "";
            }

            var resolved = ResolveLibraryPath(path);
            return Path.GetFullPath(resolved).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static AnimationPreviewStatus PreferBakeCacheStatus(AnimationPreviewStatus existing, AnimationPreviewStatus incoming)
        {
            return BakeCacheStatusPriority(incoming) > BakeCacheStatusPriority(existing)
                ? incoming
                : existing;
        }

        private static int BakeCacheStatusPriority(AnimationPreviewStatus status)
        {
            return status?.Status switch
            {
                "可播放" => 100,
                "已生成请求" => 40,
                "静态姿态" => 35,
                "已烘焙但需重建" => 30,
                "烘焙失败" => 20,
                "未生成" => 0,
                _ => 10,
            };
        }

        private static string FormatBakeCacheStatus(string status, string bakedGltf, UnityBakeApplyStatus applyStatus)
        {
            if (string.Equals(applyStatus?.Status, "static_pose", StringComparison.OrdinalIgnoreCase))
            {
                return "静态姿态";
            }
            if (string.Equals(applyStatus?.Status, "needs_review", StringComparison.OrdinalIgnoreCase))
            {
                return "需人工验收";
            }

            return status switch
            {
                "request_written" => "已生成请求",
                "failed" => "烘焙失败",
                "baked" => "已烘焙但需重建",
                null or "" => "未生成",
                _ => status,
            };
        }

        private static string BuildUntrustedBakeCacheMessage(string status, string bakedGltf, string applyReport, UnityBakeApplyStatus applyStatus)
        {
            if (!string.Equals(status, "baked", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(bakedGltf))
            {
                return "缓存状态是 baked，但没有记录 baked glTF 路径，需要重新 Unity 烘焙。";
            }

            if (!File.Exists(bakedGltf))
            {
                return "缓存状态是 baked，但 baked glTF 文件不存在，需要重新 Unity 烘焙。";
            }

            if (!string.IsNullOrWhiteSpace(applyReport) && File.Exists(applyReport))
            {
                if (string.Equals(applyStatus?.Status, "static_pose", StringComparison.OrdinalIgnoreCase))
                {
                    return "Unity 烘焙只生成了定格姿态，报告没有检测到帧间变化。需要换动画或继续追 Unity Avatar/Clip 采样问题。";
                }
                if (string.Equals(applyStatus?.Status, "needs_review", StringComparison.OrdinalIgnoreCase))
                {
                    return string.IsNullOrWhiteSpace(applyStatus.Message)
                        ? "Unity 烘焙使用的 Avatar 元数据不完整，需要人工验收或刷新 Avatar 元数据后重烘焙。"
                        : applyStatus.Message;
                }

                if (applyStatus == null)
                {
                    return "baked glTF 旁边的 unity_bake_apply_report.json 无法读取，需要重新 Unity 烘焙。";
                }

                var frameVaryingTracks = applyStatus.FrameVaryingTracks?.ToString() ?? "未知";
                if (applyStatus.TrustedProductionBake == false)
                {
                    return string.IsNullOrWhiteSpace(applyStatus.AvatarTrustMessage)
                        ? "Unity 烘焙报告缺少可信 Avatar 证明，需要用新版默认姿态重烘焙后再作为可播放动画。"
                        : applyStatus.AvatarTrustMessage;
                }

                return $"Unity 烘焙报告状态是 {applyStatus.Status}，帧间变化轨道数 {frameVaryingTracks}，不能当作可信可播放动画。";
            }

            if (IsUnityBakedGltf(bakedGltf))
            {
                return "缓存状态是 baked，但缺少新版 unity_bake_apply_report.json、帧间变化统计或可信 Avatar 证明，需要重新 Unity 烘焙确认。";
            }

            return "缓存状态是 baked，但 glTF 缺少 Unity bake 标记，需要重新 Unity 烘焙。";
        }

        private static string ResolveUnityBakeApplyReport(string bakedGltf)
        {
            if (string.IsNullOrWhiteSpace(bakedGltf))
            {
                return null;
            }

            var directory = Path.GetDirectoryName(bakedGltf);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return null;
            }

            var report = Path.Combine(directory, "unity_bake_apply_report.json");
            return File.Exists(report) ? report : null;
        }

        private static UnityBakeApplyStatus ReadUnityBakeApplyStatus(string reportPath)
        {
            if (string.IsNullOrWhiteSpace(reportPath) || !File.Exists(reportPath))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
                var root = document.RootElement;
                return new UnityBakeApplyStatus(
                    ReadString(root, "status"),
                    TryReadInt(root, "frameVaryingTracks", out var tracks) ? tracks : null,
                    TryReadInt(root, "frameVaryingChannels", out var channels) ? channels : null,
                    ReadString(root, "message"),
                    TryReadAvatarTrust(root, out var trusted, out var trustMessage) ? trusted : null,
                    trustMessage);
            }
            catch
            {
                return null;
            }
        }

        private static bool HasTrustedAvatarBake(JsonElement root)
        {
            return TryReadAvatarTrust(root, out var trusted, out _) && trusted;
        }

        private static bool TryReadAvatarTrust(JsonElement root, out bool trusted, out string message)
        {
            trusted = false;
            message = null;
            if (!root.TryGetProperty("avatarTrust", out var avatarTrust)
                || avatarTrust.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (avatarTrust.TryGetProperty("Message", out var messageProperty)
                || avatarTrust.TryGetProperty("message", out messageProperty))
            {
                message = messageProperty.ValueKind == JsonValueKind.String
                    ? messageProperty.GetString()
                    : null;
            }

            if (avatarTrust.TryGetProperty("TrustedProductionBake", out var trustedProperty)
                || avatarTrust.TryGetProperty("trustedProductionBake", out trustedProperty))
            {
                if (trustedProperty.ValueKind == JsonValueKind.True || trustedProperty.ValueKind == JsonValueKind.False)
                {
                    trusted = trustedProperty.GetBoolean()
                        && AvatarTrustSourceMatchesExplicitRequest(root, avatarTrust);
                    return true;
                }

                if (trustedProperty.ValueKind == JsonValueKind.String
                    && bool.TryParse(trustedProperty.GetString(), out trusted))
                {
                    trusted = trusted && AvatarTrustSourceMatchesExplicitRequest(root, avatarTrust);
                    return true;
                }
            }

            return false;
        }

        private static bool AvatarTrustSourceMatchesExplicitRequest(JsonElement root, JsonElement avatarTrust)
        {
            if (!ReportRequestHasExplicitAvatarAsset(root))
            {
                return true;
            }

            var source = ReadString(avatarTrust, "Source") ?? ReadString(avatarTrust, "source");
            return string.Equals(source, "imported_unity_avatar_asset", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ReportRequestHasExplicitAvatarAsset(JsonElement root)
        {
            var requestPath = ReadString(root, "request");
            if (string.IsNullOrWhiteSpace(requestPath) || !File.Exists(requestPath))
            {
                return false;
            }

            try
            {
                using var request = JsonDocument.Parse(File.ReadAllText(requestPath));
                var requestRoot = request.RootElement;
                if (!requestRoot.TryGetProperty("unityAssetPaths", out var unityAssetPaths)
                    || unityAssetPaths.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                var avatarAsset = ReadString(unityAssetPaths, "avatarAsset");
                return !string.IsNullOrWhiteSpace(avatarAsset);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadInt(JsonElement obj, string name, out int value)
        {
            value = 0;
            if (!obj.TryGetProperty(name, out var property))
            {
                return false;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value))
            {
                return true;
            }

            return property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out value);
        }

        private static string ReadDbString(SqliteDataReader reader, int index)
        {
            return reader.IsDBNull(index) ? null : reader.GetString(index);
        }

        private static bool HasTable(SqliteConnection connection, string tableName)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;";
            command.Parameters.AddWithValue("$name", tableName);
            return command.ExecuteScalar() != null;
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
                (libraryRoot, model, animation, output, unityProject, unityEditor, bakedGltf, unityAvatarAsset) =>
                {
                    var args = new List<string>
                    {
                        "--bake_animation_previews_from_library", libraryRoot,
                        "--preview_model", model,
                        "--pack_animations", animation,
                        "--preview_validation_limit", "1",
                        "--preview_validation_output", output,
                        "--unity_project", unityProject,
                        "--unity_editor", unityEditor,
                        "--unity_bake_worker_queue", Path.Combine(libraryRoot, ".as_browser_cache", "unity_bake_worker"),
                        "--run_unity_bake",
                    };

                    // 原神这类库可显式指定从 Unity 对象恢复出的 Avatar asset；未配置时保持普通 Unity 项目的默认路径。
                    if (!string.IsNullOrWhiteSpace(unityAvatarAsset))
                    {
                        args.Add("--unity_avatar_asset");
                        args.Add(unityAvatarAsset);
                    }

                    return args.ToArray();
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
            Func<string, string, string, string, string, string, string, string, string[]> BuildUnityBakeArguments);

        private sealed record ExternalLauncher(
            string FileName,
            string WorkingDirectory,
            Func<string, string, string, string, string[]> BuildArguments);

    }

    internal sealed record AnimationPreviewStatus(string Status, string GltfPath, string ValidationPath, string Message);

    internal sealed record UnityBakeApplyStatus(
        string Status,
        int? FrameVaryingTracks,
        int? FrameVaryingChannels,
        string Message,
        bool? TrustedProductionBake,
        string AvatarTrustMessage);
}
