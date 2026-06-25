using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AnimeStudio.CLI
{
    internal static class NarakaAvatarMeshExportPlanner
    {
        public static string WritePlan(string sourceIndexPath, string selector, string outputFolder, string sourceRoot)
        {
            if (string.IsNullOrWhiteSpace(sourceIndexPath) || !File.Exists(sourceIndexPath))
            {
                throw new FileNotFoundException("unity_source_index.db not found.", sourceIndexPath);
            }
            if (string.IsNullOrWhiteSpace(selector))
            {
                throw new ArgumentException("--export_naraka_avatar_mesh_plan requires --preview_model or --names to select one Naraka GameObject.");
            }

            outputFolder = string.IsNullOrWhiteSpace(outputFolder)
                ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sourceIndexPath)) ?? Directory.GetCurrentDirectory(), "NarakaAvatarMeshPlan_" + FixFileName(selector))
                : outputFolder;
            Directory.CreateDirectory(outputFolder);

            SQLitePCL.Batteries_V2.Init();
            using var connection = new SqliteConnection($"Data Source={sourceIndexPath};Mode=ReadOnly");
            connection.Open();

            var target = FindTarget(connection, selector);
            var parts = LoadLod0Parts(connection, target).ToList();
            if (parts.Count == 0)
            {
                throw new InvalidDataException($"No lod0RendererAssistants -> avatarMeshAsset chain found for Naraka GameObject selector: {selector}");
            }

            foreach (var part in parts)
            {
                part.Materials.AddRange(LoadMaterials(connection, part.RendererFile, part.RendererPathId));
                foreach (var material in part.Materials)
                {
                    material.Textures.AddRange(LoadTextures(connection, material.MaterialFile, material.MaterialPathId));
                }
            }

            var monoObjects = new List<SourceObjectRef>
            {
                new(target.VisualCellName, target.VisualCellSourcePath, target.VisualCellFile, target.VisualCellPathId, "ActorBodyVisualCell")
            };
            monoObjects.AddRange(parts.Select(x => new SourceObjectRef(x.AssistantName, x.AssistantSourcePath, x.AssistantFile, x.AssistantPathId, "LXRendererAssistant")));
            monoObjects.AddRange(parts.Select(x => new SourceObjectRef(x.AvatarMeshName, x.AvatarMeshSourcePath, x.AvatarMeshFile, x.AvatarMeshPathId, "AvatarMeshDataAsset")));
            monoObjects = DistinctObjects(monoObjects).ToList();

            var materialObjects = parts
                .SelectMany(x => x.Materials.Select(y => new SourceObjectRef(y.MaterialName, y.MaterialSourcePath, y.MaterialFile, y.MaterialPathId, "Material")))
                .Concat(parts.SelectMany(x => x.Materials.SelectMany(y => y.Textures.Select(z => new SourceObjectRef(z.TextureName, z.TextureSourcePath, z.TextureFile, z.TexturePathId, "Texture2D")))))
                .Where(x => x.PathId != 0)
                .ToList();
            materialObjects = DistinctObjects(materialObjects).ToList();

            var dumpRoot = Path.Combine(outputFolder, "TypeTreeDump");
            var plan = new JObject
            {
                ["generatedAt"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["kind"] = "NarakaAvatarMeshDataExportPlan",
                ["sourceIndex"] = Path.GetFullPath(sourceIndexPath),
                ["sourceRoot"] = string.IsNullOrWhiteSpace(sourceRoot) ? null : Path.GetFullPath(sourceRoot),
                ["selector"] = selector,
                ["outputFolder"] = Path.GetFullPath(outputFolder),
                ["typeTreeDump"] = dumpRoot,
                ["rule"] = "只根据 unity_source_index.db 中的 ActorBodyVisualCell.lod0RendererAssistants -> LXRendererAssistant.avatarMeshAsset、_renderer、renderer.material、material.texture 确定导出闭包；不按名字猜部件、材质或贴图。",
                ["target"] = target.ToJson(),
                ["monoBehaviourExport"] = BuildExportBlock(monoObjects, sourceRoot),
                ["materialTextureExport"] = BuildExportBlock(materialObjects, sourceRoot),
                ["lod0Parts"] = new JArray(parts.Select(x => x.ToJson())),
            };
            plan["commands"] = BuildCommands(sourceIndexPath, sourceRoot, outputFolder, dumpRoot, monoObjects, materialObjects);

            var planPath = Path.Combine(outputFolder, "naraka_avatar_mesh_export_plan.json");
            File.WriteAllText(planPath, plan.ToString(Formatting.Indented));
            var scriptPath = Path.Combine(outputFolder, "naraka_avatar_mesh_export_commands.ps1");
            File.WriteAllText(scriptPath, BuildPowerShellScript(outputFolder, plan["commands"] as JObject));

            Logger.Info($"Wrote Naraka AvatarMeshData export plan: {planPath}");
            Logger.Info($"Wrote Naraka AvatarMeshData export commands: {scriptPath}");
            return planPath;
        }

        private static TargetSelection FindTarget(SqliteConnection connection, string selector)
        {
            var pathIdMode = long.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out var selectorPathId);
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT go.name,
       go.source_path,
       go.serialized_file,
       go.path_id,
       cell.name,
       cell.source_path,
       cell.serialized_file,
       cell.path_id
