using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AnimeStudio.CLI
{
    internal static class VerifiedAnimationPreviewImporter
    {
        public static string Apply(
            string libraryRoot,
            string mergeReportPath,
            string renderReportPath,
            string sourceReportPath,
            string sourceIndexPath = null,
            string sourceGame = null)
        {
            if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
            {
                throw new DirectoryNotFoundException($"Library root not found: {libraryRoot}");
            }
            if (string.IsNullOrWhiteSpace(mergeReportPath) || !File.Exists(mergeReportPath))
            {
                throw new FileNotFoundException($"Merged animation report not found: {mergeReportPath}", mergeReportPath);
            }
            if (string.IsNullOrWhiteSpace(renderReportPath) || !File.Exists(renderReportPath))
            {
                throw new FileNotFoundException("--verified_animation_render_report is required before promoting an animation preview.", renderReportPath);
            }
            if (string.IsNullOrWhiteSpace(sourceReportPath) || !File.Exists(sourceReportPath))
            {
                throw new FileNotFoundException("--verified_animation_source_report is required before promoting a Naraka SimpleAnimation preview.", sourceReportPath);
            }

            var root = Path.GetFullPath(libraryRoot);
            var mergeReport = JObject.Parse(File.ReadAllText(mergeReportPath));
            var renderReport = JObject.Parse(File.ReadAllText(renderReportPath));
            var sourceReport = JObject.Parse(File.ReadAllText(sourceReportPath));

            var modelGltf = FullPath(S(mergeReport, "modelGltf"));
            var mergedGltf = FullPath(S(mergeReport, "outputGltf"));
            if (string.IsNullOrWhiteSpace(modelGltf) || !File.Exists(modelGltf))
            {
                throw new FileNotFoundException("Merged animation report has no existing modelGltf.", modelGltf);
            }
            if (string.IsNullOrWhiteSpace(mergedGltf) || !File.Exists(mergedGltf))
            {
                throw new FileNotFoundException("Merged animation report has no existing outputGltf.", mergedGltf);
            }

            var modelOutput = MakeRelativeIfInside(root, modelGltf);
            if (Path.IsPathRooted(modelOutput))
            {
                throw new InvalidOperationException("Merged animation modelGltf must point inside the target Library root. Build or select the Library that owns this model first.");
            }

            ValidateMergeReport(mergeReport);
            ValidateRenderReport(renderReport);
            var simpleAnimationEvidence = ExtractSimpleAnimationEvidence(sourceReport, mergeReport);

            var modelName = Path.GetFileNameWithoutExtension(modelGltf);
            var clipName = S(simpleAnimationEvidence, "clipName")
                ?? Path.GetFileNameWithoutExtension(S(mergeReport, "animationGltf"))
                ?? Path.GetFileNameWithoutExtension(mergedGltf);
            var targetDir = Path.Combine(root, "Animations", "VerifiedPreviews", $"{SafeName(modelName)}__{SafeName(clipName)}");
            Directory.CreateDirectory(targetDir);
            CopyMergedPreviewFolder(mergedGltf, targetDir);
            var targetGltf = Path.Combine(targetDir, Path.GetFileName(mergedGltf));
            var animationOutput = MakeRelative(root, targetGltf);

            var importReport = new JObject
            {
                ["status"] = "ok",
                ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["libraryRoot"] = root,
                ["modelOutput"] = modelOutput,
                ["animationOutput"] = animationOutput,
                ["clipName"] = clipName,
                ["source"] = "NarakaSimpleAnimationVerifiedPreview",
                ["productionReadiness"] = "productionPreviewReady",
                ["previewOnly"] = true,
                ["embeddedModelRequired"] = true,
                ["mergeReport"] = mergeReportPath,
                ["renderReport"] = renderReportPath,
                ["sourceReport"] = sourceReportPath,
                ["rule"] = "该关系来自 Naraka SimpleAnimation 显式 PPtr、TypeTree 自动默认 state、已合并 glTF TRS、glTF validator/渲染像素运动诊断。它是可用预览动画包，不伪装成独立无 skin AnimationClip。",
                ["simpleAnimationEvidence"] = simpleAnimationEvidence,
                ["mergeEvidence"] = BuildMergeEvidence(mergeReport),
                ["renderEvidence"] = BuildRenderEvidence(renderReport),
            };

            UpsertAnimationAssetCatalogRow(root, animationOutput, clipName, modelOutput, simpleAnimationEvidence, importReport);
            UpsertCompactModelAnimationCandidate(root, modelOutput, animationOutput, clipName, simpleAnimationEvidence, importReport);
            var reportPath = Path.Combine(targetDir, "verified_animation_preview_import.json");
            File.WriteAllText(reportPath, importReport.ToString(Formatting.Indented));

            SQLiteLibraryIndexBuilder.Build(root, sourceIndexPath: sourceIndexPath, sourceGame: sourceGame);
            UpsertCompactModelAnimationCandidate(root, modelOutput, animationOutput, clipName, simpleAnimationEvidence, importReport);
            Logger.Info($"Verified animation preview imported: {animationOutput}");
            return reportPath;
        }

        private static void ValidateMergeReport(JObject report)
        {
            if (I(report, "animationCountAdded") < 1 || I(report, "channelCount") < 1)
            {
                throw new InvalidOperationException("Merged animation report has no animation channels.");
            }

            var coverage = report["animationJointCoverage"] as JObject;
            var vertex = coverage?["vertexWeightCoverage"] as JObject;
            var core = coverage?["coreBodyCoverage"] as JObject;
            if (D(vertex, "animatedWeightedVertexCoverage") < 0.6)
            {
                throw new InvalidOperationException("Merged animation weighted vertex coverage is below promotion threshold.");
            }
            if (D(core, "animatedCoreBodyJointCoverage") < 0.9)
            {
                throw new InvalidOperationException("Merged animation core body joint coverage is below promotion threshold.");
            }
        }

        private static void ValidateRenderReport(JObject report)
        {
            if (!string.Equals(S(report, "status"), "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Render subject report is not ok: {S(report, "status")}");
            }
            var motion = report["motion"] as JObject;
            if (!string.Equals(S(motion, "status"), "ok", StringComparison.OrdinalIgnoreCase)
                || D(motion, "maxMotionPixelRatio") < 0.01
                || D(motion, "maxForegroundMotionRatio") < 0.2)
            {
                throw new InvalidOperationException("Render subject report does not prove visible motion.");
            }
        }

        private static JObject ExtractSimpleAnimationEvidence(JObject sourceReport, JObject mergeReport)
        {
            var summary = sourceReport["scriptAnimationComponentDiagnosticSummary"]?["simpleAnimationSemanticSummary"] as JObject;
            if (summary == null || I(summary, "automaticDefaultStateClipRows") < 1)
            {
                throw new InvalidOperationException("Source animation report does not contain automatic SimpleAnimation default-state clip evidence.");
            }

            var wantedClip = Path.GetFileNameWithoutExtension(S(mergeReport, "animationGltf"))?.Replace(".animation", string.Empty);
            var samples = (summary["automaticDefaultStateClipSamples"] as JArray)?.OfType<JObject>().ToList()
                ?? (summary["pairedClipSamples"] as JArray)?.OfType<JObject>().Where(x => x.Value<bool?>("automaticDefaultStateClip") == true).ToList()
                ?? new List<JObject>();
            var sample = samples.FirstOrDefault(x => string.Equals(S(x, "clipName"), wantedClip, StringComparison.OrdinalIgnoreCase))
                ?? samples.FirstOrDefault();
            if (sample == null)
            {
                throw new InvalidOperationException("Source animation report has no SimpleAnimation automatic default-state sample.");
            }

            return new JObject
            {
                ["script"] = "SimpleAnimation",
                ["clipName"] = S(sample, "clipName"),
                ["clipSourcePath"] = S(sample, "clipSourcePath"),
                ["clipSerializedFile"] = S(sample, "clipSerializedFile"),
                ["clipPathId"] = sample["clipPathId"],
                ["clipPathIdString"] = S(sample, "clipPathIdString"),
                ["monoBehaviourSerializedFile"] = S(sample, "monoBehaviourSerializedFile"),
                ["monoBehaviourPathId"] = sample["monoBehaviourPathId"],
                ["monoBehaviourPathIdString"] = S(sample, "monoBehaviourPathIdString"),
                ["gameObjectName"] = S(sample, "gameObjectName"),
                ["gameObjectSerializedFile"] = S(sample, "gameObjectSerializedFile"),
                ["gameObjectPathId"] = sample["gameObjectPathId"],
                ["gameObjectPathIdString"] = S(sample, "gameObjectPathIdString"),
                ["automaticDefaultStateClip"] = true,
                ["simpleAnimationTypeTree"] = sample["simpleAnimationTypeTree"]?.DeepClone(),
                ["summary"] = new JObject
                {
                    ["simpleAnimationRows"] = summary["simpleAnimationRows"],
                    ["pairedDefaultStateClipRows"] = summary["pairedDefaultStateClipRows"],
                    ["typeTreeMetadataRows"] = summary["typeTreeMetadataRows"],
                    ["typeTreeMetadataNotIndexedRows"] = summary["typeTreeMetadataNotIndexedRows"],
                    ["automaticDefaultStateClipRows"] = summary["automaticDefaultStateClipRows"],
                }
            };
        }

        private static JObject BuildMergeEvidence(JObject report)
        {
            var coverage = report["animationJointCoverage"] as JObject;
            return new JObject
            {
                ["sourceStatus"] = S(report, "status"),
                ["animationCountAdded"] = report["animationCountAdded"],
                ["channelCount"] = report["channelCount"],
                ["animatedWeightedVertexCoverage"] = coverage?["vertexWeightCoverage"]?["animatedWeightedVertexCoverage"],
                ["animatedCoreBodyJointCoverage"] = coverage?["coreBodyCoverage"]?["animatedCoreBodyJointCoverage"],
                ["rootMotionCoverage"] = coverage?["rootMotionCoverage"]?.DeepClone(),
                ["sourceAssessment"] = report["sourceAssessment"]?.DeepClone(),
            };
        }

        private static JObject BuildRenderEvidence(JObject report)
        {
            return new JObject
            {
                ["status"] = report["status"],
                ["minForegroundPixelRatio"] = report["minForegroundPixelRatio"],
                ["minForegroundHeightRatio"] = report["minForegroundHeightRatio"],
                ["motion"] = report["motion"]?.DeepClone(),
            };
        }

        private static void UpsertAnimationAssetCatalogRow(string root, string animationOutput, string clipName, string modelOutput, JObject simpleAnimationEvidence, JObject importReport)
        {
            var catalogPath = Path.Combine(root, "asset_catalog.jsonl");
            var rows = File.Exists(catalogPath)
                ? File.ReadAllLines(catalogPath).Where(x => !string.IsNullOrWhiteSpace(x)).Select(JObject.Parse).ToList()
                : new List<JObject>();
            rows.RemoveAll(x => string.Equals(S(x, "output"), animationOutput, StringComparison.OrdinalIgnoreCase));
            rows.Add(new JObject
            {
                ["kind"] = "Animation",
                ["resourceKind"] = "VerifiedAnimationPreview",
                ["animationType"] = "ModelAnimationPreviewGltf",
                ["libraryRole"] = "VerifiedAnimationPreview",
                ["name"] = clipName,
                ["sourceType"] = "SimpleAnimation",
                ["source"] = S(simpleAnimationEvidence, "clipSourcePath"),
                ["pathId"] = simpleAnimationEvidence["clipPathId"],
                ["pathIdString"] = S(simpleAnimationEvidence, "clipPathIdString"),
                ["output"] = animationOutput,
                ["format"] = "Gltf",
                ["validationStatus"] = "ok",
                ["modelOutput"] = modelOutput,
                ["relationSource"] = "explicit",
                ["confidence"] = "naraka_simple_animation_verified_preview",
                ["productionReadiness"] = "productionPreviewReady",
                ["previewOnly"] = true,
                ["embeddedModelRequired"] = true,
                ["diagnosticOnly"] = false,
                ["verifiedAnimationPreview"] = importReport.DeepClone(),
            });
            File.WriteAllText(catalogPath, string.Join(Environment.NewLine, rows.Select(x => x.ToString(Formatting.None))) + Environment.NewLine);
        }

        private static void UpsertCompactModelAnimationCandidate(string root, string modelOutput, string animationOutput, string clipName, JObject simpleAnimationEvidence, JObject importReport)
        {
            var compactPath = Path.Combine(root, "model_animations.compact.json");
            if (!File.Exists(compactPath))
            {
                throw new FileNotFoundException("model_animations.compact.json not found; rebuild the Library before applying verified animation previews.", compactPath);
            }

            var compact = JObject.Parse(File.ReadAllText(compactPath));
            var models = EnsureArray(compact, "models");
            var animations = EnsureArray(compact, "animations");
            var refs = EnsureArray(compact, "modelAnimationRefs");
            var model = models.OfType<JObject>().FirstOrDefault(x => string.Equals(S(x, "output"), modelOutput, StringComparison.OrdinalIgnoreCase));
            if (model == null)
            {
                throw new InvalidOperationException($"model_animations.compact.json does not contain model output: {modelOutput}");
            }

            var modelId = S(model, "id");
            var animation = animations.OfType<JObject>().FirstOrDefault(x => string.Equals(S(x, "output"), animationOutput, StringComparison.OrdinalIgnoreCase));
            var animationId = animation == null ? $"verifiedPreview{animations.Count}" : S(animation, "id");
            if (animation == null)
            {
                animation = new JObject
                {
                    ["id"] = animationId,
                    ["name"] = clipName,
                    ["resourceKind"] = "VerifiedAnimationPreview",
                    ["output"] = animationOutput,
                    ["source"] = S(simpleAnimationEvidence, "clipSourcePath"),
                    ["pathId"] = simpleAnimationEvidence["clipPathId"],
                    ["animationType"] = "ModelAnimationPreviewGltf",
                    ["directTrsAnimationReady"] = true,
                    ["verifiedPreview"] = true,
                };
                animations.Add(animation);
            }

            var modelRef = refs.OfType<JObject>().FirstOrDefault(x => string.Equals(S(x, "modelId"), modelId, StringComparison.OrdinalIgnoreCase));
            if (modelRef == null)
            {
                modelRef = new JObject
                {
                    ["modelId"] = modelId,
                    ["candidateCount"] = 0,
                    ["usableCandidateCount"] = 0,
                    ["modelReadyForAnimation"] = model.Value<bool?>("modelReadyForAnimation") ?? true,
                    ["modelAnimationGate"] = model["modelAnimationGate"]?.DeepClone(),
                    ["candidates"] = new JArray(),
                };
                refs.Add(modelRef);
            }

            var candidates = EnsureArray(modelRef, "candidates");
            RemoveMatchingObjects(candidates, x => string.Equals(S(x, "animationId"), animationId, StringComparison.OrdinalIgnoreCase));
            candidates.Add(new JObject
            {
                ["animationId"] = animationId,
                ["name"] = clipName,
                ["output"] = animationOutput,
                ["relationSource"] = "explicit",
                ["confidence"] = "naraka_simple_animation_verified_preview",
                ["score"] = 95,
                ["status"] = "explicit",
                ["modelReadyForAnimation"] = true,
                ["defaultAnimationCandidateReady"] = true,
                ["explicitCandidateRequiresVisualValidation"] = false,
                ["validationStatus"] = "ok",
                ["productionReadiness"] = "productionPreviewReady",
                ["relationshipKind"] = "NarakaSimpleAnimationVerifiedPreview",
                ["recommendedUse"] = "defaultTrustedPreview",
                ["previewOnly"] = true,
                ["embeddedModelRequired"] = true,
                ["simpleAnimationEvidence"] = simpleAnimationEvidence.DeepClone(),
                ["verifiedAnimationPreview"] = importReport.DeepClone(),
            });
            modelRef["candidateCount"] = candidates.Count;
            modelRef["usableCandidateCount"] = candidates.OfType<JObject>().Count(x => x.Value<bool?>("modelReadyForAnimation") != false && x.Value<bool?>("defaultAnimationCandidateReady") != false);

            ReplaceCompactCapabilitySummary(compact, animations);
            File.WriteAllText(compactPath, compact.ToString(Formatting.Indented));
        }

        private static void ReplaceCompactCapabilitySummary(JObject compact, JArray animations)
        {
            var oldSummary = compact["capabilitySummary"] as JObject;
            var verifiedPreviewCount = animations
                .OfType<JObject>()
                .Count(x => string.Equals(S(x, "resourceKind"), "VerifiedAnimationPreview", StringComparison.OrdinalIgnoreCase));
            compact["capabilitySummary"] = new JObject
            {
                ["animationCapabilities"] = new JObject
                {
                    ["totalAnimationAssets"] = animations.Count,
                    ["VerifiedAnimationPreview"] = verifiedPreviewCount
                },
                ["modelCapabilities"] = oldSummary?["modelCapabilities"]?.DeepClone() ?? new JObject(),
                ["rule"] = oldSummary?["rule"]?.DeepClone()
                    ?? "Humanoid body motion, Transform body motion, BlendShape/face motion, non-character Transform motion, and legacy/material/event motion are separate implementation paths."
            };
        }

        private static void RemoveMatchingObjects(JArray array, Func<JObject, bool> predicate)
        {
            for (var i = array.Count - 1; i >= 0; i--)
            {
                if (array[i] is JObject obj && predicate(obj))
                {
                    array.RemoveAt(i);
                }
            }
        }

        private static void CopyMergedPreviewFolder(string mergedGltf, string targetDir)
        {
            var sourceDir = Path.GetDirectoryName(mergedGltf) ?? throw new InvalidOperationException("Merged glTF has no source directory.");
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, file);
                if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(x => string.Equals(x, "RenderProbe", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var target = Path.Combine(targetDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target) ?? targetDir);
                File.Copy(file, target, overwrite: true);
            }
        }

        private static JArray EnsureArray(JObject obj, string name)
        {
            if (obj[name] is JArray array)
            {
                return array;
            }

            array = new JArray();
            obj[name] = array;
            return array;
        }

        private static string FullPath(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);
        }

        private static string MakeRelativeIfInside(string root, string path)
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
                ? MakeRelative(root, fullPath)
                : fullPath;
        }

        private static string MakeRelative(string root, string path)
        {
            return Path.GetRelativePath(root, path).Replace('\\', '/');
        }

        private static string SafeName(string value)
        {
            value = string.IsNullOrWhiteSpace(value) ? "animation" : value.Trim();
            return Regex.Replace(value, @"[^A-Za-z0-9_.-]+", "_").Trim('_');
        }

        private static string S(JObject obj, string name)
        {
            return obj?[name]?.Type == JTokenType.Null ? null : obj?[name]?.ToString();
        }

        private static long I(JObject obj, string name)
        {
            return obj?[name]?.Value<long?>() ?? 0;
        }

        private static double D(JObject obj, string name)
        {
            return obj?[name]?.Value<double?>() ?? 0;
        }
    }
}
