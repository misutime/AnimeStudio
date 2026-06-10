using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;

namespace AnimeStudio.LibraryBrowser
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length == 3 && string.Equals(args[0], "--render-thumbnail", StringComparison.OrdinalIgnoreCase))
            {
                using var renderer = new PersistentGltfThumbnailRenderer();
                renderer.RenderToFile(args[1], args[2]);
                return;
            }

            if (args.Length == 1 && string.Equals(args[0], "--thumbnail-worker", StringComparison.OrdinalIgnoreCase))
            {
                RunThumbnailWorker();
                return;
            }

            if (args.Length == 2 && string.Equals(args[0], "--validate-library", StringComparison.OrdinalIgnoreCase))
            {
                ValidateLibrary(args[1]);
                return;
            }

            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        private static void RunThumbnailWorker()
        {
            using var renderer = new PersistentGltfThumbnailRenderer();
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                ThumbnailWorkerRequest request = null;
                try
                {
                    request = JsonSerializer.Deserialize<ThumbnailWorkerRequest>(line);
                    if (request == null || string.IsNullOrWhiteSpace(request.GltfPath) || string.IsNullOrWhiteSpace(request.OutputPath))
                    {
                        WriteWorkerResponse(new ThumbnailWorkerResponse { Id = request?.Id ?? "", Success = false, Error = "empty request" });
                        continue;
                    }

                    renderer.RenderToFile(request.GltfPath, request.OutputPath);
                    WriteWorkerResponse(new ThumbnailWorkerResponse { Id = request.Id, Success = File.Exists(request.OutputPath), Error = "" });
                }
                catch (Exception ex)
                {
                    WriteWorkerResponse(new ThumbnailWorkerResponse { Id = request?.Id ?? "", Success = false, Error = ex.Message });
                }
            }
        }

        private static void WriteWorkerResponse(ThumbnailWorkerResponse response)
        {
            Console.WriteLine(JsonSerializer.Serialize(response));
            Console.Out.Flush();
        }

        private static void ValidateLibrary(string root)
        {
            var models = LibraryIndexReader.LoadModels(root);
            var animationIndex = LibraryAnimationIndex.Load(root);
            var textures = models.Count(x => x.IsTexture);
            var vfx = models.Count(x => x.IsVfx);
            var realModels = models.Count - textures - vfx;
            var skinned = models.Count(x => x.HasSkin || x.BoneCount > 0);
            var withAnimations = models.Count(x => animationIndex.CountForModel(x) > 0);
            var withReportedAnimationCandidates = models.Count(x => !x.IsTexture && !x.IsVfx && x.AnimationCandidateCount > 0);
            var reportedAnimationCandidates = models
                .Where(x => !x.IsTexture && !x.IsVfx)
                .Sum(x => x.AnimationCandidateCount);
            var realModelItems = models.Where(x => !x.IsTexture && !x.IsVfx).ToList();
            var taskOrPropItems = realModelItems.Where(x => x.IsTaskOrProp).ToList();
            var payload = new
            {
                root = Path.GetFullPath(root),
                models = realModels,
                skinnedModels = skinned,
                textures,
                vfx,
                animations = animationIndex.FindAllAnimations().Count,
                modelsWithAnimationCandidates = withAnimations,
                modelsWithReportedAnimationCandidates = withReportedAnimationCandidates,
                animationIndexSource = animationIndex.LoadSource,
                animationCandidates = animationIndex.IndexedCandidateCount,
                reportedAnimationCandidates,
                taskOrPropModels = taskOrPropItems.Count,
                taskOrPropReady = taskOrPropItems.Count(x => !x.NeedsReview),
                taskOrPropNeedsReview = taskOrPropItems.Count(x => x.NeedsReview),
                pathOnlyTaskOrPropModels = taskOrPropItems.Count(x => x.IsPathOnlyTask),
                taskOrPropWithComponentReferences = taskOrPropItems.Count(x => x.ComponentReferenceCount > 0),
                taskOrPropWithAnimations = taskOrPropItems.Count(x => animationIndex.CountForModel(x) > 0 || x.AnimationCandidateCount > 0),
                taskOrPropMissingMaterials = taskOrPropItems.Count(x => x.MissingMaterials),
                taskOrPropNoExternalTextureSlots = taskOrPropItems.Count(x => x.NoExternalTextureSlots),
                taskOrPropWarnings = taskOrPropItems.Count(x => string.Equals(x.ValidationStatus, "warning", StringComparison.OrdinalIgnoreCase)),
                taskOrPropErrors = taskOrPropItems.Count(x => string.Equals(x.ValidationStatus, "error", StringComparison.OrdinalIgnoreCase))
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }

        private sealed class ThumbnailWorkerRequest
        {
            public string Id { get; set; } = "";
            public string GltfPath { get; set; } = "";
            public string OutputPath { get; set; } = "";
        }

        private sealed class ThumbnailWorkerResponse
        {
            public string Id { get; set; } = "";
            public bool Success { get; set; }
            public string Error { get; set; } = "";
        }
    }
}
