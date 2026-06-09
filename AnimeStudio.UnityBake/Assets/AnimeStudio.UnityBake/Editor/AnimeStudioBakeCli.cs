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
            var requestPath = GetArgument("-animeStudioBakeRequest");
            if (string.IsNullOrWhiteSpace(requestPath) || !File.Exists(requestPath))
            {
                Fail("Missing -animeStudioBakeRequest or request file does not exist.");
                return;
            }

            try
            {
                var request = JsonUtility.FromJson<AnimeStudioBakeRequest>(File.ReadAllText(requestPath));
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
                var request = new AnimeStudioBakeRequest { outputJson = Path.Combine(Path.GetDirectoryName(requestPath) ?? ".", "unity_bake_result.json") };
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

            try
            {
                var request = JsonUtility.FromJson<AnimeStudioBakeRequest>(File.ReadAllText(requestPath));
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
                var request = new AnimeStudioBakeRequest { outputJson = Path.Combine(Path.GetDirectoryName(requestPath) ?? ".", "unity_bake_result.json") };
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
