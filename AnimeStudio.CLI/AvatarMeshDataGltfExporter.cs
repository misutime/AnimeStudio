using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AnimeStudio.CLI
{
    internal static class AvatarMeshDataGltfExporter
    {
        public static string Export(string jsonPath, string outputFolder, string sourceIndexPath = null)
        {
            if (!string.IsNullOrWhiteSpace(jsonPath) && Directory.Exists(jsonPath))
            {
                return ExportDirectory(jsonPath, outputFolder, sourceIndexPath);
            }

            if (string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath))
            {
                throw new FileNotFoundException("AvatarMeshDataAsset TypeTree JSON not found.", jsonPath);
            }

            outputFolder = string.IsNullOrWhiteSpace(outputFolder)
                ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(jsonPath)) ?? Directory.GetCurrentDirectory(), "AvatarMeshDataGltf")
                : outputFolder;
            Directory.CreateDirectory(outputFolder);

            var sourceJson = JObject.Parse(File.ReadAllText(jsonPath));
            var mesh = ReadMesh(sourceJson, jsonPath);
            var safeName = FixFileName(mesh.MeshName);
            var binName = safeName + ".bin";
            var gltfName = safeName + ".gltf";
            var reportName = safeName + ".avatar_mesh_data_report.json";
            var binPath = Path.Combine(outputFolder, binName);
            var gltfPath = Path.Combine(outputFolder, gltfName);
            var reportPath = Path.Combine(outputFolder, reportName);

            var bufferViews = new JArray();
            var accessors = new JArray();
            var attributes = new JObject();
            using var stream = File.Create(binPath);

            var indexView = WriteUIntBufferView(stream, bufferViews, mesh.Indices.Select(x => checked((uint)x)), 34963);
            accessors.Add(new JObject
            {
                ["bufferView"] = indexView,
                ["componentType"] = 5125,
                ["count"] = mesh.Indices.Count,
                ["type"] = "SCALAR"
            });
            var indexAccessor = accessors.Count - 1;

            var positionView = WriteFloatBufferView(stream, bufferViews, mesh.Positions.SelectMany(ToFloatArray3), 34962);
            accessors.Add(new JObject
            {
                ["bufferView"] = positionView,
                ["componentType"] = 5126,
                ["count"] = mesh.Positions.Count,
                ["type"] = "VEC3",
                ["min"] = new JArray(mesh.Min.X, mesh.Min.Y, mesh.Min.Z),
                ["max"] = new JArray(mesh.Max.X, mesh.Max.Y, mesh.Max.Z)
            });
            attributes["POSITION"] = accessors.Count - 1;

            if (mesh.Normals.Count == mesh.Positions.Count)
            {
                var normalView = WriteFloatBufferView(stream, bufferViews, mesh.Normals.SelectMany(ToFloatArray3), 34962);
                accessors.Add(new JObject
                {
                    ["bufferView"] = normalView,
                    ["componentType"] = 5126,
                    ["count"] = mesh.Normals.Count,
                    ["type"] = "VEC3"
                });
                attributes["NORMAL"] = accessors.Count - 1;
            }

            if (mesh.Tangents.Count == mesh.Positions.Count)
            {
                var tangentView = WriteFloatBufferView(stream, bufferViews, mesh.Tangents.SelectMany(ToFloatArray4), 34962);
                accessors.Add(new JObject
                {
                    ["bufferView"] = tangentView,
                    ["componentType"] = 5126,
                    ["count"] = mesh.Tangents.Count,
                    ["type"] = "VEC4"
                });
                attributes["TANGENT"] = accessors.Count - 1;
            }

            if (mesh.Uvs.Count == mesh.Positions.Count)
            {
                var uvView = WriteFloatBufferView(stream, bufferViews, mesh.Uvs.SelectMany(ToFloatArray2), 34962);
                accessors.Add(new JObject
                {
                    ["bufferView"] = uvView,
                    ["componentType"] = 5126,
                    ["count"] = mesh.Uvs.Count,
                    ["type"] = "VEC2"
                });
                attributes["TEXCOORD_0"] = accessors.Count - 1;
            }

            var gltf = new JObject
            {
                ["asset"] = new JObject
                {
                    ["version"] = "2.0",
                    ["generator"] = "AnimeStudio AvatarMeshDataAsset diagnostic exporter"
                },
                ["scene"] = 0,
                ["scenes"] = new JArray(new JObject { ["nodes"] = new JArray(0) }),
                ["nodes"] = new JArray(new JObject { ["name"] = mesh.MeshName, ["mesh"] = 0 }),
                ["meshes"] = new JArray(new JObject
                {
                    ["name"] = mesh.MeshName,
                    ["primitives"] = new JArray(new JObject
                    {
                        ["attributes"] = attributes,
                        ["indices"] = indexAccessor,
                        ["material"] = 0,
                        ["mode"] = 4
                    }),
                    ["extras"] = new JObject
                    {
                        ["source"] = Path.GetFullPath(jsonPath),
                        ["sourceType"] = "AvatarMeshDataAsset",
                        ["diagnosticOnly"] = true,
                        ["unityCoordinateSystemPreserved"] = true,
                        ["note"] = "Naraka 自定义 MonoBehaviour 网格诊断导出；尚未绑定 prefab 模块关系、材质或骨骼。"
                    }
                }),
                ["materials"] = new JArray(new JObject
                {
                    ["name"] = "avatar_mesh_data_diagnostic_gray",
                    ["doubleSided"] = true,
                    ["pbrMetallicRoughness"] = new JObject
                    {
                        ["baseColorFactor"] = new JArray(0.55, 0.55, 0.55, 1.0),
                        ["metallicFactor"] = 0.0,
                        ["roughnessFactor"] = 0.85
                    }
                }),
                ["buffers"] = new JArray(new JObject
                {
                    ["uri"] = Uri.EscapeDataString(binName),
                    ["byteLength"] = stream.Length
                }),
                ["bufferViews"] = bufferViews,
                ["accessors"] = accessors
            };

            File.WriteAllText(gltfPath, gltf.ToString(Formatting.Indented));
            var report = new JObject
            {
                ["status"] = "ok",
                ["kind"] = "AvatarMeshDataAssetGltf",
                ["sourceJson"] = Path.GetFullPath(jsonPath),
                ["output"] = gltfPath,
                ["meshName"] = mesh.MeshName,
                ["vertexCount"] = mesh.Positions.Count,
                ["indexCount"] = mesh.Indices.Count,
                ["triangleCount"] = mesh.Indices.Count / 3,
                ["hasNormals"] = mesh.Normals.Count == mesh.Positions.Count,
                ["hasTangents"] = mesh.Tangents.Count == mesh.Positions.Count,
                ["hasUv0"] = mesh.Uvs.Count == mesh.Positions.Count,
                ["bboxMin"] = new JArray(mesh.Min.X, mesh.Min.Y, mesh.Min.Z),
                ["bboxMax"] = new JArray(mesh.Max.X, mesh.Max.Y, mesh.Max.Z),
                ["warnings"] = new JArray(mesh.Warnings),
                ["rule"] = "只从 AvatarMeshDataAsset TypeTree JSON 里的确定性字段生成静态诊断 glTF；不猜 prefab 模块关系、材质或骨骼。"
            };
            File.WriteAllText(reportPath, report.ToString(Formatting.Indented));
            Logger.Info($"Exported AvatarMeshDataAsset diagnostic glTF: {gltfPath}");
            Logger.Info($"Wrote AvatarMeshDataAsset diagnostic report: {reportPath}");
            return gltfPath;
        }

        private static string ExportDirectory(string jsonFolder, string outputFolder, string sourceIndexPath)
        {
            var jsonFiles = Directory.GetFiles(jsonFolder, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (jsonFiles.Length == 0)
            {
                throw new FileNotFoundException("AvatarMeshDataAsset TypeTree JSON folder has no *.json files.", jsonFolder);
            }

            var visualCell = TryLoadVisualCell(jsonFiles);
            if (visualCell != null)
            {
                return ExportVisualCellDirectory(jsonFolder, outputFolder, visualCell, sourceIndexPath);
            }

            outputFolder = string.IsNullOrWhiteSpace(outputFolder)
                ? Path.Combine(Path.GetFullPath(jsonFolder), "AvatarMeshDataGltf")
                : outputFolder;
            Directory.CreateDirectory(outputFolder);

            var meshes = jsonFiles
                .Select(file => ReadMesh(JObject.Parse(File.ReadAllText(file)), file))
                .ToList();
            var safeName = FixFileName(Path.GetFileName(Path.GetFullPath(jsonFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            var binName = safeName + ".bin";
            var gltfName = safeName + ".gltf";
            var reportName = safeName + ".avatar_mesh_data_report.json";
            var binPath = Path.Combine(outputFolder, binName);
            var gltfPath = Path.Combine(outputFolder, gltfName);
            var reportPath = Path.Combine(outputFolder, reportName);

            var bufferViews = new JArray();
            var accessors = new JArray();
            var gltfMeshes = new JArray();
            var nodes = new JArray();
            using var stream = File.Create(binPath);
            for (var i = 0; i < meshes.Count; i++)
            {
                gltfMeshes.Add(WriteMeshObject(stream, bufferViews, accessors, meshes[i], Path.GetFullPath(jsonFiles[i])));
                nodes.Add(new JObject
                {
                    ["name"] = meshes[i].MeshName,
                    ["mesh"] = i
                });
            }

            var totalBounds = CalculateBounds(meshes);
            var gltf = new JObject
            {
                ["asset"] = new JObject
                {
                    ["version"] = "2.0",
                    ["generator"] = "AnimeStudio AvatarMeshDataAsset diagnostic set exporter"
                },
                ["scene"] = 0,
                ["scenes"] = new JArray(new JObject { ["nodes"] = new JArray(Enumerable.Range(0, nodes.Count)) }),
                ["nodes"] = nodes,
                ["meshes"] = gltfMeshes,
                ["materials"] = new JArray(CreateDiagnosticMaterial()),
                ["buffers"] = new JArray(new JObject
                {
                    ["uri"] = Uri.EscapeDataString(binName),
                    ["byteLength"] = stream.Length
                }),
                ["bufferViews"] = bufferViews,
                ["accessors"] = accessors,
                ["extras"] = new JObject
                {
                    ["sourceDirectory"] = Path.GetFullPath(jsonFolder),
                    ["sourceType"] = "AvatarMeshDataAssetSet",
                    ["diagnosticOnly"] = true,
                    ["unityCoordinateSystemPreserved"] = true,
                    ["note"] = "Naraka 自定义 MonoBehaviour 网格集合诊断导出；可能包含 LOD 变体，尚未绑定 prefab 模块关系、材质槽或骨骼 skin。"
                }
            };

            File.WriteAllText(gltfPath, gltf.ToString(Formatting.Indented));
            var report = new JObject
            {
                ["status"] = "ok",
                ["kind"] = "AvatarMeshDataAssetGltfSet",
                ["sourceDirectory"] = Path.GetFullPath(jsonFolder),
                ["output"] = gltfPath,
                ["meshCount"] = meshes.Count,
                ["vertexCount"] = meshes.Sum(x => x.Positions.Count),
                ["indexCount"] = meshes.Sum(x => x.Indices.Count),
                ["triangleCount"] = meshes.Sum(x => x.Indices.Count / 3),
                ["containsLodVariants"] = meshes.Any(x => Regex.IsMatch(x.MeshName ?? string.Empty, @"_L\d+_", RegexOptions.IgnoreCase)),
                ["bboxMin"] = new JArray(totalBounds.Min.X, totalBounds.Min.Y, totalBounds.Min.Z),
                ["bboxMax"] = new JArray(totalBounds.Max.X, totalBounds.Max.Y, totalBounds.Max.Z),
                ["meshes"] = new JArray(meshes.Select((mesh, index) => ToReportJson(mesh, jsonFiles[index]))),
                ["rule"] = "只从目录内 AvatarMeshDataAsset TypeTree JSON 的确定性字段生成静态诊断 glTF；不猜 prefab 模块关系、Renderer 材质槽、骨骼或 skin。"
            };
            File.WriteAllText(reportPath, report.ToString(Formatting.Indented));
            Logger.Info($"Exported AvatarMeshDataAsset diagnostic glTF set: {gltfPath}");
            Logger.Info($"Wrote AvatarMeshDataAsset diagnostic report: {reportPath}");
            return gltfPath;
        }

        private static string ExportVisualCellDirectory(string jsonFolder, string outputFolder, JObject visualCell, string sourceIndexPath)
        {
            var manifest = LoadManifest(jsonFolder);
            if (manifest.Count == 0)
            {
                throw new InvalidDataException("ActorBodyVisualCell directory export requires export_manifest.jsonl for PathID to JSON mapping.");
            }

            var selectedAssistants = ReadPPtrPathIds(visualCell["lod0RendererAssistants"] as JArray).ToList();
            if (selectedAssistants.Count == 0)
            {
                throw new InvalidDataException("ActorBodyVisualCell JSON has no lod0RendererAssistants.");
            }

            var parts = new List<VisualCellPart>();
            var warnings = new List<string>();
            foreach (var assistantPathId in selectedAssistants)
            {
                if (!manifest.TryGetValue(assistantPathId, out var assistantEntry) || !File.Exists(assistantEntry.JsonPath))
                {
                    warnings.Add($"missingAssistantJson:{assistantPathId}");
                    continue;
                }

                var assistant = JObject.Parse(File.ReadAllText(assistantEntry.JsonPath));
                var avatarMeshPathId = ReadPPtrPathId(assistant["avatarMeshAsset"]);
                if (avatarMeshPathId == 0)
                {
                    warnings.Add($"assistantWithoutAvatarMesh:{assistantPathId}");
                    continue;
                }
                if (!manifest.TryGetValue(avatarMeshPathId, out var meshEntry) || !File.Exists(meshEntry.JsonPath))
                {
                    warnings.Add($"missingAvatarMeshJson:{avatarMeshPathId}");
                    continue;
                }

                var mesh = ReadMesh(JObject.Parse(File.ReadAllText(meshEntry.JsonPath)), meshEntry.JsonPath);
                var rendererPathId = ReadPPtrPathId(assistant["_renderer"]);
                parts.Add(new VisualCellPart(
                    assistantPathId,
                    (string)assistant["gameObjectName"] ?? mesh.MeshName,
                    assistantEntry.SerializedFile,
                    rendererPathId,
                    avatarMeshPathId,
                    assistantEntry.JsonPath,
                    meshEntry.JsonPath,
                    mesh));
            }

            if (parts.Count == 0)
            {
                throw new InvalidDataException("ActorBodyVisualCell LOD0 has no resolved AvatarMeshDataAsset JSON.");
            }

            outputFolder = string.IsNullOrWhiteSpace(outputFolder)
                ? Path.Combine(Path.GetFullPath(jsonFolder), "AvatarMeshDataGltf")
                : outputFolder;
            Directory.CreateDirectory(outputFolder);

            var safeName = FixFileName(Path.GetFileName(Path.GetFullPath(jsonFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) + "_lod0");
            var binName = safeName + ".bin";
            var gltfName = safeName + ".gltf";
            var reportName = safeName + ".avatar_mesh_data_report.json";
            var binPath = Path.Combine(outputFolder, binName);
            var gltfPath = Path.Combine(outputFolder, gltfName);
            var reportPath = Path.Combine(outputFolder, reportName);

            var bufferViews = new JArray();
            var accessors = new JArray();
            var gltfMeshes = new JArray();
            var nodes = new JArray();
            var materialBindings = LoadVisualCellMaterialBindings(sourceIndexPath, parts, warnings);
            var gltfImages = new JArray();
            var gltfTextures = new JArray();
            var materialIndexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var gltfMaterials = BuildVisualCellPreviewMaterials(outputFolder, materialBindings.Values, gltfImages, gltfTextures, materialIndexByKey, warnings);
            using var stream = File.Create(binPath);
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                materialBindings.TryGetValue(GetRendererKey(part.RendererFile, part.RendererPathId), out var materialBinding);
                var materialIndex = GetVisualCellMaterialIndex(materialBinding, materialIndexByKey);
                gltfMeshes.Add(WriteMeshObject(stream, bufferViews, accessors, part.Mesh, Path.GetFullPath(part.MeshJsonPath), materialIndex));
                nodes.Add(new JObject
                {
                    ["name"] = part.GameObjectName,
                    ["mesh"] = i,
                    ["extras"] = new JObject
                    {
                        ["assistantPathId"] = part.AssistantPathId,
                        ["rendererFile"] = part.RendererFile,
                        ["rendererPathId"] = part.RendererPathId,
                        ["avatarMeshPathId"] = part.AvatarMeshPathId,
                        ["materialBinding"] = materialBinding?.ToJson()
                    }
                });
            }

            var totalBounds = CalculateBounds(parts.Select(x => x.Mesh));
            var gltf = new JObject
            {
                ["asset"] = new JObject
                {
                    ["version"] = "2.0",
                    ["generator"] = "AnimeStudio Naraka ActorBodyVisualCell diagnostic LOD exporter"
                },
                ["scene"] = 0,
                ["scenes"] = new JArray(new JObject { ["nodes"] = new JArray(Enumerable.Range(0, nodes.Count)) }),
                ["nodes"] = nodes,
                ["meshes"] = gltfMeshes,
                ["materials"] = gltfMaterials,
                ["buffers"] = new JArray(new JObject
                {
                    ["uri"] = Uri.EscapeDataString(binName),
                    ["byteLength"] = stream.Length
                }),
                ["bufferViews"] = bufferViews,
                ["accessors"] = accessors,
                ["extras"] = new JObject
                {
                    ["sourceDirectory"] = Path.GetFullPath(jsonFolder),
                    ["sourceType"] = "ActorBodyVisualCell",
                    ["selectedLodGroup"] = "lod0RendererAssistants",
                    ["diagnosticOnly"] = true,
                    ["materialBindingSourceIndex"] = string.IsNullOrWhiteSpace(sourceIndexPath) ? null : Path.GetFullPath(sourceIndexPath),
                    ["previewMaterialSource"] = gltfMaterials.Count > 1 ? Path.GetFullPath(outputFolder) : null,
                    ["unityCoordinateSystemPreserved"] = true,
                    ["note"] = "Naraka ActorBodyVisualCell LOD0 自定义网格诊断导出；按 Unity PPtr 选择 avatarMeshAsset，可记录 Renderer 材质引用，但尚未烘焙材质或绑定骨骼 skin。"
                }
            };
            if (gltfImages.Count > 0)
            {
                gltf["images"] = gltfImages;
            }
            if (gltfTextures.Count > 0)
            {
                gltf["textures"] = gltfTextures;
            }

            File.WriteAllText(gltfPath, gltf.ToString(Formatting.Indented));
            var materialRefCount = materialBindings.Values.Sum(x => x.Materials.Count);
            var materialTextureRefCount = materialBindings.Values.Sum(x => x.Materials.Sum(y => y.TextureRefCount));
            var report = new JObject
            {
                ["status"] = warnings.Count == 0 ? "ok" : "warning",
                ["kind"] = "NarakaActorBodyVisualCellLodGltf",
                ["sourceDirectory"] = Path.GetFullPath(jsonFolder),
                ["output"] = gltfPath,
                ["selectedLodGroup"] = "lod0RendererAssistants",
                ["sourceIndex"] = string.IsNullOrWhiteSpace(sourceIndexPath) ? null : Path.GetFullPath(sourceIndexPath),
                ["assistantCount"] = selectedAssistants.Count,
                ["resolvedPartCount"] = parts.Count,
                ["meshCount"] = parts.Count,
                ["vertexCount"] = parts.Sum(x => x.Mesh.Positions.Count),
                ["indexCount"] = parts.Sum(x => x.Mesh.Indices.Count),
                ["triangleCount"] = parts.Sum(x => x.Mesh.Indices.Count / 3),
                ["rendererMaterialRefCount"] = materialRefCount,
                ["materialTextureRefCount"] = materialTextureRefCount,
                ["bboxMin"] = new JArray(totalBounds.Min.X, totalBounds.Min.Y, totalBounds.Min.Z),
                ["bboxMax"] = new JArray(totalBounds.Max.X, totalBounds.Max.Y, totalBounds.Max.Z),
                ["warnings"] = new JArray(warnings),
                ["parts"] = new JArray(parts.Select(part =>
                {
                    materialBindings.TryGetValue(GetRendererKey(part.RendererFile, part.RendererPathId), out var materialBinding);
                    return ToReportJson(part, materialBinding);
                })),
                ["rule"] = "只按 ActorBodyVisualCell.lod0RendererAssistants -> LXRendererAssistant.avatarMeshAsset 的确定性 PPtr 选择 Naraka 自定义网格；材质引用只来自 renderer.material / material.texture 源索引关系，不猜 LOD、材质槽、骨骼或 skin。"
            };
            File.WriteAllText(reportPath, report.ToString(Formatting.Indented));
            WriteVisualCellLibraryMetadata(
                outputFolder,
                gltfPath,
                report,
                jsonFolder,
                sourceIndexPath,
                parts,
                materialBindings,
                gltfMaterials,
                gltfImages,
                gltfTextures);
            Logger.Info($"Exported Naraka ActorBodyVisualCell LOD0 diagnostic glTF: {gltfPath}");
            Logger.Info($"Wrote Naraka ActorBodyVisualCell diagnostic report: {reportPath}");
            return gltfPath;
        }

        private static void WriteVisualCellLibraryMetadata(
            string outputFolder,
            string gltfPath,
            JObject report,
            string jsonFolder,
            string sourceIndexPath,
            IReadOnlyList<VisualCellPart> parts,
            IReadOnlyDictionary<string, VisualCellMaterialBinding> materialBindings,
            JArray gltfMaterials,
            JArray gltfImages,
            JArray gltfTextures)
        {
            // 这里先把 Naraka 自定义网格接进 AssetLibrary v1，
            // 但仍按诊断模型处理，不能绕过模型优先门禁。
            var relativeOutput = ToLibraryRelativePath(outputFolder, gltfPath);
            var needsCustomizationTint = gltfMaterials
                .OfType<JObject>()
                .Any(x => x["extras"]?["animeStudioMaterial"]?["needsCustomizationTint"]?.Value<bool>() == true);
            var hasBaseColorTexture = gltfMaterials
                .OfType<JObject>()
                .Any(x => x["pbrMetallicRoughness"]?["baseColorTexture"] != null);
            var hasNormalTexture = gltfMaterials
                .OfType<JObject>()
                .Any(x => x["normalTexture"] != null);
            var materialStatus = needsCustomizationTint
                ? "needsCustomizationTint"
                : gltfTextures.Count > 0 ? "previewMaterial" : "diagnosticGray";
            var validationReasons = new JArray(
                "diagnostic_custom_mesh",
                "missing_skin_binding",
                parts.Any(x => x.Mesh.Skin.Status == "presentUnmapped") ? "skin_data_present_unmapped" : "skin_data_missing",
                needsCustomizationTint ? "needs_customization_tint" : "preview_material_not_full_shader");
            var sourceSkinStatuses = parts
                .Select(x => x.Mesh.Skin.Status)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var catalogEntry = new JObject
            {
                ["kind"] = "Model",
                ["libraryRole"] = "CustomMeshDiagnostic",
                ["resourceKind"] = "NarakaCustomMeshPart",
                ["exportedAt"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["name"] = Path.GetFileNameWithoutExtension(gltfPath),
                ["sourceType"] = "ActorBodyVisualCell",
                ["source"] = Path.GetFullPath(jsonFolder),
                ["pathId"] = 0,
                ["output"] = relativeOutput,
                ["format"] = "Gltf",
                ["modelSource"] = "NarakaActorBodyVisualCellLod0",
                ["textureMode"] = "Png",
                ["animationPackage"] = "Separate",
                ["diagnosticOnly"] = true,
                ["modelValidationStatus"] = "warning",
                ["modelValidationReasons"] = validationReasons,
                ["meshCount"] = report["meshCount"],
                ["vertexCount"] = report["vertexCount"],
                ["indexCount"] = report["indexCount"],
                ["triangleCount"] = report["triangleCount"],
                ["nodeCount"] = parts.Count,
                ["materialCount"] = gltfMaterials.Count,
                ["textureCount"] = gltfTextures.Count,
                ["materialStatus"] = materialStatus,
                ["materialNeedsCustomizationTint"] = needsCustomizationTint,
                ["materialMissingRendererBinding"] = materialBindings.Values.Any(x => x.Materials.Count == 0),
                ["materialHasBaseColorTexture"] = hasBaseColorTexture,
                ["materialHasNormalTexture"] = hasNormalTexture,
                ["materialImageCount"] = gltfImages.Count,
                ["rendererMaterialRefCount"] = report["rendererMaterialRefCount"],
                ["materialTextureRefCount"] = report["materialTextureRefCount"],
                ["animationCount"] = 0,
                ["embeddedAnimationCount"] = 0,
                ["importedAnimationListCount"] = 0,
                ["morphCount"] = 0,
                ["boneCount"] = 0,
                ["skinCount"] = 0,
                ["sourceSkinDataStatus"] = sourceSkinStatuses.Length == 1 ? sourceSkinStatuses[0] : string.Join(",", sourceSkinStatuses),
                ["sourceSkinBindPoseCount"] = parts.Sum(x => x.Mesh.Skin.BindPoseCount),
                ["sourceSkinMappedJointCount"] = 0,
                ["sourceSkinUnmapped"] = parts.Any(x => x.Mesh.Skin.Status == "presentUnmapped"),
                ["selectedLodGroup"] = "lod0RendererAssistants",
                ["sourceDirectory"] = Path.GetFullPath(jsonFolder),
                ["sourceIndex"] = string.IsNullOrWhiteSpace(sourceIndexPath) ? null : Path.GetFullPath(sourceIndexPath),
                ["bboxMin"] = report["bboxMin"]?.DeepClone(),
                ["bboxMax"] = report["bboxMax"]?.DeepClone(),
                ["customMeshRelation"] = new JObject
                {
                    ["status"] = "deterministic",
                    ["basis"] = "ActorBodyVisualCell.lod0RendererAssistants -> LXRendererAssistant.avatarMeshAsset",
                    ["rendererMaterialBasis"] = "renderer.material / material.texture from unity_source_index.db",
                    ["skinDataBasis"] = "AvatarMeshDataAsset.m_AnimSkinData / m_AvatarBoneWeights / m_BindPoses",
                    ["rule"] = "该记录只证明 Naraka 自定义网格、材质引用和源 skin 字段可追溯；没有 joint 映射、shader tint 和完整角色装配前不进入动画验收。"
                }
            };

            RewriteJsonLineByOutput(Path.Combine(outputFolder, "asset_catalog.jsonl"), relativeOutput, catalogEntry);
            WriteOrUpdateCustomMeshValidation(outputFolder, gltfPath, relativeOutput, validationReasons);
        }

        private static void WriteOrUpdateCustomMeshValidation(
            string outputFolder,
            string gltfPath,
            string relativeOutput,
            JArray validationReasons)
        {
            // glTF 结构可以合法，但缺 skin / tint 时仍必须保持 warning。
            var validation = ModelLibraryValidator.ValidateSingleModelForAnimationGate(gltfPath);
            validation["Status"] = "warning";
            validation["Path"] = relativeOutput;
            validation["diagnosticOnly"] = true;
            validation["customMeshValidationReasons"] = validationReasons.DeepClone();
            validation["Notes"] = MergeStringArray(
                validation["Notes"] as JArray,
                "Naraka ActorBodyVisualCell 自定义网格仍是诊断模型：源 skin 字段尚未映射到 glTF joints，hair tint/customization shader 未完全复原，禁止进入动画生产验收。");

            var body = validation["Body"] as JObject;
            if (body != null)
            {
                body["ModelBodyStatus"] = "warning";
                body["Evidence"] = MergeStringArray(
                    body["Evidence"] as JArray,
                    "自定义网格源 JSON 可包含 skin 字段，但当前没有确定性 joint 映射，所以没有写 glTF skin；材质只做保守预览，未烘焙 Naraka customization tint。");
            }

            var validationPath = Path.Combine(outputFolder, "model_validation.json");
            var root = File.Exists(validationPath)
                ? JObject.Parse(File.ReadAllText(validationPath).TrimStart('\uFEFF'))
                : new JObject
                {
                    ["generatedAt"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    ["rule"] = "模型 glTF 单独通过 Mesh/UV/材质/贴图/skin/bbox 静态验收后，才允许进入动画导出、合成或生产 smoke。",
                    ["models"] = new JArray()
                };

            root["generatedAt"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var models = root["models"] as JArray;
            if (models == null)
            {
                models = new JArray();
                root["models"] = models;
            }

            for (var i = models.Count - 1; i >= 0; i--)
            {
                var existingPath = (string)models[i]?["Path"];
                if (string.Equals(NormalizeRelativePath(existingPath), NormalizeRelativePath(relativeOutput), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(existingPath, gltfPath, StringComparison.OrdinalIgnoreCase))
                {
                    models.RemoveAt(i);
                }
            }
            models.Add(validation);
            root["totals"] = BuildValidationTotals(models);
            File.WriteAllText(validationPath, root.ToString(Formatting.Indented));
        }

        private static JObject BuildValidationTotals(JArray models)
        {
            var reports = models.OfType<JObject>().ToArray();
            return new JObject
            {
                ["models"] = reports.Length,
                ["ok"] = reports.Count(x => string.Equals((string)x["Status"], "ok", StringComparison.OrdinalIgnoreCase)),
                ["warning"] = reports.Count(x => string.Equals((string)x["Status"], "warning", StringComparison.OrdinalIgnoreCase)),
                ["error"] = reports.Count(x => string.Equals((string)x["Status"], "error", StringComparison.OrdinalIgnoreCase)),
                ["modelBodyOk"] = reports.Count(x => string.Equals((string)x["Body"]?["ModelBodyStatus"], "ok", StringComparison.OrdinalIgnoreCase)),
                ["modelBodyWarning"] = reports.Count(x => string.Equals((string)x["Body"]?["ModelBodyStatus"], "warning", StringComparison.OrdinalIgnoreCase)),
                ["modelBodyError"] = reports.Count(x => string.Equals((string)x["Body"]?["ModelBodyStatus"], "error", StringComparison.OrdinalIgnoreCase)),
                ["withSkin"] = reports.Count(x => (int?)x["Counts"]?["Skins"] > 0),
                ["withTextures"] = reports.Count(x => (int?)x["Counts"]?["Images"] > 0),
            };
        }

        private static JArray MergeStringArray(JArray existing, string value)
        {
            var result = existing != null
                ? new JArray(existing.Values<string>().Where(x => !string.IsNullOrWhiteSpace(x)))
                : new JArray();
            if (!result.Values<string>().Any(x => string.Equals(x, value, StringComparison.Ordinal)))
            {
                result.Add(value);
            }
            return result;
        }

        private static void RewriteJsonLineByOutput(string path, string relativeOutput, JObject entry)
        {
            // 诊断命令常会反复跑，同一个输出只保留最新一行。
            var lines = new List<string>();
            if (File.Exists(path))
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var keep = true;
                    try
                    {
                        var obj = JObject.Parse(line);
                        keep = !string.Equals(
                            NormalizeRelativePath((string)obj["output"] ?? (string)obj["outputPath"] ?? (string)obj["assetPath"]),
                            NormalizeRelativePath(relativeOutput),
                            StringComparison.OrdinalIgnoreCase);
                    }
                    catch (JsonException)
                    {
                        keep = true;
                    }

                    if (keep)
                    {
                        lines.Add(line);
                    }
                }
            }

            lines.Add(entry.ToString(Formatting.None));
            File.WriteAllLines(path, lines);
        }

        private static string ToLibraryRelativePath(string root, string path)
        {
            return Path.GetRelativePath(root, path).Replace('\\', '/');
        }

        private static string NormalizeRelativePath(string value)
        {
            return (value ?? string.Empty).Replace('\\', '/').TrimStart('/');
        }

        private static JObject WriteMeshObject(Stream stream, JArray bufferViews, JArray accessors, AvatarMeshData mesh, string source, int materialIndex = 0)
        {
            var attributes = new JObject();
            var indexView = WriteUIntBufferView(stream, bufferViews, mesh.Indices.Select(x => checked((uint)x)), 34963);
            accessors.Add(new JObject
            {
                ["bufferView"] = indexView,
                ["componentType"] = 5125,
                ["count"] = mesh.Indices.Count,
                ["type"] = "SCALAR"
            });
            var indexAccessor = accessors.Count - 1;

            var positionView = WriteFloatBufferView(stream, bufferViews, mesh.Positions.SelectMany(ToFloatArray3), 34962);
            accessors.Add(new JObject
            {
                ["bufferView"] = positionView,
                ["componentType"] = 5126,
                ["count"] = mesh.Positions.Count,
                ["type"] = "VEC3",
                ["min"] = new JArray(mesh.Min.X, mesh.Min.Y, mesh.Min.Z),
                ["max"] = new JArray(mesh.Max.X, mesh.Max.Y, mesh.Max.Z)
            });
            attributes["POSITION"] = accessors.Count - 1;

            if (mesh.Normals.Count == mesh.Positions.Count)
            {
                var normalView = WriteFloatBufferView(stream, bufferViews, mesh.Normals.SelectMany(ToFloatArray3), 34962);
                accessors.Add(new JObject
                {
                    ["bufferView"] = normalView,
                    ["componentType"] = 5126,
                    ["count"] = mesh.Normals.Count,
                    ["type"] = "VEC3"
                });
                attributes["NORMAL"] = accessors.Count - 1;
            }

            if (mesh.Tangents.Count == mesh.Positions.Count)
            {
                var tangentView = WriteFloatBufferView(stream, bufferViews, mesh.Tangents.SelectMany(ToFloatArray4), 34962);
                accessors.Add(new JObject
                {
                    ["bufferView"] = tangentView,
                    ["componentType"] = 5126,
                    ["count"] = mesh.Tangents.Count,
                    ["type"] = "VEC4"
                });
                attributes["TANGENT"] = accessors.Count - 1;
            }

            if (mesh.Uvs.Count == mesh.Positions.Count)
            {
                var uvView = WriteFloatBufferView(stream, bufferViews, mesh.Uvs.SelectMany(ToFloatArray2), 34962);
                accessors.Add(new JObject
                {
                    ["bufferView"] = uvView,
                    ["componentType"] = 5126,
                    ["count"] = mesh.Uvs.Count,
                    ["type"] = "VEC2"
                });
                attributes["TEXCOORD_0"] = accessors.Count - 1;
            }

            return new JObject
            {
                ["name"] = mesh.MeshName,
                ["primitives"] = new JArray(new JObject
                {
                    ["attributes"] = attributes,
                    ["indices"] = indexAccessor,
                    ["material"] = materialIndex,
                    ["mode"] = 4
                }),
                ["extras"] = new JObject
                {
                    ["source"] = source,
                    ["sourceType"] = "AvatarMeshDataAsset",
                    ["diagnosticOnly"] = true,
                    ["unityCoordinateSystemPreserved"] = true,
                    ["note"] = "Naraka 自定义 MonoBehaviour 网格诊断导出；尚未绑定 prefab 模块关系、材质或骨骼。"
                }
            };
        }

        private static JObject CreateDiagnosticMaterial()
        {
            return new JObject
            {
                ["name"] = "avatar_mesh_data_diagnostic_gray",
                ["doubleSided"] = true,
                ["pbrMetallicRoughness"] = new JObject
                {
                    ["baseColorFactor"] = new JArray(0.55, 0.55, 0.55, 1.0),
                    ["metallicFactor"] = 0.0,
                    ["roughnessFactor"] = 0.85
                }
            };
        }

        private static JArray BuildVisualCellPreviewMaterials(
            string outputFolder,
            IEnumerable<VisualCellMaterialBinding> bindings,
            JArray images,
            JArray textures,
            Dictionary<string, int> materialIndexByKey,
            List<string> warnings)
        {
            var bindingList = bindings.ToList();
            var materials = new JArray();

            var exportedAssets = LoadExportedAssetManifest(outputFolder);
            if (exportedAssets.Count == 0)
            {
                materials.Add(CreateDiagnosticMaterial());
                return materials;
            }

            foreach (var binding in bindingList)
            {
                foreach (var materialRef in binding.Materials)
                {
                    var key = GetMaterialKey(materialRef.MaterialFile, materialRef.MaterialPathId);
                    if (materialIndexByKey.ContainsKey(key))
                    {
                        continue;
                    }

                    if (!exportedAssets.TryGetValue(materialRef.MaterialPathId, out var materialAsset)
                        || !string.Equals(materialAsset.Type, "Material", StringComparison.OrdinalIgnoreCase))
                    {
                        warnings.Add($"missingExportedMaterialJson:{materialRef.MaterialPathId}");
                        continue;
                    }

                    var materialJsonPath = FindExportedFile(materialAsset, ".json");
                    if (string.IsNullOrWhiteSpace(materialJsonPath))
                    {
                        warnings.Add($"missingExportedMaterialJsonFile:{materialRef.MaterialPathId}");
                        continue;
                    }

                    var gltfMaterial = BuildVisualCellPreviewMaterial(
                        outputFolder,
                        JObject.Parse(File.ReadAllText(materialJsonPath)),
                        materialJsonPath,
                        exportedAssets,
                        images,
                        textures,
                        warnings);
                    materialIndexByKey[key] = materials.Count;
                    materials.Add(gltfMaterial);
                }
            }

            var needsDiagnosticMaterial = materials.Count == 0 || bindingList.Any(binding =>
            {
                var materialRef = binding.Materials.FirstOrDefault();
                return materialRef == null
                    || !materialIndexByKey.ContainsKey(GetMaterialKey(materialRef.MaterialFile, materialRef.MaterialPathId));
            });
            if (needsDiagnosticMaterial)
            {
                foreach (var key in materialIndexByKey.Keys.ToList())
                {
                    materialIndexByKey[key] = materialIndexByKey[key] + 1;
                }
                materials.Insert(0, CreateDiagnosticMaterial());
            }

            return materials;
        }

        private static JObject BuildVisualCellPreviewMaterial(
            string gltfFolder,
            JObject materialJson,
            string materialJsonPath,
            IReadOnlyDictionary<long, ExportedAssetInfo> exportedAssets,
            JArray images,
            JArray textures,
            List<string> warnings)
        {
            var materialName = (string)materialJson["m_Name"] ?? (string)materialJson["Name"] ?? Path.GetFileNameWithoutExtension(materialJsonPath);
            var texEnvs = materialJson["m_SavedProperties"]?["m_TexEnvs"] as JObject;
            var color = materialJson["m_SavedProperties"]?["m_Colors"]?["_Color"];
            var pbr = new JObject
            {
                ["baseColorFactor"] = ReadColorFactor(color),
                ["metallicFactor"] = 0.0,
                ["roughnessFactor"] = 0.65,
            };
            var textureSlots = new JArray();
            var usedBaseColor = false;
            JObject normalTexture = null;

            if (texEnvs != null)
            {
                foreach (var property in texEnvs.Properties())
                {
                    var textureToken = property.Value?["m_Texture"];
                    var pathId = textureToken?["m_PathID"]?.Value<long>() ?? 0;
                    if (pathId == 0)
                    {
                        continue;
                    }

                    var textureName = (string)textureToken["Name"] ?? string.Empty;
                    var slot = new JObject
                    {
                        ["slot"] = property.Name,
                        ["textureName"] = textureName,
                        ["pathId"] = pathId,
                        ["previewUsage"] = "preservedOnly"
                    };

                    if (exportedAssets.TryGetValue(pathId, out var textureAsset)
                        && string.Equals(textureAsset.Type, "Texture2D", StringComparison.OrdinalIgnoreCase))
                    {
                        var texturePath = FindExportedFile(textureAsset, ".png");
                        if (!string.IsNullOrWhiteSpace(texturePath))
                        {
                            var relativeTexturePath = Path.GetRelativePath(gltfFolder, texturePath).Replace('\\', '/');
                            slot["uri"] = relativeTexturePath;

                            if (!usedBaseColor && IsSafePreviewBaseColorSlot(property.Name, textureName))
                            {
                                var textureIndex = AddGltfTexture(images, textures, textureAsset.Name, relativeTexturePath);
                                pbr["baseColorTexture"] = new JObject { ["index"] = textureIndex };
                                slot["gltfTextureIndex"] = textureIndex;
                                slot["previewUsage"] = "baseColorTexture";
                                usedBaseColor = true;
                            }
                            else if (normalTexture == null && IsNormalSlot(property.Name, textureName))
                            {
                                var textureIndex = AddGltfTexture(images, textures, textureAsset.Name, relativeTexturePath);
                                normalTexture = new JObject { ["index"] = textureIndex };
                                slot["gltfTextureIndex"] = textureIndex;
                                slot["previewUsage"] = "normalTexture";
                            }
                        }
                    }
                    else
                    {
                        warnings.Add($"missingExportedTexture:{pathId}");
                    }

                    textureSlots.Add(slot);
                }
            }

            var result = new JObject
            {
                ["name"] = materialName,
                ["doubleSided"] = true,
                ["pbrMetallicRoughness"] = pbr,
                ["extras"] = new JObject
                {
                    ["animeStudioMaterial"] = new JObject
                    {
                        ["workflow"] = "NarakaActorBodyVisualCellDiagnostic",
                        ["materialJson"] = Path.GetFullPath(materialJsonPath),
                        ["textureSlots"] = textureSlots,
                        ["needsCustomizationTint"] = !usedBaseColor,
                        ["note"] = "只把通用 base color/normal 槽写入 glTF 预览；ID、mask、SH、dir、LUT 等自定义 shader 数据只保留引用，避免伪造材质。"
                    }
                }
            };
            if (normalTexture != null)
            {
                result["normalTexture"] = normalTexture;
            }
            return result;
        }

        private static int AddGltfTexture(JArray images, JArray textures, string name, string relativePath)
        {
            var imageIndex = images.Count;
            images.Add(new JObject
            {
                ["uri"] = relativePath,
                ["name"] = name,
            });
            var textureIndex = textures.Count;
            textures.Add(new JObject
            {
                ["source"] = imageIndex,
                ["name"] = name,
            });
            return textureIndex;
        }

        private static JArray ReadColorFactor(JToken color)
        {
            if (color == null)
            {
                return new JArray(0.8, 0.8, 0.8, 1.0);
            }

            return new JArray(
                color["r"]?.Value<float>() ?? 0.8f,
                color["g"]?.Value<float>() ?? 0.8f,
                color["b"]?.Value<float>() ?? 0.8f,
                color["a"]?.Value<float>() ?? 1.0f);
        }

        private static bool IsSafePreviewBaseColorSlot(string slot, string textureName)
        {
            if (string.IsNullOrWhiteSpace(slot))
            {
                return false;
            }

            var slotText = slot.ToLowerInvariant();
            var textureText = (textureName ?? string.Empty).ToLowerInvariant();
            if (textureText.Contains("_id")
                || textureText.Contains("mask")
                || textureText.Contains("_n")
                || textureText.Contains("normal")
                || textureText.Contains("_dir")
                || textureText.Contains("_sh")
                || textureText.Contains("lut"))
            {
                return false;
            }

            return slotText is "_basemap" or "_basecolormap" or "_albedomap" or "_diffusemap"
                || (slotText == "_maintex" && (textureText.EndsWith("_d") || textureText.Contains("diffuse") || textureText.Contains("albedo")));
        }

        private static bool IsNormalSlot(string slot, string textureName)
        {
            var slotText = (slot ?? string.Empty).ToLowerInvariant();
            var textureText = (textureName ?? string.Empty).ToLowerInvariant();
            return slotText.Contains("bump") || slotText.Contains("normal") || textureText.EndsWith("_n") || textureText.Contains("normal");
        }

        private static int GetVisualCellMaterialIndex(
            VisualCellMaterialBinding binding,
            IReadOnlyDictionary<string, int> materialIndexByKey)
        {
            var materialRef = binding?.Materials.FirstOrDefault();
            if (materialRef == null)
            {
                return 0;
            }

            return materialIndexByKey.TryGetValue(GetMaterialKey(materialRef.MaterialFile, materialRef.MaterialPathId), out var index)
                ? index
                : 0;
        }

        private static Dictionary<long, ExportedAssetInfo> LoadExportedAssetManifest(string outputFolder)
        {
            var result = new Dictionary<long, ExportedAssetInfo>();
            var manifestPath = Path.Combine(outputFolder, "export_manifest.jsonl");
            if (!File.Exists(manifestPath))
            {
                return result;
            }

            foreach (var line in File.ReadLines(manifestPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var item = JObject.Parse(line);
                var pathId = item["pathId"]?.Value<long>() ?? 0;
                if (pathId == 0)
                {
                    continue;
                }

                result[pathId] = new ExportedAssetInfo(
                    (string)item["type"] ?? string.Empty,
                    (string)item["name"] ?? string.Empty,
                    (string)item["output"] ?? outputFolder);
            }

            return result;
        }

        private static string FindExportedFile(ExportedAssetInfo asset, string extension)
        {
            if (asset == null || string.IsNullOrWhiteSpace(asset.OutputFolder))
            {
                return null;
            }

            var direct = Path.Combine(asset.OutputFolder, FixFileName(asset.Name) + extension);
            if (File.Exists(direct))
            {
                return direct;
            }

            return Directory.Exists(asset.OutputFolder)
                ? Directory.GetFiles(asset.OutputFolder, "*" + extension, SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(x => string.Equals(Path.GetFileNameWithoutExtension(x), asset.Name, StringComparison.OrdinalIgnoreCase))
                : null;
        }

        private static JObject ToReportJson(AvatarMeshData mesh, string sourceJson)
        {
            return new JObject
            {
                ["sourceJson"] = Path.GetFullPath(sourceJson),
                ["meshName"] = mesh.MeshName,
                ["vertexCount"] = mesh.Positions.Count,
                ["indexCount"] = mesh.Indices.Count,
                ["triangleCount"] = mesh.Indices.Count / 3,
                ["hasNormals"] = mesh.Normals.Count == mesh.Positions.Count,
                ["hasTangents"] = mesh.Tangents.Count == mesh.Positions.Count,
                ["hasUv0"] = mesh.Uvs.Count == mesh.Positions.Count,
                ["bboxMin"] = new JArray(mesh.Min.X, mesh.Min.Y, mesh.Min.Z),
                ["bboxMax"] = new JArray(mesh.Max.X, mesh.Max.Y, mesh.Max.Z),
                ["skin"] = mesh.Skin.ToJson(),
                ["warnings"] = new JArray(mesh.Warnings)
            };
        }

        private static JObject ToReportJson(VisualCellPart part, VisualCellMaterialBinding materialBinding)
        {
            var meshJson = ToReportJson(part.Mesh, part.MeshJsonPath);
            meshJson["assistantPathId"] = part.AssistantPathId;
            meshJson["gameObjectName"] = part.GameObjectName;
            meshJson["rendererFile"] = part.RendererFile;
            meshJson["rendererPathId"] = part.RendererPathId;
            meshJson["avatarMeshPathId"] = part.AvatarMeshPathId;
            meshJson["assistantJson"] = Path.GetFullPath(part.AssistantJsonPath);
            meshJson["materialBinding"] = materialBinding?.ToJson();
            return meshJson;
        }

        private static Dictionary<string, VisualCellMaterialBinding> LoadVisualCellMaterialBindings(
            string sourceIndexPath,
            IReadOnlyCollection<VisualCellPart> parts,
            List<string> warnings)
        {
            var result = parts.ToDictionary(
                x => GetRendererKey(x.RendererFile, x.RendererPathId),
                x => new VisualCellMaterialBinding
                {
                    Status = string.IsNullOrWhiteSpace(sourceIndexPath) ? "sourceIndexNotProvided" : "missingRendererMaterial",
                    RendererFile = x.RendererFile,
                    RendererPathId = x.RendererPathId,
                },
                StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(sourceIndexPath))
            {
                return result;
            }
            if (!File.Exists(sourceIndexPath))
            {
                warnings.Add($"missingSourceIndex:{sourceIndexPath}");
                foreach (var binding in result.Values)
                {
                    binding.Status = "sourceIndexMissing";
                }
                return result;
            }

            try
            {
                SQLitePCL.Batteries_V2.Init();
                using var connection = new SqliteConnection($"Data Source={sourceIndexPath};Mode=ReadOnly");
                connection.Open();
                foreach (var part in parts)
                {
                    var key = GetRendererKey(part.RendererFile, part.RendererPathId);
                    if (!result.TryGetValue(key, out var binding))
                    {
                        continue;
                    }

                    using var command = connection.CreateCommand();
                    command.CommandText = @"
SELECT mat.id,
       mat.to_file,
       mat.to_path_id
FROM source_relations mat
WHERE mat.relation = 'renderer.material'
  AND mat.from_file = $rendererFile COLLATE NOCASE
  AND mat.from_path_id = $rendererPathId
ORDER BY mat.id;";
                    command.Parameters.AddWithValue("$rendererFile", part.RendererFile ?? string.Empty);
                    command.Parameters.AddWithValue("$rendererPathId", part.RendererPathId);
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var materialFile = reader.GetString(1);
                        var materialPathId = reader.GetInt64(2);
                        binding.Materials.Add(new VisualCellMaterialRef
                        {
                            RelationId = reader.GetInt64(0),
                            MaterialFile = materialFile,
                            MaterialPathId = materialPathId,
                            TextureRefCount = CountMaterialTextureRefs(connection, materialFile, materialPathId),
                        });
                    }

                    binding.Status = binding.Materials.Count > 0
                        ? "sourceIndexRendererMaterial"
                        : "missingRendererMaterial";
                }
            }
            catch (Exception ex) when (ex is IOException || ex is SqliteException || ex is InvalidDataException)
            {
                warnings.Add($"sourceIndexMaterialQueryFailed:{ex.Message}");
                foreach (var binding in result.Values)
                {
                    binding.Status = "sourceIndexQueryFailed";
                }
            }

            return result;
        }

        private static int CountMaterialTextureRefs(SqliteConnection connection, string materialFile, long materialPathId)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT COUNT(*)
FROM source_relations
WHERE relation = 'material.texture'
  AND from_file = $materialFile COLLATE NOCASE
  AND from_path_id = $materialPathId;";
            command.Parameters.AddWithValue("$materialFile", materialFile ?? string.Empty);
            command.Parameters.AddWithValue("$materialPathId", materialPathId);
            return Convert.ToInt32(command.ExecuteScalar() ?? 0);
        }

        private static string GetRendererKey(string rendererFile, long rendererPathId)
        {
            return $"{rendererFile ?? string.Empty}#{rendererPathId}";
        }

        private static string GetMaterialKey(string materialFile, long materialPathId)
        {
            return $"{materialFile ?? string.Empty}#{materialPathId}";
        }

        private static JObject TryLoadVisualCell(IEnumerable<string> jsonFiles)
        {
            foreach (var jsonFile in jsonFiles)
            {
                try
                {
                    var json = JObject.Parse(File.ReadAllText(jsonFile));
                    if (json["lod0RendererAssistants"] is JArray)
                    {
                        return json;
                    }
                }
                catch (JsonException)
                {
                    // 目录里只应该有 TypeTree JSON；坏文件留给后续读取时报错。
                }
            }

            return null;
        }

        private static Dictionary<long, ManifestEntry> LoadManifest(string jsonFolder)
        {
            var result = new Dictionary<long, ManifestEntry>();
            var manifestPath = Path.Combine(jsonFolder, "export_manifest.jsonl");
            if (!File.Exists(manifestPath))
            {
                var parent = Path.GetDirectoryName(Path.GetFullPath(jsonFolder));
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    manifestPath = Path.Combine(parent, "export_manifest.jsonl");
                }
            }
            if (!File.Exists(manifestPath))
            {
                return result;
            }

            foreach (var line in File.ReadLines(manifestPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var item = JObject.Parse(line);
                var pathId = item["pathId"]?.Value<long>() ?? 0;
                if (pathId == 0)
                {
                    continue;
                }

                var name = (string)item["name"] ?? string.Empty;
                var fileName = ToExportedJsonFileName(name);
                var jsonPath = Path.Combine(jsonFolder, fileName);
                if (!File.Exists(jsonPath))
                {
                    var output = (string)item["output"];
                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        jsonPath = Path.Combine(output, fileName);
                    }
                }

                result[pathId] = new ManifestEntry(
                    jsonPath,
                    ExtractSerializedFile((string)item["source"]));
            }

            return result;
        }

        private static string ExtractSerializedFile(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return string.Empty;
            }

            var separator = source.LastIndexOf('|');
            return separator >= 0 && separator + 1 < source.Length
                ? source.Substring(separator + 1)
                : Path.GetFileName(source);
        }

        private static string ToExportedJsonFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "MonoBehaviour.json";
            }

            var monoMatch = Regex.Match(name, @"^MonoBehaviour#(?<id>\d+)$", RegexOptions.IgnoreCase);
            if (monoMatch.Success)
            {
                return "MonoBehaviour_" + monoMatch.Groups["id"].Value + ".json";
            }

            return FixFileName(name) + ".json";
        }

        private static IEnumerable<long> ReadPPtrPathIds(JArray array)
        {
            if (array == null)
            {
                yield break;
            }

            foreach (var item in array)
            {
                var pathId = ReadPPtrPathId(item);
                if (pathId != 0)
                {
                    yield return pathId;
                }
            }
        }

        private static long ReadPPtrPathId(JToken token)
        {
            if (token == null)
            {
                return 0;
            }

            return token["m_PathID"]?.Value<long>()
                ?? token["pathId"]?.Value<long>()
                ?? 0;
        }

        private static (Vector3Value Min, Vector3Value Max) CalculateBounds(IEnumerable<AvatarMeshData> meshes)
        {
            var list = meshes.ToList();
            return (
                new Vector3Value(
                    list.Min(x => x.Min.X),
                    list.Min(x => x.Min.Y),
                    list.Min(x => x.Min.Z)),
                new Vector3Value(
                    list.Max(x => x.Max.X),
                    list.Max(x => x.Max.Y),
                    list.Max(x => x.Max.Z)));
        }

        private static AvatarMeshData ReadMesh(JObject json, string source)
        {
            var meshName = (string)json["m_MeshName"];
            if (string.IsNullOrWhiteSpace(meshName))
            {
                meshName = (string)json["m_Name"] ?? Path.GetFileNameWithoutExtension(source);
            }

            var vertices = json["m_VertexData"] as JArray;
            var indices = json["m_Indices"] as JArray;
            if (vertices == null || vertices.Count == 0)
            {
                throw new InvalidDataException("AvatarMeshDataAsset JSON missing m_VertexData.");
            }
            if (indices == null || indices.Count == 0)
            {
                throw new InvalidDataException("AvatarMeshDataAsset JSON missing m_Indices.");
            }

            var result = new AvatarMeshData(meshName);
            foreach (var vertex in vertices.OfType<JObject>())
            {
                result.Positions.Add(ReadVector3(vertex["position"], "position"));
                if (vertex["normal"] != null)
                {
                    result.Normals.Add(ReadVector3(vertex["normal"], "normal"));
                }
                if (vertex["tangent"] != null)
                {
                    result.Tangents.Add(ReadVector4(vertex["tangent"], "tangent"));
                }
            }

            foreach (var token in indices)
            {
                var index = checked((int)token.Value<long>());
                if (index < 0 || index >= result.Positions.Count)
                {
                    throw new InvalidDataException($"AvatarMeshDataAsset index out of range: {index}, vertexCount={result.Positions.Count}.");
                }
                result.Indices.Add(index);
            }

            if (result.Indices.Count % 3 != 0)
            {
                result.Warnings.Add("indexCountNotMultipleOf3");
            }

            result.Uvs.AddRange(ReadUv0(json, result.Positions.Count));
            result.Skin = ReadSkinSummary(json, result.Positions.Count, result.Warnings);
            if (result.Uvs.Count != 0 && result.Uvs.Count != result.Positions.Count)
            {
                result.Warnings.Add("uvCountMismatch");
            }
            if (result.Normals.Count != 0 && result.Normals.Count != result.Positions.Count)
            {
                result.Warnings.Add("normalCountMismatch");
            }
            if (result.Tangents.Count != 0 && result.Tangents.Count != result.Positions.Count)
            {
                result.Warnings.Add("tangentCountMismatch");
            }

            result.CalculateBounds();
            return result;
        }

        private static SkinSummary ReadSkinSummary(JObject json, int vertexCount, List<string> warnings)
        {
            // Naraka 的 AvatarMeshDataAsset 里已经能看到权重和 bind pose，
            // 但 joint 名称/骨骼路径还不在这些字段里，所以这里先只记录证据，不写 glTF skin。
            var summary = new SkinSummary
            {
                AnimSkinDataCount = (json["m_AnimSkinData"] as JArray)?.Count ?? 0,
                AvatarSkinDataCount = (json["m_AvatarSkinData"] as JArray)?.Count ?? 0,
                AvatarBoneOffsetCount = (json["m_AvatarBoneOffsetCount"] as JArray)?.Count ?? 0,
                AvatarBoneWeightsCount = (json["m_AvatarBoneWeights"] as JArray)?.Count ?? 0,
                BindPoseCount = (json["m_BindPoses"] as JArray)?.Count ?? 0,
            };

            var animSkinData = json["m_AnimSkinData"] as JArray;
            if (animSkinData != null)
            {
                if (animSkinData.Count != vertexCount)
                {
                    warnings.Add("animSkinDataCountMismatch");
                    summary.LayoutWarnings.Add("animSkinDataCountMismatch");
                }

                var bones = new SortedSet<int>();
                var weightedVertices = 0;
                foreach (var item in animSkinData.OfType<JObject>())
                {
                    var weights = item["boneWeight"];
                    var indices = item["boneIndex"];
                    var hasWeight = false;
                    for (var i = 0; i < 4; i++)
                    {
                        var weight = ReadOptionalVectorComponent(weights, i);
                        if (weight <= 0)
                        {
                            continue;
                        }

                        hasWeight = true;
                        var boneIndex = (int)Math.Round(ReadOptionalVectorComponent(indices, i));
                        bones.Add(boneIndex);
                    }

                    if (hasWeight)
                    {
                        weightedVertices++;
                    }
                }

                summary.AnimSkinWeightedVertexCount = weightedVertices;
                summary.AnimSkinUniqueBoneCount = bones.Count;
                summary.AnimSkinMinBoneIndex = bones.Count == 0 ? null : bones.Min;
                summary.AnimSkinMaxBoneIndex = bones.Count == 0 ? null : bones.Max;
            }

            var offsets = json["m_AvatarBoneOffsetCount"] as JArray;
            var avatarWeights = json["m_AvatarBoneWeights"] as JArray;
            if (offsets != null || avatarWeights != null)
            {
                if (offsets == null || avatarWeights == null)
                {
                    warnings.Add("avatarBoneWeightLayoutIncomplete");
                    summary.LayoutWarnings.Add("avatarBoneWeightLayoutIncomplete");
                }
                else if (offsets.Count != vertexCount * 2)
                {
                    warnings.Add("avatarBoneOffsetCountMismatch");
                    summary.LayoutWarnings.Add("avatarBoneOffsetCountMismatch");
                }
                else
                {
                    var avatarBones = new SortedSet<int>();
                    var negativeBoneRefs = 0;
                    var maxInfluences = 0;
                    var invalidRanges = 0;
                    var weightedVertices = 0;
                    for (var vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
                    {
                        var offset = offsets[vertexIndex * 2].Value<int>();
                        var count = offsets[vertexIndex * 2 + 1].Value<int>();
                        maxInfluences = Math.Max(maxInfluences, count);
                        if (offset < 0 || count < 0 || offset + count > avatarWeights.Count)
                        {
                            invalidRanges++;
                            continue;
                        }

                        var hasWeight = false;
                        for (var i = 0; i < count; i++)
                        {
                            var weight = avatarWeights[offset + i] as JObject;
                            if (weight == null)
                            {
                                continue;
                            }

                            var value = (float?)weight["m_Weight"] ?? 0f;
                            var boneIndex = (int?)weight["m_BoneIndex"] ?? -1;
                            if (value <= 0)
                            {
                                continue;
                            }

                            hasWeight = true;
                            if (boneIndex < 0)
                            {
                                negativeBoneRefs++;
                            }
                            else
                            {
                                avatarBones.Add(boneIndex);
                            }
                        }

                        if (hasWeight)
                        {
                            weightedVertices++;
                        }
                    }

                    if (invalidRanges > 0)
                    {
                        warnings.Add("avatarBoneWeightRangeInvalid");
                        summary.LayoutWarnings.Add("avatarBoneWeightRangeInvalid");
                    }

                    summary.AvatarWeightedVertexCount = weightedVertices;
                    summary.AvatarUniqueBoneCount = avatarBones.Count;
                    summary.AvatarMinBoneIndex = avatarBones.Count == 0 ? null : avatarBones.Min;
                    summary.AvatarMaxBoneIndex = avatarBones.Count == 0 ? null : avatarBones.Max;
                    summary.AvatarNegativeBoneRefCount = negativeBoneRefs;
                    summary.AvatarMaxInfluenceCount = maxInfluences;
                    summary.AvatarInvalidRangeCount = invalidRanges;
                }
            }

            summary.Status = summary.HasAnySkinField
                ? "presentUnmapped"
                : "absent";
            summary.BindingStatus = summary.HasAnySkinField
                ? "missingJointMapping"
                : "noSourceSkinData";
            return summary;
        }

        private static float ReadOptionalVectorComponent(JToken token, int index)
        {
            var name = index switch
            {
                0 => "x",
                1 => "y",
                2 => "z",
                _ => "w",
            };
            return token?[name]?.Value<float>() ?? 0f;
        }

        private static IEnumerable<Vector2Value> ReadUv0(JObject json, int vertexCount)
        {
            var hairUvSet = json["m_HairUVSetData"] as JArray;
            if (hairUvSet != null && hairUvSet.Count == vertexCount)
            {
                foreach (var item in hairUvSet.OfType<JObject>())
                {
                    var uv = item["uv0"];
                    if (uv == null)
                    {
                        yield break;
                    }
                    yield return new Vector2Value(ReadFloat(uv, "x"), 1f - ReadFloat(uv, "y"));
                }
                yield break;
            }

            var uvData = json["m_UVData"] as JArray;
            if (uvData != null && uvData.Count == vertexCount)
            {
                foreach (var item in uvData)
                {
                    yield return new Vector2Value(ReadFloat(item, "x"), 1f - ReadFloat(item, "y"));
                }
            }
        }

        private static Vector3Value ReadVector3(JToken token, string field)
        {
            if (token == null)
            {
                throw new InvalidDataException($"AvatarMeshDataAsset vertex missing {field}.");
            }
            return new Vector3Value(ReadFloat(token, "x"), ReadFloat(token, "y"), ReadFloat(token, "z"));
        }

        private static Vector4Value ReadVector4(JToken token, string field)
        {
            if (token == null)
            {
                throw new InvalidDataException($"AvatarMeshDataAsset vertex missing {field}.");
            }
            return new Vector4Value(ReadFloat(token, "x"), ReadFloat(token, "y"), ReadFloat(token, "z"), ReadFloat(token, "w"));
        }

        private static float ReadFloat(JToken token, string name)
        {
            var value = token?[name];
            if (value == null)
            {
                throw new InvalidDataException($"AvatarMeshDataAsset field missing float {name}.");
            }
            return value.Value<float>();
        }

        private static int WriteFloatBufferView(Stream stream, JArray bufferViews, IEnumerable<float> values, int target)
        {
            Align4(stream);
            var offset = stream.Length;
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            foreach (var value in values)
            {
                writer.Write(value);
            }
            writer.Flush();
            return AddBufferView(bufferViews, offset, stream.Length - offset, target);
        }

        private static int WriteUIntBufferView(Stream stream, JArray bufferViews, IEnumerable<uint> values, int target)
        {
            Align4(stream);
            var offset = stream.Length;
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            foreach (var value in values)
            {
                writer.Write(value);
            }
            writer.Flush();
            return AddBufferView(bufferViews, offset, stream.Length - offset, target);
        }

        private static int AddBufferView(JArray bufferViews, long offset, long length, int target)
        {
            bufferViews.Add(new JObject
            {
                ["buffer"] = 0,
                ["byteOffset"] = offset,
                ["byteLength"] = length,
                ["target"] = target
            });
            return bufferViews.Count - 1;
        }

        private static void Align4(Stream stream)
        {
            while (stream.Length % 4 != 0)
            {
                stream.WriteByte(0);
            }
        }

        private static IEnumerable<float> ToFloatArray2(Vector2Value value)
        {
            yield return value.X;
            yield return value.Y;
        }

        private static IEnumerable<float> ToFloatArray3(Vector3Value value)
        {
            yield return value.X;
            yield return value.Y;
            yield return value.Z;
        }

        private static IEnumerable<float> ToFloatArray4(Vector4Value value)
        {
            yield return value.X;
            yield return value.Y;
            yield return value.Z;
            yield return value.W;
        }

        private static string FixFileName(string value)
        {
            value = string.IsNullOrWhiteSpace(value) ? "avatar_mesh_data" : value.Trim();
            value = Path.GetInvalidFileNameChars().Aggregate(value, (current, c) => current.Replace(c, '_'));
            value = Regex.Replace(value, @"\s+", "_");
            return value.Length == 0 ? "avatar_mesh_data" : value;
        }

        private sealed record ManifestEntry(
            string JsonPath,
            string SerializedFile);

        private sealed record VisualCellPart(
            long AssistantPathId,
            string GameObjectName,
            string RendererFile,
            long RendererPathId,
            long AvatarMeshPathId,
            string AssistantJsonPath,
            string MeshJsonPath,
            AvatarMeshData Mesh);

        private sealed class VisualCellMaterialBinding
        {
            public string Status { get; set; }
            public string RendererFile { get; set; }
            public long RendererPathId { get; set; }
            public List<VisualCellMaterialRef> Materials { get; } = new List<VisualCellMaterialRef>();

            public JObject ToJson()
            {
                return new JObject
                {
                    ["status"] = Status,
                    ["rendererFile"] = RendererFile,
                    ["rendererPathId"] = RendererPathId,
                    ["materials"] = new JArray(Materials.Select(x => x.ToJson())),
                    ["rule"] = "材质证据只来自 SQLite 源索引里的 renderer.material / material.texture 关系；这里不烘焙 shader，也不猜贴图用途。"
                };
            }
        }

        private sealed class VisualCellMaterialRef
        {
            public long RelationId { get; init; }
            public string MaterialFile { get; init; }
            public long MaterialPathId { get; init; }
            public string MaterialType { get; init; }
            public string MaterialName { get; init; }
            public int TextureRefCount { get; init; }

            public JObject ToJson()
            {
                return new JObject
                {
                    ["relationId"] = RelationId,
                    ["materialFile"] = MaterialFile,
                    ["materialPathId"] = MaterialPathId,
                    ["materialType"] = MaterialType,
                    ["materialName"] = MaterialName,
                    ["textureRefCount"] = TextureRefCount
                };
            }
        }

        private sealed record ExportedAssetInfo(
            string Type,
            string Name,
            string OutputFolder);

        private sealed class AvatarMeshData
        {
            public AvatarMeshData(string meshName)
            {
                MeshName = meshName;
            }

            public string MeshName { get; }
            public List<int> Indices { get; } = new();
            public List<Vector3Value> Positions { get; } = new();
            public List<Vector3Value> Normals { get; } = new();
            public List<Vector4Value> Tangents { get; } = new();
            public List<Vector2Value> Uvs { get; } = new();
            public List<string> Warnings { get; } = new();
            public SkinSummary Skin { get; set; } = new();
            public Vector3Value Min { get; private set; }
            public Vector3Value Max { get; private set; }

            public void CalculateBounds()
            {
                Min = new Vector3Value(
                    Positions.Min(x => x.X),
                    Positions.Min(x => x.Y),
                    Positions.Min(x => x.Z));
                Max = new Vector3Value(
                    Positions.Max(x => x.X),
                    Positions.Max(x => x.Y),
                    Positions.Max(x => x.Z));
            }
        }

        private sealed class SkinSummary
        {
            public string Status { get; set; } = "absent";
            public string BindingStatus { get; set; } = "noSourceSkinData";
            public int AnimSkinDataCount { get; set; }
            public int AnimSkinWeightedVertexCount { get; set; }
            public int AnimSkinUniqueBoneCount { get; set; }
            public int? AnimSkinMinBoneIndex { get; set; }
            public int? AnimSkinMaxBoneIndex { get; set; }
            public int AvatarSkinDataCount { get; set; }
            public int AvatarBoneOffsetCount { get; set; }
            public int AvatarBoneWeightsCount { get; set; }
            public int AvatarWeightedVertexCount { get; set; }
            public int AvatarUniqueBoneCount { get; set; }
            public int? AvatarMinBoneIndex { get; set; }
            public int? AvatarMaxBoneIndex { get; set; }
            public int AvatarNegativeBoneRefCount { get; set; }
            public int AvatarMaxInfluenceCount { get; set; }
            public int AvatarInvalidRangeCount { get; set; }
            public int BindPoseCount { get; set; }
            public List<string> LayoutWarnings { get; } = new();

            public bool HasAnySkinField =>
                AnimSkinDataCount > 0
                || AvatarSkinDataCount > 0
                || AvatarBoneOffsetCount > 0
                || AvatarBoneWeightsCount > 0
                || BindPoseCount > 0;

            public JObject ToJson()
            {
                return new JObject
                {
                    ["status"] = Status,
                    ["bindingStatus"] = BindingStatus,
                    ["animSkinDataCount"] = AnimSkinDataCount,
                    ["animSkinWeightedVertexCount"] = AnimSkinWeightedVertexCount,
                    ["animSkinUniqueBoneCount"] = AnimSkinUniqueBoneCount,
                    ["animSkinMinBoneIndex"] = AnimSkinMinBoneIndex,
                    ["animSkinMaxBoneIndex"] = AnimSkinMaxBoneIndex,
                    ["avatarSkinDataCount"] = AvatarSkinDataCount,
                    ["avatarBoneOffsetCount"] = AvatarBoneOffsetCount,
                    ["avatarBoneWeightsCount"] = AvatarBoneWeightsCount,
                    ["avatarWeightedVertexCount"] = AvatarWeightedVertexCount,
                    ["avatarUniqueBoneCount"] = AvatarUniqueBoneCount,
                    ["avatarMinBoneIndex"] = AvatarMinBoneIndex,
                    ["avatarMaxBoneIndex"] = AvatarMaxBoneIndex,
                    ["avatarNegativeBoneRefCount"] = AvatarNegativeBoneRefCount,
                    ["avatarMaxInfluenceCount"] = AvatarMaxInfluenceCount,
                    ["avatarInvalidRangeCount"] = AvatarInvalidRangeCount,
                    ["bindPoseCount"] = BindPoseCount,
                    ["mappedJointCount"] = 0,
                    ["layoutWarnings"] = new JArray(LayoutWarnings),
                    ["rule"] = "只记录 AvatarMeshDataAsset 里的 skin 字段证据；缺少确定性 joint 名称/路径映射前不写 glTF skin。"
                };
            }
        }

        private readonly record struct Vector2Value(float X, float Y);
        private readonly record struct Vector3Value(float X, float Y, float Z);
        private readonly record struct Vector4Value(float X, float Y, float Z, float W);
    }
}