FROM source_objects go
JOIN source_relations componentRel INDEXED BY idx_source_relations_to
  ON componentRel.to_file = go.serialized_file
 AND componentRel.to_path_id = go.path_id
 AND componentRel.relation = 'component.gameObject'
JOIN source_objects cell
  ON cell.serialized_file = componentRel.from_file
 AND cell.path_id = componentRel.from_path_id
 AND cell.type = 'MonoBehaviour'
WHERE go.type = 'GameObject'
  AND ((go.name = $selector COLLATE NOCASE) OR ($pathIdMode = 1 AND go.path_id = $pathId))
  AND EXISTS (
      SELECT 1
      FROM source_relations assistantRel INDEXED BY idx_source_relations_from
      WHERE assistantRel.from_file = cell.serialized_file
        AND assistantRel.from_path_id = cell.path_id
        AND assistantRel.relation = 'monoBehaviour.pptr'
        AND json_extract(assistantRel.raw_json, '$.details.path') = 'lod0RendererAssistants.data'
  )
ORDER BY go.name COLLATE NOCASE, cell.path_id
LIMIT 1;";
            command.Parameters.AddWithValue("$selector", selector);
            command.Parameters.AddWithValue("$pathIdMode", pathIdMode ? 1 : 0);
            command.Parameters.AddWithValue("$pathId", selectorPathId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidDataException($"No Naraka ActorBodyVisualCell GameObject found for selector: {selector}");
            }

            return new TargetSelection
            {
                GameObjectName = reader.GetString(0),
                GameObjectSourcePath = reader.GetString(1),
                GameObjectFile = reader.GetString(2),
                GameObjectPathId = reader.GetInt64(3),
                VisualCellName = reader.GetString(4),
                VisualCellSourcePath = reader.GetString(5),
                VisualCellFile = reader.GetString(6),
                VisualCellPathId = reader.GetInt64(7),
            };
        }

        private static IEnumerable<Lod0Part> LoadLod0Parts(SqliteConnection connection, TargetSelection target)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT assistantRel.id,
       assistant.name,
       assistant.source_path,
       assistant.serialized_file,
       assistant.path_id,
       mesh.name,
       mesh.source_path,
       mesh.serialized_file,
       mesh.path_id,
       renderer.to_file,
       renderer.to_path_id
FROM source_relations assistantRel INDEXED BY idx_source_relations_from
JOIN source_objects assistant
  ON assistant.serialized_file = assistantRel.to_file
 AND assistant.path_id = assistantRel.to_path_id
 AND assistant.type = 'MonoBehaviour'
JOIN source_relations avatarMesh INDEXED BY idx_source_relations_from
  ON avatarMesh.from_file = assistant.serialized_file
 AND avatarMesh.from_path_id = assistant.path_id
 AND avatarMesh.relation = 'monoBehaviour.pptr'
 AND json_extract(avatarMesh.raw_json, '$.details.path') = 'avatarMeshAsset'
JOIN source_objects mesh
  ON mesh.serialized_file = avatarMesh.to_file
 AND mesh.path_id = avatarMesh.to_path_id
 AND mesh.type = 'MonoBehaviour'
LEFT JOIN source_relations renderer INDEXED BY idx_source_relations_from
  ON renderer.from_file = assistant.serialized_file
 AND renderer.from_path_id = assistant.path_id
 AND renderer.relation = 'monoBehaviour.pptr'
 AND json_extract(renderer.raw_json, '$.details.path') = '_renderer'
