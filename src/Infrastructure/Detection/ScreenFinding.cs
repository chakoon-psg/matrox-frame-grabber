using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// One region a screen inspector found on one frame.
    ///
    /// **`Kind` and `Label` do different jobs, and that separation is the point.** `Kind` is what
    /// this app acts on - it comes from our vocabulary, it is what a report groups by, and it is
    /// what a threshold or a rule can be written against. `Label` is what the model actually said,
    /// carried through unchanged so a finding can be read back years later against a model nobody
    /// still has. A model that says "green" maps to some `Kind` by a table we control; if that
    /// table is wrong, `Label` is the only way to notice.
    ///
    /// Coordinates are in **acquired-frame pixels** - the same space the analysis ROI uses, after
    /// decimation. Not model input pixels: the letterbox that produced them is the only thing that
    /// knows how to undo itself, so it undoes itself before a finding leaves it.
    /// </summary>
    public readonly struct ScreenFinding
    {
        public ScreenFinding(AnomalyKind kind, string label, float score,
                             float x, float y, float width, float height)
        {
            Kind = kind;
            Label = label ?? string.Empty;
            Score = score;
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        /// <summary>What the app acts on. Our vocabulary, from a table we control.</summary>
        public AnomalyKind Kind { get; }

        /// <summary>What the model said, unchanged. Recorded, never acted on.</summary>
        public string Label { get; }

        /// <summary>Confidence, 0 to 1.</summary>
        public float Score { get; }

        public float X { get; }
        public float Y { get; }
        public float Width { get; }
        public float Height { get; }

        /// <summary>
        /// The right edge, named as an edge rather than x2.
        ///
        /// A frame 1024 wide has its right edge at 1024, not 1023 - the edge is between pixels.
        /// Naming it `Right` rather than `X2` is what keeps the next person from clamping to
        /// `width - 1`.
        /// </summary>
        public float Right => X + Width;

        /// <summary>The bottom edge. Same reasoning as <see cref="Right"/>.</summary>
        public float Bottom => Y + Height;

        /// <summary>Area in frame pixels, or 0 for a degenerate box.</summary>
        public float Area => Width <= 0f || Height <= 0f ? 0f : Width * Height;

        public override string ToString() =>
            $"{Kind}" + (Label.Length > 0 && !string.Equals(Label, Kind.ToString(), StringComparison.Ordinal)
                            ? $"(\"{Label}\")" : string.Empty)
            + $" {Score:F3} at {X:F1},{Y:F1} {Width:F1}x{Height:F1}";
    }
}
