using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// One region of one frame that an inspector attributes to a kind, with a confidence.
    ///
    /// Deliberately not called a Detection. In this glossary a **Detection is the moment an
    /// anomaly is confirmed** - a point on the timeline - and this is a rectangle on a picture.
    /// Letting a model's word for a box take that name would leave two meanings on one word in
    /// the layer where both are used.
    ///
    /// It speaks <see cref="AnomalyKind"/> and nothing else. A model with classes of its own maps
    /// them to kinds inside its inspector, or does not report them: the app has one vocabulary for
    /// what can be wrong with a screen, the settings window lists it, and a finding that arrived
    /// under some other name could not be switched off, reported, or filed against a DUT.
    /// </summary>
    public readonly struct ScreenFinding
    {
        public ScreenFinding(AnomalyKind kind, float score, float x, float y,
                             float width, float height)
        {
            Kind = kind;
            Score = score;
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        /// <summary>What the inspector says this region is, in the app's own vocabulary.</summary>
        public AnomalyKind Kind { get; }

        /// <summary>Confidence, 0 to 1.</summary>
        public float Score { get; }

        /// <summary>Left edge, in acquired frame pixels - the same coordinates as ChannelRoi.</summary>
        public float X { get; }

        /// <summary>Top edge, in acquired frame pixels.</summary>
        public float Y { get; }

        public float Width { get; }
        public float Height { get; }

        /// <summary>Right edge. Named for the edge rather than x2, which says nothing.</summary>
        public float Right => X + Width;

        /// <summary>Bottom edge.</summary>
        public float Bottom => Y + Height;

        /// <summary>Area in pixels, for ranking and for the overlap test.</summary>
        public float Area => Width <= 0f || Height <= 0f ? 0f : Width * Height;

        public override string ToString() =>
            $"{Kind} {Score:F2} at {X:F0},{Y:F0} {Width:F0}x{Height:F0}";
    }
}
