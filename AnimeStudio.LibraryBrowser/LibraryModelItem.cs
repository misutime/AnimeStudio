using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AnimeStudio.LibraryBrowser
{
    internal sealed class LibraryModelItem
    {
        private const string VfxPreviewSchemaVersion = "vfx-preview-v7";

        public string Name { get; init; } = "";
        public string AssetKind { get; init; } = "Model";
        public string ResourceKind { get; init; } = "Unknown";
        public string VfxCategory { get; init; } = "";
        public string Confidence { get; init; } = "";
        public string Status { get; init; } = "";
        public string ValidationStatus { get; init; } = "";
        public string LibraryRole { get; init; } = "";
        public string SourceType { get; init; } = "";
        public string Source { get; init; } = "";
        public string ObjectPath { get; init; } = "";
        public long PathId { get; init; }
        public string OutputPath { get; init; } = "";
        public string ModelPreviewPath { get; init; } = "";
        public int MeshCount { get; init; }
        public int VertexCount { get; init; }
        public int MaterialCount { get; init; }
        public int TextureCount { get; init; }
        public int ComponentCount { get; init; }
        public int MaterialRefCount { get; init; }
        public int TextureRefCount { get; init; }
        public int TexturePreviewCount { get; init; }
        public int MeshRefCount { get; init; }
        public int OccurrenceCount { get; init; } = 1;
        public int BoneCount { get; init; }
        public bool HasSkin { get; init; }
        public bool HasSkeletonPath { get; init; }
        public bool IsStaticModel { get; init; }
        public int ComponentReferenceCount { get; init; }
        public int SourceIndexObjectCount { get; init; }
        public int AnimationCandidateCount { get; init; }
        public bool IsTaskOrProp { get; init; }
        public bool IsPathOnlyTask { get; init; }
        public bool MissingMaterials { get; init; }
        public bool NoExternalTextureSlots { get; init; }
        public bool NeedsReview { get; init; }
        public string[] BonePaths { get; init; } = Array.Empty<string>();
        public string[] NodePaths { get; init; } = Array.Empty<string>();
        public string[] Signals { get; init; } = Array.Empty<string>();
        public string[] TaskSignals { get; init; } = Array.Empty<string>();
        public string[] VfxTexturePreviewPaths { get; init; } = Array.Empty<string>();
        public string VfxPreviewHintsJson { get; init; } = "";

        public bool IsVfx => EqualsIgnoreCase(AssetKind, "VFX")
            || (!EqualsIgnoreCase(AssetKind, "Model")
                && !ContainsIgnoreCase(AssetKind, "Texture")
                && EqualsIgnoreCase(ResourceKind, "VFX"));
        public bool IsTexture => !IsVfx
            && (ContainsIgnoreCase(AssetKind, "Texture")
                || ContainsIgnoreCase(ResourceKind, "Texture")
                || EqualsIgnoreCase(SourceType, "Texture2D")
                || EqualsIgnoreCase(SourceType, "Texture2DArray"));

        public string FileName => IsVfx && Directory.Exists(OutputPath)
            ? Path.GetFileName(OutputPath)
            : Path.GetFileName(OutputPath);

        public string ThumbnailSourcePath => IsTexture && File.Exists(OutputPath)
            ? OutputPath
            : !string.IsNullOrWhiteSpace(ModelPreviewPath) && File.Exists(ModelPreviewPath)
            ? ModelPreviewPath
            : File.Exists(OutputPath)
                ? OutputPath
                : "";

        public string ReportPath => IsVfx && Directory.Exists(OutputPath)
            ? Path.Combine(OutputPath, "VFX_REPORT.md")
            : "";

        public string MetadataPath => IsVfx && Directory.Exists(OutputPath)
            ? Path.Combine(OutputPath, "vfx.json")
            : "";

        public string ModelSourceLabel
        {
            get
            {
                if (IsVfx)
                {
                    return "VFX";
                }

                if (IsTexture)
                {
                    return "Texture";
                }

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

        private static bool ContainsIgnoreCase(string value, string text)
        {
            return value?.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public string StableKey
        {
            get
            {
                var raw = IsVfx
                    ? $"{VfxPreviewSchemaVersion}|{AssetKind}|{Source}|{PathId}|{OutputPath}|{ModelPreviewPath}|{Status}|{ComponentCount}|{MaterialRefCount}|{TextureRefCount}|{TexturePreviewCount}|{MeshRefCount}|{OccurrenceCount}|{string.Join(",", Signals)}|{string.Join(",", VfxTexturePreviewPaths)}|{VfxPreviewHintsJson}".ToLowerInvariant()
                    : IsTexture
                    ? $"texture|{AssetKind}|{ResourceKind}|{SourceType}|{Source}|{PathId}|{OutputPath}".ToLowerInvariant()
                    : $"{AssetKind}|{Source}|{PathId}|{OutputPath}|{ModelPreviewPath}".ToLowerInvariant();
                var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(raw));
                return Convert.ToHexString(bytes).ToLowerInvariant();
            }
        }
    }
}
