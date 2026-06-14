using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace AnimeStudio.UnityBake
{
    public static class AnimeStudioAssetBundleProbe
    {
        public static void Run()
        {
            var bundlePath = GetArgument("-bundlePath");
            var outputJson = GetArgument("-outputJson");
            if (string.IsNullOrWhiteSpace(outputJson))
            {
                outputJson = Path.Combine(Directory.GetCurrentDirectory(), "anime_studio_assetbundle_probe.json");
            }

            var report = Probe(bundlePath);
            var directory = Path.GetDirectoryName(outputJson);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(outputJson, JsonUtility.ToJson(report, true));
            Debug.Log("AnimeStudio AssetBundle probe report: " + outputJson);
            if (!string.Equals(report.status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogError(report.message);
                EditorApplication.Exit(1);
                return;
            }
            EditorApplication.Exit(0);
        }

        private static AssetBundleProbeReport Probe(string bundlePath)
        {
            var report = new AssetBundleProbeReport
            {
                bundlePath = bundlePath,
            };
            if (string.IsNullOrWhiteSpace(bundlePath) || !File.Exists(bundlePath))
            {
                report.status = "error";
                report.message = "Bundle path is missing or does not exist.";
                return report;
            }

            AssetBundle bundle = null;
            try
            {
                bundle = AssetBundle.LoadFromFile(bundlePath);
                if (bundle == null)
                {
                    return ProbeSerializedFileFallback(bundlePath, report);
                }

                report.status = "ok";
                report.message = "AssetBundle loaded by Unity Editor.";
                report.assetNames = bundle.GetAllAssetNames();
                foreach (var name in report.assetNames)
                {
                    var asset = bundle.LoadAsset<UnityEngine.Object>(name);
                    if (asset == null)
                    {
                        report.assets.Add(new AssetBundleProbeAsset
                        {
                            name = name,
                            type = "<null>",
                        });
                        continue;
                    }

                    var row = new AssetBundleProbeAsset
                    {
                        name = name,
                        unityName = asset.name,
                        type = asset.GetType().FullName,
                    };
                    if (asset is GameObject go)
                    {
                        row.gameObject = InspectGameObject(go);
                    }
                    report.assets.Add(row);
                }
                return report;
            }
            catch (Exception ex)
            {
                report.status = "error";
                report.message = ex.ToString();
                return report;
            }
            finally
            {
                if (bundle != null)
                {
                    bundle.Unload(true);
                }
            }
        }

        private static AssetBundleProbeReport ProbeSerializedFileFallback(string path, AssetBundleProbeReport report)
        {
            if (!AllowUnsafeSerializedFallback())
            {
                report.status = "error";
                report.message =
                    "AssetBundle.LoadFromFile returned null. Unity serialized-file fallback was not attempted because it can crash on raw CAB/legacy game files; set ANIMESTUDIO_ALLOW_UNSAFE_SERIALIZED_FALLBACK=1 for manual diagnostics only.";
                return report;
            }

            try
            {
                var objects = LoadSerializedFileAndForget(path);
                if (objects == null || objects.Length == 0)
                {
                    report.status = "error";
                    report.message = "AssetBundle.LoadFromFile returned null, and Unity serialized-file fallback returned no objects.";
                    return report;
                }

                report.status = "ok";
                report.message = "Unity Editor loaded the file as a SerializedFile fallback.";
                report.assetNames = objects
                    .Where(x => x != null)
                    .Select(x => string.IsNullOrWhiteSpace(x.name) ? x.GetType().Name : x.name)
                    .ToArray();
                foreach (var asset in objects)
                {
                    if (asset == null)
                    {
                        continue;
                    }

                    var row = new AssetBundleProbeAsset
                    {
                        name = string.IsNullOrWhiteSpace(asset.name) ? asset.GetType().Name : asset.name,
                        unityName = asset.name,
                        type = asset.GetType().FullName,
                    };
                    if (asset is GameObject go)
                    {
                        row.gameObject = InspectGameObject(go);
                    }
                    if (asset is Avatar avatar)
                    {
                        row.avatarValid = avatar.isValid;
                        row.avatarHuman = avatar.isHuman;
                    }
                    report.assets.Add(row);
                }
                return report;
            }
            catch (Exception ex)
            {
                report.status = "error";
                report.message = "AssetBundle.LoadFromFile returned null, and serialized-file fallback failed: " + ex;
                return report;
            }
        }

        private static bool AllowUnsafeSerializedFallback()
        {
            var value = Environment.GetEnvironmentVariable("ANIMESTUDIO_ALLOW_UNSAFE_SERIALIZED_FALLBACK");
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static UnityEngine.Object[] LoadSerializedFileAndForget(string path)
        {
            // Unity 没有公开加载裸 CAB/SerializedFile 的稳定 API；这里仅做诊断探测，
            // 真实主流程必须在确认 Unity 能返回原始 Avatar/Clip 后再接入。
            var unityEditorAssembly = typeof(EditorApplication).Assembly;
            var method = unityEditorAssembly
                .GetTypes()
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                .FirstOrDefault(x => string.Equals(x.Name, "LoadSerializedFileAndForget", StringComparison.Ordinal));
            if (method == null)
            {
                throw new MissingMethodException("UnityEditor", "LoadSerializedFileAndForget");
            }

            var parameters = method.GetParameters();
            object result;
            if (parameters.Length == 1)
            {
                result = method.Invoke(null, new object[] { path });
            }
            else if (parameters.Length == 2)
            {
                result = method.Invoke(null, new object[] { path, string.Empty });
            }
            else
            {
                throw new NotSupportedException("Unsupported LoadSerializedFileAndForget signature: " + parameters.Length);
            }

            return result as UnityEngine.Object[] ?? Array.Empty<UnityEngine.Object>();
        }

        private static AssetBundleProbeGameObject InspectGameObject(GameObject go)
        {
            var result = new AssetBundleProbeGameObject
            {
                name = go.name,
                transformCount = go.GetComponentsInChildren<Transform>(true).Length,
                rendererCount = go.GetComponentsInChildren<Renderer>(true).Length,
            };
            foreach (var animator in go.GetComponentsInChildren<Animator>(true))
            {
                result.animators.Add(new AssetBundleProbeAnimator
                {
                    path = GetPath(go.transform, animator.transform),
                    avatarName = animator.avatar != null ? animator.avatar.name : null,
                    avatarValid = animator.avatar != null && animator.avatar.isValid,
                    avatarHuman = animator.avatar != null && animator.avatar.isHuman,
                    controllerName = animator.runtimeAnimatorController != null ? animator.runtimeAnimatorController.name : null,
                    controllerType = animator.runtimeAnimatorController != null ? animator.runtimeAnimatorController.GetType().FullName : null,
                });
            }
            return result;
        }

        private static string GetPath(Transform root, Transform current)
        {
            if (root == null || current == null || current == root)
            {
                return "";
            }
            var names = new Stack<string>();
            var cursor = current;
            while (cursor != null && cursor != root)
            {
                names.Push(cursor.name);
                cursor = cursor.parent;
            }
            return string.Join("/", names.ToArray());
        }

        private static string GetArgument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }
            return null;
        }
    }

    [Serializable]
    public sealed class AssetBundleProbeReport
    {
        public string status;
        public string message;
        public string bundlePath;
        public string[] assetNames = Array.Empty<string>();
        public List<AssetBundleProbeAsset> assets = new List<AssetBundleProbeAsset>();
    }

    [Serializable]
    public sealed class AssetBundleProbeAsset
    {
        public string name;
        public string unityName;
        public string type;
        public bool avatarValid;
        public bool avatarHuman;
        public AssetBundleProbeGameObject gameObject;
    }

    [Serializable]
    public sealed class AssetBundleProbeGameObject
    {
        public string name;
        public int transformCount;
        public int rendererCount;
        public List<AssetBundleProbeAnimator> animators = new List<AssetBundleProbeAnimator>();
    }

    [Serializable]
    public sealed class AssetBundleProbeAnimator
    {
        public string path;
        public string avatarName;
        public bool avatarValid;
        public bool avatarHuman;
        public string controllerName;
        public string controllerType;
    }
}
