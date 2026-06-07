using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AnimeStudio.LibraryBrowser
{
    internal sealed class LibraryModelItem
    {
        public string Name { get; init; } = "";
        public string ResourceKind { get; init; } = "Unknown";
        public string LibraryRole { get; init; } = "";
        public string SourceType { get; init; } = "";
        public string Source { get; init; } = "";
        public long PathId { get; init; }
        public string OutputPath { get; init; } = "";
        public int MeshCount { get; init; }
        public int VertexCount { get; init; }
        public int MaterialCount { get; init; }
        public int TextureCount { get; init; }
        public int BoneCount { get; init; }
        public string[] BonePaths { get; init; } = Array.Empty<string>();
        public string[] NodePaths { get; init; } = Array.Empty<string>();

        public string FileName => Path.GetFileName(OutputPath);

        public string ModelSourceLabel
        {
            get
            {
                if (EqualsIgnoreCase(LibraryRole, "PrefabPrimary"))
                {
                    return "Prefab";
                }

                if (EqualsIgnoreCase(LibraryRole, "StaticMeshPrimary"))
                {
                    return "Mesh";
                }

                if (EqualsIgnoreCase(LibraryRole, "RawUnreferenced") || EqualsIgnoreCase(LibraryRole, "RawModel"))
                {
                    return "Raw";
                }

                if (EqualsIgnoreCase(LibraryRole, "SourcePart") || EqualsIgnoreCase(LibraryRole, "StaticMeshSource"))
                {
                    return "Part";
                }

                if (EqualsIgnoreCase(SourceType, "Mesh"))
                {
                    return "Mesh";
                }

                if (EqualsIgnoreCase(SourceType, "GameObject") || EqualsIgnoreCase(SourceType, "Animator"))
                {
                    return "Prefab";
                }

                return string.IsNullOrWhiteSpace(SourceType) ? "Unknown" : SourceType;
            }
        }

        private static bool EqualsIgnoreCase(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        public string StableKey
        {
            get
            {
                var raw = $"{Source}|{PathId}|{OutputPath}".ToLowerInvariant();
                var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(raw));
                return Convert.ToHexString(bytes).ToLowerInvariant();
            }
        }
    }
}
