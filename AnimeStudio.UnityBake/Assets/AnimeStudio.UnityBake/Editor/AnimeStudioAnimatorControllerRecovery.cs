using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace AnimeStudio.UnityBake
{
    public static class AnimeStudioAnimatorControllerRecovery
    {
        public static AnimatorControllerRecoveryResult Run(string requestPath)
        {
            AnimatorControllerRecoveryRequest request = null;
            try
            {
                request = JsonUtility.FromJson<AnimatorControllerRecoveryRequest>(File.ReadAllText(requestPath));
                if (request == null)
                {
                    return Fail(null, "Unable to parse AnimatorController recovery request.");
                }

                Directory.CreateDirectory("Assets/AnimeStudioBake/ImportedAnimatorController");
                var assetPath = NormalizeAssetPath(request.controllerAssetPath);
                if (string.IsNullOrWhiteSpace(assetPath))
                {
                    assetPath = $"Assets/AnimeStudioBake/ImportedAnimatorController/{SanitizeAssetName(request.controllerName)}.controller";
                }

                if (File.Exists(assetPath) && !request.force)
                {
                    return Write(request, new AnimatorControllerRecoveryResult
                    {
                        status = "already_exists",
                        controllerName = request.controllerName,
                        controllerAssetPath = assetPath,
                        message = "AnimatorController asset already exists.",
                    });
                }

                if (File.Exists(assetPath))
                {
                    AssetDatabase.DeleteAsset(assetPath);
                }

                var controller = AnimatorController.CreateAnimatorControllerAtPath(assetPath);
                if (controller == null)
                {
                    return Fail(request, "Unity failed to create AnimatorController asset.");
                }

                controller.name = string.IsNullOrWhiteSpace(request.controllerName)
                    ? Path.GetFileNameWithoutExtension(assetPath)
                    : request.controllerName;

                var clipsByPathId = LoadClips(request);
                var warnings = new List<string>();
                var createdLayers = 0;
                var createdStates = 0;
                var createdBlendTrees = 0;
                var createdAvatarMasks = 0;
                var avatarMaskPathCount = 0;
                var skippedAvatarMaskPathCount = 0;
                var missingClipCount = 0;
                var layerMetadata = new List<AnimatorControllerRecoveryLayerMetadata>();

                foreach (var layerSpec in (request.layers ?? Array.Empty<AnimatorControllerRecoveryLayer>())
                    .OrderBy(x => x.index))
                {
                    var machine = (request.stateMachines ?? Array.Empty<AnimatorControllerRecoveryStateMachine>())
                        .FirstOrDefault(x => x.machineIndex == layerSpec.stateMachineIndex);
                    if (machine == null)
                    {
                        warnings.Add($"Layer {layerSpec.index} skipped: missing state machine {layerSpec.stateMachineIndex}.");
                        continue;
                    }

                    var stateSpec = (machine.states ?? Array.Empty<AnimatorControllerRecoveryState>())
                        .FirstOrDefault(x => x.stateIndex == machine.defaultState);
                    if (stateSpec == null)
                    {
                        warnings.Add($"Layer {layerSpec.index} skipped: missing default state {machine.defaultState}.");
                        continue;
                    }

                    var stateName = SanitizeAnimatorName(FirstNonEmpty(stateSpec.fullPath, stateSpec.name, $"State{stateSpec.stateIndex}"));
                    var motion = BuildMotion(controller, stateSpec, clipsByPathId, warnings, ref createdBlendTrees, ref missingClipCount);
                    if (motion == null && layerSpec.index != 0)
                    {
                        // 空的 override/additive 层权重通常是 1，照样写进 controller 会把下层动作盖掉。
                        // 目前只恢复有明确 motion 的默认层；无 motion 的非 base layer 先跳过并写诊断。
                        warnings.Add($"Layer {layerSpec.index} state {stateName} skipped: no recoverable default motion.");
                        continue;
                    }

                    var layer = EnsureLayer(controller, layerSpec, stateSpec, createdLayers);
                    var maskResult = ApplyRecoveredLayerAvatarMask(
                        controller,
                        layerSpec,
                        createdLayers,
                        layer,
                        warnings,
                        ref createdAvatarMasks,
                        ref avatarMaskPathCount,
                        ref skippedAvatarMaskPathCount);
                    var stateMachine = layer.stateMachine;
                    var state = stateMachine.AddState(stateName);
                    state.speed = Mathf.Approximately(stateSpec.speed, 0f) ? 1f : stateSpec.speed;
                    state.cycleOffset = stateSpec.cycleOffset;
                    state.mirror = stateSpec.mirror;
                    state.iKOnFeet = stateSpec.iKOnFeet;
                    state.writeDefaultValues = true;
                    state.motion = motion;
                    stateMachine.defaultState = state;
                    layerMetadata.Add(new AnimatorControllerRecoveryLayerMetadata
                    {
                        sourceLayerIndex = layerSpec.index,
                        recoveredLayerIndex = createdLayers,
                        recoveredLayerName = layer.name,
                        stateName = stateName,
                        sourceStateMachineIndex = layerSpec.stateMachineIndex,
                        stateMachineMotionSetIndex = layerSpec.stateMachineMotionSetIndex,
                        binding = layerSpec.binding,
                        blendingMode = layerSpec.blendingMode,
                        defaultWeight = createdLayers == 0 ? 1f : Mathf.Max(0f, layerSpec.defaultWeight),
                        iKPass = layerSpec.iKPass,
                        iKOnFeet = stateSpec.iKOnFeet,
                        syncedLayerAffectsTiming = layerSpec.syncedLayerAffectsTiming,
                        recoveredAvatarMask = maskResult.Created,
                        recoveredAvatarMaskPathCount = maskResult.PathCount,
                        skippedAvatarMaskPathCount = maskResult.SkippedPathCount,
                        bodyMask = layerSpec.bodyMask,
                        skeletonMask = layerSpec.skeletonMask,
                    });
                    createdLayers++;
                    createdStates++;
                }

                var parameterMetadata = AddParameters(controller, request, warnings);
                EditorUtility.SetDirty(controller);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                var metadataAssetPath = WriteLayerMetadata(assetPath, request, layerMetadata, parameterMetadata);

                return Write(request, new AnimatorControllerRecoveryResult
                {
                    status = warnings.Count == 0 ? "ok" : "warning",
                    controllerName = controller.name,
                    controllerAssetPath = assetPath,
                    createdLayerCount = createdLayers,
                    createdStateCount = createdStates,
                    createdBlendTreeCount = createdBlendTrees,
                    createdAvatarMaskCount = createdAvatarMasks,
                    recoveredAvatarMaskPathCount = avatarMaskPathCount,
                    skippedAvatarMaskPathCount = skippedAvatarMaskPathCount,
                    layerMetadataAssetPath = metadataAssetPath,
                    clipAssetCount = clipsByPathId.Count,
                    missingClipCount = missingClipCount,
                    warningCount = warnings.Count,
                    warnings = warnings.ToArray(),
                    message = "Recovered a default-state AnimatorController from deterministic inspect data. Transitions, conditions, complex BlendTrees, and runtime parameters are not claimed as fully restored.",
                });
            }
            catch (Exception e)
            {
                return Fail(request, e.ToString());
            }
        }

        private static string WriteLayerMetadata(
            string controllerAssetPath,
            AnimatorControllerRecoveryRequest request,
            IReadOnlyList<AnimatorControllerRecoveryLayerMetadata> layers,
            IReadOnlyList<AnimatorControllerRecoveryParameterMetadata> parameters)
        {
            if (string.IsNullOrWhiteSpace(controllerAssetPath))
            {
                return null;
            }

            var metadataAssetPath = controllerAssetPath + ".animeStudioControllerRecovery.json";
            var metadata = new AnimatorControllerRecoveryMetadata
            {
                controllerName = request.controllerName,
                controllerAssetPath = controllerAssetPath,
                layers = (layers ?? Array.Empty<AnimatorControllerRecoveryLayerMetadata>()).ToArray(),
                parameters = (parameters ?? Array.Empty<AnimatorControllerRecoveryParameterMetadata>()).ToArray(),
                rule = "diagnostic_only: preserves original AnimatorController layer masks from unity_file_inspect so bake worker can apply masks on the target model hierarchy.",
            };
            File.WriteAllText(metadataAssetPath, JsonUtility.ToJson(metadata, true));
            AssetDatabase.ImportAsset(metadataAssetPath);
            return metadataAssetPath;
        }

        private static AnimatorControllerLayer EnsureLayer(AnimatorController controller, AnimatorControllerRecoveryLayer spec, AnimatorControllerRecoveryState state, int createdLayers)
        {
            if (createdLayers == 0)
            {
                var layer = controller.layers[0];
                layer.name = SanitizeAnimatorName(FirstNonEmpty(FirstPathSegment(state.fullPath), $"Layer{spec.index}"));
                // Unity 序列化里 base layer 可能写 0，但运行时主层仍应生效。
                // 恢复成真实 Controller 时不能照抄成 0，否则默认动作会被完全压掉。
                layer.defaultWeight = 1f;
                layer.blendingMode = spec.blendingMode == 1 ? AnimatorLayerBlendingMode.Additive : AnimatorLayerBlendingMode.Override;
                var layers = controller.layers;
                layers[0] = layer;
                controller.layers = layers;
                return controller.layers[0];
            }

            controller.AddLayer(SanitizeAnimatorName(FirstNonEmpty(FirstPathSegment(state.fullPath), $"Layer{spec.index}")));
            var all = controller.layers;
            var added = all[all.Length - 1];
            added.defaultWeight = Mathf.Max(0f, spec.defaultWeight);
            added.blendingMode = spec.blendingMode == 1 ? AnimatorLayerBlendingMode.Additive : AnimatorLayerBlendingMode.Override;
            all[all.Length - 1] = added;
            controller.layers = all;
            return controller.layers[controller.layers.Length - 1];
        }

        private static LayerAvatarMaskRecoveryResult ApplyRecoveredLayerAvatarMask(
            AnimatorController controller,
            AnimatorControllerRecoveryLayer spec,
            int recoveredLayerIndex,
            AnimatorControllerLayer layer,
            List<string> warnings,
            ref int createdAvatarMasks,
            ref int avatarMaskPathCount,
            ref int skippedAvatarMaskPathCount)
        {
            var result = new LayerAvatarMaskRecoveryResult();
            if (controller == null || spec?.skeletonMask?.entries == null || layer == null)
            {
                return result;
            }

            var paths = new List<string>();
            foreach (var entry in spec.skeletonMask.entries)
            {
                if (entry == null || entry.weight <= 0.0001f)
                {
                    continue;
                }

                var path = NormalizeMaskPath(entry.path);
                if (string.IsNullOrWhiteSpace(path))
                {
                    result.SkippedPathCount++;
                    continue;
                }

                paths.Add(path);
            }

            if (paths.Count == 0)
            {
                skippedAvatarMaskPathCount += result.SkippedPathCount;
                if (result.SkippedPathCount > 0)
                {
                    warnings.Add($"Layer {spec.index} has {result.SkippedPathCount} skeleton-mask entry/entries without transform path; AvatarMask was not recovered because hash-only paths cannot be assigned to AnimatorControllerLayer.avatarMask deterministically.");
                }
                return result;
            }

            var uniquePaths = paths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var mask = new AvatarMask
            {
                name = SanitizeAnimatorName(FirstNonEmpty(layer.name, $"Layer{spec.index}")) + "_AnimeStudioMask",
            };
            mask.transformCount = uniquePaths.Length;
            for (var i = 0; i < uniquePaths.Length; i++)
            {
                mask.SetTransformPath(i, uniquePaths[i]);
                mask.SetTransformActive(i, true);
            }

            // 把 mask 作为 controller 子资产保存，后续 AnimatorControllerPlayable
            // 可以直接使用 Unity 原生 layer.avatarMask，而不是只靠外部 layer mixer。
            AssetDatabase.AddObjectToAsset(mask, controller);
            layer.avatarMask = mask;
            var layers = controller.layers;
            if (recoveredLayerIndex >= 0 && recoveredLayerIndex < layers.Length)
            {
                layers[recoveredLayerIndex] = layer;
                controller.layers = layers;
            }

            createdAvatarMasks++;
            result.Created = true;
            result.PathCount = uniquePaths.Length;
            avatarMaskPathCount += uniquePaths.Length;
            skippedAvatarMaskPathCount += result.SkippedPathCount;
            if (result.SkippedPathCount > 0)
            {
                warnings.Add($"Layer {spec.index} recovered AvatarMask with {uniquePaths.Length} transform path(s), skipped {result.SkippedPathCount} hash-only skeleton-mask entry/entries.");
            }
            return result;
        }

        private static Motion BuildMotion(
            AnimatorController controller,
            AnimatorControllerRecoveryState state,
            IReadOnlyDictionary<long, AnimationClip> clipsByPathId,
            List<string> warnings,
            ref int createdBlendTrees,
            ref int missingClipCount)
        {
            var tree = (state.blendTrees ?? Array.Empty<AnimatorControllerRecoveryBlendTree>()).FirstOrDefault();
            var nodes = tree?.nodes ?? Array.Empty<AnimatorControllerRecoveryNode>();
            if (nodes.Length == 0)
            {
                return null;
            }

            var leafClips = new List<AnimationClip>();
            foreach (var node in nodes.Where(x => x.clipPathId != 0))
            {
                var clip = ResolveClip(clipsByPathId, node.clipPathId, ref missingClipCount);
                if (clip != null)
                {
                    leafClips.Add(clip);
                }
            }
            var hasCompositeNode = nodes.Any(x => x.childIndices != null && x.childIndices.Length > 0);
            if (leafClips.Count == 1 && !hasCompositeNode)
            {
                return leafClips[0];
            }

            var root = nodes.FirstOrDefault(x => x.nodeIndex == 0)
                ?? nodes.FirstOrDefault(x => x.childIndices != null && x.childIndices.Length > 0);
            if (root == null || string.IsNullOrWhiteSpace(root.blendEvent))
            {
                warnings.Add($"State {state.fullPath} skipped complex BlendTree: root node has no recoverable Simple1D parameter.");
                return null;
            }

            var thresholds = root.blend1dChildThresholds != null && root.blend1dChildThresholds.Length == root.childIndices.Length
                ? root.blend1dChildThresholds
                : root.childThresholds;
            if (thresholds == null || thresholds.Length != root.childIndices.Length)
            {
                warnings.Add($"State {state.fullPath} skipped BlendTree {root.blendEvent}: threshold count does not match children.");
                return null;
            }

            var blendTree = new BlendTree
            {
                name = SanitizeAnimatorName(FirstNonEmpty(state.fullPath, state.name, "BlendTree")),
                blendType = BlendTreeType.Simple1D,
                blendParameter = root.blendEvent,
                useAutomaticThresholds = false,
            };
            AssetDatabase.AddObjectToAsset(blendTree, controller);
            createdBlendTrees++;

            var byIndex = nodes.ToDictionary(x => x.nodeIndex, x => x);
            var addedChildren = 0;
            for (var i = 0; i < root.childIndices.Length; i++)
            {
                if (!byIndex.TryGetValue(root.childIndices[i], out var child) || child.clipPathId == 0)
                {
                    continue;
                }

                var clip = ResolveClip(clipsByPathId, child.clipPathId, ref missingClipCount);
                if (clip == null)
                {
                    continue;
                }

                blendTree.AddChild(clip, thresholds[i]);
                addedChildren++;
            }

            if (addedChildren == 0)
            {
                warnings.Add($"State {state.fullPath} BlendTree {root.blendEvent} has no loadable child clips.");
                return null;
            }

            warnings.Add($"State {state.fullPath} recovered Simple1D BlendTree {root.blendEvent}; default parameter value is Unity default 0 until original parameters are recovered.");
            return blendTree;
        }

        private static AnimationClip ResolveClip(IReadOnlyDictionary<long, AnimationClip> clipsByPathId, long pathId, ref int missingClipCount)
        {
            if (clipsByPathId.TryGetValue(pathId, out var clip) && clip != null)
            {
                return clip;
            }

            missingClipCount++;
            return null;
        }

        private static Dictionary<long, AnimationClip> LoadClips(AnimatorControllerRecoveryRequest request)
        {
            var result = new Dictionary<long, AnimationClip>();
            foreach (var clip in request.clips ?? Array.Empty<AnimatorControllerRecoveryClip>())
            {
                if (clip.pathId == 0 || string.IsNullOrWhiteSpace(clip.unityAssetPath))
                {
                    continue;
                }

                var assetPath = NormalizeAssetPath(clip.unityAssetPath);
                var asset = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
                if (asset != null)
                {
                    result[clip.pathId] = asset;
                }
            }
            return result;
        }

        private static List<AnimatorControllerRecoveryParameterMetadata> AddParameters(AnimatorController controller, AnimatorControllerRecoveryRequest request, List<string> warnings)
        {
            var defaults = new Dictionary<string, float>(StringComparer.Ordinal);
            var sources = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var parameter in request.parameters ?? Array.Empty<AnimatorControllerRecoveryParameterMetadata>())
            {
                if (!string.IsNullOrWhiteSpace(parameter.name) && !defaults.ContainsKey(parameter.name))
                {
                    defaults[parameter.name] = parameter.defaultFloat;
                    sources[parameter.name] = string.IsNullOrWhiteSpace(parameter.source)
                        ? "controllerDefaultValue"
                        : parameter.source;
                }
            }

            foreach (var state in (request.stateMachines ?? Array.Empty<AnimatorControllerRecoveryStateMachine>())
                .SelectMany(x => x.states ?? Array.Empty<AnimatorControllerRecoveryState>()))
            {
                foreach (var parameter in state.stateParameters ?? Array.Empty<AnimatorControllerRecoveryStateParameter>())
                {
                    if (!string.IsNullOrWhiteSpace(parameter.name))
                    {
                        defaults[parameter.name] = parameter.value;
                        sources[parameter.name] = "stateParameter";
                    }
                }

                foreach (var node in (state.blendTrees ?? Array.Empty<AnimatorControllerRecoveryBlendTree>())
                    .SelectMany(x => x.nodes ?? Array.Empty<AnimatorControllerRecoveryNode>()))
                {
                    if (!string.IsNullOrWhiteSpace(node.blendEvent) && !defaults.ContainsKey(node.blendEvent))
                    {
                        defaults[node.blendEvent] = 0f;
                        sources[node.blendEvent] = "blendEventFallbackZero";
                    }
                }
            }

            foreach (var item in defaults)
            {
                controller.AddParameter(item.Key, AnimatorControllerParameterType.Float);
                var parameters = controller.parameters;
                for (var i = 0; i < parameters.Length; i++)
                {
                    if (parameters[i].name == item.Key)
                    {
                        parameters[i].defaultFloat = item.Value;
                        break;
                    }
                }
                controller.parameters = parameters;
            }

            if (defaults.Count > 0)
            {
                var hasRecoveredDefaults = defaults.Any(x => !Mathf.Approximately(x.Value, 0f));
                var fallbackZeroCount = sources.Count(x => string.Equals(x.Value, "blendEventFallbackZero", StringComparison.Ordinal));
                var controllerDefaultCount = sources.Count(x =>
                    string.Equals(x.Value, "controllerDefaultValue", StringComparison.Ordinal)
                    || string.Equals(x.Value, "controllerResolvedDefaultValue", StringComparison.Ordinal));
                warnings.Add(fallbackZeroCount == 0
                    ? $"AnimatorController parameters were recreated from inspected controller/state defaults ({controllerDefaultCount} controller default parameter(s)). Runtime-updated values are still not fully recovered."
                    : hasRecoveredDefaults
                        ? $"AnimatorController parameters were recreated from inspected controller/state defaults where available; {fallbackZeroCount} parameter(s) still came only from BlendTree blendEvent fallback. Runtime-updated values are still not fully recovered."
                        : $"AnimatorController parameters were recreated with default value 0; {fallbackZeroCount} parameter(s) came only from BlendTree blendEvent fallback. Runtime-updated values are not fully recovered.");
            }

            return defaults
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new AnimatorControllerRecoveryParameterMetadata
                {
                    name = x.Key,
                    defaultFloat = x.Value,
                    source = sources.TryGetValue(x.Key, out var source) ? source : "unknown",
                })
                .ToList();
        }

        private static AnimatorControllerRecoveryResult Fail(AnimatorControllerRecoveryRequest request, string message)
        {
            return Write(request, new AnimatorControllerRecoveryResult
            {
                status = "error",
                message = message,
            });
        }

        private static AnimatorControllerRecoveryResult Write(AnimatorControllerRecoveryRequest request, AnimatorControllerRecoveryResult result)
        {
            var output = string.IsNullOrWhiteSpace(request?.outputJson)
                ? Path.Combine(Directory.GetCurrentDirectory(), "animator_controller_recovery_result.json")
                : request.outputJson;
            var directory = Path.GetDirectoryName(output);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(output, JsonUtility.ToJson(result, true));
            Debug.Log("AnimeStudio AnimatorController recovery result: " + output);
            return result;
        }

        private static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }
            return path.Replace('\\', '/');
        }

        private static string SanitizeAssetName(string value)
        {
            value = string.IsNullOrWhiteSpace(value) ? "AnimatorController" : value;
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(c, '_');
            }
            return value;
        }

        private static string SanitizeAnimatorName(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "State" : value.Replace('.', '_').Replace('/', '_').Replace('\\', '_');
        }

        private static string FirstPathSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }
            var parts = value.Split(new[] { '.', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? value : parts[0];
        }

        private static string NormalizeMaskPath(string path)
        {
            return string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : path.Replace('\\', '/').Trim().Trim('/');
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        }
    }

    [Serializable]
    public sealed class AnimatorControllerRecoveryRequest
    {
        public string outputJson;
        public string controllerName;
        public string controllerAssetPath;
        public bool force;
        public AnimatorControllerRecoveryClip[] clips;
        public AnimatorControllerRecoveryParameterMetadata[] parameters;
        public AnimatorControllerRecoveryLayer[] layers;
        public AnimatorControllerRecoveryStateMachine[] stateMachines;
    }

    [Serializable]
    public sealed class AnimatorControllerRecoveryClip
    {
        public int index;
        public string name;
        public long pathId;
        public string unityAssetPath;
    }

    [Serializable]
    public sealed class AnimatorControllerRecoveryLayer
    {
        public int index;
        public int stateMachineIndex;
        public int stateMachineMotionSetIndex;
        public long binding;
        public int blendingMode;
        public float defaultWeight = 1f;
        public bool iKPass;
        public bool syncedLayerAffectsTiming;
        public AnimeStudioHumanPoseMask bodyMask;
        public AnimeStudioSkeletonMask skeletonMask;
    }

    [Serializable]
    public sealed class AnimatorControllerRecoveryStateMachine
    {
        public int machineIndex;
        public int defaultState;
        public AnimatorControllerRecoveryState[] states;
    }

    [Serializable]
    public sealed class AnimatorControllerRecoveryState
    {
        public int stateIndex;
        public string name;
        public string fullPath;
        public float speed = 1f;
        public float cycleOffset;
        public bool iKOnFeet;
        public bool mirror;
        public AnimatorControllerRecoveryStateParameter[] stateParameters;
        public AnimatorControllerRecoveryBlendTree[] blendTrees;
    }

    [Serializable]
    public sealed class AnimatorControllerRecoveryStateParameter
    {
        public long nameId;
        public string nameIdHex;
        public string name;
        public float value;
    }

    [Serializable]
    public sealed class AnimatorControllerRecoveryBlendTree
    {
        public int treeIndex;
        public AnimatorControllerRecoveryNode[] nodes;
    }

    [Serializable]
    public sealed class AnimatorControllerRecoveryNode
    {
        public int nodeIndex;
        public int blendType;
        public string blendEvent;
        public int[] childIndices;
        public float[] childThresholds;
        public float[] blend1dChildThresholds;
        public int clipSlot;
        public long clipPathId;
    }

    [Serializable]
    public sealed class AnimatorControllerRecoveryResult
    {
        public string status;
        public string message;
        public string controllerName;
        public string controllerAssetPath;
        public int createdLayerCount;
        public int createdStateCount;
        public int createdBlendTreeCount;
        public int createdAvatarMaskCount;
        public int recoveredAvatarMaskPathCount;
        public int skippedAvatarMaskPathCount;
        public string layerMetadataAssetPath;
        public int clipAssetCount;
        public int missingClipCount;
        public int warningCount;
        public string[] warnings;
    }

    [Serializable]
    public sealed class AnimatorControllerRecoveryMetadata
    {
        public string controllerName;
        public string controllerAssetPath;
        public string rule;
        public AnimatorControllerRecoveryLayerMetadata[] layers;
        public AnimatorControllerRecoveryParameterMetadata[] parameters;
    }

    [Serializable]
    public sealed class AnimatorControllerRecoveryParameterMetadata
    {
        public string name;
        public float defaultFloat;
        public string source;
    }

    [Serializable]
    public sealed class AnimatorControllerRecoveryLayerMetadata
    {
        public int sourceLayerIndex;
        public int recoveredLayerIndex;
        public string recoveredLayerName;
        public string stateName;
        public int sourceStateMachineIndex;
        public int stateMachineMotionSetIndex;
        public long binding;
        public int blendingMode;
        public float defaultWeight;
        public bool iKPass;
        public bool iKOnFeet;
        public bool syncedLayerAffectsTiming;
        public bool recoveredAvatarMask;
        public int recoveredAvatarMaskPathCount;
        public int skippedAvatarMaskPathCount;
        public AnimeStudioHumanPoseMask bodyMask;
        public AnimeStudioSkeletonMask skeletonMask;
    }

    internal sealed class LayerAvatarMaskRecoveryResult
    {
        public bool Created;
        public int PathCount;
        public int SkippedPathCount;
    }
}
