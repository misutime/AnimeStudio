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

            var outputDir = Path.GetDirectoryName(Path.GetFullPath(outputFbx));
            Directory.CreateDirectory(outputDir);
            var scriptPath = Path.Combine(Path.GetTempPath(), $"animestudio_blender_export_fbx_{Guid.NewGuid():N}.py");
            var reportPath = Path.Combine(outputDir, "blender_fbx_export_report.json");
            File.WriteAllText(scriptPath, BuildScript(inputGltf, outputFbx, reportPath), Encoding.UTF8);

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

            Logger.Info($"Blender FBX export: {outputFbx}");
            using var process = Process.Start(startInfo);
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            var ok = process.ExitCode == 0 && File.Exists(outputFbx);
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
                inputGltf = Path.GetFullPath(inputGltf),
                outputFbx = Path.GetFullPath(outputFbx),
                exitCode = process.ExitCode,
                blenderReport,
                stdoutTail = Tail(stdout),
                stderrTail = Tail(stderr),
            };
            File.WriteAllText(reportPath, JsonConvert.SerializeObject(processReport, Formatting.Indented), Encoding.UTF8);

            if (!ok)
            {
                TryDelete(scriptPath);
                Logger.Error($"Blender FBX export failed. See {reportPath}");
                return false;
            }

            TryDelete(scriptPath);
            Logger.Info($"Baked FBX preview: {outputFbx}");
            Logger.Info($"Blender FBX report: {reportPath}");
            return true;
        }

        private static string ResolveBlender(string blenderPath)
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
    }
}
