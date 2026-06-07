using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AnimeStudio.LibraryBrowser
{
    internal static class LibraryIndexReader
    {
        private static readonly string[] LibraryMarkerFiles =
        {
            "LIBRARY_README.md",
            "asset_summary.json",
            "model_animations.json",
            "model_animations.compact.json",
            "animation_bindings.jsonl",
            "export_manifest.jsonl",
            "unity_relations.jsonl",
            "library_index.db"
        };

        public static void ValidateLibraryRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                throw new DirectoryNotFoundException("请选择一个存在的素材库目录。");
            }

            var catalogPath = Path.Combine(root, "asset_catalog.jsonl");
            if (!File.Exists(catalogPath))
            {
                throw new FileNotFoundException("没有找到 asset_catalog.jsonl。请选择 AnimeStudio 导出的 Library 根目录。", catalogPath);
            }

            var hasModelsDirectory = Directory.Exists(Path.Combine(root, "Models"));
            var hasLibraryMarker = LibraryMarkerFiles.Any(x => File.Exists(Path.Combine(root, x)));
            if (!hasModelsDirectory && !hasLibraryMarker)
            {
                throw new InvalidDataException(
                    "这个目录只有 asset_catalog.jsonl，缺少 Models/ 或 Library 索引/说明文件。" +
                    "请确认选择的是 AnimeStudio 导出的素材库根目录，而不是单独拷出的索引文件目录。");
            }
        }

        public static List<LibraryModelItem> LoadModels(string root)
        {
            ValidateLibraryRoot(root);

            var catalogPath = Path.Combine(root, "asset_catalog.jsonl");
            var models = new List<LibraryModelItem>();
            foreach (var line in File.ReadLines(catalogPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                var obj = document.RootElement;
                if (!StringEquals(obj, "kind", "Model"))
                {
                    continue;
                }

                var output = ReadString(obj, "output");
                if (string.IsNullOrWhiteSpace(output) || !File.Exists(output))
                {
                    continue;
                }

                models.Add(new LibraryModelItem
                {
                    Name = ReadString(obj, "name") ?? Path.GetFileNameWithoutExtension(output) ?? "",
                    ResourceKind = ReadString(obj, "resourceKind") ?? "Unknown",
                    LibraryRole = ReadString(obj, "libraryRole") ?? "",
                    SourceType = ReadString(obj, "sourceType") ?? "",
                    Source = ReadString(obj, "source") ?? "",
                    PathId = ReadInt64(obj, "pathId"),
                    OutputPath = output,
                    MeshCount = ReadInt32(obj, "meshCount"),
                    VertexCount = ReadInt32(obj, "vertexCount"),
                    MaterialCount = ReadInt32(obj, "materialCount"),
                    TextureCount = ReadInt32(obj, "textureCount"),
                    BoneCount = ReadInt32(obj, "boneCount")
                });
            }

            return models;
        }

        private static bool StringEquals(JsonElement obj, string name, string value)
        {
            return obj.TryGetProperty(name, out var property)
                && property.ValueKind == JsonValueKind.String
                && string.Equals(property.GetString(), value, StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadString(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
        }

        private static int ReadInt32(JsonElement obj, string name)
        {
            return obj.TryGetProperty(name, out var property) && property.TryGetInt32(out var value) ? value : 0;
        }

        private static long ReadInt64(JsonElement obj, string name)
        {
            return obj.TryGetProperty(name, out var property) && property.TryGetInt64(out var value) ? value : 0;
        }
    }
}
