using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AnimeStudio.CLI
{
    internal static class AvatarMeshDataGltfExporter
    {
        // Naraka 发型染色相关脚本目前只作为配置域线索，不能直接当作当前模型的颜色来源。
        private static readonly string[] HairCustomizationScriptNames =
        {
            "HairCustomConfigAsset",
            "SpecialHairCustomColorData",
            "SpecialHairCustomPartDecorator",
            "SpecialHairCustomPartDecoratorGroup",
            "UIHairConfigCustomCellAdapter",
            "UIHairConfigAdvCustomCellAdapter",
            "UIHairConfigCustomCellHolderSerialize",
            "UIHairConfigAdvCustomCellHolderSerialize",
            "UIHairEffectCustomPanelSerialize",
        };

        public static string Export(
            string jsonPath,
            string outputFolder,
            string sourceIndexPath = null,
            bool allowExternalSkeletonSkinDiagnostic = false,
            bool allowFaceRuntimeSkinDiagnostic = false)
        {
            if (!string.IsNullOrWhiteSpace(jsonPath) && Directory.Exists(jsonPath))
            {
                return ExportDirectory(jsonPath, outputFolder, sourceIndexPath, allowExternalSkeletonSkinDiagnostic, allowFaceRuntimeSkinDiagnostic);
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

            // AvatarMeshDataAsset 里的顶点是 Unity 坐标，glTF 需要 Y-up。
            // 这里在写出入口统一转换，读取层仍保留原始 JSON 证据。
            var gltfPositions = mesh.Positions.Select(ConvertUnityPositionToGltf).ToArray();
            var (gltfMin, gltfMax) = CalculateBounds(gltfPositions);
            var positionView = WriteFloatBufferView(stream, bufferViews, gltfPositions.SelectMany(ToFloatArray3), 34962);
            accessors.Add(new JObject
            {
                ["bufferView"] = positionView,
                ["componentType"] = 5126,
                ["count"] = mesh.Positions.Count,
                ["type"] = "VEC3",
                ["min"] = new JArray(gltfMin.X, gltfMin.Y, gltfMin.Z),
                ["max"] = new JArray(gltfMax.X, gltfMax.Y, gltfMax.Z)
            });
            attributes["POSITION"] = accessors.Count - 1;

            if (mesh.Normals.Count == mesh.Positions.Count)
            {
                var normalView = WriteFloatBufferView(stream, bufferViews, mesh.Normals.Select(ConvertUnityDirectionToGltf).SelectMany(ToFloatArray3), 34962);
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
                var tangentView = WriteFloatBufferView(stream, bufferViews, mesh.Tangents.Select(ConvertUnityTangentToGltf).SelectMany(ToFloatArray4), 34962);
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
                        ["unityCoordinateSystemPreserved"] = false,
                        ["axisConversion"] = "Unity(x,y,z) -> glTF(x,z,-y)",
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

        private static string ExportDirectory(
            string jsonFolder,
            string outputFolder,
            string sourceIndexPath,
            bool allowExternalSkeletonSkinDiagnostic,
            bool allowFaceRuntimeSkinDiagnostic)
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
                return ExportVisualCellDirectory(jsonFolder, outputFolder, visualCell, sourceIndexPath, allowExternalSkeletonSkinDiagnostic, allowFaceRuntimeSkinDiagnostic);
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
                    ["unityCoordinateSystemPreserved"] = false,
                    ["axisConversion"] = "Unity(x,y,z) -> glTF(x,z,-y)",
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

        private static string ExportVisualCellDirectory(
            string jsonFolder,
            string outputFolder,
            VisualCellJson visualCellSource,
            string sourceIndexPath,
            bool allowExternalSkeletonSkinDiagnostic,
            bool allowFaceRuntimeSkinDiagnostic)
        {
            var visualCell = visualCellSource.Json;
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
            var selectedVisualCell = ResolveSelectedVisualCellInfo(visualCellSource, manifest, sourceIndexPath, warnings);
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
                    meshEntry.SerializedFile,
                    avatarMeshPathId,
                    assistantEntry.JsonPath,
                    meshEntry.JsonPath,
                    mesh));
            }

            if (parts.Count == 0)
            {
                throw new InvalidDataException("ActorBodyVisualCell LOD0 has no resolved AvatarMeshDataAsset JSON.");
            }
            var headCollisionEvidence = LoadAssistantHeadCollisionEvidence(sourceIndexPath, parts, warnings);

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
            var rendererSkinBindings = LoadVisualCellRendererSkinBindings(sourceIndexPath, parts, warnings);
            AttachSkinnedMeshRendererJsonEvidence(jsonFolder, rendererSkinBindings.Values, warnings);
            var avatarMeshRelationEvidence = LoadAvatarMeshDataRelationEvidence(sourceIndexPath, parts, warnings);
            var avatarPartDataEvidence = LoadAvatarPartDataEvidence(jsonFolder, manifest, parts, sourceIndexPath, warnings);
            var boneDriverHints = LoadBoneDriverHints(jsonFolder, manifest, sourceIndexPath, warnings);
            var selectedBoneDriverHints = GetSelectedVisualCellBoneDriverHints(boneDriverHints, selectedVisualCell).ToArray();
            var exportedTransformNodes = LoadExportedTransformNodes(jsonFolder, warnings);
            var transformNodeTables = LoadTransformNodeTableCandidates(sourceIndexPath, parts, warnings);
            var rendererListEvidence = LoadVisualCellRendererListEvidence(visualCell, selectedVisualCell.SerializedFile, sourceIndexPath, warnings);
            var childComponentEvidence = LoadSelectedVisualCellChildComponentEvidence(selectedVisualCell, sourceIndexPath, warnings);
            var boneDriverNodeNames = BuildBoneDriverNodeNameSet(boneDriverHints);
            foreach (var part in parts)
            {
                part.Mesh.Skin.TransformNodeTableCandidates.AddRange(BuildTransformNodeTableCandidates(part.Mesh.Skin, transformNodeTables, part.AvatarMeshFile, exportedTransformNodes, boneDriverNodeNames, selectedVisualCell.PathId));
            }
            var transformNodeTableCandidates = GetDistinctTransformNodeTableCandidates(parts).ToArray();
            var externalSkeletonContextCandidate = SelectExternalSkeletonContextCandidate(transformNodeTableCandidates);
            var totalBounds = CalculateBounds(parts.Select(x => x.Mesh));
            var transformNodeTableCandidateStatus = SummarizeTransformNodeTableCandidateStatus(transformNodeTableCandidates);
            var selectedTransformNodeTableStatus = SummarizeSelectedVisualCellTransformNodeTableStatus(transformNodeTableCandidates, selectedVisualCell);
            var usedBoneCoverageCandidateCount = transformNodeTableCandidates.Count(x => x.CoversAllUsedBoneIndices);
            var usedBoneCoverageWithTrsCandidateCount = transformNodeTableCandidates.Count(x => x.CoversAllUsedBoneIndices && x.AllCoveredUsedBoneTransformsExported);
            var sourceSkinAvatarMaxBoneIndex = GetAvatarBoneMaxIndex(parts);
            var sourceSkinAvatarRequiredNodeCount = GetAvatarBoneRequiredNodeCount(parts);
            var selectedTransformNodeRequiredCount = GetSelectedVisualCellTransformNodeRequiredCount(parts, transformNodeTableCandidates);
            var selectedTransformNodeMissingCount = GetSelectedVisualCellTransformNodeMissingCount(parts, transformNodeTableCandidates, selectedVisualCell);
            var selectedTransformNodeCoverageStatus = SummarizeSelectedVisualCellTransformNodeCoverageStatus(
                selectedTransformNodeTableStatus,
                selectedTransformNodeRequiredCount,
                selectedTransformNodeMissingCount);
            var sourceSkinMappingStatus = SummarizeSourceSkinMappingStatus(
                selectedTransformNodeTableStatus,
                transformNodeTableCandidates);
            var hairDeformSummaries = parts.Select(x => x.Mesh.HairDeform).ToArray();
            var rangeCoveringMappedNodeNames = GetTransformNodeTableRangeCoveringMappedNodeNames(parts).ToArray();
            var rangeCoveringEvidence = BuildTransformNodeTableRangeCoveringEvidence(parts);
            var faceRuntimeEvidence = LoadFaceRuntimeEvidence(
                jsonFolder,
                manifest,
                parts,
                selectedVisualCell,
                sourceSkinAvatarRequiredNodeCount,
                warnings);
            var externalDiagnosticSkin = BuildExternalSkeletonDiagnosticSkin(
                nodes,
                externalSkeletonContextCandidate,
                exportedTransformNodes,
                allowExternalSkeletonSkinDiagnostic,
                warnings);
            var faceRuntimeDiagnosticSkin = BuildFaceRuntimeDiagnosticSkin(
                nodes,
                faceRuntimeEvidence,
                allowFaceRuntimeSkinDiagnostic && externalDiagnosticSkin == null,
                warnings);
            if (allowFaceRuntimeSkinDiagnostic && externalDiagnosticSkin != null)
            {
                warnings.Add("faceRuntimeDiagnosticSkinSkipped:externalSkeletonDiagnosticSkinAlreadySelected");
            }
            INarakaDiagnosticSkin diagnosticSkin = externalDiagnosticSkin != null
                ? externalDiagnosticSkin
                : faceRuntimeDiagnosticSkin;
            var sceneNodeIndices = new List<int>();
            if (diagnosticSkin != null)
            {
                sceneNodeIndices.AddRange(diagnosticSkin.RootNodeIndices);
            }

            var gltfImages = new JArray();
            var gltfTextures = new JArray();
            var materialIndexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var previewMaterialSource = ResolveVisualCellPreviewMaterialSource(jsonFolder, outputFolder);
            var gltfMaterials = BuildVisualCellPreviewMaterials(previewMaterialSource, outputFolder, materialBindings.Values, gltfImages, gltfTextures, materialIndexByKey, warnings);
            using var stream = File.Create(binPath);
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                materialBindings.TryGetValue(GetRendererKey(part.RendererFile, part.RendererPathId), out var materialBinding);
                var materialIndex = GetVisualCellMaterialIndex(materialBinding, materialIndexByKey);
                avatarPartDataEvidence.TryGetValue(part.AvatarMeshPathId, out var partDataEvidence);
                var nodeAvatarPartDataEvidence = partDataEvidence != null
                    ? (IEnumerable<AvatarPartDataEvidence>)partDataEvidence
                    : Enumerable.Empty<AvatarPartDataEvidence>();
                gltfMeshes.Add(WriteMeshObject(stream, bufferViews, accessors, part.Mesh, Path.GetFullPath(part.MeshJsonPath), materialIndex, diagnosticSkin));
                var node = new JObject
                {
                    ["name"] = part.GameObjectName,
                    ["mesh"] = i,
                    ["extras"] = new JObject
                    {
                        ["assistantPathId"] = part.AssistantPathId,
                        ["rendererFile"] = part.RendererFile,
                        ["rendererPathId"] = part.RendererPathId,
                        ["avatarMeshPathId"] = part.AvatarMeshPathId,
                        ["materialBinding"] = materialBinding?.ToJson(),
                        ["avatarPartDataEvidence"] = new JArray(nodeAvatarPartDataEvidence.Select(x => x.ToJson()))
                    }
                };
                if (diagnosticSkin != null)
                {
                    node["skin"] = 0;
                    node["extras"]["narakaDiagnosticSkin"] = diagnosticSkin.ToNodeJson();
                }
                nodes.Add(node);
                sceneNodeIndices.Add(nodes.Count - 1);
            }
            var skins = new JArray();
            if (diagnosticSkin != null)
            {
                skins.Add(diagnosticSkin.BuildSkinJson(stream, bufferViews, accessors));
            }
            var gltf = new JObject
            {
                ["asset"] = new JObject
                {
                    ["version"] = "2.0",
                    ["generator"] = "AnimeStudio Naraka ActorBodyVisualCell diagnostic LOD exporter"
                },
                ["scene"] = 0,
                ["scenes"] = new JArray(new JObject { ["nodes"] = new JArray(sceneNodeIndices.Distinct()) }),
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
                    ["selectedVisualCellPathId"] = selectedVisualCell.PathId,
                    ["selectedVisualCellGameObjectName"] = selectedVisualCell.GameObjectName,
                    ["selectedLodGroup"] = "lod0RendererAssistants",
                    ["diagnosticOnly"] = true,
                    ["materialBindingSourceIndex"] = string.IsNullOrWhiteSpace(sourceIndexPath) ? null : Path.GetFullPath(sourceIndexPath),
                    ["previewMaterialSource"] = gltfMaterials.Count > 1 && !string.IsNullOrWhiteSpace(previewMaterialSource) ? Path.GetFullPath(previewMaterialSource) : null,
                    ["unityCoordinateSystemPreserved"] = false,
                    ["axisConversion"] = "Unity(x,y,z) -> glTF(x,z,-y)",
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
            if (skins.Count > 0)
            {
                gltf["skins"] = skins;
            }

            File.WriteAllText(gltfPath, gltf.ToString(Formatting.Indented));
            var materialRefCount = materialBindings.Values.Sum(x => x.Materials.Count);
            var materialTextureRefCount = materialBindings.Values.Sum(x => x.Materials.Sum(y => y.TextureRefCount));
            var hairCustomizationTintEvidence = LoadHairCustomizationTintEvidence(sourceIndexPath, selectedVisualCell, warnings, jsonFolder);
            var report = new JObject
            {
                ["status"] = warnings.Count == 0 ? "ok" : "warning",
                ["kind"] = "NarakaActorBodyVisualCellLodGltf",
                ["sourceDirectory"] = Path.GetFullPath(jsonFolder),
                ["output"] = gltfPath,
                ["selectedLodGroup"] = "lod0RendererAssistants",
                ["selectedVisualCellJson"] = Path.GetFullPath(visualCellSource.JsonPath),
                ["selectedVisualCellFile"] = selectedVisualCell.SerializedFile,
                ["selectedVisualCellPathId"] = selectedVisualCell.PathId,
                ["selectedVisualCellGameObjectPathId"] = selectedVisualCell.GameObjectPathId,
                ["selectedVisualCellGameObjectName"] = selectedVisualCell.GameObjectName,
                ["selectedVisualCellTransformPathId"] = selectedVisualCell.TransformPathId,
                ["selectedVisualCellTransformPath"] = selectedVisualCell.TransformPath,
                ["selectedVisualCellTransformPathStatus"] = selectedVisualCell.TransformPathStatus,
                ["selectedVisualCellDirectChildCount"] = selectedVisualCell.DirectChildCount,
                ["selectedVisualCellDirectChildNames"] = new JArray(selectedVisualCell.DirectChildNames.Select(x => new JValue(x))),
                ["selectedVisualCellChildComponentStatus"] = childComponentEvidence.Status,
                ["selectedVisualCellChildGameObjectCount"] = childComponentEvidence.ChildGameObjectCount,
                ["selectedVisualCellChildComponentCount"] = childComponentEvidence.ComponentCount,
                ["selectedVisualCellChildSkinnedMeshRendererCount"] = childComponentEvidence.SkinnedMeshRendererCount,
                ["selectedVisualCellChildMonoBehaviourCount"] = childComponentEvidence.MonoBehaviourCount,
                ["selectedVisualCellChildMonoBehaviourScriptNames"] = new JArray(childComponentEvidence.MonoBehaviourScriptNames.Select(x => new JValue(x))),
                ["selectedVisualCellChildComponents"] = childComponentEvidence.ToJson(),
                ["selectedVisualCellTransformNodeCount"] = selectedVisualCell.TransformNodeCount,
                ["visualCellRendererAssistantCount"] = rendererListEvidence.RendererAssistantCount,
                ["visualCellAvatarRendererCount"] = rendererListEvidence.AvatarRendererCount,
                ["visualCellMeshRendererCount"] = rendererListEvidence.MeshRendererCount,
                ["visualCellLodRendererAssistantCounts"] = rendererListEvidence.LodRendererAssistantCounts.ToJson(),
                ["visualCellRendererListStatus"] = rendererListEvidence.Status,
                ["visualCellRendererListEvidence"] = rendererListEvidence.ToJson(),
                ["sourceIndex"] = string.IsNullOrWhiteSpace(sourceIndexPath) ? null : Path.GetFullPath(sourceIndexPath),
                ["assistantCount"] = selectedAssistants.Count,
                ["resolvedPartCount"] = parts.Count,
                ["meshCount"] = parts.Count,
                ["skinCount"] = diagnosticSkin != null ? 1 : 0,
                ["boneCount"] = diagnosticSkin?.JointCount ?? 0,
                ["diagnosticSkinStatus"] = diagnosticSkin?.ToJson()?["status"],
                ["diagnosticSkin"] = diagnosticSkin?.ToJson(),
                ["externalSkeletonDiagnosticSkin"] = externalDiagnosticSkin?.ToJson(),
                ["faceRuntimeDiagnosticSkin"] = faceRuntimeDiagnosticSkin?.ToJson(),
                ["vertexCount"] = parts.Sum(x => x.Mesh.Positions.Count),
                ["indexCount"] = parts.Sum(x => x.Mesh.Indices.Count),
                ["triangleCount"] = parts.Sum(x => x.Mesh.Indices.Count / 3),
                ["rendererMaterialRefCount"] = materialRefCount,
                ["materialTextureRefCount"] = materialTextureRefCount,
                ["hairCustomizationTintEvidenceStatus"] = hairCustomizationTintEvidence.Status,
                ["hairCustomizationTintEvidence"] = hairCustomizationTintEvidence.ToJson(),
                ["hairCustomizationTintRule"] = "这里只诊断 Naraka 发型 customization/tint 配置线索。只有找到和选中 VisualCell/GameObject 的确定性引用，并且字段里有可解释颜色参数时，才允许后续烘焙预览色；否则继续保持 needsCustomizationTint。",
                ["faceRuntimeEvidenceStatus"] = faceRuntimeEvidence.Status,
                ["faceRuntimeEvidence"] = faceRuntimeEvidence.ToJson(),
                ["faceRuntimeEvidenceRule"] = "这里只记录 AvatarFaceRuntime -> AvatarFaceData 的确定性 PPtr 证据。骨骼数量、名称和 bind local TRS 可用于定位 Naraka 脸部 skin 映射，但在 boneIndex 到 glTF joint 层级和视觉验证完成前，不能据此写生产 skin。",
                ["rendererSkinBindingStatus"] = SummarizeRendererSkinBindingStatus(rendererSkinBindings.Values),
                ["rendererSkinRendererRefCount"] = rendererSkinBindings.Values.Count(x => x.RendererObjectFound),
                ["rendererSkinRendererWithoutSkinRefCount"] = rendererSkinBindings.Values.Count(x => x.RendererObjectFound && !x.HasAnyRelation),
                ["rendererSkinRendererGameObjectRefCount"] = rendererSkinBindings.Values.Count(x => x.RendererGameObjectPathId.HasValue),
                ["rendererSkinRendererMaterialRefCount"] = rendererSkinBindings.Values.Sum(x => x.RendererMaterialRefCount),
                ["rendererSkinMeshRefCount"] = rendererSkinBindings.Values.Count(x => x.MeshPathId.HasValue),
                ["rendererSkinRootBoneRefCount"] = rendererSkinBindings.Values.Count(x => x.RootBonePathId.HasValue),
                ["rendererSkinBoneRefCount"] = rendererSkinBindings.Values.Sum(x => x.BoneCount ?? 0),
                ["rendererSkinJsonStatus"] = SummarizeRendererSkinJsonStatus(rendererSkinBindings.Values),
                ["rendererSkinJsonEmptyBoneRendererCount"] = rendererSkinBindings.Values.Count(x => string.Equals(x.RendererJsonStatus, "skinnedRendererJsonEmptyBones", StringComparison.OrdinalIgnoreCase)),
                ["previewMaterialSource"] = gltfMaterials.Count > 1 && !string.IsNullOrWhiteSpace(previewMaterialSource) ? Path.GetFullPath(previewMaterialSource) : null,
                ["avatarMeshExplicitReferenceStatus"] = SummarizeAvatarMeshRelationStatus(avatarMeshRelationEvidence.Values),
                ["avatarMeshRelationCount"] = avatarMeshRelationEvidence.Values.Sum(x => x.RelationCount),
                ["avatarMeshNonScriptPPtrRefCount"] = avatarMeshRelationEvidence.Values.Sum(x => x.NonScriptPPtrCount),
                ["animSkinBindPoseSlotStatus"] = SummarizeAnimSkinBindPoseSlotStatus(parts.Select(x => x.Mesh.Skin)),
                ["animSkinDirectBindPosePartCount"] = parts.Count(x => x.Mesh.Skin.AnimSkinIndicesFitBindPoseSlots),
                ["animSkinBindPoseRule"] = "m_AnimSkinData 的 boneIndex 先按 m_BindPoses 槽位做自洽检查；它和 m_AvatarBoneWeights 的大范围 avatar boneIndex 分开记录，不能混用。",
                ["avatarBoneOffsetPairStatus"] = SummarizeAvatarBoneOffsetPairStatus(parts.Select(x => x.Mesh.Skin)),
                ["avatarBoneOffsetPairExactPartCount"] = parts.Count(x => string.Equals(x.Mesh.Skin.AvatarBoneOffsetPairStatus, "continuousCoversWeights", StringComparison.OrdinalIgnoreCase)),
                ["avatarBoneOffsetPairRule"] = "m_AvatarBoneOffsetCount 当前按每顶点 [weightOffset, influenceCount] 二元组诊断；只证明权重表布局，不证明 boneIndex 到 Transform/joint 的映射。",
                ["bboxMin"] = new JArray(totalBounds.Min.X, totalBounds.Min.Y, totalBounds.Min.Z),
                ["bboxMax"] = new JArray(totalBounds.Max.X, totalBounds.Max.Y, totalBounds.Max.Z),
                ["warnings"] = new JArray(warnings),
                ["parts"] = new JArray(parts.Select(part =>
                {
                    materialBindings.TryGetValue(GetRendererKey(part.RendererFile, part.RendererPathId), out var materialBinding);
                    rendererSkinBindings.TryGetValue(GetRendererKey(part.RendererFile, part.RendererPathId), out var rendererSkinBinding);
                    avatarMeshRelationEvidence.TryGetValue(part.AvatarMeshPathId, out var avatarMeshRelations);
                    avatarPartDataEvidence.TryGetValue(part.AvatarMeshPathId, out var partData);
                    headCollisionEvidence.TryGetValue(part.AssistantPathId, out var headCollision);
                    return ToReportJson(part, materialBinding, rendererSkinBinding, avatarMeshRelations, partData, headCollision);
                })),
                ["avatarPartDataEvidenceStatus"] = SummarizeAvatarPartDataEvidenceStatus(avatarPartDataEvidence.Values.SelectMany(x => x)),
                ["avatarPartDataEvidenceCount"] = avatarPartDataEvidence.Values.Sum(x => x.Count),
                ["headCollisionDataStatus"] = SummarizeHeadCollisionDataStatus(headCollisionEvidence.Values),
                ["headCollisionDataRefCount"] = headCollisionEvidence.Values.Count(x => x.HasReference),
                ["headCollisionDataMissingRefCount"] = headCollisionEvidence.Values.Count(x => !x.HasReference),
                ["headCollisionDataSharedTargetCount"] = CountDistinctHeadCollisionTargets(headCollisionEvidence.Values),
                ["headCollisionDataResolvedTargetCount"] = headCollisionEvidence.Values.Count(x => x.TargetObjectFound),
                ["headCollisionDataEvidence"] = new JArray(headCollisionEvidence.Values.Select(x => x.ToJson())),
                ["boneDriverHintStatus"] = SummarizeBoneDriverHintStatus(boneDriverHints),
                ["boneDriverHintCount"] = boneDriverHints.Count,
                ["boneDriverHintNames"] = new JArray(GetBoneDriverNames(boneDriverHints)),
                ["boneDriverHintPaths"] = new JArray(GetBoneDriverPaths(boneDriverHints)),
                ["boneDriverHints"] = new JArray(boneDriverHints.Select(x => x.ToJson())),
                ["selectedVisualCellBoneDriverHintStatus"] = SummarizeSelectedVisualCellBoneDriverHintStatus(selectedBoneDriverHints, selectedVisualCell),
                ["selectedVisualCellBoneDriverHintCount"] = selectedBoneDriverHints.Length,
                ["selectedVisualCellBoneDriverHintNames"] = new JArray(GetBoneDriverNames(selectedBoneDriverHints)),
                ["selectedVisualCellBoneDriverHintPaths"] = new JArray(GetBoneDriverPaths(selectedBoneDriverHints)),
                ["selectedVisualCellBoneDriverHints"] = new JArray(selectedBoneDriverHints.Select(x => x.ToJson())),
                ["transformNodeTableCandidateStatus"] = transformNodeTableCandidateStatus,
                ["transformNodeTableCandidateCount"] = transformNodeTableCandidates.Length,
                ["transformNodeTableRangeCoveringCandidateCount"] = transformNodeTableCandidates.Count(x => x.CoversAvatarBoneIndexRange),
                ["transformNodeTableUsedBoneCoverageCandidateCount"] = usedBoneCoverageCandidateCount,
                ["transformNodeTableUsedBoneCoverageWithTrsCandidateCount"] = usedBoneCoverageWithTrsCandidateCount,
                ["selectedVisualCellUsedBoneCoverageCandidateCount"] = transformNodeTableCandidates.Count(x => x.IsSelectedVisualCell && x.CoversAllUsedBoneIndices),
                ["selectedVisualCellUsedBoneCoverageWithTrsCandidateCount"] = transformNodeTableCandidates.Count(x => x.IsSelectedVisualCell && x.CoversAllUsedBoneIndices && x.AllCoveredUsedBoneTransformsExported),
                ["selectedVisualCellTransformNodeTableStatus"] = selectedTransformNodeTableStatus,
                ["selectedVisualCellTransformNodeTableCandidateCount"] = transformNodeTableCandidates.Count(x => x.IsSelectedVisualCell),
                ["sourceSkinAvatarMaxBoneIndex"] = sourceSkinAvatarMaxBoneIndex,
                ["sourceSkinAvatarRequiredNodeCount"] = sourceSkinAvatarRequiredNodeCount,
                ["selectedVisualCellTransformNodeRequiredCount"] = selectedTransformNodeRequiredCount,
                ["selectedVisualCellTransformNodeMissingCount"] = selectedTransformNodeMissingCount,
                ["selectedVisualCellTransformNodeCoverageStatus"] = selectedTransformNodeCoverageStatus,
                ["sourceSkinMappingStatus"] = sourceSkinMappingStatus,
                ["sourceSkinMappingRule"] = "只有选中 ActorBodyVisualCell 自己的 transformNodes.data 覆盖 AvatarBoneWeights 的最大 boneIndex，且 joint 名称/路径映射也能确定时，才允许写 glTF skin；同包其它 VisualCell 的覆盖表只能作为诊断线索。",
                ["externalSkeletonContextCandidateStatus"] = SummarizeExternalSkeletonContextCandidateStatus(externalSkeletonContextCandidate),
                ["externalSkeletonContextCandidate"] = BuildExternalSkeletonContextCandidateJson(externalSkeletonContextCandidate),
                ["externalSkeletonContextRule"] = "外部 VisualCell 的 transformNodes 只说明当前网格可能需要完整角色/身体骨架上下文；它不能替代选中 VisualCell 自己的 joint 映射，也不能直接写 glTF skin。",
                ["transformNodeTableBoneDriverOverlapStatus"] = SummarizeTransformNodeTableBoneDriverOverlapStatus(transformNodeTableCandidates),
                ["transformNodeTableBoneDriverOverlapCandidateCount"] = transformNodeTableCandidates.Count(x => x.BoneDriverNodeNameMatchCount > 0),
                ["transformNodeJsonStatus"] = SummarizeExportedTransformNodeStatus(exportedTransformNodes),
                ["transformNodeJsonCount"] = exportedTransformNodes.Count,
                ["transformNodeJsonReadableTrsCount"] = exportedTransformNodes.Values.Count(x => x.HasLocalTrs),
                ["transformNodeTableCandidateVisualCells"] = new JArray(GetTransformNodeTableCandidateVisualCells(transformNodeTableCandidates)),
                ["transformNodeTableRangeCoveringVisualCells"] = new JArray(GetTransformNodeTableRangeCoveringVisualCells(transformNodeTableCandidates)),
                ["transformNodeTableRangeCoveringMappedNodeNames"] = new JArray(rangeCoveringMappedNodeNames.Select(x => new JValue(x))),
                ["transformNodeTableRangeCoveringEvidenceCount"] = rangeCoveringEvidence.Count,
                ["transformNodeTableRangeCoveringEvidence"] = rangeCoveringEvidence,
                ["transformNodeTableCandidateRule"] = "这些节点表只是 AvatarBoneWeights boneIndex 与 ActorBodyVisualCell.transformNodes.data 顺序的候选对照；usedBoneCoverage 只说明实际用到的 boneIndex 是否落在候选表范围内，withTrs 只说明对应 Transform JSON 已导出且可读。同 SerializedFile 内其它节点表只能诊断，不能作为 skin joint 映射。",
                ["hairDeformDataStatus"] = SummarizeHairDeformDataStatus(hairDeformSummaries),
                ["hairDeformDataPartCount"] = hairDeformSummaries.Count(x => x.Count > 0),
                ["hairDeformDataVertexCount"] = hairDeformSummaries.Sum(x => x.Count),
                ["hairDeformDataRule"] = "m_HairDeformData 当前按每顶点两个 uint32 / 四个 half-float 诊断记录；它更像头发变形/插值参数，不能作为 joint 索引或 skin 映射。",
                ["rule"] = "只按 ActorBodyVisualCell.lod0RendererAssistants -> LXRendererAssistant.avatarMeshAsset 的确定性 PPtr 选择 Naraka 自定义网格；材质引用只来自 renderer.material / material.texture 源索引关系；AvatarPartDataAsset.m_MeshData 只记录部件/LOD 顺序，不猜材质槽、骨骼或 skin。"
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
                rendererSkinBindings,
                avatarPartDataEvidence,
                headCollisionEvidence,
                boneDriverHints,
                rendererListEvidence,
                gltfMaterials,
                gltfImages,
                gltfTextures,
                diagnosticSkin,
                externalDiagnosticSkin,
                faceRuntimeDiagnosticSkin);
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
            IReadOnlyDictionary<string, VisualCellRendererSkinBinding> rendererSkinBindings,
            IReadOnlyDictionary<long, List<AvatarPartDataEvidence>> avatarPartDataEvidence,
            IReadOnlyDictionary<long, AssistantHeadCollisionEvidence> headCollisionEvidence,
            IReadOnlyList<BoneDriverHint> boneDriverHints,
            VisualCellRendererListEvidence rendererListEvidence,
            JArray gltfMaterials,
            JArray gltfImages,
            JArray gltfTextures,
            INarakaDiagnosticSkin diagnosticSkin,
            ExternalSkeletonDiagnosticSkin externalDiagnosticSkin,
            FaceRuntimeDiagnosticSkin faceRuntimeDiagnosticSkin)
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
            var transformNodeTableCandidates = GetDistinctTransformNodeTableCandidates(parts).ToArray();
            var externalSkeletonContextCandidate = SelectExternalSkeletonContextCandidate(transformNodeTableCandidates);
            var transformNodeTableCandidateStatus = SummarizeTransformNodeTableCandidateStatus(transformNodeTableCandidates);
            var selectedTransformNodeTableStatus = (string)report["selectedVisualCellTransformNodeTableStatus"];
            var usedBoneCoverageCandidateCount = transformNodeTableCandidates.Count(x => x.CoversAllUsedBoneIndices);
            var usedBoneCoverageWithTrsCandidateCount = transformNodeTableCandidates.Count(x => x.CoversAllUsedBoneIndices && x.AllCoveredUsedBoneTransformsExported);
            var selectedBoneDriverHintCount = report["selectedVisualCellBoneDriverHintCount"]?.Value<int?>() ?? 0;
            var exportedTransformNodes = LoadExportedTransformNodes(jsonFolder, new List<string>());
            var hairDeformSummaries = parts.Select(x => x.Mesh.HairDeform).ToArray();
            var hairDeformDataStatus = SummarizeHairDeformDataStatus(hairDeformSummaries);
            var hasExternalDiagnosticSkin = externalDiagnosticSkin != null;
            var hasFaceRuntimeDiagnosticSkin = faceRuntimeDiagnosticSkin != null;
            var validationReasons = new JArray(
                "diagnostic_custom_mesh",
                hasExternalDiagnosticSkin
                    ? "external_skeleton_diagnostic_skin_present"
                    : hasFaceRuntimeDiagnosticSkin
                        ? "face_runtime_diagnostic_skin_present"
                        : "missing_skin_binding",
                hasExternalDiagnosticSkin
                    ? "external_skeleton_rest_inverse_bind_matrices_diagnostic"
                    : hasFaceRuntimeDiagnosticSkin
                        ? "face_runtime_bind_local_inverse_matrices_diagnostic"
                        : "skin_inverse_bind_matrices_missing",
                parts.Any(x => x.Mesh.Skin.Status == "presentUnmapped") ? "skin_data_present_unmapped" : "skin_data_missing",
                parts.Any(x => x.Mesh.Skin.AnimSkinIndicesFitBindPoseSlots)
                    ? "anim_skin_bind_pose_slots_self_consistent"
                    : "anim_skin_bind_pose_slots_unverified",
                parts.All(x => string.Equals(x.Mesh.Skin.AvatarBoneOffsetPairStatus, "continuousCoversWeights", StringComparison.OrdinalIgnoreCase))
                    ? "avatar_bone_offset_pairs_continuous"
                    : parts.Any(x => string.Equals(x.Mesh.Skin.AvatarBoneOffsetPairStatus, "continuousCoversWeights", StringComparison.OrdinalIgnoreCase))
                        ? "avatar_bone_offset_pairs_partial"
                        : "avatar_bone_offset_pairs_unverified",
                rendererSkinBindings.Values.Any(x => string.Equals(x.RendererJsonStatus, "skinnedRendererJsonEmptyBones", StringComparison.OrdinalIgnoreCase))
                    ? "skinned_renderer_json_confirms_empty_bones"
                    : "skinned_renderer_json_missing_or_unverified",
                rendererSkinBindings.Values.Any(x => x.Status == "rendererSkinRelations")
                    ? "renderer_skin_relations_present"
                    : rendererSkinBindings.Values.Any(x => x.Status == "rendererPresentWithoutSkinRelations")
                        ? "renderer_present_without_skin_relations"
                        : "renderer_skin_relations_missing",
                string.Equals((string)report["avatarMeshExplicitReferenceStatus"], "scriptOnlyNoExplicitPPtr", StringComparison.OrdinalIgnoreCase)
                    ? "avatar_mesh_script_only_no_explicit_pptr"
                    : "avatar_mesh_explicit_reference_check",
                avatarPartDataEvidence.Values.Any(x => x.Count > 0)
                    ? "avatar_part_data_mesh_order_present"
                    : "avatar_part_data_mesh_order_missing",
                headCollisionEvidence.Values.Any(x => x.HasReference)
                    ? "head_collision_data_refs_present"
                    : "head_collision_data_refs_missing",
                boneDriverHints.Count > 0 ? "bone_driver_hints_present" : "bone_driver_hints_missing",
                selectedBoneDriverHintCount > 0
                    ? "selected_visual_cell_bone_driver_hints_present"
                    : "selected_visual_cell_bone_driver_hints_missing",
                string.Equals(rendererListEvidence?.Status, "assistantMeshRendererGameObjectsAligned", StringComparison.OrdinalIgnoreCase)
                    ? "visual_cell_renderer_lists_aligned"
                    : "visual_cell_renderer_lists_unverified",
                string.Equals((string)report["selectedVisualCellTransformPathStatus"], "resolved", StringComparison.OrdinalIgnoreCase)
                    ? "selected_visual_cell_transform_hierarchy_present"
                    : "selected_visual_cell_transform_hierarchy_missing",
                transformNodeTableCandidateStatus == "missing"
                    ? "transform_node_table_candidates_missing"
                    : transformNodeTableCandidateStatus == "indexOrderCandidatesAmbiguous"
                        ? "transform_node_table_candidates_ambiguous"
                        : "transform_node_table_candidate_present",
                selectedTransformNodeTableStatus == "selectedVisualCellHasNoTransformNodes"
                    ? "selected_visual_cell_transform_nodes_missing"
                    : selectedTransformNodeTableStatus == "selectedVisualCellPresentButInsufficientForAvatarBoneRange"
                        ? "selected_visual_cell_transform_nodes_insufficient"
                        : selectedTransformNodeTableStatus == "selectedVisualCellRangeCoveringCandidate"
                            ? "selected_visual_cell_transform_nodes_present"
                            : "selected_visual_cell_transform_nodes_unverified",
                transformNodeTableCandidates.Any(x => x.BoneDriverNodeNameMatchCount > 0)
                    ? "global_bone_driver_transform_node_overlap_present"
                    : "global_bone_driver_transform_node_overlap_missing",
                externalSkeletonContextCandidate != null
                    ? "external_skeleton_context_candidate_present"
                    : "external_skeleton_context_candidate_missing",
                hairDeformDataStatus == "missing"
                    ? "hair_deform_data_missing"
                    : hairDeformDataStatus == "countMismatch"
                        ? "hair_deform_data_count_mismatch"
                        : "hair_deform_data_packed_half4_diagnostic",
                needsCustomizationTint ? "needs_customization_tint" : "preview_material_not_full_shader");
            var faceRuntimeStatus = (string)report["faceRuntimeEvidenceStatus"];
            if (string.Equals(faceRuntimeStatus, "avatarFaceDataMatchesSourceSkinBoneTable", StringComparison.OrdinalIgnoreCase))
            {
                validationReasons.Add("face_runtime_avatar_data_matches_source_skin_bone_table_diagnostic");
            }
            else if (!string.IsNullOrWhiteSpace(faceRuntimeStatus)
                && !string.Equals(faceRuntimeStatus, "faceRuntimeEvidenceMissing", StringComparison.OrdinalIgnoreCase))
            {
                validationReasons.Add("face_runtime_avatar_data_unverified_diagnostic");
            }
            var sourceSkinStatuses = parts
                .Select(x => x.Mesh.Skin.Status)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var avatarPartEvidenceItems = avatarPartDataEvidence.Values.SelectMany(x => x).ToArray();
            var selectedVisualCellGameObjectName = (string)report["selectedVisualCellGameObjectName"];
            var characterAssemblyRole = InferNarakaCharacterAssemblyRole(selectedVisualCellGameObjectName);
            var characterAssemblyFamily = InferNarakaCharacterAssemblyFamily(selectedVisualCellGameObjectName);

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
                ["selectedVisualCellFile"] = report["selectedVisualCellFile"]?.DeepClone(),
                ["selectedVisualCellPathId"] = report["selectedVisualCellPathId"]?.DeepClone(),
                ["selectedVisualCellGameObjectPathId"] = report["selectedVisualCellGameObjectPathId"]?.DeepClone(),
                ["selectedVisualCellGameObjectName"] = report["selectedVisualCellGameObjectName"]?.DeepClone(),
                ["selectedVisualCellTransformPathId"] = report["selectedVisualCellTransformPathId"]?.DeepClone(),
                ["selectedVisualCellTransformPath"] = report["selectedVisualCellTransformPath"]?.DeepClone(),
                ["selectedVisualCellTransformPathStatus"] = report["selectedVisualCellTransformPathStatus"]?.DeepClone(),
                ["selectedVisualCellDirectChildCount"] = report["selectedVisualCellDirectChildCount"]?.DeepClone(),
                ["selectedVisualCellDirectChildNames"] = report["selectedVisualCellDirectChildNames"]?.DeepClone(),
                ["selectedVisualCellChildComponentStatus"] = report["selectedVisualCellChildComponentStatus"]?.DeepClone(),
                ["selectedVisualCellChildGameObjectCount"] = report["selectedVisualCellChildGameObjectCount"]?.DeepClone(),
                ["selectedVisualCellChildComponentCount"] = report["selectedVisualCellChildComponentCount"]?.DeepClone(),
                ["selectedVisualCellChildSkinnedMeshRendererCount"] = report["selectedVisualCellChildSkinnedMeshRendererCount"]?.DeepClone(),
                ["selectedVisualCellChildMonoBehaviourCount"] = report["selectedVisualCellChildMonoBehaviourCount"]?.DeepClone(),
                ["selectedVisualCellChildMonoBehaviourScriptNames"] = report["selectedVisualCellChildMonoBehaviourScriptNames"]?.DeepClone(),
                ["visualCellRendererAssistantCount"] = report["visualCellRendererAssistantCount"]?.DeepClone(),
                ["visualCellAvatarRendererCount"] = report["visualCellAvatarRendererCount"]?.DeepClone(),
                ["visualCellMeshRendererCount"] = report["visualCellMeshRendererCount"]?.DeepClone(),
                ["visualCellLodRendererAssistantCounts"] = report["visualCellLodRendererAssistantCounts"]?.DeepClone(),
                ["visualCellRendererListStatus"] = report["visualCellRendererListStatus"]?.DeepClone(),
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
                ["hairCustomizationTintEvidenceStatus"] = report["hairCustomizationTintEvidenceStatus"]?.DeepClone(),
                ["hairCustomizationTintEvidence"] = report["hairCustomizationTintEvidence"]?.DeepClone(),
                ["faceRuntimeEvidenceStatus"] = report["faceRuntimeEvidenceStatus"]?.DeepClone(),
                ["faceRuntimeAvatarBoneCount"] = report["faceRuntimeEvidence"]?["avatarFaceDataBoneCount"]?.DeepClone(),
                ["faceRuntimeMappedUsedBoneIndexCount"] = report["faceRuntimeEvidence"]?["mappedUsedBoneIndexCount"]?.DeepClone(),
                ["faceRuntimeEvidence"] = report["faceRuntimeEvidence"]?.DeepClone(),
                ["animationCount"] = 0,
                ["embeddedAnimationCount"] = 0,
                ["importedAnimationListCount"] = 0,
                ["morphCount"] = 0,
                ["boneCount"] = diagnosticSkin?.JointCount ?? 0,
                ["skinCount"] = diagnosticSkin != null ? 1 : 0,
                ["diagnosticSkinStatus"] = report["diagnosticSkinStatus"]?.DeepClone(),
                ["diagnosticSkin"] = report["diagnosticSkin"]?.DeepClone(),
                ["externalSkeletonDiagnosticSkin"] = report["externalSkeletonDiagnosticSkin"]?.DeepClone(),
                ["faceRuntimeDiagnosticSkin"] = report["faceRuntimeDiagnosticSkin"]?.DeepClone(),
                ["sourceSkinDataStatus"] = sourceSkinStatuses.Length == 1 ? sourceSkinStatuses[0] : string.Join(",", sourceSkinStatuses),
                ["sourceSkinBindPoseCount"] = parts.Sum(x => x.Mesh.Skin.BindPoseCount),
                ["animSkinBindPoseSlotStatus"] = SummarizeAnimSkinBindPoseSlotStatus(parts.Select(x => x.Mesh.Skin)),
                ["animSkinDirectBindPosePartCount"] = parts.Count(x => x.Mesh.Skin.AnimSkinIndicesFitBindPoseSlots),
                ["sourceSkinAvatarMaxBoneIndex"] = report["sourceSkinAvatarMaxBoneIndex"]?.DeepClone(),
                ["sourceSkinAvatarRequiredNodeCount"] = report["sourceSkinAvatarRequiredNodeCount"]?.DeepClone(),
                ["avatarBoneOffsetPairStatus"] = report["avatarBoneOffsetPairStatus"]?.DeepClone(),
                ["avatarBoneOffsetPairExactPartCount"] = report["avatarBoneOffsetPairExactPartCount"]?.DeepClone(),
                ["sourceSkinMappedJointCount"] = diagnosticSkin?.JointCount ?? 0,
                ["sourceSkinUnmapped"] = parts.Any(x => x.Mesh.Skin.Status == "presentUnmapped"),
                ["rendererSkinBindingStatus"] = SummarizeRendererSkinBindingStatus(rendererSkinBindings.Values),
                ["rendererSkinRendererRefCount"] = rendererSkinBindings.Values.Count(x => x.RendererObjectFound),
                ["rendererSkinRendererWithoutSkinRefCount"] = rendererSkinBindings.Values.Count(x => x.RendererObjectFound && !x.HasAnyRelation),
                ["rendererSkinRendererGameObjectRefCount"] = rendererSkinBindings.Values.Count(x => x.RendererGameObjectPathId.HasValue),
                ["rendererSkinRendererMaterialRefCount"] = rendererSkinBindings.Values.Sum(x => x.RendererMaterialRefCount),
                ["rendererSkinMeshRefCount"] = rendererSkinBindings.Values.Count(x => x.MeshPathId.HasValue),
                ["rendererSkinRootBoneRefCount"] = rendererSkinBindings.Values.Count(x => x.RootBonePathId.HasValue),
                ["rendererSkinBoneRefCount"] = rendererSkinBindings.Values.Sum(x => x.BoneCount ?? 0),
                ["rendererSkinJsonStatus"] = SummarizeRendererSkinJsonStatus(rendererSkinBindings.Values),
                ["rendererSkinJsonEmptyBoneRendererCount"] = rendererSkinBindings.Values.Count(x => string.Equals(x.RendererJsonStatus, "skinnedRendererJsonEmptyBones", StringComparison.OrdinalIgnoreCase)),
                ["avatarMeshExplicitReferenceStatus"] = report["avatarMeshExplicitReferenceStatus"]?.DeepClone(),
                ["avatarMeshRelationCount"] = report["avatarMeshRelationCount"]?.DeepClone(),
                ["avatarMeshNonScriptPPtrRefCount"] = report["avatarMeshNonScriptPPtrRefCount"]?.DeepClone(),
                ["avatarPartDataEvidenceStatus"] = SummarizeAvatarPartDataEvidenceStatus(avatarPartEvidenceItems),
                ["avatarPartDataEvidenceCount"] = avatarPartEvidenceItems.Length,
                ["avatarPartDataMeshDataIndexes"] = new JArray(avatarPartEvidenceItems.Select(x => x.MeshDataIndex).OrderBy(x => x)),
                ["headCollisionDataStatus"] = report["headCollisionDataStatus"]?.DeepClone(),
                ["headCollisionDataRefCount"] = report["headCollisionDataRefCount"]?.DeepClone(),
                ["headCollisionDataMissingRefCount"] = report["headCollisionDataMissingRefCount"]?.DeepClone(),
                ["headCollisionDataSharedTargetCount"] = report["headCollisionDataSharedTargetCount"]?.DeepClone(),
                ["headCollisionDataResolvedTargetCount"] = report["headCollisionDataResolvedTargetCount"]?.DeepClone(),
                ["boneDriverHintStatus"] = SummarizeBoneDriverHintStatus(boneDriverHints),
                ["boneDriverHintCount"] = boneDriverHints.Count,
                ["boneDriverHintNames"] = new JArray(GetBoneDriverNames(boneDriverHints)),
                ["boneDriverHintPaths"] = new JArray(GetBoneDriverPaths(boneDriverHints)),
                ["selectedVisualCellBoneDriverHintStatus"] = report["selectedVisualCellBoneDriverHintStatus"]?.DeepClone(),
                ["selectedVisualCellBoneDriverHintCount"] = report["selectedVisualCellBoneDriverHintCount"]?.DeepClone(),
                ["selectedVisualCellBoneDriverHintNames"] = report["selectedVisualCellBoneDriverHintNames"]?.DeepClone(),
                ["selectedVisualCellBoneDriverHintPaths"] = report["selectedVisualCellBoneDriverHintPaths"]?.DeepClone(),
                ["transformNodeTableCandidateStatus"] = transformNodeTableCandidateStatus,
                ["transformNodeTableCandidateCount"] = transformNodeTableCandidates.Length,
                ["transformNodeTableRangeCoveringCandidateCount"] = transformNodeTableCandidates.Count(x => x.CoversAvatarBoneIndexRange),
                ["transformNodeTableUsedBoneCoverageCandidateCount"] = usedBoneCoverageCandidateCount,
                ["transformNodeTableUsedBoneCoverageWithTrsCandidateCount"] = usedBoneCoverageWithTrsCandidateCount,
                ["selectedVisualCellUsedBoneCoverageCandidateCount"] = report["selectedVisualCellUsedBoneCoverageCandidateCount"]?.DeepClone(),
                ["selectedVisualCellUsedBoneCoverageWithTrsCandidateCount"] = report["selectedVisualCellUsedBoneCoverageWithTrsCandidateCount"]?.DeepClone(),
                ["selectedVisualCellTransformNodeCount"] = report["selectedVisualCellTransformNodeCount"]?.DeepClone(),
                ["selectedVisualCellTransformNodeTableStatus"] = report["selectedVisualCellTransformNodeTableStatus"]?.DeepClone(),
                ["selectedVisualCellTransformNodeTableCandidateCount"] = report["selectedVisualCellTransformNodeTableCandidateCount"]?.DeepClone(),
                ["selectedVisualCellTransformNodeRequiredCount"] = report["selectedVisualCellTransformNodeRequiredCount"]?.DeepClone(),
                ["selectedVisualCellTransformNodeMissingCount"] = report["selectedVisualCellTransformNodeMissingCount"]?.DeepClone(),
                ["selectedVisualCellTransformNodeCoverageStatus"] = report["selectedVisualCellTransformNodeCoverageStatus"]?.DeepClone(),
                ["sourceSkinMappingStatus"] = report["sourceSkinMappingStatus"]?.DeepClone(),
                ["externalSkeletonContextCandidateStatus"] = report["externalSkeletonContextCandidateStatus"]?.DeepClone(),
                ["externalSkeletonContextCandidate"] = report["externalSkeletonContextCandidate"]?.DeepClone(),
                ["externalSkeletonContextRule"] = report["externalSkeletonContextRule"]?.DeepClone(),
                ["transformNodeTableBoneDriverOverlapStatus"] = SummarizeTransformNodeTableBoneDriverOverlapStatus(transformNodeTableCandidates),
                ["transformNodeTableBoneDriverOverlapCandidateCount"] = transformNodeTableCandidates.Count(x => x.BoneDriverNodeNameMatchCount > 0),
                ["transformNodeJsonStatus"] = SummarizeExportedTransformNodeStatus(exportedTransformNodes),
                ["transformNodeJsonCount"] = exportedTransformNodes.Count,
                ["transformNodeJsonReadableTrsCount"] = exportedTransformNodes.Values.Count(x => x.HasLocalTrs),
                ["transformNodeTableCandidateVisualCells"] = new JArray(GetTransformNodeTableCandidateVisualCells(transformNodeTableCandidates)),
                ["transformNodeTableRangeCoveringVisualCells"] = new JArray(GetTransformNodeTableRangeCoveringVisualCells(transformNodeTableCandidates)),
                ["transformNodeTableRangeCoveringMappedNodeNames"] = report["transformNodeTableRangeCoveringMappedNodeNames"]?.DeepClone(),
                ["transformNodeTableRangeCoveringEvidenceCount"] = report["transformNodeTableRangeCoveringEvidenceCount"]?.DeepClone(),
                ["hairDeformDataStatus"] = hairDeformDataStatus,
                ["hairDeformDataPartCount"] = hairDeformSummaries.Count(x => x.Count > 0),
                ["hairDeformDataVertexCount"] = hairDeformSummaries.Sum(x => x.Count),
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
                    ["avatarBoneOffsetPairBasis"] = "AvatarMeshDataAsset.m_AvatarBoneOffsetCount as [weightOffset, influenceCount] pairs",
                    ["rendererSkinBasis"] = "SkinnedMeshRenderer object / component.gameObject / renderer.material / skinnedMeshRenderer.mesh/rootBone/bones from unity_source_index.db",
                    ["avatarPartDataBasis"] = "AvatarPartDataAsset.m_MeshData",
                    ["headCollisionDataBasis"] = "LXRendererAssistant.headCollisionData PPtr",
                    ["faceRuntimeBasis"] = "AvatarFaceRuntime.m_AvatarFaceData / AvatarFaceData.m_AvatarBones",
                    ["faceRuntimeEvidenceStatus"] = report["faceRuntimeEvidenceStatus"]?.DeepClone(),
                    ["diagnosticSkinStatus"] = report["diagnosticSkinStatus"]?.DeepClone(),
                    ["boneDriverHintBasis"] = "BoneFollowDriver/BoneHairFollowDriver serializeName fields",
                    ["selectedBoneDriverHintBasis"] = "BoneDriver TransformPath scoped to selected ActorBodyVisualCell root",
                    ["rendererListBasis"] = "ActorBodyVisualCell.rendererAssistants/avatarRenderers/meshRenderer/lod*RendererAssistants",
                    ["transformNodeTableCandidateBasis"] = "ActorBodyVisualCell.transformNodes.data index-order candidates from unity_source_index.db",
                    ["selectedTransformHierarchyBasis"] = "GameObject.component -> Transform / Transform.child / Transform.parent from unity_source_index.db",
                    ["selectedChildComponentBasis"] = "selected VisualCell Transform child -> GameObject.component -> Component/MonoBehaviour.script from unity_source_index.db",
                    ["selectedTransformNodeTableStatus"] = report["selectedVisualCellTransformNodeTableStatus"]?.DeepClone(),
                    ["selectedTransformNodeCoverageStatus"] = report["selectedVisualCellTransformNodeCoverageStatus"]?.DeepClone(),
                    ["sourceSkinMappingStatus"] = report["sourceSkinMappingStatus"]?.DeepClone(),
                    ["externalSkeletonContextCandidateStatus"] = report["externalSkeletonContextCandidateStatus"]?.DeepClone(),
                    ["hairDeformDataBasis"] = "AvatarMeshDataAsset.m_HairDeformData packed half4 diagnostic values",
                    ["rule"] = "该记录只证明 Naraka 自定义网格、材质引用、部件顺序、骨骼名称线索、目标节点表状态和源 skin 字段可追溯；BoneDriver 会同时区分全目录线索和选中 VisualCell 作用域，Renderer/AvatarPartDataAsset/BoneDriver/同包 transformNodes 候选目前都没有提供 mesh joint 映射，shader tint 和完整角色装配前不进入动画验收。"
                }
            };
            if (!string.Equals(characterAssemblyRole, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                catalogEntry["characterAssemblyRole"] = characterAssemblyRole;
                catalogEntry["characterAssemblyFamily"] = characterAssemblyFamily;
                catalogEntry["characterAssemblyCandidateOnly"] = true;
                catalogEntry["characterAssemblyBasis"] = "selectedVisualCellGameObjectName";
                catalogEntry["characterAssemblyRule"] = "该角色模块标注只服务索引、浏览和人工装配预览；当前资源仍是 CustomMeshDiagnostic/diagnosticOnly，不能进入默认自动推荐、正式装配或动画 smoke。";
            }

            var catalogPath = Path.Combine(outputFolder, "asset_catalog.jsonl");
            RewriteJsonLineByOutput(catalogPath, relativeOutput, catalogEntry);
            WriteCustomMeshTextureCatalogEntries(catalogPath, outputFolder, relativeOutput, gltfImages);
            WriteOrUpdateCustomMeshValidation(outputFolder, gltfPath, relativeOutput, validationReasons);
            WriteCustomMeshReadmes(outputFolder, relativeOutput, catalogEntry, report, gltfMaterials, validationReasons);
        }

        private static void WriteCustomMeshReadmes(
            string outputFolder,
            string relativeOutput,
            JObject catalogEntry,
            JObject report,
            JArray gltfMaterials,
            JArray validationReasons)
        {
            // Naraka 自定义网格仍是诊断资产；这里写人读入口，避免使用者只看到 glTF 就误判为生产可用。
            WriteCustomMeshAssetReadme(outputFolder, relativeOutput, catalogEntry, report, validationReasons);
            WriteCustomMeshMaterialReport(outputFolder, relativeOutput, catalogEntry, gltfMaterials);
        }

        private static void WriteCustomMeshAssetReadme(
            string outputFolder,
            string relativeOutput,
            JObject catalogEntry,
            JObject report,
            JArray validationReasons)
        {
            var path = Path.Combine(outputFolder, "ASSET_README.md");
            var sb = new StringBuilder();
            sb.AppendLine("# Naraka 自定义网格诊断资产");
            sb.AppendLine();
            sb.AppendLine("这份文件是给人看的入口。当前资源来自 Naraka `ActorBodyVisualCell -> LXRendererAssistant -> AvatarMeshDataAsset` 确定性链路，但仍是诊断模型，不能直接作为动画生产验收样本。");
            sb.AppendLine();
            sb.AppendLine("## 输出");
            sb.AppendLine();
            AppendKeyValue(sb, "模型", relativeOutput);
            AppendKeyValue(sb, "资源名", (string)catalogEntry["selectedVisualCellGameObjectName"] ?? (string)catalogEntry["name"]);
            AppendKeyValue(sb, "资源类型", (string)catalogEntry["resourceKind"]);
            AppendKeyValue(sb, "模型状态", (string)catalogEntry["modelValidationStatus"]);
            AppendKeyValue(sb, "材质状态", (string)catalogEntry["materialStatus"]);
            AppendKeyValue(sb, "诊断资产", ((bool?)catalogEntry["diagnosticOnly"] ?? true) ? "true" : "false");
            AppendKeyValue(sb, "装配角色", ToText(catalogEntry["characterAssemblyRole"]));
            AppendKeyValue(sb, "装配 family", ToText(catalogEntry["characterAssemblyFamily"]));
            sb.AppendLine();
            sb.AppendLine("## 模型统计");
            sb.AppendLine();
            AppendKeyValue(sb, "mesh", ToText(catalogEntry["meshCount"]));
            AppendKeyValue(sb, "vertex", ToText(catalogEntry["vertexCount"]));
            AppendKeyValue(sb, "triangle", ToText(catalogEntry["triangleCount"]));
            AppendKeyValue(sb, "material", ToText(catalogEntry["materialCount"]));
            AppendKeyValue(sb, "texture", ToText(catalogEntry["textureCount"]));
            AppendKeyValue(sb, "skin", ToText(catalogEntry["skinCount"]));
            AppendKeyValue(sb, "bone", ToText(catalogEntry["boneCount"]));
            sb.AppendLine();
            sb.AppendLine("## Unity 证据");
            sb.AppendLine();
            AppendKeyValue(sb, "选中 VisualCell", $"{ToText(report["selectedVisualCellFile"])}#{ToText(report["selectedVisualCellPathId"])}");
            AppendKeyValue(sb, "GameObject", $"{ToText(report["selectedVisualCellGameObjectName"])}#{ToText(report["selectedVisualCellGameObjectPathId"])}");
            AppendKeyValue(sb, "Transform 路径", $"{ToText(report["selectedVisualCellTransformPath"])} ({ToText(report["selectedVisualCellTransformPathStatus"])})");
            AppendKeyValue(sb, "Renderer 列表", ToText(report["visualCellRendererListStatus"]));
            AppendKeyValue(sb, "Renderer 材质引用", ToText(report["rendererMaterialRefCount"]));
            AppendKeyValue(sb, "材质贴图引用", ToText(report["materialTextureRefCount"]));
            AppendKeyValue(sb, "发型 tint 证据", ToText(report["hairCustomizationTintEvidenceStatus"]));
            AppendKeyValue(sb, "脸部运行时骨骼表", ToText(report["faceRuntimeEvidenceStatus"]));
            sb.AppendLine();
            sb.AppendLine("## 动画门禁");
            sb.AppendLine();
            sb.AppendLine("当前禁止进入动画导出、合成或 production smoke。原因：");
            foreach (var reason in validationReasons?.Values<string>() ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(reason))
                {
                    sb.AppendLine("- `" + reason + "`");
                }
            }
            sb.AppendLine();
            sb.AppendLine("## 相关报告");
            sb.AppendLine();
            sb.AppendLine("- `MATERIAL_REPORT.md`：材质、贴图槽和 tint 说明。");
            sb.AppendLine("- `model_validation.json`：模型静态结构和动画门禁。");
            sb.AppendLine("- `" + Path.GetFileNameWithoutExtension(relativeOutput) + ".avatar_mesh_data_report.json`：Naraka 自定义网格完整诊断证据。");
            File.WriteAllText(path, sb.ToString());
        }

        private static void WriteCustomMeshMaterialReport(
            string outputFolder,
            string relativeOutput,
            JObject catalogEntry,
            JArray gltfMaterials)
        {
            var path = Path.Combine(outputFolder, "MATERIAL_REPORT.md");
            var sb = new StringBuilder();
            sb.AppendLine("# Naraka 自定义网格材质报告");
            sb.AppendLine();
            sb.AppendLine("本报告只解释 glTF 预览材质和 Unity 材质槽证据，不复刻 Naraka 自定义 shader。找不到确定 tint 配置时保持 `needsCustomizationTint`，不硬猜颜色。");
            sb.AppendLine();
            AppendKeyValue(sb, "模型", relativeOutput);
            AppendKeyValue(sb, "材质状态", (string)catalogEntry["materialStatus"]);
            AppendKeyValue(sb, "需要 tint/customization", ToText(catalogEntry["materialNeedsCustomizationTint"]));
            AppendKeyValue(sb, "有 baseColorTexture", ToText(catalogEntry["materialHasBaseColorTexture"]));
            AppendKeyValue(sb, "有 normalTexture", ToText(catalogEntry["materialHasNormalTexture"]));
            AppendKeyValue(sb, "glTF image 数", ToText(catalogEntry["materialImageCount"]));
            AppendKeyValue(sb, "发型 tint 证据", ToText(catalogEntry["hairCustomizationTintEvidenceStatus"]));
            var tintEvidence = catalogEntry["hairCustomizationTintEvidence"] as JObject;
            if (tintEvidence != null)
            {
                AppendKeyValue(sb, "tint 脚本实例数", ToText(tintEvidence["totalScriptInstanceCount"]));
                AppendKeyValue(sb, "和选中模型直接 PPtr", ToText(tintEvidence["directLinkCount"]));
                AppendKeyValue(sb, "完整颜色字段数", ToText(tintEvidence["typeTreeColorDataCount"]));
                AppendKeyValue(sb, "特殊分区配置数", ToText(tintEvidence["typeTreePartDataCount"]));
            }
            sb.AppendLine();
            sb.AppendLine("## glTF 材质");
            sb.AppendLine();
            foreach (var material in gltfMaterials?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var name = (string)material["name"] ?? "(unnamed)";
                var anime = material["extras"]?["animeStudioMaterial"] as JObject;
                var textureSlots = anime?["textureSlots"] as JArray;
                sb.AppendLine("### " + name);
                sb.AppendLine();
                AppendKeyValue(sb, "baseColorTexture", material["pbrMetallicRoughness"]?["baseColorTexture"] != null ? "true" : "false");
                AppendKeyValue(sb, "normalTexture", material["normalTexture"] != null ? "true" : "false");
                AppendKeyValue(sb, "needsCustomizationTint", ToText(anime?["needsCustomizationTint"]));
                AppendKeyValue(sb, "说明", ToText(anime?["note"]));
                if (textureSlots != null && textureSlots.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("| Unity 槽 | 贴图 | 用途 | URI |");
                    sb.AppendLine("| --- | --- | --- | --- |");
                    foreach (var slot in textureSlots.OfType<JObject>())
                    {
                        sb.AppendLine("| " +
                            MdCell(ToText(slot["slot"])) + " | " +
                            MdCell(FirstNonEmpty(ToText(slot["resolvedTextureName"]), ToText(slot["textureName"]))) + " | " +
                            MdCell(ToText(slot["previewUsage"])) + " | " +
                            MdCell(ToText(slot["uri"])) + " |");
                    }
                }
                sb.AppendLine();
            }

            sb.AppendLine("## 规则");
            sb.AppendLine();
            sb.AppendLine("- `baseColorTexture` 只来自安全的通用颜色槽。");
            sb.AppendLine("- `_Color` 接近白色且没有安全 base color 贴图时，预览使用中性灰，同时保留 `needsCustomizationTint=true`。");
            sb.AppendLine("- ID、mask、SH、dir、LUT 等自定义 shader 数据只保留引用，不猜最终颜色。");
            File.WriteAllText(path, sb.ToString());
        }

        private static void AppendKeyValue(StringBuilder sb, string key, string value)
        {
            sb.AppendLine("- " + key + ": " + (string.IsNullOrWhiteSpace(value) ? "未知" : value));
        }

        private static string ToText(JToken token)
        {
            return token == null || token.Type == JTokenType.Null
                ? string.Empty
                : token.Type == JTokenType.String
                    ? token.Value<string>() ?? string.Empty
                    : token.ToString(Formatting.None);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
        }

        private static string InferNarakaCharacterAssemblyRole(string name)
        {
            name ??= string.Empty;
            if (Regex.IsMatch(name, "(^|[_-])(face|head)([_-]|$)", RegexOptions.IgnoreCase))
            {
                return "Face";
            }
            if (Regex.IsMatch(name, "(^|[_-])hair([_-]|$)", RegexOptions.IgnoreCase))
            {
                return "Hair";
            }
            if (Regex.IsMatch(name, "accessori|accessory|mask|choker|cape|cloth", RegexOptions.IgnoreCase))
            {
                return "Accessory";
            }
            if (Regex.IsMatch(name, "(^|[_-])(body|skin)([_-]|$)", RegexOptions.IgnoreCase))
            {
                return "Body";
            }
            return "Unknown";
        }

        private static string InferNarakaCharacterAssemblyFamily(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var parts = name.Split(new[] { '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3
                && string.Equals(parts[0], "ch", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(parts[1], "m", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(parts[1], "f", StringComparison.OrdinalIgnoreCase)))
            {
                // Naraka 角色模块常用 ch_性别_角色 的稳定前缀；这只是索引 family，不是装配关系。
                return string.Join("_", parts.Take(3));
            }

            return parts.Length == 0 ? string.Empty : parts[0];
        }

        private static string MdCell(string value)
        {
            return (value ?? string.Empty).Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        }

        private static void WriteCustomMeshTextureCatalogEntries(string catalogPath, string outputFolder, string modelOutput, JArray gltfImages)
        {
            var textureRoot = Path.Combine(outputFolder, "Texture2D");
            if (!Directory.Exists(textureRoot))
            {
                return;
            }

            var previewImageUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var image in gltfImages.OfType<JObject>())
            {
                var uri = NormalizeRelativePath((string)image["uri"]);
                if (!string.IsNullOrWhiteSpace(uri))
                {
                    previewImageUris.Add(uri);
                }
            }

            foreach (var texturePath in Directory.EnumerateFiles(textureRoot, "*.png", SearchOption.AllDirectories))
            {
                var relativeOutput = ToLibraryRelativePath(outputFolder, texturePath);
                var file = new FileInfo(texturePath);
                var isPreviewImage = previewImageUris.Contains(NormalizeRelativePath(relativeOutput));
                var entry = new JObject
                {
                    ["kind"] = "Texture",
                    ["libraryRole"] = isPreviewImage ? "ModelPreviewTexture" : "MaterialDependency",
                    ["resourceKind"] = "Texture2D",
                    ["exportedAt"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    ["name"] = Path.GetFileNameWithoutExtension(texturePath),
                    ["sourceType"] = "NarakaMaterialTexture",
                    ["source"] = modelOutput,
                    ["pathId"] = 0,
                    ["output"] = relativeOutput,
                    ["format"] = "Png",
                    ["modelOutput"] = modelOutput,
                    ["usedByGltfPreview"] = isPreviewImage,
                    ["sizeBytes"] = file.Exists ? file.Length : 0,
                    ["rule"] = "只把本次 Naraka 自定义网格诊断流程真实导出的 Texture2D PNG 写入 catalog；是否用于 glTF 标准预览由 glTF image URI 决定，不按名字猜材质用途。"
                };
                RewriteJsonLineByOutput(catalogPath, relativeOutput, entry);
            }
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
            var hasExternalDiagnosticSkin = (validationReasons ?? new JArray())
                .Values<string>()
                .Any(x => string.Equals(x, "external_skeleton_diagnostic_skin_present", StringComparison.OrdinalIgnoreCase));
            var hasFaceRuntimeDiagnosticSkin = (validationReasons ?? new JArray())
                .Values<string>()
                .Any(x => string.Equals(x, "face_runtime_diagnostic_skin_present", StringComparison.OrdinalIgnoreCase));
            validation["Notes"] = MergeStringArray(
                validation["Notes"] as JArray,
                hasExternalDiagnosticSkin
                    ? "Naraka ActorBodyVisualCell 自定义网格仍是诊断模型：外部骨架 skin 只在显式诊断开关下写出，rest inverse bind matrix 和视觉效果尚未通过生产验收。"
                    : hasFaceRuntimeDiagnosticSkin
                        ? "Naraka ActorBodyVisualCell 自定义网格仍是诊断模型：脸部运行时 skin 只在显式诊断开关下写出，AvatarFaceData bind local TRS 空间和视觉效果尚未通过生产验收。"
                    : "Naraka ActorBodyVisualCell 自定义网格仍是诊断模型：源 skin 字段尚未映射到 glTF joints，hair tint/customization shader 未完全复原，禁止进入动画生产验收。");

            var body = validation["Body"] as JObject;
            if (body != null)
            {
                body["ModelBodyStatus"] = "warning";
                body["Evidence"] = MergeStringArray(
                    body["Evidence"] as JArray,
                    hasExternalDiagnosticSkin
                        ? "显式诊断开关已写入外部骨架 skin；joint 来源和权重可追溯，inverse bind matrix 来自外部骨架 rest world TRS 求逆，材质仍未烘焙 Naraka customization tint。"
                        : hasFaceRuntimeDiagnosticSkin
                            ? "显式诊断开关已写入脸部运行时 skin；joint 来源和权重可追溯，inverse bind matrix 来自 AvatarFaceData bind local TRS 层级，材质仍未烘焙 Naraka customization tint。"
                        : "自定义网格源 JSON 可包含 skin 字段，但当前没有确定性 joint 映射，所以没有写 glTF skin；材质只做保守预览，未烘焙 Naraka customization tint。");
            }

            validation["animationGate"] = BuildBlockedCustomMeshAnimationGate(validation, validationReasons);

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

        private static JObject BuildBlockedCustomMeshAnimationGate(JObject validation, JArray validationReasons)
        {
            var customReasons = (validationReasons ?? new JArray())
                .Values<string>()
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();
            var hasExternalDiagnosticSkin = customReasons.Any(x => string.Equals(x, "external_skeleton_diagnostic_skin_present", StringComparison.OrdinalIgnoreCase));
            var hasFaceRuntimeDiagnosticSkin = customReasons.Any(x => string.Equals(x, "face_runtime_diagnostic_skin_present", StringComparison.OrdinalIgnoreCase));
            var reasons = MergeStringArray(
                validation?["animationGate"]?["reasons"] as JArray,
                "model_validation_not_ok",
                "model_body_not_ok",
                "diagnostic_custom_mesh");
            reasons = hasExternalDiagnosticSkin
                ? MergeStringArray(reasons, "external_skeleton_diagnostic_skin_present", "external_skeleton_rest_inverse_bind_matrices_diagnostic")
                : hasFaceRuntimeDiagnosticSkin
                    ? MergeStringArray(reasons, "face_runtime_diagnostic_skin_present", "face_runtime_bind_local_inverse_matrices_diagnostic")
                    : MergeStringArray(reasons, "missing_skin_binding", "source_skin_unmapped");
            foreach (var reason in customReasons.Where(x =>
                string.Equals(x, "needs_customization_tint", StringComparison.OrdinalIgnoreCase)
                || string.Equals(x, "skin_data_present_unmapped", StringComparison.OrdinalIgnoreCase)
                || string.Equals(x, "selected_visual_cell_transform_nodes_insufficient", StringComparison.OrdinalIgnoreCase)
                || string.Equals(x, "selected_visual_cell_transform_nodes_missing", StringComparison.OrdinalIgnoreCase)))
            {
                reasons = MergeStringArray(reasons, reason);
            }

            return new JObject
            {
                ["ready"] = false,
                ["status"] = "blocked",
                ["reasons"] = reasons,
                ["rule"] = hasExternalDiagnosticSkin
                    ? "Naraka ActorBodyVisualCell 外部骨架 skin 仍是显式诊断输出；rest inverse bind matrix、材质 tint 和清晰视觉验收通过前，禁止进入动画导出、合成或生产 smoke。"
                    : hasFaceRuntimeDiagnosticSkin
                        ? "Naraka ActorBodyVisualCell 脸部运行时 skin 仍是显式诊断输出；AvatarFaceData bind pose 空间、完整角色父骨、材质 tint 和清晰视觉验收通过前，禁止进入动画导出、合成或生产 smoke。"
                    : "Naraka ActorBodyVisualCell 自定义网格仍是诊断模型；缺少确定性 glTF skin/joint 映射或 customization tint 时，禁止进入动画导出、合成或生产 smoke。"
            };
        }

        private static JArray MergeStringArray(JArray existing, params string[] values)
        {
            var result = existing != null
                ? new JArray(existing.Values<string>().Where(x => !string.IsNullOrWhiteSpace(x)))
                : new JArray();
            foreach (var value in values ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }
                if (!result.Values<string>().Any(x => string.Equals(x, value, StringComparison.Ordinal)))
                {
                    result.Add(value);
                }
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

        private static JObject WriteMeshObject(Stream stream, JArray bufferViews, JArray accessors, AvatarMeshData mesh, string source, int materialIndex = 0, INarakaDiagnosticSkin diagnosticSkin = null)
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

            // AvatarMeshDataAsset 里的顶点是 Unity 坐标，glTF 需要 Y-up。
            // 目录/LOD0 模式也走这里，避免单件和组装件坐标不一致。
            var gltfPositions = mesh.Positions.Select(ConvertUnityPositionToGltf).ToArray();
            var (gltfMin, gltfMax) = CalculateBounds(gltfPositions);
            var positionView = WriteFloatBufferView(stream, bufferViews, gltfPositions.SelectMany(ToFloatArray3), 34962);
            accessors.Add(new JObject
            {
                ["bufferView"] = positionView,
                ["componentType"] = 5126,
                ["count"] = mesh.Positions.Count,
                ["type"] = "VEC3",
                ["min"] = new JArray(gltfMin.X, gltfMin.Y, gltfMin.Z),
                ["max"] = new JArray(gltfMax.X, gltfMax.Y, gltfMax.Z)
            });
            attributes["POSITION"] = accessors.Count - 1;

            if (mesh.Normals.Count == mesh.Positions.Count)
            {
                var normalView = WriteFloatBufferView(stream, bufferViews, mesh.Normals.Select(ConvertUnityDirectionToGltf).SelectMany(ToFloatArray3), 34962);
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
                var tangentView = WriteFloatBufferView(stream, bufferViews, mesh.Tangents.Select(ConvertUnityTangentToGltf).SelectMany(ToFloatArray4), 34962);
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

            if (diagnosticSkin != null && TryBuildDiagnosticSkinAttributes(mesh, diagnosticSkin, out var jointValues, out var weightValues))
            {
                var jointsView = WriteUShortBufferView(stream, bufferViews, jointValues, 34962);
                accessors.Add(new JObject
                {
                    ["bufferView"] = jointsView,
                    ["componentType"] = 5123,
                    ["count"] = mesh.Positions.Count,
                    ["type"] = "VEC4"
                });
                attributes["JOINTS_0"] = accessors.Count - 1;

                var weightsView = WriteFloatBufferView(stream, bufferViews, weightValues, 34962);
                accessors.Add(new JObject
                {
                    ["bufferView"] = weightsView,
                    ["componentType"] = 5126,
                    ["count"] = mesh.Positions.Count,
                    ["type"] = "VEC4"
                });
                attributes["WEIGHTS_0"] = accessors.Count - 1;
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
                    ["unityCoordinateSystemPreserved"] = false,
                    ["axisConversion"] = "Unity(x,y,z) -> glTF(x,z,-y)",
                    ["note"] = "Naraka 自定义 MonoBehaviour 网格诊断导出；尚未绑定 prefab 模块关系、材质或骨骼。"
                }
            };
        }

        private static ExternalSkeletonDiagnosticSkin BuildExternalSkeletonDiagnosticSkin(
            JArray nodes,
            TransformNodeTableCandidate candidate,
            IReadOnlyDictionary<long, ExportedTransformNode> exportedTransformNodes,
            bool enabled,
            List<string> warnings)
        {
            if (!enabled)
            {
                return null;
            }

            if (candidate == null
                || !candidate.CoversAllUsedBoneIndices
                || !candidate.AllCoveredUsedBoneTransformsExported)
            {
                warnings.Add("externalSkeletonDiagnosticSkinSkipped:candidateMissingOrIncomplete");
                return null;
            }

            var exported = exportedTransformNodes ?? new Dictionary<long, ExportedTransformNode>();
            var usedRefs = (candidate.CoveredUsedBoneRefs ?? Array.Empty<TransformNodeTableUsedBoneRef>())
                .Where(x => x.BoneIndex >= 0 && x.ExportedTransform?.HasLocalTrs == true)
                .GroupBy(x => x.BoneIndex)
                .Select(x => x.OrderByDescending(y => y.WeightedRefCount).First())
                .OrderBy(x => x.BoneIndex)
                .ToArray();
            if (usedRefs.Length == 0)
            {
                warnings.Add("externalSkeletonDiagnosticSkinSkipped:noReadableUsedBoneRefs");
                return null;
            }

            var pathIds = new HashSet<long>(usedRefs.Select(x => x.TransformPathId));
            foreach (var usedRef in usedRefs)
            {
                var current = usedRef.TransformPathId;
                for (var guard = 0; guard < 128; guard++)
                {
                    if (!exported.TryGetValue(current, out var transform)
                        || transform.FatherPathId == 0
                        || pathIds.Contains(transform.FatherPathId))
                    {
                        break;
                    }

                    pathIds.Add(transform.FatherPathId);
                    current = transform.FatherPathId;
                }
            }

            var nameByPath = usedRefs
                .GroupBy(x => x.TransformPathId)
                .ToDictionary(x => x.Key, x => x.First().GameObjectName, EqualityComparer<long>.Default);
            var nodeIndexByPath = new Dictionary<long, int>();
            foreach (var pathId in pathIds.OrderBy(x => x))
            {
                if (!exported.TryGetValue(pathId, out var transform) || !transform.HasLocalTrs)
                {
                    continue;
                }

                var node = new JObject
                {
                    ["name"] = !string.IsNullOrWhiteSpace(nameByPath.TryGetValue(pathId, out var name) ? name : null)
                        ? name
                        : "Transform_" + pathId.ToString(CultureInfo.InvariantCulture),
                    ["translation"] = new JArray(ToFloatArray3(ConvertUnityPositionToGltf(transform.LocalPosition.Value))),
                    ["rotation"] = new JArray(ToFloatArray4(ConvertUnityRotationToGltf(transform.LocalRotation.Value))),
                    ["scale"] = new JArray(ToFloatArray3(transform.LocalScale.Value)),
                    ["extras"] = new JObject
                    {
                        ["narakaExternalSkeletonDiagnostic"] = true,
                        ["sourceTransformPathId"] = pathId,
                        ["sourceGameObjectPathId"] = transform.GameObjectPathId,
                        ["sourceJson"] = string.IsNullOrWhiteSpace(transform.JsonPath) ? null : Path.GetFullPath(transform.JsonPath)
                    }
                };
                nodes.Add(node);
                nodeIndexByPath[pathId] = nodes.Count - 1;
            }

            foreach (var pair in nodeIndexByPath.ToArray())
            {
                if (!exported.TryGetValue(pair.Key, out var transform)
                    || !nodeIndexByPath.TryGetValue(transform.FatherPathId, out var parentNodeIndex))
                {
                    continue;
                }

                var parent = (JObject)nodes[parentNodeIndex];
                var children = parent["children"] as JArray;
                if (children == null)
                {
                    children = new JArray();
                    parent["children"] = children;
                }
                children.Add(pair.Value);
            }

            var jointNodeIndices = new List<int>();
            var jointPathIds = new List<long>();
            var boneIndexToJointSlot = new Dictionary<int, int>();
            foreach (var usedRef in usedRefs)
            {
                if (!nodeIndexByPath.TryGetValue(usedRef.TransformPathId, out var nodeIndex))
                {
                    continue;
                }

                boneIndexToJointSlot[usedRef.BoneIndex] = jointNodeIndices.Count;
                jointNodeIndices.Add(nodeIndex);
                jointPathIds.Add(usedRef.TransformPathId);
            }

            if (jointNodeIndices.Count == 0)
            {
                warnings.Add("externalSkeletonDiagnosticSkinSkipped:noJointNodes");
                return null;
            }

            var sourceRootNodeIndices = nodeIndexByPath
                .Where(x => !exported.TryGetValue(x.Key, out var transform) || !nodeIndexByPath.ContainsKey(transform.FatherPathId))
                .Select(x => x.Value)
                .Distinct()
                .OrderBy(x => x)
                .ToArray();
            var syntheticRoot = new JObject
            {
                ["name"] = "NarakaExternalSkeletonDiagnosticRoot",
                ["children"] = new JArray(sourceRootNodeIndices),
                ["extras"] = new JObject
                {
                    ["narakaExternalSkeletonDiagnostic"] = true,
                    ["syntheticRoot"] = true,
                    ["rule"] = "glTF skin 需要 joint 共享根；该节点只把外部 VisualCell transformNodes 的多个根合并为诊断骨架根，不代表 Unity 原对象。"
                }
            };
            nodes.Add(syntheticRoot);
            var syntheticRootIndex = nodes.Count - 1;
            var inverseBindMatrices = BuildInverseBindMatrices(jointPathIds, pathIds, exported);

            return new ExternalSkeletonDiagnosticSkin(
                candidate,
                jointNodeIndices.ToArray(),
                new[] { syntheticRootIndex },
                boneIndexToJointSlot,
                inverseBindMatrices,
                nodeIndexByPath.Count + 1,
                pathIds.Count - jointNodeIndices.Count);
        }

        private static FaceRuntimeDiagnosticSkin BuildFaceRuntimeDiagnosticSkin(
            JArray nodes,
            FaceRuntimeEvidence evidence,
            bool enabled,
            List<string> warnings)
        {
            if (!enabled)
            {
                return null;
            }
            if (evidence == null
                || !string.Equals(evidence.Status, "avatarFaceDataMatchesSourceSkinBoneTable", StringComparison.OrdinalIgnoreCase)
                || evidence.AvatarFaceDataBoneSamples.Count == 0)
            {
                warnings.Add("faceRuntimeDiagnosticSkinSkipped:evidenceMissingOrUnmatched");
                return null;
            }

            var bones = evidence.AvatarFaceDataBoneSamples
                .OrderBy(x => x.BoneIndex)
                .ToArray();
            if (bones.Any(x => x.BoneIndex < 0 || x.BoneIndex >= bones.Length))
            {
                warnings.Add("faceRuntimeDiagnosticSkinSkipped:boneIndexRangeInvalid");
                return null;
            }
            if (bones.Any(x => !x.HasLocalTrs))
            {
                warnings.Add("faceRuntimeDiagnosticSkinSkipped:bindLocalTrsMissing");
                return null;
            }

            var childSet = new HashSet<int>();
            var nodeIndexByBoneIndex = new Dictionary<int, int>();
            foreach (var bone in bones)
            {
                var t = ConvertUnityPositionToGltf(bone.BindLocalPosition.Value);
                var r = ConvertUnityRotationToGltf(bone.BindLocalRotation.Value);
                var s = bone.BindLocalScale.Value;
                var node = new JObject
                {
                    ["name"] = string.IsNullOrWhiteSpace(bone.Name)
                        ? "FaceBone_" + bone.BoneIndex.ToString(CultureInfo.InvariantCulture)
                        : bone.Name,
                    ["translation"] = new JArray(ToFloatArray3(t)),
                    ["rotation"] = new JArray(ToFloatArray4(r)),
                    ["scale"] = new JArray(ToFloatArray3(s)),
                    ["extras"] = new JObject
                    {
                        ["narakaFaceRuntimeDiagnostic"] = true,
                        ["boneIndex"] = bone.BoneIndex,
                        ["boneSide"] = bone.BoneSide,
                        ["avatarFaceDataPathId"] = evidence.AvatarFaceDataPathId,
                    }
                };
                nodes.Add(node);
                nodeIndexByBoneIndex[bone.BoneIndex] = nodes.Count - 1;
            }

            foreach (var bone in bones)
            {
                if (!nodeIndexByBoneIndex.TryGetValue(bone.BoneIndex, out var parentNodeIndex))
                {
                    continue;
                }

                var children = new JArray();
                foreach (var childIndex in bone.ChildBoneIndices ?? Array.Empty<int>())
                {
                    if (!nodeIndexByBoneIndex.TryGetValue(childIndex, out var childNodeIndex))
                    {
                        warnings.Add($"faceRuntimeDiagnosticSkinSkippedChildOutOfRange:{bone.BoneIndex}->{childIndex}");
                        continue;
                    }
                    children.Add(childNodeIndex);
                    childSet.Add(childIndex);
                }
                if (children.Count > 0)
                {
                    ((JObject)nodes[parentNodeIndex])["children"] = children;
                }
            }

            var rootNodeIndices = bones
                .Where(x => !childSet.Contains(x.BoneIndex))
                .Select(x => nodeIndexByBoneIndex[x.BoneIndex])
                .ToArray();
            if (rootNodeIndices.Length == 0)
            {
                warnings.Add("faceRuntimeDiagnosticSkinSkipped:noRootBones");
                return null;
            }

            var syntheticRoot = new JObject
            {
                ["name"] = "NarakaFaceRuntimeDiagnosticRoot",
                ["children"] = new JArray(rootNodeIndices),
                ["extras"] = new JObject
                {
                    ["narakaFaceRuntimeDiagnostic"] = true,
                    ["syntheticRoot"] = true,
                    ["avatarFaceDataPathId"] = evidence.AvatarFaceDataPathId,
                    ["rule"] = "AvatarFaceData 只给脸部局部骨骼表；该节点把多个脸部根合并成 glTF 诊断 skin 根，不代表完整角色骨架。"
                }
            };
            nodes.Add(syntheticRoot);
            var syntheticRootIndex = nodes.Count - 1;

            var jointNodeIndices = bones.Select(x => nodeIndexByBoneIndex[x.BoneIndex]).ToArray();
            var boneIndexToJointSlot = bones.ToDictionary(x => x.BoneIndex, x => x.BoneIndex);
            var inverseBindMatrices = BuildFaceRuntimeInverseBindMatrices(bones);
            return new FaceRuntimeDiagnosticSkin(
                evidence,
                jointNodeIndices,
                new[] { syntheticRootIndex },
                boneIndexToJointSlot,
                inverseBindMatrices,
                rootNodeIndices.Length);
        }

        private static IReadOnlyList<float[]> BuildFaceRuntimeInverseBindMatrices(IReadOnlyList<FaceRuntimeBoneSample> bones)
        {
            var boneByIndex = (bones ?? Array.Empty<FaceRuntimeBoneSample>())
                .ToDictionary(x => x.BoneIndex, x => x);
            var parentByChild = new Dictionary<int, int>();
            foreach (var bone in bones ?? Array.Empty<FaceRuntimeBoneSample>())
            {
                foreach (var child in bone.ChildBoneIndices ?? Array.Empty<int>())
                {
                    if (boneByIndex.ContainsKey(child) && !parentByChild.ContainsKey(child))
                    {
                        parentByChild[child] = bone.BoneIndex;
                    }
                }
            }

            var worldCache = new Dictionary<int, System.Numerics.Matrix4x4>();
            System.Numerics.Matrix4x4 GetWorld(int boneIndex)
            {
                if (worldCache.TryGetValue(boneIndex, out var cached))
                {
                    return cached;
                }
                if (!boneByIndex.TryGetValue(boneIndex, out var bone) || !bone.HasLocalTrs)
                {
                    return System.Numerics.Matrix4x4.Identity;
                }

                var local = ToMatrix4x4(bone);
                var world = parentByChild.TryGetValue(boneIndex, out var parentIndex)
                    ? local * GetWorld(parentIndex)
                    : local;
                worldCache[boneIndex] = world;
                return world;
            }

            return (bones ?? Array.Empty<FaceRuntimeBoneSample>())
                .OrderBy(x => x.BoneIndex)
                .Select(x =>
                {
                    var world = GetWorld(x.BoneIndex);
                    if (!System.Numerics.Matrix4x4.Invert(world, out var inverse))
                    {
                        inverse = System.Numerics.Matrix4x4.Identity;
                    }
                    return ToFloatArray(inverse).ToArray();
                })
                .ToArray();
        }

        private static IReadOnlyList<float[]> BuildInverseBindMatrices(
            IReadOnlyList<long> jointPathIds,
            IReadOnlyCollection<long> includedPathIds,
            IReadOnlyDictionary<long, ExportedTransformNode> exportedTransformNodes)
        {
            var included = new HashSet<long>(includedPathIds ?? Array.Empty<long>());
            var worldCache = new Dictionary<long, System.Numerics.Matrix4x4>();
            System.Numerics.Matrix4x4 GetWorld(long pathId)
            {
                if (worldCache.TryGetValue(pathId, out var cached))
                {
                    return cached;
                }

                if (exportedTransformNodes == null
                    || !exportedTransformNodes.TryGetValue(pathId, out var transform)
                    || !transform.HasLocalTrs)
                {
                    return System.Numerics.Matrix4x4.Identity;
                }

                var local = ToMatrix4x4(transform);
                var world = transform.FatherPathId != 0 && included.Contains(transform.FatherPathId)
                    ? local * GetWorld(transform.FatherPathId)
                    : local;
                worldCache[pathId] = world;
                return world;
            }

            return (jointPathIds ?? Array.Empty<long>())
                .Select(pathId =>
                {
                    var world = GetWorld(pathId);
                    if (!System.Numerics.Matrix4x4.Invert(world, out var inverse))
                    {
                        inverse = System.Numerics.Matrix4x4.Identity;
                    }
                    return ToFloatArray(inverse).ToArray();
                })
                .ToArray();
        }

        private static System.Numerics.Matrix4x4 ToMatrix4x4(ExportedTransformNode transform)
        {
            var t = ConvertUnityPositionToGltf(transform.LocalPosition.Value);
            var r = ConvertUnityRotationToGltf(transform.LocalRotation.Value);
            var s = transform.LocalScale.Value;
            var scale = System.Numerics.Matrix4x4.CreateScale(s.X, s.Y, s.Z);
            var rotation = System.Numerics.Matrix4x4.CreateFromQuaternion(new System.Numerics.Quaternion(r.X, r.Y, r.Z, r.W));
            var translation = System.Numerics.Matrix4x4.CreateTranslation(t.X, t.Y, t.Z);
            return scale * rotation * translation;
        }

        private static System.Numerics.Matrix4x4 ToMatrix4x4(FaceRuntimeBoneSample bone)
        {
            var t = ConvertUnityPositionToGltf(bone.BindLocalPosition.Value);
            var r = ConvertUnityRotationToGltf(bone.BindLocalRotation.Value);
            var s = bone.BindLocalScale.Value;
            var scale = System.Numerics.Matrix4x4.CreateScale(s.X, s.Y, s.Z);
            var rotation = System.Numerics.Matrix4x4.CreateFromQuaternion(new System.Numerics.Quaternion(r.X, r.Y, r.Z, r.W));
            var translation = System.Numerics.Matrix4x4.CreateTranslation(t.X, t.Y, t.Z);
            return scale * rotation * translation;
        }

        private static IEnumerable<float> ToFloatArray(System.Numerics.Matrix4x4 matrix)
        {
            yield return CleanMatrixFloat(matrix.M11); yield return CleanMatrixFloat(matrix.M12); yield return CleanMatrixFloat(matrix.M13); yield return CleanMatrixFloat(matrix.M14);
            yield return CleanMatrixFloat(matrix.M21); yield return CleanMatrixFloat(matrix.M22); yield return CleanMatrixFloat(matrix.M23); yield return CleanMatrixFloat(matrix.M24);
            yield return CleanMatrixFloat(matrix.M31); yield return CleanMatrixFloat(matrix.M32); yield return CleanMatrixFloat(matrix.M33); yield return CleanMatrixFloat(matrix.M34);
            yield return CleanMatrixFloat(matrix.M41); yield return CleanMatrixFloat(matrix.M42); yield return CleanMatrixFloat(matrix.M43); yield return CleanMatrixFloat(matrix.M44);
        }

        private static float CleanMatrixFloat(float value)
        {
            if (Math.Abs(value) < 0.0000001f)
            {
                return 0f;
            }
            if (Math.Abs(value - 1f) < 0.000001f)
            {
                return 1f;
            }
            if (Math.Abs(value + 1f) < 0.000001f)
            {
                return -1f;
            }
            return value;
        }

        private static bool TryBuildDiagnosticSkinAttributes(
            AvatarMeshData mesh,
            INarakaDiagnosticSkin diagnosticSkin,
            out ushort[] jointValues,
            out float[] weightValues)
        {
            jointValues = null;
            weightValues = null;
            if (mesh?.Skin?.VertexAvatarInfluences == null
                || mesh.Skin.VertexAvatarInfluences.Count != mesh.Positions.Count
                || diagnosticSkin?.BoneIndexToJointSlot == null)
            {
                return false;
            }

            jointValues = new ushort[mesh.Positions.Count * 4];
            weightValues = new float[mesh.Positions.Count * 4];
            for (var vertexIndex = 0; vertexIndex < mesh.Positions.Count; vertexIndex++)
            {
                var picked = mesh.Skin.VertexAvatarInfluences[vertexIndex]
                    .Where(x => x.Weight > 0f && diagnosticSkin.BoneIndexToJointSlot.ContainsKey(x.BoneIndex))
                    .OrderByDescending(x => x.Weight)
                    .ThenBy(x => x.BoneIndex)
                    .Take(4)
                    .ToArray();
                var weightSum = picked.Sum(x => Math.Max(0f, x.Weight));
                if (picked.Length == 0 || weightSum <= 0f)
                {
                    weightValues[vertexIndex * 4] = 1f;
                    continue;
                }

                for (var i = 0; i < picked.Length; i++)
                {
                    var dst = vertexIndex * 4 + i;
                    jointValues[dst] = checked((ushort)diagnosticSkin.BoneIndexToJointSlot[picked[i].BoneIndex]);
                    weightValues[dst] = Math.Max(0f, picked[i].Weight) / weightSum;
                }
            }

            return true;
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

        private static string ResolveVisualCellPreviewMaterialSource(string jsonFolder, string outputFolder)
        {
            // Naraka 计划脚本通常把 TypeTree JSON 放在 TypeTreeDump/MonoBehaviour，
            // 材质和贴图放在计划根目录。复测单独输出时要回到这个已导出的根目录取证据。
            foreach (var candidate in EnumeratePreviewMaterialSourceCandidates(jsonFolder, outputFolder))
            {
                if (Directory.Exists(candidate)
                    && File.Exists(Path.Combine(candidate, "export_manifest.jsonl"))
                    && Directory.Exists(Path.Combine(candidate, "Material"))
                    && Directory.Exists(Path.Combine(candidate, "Texture2D")))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            return null;
        }

        private static IEnumerable<string> EnumeratePreviewMaterialSourceCandidates(string jsonFolder, string outputFolder)
        {
            if (!string.IsNullOrWhiteSpace(outputFolder))
            {
                yield return Path.GetFullPath(outputFolder);
            }

            if (string.IsNullOrWhiteSpace(jsonFolder))
            {
                yield break;
            }

            var current = new DirectoryInfo(Path.GetFullPath(jsonFolder));
            while (current != null)
            {
                yield return current.FullName;
                current = current.Parent;
            }
        }

        private static JArray BuildVisualCellPreviewMaterials(
            string materialSourceFolder,
            string gltfFolder,
            IEnumerable<VisualCellMaterialBinding> bindings,
            JArray images,
            JArray textures,
            Dictionary<string, int> materialIndexByKey,
            List<string> warnings)
        {
            var bindingList = bindings.ToList();
            var materials = new JArray();

            var exportedAssets = string.IsNullOrWhiteSpace(materialSourceFolder)
                ? new Dictionary<long, ExportedAssetInfo>()
                : LoadExportedAssetManifest(materialSourceFolder);
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
                        gltfFolder,
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
            PreviewAlphaDecision alphaDecision = null;

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
                            texturePath = EnsurePreviewTextureInGltfFolder(texturePath, gltfFolder, pathId, warnings);
                            var relativeTexturePath = Path.GetRelativePath(gltfFolder, texturePath).Replace('\\', '/');
                            var effectiveTextureName = GetPreviewTextureName(textureName, textureAsset.Name, texturePath);
                            slot["uri"] = relativeTexturePath;
                            if (!string.Equals(textureName, effectiveTextureName, StringComparison.Ordinal))
                            {
                                slot["resolvedTextureName"] = effectiveTextureName;
                            }

                            if (!usedBaseColor && IsSafePreviewBaseColorSlot(property.Name, effectiveTextureName))
                            {
                                var textureIndex = AddGltfTexture(images, textures, effectiveTextureName, relativeTexturePath);
                                pbr["baseColorTexture"] = new JObject { ["index"] = textureIndex };
                                slot["gltfTextureIndex"] = textureIndex;
                                slot["previewUsage"] = "baseColorTexture";
                                usedBaseColor = true;
                                if (TryBuildPreviewAlphaDecision(texturePath, materialJson, warnings, out var decision))
                                {
                                    alphaDecision = decision;
                                }
                            }
                            else if (normalTexture == null && IsNormalSlot(property.Name, effectiveTextureName))
                            {
                                var textureIndex = AddGltfTexture(images, textures, effectiveTextureName, relativeTexturePath);
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
            ProtectCustomizationTintPreviewBaseColor(result, color, usedBaseColor);
            if (normalTexture != null)
            {
                result["normalTexture"] = normalTexture;
            }
            if (alphaDecision != null)
            {
                result["alphaMode"] = alphaDecision.AlphaMode;
                if (alphaDecision.AlphaCutoff.HasValue)
                {
                    result["alphaCutoff"] = alphaDecision.AlphaCutoff.Value;
                }

                ((JObject)result["extras"]["animeStudioMaterial"])["previewAlpha"] = alphaDecision.Extras;
            }
            return result;
        }

        private static string EnsurePreviewTextureInGltfFolder(string texturePath, string gltfFolder, long pathId, List<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(texturePath)
                || string.IsNullOrWhiteSpace(gltfFolder)
                || !File.Exists(texturePath))
            {
                return texturePath;
            }

            var fullTexturePath = Path.GetFullPath(texturePath);
            var fullGltfFolder = Path.GetFullPath(gltfFolder);
            if (IsPathInsideFolder(fullTexturePath, fullGltfFolder))
            {
                return fullTexturePath;
            }

            try
            {
                // 诊断 glTF 可以复用计划根目录的材质证据，但真正引用的贴图要复制到本次输出，
                // 这样报告、SQLite 和手动预览都能跟着输出目录一起移动。
                var textureFolder = Path.Combine(fullGltfFolder, "Texture2D");
                Directory.CreateDirectory(textureFolder);

                var fileName = Path.GetFileName(fullTexturePath);
                var targetPath = Path.Combine(textureFolder, fileName);
                if (File.Exists(targetPath) && new FileInfo(targetPath).Length != new FileInfo(fullTexturePath).Length)
                {
                    var stem = Path.GetFileNameWithoutExtension(fileName);
                    var ext = Path.GetExtension(fileName);
                    var safePathId = pathId.ToString(CultureInfo.InvariantCulture).Replace("-", "m", StringComparison.Ordinal);
                    targetPath = Path.Combine(textureFolder, $"{stem}_{safePathId}{ext}");
                }

                File.Copy(fullTexturePath, targetPath, overwrite: true);
                return targetPath;
            }
            catch (Exception ex)
            {
                warnings.Add($"copyPreviewTextureFailed:{pathId}:{ex.GetType().Name}");
                return fullTexturePath;
            }
        }

        private static bool IsPathInsideFolder(string filePath, string folderPath)
        {
            var relativePath = Path.GetRelativePath(folderPath, filePath);
            return relativePath == "."
                || (!relativePath.StartsWith("..", StringComparison.Ordinal)
                    && !Path.IsPathRooted(relativePath));
        }

        private static void ProtectCustomizationTintPreviewBaseColor(JObject material, JToken unityColor, bool hasBaseColorTexture)
        {
            if (hasBaseColorTexture)
            {
                return;
            }

            var pbr = material["pbrMetallicRoughness"] as JObject;
            var anime = material["extras"]?["animeStudioMaterial"] as JObject;
            if (pbr == null || anime == null)
            {
                return;
            }

            var factor = ReadColorFactor(unityColor);
            if (!IsNearlyWhite(factor))
            {
                return;
            }

            // Naraka 发型常把真实颜色放在 customization / shader 参数里，_Color=白色不是最终发色。
            // 这里只保护诊断预览可读性，不把任何私有 shader slot 猜成最终颜色。
            anime["originalBaseColorFactor"] = factor;
            anime["previewBaseColorFactorProtected"] = true;
            anime["previewBaseColorFactorReason"] = "Unity _Color is nearly white and no safe base color texture was found; the Naraka custom mesh preview uses neutral gray while preserving the original value and needsCustomizationTint=true.";
            pbr["baseColorFactor"] = new JArray(0.55, 0.55, 0.55, 1.0);
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

        private static string GetPreviewTextureName(string materialTextureName, string exportedTextureName, string texturePath)
        {
            // Naraka 的部分材质 PPtr 只有 PathID，m_Texture.Name 为空。
            // 这里仍然只沿同一个已导出的 Texture2D 取名字，不按目录或角色名猜贴图用途。
            if (!string.IsNullOrWhiteSpace(materialTextureName))
            {
                return materialTextureName;
            }
            if (!string.IsNullOrWhiteSpace(exportedTextureName))
            {
                return exportedTextureName;
            }
            return Path.GetFileNameWithoutExtension(texturePath) ?? string.Empty;
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

        private static bool IsNearlyWhite(JArray factor)
        {
            if (factor == null || factor.Count < 4)
            {
                return false;
            }

            return factor[0]?.Value<float>() >= 0.97f
                && factor[1]?.Value<float>() >= 0.97f
                && factor[2]?.Value<float>() >= 0.97f
                && factor[3]?.Value<float>() > 0.5f;
        }

        private static bool TryBuildPreviewAlphaDecision(
            string texturePath,
            JObject materialJson,
            List<string> warnings,
            out PreviewAlphaDecision decision)
        {
            decision = null;
            if (!TryReadMeaningfulTextureAlpha(texturePath, warnings, out var alphaStats))
            {
                return false;
            }

            var cutoff = ReadMaterialFloat(materialJson, "_Cutoff", 0f);
            var alphaClip = ReadMaterialFloat(materialJson, "_AlphaClip", 0f);
            var alphaTest = ReadMaterialFloat(materialJson, "_AlphaTest", 0f);
            if (cutoff > 0.001f || alphaClip > 0.5f || alphaTest > 0.5f)
            {
                var alphaCutoff = cutoff > 0.001f ? Math.Clamp(cutoff, 0.01f, 0.99f) : 0.5f;
                decision = new PreviewAlphaDecision(
                    "MASK",
                    alphaCutoff,
                    BuildPreviewAlphaExtras(texturePath, alphaStats, "baseColorTextureAlphaAndUnityCutoff", alphaCutoff));
                return true;
            }

            var colorAlpha = ReadMaterialColorAlpha(materialJson, "_Color", 1f);
            var surface = ReadMaterialFloat(materialJson, "_Surface", 0f);
            if (colorAlpha < 0.999f || surface > 0.5f)
            {
                decision = new PreviewAlphaDecision(
                    "BLEND",
                    null,
                    BuildPreviewAlphaExtras(texturePath, alphaStats, "baseColorTextureAlphaAndUnityTransparentState", null));
                return true;
            }

            return false;
        }

        private static bool TryReadMeaningfulTextureAlpha(
            string texturePath,
            List<string> warnings,
            out TextureAlphaStats stats)
        {
            stats = null;
            if (string.IsNullOrWhiteSpace(texturePath) || !File.Exists(texturePath))
            {
                return false;
            }

            try
            {
                using var image = Image.Load<Rgba32>(texturePath);
                long transparentPixels = 0;
                long partialPixels = 0;
                long opaquePixels = 0;
                byte minAlpha = byte.MaxValue;
                byte maxAlpha = byte.MinValue;

                image.ProcessPixelRows(accessor =>
                {
                    for (var y = 0; y < accessor.Height; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (var x = 0; x < row.Length; x++)
                        {
                            var alpha = row[x].A;
                            minAlpha = Math.Min(minAlpha, alpha);
                            maxAlpha = Math.Max(maxAlpha, alpha);
                            if (alpha <= 5)
                            {
                                transparentPixels++;
                            }
                            else if (alpha >= 254)
                            {
                                opaquePixels++;
                            }
                            else
                            {
                                partialPixels++;
                            }
                        }
                    }
                });

                var totalPixels = (long)image.Width * image.Height;
                stats = new TextureAlphaStats(
                    image.Width,
                    image.Height,
                    totalPixels,
                    transparentPixels,
                    partialPixels,
                    opaquePixels,
                    minAlpha,
                    maxAlpha);

                // 很多 Unity 贴图导成 PNG 后会出现 254/255 这种“近似不透明”alpha。
                // 只有存在成片透明区时，才把它当成可影响预览材质的 alpha。
                return totalPixels > 0
                    && minAlpha <= 5
                    && transparentPixels >= Math.Max(16, totalPixels / 100);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is InvalidImageContentException)
            {
                warnings?.Add($"previewAlphaReadFailed:{Path.GetFileName(texturePath)}:{ex.GetType().Name}");
                return false;
            }
        }

        private static JObject BuildPreviewAlphaExtras(
            string texturePath,
            TextureAlphaStats stats,
            string basis,
            float? alphaCutoff)
        {
            var result = new JObject
            {
                ["basis"] = basis,
                ["texture"] = Path.GetFileName(texturePath),
                ["width"] = stats.Width,
                ["height"] = stats.Height,
                ["minAlpha"] = stats.MinAlpha,
                ["maxAlpha"] = stats.MaxAlpha,
                ["transparentPixels"] = stats.TransparentPixels,
                ["partialPixels"] = stats.PartialPixels,
                ["opaquePixels"] = stats.OpaquePixels,
                ["transparentRatio"] = stats.TotalPixels > 0 ? (double)stats.TransparentPixels / stats.TotalPixels : 0.0,
                ["note"] = "只在 baseColor 贴图有真实透明区，并且 Unity 材质有裁剪/透明信号时，才写 glTF alphaMode。"
            };
            if (alphaCutoff.HasValue)
            {
                result["alphaCutoff"] = alphaCutoff.Value;
            }
            return result;
        }

        private static float ReadMaterialFloat(JObject materialJson, string name, float fallback)
        {
            var floats = materialJson?["m_SavedProperties"]?["m_Floats"] as JObject;
            if (floats == null)
            {
                return fallback;
            }

            foreach (var property in floats.Properties())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value.Value<float>();
                }
            }

            return fallback;
        }

        private static float ReadMaterialColorAlpha(JObject materialJson, string name, float fallback)
        {
            var colors = materialJson?["m_SavedProperties"]?["m_Colors"] as JObject;
            if (colors == null)
            {
                return fallback;
            }

            foreach (var property in colors.Properties())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value?["a"]?.Value<float>() ?? fallback;
                }
            }

            return fallback;
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
                || LooksLikeNormalTextureName(textureText)
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
            return IsStandardPreviewNormalSlot(slotText) && LooksLikeNormalTextureName(textureText);
        }

        private static bool IsStandardPreviewNormalSlot(string slotText)
        {
            // 只把“整材质”的通用法线槽写进 glTF normalTexture。
            // 眉毛/皱纹/贴花/bent normal 这类局部 shader 输入会保留在 extras，不能套到整张脸上。
            return slotText is "_bumpmap" or "_normalmap" or "_normaltex";
        }

        private static bool LooksLikeNormalTextureName(string textureText)
        {
            if (string.IsNullOrWhiteSpace(textureText))
            {
                return false;
            }

            // 只能把明确的 normal 语义或结尾 token 当法线。
            // 不能用 Contains("_n")，Naraka 的 female_face01_nielian_d 会被误伤。
            var text = textureText.ToLowerInvariant();
            return text.Contains("normal")
                || text.EndsWith("_n")
                || text.EndsWith("_nx")
                || text.EndsWith("_normal")
                || text.EndsWith("_norm");
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

        private static Dictionary<long, ExportedTransformNode> LoadExportedTransformNodes(string jsonFolder, List<string> warnings)
        {
            var result = new Dictionary<long, ExportedTransformNode>();
            var dumpRoot = Path.GetDirectoryName(Path.GetFullPath(jsonFolder));
            if (string.IsNullOrWhiteSpace(dumpRoot) || !Directory.Exists(dumpRoot))
            {
                return result;
            }

            foreach (var pair in LoadExportedAssetManifest(dumpRoot)
                         .Where(x => string.Equals(x.Value.Type, "Transform", StringComparison.OrdinalIgnoreCase)))
            {
                var jsonPath = FindExportedFile(pair.Value, ".json");
                if (string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath))
                {
                    warnings?.Add($"missingTransformJson:{pair.Key}");
                    continue;
                }

                try
                {
                    var json = JObject.Parse(File.ReadAllText(jsonPath));
                    result[pair.Key] = new ExportedTransformNode
                    {
                        PathId = pair.Key,
                        JsonPath = jsonPath,
                        LocalPosition = ReadOptionalVector3(json["m_LocalPosition"]),
                        LocalRotation = ReadOptionalVector4(json["m_LocalRotation"]),
                        LocalScale = ReadOptionalVector3(json["m_LocalScale"]),
                        FatherPathId = ReadPPtrPathId(json["m_Father"]),
                        GameObjectPathId = ReadPPtrPathId(json["m_GameObject"]),
                        ChildCount = (json["m_Children"] as JArray)?.Count ?? 0,
                    };
                }
                catch (Exception ex) when (ex is IOException || ex is JsonException || ex is InvalidDataException)
                {
                    warnings?.Add($"transformJsonReadFailed:{pair.Key}:{ex.Message}");
                }
            }

            return result;
        }

        private static string SummarizeExportedTransformNodeStatus(IReadOnlyDictionary<long, ExportedTransformNode> nodes)
        {
            if (nodes == null || nodes.Count == 0)
            {
                return "missing";
            }

            return nodes.Values.All(x => x.HasLocalTrs)
                ? "readableLocalTrs"
                : "partialReadableLocalTrs";
        }

        private static string FindExportedFile(ExportedAssetInfo asset, string extension)
        {
            if (asset == null || string.IsNullOrWhiteSpace(asset.OutputFolder))
            {
                return null;
            }

            var fileName = string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase)
                ? ToExportedJsonFileName(asset.Name)
                : FixFileName(asset.Name) + extension;
            var direct = Path.Combine(asset.OutputFolder, fileName);
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
                ["hairDeformData"] = mesh.HairDeform.ToJson(),
                ["warnings"] = new JArray(mesh.Warnings)
            };
        }

        private static string SummarizeRendererSkinBindingStatus(IEnumerable<VisualCellRendererSkinBinding> bindings)
        {
            var list = bindings?.ToList() ?? new List<VisualCellRendererSkinBinding>();
            if (list.Count == 0)
            {
                return "unknown";
            }
            if (list.All(x => x.Status == "rendererSkinRelations"))
            {
                return "rendererSkinRelations";
            }
            if (list.Any(x => x.Status == "rendererSkinRelations"))
            {
                return "partialRendererSkinRelations";
            }
            if (list.All(x => x.Status == "rendererPresentWithoutSkinRelations"))
            {
                return "rendererPresentWithoutSkinRelations";
            }
            if (list.Any(x => x.Status == "rendererPresentWithoutSkinRelations"))
            {
                return "partialRendererPresentWithoutSkinRelations";
            }
            if (list.Any(x => x.Status == "sourceIndexNotProvided" || x.Status == "sourceIndexMissing" || x.Status == "sourceIndexQueryFailed"))
            {
                return "sourceIndexUnavailable";
            }
            return "noRendererSkinRelations";
        }

        private static string SummarizeRendererSkinJsonStatus(IEnumerable<VisualCellRendererSkinBinding> bindings)
        {
            var statuses = (bindings ?? Enumerable.Empty<VisualCellRendererSkinBinding>())
                .Select(x => x.RendererJsonStatus)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (statuses.Length == 0)
            {
                return "missing";
            }

            return statuses.Length == 1
                ? statuses[0]
                : "mixed:" + string.Join(",", statuses);
        }

        private static string SummarizeAvatarMeshRelationStatus(IEnumerable<AvatarMeshDataRelationEvidence> evidences)
        {
            var list = evidences?.ToList() ?? new List<AvatarMeshDataRelationEvidence>();
            if (list.Count == 0)
            {
                return "unknown";
            }
            if (list.All(x => x.Status == "scriptOnlyNoExplicitPPtr"))
            {
                return "scriptOnlyNoExplicitPPtr";
            }
            if (list.Any(x => x.NonScriptPPtrCount > 0))
            {
                return "hasExplicitPPtrRelations";
            }
            if (list.Any(x => x.Status == "sourceIndexNotProvided" || x.Status == "sourceIndexMissing" || x.Status == "sourceIndexQueryFailed"))
            {
                return "sourceIndexUnavailable";
            }
            if (list.Any(x => x.RelationCount > 0))
            {
                return "nonPPtrRelationsOnly";
            }
            return "noSourceRelations";
        }

        private static string SummarizeAvatarPartDataEvidenceStatus(IEnumerable<AvatarPartDataEvidence> evidences)
        {
            var list = evidences?.ToList() ?? new List<AvatarPartDataEvidence>();
            if (list.Count == 0)
            {
                return "missing";
            }
            if (list.All(x => x.Status == "avatarPartDataMeshOrder"))
            {
                return "avatarPartDataMeshOrder";
            }
            if (list.Any(x => x.Status == "avatarPartDataMeshOrder"))
            {
                return "partialAvatarPartDataMeshOrder";
            }
            if (list.Any(x => x.Status == "avatarPartDataMeshOrderScriptUnverified"))
            {
                return "scriptUnverified";
            }
            return "unknown";
        }

        private static string SummarizeHeadCollisionDataStatus(IEnumerable<AssistantHeadCollisionEvidence> evidences)
        {
            var list = evidences?.ToArray() ?? Array.Empty<AssistantHeadCollisionEvidence>();
            if (list.Length == 0 || list.All(x => !x.HasReference))
            {
                return "missing";
            }

            if (list.Any(x => x.TargetObjectFound))
            {
                return "targetObjectResolved";
            }

            return CountDistinctHeadCollisionTargets(list) == 1
                ? "sharedExternalPPtrUnresolved"
                : "externalPPtrUnresolved";
        }

        private static int CountDistinctHeadCollisionTargets(IEnumerable<AssistantHeadCollisionEvidence> evidences)
        {
            return (evidences ?? Array.Empty<AssistantHeadCollisionEvidence>())
                .Where(x => x.HasReference)
                .Select(x => $"{NormalizeSerializedFileName(x.TargetFile)}:{x.TargetPathId}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        }

        private static string SummarizeBoneDriverHintStatus(IEnumerable<BoneDriverHint> hints)
        {
            return hints != null && hints.Any()
                ? "boneNameHintsOnly"
                : "missing";
        }

        private static IEnumerable<string> GetBoneDriverNames(IEnumerable<BoneDriverHint> hints)
        {
            return (hints ?? Array.Empty<BoneDriverHint>())
                .SelectMany(x => new[]
                {
                    x.DriverSerializeName,
                    x.AimSerializeName,
                    x.Aim1SerializeName,
                    x.Aim2SerializeName,
                })
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> GetBoneDriverPaths(IEnumerable<BoneDriverHint> hints)
        {
            return (hints ?? Array.Empty<BoneDriverHint>())
                .Select(x => x.TransformPath)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<BoneDriverHint> GetSelectedVisualCellBoneDriverHints(
            IEnumerable<BoneDriverHint> hints,
            SelectedVisualCellInfo selectedVisualCell)
        {
            var rootPath = NormalizeTransformPath(selectedVisualCell?.TransformPath);
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                rootPath = NormalizeTransformPath(selectedVisualCell?.GameObjectName);
            }

            if (string.IsNullOrWhiteSpace(rootPath))
            {
                return Array.Empty<BoneDriverHint>();
            }

            // BoneDriver 扫描来自同一诊断目录，必须按选中的 VisualCell 根路径再收窄一次。
            return (hints ?? Array.Empty<BoneDriverHint>())
                .Where(x =>
                {
                    var path = NormalizeTransformPath(x.TransformPath);
                    return string.Equals(path, rootPath, StringComparison.OrdinalIgnoreCase)
                        || path.StartsWith(rootPath + "/", StringComparison.OrdinalIgnoreCase);
                });
        }

        private static string SummarizeSelectedVisualCellBoneDriverHintStatus(
            IEnumerable<BoneDriverHint> hints,
            SelectedVisualCellInfo selectedVisualCell)
        {
            var rootPath = NormalizeTransformPath(selectedVisualCell?.TransformPath);
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                rootPath = NormalizeTransformPath(selectedVisualCell?.GameObjectName);
            }

            if (string.IsNullOrWhiteSpace(rootPath))
            {
                return "unknownSelectedVisualCellPath";
            }

            return hints != null && hints.Any()
                ? "selectedVisualCellBoneNameHintsOnly"
                : "selectedVisualCellHasNoBoneDriverHints";
        }

        private static string NormalizeTransformPath(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/').Trim('/');
        }

        private static HashSet<string> BuildBoneDriverNodeNameSet(IEnumerable<BoneDriverHint> hints)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var hint in hints ?? Array.Empty<BoneDriverHint>())
            {
                if (!string.IsNullOrWhiteSpace(hint.GameObjectName))
                {
                    result.Add(hint.GameObjectName.Trim());
                }

                var path = (hint.TransformPath ?? string.Empty).Replace('\\', '/').Trim('/');
                var lastSlash = path.LastIndexOf('/');
                var leaf = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
                if (!string.IsNullOrWhiteSpace(leaf))
                {
                    result.Add(leaf.Trim());
                }
            }

            return result;
        }

        private static IEnumerable<TransformNodeTableCandidate> GetDistinctTransformNodeTableCandidates(
            IEnumerable<VisualCellPart> parts)
        {
            return (parts ?? Array.Empty<VisualCellPart>())
                .SelectMany(x => x.Mesh?.Skin?.TransformNodeTableCandidates ?? Enumerable.Empty<TransformNodeTableCandidate>())
                .GroupBy(
                    x => $"{NormalizeSerializedFileName(x.SerializedFile)}:{x.VisualCellPathId}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(x => x
                    .OrderByDescending(candidate => candidate.MatchedTopBoneIndexCount)
                    .ThenByDescending(candidate => candidate.TransformNodeCount)
                    .ThenBy(candidate => candidate.VisualCellGameObjectName, StringComparer.OrdinalIgnoreCase)
                    .First())
                .OrderByDescending(x => x.MatchedTopBoneIndexCount)
                .ThenByDescending(x => x.TransformNodeCount)
                .ThenBy(x => x.VisualCellGameObjectName, StringComparer.OrdinalIgnoreCase);
        }

        private static string SummarizeTransformNodeTableCandidateStatus(
            IEnumerable<TransformNodeTableCandidate> candidates)
        {
            var count = candidates?.Count() ?? 0;
            if (count == 0)
            {
                return "missing";
            }

            // Naraka 同一个 SerializedFile 里可能同时有身体、头发、披风等多套节点表。
            // 只有一套时也只能当顺序候选，多套时必须明确标成歧义，避免误写 skin joint。
            return count == 1
                ? "indexOrderCandidateOnly"
                : "indexOrderCandidatesAmbiguous";
        }

        private static string SummarizeTransformNodeTableBoneDriverOverlapStatus(
            IEnumerable<TransformNodeTableCandidate> candidates)
        {
            var list = candidates?.ToArray() ?? Array.Empty<TransformNodeTableCandidate>();
            if (list.Length == 0)
            {
                return "missingTransformNodeCandidates";
            }

            var matched = list.Count(x => x.BoneDriverNodeNameMatchCount > 0);
            if (matched == 0)
            {
                return "missingBoneDriverNodeOverlap";
            }

            return matched == 1
                ? "singleBoneDriverNodeOverlapCandidate"
                : "multipleBoneDriverNodeOverlapCandidates";
        }

        private static TransformNodeTableCandidate SelectExternalSkeletonContextCandidate(
            IEnumerable<TransformNodeTableCandidate> candidates)
        {
            // 只选非当前 VisualCell 的节点表作为“完整角色/身体上下文”线索。
            // 它不授权写 skin，只方便后续装配阶段定位最可能的骨架来源。
            return (candidates ?? Array.Empty<TransformNodeTableCandidate>())
                .Where(x => !x.IsSelectedVisualCell)
                .OrderByDescending(x => x.CoversAllUsedBoneIndices && x.AllCoveredUsedBoneTransformsExported)
                .ThenByDescending(x => x.CoversAvatarBoneIndexRange)
                .ThenByDescending(x => x.BoneDriverNodeNameMatchCount)
                .ThenByDescending(x => x.MatchedTopBoneIndexCount)
                .ThenByDescending(x => x.TransformNodeCount)
                .ThenBy(x => x.VisualCellGameObjectName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        private static string SummarizeExternalSkeletonContextCandidateStatus(TransformNodeTableCandidate candidate)
        {
            if (candidate == null)
            {
                return "missingExternalSkeletonContextCandidate";
            }

            if (candidate.CoversAllUsedBoneIndices && candidate.AllCoveredUsedBoneTransformsExported)
            {
                return "externalSkeletonContextCoversUsedBonesWithTrs";
            }

            if (candidate.CoversAllUsedBoneIndices)
            {
                return "externalSkeletonContextCoversUsedBonesMissingTrs";
            }

            if (candidate.CoversAvatarBoneIndexRange)
            {
                return "externalSkeletonContextCoversAvatarRangeOnly";
            }

            return "externalSkeletonContextDiagnosticOnly";
        }

        private static JObject BuildExternalSkeletonContextCandidateJson(TransformNodeTableCandidate candidate)
        {
            if (candidate == null)
            {
                return null;
            }

            return new JObject
            {
                ["status"] = SummarizeExternalSkeletonContextCandidateStatus(candidate),
                ["serializedFile"] = candidate.SerializedFile,
                ["visualCellPathId"] = candidate.VisualCellPathId,
                ["visualCellGameObjectName"] = candidate.VisualCellGameObjectName,
                ["container"] = candidate.Container,
                ["transformNodeCount"] = candidate.TransformNodeCount,
                ["requiredNodeCount"] = candidate.RequiredNodeCount,
                ["usedBoneIndexCount"] = candidate.UsedBoneIndexCount,
                ["coveredUsedBoneIndexCount"] = candidate.CoveredUsedBoneIndexCount,
                ["missingUsedBoneIndexCount"] = candidate.MissingUsedBoneIndexCount,
                ["coversAllUsedBoneIndices"] = candidate.CoversAllUsedBoneIndices,
                ["allCoveredUsedBoneTransformsExported"] = candidate.AllCoveredUsedBoneTransformsExported,
                ["matchedTopBoneIndexCount"] = candidate.MatchedTopBoneIndexCount,
                ["missingTopBoneIndexCount"] = candidate.MissingTopBoneIndexCount,
                ["boneDriverNodeNameMatchCount"] = candidate.BoneDriverNodeNameMatchCount,
                ["boneDriverNodeNameMatches"] = new JArray((candidate.BoneDriverNodeNameMatches ?? Array.Empty<string>()).Take(32).Select(x => new JValue(x))),
                ["mappedTopBoneRefs"] = new JArray((candidate.MappedTopBoneRefs ?? Array.Empty<TransformNodeTableCandidateRef>()).Take(24).Select(x => x.ToJson())),
                ["rule"] = "这是外部 VisualCell 的 transformNodes 候选，只能作为完整角色装配/骨架上下文线索；选中网格自身没有确定 joint 映射时仍不能写 glTF skin。",
            };
        }

        private static string SummarizeSelectedVisualCellTransformNodeTableStatus(
            IEnumerable<TransformNodeTableCandidate> candidates,
            SelectedVisualCellInfo selectedVisualCell)
        {
            if (selectedVisualCell == null || !selectedVisualCell.PathId.HasValue)
            {
                return "unknownSelectedVisualCell";
            }

            var selectedCandidates = (candidates ?? Array.Empty<TransformNodeTableCandidate>())
                .Where(x => x.IsSelectedVisualCell)
                .ToArray();
            if (selectedCandidates.Length == 0)
            {
                return selectedVisualCell.TransformNodeCount == 0
                    ? "selectedVisualCellHasNoTransformNodes"
                    : "missingSelectedVisualCellTransformNodeCandidate";
            }

            return selectedCandidates.Any(x => x.CoversAvatarBoneIndexRange)
                ? "selectedVisualCellRangeCoveringCandidate"
                : "selectedVisualCellPresentButInsufficientForAvatarBoneRange";
        }

        private static int? GetSelectedVisualCellTransformNodeRequiredCount(
            IEnumerable<VisualCellPart> parts,
            IEnumerable<TransformNodeTableCandidate> candidates)
        {
            var selected = (candidates ?? Array.Empty<TransformNodeTableCandidate>())
                .Where(x => x.IsSelectedVisualCell && x.RequiredNodeCount.HasValue)
                .Select(x => x.RequiredNodeCount.Value)
                .ToArray();
            var sourceRequired = GetAvatarBoneRequiredNodeCount(parts);
            if (selected.Length == 0)
            {
                return sourceRequired;
            }

            return sourceRequired.HasValue
                ? Math.Max(sourceRequired.Value, selected.Max())
                : selected.Max();
        }

        private static int? GetSelectedVisualCellTransformNodeMissingCount(
            IEnumerable<VisualCellPart> parts,
            IEnumerable<TransformNodeTableCandidate> candidates,
            SelectedVisualCellInfo selectedVisualCell)
        {
            var required = GetSelectedVisualCellTransformNodeRequiredCount(parts, candidates);
            if (!required.HasValue)
            {
                return null;
            }

            var selectedCounts = (candidates ?? Array.Empty<TransformNodeTableCandidate>())
                .Where(x => x.IsSelectedVisualCell)
                .Select(x => x.TransformNodeCount)
                .ToArray();
            var actual = selectedCounts.Length == 0
                ? selectedVisualCell?.TransformNodeCount ?? 0
                : selectedCounts.Max();
            return Math.Max(0, required.Value - actual);
        }

        private static int? GetAvatarBoneMaxIndex(IEnumerable<VisualCellPart> parts)
        {
            var values = (parts ?? Array.Empty<VisualCellPart>())
                .Select(x => x?.Mesh?.Skin?.AvatarMaxBoneIndex)
                .Where(x => x.HasValue)
                .Select(x => x.Value)
                .ToArray();
            return values.Length == 0 ? null : values.Max();
        }

        private static int? GetAvatarBoneRequiredNodeCount(IEnumerable<VisualCellPart> parts)
        {
            var maxIndex = GetAvatarBoneMaxIndex(parts);
            if (!maxIndex.HasValue)
            {
                return null;
            }

            // AvatarBoneWeights 的非负 boneIndex 是节点表的下标，因此需要 maxIndex + 1 个节点。
            // 这只用于解释为什么阻断 skin 映射，不代表已经证明 joint 名称或路径。
            return maxIndex.Value + 1;
        }

        private static string SummarizeSelectedVisualCellTransformNodeCoverageStatus(
            string selectedTransformNodeTableStatus,
            int? requiredNodeCount,
            int? missingNodeCount)
        {
            if (string.Equals(selectedTransformNodeTableStatus, "selectedVisualCellRangeCoveringCandidate", StringComparison.OrdinalIgnoreCase))
            {
                return "coversAvatarBoneIndexRange";
            }

            if (string.Equals(selectedTransformNodeTableStatus, "selectedVisualCellPresentButInsufficientForAvatarBoneRange", StringComparison.OrdinalIgnoreCase))
            {
                return missingNodeCount.HasValue && missingNodeCount.Value > 0
                    ? "insufficientForAvatarBoneRange"
                    : "insufficientForAvatarBoneRangeUnknownGap";
            }

            if (string.Equals(selectedTransformNodeTableStatus, "selectedVisualCellHasNoTransformNodes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(selectedTransformNodeTableStatus, "missingSelectedVisualCellTransformNodeCandidate", StringComparison.OrdinalIgnoreCase))
            {
                return "missingSelectedVisualCellTransformNodes";
            }

            return requiredNodeCount.HasValue ? "unverified" : "unknownRequiredNodeCount";
        }

        private static string SummarizeSourceSkinMappingStatus(
            string selectedTransformNodeTableStatus,
            IEnumerable<TransformNodeTableCandidate> candidates)
        {
            // 这里保持保守：节点数量覆盖也只是必要条件，不等于 joint 名称/路径映射已经确定。
            if (string.Equals(selectedTransformNodeTableStatus, "selectedVisualCellPresentButInsufficientForAvatarBoneRange", StringComparison.OrdinalIgnoreCase))
            {
                return "blockedSelectedTransformNodesInsufficient";
            }

            if (string.Equals(selectedTransformNodeTableStatus, "selectedVisualCellHasNoTransformNodes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(selectedTransformNodeTableStatus, "missingSelectedVisualCellTransformNodeCandidate", StringComparison.OrdinalIgnoreCase))
            {
                return "blockedSelectedTransformNodesMissing";
            }

            if (string.Equals(selectedTransformNodeTableStatus, "selectedVisualCellRangeCoveringCandidate", StringComparison.OrdinalIgnoreCase))
            {
                return "blockedJointNamesUnverified";
            }

            return (candidates ?? Array.Empty<TransformNodeTableCandidate>()).Any(x => x.CoversAvatarBoneIndexRange)
                ? "blockedRangeCoveringCandidatesNotSelected"
                : "blockedNoDeterministicJointMapping";
        }

        private static string ResolveAnimSkinBindPoseSlotStatus(SkinSummary summary)
        {
            if (summary == null || summary.AnimSkinDataCount <= 0)
            {
                return "missingAnimSkinData";
            }

            if (summary.BindPoseCount <= 0)
            {
                return "missingBindPoses";
            }

            if (!summary.AnimSkinMinBoneIndex.HasValue || !summary.AnimSkinMaxBoneIndex.HasValue)
            {
                return "animSkinNoWeightedVertices";
            }

            if (summary.AnimSkinMinBoneIndex.Value < 0)
            {
                return "animSkinNegativeBoneIndex";
            }

            return summary.AnimSkinMaxBoneIndex.Value < summary.BindPoseCount
                ? "animSkinBoneIndicesWithinBindPoseRange"
                : "animSkinBoneIndicesExceedBindPoseRange";
        }

        private static string SummarizeAnimSkinBindPoseSlotStatus(IEnumerable<SkinSummary> skins)
        {
            var statuses = (skins ?? Enumerable.Empty<SkinSummary>())
                .Select(x => x?.AnimSkinBindPoseSlotStatus)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (statuses.Length == 0)
            {
                return "missing";
            }

            return statuses.Length == 1
                ? statuses[0]
                : "mixed:" + string.Join(",", statuses);
        }

        private static string SummarizeAvatarBoneOffsetPairStatus(IEnumerable<SkinSummary> skins)
        {
            var statuses = (skins ?? Enumerable.Empty<SkinSummary>())
                .Where(x => x != null && (x.AvatarBoneOffsetCount > 0 || x.AvatarBoneWeightsCount > 0))
                .Select(x => x.AvatarBoneOffsetPairStatus)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (statuses.Length == 0)
            {
                return "missing";
            }

            return statuses.Length == 1
                ? statuses[0]
                : "mixed:" + string.Join(",", statuses);
        }

        private static IEnumerable<string> GetTransformNodeTableCandidateVisualCells(
            IEnumerable<TransformNodeTableCandidate> candidates)
        {
            return (candidates ?? Array.Empty<TransformNodeTableCandidate>())
                .Select(x => x.VisualCellGameObjectName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> GetTransformNodeTableRangeCoveringVisualCells(
            IEnumerable<TransformNodeTableCandidate> candidates)
        {
            return (candidates ?? Array.Empty<TransformNodeTableCandidate>())
                .Where(x => x.CoversAvatarBoneIndexRange)
                .Select(x => x.VisualCellGameObjectName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> GetTransformNodeTableRangeCoveringMappedNodeNames(
            IEnumerable<VisualCellPart> parts)
        {
            return GetTransformNodeTableRangeCoveringRefs(parts)
                .Select(x => x.Ref.GameObjectName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Take(64);
        }

        private static JArray BuildTransformNodeTableRangeCoveringEvidence(
            IEnumerable<VisualCellPart> parts)
        {
            var refs = GetTransformNodeTableRangeCoveringRefs(parts).ToArray();
            var groups = refs
                .GroupBy(
                    x => $"{NormalizeSerializedFileName(x.Candidate.SerializedFile)}:{x.Candidate.VisualCellPathId}",
                    StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x.Max(y => y.Candidate.MatchedTopBoneIndexCount))
                .ThenByDescending(x => x.Max(y => y.Candidate.TransformNodeCount))
                .ThenBy(x => x.First().Candidate.VisualCellGameObjectName, StringComparer.OrdinalIgnoreCase);

            var result = new JArray();
            foreach (var group in groups.Take(8))
            {
                var first = group.First().Candidate;
                var mappedRefs = group
                    .OrderByDescending(x => x.Ref.WeightedRefCount)
                    .ThenBy(x => x.PartName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Ref.BoneIndex)
                    .Take(24)
                    .Select(x => new JObject
                    {
                        ["partName"] = x.PartName,
                        ["boneIndex"] = x.Ref.BoneIndex,
                        ["weightedRefCount"] = x.Ref.WeightedRefCount,
                        ["candidateGameObjectName"] = x.Ref.GameObjectName,
                        ["candidateGameObjectPathId"] = x.Ref.GameObjectPathId,
                        ["candidateTransformPathId"] = x.Ref.TransformPathId,
                    });

                result.Add(new JObject
                {
                    ["status"] = first.Status,
                    ["serializedFile"] = first.SerializedFile,
                    ["visualCellPathId"] = first.VisualCellPathId,
                    ["visualCellGameObjectName"] = first.VisualCellGameObjectName,
                    ["isSelectedVisualCell"] = first.IsSelectedVisualCell,
                    ["transformNodeCount"] = first.TransformNodeCount,
                    ["requiredNodeCountMax"] = group.Max(x => x.Candidate.RequiredNodeCount),
                    ["usedBoneIndexCountMax"] = group.Max(x => x.Candidate.UsedBoneIndexCount),
                    ["coveredUsedBoneIndexCountMax"] = group.Max(x => x.Candidate.CoveredUsedBoneIndexCount),
                    ["missingUsedBoneIndexCountMin"] = group.Min(x => x.Candidate.MissingUsedBoneIndexCount),
                    ["coversAllUsedBoneIndices"] = group.Any(x => x.Candidate.CoversAllUsedBoneIndices),
                    ["allCoveredUsedBoneTransformsExported"] = group.Any(x => x.Candidate.CoversAllUsedBoneIndices && x.Candidate.AllCoveredUsedBoneTransformsExported),
                    ["matchedTopBoneIndexCountMax"] = group.Max(x => x.Candidate.MatchedTopBoneIndexCount),
                    ["missingTopBoneIndexCountMin"] = group.Min(x => x.Candidate.MissingTopBoneIndexCount),
                    ["boneDriverNodeNameMatchCount"] = first.BoneDriverNodeNameMatchCount,
                    ["boneDriverNodeNameMatches"] = new JArray((first.BoneDriverNodeNameMatches ?? Array.Empty<string>()).Take(32).Select(x => new JValue(x))),
                    ["partNames"] = new JArray(group.Select(x => x.PartName).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                    ["mappedNodeNames"] = new JArray(group.Select(x => x.Ref.GameObjectName).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(64)),
                    ["mappedTopBoneRefs"] = new JArray(mappedRefs),
                    ["rule"] = "这些是 range-covering transformNodes 候选按 AvatarBoneWeights 高频 boneIndex 得到的节点名样本；候选不属于选中 VisualCell 或节点名语义不一致时，只能诊断，不能写 glTF skin。"
                });
            }

            return result;
        }

        private static IEnumerable<(string PartName, TransformNodeTableCandidate Candidate, TransformNodeTableCandidateRef Ref)> GetTransformNodeTableRangeCoveringRefs(
            IEnumerable<VisualCellPart> parts)
        {
            foreach (var part in parts ?? Array.Empty<VisualCellPart>())
            {
                var candidates = part.Mesh?.Skin?.TransformNodeTableCandidates ?? Enumerable.Empty<TransformNodeTableCandidate>();
                foreach (var candidate in candidates.Where(x => x.CoversAvatarBoneIndexRange))
                {
                    foreach (var mappedRef in candidate.MappedTopBoneRefs ?? Array.Empty<TransformNodeTableCandidateRef>())
                    {
                        if (string.IsNullOrWhiteSpace(mappedRef.GameObjectName))
                        {
                            continue;
                        }

                        yield return (part.GameObjectName, candidate, mappedRef);
                    }
                }
            }
        }

        private static string SummarizeHairDeformDataStatus(IEnumerable<HairDeformSummary> summaries)
        {
            var list = summaries?.ToArray() ?? Array.Empty<HairDeformSummary>();
            if (list.Length == 0 || list.All(x => x.Count == 0))
            {
                return "missing";
            }
            if (list.Any(x => x.Status == "countMismatch"))
            {
                return "countMismatch";
            }
            return "packedHalf4Diagnostic";
        }

        private static IReadOnlyList<TransformNodeTable> LoadTransformNodeTableCandidates(
            string sourceIndexPath,
            IReadOnlyCollection<VisualCellPart> parts,
            List<string> warnings)
        {
            var result = new List<TransformNodeTable>();
            if (string.IsNullOrWhiteSpace(sourceIndexPath) || !File.Exists(sourceIndexPath))
            {
                return result;
            }

            var files = parts
                .Select(x => NormalizeSerializedFileName(x.AvatarMeshFile))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (files.Length == 0)
            {
                return result;
            }

            try
            {
                SQLitePCL.Batteries_V2.Init();
                using var connection = new SqliteConnection($"Data Source={sourceIndexPath};Mode=ReadOnly");
                connection.Open();
                foreach (var file in files)
                {
                    foreach (var visualCell in LoadActorBodyVisualCellIds(connection, file))
                    {
                        var nodeIds = LoadTransformNodeIds(connection, file, visualCell.PathId, maxCount: 128);
                        if (nodeIds.Count == 0)
                        {
                            continue;
                        }

                        var nodes = new List<TransformNodeTableNode>();
                        for (var index = 0; index < nodeIds.Count; index++)
                        {
                            var transformPathId = nodeIds[index];
                            var gameObjectPathId = ResolveGameObjectForComponent(connection, file, transformPathId);
                            nodes.Add(new TransformNodeTableNode(
                                index,
                                transformPathId,
                                gameObjectPathId,
                                ResolveObjectName(connection, file, gameObjectPathId)));
                        }

                        result.Add(new TransformNodeTable
                        {
                            SerializedFile = file,
                            VisualCellPathId = visualCell.PathId,
                            VisualCellName = visualCell.Name,
                            VisualCellGameObjectName = ResolveObjectName(connection, file, ResolveGameObjectForComponent(connection, file, visualCell.PathId)),
                            Container = ResolveFirstContainer(connection, file, visualCell.PathId),
                            TransformNodeCount = CountTransformNodes(connection, file, visualCell.PathId),
                            Nodes = nodes,
                        });
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is SqliteException || ex is InvalidDataException)
            {
                warnings.Add($"transformNodeTableCandidateQueryFailed:{ex.Message}");
            }

            return result
                .OrderByDescending(x => x.TransformNodeCount)
                .ThenBy(x => x.VisualCellGameObjectName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static IEnumerable<SourceObjectLiteWithPathId> LoadActorBodyVisualCellIds(SqliteConnection connection, string serializedFile)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT path_id, name
FROM source_objects INDEXED BY idx_source_objects_file_path
WHERE serialized_file = $file
  AND type = 'MonoBehaviour'
  AND name = 'ActorBodyVisualCell'
ORDER BY path_id;";
            command.Parameters.AddWithValue("$file", serializedFile ?? string.Empty);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                yield return new SourceObjectLiteWithPathId(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? string.Empty : reader.GetString(1));
            }
        }

        private static List<long> LoadTransformNodeIds(SqliteConnection connection, string serializedFile, long visualCellPathId, int maxCount)
        {
            var result = new List<long>();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT to_path_id
FROM source_relations INDEXED BY idx_source_relations_from
WHERE from_path_id = $pathId
  AND relation = 'monoBehaviour.pptr'
  AND from_file = $file
  AND json_extract(raw_json, '$.details.path') = 'transformNodes.data'
ORDER BY id
LIMIT $limit;";
            command.Parameters.AddWithValue("$file", serializedFile ?? string.Empty);
            command.Parameters.AddWithValue("$pathId", visualCellPathId);
            command.Parameters.AddWithValue("$limit", Math.Max(1, maxCount));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(reader.GetInt64(0));
            }

            return result;
        }

        private static int CountTransformNodes(SqliteConnection connection, string serializedFile, long visualCellPathId)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT COUNT(DISTINCT to_path_id)
FROM source_relations INDEXED BY idx_source_relations_from
WHERE from_path_id = $pathId
  AND relation = 'monoBehaviour.pptr'
  AND from_file = $file
  AND json_extract(raw_json, '$.details.path') = 'transformNodes.data';";
            command.Parameters.AddWithValue("$file", serializedFile ?? string.Empty);
            command.Parameters.AddWithValue("$pathId", visualCellPathId);
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private static string ResolveFirstContainer(SqliteConnection connection, string serializedFile, long pathId)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT json_extract(raw_json, '$.details.container')
FROM source_relations INDEXED BY idx_source_relations_to
WHERE to_path_id = $pathId
  AND relation = 'assetBundle.containerPreload'
  AND to_file = $file
ORDER BY id
LIMIT 1;";
            command.Parameters.AddWithValue("$file", serializedFile ?? string.Empty);
            command.Parameters.AddWithValue("$pathId", pathId);
            return command.ExecuteScalar() as string ?? string.Empty;
        }

        private static IEnumerable<TransformNodeTableCandidate> BuildTransformNodeTableCandidates(
            SkinSummary skin,
            IReadOnlyList<TransformNodeTable> tables,
            string serializedFile,
            IReadOnlyDictionary<long, ExportedTransformNode> exportedTransformNodes,
            ISet<string> boneDriverNodeNames,
            long? selectedVisualCellPathId)
        {
            if (skin == null || tables == null || tables.Count == 0)
            {
                yield break;
            }

            var file = NormalizeSerializedFileName(serializedFile);
            var usedRefs = skin.AvatarBoneRefs
                .Where(x => x.BoneIndex >= 0)
                .OrderBy(x => x.BoneIndex)
                .ToArray();
            var topRefs = skin.AvatarTopBoneRefs
                .Where(x => x.BoneIndex >= 0)
                .Take(12)
                .ToArray();
            if (usedRefs.Length == 0 && topRefs.Length == 0)
            {
                yield break;
            }

            var requiredNodeCount = skin.AvatarMaxBoneIndex.HasValue
                ? skin.AvatarMaxBoneIndex.Value + 1
                : (int?)null;
            var selectedTable = selectedVisualCellPathId.HasValue
                ? tables.FirstOrDefault(x => string.Equals(x.SerializedFile, file, StringComparison.OrdinalIgnoreCase)
                    && x.VisualCellPathId == selectedVisualCellPathId.Value)
                : null;
            var driverNames = boneDriverNodeNames ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in tables.Where(x => string.Equals(x.SerializedFile, file, StringComparison.OrdinalIgnoreCase)))
            {
                var coveredUsedRefs = usedRefs
                    .Where(x => x.BoneIndex < table.Nodes.Count)
                    .ToArray();
                var mappedUsedRefs = coveredUsedRefs
                    .Select(x =>
                    {
                        var node = table.Nodes[x.BoneIndex];
                        ExportedTransformNode exportedTransform = null;
                        exportedTransformNodes?.TryGetValue(node.TransformPathId, out exportedTransform);
                        return new TransformNodeTableUsedBoneRef(
                            x.BoneIndex,
                            x.WeightedRefCount,
                            node.GameObjectName,
                            node.GameObjectPathId,
                            node.TransformPathId,
                            exportedTransform != null,
                            exportedTransform?.HasLocalTrs == true,
                            exportedTransform);
                    })
                    .ToArray();
                var missingUsedRefs = usedRefs
                    .Where(x => x.BoneIndex >= table.Nodes.Count)
                    .ToArray();
                var exportedCoveredUsedBoneTransformCount = coveredUsedRefs.Count(x =>
                {
                    var node = table.Nodes[x.BoneIndex];
                    return exportedTransformNodes != null
                        && exportedTransformNodes.TryGetValue(node.TransformPathId, out var exportedTransform)
                        && exportedTransform.HasLocalTrs;
                });
                var mapped = topRefs
                    .Where(x => x.BoneIndex >= 0 && x.BoneIndex < table.Nodes.Count)
                    .Select(x =>
                    {
                        var node = table.Nodes[x.BoneIndex];
                        ExportedTransformNode exportedTransform = null;
                        exportedTransformNodes?.TryGetValue(node.TransformPathId, out exportedTransform);
                        return new TransformNodeTableCandidateRef(x.BoneIndex, x.WeightedRefCount, node.GameObjectName, node.GameObjectPathId, node.TransformPathId, exportedTransform);
                    })
                    .Where(x => !string.IsNullOrWhiteSpace(x.GameObjectName))
                    .ToArray();
                if (mapped.Length == 0 && coveredUsedRefs.Length == 0)
                {
                    continue;
                }

                var boneDriverMatches = table.Nodes
                    .Where(x => !string.IsNullOrWhiteSpace(x.GameObjectName) && driverNames.Contains(x.GameObjectName))
                    .Select(x => x.GameObjectName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                yield return new TransformNodeTableCandidate
                {
                    Status = "indexOrderCandidateOnly",
                    SerializedFile = table.SerializedFile,
                    VisualCellPathId = table.VisualCellPathId,
                    VisualCellGameObjectName = table.VisualCellGameObjectName,
                    Container = table.Container,
                    IsSelectedVisualCell = selectedVisualCellPathId.HasValue && table.VisualCellPathId == selectedVisualCellPathId.Value,
                    TransformNodeCount = table.TransformNodeCount,
                    RequiredNodeCount = requiredNodeCount,
                    CoversAvatarBoneIndexRange = requiredNodeCount.HasValue && table.TransformNodeCount >= requiredNodeCount.Value,
                    MatchedTopBoneIndexCount = mapped.Length,
                    MissingTopBoneIndexCount = topRefs.Length - mapped.Length,
                    MappedTopBoneRefs = mapped,
                    UsedBoneIndexCount = usedRefs.Length,
                    CoveredUsedBoneIndexCount = coveredUsedRefs.Length,
                    MissingUsedBoneIndexCount = Math.Max(0, usedRefs.Length - coveredUsedRefs.Length),
                    CoversAllUsedBoneIndices = usedRefs.Length > 0 && coveredUsedRefs.Length == usedRefs.Length,
                    UsedBoneIndexMax = usedRefs.Length == 0 ? null : usedRefs.Max(x => x.BoneIndex),
                    CoveredUsedBoneRefs = mappedUsedRefs,
                    MissingUsedBoneRefs = missingUsedRefs,
                    ExportedCoveredUsedBoneTransformCount = exportedCoveredUsedBoneTransformCount,
                    MissingExportedCoveredUsedBoneTransformCount = Math.Max(0, coveredUsedRefs.Length - exportedCoveredUsedBoneTransformCount),
                    AllCoveredUsedBoneTransformsExported = coveredUsedRefs.Length > 0 && exportedCoveredUsedBoneTransformCount == coveredUsedRefs.Length,
                    SelectedVisualCellIndexOrderComparison = CompareWithSelectedTransformNodeTable(table, selectedTable, usedRefs),
                    BoneDriverNodeNameMatchCount = boneDriverMatches.Length,
                    BoneDriverNodeNameMatches = boneDriverMatches,
                };
            }
        }

        private static SelectedTransformNodeTableComparison CompareWithSelectedTransformNodeTable(
            TransformNodeTable candidate,
            TransformNodeTable selectedTable,
            IReadOnlyCollection<BoneRefCount> usedRefs)
        {
            if (candidate == null)
            {
                return null;
            }

            if (selectedTable == null)
            {
                return new SelectedTransformNodeTableComparison
                {
                    Status = "selectedVisualCellTransformNodeTableMissing",
                };
            }

            if (candidate.VisualCellPathId == selectedTable.VisualCellPathId
                && string.Equals(candidate.SerializedFile, selectedTable.SerializedFile, StringComparison.OrdinalIgnoreCase))
            {
                var comparedCount = (usedRefs ?? Array.Empty<BoneRefCount>())
                    .Count(x => x.BoneIndex >= 0 && x.BoneIndex < selectedTable.Nodes.Count);
                return new SelectedTransformNodeTableComparison
                {
                    Status = "selectedVisualCellSelf",
                    ComparedBoneIndexCount = comparedCount,
                    NameMatchCount = comparedCount,
                };
            }

            var refs = new List<SelectedTransformNodeTableComparisonRef>();
            foreach (var usedRef in usedRefs ?? Array.Empty<BoneRefCount>())
            {
                if (usedRef.BoneIndex < 0
                    || usedRef.BoneIndex >= selectedTable.Nodes.Count
                    || usedRef.BoneIndex >= candidate.Nodes.Count)
                {
                    continue;
                }

                var selectedNode = selectedTable.Nodes[usedRef.BoneIndex];
                var candidateNode = candidate.Nodes[usedRef.BoneIndex];
                var nameMatches = !string.IsNullOrWhiteSpace(selectedNode.GameObjectName)
                    && string.Equals(selectedNode.GameObjectName, candidateNode.GameObjectName, StringComparison.OrdinalIgnoreCase);
                refs.Add(new SelectedTransformNodeTableComparisonRef(
                    usedRef.BoneIndex,
                    usedRef.WeightedRefCount,
                    selectedNode.GameObjectName,
                    selectedNode.GameObjectPathId,
                    selectedNode.TransformPathId,
                    candidateNode.GameObjectName,
                    candidateNode.GameObjectPathId,
                    candidateNode.TransformPathId,
                    nameMatches));
            }

            var matchCount = refs.Count(x => x.NameMatches);
            var conflictCount = refs.Count - matchCount;
            return new SelectedTransformNodeTableComparison
            {
                Status = refs.Count == 0
                    ? "noSharedUsedBoneIndicesWithSelectedVisualCell"
                    : conflictCount == 0
                        ? "sameIndexNamesMatchSelectedVisualCell"
                        : "sameIndexNamesConflictWithSelectedVisualCell",
                ComparedBoneIndexCount = refs.Count,
                NameMatchCount = matchCount,
                NameConflictCount = conflictCount,
                Conflicts = refs.Where(x => !x.NameMatches).Take(32).ToArray(),
            };
        }

        private static JObject ToReportJson(
            VisualCellPart part,
            VisualCellMaterialBinding materialBinding,
            VisualCellRendererSkinBinding rendererSkinBinding,
            AvatarMeshDataRelationEvidence avatarMeshRelations,
            IReadOnlyCollection<AvatarPartDataEvidence> avatarPartDataEvidence,
            AssistantHeadCollisionEvidence headCollisionEvidence)
        {
            var meshJson = ToReportJson(part.Mesh, part.MeshJsonPath);
            meshJson["assistantPathId"] = part.AssistantPathId;
            meshJson["gameObjectName"] = part.GameObjectName;
            meshJson["rendererFile"] = part.RendererFile;
            meshJson["rendererPathId"] = part.RendererPathId;
            meshJson["avatarMeshFile"] = part.AvatarMeshFile;
            meshJson["avatarMeshPathId"] = part.AvatarMeshPathId;
            meshJson["assistantJson"] = Path.GetFullPath(part.AssistantJsonPath);
            meshJson["materialBinding"] = materialBinding?.ToJson();
            meshJson["rendererSkinBinding"] = rendererSkinBinding?.ToJson();
            meshJson["avatarMeshDataRelations"] = avatarMeshRelations?.ToJson();
            meshJson["avatarPartDataEvidenceStatus"] = SummarizeAvatarPartDataEvidenceStatus(avatarPartDataEvidence);
            meshJson["avatarPartDataEvidence"] = new JArray((avatarPartDataEvidence ?? Array.Empty<AvatarPartDataEvidence>()).Select(x => x.ToJson()));
            meshJson["headCollisionData"] = headCollisionEvidence?.ToJson();
            return meshJson;
        }

        private static HairCustomizationTintEvidence LoadHairCustomizationTintEvidence(
            string sourceIndexPath,
            SelectedVisualCellInfo selectedVisualCell,
            List<string> warnings,
            string typeTreeJsonFolder)
        {
            // 这里先只做保守诊断：有脚本、无直连、字段不完整时继续保留 needsCustomizationTint。
            var evidence = new HairCustomizationTintEvidence
            {
                Status = string.IsNullOrWhiteSpace(sourceIndexPath) ? "sourceIndexNotProvided" : "noCustomizationScriptsFound",
                SelectedVisualCellFile = selectedVisualCell?.SerializedFile,
                SelectedVisualCellPathId = selectedVisualCell?.PathId,
                SelectedGameObjectPathId = selectedVisualCell?.GameObjectPathId,
            };
            LoadHairCustomizationTypeTreeEvidence(typeTreeJsonFolder, evidence);
            if (string.IsNullOrWhiteSpace(sourceIndexPath))
            {
                if (evidence.TypeTreeColorDataCount > 0 || evidence.TypeTreePartDataCount > 0)
                {
                    evidence.Status = "customizationTypeTreeFieldsExportedNoSourceIndex";
                }
                return evidence;
            }
            if (!File.Exists(sourceIndexPath))
            {
                evidence.Status = "sourceIndexMissing";
                warnings?.Add($"missingSourceIndexForHairCustomizationTint:{sourceIndexPath}");
                return evidence;
            }

            try
            {
                SQLitePCL.Batteries_V2.Init();
                using var connection = new SqliteConnection($"Data Source={sourceIndexPath};Mode=ReadOnly");
                connection.Open();
                LoadHairCustomizationScriptCounts(connection, evidence);
                CountHairCustomizationDirectLinks(connection, evidence);
                evidence.Status = ResolveHairCustomizationTintStatus(evidence);
            }
            catch (Exception ex)
            {
                evidence.Status = "sourceIndexQueryFailed";
                warnings?.Add($"hairCustomizationTintQueryFailed:{ex.Message}");
            }

            return evidence;
        }

        private static FaceRuntimeEvidence LoadFaceRuntimeEvidence(
            string typeTreeJsonFolder,
            IReadOnlyDictionary<long, ManifestEntry> manifest,
            IReadOnlyList<VisualCellPart> parts,
            SelectedVisualCellInfo selectedVisualCell,
            int? sourceSkinRequiredNodeCount,
            List<string> warnings)
        {
            // Naraka 脸部网格的 joint 线索来自挂在同一个 GameObject 上的 AvatarFaceRuntime。
            // 这里只把它和 AvatarMeshData 的 boneIndex 表对上，先不写 glTF skin。
            var evidence = new FaceRuntimeEvidence
            {
                Status = "faceRuntimeEvidenceMissing",
                SelectedVisualCellFile = selectedVisualCell?.SerializedFile,
                SelectedVisualCellPathId = selectedVisualCell?.PathId,
                SelectedGameObjectPathId = selectedVisualCell?.GameObjectPathId,
                SourceSkinRequiredNodeCount = sourceSkinRequiredNodeCount,
            };
            if (string.IsNullOrWhiteSpace(typeTreeJsonFolder) || !Directory.Exists(typeTreeJsonFolder))
            {
                evidence.Status = "typeTreeFolderMissing";
                return evidence;
            }
            if (selectedVisualCell?.GameObjectPathId.HasValue != true)
            {
                evidence.Status = "selectedGameObjectMissing";
                return evidence;
            }

            foreach (var path in Directory.EnumerateFiles(typeTreeJsonFolder, "*.json", SearchOption.TopDirectoryOnly))
            {
                if (!TryReadJsonObject(path, out var json))
                {
                    continue;
                }
                if (ReadPPtrPathId(json["m_GameObject"]) != selectedVisualCell.GameObjectPathId.Value)
                {
                    continue;
                }
                if (ReadPPtrPathId(json["m_AvatarFaceData"]) == 0)
                {
                    continue;
                }

                evidence.RuntimeJsonPath = path;
                evidence.RuntimePathId = ResolveManifestPathId(manifest, path);
                evidence.RuntimeRootAnchorPathId = ReadPPtrPathId(json["m_RootAnchor"]);
                evidence.RuntimeNeckNodePathId = ReadPPtrPathId(json["m_NeckNode"]);
                evidence.RuntimeHeadNodePathId = ReadPPtrPathId(json["m_HeadNode"]);
                evidence.AvatarFaceDataPathId = ReadPPtrPathId(json["m_AvatarFaceData"]);
                var runtimeBones = json["m_AvatarBones"] as JArray;
                evidence.RuntimeAvatarBoneCount = runtimeBones?.Count ?? 0;
                evidence.RuntimeBoneNames.AddRange(ReadFaceRuntimeBoneNames(runtimeBones).Take(96));
                break;
            }

            if (string.IsNullOrWhiteSpace(evidence.RuntimeJsonPath))
            {
                return evidence;
            }

            JObject avatarFaceDataJson = null;
            if (evidence.AvatarFaceDataPathId.HasValue
                && manifest != null
                && manifest.TryGetValue(evidence.AvatarFaceDataPathId.Value, out var faceDataEntry)
                && File.Exists(faceDataEntry.JsonPath)
                && TryReadJsonObject(faceDataEntry.JsonPath, out avatarFaceDataJson))
            {
                evidence.AvatarFaceDataJsonPath = faceDataEntry.JsonPath;
                evidence.AvatarFaceDataFile = faceDataEntry.SerializedFile;
            }

            if (avatarFaceDataJson == null)
            {
                evidence.Status = "avatarFaceDataJsonMissing";
                warnings?.Add($"avatarFaceDataJsonMissing:{evidence.AvatarFaceDataPathId}");
                return evidence;
            }

            evidence.AvatarFaceDataName = (string)avatarFaceDataJson["m_Name"];
            evidence.FaceType = avatarFaceDataJson["m_FaceType"]?.Value<int?>();
            var avatarBones = avatarFaceDataJson["m_AvatarBones"] as JArray;
            evidence.AvatarFaceDataBoneCount = avatarBones?.Count ?? 0;
            evidence.AvatarFaceDataBindPoseCount = avatarBones?
                .OfType<JObject>()
                .Count(x => x["m_BindLocalPosition"] != null
                    || x["m_BindLocalRotation"] != null
                    || x["m_BindLocalScale"] != null) ?? 0;
            evidence.AvatarFaceDataBoneSamples.AddRange(ReadAvatarFaceDataBoneSamples(avatarBones));

            var faceBoneNames = ReadAvatarFaceDataBoneNames(avatarBones).ToArray();
            evidence.RuntimeBoneNamesMatchAvatarFaceData = evidence.RuntimeBoneNames.Count == faceBoneNames.Length
                && evidence.RuntimeBoneNames.SequenceEqual(faceBoneNames, StringComparer.Ordinal);
            var usedRefs = parts
                .SelectMany(x => x.Mesh?.Skin?.AvatarBoneRefs ?? Enumerable.Empty<BoneRefCount>())
                .GroupBy(x => x.BoneIndex)
                .Select(x => new BoneRefCount(x.Key, x.Sum(y => y.WeightedRefCount)))
                .OrderBy(x => x.BoneIndex)
                .ToArray();
            evidence.UsedBoneIndexCount = usedRefs.Length;
            evidence.UsedBoneIndexMax = usedRefs.Length == 0 ? null : usedRefs.Max(x => x.BoneIndex);
            foreach (var boneRef in usedRefs)
            {
                if (boneRef.BoneIndex >= 0 && boneRef.BoneIndex < faceBoneNames.Length)
                {
                    evidence.MappedUsedBoneIndexCount++;
                    if (evidence.MappedUsedBoneSamples.Count < 64)
                    {
                        evidence.MappedUsedBoneSamples.Add(new FaceRuntimeMappedBoneRef(
                            boneRef.BoneIndex,
                            boneRef.WeightedRefCount,
                            faceBoneNames[boneRef.BoneIndex]));
                    }
                }
                else
                {
                    evidence.MissingUsedBoneIndexCount++;
                    if (evidence.MissingUsedBoneRefs.Count < 32)
                    {
                        evidence.MissingUsedBoneRefs.Add(boneRef);
                    }
                }
            }

            evidence.SourceSkinRequiredNodeCountMatchesAvatarFaceData =
                sourceSkinRequiredNodeCount.HasValue
                && sourceSkinRequiredNodeCount.Value == evidence.AvatarFaceDataBoneCount;
            evidence.AllUsedBoneIndicesMapped = usedRefs.Length > 0 && evidence.MappedUsedBoneIndexCount == usedRefs.Length;
            evidence.Status = ResolveFaceRuntimeEvidenceStatus(evidence);
            return evidence;
        }

        private static string ResolveFaceRuntimeEvidenceStatus(FaceRuntimeEvidence evidence)
        {
            if (evidence.AvatarFaceDataBoneCount <= 0)
            {
                return "avatarFaceDataBonesMissing";
            }
            if (evidence.SourceSkinRequiredNodeCountMatchesAvatarFaceData
                && evidence.AllUsedBoneIndicesMapped
                && evidence.RuntimeBoneNamesMatchAvatarFaceData)
            {
                return "avatarFaceDataMatchesSourceSkinBoneTable";
            }
            if (evidence.SourceSkinRequiredNodeCountMatchesAvatarFaceData && evidence.AllUsedBoneIndicesMapped)
            {
                return "avatarFaceDataBoneCountMatchesRequired";
            }
            if (evidence.SourceSkinRequiredNodeCount.HasValue)
            {
                return "avatarFaceDataBoneCountMismatch";
            }
            return "avatarFaceDataPresentNoSourceSkinRange";
        }

        private static IEnumerable<string> ReadFaceRuntimeBoneNames(JArray bones)
        {
            if (bones == null)
            {
                yield break;
            }
            foreach (var bone in bones.OfType<JObject>())
            {
                yield return (string)bone["m_BoneName"] ?? string.Empty;
            }
        }

        private static IEnumerable<string> ReadAvatarFaceDataBoneNames(JArray bones)
        {
            if (bones == null)
            {
                yield break;
            }
            foreach (var bone in bones.OfType<JObject>())
            {
                yield return (string)bone["m_Name"] ?? string.Empty;
            }
        }

        private static IEnumerable<FaceRuntimeBoneSample> ReadAvatarFaceDataBoneSamples(JArray bones)
        {
            if (bones == null)
            {
                yield break;
            }

            var index = 0;
            foreach (var bone in bones.OfType<JObject>())
            {
                var childIndices = (bone["m_Children"] as JArray)?
                    .Values<int>()
                    .Where(x => x >= 0)
                    .ToArray() ?? Array.Empty<int>();
                var position = TryReadVector3Value(bone["m_BindLocalPosition"], out var p) ? p : (Vector3Value?)null;
                var rotation = TryReadVector4Value(bone["m_BindLocalRotation"]?["value"], out var r) ? r : (Vector4Value?)null;
                var scale = TryReadVector3Value(bone["m_BindLocalScale"], out var s) ? s : (Vector3Value?)null;
                yield return new FaceRuntimeBoneSample(
                    index,
                    (string)bone["m_Name"],
                    bone["m_BoneSide"]?.Value<int?>(),
                    childIndices,
                    position,
                    rotation,
                    scale);
                index++;
            }
        }

        private static bool TryReadVector3Value(JToken token, out Vector3Value value)
        {
            value = default;
            if (token == null)
            {
                return false;
            }

            try
            {
                value = ReadVector3(token, "vector3");
                return true;
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is FormatException || ex is OverflowException)
            {
                return false;
            }
        }

        private static bool TryReadVector4Value(JToken token, out Vector4Value value)
        {
            value = default;
            if (token == null)
            {
                return false;
            }

            try
            {
                value = ReadVector4(token, "vector4");
                return true;
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is FormatException || ex is OverflowException)
            {
                return false;
            }
        }

        private static bool TryReadJsonObject(string path, out JObject json)
        {
            json = null;
            try
            {
                json = JObject.Parse(File.ReadAllText(path));
                return true;
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static long? ResolveManifestPathId(IReadOnlyDictionary<long, ManifestEntry> manifest, string jsonPath)
        {
            if (manifest == null || string.IsNullOrWhiteSpace(jsonPath))
            {
                return null;
            }

            foreach (var pair in manifest)
            {
                if (PathsEqual(pair.Value.JsonPath, jsonPath))
                {
                    return pair.Key;
                }
            }
            return null;
        }

        private static string ResolveHairCustomizationTintStatus(HairCustomizationTintEvidence evidence)
        {
            if (evidence.TotalScriptInstanceCount <= 0)
            {
                return "noCustomizationScriptsFound";
            }
            if (evidence.DirectLinkCount > 0)
            {
                return evidence.HasTypeTreeTintFields
                    ? "directCustomizationPPtrFoundWithTypeTreeFields"
                    : "directCustomizationPPtrFound";
            }
            if (evidence.HasTypeTreeTintFields)
            {
                return "customizationTypeTreeFieldsExportedNoDirectVisualCellLink";
            }
            return evidence.LightweightRawJsonCount >= evidence.TotalScriptInstanceCount
                ? "customizationScriptsPresentButRawJsonLightweight"
                : "customizationScriptsPresentNoDirectVisualCellLink";
        }

        private static void LoadHairCustomizationTypeTreeEvidence(string typeTreeJsonFolder, HairCustomizationTintEvidence evidence)
        {
            if (string.IsNullOrWhiteSpace(typeTreeJsonFolder) || !Directory.Exists(typeTreeJsonFolder))
            {
                return;
            }

            foreach (var path in Directory.EnumerateFiles(typeTreeJsonFolder, "*.json", SearchOption.TopDirectoryOnly))
            {
                JObject json;
                try
                {
                    json = JObject.Parse(File.ReadAllText(path));
                }
                catch (JsonException)
                {
                    continue;
                }
                catch (IOException)
                {
                    continue;
                }

                var name = (string)json["m_Name"] ?? Path.GetFileNameWithoutExtension(path);
                var colorData = json["MyData"] as JObject;
                if (colorData?["HighLightingColor"] is JObject highLightingColor)
                {
                    evidence.TypeTreeColorDataCount++;
                    if (evidence.TypeTreeColorSamples.Count < 8)
                    {
                        evidence.TypeTreeColorSamples.Add(new HairCustomizationColorSample
                        {
                            Name = name,
                            Description = (string)colorData["Description"],
                            HighLightingColor = highLightingColor,
                            Ks = colorData["Ks"] as JObject,
                            KsBoost = colorData["KsBoost"]?.Value<double?>(),
                            KsWidth = colorData["KsWidth"]?.Value<double?>(),
                        });
                    }
                }
                if (json["HighLightingAreaOverride"] is JArray)
                {
                    evidence.TypeTreePartDataCount++;
                }
                if (json["CustomColorCandidates"] is JArray candidates)
                {
                    evidence.TypeTreeGroupCount++;
                    evidence.TypeTreeGroupCandidateCount += candidates.Count;
                }
                if (string.Equals(name, "hair_custom_config_asset", StringComparison.OrdinalIgnoreCase))
                {
                    evidence.TypeTreeConfigCount++;
                }
            }
        }

        private static void LoadHairCustomizationScriptCounts(SqliteConnection connection, HairCustomizationTintEvidence evidence)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT name, serialized_file, path_id
FROM source_objects
WHERE type = 'MonoScript'
  AND name IN (
      'HairCustomConfigAsset',
      'SpecialHairCustomColorData',
      'SpecialHairCustomPartDecorator',
      'SpecialHairCustomPartDecoratorGroup',
      'UIHairConfigCustomCellAdapter',
      'UIHairConfigAdvCustomCellAdapter',
      'UIHairConfigCustomCellHolderSerialize',
      'UIHairConfigAdvCustomCellHolderSerialize',
      'UIHairEffectCustomPanelSerialize'
  )
ORDER BY name COLLATE NOCASE;";

            var scripts = new List<SourceObjectRef>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    scripts.Add(new SourceObjectRef(ReadString(reader, 0), ReadString(reader, 1), ReadLong(reader, 2)));
                }
            }

            foreach (var script in scripts)
            {
                var scriptName = script.Name;
                var scriptFile = script.SerializedFile;
                var scriptPathId = script.PathId;
                var (instanceCount, maxRawJsonLength) = CountMonoBehavioursUsingScript(connection, scriptFile, scriptPathId);
                var row = new HairCustomizationScriptCount
                {
                    ScriptName = scriptName,
                    InstanceCount = instanceCount,
                    MaxRawJsonLength = maxRawJsonLength,
                };
                evidence.ScriptCounts.Add(row);
            }
        }

        private static (long InstanceCount, long MaxRawJsonLength) CountMonoBehavioursUsingScript(SqliteConnection connection, string scriptFile, long scriptPathId)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT COUNT(*) AS instance_count, COALESCE(MAX(LENGTH(obj.raw_json)), 0) AS max_raw_len
FROM source_relations scriptRel INDEXED BY idx_source_relations_to
JOIN source_objects obj
  ON obj.serialized_file = scriptRel.from_file
 AND obj.path_id = scriptRel.from_path_id
 AND obj.type = 'MonoBehaviour'
WHERE scriptRel.relation = 'monoBehaviour.script'
  AND scriptRel.to_file = $scriptFile
  AND scriptRel.to_path_id = $scriptPathId;";
            command.Parameters.AddWithValue("$scriptFile", scriptFile ?? string.Empty);
            command.Parameters.AddWithValue("$scriptPathId", scriptPathId);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? (ReadLong(reader, 0), ReadLong(reader, 1))
                : (0, 0);
        }

        private static string ReadString(SqliteDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
        }

        private static long ReadLong(SqliteDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }

        private static void CountHairCustomizationDirectLinks(SqliteConnection connection, HairCustomizationTintEvidence evidence)
        {
            var selectedFile = evidence.SelectedVisualCellFile ?? string.Empty;
            var selectedPathIds = new[] { evidence.SelectedVisualCellPathId, evidence.SelectedGameObjectPathId }
                .Where(x => x.HasValue)
                .Select(x => x.Value)
                .Distinct()
                .ToArray();
            if (string.IsNullOrWhiteSpace(selectedFile) || selectedPathIds.Length == 0)
            {
                return;
            }

            foreach (var pathId in selectedPathIds)
            {
                evidence.DirectLinkCount += CountHairCustomizationDirectLinksForTarget(connection, selectedFile, pathId);
            }
        }

        private static long CountHairCustomizationDirectLinksForTarget(SqliteConnection connection, string selectedFile, long selectedPathId)
        {
            return CountHairCustomizationIncomingLinks(connection, selectedFile, selectedPathId)
                + CountHairCustomizationOutgoingLinks(connection, selectedFile, selectedPathId);
        }

        private static long CountHairCustomizationIncomingLinks(SqliteConnection connection, string selectedFile, long selectedPathId)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT rel.from_file, rel.from_path_id
FROM source_relations rel INDEXED BY idx_source_relations_to
WHERE rel.relation = 'monoBehaviour.pptr'
  AND rel.to_file = $selectedFile
  AND rel.to_path_id = $selectedPathId;";
            command.Parameters.AddWithValue("$selectedFile", selectedFile ?? string.Empty);
            command.Parameters.AddWithValue("$selectedPathId", selectedPathId);
            var sources = new List<SourceObjectRef>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    sources.Add(new SourceObjectRef(string.Empty, ReadString(reader, 0), ReadLong(reader, 1)));
                }
            }
            return sources.Count(x => IsHairCustomizationMonoBehaviour(connection, x.SerializedFile, x.PathId));
        }

        private static long CountHairCustomizationOutgoingLinks(SqliteConnection connection, string selectedFile, long selectedPathId)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT rel.to_file, rel.to_path_id
FROM source_relations rel INDEXED BY idx_source_relations_from
WHERE rel.relation = 'monoBehaviour.pptr'
  AND rel.from_file = $selectedFile
  AND rel.from_path_id = $selectedPathId;";
            command.Parameters.AddWithValue("$selectedFile", selectedFile ?? string.Empty);
            command.Parameters.AddWithValue("$selectedPathId", selectedPathId);
            var targets = new List<SourceObjectRef>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    targets.Add(new SourceObjectRef(string.Empty, ReadString(reader, 0), ReadLong(reader, 1)));
                }
            }
            return targets.Count(x => IsHairCustomizationMonoBehaviour(connection, x.SerializedFile, x.PathId));
        }

        private static bool IsHairCustomizationMonoBehaviour(SqliteConnection connection, string serializedFile, long pathId)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT script.name
FROM source_relations scriptRel INDEXED BY idx_source_relations_from
JOIN source_objects script
  ON script.serialized_file = scriptRel.to_file
 AND script.path_id = scriptRel.to_path_id
WHERE scriptRel.from_file = $file
  AND scriptRel.from_path_id = $pathId
  AND scriptRel.relation = 'monoBehaviour.script'
LIMIT 1;";
            command.Parameters.AddWithValue("$file", serializedFile ?? string.Empty);
            command.Parameters.AddWithValue("$pathId", pathId);
            var scriptName = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            return HairCustomizationScriptNames.Any(x => string.Equals(x, scriptName, StringComparison.Ordinal));
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

        private static Dictionary<string, VisualCellRendererSkinBinding> LoadVisualCellRendererSkinBindings(
            string sourceIndexPath,
            IReadOnlyCollection<VisualCellPart> parts,
            List<string> warnings)
        {
            var result = parts.ToDictionary(
                x => GetRendererKey(x.RendererFile, x.RendererPathId),
                x => new VisualCellRendererSkinBinding
                {
                    Status = string.IsNullOrWhiteSpace(sourceIndexPath) ? "sourceIndexNotProvided" : "noRendererSkinRelations",
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
                warnings.Add($"missingSourceIndexForRendererSkin:{sourceIndexPath}");
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

                    LoadRendererObjectEvidence(connection, binding);

                    using var command = connection.CreateCommand();
                    command.CommandText = @"
SELECT relation,
       to_file,
       to_path_id,
       target_count,
       raw_json
FROM source_relations
WHERE from_file = $rendererFile COLLATE NOCASE
  AND from_path_id = $rendererPathId
  AND relation IN ('component.gameObject', 'renderer.material', 'skinnedMeshRenderer.mesh', 'skinnedMeshRenderer.rootBone', 'skinnedMeshRenderer.bones')
ORDER BY relation;";
                    command.Parameters.AddWithValue("$rendererFile", part.RendererFile ?? string.Empty);
                    command.Parameters.AddWithValue("$rendererPathId", part.RendererPathId);
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var relation = reader.GetString(0);
                        var targetFile = reader.IsDBNull(1) ? null : reader.GetString(1);
                        var targetPathId = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                        var targetCount = reader.IsDBNull(3) ? (int?)null : Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture);
                        var rawJson = reader.IsDBNull(4) ? null : reader.GetString(4);
                        var details = TryReadRelationDetails(rawJson);
                        if (string.Equals(relation, "component.gameObject", StringComparison.OrdinalIgnoreCase) && targetPathId != 0)
                        {
                            binding.RendererGameObjectFile = targetFile;
                            binding.RendererGameObjectPathId = targetPathId;
                        }
                        else if (string.Equals(relation, "renderer.material", StringComparison.OrdinalIgnoreCase))
                        {
                            binding.RendererMaterialRefCount++;
                        }
                        else if (string.Equals(relation, "skinnedMeshRenderer.mesh", StringComparison.OrdinalIgnoreCase) && targetPathId != 0)
                        {
                            binding.MeshFile = targetFile;
                            binding.MeshPathId = targetPathId;
                        }
                        else if (string.Equals(relation, "skinnedMeshRenderer.rootBone", StringComparison.OrdinalIgnoreCase) && targetPathId != 0)
                        {
                            binding.RootBoneFile = targetFile;
                            binding.RootBonePathId = targetPathId;
                        }
                        else if (string.Equals(relation, "skinnedMeshRenderer.bones", StringComparison.OrdinalIgnoreCase))
                        {
                            binding.BoneCount = (int?)details?["count"] ?? targetCount;
                            binding.BonesTruncated = (bool?)details?["truncated"];
                            binding.BoneTargets = details?["targets"] as JArray;
                        }
                    }

                    binding.Status = binding.HasAnyRelation
                        ? "rendererSkinRelations"
                        : binding.RendererObjectFound
                            ? "rendererPresentWithoutSkinRelations"
                            : part.RendererPathId == 0
                                ? "missingRendererPathId"
                                : "missingRendererObject";
                }
            }
            catch (Exception ex) when (ex is IOException || ex is SqliteException || ex is JsonException || ex is InvalidDataException)
            {
                warnings.Add($"sourceIndexRendererSkinQueryFailed:{ex.Message}");
                foreach (var binding in result.Values)
                {
                    binding.Status = "sourceIndexQueryFailed";
                }
            }

            return result;
        }

        private static void AttachSkinnedMeshRendererJsonEvidence(
            string monoBehaviourJsonFolder,
            IEnumerable<VisualCellRendererSkinBinding> bindings,
            List<string> warnings)
        {
            var list = bindings?.ToList() ?? new List<VisualCellRendererSkinBinding>();
            if (list.Count == 0 || string.IsNullOrWhiteSpace(monoBehaviourJsonFolder))
            {
                return;
            }

            var dumpRoot = Directory.GetParent(Path.GetFullPath(monoBehaviourJsonFolder))?.FullName;
            if (string.IsNullOrWhiteSpace(dumpRoot))
            {
                return;
            }

            var rendererJsonFolder = Path.Combine(dumpRoot, "SkinnedMeshRenderer");
            if (!Directory.Exists(rendererJsonFolder))
            {
                return;
            }

            var byGameObject = list
                .Where(x => x.RendererGameObjectPathId.HasValue)
                .GroupBy(x => x.RendererGameObjectPathId.Value)
                .ToDictionary(x => x.Key, x => x.First());

            foreach (var jsonPath in Directory.GetFiles(rendererJsonFolder, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var json = JObject.Parse(File.ReadAllText(jsonPath));
                    var gameObjectPathId = ReadPPtrPathId(json["m_GameObject"]);
                    if (gameObjectPathId == 0 || !byGameObject.TryGetValue(gameObjectPathId, out var binding))
                    {
                        continue;
                    }

                    binding.RendererJsonPath = jsonPath;
                    binding.RendererJsonGameObjectPathId = gameObjectPathId;
                    var meshPathId = ReadPPtrPathId(json["m_Mesh"]);
                    var rootBonePathId = ReadPPtrPathId(json["m_RootBone"]);
                    binding.RendererJsonMeshPathId = meshPathId == 0 ? null : meshPathId;
                    binding.RendererJsonRootBonePathId = rootBonePathId == 0 ? null : rootBonePathId;
                    binding.RendererJsonBoneCount = (json["m_Bones"] as JArray)?.Count;
                    binding.RendererJsonMaterialCount = (json["m_Materials"] as JArray)?.Count;
                    binding.RendererJsonStatus = ResolveSkinnedMeshRendererJsonStatus(binding);
                }
                catch (Exception ex) when (ex is IOException || ex is JsonException || ex is InvalidDataException)
                {
                    warnings?.Add($"skinnedMeshRendererJsonReadFailed:{Path.GetFileName(jsonPath)}:{ex.Message}");
                }
            }
        }

        private static string ResolveSkinnedMeshRendererJsonStatus(VisualCellRendererSkinBinding binding)
        {
            if (binding == null || string.IsNullOrWhiteSpace(binding.RendererJsonPath))
            {
                return "missingSkinnedRendererJson";
            }

            var boneCount = binding.RendererJsonBoneCount ?? 0;
            if (boneCount == 0 && !binding.RendererJsonRootBonePathId.HasValue && !binding.RendererJsonMeshPathId.HasValue)
            {
                return "skinnedRendererJsonEmptyBones";
            }

            return boneCount > 0 || binding.RendererJsonRootBonePathId.HasValue || binding.RendererJsonMeshPathId.HasValue
                ? "skinnedRendererJsonSkinRefs"
                : "skinnedRendererJsonNoSkinRefs";
        }

        private static void LoadRendererObjectEvidence(SqliteConnection connection, VisualCellRendererSkinBinding binding)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT type, class_id, name
FROM source_objects INDEXED BY idx_source_objects_file_path
WHERE serialized_file = $rendererFile
  AND path_id = $rendererPathId
LIMIT 1;";
            command.Parameters.AddWithValue("$rendererFile", binding.RendererFile ?? string.Empty);
            command.Parameters.AddWithValue("$rendererPathId", binding.RendererPathId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return;
            }

            binding.RendererObjectFound = true;
            binding.RendererType = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            binding.RendererClassId = reader.IsDBNull(1) ? (int?)null : Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
            binding.RendererName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
        }

        private static JObject TryReadRelationDetails(string rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                return null;
            }

            try
            {
                return JObject.Parse(rawJson)["details"] as JObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static Dictionary<long, AvatarMeshDataRelationEvidence> LoadAvatarMeshDataRelationEvidence(
            string sourceIndexPath,
            IReadOnlyCollection<VisualCellPart> parts,
            List<string> warnings)
        {
            var result = parts
                .GroupBy(x => x.AvatarMeshPathId)
                .ToDictionary(
                x => x.Key,
                x =>
                {
                    var part = x.First();
                    return new AvatarMeshDataRelationEvidence
                    {
                        Status = string.IsNullOrWhiteSpace(sourceIndexPath) ? "sourceIndexNotProvided" : "noSourceRelations",
                        AvatarMeshFile = part.AvatarMeshFile,
                        AvatarMeshPathId = part.AvatarMeshPathId,
                    };
                });

            if (string.IsNullOrWhiteSpace(sourceIndexPath))
            {
                return result;
            }
            if (!File.Exists(sourceIndexPath))
            {
                warnings.Add($"missingSourceIndexForAvatarMeshRelations:{sourceIndexPath}");
                foreach (var item in result.Values)
                {
                    item.Status = "sourceIndexMissing";
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
                    if (!result.TryGetValue(part.AvatarMeshPathId, out var evidence))
                    {
                        continue;
                    }

                    using var command = connection.CreateCommand();
                    command.CommandText = @"
SELECT relation,
       to_file,
       to_path_id,
       target_count,
       raw_json
FROM source_relations INDEXED BY idx_source_relations_from
WHERE from_file = $avatarMeshFile COLLATE NOCASE
  AND from_path_id = $avatarMeshPathId
ORDER BY id;";
                    command.Parameters.AddWithValue("$avatarMeshFile", part.AvatarMeshFile ?? string.Empty);
                    command.Parameters.AddWithValue("$avatarMeshPathId", part.AvatarMeshPathId);
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var relation = reader.GetString(0);
                        var targetFile = reader.IsDBNull(1) ? null : reader.GetString(1);
                        var targetPathId = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                        var targetCount = reader.IsDBNull(3) ? (int?)null : Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture);
                        var rawJson = reader.IsDBNull(4) ? null : reader.GetString(4);
                        var details = TryReadRelationDetails(rawJson);
                        var path = (string)details?["path"] ?? string.Empty;

                        evidence.RelationCount++;
                        if (string.Equals(relation, "monoBehaviour.script", StringComparison.OrdinalIgnoreCase))
                        {
                            evidence.ScriptRefCount++;
                        }
                        if (string.Equals(relation, "monoBehaviour.pptr", StringComparison.OrdinalIgnoreCase))
                        {
                            evidence.PPtrRefCount++;
                            if (!string.Equals(path, "m_Script", StringComparison.OrdinalIgnoreCase))
                            {
                                evidence.NonScriptPPtrCount++;
                            }
                        }

                        if (evidence.Relations.Count < 12)
                        {
                            evidence.Relations.Add(new AvatarMeshDataRelationRef(
                                relation,
                                path,
                                targetFile,
                                targetPathId,
                                targetCount));
                        }
                    }

                    evidence.Status = evidence.NonScriptPPtrCount > 0
                        ? "hasExplicitPPtrRelations"
                        : evidence.RelationCount == evidence.ScriptRefCount && evidence.ScriptRefCount > 0
                            ? "scriptOnlyNoExplicitPPtr"
                            : evidence.RelationCount > 0
                                ? "nonPPtrRelationsOnly"
                                : "noSourceRelations";
                }
            }
            catch (Exception ex) when (ex is IOException || ex is SqliteException || ex is JsonException || ex is InvalidDataException)
            {
                warnings.Add($"sourceIndexAvatarMeshRelationQueryFailed:{ex.Message}");
                foreach (var item in result.Values)
                {
                    item.Status = "sourceIndexQueryFailed";
                }
            }

            return result;
        }

        private static Dictionary<long, AssistantHeadCollisionEvidence> LoadAssistantHeadCollisionEvidence(
            string sourceIndexPath,
            IReadOnlyCollection<VisualCellPart> parts,
            List<string> warnings)
        {
            var result = new Dictionary<long, AssistantHeadCollisionEvidence>();
            foreach (var part in parts ?? Array.Empty<VisualCellPart>())
            {
                var evidence = new AssistantHeadCollisionEvidence
                {
                    Status = "missingReference",
                    AssistantPathId = part.AssistantPathId,
                    GameObjectName = part.GameObjectName,
                    AssistantFile = NormalizeSerializedFileName(part.RendererFile),
                    AssistantJsonPath = part.AssistantJsonPath,
                };

                try
                {
                    var assistantJson = JObject.Parse(File.ReadAllText(part.AssistantJsonPath));
                    var token = assistantJson["headCollisionData"];
                    evidence.PPtrFileId = ReadPPtrFileId(token);
                    evidence.PPtrPathId = ReadPPtrPathId(token);
                    evidence.Status = evidence.HasReference ? "pptrOnly" : "missingReference";
                }
                catch (Exception ex) when (ex is IOException || ex is JsonException || ex is InvalidDataException)
                {
                    warnings?.Add($"headCollisionDataJsonReadFailed:{part.AssistantPathId}:{ex.Message}");
                    evidence.Status = "assistantJsonReadFailed";
                }

                result[part.AssistantPathId] = evidence;
            }

            if (string.IsNullOrWhiteSpace(sourceIndexPath) || !File.Exists(sourceIndexPath))
            {
                return result;
            }

            try
            {
                SQLitePCL.Batteries_V2.Init();
                using var connection = new SqliteConnection($"Data Source={sourceIndexPath};Mode=ReadOnly");
                connection.Open();
                foreach (var part in parts ?? Array.Empty<VisualCellPart>())
                {
                    if (!result.TryGetValue(part.AssistantPathId, out var evidence) || !evidence.HasReference)
                    {
                        continue;
                    }

                    using var command = connection.CreateCommand();
                    command.CommandText = @"
SELECT id, to_file, to_path_id
FROM source_relations
WHERE from_file = $file COLLATE NOCASE
  AND from_path_id = $pathId
  AND relation = 'monoBehaviour.pptr'
  AND json_extract(raw_json, '$.details.path') = 'headCollisionData'
ORDER BY id
LIMIT 1;";
                    command.Parameters.AddWithValue("$file", part.RendererFile ?? string.Empty);
                    command.Parameters.AddWithValue("$pathId", part.AssistantPathId);
                    using var reader = command.ExecuteReader();
                    if (reader.Read())
                    {
                        evidence.RelationId = reader.GetInt64(0);
                        evidence.TargetFile = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                        evidence.TargetPathId = reader.IsDBNull(2) ? evidence.PPtrPathId : reader.GetInt64(2);
                        LoadHeadCollisionTargetObject(connection, evidence);
                        evidence.Status = evidence.TargetObjectFound
                            ? "targetObjectResolved"
                            : "externalPPtrUnresolved";
                    }
                    else
                    {
                        evidence.Status = "sourceRelationMissing";
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is SqliteException || ex is InvalidDataException)
            {
                warnings?.Add($"headCollisionDataQueryFailed:{ex.Message}");
                foreach (var evidence in result.Values.Where(x => x.HasReference))
                {
                    evidence.Status = "sourceIndexQueryFailed";
                }
            }

            return result;
        }

        private static void LoadHeadCollisionTargetObject(SqliteConnection connection, AssistantHeadCollisionEvidence evidence)
        {
            if (connection == null || evidence == null || string.IsNullOrWhiteSpace(evidence.TargetFile) || evidence.TargetPathId == 0)
            {
                return;
            }

            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT type, class_id, name
FROM source_objects INDEXED BY idx_source_objects_file_path
WHERE serialized_file = $file
  AND path_id = $pathId
LIMIT 1;";
            command.Parameters.AddWithValue("$file", NormalizeSerializedFileName(evidence.TargetFile));
            command.Parameters.AddWithValue("$pathId", evidence.TargetPathId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return;
            }

            evidence.TargetObjectFound = true;
            evidence.TargetType = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            evidence.TargetClassId = reader.IsDBNull(1) ? (int?)null : Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
            evidence.TargetName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
        }

        private static Dictionary<long, List<AvatarPartDataEvidence>> LoadAvatarPartDataEvidence(
            string jsonFolder,
            IReadOnlyDictionary<long, ManifestEntry> manifest,
            IReadOnlyCollection<VisualCellPart> parts,
            string sourceIndexPath,
            List<string> warnings)
        {
            var result = parts.ToDictionary(
                x => x.AvatarMeshPathId,
                _ => new List<AvatarPartDataEvidence>());
            if (result.Count == 0 || manifest.Count == 0)
            {
                return result;
            }

            SqliteConnection connection = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(sourceIndexPath) && File.Exists(sourceIndexPath))
                {
                    SQLitePCL.Batteries_V2.Init();
                    connection = new SqliteConnection($"Data Source={sourceIndexPath};Mode=ReadOnly");
                    connection.Open();
                }
                else if (!string.IsNullOrWhiteSpace(sourceIndexPath))
                {
                    warnings.Add($"missingSourceIndexForAvatarPartData:{sourceIndexPath}");
                }

                foreach (var item in manifest)
                {
                    var pathId = item.Key;
                    var entry = item.Value;
                    if (string.IsNullOrWhiteSpace(entry.JsonPath) || !File.Exists(entry.JsonPath))
                    {
                        continue;
                    }

                    JObject json;
                    try
                    {
                        json = JObject.Parse(File.ReadAllText(entry.JsonPath));
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    var meshData = json["m_MeshData"] as JArray;
                    if (meshData == null || meshData.Count == 0 || json["m_VertexData"] != null)
                    {
                        continue;
                    }

                    var scriptName = connection != null
                        ? ResolveMonoScriptName(connection, entry.SerializedFile, pathId)
                        : string.Empty;
                    var scriptVerified = string.Equals(scriptName, "AvatarPartDataAsset", StringComparison.Ordinal);
                    if (!scriptVerified && !string.IsNullOrWhiteSpace(scriptName))
                    {
                        continue;
                    }

                    var assetName = (string)json["m_Name"] ?? Path.GetFileNameWithoutExtension(entry.JsonPath);
                    for (var index = 0; index < meshData.Count; index++)
                    {
                        var meshPathId = ReadPPtrPathId(meshData[index]);
                        if (meshPathId == 0 || !result.TryGetValue(meshPathId, out var evidences))
                        {
                            continue;
                        }

                        evidences.Add(new AvatarPartDataEvidence
                        {
                            Status = scriptVerified ? "avatarPartDataMeshOrder" : "avatarPartDataMeshOrderScriptUnverified",
                            AssetName = assetName,
                            AssetJsonPath = entry.JsonPath,
                            AssetFile = entry.SerializedFile,
                            AssetPathId = pathId,
                            ScriptName = scriptName,
                            MeshDataIndex = index,
                            MeshDataCount = meshData.Count,
                            MeshDataPathId = meshPathId,
                        });
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is SqliteException || ex is JsonException || ex is InvalidDataException)
            {
                warnings.Add($"avatarPartDataEvidenceQueryFailed:{ex.Message}");
            }
            finally
            {
                connection?.Dispose();
            }

            return result;
        }

        private static string ResolveMonoScriptName(SqliteConnection connection, string serializedFile, long pathId)
        {
            foreach (var file in new[] { NormalizeSerializedFileName(serializedFile), serializedFile }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT script.name
FROM source_relations scriptRel INDEXED BY idx_source_relations_from
JOIN source_objects script
  ON script.serialized_file = scriptRel.to_file
 AND script.path_id = scriptRel.to_path_id
WHERE scriptRel.from_file = $file COLLATE NOCASE
  AND scriptRel.from_path_id = $pathId
  AND scriptRel.relation = 'monoBehaviour.script'
LIMIT 1;";
                command.Parameters.AddWithValue("$file", file ?? string.Empty);
                command.Parameters.AddWithValue("$pathId", pathId);
                var value = command.ExecuteScalar() as string;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        private static IReadOnlyList<BoneDriverHint> LoadBoneDriverHints(
            string jsonFolder,
            IReadOnlyDictionary<long, ManifestEntry> manifest,
            string sourceIndexPath,
            List<string> warnings)
        {
            var result = new List<BoneDriverHint>();
            if (string.IsNullOrWhiteSpace(jsonFolder) || !Directory.Exists(jsonFolder))
            {
                return result;
            }

            var manifestByJsonPath = manifest.Values
                .Where(x => !string.IsNullOrWhiteSpace(x.JsonPath))
                .GroupBy(x => Path.GetFullPath(x.JsonPath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

            SqliteConnection connection = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(sourceIndexPath) && File.Exists(sourceIndexPath))
                {
                    SQLitePCL.Batteries_V2.Init();
                    connection = new SqliteConnection($"Data Source={sourceIndexPath};Mode=ReadOnly");
                    connection.Open();
                }

                var pathIdByJsonPath = manifest
                    .Where(x => !string.IsNullOrWhiteSpace(x.Value.JsonPath))
                    .GroupBy(x => Path.GetFullPath(x.Value.JsonPath), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.First().Key, StringComparer.OrdinalIgnoreCase);

                foreach (var jsonPath in Directory.GetFiles(jsonFolder, "*.json", SearchOption.TopDirectoryOnly))
                {
                    JObject json;
                    try
                    {
                        json = JObject.Parse(File.ReadAllText(jsonPath));
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    var driverName = (string)json["driverSerializeName"];
                    var aimName = (string)json["aimSerializeName"];
                    var aim1Name = (string)json["aim1SerializeName"];
                    var aim2Name = (string)json["aim2SerializeName"];
                    if (string.IsNullOrWhiteSpace(driverName)
                        && string.IsNullOrWhiteSpace(aimName)
                        && string.IsNullOrWhiteSpace(aim1Name)
                        && string.IsNullOrWhiteSpace(aim2Name))
                    {
                        continue;
                    }

                    var fullJsonPath = Path.GetFullPath(jsonPath);
                    manifestByJsonPath.TryGetValue(fullJsonPath, out var entry);
                    pathIdByJsonPath.TryGetValue(fullJsonPath, out var pathId);
                    var scriptName = string.Empty;
                    var gameObjectPathId = ReadPPtrPathId(json["m_GameObject"]);
                    var gameObjectName = string.Empty;
                    var transformPath = string.Empty;
                    var transformPathStatus = "sourceIndexNotProvided";
                    if (connection != null && entry != null && pathId != 0)
                    {
                        scriptName = ResolveMonoScriptName(connection, entry.SerializedFile, pathId);
                        var gameObjectPath = ResolveGameObjectTransformPath(connection, entry.SerializedFile, gameObjectPathId);
                        gameObjectName = gameObjectPath.GameObjectName;
                        transformPath = gameObjectPath.TransformPath;
                        transformPathStatus = gameObjectPath.Status;
                    }
                    if (string.IsNullOrWhiteSpace(scriptName))
                    {
                        scriptName = !string.IsNullOrWhiteSpace(driverName) ? "BoneFollowDriver" : "BoneHairFollowDriver";
                    }

                    result.Add(new BoneDriverHint
                    {
                        Status = "boneNameHintOnly",
                        ScriptName = scriptName,
                        SourceJson = jsonPath,
                        SerializedFile = entry?.SerializedFile ?? string.Empty,
                        PathId = pathId,
                        GameObjectPathId = gameObjectPathId,
                        GameObjectName = gameObjectName,
                        TransformPath = transformPath,
                        TransformPathStatus = transformPathStatus,
                        DriverSerializeName = driverName ?? string.Empty,
                        AimSerializeName = aimName ?? string.Empty,
                        Aim1SerializeName = aim1Name ?? string.Empty,
                        Aim2SerializeName = aim2Name ?? string.Empty,
                    });
                }
            }
            catch (Exception ex) when (ex is IOException || ex is SqliteException || ex is JsonException || ex is InvalidDataException)
            {
                warnings.Add($"boneDriverHintLoadFailed:{ex.Message}");
            }
            finally
            {
                connection?.Dispose();
            }

            return result
                .OrderBy(x => x.ScriptName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.PathId)
                .ToArray();
        }

        private static GameObjectTransformPath ResolveGameObjectTransformPath(SqliteConnection connection, string serializedFile, long gameObjectPathId)
        {
            if (connection == null || string.IsNullOrWhiteSpace(serializedFile) || gameObjectPathId == 0)
            {
                return new GameObjectTransformPath(string.Empty, string.Empty, "missingGameObject");
            }

            var file = NormalizeSerializedFileName(serializedFile);
            var gameObjectName = ResolveObjectName(connection, file, gameObjectPathId);
            var transformPathId = ResolveTransformForGameObject(connection, file, gameObjectPathId);
            if (transformPathId == 0)
            {
                return new GameObjectTransformPath(gameObjectName, gameObjectName, "missingTransform");
            }

            var names = new List<string>();
            var visited = new HashSet<long>();
            var current = transformPathId;
            for (var depth = 0; depth < 64 && current != 0 && visited.Add(current); depth++)
            {
                var currentGameObjectId = ResolveGameObjectForComponent(connection, file, current);
                var currentName = ResolveObjectName(connection, file, currentGameObjectId);
                if (!string.IsNullOrWhiteSpace(currentName))
                {
                    names.Add(currentName);
                }

                current = ResolveParentTransform(connection, file, current);
            }

            names.Reverse();
            return new GameObjectTransformPath(
                gameObjectName,
                names.Count > 0 ? string.Join("/", names) : gameObjectName,
                names.Count > 0 ? "resolved" : "missingTransformPath");
        }

        private static long ResolveTransformForGameObject(SqliteConnection connection, string serializedFile, long gameObjectPathId)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT rel.to_path_id
FROM source_relations rel INDEXED BY idx_source_relations_from
JOIN source_objects obj INDEXED BY idx_source_objects_file_path
  ON obj.serialized_file = rel.to_file
 AND obj.path_id = rel.to_path_id
WHERE rel.from_path_id = $pathId
  AND rel.relation = 'gameObject.component'
  AND rel.from_file = $file
  AND obj.type = 'Transform'
LIMIT 1;";
            command.Parameters.AddWithValue("$file", serializedFile ?? string.Empty);
            command.Parameters.AddWithValue("$pathId", gameObjectPathId);
            return ToInt64(command.ExecuteScalar());
        }

        private static long ResolveGameObjectForComponent(SqliteConnection connection, string serializedFile, long componentPathId)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT rel.to_path_id
FROM source_relations rel INDEXED BY idx_source_relations_from
WHERE rel.from_path_id = $pathId
  AND rel.relation = 'component.gameObject'
  AND rel.from_file = $file
LIMIT 1;";
            command.Parameters.AddWithValue("$file", serializedFile ?? string.Empty);
            command.Parameters.AddWithValue("$pathId", componentPathId);
            return ToInt64(command.ExecuteScalar());
        }

        private static long ResolveParentTransform(SqliteConnection connection, string serializedFile, long transformPathId)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT rel.to_path_id
FROM source_relations rel INDEXED BY idx_source_relations_from
WHERE rel.from_path_id = $pathId
  AND rel.relation = 'transform.parent'
  AND rel.from_file = $file
LIMIT 1;";
            command.Parameters.AddWithValue("$file", serializedFile ?? string.Empty);
            command.Parameters.AddWithValue("$pathId", transformPathId);
            return ToInt64(command.ExecuteScalar());
        }

        private static string ResolveObjectName(SqliteConnection connection, string serializedFile, long pathId)
        {
            if (pathId == 0)
            {
                return string.Empty;
            }

            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT name
FROM source_objects INDEXED BY idx_source_objects_file_path
WHERE serialized_file = $file
  AND path_id = $pathId
LIMIT 1;";
            command.Parameters.AddWithValue("$file", serializedFile ?? string.Empty);
            command.Parameters.AddWithValue("$pathId", pathId);
            return command.ExecuteScalar() as string ?? string.Empty;
        }

        private static long ToInt64(object value)
        {
            if (value == null || value == DBNull.Value)
            {
                return 0;
            }

            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
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

        private static VisualCellJson TryLoadVisualCell(IEnumerable<string> jsonFiles)
        {
            foreach (var jsonFile in jsonFiles)
            {
                try
                {
                    var json = JObject.Parse(File.ReadAllText(jsonFile));
                    if (json["lod0RendererAssistants"] is JArray)
                    {
                        return new VisualCellJson(json, jsonFile);
                    }
                }
                catch (JsonException)
                {
                    // 目录里只应该有 TypeTree JSON；坏文件留给后续读取时报错。
                }
            }

            return null;
        }

        private static SelectedVisualCellInfo ResolveSelectedVisualCellInfo(
            VisualCellJson visualCellSource,
            IReadOnlyDictionary<long, ManifestEntry> manifest,
            string sourceIndexPath,
            List<string> warnings)
        {
            long? pathId = null;
            var serializedFile = string.Empty;
            foreach (var item in manifest ?? new Dictionary<long, ManifestEntry>())
            {
                if (PathsEqual(item.Value.JsonPath, visualCellSource.JsonPath))
                {
                    pathId = item.Key;
                    serializedFile = NormalizeSerializedFileName(item.Value.SerializedFile);
                    break;
                }
            }

            var gameObjectPathId = ReadPPtrPathId(visualCellSource.Json["m_GameObject"]);
            var gameObjectName = string.Empty;
            long? transformPathId = null;
            var transformPath = string.Empty;
            var transformPathStatus = "sourceIndexUnavailable";
            var directChildNames = Array.Empty<string>();
            int? transformNodeCount = null;

            if (pathId.HasValue
                && !string.IsNullOrWhiteSpace(serializedFile)
                && !string.IsNullOrWhiteSpace(sourceIndexPath)
                && File.Exists(sourceIndexPath))
            {
                try
                {
                    SQLitePCL.Batteries_V2.Init();
                    using var connection = new SqliteConnection($"Data Source={sourceIndexPath};Mode=ReadOnly");
                    connection.Open();
                    transformNodeCount = CountTransformNodes(connection, serializedFile, pathId.Value);
                    if (gameObjectPathId != 0)
                    {
                        gameObjectName = ResolveObjectName(connection, serializedFile, gameObjectPathId);
                        var transformInfo = ResolveGameObjectTransformPath(connection, serializedFile, gameObjectPathId);
                        transformPath = transformInfo.TransformPath;
                        transformPathStatus = transformInfo.Status;
                        var resolvedTransformPathId = ResolveTransformForGameObject(connection, serializedFile, gameObjectPathId);
                        if (resolvedTransformPathId != 0)
                        {
                            transformPathId = resolvedTransformPathId;
                            directChildNames = LoadDirectChildGameObjectNames(connection, serializedFile, resolvedTransformPathId, maxCount: 64);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is SqliteException || ex is InvalidDataException)
                {
                    warnings.Add($"selectedVisualCellQueryFailed:{ex.Message}");
                }
            }

            return new SelectedVisualCellInfo(
                pathId,
                serializedFile,
                gameObjectPathId == 0 ? null : gameObjectPathId,
                gameObjectName,
                transformPathId,
                transformPath,
                transformPathStatus,
                directChildNames.Length,
                directChildNames,
                transformNodeCount);
        }

        private static string[] LoadDirectChildGameObjectNames(
            SqliteConnection connection,
            string serializedFile,
            long transformPathId,
            int maxCount)
        {
            var result = new List<string>();
            if (connection == null || string.IsNullOrWhiteSpace(serializedFile) || transformPathId == 0)
            {
                return result.ToArray();
            }

            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT go.name
FROM source_relations rel INDEXED BY idx_source_relations_to
LEFT JOIN source_relations cg
  ON cg.from_file = rel.from_file
 AND cg.from_path_id = rel.from_path_id
 AND cg.relation = 'component.gameObject'
LEFT JOIN source_objects go
  ON go.serialized_file = cg.to_file
 AND go.path_id = cg.to_path_id
WHERE rel.to_file = $file
  AND rel.to_path_id = $pathId
  AND rel.relation = 'transform.parent'
ORDER BY go.name
LIMIT $limit;";
            command.Parameters.AddWithValue("$file", NormalizeSerializedFileName(serializedFile));
            command.Parameters.AddWithValue("$pathId", transformPathId);
            command.Parameters.AddWithValue("$limit", Math.Max(1, maxCount));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    result.Add(name);
                }
            }

            return result
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static SelectedVisualCellChildComponentEvidence LoadSelectedVisualCellChildComponentEvidence(
            SelectedVisualCellInfo selectedVisualCell,
            string sourceIndexPath,
            List<string> warnings)
        {
            var evidence = new SelectedVisualCellChildComponentEvidence
            {
                Status = string.IsNullOrWhiteSpace(sourceIndexPath) ? "sourceIndexNotProvided" : "missingSelectedTransform",
                SerializedFile = selectedVisualCell?.SerializedFile,
                ParentTransformPathId = selectedVisualCell?.TransformPathId,
            };

            if (selectedVisualCell == null || !selectedVisualCell.TransformPathId.HasValue)
            {
                return evidence;
            }
            if (string.IsNullOrWhiteSpace(sourceIndexPath))
            {
                evidence.Status = "sourceIndexNotProvided";
                return evidence;
            }
            if (!File.Exists(sourceIndexPath))
            {
                evidence.Status = "sourceIndexMissing";
                warnings?.Add($"missingSourceIndexForSelectedChildComponents:{sourceIndexPath}");
                return evidence;
            }
            if (string.IsNullOrWhiteSpace(selectedVisualCell.SerializedFile))
            {
                evidence.Status = "missingSelectedVisualCellFile";
                return evidence;
            }

            try
            {
                SQLitePCL.Batteries_V2.Init();
                using var connection = new SqliteConnection($"Data Source={sourceIndexPath};Mode=ReadOnly");
                connection.Open();
                var file = NormalizeSerializedFileName(selectedVisualCell.SerializedFile);
                evidence.SerializedFile = file;
                evidence.Children = LoadSelectedVisualCellChildComponents(connection, file, selectedVisualCell.TransformPathId.Value, maxCount: 64);
                evidence.Status = ResolveSelectedVisualCellChildComponentStatus(evidence);
            }
            catch (Exception ex) when (ex is IOException || ex is SqliteException || ex is InvalidDataException)
            {
                warnings?.Add($"selectedVisualCellChildComponentQueryFailed:{ex.Message}");
                evidence.Status = "sourceIndexQueryFailed";
            }

            return evidence;
        }

        private static IReadOnlyList<SelectedVisualCellChildGameObject> LoadSelectedVisualCellChildComponents(
            SqliteConnection connection,
            string serializedFile,
            long parentTransformPathId,
            int maxCount)
        {
            var childTransformIds = new List<long>();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT rel.from_path_id
FROM source_relations rel INDEXED BY idx_source_relations_to
WHERE rel.to_file = $file
  AND rel.to_path_id = $pathId
  AND rel.relation = 'transform.parent'
ORDER BY rel.from_path_id
LIMIT $limit;";
            command.Parameters.AddWithValue("$file", NormalizeSerializedFileName(serializedFile));
            command.Parameters.AddWithValue("$pathId", parentTransformPathId);
            command.Parameters.AddWithValue("$limit", Math.Max(1, maxCount));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                childTransformIds.Add(reader.GetInt64(0));
            }

            var children = new List<SelectedVisualCellChildGameObject>();
            foreach (var transformPathId in childTransformIds)
            {
                var gameObjectPathId = ResolveGameObjectForComponent(connection, serializedFile, transformPathId);
                var gameObjectName = ResolveObjectName(connection, serializedFile, gameObjectPathId);
                children.Add(new SelectedVisualCellChildGameObject
                {
                    TransformPathId = transformPathId,
                    GameObjectPathId = gameObjectPathId == 0 ? null : gameObjectPathId,
                    GameObjectName = gameObjectName,
                    Components = LoadGameObjectComponents(connection, serializedFile, gameObjectPathId),
                });
            }

            return children
                .OrderBy(x => x.GameObjectName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.TransformPathId)
                .ToArray();
        }

        private static IReadOnlyList<SelectedVisualCellComponent> LoadGameObjectComponents(
            SqliteConnection connection,
            string serializedFile,
            long gameObjectPathId)
        {
            var components = new List<SelectedVisualCellComponent>();
            if (gameObjectPathId == 0)
            {
                return components;
            }

            var rows = new List<(long PathId, string Type, int? ClassId, string Name)>();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT rel.to_path_id,
       obj.type,
       obj.class_id,
       obj.name
FROM source_relations rel INDEXED BY idx_source_relations_from
LEFT JOIN source_objects obj INDEXED BY idx_source_objects_file_path
  ON obj.serialized_file = rel.to_file
 AND obj.path_id = rel.to_path_id
WHERE rel.from_file = $file
  AND rel.from_path_id = $pathId
  AND rel.relation = 'gameObject.component'
ORDER BY CASE obj.type
    WHEN 'Transform' THEN 0
    WHEN 'SkinnedMeshRenderer' THEN 1
    WHEN 'MeshRenderer' THEN 2
    WHEN 'MonoBehaviour' THEN 3
    ELSE 4
  END,
  rel.to_path_id;";
            command.Parameters.AddWithValue("$file", NormalizeSerializedFileName(serializedFile));
            command.Parameters.AddWithValue("$pathId", gameObjectPathId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var pathId = reader.GetInt64(0);
                var type = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var classId = reader.IsDBNull(2) ? (int?)null : Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture);
                var name = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                rows.Add((pathId, type, classId, name));
            }

            foreach (var row in rows)
            {
                components.Add(new SelectedVisualCellComponent
                {
                    PathId = row.PathId,
                    Type = row.Type,
                    ClassId = row.ClassId,
                    Name = row.Name,
                    ScriptName = string.Equals(row.Type, "MonoBehaviour", StringComparison.OrdinalIgnoreCase)
                        ? ResolveMonoBehaviourScriptName(connection, serializedFile, row.PathId)
                        : string.Empty,
                });
            }

            return components;
        }

        private static string ResolveMonoBehaviourScriptName(SqliteConnection connection, string serializedFile, long monoBehaviourPathId)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT script.name
FROM source_relations rel INDEXED BY idx_source_relations_from
LEFT JOIN source_objects script INDEXED BY idx_source_objects_file_path
  ON script.serialized_file = rel.to_file
 AND script.path_id = rel.to_path_id
WHERE rel.from_file = $file
  AND rel.from_path_id = $pathId
  AND rel.relation = 'monoBehaviour.script'
LIMIT 1;";
            command.Parameters.AddWithValue("$file", NormalizeSerializedFileName(serializedFile));
            command.Parameters.AddWithValue("$pathId", monoBehaviourPathId);
            return command.ExecuteScalar() as string ?? string.Empty;
        }

        private static string ResolveSelectedVisualCellChildComponentStatus(SelectedVisualCellChildComponentEvidence evidence)
        {
            if (evidence == null)
            {
                return "missing";
            }

            var children = evidence.Children ?? Array.Empty<SelectedVisualCellChildGameObject>();
            if (children.Count == 0)
            {
                return "selectedVisualCellHasNoChildObjects";
            }

            var hasSkinnedMeshRenderer = evidence.SkinnedMeshRendererCount > 0;
            var scripts = new HashSet<string>(evidence.MonoBehaviourScriptNames, StringComparer.OrdinalIgnoreCase);
            var hasOnlyKnownRendererScripts = scripts.All(x =>
                string.Equals(x, "LXRendererAssistant", StringComparison.OrdinalIgnoreCase)
                || string.Equals(x, "CustomTags", StringComparison.OrdinalIgnoreCase));
            var hasOnlyKnownComponents = children
                .SelectMany(x => x.Components ?? Array.Empty<SelectedVisualCellComponent>())
                .All(x =>
                    string.Equals(x.Type, "Transform", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x.Type, "SkinnedMeshRenderer", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x.Type, "MonoBehaviour", StringComparison.OrdinalIgnoreCase));

            if (hasSkinnedMeshRenderer && hasOnlyKnownComponents && hasOnlyKnownRendererScripts)
            {
                return "childComponentsRendererAssistantAndTagsOnly";
            }

            return scripts.Count > 0
                ? "childComponentsContainCustomScripts"
                : "childComponentsCollected";
        }

        private static VisualCellRendererListEvidence LoadVisualCellRendererListEvidence(
            JObject visualCell,
            string serializedFile,
            string sourceIndexPath,
            List<string> warnings)
        {
            var rendererAssistantIds = ReadPPtrPathIds(visualCell?["rendererAssistants"] as JArray).ToArray();
            var avatarRendererIds = ReadPPtrPathIds(visualCell?["avatarRenderers"] as JArray).ToArray();
            var meshRendererIds = ReadPPtrPathIds(visualCell?["meshRenderer"] as JArray).ToArray();
            var lodCounts = new LodRendererAssistantCounts
            {
                Lod0 = ReadPPtrPathIds(visualCell?["lod0RendererAssistants"] as JArray).Count(),
                Lod1 = ReadPPtrPathIds(visualCell?["lod1RendererAssistants"] as JArray).Count(),
                Lod2 = ReadPPtrPathIds(visualCell?["lod2RendererAssistants"] as JArray).Count(),
                Lod3 = ReadPPtrPathIds(visualCell?["lod3RendererAssistants"] as JArray).Count(),
                Other = ReadPPtrPathIds(visualCell?["nLodRendererAssistants"] as JArray).Count(),
            };

            var evidence = new VisualCellRendererListEvidence
            {
                RendererAssistantCount = rendererAssistantIds.Length,
                AvatarRendererCount = avatarRendererIds.Length,
                MeshRendererCount = meshRendererIds.Length,
                LodRendererAssistantCounts = lodCounts,
                AvatarRendererMatchesRendererAssistants = rendererAssistantIds.SequenceEqual(avatarRendererIds),
                Status = "countsOnly",
            };

            if (string.IsNullOrWhiteSpace(sourceIndexPath) || !File.Exists(sourceIndexPath) || string.IsNullOrWhiteSpace(serializedFile))
            {
                evidence.Status = string.IsNullOrWhiteSpace(sourceIndexPath) ? "sourceIndexNotProvided" : "sourceIndexUnavailable";
                return evidence;
            }

            try
            {
                SQLitePCL.Batteries_V2.Init();
                using var connection = new SqliteConnection($"Data Source={sourceIndexPath};Mode=ReadOnly");
                connection.Open();
                var file = NormalizeSerializedFileName(serializedFile);
                evidence.RendererAssistantGameObjectNames = LoadComponentGameObjectNames(connection, file, rendererAssistantIds);
                evidence.AvatarRendererGameObjectNames = LoadComponentGameObjectNames(connection, file, avatarRendererIds);
                evidence.MeshRendererGameObjectNames = LoadComponentGameObjectNames(connection, file, meshRendererIds);
                evidence.SkinnedMeshRendererRefCount = CountObjectsOfType(connection, file, meshRendererIds, "SkinnedMeshRenderer");
                evidence.Status = ResolveVisualCellRendererListStatus(evidence);
            }
            catch (Exception ex) when (ex is IOException || ex is SqliteException || ex is InvalidDataException)
            {
                warnings?.Add($"visualCellRendererListQueryFailed:{ex.Message}");
                evidence.Status = "sourceIndexQueryFailed";
            }

            return evidence;
        }

        private static string ResolveVisualCellRendererListStatus(VisualCellRendererListEvidence evidence)
        {
            if (evidence == null)
            {
                return "missing";
            }

            if (evidence.RendererAssistantCount == 0 && evidence.MeshRendererCount == 0)
            {
                return "missingRendererLists";
            }

            if (evidence.RendererAssistantCount != evidence.MeshRendererCount)
            {
                return "assistantMeshRendererCountMismatch";
            }

            if (evidence.SkinnedMeshRendererRefCount != evidence.MeshRendererCount)
            {
                return "meshRendererTypeMismatch";
            }

            var assistantNames = evidence.RendererAssistantGameObjectNames ?? Array.Empty<string>();
            var meshNames = evidence.MeshRendererGameObjectNames ?? Array.Empty<string>();
            if (assistantNames.Count == 0 || meshNames.Count == 0)
            {
                return "missingRendererGameObjectNames";
            }

            if (assistantNames.SequenceEqual(meshNames, StringComparer.OrdinalIgnoreCase))
            {
                return evidence.AvatarRendererMatchesRendererAssistants
                    ? "assistantMeshRendererGameObjectsAligned"
                    : "avatarRendererListDiffers";
            }

            var assistantSet = new HashSet<string>(assistantNames, StringComparer.OrdinalIgnoreCase);
            var meshSet = new HashSet<string>(meshNames, StringComparer.OrdinalIgnoreCase);
            return assistantSet.SetEquals(meshSet)
                ? "assistantMeshRendererGameObjectsSameSetDifferentOrder"
                : "assistantMeshRendererGameObjectMismatch";
        }

        private static string[] LoadComponentGameObjectNames(SqliteConnection connection, string serializedFile, IEnumerable<long> componentPathIds)
        {
            var result = new List<string>();
            foreach (var componentPathId in componentPathIds ?? Array.Empty<long>())
            {
                var gameObjectPathId = ResolveGameObjectForComponent(connection, serializedFile, componentPathId);
                result.Add(ResolveObjectName(connection, serializedFile, gameObjectPathId));
            }

            return result.ToArray();
        }

        private static int CountObjectsOfType(SqliteConnection connection, string serializedFile, IEnumerable<long> pathIds, string objectType)
        {
            var count = 0;
            foreach (var pathId in pathIds ?? Array.Empty<long>())
            {
                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT type
FROM source_objects INDEXED BY idx_source_objects_file_path
WHERE serialized_file = $file
  AND path_id = $pathId
LIMIT 1;";
                command.Parameters.AddWithValue("$file", NormalizeSerializedFileName(serializedFile));
                command.Parameters.AddWithValue("$pathId", pathId);
                var type = command.ExecuteScalar() as string;
                if (string.Equals(type, objectType, StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }

            return count;
        }

        private static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            try
            {
                return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
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

            var transformMatch = Regex.Match(name, @"^Transform#(?<id>\d+)$", RegexOptions.IgnoreCase);
            if (transformMatch.Success)
            {
                return "Transform_" + transformMatch.Groups["id"].Value + ".json";
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

        private static int ReadPPtrFileId(JToken token)
        {
            if (token == null)
            {
                return 0;
            }

            return token["m_FileID"]?.Value<int>()
                ?? token["fileId"]?.Value<int>()
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

        private static (Vector3Value Min, Vector3Value Max) CalculateBounds(IReadOnlyCollection<Vector3Value> values)
        {
            return (
                new Vector3Value(
                    values.Min(x => x.X),
                    values.Min(x => x.Y),
                    values.Min(x => x.Z)),
                new Vector3Value(
                    values.Max(x => x.X),
                    values.Max(x => x.Y),
                    values.Max(x => x.Z)));
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
            result.HairDeform = ReadHairDeformSummary(json, result.Positions.Count, result.Warnings);
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
            summary.BindPoseMatrix = SummarizeBindPoseMatrices(json["m_BindPoses"] as JArray);
            if (summary.BindPoseMatrix.NonFiniteCount > 0)
            {
                warnings.Add("bindPoseMatrixNonFinite");
                summary.LayoutWarnings.Add("bindPoseMatrixNonFinite");
            }
            if (summary.BindPoseMatrix.NonAffineCount > 0)
            {
                warnings.Add("bindPoseMatrixNonAffine");
                summary.LayoutWarnings.Add("bindPoseMatrixNonAffine");
            }

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
                summary.AvatarBoneOffsetPairCount = offsets?.Count / 2 ?? 0;
                summary.AvatarBoneOffsetPairCountMatchesVertexCount = offsets != null && offsets.Count == vertexCount * 2;
                if (offsets == null || avatarWeights == null)
                {
                    warnings.Add("avatarBoneWeightLayoutIncomplete");
                    summary.LayoutWarnings.Add("avatarBoneWeightLayoutIncomplete");
                    summary.AvatarBoneOffsetPairStatus = "incomplete";
                }
                else if (offsets.Count != vertexCount * 2)
                {
                    warnings.Add("avatarBoneOffsetCountMismatch");
                    summary.LayoutWarnings.Add("avatarBoneOffsetCountMismatch");
                    summary.AvatarBoneOffsetPairStatus = "offsetCountMismatch";
                }
                else
                {
                    var avatarBones = new SortedSet<int>();
                    var avatarBoneRefCounts = new Dictionary<int, int>();
                    var negativeBoneRefs = 0;
                    var verticesWithNegativeBoneRefs = 0;
                    var maxInfluences = 0;
                    var invalidRanges = 0;
                    var weightedVertices = 0;
                    var expectedOffset = 0;
                    var continuousPairs = 0;
                    var gapOrOverlapPairs = 0;
                    for (var vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
                    {
                        var offset = offsets[vertexIndex * 2].Value<int>();
                        var count = offsets[vertexIndex * 2 + 1].Value<int>();
                        var vertexInfluences = new List<VertexSkinInfluence>();
                        maxInfluences = Math.Max(maxInfluences, count);
                        if (offset == expectedOffset)
                        {
                            continuousPairs++;
                        }
                        else
                        {
                            gapOrOverlapPairs++;
                        }
                        expectedOffset = offset + count;
                        if (offset < 0 || count < 0 || offset + count > avatarWeights.Count)
                        {
                            invalidRanges++;
                            summary.VertexAvatarInfluences.Add(Array.Empty<VertexSkinInfluence>());
                            continue;
                        }

                        var hasWeight = false;
                        var hasNegativeBoneRef = false;
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
                            vertexInfluences.Add(new VertexSkinInfluence(boneIndex, value));
                            avatarBoneRefCounts[boneIndex] = avatarBoneRefCounts.TryGetValue(boneIndex, out var oldCount)
                                ? oldCount + 1
                                : 1;
                            if (boneIndex < 0)
                            {
                                negativeBoneRefs++;
                                hasNegativeBoneRef = true;
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
                        summary.VertexAvatarInfluences.Add(vertexInfluences
                            .OrderByDescending(x => x.Weight)
                            .ThenBy(x => x.BoneIndex)
                            .ToArray());
                        if (hasNegativeBoneRef)
                        {
                            verticesWithNegativeBoneRefs++;
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
                    summary.AvatarVerticesWithNegativeBoneRefCount = verticesWithNegativeBoneRefs;
                    summary.AvatarMaxInfluenceCount = maxInfluences;
                    summary.AvatarInvalidRangeCount = invalidRanges;
                    summary.AvatarBoneOffsetContinuousPairCount = continuousPairs;
                    summary.AvatarBoneOffsetGapOrOverlapCount = gapOrOverlapPairs;
                    summary.AvatarBoneOffsetLastRangeEnd = expectedOffset;
                    summary.AvatarBoneOffsetPairStatus = invalidRanges > 0
                        ? "invalidRanges"
                        : gapOrOverlapPairs > 0
                            ? "nonContinuousRanges"
                            : expectedOffset == avatarWeights.Count
                                ? "continuousCoversWeights"
                                : "continuousDoesNotCoverAllWeights";
                    summary.AvatarBoneRefs.AddRange(avatarBoneRefCounts
                        .Where(x => x.Key >= 0)
                        .OrderBy(x => x.Key)
                        .Select(x => new BoneRefCount(x.Key, x.Value)));
                    summary.AvatarTopBoneRefs.AddRange(avatarBoneRefCounts
                        .OrderByDescending(x => x.Value)
                        .ThenBy(x => x.Key)
                        .Take(16)
                        .Select(x => new BoneRefCount(x.Key, x.Value)));
                }
            }

            summary.Status = summary.HasAnySkinField
                ? "presentUnmapped"
                : "absent";
            summary.BindingStatus = summary.HasAnySkinField
                ? "missingJointMapping"
                : "noSourceSkinData";
            summary.AnimSkinBindPoseSlotStatus = ResolveAnimSkinBindPoseSlotStatus(summary);
            summary.AnimSkinIndicesFitBindPoseSlots = string.Equals(
                summary.AnimSkinBindPoseSlotStatus,
                "animSkinBoneIndicesWithinBindPoseRange",
                StringComparison.OrdinalIgnoreCase);
            return summary;
        }

        private static BindPoseMatrixSummary SummarizeBindPoseMatrices(JArray bindPoses)
        {
            var summary = new BindPoseMatrixSummary
            {
                Count = bindPoses?.Count ?? 0,
            };
            if (bindPoses == null || bindPoses.Count == 0)
            {
                summary.Status = "missing";
                return summary;
            }

            for (var index = 0; index < bindPoses.Count; index++)
            {
                var matrix = bindPoses[index] as JObject;
                if (matrix == null || !TryReadBindPoseMatrix(matrix, out var values))
                {
                    summary.IncompleteCount++;
                    if (summary.Samples.Count < 3)
                    {
                        summary.Samples.Add(BindPoseMatrixSample.Incomplete(index));
                    }
                    continue;
                }

                var finite = values.All(float.IsFinite);
                if (!finite)
                {
                    summary.NonFiniteCount++;
                }
                else
                {
                    summary.FiniteCount++;
                }

                var affine = finite
                    && Math.Abs(values[12]) <= 0.0001f
                    && Math.Abs(values[13]) <= 0.0001f
                    && Math.Abs(values[14]) <= 0.0001f
                    && Math.Abs(values[15] - 1f) <= 0.0001f;
                if (!affine)
                {
                    summary.NonAffineCount++;
                }

                var determinant = finite ? CalculateMatrix3x3Determinant(values) : float.NaN;
                if (finite)
                {
                    summary.MinDeterminant = summary.FiniteCount == 1
                        ? determinant
                        : Math.Min(summary.MinDeterminant, determinant);
                    summary.MaxDeterminant = summary.FiniteCount == 1
                        ? determinant
                        : Math.Max(summary.MaxDeterminant, determinant);
                    if (Math.Abs(determinant) < 0.000001f)
                    {
                        summary.NearZeroDeterminantCount++;
                    }

                    var translationLength = MathF.Sqrt(
                        values[3] * values[3]
                        + values[7] * values[7]
                        + values[11] * values[11]);
                    summary.MinTranslationLength = summary.FiniteCount == 1
                        ? translationLength
                        : Math.Min(summary.MinTranslationLength, translationLength);
                    summary.MaxTranslationLength = summary.FiniteCount == 1
                        ? translationLength
                        : Math.Max(summary.MaxTranslationLength, translationLength);
                }

                if (summary.Samples.Count < 3)
                {
                    summary.Samples.Add(BindPoseMatrixSample.FromMatrix(index, values, determinant, affine));
                }
            }

            summary.Status = summary.IncompleteCount > 0 || summary.NonFiniteCount > 0
                ? "invalid"
                : summary.NonAffineCount > 0
                    ? "nonAffine"
                    : "readableAffine";
            return summary;
        }

        private static bool TryReadBindPoseMatrix(JObject matrix, out float[] values)
        {
            values = new float[16];
            for (var row = 0; row < 4; row++)
            {
                for (var column = 0; column < 4; column++)
                {
                    var token = matrix[$"e{row}{column}"];
                    if (token == null)
                    {
                        values = Array.Empty<float>();
                        return false;
                    }
                    values[row * 4 + column] = token.Value<float>();
                }
            }

            return true;
        }

        private static float CalculateMatrix3x3Determinant(IReadOnlyList<float> values)
        {
            return values[0] * (values[5] * values[10] - values[6] * values[9])
                - values[1] * (values[4] * values[10] - values[6] * values[8])
                + values[2] * (values[4] * values[9] - values[5] * values[8]);
        }

        private static HairDeformSummary ReadHairDeformSummary(JObject json, int vertexCount, List<string> warnings)
        {
            var data = json["m_HairDeformData"] as JArray;
            var summary = new HairDeformSummary
            {
                Count = data?.Count ?? 0,
                VertexCount = vertexCount,
            };
            if (data == null || data.Count == 0)
            {
                summary.Status = "missing";
                return summary;
            }

            summary.Status = data.Count == vertexCount
                ? "packedHalf4Diagnostic"
                : "countMismatch";
            if (data.Count != vertexCount)
            {
                warnings.Add("hairDeformDataCountMismatch");
            }

            foreach (var item in data.OfType<JObject>())
            {
                var x = ReadPackedUInt32(item["x"]);
                var y = ReadPackedUInt32(item["y"]);
                var halfBits = new[]
                {
                    (ushort)(x & 0xffff),
                    (ushort)((x >> 16) & 0xffff),
                    (ushort)(y & 0xffff),
                    (ushort)((y >> 16) & 0xffff),
                };

                var values = halfBits.Select(ReadHalfFloat).ToArray();
                for (var i = 0; i < values.Length; i++)
                {
                    summary.Components[i].Add(values[i]);
                }

                if (summary.Samples.Count < 4)
                {
                    summary.Samples.Add(new HairDeformSample(x, y, values));
                }
            }

            return summary;
        }

        private static uint ReadPackedUInt32(JToken token)
        {
            if (token == null)
            {
                return 0;
            }

            return unchecked((uint)token.Value<long>());
        }

        private static float ReadHalfFloat(ushort bits)
        {
            var sign = (bits & 0x8000) != 0 ? -1f : 1f;
            var exponent = (bits >> 10) & 0x1f;
            var fraction = bits & 0x03ff;
            if (exponent == 0)
            {
                return sign * (float)Math.Pow(2, -14) * (fraction / 1024f);
            }
            if (exponent == 0x1f)
            {
                return fraction == 0
                    ? sign * float.PositiveInfinity
                    : float.NaN;
            }

            return sign * (float)Math.Pow(2, exponent - 15) * (1f + fraction / 1024f);
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

        private static Vector3Value? ReadOptionalVector3(JToken token)
        {
            if (token == null)
            {
                return null;
            }

            return new Vector3Value(
                ReadOptionalNamedFloat(token, "x", "X"),
                ReadOptionalNamedFloat(token, "y", "Y"),
                ReadOptionalNamedFloat(token, "z", "Z"));
        }

        private static Vector4Value ReadVector4(JToken token, string field)
        {
            if (token == null)
            {
                throw new InvalidDataException($"AvatarMeshDataAsset vertex missing {field}.");
            }
            return new Vector4Value(ReadFloat(token, "x"), ReadFloat(token, "y"), ReadFloat(token, "z"), ReadFloat(token, "w"));
        }

        private static Vector4Value? ReadOptionalVector4(JToken token)
        {
            if (token == null)
            {
                return null;
            }

            return new Vector4Value(
                ReadOptionalNamedFloat(token, "x", "X"),
                ReadOptionalNamedFloat(token, "y", "Y"),
                ReadOptionalNamedFloat(token, "z", "Z"),
                ReadOptionalNamedFloat(token, "w", "W"));
        }

        private static float ReadOptionalNamedFloat(JToken token, string lowerName, string upperName)
        {
            return token?[lowerName]?.Value<float>()
                ?? token?[upperName]?.Value<float>()
                ?? 0f;
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

        private static int WriteUShortBufferView(Stream stream, JArray bufferViews, IEnumerable<ushort> values, int target)
        {
            Align4(stream);
            var offset = stream.Length;
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            foreach (var value in values)
            {
                writer.Write(value);
            }
            writer.Flush();
            Align4(stream);
            return AddBufferView(bufferViews, offset, stream.Length - offset, target);
        }

        private static int AddBufferView(JArray bufferViews, long offset, long length, int target)
        {
            var bufferView = new JObject
            {
                ["buffer"] = 0,
                ["byteOffset"] = offset,
                ["byteLength"] = length,
            };
            if (target != 0)
            {
                bufferView["target"] = target;
            }
            bufferViews.Add(bufferView);
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

        private static Vector3Value ConvertUnityPositionToGltf(Vector3Value value)
        {
            return new Vector3Value(value.X, value.Z, -value.Y);
        }

        private static Vector3Value ConvertUnityDirectionToGltf(Vector3Value value)
        {
            return new Vector3Value(value.X, value.Z, -value.Y);
        }

        private static Vector4Value ConvertUnityTangentToGltf(Vector4Value value)
        {
            return new Vector4Value(value.X, value.Z, -value.Y, value.W);
        }

        private static Vector4Value ConvertUnityRotationToGltf(Vector4Value value)
        {
            // 诊断 skin 和位置/法线使用同一轴变换。这里仍需后续用视觉对照确认 bind pose 空间。
            return new Vector4Value(value.X, value.Z, -value.Y, value.W);
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

        private sealed record VisualCellJson(
            JObject Json,
            string JsonPath);

        private sealed record PreviewAlphaDecision(
            string AlphaMode,
            float? AlphaCutoff,
            JObject Extras);

        private sealed record TextureAlphaStats(
            int Width,
            int Height,
            long TotalPixels,
            long TransparentPixels,
            long PartialPixels,
            long OpaquePixels,
            byte MinAlpha,
            byte MaxAlpha);

        private sealed record SelectedVisualCellInfo(
            long? PathId,
            string SerializedFile,
            long? GameObjectPathId,
            string GameObjectName,
            long? TransformPathId,
            string TransformPath,
            string TransformPathStatus,
            int DirectChildCount,
            IReadOnlyList<string> DirectChildNames,
            int? TransformNodeCount);

        private sealed class FaceRuntimeEvidence
        {
            public string Status { get; set; }
            public string SelectedVisualCellFile { get; set; }
            public long? SelectedVisualCellPathId { get; set; }
            public long? SelectedGameObjectPathId { get; set; }
            public string RuntimeJsonPath { get; set; }
            public long? RuntimePathId { get; set; }
            public long? RuntimeRootAnchorPathId { get; set; }
            public long? RuntimeNeckNodePathId { get; set; }
            public long? RuntimeHeadNodePathId { get; set; }
            public long? AvatarFaceDataPathId { get; set; }
            public string AvatarFaceDataJsonPath { get; set; }
            public string AvatarFaceDataFile { get; set; }
            public string AvatarFaceDataName { get; set; }
            public int? FaceType { get; set; }
            public int RuntimeAvatarBoneCount { get; set; }
            public int AvatarFaceDataBoneCount { get; set; }
            public int AvatarFaceDataBindPoseCount { get; set; }
            public int? SourceSkinRequiredNodeCount { get; set; }
            public bool SourceSkinRequiredNodeCountMatchesAvatarFaceData { get; set; }
            public bool RuntimeBoneNamesMatchAvatarFaceData { get; set; }
            public int UsedBoneIndexCount { get; set; }
            public int? UsedBoneIndexMax { get; set; }
            public int MappedUsedBoneIndexCount { get; set; }
            public int MissingUsedBoneIndexCount { get; set; }
            public bool AllUsedBoneIndicesMapped { get; set; }
            public List<string> RuntimeBoneNames { get; } = new();
            public List<FaceRuntimeBoneSample> AvatarFaceDataBoneSamples { get; } = new();
            public List<FaceRuntimeMappedBoneRef> MappedUsedBoneSamples { get; } = new();
            public List<BoneRefCount> MissingUsedBoneRefs { get; } = new();

            public JObject ToJson() => new()
            {
                ["status"] = Status,
                ["selectedVisualCellFile"] = SelectedVisualCellFile,
                ["selectedVisualCellPathId"] = SelectedVisualCellPathId,
                ["selectedGameObjectPathId"] = SelectedGameObjectPathId,
                ["runtimeJson"] = string.IsNullOrWhiteSpace(RuntimeJsonPath) ? null : Path.GetFullPath(RuntimeJsonPath),
                ["runtimePathId"] = RuntimePathId,
                ["runtimeRootAnchorPathId"] = RuntimeRootAnchorPathId,
                ["runtimeNeckNodePathId"] = RuntimeNeckNodePathId,
                ["runtimeHeadNodePathId"] = RuntimeHeadNodePathId,
                ["avatarFaceDataPathId"] = AvatarFaceDataPathId,
                ["avatarFaceDataJson"] = string.IsNullOrWhiteSpace(AvatarFaceDataJsonPath) ? null : Path.GetFullPath(AvatarFaceDataJsonPath),
                ["avatarFaceDataFile"] = AvatarFaceDataFile,
                ["avatarFaceDataName"] = AvatarFaceDataName,
                ["faceType"] = FaceType,
                ["runtimeAvatarBoneCount"] = RuntimeAvatarBoneCount,
                ["avatarFaceDataBoneCount"] = AvatarFaceDataBoneCount,
                ["avatarFaceDataBindPoseCount"] = AvatarFaceDataBindPoseCount,
                ["sourceSkinRequiredNodeCount"] = SourceSkinRequiredNodeCount,
                ["sourceSkinRequiredNodeCountMatchesAvatarFaceData"] = SourceSkinRequiredNodeCountMatchesAvatarFaceData,
                ["runtimeBoneNamesMatchAvatarFaceData"] = RuntimeBoneNamesMatchAvatarFaceData,
                ["usedBoneIndexCount"] = UsedBoneIndexCount,
                ["usedBoneIndexMax"] = UsedBoneIndexMax,
                ["mappedUsedBoneIndexCount"] = MappedUsedBoneIndexCount,
                ["missingUsedBoneIndexCount"] = MissingUsedBoneIndexCount,
                ["allUsedBoneIndicesMapped"] = AllUsedBoneIndicesMapped,
                ["runtimeBoneNameSamples"] = new JArray(RuntimeBoneNames.Take(32).Select(x => new JValue(x))),
                ["avatarFaceDataBoneSamples"] = new JArray(AvatarFaceDataBoneSamples.Select(x => x.ToJson())),
                ["mappedUsedBoneSamples"] = new JArray(MappedUsedBoneSamples.Select(x => x.ToJson())),
                ["missingUsedBoneRefs"] = new JArray(MissingUsedBoneRefs.Select(x => x.ToJson())),
                ["rule"] = "AvatarFaceRuntime 和 AvatarFaceData 只提供脸部 boneIndex 顺序、骨骼名和 bind local TRS 线索；缺少最终 joint 层级写入和视觉验证前，仍保持 diagnostic skin blocked。"
            };
        }

        private sealed record FaceRuntimeBoneSample(
            int BoneIndex,
            string Name,
            int? BoneSide,
            IReadOnlyList<int> ChildBoneIndices,
            Vector3Value? BindLocalPosition,
            Vector4Value? BindLocalRotation,
            Vector3Value? BindLocalScale)
        {
            public bool HasLocalTrs => BindLocalPosition.HasValue && BindLocalRotation.HasValue && BindLocalScale.HasValue;

            public JObject ToJson() => new()
            {
                ["boneIndex"] = BoneIndex,
                ["name"] = Name,
                ["boneSide"] = BoneSide,
                ["childCount"] = ChildBoneIndices?.Count ?? 0,
                ["childBoneIndices"] = new JArray((ChildBoneIndices ?? Array.Empty<int>()).Select(x => new JValue(x))),
                ["hasLocalTrs"] = HasLocalTrs,
                ["bindLocalPosition"] = ToJson(BindLocalPosition),
                ["bindLocalRotation"] = ToJson(BindLocalRotation),
                ["bindLocalScale"] = ToJson(BindLocalScale),
            };

            private static JToken ToJson(Vector3Value? value)
            {
                return value.HasValue
                    ? new JArray(value.Value.X, value.Value.Y, value.Value.Z)
                    : JValue.CreateNull();
            }

            private static JToken ToJson(Vector4Value? value)
            {
                return value.HasValue
                    ? new JArray(value.Value.X, value.Value.Y, value.Value.Z, value.Value.W)
                    : JValue.CreateNull();
            }
        }

        private sealed record FaceRuntimeMappedBoneRef(
            int BoneIndex,
            int WeightedRefCount,
            string BoneName)
        {
            public JObject ToJson() => new()
            {
                ["boneIndex"] = BoneIndex,
                ["weightedRefCount"] = WeightedRefCount,
                ["boneName"] = BoneName,
            };
        }

        private sealed class HairCustomizationTintEvidence
        {
            public string Status { get; set; }
            public string SelectedVisualCellFile { get; set; }
            public long? SelectedVisualCellPathId { get; set; }
            public long? SelectedGameObjectPathId { get; set; }
            public long DirectLinkCount { get; set; }
            public int TypeTreeColorDataCount { get; set; }
            public int TypeTreePartDataCount { get; set; }
            public int TypeTreeGroupCount { get; set; }
            public int TypeTreeConfigCount { get; set; }
            public int TypeTreeGroupCandidateCount { get; set; }
            public List<HairCustomizationColorSample> TypeTreeColorSamples { get; } = new();
            public List<HairCustomizationScriptCount> ScriptCounts { get; } = new();
            public long TotalScriptInstanceCount => ScriptCounts.Sum(x => x.InstanceCount);
            public long LightweightRawJsonCount => ScriptCounts.Where(x => x.RawJsonLooksLightweight).Sum(x => x.InstanceCount);
            public bool HasTypeTreeTintFields => TypeTreeColorDataCount > 0 || TypeTreePartDataCount > 0 || TypeTreeGroupCount > 0;

            public JObject ToJson() => new()
            {
                ["status"] = Status,
                ["selectedVisualCellFile"] = SelectedVisualCellFile,
                ["selectedVisualCellPathId"] = SelectedVisualCellPathId,
                ["selectedGameObjectPathId"] = SelectedGameObjectPathId,
                ["totalScriptInstanceCount"] = TotalScriptInstanceCount,
                ["lightweightRawJsonCount"] = LightweightRawJsonCount,
                ["directLinkCount"] = DirectLinkCount,
                ["scriptCounts"] = new JArray(ScriptCounts.Select(x => x.ToJson())),
                ["typeTreeColorDataCount"] = TypeTreeColorDataCount,
                ["typeTreePartDataCount"] = TypeTreePartDataCount,
                ["typeTreeGroupCount"] = TypeTreeGroupCount,
                ["typeTreeConfigCount"] = TypeTreeConfigCount,
                ["typeTreeGroupCandidateCount"] = TypeTreeGroupCandidateCount,
                ["typeTreeColorSamples"] = new JArray(TypeTreeColorSamples.Select(x => x.ToJson())),
                ["rule"] = "这些脚本名只证明 Naraka 存在发型 customization/tint 配置域；没有选中 VisualCell/GameObject 的直接 PPtr 和可解释颜色字段前，不能把它们应用到当前模型。"
            };
        }

        private sealed class HairCustomizationColorSample
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public JObject HighLightingColor { get; set; }
            public JObject Ks { get; set; }
            public double? KsBoost { get; set; }
            public double? KsWidth { get; set; }

            public JObject ToJson() => new()
            {
                ["name"] = Name,
                ["description"] = Description,
                ["highLightingColor"] = HighLightingColor?.DeepClone(),
                ["ks"] = Ks?.DeepClone(),
                ["ksBoost"] = KsBoost,
                ["ksWidth"] = KsWidth,
            };
        }

        private sealed class HairCustomizationScriptCount
        {
            public string ScriptName { get; set; }
            public long InstanceCount { get; set; }
            public long MaxRawJsonLength { get; set; }
            public bool RawJsonLooksLightweight => MaxRawJsonLength > 0 && MaxRawJsonLength <= 600;

            public JObject ToJson() => new()
            {
                ["scriptName"] = ScriptName,
                ["instanceCount"] = InstanceCount,
                ["maxRawJsonLength"] = MaxRawJsonLength,
                ["rawJsonLooksLightweight"] = RawJsonLooksLightweight
            };
        }

        private sealed record SourceObjectRef(
            string Name,
            string SerializedFile,
            long PathId);

        private sealed class SelectedVisualCellChildComponentEvidence
        {
            public string Status { get; set; }
            public string SerializedFile { get; set; }
            public long? ParentTransformPathId { get; set; }
            public IReadOnlyList<SelectedVisualCellChildGameObject> Children { get; set; } = Array.Empty<SelectedVisualCellChildGameObject>();

            public int ChildGameObjectCount => Children?.Count ?? 0;
            public int ComponentCount => Children?.Sum(x => x.Components?.Count ?? 0) ?? 0;
            public int SkinnedMeshRendererCount => Children?.Sum(x => x.Components?.Count(y => string.Equals(y.Type, "SkinnedMeshRenderer", StringComparison.OrdinalIgnoreCase)) ?? 0) ?? 0;
            public int MonoBehaviourCount => Children?.Sum(x => x.Components?.Count(y => string.Equals(y.Type, "MonoBehaviour", StringComparison.OrdinalIgnoreCase)) ?? 0) ?? 0;
            public IReadOnlyList<string> MonoBehaviourScriptNames => (Children ?? Array.Empty<SelectedVisualCellChildGameObject>())
                .SelectMany(x => x.Components ?? Array.Empty<SelectedVisualCellComponent>())
                .Where(x => string.Equals(x.Type, "MonoBehaviour", StringComparison.OrdinalIgnoreCase))
                .Select(x => string.IsNullOrWhiteSpace(x.ScriptName) ? x.Name : x.ScriptName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            public JObject ToJson() => new()
            {
                ["status"] = Status,
                ["serializedFile"] = SerializedFile,
                ["parentTransformPathId"] = ParentTransformPathId,
                ["childGameObjectCount"] = ChildGameObjectCount,
                ["componentCount"] = ComponentCount,
                ["skinnedMeshRendererCount"] = SkinnedMeshRendererCount,
                ["monoBehaviourCount"] = MonoBehaviourCount,
                ["monoBehaviourScriptNames"] = new JArray(MonoBehaviourScriptNames.Select(x => new JValue(x))),
                ["children"] = new JArray((Children ?? Array.Empty<SelectedVisualCellChildGameObject>()).Take(64).Select(x => x.ToJson())),
                ["rule"] = "只记录选中 ActorBodyVisualCell Transform 直属子节点的组件清单；SkinnedMeshRenderer/LXRendererAssistant/CustomTags 只能证明层级和渲染组件存在，不能证明 AvatarBoneWeights boneIndex 到 joint 的映射。"
            };
        }

        private sealed class SelectedVisualCellChildGameObject
        {
            public long TransformPathId { get; init; }
            public long? GameObjectPathId { get; init; }
            public string GameObjectName { get; init; }
            public IReadOnlyList<SelectedVisualCellComponent> Components { get; init; } = Array.Empty<SelectedVisualCellComponent>();

            public JObject ToJson() => new()
            {
                ["gameObjectName"] = GameObjectName,
                ["gameObjectPathId"] = GameObjectPathId,
                ["transformPathId"] = TransformPathId,
                ["componentCount"] = Components?.Count ?? 0,
                ["components"] = new JArray((Components ?? Array.Empty<SelectedVisualCellComponent>()).Select(x => x.ToJson())),
            };
        }

        private sealed class SelectedVisualCellComponent
        {
            public long PathId { get; init; }
            public string Type { get; init; }
            public int? ClassId { get; init; }
            public string Name { get; init; }
            public string ScriptName { get; init; }

            public JObject ToJson() => new()
            {
                ["pathId"] = PathId,
                ["type"] = Type,
                ["classId"] = ClassId,
                ["name"] = Name,
                ["scriptName"] = string.IsNullOrWhiteSpace(ScriptName) ? null : ScriptName,
            };
        }

        private sealed class VisualCellRendererListEvidence
        {
            public string Status { get; set; }
            public int RendererAssistantCount { get; init; }
            public int AvatarRendererCount { get; init; }
            public int MeshRendererCount { get; init; }
            public int SkinnedMeshRendererRefCount { get; set; }
            public LodRendererAssistantCounts LodRendererAssistantCounts { get; init; } = new();
            public bool AvatarRendererMatchesRendererAssistants { get; init; }
            public IReadOnlyList<string> RendererAssistantGameObjectNames { get; set; } = Array.Empty<string>();
            public IReadOnlyList<string> AvatarRendererGameObjectNames { get; set; } = Array.Empty<string>();
            public IReadOnlyList<string> MeshRendererGameObjectNames { get; set; } = Array.Empty<string>();

            public JObject ToJson() => new()
            {
                ["status"] = Status,
                ["rendererAssistantCount"] = RendererAssistantCount,
                ["avatarRendererCount"] = AvatarRendererCount,
                ["meshRendererCount"] = MeshRendererCount,
                ["skinnedMeshRendererRefCount"] = SkinnedMeshRendererRefCount,
                ["lodRendererAssistantCounts"] = LodRendererAssistantCounts.ToJson(),
                ["avatarRendererMatchesRendererAssistants"] = AvatarRendererMatchesRendererAssistants,
                ["rendererAssistantGameObjectNames"] = new JArray((RendererAssistantGameObjectNames ?? Array.Empty<string>()).Take(32).Select(x => new JValue(x))),
                ["avatarRendererGameObjectNames"] = new JArray((AvatarRendererGameObjectNames ?? Array.Empty<string>()).Take(32).Select(x => new JValue(x))),
                ["meshRendererGameObjectNames"] = new JArray((MeshRendererGameObjectNames ?? Array.Empty<string>()).Take(32).Select(x => new JValue(x))),
                ["rule"] = "只记录 ActorBodyVisualCell 顶层 rendererAssistants/avatarRenderers/meshRenderer/lod* 列表的数量和 GameObject 对齐情况；它能证明自定义 Renderer 链路完整，但不能替代 SkinnedMeshRenderer.bones/rootBone/mesh 的 skin 绑定证据。"
            };
        }

        private sealed class LodRendererAssistantCounts
        {
            public int Lod0 { get; init; }
            public int Lod1 { get; init; }
            public int Lod2 { get; init; }
            public int Lod3 { get; init; }
            public int Other { get; init; }

            public JObject ToJson() => new()
            {
                ["lod0"] = Lod0,
                ["lod1"] = Lod1,
                ["lod2"] = Lod2,
                ["lod3"] = Lod3,
                ["other"] = Other,
            };
        }

        private sealed record ManifestEntry(
            string JsonPath,
            string SerializedFile);

        private sealed record VisualCellPart(
            long AssistantPathId,
            string GameObjectName,
            string RendererFile,
            long RendererPathId,
            string AvatarMeshFile,
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

        private sealed class VisualCellRendererSkinBinding
        {
            public string Status { get; set; }
            public string RendererFile { get; set; }
            public long RendererPathId { get; set; }
            public bool RendererObjectFound { get; set; }
            public string RendererType { get; set; }
            public int? RendererClassId { get; set; }
            public string RendererName { get; set; }
            public string RendererGameObjectFile { get; set; }
            public long? RendererGameObjectPathId { get; set; }
            public int RendererMaterialRefCount { get; set; }
            public string MeshFile { get; set; }
            public long? MeshPathId { get; set; }
            public string RootBoneFile { get; set; }
            public long? RootBonePathId { get; set; }
            public int? BoneCount { get; set; }
            public bool? BonesTruncated { get; set; }
            public JArray BoneTargets { get; set; }
            public string RendererJsonStatus { get; set; }
            public string RendererJsonPath { get; set; }
            public long? RendererJsonGameObjectPathId { get; set; }
            public long? RendererJsonMeshPathId { get; set; }
            public long? RendererJsonRootBonePathId { get; set; }
            public int? RendererJsonBoneCount { get; set; }
            public int? RendererJsonMaterialCount { get; set; }

            public bool HasAnyRelation => MeshPathId.HasValue || RootBonePathId.HasValue || BoneCount.HasValue;

            public JObject ToJson()
            {
                return new JObject
                {
                    ["status"] = Status,
                    ["rendererFile"] = RendererFile,
                    ["rendererPathId"] = RendererPathId,
                    ["rendererObjectFound"] = RendererObjectFound,
                    ["rendererType"] = RendererType,
                    ["rendererClassId"] = RendererClassId,
                    ["rendererName"] = RendererName,
                    ["rendererGameObjectFile"] = RendererGameObjectFile,
                    ["rendererGameObjectPathId"] = RendererGameObjectPathId,
                    ["rendererMaterialRefCount"] = RendererMaterialRefCount,
                    ["meshFile"] = MeshFile,
                    ["meshPathId"] = MeshPathId,
                    ["rootBoneFile"] = RootBoneFile,
                    ["rootBonePathId"] = RootBonePathId,
                    ["boneCount"] = BoneCount,
                    ["bonesTruncated"] = BonesTruncated,
                    ["boneTargetsPreview"] = BoneTargets != null ? new JArray(BoneTargets.Take(32).Select(x => x.DeepClone())) : null,
                    ["rendererJsonStatus"] = RendererJsonStatus,
                    ["rendererJsonPath"] = string.IsNullOrWhiteSpace(RendererJsonPath) ? null : Path.GetFullPath(RendererJsonPath),
                    ["rendererJsonGameObjectPathId"] = RendererJsonGameObjectPathId,
                    ["rendererJsonMeshPathId"] = RendererJsonMeshPathId,
                    ["rendererJsonRootBonePathId"] = RendererJsonRootBonePathId,
                    ["rendererJsonBoneCount"] = RendererJsonBoneCount,
                    ["rendererJsonMaterialCount"] = RendererJsonMaterialCount,
                    ["rule"] = "Renderer 对象、GameObject 和材质引用只说明 LXRendererAssistant 链路有效；SQLite 或 SkinnedMeshRenderer JSON 里的 mesh/rootBone/bones 为空时，说明该 Naraka 样本不是标准 Unity Renderer.bones skin 路径，不能写 glTF skin。"
                };
            }
        }

        private sealed class AvatarMeshDataRelationEvidence
        {
            public string Status { get; set; }
            public string AvatarMeshFile { get; set; }
            public long AvatarMeshPathId { get; set; }
            public int RelationCount { get; set; }
            public int ScriptRefCount { get; set; }
            public int PPtrRefCount { get; set; }
            public int NonScriptPPtrCount { get; set; }
            public List<AvatarMeshDataRelationRef> Relations { get; } = new();

            public JObject ToJson()
            {
                return new JObject
                {
                    ["status"] = Status,
                    ["avatarMeshFile"] = AvatarMeshFile,
                    ["avatarMeshPathId"] = AvatarMeshPathId,
                    ["relationCount"] = RelationCount,
                    ["scriptRefCount"] = ScriptRefCount,
                    ["pptrRefCount"] = PPtrRefCount,
                    ["nonScriptPPtrCount"] = NonScriptPPtrCount,
                    ["relationsPreview"] = new JArray(Relations.Select(x => x.ToJson())),
                    ["rule"] = "只记录 AvatarMeshDataAsset 在源索引里的显式关系；如果只有 monoBehaviour.script，说明当前 joint 映射没有通过 PPtr 暴露，不能凭同包对象猜 skin。"
                };
            }
        }

        private sealed record AvatarMeshDataRelationRef(
            string Relation,
            string Path,
            string TargetFile,
            long TargetPathId,
            int? TargetCount)
        {
            public JObject ToJson() => new()
            {
                ["relation"] = Relation,
                ["path"] = Path,
                ["targetFile"] = TargetFile,
                ["targetPathId"] = TargetPathId,
                ["targetCount"] = TargetCount,
            };
        }

        private sealed class AvatarPartDataEvidence
        {
            public string Status { get; init; }
            public string AssetName { get; init; }
            public string AssetJsonPath { get; init; }
            public string AssetFile { get; init; }
            public long AssetPathId { get; init; }
            public string ScriptName { get; init; }
            public int MeshDataIndex { get; init; }
            public int MeshDataCount { get; init; }
            public long MeshDataPathId { get; init; }

            public JObject ToJson()
            {
                return new JObject
                {
                    ["status"] = Status,
                    ["assetName"] = AssetName,
                    ["assetJson"] = string.IsNullOrWhiteSpace(AssetJsonPath) ? null : Path.GetFullPath(AssetJsonPath),
                    ["assetFile"] = AssetFile,
                    ["assetPathId"] = AssetPathId,
                    ["scriptName"] = ScriptName,
                    ["meshDataIndex"] = MeshDataIndex,
                    ["meshDataCount"] = MeshDataCount,
                    ["meshDataPathId"] = MeshDataPathId,
                    ["rule"] = "只表示 AvatarPartDataAsset.m_MeshData 的显式 PPtr 顺序；不能证明骨骼、joint 或 skin 绑定。"
                };
            }
        }

        private sealed class AssistantHeadCollisionEvidence
        {
            public string Status { get; set; }
            public long AssistantPathId { get; init; }
            public string GameObjectName { get; init; }
            public string AssistantFile { get; init; }
            public string AssistantJsonPath { get; init; }
            public int PPtrFileId { get; set; }
            public long PPtrPathId { get; set; }
            public long? RelationId { get; set; }
            public string TargetFile { get; set; }
            public long TargetPathId { get; set; }
            public bool TargetObjectFound { get; set; }
            public string TargetType { get; set; }
            public int? TargetClassId { get; set; }
            public string TargetName { get; set; }

            public bool HasReference => PPtrFileId != 0 || PPtrPathId != 0 || !string.IsNullOrWhiteSpace(TargetFile) || TargetPathId != 0;

            public JObject ToJson()
            {
                return new JObject
                {
                    ["status"] = Status,
                    ["assistantPathId"] = AssistantPathId,
                    ["gameObjectName"] = GameObjectName,
                    ["assistantFile"] = AssistantFile,
                    ["assistantJson"] = string.IsNullOrWhiteSpace(AssistantJsonPath) ? null : Path.GetFullPath(AssistantJsonPath),
                    ["pptrFileId"] = PPtrFileId,
                    ["pptrPathId"] = PPtrPathId,
                    ["relationId"] = RelationId,
                    ["targetFile"] = TargetFile,
                    ["targetPathId"] = TargetPathId,
                    ["targetObjectFound"] = TargetObjectFound,
                    ["targetType"] = TargetType,
                    ["targetClassId"] = TargetClassId,
                    ["targetName"] = TargetName,
                    ["rule"] = "只记录 LXRendererAssistant.headCollisionData 的外部 PPtr 线索；它表示碰撞/辅助数据引用，当前没有骨骼名、Transform PathID 或 joint 映射证据，不能用于 glTF skin。"
                };
            }
        }

        private sealed class BoneDriverHint
        {
            public string Status { get; init; }
            public string ScriptName { get; init; }
            public string SourceJson { get; init; }
            public string SerializedFile { get; init; }
            public long PathId { get; init; }
            public long GameObjectPathId { get; init; }
            public string GameObjectName { get; init; }
            public string TransformPath { get; init; }
            public string TransformPathStatus { get; init; }
            public string DriverSerializeName { get; init; }
            public string AimSerializeName { get; init; }
            public string Aim1SerializeName { get; init; }
            public string Aim2SerializeName { get; init; }

            public JObject ToJson()
            {
                return new JObject
                {
                    ["status"] = Status,
                    ["scriptName"] = ScriptName,
                    ["sourceJson"] = string.IsNullOrWhiteSpace(SourceJson) ? null : Path.GetFullPath(SourceJson),
                    ["serializedFile"] = SerializedFile,
                    ["pathId"] = PathId,
                    ["gameObjectPathId"] = GameObjectPathId,
                    ["gameObjectName"] = GameObjectName,
                    ["transformPath"] = TransformPath,
                    ["transformPathStatus"] = TransformPathStatus,
                    ["driverSerializeName"] = DriverSerializeName,
                    ["aimSerializeName"] = AimSerializeName,
                    ["aim1SerializeName"] = Aim1SerializeName,
                    ["aim2SerializeName"] = Aim2SerializeName,
                    ["rule"] = "只记录 BoneFollowDriver/BoneHairFollowDriver 暴露的骨骼名称和所在 Transform 路径线索；这些字段不能单独作为 AvatarMeshDataAsset 的 joint 映射。"
                };
            }
        }

        private sealed record GameObjectTransformPath(string GameObjectName, string TransformPath, string Status);

        private sealed record SourceObjectLiteWithPathId(long PathId, string Name);

        private sealed class TransformNodeTable
        {
            public string SerializedFile { get; init; }
            public long VisualCellPathId { get; init; }
            public string VisualCellName { get; init; }
            public string VisualCellGameObjectName { get; init; }
            public string Container { get; init; }
            public int TransformNodeCount { get; init; }
            public IReadOnlyList<TransformNodeTableNode> Nodes { get; init; } = Array.Empty<TransformNodeTableNode>();
        }

        private sealed record TransformNodeTableNode(
            int Index,
            long TransformPathId,
            long GameObjectPathId,
            string GameObjectName);

        private sealed class TransformNodeTableCandidate
        {
            public string Status { get; init; }
            public string SerializedFile { get; init; }
            public long VisualCellPathId { get; init; }
            public string VisualCellGameObjectName { get; init; }
            public string Container { get; init; }
            public bool IsSelectedVisualCell { get; init; }
            public int TransformNodeCount { get; init; }
            public int? RequiredNodeCount { get; init; }
            public bool CoversAvatarBoneIndexRange { get; init; }
            public int MatchedTopBoneIndexCount { get; init; }
            public int MissingTopBoneIndexCount { get; init; }
            public IReadOnlyList<TransformNodeTableCandidateRef> MappedTopBoneRefs { get; init; } = Array.Empty<TransformNodeTableCandidateRef>();
            public int UsedBoneIndexCount { get; init; }
            public int CoveredUsedBoneIndexCount { get; init; }
            public int MissingUsedBoneIndexCount { get; init; }
            public bool CoversAllUsedBoneIndices { get; init; }
            public int? UsedBoneIndexMax { get; init; }
            public IReadOnlyList<TransformNodeTableUsedBoneRef> CoveredUsedBoneRefs { get; init; } = Array.Empty<TransformNodeTableUsedBoneRef>();
            public IReadOnlyList<BoneRefCount> MissingUsedBoneRefs { get; init; } = Array.Empty<BoneRefCount>();
            public int ExportedCoveredUsedBoneTransformCount { get; init; }
            public int MissingExportedCoveredUsedBoneTransformCount { get; init; }
            public bool AllCoveredUsedBoneTransformsExported { get; init; }
            public SelectedTransformNodeTableComparison SelectedVisualCellIndexOrderComparison { get; init; }
            public int BoneDriverNodeNameMatchCount { get; init; }
            public IReadOnlyList<string> BoneDriverNodeNameMatches { get; init; } = Array.Empty<string>();

            public JObject ToJson() => new()
            {
                ["status"] = Status,
                ["serializedFile"] = SerializedFile,
                ["visualCellPathId"] = VisualCellPathId,
                ["visualCellGameObjectName"] = VisualCellGameObjectName,
                ["container"] = Container,
                ["isSelectedVisualCell"] = IsSelectedVisualCell,
                ["transformNodeCount"] = TransformNodeCount,
                ["requiredNodeCount"] = RequiredNodeCount,
                ["coversAvatarBoneIndexRange"] = CoversAvatarBoneIndexRange,
                ["matchedTopBoneIndexCount"] = MatchedTopBoneIndexCount,
                ["missingTopBoneIndexCount"] = MissingTopBoneIndexCount,
                ["usedBoneIndexCount"] = UsedBoneIndexCount,
                ["coveredUsedBoneIndexCount"] = CoveredUsedBoneIndexCount,
                ["missingUsedBoneIndexCount"] = MissingUsedBoneIndexCount,
                ["coversAllUsedBoneIndices"] = CoversAllUsedBoneIndices,
                ["usedBoneIndexMax"] = UsedBoneIndexMax,
                ["coveredUsedBoneRefs"] = new JArray((CoveredUsedBoneRefs ?? Array.Empty<TransformNodeTableUsedBoneRef>()).Take(64).Select(x => x.ToJson())),
                ["missingUsedBoneRefs"] = new JArray((MissingUsedBoneRefs ?? Array.Empty<BoneRefCount>()).Take(64).Select(x => x.ToJson())),
                ["exportedCoveredUsedBoneTransformCount"] = ExportedCoveredUsedBoneTransformCount,
                ["missingExportedCoveredUsedBoneTransformCount"] = MissingExportedCoveredUsedBoneTransformCount,
                ["allCoveredUsedBoneTransformsExported"] = AllCoveredUsedBoneTransformsExported,
                ["selectedVisualCellIndexOrderComparison"] = SelectedVisualCellIndexOrderComparison?.ToJson(),
                ["boneDriverNodeNameMatchCount"] = BoneDriverNodeNameMatchCount,
                ["boneDriverNodeNameMatches"] = new JArray((BoneDriverNodeNameMatches ?? Array.Empty<string>()).Take(32).Select(x => new JValue(x))),
                ["mappedTopBoneRefs"] = new JArray((MappedTopBoneRefs ?? Array.Empty<TransformNodeTableCandidateRef>()).Select(x => x.ToJson())),
                ["rule"] = "只把 AvatarBoneWeights 的 boneIndex 按 ActorBodyVisualCell.transformNodes.data 顺序做候选对照；covered/missing usedBoneRefs 只说明实际权重索引是否落在候选表内，selectedVisualCellIndexOrderComparison 用来防止把同 index 但节点名冲突的其它 VisualCell 表误当成可拼接 skin，仍不能写入 glTF skin。"
            };
        }

        private sealed class SelectedTransformNodeTableComparison
        {
            public string Status { get; init; }
            public int ComparedBoneIndexCount { get; init; }
            public int NameMatchCount { get; init; }
            public int NameConflictCount { get; init; }
            public IReadOnlyList<SelectedTransformNodeTableComparisonRef> Conflicts { get; init; } = Array.Empty<SelectedTransformNodeTableComparisonRef>();

            public JObject ToJson() => new()
            {
                ["status"] = Status,
                ["comparedBoneIndexCount"] = ComparedBoneIndexCount,
                ["nameMatchCount"] = NameMatchCount,
                ["nameConflictCount"] = NameConflictCount,
                ["conflicts"] = new JArray((Conflicts ?? Array.Empty<SelectedTransformNodeTableComparisonRef>()).Select(x => x.ToJson())),
                ["rule"] = "同一个 boneIndex 在不同 ActorBodyVisualCell.transformNodes.data 中可能表示完全不同节点；如果这里大量冲突，同包其它覆盖表只能作为来源线索，不能拿来补选中网格的 skin joint。"
            };
        }

        private sealed record SelectedTransformNodeTableComparisonRef(
            int BoneIndex,
            int WeightedRefCount,
            string SelectedGameObjectName,
            long SelectedGameObjectPathId,
            long SelectedTransformPathId,
            string CandidateGameObjectName,
            long CandidateGameObjectPathId,
            long CandidateTransformPathId,
            bool NameMatches)
        {
            public JObject ToJson() => new()
            {
                ["boneIndex"] = BoneIndex,
                ["weightedRefCount"] = WeightedRefCount,
                ["selectedGameObjectName"] = SelectedGameObjectName,
                ["selectedGameObjectPathId"] = SelectedGameObjectPathId,
                ["selectedTransformPathId"] = SelectedTransformPathId,
                ["candidateGameObjectName"] = CandidateGameObjectName,
                ["candidateGameObjectPathId"] = CandidateGameObjectPathId,
                ["candidateTransformPathId"] = CandidateTransformPathId,
                ["nameMatches"] = NameMatches,
            };
        }

        private sealed record TransformNodeTableUsedBoneRef(
            int BoneIndex,
            int WeightedRefCount,
            string GameObjectName,
            long GameObjectPathId,
            long TransformPathId,
            bool HasExportedTransform,
            bool HasLocalTrs,
            ExportedTransformNode ExportedTransform)
        {
            public JObject ToJson() => new()
            {
                ["boneIndex"] = BoneIndex,
                ["weightedRefCount"] = WeightedRefCount,
                ["candidateGameObjectName"] = GameObjectName,
                ["candidateGameObjectPathId"] = GameObjectPathId,
                ["candidateTransformPathId"] = TransformPathId,
                ["hasExportedTransform"] = HasExportedTransform,
                ["hasLocalTrs"] = HasLocalTrs,
                ["candidateTransform"] = ExportedTransform?.ToJson(),
            };
        }

        private sealed record TransformNodeTableCandidateRef(
            int BoneIndex,
            int WeightedRefCount,
            string GameObjectName,
            long GameObjectPathId,
            long TransformPathId,
            ExportedTransformNode ExportedTransform)
        {
            public JObject ToJson() => new()
            {
                ["boneIndex"] = BoneIndex,
                ["weightedRefCount"] = WeightedRefCount,
                ["candidateGameObjectName"] = GameObjectName,
                ["candidateGameObjectPathId"] = GameObjectPathId,
                ["candidateTransformPathId"] = TransformPathId,
                ["candidateTransform"] = ExportedTransform?.ToJson(),
            };
        }

        private sealed class ExportedTransformNode
        {
            public long PathId { get; init; }
            public string JsonPath { get; init; }
            public Vector3Value? LocalPosition { get; init; }
            public Vector4Value? LocalRotation { get; init; }
            public Vector3Value? LocalScale { get; init; }
            public long FatherPathId { get; init; }
            public long GameObjectPathId { get; init; }
            public int ChildCount { get; init; }

            public bool HasLocalTrs => LocalPosition.HasValue && LocalRotation.HasValue && LocalScale.HasValue;

            public JObject ToJson() => new()
            {
                ["status"] = HasLocalTrs ? "readableLocalTrs" : "missingLocalTrs",
                ["sourceJson"] = string.IsNullOrWhiteSpace(JsonPath) ? null : Path.GetFullPath(JsonPath),
                ["transformPathId"] = PathId,
                ["gameObjectPathIdFromJson"] = GameObjectPathId,
                ["fatherPathId"] = FatherPathId,
                ["childCount"] = ChildCount,
                ["localPosition"] = ToJson(LocalPosition),
                ["localRotation"] = ToJson(LocalRotation),
                ["localScale"] = ToJson(LocalScale),
                ["rule"] = "该 Transform JSON 只提供候选节点的 local TRS 和层级线索；缺少 AvatarMeshDataAsset 到节点表的确定映射前不能写 glTF skin。"
            };

            private static JToken ToJson(Vector3Value? value)
            {
                return value.HasValue
                    ? new JArray(value.Value.X, value.Value.Y, value.Value.Z)
                    : JValue.CreateNull();
            }

            private static JToken ToJson(Vector4Value? value)
            {
                return value.HasValue
                    ? new JArray(value.Value.X, value.Value.Y, value.Value.Z, value.Value.W)
                    : JValue.CreateNull();
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
            public HairDeformSummary HairDeform { get; set; } = new();
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

        private sealed class HairDeformSummary
        {
            public string Status { get; set; } = "missing";
            public int Count { get; set; }
            public int VertexCount { get; set; }
            public HairDeformComponentSummary[] Components { get; } =
            {
                new(),
                new(),
                new(),
                new(),
            };
            public List<HairDeformSample> Samples { get; } = new();

            public JObject ToJson()
            {
                return new JObject
                {
                    ["status"] = Status,
                    ["count"] = Count,
                    ["vertexCount"] = VertexCount,
                    ["matchesVertexCount"] = Count == VertexCount,
                    ["packing"] = Count > 0 ? "twoUInt32AsHalf4" : "missing",
                    ["componentRanges"] = new JArray(Components.Select(x => x.ToJson())),
                    ["samples"] = new JArray(Samples.Select(x => x.ToJson())),
                    ["rule"] = "只把 m_HairDeformData 作为每顶点 packed half4 诊断值记录；当前没有节点名、PathID 或离散 joint 索引证据，不能用于 glTF skin。"
                };
            }
        }

        private sealed class HairDeformComponentSummary
        {
            private readonly HashSet<string> _roundedValues = new(StringComparer.Ordinal);

            public int Count { get; private set; }
            public int FiniteCount { get; private set; }
            public int ZeroCount { get; private set; }
            public int OneCount { get; private set; }
            public int NegativeCount { get; private set; }
            public float Min { get; private set; } = float.PositiveInfinity;
            public float Max { get; private set; } = float.NegativeInfinity;

            public void Add(float value)
            {
                Count++;
                if (!float.IsFinite(value))
                {
                    return;
                }

                FiniteCount++;
                Min = Math.Min(Min, value);
                Max = Math.Max(Max, value);
                if (Math.Abs(value) < 0.000001f)
                {
                    ZeroCount++;
                }
                if (Math.Abs(value - 1f) < 0.000001f)
                {
                    OneCount++;
                }
                if (value < 0f)
                {
                    NegativeCount++;
                }
                _roundedValues.Add(value.ToString("G6", CultureInfo.InvariantCulture));
            }

            public JObject ToJson() => new()
            {
                ["count"] = Count,
                ["finiteCount"] = FiniteCount,
                ["min"] = FiniteCount == 0 ? JValue.CreateNull() : new JValue(Min),
                ["max"] = FiniteCount == 0 ? JValue.CreateNull() : new JValue(Max),
                ["zeroCount"] = ZeroCount,
                ["oneCount"] = OneCount,
                ["negativeCount"] = NegativeCount,
                ["roundedUniqueCount"] = _roundedValues.Count,
            };
        }

        private sealed record HairDeformSample(uint X, uint Y, IReadOnlyList<float> Half4)
        {
            public JObject ToJson() => new()
            {
                ["rawX"] = "0x" + X.ToString("x8", CultureInfo.InvariantCulture),
                ["rawY"] = "0x" + Y.ToString("x8", CultureInfo.InvariantCulture),
                ["half4"] = new JArray((Half4 ?? Array.Empty<float>()).Select(x => Math.Round(x, 6))),
            };
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
            public string AnimSkinBindPoseSlotStatus { get; set; } = "missingAnimSkinData";
            public bool AnimSkinIndicesFitBindPoseSlots { get; set; }
            public int AvatarSkinDataCount { get; set; }
            public int AvatarBoneOffsetCount { get; set; }
            public int AvatarBoneWeightsCount { get; set; }
            public string AvatarBoneOffsetPairStatus { get; set; } = "missing";
            public int AvatarBoneOffsetPairCount { get; set; }
            public bool AvatarBoneOffsetPairCountMatchesVertexCount { get; set; }
            public int AvatarBoneOffsetContinuousPairCount { get; set; }
            public int AvatarBoneOffsetGapOrOverlapCount { get; set; }
            public int AvatarBoneOffsetLastRangeEnd { get; set; }
            public int AvatarWeightedVertexCount { get; set; }
            public int AvatarUniqueBoneCount { get; set; }
            public int? AvatarMinBoneIndex { get; set; }
            public int? AvatarMaxBoneIndex { get; set; }
            public int AvatarNegativeBoneRefCount { get; set; }
            public int AvatarVerticesWithNegativeBoneRefCount { get; set; }
            public int AvatarMaxInfluenceCount { get; set; }
            public int AvatarInvalidRangeCount { get; set; }
            public int BindPoseCount { get; set; }
            public BindPoseMatrixSummary BindPoseMatrix { get; set; } = new();
            public List<BoneRefCount> AvatarBoneRefs { get; } = new();
            public List<BoneRefCount> AvatarTopBoneRefs { get; } = new();
            public List<IReadOnlyList<VertexSkinInfluence>> VertexAvatarInfluences { get; } = new();
            public List<TransformNodeTableCandidate> TransformNodeTableCandidates { get; } = new();
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
                    ["animSkinBindPoseSlotStatus"] = AnimSkinBindPoseSlotStatus,
                    ["animSkinIndicesFitBindPoseSlots"] = AnimSkinIndicesFitBindPoseSlots,
                    ["avatarSkinDataCount"] = AvatarSkinDataCount,
                    ["avatarBoneOffsetCount"] = AvatarBoneOffsetCount,
                    ["avatarBoneWeightsCount"] = AvatarBoneWeightsCount,
                    ["avatarBoneOffsetPairStatus"] = AvatarBoneOffsetPairStatus,
                    ["avatarBoneOffsetPairCount"] = AvatarBoneOffsetPairCount,
                    ["avatarBoneOffsetPairCountMatchesVertexCount"] = AvatarBoneOffsetPairCountMatchesVertexCount,
                    ["avatarBoneOffsetContinuousPairCount"] = AvatarBoneOffsetContinuousPairCount,
                    ["avatarBoneOffsetGapOrOverlapCount"] = AvatarBoneOffsetGapOrOverlapCount,
                    ["avatarBoneOffsetLastRangeEnd"] = AvatarBoneOffsetLastRangeEnd,
                    ["avatarWeightedVertexCount"] = AvatarWeightedVertexCount,
                    ["avatarUniqueBoneCount"] = AvatarUniqueBoneCount,
                    ["avatarMinBoneIndex"] = AvatarMinBoneIndex,
                    ["avatarMaxBoneIndex"] = AvatarMaxBoneIndex,
                    ["avatarNegativeBoneRefCount"] = AvatarNegativeBoneRefCount,
                    ["avatarVerticesWithNegativeBoneRefCount"] = AvatarVerticesWithNegativeBoneRefCount,
                    ["avatarMaxInfluenceCount"] = AvatarMaxInfluenceCount,
                    ["avatarInvalidRangeCount"] = AvatarInvalidRangeCount,
                    ["avatarBoneRefCount"] = AvatarBoneRefs.Count,
                    ["avatarBoneRefs"] = new JArray(AvatarBoneRefs.Select(x => x.ToJson())),
                    ["avatarTopBoneRefs"] = new JArray(AvatarTopBoneRefs.Select(x => x.ToJson())),
                    ["transformNodeTableCandidateCount"] = TransformNodeTableCandidates.Count,
                    ["transformNodeTableCandidates"] = new JArray(GetReportTransformNodeTableCandidates().Select(x => x.ToJson())),
                    ["bindPoseCount"] = BindPoseCount,
                    ["bindPoseMatrixStatus"] = BindPoseMatrix?.Status ?? "missing",
                    ["bindPoseMatrix"] = BindPoseMatrix?.ToJson(),
                    ["mappedJointCount"] = 0,
                    ["layoutWarnings"] = new JArray(LayoutWarnings),
                    ["rule"] = "m_AnimSkinData 的 boneIndex 只先按 m_BindPoses 槽位做自洽检查；bindPoseMatrix 只说明 m_BindPoses 数值是否可读/仿射/非奇异；m_AvatarBoneWeights 是另一套 avatar boneIndex 证据，avatarBoneRefs 只列出实际参与权重的非负 boneIndex，缺少确定性 joint 名称/路径映射前不写 glTF skin。"
                };
            }

            private IEnumerable<TransformNodeTableCandidate> GetReportTransformNodeTableCandidates()
            {
                var picked = new List<TransformNodeTableCandidate>();
                foreach (var candidate in TransformNodeTableCandidates
                    .OrderByDescending(x => x.CoversAllUsedBoneIndices)
                    .ThenByDescending(x => x.AllCoveredUsedBoneTransformsExported)
                    .ThenByDescending(x => x.MatchedTopBoneIndexCount)
                    .ThenByDescending(x => x.TransformNodeCount)
                    .ThenBy(x => x.VisualCellGameObjectName, StringComparer.OrdinalIgnoreCase)
                    .Take(8))
                {
                    picked.Add(candidate);
                }

                foreach (var selected in TransformNodeTableCandidates.Where(x => x.IsSelectedVisualCell))
                {
                    if (picked.Any(x => x.VisualCellPathId == selected.VisualCellPathId
                        && string.Equals(x.SerializedFile, selected.SerializedFile, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                    picked.Add(selected);
                }

                return picked;
            }
        }

        private sealed class BindPoseMatrixSummary
        {
            public string Status { get; set; } = "missing";
            public int Count { get; set; }
            public int FiniteCount { get; set; }
            public int IncompleteCount { get; set; }
            public int NonFiniteCount { get; set; }
            public int NonAffineCount { get; set; }
            public int NearZeroDeterminantCount { get; set; }
            public float MinDeterminant { get; set; }
            public float MaxDeterminant { get; set; }
            public float MinTranslationLength { get; set; }
            public float MaxTranslationLength { get; set; }
            public List<BindPoseMatrixSample> Samples { get; } = new();

            public JObject ToJson() => new()
            {
                ["status"] = Status,
                ["count"] = Count,
                ["finiteCount"] = FiniteCount,
                ["incompleteCount"] = IncompleteCount,
                ["nonFiniteCount"] = NonFiniteCount,
                ["nonAffineCount"] = NonAffineCount,
                ["nearZeroDeterminantCount"] = NearZeroDeterminantCount,
                ["determinantMin"] = FiniteCount == 0 ? JValue.CreateNull() : RoundFloat(MinDeterminant),
                ["determinantMax"] = FiniteCount == 0 ? JValue.CreateNull() : RoundFloat(MaxDeterminant),
                ["translationLengthMin"] = FiniteCount == 0 ? JValue.CreateNull() : RoundFloat(MinTranslationLength),
                ["translationLengthMax"] = FiniteCount == 0 ? JValue.CreateNull() : RoundFloat(MaxTranslationLength),
                ["samples"] = new JArray(Samples.Select(x => x.ToJson())),
                ["rule"] = "该摘要只检查 AvatarMeshDataAsset.m_BindPoses 矩阵数值健康度；它不能单独证明 boneIndex 到 Unity joint 的映射正确。"
            };
        }

        private sealed record BindPoseMatrixSample(int Index, string Status, float[] Row0, float[] Row1, float[] Row2, float Determinant, bool Affine)
        {
            public static BindPoseMatrixSample Incomplete(int index) =>
                new(index, "incomplete", Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>(), float.NaN, false);

            public static BindPoseMatrixSample FromMatrix(int index, IReadOnlyList<float> values, float determinant, bool affine) =>
                new(
                    index,
                    affine ? "affine" : "nonAffine",
                    new[] { values[0], values[1], values[2], values[3] },
                    new[] { values[4], values[5], values[6], values[7] },
                    new[] { values[8], values[9], values[10], values[11] },
                    determinant,
                    affine);

            public JObject ToJson() => new()
            {
                ["index"] = Index,
                ["status"] = Status,
                ["affine"] = Affine,
                ["determinant"] = float.IsFinite(Determinant) ? RoundFloat(Determinant) : JValue.CreateNull(),
                ["row0"] = new JArray(Row0.Select(RoundFloat)),
                ["row1"] = new JArray(Row1.Select(RoundFloat)),
                ["row2"] = new JArray(Row2.Select(RoundFloat)),
            };
        }

        private static JValue RoundFloat(float value) =>
            new(Math.Round(value, 6));

        private sealed record BoneRefCount(int BoneIndex, int WeightedRefCount)
        {
            public JObject ToJson() => new()
            {
                ["boneIndex"] = BoneIndex,
                ["weightedRefCount"] = WeightedRefCount,
                ["meaning"] = BoneIndex < 0
                    ? "Naraka AvatarBoneWeights 中出现的负索引权重；当前只记录为未知哨兵/特殊权重，不能写成 glTF joint。"
                    : "Naraka AvatarBoneWeights 中的高频 boneIndex；缺少确定性节点表映射前不能写成 glTF joint。"
            };
        }

        private interface INarakaDiagnosticSkin
        {
            IReadOnlyList<int> JointNodeIndices { get; }
            IReadOnlyList<int> RootNodeIndices { get; }
            IReadOnlyDictionary<int, int> BoneIndexToJointSlot { get; }
            int JointCount { get; }
            JObject BuildSkinJson(Stream stream, JArray bufferViews, JArray accessors);
            JObject ToNodeJson();
            JObject ToJson();
        }

        private sealed class ExternalSkeletonDiagnosticSkin : INarakaDiagnosticSkin
        {
            private readonly TransformNodeTableCandidate _candidate;

            public ExternalSkeletonDiagnosticSkin(
                TransformNodeTableCandidate candidate,
                IReadOnlyList<int> jointNodeIndices,
                IReadOnlyList<int> rootNodeIndices,
                IReadOnlyDictionary<int, int> boneIndexToJointSlot,
                IReadOnlyList<float[]> inverseBindMatrices,
                int nodeCount,
                int ancestorNodeCount)
            {
                _candidate = candidate;
                JointNodeIndices = jointNodeIndices ?? Array.Empty<int>();
                RootNodeIndices = rootNodeIndices ?? Array.Empty<int>();
                BoneIndexToJointSlot = boneIndexToJointSlot ?? new Dictionary<int, int>();
                InverseBindMatrices = inverseBindMatrices ?? Array.Empty<float[]>();
                NodeCount = nodeCount;
                AncestorNodeCount = Math.Max(0, ancestorNodeCount);
            }

            public IReadOnlyList<int> JointNodeIndices { get; }
            public IReadOnlyList<int> RootNodeIndices { get; }
            public IReadOnlyDictionary<int, int> BoneIndexToJointSlot { get; }
            public IReadOnlyList<float[]> InverseBindMatrices { get; }
            public int NodeCount { get; }
            public int AncestorNodeCount { get; }
            public int JointCount => JointNodeIndices.Count;

            public JObject BuildSkinJson(Stream stream, JArray bufferViews, JArray accessors)
            {
                var matrices = InverseBindMatrices.Count == JointCount
                    ? InverseBindMatrices.SelectMany(x => x)
                    : Enumerable.Range(0, JointCount).SelectMany(_ => IdentityMatrix4x4());
                var inverseBindView = WriteFloatBufferView(stream, bufferViews, matrices, 0);
                accessors.Add(new JObject
                {
                    ["bufferView"] = inverseBindView,
                    ["componentType"] = 5126,
                    ["count"] = JointCount,
                    ["type"] = "MAT4"
                });

                var result = new JObject
                {
                    ["name"] = "NarakaExternalSkeletonDiagnosticSkin",
                    ["joints"] = new JArray(JointNodeIndices),
                    ["inverseBindMatrices"] = accessors.Count - 1,
                    ["extras"] = ToJson()
                };
                if (RootNodeIndices.Count > 0)
                {
                    result["skeleton"] = RootNodeIndices[0];
                }
                return result;
            }

            public JObject ToNodeJson() => new()
            {
                ["status"] = "externalSkeletonDiagnosticSkin",
                ["skinIndex"] = 0,
                ["jointCount"] = JointCount,
                ["rule"] = "显式诊断开关生成的外部骨架 skin；用于验证 Naraka 模块 boneIndex 是否能套用完整身体 VisualCell 节点表，不代表默认生产绑定已通过。"
            };

            public JObject ToJson() => new()
            {
                ["status"] = "externalSkeletonDiagnosticSkin",
                ["visualCellPathId"] = _candidate?.VisualCellPathId,
                ["visualCellGameObjectName"] = _candidate?.VisualCellGameObjectName,
                ["jointCount"] = JointCount,
                ["nodeCount"] = NodeCount,
                ["ancestorNodeCount"] = AncestorNodeCount,
                ["rootNodeCount"] = RootNodeIndices.Count,
                ["inverseBindMatrixSource"] = InverseBindMatrices.Count == JointCount
                    ? "externalSkeletonRestWorldInverseDiagnostic"
                    : "identityDiagnosticFallback",
                ["weightSource"] = "AvatarMeshDataAsset.m_AvatarBoneWeights",
                ["jointSource"] = "external ActorBodyVisualCell.transformNodes.data index order",
                ["rule"] = "该 skin 只在显式诊断开关下写出。boneIndex 到 joint 的映射来自外部 VisualCell 节点表顺序，inverse bind matrix 来自外部骨架 rest world TRS 求逆，必须通过静态/动画视觉验证后才能进入生产结论。"
            };

            private static IEnumerable<float> IdentityMatrix4x4()
            {
                yield return 1f; yield return 0f; yield return 0f; yield return 0f;
                yield return 0f; yield return 1f; yield return 0f; yield return 0f;
                yield return 0f; yield return 0f; yield return 1f; yield return 0f;
                yield return 0f; yield return 0f; yield return 0f; yield return 1f;
            }
        }

        private sealed class FaceRuntimeDiagnosticSkin : INarakaDiagnosticSkin
        {
            private readonly FaceRuntimeEvidence _evidence;

            public FaceRuntimeDiagnosticSkin(
                FaceRuntimeEvidence evidence,
                IReadOnlyList<int> jointNodeIndices,
                IReadOnlyList<int> rootNodeIndices,
                IReadOnlyDictionary<int, int> boneIndexToJointSlot,
                IReadOnlyList<float[]> inverseBindMatrices,
                int faceRootBoneCount)
            {
                _evidence = evidence;
                JointNodeIndices = jointNodeIndices ?? Array.Empty<int>();
                RootNodeIndices = rootNodeIndices ?? Array.Empty<int>();
                BoneIndexToJointSlot = boneIndexToJointSlot ?? new Dictionary<int, int>();
                InverseBindMatrices = inverseBindMatrices ?? Array.Empty<float[]>();
                FaceRootBoneCount = Math.Max(0, faceRootBoneCount);
            }

            public IReadOnlyList<int> JointNodeIndices { get; }
            public IReadOnlyList<int> RootNodeIndices { get; }
            public IReadOnlyDictionary<int, int> BoneIndexToJointSlot { get; }
            public IReadOnlyList<float[]> InverseBindMatrices { get; }
            public int FaceRootBoneCount { get; }
            public int JointCount => JointNodeIndices.Count;

            public JObject BuildSkinJson(Stream stream, JArray bufferViews, JArray accessors)
            {
                var matrices = InverseBindMatrices.Count == JointCount
                    ? InverseBindMatrices.SelectMany(x => x)
                    : Enumerable.Range(0, JointCount).SelectMany(_ => IdentityMatrix4x4());
                var inverseBindView = WriteFloatBufferView(stream, bufferViews, matrices, 0);
                accessors.Add(new JObject
                {
                    ["bufferView"] = inverseBindView,
                    ["componentType"] = 5126,
                    ["count"] = JointCount,
                    ["type"] = "MAT4"
                });

                var result = new JObject
                {
                    ["name"] = "NarakaFaceRuntimeDiagnosticSkin",
                    ["joints"] = new JArray(JointNodeIndices),
                    ["inverseBindMatrices"] = accessors.Count - 1,
                    ["extras"] = ToJson()
                };
                if (RootNodeIndices.Count > 0)
                {
                    result["skeleton"] = RootNodeIndices[0];
                }
                return result;
            }

            public JObject ToNodeJson() => new()
            {
                ["status"] = "faceRuntimeDiagnosticSkin",
                ["skinIndex"] = 0,
                ["jointCount"] = JointCount,
                ["avatarFaceDataPathId"] = _evidence?.AvatarFaceDataPathId,
                ["rule"] = "显式诊断开关生成的 Naraka 脸部运行时 skin；用于验证 AvatarFaceData boneIndex 顺序和 bind local TRS，不代表完整角色 skin 已通过。"
            };

            public JObject ToJson() => new()
            {
                ["status"] = "faceRuntimeDiagnosticSkin",
                ["avatarFaceDataPathId"] = _evidence?.AvatarFaceDataPathId,
                ["avatarFaceDataName"] = _evidence?.AvatarFaceDataName,
                ["jointCount"] = JointCount,
                ["faceRootBoneCount"] = FaceRootBoneCount,
                ["inverseBindMatrixSource"] = InverseBindMatrices.Count == JointCount
                    ? "avatarFaceDataBindLocalTrsHierarchyInverseDiagnostic"
                    : "identityDiagnosticFallback",
                ["weightSource"] = "AvatarMeshDataAsset.m_AvatarBoneWeights",
                ["jointSource"] = "AvatarFaceRuntime.m_AvatarFaceData -> AvatarFaceData.m_AvatarBones index order",
                ["mappedUsedBoneIndexCount"] = _evidence?.MappedUsedBoneIndexCount,
                ["usedBoneIndexCount"] = _evidence?.UsedBoneIndexCount,
                ["rule"] = "该 skin 只在显式诊断开关下写出。boneIndex 到 joint 的映射来自 AvatarFaceData.m_AvatarBones 顺序，inverse bind matrix 来自脸部 bind local TRS 层级，缺少完整角色父骨、材质 tint 和视觉验收前不能进入生产结论。"
            };

            private static IEnumerable<float> IdentityMatrix4x4()
            {
                yield return 1f; yield return 0f; yield return 0f; yield return 0f;
                yield return 0f; yield return 1f; yield return 0f; yield return 0f;
                yield return 0f; yield return 0f; yield return 1f; yield return 0f;
                yield return 0f; yield return 0f; yield return 0f; yield return 1f;
            }
        }

        private sealed record VertexSkinInfluence(int BoneIndex, float Weight);

        private readonly record struct Vector2Value(float X, float Y);
        private readonly record struct Vector3Value(float X, float Y, float Z);
        private readonly record struct Vector4Value(float X, float Y, float Z, float W);
    }
}
