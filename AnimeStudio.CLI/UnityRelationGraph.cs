using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace AnimeStudio.CLI
{
    internal static class UnityRelationGraph
    {
        public static void Generate(string savePath, AssetsManager assetsManager, IEnumerable<AssetItem> exportableAssets)
        {
            if (string.IsNullOrWhiteSpace(savePath) || assetsManager?.assetsFileList == null)
            {
                return;
            }

            var outputPath = Path.Combine(savePath, "unity_relations.jsonl");
            var exportedByObject = (exportableAssets ?? Enumerable.Empty<AssetItem>())
                .Where(x => x?.Asset != null)
                .GroupBy(x => x.Asset)
                .ToDictionary(x => x.Key, x => x.First());

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            var stats = new GraphStats();
            using var writer = new StreamWriter(outputPath, false);
            foreach (var obj in assetsManager.assetsFileList.SelectMany(x => x.Objects))
            {
                WriteAsset(writer, obj, exportedByObject, stats);
                WriteRelations(writer, obj, stats);
            }

            WriteSummary(savePath, outputPath, stats);
        }

        private static void WriteAsset(StreamWriter writer, AnimeStudio.Object obj, Dictionary<AnimeStudio.Object, AssetItem> exportedByObject, GraphStats stats)
        {
            if (obj == null)
            {
                return;
            }

            exportedByObject.TryGetValue(obj, out var item);
            stats.AssetCount++;
            stats.Increment(stats.AssetTypes, obj.type.ToString());
            if (item != null)
            {
                stats.ExportedAssetCount++;
                stats.Increment(stats.ExportedAssetTypes, obj.type.ToString());
            }

            writer.WriteLine(JsonConvert.SerializeObject(new
            {
                kind = "asset",
                asset = Describe(obj, item),
                exported = item != null,
                container = item?.Container,
            }));
        }

        private static void WriteRelations(StreamWriter writer, AnimeStudio.Object obj, GraphStats stats)
        {
            switch (obj)
            {
                case GameObject gameObject:
                    WriteGameObjectRelations(writer, gameObject, stats);
                    break;
                case Transform transform:
                    WriteTransformRelations(writer, transform, stats);
                    break;
                case Animator animator:
                    WriteAnimatorRelations(writer, animator, stats);
                    break;
                case Animation animation:
                    WriteAnimationRelations(writer, animation, stats);
                    break;
                case AnimatorController controller:
                    WriteAnimatorControllerRelations(writer, controller, stats);
                    break;
                case AnimatorOverrideController overrideController:
                    WriteAnimatorOverrideControllerRelations(writer, overrideController, stats);
                    break;
                case SkinnedMeshRenderer skinnedMeshRenderer:
                    WriteSkinnedMeshRendererRelations(writer, skinnedMeshRenderer, stats);
                    break;
                case MeshFilter meshFilter:
                    WriteMeshFilterRelations(writer, meshFilter, stats);
                    break;
                case Renderer renderer:
                    WriteRendererRelations(writer, renderer, stats);
                    break;
                case AnimationClip clip:
                    WriteAnimationClipBindings(writer, clip, stats);
                    break;
            }
        }

        private static void WriteGameObjectRelations(StreamWriter writer, GameObject gameObject, GraphStats stats)
        {
            foreach (var componentPtr in gameObject.m_Components ?? Enumerable.Empty<PPtr<Component>>())
            {
                if (componentPtr.TryGet(out var component))
                {
                    WriteRelation(writer, stats, "gameObject.component", gameObject, component, "explicit", new
                    {
                        componentType = component.type.ToString(),
                    });
                }
            }
        }

        private static void WriteTransformRelations(StreamWriter writer, Transform transform, GraphStats stats)
        {
            if (transform.m_GameObject.TryGet(out var gameObject))
            {
                WriteRelation(writer, stats, "component.gameObject", transform, gameObject, "explicit");
            }

            if (transform.m_Father != null && transform.m_Father.TryGet(out var father))
            {
                WriteRelation(writer, stats, "transform.parent", transform, father, "explicit");
            }

            foreach (var childPtr in transform.m_Children ?? Enumerable.Empty<PPtr<Transform>>())
            {
                if (childPtr.TryGet(out var child))
                {
                    WriteRelation(writer, stats, "transform.child", transform, child, "explicit");
                }
            }
        }

        private static void WriteAnimatorRelations(StreamWriter writer, Animator animator, GraphStats stats)
        {
            stats.AnimatorCount++;
            if (animator.m_GameObject.TryGet(out var gameObject))
            {
                WriteRelation(writer, stats, "component.gameObject", animator, gameObject, "explicit");
            }

            if (animator.m_Avatar.TryGet(out var avatar))
            {
                stats.AnimatorWithAvatarCount++;
                WriteRelation(writer, stats, "animator.avatar", animator, avatar, "explicit", new
                {
                    hasHumanDescription = avatar.m_HumanDescription != null,
                    humanBoneCount = avatar.m_HumanDescription?.m_Human?.Count ?? 0,
                    skeletonBoneCount = avatar.m_HumanDescription?.m_Skeleton?.Count ?? 0,
                });
            }

            if (animator.m_Controller.TryGet<RuntimeAnimatorController>(out var controller))
            {
                stats.AnimatorWithControllerCount++;
                WriteRelation(writer, stats, "animator.controller", animator, controller, "explicit", new
                {
                    controllerType = controller.type.ToString(),
                });
            }
        }

        private static void WriteAnimationRelations(StreamWriter writer, Animation animation, GraphStats stats)
        {
            if (animation.m_GameObject.TryGet(out var gameObject))
            {
                WriteRelation(writer, stats, "component.gameObject", animation, gameObject, "explicit");
            }

            foreach (var clipPtr in animation.m_Animations ?? Enumerable.Empty<PPtr<AnimationClip>>())
            {
                if (clipPtr.TryGet(out var clip))
                {
                    WriteRelation(writer, stats, "animation.clip", animation, clip, "explicit");
                }
            }
        }

        private static void WriteAnimatorControllerRelations(StreamWriter writer, AnimatorController controller, GraphStats stats)
        {
            stats.AnimatorControllerCount++;
            foreach (var clipPtr in controller.m_AnimationClips ?? Enumerable.Empty<PPtr<AnimationClip>>())
            {
                if (clipPtr.TryGet(out var clip))
                {
                    WriteRelation(writer, stats, "animatorController.clip", controller, clip, "explicit");
                }
            }
        }

        private static void WriteAnimatorOverrideControllerRelations(StreamWriter writer, AnimatorOverrideController controller, GraphStats stats)
        {
            if (controller.m_Controller.TryGet<RuntimeAnimatorController>(out var baseController))
            {
                WriteRelation(writer, stats, "animatorOverrideController.baseController", controller, baseController, "explicit");
            }

            foreach (var clip in controller.m_Clips ?? Enumerable.Empty<AnimationClipOverride>())
            {
                var original = clip.m_OriginalClip.TryGet(out var originalClip) ? originalClip : null;
                var replacement = clip.m_OverrideClip.TryGet(out var overrideClip) ? overrideClip : null;
                stats.RelationCount++;
                stats.Increment(stats.RelationTypes, "animatorOverrideController.clipOverride");
                writer.WriteLine(JsonConvert.SerializeObject(new
                {
                    kind = "relation",
                    relation = "animatorOverrideController.clipOverride",
                    confidence = "explicit",
                    from = Describe(controller),
                    original = Describe(original),
                    @override = Describe(replacement),
                }));
            }
        }

        private static void WriteSkinnedMeshRendererRelations(StreamWriter writer, SkinnedMeshRenderer renderer, GraphStats stats)
        {
            if (renderer.m_GameObject.TryGet(out var gameObject))
            {
                WriteRelation(writer, stats, "component.gameObject", renderer, gameObject, "explicit");
            }

            if (renderer.m_Mesh.TryGet(out var mesh))
            {
                WriteRelation(writer, stats, "skinnedMeshRenderer.mesh", renderer, mesh, "explicit");
            }

            if (renderer.m_RootBone != null && renderer.m_RootBone.TryGet(out var rootBone))
            {
                WriteRelation(writer, stats, "skinnedMeshRenderer.rootBone", renderer, rootBone, "explicit");
            }

            var bones = (renderer.m_Bones ?? new List<PPtr<Transform>>())
                .Select(x => x.TryGet(out var bone) ? Describe(bone) : null)
                .Where(x => x != null)
                .ToArray();
            stats.RelationCount++;
            stats.Increment(stats.RelationTypes, "skinnedMeshRenderer.bones");
            stats.SkinnedMeshRendererWithBonesCount++;
            writer.WriteLine(JsonConvert.SerializeObject(new
            {
                kind = "relation",
                relation = "skinnedMeshRenderer.bones",
                confidence = "explicit",
                from = Describe(renderer),
                count = bones.Length,
                targets = bones.Take(256).ToArray(),
                truncated = bones.Length > 256,
            }));

            WriteRendererRelations(writer, renderer, stats);
        }

        private static void WriteMeshFilterRelations(StreamWriter writer, MeshFilter meshFilter, GraphStats stats)
        {
            if (meshFilter.m_GameObject.TryGet(out var gameObject))
            {
                WriteRelation(writer, stats, "component.gameObject", meshFilter, gameObject, "explicit");
            }

            if (meshFilter.m_Mesh.TryGet(out var mesh))
            {
                WriteRelation(writer, stats, "meshFilter.mesh", meshFilter, mesh, "explicit");
            }
        }

        private static void WriteRendererRelations(StreamWriter writer, Renderer renderer, GraphStats stats)
        {
            if (renderer.m_GameObject.TryGet(out var gameObject))
            {
                WriteRelation(writer, stats, "component.gameObject", renderer, gameObject, "explicit");
            }

            foreach (var materialPtr in renderer.m_Materials ?? Enumerable.Empty<PPtr<Material>>())
            {
                if (materialPtr.TryGet(out var material))
                {
                    WriteRelation(writer, stats, "renderer.material", renderer, material, "explicit");
                }
            }
        }

        private static void WriteAnimationClipBindings(StreamWriter writer, AnimationClip clip, GraphStats stats)
        {
            var bindings = clip.m_ClipBindingConstant?.genericBindings ?? new List<GenericBinding>();
            var tos = clip.FindTOS();
            var bindingEntries = bindings
                .Select(x => new
                {
                    path = tos != null && tos.TryGetValue(x.path, out var path) ? path : null,
                    type = x.typeID.ToString(),
                    attribute = x.attribute,
                    customType = ((BindingCustomType)x.customType).ToString(),
                    isPPtrCurve = x.isPPtrCurve == 1,
                })
                .ToArray();

            stats.AnimationClipCount++;
            stats.AnimationBindingEntryCount += bindingEntries.Length;
            if (clip.m_MuscleClip != null)
            {
                stats.AnimationClipWithMuscleCount++;
            }

            writer.WriteLine(JsonConvert.SerializeObject(new
            {
                kind = "animationBindings",
                confidence = "structural",
                animation = Describe(clip),
                hasMuscleClip = clip.m_MuscleClip != null,
                bindingCount = bindingEntries.Length,
                bindings = bindingEntries.Take(512).ToArray(),
                truncated = bindingEntries.Length > 512,
            }));
        }

        private static void WriteRelation(StreamWriter writer, GraphStats stats, string relation, AnimeStudio.Object from, AnimeStudio.Object to, string confidence, object details = null)
        {
            stats.RelationCount++;
            stats.Increment(stats.RelationTypes, relation);
            writer.WriteLine(JsonConvert.SerializeObject(new
            {
                kind = "relation",
                relation,
                confidence,
                from = Describe(from),
                to = Describe(to),
                details,
            }));
        }

        private static object Describe(AnimeStudio.Object obj, AssetItem item = null)
        {
            if (obj == null)
            {
                return null;
            }

            return new
            {
                type = obj.type.ToString(),
                name = string.IsNullOrWhiteSpace(obj.Name) ? item?.Text : obj.Name,
                source = obj.assetsFile?.originalPath ?? obj.assetsFile?.fileName,
                file = obj.assetsFile?.fileName,
                pathId = obj.m_PathID,
                container = item?.Container,
            };
        }

        private static void WriteSummary(string savePath, string relationPath, GraphStats stats)
        {
            var summary = new
            {
                generatedAt = DateTime.UtcNow.ToString("O"),
                relationGraph = relationPath,
                note = "Unity 原生关系图摘要。model_animations.json 在完全迁移前仍可能包含低优先级启发式候选；动画绑定应优先从 unity_relations.jsonl 派生并通过 preview 验证。",
                assets = new
                {
                    total = stats.AssetCount,
                    exported = stats.ExportedAssetCount,
                    byType = stats.AssetTypes.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value),
                    exportedByType = stats.ExportedAssetTypes.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value),
                },
                relations = new
                {
                    total = stats.RelationCount,
                    byType = stats.RelationTypes.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value),
                },
                animation = new
                {
                    clipCount = stats.AnimationClipCount,
                    clipWithMuscleCount = stats.AnimationClipWithMuscleCount,
                    bindingEntryCount = stats.AnimationBindingEntryCount,
                },
                animator = new
                {
                    count = stats.AnimatorCount,
                    withAvatar = stats.AnimatorWithAvatarCount,
                    withController = stats.AnimatorWithControllerCount,
                    controllerCount = stats.AnimatorControllerCount,
                },
                skin = new
                {
                    skinnedMeshRendererWithBones = stats.SkinnedMeshRendererWithBonesCount,
                },
                nextUse = new[]
                {
                    "用 animator.controller / animatorController.clip / animation.clip 建立显式动画候选。",
                    "用 animator.avatar、AnimationClip binding 和 SkinnedMeshRenderer bones 做结构兼容验证。",
                    "低优先级路径/名称候选只能补充缺失关系，不能覆盖 Unity 显式关系。",
                },
            };

            File.WriteAllText(
                Path.Combine(savePath, "unity_relation_summary.json"),
                JsonConvert.SerializeObject(summary, Newtonsoft.Json.Formatting.Indented)
            );
        }

        private sealed class GraphStats
        {
            public int AssetCount { get; set; }
            public int ExportedAssetCount { get; set; }
            public int RelationCount { get; set; }
            public int AnimatorCount { get; set; }
            public int AnimatorWithAvatarCount { get; set; }
            public int AnimatorWithControllerCount { get; set; }
            public int AnimatorControllerCount { get; set; }
            public int AnimationClipCount { get; set; }
            public int AnimationClipWithMuscleCount { get; set; }
            public int AnimationBindingEntryCount { get; set; }
            public int SkinnedMeshRendererWithBonesCount { get; set; }
            public Dictionary<string, int> AssetTypes { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, int> ExportedAssetTypes { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, int> RelationTypes { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            public void Increment(Dictionary<string, int> values, string key)
            {
                key = string.IsNullOrWhiteSpace(key) ? "Unknown" : key;
                values.TryGetValue(key, out var count);
                values[key] = count + 1;
            }
        }
    }
}