WHERE assistantRel.from_file = $cellFile
  AND assistantRel.from_path_id = $cellPathId
  AND assistantRel.relation = 'monoBehaviour.pptr'
  AND json_extract(assistantRel.raw_json, '$.details.path') = 'lod0RendererAssistants.data'
ORDER BY assistantRel.id;";
            command.Parameters.AddWithValue("$cellFile", target.VisualCellFile);
            command.Parameters.AddWithValue("$cellPathId", target.VisualCellPathId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                yield return new Lod0Part
                {
                    RelationId = reader.GetInt64(0),
                    AssistantName = reader.GetString(1),
                    AssistantSourcePath = reader.GetString(2),
                    AssistantFile = reader.GetString(3),
                    AssistantPathId = reader.GetInt64(4),
                    AvatarMeshName = reader.GetString(5),
                    AvatarMeshSourcePath = reader.GetString(6),
                    AvatarMeshFile = reader.GetString(7),
                    AvatarMeshPathId = reader.GetInt64(8),
                    RendererFile = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                    RendererPathId = reader.IsDBNull(10) ? 0 : reader.GetInt64(10),
                };
            }
        }

        private static IEnumerable<MaterialRef> LoadMaterials(SqliteConnection connection, string rendererFile, long rendererPathId)
        {
            if (string.IsNullOrWhiteSpace(rendererFile) || rendererPathId == 0)
            {
                yield break;
            }

            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT material.id,
       material.to_file,
       material.to_path_id
FROM source_relations material INDEXED BY idx_source_relations_from
WHERE material.from_file = $rendererFile
  AND material.from_path_id = $rendererPathId
  AND material.relation = 'renderer.material'
ORDER BY material.id;";
            command.Parameters.AddWithValue("$rendererFile", NormalizeSerializedFileName(rendererFile));
            command.Parameters.AddWithValue("$rendererPathId", rendererPathId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var materialFile = reader.GetString(1);
                var materialPathId = reader.GetInt64(2);
                var materialObject = ResolveSourceObject(connection, materialFile, materialPathId);
                yield return new MaterialRef
                {
                    RelationId = reader.GetInt64(0),
                    MaterialFile = materialFile,
                    MaterialPathId = materialPathId,
                    MaterialName = materialObject.Name,
                    MaterialSourcePath = materialObject.SourcePath,
                };
            }
        }

        private static IEnumerable<TextureRef> LoadTextures(SqliteConnection connection, string materialFile, long materialPathId)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT texture.id,
       texture.to_file,
       texture.to_path_id,
       json_extract(texture.raw_json, '$.details.path')
FROM source_relations texture INDEXED BY idx_source_relations_from
WHERE texture.from_file = $materialFile
  AND texture.from_path_id = $materialPathId
  AND texture.relation = 'material.texture'
