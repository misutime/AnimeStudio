using System;

namespace AnimeStudio.LibraryBrowser
{
    internal sealed class LibraryAnimationUsage
    {
        public string Key { get; init; } = "";
        public LibraryAnimationCandidate Animation { get; init; } = new();
        public string[] ModelOutputs { get; init; } = Array.Empty<string>();
        public int? ModelCountOverride { get; init; }
        public int ModelCount => ModelCountOverride ?? ModelOutputs.Length;
    }
}
