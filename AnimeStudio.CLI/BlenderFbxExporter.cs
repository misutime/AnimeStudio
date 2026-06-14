using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace AnimeStudio.CLI
{
    internal static class BlenderFbxExporter
    {
        public static bool Export(string inputGltf, string outputFbx, string blenderPath)
        {
            if (string.IsNullOrWhiteSpace(inputGltf) || !File.Exists(inputGltf))
            {
                Logger.Error($"Baked glTF not found for FBX export: {inputGltf}");
                return false;
            }
            if (string.IsNullOrWhiteSpace(outputFbx))
            {
                Logger.Error("--baked_fbx_output is empty.");
                return false;
            }

            blenderPath = ResolveBlender(blenderPath);
            if (string.IsNullOrWhiteSpace(blenderPath))
            {
                Logger.Error("Blender was not found. Pass --blender \"C:\\Program Files\\Blender Foundation\\Blender 5.1\\blender.exe\".");
                return false;
            }

            var originalInputGltf = Path.GetFullPath(inputGltf);
            var originalOutputFbx = Path.GetFullPath(outputFbx);
            var outputDir = Path.GetDirectoryName(originalOutputFbx);
            Directory.CreateDirectory(outputDir);
            var blenderInputGltf = originalInputGltf;
            var blenderOutputFbx = originalOutputFbx;
            var stagedInputDir = null as string;
            var stagedOutputDir = null as string;
            if (NeedsShortBlenderPath(blenderInputGltf))
            {
                stagedInputDir = Path.Combine(Path.GetTempPath(), $"as_gltf_in_{Guid.NewGuid():N}");
                CopyDirectory(Path.GetDirectoryName(originalInputGltf), stagedInputDir);
                blenderInputGltf = Path.Combine(stagedInputDir, Path.GetFileName(originalInputGltf));
                Logger.Info($"Blender input path is long; using temporary short path: {blenderInputGltf}");
            }
            if (NeedsShortBlenderPath(blenderOutputFbx))
            {
                stagedOutputDir = Path.Combine(Path.GetTempPath(), $"as_fbx_out_{Guid.NewGuid():N}");
                Directory.CreateDirectory(stagedOutputDir);
                blenderOutputFbx = Path.Combine(stagedOutputDir, Path.GetFileName(originalOutputFbx));
                Logger.Info($"Blender output path is long; using temporary short path: {blenderOutputFbx}");
            }

            var scriptPath = Path.Combine(Path.GetTempPath(), $"animestudio_blender_export_fbx_{Guid.NewGuid():N}.py");
            var reportPath = Path.Combine(outputDir, "blender_fbx_export_report.json");
            File.WriteAllText(scriptPath, BuildScript(blenderInputGltf, blenderOutputFbx, reportPath), Encoding.UTF8);

            var startInfo = new ProcessStartInfo
            {
                FileName = blenderPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--background");
            startInfo.ArgumentList.Add("--python");
            startInfo.ArgumentList.Add(scriptPath);

            Logger.Info($"Blender FBX export: {originalOutputFbx}");
            using var process = Process.Start(startInfo);
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode == 0 && !string.Equals(blenderOutputFbx, originalOutputFbx, StringComparison.OrdinalIgnoreCase) && File.Exists(blenderOutputFbx))
            {
                File.Copy(blenderOutputFbx, originalOutputFbx, true);
            }

            var ok = process.ExitCode == 0 && File.Exists(originalOutputFbx);
            object blenderReport = null;
            if (File.Exists(reportPath))
            {
                try
                {
                    blenderReport = JsonConvert.DeserializeObject(File.ReadAllText(reportPath));
                }
                catch
                {
                    blenderReport = null;
                }
            }
            var processReport = new
            {
                generatedAt = DateTime.UtcNow.ToString("O"),
                status = ok ? "ok" : "error",
                blender = blenderPath,
                inputGltf = originalInputGltf,
                outputFbx = originalOutputFbx,
                stagedInputGltf = string.Equals(blenderInputGltf, originalInputGltf, StringComparison.OrdinalIgnoreCase) ? null : blenderInputGltf,
                stagedOutputFbx = string.Equals(blenderOutputFbx, originalOutputFbx, StringComparison.OrdinalIgnoreCase) ? null : blenderOutputFbx,
                exitCode = process.ExitCode,
                blenderReport,
                stdoutTail = Tail(stdout),
                stderrTail = Tail(stderr),
            };
            File.WriteAllText(reportPath, JsonConvert.SerializeObject(processReport, Formatting.Indented), Encoding.UTF8);

            if (!ok)
            {
                TryDelete(scriptPath);
                TryDeleteDirectory(stagedInputDir);
                TryDeleteDirectory(stagedOutputDir);
                Logger.Error($"Blender FBX export failed. See {reportPath}");
                return false;
            }

            TryDelete(scriptPath);
            TryDeleteDirectory(stagedInputDir);
            TryDeleteDirectory(stagedOutputDir);
            Logger.Info($"Baked FBX preview: {originalOutputFbx}");
            Logger.Info($"Blender FBX report: {reportPath}");
            return true;
        }

        internal static string ResolveBlender(string blenderPath)
        {
            if (!string.IsNullOrWhiteSpace(blenderPath) && File.Exists(blenderPath))
            {
                return blenderPath;
            }

            var candidates = new[]
            {
                @"C:\Program Files\Blender Foundation\Blender 5.1\blender.exe",
                @"C:\Program Files\Blender Foundation\Blender 5.0\blender.exe",
                @"C:\Program Files\Blender Foundation\Blender 4.4\blender.exe",
                @"C:\Program Files\Blender Foundation\Blender 4.3\blender.exe",
                @"C:\Program Files\Blender Foundation\Blender 4.2\blender.exe",
                @"C:\Program Files\Blender Foundation\Blender\blender.exe",
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        private static string BuildScript(string inputGltf, string outputFbx, string reportPath)
        {
            var gltfLiteral = JsonConvert.SerializeObject(Path.GetFullPath(inputGltf));
            var fbxLiteral = JsonConvert.SerializeObject(Path.GetFullPath(outputFbx));
            var reportLiteral = JsonConvert.SerializeObject(Path.GetFullPath(reportPath));
            return $@"
import bpy, json

input_gltf = {gltfLiteral}
output_fbx = {fbxLiteral}
report_path = {reportLiteral}

bpy.ops.object.select_all(action='SELECT')
bpy.ops.object.delete()
bpy.ops.import_scene.gltf(filepath=input_gltf)

scene = bpy.context.scene
actions = [a.name for a in bpy.data.actions]
if bpy.data.actions:
    frame_min = 1
    frame_max = 1
    for action in bpy.data.actions:
        frame_min = min(frame_min, int(action.frame_range[0]))
        frame_max = max(frame_max, int(action.frame_range[1]))
    scene.frame_start = frame_min
    scene.frame_end = frame_max

for obj in scene.objects:
    obj.select_set(obj.type in {{'ARMATURE', 'MESH', 'EMPTY'}})

bpy.ops.export_scene.fbx(
    filepath=output_fbx,
    use_selection=True,
    object_types={{'ARMATURE', 'MESH', 'EMPTY'}},
    bake_anim=True,
    bake_anim_use_all_bones=True,
    bake_anim_use_nla_strips=False,
    bake_anim_use_all_actions=True,
    add_leaf_bones=False,
    path_mode='COPY',
    embed_textures=True,
)

report = {{
    'status': 'ok',
    'inputGltf': input_gltf,
    'outputFbx': output_fbx,
    'objects': len(scene.objects),
    'meshes': len([o for o in scene.objects if o.type == 'MESH']),
    'armatures': len([o for o in scene.objects if o.type == 'ARMATURE']),
    'actions': actions,
    'frameStart': scene.frame_start,
    'frameEnd': scene.frame_end,
}}
with open(report_path, 'w', encoding='utf-8') as f:
    json.dump(report, f, ensure_ascii=False, indent=2)
print('ANIMESTUDIO_BLENDER_FBX_EXPORT ' + json.dumps(report, ensure_ascii=False))
";
        }

        private static string Tail(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            const int max = 4000;
            return text.Length <= max ? text : text[^max..];
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Temporary script cleanup should not fail the export.
            }
        }

        private static bool NeedsShortBlenderPath(string path)
        {
            // Blender 的 Python/glTF 插件在部分 Windows 环境仍会卡在 MAX_PATH，长路径先转短临时目录。
            return !string.IsNullOrWhiteSpace(path) && path.Length >= 240;
        }

        private static void CopyDirectory(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);
            foreach (var directory in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(targetDir, Path.GetRelativePath(sourceDir, directory)));
            }
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(targetDir, Path.GetRelativePath(sourceDir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
                // 临时目录清理失败不影响 FBX 结果，后续系统临时目录会自然清理。
            }
        }
    }
}