ORDER BY texture.id;";
            command.Parameters.AddWithValue("$materialFile", NormalizeSerializedFileName(materialFile));
            command.Parameters.AddWithValue("$materialPathId", materialPathId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var textureFile = reader.GetString(1);
                var texturePathId = reader.GetInt64(2);
                var textureObject = ResolveSourceObject(connection, textureFile, texturePathId);
                yield return new TextureRef
                {
                    RelationId = reader.GetInt64(0),
                    TextureFile = textureFile,
                    TexturePathId = texturePathId,
                    TextureName = textureObject.Name,
                    TextureSourcePath = textureObject.SourcePath,
                    Slot = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                };
            }
        }

        private static SourceObjectLite ResolveSourceObject(SqliteConnection connection, string serializedFile, long pathId)
        {
            foreach (var file in new[] { NormalizeSerializedFileName(serializedFile), serializedFile }.Distinct(StringComparer.Ordinal))
            {
                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT name, source_path
FROM source_objects INDEXED BY idx_source_objects_file_path
WHERE serialized_file = $file
  AND path_id = $pathId
LIMIT 1;";
                command.Parameters.AddWithValue("$file", file ?? string.Empty);
                command.Parameters.AddWithValue("$pathId", pathId);
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    return new SourceObjectLite(
                        reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                        reader.IsDBNull(1) ? string.Empty : reader.GetString(1));
                }
            }

            return new SourceObjectLite(string.Empty, string.Empty);
        }

        private static JObject BuildExportBlock(IReadOnlyList<SourceObjectRef> objects, string sourceRoot)
        {
            var sourceFiles = objects
                .Select(x => PhysicalSourcePath(x.SourcePath))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new JObject
            {
                ["objectCount"] = objects.Count,
                ["sourceFiles"] = new JArray(sourceFiles),
                ["sourceFilesRelativeToPreviewSourceRoot"] = new JArray(sourceFiles.Select(x => ToSourceFilterPath(sourceRoot, x)).Where(x => !string.IsNullOrWhiteSpace(x))),
                ["pathIds"] = new JArray(objects.Select(x => x.PathId).Distinct().OrderBy(x => x)),
                ["objects"] = new JArray(objects.Select(x => x.ToJson())),
            };
        }

        private static JObject BuildCommands(
            string sourceIndexPath,
            string sourceRoot,
            string outputFolder,
            string dumpRoot,
            IReadOnlyList<SourceObjectRef> monoObjects,
            IReadOnlyList<SourceObjectRef> materialObjects)
        {
            var commands = new JObject();
            if (string.IsNullOrWhiteSpace(sourceRoot))
            {
                commands["note"] = "Pass --preview_source_root to generate runnable export commands with relative --source_files.";
                return commands;
            }

            var steps = new JArray();
            foreach (var step in BuildExportSteps("dumpMonoBehaviours", sourceRoot, dumpRoot, sourceIndexPath, "MonoBehaviour", monoObjects))
            {
                steps.Add(step.ToJson());
            }
            if (materialObjects.Count > 0)
            {
                foreach (var step in BuildExportSteps("exportMaterialsAndTextures", sourceRoot, outputFolder, sourceIndexPath, "Material Texture2D", materialObjects))
                {
                    steps.Add(step.ToJson());
                }
            }
            var exportGltf = string.Join(" ",
                CliExe(),
                "--export_avatar_mesh_data_gltf",
                PsQuote(Path.Combine(dumpRoot, "MonoBehaviour")),
                "--preview_output",
                PsQuote(outputFolder),
                "--source_index",
                PsQuote(sourceIndexPath));
            steps.Add(new PlanStep("exportGltf", exportGltf, null, Array.Empty<long>()).ToJson());
            var buildSqliteIndex = string.Join(" ",
                CliExe(),
                "--build_sqlite_index",
                PsQuote(outputFolder),
                "--source_index",
                PsQuote(sourceIndexPath),
                "--game Naraka",
                "--skip_sqlite_json_documents");
            steps.Add(new PlanStep("buildSqliteIndex", buildSqliteIndex, null, Array.Empty<long>()).ToJson());

            commands["steps"] = steps;
            commands["dumpMonoBehaviours"] = string.Join(Environment.NewLine, steps.OfType<JObject>()
                .Where(x => ((string)x["name"])?.StartsWith("dumpMonoBehaviours", StringComparison.OrdinalIgnoreCase) == true)
                .Select(x => (string)x["command"]));
            commands["exportMaterialsAndTextures"] = string.Join(Environment.NewLine, steps.OfType<JObject>()
                .Where(x => ((string)x["name"])?.StartsWith("exportMaterialsAndTextures", StringComparison.OrdinalIgnoreCase) == true)
                .Select(x => (string)x["command"]));
            commands["exportGltf"] = exportGltf;
            commands["buildSqliteIndex"] = buildSqliteIndex;
            return commands;
        }

        private static IEnumerable<PlanStep> BuildExportSteps(
            string stepPrefix,
            string sourceRoot,
            string outputFolder,
            string sourceIndexPath,
            string types,
            IReadOnlyList<SourceObjectRef> objects)
        {
            var groups = objects
                .Where(x => x.PathId != 0)
                .GroupBy(x => ToSourceFilterPath(sourceRoot, x.SourcePath), StringComparer.OrdinalIgnoreCase)
                .Where(x => !string.IsNullOrWhiteSpace(x.Key))
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var index = 0;
            foreach (var group in groups)
            {
                index++;
                var pathIds = group
                    .Select(x => x.PathId)
                    .Distinct()
                    .OrderBy(x => x)
                    .ToArray();
                var command = string.Join(" ",
                    CliExe(),
                    PsQuote(sourceRoot),
                    PsQuote(outputFolder),
                    "--game Naraka",
                    "--mode Export",
                    "--source_files",
                    PsQuote(group.Key),
                    "--types",
                    types,
                    "--path_ids",
                    string.Join(" ", pathIds.Select(x => x.ToString(CultureInfo.InvariantCulture))),
                    "--export_type Convert",
                    "--group_assets ByType",
                    "--source_index",
                    PsQuote(sourceIndexPath));

                yield return new PlanStep(
                    $"{stepPrefix}_{index:D2}_{FixStepName(group.Key)}",
                    command,
                    group.Key,
                    pathIds);
            }
        }

        private static string BuildPowerShellScript(string outputFolder, JObject commands)
        {
            var lines = new List<string>
            {
                "$ErrorActionPreference = 'Stop'",
                "# 这些命令只服务 Naraka 自定义网格诊断；产物仍是 warning，不能直接进入动画验收。",
                "$StateDir = " + PsQuote(Path.Combine(outputFolder, ".naraka_plan_state")),
                "New-Item -ItemType Directory -Force -Path $StateDir | Out-Null",
                "function Invoke-PlanStep([string]$Name, [string]$Command) {",
                "  $Marker = Join-Path $StateDir ($Name + '.done')",
                "  if (Test-Path -LiteralPath $Marker) { Write-Host \"[skip] $Name\"; return }",
                "  Write-Host \"[run] $Name\"",
                "  Invoke-Expression $Command",
                "  if ($LASTEXITCODE -ne 0) { throw \"Step failed: $Name exit=$LASTEXITCODE\" }",
                "  Set-Content -LiteralPath $Marker -Value (Get-Date).ToUniversalTime().ToString('O')",
                "}",
            };
            var steps = commands?["steps"]?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
            foreach (var step in steps)
            {
                var name = (string)step["name"];
                var command = (string)step["command"];
                if (!string.IsNullOrWhiteSpace(command))
                {
                    lines.Add("");
                    lines.Add("# " + name);
                    lines.Add("Invoke-PlanStep " + PsQuote(name) + " " + PsQuote(command));
                }
            }
            return string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }

        private static string FixStepName(string value)
        {
            value = (value ?? string.Empty).Replace('\\', '/').Trim('/');
            foreach (var c in Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '|', ' ' }).Distinct())
            {
                value = value.Replace(c, '_');
            }
            return string.IsNullOrWhiteSpace(value) ? "source" : value;
        }

        private static IEnumerable<SourceObjectRef> DistinctObjects(IEnumerable<SourceObjectRef> objects)
        {
            return objects
                .Where(x => x != null && x.PathId != 0)
                .GroupBy(x => $"{x.SerializedFile}#{x.PathId}", StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First());
        }

        private static string ToSourceFilterPath(string sourceRoot, string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourceRoot) || string.IsNullOrWhiteSpace(sourcePath))
            {
                return null;
            }

            sourcePath = PhysicalSourcePath(sourcePath);
            if (!Path.IsPathRooted(sourcePath))
            {
                return sourcePath.Replace('\\', '/');
            }

            var fullRoot = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullSource = Path.GetFullPath(sourcePath);
            if (!fullSource.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return fullSource.Replace('\\', '/');
            }

            return Path.GetRelativePath(fullRoot, fullSource).Replace('\\', '/');
        }

        private static string PhysicalSourcePath(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return sourcePath;
            }

            var separator = sourcePath.IndexOf('|');
            return separator >= 0 ? sourcePath.Substring(0, separator) : sourcePath;
        }

        private static string NormalizeSerializedFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            return value.StartsWith("cab-", StringComparison.OrdinalIgnoreCase)
                ? "CAB-" + value.Substring(4)
                : value;
        }

        private static string CliExe()
        {
            return ".\\AnimeStudio.CLI\\bin\\Debug\\net8.0-windows\\AnimeStudio.CLI.exe";
        }

        private static string PsQuote(string value)
        {
            return "'" + (value ?? string.Empty).Replace("'", "''") + "'";
        }

        private static string FixFileName(string value)
        {
            value = string.IsNullOrWhiteSpace(value) ? "naraka_avatar_mesh" : value.Trim();
            value = Path.GetInvalidFileNameChars().Aggregate(value, (current, c) => current.Replace(c, '_'));
            return value.Length == 0 ? "naraka_avatar_mesh" : value;
        }

        private sealed class TargetSelection
        {
            public string GameObjectName { get; init; }
            public string GameObjectSourcePath { get; init; }
            public string GameObjectFile { get; init; }
            public long GameObjectPathId { get; init; }
            public string VisualCellName { get; init; }
            public string VisualCellSourcePath { get; init; }
            public string VisualCellFile { get; init; }
            public long VisualCellPathId { get; init; }

            public JObject ToJson() => new()
            {
                ["gameObjectName"] = GameObjectName,
                ["gameObjectSourcePath"] = GameObjectSourcePath,
                ["gameObjectFile"] = GameObjectFile,
                ["gameObjectPathId"] = GameObjectPathId,
                ["visualCellName"] = VisualCellName,
                ["visualCellSourcePath"] = VisualCellSourcePath,
                ["visualCellFile"] = VisualCellFile,
                ["visualCellPathId"] = VisualCellPathId,
            };
        }

        private sealed class Lod0Part
        {
            public long RelationId { get; init; }
            public string AssistantName { get; init; }
            public string AssistantSourcePath { get; init; }
            public string AssistantFile { get; init; }
            public long AssistantPathId { get; init; }
            public string AvatarMeshName { get; init; }
            public string AvatarMeshSourcePath { get; init; }
            public string AvatarMeshFile { get; init; }
            public long AvatarMeshPathId { get; init; }
            public string RendererFile { get; init; }
            public long RendererPathId { get; init; }
            public List<MaterialRef> Materials { get; } = new();

            public JObject ToJson() => new()
            {
                ["relationId"] = RelationId,
                ["assistantName"] = AssistantName,
                ["assistantSourcePath"] = AssistantSourcePath,
                ["assistantFile"] = AssistantFile,
                ["assistantPathId"] = AssistantPathId,
                ["avatarMeshName"] = AvatarMeshName,
                ["avatarMeshSourcePath"] = AvatarMeshSourcePath,
                ["avatarMeshFile"] = AvatarMeshFile,
                ["avatarMeshPathId"] = AvatarMeshPathId,
                ["rendererFile"] = RendererFile,
                ["rendererPathId"] = RendererPathId,
                ["materials"] = new JArray(Materials.Select(x => x.ToJson())),
            };
        }

        private sealed class MaterialRef
        {
            public long RelationId { get; init; }
            public string MaterialFile { get; init; }
            public long MaterialPathId { get; init; }
            public string MaterialName { get; init; }
            public string MaterialSourcePath { get; init; }
            public List<TextureRef> Textures { get; } = new();

            public JObject ToJson() => new()
            {
                ["relationId"] = RelationId,
                ["materialName"] = MaterialName,
                ["materialSourcePath"] = MaterialSourcePath,
                ["materialFile"] = MaterialFile,
                ["materialPathId"] = MaterialPathId,
                ["textureRefCount"] = Textures.Count,
                ["textures"] = new JArray(Textures.Select(x => x.ToJson())),
            };
        }

        private sealed class TextureRef
        {
            public long RelationId { get; init; }
            public string Slot { get; init; }
            public string TextureName { get; init; }
            public string TextureSourcePath { get; init; }
            public string TextureFile { get; init; }
            public long TexturePathId { get; init; }

            public JObject ToJson() => new()
            {
                ["relationId"] = RelationId,
                ["slot"] = Slot,
                ["textureName"] = TextureName,
                ["textureSourcePath"] = TextureSourcePath,
                ["textureFile"] = TextureFile,
                ["texturePathId"] = TexturePathId,
            };
        }

        private sealed record SourceObjectRef(string Name, string SourcePath, string SerializedFile, long PathId, string Role)
        {
            public JObject ToJson() => new()
            {
                ["name"] = Name,
                ["sourcePath"] = SourcePath,
                ["serializedFile"] = SerializedFile,
                ["pathId"] = PathId,
                ["role"] = Role,
            };
        }

        private sealed record SourceObjectLite(string Name, string SourcePath);

        private sealed record PlanStep(string Name, string Command, string SourceFile, IReadOnlyCollection<long> PathIds)
        {
            public JObject ToJson() => new()
            {
                ["name"] = Name,
                ["sourceFile"] = SourceFile,
                ["pathIds"] = new JArray(PathIds ?? Array.Empty<long>()),
                ["command"] = Command,
            };
        }
    }
}
