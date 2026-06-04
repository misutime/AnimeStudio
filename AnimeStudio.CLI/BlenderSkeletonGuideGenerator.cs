using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace AnimeStudio.CLI
{
    internal static class BlenderSkeletonGuideGenerator
    {
        public static bool Generate(string inputModel, string outputDir, string catalogPath, string blenderPath)
        {
            if (string.IsNullOrWhiteSpace(inputModel) || !File.Exists(inputModel))
            {
                Logger.Error($"Model not found for skeleton guide: {inputModel}");
                return false;
            }

            blenderPath = BlenderFbxExporter.ResolveBlender(blenderPath);
            if (string.IsNullOrWhiteSpace(blenderPath))
            {
                Logger.Error("Blender was not found. Pass --blender \"C:\\Program Files\\Blender Foundation\\Blender 5.1\\blender.exe\".");
                return false;
            }

            var inputFullPath = Path.GetFullPath(inputModel);
            var extension = Path.GetExtension(inputFullPath).ToLowerInvariant();
            if (extension != ".fbx" && extension != ".gltf" && extension != ".glb")
            {
                Logger.Error("--generate_skeleton_guide supports .fbx, .gltf, and .glb inputs.");
                return false;
            }
            outputDir = string.IsNullOrWhiteSpace(outputDir)
                ? Path.Combine(Path.GetDirectoryName(inputFullPath), "SkeletonGuide")
                : Path.GetFullPath(outputDir);
            Directory.CreateDirectory(outputDir);

            var modelName = Path.GetFileNameWithoutExtension(inputFullPath);
            var blendPath = Path.Combine(outputDir, $"{modelName}_core_skeleton_guide.blend");
            var canonicalCopy = Path.Combine(outputDir, $"{modelName}.canonical{extension}");
            var reportPath = Path.Combine(outputDir, "core_skeleton_guide_report.json");
            CopyCanonicalModel(inputFullPath, canonicalCopy, extension);

            var relation = ResolveSkeletonRelation(inputFullPath, catalogPath);
            var edges = relation.Edges.Count == 0 ? BuildFallbackBip001Edges() : relation.Edges;
            var scriptPath = Path.Combine(Path.GetTempPath(), $"animestudio_skeleton_guide_{Guid.NewGuid():N}.py");
            File.WriteAllText(scriptPath, BuildScript(inputFullPath, extension, blendPath, reportPath, canonicalCopy, edges, relation), Encoding.UTF8);

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

            Logger.Info($"Blender CoreHumanoid skeleton guide: {blendPath}");
            using var process = Process.Start(startInfo);
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            var ok = process.ExitCode == 0 && File.Exists(blendPath);
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
                inputModel = inputFullPath,
                outputBlend = blendPath,
                canonicalCopy,
                relationSource = relation.Source,
                catalog = relation.CatalogPath,
                edgeCount = edges.Count,
                exitCode = process.ExitCode,
                blenderReport,
                stdoutTail = Tail(stdout),
                stderrTail = Tail(stderr),
            };
            File.WriteAllText(reportPath, JsonConvert.SerializeObject(processReport, Formatting.Indented), Encoding.UTF8);

            TryDelete(scriptPath);
            if (!ok)
            {
                Logger.Error($"Skeleton guide generation failed. See {reportPath}");
                return false;
            }

            Logger.Info($"CoreHumanoid skeleton guide: {blendPath}");
            Logger.Info($"Skeleton guide report: {reportPath}");
            return true;
        }

        private static SkeletonRelation ResolveSkeletonRelation(string inputModel, string catalogPath)
        {
            catalogPath = ResolveCatalogPath(inputModel, catalogPath);
            if (string.IsNullOrWhiteSpace(catalogPath))
            {
                return new SkeletonRelation("fallback_bip001", null, new List<SkeletonEdge>());
            }

            var inputFullPath = Path.GetFullPath(inputModel);
            var inputName = Path.GetFileNameWithoutExtension(inputFullPath);
            JObject best = null;
            foreach (var line in File.ReadLines(catalogPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                JObject entry;
                try
                {
                    entry = JObject.Parse(line);
                }
                catch
                {
                    continue;
                }
                if ((string)entry["kind"] != "Model")
                {
                    continue;
                }
                var output = (string)entry["output"];
                var name = (string)entry["name"];
                if (!string.IsNullOrWhiteSpace(output) &&
                    string.Equals(Path.GetFullPath(output), inputFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    best = entry;
                    break;
                }
                if (best == null && string.Equals(name, inputName, StringComparison.OrdinalIgnoreCase))
                {
                    best = entry;
                }
            }

            if (best == null)
            {
                return new SkeletonRelation("fallback_bip001", catalogPath, new List<SkeletonEdge>());
            }

            var humanBones = ParseHumanBoneMap(best["avatar"]?["humanBones"] as JArray);
            var edges = BuildAvatarEdges(humanBones);
            if (edges.Count > 0)
            {
                return new SkeletonRelation("unity_avatar_human_description", catalogPath, edges);
            }

            edges = BuildValidationChainEdges(best["skeletonValidation"]?["chains"] as JArray);
            return new SkeletonRelation(
                edges.Count == 0 ? "fallback_bip001" : "unity_skeleton_validation_chains",
                catalogPath,
                edges
            );
        }

        private static string ResolveCatalogPath(string inputModel, string catalogPath)
        {
            if (!string.IsNullOrWhiteSpace(catalogPath) && File.Exists(catalogPath))
            {
                return Path.GetFullPath(catalogPath);
            }

            var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(inputModel)));
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "asset_catalog.jsonl");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                dir = dir.Parent;
            }
            return null;
        }

        private static void CopyCanonicalModel(string inputFullPath, string canonicalCopy, string extension)
        {
            if (!File.Exists(canonicalCopy))
            {
                File.Copy(inputFullPath, canonicalCopy, false);
            }

            if (extension != ".gltf")
            {
                return;
            }

            JObject gltf;
            try
            {
                gltf = JObject.Parse(File.ReadAllText(inputFullPath));
            }
            catch
            {
                return;
            }

            var inputDir = Path.GetDirectoryName(inputFullPath);
            var outputDir = Path.GetDirectoryName(canonicalCopy);
            var uris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var uri in gltf["buffers"]?.Children<JObject>().Select(x => (string)x["uri"]) ?? Enumerable.Empty<string>())
            {
                uris.Add(uri);
            }
            foreach (var uri in gltf["images"]?.Children<JObject>().Select(x => (string)x["uri"]) ?? Enumerable.Empty<string>())
            {
                uris.Add(uri);
            }

            foreach (var uri in uris)
            {
                if (string.IsNullOrWhiteSpace(uri)
                    || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    || Uri.TryCreate(uri, UriKind.Absolute, out _))
                {
                    continue;
                }

                var relativePath = uri.Replace('/', Path.DirectorySeparatorChar);
                var source = Path.GetFullPath(Path.Combine(inputDir, relativePath));
                if (!File.Exists(source))
                {
                    continue;
                }

                var target = Path.GetFullPath(Path.Combine(outputDir, relativePath));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if (!File.Exists(target))
                {
                    File.Copy(source, target, false);
                }
            }
        }

        private static Dictionary<string, string> ParseHumanBoneMap(JArray humanBones)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (humanBones == null)
            {
                return map;
            }
            foreach (var item in humanBones.Values<string>())
            {
                var separator = item.IndexOf(':');
                if (separator <= 0 || separator >= item.Length - 1)
                {
                    continue;
                }
                map[item[..separator]] = item[(separator + 1)..];
            }
            return map;
        }

        private static List<SkeletonEdge> BuildAvatarEdges(Dictionary<string, string> map)
        {
            var edges = new List<SkeletonEdge>();
            AddChain(edges, map, "Hips", "Spine", "Chest", "UpperChest", "Neck", "Head");
            var shoulderRoot = map.ContainsKey("UpperChest") ? "UpperChest" : "Chest";
            AddChain(edges, map, shoulderRoot, "LeftShoulder", "LeftUpperArm", "LeftLowerArm", "LeftHand");
            AddChain(edges, map, shoulderRoot, "RightShoulder", "RightUpperArm", "RightLowerArm", "RightHand");
            AddChain(edges, map, "Hips", "LeftUpperLeg", "LeftLowerLeg", "LeftFoot", "LeftToes");
            AddChain(edges, map, "Hips", "RightUpperLeg", "RightLowerLeg", "RightFoot", "RightToes");
            return edges
                .GroupBy(edge => $"{edge.From}\n{edge.To}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        private static void AddChain(List<SkeletonEdge> edges, Dictionary<string, string> map, params string[] humanBones)
        {
            string previous = null;
            foreach (var humanBone in humanBones)
            {
                if (!map.TryGetValue(humanBone, out var skeletonBone) || string.IsNullOrWhiteSpace(skeletonBone))
                {
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(previous) &&
                    !string.Equals(previous, skeletonBone, StringComparison.OrdinalIgnoreCase))
                {
                    edges.Add(new SkeletonEdge(previous, skeletonBone));
                }
                previous = skeletonBone;
            }
        }

        private static List<SkeletonEdge> BuildValidationChainEdges(JArray chains)
        {
            var edges = new List<SkeletonEdge>();
            if (chains == null)
            {
                return edges;
            }
            foreach (var chain in chains.OfType<JObject>())
            {
                var mapped = chain["mappedBones"] as JArray;
                if (mapped == null)
                {
                    continue;
                }
                var bones = mapped.Values<string>()
                    .Select(value =>
                    {
                        var separator = value.IndexOf(':');
                        return separator >= 0 && separator < value.Length - 1 ? value[(separator + 1)..] : value;
                    })
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToArray();
                for (var i = 0; i < bones.Length - 1; i++)
                {
                    edges.Add(new SkeletonEdge(bones[i], bones[i + 1]));
                }
            }
            return edges;
        }

        private static List<SkeletonEdge> BuildFallbackBip001Edges() => new()
        {
            new("Bip001 Pelvis", "Bip001 Spine"),
            new("Bip001 Spine", "Bip001 Spine1"),
            new("Bip001 Spine1", "Bip001 Spine2"),
            new("Bip001 Spine2", "Bip001 Neck"),
            new("Bip001 Neck", "Bip001 Head"),
            new("Bip001 Spine2", "Bip001 L Clavicle"),
            new("Bip001 L Clavicle", "Bip001 L UpperArm"),
            new("Bip001 L UpperArm", "Bip001 L Forearm"),
            new("Bip001 L Forearm", "Bip001 L Hand"),
            new("Bip001 Spine2", "Bip001 R Clavicle"),
            new("Bip001 R Clavicle", "Bip001 R UpperArm"),
            new("Bip001 R UpperArm", "Bip001 R Forearm"),
            new("Bip001 R Forearm", "Bip001 R Hand"),
            new("Bip001 Pelvis", "Bip001 L Thigh"),
            new("Bip001 L Thigh", "Bip001 L Calf"),
            new("Bip001 L Calf", "Bip001 L Foot"),
            new("Bip001 L Foot", "Bip001 L Toe0"),
            new("Bip001 Pelvis", "Bip001 R Thigh"),
            new("Bip001 R Thigh", "Bip001 R Calf"),
            new("Bip001 R Calf", "Bip001 R Foot"),
            new("Bip001 R Foot", "Bip001 R Toe0"),
        };

        private static string BuildScript(
            string inputFbx,
            string extension,
            string blendPath,
            string reportPath,
            string canonicalCopy,
            IReadOnlyList<SkeletonEdge> edges,
            SkeletonRelation relation)
        {
            var modelLiteral = JsonConvert.SerializeObject(inputFbx);
            var extensionLiteral = JsonConvert.SerializeObject(extension);
            var blendLiteral = JsonConvert.SerializeObject(blendPath);
            var reportLiteral = JsonConvert.SerializeObject(reportPath);
            var copyLiteral = JsonConvert.SerializeObject(canonicalCopy);
            var edgesLiteral = JsonConvert.SerializeObject(edges.Select(edge => new[] { edge.From, edge.To }));
            var relationLiteral = JsonConvert.SerializeObject(relation.Source);
            var catalogLiteral = JsonConvert.SerializeObject(relation.CatalogPath);
            return $@"
import bpy, json

input_model = {modelLiteral}
input_extension = {extensionLiteral}
output_blend = {blendLiteral}
report_path = {reportLiteral}
canonical_copy = {copyLiteral}
edges = {edgesLiteral}
relation_source = {relationLiteral}
catalog_path = {catalogLiteral}

bpy.ops.object.select_all(action='SELECT')
bpy.ops.object.delete()
if input_extension == '.fbx':
    bpy.ops.import_scene.fbx(filepath=input_model, use_custom_normals=True)
elif input_extension in {{'.gltf', '.glb'}}:
    bpy.ops.import_scene.gltf(filepath=input_model)
else:
    raise RuntimeError('Unsupported skeleton guide input: ' + input_model)

arm = next((o for o in bpy.context.scene.objects if o.type == 'ARMATURE'), None)
if arm is None:
    raise RuntimeError('No armature found')

arm.hide_viewport = True
arm.hide_render = True

coll = bpy.data.collections.new('AnimeStudio_CoreHumanoid_Guide_VISIBLE')
bpy.context.scene.collection.children.link(coll)

line_mat = bpy.data.materials.new('AnimeStudio_CoreHumanoid_Red_Visible')
line_mat.diffuse_color = (1.0, 0.0, 0.0, 1.0)
line_mat.use_nodes = True
bsdf = line_mat.node_tree.nodes.get('Principled BSDF')
if bsdf:
    for name, value in [
        ('Base Color', (1.0, 0.0, 0.0, 1.0)),
        ('Emission Color', (1.0, 0.0, 0.0, 1.0)),
        ('Emission Strength', 1.5),
        ('Roughness', 0.35),
    ]:
        if name in bsdf.inputs:
            bsdf.inputs[name].default_value = value

joint_mat = bpy.data.materials.new('AnimeStudio_CoreHumanoid_Joint_Yellow')
joint_mat.diffuse_color = (1.0, 0.82, 0.0, 1.0)
joint_mat.use_nodes = True
bsdf = joint_mat.node_tree.nodes.get('Principled BSDF')
if bsdf:
    for name, value in [
        ('Base Color', (1.0, 0.82, 0.0, 1.0)),
        ('Emission Color', (1.0, 0.65, 0.0, 1.0)),
        ('Emission Strength', 1.2),
    ]:
        if name in bsdf.inputs:
            bsdf.inputs[name].default_value = value

def head_world(name):
    bone = arm.data.bones.get(name)
    if bone is None:
        return None
    return arm.matrix_world @ bone.head_local

def link_to_guide(obj):
    for collection in list(obj.users_collection):
        collection.objects.unlink(obj)
    coll.objects.link(obj)
    obj.show_in_front = True

def make_cylinder_between(name, a, b, radius=0.018):
    direction = b - a
    length = direction.length
    if length <= 0.0001:
        return None
    midpoint = (a + b) * 0.5
    bpy.ops.mesh.primitive_cylinder_add(vertices=16, radius=radius, depth=length, location=midpoint)
    obj = bpy.context.object
    obj.name = name
    obj.data.name = name + '_mesh'
    obj.rotation_euler = direction.to_track_quat('Z', 'Y').to_euler()
    obj.data.materials.append(line_mat)
    link_to_guide(obj)
    return obj

created = []
missing = []
points = {{}}
for start, end in edges:
    a = head_world(start)
    b = head_world(end)
    if a is None or b is None:
        missing.append([start, end])
        continue
    make_cylinder_between('CORE_LINE ' + start + ' -> ' + end, a, b)
    created.append({{'from': start, 'to': end, 'length': round((b - a).length, 4)}})
    points[start] = a
    points[end] = b

for name, point in points.items():
    bpy.ops.mesh.primitive_uv_sphere_add(segments=16, ring_count=8, radius=0.035, location=point)
    obj = bpy.context.object
    obj.name = 'CORE_JOINT_VISIBLE ' + name
    obj.data.materials.append(joint_mat)
    link_to_guide(obj)

bpy.ops.object.light_add(type='AREA', location=(0, -3.5, 3.5))
light = bpy.context.object
light.name = 'AnimeStudio Preview Area Light'
light.data.energy = 600
light.data.size = 5
bpy.ops.object.camera_add(location=(0, -4.0, 1.35), rotation=(1.5708, 0, 0))
bpy.context.scene.camera = bpy.context.object
bpy.context.scene.unit_settings.system = 'METRIC'

report = {{
    'status': 'ok',
    'inputModel': input_model,
    'outputBlend': output_blend,
    'canonicalCopy': canonical_copy,
    'mode': 'non_destructive_visible_core_humanoid_guide',
    'relationSource': relation_source,
    'catalog': catalog_path,
    'originalArmatureHidden': True,
    'createdEdgeCount': len(created),
    'createdJointCount': len(points),
    'missingEdgeCount': len(missing),
    'createdEdges': created,
    'missingEdges': missing,
    'armatureBoneCount': len(arm.data.bones),
    'meshObjects': [
        {{'name': obj.name, 'verts': len(obj.data.vertices), 'polys': len(obj.data.polygons)}}
        for obj in bpy.context.scene.objects
        if obj.type == 'MESH' and not obj.name.startswith('CORE_')
    ],
    'note': 'Canonical model file is copied but not re-exported. Inspect red tubes and yellow spheres for CoreHumanoid structure.',
}}
with open(report_path, 'w', encoding='utf-8') as f:
    json.dump(report, f, ensure_ascii=False, indent=2)
bpy.ops.wm.save_as_mainfile(filepath=output_blend)
print('ANIMESTUDIO_CORE_SKELETON_GUIDE ' + json.dumps(report, ensure_ascii=False))
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
                // Temporary script cleanup should not fail the guide generation.
            }
        }

        private sealed record SkeletonEdge(string From, string To);

        private sealed record SkeletonRelation(string Source, string CatalogPath, List<SkeletonEdge> Edges);
    }
}
