using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AnimeStudio.UnityBake
{
    public static class AnimeStudioBakeCli
    {
        public static void Run()
        {
            var avatarOraclePath = GetArgument("-animeStudioAvatarOracleProbe");
            if (!string.IsNullOrWhiteSpace(avatarOraclePath))
            {
                AnimeStudioAvatarOracleProbe.Run(avatarOraclePath, GetArgument("-outputJson"));
                return;
            }

            var avatarAssetPath = GetArgument("-animeStudioImportedAvatarProbe");
            if (!string.IsNullOrWhiteSpace(avatarAssetPath))
            {
                AnimeStudioImportedAvatarProbe.Run(avatarAssetPath, GetArgument("-outputJson"));
                return;
            }

            var avatarAssetRoot = GetArgument("-animeStudioImportedAvatarProbeDir");
            if (!string.IsNullOrWhiteSpace(avatarAssetRoot))
            {
                AnimeStudioImportedAvatarProbe.RunDirectory(avatarAssetRoot, GetArgument("-outputJson"));
                return;
            }

            var acceleratedRequestPath = GetArgument("-animeStudioAcceleratedBakeRequest");
            if (!string.IsNullOrWhiteSpace(acceleratedRequestPath))
            {
                AnimeStudioUnityBakeAcceleratedWorker.RunOnce();
                return;
            }

            var requestPath = GetArgument("-animeStudioBakeRequest");
            if (string.IsNullOrWhiteSpace(requestPath) || !File.Exists(requestPath))
            {
                Fail("Missing -animeStudioBakeRequest or request file does not exist.");
                return;
            }

            AnimeStudioBakeRequest request = null;
            try
            {
                request = JsonUtility.FromJson<AnimeStudioBakeRequest>(File.ReadAllText(requestPath));
                if (request == null)
                {
                    Fail("Unable to parse AnimeStudio bake request.");
                    return;
                }
                var result = AnimeStudioPlayableBaker.Bake(request);
                WriteResult(request, result);
                if (result.status != "ok")
                {
                    Fail(result.message);
                }
            }
            catch (Exception e)
            {
                // 已经读到 request 时，错误也必须写到 request.outputJson。
                // 这样显式 Avatar asset 加载失败不会让调用端等错结果文件。
                request ??= new AnimeStudioBakeRequest { outputJson = Path.Combine(Path.GetDirectoryName(requestPath) ?? ".", "unity_bake_result.json") };
                WriteResult(request, new AnimeStudioBakeResult
                {
                    status = "error",
                    message = e.ToString(),
                });
                Fail(e.ToString());
            }
        }

        private static void WriteResult(AnimeStudioBakeRequest request, AnimeStudioBakeResult result)
        {
            var output = string.IsNullOrWhiteSpace(request.outputJson)
                ? Path.Combine(Directory.GetCurrentDirectory(), "unity_bake_result.json")
                : request.outputJson;
            var directory = Path.GetDirectoryName(output);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(output, JsonUtility.ToJson(result, true));
            Debug.Log($"AnimeStudio bake result: {output}");
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

        private static void Fail(string message)
        {
            Debug.LogError(message);
            EditorApplication.Exit(1);
        }

        internal static string GetArgumentValue(string name)
        {
            return GetArgument(name);
        }

        internal static AnimeStudioBakeResult RunRequest(string requestPath)
        {
            if (string.IsNullOrWhiteSpace(requestPath) || !File.Exists(requestPath))
            {
                return new AnimeStudioBakeResult
                {
                    status = "error",
                    message = "Missing -animeStudioBakeRequest or request file does not exist.",
                };
            }

            AnimeStudioBakeRequest request = null;
            try
            {
                request = JsonUtility.FromJson<AnimeStudioBakeRequest>(File.ReadAllText(requestPath));
                if (request == null)
                {
                    return new AnimeStudioBakeResult
                    {
                        status = "error",
                        message = "Unable to parse AnimeStudio bake request.",
                    };
                }

                var result = AnimeStudioPlayableBaker.Bake(request);
                WriteResult(request, result);
                return result;
            }
            catch (Exception e)
            {
                // 保留原 request.outputJson，避免测试/批处理读取不到失败报告。
                request ??= new AnimeStudioBakeRequest { outputJson = Path.Combine(Path.GetDirectoryName(requestPath) ?? ".", "unity_bake_result.json") };
                var result = new AnimeStudioBakeResult
                {
                    status = "error",
                    message = e.ToString(),
                };
                WriteResult(request, result);
                return result;
            }
        }
    }
}
