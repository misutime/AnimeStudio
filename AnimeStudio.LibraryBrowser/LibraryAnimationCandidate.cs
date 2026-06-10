namespace AnimeStudio.LibraryBrowser
{
    internal sealed class LibraryAnimationCandidate
    {
        public string Name { get; init; } = "";
        public string OutputPath { get; init; } = "";
        public string AnimationAssetPath { get; init; } = "";
        public string Source { get; init; } = "";
        public string SourceType { get; init; } = "";
        public string Format { get; init; } = "";
        public string AnimationType { get; init; } = "";
        public string Capability { get; init; } = "";
        public string Relation { get; init; } = "";
        public string RelationSource { get; init; } = "";
        public string Confidence { get; init; } = "";
        public string ExportStatus { get; init; } = "";
        public double Score { get; init; }
        public double Duration { get; init; }
        public double SampleRate { get; init; }
        public int CurveCount { get; init; }
        public int FrameCount { get; init; }
        public int TrackCount { get; init; }
        public int SegmentCount { get; init; }
        public int MatchedPathCount { get; init; }
        public double TrackCoverage { get; init; }
        public string ValidationStatus { get; init; } = "";
        public string ValidationCategory { get; init; } = "";
        public string ValidationReason { get; init; } = "";
        public bool RequiresHumanoidBake { get; init; }
        public bool NeedsValidation { get; init; }
        public bool IsContainerAnimation { get; init; }
        public string[] BindingPaths { get; init; } = System.Array.Empty<string>();

        public string BestPath => !string.IsNullOrWhiteSpace(OutputPath) ? OutputPath : AnimationAssetPath;
        public bool IsUnreal => string.Equals(AnimationType, "Unreal", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(Format, "ueanim", System.StringComparison.OrdinalIgnoreCase)
            || SourceType.StartsWith("UAnim", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(SourceType, "AnimSequence", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(SourceType, "AnimMontage", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(SourceType, "AnimComposite", System.StringComparison.OrdinalIgnoreCase)
            || Relation?.StartsWith("unreal.", System.StringComparison.OrdinalIgnoreCase) == true
            || (!string.IsNullOrWhiteSpace(OutputPath)
                && OutputPath.EndsWith(".ueanim", System.StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(AnimationAssetPath)
                && AnimationAssetPath.EndsWith(".ueanim", System.StringComparison.OrdinalIgnoreCase));
        public bool IsExplicit => string.Equals(RelationSource, "explicit", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(RelationSource, "componentOwner", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(Confidence, "explicit_unity_reference", System.StringComparison.OrdinalIgnoreCase);
        public bool IsMetadataOnly => string.Equals(ExportStatus, "metadata", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(Format, "json", System.StringComparison.OrdinalIgnoreCase)
            || BestPath.EndsWith(".metadata.json", System.StringComparison.OrdinalIgnoreCase);
        public bool IsUsableCandidate => !IsUnreal ||
            ((string.IsNullOrWhiteSpace(ExportStatus) || string.Equals(ExportStatus, "ok", System.StringComparison.OrdinalIgnoreCase)) &&
             !IsMetadataOnly &&
             TrackCount > 0 &&
             BestPath.EndsWith(".ueanim", System.StringComparison.OrdinalIgnoreCase) &&
             System.IO.File.Exists(BestPath) &&
             !string.Equals(ValidationStatus, "error", System.StringComparison.OrdinalIgnoreCase));
    }
}
