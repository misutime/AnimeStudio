namespace AnimeStudio.LibraryBrowser
{
    internal sealed class LibraryAnimationCandidate
    {
        public string Name { get; init; } = "";
        public string OutputPath { get; init; } = "";
        public string AnimationAssetPath { get; init; } = "";
        public string Source { get; init; } = "";
        public string AnimationType { get; init; } = "";
        public string Capability { get; init; } = "";
        public string Relation { get; init; } = "";
        public string RelationSource { get; init; } = "";
        public string Confidence { get; init; } = "";
        public double Score { get; init; }
        public double Duration { get; init; }
        public double SampleRate { get; init; }
        public int CurveCount { get; init; }
        public int MatchedPathCount { get; init; }
        public bool RequiresHumanoidBake { get; init; }
        public bool NeedsValidation { get; init; }
        public string[] BindingPaths { get; init; } = System.Array.Empty<string>();

        public string BestPath => !string.IsNullOrWhiteSpace(OutputPath) ? OutputPath : AnimationAssetPath;
        public bool IsExplicit => string.Equals(RelationSource, "explicit", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(Confidence, "explicit_unity_reference", System.StringComparison.OrdinalIgnoreCase);
    }
}
