using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// The kinds of screen fault this app is for, from CONTEXT.md.
    ///
    /// All five are here even though only <see cref="Dropout"/> is implemented, because the
    /// settings file needs a key per kind and the settings window needs to list what is *not* being
    /// checked - a quiet panel must not read as "nothing wrong" when one of five things is being
    /// looked at. Whether a kind has a detector is <see cref="AnomalyCatalog.Implemented"/>, not a
    /// property of the enum.
    /// </summary>
    public enum AnomalyKind
    {
        /// <summary>Brightness falls and comes back. The only one implemented.</summary>
        Dropout = 0,
        /// <summary>The picture goes, or nearly goes, and stays gone.</summary>
        Blackout = 1,
        /// <summary>Brightness rises until detail is lost.</summary>
        Washout = 2,
        /// <summary>Brightness oscillates rather than stepping.</summary>
        Flicker = 3,
        /// <summary>The image is rotated or mirrored.</summary>
        Flip = 4,
    }

    /// <summary>
    /// Which way a kind moves brightness. The gates in a fall detector - depth, coherence, onset
    /// spread - are all worded for a fall, and a rise needs the sign the other way round; carrying
    /// the direction stops "depth 0.12" being printed for something that got brighter.
    /// </summary>
    public enum AnomalyDirection
    {
        /// <summary>Darker than the baseline.</summary>
        Fall = 0,
        /// <summary>Brighter than the baseline.</summary>
        Rise = 1,
        /// <summary>Both, or neither - flicker swings and a flip does not move the mean at all.</summary>
        Either = 2,
    }

    /// <summary>
    /// What is known about each kind without asking a detector: its name, which way it moves, and
    /// whether anything implements it.
    ///
    /// Separate from the enum so the enum stays a plain key, and separate from the detectors so the
    /// settings window can list all five before four of them exist.
    /// </summary>
    public static class AnomalyCatalog
    {
        /// <summary>Every kind, in the order the settings window shows them.</summary>
        public static readonly AnomalyKind[] All =
        {
            AnomalyKind.Dropout, AnomalyKind.Blackout, AnomalyKind.Washout,
            AnomalyKind.Flicker, AnomalyKind.Flip,
        };

        /// <summary>Number of kinds, for arrays indexed by <see cref="AnomalyKind"/>.</summary>
        public const int Count = 5;

        /// <summary>
        /// Whether a detector exists. Only Dropout does, and it took a week of measurement to
        /// trust: a rate-based depth threshold, a coherence gate, an onset-spread gate, an event
        /// cap and a histogram that excludes the frames around an event.
        /// </summary>
        public static bool Implemented(AnomalyKind kind) => kind == AnomalyKind.Dropout;

        /// <summary>Which way this kind moves brightness.</summary>
        public static AnomalyDirection DirectionOf(AnomalyKind kind)
        {
            switch (kind)
            {
                case AnomalyKind.Dropout:
                case AnomalyKind.Blackout: return AnomalyDirection.Fall;
                case AnomalyKind.Washout: return AnomalyDirection.Rise;
                default: return AnomalyDirection.Either;
            }
        }

        /// <summary>
        /// The word for a deviation of this kind, so a report reads correctly: a fall has a depth,
        /// a rise does not.
        /// </summary>
        public static string DeviationWord(AnomalyKind kind)
        {
            switch (DirectionOf(kind))
            {
                case AnomalyDirection.Fall: return "depth";
                case AnomalyDirection.Rise: return "rise";
                default: return "dev";
            }
        }

        /// <summary>One line for the settings window, in the operator's terms rather than the code's.</summary>
        public static string Describe(AnomalyKind kind)
        {
            switch (kind)
            {
                case AnomalyKind.Dropout: return "화면이 어두워졌다 돌아옵니다";
                case AnomalyKind.Blackout: return "화면이 사라진 채로 유지됩니다";
                case AnomalyKind.Washout: return "화면이 밝아져 형체가 사라집니다";
                case AnomalyKind.Flicker: return "밝기가 주기적으로 흔들립니다";
                case AnomalyKind.Flip: return "화면이 회전·반전됩니다";
                default: return string.Empty;
            }
        }

        /// <summary>Index into a per-kind array, guarded so an out-of-range enum cannot throw.</summary>
        public static int Index(AnomalyKind kind)
        {
            int i = (int)kind;
            return i < 0 || i >= Count ? 0 : i;
        }
    }
}
