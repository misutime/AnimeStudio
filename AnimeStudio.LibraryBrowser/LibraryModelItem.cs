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

        public string FileName => Path.GetFileName(OutputPath);

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
